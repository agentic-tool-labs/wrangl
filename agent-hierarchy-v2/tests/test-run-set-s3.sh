#!/bin/bash
# agent-hierarchy — tests/run-set.sh, part 3 of 3 (section 12, usage, the rerun slot, b166 and the affected set): fail fast, the
# usage errors, a rerun that cannot get a slot, the failure diffs kept beside a result, and the affected set. Stand-ins and store as
# in part 1; the setup is tests/lib-run-set-test.sh's.
# Usage: bash tests/test-run-set-s3.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-run-set-test.sh"

# 12. fail fast: a systemic stop, the rerun cap, parallel reruns, the rerun timeout
mkcommit systemic-count; printf 'Ah.Fake.Tests.C.T1\nAh.Fake.Tests.C.T2\nAh.Fake.Tests.C.U\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.T1\n' > "$ST/passes-alone"
AH_SYSTEMIC_MAX=2 rs "$SHA" dotnet
check "R1: more failures than AH_SYSTEMIC_MAX is SYSTEMIC (exit 1), stored, and nothing is rerun" '[ $RC = 1 ] && [ "$(rjson dotnet status)" = SYSTEMIC ] && printf %s "$OUT" | grep -q "^run-set: SYSTEMIC set=dotnet" && [ "$(calls dotnet.reruns)" = 0 ]'
check "R1: the failures are grouped by first error line with numbers stripped: a group of two (names listed) and a group of one" 'printf %s "$OUT" | grep -q "2 x boom Ah.Fake.Tests.C.T  *\[Ah.Fake.Tests.C.T1, Ah.Fake.Tests.C.T2\]" && printf %s "$OUT" | grep -q "1 x boom Ah.Fake.Tests.C.U  *\[Ah.Fake.Tests.C.U\]"'
AH_SYSTEMIC_MAX=2 rs "$SHA" dotnet
check "R1: a stored SYSTEMIC is not reused: the next request runs again" '[ "$(calls dotnet.runs)" = 2 ]'
AH_SYSTEMIC_MAX=3 rs "$SHA" dotnet
check "R1: as many failures as AH_SYSTEMIC_MAX is not systemic (nearest non-match): they are rerun" '[ "$(rjson dotnet status)" = FAIL ] && [ "$(calls dotnet.reruns)" = 3 ]'
mkcommit systemic-pct; printf 'Ah.Fake.Tests.C.A\nAh.Fake.Tests.C.B\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\nAh.Fake.Tests.C.B\n' > "$ST/passes-alone"
AH_SYSTEMIC_MAX=50 AH_SYSTEMIC_PCT=30 AH_SYSTEMIC_PCT_MIN=1 rs "$SHA" dotnet
check "R1: 2 of 5 failing is more than 30 percent: SYSTEMIC, no rerun" '[ $RC = 1 ] && [ "$(rjson dotnet status)" = SYSTEMIC ] && [ "$(calls dotnet.reruns)" = 0 ]'
AH_SYSTEMIC_MAX=50 AH_SYSTEMIC_PCT=40 AH_SYSTEMIC_PCT_MIN=1 rs "$SHA" dotnet --fresh
check "R1: 2 of 5 failing is not more than 40 percent (nearest non-match): rerun, FLAKY-PASS" '[ $RC = 0 ] && [ "$(rjson dotnet status)" = FLAKY-PASS ] && [ "$(calls dotnet.reruns)" = 2 ]'
mkcommit systemic-shell; printf 'test-s1\ntest-s2\n' > "$ST/shell.fails"
AH_SYSTEMIC_MAX=1 rs "$SHA" shell
check "R1 applies to the shell set: SYSTEMIC, run-all was not asked for any --exact rerun" '[ $RC = 1 ] && [ "$(rjson shell status)" = SYSTEMIC ] && ! grep -q -- "--exact" "$ST/shell.calls"'

