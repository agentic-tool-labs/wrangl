#!/bin/bash
# agent-hierarchy — tests/run-set.sh: the stored result of a (commit, set), the single flight per key, the isolated rerun of a failed
# test, FLAKY-PASS, ERROR that is not stored, the publish made once per clone, and the sets running the clone's own commands without
# a slot of run-set's own. It runs on a scratch repository whose commits hold stand-ins for the clone's commands
# (scripts/dotnet-test.sh, tests/run-all.sh, tests/publish-bin.sh), so no real suite runs; the store is a
# directory of this test's own.
# Usage: bash tests/test-run-set.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-pid.sh"
# a run under run-all.sh inherits its held slot; this test starts from none
unset AH_TEST_SLOT_HELD
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
RUNSET="$PLUGIN/tests/run-set.sh"
TOOLS="$PLUGIN/tests/run-set-tools.mjs"
PFX="$(git -C "$PLUGIN" rev-parse --show-prefix)"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:700})"; fi; }

export AH_RESULTS_DIR="$SB/results"
export AH_TEST_SLOT_WAIT=60
# the stand-in sets are tiny, so the percentage limit is off here; the cases of section 12 turn it on
export AH_SYSTEMIC_PCT=100
# reruns go through the machine-wide slot limiter; this test uses slots of its own
export AH_SLOTS_DIR="$SB/slots"
REPO="$SB/repo"
GIT() { git -C "$REPO" -c user.name=t -c user.email=t@t "$@"; }
mkdir -p "$REPO" && GIT init -q
mkdir -p "$REPO/${PFX}native/tests/Ah.Fake.Tests"
: > "$REPO/${PFX}native/tests/Ah.Fake.Tests/Ah.Fake.Tests.csproj"
mkdir -p "$REPO/${PFX}native/tests/Ah.Helper"; : > "$REPO/${PFX}native/tests/Ah.Helper/Ah.Helper.csproj"

# ---- the stand-ins, each told where to keep its state ($ST) and recording what it was called with
stubs() { # <state dir> <with-dotnet-script: yes|no>
  local st=$1 d="$REPO/${PFX}"
  mkdir -p "$d/scripts" "$d/tests" "$st"
  rm -f "$d/scripts/dotnet-test.sh"
  if [ "$2" = yes ]; then
    cat > "$d/scripts/dotnet-test.sh" <<'DOTNET'
#!/bin/bash
ST="@ST@"
echo "AH_TEST_SLOT_HELD=${AH_TEST_SLOT_HELD:-} $*" >> "$ST/dotnet.calls"
echo "split=${AH_TEST_HEAVY_SPLIT:-} projects=${AH_DOTNET_TEST_PROJECTS:-}" >> "$ST/dotnet.env"
pwd >> "$ST/dotnet.pwd"
if [ -f "$ST/dotnet.kill-job" ]; then
  # the job is the child of the topmost run-set.sh above this script
  c=$PPID; p=$(ps -o ppid= -p "$c" | tr -d ' ')
  while case "$(ps -o command= -p "$(ps -o ppid= -p "$p" | tr -d ' ')")" in */run-set.sh*) true;; *) false;; esac; do c=$p; p=$(ps -o ppid= -p "$c" | tr -d ' '); done
  kill -9 "$c"; sleep 1  # pid-owned: this stand-in's own ancestor, the job of the run-set this test started, read from the live process tree just now
fi
dir=""; filter=""; prev=""
for a in "$@"; do case "$prev" in --results-directory) dir=$a;; --filter) filter=$a;; esac; prev=$a; done
mkdir -p "$dir"
[ -f "$ST/dotnet.sleep" ] && sleep "$(cat "$ST/dotnet.sleep")"
[ -f "$ST/dotnet.build-fails" ] && { echo "build failed"; exit 1; }
trx() { # <passed> then the failed names
  local p=$1; shift; local n=0 defs="" res="" f
  for f in "$@"; do
    n=$((n + 1))
    defs="$defs<UnitTest name=\"m$n\" id=\"id$n\"><TestMethod codeBase=\"/x/Ah.Fake.Tests.dll\" className=\"${f%.*}\" name=\"${f##*.}\" /></UnitTest>"
    res="$res<UnitTestResult testId=\"id$n\" testName=\"m$n\" outcome=\"Failed\"><Output><ErrorInfo><Message>boom $f&#xA;second line</Message></ErrorInfo></Output></UnitTestResult>"
  done
  echo "<?xml version=\"1.0\"?><TestRun><TestDefinitions>$defs</TestDefinitions><Results>$res</Results><ResultSummary outcome=\"$(cat "$ST/dotnet.outcome" 2>/dev/null || { [ $# -gt 0 ] && echo Failed || echo Completed; })\"><Counters total=\"$((p + $#))\" executed=\"$((p + $#))\" passed=\"$p\" failed=\"$#\" /></ResultSummary></TestRun>" > "$dir/r-$$.trx"
}
if [ -n "$filter" ]; then
  name="${filter#FullyQualifiedName=}"
  echo "$name" >> "$ST/dotnet.reruns"
  echo "start $name" >> "$ST/dotnet.rerun-events"
  [ -f "$ST/dotnet.rerun-sleep" ] && sleep "$(cat "$ST/dotnet.rerun-sleep")"
  echo "end $name" >> "$ST/dotnet.rerun-events"
  if grep -qx "$name" "$ST/passes-alone" 2>/dev/null; then trx 1; exit 0; fi
  trx 0 "$name"; exit 1
