#!/bin/bash
# agent-hierarchy — scripts/dogfood.sh in a sandbox: a scratch repository, a fake `claude` that records its argv and keeps the
# plugin files Claude Code would, a fake `ah` and `ahd`, and an install.sh stub in the exported plugin. Nothing here touches the
# real home or the real Claude Code install.
# Usage: bash tests/test-dogfood.sh   (exits 0 iff every row passes)
# DOGFOOD_SCRIPT=<file> runs the rows against another copy of the script (to see a row fail on the script before a change).

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
. "$PLUGIN/scripts/lib-version.sh"
SCRIPT="${DOGFOOD_SCRIPT:-$PLUGIN/scripts/dogfood.sh}"

T="$(mktemp -d /tmp/ahprobe.XXXXXX)"
[ -n "$T" ] && [ -d "$T" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$T"'

FAIL=0
ok()  { echo "ok   $1"; }
bad() { echo "FAIL $1"; FAIL=1; }
check() { local name="$1"; shift; if "$@" >/dev/null 2>&1; then ok "$name"; else bad "$name"; fi; }
same() { [ "$1" = "$2" ]; }
has() { printf '%s' "$1" | grep -q -- "$2"; }

export HOME="$T/home"
export AH_HOME="$HOME/.claude/ah2"
export AH_TEST=1
unset AH_SPAWN_DEPTH CLAUDECODE CLAUDE_CODE_SESSION_ID CLAUDE_CONFIG_DIR CLAUDE_BIN
export FAKE_LOG="$T/fake.log"
export FAKE_SYNC_LOG="$T/sync.log"
: > "$FAKE_SYNC_LOG"
CFG="$HOME/.claude"
FAKEBIN="$T/fakebin"
REPO="$T/repo"
WORK="$T/work"
mkdir -p "$HOME" "$FAKEBIN" "$WORK" "$CFG/plugins" "$AH_HOME/bin"
: > "$FAKE_LOG"
export PATH="$FAKEBIN:$PATH"
export AH_TEST_FAKE_BIN="${AH_TEST_FAKE_BIN:+$AH_TEST_FAKE_BIN:}$FAKEBIN"

# --- the fakes -----------------------------------------------------------------------------------------------------------
cat > "$FAKEBIN/claude" <<'EOF'
#!/bin/bash
echo "claude $*" >> "$FAKE_LOG"
[ -n "${FAKE_CLAUDE_FAIL:-}" ] && case "$*" in *"$FAKE_CLAUDE_FAIL"*) echo "fake claude failed" >&2; exit 1 ;; esac
cfg="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
case "$1 $2 $3" in
  "plugin marketplace add") printf '{"wrangl-dogfood":{}}\n' > "$cfg/plugins/known_marketplaces.json" ;;
  "plugin uninstall "*)
    node -e 'const fs=require("fs");const s=JSON.parse(fs.readFileSync(process.argv[1],"utf8"));delete s.pluginConfigs[process.argv[3]];fs.writeFileSync(process.argv[1],JSON.stringify(s));
      const p=JSON.parse(fs.readFileSync(process.argv[2],"utf8"));delete p.plugins[process.argv[3]];fs.writeFileSync(process.argv[2],JSON.stringify(p))' "$cfg/settings.json" "$cfg/plugins/installed_plugins.json" "$3" ;;
  "plugin install wrangl@wrangl-dogfood"|"plugin update wrangl@wrangl-dogfood")
    src="$AH_HOME/dogfood/marketplace/wrangl"
    v=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$src/.claude-plugin/plugin.json" | head -1)
    dest="$cfg/plugins/cache/wrangl-dogfood/wrangl/$v"
    mkdir -p "$dest" && cp -R "$src/." "$dest/"
    node -e 'const fs=require("fs");const p=JSON.parse(fs.readFileSync(process.argv[1],"utf8"));p.plugins["wrangl@wrangl-dogfood"]=[{scope:"user",installPath:process.argv[2],version:process.argv[3]}];fs.writeFileSync(process.argv[1],JSON.stringify(p))' "$cfg/plugins/installed_plugins.json" "$dest" "$v" ;;
esac
exit 0
EOF
cat > "$FAKEBIN/ah" <<'EOF'
#!/bin/sh
if [ "$1 $2 $3" = "roster permissions sync" ]; then
  echo "ah $*" >> "$FAKE_SYNC_LOG"
  [ -n "${FAKE_SYNC_FAIL:-}" ] && { echo "fake sync failed" >&2; exit 1; }
  echo "sync: 0 rules changed"
  exit 0
