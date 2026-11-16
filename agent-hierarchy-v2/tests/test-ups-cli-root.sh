#!/bin/bash
# agent-hierarchy — the `ah CLI root:` line on UserPromptSubmit.
#
# SessionStart alone does not cover the case that produced this: `/reload-plugins` fires no
# SessionStart, so a session that updates mid-flight never hears the new root and the CLI paths
# are the whole interface. UserPromptSubmit repeats the line when it changed. Its classification must
# match SessionStart's — nothing for subagents, nothing when the hierarchy is configured-and-
# disabled — and the line itself must come from the one builder, not a restatement.
# HOME-redirected; real config never touched.
# Usage: bash tests/test-ups-cli-root.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-ups-cli-root-test.XXXXXX")"
hermetic_on_exit 'rm -rf "$SANDBOX"'
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/proj"
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2"
ah_test_always_on "$FAKEHOME"
(cd "$PROJ" && git init -q)
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:300})"; fi
}

# ups <prompt> [agent_id] [session_id]: a fresh session id per call unless one is given, so an earlier
# prompt's record cannot silence a later case. An id of "-" sends no session_id at all.
UPS_N=0
ups() {
  local prompt=$1 agent_id=${2:-} sid=${3:-}
  UPS_N=$((UPS_N+1))
  [ -z "$sid" ] && sid="s-auto-$UPS_N"
  OUT=$(node -e '
    const payload = { cwd: process.argv[1], prompt: process.argv[2] };
    if (process.argv[4] !== "-") payload.session_id = process.argv[4];
    if (process.argv[3]) payload.agent_id = process.argv[3];
    process.stdout.write(JSON.stringify(payload));
  ' "$PROJ" "$prompt" "$agent_id" "$sid" | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" userpromptsubmit-peer-tracking 2>&1); RC=$?
}

# Two root lines in one context after a mid-session update name two different plugin directories;
# the version token is what tells a role which of them it is looking at.
PJ_VERSION=$(node -e 'process.stdout.write(require(process.argv[1]).version)' "$PLUGIN/.claude-plugin/plugin.json")
ROOT_LINE="ah CLI root (v$PJ_VERSION): $PLUGIN — roster: \`$PLUGIN/bin/ah roster <verb> --cwd <abs cwd>\`, messages: \`$PLUGIN/bin/ah msg <verb> --cwd <abs cwd>\` (verbs: $PLUGIN/docs/cli-tools.md)"
ups "what is the root"
EMITTED=$(printf '%s' "$OUT" | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8")||"{}");const c=(o.hookSpecificOutput&&o.hookSpecificOutput.additionalContext)||"";process.stdout.write(c.split("\n").filter((l)=>l.startsWith("ah CLI root (v")).join("\n"))')
check "the emitted root line opens with the root marker and names both CLIs" \
  '[ -n "$EMITTED" ] && echo "$EMITTED" | grep -qE "^ah CLI root \(v[^)]+\): " &&
   echo "$EMITTED" | grep -q "/bin/ah roster" && echo "$EMITTED" | grep -q "/bin/ah msg"'
check "the emitted root line spells the absolute ah paths of this plugin root" \
  'echo "$EMITTED" | grep -q "\`$PLUGIN/bin/ah roster <verb>" && echo "$EMITTED" | grep -q "\`$PLUGIN/bin/ah msg <verb>"'
check "the emitted root line carries the plugin.json version as a (vX.Y.Z) token" \
  '[ -n "$PJ_VERSION" ] && echo "$EMITTED" | grep -qF "ah CLI root (v$PJ_VERSION):"'

# ---- 1: an ordinary prompt in a main session carries the line, verbatim from cliRootLine()
ups "please fix the bug in the login form"
check "main session, unconfigured cwd: emits the root line" \
  '[ "$RC" -eq 0 ] && echo "$OUT" | grep -q "ah CLI root (v"'
check "main session: additionalContext contains cliRootLine() byte-for-byte" \
  'node -e "
     const out = JSON.parse(process.argv[1]);
     const ctx = out.hookSpecificOutput.additionalContext;
     process.exit(ctx.includes(process.argv[2]) ? 0 : 1);
   " "$OUT" "$ROOT_LINE"'
check "main session: still exactly one stdout JSON object" \
  '[ "$(echo "$OUT" | node -e "let d=\"\";process.stdin.on(\"data\",c=>d+=c).on(\"end\",()=>{try{JSON.parse(d);console.log(\"one\")}catch{console.log(\"not-one\")}})")" = "one" ]'
check "main session: hookEventName is UserPromptSubmit" 'echo "$OUT" | grep -q "\"hookEventName\":\"UserPromptSubmit\""'

# ---- 2: a subagent gets nothing (same rule as sessionstart.mjs)
ups "please fix the bug in the login form" "agent123"
check "subagent: no output at all" '[ "$RC" -eq 0 ] && [ -z "$OUT" ]'

# ---- 3: configured-and-disabled gets nothing
cat > "$PROJ/.claude/ah2/agent-hierarchy.json" <<EOF
{ "version": 1, "enabled": false, "roles": {} }
EOF
ups "please fix the bug in the login form"
check "hierarchy configured but disabled: no output at all" '[ "$RC" -eq 0 ] && [ -z "$OUT" ]'

# ---- 4: configured and enabled gets the line
cat > "$PROJ/.claude/ah2/agent-hierarchy.json" <<EOF
{ "version": 1, "enabled": true, "roles": {} }
EOF
ups "please fix the bug in the login form"
check "hierarchy configured and enabled: emits the root line" \
  '[ "$RC" -eq 0 ] && echo "$OUT" | grep -q "ah CLI root (v"'

# ---- 5: the pre-existing team-intent nudge still fires, and now shares the write
ups "let's spawn the team for this repo"
check "team-intent prompt: nudge AND root line in one stdout object" \
  '[ "$RC" -eq 0 ] && echo "$OUT" | grep -q "wrangl:agent-team" && echo "$OUT" | grep -q "ah CLI root (v" && [ "$(echo "$OUT" | grep -c "hookSpecificOutput")" -eq 1 ]'

# ---- 5b: the line is emitted when it differs from the one last put into the session's context
has_line() { echo "$OUT" | grep -q "ah CLI root (v"; }
ups "first prompt" "" sess-a
check "first prompt of a session emits the root line" '[ "$RC" -eq 0 ] && has_line'
ups "second prompt" "" sess-a
check "an identical second prompt in the same session does not" '[ "$RC" -eq 0 ] && ! has_line'
ups "another session's first prompt" "" sess-b
check "a different session still gets its own first emission" 'has_line'
ups "no session id, first" "" -
check "no session id: emits" 'has_line'
ups "no session id, again" "" -
check "no session id: emits every time" 'has_line'
check "the emission left one cli-root row per session, carrying a digest and no line text" \
  '[ "$(ah_peer_rows | grep -c "\"type\":\"cli-root\"")" -ge 2 ] && ! ah_peer_rows | grep "\"type\":\"cli-root\"" | grep -q "ah CLI root"'
# a recorded digest of some other line (a changed version or root) reads as a change
ah_seed_peer '{"type":"cli-root","session_id":"sess-c","digest":"0000000000000000","ts":"2026-01-01T00:00:00.000Z"}'
ups "after a version or root change" "" sess-c
check "a recorded digest that differs from the current line: emits" 'has_line'
ups "and then it is quiet again" "" sess-c
check "...and records the new line, so the next prompt is quiet" '! has_line'
# after a SessionStart that emitted, the next prompt does not repeat it
SS_OUT=$(printf '{"session_id":"sess-e","cwd":"%s","source":"startup","hook_event_name":"SessionStart"}' "$PROJ" | HOME="$FAKEHOME" ah_test_in_harness sh "$PLUGIN/bin/ah-hook" sessionstart 2>/dev/null)
check "SessionStart emits the root line into a configured session" 'echo "$SS_OUT" | grep -q "ah CLI root (v"'
ups "first prompt after the SessionStart" "" sess-e
check "the first prompt after a SessionStart that emitted does not repeat the line" '! has_line'

# ---- 6: malformed input still fails open
OUT=$(printf 'not json at all' | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" userpromptsubmit-peer-tracking 2>&1); RC=$?
check "malformed input: RC 0, no throw" '[ "$RC" -eq 0 ]'

# ---- 7: the injection points share one source of truth. If a hook ever restates the line instead of calling
# the builder, a second source file carries the literal and that is the drift this catches.
check "exactly one native source file carries the root line text" \
  '[ "$(grep -rl "ah CLI root (v" "$PLUGIN/native/src" --include="*.cs" | wc -l | tr -d " ")" = 1 ]'

# ---- 8: no `<AH_ROOT>` placeholder survives in text the harness substitutes into (agents/, skills/)
check "agents/ and skills/ use \${CLAUDE_PLUGIN_ROOT}, never the <AH_ROOT> placeholder" \
  '[ "$(grep -rl "<AH_ROOT>" "$PLUGIN/agents" "$PLUGIN/skills" 2>/dev/null | wc -l | tr -d " ")" -eq 0 ]'

# docs/cli-tools.md is read as a FILE, never substituted, so it keeps <AH_ROOT> — but it must carry
# the resolve-it-yourself recipe, or a reader has a placeholder and no way to expand it.
check "docs/cli-tools.md keeps <AH_ROOT> and carries the installed_plugins.json fallback recipe" \
  'grep -q "<AH_ROOT>" "$PLUGIN/docs/cli-tools.md" && grep -q "installed_plugins.json" "$PLUGIN/docs/cli-tools.md"'
# One recipe, in docs/cli-tools.md: a second copy is a second thing to drift.
check "orchestrator.md points at cli-tools.md rather than carrying its own recipe" \
  '! grep -q "installed_plugins.json" "$PLUGIN/agents/orchestrator.md" && grep -q "docs/cli-tools.md" "$PLUGIN/agents/orchestrator.md"'

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
