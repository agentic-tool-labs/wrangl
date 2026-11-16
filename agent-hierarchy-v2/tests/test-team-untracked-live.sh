#!/bin/bash
# agent-hierarchy — spec 0046 §2.2/§2.4/§2.5/§8.6: the GitHub #4 path. A live peer that team.json
# never recorded must be visible (`untracked_live`), closable by every identifier the user can see
# (pane_id, session_id, session_id prefix, role@sid8, briefed name), in BOTH the default-team and
# the `--team named` scope — and a named-team disband must NOT close the default team's peers.
# Usage: bash tests/test-team-untracked-live.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-seed.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
H="$PLUGIN/hooks"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-untracked-live-test.XXXXXX")"
export AH_TEST_FAKE_BIN="$SANDBOX/nolaunch:$SANDBOX/bin"
hermetic_on_exit 'rm -rf "$SANDBOX"'
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
ah_test_prove_self orchestrator "$(basename "$PROJ")" > /dev/null  # this shell is the Orchestrator of the team the verbs below create

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:500})"; fi
}
has() { echo "$OUT" | tr -d " \n" | grep -q "$1"; }

echo '[]' > "$SANDBOX/agents.json"
cat > "$SANDBOX/bin/herdr" <<'EOF'
#!/usr/bin/env node
const fs = require("fs");
const args = process.argv.slice(2);
if (process.env.FAKE_HERDR_INVOKED_LOG) fs.appendFileSync(process.env.FAKE_HERDR_INVOKED_LOG, JSON.stringify(args) + "\n");
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
INVOKED_LOG="$SANDBOX/herdr-invoked.log"

run() { OUT=$(HOME="$FAKEHOME" PATH="$SANDBOX/bin:$NODE_DIR" FAKE_HERDR_STATE="$SANDBOX/agents.json" FAKE_HERDR_INVOKED_LOG="$INVOKED_LOG" "$PLUGIN/bin/ah" roster "$@" --cwd "$PROJ" 2>&1); RC=$?; }
closed_panes() { grep -o '"pane","close","[^"]*"' "$INVOKED_LOG" 2>/dev/null | sed 's/.*,"//;s/"//' | sort | tr '\n' ' '; }
token() { echo "$OUT" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.parse(s).close_token||"")}catch{console.log("")}})'; }

HIER="$PROJ/.claude/ah2"
PEERS_FILE="$HIER/peers.jsonl"
DEFAULT_TEAM="$HIER/teams/$(basename "$PROJ").json"
NAMED_TEAM="$HIER/teams/alpha.json"
LEGACY_TEAM="$HIER/team.json"
mkdir -p "$HIER"
cat > "$PROJ/.claude/ah2/agent-hierarchy.json" <<'EOF'
{"version":1,"enabled":true,"roster":{"route":"peer","members":[{"role":"architect","model":"opus"},{"role":"reviewer","model":"opus"}]}}
EOF
DEFTAG="$(basename "$PROJ")"
ah_test_harness_peer; HP=$AH_TEST_HARNESS_PID  # a live member needs a harness-shaped process; this shell is not one

seed_peer() { # <name|""> <role> <status> <pid, $$ meaning the harness process> <pane_id> <team|""> <session_id|"">
  [ "$4" != "$$" ] || set -- "$1" "$2" "$3" "$HP" "${@:5}"
  node -e 'const fs=require("fs");const[f,n,r,st,p,pane,team,sid]=process.argv.slice(1);
    const rec={type:"peer",status:st,role:r,pid:Number(p)||undefined,ts:new Date().toISOString()};
    if(n) rec.name=n;
    if(pane) rec.pane_id=pane;
    if(team) rec.team=team;
    if(sid) rec.session_id=sid;
    fs.appendFileSync(f,JSON.stringify(rec)+"\n");' "$PEERS_FILE" "$1" "$2" "$3" "$4" "${5:-}" "${6:-}" "${7:-}"
  ah_flush_roster "$PEERS_FILE"
}
write_team() { # <path> <team_id> [members-json]
  node -e 'const[id,m]=process.argv.slice(1);
    process.stdout.write(JSON.stringify({version:1,team_id:id,created:"2026-01-01T00:00:00Z",roster_level:"repo",transport:"herdr",
      orchestrator:{session_id:null,pid:null},members:JSON.parse(m||"[]"),partial:false}));' "$2" "${3:-[]}" | seed_team_file "$1"
  case "$1" in "$NAMED_TEAM") ah_test_prove_self orchestrator alpha "$1" > /dev/null ;; *) ah_test_prove_self orchestrator "$DEFTAG" "$1" > /dev/null ;; esac
}
fresh() { for f in "$DEFAULT_TEAM" "$NAMED_TEAM" "$LEGACY_TEAM"; do ah_forget_team "$f"; done; ah_clear_roster "$HIER"; rm -rf "$HIER/teams" "$HIER/msgs" "$LEGACY_TEAM" "$PEERS_FILE"; : > "$INVOKED_LOG"; }

