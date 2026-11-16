#!/bin/bash
# agent-hierarchy — suites never inherit the invoking session's CLAUDE_PID: every tests/*.sh that
# unsets WRANGL_TEAM_FILE also unsets CLAUDE_PID, so a check that needs an orchestrator pid sets it
# itself and the suite passes the same inside and outside a Claude session.
# It also lints that every suite sources tests/lib-hermetic.sh first and declares its fake herdr/tmux
# directories in AH_TEST_FAKE_BIN, and proves the guard in hooks/lib-mux.mjs fires (spec 0091).
# Usage: bash tests/test-suite-hermeticity.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it

PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-suite-hermeticity-test.XXXXXX")"
hermetic_on_exit 'rm -rf "$SANDBOX"'
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (OUT=${OUT:0:500})"; fi
}

# Each *.sh in <dir> that unsets WRANGL_TEAM_FILE but never CLAUDE_PID, one per line.
lint() {
  local f
  for f in "$1"/*.sh; do
    grep -qE '^[[:space:]]*(export PATH=[^;]*;[[:space:]]*)?unset[[:space:]]([^#]*[[:space:]])?WRANGL_TEAM_FILE([[:space:];]|$)' "$f" || continue
    grep -qE '^[[:space:]]*(export PATH=[^;]*;[[:space:]]*)?unset[[:space:]]([^#]*[[:space:]])?CLAUDE_PID([[:space:];]|$)' "$f" || basename "$f"
  done
}

OUT=$(lint "$PLUGIN/tests")
check "T-H3: every suite that unsets WRANGL_TEAM_FILE also unsets CLAUDE_PID" '[ -z "$OUT" ]'

mkdir -p "$SANDBOX/scratch"
printf '#!/bin/bash\nunset WRANGL_TEAM_FILE  # x\necho hi\n' > "$SANDBOX/scratch/test-only-team-file.sh"
printf '#!/bin/bash\nunset WRANGL_TEAM_FILE  # x\nunset CLAUDE_PID  # x\n' > "$SANDBOX/scratch/test-both-lines.sh"
printf '#!/bin/bash\nexport PATH="/x:$PATH"; unset HERDR_ENV WRANGL_TEAM_FILE CLAUDE_PID\n' > "$SANDBOX/scratch/test-both-one-line.sh"
printf '#!/bin/bash\nunset WRANGL_TEAM_FILE\n# unset CLAUDE_PID only in a comment\n' > "$SANDBOX/scratch/test-comment-only.sh"
printf '#!/bin/bash\necho no unsets here\n' > "$SANDBOX/scratch/test-neither.sh"
OUT=$(lint "$SANDBOX/scratch" | sort | tr '\n' ' ')
check "T-H3: the lint names a suite that unsets only WRANGL_TEAM_FILE, or names CLAUDE_PID only in a comment, and nothing else" \
  '[ "$OUT" = "test-comment-only.sh test-only-team-file.sh " ]'

# ---- H1 / H2: every suite runs under the shared guard (spec 0091 section 3.3).
# H1: the first line that is not blank, a comment or a `set` line sources lib-hermetic.sh.
lint_h1() {
  local f first
  for f in "$1"/test-*.sh; do
    first=$(grep -vE '^[[:space:]]*($|#|set[[:space:]])' "$f" | head -1)
    case "$first" in
      '. "$(dirname "$0")/lib-hermetic.sh"') ;;
      *) basename "$f" ;;
    esac
  done
}
# H2: every directory a suite writes an executable herdr, tmux, claude or codex into appears in an AH_TEST_FAKE_BIN export of that suite.
lint_h2() {
  local f exports dirs d
  for f in "$1"/test-*.sh; do
    dirs=$(grep -E '(>|tee |cp |ln -s |chmod \+x )[^|;&]*/(herdr|tmux|claude|codex)("|[[:space:]]|<|;|&|$)' "$f" |
      sed -nE 's#.*[ ">]"?(\$\{?[A-Za-z_][A-Za-z0-9_]*\}?[A-Za-z0-9_./${}-]*)/(herdr|tmux|claude|codex)("|[[:space:]]|<|;|&|$).*#\1#p' | sort -u)
    [ -n "$dirs" ] || continue
    exports=$(grep -E '(^|;)[[:space:]]*export AH_TEST_FAKE_BIN=' "$f")
    for d in $dirs; do
      case "$exports" in *"$d"*) ;; *) echo "$(basename "$f"):$d" ;; esac
    done
  done
}

