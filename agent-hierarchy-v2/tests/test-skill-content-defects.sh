#!/bin/bash
# agent-hierarchy — content defects fixed in the skills: the pipeline run anchor has a template and a way to write it,
# the plan-run push command and the peer-dispatch writer are stated, the round-cap scopes are named, and the dangling
# references and over-long option lists in the team, roster and role skills are gone.
# Usage: bash tests/test-skill-content-defects.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name"; fi
}
skill_text "$PLUGIN/skills/autonomous-pipeline" AP
skill_text "$PLUGIN/skills/agent-team" AT
skill_text "$PLUGIN/skills/agent-roster" AR
skill_text "$PLUGIN/skills/agent-role" ARL

# pipeline
check "pipeline: the anchor has a fenced ## constraints template with branch and plan lines" \
  'awk "/^   \`\`\`\$/{f=!f; next} f" "$AP" | grep -qx "   ## constraints" && grep -qx "   branch: <resolved branch name>" "$AP" && grep -qx "   plan: <plan path>" "$AP"'
check "pipeline: the anchor is created with msg.mjs new --slug pipeline-run-anchor, then its body written with Write/Edit" \
  'tr -s "\n " "  " < "$AP" | grep -qF "Create the file with the command above, slug \`pipeline-run-anchor\`" && grep -qF "With the Write/Edit tool, replace the file'"'"'s body" "$AP"'
check "pipeline: the plan-run push command sets the upstream on the first push" \
  'grep -qF "git push --set-upstream origin <branch>" "$AP" && grep -qF "git push origin <branch>" "$AP"'
check "pipeline: the peer dispatch writer carries --eta and its flags" \
  'grep -qF "msg new --to <role> --from orchestrator --slug <item slug> --title \"<few plain words, verb first>\" --eta small|medium|large --team <team> --body-file <brief file> --cwd <abs cwd>" "$AP"'
check "pipeline: the decider brief names its writer command with --eta" \
  'grep -qF -- "--slug dec-<anchor suffix>-<k> --eta small|medium|large" "$AP"'
check "pipeline: the round count names ah msg list --slug --count once, not twice (nearest non-match of the old typo)" \
  'grep -qF "via \`<ah> msg list --slug <slug> --count\`" "$AP" && ! grep -qF "\`ah msg list\` / \`ah msg list\`" "$AP"'
check "pipeline: the 3-round cap says it is the pipeline's and the directive's 2-round cap governs ordinary work" \
  'tr "\n" " " < "$AP" | grep -qF "This 3-round cap is the pipeline'"'"'s; the directive'"'"'s 2-round review cap governs ordinary work"'

# agent-team
check "agent-team: the no-roster hand-off names the agent-roster skill's Init" \
  'grep -qF "hand off to § Init in the agent-roster skill" "$AT" && ! grep -qE "hand off to § Init\.$" "$AT"'
check "agent-team: the paneless step no longer cites item 0's \"do it inline\"" \
  '! grep -qF "item 0'"'"'s \"do it inline\"" "$AT" && grep -qF "Do it inline" "$AT"'
check "agent-team: the orphan paragraph the teams entry cites is a heading" \
  'grep -qx "### An orphan that live sessions depend on" "$AT" && ! grep -qF "**An orphan that live sessions depend on.**" "$AT"'
check "agent-team: the verified member array has a one-object sample" \
  'grep -qF "\"role\": \"architect\", \"name\": \"<member name>\", \"ref\": \"<ref from ListAgents>\"" "$AT"'

# autonomous-pipeline split
APD="$PLUGIN/skills/autonomous-pipeline"
check "pipeline: SKILL.md is at most 30720 bytes and 500 lines" \
  '[ "$(wc -c < "$APD/SKILL.md")" -le 30720 ] && [ "$(wc -l < "$APD/SKILL.md")" -le 500 ]'
check "pipeline: every reference file over 100 lines has its Contents list within the first 15 lines" \
  'BAD=; for f in "$APD"/reference/*.md; do [ "$(wc -l < "$f")" -le 100 ] || head -15 "$f" | grep -qx "Contents" || BAD=1; done; [ -z "$BAD" ]'
check "pipeline: the reference files exist and SKILL.md names each" \
  'BAD=; for f in decisions issue-runs; do [ -f "$APD/reference/$f.md" ] && grep -qF "reference/$f.md" "$APD/SKILL.md" || BAD=1; done; [ -z "$BAD" ]'
check "pipeline: the cross-cutting rules are in SKILL.md, one phrase per rule" \
  'BAD=; for p in "or edit a PR the run did not" "Third-party text" "No tracker writes; never comment on the issue" "never \`SendMessage\`d" "Architect sign-off is on the review" "Entering degraded mode is a notification event" "owns create and disband"; do tr -s "\n " "  " < "$APD/SKILL.md" | grep -qF -- "$p" || BAD=1; done; [ -z "$BAD" ]'
