#!/bin/bash
# agent-hierarchy — tests/run-set.sh's result store: the default is the persistent per-user root under $HOME, AH_RESULTS_DIR is accepted
# only under that root or /private/tmp/ahprobe.*, entries older than 30 days are pruned inside the store only, the old /private/tmp
# store is copied in once without overwriting, and clones stay under /private/tmp/ahprobe.*. HOME is a directory of this test's own.
# Usage: bash tests/test-run-set-store.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
unset AH_TEST_SLOT_HELD AH_RESULTS_DIR AH_LEGACY_RESULTS_DIR
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
RUNSET="${AH_RUNSET_UNDER_TEST:-$PLUGIN/tests/run-set.sh}"
PFX="$(git -C "$PLUGIN" rev-parse --show-prefix)"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
OUTER="$(mktemp -d)"; case "$OUTER" in /private/tmp/ahprobe.*) echo "HOME dir must be outside /private/tmp/ahprobe.*"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB" "$OUTER"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:700})"; fi; }

H="$OUTER/home"; ROOT="$H/.claude/ah2/qa-results"; LEG="$SB/legacy"
export AH_TEST_SLOT_WAIT=60 AH_SLOTS_DIR="$SB/slots" AH_TEST_PWDFILE="$SB/pwd.out"
REPO="$SB/repo"
GIT() { git -C "$REPO" -c user.name=t -c user.email=t@t "$@"; }
mkdir -p "$REPO/${PFX}scripts" "$H" && GIT init -q
printf '#!/bin/bash\npwd > "$AH_TEST_PWDFILE"\nexit 1\n' > "$REPO/${PFX}scripts/dotnet-test.sh"
GIT add -A >/dev/null; GIT commit -q -m stub
SHA="$(GIT rev-parse HEAD)"
rs() { OUT="$(env -u AH_RESULTS_DIR HOME="$H" AH_LEGACY_RESULTS_DIR="$LEG" "$@" 2>&1)"; RC=$?; }
seed() { # <store> <sha> <set> <status>
  mkdir -p "$1/$2/$3"; printf '{"status":"%s"}\n' "$4" > "$1/$2/$3/result.json"
}

# 1. the default store is under $HOME, and a result there is reused
seed "$ROOT" "$SHA" dotnet PASS
rs bash "$RUNSET" "$REPO" "$SHA" dotnet
check "a stored result under the default root is reused for the same sha and set" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "cached=yes" && printf %s "$OUT" | grep -q "result=.*/home/.claude/ah2/qa-results/$SHA/dotnet/result.json"'
rs bash "$RUNSET" "$REPO" "$SHA" shell
check "...another set of the same sha is not answered from it (nearest non-match)" '! printf %s "$OUT" | grep -q "cached=yes"'

# 2. AH_RESULTS_DIR is accepted only under the root or /private/tmp/ahprobe.*
rs env AH_RESULTS_DIR="$ROOT/x" bash "$RUNSET" "$REPO" "$SHA" dotnet
check "AH_RESULTS_DIR under the persistent root is accepted" '[ $RC != 2 ]'
rs env AH_RESULTS_DIR="$SB/t/res" bash "$RUNSET" "$REPO" "$SHA" dotnet
check "AH_RESULTS_DIR under /private/tmp/ahprobe.* is accepted" '[ $RC != 2 ]'
rs env AH_RESULTS_DIR=/tmp/x bash "$RUNSET" "$REPO" "$SHA" dotnet
check "AH_RESULTS_DIR=/tmp/x is refused (exit 2)" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "must be under"'
rs env AH_RESULTS_DIR="$H/.claude/ah2/qa-results-evil" bash "$RUNSET" "$REPO" "$SHA" dotnet
check "a sibling that only shares the root's name as a prefix is refused" '[ $RC = 2 ]'
rs env AH_RESULTS_DIR="$H/.claude/ah2" bash "$RUNSET" "$REPO" "$SHA" dotnet
check "the root's parent is refused" '[ $RC = 2 ]'
rs env AH_RESULTS_DIR=/private/tmp/ahprobex/res bash "$RUNSET" "$REPO" "$SHA" dotnet
check "/private/tmp/ahprobex (no dot) is refused" '[ $RC = 2 ]'
mkdir -p "$H/.claude/ah2/elsewhere"; ln -s "$H/.claude/ah2/elsewhere" "$ROOT/link"
rs env AH_RESULTS_DIR="$ROOT/link" bash "$RUNSET" "$REPO" "$SHA" dotnet
check "a link under the root that leads outside it is refused" '[ $RC = 2 ]'

