#!/bin/bash
# agent-hierarchy — the reboot path end to end, on a sandboxed daemon and a fake herdr: `quit --all` then `resume --all --yes`.
# Proves what the verb tests cannot with their fakes: a quit's checkpoint brief reaches a lane member and its answer reads back; the
# daemon relaunches a held bare member once the hold lifts and sends its open brief itself, and that brief's answer reads back; and a team
# whose Orchestrator died, wound down by the daemon's own sweep past its grace, is taken over by a new session with a fresh record and its member's brief.
# Stand-ins: `claude` is a looping script, and the row a relaunched session's own hook would write is written here.
# Usage: bash tests/test-roster-reboot-resume.sh   (exits 0 iff all cases pass; needs AHD_NATIVE_BIN or AH_BIN)

. "$(dirname "$0")/lib-hermetic.sh"
AH_TEST_DAEMON=1
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
AH_TEST_RUNS_HOOKS=1
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
T="$(mktemp -d /tmp/ahprobe.XXXXXX)"
case "$T" in /tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting" >&2; exit 1;; esac
T="$(cd "$T" && pwd -P)"
export AH_HOME="$T/ah" HOME="$T/home" AH_TEST=1 AHD_WATCH_POLL_MS=500 AH_WATCH_POLL_MS=500 AH_STALL_MARGIN=3 AH_STALL_RESCAN=1
PROJ="$T/proj"; HIER="$PROJ/.claude/ah2"
export WRANGL_HIERARCHY_DIR="$HIER"
for v in AH_HOME HOME WRANGL_HIERARCHY_DIR; do case "${!v}" in "$T"/*) ;; *) echo "$v is not under $T; aborting" >&2; exit 1;; esac; done
mkdir -p "$AH_HOME/run" "$HOME/.claude/ah2" "$HIER/teams"
git -C "$PROJ" init -q -b work 2>/dev/null || { mkdir -p "$PROJ"; git -C "$PROJ" init -q -b work; }
git -C "$PROJ" -c user.name=t -c user.email=t@t commit -q --allow-empty -m one
SHA=$(git -C "$PROJ" rev-parse HEAD)

mkdir -p "$T/fakebin" "$T/herdr-state"
# a stand-in runs $T/hear/<its pid> once when it appears, as its own child: what a real session's adapter does over the socket
mkdir -p "$T/hear"
printf '#!/bin/bash\nwhile :; do f="%s/hear/$$"; if [ -f "$f" ]; then bash "$f" >"$f.out" 2>&1; mv "$f" "$f.done"; fi; sleep 0.5; done\n' "$T" > "$T/fakebin/claude"; chmod +x "$T/fakebin/claude"
ah_test_install_fake_herdr "$T/fakebin"
export AH_TEST_FAKE_BIN="$T/fakebin" FAKE_STATE_DIR="$T/herdr-state" PATH="$T/fakebin:$PATH" FAKE_HERDR_SOCKET="$T/herdr.sock"
mkfifo "$FAKE_HERDR_SOCKET"   # the pane listing takes only a non-regular file this user owns as an instance socket
ah_test_seed_fake_herdr "$FAKE_STATE_DIR" 200 50 "$PROJ" >/dev/null

PIDS=""
keep() { PIDS="$PIDS $1"; }
daemon_pids() {
  { node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid)+"\n")}catch{}' "$AH_HOME/run/ahd.json"
    cat "$AH_HOME"/logs/ahd.log* 2>/dev/null | sed -n 's/.* pid \([0-9]*\) root .*/\1/p'; } | sort -u
}
cleanup() {
  local p
  [ -s "${AH_TEST_TRIPWIRE:-}" ] && { echo "TRIPWIRE:"; cat "$AH_TEST_TRIPWIRE"; }
  for p in $(daemon_pids); do ah_signal_ours TERM "$p" "$T"; done
  for p in $PIDS; do ah_signal_child TERM "$p"; done
  sleep 0.7
  for p in $(daemon_pids); do ah_signal_ours KILL "$p" "$T"; done
  for p in $PIDS; do ah_signal_child KILL "$p"; done
  rm -rf "$T"
}
hermetic_on_exit cleanup
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:900})"; fi; }
waitfor() { local i; for i in $(seq 1 "$1"); do shift 0; if eval "$2"; then return 0; fi; sleep 0.5; done; return 1; }
NOW() { node -e 'process.stdout.write(new Date().toISOString())'; }
startof() { TZ=UTC ps -o lstart= -p "$1" | sed 's/^ *//; s/ *$//'; }
field() { node -e 'try{const v=(new Function("o","return ("+process.argv[2]+");"))(JSON.parse(process.argv[1]));process.stdout.write(v===undefined||v===null?"":typeof v==="string"?v:JSON.stringify(v))}catch{}' "$1" "$2"; }
boxdir() { node -e 'const c=require("crypto"),fs=require("fs"),path=require("path");const tf=process.argv[1];const h=c.createHash("sha256").update(fs.realpathSync(tf)).digest("hex").slice(0,10);process.stdout.write(path.join(process.env.AH_HOME,"mailbox",path.basename(tf).replace(/\.json$/,"")+"~"+h,process.argv[2]))' "$1" "$2"; }
# team <name> <orchestrator session> <orchestrator pid or null> <orchestrator start or empty> <parent or empty> <member:session:pane>...
team() {
  local name=$1 sess=$2 pid=$3 start=$4 parent=$5; shift 5
  node -e '
    const fs=require("fs");const [f,name,sess,pid,start,parent,proj,...ms]=process.argv.slice(1);
    const t={team_id:"t-"+name,version:1,transport:"herdr",orchestrator:{session_id:sess,pid:pid==="null"?null:Number(pid),...(start?{pid_start:start}:{})},
      members:ms.map(m=>{const [n,s,p]=m.split(":");const pane=p+":"+m.split(":")[3];return {role:"implementor",route:"peer",model:"sonnet",name:n,session_id:s,transport_id:pane,expected_root:proj,paste:false}})};
    if(parent)t.parent={team:parent,stream:"ws1"};
    fs.writeFileSync(f,JSON.stringify(t)+"\n")' "$HIER/teams/$name.json" "$name" "$sess" "$pid" "$start" "$parent" "$PROJ" "$@"
  store_team "$name"
}
# store_team <name>: the team file as edited here, stored (the store never reads a planted file)
store_team() { ah_seed_team "$HIER/teams/$1.json" < "$HIER/teams/$1.json"; }
# edit_team <name> <js statements over t, the stored team, and a, the remaining args>: changes the stored team in place
edit_team() { local f="$HIER/teams/$1.json" js=$2; shift 2; ah_team_doc "$f" | node -e 'const t=JSON.parse(require("fs").readFileSync(0,"utf8"));const a=process.argv.slice(2);eval(process.argv[1]);console.log(JSON.stringify(t))' "$js" "$@" | ah_seed_team "$f"; }
# register <session> <member> <team> <pid>: a live registration, the way a real session's hook writes one
register() {
  local tf="$HIER/teams/$3.json" box
  box=$(boxdir "$tf" "$2"); mkdir -p "$box"
  printf '%s\n' "{\"session_id\":\"$1\",\"boxes\":[{\"address\":\"$2@$3\",\"dir\":\"$box\"}],\"team_file\":\"$tf\",\"harness\":\"stub\",\"pid\":$4,\"pid_start\":\"$(startof "$4")\",\"pane_id\":null,\"cwd\":\"$PROJ\",\"bound\":false}" \
    | sh "$PLUGIN/bin/ah" db test-seed sessions >/dev/null
}
pane_holds() { node -e 'const fs=require("fs");const [d,pane,pid,cwd,...argv]=process.argv.slice(1);const f=d+"/herdr.json";const s=JSON.parse(fs.readFileSync(f,"utf8"));s.processes??={};s.processes[pane]=pid==="none"?[]:[{pid:Number(pid),argv,cwd}];fs.writeFileSync(f,JSON.stringify(s))' "$FAKE_STATE_DIR" "$@"; }
as_orch() { AH_TEST_AS_SESSION=orch ah_test_run_as orchestrator tm "$HIER/teams/tm.json" "$@"; }
# Each call is proven from a subshell, which writes itself into the team file; the Orchestrator that stays up afterwards is the long-lived stand-in.
ah_top() { OUT=$(as_orch timeout 150 bash -c 'cd "$0" && "$1/bin/ah" "${@:2}"' "$PROJ" "$PLUGIN" "$@" 2>"$T/err"); RC=$?; orch_stays_up; }
orch_stays_up() {
  edit_team tm 't.orchestrator={session_id:"orch",pid:Number(a[0]),pid_start:a[1]}' "$ORCH" "$(startof "$ORCH")"
  register orch orchestrator tm "$ORCH"
}
ah_as() { local who=$1 team=$2 sess=$3; shift 3; OUT=$(AH_TEST_AS_SESSION=$sess ah_test_run_as "$who" "$team" "$HIER/teams/$team.json" timeout 60 bash -c 'cd "$0" && "$1/bin/ah" "${@:2}"' "$PROJ" "$PLUGIN" "$@" 2>"$T/err"); RC=$?; }
# the store's rows, read-only (recovery records: atp-1606; checkpoints: atp-1567 F2b; neither is a file any more)
db_read() { [ -f "$AH_HOME/state/ah.db" ] && sqlite3 -readonly "$AH_HOME/state/ah.db" "$1" 2>/dev/null; }
recover_state() { db_read "SELECT body FROM recover_records WHERE team = '$1' AND member = '$2' AND kind = 'record'" | node -e 'try{process.stdout.write(JSON.parse(require("fs").readFileSync(0,"utf8")).state||"")}catch{}'; }
# checkpoint_body <team>: the team's newest record; checkpoint_key <key>: the record with that key
checkpoint_body() { db_read "SELECT body FROM checkpoints WHERE team = '$1' ORDER BY ts DESC LIMIT 1"; }
checkpoint_key() { db_read "SELECT body FROM checkpoints WHERE key = '$1'"; }
in_box() { [ -f "$AH_HOME/state/ah.db" ] && sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT CAST(raw AS TEXT) FROM mail_entries WHERE \"to\" = '$3@$1'" | grep -q "$2"; }
# whether the team has a hold of its own, as `roster quit --status` reads it (the holds table; S11a-holds)
held_own() { [ -n "$( (cd "$PROJ" && "$PLUGIN/bin/ah" roster quit --status --team "$1" --cwd "$PROJ" 2>/dev/null) | node -e 'try{const o=JSON.parse(require("fs").readFileSync(0,"utf8"));if(o.hold&&!o.cascade_from)process.stdout.write("y")}catch{}')" ]; }
ahd_alive() { local p; p=$(node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid))}catch{}' "$AH_HOME/run/ahd.json"); [ -n "$p" ] && kill -0 "$p" 2>/dev/null; }

