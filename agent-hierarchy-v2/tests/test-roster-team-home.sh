#!/bin/bash
# agent-hierarchy — one team file per checkout: the team file is the master list for spawn,
# disband, dismiss and teams (spec 0072). Every case runs in a scratch repo with a linked
# worktree, a scratch HOME and a PATH herdr shim; no real herdr, claude or repo is touched.
# Usage: bash tests/test-roster-team-home.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
export AH_DAEMON=off  # not a daemon test: a lazily started ahd would outlive it
H="$PLUGIN/hooks"
unset WRANGL_TEAM_FILE CLAUDE_PID WRANGL_HIERARCHY_DIR
# Under /tmp/ahprobe.*: in test mode a hook's state write anywhere else is skipped, and SessionStart's peers.jsonl row with it.
T="$(mktemp -d /tmp/ahprobe.XXXXXX)"
[ -n "$T" ] || exit 9
T="$(cd "$T" && pwd -P)"
export AH_TEST_FAKE_BIN="$T/nolaunch:$T/bin"
SLEEPER=""
cleanup() { [ -n "$SLEEPER" ] && ah_signal_child TERM "$SLEEPER"; rm -rf "$T"; }
hermetic_on_exit cleanup
NODE_BIN="$(command -v node)"
NODE_DIR="$(dirname "$NODE_BIN")"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:500})"; fi
}

# A live pid that owns every team the cases create.
sleep 300 & SLEEPER=$!

mkdir -p "$T/bin" "$T/home/.claude/ah2" "$T/nolaunch"
printf '#!/bin/sh\nexit 1\n' > "$T/nolaunch/tmux"; chmod +x "$T/nolaunch/tmux"
ah_test_install_fake_herdr "$T/bin"

# ---- scratch repo with a linked worktree
REPO="$T/myrepo"; WT="$REPO/.claude/worktrees/w"; OTHER="$T/otherrepo"
git -C "$T" init -q myrepo
git -C "$REPO" -c user.email=a@b -c user.name=n commit -q --allow-empty -m init
git -C "$REPO" worktree add -q "$WT" 2>/dev/null
git -C "$T" init -q otherrepo
HOME_MAIN="$REPO/.claude/ah2"; HOME_WT="$WT/.claude/ah2"
STATE="$T/state"

reset_state() {
  ah_test_harness_stop
  local d; for d in "$HOME_MAIN" "$HOME_WT" "$OTHER/.claude/ah2" "$T/pinned"; do ah_forget_teams "$d"; ah_clear_roster "$d"; done
  ah_clear_roster "$REPO/.claude/ah2"; ah_clear_roster "$WT/.claude/ah2"
  rm -rf "$STATE" "$HOME_MAIN" "$HOME_WT" "$OTHER/.claude"
  mkdir -p "$STATE"
  HERDR_PANE_VARS=$(ah_test_seed_fake_herdr "$STATE" 180 42)
}

# r <extra env words…> -- <roster.mjs args…>: always shimmed, always the scratch HOME.
r() {
  local -a envs=()
  while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  # this shell is the Orchestrator of the team the arguments name (--team, else the repo's own)
  local t; t=$(basename "$REPO"); local prev= a; for a in "$@"; do [ "$prev" = "--team" ] && t=$a; prev=$a; done
  ah_test_prove_self orchestrator "$t" > /dev/null
  OUT=$(env HOME="$T/home" HERDR_ENV=1 $HERDR_PANE_VARS PATH="$T/bin:$T/nolaunch:$NODE_DIR:/usr/bin:/bin" FAKE_STATE_DIR="$STATE" "${envs[@]}" "$PLUGIN/bin/ah" roster "$@" 2>&1); RC=$?
}

# A global-level roster: ultra-advisor, architect, reviewer, implementor, implementor-2.
setup_roster() {
  r -- init --level global --route peer --cwd "$REPO"
  for ro in ultra-advisor architect reviewer implementor implementor; do
    r -- add --no-spawn --level global --role $ro --model opus --cwd "$REPO"
  done
}

# spawn <role> <member|-> <cwd> [team]: spawn-one, the agent's cwd reported as the launch cwd.
spawn() {
  local role=$1 member=$2 cwd=$3 team=${4:-demo}
  local -a ma=(); [ "$member" != "-" ] && ma=(--member "$member")
  # --allow-main: the scenario puts an implementor in the main checkout, which a spawn refuses without it
  r FAKE_HERDR_AGENT_CWD="$cwd" -- spawn-one "$role" "${ma[@]}" --team "$team" --orchestrator-pid "$$" --allow-main --cwd "$cwd"
}

