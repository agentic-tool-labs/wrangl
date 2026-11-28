#!/bin/bash
# agent-hierarchy — a team on hold stays on hold. T1 replays the 2026-10-24 failure: a team is quit, its members go bare, ahd is
# stopped and started, and the hooks of the held sessions fire. Nothing may start, relaunch, nudge, alarm or inject mail for the team:
# no ahd comes back from a hook, no launch is recorded, mail sent to the team ends in a drained row, the hooks print no block, and
# `quit --status` says the team is held. Every process started is stopped.
# Usage: bash tests/test-ahd-team-hold.sh   (exits 0 iff all cases pass; needs AHD_NATIVE_BIN or AH_BIN)

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
mkdir -p "$AH_HOME/run" "$HOME/.claude/ah2" "$HIER/teams" "$HIER/msgs"
git -C "$PROJ" init -q
sleep 3000 & LIVE=$!
daemon_pids() {
  { node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid)+"\n")}catch{}' "$AH_HOME/run/ahd.json"
    cat "$AH_HOME"/logs/ahd.log* 2>/dev/null | sed -n 's/.* pid \([0-9]*\) root .*/\1/p'; } | sort -u
}
cleanup() {
  local p
  [ -s "${AH_TEST_TRIPWIRE:-}" ] && { echo "TRIPWIRE:"; cat "$AH_TEST_TRIPWIRE"; }
  for p in $(daemon_pids); do ah_signal_ours CONT "$p" "$T"; ah_signal_ours TERM "$p" "$T"; done
  ah_signal_child CONT "$LIVE"; ah_signal_child TERM "$LIVE"
  for p in ${HARNESS:-} ${JOBP:-} ${JOBX:-} ${JOBR:-} ${ORCHPID:-} ${ZPAR:-}; do pkill -TERM -P "$p" 2>/dev/null; ah_signal_child TERM "$p"; done  # pid-owned: the jobs this suite started and has not reaped
  sleep 0.3
  for p in ${HARNESS:-} ${JOBP:-} ${JOBX:-} ${JOBR:-} ${ORCHPID:-} ${ZPAR:-}; do pkill -KILL -P "$p" 2>/dev/null; ah_signal_child KILL "$p"; done  # pid-owned: the jobs this suite started and has not reaped
  for p in $(daemon_pids); do ah_signal_ours KILL "$p" "$T"; done
  ah_signal_child KILL "$LIVE"
  local left; left=$(pgrep -f "$T" 2>/dev/null | grep -v "^$$\$" | tr '\n' ' ')
  [ -n "${left// /}" ] && echo "FAIL: processes still alive under $T: $left"
  rm -rf "$T"
}
hermetic_on_exit cleanup
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n"; printf -- '--- OUT:\n%s\n--- ahd log tail:\n' "$OUT"; tail -n 20 "$AH_HOME"/logs/ahd.log* 2>/dev/null; fi; }
put() { node -e 'const fs=require("fs"),path=require("path");fs.mkdirSync(path.dirname(process.argv[1]),{recursive:true});fs.writeFileSync(process.argv[1],process.argv[2]+"\n")' "$1" "$2"; }
NOW() { node -e 'process.stdout.write(new Date().toISOString())'; }
# the team's hold document as `roster quit --status` reads it (the holds table; S11a-holds), pretty-printed
hold_of() { (cd "$PROJ" && "$PLUGIN/bin/ah" roster quit --status --team "$1" --cwd "$PROJ" 2>/dev/null) | node -e 'try{const o=JSON.parse(require("fs").readFileSync(0,"utf8"));if(o.hold)process.stdout.write(JSON.stringify(o.hold,null,2))}catch{}'; }
. "$PLUGIN/tests/lib-seed.sh"
# <team file> <js over t, with o the document of $TF>: rewrites that stored team through the writer.
team_js() { ah_team_doc "$1" | node -e 'const fs=require("fs");const t=JSON.parse(fs.readFileSync(0,"utf8")),o=JSON.parse(process.argv[2]||"{}");eval(process.argv[1]);console.log(JSON.stringify(t))' "$2" "$(ah_team_doc "$TF")" | AH_DAEMON=off ah_seed_team "$1"; }
# <session id> <address> <box dir> <pid>: a stub-harness registration of that session, through the real register op.
register() { printf '{"session_id":"%s","address":"%s","boxes":[{"address":"%s","dir":"%s"}],"team_file":"%s","harness":"stub","pid":%s,"pane_id":null,"cwd":"%s"}' "$1" "$2" "$2" "$3" "$TF" "$4" "$PROJ" | AH_DAEMON=off seed_session; }
# <a box dir of the team>: every stored box of that team as {box: {es: [entries], rs: [attempts]}}, each in write order.
boxdump() {
  sqlite3 -readonly -separator $'\t' "$AH_HOME/state/ah.db" "SELECT 'es', box, CAST(raw AS TEXT) FROM mail_entries ORDER BY pk; SELECT 'rs', box, CAST(raw AS TEXT) FROM mail_attempts ORDER BY pk" \
    | node -e 'const pre=process.argv[1]+"/";const B={};for(const l of require("fs").readFileSync(0,"utf8").split("\n").filter(Boolean)){const [k,box,...r]=l.split("\t");if(!box.startsWith(pre))continue;(B[box]??={es:[],rs:[]})[k].push(JSON.parse(r.join("\t")))}process.stdout.write(JSON.stringify(B))' "$(basename "$(dirname "$1")")"
}
boxdir() { node -e 'const c=require("crypto"),fs=require("fs"),path=require("path");const tf=path.join(fs.realpathSync(path.dirname(process.argv[1])),path.basename(process.argv[1]));const h=c.createHash("sha256").update(tf).digest("hex").slice(0,10);process.stdout.write(path.join(process.env.AH_HOME,"mailbox",path.basename(tf).replace(/\.json$/,"")+"~"+h,process.argv[2]))' "$1" "$2"; }
ahd_pid() { node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid))}catch{}' "$AH_HOME/run/ahd.json"; }
ahd_alive() { local p; p=$(ahd_pid); [ -n "$p" ] && kill -0 "$p" 2>/dev/null; }
start_daemon() {
  local i
  ( cd "$PROJ" && exec "$AH_BIN" --plugin-root "$PLUGIN" serve --ah-home "$AH_HOME" >"$T/start.out" 2>&1 ) &
  for i in $(seq 1 20); do ahd_alive && return 0; sleep 0.5; done
  OUT="$(cat "$T/start.out" 2>/dev/null) :: $(tail -3 "$AH_HOME"/logs/ahd.log* 2>/dev/null)"
  return 1
}
stop_daemon() {
  local p
  for p in $(daemon_pids); do ah_signal_ours TERM "$p" "$T"; done
  for p in $(seq 1 20); do ahd_alive || return 0; sleep 0.5; done
  for p in $(daemon_pids); do ah_signal_ours KILL "$p" "$T"; done
  return 0
}

