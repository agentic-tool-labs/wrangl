#!/bin/bash
# agent-hierarchy — tests/run-set.sh, part 1 of 3 (sections 1 to 11b): the stored result of a (commit, set), the single flight per
# key, the isolated rerun of a failed test, FLAKY-PASS, ERROR that is not stored, the publish made once per clone, and the sets
# running the clone's own commands without a slot of run-set's own. It runs on a scratch repository whose commits hold stand-ins
# for the clone's commands (scripts/dotnet-test.sh, tests/run-all.sh, tests/publish-bin.sh), so no real suite runs; the store is a
# directory of this test's own. The setup is tests/lib-run-set-test.sh's; parts 2 and 3 are tests/test-run-set-s2.sh and -s3.sh.
# Usage: bash tests/test-run-set-s1.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-run-set-test.sh"

# 1. a pass is stored and reused: the second request runs nothing
mkcommit pass; : > "$ST/fails"
rs "$SHA" dotnet
check "a green dotnet run exits 0, says PASS and stores result.json" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "^run-set: PASS" && [ "$(rjson dotnet status)" = PASS ] && [ "$(calls dotnet.runs)" = 1 ]'
check "...with no isolated rerun, because nothing failed" '[ "$(calls dotnet.reruns)" = 0 ]'
check "...and run-set itself holds no slot while its command runs (the command sees none held)" 'grep -q "^AH_TEST_SLOT_HELD= " "$ST/dotnet.calls"'
rs "$SHA" dotnet
check "the same request again is answered from the store: cached, no second run" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "cached=yes" && [ "$(calls dotnet.runs)" = 1 ]'

# 2. a stored FAIL is returned unless --fresh; --fresh runs again and replaces it
mkcommit failing; printf 'Ah.Fake.Tests.C.Boom\n' > "$ST/fails"
rs "$SHA" dotnet
check "a test that fails twice makes the set FAIL (exit 1) and is named, failed_twice" '[ $RC = 1 ] && [ "$(rjson dotnet status)" = FAIL ] && [ "$(nfail dotnet)" = 1:1 ] && rjson dotnet failures | grep -q "Ah.Fake.Tests.C.Boom"'
check "...its project and first error line are recorded" 'rjson dotnet failures | grep -q "\"project\":\"Ah.Fake.Tests\"" && rjson dotnet failures | grep -q "boom Ah.Fake.Tests.C.Boom"'
rs "$SHA" dotnet
check "a stored FAIL without --fresh is returned cached and nothing runs" '[ $RC = 1 ] && printf %s "$OUT" | grep -q "cached=yes" && [ "$(calls dotnet.runs)" = 1 ]'
: > "$ST/fails"
rs "$SHA" dotnet --fresh
check "--fresh runs a stored FAIL again and the new PASS replaces it" '[ $RC = 0 ] && [ "$(calls dotnet.runs)" = 2 ] && [ "$(rjson dotnet status)" = PASS ]'
rs "$SHA" dotnet --fresh
check "--fresh does not rerun a stored PASS (nearest non-match)" '[ "$(calls dotnet.runs)" = 2 ] && printf %s "$OUT" | grep -q "cached=yes"'

# 3. the flake rule: each failed test is rerun once, alone; passing alone is flaky, failing again is FAIL
mkcommit flake-one; printf 'Ah.Fake.Tests.C.A\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\n' > "$ST/passes-alone"
rs "$SHA" dotnet
check "a test that fails and then passes alone gives FLAKY-PASS (exit 0), named in flaky" '[ $RC = 0 ] && [ "$(rjson dotnet status)" = FLAKY-PASS ] && rjson dotnet flaky | grep -q "Ah.Fake.Tests.C.A" && [ "$(nfail dotnet)" = 0:0 ]'
check "...the rerun was one call for that one test" '[ "$(calls dotnet.reruns)" = 1 ] && grep -q "native/tests/Ah.Fake.Tests/Ah.Fake.Tests.csproj --no-build --filter FullyQualifiedName=Ah.Fake.Tests.C.A " "$ST/dotnet.calls"'
rs "$SHA" dotnet
check "a FLAKY-PASS is cached as it is, with its flaky names repeated, and nothing is rerun again" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "FLAKY-PASS" && printf %s "$OUT" | grep -q "cached=yes" && [ "$(calls dotnet.reruns)" = 1 ] && [ "$(calls dotnet.runs)" = 1 ] && rjson dotnet flaky | grep -q "Ah.Fake.Tests.C.A"'
mkcommit flake-mixed; printf 'Ah.Fake.Tests.C.A\nAh.Fake.Tests.C.B\nAh.Fake.Tests.C.C\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\nAh.Fake.Tests.C.B\n' > "$ST/passes-alone"
rs "$SHA" dotnet
check "three fail, two pass alone and one fails again: the set is FAIL, the two are flaky, the one failed twice" '[ $RC = 1 ] && [ "$(nfail dotnet)" = 1:1 ] && rjson dotnet failures | grep -q "Ah.Fake.Tests.C.C" && rjson dotnet flaky | grep -q "Ah.Fake.Tests.C.A" && rjson dotnet flaky | grep -q "Ah.Fake.Tests.C.B"'
check "...each failed test was rerun exactly once, one per call, and never a second time" '[ "$(calls dotnet.reruns)" = 3 ] && [ "$(sort -u "$ST/dotnet.reruns" | wc -l | tr -d " ")" = 3 ]'

