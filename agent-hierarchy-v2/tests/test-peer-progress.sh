#!/bin/bash
# agent-hierarchy — a peer's passive progress (the last finished tool in its activity record and the status
# file: tool name only, throttled) and the push-note rules around it: the notice and orchestrator text, a note
# counted as heard, and the report-back gate treating a note and a BLOCKED report differently.
# HOME- and WRANGL_HIERARCHY_DIR-redirected; real state untouched.
# Usage: bash tests/test-peer-progress.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it

PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
. "$PLUGIN/tests/lib-seed.sh"
. "$PLUGIN/tests/lib-status-pool.sh"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-peer-progress-test.XXXXXX")"
[ -n "$SANDBOX" ] && [ -d "$SANDBOX" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (OUT=${OUT:0:300})"; fi
}

pool "$SANDBOX/p"
ah_test_always_on "$FAKEHOME"
SID="sess-role-0076"
MEMBER='{"role":"implementor","name":"demo-impl","route":"peer","kind":"claude","transport_id":"w1:p1"}'
HOME="$FAKEHOME" seed_session_role "$SID" implementor
team - "[$MEMBER]"
up demo-impl implementor "$SID" w1:p1

hook() { OUT=$(node -e 'const [e, s, cwd, x] = process.argv.slice(1); process.stdout.write(JSON.stringify({ hook_event_name: e, session_id: s, cwd, ...JSON.parse(x || "{}") }))' "$1" "$2" "$PROJ" "${3:-}" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" activity 2>&1); RC=$?; }
tool() { hook PostToolUse "$SID" "$(node -e 'process.stdout.write(JSON.stringify({ tool_name: process.argv[1], tool_input: JSON.parse(process.argv[2] || "{}") }))' "$1" "${2:-}")"; }
# the activity record is a database row: ah_activity reads it, ah_seed_activity writes it (no activity/<session>.json file)
rec() { node -e 'try { const a = JSON.parse(process.argv[1]); console.log(eval(process.argv[2])); } catch { console.log("none"); }' "$(ah_activity "$HD" "$SID")" "$1"; }
stat() { node -e 'try { const o = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); const m = o.teams[0].members[0]; console.log(eval(process.argv[2])); } catch { console.log("none"); }' "$(vdoc)" "$1"; }
backdate_tool() { ah_seed_activity "$HD" "$SID" "$(node -e 'const a = JSON.parse(process.argv[1]); a.tool_at = new Date(Date.now() - 16000).toISOString(); process.stdout.write(JSON.stringify(a))' "$(ah_activity "$HD" "$SID")")"; }

# the status document is served from the database (files-off-disk F3a); this first read stays as the run's warm-up
(cd "$SANDBOX" && HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" "$PLUGIN/bin/ah" roster status --cwd "$PROJ" >/dev/null 2>&1)

# ---- A1: the tool's input never reaches the record or the status file
hook UserPromptSubmit "$SID"
tool Bash '{"command":"cat /home/u/private/key.pem","note":"sk-live-SECRET-9f3a"}'
check "A1: the record holds the tool name" '[ "$(rec a.tool)" = Bash ] && [ "$(rec "typeof a.tool_at")" = string ]'
check "A1: the status file shows it as last_tool / last_tool_at" '[ "$(stat m.last_tool)" = Bash ] && [ "$(stat "m.last_tool_at")" = "$(rec a.tool_at)" ]'
check "A1: neither file holds any of the input text" '[ -n "$(ah_activity "$HD" "$SID")" ] && ! { ah_activity "$HD" "$SID"; cat "$(vdoc)"; } | grep -q "key.pem\|SECRET\|/home/u"'

# ---- A2: ten events inside the interval write once
T0=$(rec a.tool_at); W0=$(stat "0"); sleep 1.1
for n in 1 2 3 4 5 6 7 8 9 10; do tool "Tool$n"; done
check "A2: ten PostToolUse events within 15 s leave the record as the first wrote it" '[ "$(rec a.tool_at)" = "$T0" ] && [ "$(rec a.tool)" = Bash ]'

# ---- A7: the throttle drops the trailing edge, and the record says so
check "A7: after tool B was throttled the record and status still show tool A with A's time" '[ "$(rec a.tool)" = Bash ] && [ "$(stat m.last_tool)" = Bash ] && [ "$(stat m.last_tool_at)" = "$T0" ]'

# ---- A3: past the interval one event writes
backdate_tool
tool Edit
check "A3: a PostToolUse 15 s after the last tool_at writes the new tool" '[ "$(rec a.tool)" = Edit ] && [ "$(rec a.tool_at)" != "$T0" ] && [ "$(stat m.last_tool)" = Edit ]'

# ---- A8: a tool-only write leaves the state's start alone; a state change moves it
AT_BEFORE=$(rec a.at); backdate_tool
sleep 1.1; tool Grep
check "A8: a tool-only write (activity unchanged) writes the new tool and keeps the record's at" '[ "$(rec a.tool)" = Grep ] && [ "$(rec a.at)" = "$AT_BEFORE" ] && [ "$(stat m.activity_at)" = "$AT_BEFORE" ]'
check "A8: tool_at itself moved on that write" '[ "$(rec a.tool_at)" != "$AT_BEFORE" ]'

# ---- A4: Stop keeps the tool
TA=$(rec a.tool_at)
hook Stop "$SID"
check "A4: Stop records idle and preserves tool and tool_at" '[ "$(rec a.activity)" = idle ] && [ "$(rec a.tool)" = Grep ] && [ "$(rec a.tool_at)" = "$TA" ]'
sleep 1.1
hook UserPromptSubmit "$SID"
check "A8: a state change (a new turn) does move at, and so activity_at" '[ "$(rec a.at)" != "$AT_BEFORE" ] && [ "$(stat m.activity_at)" = "$(rec a.at)" ]'
check "A4: a new turn's UserPromptSubmit keeps them too, with activity working" '[ "$(rec a.activity)" = working ] && [ "$(rec a.tool)" = Grep ] && [ "$(rec a.tool_at)" = "$TA" ]'

# ---- A5: a subagent writes nothing
BEFORE=$(ah_activity "$HD" "$SID")
hook PostToolUse "$SID" '{"tool_name":"SubTool","agent_id":"a1","agent_type":"task-gopher:task-gopher"}'
check "A5: a subagent's PostToolUse changes nothing" '[ "$(ah_activity "$HD" "$SID")" = "$BEFORE" ]'

# ---- A6: control characters and length
backdate_tool
LONG=$(node -e 'process.stdout.write("T" + "x".repeat(120))')
hook PostToolUse "$SID" "$(node -e 'process.stdout.write(JSON.stringify({ tool_name: "A\u001b[31mB\u0007C" + process.argv[1] }))' "$LONG")"
check "A6: the record's tool has no control characters and is at most 64 long" '[ "$(rec "/[\\u0000-\\u001f\\u007f-\\u009f]/.test(a.tool)")" = false ] && [ "$(rec a.tool.length)" -le 64 ] && [ "$(rec a.tool.slice\(0,3\))" = ABC ]'
check "A6: so does the status file's last_tool" '[ "$(stat m.last_tool.length)" -le 64 ] && [ "$(stat m.last_tool.slice\(0,3\))" = ABC ]'
backdate_tool
hook PostToolUse "$SID" '{"tool_name":"","tool_input":{}}'
check "A6: an empty tool name writes no tool, and keeps the previous one" '[ "$(rec "a.tool.slice(0,3)")" = ABC ]'

# ---- P1, P2: the prose
ORCH="$PLUGIN/agents/orchestrator.md"
check "P2: orchestrator.md no longer carries the notes rule" '! grep -q "never closes" "$ORCH"'
for f in implementor reviewer architect; do
  git -C "$PLUGIN" diff --quiet HEAD -- "agents/$f.md" 2>/dev/null; check "no text went into agents/$f.md" '[ $? -eq 0 ]'
done

# ---- P5, P6, P8: the notes and the gates
PENDING="$FAKEHOME/.claude/ah2/agent-hierarchy.peer-pending.jsonl"
HIER_DIR="$HD"
now_iso() { date -u +%Y-%m-%dT%H:%M:%SZ; }
iso_ago() { node -e 'console.log(new Date(Date.now()-Number(process.argv[1])*1000).toISOString())' "$1"; }
wrapped() { printf '<cross-session-message from="uds:/tmp/cc-socks/1.sock" from-name="%s" from-mode="prompting">\n%s\n</cross-session-message>' "${2:-at-orchestrator}" "$1"; }
ups() {  # <session> <prompt>
  node -e 'console.log(JSON.stringify({session_id:process.argv[1],cwd:process.argv[2],prompt:process.argv[3],hook_event_name:"UserPromptSubmit"}))' "$1" "$PROJ" "$2" > "$SANDBOX/ups.json"
  OUT=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" userpromptsubmit-peer-tracking < "$SANDBOX/ups.json" 2>&1); RC=$?
}
send() {  # <session> <to> <message>: the peer's PostToolUse for a SendMessage
  OUT=$(node -e 'console.log(JSON.stringify({session_id:process.argv[1],cwd:process.argv[2],hook_event_name:"PostToolUse",tool_name:"SendMessage",tool_input:{to:process.argv[3],message:process.argv[4]}}))' "$1" "$PROJ" "$2" "$3" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" posttooluse-peer-resolve 2>&1); RC=$?
}
peer_stop() { OUT=$(printf '{"session_id":"%s","cwd":"%s","hook_event_name":"Stop"}' "$1" "$PROJ" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" stop-peer-nudge 2>&1); RC=$?; }
is_block() { case "$OUT" in *'"decision":"block"'*) true;; *) false;; esac; }
rows() { ah_peer_rows | grep -c "$1"; }
session_start() { phook HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" HOOK=sessionstart -- "$(printf '{"session_id":"%s","cwd":"%s","agent_type":"%s","hook_event_name":"SessionStart","source":"startup"}' "$1" "$PROJ" "$2")" >/dev/null 2>&1; }

# P5: a note from the peer, delivered to the Orchestrator's session, is a heard record
ID5=20260101-000000-p5aa
request "$ID5" implementor p5 "$(iso_ago 360)" small demo-impl
ah_seed_peer "$(printf '{"type":"dispatch","session_id":"s-orch5","request_id":"%s","to":"implementor","path":"%s","to_addr":"peer-heard","created":"%s"}' "$ID5" "$HD/msgs/$ID5--implementor--p5--request.md" "$(iso_ago 0)")"
ups s-orch5 "$(wrapped "note $ID5: halfway, 3 of 6 done" peer-heard)"
check "P5: a peer note delivered to the Orchestrator writes a heard row" '[ "$(rows "\"type\":\"heard\"")" -ge 1 ]'
ups s-orch5b "$(wrapped "note $ID5: halfway" someone-else)"
check "P5: a note from a name that is no dispatch's peer writes none more" '[ "$(rows "\"type\":\"heard\"")" -eq 1 ]'

# P6: a note is not a report
ID6=20260101-000000-p6aa
request "$ID6" implementor p6 "2020-01-01T00:00:00-00:00" large demo-impl
REQ6="$HD/msgs/$ID6--implementor--p6--request.md"
session_start s-peer6 wrangl:implementor
ups s-peer6 "$(wrapped "Implement it. [hierarchy-msg $REQ6]")"
peer_stop s-peer6
check "P6 setup: the peer owes a report, so its Stop blocks" 'is_block'
send s-peer6 at-orchestrator "note $ID6: halfway, 3 of 6 done"
peer_stop s-peer6
check "P6: after sending only a note the Stop gate still holds" 'is_block'

# P10: a token-free note is no dispatch; the old format was one
send s-peer6 at-orchestrator "note $ID6: halfway, 3 of 6 done"
check "P10: the note is run through the resolver: no output" '[ -z "$OUT" ]'
check "P10: and no dispatch row exists for the peer's session" '[ "$(ah_peer_rows | grep "\"type\":\"dispatch\"" | grep -c "\"session_id\":\"s-peer6\"")" -eq 0 ]'
send s-peer6 at-orchestrator "[hierarchy-msg $REQ6] note: halfway"
check "P10 contrast: the tokened form is taken for a dispatch (a row is written)" '[ "$(ah_peer_rows | grep "\"type\":\"dispatch\"" | grep -c "\"session_id\":\"s-peer6\"")" -ge 1 ]'

# P11: a note is not held by the report check while a report is owed; the tokened form is
resp_gate() { # <message>: the PreToolUse hook for the implementor peer's SendMessage
  OUT=$(node -e 'console.log(JSON.stringify({session_id:"s-peer6",cwd:process.argv[1],hook_event_name:"PreToolUse",agent_type:"implementor",tool_name:"SendMessage",tool_input:{to:"at-orchestrator",message:process.argv[2]}}))' "$PROJ" "$1" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" pretooluse-sendmessage-response 2>&1); RC=$?
}
resp_gate "note $ID6: halfway, 3 of 6 done"
check "P11: with a report owed, a token-free note is allowed (no deny, no hold)" '[ -z "$OUT" ]'

# P8: a BLOCKED report is a report
TEAM6=$(basename "$PROJ")
with_proof "$TEAM6" "$HD/team.json" implementor orchestrator -- "$PLUGIN/bin/ah" msg new --type response --id "$ID6" --to orchestrator --from implementor --cwd "$PROJ" >/dev/null 2>&1
REQ6_BASE=${REQ6##*/}; REQ6_SLUG=${REQ6_BASE#*--*--}
RESP6="$HD/msgs/$ID6--orchestrator--${REQ6_SLUG%--request.md}--response.md"
with_proof "$TEAM6" "$HD/team.json" implementor orchestrator -- "$PLUGIN/bin/ah" msg fill --id "$ID6" --from implementor --section 1 --body '"- BLOCKED: need the exact question answered\n"' --cwd "$PROJ" >/dev/null 2>&1
send s-peer6 at-orchestrator "BLOCKED. [hierarchy-msg $RESP6]"
peer_stop s-peer6
check "P8: after a BLOCKED response file and its message the Stop gate lets the peer stop" '[ -n "$RESP6" ] && [ -z "$OUT" ]'


# P9: what the new-request rule rests on
msgcli() { local o rc; o=$(cd "$SANDBOX" && HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" with_proof "$TEAM6" "$HD/team.json" implementor orchestrator -- "$PLUGIN/bin/ah" msg "$@" --cwd "$PROJ" 2>&1); rc=$?; ah_drop_checklist_hint <<< "$o"; return $rc; }
P9A_BEFORE=$(ah_msg_text "$RESP6" --cwd "$PROJ")
OUT=$(msgcli new --type response --id "$ID6" --to orchestrator --from implementor); RC=$?
check "P9a: a second response for the blocked id is refused: non-zero exit, no path on stdout, 'already exists' named" '[ "$RC" -ne 0 ] && ! printf "%s" "$OUT" | grep -q "\"path\"" && printf "%s" "$OUT" | grep -q "already exists"'
check "P9a: the blocked response is still the only one and is byte-identical" '[ "$(ah_msg_text "$RESP6" --cwd "$PROJ")" = "$P9A_BEFORE" ]'
OUT=$(msgcli new --type request --to implementor --from orchestrator --slug p9-next --title "Fix test titles" --parent "$ID6")
NEWID=$(printf "%s" "$OUT" | node -e 'try { console.log(JSON.parse(require("fs").readFileSync(0, "utf8")).id) } catch { console.log("") }')
check "P9b: a request with --parent makes a new id whose frontmatter parent is the blocked id" '[ -n "$NEWID" ] && [ "$NEWID" != "$ID6" ] && [ -n "$NEWID" ] && "$PLUGIN/bin/ah" msg show "$NEWID" request --cwd "$PROJ" | grep -q "^parent: $ID6$"'
OUT=$(msgcli new --type response --id "$NEWID" --to orchestrator --from implementor)
check "P9c: a response to the new id is accepted" 'printf "%s" "$OUT" | grep -q "\"id\":\"$NEWID\""'

echo "----"
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
