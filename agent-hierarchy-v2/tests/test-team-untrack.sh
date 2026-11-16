#!/bin/bash
# agent-hierarchy — spec 0046 §2.3/§8: `untrack` is the sole tracking-only verb. Plan is
# read-only; commit refuses a live target without --keep-sessions (an unknown-liveness one proceeds, worded as such); a live
# session team.json never recorded has no record to forget and is named as `dismiss`'s job;
# untracking something already gone is idempotent, not an error.
# Usage: bash tests/test-team-untrack.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-seed.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
H="$PLUGIN/hooks"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-untrack-test.XXXXXX")"
export AH_TEST_FAKE_BIN="$SANDBOX/bin"
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/myrepo"
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2" "$SANDBOX/bin"
(cd "$PROJ" && git init -q)
NODE_DIR="$(dirname "$(command -v node)")"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:400})"; fi
}

echo '[]' > "$SANDBOX/agents.json"
cat > "$SANDBOX/bin/herdr" <<'EOF'
#!/usr/bin/env node
const fs = require("fs");
const args = process.argv.slice(2);
if (args[0] === "agent" && args[1] === "list") {
  const agents = JSON.parse(fs.readFileSync(process.env.FAKE_HERDR_STATE, "utf8"));
  console.log(JSON.stringify({ id: "cli:agent:list", result: { agents, type: "agent_list" } }));
  process.exit(0);
}
if (args[0] === "pane" && args[1] === "close") {
  console.log(JSON.stringify({ id: "cli:pane:close", result: { ok: true } }));
  process.exit(0);
}
process.stderr.write("fake herdr: unhandled args " + JSON.stringify(args) + "\n");
process.exit(1);
EOF
chmod +x "$SANDBOX/bin/herdr"

# $1=key $2=value, matched against the whitespace-stripped JSON so pretty-printing is irrelevant.
has() { echo "$OUT" | tr -d " \n" | grep -q "\"$1\":$2"; }
run() { OUT=$(HOME="$FAKEHOME" PATH="$SANDBOX/bin:$NODE_DIR" FAKE_HERDR_STATE="$SANDBOX/agents.json" "$PLUGIN/bin/ah" roster "$@" --cwd "$PROJ" 2>&1); RC=$?; }

HIER="$PROJ/.claude/ah2"
TEAM_FILE="$HIER/teams/$(basename "$PROJ").json"
PEERS_FILE="$HIER/peers.jsonl"
mkdir -p "$HIER"
cat > "$PROJ/.claude/ah2/agent-hierarchy.json" <<'EOF'
{"version":1,"enabled":true,"roster":{"route":"peer","members":[{"role":"architect","model":"opus"},{"role":"reviewer","model":"opus"}]}}
EOF
TEAMTAG="$(basename "$PROJ")"
ah_test_harness_peer; HP=$AH_TEST_HARNESS_PID  # a live member needs a harness-shaped process; this shell is not one

seed_peer() { # <name> <role> <status> <pid> [pane_id] [team] [session_id] [pid_start]
  node -e 'const fs=require("fs");const[f,n,r,st,p,pane,team,sid,ps]=process.argv.slice(1);
    const rec={type:"peer",status:st,name:n||undefined,role:r,pid:Number(p)||undefined,ts:new Date().toISOString()};
    if(pane) rec.pane_id=pane;
    if(team) rec.team=team;
    if(sid) rec.session_id=sid;
    if(ps) rec.pid_start=ps;
    fs.appendFileSync(f,JSON.stringify(rec)+"\n");' "$PEERS_FILE" "$1" "$2" "$3" "$4" "${5:-}" "${6:-}" "${7:-}" "${8:-}"
  ah_flush_roster "$PEERS_FILE"
}
fresh() { ah_forget_team "$TEAM_FILE"; ah_clear_roster "$HIER"; }
write_team() {
  seed_team_file "$TEAM_FILE" <<EOF
{
  "version": 1, "team_id": "t1", "created": "2026-01-01T00:00:00Z",
  "roster_level": "repo", "transport": "herdr",
  "orchestrator": { "session_id": null, "pid": null },
  "members": [
    {"role": "architect", "name": "myrepo-architect", "ref": "r1", "route": "peer", "model": "opus", "transport_id": "PANE1", "checked_in": "2026-01-01T00:00:00Z"},
    {"role": "reviewer", "name": "myrepo-reviewer", "ref": "r2", "route": "peer", "model": "opus", "transport_id": "PANE2", "checked_in": "2026-01-01T00:00:00Z"}
  ],
  "partial": false
}
EOF
  ah_test_prove_self orchestrator "$TEAMTAG" "$TEAM_FILE" > /dev/null
}

# ---- U1: plan is read-only and reports liveness, even for a live target
fresh; write_team
seed_peer myrepo-architect architect up $HP PANE1 "$TEAMTAG"
run untrack myrepo-architect
check "U1: plan on a LIVE member succeeds, is read-only, and reports live" \
  '[ "$RC" -eq 0 ] && has plan .untrack. && has live true && ah_team_stored "$TEAM_FILE"'