mkcommit theory; printf 'Ah.Fake.Tests.C.T(x: 1, y: "a")\nAh.Fake.Tests.C.U(x: 2)\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.T\n' > "$ST/passes-alone"
rs "$SHA" dotnet
check "a failed theory row is rerun by its method name, not its display name: it passes alone and is flaky" '[ $RC = 1 ] && rjson dotnet flaky | grep -q "Ah.Fake.Tests.C.T" && grep -qx "Ah.Fake.Tests.C.T" "$ST/dotnet.reruns"'
check "...a theory row that fails alone too stays a failure (nearest non-match)" 'rjson dotnet failures | grep -q "Ah.Fake.Tests.C.U" && grep -qx "Ah.Fake.Tests.C.U" "$ST/dotnet.reruns"'

mkcommit aborted; printf 'Ah.Fake.Tests.C.A\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\n' > "$ST/passes-alone"; : > "$ST/dotnet.aborted-project"
rs "$SHA" dotnet
check "one flaky failure plus one aborted project is an ERROR (exit 3), not a FLAKY-PASS, and nothing is stored" '[ $RC = 3 ] && printf %s "$OUT" | grep -q "did not complete" && [ ! -e "$AH_RESULTS_DIR/$SHA/dotnet/result.json" ] && [ "$(calls dotnet.reruns)" = 0 ]'
mkcommit crashed; printf 'Ah.Fake.Tests.C.A\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\n' > "$ST/passes-alone"; : > "$ST/dotnet.crash-log"
rs "$SHA" dotnet
check "a failure plus a crashed-host line in the log is an ERROR and nothing is stored" '[ $RC = 3 ] && printf %s "$OUT" | grep -q "crashed test host" && [ ! -e "$AH_RESULTS_DIR/$SHA/dotnet/result.json" ]'
mkcommit xunit-crash; : > "$ST/fails"; echo '[xUnit.net 00:01:09.65] Catastrophic failure: Test process crashed with exit code 134.' > "$ST/dotnet.says"
rs "$SHA" dotnet
check "xUnit's line for a project that crashed, in an otherwise green run, is an ERROR and nothing is stored: no false pass" '[ $RC = 3 ] && printf %s "$OUT" | grep -q "crashed test host" && [ ! -e "$AH_RESULTS_DIR/$SHA/dotnet/result.json" ]'
mkcommit xunit-ended; : > "$ST/fails"; echo '[xUnit.net 00:01:09.65] Test process ended with exit code 0.' > "$ST/dotnet.says"
rs "$SHA" dotnet
check "NEGATIVE: a test process that ended with an exit code but did not crash stays a PASS (nearest non-match)" '[ $RC = 0 ] && [ "$(rjson dotnet status)" = PASS ]'
mkcommit fewer-trx; printf 'Ah.Fake.Tests.C.A\n' > "$ST/fails"; printf 'Ah.Fake.Tests.C.A\n' > "$ST/passes-alone"
mkdir -p "$REPO/${PFX}native/tests/Ah.Other.Tests"; : > "$REPO/${PFX}native/tests/Ah.Other.Tests/Ah.Other.Tests.csproj"
GIT add -A >/dev/null; GIT commit -q -m "second test project"; SHA="$(GIT rev-parse HEAD)"
rs "$SHA" dotnet
check "fewer projects with a result than test projects is an ERROR and nothing is stored" '[ $RC = 3 ] && printf %s "$OUT" | grep -q "1 of 2 test projects" && [ ! -e "$AH_RESULTS_DIR/$SHA/dotnet/result.json" ]'
GIT rm -rq "${PFX}native/tests/Ah.Other.Tests"; GIT commit -q -m "drop it"

# 4. ERROR is reported but never stored
mkcommit nodotnet no-dotnet
rs "$SHA" dotnet
check "a commit that predates scripts/dotnet-test.sh is an ERROR (exit 3) with no fallback" '[ $RC = 3 ] && printf %s "$OUT" | grep -q "predates scripts/dotnet-test.sh" && [ "$(calls dotnet.calls)" = 0 ]'
check "...and the ERROR is not stored: the key holds no result.json" '[ ! -e "$AH_RESULTS_DIR/$SHA/dotnet/result.json" ]'
mkcommit build-broken; : > "$ST/fails"; : > "$ST/dotnet.build-fails"
rs "$SHA" dotnet
check "a build that fails is an ERROR (exit 3), not a FAIL, and nothing is stored" '[ $RC = 3 ] && printf %s "$OUT" | grep -q "^run-set: ERROR" && [ ! -e "$AH_RESULTS_DIR/$SHA/dotnet/result.json" ]'
rm -f "$ST/dotnet.build-fails"
rs "$SHA" dotnet
check "the next request after an ERROR runs again, and its PASS is stored" '[ $RC = 0 ] && [ "$(calls dotnet.runs)" = 1 ] && [ "$(rjson dotnet status)" = PASS ]'

