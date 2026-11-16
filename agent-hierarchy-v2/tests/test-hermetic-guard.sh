#!/bin/bash
# agent-hierarchy — the guard in tests/lib-hermetic.sh that fails a test run which leaves a test's rows in the real hook-errors
# log. The guard is pointed at a sandbox file (AH_HERMETIC_REAL_LOG, which exists for this test), so nothing real is touched.
# Usage: bash tests/test-hermetic-guard.sh   (exits 0 iff every row passes)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
HERE="$(cd "$(dirname "$0")" && pwd)"
T=$(mktemp -d /private/tmp/ahprobe.XXXXXX)
case "$T" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting" >&2; exit 1 ;; esac
hermetic_on_exit 'rm -rf "$T"'
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=${RC:-} ERR=${ERR:0:200})"; fi
}

LOG="$T/hook-errors.jsonl"
BASE='{"ts":"2026-10-12T00:00:00.000Z","hook":"pretooluse-ultra-gate.mjs","event":null,"session_id":null,"message":"x","stack":[]}'

# guarded <body run after lib-hermetic is sourced, against the sandbox log> -> RC, ERR
guarded() {
  ERR=$(env -u AH_TEST_HERMETIC -u AH_TEST_FAKE_BIN -u AH_TEST_PID -u AH_TEST_TRIPWIRE -u TMUX_TMPDIR AH_HERMETIC_REAL_LOG="$LOG" bash -c '. "$1"; eval "$2"' bash "$HERE/lib-hermetic.sh" "$1" 2>&1 >/dev/null); RC=$?
}

printf '%s\n' "$BASE" > "$LOG"
guarded ':'
check "a run that changes nothing passes" '[ $RC = 0 ] && [ -z "$ERR" ]'

printf '%s\n' "$BASE" > "$LOG"
guarded 'printf "%s\n" "{\"ts\":\"t\",\"hook\":\"posttooluse-transcript.mjs\",\"event\":null,\"session_id\":null,\"message\":\"disk full\",\"stack\":[]}" >> "$AH_HERMETIC_REAL_LOG"'
check "a real hook's row appended during the run (the user's own sessions) is left alone" '[ $RC = 0 ] && [ -z "$ERR" ]'

printf '%s\n' "$BASE" > "$LOG"
guarded 'printf "%s\n" "{\"ts\":\"t\",\"hook\":\"zzprobe.mjs\",\"event\":null,\"session_id\":null,\"message\":\"m\",\"stack\":[]}" >> "$AH_HERMETIC_REAL_LOG"'
check "a probe unit's row appended during the run fails the run" '[ $RC = 1 ] && printf %s "$ERR" | grep -q "appended test rows to the real hook-errors log"'

printf '%s\n' "$BASE" > "$LOG"
guarded 'printf "%s\n" "{\"ts\":\"t\",\"hook\":\"posttooluse-x.mjs\",\"event\":null,\"session_id\":null,\"message\":\"ah: AH_HOME is not the default home and is not trusted, so ah did not run. Ask\",\"stack\":[]}" >> "$AH_HERMETIC_REAL_LOG"'
check "the shim's refusal text appended during the run fails the run" '[ $RC = 1 ] && printf %s "$ERR" | grep -q "so ah did not run"'

# the very row the shim writes (bin/ah-trust.sh ah_hook_refusal) is what the guard looks for
PWH="$T/pw"; mkdir -p "$PWH/.claude/ah2"
ERR=$(AH_HERMETIC_REAL_LOG="$PWH/.claude/ah2/hierarchy/hook-errors.jsonl" bash -c '. "$1"; . "$2"; . "$3"; PATH=/usr/bin:/bin ah_hook_refusal "$4" posttooluse-probe "ah: AH_HOME is not the default home and is not trusted, so ah did not run. Ask the user to trust this home from a terminal, or to unset AH_HOME."' \
  bash "$HERE/lib-hermetic.sh" "$HERE/../bin/ah-testmode.sh" "$HERE/../bin/ah-trust.sh" "$PWH" 2>&1 >/dev/null); RC=$?