OUT=$(lint_h1 "$PLUGIN/tests")
check "H1: every tests/test-*.sh sources lib-hermetic.sh before anything else runs" '[ -z "$OUT" ]'
OUT=$(lint_h2 "$PLUGIN/tests")
check "H2: every suite that writes a fake herdr or tmux lists its directory in AH_TEST_FAKE_BIN" '[ -z "$OUT" ]'

mkdir -p "$SANDBOX/h1" "$SANDBOX/h2"
printf '#!/bin/bash\n# header\nset -u\n. "$(dirname "$0")/lib-hermetic.sh"\necho ok\n' > "$SANDBOX/h1/test-good.sh"
printf '#!/bin/bash\nPLUGIN=x\n. "$(dirname "$0")/lib-hermetic.sh"\n' > "$SANDBOX/h1/test-late.sh"
printf '#!/bin/bash\necho no source line\n' > "$SANDBOX/h1/test-none.sh"
OUT=$(lint_h1 "$SANDBOX/h1" | sort | tr '\n' ' ')
check "H1: a file whose first executable line is not the source line fails, a file that sources it first passes" '[ "$OUT" = "test-late.sh test-none.sh " ]'

printf '#!/bin/bash\nSANDBOX=x\nmkdir -p "$SANDBOX/bin"\ncat > "$SANDBOX/bin/%s" <<EOF\nexit 1\nEOF\nexport AH_TEST_FAKE_BIN="$SANDBOX/bin"\n' herdr > "$SANDBOX/h2/test-listed.sh"
printf '#!/bin/bash\nSANDBOX=x\nmkdir -p "$SANDBOX/bin"\ncat > "$SANDBOX/bin/%s" <<EOF\nexit 1\nEOF\n' herdr > "$SANDBOX/h2/test-unlisted.sh"
printf '#!/bin/bash\nSANDBOX=x\nprintf x > "$SANDBOX/fake/%s"\nexport AH_TEST_FAKE_BIN="$SANDBOX/bin"\n' tmux > "$SANDBOX/h2/test-wrongdir.sh"
printf '#!/bin/bash\necho nothing here\n' > "$SANDBOX/h2/test-nofake.sh"
OUT=$(lint_h2 "$SANDBOX/h2" | sort | tr '\n' ' ')
check "H2: a suite that writes a fake without listing its directory fails; listed and no-fake suites pass" \
  '[ "$OUT" = "test-unlisted.sh:\$SANDBOX/bin test-wrongdir.sh:\$SANDBOX/fake " ]'

# H3: no suite sets its own trap on EXIT, TERM, INT or HUP; the shared exit path in lib-hermetic.sh owns them, and a suite
# registers cleanup with hermetic_on_exit.
lint_h3() {
  local f
  for f in "$1"/test-*.sh; do
    grep -qE '^[[:space:]]*trap[[:space:]].*(EXIT|TERM|INT|HUP)' "$f" && basename "$f"
  done
}
# H4: no suite passes tmux -S a literal absolute path, or assigns one ending in .sock; sockets live under $TMUX_TMPDIR.
lint_h4() {
  local f
  for f in "$1"/test-*.sh; do
    grep -qE -e '-S[[:space:]]+"?/' -e '="?/[^" ]*\.sock' "$f" && basename "$f"
  done
}

OUT=$(lint_h3 "$PLUGIN/tests")
check "H3: no tests/test-*.sh sets a trap on EXIT, TERM, INT or HUP (they use hermetic_on_exit)" '[ -z "$OUT" ]'
OUT=$(lint_h4 "$PLUGIN/tests")
check "H4: no tests/test-*.sh puts a tmux socket at a literal absolute path" '[ -z "$OUT" ]'

mkdir -p "$SANDBOX/h3" "$SANDBOX/h4"
S_FLAG=-S
printf '#!/bin/bash\ntra%s rm_all EXIT\n' p > "$SANDBOX/h3/test-trap-exit.sh"
printf '#!/bin/bash\n  tra%s cleanup INT\n' p > "$SANDBOX/h3/test-trap-int.sh"
printf '#!/bin/bash\nhermetic_on_exit rm_all\n# a comment about a trap\n' > "$SANDBOX/h3/test-clean.sh"
OUT=$(lint_h3 "$SANDBOX/h3" | sort | tr '\n' ' ')
check "H3: a suite that sets a trap on EXIT or INT fails, one that uses hermetic_on_exit passes" '[ "$OUT" = "test-trap-exit.sh test-trap-int.sh " ]'
printf '#!/bin/bash\ntmux %s /tm%s/x.sock new-session\n' "$S_FLAG" p > "$SANDBOX/h4/test-literal-flag.sh"
printf '#!/bin/bash\nSOCK="%s"\n' "/tm$(printf p)/y.sock" > "$SANDBOX/h4/test-literal-assign.sh"
printf '#!/bin/bash\nSOCK="$TMUX_TMPDIR/z.sock"\ntmux %s "$SOCK" new-session\n' "$S_FLAG" > "$SANDBOX/h4/test-under-tmpdir.sh"
OUT=$(lint_h4 "$SANDBOX/h4" | sort | tr '\n' ' ')
check "H4: a literal /tmp socket path fails; a socket under TMUX_TMPDIR passes" '[ "$OUT" = "test-literal-assign.sh test-literal-flag.sh " ]'

