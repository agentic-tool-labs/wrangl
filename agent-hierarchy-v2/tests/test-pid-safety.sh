#!/bin/bash
# agent-hierarchy — no test signals a process by number without an ownership check.
# A pid recorded earlier can belong to an unrelated process by the time it is signalled (a busy machine reuses pids within
# minutes), and the signal kills that process. tests/lib-pid.sh holds the checked ways to signal a number; this fails any other
# `kill <variable>`, `kill -9 <n>`, `pkill`, `killall` or `process.kill(<n>)` in tests/. A line that signals a handle the test
# itself holds (its own child, a group it made) says so with a trailing `# pid-owned: <why>`.
# Usage: bash tests/test-pid-safety.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
HERE="$(cd "$(dirname "$0")" && pwd)"
SB="$(mktemp -d "${TMPDIR:-/tmp}/ahprobe.XXXXXX")"
[ -n "$SB" ] && [ -d "$SB" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:600})"; fi; }

# unsafe <file>: prints <file>:<line>:<text> for every line that signals a number, or a name, without the helper or a marker.
unsafe() {
  grep -nE '(^|[^A-Za-z0-9_.$/-])(pkill|killall|kill)([[:space:]]|$)|process\.kill\(' "$1" 2>/dev/null \
    | grep -vE '^[0-9]+:[[:space:]]*(#|//)' \
    | grep -v 'pid-owned:' \
    | sed -E 's/kill[[:space:]]+-(0|l)[[:space:]]+(--[[:space:]]+)?("[^"]*"|[^[:space:];&|)]+)//g' \
    | grep -E '(^|[^A-Za-z0-9_.$/-])(pkill|killall)([[:space:]]|$)|(^|[^A-Za-z0-9_.$/-])kill(([[:space:]]+-[A-Za-z]+)|([[:space:]]+--))*[[:space:]]+[-"'"'"']*(\$[A-Za-z_{(]|[0-9])|process\.kill\([^p)]' \
    | sed "s|^|$1:|"
}

# the check can fail: planted lines of every unsafe shape are reported, and the allowed shapes are not
cat >"$SB/planted.sh" <<'PLANT'
kill "$pid" 2>/dev/null
kill -9 $p
kill -"$sig" "$pid"
for g in $pgs; do kill -KILL -- "-$g"; done
pkill -f something
killall node
node -e 'process.kill(1234, "SIGKILL")'
PLANT
cat >"$SB/allowed.sh" <<'ALLOW'
kill -0 "$p" 2>/dev/null
kill -0 "$p" && echo up
kill -l
kill -l $SIG
kill -TERM $$
kill -INT $$
ah_signal_child TERM "$p"
ah_signal_ours KILL "$p" "$SANDBOX"
kill "$p"  # pid-owned: a child this test started
# kill "$p"
check "7.2.8: Stop after the kill -> block"
node -e 'process.kill(process.pid, "SIGKILL")'
ALLOW
OUT=$(unsafe "$SB/planted.sh")
check "a planted bare kill, kill -9, signal variable, group kill, pkill, killall and process.kill(<n>) are all reported" '[ "$(printf "%s\n" "$OUT" | grep -c .)" = 7 ]'
OUT=$(unsafe "$SB/allowed.sh")
check "kill -0, kill -l, a signal to the test itself, the helpers, a marked line, a comment and process.kill(process.pid) are not reported (nearest non-match)" '[ -z "$OUT" ]'

