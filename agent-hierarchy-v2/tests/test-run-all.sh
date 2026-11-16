#!/bin/bash
# agent-hierarchy — tests/run-all.sh: the parallel bound, the serial lane after the parallel lane, the result set, the
# per-test timeout, the per-test environment, the machine-wide lock and the paths it refuses. The runner runs here on a
# directory of fake tests, with a lock of its own under the sandbox, so it never touches a real loop or a real suite.
# Usage: bash tests/test-run-all.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
RUNNER="$PLUGIN/tests/run-all.sh"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:600})"; fi; }

FT="$SB/tests"; mkdir -p "$FT"
LOG="$SB/events.log"
# Four fast fakes that record when they start and end, one that fails, one slow one for the timeout, one for the serial lane.
for n in a b c d; do
  printf '#!/bin/bash\necho "start par-%s" >>"%s"; sleep 1; echo "end par-%s" >>"%s"\n' "$n" "$LOG" "$n" "$LOG" >"$FT/test-par-$n.sh"
done
printf '#!/bin/bash\necho "failing on purpose"; exit 1\n' >"$FT/test-bad.sh"
printf '#!/bin/bash\nsleep 6\n' >"$FT/test-slow.sh"
printf '#!/bin/bash\necho "start ser" >>"%s"; sleep 1; echo "end ser" >>"%s"\n' "$LOG" "$LOG" >"$FT/test-ser.sh"
printf '#!/bin/bash\n[ "$AH_DAEMON" = off ] && case "$HOME:$AH_HOME:$TMPDIR" in /private/tmp/ahprobe.??????/t*/home:/private/tmp/ahprobe.??????/t*/ah:/private/tmp/ahprobe.??????/t*/tmp) echo "$HOME" >>"%s/homes.log"; exit 0;; esac\necho "bad env: $HOME $AH_HOME $TMPDIR $AH_DAEMON"; exit 1\n' "$SB" >"$FT/test-env1.sh"
cp "$FT/test-env1.sh" "$FT/test-env2.sh"

LK="$SB/lock"
run() { AH_RUN_ALL_LEAK="${LEAK:-off}" AH_RUN_ALL_SETTLE=0 AH_RUN_ALL_TESTS="${DIR:-$FT}" AH_RUN_ALL_LOCK="$LK" AH_RUN_ALL_SERIAL="${SER:-}" bash "$RUNNER" "$@" 2>&1; }
maxrun() { awk '/^start/ {n++; if (n > m) m = n} /^end/ {n--} END {print m + 0}' "$LOG"; }
drop_kept() { local p; p=$(printf '%s\n' "$OUT" | sed -n 's/^run-all: kept \(\/private\/tmp\/ahprobe\.[A-Za-z0-9]\{6\}\).*/\1/p' | head -1); case "$p" in /private/tmp/ahprobe.??????) rm -rf "$p";; esac; }

# 1. The parallel bound: four one-second tests under -j 2 overlap two at a time, under -j 1 never.
: >"$LOG"; OUT=$(run -j 2 par-); RC=$?
check "-j 2 on four tests: at most two overlap, and two do" '[ "$(maxrun)" = 2 ]'
check "-j 2: all four pass and the run exits 0" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "4/4 passed"'
: >"$LOG"; OUT=$(run -j 1 par-)
check "-j 1: no two tests overlap (the nearest non-match of the bound above)" '[ "$(maxrun)" = 1 ]'

# 2. The serial lane runs after every parallel test has ended; unnamed, the same test runs beside the others.
FT2="$SB/tests2"; mkdir -p "$FT2"; cp "$FT"/test-par-?.sh "$FT/test-ser.sh" "$FT2/"
: >"$LOG"; SER="test-ser"; OUT=$(DIR="$FT2" run -j 5); SER=""
check "named in the serial lane, the test starts after the last parallel test ends" '[ "$(awk "/^start ser/ {s=NR} /^end par/ {e=NR} END {print (s > e)}" "$LOG")" = 1 ]'
: >"$LOG"; OUT=$(DIR="$FT2" run -j 5)
check "not named, the same test starts beside the others, before the last of them ends" '[ "$(awk "/^start ser/ {s=NR} /^end par/ {e=NR} END {print (s < e)}" "$LOG")" = 1 ]'