fi
echo run >> "$ST/dotnet.runs"
fails=()
while read -r l; do [ -n "$l" ] && fails+=("$l"); done < "$ST/fails" 2>/dev/null
trx "$(cat "$ST/dotnet.passed" 2>/dev/null || echo 3)" ${fails[@]+"${fails[@]}"}
if [ -f "$ST/dotnet.aborted-project" ]; then
  echo '<TestRun><ResultSummary outcome="Aborted"><Counters total="0" passed="0" failed="0" /></ResultSummary></TestRun>' > "$dir/b-$$.trx"
fi
[ -f "$ST/dotnet.crash-log" ] && echo 'Test host process crashed'
[ -f "$ST/dotnet.says" ] && cat "$ST/dotnet.says"
[ "${#fails[@]}" = 0 ]
DOTNET
    sed -i.bak "s#@ST@#$st#" "$d/scripts/dotnet-test.sh"; rm -f "$d/scripts/dotnet-test.sh.bak"
  fi
  cat > "$d/tests/publish-bin.sh" <<'PUBLISH'
#!/bin/bash
ST="@ST@"
out=""; prev=""
for a in "$@"; do [ "$prev" = --out ] && out=$a; prev=$a; done
echo "publish $*" >> "$ST/publish.calls"
pwd >> "$ST/publish.pwd"
[ -f "$ST/publish.fails" ] && { echo "publish failed"; exit 2; }
mkdir -p "$out"; printf '#!/bin/bash\nexit 0\n' > "$out/ahd"; chmod +x "$out/ahd"; : > "$out/ahd-tui"
PUBLISH
  cat > "$d/tests/run-all.sh" <<'RUNALL'
#!/bin/bash
ST="@ST@"
echo "WAIT=${AH_RUN_ALL_LOCK_WAIT:-} LOCK=${AH_RUN_ALL_LOCK:-} AHD_NATIVE_BIN=${AHD_NATIVE_BIN:-} exe=$([ -x "${AHD_NATIVE_BIN:-/nonexistent}" ] && echo yes || echo no) $*" >> "$ST/shell.calls"
exact=""; out=""; only=""; prev=""
for a in "$@"; do case "$prev" in --exact) exact=$a;; -o) out=$a;; --only) only=$a;; esac; prev=$a; done
[ -f "$ST/shell.lock-timeout" ] && { echo "run-all: waited for the lock; giving up (exit 75)" >&2; exit 75; }
[ -n "$exact" ] && [ -f "$ST/shell.refuse-rerun" ] && { echo "run-all: refusing" >&2; exit 2; }
if [ -n "$exact" ]; then
  [ -f "$ST/dotnet.runs" ] || : >> "$ST/shell.rerun-before-dotnet-ended"
  echo "start $exact" >> "$ST/shell.events"
  [ -f "$ST/shell.rerun-sleep" ] && sleep "$(cat "$ST/shell.rerun-sleep")"
  echo "end $exact" >> "$ST/shell.events"
  if grep -qx "test-$exact" "$ST/shell.passes-alone" 2>/dev/null; then echo "test-$exact ok" > "$out"; exit 0; fi
  echo "test-$exact FAIL" > "$out"; exit 1
fi
rc=0; : > "$out"
suites="test-s1 test-s2 test-s3 $(cat "$ST/shell.extra" 2>/dev/null)"
[ "${!#}" = contracts-s ] && suites="test-contracts-s0 test-contracts-s1 test-contracts-s2 test-contracts-s3"
[ -n "$only" ] && suites="$(cat "$only")"
for t in $suites; do
  grep -qx "$t" "$ST/shell.drop" 2>/dev/null && continue
  if grep -qx "$t" "$ST/shell.fails" 2>/dev/null; then echo "$t FAIL" >> "$out"; rc=1; else echo "$t ok" >> "$out"; fi
