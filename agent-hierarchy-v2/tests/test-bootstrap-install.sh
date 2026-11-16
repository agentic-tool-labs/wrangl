#!/bin/bash
# agent-hierarchy — the repo-root install.sh (the one-command bootstrap) against a fake `claude` on PATH and a fake
# plugin root whose scripts/install.sh records its calls. HOME is a temp dir; the real claude and ~/.claude are never
# reached. Skips when install.sh is not beside the plugin folder.
# Usage: bash tests/test-bootstrap-install.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
BOOT="$(dirname "$PLUGIN")/install.sh"
if [ ! -f "$BOOT" ]; then echo "SKIP: no $BOOT (the plugin folder without its repo root)"; exit 0; fi
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name"; fi
}
T=$(mktemp -d "${TMPDIR:-/tmp}/ahprobe.XXXXXX")
[ -n "$T" ] || exit 1
hermetic_on_exit 'rm -rf "$T"'
FAKE="$T/fake"; LOG="$FAKE/log"; ROOT="$T/plugin root"   # the space is deliberate
mkdir -p "$T/bin" "$T/winbin" "$T/home" "$ROOT/scripts"
export AH_TEST_FAKE_BIN="$T/bin"
BASEPATH=/usr/bin:/bin
if PATH=$BASEPATH command -v claude >/dev/null 2>&1; then echo "FAIL: a real claude is on $BASEPATH; the test cannot isolate it"; exit 1; fi

cat >"$T/bin/claude" <<'EOF'
#!/bin/bash
echo "claude $*" >>"$FAKE/log"
case "$*" in
  --version) cat "$FAKE/version";;
  "plugin marketplace list --json") cat "$FAKE/markets.json";;
  "plugin list --json") [ -f "$FAKE/list-fails" ] && [ -f "$FAKE/installed" ] && exit 4; cat "$FAKE/plugins.json";;
  "plugin install "*|"plugin update "*)
    [ -f "$FAKE/install-fails" ] && { echo "install broke" >&2; exit 3; }
    touch "$FAKE/installed"
    [ -f "$FAKE/plugins-after.json" ] && cp "$FAKE/plugins-after.json" "$FAKE/plugins.json";;
esac
exit 0
EOF
cat >"$ROOT/scripts/install.sh" <<'EOF'
echo "installer [$*]" >>"$FAKE/log"
EOF
printf '#!/bin/bash\necho MINGW64_NT-10.0\n' >"$T/winbin/uname"
chmod +x "$T/bin/claude" "$T/winbin/uname"

WRANGL_PLUGIN='{"id": "wrangl@wrangl", "version": "2.0.0", "scope": "user", "enabled": true, "installPath": "'"$ROOT"'"}'
fresh() {   # a machine with neither the marketplace nor the plugin
  rm -rf "$FAKE"; mkdir -p "$FAKE"
  echo "2.1.325 (Claude Code)" >"$FAKE/version"
  printf '[\n  {\n    "name": "wrangl-dev",\n    "source": "github"\n  }\n]\n' >"$FAKE/markets.json"
  printf '[\n  {\n    "id": "brag@brag",\n    "enabled": true\n  }\n]\n' >"$FAKE/plugins.json"
  printf '[\n%s\n]\n' "$WRANGL_PLUGIN" >"$FAKE/plugins-after.json"
}
boot() {   # boot ARGS...: run the bootstrap; no terminal unless TTYF names an answers file; output in $OUT, exit code in $RC
  RC=0
  OUT=$(env HOME="$T/home" PATH="${XPATH:-}$T/bin:$BASEPATH" FAKE="$FAKE" WRANGL_BOOTSTRAP_TTY="${TTYF:-$T/no-tty}" \
    ${CCD:+"CLAUDE_CONFIG_DIR=$CCD"} bash "$BOOT" "$@" </dev/null 2>&1) || RC=$?
}
market() {   # market KIND FIELD VALUE: a configured marketplace named wrangl
  printf '[\n  {\n    "name": "wrangl",\n    "source": "%s",\n    "%s": "%s"\n  }\n]\n' "$1" "$2" "$3" >"$FAKE/markets.json"
}
changes() { grep -E 'marketplace (add|update|remove)|plugin (install|update|disable|enable|uninstall)|^installer' "$LOG" 2>/dev/null; }

