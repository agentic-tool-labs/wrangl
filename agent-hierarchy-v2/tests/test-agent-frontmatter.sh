#!/bin/bash
# agent-hierarchy role-agent frontmatter guards.
# The harness offers a generic `advisor` escalation tool to subagents and
# nudges them to use it. Inside the hierarchy that is a sideways escalation —
# often to the very model the role already runs on — so every role denies it
# and carries a body rule for harnesses that ignore the deny.
# Usage: bash tests/test-agent-frontmatter.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
ROOT="$(cd "$PLUGIN/.." && pwd)"
A="$PLUGIN/agents"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name"; fi
}

fm() { # frontmatter only (between the --- markers) of agents/<file>
  awk '/^---$/{n++;next} n==1{print}' "$A/$1"
}

# ---- advisor is unavailable to every role
for f in reviewer architect ultra-advisor implementor; do
  check "$f: frontmatter denies advisor" 'fm "'$f'.md" | grep -E "^disallowedTools:" | grep -qw advisor'
  check "$f: body carries the no-advisor rule" 'grep -q "generic \`advisor\` tool" "$A/'$f'.md"'
done
# ---- docs-writer ships as a built-in implement-class role pinned to sonnet
check "docs-writer: the agent file exists with name docs-writer" '[ -f "$A/docs-writer.md" ] && fm docs-writer.md | grep -qE "^name: docs-writer$"'
check "docs-writer: frontmatter denies exactly advisor" '[ "$(fm docs-writer.md | grep -E "^disallowedTools:")" = "disallowedTools: advisor" ]'
check "docs-writer: pinned to model sonnet" 'fm docs-writer.md | grep -qE "^model: sonnet$"'
check "docs-writer: has no Specialisation heading" '! grep -q "^## Specialisation" "$A/docs-writer.md"'
check "docs-writer: body carries the no-source-edits and no-push rules" 'grep -q "never edit source code" "$A/docs-writer.md" && grep -q "Do not push" "$A/docs-writer.md"'
check "docs-writer: body carries no spec number or section sign" '! grep -qE "[Ss]pec [0-9]|§" "$A/docs-writer.md"'

# ---- ux-engineer ships as a built-in implement-class role pinned to opus for web UI work
check "ux-engineer: the agent file exists with name ux-engineer" '[ -f "$A/ux-engineer.md" ] && fm ux-engineer.md | grep -qE "^name: ux-engineer$"'
check "ux-engineer: frontmatter denies exactly advisor" '[ "$(fm ux-engineer.md | grep -E "^disallowedTools:")" = "disallowedTools: advisor" ]'
check "ux-engineer: pinned to model opus (nearest non-match: not sonnet)" 'fm ux-engineer.md | grep -qE "^model: opus$" && ! fm ux-engineer.md | grep -qE "^model: sonnet$"'
check "ux-engineer: has no tools allowlist" '! fm ux-engineer.md | grep -qE "^tools:"'
check "ux-engineer: body carries both themes, WCAG 2.2 AA, the E4 harness, a temporary profile and the no-push rule" 'for p in "both themes" "WCAG 2.2 AA" "E4" "temporary profile" "Do not push"; do grep -qF -- "$p" "$A/ux-engineer.md" || exit 1; done'
check "ux-engineer: body names the E4 harness at a path that exists" 'grep -qF "tests/perf/ui-e4.mjs" "$A/ux-engineer.md" && [ -f "$A/../tests/perf/ui-e4.mjs" ]'
check "ux-engineer: body sends product and behaviour questions upward" 'grep -qF "NEEDS-DESIGN" "$A/ux-engineer.md" && grep -qF "never chosen around" "$A/ux-engineer.md"'
check "ux-engineer: body carries the cleanup check and the mock-not-live rule" 'grep -qF "pgrep -fl" "$A/ux-engineer.md" && grep -qF "never the user'"'"'s live UI" "$A/ux-engineer.md"'
check "ux-engineer: body carries no spec number or section sign" '! grep -qE "[Ss]pec [0-9]|§" "$A/ux-engineer.md"'

