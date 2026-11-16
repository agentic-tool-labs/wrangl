#!/bin/bash
# agent-hierarchy — run_progress (scripts/lib-progress.sh), which shows a slow install step's progress:
#   at a terminal, a redrawn spinner line with the command's latest output, finished with ✓ or ✗; piped, the plain output with
#   no control codes; either way the full output in the log, the command's status returned, and on failure the log's tail and
#   path. publish-native.sh, install.sh and dogfood.sh all go through it.
# Every run is in a sandbox under /private/tmp/ahprobe.*.
# Usage: bash tests/test-progress.sh   (exits 0 iff all cases pass)
. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n"; fi; }
has() { case "$1" in *"$2"*) return 0;; esac; return 1; }

ESC=$'\033'
# A command whose output comes in two steps, with a pause between them long enough for the spinner to show the first.
cat > "$SB/cmd.sh" <<'EOF'
echo "  step one"; sleep 0.7; echo "  step two"; exit "${1:-0}"
EOF
# run IN_TTY LOG STATUS: run_progress over cmd.sh, at a pseudo-terminal when IN_TTY is true, with stderr folded in.
run() {
    local inner=". '$PLUGIN/scripts/lib-progress.sh'; run_progress demo '$2' bash '$SB/cmd.sh' $3; echo \"rc=\$?\""
    if $1; then
        if [ "$(uname)" = Darwin ]; then script -q /dev/null bash -c "$inner 2>&1" < /dev/null
        else script -qc "bash -c \"$inner 2>&1\"" /dev/null < /dev/null; fi
    else
        bash -c "$inner" 2>&1 | cat
    fi
}

out=$(run true "$SB/tty-ok.log" 0)
check "1 at a terminal, the line is redrawn in place" 'has "$out" "${ESC}[K⠋ demo"'
# The elapsed time is not pinned: on a loaded machine the first redraw that shows the output line can come after the first second.
check "2 ... with a spinner frame and the latest output line" 'case "$out" in *"⠋ demo · "[0-9]*s*) ;; *) false;; esac && case "$out" in *"demo · "[0-9]*"s · step one"*) ;; *) false;; esac'
check "3 ... finished with a check mark and the total time" 'has "$out" "✓ demo · "'
check "4 ... the status is returned" 'has "$out" "rc=0"'
check "5 ... a success prints no log tail" '! has "$out" "last lines of"'
check "6 ... the log holds the full output" '[ "$(cat "$SB/tty-ok.log")" = "$(printf "  step one\n  step two")" ]'

out=$(run true "$SB/tty-bad.log" 3)
check "7 at a terminal, a failure finishes with a cross" 'has "$out" "✗ demo · "'
check "8 ... returns the command's status" 'has "$out" "rc=3"'
check "9 ... and prints the log tail and its path" 'has "$out" "last lines of $SB/tty-bad.log" && has "$out" "full log: $SB/tty-bad.log"'

out=$(run false "$SB/pipe-ok.log" 0)
check "10 piped, the output is printed plainly" '[ "$out" = "$(printf "  step one\n  step two\nrc=0")" ]'
check "11 ... with no escape codes or carriage returns" '! has "$out" "$ESC" && ! has "$out" $'"'"'\r'"'"''
check "12 ... and the log holds it too" '[ "$(cat "$SB/pipe-ok.log")" = "$(printf "  step one\n  step two")" ]'

out=$(run false "$SB/pipe-bad.log" 4)
check "13 piped, a failure returns the command's status" 'has "$out" "rc=4"'
check "14 ... and prints the log tail and its path, with no escape codes" 'has "$out" "full log: $SB/pipe-bad.log" && ! has "$out" "$ESC"'

# Ctrl-C at the terminal: ^C typed into the pty reaches the whole foreground group. The command's child ignores HUP, so only
# run_progress's own kill (not the pty closing) can stop it; the outer shell catches INT so it can print the inner status.
# The marker process is named by this run's sandbox path, so nothing else matches it. A suite runner can start this file with
# INT ignored, which a bash it starts cannot undo, so perl resets INT to its default before the shell under the pty starts.
MARK="$SB/long-marker"
cat > "$SB/long.sh" <<EOF
echo "  working"; trap '' HUP; bash -c 'sleep 41.7; :' '$MARK' & wait
EOF
cat > "$SB/ctrlc.sh" <<EOF
echo start; trap : INT
bash -c ". '$PLUGIN/scripts/lib-progress.sh'; run_progress demo '$SB/int.log' bash '$SB/long.sh'; echo after-run_progress" 2>&1
echo "rc=\$?"
EOF
if [ "$(uname)" = Darwin ]; then out=$( (sleep 1.5; printf '\003'; sleep 3) | script -q /dev/null perl -e '$SIG{INT} = "DEFAULT"; exec @ARGV' bash "$SB/ctrlc.sh")
else out=$( (sleep 1.5; printf '\003'; sleep 3) | script -qc "perl -e '\$SIG{INT} = \"DEFAULT\"; exec @ARGV' bash '$SB/ctrlc.sh'" /dev/null); fi
sleep 0.3
check "15 a Ctrl-C stops the command's whole process tree" '! pgrep -f "$MARK" >/dev/null'
check "16 ... finishes the line with a cross and interrupted" 'has "$out" "✗ demo · interrupted · "'
check "17 ... and the shell dies of the interrupt (130), going no further" 'has "$out" "rc=130" && ! has "$out" "after-run_progress"'
pkill -f "$MARK" 2>/dev/null  # pid-owned: only this run's own marker process carries its sandbox path

check "18 publish-native.sh, install.sh and dogfood.sh load the helper" \
    '( for s in publish-native install dogfood; do grep -q "lib-progress.sh\" ||" "$PLUGIN/scripts/$s.sh" || exit 1; done )'
check "19 publish-native.sh shows its restore and publishes through it" '[ "$(grep -c "run_progress " "$PLUGIN/scripts/publish-native.sh")" = 2 ]'
check "20 dogfood.sh shows its claude calls and the install through it" '[ "$(grep -c "run_progress " "$PLUGIN/scripts/dogfood.sh")" = 2 ]'

echo "progress: $PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