done
if [ -f "$ST/shell.diff" ]; then mkdir -p "$AH_RUN_ALL_FAIL_DIR/test-s2/lifecycle"; cp "$ST/shell.diff" "$AH_RUN_ALL_FAIL_DIR/test-s2/lifecycle/12-x.diff"; fi
if [ -f "$ST/shell.log" ]; then echo "FAIL    test-s1 1s  log: $ST/shell.log"; fi
[ -f "$ST/shell.abort" ] && echo "run-all: aborted: systemic: 2 of 3 finished tests failed (stand-in)"
exit $rc
RUNALL
  local f
  for f in publish-bin run-all; do sed -i.bak "s#@ST@#$st#" "$d/tests/$f.sh"; rm -f "$d/tests/$f.sh.bak"; done
}
NEXT=0
mkcommit() { # <scenario> [no-dotnet]: a commit of stand-ins keeping state in $SB/st-<scenario>; sets SHA and ST
  NEXT=$((NEXT + 1)); ST="$SB/st-$1"
  stubs "$ST" "${2:-yes}"
  echo "$NEXT $1" > "$REPO/${PFX}marker"
  GIT add -A >/dev/null; GIT commit -q -m "$1 $NEXT"
  SHA="$(GIT rev-parse HEAD)"
}
rs() { OUT="$(bash "$RUNSET" "$REPO" "$@" 2>&1)"; RC=$?; }
calls() { [ -f "$ST/$1" ] && wc -l < "$ST/$1" | tr -d ' ' || echo 0; }
rjson() { node "$TOOLS" field "$AH_RESULTS_DIR/$SHA/$1/result.json" "$2"; }
nfail() { rjson "$1" failures | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const f=JSON.parse(s);console.log(f.length+":"+f.filter(x=>x.failed_twice).length)})'; }

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
vjson() { node -e 'const r=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")); console.log(JSON.stringify(eval(process.argv[2])))' "$(ls -t "$AH_RESULTS_DIR/$SHA/$1"/verdict-*.json | head -1)" "$2"; }
change() { # <path>: a commit that changes only that file, stand-ins untouched
  NEXT=$((NEXT + 1)); mkdir -p "$REPO/${PFX}$(dirname "$1")"; echo "$NEXT" > "$REPO/${PFX}$1"
  GIT add -A >/dev/null; GIT commit -q -m "change $1"; SHA="$(GIT rev-parse HEAD)"
}
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
# 13. --base: the verdict against a base commit (NEW, FIXED, STILL-FAILING, FLAKY, ADDED, REMOVED), and fail fast counting NEW only
fullruns() { grep "^WAIT" "$1" | grep -vc -- "--exact"; }
mkcommit sb-base; printf 'test-s1\ntest-s2\n' > "$ST/shell.fails"
BASE=$SHA; BST=$ST
mkcommit sb-head; printf 'test-s2\ntest-s3\n' > "$ST/shell.fails"
rs "$SHA" shell --base "$BASE"
check "base fails {s1,s2}, head fails {s2,s3}: NEW {s3}, FIXED {s1}, STILL {s2}, and the new-failures exit (4)" '[ $RC = 4 ] && [ "$(vjson shell r.verdict)" = "\"new-failures\"" ] && [ "$(vjson shell r.classes.new)" = "[\"test-s3\"]" ] && [ "$(vjson shell r.classes.fixed)" = "[\"test-s1\"]" ] && [ "$(vjson shell r.classes.still)" = "[\"test-s2\"]" ]'
check "...the base was not stored before, so the call ran it (once), and the result.json names it" '[ "$(fullruns "$BST/shell.calls")" = 1 ] && [ "$(vjson shell r.base)" = "\"$BASE\"" ]'
check "...only the NEW failure was rerun alone; the STILL-FAILING one was not" '[ "$(grep -c -- "--exact" "$ST/shell.calls")" = 1 ] && grep -q -- "--exact s3" "$ST/shell.calls"'
check "...the verdict line and the NEW name are printed" 'printf %s "$OUT" | grep -q "verdict=new-failures" && printf %s "$OUT" | grep -q "NEW test-s3"'
check "...the shell set ran with run-all early abort off" 'grep -q -- "--no-abort" "$ST/shell.calls"'
rs "$SHA" shell --base "$BASE"
check "the second call answers from the store (head and base): same verdict, exit 4, the base run once" '[ $RC = 4 ] && printf %s "$OUT" | grep -q "cached=yes" && [ "$(fullruns "$BST/shell.calls")" = 1 ]'
rs "$SHA" shell
check "the same stored result without --base keeps its plain exit (1: FAIL) and prints no verdict (nearest non-match)" '[ $RC = 1 ] && ! printf %s "$OUT" | grep -q "verdict="'

mkcommit sb-still; printf 'test-s2\n' > "$ST/shell.fails"
rs "$SHA" shell --base "$BASE"
check "head fails {s2} only, as the base does: verdict clean, exit 0, although the status is FAIL" '[ $RC = 0 ] && [ "$(vjson shell r.verdict)" = "\"clean\"" ] && [ "$(rjson shell status)" = FAIL ] && [ "$(vjson shell r.classes.still)" = "[\"test-s2\"]" ] && [ "$(vjson shell r.classes.new)" = "[]" ]'
check "...a failure that already fails at the base is not rerun" '! grep -q -- "--exact" "$ST/shell.calls"'

