#!/bin/bash
# agent-hierarchy — a test never talks to the launching shell's state home, and every sandbox that runs the native binary or the
# shims is one the test-mode predicate accepts (/tmp/ahprobe.<alphanumerics>): outside test mode the binary honours neither HOME
# nor AH_HOME and writes its refusals to the real home's hook-errors log.
#   - tests/lib-hermetic.sh drops an ambient AH_HOME / WRANGL_HIERARCHY_DIR that is not inside an ahprobe sandbox (and one with a
#     `..` component), and keeps one that is;
#   - no test makes its sandbox under the hermetic helper directory (/tmp/ahtt.*) while turning test mode on, or names it
#     ahprobe.<word>.<id> (a dot in the tail fails the predicate);
#   - tests/test-native-frontend.sh, the test that leaked, makes its sandbox /tmp/ahprobe.XXXXXX and checks the real log.
# Usage: bash tests/test-hermetic-ambient.sh   (exits 0 iff every row passes)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
LIB="$PLUGIN/tests/lib-hermetic.sh"
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:200})"; fi; }

# What a test sees of VAR after the preamble, when its caller exported VAR=value (a clean environment otherwise).
seen() {
  env -i PATH="$PATH" HOME="${HOME:-/}" TMPDIR=/tmp "$1=$2" bash -c '. "$0"; v=$1; eval "printf %s \"\${$v-<unset>}\""' "$LIB" "$1"
}

# ---- the ambient state home
OUT=$(seen AH_HOME /Users/someone/.claude/ah2)
check "a hostile ambient AH_HOME is dropped" '[ "$OUT" = "<unset>" ]'
OUT=$(seen WRANGL_HIERARCHY_DIR /Users/someone/proj/.claude/ah2)
check "a hostile ambient WRANGL_HIERARCHY_DIR is dropped" '[ "$OUT" = "<unset>" ]'
OUT=$(seen AH_HOME relative/ah)
check "a relative ambient AH_HOME is dropped" '[ "$OUT" = "<unset>" ]'
OUT=$(seen AH_HOME /tmp/ahprobe.AbC123/ah)
check "an AH_HOME inside /tmp/ahprobe.<id> is kept" '[ "$OUT" = "/tmp/ahprobe.AbC123/ah" ]'
OUT=$(seen AH_HOME /private/tmp/ahprobe.AbC123/t001/ah)
check "...and one deeper under /private/tmp/ahprobe.<id> (run-all's shape) is kept" '[ "$OUT" = "/private/tmp/ahprobe.AbC123/t001/ah" ]'
OUT=$(seen WRANGL_HIERARCHY_DIR /tmp/ahprobe.AbC123/proj/.claude/ah2)
check "...and an WRANGL_HIERARCHY_DIR there is kept" '[ "$OUT" = "/tmp/ahprobe.AbC123/proj/.claude/ah2" ]'
OUT=$(seen AH_HOME /tmp/ahprobe.AbC123/../../Users/someone/.claude/ah2)
check "a path that climbs out of the sandbox with .. is dropped" '[ "$OUT" = "<unset>" ]'
OUT=$(seen AH_HOME /tmp/ahprobe.AbC123/ah/..)
check "...also with a trailing .." '[ "$OUT" = "<unset>" ]'
OUT=$(seen AH_HOME /home/u/tmp/ahprobe.AbC123/ah)
check "a path that merely contains an ahprobe name below another directory is dropped" '[ "$OUT" = "<unset>" ]'
OUT=$(seen AH_HOME /tmp/ahprobe/ah)
check "a directory named ahprobe with no id is dropped" '[ "$OUT" = "<unset>" ]'
OUT=$(seen AH_HOME /tmp/ahprobe./ah)
check "a directory named ahprobe. with an empty id is dropped" '[ "$OUT" = "<unset>" ]'

# ---- no sandbox the predicate would refuse, in a test that turns test mode on
OUT=$(cd "$PLUGIN/tests" && grep -l 'AH_TEST=1' test-*.sh | grep -v '^test-hermetic-ambient.sh$' | while read -r f; do
  if grep -E 'mktemp -d .*\$\{?_AH_HERMETIC_DIR|mktemp -d .*/ahtt\.' "$f" >/dev/null; then echo "$f"; fi
done)
check "no test that sets AH_TEST=1 makes its sandbox under the hermetic helper directory" '[ -z "$OUT" ]'
OUT=$(cd "$PLUGIN/tests" && grep -nE 'mktemp -d [^ ]*/ahprobe\.[A-Za-z0-9_-]+\.X{6}' test-*.sh lib-*.sh | grep -v 'test-hermetic-ambient.sh' | head -5)
check "no sandbox template puts a word and a dot between ahprobe. and the id (the predicate takes alphanumerics only)" '[ -z "$OUT" ]'
OUT=$(grep -c 'mktemp -d /private/tmp/ahprobe.XXXXXX' "$PLUGIN/tests/test-codex-appserver-spawn.sh")
check "the codex app-server spawn test names its sandbox ahprobe.<id>" '[ "$OUT" = 1 ]'

# ---- the test that leaked
F="$PLUGIN/tests/test-native-frontend.sh"
OUT=$(grep -n 'T="$(mktemp -d ' "$F")
check "test-native-frontend.sh makes its sandbox /tmp/ahprobe.XXXXXX" 'printf %s "$OUT" | grep -q "mktemp -d /tmp/ahprobe.XXXXXX"'
OUT=$(grep -c '_AH_HERMETIC_DIR' "$F")
check "...and does not derive it from the hermetic helper directory" '[ "$OUT" = 0 ]'
OUT=$(grep -c 'ah_test_mode' "$F")
check "...and checks that its sandbox is a test-mode sandbox" '[ "$OUT" -ge 1 ]'
OUT=$(grep -cF -e '${_AH_REAL_LOG:-}' -e '"$T" "$_AH_REAL_LOG"' "$F")
check "...and checks the real hook-errors log for rows naming it" '[ "$OUT" -ge 2 ]'

echo
echo "RESULT: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
