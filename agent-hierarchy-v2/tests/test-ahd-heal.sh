#!/bin/bash
# agent-hierarchy — ahd heal end to end (ah2-22, crash-resume 14.11): against a real daemon in a throwaway home, a healthy daemon is
# judged not hung and left alone, a daemon frozen with SIGSTOP is proven hung and replaced (TERM, then KILL, then a normal start),
# a second heal while one runs does nothing, and a direct write that gives up on a busy database starts one. The verdict takes real
# time: this suite runs for about three minutes.
# Daemons run under /tmp/ahprobe.* and are recorded, killed and asserted gone.
# Usage: bash tests/test-ahd-heal.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
AH_TEST_DAEMON=1
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE CLAUDE_PID CLAUDE_CODE_SESSION_ID CLAUDE_CODE_MESSAGING_SOCKET WRANGL_HIERARCHY_DIR AH_DAEMON AHD_VERSION_OVERRIDE AHD_CRASH_AT AHD_TEST_LOCK_HOLD_MS HERDR_PANE_ID TMUX_PANE
ROOT="$(mktemp -d /tmp/ahprobe.XXXXXX)"
[ -n "$ROOT" ] && [ -d "$ROOT" ] || { echo "mktemp failed"; exit 1; }
PIDS=""
reap() { local p; for p in $PIDS; do ah_signal_ours CONT "$p" "$ROOT"; ah_signal_ours KILL "$p" "$ROOT"; done; }
hermetic_on_exit 'reap; rm -rf "$ROOT"'
mkdir -p "$ROOT/home/.claude/ah2"
export HOME="$ROOT/home" AH_TEST=1 AHD_IDLE_EXIT_MS=900000
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n"; fi; }
alive() { kill -0 "$1" 2>/dev/null; }
dpid() { node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1]+"/run/ahd.json","utf8")).pid))}catch{}' "$1"; }
gone() { local i; for i in $(seq 1 $(( $2 * 5 ))); do alive "$1" || return 0; sleep 0.2; done; return 1; }

AH="$ROOT/ah"
mkdir -p "$AH"
export AH_HOME="$AH"
"$PLUGIN/bin/ahd" start >/dev/null 2>&1
D="$(dpid "$AH")"
PIDS="$PIDS $D"
check "a daemon started" '[ -n "$D" ] && alive "$D"'

# a healthy daemon: one probe once it has been up 30 s, a reply, nothing touched
"$PLUGIN/bin/ahd" heal >"$ROOT/heal1.out" 2>"$ROOT/heal1.err"; RC=$?
check "heal on a healthy daemon exits 0" '[ "$RC" = 0 ]'
check "it says the daemon is not hung because it replied" 'grep -q "ahd is not hung (replied)" "$ROOT/heal1.out"'
check "the healthy daemon is the same process, still running" '[ "$(dpid "$AH")" = "$D" ] && alive "$D"'
check "a heal that restarted nothing recorded no restart" '! grep -q "restarted" "$AH/run/heal-state.json" 2>/dev/null'

# a frozen daemon: accepts nothing and answers nothing, so every probe misses
ah_signal_ours STOP "$D" "$ROOT"
"$PLUGIN/bin/ahd" heal >"$ROOT/heal2.out" 2>"$ROOT/heal2.err" &
HEAL=$!
sleep 3
"$PLUGIN/bin/ahd" heal >"$ROOT/heal3.out" 2>"$ROOT/heal3.err"; RC3=$?
check "a second heal while one runs exits 0 and names the first" '[ "$RC3" = 0 ] && grep -q "^heal in progress (pid $HEAL)" "$ROOT/heal3.out"'
check "nothing was signalled in the first seconds: the frozen daemon is still there" 'alive "$D"'
wait "$HEAL"; RC2=$?
N="$(dpid "$AH")"
[ -n "$N" ] && PIDS="$PIDS $N"
check "heal on a frozen daemon exits 0" '[ "$RC2" = 0 ]'
check "it reports the restart with the verdict and the signal" 'grep -q "^restarted by heal: ahd pid $D replaced by $N (H1, KILL)" "$ROOT/heal2.out"'
check "it probed at least five times before it signalled" '[ "$(grep -c "no reply within 10 s" "$ROOT/heal2.err")" -ge 5 ]'
check "the frozen daemon is gone" 'gone "$D" 5'
check "a new daemon runs and answers" '[ -n "$N" ] && [ "$N" != "$D" ] && alive "$N" && "$PLUGIN/bin/ahd" health >/dev/null 2>&1'
check "the log of the hung daemon was copied" 'ls "$AH"/logs/ahd-hung-*.log >/dev/null 2>&1'
check "the journal rows were written to the database, not dropped to the heal output" '! grep -q "journal" "$ROOT/heal1.err" "$ROOT/heal2.err"'
check "the restart is recorded for the cap and the doctor row" 'grep -q "\"kind\":\"restarted\"" "$AH/run/heal-state.json" && grep -q "\"restarts\":\[\"" "$AH/run/heal-state.json"'
check "the heal left no pid file behind" '[ ! -e "$AH/run/heal.pid" ]'

# trigger 1, in the published binary: a command's direct write that gives up on a busy database while the daemon does not answer
# fails as before and starts a heal, which it names. The heal is stopped at once: its verdict is the case above.
if command -v sqlite3 >/dev/null 2>&1 && [ -n "$N" ]; then
  ah_signal_ours STOP "$N" "$ROOT"
  ( sqlite3 "$AH/state/ah.db" "BEGIN IMMEDIATE;" ".shell sleep 8" "COMMIT;" >/dev/null 2>&1 ) &
  HOLD=$!
  sleep 1
  mkdir -p "$ROOT/proj"
  "$PLUGIN/bin/ah" work add "written while the database is held" --cwd "$ROOT/proj" >"$ROOT/dw.out" 2>"$ROOT/dw.err"; RCW=$?
  HP="$(cat "$ROOT/dw.out" "$ROOT/dw.err" | sed -n 's/.*ahd heal started (pid \([0-9][0-9]*\)).*/\1/p' | head -1)"
  check "a direct write that gave up on a busy database with an unanswering daemon fails and names the heal it started" '[ "$RCW" != 0 ] && [ -n "$HP" ]'
  check "that heal is an ahd heal started by the direct write" '[ -n "$HP" ] && ps -ww -o command= -p "$HP" | grep -q "heal --trigger direct-write"'
  [ -n "$HP" ] && ah_signal_ours KILL "$HP" 'heal --trigger direct-write'
  wait "$HOLD" 2>/dev/null
  ah_signal_ours CONT "$N" "$ROOT"
  check "the daemon the stopped heal was judging was not signalled" 'alive "$N"'
else
  echo "SKIP: no sqlite3 to hold the database; the direct-write trigger is not exercised"
fi

reap; sleep 0.3
LEFT=""; for p in $PIDS; do alive "$p" && LEFT="$LEFT $p"; done
check "no daemon started by this test is still running" '[ -z "$LEFT" ]'

echo
echo "Passed: $PASS  Failed: $FAIL"
[ "$FAIL" -eq 0 ]