# ---- --plan changes nothing
fresh; boot --plan
check "--plan exits 0" '[ "$RC" = 0 ]'
check "--plan shows the add and install commands" 'grep -qF "claude plugin marketplace add agentic-tool-labs/wrangl" <<<"$OUT" && grep -qF "claude plugin install wrangl@wrangl --scope user" <<<"$OUT"'
check "--plan runs nothing that changes state" '[ -z "$(changes)" ]'

# ---- no terminal and no --yes changes nothing
fresh; boot
check "no terminal, no --yes: exits 0" '[ "$RC" = 0 ]'
check "no terminal, no --yes: says to re-run with --yes" 'grep -q -- "--yes" <<<"$OUT" && grep -q "nothing was changed" <<<"$OUT"'
check "no terminal, no --yes: runs nothing that changes state" '[ -z "$(changes)" ]'

# ---- --yes: add, install, installer plan, installer --apply, in that order
fresh; boot --yes
EXPECT="claude plugin marketplace add agentic-tool-labs/wrangl
claude plugin install wrangl@wrangl --scope user
installer []
installer [--apply --auto-rebuild]"
check "--yes exits 0" '[ "$RC" = 0 ]'
check "--yes runs add, install, installer plan, installer --apply in that order" '[ "$(changes)" = "$EXPECT" ]'
check "--yes prints next steps and the early-testing line" 'grep -qF "/wrangl:hierarchy init" <<<"$OUT" && grep -qF "Wrangl is in early testing" <<<"$OUT"'
check "a marketplace whose name only starts with wrangl does not count as added" 'grep -q "marketplace add" "$LOG"'

# ---- --source is the marketplace source; --skip-build installs the plugin only
fresh; boot --yes --source /some/clone --skip-build
check "--source is passed to marketplace add" 'grep -qx "claude plugin marketplace add /some/clone" "$LOG"'
check "--skip-build does not run the plugin installer" '[ "$RC" = 0 ] && ! grep -q "^installer" "$LOG"'
check "--skip-build does not look up the plugin root" '[ "$(grep -c "plugin list" "$LOG")" = 1 ] && ! grep -q "Plugin root" <<<"$OUT"'
check "--skip-build points at /wrangl:hierarchy install, not at ah --version" 'grep -qF "/wrangl:hierarchy install" <<<"$OUT" && ! grep -q "ah --version" <<<"$OUT"'

# ---- an already-added marketplace and an installed plugin take the update path
fresh; market github repo agentic-tool-labs/wrangl
cp "$FAKE/plugins-after.json" "$FAKE/plugins.json"; boot --yes
EXPECT="claude plugin marketplace update wrangl
claude plugin update wrangl@wrangl
installer []
installer [--apply --auto-rebuild]"
check "already added and installed: marketplace update and plugin update, then the installer" '[ "$RC" = 0 ] && [ "$(changes)" = "$EXPECT" ]'
check "the plan names the configured source" 'grep -qF "configured source: github agentic-tool-labs/wrangl" <<<"$OUT"'
check "the official repo as configured source: no source warning" '! grep -q "WARNING: that source" <<<"$OUT"'
check "an installPath with a space is run" 'grep -qF "Plugin root: $ROOT" <<<"$OUT" && grep -q "^installer \[--apply" "$LOG"'

# ---- the configured marketplace source: other GitHub forms of the repo, a foreign source, a differing --source
fresh; market git url https://github.com/Agentic-Tool-Labs/wrangl.git; boot --plan
check "the repo as a git URL counts as the official source" '[ "$RC" = 0 ] && ! grep -q "WARNING: that source" <<<"$OUT"'
fresh; market github repo someone/wrangl-fork; boot --plan
check "a foreign configured source is a warning line in the plan" '[ "$RC" = 0 ] && grep -qF "WARNING: that source is not agentic-tool-labs/wrangl" <<<"$OUT"'
fresh; market github repo agentic-tool-labs/wrangl; boot --yes --source /some/clone
check "a --source that differs from the configured one: exits non-zero, says to remove it, changes nothing" \
  '[ "$RC" != 0 ] && grep -qF "claude plugin marketplace remove wrangl" <<<"$OUT" && [ -z "$(changes)" ]'
