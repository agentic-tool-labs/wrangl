#!/bin/bash
# agent-hierarchy — sub-orchestrators end to end: stream-open, spawn the sub-orchestrator, create --parent, stream-report through
# the states and the result, with the worktree gate absent, enforcing and off. A stateful fake herdr and a logging git sit first on
# PATH, so no real herdr, tmux or ~/.claude is touched. HOME-redirected.
# Usage: bash tests/test-sub-orchestrators.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
H="$PLUGIN/hooks"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-sub-orchestrators-test.XXXXXX")"
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
export AH_TEST_FAKE_BIN="$SANDBOX/nolaunch:$SANDBOX/bin"
# No test may reach the real herdr or tmux: a stub that fails every call sits first on PATH, and
# the session's pane environment is dropped. A wrapper that sets PATH to its own fakes still wins.
mkdir -p "$SANDBOX/nolaunch"; printf '#!/bin/sh\nexit 1\n' > "$SANDBOX/nolaunch/herdr"; cp "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"; chmod +x "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"
REAL_GIT="$(command -v git)"
export PATH="$SANDBOX/nolaunch:$PATH"; unset WRANGL_TEAM_FILE
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/myrepo"
STATE="$SANDBOX/state"
mkdir -p "$FAKEHOME/.claude/ah2" "$SANDBOX/bin" "$STATE"
NODE_DIR="$(dirname "$(command -v node)")"
TEAM_FILE="$PROJ/.claude/ah2/teams/myrepo.json"
PASS=0; FAIL=0
ah_test_prove_self orchestrator "$(basename "$PROJ")" > /dev/null  # this shell is the Orchestrator of the team the verbs below create

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:600} ERR=$(head -c 400 "$STATE/err" 2>/dev/null))"; fi
}

# ---- fake herdr: the shared one (tests/fake-herdr.mjs).
# FAKE_HERDR_FAIL=1 fails every call; FAKE_HERDR_FAIL_CMD="tab create,tab rename" fails those.
ah_test_install_fake_herdr "$SANDBOX/bin"

# ---- git: the real binary, every argv logged to $STATE/git.log.
cat > "$SANDBOX/bin/git" <<EOF
#!/bin/sh
printf '%s\n' "\$*" >> "$STATE/git.log"
exec "$REAL_GIT" "\$@"
EOF
chmod +x "$SANDBOX/bin/git"

# ---- tmux: FAKE_TMUX=up answers every call (new-window prints a pane id); anything else fails.
cat > "$SANDBOX/bin/tmux" <<EOF
#!/bin/sh
printf '%s\n' "\$*" >> "$STATE/tmux.log"
[ "\$FAKE_TMUX" = up ] || exit 1
[ "\$1" = new-window ] && echo "%9"
exit 0
EOF
chmod +x "$SANDBOX/bin/tmux"

fresh() { # a new repo on main with one commit, no hierarchy, fresh fake herdr state, empty logs
  ah_forget_teams "$PROJ/.claude/ah2"
  rm -rf "$PROJ" "$SANDBOX/other"
  mkdir -p "$PROJ"
  "$REAL_GIT" -C "$PROJ" init -q -b main
  "$REAL_GIT" -C "$PROJ" -c user.email=t@example.com -c user.name=t commit -q --allow-empty -m init
  printf '.claude/\n' >> "$PROJ/.git/info/exclude"  # the hierarchy's own files are not work, so a member's tree reads clean to the release check
  ah_test_seed_fake_herdr "$STATE" 200 50 "$PROJ" > /dev/null
  clear_logs
}
clear_logs() { ah_test_fake_herdr_reset_calls "$STATE"; : > "$STATE/git.log"; : > "$STATE/tmux.log"; }

