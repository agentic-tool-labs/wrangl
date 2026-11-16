#!/bin/bash
# agent-hierarchy — the overdue failsafe against a real process: with ahd killed (or its watch job frozen) the timer still wakes the
# Orchestrator at a dispatch's deadline plus the margin, names only that dispatch, writes the health file and the journal line, does not
# fire again for it after a restart, and fires for the next dispatch at its own deadline. The timer never starts a daemon and never
# connects to one. Thresholds are shortened through AH_STALL_* (margin 3 s, rescan 1 s); dispatches are backdated so A is due 20 s and B
# 40 s after the start. Every process started is stopped, and each wait ends the suite after a timeout.
# Usage: bash tests/test-stall-failsafe.sh   (exits 0 iff all cases pass; needs AHD_NATIVE_BIN or AH_BIN)

. "$(dirname "$0")/lib-hermetic.sh"
AH_TEST_DAEMON=1
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
. "$PLUGIN/tests/lib-seed.sh"
T="$(mktemp -d /tmp/ahprobe.XXXXXX)"
case "$T" in /tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting" >&2; exit 1;; esac
T="$(cd "$T" && pwd -P)"
export AH_HOME="$T/ah" HOME="$T/home" AH_TEST=1 AHD_WATCH_POLL_MS=500 AH_WATCH_POLL_MS=500 AH_STALL_MARGIN=3 AH_STALL_RESCAN=1
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
TIMER=""
cleanup() {
  local p
  [ -n "$TIMER" ] && { ah_signal_child TERM "$TIMER"; ah_signal_child KILL "$TIMER"; }
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
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:600})"; fi; }
put() { node -e 'const fs=require("fs"),path=require("path");fs.mkdirSync(path.dirname(process.argv[1]),{recursive:true});fs.writeFileSync(process.argv[1],process.argv[2]+"\n")' "$1" "$2"; }
NOW() { node -e 'process.stdout.write(new Date().toISOString())'; }
ago() { node -e 'process.stdout.write(new Date(Date.now()-Number(process.argv[1])*1000).toISOString())' "$1"; }
boxdir() { node -e 'const c=require("crypto"),fs=require("fs"),path=require("path");const tf=process.argv[1];const h=c.createHash("sha256").update(path.join(fs.realpathSync(path.dirname(tf)),path.basename(tf))).digest("hex").slice(0,10);process.stdout.write(path.join(process.env.AH_HOME,"mailbox",path.basename(tf).replace(/\.json$/,"")+"~"+h,process.argv[2]))' "$1" "$2"; }
ahd_pid() { node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid))}catch{}' "$AH_HOME/run/ahd.json"; }
ahd_alive() { local p; p=$(ahd_pid); [ -n "$p" ] && kill -0 "$p" 2>/dev/null; }

mkdir -p "$T/fakebin" "$T/herdr-state"
ah_test_install_fake_herdr "$T/fakebin"
export AH_TEST_FAKE_BIN="$T/fakebin"
export FAKE_STATE_DIR="$T/herdr-state" PATH="$T/fakebin:$PATH"

TF="$HIER/teams/tm.json"
printf '{"team_id":"t-tm","transport":"tmux","mesh":{"policy":"open"},"orchestrator":{"pid":%s,"session_id":"orch"},"members":[{"role":"implementor","name":"cd","transport_id":"%%1","paste":false}]}\n' "$LIVE" | AH_DAEMON=off ah_seed_team "$TF"
ODIR=$(boxdir "$TF" orchestrator)
printf '{"session_id":"orch","address":"orchestrator@tm","boxes":[{"address":"orchestrator@tm","dir":"%s"}],"team_file":"%s","harness":"stub","pid":%s,"pane_id":null,"cwd":"%s"}' "$ODIR" "$TF" "$LIVE" "$PROJ" | AH_DAEMON=off seed_session
DB="$AH_HOME/state/ah.db"
CDIR=$(boxdir "$TF" cd)
# the adapter heartbeat has no served op (an adapter writes it in process), so it is planted as the row that write makes
sqlite3 "$DB" "INSERT INTO mail_adapters(box, session, at_ms, raw) VALUES ('${CDIR#"$AH_HOME"/mailbox/}', 's-cd', CAST(strftime('%s','now') AS INTEGER) * 1000, CAST('{\"v\":1,\"adapter\":\"stub\",\"session_id\":\"s-cd\",\"poll_ms\":5000,\"at\":\"$(NOW)\",\"acked_through\":0}' AS BLOB))"

dispatch() {
  local id=$1 slug=$2 age=$3
  printf -- '---\nid: %s\ntype: request\nto: implementor\nfrom: orchestrator\nslug: %s\nparent: null\nreason: null\neta: small\nto_name: cd\nfrom_name: null\nteam: tm\ncreated: %s\n---\n\n## [0] tldr\n- test\n' "$id" "$slug" "$(ago "$age")" > "$HIER/msgs/$id--implementor--$slug--request.md"
  ah_seed_msgs "$HIER"
  # The brief was sent: one nobody sent to a member with no session owes no alarm, from ahd or from the timer.
  printf '{"v":1,"id":"brief-%s","from":"orchestrator@tm","to":"cd@tm","kind":"brief","route":"pull","created":"%s","ref":"%s"}\n' "$id" "$(ago "$age")" "$HIER/msgs/$id--implementor--$slug--request.md" | AH_DAEMON=off seed_mail "$CDIR"
}
A=20261021-100000-aaaa; B=20261021-100000-bbbb

