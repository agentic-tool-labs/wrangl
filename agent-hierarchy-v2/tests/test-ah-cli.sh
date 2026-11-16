#!/bin/bash
# agent-hierarchy — spec 0048 §6 T1/T2/T7/T8: the ah-command grammar and the allow hook (`ah-hook pretooluse-ah-cli`), driven
# through the hook itself, the no-MCP invariant, and the hooks.json wiring.
# HOME-redirected; real state untouched.
# Usage: bash tests/test-ah-cli.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-ah-cli-test.XXXXXX")"
export AH_TEST_FAKE_BIN="$SANDBOX/nolaunch"
# No test may reach the real herdr or tmux: a stub that fails every call sits first on PATH, and
# the session's pane environment is dropped. A wrapper that sets PATH to its own fakes still wins.
mkdir -p "$SANDBOX/nolaunch"; printf '#!/bin/sh\nexit 1\n' > "$SANDBOX/nolaunch/herdr"; cp "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"; chmod +x "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"
export PATH="$SANDBOX/nolaunch:$PATH"; unset WRANGL_TEAM_FILE
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
hermetic_on_exit 'rm -rf "$SANDBOX"'
FAKEHOME="$SANDBOX/home"
mkdir -p "$FAKEHOME/.claude/ah2"
# ah's hooks do nothing for a session that is not part of ah, so this suite's sessions are in by the user's own setting.
printf '{"alwaysOrchestrator":true}' > "$FAKEHOME/.claude/ah2/agent-hierarchy.json"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:300})"; fi
}

# ---------------------------------------------------------------------------
# T2 — the allow hook. Run from COPIES of the plugin's bin so the sibling rule is real.
# ---------------------------------------------------------------------------
INST="$SANDBOX/inst"
mkdir -p "$INST/A" "$INST/B" "$SANDBOX/outside"
for d in "$INST/A" "$INST/B" "$SANDBOX/outside"; do cp -R "$PLUGIN/bin" "$PLUGIN/.claude-plugin" "$d/"; done
# a session's own project, where the hook's peer rows and the persisted role land
PROJ="$SANDBOX/proj"; mkdir -p "$PROJ"; git -C "$PROJ" init -q

allow_hook() {
  OUT=$(node -e 'process.stdout.write(JSON.stringify({ session_id: "s1", cwd: "/repo", tool_name: process.argv[2] || "Bash", tool_input: { command: process.argv[1] } }))' "$1" "$2" \
    | HOME="$FAKEHOME" sh "$INST/A/bin/ah-hook" pretooluse-ah-cli 2>&1); RC=$?
}
is_allow() { case "$OUT" in *'"permissionDecision":"allow"'*) return 0;; *) return 1;; esac; }

allow_hook ""$INST/A/bin/ah" roster show --cwd /repo"
check "T2: recognised command under the hook's own root → allow" '[ "$RC" -eq 0 ] && is_allow'
BAREHOME="$SANDBOX/barehome"; mkdir -p "$BAREHOME/.claude/ah2"
OUT=$(node -e 'process.stdout.write(JSON.stringify({ session_id: "s1", cwd: "/repo", tool_name: "Bash", tool_input: { command: process.argv[1] } }))' "$INST/A/bin/ah roster show --cwd /repo" \
  | HOME="$BAREHOME" sh "$INST/A/bin/ah-hook" pretooluse-ah-cli 2>&1); RC=$?
