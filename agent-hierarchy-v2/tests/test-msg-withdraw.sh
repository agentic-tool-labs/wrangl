#!/bin/bash
# agent-hierarchy — `ah msg withdraw`: the sender ends a mailed brief, or re-addresses it to another member of the same role.
# HOME- and AH_HOME-redirected; real state untouched.
# Usage: bash tests/test-msg-withdraw.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-seed.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
SANDBOX="$(dirname "$AH_HOME")/withdraw"  # inside the test home's ahprobe sandbox: the hierarchy dir is trusted there and nowhere else under /tmp
# The trap below removes $SANDBOX recursively: an unset AH_HOME would make it ./withdraw, so nothing is created or removed unless it is
# inside an ahprobe sandbox.
case "${AH_HOME:-}:$SANDBOX" in
  ?*:/tmp/ahprobe.??????/withdraw|?*:/private/tmp/ahprobe.??????/withdraw|?*:/tmp/ahprobe.??????/t???/withdraw|?*:/private/tmp/ahprobe.??????/t???/withdraw) ;;
  *) echo "refusing to run: AH_HOME='${AH_HOME:-}' does not put the sandbox '$SANDBOX' inside /tmp/ahprobe.*"; exit 1 ;;
esac
mkdir -p "$SANDBOX" && [ -d "$SANDBOX" ] || { echo "sandbox mkdir failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
FAKEHOME="$SANDBOX/home"
HD="$SANDBOX/hier"
PENDING="$FAKEHOME/.claude/ah2/agent-hierarchy.peer-pending.jsonl"
mkdir -p "$FAKEHOME/.claude/ah2" "$HD/teams"
PASS=0; FAIL=0
printf '{"team_id":"t-wd","transport":"tmux","orchestrator":{"session_id":"s-wd"},"members":[{"role":"reviewer","name":"wd-reviewer","transport_id":"p1"},{"role":"reviewer","name":"wd-reviewer2","transport_id":"p2"},{"role":"implementor","name":"wd-implementor","transport_id":"p3"}]}\n' | ah_seed_team "$HD/teams/wd.json"
SID=$(ah_test_prove_self orchestrator wd "$HD/teams/wd.json")  # this shell is the Orchestrator that sends and withdraws

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:500})"; fi
}
msg() { OUT=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" "$PLUGIN/bin/ah" msg "$@" 2>&1); RC=$?; OUT=$(ah_drop_checklist_hint <<< "$OUT"); }
field() { node -e 'let v=JSON.parse(process.argv[1]);for(const k of process.argv[2].split("."))v=v?.[k];process.stdout.write(v==null?"":String(v))' "$OUT" "$1"; }
box() { printf '%s/mailbox/%s' "$AH_HOME" "$(sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT box FROM mail_entries WHERE box LIKE '%/$1' LIMIT 1")"; }  # a box the store holds entries in
ahcli() { node -e 'process.stdout.write(JSON.stringify({session_id:process.argv[3],cwd:process.argv[1],tool_name:"Bash",tool_input:{command:process.argv[2]}}))' "$SANDBOX" "$1" "$SID" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" pretooluse-ah-cli 2>&1; }

new_request() { # <to-name>: a reviewer request; REQ is its path, RID its id
  WD_N=$((${WD_N:-0} + 1))
  msg new --to reviewer --from orchestrator --slug wd --title "Fix test titles" --to-name "$1" --id "$(printf "20260101-0000%02d-wd%02d" "$WD_N" "$WD_N")"
  RID=$(field id); REQ=$(field path)
  # An empty REQ makes dirname "." and later writes land in the caller's cwd; nothing here may be written outside the sandbox.
  case "$REQ" in "$SANDBOX"/?*) ;; *) echo "FAIL: msg new gave no request path under the sandbox (RC=$RC OUT=${OUT:0:500})"; exit 1 ;; esac
  [ -n "$RID" ] || { echo "FAIL: msg new gave no request id (RC=$RC OUT=${OUT:0:500})"; exit 1; }
}

# ---- 1: a withdrawal ends the brief and tells the addressee
new_request wd-reviewer
REQ_BEFORE=$(ah_msg_text "$REQ" --cwd "$PROJ")
msg send wd-reviewer@wd --ref "$RID" --id b1 --json
check "setup: the brief was sent" '[ $RC -eq 0 ]'
msg withdraw b1 --reason "moved to the second reviewer" --json
check "withdraw: exit 0 and names the entry" '[ $RC -eq 0 ] && [ "$(field withdrawn)" = b1 ]'
check "withdraw: progress is not_delivered for an unread brief" '[ "$(field progress)" = not_delivered ]'
msg status b1 --json
check "status: withdrawn, with the reason" '[ "$(field state)" = withdrawn ] && [ "$(field withdrawn.reason)" = "moved to the second reviewer" ]'
check "the addressee's box holds the note naming the reason" 'ah_box_entries wd-reviewer | grep -q "withdrawn by orchestrator@wd: moved to the second reviewer"'
check "the stored request is unchanged" '[ "$(ah_msg_text "$REQ" --cwd "$PROJ")" = "$REQ_BEFORE" ]'

