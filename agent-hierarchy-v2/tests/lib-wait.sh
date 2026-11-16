#!/bin/bash
# agent-hierarchy — the one bounded wait for test suites: poll a condition every half second for at most 30 s.
#
#   . "$(dirname "$0")/lib-wait.sh"
#   waitfor '<shell condition>' <tries>
#
# A condition that does not come true within the tries (capped at 60, that is 30 s) stops the suite at once, because the
# cases after it would only wait on the same thing: it prints which condition it waited for and, when the suite names a
# log in WAITFOR_LOG, the last lines of the files that match it, then the suite's tally and exits 1. A suite that keeps
# counting its cases in PASS and FAIL gets them in that tally.

waitfor() {
  local _wf_i _wf_n=$2
  case "$_wf_n" in ''|*[!0-9]*) _wf_n=60;; esac
  [ "$_wf_n" -gt 60 ] && _wf_n=60
  for _wf_i in $(seq 1 "$_wf_n"); do eval "$1" && return 0; sleep 0.5; done
  echo "FAIL: waited $((_wf_n / 2)) s for: $1"
  if [ -n "${WAITFOR_LOG:-}" ]; then
    echo "---- the last lines of $WAITFOR_LOG"
    tail -n 20 $WAITFOR_LOG 2>/dev/null | cut -c1-240
  fi
  echo "----"
  echo "passed: ${PASS:-0}  failed: $(( ${FAIL:-0} + 1 ))"
  exit 1
}