mkcommit sb-added; printf 'test-s4\n' > "$ST/shell.extra"; printf 'test-s4\n' > "$ST/shell.fails"
rs "$SHA" shell --base "$BASE"
check "a suite that exists only at the head and fails is NEW (and ADDED); the base's failures that now pass are FIXED" '[ $RC = 4 ] && [ "$(vjson shell r.classes.new)" = "[\"test-s4\"]" ] && [ "$(vjson shell r.classes.added)" = "[\"test-s4\"]" ] && [ "$(vjson shell r.classes.fixed)" = "[\"test-s1\",\"test-s2\"]" ]'
mkcommit sb-added-ok; printf 'test-s5\n' > "$ST/shell.extra"; printf 'test-s2\n' > "$ST/shell.fails"
rs "$SHA" shell --base "$BASE"
check "a suite that exists only at the head and passes is ADDED and not NEW (nearest non-match): verdict clean" '[ $RC = 0 ] && [ "$(vjson shell r.classes.added)" = "[\"test-s5\"]" ] && [ "$(vjson shell r.classes.new)" = "[]" ]'
mkcommit sb-removed; printf 'test-s1\n' > "$ST/shell.drop"; : > "$ST/shell.fails"
rs "$SHA" shell --base "$BASE"
check "a suite that failed at the base and is not at the head is REMOVED, not FIXED" '[ $RC = 0 ] && [ "$(vjson shell r.classes.removed)" = "[\"test-s1\"]" ] && [ "$(vjson shell r.classes.fixed)" = "[\"test-s2\"]" ]'

mkcommit sb-flaky; printf 'test-s3\n' > "$ST/shell.fails"; printf 'test-s3\n' > "$ST/shell.passes-alone"
rs "$SHA" shell --base "$BASE"
check "a NEW candidate that passes when rerun alone is FLAKY, not NEW: verdict clean, exit 0, status FLAKY-PASS" '[ $RC = 0 ] && [ "$(rjson shell status)" = FLAKY-PASS ] && [ "$(vjson shell r.classes.flaky)" = "[\"test-s3\"]" ] && [ "$(vjson shell r.classes.new)" = "[]" ]'

# fail fast counts the NEW failures only
mkcommit sb-dn-base; printf 'Ah.Fake.Tests.C.F1\nAh.Fake.Tests.C.F2\nAh.Fake.Tests.C.F3\nAh.Fake.Tests.C.F4\nAh.Fake.Tests.C.F5\nAh.Fake.Tests.C.F6\nAh.Fake.Tests.C.F7\nAh.Fake.Tests.C.F8\nAh.Fake.Tests.C.F9\nAh.Fake.Tests.C.F10\nAh.Fake.Tests.C.F11\nAh.Fake.Tests.C.F12\n' > "$ST/fails"
DBASE=$SHA; DBST=$ST
mkcommit sb-dn-head; cp "$DBST/fails" "$ST/fails"; printf 'Ah.Fake.Tests.C.N1\n' >> "$ST/fails"
rs "$SHA" dotnet --base "$DBASE"
check "R1 with --base: 12 failures that already fail at the base plus 1 NEW is not SYSTEMIC; only the NEW one is rerun; verdict new-failures (exit 4)" '[ $RC = 4 ] && [ "$(rjson dotnet status)" = FAIL ] && [ "$(calls dotnet.reruns)" = 1 ] && [ "$(vjson dotnet r.classes.new)" = "[\"Ah.Fake.Tests.C.N1\"]" ] && [ "$(vjson dotnet r.classes.still.length)" = 12 ]'
check "...the base itself, with 12 failures, was run to the end and stored as FAIL, not SYSTEMIC" '[ "$(node "$TOOLS" field "$AH_RESULTS_DIR/$DBASE/dotnet/result.json" status)" = FAIL ]'
mkcommit sb-dn-nobase; cp "$SB/st-sb-dn-head/fails" "$ST/fails"
rs "$SHA" dotnet
check "R1 without --base is unchanged (nearest non-match): the same 13 failures are SYSTEMIC, exit 1, no rerun" '[ $RC = 1 ] && [ "$(rjson dotnet status)" = SYSTEMIC ] && [ "$(calls dotnet.reruns)" = 0 ]'
mkcommit sb-dn-allnew; printf 'Ah.Fake.Tests.C.M1\nAh.Fake.Tests.C.M2\nAh.Fake.Tests.C.M3\nAh.Fake.Tests.C.M4\nAh.Fake.Tests.C.M5\nAh.Fake.Tests.C.M6\nAh.Fake.Tests.C.M7\nAh.Fake.Tests.C.M8\nAh.Fake.Tests.C.M9\nAh.Fake.Tests.C.M10\nAh.Fake.Tests.C.M11\n' > "$ST/fails"
rs "$SHA" dotnet --base "$DBASE"
check "R1 with --base: 11 NEW failures are SYSTEMIC all the same (nearest non-match), and the verdict says new-failures" '[ $RC = 4 ] && [ "$(rjson dotnet status)" = SYSTEMIC ] && [ "$(calls dotnet.reruns)" = 0 ]'