mkcommit pct-min-19; printf 'Ah.Fake.Tests.C.A\nAh.Fake.Tests.C.B\nAh.Fake.Tests.C.C\nAh.Fake.Tests.C.D\n' > "$ST/fails"; cp "$ST/fails" "$ST/passes-alone"; echo 15 > "$ST/dotnet.passed"
AH_SYSTEMIC_PCT=15 rs "$SHA" dotnet
check "a set of 19 tests with 4 failing (21 percent) is not SYSTEMIC, the percentage rule needs 20 tests: they are rerun" '[ "$(rjson dotnet status)" = FLAKY-PASS ] && [ "$(calls dotnet.reruns)" = 4 ]'
mkcommit pct-min-20; cp "$SB/st-pct-min-19/fails" "$ST/fails"; echo 16 > "$ST/dotnet.passed"
AH_SYSTEMIC_PCT=15 rs "$SHA" dotnet
check "a set of 20 tests with the same 4 failing is SYSTEMIC, no rerun" '[ $RC = 1 ] && [ "$(rjson dotnet status)" = SYSTEMIC ] && [ "$(calls dotnet.reruns)" = 0 ]'
mkcommit full-systemic; : > "$ST/fails"; : > "$ST/native.fails"; printf 'test-s1\ntest-s2\n' > "$ST/shell.fails"
AH_SYSTEMIC_MAX=1 rs "$SHA" full
check "a full run with a SYSTEMIC shell part is SYSTEMIC (exit 1), never PASS" '[ $RC = 1 ] && [ "$(rjson shell status)" = SYSTEMIC ] && [ "$(rjson full status)" = SYSTEMIC ]'
mkcommit rerun-cap; printf 'Ah.Fake.Tests.C.A\nAh.Fake.Tests.C.B\nAh.Fake.Tests.C.C\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\nAh.Fake.Tests.C.B\nAh.Fake.Tests.C.C\n' > "$ST/passes-alone"
AH_SYSTEMIC_MAX=50 AH_RERUN_MAX=2 rs "$SHA" dotnet
check "R2: with AH_RERUN_MAX=2 only two of three failures are rerun" '[ "$(calls dotnet.reruns)" = 2 ] && ! grep -qx "Ah.Fake.Tests.C.C" "$ST/dotnet.reruns"'
check "R2: the third is reported not-rerun, counted as failed: the set is FAIL" '[ $RC = 1 ] && [ "$(rjson dotnet status)" = FAIL ] && rjson dotnet failures | grep -q "not-rerun" && rjson dotnet failures | grep -q "Ah.Fake.Tests.C.C" && printf %s "$OUT" | grep -q "1 failures of dotnet not-rerun"'
mkcommit rerun-cap-ok; cp "$SB/st-rerun-cap/fails" "$ST/fails"; cp "$SB/st-rerun-cap/passes-alone" "$ST/passes-alone"
AH_SYSTEMIC_MAX=50 AH_RERUN_MAX=3 rs "$SHA" dotnet
check "R2: AH_RERUN_MAX=3 reruns all three (nearest non-match): FLAKY-PASS" '[ $RC = 0 ] && [ "$(calls dotnet.reruns)" = 3 ] && [ "$(rjson dotnet status)" = FLAKY-PASS ]'

maxrun() { awk '/^start/ {n++; if (n > m) m = n} /^end/ {n--} END {print m + 0}' "$1"; }
mkcommit rerun-parallel; printf 'test-s1\ntest-s2\ntest-s3\n' > "$ST/shell.fails"; printf 'test-s1\ntest-s2\ntest-s3\n' > "$ST/shell.passes-alone"; echo 1 > "$ST/shell.rerun-sleep"
AH_TEST_SLOTS=0 AH_RERUN_JOBS=3 rs "$SHA" shell
check "R3: with AH_RERUN_JOBS=3 the three reruns overlap (more than one at a time)" '[ $RC = 0 ] && [ "$(rjson shell status)" = FLAKY-PASS ] && [ "$(grep -c -- "--exact" "$ST/shell.calls")" = 3 ] && [ "$(maxrun "$ST/shell.events")" -ge 2 ]'
mkcommit rerun-serial; cp "$SB/st-rerun-parallel/shell.fails" "$ST/"; cp "$SB/st-rerun-parallel/shell.passes-alone" "$ST/"; echo 1 > "$ST/shell.rerun-sleep"
AH_TEST_SLOTS=0 AH_RERUN_JOBS=1 rs "$SHA" shell
check "R3: AH_RERUN_JOBS=1 runs them one at a time (nearest non-match): never two at once" '[ $RC = 0 ] && [ "$(grep -c "^start" "$ST/shell.events")" = 3 ] && [ "$(maxrun "$ST/shell.events")" = 1 ]'