# H5: no suite ends its own shell with a top-level exec. exec replaces the shell, so the shared exit path in lib-hermetic.sh
# (cleanup commands, the tmux-server sweep, removing TMUX_TMPDIR) never runs. Lines inside here-documents (fake scripts that
# do exec) and indented lines (a subshell's exec) are not the suite's own shell.
lint_h5() {
  python3 -I - "$1" <<'PYEOF'
import glob, os, re, sys

heredoc = re.compile(r"<<-?\s*(['\"]?)([A-Za-z_][A-Za-z0-9_]*)\1")
for f in sorted(glob.glob(os.path.join(sys.argv[1], "test-*.sh"))):
    tag = None
    dash = False
    hit = False
    for line in open(f, errors="replace").read().split("\n"):
        if tag is not None:
            if (line.lstrip("\t") if dash else line) == tag:
                tag = None
            continue
        if re.match(r"exec\s", line):
            hit = True
        m = heredoc.search(line)
        if m:
            tag, dash = m.group(2), "<<-" in line
    if hit:
        print(os.path.basename(f))
PYEOF
}

OUT=$(lint_h5 "$PLUGIN/tests")
check "H5: no tests/test-*.sh ends its own shell with a top-level exec (it would skip the shared exit path)" '[ -z "$OUT" ]'

mkdir -p "$SANDBOX/h5" "$SANDBOX/h5old"
printf '#!/bin/bash\n. "$(dirname "$0")/lib-hermetic.sh"\nPLUGIN=x\nex%s node "$PLUGIN/tests/x.mjs"\n' ec > "$SANDBOX/h5/test-exec-last.sh"
printf '#!/bin/bash\ncat > "$X/fake" <<%sEOF%s\nex%s real "$@"\nEOF\n( ex%s true )\n' "'" "'" ec ec > "$SANDBOX/h5/test-exec-in-heredoc.sh"
printf '#!/bin/bash\nnode x.mjs\n' > "$SANDBOX/h5/test-plain.sh"
OUT=$(lint_h5 "$SANDBOX/h5" | sort | tr '\n' ' ')
check "H5: a suite whose last line is exec fails; exec inside a here-document or a subshell, and a plain command, pass" '[ "$OUT" = "test-exec-last.sh " ]'

# the file this rule was found on, as it was before the fix: it ended with exec node
printf '#!/bin/bash\n# header\n. "$(dirname "$0")/lib-hermetic.sh"\nPLUGIN="$(cd "$(dirname "$0")/.." && pwd)"\nex%s node "$PLUGIN/tests/status-reader-hazard.mjs"\n' ec > "$SANDBOX/h5old/test-status-reader-hazard.sh"
OUT=$(lint_h5 "$SANDBOX/h5old")
check "H5: the old test-status-reader-hazard.sh (ending exec node) fails the lint" '[ "$OUT" = "test-status-reader-hazard.sh" ]'

