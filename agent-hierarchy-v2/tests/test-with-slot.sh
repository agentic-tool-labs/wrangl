#!/bin/bash
# agent-hierarchy — the machine-wide test token bucket (bin/ah-test-run, also `ah test run`; tests/with-slot.sh is a shim) and its use by run-all.sh.
#   2 tokens, 4 contenders of 1 token: at most 2 run at once, and all 4 run; the command's exit code is passed through;
#   a stale lease (holder pid gone) is reaped, and a path it did not make is left alone; a lease with a live holder is never
#   taken over (the waiter gives up with exit 75 and does not run); a nested call takes no tokens; AH_TEST_SLOTS=0 is off; a
#   pool directory outside /private/tmp/ahprobe.* is refused; run-all.sh caps -j at the tokens granted.
#   Tokens: a request counts tokens, not runs; a head request that does not fit blocks smaller ones behind it (FIFO); a holder
#   killed with -9 frees its tokens; --tokens is clamped to the capacity, below 1 is refused, and the grant is exported.
#   --eta is optional (default medium) and the run is capped at 3 x the ETA: TERM, KILL 10 s later, one journal line, exit 124.
#   The registry step (a stub ah, a plugin root away from this repo) and the shim are at the end.
# Every run is in a sandbox under /private/tmp/ahprobe.*.
# Usage: bash tests/test-with-slot.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-pid.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:500})"; fi; }
WS="${AH_TEST_WITH_SLOT:-$PLUGIN/bin/ah-test-run}"
unset AH_TEST_SLOT_HELD AH_TEST_SLOT_N AH_TEST_TOKENS AH_TEST_TOKENS_GRANTED AH_TEST_SLOTS
# the registry step is off except where a case turns it on, so that no case depends on an ah binary
export AH_TEST_ADAPT=0
# the lease of a fake holder: <pool dir> <lease name> <pid> <tokens>
mklease() { mkdir -p "$1/held/$2"; printf '{"label": "fake", "pid": %s, "pid_start": "%s", "tokens": %s, "since": 1}\n' "$3" "$(TZ=UTC ps -o lstart= -p "$3" 2>/dev/null | sed 's/^ *//; s/ *$//')" "$4" >"$1/held/$2/holder"; }
export AH_TEST_SLOT_WAIT=60

# ---- 2 slots, 4 contenders
S1="$SB/slots1"; LOG="$SB/conc.log"; : >"$LOG"
for i in 1 2 3 4; do
  AH_SLOTS_DIR="$S1" AH_TEST_TOKENS=2 bash "$WS" --eta 600 sh -c 'echo "+" >>"$1"; sleep 2; echo "-" >>"$1"' _ "$LOG" >/dev/null 2>&1 &
done
wait
MAX=$(awk '{ if ($1 == "+") { c++; if (c > m) m = c } else c-- } END { print m + 0 }' "$LOG")
STARTS=$(grep -c '^+' "$LOG")
OUT="max=$MAX starts=$STARTS"
check "2 slots, 4 contenders: all 4 ran, and at most 2 ran at once" '[ "$STARTS" = 4 ] && [ "$MAX" -le 2 ] && [ "$MAX" -ge 1 ]'
check "...and the 2 slots were really used (at times 2 at once)" '[ "$MAX" = 2 ]'
OUT=$(ls "$S1" | tr '\n' ' ')
check "...and nothing but the capacity record and the empty lease and queue directories is left behind" '[ "$OUT" = "capacity held q " ] && [ -z "$(ls -A "$S1/q")" ] && [ -z "$(ls -A "$S1/held")" ]'

# ---- exit code passthrough
AH_SLOTS_DIR="$SB/slots2" AH_TEST_TOKENS=2 bash "$WS" --eta 600 sh -c 'exit 7' >/dev/null 2>&1; RC=$?
check "the command's exit code is passed through (7)" '[ $RC = 7 ]'
AH_SLOTS_DIR="$SB/slots2" AH_TEST_TOKENS=2 bash "$WS" --eta 600 true >/dev/null 2>&1; RC=$?
check "...and 0 as 0" '[ $RC = 0 ]'

# ---- stale slots are recovered; a path it did not make is left alone
S3="$SB/slots3"; mkdir -p "$S3/held" "$S3/q"
sh -c 'exit 0' & DEAD=$!; wait $DEAD
mklease "$S3" 1-$DEAD "$DEAD" 1; mklease "$S3" 2-$DEAD "$DEAD" 1
echo keep >"$S3/keepme"; mkdir "$S3/foreign"; echo x >"$S3/foreign/f"
OUT=$(AH_SLOTS_DIR="$S3" AH_TEST_TOKENS=2 AH_TEST_SLOT_WAIT=20 bash "$WS" --eta 600 echo ran 2>&1); RC=$?
check "two stale leases (holder pid gone): the command still runs at once" '[ $RC = 0 ] && echo "$OUT" | grep -q "^ran$"'
check "...and a file and a directory in the pool directory that are not leases are untouched" '[ "$(cat "$S3/keepme")" = keep ] && [ -f "$S3/foreign/f" ]'
OUT=$(ls "$S3" | tr '\n' ' ')
check "...and both stale leases are reaped and no tomb is left" '[ -z "$(ls -A "$S3/held")" ] && ! ls -A "$S3" | grep -q stale && [ ! -e "$S3/lock" ]'

# ---- a live owner is never taken
S4="$SB/slots4"; mkdir -p "$S4/held" "$S4/q"
sleep 30 & LIVE1=$!
sleep 30 & LIVE2=$!
mklease "$S4" 1-$LIVE1 "$LIVE1" 1; mklease "$S4" 2-$LIVE2 "$LIVE2" 1
T0=$SECONDS
OUT=$(AH_SLOTS_DIR="$S4" AH_TEST_TOKENS=2 AH_TEST_SLOT_WAIT=3 bash "$WS" --eta 600 echo should-not-run 2>&1); RC=$?
DT=$((SECONDS - T0))
check "both tokens held by live processes: the waiter gives up with exit 75 within the bound and does not run" '[ $RC = 75 ] && ! echo "$OUT" | grep -q "^should-not-run" && [ "$DT" -le 10 ]'
check "...and it printed that it queued" 'echo "$OUT" | grep -q "queued at position 1 of 1"'
check "...and the live holders' leases are intact" '[ -f "$S4/held/1-$LIVE1/holder" ] && [ -f "$S4/held/2-$LIVE2/holder" ]'
ah_signal_child TERM "$LIVE1"; ah_signal_child TERM "$LIVE2"; wait "$LIVE1" "$LIVE2" 2>/dev/null

# ---- nested, off, refused
OUT=$(AH_SLOTS_DIR="$SB/slots5" AH_TEST_TOKENS=1 AH_TEST_SLOT_WAIT=10 bash "$WS" --eta 600 bash "$WS" --eta 600 echo inner 2>&1); RC=$?
check "a nested call inside the only slot takes no second slot (no deadlock)" '[ $RC = 0 ] && echo "$OUT" | grep -q "^inner$"'
OUT=$(AH_SLOTS_DIR="$SB/slots6" AH_TEST_SLOTS=0 bash "$WS" --eta 600 echo off 2>&1); RC=$?
check "AH_TEST_SLOTS=0: the command runs and no slot directory is made" '[ $RC = 0 ] && [ "$OUT" = off ] && [ ! -e "$SB/slots6" ]'
OUT=$(AH_SLOTS_DIR=/tmp/not-ours AH_TEST_TOKENS=2 bash "$WS" --eta 600 echo no 2>&1); RC=$?
check "a slot directory outside /private/tmp/ahprobe.* is refused (exit 2) and nothing is created" '[ $RC = 2 ] && [ ! -e /tmp/not-ours ]'
OUT=$(AH_SLOTS_DIR="$SB/slots7" AH_TEST_SLOTS=x bash "$WS" --eta 600 echo no 2>&1); RC=$?
check "a non-numeric AH_TEST_SLOTS is refused (exit 2)" '[ $RC = 2 ]'