# 5. usage errors run nothing
mkcommit usage; : > "$ST/fails"
rs "$SHA" nosuchset
check "an unknown set is a usage error (exit 2) and nothing runs" '[ $RC = 2 ] && [ "$(calls dotnet.calls)" = 0 ]'
rs "0000000000000000000000000000000000000000" dotnet
check "a commit the repository does not hold is a usage error and nothing runs" '[ $RC = 2 ] && [ "$(calls dotnet.calls)" = 0 ]'
rs "${SHA:0:12}" dotnet
check "a short sha is a usage error: the full 40-hex commit is required" '[ $RC = 2 ] && [ "$(calls dotnet.calls)" = 0 ]'
OUT="$(bash "$RUNSET" "$SB/no-such-dir" "$SHA" dotnet 2>&1)"; RC=$?
check "a repo that is not a directory is a usage error" '[ $RC = 2 ] && [ "$(calls dotnet.calls)" = 0 ]'
OUT="$(AH_RESULTS_DIR="$SB/results/../../../Users" bash "$RUNSET" "$REPO" "$SHA" dotnet 2>&1)"; RC=$?
check "an AH_RESULTS_DIR holding .. is a usage error and nothing runs" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "must not contain" && [ "$(calls dotnet.calls)" = 0 ]'
ln -s /private/tmp "$SB/linkout"
OUT="$(AH_RESULTS_DIR="$SB/linkout" bash "$RUNSET" "$REPO" "$SHA" dotnet 2>&1)"; RC=$?
check "an ahprobe.* path that is a symlink out of /private/tmp/ahprobe.* is a usage error" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "must be under" && [ "$(calls dotnet.calls)" = 0 ]'
printf 'uncommitted\n' > "$REPO/${PFX}scratch.txt"
rs "$SHA" dotnet
check "uncommitted work in the repo is never what runs: the commit is cloned, not the tree" '[ $RC = 0 ] && ! grep -rq uncommitted "$AH_RESULTS_DIR/$SHA/dotnet/run.log"'
rm -f "$REPO/${PFX}scratch.txt"

# 6. a parent commit's result is never reused
mkcommit child-a; : > "$ST/fails"; PARENT="$SHA"; PST="$ST"
rs "$PARENT" dotnet
mkcommit child-b
cp "$PST/fails" "$ST/fails"
rs "$SHA" dotnet
check "a stored PASS for another commit is not reused: the new commit runs" '[ "$(calls dotnet.runs)" = 1 ] && printf %s "$OUT" | grep -q "cached=no"'

# 7. single flight: two callers of one key run it once, the second returns the stored result
mkcommit concurrent; : > "$ST/fails"; echo 3 > "$ST/dotnet.sleep"
( bash "$RUNSET" "$REPO" "$SHA" dotnet > "$SB/c1.out" 2>&1; echo $? > "$SB/c1.rc" ) &
sleep 1
( bash "$RUNSET" "$REPO" "$SHA" dotnet > "$SB/c2.out" 2>&1; echo $? > "$SB/c2.rc" ) &
wait
OUT="$(cat "$SB/c1.out" "$SB/c2.out")"
check "two concurrent requests for one key run it once and both exit 0" '[ "$(calls dotnet.runs)" = 1 ] && [ "$(cat "$SB/c1.rc")" = 0 ] && [ "$(cat "$SB/c2.rc")" = 0 ]'
check "...exactly one of them was answered from the store" '[ "$(cat "$SB/c1.out" "$SB/c2.out" | grep -c "cached=yes")" = 1 ]'
mkcommit parallel-a; : > "$ST/fails"; echo 3 > "$ST/dotnet.sleep"; SHA_A="$SHA"; ST_A="$ST"
mkcommit parallel-b; : > "$ST/fails"; echo 3 > "$ST/dotnet.sleep"; SHA_B="$SHA"; ST_B="$ST"
T0=$SECONDS
( bash "$RUNSET" "$REPO" "$SHA_A" dotnet > /dev/null 2>&1 ) &
( bash "$RUNSET" "$REPO" "$SHA_B" dotnet > /dev/null 2>&1 ) &
wait
check "two different commits do not wait on each other (both ran, in about one run's time)" '[ -f "$ST_A/dotnet.runs" ] && [ -f "$ST_B/dotnet.runs" ] && [ $((SECONDS - T0)) -lt 8 ]'

