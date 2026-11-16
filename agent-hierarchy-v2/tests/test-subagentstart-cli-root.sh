#!/bin/bash
# agent-hierarchy — the `ah CLI root` line on SubagentStart.
#
# SessionStart and UserPromptSubmit never fire inside an Agent-tool subagent, so this hook is a
# subagent's only provider of the absolute CLI paths. It is also the one event whose additionalContext
# must sit inside the hookSpecificOutput envelope — bare stdout is discarded — so the shape is
# asserted, not just the text.
# HOME-redirected; real state untouched.
# Usage: bash tests/test-subagentstart-cli-root.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-subagentstart-test.XXXXXX")"
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/myrepo"
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2"
ah_test_always_on "$FAKEHOME"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:300})"; fi
}

fire() {
  OUT=$(printf '{"hook_event_name":"SubagentStart","cwd":"%s","session_id":"s1","agent_id":"a1","agent_type":"%s"}' "$PROJ" "${1:-ah2:implementor}" \
    | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" subagentstart-cli-root 2>&1); RC=$?
}

PJ_VERSION=$(node -e 'process.stdout.write(require(process.argv[1]).version)' "$PLUGIN/.claude-plugin/plugin.json")
# The line the three injection points share: the plugin's version, its root, and the two absolute CLI commands.
ROOT_LINE="ah CLI root (v$PJ_VERSION): $PLUGIN — roster: \`$PLUGIN/bin/ah roster <verb> --cwd <abs cwd>\`, messages: \`$PLUGIN/bin/ah msg <verb> --cwd <abs cwd>\` (verbs: $PLUGIN/docs/cli-tools.md)"

# ---- 1: unconfigured cwd — a subagent still gets the line
fire
check "unconfigured cwd: exits 0" '[ "$RC" -eq 0 ]'
check "exactly one JSON object on stdout" \
  '[ "$(echo "$OUT" | node -e "let d=\"\";process.stdin.on(\"data\",c=>d+=c).on(\"end\",()=>{try{JSON.parse(d);console.log(\"one\")}catch{console.log(\"not-one\")}})")" = "one" ]'
check "hookEventName is SubagentStart" 'echo "$OUT" | grep -q "\"hookEventName\":\"SubagentStart\""'
check "additionalContext sits inside hookSpecificOutput, not at top level" \
  'node -e "
     const o = JSON.parse(process.argv[1]);
     process.exit(o.hookSpecificOutput && typeof o.hookSpecificOutput.additionalContext === \"string\" && o.additionalContext === undefined ? 0 : 1);
   " "$OUT"'
# A role subagent also gets the chain-of-command paragraph (ah-chain-of-command.md slice B) after the root line.
check "role subagent: the root line byte-for-byte, then the chain-of-command paragraph" \
  'node -e "
     const o = JSON.parse(process.argv[1]);
     process.exit(o.hookSpecificOutput.additionalContext.startsWith(process.argv[2] + \"\\n\\nChain of command: \") ? 0 : 1);
   " "$OUT" "$ROOT_LINE"'
# Negative: a plain subagent (no hierarchy role) gets the root line alone.
fire general-purpose
check "plain subagent: additionalContext equals the plugin's root line byte-for-byte" \
  'node -e "
     const o = JSON.parse(process.argv[1]);
     process.exit(o.hookSpecificOutput.additionalContext === process.argv[2] ? 0 : 1);
   " "$OUT" "$ROOT_LINE"'

# ---- 2: configured and enabled still gets the line
cat > "$PROJ/.claude/ah2/agent-hierarchy.json" <<EOF
{ "version": 1, "enabled": true, "roles": {} }
EOF
fire
check "hierarchy configured and enabled: emits the root line" '[ "$RC" -eq 0 ] && echo "$OUT" | grep -q "ah CLI root"'

# ---- 3: a disabled hierarchy injects nothing anywhere, this hook included
cat > "$PROJ/.claude/ah2/agent-hierarchy.json" <<EOF
{ "version": 1, "enabled": false, "roles": {} }
EOF
fire
check "hierarchy configured but disabled: no output at all" '[ "$RC" -eq 0 ] && [ -z "$OUT" ]'
rm -f "$PROJ/.claude/ah2/agent-hierarchy.json"

# ---- 4: malformed input fails open
OUT=$(printf 'not json at all' | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" subagentstart-cli-root 2>&1); RC=$?
check "malformed input: RC 0, no throw" '[ "$RC" -eq 0 ]'

# ---- 5: hooks.json wiring
check "hooks.json runs this hook on SubagentStart" \
  'node -e "
     const j = JSON.parse(require(\"node:fs\").readFileSync(process.argv[1], \"utf8\"));
     const rules = (j.hooks.SubagentStart || []).filter((r) => (r.hooks || []).some((h) => /\\/bin\\/ah-hook\"? subagentstart-cli-root\$/.test(h.command || \"\")));
     process.exit(rules.length === 1 && rules[0].matcher === \"*\" ? 0 : 1);
   " "$PLUGIN/hooks/registry.json"'

# ---- 6: $CLAUDE_PLUGIN_ROOT is empty in a Bash-tool environment, so a hook that prints it as part
# of a runnable command hands the reader a broken command. Only hooks.json may name it.
# The one code use is the spawn-shape constant that holds the placeholder so it can remove it from an exported agent body
# (it removes the variable, it does not emit it). Only that whole line, in that file, is allowed; any other
# non-comment mention in the native sources is a leak.
leaks_in() {
  grep -nH 'CLAUDE_PLUGIN_ROOT' "$@" 2>/dev/null | grep -vE ':[0-9]+: *(//|\*|/\*)' \
    | grep -vE '(^|/)Ah\.Launch/Spawn/SpawnShapes\.cs:[0-9]+: *private const string PluginRootPlaceholder = "\$\{CLAUDE_PLUGIN_ROOT\}";$' || true
}
LEAKS=$(leaks_in $(find "$PLUGIN/native/src" -name '*.cs'))
check "no native source emits \$CLAUDE_PLUGIN_ROOT outside a comment" '[ -z "$LEAKS" ] && [ -f "$PLUGIN/native/src/Ah.Launch/Spawn/SpawnShapes.cs" ]'
PROBE_DIR="$(mktemp -d "${TMPDIR:-/tmp}/leak-probe.XXXXXX")"
hermetic_on_exit "rm -rf '$PROBE_DIR'"
mkdir -p "$PROBE_DIR/Ah.Launch/Spawn"
SHAPES="$PROBE_DIR/Ah.Launch/Spawn/SpawnShapes.cs"
ALLOWED='    private const string PluginRootPlaceholder = "${CLAUDE_PLUGIN_ROOT}";'
printf '%s\n' "$ALLOWED" > "$SHAPES"
check "the allowance admits exactly that whole line in Ah.Launch/Spawn/SpawnShapes.cs" '[ -z "$(leaks_in "$SHAPES")" ]'
printf '%s\n' "$ALLOWED"' Emit("${CLAUDE_PLUGIN_ROOT}/hooks");' > "$SHAPES"
check "the allowed text with an emission after it on the same line is still a leak" '[ -n "$(leaks_in "$SHAPES")" ]'
printf '%s\n' "$ALLOWED" > "$PROBE_DIR/Other.cs"
check "the allowed line in another source file is still a leak" '[ -n "$(leaks_in "$PROBE_DIR/Other.cs")" ]'
printf '%s\n' 'var s = "${CLAUDE_PLUGIN_ROOT}/hooks";' > "$SHAPES"
check "a different line in SpawnShapes.cs emitting the variable is still a leak" '[ -n "$(leaks_in "$SHAPES")" ]'

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
