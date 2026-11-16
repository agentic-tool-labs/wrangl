#!/bin/bash
# K5 scenario 12: the native terminal client's plain session against a running native daemon (U-D2, native half). The same fixture tree
# gives the same snapshot on every read, the snapshot lists the fixture team, and once the daemon is stopped the client
# reports it down with exit 3. Native only: the client is `ahd-tui` beside AHD_NATIVE_BIN and the daemon is the native one.
. "$(dirname "$0")/_lib.sh"
if [ "$IMPL_CLIENT" != native ] || [ "$IMPL_DAEMON" != native ]; then echo "this scenario's pair is native client and native daemon (client=$IMPL_CLIENT daemon=$IMPL_DAEMON)"; exit 77; fi
lc_as_harness "$@"
lc_runtimes
lc_init
TUI="$(dirname "$AHD_NATIVE_BIN")/ahd-tui"
[ -x "$TUI" ] || { echo "no ahd-tui beside $AHD_NATIVE_BIN"; exit 77; }
# Other suites run this binary at the same time, so this scenario's own runs go by a name only this sandbox has.
ln -s "$TUI" "$T/ahd-tui"
TUI="$T/ahd-tui"
unset NO_COLOR COLUMNS
printf '{"team_id":"t-ui","orchestrator":{"pid":%s,"pid_start":"%s","session_id":"orch-ui"},"members":[{"role":"implementor","name":"ui-implementor","transport_id":"p1"}]}\n' "$$" "$(lc_start_of $$)" >"$PROJ/.claude/ah2/teams/ui.json"

# The team list enumerates known directories, so a session's hooks would have noted this one.
lc_state known-dirs note "{\"hierarchy_dir\":\"$PROJ/.claude/ah2\"}" "$PROJ/.claude/ah2" >/dev/null

tui() { (cd "$PROJ" && COLUMNS=120 timeout 30 "$TUI" --once 2>"$T/tui.err"); }

lc_ctl start >/dev/null 2>&1
PID=$(lc_info_pid)
lc_check "a native daemon is up" 'lc_alive "$PID"'

TF="$PROJ/.claude/ah2/teams/ui.json"
BOX=$(AH_HOME="$AH_HOME" node -e 'const c=require("crypto"),fs=require("fs"),p=require("path");const f=process.argv[1];let r=f;try{r=fs.realpathSync(f)}catch{}console.log(p.join(process.env.AH_HOME,"mailbox",p.basename(f).replace(/\.json$/,"")+"~"+c.createHash("sha256").update(r).digest("hex").slice(0,10),process.argv[2]))' "$TF" ui-implementor)
mkdir -p "$BOX"
node -e '
const http=require("http");
const body=JSON.stringify({session_id:"orch-ui",addresses:["ui-implementor@ui"],boxes:{"ui-implementor@ui":process.argv[2]},harness:"claude",pid:Number(process.argv[3]),team_file:process.argv[4],cwd:process.argv[5]});
const r=http.request({socketPath:process.argv[1],path:"/v1/register",method:"POST",headers:{"Content-Type":"application/json","Content-Length":Buffer.byteLength(body)}},(res)=>{res.resume();res.on("end",()=>console.log(res.statusCode))});
r.on("error",()=>console.log("error"));r.end(body);' "$AH_HOME/run/ahd.sock" "$BOX" "$$" "$TF" "$PROJ" >"$T/reg.out"
lc_check "a registration with a live pid is accepted" 'grep -q "^200$" "$T/reg.out"'

# A loaded machine (40 copies of this scenario at once) can keep the team list from a frame's 15 s wait for a building daemon, so the
# reads start once the summary serves the list; this read is a client, so the frames below are of a daemon a client has held.
node -e '
const http=require("http");const t0=Date.now();const end=t0+150000;
const get=()=>http.get({socketPath:process.argv[1],path:"/v1/view/summary"},(res)=>{let b="";res.on("data",(d)=>{b+=d});res.on("end",()=>{
  let j={};try{j=JSON.parse(b)}catch{}
  const l=(j.slices||[]).find((s)=>s.id==="list:teams");
  if(l&&((l.body&&l.body.teams)||[]).some((t)=>t&&t.name==="ui")){console.log("time to list: "+(Date.now()-t0)+" ms");process.exit(0)}
  if(Date.now()>end){console.log("time to list: over 150000 ms");process.exit(1)}setTimeout(get,250)})}).on("error",()=>{if(Date.now()>end){console.log("time to list: over 150000 ms");process.exit(1)}setTimeout(get,250)});
get();' "$AH_HOME/run/ahd.sock"; RC0=$?
lc_check "the daemon's summary lists the fixture team within 150 s" '[ "$RC0" -eq 0 ]'

FIRST=$(tui); RC1=$?
SECOND=$(tui); RC2=$?
TEAM=$(cd "$PROJ" && COLUMNS=120 timeout 30 "$TUI" --once --team ui 2>"$T/tui.err"); RC4=$?
lc_check "the client exits 0 against the running daemon" '[ "$RC1" -eq 0 ] && [ "$RC2" -eq 0 ]'
lc_check "the snapshot lists the fixture team" 'printf "%s\n" "$FIRST" | grep -E "^ +ui " >/dev/null'
no_age() { printf '%s\n' "$1" | sed -E 's/updated [0-9]+[a-z]+ ago/updated/'; }
lc_check "two reads of the same tree give the same snapshot, apart from the age of the read" '[ "$(no_age "$FIRST")" = "$(no_age "$SECOND")" ]'
lc_check "--team's first frame of its own has the team's sitrep, not loading" '[ "$RC4" -eq 0 ] && printf "%s\n" "$TEAM" | grep -q "\[-\] Completed 0" && ! printf %s "$TEAM" | grep -q "loading"'
lc_check "no daemon-down text while the daemon is up" '! printf %s "$FIRST" | grep -qi "is not running"'

lc_ctl stop >/dev/null 2>&1
lc_wait_for 8 bash -c "! kill -0 $PID 2>/dev/null"
DOWN=$(tui); RC3=$?
lc_check "with the daemon stopped the client exits 3" '[ "$RC3" -eq 3 ]'
lc_check "...and the snapshot is the one down line, listing no team" '[ "$DOWN" = "ahd is not running" ]'
lc_check "no ahd-tui process of this sandbox remains" '[ -z "$(pgrep -f "$TUI --once" 2>/dev/null)" ]'
[ "$LC_FAIL" -eq 0 ] || printf '%s\n' "--- first" "$FIRST" "--- second" "$SECOND" "--- team" "$TEAM" | cut -c1-120
lc_done
