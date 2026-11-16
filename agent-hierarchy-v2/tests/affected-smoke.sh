#!/bin/bash
# agent-hierarchy — the smoke for a change: the test classes and shell suites the diff reaches, not a hand-picked --filter.
#
#   tests/affected-smoke.sh --base <ref> [--repo <plugin root>] [--print] [--why]
#
#   --base REF   the branch or sha the change is measured from (required); the diff is REF...HEAD plus uncommitted and untracked files
#   --repo DIR   the work tree under test, the directory that holds native/ and tests/ (default: the nearest one up from the cwd, else the
#                cwd's git toplevel); its tests/run-all.sh, with-slot.sh and selector are the ones used
#   --print      list the selection and run nothing
#   --why        say, per class, what selected it
#
# Selection (tests/contract-tools/affected-select.mjs): every test class whose text names a changed C# type, or a changed
# non-private member, plus <Type>Tests, plus a changed test file's own classes; the Core, Config and Architecture test projects
# in full; and the shell suites whose manifest `covers` globs match a changed path (select.mjs --native-covers, so a native path
# selects only the suites covering it; a docs or contract change adds test-contracts*), each run by `run-all.sh --exact`. The C# runs are one `dotnet test` per project with a --filter, inside a single
# tests/with-slot.sh job. Exit 0 iff every part passes. The shell part needs the native binary ($AHD_NATIVE_BIN); without one it is
# skipped with a notice, because run-all.sh would exit 1 and run nothing.
# Exit 2: unknown base or bad usage. Exit 3: the selection is too broad (more than 250 classes, the user's ruling, or three projects of which more
# than 80% is selected): it is QA's full suite, not a smoke, and nothing runs. This script never runs an unfiltered project outside
# the three above, the full suite, or run-all.sh without --changed.

HERE="$(cd "$(dirname "$0")" && pwd)"
PLUGIN="$(cd "$HERE/.." && pwd)"
BASE=""; REPO=""; PRINT=0; WHY=""
USAGE="usage: affected-smoke.sh --base <ref> [--repo <plugin root>] [--print] [--why]"

while [ $# -gt 0 ]; do
  case "$1" in
    --base) [ $# -ge 2 ] || { echo "$USAGE" >&2; exit 2; }; BASE=$2; shift 2;;
    --repo) [ $# -ge 2 ] || { echo "$USAGE" >&2; exit 2; }; REPO=$2; shift 2;;
    --print) PRINT=1; shift;;
    --why) WHY=--why; shift;;
    *) echo "$USAGE" >&2; exit 2;;
  esac
done
[ -n "$BASE" ] || { echo "$USAGE" >&2; exit 2; }

# the work tree under test: the nearest directory up from the cwd that holds native/src and tests/run-all.sh, else the cwd's git toplevel
if [ -z "$REPO" ]; then
  d="$(pwd -P)"
  while [ "$d" != "/" ]; do
    if [ -d "$d/native/src" ] && [ -f "$d/tests/run-all.sh" ]; then REPO=$d; break; fi
    d="$(dirname "$d")"
  done
  [ -n "$REPO" ] || REPO="$(git rev-parse --show-toplevel 2>/dev/null)" || REPO="$PLUGIN"
fi
SLOT="$REPO/tests/with-slot.sh"; [ -x "$SLOT" ] || SLOT="$PLUGIN/tests/with-slot.sh"
SELECT="$REPO/tests/contract-tools/affected-select.mjs"; [ -f "$SELECT" ] || SELECT="$HERE/contract-tools/affected-select.mjs"

OUT="$(mktemp -d "${TMPDIR:-/tmp}/affected-smoke.XXXXXX")"
case "$OUT" in */affected-smoke.??????) ;; *) echo "mktemp returned '$OUT'; aborting" >&2; exit 2;; esac
trap 'rm -rf "$OUT"' EXIT

node "$SELECT" --repo "$REPO" --base "$BASE" --out "$OUT" $WHY
rc=$?
[ $rc = 0 ] || exit $rc
[ $PRINT = 1 ] && exit 0

{
  echo 'cd "$1/native" || exit 1'
  echo 'rc=0'
  while IFS=$'\t' read -r proj filter; do
    [ -n "$proj" ] || continue
    if [ -z "$filter" ]; then
      printf 'dotnet test tests/%s || rc=1\n' "$proj"
    else
      printf 'dotnet test tests/%s --filter %q || rc=1\n' "$proj" "$filter"
    fi
  done < "$OUT/runs.tsv"
  echo 'exit $rc'
} > "$OUT/run.sh"

FAIL=0
MSBUILDDISABLENODEREUSE=1 "$SLOT" --eta medium bash "$OUT/run.sh" "$REPO" || FAIL=1

SKIPPED=0
if [ -s "$OUT/shell.txt" ]; then
  if [ -z "${AHD_NATIVE_BIN:-}" ] || [ ! -x "$AHD_NATIVE_BIN" ]; then
    # run-all.sh runs every suite against the native binary and exits 1 without one
    SKIPPED=$(grep -c . "$OUT/shell.txt")
    echo "affected-smoke: shell part SKIPPED: no native binary (AHD_NATIVE_BIN unset or not executable); run it where one is built"
  else
    while IFS= read -r suite; do
      [ -n "$suite" ] || continue
      bash "$REPO/tests/run-all.sh" --exact "${suite#test-}" || FAIL=1
    done < "$OUT/shell.txt"
  fi
fi

if [ $FAIL != 0 ]; then echo "affected-smoke: FAILED"
elif [ $SKIPPED != 0 ]; then echo "affected-smoke: passed (shell part SKIPPED: $SKIPPED suites)"
else echo "affected-smoke: passed"
fi
exit $FAIL