# 3. Result set: a failing test fails the run, is named, and the set file lists exactly it as FAIL.
FT3="$SB/tests3"; mkdir -p "$FT3"; cp "$FT/test-bad.sh" "$FT"/test-par-[ab].sh "$FT3/"
SET="$SB/set.txt"; OUT=$(DIR="$FT3" run -j 4 -o "$SET"); RC=$?
check "a failing test makes the run exit 1 and names it" '[ $RC = 1 ] && printf %s "$OUT" | grep -q "failing: test-bad"'
check "the result set has the failing test as FAIL and the passing ones as ok" 'grep -qx "test-bad FAIL" "$SET" && grep -qx "test-par-a ok" "$SET" && grep -qx "test-par-b ok" "$SET" && [ "$(wc -l <"$SET" | tr -d " ")" = 3 ]'
drop_kept
OUT=$(run -j 4 -o "$SET" par-); RC=$?
check "a prefix that selects only passing tests exits 0 and its set has no FAIL" '[ $RC = 0 ] && ! grep -q FAIL "$SET" && [ "$(wc -l <"$SET" | tr -d " ")" = 4 ]'

# 4. Per-test timeout: a six-second test under -t 2 is a TIMEOUT; the same test under a long limit is not.
OUT=$(run -j 2 -t 2 -o "$SET" slow); RC=$?
check "a test over the timeout is reported TIMEOUT and fails the run" '[ $RC = 1 ] && grep -qx "test-slow TIMEOUT" "$SET"'
drop_kept
OUT=$(run -j 2 -t 30 -o "$SET" slow); RC=$?
check "the same test under a longer timeout passes" '[ $RC = 0 ] && grep -qx "test-slow ok" "$SET"'

# 5. The per-test environment: own HOME/AH_HOME/TMPDIR under the run directory, AH_DAEMON=off, distinct per test.
rm -f "$SB/homes.log"; OUT=$(run -j 2 env); RC=$?
check "each test gets HOME/AH_HOME/TMPDIR in its own t<NNN> directory under a fresh run dir, with the daemon off" '[ $RC = 0 ] && [ "$(wc -l <"$SB/homes.log" | tr -d " ")" = 2 ]'
check "two tests do not share a HOME" '[ "$(sort -u "$SB/homes.log" | wc -l | tr -d " ")" = 2 ]'
check "a fully green run removes its run directory" '! printf %s "$OUT" | grep -q "run-all: kept"'
OUT=$(run -j 2 bad); drop_kept
check "a red run keeps its run directory and says where" 'printf %s "$OUT" | grep -q "run-all: kept /private/tmp/ahprobe\."'