# the fingerprint
mkcommit sb-fp-head; printf 'test-s2\ntest-s3\n' > "$ST/shell.fails"
CLAUDECODE=1 rs "$SHA" shell --base "$BASE"
check "a head made inside an agent session against a base made outside one is incomparable (exit 5), names the reason, reports no NEW" '[ $RC = 5 ] && [ "$(vjson shell r.verdict)" = "\"incomparable\"" ] && printf %s "$OUT" | grep -q "agent session" && [ "$(vjson shell r.classes.new)" = "[]" ]'
check "...the base was not run again, and R1 and run-all were left as without --base (no --no-abort)" '[ "$(fullruns "$BST/shell.calls")" = 1 ] && ! grep -q -- "--no-abort" "$ST/shell.calls"'
mkcommit sb-fp-head2; printf 'test-s2\ntest-s3\n' > "$ST/shell.fails"
CLAUDECODE=1 rs "$SHA" shell --base "$BASE" --fresh-base
check "--fresh-base runs the base again where this run is, and then compares (NEW {s3}, exit 4)" '[ "$(fullruns "$BST/shell.calls")" = 2 ] && [ $RC = 4 ] && [ "$(vjson shell r.classes.new)" = "[\"test-s3\"]" ]'
mkcommit sb-fp-head3; printf 'test-s2\ntest-s3\n' > "$ST/shell.fails"
CLAUDECODE=1 rs "$SHA" shell --base "$BASE"
check "a base that was made where this run is needs no --fresh-base (nearest non-match): compared, exit 4" '[ $RC = 4 ] && [ "$(fullruns "$BST/shell.calls")" = 2 ]'
mkcommit sb-nofp-base; printf 'test-s1\n' > "$ST/shell.fails"
NOFP=$SHA; rs "$NOFP" shell
node -e 'const fs=require("fs"),f=process.argv[1],r=JSON.parse(fs.readFileSync(f,"utf8"));delete r.env;fs.writeFileSync(f,JSON.stringify(r))' "$AH_RESULTS_DIR/$NOFP/shell/result.json"
mkcommit sb-nofp-head; printf 'test-s1\n' > "$ST/shell.fails"
rs "$SHA" shell --base "$NOFP"
check "a base result with no fingerprint is incomparable, not guessed at" '[ $RC = 5 ] && printf %s "$OUT" | grep -q "no environment fingerprint"'

# what makes a base a baseline: compared on results written by hand
FPJ=$(node "$TOOLS" env)
mkres() { # <file> <status> <failures json> <env json>
  node -e 'require("fs").writeFileSync(process.argv[1], JSON.stringify({ sha: "x", set: "shell", status: process.argv[2], failures: JSON.parse(process.argv[3]), flaky: [], counts: { total: 30, passed: 0, failed: 0 }, env: JSON.parse(process.argv[4]) }))' "$@"; }
