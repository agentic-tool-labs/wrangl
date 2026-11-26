#!/bin/bash
# agent-hierarchy — run a named test set at a commit and keep the result by (commit, set), so a pass is reused instead of run again.
# The one implementation of "run set S at SHA X": the QA agent, the reviewer and the pipeline all go through it.
#
#   bash tests/run-set.sh <repo> <sha> <set> [--fresh] [--base <base-sha> [--fresh-base]] [--tests <name>[,<name>...]]
#   bash tests/run-set.sh --list-sets      print the set names, one per line, and exit 0 (it must be the first argument)
#
#   repo   a checkout that contains the commit (any work tree of the repository)
#   sha    the full 40-hex commit; the work tree is not looked at, so uncommitted work is never tested
#   set    dotnet | shell | full | affected | list (full is dotnet alongside shell, dotnet in a clone of its own and its lines printed whole when it ends;
#          both run even after a red one)
#          affected needs --base: it builds the solution, tests the test projects and runs the shell suites the diff from the base reaches
#          (test projects by tests/contract-tools/gate-select.mjs; suites changed or covering a changed path), then tests/keep-set.sh, dotnet and
#          keep-set alongside shell as in full. It runs nothing at the base and writes no verdict, so a failure is a failure: exit 0 PASS or
#          FLAKY-PASS, 1 FAIL or SYSTEMIC, 3 ERROR. Its result is stored per base in <sha>/affected-<base>/, with affected_base and
#          ran_only naming what ran (`run-set-tools.mjs affectedcheck`). It becomes full --base, with that set's verdict and exit codes,
#          when the diff touches shared infrastructure (AFF_SHARED), the selector fails or asks for full (`full <why>`).
#          A full run with --base whose diff from the base touches no shell or script file (diff_mode) does not run shell, except the
#          contract shards when the diff touches native/ outside native/tests: they drive the C# binary. The result and the verdict carry
#          not_run: ["shell"], and ran_only: {"shell": [the suites]} when the shards ran; the verdict line says so. Such a result answers
#          only a request that may skip as much; any other request runs shell and remakes it. A request for the shell set itself always
#          runs it. The base of a shards run needs only its dotnet and its shards, so a base that was itself such a run serves as it is.
#   --fresh  run again a set whose stored result is FAIL. A stored PASS or FLAKY-PASS is always reused.
#   --base B  also judge the result against the one of the full commit B (run first, and stored, when B has none): each failing suite is
#            NEW (fails here, not at B; a suite that exists only here and fails is NEW), STILL-FAILING (fails at both), and each suite
#            failing at B only is FIXED; a failure that passes when rerun alone is FLAKY. The verdict (clean, new-failures or
#            incomparable) and the lists are written to <sha>/<set>/verdict-<B>.json (keys sha, set, base, verdict, classes) and printed;
#            the file also holds the sha256 of the result.json it judged and of the base's (head_sha256, base_sha256): it is valid only
#            while both still match the files on disk (`run-set-tools.mjs verdictcheck`), and any run that rewrites the sha's result.json
#            deletes the set's verdict files under the same lock.
#            result.json carries no verdict, so a caller without --base reads the same result whoever ran before it. A --base run
#            stores FAIL for a head with STILL-FAILING suites without rerunning them, and a later caller without --base reuses that FAIL.
#            Exit with --base: 0 clean (even when the status is FAIL because the failures are all STILL-FAILING), 4 new-failures,
#            5 incomparable, 3 ERROR (a result of the sha that could not complete), 75 as below. Failures at B are not rerun.
#            Fail fast then counts the NEW failures only, so failures that already existed at B do not make a run SYSTEMIC; and
#            the shell set runs with run-all's early abort off.
#            A base is run to the end and stored whole (no systemic limit, no rerun cap, the early abort off, its isolated reruns on); a
#            base that ends SYSTEMIC or ERROR is incomparable, never a baseline. A failure is STILL-FAILING only when it failed again on
#            its isolated rerun at B; one that was FLAKY there, or not rerun, and fails here is NEW. B must be an ancestor of the sha.
#            Each stored result records an environment fingerprint (hostname, cpu count, run-all's worker count, sandbox flag, inside an agent session or not: any of
#            CLAUDECODE, HERDR_*, or any CODEX_* except CODEX_HOME in the environment). A base whose host or in-session flag differs from this run's, or that
#            has no fingerprint, is not compared: the verdict is incomparable (no NEW is reported, and fail fast counts every
#            failure). --fresh-base runs the base again, here, in that case only. The store is persistent, so a stored base is
#            reused across a reboot; one that is gone (pruned) is simply run again.
#
# The sets (the only place they are named; adding one is a new case in run_set_body and a name in SETS):
#   dotnet   the clone's scripts/dotnet-test.sh --logger trx, which builds and tests the whole solution inside one machine-wide slot
#   shell    the clone's tests/run-all.sh, with AHD_NATIVE_BIN the ahd published from the clone
#   list     shell narrowed to the suites --tests names (test-<name>, with or without tests/ and .sh; each must exist at the sha, else
#            a usage error before anything runs), stored per list in <sha>/list-<hash of the sorted names>/, so it never answers
#            a request for shell or full. With --base the base runs the same list.
# Every command is the clone's own copy, so the tests match the commit; this script never takes a slot itself (each command takes its
# own, so two runs cannot hold one slot each while waiting for another) and never calls dotnet itself.
#
# Flow: look the result up -> take the key's lock (a second caller waits for the result and returns it) -> make a clone of the commit
# -> run the set -> run each failed test once more, alone (under full a shell test waits for the dotnet job first) -> write result.json. A test that fails twice makes the set FAIL; a
# test that fails and then passes alone is flaky and the set is FLAKY-PASS (green); a set whose run could not complete is ERROR and
# is not stored.
#
# A suite's kept failure diffs go to fail/<suite>/ beside the result of the set that ran it, and first_error holds the first one's opening lines.
# Store: $HOME/.claude/ah2/qa-results/<sha>/<set>/{result.json,run.log}, which survives a reboot (AH_RESULTS_DIR names another store, only
# under that root or /private/tmp/ahprobe.*). The first run copies in, without overwriting, the entries of the old
# /private/tmp/ahprobe.results; an entry untouched for more than 30 days is removed at start. Clones, binaries and sockets stay under
# /private/tmp/ahprobe.*. AH_TEST_SLOT_WAIT (default 3600 s) bounds the wait for another caller's lock.
# Exit: 0 PASS or FLAKY-PASS; 1 FAIL or SYSTEMIC; 2 usage error; 3 ERROR; 75 the wait for a lock ran out; with --base 0, 3, 4, 5, 75
# as described there.
#
# Fail fast (all three sets alike; each threshold is an environment variable):
#   SYSTEMIC   after the main pass, failures > AH_SYSTEMIC_MAX (default 10) or, for a set of at least AH_SYSTEMIC_PCT_MIN tests
#              (default 20), > AH_SYSTEMIC_PCT percent of the set (default 15)
#              end the run with status SYSTEMIC and no rerun at all. The failures are printed grouped by their first FAIL/error
#              line (paths and numbers stripped), with a count per group and up to 3 names each.
#   rerun cap  otherwise at most AH_RERUN_MAX (default 10) failures are rerun; the rest are reported "not-rerun" and count as failed.
#   parallel   reruns run AH_RERUN_JOBS at a time (default min(ncpu/2, 8)). Each rerun also takes 1 test token
#              (AH_TEST_TOKENS, default max(2, ncpu - 2)), so no more reruns than there are tokens run at once whatever AH_RERUN_JOBS
#              says; a rerun that waits longer than AH_TEST_SLOT_WAIT for its token makes the set ERROR (exit 3), not FAIL.
#   timeout    each single-test shell or .NET rerun gets AH_RERUN_TIMEOUT seconds (default 60) once it holds its test slot (the
#              wait for a slot is not timed); one that runs out failed again. A shell rerun gets at least the suite's own limit
#              in run-all (`run-all.sh --limit NAME`: 300 s, or its LONG_TESTS floor; the clone's first, run-set's own when that
#              gives no number), plus 30 s so run-all records its own TIMEOUT first.
#   aborted    a run-all inside the shell set that stopped early ("aborted: systemic") makes the set SYSTEMIC whatever the limits.
#   SYSTEMIC exits 1, is never stored for reuse, and runs no rerun: report the failure groups it printed.

