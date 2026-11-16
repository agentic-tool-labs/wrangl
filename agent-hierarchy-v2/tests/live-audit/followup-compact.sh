#!/usr/bin/env bash
# Follow-up, H04 and L08: after /compact, does a role member keep its open brief; does one that stalls raise anything. Two members, so asking the first cannot reset the second. Run with AUDIT_FILTER=L08.
AUDIT_PHASE=3
. "$(dirname "$0")/lib-audit.sh"
. "$(dirname "$0")/lib-follow.sh"
audit_init 45
start_daemon || { echo "daemon failed"; exit 9; }
canaries_or_stop start
HIER="$REPO/.claude/ah2"
start_orch || { echo "orch failed"; exit 9; }
spawn_member implementor --allow-main || { echo "k1 failed"; exit 9; }
K1=$SPAWNED_PANE; K1N=$SPAWNED_NAME; K1X=$SPAWNED_TX; K1S=$SPAWNED_SID
spawn_member implementor --allow-main || { echo "k2 failed"; exit 9; }
K2=$SPAWNED_PANE; K2N=$SPAWNED_NAME; K2X=$SPAWNED_TX; K2S=$SPAWNED_SID
compact() { local c0; c0=$(grep -c '"source":"compact"' "$T/hook-payloads.jsonl"); slash "$1" /compact; poll 180 "[ \"\$(grep -c '\"source\":\"compact\"' '$T/hook-payloads.jsonl')\" -gt $c0 ]"; }

# ---- K1: unfinished brief, compact, then neutral words.
begin H04
brief_to k1 "$K1N" implementor k1-open "Step 1: run ls once. Step 2: stop and wait. When you receive the single word GO, write the word done to $T/out/k1-done, then file your response with ah msg fill and send your report. Do not reply before GO."; R1=$(req_of k1); keep k1
poll 90 "tool_done '$K1X' Bash 'ls'"; K1W=$?
sleep 15
compact "$K1"; K1C=$?; sleep 15
N0=$(wc -l <"$K1X")
# What the session received at the compact.
tx_marks 'You are a peer|isCompactSummary' "$K1X" | tail -3 | scrub | cut -c1-200 | ev H04-follow compact-rows.txt
python3 -I -c '
import json, sys
last = None
for raw in open(sys.argv[1], errors="replace"):
    if "You are a peer" in raw:
        last = raw
print(last or "no role notice in the transcript")
' "$K1X" | sed 's/\\n/\n/g' | scrub | cut -c1-900 | head -60 | ev H04-follow role-notice.txt
grep -c "$R1\|k1-open" "$T/evidence/H04-follow/role-notice.txt" | ev H04-follow notice-names-brief.txt
python3 -I -c '
import sys
for raw in open(sys.argv[1], errors="replace"):
    if "isCompactSummary" in raw:
        print(raw)
' "$K1X" | sed 's/\\n/\n/g' | scrub | cut -c1-600 | head -60 | ev H04-follow compact-summary.txt
grep -c "$R1\|k1-open\|GO" "$T/evidence/H04-follow/compact-summary.txt" | ev H04-follow summary-names-brief.txt
tail -n +1 "$K1X" | grep -c 'HIERARCHY STATE' | ev H04-follow member-state-block.txt
say "$K1" "Continue."; sleep 45
{ echo "--- after: Continue."; tx_after "$K1X" 4; } | scrub | cut -c1-700 | ev H04-follow next-after-continue.txt
say "$K1" "GO"; poll 120 "[ -s '$T/out/k1-done' ]"; K1G=$?; sleep 30
{ echo "--- after: GO"; tx_after "$K1X" 8; } | scrub | cut -c1-700 | ev H04-follow next-after-go.txt
ls -l "$T/out/k1-done" 2>&1 | scrub | ev H04-follow done-file.txt
"$AH" msg show "$R1" response --cwd "$REPO" 2>&1 | scrub | cut -c1-200 | head -3 | ev H04-follow response.txt

# ---- K2: open brief, a working turn, compact, then nothing from anyone here.
begin L08
brief_to k2 "$K2N" implementor k2-comp "Do nothing until told. Do not reply."; R2=$(req_of k2); keep k2
direct_in "$K2" "$K2X" "ls $REPO" 90; K2W=$?
sleep 15
compact "$K2"; K2C=$?; K2T=$SECONDS; CT=$(now)
LN=$(($(wc -l <"$K2X")))
ACT="$HIER/activity/$K2S.json"
{ echo "compact seen $CT; brief $R2; member session $K2S"
  while [ $((SECONDS - K2T)) -lt 540 ]; do
    printf '%s t+%ss %s | down-box=%s\n' "$(now)" "$((SECONDS - K2T))" "$(python3 -I -c '
import json, sys
try:
    d = json.load(open(sys.argv[1]))
    print({k: d.get(k) for k in ("activity", "state_since", "working_at", "compacted_at", "bg_open", "at")})
except Exception as e:
    print("no activity file:", e)
' "$ACT")" "$(BOX | grep -c 'stalled-after-compact')"
    sleep 30
  done
} | scrub | ev L08-follow activity-samples.txt
BOX | scrub | grep -oE '"event": *"[a-z-]+"' | sort | uniq -c | ev L08-follow box-events.txt
{ grep -rhaE 'stalled-after-compact' "$AH_HOME" "$HIER" "$HOME/.claude/ah2" 2>/dev/null; } | scrub | cut -c1-400 | tail -6 | ev L08-follow rows.txt
tail -n +"$LN" "$K2X" | grep -E 'you compacted your context|Continue your brief' | scrub | cut -c1-500 | tail -3 | ev L08-follow resume-mail.txt
{ tx_marks 'you compacted your context|Continue your brief' "$K2X" | scrub | cut -c1-300; echo "--- member's next action"; tx_after "$K2X" 6; } | scrub | cut -c1-600 | ev L08-follow member-next.txt
head -c 3000 "$HIER/status.json" | scrub | ev L08-follow status-head.txt
cap "$K2" | scrub | tail -20 | ev L08-follow member-screen.txt
if [ "$K2W$K2C" != 00 ]; then driver "stalled-after-compact" "member compacts mid-brief and idles" "working turn rc=$K2W compact rc=$K2C"; else
chk "a stalled-after-compact row or event within 9 min" "[ -s '$T/evidence/L08-follow/rows.txt' ]"
finish "stalled-after-compact" "member compacts mid-brief and idles" "Measured: L08-follow/activity-samples.txt holds the condition's fields every 30 s; member-next.txt is what the member did. Nothing here spoke to the member after the compact."; fi

canaries_or_stop end-follow
echo "H04 K1: working rc=$K1W compact rc=$K1C go rc=$K1G"
echo "FOLLOW COMPACT DONE"
