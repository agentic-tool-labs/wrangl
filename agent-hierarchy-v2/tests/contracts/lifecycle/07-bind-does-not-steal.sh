#!/bin/bash
# K5 scenario 7: the bind never steals. (a) A second daemon started directly while one is live, outside the lock, fails to
# bind and exits; the first still answers /v1/health and its socket inode is unchanged. (b) A stale socket file with no
# lock held and no live owner: since b289 the daemon replaces it (nothing listens on it) and serves; a client `start` then uses it.
. "$(dirname "$0")/_lib.sh"
lc_runtimes
lc_init

lc_ctl start >/dev/null 2>&1
A=$(lc_info_pid)
INODE=$(ls -i "$AH_HOME/run/ahd.sock" | awk '{print $1}')
lc_check "daemon A is up and answers health" 'lc_alive "$A" && lc_health | grep -q "\"pid\":$A"'

lc_direct_daemon >"$T/second.out" 2>"$T/second.err" &
B=$!
lc_wait_for 5 bash -c "! kill -0 $B 2>/dev/null"
lc_check "(a) a second daemon started directly exits (it cannot bind)" '! lc_alive "$B"'
lc_check "(a) the first daemon still answers health with its own pid" 'lc_health | grep -q "\"pid\":$A"'
lc_check "(a) its socket inode is unchanged" '[ "$(ls -i "$AH_HOME/run/ahd.sock" | awk "{print \$1}")" = "$INODE" ]'
lc_check "(a) and ahd.json still names the first daemon" '[ "$(lc_info_pid)" = "$A" ]'

# (b) kill the daemon hard: the socket file stays; no lock is held
kill -9 "$A" 2>/dev/null
sleep 0.5
lc_check "(b) after a hard kill the socket file is still there" '[ -S "$AH_HOME/run/ahd.sock" ]'
rm -f "$AH_HOME/run/ahd.json" "$AH_HOME/run/ahd.lock"
lc_direct_daemon >"$T/third.out" 2>"$T/third.err" &
C=$!
LC_PIDS="$LC_PIDS $C"
# $C is the background subshell; the daemon is its child, named by the ahd.json it writes
c_answers() { local p; p=$(lc_info_pid); [ -n "$p" ] && [ "$p" != "$A" ] && lc_health 2>/dev/null | grep -q "\"pid\":$p"; }
lc_wait_for 5 c_answers
CP=$(lc_info_pid)
lc_check "(b) a daemon started directly over the stale socket (nothing listens, no live owner) replaces it and answers (b289)" 'c_answers && lc_alive "$CP"'
lc_ctl start >"$T/client.json" 2>/dev/null
lc_check "(b) a client start then finds that daemon running and starts no other" 'grep -q "\"ok\":true" "$T/client.json" && lc_health | grep -q "\"pid\":$CP" && [ "$(lc_daemons)" = 1 ]'
lc_done
