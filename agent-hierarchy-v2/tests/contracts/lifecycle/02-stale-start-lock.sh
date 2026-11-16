#!/bin/bash
# K5 scenario 2: the start lock run/ahd.lock. A lock whose `at` is 11 s old is stale (whatever its pid) and is taken over, so
# the start succeeds. A lock 5 s old is fresh: a start a moment later has not taken it, and has not started a daemon.
. "$(dirname "$0")/_lib.sh"
lc_runtimes
lc_init
mkdir -p "$AH_HOME/run"
plant() { node -e 'require("fs").writeFileSync(process.argv[1], JSON.stringify({pid: 999999, at: Date.now() - Number(process.argv[2]), nonce: process.argv[3]}))' "$AH_HOME/run/ahd.lock" "$1" "$2"; }

# a fresh lock (5 s old, nobody holds it): a start does not take it in the first second
plant 5000 fresh
lc_ctl start >"$T/fresh.json" 2>/dev/null &
SP=$!
sleep 1
lc_check "a lock 5 s old is not taken over: after 1 s it still carries its own nonce" 'grep -q "\"nonce\":\"fresh\"" "$AH_HOME/run/ahd.lock"'
lc_check "...and no daemon has been started under it" '[ ! -f "$AH_HOME/run/ahd.json" ] && [ "$(lc_daemons)" = 0 ]'
kill $SP 2>/dev/null; wait $SP 2>/dev/null
for p in $(lc_daemon_pids); do kill "$p" 2>/dev/null; done
sleep 0.5
rm -f "$AH_HOME/run/ahd.lock" "$AH_HOME/run/ahd.json" "$AH_HOME/run/ahd.sock"

# a stale lock (11 s old): taken over, and the daemon comes up
plant 11000 old
lc_ctl start >"$T/stale.json" 2>/dev/null
lc_check "a lock 11 s old is taken over: the start succeeds" 'grep -q "\"ok\":true" "$T/stale.json"'
lc_check "...a daemon is up, and the old nonce is gone from the lock path" 'lc_alive "$(lc_info_pid)" && ! grep -q "\"nonce\":\"old\"" "$AH_HOME/run/ahd.lock" 2>/dev/null'
lc_check "...no aside file of the takeover is left behind" '[ "$(ls "$AH_HOME/run" | grep -c "ahd.lock.stale")" = 0 ]'
lc_done
