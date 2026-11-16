#!/bin/bash
# agent-hierarchy — the shared test environment (tests/lib-test-env.sh): every tests/test-*.sh sources it as its
# first command after the hermetic preamble, so a test started directly (`bash tests/test-x.sh`) starts no daemon unless it opted in.
# Usage: bash tests/test-test-env.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SB="$(mktemp -d "${TMPDIR:-/tmp}/ahprobe.XXXXXX")"
[ -n "$SB" ] && [ -d "$SB" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:300})"; fi; }

# first_commands <file>: prints the first two non-blank, non-comment lines after the hermetic preamble line.
first_commands() { grep -v -E '^[[:space:]]*(#|$)' "$1" | grep -v -F 'lib-hermetic.sh' | head -2; }
SRC_RE='^(\.|source) +"?\$\(cd "\$\(dirname "\$0"\)" && pwd\)/lib-test-env\.sh"?$'

MISSING=""
for f in "$PLUGIN"/tests/test-*.sh; do
  b=$(basename "$f")
  l1=$(first_commands "$f" | sed -n 1p)
  l2=$(first_commands "$f" | sed -n 2p)
  if printf '%s\n' "$l1" | grep -Eq "$SRC_RE"; then continue; fi
  if [ "$l1" = "AH_TEST_DAEMON=1" ] && printf '%s\n' "$l2" | grep -Eq "$SRC_RE"; then continue; fi
  MISSING="$MISSING $b"
done
OUT="not first:$MISSING"
check "every tests/test-*.sh sources lib-test-env.sh as its first command (opt-in files set AH_TEST_DAEMON=1 on the line before)" '[ -z "$MISSING" ]'

# the check can fail: a file that sources it second is reported
printf '#!/bin/bash\necho hi\n. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"\n' > "$SB/test-late.sh"
l1=$(first_commands "$SB/test-late.sh" | sed -n 1p)
check "a file whose first command is something else is not accepted" '! printf "%s\n" "$l1" | grep -Eq "$SRC_RE"'

# opt-in files are exactly the daemon's own tests
OPT=""
for f in "$PLUGIN"/tests/test-*.sh; do
  [ "$(first_commands "$f" | sed -n 1p)" = "AH_TEST_DAEMON=1" ] && OPT="$OPT $(basename "$f" .sh)"
done
OUT="opted in:$OPT"
BAD=""
for o in $OPT; do case "$o" in test-ahd-*|test-delivery-ladder-daemon|test-pipeline-decisions|test-native-sessionstart|test-tui-no-team|test-hook-state-writes|test-stall-failsafe|test-roster-reboot-resume) ;; *) BAD="$BAD $o";; esac; done
OUT="opted in outside the daemon's own tests:$BAD"
check "only the daemon's own tests opt in" '[ -z "$BAD" ]'

# the helper itself
mkdir -p "$SB/tests"; cp "$PLUGIN/tests/lib-test-env.sh" "$PLUGIN/tests/lib-pid.sh" "$SB/tests/"
printf '#!/bin/bash\n. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"\necho "[$AH_DAEMON]"\n' > "$SB/tests/test-plain.sh"
printf '#!/bin/bash\nAH_TEST_DAEMON=1\n. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"\necho "[$AH_DAEMON]"\n' > "$SB/tests/test-optin.sh"
OUT=$(env -u AH_DAEMON bash "$SB/tests/test-plain.sh")
check "a test run directly gets AH_DAEMON=off" '[ "$OUT" = "[off]" ]'
printf '#!/bin/bash\n. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"\necho "$AH_HOME|$TMPDIR"\n' > "$SB/tests/test-tmp.sh"
OUT=$(env -u AH_HOME TMPDIR="$SB" bash "$SB/tests/test-tmp.sh")
OWN_ROOT=$(dirname "${OUT%%|*}")
check "a test run directly with no AH_HOME gets a TMPDIR inside its sandbox" '[ "${OUT#*|}" = "$OWN_ROOT/tmp" ]'
case "$OWN_ROOT" in /tmp/ahprobe.*) rm -rf "$OWN_ROOT" ;; esac
OUT=$(AH_HOME="$SB/given" TMPDIR="$SB" bash "$SB/tests/test-tmp.sh")
check "negative: a caller that names AH_HOME keeps its own TMPDIR" '[ "$OUT" = "$SB/given|$SB" ]'
OUT=$(env -u AH_DAEMON bash "$SB/tests/test-optin.sh")
check "...unless it set AH_TEST_DAEMON=1 first" '[ "$OUT" = "[]" ]'
OUT=$(AH_DAEMON=off bash "$SB/tests/test-optin.sh")
check "an opted-in test gets a daemon-enabled environment even when its caller exported AH_DAEMON=off (as run-suite.sh does)" '[ "$OUT" = "[]" ]'
OUT=$(AH_DAEMON=off bash "$SB/tests/test-plain.sh")
check "a test that did not opt in keeps AH_DAEMON=off when its caller exported it (nearest non-match)" '[ "$OUT" = "[off]" ]'
OUT=$(AH_DAEMON=on bash "$SB/tests/test-plain.sh")
check "a test that did not opt in has a caller's other value overridden to off (nearest non-match)" '[ "$OUT" = "[off]" ]'