# ---- run-all.sh caps -j
NCPU=$(sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4)
OUT=$(AH_SLOTS_DIR="$SB/slots8" AH_TEST_TOKENS=4 bash "$PLUGIN/tests/run-all.sh" -j 8 --lane fast agent-frontmatter 2>&1)
check "run-all.sh inside a bucket of 4 asks for ceil(3 x 4 / 4) = 3 tokens, caps -j 8 to the 3 granted and still runs" 'echo "$OUT" | grep -q "run-all: -j 8 capped to 3 (the test tokens granted)" && echo "$OUT" | grep -q "1/1 passed"'
OUT=$(AH_TEST_HEAVY_SPLIT=1 AH_SLOTS_DIR="$SB/slots8s" AH_TEST_TOKENS=4 bash "$PLUGIN/tests/run-all.sh" -j 8 --lane fast agent-frontmatter 2>&1)
check "...one meant to overlap another heavy run asks for ceil(4 / 2) = 2" 'echo "$OUT" | grep -q "run-all: -j 8 capped to 2 (the test tokens granted)"'
HV() { env "$@" bash -c '. "$1/tests/lib-tokens.sh"; ah_bucket_heavy' _ "$PLUGIN"; }
check "the heavy grant: 12 of a bucket of 16, capped at 12 however large the bucket" '[ "$(HV AH_TEST_TOKENS=16)" = 12 ] && [ "$(HV AH_TEST_TOKENS=64)" = 12 ]'
check "...two that overlap fit a bucket of 16 side by side: 8 each (nearest non-match)" '[ "$(HV AH_TEST_TOKENS=16 AH_TEST_HEAVY_SPLIT=1)" = 8 ]'
check "...any other value of the split variable is not a split" '[ "$(HV AH_TEST_TOKENS=16 AH_TEST_HEAVY_SPLIT=0)" = 12 ]'
check "...a bucket of 1 still grants 1" '[ "$(HV AH_TEST_TOKENS=1)" = 1 ] && [ "$(HV AH_TEST_TOKENS=1 AH_TEST_HEAVY_SPLIT=1)" = 1 ]'
OUT=$(AH_SLOTS_DIR="$SB/slots8b" AH_TEST_TOKENS=8 bash "$WS" --eta 600 --tokens 3 bash "$PLUGIN/tests/run-all.sh" -j 8 --lane fast agent-frontmatter 2>&1)
check "a run-all.sh started inside a grant of 3 takes no tokens of its own and caps -j 8 to that grant" 'echo "$OUT" | grep -q "run-all: -j 8 capped to 3 (the test tokens granted)" && echo "$OUT" | grep -q "1/1 passed"'
OUT=$(AH_SLOTS_DIR="$SB/slots8c" AH_TEST_TOKENS=8 bash "$WS" --eta 600 --tokens 3 bash "$PLUGIN/tests/run-all.sh" -j 2 --lane fast agent-frontmatter 2>&1)
check "...but a -j below the grant is left alone (nearest non-match)" '! echo "$OUT" | grep -q "capped" && echo "$OUT" | grep -q "1/1 passed"'
OUT=$(AH_SLOTS_DIR="$SB/slots9" AH_TEST_TOKENS=1 bash "$PLUGIN/tests/run-all.sh" -j 1 --lane fast agent-frontmatter 2>&1)
check "run-all.sh takes its slot itself and a nested slot request inside it does not deadlock" 'echo "$OUT" | grep -q "1/1 passed"'
OUT=$(ls "$SB/slots9" 2>/dev/null | tr '\n' ' ')
check "...and it leaves no lease behind" '[ "$OUT" = "capacity held q " ] && [ -z "$(ls -A "$SB/slots9/held")" ]'

# ---- --eta is validated, and the run is capped at 3 x the ETA
CAPS="$SB/capslots"
OUT=$(AH_SLOTS_DIR="$CAPS" AH_TEST_TOKENS=2 bash "$WS" echo yes 2>&1); RC=$?
check "without --eta the command runs (the default is medium)" '[ $RC = 0 ] && [ "$OUT" = yes ]'
OUT=$(AH_SLOTS_DIR="$CAPS" AH_TEST_TOKENS=2 bash "$WS" --eta soon echo no 2>&1); RC=$?
check "an --eta that is neither seconds nor small, medium or large is refused (exit 2)" '[ $RC = 2 ] && ! echo "$OUT" | grep -q "^no$"'
OUT=$(AH_SLOTS_DIR="$CAPS" AH_TEST_TOKENS=2 bash "$WS" --eta 0 echo no 2>&1); RC=$?
check "an --eta of 0 is refused (exit 2)" '[ $RC = 2 ]'
OUT=$(AH_SLOTS_DIR="$CAPS" AH_TEST_TOKENS=2 bash "$WS" --eta small echo ran 2>&1); RC=$?
check "--eta small is accepted and a command that ends before the cap is untouched: its output, exit 0, no journal line" '[ $RC = 0 ] && [ "$OUT" = ran ] && [ ! -e "$CAPS/journal.jsonl" ]'
AH_SLOTS_DIR="$CAPS" AH_TEST_TOKENS=2 bash "$WS" --eta 5 sh -c 'exit 7' >/dev/null 2>&1; RC=$?
check "...and its exit code still passes through (7)" '[ $RC = 7 ]'
# the pids a capped run's journal line names are dead afterwards (judged by pid, never by a machine-wide process-name search)
dead_in_journal() { # <journal line>
  local killed p alive=0
  killed=$(echo "$1" | sed -n 's/.*"killed": \[\([0-9, ]*\)\].*/\1/p' | tr ',' ' ')
  [ -n "$killed" ] || return 1
  for p in $killed; do kill -0 "$p" 2>/dev/null && alive=1; done
  [ $alive = 0 ]
}
UNIQ=$((40000 + RANDOM))
T0=$SECONDS
OUT=$(AH_SLOTS_DIR="$CAPS" AH_TEST_TOKENS=2 bash "$WS" --eta 2 sleep "$UNIQ" 2>&1); RC=$?
DT=$((SECONDS - T0))
check "a sleep under --eta 2 is capped at 6 s: exit 124, the CAPPED line naming the cap and the eta, after at least 6 s" \
  '[ $RC = 124 ] && echo "$OUT" | grep -q "^with-slot: CAPPED after 6s (eta 2s); killed [0-9][0-9]*" && [ "$DT" -ge 6 ]'
OUT=$(tail -1 "$CAPS/journal.jsonl" 2>/dev/null)
check "...and one journal line records the slot, the holder, the eta, the cap and the pids killed" \
  'echo "$OUT" | grep -q "\"kind\": \"capped\"" && echo "$OUT" | grep -q "\"eta\": 2, \"cap\": 6, \"killed\": \[[0-9][0-9]*" && echo "$OUT" | grep -q "\"slot\": \"[0-9]*-[0-9]*\"" && [ "$(wc -l <"$CAPS/journal.jsonl" | tr -d " ")" = 1 ]'
check "...and every pid the journal names is gone" 'dead_in_journal "$OUT"'
OUT=$(ls "$CAPS" | tr '\n' ' ')
check "...and the capped run's lease is released" '[ "$OUT" = "capacity held journal.jsonl q " ] && [ -z "$(ls -A "$CAPS/held")" ]'
UNIQ=$((40000 + RANDOM))
T0=$SECONDS
OUT=$(AH_SLOTS_DIR="$CAPS" AH_TEST_TOKENS=2 bash "$WS" --eta 1 sh -c 'trap "" TERM; sleep "$1" & wait' _ "$UNIQ" 2>&1); RC=$?
DT=$((SECONDS - T0))
check "a command that ignores TERM is killed 10 s after it: exit 124 after at least 13 s" '[ $RC = 124 ] && [ "$DT" -ge 13 ]'
OUT=$(tail -1 "$CAPS/journal.jsonl" 2>/dev/null)
check "...and every pid the journal names for it is gone" 'dead_in_journal "$OUT"'

# ---- a named pool: its own slot, holder and waiter records, FIFO, the waiting line, stale holders
PS="$SB/pools"; PE="$PS/pool-e4"
AH_SLOTS_DIR="$PS" AH_SLOT_POOL=e4 AH_SLOT_LABEL="ui-e4 small wt1" bash "$WS" --eta 600 sleep 12 & H1=$!
sleep 2
OUT=$(cat "$PE"/held/*/holder 2>/dev/null)
check "pool e4: the holder record names the label, the with-slot pid, a start time, the tokens and the time taken" \
  'echo "$OUT" | grep -q "\"label\": \"ui-e4 small wt1\"" && echo "$OUT" | grep -q "\"pid\": $H1," && echo "$OUT" | grep -q "\"pid_start\": \"[A-Z][a-z][a-z] " && echo "$OUT" | grep -q "\"tokens\": 1, \"waited\": [0-9], \(\"pgid\": [0-9][0-9]*, \)\{0,1\}\"since\": [0-9][0-9]*}"'
OUT=$(AH_SLOTS_DIR="$PS" AH_TEST_TOKENS=1 AH_TEST_SLOT_WAIT=5 bash "$WS" --eta 600 echo default-pool 2>&1)
check "...and the default pool is a separate pool: it is not held up by the e4 holder" '[ "$OUT" = default-pool ]'
ORDER="$SB/order.txt"; : >"$ORDER"
for w in w1 w2 w3; do
  AH_SLOTS_DIR="$PS" AH_SLOT_POOL=e4 AH_SLOT_LABEL="$w" AH_TEST_SLOT_WAIT=60 bash "$WS" --eta 600 sh -c 'echo "$1" >>"$2"' _ "$w" "$ORDER" >/dev/null 2>"$SB/$w.err" &
  sleep 0.4
done
sleep 1
OUT=$(cat "$PE"/q/*/waiter 2>/dev/null | grep -o '"label": "w[0-9]"' | sort | tr '\n' ' ')
check "three waiters each have a waiter record in the pool queue" '[ "$OUT" = "\"label\": \"w1\" \"label\": \"w2\" \"label\": \"w3\" " ]'
OUT=$(grep -c '^waiting for e4 slot held by ui-e4 small wt1 (pid '"$H1"', [0-9][0-9]*s)$' "$SB/w1.err")
check "a waiter prints 'waiting for <pool> slot held by <label> (pid <pid>, <age>)' once" '[ "$OUT" = 1 ]'
wait
OUT=$(tr '\n' ' ' <"$ORDER")
check "FIFO: the three waiters ran in arrival order" '[ "$OUT" = "w1 w2 w3 " ]'
OUT=$(ls -A "$PE" "$PE/q" 2>&1 | tr '\n' ' ')
check "...and the holder and waiter records are gone afterwards" '[ "$OUT" = "$PE: capacity held q  $PE/q: " ] && [ -z "$(ls -A "$PE/held")" ]'