# 6. The lock: held by someone else means exit 3 with the lock and its owner untouched; free means it is taken and released.
mkdir "$LK"; echo "someone else" >"$LK/owner"
OUT=$(run -j 2 par-); RC=$?
check "a held lock refuses the run with exit 3 and names the owner" '[ $RC = 3 ] && printf %s "$OUT" | grep -q "someone else"'
check "T11: the refusal says not to retry in a loop, to run the touched suites, and points at the policy in the runner's header" 'printf %s "$OUT" | grep -q "do not retry in a loop" && printf %s "$OUT" | grep -q "Run only the suites you touched" && printf %s "$OUT" | grep -q "Concurrency policy" && grep -q "^# Concurrency policy" "$RUNNER"'
check "the held lock and its owner file are still there afterwards" '[ "$(cat "$LK/owner")" = "someone else" ]'
rm -f "$LK/owner"; rmdir "$LK"
OUT=$(run -j 2 par-); RC=$?
check "a free lock is taken for the run and released after it" '[ $RC = 0 ] && [ ! -e "$LK" ]'
OUT=$(run -j 4 bad); RC=$?; drop_kept
check "the lock is released after a red run too" '[ $RC = 1 ] && [ ! -e "$LK" ]'
# 6b. AH_RUN_ALL_LOCK_WAIT=1 turns a held lock into a bounded wait; the default stays a refusal.
mkdir "$LK"; echo "run-all pid 99999999 2000-01-01T00:00:00Z" >"$LK/owner"
OUT=$(run -j 2 par-); RC=$?
check "without the wait variable a dead owner's lock is still refused at once (exit 3) and left alone" '[ $RC = 3 ] && [ -d "$LK" ] && grep -q 99999999 "$LK/owner"'
OUT=$(AH_RUN_ALL_LOCK_WAIT=1 AH_TEST_SLOT_WAIT=30 run -j 2 par-); RC=$?
check "with the wait variable a lock whose owner pid is dead is taken over and the run goes ahead" '[ $RC = 0 ] && [ ! -e "$LK" ] && [ ! -e "$LK.stale.$$" ]'
mkdir "$LK"; echo "run-all pid $$ 2000-01-01T00:00:00Z" >"$LK/owner"
( sleep 3; rm -f "$LK/owner"; rmdir "$LK" ) &
REL=$!
T0=$(date +%s)
OUT=$(AH_RUN_ALL_LOCK_WAIT=1 AH_TEST_SLOT_WAIT=30 run -j 2 par-); RC=$?
T1=$(date +%s)
wait "$REL"
check "with the wait variable a lock held by a live owner is waited for, then the run goes ahead" '[ $RC = 0 ] && [ $((T1 - T0)) -ge 3 ] && [ ! -e "$LK" ]'
mkdir "$LK"; echo "run-all pid $$ 2000-01-01T00:00:00Z" >"$LK/owner"
OUT=$(AH_RUN_ALL_LOCK_WAIT=1 AH_TEST_SLOT_WAIT=2 run -j 2 par-); RC=$?
check "a wait that runs out exits 75, and the live owner's lock and owner file are untouched" '[ $RC = 75 ] && [ -d "$LK" ] && grep -q "pid $$ " "$LK/owner"'
rm -f "$LK/owner"; rmdir "$LK"

# 7. Paths and arguments it refuses.
for bad in /private/tmp/other.lock /tmp/ahprobe.xyz "/private/tmp/ahprobe.x/../y" /Users/nobody/ahprobe.lock; do
  OUT=$(AH_RUN_ALL_TESTS="$FT" AH_RUN_ALL_LOCK="$bad" bash "$RUNNER" par- 2>&1); RC=$?
  check "a lock path of '$bad' is refused with exit 2 and nothing is created" '[ $RC = 2 ] && [ ! -e "$bad" ]'