start_timer() {
  : > "$T/timer.out"
  ( cd "$PROJ" && CLAUDE_CODE_SESSION_ID=orch timeout 120 "$PLUGIN/bin/ah" roster overdue --cwd "$PROJ" > "$T/timer.out" 2>&1; echo "exit=$?" >> "$T/timer.out" ) &
  TIMER=$!
}
wait_timer() {
  local i
  for i in $(seq 1 110); do grep -q '^exit=' "$T/timer.out" 2>/dev/null && return 0; sleep 1; done
  echo "FAIL: the overdue timer did not exit within 110 s"; cat "$T/timer.out" | head -5
  return 1
}
start_daemon() {
  local i
  ( cd "$PROJ" && "$AH_BIN" start --cwd "$PROJ" >"$T/start.out" 2>&1 )
  for i in $(seq 1 20); do ahd_alive && return 0; sleep 0.5; done
  OUT="$(cat "$T/start.out" 2>/dev/null) :: $(ls "$AH_HOME/run" 2>&1 | tr '\n' ' ') :: $(tail -3 "$AH_HOME"/logs/ahd.log* 2>/dev/null)"
  return 1
}
# A failed start is a SKIP only where the environment cannot create unix sockets; any other failure (a stale socket included) is a FAIL.
daemon_unavailable() {
  if cat "$T/start.out" "$AH_HOME"/logs/ahd.log* 2>/dev/null | grep -qE '^([0-9TZ:.-]+ )?ahd (listen failed: listen (AccessDenied|AddressFamilyNotSupported|EPERM|EACCES|EAFNOSUPPORT)\b|listen failed: listen failed: .*(Permission denied|not permitted)|socket path .* is longer than [0-9]+ bytes)'; then
    skip "$1 ahd cannot create a unix socket in this environment (${OUT:0:160}); the cases below run with ahd down from the start"
  else
    FAIL=$((FAIL+1)); echo "FAIL: $1 ahd did not start: ${OUT:0:400}"
  fi
}
journal() { sqlite3 "$DB" "SELECT row FROM stall_journal ORDER BY seq" 2>/dev/null; }

# 30: the daemon is started, killed five seconds in, and the timer carries the wake.
dispatch "$A" first 280
dispatch "$B" second 260
skip() { echo "SKIP: $1"; }
HAVE_DAEMON=0
start_daemon && HAVE_DAEMON=1
if [ "$HAVE_DAEMON" = 1 ]; then check "30.0 ahd is running before the kill" 'ahd_alive'; else daemon_unavailable "30.0"; fi
start_timer
sleep 5
if [ "$HAVE_DAEMON" = 1 ]; then P=$(ahd_pid); ah_signal_ours KILL "$P" "$T"; sleep 1; fi
check "30.1 ahd is dead after the kill" '! ahd_alive'
wait_timer
OUT=$(cat "$T/timer.out")
check "30.2 the timer exited 0 naming A and not B" 'grep -q "FAILSAFE HIT (ah bug): $A " "$T/timer.out" && ! grep -q "$B" "$T/timer.out" && grep -q "^exit=0" "$T/timer.out"'
check "30.3 the wake text carries the five steps" 'grep -q "1) .*stall-diagnose $A  2) handle the late dispatch yourself" "$T/timer.out"'
check "30.4 the health file is set and the journal has ahd-missed" '[ -n "$(sqlite3 "$DB" "SELECT value FROM meta WHERE key = '"'"'overdue.ahd-health'"'"'" 2>/dev/null)" ] && journal | grep -q "\"kind\":\"ahd-missed\""'
check "33.1 the timer started no daemon" '! ahd_alive'
start_timer
wait_timer
OUT=$(cat "$T/timer.out")
check "30.5 the restarted timer fires for B only" 'grep -q "FAILSAFE HIT (ah bug): $B " "$T/timer.out" && ! grep -q "FAILSAFE HIT (ah bug): $A " "$T/timer.out"'
check "33.2 still no daemon after the second run" '! ahd_alive'
TIMER=""

# 31: ahd alive but its watch job frozen (a poll period far beyond the test): the same outcome.
sqlite3 "$DB" "DELETE FROM meta WHERE key LIKE 'overdue.%' OR key LIKE 'stall.%'; DELETE FROM stall_journal"
rm -f "$HIER"/msgs/*--request.md
sqlite3 "$DB" "DELETE FROM mail_entries WHERE box = '${CDIR#"$AH_HOME"/mailbox/}'"
ah_forget_msgs "$A" "$B"
export AHD_WATCH_POLL_MS=3600000 AH_WATCH_POLL_MS=3600000
dispatch "$A" first 280
HAVE_DAEMON=0
start_daemon && HAVE_DAEMON=1
if [ "$HAVE_DAEMON" = 1 ]; then
  check "31.0 ahd is alive with its watch job frozen" 'ahd_alive'
  start_timer
  wait_timer
  OUT=$(cat "$T/timer.out")
  check "31.1 the timer fires for A with ahd alive" 'grep -q "FAILSAFE HIT (ah bug): $A " "$T/timer.out" && grep -q "^exit=0" "$T/timer.out"'
  # The relay chain's asks to relay the unread brief are ahd's mail delivery, not the dispatch clock this failsafe stands behind.
  check "31.2 no clock event for A reached the Orchestrator's box" '! ah_box_entries "$ODIR" | grep -v "\"event\":\"relay-needed\"" | grep -q "$A"'
else
  daemon_unavailable "31"
fi
TIMER=""

echo "passed $PASS, failed $FAIL"
[ "$FAIL" -eq 0 ]