AH_SLOTS_DIR="$PS" AH_SLOT_POOL=e4 AH_SLOT_LABEL="doomed" bash "$WS" --eta 600 sleep 60 >/dev/null 2>&1 & D1=$!
sleep 1
CP=$(pgrep -P "$D1" -x sleep); CPSTAMP=$(ah_pid_stamp "$CP")
ah_signal_child KILL "$D1"; wait "$D1" 2>/dev/null; ah_signal_stamped TERM "$CP" "$CPSTAMP"
OUT=$(AH_SLOTS_DIR="$PS" AH_SLOT_POOL=e4 AH_TEST_SLOT_WAIT=10 bash "$WS" --eta 600 echo took-over 2>&1)
check "a holder killed with -9 is reclaimed as stale, with a line, and the next waiter proceeds" \
  'echo "$OUT" | grep -q "^with-slot: reclaimed the e4 slot of a holder that is gone: doomed (pid $D1)$" && echo "$OUT" | grep -q "^took-over$"'

sleep 30 & LIVE=$!
mkdir -p "$PE/held/1-reused"
printf '{"label": "reused", "pid": %s, "pid_start": "Mon Jan  1 00:00:00 2001", "tokens": 1, "since": 1}\n' "$LIVE" >"$PE/held/1-reused/holder"
OUT=$(AH_SLOTS_DIR="$PS" AH_SLOT_POOL=e4 AH_TEST_SLOT_WAIT=10 bash "$WS" --eta 600 echo reclaimed-reuse 2>&1)
check "a holder whose pid is alive but whose recorded start time is not that process's is stale" 'echo "$OUT" | grep -q "^reclaimed-reuse$"'
mklease "$PE" 2-genuine "$LIVE" 1; sed -i.bak 's/"fake"/"genuine"/' "$PE/held/2-genuine/holder"; rm -f "$PE/held/2-genuine/holder.bak"
OUT=$(AH_SLOTS_DIR="$PS" AH_SLOT_POOL=e4 AH_TEST_SLOT_WAIT=3 bash "$WS" --eta 600 echo must-not-run 2>&1); RC=$?
check "a live holder whose start time matches is not reclaimed: the waiter gives up with exit 75 and names the holder" \
  '[ $RC = 75 ] && ! echo "$OUT" | grep -q "^must-not-run$" && echo "$OUT" | grep -q "^waiting for e4 slot held by genuine (pid $LIVE, "'
ah_signal_child TERM "$LIVE"; wait "$LIVE" 2>/dev/null
ah_signal_child TERM "$H1"; wait "$H1" 2>/dev/null

OUT=$(AH_SLOTS_DIR="$SB/pools2" AH_SLOT_POOL=e4 bash "$WS" --eta 600 bash "$WS" --eta 600 echo inner 2>&1)
check "a nested call inside the e4 slot takes no second slot (no deadlock on a pool of one)" '[ "$OUT" = inner ]'
OUT=$(AH_SLOTS_DIR="$SB/pools3" AH_SLOT_POOL="E4/x" bash "$WS" --eta 600 echo no 2>&1); RC=$?
check "a pool name outside lower-case letters, digits and '-' is refused" '[ $RC = 2 ] && ! echo "$OUT" | grep -q "^no$"'

# ---- ui-e4 takes the e4 slot
if command -v node >/dev/null 2>&1; then
  E4="$PLUGIN/tests/perf/ui-e4.mjs"; PU="$SB/e4pool"
  OUT=$(AH_SLOTS_DIR="$PU" node "$E4" --check-slot 2>&1)
  check "ui-e4 acquires the slot once (the re-run does not take a second) and the run sees it held" 'echo "$OUT" | grep -q "^slot ok: held [0-9][0-9]*-[0-9][0-9]*$"'
  check "...and releases it when the run ends" '[ -z "$(ls -A "$PU/pool-e4/held")" ]'
  AH_SLOTS_DIR="$PU" node "$E4" --fixture small --check-slot 8 >/dev/null 2>&1 & U1=$!
  # wait for the labelled lease, as long as the holder lives
  while kill -0 "$U1" 2>/dev/null && ! grep -q '"label": "ui-e4 small ' "$PU"/pool-e4/held/*/holder 2>/dev/null; do sleep 0.2; done
  OUT=$(cat "$PU"/pool-e4/held/*/holder 2>/dev/null)
  check "...labelled with the fixture and the working directory" 'echo "$OUT" | grep -q "\"label\": \"ui-e4 small '"$(basename "$PWD")"'\""'
  OUT=$(AH_SLOTS_DIR="$PU" AH_TEST_SLOT_WAIT=2 node "$E4" --check-slot 2>&1); RC=$?
  check "a second run at once waits for the holder, names it, and gives up with exit 75 when its wait ends" \
    '[ $RC = 75 ] && echo "$OUT" | grep -q "^waiting for e4 slot held by ui-e4 small " && ! echo "$OUT" | grep -q "slot ok"'
  OUT=$(AH_SLOTS_DIR="$PU" AH_TEST_SLOT_WAIT=20 node "$E4" --fixture=huge 2>&1); RC=$?
  check "an unknown preset is refused with exit 2 at once, even while another run holds the slot (no wait)" '[ $RC = 2 ] && echo "$OUT" | grep -q "unknown fixture \"huge\"" && ! echo "$OUT" | grep -q "waiting for e4 slot"'
  ah_signal_child INT "$U1"; wait "$U1" 2>/dev/null
  sleep 1
  check "SIGINT ends the run, releases the slot and leaves no child behind" '[ -z "$(ls -A "$PU/pool-e4/held")" ] && ! pgrep -f "ui-e4.mjs --fixture small --check-slot 8" >/dev/null'
  OUT=$(AH_SLOTS_DIR="$PU" node "$E4" --no-slot --check-slot 2>&1)
  check "--no-slot runs at once, takes no slot and says the numbers are not gate-grade" 'echo "$OUT" | grep -q "slot ok: none" && echo "$OUT" | grep -q "NOT gate-grade" && [ -z "$(ls -A "$PU/pool-e4/held" 2>/dev/null)" ]'
  AH_SLOTS_DIR="$PU" node "$E4" --check-slot 6 >/dev/null 2>&1 & U2=$!
  sleep 3
  OUT=$(AH_SLOTS_DIR="$PU" AH_SLOT_HELD_E4= node "$E4" --child --check-slot 2>&1); RC=$?
  check "a --child run never acquires a slot, even while another run holds it" '[ $RC = 0 ] && [ "$OUT" = "slot ok: child" ]'
  ah_signal_child INT "$U2"; wait "$U2" 2>/dev/null
else
  echo "SKIP: ui-e4 slot cases: no node on PATH"
fi