check "T2 negative: the same command in a session nothing identifies gets no decision" '[ "$RC" -eq 0 ] && [ -z "$OUT" ]'
allow_hook ""$INST/B/bin/ah" roster show --cwd /repo"
check "T2: same command under a SIBLING install dir → allow (post-bump root)" '[ "$RC" -eq 0 ] && is_allow'
allow_hook ""$INST/A/bin/ah" roster whoami --team alpha --cwd /repo"
check "T2: whoami is allowed" '[ "$RC" -eq 0 ] && is_allow'
allow_hook ""$INST/A/bin/ah" msg list --open --cwd /repo"
check "T2: msg.mjs is allowed too" '[ "$RC" -eq 0 ] && is_allow'
allow_hook ""$INST/A/bin/ahd" inspect timers --cwd /repo"
check "T2: ahd is allowed too, and the reason names it" '[ "$RC" -eq 0 ] && is_allow && case "$OUT" in *"ahd inspect"*) true;; *) false;; esac'
BS=$(printf '\134')
FILL_REQ="/repo/.claude/ah2/msgs/20261008-000000-aaaa--reviewer--s--request.md"
FILL_CMD=""$INST/A/bin/ah" msg fill --id 20261008-000000-aaaa --from reviewer --req $FILL_REQ --body"
# allow_hook_as <command> <agent_type or -> [session id]: the same payload with the role the session runs as.
allow_hook_as() {
  OUT=$(node -e 'const o = { session_id: process.argv[3] || "s1", cwd: "/repo", tool_name: "Bash", tool_input: { command: process.argv[1] } }; if (process.argv[2] !== "-") o.agent_type = process.argv[2]; process.stdout.write(JSON.stringify(o))' "$1" "$2" "$3" \
    | HOME="$FAKEHOME" sh "$INST/A/bin/ah-hook" pretooluse-ah-cli 2>&1); RC=$?
}
is_deny() { case "$OUT" in *'"permissionDecision":"deny"'*) return 0;; *) return 1;; esac; }
allow_hook_as "$FILL_CMD '\"line one${BS}nline two${BS}u0027s; \$x | y # z\"' --cwd /repo" wrangl:reviewer
check "T2 (spec 0090): msg.mjs fill with a single-quoted JSON body, from a direct reviewer whose --from is reviewer → allow" '[ "$RC" -eq 0 ] && is_allow'
BIGBODY=$(node -e 'process.stdout.write("\"" + "a".repeat(65536) + "\"")')
allow_hook_as "$FILL_CMD '$BIGBODY' --cwd /repo" wrangl:reviewer
check "T2 (spec 0090): ...a 64 KiB body is allowed too: the hook has no length cap" '[ "$RC" -eq 0 ] && is_allow'
(cd "$PROJ" && printf '{"hook_event_name":"SessionStart","source":"startup","cwd":"%s","session_id":"s-persisted","agent_type":"wrangl:reviewer"}' "$PROJ" | HOME="$FAKEHOME" sh "$INST/A/bin/ah-hook" sessionstart >/dev/null 2>&1)
allow_hook_as "$FILL_CMD '\"a\"' --cwd /repo" wrangl:reviewer s-persisted
check "T2 (spec 0090): a session with the role in the payload → allow" '[ "$RC" -eq 0 ] && is_allow'
allow_hook_as ""$INST/A/bin/ah" msg list --open --cwd /repo" wrangl:implementor
check "T2 (spec 0090): the role binding is for fill alone: msg.mjs list from the same implementor → allow" '[ "$RC" -eq 0 ] && is_allow'
allow_hook_as "$FILL_CMD '\"a\"' | tee /tmp/ah-test-out" wrangl:reviewer
check "T2 (spec 0090): fill with a pipe outside the quotes → no output, whatever the role" '[ -z "$OUT" ]'
allow_hook_as "$FILL_CMD '\"a\"' > /tmp/ah-test-out" wrangl:implementor
check "T2 (spec 0090): fill with a redirection outside the quotes → no output (not parsed, so no decision)" '[ -z "$OUT" ]'
allow_hook_as "$FILL_CMD \$(id) --cwd /repo" wrangl:reviewer
check "T2 (spec 0090): fill with a command substitution outside the quotes → no output" '[ -z "$OUT" ]'
allow_hook_as ""$SANDBOX/outside/bin/ah" msg fill --id 20261008-000000-aaaa --from reviewer --req $FILL_REQ --body '\"a\"' --cwd /repo" wrangl:implementor
check "T2 (spec 0090): fill from a copy outside the root → no output (normal prompt), the binding is only for the plugin's own script" '[ -z "$OUT" ]'
allow_hook ""$SANDBOX/outside/bin/ah" roster show --cwd /repo"
check "T2: a copy outside the root and its siblings → no output (normal prompt)" '[ -z "$OUT" ]'
allow_hook ""$INST/A/bin/ah" roster dismiss bob --close --confirm --plan-token t --cwd /repo"
check "T2: a close command → no output (the close gate's ask must stand alone)" '[ -z "$OUT" ]'
allow_hook ""$INST/A/bin/ah" roster disband --close --confirm --plan-token t --cwd /repo"
check "T2: disband --close → no output" '[ -z "$OUT" ]'
allow_hook ""$INST/A/bin/ah" roster disband --cwd /repo"
check "T2: the disband PLAN form is allowed (only --close is withheld)" '[ "$RC" -eq 0 ] && is_allow'
allow_hook "ls -la"
check "T2: an unrelated Bash command → no output" '[ -z "$OUT" ]'
allow_hook ""$INST/A/bin/ah" roster show --cwd /repo" "Read"
check "T2: a non-Bash tool → no output" '[ -z "$OUT" ]'
OUT=$(printf 'not json' | HOME="$FAKEHOME" sh "$INST/A/bin/ah-hook" pretooluse-ah-cli 2>&1); RC=$?
check "T2: a payload that is not JSON → exit 0, no deny, and a hook-errors row (the hook fails open)" \
  '[ "$RC" -eq 0 ] && ! is_deny && grep -q "pretooluse-ah-cli" "$FAKEHOME/.claude/ah2/hierarchy/hook-errors.jsonl"'