# 8. locks: a dead owner is taken over; an interrupted run leaves no result and no lock behind
mkcommit stale-lock; : > "$ST/fails"
mkdir -p "$AH_RESULTS_DIR/$SHA/dotnet/.lock"; echo 99999999 > "$AH_RESULTS_DIR/$SHA/dotnet/.lock/owner"
rs "$SHA" dotnet
check "a lock whose owner is dead is taken over and the run goes ahead" '[ $RC = 0 ] && [ "$(calls dotnet.runs)" = 1 ]'
mkcommit killed; : > "$ST/fails"; echo 30 > "$ST/dotnet.sleep"
bash "$RUNSET" "$REPO" "$SHA" dotnet > /dev/null 2>&1 &
RP=$!
sleep 3; ah_signal_child TERM "$RP"; wait "$RP" 2>/dev/null
for LP in $(pgrep -f "$REPO/${PFX}scripts/dotnet-test.sh" 2>/dev/null); do ah_signal_ours TERM "$LP" "$REPO/"; done
check "a run killed mid-way leaves no result.json and no lock" '[ ! -e "$AH_RESULTS_DIR/$SHA/dotnet/result.json" ] && [ ! -e "$AH_RESULTS_DIR/$SHA/dotnet/.lock" ] && [ -z "$(ls "$AH_RESULTS_DIR/$SHA/dotnet"/.result.* 2>/dev/null)" ]'

# 9. shell: the binary comes from a publish of the clone; a failed script is rerun alone through the exact selector
mkcommit shell-flake; printf 'test-s2\n' > "$ST/shell.fails"; printf 'test-s2\n' > "$ST/shell.passes-alone"
rs "$SHA" shell
check "a shell script that fails then passes alone gives FLAKY-PASS" '[ $RC = 0 ] && [ "$(rjson shell status)" = FLAKY-PASS ] && rjson shell flaky | grep -q "test-s2"'
check "...the run got an executable AHD_NATIVE_BIN published from the clone, once" 'grep -q "exe=yes" "$ST/shell.calls" && [ "$(calls publish.calls)" = 1 ]'
check "...and the rerun selected the one script exactly, with -j 1 before it" 'grep -q -- "-j 1 --exact s2 -o " "$ST/shell.calls" && [ "$(grep -c -- "--exact" "$ST/shell.calls")" = 1 ]'
check "...run-all was asked to wait for its lock, in the run and in the rerun" '[ "$(grep -c "^WAIT=1 " "$ST/shell.calls")" = "$(wc -l < "$ST/shell.calls" | tr -d " ")" ]'
check "...the rerun's own lock is one run-all accepts, under an ahprobe directory" 'grep -- "--exact" "$ST/shell.calls" | grep -qE "LOCK=/private/tmp/ahprobe\.[^/ ]+/rerun-lock-"'
check "...the rerun limit was asked of the clone's run-all first" '[ "$(cat "$ST/shell.limit-calls" 2>/dev/null)" = test-s2 ]'
mkcommit shell-limit-nan; printf 'test-s2\n' > "$ST/shell.fails"; printf 'test-s2\n' > "$ST/shell.passes-alone"; printf '12x\n' > "$ST/shell.limit"
rs "$SHA" shell
check "a clone limit that is not a number never reaches timeout (the guard; not the fallback): the rerun runs and passes (FLAKY-PASS)" '[ $RC = 0 ] && [ "$(rjson shell status)" = FLAKY-PASS ] && [ "$(grep -c -- "--exact" "$ST/shell.calls")" = 1 ]'
mkcommit shell-rerun-refused; printf 'test-s2\n' > "$ST/shell.fails"; printf 'test-s2\n' > "$ST/shell.passes-alone"; : > "$ST/shell.refuse-rerun"
rs "$SHA" shell
check "a rerun run-all refused (exit 2) is an ERROR (exit 3), not a test that failed twice" '[ $RC = 3 ] && printf %s "$OUT" | grep -q "refused the rerun" && [ ! -e "$AH_RESULTS_DIR/$SHA/shell/result.json" ]'
rm -f "$ST/shell.refuse-rerun"
mkcommit shell-lock-timeout; : > "$ST/shell.lock-timeout"
rs "$SHA" shell
check "a run-all whose lock wait ran out (exit 75) is an ERROR (exit 3), not cached" '[ $RC = 3 ] && [ ! -e "$AH_RESULTS_DIR/$SHA/shell/result.json" ]'
mkcommit shell-red; printf 'test-s1\ntest-s3\n' > "$ST/shell.fails"; printf 'test-s1\n' > "$ST/shell.passes-alone"
rs "$SHA" shell
check "a script that fails again alone makes the shell set FAIL; the one that passed alone is flaky" '[ $RC = 1 ] && [ "$(nfail shell)" = 1:1 ] && rjson shell failures | grep -q "test-s3" && rjson shell flaky | grep -q "test-s1"'
mkcommit publish-broken; printf '1\n' > "$ST/publish.fails"
rs "$SHA" shell
check "a publish that fails is an ERROR (exit 3), not cached" '[ $RC = 3 ] && printf %s "$OUT" | grep -q "publish" && [ ! -e "$AH_RESULTS_DIR/$SHA/shell/result.json" ]'
rm -f "$ST/publish.fails"
rs "$SHA" shell
check "...and the next request publishes again and runs" '[ "$(calls publish.calls)" = 2 ] && [ $RC = 0 ]'

