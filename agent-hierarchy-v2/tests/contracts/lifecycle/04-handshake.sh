#!/bin/bash
# K5 scenario 4: the version handshake in ensureDaemon. A non-hook client newer than the daemon drains it, waits for it to
# go, and starts its own (the client's version is overridden under AH_TEST=1). A client of equal version leaves it running.
# The client here is `msg.mjs inbox`, a verb that reaches the daemon through ensureDaemon (ahd-ctl `start` reports a running
# daemon as it is and does not compare versions unless asked to upgrade).
. "$(dirname "$0")/_lib.sh"
lc_as_harness "$@"
lc_runtimes
lc_init
printf '{"team_id":"t-tm","orchestrator":{"pid":%s,"pid_start":"%s","session_id":"orch-tm"},"members":[{"role":"implementor","name":"tm-implementor","transport_id":"p1"}]}\n' "$$" "$(lc_start_of $$)" >"$PROJ/.claude/ah2/teams/tm.json"
lc_register_self orch-tm orchestrator tm "$PROJ/.claude/ah2/teams/tm.json"
inbox() { lc_msg inbox --address tm-implementor@tm; }

lc_ctl start >/dev/null 2>&1
OLD=$(lc_info_pid)
VER=$(lc_info_field version)
lc_check "a daemon is up" 'lc_alive "$OLD" && [ -n "$VER" ]'

# an equal version: nothing is drained
AHD_VERSION_OVERRIDE="$VER" inbox >"$T/equal.json" 2>/dev/null
lc_check "a client of the same version keeps the daemon: same pid, still alive" '[ "$(lc_info_pid)" = "$OLD" ] && lc_alive "$OLD"'

# a newer client drains the older daemon and a new one answers
AHD_VERSION_OVERRIDE="99.0.0" inbox >"$T/newer.json" 2>/dev/null
lc_wait_for 8 bash -c "! kill -0 $OLD 2>/dev/null"
NEW=$(lc_info_pid)
lc_check "a newer client drains the older daemon: the old pid is gone" '! lc_alive "$OLD"'
lc_check "...and a daemon of another pid, of the newer version, is up" '[ -n "$NEW" ] && [ "$NEW" != "$OLD" ] && lc_alive "$NEW" && [ "$(lc_info_field version)" = "99.0.0" ]'
lc_check "...and there is one daemon for this AH_HOME" '[ "$(lc_daemons)" = 1 ]'
lc_done