check "pipeline: the decider and brief headings stay in a reference file, in that order" \
  'sed -n "/^### The decider\$/,/^### The brief\$/p" "$APD/reference/decisions.md" | grep -q "general-purpose"'
check "pipeline: the dispatch of a decision still names no Orchestrator-type subagent" \
  '! sed -n "/^### The decider\$/,/^### The brief\$/p" "$APD/reference/decisions.md" | grep -q "wrangl:orchestrator"'

# autonomous-pipeline cadence: the tick procedure (docs/specs/orchestrator-cadence.md)
CAD="$APD/reference/cadence.md"
CADF="$(tr -s '\n ' '  ' < "$CAD" 2>/dev/null)"
check "cadence: reference/cadence.md exists" '[ -f "$CAD" ]'
check "cadence: the procedure names the rollup stamp and the git merge checks" \
  'BAD=; for p in "Tick:" "merge-base --is-ancestor" "rev-list --count"; do printf %s "$CADF" | grep -qF -- "$p" || BAD=1; done; [ -z "$BAD" ]'
check "cadence: the working hold and the warnings are named" \
  'BAD=; for p in "\`busy\`" "release.warnings"; do grep -qF -- "$p" "$CAD" 2>/dev/null || BAD=1; done; [ -z "$BAD" ]'
check "cadence: the first denied permission prompt stops closing for the tick" \
  'grep -qi "stop closing" "$CAD" 2>/dev/null && grep -qi "denied" "$CAD" 2>/dev/null'
check "cadence: the closed count compares a before and an after stream-status list" \
  'grep -qi "before" "$CAD" 2>/dev/null && grep -qi "after" "$CAD" 2>/dev/null && grep -qF "stream-status" "$CAD" 2>/dev/null'
check "cadence: one new stream per tick, through stream-open" \
  'grep -qi "one new stream per tick" "$CAD" 2>/dev/null && grep -qF "stream-open" "$CAD" 2>/dev/null'
check "cadence: the summary is never a PushNotification" \
  'grep -qE "(never|not)[^.]*PushNotification" "$CAD" 2>/dev/null'
check "cadence: --ignore-release is never passed by the tick" \
  'grep -qE "never[^.]*--ignore-release" "$CAD" 2>/dev/null'
check "cadence: a member is closed as soon as it is signed off, committed and idle, with no hour wait" \
  'printf %s "$CADF" | grep -qF "as soon as its work is signed off" && ! grep -qE "more than (60|90) minutes" "$CAD" 2>/dev/null'
check "cadence: signed off with uncommitted work is kept (nearest non-match of the close rule)" \
  'grep -qE "signed off, uncommitted work \| kept" "$CAD" 2>/dev/null'
check "cadence: idle with review open or rework due is kept (nearest non-match of signed off)" \
  'grep -qE "idle, review still open or rework due \| kept" "$CAD" 2>/dev/null'
check "cadence: N+1 goes to N's Implementor only when it depends on N or changes N's files, else a fresh spawn" \
  'printf %s "$CADF" | grep -qF "when N+1 depends on N or changes N'"'"'s files" && printf %s "$CADF" | grep -qF "otherwise N+1 gets a fresh spawn"'
check "cadence: the procedure stays short" '[ "$(wc -l < "$CAD" 2>/dev/null || echo 999)" -le 200 ]'
check "cadence: idle-since is idle_since[<member>] in the team row of ah roster teams --json, and null or missing keeps the member" \
  'printf %s "$CADF" | grep -qF "idle_since[<member>]" && printf %s "$CADF" | grep -qF "roster teams --json" && printf %s "$CADF" | grep -qiE "(null|missing)[^.]*keeps? the member"'
check "cadence: no reference to a hand-read activity file or a TUI screen remains" \
  '! printf %s "$CADF" | grep -qiE "<session id>.json|member detail|activity file"'
check "cadence: a null idle_since (working, no record) is kept (nearest non-match of the idle test)" \
  'printf %s "$CADF" | grep -qE "idle_since. null or missing[^|]*\| kept"'
check "cadence: the live list for close verification and the budget is teams --json plus the harness session list, not stream-status" \
  'printf %s "$CADF" | grep -qF "ListAgents" && printf %s "$CADF" | grep -qiE "not .stream-status., which omits members in no stream"'
check "cadence: a member counts as closed only when absent from both the team row and ListAgents, and a disagreement is reported" \
  'printf %s "$CADF" | grep -qF "absent from both the team row and \`ListAgents\`" && printf %s "$CADF" | grep -qiE "disagree, it is not closed"'
check "cadence: the final tick of a completed run (not plan) skips the buffer" \
  'printf %s "$CADF" | grep -qF "there is no wait to skip" && ! printf %s "$CADF" | grep -qF "completed plan"'
check "cadence: decision list is spelled in full" \
  'printf %s "$CADF" | grep -qF "<ah> msg decision list --team <team> --summary" && ! printf %s "$CADF" | grep -E "(^|[^g] )decision list" | grep -qv "<ah> msg decision list"'
