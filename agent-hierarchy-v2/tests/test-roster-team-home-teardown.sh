#!/bin/bash
# agent-hierarchy — one team file per checkout, second half: the teardown cases from S8 on (stray re-checks, pool copies,
# dismiss, teams, a spawn into another checkout; spec 0072). The first half, test-roster-team-home.sh, holds placement and
# L1-S6; both share lib-roster-team-home.sh. Every case runs in a scratch repo with a linked worktree, a scratch HOME and a
# PATH herdr shim; no real herdr, claude or repo is touched.
# Usage: bash tests/test-roster-team-home-teardown.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-roster-team-home.sh"

# S8/S9 — a stray is closed only when a fresh query still shows it under the name it was matched by.
# list_calls_before_close: how many `agent list` calls precede the first pane close
list_calls_before_close() {
  ah_test_fake_herdr_calls "$STATE" | node -e 'let t="";process.stdin.on("data",d=>t+=d).on("end",()=>{let n=0;for(const l of t.split("\n").filter(x=>x)){if(l.includes("\"pane\",\"close\""))break;if(l.includes("\"agent\",\"list\""))n++}console.log(n)})'
}
stray_close() {  # <env words…>: plan, then close the one stray with those env words
  reset_state; setup_roster; put_agent myrepo-architect w1:p8 "$REPO"
  plan --cwd "$WT"; PTOKEN=$(jq_ 'o.close_token')
  ah_test_fake_herdr_reset_calls "$STATE"
  r CLAUDE_PID="$$" "$@" -- disband --close --confirm --ignore-release --plan-token "$PTOKEN" --cwd "$WT"
}
stray_close; STRAY_K=$(list_calls_before_close)
check "S8: baseline — an unchanged stray is closed and verified" '[ "$(jq_ o.closed)" = "true" ] && [ "$STRAY_K" -ge 1 ]'
stray_close FAKE_HERDR_LIST_FAIL_FROM="$STRAY_K"
check "S8: the re-check query fails -> the stray is not closed, with a warning" '[ "$(jq_ o.closed)" = "false" ] && [ "$(ah_test_fake_herdr_calls "$STATE" | grep -c "\"pane\",\"close\"")" = "0" ] && echo "$OUT" | grep -q "could not be re-checked"'
stray_close FAKE_HERDR_NAMELESS_FROM="$STRAY_K" FAKE_HERDR_NAMELESS_PANE=w1:p8
check "S9: the pane has lost its name but still holds the same agent session -> it is the same member, so it is closed" '[ "$(jq_ o.closed)" = "true" ] && [ "$(ah_test_fake_herdr_calls "$STATE" | grep -c "\"pane\",\"close\"")" = "1" ]'
stray_close FAKE_HERDR_NAMELESS_FROM="$STRAY_K" FAKE_HERDR_NAMELESS_PANE=w1:p8 FAKE_HERDR_NAMELESS_SESSION=session-other
check "S9: the pane has lost its name and holds another agent session -> the stray is not closed, with a warning" '[ "$(jq_ o.closed)" = "false" ] && [ "$(ah_test_fake_herdr_calls "$STATE" | grep -c "\"pane\",\"close\"")" = "0" ] && echo "$OUT" | grep -q "now carries (no name)"'

# V5 — no team record: a live peer with no pane keeps the close from reporting success
reset_state; setup_roster
mkdir -p "$HOME_WT"
put_agent demo-architect w1:p7 "$WT"
ah_test_harness_peer demo-architect demo; ARCH_PID=$AH_TEST_HARNESS_PID
ah_test_harness_peer demo-reviewer demo; REV_PID=$AH_TEST_HARNESS_PID
{ echo "{\"type\":\"peer\",\"status\":\"up\",\"name\":\"demo-architect\",\"role\":\"architect\",\"pid\":$ARCH_PID,\"pane_id\":\"w1:p7\",\"ts\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}"
  echo "{\"type\":\"peer\",\"status\":\"up\",\"name\":\"demo-reviewer\",\"role\":\"reviewer\",\"pid\":$REV_PID,\"ts\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}"; } > "$HOME_WT/peers.jsonl"