mkcommit rerun-timeout; printf 'Ah.Fake.Tests.C.A\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\n' > "$ST/passes-alone"; echo 8 > "$ST/dotnet.rerun-sleep"
AH_RERUN_TIMEOUT=2 rs "$SHA" dotnet
check "R4: a rerun that outlives AH_RERUN_TIMEOUT failed again and was killed before it ended: the set is FAIL" '[ $RC = 1 ] && [ "$(nfail dotnet)" = 1:1 ] && grep -q "^start" "$ST/dotnet.rerun-events" && ! grep -q "^end" "$ST/dotnet.rerun-events"'
mkcommit rerun-in-time; printf 'Ah.Fake.Tests.C.A\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\n' > "$ST/passes-alone"; echo 1 > "$ST/dotnet.rerun-sleep"
AH_RERUN_TIMEOUT=5 rs "$SHA" dotnet
check "R4: a rerun that finishes inside the timeout counts as it did before (nearest non-match): FLAKY-PASS, it ended" '[ $RC = 0 ] && [ "$(rjson dotnet status)" = FLAKY-PASS ] && grep -q "^end" "$ST/dotnet.rerun-events"'

# the rerun timeout starts when the rerun holds its slot, not while it waits for one
mkcommit rerun-slot-wait; printf 'Ah.Fake.Tests.C.A\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\n' > "$ST/passes-alone"
AH_TEST_SLOTS=1 bash "$PLUGIN/tests/with-slot.sh" --eta 60 sleep 6 &
HOLDER=$!
sleep 1
AH_TEST_SLOTS=1 AH_RERUN_TIMEOUT=2 rs "$SHA" dotnet
wait "$HOLDER"
check "R4: a rerun that waits for a slot longer than AH_RERUN_TIMEOUT still counts by its own result: it passed alone, FLAKY-PASS" '[ $RC = 0 ] && [ "$(rjson dotnet status)" = FLAKY-PASS ] && grep -q "^end" "$ST/dotnet.rerun-events"'

# a run-all that stopped early is SYSTEMIC whatever the thresholds say
mkcommit shell-aborted; printf 'test-s1\ntest-s2\n' > "$ST/shell.fails"; printf 'test-s1\ntest-s2\n' > "$ST/shell.passes-alone"; : > "$ST/shell.abort"
AH_ABORT_MIN=10 rs "$SHA" shell
check "a run-all that printed 'aborted: systemic' makes the shell set SYSTEMIC (exit 1) although 2 failures are under every limit, and nothing is rerun" '[ $RC = 1 ] && [ "$(rjson shell status)" = SYSTEMIC ] && ! grep -q -- "--exact" "$ST/shell.calls"'
mkcommit shell-not-aborted; printf 'test-s1\ntest-s2\n' > "$ST/shell.fails"; printf 'test-s1\ntest-s2\n' > "$ST/shell.passes-alone"
rs "$SHA" shell
check "the same failures without the aborted line are rerun as before (nearest non-match): FLAKY-PASS" '[ $RC = 0 ] && [ "$(rjson shell status)" = FLAKY-PASS ]'

# usage
rs "$SHA" shell --base zzz
check "--base needs a full 40-hex commit (exit 2, nothing run)" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "40-hex"'
rs "$SHA" shell --base "$SHA"
check "--base must differ from the sha (exit 2)" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "differ"'
rs "$SHA" shell --fresh-base
check "--fresh-base without --base is a usage error (exit 2)" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "needs --base"'
rs "$SHA" shell --base
check "--base without a value is a usage error (exit 2)" '[ $RC = 2 ]'