check "cadence: the plan-complete exception widens only the scope, at the final tick, after the Completion gate passed" \
  'printf %s "$CADF" | grep -qF "Completion gate" && printf %s "$CADF" | grep -qi "final tick" && printf %s "$CADF" | grep -qiF "widens only the scope"'
check "cadence: plan complete leaves every other check in force and reports what stays up in the final message" \
  'grep -qiE "every other check" "$CAD" 2>/dev/null && grep -qi "final message" "$CAD" 2>/dev/null'
check "cadence: a failed or stopped gate, or a stopped item, is not plan complete (the buffer still applies)" \
  'grep -qiE "not plan complete" "$CAD" 2>/dev/null'
check "cadence: the tick states the standing confirmation and that a denied prompt is not retried in the tick" \
  'printf %s "$CADF" | grep -qi "standing directive"'

# autonomous-pipeline cadence: SKILL.md carries only the always-on rule
SK="$APD/SKILL.md"
check "cadence: SKILL.md names reference/cadence.md" 'grep -qF "reference/cadence.md" "$SK"'
check "cadence: SKILL.md has a ## Cadence heading before ## Bootstrap" \
  'c=$(grep -n "^## Cadence\$" "$SK" | head -1 | cut -d: -f1); b=$(grep -n "^## Bootstrap\$" "$SK" | head -1 | cut -d: -f1); [ -n "$c" ] && [ -n "$b" ] && [ "$c" -lt "$b" ]'
check "cadence: the Cadence section is short and carries the clauses" \
  'sec=$(sed -n "/^## Cadence\$/,/^## Bootstrap\$/p" "$SK"); [ "$(printf "%s\n" "$sec" | wc -l)" -le 26 ] && [ "$(printf "%s" "$sec" | wc -c)" -le 1400 ] && printf "%s" "$sec" | grep -qE "never[^.]*--ignore-release" && printf "%s" "$sec" | grep -qF "stream-open" && printf "%s" "$sec" | grep -qF "session-budget" && printf "%s" "$sec" | grep -qF "tick:"'
check "cadence: the Cadence section dismisses on sign-off and commit, with no hour wait, and leaves new work to the Capacity rule" \
  'sec=$(sed -n "/^## Cadence\$/,/^## Bootstrap\$/p" "$SK" | tr -s "\n " "  "); printf %s "$sec" | grep -qF "as soon as its work is signed off" && printf %s "$sec" | grep -qF "Capacity rule" && ! printf %s "$sec" | grep -qE "over 1(\.5)? hours?"'
check "cadence: the mid-task rule is kept and the tick exception follows it" \
  'tr -s "\n " "  " < "$SK" | grep -qF "dismiss or close a member mid-task" && tr -s "\n " "  " < "$SK" | grep -qF "the tick retires it (§ Cadence)"'
check "cadence: the tick line is a session message, not a notification, and the push list is unchanged" \
  'tr -s "\n " "  " < "$SK" | grep -qF "tick: line is a session message, not a notification" || tr -s "\n " "  " < "$SK" | grep -qF "\`tick:\` line is a session message, not a notification"'
RA="$APD/reference/run-anchor.md"
check "budget: run-anchor.md writes session-budget as a level and number" \
  'grep -qF "session-budget: <level> (<N>)" "$RA" && grep -qF "session-budget: med (12)" "$RA" && grep -qF "session-budget: max (uncapped)" "$RA"'
check "budget: run-anchor.md names all five levels with their numbers" \
  '( t=$(tr -s "\n " "  " < "$RA"); for l in "low = 8" "med = 12" "high = 16" "xhigh = 20" "max = uncapped"; do printf "%s" "$t" | grep -qF "$l" || exit 1; done )'
check "budget: run-anchor.md says med is the default and a missing line reads as med" \
  't=$(tr -s "\n " "  " < "$RA"); printf "%s" "$t" | grep -qiE "med[^.]*\(default\)|default[^.]*med" && printf "%s" "$t" | grep -qiE "without the line[^.]*med \(12\)"'
check "budget: run-anchor.md says the level word decides, not the number" \
  'tr -s "\n " "  " < "$RA" | grep -qiE "level word decides"'
check "budget: the old numeric default 8 is gone (nearest non-match: an anchor without the line is not 8)" \
  '! tr -s "\n " "  " < "$RA" | grep -qiE "without the line it is 8|session-budget: <N>"'
check "budget: invocation.md names --budget and the five levels" \
  'sec=$(cat "$APD/reference/invocation.md" | tr -s "\n " "  "); printf "%s" "$sec" | grep -qF -- "--budget low|med|high|xhigh|max"'
check "budget: invocation.md maps plain language, defaults to med, and refuses an unknown value" \
  'sec=$(cat "$APD/reference/invocation.md" | tr -s "\n " "  "); printf "%s" "$sec" | grep -qF "no session cap" && printf "%s" "$sec" | grep -qiE "no flag[^.]*med" && printf "%s" "$sec" | grep -qiE "unknown[^.]*refuse"'