# 11. full: both sets run even after a red one, each is stored under its own key, and the publish is made once
mkcommit full-red; printf 'Ah.Fake.Tests.C.Z\n' > "$ST/fails"
rs "$SHA" full
check "full runs dotnet and shell even though dotnet is red, and is FAIL (exit 1)" '[ $RC = 1 ] && [ "$(rjson full status)" = FAIL ] && [ "$(calls dotnet.runs)" = 1 ] && [ "$(calls shell.calls)" -ge 1 ]'
check "...each set is stored under its own key and the publish happened once" '[ "$(rjson dotnet status)" = FAIL ] && [ "$(rjson shell status)" = PASS ] && [ "$(calls publish.calls)" = 1 ]'
check "...the failure is listed under full with its set" 'rjson full failures | grep -q "\"set\":\"dotnet\""'
mkcommit full-reuse; : > "$ST/fails"
rs "$SHA" dotnet
rs "$SHA" full
check "a stored dotnet PASS is reused by full: only shell runs, and full is recorded" '[ $RC = 0 ] && [ "$(calls dotnet.runs)" = 1 ] && [ "$(calls shell.calls)" -ge 1 ] && [ "$(rjson full status)" = PASS ]'
rs "$SHA" full
check "a second full is answered from the store" 'printf %s "$OUT" | grep -q "cached=yes" && [ "$(calls dotnet.runs)" = 1 ]'

# 11a. full runs dotnet alongside shell, each in a clone of its own, and their output stays apart
mkcommit full-overlap; : > "$ST/fails"; echo 3 > "$ST/dotnet.sleep"
rs "$SHA" full
check "shell starts while dotnet is still running" '[ $RC = 0 ] && [[ "$(rjson shell started)" < "$(rjson dotnet ended)" ]]'
check "...and the build and the publish are in different clones, so a restore of one cannot race the other" '[ -s "$ST/dotnet.pwd" ] && [ -s "$ST/publish.pwd" ] && [ "$(cat "$ST/dotnet.pwd")" != "$(cat "$ST/publish.pwd")" ]'
mkcommit full-both-red; printf 'Ah.Fake.Tests.C.A\nAh.Fake.Tests.C.B\n' > "$ST/fails"; printf 'test-s1\ntest-s2\n' > "$ST/shell.fails"
AH_SYSTEMIC_MAX=1 rs "$SHA" full
DBLOCK="$(printf '%s\n' "$OUT" | awk '/^run-set: SYSTEMIC dotnet/ {f = 1; next} /^run-set:/ {f = 0} f')"
check "two red sets at once: the dotnet failure groups print whole under the dotnet line, with no shell name among them" '[ $RC = 1 ] && printf %s "$DBLOCK" | grep -q Ah.Fake.Tests.C.A && ! printf %s "$DBLOCK" | grep -q test-s'
check "...and each failure is under its own set in the result" 'rjson full failures | grep -q "\"name\":\"test-s1\".*\"set\":\"shell\"" && rjson full failures | grep -q "\"name\":\"Ah.Fake.Tests.C.A\".*\"set\":\"dotnet\""'
mkcommit full-lock-wait; : > "$ST/fails"
mkdir -p "$AH_RESULTS_DIR/$SHA/dotnet/.lock"; echo $$ > "$AH_RESULTS_DIR/$SHA/dotnet/.lock/owner"
AH_TEST_SLOT_WAIT=1 rs "$SHA" full
check "a dotnet whose lock wait runs out makes full exit 75, with shell stored and full's own lock gone" '[ $RC = 75 ] && [ "$(rjson shell status)" = PASS ] && [ ! -e "$AH_RESULTS_DIR/$SHA/full/.lock" ] && [ ! -f "$AH_RESULTS_DIR/$SHA/full/result.json" ]'
rm -rf "$AH_RESULTS_DIR/$SHA/dotnet/.lock"
mkcommit full-job-killed; : > "$ST/fails"; : > "$ST/dotnet.kill-job"
rs "$SHA" full
check "a dotnet job that dies without a result makes full ERROR (exit 3), never a pass, and says so" '[ $RC = 3 ] && printf %s "$OUT" | grep -q "the dotnet set ended without a result" && [ ! -f "$AH_RESULTS_DIR/$SHA/full/result.json" ] && [ "$(rjson shell status)" = PASS ]'
KC="$(head -1 "$ST/dotnet.pwd")"; KC="${KC%%/agent-hierarchy-v2*}"
case "$KC" in /private/tmp/ahprobe.??????) rm -rf "$KC";; esac

