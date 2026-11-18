#!/usr/bin/env bash
# agent-hierarchy — run every tests/test-*.sh with bounded parallelism, then the tests that cannot share the machine
# one at a time. Exits 0 iff every file passes and no daemon of a test sandbox is left running.
#
# Usage: bash tests/run-all.sh [-j N] [-t SECONDS] [-k] [-o FILE] [--impl I] [--lane L] [--with-serial] [--changed BASE]
#                              [--first FILE] [--fail-fast] [--no-abort] [--tap DIR] [--exact NAME] [--require-native] [PREFIX]
#   -j N        parallel workers (default: min(2 ncpu / 3, 12)); -j 1 runs everything one at a time
#   -t SECONDS  per-test timeout (default 300)
#   -k          keep the run directory (logs and sandboxes) even when everything passed
#   -o FILE     write the result set there, one "<name> ok|FAIL|TIMEOUT" line per test, sorted by name; two runs
#               of the same tree agree exactly when their files are identical
#   PREFIX      only tests whose file name starts with test-PREFIX (default: all)
#   --impl native
#               the only implementation (and the default), exported to each test as AH_IMPL; node and manifest are usage errors
#   --lane fast|full|all
#               select by the lane in tests/manifest.json (default all: today's set). A serial-lane suite is
#               excluded from fast and full unless --with-serial is given, and then runs in the serial lane
#   --changed BASE
#               select the suites whose manifest `covers` globs match a path in `git diff --name-only BASE...HEAD`;
#               an empty diff selects nothing (prints "run-all: 0/0 passed", exit 0)
#   --only FILE
#               only the suites FILE names, one test-<name> per line (the shell part of run-set.sh's affected set)
#   --first FILE
#               start the FAIL and TIMEOUT names listed in FILE (the -o format) first, in that order
#   --fail-fast after the first FAIL or TIMEOUT no further test starts (the running ones finish); exit 1. Refused (exit 2)
#               for a full-lane run: no --lane and no --changed
#   --no-abort  turn off the early abort. By default, once AH_ABORT_MIN (default 20) tests have finished, if more than AH_ABORT_PCT
#               (default 50) percent of the finished ones failed or timed out, no new test starts, the running ones finish or
#               time out, and the run exits 1 with an "aborted: systemic" line naming the failures grouped by their first
#               FAIL/error line (paths and numbers stripped), a count per group and up to 3 names each
#   --tap DIR   export AH_PARITY_TAP=DIR/<test-name> into each test
#   --exact NAME
#               run exactly tests/test-NAME.sh and nothing whose name it prefixes (PREFIX test-a also runs test-a-b.sh;
#               --exact a does not). NAME is a test name without test- and .sh. Refused (exit 2) together with a PREFIX
#               or a NAME that holds a glob character or a slash
#   --require-native
#               accepted and always on: a run with no native binary exits 1 and runs nothing. The binary is $AHD_NATIVE_BIN;
#               without one every suite is SKIPPED (no-native-binary) and the run fails. With one, AH_BIN names it and AH_TEST=1
#               is exported
#   A run takes the machine lock as it always did. With --lane or --changed it takes no lock when the selection holds no
#   serial-lane suite, unless AH_RUN_ALL_LOCK_FAST=1.
#
# What a test gets. Its own directory <run>/t<NNN>/ with home/ (HOME), ah/ (AH_HOME) and tmp/ (TMPDIR), and
# AH_DAEMON=off like tests/run-suite.sh; the test's own mktemp sandboxes and tmux/daemon sockets are already unique.
# The suite's environment is not the caller's: env -i plus an allow-list (the worker block below), PATH rebuilt, and CODEX_HOME and
# CLAUDE_CONFIG_DIR set to empty directories of the test's own. Each test's output goes to <run>/logs/<name>.log. <run> is a fresh /private/tmp/ahprobe.XXXXXX; it is removed after a
# fully green run and kept (and printed) otherwise.
#
# One runner at a time machine-wide: the run takes the directory lock /private/tmp/ahprobe.looplock (mkdir is atomic),
# writes an owner file in it, and removes exactly that path on exit. A lock someone else holds is refused at once (exit 3)
# and never removed here, unless AH_RUN_ALL_LOCK_WAIT=1: then the run polls for it up to AH_TEST_SLOT_WAIT seconds (default
# 3600; exit 75 when that runs out) and takes over a lock whose owner pid is dead.
# AH_RUN_ALL_LOCK may name another lock, only under /private/tmp/ahprobe.*; AH_RUN_ALL_TESTS may name another tests
# directory and AH_RUN_ALL_SERIAL another serial lane (test names, one per line); this runner's own test
# uses all three, and AH_RUN_ALL_LEAK / AH_RUN_ALL_SETTLE (see the daemon check at the end).
#
# Concurrency policy (for every agent that runs tests; this header is the one place it is stated).
#   1. Full runs: one at a time per machine, by the lock above. When it is held, do not wait in a loop: run only the
#      suites the change touches (bash tests/test-x.sh), and report that the full run was skipped because another was
#      in progress, naming the owner this runner printed.
#   2. Solo suites take no lock: bash tests/test-x.sh, or bash tests/run-all.sh -j 1 <prefix> when the lock and the leak
#      check are wanted. A suite must pass under load; a solo failure that passes on one rerun is a flake, reported with
#      its log, and does not count as a pass.
#   3. Reruns: only the suites that failed, at most twice each. Never rerun a full run to confirm a result. No runner
#      retries without a bound.
#   4. No test runs against the user's real home or daemon (the hermetic rules in tests/lib-hermetic.sh).
#   5. Tokens. Every run of this script, in any lane, first takes ah_bucket_heavy (tests/lib-tokens.sh) of the B machine-wide test tokens
#      (tests/with-slot.sh; B is AH_TEST_TOKENS, default max(2, cpus - 2); <AH_SLOTS_DIR> defaults to /private/tmp/ahprobe.slots),
#      waiting in order and printing its queue position, and runs that many workers: -j is capped at the grant
#      (AH_TEST_TOKENS_GRANTED). Anything else heavy (a `dotnet test`, a mutation sweep) goes through the same bucket:
#      `bash tests/with-slot.sh --eta <seconds|small|medium|large> [--tokens <k>] <command>`. A command already inside a
#      grant (the environment holds AH_TEST_SLOT_HELD=1) takes no second one. AH_TEST_SLOTS=0 turns the limiter off; a wait longer
#      than AH_TEST_SLOT_WAIT seconds (default 3600) exits 75. A full run additionally keeps the lock above.
#
# Serial lane. The tests below run after the parallel lane, one at a time, each for the reason given. A reason is
# either read from the test or a measured divergence (parallel result differing from the -j 1 result). The list is a
# single-quoted string: a reason must not contain a single quote, and any double quote or dollar sign is fine there.
SERIAL_LANE='
test-test-env           # asserts on machine-wide process listings
test-suite-hermeticity  # plants a tmux server and checks the hermetic leak report for the whole machine
test-run-all            # starts runners of its own and measures their concurrency
test-roster-create-spawn # its concurrency smoke needs four 400 ms launches to finish well under their serial time; fails under parallel load, passes alone
'
# Slow first. These start before the rest of the parallel lane so the longest test is not the last one left running.
# Times are from full parallel runs on this checkout; a list that has gone stale only costs wall time.
SLOW_FIRST='
test-ahd-team-hold
test-codex-appserver-spawn
test-contracts-s0
test-contracts-s1
test-contracts-s2
test-contracts-s3
test-mod-readonly
test-native-frontend
test-native-install
test-role-packs
test-roster-team-home
test-run-set-s1
test-run-set-s2
test-run-set-s3
test-stall-failsafe
test-with-slot
'
# Long tests. A test listed here gets at least the given number of seconds (a floor under -t): its solo time is within
# a few seconds of the default 300 s, so a loaded machine times it out with nothing failing.
LONG_TESTS='
test-mod-readonly 600   # 267 s alone; timed out at 300 s in a loaded full run with no failing row
'
[ -n "${AH_RUN_ALL_SERIAL+x}" ] && SERIAL_LANE="$AH_RUN_ALL_SERIAL"
[ -n "${AH_RUN_ALL_LONG+x}" ] && LONG_TESTS="$AH_RUN_ALL_LONG"
long_seconds() { printf '%s\n' "$LONG_TESTS" | sed -e 's/#.*//' | awk -v n="$1" '$1 == n && $2 ~ /^[0-9]+$/ { print $2; exit }'; }
# Tests that must keep the real HOME (they run a program that reads the user's real configuration): none known.
REAL_HOME_LANE=""

PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
TESTS="${AH_RUN_ALL_TESTS:-$PLUGIN/tests}"
LOCK="${AH_RUN_ALL_LOCK:-/private/tmp/ahprobe.looplock}"

ncpu=$(sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4)
J=$((2 * ncpu / 3)); [ "$J" -gt 12 ] && J=12; [ "$J" -lt 1 ] && J=1
TMO=300; KEEP=0; OUTSET=""

serial_names() { printf '%s\n' "$SERIAL_LANE" | sed -e 's/#.*//' | awk 'NF {print $1}'; }
if [ "${1:-}" = "--serial-lane" ]; then serial_names; exit 0; fi
if [ "${1:-}" = "--long-tests" ]; then printf '%s\n' "$LONG_TESTS" | sed -e 's/#.*//' | awk 'NF { print $1, $2 }'; exit 0; fi

# Worker mode: run one test and write its result. Called through xargs as: run-all.sh --one <run> <idx> <test file>
if [ "${1:-}" = "--one" ]; then
  RUN=$2; IDX=$3; FILE=$4; TMO=${AH_RUN_ALL_TMO:-300}
  name=$(basename "$FILE" .sh)
  long=$(long_seconds "$name"); if [ -n "$long" ] && [ "$long" -gt "$TMO" ]; then TMO=$long; fi
  d="$RUN/t$IDX"
  mkdir -p "$d/home" "$d/ah" "$d/tmp" || exit 0
  home="$d/home"
  case " $AH_RUN_ALL_REAL_HOME " in *" $name "*) home="$HOME";; esac
  [ -e "$RUN/stop" ] && exit 0
  t0=$(date +%s)
  # The process that becomes `timeout` writes its own pid first; timeout puts itself in a group of that id, so the
  # file names the group of this test and everything it started. The runner signals those groups when it is stopped.
  mkdir -p "$d/codex" "$d/claude" || exit 0
  # The suite starts from an emptied environment plus this allow-list, so nothing the launching session exports reaches it
  # (a deny-list goes stale whenever the binary learns a new variable). A variable joins the list only with a comment
  # naming the suite that needs it and why; never CLAUDE_*, CODEX_*, HERDR_* or TMUX*.
  # AH_TEST_TOKENS and AH_TEST_TOKENS_GRANTED: test-with-slot.sh and the suites that start tests/with-slot.sh or a runner inside
  # this run size the bucket and their own parallelism from them.
  allow=()
  for v in USER LOGNAME SHELL LANG TERM TZ \
           AH_BIN AH_TEST AH_IMPL AHD_NATIVE_BIN AH_PROC_KERNEL AH_RESULTS_DIR AH_TEST_SLOT_HELD AH_TEST_SLOTS \
           AH_TEST_TOKENS AH_TEST_TOKENS_GRANTED; do
    [ -n "${!v+x}" ] && allow+=("$v=${!v}")
  done
  for v in $(compgen -e); do
    case "$v" in
      # this run's lock and abort controls stay with the runner: a suite that starts a run-all of its own would otherwise wait on,
      # or share, the lock of the run that started it
      AH_RUN_ALL_LOCK|AH_RUN_ALL_LOCK_WAIT|AH_RUN_ALL_LOCK_FAST|AH_RUN_ALL_NOABORT|AH_RUN_ALL_FAILFAST|AH_RUN_ALL_FAIL_DIR) ;;
      LC_*|AH_RUN_ALL_*) allow+=("$v=${!v}");;
    esac
  done
  # The run marker the leak check reads from daemon environments: a daemon a suite starts must carry it to count as this run's
  # (test-run-all's daemon-leak case plants one).
  [ -n "${AHD_LEAK_RUN:-}" ] && allow+=("AHD_LEAK_RUN=$AHD_LEAK_RUN")
  [ -n "${AH_RUN_ALL_TAP:-}" ] && allow+=("AH_PARITY_TAP=$AH_RUN_ALL_TAP/$name")
  # Where the contract shards (lib-contracts.sh) keep their failure diffs: their sandbox goes when they end. run-set.sh names a directory beside its result.
  allow+=("AH_TEST_FAIL_DIR=${AH_RUN_ALL_FAIL_DIR:-$RUN/fail}/$name")
  allow+=("HOME=$home" "AH_HOME=$d/ah" "TMPDIR=$d/tmp" "AH_DAEMON=off" "CODEX_HOME=$d/codex" "CLAUDE_CONFIG_DIR=$d/claude" "PATH=${AH_RUN_ALL_SUITE_PATH:-/usr/bin:/bin:/usr/sbin:/sbin}")
  ( cd "$d" && exec bash -c 'echo $$ >"$1"; shift; exec timeout -k 10 "$@"' _ "$RUN/pg/$IDX" "$TMO" /usr/bin/env -i "${allow[@]}" "${AH_RUN_ALL_BASH:-bash}" "$FILE" ) >"$RUN/logs/$name.log" 2>&1
  rc=$?
  rm -f "$RUN/pg/$IDX"
  case $rc in 0) st=ok;; 124|137) st=TIMEOUT;; *) st=FAIL;; esac
  # --fail-fast: the first failure stops further starts; tests already running finish
  if [ "$st" != ok ] && [ "${AH_RUN_ALL_FAILFAST:-0}" = 1 ]; then : >"$RUN/stop"; fi
  secs=$(( $(date +%s) - t0 ))
  printf '%s %s %s\n' "$name" "$st" "$secs" >"$RUN/res/$name"
  # early abort: past AH_ABORT_MIN finished tests, more than AH_ABORT_PCT percent of them not ok stops further starts
  if [ "${AH_RUN_ALL_NOABORT:-0}" != 1 ] && [ ! -e "$RUN/aborted" ]; then
    done_n=$(cat "$RUN"/res/* | wc -l); bad_n=$(cat "$RUN"/res/* | awk '$2 != "ok"' | wc -l)
    amin=${AH_ABORT_MIN:-20}; apct=${AH_ABORT_PCT:-50}
    case "$amin" in ''|*[!0-9]*) amin=20;; esac; case "$apct" in ''|*[!0-9]*) apct=50;; esac
    if [ "$done_n" -ge "$amin" ] && [ $((bad_n * 100)) -gt $((apct * done_n)) ]; then : >"$RUN/aborted"; : >"$RUN/stop"; fi
  fi
  if [ "$st" = ok ]; then printf 'ok      %-44s %4ss\n' "$name" "$secs"; else printf '%-7s %-44s %4ss  log: %s\n' "$st" "$name" "$secs" "$RUN/logs/$name.log"; fi
  exit 0
fi

# The long options come out first; what is left goes to getopts as before.
NOABORT=0; IMPL=native; LANE=all; WITH_SERIAL=0; CHANGED=""; HAVE_CHANGED=0; FIRST=""; EXACT=""; ONLY=""; FAILFAST=0; TAPDIR=""; REQUIRE_NATIVE=1
RA_USAGE="usage: run-all.sh [-j N] [-t SECONDS] [-k] [-o FILE] [--impl native] [--lane fast|full|all] [--with-serial] [--changed BASE] [--first FILE] [--fail-fast] [--no-abort] [--tap DIR] [--exact NAME] [--require-native] [PREFIX]"
# Machine-wide tokens (tests/with-slot.sh): the re-exec sets AH_TEST_SLOT_HELD=1, so the inner run takes no second grant. A runner
# started on a different tests directory (this runner's own test) is already inside its caller's grant.
. "$PLUGIN/tests/lib-tokens.sh"
HEAVY=$(ah_bucket_heavy); unset AH_TEST_HEAVY_SPLIT
if [ "${AH_TEST_SLOT_HELD:-}" != 1 ] && [ "$(ah_bucket_capacity)" != 0 ] && [ -z "${AH_RUN_ALL_TESTS:-}" ] && [ -f "$PLUGIN/tests/with-slot.sh" ]; then
  exec bash "$PLUGIN/tests/with-slot.sh" --eta large --tokens "$HEAVY" bash "$0" "$@"
fi

RA_REST=()
while [ $# -gt 0 ]; do
  case "$1" in
    --impl|--lane|--changed|--first|--tap|--exact|--only)
      [ $# -ge 2 ] || { echo "run-all: $1 needs a value" >&2; exit 2; }
      case "$1" in
        --impl) IMPL=$2;; --lane) LANE=$2;; --changed) CHANGED=$2; HAVE_CHANGED=1;; --first) FIRST=$2;; --tap) TAPDIR=$2;; --exact) EXACT=$2;; --only) ONLY=$2;;
      esac
      shift 2;;
    --with-serial) WITH_SERIAL=1; shift;;
    --fail-fast) FAILFAST=1; shift;;
    --no-abort) NOABORT=1; shift;;
    --require-native) REQUIRE_NATIVE=1; shift;;
    --) shift; RA_REST+=("$@"); break;;
    *) RA_REST+=("$1"); shift;;
  esac
done
set -- ${RA_REST[@]+"${RA_REST[@]}"}
while getopts "j:t:ko:" o; do
  case $o in
    j) J=$OPTARG;; t) TMO=$OPTARG;; k) KEEP=1;; o) OUTSET=$OPTARG;;
    *) echo "$RA_USAGE" >&2; exit 2;;
  esac
done
shift $((OPTIND - 1))
PREFIX="${1:-}"
GLOB='*'
if [ -n "$EXACT" ]; then
  [ -z "$PREFIX" ] || { echo "run-all: --exact and a PREFIX cannot be given together" >&2; exit 2; }
  case "$EXACT" in *[\*?[/]*) echo "run-all: --exact needs a plain test name (no glob characters, no slash): $EXACT" >&2; exit 2;; esac
  PREFIX=$EXACT; GLOB=''
fi
case "$J" in ''|*[!0-9]*|0) echo "run-all: -j needs a positive number" >&2; exit 2;; esac
case "$TMO" in ''|*[!0-9]*|0) echo "run-all: -t needs a positive number of seconds" >&2; exit 2;; esac
if [ "${AH_TEST_SLOT_HELD:-}" = 1 ]; then
  case "${AH_TEST_TOKENS_GRANTED:-}" in
    ''|*[!0-9]*|0) ;;
    *) if [ "$J" -gt "$AH_TEST_TOKENS_GRANTED" ]; then echo "run-all: -j $J capped to $AH_TEST_TOKENS_GRANTED (the test tokens granted)" >&2; J=$AH_TEST_TOKENS_GRANTED; fi ;;
  esac
fi
# Pruned 2026-11-14 (handoff/test-prune.md): suites that are skipped, kept on disk for later removal.
is_pruned() {
  case "$(basename "$1" .sh)" in
    test-skill-content-defects|test-skill-disband-search|test-lib-wait|test-run-set-list|test-path-independence|test-review-scenario|test-codex-appserver-spawn-kill|test-lib-hermetic-exit) return 0;;
  esac
  return 1
}

case "$IMPL" in native) export AH_IMPL=native;; *) echo "run-all: --impl needs native (node and manifest no longer exist)" >&2; exit 2;; esac
case "$LANE" in fast|full|all) ;; *) echo "run-all: --lane needs fast, full or all" >&2; exit 2;; esac
if [ "$IMPL" = native ]; then
  if [ -n "${AHD_NATIVE_BIN:-}" ] && { [ ! -f "${AHD_NATIVE_BIN}" ] || [ ! -x "${AHD_NATIVE_BIN}" ]; }; then
    echo "run-all: AHD_NATIVE_BIN must name the ahd executable file (a regular file, not a directory): ${AHD_NATIVE_BIN} is not; expected e.g. ${AHD_NATIVE_BIN%/}/ahd" >&2
    exit 2
  fi
  if [ -n "${AHD_NATIVE_BIN:-}" ]; then
    export AH_BIN="$AHD_NATIVE_BIN" AH_TEST=1
  else
    NSUITES=0
    for f in "$TESTS"/test-"$PREFIX"$GLOB.sh; do [ -f "$f" ] && ! is_pruned "$f" && NSUITES=$((NSUITES + 1)); done
    echo "run-all: --impl native: no native binary (AHD_NATIVE_BIN unset or not executable): $NSUITES suites SKIPPED (no-native-binary)"
    [ "$REQUIRE_NATIVE" = 1 ] && { echo "run-all: --require-native: nothing ran" >&2; exit 1; }
    exit 0
  fi
fi
if [ "$FAILFAST" = 1 ] && [ "$LANE" = all ] && [ "$HAVE_CHANGED" = 0 ]; then
  echo "run-all: --fail-fast is refused for a full-lane run (no --lane and no --changed): the full lane reports everything" >&2
  exit 2
fi
if [ -n "$FIRST" ] && [ ! -f "$FIRST" ]; then echo "run-all: --first file not found: $FIRST" >&2; exit 2; fi
if [ -n "$TAPDIR" ]; then
  case "$TAPDIR" in /*) ;; *) TAPDIR="$PWD/$TAPDIR";; esac
  mkdir -p "$TAPDIR" || { echo "run-all: cannot create --tap $TAPDIR" >&2; exit 2; }
  export AH_RUN_ALL_TAP="$TAPDIR"
fi
[ "$FAILFAST" = 1 ] && export AH_RUN_ALL_FAILFAST=1
[ "$NOABORT" = 1 ] && export AH_RUN_ALL_NOABORT=1

# --lane and --changed narrow the candidate list through the manifest; the rest of the run is as before.
NARROW=0; SEL=""
if [ "$LANE" != all ] || [ "$HAVE_CHANGED" = 1 ]; then
  NARROW=1
  MANIFEST="$TESTS/manifest.json"
  [ -f "$MANIFEST" ] || { echo "run-all: --lane and --changed read $MANIFEST, which is missing" >&2; exit 2; }
  CAND=""
  for f in "$TESTS"/test-"$PREFIX"$GLOB.sh; do [ -f "$f" ] && ! is_pruned "$f" && CAND="$CAND$(basename "$f" .sh)"$'\n'; done
  SEL_ARGS=(--manifest "$MANIFEST" --names /dev/stdin --lane "$LANE")
  [ "$WITH_SERIAL" = 1 ] && SEL_ARGS+=(--with-serial)
  if [ "$HAVE_CHANGED" = 1 ]; then
    CHANGED_LIST=$(git -C "$PLUGIN" diff --relative --name-only "$CHANGED...HEAD" 2>/dev/null) || { echo "run-all: git diff against '$CHANGED' failed" >&2; exit 2; }
    SEL_ARGS+=(--changed-list "$CHANGED_LIST")
  fi
  SEL=$(printf '%s' "$CAND" | node "$PLUGIN/tests/contract-tools/select.mjs" "${SEL_ARGS[@]}") || { echo "run-all: selection failed" >&2; exit 2; }
  if [ -z "$SEL" ]; then echo "run-all: 0/0 passed"; exit 0; fi
fi

if [ -n "$ONLY" ]; then
  [ -f "$ONLY" ] || { echo "run-all: --only: no such file: $ONLY" >&2; exit 2; }
  NARROW=1; SEL=$(cat "$ONLY")
fi

# The lock path and the run directory are only ever literal ahprobe paths.
case "$LOCK" in /private/tmp/ahprobe.?*) ;; *) echo "run-all: refusing a lock outside /private/tmp/ahprobe.*: $LOCK" >&2; exit 2;; esac
case "$LOCK" in */../*|*/..) echo "run-all: refusing a lock path with ..: $LOCK" >&2; exit 2;; esac

