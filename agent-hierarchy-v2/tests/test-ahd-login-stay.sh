#!/bin/bash
# agent-hierarchy — the login item's stay rule: a daemon that reads <AH_HOME>/login.json {"stay": true} at startup never
# idle-exits; without it (absent, stay false, stay not the boolean true, unparsable) the idle exit is unchanged.
# Daemons run under /tmp/ahprobe.* with a short idle limit and are recorded, killed and asserted gone.
# Usage: bash tests/test-ahd-login-stay.sh   (exits 0 iff all cases pass)

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
export HOME="$ROOT/home" AH_TEST=1 AHD_IDLE_EXIT_MS=1500
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n"; fi; }
alive() { kill -0 "$1" 2>/dev/null; }
dpid() { node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1]+"/run/ahd.json","utf8")).pid))}catch{}' "$1"; }

# start_case <name> [login.json content]: a daemon under its own AH_HOME; sets AH and D (its pid)
start_case() {
  AH="$ROOT/$1"
  mkdir -p "$AH"
  if [ "$#" -ge 2 ]; then printf '%s' "$2" > "$AH/login.json"; fi
  AH_HOME="$AH" "$PLUGIN/bin/ahd" start >/dev/null 2>&1
  D="$(dpid "$AH")"
  PIDS="$PIDS $D"
}
# gone <pid> <seconds>: waits for the process to exit
gone() { local i; for i in $(seq 1 $(( $2 * 5 ))); do alive "$1" || return 0; sleep 0.2; done; return 1; }

start_case none
check "a daemon started" '[ -n "$D" ]'
check "without login.json the idle exit still ends the daemon" 'gone "$D" 8'

start_case stay '{"stay":true,"since":"2026-01-01T00:00:00.000Z"}'
STAY_PID="$D"
sleep 4.5
check "with {\"stay\":true} the daemon is still running after three idle limits" 'alive "$STAY_PID"'
rm -f "$AH/login.json"
sleep 3
check "removing login.json does not end a running daemon (it keeps the mode it started with)" 'alive "$STAY_PID"'
# the daemon drains only for a session it can identify or the user, so stop is called from a shell proven as a team's Orchestrator
ah_test_team "$ROOT/proof" ls implementor
AH_HOME="$AH" ah_test_prove_self orchestrator ls "$ROOT/proof/teams/ls.json" >/dev/null
AH_HOME="$AH" "$PLUGIN/bin/ahd" stop >/dev/null 2>&1
check "ahd stop still ends a staying daemon" 'gone "$STAY_PID" 8'

start_case stayfalse '{"stay":false}'
check "{\"stay\":false} does not keep the daemon" 'gone "$D" 8'

start_case staystring '{"stay":"true"}'
check "stay as the string \"true\" does not keep the daemon" 'gone "$D" 8'

start_case garbage 'not json'
check "an unparsable login.json does not keep the daemon" 'gone "$D" 8'

start_case arr '[true]'
check "a login.json that is not an object does not keep the daemon" 'gone "$D" 8'

reap; sleep 0.3
LEFT=""; for p in $PIDS; do alive "$p" && LEFT="$LEFT $p"; done
check "no daemon started by this test is still running" '[ -z "$LEFT" ]'

echo
echo "Passed: $PASS  Failed: $FAIL"
[ "$FAIL" -eq 0 ]