cmpv() { node "$TOOLS" compare --head "$CH" --base "$CB" --basesha b > "$CO" 2>&1; CRC=$?; }
CH="$SB/head.json"; CB="$SB/base.json"; CO="$SB/cmp.out"
F2='[{"name":"t1","failed_twice":true}]'; F1='[{"name":"t1","failed_twice":false}]'
mkres "$CH" FAIL "$F2" "$FPJ"; mkres "$CB" FAIL "$F2" "$FPJ"; cmpv
check "a failure that failed again on its isolated rerun at the base is STILL, verdict clean" '[ $CRC = 0 ] && grep -q "\"verdict\": \"clean\"" "$CO" && grep -q "t1" "$CO"'
mkres "$CB" FAIL "$F1" "$FPJ"; cmpv
check "a base failure that was not confirmed on its rerun is no baseline: the same failure at the head is NEW (exit 4)" '[ $CRC = 4 ]'
FS='[{"name":"t1","first_error":"boom","failed_twice":false}]'; FN='[{"name":"t1","first_error":"not-rerun: boom","failed_twice":false}]'
mkres "$CB" FAIL "$FS" "$FPJ"; cmpv
check "a still-class base failure (failed at its own base, stored failed_twice false with its error) is a baseline: STILL, verdict clean" '[ $CRC = 0 ] && grep -q "\"verdict\": \"clean\"" "$CO"'
check "...and the base's failure list names it" '[ "$(node "$TOOLS" keys "$CB")" = t1 ]'
mkres "$CB" FAIL "$FN" "$FPJ"; cmpv
check "a base row that was never rerun (not-rerun) is no baseline: NEW (exit 4) (nearest non-match)" '[ $CRC = 4 ] && [ -z "$(node "$TOOLS" keys "$CB")" ]'
mkres "$CB" SYSTEMIC "$F2" "$FPJ"; cmpv
check "a base that ended SYSTEMIC is incomparable (exit 5), never a baseline" '[ $CRC = 5 ] && grep -q "SYSTEMIC" "$CO"'
mkres "$CB" ERROR "$F2" "$FPJ"; cmpv
check "...and so is one that ended ERROR" '[ $CRC = 5 ]'
mkres "$CB" FLAKY-PASS "[]" "$FPJ"; cmpv
check "a base that ended FLAKY-PASS is a baseline (nearest non-match): its failures are none, so the head failure is NEW" '[ $CRC = 4 ]'
mkres "$CB" FAIL "$F2" "$(node -e 'const f=JSON.parse(process.argv[1]); f.workers+=1; console.log(JSON.stringify(f))' "$FPJ")"; cmpv
check "a base run with another worker count is incomparable (exit 5)" '[ $CRC = 5 ] && grep -q "workers" "$CO"'
mkres "$CB" FAIL "$F2" "$(node -e 'const f=JSON.parse(process.argv[1]); f.sandbox=true; console.log(JSON.stringify(f))' "$FPJ")"; cmpv
check "a base run with another sandbox flag is incomparable (exit 5)" '[ $CRC = 5 ] && grep -q "sandbox" "$CO"'
WK() { env "$@" node "$TOOLS" env | node -e 'console.log(JSON.parse(require("fs").readFileSync(0,"utf8")).workers)'; }
check "the fingerprint records run-all's suite worker count: two thirds of the cpus up to 12, and no more than the granted test tokens under a held grant" \
  '[ "$(WK AH_TEST_SLOT_HELD=1 AH_TEST_TOKENS_GRANTED=1)" = 1 ] && [ "$(WK AH_TEST_SLOT_HELD=1 AH_TEST_TOKENS_GRANTED=1000)" -ge 1 ] && [ "$(WK AH_TEST_SLOT_HELD=0)" -le 12 ]'
check "...the granted tokens do not change it when no slot is held (nearest non-match)" '[ "$(WK AH_TEST_SLOT_HELD=0 AH_TEST_TOKENS_GRANTED=1)" = "$(WK AH_TEST_SLOT_HELD=0 AH_TEST_TOKENS_GRANTED=1000)" ]'

# a base is run to the end: 13 failures at the base are not SYSTEMIC there, and the stored base is FAIL
mkcommit sb-bs-base; printf 'Ah.Fake.Tests.C.B1\nAh.Fake.Tests.C.B2\nAh.Fake.Tests.C.B3\nAh.Fake.Tests.C.B4\nAh.Fake.Tests.C.B5\nAh.Fake.Tests.C.B6\nAh.Fake.Tests.C.B7\nAh.Fake.Tests.C.B8\nAh.Fake.Tests.C.B9\nAh.Fake.Tests.C.B10\nAh.Fake.Tests.C.B11\nAh.Fake.Tests.C.B12\nAh.Fake.Tests.C.B13\n' > "$ST/fails"
BSB=$SHA; BSF=$(cat "$ST/fails")
mkcommit sb-bs-head; printf '%s\n' "$BSF" > "$ST/fails"
rs "$SHA" dotnet --base "$BSB"
check "a base run for --base has no systemic limit: 13 failures there are stored as FAIL, not SYSTEMIC, and 13 STILL at the head are a clean verdict" \
  '[ "$(node "$TOOLS" field "$AH_RESULTS_DIR/$BSB/dotnet/result.json" status)" = FAIL ] && [ $RC = 0 ] && [ "$(vjson dotnet "r.classes.still.length")" = 13 ]'
mkcommit sb-bs-plain; printf '%s\n' "$BSF" > "$ST/fails"
rs "$SHA" dotnet
check "the same 13 failures run without --base are SYSTEMIC (nearest non-match: the limits stay for a plain run)" '[ "$(rjson dotnet status)" = SYSTEMIC ]'

# the base must be an ancestor of the sha
rs "$BSB" dotnet --base "$SHA"
check "a --base that is not an ancestor of the sha is a usage error (exit 2)" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "ancestor"'