set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
PLUGIN="$(cd "$HERE/.." && pwd)"
TOOLS="$HERE/run-set-tools.mjs"
STORE_ROOT="$HOME/.claude/ah2/qa-results"
DEFAULT_STORE=1; [ -z "${AH_RESULTS_DIR:-}" ] || DEFAULT_STORE=0
STORE="${AH_RESULTS_DIR:-$STORE_ROOT}"
LEGACY_STORE="${AH_LEGACY_RESULTS_DIR:-/private/tmp/ahprobe.results}"
WAIT="${AH_TEST_SLOT_WAIT:-3600}"
SETS="dotnet shell full affected list"
# the set names, one per line, for callers that must not keep their own copy of the list
if [ "${1:-}" = "--list-sets" ]; then printf '%s\n' $SETS; exit 0; fi
numvar() { local v; eval "v=\${$1:-}"; case "$v" in ''|*[!0-9]*) echo "$2";; *) echo "$v";; esac; }
SYSTEMIC_MAX=$(numvar AH_SYSTEMIC_MAX 10); SYSTEMIC_PCT=$(numvar AH_SYSTEMIC_PCT 15)
SYSTEMIC_PCT_MIN=$(numvar AH_SYSTEMIC_PCT_MIN 20)
RERUN_MAX=$(numvar AH_RERUN_MAX 10); RERUN_TMO=$(numvar AH_RERUN_TIMEOUT 60)
RERUN_J=$(( $(sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4) / 2 )); [ "$RERUN_J" -gt 8 ] && RERUN_J=8
RERUN_J=$(numvar AH_RERUN_JOBS "$RERUN_J"); [ "$RERUN_J" -lt 1 ] && RERUN_J=1

usage() { echo "run-set: $1" >&2; echo "usage: run-set.sh <repo> <sha> <set> [--fresh] [--base <sha> [--fresh-base]] [--tests <name>[,<name>...]]   (set: dotnet|shell|full|affected|list)   or: run-set.sh --list-sets" >&2; exit 2; }

case "$STORE" in */..|*/../*) usage "AH_RESULTS_DIR must not contain ..: $STORE";; esac
if [ -d "$STORE" ]; then STORE="$(cd "$STORE" && pwd -P)"; fi
STORE_ROOT_P="$STORE_ROOT"; if [ -d "$STORE_ROOT" ]; then STORE_ROOT_P="$(cd "$STORE_ROOT" && pwd -P)"; fi
case "$STORE" in
  /private/tmp/ahprobe.?*) ;;
  "$STORE_ROOT"|"$STORE_ROOT"/?*|"$STORE_ROOT_P"|"$STORE_ROOT_P"/?*) ;;
  *) usage "AH_RESULTS_DIR must be under $STORE_ROOT or /private/tmp/ahprobe.*: $STORE";;
esac
case "$WAIT" in ''|*[!0-9]*) WAIT=3600;; esac
FRESH=0; ARGS=(); BASE=""; FRESHBASE=0; TESTS=""; HAVE_TESTS=0; LIST=""; LKEY=""
# set only by resolve_base on the base run it starts: that run makes again any stored result that was not made where this run is
BASERUN="${AH_RUN_SET_BASERUN:-0}"; unset AH_RUN_SET_BASERUN
# set only by resolve_base with it: how much of shell the run being judged needs from this base
BASEMODE="${AH_RUN_SET_BASEMODE:-}"; unset AH_RUN_SET_BASEMODE
while [ $# -gt 0 ]; do
  case "$1" in
    --fresh) FRESH=1;;
    --fresh-base) FRESHBASE=1;;
    --base) [ $# -ge 2 ] || usage "--base needs a commit"; BASE="$2"; shift;;
    --tests) [ $# -ge 2 ] || usage "--tests needs <name>[,<name>...]"; TESTS="$2"; HAVE_TESTS=1; shift;;
    -h|--help) sed -n '2,/^set -u/p' "$0" | sed '$d'; exit 0;;
    -*) usage "unknown option $1";;
    *) ARGS+=("$1");;
  esac
  shift
done
[ "${#ARGS[@]}" = 3 ] || usage "needs <repo> <sha> <set>"
REPO="${ARGS[0]}"; SHA="${ARGS[1]}"; SET="${ARGS[2]}"
case " $SETS " in *" $SET "*) ;; *) usage "unknown set '$SET'";; esac
case "$SHA" in *[!0-9a-f]*|'') usage "sha must be the full 40-hex commit";; esac
[ "${#SHA}" = 40 ] || usage "sha must be the full 40-hex commit"
[ -d "$REPO" ] || usage "repo is not a directory: $REPO"
git -C "$REPO" cat-file -e "$SHA^{commit}" 2>/dev/null || usage "$SHA is not a commit of $REPO"
if [ -n "$BASE" ]; then
  case "$BASE" in *[!0-9a-f]*) usage "--base must be the full 40-hex commit";; esac
  [ "${#BASE}" = 40 ] || usage "--base must be the full 40-hex commit"
  [ "$BASE" != "$SHA" ] || usage "--base must differ from the sha"
  git -C "$REPO" cat-file -e "$BASE^{commit}" 2>/dev/null || usage "$BASE is not a commit of $REPO"
  git -C "$REPO" merge-base --is-ancestor "$BASE" "$SHA" 2>/dev/null || usage "--base must be an ancestor of the sha"
fi
[ "$FRESHBASE" = 0 ] || [ -n "$BASE" ] || usage "--fresh-base needs --base"
[ "$SET" != affected ] || [ -n "$BASE" ] || usage "affected needs --base: the diff from it decides what runs"
# where the plugin sits inside the repository ("agent-hierarchy-v2/", or empty at the root)
PREFIXREL="$(git -C "$PLUGIN" rev-parse --show-prefix 2>/dev/null)"
# list: the named suites, checked against the sha's tree before anything runs; LIST one name per line, sorted; LKEY its store key
if [ "$SET" = list ]; then
  IFS=, read -r -a NAMES <<< "$TESTS"
  for n in ${NAMES[@]+"${NAMES[@]}"}; do
    n="${n#tests/}"; n="${n%.sh}"
    [ -n "$n" ] || continue
    case "$n" in test-*) ;; *) usage "unknown suite '$n': a suite is a test-<name>";; esac
    case "$n" in */*|*[!A-Za-z0-9._-]*) usage "unknown suite '$n'";; esac
    git -C "$REPO" cat-file -e "$SHA:${PREFIXREL}tests/$n.sh" 2>/dev/null || usage "unknown suite '$n': no tests/$n.sh at $SHA"
    LIST="$LIST$n"$'\n'
  done
  [ -n "$LIST" ] || usage "list needs --tests <name>[,<name>...]"
  LIST="$(printf '%s' "$LIST" | sort -u)"
  TESTS="$(printf '%s\n' "$LIST" | paste -sd, -)"
  LKEY="list-$(printf '%s\n' "$LIST" | git hash-object --stdin | cut -c1-12)"
elif [ "$HAVE_TESTS" = 1 ]; then
  usage "--tests needs the list set"
fi

now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
CLONE=""; BINDIR=""; MYLOCKS=(); DOTNET_JOB=""