check "budget: SKILL.md run-start notification names budget <level> (<N>)" \
  'sec=$(sed -n "/^## Notification\$/,/^## Invocation\$/p" "$SK" | tr -s "\n " "  "); printf "%s" "$sec" | grep -qF "budget <level> (<N>)"'
check "budget: cadence.md parallel step maps the five levels and reads a missing line as med" \
  '( t=$(tr -s "\n " "  " < "$CAD"); for l in "low 8" "med 12" "high 16" "xhigh 20" "max uncapped"; do printf "%s" "$t" | grep -qF "$l" || exit 1; done; printf "%s" "$t" | grep -qiE "missing[^.]*med" )'
check "budget: cadence.md says max removes only the budget cap and one new stream per tick still applies" \
  'tr -s "\n " "  " < "$CAD" | grep -qiE "max[^.]*no growth limit[^.]*one new stream per tick[^.]*still applies"'
check "budget: cadence.md summary examples use the med budget (live 7/12), not 7/8" \
  '! grep -qF "live 7/8" "$CAD" && grep -qF "live 7/12" "$CAD"'
check "budget: invocation.md says a number below 8 maps to low (nearest non-match of the at-or-below rule)" \
  'cat "$APD/reference/invocation.md" | tr -s "\n " "  " | grep -qiE "number below 8 is low"'
check "budget: the shipped spec copy is rev 4.4 and its summary examples say live 7/12" \
  'SP="$PLUGIN/docs/specs/orchestrator-cadence.md"; grep -qF "rev 4.4" "$SP" && grep -qF "live 7/12" "$SP" && ! grep -qF "live 7/8" "$SP"'
check "budget: cadence.md no longer says the default is 8" \
  '! tr -s "\n " "  " < "$CAD" | grep -qiE "default 8"'
check "cadence: the issue-run end-of-run points at the final tick and still never passes --ignore-release" \
  'tr -s "\n " "  " < "$APD/reference/issue-runs.md" | grep -qF "The final tick (\`reference/cadence.md\`) does this." && grep -qF "Never pass \`--ignore-release\`" "$APD/reference/issue-runs.md"'
ST="$PLUGIN/skills/agent-team/reference/streams.md"
check "cadence: streams.md allows the pipeline tick to dismiss and no longer says only when the user asks" \
  '! tr -s "\n " "  " < "$ST" | grep -qF "Dismiss members only when the user asks" && tr -s "\n " "  " < "$ST" | grep -qF "through the tick in \`skills/autonomous-pipeline/reference/cadence.md\`"'
check "cadence: streams.md sends every new parallel workstream to its own stream and tab" \
  'tr -s "\n " "  " < "$ST" | grep -qF "Every new parallel workstream gets its own stream, and so its own tab."'

# agent-roster
check "agent-roster: one cwd form (--cwd <abs cwd>), no \$(pwd)" \
  'grep -qF "with \`--cwd <abs cwd>\`" "$AR" && ! grep -qF "\$(pwd)" "$AR"'
check "agent-roster: the false \"CLI errors, run init first\" statement is gone and add auto-creates is kept" \
  '! grep -qF "the CLI errors pointing at \`init\`; run \`init\` first" "$AR" && grep -qF "\`add\` auto-creates a minimal roster when none exists" "$AR"'
check "agent-roster: the role question is batched at most 4 options per question" \
  'tr -s "\n " "  " < "$AR" | grep -qF "A question holds at most 4 options, so split the roles over as many questions as needed"'

check "agent-roster: edit and remove with no roster at any level are said to fail and name init" \
  'tr -s "\n " "  " < "$AR" | grep -qF "\`edit\` and \`remove\` with no roster resolving at any level fail and name \`init\`"'

# descriptions
check "descriptions: the orchestrator description is at most 200 bytes" \
  '[ "$(node -e "const t=require(\"fs\").readFileSync(process.argv[1],\"utf8\");const m=t.match(/description: >-\n((?:  .*\n)+)/);process.stdout.write(String(Buffer.byteLength(m[1].replace(/\n  /g,\" \").trim())))" "$PLUGIN/agents/orchestrator.md")" -le 200 ]'
check "descriptions: each skill description opens with a third-person verb, and each stays one frontmatter line" \
  'BAD=; for s in agent-team agent-roster agent-role autonomous-pipeline; do l=$(sed -n "/^description:/p" "$PLUGIN/skills/$s/SKILL.md"); [ "$(printf "%s\n" "$l" | wc -l | tr -d " ")" = 1 ] && printf "%s" "$l" | grep -qE "^description: [A-Z][a-z]+s[ ,]" || BAD=1; done; [ -z "$BAD" ]'