fi
[ -f "$FAKE_STATUS_DOC" ] || exit 1
cat "$FAKE_STATUS_DOC"
EOF
cat > "$AH_HOME/bin/ahd" <<'EOF'
#!/bin/sh
case "$1" in
  restart)
    echo "ahd restart" >> "$FAKE_LOG"
    if [ -n "${FAKE_AHD_RESTART_FAILS:-}" ]; then exit 1; fi ;;
  start) echo "ahd start" >> "$FAKE_LOG" ;;
  status)
    if [ "${FAKE_AHD_RESTART_FAILS:-}" = "down" ] && ! grep -q '^ahd start$' "$FAKE_LOG"; then
      printf '{"up":false}\n'; exit 0
    fi
    v="${FAKE_AHD_VERSION:-$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$AH_HOME/install.json" | head -1)}"
    printf '{"up":true,"version":"%s"}\n' "$v" ;;
esac
EOF
chmod +x "$FAKEBIN/claude" "$FAKEBIN/ah" "$AH_HOME/bin/ahd"

# --- the scratch repository: the plugin files dogfood.sh reads, with an install.sh stub and an `ah` stub --------------------
G() { git -C "$REPO" "$@"; }
PV="$REPO/agent-hierarchy-v2"
mkdir -p "$PV/.claude-plugin" "$PV/native/src/Ah.Commands/Roster/Read" "$PV/scripts" "$PV/bin" "$PV/docs"
printf '{\n  "name": "wrangl",\n  "version": "2.5.0-pre.1"\n}\n' > "$PV/.claude-plugin/plugin.json"
printf '{"contract":4}\n' > "$PV/native/cutover.json"
printf 'rules v1\n' > "$PV/native/src/Ah.Commands/Roster/Read/PermissionsVerb.cs"
printf 'doc v1\n' > "$PV/docs/lifecycle-permissions.md"
cat > "$PV/scripts/install.sh" <<'EOF'
#!/bin/sh
d=$(cd "$(dirname "$0")/.." && pwd)
v=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$d/.claude-plugin/plugin.json" | head -1)
c=$(sed -n 's/.*"contract"[[:space:]]*:[[:space:]]*\([0-9]*\).*/\1/p' "$d/native/cutover.json" | head -1)
echo "install.sh@$v $*" >> "$FAKE_LOG"
printf '{"version":"%s","contract":%s,"plugin_root":"%s"}\n' "$v" "$c" "$d" > "$AH_HOME/install.json"
if [ -z "${FAKE_NO_LINK:-}" ]; then
  mkdir -p "$HOME/.local/bin" && ln -sfn "$d/bin/ah" "$HOME/.local/bin/ah"
fi
EOF
cp "$FAKEBIN/ah" "$PV/bin/ah"
chmod +x "$PV/scripts/install.sh" "$PV/bin/ah"
G init -q -b main
G config user.email t@example.com
G config user.name t
G add -A
G commit -q -m c1
touch_commit() { printf '%s\n' "$1" >> "$PV/native/marker.txt"; G add -A; G commit -q -m "$1"; }
touch_commit c2
SHA2=$(G rev-parse HEAD)
touch_commit c3
SHA3=$(G rev-parse HEAD)
SHA7_2=$(printf '%s' "$SHA2" | cut -c1-7)

# the install as it is before any dogfood build, with the plugin's options set under the old marketplace
printf '{"version":"2.5.0-pre.1","contract":4}\n' > "$AH_HOME/install.json"
printf '{"enabledPlugins":{"ah2@old-marketplace":true},"pluginConfigs":{"ah2@old-marketplace":{"options":{"status_entry":false,"toast_seconds":5,"a":"x","b":true}}}}\n' > "$CFG/settings.json"
printf '{"version":2,"plugins":{"ah2@old-marketplace":[{"scope":"user","installPath":"/old/cache","version":"2.5.0-pre.1"}]}}\n' > "$CFG/plugins/installed_plugins.json"
export FAKE_STATUS_DOC="$T/status.json"
printf '{"teams":[{"members":[{"name":"imp-1","live":true,"kind":"claude","activity":"idle"},{"name":"gone-1","live":false,"kind":"claude","activity":"working"},{"name":"cod-1","live":true,"kind":"codex","route":"pane","activity":"idle"},{"name":"arch-1","live":true,"kind":"claude","activity":"idle"}]}]}\n' > "$FAKE_STATUS_DOC"

D() { (cd "$WORK" && bash "$SCRIPT" --repo "$REPO" "$@"); }
snapshot() { (cd "$1" && find . -print | sort | while IFS= read -r p; do if [ -f "$p" ]; then echo "$p $(cksum < "$p")"; else echo "$p"; fi; done); }
nclaude() { grep -c '^claude ' "$FAKE_LOG"; }