# ---- the token bucket: tokens, not runs; strict FIFO; a killed holder frees its tokens; the grant
TB="$SB/bucket"
AH_SLOTS_DIR="$TB" AH_TEST_TOKENS=4 bash "$WS" --eta 600 --tokens 2 sleep 8 >/dev/null 2>&1 & HA=$!
AH_SLOTS_DIR="$TB" AH_TEST_TOKENS=4 bash "$WS" --eta 600 --tokens 2 sleep 3 >/dev/null 2>&1 & HB=$!
# wait for both leases, as long as the 3 s holder lives; then for W1's place in the queue, as long as W1 lives
until [ "$(ls "$TB/held" 2>/dev/null | wc -l | tr -d " ")" = 2 ] || ! kill -0 "$HB" 2>/dev/null; do sleep 0.1; done
RUNLOG="$SB/b1.log"; : >"$RUNLOG"
AH_SLOTS_DIR="$TB" AH_TEST_TOKENS=4 AH_TEST_SLOT_WAIT=30 bash "$WS" --eta 600 --tokens 1 sh -c 'echo ran >>"$1"' _ "$RUNLOG" >/dev/null 2>&1 & W1=$!
until ls "$TB"/q/*/waiter >/dev/null 2>&1 || ! kill -0 "$W1" 2>/dev/null; do sleep 0.1; done
check "B1: B = 4 held as 2 + 2 by two runs: a request of 1 queues (it has not run)" '[ ! -s "$RUNLOG" ] && [ "$(ls "$TB/held" | wc -l | tr -d " ")" = 2 ]'
wait "$W1"
# the state, not the elapsed time: once W1 has run, the 3 s holder has exited and the 8 s holder is still alive
check "...and runs once one holder exits (the 3 s one), while the 8 s holder still holds" '[ "$(cat "$RUNLOG")" = ran ] && ! kill -0 "$HB" 2>/dev/null && kill -0 "$HA" 2>/dev/null'
ah_signal_child TERM "$HA"; wait "$HA" "$HB" 2>/dev/null

# the head asks for all 4 while 2 are free: a later request of 1 (which would fit) waits behind it
TF="$SB/fifo"; ORDER3="$SB/fifo.txt"; : >"$ORDER3"
# each run starts only once the one before it holds its lease or has its ticket (a bounded poll: under load a fixed sleep let
# the small run take its ticket before the big one had, so it was served first)
queued() { cat "$TF"/q/*/waiter 2>/dev/null | grep -q "\"label\": \"$1\""; }
AH_SLOTS_DIR="$TF" AH_TEST_TOKENS=4 bash "$WS" --eta 600 --tokens 2 sleep 4 >/dev/null 2>&1 & HC=$!
for _ in $(seq 1 100); do [ "$(ls "$TF/held" 2>/dev/null | grep -c .)" = 1 ] && break; sleep 0.1; done
AH_SLOTS_DIR="$TF" AH_TEST_TOKENS=4 AH_TEST_SLOT_WAIT=30 AH_SLOT_LABEL=big bash "$WS" --eta 600 --tokens 4 sh -c 'echo big >>"$1"; sleep 1' _ "$ORDER3" >/dev/null 2>&1 & HD=$!
for _ in $(seq 1 100); do queued big && break; sleep 0.1; done
AH_SLOTS_DIR="$TF" AH_TEST_TOKENS=4 AH_TEST_SLOT_WAIT=30 AH_SLOT_LABEL=small bash "$WS" --eta 600 --tokens 1 sh -c 'echo small >>"$1"' _ "$ORDER3" >/dev/null 2>&1 & HE=$!
for _ in $(seq 1 100); do queued small && break; sleep 0.1; done
# time for the small run to be served if the head did not block it
sleep 1
OUT=$(cat "$ORDER3"; ls "$TF/held" "$TF/q" 2>&1 | tr '\n' ' ')
check "B3: a head request of 4 with 2 free blocks a later request of 1 (it has not run)" '[ ! -s "$ORDER3" ]'
wait "$HC" "$HD" "$HE"
OUT=$(tr "\n" " " <"$ORDER3")
check "...and when the tokens come free they are served in arrival order: big, then small" '[ "$OUT" = "big small " ]'

# a holder killed with -9 frees its tokens at the next acquire
TK="$SB/killed"
AH_SLOTS_DIR="$TK" AH_TEST_TOKENS=2 bash "$WS" --eta 600 --tokens 2 sleep 60 >/dev/null 2>&1 & K1=$!
KCHILD=""; for _ in $(seq 1 100); do KCHILD=$(pgrep -P "$K1" -x sleep) && break; sleep 0.1; done; KSTAMP=$(ah_pid_stamp "$KCHILD")
OUT=$(AH_SLOTS_DIR="$TK" AH_TEST_TOKENS=2 AH_TEST_SLOT_WAIT=3 bash "$WS" --eta 600 --tokens 1 echo no 2>&1); RC=$?
check "B2 (nearest non-match): a live holder of all 2 tokens keeps a request of 1 waiting: exit 75" '[ $RC = 75 ] && ! echo "$OUT" | grep -q "^no$"'
ah_signal_child KILL "$K1"; wait "$K1" 2>/dev/null; ah_signal_stamped TERM "$KCHILD" "$KSTAMP"
OUT=$(AH_SLOTS_DIR="$TK" AH_TEST_TOKENS=2 AH_TEST_SLOT_WAIT=10 bash "$WS" --eta 600 --tokens 1 echo took-over 2>&1)
check "B2: the same holder killed with -9: its lease is reaped (with a line) and the waiter runs" 'echo "$OUT" | grep -q "^with-slot: reclaimed the test slot of a holder that is gone: " && echo "$OUT" | grep -q "^took-over$"'

# the request and the grant
TG="$SB/grant"
OUT=$(AH_SLOTS_DIR="$TG" AH_TEST_TOKENS=4 bash "$WS" --eta 600 --tokens 3 sh -c 'echo "$AH_TEST_TOKENS_GRANTED $AH_TEST_SLOT_HELD"' 2>&1)
check "B4: the command gets the grant as AH_TEST_TOKENS_GRANTED, and the nesting marker" '[ "$OUT" = "3 1" ]'
OUT=$(AH_SLOTS_DIR="$TG" AH_TEST_TOKENS=4 bash "$WS" --eta 600 sh -c 'echo "$AH_TEST_TOKENS_GRANTED"' 2>&1)
check "...no --tokens asks for ceil(B / 2) = 2" '[ "$OUT" = 2 ]'
OUT=$(AH_SLOTS_DIR="$TG" AH_TEST_TOKENS=5 bash "$WS" --eta 600 sh -c 'echo "$AH_TEST_TOKENS_GRANTED"' 2>&1)
check "...and ceil rounds up: B = 5 asks for 3" '[ "$OUT" = 3 ]'
OUT=$(AH_SLOTS_DIR="$TG" AH_TEST_TOKENS=4 bash "$WS" --eta 600 --tokens 20 sh -c 'echo "$AH_TEST_TOKENS_GRANTED"' 2>&1)
check "a request above the capacity is clamped to it (20 with B = 4 runs alone as 4)" '[ "$OUT" = 4 ]'
OUT=$(AH_SLOTS_DIR="$TG" AH_TEST_TOKENS=4 bash "$WS" --eta 600 --tokens 4 sh -c 'echo "$AH_TEST_TOKENS_GRANTED"' 2>&1)
check "...and a request equal to the capacity is not changed (nearest non-match)" '[ "$OUT" = 4 ]'
OUT=$(AH_SLOTS_DIR="$TG" AH_TEST_TOKENS=4 bash "$WS" --eta 600 --tokens 0 echo no 2>&1); RC=$?
check "--tokens 0 is refused (exit 2) and the command does not run" '[ $RC = 2 ] && ! echo "$OUT" | grep -q "^no$"'
OUT=$(AH_SLOTS_DIR="$TG" AH_TEST_TOKENS=4 bash "$WS" --eta 600 --tokens 1 echo yes 2>&1); RC=$?
check "...and --tokens 1 is accepted (nearest non-match)" '[ $RC = 0 ] && [ "$OUT" = yes ]'
OUT=$(AH_SLOTS_DIR="$TG" AH_TEST_TOKENS=4 bash "$WS" --eta 600 --tokens x echo no 2>&1); RC=$?
check "a non-numeric --tokens is refused (exit 2)" '[ $RC = 2 ]'
OUT=$(AH_SLOTS_DIR="$TG" AH_TEST_TOKENS=4 bash "$WS" --eta 600 --tokens 3 bash "$WS" --eta 600 --tokens 4 sh -c 'echo "$AH_TEST_TOKENS_GRANTED"' 2>&1)
check "a nested call inside a grant of 3 takes no tokens and sees the parent's grant, not its own request" '[ "$OUT" = 3 ]'
OUT=$(AH_SLOTS_DIR="$TG" AH_TEST_TOKENS=4 AH_TEST_SLOTS=9 bash "$WS" --eta 600 sh -c 'echo "$AH_TEST_TOKENS_GRANTED"' 2>&1)
check "AH_TEST_TOKENS wins over the legacy AH_TEST_SLOTS, and says nothing about it" '[ "$OUT" = 2 ]'
OUT=$(AH_SLOTS_DIR="$TG" AH_TEST_SLOTS=4 bash "$WS" --eta 600 sh -c 'echo "$AH_TEST_TOKENS_GRANTED"' 2>&1)
check "AH_TEST_SLOTS=4 alone is read as a capacity of 4 (a grant of 2), with one line saying so" 'echo "$OUT" | grep -q "^with-slot: AH_TEST_SLOTS=4 is read as AH_TEST_TOKENS=4" && [ "$(echo "$OUT" | tail -1)" = 2 ] && [ "$(echo "$OUT" | grep -c legacy)" = 1 ]'
OUT=$(AH_SLOTS_DIR="$TG" bash "$WS" --eta 600 sh -c 'echo "$AH_TEST_TOKENS_GRANTED"' 2>&1)
NC=$(sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4); BCAP=$((NC - 2)); [ "$BCAP" -lt 2 ] && BCAP=2
check "with no variable set the capacity is max(2, ncpu - 2) = $BCAP, written to the pool's capacity record" '[ "$OUT" = "$(( (BCAP + 1) / 2 ))" ] && [ "$(cat "$TG/capacity")" = "$BCAP" ]'

