#!/bin/bash
# ah2 keeps its native machine-wide names unchanged (launchd and systemd login items, the `ahd` PATH link, the rc-file
# markers), so v1 (the sibling `agent-hierarchy/` directory of this repo) must create none of them, or the two plugins
# would fight over one name. This scans v1's code (everything except docs/ and tests/, which only describe) for each
# kind of name, and scans ah2's own sources as the control that proves the scan can see them.
# The scan reads the files git tracks under v1: that is what ships, and it keeps a stray untracked build directory (a
# native publish can be a gigabyte of binaries and generated docs that name launchctl) from being scanned or reported.
# Usage: bash tests/test-v1-no-global-names.sh   (exits 0 iff v1 creates no machine-wide name)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
V1="$(cd "$PLUGIN/../agent-hierarchy" 2>/dev/null && pwd)"
PASS=0; FAIL=0

check() {
  local name="$1"; shift
  if "$@"; then echo "PASS: $name"; PASS=$((PASS + 1)); else echo "FAIL: $name"; FAIL=$((FAIL + 1)); fi
}

# kind -> extended regex naming that kind of machine-wide name
re_launchd='io\.github\.agent-tools|LaunchAgents|launchctl'
re_systemd='ahd-login\.service|systemctl|systemd/user'
re_pathlink='\.local/bin|ln -s'
re_rcmarker='# >>> ah >>>|# <<< ah <<<|\.zshrc|\.bashrc|\.zprofile|\.bash_profile'

# hits <kind-regex> <file...>: the files among the arguments that match
hits() {
  local re="$1"; shift
  grep -lE -- "$re" "$@" 2>/dev/null
}

# code_files <dir>: absolute paths of the code files of a plugin tree, not documentation or tests. A git work tree is listed
# by what it tracks; a tree git does not know (an installed copy, or one whose tracked list is empty) by what is on disk.
code_files() {
  local d="$1" list
  list=$(git -C "$d" ls-files 2>/dev/null)
  [ -n "$list" ] || list=$(cd "$d" && find . -type f -not -path './.git/*' 2>/dev/null | sed 's|^\./||')
  printf '%s\n' "$list" | grep -vE '(^|/)(docs|tests|node_modules)/' | sed "s|^|$d/|"
}

# matching <file-list> <kind-regex>: the listed files (one per line) that match
matching() { printf '%s\n' "$1" | tr '\n' '\0' | xargs -0 grep -lE -- "$2" 2>/dev/null; }

some() { [ -n "$(hits "$1" "${@:2}")" ]; }

if [ -z "$V1" ] || [ ! -f "$V1/.claude-plugin/plugin.json" ]; then
  # Wrangl ships from its own repository: v1 is beside it only in a checkout that holds both plugins
  echo "SKIP: no v1 tree next to $PLUGIN (<repo>/agent-hierarchy); nothing to scan"
  echo "----"; echo "SUMMARY: 0 passed, 0 failed"; exit 0
fi

V1_FILES=$(code_files "$V1")
none() { [ -z "$(matching "$V1_FILES" "$1")" ]; }

check "v1 is the plugin named ah (the scan is looking at the right tree)" \
  node -e 'process.exit(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).name==="ah"?0:1)' "$V1/.claude-plugin/plugin.json"
check "v1 has code files to scan" test -n "$V1_FILES"

check "v1 creates no launchd job (no io.github.agent-tools label, LaunchAgents path or launchctl call)" none "$re_launchd"
check "v1 creates no systemd unit (no ahd-login.service, systemctl call or systemd/user path)" none "$re_systemd"
check "v1 creates no PATH link (no ~/.local/bin path, no ln -s)" none "$re_pathlink"
check "v1 creates no rc marker (no ah block markers, no shell rc file path)" none "$re_rcmarker"

# control: the same patterns hit ah2's own sources, so a v1 file that grew one would be seen
LOGIN="$PLUGIN/native/src/Ah.Cli/Login/LoginItem.cs"
INSTALL="$PLUGIN/scripts/install.sh"
check "control: the launchd pattern matches ah2's login item" some "$re_launchd" "$LOGIN"
check "control: the systemd pattern matches ah2's login item" some "$re_systemd" "$LOGIN"
check "control: the PATH-link pattern matches ah2's installer" some "$re_pathlink" "$INSTALL"
check "control: the rc-marker pattern matches ah2's installer" some "$re_rcmarker" "$INSTALL"

# nearest non-match: a v1-shaped file that names none of them is not reported
D=$(mktemp -d "${TMPDIR:-/tmp}/ah-v1names.XXXXXX") || { echo "FAIL: mktemp"; exit 1; }
printf 'const dir = "~/.claude/ah2/bin"; // a path that is not a PATH link\n' > "$D/clean.mjs"
printf 'fs.symlinkSync(target, "%s/.local/bin/tool")\n' '~' > "$D/dirty.mjs"
check "control: a file naming ~/.local/bin is reported" some "$re_pathlink" "$D/dirty.mjs"
check "control: a file naming no machine-wide name is not reported" test -z "$(hits "$re_pathlink|$re_launchd|$re_systemd|$re_rcmarker" "$D/clean.mjs")"
rm -f "$D/clean.mjs" "$D/dirty.mjs"; rmdir "$D"

# which files are scanned: a git tree by what it tracks, code only; a tree git does not know by what is on disk.
# The scratch trees live under the hermetic dir, which the library removes on exit.
R="$_AH_HERMETIC_DIR/v1shaped"
N="$_AH_HERMETIC_DIR/not-a-repo"
mkdir -p "$R/lib" "$R/docs" "$R/native/artifacts/bin" "$N/lib" "$N/docs"
printf 'export const ok = 1\n' > "$R/lib/clean.mjs"
printf 'run("launchctl", ["load", p])\n' > "$R/lib/login.mjs"
printf 'the login item calls launchctl\n' > "$R/docs/note.md"
printf '<doc>launchctl</doc>\n' > "$R/native/artifacts/bin/Ah.Cli.xml"
printf 'run("launchctl", ["load", p])\n' > "$N/lib/login.mjs"
printf 'the login item calls launchctl\n' > "$N/docs/note.md"
git -C "$R" init -q
git -C "$R" add -- lib/clean.mjs docs/note.md
R_FILES=$(code_files "$R")
check "scan: a tracked clean file is listed" test -n "$(printf '%s\n' "$R_FILES" | grep -F "$R/lib/clean.mjs")"
check "scan: a tracked docs file naming launchctl is not listed" test -z "$(printf '%s\n' "$R_FILES" | grep -F "$R/docs/note.md")"
check "scan: an untracked build output naming launchctl is not listed" test -z "$(printf '%s\n' "$R_FILES" | grep -F "$R/native/")"
check "scan: the tracked tree with only that untracked hit reports no launchd name" test -z "$(matching "$R_FILES" "$re_launchd")"
git -C "$R" add -- lib/login.mjs
check "scan: a tracked code file naming launchctl is reported" \
  test -n "$(matching "$(code_files "$R")" "$re_launchd" | grep -F "$R/lib/login.mjs")"
N_FILES=$(code_files "$N")
check "scan: a tree git does not know is read from disk, code only" \
  test "$(printf '%s\n' "$N_FILES" | grep -c .)" = 1 -a -n "$(matching "$N_FILES" "$re_launchd")"

echo "----"
echo "SUMMARY: $PASS passed, $FAIL failed"
[ $FAIL -eq 0 ]
