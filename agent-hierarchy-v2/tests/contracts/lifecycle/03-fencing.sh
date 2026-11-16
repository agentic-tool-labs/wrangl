#!/bin/bash
# K5 scenario 3: fencing. A daemon checks every second that run/ahd.json names its pid and that its socket is the one it
# bound. When ahd.json names another pid it exits, removes nothing, and the socket file is left in place.
# Negative: while ahd.json names it, the daemon stays up for the same time.
. "$(dirname "$0")/_lib.sh"
lc_runtimes
lc_init

lc_ctl start >/dev/null 2>&1
D=$(lc_info_pid)
lc_check "a daemon is up" 'lc_alive "$D"'
sleep 3
lc_check "after 3 s with a matching ahd.json it is still running (the fence does not fire by itself)" 'lc_alive "$D"'

cp "$AH_HOME/run/ahd.json" "$T/ahd.json.orig"
node -e '
const fs=require("fs"); const f=process.argv[1]; const j=JSON.parse(fs.readFileSync(f,"utf8")); j.pid = 999999; fs.writeFileSync(f, JSON.stringify(j)+"\n");' "$AH_HOME/run/ahd.json"
lc_wait_for 5 bash -c "! kill -0 $D 2>/dev/null"
lc_check "with ahd.json naming another pid the daemon exits within 5 s" '! lc_alive "$D"'
lc_check "...the socket file is left in place (a fenced daemon removes nothing)" '[ -S "$AH_HOME/run/ahd.sock" ]'
lc_check "...and ahd.json is the other pid's, untouched" 'grep -q "\"pid\":999999" "$AH_HOME/run/ahd.json"'
lc_done