# ---- 2: a second withdrawal and a non-sender are refused
msg withdraw b1 --reason again
check "an already withdrawn brief is refused already_closed" '[ $RC -eq 1 ] && echo "$OUT" | grep -q already_closed'
printf '{"v":1,"id":"b2","from":"wd-implementor@wd","to":"wd-reviewer@wd","kind":"brief","route":"pull","created":"2026-10-18T12:00:00.000Z","ref":"%s"}' "$REQ" | seed_mail "$(box wd-reviewer)"
msg withdraw b2 --reason "not mine"
check "a brief sent by someone else is refused not_sender" '[ $RC -eq 1 ] && echo "$OUT" | grep -q not_sender'
msg status b2 --json
check "and its state is unchanged" '[ "$(field state)" != withdrawn ]'

# ---- 3: a re-address mails the same request to the other reviewer and keeps the dispatch with the new member
new_request wd-reviewer
msg send wd-reviewer@wd --ref "$RID" --id b3 --json
OUT=$(ahcli "$PLUGIN/bin/ah msg withdraw b3 --reason busy --to wd-reviewer2@wd --cwd $SANDBOX")
check "the gate allows a re-address" 'echo "$OUT" | grep -q "\"permissionDecision\":\"allow\""'
check "the gate pre-records no dispatch row" '! ah_peer_rows | grep -q "\"request_id\":\"$RID\""'
msg withdraw b3 --reason busy --to wd-reviewer2@wd --json --session "$SID"
check "re-address: exit 0" '[ $RC -eq 0 ]'
check "the CLI records the dispatch row for the new member once it landed" 'ah_peer_rows | grep -q "\"request_id\":\"$RID\"" && ah_peer_rows | grep -q "wd-reviewer2@wd"'
NEWID=$(field readdressed_to)
check "re-address: the new box holds a live brief with the same ref" 'ah_box_entries wd-reviewer2 | grep "\"id\":\"$NEWID\"" | grep -qF "$REQ"'
msg status b3 --json
check "re-address: the old brief is withdrawn and names the new entry" '[ "$(field state)" = withdrawn ] && [ "$(field withdrawn.readdressed_to)" = "$NEWID" ]'
msg withdraw "$NEWID" --reason x --to wd-implementor@wd
check "a different role is refused role_mismatch" '[ $RC -eq 1 ] && echo "$OUT" | grep -q role_mismatch'
msg status "$NEWID" --json
check "and the new brief stays live" '[ "$(field state)" != withdrawn ]'

# ---- 4: a drafted response is reported
new_request wd-reviewer
msg send wd-reviewer@wd --ref "$RID" --id b4 --json
mkdir -p "$(dirname "$REQ")"
printf -- '---\nid: %s\ntype: response\n---\n\n## [0] tldr\n- [1] status: half written\n' "$RID" > "$(dirname "$REQ")/$RID--orchestrator--wd--response.md"
ah_seed_msgs "$(dirname "$(dirname "$REQ")")"
msg withdraw b4 --reason r --json
check "progress is response_drafted" '[ "$(field progress)" = response_drafted ]'

# ---- 5: a withdrawn request no longer blocks the Orchestrator's Stop; the same request unwithdrawn does
stop_hook() { node -e 'process.stdout.write(JSON.stringify({hook_event_name:"Stop",session_id:process.argv[2],cwd:process.argv[1]}))' "$SANDBOX" "$SID" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" stop-orchestrator-liveness 2>&1; }
old_dispatch() { # <request path> <request id> <to address>: a dispatch row this session made long ago, past any eta
  ah_seed_peer "$(node -e 'const[p,i,a,s]=process.argv.slice(1);process.stdout.write(JSON.stringify({type:"dispatch",session_id:s,request_id:i,to:"reviewer",to_addr:a,path:p,created:"2020-01-01T00:00:00+00:00"}))' "$1" "$2" "$3" "$SID")"
}
new_request wd-reviewer
sqlite3 "$AH_HOME/state/ah.db" "UPDATE msg_messages SET created_ms = 1577836800000 WHERE id = '$RID'"
msg send wd-reviewer@wd --ref "$RID" --id b5 --json
old_dispatch "$REQ" "$RID" wd-reviewer@wd
OUT=$(stop_hook)
check "setup: an unwithdrawn overdue dispatch blocks the Stop hook" 'echo "$OUT" | grep -q "\"decision\":\"block\"" && echo "$OUT" | grep -qF "$RID"'
msg withdraw b5 --reason r --json
check "setup: the withdrawal succeeded" '[ $RC -eq 0 ]'
OUT=$(stop_hook)
check "after a plain withdraw the Stop hook does not block for that request" '! echo "$OUT" | grep -qF "$RID"'

echo "---"; echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