# run <verb> [args...] — herdr transport (HERDR_ENV=1). Env overrides go before the call:
#   TRANSPORT=tmux|terminal run ...   FAKE_HERDR_FAIL_CMD="tab create" run ...
run() {
  local pidenv=CLAUDE_PID=$$ unsetpid=()
  # a caller proven by its ancestry (RUN_PREFIX) must not also name this shell's pid as its Claude pid
  [ -z "${RUN_PREFIX:-}${PROVEN:-}" ] || { pidenv=; unsetpid=(-u CLAUDE_PID); }
  local envs=(HOME="$FAKEHOME" PATH="$SANDBOX/bin:$NODE_DIR:/usr/bin:/bin" FAKE_STATE_DIR="$STATE" ${pidenv:+"$pidenv"} FAKE_HERDR_FAIL="${FAKE_HERDR_FAIL:-0}" FAKE_HERDR_FAIL_CMD="${FAKE_HERDR_FAIL_CMD:-}")
  case "${TRANSPORT:-herdr}" in
    herdr) envs+=(HERDR_ENV=1 HERDR_PANE_ID=w1:p0 HERDR_WORKSPACE_ID=w1 FAKE_TMUX=down) ;;
    tmux) envs+=(FAKE_TMUX=up) ;;
    terminal) envs+=(FAKE_TMUX=down) ;;
  esac
  OUT=$(${RUN_PREFIX:-} env -u HERDR_ENV "${unsetpid[@]}" "${envs[@]}" "$PLUGIN/bin/ah" roster "$@" --cwd "${RUN_CWD:-$PROJ}" 2>"$STATE/err"); RC=$?
}
# oj <js boolean over `o`, the parsed stdout>
oj() { printf '%s' "$OUT" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{let o;try{o=JSON.parse(s)}catch{process.exit(1)}process.exit((new Function("o","return ("+process.argv[1]+");"))(o)?0:1)})' "$1"; }
# tj <js boolean over `t`, the parsed team file>
tj() { ah_team_doc "$TEAM_FILE" | node -e 'let t;try{t=JSON.parse(require("fs").readFileSync(0,"utf8"))}catch{process.exit(1)}process.exit((new Function("t","return ("+process.argv[1]+");"))(t)?0:1)' "$1"; }
# hj <js boolean over `s`, the fake herdr state>
hj() { ah_test_fake_herdr_holds "$STATE" "$1"; }
hcount() { ah_test_fake_herdr_calls "$STATE" | grep -c -- "$1"; }
set_status() { ah_test_fake_herdr_edit "$STATE" "s.agents['$1'].status='$2'"; }  # <pane> <status>: a live agent's herdr status
kill_agent() { ah_test_fake_herdr_edit "$STATE" "delete s.agents['$1']"; }  # <pane>: the agent is gone, the pane stays
member_pane() { node -e 'const t=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));const m=t.members.find(m=>m.name===process.argv[2]);console.log(m?m.transport_id:"")' "$TEAM_FILE" "$1"; }
stream_tab() { node -e 'const t=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));console.log((t.streams&&t.streams[process.argv[2]]&&t.streams[process.argv[2]].tab_id)||"")' "$TEAM_FILE" "$1"; }
tab_label() { ah_test_fake_herdr_state "$STATE" "(s.tabs['$1']||{}).label||''"; }
request() { # <to role> <to_name> <from_name> -> prints the request path
  HOME="$FAKEHOME" "$PLUGIN/bin/ah" msg new --to "$1" --from orchestrator --slug s --title "Fix test titles" --to-name "$2" --from-name "$3" --team myrepo --cwd "$PROJ" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>console.log(JSON.parse(s).path))'
}
respond() { # <request path> <the request's to role>: a filled response, which closes a member's exchange
  local id; id=$(basename "$1" | cut -d- -f1-3)
  local resp; resp=$(HOME="$FAKEHOME" "$PLUGIN/bin/ah" msg new --type response --id "$id" --to orchestrator --from "$2" --slug s --team myrepo --req "$1" --cwd "$PROJ" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>console.log(JSON.parse(s).path))')
  printf -- '- done: reported\n' >> "$resp"
}
setup_roster() { # repo roster: one architect and one reviewer, both peers
  HOME="$FAKEHOME" "$PLUGIN/bin/ah" roster init --level repo --route peer --cwd "$PROJ" >/dev/null
  HOME="$FAKEHOME" "$PLUGIN/bin/ah" roster add --no-spawn --level repo --role architect --model opus --cwd "$PROJ" >/dev/null
  HOME="$FAKEHOME" "$PLUGIN/bin/ah" roster add --no-spawn --level repo --role reviewer --model opus --cwd "$PROJ" >/dev/null
}