# 11b. a full run whose diff from --base touches no shell or script file does not run shell, and says so
mkcommit skip-base; : > "$ST/fails"; printf 'test-s1\n' > "$ST/shell.fails"
SKIPB=$SHA; rs "$SKIPB" full
change native/tests/Ah.Fake.Tests/A.cs
N0="$(calls shell.calls)"; D0="$(calls dotnet.runs)"
rs "$SHA" full --base "$SKIPB"
check "a diff of C# tests only: shell does not run, dotnet does, and the verdict is clean (exit 0)" '[ $RC = 0 ] && [ "$(calls shell.calls)" = "$N0" ] && [ "$(calls dotnet.runs)" -gt "$D0" ] && [ "$(vjson full r.verdict)" = "\"clean\"" ]'
check "...the result and the verdict both name what did not run, and the verdict line says it" '[ "$(rjson full not_run)" = "[\"shell\"]" ] && [ "$(vjson full r.classes.not_run)" = "[\"shell\"]" ] && printf %s "$OUT" | grep -q "did not run: shell"'
check "...a failure of the base in the set that did not run is not reported fixed, and its suites not removed" '[ "$(vjson full r.classes.fixed)" = "[]" ] && [ "$(vjson full r.classes.removed)" = "[]" ]'
check "...and verdictcheck accepts the verdict" 'node "$TOOLS" verdictcheck "$AH_RESULTS_DIR/$SHA/full/verdict-$SKIPB.json" "$AH_RESULTS_DIR/$SHA/full/result.json" "$AH_RESULTS_DIR/$SKIPB/full/result.json" | grep -qx ok'
rs "$SHA" full --base "$SKIPB"
check "the same request again is answered from the store (nearest non-match of the next check)" '[ $RC = 0 ] && [ "$(calls shell.calls)" = "$N0" ] && printf %s "$OUT" | grep -q "cached=yes"'
rs "$SHA" full
check "a caller without --base is not answered by the result without shell: shell runs and the result is whole" '[ "$(calls shell.calls)" != "$N0" ] && ! rjson full not_run | grep -q shell'
change native/tests/Ah.Fake.Tests/B.cs; N0="$(calls shell.calls)"
rs "$SHA" shell --base "$SKIPB"
check "a request for the shell set itself always runs it (nearest non-match)" '[ "$(calls shell.calls)" != "$N0" ]'
change native/tests/Ah.Fake.Tests/C.cs; printf 'Ah.Fake.Tests.C.Z\n' > "$ST/fails"
rs "$SHA" full --base "$SKIPB"
check "a dotnet failure on a run that skipped shell is still NEW (exit 4)" '[ $RC = 4 ] && [ "$(vjson full r.classes.new)" = "[\"dotnet|Ah.Fake.Tests.C.Z\"]" ]'
: > "$ST/fails"
change native/tests/Ah.Fake.Tests/D.cs; N0="$(calls shell.calls)"
rs "$SHA" full --base "$SKIPB"
check "a C# file under native/tests is not a script: shell does not run" '[ "$(calls shell.calls)" = "$N0" ] && ! rjson full ran_only | grep -q shell'
change native/src/Ah.Fake/F.cs; N0="$(calls shell.calls)"
rs "$SHA" full --base "$SKIPB"
check "a diff that touches native/src and no script runs the contract shards and nothing else of shell: one run-all, for that prefix" \
  '[ $RC = 0 ] && [ "$(($(calls shell.calls) - N0))" = 1 ] && tail -1 "$ST/shell.calls" | grep -q " contracts-s\$"'
check "...the result and the verdict record both what ran and what did not, and the verdict line says it" \
  '[ "$(rjson full not_run)" = "[\"shell\"]" ] && [ "$(rjson full ran_only)" = "{\"shell\":[\"test-contracts-s0\",\"test-contracts-s1\",\"test-contracts-s2\",\"test-contracts-s3\"]}" ] && [ "$(vjson full r.classes.ran_only.shell.length)" = 4 ] && printf %s "$OUT" | grep -q "of shell, ran only: test-contracts-s0"'