# a test never gets the user's own AH_HOME
printf '#!/bin/bash\n. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"\necho "[$AH_HOME]"\n' > "$SB/tests/test-home.sh"
OUT=$(env -u AH_HOME HOME="$SB/home" bash "$SB/tests/test-home.sh")
check "a test run directly gets a private AH_HOME of its own, never <HOME>/.claude/ah2" '[ "$OUT" != "[]" ] && [ "$OUT" != "[$SB/home/.claude/ah2]" ] && [ "$OUT" != "[$HOME/.claude/ah2]" ]'
OUT=$(AH_HOME="$SB/given" bash "$SB/tests/test-home.sh")
check "a test whose caller gave an AH_HOME keeps it (nearest non-match)" '[ "$OUT" = "[$SB/given]" ]'

# and a directly started test really starts no daemon
printf '#!/bin/bash\n. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"\n"%s/bin/ahd" start\necho "rc=$?"\n' "$PLUGIN" > "$SB/tests/test-nodaemon.sh"
OUT=$(env -u AH_DAEMON AH_TEST=1 AH_HOME="$SB/ahh" HOME="$SB/home" bash "$SB/tests/test-nodaemon.sh" 2>&1)
check "a directly started test that asks for a daemon gets none (refused, no run directory created)" 'echo "$OUT" | grep -q "rc=1" && [ ! -e "$SB/ahh/run/ahd.json" ] && [ -z "$(ps -axo command= | grep -F -- "--ah-home $SB/ahh" | grep -v grep)" ]'

# drop_project_state keeps the roster, the conventions file and the other agent-hierarchy* files and removes the rest
PD="$SB/proj"; mkdir -p "$PD/.claude/ah2/teams" "$PD/.claude/ah2/msgs" "$PD/.claude/ah2/pipeline/r1" "$PD/.claude/ah2/agent-hierarchy.state"
: > "$PD/.claude/ah2/agent-hierarchy.json"; : > "$PD/.claude/ah2/ah-conventions.json"; : > "$PD/.claude/ah2/agent-hierarchy.usage.jsonl"
: > "$PD/.claude/ah2/teams/t.json"; : > "$PD/.claude/ah2/peers.jsonl"; : > "$PD/.claude/ah2/ah-conventions.json.bak"; : > "$PD/.claude/ah2/team.json"
drop_project_state "$PD"
check "drop_project_state keeps the roster, the conventions file and the agent-hierarchy* entries" '[ -f "$PD/.claude/ah2/agent-hierarchy.json" ] && [ -f "$PD/.claude/ah2/ah-conventions.json" ] && [ -f "$PD/.claude/ah2/agent-hierarchy.usage.jsonl" ] && [ -d "$PD/.claude/ah2/agent-hierarchy.state" ]'
check "...and removes teams, messages, peers, pipeline runs and the team pointer" '[ ! -e "$PD/.claude/ah2/teams" ] && [ ! -e "$PD/.claude/ah2/msgs" ] && [ ! -e "$PD/.claude/ah2/pipeline" ] && [ ! -e "$PD/.claude/ah2/peers.jsonl" ] && [ ! -e "$PD/.claude/ah2/team.json" ]'
check "a file that only starts like the conventions file is removed (nearest non-match)" '[ ! -e "$PD/.claude/ah2/ah-conventions.json.bak" ]'
check "the project dir itself stays" '[ -d "$PD/.claude/ah2" ]'
drop_project_state "$SB/nowhere"; RC=$?
check "a repo with no project dir is not an error and creates nothing" '[ "$RC" -eq 0 ] && [ ! -e "$SB/nowhere" ]'

