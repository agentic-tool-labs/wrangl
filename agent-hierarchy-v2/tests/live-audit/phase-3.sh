#!/usr/bin/env bash
# Phase 3: liveness, alarms, stuck, wake (spec §5). Default timers unless a row says otherwise. Most rows are waits, so they overlap in one sandbox.
# Sandbox difference for every row that kills a member: the herdr shim answers probes with exit 99, which no real machine does (§3.4.1).
AUDIT_PHASE=3
. "$(dirname "$0")/lib-audit.sh"
audit_init 75
start_daemon || { echo "daemon failed"; exit 9; }
canaries_or_stop start
HIER="$REPO/.claude/ah2"; GATES="$HIER/gates.jsonl"; PEERS="$HIER/peers.jsonl"
start_orch || { echo "orch failed"; exit 9; }
BOX() { "$AHD" inspect entries --address "orchestrator@$TEAM" --all 2>/dev/null; }
# box_events <event> [name in the text]: lines, one per entry of that event in orch's box.
# inspect entries carries no event text, so the text comes from orch's inbox and transcript: the words before "[ah-entry <id>]".
# An event whose text is found nowhere prints <no-text> and matches no name: L02's storm check counts it, so it cannot pass silently.
box_events() {
  # The watcher's own journal names the subject of each event (StallWatch.cs:287); prefer it when it holds that kind.
  local j="$AH_HOME/stall/journal.jsonl"
  if grep -qF "\"kind\":\"$1\"" "$j" 2>/dev/null; then
    grep -F "\"kind\":\"$1\"" "$j" | if [ -n "${2:-}" ]; then grep -F "$2"; else cat; fi
    return 0
  fi
  "$AH" msg inbox --address "orchestrator@$TEAM" --all --session "$ORCH_SID" --cwd "$REPO" >"$T/cap/inbox.txt" 2>&1
  BOX | python3 -I -c '
import json, sys
ev, name = sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else ""
try:
    es = json.load(sys.stdin).get("entries", [])
except Exception:
    es = []
hay = ""
for p in sys.argv[1:3]:
    try:
        hay += open(p, errors="replace").read() + "\n"
    except OSError:
        pass
for e in es:
    if e.get("event") != ev:
        continue
    mark = "[ah-entry " + e.get("id", "?")
    i = hay.find(mark)
    text = "<no-text>"
    if i >= 0:
        start = hay.rfind("[ah-entry ", 0, i)
        text = hay[start + 1:i][-500:]
    if not name or name in text:
        print(json.dumps({"id": e.get("id"), "event": ev, "text": text}))' "$T/cap/inbox.txt" "$ORCH_TX" "$@"
}
box_has() { [ -n "$(box_events "$@")" ]; }
SH() { sed -E 's/^/  /' | scrub; }
fresh() { spawn_member implementor --allow-main || return 1; FP=$SPAWNED_PANE; FN=$SPAWNED_NAME; FX=$SPAWNED_TX; }
slash() { wait_ready claude "$1" 120; tm send-keys -t "$1" -l "$2"; sleep 0.5; tm send-keys -t "$1" Enter; }

# Stand up the cast. Every wait below is measured from here.
fresh; VP=$FP; VN=$FN; VX=$FX      # L01 victim
fresh; IP=$FP; IN=$FN              # L02 idle
fresh; SP=$FP; SN=$FN              # L02 in a long sleep
fresh; DP=$FP; DN=$FN              # L02 dismissed, then killed
fresh; UP=$FP; UN=$FN              # L02 untracked, then killed
fresh; CP=$FP; CN=$FN; CX=$FX      # L04 silent assignee; L05 idle assignee
fresh; WP=$FP; WN=$FN; WX=$FX      # L04 word midway
fresh; KP=$FP; KN=$FN; KX=$FX      # L08 compacts
fresh; JP=$FP; JN=$FN; JX=$FX      # L09, L10 jobs
T0=$SECONDS
say "$SP" "Run exactly this one Bash command, then reply DONE: sleep 600"
# L05's condition is a queued checklist item no dispatch covers, with a role member idle (QueueRule.cs:9-12).
do_in "$ORCH" l05-q <<'EOF'
$AH roster checklist add "audit queued work" --team $TEAM --cwd "$REPO"; echo "queued rc=$?"
EOF