# ---- G1-G7: the mux guard, against the real roster verbs.
G="$SANDBOX/g"
H="$PLUGIN/bin"
NODE_DIR="$(dirname "$(command -v node)")"
mkdir -p "$G/realbin" "$G/linkbin" "$G/home"
mkfake() { # <dir> <name>: a binary that logs its argv to $G/real.log and fails
  printf '#!/bin/sh\necho "%s $*" >> "%s"\nexit 1\n' "$2" "$G/real.log" > "$1/$2"
  chmod +x "$1/$2"
}
mkfake "$G/realbin" tmux
# the stand-in for a real herdr that runs when listed: it logs its argv to real.log like mkfake, then answers as the stateful fake
printf '#!/bin/sh\necho "herdr $*" >> "%s"\nFAKE_STATE_DIR="%s" exec "%s" "%s/fake-herdr.mjs" "$@"\n' "$G/real.log" "$G/herdr-state" "$(command -v node)" "$PLUGIN/tests" > "$G/realbin/herdr"
chmod +x "$G/realbin/herdr"
export AH_TEST_FAKE_BIN="$G/realbin${AH_TEST_FAKE_BIN:+:$AH_TEST_FAKE_BIN}"
printf '#!/bin/sh\necho "claude $*" >> "%s"\nexit 0\n' "$G/real.log" > "$G/realbin/claude"
chmod +x "$G/realbin/claude"
LINK_NAME=herdr
ln -s "$G/realbin/$LINK_NAME" "$G/linkbin/$LINK_NAME"
cat > "$G/child.sh" <<'CHILD'
#!/bin/bash
[ -n "${G_NOLIB:-}" ] || . "$G_LIB/lib-hermetic.sh"
[ -n "${G_LIST+x}" ] && export AH_TEST_FAKE_BIN="$G_LIST"
[ -n "${G_NOPID:-}" ] && unset AH_TEST_PID
[ -n "${G_NOLIB:-}" ] || hermetic_on_exit 'cp "$AH_TEST_TRIPWIRE" "$G_OUT/tripwire.copy" 2>/dev/null'
export PATH="$G_PATH" HOME="$G_HOME"
[ -n "${G_HERDR_ENV:-}" ] && export HERDR_ENV=1 HERDR_PANE_ID=w1:p0 HERDR_WORKSPACE_ID=w1
"$G_H/ah" roster stream-open sx --no-worktree --cwd "$G_PROJ" || exit $?
"$G_H/ah" roster spawn-ad-hoc implementor --model sonnet --route pane --stream sx --cwd "$G_PROJ"
CHILD
# run_g <env assignments...>: one child bash; GRC its exit status, GERR its stderr, GTRIP its tripwire file.
GRUN=0
run_g() {
  GRUN=$((GRUN + 1))
  GPROJ="$G/proj$GRUN"
  mkdir -p "$GPROJ"
  git -C "$GPROJ" init -q -b main
  git -C "$GPROJ" -c user.email=t@example.com -c user.name=t commit -q --allow-empty -m init
  rm -f "$G/real.log" "$G/tripwire.copy"
  ah_test_seed_fake_herdr "$G/herdr-state" 200 50 "$GPROJ" >/dev/null
  ah_test_run_as orchestrator tm "" \
    env -u AH_TEST_HERMETIC -u AH_TEST_FAKE_BIN -u AH_TEST_PID -u AH_TEST_TRIPWIRE -u TMUX_TMPDIR \
    G_LIB="$PLUGIN/tests" G_H="$H" G_OUT="$G" G_HOME="$G/home" G_PROJ="$GPROJ" "$@" bash "$G/child.sh" >"$G/out" 2>"$G/err"
  GRC=$?
  GERR=$(cat "$G/err")
  GTRIP=$(cat "$G/tripwire.copy" 2>/dev/null)
  OUT="rc=$GRC stdout=$(head -c 400 "$G/out" | tr '\n' ' ') stderr=${GERR:0:300} tripwire=${GTRIP:0:120} real.log=$(head -2 "$G/real.log" 2>/dev/null | tr '\n' ';')"
}
PATH_REAL="$G/realbin:$NODE_DIR:/usr/bin:/bin"

run_g G_PATH="$PATH_REAL" G_HERDR_ENV=1
check "G1: an unlisted herdr first on PATH: the child is killed, the binary never ran, the tripwire names it, stderr names it" \
  '[ "$GRC" -ne 0 ] && [ ! -e "$G/real.log" ] && printf "%s" "$GTRIP" | grep -q "^herdr " && printf "%s" "$GERR" | grep -q "real herdr"'

run_g G_PATH="$PATH_REAL" G_HERDR_ENV=1 G_LIST="$G/realbin"
check "G2: the same fake listed in AH_TEST_FAKE_BIN runs, nothing trips, the child completes" \
  '[ "$GRC" -eq 0 ] && grep -q "^herdr tab create" "$G/real.log" && [ -z "$GTRIP" ]'

run_g G_PATH="$PATH_REAL"
check "G3: tmux, unlisted: detectTransport reaches list-sessions, tripwire, killed, binary never ran" \
  '[ "$GRC" -ne 0 ] && [ ! -e "$G/real.log" ] && printf "%s" "$GTRIP" | grep -q "^tmux list-sessions" && printf "%s" "$GERR" | grep -q "real tmux"'
run_g G_PATH="$PATH_REAL" G_LIST="$G/realbin"
check "G3: tmux, listed: the fake runs and the child completes" '[ "$GRC" -eq 0 ] && grep -q "^tmux list-sessions" "$G/real.log" && [ -z "$GTRIP" ]'

run_g G_PATH="$PATH_REAL" G_HERDR_ENV=1 G_NOLIB=1
check "G4: no guard (AH_TEST_HERMETIC unset): the product path is unchanged and the fake runs" \
  '[ "$GRC" -eq 0 ] && grep -q "^herdr tab create" "$G/real.log"'