# a recorded pid is signalled only while its command line still names the sandbox
sleep 7001 & KP=$!
ah_signal_ours KILL "$KP" "no-such-sandbox-path"; RC1=$?
check "ah_signal_ours leaves a process whose command line does not name the needle (nearest non-match)" '[ $RC1 = 1 ] && kill -0 "$KP" 2>/dev/null'
ah_signal_ours KILL "$KP" "sleep 7001"; wait "$KP" 2>/dev/null
check "ah_signal_ours signals a process whose command line names the needle" '! kill -0 "$KP" 2>/dev/null'
ah_signal_child TERM "$KP"

# a test started directly is in test mode like a run-all worker: AH_TEST=1 whenever its AH_HOME is inside an ahprobe sandbox
mkdir -p "$SB/bin"; cp "$PLUGIN/bin/ah-testmode.sh" "$SB/bin/"; cp "$PLUGIN/tests/lib-hermetic.sh" "$SB/tests/"
printf '#!/bin/bash\n. "$(cd "$(dirname "$0")" && pwd)/lib-hermetic.sh"\n. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"\necho "[${AH_TEST-unset}]"\n' > "$SB/tests/test-mode.sh"
printf '#!/bin/bash\n. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"\necho "[${AH_TEST-unset}]"\n' > "$SB/tests/test-mode-bare.sh"
OUT=$(env -u AH_TEST -u AH_HOME bash "$SB/tests/test-mode.sh")
check "a test run directly (no AH_TEST, no AH_HOME) is in test mode: AH_TEST=1 with an AH_HOME in its own ahprobe sandbox" '[ "$OUT" = "[1]" ]'
OUT=$(env -u AH_HOME AH_TEST=0 bash "$SB/tests/test-mode.sh")
check "a caller's own AH_TEST value is kept (nearest non-match)" '[ "$OUT" = "[0]" ]'
OUT=$(env -u AH_TEST AH_HOME=/private/tmp/notaprobe.dir/ah bash "$SB/tests/test-mode-bare.sh")
check "an AH_HOME outside an ahprobe sandbox does not turn test mode on (nearest non-match)" '[ "$OUT" = "[unset]" ]'

# a test started directly runs the binary under test: AHD_NATIVE_BIN becomes AH_BIN
printf '#!/bin/bash\n. "$(cd "$(dirname "$0")" && pwd)/lib-hermetic.sh"\n. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"\necho "[${AH_BIN-unset}]"\n' > "$SB/tests/test-bin.sh"
OUT=$(env -u AH_BIN AHD_NATIVE_BIN=/usr/bin/true bash "$SB/tests/test-bin.sh")
check "a test run directly with AHD_NATIVE_BIN set runs with AH_BIN pinned to it" '[ "$OUT" = "[/usr/bin/true]" ]'
OUT=$(AH_BIN=/usr/bin/false AHD_NATIVE_BIN=/usr/bin/true bash "$SB/tests/test-bin.sh")
check "a caller's own AH_BIN is kept (nearest non-match)" '[ "$OUT" = "[/usr/bin/false]" ]'
OUT=$(env -u AH_BIN AHD_NATIVE_BIN="$SB/bin" bash "$SB/tests/test-bin.sh")
check "an AHD_NATIVE_BIN that is a directory pins nothing (nearest non-match)" '[ "$OUT" = "[unset]" ]'
: > "$SB/notexec-bin"
OUT=$(env -u AH_BIN AHD_NATIVE_BIN="$SB/notexec-bin" bash "$SB/tests/test-bin.sh")
check "an AHD_NATIVE_BIN that is not executable pins nothing (nearest non-match)" '[ "$OUT" = "[unset]" ]'
OUT=$(env -u AH_BIN -u AHD_NATIVE_BIN bash "$SB/tests/test-bin.sh")
check "with neither set AH_BIN stays unset" '[ "$OUT" = "[unset]" ]'

