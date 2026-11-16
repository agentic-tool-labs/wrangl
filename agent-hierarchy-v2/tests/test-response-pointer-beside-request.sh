#!/bin/bash
# agent-hierarchy — a response written beside its request (ah msg new --req) is a valid
# SendMessage pointer even when the responder's cwd pool is a different checkout's pool.
# Real main checkout + linked worktree; HOME-redirected; WRANGL_HIERARCHY_DIR unset.
# Usage: bash tests/test-response-pointer-beside-request.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE CLAUDE_PID WRANGL_HIERARCHY_DIR
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-besidereq-test.XXXXXX")"
[ -n "$SANDBOX" ] || exit 1
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
FAKEHOME="$SANDBOX/home"
MAIN="$SANDBOX/main"
WT="$SANDBOX/wt"
mkdir -p "$FAKEHOME/.claude/ah2" "$MAIN/.claude/ah2"
ah_test_always_on "$FAKEHOME"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:500})"; fi
}

git -C "$MAIN" init -q
git -C "$MAIN" -c user.email=t@t -c user.name=t commit -q --allow-empty -m init
git -C "$MAIN" worktree add -q "$WT" -b wt-branch
echo '{ "version": 1, "enabled": true, "roles": {} }' > "$MAIN/.claude/ah2/agent-hierarchy.json"

# this suite runs hooks, so its own shell stays unregistered: every msg call runs in a subshell proven as its --from
export AH_TEST_RUNS_HOOKS=1 AH_TEST_ORCH_SUBSHELL=1
HD="$SANDBOX/proof"
ah_test_team "$HD" bq architect

msg() { OUT=$(HOME="$FAKEHOME" ah_test_run_as_from bq "$HD/teams/bq.json" "$PLUGIN/bin/ah" msg "$@" 2>"$SANDBOX/err"); RC=$?; }
field() { node -e 'process.stdout.write(String(JSON.parse(process.argv[1])[process.argv[2]]))' "$OUT" "$1"; }

# The PreToolUse gates that fit SendMessage, in the order of the GateUnits rows the pretooluse-chain unit runs; first deny wins.
CHAIN=$(node -e 'const src=require("fs").readFileSync(process.argv[1],"utf8");const dispatch=src.match(/Dispatch = "([^"]+)"/)[1];const names=[];for(const l of src.split("\n")){const n=l.match(/new\("(pretooluse-[a-z-]+)"/);const m=l.match(/Matcher: (Dispatch|"[^"]*")(?: \+ "([^"]*)")?|, "([^"]*)"\),\s*$/);if(!n||!m)continue;const matcher=m[1]==="Dispatch"?dispatch+(m[2]||""):(m[1]||"").replace(/"/g,"")||m[3];if(matcher.split("|").includes("SendMessage")||matcher==="*")names.push(n[1])}process.stdout.write(names.join(" "))' "$PLUGIN/native/src/Ah.Hooks/Gates/GateUnits.cs")
send_chain() { # <session> <message>
  local p; p=$(node -e 'const[s,m]=process.argv.slice(1);process.stdout.write(JSON.stringify({session_id:s,cwd:process.env.MAIN,tool_name:"SendMessage",agent_type:"architect",tool_input:{to:"ct-orchestrator",message:m}}))' "$1" "$2")
  OUT=""; RC=0
  for h in $CHAIN; do
    OUT=$(printf '%s' "$p" | HOME="$FAKEHOME" MAIN="$MAIN" sh "$PLUGIN/bin/ah-hook" "$h" 2>&1); RC=$?
    echo "$OUT" | grep -q '"permissionDecision":"deny"' && return
  done
  OUT=""
}
denied() { echo "$OUT" | grep -q '"permissionDecision":"deny"'; }
allowed() { [ -z "$OUT" ]; }

arm() { # <session> <request path>
  node -e 'const[s,l]=process.argv.slice(1);process.stdout.write(JSON.stringify({session_id:s,cwd:process.env.MAIN,prompt:`<cross-session-message from="ct-orchestrator" from-name="ct-orchestrator">\n${l}\ntldr`}))' "$1" "[hierarchy-msg $2]" \
    | HOME="$FAKEHOME" MAIN="$MAIN" sh "$PLUGIN/bin/ah-hook" userpromptsubmit-peer-tracking >/dev/null 2>&1
}

