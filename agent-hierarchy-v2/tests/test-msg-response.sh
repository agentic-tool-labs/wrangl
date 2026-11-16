#!/bin/bash
# agent-hierarchy — owed responses are nudged and recorded, never held: the SubagentStop nudge fires once per
# agent and records it in gates.jsonl, and a peer obligation that carries `msg` resolves only on a response pointer.
# HOME- and WRANGL_HIERARCHY_DIR-redirected; real state untouched.
# Usage: bash tests/test-msg-response.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE CLAUDE_PID
FIXTURE="$PLUGIN/tests/fixtures/subagentstop-payload.json"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-msgresp-test.XXXXXX")"
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
FAKEHOME="$SANDBOX/home"
HD="$SANDBOX/hier"
PROJ="$SANDBOX/proj"
PROJDIR="$SANDBOX/projects/-proj"
STATE_FILE="$FAKEHOME/.claude/ah2/agent-hierarchy.peer-pending.jsonl"
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2" "$PROJDIR"
ah_test_always_on "$FAKEHOME"
printf '%s\n' "$HD" > "$AH_HOME/trusted-hierarchy-dirs"  # an WRANGL_HIERARCHY_DIR override must be listed here
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:400})"; fi
}
echo '{ "version": 1, "enabled": true, "roles": {} }' > "$PROJ/.claude/ah2/agent-hierarchy.json"

msg() { OUT=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" ah_test_run_as orchestrator mr "$HD/teams/mr.json" "$PLUGIN/bin/ah" msg --cwd "$PROJ" "$@" 2>&1); RC=$?; OUT=$(ah_drop_checklist_hint <<< "$OUT"); }
field() { node -e 'process.stdout.write(JSON.parse(process.argv[1])[process.argv[2]])' "$OUT" "$1"; }
export AH_TEST_RUNS_HOOKS=1
ah_test_team "$HD" mr implementor reviewer

# <agent_id> <agent_type> <last_assistant_message> <agent_transcript_path>
nudge() {
  OUT=$(node -e '
    const fs=require("fs");const [f,id,type,last,tp,cwd]=process.argv.slice(1);
    const o=JSON.parse(fs.readFileSync(f,"utf8"));
    Object.assign(o,{agent_id:id,agent_type:type,cwd,session_id:"sess1",agent_transcript_path:tp,last_assistant_message:last});
    o.transcript_path=tp.replace(/\/[^/]+\/subagents\/.*$/,"")+"/sess1.jsonl";
    process.stdout.write(JSON.stringify(o));' "$FIXTURE" "$1" "$2" "$3" "$4" "$PROJ" \
    | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" subagentstop-msg-nudge 2>&1); RC=$?
}
transcript() { # <path> <brief> <final text>
  mkdir -p "$(dirname "$1")"
  node -e 'const [p,u,a]=process.argv.slice(1);require("fs").writeFileSync(p,[
    {type:"user",message:{role:"user",content:u}},{type:"assistant",message:{role:"assistant",content:[{type:"text",text:a}]}}
  ].map(l=>JSON.stringify(l)).join("\n")+"\n");' "$1" "$2" "$3"
}
nudged() { echo "$OUT" | grep -q '"additionalContext"' && ! echo "$OUT" | grep -q '"decision":"block"'; }
quiet() { [ $RC -eq 0 ] && [ -z "$OUT" ]; }

# ---- 1: a briefed role subagent that stops without its response pointer is nudged once, recorded, then let go
msg new --to implementor --from orchestrator --slug impl-x --title "Fix test titles"
ID=$(field id); REQ=$(field path)
T1="$PROJDIR/sess1/subagents/agent-a1.jsonl"
transcript "$T1" "[hierarchy-msg $REQ]" "Done, I changed three files."
nudge a1 wrangl:implementor "Done, I changed three files." "$T1"
check "no pointer -> one nudge naming the response command" 'nudged && echo "$OUT" | grep -q "msg new --type response --id $ID"'
check "the nudge is recorded in the gate log" 'ah_gate_rows | grep -q "\"type\":\"nudge\".*\"agent_id\":\"a1\""'
nudge a1 wrangl:implementor "Done, I changed three files." "$T1"
check "second stop of the same agent -> no second nudge" 'quiet'

# ---- 2: a pointer to a filled response file is not nudged (negative case)
msg new --type response --id "$ID"
RESP=$(field path)
OUT=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" ah_test_run_as implementor mr "$HD/teams/mr.json" "$PLUGIN/bin/ah" msg --cwd "$PROJ" fill --id "$ID" --from implementor --body '"- done: PASS\n"' 2>&1) || echo "fill failed: $OUT" >&2
nudge a2 wrangl:implementor "[hierarchy-msg $RESP]
- done: PASS" "$T1"
check "pointer to the filled response -> no nudge" 'quiet'

# ---- 3: peer side: an obligation with msg stays pending on a bare reply and resolves on the response pointer
ups() { OUT=$(node -e 'process.stdout.write(JSON.stringify({session_id:"peer1",prompt:process.argv[1],hook_event_name:"UserPromptSubmit"}))' "$1" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" userpromptsubmit-peer-tracking 2>&1); RC=$?; }
ptu() { OUT=$(node -e 'process.stdout.write(JSON.stringify({session_id:"peer1",tool_name:"SendMessage",tool_input:{to:"uds:/tmp/cc-socks/1.sock",message:process.argv[1]}}))' "$1" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" posttooluse-peer-resolve 2>&1); RC=$?; }
status() { ah_peer_rows | node -e 'const r=require("fs").readFileSync(0,"utf8").trim().split("\n").map(JSON.parse).filter(r=>r.session_id==="peer1"&&!r.type&&r.status);process.stdout.write(r.length?r[r.length-1].status:"none")'; }
ups "<cross-session-message from=\"uds:/tmp/cc-socks/1.sock\" from-name=\"orch\" from-mode=\"prompting\">
[hierarchy-peer-brief reply-to=\"sender\" task=\"impl-x\"]
[hierarchy-msg $REQ]
</cross-session-message>"
ptu "done, all good"
check "bare reply -> obligation stays pending" '[ "$(status)" = pending ]'
ptu "[hierarchy-msg $RESP]
- done: PASS"
check "response pointer -> obligation resolved" '[ "$(status)" = resolved ]'

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
