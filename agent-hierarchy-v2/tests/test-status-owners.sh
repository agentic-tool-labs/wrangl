#!/bin/bash
# agent-hierarchy — the `owners` key of the status document (`ah view status`) and the session records behind it.
# Each live team belongs to one orchestrator process; `owners` groups the teams by that process and lists the
# session ids recorded for it (session-pids.jsonl, written by SessionStart and by the team-writing CLI verbs).
# HOME- and WRANGL_HIERARCHY_DIR-redirected; real state untouched.
# Usage: bash tests/test-status-owners.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID CLAUDE_CODE_SESSION_ID  # every Claude session exports these; a test must not inherit them

PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
. "$PLUGIN/tests/lib-status-pool.sh"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-status-owners-test.XXXXXX")"
[ -n "$SANDBOX" ] && [ -d "$SANDBOX" ] || { echo "mktemp failed"; exit 1; }
sleep 300 & OTHER=$!
hermetic_on_exit 'ah_signal_child TERM "$OTHER"; rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
PASS=0; FAIL=0
STATUS_POOL_IN=1  # these sessions are Orchestrators: a pool's home says so, or their hooks would stay silent
POOLS="$AH_HOME/pools"  # the state hub trusts a claimed hierarchy dir only inside the ahprobe sandbox AH_HOME lives in

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name"; fi
}

REF="2026-01-01T12:00:00.000Z"
ARCH='{"role":"architect","name":"demo-architect","route":"pane","kind":"codex","transport_id":"w1:p1"}'
REV='{"role":"reviewer","name":"demo-reviewer","route":"pane","kind":"codex","transport_id":"w1:p2"}'

