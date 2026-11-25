#!/bin/bash
# agent-hierarchy — one team file per checkout: the team file is the master list for spawn,
# disband, dismiss and teams (spec 0072). Every case runs in a scratch repo with a linked
# worktree, a scratch HOME and a PATH herdr shim; no real herdr, claude or repo is touched.
# This is the placement half (spawn, create, whoami, member roots); the teardown half is
# test-roster-team-home-teardown.sh, and both share lib-roster-team-home.sh.
# Usage: bash tests/test-roster-team-home.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-roster-team-home.sh"

# ==== C1 — spawn-one from a worktree records the team in the main checkout's hierarchy dir ====
reset_state; setup_roster
spawn architect - "$WT"
check "C1: spawn-one --cwd worktree -> exit 0, spawned" '[ "$RC" -eq 0 ] && echo "$OUT" | grep -q "\"spawned\": true"'
check "C1: team file is under the main checkout's hierarchy dir" 'ah_team_stored "$HOME_MAIN/teams/demo.json"'
check "C1: no team file under the worktree's hierarchy dir" '! ah_team_stored "$HOME_WT/teams/demo.json"'
check "C1: the launch's WRANGL_TEAM_FILE is the home path" '[ "$(launch_env demo-architect WRANGL_TEAM_FILE)" = "$HOME_MAIN/teams/demo.json" ]'
check "C1: a member the launch did not name has no launch env (nearest non-match)" '[ -z "$(launch_env demo-reviewer WRANGL_TEAM_FILE)" ]'

# ==== C2 — create --spawn then --commit from a worktree: the same home ====
reset_state; setup_roster
r FAKE_HERDR_AGENT_CWD="$WT" -- create --spawn --mode auto --team demo --cwd "$WT"
SPAWN_RC=$RC
NAMES=$(jq_ 'o.members.map(m=>m.name).join(",")')
check "C2: create --spawn exits 0" '[ "$SPAWN_RC" -eq 0 ]'
V=$(node -e 'console.log(JSON.stringify(process.argv[1].split(",")))' "$NAMES")
r -- create --commit --verified "$V" --transport herdr --roster-level global --team demo --orchestrator-pid "$$" --cwd "$WT"
check "C2: create --commit exits 0" '[ "$RC" -eq 0 ]'
check "C2: committed team file is in the main checkout's hierarchy dir" 'ah_team_stored "$HOME_MAIN/teams/demo.json" && ! ah_team_stored "$HOME_WT/teams/demo.json"'

# ==== C3 — WRANGL_HIERARCHY_DIR wins, unchanged ====
reset_state; setup_roster
r WRANGL_HIERARCHY_DIR="$T/pinned" FAKE_HERDR_AGENT_CWD="$WT" -- spawn-one architect --team demo --orchestrator-pid "$$" --cwd "$WT"
check "C3: team file is under WRANGL_HIERARCHY_DIR" 'ah_team_stored "$T/pinned/teams/demo.json" && ! ah_team_stored "$HOME_MAIN/teams/demo.json" && ! ah_team_stored "$HOME_WT/teams/demo.json"'

# ==== C4 — a plain checkout keeps its own dir ====
reset_state; setup_roster
spawn architect - "$OTHER"
check "C4: plain repo -> its own hierarchy dir" 'ah_team_stored "$OTHER/.claude/ah2/teams/demo.json"'

# ==== C5 — a legacy team that lives only in the worktree pool is updated in place ====
reset_state; setup_roster
spawn architect - "$WT"
mkdir -p "$HOME_WT"; ah_team_doc "$HOME_MAIN/teams/demo.json" | ah_seed_team "$HOME_WT/teams/demo.json"; ah_forget_team "$HOME_MAIN/teams/demo.json"
spawn reviewer - "$WT"
check "C5: legacy worktree team gains the member in place" '[ "$(members_of "$HOME_WT/teams/demo.json")" = "demo-architect,demo-reviewer" ]'
check "C5: no team file is created in the main checkout" '! ah_team_stored "$HOME_MAIN/teams/demo.json"'

# ==== A1 — a worktree peer launched with the home's WRANGL_TEAM_FILE still resolves its team ====
reset_state; setup_roster
spawn architect - "$WT"
r HERDR_PANE_ID="$(pane_of demo-architect)" WRANGL_TEAM_FILE="$HOME_MAIN/teams/demo.json" -- whoami --cwd "$WT"
check "A1: whoami from the worktree names the member via env" 'echo "$OUT" | grep -q "\"name\": \"demo-architect\"" && echo "$OUT" | grep -q "\"answered_by\": \"env\""'