check "...a base failure in a suite that did not run is not reported fixed, and the suites that did not run are not removed" '[ "$(vjson full r.classes.fixed)" = "[]" ] && [ "$(vjson full r.classes.removed)" = "[]" ]'
check "...and the shards are not stored as the shell set, so they cannot answer a request for it" '[ ! -f "$AH_RESULTS_DIR/$SHA/shell/result.json" ] && [ -f "$AH_RESULTS_DIR/$SHA/contracts/result.json" ]'
rs "$SHA" shell
check "a request for the shell set at that commit runs all of it (nearest non-match)" 'tail -1 "$ST/shell.calls" | grep -qv " contracts-s\$" && rjson shell suites | grep -q test-s1'
PARENT=$SHA
N0="$(calls shell.calls)"
change docs/note.md
rs "$SHA" full --base "$PARENT"
check "a diff of docs alone runs no suite of shell, not even the shards (nearest non-match)" '[ $RC = 0 ] && [ "$(calls shell.calls)" = "$N0" ] && [ -z "$(rjson full ran_only)" ]'
rs "$SHA" full --base "$SKIPB"
check "...and that result does not answer a base whose diff touches native/src: the shards run" '[ "$(($(calls shell.calls) - N0))" = 1 ] && tail -1 "$ST/shell.calls" | grep -q " contracts-s\$" && [ -n "$(rjson full ran_only)" ]'
printf 'test-s1\ntest-contracts-s1\n' > "$ST/shell.fails"
change native/src/Ah.Fake/G.cs
rs "$SHA" full --base "$SKIPB"
check "a shard that fails there is NEW under shell (exit 4)" '[ $RC = 4 ] && [ "$(vjson full r.classes.new)" = "[\"shell|test-contracts-s1\"]" ]'
printf 'test-s1\n' > "$ST/shell.fails"
mkcommit shard-still; : > "$ST/fails"; echo test-contracts-s0 > "$ST/shell.extra"; echo test-contracts-s0 > "$ST/shell.fails"
STILLB=$SHA; rs "$STILLB" full
change native/src/Ah.Fake/H.cs
rs "$SHA" full --base "$STILLB"
check "a shard that already fails at the base is STILL-FAILING, not NEW, and the run is clean (exit 0)" '[ $RC = 0 ] && [ "$(vjson full r.classes.still)" = "[\"shell|test-contracts-s0\"]" ] && [ "$(vjson full r.classes.new)" = "[]" ]'
: > "$ST/shell.fails"; : > "$ST/shell.extra"
FROM=$SHA; rs "$FROM" full
change tests/contracts/case.json; N0="$(calls shell.calls)"
rs "$SHA" full --base "$FROM"
check "a contract case under tests/ counts (nearest non-match): all of shell runs" '[ "$(calls shell.calls)" != "$N0" ] && ! tail -1 "$ST/shell.calls" | grep -q " contracts-s\$" && [ -z "$(rjson full not_run)" ]'
FROM=$SHA
change native/tools/x.sh; N0="$(calls shell.calls)"
rs "$SHA" full --base "$FROM"
check "a .sh anywhere counts, even under native/ (nearest non-match): all of shell runs" '[ "$(calls shell.calls)" != "$N0" ] && ! tail -1 "$ST/shell.calls" | grep -q " contracts-s\$"'
FROM=$SHA
change native/src/Ah.Fake/J.cs
change native/tools/y.mjs; N0="$(calls shell.calls)"
rs "$SHA" full --base "$FROM"
check "native/src together with a script is not the shards-only case: all of shell runs" '[ "$(calls shell.calls)" != "$N0" ] && ! tail -1 "$ST/shell.calls" | grep -q " contracts-s\$" && [ -z "$(rjson full not_run)" ]'
FROM=$SHA; rs "$FROM" full
change native/Directory.Packages.props
rs "$SHA" full --base "$FROM"
check "a package or build file under native/, outside native/src, runs the shards" 'tail -1 "$ST/shell.calls" | grep -q " contracts-s\$" && [ -n "$(rjson full ran_only)" ]'
change native/src/Ah.Fake/Moved.cs
FROM=$SHA; rs "$FROM" full
mkdir -p "$REPO/${PFX}native/tests/Ah.Fake.Tests"; GIT mv "${PFX}native/src/Ah.Fake/Moved.cs" "${PFX}native/tests/Ah.Fake.Tests/Moved.cs"; GIT commit -q -m move; SHA="$(GIT rev-parse HEAD)"
rs "$SHA" full --base "$FROM"
check "a move from native/src to native/tests runs the shards: the move lists both names" 'tail -1 "$ST/shell.calls" | grep -q " contracts-s\$" && [ -n "$(rjson full ran_only)" ]'
change docs/moved.sh
FROM=$SHA
GIT mv "${PFX}docs/moved.sh" "${PFX}docs/moved.txt"; GIT commit -q -m move; SHA="$(GIT rev-parse HEAD)"
rs "$SHA" full --base "$FROM"
check "a script moved to a name that is no script runs all of shell (nearest non-match)" '[ -z "$(rjson full not_run)" ]'