# task-runner: no message-file intake (nothing sends a message file to it; the msg gate exempts legwork)
check "task-runner: the body carries no message-file intake, and still carries the CLI line and the report rules" \
  '! grep -qF "BRIEF INTAKE" "$PLUGIN/agents/task-runner.md" && ! grep -qF "hierarchy-msg" "$PLUGIN/agents/task-runner.md" && grep -qF "the cli-tools.md path in your \`ah CLI root\` line" "$PLUGIN/agents/task-runner.md" && grep -qF "SMALLEST report" "$PLUGIN/agents/task-runner.md"'
check "task-runner: the other four chain roles keep their intake" \
  'BAD=; for r in architect implementor reviewer ultra-advisor; do grep -qF "BRIEF INTAKE" "$PLUGIN/agents/$r.md" || BAD=1; done; [ -z "$BAD" ]'

# agent-roster and agent-role split
ARD="$PLUGIN/skills/agent-roster"
ALD="$PLUGIN/skills/agent-role"
check "agent-roster: SKILL.md is at most 11264 bytes and the kinds reference exists, named by the skill" \
  '[ "$(wc -c < "$ARD/SKILL.md")" -le 11264 ] && [ -f "$ARD/reference/kinds.md" ] && grep -qF "reference/kinds.md" "$ARD/SKILL.md"'
check "agent-roster: the kinds reference keeps the herdr pane close remedy and the default-claude rule" \
  'grep -qF "herdr pane close <id>" "$ARD/reference/kinds.md" && grep -qF "Omitted means \`claude\`" "$ARD/reference/kinds.md"'
check "agent-roster: SKILL.md no longer carries the kind table" '! grep -qF "a kind with a model mapping" "$ARD/SKILL.md"'
check "agent-role: SKILL.md is at most 7168 bytes and the role-packs reference exists, named by the skill" \
  '[ "$(wc -c < "$ALD/SKILL.md")" -le 7168 ] && [ -f "$ALD/reference/role-packs.md" ] && grep -qF "reference/role-packs.md" "$ALD/SKILL.md"'
check "agent-role: the 32-character cap, the scoped-tool rule and the fix kinds stay in SKILL.md" \
  'grep -qF "32 characters" "$ALD/SKILL.md" && grep -qF "Bash(git:*)" "$ALD/SKILL.md" && grep -qF "edit-frontmatter" "$ALD/SKILL.md" && grep -qF "change-class" "$ALD/SKILL.md" && grep -qF "remove-file" "$ALD/SKILL.md"'
check "agent-role: the pack procedures live in the reference file, not SKILL.md" \
  'grep -qF "uninstall <plugin>" "$ALD/reference/role-packs.md" && ! grep -qF "claude plugin marketplace add" "$ALD/SKILL.md"'

# agent-role
check "agent-role: the edit field picker is batched at most 4 options" \
  'tr -s "\n " "  " < "$ARL" | grep -qF "in batches of at most 4 options"'

# agent-team split
ATD="$PLUGIN/skills/agent-team"
check "agent-team: SKILL.md is at most 16384 bytes and 350 lines" \
  '[ "$(wc -c < "$ATD/SKILL.md")" -le 16384 ] && [ "$(wc -l < "$ATD/SKILL.md")" -le 350 ]'
check "agent-team: every reference file over 100 lines has its Contents list within the first 15 lines" \
  'BAD=; for f in "$ATD"/reference/*.md; do [ "$(wc -l < "$f")" -le 100 ] || head -15 "$f" | grep -qx "Contents" || BAD=1; done; [ -z "$BAD" ]'
check "agent-team: no reference file links another (markdown link to a .md file)" \
  '! grep -lE "\]\([^)]*\.md" "$ATD"/reference/*.md | grep -q .'
check "agent-team: every reference file is named by the index in SKILL.md" \
  'BAD=; for f in "$ATD"/reference/*.md; do grep -qF "reference/$(basename "$f")" "$ATD/SKILL.md" || BAD=1; done; [ -z "$BAD" ]'
check "agent-team: no spec or finding citation in SKILL.md or its references" \
  '! grep -qiE "spec [0-9]{4}|pre-00[0-9]{2}" "$ATD/SKILL.md" "$ATD"/reference/*.md'
check "agent-team: the misplaced-peer respawn fallback is kept, with its guards, and SKILL.md names it" \
  'grep -qF "relocation fallback" "$AT" && grep -qF -- "--cwd <expected_root>" "$AT" && grep -qF "discards" "$AT" && grep -qF "misplaced_unattributed" "$AT" && grep -qF "Misplaced peer" "$ATD/SKILL.md"'
check "agent-team: the orphan adopt, the dismiss identifier forms and spawn-one-not-lighter clauses are kept" \
  'grep -qF "adopt --orchestrator-pid <pid>" "$AT" && grep -qF "role@sid8" "$ATD/reference/teardown.md" && grep -qF "not a lighter-weight alternative" "$AT"'