SID_LONG="abcdef0123456789-untracked-session"
SID8="abcdef01"
ALPHA_SID="alpha-session-9999"
export AH_TEST_RELEASE_REPROVE=1  # this shell is the Orchestrator of the team files below; ah_test_release_ready hands the owner back to it

# ---- L1: the orphan is VISIBLE (spec 0046 §2.5)
fresh
write_team "$DEFAULT_TEAM" t-default '[{"role":"architect","name":"myrepo-architect","ref":"r1","route":"peer","model":"opus","transport_id":"w1:p1","checked_in":"2026-01-01T00:00:00Z"}]'
seed_peer myrepo-architect architect up $$ w1:p1 "$DEFTAG" sid-arch-0001
seed_peer "" implementor up $$ w1:p91 "$DEFTAG" "$SID_LONG"
run teams
check "L1: teams lists the untracked live peer under its own team" \
  '[ "$RC" -eq 0 ] && has "untracked_live" && has "w1:p91"'
check "L1b: the nameless row is named by the synthesized role@sid8 form (2.4)" \
  'has "implementor@$SID8"'
run reap
check "L1c: reap reports untracked_live and takes no action on it" \
  '[ "$RC" -eq 0 ] && has "untracked_live" && [ -z "$(closed_panes)" ]'

# ---- L1d (review r1, D2): a TRACKED live default-team member must NOT show up in the top-level
# untracked_live. A briefed row carries the team.json member name, so an empty tracked-name set
# lists every tracked peer as an orphan.
# The legacy shared team.json has teamName null, so its members' rows land in NO_TEAM_SCOPE —
# they are tracked AND in the top-level bucket, which is exactly where an empty tracked-name set
# misreports them.
fresh
write_team "$LEGACY_TEAM" t-legacy '[{"role":"architect","name":"myrepo-architect","ref":"r1","route":"peer","model":"opus","transport_id":"w1:p1","checked_in":"2026-01-01T00:00:00Z"}]'
seed_peer myrepo-architect architect up $$ w1:p1 "" sid-arch-0001
seed_peer "" implementor up $$ w1:p91 "" "$SID_LONG"
run teams
check "L1d: a tracked default-team member is absent from the top-level untracked_live (D2)" \
  '[ "$RC" -eq 0 ] && ! (echo "$OUT" | node -e "let s=\"\";process.stdin.on(\"data\",d=>s+=d).on(\"end\",()=>{const o=JSON.parse(s);process.exit(o.untracked_live.some(e=>e.name===\"myrepo-architect\")?0:1)})")'
check "L1e: the genuinely untracked one IS listed at top level (D2)" \
  'echo "$OUT" | node -e "let s=\"\";process.stdin.on(\"data\",d=>s+=d).on(\"end\",()=>{const o=JSON.parse(s);process.exit(o.untracked_live.some(e=>e.pane_id===\"w1:p91\")?0:1)})"'
run reap
check "L1f: reap's top-level untracked_live applies the same exclusion (D2)" \
  '[ "$RC" -eq 0 ] && echo "$OUT" | node -e "let s=\"\";process.stdin.on(\"data\",d=>s+=d).on(\"end\",()=>{const o=JSON.parse(s);process.exit(!o.untracked_live.some(e=>e.name===\"myrepo-architect\") && o.untracked_live.some(e=>e.pane_id===\"w1:p91\")?0:1)})"'

# ---- L1g (review r1, D1): one session present as BOTH a nameless `up` row and a briefed row.
# The no-team disband plan dedups; the close set did not, and closeToken() sorts ids without
# deduping, so the token the plan minted never matched the set the close built.
fresh
seed_peer "" implementor up $$ w1:p91 "" "$SID_LONG"
seed_peer peer-briefed-name implementor briefed $$ w1:p91 "" "$SID_LONG"
write_team "$NAMED_TEAM" t-alpha '[]'  # the Orchestrator that files the release requests is the owner of a team file, here another team's
ah_test_release_ready "$NAMED_TEAM" "implementor@$SID8" && AH_TEST_RELEASE_ROLE=implementor ah_test_release_ready "$NAMED_TEAM" peer-briefed-name
ah_forget_team "$NAMED_TEAM"  # the case is the no-team scope: the file only let the Orchestrator file the requests
run disband
check "L1g: no-team plan over a doubled session yields a close_token" '[ "$RC" -eq 0 ] && has "close_token"'
TOK="$(token)"
run disband --close --confirm --plan-token "$TOK"
check "L1g: the close ACCEPTS that token (D1 — dedup parity between plan and close)" \
  '[ "$RC" -eq 0 ] && [ "$(closed_panes)" = "w1:p91 " ]'