# Fake herdr (state in $FAKE_STATE_DIR, one file per call) and a fake tmux that logs every call and shows an empty screen, so a paste is
# refused. They are first on PATH and the only binaries the guard admits, so nothing here can reach a real pane.
mkdir -p "$T/fakebin" "$T/herdr-state"
printf '#!/bin/sh\necho "$*" >> "%s/tmux.log"\nexit 0\n' "$T" > "$T/fakebin/tmux"; chmod +x "$T/fakebin/tmux"
ah_test_install_fake_herdr "$T/fakebin"
export AH_TEST_FAKE_BIN="$T/fakebin"
export FAKE_STATE_DIR="$T/herdr-state" PATH="$T/fakebin:$PATH"
herdr_starts_after() { node -e '
  const fs = require("fs"), d = process.argv[1] + "/calls", since = Number(process.argv[2]);
  let n = 0;
  try { for (const f of fs.readdirSync(d)) { const c = JSON.parse(fs.readFileSync(d + "/" + f, "utf8")); if (c.argv[0] === "agent" && c.argv[1] === "start" && c.start_ms >= since) n++; } } catch {}
  process.stdout.write(String(n));' "$FAKE_STATE_DIR" "$1"; }

TF="$HIER/teams/tm.json"
ah_test_team "$HIER" tm implementor:m1 implementor:m2 implementor:m3 implementor:m4
team_js "$TF" 't.orchestrator = { pid: '"$LIVE"', session_id: "orch" }; t.members.forEach((m, i) => { m.session_id = "s-" + m.name; m.paste = false; m.transport_id = "%" + (i + 1); })'
ODIR=$(boxdir "$TF" orchestrator); M1=$(boxdir "$TF" m1); M2=$(boxdir "$TF" m2)
register orch orchestrator@tm "$ODIR" "$LIVE"
ah_seed_home_doc home.recover-switch '{"on":false}'

orch() { AH_TEST_AS_SESSION=orch ah_test_run_as orchestrator tm "$TF" "$@"; }
ah_run() { OUT=$(orch timeout 60 bash -c 'cd "$0" && "$1/bin/ah" "${@:2}"' "$PROJ" "$PLUGIN" "$@" 2>"$T/err"); RC=$?; }
hook() { printf '%s' "$2" | orch sh "$PLUGIN/bin/ah-hook" "$1" 2>"$T/hook.err"; }

start_daemon
check "the daemon starts" 'ahd_alive'

# Mail queued before the quit.
ah_run msg send m1@tm --from orchestrator@tm --text "queued before the quit" --json
PRE=$(node -e 'try{process.stdout.write(JSON.parse(process.argv[1]).entry.id)}catch{}' "$OUT")

QUIT_MS=$(node -e 'process.stdout.write(String(Date.now()))')
# 1. quit
OUT=$(orch bash -c "cd '$PROJ' && '$PLUGIN/bin/ah' roster quit --team tm --wait 1 --cwd '$PROJ'" 2>"$T/err"); RC=$?
SINCE=$(node -e 'try{process.stdout.write(JSON.parse(process.argv[1]).since)}catch{}' "$OUT")
check "quit seals the hold and exits 0 or 3" '[ "$RC" = 0 ] || [ "$RC" = 3 ]'
check "quit prints a plan that exists" 'node -e "process.exit(require(\"fs\").existsSync(JSON.parse(process.argv[1]).plan)?0:1)" "$OUT"'
check "(R1) the plan's Drained mail lists the mail that was queued before the quit" 'P=$(node -e "process.stdout.write(JSON.parse(process.argv[1]).plan)" "$OUT"); sed -n "/^## Drained mail/,/^## Open requests/p" "$P" | grep -q "$PRE"'
check "(R1) the plan lists the mail once" 'P=$(node -e "process.stdout.write(JSON.parse(process.argv[1]).plan)" "$OUT"); [ "$(grep -c "$PRE" "$P")" = 1 ]'
check "the hold says held" 'hold_of tm | grep -q "\"phase\": \"held\""'

# 2. mail sent to the held team is drained
ah_run msg send m2@tm --from orchestrator@tm --text "after the quit" --json
POST=$(node -e 'try{process.stdout.write(JSON.parse(process.argv[1]).entry.id)}catch{}' "$OUT")
SENT="rc=$RC out=$OUT err=$(cat "$T/err")"
check "(f) a message to the held team reports drained" 'if [ -z "$POST" ]; then OUT="$SENT"; false; else ah_run msg status "$POST"; printf %s "$OUT" | grep -q drained; fi'
check "(f) mail queued before the quit reports drained" 'ah_run msg status "$PRE"; printf %s "$OUT" | grep -q drained'

# 3. ahd stopped, then the hooks of the held sessions fire
stop_daemon
HOOKOUT="$(hook stop-orchestrator-liveness "{\"hook_event_name\":\"Stop\",\"session_id\":\"orch\",\"cwd\":\"$PROJ\"}")$(hook userpromptsubmit-peer-tracking "{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"orch\",\"cwd\":\"$PROJ\",\"prompt\":\"hello\"}")"
check "(a) no ahd after the hooks of a held team fired" '! ahd_alive'
check "(e) the hooks printed no block" '! printf %s "$HOOKOUT" | grep -q "\"decision\":\"block\""'

# 4. ahd started by hand: nothing for the held team
start_daemon
sleep 10
check "(b)(c) the fake herdr saw no agent start after the quit" '[ "$(herdr_starts_after "$QUIT_MS")" = 0 ]'
check "(b)(c) no member was relaunched or stopped" '! ls "$AH_HOME"/recover/*.json "$AH_HOME"/recover/*/* 2>/dev/null | xargs -r grep -l launch_started_at 2>/dev/null | grep -q .'
check "(d) every entry in a team box carries a drained row" '
  node -e "
    const B=JSON.parse(process.argv[1]);let bad=0;
    for(const box in B){const {es,rs}=B[box];
      for(const e of es){const last=rs.filter(r=>r.id===e.id&&r.ladder===true).pop();
        if(e.kind===\"event\"||e.from===\"ahd\"||e.id===process.argv[2]||e.id===process.argv[3]){if(!last||last.state!==\"drained\"){bad++;console.error(\"not drained\",box,e.id,e.event||e.kind)}}}}
    process.exit(bad?1:0)" "$(boxdump "$M1")" "$PRE" "$POST"'
check "(F2) mail queued before the quit is drained before the checkpoint's own briefs get any row" '
  node -e "
    const B=JSON.parse(process.argv[1]);let seen=0,bad=0;
    for(const box in B){const rs=B[box].rs,es=B[box].es.map(e=>e.id);
      const at=rs.findIndex(r=>r.id===process.argv[2]&&r.ladder===true&&r.state===\"drained\");
      if(at<0)continue;
      seen++;
      const later=new Set(es.slice(es.indexOf(process.argv[2])+1));
      if(rs.slice(0,at).some(r=>later.has(r.id)))bad++;
      if(!rs.slice(at).some(r=>later.has(r.id)))bad++}
    process.exit(seen&&!bad?0:1)" "$(boxdump "$M1")" "$PRE"'
ah_run roster quit --status --team tm --cwd "$PROJ"
check "(g) quit --status exits 5 for the held team" '[ "$RC" = 5 ]'
check "(a) the hold survived the ahd restart" 'hold_of tm | grep -q "\"phase\": \"held\""'
stop_daemon

# 5. T2: resume from quit plans without changing anything, and a held team refuses the plain verbs
ah_run roster resume --hold --team tm --cwd "$PROJ"
check "(T2) resume --hold without --yes plans and exits 0" '[ "$RC" = 0 ] && printf %s "$OUT" | grep -q "\"status\": *\"planned\""'
check "(T2) the hold is still held after the plan" 'hold_of tm | grep -q "\"phase\": \"held\""'
ah_run roster resume --team tm --cwd "$PROJ"
check "(T2) plain resume of a held team is refused team_held" '[ "$RC" != 0 ] && printf %s "$OUT$(cat "$T/err")" | grep -q team_held'
ah_run roster resume --hold --team tm --all --cwd "$PROJ"
check "(T2) resume --hold --all is refused bad_flags" '[ "$RC" != 0 ] && printf %s "$OUT$(cat "$T/err")" | grep -q bad_flags'

# 6. stop: the guard, the plan, then the stop itself over the quit
ah_run roster stop --team tm --cwd "$PROJ"
check "(T3) stop without --now is refused stop_needs_now" '[ "$RC" != 0 ] && printf %s "$OUT$(cat "$T/err")" | grep -q stop_needs_now'
ah_run roster stop --now oops --team tm --cwd "$PROJ"
check "(T3) stop --now with a word is refused: --now takes no value, got oops" '[ "$RC" != 0 ] && printf %s "$OUT$(cat "$T/err")" | grep -q "takes no value, got oops"'

startof() { TZ=UTC ps -o lstart= -p "$1" | sed 's/^ *//; s/ *$//'; }
stored_emergency_stop() { sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT CAST(b.bytes AS TEXT) FROM msg_bodies b JOIN msg_messages m ON m.pk = b.msg_pk WHERE m.slug = 'emergency-stop'" | grep -q "EMERGENCY STOP for $1"; }
dead() { local s; s=$(ps -o stat= -p "$1" 2>/dev/null | tr -d ' '); [ -z "$s" ] || [ "${s#Z}" != "$s" ]; }
sh -c 'sleep 2001 & wait' & HARNESS=$!
sh -c 'sleep 2002 & wait' & JOBP=$!
sleep 1
register s-m2 m2@tm "$M2" "$HARNESS"
jobrec() { ah_seed_job "$HIER" tm "$1" "{\"id\":\"$1\",\"team\":\"tm\",\"owner\":\"m2\",\"session_id\":\"s-m2\",\"kind\":\"bg\",\"eta\":\"medium\",\"label\":\"$1\",\"background\":true,\"pid\":$2,\"pid_start\":$3}"; }
jobrec job-real "$JOBP" "\"$(startof "$JOBP")\""
jobrec job-harness "$HARNESS" "\"$(startof "$HARNESS")\""
jobrec job-ancestor "$$" "\"$(startof $$)\""
sleep 2005 & ORCHPID=$!
team_js "$TF" 't.members.find((m) => m.name === "m3").pid = '"$ORCHPID"
jobrec job-orch "$ORCHPID" "\"$(startof "$ORCHPID")\""
jobrec job-null null null
sleep 2003 & JOBX=$!
sleep 2004 & JOBR=$!
jobrec job-late "$JOBX" "\"$(startof "$JOBX")\""
jobrec job-reused "$JOBR" "\"Thu Jan  1 00:00:00 1970\""
( sleep 1; register s-late late@tm "$M2" "$JOBX" ) &
ah_run roster stop --now --team tm --cwd "$PROJ"
check "(T3) stop --now without --confirm only plans" '[ "$RC" = 0 ] && printf %s "$OUT" | grep -q "\"status\": *\"planned\"" && hold_of tm | grep -q "\"kind\": \"quit\""'
ah_run roster stop --now --team tm --confirm --grace 3 --reason "test stop" --cwd "$PROJ"
STOPOUT="$OUT"; STOPRC=$RC
check "(T3) stop --confirm exits 0 and says stopped" '[ "$STOPRC" = 0 ] && printf %s "$STOPOUT" | grep -q "\"phase\": *\"stopped\""'
check "(T2.2) with a session process alive and listed, stop --confirm exits 0" '[ "$STOPRC" = 0 ] && node -e "process.exit(JSON.parse(process.argv[1]).session_processes.length>0?0:1)" "$STOPOUT"'
check "(T3) the hold is a stop and nests the quit" 'hold_of tm | grep -q "\"kind\": \"stop\"" && hold_of tm | grep -q "\"previous\": {"'
check "(T3) the evidence file exists" 'node -e "process.exit(require(\"fs\").existsSync(JSON.parse(process.argv[1]).evidence)?0:1)" "$STOPOUT"'
check "(T3) ahd is still not running after the stop" '! ahd_alive'
check "(T3) Esc went to the pane and no Ctrl-C was sent" 'grep -q "send-keys -t %2 Escape" "$T/tmux.log" && ! grep -qiE "C-c|\^C" "$T/tmux.log"'
check "(T3) a refused paste falls back to a mail brief, via mail" 'printf %s "$STOPOUT" | grep -q "\"via\": *\"mail\"" && stored_emergency_stop "team tm"'
check "(T3) the registered job and its child are stopped" 'dead "$JOBP" && ! pgrep -f "sleep 2002" >/dev/null && printf %s "$STOPOUT" | grep -q "\"job_id\": *\"job-real\""'
check "(T3) the harness stand-in and its MCP-stand-in child are alive" '! dead "$HARNESS" && pgrep -f "sleep 2001" >/dev/null'
check "(T2.1) the MCP stand-in is listed under session_processes and the printed result has no still_running key" 'node -e "const r=JSON.parse(process.argv[1]);process.exit(r.session_processes.some(p=>JSON.stringify(p).includes(\"sleep 2001\"))&&!(\"still_running\" in r)&&typeof r.session_processes_note===\"string\"?0:1)" "$STOPOUT"'
check "(T2.1) the evidence file keeps the stand-in under still_running" 'node -e "const fs=require(\"fs\");const r=JSON.parse(process.argv[1]);const e=JSON.parse(fs.readFileSync(r.evidence,\"utf8\"));process.exit(e.still_running.some(p=>JSON.stringify(p).includes(\"sleep 2001\"))?0:1)" "$STOPOUT"'
check "(T3) the job with no pid is listed unverified" 'printf %s "$STOPOUT" | grep -q "job-null"'
check "(T3) a job whose pid is a harness is excluded and the harness lives" '! dead "$HARNESS" && node -e "process.exit(JSON.parse(process.argv[1]).jobs_excluded.some(j=>j.job_id===\"job-harness\")?0:1)" "$STOPOUT"'
check "(T3) a job whose pid is a caller ancestor is excluded and the test lives" 'node -e "process.exit(JSON.parse(process.argv[1]).jobs_excluded.some(j=>j.job_id===\"job-ancestor\")?0:1)" "$STOPOUT"'
check "(T3) a job whose pid is a member pid recorded in the team file is excluded and lives" '! dead "$ORCHPID" && node -e "process.exit(JSON.parse(process.argv[1]).jobs_excluded.some(j=>j.job_id===\"job-orch\")?0:1)" "$STOPOUT"'
ah_run roster quit --team tm --cwd "$PROJ"
check "(T3) quit over a stop is refused team_stopped" '[ "$RC" != 0 ] && printf %s "$OUT$(cat "$T/err")" | grep -q team_stopped'

check "(T3) a process registered during the grace is not stopped (the scan is after the grace)" '! dead "$JOBX"'
check "(T3) a job whose recorded start time is not its process start time is never signalled" '! dead "$JOBR"'

# 7. T4: resume from stop assesses first
ah_run roster resume --hold --team tm --yes --cwd "$PROJ"
check "(T4) resume --yes before the assessment is refused assess_first" '[ "$RC" != 0 ] && printf %s "$OUT$(cat "$T/err")" | grep -q assess_first'
ah_run roster resume --hold --team tm --cwd "$PROJ"
check "(T4) resume --hold without --yes assesses" '[ "$RC" = 0 ] && printf %s "$OUT" | grep -q "\"status\": *\"assessed\""'
check "(T4) the assessment file exists and is named in the hold" 'A=$(ls "$HIER"/holds/tm/*-assessment.md 2>/dev/null | head -1); [ -n "$A" ] && hold_of tm | grep -q "assessment"'

# 8. a stop that lands during a quit's wind-down survives the quit's seal
ah_test_team "$HIER" tm2 implementor:n1
team_js "$HIER/teams/tm2.json" 't.orchestrator = o.orchestrator; t.members.forEach((m) => { m.session_id = "s-" + m.name; m.paste = false; m.transport_id = "%7"; })'
# --wait 20: the quit's window runs from its since, and the stop lands only after the test's two gates below; under load that passed
# an 8 s window (2.0.67 QA: seal at since+8.15 s, stop at since+8.97 s)
( QS=$SECONDS; QOUT=$(orch timeout 60 bash -c 'cd "$0" && "$1/bin/ah" "${@:2}"' "$PROJ" "$PLUGIN" roster quit --team tm2 --wait 20 --cwd "$PROJ" 2>"$T/qerr"); echo "$?" > "$T/q.rc"; echo "$((SECONDS - QS))" > "$T/q.secs"; printf %s "$QOUT$(cat "$T/qerr")" > "$T/q.out" ) &
QPID=$!
# wait (deadline 30 s) until the quit has written its winding-down hold, so the stop lands during the wind-down
END=$((SECONDS + 30)); until hold_of tm2 | grep -q '"phase": "winding-down"' || [ "$SECONDS" -ge "$END" ]; do sleep 0.2; done
hold_of tm2 | grep -q '"phase": "winding-down"' || echo "note: the 30 s wait for the quit's winding-down hold ran out; the stop lands without it"
# the quit, past its owner check, still holds session orch live, and the store registers a session to one live process: its
# registration is dropped so the stop can prove the same Orchestrator session
# The quit's member mail runs `ah msg send` as session orch, which re-registers orch bound to the quit's live pid; a bound registration
# refuses the stop's unbound proof (exit 97). So wait (deadline 30 s) until that bound registration is written, then drop it.
bound_doc() { node -e 'try{process.exit(JSON.parse(process.argv[1]).bound===true?0:1)}catch{process.exit(1)}' "$1"; }
check "(F4) the bound-registration wait does not accept an unbound registration" '! bound_doc "{\"session_id\":\"orch\",\"bound\":false}"'
END=$((SECONDS + 30)); until bound_doc "$(ah_session_doc orch)" || [ "$SECONDS" -ge "$END" ]; do sleep 0.2; done
bound_doc "$(ah_session_doc orch)" || echo "note: the 30 s wait for the quit's bound registration of orch ran out; the stop proves without it"
ah_forget_sessions orch
# the stop's proof is seeded bound, so a bound registration the quit writes after the forget cannot refuse it
AH_TEST_PROVE_BOUND=1 ah_run roster stop --now --team tm2 --confirm --grace 0 --cwd "$PROJ"
wait "$QPID"
# one cause line first: which command failed, its exit code, the hold's kind and the phase the stop found
read -r HKIND HPREV <<EOF
$(hold_of tm2 | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{const h=JSON.parse(s);console.log(h.kind+" "+(h.previous?h.previous.phase:"none"))}catch{console.log("unreadable none")}})')
EOF
QRC=$(cat "$T/q.rc")
echo "note: (F4) the quit took $(cat "$T/q.secs") s of its --wait 20 window"
if [ "$RC" != 0 ]; then CAUSE="the stop failed rc=$RC; hold kind $HKIND"
elif [ "$HKIND" != stop ]; then CAUSE="hold kind $HKIND: the quit's seal overwrote the stop (stop rc=0, quit rc=$QRC)"
elif [ "$QRC" = 0 ]; then CAUSE="the quit exited 0 over the stop; hold kind stop"
elif ! grep -q team_stopped "$T/q.out"; then CAUSE="the quit exited $QRC without team_stopped; hold kind stop over a quit in phase $HPREV (held = the stop landed after the quit's window)"
elif [ "$(cat "$T/q.secs")" -ge 18 ]; then CAUSE="the quit waited $(cat "$T/q.secs") s of its --wait 20 window after the stop landed"
else CAUSE="none"; fi
OUT="cause: $CAUSE
quit elapsed: $(cat "$T/q.secs") s
stop: rc=$RC $OUT $(head -c 600 "$T/err") :: quit: rc=$(cat "$T/q.rc") $(cat "$T/q.out") :: hold: $(hold_of tm2)"
check "(F4) a stop that lands during the wind-down is not overwritten by the quit's seal" 'hold_of tm2 | grep -q "\"kind\": \"stop\"" &&[ "$(cat "$T/q.rc")" != 0 ] && grep -q team_stopped "$T/q.out" && [ "$(cat "$T/q.secs")" -lt 18 ]'

# 9. T1: STOP lands by pane when the member is still redrawing after the Esc, and falls back to mail with the reason when it never settles
herdr_team() {
  ah_test_team "$HIER" "$1" implementor:"$2"
  team_js "$HIER/teams/$1.json" 't.orchestrator = o.orchestrator; t.transport = "herdr"; t.members.forEach((m) => { m.session_id = "s-" + m.name; m.transport_id = "w1:p5"; })'
  ah_test_seed_fake_herdr "$FAKE_STATE_DIR" 200 50 "$PROJ" >/dev/null
  ah_test_fake_herdr_agent "$FAKE_STATE_DIR" "$2" w1:p5 "$PROJ"
}
herdr_prompts() { ah_test_fake_herdr_calls "$FAKE_STATE_DIR" | grep -c '^\["agent","prompt"'; }
# ms from the Esc (the first send-keys) to the end of the last herdr call: the stop's own work, apart from the CLI's start
herdr_span() { node -e 'const fs=require("fs"),d=process.argv[1]+"/calls";try{const r=fs.readdirSync(d).map(f=>JSON.parse(fs.readFileSync(d+"/"+f,"utf8")));const k=r.filter(c=>c.argv[0]==="agent"&&c.argv[1]==="send-keys");if(k.length)process.stdout.write(String(Math.max(...r.map(c=>c.end_ms))-Math.min(...k.map(c=>c.start_ms))))}catch{}' "$FAKE_STATE_DIR"; }
member_signalled() { node -e 'const r=JSON.parse(process.argv[1]);const s=(r.signalled||[]).find(x=>x.member===process.argv[2]);process.stdout.write(s?JSON.stringify(s):"")' "$1" "$2"; }

herdr_team tm5 q1
export FAKE_HERDR_READ_FAIL_FIRST=2
ah_run roster stop --now --team tm5 --confirm --grace 0 --cwd "$PROJ"
SIG=$(member_signalled "$OUT" q1)
check "(T1.1) a member still redrawing after the Esc is reached by pane once it settles, with no mail brief" 'printf %s "$SIG" | grep -q "\"via\": *\"pane\"" && printf %s "$SIG" | grep -q "\"ok\": *true" && [ "$(herdr_prompts)" = 1 ] && ! grep -lq "EMERGENCY STOP for team tm5" "$HIER"/msgs/*emergency-stop* 2>/dev/null'
unset FAKE_HERDR_READ_FAIL_FIRST

herdr_team tm6 q2
export FAKE_HERDR_READ_FAIL_FIRST=999
T0=$(node -e 'process.stdout.write(String(Date.now()))')
ah_run roster stop --now --team tm6 --confirm --grace 0 --cwd "$PROJ"
ELAPSED=$(( $(node -e 'process.stdout.write(String(Date.now()))') - T0 ))
SIG=$(member_signalled "$OUT" q2)
check "(T1.2) a member that never settles is reached by mail after the 8 s window, with the reason and why, and nothing typed" 'printf %s "$SIG" | grep -q "\"via\": *\"mail\"" && printf %s "$SIG" | grep -q "pane_error\": *\"indeterminate: [^\"]\+" && [ "$ELAPSED" -ge 8000 ] && [ "$(herdr_prompts)" = 0 ] && stored_emergency_stop "team tm6"'
unset FAKE_HERDR_READ_FAIL_FIRST

# The 8 s window is wall-clock from the Esc: slow reads must not stretch it to 8 tries of (1 s + read time).
herdr_team tm8 q3
export FAKE_HERDR_READ_FAIL_FIRST=999 FAKE_HERDR_READ_DELAY_MS=2000
T0=$(node -e 'process.stdout.write(String(Date.now()))')
ah_run roster stop --now --team tm8 --confirm --grace 0 --cwd "$PROJ"
ELAPSED=$(( $(node -e 'process.stdout.write(String(Date.now()))') - T0 ))
SIG=$(member_signalled "$OUT" q3)
SPAN=$(herdr_span)
OUT="span_ms=$SPAN elapsed_ms=$ELAPSED sig=$SIG out=$OUT"
check "(T1.3) slow screen reads do not stretch the 8 s window: mail after about 8 s, not 8 tries of 1 s plus the read" 'printf %s "$SIG" | grep -q "\"via\": *\"mail\"" && [ "$ELAPSED" -ge 8000 ] && [ -n "$SPAN" ] && [ "$SPAN" -lt 15000 ]'
unset FAKE_HERDR_READ_FAIL_FIRST FAKE_HERDR_READ_DELAY_MS

# blocked after the Esc (a harness prompt is up): mail at once, no settle loop, nothing typed.
herdr_team tm9 q4
node -e 'const fs=require("fs"),f=process.argv[1]+"/herdr.json",s=JSON.parse(fs.readFileSync(f,"utf8"));for(const a of Object.values(s.agents))a.status="blocked";fs.writeFileSync(f,JSON.stringify(s))' "$FAKE_STATE_DIR"
T0=$(node -e 'process.stdout.write(String(Date.now()))')
ah_run roster stop --now --team tm9 --confirm --grace 0 --cwd "$PROJ"
ELAPSED=$(( $(node -e 'process.stdout.write(String(Date.now()))') - T0 ))
SIG=$(member_signalled "$OUT" q4)
# evidence for a failure: the time from the Esc to the last herdr call, apart from the CLI's own start
SPAN=$(herdr_span)
CALLS=$(ah_test_fake_herdr_calls "$FAKE_STATE_DIR")
OUT="rc=$RC span_ms=$SPAN elapsed_ms=$ELAPSED prompts=$(herdr_prompts) sig=$SIG out=$OUT err=$(cat "$T/err") calls=$CALLS"
# no settle loop, by the calls: the Esc is the only key sent, and one paste attempt reads the screen once after it (each settle try reads again)
check "(T1.4) a member showing a harness prompt after the Esc is mailed at once: no settle loop, nothing typed" 'printf %s "$SIG" | grep -q "\"via\": *\"mail\"" && printf %s "$SIG" | grep -q "pane_error\": *\"blocked" && [ "$(printf %s "$CALLS" | grep -c "^\[\"agent\",\"send-keys\"")" = 1 ] && printf %s "$CALLS" | grep -q "^\[\"agent\",\"send-keys\",\"q4\",\"esc\"\]" && [ "$(printf %s "$CALLS" | sed -n "/^\[\"agent\",\"send-keys\"/,\$p" | grep -c "^\[\"agent\",\"read\"")" = 1 ] && [ "$(herdr_prompts)" = 0 ] && stored_emergency_stop "team tm9"'

# unconfirmed (typed, no activity): mail, and no second paste.
herdr_team tm10 q5
export FAKE_HERDR_PROMPT_NOOP=1 AH_PASTE_VERIFY_MS=1500
ah_run roster stop --now --team tm10 --confirm --grace 0 --cwd "$PROJ"
SIG=$(member_signalled "$OUT" q5)
check "(T1.5) a paste that was typed but never started a turn goes to mail with no second paste" 'printf %s "$SIG" | grep -q "\"via\": *\"mail\"" && printf %s "$SIG" | grep -q "pane_error\": *\"no-activity" && [ "$(herdr_prompts)" = 1 ] && stored_emergency_stop "team tm10"'
unset FAKE_HERDR_PROMPT_NOOP AH_PASTE_VERIFY_MS

# msg new --to <member name> keeps --to role-only but points at --to-name
ah_run msg new --type request --to m1 --from orchestrator --slug probe --title "Probe the hint" --team tm --cwd "$PROJ"
OUT="rc=$RC out=$OUT err=$(cat "$T/err")"
check "(T3.1) msg new --to <member> says to use --to <role> --to-name <member>" 'printf %s "$OUT$(cat "$T/err")" | grep -q -- "--to <its role> --to-name m1"'
ah_run msg new --type request --to nobody-here --from orchestrator --slug probe --title "Probe the hint" --team tm --cwd "$PROJ"
check "(T3.1) msg new --to an unknown name is refused as a bad role and gets no member hint" '[ "$RC" != 0 ] && printf %s "$OUT$(cat "$T/err")" | grep -q -- "--to must be one of" && ! printf %s "$OUT$(cat "$T/err")" | grep -q -- "--to-name"'

# 10. T2.3: a registered job that survives TERM and KILL (a zombie) is the one survivor: exit 4, in job_survivors, never in session_processes
ah_test_team "$HIER" tm7 implementor:z1
team_js "$HIER/teams/tm7.json" 't.orchestrator = o.orchestrator; t.members.forEach((m) => { m.session_id = "s-" + m.name; m.paste = false; m.transport_id = "%8"; })'
ZFILE="$T/zombie.pid"
perl -e 'my $p = fork; if (!$p) { exit 0 } open(my $f, ">", $ARGV[0]); print $f "$p\n"; close($f); sleep 120' "$ZFILE" & ZPAR=$!
for _ in 1 2 3 4 5 6 7 8 9 10; do [ -s "$ZFILE" ] && break; sleep 0.2; done
ZOMBIE=$(cat "$ZFILE")
ah_seed_job "$HIER" tm7 job-zombie "{\"id\":\"job-zombie\",\"team\":\"tm7\",\"owner\":\"z1\",\"session_id\":\"s-z1\",\"kind\":\"bg\",\"eta\":\"medium\",\"label\":\"job-zombie\",\"background\":true,\"pid\":$ZOMBIE,\"pid_start\":\"$(startof "$ZOMBIE")\"}"
ah_run roster stop --now --team tm7 --confirm --grace 0 --cwd "$PROJ"
check "(T2.3) a job that survives TERM and KILL exits 4, is in job_survivors and is not in session_processes" '[ "$RC" = 4 ] && node -e "const r=JSON.parse(process.argv[1]);process.exit(r.job_survivors.some(s=>s.pid===Number(process.argv[2]))&&!r.session_processes.some(s=>s.pid===Number(process.argv[2]))?0:1)" "$OUT" "$ZOMBIE"'

echo "----"
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