SB1=11111111-1111-4111-8111-111111111111   # a relaunch resumes only a usable session id
# ---- the teams: a top team with a bare member tm-implementor and a lane with a live member l1
"$T/fakebin/claude" --resume $SB1 & BARE=$!; keep "$BARE"
"$T/fakebin/claude" --stand-in-l1 & L1=$!; keep "$L1"
sleep 3000 & ORCH=$!; keep "$ORCH"
team tm orch null "" "" tm-implementor:$SB1:w1:p5
# a lane as a real one is: the manager records the stream and its sub-orchestrator row, and the lane's Orchestrator is that lead's own session, not the top's
node -e 'const fs=require("fs");const f=process.argv[1];const t=JSON.parse(fs.readFileSync(f,"utf8"));t.streams={ws1:{sub_orchestrator:"so1",state:"open"}};t.members.push({role:"sub-orchestrator",route:"peer",model:"sonnet",name:"so1",session_id:"s-so1",transport_id:"w1:p8",expected_root:process.argv[2],paste:false});fs.writeFileSync(f,JSON.stringify(t)+"\n")' "$HIER/teams/tm.json" "$PROJ"
store_team tm
team tm-ws1 s-so1 null "" tm l1:s-l1:w1:p6
ah_test_fake_herdr_agent "$FAKE_STATE_DIR" tm-implementor w1:p5 "$PROJ"
ah_test_fake_herdr_agent "$FAKE_STATE_DIR" l1 w1:p6 "$PROJ"
pane_holds w1:p5 "$BARE" "$PROJ" claude --resume $SB1
register s-l1 l1 tm-ws1 "$L1"
register $SB1 tm-implementor tm "$BARE"
mkdir -p "$(boxdir "$HIER/teams/tm.json" tm-implementor)"
orch_stays_up

