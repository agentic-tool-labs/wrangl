#!/bin/bash
# agent-hierarchy — liveness readers attribute a nameless peers.jsonl row to the team member whose
# transport_id is that row's pane (and whose role matches), so a spawned peer that never got a
# name on its SessionStart row still reads live: spawn-one refuses a duplicate, teams/dismiss see
# it, the route gate names it. SessionEnd's down row carries the pane so a closed peer reads dead.
# Usage: bash tests/test-peer-pane-attribution.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-seed.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
H="$PLUGIN/hooks"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-pane-attr-test.XXXXXX")"
export AH_TEST_FAKE_BIN="$SANDBOX/nolaunch:$SANDBOX/bin"
ah_test_harness_peer; LIVE_PID=$AH_TEST_HARNESS_PID  # a live member needs a harness-shaped process, which a bare sleep is not
sleep 0 & DEAD_PID=$!; wait "$DEAD_PID" 2>/dev/null
hermetic_on_exit 'ah_signal_child TERM "$LIVE_PID"; rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
# No test may reach the real herdr or tmux: a stub that fails every call sits first on PATH, and
# the session's pane environment is dropped. A wrapper that sets PATH to its own fakes still wins.
mkdir -p "$SANDBOX/nolaunch"; printf '#!/bin/sh\nexit 1\n' > "$SANDBOX/nolaunch/herdr"; cp "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"; chmod +x "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"
export PATH="$SANDBOX/nolaunch:$PATH"; unset WRANGL_TEAM_FILE
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/myrepo"
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2" "$SANDBOX/bin"
(cd "$PROJ" && git init -q)
NODE_DIR="$(dirname "$(command -v node)")"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:500})"; fi
}
has() { echo "$OUT" | tr -d " \n" | grep -q "$1"; }
jq_ok() { echo "$OUT" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{const o=JSON.parse(s);process.exit(($1)?0:1)})"; }

# Fake herdr: logs every invocation, answers nothing useful — a launch attempt is the failure.
INVOKED_LOG="$SANDBOX/herdr-invoked.log"
cat > "$SANDBOX/bin/herdr" <<'EOF'
#!/usr/bin/env node
require("fs").appendFileSync(process.env.FAKE_HERDR_INVOKED_LOG, JSON.stringify(process.argv.slice(2)) + "\n");
if (process.argv[2] === "agent" && process.argv[3] === "list") { console.log(JSON.stringify({ result: { agents: [] } })); process.exit(0); }
process.exit(1);
EOF
chmod +x "$SANDBOX/bin/herdr"
launches() { grep -c '"agent","start"' "$INVOKED_LOG" 2>/dev/null || true; }

run() { OUT=$(ah_test_run_as orchestrator myrepo "$TEAM" env -u CLAUDE_PID HOME="$FAKEHOME" HERDR_ENV=1 HERDR_PANE_ID=p0 PATH="$SANDBOX/bin:$NODE_DIR:/usr/bin:/bin" FAKE_HERDR_INVOKED_LOG="$INVOKED_LOG" "$PLUGIN/bin/ah" roster "$@" --cwd "$PROJ" 2>&1); RC=$?; }

HIER="$PROJ/.claude/ah2"
PEERS_FILE="$HIER/peers.jsonl"
TEAM="$HIER/team.json"
cat > "$PROJ/.claude/ah2/agent-hierarchy.json" <<'EOF'
{"version":1,"enabled":true,"roster":{"route":"peer","members":[{"role":"architect","model":"opus"}]}}
EOF
cp "$PROJ/.claude/ah2/agent-hierarchy.json" "$SANDBOX/conf.json"   # the project dir holds the config, so a reset puts it back

write_team() { # <path> <team_id> <members-json>
  node -e 'const[id,m]=process.argv.slice(1);
    process.stdout.write(JSON.stringify({version:1,team_id:id,created:"2026-01-01T00:00:00Z",roster_level:"repo",transport:"herdr",
      orchestrator:{session_id:null,pid:null},members:JSON.parse(m),partial:false}));' "$2" "$3" | seed_team_file "$1"
}
ARCH_MEMBER='[{"role":"architect","name":"myrepo-architect","ref":"r1","route":"peer","model":"opus","transport_id":"PANE1","checked_in":"2026-01-01T00:00:00Z"}]'
seed() { # <json fields> — one roster row; ts defaults to now
  ah_seed_roster "$HIER" "$(node -e 'const[j]=process.argv.slice(1);process.stdout.write(JSON.stringify({type:"peer",ts:new Date().toISOString(),...JSON.parse(j)}));' "$1")"
}
SID="abcdef0123456789-arch"
fresh() { ah_forget_teams "$HIER"; rm -rf "$HIER"; mkdir -p "$HIER"; cp "$SANDBOX/conf.json" "$HIER/agent-hierarchy.json"; : > "$INVOKED_LOG"; write_team "$TEAM" t-default "$ARCH_MEMBER"; }
up_row() { # <pid> [role] [session]
  seed "{\"status\":\"up\",\"role\":\"${2:-architect}\",\"pid\":$1,\"pane_id\":\"PANE1\",\"session_id\":\"${3:-$SID}\",\"cwd\":\"$PROJ\"}"
}

