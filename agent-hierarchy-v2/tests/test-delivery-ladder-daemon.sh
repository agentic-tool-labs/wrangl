#!/bin/bash
# agent-hierarchy — the delivery ladder as the daemon runs it: the tick walks an unacked entry down the rungs, mails the relay
# request and the delivery-failed event, and `daemon status` and `inbox` show where the entry is. A member with `paste: false`
# keeps the walk away from any pane; the ladder's per-row rules are tests/test-delivery-ladder.sh's. The daemon's poll is lowered,
# which scales the ladder's waits with it (rung 3 lasts 20 s here). Every wait ends the suite after 30 s, naming what it waited for
# and printing the end of the daemon log (tests/lib-wait.sh). Every daemon started is stopped.
# Usage: bash tests/test-delivery-ladder-daemon.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
AH_TEST_DAEMON=1
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
T="$(mktemp -d /tmp/ahprobe.XXXXXX)"
case "$T" in /tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting" >&2; exit 1;; esac
T="$(cd "$T" && pwd -P)"
export AH_HOME="$T/ah" HOME="$T/home" AH_TEST=1 AHD_WATCH_POLL_MS=500 AH_WATCH_POLL_MS=500
PROJ="$T/proj"; HIER="$PROJ/.claude/ah2"
export WRANGL_HIERARCHY_DIR="$HIER"
for v in AH_HOME HOME WRANGL_HIERARCHY_DIR; do case "${!v}" in "$T"/*) ;; *) echo "$v is not under $T; aborting" >&2; exit 1;; esac; done
mkdir -p "$AH_HOME" "$HOME/.claude/ah2" "$HIER/teams" "$HIER/msgs"
git -C "$PROJ" init -q
sleep 3000 & LIVE=$!
daemon_pids() {
  { node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid)+"\n")}catch{}' "$AH_HOME/run/ahd.json"
    cat "$AH_HOME"/logs/ahd.log* 2>/dev/null | sed -n 's/.* pid \([0-9]*\) root .*/\1/p'; } | sort -u
}
cleanup() {
  local p
  for p in $(daemon_pids); do ah_signal_ours CONT "$p" "$T"; ah_signal_ours TERM "$p" "$T"; done
  ah_signal_child CONT "$LIVE"; ah_signal_child TERM "$LIVE"
  sleep 0.3
  for p in $(daemon_pids); do ah_signal_ours KILL "$p" "$T"; done
  ah_signal_child KILL "$LIVE"
  local left; left=$(pgrep -f "$T" 2>/dev/null | grep -v "^$$\$" | tr '\n' ' ')
  [ -n "${left// /}" ] && echo "FAIL: processes still alive under $T: $left"
  rm -rf "$T"
}
hermetic_on_exit cleanup
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:500})"; fi; }
. "$(cd "$(dirname "$0")" && pwd)/lib-wait.sh"
WAITFOR_LOG="$AH_HOME/logs/ahd.log*"
mx() { OUT=$(cd "$PROJ" && "$PLUGIN/bin/ah" msg "$@" 2>"$T/err"); RC=$?; }
DB="$AH_HOME/state/ah.db"
# A box directory: <ah home>/mailbox/<team>~<first 10 hex of sha256 of the team file's real path>/<name>.
boxdir() { node -e 'const c=require("crypto"),fs=require("fs"),path=require("path");const tf=path.join(fs.realpathSync(path.dirname(process.argv[1])),path.basename(process.argv[1]));const h=c.createHash("sha256").update(tf).digest("hex").slice(0,10);process.stdout.write(path.join(process.env.AH_HOME,"mailbox",path.basename(tf).replace(/\.json$/,"")+"~"+h,process.argv[2]))' "$1" "$2"; }

TF="$HIER/teams/tm.json"
printf '{"team_id":"t-tm","transport":"tmux","mesh":{"policy":"open"},"orchestrator":{"pid":%s,"session_id":"orch"},"members":[{"role":"implementor","name":"cd","transport_id":"%%1","paste":false}]}\n' "$LIVE" | AH_DAEMON=off ah_seed_team "$TF"
ODIR=$(boxdir "$TF" orchestrator); CDIR=$(boxdir "$TF" cd)
# the adapter heartbeat has no served op (an adapter writes it in process), so it is planted as the row that write makes
NOW_MS=$(node -e 'process.stdout.write(String(Date.now()))')
sqlite3 "$DB" "INSERT INTO mail_adapters(box, session, at_ms, raw) VALUES ('${CDIR#"$AH_HOME"/mailbox/}', 's-cd', $NOW_MS, CAST('{\"v\":1,\"adapter\":\"stub\",\"session_id\":\"s-cd\",\"poll_ms\":5000,\"at\":\"'||strftime('%Y-%m-%dT%H:%M:%fZ','now')||'\",\"acked_through\":0}' AS BLOB))"
printf '{"single_carrier":false}\n' > "$HIER/agent-hierarchy.json"  # the ladder this suite walks is the one an entry with no carrier claim follows
ah_test_prove_self orchestrator tm "$TF" "$ODIR" orch > /dev/null  # this suite runs no hook, so its own shell is the Orchestrator session
# A question: a peer's plain note to a member waits for the member's next turn in the digest, and the ladder leaves it alone.
mx send cd@tm --from orchestrator@tm --session orch --kind note --text "QUESTION: for cd" --json
ID=$(node -e 'process.stdout.write(JSON.parse(process.argv[1]).entry.id)' "$OUT")
# The entry and its routing row are two minutes old, so the ladder's first tick finds its rung-2 wait already over.
AGO_MS=$(node -e 'process.stdout.write(String(Date.now() - 120000))'); AGO=$(node -e 'process.stdout.write(new Date(Number(process.argv[1])).toISOString())' "$AGO_MS")
sqlite3 "$DB" "UPDATE mail_entries SET created_ms = $AGO_MS, raw = CAST(json_set(CAST(raw AS TEXT), '\$.created', '$AGO') AS BLOB) WHERE id = '$ID';
  UPDATE mail_attempts SET at_ms = $AGO_MS, raw = CAST(json_set(CAST(raw AS TEXT), '\$.at', '$AGO') AS BLOB) WHERE id = '$ID'"
