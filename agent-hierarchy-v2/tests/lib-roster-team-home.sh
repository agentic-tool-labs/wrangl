# agent-hierarchy — the shared setup of test-roster-team-home.sh and test-roster-team-home-teardown.sh: a scratch repo
# with a linked worktree, a scratch HOME and a PATH herdr shim, and the helpers both suites drive. Sourced after
# lib-hermetic.sh and lib-test-env.sh.
PLUGIN="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
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

# The agents the fake herdr holds, as [{name, pane_id, cwd}].
herdr_agents() { ah_test_fake_herdr_state "$STATE" 'Object.entries(s.agents).filter(([id])=>s.panes[id]).map(([id,a])=>({name:a.name,pane_id:id,cwd:s.panes[id].cwd}))'; }
pane_of() { node -e 'const a=JSON.parse(process.argv[1]).find(x=>x.name===process.argv[2]);console.log(a?a.pane_id:"")' "$(herdr_agents)" "$1"; }

# ---- teardown helpers (disband plan/close, strays), used by both suites
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