# The grammar, through the hook: every documented shape is allowed and every unsafe one is silent (the hook allows only what the
# parser fully understood). @BIN@, @HK@ and @ROOT@ stand for the copy's bin, hooks and root directories.
ALLOW_FAIL=""; SILENT_FAIL=""
while IFS= read -r cmd; do
  [ -z "$cmd" ] && continue
  cmd=${cmd//@BIN@/$INST/A/bin}; cmd=${cmd//@HK@/$INST/A/hooks}; cmd=${cmd//@ROOT@/$INST/A}
  allow_hook "$cmd"
  is_allow && [ "$RC" -eq 0 ] || ALLOW_FAIL="$ALLOW_FAIL|$cmd"
done <<'EOF'
"@BIN@/ah" roster show --cwd /repo
"@BIN@/ah" roster teams --cwd /repo
"@BIN@/ah" roster whoami --team alpha --cwd /repo
"@BIN@/ah" roster spawn-one architect --dry-run --cwd /repo
"@BIN@/ah" roster create --commit --verified '[{"name":"a-architect","role":"architect"}]' --transport terminal --roster-level repo --cwd /repo
"@BIN@/ah" roster move bob --tab t1 --split right --cwd /repo
"@BIN@/ah" roster untrack --all --commit --keep-sessions --cwd /repo
"@BIN@/ah" msg new --to architect --from orchestrator --slug s --title "Fix test titles" --cwd /repo
"@BIN@/ah" msg list --open --to implementor --cwd /repo
"@BIN@/ah" msg index /repo/.claude/ah2/msgs/x--request.md
"@BIN@/ahd" status --plain --cwd /repo
"@BIN@/ahd" inspect entries --address a@t --all -n 5 --cwd /repo
"@BIN@/ahd" drain --require
"@BIN@/ah" roster show -n 5
EOF
check "T1: every documented command shape is allowed by the hook" '[ -z "$ALLOW_FAIL" ]'
while IFS= read -r cmd; do
  [ -z "$cmd" ] && continue
  cmd=${cmd//@BIN@/$INST/A/bin}; cmd=${cmd//@HK@/$INST/A/hooks}; cmd=${cmd//@ROOT@/$INST/A}
  allow_hook "$cmd"
  [ -z "$OUT" ] || SILENT_FAIL="$SILENT_FAIL|$cmd"
done <<'EOF'
node '@HK@/roster.mjs' show --cwd "/re po"
"@BIN@/ahd" status; ls
"@BIN@/ahd" logs -n $(id)
cd /x && "@BIN@/ah" roster show --cwd /repo
"@BIN@/ah" roster show --cwd /repo ; rm -rf /
"@BIN@/ah" roster show --cwd /repo | cat
"@BIN@/ah" roster show --cwd "$PWD"
"@BIN@/ah" roster show --cwd $(id)
node @HK@/roster.mjs show --cwd `id`
FOO=1 "@BIN@/ah" roster show --cwd /repo
nodejs @HK@/roster.mjs show --cwd /repo
node /tmp/x/roster.mjs show --cwd /repo
node @HK@/other.mjs show
node @ROOT@/*/hooks/roster.mjs show --cwd /repo
node @ROOT@/0.7?.0/hooks/roster.mjs show --cwd /repo
node @ROOT@/[a]/hooks/roster.mjs show --cwd /repo
node @ROOT@/{a,b}/hooks/roster.mjs show --cwd /repo
node ~/ah/hooks/roster.mjs show --cwd /repo
"@BIN@/ah" roster dismiss bob --cl{o..o}se --confirm --cwd /repo
"@BIN@/ah" roster dismiss bob --clos? --confirm --cwd /repo
"@BIN@/ah" roster dismiss bob --clos[e] --confirm --cwd /repo
"@BIN@/ah" roster show --cwd ~/repo
"@BIN@/ah" roster show --cwd /re*po
"@BIN@/ah" roster dismiss bob --cl{o..o}s''e --confirm --cwd /repo
"@BIN@/ah" roster dismiss bob ''{dismiss,--close} --confirm --cwd /repo
"@BIN@/ah" roster dismiss bob --close{,}'' --confirm --cwd /repo
node hooks/roster.mjs show --cwd /repo
node ./hooks/roster.mjs show --cwd /repo
"@BIN@/ah" roster show > /tmp/out
"@BIN@/ah" roster show --cwd /repo &
"@BIN@/ah" roster show --cwd /re\ po
"@BIN@/ah" roster show --cwd '/repo
node
EOF
allow_hook ""
[ -z "$OUT" ] || SILENT_FAIL="$SILENT_FAIL|(empty command)"
check "T1: every unsafe command shape is silent at the hook" '[ -z "$SILENT_FAIL" ]'
check "T1: the table can fail: an allowed shape is not silent (nearest non-match of the unsafe table)" 'allow_hook "\"$INST/A/bin/ah\" roster show --cwd /repo"; is_allow'

# every T1 rejection must also be silent at the hook (the grammar IS the boundary)
REJ_FAIL=""
while IFS= read -r cmd; do
  allow_hook "$cmd"
  [ -n "$OUT" ] && REJ_FAIL="$REJ_FAIL|$cmd"
done <<EOF
cd /x && "$INST/A/bin/ah" roster show --cwd /repo
"$INST/A/bin/ah" roster show --cwd /repo ; rm -rf /tmp/nothing
"$INST/A/bin/ah" roster show --cwd /repo | cat
"$INST/A/bin/ah" roster show --cwd "\$PWD"
FOO=1 "$INST/A/bin/ah" roster show --cwd /repo
nodejs $INST/A/hooks/roster.mjs show --cwd /repo
"$INST/A/bin/ah" roster show > /tmp/ah-test-out
EOF
check "T2: every rejected form is silent at the hook (never allowed on a partial parse)" '[ -z "$REJ_FAIL" ]'

# ---------------------------------------------------------------------------
# T7 — the no-MCP invariant
# ---------------------------------------------------------------------------
# Line-level hits. Exactly two are exempt, each matched once by its file and full line text, whatever
# its line number. Both read third-party MCP tool names, never ah's own: the MCP-tool name prefix
# that role-pack checks and the GitHub MCP merge rule read those names with, and the hooks.json
# matcher that sends MCP tool calls to that rule (a matcher can't import the constant).
T7_EXEMPT=(
  'hooks/registry.json|        "matcher": "mcp__.*",'
)
# T7's file set. In a git work tree: the files git would track there (tracked, or untracked and not
# ignored), so a --plugin-dir load's ignored output (.claude-plugin/types/) is never read. Outside one
# (a git archive copy): every file under the same paths.
t7_grep() {
  local pat='mcp__\|mcpServers\|server\.mjs\|AH_MCP'
  if [ "$(git -C "$PLUGIN" rev-parse --is-inside-work-tree 2>/dev/null)" = true ]; then
    git -C "$PLUGIN" ls-files -z --cached --others --exclude-standard -- agents skills commands hooks README.md ':(glob)docs/*.md' .claude-plugin |
      (cd "$PLUGIN" && xargs -0 grep -Hn "$pat" 2>/dev/null)
  else
    (cd "$PLUGIN" && grep -rn "$pat" agents/ skills/ commands/ hooks/ README.md docs/*.md .claude-plugin/ 2>/dev/null)
  fi
}
HITS=$(t7_grep | {
  used=""
  while IFS= read -r hit; do
    file=${hit%%:*}; rest=${hit#*:}; text=${rest#*:}
    if [[ " $used " != *" $file "* ]] && printf '%s\n' "${T7_EXEMPT[@]}" | grep -qxF "$file|$text"; then used="$used $file"; continue; fi
    printf '%s\n' "$hit"
  done
})
OUT="$HITS"
check "T7: no mcp__/mcpServers/server.mjs/AH_MCP reference outside docs/specs and tests" '[ -z "$HITS" ]'
check "T7: no .mcp.json at the plugin root" '[ ! -e "$PLUGIN/.mcp.json" ]'
check "T7: the mcp/ directory is gone" '[ ! -d "$PLUGIN/mcp" ]'
check "T7: hooks.json and the PreToolUse chain name no MCP tool" '[ "$(grep -c "mcp__" "$PLUGIN/hooks/registry.json")" = 0 ] && [ "$(grep -c "mcp__" "$PLUGIN/native/src/Ah.Hooks/Gates/GateUnits.cs")" = 0 ] && grep -q "ah-hook.* pretooluse-chain" "$PLUGIN/hooks/registry.json"'
check "T7: docs/cli-tools.md replaced docs/mcp-tools.md" '[ -f "$PLUGIN/docs/cli-tools.md" ] && [ ! -f "$PLUGIN/docs/mcp-tools.md" ]'

# ---------------------------------------------------------------------------
# T8 — hooks.json wiring
# ---------------------------------------------------------------------------
WIRING=$(node -e '
const cfg = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
const bad = [];
const chain = (cfg.hooks.PreToolUse || []).filter((r) => r.matcher === "*").flatMap((r) => r.hooks.map((h) => h.command));
if (!chain.some((c) => c.includes("pretooluse-chain"))) bad.push("pretooluse-chain is not registered");
const units = require("fs").readFileSync(process.argv[2], "utf8").split("\n");
for (const f of ["pretooluse-ah-cli", "pretooluse-compound-allow"]) {
  if (!units.some((l) => l.includes(`"${f}"`) && l.includes("\"Bash\""))) bad.push(`${f} is not a chain row on the Bash matcher`);
}
const ss = JSON.stringify(cfg.hooks.SessionStart || []);
if (ss.includes("sessionstart-mcp-ensure")) bad.push("sessionstart-mcp-ensure entry survives");
console.log(bad.length ? "FAIL " + bad.join("; ") : "PASS");
' "$PLUGIN/hooks/registry.json" "$PLUGIN/native/src/Ah.Hooks/Gates/GateUnits.cs")
check "T8: allow hook + both gates are chain rows on matcher Bash, no sessionstart-mcp-ensure entry" '[ "$WIRING" = "PASS" ]'
# The gates' verb sets live in native/src/Ah.Hooks/Gates (AhCli.CloseVerbs, the gate lists) and are pinned by
# native/tests/Ah.Hooks.Tests; the rows above pin that each gate is wired on the Bash matcher.
check "T8: sessionstart-mcp-ensure.mjs is deleted" '[ ! -f "$PLUGIN/hooks/sessionstart-mcp-ensure.mjs" ]'

# ---------------------------------------------------------------------------
# §2.1: SessionStart publishes the absolute CLI root — the only channel roles have
# ---------------------------------------------------------------------------
# The session's cwd is a sandbox repo, so the peer row the hook writes lands there, never in the
# checkout this suite runs from.
ROOT_LINE=$(cd "$PROJ" && printf '{"source":"startup","cwd":"%s","session_id":"root-line","agent_type":"wrangl:implementor"}' "$PROJ" \
  | ah_test_in_harness env HOME="$FAKEHOME" AH_MEMBER_NAME=root-line-member sh "$PLUGIN/bin/ah-hook" sessionstart 2>&1)
OUT="$ROOT_LINE"
check "§2.1: the hook's peer row lands in the sandbox repo's hierarchy dir" 'ah_roster_rows "$PROJ/.claude/ah2" | grep -q "\"session_id\":\"root-line\""'
check "§2.1: SessionStart injects an 'ah CLI root:' line naming the absolute root and both commands" \
  'echo "$ROOT_LINE" | grep -qE "ah CLI root \(v[^)]+\): $PLUGIN" && echo "$ROOT_LINE" | grep -q "$PLUGIN/bin/ah roster" && echo "$ROOT_LINE" | grep -q "$PLUGIN/bin/ah msg"'

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