# ---- L2: dismiss --close resolves the orphan by ALL FIVE 2.4 identifier forms
for form in "w1:p91|pane_id" "$SID_LONG|session_id" "abcdef0123|session_id_prefix" "implementor@$SID8|role_at_sid8" "peer-briefed-name|briefed_name"; do
  ident="${form%%|*}"; label="${form##*|}"
  fresh
  write_team "$DEFAULT_TEAM" t-default '[]'
  if [ "$label" = "briefed_name" ]; then
    seed_peer peer-briefed-name implementor up $$ w1:p91 "$DEFTAG" "$SID_LONG"
  else
    seed_peer "" implementor up $$ w1:p91 "$DEFTAG" "$SID_LONG"
  fi
  if [ "$label" = "briefed_name" ]; then AH_TEST_RELEASE_ROLE=implementor ah_test_release_ready "$DEFAULT_TEAM" peer-briefed-name; else ah_test_release_ready "$DEFAULT_TEAM" "implementor@$SID8"; fi
  run dismiss "$ident"
  check "L2 ($label): plan resolves the untracked live peer and returns a close_token" \
    '[ "$RC" -eq 0 ] && has "close_token"'
  TOK="$(token)"
  run dismiss "$ident" --close --confirm --plan-token "$TOK"
  check "L2 ($label): --close closes exactly that pane" \
    '[ "$RC" -eq 0 ] && [ "$(closed_panes)" = "w1:p91 " ]'
done

# ---- L2f: an AMBIGUOUS identifier fails and lists the candidates, never guesses
fresh
write_team "$DEFAULT_TEAM" t-default '[]'
seed_peer "" implementor up $$ w1:p92 "$DEFTAG" "dupdupdup-1111"
seed_peer "" implementor up $$ w1:p93 "$DEFTAG" "dupdupdup-2222"
run dismiss dupdupdup
check "L2f: an ambiguous prefix fails and names every candidate" \
  '[ "$RC" -ne 0 ] && echo "$OUT" | grep -q "w1:p92" && echo "$OUT" | grep -q "w1:p93"'
check "L2f: nothing was closed" '[ -z "$(closed_panes)" ]'

# ---- L3: disband --close closes the orphan too, in BOTH scopes
fresh
write_team "$DEFAULT_TEAM" t-default '[]'
seed_peer "" implementor up $$ w1:p91 "$DEFTAG" "$SID_LONG"
run disband
check "L3: default-scope disband plan lists the untracked live peer" '[ "$RC" -eq 0 ] && has "w1:p91"'
TOK="$(token)"
run disband --close --confirm --plan-token "$TOK"
check "L3b: default-scope disband --close closes it" '[ "$RC" -eq 0 ] && [ "$(closed_panes)" = "w1:p91 " ]'

fresh
write_team "$NAMED_TEAM" t-alpha '[]'
seed_peer "" reviewer up $$ w1:p94 alpha "alpha-session-9999"
ah_test_release_ready "$NAMED_TEAM" "reviewer@${ALPHA_SID:0:8}"
run disband --team alpha
check "L3c: named-scope disband plan lists that team's untracked live peer (2.2 - the #4 fix)" \
  '[ "$RC" -eq 0 ] && has "w1:p94"'
TOK="$(token)"
run disband --team alpha --close --confirm --plan-token "$TOK"
check "L3d: named-scope disband --close closes it" '[ "$RC" -eq 0 ] && [ "$(closed_panes)" = "w1:p94 " ]'

# ---- L4: ISOLATION - a named-team disband never closes the default team's peers
fresh
write_team "$NAMED_TEAM" t-alpha '[]'
write_team "$DEFAULT_TEAM" t-default '[]'
seed_peer "" reviewer up $$ w1:p94 alpha "alpha-session-9999"
seed_peer "" architect up $$ w1:p95 "$DEFTAG" "default-session-1111"
ah_test_release_ready "$NAMED_TEAM" "reviewer@${ALPHA_SID:0:8}"
run disband --team alpha
check "L4: the named-team plan lists only its own peer" \
  '[ "$RC" -eq 0 ] && has "w1:p94" && ! has "w1:p95"'
TOK="$(token)"
run disband --team alpha --close --confirm --plan-token "$TOK"
check "L4b: the named-team close leaves the default team's peer running" \
  '[ "$RC" -eq 0 ] && [ "$(closed_panes)" = "w1:p94 " ]'

echo "---- $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
