#!/bin/bash
# K5 scenario 1: two clients race to start the daemon. One daemon results, and both clients discover the same one.
# Negative: with no start at all there is no daemon (the count is not 1 by accident).
. "$(dirname "$0")/_lib.sh"
lc_runtimes
lc_init

lc_check "no daemon before any client starts one" '[ "$(lc_daemons)" = 0 ] && [ ! -f "$AH_HOME/run/ahd.json" ]'
lc_ctl start >"$T/a.json" 2>"$T/a.err" &
PA=$!
lc_ctl start >"$T/b.json" 2>"$T/b.err" &
PB=$!
wait $PA; wait $PB
DAEMON=$(lc_info_pid)
lc_check "both clients finished with a started or running daemon" 'grep -q "\"ok\":true" "$T/a.json" && grep -q "\"ok\":true" "$T/b.json"'
lc_check "exactly one daemon process for this AH_HOME" '[ "$(lc_daemons)" = 1 ]'
lc_check "run/ahd.json names a live daemon" 'lc_alive "$DAEMON"'
PIDS=$(cat "$T/a.json" "$T/b.json" | grep -o '"pid":[0-9]*' | sort -u | tr '\n' ' ')
lc_check "both clients report the same daemon pid" '[ "$PIDS" = "\"pid\":$DAEMON " ]'
lc_check "a later client discovers it too (status reports the same pid)" 'lc_ctl status 2>/dev/null | grep -q "\"pid\":$DAEMON"'
lc_done