fresh; market github repo agentic-tool-labs/wrangl; boot --yes --source https://github.com/agentic-tool-labs/wrangl
check "a --source naming the configured repo in another form is not a conflict" '[ "$RC" = 0 ] && grep -q "marketplace update wrangl" "$LOG"'
mkdir -p "$T/clone"
fresh; market directory path "$T/clone"; boot --plan --source "$T/clone/."
check "a --source path naming the configured directory is not a conflict" '[ "$RC" = 0 ]'

# ---- plugin root: the user-scope record wins; the cache fallback by version; a plain failure with neither
OTHER="$T/other root"; mkdir -p "$OTHER/scripts"; cp "$ROOT/scripts/install.sh" "$OTHER/scripts/install.sh"
fresh; printf '[\n{"id": "wrangl@wrangl", "scope": "project", "installPath": "%s"},\n%s\n]\n' "$OTHER" "$WRANGL_PLUGIN" >"$FAKE/plugins-after.json"
boot --yes
check "two records: the user-scope installPath is the plugin root" '[ "$RC" = 0 ] && grep -qF "Plugin root: $ROOT" <<<"$OUT"'
fresh; printf '[\n{"id": "wrangl@wrangl", "scope": "project", "installPath": "%s"}\n]\n' "$OTHER" >"$FAKE/plugins-after.json"
boot --yes
check "no user-scope record: the first record's installPath is the plugin root" '[ "$RC" = 0 ] && grep -qF "Plugin root: $OTHER" <<<"$OUT"'

CCD="$T/cfg"; C="$CCD/plugins/cache/wrangl/wrangl"
for v in 2.0.0 10.1.0 9.9.9 latest; do mkdir -p "$C/$v/scripts"; cp "$ROOT/scripts/install.sh" "$C/$v/scripts/install.sh"; done
touch -t 202001010000 "$C/10.1.0"
fresh; printf '[]\n' >"$FAKE/plugins-after.json"; boot --yes
check "no installPath: the highest-version cache directory under CLAUDE_CONFIG_DIR, not the newest or a non-version name" \
  '[ "$RC" = 0 ] && grep -qF "Plugin root: $C/10.1.0" <<<"$OUT"'
rm -rf "$C/2.0.0" "$C/10.1.0" "$C/9.9.9"
fresh; printf '[]\n' >"$FAKE/plugins-after.json"; boot --yes
check "only a non-version cache directory: exits non-zero, says so, runs no installer" \
  '[ "$RC" != 0 ] && grep -q "cannot find the installed plugin" <<<"$OUT" && ! grep -q "^installer" "$LOG"'
unset CCD
fresh; printf '[]\n' >"$FAKE/plugins-after.json"; boot --yes
check "no installPath and no cache: exits non-zero, says so, runs no installer" '[ "$RC" != 0 ] && grep -q "cannot find the installed plugin" <<<"$OUT" && ! grep -q "^installer" "$LOG"'

# ---- interactive: answers fed through an answers file in place of the terminal
fresh; printf 'y\ny\n' >"$T/answers"; TTYF="$T/answers" boot
EXPECT="claude plugin marketplace add agentic-tool-labs/wrangl
claude plugin install wrangl@wrangl --scope user
installer []
installer [--apply]"
check "interactive, yes and yes: add, install, plan, --apply (no --auto-rebuild)" '[ "$RC" = 0 ] && [ "$(changes)" = "$EXPECT" ]'
check "interactive: both prompts are written to the terminal" '[ "$(grep -o "\[y/N\]" "$T/answers" | wc -l | tr -d " ")" = 2 ]'
fresh; printf 'n\n' >"$T/answers"; TTYF="$T/answers" boot
check "interactive, no to Proceed: exits 0 and changes nothing" '[ "$RC" = 0 ] && [ -z "$(changes)" ] && grep -q "Nothing was changed" <<<"$OUT"'
fresh; printf 'y\nn\n' >"$T/answers"; TTYF="$T/answers" boot
EXPECT="claude plugin marketplace add agentic-tool-labs/wrangl
claude plugin install wrangl@wrangl --scope user
installer []"
check "interactive, yes then no: stops after the installer plan, says how to build later" \
  '[ "$RC" = 0 ] && [ "$(changes)" = "$EXPECT" ] && grep -q "Stopped before the build" <<<"$OUT"'

