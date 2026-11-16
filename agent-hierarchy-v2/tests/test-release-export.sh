#!/bin/bash
# agent-hierarchy — scripts/release-export.sh at HEAD leaves out a path release-exclude.txt lists and keeps a
# carve-out (a ! line) inside an excluded directory. Skips outside a git work tree; writes only under a temp dir.
# Usage: bash tests/test-release-export.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
if [ "$(git -C "$PLUGIN" rev-parse --is-inside-work-tree 2>/dev/null)" != true ]; then
  echo "SKIP: $PLUGIN is not inside a git work tree (a git archive copy), so there is nothing to export"
  exit 0
fi
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name"; fi
}
T=$(mktemp -d "${TMPDIR:-/tmp}/ahprobe.XXXXXX")
[ -n "$T" ] || exit 1
hermetic_on_exit 'rm -rf "$T"'
E="$T/agent-hierarchy-v2"

bash "$PLUGIN/scripts/release-export.sh" HEAD "$T" >/dev/null 2>&1; RC=$?
check "the export exits 0" '[ "$RC" -eq 0 ]'
check "a kept file is exported" '[ -f "$E/README.md" ]'
check "an excluded directory is absent" '[ -z "$(ls -A "$E/docs/audits" 2>/dev/null)" ]'
check "a non-carve-out file in an excluded directory is absent" '[ ! -e "$E/bench/probe-gate-chain.sh" ]'
check "a carve-out inside an excluded directory is present" '[ -f "$E/bench/ah-call-latency.sh" ]'
check "docs/progress.md stays public" '[ -f "$E/docs/progress.md" ]'

echo "----"
echo "SUMMARY: $PASS passed, $FAIL failed"
[ $FAIL -eq 0 ]
