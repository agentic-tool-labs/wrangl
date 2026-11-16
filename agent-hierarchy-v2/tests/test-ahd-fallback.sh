#!/bin/bash
# agent-hierarchy — ahd: a context field the daemon rejects sends the CLI to the files (same output as with the
# daemon down), `health.requests`, a retry after the daemon died, and senders racing for the append lock.
# Daemons run under /tmp/ahprobe.* and are recorded, killed and asserted gone.
# Usage: bash tests/test-ahd-fallback.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
AH_TEST_DAEMON=1
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE CLAUDE_PID CLAUDE_CODE_SESSION_ID CLAUDE_CODE_MESSAGING_SOCKET WRANGL_HIERARCHY_DIR AH_DAEMON AHD_VERSION_OVERRIDE AHD_IDLE_EXIT_MS AHD_CRASH_AT AHD_TEST_LOCK_HOLD_MS HERDR_PANE_ID TMUX_PANE
EXTRA="$(mktemp -d /tmp/ahprobe.XXXXXX)"
[ -n "$EXTRA" ] && [ -d "$EXTRA" ] || { echo "mktemp failed"; exit 1; }
EXTRA="$(cd "$EXTRA" && pwd -P)"  # one spelling of the team's path: the store keys a team by the directory it is given
AHH="$EXTRA/ah"; mkdir -p "$AHH"
sleep 900 & LIVE=$!
ALLPIDS=""
collect() { ALLPIDS="$ALLPIDS $(cat "$AHH"/logs/ahd.log* 2>/dev/null | sed -n 's/.* pid \([0-9]*\) root .*/\1/p' | tr '\n' ' ')"; }
reap() { collect; local p; for p in $ALLPIDS; do ah_signal_ours CONT "$p" "$AHH"; ah_signal_ours KILL "$p" "$AHH"; done; }
hermetic_on_exit 'reap; ah_signal_child TERM $LIVE; rm -rf "$AHH" "$EXTRA"'
FAKEHOME="$EXTRA/home"; M="$EXTRA/main"; W="$EXTRA/wt"
mkdir -p "$FAKEHOME/.claude/ah2" "$M/.claude/ah2/teams"
git -C "$M" init -q
git -C "$M" -c user.email=t@example.test -c user.name=t commit -q --allow-empty -m init
git -C "$M" worktree add -q "$W" -b wt-branch
printf -- '---\nid: r1\n---\n' > "$M/.claude/ah2/msgs-placeholder"
export AH_HOME="$AHH" HOME="$FAKEHOME" AH_TEST=1
printf '{"team_id":"t-tm","orchestrator":{"pid":%s,"session_id":null},"members":[{"role":"implementor","name":"tm-implementor","transport_id":"p-tm-implementor"}]}\n' "$LIVE" | AH_DAEMON=off ah_seed_team "$M/.claude/ah2/teams/tm.json"
# this shell is the team's Orchestrator, proven; the inbox is read as the member whose address it is
TF="$M/.claude/ah2/teams/tm.json"
proof() { ah_test_prove_self orchestrator tm "$TF" >/dev/null; }
proof
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (RC=$RC OUT=${OUT:0:300} ERR=${ERR:0:200})"; fi; }
mxw() { OUT=$(cd "$W" && "$PLUGIN/bin/ah" msg "$@" 2>"$EXTRA/err"); RC=$?; ERR=$(cat "$EXTRA/err"); }
mx() { OUT=$(cd "$M" && "$PLUGIN/bin/ah" msg "$@" 2>"$EXTRA/err"); RC=$?; ERR=$(cat "$EXTRA/err"); }
jf() { node -e "const o=JSON.parse(require('fs').readFileSync(0,'utf8'));process.stdout.write(String($1))"; }
dpid() { node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.env.AH_HOME+"/run/ahd.json","utf8")).pid))}catch{}'; }
alive() { kill -0 "$1" 2>/dev/null; }
waitfor() { local i; for i in $(seq 1 "$2"); do eval "$1" && return 0; sleep 0.2; done; return 1; }
reset() { collect; local p; for p in $ALLPIDS; do ah_signal_ours CONT "$p" "$AHH"; ah_signal_ours TERM "$p" "$AHH"; done; waitfor '! alive ${ALLPIDS##* }' 25; reap; rm -rf "$AHH/run" "$AHH/logs" "$AHH/mailbox" "$AHH/registrations"; ah_forget_sessions; ALLPIDS=""; proof; }
# <method> <path> [json body]: the daemon's HTTP answer over its socket, as {"status","body"}.
rq() {
  local out body
  out=$(curl -s -m 35 --unix-socket "$AH_HOME/run/ahd.sock" -X "$1" ${3:+-H "content-type: application/json" -d "$3"} -w '\n%{http_code}' "http://ahd$2")
  body=${out%$'\n'*}
  node -e 'let b = null; try { b = JSON.parse(process.argv[2]); } catch {} process.stdout.write(JSON.stringify({ status: Number(process.argv[1]), body: b }))' "${out##*$'\n'}" "$body"
}
norm() { sed -E 's/(b|k|h|t)[0-9]+/ID/g'; }
# the delivery line names a carrier, and an unclaimed prompt send adds a receiver line, only when a claim was made, which AH_DAEMON=off beside a running daemon does not do; every other line must match
normd() { norm | grep -v -e "^delivery: " -e "^no single registered receiver for "; }
# the stored mailbox entries of the state home, one `"id":"<id>"` line each (the test's address has the one box)
ents() { sqlite3 -readonly "$AHH/state/ah.db" "SELECT '\"id\":\"' || id || '\"' FROM mail_entries" 2>/dev/null; }