( cd "$PROJ" && exec "$AH_BIN" --plugin-root "$PLUGIN" serve --ah-home "$AH_HOME" >"$T/start.out" 2>&1 ) &
waitfor 40 'ahd_alive'
check "the daemon starts" 'ahd_alive'

# b267: the daemon signals only a process it has heard since it started. The bare stand-in pulls its own box once, with an
# adapter, from a child of its own process, so the daemon's peer proof names it and records it as heard.
printf 'exec node -e %q %q %q\n' 'const [sock, q] = process.argv.slice(1); require("http").get({ socketPath: sock, path: "/v1/inbox?" + q }, (r) => { console.log(r.statusCode); r.resume(); r.on("end", () => process.exit(0)); }).on("error", (e) => { console.log("error " + e.message); process.exit(1); });' \
  "$AH_HOME/run/ahd.sock" "$(node -e 'process.stdout.write(new URLSearchParams({address:"tm-implementor@tm",session_id:process.argv[1],adapter:"stub",since:"0",cwd:process.argv[2],hierarchy_dir:process.argv[3]}).toString())' "$SB1" "$PROJ" "$HIER")" > "$T/hear/$BARE"
waitfor 20 '[ -f "$T/hear/$BARE.done" ]'
OUT=$(cat "$T/hear/$BARE.out" 2>/dev/null)
check "the bare stand-in's own inbox pull is answered (the daemon has heard it)" '[ "$OUT" = 200 ]'