safe_rm() { case "$1" in */..|*/../*) ;; /private/tmp/ahprobe.?*) chmod -R u+w "$1" 2>/dev/null; rm -rf "$1";; esac; }
# The only removal inside the store, and only of in-progress state: a lock, or a key that holds no result.json. A stored result is never removed here.
store_rm() {
  case "$1" in */..|*/../*) return 0;; esac
  case "$1" in
    "$STORE"/*/*/.lock|"$STORE"/*/*/.lock.stale.*) ;;
    "$STORE"/?*/?*) [ -e "$1/result.json" ] && return 0;;
    *) return 0;;
  esac
  rm -rf "$1"
}
# results are added or replaced per <sha>/<set>; the only deletion is an entry untouched for more than 30 days, two levels below the store
prune_store() {
  local d
  [ -d "$STORE" ] || return 0
  find "$STORE" -mindepth 2 -maxdepth 2 -type d -mtime +30 ! -path "$STORE/errors/*" 2>/dev/null | while IFS= read -r d; do
    case "$d" in */..|*/../*) continue;; "$STORE"/?*/?*) ;; *) continue;; esac
    [ -e "$d/.lock" ] || rm -rf "$d"
  done
  find "$STORE" -mindepth 1 -maxdepth 1 -type d -empty ! -name errors -exec rmdir {} \; 2>/dev/null
  return 0
}
# one-time copy of the entries of the old /private/tmp store that the new one lacks; nothing is overwritten and nothing is deleted
adopt_entry() { # <sha>/<set>
  local rel=$1 d="$LEGACY_STORE/$1"
  [ -f "$d/result.json" ] || return 0
  case "$rel" in errors/*) return 0;; esac
  [ -e "$STORE/$rel" ] && return 0
  mkdir -p "$STORE/${rel%/*}" && cp -R "$d" "$STORE/$rel.adopt.$$" && rm -rf "$STORE/$rel.adopt.$$/.lock" && mv "$STORE/$rel.adopt.$$" "$STORE/$rel" 2>/dev/null
  [ -e "$STORE/$rel.adopt.$$" ] && rm -rf "$STORE/$rel.adopt.$$"
  return 0
}
adopt_legacy() {
  local d
  [ "$DEFAULT_STORE" = 1 ] && [ -d "$LEGACY_STORE" ] || return 0
  for d in "$LEGACY_STORE"/*/*; do adopt_entry "${d#"$LEGACY_STORE"/}"; done
  return 0
}
cleanup() {
  local l
  [ -n "$DOTNET_JOB" ] && kill "$DOTNET_JOB" 2>/dev/null  # pid-owned: this shell's own background job, not yet waited for
  for l in ${MYLOCKS[@]+"${MYLOCKS[@]}"}; do
    [ "$(cat "$l/owner" 2>/dev/null)" = "$$" ] && store_rm "$l"
  done
  [ -n "$CLONE" ] && safe_rm "$CLONE"
  [ -n "$BINDIR" ] && safe_rm "$BINDIR"
}
trap cleanup EXIT
trap 'exit 130' INT TERM HUP

# ---- the lock of a key: mkdir plus an owner pid; stale when the owner is dead or never wrote its pid within 5 s
lock_take() { # <dir>: 0 taken, 1 held by a live owner
  local d=$1 o i
  mkdir -p "$(dirname "$d")" || return 1
  if mkdir "$d" 2>/dev/null; then echo $$ > "$d/owner"; MYLOCKS+=("$d"); return 0; fi
  o="$(cat "$d/owner" 2>/dev/null)"
  i=0
  while [ -z "$o" ] && [ "$i" -lt 5 ] && [ -d "$d" ]; do sleep 1; o="$(cat "$d/owner" 2>/dev/null)"; i=$((i + 1)); done
  if [ -n "$o" ] && kill -0 "$o" 2>/dev/null; then return 1; fi
  if mv "$d" "$d.stale.$$" 2>/dev/null; then
    # the directory moved may be a lock another waiter took after reading the same dead owner: put it back
    if [ "$(cat "$d.stale.$$/owner" 2>/dev/null)" = "$o" ]; then store_rm "$d.stale.$$"; else mv "$d.stale.$$" "$d" 2>/dev/null; return 1; fi
  fi
  if mkdir "$d" 2>/dev/null; then echo $$ > "$d/owner"; MYLOCKS+=("$d"); return 0; fi
  return 1
}
lock_drop() { local d=$1; [ "$(cat "$d/owner" 2>/dev/null)" = "$$" ] && store_rm "$d"; }

# ---- result bookkeeping: R_STATUS, R_PATH, R_CACHED describe the last set resolved
R_STATUS=""; R_PATH=""; R_CACHED=0
# a caller that waited on another caller's run takes that run's FAIL even under --fresh: it is as new as a rerun would be
WAITED=0

lookup() { # <set>: 0 when a stored result answers this request
  local f="$STORE/$SHA/$1/result.json" st
  [ -f "$f" ] || return 1
  # a base run reuses a stored result only when it was made where this run is: one without a fingerprint, or with another, is made again
  [ "$BASERUN" = 1 ] && [ -n "$(node "$TOOLS" envcmp "$f")" ] && return 1
  # a full made without all of shell answers only a request that may skip as much
  if [ "$1" = full ] && grep -q '"not_run"' "$f"; then
    case "$DIFF_MODE" in
      all) return 1;;
      shards) grep -q '"ran_only"' "$f" || return 1;;
    esac
  fi
  st="$(node "$TOOLS" field "$f" status)"
  case "$st" in
    PASS|FLAKY-PASS) ;;
    FAIL) [ "$FRESH" = 1 ] && [ "$WAITED" = 0 ] && return 1;;
    *) return 1;;
  esac
  R_STATUS="$st"; R_PATH="$f"; R_CACHED=1
  return 0
}

error_result() { # <set> <message>: an ERROR result outside the store; the key is removed
  local set=$1 msg=$2 d="$STORE/$SHA/$1" ef="$STORE/errors/$SHA-$1-$$"
  mkdir -p "$STORE/errors"
  [ -f "$d/run.log" ] && cp "$d/run.log" "$ef.log"
  printf 'run-set\t\t%s\tfalse\n' "$msg" > "$ef.failures"
  node "$TOOLS" result --sha "$SHA" --set "$set" --status ERROR --started "$KSTART" --ended "$(now)" --log "$ef.log" \
    --counts "0 0 0" --failures "$ef.failures" --flaky /dev/null > "$ef.json"
  rm -f "$ef.failures"
  lock_drop "$d/.lock"
  store_rm "$d"
  R_STATUS=ERROR; R_PATH="$ef.json"; R_CACHED=0
  echo "run-set: ERROR $set: $msg" >&2
}

# ---- the clone, and the binaries published from it, made when a set first needs them and shared by every set of this run
ensure_clone() {
  [ -n "$CLONE" ] && return 0
  CLONE="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
  case "$CLONE" in /private/tmp/ahprobe.??????) ;; *) CLONE=""; ERRMSG="mktemp returned an unexpected directory"; return 1;; esac
  if ! git clone -q --shared --no-checkout "$REPO" "$CLONE" >> "$LOG" 2>&1 || ! git -C "$CLONE" checkout -q --detach "$SHA" >> "$LOG" 2>&1; then
    safe_rm "$CLONE"; CLONE=""; ERRMSG="the clone of $SHA failed"; return 1
  fi
  return 0
}
ensure_bin() {
  [ -n "$BINDIR" ] && return 0
  ensure_clone || return 1
  [ -f "$CLONE/${PREFIXREL}tests/publish-bin.sh" ] || { ERRMSG="SHA predates tests/publish-bin.sh"; return 1; }
  BINDIR="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
  case "$BINDIR" in /private/tmp/ahprobe.??????) ;; *) BINDIR=""; ERRMSG="mktemp returned an unexpected directory"; return 1;; esac
  if ! ( cd "$CLONE/$PREFIXREL" && bash tests/publish-bin.sh --out "$BINDIR/bin" --log-dir "$BINDIR" ) >> "$LOG" 2>&1; then
    ERRMSG="the publish of ahd and ahd-tui from $SHA failed"; safe_rm "$BINDIR"; BINDIR=""; return 1
  fi
  return 0
}