# ---- review fixes: liveness without ps, the pool's own capacity, a killed with-slot whose command still runs, the dotnet-test arguments
STUB="$SB/psstub"; mkdir -p "$STUB"; printf '#!/bin/sh\nexit 1\n' >"$STUB/ps"; chmod +x "$STUB/ps"
TP="$SB/psfail"
AH_SLOTS_DIR="$TP" AH_TEST_TOKENS=1 bash "$WS" --eta 600 --tokens 1 sleep 30 >/dev/null 2>&1 & PH=$!
sleep 1.5
OUT=$(PATH="$STUB:$PATH" AH_SLOTS_DIR="$TP" AH_TEST_TOKENS=1 AH_TEST_SLOT_WAIT=4 bash "$WS" --eta 600 --tokens 1 echo stolen 2>&1); RC=$?
check "C-1: with every ps failing (an overloaded machine) a live holder is still live: the waiter does not run, reaps nothing and exits 75" \
  '[ $RC = 75 ] && ! echo "$OUT" | grep -q "^stolen$" && ! echo "$OUT" | grep -q reclaimed && [ "$(ls "$TP/held" | wc -l | tr -d " ")" = 1 ]'
ah_signal_child TERM "$PH"; wait "$PH" 2>/dev/null
OUT=$(PATH="$STUB:$PATH" AH_SLOTS_DIR="$TP" AH_TEST_TOKENS=1 AH_TEST_SLOT_WAIT=5 bash "$WS" --eta 600 --tokens 1 echo free-now 2>&1)
check "...and once the holder is really gone, the same failing ps does not stop the next run (nearest non-match)" 'echo "$OUT" | grep -q "^free-now$"'

TC="$SB/cap"
AH_SLOTS_DIR="$TC" AH_TEST_TOKENS=4 bash "$WS" --eta 600 --tokens 1 sleep 8 >/dev/null 2>&1 & CH=$!
sleep 1.5
OUT=$(AH_SLOTS_DIR="$TC" AH_TEST_TOKENS=16 AH_TEST_SLOT_WAIT=3 bash "$WS" --eta 600 --tokens 15 echo big 2>&1); RC=$?
check "S-2: a caller whose capacity disagrees with the held pool's recorded 4 follows the pool (15 is clamped to 4, which does not fit beside the holder), and says so" \
  '[ $RC = 75 ] && ! echo "$OUT" | grep -q "^big$" && echo "$OUT" | grep -q "recorded capacity is 4 tokens" && [ "$(cat "$TC/capacity")" = 4 ]'
ah_signal_child TERM "$CH"; wait "$CH" 2>/dev/null
OUT=$(AH_SLOTS_DIR="$TC" AH_TEST_TOKENS=16 bash "$WS" --eta 600 --tokens 15 echo big 2>&1)
check "...and a pool with no holder takes the capacity of the caller that finds it empty" 'echo "$OUT" | grep -q "^big$" && [ "$(cat "$TC/capacity")" = 16 ]'

TS="$SB/pg"
AH_SLOTS_DIR="$TS" AH_TEST_TOKENS=1 bash "$WS" --eta 600 --tokens 1 sleep 60 >/dev/null 2>&1 & Q1=$!
sleep 1.5
QCHILD=$(pgrep -P "$Q1" -x sleep); QSTAMP=$(ah_pid_stamp "$QCHILD")
ah_signal_child KILL "$Q1"; wait "$Q1" 2>/dev/null
OUT=$(AH_SLOTS_DIR="$TS" AH_TEST_TOKENS=1 AH_TEST_SLOT_WAIT=3 bash "$WS" --eta 600 --tokens 1 echo early 2>&1); RC=$?
check "S-4: a with-slot killed with -9 whose command still runs keeps its tokens: the waiter does not run (exit 75) and the lease records the process group" \
  '[ $RC = 75 ] && ! echo "$OUT" | grep -q "^early$" && grep -q "\"pgid\": " "$TS"/held/*/holder'
ah_signal_stamped TERM "$QCHILD" "$QSTAMP"; sleep 1
OUT=$(AH_SLOTS_DIR="$TS" AH_TEST_TOKENS=1 AH_TEST_SLOT_WAIT=10 bash "$WS" --eta 600 --tokens 1 echo late 2>&1)
check "...and once that command is gone the tokens are reclaimed and the next run goes" 'echo "$OUT" | grep -q "^late$" && echo "$OUT" | grep -q reclaimed'

# a holder whose pid now belongs to another process keeps its tokens while the process group it recorded is alive
RP="$SB/reuse"; sleep 30 & RLIVE=$!
mkdir -p "$RP/held/1-reuse"; echo 2 >"$RP/capacity"
printf '{"label": "reuse-grp", "pid": %s, "pid_start": "Mon Jan  1 00:00:00 2001", "tokens": 2, "pgid": %s, "since": 1}\n' "$RLIVE" "$(ps -o pgid= -p $$ | tr -d ' ')" >"$RP/held/1-reuse/holder"
OUT=$(AH_SLOTS_DIR="$RP" AH_TEST_TOKENS=2 AH_TEST_SLOT_WAIT=3 bash "$WS" --eta 600 echo must-wait 2>&1); RC=$?
check "N-2: a recycled pid with its recorded process group still alive keeps the tokens (the waiter gives up, 75)" '[ $RC = 75 ] && ! echo "$OUT" | grep -q "^must-wait$" && [ -d "$RP/held/1-reuse" ]'
printf '{"label": "reuse-grp", "pid": %s, "pid_start": "Mon Jan  1 00:00:00 2001", "tokens": 2, "pgid": 999999, "since": 1}\n' "$RLIVE" >"$RP/held/1-reuse/holder"
OUT=$(AH_SLOTS_DIR="$RP" AH_TEST_TOKENS=2 AH_TEST_SLOT_WAIT=10 bash "$WS" --eta 600 echo went 2>&1)
check "...and with that process group gone the lease is stale and the next run goes (nearest non-match)" 'echo "$OUT" | grep -q "^went$"'
ah_signal_child TERM "$RLIVE"; wait "$RLIVE" 2>/dev/null

# dotnet-test.sh sizes -m and the xUnit cap to the grant; with the limiter off it adds neither; the cap follows the caller's own --
DS="$SB/dstub"; mkdir -p "$DS"; DLOG="$SB/dotnet.args"
printf '#!/bin/sh\nprintf "%%s\\n" "$*" >>"%s"\nexit 0\n' "$DLOG" >"$DS/dotnet"; chmod +x "$DS/dotnet"
DT="$PLUGIN/scripts/dotnet-test.sh"
: >"$DLOG"; PATH="$DS:$PATH" AH_SLOTS_DIR="$SB/dt1" AH_TEST_TOKENS=8 bash "$DT" --filter X >/dev/null 2>&1
check "dotnet-test.sh, targeted: -m:2 and the xUnit cap 2 (a targeted run asks for 2 tokens)" 'grep -q -- "-m:2 .*--filter X -- xUnit.MaxParallelThreads=2\$" "$DLOG"'
: >"$DLOG"; PATH="$DS:$PATH" AH_SLOTS_DIR="$SB/dt2" AH_TEST_SLOTS=0 bash "$DT" --filter X >/dev/null 2>&1
check "C-2: AH_TEST_SLOTS=0 is unbounded: no -m and no xUnit cap" '[ -s "$DLOG" ] && ! grep -q -- "-m:" "$DLOG" && ! grep -q xUnit "$DLOG"'
: >"$DLOG"; PATH="$DS:$PATH" AH_SLOTS_DIR="$SB/dt3" AH_TEST_TOKENS=8 bash "$DT" --filter X -- RunConfiguration.Foo=1 >/dev/null 2>&1
check "S-5: a caller's own -- arguments come first and the xUnit cap follows them, with no second --" \
  'grep -q -- "-- RunConfiguration.Foo=1 xUnit.MaxParallelThreads=2\$" "$DLOG" && [ "$(grep -o -- " -- " "$DLOG" | wc -l | tr -d " ")" = 1 ]'
: >"$DLOG"; PATH="$DS:$PATH" AH_SLOTS_DIR="$SB/dt4" AH_TEST_TOKENS=16 bash "$DT" >/dev/null 2>&1
check "dotnet-test.sh, whole solution with a grant of 12: the build uses -m:12, the test run -m:3 and 4 xUnit threads, so the two multiply to the grant" \
  'grep -q "^build .*-m:12" "$DLOG" && grep -q -- "^test .*-m:3 .*-- xUnit.MaxParallelThreads=4\$" "$DLOG"'
: >"$DLOG"; PATH="$DS:$PATH" AH_SLOTS_DIR="$SB/dt4" AH_TEST_TOKENS=16 AH_DOTNET_TEST_PROJECTS="a.csproj b.csproj" bash "$DT" >/dev/null 2>&1
check "dotnet-test.sh with AH_DOTNET_TEST_PROJECTS: builds the solution once, then tests each named project and not the solution" \
  '[ "$(grep -c "^build .*Ah.sln" "$DLOG")" = 1 ] && grep -q "^test a.csproj --no-build .*xUnit.MaxParallelThreads=4\$" "$DLOG" && grep -q "^test b.csproj --no-build " "$DLOG" && [ "$(grep -c "^test " "$DLOG")" = 2 ]'