done
OUT=$(run -j 0 par-); RC=$?
check "-j 0 is refused" '[ $RC = 2 ]'
OUT=$(run -j x par-); RC=$?
check "-j with a non-number is refused" '[ $RC = 2 ]'
# 8. The daemon check: a process naming an ahd.mjs that carries the run's marker and outlives the run is reported and
# fails it; an unmarked one on a home that is not a temp directory is not. Each plant sets its marker explicitly.
FT4="$SB/tests4"; mkdir -p "$FT4"
cat >"$FT4/test-plant.sh" <<'PLANT'
#!/bin/bash
AHD_LEAK_RUN="$AHD_LEAK_RUN" nohup node -e 'setTimeout(() => {}, 60000)' /x/ahd.mjs --ah-home /private/tmp/ahprobe.leakplant >/dev/null 2>&1 &
env -u AHD_LEAK_RUN nohup node -e 'setTimeout(() => {}, 60000)' /x/ahd.mjs --ah-home /Users/nobody/ah-elsewhere >/dev/null 2>&1 &
exit 0
PLANT
OUT=$(LEAK=on DIR="$FT4" run -j 1); RC=$?; drop_kept
pkill -f -- '--ah-home /private/tmp/ahprobe.leakplant' 2>/dev/null; pkill -f -- '--ah-home /Users/nobody/ah-elsewhere' 2>/dev/null  # pid-owned: patterns name the two daemons this test planted
check "a daemon left under a test temp directory fails the run and is named" '[ $RC = 1 ] && printf %s "$OUT" | grep -q "ahprobe.leakplant" && printf %s "$OUT" | grep -q "daemon-leak"'
check "...and its report carries the pid, parent, start time, AH_HOME and command line" 'printf %s "$OUT" | grep -q "^pid=[0-9]* ppid=[0-9]* start=\"" && printf %s "$OUT" | grep -q " argv=node .*--ah-home /private/tmp/ahprobe.leakplant"'
check "an unmarked daemon on a home that is not a temp directory is not counted as a leak" '! printf %s "$OUT" | sed -n "/^LEAK:/,/^not this run/p" | grep -q "ah-elsewhere"'
check "...and is printed under the not-this-run's heading with its attribution" 'printf %s "$OUT" | sed -n "/^not this run/,\$p" | grep -q "^pid=[0-9]* ppid=.* argv=node .*ah-elsewhere"'

# 9. A signal to the runner mid-test: the running test and what it started are stopped, and the lock is released only
# after they are gone. The fake test (tests/fixtures/run-all-term-fake.sh) takes a second to finish after TERM, so a
# runner that releases the lock without waiting for it is caught as "early". perl resets SIGINT, which a background
# job would otherwise ignore.
signal_scenario() { # <runner> <signal>: S_SURV S_ORDER S_LOCK
  local runner=$1 sig=$2 M pid i P1 P2 S1 S2
  M="$(mktemp -d "$SB/sig.XXXXXX")"; mkdir "$M/t"
  sed "s|@M@|$M|g" "$PLUGIN/tests/fixtures/run-all-term-fake.sh" >"$M/t/test-hang.sh"
  AH_RUN_ALL_TESTS="$M/t" AH_RUN_ALL_LOCK="$M/lock" AH_RUN_ALL_SERIAL="" AH_RUN_ALL_LEAK=off \
    perl -e '$SIG{INT} = "DEFAULT"; exec @ARGV' bash "$runner" -j 1 -t 120 >"$M/out" 2>&1 &
  pid=$!
  i=0; while { [ ! -s "$M/gc.pid" ] || [ ! -s "$M/t.pid" ]; } && [ "$i" -lt 150 ]; do sleep 0.1; i=$((i + 1)); done
  P1=$(cat "$M/t.pid" 2>/dev/null); P2=$(cat "$M/gc.pid" 2>/dev/null)
  S1=$(ah_pid_stamp "$P1"); S2=$(ah_pid_stamp "$P2")
  ( while [ -d "$M/lock" ]; do sleep 0.02; done; if [ -e "$M/termed" ]; then echo ordered >"$M/order"; else echo early >"$M/order"; fi ) &
  ah_signal_child "$sig" "$pid"
  wait "$pid" 2>/dev/null
  sleep 0.5
  S_SURV=0
  if [ -n "$S1" ] && [ "$(ah_pid_stamp "$P1")" = "$S1" ]; then S_SURV=$((S_SURV + 1)); ah_signal_stamped KILL "$P1" "$S1"; fi
  if [ -n "$S2" ] && [ "$(ah_pid_stamp "$P2")" = "$S2" ]; then S_SURV=$((S_SURV + 1)); ah_signal_stamped KILL "$P2" "$S2"; fi
  S_ORDER=$(cat "$M/order" 2>/dev/null); [ -e "$M/lock" ] && S_LOCK=held || S_LOCK=gone
}
for s in TERM INT HUP; do
  signal_scenario "$RUNNER" $s
  check "$s to the runner mid-test: no test process survives, the lock is gone, and it went only after the test ended" '[ "$S_SURV" = 0 ] && [ "$S_LOCK" = gone ] && [ "$S_ORDER" = ordered ]'