OUT="$(bash "$RUNSET" --help 2>&1)"
# a rerun that cannot get a slot in time is ERROR, not FAIL: the slot is held past AH_TEST_SLOT_WAIT
mkcommit rerun-slot-75; printf 'Ah.Fake.Tests.C.A\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\n' > "$ST/passes-alone"
AH_TEST_SLOTS=1 bash "$PLUGIN/tests/with-slot.sh" --eta 60 sleep 8 &
HOLDER=$!
sleep 1
AH_TEST_SLOTS=1 AH_TEST_SLOT_WAIT=2 rs "$SHA" dotnet
wait "$HOLDER"
check "a rerun whose slot wait ran out (exit 75) makes the set ERROR (exit 3), says so, and stores nothing" '[ $RC = 3 ] && printf %s "$OUT" | grep -q "the wait for a test slot ran out (exit 75) before the rerun" && [ ! -f "$AH_RESULTS_DIR/$SHA/dotnet/result.json" ]'
mkcommit rerun-slot-free; printf 'Ah.Fake.Tests.C.A\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\n' > "$ST/passes-alone"
AH_TEST_SLOTS=1 AH_TEST_SLOT_WAIT=2 rs "$SHA" dotnet
check "the same run with the slot free (nearest non-match) reruns and is FLAKY-PASS" '[ $RC = 0 ] && [ "$(rjson dotnet status)" = FLAKY-PASS ]'

rs "$SHA" native
check "native is no longer a set: a usage error, exit 2" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "unknown set"'
OUT="$(bash "$RUNSET" --help 2>&1)"
check "the usage text names every threshold variable" 'for v in AH_SYSTEMIC_MAX AH_SYSTEMIC_PCT AH_SYSTEMIC_PCT_MIN AH_RERUN_MAX AH_RERUN_JOBS AH_RERUN_TIMEOUT AH_TEST_SLOTS --base --fresh-base; do printf %s "$OUT" | grep -q "$v" || return 1; done'

# b166: a suite's failure diffs stay beside the result, and first_error is the real line
FE="$SB/firsterror"; mkdir -p "$FE/d/lifecycle" "$FE/empty"
printf 'PASS: no error here\nsome output\nFAIL: the real one\nFAIL: a later one\n' > "$FE/log"
OUT="$(node "$TOOLS" firsterror "$FE/log")"
check "firsterror: the log's first FAIL line" '[ "$OUT" = "FAIL: the real one" ]'
printf 'PASS: no failure here\nPASS: an error word\n' > "$FE/passes"
OUT="$(node "$TOOLS" firsterror "$FE/passes")"
check "...a PASS line that says error or failure is never it (nearest non-match)" '[ -z "$OUT" ]'
printf 'PASS: fine\nnative error: boom\n' > "$FE/noprefix"
OUT="$(node "$TOOLS" firsterror "$FE/noprefix" "$FE/empty")"
check "...with no FAIL line and no diff, the first line that mentions an error" '[ "$OUT" = "native error: boom" ]'
printf -- '--- expected\n+++ actual\n {\n-  "exit": 0\n+  "exit": 3\n }\n' > "$FE/d/lifecycle/12-x.diff"
printf 'other\n' > "$FE/d/lifecycle/13-y.diff"
OUT="$(node "$TOOLS" firsterror "$FE/log" "$FE/d")"
check "...a kept diff wins: its changed lines, not its headers or context, and how many more there are" '[ "$OUT" = "lifecycle/12-x: -  \"exit\": 0 | +  \"exit\": 3 (+1 more)" ]'
OUT="$(node "$TOOLS" firsterror "$FE/missing" "$FE/missing")"; RC=$?
check "...nothing to read prints nothing and is not an error" '[ $RC = 0 ] && [ -z "$OUT" ]'