new_pair() { # <slug> -> sets REQ RESP ID (request in worktree pool, response beside it)
  msg new --type request --to architect --from orchestrator --slug "$1" --title "Fix test titles" --cwd "$WT"; ID=$(field id); REQ=$(field path)
  msg new --type response --id "$ID" --to orchestrator --from architect --cwd "$WT"; RESP=$(field path)
  msg fill --id "$ID" --from architect --body '"- done: PASS\n"' --cwd "$WT"
}

# ---- T1: worktree request, main-checkout responder, team_file null
new_pair t1
check "T1 precondition: the response is filled" '[ $RC = 0 ]'
check "T1 precondition: request has team_file null" 'ah_msg_text "$REQ" --cwd "$MAIN" | grep -q "^team_file: null"'
arm s1 "$REQ"
# 1 while the session's latest obligation row in the peer store is pending, else 0 (one obligation per session here).
pending_n() {
  ah_peer_rows | node -e '
    const rows = require("fs").readFileSync(0, "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l));
    const mine = rows.filter((r) => r.session_id === process.argv[1] && !r.type);
    const last = mine[mine.length - 1];
    process.stdout.write(last && last.status === "pending" ? "1" : "0");' "$1"
}
check "T1 precondition: s1 owes one response" '[ "$(pending_n s1)" = 1 ]'
send_chain s1 "[hierarchy-msg $RESP]
- done: PASS"
check "T1a: whole SendMessage chain allows the beside-request response" 'allowed'
OUT=$(node -e 'process.stdout.write(JSON.stringify({session_id:"s1",cwd:process.env.MAIN,tool_name:"SendMessage",tool_input:{to:"ct-orchestrator",message:process.argv[1]}}))' "[hierarchy-msg $RESP]
- done: PASS" | HOME="$FAKEHOME" MAIN="$MAIN" sh "$PLUGIN/bin/ah-hook" posttooluse-peer-resolve 2>&1); RC=$?
check "T1b: peer-resolve recorded s1's obligation resolved" '[ "$(pending_n s1)" = 0 ]'
OUT=$(printf '{"session_id":"s1","cwd":"%s","stop_hook_active":false}' "$MAIN" | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" stop-peer-nudge 2>&1); RC=$?
check "T1c: stop-peer-nudge does not block after the send" '! echo "$OUT" | grep -q "\"decision\":\"block\""'

# ---- T1 control: the obligation really is owed before the send
new_pair t1b
arm s1b "$REQ"
OUT=$(printf '{"session_id":"s1b","cwd":"%s","stop_hook_active":false}' "$MAIN" | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" stop-peer-nudge 2>&1); RC=$?
check "T1 control: stop-peer-nudge blocks before any send" 'echo "$OUT" | grep -q "\"decision\":\"block\""'

# ---- T3: symlink alias of the pool directory
new_pair t3
mkdir -p "$(dirname "$REQ")"  # the store writes no file, and an alias only resolves through a directory that exists
ln -s "$(dirname "$REQ")" "$SANDBOX/alias"
arm s3 "$SANDBOX/alias/$(basename "$REQ")"
send_chain s3 "[hierarchy-msg $RESP]
- done: PASS"
check "T3: aliased request path vs real response path is allowed" 'allowed'

# ---- T5: a FIFO pointer does not hang the hook
new_pair t5
arm s5 "$REQ"
mkfifo "$SANDBOX/x.txt"
OUT=$(node -e 'process.stdout.write(JSON.stringify({session_id:"s5",cwd:process.env.MAIN,tool_name:"SendMessage",agent_type:"architect",tool_input:{to:"ct-orchestrator",message:"[hierarchy-msg "+process.argv[1]+"]\n- x"}}))' "$SANDBOX/x.txt" | HOME="$FAKEHOME" MAIN="$MAIN" perl -e 'alarm 10; exec @ARGV' sh "$PLUGIN/bin/ah-hook" pretooluse-sendmessage-response 2>&1); RC=$?
check "T5: FIFO pointer does not hang the hook" '[ $RC -ne 142 ]'

echo "pass=$PASS fail=$FAIL"
[ "$FAIL" -eq 0 ]