done
mkdir -p "$SB/mut/tests"; cp "$RUNNER" "$PLUGIN/tests/lib-daemon-leak.sh" "$SB/mut/tests/"
sed -i.bak 's/^kill_test_groups() {$/kill_test_groups() { return 0/' "$SB/mut/tests/run-all.sh"
signal_scenario "$SB/mut/tests/run-all.sh" TERM
check "without the group kill the same row fails: the test survives and the lock goes early (the check can fail)" '[ "$S_SURV" -ge 1 ] && [ "$S_ORDER" = early ]'

# 10. The serial lane parses: every test named in the script's list comes out of the runner's own parse, nothing on
# stderr while the script is read, and every named file exists. A reason with a double quote inside a double-quoted
# list (the way this once broke) is caught by the same check.
lane_report() { # <runner>: LANE_ERR LANE_OUT
  LANE_ERR=$(bash "$1" --serial-lane 2>&1 >/dev/null)
  LANE_OUT=$(bash "$1" --serial-lane 2>/dev/null)
}
LISTED=$(awk "/^SERIAL_LANE='/{f=1;next} f&&/^'/{exit} f&&/^test-/{print \$1}" "$RUNNER")
lane_report "$RUNNER"
check "the runner reads with nothing on stderr" '[ -z "$LANE_ERR" ]'
check "the serial lane it parses is exactly the tests the list names, and the list is not empty" '[ -n "$LISTED" ] && [ "$LANE_OUT" = "$LISTED" ]'
MISSING=""; for n in $LISTED; do [ -f "$PLUGIN/tests/$n.sh" ] || MISSING="$MISSING $n"; done
check "every test named in the serial lane exists" '[ -z "$MISSING" ]'
mkdir -p "$SB/lane/tests"; cp "$RUNNER" "$SB/lane/tests/run-all.sh"
sed -i.bak -e "/^SERIAL_LANE='\$/,/^'\$/{s/^SERIAL_LANE='\$/SERIAL_LANE=\"/;s/^'\$/\"/;s/^test-test-env  /test-test-env  # a \"two words\" here # /;}" "$SB/lane/tests/run-all.sh"
lane_report "$SB/lane/tests/run-all.sh"
check "a double quote inside a double-quoted lane list is caught (stderr output or a different lane)" '[ -n "$LANE_ERR" ] || [ "$LANE_OUT" != "$LISTED" ]'

runner_dirs() { ls -d /private/tmp/ahprobe.??????/par.list 2>/dev/null | sort; }
BEFORE=$(runner_dirs)
OUT=$(run nosuchprefix-); RC=$?
check "a prefix that matches no test exits 2, not 0, takes no lock and leaves no run directory" '[ $RC = 2 ] && [ ! -e "$LK" ] && [ "$(runner_dirs)" = "$BEFORE" ]'