# ---- one run of a failed test alone: 0 passed alone, 1 failed again, 3 the run could not tell (ERRMSG says why)
rerun_dotnet() { # <name> <project>
  local name=$2 proj=$3 csproj dir rc c
  csproj="native/tests/$proj/$proj.csproj"
  [ -f "$CLONE/${PREFIXREL}$csproj" ] || return 1
  dir="$KD/rerun-$RERUN_N"; RERUN_N=$((RERUN_N + 1))
  ( cd "$CLONE/$PREFIXREL" && bash "$HERE/with-slot.sh" --eta "$RERUN_TMO" --tokens 1 timeout -k 5 "$RERUN_TMO" bash scripts/dotnet-test.sh "$csproj" --no-build --filter "FullyQualifiedName=${name%%(*}" --logger trx --results-directory "$dir" ) >> "$LOG" 2>&1
  rc=$?
  [ "$rc" = 75 ] && { ERRMSG="the wait for a test slot ran out (exit 75) before the rerun of $name"; return 3; }
  c="$(node "$TOOLS" trx "$dir" | sed -n 's/^COUNTS //p')"
  set -- $c
  [ "$rc" = 0 ] && [ "${1:-0}" -ge 1 ] && [ "${3:-1}" = 0 ]
}
# the err column of a failed script becomes its first error (run-set-tools.mjs firsterror), from the diffs it kept and the log run-all named in $LOG
shell_first_errors() {
  local name proj err logf line
  : > "$FAILED.new"
  while IFS=$'\t' read -r name proj err; do
    logf="$(grep -E "^(FAIL|TIMEOUT) +$name " "$LOG" | sed -n 's/.* log: //p' | tail -1)"
    line="$(node "$TOOLS" firsterror "$logf" "$KD/fail/$name")"
    printf '%s\t%s\t%s\n' "$name" "$proj" "${line:-$err}" >> "$FAILED.new"
  done < "$FAILED"
  mv "$FAILED.new" "$FAILED"
}
rerun_shell() { # <name> <project>
  local name=$2 stem set rc tmo lim
  stem="${name#test-}"
  set="$KD/rerun-$RERUN_N.set"; RERUN_N=$((RERUN_N + 1))
  # at least the suite's own limit in run-all: a suite that needs more than AH_RERUN_TIMEOUT alone could never pass a rerun.
  # The clone's run-all first (it is the one that runs the rerun); run-set's own only when that gives no number.
  # AH_TEST_SLOT_HELD=1: a clone's run-all from before --limit would otherwise queue for a heavy grant, then refuse the option
  tmo=$RERUN_TMO; lim=$(AH_TEST_SLOT_HELD=1 bash "$CLONE/${PREFIXREL}tests/run-all.sh" --limit "$name" 2>/dev/null)
  case "$lim" in ''|*[!0-9]*) lim=$(bash "$HERE/run-all.sh" --limit "$name" 2>/dev/null);; esac
  case "$lim" in ''|*[!0-9]*) ;; *) [ "$lim" -gt "$tmo" ] && tmo=$lim;; esac
  # the outer timeout is the inner limit plus 30 s, so run-all records the TIMEOUT before the outer kill.
  # a lock of its own: the machine lock would make concurrent reruns wait for one another. Under BINDIR, since run-all refuses a lock outside ahprobe.
  ( cd "$CLONE/$PREFIXREL" && AH_RUN_ALL_LOCK="$BINDIR/rerun-lock-$RERUN_N" AH_RUN_ALL_LOCK_WAIT=1 AHD_NATIVE_BIN="$BINDIR/bin/ahd" \
      bash "$HERE/with-slot.sh" --eta "$((tmo + 30))" --tokens 1 timeout -k 5 "$((tmo + 30))" bash tests/run-all.sh -j 1 --exact "$stem" -o "$set" ) >> "$LOG" 2>&1
  rc=$?
  [ "$rc" = 75 ] && { ERRMSG="the wait for a test slot ran out (exit 75) before the rerun of $name"; return 3; }
  [ "$rc" = 2 ] && { ERRMSG="run-all refused the rerun of $name (exit 2), so the test never ran"; return 3; }
  [ "$(cat "$set" 2>/dev/null)" = "$name ok" ]
}

# say <line>: to the log and to stderr
say() { printf '%s\n' "$1" | tee -a "$LOG" >&2; }

# failure_groups: stdin is name<TAB>project<TAB>first error; prints one line per group of equal errors (paths and numbers
# stripped), most frequent first, with the count and up to 3 names
failure_groups() {
  local key name proj err
  while IFS=$'\t' read -r name proj err; do
    [ -n "$name" ] || continue
    key="$(printf '%s' "${err:-failed, no error line}" | sed -E 's#[^ ]*/[^ ]*##g; s/[0-9]+//g; s/  +/ /g; s/^ //; s/ $//')"
    printf '%s\t%s\n' "${key:-failed, no error line}" "$name"
  done | awk -F'\t' '{ c[$1]++; if (c[$1] <= 3) n[$1] = n[$1] (n[$1] == "" ? "" : ", ") $2; if (!($1 in o)) o[$1] = ++k }
                     END { for (g in c) printf "%d\t%d\t%s\t%s\n", c[g], o[g], g, n[g] }' |
    sort -t$'\t' -k1,1nr -k2,2n | awk -F'\t' '{ printf "  %d x %s  [%s]\n", $1, $3, $4 }'
}