HAVE_LOCK=0; RUN=""; XP=""; DROP_RUN=0
# The lock is always taken, except for a --lane/--changed run whose selection holds no serial-lane suite (and
# AH_RUN_ALL_LOCK_FAST is not 1): the leak check below is by run marker, and does not need it.
NEED_LOCK=1
if [ "$NARROW" = 1 ] && [ "${AH_RUN_ALL_LOCK_FAST:-0}" != 1 ]; then
  HAS_SERIAL=0
  for n in $(serial_names); do printf '%s\n' "$SEL" | grep -qx -- "$n" && HAS_SERIAL=1; done
  [ "$HAS_SERIAL" = 0 ] && NEED_LOCK=0
fi
if [ "$NEED_LOCK" = 1 ]; then
  # AH_RUN_ALL_LOCK_WAIT=1 makes a held lock a wait instead of a refusal: poll up to AH_TEST_SLOT_WAIT seconds (default 3600),
  # exit 75 when it runs out, and take over a lock whose owner pid is dead. A live owner's lock is never removed.
  WAIT_LEFT="${AH_TEST_SLOT_WAIT:-3600}"
  case "$WAIT_LEFT" in ''|*[!0-9]*) WAIT_LEFT=3600;; esac
  while ! mkdir "$LOCK" 2>/dev/null; do
    if [ "${AH_RUN_ALL_LOCK_WAIT:-0}" = 1 ]; then
      OWNER_PID=$(awk '{print $3}' "$LOCK/owner" 2>/dev/null)
      case "$OWNER_PID" in
        ''|*[!0-9]*) ;;
        *) if ! kill -0 "$OWNER_PID" 2>/dev/null && mv "$LOCK" "$LOCK.stale.$$" 2>/dev/null; then
             # the lock moved may be one another waiter took after reading the same dead owner: put it back
             if [ "$(awk '{print $3}' "$LOCK.stale.$$/owner" 2>/dev/null)" = "$OWNER_PID" ]; then rm -rf "$LOCK.stale.$$"; else mv "$LOCK.stale.$$" "$LOCK" 2>/dev/null; fi
             continue
           fi;;
      esac
      if [ "$WAIT_LEFT" -le 0 ]; then
        echo "run-all: waited for $LOCK and it is still held by pid ${OWNER_PID:-?}; giving up (exit 75)" >&2
        exit 75
      fi
      sleep 1; WAIT_LEFT=$((WAIT_LEFT - 1))
      continue
    fi
    echo "run-all: another runner holds $LOCK: $(cat "$LOCK/owner" 2>/dev/null || echo 'no owner file')" >&2
    echo "run-all: not removing it; if that runner is gone, remove the lock yourself" >&2
    echo "run-all: another full run holds the lock (owner above); do not retry in a loop. Run only the suites you touched (bash tests/test-<name>.sh) and report that the full run was skipped. Concurrency policy: the header of tests/run-all.sh" >&2
    exit 3
  done
  HAVE_LOCK=1
  echo "run-all pid $$ $(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$LOCK/owner"