# ---- U2: commit on a LIVE member REFUSES without --keep-sessions, naming both remedies
run untrack myrepo-architect --commit
check "U2: commit on a live member refuses" '[ "$RC" -ne 0 ]'
check "U2b: refusal names dismiss as the close path" 'echo "$OUT" | grep -q "dismiss"'
check "U2c: refusal names --keep-sessions as the other remedy" 'echo "$OUT" | grep -q -- "--keep-sessions"'
check "U2d: refusal says the record cannot be recovered" 'echo "$OUT" | grep -qi "cannot be"'
check "U2e: the refused commit left team.json untouched" 'ah_team_doc "$TEAM_FILE" | node -e "const t=JSON.parse(require(\"fs\").readFileSync(0,\"utf8\")); process.exit(t.members.length===2?0:1)"'

# ---- U3: --keep-sessions is the explicit opt-in and removes only that row
run untrack myrepo-architect --commit --keep-sessions
check "U3: --keep-sessions untracks the live member" '[ "$RC" -eq 0 ] && has untracked true'
check "U3b: only that row is gone; the sibling stays" \
  'ah_team_doc "$TEAM_FILE" | node -e "const t=JSON.parse(require(\"fs\").readFileSync(0,\"utf8\")); process.exit(t.members.length===1 && t.members[0].name===\"myrepo-reviewer\"?0:1)"'

# ---- U4: idempotent — untracking something already gone is not an error
ah_clear_roster "$HIER"
run untrack myrepo-architect --commit
check "U4: re-untracking an already-gone member reports already_untracked, RC 0" \
  '[ "$RC" -eq 0 ] && has already_untracked true'

# ---- U5: a member nothing shows running needs no --keep-sessions only when its death is proven; an undecided one is worded as undecided
fresh; write_team
sleep 0 & GONE_PID=$!; wait "$GONE_PID"
seed_peer myrepo-reviewer reviewer down "$GONE_PID" PANE2 "$TEAMTAG" "" "2026-01-01T00:00:00.000Z"
run untrack myrepo-reviewer --commit
check "U5: a member whose down row names an exited pid and its start is proven dead and untracks without --keep-sessions" '[ "$RC" -eq 0 ] && has untracked true'
fresh; write_team
seed_peer myrepo-reviewer reviewer down 0 PANE2 "$TEAMTAG"
run untrack myrepo-reviewer --commit
check "U5c: a down row naming no pid proves nothing, so the member is undecided and untracks with the liveness-unknown note" '[ "$RC" -eq 0 ] && echo "$OUT" | grep -q "liveness unknown"'
fresh; write_team
run untrack myrepo-reviewer --commit --keep-sessions
check "U5: an undecided member is worded as possibly running, never as live" \
  'echo "$OUT" | grep -q "may still be running (" && echo "$OUT" | grep -q "liveness unknown" && ! echo "$OUT" | grep -q "is still live"'
fresh; write_team
seed_peer myrepo-architect architect up $HP PANE1 "$TEAMTAG"
run untrack myrepo-architect --commit --keep-sessions
check "U5b: a live member keeps the is-still-live wording" 'echo "$OUT" | grep -q "is still live"'

# ---- U6: a LIVE session team.json never recorded has no record to forget
fresh; write_team
seed_peer "" implementor up $HP PANEX "$TEAMTAG" "sess-untracked-0001"
run untrack PANEX --commit
check "U6: untracking a live UNTRACKED session fails and names dismiss" \
  '[ "$RC" -ne 0 ] && echo "$OUT" | grep -q "dismiss"'

# ---- U7: --all forgets the whole record and closes nothing
fresh; write_team
seed_peer myrepo-architect architect up $HP PANE1 "$TEAMTAG"
run untrack --all --commit --keep-sessions
check "U7: --all removes the team file" '[ "$RC" -eq 0 ] && ! ah_team_stored "$TEAM_FILE"'
check "U7b: --all reports the members it forgot so the user can close them" 'echo "$OUT" | grep -q "myrepo-architect"'
run untrack --all --commit
check "U7c: --all is idempotent once the file is gone" '[ "$RC" -eq 0 ] && has already_untracked true'

# ---- U8: --all with a live member refuses without --keep-sessions
fresh; write_team
seed_peer myrepo-architect architect up $HP PANE1 "$TEAMTAG"
run untrack --all --commit
check "U8: --all refuses while a member is live" '[ "$RC" -ne 0 ] && ah_team_stored "$TEAM_FILE"'

# ---- U9: flag hygiene — the removed destructive flags are not silently accepted
run untrack myrepo-architect --close
check "U9: --close is not an untrack flag" '[ "$RC" -ne 0 ]'
run untrack --all --also-config --commit --keep-sessions
check "U9b: --also-config is refused with --all" '[ "$RC" -ne 0 ]'

# ---- U10: the destructive verbs no longer carry the tracking-only flags (spec 0046 §2.1)
fresh; write_team
run dismiss myrepo-architect --commit
check "U10: dismiss --commit is gone" '[ "$RC" -ne 0 ]'
run disband --commit
check "U10b: disband --commit is gone" '[ "$RC" -ne 0 ]'
run disband --keep-sessions
check "U10c: disband --keep-sessions is gone" '[ "$RC" -ne 0 ]'

echo "---- $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