# classify <set>: past the systemic limits nothing is rerun; otherwise up to RERUN_MAX failures (name, project, first error)
# are run once more alone, RERUN_J at a time, each for at most RERUN_TMO seconds
classify() {
  local set=$1 name proj err rc idx n nf total twice=0 flaky=0 skipped=0 nstill=0 RR="$KD/rr" CAND="$KD/newcand" STILLF="$KD/still"
  : > "$KD/failures.tsv"; : > "$KD/flaky.tsv"; : > "$STILLF"
  if [ -n "$BASEKEYS" ]; then
    : > "$CAND"
    while IFS=$'\t' read -r name proj err; do
      [ -n "$name" ] || continue
      if grep -qxF -- "$name" "$BASEKEYS"; then printf '%s\t%s\t%s\n' "$name" "$proj" "$err" >> "$STILLF"; else printf '%s\t%s\t%s\n' "$name" "$proj" "$err" >> "$CAND"; fi
    done < "$FAILED"
    nstill="$(grep -c . "$STILLF")"
  else
    cp "$FAILED" "$CAND"
  fi
  nf="$(grep -c . "$CAND")"
  total="$(echo "$COUNTS" | awk '{print $1 + 0}')"
  if [ "${SHELL_ABORTED:-0}" = 1 ] || [ "$nf" -gt "$SYSTEMIC_MAX" ] || { [ "$total" -ge "$SYSTEMIC_PCT_MIN" ] && [ "$total" -gt 0 ] && [ $((nf * 100)) -gt $((SYSTEMIC_PCT * total)) ]; }; then
    while IFS=$'\t' read -r name proj err; do
      [ -n "$name" ] && printf '%s\t%s\t%s\tfalse\n' "$name" "$proj" "$err" >> "$KD/failures.tsv"
    done < "$FAILED"
    say "run-set: SYSTEMIC $set: $nf of $total failed${BASEKEYS:+ ($nstill more already fail at the base and are not counted)} (limits: more than $SYSTEMIC_MAX, or more than $SYSTEMIC_PCT% of at least $SYSTEMIC_PCT_MIN); no rerun. Failure groups:"
    failure_groups < "$CAND" | tee -a "$LOG" >&2
    STATUS=SYSTEMIC
    return 0
  fi
  rm -rf "$RR"; mkdir -p "$RR"; : > "$RR/list"; : > "$RR/skipped"
  idx=0
  while IFS=$'\t' read -r name proj err; do
    [ -n "$name" ] || continue
    idx=$((idx + 1))
    if [ "$idx" -le "$RERUN_MAX" ]; then printf '%s\t%s\t%s\t%s\n' "$idx" "$name" "$proj" "$err" >> "$RR/list"
    else printf '%s\t%s\t%s\n' "$name" "$proj" "$err" >> "$RR/skipped"; fi
  done < "$CAND"
  # a shell rerun must run alone: a suite that fails only under dotnet's load would otherwise fail twice, and at a base hide a real failure as
  # STILL. By polling: a wait here would take the status the main wait needs. The dotnet job's own reruns do not wait, so the two cannot deadlock.
  if [ "$set" != dotnet ] && [ -n "$DOTNET_JOB" ] && [ -s "$RR/list" ]; then
    while kill -0 "$DOTNET_JOB" 2>/dev/null; do sleep 1; done  # pid-owned: this shell's own background job, not yet waited for
  fi
  idx=0; RJOBS=()
  while IFS=$'\t' read -r n name proj err; do
    ( RERUN_N=$n; ERRMSG=""; "rerun_$set" "$set" "$name" "$proj"; rc=$?; printf '%s' "$ERRMSG" > "$RR/$n.err"; echo "$rc" > "$RR/$n.rc" ) < /dev/null &
    RJOBS+=($!)
    idx=$((idx + 1)); if [ $((idx % RERUN_J)) -eq 0 ]; then wait "${RJOBS[@]}"; RJOBS=(); fi
  done < "$RR/list"
  # by pid: under full the dotnet job is a child of this shell too, and a bare wait would wait for it and take its exit status
  [ "${#RJOBS[@]}" = 0 ] || wait "${RJOBS[@]}"
  while IFS=$'\t' read -r n name proj err; do
    rc="$(cat "$RR/$n.rc" 2>/dev/null)"
    case "${rc:-3}" in
      0) printf '%s\t%s\n' "$name" "$proj" >> "$KD/flaky.tsv"; flaky=$((flaky + 1));;
      1) printf '%s\t%s\t%s\ttrue\n' "$name" "$proj" "$err" >> "$KD/failures.tsv"; twice=$((twice + 1));;
      *) ERRMSG="$(cat "$RR/$n.err" 2>/dev/null)"; return 1;;
    esac
  done < "$RR/list"
  while IFS=$'\t' read -r name proj err; do
    printf '%s\t%s\tnot-rerun: %s\tfalse\n' "$name" "$proj" "$err" >> "$KD/failures.tsv"; skipped=$((skipped + 1))
  done < "$RR/skipped"
  [ "$skipped" -gt 0 ] && say "run-set: $skipped failures of $set not-rerun (more than $RERUN_MAX)"
  while IFS=$'\t' read -r name proj err; do
    printf '%s\t%s\t%s\tfalse\n' "$name" "$proj" "$err" >> "$KD/failures.tsv"
  done < "$STILLF"
  if [ "$twice" -gt 0 ] || [ "$skipped" -gt 0 ] || [ "$nstill" -gt 0 ]; then STATUS=FAIL; elif [ "$flaky" -gt 0 ]; then STATUS=FLAKY-PASS; else STATUS=PASS; fi
  return 0
}

# ---- the sets: each fills $FAILED (tab-separated name, project, first error), $COUNTS ("total passed failed") and returns 0, or
# sets ERRMSG and returns 1 when the run could not complete
run_dotnet() {
  local rc c bad nproj nfiles
  [ -f "$CLONE/${PREFIXREL}scripts/dotnet-test.sh" ] || { ERRMSG="SHA predates scripts/dotnet-test.sh"; return 1; }
  rm -rf "$KD/trx"; mkdir -p "$KD/trx"
  ( cd "$CLONE/$PREFIXREL" && bash scripts/dotnet-test.sh --logger trx --results-directory "$KD/trx" ) > "$KD/dotnet.out" 2>&1
  rc=$?
  cat "$KD/dotnet.out" >> "$LOG"
  [ "$rc" = 75 ] && { ERRMSG="the wait for a test slot ran out (exit 75)"; return 1; }
  node "$TOOLS" trx "$KD/trx" > "$KD/trx.out"
  COUNTS="$(sed -n 's/^COUNTS //p' "$KD/trx.out")"
  sed -n 's/^FAIL //p' "$KD/trx.out" > "$FAILED"
  bad="$(sed -n 's/^BADOUTCOME //p' "$KD/trx.out" | paste -sd, -)"
  [ -z "$bad" ] || { ERRMSG="a test run did not complete ($bad)"; return 1; }
  # the second is xUnit's own line: its project then reports no tests and still writes a result, so nothing else here sees it
  if grep -qE 'Test host process crashed|Test process crashed with exit code|The active test run was aborted' "$KD/dotnet.out"; then
    ERRMSG="the log shows a crashed test host or an aborted run"; return 1
  fi
  nproj="$(ls -d "$CLONE/${PREFIXREL}native/tests"/*/*.Tests.csproj 2>/dev/null | wc -l | tr -d ' ')"
  [ -z "${AH_DOTNET_TEST_PROJECTS:-}" ] || nproj="$(printf '%s\n' $AH_DOTNET_TEST_PROJECTS | wc -l | tr -d ' ')"
  nfiles="$(sed -n 's/^FILES //p' "$KD/trx.out")"
  if [ "${nfiles:-0}" -lt "$nproj" ]; then ERRMSG="only ${nfiles:-0} of $nproj test projects wrote a result"; return 1; fi
  set -- $COUNTS
  if [ "${1:-0}" = 0 ]; then ERRMSG="no test result was written (exit $rc): the build or the test host failed"; return 1; fi
  if [ ! -s "$FAILED" ] && [ "$rc" != 0 ]; then ERRMSG="the run exited $rc with no failed test: the test host did not finish"; return 1; fi
  return 0
}
run_shell() {
  local rc set="$KD/shell.set"
  ensure_bin || return 1
  rm -f "$set"
  ( cd "$CLONE/$PREFIXREL" && AH_RUN_ALL_FAIL_DIR="$KD/fail" AH_RUN_ALL_LOCK_WAIT=1 AHD_NATIVE_BIN="$BINDIR/bin/ahd" bash tests/run-all.sh ${BASEKEYS:+--no-abort} ${SHELL_ONLY:+--only "$SHELL_ONLY"} -o "$set" ${SHELL_PREFIX:-} ) > "$KD/shell.out" 2>&1
  rc=$?
  cat "$KD/shell.out" >> "$LOG"
  grep -q '^run-all: aborted: systemic' "$KD/shell.out" && SHELL_ABORTED=1
  [ -f "$set" ] || { ERRMSG="run-all wrote no result set (exit $rc)"; return 1; }
  awk '$2 == "FAIL" || $2 == "TIMEOUT" {printf "%s\t%s\t%s\n", $1, $1 ".sh", $2}' "$set" > "$FAILED"
  awk '{print $1}' "$set" > "$KD/suites.txt"
  shell_first_errors
  COUNTS="$(awk '{t++} $2 == "ok" {p++} $2 != "ok" {f++} END {print t + 0, p + 0, f + 0}' "$set")"
  if [ ! -s "$FAILED" ] && [ "$rc" != 0 ]; then ERRMSG="run-all exited $rc with no failed test"; return 1; fi
  return 0
}
# the contract shards alone, under a key of their own so that they never answer a request for shell; their result reads as part of shell
run_contracts() { SHELL_PREFIX=contracts-s; run_shell; }
rerun_contracts() { rerun_shell "$@"; }
# the list set: shell narrowed to the named suites, under its own key per list
# A name the clone's run-all would not run (pruned) is refused before the run; after it, the suites that ran must be the list.
# A clone's run-all from before --runnable (exit 2) is answered by run-set's own, as --limit is.
run_list() {
  local n rc
  for n in $LIST; do
    AH_TEST_SLOT_HELD=1 bash "$CLONE/${PREFIXREL}tests/run-all.sh" --runnable "$n" > /dev/null 2>&1; rc=$?
    [ "$rc" -le 1 ] || { AH_TEST_SLOT_HELD=1 AH_RUN_ALL_TESTS="$CLONE/${PREFIXREL}tests" bash "$HERE/run-all.sh" --runnable "$n" > /dev/null 2>&1; rc=$?; }
    [ "$rc" = 0 ] || { ERRMSG="unknown suite '$n': run-all at $SHA would not run it (pruned)"; return 1; }
  done
  SHELL_ONLY="$KD/list.only"; printf '%s\n' "$LIST" > "$SHELL_ONLY"
  run_shell || return 1
  [ "$(sort -u "$KD/suites.txt")" = "$LIST" ] || { ERRMSG="the suites that ran ($(sort -u "$KD/suites.txt" | paste -sd, -)) are not the list ($TESTS)"; return 1; }
}
rerun_list() { rerun_shell "$@"; }

