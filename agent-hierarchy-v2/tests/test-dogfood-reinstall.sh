#!/bin/bash
# agent-hierarchy — scripts/dogfood-reinstall.sh against a fake `claude` on PATH and a stub install.sh: the steps run in order with the
# marketplace read from the installed plugin record, the first failing step stops the rest, and --dry-run runs nothing.
# Usage: bash tests/test-dogfood-reinstall.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$PLUGIN/scripts/dogfood-reinstall.sh"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (RC=$RC OUT=${OUT:0:500} CALLS=$(tr '\n' '|' < "$SB/calls" 2>/dev/null))"; fi; }

mkdir -p "$SB/bin" "$SB/home/.claude/plugins" "$SB/cache/wrangl/9.9.9/scripts"
export AH_TEST_FAKE_BIN="$SB/bin${AH_TEST_FAKE_BIN:+:$AH_TEST_FAKE_BIN}"
cat > "$SB/bin/claude" <<'FAKE'
#!/bin/bash
echo "$*" >> "$CALLS"
[ -n "${FAKE_FAIL:-}" ] && case " $* " in *" $FAKE_FAIL "*) exit 1;; esac
exit 0
FAKE
cat > "$SB/cache/wrangl/9.9.9/scripts/install.sh" <<'STUB'
#!/bin/bash
echo "install.sh $*" >> "$CALLS"
[ "${FAKE_FAIL:-}" = install.sh ] && exit 1
exit 0
STUB
chmod +x "$SB/bin/claude" "$SB/cache/wrangl/9.9.9/scripts/install.sh"

record() {
  printf '{\n  "version": 2,\n  "plugins": {\n    "wrangl@%s": [\n      {\n        "scope": "user",\n        "installPath": "%s",\n        "version": "9.9.9"\n      }\n    ]\n  }\n}\n' "$1" "$SB/cache/wrangl/9.9.9" > "$SB/home/.claude/plugins/installed_plugins.json"
}
# run <fail-word> [args]: the script with the fake claude first on PATH; CALLS gets every claude and install.sh invocation
run() {
  local fail="$1"; shift
  : > "$SB/calls"
  OUT=$(env -u CLAUDE_BIN HOME="$SB/home" PATH="$SB/bin:$PATH" CALLS="$SB/calls" FAKE_FAIL="$fail" bash "$SCRIPT" "$@" 2>&1); RC=$?
}
calls() { tr '\n' '|' < "$SB/calls"; }

record old-marketplace
run ""
check "the steps run in order with the marketplace from the installed record, and the run ends by naming /reload-plugins (exit 0)" \
  '[ $RC = 0 ] && [ "$(calls)" = "plugin marketplace update old-marketplace|plugin uninstall wrangl@old-marketplace|plugin install wrangl@old-marketplace|install.sh --apply --auto-rebuild|" ] && echo "$OUT" | grep -q "/reload-plugins"'

run update
check "a failing marketplace update stops before the uninstall (exit 1)" '[ $RC = 1 ] && [ "$(calls)" = "plugin marketplace update old-marketplace|" ] && echo "$OUT" | grep -q "step 1 failed"'
run uninstall
check "a failing uninstall stops before the install (exit 1)" '[ $RC = 1 ] && [ "$(calls)" = "plugin marketplace update old-marketplace|plugin uninstall wrangl@old-marketplace|" ]'
run install
check "a failing install stops before install.sh runs (exit 1)" '[ $RC = 1 ] && ! echo "$(calls)" | grep -q "install.sh" && echo "$OUT" | grep -q "step 3 failed"'
run install.sh
check "a failing install.sh is a step 4 failure and the run does not say it is done (exit 1)" '[ $RC = 1 ] && echo "$OUT" | grep -q "step 4 failed" && ! echo "$OUT" | grep -q "5/5"'

run "" --dry-run
check "--dry-run prints the four steps and runs nothing (exit 0)" '[ $RC = 0 ] && [ ! -s "$SB/calls" ] && echo "$OUT" | grep -q "^\[1/5\] .*marketplace update old-marketplace" && echo "$OUT" | grep -q "^\[4/5\] .*install.sh --apply --auto-rebuild"'

record other-market
run ""
check "another marketplace in the record is the one used (nearest non-match of the default name)" '[ $RC = 0 ] && echo "$(calls)" | grep -q "^plugin marketplace update other-market|plugin uninstall wrangl@other-market|" && ! echo "$(calls)" | grep -q old-marketplace'

rm -f "$SB/home/.claude/plugins/installed_plugins.json"
run ""
check "no installed record is refused before any claude call (exit 2)" '[ $RC = 2 ] && [ ! -s "$SB/calls" ]'

record old-marketplace
run "" --nonsense
check "an unknown option is a usage error and runs nothing (exit 2)" '[ $RC = 2 ] && [ ! -s "$SB/calls" ]'

run install
check "a failing install, after the uninstall, prints the command that puts wrangl back" 'echo "$OUT" | grep -q "is uninstalled; put it back with: .*claude plugin install wrangl@old-marketplace"'
run update
check "...and a failing marketplace update, before anything is uninstalled, does not (nearest non-match)" '! echo "$OUT" | grep -q "is uninstalled"'
run install.sh
check "...nor does a failing install.sh, after the install (nearest non-match)" '! echo "$OUT" | grep -q "is uninstalled"'

printf '{\n  "plugins": {\n    "wrangl@old-marketplace": [{"installPath": "%s"}],\n    "wrangl@other-market": [{"installPath": "%s"}]\n  }\n}\n' "$SB/cache/wrangl/9.9.9" "$SB/cache/wrangl/9.9.9" > "$SB/home/.claude/plugins/installed_plugins.json"
run ""
check "two wrangl installs in the record are refused before any claude call, naming both (exit 2)" '[ $RC = 2 ] && [ ! -s "$SB/calls" ] && echo "$OUT" | grep -q "wrangl@old-marketplace" && echo "$OUT" | grep -q "wrangl@other-market"'
printf '{\n  "plugins": {\n    "wrangl@old-marketplace": [{"installPath": "%s"}, {"installPath": "%s"}]\n  }\n}\n' "$SB/cache/wrangl/9.9.9" "$SB/cache/wrangl/9.9.9" > "$SB/home/.claude/plugins/installed_plugins.json"
run ""
check "...one key with two scope entries is not a second install (nearest non-match, exit 0)" '[ $RC = 0 ]'

echo "----"
echo "RESULT: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