# The five-peer field scenario: two members launched from the worktree, three from main.
field_scenario() {
  reset_state; setup_roster
  spawn ultra-advisor - "$WT"; spawn architect - "$WT"
  spawn reviewer - "$REPO"; spawn implementor - "$REPO"; spawn implementor demo-implementor-2 "$WT"  # a second writer may not share the first one's tree
}

jq_() { echo "$OUT" | grep -v '^ah roster:' | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const o=JSON.parse(s);console.log(String(eval(process.argv[1])))})' "$1"; }
members_of() { ah_team_query "$1" 't.members.map(m=>m.name).join(",")'; }
# team_set <team file> <js over t>: rewrites the stored team through the writer.
team_set() { ah_team_doc "$1" | node -e 'const t=JSON.parse(require("fs").readFileSync(0,"utf8"));eval(process.argv[1]);console.log(JSON.stringify(t))' "$2" | ah_seed_team "$1"; }
log_has() { ah_test_fake_herdr_calls "$STATE" | grep -q -- "$1"; }
# launch_env <member> <key>: that env value of the settings file the launch passes with --settings, once the launch names that file.
launch_env() {
  local f="$HOME_MAIN/launch/$1.settings.json"
  log_has "$f" && node -e 'console.log(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).env[process.argv[2]])' "$f" "$2"
}

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

# The agents the fake herdr holds, as [{name, pane_id, cwd}].
herdr_agents() { ah_test_fake_herdr_state "$STATE" 'Object.entries(s.agents).filter(([id])=>s.panes[id]).map(([id,a])=>({name:a.name,pane_id:id,cwd:s.panes[id].cwd}))'; }
pane_of() { node -e 'const a=JSON.parse(process.argv[1]).find(x=>x.name===process.argv[2]);console.log(a?a.pane_id:"")' "$(herdr_agents)" "$1"; }

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

# ==== teardown: L1/L2, V1-V3, W-1, S1-S7, D1, T1, C6, C7 ====
# seed_peers: mimic each launched agent's SessionStart row, in the pool of the checkout it runs in. Each agent is a harness-shaped
# process registered as <agent>@demo, which is what makes it a live member.
seed_peers() {
  local name pane cwd
  while IFS=$'\t' read -r name pane cwd; do
    [ -n "$cwd" ] || continue
    ah_test_harness_peer "$name" demo || return 1
    node -e '
      const fs=require("fs"),path=require("path");
      const [name,pane,cwd,pid]=process.argv.slice(1);
      const pool=path.join(cwd,".claude","ah2");fs.mkdirSync(pool,{recursive:true});
      const role=name.replace(/^[a-z]+-/,"").replace(/-\d+$/,"");
      fs.appendFileSync(path.join(pool,"peers.jsonl"),JSON.stringify({type:"peer",status:"up",name,role,pid:Number(pid),pane_id:pane,ts:new Date().toISOString()})+"\n");' "$name" "$pane" "$cwd" "$AH_TEST_HARNESS_PID"
    ah_flush_roster "$cwd/.claude/ah2/peers.jsonl" || return 1
  done < <(herdr_agents | node -e 'let t="";process.stdin.on("data",d=>t+=d).on("end",()=>{for(const a of JSON.parse(t))console.log([a.name,a.pane_id,a.cwd||""].join("\t"))})')
}
# put_agent <name> <pane> <cwd>: a herdr agent no team file names.
put_agent() {
  ah_test_fake_herdr_agent "$STATE" "$1" "$2" "$3"
}
rename_agent() { ah_test_fake_herdr_rename_agent "$STATE" "$1" "$2"; }
close_names() { jq_ 'o.close.map(c=>c.name).sort().join(",")'; }
plan() { r CLAUDE_PID="$$" -- disband --plan --ignore-release "$@"; }
closecmd() { # <team-args…> --cwd X : plan, then close with its token
  plan "$@"; PTOKEN=$(jq_ 'o.close_token')
  r CLAUDE_PID="$$" "${CLOSE_ENV[@]}" -- disband --close --confirm --ignore-release --plan-token "$PTOKEN" "$@"
}
CLOSE_ENV=()
FIVE="demo-architect,demo-implementor,demo-implementor-2,demo-reviewer,demo-ultra-advisor"
closed_panes() { ah_test_fake_herdr_calls "$STATE" | grep -c '"pane","close"'; }

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