CHILD_FILE="$PROJ/.claude/ah2/teams/myrepo-ws1.json"
MO_FILE="$TEAM_FILE"
cj() { TEAM_FILE="$CHILD_FILE" tj "$1"; }

flow() { # <grants mode: none|enforce|off>
  local mode=$1
  fresh
  setup_roster
  run stream-open ws1
  check "$mode 1: stream-open ws1" 'oj "o.opened===true"'
  if [ "$mode" != none ]; then
    run gate "$mode"
    check "$mode 1: gate $mode set" '[ "$RC" -eq 0 ]'
  fi

  run spawn-one sub-orchestrator --stream ws1
  check "$mode 2: the sub-orchestrator spawns" '[ "$RC" -eq 0 ]'
  check "$mode 2: member row myrepo-so-ws1 of role sub-orchestrator" 'tj "t.members.some(m=>m.name===\"myrepo-so-ws1\" && m.role===\"sub-orchestrator\")"'
  check "$mode 2: the stream records its sub-orchestrator" 'tj "t.streams.ws1.sub_orchestrator===\"myrepo-so-ws1\""'

  run spawn-one sub-orchestrator --stream ws1
  check "$mode 2: a second sub-orchestrator for the same stream is refused" '[ "$RC" -ne 0 ]'
  run spawn-one sub-orchestrator
  check "$mode 2: a sub-orchestrator with no stream is refused" '[ "$RC" -ne 0 ]'

  # the lane's sub-orchestrator creates its own child team, from a shell that runs under its registered session
  # every call of the lead is the same process: a child team belongs to the process that created it, and only its registered lane lead may staff it
  lead_block() {
    PROVEN=1
    run create --parent myrepo --stream ws1 --commit --transport "${TRANSPORT:-herdr}" --roster-level repo
    check "$mode 4: create --parent writes myrepo-ws1" '[ "$RC" -eq 0 ] && ah_team_stored "$CHILD_FILE"'
    RUN_CWD="$PROJ/.claude/worktrees/ws1"
    run spawn-one reviewer --team myrepo-ws1 --stream ws1
    check "$mode 5: the lane lead spawns a member in the child" '[ "$RC" -eq 0 ]'
    echo "$PASS $FAIL" > "$STATE/lead.cnt"  # the counters of this subshell go back to the parent
  }
  rm -f "$STATE/lead.cnt"
  ah_test_run_as myrepo-so-ws1 myrepo "$TEAM_FILE" lead_block
  if [ -s "$STATE/lead.cnt" ]; then read -r PASS FAIL < "$STATE/lead.cnt"; else FAIL=$((FAIL+1)); echo "FAIL: $mode 4: the lead's calls ran (no counters came back)"; fi
  check "$mode 4: the child records its parent and a working workstream" 'cj "t.parent.team===\"myrepo\" && t.parent.stream===\"ws1\" && t.workstream.state===\"working\""'

  run stream-report --team myrepo-ws1 --state review --summary "parser done"
  check "$mode 6: stream-report review" '[ "$RC" -eq 0 ] && cj "t.workstream.state===\"review\" && t.workstream.summary===\"parser done\""'
  run stream-report --team myrepo-ws1 --state finished --summary "x"
  check "$mode 6: an unknown state is refused" '[ "$RC" -ne 0 ]'
  run stream-report --team myrepo --state working --summary "x"
  check "$mode 6: a team with no parent is refused" '[ "$RC" -ne 0 ]'

  # a result must be a stored response, so a real one is filed
  local field='let s="";process.stdin.on("data",d=>s+=d).on("end",()=>console.log(JSON.parse(s)[process.argv[1]]))'
  local made resp
  made=$(HOME="$FAKEHOME" "$PLUGIN/bin/ah" msg new --to orchestrator --from orchestrator --slug ws-ws1 --title "Finish the stream" --team myrepo --cwd "$PROJ")
  resp=$(HOME="$FAKEHOME" "$PLUGIN/bin/ah" msg new --type response --id "$(printf '%s' "$made" | node -e "$field" id)" --from orchestrator --req "$(printf '%s' "$made" | node -e "$field" path)" --team myrepo --cwd "$PROJ" | node -e "$field" path)
  run stream-report --team myrepo-ws1 --state done --summary "all merged" --result "$resp"
  check "$mode 7: done with a result" '[ "$RC" -eq 0 ] && cj "t.workstream.state===\"done\" && t.workstream.result===\"$resp\""'
  run stream-report --team myrepo-ws1 --state working --summary "again" --result "$resp"
  check "$mode 7: a result goes only with done" '[ "$RC" -ne 0 ]'

  TEAM_FILE="$MO_FILE" tj "t.streams.ws1.sub_orchestrator===\"myrepo-so-ws1\" && t.parent===undefined"
  check "$mode 7: the manager's team has no parent and still names its sub-orchestrator" '[ $? -eq 0 ]'

  run close-approve --team myrepo-ws1 --verb disband
  check "$mode 8: a member whose liveness is undecided is not held, so the plan targets it and warns" 'oj "o.targets.length===1 && o.held===0 && o.release[0].status===\"liveness-unknown\""'
  ah_test_harness_peer myrepo-ws1-reviewer myrepo-ws1
  run close-approve --team myrepo-ws1 --verb disband
  check "$mode 8: close-approve without a token prints the plan and writes nothing" '[ "$RC" -eq 0 ] && oj "o.approve===false && typeof o.close_token===\"string\"" && TEAM_FILE="$MO_FILE" tj "!t.streams.ws1.close_grants"'
  local token; token=$(printf '%s' "$OUT" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>console.log(JSON.parse(s).close_token))')
  run close-approve --team myrepo-ws1 --verb disband --plan-token 0000000000000000
  check "$mode 8: close-approve with a stale token is refused" '[ "$RC" -ne 0 ]'
  # the grant binds to the manager's recorded session, pid and start
  ah_test_prove_self orchestrator myrepo "$MO_FILE" "" mo-session > /dev/null
  ah_team_doc "$MO_FILE" | node -e 'const t=JSON.parse(require("fs").readFileSync(0,"utf8"));t.orchestrator.session_id="mo-session";console.log(JSON.stringify(t))' | ah_seed_team "$MO_FILE"
  run close-approve --team myrepo-ws1 --verb disband --plan-token "$token"
  check "$mode 8: close-approve with the current token records one grant" '[ "$RC" -eq 0 ] && TEAM_FILE="$MO_FILE" tj "t.streams.ws1.close_grants.length===1 && t.streams.ws1.close_grants[0].plan_token===\"$token\""'
  ah_test_harness_stop

  run dismiss myrepo-so-ws1 --plan
  check "$mode 9: the manager plans dismissing the sub-orchestrator" '[ "$RC" -eq 0 ]'

  run stream-done ws1
  check "$mode 10: stream-done" '[ "$RC" -eq 0 ] && TEAM_FILE="$MO_FILE" tj "t.streams.ws1.state===\"done\""'
}