# a run of the shards judged against a base that itself ran only the shards
mkcommit shard-chain; : > "$ST/fails"
W0=$SHA; rs "$W0" full
change native/src/Ah.Fake/A.cs; H1=$SHA
rs "$H1" full --base "$W0"
change native/src/Ah.Fake/B.cs; H2=$SHA
WHOLE="$(grep -vc "contracts-s\$" "$ST/shell.calls")"
rs "$H2" full --base "$H1"
check "a shards run on a base that ran only the shards runs no whole shell at the base: clean, and verdictcheck accepts it" \
  '[ $RC = 0 ] && [ "$(grep -vc "contracts-s\$" "$ST/shell.calls")" = "$WHOLE" ] && [ ! -f "$AH_RESULTS_DIR/$H1/shell/result.json" ] && node "$TOOLS" verdictcheck "$AH_RESULTS_DIR/$H2/full/verdict-$H1.json" "$AH_RESULTS_DIR/$H2/full/result.json" "$AH_RESULTS_DIR/$H1/full/result.json" | grep -qx ok'
change native/src/Ah.Fake/C.cs; H3=$SHA
change native/src/Ah.Fake/D.cs; H4=$SHA
N0="$(calls shell.calls)"
rs "$H4" full --base "$H3"
check "a base with no stored result runs for a shards run with the shards only: two run-alls, both of that prefix, and the base reads as such" \
  '[ $RC = 0 ] && [ "$(($(calls shell.calls) - N0))" = 2 ] && [ "$(grep -vc "contracts-s\$" "$ST/shell.calls")" = "$WHOLE" ] && [ -n "$(node "$TOOLS" field "$AH_RESULTS_DIR/$H3/full/result.json" ran_only)" ]'
change native/src/Ah.Fake/E.cs; H5=$SHA
echo test-contracts-s1 > "$ST/shell.fails"; echo test-contracts-s1 > "$ST/shell.extra"
rs "$H5" full
change native/src/Ah.Fake/F.cs; H6=$SHA
rs "$H6" full --base "$H5"
check "a shard that fails at a base with whole results and again here is STILL (nearest non-match of the next check)" '[ $RC = 0 ] && [ "$(vjson full r.classes.still)" = "[\"shell|test-contracts-s1\"]" ]'
change native/src/Ah.Fake/G.cs; H7=$SHA
rs "$H7" full --base "$H6"
check "a shard that fails at a base that ran only the shards and again here is STILL, not NEW" '[ $RC = 0 ] && [ "$(vjson full r.classes.still)" = "[\"shell|test-contracts-s1\"]" ] && [ "$(vjson full r.classes.new)" = "[]" ]'
: > "$ST/shell.fails"; : > "$ST/shell.extra"
# a base with no full, and all of its shell stored
change native/tests/Ah.Fake.Tests/Z.cs; PB=$SHA
rs "$PB" dotnet
rs "$PB" shell
N0="$(calls shell.calls)"
change native/src/Ah.Fake/H.cs
rs "$SHA" full --base "$PB"
check "a shards run on a base with all of shell stored runs no shards at the base: its full is made whole from that" \
  '[ $RC = 0 ] && [ "$(($(calls shell.calls) - N0))" = 1 ] && [ ! -f "$AH_RESULTS_DIR/$PB/contracts/result.json" ] && [ -f "$AH_RESULTS_DIR/$PB/full/result.json" ] && [ -z "$(node "$TOOLS" field "$AH_RESULTS_DIR/$PB/full/result.json" not_run)" ]'

# under full, a shell rerun waits for the dotnet job; the dotnet job's own rerun does not wait for shell
mkcommit full-rerun-alone; echo Ah.Fake.Tests.C.A > "$ST/fails"; echo Ah.Fake.Tests.C.A > "$ST/passes-alone"
echo 3 > "$ST/dotnet.sleep"; echo test-s1 > "$ST/shell.fails"; echo test-s1 > "$ST/shell.passes-alone"
rs "$SHA" full
check "a shell failure under full is rerun after the dotnet job ended, and both reruns finish: no deadlock, FLAKY-PASS" \
  '[ $RC = 0 ] && [ "$(rjson full status)" = FLAKY-PASS ] && grep -q "^start s1" "$ST/shell.events" && [ ! -e "$ST/shell.rerun-before-dotnet-ended" ] && [ "$(calls dotnet.reruns)" = 1 ]'

mkcommit skip-base-dotnet-only; : > "$ST/fails"
SB2=$SHA; rs "$SB2" dotnet
change native/tests/Ah.Fake.Tests/E.cs; N0="$(calls shell.calls)"
rs "$SHA" full --base "$SB2"
check "a skipping run needs only the base's dotnet: no shell run at either commit, no base full made, and the verdict checks out against it" '[ $RC = 0 ] && [ "$(calls shell.calls)" = "$N0" ] && [ ! -f "$AH_RESULTS_DIR/$SB2/full/result.json" ] && node "$TOOLS" verdictcheck "$AH_RESULTS_DIR/$SHA/full/verdict-$SB2.json" "$AH_RESULTS_DIR/$SHA/full/result.json" "$AH_RESULTS_DIR/$SB2/dotnet/result.json" | grep -qx ok'

echo "$PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
