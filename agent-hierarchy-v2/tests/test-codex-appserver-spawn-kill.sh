#!/bin/bash
# agent-hierarchy — a run of tests/test-codex-appserver-spawn.sh that is terminated while a launch is in flight (what run-suite.sh's
# per-file timeout does) leaves nothing behind: its process ends, no process names its sandbox, and the sandbox directory is gone.
# The ready wait is stretched so a spawn is certainly in flight when the signal arrives.
# Usage: bash tests/test-codex-appserver-spawn-kill.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
UNDER="${SPAWN_TEST_UNDER:-$PLUGIN/tests/test-codex-appserver-spawn.sh}"
T="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$T" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting" >&2; exit 1;; esac
PID=""
SB=""
end() {
  # a failed case must not leave the run it started behind; only a job this shell still holds is signalled, because the
  # number of a run that ended and was reaped can already belong to an unrelated process
  ah_signal_child KILL "$PID"
  if [ -n "$SB" ]; then for p in $(pgrep -f -- "$SB/" 2>/dev/null); do [ "$p" = "$$" ] || ah_signal_ours KILL "$p" "$SB/"; done; fi
  case "$SB" in /private/tmp/ahprobe.??????) rm -rf "$SB" ;; esac
  rm -rf "$T"
}
hermetic_on_exit end
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n"; fi; }

# the descendants of a pid, as "pid command" lines
descendants() { ps -axo pid=,ppid=,command= | awk -v root="$1" '{ pid[NR] = $1; ppid[NR] = $2; $1 = ""; $2 = ""; line[NR] = $0 } END { n = 1; q[1] = root; while (n > 0) { cur = q[n]; n--; for (i in pid) if (ppid[i] == cur) { print pid[i] line[i]; q[++n] = pid[i] } } }'; }

SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting" >&2; exit 1;; esac
SPAWN_TEST_SANDBOX="$SB" AH_HERDR_READY_WAIT_MS=15000 AH_COMPOSER_REREAD_MS=0 bash "$UNDER" > "$T/out.log" 2>&1 &
PID=$!

# wait for its second launch: the first leaves no server, the second does (a fake app-server is running while the spawn waits).
# The sandbox is the one made above and handed to the run; it is never read back out of a command line, because the binary under
# test can itself live in another sandbox (a gate publishes it into one) and that path leads every ahd line.
for i in $(seq 1 240); do
  D=$(descendants "$PID")
  if printf '%s\n' "$D" | grep -F "$SB/" | grep -q 'fake-codex-appserver' && printf '%s\n' "$D" | grep -q 'roster spawn-one'; then break; fi
  sleep 0.5
done
check "the run is under way: its sandbox is known and a launch is in flight beside a running fake app-server" '[ -n "$SB" ] && printf "%s\n" "$D" | grep -q "fake-codex-appserver" && printf "%s\n" "$D" | grep -q "roster spawn-one"'

ah_signal_child TERM "$PID"
for i in $(seq 1 120); do jobs -p | grep -qx "$PID" || break; kill -0 "$PID" 2>/dev/null || break; sleep 0.5; done
check "the terminated run ends" '! kill -0 "$PID" 2>/dev/null'
wait "$PID" 2>/dev/null; RC=$?
check "...with the termination status (143), not a pass" '[ "$RC" -eq 143 ]'
# what the run signalled on its way out needs a moment to exit and to stop writing: under load it is not instant
named() { [ -n "$SB" ] && [ -n "$(pgrep -f -- "$SB/" | grep -v "^$$\$")" ]; }
for i in $(seq 1 40); do if ! named && [ ! -e "$SB" ]; then break; fi; sleep 0.5; done
check "no process names its sandbox any more" '[ -n "$SB" ] && ! named'
check "its sandbox directory is gone" '[ -n "$SB" ] && [ ! -e "$SB" ]'
if [ "$FAIL" -ne 0 ] && [ -n "$SB" ]; then
  echo "left behind under $SB:"; ps -axo pid=,ppid=,stat=,command= | grep -F -- "$SB/" | grep -v grep | cut -c1-240; ls -la "$SB" 2>&1 | head -20
fi

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