# --- --plan changes nothing ------------------------------------------------------------------------------------------------
before="$(snapshot "$T/home")|$(G status --porcelain)"
out=$(D --plan "$SHA2" 2>&1); rc=$?
check "plan exits 0" test "$rc" -eq 0
check "plan prints every step" sh -c 'for s in "Export" "Marketplace" "Publish" "Build and place" "Verify"; do printf %s "$1" | grep -q "^== $s" || exit 1; done' _ "$out"
check "plan names the version it would install" has "$out" "2.5.0-pre.1.2"
check "plan leaves HOME byte-identical" same "$before" "$(snapshot "$T/home")|$(G status --porcelain)"
check "plan ran no claude command" test ! -s "$FAKE_LOG"
out=$(D --plan --migrate "$SHA2" 2>&1)
check "plan --migrate lists the uninstall, the install and the restore" sh -c 'printf %s "$1" | grep -q "plugin uninstall ah2@old-marketplace" && printf %s "$1" | grep -q "plugin install wrangl@wrangl-dogfood" && printf %s "$1" | grep -q "restore the options"' _ "$out"
out=$(CLAUDECODE=1 D --plan --migrate "$SHA2" 2>&1); rc=$?
check "plan --migrate under CLAUDECODE still plans (it writes nothing)" test "$rc" -eq 0

# --- before the one-time migration, a plain run refuses ------------------------------------------------------------------
: > "$FAKE_LOG"
out=$(D "$SHA2" 2>&1); rc=$?
check "a plain run before the migration exits 1" test "$rc" -eq 1
check "... names --migrate" has "$out" -- "--migrate"
check "... and runs no claude command" test ! -s "$FAKE_LOG"
check "... and leaves install.json alone" same "$(cat "$AH_HOME/install.json")" '{"version":"2.5.0-pre.1","contract":4}'
check "... and leaves no export behind" test ! -d "$AH_HOME/dogfood/marketplace/wrangl"

# --- --migrate is for a terminal ---------------------------------------------------------------------------------------------
: > "$FAKE_LOG"
settings_before=$(cat "$CFG/settings.json")
out=$(CLAUDECODE=1 D --migrate "$SHA2" 2>&1); rc=$?
check "--migrate under CLAUDECODE exits 1 and says to use a terminal" sh -c '[ "$1" -eq 1 ] && printf %s "$2" | grep -q "from your terminal"' _ "$rc" "$out"
check "... runs no claude command and writes no settings" sh -c '[ ! -s "$1" ] && [ "$(cat "$2")" = "$3" ]' _ "$FAKE_LOG" "$CFG/settings.json" "$settings_before"

# --- a migration that fails before the uninstall says nothing was uninstalled --------------------------------------------------
: > "$FAKE_LOG"
out=$(FAKE_CLAUDE_FAIL="marketplace add" D --migrate "$SHA2" 2>&1); rc=$?
check "a failing marketplace add exits 1" test "$rc" -eq 1
check "... does not claim the plugin is uninstalled" sh -c '! printf %s "$1" | grep -q "is uninstalled"' _ "$out"
check "... ran no uninstall" test -z "$(grep 'uninstall' "$FAKE_LOG")"
check "... and rolled the export back" test ! -d "$AH_HOME/dogfood/marketplace/wrangl"

# --- a migration that fails after the uninstall prints the way back, and a re-run resumes ----------------------------------------
: > "$FAKE_LOG"
out=$(FAKE_CLAUDE_FAIL="plugin install" D --migrate "$SHA2" 2>&1); rc=$?
check "a failing install after the uninstall exits 1" test "$rc" -eq 1
check "... the call sequence stopped at the install" same "$(cat "$FAKE_LOG")" "claude plugin marketplace add $AH_HOME/dogfood/marketplace
claude plugin uninstall ah2@old-marketplace
claude plugin install wrangl@wrangl-dogfood"
check "... names the snapshot file and the command that goes back" sh -c 'printf %s "$1" | grep -q "userconfig\..*\.json" && printf %s "$1" | grep -q "claude plugin install ah2@old-marketplace"' _ "$out"
check "... does not say the install is untouched" sh -c '! printf %s "$1" | grep -q "untouched"' _ "$out"
check "... the options are gone from the settings, as the uninstall left them" same "$(node -e 'const s=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));console.log(Object.keys((s.pluginConfigs||{})).length)' "$CFG/settings.json")" 0