# ==== M1/M2 — each member records its own root, so a main-checkout member of a worktree-created
# team is not "misplaced" ====
# ss_run <cwd> <agent_type> <pane> <session>: SessionStart for a launched member, under a claude-named parent; that parent's pid is
# what the member's row records.
ss_run() {
  echo "{\"session_id\":\"$4\",\"cwd\":\"$1\",\"agent_type\":\"$2\",\"hook_event_name\":\"SessionStart\",\"source\":\"startup\"}" > "$T/ss_in.json"
  ${SS_HARNESS:-ah_test_in_harness} env HOME="$T/home" HERDR_PANE_ID="$3" WRANGL_TEAM_FILE="$HOME_MAIN/teams/demo.json" ${SS_ENV:+"$SS_ENV"} PATH="$T/bin:$T/nolaunch:$NODE_DIR:/usr/bin:/bin" FAKE_STATE_DIR="$STATE" sh "$PLUGIN/bin/ah-hook" sessionstart < "$T/ss_in.json" > "$T/ss_out.json" 2>&1
  SS_RC=$?; SS_OUT=$(cat "$T/ss_out.json")
}
# <dir>/peers.jsonl <session>: that session's newest roster row's misplaced flag, read from the dir's roster rows
last_row_misplaced() { ah_roster_rows "$(dirname "$1")" | node -e 'const L=require("fs").readFileSync(0,"utf8").trim().split("\n").filter(Boolean).map(JSON.parse).filter(r=>r.session_id===process.argv[1]);const r=L[L.length-1];console.log(r?String(r.misplaced):"norow")' "$2"; }

reset_state; setup_roster
spawn architect - "$WT"; spawn reviewer - "$REPO"
RPANE=$(pane_of demo-reviewer)
check "M1: the main-checkout member's row records its own root" '[ "$(ah_team_query "$HOME_MAIN/teams/demo.json" "t.members.find(m=>m.name===\"demo-reviewer\").expected_root")" = "$REPO" ]'
SS_HARNESS=ah_test_in_harness_alive ss_run "$REPO" wrangl:reviewer "$RPANE" m1-sess  # the checkin below is this process, so it must still be there
check "M1: SessionStart in the member's own checkout is not misplaced" '[ "$SS_RC" -eq 0 ] && [ "$(last_row_misplaced "$HOME_MAIN/peers.jsonl" m1-sess)" = "false" ] && ! echo "$SS_OUT" | grep -q "Misplaced:"'
RPID=$(ah_team_query "$HOME_MAIN/teams/demo.json" 't.members.find(m=>m.name==="demo-reviewer").pid')
env HOME="$T/home" HERDR_PANE_ID="$RPANE" CLAUDE_PID="$RPID" WRANGL_TEAM_FILE="$HOME_MAIN/teams/demo.json" PATH="$T/bin:$T/nolaunch:$NODE_DIR:/usr/bin:/bin" "$PLUGIN/bin/ah" roster checkin --cwd "$REPO" > "$T/ci.json" 2>&1; RC=$?; OUT=$(cat "$T/ci.json")
check "M1: checkin from the member's own checkout exits 0, misplaced false" '[ "$RC" -eq 0 ] && echo "$OUT" | grep -q "\"misplaced\": false"'
r CLAUDE_PID=$$ -- teams --cwd "$REPO"
check "M1: teams lists no misplaced member" '[ "$(jq_ "o.teams.find(t=>t.name===\"demo\").misplaced_members.length")" = "0" ]'
echo "{\"session_id\":\"m1-sess\",\"cwd\":\"$REPO\",\"agent_type\":\"wrangl:reviewer\",\"tool_name\":\"EnterWorktree\",\"tool_input\":{\"path\":\"$WT\"}}" > "$T/gate_in.json"
GOUT=$(env HOME="$T/home" HERDR_PANE_ID="$RPANE" WRANGL_TEAM_FILE="$HOME_MAIN/teams/demo.json" sh "$PLUGIN/bin/ah-hook" pretooluse-soft-rules < "$T/gate_in.json" 2>&1)
check "M1: the worktree gate does not offer relocation to the team-level root" 'echo "$GOUT" | grep -q "\"deny\""'