# in_session ignores the variables that configure a CLI
INS() { env -i PATH="$PATH" "$@" node "$PLUGIN/tests/run-set-tools.mjs" env | node -e 'console.log(JSON.parse(require("fs").readFileSync(0,"utf8")).in_session)'; }
check "CODEX_HOME and CLAUDE_CONFIG_DIR alone do not make a run in-session" '[ "$(INS CODEX_HOME=/x CLAUDE_CONFIG_DIR=/y)" = false ]'
check "...another CODEX_ variable does (nearest non-match), and so do CLAUDECODE and HERDR_PANE_ID" '[ "$(INS CODEX_HOME=/x CODEX_FOO=1)" = true ] && [ "$(INS CLAUDECODE=1)" = true ] && [ "$(INS HERDR_PANE_ID=p)" = true ]'

# a base that cannot be run
mkcommit sb-err-base; touch "$ST/dotnet.build-fails"
EBASE=$SHA
mkcommit sb-err-head; : > "$ST/fails"
rs "$SHA" dotnet --base "$EBASE"
check "a base whose run could not complete leaves the verdict incomparable (exit 5), says it could not be run, and the head is still judged on its own: PASS" '[ $RC = 5 ] && [ "$(rjson dotnet status)" = PASS ] && printf %s "$OUT" | grep -q "could not be run"'

# full
mkcommit sb-full-base; : > "$ST/fails"; : > "$ST/shell.fails"
FBASE=$SHA
mkcommit sb-full-head; printf 'Ah.Fake.Tests.C.Z\n' > "$ST/fails"; : > "$ST/shell.fails"
rs "$SHA" full --base "$FBASE"
check "full with --base: a dotnet failure absent at the base is NEW under full, with its set, exit 4" '[ $RC = 4 ] && [ "$(vjson full r.verdict)" = "\"new-failures\"" ] && [ "$(vjson full r.classes.new)" = "[\"dotnet|Ah.Fake.Tests.C.Z\"]" ]'

# a full base that exists only as its components
mkcommit sb-fullc-base; : > "$ST/fails"; : > "$ST/shell.fails"
FCB=$SHA; rs "$FCB" dotnet; rs "$FCB" shell
mkcommit sb-fullc-head; printf 'Ah.Fake.Tests.C.Z\n' > "$ST/fails"
rs "$SHA" full --base "$FCB"
check "full with --base where the base was run as dotnet and shell apart: its full result is made, the verdict compares (NEW dotnet|Z, exit 4), not incomparable" \
  '[ $RC = 4 ] && [ -f "$AH_RESULTS_DIR/$FCB/full/result.json" ] && [ "$(vjson full r.classes.new)" = "[\"dotnet|Ah.Fake.Tests.C.Z\"]" ]'

check "the verdict is in <sha>/<set>/verdict-<base>.json with the sha, set and base, and result.json carries no verdict fields" \
  '[ "$(vjson full r.sha)" = "\"$SHA\"" ] && [ "$(vjson full r.set)" = "\"full\"" ] && [ "$(vjson full r.base)" = "\"$FCB\"" ] && [ -f "$AH_RESULTS_DIR/$SHA/full/verdict-$FCB.json" ] && ! grep -q "\"verdict\"" "$AH_RESULTS_DIR/$SHA/full/result.json"'
rs "$SHA" full
check "a caller without --base on the same result prints no verdict and reuses it (nearest non-match)" '! printf %s "$OUT" | grep -q "verdict=" && printf %s "$OUT" | grep -q "cached=yes"'
VF1="$AH_RESULTS_DIR/$SHA/full/verdict-$FCB.json"
check "a verdict records the hashes of the head and base results and verdictcheck accepts it while they match" \
  '[ -f "$VF1" ] && node "$TOOLS" verdictcheck "$VF1" "$AH_RESULTS_DIR/$SHA/full/result.json" "$AH_RESULTS_DIR/$FCB/full/result.json" | grep -qx ok'
cp "$AH_RESULTS_DIR/$SHA/full/result.json" "$SB/head.keep"; printf '\n' >> "$AH_RESULTS_DIR/$SHA/full/result.json"
check "...a head result.json that changed after the verdict makes it stale (exit 1)" '! node "$TOOLS" verdictcheck "$VF1" "$AH_RESULTS_DIR/$SHA/full/result.json" "$AH_RESULTS_DIR/$FCB/full/result.json" >/dev/null'
cp "$SB/head.keep" "$AH_RESULTS_DIR/$SHA/full/result.json"; cp "$AH_RESULTS_DIR/$FCB/full/result.json" "$SB/base.keep"; printf '\n' >> "$AH_RESULTS_DIR/$FCB/full/result.json"
check "...and so does a base result.json that changed" '! node "$TOOLS" verdictcheck "$VF1" "$AH_RESULTS_DIR/$SHA/full/result.json" "$AH_RESULTS_DIR/$FCB/full/result.json" >/dev/null'
cp "$SB/base.keep" "$AH_RESULTS_DIR/$FCB/full/result.json"
# a run that replaces the result deletes the verdicts that judged the old one
printf 'Ah.Fake.Tests.C.Z\n' > "$ST/fails"
rs "$SHA" full --fresh
check "a --fresh run without --base that rewrites the result deletes the set's verdict files (a stale clean verdict cannot outlive it)" '[ ! -e "$VF1" ]'
rs "$SHA" full --base "$FCB"
check "...and running with --base again makes a new verdict that checks out (nearest non-match)" '[ -f "$VF1" ] && node "$TOOLS" verdictcheck "$VF1" "$AH_RESULTS_DIR/$SHA/full/result.json" "$AH_RESULTS_DIR/$FCB/full/result.json" | grep -qx ok'