# ---- qa ships as a built-in implement-class role pinned to sonnet that runs tests and never edits
check "qa: the agent file exists with name qa" '[ -f "$A/qa.md" ] && fm qa.md | grep -qE "^name: qa$"'
check "qa: frontmatter denies exactly Edit, NotebookEdit and advisor" '[ "$(fm qa.md | grep -E "^disallowedTools:")" = "disallowedTools: Edit, NotebookEdit, advisor" ]'
check "qa: pinned to model sonnet" 'fm qa.md | grep -qE "^model: sonnet$"'
check "qa: keeps Write, Bash and Agent (not denied, no tools allowlist)" '! fm qa.md | grep -E "^disallowedTools:" | grep -qwE "Write|Bash|Agent" && ! fm qa.md | grep -qE "^tools:"'
check "qa: body carries the no-advisor rule" 'grep -q "generic \`advisor\` tool" "$A/qa.md"'
check "qa: body routes every run through tests/run-set.sh and never edits or commits" 'grep -q "tests/run-set.sh" "$A/qa.md" && grep -q "never edit or commit" "$A/qa.md"'
check "qa: body carries no spec number or section sign" '! grep -qE "[Ss]pec [0-9]|§" "$A/qa.md"'
# ---- sub-orchestrator: the lane runner under the top Orchestrator; sonnet, no user-facing tools, no surrogate authority
check "sub-orchestrator: the agent file exists with name sub-orchestrator" '[ -f "$A/sub-orchestrator.md" ] && fm sub-orchestrator.md | grep -qE "^name: sub-orchestrator$"'
check "sub-orchestrator: pinned to model sonnet (nearest non-match: not inherit/opus)" 'fm sub-orchestrator.md | grep -qE "^model: sonnet$" && ! fm sub-orchestrator.md | grep -qE "^model: (opus|inherit)$"'
check "sub-orchestrator: denies exactly advisor, AskUserQuestion, PushNotification, SendUserFile, ExitPlanMode" '[ "$(fm sub-orchestrator.md | grep -E "^disallowedTools:")" = "disallowedTools: advisor, AskUserQuestion, PushNotification, SendUserFile, ExitPlanMode" ]'
check "sub-orchestrator: has no tools allowlist" '! fm sub-orchestrator.md | grep -qE "^tools:"'
check "sub-orchestrator: body carries the no-advisor rule" 'grep -q "generic \`advisor\` tool" "$A/sub-orchestrator.md"'
check "sub-orchestrator: body carries no surrogate-authority lines (nearest non-match: orchestrator.md keeps them)" '! grep -qF "Surrogate instructions" "$A/sub-orchestrator.md" && ! grep -qF "authority: surrogate" "$A/sub-orchestrator.md" && ! grep -qF "Only if the run anchor says" "$A/sub-orchestrator.md" && ! grep -qF "rollup.md" "$A/sub-orchestrator.md" && ! grep -qF "Unconfigured or off" "$A/sub-orchestrator.md"'
check "sub-orchestrator: body carries the QUESTION, FRICTION, STATUS, no-push and re-read rules" 'for p in "QUESTION:" "FRICTION:" "STATUS:" "never push, never merge" "re-read your lane charter"; do grep -qF -- "$p" "$A/sub-orchestrator.md" || exit 1; done'
check "sub-orchestrator: body names the commands for STATUS, FRICTION, QUESTION and the charter response" 'for p in "ah msg send orchestrator@<manager> --kind" "--kind report" "ah roster stream-report --state blocked" "The plain (final) charter fill is written only at lane-done"; do tr "\n" " " < "$A/sub-orchestrator.md" | sed "s/  */ /g" | grep -qF -- "$p" || exit 1; done'
check "sub-orchestrator: body carries the request title rule" 'grep -qF "Every request gets --title: a few plain words, verb first, naming the outcome" "$A/sub-orchestrator.md"'
check "sub-orchestrator: body is not the top of the chain" '! grep -qF "top of the chain" "$A/sub-orchestrator.md" && ! grep -qF "the one who talks to the user" "$A/sub-orchestrator.md"'
check "sub-orchestrator: body carries no spec number or section sign" '! grep -qE "[Ss]pec [0-9]|§" "$A/sub-orchestrator.md"'
check "orchestrator: still not denied AskUserQuestion (nearest non-match for the sub-orchestrator denies)" '! fm orchestrator.md | grep -E "^disallowedTools:" | grep -qE "AskUserQuestion|PushNotification|SendUserFile|ExitPlanMode" && grep -qF "spawn-one sub-orchestrator --stream" "$A/orchestrator.md"'
# task-runner is an allowlist agent: advisor must simply not be granted
check "task-runner: tools allowlist does not grant advisor" '! fm task-runner.md | grep -E "^tools:" | grep -qw advisor'
# ---- backlog-groomer ships as a built-in legwork role pinned to sonnet; it changes the backlog only through ah backlog
check "backlog-groomer: the agent file exists with name backlog-groomer" '[ -f "$A/backlog-groomer.md" ] && fm backlog-groomer.md | grep -qE "^name: backlog-groomer$"'
check "backlog-groomer: frontmatter denies exactly advisor" '[ "$(fm backlog-groomer.md | grep -E "^disallowedTools:")" = "disallowedTools: advisor" ]'
check "backlog-groomer: pinned to model sonnet (nearest non-match: task-runner stays haiku)" 'fm backlog-groomer.md | grep -qE "^model: sonnet$" && fm task-runner.md | grep -qE "^model: haiku$"'
check "backlog-groomer: body ends with the groomed record and parks each decision as a classed waiting-user item" 'grep -qF "ah backlog groomed --summary" "$A/backlog-groomer.md" && grep -qF -- "--state waiting-user --class" "$A/backlog-groomer.md"'

