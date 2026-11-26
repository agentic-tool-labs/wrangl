#!/bin/bash
# agent-hierarchy — tests/run-set.sh --list-sets: prints the set names, one per line, and nothing else; any other use of the
# word is not the listing.
# Usage: bash tests/test-run-set-list.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
RUNSET="$PLUGIN/tests/run-set.sh"
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:300})"; fi; }

OUT=$(bash "$RUNSET" --list-sets 2>/dev/null); RC=$?
check "--list-sets exits 0" '[ "$RC" = 0 ]'
check "--list-sets prints exactly dotnet shell full affected list, one per line" '[ "$OUT" = "$(printf "dotnet\nshell\nfull\naffected\nlist")" ]'

DECLARED=$(sed -n 's/^SETS="\(.*\)"$/\1/p' "$RUNSET" | tr ' ' '\n')
OUT=$(bash "$RUNSET" --list-sets 2>/dev/null)
check "--list-sets equals the SETS= line of run-set.sh" '[ -n "$DECLARED" ] && [ "$OUT" = "$DECLARED" ]'

# nearest non-matches: a longer option, and the option after the positionals, are not the listing
OUT=$(bash "$RUNSET" --list-setsx 2>&1); RC=$?
check "--list-setsx is an unknown option (exit 2, no set list)" '[ "$RC" = 2 ] && ! printf "%s" "$OUT" | grep -qx "shell"'
OUT=$(bash "$RUNSET" /nonexistent 0000000000000000000000000000000000000000 dotnet --list-sets 2>&1); RC=$?
check "--list-sets after the positionals is not the listing" '[ "$RC" = 2 ] && ! printf "%s" "$OUT" | grep -qx "shell"'

echo "passed=$PASS failed=$FAIL"
[ "$FAIL" = 0 ]