run_g G_PATH="$G/linkbin:$NODE_DIR:/usr/bin:/bin" G_HERDR_ENV=1 G_LIST="$G/linkbin"
check "G5: a listed directory holding a symlink to a binary outside it: tripwire, killed, binary never ran" \
  '[ "$GRC" -ne 0 ] && [ ! -e "$G/real.log" ] && printf "%s" "$GTRIP" | grep -q "^herdr "'

run_g G_PATH="$PATH_REAL" G_HERDR_ENV=1 G_NOPID=1
check "G6: AH_TEST_PID unset: no kill, but the tripwire line and the refusal still happen and the binary never ran" \
  '[ ! -e "$G/real.log" ] && printf "%s" "$GTRIP" | grep -q "^herdr " && printf "%s" "$GERR" | grep -q "real herdr"'

run_g G_PATH="$NODE_DIR:/usr/bin:/bin" G_HERDR_ENV=1
# b536: a tab list that fails no longer refuses by itself; the spawn goes on to create the tab and fails there
check "G7: no herdr anywhere on PATH: the spawn fails on the missing herdr itself, no tripwire, no kill" \
  '[ "$GRC" -ne 0 ] && printf "%s" "$GERR" | grep -q "herdr tab create .* failed: spawnSync herdr ENOENT" && [ -z "$GTRIP" ] && [ ! -e "$G/real.log" ]'

# ---- G8-G12 (r2): member launch strings, and test-owned tmux servers.
mkdir -p "$G/notmux" "$G/specproj"
mkfail() { printf '#!/bin/sh\nexit 1\n' > "$1/$2"; chmod +x "$1/$2"; }
mkfail "$G/notmux" tmux
mkfake "$G/realbin" claude
cat > "$G/child-spawn.sh" <<'CHILD'
#!/bin/bash
. "$G_LIB/lib-hermetic.sh"
hermetic_on_exit 'cp "$AH_TEST_TRIPWIRE" "$G_OUT/tripwire.copy" 2>/dev/null'
export AH_TEST_FAKE_BIN="$G_LIST"
export PATH="$G_PATH" HOME="$G_HOME"
"$G_H/ah" roster create --spawn --mode auto --cwd "$G_PROJ"
CHILD
cat > "$G/child-leak.sh" <<'CHILD'
#!/bin/bash
. "$G_LIB/lib-hermetic.sh"
hermetic_on_exit 'cp "$AH_TEST_TRIPWIRE" "$G_OUT/tripwire.copy" 2>/dev/null'
[ -n "$AH_REAL_TMUX" ] || exit 77
echo "$TMUX_TMPDIR" > "$G_OUT/leak-dir"
"$AH_REAL_TMUX" -S "$TMUX_TMPDIR/leak.sock" new-session -d -s leak || exit 78
case "$G_MODE" in
  leak) exit 0 ;;
  term) kill -TERM $$; sleep 3; exit 0 ;;
  tidy) "$AH_REAL_TMUX" -S "$TMUX_TMPDIR/leak.sock" kill-server; exit 0 ;;
esac
CHILD

# run_sh <child script> <env assignments...>: one child bash; GRC, GERR, and GTRIP from the copy the child saves on exit.
run_sh() {
  local child=$1; shift
  local as=()
  [ "$child" != child-spawn.sh ] || as=(ah_test_run_as orchestrator tm "")
  rm -f "$G/real.log" "$G/tripwire.copy" "$G/leak-dir"
  ${as[@]+"${as[@]}"} env -u AH_TEST_HERMETIC -u AH_TEST_FAKE_BIN -u AH_TEST_PID -u AH_TEST_TRIPWIRE -u TMUX_TMPDIR \
    G_LIB="$PLUGIN/tests" G_H="$H" G_OUT="$G" G_HOME="$G/home" "$@" bash "$G/$child" >"$G/out" 2>"$G/err"
  GRC=$?
  GERR=$(cat "$G/err")
  GTRIP=$(cat "$G/tripwire.copy" 2>/dev/null)
}