# ---- a worktree with no hierarchy dir of its own, the team in the main checkout
mx daemon start
# each send has its own text: the same note again within ten minutes is answered with the earlier entry, whatever its --id
mxw send tm-implementor@tm --text hi-b1 --id b1; UP="$OUT"; UERR="$ERR"; URC=$RC
OUT=$(cd "$W" && AH_DAEMON=off "$PLUGIN/bin/ah" msg send tm-implementor@tm --text hi-b2 --id b2 2>"$EXTRA/err"); ORC=$?; OERR=$(cat "$EXTRA/err")
check "worktree without its own hierarchy dir: send with the daemon up equals the files (stdout, exit); the daemon's context-field 400 is announced on stderr, the files run is silent" '[ $URC -eq 0 ] && [ $ORC -eq 0 ] && [ "$(echo "$UP" | normd)" = "$(echo "$OUT" | normd)" ] && [ "$UERR" = "ah msg: warning: ahd unavailable (http 400 bad_request); using files" ] && [ -z "$OERR" ]'
OUT=$(cd "$W" && ah_test_run_as tm-implementor tm "$TF" "$PLUGIN/bin/ah" msg inbox --address tm-implementor@tm 2>"$EXTRA/err"); RC=$?; ERR=$(cat "$EXTRA/err"); UP="$OUT"; URC=$RC; UERR="$ERR"
OUT=$(cd "$W" && AH_DAEMON=off ah_test_run_as tm-implementor tm "$TF" "$PLUGIN/bin/ah" msg inbox --address tm-implementor@tm 2>&1); ORC=$?
check "...and inbox" '[ $URC -eq 0 ] && [ $ORC -eq 0 ] && [ -n "$UP" ] && [ "$UP" = "$OUT" ]'
REG="$AHH/registrations"; mkdir -p "$REG"
mxw ack --session nosuch b1
UP="$OUT"; URC=$RC; UERR="$ERR"
OUT=$(cd "$W" && AH_DAEMON=off "$PLUGIN/bin/ah" msg ack --session nosuch b1 2>"$EXTRA/err"); ORC=$?; OERR=$(cat "$EXTRA/err")
check "...and ack (no address: the same error either way)" '[ $URC -eq $ORC ] && [ "$UERR" = "$OERR" ] && [ "$UP" = "$OUT" ]'
UP=$(cd "$M" && WRANGL_HIERARCHY_DIR="$EXTRA/does-not-exist" "$PLUGIN/bin/ah" msg send tm-implementor@tm --text hi-k1 --id k1 2>&1); URC=$?
OFF=$(cd "$M" && AH_DAEMON=off WRANGL_HIERARCHY_DIR="$EXTRA/does-not-exist" "$PLUGIN/bin/ah" msg send tm-implementor@tm --text hi-k2 --id k2 2>&1); ORC=$?
check "WRANGL_HIERARCHY_DIR pointing at a missing dir: daemon up and the files each land the send once in the team's real mailbox and write nothing under the missing dir" '[ $URC -eq 0 ] && [ $ORC -eq 0 ] && [ "$(ents | grep -c "\"id\":\"k1\"")" = 1 ] && [ "$(ents | grep -c "\"id\":\"k2\"")" = 1 ] && [ ! -e "$EXTRA/does-not-exist" ]'
mx send tm-implementor@tm --ref "$EXTRA/not-a-msgs-dir.md" --id h1
UP="$ERR"; URC=$RC
OFF=$(cd "$M" && AH_DAEMON=off "$PLUGIN/bin/ah" msg send tm-implementor@tm --ref "$EXTRA/not-a-msgs-dir.md" --id h2 2>&1 >/dev/null); ORC=$?
check "a genuine bad_request (bad --ref) still surfaces, identically daemon up or down" '[ $URC -ne 0 ] && [ $URC -eq $ORC ] && echo "$UP" | grep -q "^error: bad_request:" && [ "$UP" = "$OFF" ]'
J=$(rq POST /v1/send '{"to":"tm-implementor@tm","text":"x","id":"h3","cwd":"'"$W"'","hierarchy_dir":"'"$W/.claude/ah2"'","main_hierarchy_dir":"'"$M/.claude/ah2"'"}')
check "the daemon's own answer names the rejected field (error.field) so a client can tell it from a real bad_request" 'echo "$J" | jf "o.status===400&&o.body.error.code===\"bad_request\"&&o.body.error.field===\"hierarchy_dir\"" | grep -q true'