mkcommit keepdiff; printf 'test-s1\ntest-s2\n' > "$ST/shell.fails"
printf -- '+  "exit": 3\n' > "$ST/shell.diff"; printf 'PASS: no error\nFAIL: s1 real line\n' > "$ST/shell.log"
rs "$SHA" shell
KEPT="$AH_RESULTS_DIR/$SHA/shell"
check "run-set keeps a suite's diffs beside its result.json" '[ -f "$KEPT/result.json" ] && [ -f "$KEPT/fail/test-s2/lifecycle/12-x.diff" ]'
check "...first_error of that suite holds the diff's line" 'grep -q "lifecycle/12-x: +  .*exit.*: 3" "$KEPT/result.json"'
check "...first_error of a suite with no diff is its first FAIL line, not a PASS line" 'grep -q "FAIL: s1 real line" "$KEPT/result.json" && ! grep -q "PASS: no error" "$KEPT/result.json"'
rm -f "$ST/shell.diff"; rs "$SHA" shell --fresh
check "...a fresh run takes the old diffs away with the old result (nearest non-match)" '[ -f "$KEPT/result.json" ] && [ ! -e "$KEPT/fail/test-s2" ]'

# ---- the affected set: stand-ins for the selector and the keep-set, on a commit of their own
aff_commit() { # <scenario>: a commit with the two stand-ins; sets SHA and ST
  NEXT=$((NEXT + 1)); ST="$SB/st-$1"; stubs "$ST" yes
  local d="$REPO/${PFX}tests"
  mkdir -p "$d/contract-tools"
  cat > "$d/contract-tools/gate-select.mjs" <<'SELECT'
import { existsSync, readFileSync, writeFileSync } from "node:fs";
const ST = "@ST@";
const said = (f, d) => existsSync(`${ST}/${f}`) ? readFileSync(`${ST}/${f}`, "utf8") : d;
writeFileSync(`${ST}/select.called`, "");
console.log("selection" + said("sel.note", ""));
if (existsSync(`${ST}/sel.exit`)) process.exit(Number(said("sel.exit", "0")));
console.log("dotnet native/tests/Ah.Fake.Tests/Ah.Fake.Tests.csproj");
for (const s of said("sel.shell", "test-s2\n").split("\n").filter(Boolean)) console.log(`shell ${s}`);
SELECT
  # the manifest's covers selection, a stand-in that selects no suite of its own
  echo "// stand-in" > "$d/contract-tools/select.mjs"
  cat > "$d/keep-set.sh" <<'KEEP'
#!/bin/bash
ST="@ST@"
echo "config=${AH_KEEP_SET_CONFIG:-} only=${AH_KEEP_SET_ONLY:-} split=${AH_TEST_HEAVY_SPLIT:-} $*" >> "$ST/keep.calls"
[ -f "$ST/keep.dies" ] && { echo "keep-set: build FAILED"; exit 1; }
rc=0
for row in E1:Ah.Fake.Tests E5:web.sh; do
  [ -z "${AH_KEEP_SET_ONLY:-}" ] || [ "$AH_KEEP_SET_ONLY" = "$row" ] || continue
  if grep -qx "$row" "$ST/keep.fails" 2>/dev/null && { [ -z "${AH_KEEP_SET_ONLY:-}" ] || ! grep -qx "$row" "$ST/keep.passes-alone" 2>/dev/null; }; then
    echo "FAIL ${row%%:*} ${row#*:}"; echo "    Failed Some.Test"; rc=1
  else
    echo "PASS ${row%%:*} ${row#*:} (1s)"
  fi
done
exit $rc
KEEP
  sed -i.bak "s#@ST@#$ST#" "$d/contract-tools/gate-select.mjs" "$d/keep-set.sh"; rm -f "$d/contract-tools/gate-select.mjs.bak" "$d/keep-set.sh.bak"
  echo "$NEXT $1" > "$REPO/${PFX}marker"
  GIT add -A >/dev/null; GIT commit -q -m "$1 $NEXT"; SHA="$(GIT rev-parse HEAD)"
}
akey() { echo "$AH_RESULTS_DIR/$SHA/affected-$AFFB"; }
afield() { node "$TOOLS" field "$(akey)/result.json" "$1"; }
acheck() { node "$TOOLS" affectedcheck "$1" "$2" "$3"; }