# ---- L01: kill -9, expect one member-down.
begin L01; K1=$SECONDS
kill_pane "$VP"
poll 240 "box_has member-down '$VN'"; D=$?; L01S=$((SECONDS - K1))
"$AHD" inspect entries --address "orchestrator@$TEAM" --all 2>&1 | scrub | ev L01 box-raw.txt
sleep 60
N=$(box_events member-down "$VN" | grep -c .)
box_events member-down "$VN" | scrub | ev L01 events.txt
brief_to l01b "$CN" implementor l01-diag "Do nothing. Do not reply."; RD=$(req_of l01b); keep l01b
do_in "$ORCH" l01d <<EOF
\$AH roster stall-diagnose $RD --cwd "\$REPO"; echo "diag rc=\$?"
EOF
keep l01d
chk "member-down within 4 min ($L01S s)" "[ $D = 0 ]"
chk "reported once ($N)" "[ '$N' = 1 ]"
chk "stall-diagnose answered" "out_of l01d | grep -q 'diag rc=0'"
want L01 && finish "member-down, stall-diagnose" "kill -9 a member's claude" "Sandbox difference: herdr shim exit 99. Diagnose run on a live dispatch, since the row's kill has no open request."

# ---- L02: dismissed and untracked members killed; a daemon restart with a live team. Idle and sleeping members are watched throughout.
do_in "$ORCH" l02-d <<EOF
\$AH roster dismiss $DN --plan --cwd "\$REPO"
\$AH roster dismiss $DN --cwd "\$REPO" --close; echo "dismiss rc=\$?"
\$AH roster untrack $UN --commit --keep-sessions --cwd "\$REPO"; echo "untrack rc=\$?"
EOF
out_of l02-d | scrub | ev L02 dismiss-untrack.txt
kill_pane "$DP" 2>/dev/null; kill_pane "$UP"
restart_daemon; L2R=$?
cat "$T/cap/restarts.txt" 2>/dev/null | scrub | ev L02 restart.txt

# ---- L04: the dispatch clock. CN got a brief above and does nothing. WN gets one and a word midway.
begin L04
brief_to l04w "$WN" implementor l04-word "Do nothing until told. Do not reply."; RW=$(req_of l04w)
sleep 300
note_to "$WP" l04-word orchestrator "still working on it" || true
L04M=$SECONDS

# ---- L08: compact mid-brief and idle.
brief_to l08 "$KN" implementor l08-comp "Do nothing until told. Do not reply."; RK=$(req_of l08)
# The verdict needs a working turn before the compact (HierLib.Status.cs MemberVerdictOf).
direct_in "$KP" "$KX" "ls $REPO" 90; L08W=$?
C0=$(grep -c '"source":"compact"' "$T/hook-payloads.jsonl")
slash "$KP" "/compact"; poll 180 "[ \"\$(grep -c '\"source\":\"compact\"' '$T/hook-payloads.jsonl')\" -gt $C0 ]"; L08C=$?; L08T=$SECONDS

# ---- L09, L10: jobs.
# Not a bare sleep: job tracking excludes a command bounded by its own argument (JobExclusion.cs:6).
say "$JP" "Run exactly this Bash command with run_in_background true, then end your turn: bash -c 'sleep 90; echo done'"
L10J=$SECONDS
poll 150 "grep -q 'stop-member-liveness\|ah roster wake' '$JX'"; JN1=$?
tool_done "$JX" Bash 'sleep 90'; L10D=$?
poll 180 "tool_done '$JX' Bash 'sleep 90'" && L10D=0
# The tool result returns when the job starts; the job ends 90 s later.
while [ $((SECONDS - L10J)) -lt 170 ]; do sleep 5; done
L10E=$SECONDS
begin L10
JS=$(basename "$JX" .jsonl)
grep -E 'wake-nudge|wake-live|member-job-nudge' "$GATES" | grep -F "$JS" | scrub | cut -c1-300 | ev L10 gates.txt
txlines "$JX" "roster wake" tx.txt
grep -E '"role":"assistant"' "$JX" | tail -3 | scrub | cut -c1-1200 | ev L10 last-turns.txt
grep -F 'ahd wake:' "$JX" | scrub | tail -5 | cut -c1-300 | ev L10 wake-out.txt
chk "wake-nudge at stop, this member" "[ -s '$T/evidence/L10/gates.txt' ]"
# A Bash call of the member's own: the nudge's text also holds the words.
chk "a wake was attempted" "grep -qE '\"command\":\"[^\"]*roster wake' '$JX'"
chk "ahd wake: line in the waiter's output" "[ -s '$T/evidence/L10/wake-out.txt' ]"
if [ "$L10D" != 0 ]; then want L10 && driver "wake on job end" "member starts sleep 90 in the background, stops, runs ah roster wake as nudged" "Haiku did not start the job"; else
want L10 && finish "wake on job end" "member starts sleep 90 in the background, stops, runs ah roster wake as nudged" "Turn after job end is in last-turns.txt, read at $((L10E - L10J)) s after start."; fi