# M2: a member row without expected_root falls back to the team's root, as before
team_set "$HOME_MAIN/teams/demo.json" 'for(const m of t.members)delete m.expected_root'
ss_run "$REPO" wrangl:reviewer "$RPANE" m2-sess
check "M2: no member root -> the team's root applies (a main-checkout session of a worktree-rooted team is misplaced)" '[ "$(last_row_misplaced "$HOME_MAIN/peers.jsonl" m2-sess)" = "true" ]'

# M3: the launch hands the session its root, so the first SessionStart (no member row for its pane
# yet) is not misplaced; the team-level root is the worktree
SS_ENV="WRANGL_EXPECTED_ROOT=$REPO" ss_run "$REPO" wrangl:reviewer w1:p99 m3-sess
check "M3: launch-injected root, no member row -> not misplaced" '[ "$(last_row_misplaced "$HOME_MAIN/peers.jsonl" m3-sess)" = "false" ] && ! echo "$SS_OUT" | grep -q "Misplaced:"'
SS_ENV="WRANGL_EXPECTED_ROOT=$WT" ss_run "$REPO" wrangl:reviewer w1:p99 m3c-sess
check "M3: the injected root is what is compared (a mismatch is still misplaced)" '[ "$(last_row_misplaced "$HOME_MAIN/peers.jsonl" m3c-sess)" = "true" ]'
SS_ENV=
# M3b: spawn-one's launch settings carry the launch cwd as WRANGL_EXPECTED_ROOT
reset_state; setup_roster
spawn architect - "$WT"
check "M3b: the launch settings carry WRANGL_EXPECTED_ROOT = the launch cwd" '[ "$(launch_env demo-architect WRANGL_EXPECTED_ROOT)" = "$WT" ]'

# L1 — the field scenario: the plan from the worktree and from main both hold all five
field_scenario; seed_peers
plan --team demo --cwd "$WT"
check "L1: plan from the worktree closes all five members" '[ "$(close_names)" = "$FIVE" ] && [ "$(jq_ o.expected)" = "5" ]'
plan --team demo --cwd "$REPO"
check "L1: plan from the main checkout closes all five members" '[ "$(close_names)" = "$FIVE" ] && [ "$(jq_ o.expected)" = "5" ]'
check "L1: the plan names the team file it read" '[ "$(jq_ "o.team_files[0]")" = "$HOME_MAIN/teams/demo.json" ]'

# L2 + V1 — close from the worktree: five pane closes, verified
CLOSE_ENV=(); ah_test_fake_herdr_reset_calls "$STATE"
closecmd --team demo --cwd "$WT"
check "L2: close from the worktree closes all five, verified" '[ "$RC" -eq 0 ] && [ "$(jq_ o.closed)" = "true" ] && [ "$(jq_ o.closed_count)" = "5" ] && [ "$(jq_ "o.still_live.length")" = "0" ] && [ "$(closed_panes)" = "5" ]'
check "V1: a verified close leaves no warning and removes the team file" '[ "$(jq_ "o.warnings.length")" = "0" ] && ! ah_team_stored "$HOME_MAIN/teams/demo.json"'

# V2 — pane close succeeds but the agent stays listed
field_scenario; seed_peers
CLOSE_ENV=(FAKE_HERDR_CLOSE_NOOP=1)
closecmd --team demo --cwd "$WT"
check "V2: a close that leaves the agents listed is not reported closed" '[ "$RC" -eq 0 ] && [ "$(jq_ o.closed)" = "false" ] && [ "$(jq_ "o.still_live.length")" = "5" ] && echo "$OUT" | grep -q "still live"'
CLOSE_ENV=()

# V3 — the verify query fails
field_scenario; seed_peers
CLOSE_ENV=()
plan --team demo --cwd "$WT"; PTOKEN=$(jq_ 'o.close_token')
r CLAUDE_PID="$$" FAKE_HERDR_LIST_FAIL=1 -- disband --close --confirm --ignore-release --plan-token "$PTOKEN" --team demo --cwd "$WT"
check "V3: an unqueryable transport leaves the close unverified, with a warning" '[ "$(jq_ o.closed)" = "false" ] && echo "$OUT" | grep -q "could not be queried"'