: >"$DLOG"; PATH="$DS:$PATH" AH_SLOTS_DIR="$SB/dt4" AH_TEST_TOKENS=16 AH_DOTNET_TEST_PROJECTS="a.csproj" bash "$DT" --filter X >/dev/null 2>&1
check "...a targeted run does not take it (nearest non-match)" '! grep -q "a.csproj" "$DLOG"'
DF="$SB/dstub-fail"; mkdir -p "$DF"
printf '#!/bin/sh\ncase "$*" in *b.csproj*) exit 1;; esac\nexit 0\n' >"$DF/dotnet"; chmod +x "$DF/dotnet"
PATH="$DF:$PATH" AH_SLOTS_DIR="$SB/dt4" AH_TEST_TOKENS=16 AH_DOTNET_TEST_PROJECTS="a.csproj b.csproj" bash "$DT" >/dev/null 2>&1; DRC=$?
check "...one project failing fails the run" '[ "$DRC" != 0 ]'
: >"$DLOG"; PATH="$DS:$PATH" AH_SLOTS_DIR="$SB/dt4" AH_TEST_TOKENS=16 AH_TEST_HEAVY_SPLIT=1 bash "$DT" >/dev/null 2>&1
check "...overlapping another heavy run, a grant of 8: the build uses -m:8, the test run -m:2 and 4 xUnit threads" \
  'grep -q "^build .*-m:8" "$DLOG" && grep -q -- "^test .*-m:2 .*-- xUnit.MaxParallelThreads=4\$" "$DLOG"'
: >"$DLOG"; PATH="$DS:$PATH" AH_SLOTS_DIR="$SB/dt5" AH_TEST_TOKENS=1 bash "$DT" >/dev/null 2>&1
check "...a grant of 1 stays -m:1 and 1 thread (nearest non-match)" 'grep -q -- "^test .*-m:1 .*-- xUnit.MaxParallelThreads=1\$" "$DLOG"'
: >"$DLOG"; PATH="$DS:$PATH" AH_SLOTS_DIR="$SB/dt6" AH_TEST_SLOTS=0 bash "$DT" >/dev/null 2>&1
check "...the limiter off adds neither to a whole-solution run" '[ -s "$DLOG" ] && ! grep -q -- "-m:" "$DLOG" && ! grep -q xUnit "$DLOG"'

# ---- the registry step, away from this repo: a plugin root of its own with a stub `ah`, and a command that is not from this repo
RT="$SB/plugin"; mkdir -p "$RT/bin" "$RT/tests"
cp "$PLUGIN/bin/ah-test-run" "$RT/bin/"; cp "$PLUGIN/tests/lib-pid.sh" "$PLUGIN/tests/lib-tokens.sh" "$RT/tests/"
PLANLOG="$SB/plan.log"; : >"$PLANLOG"
cat >"$RT/bin/ah" <<'EOF'
#!/bin/sh
echo "$*" >>"$PLANLOG"
size=2; while [ "$1" != -- ]; do [ "$1" = --tokens ] && size=$2; shift; done; shift
if [ "$1" = fakecheck ]; then
  printf 'row\tfake\ntokens\t3\nenv\tFAKE_WORKERS\t3\narg\tfakecheck\narg\t-j3\n'
  shift; for a in "$@"; do printf 'arg\t%s\n' "$a"; done
elif [ "$1" = fakedotnet ]; then
  # a light run of a `half` row: like the dotnet adapter, it adds -m only where there is none
  printf 'row\tfakedotnet\ntokens\t2\nelastic\t1\n'
  case "$*" in *-m:*) ;; *) printf 'arg\t%s\n' "$1" "-m:$size"; shift; for a in "$@"; do printf 'arg\t%s\n' "$a"; done ;; esac
elif [ "$1" = fakemake ]; then
  # a `half` row whose adapter appends to the variable as it finds it, like make-check's env_append
  printf 'row\tfakemake\ntokens\t2\nelastic\t1\nenv\tMAKEFLAGS\t%s\n' "${MAKEFLAGS:+$MAKEFLAGS }-j$size"
else
  printf 'row\t-\ntokens\t0\n'
fi
EOF
cat >"$RT/fakecheck" <<'EOF'
#!/bin/sh
echo "workers=${FAKE_WORKERS:-none} grant=$AH_TEST_TOKENS_GRANTED args=$*"
EOF
printf '#!/bin/sh\necho "grant=$AH_TEST_TOKENS_GRANTED args=$*"\n' >"$RT/fakedotnet"
printf '#!/bin/sh\necho "grant=$AH_TEST_TOKENS_GRANTED MAKEFLAGS=$MAKEFLAGS"\n' >"$RT/fakemake"
chmod +x "$RT/bin/ah" "$RT/fakecheck" "$RT/fakedotnet" "$RT/fakemake" "$RT/bin/ah-test-run"
RUN="$RT/bin/ah-test-run"
export PLANLOG
OUT=$(AH_TEST_ADAPT=1 PATH="$RT:$PATH" AH_SLOTS_DIR="$SB/rt1" AH_TEST_TOKENS=8 bash "$RUN" fakecheck x 2>&1); RC=$?
check "registry: a command the plan names gets its row's tokens as the grant, the row's environment and the plan's arguments (run from a plugin root that is not this repo)" \
  '[ $RC = 0 ] && [ "$OUT" = "workers=3 grant=3 args=-j3 x" ]'
OUT=$(AH_TEST_ADAPT=1 PATH="$RT:$PATH" AH_SLOTS_DIR="$SB/rt2" AH_TEST_TOKENS=8 bash "$RUN" sh -c 'echo "grant=$AH_TEST_TOKENS_GRANTED workers=${FAKE_WORKERS:-none}"' 2>&1)
check "...a command no row names gets half the bucket, no environment and no changed arguments (nearest non-match)" '[ "$OUT" = "grant=4 workers=none" ]'
: >"$PLANLOG"
OUT=$(AH_TEST_ADAPT=1 PATH="$RT:$PATH" AH_SLOTS_DIR="$SB/rt3" AH_TEST_TOKENS=8 bash "$RUN" --tokens 2 -- fakecheck y 2>&1)
check "...an explicit --tokens wins over the row and is what the plan is asked to size for; -- is accepted" \
  '[ "$OUT" = "workers=3 grant=2 args=-j3 y" ] && grep -q -- "--capacity 8 --tokens 2 " "$PLANLOG"'
: >"$PLANLOG"
OUT=$(AH_TEST_ADAPT=1 PATH="$RT:$PATH" AH_SLOTS_DIR="$SB/rt4" AH_TEST_TOKENS=8 bash "$RUN" --tokens 3 sh -c '"$0" fakecheck z' "$RUN" 2>&1)
check "...a nested call takes no tokens, is planned for the grant it inherited, and is still adapted" \
  '[ "$OUT" = "workers=3 grant=3 args=-j3 z" ] && [ "$(grep -c -- "--tokens 3 " "$PLANLOG")" = 2 ]'
OUT=$(AH_TEST_ADAPT=1 PATH="$RT:$PATH" AH_SLOTS_DIR="$SB/rt5" AH_TEST_TOKENS=8 bash "$RUN" FOO=bar sh -c 'echo "foo=$FOO"' 2>&1)
check "...a command that opens with VAR=value runs with it set" '[ "$OUT" = "foo=bar" ]'
OUT=$(AH_TEST_ADAPT=1 PATH="$RT:$PATH" AH_SLOTS_DIR="$SB/rt6" AH_TEST_SLOTS=0 bash "$RUN" fakecheck w 2>&1)
check "...with the limiter off the command runs as it is: no plan, no tokens, no adapter" '[ "$OUT" = "workers=none grant= args=w" ]'
OUT=$(AH_TEST_ADAPT=0 PATH="$RT:$PATH" AH_SLOTS_DIR="$SB/rt6b" AH_TEST_TOKENS=8 bash "$RUN" fakecheck u 2>&1)
check "...AH_TEST_ADAPT=0 skips the registry step: half the bucket, no environment, no added arguments (nearest non-match of the first case)" '[ "$OUT" = "workers=none grant=4 args=u" ]'
OUT=$(AH_TEST_ADAPT=1 PATH="$RT:$PATH" AH_SLOTS_DIR="$SB/rt10" AH_TEST_TOKENS=8 bash "$RUN" fakedotnet --filter X 2>&1)
check "idle grant: an elastic run with no --tokens alone in the bucket gets the whole bucket, 8 of 8, and is planned again from its original words for it" \
  '[ "$OUT" = "grant=8 args=-m:8 --filter X" ]'
OUT=$(env -u MAKEFLAGS AH_TEST_ADAPT=1 PATH="$RT:$PATH" AH_SLOTS_DIR="$SB/rt11" AH_TEST_TOKENS=8 bash "$RUN" fakemake 2>&1)
check "...a variable the adapter appends to is planned again from its value before the first plan: one -j, the grant's" \
  '[ "$OUT" = "grant=8 MAKEFLAGS=-j8" ]'