# ---- 1: a nameless up row on the member's pane, live pid -> spawn-one refuses, no launch
fresh; up_row "$LIVE_PID"
run spawn-one architect
check "1: spawn-one sees the pane-attributed member as already live" 'has "\"reason\":\"alreadylive\""'
check "1b: spawn-one launched nothing" '[ "$(launches)" -eq 0 ]'

# ---- 2: teams/dismiss/untrack read the member live and do not list it as untracked
run teams
check "2: teams does not list the member as untracked role@sid8" '[ "$RC" -eq 0 ] && ! has "architect@abcdef01"'
run dismiss myrepo-architect --plan
check "2b: dismiss plan shows the member live" '[ "$RC" -eq 0 ] && jq_ok "o.live===true"'
check "2c: dismiss plan counts one live peer" 'jq_ok "o.sources.peers.live===1"'
run untrack myrepo-architect
check "2d: untrack plan shows the member live" '[ "$RC" -eq 0 ] && jq_ok "o.member.live===true"'

# ---- 3: dead pid -> not live
fresh; up_row "$DEAD_PID"
run dismiss myrepo-architect --plan
check "3: a dead pid with no recorded start proves nothing: the plan reports the member's liveness as unknown (live null)" '[ "$RC" -eq 0 ] && jq_ok "o.live===null"'
fresh
seed "{\"status\":\"up\",\"role\":\"architect\",\"pid\":$DEAD_PID,\"pid_start\":\"2026-01-01T00:00:00.000Z\",\"pane_id\":\"PANE1\",\"session_id\":\"$SID\",\"cwd\":\"$PROJ\"}"
run dismiss myrepo-architect --plan
check "3b: an exited pid with its recorded start is proven dead" '[ "$RC" -eq 0 ] && jq_ok "o.live===false"'

# ---- 4: role mismatch, or a pane two team files claim -> not attributed
fresh; up_row "$LIVE_PID" reviewer
run dismiss myrepo-architect --plan
check "4: a row whose role differs from the member's is not attributed (the member is undecided, not live)" '[ "$RC" -eq 0 ] && jq_ok "o.live!==true"'
fresh; up_row "$LIVE_PID"
write_team "$HIER/teams/alpha.json" t-alpha '[{"role":"architect","name":"alpha-architect","ref":"r1","route":"peer","model":"opus","transport_id":"PANE1","checked_in":"2026-01-01T00:00:00Z"}]'
run dismiss myrepo-architect --plan
check "4b: a pane claimed by two team files is not attributed (the member is undecided, not live)" '[ "$RC" -eq 0 ] && jq_ok "o.live!==true"'

# ---- 5: pane up row + fresh briefed row -> one slot, not ambiguous, busy/task borrowed
fresh; up_row "$LIVE_PID"
seed '{"status":"briefed","name":"myrepo-architect","role":"architect","busy":true,"task":"spec-x"}'
OUT=$(HOME="$FAKEHOME" "$PLUGIN/bin/ah" msg roster --plain --cwd "$PROJ" 2>&1); RC=$?
check "5: one architect entry for the member" '[ "$(echo "$OUT" | grep -c "^architect:")" -eq 1 ]'
check "5b: liveness comes from the pane row, busy/task from the briefed row" 'echo "$OUT" | grep "^architect: myrepo-architect" | grep "up-pid" | grep "busy" | grep -q "task=spec-x"'
run dismiss myrepo-architect --plan
check "5c: dismiss-by-name is not ambiguous" '[ "$RC" -eq 0 ] && ! has "ambiguous" && jq_ok "o.live===true"'

# ---- 6: pane-attributed down row + fresh briefed row -> not live; SessionEnd writes pane_id
fresh
# the registered pid is dead, so a SessionEnd from any process may mark the member down
sleep 0 & DEAD_PID=$!; wait "$DEAD_PID"
seed "{\"status\":\"up\",\"role\":\"architect\",\"pid\":$DEAD_PID,\"pid_start\":\"2026-01-01T00:00:00.000Z\",\"pane_id\":\"PANE1\",\"session_id\":\"$SID\",\"cwd\":\"$PROJ\",\"ts\":\"2026-09-01T00:00:00.000Z\"}"
echo "{\"session_id\":\"$SID\",\"cwd\":\"$PROJ\",\"hook_event_name\":\"SessionEnd\"}" | env -u HERDR_PANE_ID HOME="$FAKEHOME" AH_MEMBER_NAME=suite-member sh "$PLUGIN/bin/ah-hook" sessionend-roster
check "6: SessionEnd's down row carries the up row's pane_id" \
  'ah_roster_rows "$HIER" | tail -1 | grep -q "\"status\":\"down\"" && ah_roster_rows "$HIER" | tail -1 | grep -q "\"pane_id\":\"PANE1\""'
seed "{\"status\":\"down\",\"role\":\"architect\",\"pid\":$DEAD_PID,\"pid_start\":\"2026-01-01T00:00:00.000Z\",\"pane_id\":\"PANE1\",\"session_id\":\"$SID\",\"cwd\":\"$PROJ\"}"
seed '{"status":"briefed","name":"myrepo-architect","role":"architect"}'
run dismiss myrepo-architect --plan
check "6b: a briefed row never decides liveness, so the pane-attributed down row (exited pid with its start) keeps the member not live" '[ "$RC" -eq 0 ] && jq_ok "o.live===false"'

echo "---- $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