begin L09
do_in "$JP" l09 <<'EOF'
$AH roster job start --kind bg --label audit-l09 --eta small --cap-secs 60 --cwd "$REPO"; echo "start rc=$?"
EOF
keep l09
JOBID=$(out_of l09 | grep -oE '[0-9]{8}-[0-9]{6}-[a-z0-9]{4}' | head -1)
find "$HIER" "$AH_HOME" -path '*jobs*' -type f 2>/dev/null | scrub | head -10 | ev L09 job-files.txt
poll 720 "box_has job-overdue audit-l09 || grep -q 'job-overdue' '$JX'"; D=$?
sleep 60
# One delivery can appear on an api-request line as well as its user line: count delivery lines only.
grep -F 'job-overdue' "$JX" | grep -E '"type":"(user|attachment)"' | grep -c audit-l09 | ev L09 tx-count.txt
N=$(cat "$T/evidence/L09/tx-count.txt")
chk "record in jobs/" "[ -s '$T/evidence/L09/job-files.txt' ]"
chk "job-overdue seen" "[ $D = 0 ]"
chk "once ($N)" "[ '$N' -le 1 ]"
want L09 && finish "job tracking, job-overdue" "job with a short eta and no process" "ETA small; due + 2 min per the docs."

# By here at least 15 min have passed; wait out the rest of L02, L04 and L05's windows.
while [ $((SECONDS - T0)) -lt 960 ]; do sleep 20; done

begin L04
box_events check-in-due | scrub | ev L04 check-in-due.txt
box_events peer-silent | scrub | ev L04 peer-silent.txt
box_events report-landed | scrub | ev L04 report-landed.txt
grep -F liveness-nudge "$GATES" | scrub | cut -c1-300 | tail -5 | ev L04 gates.txt
# The inbox drops data.request_id (DispatchClock.cs:163): the stored entries keep it.
{ sqlite3 -readonly "$AH_HOME/state/ah.db" 'select * from mail_entries' 2>/dev/null; grep -rh 'check-in-due' "$AH_HOME/mailbox" 2>/dev/null; } | grep -a 'check-in-due' | scrub | cut -c1-500 | ev L04 stored-entries.txt
chk "check-in-due fires for the silent assignee" "grep -qE '$CN|$RD' '$T/evidence/L04/check-in-due.txt' || grep -qaE '$CN|$RD' '$T/evidence/L04/stored-entries.txt'"
chk "a peer-silent later" "[ -s '$T/evidence/L04/peer-silent.txt' ]"
want L04 && finish "dispatch clock" "small brief, member told to do nothing; second member sends a word midway" "Thresholds are in the events. report-landed leg: see L13's report if any."

begin L05
box_events queue-stall | scrub | ev L05 queue-stall.txt
box_events queue-stall-unstaffed | scrub | ev L05 unstaffed.txt
out_of l05-q | scrub | ev L05 staging.txt
chk "queue-stall with work queued and a role member idle" "[ -s '$T/evidence/L05/queue-stall.txt' ]"
want L05 && finish "queue-stall" "open task, assignee live and idle, 10 min" "Staged as a checklist item with no dispatch, the rule's condition. Steady-tool-calls negative and unstaffed leg not staged."

begin L08
# The verdict waits on max(5 min, 2 polls) since the compact (HierLib.Status.cs:244): measured from the compact, not from this line.
while [ $((SECONDS - L08T)) -lt 420 ] && ! box_has stalled-after-compact; do sleep 10; done
box_events stalled-after-compact | scrub | ev L08 events.txt
chk "stalled-after-compact event" "[ -s '$T/evidence/L08/events.txt' ]"
if [ "$L08W$L08C" != 00 ]; then want L08 && driver "stalled-after-compact" "member compacts mid-brief and idles" "working turn rc=$L08W compact rc=$L08C"; else
head -c 8000 "$HIER/status.json" 2>/dev/null | scrub | ev L08 status.txt
want L08 && finish "stalled-after-compact" "member compacts mid-brief and idles"; fi