# <team name or -> <pid> <session id or null> <members json> [<pid start>]: a team owned by <pid>; a recorded start is what lets the
# owner's absence show it is gone.
owned() {
  local path="$HD/team.json"
  [ "$1" != - ] && { mkdir -p "$HD/teams"; path="$HD/teams/$1.json"; }
  node -e 'const [pid, sid, members, start] = process.argv.slice(1); const orchestrator = { session_id: sid === "null" ? null : sid, pid: Number(pid) }; if (start) orchestrator.pid_start = start; console.log(JSON.stringify({ team_id: "t-demo", created: new Date().toISOString(), orchestrator, members: JSON.parse(members) }))' "$2" "$3" "$4" "${5:-}" | ah_seed_team "$path"
}
GONE_START="2026-01-01T00:00:00.000Z"
# <session id> <pid>: a session record.
# through the state hub's session-pids append, which skips a row repeating the newest one
# <pool>/session-pids.jsonl: that pool's stored session pid rows (per-dir-files-to-db S1: the session_pids table), `session pid` per line
pids() { ah_session_pids "$(dirname "$1")"; }
rows_of() { pids "$1" | grep -c .; }
clear_pids() { for f in "$@"; do sqlite3 "$AH_HOME/state/ah.db" "DELETE FROM session_pids WHERE hier_dir IN ($(_ah_dirs "$(dirname "$f")"))" 2>/dev/null; done; }
row() { printf '{"args":{"dir":"%s","session_id":"%s","pid":%s},"context":{"cwd":"%s","hierarchy_dir":"%s"},"op_id":"row-%s-%s"}' "$HD" "$1" "$2" "$PROJ" "$HD" "$RANDOM$RANDOM" "$$" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" "$PLUGIN/bin/ahd" state session-pids append >/dev/null 2>&1; }

# ---- producer
pool "$POOLS/two"
owned alpha $$ null "[$ARCH]"; owned beta "$OTHER" null "[$REV]"
row sess-a $$; row sess-b "$OTHER"
activity pane-demo-architect working "$(at -60000)"
request 20260101-115800-w001 architect build-step "$(at -120000)" small demo-architect alpha
dispatch 20260101-115800-w001 architect "$(at -60000)"; stub 20260101-115800-w001
status "$REF"
check "two owners: one entry each, with its own sessions and teams" '[ "$(jq_doc "o.owners.map(x => x.sessions.join() + \"/\" + x.teams.join()).sort().join(\" \")")" = "sess-a/alpha sess-b/beta" ]'
check "per-owner timeline counts only that owner's own teams (1 live 1 out vs 1 live 0 out)" '[ "$(jq_doc "o.owners.map(x => x.teams[0] + \":\" + x.timeline[0].live + \"/\" + x.timeline[0].out).sort().join(\" \")")" = "alpha:1/1 beta:1/0" ]'
check "the top-level timeline stays pool-wide (2 live, 1 out) and the other top-level keys are unchanged" '[ "$(jq_doc "o.timeline[0].live + \"/\" + o.timeline[0].out + \" \" + Object.keys(o).join()")" = "2/1 schema,written_at,expires_at,enabled,member_sessions,teams,timeline,owners" ]'
check "every team object is in exactly one owner entry" '[ "$(jq_doc "o.teams.length + \" \" + o.owners.reduce((n, x) => n + x.teams.length, 0)")" = "2 2" ]'

pool "$POOLS/clear"
owned - $$ null "[$ARCH]"
row sess-old $$; row sess-new $$
status "$REF"
check "two rows for one pid (a /clear): both session ids, newest first" '[ "$(jq_doc "o.owners[0].sessions.join()")" = "sess-new,sess-old" ]'

pool "$POOLS/same"
owned - $$ null "[$ARCH]"; owned named $$ null "[$REV]"
status "$REF"
check "default team and a named team with one pid: one entry, teams [null, name]" '[ "$(jq_doc "o.owners.length + JSON.stringify(o.owners[0].teams)")" = "1[null,\"named\"]" ]'
check "no rows and no orchestrator.session_id: sessions is []" '[ "$(jq_doc "JSON.stringify(o.owners[0].sessions)")" = "[]" ]'

pool "$POOLS/teamsid"
owned - $$ sess-in-team "[$ARCH]"
status "$REF"
check "orchestrator.session_id on the team file appears in sessions" '[ "$(jq_doc "o.owners[0].sessions.join()")" = "sess-in-team" ]'

pool "$POOLS/orphan"
owned - $$ null "[$ARCH]"; owned dead 2147483646 null "[$REV]" "$GONE_START"
row sess-dead 2147483646
status "$REF"
check "a team with a dead orchestrator pid is in no owner entry, and its rows are ignored" '[ "$(jq_doc "o.owners.length + \" \" + JSON.stringify(o.owners[0].teams) + \" \" + o.owners[0].sessions.length")" = "1 [null] 0" ]'

pool "$POOLS/cap"
owned - $$ null "[$ARCH]"
for i in $(seq 1 40); do row "sess-$i" $$; done
status "$REF"
check "sessions is capped at 32, newest first" '[ "$(jq_doc "o.owners[0].sessions.length + o.owners[0].sessions[0]")" = "32sess-40" ]'

pool "$POOLS/repeat"
owned - $$ null "[$ARCH]"
row sess-r $$; row sess-r $$
check "a row repeating the latest one is not appended" '[ "$(rows_of "$HD/session-pids.jsonl")" = 1 ]'

# ---- SessionStart
# Hooks run through phook (tests/lib-status-pool.sh): its driver is the hook's parent, and DRV_TEAM makes it the owner of the default
# team first, so the status document the hook leaves has a team to list the session under.
hook() { # <payload json> -> driver pid
  phook HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" HOOK=sessionstart DRV_TEAM="$HD/team.json" -- "$1"
}

pool "$POOLS/ss"
P=$(hook "{\"session_id\":\"sess-top\",\"source\":\"startup\",\"cwd\":\"$PROJ\"}")
check "SessionStart in a top-level session writes {session_id, pid = hook ppid} into the pool" '[ "$(rows_of "$HD/session-pids.jsonl") $(pids "$HD/session-pids.jsonl" | head -1)" = "1 sess-top $P" ]'
check "the status.json SessionStart leaves already lists the session under its team" '[ "$(node -e "const o = JSON.parse(require(\"fs\").readFileSync(process.argv[1], \"utf8\")); process.stdout.write(o.owners.map((x) => x.sessions.join()).join())" "$(vdoc)")" = sess-top ]'

pool "$POOLS/ss-sub"
hook "{\"session_id\":\"sess-sub\",\"source\":\"startup\",\"cwd\":\"$PROJ\",\"agent_id\":\"a1\",\"agent_type\":\"reviewer\"}" >/dev/null
check "SessionStart in a subagent writes no row" '[ "$(rows_of "$HD/session-pids.jsonl")" = 0 ]'

pool "$POOLS/ss-nodir"
rm -rf "$HD"
hook "{\"session_id\":\"sess-nd\",\"source\":\"startup\",\"cwd\":\"$PROJ\"}" >/dev/null
check "SessionStart with no hierarchy dir writes nothing and creates no dir" '[ "$(rows_of "$HD/session-pids.jsonl")" = 0 ]'

# ---- CLI
pool "$POOLS/cli"
CLI_SESSION=$(ah_test_prove_self orchestrator "$(basename "$PROJ")")  # this shell, proven; its session is the one adopt binds
cli() { HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" CLAUDE_CODE_SESSION_ID="$CLI_SESSION" "$@"; }
owned - 2147483646 null "[$ARCH]" "$GONE_START"
cli env CLAUDE_PID=$$ "$PLUGIN/bin/ah" roster adopt --orchestrator-pid $$ --cwd "$PROJ" >/dev/null 2>&1
check "adopt from the proven shell, naming its own pid: a row is written for its session" '[ "$(pids "$HD/session-pids.jsonl" | grep -c "^$CLI_SESSION ")" = 1 ]'
clear_pids "$HD/session-pids.jsonl"
owned - 2147483646 null "[$ARCH]"
cli env CLAUDE_PID=$$ "$PLUGIN/bin/ah" roster adopt --orchestrator-pid "$OTHER" --cwd "$PROJ" >/dev/null 2>&1
check "adopt naming another pid binds nothing: no row" '[ "$(rows_of "$HD/session-pids.jsonl")" = 0 ]'
ah_forget_sessions "$CLI_SESSION"  # the sections below run hooks whose sessions must not be this shell's

# ---- prompt-time registration (UserPromptSubmit, the activity hook)
owners_of() { node -e 'const o = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); process.stdout.write(o.owners.map((x) => x.sessions.join()).join(";") + "|" + o.member_sessions.join())' "$1"; }

pool "$POOLS/pr"
P=$(phook HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" HOOK=activity DRV_TEAM="$HD/team.json" -- "{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"sess-prompt\",\"cwd\":\"$PROJ\"}")
check "a prompt from a session with no row registers it: its pid row exists and the same run's status.json lists it as owner" '[ "$(owners_of "$(vdoc)")" = "sess-prompt|" ] && [ "$(pids "$HD/session-pids.jsonl" | grep -c " $P$")" = 1 ]'

pool "$POOLS/pr-grow"
# one driver process per session, as two Claude processes are: since ah2-32 the first prompt registers its session with its process,
# and a payload naming another session from that same process is refused
GROW_PIDS=""
for s in a b; do
  phook HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" HOOK=activity DRV_TEAM="$HD/team.json" -- $(for i in 1 2 3 4 5; do echo "{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"sess-$s\",\"cwd\":\"$PROJ\"}"; done | tr '\n' ' ') >/dev/null &
  GROW_PIDS="$GROW_PIDS $!"
done
wait $GROW_PIDS
check "two sessions sending five prompts each at the same time leave exactly two rows" '[ "$(rows_of "$HD/session-pids.jsonl")" = 2 ]'

pool "$POOLS/pr-sub"
phook HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" HOOK=activity -- "{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"sess-sub\",\"cwd\":\"$PROJ\",\"agent_id\":\"a1\",\"agent_type\":\"reviewer\"}" >/dev/null
check "a subagent prompt writes no row" '[ "$(rows_of "$HD/session-pids.jsonl")" = 0 ]'

pool "$POOLS/pr-nodir"
rm -rf "$HD"
phook HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" HOOK=activity -- "{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"sess-nd\",\"cwd\":\"$PROJ\"}" >/dev/null
check "a prompt with no hierarchy dir creates nothing" '[ ! -e "$HD" ]'

pool "$POOLS/pr-pid"
row sess-p 111; row sess-q 222; row sess-p 333; row sess-p 333; row sess-q 222
check "a new pid for a known session appends once; repeats of any session's newest pid do not" '[ "$(rows_of "$HD/session-pids.jsonl")" = 3 ]'

pool "$POOLS/pr-member"
REF="2026-01-01T12:00:00.000Z"
MEM='{"role":"reviewer","name":"demo-reviewer","route":"peer","kind":"claude","transport_id":"w1:p2"}'
owned - $$ null "[$MEM]"; row sess-boss $$
up demo-reviewer reviewer sess-member w1:p2
phook HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" HOOK=activity -- "{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"sess-member\",\"cwd\":\"$PROJ\"}" >/dev/null
check "the member's own prompt wrote a row" 'pids "$HD/session-pids.jsonl" | grep -q "^sess-member "'
check "...but it is in no owner's sessions and stays in member_sessions" '[ "$(owners_of "$(vdoc)")" = "sess-boss|sess-member" ]'

# A real linked worktree: the main checkout holds the team, the session prompts from the worktree.
WT_MAIN="$SANDBOX/wtmain"; WT="$SANDBOX/wt"
mkdir -p "$WT_MAIN"
git -C "$WT_MAIN" init -q && git -C "$WT_MAIN" -c user.email=a@b -c user.name=n commit -q --allow-empty -m i && git -C "$WT_MAIN" worktree add -q "$WT" -b wtb
mkdir -p "$WT_MAIN/.claude/ah2" "$WT/.claude/ah2" "$SANDBOX/wthome/.claude/ah2"
printf '{"alwaysOrchestrator":true}\n' > "$SANDBOX/wthome/.claude/ah2/agent-hierarchy.json"
WT_MAIN="$(cd "$WT_MAIN" && pwd -P)"; WT="$(cd "$WT" && pwd -P)"
phook HOME="$SANDBOX/wthome" HOOK=activity DRV_TEAM="$WT_MAIN/.claude/ah2/team.json" -- "{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"sess-wt\",\"cwd\":\"$WT\"}" >/dev/null
check "a prompt from a linked worktree writes a row in both pools" '[ "$(rows_of "$WT/.claude/ah2/session-pids.jsonl")" = 1 ] && [ "$(rows_of "$WT_MAIN/.claude/ah2/session-pids.jsonl")" = 1 ]'
check "...and the main checkout's status.json lists the session as owner" '[ "$(owners_of "$(vdoc "$WT_MAIN/.claude/ah2")")" = "sess-wt|" ]'
check "the worktree's own status.json lists the team homed in main, with the viewer as owner" '[ "$(owners_of "$(vdoc "$WT/.claude/ah2")")" = "sess-wt|" ]'

# A session working in a worktree sees its own pool's exchange: the worktree document counts the dispatch, main's does not.
WTH="$WT/.claude/ah2"; MAINH="$WT_MAIN/.claude/ah2"
rm -rf "$MAINH" "$WTH"; mkdir -p "$MAINH/msgs" "$MAINH/activity" "$WTH/msgs" "$WTH/activity"
REF="$(node -e 'process.stdout.write(new Date(Date.now() - 1000).toISOString())')"
FAKEHOME="$SANDBOX/wthome"; PENDING="$FAKEHOME/.claude/ah2/agent-hierarchy.peer-pending.jsonl"; PROJ="$WT"
HD="$MAINH"; owned - $$ null "[$ARCH]"; row sess-ex $$
HD="$WTH"; row sess-ex $$
request 20260101-115800-w001 architect build-step "$(at -120000)" small demo-architect
dispatch 20260101-115800-w001 architect "$(at -60000)" sess-ex; stub 20260101-115800-w001
# the status document of the checkout <cwd> belongs to: the worktree's pool for the worktree, main's for main
wstatus() { HOME="$SANDBOX/wthome" "$PLUGIN/bin/ah" roster status --cwd "$1" >/dev/null 2>&1; }
unset WRANGL_HIERARCHY_DIR
wstatus "$WT"; wstatus "$WT_MAIN"
outs() { node -e 'const o = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); process.stdout.write(o.owners.map((x) => x.timeline[0].out).join())' "$1"; }
check "the worktree document counts the exchange in the worktree's pool; main's does not" '[ "$(outs "$(vdoc "$WTH")")" = 1 ] && [ "$(outs "$(vdoc "$MAINH")")" = 0 ]'
clear_pids "$WT_MAIN/.claude/ah2/session-pids.jsonl" "$WT/.claude/ah2/session-pids.jsonl"
phook HOME="$SANDBOX/wthome" HOOK=sessionstart DRV_TEAM="$WT_MAIN/.claude/ah2/team.json" -- "{\"session_id\":\"sess-wt2\",\"source\":\"startup\",\"cwd\":\"$WT\"}" >/dev/null
check "SessionStart in a linked worktree writes a row in both pools, and the main status.json lists the session" '[ "$(rows_of "$WT/.claude/ah2/session-pids.jsonl")" = 1 ] && [ "$(rows_of "$WT_MAIN/.claude/ah2/session-pids.jsonl")" = 1 ] && [ "$(owners_of "$(vdoc "$WT_MAIN/.claude/ah2")")" = "sess-wt2|" ]'

clear_pids "$WT_MAIN/.claude/ah2/session-pids.jsonl"
pool "$POOLS/pr-env"
phook HOME="$SANDBOX/wthome" WRANGL_HIERARCHY_DIR="$HD" HOOK=activity -- "{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"sess-env\",\"cwd\":\"$WT\"}" >/dev/null
check "with WRANGL_HIERARCHY_DIR set, a worktree prompt writes the pool only" '[ "$(rows_of "$HD/session-pids.jsonl")" = 1 ] && [ "$(rows_of "$WT_MAIN/.claude/ah2/session-pids.jsonl")" = 0 ]'

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