# ---- the parts of the affected set, under keys of their own per base so that they never answer a request for another set or base
# What every test depends on, so a narrowed run could not stand for it: Ah.Core and the three other projects most projects reference, the test
# kit and the sandbox linked into every test project, the solution's build files, hooks.json, and what decides or carries out a run. Any of these in the diff makes the request a full one.
AFF_SHARED='(^|/)(native/src/Ah\.(Core|Abstractions|Config|Platform)/|native/tests/(Ah\.TestKit/|ProcessSandbox\.cs$)|native/(Ah\.sln|global\.json|nuget\.config|Directory\.)|hooks/hooks\.json$|tests/(run-set\.sh|run-set-tools\.mjs|keep-set\.sh|run-all\.sh|with-slot\.sh|lib-tokens\.sh|publish-bin\.sh|manifest\.json|contract-tools/(select|gate-select)\.mjs)$|scripts/dotnet-test\.sh$)'
AFF_PROJECTS=""; AFF_SHELL=""; AFF_WHY=""; AFF_BUILT=0
# aff_select: fills AFF_PROJECTS and AFF_SHELL (a file of suite names, maybe empty) from the clone's selector, or says in AFF_WHY why this must be full
aff_select() {
  local names hit sel out rc
  names="$(git -C "$REPO" diff --no-renames --name-only "$BASE" "$SHA")" || { AFF_WHY="the diff could not be read"; return 1; }
  hit="$(printf '%s\n' "$names" | grep -E "$AFF_SHARED" | head -1)"
  [ -z "$hit" ] || { AFF_WHY="the diff touches shared infrastructure: $hit"; return 1; }
  ensure_clone || { AFF_WHY="$ERRMSG"; return 1; }
  sel="$CLONE/${PREFIXREL}tests/contract-tools/gate-select.mjs"; out="$CLONE/.affected.txt"
  [ -f "$sel" ] || { AFF_WHY="the commit has no selector"; return 1; }
  node "$sel" --repo "$CLONE/$PREFIXREL" --base "$BASE" --sha "$SHA" > "$out" 2>&1
  rc=$?
  cat "$out" >&2
  hit="$(grep -m1 '^full ' "$out")"
  [ -z "$hit" ] || { AFF_WHY="the selector asks for $hit"; return 1; }
  [ "$rc" = 0 ] || { AFF_WHY="the selector exited $rc"; return 1; }
  AFF_PROJECTS="$(sed -n 's#^dotnet .*/\([^/]*\)\.csproj$#\1#p' "$out" | paste -sd' ' -)"
  # the shell suites: the changed ones, plus those whose manifest covers name a changed native/ path
  AFF_SHELL="$CLONE/.affected.shell"
  sed -n 's/^shell //p' "$out" > "$AFF_SHELL"
  for f in "$CLONE/${PREFIXREL}"tests/test-*.sh; do basename "$f" .sh; done | node "$CLONE/${PREFIXREL}tests/contract-tools/select.mjs" \
    --manifest "$CLONE/${PREFIXREL}tests/manifest.json" --names /dev/stdin --lane fast --native-covers \
    --changed-list "$(git -C "$CLONE/$PREFIXREL" diff --relative --no-renames --name-only "$BASE" "$SHA")" >> "$AFF_SHELL" 2>/dev/null \
    || { AFF_WHY="the shell selection failed"; return 1; }
  sort -u -o "$AFF_SHELL" "$AFF_SHELL"
  return 0
}
run_affdotnet() {
  local p paths=""
  for p in $AFF_PROJECTS; do paths="$paths $CLONE/${PREFIXREL}native/tests/$p/$p.csproj"; done
  export AH_DOTNET_TEST_PROJECTS="${paths# }"
  run_dotnet || return 1
  unset AH_DOTNET_TEST_PROJECTS
  AFF_BUILT=1
  printf '%s\n' $AFF_PROJECTS > "$KD/suites.txt"
}
rerun_affdotnet() { rerun_dotnet "$@"; }
run_affshell() { SHELL_ONLY="$AFF_SHELL"; run_shell; }
rerun_affshell() { rerun_shell "$@"; }
# one keep-set run in this clone: built by the dotnet part, so in the configuration that part builds, the default one
keep_set() { # <keep-set arguments>
  ( cd "$CLONE/$PREFIXREL" && env -u AH_TEST_HEAVY_SPLIT AH_KEEP_SET_CONFIG=Debug "$@" )
}
run_affkeep() {
  local rc nb=""
  [ -f "$CLONE/${PREFIXREL}tests/keep-set.sh" ] || { ERRMSG="SHA predates tests/keep-set.sh"; return 1; }
  [ "$AFF_BUILT" = 1 ] && nb=--no-build
  keep_set bash "$HERE/with-slot.sh" --eta large --tokens 4 bash tests/keep-set.sh $nb > "$KD/keep.out" 2>&1
  rc=$?
  cat "$KD/keep.out" >> "$LOG"
  [ "$rc" = 75 ] && { ERRMSG="the wait for a test slot ran out (exit 75)"; return 1; }
  awk '$1 == "FAIL" && NF == 3 {printf "%s:%s\t%s\tkeep-set failed\n", $2, $3, $3}' "$KD/keep.out" > "$FAILED"
  COUNTS="$(awk '$1 == "PASS" {p++} $1 == "FAIL" && NF == 3 {f++} END {print p + f + 0, p + 0, f + 0}' "$KD/keep.out")"
  if [ ! -s "$FAILED" ] && [ "$rc" != 0 ]; then ERRMSG="keep-set exited $rc with no failed row"; return 1; fi
  return 0
}
rerun_affkeep() { # <set> <name> <project>
  local rc
  keep_set AH_KEEP_SET_ONLY="$2" bash "$HERE/with-slot.sh" --eta "$RERUN_TMO" --tokens 1 timeout -k 5 "$RERUN_TMO" bash tests/keep-set.sh --no-build >> "$LOG" 2>&1
  rc=$?
  [ "$rc" = 75 ] && { ERRMSG="the wait for a test slot ran out (exit 75) before the rerun of $2"; return 3; }
  [ "$rc" = 0 ]
}