ah_clear_roster "$HOME_WT"; ah_flush_roster "$HOME_WT/peers.jsonl"
plan --team demo --cwd "$WT"; PTOKEN=$(jq_ 'o.close_token')
r CLAUDE_PID="$$" -- disband --close --confirm --ignore-release --plan-token "$PTOKEN" --team demo --cwd "$WT"
check "V5: no team record, one pane peer closed and one peer with no pane -> closed false, not_closable listed, W8" '[ "$(jq_ o.closed)" = "false" ] && [ "$(jq_ "o.not_closable.length")" = "1" ] && echo "$OUT" | grep -q "W8"'

# S7 — a pool copy owned by another live orchestrator is read and left alone
field_scenario; seed_peers
mkdir -p "$HOME_WT"
echo "{\"version\":1,\"team_id\":\"old\",\"created\":\"2026-01-01T00:00:00Z\",\"transport\":\"herdr\",\"orchestrator\":{\"pid\":$SLEEPER,\"pid_start\":\"$(ah_test_pid_start "$SLEEPER")\"},\"members\":[{\"role\":\"architect\",\"name\":\"demo-legacy-architect\",\"route\":\"peer\",\"transport_id\":\"w1:p99\"}]}" | ah_seed_team "$HOME_WT/teams/demo.json"
plan --team demo --cwd "$WT"
check "S7: a pool copy owned by another live orchestrator is skipped with a warning" 'echo "$OUT" | grep -q "belongs to another live orchestrator" && echo "$OUT" | grep -q "left by an older release" && ! echo "$OUT" | grep -q "demo-legacy-architect"'

# C6 — a split owned by this session: both copies are read; teardown covers both
field_scenario; seed_peers
mkdir -p "$HOME_WT"
echo "{\"version\":1,\"team_id\":\"old\",\"created\":\"2026-01-01T00:00:00Z\",\"transport\":\"herdr\",\"orchestrator\":{\"pid\":$$,\"pid_start\":\"$(ah_test_pid_start "$$")\"},\"members\":[{\"role\":\"architect\",\"name\":\"demo-legacy-architect\",\"route\":\"peer\",\"transport_id\":\"w1:p99\"}]}" | ah_seed_team "$HOME_WT/teams/demo.json"
plan --team demo --cwd "$WT"
check "C6: a split team's pool copy is read by teardown, with a warning" 'echo "$OUT" | grep -q "left by an older release" && echo "$OUT" | grep -q "demo-legacy-architect" && [ "$(jq_ "o.team_files.length")" = "2" ]'
reset_state; setup_roster; spawn architect - "$WT"
mkdir -p "$HOME_WT"; echo '{"version":1,"team_id":"old","created":"2026-01-01T00:00:00Z","transport":"herdr","orchestrator":{"pid":1},"members":[]}' | ah_seed_team "$HOME_WT/teams/demo.json"
spawn reviewer - "$WT"
check "C6: spawn warns when the team is split across two dirs" 'echo "$OUT" | grep -q "has records in"'

