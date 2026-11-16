#!/bin/bash
# agent-hierarchy — spec 0018 (orchestrator identity, now resolved entirely on the Bash path
# per spec 0048 §2.3): Bash-path
# refusals for the two write sites (create --commit is covered in test-roster-cli.sh;
# spawn-one is covered here), and the `adopt` recovery verb / hijack guard (§5).
# HOME-redirected; real state untouched.
# Usage: bash tests/test-orchestrator-identity.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
H="$PLUGIN/hooks"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-orch-identity-test.XXXXXX")"
export AH_TEST_FAKE_BIN="$SANDBOX/nolaunch"
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
# No test may reach the real herdr or tmux: a stub that fails every call sits first on PATH, and
# the session's pane environment is dropped. A wrapper that sets PATH to its own fakes still wins.
mkdir -p "$SANDBOX/nolaunch"; printf '#!/bin/sh\nexit 1\n' > "$SANDBOX/nolaunch/herdr"; cp "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"; chmod +x "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"
export PATH="$SANDBOX/nolaunch:$PATH"; unset WRANGL_TEAM_FILE
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/myrepo"
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2"
(cd "$PROJ" && git init -q)
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:400})"; fi
}

run() { OUT=$(env -u CLAUDE_PID HOME="$FAKEHOME" "$PLUGIN/bin/ah" roster "$@" --cwd "$PROJ" 2>&1); RC=$?; }

TEAM_FILE="$PROJ/.claude/ah2/team.json"

run init --level repo --route peer
run add --no-spawn --level repo --role architect --model opus

# A shell is the Orchestrator only when a registration under AH_HOME records its pid and start time. $UNPROVEN_HOME holds none, so a
# command run under it is the shell of a session nobody can prove.
UNPROVEN_HOME="$SANDBOX/ah-unproven"; mkdir -p "$UNPROVEN_HOME"
run_unproven() { OUT=$(env -u CLAUDE_PID AH_HOME="$UNPROVEN_HOME" HOME="$FAKEHOME" "$PLUGIN/bin/ah" roster "$@" --cwd "$PROJ" 2>&1); RC=$?; }

# ---- spawn-one (Bash path): an unproven shell, no existing team -> refuses owner-unproven, exit 2, no team.json written
ah_forget_team "$TEAM_FILE"
run_unproven spawn-one architect
check "spawn-one: unproven shell, new team -> exit 2" '[ "$RC" -eq 2 ]'
check "spawn-one: unproven shell -> refused as owner-unproven" 'echo "$OUT" | grep -q "owner-unproven"'
check "spawn-one: unproven shell -> no team.json written" '! ah_team_stored "$TEAM_FILE"'

# ---- spawn-one: a proven shell naming a dead --orchestrator-pid -> refuses as a pid mismatch, no team.json
ah_test_prove_self orchestrator "$(basename "$PROJ")" > /dev/null
OUT=$(env -u CLAUDE_PID HOME="$FAKEHOME" "$PLUGIN/bin/ah" roster spawn-one architect --orchestrator-pid 99999999 --cwd "$PROJ" 2>&1); RC=$?
check "spawn-one: dead --orchestrator-pid -> exit 2" '[ "$RC" -eq 2 ]'
check "spawn-one: dead --orchestrator-pid -> refused as orchestrator-pid-mismatch" 'echo "$OUT" | grep -q "orchestrator-pid-mismatch"'
check "spawn-one: dead --orchestrator-pid -> no team.json written" '! ah_team_stored "$TEAM_FILE"'

# ---- every CLI write site proves its caller the same way. An unproven shell is refused at each, and the refusal names the
# missing proof rather than a pid variable.
ah_forget_team "$TEAM_FILE"
run_unproven create --commit --transport tmux --roster-level repo --verified '["myrepo-architect"]'
check "create --commit: unproven shell -> exit non-zero" '[ "$RC" -ne 0 ]'
check "create --commit: unproven shell -> refused as owner-unproven" 'echo "$OUT" | grep -q "owner-unproven"'
check "create --commit: unproven shell -> no team.json written" '! ah_team_stored "$TEAM_FILE"'

run_unproven checkin
check "checkin: unproven shell -> exit non-zero, naming CLAUDE_PID" '[ "$RC" -ne 0 ] && echo "$OUT" | grep -q "CLAUDE_PID is unset"'
check "checkin: unproven shell -> no team.json written" '! ah_team_stored "$TEAM_FILE"'