# The shapes a lane lead really calls the verbs in: with its member marker, from its worktree, --plan first, --help; and who must stay refused.
lane_shapes() { # <marked|unmarked>
  local shape=$1
  fresh
  setup_roster
  run stream-open ws1
  run spawn-one sub-orchestrator --stream ws1
  run spawn-one reviewer
  check "$shape: setup" '[ "$RC" -eq 0 ]'

  shape_lead() {
    PROVEN=1
    if [ "$shape" = marked ]; then export AH_MEMBER_NAME=myrepo-so-ws1 WRANGL_TEAM_FILE="$MO_FILE"; fi
    RUN_CWD="$PROJ/.claude/worktrees/ws1"
    run spawn-one --help
    check "$shape: spawn-one --help answers" '[ "$RC" -eq 0 ]'
    run create --help
    check "$shape: create --help answers" '[ "$RC" -eq 0 ]'
    run create --parent myrepo --stream ws1 --plan --transport "${TRANSPORT:-herdr}" --roster-level repo
    check "$shape: create --plan from the worktree" '[ "$RC" -eq 0 ]'
    run create --parent myrepo --stream ws1 --commit --transport "${TRANSPORT:-herdr}" --roster-level repo
    check "$shape: create --commit from the worktree" '[ "$RC" -eq 0 ] && ah_team_stored "$CHILD_FILE"'
    run stream-open ws1-a --team myrepo-ws1
    check "$shape: stream-open in its child" '[ "$RC" -eq 0 ]'
    RUN_CWD="$PROJ/.claude/worktrees/ws1-a"
    run spawn-ad-hoc reviewer --team myrepo-ws1 --stream ws1-a --model opus --effort medium
    check "$shape: spawn-ad-hoc into its child" '[ "$RC" -eq 0 ]'
    RUN_CWD="$PROJ/.claude/worktrees/ws1"
    run status
    check "$shape: roster status answers" '[ "$RC" -eq 0 ]'
    run stream-open ws9 --team myrepo
    check "$shape: stream-open on the manager is refused" '[ "$RC" -ne 0 ]'
    run create --parent myrepo --stream ws2
    check "$shape: create for a stream it does not run is refused" '[ "$RC" -ne 0 ]'
    echo "$PASS $FAIL" > "$STATE/lead.cnt"
  }
  rm -f "$STATE/lead.cnt"
  ah_test_run_as myrepo-so-ws1 myrepo "$TEAM_FILE" shape_lead
  if [ -s "$STATE/lead.cnt" ]; then read -r PASS FAIL < "$STATE/lead.cnt"; else FAIL=$((FAIL+1)); echo "FAIL: $shape: the lead's calls ran"; fi

  shape_member() {
    PROVEN=1
    if [ "$shape" = marked ]; then export AH_MEMBER_NAME=myrepo-reviewer WRANGL_TEAM_FILE="$MO_FILE"; fi
    run create --parent myrepo --stream ws1 --plan --transport "${TRANSPORT:-herdr}" --roster-level repo
    check "$shape: a plain member cannot create" '[ "$RC" -ne 0 ] && oj "/^member-cannot-create/.test(o.refused)"'
    run spawn-one reviewer --team myrepo-ws1
    check "$shape: a plain member cannot spawn into the child" '[ "$RC" -ne 0 ] && oj "/^member-cannot-spawn-one/.test(o.refused)"'
    echo "$PASS $FAIL" > "$STATE/lead.cnt"
  }
  rm -f "$STATE/lead.cnt"
  ah_test_run_as myrepo-reviewer myrepo "$TEAM_FILE" shape_member
  if [ -s "$STATE/lead.cnt" ]; then read -r PASS FAIL < "$STATE/lead.cnt"; else FAIL=$((FAIL+1)); echo "FAIL: $shape: the member's calls ran"; fi

  # The top Orchestrator passes the owner check on a lane's child team (#401: it was not-team-owner). The lead's shell has exited by
  # now, so the one refusal left is the lane rule that a gone lead is respawned first.
  RUN_CWD="$PROJ/.claude/worktrees/ws1"
  run spawn-ad-hoc architect --team myrepo-ws1 --stream ws1 --model opus --effort medium
  check "$shape: the top Orchestrator passes the owner check on the child" 'oj "o.refused===\"lane-lead-gone\""'
  unset RUN_CWD
}

lane_shapes marked
lane_shapes unmarked

flow none
flow enforce
flow off

echo "passed=$PASS failed=$FAIL"
[ "$FAIL" -eq 0 ]
