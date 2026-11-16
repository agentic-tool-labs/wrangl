#!/bin/bash
# agent-hierarchy — a Claude role session's own activity record (the `activity` hook), that it writes no
# status file, and how records end: SessionEnd removes the session's own, and `ah msg sweep` drops
# any not written within its cutoff.
# HOME- and WRANGL_HIERARCHY_DIR-redirected; real state untouched.
# Usage: bash tests/test-status-activity.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
export AH_MEMBER_NAME=suite-member  # these sessions are team members; the hooks of one nothing identifies stay silent
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it

PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
. "$PLUGIN/tests/lib-status-pool.sh"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-status-activity-test.XXXXXX")"
[ -n "$SANDBOX" ] && [ -d "$SANDBOX" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (OUT=${OUT:0:300})"; fi
}

pool "$SANDBOX/p"
ROLE_SID="sess-role-0001"
OTHER_SID="sess-plain-0001"
ah_seed_home_doc home.session-roles "{\"version\":1,\"sessions\":{\"$ROLE_SID\":{\"role\":\"architect\",\"at\":\"$(date -u +%Y-%m-%dT%H:%M:%S.000Z)\"}}}"

# <event> <session id> [extra json fields]: the activity hook on that event.
hook() { OUT=$(node -e 'const [e, s, cwd, x] = process.argv.slice(1); process.stdout.write(JSON.stringify({ hook_event_name: e, session_id: s, cwd, ...JSON.parse(x || "{}") }))' "$1" "$2" "$PROJ" "${3:-}" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" activity 2>&1); RC=$?; }
# the records are rows of the activity table (per-dir-files-to-db S2)
rec() { ah_activity "$HD" "$1" | node -e 'try { const a = JSON.parse(require("fs").readFileSync(0, "utf8")); console.log(eval(process.argv[1])); } catch { console.log("none"); }' "$2"; }
mtime_of() { ah_activity_at "$HD" "$1"; }
has_rec() { [ -n "$(ah_activity "$HD" "$1")" ]; }
drop_rec() { sqlite3 "$AH_HOME/state/ah.db" "DELETE FROM activity WHERE hier_dir IN ($(_ah_dirs "$HD")) AND session_id = '$1'"; }
# files-off-disk F3a: the status document is served from the database; no hook writes a status.json (AC5 item 5 retired)
no_status_file() { [ ! -e "$HD/status.json" ] && [ ! -e "$HD/.state-status" ]; }

# ---------------------------------------------------------------- AC 8: a role session's events
hook UserPromptSubmit "$ROLE_SID"
check "UserPromptSubmit records working, silently, exit 0" '[ "$RC" = 0 ] && [ -z "$OUT" ] && [ "$(rec "$ROLE_SID" a.activity)" = working ]'
check "F3a: that activity change writes no status.json and no .state-status/" 'no_status_file'
hook Stop "$ROLE_SID"
check "Stop records idle" '[ "$(rec "$ROLE_SID" a.activity)" = idle ]'
hook Notification "$ROLE_SID" '{"message":"Claude needs your permission","notification_type":"permission_prompt"}'
check "a permission prompt records blocked, blocked_by permission, note null though the input has a message" '[ "$(rec "$ROLE_SID" "a.activity + \" \" + a.blocked_by + \" \" + a.note")" = "blocked permission null" ]'
hook PostToolUse "$ROLE_SID" '{"tool_name":"Bash"}'
check "PostToolUse after blocked records working" '[ "$(rec "$ROLE_SID" a.activity)" = working ] && [ "$(rec "$ROLE_SID" a.blocked_by)" = null ]'
ah_activity_age "$HD" "$ROLE_SID" 3600
BEFORE=$(mtime_of "$ROLE_SID")
hook PostToolUse "$ROLE_SID" '{"tool_name":"Read"}'
check "PostToolUse while already working rewrites nothing: the mtime is unchanged" '[ "$(mtime_of "$ROLE_SID")" = "$BEFORE" ]'

# ---------------------------------------------------------------- AC 8: registration
reg() { node -e 'const h = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).hooks; const out = []; for (const [ev, arr] of Object.entries(h)) for (const e of arr) for (const k of e.hooks) if (/\/bin\/ah-hook"? activity$/.test(k.command)) out.push(ev + ":" + e.matcher + ":" + (k.async === true)); console.log(out.sort().join(" "))' "$PLUGIN/hooks/registry.json"; }
check "hooks.json: PostToolUse async; PreToolUse(AskUserQuestion), UserPromptSubmit, Stop, SessionStart(compact) and Notification(permission_prompt) sync" '[ "$(reg)" = "Notification:permission_prompt:false PostToolUse:*:true PreToolUse:AskUserQuestion:false SessionStart:compact:false Stop:*:false UserPromptSubmit:*:false" ]'
regr() { node -e 'const h = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).hooks; const out = []; for (const [ev, arr] of Object.entries(h)) for (const e of arr) for (const k of e.hooks) if (/\/bin\/ah-hook"? session-rules$/.test(k.command)) out.push(ev + ":" + e.matcher + ":" + (k.async === true)); console.log(out.sort().join(" "))' "$PLUGIN/hooks/registry.json"; }
check "hooks.json: session-rules is its own sync SessionStart entry on every source, compaction included" '[ "$(regr)" = "SessionStart:startup|resume|clear|compact|fork:false" ]'

# ---------------------------------------------------------------- AC 8: who records
hook UserPromptSubmit "$OTHER_SID"
check "a non-role session records nothing" '[ "$RC" = 0 ] && ! has_rec "$OTHER_SID"'
hook Stop "$OTHER_SID"
hook PostToolUse "$OTHER_SID" '{"tool_name":"Bash"}'
check "...nor do its Stop and PostToolUse, and no status.json is written" '! has_rec "$OTHER_SID" && no_status_file'
drop_rec "$ROLE_SID"
hook UserPromptSubmit "$ROLE_SID" '{"agent_id":"a1","agent_type":"task-gopher:task-gopher"}'
check "a subagent of a role session records nothing" '[ "$RC" = 0 ] && ! has_rec "$ROLE_SID"'

# ---------------------------------------------------------------- AC 8: SessionEnd
hook UserPromptSubmit "$ROLE_SID"
printf '{"session_id":"%s","cwd":"%s","hook_event_name":"SessionEnd"}' "$ROLE_SID" "$PROJ" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" sessionend-roster >/dev/null 2>&1; RC=$?
check "SessionEnd removes the session's own record" '[ "$RC" = 0 ] && ! has_rec "$ROLE_SID"'

# ---------------------------------------------------------------- the sweep
activity sess-old idle "2026-01-01T00:00:00.000Z"
activity sess-new idle "2026-01-01T00:00:00.000Z"
ah_activity_age "$HD" sess-old $((8 * 86400))
OUT=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" "$PLUGIN/bin/ah" msg sweep --cwd "$PROJ" 2>&1); RC=$?
check "ah msg sweep deletes an activity record older than its cutoff and keeps a fresh one" '[ "$RC" = 0 ] && ! has_rec sess-old && has_rec sess-new'
check "...its output is unchanged" '[ "$OUT" = "{\"archived\":0}" ]'

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