aff_commit aff-narrow; : > "$ST/fails"; AFFB=$SHA
change native/src/Ah.Fake/F.cs
rs "$SHA" affected --base "$AFFB"
check "affected: a diff that touches nothing shared runs and passes (exit 0), with no run at the base" '[ $RC = 0 ] && [ "$(afield status)" = PASS ] && [ ! -e "$AH_RESULTS_DIR/$AFFB/dotnet" ] && [ ! -e "$AH_RESULTS_DIR/$AFFB/full" ]'
check "...dotnet builds and tests the projects the selector named, whole, not its class filter" 'grep -q "projects=.*native/tests/Ah.Fake.Tests/Ah.Fake.Tests.csproj\$" "$ST/dotnet.env" && ! grep -q "FullyQualifiedName" "$ST/dotnet.calls"'
check "...shell runs only the suites the selector named" '[ "$(calls shell.calls)" = 1 ] && grep -q -- "--only " "$ST/shell.calls" && [ "$(afield ran_only)" = "{\"dotnet\":[\"Ah.Fake.Tests\"],\"shell\":[\"test-s2\"]}" ]'
check "...keep-set runs on the build the dotnet part made, in its configuration, without the overlap mark" 'grep -q "^config=Debug only= split= --no-build\$" "$ST/keep.calls" && [ "$(afield components)" = "{\"dotnet\":\"PASS\",\"shell\":\"PASS\",\"keep\":\"PASS\"}" ]'
check "...dotnet overlaps shell, so it takes the smaller grant" 'grep -q "^split=1 " "$ST/dotnet.env"'
check "...the narrowed parts answer no other set: nothing under dotnet, shell or full" '[ ! -e "$AH_RESULTS_DIR/$SHA/dotnet" ] && [ ! -e "$AH_RESULTS_DIR/$SHA/shell" ] && [ ! -e "$AH_RESULTS_DIR/$SHA/full" ] && ! ls "$(akey)" | grep -q verdict'
check "...the gate check accepts it for this sha and base" '[ "$(acheck "$(akey)/result.json" "$SHA" "$AFFB")" = ok ]'
check "...and not for another sha (nearest non-match)" 'acheck "$(akey)/result.json" "$AFFB" "$AFFB" | grep -q "not accepted"'
check "...nor another base" 'acheck "$(akey)/result.json" "$SHA" "$SHA" | grep -q "not accepted: the result was narrowed"'
N0="$(calls dotnet.runs)"; rs "$SHA" affected --base "$AFFB"
check "a second request reuses it" '[ $RC = 0 ] && [ "$(calls dotnet.runs)" = "$N0" ] && echo "$OUT" | grep -q "cached=yes"'
rs "$SHA" affected
check "affected without --base is a usage error" '[ $RC = 2 ]'

aff_commit aff-noshell; : > "$ST/fails"; : > "$ST/sel.shell"; AFFB=$SHA
change native/tests/Ah.Fake.Tests/A.cs
rs "$SHA" affected --base "$AFFB"
check "affected with no suite selected: no run-all and no publish, and dotnet takes the larger grant" '[ $RC = 0 ] && [ "$(calls shell.calls)" = 0 ] && [ "$(calls publish.calls)" = 0 ] && grep -q "^split= " "$ST/dotnet.env"'