# --force is no caller's option: only the base run that resolve_base starts reruns a stored result
rs "$SHA" shell --force
check "--force is not an option (exit 2)" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "unknown option"'

# a base kept from before the fingerprint had a worker count is made again where this run is, whichever components it needs
stripw() { node -e 'const fs=require("fs"),f=process.argv[1],r=JSON.parse(fs.readFileSync(f,"utf8"));delete r.env.workers;fs.writeFileSync(f,JSON.stringify(r))' "$1"; }
hasw() { node -e 'const r=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));process.exit(r.env&&r.env.workers>0?0:1)' "$1"; }
mkcommit fp-old-base; : > "$ST/fails"; : > "$ST/shell.fails"
OLDB=$SHA; rs "$OLDB" dotnet
stripw "$AH_RESULTS_DIR/$OLDB/dotnet/result.json"
mkcommit fp-old-head; : > "$ST/fails"; : > "$ST/shell.fails"
rs "$SHA" full --base "$OLDB"
check "a base whose stored dotnet result has no worker count (its shell result absent) is run again for it: both fingerprints carry workers and the verdict compares (exit 0)" \
  '[ $RC = 0 ] && hasw "$AH_RESULTS_DIR/$OLDB/full/result.json" && hasw "$AH_RESULTS_DIR/$OLDB/dotnet/result.json" && hasw "$AH_RESULTS_DIR/$SHA/full/result.json" && [ "$(vjson full r.verdict)" = "\"clean\"" ]'
stripw "$AH_RESULTS_DIR/$OLDB/dotnet/result.json"; stripw "$AH_RESULTS_DIR/$OLDB/shell/result.json"; stripw "$AH_RESULTS_DIR/$OLDB/full/result.json"
mkcommit fp-old-head2; : > "$ST/fails"; : > "$ST/shell.fails"
rs "$SHA" full --base "$OLDB" --fresh-base
check "--fresh-base with every stored base result lacking workers: all of them are made again and the verdict compares (exit 0)" \
  '[ $RC = 0 ] && hasw "$AH_RESULTS_DIR/$OLDB/full/result.json" && hasw "$AH_RESULTS_DIR/$OLDB/shell/result.json" && [ "$(vjson full r.verdict)" = "\"clean\"" ]'
# a full base whose parts were made under different fingerprints has none of its own
mkcommit fp-mix-base; : > "$ST/fails"; : > "$ST/shell.fails"
MIXB=$SHA; rs "$MIXB" dotnet; rs "$MIXB" shell
stripw "$AH_RESULTS_DIR/$MIXB/shell/result.json"
rs "$MIXB" full
check "a full result merged from a part without workers and parts with them carries no fingerprint" '! hasw "$AH_RESULTS_DIR/$MIXB/full/result.json" && [ "$(node -e "const r=JSON.parse(require(\"fs\").readFileSync(process.argv[1],\"utf8\"));console.log(r.env===undefined)" "$AH_RESULTS_DIR/$MIXB/full/result.json")" = true ]'
DOT0=$(cksum < "$AH_RESULTS_DIR/$MIXB/dotnet/result.json")
mkcommit fp-mix-head; : > "$ST/fails"; : > "$ST/shell.fails"
rs "$SHA" full --base "$MIXB"
check "without --fresh-base such a base is incomparable (exit 5) and the part without workers is not remade" '[ $RC = 5 ] && [ "$(vjson full r.verdict)" = "\"incomparable\"" ] && ! hasw "$AH_RESULTS_DIR/$MIXB/shell/result.json"'
mkcommit fp-mix-head2; : > "$ST/fails"; : > "$ST/shell.fails"
rs "$SHA" full --base "$MIXB" --fresh-base
check "--fresh-base remakes the part without workers only: shell has them now, the dotnet result is the same file, the verdict compares (exit 0)" \
  '[ $RC = 0 ] && hasw "$AH_RESULTS_DIR/$MIXB/shell/result.json" &&hasw "$AH_RESULTS_DIR/$MIXB/full/result.json" && [ "$(cksum < "$AH_RESULTS_DIR/$MIXB/dotnet/result.json")" = "$DOT0" ]'

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