begin L02
for pair in "idle:$IN" "sleeping:$SN" "dismissed:$DN" "untracked:$UN"; do
  lab=${pair%%:*}; nm=${pair#*:}
  box_events member-down "$nm" | scrub | ev L02 "down-$lab.txt"
  chk "no member-down for the $lab member" "[ ! -s '$T/evidence/L02/down-$lab.txt' ]"
done
chk "daemon restart went through" "[ $L2R = 0 ]"
chk "no alarm storm from the restart: only the L01 victim is down" "[ \"\$(box_events member-down | grep -vc '$VN')\" = 0 ]"
want L02 && finish "member-down, must stay silent" "idle 15 min; sleep 600; dismissed killed; untracked killed; daemon restart during a live team" "Any alarm is a false alarm: FAIL."

# ---- L03: respawn the victim.
begin L03
script l03 <<EOF
\$AH roster spawn-one implementor --member $VN --team \$TEAM --model inherit --allow-main --replace-live --cwd "\$REPO"; echo "spawn rc=\$?"
EOF
run_in "$ORCH" l03 180; keep l03
poll 120 "box_has member-recovered '$VN'"; D=$?
box_events member-recovered "$VN" | scrub | ev L03 events.txt
if out_of l03 | grep -q 'spawn rc=0'; then
  chk "member-recovered" "[ $D = 0 ]"
  want L03 && finish "member-recovered" "respawn the L01 member"
else want L03 && record L03 "member-recovered" "respawn the L01 member" FAIL "the respawn was refused: see l03.txt"; fi

# ---- L16: the stall probe through the shim.
begin L16
TR="$T/cap/herdr-trace.txt"
cp "$TR" "$(ev_dir L16)/trace-at-read.txt" 2>/dev/null
A0=$(grep -c "agent get $VN" "$TR" 2>/dev/null || echo 0)
D0=$(grep -c "agent get $DN" "$TR" 2>/dev/null || echo 0)
U0=$(grep -c "agent get $UN" "$TR" 2>/dev/null || echo 0)
sleep 120
D1=$(grep -c "agent get $DN" "$TR" 2>/dev/null || echo 0)
U1=$(grep -c "agent get $UN" "$TR" 2>/dev/null || echo 0)
grep '^argv:' "$TR" 2>/dev/null | awk '{print $2,$3}' | sort | uniq -c | ev L16 verbs.txt
printf 'victim calls %s; dismissed member %s then %s; untracked %s then %s\n' "$A0" "$D0" "$D1" "$U0" "$U1" | ev L16 counts.txt
chk "no probe grows for a dismissed member" "[ '$D1' = '$D0' ]"
chk "no probe grows for an untracked member" "[ '$U1' = '$U0' ]"
chk "only reads reach the shim" "! grep -qvE ' agent (get|read|list)( |\$)| pane (read|list|process-info)( |\$)| session list( |\$)' '$T/evidence/L16/verbs.txt'"
want L16 && finish "stall probe of herdr for a dead member" "read the trace for L01's kill, then dismissed and untracked members" "Decision for the user: a tmux member is probed by name through herdr at all (see verbs.txt). Reap leg is R05."

# ---- L07: a member on no allow list hits a permission prompt.
begin L07
python3 -I -c '
import json, sys
p = sys.argv[1]
d = json.load(open(p)); d["permissions"]["allow"] = []
json.dump(d, open(p, "w"))' "$HOME/.claude/settings.json"
fresh; BP=$FP; BN=$FN
say "$BP" "Run exactly this one Bash command, then reply DONE: touch $REPO/l07-probe"
poll 90 "cap '$BP' | grep -qiE 'do you want|allow this'"; L07P=$?
poll 300 "box_has member-blocked '$BN'"; D=$?
cap "$BP" | scrub | ev L07 screen.txt
box_events member-blocked "$BN" | scrub | ev L07 events.txt
box_events member-down "$BN" | scrub | ev L07 false-down.txt
python3 -I -c '
import json, sys
p = sys.argv[1]
d = json.load(open(p)); d["permissions"]["allow"] = ["Bash", "Write", "Edit", "Read"]
json.dump(d, open(p, "w"))' "$HOME/.claude/settings.json"
chk "member-blocked" "[ $D = 0 ]"
chk "no false member-down" "[ ! -s '$T/evidence/L07/false-down.txt' ]"
if [ "$L07P" != 0 ]; then want L07 && record L07 "member-blocked" "member with no allow list hits a permission prompt" NOT-TESTED "sandbox-cannot-host: empty allow list raised no permission prompt (screen.txt), so nothing could block."; else
want L07 && finish "member-blocked" "member with no allow list hits a permission prompt"; fi
tm send-keys -t "$BP" Escape; kill_pane "$BP"

# ---- L06: is there a producer of MEMBER-STUCK at all? (a) every row this phase wrote; (b) an injected row, shortened window.
begin L06
grep -c 'MEMBER-STUCK' "$PEERS" | ev L06 peers-count.txt
grep -rl 'MEMBER-STUCK' "$T/plugin/native/src" 2>/dev/null | scrub | ev L06 source-hits.txt
{ grep -rl 'MEMBER-STUCK' "$AUDIT_TREE/docs/audits/ah-base-audit/evidence" 2>/dev/null | grep -v L06; } | sed "s|$AUDIT_TREE/||" | ev L06 other-phase-hits.txt
chk "(a) a MEMBER-STUCK row was raised somewhere in the audit" "[ \"\$(cat '$T/evidence/L06/peers-count.txt')\" -gt 0 ] || [ -s '$T/evidence/L06/other-phase-hits.txt' ]"
want L06 && finish "MEMBER-STUCK" "(a) grep every phase's peers for the kind" "(b) injection leg not run: the row format is internal and a hand-written row would prove the fake. Findings names K4: whoever raised today's stuck alarms is not identified here; the Orchestrator's entry ids were not supplied."

# ---- L11: the overdue failsafe, daemon stopped so a step is missed. Shortened with AHD_STALL_MARGIN.
begin L11
brief_to l11 "$IN" implementor l11-over "Do nothing. Do not reply."; keep l11
# The margin in the waiter's own env, in seconds: it reads its own (OverdueTimer.cs:71), and the file lives only while it does (OverdueVerb.cs:33).
say "$ORCH" "Run exactly this Bash command with run_in_background true, then end your turn: AHD_STALL_MARGIN=5 $AH roster overdue --cwd $REPO"
poll 60 "grep -q 'roster overdue' '$ORCH_TX'"
poll 20 "ls '$AH_HOME/wake' 2>/dev/null | grep -q overdue"; F0=$?
ls -l "$AH_HOME/wake" 2>/dev/null | scrub | ev L11 wake-after-launch.txt
dp=$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["pid"])' "$AH_HOME/run/ahd.json"); st=$(ah_pid_stamp "$dp")
# What fires is a new ahd-missed row: the waiter's file is there from launch, and the first miss may be any open dispatch's, not only l11's.
MISSED() { cat "$AH_HOME/stall/journal.jsonl" 2>/dev/null | grep -c '"kind":"ahd-missed"'; }
M0=$(MISSED)
ah_signal_stamped KILL "$dp" "$st"
poll 420 "[ \"\$(MISSED)\" -gt $M0 ]"; D=$?
grep -F '"kind":"ahd-missed"' "$AH_HOME/stall/journal.jsonl" 2>/dev/null | scrub | ev L11 missed.txt
find "$AH_HOME/wake" "$HIER" "$HOME/.claude/ah2" -name '*overdue*' 2>/dev/null | scrub | ev L11 files.txt
tx_tool_results "$ORCH_TX" Bash "roster overdue" | scrub | cut -c1-1500 | ev L11 tx.txt
start_daemon
chk "overdue file at launch" "[ $F0 = 0 ]"
chk "the failsafe fired after the kill" "[ $D = 0 ]"
chk "waiter ran" "[ -s '$T/evidence/L11/tx.txt' ]"
want L11 && finish "overdue failsafe" "orch runs ah roster overdue; daemon killed so a step is missed; AHD_STALL_MARGIN shortened" "Shortened timers."