# --- the one-time migration (resumed) --------------------------------------------------------------------------------------------
: > "$FAKE_LOG"
out=$(D --migrate "$SHA2" 2>&1); rc=$?
check "the re-run exits 0" test "$rc" -eq 0
check "the re-run resumes: no marketplace add, no uninstall, only the install and install.sh" same "$(cat "$FAKE_LOG")" "claude plugin install wrangl@wrangl-dogfood
install.sh@2.5.0-pre.1.2 --apply --rebuild-only --no-daemon-restart"
check "the re-run reuses the snapshot" has "$out" "resuming: reusing the earlier snapshot"
check "migrate: install.json names the dogfood version" grep -q '"version":"2.5.0-pre.1.2"' "$AH_HOME/install.json"
check "migrate: builds.jsonl records the version and the full sha" grep -q "\"version\":\"2.5.0-pre.1.2\",\"sha\":\"$SHA2\"" "$AH_HOME/dogfood/builds.jsonl"
check "migrate: the options came back under the new plugin id, all four" same "$(node -e 'const s=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));console.log(Object.keys(s.pluginConfigs["wrangl@wrangl-dogfood"].options).length)' "$CFG/settings.json")" 4
check "migrate: the repo working tree is untouched" same "$(G status --porcelain)|$(grep '"version"' "$PV/.claude-plugin/plugin.json")" '|  "version": "2.5.0-pre.1"'
check "migrate: the export carries the dogfood version and the repo does not" grep -q '"version": "2.5.0-pre.1.2"' "$AH_HOME/dogfood/marketplace/wrangl/.claude-plugin/plugin.json"
check "migrate: the marketplace file names the same version" grep -q '"version":"2.5.0-pre.1.2"' "$AH_HOME/dogfood/marketplace/.claude-plugin/marketplace.json"
check "migrate: the report lists the live claude peers, no codex pane member and no gone member" sh -c 'printf %s "$1" | grep "^reload:" | grep -q "arch-1 imp-1$" && ! printf %s "$1" | grep "^reload:" | grep -q "gone-1\|cod-1"' _ "$out"
check "migrate: the report line names old and new version and the commit" has "$out" "^dogfood 2.5.0-pre.1 -> 2.5.0-pre.1.2 ($SHA7_2):"
check "migrate: the report line says the next client on this version drains the deferred daemon" has "$out" "daemon deferred only until the next client on this version drains it"
check "migrate: the run itself runs the permission sync, once, in the new plugin dir" same "$(cat "$FAKE_SYNC_LOG")" "ah roster permissions sync --cwd $WORK"
check "migrate: after a sync that worked the report says no sync is needed" sh -c 'printf %s "$1" | grep -q "permissions sync no," && ! printf %s "$1" | grep -q "ask the user to run"' _ "$out"
check "migrate: the ah link leads to the new plugin dir" same "$(readlink "$HOME/.local/bin/ah")" "$CFG/plugins/cache/wrangl-dogfood/wrangl/2.5.0-pre.1.2/bin/ah"