rows() {
  sqlite3 -readonly "$DB" "SELECT CAST(raw AS TEXT) FROM mail_attempts WHERE id = '$ID' ORDER BY pk" | node -e '
    const rows = require("fs").readFileSync(0, "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l));
    process.stdout.write(rows.filter((r) => r.ladder === true || r.state === "routed-pull").map((r) => [r.rung, r.state, r.attempt, r.reason].filter((x) => x !== undefined).join(":")).join(","));'
}
orch_inbox() { OUT=$(cd "$PROJ" && "$PLUGIN/bin/ah" msg inbox --address orchestrator@tm --all 2>/dev/null); }

check "before the daemon runs the entry shows only its routing rung" '[ "$(rows)" = "2:routed-pull" ]'
mx daemon start
check "the daemon starts" '[ $RC -eq 0 ]'
waitfor '[ "$(rows)" != "2:routed-pull" ]' 60
OUT=$(rows)
check "the daemon's tick moves the unacked entry to rung 3 by itself" 'printf %s "$OUT" | grep -q "3:pending:1"'
waitfor 'orch_inbox; printf %s "$OUT" | grep -q relay-needed' 60
orch_inbox
check "the Orchestrator is mailed the relay-needed request for the entry" 'printf %s "$OUT" | grep relay-needed | grep -q "$ID"'
mx inbox --address cd@tm --all
check "inbox shows the entry's rung and state after its line" 'printf %s "$OUT" | grep "$ID" | grep -Eq "\(rung 3 (pending|sending) carrier orchestrator@tm\)$"'
waitfor '[ "$(rows | tr "," "\n" | grep -c "5:escalated")" = 1 ]' 60
OUT=$(rows)
check "the rung-3 deadline passes and a member that takes no paste goes to rung 5" 'printf %s "$OUT" | grep -q "4:pending:1:rung3-timeout" && printf %s "$OUT" | grep -q "5:escalated:paste-disabled"'
waitfor 'orch_inbox; printf %s "$OUT" | grep -q delivery-failed' 60
orch_inbox
check "the Orchestrator is mailed exactly one delivery-failed naming the recipient" '[ "$(printf %s "$OUT" | grep -c delivery-failed)" = 1 ] && printf %s "$OUT" | grep delivery-failed | grep -q "cd@tm"'
mx daemon status
check "daemon status counts the escalated entry on the ladder line" 'printf %s "$OUT" | grep -q "^ladder: pending 0, sending 0, paused 0, escalated 1$"'
mx daemon status --json
check "daemon status --json carries the same counts" '[ "$(node -e "const o=JSON.parse(process.argv[1]);console.log(JSON.stringify(o.ladder))" "$OUT")" = "{\"pending\":0,\"sending\":0,\"paused\":0,\"escalated\":1}" ]'
mx inbox --address cd@tm --all
check "inbox tags the escalated entry" 'printf %s "$OUT" | grep "$ID" | grep -q "(rung 5 escalated)$"'
# mailbox.ack is not served through `ahd state` either: the ack is planted as the row an ack writes
sqlite3 "$DB" "INSERT INTO mail_acks(box, id, at, by, raw) VALUES ('${CDIR#"$AH_HOME"/mailbox/}', '$ID', strftime('%Y-%m-%dT%H:%M:%fZ','now'), 'cli', CAST('{\"v\":1,\"id\":\"$ID\",\"at\":\"'||strftime('%Y-%m-%dT%H:%M:%fZ','now')||'\",\"by\":\"cli\"}' AS BLOB))"
waitfor '[ "$(rows | tr "," "\n" | grep -c delivered)" = 1 ]' 60
check "an ack after the escalation gives the entry its delivered row" '[ "$(rows | tr "," "\n" | grep -c delivered)" = 1 ]'
waitfor 'mx daemon status; printf %s "$OUT" | grep -q "^ladder: none$"' 60
check "daemon status says ladder: none once the entry is delivered" 'printf %s "$OUT" | grep -q "^ladder: none$"'
mx daemon stop
mx daemon status
check "with the daemon down status says down and has no ladder line" 'printf %s "$OUT" | head -1 | grep -q "^down" && ! printf %s "$OUT" | grep -q "^ladder:"'

echo "----"
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