# G10 is the whole product path: create --spawn on the terminal transport launches "claude ... --bg" through runShell.
# Setup runs with a failing tmux listed, so detectTransport has nothing real to reach.
SP="$G/specproj"
git -C "$SP" init -q -b main
git -C "$SP" -c user.email=t@example.com -c user.name=t commit -q --allow-empty -m init
env HOME="$G/home" PATH="$G/notmux:$PATH" AH_TEST_FAKE_BIN="$G/notmux" "$PLUGIN/bin/ah" roster init --level repo --route peer --cwd "$SP" >/dev/null 2>&1
env HOME="$G/home" PATH="$G/notmux:$PATH" AH_TEST_FAKE_BIN="$G/notmux" "$PLUGIN/bin/ah" roster add --no-spawn --level repo --role implementor --model inherit --cwd "$SP" >/dev/null 2>&1
run_sh child-spawn.sh G_PROJ="$SP" G_LIST="$G/notmux" G_PATH="$G/notmux:$G/realbin:$NODE_DIR:/usr/bin:/bin"
OUT="rc=$GRC stderr=${GERR:0:300} tripwire=${GTRIP:0:120} real.log=$(head -2 "$G/real.log" 2>/dev/null | tr '\n' ';')"
check "G10: terminal transport launch (claude ... --bg), claude not listed: tripwire names claude, no real session starts" \
  '[ "$GRC" -ne 0 ] && [ ! -e "$G/real.log" ] && printf "%s" "$GTRIP" | grep -q "^claude "'

# The skip depends on a lookup that is independent of the variable under test: a lib-hermetic.sh that stopped finding tmux
# must fail here, not switch the leak cases off.
PROBE_TMUX=$(PATH="$PATH:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin" command -v tmux 2>/dev/null)
if [ -z "$PROBE_TMUX" ]; then
  echo "SKIP: G12 leak detection needs a real tmux; none found on PATH or in the usual install directories"
else
  check "G12: lib-hermetic.sh found the real tmux: AH_REAL_TMUX is set and equals an independent lookup" '[ -n "$AH_REAL_TMUX" ] && [ "$AH_REAL_TMUX" = "$PROBE_TMUX" ]'
  run_sh child-leak.sh G_MODE=leak
  check "G12: a test that exits 0 with a live tmux server: exit 1, stderr names the socket, server and directory gone" \
    '[ "$GRC" -eq 1 ] && printf "%s" "$GERR" | grep -q "leak.sock" && [ ! -e "$(cat "$G/leak-dir")" ] && ! "$PROBE_TMUX" -S "$(cat "$G/leak-dir")/leak.sock" list-sessions >/dev/null 2>&1'
  run_sh child-leak.sh G_MODE=term
  check "G12: the same when the test is sent SIGTERM mid-way: non-zero exit, the socket named, server and directory gone" \
    '[ "$GRC" -ne 0 ] && printf "%s" "$GERR" | grep -q "leak.sock" && [ ! -e "$(cat "$G/leak-dir")" ] && ! "$PROBE_TMUX" -S "$(cat "$G/leak-dir")/leak.sock" list-sessions >/dev/null 2>&1'
  run_sh child-leak.sh G_MODE=tidy
  check "G12: a test that kills its own server: exit 0, no leak reported, directory gone" \
    '[ "$GRC" -eq 0 ] && ! printf "%s" "$GERR" | grep -q "left a tmux server" && [ ! -e "$(cat "$G/leak-dir")" ]'
fi

# ---- TMPDIR is kept short: a value over 40 characters is replaced by a dir the sourcing shell removes at exit
tmp_probe() { # <value|unset>: "<TMPDIR seen after sourcing>|<exists inside the shell>"
  local v=$1 out
  if [ "$v" = unset ]; then
    out=$(cd "$SANDBOX" && env -u TMPDIR bash -c '. "$0"; printf "%s|" "${TMPDIR-unset}"; [ -d "${TMPDIR:-/nonexistent}" ] && echo exists || echo missing' "$PLUGIN/tests/lib-hermetic.sh")
  else
    out=$(cd "$SANDBOX" && TMPDIR="$v" bash -c '. "$0"; printf "%s|" "${TMPDIR-unset}"; [ -d "${TMPDIR:-/nonexistent}" ] && echo exists || echo missing' "$PLUGIN/tests/lib-hermetic.sh")
  fi
  printf '%s' "$out"
}
pad() { printf '/tmp/%s' "$(printf 'q%.0s' $(seq 1 $(($1 - 5))))"; }  # a path of exactly $1 characters
LONG109=$(pad 109)
OUT=$(tmp_probe "$LONG109"); SEEN=${OUT%%|*}
check "T9: a 109-character TMPDIR is replaced by a /tmp/ahprobe.* dir of at most 40 characters that exists in the shell" '[ "${#LONG109}" = 109 ] && [[ "$SEEN" == /tmp/ahprobe.?????? ]] && [ "${#SEEN}" -le 40 ] && [ "${OUT#*|}" = exists ]'
check "T9: ...and it is gone once that shell has exited" '[ -n "$SEEN" ] && [ ! -e "$SEEN" ]'
OUT=$(tmp_probe "$(pad 41)"); SEEN=${OUT%%|*}
check "T9 boundary: 41 characters is replaced" '[[ "$SEEN" == /tmp/ahprobe.?????? ]]'
OUT=$(tmp_probe "$(pad 40)"); SEEN=${OUT%%|*}
check "T10 boundary: 40 characters is left alone" '[ "$SEEN" = "$(pad 40)" ]'
OUT=$(tmp_probe "$(pad 36)"); SEEN=${OUT%%|*}
check "T10: a 36-character TMPDIR (run-all.sh's per-test one) is unchanged" '[ "$SEEN" = "$(pad 36)" ]'
OUT=$(tmp_probe unset)
check "T10: an unset TMPDIR stays unset" '[ "${OUT%%|*}" = unset ]'