# D2 — a dismiss plan for a target with no pane lists it and raises W8; D3 — one with a pane does not
field_scenario; seed_peers
team_set "$HOME_MAIN/teams/demo.json" 't.transport="terminal"'
r CLAUDE_PID="$$" -- dismiss demo-architect --plan --cwd "$WT"
check "D2: team member on a terminal transport -> plan lists it in not_closable with W8, no next" '[ "$RC" -eq 0 ] && [ "$(jq_ o.expected)" = "0" ] && [ "$(jq_ "o.next===undefined")" = "true" ] && [ "$(jq_ "o.not_closable.map(m=>m.name).join()")" = "demo-architect" ] && [ "$(jq_ "o.warnings.filter(w=>w.startsWith(\"W8\")&&w.includes(\"demo-architect\")).length")" = "1" ]'
reset_state; setup_roster
mkdir -p "$HOME_WT"
ah_test_harness_peer demo-reviewer demo
echo "{\"type\":\"peer\",\"status\":\"up\",\"name\":\"demo-reviewer\",\"role\":\"reviewer\",\"pid\":$AH_TEST_HARNESS_PID,\"ts\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}" > "$HOME_WT/peers.jsonl"
ah_clear_roster "$HOME_WT"; ah_flush_roster "$HOME_WT/peers.jsonl"
r CLAUDE_PID="$$" -- dismiss demo-reviewer --plan --cwd "$WT"
check "D2: live peer record with no pane_id -> plan lists it in not_closable with W8, no next" '[ "$RC" -eq 0 ] && [ "$(jq_ o.expected)" = "0" ] && [ "$(jq_ "o.next===undefined")" = "true" ] && [ "$(jq_ "o.not_closable.map(m=>m.name).join()")" = "demo-reviewer" ] && [ "$(jq_ "o.warnings.filter(w=>w.startsWith(\"W8\")&&w.includes(\"demo-reviewer\")).length")" = "1" ]'
field_scenario; seed_peers
r CLAUDE_PID="$$" -- dismiss demo-architect --plan --cwd "$WT"
check "D3: a member with a pane -> not_closable empty, no W8" '[ "$RC" -eq 0 ] && [ "$(jq_ "o.not_closable.length")" = "0" ] && ! echo "$OUT" | grep -q "W8"'

# D4 — dismiss --close on a terminal-transport member that has a transport_id still has no pane to close
field_scenario; seed_peers
team_set "$HOME_MAIN/teams/demo.json" 't.transport="terminal"'
ah_test_fake_herdr_reset_calls "$STATE"
r CLAUDE_PID="$$" -- dismiss demo-architect --close --confirm --plan-token x --cwd "$WT"
check "D4: terminal member with a pane id -> dismiss --close fails with exit 2, no addressable pane, nothing closed" '[ "$RC" -eq 2 ] && echo "$OUT" | grep -q "no addressable pane" && [ "$(closed_panes)" = "0" ]'

# D1 — dismiss a stray
reset_state; setup_roster; put_agent myrepo-architect w1:p8 "$REPO"
r -- dismiss myrepo-architect --ignore-release --cwd "$WT"
DTOKEN=$(jq_ 'o.close_token')
r -- dismiss myrepo-architect --close --confirm --ignore-release --plan-token "$DTOKEN" --cwd "$WT"
check "D1: dismiss closes a stray and verifies it gone" '[ "$(jq_ o.closed)" = "true" ] && [ "$(jq_ "o.still_live.length")" = "0" ]'

# T1 — teams from the worktree sees the team
field_scenario; seed_peers
r CLAUDE_PID="$$" -- teams --cwd "$WT"
check "T1: teams from the worktree lists the team and its five members" '[ "$(jq_ "o.teams.length")" = "1" ] && [ "$(jq_ "o.teams[0].name")" = "demo" ] && [ "$(jq_ "o.teams[0].members")" = "5" ]'

# C7 — spawning into another checkout than the caller's is warned about, never blocked
reset_state; setup_roster
(cd "$REPO" && env HOME="$T/home" HERDR_ENV=1 $HERDR_PANE_VARS PATH="$T/bin:$T/nolaunch:$NODE_DIR:/usr/bin:/bin" FAKE_STATE_DIR="$STATE" FAKE_HERDR_AGENT_CWD="$OTHER" "$PLUGIN/bin/ah" roster spawn-one architect --team demo --orchestrator-pid "$$" --cwd "$OTHER" > "$T/c7.out" 2>&1); RC=$?; OUT=$(cat "$T/c7.out")
check "C7: a spawn into another checkout warns and still spawns" '[ "$RC" -eq 0 ] && echo "$OUT" | grep -q "\"spawned\": true" && echo "$OUT" | grep -q "will not see it"'

echo
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
