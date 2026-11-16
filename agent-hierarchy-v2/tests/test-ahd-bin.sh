#!/bin/bash
# agent-hierarchy — bin/ahd, the bare `ahd` command: it runs the native binary with the caller's argv, stdio and exit code,
# also when reached through a symlink or from another directory. Fakes only: a sandbox HOME and AH_HOME, no daemon.
# Usage: bash tests/test-ahd-bin.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
T=$(mktemp -d /private/tmp/ahprobe.XXXXXX)
case "$T" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting" >&2; exit 1;; esac
hermetic_on_exit 'rm -rf "$T"'
export HOME="$T/home" AH_HOME="$T/ah" AH_TEST=1; mkdir -p "$HOME" "$AH_HOME" "$T/elsewhere" "$T/links"
unset WRANGL_TEAM_FILE CLAUDE_PID
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:200} ERR=${ERR:0:200})"; fi
}
run() { OUT=$("$@" 2>"$T/err"); RC=$?; ERR=$(cat "$T/err"); }

BIN="$PLUGIN/bin/ahd"
ln -s "$BIN" "$T/links/ahd"
ln -s "$T/no-such-target" "$T/links/dangling"

# outside a git checkout (an exported archive) there is no index to read; the on-disk mode check below still holds
check "bin/ahd is committed as an executable (mode 100755)" '! git -C "$PLUGIN" rev-parse --git-dir >/dev/null 2>&1 || [ "$(git -C "$PLUGIN" ls-files -s bin/ahd | cut -d" " -f1)" = 100755 ]'
check "bin/ahd is executable on disk" '[ -x "$BIN" ]'

run "$BIN" status
check "direct path: status exits 0 and answers an ok JSON document" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "\"ok\":true"'
run "$T/links/ahd" status
check "through a symlink: the same answer" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "\"ok\":true"'
run bash -c "cd '$T/elsewhere' && '$T/links/ahd' status"
check "from another cwd, through a symlink: the same answer" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "\"ok\":true"'

run "$BIN" bogus
check "an unknown verb exits 2 and names it on stderr, with nothing on stdout" '[ $RC = 2 ] && printf %s "$ERR" | grep -q "^ahd: unknown command .bogus.$" && [ -z "$OUT" ]'
run "$BIN" drain --require
check "a verb that needs the daemon exits 3 when it is down" '[ $RC = 3 ] && printf %s "$OUT" | grep -q daemon_down'
run "$BIN" status --plain
check "a successful verb exits 0 (nearest non-match of the 2 and 3 cases)" '[ $RC = 0 ]'

run "$BIN" --help
check "--help prints the usage block and exits 0" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "ahd start | stop | restart"'
run "$T/links/ahd" --help
check "--help through a symlink prints the same text" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "ahd start | stop | restart"'

run "$T/links/dangling" status
check "a symlink to a missing target fails with a non-zero exit and a message" '[ $RC != 0 ] && [ -n "$ERR$OUT" ]'

# the state-home rule: an AH_HOME that is neither the default home, nor trusted, nor a test sandbox is refused before anything runs
run env -u AH_TEST "$BIN" status
check "outside test mode an AH_HOME that is not the default home is refused with exit 2 and a reason" '[ $RC = 2 ] && printf %s "$ERR" | grep -q "is not the default home and is not trusted" && [ -z "$OUT" ]'
check "the refusal names no trust command to a reader that is not a terminal" '! printf %s "$ERR" | grep -q "ahd home trust"'
run env AH_HOME="relative/home" "$BIN" status
check "a relative AH_HOME is refused" '[ $RC = 2 ] && printf %s "$ERR" | grep -q "not an absolute path"'

echo "---"; echo "pass=$PASS fail=$FAIL"
[ "$FAIL" = 0 ]
