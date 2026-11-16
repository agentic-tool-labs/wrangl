#!/bin/bash
# agent-hierarchy — suites do not depend on the checkout's path. A suite that greps emitted text for a word must
# not match a word that is only part of where the checkout lives (a worktree named for "pane" tripped one). A copy of
# the plugin under a directory named for such words runs the suites that grep for them, and each must pass.
# Usage: bash tests/test-path-independence.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SB="$(mktemp -d /tmp/ahprobe.XXXXXX)"
[ -n "$SB" ] && [ -d "$SB" ] || { echo "mktemp failed"; exit 1; }
SB="$(cd "$SB" && pwd -P)"
hermetic_on_exit 'case "$SB" in /tmp/ahprobe.??????|/private/tmp/ahprobe.??????) rm -rf "$SB" ;; esac'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (RC=$RC OUT=${OUT:0:300})"; fi; }

COPY="$SB/checkout-pane-relay/agent-hierarchy"
mkdir -p "$COPY"
# the plugin without its build output and history: the suites below read the native source and run the shims, not a published tree
(cd "$PLUGIN" && tar --exclude=./native/artifacts --exclude=./native/obj --exclude=./.git --exclude=./node_modules -cf - .) | (cd "$COPY" && tar -xf -)
check "the copy's path holds the words pane and relay" '[[ "$COPY" == *pane* && "$COPY" == *relay* ]]'
for s in test-peer-pane-attribution test-ah-cli; do
  OUT=$(cd "$COPY" && bash "tests/$s.sh" 2>&1); RC=$?
  OUT=$(printf '%s' "$OUT" | grep -E '^FAIL' | head -3)
  check "$s passes from a checkout whose path holds pane and relay" '[ "$RC" = 0 ] && [ -z "$OUT" ]'
done

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
