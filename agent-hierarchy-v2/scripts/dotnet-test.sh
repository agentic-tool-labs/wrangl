#!/bin/bash
# agent-hierarchy — build and test native/Ah.sln while holding tokens of the machine-wide bucket of tests/with-slot.sh, so that
# however many sessions start a run at once, the runs together never need more than AH_TEST_TOKENS CPUs.
#
#   scripts/dotnet-test.sh [extra dotnet test arguments...]
#
#   AH_TEST_TOKENS, AH_TEST_SLOTS, AH_TEST_SLOT_WAIT, AH_SLOTS_DIR   as for tests/with-slot.sh (AH_TEST_SLOTS=0 turns the limiter off).
#   AH_DOTNET_TEST_PROJECTS   csproj paths, space-separated: a whole-solution run still builds the solution, then tests only these
#                             (run-set.sh's affected set). The exit is xargs's: 0 only when every project passed.
#
# A whole-solution run asks for ah_bucket_heavy (tests/lib-tokens.sh) tokens and a targeted run for 2; inside a grant that is already held (a
# caller's with-slot.sh) the caller's grant is used. The build and a targeted test run use `-m:<grant>` and xUnit's MaxParallelThreads
# set to the grant. A whole-solution test run tests -m assemblies at once with MaxParallelThreads each, so those two multiply: it uses
# `-m:<grant / 4>` (at least 1) and the rest as threads, so the run needs about the grant in CPUs and the largest assembly, which sets the
# run's length, gets more than 2 threads. A grant of 8 is 2 assemblies at 4 threads, a grant of 12 is 3 at 4.
# The whole-solution run is declared `--eta large` to the bucket, so it is killed at 3 x 20 min, and every `dotnet test` here carries
# `--blame-hang-timeout 20m` (a test host that hangs longer dumps and dies on its own first) unless the caller passes its own.
#
# Prints how long the run waited for its tokens. A targeted run, one that names a --filter, a project, a solution or a directory
# of its own, is declared `--eta medium`; when it names no project, solution or directory, the solution is added so that dotnet
# has something to test. A run with a -c/--configuration builds in it as well as tests in it.
# The exit code is the build's, or else the test run's.

set -u
. "$(dirname "$0")/lib-tree-root.sh"
HERE="$(ah_tree_root "$(dirname "$0")")"
SLN="$HERE/../native/Ah.sln"
SLOT="$HERE/../tests/with-slot.sh"
. "$HERE/../tests/lib-tokens.sh"
HEAVY=$(ah_bucket_heavy); unset AH_TEST_HEAVY_SPLIT

filtered=0
has_target=0
has_blame=0
config=""
prev=""
ARGS=()
for a in "$@"; do
  case "$prev" in
    -c|--configuration) config="$a" ;;
    --blame-hang-timeout) has_blame=1 ;;
  esac
  case "$prev" in
    # An option's value is never a target, even when it names a directory.
    -c|--configuration|-r|--results-directory|-o|--output|-s|--settings|-l|--logger|-a|--test-adapter-path|-f|--framework|--filter|--runtime|--arch|--os|-d|--diag|-v|--verbosity|-p|--property|--blame-hang-timeout|--blame-crash-collect-always)
      prev=""
      ARGS+=("$a")
      continue ;;
  esac
  case "$a" in
    --filter|--filter=*) filtered=1 ;;
    --blame-hang-timeout=*) has_blame=1 ;;
    --configuration=*) config="${a#--configuration=}" ;;
    -*) ;;
    *.csproj|*.sln|*.slnx) has_target=1; [ -f "$a" ] && a="$(ah_tree_root "$(dirname "$a")")/$(basename "$a")" ;;
    *) if [ -d "$a" ]; then has_target=1; a="$(ah_tree_root "$a")"; fi ;;
  esac
  ARGS+=("$a")
  prev="$a"
done

BLAME=()
[ "$has_blame" = 1 ] || BLAME=(--blame-hang-timeout 20m)

# Runs inside the grant. $1 is solution or targeted, $2 the tokens to ask for (used when no caller's grant is held), then the
# arguments. A caller that passes its own `--` (runner arguments) keeps them, and the xUnit thread cap follows them.
INNER='
mode=$1; dflt=$2; shift 2
k=${AH_TEST_TOKENS_GRANTED:-$dflt}
threads=$k; assemblies=$k
if [ "$mode" = solution ] && [ "$k" -ge 2 ]; then assemblies=$((k / 4)); [ "$assemblies" -ge 1 ] || assemblies=1; threads=$((k / assemblies)); fi
mflag=(-m:$k)
testm=(-m:$assemblies)
xcap=(-- xUnit.MaxParallelThreads=$threads)
for a; do [ "$a" = -- ] && xcap=(xUnit.MaxParallelThreads=$threads); done
# with the limiter off nothing is granted and nothing is capped
if [ -z "${AH_TEST_TOKENS_GRANTED:-}" ] && [ -n "${AH_DOTNET_TEST_UNBOUNDED:-}" ]; then mflag=(); testm=(); xcap=(); fi
if [ "$mode" = solution ]; then
  echo "dotnet-test: waited $(( $(date +%s) - AH_DOTNET_TEST_QUEUED_AT )) s for $k test tokens" >&2
  sln=$1; shift
  dotnet build "$sln" -warnaserror ${mflag[@]+"${mflag[@]}"} ${AH_DOTNET_TEST_CONFIG:+-c "$AH_DOTNET_TEST_CONFIG"} || exit $?
  if [ -n "${AH_DOTNET_TEST_PROJECTS:-}" ]; then
    printf "%s\n" $AH_DOTNET_TEST_PROJECTS | xargs -P "$assemblies" -I{} dotnet test {} --no-build ${AH_DOTNET_TEST_BLAME:+--blame-hang-timeout "$AH_DOTNET_TEST_BLAME"} "$@" ${xcap[@]+"${xcap[@]}"}
    exit $?
  fi
  exec dotnet test "$sln" --no-build ${AH_DOTNET_TEST_BLAME:+--blame-hang-timeout "$AH_DOTNET_TEST_BLAME"} ${testm[@]+"${testm[@]}"} "$@" ${xcap[@]+"${xcap[@]}"}
fi
exec dotnet test ${AH_DOTNET_TEST_BLAME:+--blame-hang-timeout "$AH_DOTNET_TEST_BLAME"} ${mflag[@]+"${mflag[@]}"} "$@" ${xcap[@]+"${xcap[@]}"}
'

export AH_DOTNET_TEST_QUEUED_AT="$(date +%s)"
if [ "$(ah_bucket_capacity)" = 0 ]; then export AH_DOTNET_TEST_UNBOUNDED=1; else unset AH_DOTNET_TEST_UNBOUNDED; fi
export AH_DOTNET_TEST_CONFIG="$config"
export AH_DOTNET_TEST_BLAME="${BLAME[1]:-}"
if [ "$filtered" = 1 ] || [ "$has_target" = 1 ]; then
  [ "$has_target" = 1 ] || ARGS=("$SLN" ${ARGS[@]+"${ARGS[@]}"})
  exec bash "$SLOT" --eta medium --tokens 2 bash -c "$INNER" _ targeted 2 ${ARGS[@]+"${ARGS[@]}"}
fi
exec bash "$SLOT" --eta large --tokens "$HEAVY" bash -c "$INNER" _ solution "$HEAVY" "$SLN" ${ARGS[@]+"${ARGS[@]}"}