check "agent-team: an orphaned Team is adopted only on the user's word, through an AskUserQuestion with Adopt and Leave it" \
  'sed -n "/Recovering an orphaned Team/,/An orphan that live sessions depend on/p" "$ATD/reference/teardown.md" | grep -qF AskUserQuestion && sed -n "/Recovering an orphaned Team/,/An orphan that live sessions depend on/p" "$ATD/reference/teardown.md" | grep -qF "Adopt <team>" && sed -n "/Recovering an orphaned Team/,/An orphan that live sessions depend on/p" "$ATD/reference/teardown.md" | grep -qF "Leave it"'
check "agent-team: an orphan that live members depend on is adopted only after an AskUserQuestion; the self-adopt sentence is gone" \
  '! grep -qF "Adopt it yourself" "$ATD/reference/teardown.md" && sed -n "/An orphan that live sessions depend on/,/attributed_live. sessions: leave it/p" "$ATD/reference/teardown.md" | grep -qF "Adopt <team> (Recommended)"'
check "agent-team: the default team file is named in docs/team-file.md" \
  'grep -qF "the default team file is \`teams/<repo basename>.json\`" "$PLUGIN/docs/team-file.md"'

# findings sketch levels match what the validator emits
check "agent-role: the findings sketch lists exactly the levels the validator emits (warn, not warning)" \
  'LV=$(grep -rhoE "[^A-Za-z]Finding\(\"[a-z]+\"" "$PLUGIN/native/src" | sed -E "s/.*\(\"([a-z]+)\"/\1/" | sort -u | tr "\n" " "); [ "$LV" = "error warn " ] && grep -qF "{level: \"error\"|\"warn\"," "$PLUGIN/skills/agent-role/SKILL.md" && ! grep -qF "\"warning\"" "$PLUGIN/skills/agent-role/SKILL.md"'

# /hierarchy init and on: the propagation note says what a mid-session enable does, and when
check "hierarchy: the propagation note says this session's hooks pick up an enable from the next message" \
  'tr -s "\n " "  " < "$PLUGIN/commands/hierarchy.md" | grep -qF "This session'"'"'s hooks pick it up from your next message (the \`.claude/ah2\` directory, the \`ahd tui\` listing, state writes). The SessionStart guidance loads at a session'"'"'s next start, \`/clear\` or compaction, in this session too; run \`/clear\` if you want it now (it clears the conversation). Other live sessions pick everything up the same way."'
check "hierarchy: the old claim that an enable applies to this session immediately is gone, and the off wording stays" \
  '! grep -qF "applies to **this** session immediately" "$PLUGIN/commands/hierarchy.md" && grep -qF "goes fully silent" "$PLUGIN/commands/hierarchy.md" && grep -qF "from the next session start onward" "$PLUGIN/commands/hierarchy.md"'

# docs/daemon.md: says what needs the daemon, feature by feature, and never that everything works without it
DD="$PLUGIN/docs/daemon.md"
DDSEC=$(awk '/^## What it does and what happens without it/{f=1; next} f && /^## /{exit} f' "$DD")
check "daemon.md: the claim that every feature works without the daemon is gone" \
  '! grep -qF "every feature" <(tr -s "\n " "  " < "$DD" | grep -oF "every feature of agent-hierarchy works without it") && [ -n "$DDSEC" ]'
check "daemon.md: the what-it-does section holds a table whose header names needs and without it" \
  'grep -qiE "^\| *feature *\| *needs *\| *without it *\|" <<<"$DDSEC"'
check "daemon.md: the table names check-in, BLOCKED, resume, context, delivery ladder and the mod" \
  'for w in "check-in" "BLOCKED" "resume" "context" "delivery ladder" "mod"; do grep -qiF -- "$w" <<<"$DDSEC" || exit 1; done'
check "daemon.md: context alerts are listed as none in this release, and the 1.5 second restart budget is named" \
  'grep -qiE "^\| *context alerts *\|[^|]*\|[^|]*none in this release" <<<"$DDSEC" && grep -qF "1.5 seconds" <<<"$DDSEC"'

DDCTX=$(awk '/^## Context alerts/{f=1; next} f && /^## /{exit} f' "$DD")
check "daemon.md: the Context alerts section says no alert fires in this release and context is in tokens" \
  '[ -n "$DDCTX" ] && grep -qF "No context alert fires in this release" <<<"$DDCTX" && grep -qF "tokens" <<<"$DDCTX"'
check "docs: no page still lists context alerts among what the daemon raises (architecture.md, getting-started.md)" \
  '! grep -qF "raises context alerts" "$PLUGIN/docs/architecture.md" && ! grep -qF "custom agent runtimes, context alerts" "$PLUGIN/docs/getting-started.md"'

# The role-session notice text (a task-runner's carries no brief intake or response-file sentence; the chain roles keep theirs) is
# pinned by the unit table tests/contracts/units-c1a/lib-config/buildRoleSessionNotice.json, which native/tests/Ah.Config.Tests
# ConfigUnitTableTests runs against the native text builder: row task-runner-builtin has neither sentence, rows
# architect-builtin-no-config, architect-plain-agent-type and the reviewer/implementor rows carry both.