# --exact NAME runs the one test file named, not the tests whose names it prefixes; a PREFIX is still a glob.
FT5="$SB/tests5"; mkdir -p "$FT5"
for n in a a-b ab; do printf '#!/bin/bash\nexit 0\n' >"$FT5/test-$n.sh"; done
OUT=$(DIR="$FT5" run -j 1 -o "$SET" a); RC=$?
check "a PREFIX stays a glob: a runs a, a-b and ab" '[ $RC = 0 ] && [ "$(wc -l <"$SET" | tr -d " ")" = 3 ] && grep -qx "test-a ok" "$SET" && grep -qx "test-a-b ok" "$SET" && grep -qx "test-ab ok" "$SET"'
OUT=$(DIR="$FT5" run -j 1 --exact a -o "$SET"); RC=$?
check "--exact a runs test-a alone and not test-a-b or test-ab" '[ $RC = 0 ] && [ "$(cat "$SET")" = "test-a ok" ]'
OUT=$(DIR="$FT5" run -j 1 --exact a-b -o "$SET"); RC=$?
check "--exact a-b runs test-a-b alone" '[ $RC = 0 ] && [ "$(cat "$SET")" = "test-a-b ok" ]'
OUT=$(DIR="$FT5" run --exact nosuch); RC=$?
check "--exact with no such test exits 2 and runs nothing" '[ $RC = 2 ] && ! printf %s "$OUT" | grep -q "passed"'
OUT=$(DIR="$FT5" run --exact a a); RC=$?
check "--exact together with a PREFIX exits 2" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "cannot be given together"'
OUT=$(DIR="$FT5" run --exact "a*"); RC=$?
check "--exact with a glob character exits 2 instead of selecting more than one test" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "plain test name"'
OUT=$(DIR="$FT5" run --exact); RC=$?
check "--exact with no value exits 2" '[ $RC = 2 ]'

# 6. AHD_NATIVE_BIN names the ahd file: a directory or a non-executable file is refused, naming the expected path.
: > "$SB/notexec"; chmod 644 "$SB/notexec"; printf '#!/bin/sh\nexit 0\n' > "$SB/ahd-ok"; chmod 755 "$SB/ahd-ok"
OUT=$(AHD_NATIVE_BIN="$SB" DIR="$FT5" run --exact a-b); RC=$?
check "AHD_NATIVE_BIN naming a directory exits 2 and names the expected file" '[ $RC = 2 ] && printf %s "$OUT" | grep -qF "expected e.g. $SB/ahd" && ! printf %s "$OUT" | grep -q "passed"'
OUT=$(AHD_NATIVE_BIN="$SB/notexec" DIR="$FT5" run --exact a-b); RC=$?
check "AHD_NATIVE_BIN naming a file that is not executable exits 2" '[ $RC = 2 ] && printf %s "$OUT" | grep -qF "must name the ahd executable file"'
OUT=$(AHD_NATIVE_BIN="$SB/ahd-ok" DIR="$FT5" run --exact a-b); RC=$?
check "AHD_NATIVE_BIN naming an executable regular file runs the test (nearest non-match)" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "1/1 passed"'
OUT=$(AHD_NATIVE_BIN= DIR="$FT5" run --exact a-b); RC=$?
check "AHD_NATIVE_BIN empty is still the skip, not the refusal" 'printf %s "$OUT" | grep -q "SKIPPED" && ! printf %s "$OUT" | grep -q "must name the ahd executable file"'