check "a row written by the shim's own refusal path fails the run" '[ $RC = 1 ] && printf %s "$ERR" | grep -q "appended test rows"'

# a row that was already there before the run is not this run's
printf '%s\n' "$BASE" '{"ts":"t","hook":"zzprobe.mjs","event":null,"session_id":null,"message":"old","stack":[]}' > "$LOG"
guarded ':'
check "a probe row already in the log before the run does not fail it" '[ $RC = 0 ]'

# a log that is rotated away or removed during the run is not a violation
printf '%s\n' "$BASE" "$BASE" > "$LOG"
guarded ': > "$AH_HERMETIC_REAL_LOG"'
check "a log truncated or rotated during the run is not a violation" '[ $RC = 0 ]'
printf '%s\n' "$BASE" > "$LOG"
guarded 'rm -f "$AH_HERMETIC_REAL_LOG"'
check "a log removed during the run is not a violation" '[ $RC = 0 ]'

# a log that rotated during the run: the new file is all this run's, the old one is at .1 past the recorded size
ROT='mv "$AH_HERMETIC_REAL_LOG" "$AH_HERMETIC_REAL_LOG.1"'
PROBE='{\"ts\":\"t\",\"hook\":\"zzprobe.mjs\",\"event\":null,\"session_id\":null,\"message\":\"m\",\"stack\":[]}'
REAL='{\"ts\":\"t\",\"hook\":\"posttooluse-transcript.mjs\",\"event\":null,\"session_id\":null,\"message\":\"disk full\",\"stack\":[]}'
rm -f "$LOG" "$LOG.1"; printf '%s\n' "$BASE" > "$LOG"
guarded "$ROT; printf '%s\n' \"$PROBE\" > \"\$AH_HERMETIC_REAL_LOG\""
check "a probe row in the new file after a rotation fails the run" '[ $RC = 1 ] && printf %s "$ERR" | grep -q "appended test rows"'
rm -f "$LOG" "$LOG.1"; printf '%s\n' "$BASE" > "$LOG"
guarded "printf '%s\n' \"$PROBE\" >> \"\$AH_HERMETIC_REAL_LOG\"; $ROT; printf '%s\n' \"$REAL\" > \"\$AH_HERMETIC_REAL_LOG\""
check "a probe row appended before the rotation, now at .1, fails the run" '[ $RC = 1 ] && printf %s "$ERR" | grep -q "appended test rows"'
rm -f "$LOG" "$LOG.1"; printf '%s\n' "$BASE" > "$LOG"
guarded "$ROT; printf '%s\n' \"$REAL\" > \"\$AH_HERMETIC_REAL_LOG\""
check "a rotation with only a real hook's row afterwards is left alone" '[ $RC = 0 ] && [ -z "$ERR" ]'
rm -f "$LOG" "$LOG.1"; printf '%s\n' "$BASE" '{"ts":"t","hook":"zzprobe.mjs","event":null,"session_id":null,"message":"old","stack":[]}' > "$LOG"
guarded "$ROT; printf '%s\n' \"$REAL\" > \"\$AH_HERMETIC_REAL_LOG\""
check "a probe row that was in the log before the run and is now at .1 does not fail it" '[ $RC = 0 ]'
# a .1 left by an earlier rotation is another file: its rows are not this run's even when the log is replaced during the run
rm -f "$LOG" "$LOG.1"; printf '%s\n' "$BASE" > "$LOG"
printf '%s\n' "$BASE" '{"ts":"t","hook":"zzprobe.mjs","event":null,"session_id":null,"message":"older","stack":[]}' '{"ts":"t","hook":"zzprobe.mjs","event":null,"session_id":null,"message":"older","stack":[]}' > "$LOG.1"
guarded "rm -f \"\$AH_HERMETIC_REAL_LOG\"; printf '%s\n' \"$REAL\" > \"\$AH_HERMETIC_REAL_LOG\""
check "a probe row in a .1 that is not the rotated log does not fail a run that replaced the log" '[ $RC = 0 ] && [ -z "$ERR" ]'
rm -f "$LOG" "$LOG.1"