# run-suite.sh refuses without a native binary and, with one, gives every test the binary and test mode as a run-all worker does
mkdir -p "$SB/p/tests"; cp "$PLUGIN/tests/run-suite.sh" "$PLUGIN/tests/lib-daemon-leak.sh" "$SB/p/tests/"
printf '#!/bin/bash\nprintf "[%%s][%%s]" "${AH_TEST-unset}" "${AH_BIN-unset}" > "%s/seen"\n' "$SB/p" > "$SB/p/tests/test-zzenv.sh"
: > "$SB/p/notexec"
OUT=$(env -u AHD_NATIVE_BIN TMPDIR="$SB" bash "$SB/p/tests/run-suite.sh" test-zzenv 2>&1); RC=$?
check "run-suite.sh with no native binary runs nothing and exits 1" '[ "$RC" = 1 ] && printf %s "$OUT" | grep -q "no native binary" && [ ! -e "$SB/p/seen" ]'
OUT=$(AHD_NATIVE_BIN="$SB/p/notexec" TMPDIR="$SB" bash "$SB/p/tests/run-suite.sh" test-zzenv 2>&1); RC=$?
check "...also when AHD_NATIVE_BIN names a file that is not executable (nearest non-match)" '[ "$RC" = 1 ] && printf %s "$OUT" | grep -q "no native binary" && [ ! -e "$SB/p/seen" ]'
OUT=$(AHD_NATIVE_BIN=/usr/bin/true TMPDIR="$SB" bash "$SB/p/tests/run-suite.sh" test-zzenv 2>&1); RC=$?
check "with a native binary every test runs with AH_TEST=1 and AH_BIN set to it" '[ "$(cat "$SB/p/seen" 2>/dev/null)" = "[1][/usr/bin/true]" ]'

# ---- ah_test_detached: a command with none of the caller's ancestors, and its output and status back
ANCESTORS='p=$$; while [ "$p" -gt 1 ]; do p=$(ps -o ppid= -p "$p" | tr -d " "); [ -n "$p" ] || break; echo "$p"; done'
PLAIN=$(sh -c "$ANCESTORS")
check "a plain child has this shell among its ancestors (the case ah_test_detached is for)" 'printf "%s\n" "$PLAIN" | grep -qx "$$"'
DET=$(ah_test_detached sh -c "$ANCESTORS"); RC=$?
check "ah_test_detached: the command's ancestors do not include this shell" '[ $RC = 0 ] && [ -n "$DET" ] && ! printf "%s\n" "$DET" | grep -qx "$$"'
DET=$(ah_test_detached sh -c 'echo out; echo err >&2; exit 7' 2>"$SB/det.err"); RC=$?
check "...its stdout and stderr come back on ours and its exit status is returned" '[ $RC = 7 ] && [ "$DET" = out ] && [ "$(cat "$SB/det.err")" = err ]'
DET=$(AH_TEST_DETACHED_SECONDS=1 ah_test_detached sh -c 'sleep 30' 2>&1); RC=$?
check "...a command that outlives AH_TEST_DETACHED_SECONDS is stopped and the call returns 124" '[ $RC = 124 ]'

# ---- a sandbox home is on the store by default: sourcing writes no mode row
OUT=$(sqlite3 "$AH_HOME/state/ah.db" "SELECT value FROM meta WHERE key = 'mode.msgs'" 2>&1)
check "a sandbox home has no mode.msgs = files row" '[ "$OUT" != files ]'

echo
echo "Passed: $PASS  Failed: $FAIL"
[ "$FAIL" -eq 0 ]
