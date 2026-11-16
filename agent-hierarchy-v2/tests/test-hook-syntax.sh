#!/bin/bash
# agent-hierarchy — hooks.json parses and every command it names resolves.
#
# On a machine whose marketplace is a local checkout, hooks run straight from the working tree, so an
# uncommitted edit that breaks hooks.json, or a command that names a shim that is not there, takes out every
# hook entrypoint at once, and Claude Code reports that only under --debug. This is the cheapest guard:
# seconds, catching the whole class before a session does. That every unit a command names is a native unit is
# pinned by CutoverHooksTests (native/tests/Ah.Hooks.Tests).
# Usage: bash tests/test-hook-syntax.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name"; fi
}

check "hooks.json is valid JSON" 'node -e "JSON.parse(require(\"node:fs\").readFileSync(process.argv[1],\"utf8\"))" "$PLUGIN/hooks/registry.json"'

# Every shim hooks.json runs must exist and be a regular file: a rename that misses one is silent at runtime.
SHIMS=$(node -e '
  const raw = require("node:fs").readFileSync(process.argv[1], "utf8");
  const found = [...raw.matchAll(/\$\{CLAUDE_PLUGIN_ROOT\}\/(bin\/[A-Za-z0-9._-]+)/g)].map((m) => m[1]);
  console.log([...new Set(found)].join(" "));
' "$PLUGIN/hooks/registry.json")
check "hooks.json names at least one shim" '[ -n "$SHIMS" ]'
MISSING=""
for s in $SHIMS; do [ -f "$PLUGIN/$s" ] || MISSING="$MISSING $s"; done
check "every shim hooks.json runs exists" '[ -z "$MISSING" ]'

# Every command is the ah-hook shim followed by one unit name.
ODD=$(node -e '
  const cfg = JSON.parse(require("node:fs").readFileSync(process.argv[1], "utf8"));
  const shape = /^sh "\$\{CLAUDE_PLUGIN_ROOT\}\/bin\/ah-hook" [a-z0-9-]+$/;
  const odd = [];
  for (const rows of Object.values(cfg.hooks || {})) {
    for (const row of rows) for (const h of row.hooks || []) if (!shape.test(String(h.command))) odd.push(String(h.command));
  }
  console.log(odd.join("\n"));
' "$PLUGIN/hooks/registry.json")
check "every hooks.json command is the ah-hook shim and one unit name" '[ -z "$ODD" ]'

# Controls: the checks must be able to fail, or a broken file would sail past them.
TMP="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-hook-syntax.XXXXXX")"
hermetic_on_exit 'rm -rf "$TMP"'
printf '{"hooks": {\n' > "$TMP/broken.json"
OUT=$(node -e "JSON.parse(require('node:fs').readFileSync(process.argv[1],'utf8'))" "$TMP/broken.json" 2>&1)
check "control: a malformed hooks.json is reported" '[ -n "$OUT" ]'
LEGACY=$(node -e '
  const shape = /^sh "\$\{CLAUDE_PLUGIN_ROOT\}\/bin\/ah-hook" [a-z0-9-]+$/;
  console.log(shape.test("node \"${CLAUDE_PLUGIN_ROOT}/hooks/x.mjs\"") ? "shim" : "legacy");
')
check "control: a Node-form command is not the shim form" '[ "$LEGACY" = legacy ]'

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
