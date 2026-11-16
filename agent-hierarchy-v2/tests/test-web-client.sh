#!/bin/bash
# agent-hierarchy — the browser client's tests (tests/web/*.test.mjs, run by node's own runner; no npm): the card
# goldens from the normative detail fixtures, the grid layout at each width, the XSS and banned-API checks, and the
# events-triggered detail re-read. AH_WEB_GOLDEN=update bash tests/web/run.sh rewrites the goldens.
# Usage: bash tests/test-web-client.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (OUT=${OUT:0:400})"; fi
}

OUT=$(sh "$PLUGIN/tests/web/run.sh" 2>&1); RC=$?
# Each failing test by name, before the checks: their OUT is cut at 400 chars, and run-all groups on a log's first FAIL line.
NAMES=$(printf '%s\n' "$OUT" | sed -n '/^✖ failing tests:/,$s/^✖ \(.*\) ([0-9.]*ms)$/FAIL: web test: \1/p')
[ -n "$NAMES" ] && printf '%s\n' "$NAMES"
# No names from a failed run means the runner itself died, so its last lines are the only evidence.
[ "$RC" -ne 0 ] && [ -z "$NAMES" ] && printf '%s\n' "$OUT" | tail -20
check "the web client's node tests exit 0" '[ "$RC" -eq 0 ]'
check "they ran, and none failed" 'printf "%s" "$OUT" | grep -q "^ℹ fail 0$" && printf "%s" "$OUT" | grep -Eq "^ℹ pass [1-9][0-9]*$"'

echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