AH_SLOTS_DIR="$SB/rt10" AH_TEST_TOKENS=8 bash "$RUN" --tokens 2 sleep 30 >/dev/null 2>&1 & IH=$!
# the busy case needs the holder's lease in place: wait for it, as long as the holder lives
while kill -0 "$IH" 2>/dev/null && ! ls "$SB"/rt10/held/*/holder >/dev/null 2>&1; do sleep 0.2; done
OUT=$(AH_TEST_ADAPT=1 PATH="$RT:$PATH" AH_SLOTS_DIR="$SB/rt10" AH_TEST_TOKENS=8 bash "$RUN" fakedotnet --filter X 2>&1)
check "...with another run holding 2 tokens it gets the planned 2 (nearest non-match)" '[ "$OUT" = "grant=2 args=-m:2 --filter X" ]'
ah_signal_child TERM "$IH"; wait "$IH" 2>/dev/null
mv "$RT/bin/ah" "$RT/bin/ah.off"
OUT=$(AH_TEST_ADAPT=1 PATH="$RT:$PATH" AH_SLOTS_DIR="$SB/rt7" AH_TEST_TOKENS=8 bash "$RUN" fakecheck v 2>&1)
check "...without the ah binary the command still runs, with half the bucket and no adapter" '[ "$OUT" = "workers=none grant=4 args=v" ]'
mv "$RT/bin/ah.off" "$RT/bin/ah"
AH_SLOTS_DIR="$SB/rt8" AH_TEST_TOKENS=8 bash "$RUN" --label "my label" sleep 3 >/dev/null 2>&1 & LB=$!
# wait for the labelled lease, as long as the holder lives
while kill -0 "$LB" 2>/dev/null && ! grep -q '"label": "my label"' "$SB"/rt8/held/*/holder 2>/dev/null; do sleep 0.2; done
check "--label names the holder in its lease" 'grep -q "\"label\": \"my label\"" "$SB"/rt8/held/*/holder'
ah_signal_child TERM "$LB"; wait "$LB" 2>/dev/null
OUT=$(AH_SLOTS_DIR="$SB/rt9" AH_TEST_TOKENS=8 bash "$RUN" echo noeta 2>&1); RC=$?
check "--eta is optional: without it the run goes (default medium)" '[ $RC = 0 ] && [ "$OUT" = noeta ]'
OUT=$(AH_SLOTS_DIR="$SB/rt9" AH_TEST_TOKENS=8 bash "$RUN" --eta 2>&1); RC=$?
check "...but a flag with no value is refused (exit 2)" '[ $RC = 2 ]'

# ---- the shim
OUT=$(AH_SLOTS_DIR="$SB/sh1" AH_TEST_TOKENS=8 bash "$PLUGIN/tests/with-slot.sh" --eta medium --tokens 2 sh -c 'echo "shim grant=$AH_TEST_TOKENS_GRANTED"; exit 7' 2>&1); RC=$?
check "tests/with-slot.sh behaves as bin/ah-test-run: the same flags, the grant, the exit code passed through" '[ $RC = 7 ] && [ "$OUT" = "shim grant=2" ]'
check "...and is nothing but an exec of it" '[ "$(grep -vc "^#\|^$" "$PLUGIN/tests/with-slot.sh")" = 1 ] && grep -q "exec .*bin/ah-test-run" "$PLUGIN/tests/with-slot.sh"'

# ---- a dotnet test filter that matched nothing fails the run (exit 4); anything else passes through
mkdir -p "$SB/fakebin"
cat >"$SB/fakebin/dotnet" <<'STUB'
#!/bin/bash
case "$STUB_MODE" in
  nomatch) echo "No test matches the given testcase filter \`$STUB_FILTER\` in /x/Some.Tests.dll"; exit 0 ;;
  matched) echo "Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3"; exit "${STUB_RC:-0}" ;;
  lingering) echo "Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3"; sleep 40 2>/dev/null </dev/null & echo $! >"$STUB_PIDFILE"; ps -ww -p $! -o lstart= | sed 's/^ *//; s/ *$//' >"$STUB_PIDFILE.stamp"; exit 0 ;;
esac
STUB
chmod +x "$SB/fakebin/dotnet"
SLOTB="$SB/slotsb"
OUT=$(PATH="$SB/fakebin:$PATH" STUB_MODE=nomatch STUB_FILTER='FullyQualifiedName~Nope' AH_SLOTS_DIR="$SLOTB" AH_TEST_SLOTS=2 bash "$WS" --eta 600 dotnet test x.csproj --filter 'FullyQualifiedName~Nope' 2>&1); RC=$?
check "B3-1 a filter that matched no test exits 4 with the message naming the filter" \
  '[ $RC = 4 ] && echo "$OUT" | grep -q "^with-slot: the test filter matched 0 tests; nothing was tested (filter: FullyQualifiedName~Nope)$"'
check "...and the command's own output still reaches the caller" 'echo "$OUT" | grep -q "No test matches the given testcase filter"'
OUT=$(PATH="$SB/fakebin:$PATH" STUB_MODE=nomatch STUB_FILTER='X' AH_SLOTS_DIR="$SLOTB" AH_TEST_SLOTS=2 bash "$WS" --eta 600 dotnet test x.csproj --filter=X 2>&1); RC=$?
check "...the --filter=<f> spelling too" '[ $RC = 4 ]'
OUT=$(PATH="$SB/fakebin:$PATH" STUB_MODE=matched STUB_RC=0 AH_SLOTS_DIR="$SLOTB" AH_TEST_SLOTS=2 bash "$WS" --eta 600 dotnet test x.csproj --filter 'FullyQualifiedName~Real' 2>&1); RC=$?
check "B3-2 a run that tested something keeps exit 0" '[ $RC = 0 ] && ! echo "$OUT" | grep -q "matched 0 tests"'
OUT=$(PATH="$SB/fakebin:$PATH" STUB_MODE=matched STUB_RC=1 AH_SLOTS_DIR="$SLOTB" AH_TEST_SLOTS=2 bash "$WS" --eta 600 dotnet test x.csproj --filter 'FullyQualifiedName~Real' 2>&1); RC=$?
check "...and a failing run keeps its own exit code (1)" '[ $RC = 1 ]'
T0=$SECONDS
OUT=$(PATH="$SB/fakebin:$PATH" STUB_MODE=lingering STUB_PIDFILE="$SB/linger.pid" AH_SLOTS_DIR="$SLOTB" AH_TEST_SLOTS=2 bash "$WS" --eta 600 dotnet test x.csproj --filter 'FullyQualifiedName~Real' 2>&1); RC=$?
ELAPSED=$((SECONDS - T0))
LINGER_LEFT=0; kill -0 "$(cat "$SB/linger.pid" 2>/dev/null)" 2>/dev/null && LINGER_LEFT=1
[ -s "$SB/linger.pid" ] && ah_signal_stamped TERM "$(cat "$SB/linger.pid")" "$(cat "$SB/linger.pid.stamp" 2>/dev/null)"
check "a process the command leaves running does not keep the run alive after the command ends (exit 0, under 20s)" '[ $RC = 0 ] && [ "$ELAPSED" -lt 20 ] && echo "$OUT" | grep -q "Passed!"'
check "...and it is stopped with the run, not left to be reparented" '[ "$LINGER_LEFT" = 0 ]'
AH_SLOTS_DIR="$SLOTB" AH_TEST_SLOTS=2 bash "$WS" --eta 600 sh -c 'perl -e "setpgrp(0, 0); exec q(sleep), 60" >/dev/null 2>&1 </dev/null & echo $! >"$1"; ps -ww -p $! -o lstart= | sed "s/^ *//; s/ *$//" >"$1.stamp"; sleep 1' _ "$SB/owngroup.pid" >/dev/null 2>&1
OWN_LEFT=0; kill -0 "$(cat "$SB/owngroup.pid" 2>/dev/null)" 2>/dev/null && OWN_LEFT=1
[ -s "$SB/owngroup.pid" ] && ah_signal_stamped TERM "$(cat "$SB/owngroup.pid")" "$(cat "$SB/owngroup.pid.stamp" 2>/dev/null)"
check "...but a process that took its own group is not touched (nearest non-match)" '[ "$OWN_LEFT" = 1 ]'
OUT=$(PATH="$SB/fakebin:$PATH" STUB_MODE=nomatch STUB_FILTER='' AH_SLOTS_DIR="$SLOTB" AH_TEST_SLOTS=2 bash "$WS" --eta 600 dotnet test x.csproj 2>&1); RC=$?
check "B3-3 the no-match output without --filter is unchanged (exit 0)" '[ $RC = 0 ] && ! echo "$OUT" | grep -q "matched 0 tests"'
OUT=$(PATH="$SB/fakebin:$PATH" STUB_MODE=nomatch STUB_FILTER='X' AH_SLOTS_DIR="$SLOTB" AH_TEST_SLOTS=2 bash "$WS" --eta 600 dotnet build x.csproj --filter X 2>&1); RC=$?
check "...and a command that is not dotnet test is unchanged even with --filter" '[ $RC = 0 ] && ! echo "$OUT" | grep -q "matched 0 tests"'

