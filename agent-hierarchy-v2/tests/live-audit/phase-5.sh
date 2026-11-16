#!/usr/bin/env bash
# Phase 5: sub-orchestrators and lanes (spec §5). D5-D8 are the user's decisions; a row says FAIL against the decision text and Q1 stays theirs.
AUDIT_PHASE=5
. "$(dirname "$0")/lib-audit.sh"
audit_init 50
start_daemon || { echo "daemon failed"; exit 9; }
canaries_or_stop start
HIER="$REPO/.claude/ah2"; TF="$HIER/teams/$TEAM.json"
start_orch || { echo "orch failed"; exit 9; }
BOX() { "$AHD" inspect entries --address "orchestrator@$TEAM" --all 2>/dev/null; }
A0=$(wc -l <"$T/cap/claude-argv.jsonl")

begin S01
B=$(tm list-panes -a -F '#{pane_id}' | sort)
do_in "$ORCH" s01 <<'EOF' 240
$AH roster stream-open a --team $TEAM --cwd "$REPO"; echo "open rc=$?"
$AH roster spawn-one sub-orchestrator --stream a --team $TEAM --cwd "$REPO"; echo "spawn rc=$?"
$AH roster stream-status --team $TEAM --cwd "$REPO"; echo "status rc=$?"
$AH roster stream-list --cwd "$REPO"; echo "list rc=$?"
EOF
keep s01
SO_NAME=$(out_of s01 | grep -oE "$TEAM-so-a[^\" ]*" | head -1)
sleep 15
SO=$(tm list-panes -a -F '#{pane_id}' | sort | comm -13 <(printf '%s\n' "$B") - | head -1)
[ -n "$SO" ] && reach_ready claude "$SO" 120
# The SO's transcript: the one under the lane worktree's project dir, not the newest anywhere.
SO_TX=$(ls -t "$HOME"/.claude/projects/*worktrees-a*/*.jsonl 2>/dev/null | head -1)
[ -n "$SO_TX" ] || SO_TX=$(ls -t "$HOME"/.claude/projects/*/*.jsonl | grep -vF "$ORCH_TX" | head -1)
printf '%s\n' "$SO_TX" | scrub | ev S01 so-tx.txt
tail -n +"$((A0 + 1))" "$T/cap/claude-argv.jsonl" | scrub | cut -c1-600 | ev S01 argv.txt
chk "member <X>-so-a" "[ -n '$SO_NAME' ]"
chk "pane" "[ -n '$SO' ]"
chk "stream verbs exit 0" "out_of s01 | grep -q 'status rc=0' && out_of s01 | grep -q 'list rc=0'"
want S01 && finish "stream-open, spawn SO" "orch opens stream a and spawns sub-orchestrator --stream a"

if want S05; then begin S05
  chk "argv has no --model (D6)" "! grep -q -- '--model' '$T/evidence/S01/argv.txt'"
  finish "D6, model inherit" "default roster row; spawn SO" "The argv is the launch as asked, before the shim's rewrite. FAIL here is against D6's text; Q1."
fi

if want S02; then begin S02
  do_in "$ORCH" s02 <<'EOF' 240
$AH roster spawn-one sub-orchestrator --team $TEAM --dry-run --cwd "$REPO"; echo "nostream rc=$?"
$AH roster spawn-one sub-orchestrator --stream a --team $TEAM --dry-run --cwd "$REPO"; echo "dup rc=$?"
$AH roster lane-caps --max-lanes 1 --team $TEAM --cwd "$REPO"; echo "caps rc=$?"
$AH roster stream-open b --base main --team $TEAM --cwd "$REPO"; echo "openb rc=$?"
$AH roster spawn-one sub-orchestrator --stream b --team $TEAM --dry-run --cwd "$REPO"; echo "over rc=$?"
$AH roster lane-caps --max-lanes 6 --team $TEAM --cwd "$REPO"; echo "caps2 rc=$?"
EOF
  keep s02
  grep -n -i 'lanes\|sub-orchestrators' "$T/plugin/docs/team-file.md" | head -3 | cut -c1-200 | ev S02 docs-team-file.txt
  grep -n -i 'at most three\|three' "$T/plugin/docs/cli-tools.md" | grep -i 'lane\|sub-orch' | head -3 | cut -c1-200 | ev S02 docs-cli.txt
  chk "no stream: stream-required" "out_of s02 | grep -q 'stream-required'"
  chk "duplicate: sub-orchestrator-exists" "out_of s02 | grep -q 'sub-orchestrator-exists'"
  chk "over cap: sub-orchestrator-breadth" "out_of s02 | grep -q 'sub-orchestrator-breadth'"
  finish "SO refusal codes, caps" "SO without stream; duplicate; over cap with lane-caps 1" "SO-spawns-an-SO leg (sub-orchestrator-depth) is under S03's session if it tried. The doc numbers on the cap are in docs-*.txt: team-file says 6; cli-tools says three."
fi

if [ -n "${SO:-}" ] && want S03; then begin S03
  CH="$TEAM-a"
  do_in "$SO" s03 <<EOF 240
\$AH roster whoami --cwd "\$REPO"; echo "who rc=\$?"
\$AH roster spawn-one sub-orchestrator --team $CH --stream a --dry-run --cwd "\$REPO"; echo "depth rc=\$?"
\$AH roster create --parent $TEAM --stream a --spawn --team $CH --cwd "\$REPO"; echo "create rc=\$?"
\$AH roster stream-report --state working --summary "audit lane up" --team $CH --cwd "\$REPO"; echo "report rc=\$?"
EOF
  S3=$?; keep s03
  sleep 10
  do_in "$ORCH" s03t <<'EOF'
$AH roster stream-status --team $TEAM --cwd "$REPO"; echo "status rc=$?"
EOF
  keep s03t
  head -c 4000 "$HIER/teams/$CH.json" 2>/dev/null | scrub | ev S03 child-team.txt
  BOX | scrub | grep -iE 'stream-report|workstream|audit lane up' | cut -c1-400 | ev S03 top-box.txt
  tx_tool_results "$SO_TX" AskUserQuestion | scrub | cut -c1-300 | ev S03 asks.txt
  if [ $S3 != 0 ]; then driver "SO dispatch" "SO spawns an implementor, briefs it, reports upward" "SO did not run the script"; else
  chk "SO-spawns-an-SO refused" "out_of s03 | grep -qE 'sub-orchestrator-depth|role-not-for-lanes'"
  chk "stream-report accepted" "out_of s03 | grep -q 'report rc=0'"
  # stream-report writes the child team file only (cli-tools.md:305, StreamReportVerb.cs:109); the top reads it from there.
  chk "report reaches the top" "grep -q 'audit lane up' '$HIER/teams/$CH.json' || out_of s03t | grep -q 'audit lane up' || grep -q 'audit lane up' '$TF'"
  finish "SO dispatch" "SO reports upward through stream-report" "The child implementor exchange not driven: each extra Haiku in a lane is a further session; the report path is what reaches the top. The refusal code seen is in s03.txt: role-not-for-lanes fires before sub-orchestrator-depth (SubOrchestratorSpawn.cs:240 before :56). Child team file: child-team.txt."; fi
fi

if want S04; then begin S04
  do_in "$ORCH" s04 <<'EOF'
$AH msg list --all --team $TEAM --cwd "$REPO"; echo "list rc=$?"
ls -d "$REPO"/.claude/worktrees/*/.claude/ah2/msgs "$REPO"/.claude/ah2/msgs 2>/dev/null
EOF
  keep s04
  find "$REPO/.claude" -type d -name msgs 2>/dev/null | scrub | ev S04 pools.txt
  chk "D5: one pool per tree" "[ \"\$(wc -l <'$T/evidence/S04/pools.txt')\" -le 1 ]"
  finish "D5, one pool per tree" "open a lane with a worktree; look at where messages live" "Rev 4 of the spec replaced one pool with worktree pools, so a FAIL against D5's text is expected; Q1. A cross-pool --ref delivery is S09."
fi

if [ -n "${SO:-}" ] && want S09; then begin S09
  do_in "$ORCH" s09 <<EOF
\$AH rules who-may-message orchestrator@\$TEAM $SO_NAME@\$TEAM; echo "rules rc=\$?"
\$AH msg send $SO_NAME@\$TEAM --kind note --text "audit top to lane" --json --cwd "\$REPO"; echo "top rc=\$?"
EOF
  keep s09; E=$(entry_of s09)
  # A transcript exists only from the session's first prompt, so S01 was too early to name it.
  [ -s "$SO_TX" ] || SO_TX=$(ls -t "$HOME"/.claude/projects/*worktrees-a*/*.jsonl 2>/dev/null | head -1)
  printf '%s\n' "$SO_TX" | scrub | ev S09 so-tx.txt
  poll 180 "m04_check '$E' '$SO_TX'"; D=$?
  do_in "$SO" s09b <<EOF
\$AH msg send orchestrator@$TEAM --kind note --text "audit lane to top" --json --cwd "\$REPO"; echo "up rc=\$?"
EOF
  S9=$?; keep s09b; U=$(entry_of s09b)
  poll 120 "m04_check '$U' '$ORCH_TX'"; D2=$?
  chk "top to SO delivered" "[ $D = 0 ]"
  if [ $S9 = 0 ]; then chk "SO to top delivered" "[ $D2 = 0 ]"; fi
  finish "lane mail" "top to SO member; SO to orch" "Lane-to-lane needs a second lane; not staged. Who-may-message answer in s09.txt."
fi

if [ -n "${SO:-}" ] && want S07; then begin S07
  # SO goes quiet for 10 min, then is killed. Labels only: never signalled or respawned by the product.
  N0=$(wc -l <"$T/cap/claude-argv.jsonl")
  sleep 600
  head -c 4000 "$HIER/status.json" 2>/dev/null | scrub | ev S07 status-quiet.txt
  SOPID=$(tm display -p -t "$SO" '#{pane_pid}')
  chk "SO still alive after 10 quiet minutes (never signalled)" "! pane_dead '$SO'"
  KILLS7=1
fi
if [ -n "${SO:-}" ] && { want S07 || want S06 || want S08; }; then
  begin S06
  kill_pane "$SO"
  # The event name is lowercase (LaneEscalation.cs:58); its text, with the code, is in the inbox.
  poll 420 "BOX | grep -qi 'lane-escalated'"; D=$?
  BOX | scrub | grep -oiE '(lane-[a-z]+|member-down)[^"]{0,160}' | ev S06 box.txt
  "$AH" msg inbox --address "orchestrator@$TEAM" --all --session "$ORCH_SID" --cwd "$REPO" 2>&1 | grep -iE 'lane-|escalat' | scrub | cut -c1-500 | ev S06 inbox.txt
  sleep 60
  tx_tool_results "$ORCH_TX" AskUserQuestion | scrub | cut -c1-500 | ev S06 asks.txt
  grep -F 'AskUserQuestion' "$T/cap/pretool.jsonl" 2>/dev/null | scrub | cut -c1-500 | ev S06 pretool.txt
  grep -iE 'lane-escalated|escalated \(E-a' "$ORCH_TX" | scrub | cut -c1-900 | head -3 | ev S06 tx.txt
  chk "LANE-ESCALATED E-a in orch's box" "grep -qi 'lane-escalated' '$T/evidence/S06/box.txt' && grep -q 'E-a' '$T/evidence/S06/inbox.txt' '$T/evidence/S06/tx.txt'"
  chk "the top offers respawn, finish or wind down" "[ -s '$T/evidence/S06/asks.txt' ] || [ -s '$T/evidence/S06/pretool.txt' ]"
  want S06 && finish "D7, dead SO asks the user" "kill -9 SO; orch running" "Sandbox difference: herdr shim exit 99. Haiku's choice on the ask is what is seen; no agent or skill text was found carrying this protocol."
  if want S07; then
    SAVED_S07_FAILS=
    begin S07
    head -c 4000 "$HIER/status.json" 2>/dev/null | scrub | ev S07 status-dead.txt
    N1=$(wc -l <"$T/cap/claude-argv.jsonl")
    tail -n +"$((${N0:-$A0} + 1))" "$T/cap/claude-argv.jsonl" | grep -F sub-orchestrator | scrub | cut -c1-300 | ev S07 respawns.txt
    chk "no respawn in argv" "[ ! -s '$T/evidence/S07/respawns.txt' ]"
    chk "team row still there" "grep -q '$SO_NAME' '$TF'"
    chk "dead label" "grep -q '\"live\": *false' '$T/evidence/S07/status-dead.txt' || grep -q 'member-down\|E-a' '$T/evidence/S06/box.txt'"
    finish "D8, labels only" "SO quiet 10 min, then killed" "Sandbox difference: herdr shim exit 99."
  fi
fi

if want S08; then begin S08
  "$AH" msg inbox --address "orchestrator@$TEAM" --all --session "$ORCH_SID" --cwd "$REPO" 2>&1 | grep -oiE 'E-[a-d]\b|lane-recovered' | sort | uniq -c | ev S08 codes.txt
  record S08 "lane escalation E-b, E-c, E-d, recovered" "SO blocked on a job; event unacked 5 min; status stale 30 min" NOT-TESTED "sandbox-cannot-host: no AHD_ knob found for Unacked and Stale, and the SO is already dead by this row. Codes seen this phase are in codes.txt; each leg would need a live lane for its real wait."
fi

if want S10; then begin S10
  do_in "$ORCH" s10 <<'EOF'
$AH roster stream-done a --dry-run --team $TEAM --cwd "$REPO"; echo "dry rc=$?"
$AH roster stream-done a --team $TEAM --cwd "$REPO"; echo "done rc=$?"
$AH roster stream-status --team $TEAM --cwd "$REPO"; echo "status rc=$?"
EOF
  keep s10
  chk "done answered" "out_of s10 | grep -qE 'done rc=[0-9]'"
  chk "state done, or refused with work open" "out_of s10 | grep -qE '\"state\": *\"done\"|refus|open'"
  finish "stream-done" "with work open" "Clean leg needs the lane wound down first; SO was killed above. Docs: it removes no worktree."
fi

for id in S01 S03 S09 S07; do
  if want "$id" && ! grep -q "^$id"$'\t' "$T/rows.tsv"; then
    record "$id" "lane row $id" "needs a live SO" NOT-TESTED "driver: no SO reached a composer in this run"
  fi
done

canaries_or_stop end-5
echo "PHASE 5 DONE: $(cut -f4 "$T/rows.tsv" | sort | uniq -c | tr '\n' ' ')"