aff_commit aff-red; : > "$ST/fails"; AFFB=$SHA
printf 'E1:Ah.Fake.Tests\n' > "$ST/keep.fails"; printf 'E1:Ah.Fake.Tests\n' > "$ST/keep.passes-alone"
change native/src/Ah.Fake/F.cs
rs "$SHA" affected --base "$AFFB"
check "a keep-set row that passes alone is a flake: FLAKY-PASS, exit 0, accepted" '[ $RC = 0 ] && [ "$(afield status)" = FLAKY-PASS ] && grep -q "only=E1:Ah.Fake.Tests " "$ST/keep.calls" && [ "$(acheck "$(akey)/result.json" "$SHA" "$AFFB")" = ok ]'
: > "$ST/keep.passes-alone"
change native/src/Ah.Fake/G.cs
rs "$SHA" affected --base "$AFFB"
check "a keep-set row that fails again is FAIL: exit 1, no verdict to hide it, not accepted" '[ $RC = 1 ] && [ "$(afield status)" = FAIL ] && acheck "$(akey)/result.json" "$SHA" "$AFFB" | grep -q "not accepted: the result is FAIL"'
: > "$ST/keep.fails"; printf 'Ah.Fake.Tests.C.Z\n' > "$ST/fails"
change native/src/Ah.Fake/H.cs
rs "$SHA" affected --base "$AFFB"
check "a dotnet failure is FAIL too, and keep-set still ran" '[ $RC = 1 ] && [ "$(afield status)" = FAIL ] && [ "$(node "$TOOLS" field "$(akey)/result.json" components | grep -c "\"keep\":\"PASS\"")" = 1 ]'
: > "$ST/fails"; printf 'test-s2\n' > "$ST/shell.fails"
change native/src/Ah.Fake/I.cs
rs "$SHA" affected --base "$AFFB"
check "a selected suite that fails is FAIL" '[ $RC = 1 ] && [ "$(afield status)" = FAIL ]'
: > "$ST/shell.fails"; : > "$ST/keep.dies"
change native/src/Ah.Fake/J.cs
rs "$SHA" affected --base "$AFFB"
check "a keep-set that dies with no row is ERROR, not a pass: exit 3, nothing stored" '[ $RC = 3 ] && [ ! -f "$(akey)/result.json" ]'

aff_commit aff-shared; : > "$ST/fails"; AFFB=$SHA
rs "$AFFB" full
for shared in native/src/Ah.Core/X.cs native/tests/ProcessSandbox.cs hooks/hooks.json tests/run-set.sh tests/keep-set.sh tests/contract-tools/select.mjs native/Directory.Build.props; do
  rm -f "$ST/select.called" "$ST/dotnet.env"
  # each from the commit before it, so the diff holds only this path
  AFFB=$SHA; change "$shared"
  rs "$SHA" affected --base "$AFFB"
  check "NEGATIVE: a diff touching $shared gets no narrowed run: it says so, runs full against the base, the selector is not asked" \
    'echo "$OUT" | grep -q "affected becomes full: the diff touches shared infrastructure" && [ -f "$AH_RESULTS_DIR/$SHA/full/result.json" ] && [ -f "$AH_RESULTS_DIR/$SHA/full/verdict-$AFFB.json" ] && [ ! -e "$(akey)" ] && [ ! -e "$ST/select.called" ] && ! grep -q "projects=.\+" "$ST/dotnet.env"'
done
aff_commit aff-near; : > "$ST/fails"; AFFB=$SHA
change native/src/Ah.Corex/X.cs
rs "$SHA" affected --base "$AFFB"
check "a project whose name only starts like a shared one is narrowed (nearest non-match)" '[ $RC = 0 ] && [ "$(afield status)" = PASS ] && [ -e "$ST/select.called" ]'
aff_commit aff-broad; : > "$ST/fails"; echo 3 > "$ST/sel.exit"; AFFB=$SHA
rs "$AFFB" full
change native/src/Ah.Fake/F.cs
rs "$SHA" affected --base "$AFFB"
check "a selector that fails becomes full" 'echo "$OUT" | grep -q "affected becomes full: the selector exited 3" && [ -f "$AH_RESULTS_DIR/$SHA/full/result.json" ] && [ ! -e "$(akey)" ]'
aff_commit aff-unmapped; : > "$ST/fails"; printf '\nfull native/src/Ah.Fake/x.props' > "$ST/sel.note"; AFFB=$SHA
rs "$AFFB" full
change native/src/Ah.Fake/F.cs
rs "$SHA" affected --base "$AFFB"
check "a selector that asks for full gets full, even with exit 0" 'echo "$OUT" | grep -q "affected becomes full: the selector asks for full native/src/Ah.Fake/x.props" && [ -f "$AH_RESULTS_DIR/$SHA/full/result.json" ]'

echo "$PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