# diff_mode: how much of shell this request runs. all: anything but a full run with --base whose diff touches no shell or script file. A
# script is a .sh, .mjs, .js or .py anywhere, or any file under bin, hooks, scripts or tests outside native/ (a suite's fixtures and the
# contract cases are what the suites run). shards: such a diff that touches native/ outside native/tests, because package and build files
# change the binary too. none: any other such diff. --no-renames: a move lists both names, so moving code out of native/src still counts.
diff_mode() {
  local names
  [ -z "$BASEMODE" ] || { echo "$BASEMODE"; return; }
  [ -n "$BASE" ] && [ "$SET" = full ] || { echo all; return; }
  names="$(git -C "$REPO" diff --no-renames --name-only "$BASE" "$SHA")" || { echo all; return; }
  if printf '%s\n' "$names" | awk '/\.(sh|mjs|js|py)$/ || (!/(^|\/)native\// && /(^|\/)(bin|hooks|scripts|tests)\//) {f = 1} END {exit !f}'; then echo all
  elif printf '%s\n' "$names" | grep -E '(^|/)native/' | grep -qvE '(^|/)native/tests/'; then echo shards
  else echo none; fi
}
DIFF_MODE="$(diff_mode)"
# dotnet overlaps shell in a full run, so each takes the smaller grant; with no shell to overlap, dotnet takes the larger one
[ "$SET" = full ] && [ "$DIFF_MODE" != none ] && export AH_TEST_HEAVY_SPLIT=1

BASEKEYS=""
base_status_ok() { case "$(node "$TOOLS" field "$1" status)" in PASS|FLAKY-PASS|FAIL) return 0;; *) return 1;; esac; }
# the results of the base this run is judged against: a run with no shell at all needs only the base's dotnet, a shards run only its shards
base_comps() {
  if [ "$DIFF_MODE" = none ]; then echo dotnet
  elif [ "$DIFF_MODE" = shards ] && [ -f "$STORE/$BASE/contracts/result.json" ]; then echo dotnet contracts full
  elif [ "$SET" = full ]; then echo dotnet shell full
  else echo "${LKEY:-$SET}"; fi
}
# base_state: ok when every component result of the base exists, has a status that can be compared and was made where this run is;
# missing when one is absent or unusable; incomparable otherwise
base_state() {
  local c st=ok f
  for c in $(base_comps); do
    f="$STORE/$BASE/$c/result.json"
    if [ ! -f "$f" ] || ! base_status_ok "$f"; then echo missing; return; fi
    [ -z "$(node "$TOOLS" envcmp "$f")" ] || st=incomparable
  done
  echo "$st"
}
# base_why: the first reason a component of the base cannot be compared with this run, or nothing
base_why() {
  local c w
  for c in $(base_comps); do
    w="$(node "$TOOLS" envcmp "$STORE/$BASE/$c/result.json")"
    [ -z "$w" ] || { echo "$c: $w"; return; }
  done
}
resolve_base() {
  local st rc c
  # a base stored before the store moved is still found, whichever store this run uses
  for c in dotnet shell full; do adopt_entry "$BASE/$c"; done
  st="$(base_state)"
  if [ "$st" = missing ] || { [ "$st" = incomparable ] && [ "$FRESHBASE" = 1 ]; }; then
    echo "run-set: the base $BASE: $( [ "$st" = missing ] && echo 'no stored result, running it' || echo 'not comparable, running it again here')" >&2
    # the early abort stays off in a base run, so its failure list is complete
    # a base is run to the end and stored whole: no systemic limit, no rerun cap, no early abort; its isolated reruns stay on
    AH_SYSTEMIC_MAX=999999999 AH_SYSTEMIC_PCT=100 AH_RERUN_MAX=999999999 AH_RUN_SET_BASERUN=1 AH_RUN_SET_BASEMODE="$DIFF_MODE" AH_RUN_ALL_NOABORT=1 bash "$0" "$REPO" "$BASE" "$( [ "$DIFF_MODE" = none ] && echo dotnet || echo "$SET" )" ${LKEY:+--tests "$TESTS"} >&2
    rc=$?
    [ "$rc" = 75 ] && exit 75
    st="$(base_state)"
    [ "$st" = missing ] && echo "run-set: the base $BASE could not be run (exit $rc)" >&2
  fi
}

# resolve_one <set>: leaves R_STATUS, R_PATH and R_CACHED for a dotnet, shell or native request
resolve_one() {
  local set=$1 got=0 waited=0 deadline rset=$1 bset=$1 key=$1
  case "$set" in
    contracts)
      rset=shell; bset=shell
      # a base that ran only its shards keeps them under the same key; one with all of shell serves too
      [ -f "$STORE/$BASE/contracts/result.json" ] && bset=contracts;;
    affdotnet) rset=dotnet; key="$set-$BASE";;
    affshell) rset=shell; key="$set-$BASE";;
    affkeep) rset=keep; key="$set-$BASE";;
    list) bset=$LKEY; key=$LKEY;;
  esac
  KD="$STORE/$SHA/$key"
  deadline=$((SECONDS + WAIT))
  while :; do
    lookup "$key" && return 0
    if lock_take "$KD/.lock"; then
      lookup "$key" && { lock_drop "$KD/.lock"; return 0; }
      got=1; break
    fi
    [ "$SECONDS" -ge "$deadline" ] && { echo "run-set: waited ${WAIT}s for the run of $set at $SHA to finish" >&2; exit 75; }
    WAITED=1
    sleep 1
  done
  KSTART="$(now)"; STATUS=""; ERRMSG=""; COUNTS="0 0 0"; RERUN_N=0; SHELL_ABORTED=0; BASEKEYS=""
  LOG="$KD/run.log"; FAILED="$KD/failed.tsv"
  : > "$LOG"; : > "$FAILED"; : > "$KD/suites.txt"
  node "$TOOLS" env > "$KD/env.json"
  # the failures of the base decide what is NEW, but only when the base was made where this run is
  if [ -n "$BASE" ] && [ -f "$STORE/$BASE/$bset/result.json" ] && [ -z "$(node "$TOOLS" envcmp "$STORE/$BASE/$bset/result.json")" ] \
     && base_status_ok "$STORE/$BASE/$bset/result.json"; then
    BASEKEYS="$KD/base.keys"; node "$TOOLS" keys "$STORE/$BASE/$bset/result.json" > "$BASEKEYS"
  fi
  # a verdict judged the result that is going away
  rm -f "$KD/result.json" "$KD"/verdict-*.json
  rm -rf "$KD/fail"
  echo "run-set: running $set at $SHA" >&2
  if ensure_clone && "run_$set" && classify "$set"; then
    if [ "$STATUS" = PASS ]; then : > "$KD/failures.tsv"; : > "$KD/flaky.tsv"; fi
    node "$TOOLS" result --sha "$SHA" --set "$rset" --status "$STATUS" --started "$KSTART" --ended "$(now)" --log "$LOG" \
      --counts "$COUNTS" --failures "$KD/failures.tsv" --flaky "$KD/flaky.tsv" --env "$KD/env.json" --suites "$KD/suites.txt" > "$KD/.result.$$" && mv "$KD/.result.$$" "$KD/result.json"
    R_STATUS="$STATUS"; R_PATH="$KD/result.json"; R_CACHED=0
    lock_drop "$KD/.lock"
  else
    error_result "$key" "${ERRMSG:-the run could not complete}"
  fi
}