# 6. Early abort: once AH_ABORT_MIN tests have finished, more than AH_ABORT_PCT percent failing stops new starts.
FT6="$SB/tests6"; mkdir -p "$FT6"
for i in 01 02 03 04; do printf '#!/bin/bash\necho "ERROR: cannot open /work/%s/file %s"; exit 1\n' "$i" "$i$i" >"$FT6/test-x$i.sh"; done
printf '#!/bin/bash\necho "FAIL: some other thing"; exit 1\n' >"$FT6/test-x05.sh"
for i in 06 07 08 09 10 11; do printf '#!/bin/bash\nexit 0\n' >"$FT6/test-x$i.sh"; done
SET6="$SB/set6.txt"
OUT=$(AH_ABORT_MIN=5 DIR="$FT6" run -j 1 -o "$SET6"); RC=$?
check "more than half of the first five failing aborts: exit 1, an aborted: systemic line, the rest not started" '[ $RC = 1 ] && printf %s "$OUT" | grep -q "aborted: systemic" && [ "$(wc -l <"$SET6" | tr -d " ")" = 5 ] && printf %s "$OUT" | grep -q "6 tests not started"'
check "...the line groups the failures by first error line, paths and numbers stripped: four alike, one other" 'printf %s "$OUT" | grep -q "4 x ERROR: cannot open  *\[test-x01, test-x02, test-x03\]" && printf %s "$OUT" | grep -q "1 x FAIL: some other thing"'
drop_kept
OUT=$(AH_ABORT_MIN=5 DIR="$FT6" run -j 1 --no-abort -o "$SET6"); RC=$?
check "--no-abort runs all eleven and prints no aborted line" '[ $RC = 1 ] && [ "$(wc -l <"$SET6" | tr -d " ")" = 11 ] && ! printf %s "$OUT" | grep -q "aborted:"'
drop_kept
OUT=$(DIR="$FT6" run -j 1 -o "$SET6"); RC=$?
check "below the default minimum of 20 finished tests nothing aborts (nearest non-match of the count)" '[ "$(wc -l <"$SET6" | tr -d " ")" = 11 ] && ! printf %s "$OUT" | grep -q "aborted:"'
drop_kept
FT7="$SB/tests7"; mkdir -p "$FT7"
for i in 02 04 06 08; do printf '#!/bin/bash\nexit 1\n' >"$FT7/test-y$i.sh"; done
for i in 01 03 05 07; do printf '#!/bin/bash\nexit 0\n' >"$FT7/test-y$i.sh"; done
OUT=$(AH_ABORT_MIN=4 DIR="$FT7" run -j 1 -o "$SET6"); RC=$?
check "exactly half failing is not more than half: no abort, all eight run (nearest non-match of the percentage)" '[ "$(wc -l <"$SET6" | tr -d " ")" = 8 ] && ! printf %s "$OUT" | grep -q "aborted:"'
drop_kept
OUT=$(AH_ABORT_MIN=4 AH_ABORT_PCT=40 DIR="$FT7" run -j 1 -o "$SET6"); RC=$?
check "AH_ABORT_PCT=40 turns the same half-failing set into an abort" 'printf %s "$OUT" | grep -q "aborted: systemic"'
drop_kept

node -e 'const fs=require("fs");const d=process.argv[1];fs.writeFileSync(d+"/manifest.json",JSON.stringify(fs.readdirSync(d).filter((f)=>/^test-.*[.]sh$/.test(f)).map((f)=>({name:f.slice(0,-3),kind:"static",libs:[],covers:[],units:[],lane:"fast",reads_source:false}))))' "$FT6"
OUT=$(AH_ABORT_MIN=1 DIR="$FT6" run -j 1 --lane fast --fail-fast); RC=$?
check "--fail-fast together with an abort still prints the aborted line and its groups" 'printf %s "$OUT" | grep -q "aborted: systemic" && printf %s "$OUT" | grep -q "1 x ERROR: cannot open"'
drop_kept

# A suite is told where to keep its failure diffs: under the directory run-set names, else under the run's own kept directory
FTD="$SB/tests-faildir"; mkdir -p "$FTD"; FDO="$SB/faildir.out"
printf '#!/bin/bash\necho "$AH_TEST_FAIL_DIR" >"%s"; echo "${AH_RUN_ALL_FAIL_DIR:-unset}" >>"%s"\n' "$FDO" "$FDO" >"$FTD/test-fd.sh"
OUT=$(AH_RUN_ALL_FAIL_DIR="$SB/kept" DIR="$FTD" run fd); RC=$?
check "AH_RUN_ALL_FAIL_DIR reaches a suite as its own AH_TEST_FAIL_DIR" '[ $RC = 0 ] && [ "$(sed -n 1p "$FDO")" = "$SB/kept/test-fd" ]'
check "...and stays with the runner, so a run the suite starts cannot write there (nearest non-match)" '[ "$(sed -n 2p "$FDO")" = unset ]'
OUT=$(DIR="$FTD" run fd)
check "with none named, the suite keeps them under the run" 'case "$(sed -n 1p "$FDO")" in /private/tmp/ahprobe.??????/fail/test-fd) true;; *) false;; esac'

echo "$PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