# needles <file>: prints <file>:<line>:<variable> for every ah_signal_ours / ah_pid_named whose needle names a variable the same file
# assigns from a process listing (ps, pgrep, lsof, a descendants helper, grep -o or awk over one). A path read out of a command line
# can be another run's, or the directory the binary under test lives in; the needle is the suite's own mktemp result.
needles() {
  local f=$1 line n v
  grep -nE 'ah_(signal_ours|pid_named)[[:space:]]' "$f" 2>/dev/null | grep -vE '^[0-9]+:[[:space:]]*#' | while IFS= read -r line; do
    n=$(printf '%s' "${line#*:}" | sed -nE 's/.*ah_(signal_ours|pid_named)[^;&|]*"([^"]*)"[[:space:]]*($|;|&&|\|\||\)).*/\2/p')
    case "$n" in *'/ahd'*|*'$BIN'*|*'${BIN'*|*'$AH_BIN'*|*'$AHD_NATIVE_BIN'*) echo "$f:${line%%:*}:$n"; continue ;; esac
    v=$(printf '%s' "$n" | grep -oE '\$\{?[A-Za-z_][A-Za-z0-9_]*' | head -1 | tr -d '${')
    [ -n "$v" ] || continue
    if grep -E "(^|[;{[:space:]])(local[[:space:]]+)?$v=" "$f" | grep -E '[[:space:](|]ps[[:space:]]|pgrep|lsof|descendants|grep -o|[[:space:]]awk[[:space:]]' | grep -q .; then
      echo "$f:${line%%:*}:$v"
    fi
  done
}
cat >"$SB/needle-bad.sh" <<'PLANT'
SB=$(printf '%s\n' "$D" | grep -o '/private/tmp/ahprobe\.[A-Za-z0-9]\{6\}' | head -1)
for p in $(pgrep -f -- "$SB/"); do ah_signal_ours KILL "$p" "$SB/"; done
ROOT=$(ps -axo command= | awk '/serve/ {print $4}')
ah_pid_named "$p" "$ROOT" && echo yes
for p in $found; do ah_signal_ours TERM "$p" "$BIN/ahd"; done
PLANT
cat >"$SB/needle-ok.sh" <<'ALLOW'
T=$(mktemp -d /private/tmp/ahprobe.XXXXXX)
D=$(ps -axo command= | grep serve)
for p in $(pgrep -f -- "$T/"); do ah_signal_ours KILL "$p" "$T/"; done
ah_signal_ours KILL "$(cat "$T/pid")" "sleep 3143"
# ah_signal_ours KILL "$p" "$D"
ALLOW
OUT=$(needles "$SB/needle-bad.sh")
check "a needle assigned from a process listing, or the binary's own path, is reported, for ah_signal_ours and ah_pid_named alike" '[ "$(printf "%s\n" "$OUT" | grep -c .)" = 3 ]'
OUT=$(needles "$SB/needle-ok.sh")
check "a needle that is the suite's own mktemp result, a literal command, or a comment is not reported; a listing assigned to another variable is not (nearest non-match)" '[ -z "$OUT" ]'
BAD=""
for f in "$HERE"/*.sh; do
  [ -f "$f" ] || continue
  case "$(basename "$f")" in lib-pid.sh|test-pid-safety.sh) continue ;; esac
  BAD="$BAD$(needles "$f")"$'\n'
done
OUT="$(printf '%s' "$BAD" | grep . | head -20)"
check "no test signals by a needle it read out of a process listing" '[ -z "$OUT" ]'

# lib-pid.sh refuses a needle the binary under test's own path contains: it would name every process running that binary
bash -c 'sleep 60; :' "$SB/marker" & KP=$!   # pid-owned: a process this test started (two commands, so bash stays the process and its argv names the marker)
for i in $(seq 1 20); do ah_pid_named "$KP" "$SB/marker" && break; sleep 0.1; done
mkdir -p "$SB/bin"
AH_BIN="$SB/bin/ahd" ah_signal_ours KILL "$KP" "$SB/" 2>/dev/null; RC=$?
check "a needle that is a directory above the binary is refused (status 2) and nothing is signalled" '[ "$RC" = 2 ] && kill -0 "$KP" 2>/dev/null'
AH_BIN="$SB/bin/ahd" ah_signal_ours KILL "$KP" "$SB/bin" 2>/dev/null; RC=$?
check "a needle that is the binary's own directory is refused" '[ "$RC" = 2 ] && kill -0 "$KP" 2>/dev/null'
AH_BIN="$SB/bin/ahd" ah_signal_ours KILL "$KP" "$SB/bin/ahd" 2>/dev/null; RC=$?
check "a needle that is the binary's path is refused" '[ "$RC" = 2 ] && kill -0 "$KP" 2>/dev/null'
AHD_NATIVE_BIN="$SB/bin/ahd" ah_signal_ours KILL "$KP" "$SB/" 2>/dev/null; RC=$?
check "the binary named by AHD_NATIVE_BIN is guarded the same way" '[ "$RC" = 2 ] && kill -0 "$KP" 2>/dev/null'
AH_BIN="$SB/bin/ahd" ah_signal_ours KILL "$KP" "$SB/marker"; RC=$?
wait "$KP" 2>/dev/null
check "a needle beside the binary's directory, not inside its path, signals the process (nearest non-match)" '[ "$RC" = 0 ] && ! kill -0 "$KP" 2>/dev/null'

# the tests themselves
BAD=""
for f in "$HERE"/*.sh "$HERE"/*.mjs "$HERE"/fixtures/*; do
  [ -f "$f" ] || continue
  case "$(basename "$f")" in lib-pid.sh|test-pid-safety.sh) continue ;; esac
  BAD="$BAD$(unsafe "$f")"$'\n'
done
OUT="$(printf '%s' "$BAD" | grep . | head -20)"
check "no test signals a number without lib-pid.sh or a pid-owned marker" '[ -z "$OUT" ]'

echo
echo "Passed: $PASS  Failed: $FAIL"
[ "$FAIL" -eq 0 ]
