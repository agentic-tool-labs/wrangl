#!/bin/bash
# agent-hierarchy — tests/lib-wait.sh: the bounded wait. A true condition returns at once, one that becomes true is waited for, and one
# that never does stops the suite after its tries (capped at 60, thirty seconds), naming the condition, printing the end of the log the
# suite named, and giving the suite's tally with the failure counted.
# Usage: bash tests/test-lib-wait.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
HERE="$(cd "$(dirname "$0")" && pwd)"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:500})"; fi; }

# w <tries> <condition>: the wait in a shell of its own with the suite's tally of 3 passed and 1 failed; RC and OUT are its exit and output
w() { OUT=$(PASS=3 FAIL=1 bash -c '. "$0"; waitfor "$2" "$1"; echo returned' "$HERE/lib-wait.sh" "$@" 2>&1); RC=$?; }

w 10 true
check "a condition that is true at once returns 0 and the suite goes on" '[ $RC = 0 ] && [ "$OUT" = returned ]'
rm -f "$SB/flag"; ( sleep 1; touch "$SB/flag" ) &
w 10 "[ -f $SB/flag ]"
check "a condition that becomes true within its tries is waited for: the suite goes on" '[ $RC = 0 ] && [ "$OUT" = returned ]'
wait

S=$SECONDS
w 4 "[ -f $SB/never ]"
T=$((SECONDS - S))
check "a condition that never comes true stops the suite with exit 1 after its tries, not later" '[ $RC = 1 ] && [ "$T" -le 5 ] && ! printf %s "$OUT" | grep -q returned'
check "...it names the wait and the condition" 'printf %s "$OUT" | grep -q "^FAIL: waited 2 s for: \[ -f $SB/never \]"'
check "...and gives the tally with the failure counted: 3 passed, 2 failed" 'printf %s "$OUT" | grep -qx "passed: 3  failed: 2"'

# the cap: a suite asking for 1000 tries waits 60 (30 s). sleep is a function here so the test does not wait.
OUT=$(bash -c 'sleep() { :; }; tries_seen=0; cnt() { tries_seen=$((tries_seen + 1)); echo "$tries_seen" > "$1"; return 1; }; . "$0"; waitfor "cnt $1" 1000' "$HERE/lib-wait.sh" "$SB/count" 2>&1); RC=$?
check "a request for 1000 tries is cut to 60: the condition is tried 60 times and the wait says 30 s" '[ $RC = 1 ] && [ "$(cat "$SB/count")" = 60 ] && printf %s "$OUT" | grep -q "^FAIL: waited 30 s for:"'
OUT=$(bash -c 'sleep() { :; }; tries_seen=0; cnt() { tries_seen=$((tries_seen + 1)); echo "$tries_seen" > "$1"; return 1; }; . "$0"; waitfor "cnt $1" 60' "$HERE/lib-wait.sh" "$SB/count60" 2>&1); RC=$?
check "...60 tries are kept as asked (nearest non-match)" '[ "$(cat "$SB/count60")" = 60 ] && printf %s "$OUT" | grep -q "^FAIL: waited 30 s for:"'
OUT=$(bash -c 'sleep() { :; }; . "$0"; waitfor false abc' "$HERE/lib-wait.sh" 2>&1); RC=$?
check "a tries value that is not a number means the cap, not an endless loop" '[ $RC = 1 ] && printf %s "$OUT" | grep -q "waited 30 s"'

# the log
printf 'first line\nsecond line\n' > "$SB/daemon.log"
OUT=$(WAITFOR_LOG="$SB/daemon.*" PASS=0 FAIL=0 bash -c '. "$0"; waitfor false 1' "$HERE/lib-wait.sh" 2>&1); RC=$?
check "when the suite names a log, its last lines are printed on expiry" 'printf %s "$OUT" | grep -q "second line" && printf %s "$OUT" | grep -q "the last lines of $SB/daemon"'
OUT=$(PASS=0 FAIL=0 bash -c '. "$0"; waitfor false 1' "$HERE/lib-wait.sh" 2>&1); RC=$?
check "...and none is when it names no log (nearest non-match)" '! printf %s "$OUT" | grep -q "the last lines" && [ $RC = 1 ]'

echo "----"
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