# ---- the test-scope check: `ah test plan --check` runs first and unconditionally (a stub ah here); 64 means refused and nothing runs
CK="$SB/checkplug"; mkdir -p "$CK/bin" "$CK/tests"
cp "$PLUGIN/bin/ah-test-run" "$CK/bin/"; cp "$PLUGIN/tests/lib-pid.sh" "$PLUGIN/tests/lib-tokens.sh" "$CK/tests/"
cat >"$CK/bin/ah" <<'EOF'
#!/bin/sh
case "$*" in
  *--check*)
    [ "${STUB_CHECK_RC:-0}" = 64 ] && echo "ah: whole-project test runs belong to QA (stub)" >&2
    [ "${STUB_CHECK_RC:-0}" = 3 ] && echo "ah: stub check broke" >&2
    exit "${STUB_CHECK_RC:-0}" ;;
esac
printf 'row\t-\ntokens\t0\n'
EOF
chmod +x "$CK/bin/ah" "$CK/bin/ah-test-run"
CKRUN="$CK/bin/ah-test-run"
unset AH_MEMBER_NAME
OUT=$(AH_TEST_ADAPT=0 STUB_CHECK_RC=64 HOME="$SB/emptyHome" AH_SLOTS_DIR="$SB/ck1" AH_TEST_TOKENS=8 bash "$CKRUN" sh -c 'echo ran' 2>&1); RC=$?
check "scope check: a refusal (exit 64) is passed through with its text and the command does not run, even with AH_TEST_ADAPT=0" \
  '[ $RC = 64 ] && echo "$OUT" | grep -q "belong to QA" && ! echo "$OUT" | grep -q "^ran$"'
OUT=$(AH_TEST_ADAPT=0 STUB_CHECK_RC=0 HOME="$SB/emptyHome" AH_SLOTS_DIR="$SB/ck2" AH_TEST_TOKENS=8 bash "$CKRUN" sh -c 'echo ran' 2>&1); RC=$?
check "...the same call when the check passes (exit 0) runs the command (nearest non-match)" '[ $RC = 0 ] && [ "$OUT" = ran ]'
OUT=$(AH_TEST_ADAPT=0 STUB_CHECK_RC=64 AH_TEST_SLOTS=0 HOME="$SB/emptyHome" AH_SLOTS_DIR="$SB/ck3" bash "$CKRUN" sh -c 'echo ran' 2>&1); RC=$?
check "...and the limiter being off (AH_TEST_SLOTS=0) does not skip the check" '[ $RC = 64 ] && ! echo "$OUT" | grep -q "^ran$"'
OUT=$(AH_TEST_ADAPT=0 STUB_CHECK_RC=3 AH_MEMBER_NAME=m1 HOME="$SB/emptyHome" AH_SLOTS_DIR="$SB/ck4" AH_TEST_TOKENS=8 bash "$CKRUN" sh -c 'echo ran' 2>&1); RC=$?
check "...a check that fails some other way refuses a member (64), and shows what the check said" '[ $RC = 64 ] && ! echo "$OUT" | grep -q "^ran$" && echo "$OUT" | grep -q "stub check broke"'
OUT=$(AH_TEST_ADAPT=0 STUB_CHECK_RC=3 HOME="$SB/emptyHome" AH_SLOTS_DIR="$SB/ck5" AH_TEST_TOKENS=8 bash "$CKRUN" sh -c 'echo ran' 2>&1); RC=$?
check "...but not a session without the member marker (nearest non-match)" '[ $RC = 0 ] && [ "$OUT" = ran ]'
CKN="$SB/checknone"; mkdir -p "$CKN/bin" "$CKN/tests"
cp "$PLUGIN/bin/ah-test-run" "$CKN/bin/"; cp "$PLUGIN/tests/lib-pid.sh" "$PLUGIN/tests/lib-tokens.sh" "$CKN/tests/"
OUT=$(AH_TEST_ADAPT=0 AH_MEMBER_NAME=m1 PATH=/usr/bin:/bin HOME="$SB/emptyHome" AH_SLOTS_DIR="$SB/ck6" AH_TEST_TOKENS=8 /bin/bash "$CKN/bin/ah-test-run" sh -c 'echo ran' 2>&1); RC=$?
check "...without any ah binary a member's run is refused (64)" '[ $RC = 64 ] && echo "$OUT" | grep -q "cannot be checked" && ! echo "$OUT" | grep -q "^ran$"'
OUT=$(AH_TEST_ADAPT=0 PATH=/usr/bin:/bin HOME="$SB/emptyHome" AH_SLOTS_DIR="$SB/ck7" AH_TEST_TOKENS=8 /bin/bash "$CKN/bin/ah-test-run" sh -c 'echo ran' 2>&1); RC=$?
check "...and without any ah binary a session without the member marker still runs (nearest non-match)" '[ $RC = 0 ] && [ "$OUT" = ran ]'

# ---- a caller's exported AH_BIN reaches the command; the check still uses the binary it found
OUT=$(AH_BIN="$SB/marker-ah" AH_TEST_ADAPT=0 HOME="$SB/emptyHome" AH_SLOTS_DIR="$SB/ck11" AH_TEST_TOKENS=8 bash "$CKRUN" sh -c 'printf %s "$AH_BIN"' 2>&1); RC=$?
check "AH_BIN: a caller's exported AH_BIN is the one the command sees" '[ $RC = 0 ] && [ "$OUT" = "$SB/marker-ah" ]'
OUT=$(AH_BIN= AH_TEST_ADAPT=0 HOME="$SB/emptyHome" AH_SLOTS_DIR="$SB/ck12" AH_TEST_TOKENS=8 bash "$CKRUN" sh -c 'printf %s "$AH_BIN"' 2>&1); RC=$?
check "...but an exported empty AH_BIN is not kept: the command sees the binary that ran the check (nearest non-match)" '[ $RC = 0 ] && [ "$OUT" = "$CK/bin/ah" ]'

# ---- a member's work tree whose own bin/ah has no native binary: the installed ah (~/.local/bin/ah) answers the check
CKW="$SB/checkworktree"; mkdir -p "$CKW/bin" "$CKW/tests" "$SB/instHome/.local/bin" "$SB/emptyHome"
cp "$PLUGIN/bin/ah-test-run" "$CKW/bin/"; cp "$PLUGIN/tests/lib-pid.sh" "$PLUGIN/tests/lib-tokens.sh" "$CKW/tests/"
printf '#!/bin/sh\necho "ah: no native binary is installed; run: sh install.sh --apply" >&2\nexit 1\n' >"$CKW/bin/ah"
cat >"$SB/instHome/.local/bin/ah" <<'EOF'
#!/bin/sh
case "$*" in
  *--check*) [ "${STUB_CHECK_RC:-0}" = 64 ] && echo "ah: whole-project test runs belong to QA (installed stub)" >&2; exit "${STUB_CHECK_RC:-0}" ;;
esac
printf 'row\t-\ntokens\t0\n'
EOF
chmod +x "$CKW/bin/ah" "$CKW/bin/ah-test-run" "$SB/instHome/.local/bin/ah"
OUT=$(HOME="$SB/instHome" AH_TEST_ADAPT=0 AH_MEMBER_NAME=m1 PATH=/usr/bin:/bin AH_SLOTS_DIR="$SB/ck8" AH_TEST_TOKENS=8 /bin/bash "$CKW/bin/ah-test-run" sh -c 'echo ran' 2>&1); RC=$?
check "worktree: a failing in-tree bin/ah falls back to the installed ah, and a member's run proceeds" '[ $RC = 0 ] && [ "$OUT" = ran ]'
OUT=$(HOME="$SB/instHome" STUB_CHECK_RC=64 AH_TEST_ADAPT=0 AH_MEMBER_NAME=m1 PATH=/usr/bin:/bin AH_SLOTS_DIR="$SB/ck9" AH_TEST_TOKENS=8 /bin/bash "$CKW/bin/ah-test-run" sh -c 'echo ran' 2>&1); RC=$?
check "...and the installed ah's refusal (64) still stops it, with its text" '[ $RC = 64 ] && echo "$OUT" | grep -q "installed stub" && ! echo "$OUT" | grep -q "^ran$"'
OUT=$(HOME="$SB/emptyHome" AH_TEST_ADAPT=0 AH_MEMBER_NAME=m1 PATH=/usr/bin:/bin AH_SLOTS_DIR="$SB/ck10" AH_TEST_TOKENS=8 /bin/bash "$CKW/bin/ah-test-run" sh -c 'echo ran' 2>&1); RC=$?
check "...with no installed ah either, a member's run is still refused (nearest non-match)" '[ $RC = 64 ] && echo "$OUT" | grep -q "cannot be checked" && ! echo "$OUT" | grep -q "^ran$"'

echo
echo "RESULT: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
