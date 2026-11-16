#!/bin/bash
# agent-hierarchy — ah2's TypeScript and JavaScript (mod/, tests/, scripts/) spell no ah v1 path, whole or in segments
# (`join(join(root, '.claude'), 'hierarchy')`). The scanner proves it can see them first: planted spellings must be flagged and
# clean lines must not be. The native sources have their own scan (Ah.Architecture.Tests HomeLayoutScanTests).
# Usage: bash tests/test-v1-path-scan.sh   (exits 0 iff the scanner's self-test and the scan of this plugin are clean)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SCAN="$PLUGIN/tests/contract-tools/scan-v1-paths.mjs"
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (${OUT:0:400})"; fi; }

OUT=$(node "$SCAN" --self-test 2>&1); RC=$?
check "the scanner flags every planted v1 spelling and none of the clean lines" '[ $RC = 0 ]'
OUT=$(node "$SCAN" "$PLUGIN" 2>&1); RC=$?
check "no TypeScript or JavaScript under the plugin spells a v1 path" '[ $RC = 0 ]'

echo "$PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
