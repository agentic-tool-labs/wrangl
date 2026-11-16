#!/usr/bin/env bash
# Follow-up, M15: does a queued report ring? Orch idle, then in one long Bash call, then between two calls. Also D01's plan row. Run with AUDIT_FILTER=M15.
AUDIT_PHASE=1
. "$(dirname "$0")/lib-audit.sh"
. "$(dirname "$0")/lib-follow.sh"
audit_init 60
start_daemon || { echo "daemon failed"; exit 9; }
canaries_or_stop start
HIER="$REPO/.claude/ah2"
start_orch || { echo "orch failed"; exit 9; }
spawn_member implementor --allow-main || { echo "impl failed"; exit 9; }
IMPL=$SPAWNED_PANE; IMPL_NAME=$SPAWNED_NAME
printf 'Report for the doorbell.\n' >"$T/r15"

begin M15
{ claude --version 2>&1 | head -1; ls "$AH_HOME/registrations" 2>&1 | head -5; } | scrub | ev M15-follow setup.txt
# leg <name> <n reports> <what orch is doing>: the reports go out from the member; nothing else speaks to orch until sampling ends.
send_reports() {
  local name=$1 n=$2 i ids=
  for i in $(seq 1 "$n"); do
    brief_to "$name-b$i" "$IMPL_NAME" implementor "$name-$i" "Reply DONE and do nothing else until told."
    ids="$ids $(req_of "$name-b$i")"
  done
  wait_ready claude "$ORCH" 120
  sleep 30
  : >"$T/s-$name"
  for i in $ids; do
    printf '$AH msg fill --id %s --from implementor --body-file %s --cwd "$REPO"\n$AH msg send "orchestrator@$TEAM" --kind report --ref %s --json --cwd "$REPO"\n' "$i" "$T/r15" "$i" >>"$T/s-$name"
  done
  script "$name-send" <"$T/s-$name"
}

# Idle, two reports: the multi-item ring.
send_reports idle2 2
run_in "$IMPL" idle2-send 120; S1=$(now)
sample_door M15-follow/idle2 240 "$ORCH_SID"
{ echo "sent $S1"; tx_marks "$RINGS" "$ORCH_TX" | scrub | cut -c1-420; } | ev M15-follow/idle2 rings.txt
cap "$ORCH" | scrub | ev M15-follow/idle2 screen.txt
out_of idle2-send | scrub | cut -c1-500 | ev M15-follow/idle2 send.txt
do_in "$ORCH" idle2-n <<'EOF'
$AH msg next --cwd "$REPO"; echo "next rc=$?"
$AH msg next --cwd "$REPO"; echo "next2 rc=$?"
EOF
out_of idle2-n | scrub | cut -c1-300 | ev M15-follow/idle2 next.txt
M0=$(tx_marks "$RINGS" "$ORCH_TX" | grep -vc ' tool_result ')

# Idle, one report: the inline ring.
send_reports idle1 1
run_in "$IMPL" idle1-send 120; S2=$(now)
sample_door M15-follow/idle1 180 "$ORCH_SID"
{ echo "sent $S2"; tx_marks "$RINGS" "$ORCH_TX" | scrub | cut -c1-420; } | ev M15-follow/idle1 rings.txt
M1=$(tx_marks "$RINGS" "$ORCH_TX" | grep -vc ' tool_result ')

# Busy in one Bash call: is it deferred, and does it come after?
send_reports busy1 1
say "$ORCH" "Run exactly this one Bash command, then reply DONE: python3 -c 'import time; time.sleep(90)'"
poll 60 "grep -q 'time.sleep(90)' '$ORCH_TX'"; sleep 5
run_in "$IMPL" busy1-send 120; S3=$(now)
sample_door M15-follow/busy1 240 "$ORCH_SID"
{ echo "sent $S3"; tx_marks 'time.sleep\(90\)' "$ORCH_TX" | scrub | cut -c1-260; tx_marks "$RINGS" "$ORCH_TX" | scrub | cut -c1-420; } | ev M15-follow/busy1 rings.txt
M2=$(tx_marks "$RINGS" "$ORCH_TX" | grep -vc ' tool_result ')

# Busy between two Bash calls: the tool carrier.
send_reports busy2 1
say "$ORCH" "Run these two Bash commands one after the other as separate calls, then reply DONE: python3 -c 'import time; time.sleep(45)' and then python3 -c 'import time; time.sleep(46)'"
poll 60 "grep -q 'time.sleep(45)' '$ORCH_TX'"; sleep 5
run_in "$IMPL" busy2-send 120; S4=$(now)
sample_door M15-follow/busy2 200 "$ORCH_SID"
{ echo "sent $S4"; tx_marks 'time.sleep\(4[56]\)' "$ORCH_TX" | scrub | cut -c1-260; tx_marks "$RINGS" "$ORCH_TX" | scrub | cut -c1-420; } | ev M15-follow/busy2 rings.txt
M3=$(tx_marks "$RINGS" "$ORCH_TX" | grep -vc ' tool_result ')
tx_after "$ORCH_TX" 12 | scrub | cut -c1-500 | ev M15-follow orch-last-turns.txt
tx_rows "$ORCH_TX" | scrub | cut -c1-520 | ev M15-follow orch-all-rows.txt
mod_state M15-follow
printf 'idle2 %s\nidle1 %s\nbusy1 %s\nbusy2 %s\n' "$S1" "$S2" "$S3" "$S4" | ev M15-follow sent-times.txt

chk "idle, two reports: a ring ($M0)" "[ '$M0' -ge 1 ]"
chk "idle, one report: another ring ($M1 after $M0)" "[ '$M1' -gt '$M0' ]"
chk "busy, one Bash call: a ring by the end ($M2 after $M1)" "[ '$M2' -gt '$M1' ]"
chk "at most 3 rings per leg" "[ $((M3 - M0)) -le 9 ]"
finish "doorbell, msg next" "reports to orch: idle, in one Bash call, between two; ah msg next" "Measured: M15-follow/<leg>/rings.txt with times and row types, door-samples.txt with the doorbell note over time. AH_MAIL_DOORBELL=0 leg not run."

# D01: the plan row a member's own checklist add writes, and the member marker in its shell.
do_in "$IMPL" d01f <<'EOF'
env | grep -E '^(AH_|CLAUDE_CODE_SESSION|AGENT_HIERARCHY)' | cut -d= -f1 | sort | tr '\n' ' '; echo
$AH roster whoami --cwd "$REPO" | head -20
$AH roster checklist add "member follow-up item" --team $TEAM --cwd "$REPO"; echo "member rc=$?"
$AH roster adopt --orchestrator-pid 1 --team $TEAM --cwd "$REPO"; echo "owner verb rc=$?"
EOF
out_of d01f | scrub | cut -c1-400 | ev D01-follow member.txt
grep -F 'member-follow-up-item' "$HIER/progress/$TEAM/_run.plan.jsonl" | scrub | cut -c1-500 | ev D01-follow plan-row.txt
tx_marks '"command":"[^"]*d01f' "$SPAWNED_TX" | scrub | cut -c1-300 | ev D01-follow tx-proof.txt

canaries_or_stop end-follow
echo "FOLLOW M15 DONE"