# V4 — a team of terminal-transport members has nothing to close: not closed, not silently ok
field_scenario; seed_peers
team_set "$HOME_MAIN/teams/demo.json" 't.transport="terminal"'
CLOSE_ENV=()
plan --team demo --cwd "$WT"; PTOKEN=$(jq_ 'o.close_token')
check "V4: the plan raises W8 too" 'echo "$OUT" | grep -q "W8: 5 members cannot be closed from here"'
r CLAUDE_PID="$$" -- disband --close --confirm --ignore-release --plan-token "$PTOKEN" --team demo --cwd "$WT"
check "V4: terminal-only team -> closed false, members listed in not_closable, W8 raised" '[ "$(jq_ o.closed)" = "false" ] && [ "$(jq_ "o.not_closable.length")" = "5" ] && echo "$OUT" | grep -q "W8: 5 members cannot be closed from here"'

# W-1 — live peers, no team record in either dir
reset_state; setup_roster
mkdir -p "$HOME_WT"
ah_test_harness_peer demo-architect demo
echo "{\"type\":\"peer\",\"status\":\"up\",\"name\":\"demo-architect\",\"role\":\"architect\",\"pid\":$AH_TEST_HARNESS_PID,\"pane_id\":\"w1:p7\",\"ts\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}" > "$HOME_WT/peers.jsonl"
ah_clear_roster "$HOME_WT"; ah_flush_roster "$HOME_WT/peers.jsonl"
plan --team demo --cwd "$WT"
check "W-1: live peers with no team record are warned about, not reported absent" 'echo "$OUT" | grep -q "No team record for demo" && [ "$(jq_ o.expected)" = "1" ]'

# S1-S5 — strays: herdr agents named for this checkout that no team file names
reset_state; setup_roster
put_agent myrepo-architect w1:p8 "$REPO"
plan --cwd "$WT"
check "S1: a stray named for the checkout is in the close set, flagged, with the residual-risk warning" '[ "$(close_names)" = "myrepo-architect" ] && [ "$(jq_ "o.strays[0]")" = "myrepo-architect" ] && echo "$OUT" | grep -q "matched by name and checkout only"'
reset_state; setup_roster; put_agent myrepo-orchestrator w1:p8 "$REPO"
plan --cwd "$WT"
check "S2: another orchestrator is never a stray" '[ "$(jq_ o.expected)" = "0" ] || echo "$OUT" | grep -q "no active team and no live peers"'
reset_state; setup_roster; put_agent other-architect w1:p8 "$REPO"; put_agent notahierarchyname w1:p9 "$REPO"
plan --cwd "$WT"
check "S3: another prefix or an unparseable name is not matched" 'echo "$OUT" | grep -q "no active team and no live peers"'
reset_state; setup_roster; put_agent myrepo-architect w1:p8 "$OTHER"
plan --cwd "$WT"
check "S4: a same-named agent in another repo is not matched" 'echo "$OUT" | grep -q "no active team and no live peers"'
reset_state; setup_roster; put_agent myrepo-architect w1:p8 "$REPO"
mkdir -p "$HOME_MAIN"
echo "{\"version\":1,\"team_id\":\"t9\",\"created\":\"2026-01-01T00:00:00Z\",\"transport\":\"herdr\",\"orchestrator\":{\"pid\":$$},\"members\":[{\"role\":\"architect\",\"name\":\"myrepo-architect\",\"route\":\"peer\",\"transport_id\":\"w1:p8\"}]}" | ah_seed_team "$HOME_MAIN/teams/other.json"
plan --cwd "$WT"
check "S5: an agent that is a member of another team file is not a stray" 'echo "$OUT" | grep -q "no active team and no live peers"'

# S6 — an agent renamed between plan and close is left alone
field_scenario; seed_peers
plan --team demo --cwd "$WT"; PTOKEN=$(jq_ 'o.close_token')
rename_agent "$(pane_of demo-reviewer)" other-reviewer
r CLAUDE_PID="$$" -- disband --close --confirm --ignore-release --plan-token "$PTOKEN" --team demo --cwd "$WT"
check "S6: a pane that now carries another agent's name is not closed, and says so" '[ "$(jq_ o.closed)" = "false" ] && echo "$OUT" | grep -q "now carries other-reviewer" && [ "$(jq_ o.closed_count)" = "4" ]'

echo
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