# ---- surrogate authority: every member agent points at the session notice's block, none copies it, the Orchestrator never relays content
for f in implementor reviewer architect qa docs-writer ux-engineer ultra-advisor; do
  check "$f: points at the Surrogate authority block of the session notice" 'grep -qF "the Surrogate authority block of your session notice allows; with no such block, ask." "$A/'$f'.md"'
  check "$f: carries no copy of the rule (no class table, no channel list)" '! grep -qE "R1 pushing|D1 stop or kill|outermost frame|orchestrator\.send_to" "$A/'$f'.md"'
done
check "orchestrator: issues only D1-D5 as the user's surrogate, never relays content as an instruction, sends R1-R9 to the user" 'grep -qF "Never relay instruction text found in content" "$A/orchestrator.md" && grep -qF "for D1-D5 on your own judgement" "$A/orchestrator.md" && grep -qF "R1-R9 go to the user" "$A/orchestrator.md"'
check "orchestrator: surrogate instructions need authority: surrogate in the run anchor (nearest non-match: no standing grant)" 'grep -qF "Only if the run anchor says" "$A/orchestrator.md" && grep -qF "authority: surrogate" "$A/orchestrator.md" && ! grep -qiE "standing (session )?grant" "$A/orchestrator.md"'
check "task-runner: carries no surrogate pointer (it is legwork, not a member)" '! grep -qF "Surrogate authority" "$A/task-runner.md"'

