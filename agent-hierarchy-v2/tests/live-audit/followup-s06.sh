#!/usr/bin/env bash
# Follow-up, S06: a dead sub-orchestrator. Does an ask reach the user? FOLLOW_HERDR=1 replaces the shim's exit 99 with a plain "no such agent" exit 1, as the control. Run with AUDIT_FILTER=S06 (exit 99) or AUDIT_FILTER=none (control).
AUDIT_PHASE=5
. "$(dirname "$0")/lib-audit.sh"
. "$(dirname "$0")/lib-follow.sh"
audit_init 40
D=S06-follow
if [ -n "${FOLLOW_HERDR:-}" ]; then
  D=S06-follow-exit$FOLLOW_HERDR
  { printf '#!/bin/sh\necho "no such agent"\n{ date -u +%%H:%%M:%%S; echo "argv: $*"; } >>"%s/cap/herdr-trace.txt"\nexit %s\n' "$T" "$FOLLOW_HERDR"; } >"$T/bin/herdr"
fi
start_daemon || { echo "daemon failed"; exit 9; }
canaries_or_stop start
HIER="$REPO/.claude/ah2"
start_orch || { echo "orch failed"; exit 9; }
B=$(tm list-panes -a -F '#{pane_id}' | sort)
do_in "$ORCH" s01 <<'EOF' 240
$AH roster stream-open a --team $TEAM --cwd "$REPO"; echo "open rc=$?"
$AH roster spawn-one sub-orchestrator --stream a --team $TEAM --cwd "$REPO"; echo "spawn rc=$?"
EOF
out_of s01 | scrub | cut -c1-300 | tail -6 | ev "$D" spawn.txt
sleep 15
SO=$(tm list-panes -a -F '#{pane_id}' | sort | comm -13 <(printf '%s\n' "$B") - | head -1)
begin S06
if [ -z "$SO" ] || ! reach_ready claude "$SO" 120; then
  driver "D7, dead SO asks the user" "kill -9 SO; orch running" "no SO reached a composer"
  exit 0
fi
touch "$T/cap/herdr-trace.txt"; wc -l <"$T/cap/herdr-trace.txt" | ev "$D" herdr-before.txt
wait_ready claude "$ORCH" 120
AQ0=$(grep -c AskUserQuestion "$T/cap/pretool.jsonl" 2>/dev/null); LN=$(($(wc -l <"$ORCH_TX")))
K=$SECONDS; KT=$(now)
kill_pane "$SO"
poll 420 "BOX | grep -qi 'lane-escalated'"; E=$?; ET=$((SECONDS - K))
# Nothing from here speaks to orch for 5 min: this is what orch does by itself.
sample_door "$D" 300 "$ORCH_SID"
{ echo "killed $KT; lane-escalated in the box after ${ET}s (rc=$E)"; BOX | grep -oE '"event": *"[a-z-]+"' | sort | uniq -c; } | scrub | ev "$D" timing.txt
BOX | scrub | head -c 3000 | ev "$D" box-raw.txt
tail -n +"$LN" "$ORCH_TX" >"$T/orch-after"
tx_rows "$T/orch-after" | scrub | cut -c1-520 | ev "$D" orch-all-rows.txt
tx_marks 'escalated \(E-|lane-escalated|member-down|You have [0-9]+ message|ah mail for orchestrator@' "$T/orch-after" | scrub | cut -c1-500 | ev "$D" orch-received.txt
tx_marks '"name":"AskUserQuestion"' "$T/orch-after" | scrub | cut -c1-500 | ev "$D" asks-tx.txt
grep -c AskUserQuestion "$T/cap/pretool.jsonl" 2>/dev/null | ev "$D" asks-hook-count.txt
tx_after "$T/orch-after" 8 | scrub | cut -c1-700 | ev "$D" orch-did.txt
cap "$ORCH" | scrub | ev "$D" orch-screen.txt
cat "$T/cap/herdr-trace.txt" 2>/dev/null | scrub | cut -c1-200 | tail -12 | ev "$D" herdr-trace.txt
wc -l <"$T/cap/herdr-trace.txt" 2>/dev/null | ev "$D" herdr-after.txt
head -c 3000 "$HIER/status.json" | scrub | ev "$D" status-dead.txt
# Now, from orch's own session, what the event says.
do_in "$ORCH" inb <<'EOF'
$AH msg inbox --all --cwd "$REPO"; echo "inbox rc=$?"
EOF
out_of inb | grep -iE 'lane|escalat|inbox rc' | scrub | cut -c1-600 | ev "$D" inbox-from-orch.txt
AQ1=$(cat "$T/evidence/$D/asks-hook-count.txt")
chk "lane-escalated raised" "[ $E = 0 ]"
chk "E-a reached orch's session" "grep -q 'E-a' '$T/evidence/$D/orch-received.txt' || grep -q 'E-a' '$T/evidence/$D/inbox-from-orch.txt'"
chk "the top offers respawn, finish or wind down" "[ -s '$T/evidence/$D/asks-tx.txt' ] || [ '${AQ1:-0}' -gt '${AQ0:-0}' ]"
finish "D7, dead SO asks the user" "kill -9 SO; orch running" "Measured: $D/orch-received.txt is what reached orch's transcript, orch-did.txt what orch then did by itself over 5 min, herdr-trace.txt each shim call. Control with exit 1: S06-follow-exit1/."
canaries_or_stop end-follow
echo "FOLLOW S06 DONE"