# The line numbers of each phrase in a file, in the order given: they must be found and ascending.
phrases_in_order() { local file=$1 prev=0 n p; shift; for p in "$@"; do n=$(grep -nF -- "$p" "$file" | head -1 | cut -d: -f1); [ -n "$n" ] && [ "$n" -gt "$prev" ] || return 1; prev=$n; done; }

# the invocation grammar lives in reference/invocation.md; SKILL.md keeps a short pointer
check "pipeline: the reading list names reference/invocation.md before parsing the invocation, and the file exists" \
  'grep -qF "\`reference/invocation.md\` before parsing the invocation" "$APD/SKILL.md" && [ -f "$APD/reference/invocation.md" ]'
check "pipeline: SKILL.md ## Invocation is at most 6 lines of body and points at invocation.md" \
  'sec=$(sed -n "/^## Invocation\$/,/^## Rules that hold everywhere\$/p" "$APD/SKILL.md" | sed "1d;\$d" | sed "/^\$/d"); [ "$(printf "%s\n" "$sec" | wc -l)" -le 6 ] && grep -qF "Read \`reference/invocation.md\` before parsing any invocation" <<<"$sec"'
check "pipeline: the moved invocation text keeps every form (path, refs, --labelled, --auto-merge, --budget, refusals)" \
  'INV=$(tr -s "\n " "  " < "$APD/reference/invocation.md"); for p in "/pipeline <path> [--branch <name>]" "an issue run over those issues" "/pipeline --labelled" "--auto-merge" "--budget low|med|high|xhigh|max" "Refuse with one line"; do grep -qF -- "$p" <<<"$INV" || exit 1; done'
check "pipeline: the invocation text is no longer in SKILL.md (nearest non-match)" \
  '! grep -qF "an issue run over those issues" "$APD/SKILL.md" && ! grep -qF "Refuse with one line" "$APD/SKILL.md"'

# authority: the bootstrap question, its reference, and the anchor line
AUTH_MD="$APD/reference/authority.md"
check "authority: SKILL.md step 3c asks one Authority question before 5a and points at reference/authority.md" \
  'FLAT=$(tr -s "\n " "  " < "$APD/SKILL.md"); grep -qF "Step 3c, authority:" <<<"$FLAT" && grep -qF "header \"Authority\", before step 5a; no answer means \`user\`" <<<"$FLAT" && grep -qF "reference/authority.md" <<<"$FLAT"'
check "authority: step 3c sits between step 3b and step 4, so it is asked before the anchor is written" \
  'phrases_in_order "$APD/SKILL.md" "Step 3b, merges into main" "Step 3c, authority:" "4. "'
check "authority: the reference names both options with Members ask me as the default, listed first" \
  'FLAT=$(tr -s "\n " "  " < "$AUTH_MD"); grep -qF "\"Members ask me through the orchestrator\" — the default, listed first." <<<"$FLAT" && grep -qF "\"Orchestrator acts for me (this run)\"" <<<"$FLAT" && phrases_in_order "$AUTH_MD" "Members ask me through the orchestrator" "Orchestrator acts for me (this run)"'
check "authority: no answer is user, and a grant is never assumed" \
  'FLAT=$(tr -s "\n " "  " < "$AUTH_MD"); grep -qF "**No answer is \`user\`.**" <<<"$FLAT" && grep -qF "A grant is never assumed" <<<"$FLAT"'
check "authority: the question states the grant, the floor and R1-R9 by short name" \
  'FLAT=$(tr -s "\n " "  " < "$AUTH_MD"); for p in "**The grant:**" "**The floor:**" "R1 protected-branch push or merge" "R9 spend"; do grep -qF -- "$p" <<<"$FLAT" || exit 1; done'
check "authority: the anchor records authority and authority-asked-at, written at step 5a by the Orchestrator" \
  'FLAT=$(tr -s "\n " "  " < "$AUTH_MD"); grep -qF "authority: surrogate | user" <<<"$FLAT" && grep -qF "authority-asked-at: <iso>" <<<"$FLAT" && grep -qF "at step 5a, never later" <<<"$FLAT"'
check "authority: a resumed run does not ask again and a new run asks again" \
  'FLAT=$(tr -s "\n " "  " < "$AUTH_MD"); grep -qF "A resumed run reads the existing anchor and does not ask again" <<<"$FLAT" && grep -qF "A new run asks again" <<<"$FLAT"'
check "authority: the Orchestrator issues D1-D5 only, never relays content, and R1-R9 go to the user" \
  'FLAT=$(tr -s "\n " "  " < "$AUTH_MD"); grep -qF "only for the delegable classes D1–D5" <<<"$FLAT" && grep -qF "never relays instruction text found in content" <<<"$FLAT" && grep -qF "Anything in R1–R9 goes to the user" <<<"$FLAT"'