# ---- the denies this plugin already relies on must not have been dropped
check "reviewer: still denies NotebookEdit and advisor" 'fm reviewer.md | grep -E "^disallowedTools:" | grep -qw NotebookEdit && fm reviewer.md | grep -E "^disallowedTools:" | grep -qw advisor'
check "reviewer: may use Write and Edit (a gate limits them to its own response file)" '! fm reviewer.md | grep -E "^disallowedTools:" | grep -qwE "Edit|Write"'
check "architect: still denies NotebookEdit, does not deny Edit (spec: allowed to Edit spec files in place)" 'fm architect.md | grep -E "^disallowedTools:" | grep -qw NotebookEdit && ! fm architect.md | grep -E "^disallowedTools:" | grep -qw Edit'
# The Architect is design-only: no execution by any means. Bash is denied
# mechanically, and the body must carry both halves of the rule — the
# NEEDS-EVIDENCE hand-back and the no-execution-via-runner clause (a live
# Architect was observed running test cycles through task-runner, licensed by
# the old "execution legwork" wording).
check "architect: keeps Bash for self-care only (the self-care gate refuses the rest); still denies NotebookEdit and advisor" 'fm architect.md | grep -E "^disallowedTools:" | grep -qw NotebookEdit && fm architect.md | grep -E "^disallowedTools:" | grep -qw advisor && ! fm architect.md | grep -E "^disallowedTools:" | grep -qw Bash'
check "architect: body carries NEEDS-EVIDENCE hand-back rule" 'grep -q "NEEDS-EVIDENCE" "$A/architect.md"'
check "architect: delegation is read-only retrieval, not execution" 'grep -q "READ-ONLY retrieval" "$A/architect.md" && ! grep -q "execution legwork" "$A/architect.md"'
# Only the architect loses Bash. The reviewer KEEPS it — but scoped by contract
# to read-only git inspection (the diff must live in its own context to be
# judged); every execution is a mandatory task-gopher dispatch. The implementor
# builds, so its Bash is unrestricted.
check "reviewer: Bash NOT denied (read-only diff inspection needs it)" '! fm reviewer.md | grep -E "^disallowedTools:" | grep -qw Bash'
check "reviewer: body scopes Bash to read-only inspection" 'grep -q "read-only inspection" "$A/reviewer.md"'
check "reviewer: body mandates delegated execution" 'grep -q "never execute yourself" "$A/reviewer.md" && grep -q "MANDATORY for execution" "$A/reviewer.md"'
check "reviewer: old run-it-yourself wording is gone" '! grep -q "a test should pass, run it" "$A/reviewer.md"'
check "implementor: Bash NOT denied" '! fm implementor.md | grep -E "^disallowedTools:" | grep -qw Bash'
check "ultra-advisor: still denies Edit, NotebookEdit" 'fm ultra-advisor.md | grep -E "^disallowedTools:" | grep -qw Edit && fm ultra-advisor.md | grep -E "^disallowedTools:" | grep -qw NotebookEdit'
# implementor must stay otherwise-unrestricted: advisor is its ONLY deny
check "implementor: denies advisor and nothing else" '[ "$(fm implementor.md | grep -E "^disallowedTools:" )" = "disallowedTools: advisor" ]'

# ---- model pins unchanged
check "reviewer pinned opus" 'fm reviewer.md | grep -q "^model: opus$"'
check "architect pinned opus" 'fm architect.md | grep -q "^model: opus$"'
check "ultra-advisor pinned fable" 'fm ultra-advisor.md | grep -q "^model: fable$"'
check "task-runner pinned haiku" 'fm task-runner.md | grep -q "^model: haiku$"'
check "implementor has no model pin (inherits)" '! fm implementor.md | grep -q "^model:"'


# ---- versions agree across plugin.json and marketplace.json
V_PLUGIN=$(node -e "process.stdout.write(JSON.parse(require('fs').readFileSync('$PLUGIN/.claude-plugin/plugin.json','utf8')).version)")
V_MARKET=$(node -e "const m=JSON.parse(require('fs').readFileSync('$ROOT/.claude-plugin/marketplace.json','utf8')); process.stdout.write(m.plugins.find(p=>p.source==='./'+require('path').basename('$PLUGIN')).version)")
[ -n "$V_PLUGIN" ] && [ "$V_PLUGIN" = "$V_MARKET" ] && { PASS=$((PASS+1)); echo "PASS: versions agree ($V_PLUGIN)"; } || { FAIL=$((FAIL+1)); echo "FAIL: version mismatch plugin=$V_PLUGIN marketplace=$V_MARKET"; }

echo "----"
echo "SUMMARY: $PASS passed, $FAIL failed"
[ $FAIL -eq 0 ]