# --- a later push: update only, no uninstall -----------------------------------------------------------------------------
: > "$FAKE_LOG"
out=$(D "$SHA3" 2>&1); rc=$?
check "a later build exits 0" test "$rc" -eq 0
check "a later build: marketplace update, plugin update, install.sh; no uninstall, no install" same "$(cat "$FAKE_LOG")" "claude plugin marketplace update wrangl-dogfood
claude plugin update wrangl@wrangl-dogfood
install.sh@2.5.0-pre.1.3 --apply --rebuild-only --no-daemon-restart"
check "a later build's version sorts above the earlier one" test "$(ah_version_cmp 2.5.0-pre.1.2 2.5.0-pre.1.3)" = -1
check "builds.jsonl holds both builds, in order" same "$(sed -n 's/.*"version":"\([^"]*\)".*/\1/p' "$AH_HOME/dogfood/builds.jsonl" | tr '\n' ' ')" "2.5.0-pre.1.2 2.5.0-pre.1.3 "
check "the previous export is kept as wrangl.prev" grep -q '"version": "2.5.0-pre.1.2"' "$AH_HOME/dogfood/marketplace/wrangl.prev/.claude-plugin/plugin.json"
check "permission sources unchanged between the builds: sync is not needed" has "$out" "permissions sync no,"
check "... and no ask to run the sync" sh -c '! printf %s "$1" | grep -q "ah roster permissions sync"' _ "$out"

# --- the line: the same commit, an ancestor, a sibling -------------------------------------------------------------------------
: > "$FAKE_LOG"
stamp_before=$(cat "$AH_HOME/install.json")
out=$(D "$SHA3" 2>&1); rc=$?
check "the same commit again exits 0 and says unchanged" sh -c '[ "$1" -eq 0 ] && printf %s "$2" | grep -q "^unchanged:"' _ "$rc" "$out"
check "... with no claude command and no install.sh" test ! -s "$FAKE_LOG"
out=$(D "$SHA2" 2>&1); rc=$?
check "an ancestor of the last build exits 0 and says older than installed" sh -c '[ "$1" -eq 0 ] && printf %s "$2" | grep -q "unchanged (older than installed)"' _ "$rc" "$out"
check "... with no claude command, and install.json untouched (never a downgrade)" sh -c '[ ! -s "$1" ] && [ "$(cat "$2")" = "$3" ]' _ "$FAKE_LOG" "$AH_HOME/install.json" "$stamp_before"
G checkout -q -b side "$SHA2"
touch_commit s1
SIDE=$(G rev-parse HEAD)
G checkout -q main
market_before=$(snapshot "$AH_HOME/dogfood/marketplace")
out=$(D "$SIDE" 2>&1); rc=$?
check "a sibling branch commit (the same count as the last build) exits 1" test "$rc" -eq 1
check "... names both commits" sh -c 'printf %s "$1" | grep -q "$2" && printf %s "$1" | grep -q "$3"' _ "$out" "$SIDE" "$SHA3"
check "... runs no claude command" test ! -s "$FAKE_LOG"
check "... and changes nothing: install.json and the marketplace are as they were" sh -c '[ "$(cat "$1")" = "$2" ] && [ "$3" = "$4" ]' _ "$AH_HOME/install.json" "$stamp_before" "$market_before" "$(snapshot "$AH_HOME/dogfood/marketplace")"
G checkout -q -b side2 "$SHA2"
touch_commit s2
touch_commit s3
touch_commit s4
SIDE2=$(G rev-parse HEAD)
G checkout -q main
out=$(D "$SIDE2" 2>&1); rc=$?
check "a sibling with a higher count than the last build is refused too" test "$rc" -eq 1
# a last build the repository no longer has
cp "$AH_HOME/dogfood/builds.jsonl" "$T/builds.keep"
printf '{"version":"2.5.0-pre.1.9","sha":"0123456789012345678901234567890123456789","exported_at":"x","contract":4}\n' >> "$AH_HOME/dogfood/builds.jsonl"
out=$(D "$SHA3" 2>&1); rc=$?
check "a last build the repository does not have is refused, naming it" sh -c '[ "$1" -eq 1 ] && printf %s "$2" | grep -q "0123456789012345678901234567890123456789"' _ "$rc" "$out"
cp "$T/builds.keep" "$AH_HOME/dogfood/builds.jsonl"

# --- a contract change stops before any claude command ------------------------------------------------------------------
printf '{"contract":5}\n' > "$PV/native/cutover.json"
touch_commit c4
SHA4=$(G rev-parse HEAD)
: > "$FAKE_LOG"
stamp_before=$(cat "$AH_HOME/install.json")
out=$(D "$SHA4" 2>&1); rc=$?
check "a contract change exits 1" test "$rc" -eq 1
check "... says which contracts differ" has "$out" "contract 4 -> 5"
check "... runs no claude command" test ! -s "$FAKE_LOG"
check "... and leaves install.json alone" same "$stamp_before" "$(cat "$AH_HOME/install.json")"
check "... and the export dir still holds the previous build" grep -q '"version": "2.5.0-pre.1.3"' "$AH_HOME/dogfood/marketplace/wrangl/.claude-plugin/plugin.json"
printf '{"contract":4}\n' > "$PV/native/cutover.json"
printf 'doc v2\n' > "$PV/docs/lifecycle-permissions.md"
touch_commit c5
SHA5=$(G rev-parse HEAD)

# --- a failing claude command stops the build and puts the previous export back --------------------------------------------------
: > "$FAKE_LOG"
stamp_before=$(cat "$AH_HOME/install.json")
out=$(FAKE_CLAUDE_FAIL="plugin update" D "$SHA5" 2>&1); rc=$?
check "a failing plugin update exits 1" test "$rc" -eq 1
check "... stops there: no install.sh ran" test -z "$(grep '^install.sh' "$FAKE_LOG")"
check "... and install.json is untouched" same "$stamp_before" "$(cat "$AH_HOME/install.json")"
check "... and the failure names the command" has "$out" "claude plugin update wrangl@wrangl-dogfood exited 1"
check "... and the export is rolled back to the previous build" grep -q '"version": "2.5.0-pre.1.3"' "$AH_HOME/dogfood/marketplace/wrangl/.claude-plugin/plugin.json"
check "... and so is the marketplace file" grep -q '"version":"2.5.0-pre.1.3"' "$AH_HOME/dogfood/marketplace/.claude-plugin/marketplace.json"
check "... and builds.jsonl has no entry for the failed build" same "$(grep -c '' "$AH_HOME/dogfood/builds.jsonl")" 2
: > "$FAKE_LOG"
out=$(D "$SHA5" 2>&1); rc=$?
check "the same build without the failure then goes through (nearest non-match)" sh -c '[ "$1" -eq 0 ] && grep -q "^install.sh@2.5.0-pre.1.5 " "$2"' _ "$rc" "$FAKE_LOG"
check "a change in a permission-rule source says the sync is needed" sh -c 'printf %s "$1" | grep -q "permissions sync needed," && printf %s "$1" | grep -q "ah roster permissions sync"' _ "$out"

# --- the peers line when the status cannot be read ----------------------------------------------------------------------
touch_commit c6
SHA6=$(G rev-parse HEAD)
out=$(FAKE_STATUS_DOC="$T/missing.json" PATH="$FAKEBIN:$PATH" D "$SHA6" 2>&1)
check "an unreadable status says the peers could not be listed" has "$out" "^reload: the live peers could not be listed"
printf '{"teams":[]}\n' > "$FAKE_STATUS_DOC"
touch_commit c7
out=$(D "$(G rev-parse HEAD)" 2>&1)
check "no live peers says so" has "$out" "^reload: no live peers"

# --- permissions: a ledger rule that does not spell this build's command path ------------------------------------------------------
ledger_with() { printf '{"v":1,"added":["%s"],"plugin_root":"x","answer":"yes"}\n' "$1" > "$AH_HOME/permissions.json"; }
touch_commit c8
ledger_with "Bash(/old/cache/wrangl/2.5.0-pre.1.1/bin/ah roster dismiss:*)"
out=$(D "$(G rev-parse HEAD)" 2>&1)
check "a ledger rule on another path, source unchanged: the sync is needed" has "$out" "permissions sync needed,"
touch_commit c9
ledger_with "Bash($HOME/.local/bin/ah roster dismiss:*)"
out=$(D "$(G rev-parse HEAD)" 2>&1)
check "a ledger rule on the stable path with the link valid, source unchanged: the sync is not needed (nearest non-match)" has "$out" "permissions sync no,"
touch_commit c10
ledger_with "Bash($CFG/plugins/cache/wrangl-dogfood/wrangl/2.5.0-pre.1.$(G rev-list --count HEAD)/bin/ah roster dismiss:*)"
out=$(D "$(G rev-parse HEAD)" 2>&1)
check "a ledger rule in the plugin-root form while the link is valid: the sync is needed" has "$out" "permissions sync needed,"
touch_commit c11
ledger_with "Bash(/elsewhere$HOME/.local/bin/ah roster dismiss:*)"
out=$(D "$(G rev-parse HEAD)" 2>&1)
check "a rule that only contains the stable path is still stale" has "$out" "permissions sync needed,"
touch_commit c12
ledger_with "Bash($HOME/.local/bin/ah roster dismiss:*)"
rm -f "$HOME/.local/bin/ah"
out=$(FAKE_NO_LINK=1 D "$(G rev-parse HEAD)" 2>&1)
check "no ah link after the install: the sync is needed, with the reason" sh -c 'printf %s "$1" | grep -q "permissions sync needed," && printf %s "$1" | grep -q "is not a link to this build"' _ "$out"
touch_commit c13
ln -sfn /tmp/fake/bin/ah "$HOME/.local/bin/ah"
out=$(FAKE_NO_LINK=1 D "$(G rev-parse HEAD)" 2>&1)
check "a link to a foreign path: the sync is needed, with the reason" sh -c 'printf %s "$1" | grep -q "permissions sync needed," && printf %s "$1" | grep -q "leads to /tmp/fake/bin/ah"' _ "$out"
touch_commit c14
rm -f "$HOME/.local/bin/ah"
printf 'x\n' > "$HOME/.local/bin/ah"
out=$(FAKE_NO_LINK=1 D "$(G rev-parse HEAD)" 2>&1)
check "a regular file where the link goes: the sync is needed, and the file is as it was" sh -c 'printf %s "$1" | grep -q "permissions sync needed," && [ "$(cat "$2")" = x ]' _ "$out" "$HOME/.local/bin/ah"
rm -f "$HOME/.local/bin/ah"
# a chain of links is followed to its end, as the daemon follows it
touch_commit c15
V=2.5.0-pre.1.$(G rev-list --count HEAD)
ln -sfn "$CFG/plugins/cache/wrangl-dogfood/wrangl/$V/bin/ah" "$HOME/.local/bin/ah-mid"
ln -sfn "$HOME/.local/bin/ah-mid" "$HOME/.local/bin/ah"
out=$(FAKE_NO_LINK=1 D "$(G rev-parse HEAD)" 2>&1)
check "a chained link that ends at this build counts as the stable path: the sync is not needed" has "$out" "permissions sync no,"
touch_commit c16
ln -sfn /tmp/fake/bin/ah "$HOME/.local/bin/ah-mid"
out=$(FAKE_NO_LINK=1 D "$(G rev-parse HEAD)" 2>&1)
check "a chained link that ends elsewhere: the sync is needed, naming where it ends" sh -c 'printf %s "$1" | grep -q "permissions sync needed," && printf %s "$1" | grep -q "leads to /tmp/fake/bin/ah"' _ "$out"
rm -f "$HOME/.local/bin/ah" "$HOME/.local/bin/ah-mid"
check "a rule that only contains this build's path is still stale" has "$out" "permissions sync needed,"
rm -f "$AH_HOME/permissions.json"

# --- usage errors ---------------------------------------------------------------------------------------------------------
out=$(D 2>&1); rc=$?
check "no commit exits 2" test "$rc" -eq 2
out=$(D no-such-ref 2>&1); rc=$?
check "an unknown commit exits 1 and names it" sh -c '[ "$1" -eq 1 ] && printf %s "$2" | grep -q "no-such-ref is not a commit"' _ "$rc" "$out"
out=$(D --swap "$SHA2" 2>&1); rc=$?
check "--swap with a commit exits 2" test "$rc" -eq 2

# --- --swap: the daemon, at a quiet point --------------------------------------------------------------------------------------
export AH_DOGFOOD_WAIT_SECONDS=0 AH_DOGFOOD_POLL_SECONDS=0 AH_DOGFOOD_VERIFY_SECONDS=0
installed=$(sed -n 's/.*"version":"\([^"]*\)".*/\1/p' "$AH_HOME/install.json")
set_status() { printf '{"teams":[{"members":[%s]}]}\n' "$1" > "$FAKE_STATUS_DOC"; }
S() { (cd "$WORK" && bash "$SCRIPT" --swap "$@"); }
restarts() { grep -c '^ahd restart$' "$FAKE_LOG"; }

: > "$FAKE_LOG"
set_status '{"name":"imp-1","live":true,"kind":"claude","activity":"working"}'
out=$(S 2>&1); rc=$?
check "swap: a working member defers the swap (exit 3)" test "$rc" -eq 3
check "... names the member and says deferred" sh -c 'printf %s "$1" | grep -q "deferred" && printf %s "$1" | grep -q "imp-1:working"' _ "$out"
check "... and never restarts the daemon" test "$(restarts)" -eq 0
set_status '{"name":"imp-1","live":true,"kind":"claude","activity":"idle"},{"name":"rev-1","live":true,"kind":"claude","activity":"blocked"}'
out=$(S 2>&1); rc=$?
check "swap: a blocked member (a pending question) defers it too" sh -c '[ "$1" -eq 3 ] && printf %s "$2" | grep -q "rev-1:blocked"' _ "$rc" "$out"
check "... still no restart" test "$(restarts)" -eq 0
printf 'not json\n' > "$FAKE_STATUS_DOC"
out=$(S 2>&1); rc=$?
check "swap: an unreadable status defers it, never forces it" sh -c '[ "$1" -eq 3 ] && printf %s "$2" | grep -q "could not be read"' _ "$rc" "$out"
check "... still no restart" test "$(restarts)" -eq 0
set_status '{"name":"imp-1","live":true,"kind":"claude","activity":"idle"},{"name":"gone-1","live":false,"kind":"claude","activity":"working"}'
mkdir -p "$AH_HOME/logs"
out=$(S 2>&1); rc=$?
check "swap: idle members and a gone one 'working' are quiet: the daemon is restarted (nearest non-match)" sh -c '[ "$1" -eq 0 ] && [ "$2" -eq 1 ]' _ "$rc" "$(restarts)"
check "... reports the daemon version and no errors" has "$out" "dogfood daemon: swapped to $installed, post-swap errors 0"
out=$(S --plan 2>&1)
check "swap --plan restarts nothing" test "$(restarts)" -eq 1
for bad in AH_DOGFOOD_WAIT_SECONDS AH_DOGFOOD_POLL_SECONDS AH_DOGFOOD_VERIFY_SECONDS; do
  for value in abc -1 1.5 5s; do
    out=$(env "$bad=$value" bash -c 'cd "$1" && bash "$2" --swap' _ "$WORK" "$SCRIPT" 2>&1); rc=$?
    check "swap: $bad='$value' exits 2 and names the variable, with no restart" sh -c '[ "$1" -eq 2 ] && printf %s "$2" | grep -q "$3" && [ "$4" -eq 1 ]' _ "$rc" "$out" "$bad" "$(restarts)"
  done
done

out=$(FAKE_AHD_RESTART_FAILS=down S 2>&1); rc=$?
check "swap: a restart that fails on a slow exit, then the daemon up on the installed version, is a swap" sh -c '[ "$1" -eq 0 ] && printf %s "$2" | grep -q "swapped to" && grep -q "^ahd start$" "$3"' _ "$rc" "$out" "$FAKE_LOG"
mkdir -p "$T/instant-sleep"
printf '#!/bin/sh\nexit 0\n' > "$T/instant-sleep/sleep"
chmod +x "$T/instant-sleep/sleep"
out=$(PATH="$T/instant-sleep:$PATH" FAKE_AHD_RESTART_FAILS=up FAKE_AHD_VERSION=0.0.0-old S 2>&1); rc=$?
check "swap: a failed restart with the old version still up is a failure (nearest non-match)" sh -c '[ "$1" -eq 1 ] && printf %s "$2" | grep -q "restart failed and the daemon reports version 0.0.0-old"' _ "$rc" "$out"

# rejections since the swap: only bad_request rows at or after the swap time count, in both the log and its rotated file
FUT='2999-01-01T00:00:00.000Z'
PAST='2000-01-01T00:00:00.000Z'
printf '{"ts":"%s","code":"bad_request","client_version":"old","caller":"c0","path":"/p0","field":"f0"}\n{"ts":"%s","code":"bad_request","client_version":"2.5.0-pre.1.2","caller":"orchestrator","path":"/msg/new","field":"to"}\n{"ts":"%s","code":"not_found","client_version":"x","caller":"c2","path":"/p2","field":null}\nnot json\n' "$PAST" "$FUT" "$FUT" > "$AH_HOME/logs/rejected.jsonl"
printf '{"ts":"%s","code":"bad_request","client_version":"2.5.0-pre.1.1","caller":"member","path":"/roster/x","field":"name"}\n' "$FUT" > "$AH_HOME/logs/rejected.jsonl.1"
out=$(S 2>&1); rc=$?
check "swap: two bad_request rows since the swap are counted (log and rotated file), the old one and the other code are not" has "$out" "post-swap errors 2, probe 0$"
check "... each row is quoted with client_version, caller, path and field" sh -c 'printf %s "$1" | grep -q "client_version=2.5.0-pre.1.2 caller=orchestrator path=/msg/new field=to" && printf %s "$1" | grep -q "client_version=2.5.0-pre.1.1 caller=member path=/roster/x field=name"' _ "$out"
check "... and the old row is not quoted" sh -c '! printf %s "$1" | grep -q "client_version=old"' _ "$out"
check "... and it asks for a tracked issue" has "$out" "tracked issue"

# the route probe `ah msg due` sends is answered 400 by design: it is reported as a probe count, never as an error
printf '{"ts":"%s","method":"GET","path":"/v1/due","code":"bad_request","message":"session must be a non-empty string","client_version":"2.5.0-pre.1","caller":null,"field":null}\n{"ts":"%s","method":"GET","path":"/v1/due","code":"bad_request","message":"session must be a non-empty string","client_version":"2.5.0-pre.1","caller":null,"field":null}\n' "$FUT" "$FUT" > "$AH_HOME/logs/rejected.jsonl"
rm -f "$AH_HOME/logs/rejected.jsonl.1"
out=$(S 2>&1)
check "swap: the route probe rows are counted as probe, not as errors" has "$out" "post-swap errors 0, probe 2$"
check "... and they are not quoted and raise no issue" sh -c '! printf %s "$1" | grep -q "bad_request client_version" && ! printf %s "$1" | grep -q "tracked issue"' _ "$out"
printf '{"ts":"%s","method":"GET","path":"/v1/due","code":"bad_request","message":"session must be a non-empty string","client_version":"2.5.0-pre.1","caller":null,"field":null}\n{"ts":"%s","method":"GET","path":"/v1/due","code":"bad_request","message":"poll_ms must be a number","client_version":"2.5.0-pre.1","caller":"mod","field":"poll_ms"}\n{"ts":"%s","method":"POST","path":"/v1/due","code":"bad_request","message":"session must be a non-empty string","client_version":"2.5.0-pre.1","caller":"x","field":null}\n' "$FUT" "$FUT" "$FUT" > "$AH_HOME/logs/rejected.jsonl"
out=$(S 2>&1)
check "swap: another /v1/due bad_request (another message) and the probe text on another method are still reported" has "$out" "post-swap errors 2, probe 1$"
check "... with their fields quoted" sh -c 'printf %s "$1" | grep -q "caller=mod path=/v1/due field=poll_ms" && printf %s "$1" | grep -q "caller=x path=/v1/due"' _ "$out"
rm -f "$AH_HOME/logs/rejected.jsonl" "$AH_HOME/logs/rejected.jsonl.1"
out=$(FAKE_AHD_VERSION=0.0.1 S 2>&1); rc=$?
check "swap: a daemon that comes back at another version fails the run" sh -c '[ "$1" -eq 1 ] && printf %s "$2" | grep -q "reports version 0.0.1"' _ "$rc" "$out"
mv "$AH_HOME/bin/ahd" "$AH_HOME/bin/ahd.keep"
out=$(S 2>&1); rc=$?
check "swap: no daemon binary fails the run" test "$rc" -eq 1
mv "$AH_HOME/bin/ahd.keep" "$AH_HOME/bin/ahd"

# --- --migrate when the permission sync fails: the failure is reported and success is not claimed ---------------------------------
touch_commit m1
rm -f "$AH_HOME"/dogfood/userconfig.*.json
printf '{"enabledPlugins":{"ah2@old-marketplace":true},"pluginConfigs":{"ah2@old-marketplace":{"options":{"a":"x"}}}}\n' > "$CFG/settings.json"
printf '{"version":2,"plugins":{"ah2@old-marketplace":[{"scope":"user","installPath":"/old/cache","version":"2.5.0-pre.1"}]}}\n' > "$CFG/plugins/installed_plugins.json"
: > "$FAKE_SYNC_LOG"
out=$(FAKE_SYNC_FAIL=1 D --migrate "$(G rev-parse HEAD)" 2>&1); rc=$?
check "a migrate whose sync fails still places the build (exit 0)" test "$rc" -eq 0
check "... tried the sync once" same "$(cat "$FAKE_SYNC_LOG")" "ah roster permissions sync --cwd $WORK"
check "... names the failure" has "$out" "permissions sync failed: fake sync failed"
check "... reports the sync as needed, not done" sh -c 'printf %s "$1" | grep -q "permissions sync needed," && ! printf %s "$1" | grep -q "permissions sync no,"' _ "$out"
check "... and asks the user to run it" has "$out" "ask the user to run"

echo "---"
[ "$FAIL" -eq 0 ] && echo "all passed" || echo "some failed"
exit "$FAIL"
