#!/bin/bash
# agent-hierarchy — ui-e4 --record-baseline writes the whole baseline --compare reads, so a recorded baseline compares
# clean against its own run, and a run on another Chrome binary fails with the re-record hint. No Chrome is started.
# Usage: bash tests/test-e4-baseline.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
E4="$PLUGIN/tests/perf/ui-e4.mjs"
T=$(mktemp -d "${TMPDIR:-/tmp}/ahe4base.XXXXXX"); [ -n "$T" ] || exit 1
hermetic_on_exit 'rm -rf "$T"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n"; fi; }

# A --fixture both run file built from the checked-in baseline: its small entries, and three copies of each realistic median.
node -e '
const b = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
const real = b.realistic.median;
const run = { label: "run", at: "2026-01-01T00:00:00Z", chrome: "/opt/headless-shell-1248/chrome-headless-shell", runs_per_mode: 3,
  fixtures: ["small", "realistic"], runs: [...b.runs, ...real, ...real, ...real], median: [...b.median, ...real] };
require("fs").writeFileSync(process.argv[2], JSON.stringify(run));
const other = { ...run, chrome: "/opt/headless-shell-1249/chrome-headless-shell" };
require("fs").writeFileSync(process.argv[3], JSON.stringify(other));
const smallOnly = { ...run, fixtures: ["small"], runs: b.runs, median: b.median };
require("fs").writeFileSync(process.argv[4], JSON.stringify(smallOnly));
const on = (l) => l.filter((m) => m.diagnostics === "on");
require("fs").writeFileSync(process.argv[5], JSON.stringify({ ...run, runs: on(run.runs), median: on(run.median) }));
const { chrome, ...noChrome } = run;
require("fs").writeFileSync(process.argv[6], JSON.stringify(noChrome));
' "$PLUGIN/tests/perf/baseline-9a71e90c.json" "$T/run.json" "$T/other.json" "$T/small.json" "$T/diagon.json" "$T/nochrome.json"

node "$E4" --record-baseline "$T/base.json" "$T/run.json" --sha abc >"$T/rec.out" 2>&1; RC=$?
check "--record-baseline writes a baseline from a both-fixture run" '[ $RC = 0 ] && [ -s "$T/base.json" ]'
SHAPE=$(node -e 'const b = require(process.argv[1]); console.log(b.chrome, b.median.length, b.runs.length, b.realistic.median.length, b.realistic.sha)' "$T/base.json")
check "...holding the run's chrome, small entries and realistic entry" '[ "$SHAPE" = "/opt/headless-shell-1248/chrome-headless-shell 2 6 2 abc" ]'

node "$E4" --compare "$T/base.json" "$T/run.json" >"$T/same.out" 2>&1; RC=$?
check "a recorded baseline compares clean on the same binary" '[ $RC = 0 ] && ! grep -q REGRESSION "$T/same.out"'

node "$E4" --compare "$T/base.json" "$T/other.json" >"$T/other.out" 2>&1; RC=$?
check "a new build of the same binary fails the compare" '[ $RC = 1 ] && grep -q "a new Chrome or Playwright build counts as a new binary" "$T/other.out"'

cp "$T/base.json" "$T/keep.json"
node "$E4" --record-baseline "$T/base.json" "$T/small.json" >"$T/small.out" 2>&1; RC=$?
check "a run without the realistic fixture is refused and the baseline is untouched" '[ $RC = 2 ] && cmp -s "$T/base.json" "$T/keep.json"'
node "$E4" --record-baseline "$T/base.json" "$T/diagon.json" >"$T/diagon.out" 2>&1; RC=$?
check "a run with both fixtures but only diagnostics on is refused and the baseline is untouched" '[ $RC = 2 ] && cmp -s "$T/base.json" "$T/keep.json"'
node "$E4" --record-baseline "$T/base.json" "$T/nochrome.json" >"$T/nochrome.out" 2>&1; RC=$?
check "a run that does not name its chrome is refused and the baseline is untouched" '[ $RC = 2 ] && cmp -s "$T/base.json" "$T/keep.json"'

echo "passed $PASS, failed $FAIL"
[ "$FAIL" -eq 0 ]