# ---- S2: run-all.sh starts every suite from an emptied environment plus an allow-list, with its own PATH
LEAKED="CLAUDE_CODE_MESSAGING_SOCKET CLAUDECODE CLAUDE_CODE_SESSION_ID CLAUDE_PID AH_SPAWN_DEPTH WRANGL_ROSTER WRANGL_EXPECTED_ROOT AH_MEMBER_NAME HERDR_PANE_ID CODEX_HOME CLAUDE_CONFIG_DIR"
EP="$SANDBOX/envprobe"; mkdir -p "$EP/suites" "$EP/planted"
printf '#!/bin/bash\necho "ah" > "%s/ran-ah"\n' "$EP" > "$EP/planted/ah"; chmod 755 "$EP/planted/ah"
printf '#!/bin/sh\nexit 0\n' > "$EP/ahd"; chmod 755 "$EP/ahd"
mkdir -p "$EP/cli"; for _t in claude codex ah ahd; do printf '#!/bin/sh\nexit 0\n' > "$EP/cli/$_t"; chmod 755 "$EP/cli/$_t"; done
printf '#!/bin/bash\nenv > "%s/env.txt"\ncommand -v ah > "%s/ah.txt"\ncommand -v claude > "%s/claude.txt"\ncommand -v codex > "%s/codex.txt"\nprintf "%%s" "$HOME" > "%s/home.txt"\n' "$EP" "$EP" "$EP" "$EP" "$EP" > "$EP/suites/test-envprobe.sh"
printf 'command -v ahd > "%s/ahd.txt"\n' "$EP" >> "$EP/suites/test-envprobe.sh"
ENVLK=$(mktemp -u /private/tmp/ahprobe.XXXXXX)
OUT=$(env CLAUDE_CODE_MESSAGING_SOCKET=/real/sock CLAUDECODE=1 CLAUDE_CODE_SESSION_ID=real CLAUDE_PID=4242 AH_SPAWN_DEPTH=2 WRANGL_ROSTER=real \
  WRANGL_EXPECTED_ROOT=/real AH_MEMBER_NAME=real HERDR_PANE_ID=p1 CODEX_HOME=/real/codex CLAUDE_CONFIG_DIR=/real/claude LANG=en_US.UTF-8 \
  AH_RUN_ALL_LOCK_WAIT=1 AH_RUN_ALL_NOABORT=1 AH_RUN_ALL_LOCK_FAST=1 AH_RUN_ALL_FAILFAST=1 \
  PATH="$EP/planted:$EP/cli:$PATH" AHD_NATIVE_BIN="$EP/ahd" AH_RUN_ALL_TESTS="$EP/suites" AH_RUN_ALL_LOCK="$ENVLK" \
  AH_RUN_ALL_LEAK=off AH_RUN_ALL_SETTLE=0 bash "$PLUGIN/tests/run-all.sh" -j 1 2>&1)
rm -rf "$ENVLK"
check "S2: the probe suite ran under run-all.sh" '[ -s "$EP/env.txt" ] && printf %s "$OUT" | grep -q "1/1 passed"'
check "S2: none of the session variables reaches the suite" '! grep -E "^(CLAUDE_CODE_MESSAGING_SOCKET|CLAUDECODE|CLAUDE_CODE_SESSION_ID|CLAUDE_PID|AH_SPAWN_DEPTH|WRANGL_ROSTER|WRANGL_EXPECTED_ROOT|AH_MEMBER_NAME|HERDR_PANE_ID)=" "$EP/env.txt"'
HOMEDIR=$(dirname "$(cat "$EP/home.txt")")
check "S2: CODEX_HOME and CLAUDE_CONFIG_DIR are set to directories under the suite dir, not inherited" \
  'grep -qx "CODEX_HOME=$HOMEDIR/codex" "$EP/env.txt" && grep -qx "CLAUDE_CONFIG_DIR=$HOMEDIR/claude" "$EP/env.txt"'