# ---- L13, L14, L15: quick reads.
begin L13
do_in "$ORCH" l13 <<'EOF'
$AH roster status --plain --cwd "$REPO"; echo "plain rc=$?"
$AH roster status --cwd "$REPO" >"$T/cap/status-cli.json"; echo "doc rc=$?"
curl -s --max-time 10 --unix-socket "$AH_HOME/run/ahd.sock" http://x/v1/status >"$T/cap/status-http.json"; echo "http rc=$?"
EOF
keep l13
head -c 3000 "$T/cap/status-http.json" 2>/dev/null | scrub | ev L13 http.txt
head -c 3000 "$HIER/status.json" 2>/dev/null | scrub | ev L13 status-json.txt
chk "plain line" "out_of l13 | grep -q '^ah ·\|plain rc=0'"
chk "status.json present" "[ -s '$HIER/status.json' ]"
chk "GET /v1/status answered" "[ -s '$T/cap/status-http.json' ]"
want L13 && finish "status line, status.json" "team with a dead member" "Blocked-member count not staged here; equality of the two documents is by eye from http.txt and status-json.txt."

begin L14
do_in "$JP" l14 <<'EOF'
sleep 9999 &
P=$!; echo "pid=$P"
$AH proc list --cwd "$REPO"; echo "list rc=$?"
$AH proc stop $P --dry-run --cwd "$REPO"; echo "dry rc=$?"
kill -0 $P && echo "alive after dry run"
$AH proc stop $P --cwd "$REPO"; echo "stop rc=$?"
sleep 2; kill -0 $P 2>/dev/null && echo "alive after stop" || echo "gone after stop"
$AH proc stop 1 --cwd "$REPO"; echo "one rc=$?"
EOF
keep l14
tail -4 "$AH_HOME/state/proc-audit.jsonl" 2>/dev/null | scrub | cut -c1-300 | ev L14 audit.txt
chk "runaway line in the directive" "grep -q -i 'ah proc stop' '$ORCH_TX'"
chk "dry run leaves it" "out_of l14 | grep -q 'alive after dry run'"
chk "stop ended it" "out_of l14 | grep -q 'gone after stop'"
chk "pid 1 refused" "out_of l14 | grep -qE 'refused' && out_of l14 | grep -qE 'one rc=[1-9]'"
chk "audit row" "[ -s '$T/evidence/L14/audit.txt' ]"
want L14 && finish "runaway line, ah proc" "member starts sleep 9999; list, stop --dry-run, stop; stop 1" "Run from the member's session, not orch: the rule on who may stop what is in l14.txt."