fi

# Stops every test still running: TERM to its process group, a short grace, then KILL. Only groups of tests that
# have not ended are listed (a worker removes its file when its test ends), so a finished test's id is never signalled.
kill_test_groups() {
  local g pgs i alive
  pgs=$(cat "$RUN"/pg/* 2>/dev/null)
  for g in $pgs; do kill -TERM -- "-$g" 2>/dev/null; done  # pid-owned: groups of tests the workers still list as running
  i=0
  while [ "$i" -lt 20 ]; do
    alive=0
    for g in $pgs; do kill -0 -- "-$g" 2>/dev/null && alive=1; done
    [ "$alive" = 0 ] && break
    sleep 0.5; i=$((i + 1))
  done
  for g in $pgs; do kill -KILL -- "-$g" 2>/dev/null; done  # pid-owned: groups of tests the workers still list as running
}

# On any way out: no new test starts, the running ones are stopped and gone, and only then is the lock released.
cleanup() {
  trap - EXIT INT TERM HUP
  if [ -n "$RUN" ] && [ -d "$RUN/pg" ]; then
    : >"$RUN/stop"
    [ -n "$XP" ] && kill -TERM -- "-$XP" 2>/dev/null  # pid-owned: the worker pool this runner started and has not reaped
    kill_test_groups
    [ -n "$XP" ] && wait "$XP" 2>/dev/null
  fi
  if [ "$DROP_RUN" = 1 ]; then case "$RUN" in /private/tmp/ahprobe.??????) chmod -R u+w "$RUN" 2>/dev/null; rm -rf "$RUN";; esac; fi
  if [ "$HAVE_LOCK" = 1 ]; then rm -f "$LOCK/owner"; rmdir "$LOCK" 2>/dev/null; fi
}
trap 'cleanup; exit 130' INT TERM HUP
trap cleanup EXIT

RUN=$(mktemp -d /private/tmp/ahprobe.XXXXXX)
case "$RUN" in /private/tmp/ahprobe.??????) ;; *) echo "run-all: mktemp returned '$RUN'; aborting" >&2; exit 1;; esac
mkdir "$RUN/logs" "$RUN/res" "$RUN/pg"

. "$PLUGIN/tests/lib-daemon-leak.sh"
leak_mark_run
BASELINE=$(leak_candidates | tr '\n' ' ')

# Split the test list into the two lanes.
SERIAL_NAMES=$(serial_names)
: >"$RUN/par.list"; : >"$RUN/par.slow"; : >"$RUN/ser.list"
idx=0
for f in "$TESTS"/test-"$PREFIX"$GLOB.sh; do
  [ -f "$f" ] || continue
  is_pruned "$f" && continue
  n=$(basename "$f" .sh)
  if [ "$NARROW" = 1 ] && ! printf '%s\n' "$SEL" | grep -qx -- "$n"; then continue; fi
  idx=$((idx + 1)); i=$(printf '%03d' "$idx")
  if printf '%s\n' "$SERIAL_NAMES" | grep -qx -- "$n"; then printf '%s %s\n' "$i" "$f" >>"$RUN/ser.list"
  elif printf '%s\n' "$SLOW_FIRST" | grep -qx -- "$n"; then printf '%s %s\n' "$i" "$f" >>"$RUN/par.slow"
  else printf '%s %s\n' "$i" "$f" >>"$RUN/par.list"; fi
done
cat "$RUN/par.list" >>"$RUN/par.slow" && mv "$RUN/par.slow" "$RUN/par.list"
# --first: the FAIL and TIMEOUT names of the file start first, in the file's order, in each lane
if [ -n "$FIRST" ]; then
  awk '$2 == "FAIL" || $2 == "TIMEOUT" { print $1 }' "$FIRST" >"$RUN/first.names"
  for lst in par ser; do
    awk 'NR == FNR { order[$1] = ++k; next }
         { n = $2; sub(/.*\//, "", n); sub(/\.sh$/, "", n)
           if (n in order) f[order[n]] = $0; else r[++m] = $0 }
         END { for (i = 1; i <= k; i++) if (i in f) print f[i]; for (i = 1; i <= m; i++) print r[i] }' \
      "$RUN/first.names" "$RUN/$lst.list" >"$RUN/$lst.first" && mv "$RUN/$lst.first" "$RUN/$lst.list"
  done
fi
TOTAL=$idx
[ "$TOTAL" -gt 0 ] || { echo "run-all: no tests match" >&2; DROP_RUN=1; exit 2; }

# The suites' PATH is rebuilt, never inherited: the checkout's bin first (so `ah` is the checkout's, not an installed plugin's),
# then the directory of each tool the suites use as found on the caller's PATH now (claude and codex included: test-mod-plugin-test and
# test-mod-readonly run the claude CLI), then the system directories.
suite_path=$PLUGIN/bin
for t in node dotnet git jq perl python3 tmux claude codex; do
  p=$(command -v "$t" 2>/dev/null) || continue
  case ":$suite_path:" in *":$(dirname "$p"):"*) ;; *) suite_path=$suite_path:$(dirname "$p");; esac
done
for p in /usr/bin /bin /usr/sbin /sbin; do case ":$suite_path:" in *":$p:"*) ;; *) suite_path=$suite_path:$p;; esac; done
export AH_RUN_ALL_SUITE_PATH=$suite_path AH_RUN_ALL_BASH=$(command -v bash)

SELF="$PLUGIN/tests/run-all.sh"
export AH_RUN_ALL_TMO=$TMO AH_RUN_ALL_REAL_HOME="$REAL_HOME_LANE"
T0=$(date +%s)
echo "run-all: $TOTAL tests, $J workers, timeout ${TMO}s, run dir $RUN"

# xargs appends each list line (<idx> <file>) after --one <run>, which is the worker's argument order.
lane_run() {
  [ -s "$1" ] || return 0
  set -m
  xargs -P "$2" -L 1 bash "$SELF" --one "$RUN" <"$1" &
  XP=$!
  set +m
  wait "$XP"; XP=""
}
echo "run-all: parallel lane ($(wc -l <"$RUN/par.list" | tr -d ' ') tests)"
lane_run "$RUN/par.list" "$J"
echo "run-all: serial lane ($(wc -l <"$RUN/ser.list" | tr -d ' ') tests)"
lane_run "$RUN/ser.list" 1

WALL=$(( $(date +%s) - T0 ))
cat "$RUN"/res/* 2>/dev/null | sort >"$RUN/results.txt"
PASS=$(awk '$2 == "ok"' "$RUN/results.txt" | wc -l | tr -d ' ')
BAD=$(awk '$2 != "ok" {print $1 " (" $2 ")"}' "$RUN/results.txt" | tr '\n' ' ')
NRES=$(wc -l <"$RUN/results.txt" | tr -d ' ')
if [ -e "$RUN/aborted" ] && [ "$NRES" -lt "$TOTAL" ]; then
  echo "run-all: aborted: systemic: $(awk '$2 != "ok"' "$RUN/results.txt" | wc -l | tr -d ' ') of $NRES finished tests failed (limit: more than ${AH_ABORT_PCT:-50}% once ${AH_ABORT_MIN:-20} have finished); $((TOTAL - NRES)) tests not started. Failure groups by first error line:"
  for f in $(awk '$2 != "ok" {print $1}' "$RUN/results.txt"); do
    st=$(awk -v n="$f" '$1 == n {print $2}' "$RUN/results.txt")
    line=$(node "$PLUGIN/tests/run-set-tools.mjs" firsterror "$RUN/logs/$f.log" 2>/dev/null)
    key=$(printf '%s' "${line:-$st}" | sed -E 's#[^ ]*/[^ ]*##g; s/[0-9]+//g; s/  +/ /g; s/^ //; s/ $//')
    printf '%s\t%s\n' "${key:-$st}" "$f"
  done | awk -F'\t' '{ c[$1]++; if (c[$1] <= 3) n[$1] = n[$1] (n[$1] == "" ? "" : ", ") $2; if (!($1 in o)) o[$1] = ++k }
                     END { for (g in c) printf "%d\t%d\t%s\t%s\n", c[g], o[g], g, n[g] }' |
    sort -t$'\t' -k1,1nr -k2,2n | awk -F'\t' '{ printf "  %d x %s  [%s]\n", $1, $3, $4 }'
  TOTAL=$NRES
fi
if [ "$FAILFAST" = 1 ] && [ -e "$RUN/stop" ] && [ "$NRES" -lt "$TOTAL" ]; then
  echo "run-all: fail-fast: $((TOTAL - NRES)) tests not started"
  TOTAL=$NRES
fi
[ "$NRES" = "$TOTAL" ] || BAD="$BAD [$((TOTAL - NRES)) tests wrote no result]"
# A daemon another session's tests started during this run is not this run's leak and exits on its own, so a daemon
# only counts once it has outlived a settle wait. AH_RUN_ALL_LEAK=off skips the check; AH_RUN_ALL_SETTLE sets the wait.
LEAK=""
if [ "${AH_RUN_ALL_LEAK:-on}" != off ]; then
  LEAK=$(leaked_daemons $BASELINE)
  SETTLE=${AH_RUN_ALL_SETTLE:-60}
  while [ -n "$LEAK" ] && [ "$SETTLE" -gt 0 ]; do sleep 5; SETTLE=$((SETTLE - 5)); LEAK=$(leaked_daemons $BASELINE); done
fi
if [ -n "$LEAK" ]; then echo "LEAK: daemons left running under a test sandbox:"; echo "$LEAK"; leak_attribution "$LEAK"; BAD="$BAD daemon-leak"; fi
[ "${AH_RUN_ALL_LEAK:-on}" != off ] && leak_foreign_report $BASELINE
[ -n "$OUTSET" ] && awk '{print $1, $2}' "$RUN/results.txt" >"$OUTSET"
echo "run-all: $PASS/$TOTAL passed, wall ${WALL}s, failing: ${BAD:-none}"
if [ -z "$BAD" ] && [ "$NRES" = "$TOTAL" ]; then
  [ "$KEEP" = 1 ] && echo "run-all: kept $RUN" || { chmod -R u+w "$RUN" 2>/dev/null; rm -rf "$RUN"; }
  exit 0
fi
echo "run-all: kept $RUN (logs in $RUN/logs)"
exit 1