# ---- claude missing
fresh; RC=0; OUT=$(env HOME="$T/home" PATH="$BASEPATH" bash "$BOOT" --yes </dev/null 2>&1) || RC=$?
check "claude missing: exits non-zero and says to install Claude Code" '[ "$RC" != 0 ] && grep -q "not on PATH" <<<"$OUT" && grep -q "Install Claude Code" <<<"$OUT"'

# ---- Claude Code version: too old fails, the minimum passes, unparseable warns and continues
fresh; echo "2.1.288 (Claude Code)" >"$FAKE/version"; boot --yes
check "Claude Code 2.1.288: exits non-zero, names the minimum, changes nothing" '[ "$RC" != 0 ] && grep -q "2.1.289" <<<"$OUT" && [ -z "$(changes)" ]'
fresh; echo "2.1.289 (Claude Code)" >"$FAKE/version"; boot --plan
check "Claude Code 2.1.289 (the minimum) passes" '[ "$RC" = 0 ] && ! grep -q "too old" <<<"$OUT"'
fresh; echo "nightly" >"$FAKE/version"; boot --plan
check "an unparseable version warns and continues" '[ "$RC" = 0 ] && grep -q "warning: could not read the Claude Code version" <<<"$OUT"'

# ---- OS: not macOS or Linux is refused
fresh; XPATH="$T/winbin:" boot --yes
check "a Windows uname is refused before anything runs" '[ "$RC" != 0 ] && grep -q "macOS or Linux" <<<"$OUT" && [ -z "$(changes)" ]'

# ---- a failing plugin install stops before the plugin installer and says what was done
fresh; touch "$FAKE/install-fails"; boot --yes
check "failing install: exits non-zero" '[ "$RC" != 0 ]'
check "failing install: the plugin installer never runs" '! grep -q "^installer" "$LOG"'
check "failing install: names the failed command and that the marketplace was added" 'grep -qF "failed (exit 3): claude plugin install wrangl@wrangl" <<<"$OUT" && grep -A1 "Already done" <<<"$OUT" | grep -qF "claude plugin marketplace add agentic-tool-labs/wrangl"'

# ---- a failing plugin list after the install says what was done
fresh; touch "$FAKE/list-fails"; boot --yes
check "failing second plugin list: exits non-zero, names it, lists what was already done" \
  '[ "$RC" != 0 ] && grep -qF "failed (exit 4): claude plugin list --json" <<<"$OUT" && grep -A2 "Already done" <<<"$OUT" | grep -qF "claude plugin install wrangl@wrangl" && ! grep -q "^installer" "$LOG"'

# ---- the older ah@agent-tools plugin: warn when enabled, never disable; quiet when disabled
fresh; printf '[\n  {\n    "id": "ah@agent-tools",\n    "enabled": true\n  }\n]\n' >"$FAKE/plugins.json"; boot --yes
check "older plugin enabled: warns and prints the disable command" 'grep -q "WARNING: the older plugin ah@agent-tools" <<<"$OUT" && grep -qF "claude plugin disable ah@agent-tools" <<<"$OUT"'
check "older plugin enabled: does not disable it" '! grep -q "plugin disable" "$LOG"'
fresh; printf '[\n  {\n    "id": "ah@agent-tools",\n    "enabled": false\n  }\n]\n' >"$FAKE/plugins.json"; boot --plan
check "older plugin installed but disabled: no warning" '! grep -q "WARNING" <<<"$OUT"'

echo "----"
echo "SUMMARY: $PASS passed, $FAIL failed"
[ $FAIL -eq 0 ]