# ---- `adopt` recovery verb
# Orphaned (null-owner) team, hand-written directly — no CLI path can produce this shape any more,
# but it is exactly what an older commit left behind.
# <team id> <orchestrator pid or null> <members json> [<pid_start>]: writes the team file at $TEAM_FILE the way an older commit left it; a
# recorded start lets the owner's death be proven.
seed_team() {
  node -e 'const [id,pid,members,start]=process.argv.slice(1);const orchestrator={session_id:null,pid:pid==="null"?null:Number(pid)};if(start)orchestrator.pid_start=start;console.log(JSON.stringify({version:1,team_id:id,created:new Date().toISOString(),roster_level:"repo",transport:"terminal",orchestrator,members:JSON.parse(members),partial:false}))' "$1" "$2" "$3" "${4:-}" | ah_seed_team "$TEAM_FILE"
}
owner_pid() { ah_team_query "$TEAM_FILE" 'String(t.orchestrator.pid)'; }
seed_team orphan-1 null '[{"role":"architect","name":"myrepo-architect"}]'
BEFORE_MEMBERS=$(ah_team_query "$TEAM_FILE" 't.members')
run_unproven adopt --orchestrator-pid "$$"
check "adopt: unproven shell -> refused as owner-unproven" '[ "$RC" -eq 2 ] && echo "$OUT" | grep -q "owner-unproven"'
check "adopt: unproven shell -> orchestrator.pid still null" '[ "$(owner_pid)" = null ]'
run adopt --orchestrator-pid "$$"
check "adopt: orphan (null owner), proven shell -> succeeds" '[ "$RC" -eq 0 ]'
check "adopt: orphan -> orchestrator.pid re-stamped to the proven shell" '[ "$(owner_pid)" = "$$" ]'
AFTER_MEMBERS=$(ah_team_query "$TEAM_FILE" 't.members')
check "adopt: members byte-identical before/after" '[ "$BEFORE_MEMBERS" = "$AFTER_MEMBERS" ]'
check "adopt: team_id unchanged" '[ "$(ah_team_query "$TEAM_FILE" t.team_id)" = "orphan-1" ]'

# ---- adopt: recorded owner alive and different -> hijack guard refuses, before any write
ah_test_harness_peer; OWNER_PID=$AH_TEST_HARNESS_PID
( sleep 30 ) & OTHER_PID=$!
seed_team owned-1 "$OWNER_PID" '[]' "$(ah_test_pid_start "$OWNER_PID")"
run adopt --orchestrator-pid "$$"
check "adopt: recorded owner alive and different -> refused" '[ "$RC" -eq 2 ] && echo "$OUT" | grep -q "team is owned by live pid"'
check "adopt: hijack guard -> orchestrator.pid unchanged" '[ "$(owner_pid)" = "$OWNER_PID" ]'
run adopt --orchestrator-pid "$OTHER_PID"
check "adopt: --orchestrator-pid naming another live pid -> refused as orchestrator-pid-mismatch" '[ "$RC" -eq 2 ] && echo "$OUT" | grep -q "orchestrator-pid-mismatch"'
check "adopt: pid mismatch -> orchestrator.pid unchanged" '[ "$(owner_pid)" = "$OWNER_PID" ]'
ah_signal_child TERM "$OTHER_PID"

# ---- adopt: recorded owner dead -> allowed
( sleep 30 ) & DEAD_PID=$!
DEAD_START=$(ah_test_pid_start "$DEAD_PID")
ah_signal_child TERM "$DEAD_PID"; wait "$DEAD_PID" 2>/dev/null
seed_team owned-2 "$DEAD_PID" '[]' "$DEAD_START"
run adopt --orchestrator-pid "$$"
check "adopt: recorded owner dead -> allowed" '[ "$RC" -eq 0 ]'
check "adopt: dead-owner adopt -> pid re-stamped to the proven shell" '[ "$(owner_pid)" = "$$" ]'

# ---- adopt: supplied pid itself dead -> refused
seed_team dead-1 null '[]'
run adopt --orchestrator-pid 99999999
check "adopt: supplied pid dead -> refused as orchestrator-pid-mismatch" '[ "$RC" -eq 2 ] && echo "$OUT" | grep -q "orchestrator-pid-mismatch"'

# ---- adopt: no team file at this scope -> refused
ah_forget_team "$TEAM_FILE"
run adopt --orchestrator-pid "$$"
check "adopt: no team file at this scope -> refused" '[ "$RC" -eq 2 ]'

echo ""
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