# the failing status survives a test that itself exits 0, and a failing test keeps its own status
printf '%s\n' "$BASE" > "$LOG"
guarded 'printf "%s\n" "{\"hook\":\"zzprobe.mjs\"}" >> "$AH_HERMETIC_REAL_LOG"; exit 0'
check "an explicit exit 0 in the test does not hide the rows" '[ $RC = 1 ]'
printf '%s\n' "$BASE" > "$LOG"
guarded 'exit 7'
check "a test's own failing status is kept when nothing was appended" '[ $RC = 7 ]'

# no log at all
rm -f "$LOG"
guarded ':'
check "no log is no violation" '[ $RC = 0 ]'

# attribution: the log is shared by every run on the machine, so a row is the test's only when the test file names its unit
# guarded_file <name> <body>: a test file of its own (the guard reads it) that sources lib-hermetic.sh and then runs <body>
guarded_file() {
  local f="$T/$1.sh"
  printf '. "%s"\n%s\n' "$HERE/lib-hermetic.sh" "$2" > "$f"
  ERR=$(env -u AH_TEST_HERMETIC -u AH_TEST_FAKE_BIN -u AH_TEST_PID -u AH_TEST_TRIPWIRE -u TMUX_TMPDIR AH_HERMETIC_REAL_LOG="$LOG" bash "$f" 2>&1 >/dev/null); RC=$?
}
ROWFMT='{"ts":"t","hook":"%s","event":null,"session_id":null,"message":"ah: AH_HOME is not the default home and is not trusted, so ah did not run.","stack":[]}'

printf '%s\n' "$BASE" > "$LOG"
guarded_file own "# this file runs posttooluse-owned
printf '$ROWFMT\\n' posttooluse-owned >> \"\$AH_HERMETIC_REAL_LOG\""
check "a unit the test file names fails the run, and the report names the unit and the file" \
  '[ $RC = 1 ] && printf %s "$ERR" | grep -q "appended test rows" && printf %s "$ERR" | grep -q "unit: posttooluse-owned" && printf %s "$ERR" | grep -q "$T/own.sh"'

printf '%s\n' "$BASE" > "$LOG"
guarded_file other "U=posttooluse-; U=\${U}other
printf '$ROWFMT\\n' \"\$U\" >> \"\$AH_HERMETIC_REAL_LOG\""
check "a unit the test file does not name is reported as another run's and does not fail the run" \
  '[ $RC = 0 ] && printf %s "$ERR" | grep -q "note: rows for unit: posttooluse-other" && printf %s "$ERR" | grep -q "this run is not failed" && ! printf %s "$ERR" | grep -q "appended test rows"'

printf '%s\n' "$BASE" > "$LOG"
guarded_file near "# names posttooluse-rost only
U=posttooluse-; U=\${U}roster
printf '$ROWFMT\\n' \"\$U\" >> \"\$AH_HERMETIC_REAL_LOG\""
check "a unit that only shares a prefix with a name in the file is not the test's (nearest non-match)" \
  '[ $RC = 0 ] && printf %s "$ERR" | grep -q "note: rows for unit: posttooluse-roster"'

printf '%s\n' "$BASE" > "$LOG"
guarded_file mixed "# runs posttooluse-owned
U=posttooluse-; U=\${U}other
printf '$ROWFMT\\n' \"\$U\" posttooluse-owned >> \"\$AH_HERMETIC_REAL_LOG\""
check "own and foreign rows together: the run fails on its own unit and the note names the other" \
  '[ $RC = 1 ] && printf %s "$ERR" | grep -q "unit: posttooluse-owned" && printf %s "$ERR" | grep -q "note: rows for unit: posttooluse-other"'

echo "---"; echo "pass=$PASS fail=$FAIL"
[ "$FAIL" = 0 ]
