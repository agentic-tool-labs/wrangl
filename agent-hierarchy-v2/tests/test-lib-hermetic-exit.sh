#!/bin/bash

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
HERE=$(cd "$(dirname "$0")" && pwd)
T=$(mktemp -d /tmp/ahprobe.XXXXXX)
[ -n "$T" ] && [ -d "$T" ] || exit 1
hermetic_on_exit 'rm -rf "$T"'
export AH_TEST_FAKE_BIN="$T${AH_TEST_FAKE_BIN:+:$AH_TEST_FAKE_BIN}"
PASS=0; FAIL=0
check() {
  if eval "$2"; then
    PASS=$((PASS+1)); echo "PASS: $1"
  else
    FAIL=$((FAIL+1)); echo "FAIL: $1"
    echo "RC=$RC"; head -3 "$T/out" "$T/err"
  fi
}
run_case() {
  printf '. "%s/lib-hermetic.sh"\n%s\n' "$HERE" "$1" > "$T/case.sh"
  : > "$T/rows"
  env -u AH_TEST_HERMETIC -u AH_TEST_PID AH_REAL_TMUX= AH_HERMETIC_REAL_LOG="$T/rows" bash "$T/case.sh" > "$T/out" 2> "$T/err"
  RC=$?
}
ROW='{"hook":"zz-hermetic-owned","message":"probe"}'
run_case 'printf "%s\n" '\''{"hook":"zz-hermetic-owned","message":"probe"}'\'' >> "$AH_HERMETIC_REAL_LOG"'
check 'owned row flips exit and names the count and unit' '[ "$RC" = 1 ] && grep -qx "FAIL: hermetic exit: 1 hook-error rows from this run (zz-hermetic-owned)" "$T/out"'
check 'owned row is printed on stdout' 'grep -qxF "$ROW" "$T/out"'
printf -v padding '%0250d' 0
LONGROW=$(printf '{"hook":"zz-hermetic-owned","message":"%s"}' "$padding")
run_case "printf '%s\\n' '$LONGROW' '$ROW' >> \"\$AH_HERMETIC_REAL_LOG\""
PREVIEW=$(printf '%s' "$LONGROW" | cut -c1-200)
check 'multiple rows report their count and only a 200-character first-row preview' '[ "$RC" = 1 ] && grep -qx "FAIL: hermetic exit: 2 hook-error rows from this run (zz-hermetic-owned)" "$T/out" && [ "$(sed -n "2p" "$T/out")" = "$PREVIEW" ] && [ "$(wc -l < "$T/out" | tr -d " ")" = 2 ]'
run_case 'U=zz-hermetic-; U=${U}foreign; printf "{\"hook\":\"%s\"}\n" "$U" >> "$AH_HERMETIC_REAL_LOG"'
check 'foreign unit is ignored with the existing stderr note' '[ "$RC" = 0 ] && [ ! -s "$T/out" ] && grep -q "note: rows for unit: zz-hermetic-foreign" "$T/err"'
run_case ':'
check 'no rows preserve success' '[ "$RC" = 0 ] && [ ! -s "$T/out" ]'
run_case 'exit 7'
check 'no leak preserves a failing exit status' '[ "$RC" = 7 ] && [ ! -s "$T/out" ]'
for outside in /tmp/not-a-probe-log "$T/../not-a-probe-log" "$T/../../tmp/not-a-probe-log"; do
  env -u AH_TEST_HERMETIC -u AH_TEST_PID AH_REAL_TMUX= AH_HERMETIC_REAL_LOG="$outside" bash -c '. "$1"; [ "$_AH_REAL_LOG" != "$AH_HERMETIC_REAL_LOG" ]' bash "$HERE/lib-hermetic.sh" > "$T/out" 2> "$T/err"
  RC=$?
  check 'override outside a probe sandbox is ignored' '[ "$RC" = 0 ]'
done
cat > "$T/tmux" <<'SH'
#!/bin/bash
exit "${FAKE_TMUX_STATUS:-0}"
SH
chmod +x "$T/tmux"
mkdir "$T/bin"
cat > "$T/bin/find" <<'SH'
#!/bin/bash
printf '%s/socket\n' "$1"
SH
chmod +x "$T/bin/find"
run_case 'AH_REAL_TMUX="$(dirname "$AH_HERMETIC_REAL_LOG")/tmux"
export PATH="$(dirname "$AH_HERMETIC_REAL_LOG")/bin:$PATH"'
check 'leftover tmux flips exit with its stdout reason' '[ "$RC" = 1 ] && grep -qx "FAIL: hermetic exit: tmux server left under TMUX_TMPDIR" "$T/out"'
run_case 'AH_REAL_TMUX="$(dirname "$AH_HERMETIC_REAL_LOG")/tmux"; export FAKE_TMUX_STATUS=1
export PATH="$(dirname "$AH_HERMETIC_REAL_LOG")/bin:$PATH"'
check 'a socket without a tmux server does not flip exit' '[ "$RC" = 0 ] && [ ! -s "$T/out" ]'
bash --posix -n "$HERE/lib-hermetic.sh" > "$T/out" 2> "$T/err"; RC=$?
check 'the library parses under sh (POSIX mode)' '[ "$RC" = 0 ]'
mkdir "$T/w"
cp "$HERE/test-web-client.sh" "$T/w/"
printf 'return 1\n' > "$T/w/lib-hermetic.sh"
sh "$T/w/test-web-client.sh" > "$T/out" 2> "$T/err"; RC=$?
check 'test-web-client.sh fails when the hermetic library fails to source' '[ "$RC" != 0 ] && ! grep -q "^PASS" "$T/out"'
printf ':\n' > "$T/w/lib-hermetic.sh"
cp "$HERE/lib-test-env.sh" "$HERE/lib-pid.sh" "$T/w/"
sh "$T/w/test-web-client.sh" > "$T/out" 2> "$T/err"; RC=$?
check 'test-web-client.sh gets past the library when it sources cleanly' 'grep -q "^PASS" "$T/out" || grep -q "^FAIL" "$T/out"'
echo "PASS: $PASS FAIL: $FAIL"
[ "$FAIL" = 0 ]