# ---- a chain-down team: its Orchestrator is dead (the proven subshell that filed this brief has exited) and its member is live
"$T/fakebin/claude" --stand-in-c1 & C1=$!; keep "$C1"
team tc tc-orch null "" "" c1:s-c1:w1:p7
TFC="$HIER/teams/tc.json"
ah_test_fake_herdr_agent "$FAKE_STATE_DIR" c1 w1:p7 "$PROJ"
register s-c1 c1 tc "$C1"
ah_as orchestrator tc tc-orch msg new --type request --to implementor --to-name c1 --from orchestrator --slug c-work --title "Finish the open work" --team tc --cwd "$PROJ" --json
CWORK=$(field "$OUT" 'o.id')
OUT="$OUT $(cat "$T/err")"; check "the dead Orchestrator's brief for c1 exists" '[ -n "$CWORK" ]'

# tm-implementor has open work, l1 is live: the daemon sees tm-implementor bare before the quit
ah_top msg new --type request --to implementor --to-name tm-implementor --from orchestrator --slug open-work --title "Open work" --team tm --cwd "$PROJ" --json
OPEN=$(field "$OUT" 'o.id')
check "the open brief for tm-implementor exists" '[ -n "$OPEN" ]'

# the nearest send that must stay refused: the same sender to the same lane member, outside a quit
ah_top msg send l1@tm-ws1 --kind note --text "not through the lead" --cwd "$PROJ"
OUT="rc=$RC $OUT $(cat "$T/err")"
check "an ordinary send from the top to a lane member is still refused" 'printf %s "$OUT" | grep -q outside_team'

