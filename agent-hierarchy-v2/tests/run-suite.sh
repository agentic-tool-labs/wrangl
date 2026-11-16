#!/bin/bash
# agent-hierarchy — run every tests/test-*.sh and fail if any leaves a daemon behind.
#
# Every test file runs with AH_DAEMON=off, so no test starts a daemon by accident; only the daemon's own tests
# (test-ahd-*.sh) unset it, inside their own sandbox, and kill what they start. After the whole run, no ahd.mjs
# process may carry a --ah-home under a test sandbox.
# Usage: AHD_NATIVE_BIN=<ahd> bash tests/run-suite.sh [pattern]   (exits 0 iff every file passes and nothing leaked; 1 with no binary)

PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
. "$PLUGIN/tests/lib-daemon-leak.sh"
export AH_DAEMON=off
# What a run-all worker gives a test: the native binary under test and test mode. Without a binary the hooks a test runs would be
# refused as outside test mode and logged to the real hook-errors log, so nothing is run.
if [ -n "${AHD_NATIVE_BIN:-}" ] && [ -x "${AHD_NATIVE_BIN}" ]; then
  export AH_BIN="$AHD_NATIVE_BIN" AH_TEST=1
else
  echo "run-suite: no native binary (AHD_NATIVE_BIN unset or not executable): nothing run" >&2
  exit 1
fi
leak_mark_run
BASELINE=$(leak_candidates | tr '\n' ' ')
PATTERN="${1:-test-}"
FAILED=""; N=0
for t in "$PLUGIN"/tests/${PATTERN}*.sh; do
  n=$(basename "$t" .sh)
  N=$((N + 1))
  if timeout 300 bash "$t" > "${TMPDIR:-/tmp}/ah-suite-$n.log" 2>&1; then echo "ok   $n"; else echo "FAIL $n (log: ${TMPDIR:-/tmp}/ah-suite-$n.log)"; FAILED="$FAILED $n"; fi
done
LEAK=$(leaked_daemons $BASELINE)
if [ -n "$LEAK" ]; then echo "LEAK: daemons left running under a test sandbox:"; echo "$LEAK"; leak_attribution "$LEAK"; FAILED="$FAILED daemon-leak"; fi
leak_foreign_report $BASELINE
echo "$N files; failed:${FAILED:- none}"
[ -z "$FAILED" ]