begin L15
script l15 <<'EOF'
$AH usage --cwd "$REPO"; echo "plain rc=$?"
EOF
run_in "$ORCH" l15 90; keep l15
grep -cE '"event":"context' "$T/evidence/L01/box-raw.txt" | ev L15 context-events.txt
chk "no usage sample: exit 0 per the docs" "out_of l15 | grep -q 'plain rc=0'"
chk "no context event" "[ \"\$(cat '$T/evidence/L15/context-events.txt')\" = 0 ]"
want L15 && finish "usage read, context alerts" "ah usage with no sample exits 0" "Partial row: no usage sample exists under a temp HOME and no fixture was staged."

# ---- L12: 3 members on the 529 stub in 60 s. Last: it leaves members in failure.
begin L12
cat >"$T/bin/stub529.py" <<'PY'
import http.server, sys
class H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("content-length", 0)); self.rfile.read(n)
        self.send_response(529); self.send_header("content-type", "application/json"); self.end_headers()
        self.wfile.write(b'{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}')
    def log_message(self, *a): pass
s = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
open(sys.argv[1], "w").write(str(s.server_port))
s.serve_forever()
PY
python3 -I "$T/bin/stub529.py" "$T/cap/stub-port" & probe_record_pid $!
poll 10 "[ -s '$T/cap/stub-port' ]"
tm set-environment -g ANTHROPIC_BASE_URL "http://127.0.0.1:$(cat "$T/cap/stub-port")"
OK=0; PANES=
for i in 1 2 3; do fresh && { OK=$((OK + 1)); PANES="$PANES $FP"; }; done
tm set-environment -gu ANTHROPIC_BASE_URL
if [ $OK = 3 ]; then
  for p in $PANES; do say "$p" "Reply with exactly one word: ok" 30; done
  poll 240 "box_has outage"; D=$?
  box_events outage | scrub | ev L12 outage.txt
  box_events member-down | scrub | ev L12 down.txt
  chk "outage event" "[ $D = 0 ]"
  chk "no member-down storm for the stub members" "[ \"\$(wc -l <'$T/evidence/L12/down.txt')\" -le 2 ]"
  want L12 && finish "outage watch" "3 members on the 529 stub in 60 s" "Sandbox difference: herdr shim exit 99."
else want L12 && record L12 "outage watch" "3 members on the 529 stub in 60 s" NOT-TESTED "driver: only $OK of 3 members reached a composer on the stub"; fi

cp "$PEERS" "$(ev_dir phase-3)/peers-excerpt.txt" 2>/dev/null
canaries_or_stop end-3
echo "PHASE 3 DONE: $(cut -f4 "$T/rows.tsv" | sort | uniq -c | tr '\n' ' ')"