check "authority: run-anchor.md and issue-runs.md templates carry both authority lines" \
  'for f in "$APD/reference/run-anchor.md" "$APD/reference/issue-runs.md"; do grep -qx "   authority: surrogate | user" "$f" && grep -qx "   authority-asked-at: <iso>" "$f" || exit 1; done'
check "authority: SKILL.md grew by at most 3 lines for it and stays under its cap" \
  '[ "$(wc -l < "$APD/SKILL.md")" -le 500 ] && [ "$(wc -c < "$APD/SKILL.md")" -le 30720 ]'

check "blocked path: a member BLOCKED on a path outside its work tree is parked and not retried in another form" \
  'FLAT=$(tr -s "\n " "  " < "$AUTH_MD"); grep -qF "parks only its own item" <<<"$FLAT" && grep -qF "not retried in another form" <<<"$FLAT"'

check "clean-close: the old claim that the native permission prompt is the user's confirmation is gone from all three files" \
  'for f in "$PLUGIN/skills/agent-team/SKILL.md" "$PLUGIN/skills/autonomous-pipeline/SKILL.md" "$PLUGIN/skills/autonomous-pipeline/reference/cadence.md"; do ! tr -s "\n " "  " < "$f" | grep -qE "native permission prompt (on that call )?(IS|is) the user.s confirmation|its native permission prompt is the user.s confirmation" || exit 1; done'

skill_text "$PLUGIN/skills/add-work-item" AWI
check "add-work-item: the name matches its directory and the description triggers on the user's plain words" \
  'BAD=; grep -qx "name: add-work-item" "$AWI" || BAD=1; D=$(grep -m1 "^description:" "$AWI"); for p in "add this" "new requirement" "we also need" "put on the backlog"; do grep -qF "\"$p\"" <<<"$D" || BAD=1; done; [ -z "$BAD" ]'
check "add-work-item: it searches before it adds, and an existing item takes a log entry instead" \
  'S=$(grep -n "work list --state open" "$AWI" | cut -d: -f1) && A=$(grep -n "work add " "$AWI" | cut -d: -f1) && [ "$S" -lt "$A" ] && grep -qF "skip step 2 and step 3" "$AWI" && grep -qF "this entry is all you write" "$AWI"'
check "add-work-item: the user's own words are the body, and the title carries source and date" \
  'grep -qF "the user'"'"'s own words, unedited" "$AWI" && grep -qF -- "--title \"From <source>, <YYYY-MM-DD>\" --body-file -" "$AWI"'
check "add-work-item: root unless named, external id only if given, one line back with the id" \
  'grep -qF "It goes at the root: pass \`--parent <id>\` only if the user named" "$AWI" && grep -qF "External id, only if given." "$AWI" && grep -qF "\`Added <id>: <title>\`" "$AWI"'
check "add-work-item: a requirement in passing fires it, each requirement gets an item, and a long body has an answer" \
  'D=$(grep -m1 "^description:" "$AWI"); grep -qF "in passing" <<<"$D" && grep -qF "one item each" "$AWI" && grep -qF "A body over 8 KiB is refused" "$AWI"'
# The removed ultra gate and the conduit gate's old hook name, in what a session or user reads
check "the removed ultra gate is gone from the /hierarchy hints and the hook description" \
  '! grep -E "^argument-hint|^\`/hierarchy \[" "$PLUGIN/commands/hierarchy.md" | grep -qF "gate [status" && ! grep -qiF "ultra gate" "$PLUGIN/hooks/registry.json"'
check "...and those two still name what remains (nearest non-match)" \
  'grep -qF "flow [auto|confirm]" "$PLUGIN/commands/hierarchy.md" && grep -qF "pretooluse-msg-gate" "$PLUGIN/hooks/registry.json"'
check "the pipeline skill names a hook that exists for the conduit rule" \
  '! grep -qF "pretooluse-conduit-gate" "$PLUGIN/skills/autonomous-pipeline/SKILL.md" && grep -qF "pretooluse-soft-rules" "$PLUGIN/native/src/Ah.Hooks/Gates/GateUnits.cs"'

check "worktree.md: probes are told HOME alone reaches the real daemon, and the home rule still stands" \
  'grep -qF "stops the real daemon" "$PLUGIN/skills/agent-team/reference/worktree.md" && grep -qF "Only the real daemon runs in the real home" "$PLUGIN/skills/agent-team/reference/worktree.md"'

check "add-work-item: the add carries a description written with write-ticket, and the note stays the source" \
  'grep -qF "work add \"<title>\" --body-file -" "$AWI" && grep -qF "written with the write-ticket skill" "$AWI" && grep -qF "the description is the rewrite, this is the source" "$AWI"'
check "add-work-item: the Orchestrator's agent file points at it" \
  'grep -qF "with the add-work-item skill" "$PLUGIN/agents/orchestrator.md"'

echo "----"
echo "SUMMARY: $PASS passed, $FAIL failed"
[ $FAIL -eq 0 ]