# ---- health.requests
J0=$(rq GET /v1/health | jf "o.body.requests")
rq GET /v1/health > /dev/null; rq GET /v1/health > /dev/null
J1=$(rq GET /v1/health | jf "o.body.requests")
check "health.requests is a number and /v1/health itself does not count" '[ "$J0" = "$J1" ] && [ "$J0" -ge 1 ]'
rq GET /v1/nope > /dev/null
rq POST /v1/ack '{"address":"x@y","ids":[],"by":"cli"}' > /dev/null
J2=$(rq GET /v1/health | jf "o.body.requests")
check "...every other request counts once (404 and 400 included)" '[ $((J2 - J1)) -eq 2 ]'
mxw send tm-implementor@tm --text more --id t1
J3=$(rq GET /v1/health | jf "o.body.requests")
check "...and a CLI send through the daemon counts" '[ $((J3 - J2)) -ge 1 ]'
mx daemon stop; reset

# ---- the daemon dies after it answered: a retry of the same send reaches the files and lands once
# (the native daemon has no fault-injection hooks, so the daemon is killed from outside after its reply)
mx daemon start; D=$(dpid)
mx send tm-implementor@tm --text first --id c1
check "a send through the daemon lands once" '[ $RC -eq 0 ] && [ "$(ents | grep -c "\"id\":\"c1\"")" = 1 ]'
ah_signal_ours KILL "$D" "$AHH"; waitfor '! alive $D' 25
mx send tm-implementor@tm --text first --id c1
check "after the daemon is gone, the same id sent again exits 0 and is not appended twice (one entry)" '[ $RC -eq 0 ] && [ "$(ents | grep -c "\"id\":\"c1\"")" = 1 ]'
mx send tm-implementor@tm --text second --id c2
check "...and a different id is appended (nearest non-match of the retry)" '[ $RC -eq 0 ] && [ "$(ents | grep -c "\"id\":\"c[12]\"")" = 2 ]'
mx daemon stop; reset

# ---- the append lock: senders racing for it all land, once each
AH_DAEMON=off mx send tm-implementor@tm --text warm --id w0
RACERS=""
for i in 1 2 3 4 5 6; do
  ( cd "$M" && AH_DAEMON=off "$PLUGIN/bin/ah" msg send tm-implementor@tm --text "race $i" --id "l$i" > "$EXTRA/l$i" 2>&1 ) &
  RACERS="$RACERS $!"
done
wait $RACERS
RACED=$(ents | grep -c "\"id\":\"l[1-6]\"")
DISTINCT=$(ents | grep -o "\"id\":\"l[1-6]\"" | sort -u | wc -l | tr -d ' ')
check "six senders racing for the append lock all land, once each" '[ "$RACED" = 6 ] && [ "$DISTINCT" = 6 ] && ! grep -L "entry l" "$EXTRA"/l[1-6] | grep -q .'

reap; sleep 0.3
LEFT=""; for p in $ALLPIDS; do alive "$p" && LEFT="$LEFT $p"; done
check "no daemon started by this test is still running" '[ -z "$LEFT" ]'

echo
echo "Passed: $PASS  Failed: $FAIL"
[ "$FAIL" -eq 0 ]