# ---- main
if [ "$BASERUN" = 0 ]; then mkdir -p "$STORE" 2>/dev/null; adopt_legacy; prune_store; fi
[ -z "$BASE" ] || [ "$SET" = affected ] || resolve_base
case "$SET" in
  dotnet|shell|native|list)
    resolve_one "$SET"
    ;;
  full|affected)
    KEY=full; [ "$SET" = affected ] && KEY="affected-$BASE"
    KD="$STORE/$SHA/$KEY"
    deadline=$((SECONDS + WAIT))
    while :; do
      lookup "$KEY" && break
      if lock_take "$KD/.lock"; then
        lookup "$KEY" && { lock_drop "$KD/.lock"; break; }
        FSTART="$(now)"; PARTS=(); ANYCACHED=1
        # a base with all of its shell stored is made whole from it, not from shards run again
        [ "$BASERUN" = 1 ] && [ "$DIFF_MODE" = shards ] && lookup shell && DIFF_MODE=all
        if [ "$SET" = affected ]; then
          LOG="$KD/run.log"
          if ! aff_select; then
            echo "run-set: affected becomes full: $AFF_WHY" >&2
            lock_drop "$KD/.lock"; store_rm "$KD"
            bash "$0" "$REPO" "$SHA" full --base "$BASE" $( [ "$FRESH" = 1 ] && echo --fresh )
            exit $?
          fi
          echo "run-set: affected at $SHA from $BASE: projects: $AFF_PROJECTS; shell suites: $(paste -sd' ' - < "$AFF_SHELL")" >&2
          # with a shell part dotnet overlaps it, as in full
          [ -s "$AFF_SHELL" ] && export AH_TEST_HEAVY_SPLIT=1
        fi
        # dotnet overlaps the other two. It gets its own clone because a publish restores into the same artifacts as its build, and a
        # sandbox daemon is attributed to a run by the checkout it names. Its lines are held back so they cannot mix with the others.
        DJOB="$KD/.dotnet.$$"
        (
          trap cleanup EXIT; trap 'exit 130' INT TERM HUP
          MYLOCKS=()
          exec > "$DJOB.out" 2>&1
          if [ "$SET" = affected ]; then
            # the selection's clone stays the shell part's; this job builds in its own
            CLONE=""; BINDIR=""
            resolve_one affdotnet
            printf '%s\n%s\n%s\n' "$R_STATUS" "$R_PATH" "$R_CACHED" > "$DJOB.res"
            resolve_one affkeep
            printf '%s\n%s\n%s\n' "$R_STATUS" "$R_PATH" "$R_CACHED" > "$DJOB.keep"
          else
            resolve_one dotnet
            printf '%s\n%s\n%s\n' "$R_STATUS" "$R_PATH" "$R_CACHED" > "$DJOB.res"
          fi
        ) &
        DOTNET_JOB=$!
        NOTRUN=()
        SHELLPART=shell
        case "$DIFF_MODE" in
          none) NOTRUN=(--not-run shell); SHELLPART=""
                echo "run-set: shell not run: the diff from $BASE touches no shell or script file" >&2;;
          shards) NOTRUN=(--not-run shell); SHELLPART=contracts
                echo "run-set: of shell only the contract shards run: ${BASE:+the diff from $BASE touches native/ outside native/tests and no shell or script file}${BASE:-this base serves a run that needs no more}" >&2;;
          *) echo "run-set: dotnet runs alongside shell; its lines print when it ends" >&2;;
        esac
        if [ "$SET" = affected ]; then
          NOTRUN=(--not-run dotnet,shell --affected-base "$BASE"); SHELLPART=affshell
          [ -s "$AFF_SHELL" ] || SHELLPART=""
        fi
        if [ -n "$SHELLPART" ]; then
          resolve_one "$SHELLPART"
          [ "$R_CACHED" = 1 ] || ANYCACHED=0
          PARTS+=("$R_PATH")
        fi
        wait "$DOTNET_JOB"; DRC=$?
        DOTNET_JOB=""
        cat "$DJOB.out" >&2
        [ "$DRC" = 75 ] && exit 75
        if [ "$SET" = affected ]; then
          if [ -f "$DJOB.keep" ]; then
            { read -r R_STATUS; read -r R_PATH; read -r R_CACHED; } < "$DJOB.keep"
          else
            KSTART="$FSTART"; error_result "affkeep-$BASE" "keep-set ended without a result (exit $DRC)"
          fi
          rm -f "$DJOB.keep"
          [ "$R_CACHED" = 1 ] || ANYCACHED=0
          PARTS+=("$R_PATH")
        fi
        if [ -f "$DJOB.res" ]; then
          { read -r R_STATUS; read -r R_PATH; read -r R_CACHED; } < "$DJOB.res"
        else
          KSTART="$FSTART"; error_result dotnet "the dotnet set ended without a result (exit $DRC)"
        fi
        rm -f "$DJOB.out" "$DJOB.res"
        [ "$R_CACHED" = 1 ] || ANYCACHED=0
        PARTS=("$R_PATH" ${PARTS[@]+"${PARTS[@]}"})
        KD="$STORE/$SHA/$KEY"
        if node "$TOOLS" merge --sha "$SHA" --set "$SET" --started "$FSTART" --ended "$(now)" --log "$KD/run.log" ${NOTRUN[@]+"${NOTRUN[@]}"} ${PARTS[@]+"${PARTS[@]}"} > "$KD/.result.$$"; then
          printf '%s is made of parts; the log of each is in its own key under %s/%s\n' "$SET" "$STORE" "$SHA" > "$KD/run.log"
          if [ "$(node "$TOOLS" field "$KD/.result.$$" status)" = ERROR ]; then
            R_PATH="$STORE/errors/$SHA-$SET-$$.json"; mkdir -p "$STORE/errors"; mv "$KD/.result.$$" "$R_PATH"; R_STATUS=ERROR; R_CACHED=0
            lock_drop "$KD/.lock"; store_rm "$KD"
          else
            rm -f "$KD"/verdict-*.json; mv "$KD/.result.$$" "$KD/result.json"
            R_STATUS="$(node "$TOOLS" field "$KD/result.json" status)"; R_PATH="$KD/result.json"; R_CACHED=0
            [ "$ANYCACHED" = 1 ] && R_CACHED=1
            lock_drop "$KD/.lock"
          fi
        else
          lock_drop "$KD/.lock"; R_STATUS=ERROR; R_PATH=""; R_CACHED=0
        fi
        break
      fi
      [ "$SECONDS" -ge "$deadline" ] && { echo "run-set: waited ${WAIT}s for the run of $SET at $SHA to finish" >&2; exit 75; }
      WAITED=1
      sleep 1
    done
    ;;
esac

if [ "$R_CACHED" = 1 ]; then CACHED=yes; else CACHED=no; fi
echo "run-set: $R_STATUS set=$SET sha=$SHA cached=$CACHED result=$R_PATH"
if [ -n "$BASE" ] && [ "$SET" != affected ] && [ "$R_STATUS" != ERROR ] && [ -f "$R_PATH" ]; then
  VF="$R_PATH.verdict.$$"
  WHY="$(base_why)"
  # a run with no shell at all may have a base with no full result: its dotnet result is all that run is judged against
  BASE_RESULT="$STORE/$BASE/${LKEY:-$SET}/result.json"
  [ "$DIFF_MODE" = none ] && [ ! -f "$BASE_RESULT" ] && BASE_RESULT="$STORE/$BASE/dotnet/result.json"
  node "$TOOLS" compare --head "$R_PATH" --base "$BASE_RESULT" --basesha "$BASE" ${WHY:+--incomparable "the base: $WHY"} > "$VF"
  VRC=$?
  # the verdict is its own file beside result.json, one per base, written under the key lock by a temp file and a rename
  KD="$(dirname "$R_PATH")"; VW=0
  until lock_take "$KD/.lock"; do VW=$((VW + 1)); [ "$VW" -ge 120 ] && break; sleep 1; done
  if [ "$VW" -ge 120 ]; then echo "run-set: the verdict could not be stored: $KD/.lock stayed held" >&2; rm -f "$VF"; exit 3; fi
  node "$TOOLS" annotate "$SHA" "$SET" "$VF" "$R_PATH" "$BASE_RESULT" > "$KD/.verdict.$$" && mv "$KD/.verdict.$$" "$KD/verdict-$BASE.json"
  lock_drop "$KD/.lock"
  node "$TOOLS" verdictline "$VF"
  rm -f "$VF"
  exit "$VRC"
fi
case "$R_STATUS" in PASS|FLAKY-PASS) exit 0;; FAIL|SYSTEMIC) exit 1;; *) exit 3;; esac
