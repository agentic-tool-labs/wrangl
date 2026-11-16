#!/bin/bash
# agent-hierarchy — the one open rule for exchanges: a member-addressed exchange is open until its
# response holds a report; an orchestrator-addressed one (the pipeline's run records, a role's
# request to the Orchestrator) closes on any response. Covers ah msg list, sweep, anchor liveness
# and the pipeline skill's record wording.
# HOME- and WRANGL_HIERARCHY_DIR-redirected; real state untouched.
# Usage: bash tests/test-exchange-open.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
unset WRANGL_TEAM_FILE  # every session roster launch carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it

PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
skill_text "$PLUGIN/skills/autonomous-pipeline" SKILL
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-exchange-open-test.XXXXXX")"
[ -n "$SANDBOX" ] && [ -d "$SANDBOX" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/proj"
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2"
HD="$SANDBOX/hier"
PASS=0; FAIL=0
TEAM="$(basename "$PROJ")"
ah_test_team "$HD" "$TEAM" architect
ah_test_prove_self orchestrator "$TEAM" "$HD/teams/$TEAM.json" > /dev/null  # this shell is the Orchestrator of the team the verbs below create
# <team>: the team file the current $HD holds for that team, for the verbs that run against $HD from here on.
orch_team() { ah_test_team "$HD" "$1" architect; ah_test_prove_self orchestrator "$1" "$HD/teams/$1.json" > /dev/null; }

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (OUT=${OUT:0:500})"; fi
}

msg() { OUT=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" ah_test_run_as_from "$TEAM" "$HD/teams/$TEAM.json" "$PLUGIN/bin/ah" msg --cwd "$PROJ" "$@" 2>&1); RC=$?; OUT=$(ah_drop_checklist_hint <<< "$OUT"); }
jget() { node -e 'const o=JSON.parse(process.argv[1]);const v=(new Function("o","return ("+process.argv[2]+")"))(o);process.stdout.write(typeof v==="string"?v:JSON.stringify(v))' "$OUT" "$1" 2>/dev/null; }
# <to> <from> <slug> [--team <t>]: a request; its id lands in ID and its path in REQ.
request() { msg new --to "$1" --from "$2" --slug "$3" --title "Fix test titles" "${@:4}"; ID=$(jget o.id); }
# <id> [--team <t>]: `ah msg new`'s bodyless (skeleton) response.
stub() { msg new --type response --id "$1" "${@:2}"; }
# <id> <from>: files the response's report.
fill() { msg fill --id "$1" --from "$2" --body '"- done: reported\n"'; }
# <id> [--open|--closed|--all]: that exchange's state in ah msg list, or empty when not listed.
state() { msg list "${2:---all}" --plain; echo "$OUT" | awk -v id="$1" '$1 == id { print $5 }'; }

# ---------------------------------------------------------------- AC 17: ah msg list
request architect orchestrator m-stub; M1=$ID; stub "$M1"
check "member-addressed request + skeleton stub → open" '[ "$(state "$M1")" = open ] && [ "$(state "$M1" --open)" = open ]'
fill "$M1" architect
check "the same exchange once its response is filled → closed" '[ "$(state "$M1")" = closed ] && [ -z "$(state "$M1" --open)" ]'

request orchestrator orchestrator rec-x; O1=$ID; stub "$O1"
check "orchestrator-addressed record + bodyless response → closed" '[ "$(state "$O1")" = closed ]'

request orchestrator architect role-ask --team "$TEAM"; R1=$ID; stub "$R1"
check "a role's request to the orchestrator + bodyless response → closed" '[ "$(state "$R1")" = closed ]'

request reviewer orchestrator m-open; M3=$ID; stub "$M3"
ids_in() { msg list "$1" --plain; echo "$OUT" | awk '{ print $1 }' | sort | tr '\n' ' '; }
ids_all_with() { msg list --all --plain; echo "$OUT" | awk -v s="$1" '$5 == s { print $1 }' | sort | tr '\n' ' '; }
check "--closed lists exactly the --all rows marked closed" '[ "$(ids_in --closed)" = "$(ids_all_with closed)" ] && [ -n "$(ids_all_with closed)" ]'
check "--open lists exactly the --all rows marked open" '[ "$(ids_in --open)" = "$(ids_all_with open)" ] && [ "$(ids_all_with open)" = "$M3 " ]'

# ---------------------------------------------------------------- AC 18: sweep
HD="$SANDBOX/hier-sweep"; orch_team "$TEAM"
OLD=$(node -e 'process.stdout.write(new Date(Date.now() - 8 * 86400 * 1000).toISOString())')
# <id> <to> <slug> <report line or empty>: an 8-day-old planted exchange from the Orchestrator; a response has an aged created stamp only by planting.
aged_pair() {
  local fm="parent: null"$'\n'"reason: null"$'\n'"eta: null"$'\n'"to_name: null"$'\n'"from_name: null"$'\n'"team: null"$'\n'"created: $OLD"
  printf -- '---\nid: %s\ntype: request\nto: %s\nfrom: orchestrator\nslug: %s\n%s\n---\n\n## [0] tldr\n- none\n' "$1" "$2" "$3" "$fm" > "$HD/msgs/$1--$2--$3--request.md"
  printf -- '---\nid: %s\ntype: response\nto: orchestrator\nfrom: %s\nslug: %s\n%s\n---\n\n## [1] status\n%s\n' "$1" "$2" "$3" "$fm" "$4" > "$HD/msgs/$1--orchestrator--$3--response.md"
  ah_seed_msgs "$HD"
}
mkdir -p "$HD/msgs"
S1=20260101-000000-s1aa; S2=20260101-000000-s2aa; S3=20260101-000000-s3aa
aged_pair "$S1" architect s-stub ""
aged_pair "$S2" architect s-filled "- done: reported"
aged_pair "$S3" orchestrator s-rec ""
msg sweep
archived() { [ "$(ah_msg_state "$1")" = archived ]; }
check "sweep: an 8-day-old stub-only member pair is not archived" '[ "$RC" = 0 ] && ! ah_msg_state "$S1" | grep -q archived'
check "sweep: an 8-day-old filled member pair is archived" 'archived "$S2"'
check "sweep: an 8-day-old stub-closed orchestrator record is archived" 'archived "$S3"'

# ---------------------------------------------------------------- AC 25: the skill's record wording
check "the pipeline skill never asks for a response body on a record close" '! grep -q "body \`closed:" "$SKILL" && ! grep -q "as the response body" "$SKILL"'
check "the pipeline skill gives the orchestrator-addressed record request command once" '[ "$(grep -c "<ah> msg new --type request --to orchestrator --from orchestrator" "$SKILL")" = 1 ]'

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