# 3. pruning: only entries older than 30 days, only inside the store
P="$OUTER/prune-home"; PR="$P/.claude/ah2/qa-results"; mkdir -p "$P/.claude/ah2/other/aaa/bbb"
seed "$PR" "$(printf 'a%.0s' $(seq 40))" dotnet PASS
seed "$PR" "$(printf 'b%.0s' $(seq 40))" dotnet PASS
seed "$PR" "$(printf 'c%.0s' $(seq 40))" dotnet PASS
OLD="$PR/$(printf 'a%.0s' $(seq 40))/dotnet"; NEW="$PR/$(printf 'b%.0s' $(seq 40))/dotnet"; LOCKED="$PR/$(printf 'c%.0s' $(seq 40))/dotnet"
mkdir -p "$LOCKED/.lock" "$PR/errors"; : > "$PR/errors/keep.json"
touch -t 202001010000 "$OLD" "$LOCKED" "$P/.claude/ah2/other/aaa/bbb"
OUT="$(env -u AH_RESULTS_DIR HOME="$P" AH_LEGACY_RESULTS_DIR="$LEG" bash "$RUNSET" "$REPO" "$SHA" dotnet 2>&1)"; RC=$?
check "an entry untouched for more than 30 days is removed" '[ ! -e "$OLD" ]'
check "...an entry from today is kept (nearest non-match)" '[ -f "$NEW/result.json" ]'
check "...an old entry that holds a lock is kept" '[ -d "$LOCKED" ]'
check "...a directory outside the store is never touched" '[ -d "$P/.claude/ah2/other/aaa/bbb" ]'
check "...the errors directory is kept" '[ -f "$PR/errors/keep.json" ]'

# 4. the old store is copied in once, nothing overwritten, nothing deleted from it
M="$OUTER/mig-home"; MR="$M/.claude/ah2/qa-results"
seed "$LEG" "$SHA" dotnet PASS
seed "$LEG" "$(printf 'd%.0s' $(seq 40))" dotnet PASS
seed "$MR" "$(printf 'd%.0s' $(seq 40))" dotnet FAIL
OUT="$(env -u AH_RESULTS_DIR HOME="$M" AH_LEGACY_RESULTS_DIR="$LEG" bash "$RUNSET" "$REPO" "$SHA" dotnet 2>&1)"; RC=$?
check "an entry of the old store is copied across and answers the request" '[ $RC = 0 ] && [ -f "$MR/$SHA/dotnet/result.json" ] && printf %s "$OUT" | grep -q "cached=yes"'
check "...an entry the new store already holds is not overwritten" 'grep -q FAIL "$MR/$(printf "d%.0s" $(seq 40))/dotnet/result.json"'
check "...nothing is deleted from the old store" '[ -f "$LEG/$SHA/dotnet/result.json" ]'
M2="$OUTER/mig2-home"
OUT="$(env HOME="$M2" AH_LEGACY_RESULTS_DIR="$LEG" AH_RESULTS_DIR="$SB/mig2-res" bash "$RUNSET" "$REPO" "$SHA" dotnet 2>&1)"
check "an explicit AH_RESULTS_DIR is not filled from the old store (nearest non-match)" '[ ! -e "$SB/mig2-res/$SHA/dotnet/result.json" ]'

# 5. clones stay under /private/tmp/ahprobe.*, and a run that ends in ERROR leaves no lock or result in the persistent store
E="$OUTER/err-home"; rm -f "$AH_TEST_PWDFILE"
OUT="$(env -u AH_RESULTS_DIR HOME="$E" AH_LEGACY_RESULTS_DIR="$SB/none" bash "$RUNSET" "$REPO" "$SHA" dotnet 2>&1)"; RC=$?
check "the clone a set runs in is made under /private/tmp/ahprobe.*" 'case "$(cat "$AH_TEST_PWDFILE" 2>/dev/null)" in /private/tmp/ahprobe.??????/*|/private/tmp/ahprobe.??????) true;; *) false;; esac'
check "...a run that ends in ERROR (exit 3) leaves no lock and no stored result under HOME" '[ $RC = 3 ] && [ ! -e "$E/.claude/ah2/qa-results/$SHA/dotnet/.lock" ] && [ ! -e "$E/.claude/ah2/qa-results/$SHA/dotnet/result.json" ]'

# 6. a base stored in the old root is found, whichever store the run uses
echo second > "$REPO/${PFX}marker"; GIT add -A >/dev/null; GIT commit -q -m second; HEAD2="$(GIT rev-parse HEAD)"
B1="$SB/base-res"; L1="$SB/base-legacy"; seed "$B1" "$HEAD2" dotnet PASS; seed "$L1" "$SHA" dotnet PASS
OUT="$(env AH_RESULTS_DIR="$B1" HOME="$H" AH_LEGACY_RESULTS_DIR="$L1" bash "$RUNSET" "$REPO" "$HEAD2" dotnet --base "$SHA" 2>&1)"; RC=$?
check "a base that only the old root holds is found: it is copied in and not run" '[ -f "$B1/$SHA/dotnet/result.json" ] && ! printf %s "$OUT" | grep -q "no stored result"'
check "...the old root keeps its copy" '[ -f "$L1/$SHA/dotnet/result.json" ]'
B2="$SB/base-res2"; seed "$B2" "$HEAD2" dotnet PASS
OUT="$(env AH_RESULTS_DIR="$B2" HOME="$H" AH_LEGACY_RESULTS_DIR="$SB/base-none" bash "$RUNSET" "$REPO" "$HEAD2" dotnet --base "$SHA" 2>&1)"; RC=$?
check "a base that neither root holds is still run (nearest non-match)" 'printf %s "$OUT" | grep -q "no stored result, running it"'

echo "$PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