check "S2: ah resolves to the checkout and the planted one never ran" '[ "$(cat "$EP/ah.txt")" = "$PLUGIN/bin/ah" ] && [ ! -e "$EP/ran-ah" ]'
check "S2: ah and ahd planted beside claude and codex, the directory put back on PATH, still resolve to the checkout's bin" \
  '[ -x "$EP/cli/ah" ] && [ -x "$EP/cli/ahd" ] && [ "$(cat "$EP/ah.txt")" = "$PLUGIN/bin/ah" ] && [ "$(cat "$EP/ahd.txt")" = "$PLUGIN/bin/ahd" ]'
check "S2: the run-all lock and abort controls (LOCK, LOCK_WAIT, LOCK_FAST, NOABORT, FAILFAST) do not reach the suite, although the run itself used the lock" \
  '! grep -E "^AH_RUN_ALL_(LOCK|LOCK_WAIT|LOCK_FAST|NOABORT|FAILFAST)=" "$EP/env.txt"'
check "S2: ...other AH_RUN_ALL_ variables still do (nearest non-match)" 'grep -qx "AH_RUN_ALL_LEAK=off" "$EP/env.txt" && grep -qx "AH_RUN_ALL_SETTLE=0" "$EP/env.txt"'
OUT=$(env AH_RUN_ALL_LOCK=/x AH_RUN_ALL_LOCK_WAIT=1 AH_RUN_ALL_LOCK_FAST=1 AH_RUN_ALL_NOABORT=1 AH_RUN_ALL_FAILFAST=1 AH_RUN_ALL_SETTLE=0 bash -c '. "$0"; env' "$PLUGIN/tests/lib-hermetic.sh" 2>&1)
check "S2: a direct run drops the run-all lock and abort controls of an outer run" '! printf %s "$OUT" | grep -E "^AH_RUN_ALL_(LOCK|LOCK_WAIT|LOCK_FAST|NOABORT|FAILFAST)="'
check "S2: ...and keeps the other AH_RUN_ALL_ variables (nearest non-match)" 'printf %s "$OUT" | grep -qx "AH_RUN_ALL_SETTLE=0"'
check "S2: the claude and codex CLIs stay reachable on the rebuilt PATH, at the caller's own copies (a suite runs them)" '[ "$(cat "$EP/claude.txt")" = "$EP/cli/claude" ] && [ "$(cat "$EP/codex.txt")" = "$EP/cli/codex" ]'
check "S2: the allow-list still passes what a suite needs (nearest non-match): LANG, AH_TEST, AH_DAEMON, HOME under the run dir" \
  'grep -qx "LANG=en_US.UTF-8" "$EP/env.txt" && grep -qx "AH_TEST=1" "$EP/env.txt" && grep -qx "AH_DAEMON=off" "$EP/env.txt" && grep -qx "AHD_NATIVE_BIN=$EP/ahd" "$EP/env.txt" && grep -q "^AHD_LEAK_RUN=" "$EP/env.txt"'
OUT=$(env CLAUDE_CODE_SESSION_ID=real CLAUDECODE=1 CLAUDE_FOO=1 CODEX_FOO=1 AH_SPAWN_DEPTH=2 WRANGL_ROSTER=real WRANGL_EXPECTED_ROOT=/real AH_MEMBER_NAME=real \
  CODEX_HOME=/real/codex CLAUDE_CONFIG_DIR=/real/claude bash -c '. "$0"; env' "$PLUGIN/tests/lib-hermetic.sh" 2>&1)
check "S2: a direct run drops CLAUDE_*, CLAUDECODE, CODEX_*, the AH_ session variables, and a CODEX_HOME / CLAUDE_CONFIG_DIR outside a sandbox" \
  '! printf %s "$OUT" | grep -E "^(CLAUDE_|CLAUDECODE=|CODEX_|AH_SPAWN_DEPTH=|WRANGL_ROSTER=|WRANGL_EXPECTED_ROOT=|AH_MEMBER_NAME=)"'
OUT=$(env CODEX_HOME=/private/tmp/ahprobe.abcdef/t001/codex CLAUDE_CONFIG_DIR=/private/tmp/ahprobe.abcdef/t001/claude bash -c '. "$0"; env' "$PLUGIN/tests/lib-hermetic.sh" 2>&1)
check "S2: ...but keeps the two a run-all.sh test is given, which sit inside an ahprobe sandbox (nearest non-match)" \
  'printf %s "$OUT" | grep -qx "CODEX_HOME=/private/tmp/ahprobe.abcdef/t001/codex" && printf %s "$OUT" | grep -qx "CLAUDE_CONFIG_DIR=/private/tmp/ahprobe.abcdef/t001/claude"'

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