touch "$PROJ/wip"   # a dirty tree, so the live lane member is asked to checkpoint rather than recorded idle and clean
ah_top roster quit --all --wait 1 --cwd "$PROJ"
QUIT="$OUT"; QUITRC=$RC; printf '%s\n%s\n' "$OUT" "$(cat "$T/err")" > "$T/quit.out"
check "quit --all seals both teams" 'held_own tm && held_own tm-ws1'

# A-1: the top Orchestrator's checkpoint reached the lane member, and its answer reads back
LANEREC=$(checkpoint_body tm-ws1)
ASK=$(field "$LANEREC" 'o.members.find(m=>m.name==="l1").request')
OUT="quit rc=$QUITRC $QUIT err=$(cat "$T/err") rec=$LANEREC"
check "(A-1) the quit reports no mail failure" '[ "$(field "$QUIT" "o.teams.every(t=>t.mail_failed===false)")" = true ]'
check "(A-1) the quit's checkpoint asked the lane member" '[ -n "$ASK" ]'
check "(A-1) the checkpoint request is in the lane member's box" 'in_box tm-ws1 "$ASK" l1'
printf '## [1] status\nCheckpoint: ready branch=work sha=%s inflight=none\n' "$SHA" > "$T/answer"
OUT=$(AH_TEST_AS_SESSION=s-l1 ah_test_run_as l1 tm-ws1 "$HIER/teams/tm-ws1.json" timeout 60 bash -c 'cd "$0" && "$1/bin/ah" msg fill --id "$2" --from implementor --body-file "$3" --cwd "$0"' "$PROJ" "$PLUGIN" "$ASK" "$T/answer" 2>&1); RC=$?
check "(A-1) the lane member's answer is filed" '[ "$RC" = 0 ]'
rm -f "$PROJ/wip"
ah_top msg show "$ASK" response --cwd "$PROJ"
check "(A-1) the Orchestrator reads the answer back" 'printf %s "$OUT" | grep -q "Checkpoint: ready"'

# ---- the reboot: tm-implementor stays bare and held; resume --all lifts the holds
waitfor 80 '[ "$(recover_state tm tm-implementor)" = held ]'
OUT="state=$(recover_state tm tm-implementor) log=$(tail -5 "$AH_HOME"/logs/ahd.log* 2>/dev/null)"
check "the daemon holds the bare member while its team is held" '[ "$(recover_state tm tm-implementor)" = held ]'
ah_top roster resume --all --yes --cwd "$PROJ"
check "resume --all --yes lifts both holds" '! held_own tm && ! held_own tm-ws1'

# ---- A-3: the daemon stops the bare process, launches, and once the launch is proved sends the brief under its own principal.
waitfor 300 'ah_test_fake_herdr_calls "$FAKE_STATE_DIR" | grep -q "^\[\"agent\",\"start\",\"tm-implementor\""'
OUT="state=$(recover_state tm tm-implementor) log=$(tail -5 "$AH_HOME"/logs/ahd.log* 2>/dev/null)"
check "the daemon relaunches the held bare member" 'ah_test_fake_herdr_calls "$FAKE_STATE_DIR" | grep -q "^\[\"agent\",\"start\",\"tm-implementor\""'
START=$(ah_test_fake_herdr_calls "$FAKE_STATE_DIR" | grep '^\["agent","start","tm-implementor"' | tail -1)
check "the relaunch resumes the member's own session" 'printf %s "$START" | grep -q -- "\"--resume\",\"$SB1\""'

# What the relaunched session's own start would do: a process with ah's launch settings, its row and its registration.
TFB="$HIER/teams/tm.json"
SETTINGS=$(node -e 'process.stdout.write(JSON.stringify({env:{WRANGL_TEAM_FILE:process.argv[1],AH_MEMBER_NAME:"tm-implementor"}}))' "$TFB")
"$T/fakebin/claude" --agent wrangl:implementor --model sonnet --resume $SB1 --settings "$SETTINGS" & UP=$!; keep "$UP"
sleep 0.3
pane_holds w1:p5 "$UP" "$PROJ" claude --agent wrangl:implementor --model sonnet --resume $SB1 --settings "$SETTINGS"
edit_team tm 'const m=t.members.find(x=>x.name==="tm-implementor");m.pid=Number(a[0]);m.pid_start=a[1]' "$UP" "$(startof "$UP")"
register $SB1 tm-implementor tm "$UP"
# The proof lands on the next bare sweep after the launch; under the fake herdr a sweep has been seen to take over 2 min (half-second steps).
waitfor 480 '[ "$(recover_state tm tm-implementor)" = done ]'
OUT="state=$(recover_state tm tm-implementor) journal=$(tail -3 "$AH_HOME/stall/journal.jsonl" | cut -c1-300)"
check "the daemon proves the relaunch" '[ "$(recover_state tm tm-implementor)" = done ]'
waitfor 40 'in_box tm "$OPEN" tm-implementor || in_box tm member-relaunch-failed orchestrator'
OUT="failed=$(grep -s member-relaunch-failed "$(boxdir "$TFB" orchestrator)/entries.jsonl" | cut -c1-500)"
check "(A-3) the daemon raised no failed-brief event" '! in_box tm member-relaunch-failed orchestrator'
check "(A-3) the daemon sent tm-implementor its open brief" 'in_box tm "$OPEN" tm-implementor'
printf '## [1] status\nanswer-from-tm-implementor\n' > "$T/answer-tm-implementor"
OUT=$(AH_TEST_AS_SESSION=$SB1 ah_test_run_as tm-implementor tm "$TFB" timeout 60 bash -c 'cd "$0" && "$1/bin/ah" msg fill --id "$2" --from implementor --body-file "$3" --cwd "$0"' "$PROJ" "$PLUGIN" "$OPEN" "$T/answer-tm-implementor" 2>&1); RC=$?
check "(A-3) tm-implementor answers the daemon's brief" '[ "$RC" = 0 ]'
ah_top msg show "$OPEN" response --cwd "$PROJ"
check "(A-3) the Orchestrator reads that answer back" 'printf %s "$OUT" | grep -q answer-from-tm-implementor'

# ---- the chain-down team: past the grace the daemon has wound c1 down; a new session then takes the team
waitfor 240 'in_box tc wind-down c1'
OUT="box=$(cut -c1-400 "$(boxdir "$TFC" c1)/entries.jsonl" 2>/dev/null)"
check "the daemon winds the member down past its grace" 'in_box tc wind-down c1'
ah_top roster resume --all --cwd "$PROJ"
check "resume --all lists the team as not owned, with the plain checkpoint command" 'printf %s "$OUT" | grep -q "ah roster resume --checkpoint --team tc --yes"'
OUT=$(AH_TEST_AS_SESSION=new-orch ah_test_run_as orchestrator tc "" timeout 60 bash -c 'cd "$0" && "$1/bin/ah" roster resume --checkpoint --team tc --yes --cwd "$0"' "$PROJ" "$PLUGIN" 2>&1); RC=$?
RESUMED="$OUT"
check "a new session resumes the chain-down team" '[ "$RC" = 0 ] && [ "$(field "$RESUMED" "o.record_only")" = taken ]'
check "the fresh record holds the brief open now" 'R=$(field "$RESUMED" "o.checkpoint"); [ -n "$R" ] && checkpoint_key "$R" | grep -q "$CWORK"'
CENTRY=$(field "$RESUMED" 'o.members[0].entries[0]')
check "(chain A-3) the brief reaches c1 past the active row" '[ -n "$CENTRY" ] && in_box tc "$CENTRY" c1 && in_box tc "$CWORK" c1'
check "the new session took the team" '[ "$(field "$RESUMED" "o.orchestrator")" = transferred ]'

echo "----"
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
