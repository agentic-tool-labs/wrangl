#!/usr/bin/env bash
# Phase 4: roster lifecycle (spec §5). Owner verbs run in orch's session; proof needs a real session above them.
AUDIT_PHASE=4
. "$(dirname "$0")/lib-audit.sh"
audit_init 45
start_daemon || { echo "daemon failed"; exit 9; }
canaries_or_stop start
HIER="$REPO/.claude/ah2"; TF="$HIER/teams/$TEAM.json"
start_orch || { echo "orch failed"; exit 9; }
fresh() { spawn_member "${1:-implementor}" "${2:---allow-main}" || return 1; FP=$SPAWNED_PANE; FN=$SPAWNED_NAME; FX=$SPAWNED_TX; }
BOX() { "$AHD" inspect entries --address "orchestrator@$TEAM" --all 2>/dev/null; }
fresh; IMPL=$FP; IMPL_NAME=$FN; TXI=$FX

if want R01; then begin R01
  B=$(tm list-panes -a -F '#{pane_id}' | sort)
  do_in "$ORCH" r01 <<'EOF'
$AH roster spawn-ad-hoc implementor --route peer --model inherit --allow-main --replace-live --team $TEAM --cwd "$REPO"; echo "adhoc rc=$?"
$AH roster spawn-ad-hoc qa --route subagent --model inherit --team $TEAM --cwd "$REPO"; echo "sub rc=$?"
$AH roster spawn-ad-hoc implementor --route subagent --model inherit --allow-main --replace-live --team $TEAM --cwd "$REPO"; echo "chain-sub rc=$?"
EOF
  keep r01; sleep 15
  N=$(tm list-panes -a -F '#{pane_id}' | sort | comm -13 <(printf '%s\n' "$B") - | head -1)
  chk "ad hoc member live" "out_of r01 | grep -q 'adhoc rc=0' && [ -n '$N' ]"
  chk "subagent route has no session to spawn" "out_of r01 | grep -q 'has no session to spawn'"
  finish "spawn-ad-hoc" "--role x --kind y --route peer; a subagent route"
fi

if want R02; then begin R02
  "$AH" roster dismiss "$IMPL_NAME" --cwd "$REPO" >"$T/r02-plain" 2>&1; P1=$?
  "$AH" roster create --plan --team auditbox-r02 --cwd "$REPO" >"$T/r02-plain2" 2>&1; P2=$?
  scrub <"$T/r02-plain" | ev R02 plain-dismiss.txt; scrub <"$T/r02-plain2" | ev R02 plain-create.txt
  do_in "$IMPL" r02 <<EOF
\$AH roster dismiss $IMPL_NAME --cwd "\$REPO"; echo "member rc=\$?"
EOF
  keep r02
  # NOTE: this harness's own shell sits under a Claude session on another home; the proof must still refuse.
  chk "plain terminal dismiss refused, exit 2" "[ $P1 = 2 ]"
  chk "refusal is owner-unproven or not-team-owner" "grep -qE 'owner-unproven|not-team-owner|no-orchestrator' '$T/evidence/R02/plain-dismiss.txt'"
  chk "member shell refused" "out_of r02 | grep -qE 'member rc=[1-9]'"
  finish "owner proof" "plain terminal, member shell each try dismiss and create" "plain create --plan rc=$P2, in plain-create.txt. Subagent leg not run."
fi

if want R03; then begin R03
  fresh; D1=$FP; DN=$FN
  do_in "$ORCH" r03 <<EOF
\$AH roster dismiss $DN --plan --cwd "\$REPO"; echo "plan rc=\$?"
\$AH roster dismiss $DN --close --cwd "\$REPO"; echo "close rc=\$?"
EOF
  keep r03; sleep 10
  chk "next in the plan" "out_of r03 | grep -q '\"next\"'"
  chk "close ran" "out_of r03 | grep -qE 'close rc=[03]'"
  chk "pane gone, or a hold with kept[].why" "! tm list-panes -a -F '#{pane_id}' | grep -qx '$D1' || out_of r03 | grep -q '\"why\"'"
  sleep 200
  BOX | grep -F "$DN" | grep -F member-down | scrub | cut -c1-300 | ev R03 alarm.txt
  chk "no later alarm" "[ ! -s '$T/evidence/R03/alarm.txt' ]"
  finish "dismiss" "--plan, --close" "Unreleased-member hold leg not staged."
fi

if want R05; then begin R05
  fresh; U1=$FP; UN=$FN
  do_in "$ORCH" r05 <<EOF
\$AH roster untrack $UN --plan --cwd "\$REPO"; echo "plan rc=\$?"
\$AH roster untrack $UN --commit --keep-sessions --cwd "\$REPO"; echo "commit rc=\$?"
\$AH roster reap --cwd "\$REPO"; echo "reap rc=\$?"
\$AH roster reap --commit --cwd "\$REPO"; echo "reapc rc=\$?"
EOF
  keep r05; sleep 5
  chk "row gone from the team file" "! grep -q '\"$UN\"' '$TF'"
  chk "pane stays for untrack" "tm list-panes -a -F '#{pane_id}' | grep -qx '$U1' && ! pane_dead '$U1'"
  chk "reap verbs exit 0" "out_of r05 | grep -q 'reap rc=0' && out_of r05 | grep -q 'reapc rc=0'"
  kill_pane "$U1"; sleep 200
  BOX | grep -F "$UN" | grep -F member-down | scrub | cut -c1-300 | ev R05 alarm.txt
  chk "no alarm for the untracked member (L02)" "[ ! -s '$T/evidence/R05/alarm.txt' ]"
  finish "untrack, reap" "untrack a live member; reap; both --commit and not"
fi

if want R06; then begin R06
  fresh; H1=$FP; HN=$FN
  do_in "$ORCH" r06 <<EOF
\$AH roster quit --team \$TEAM --wait 20 --cwd "\$REPO"; echo "quit rc=\$?"
\$AH roster quit --status --team \$TEAM --cwd "\$REPO"; echo "status rc=\$?"
\$AH msg send $HN@\$TEAM --kind note --text "audit while held" --cwd "\$REPO"; echo "held send rc=\$?"
\$AH roster resume --hold --team \$TEAM --yes --cwd "\$REPO"; echo "resume rc=\$?"
EOF
  keep r06
  E=$(out_of r06 | grep -oE 'entry [0-9-]+[a-z0-9]*' | head -1 | cut -d' ' -f2)
  chk "quit answered" "out_of r06 | grep -qE 'quit rc=[0-9]'"
  chk "delivery: drained" "out_of r06 | grep -q 'drained'"
  chk "resume exit 0" "out_of r06 | grep -q 'resume rc=0'"
  finish "quit, drained mail, resume --hold" "quit; send to the held team; quit --status; resume --hold --yes" "Whether members work again is not re-driven; the phase's later rows run on the resumed team."
fi

if want R07; then begin R07
  do_in "$ORCH" r07 <<'EOF'
$AH roster stop --now --team $TEAM --cwd "$REPO"; echo "plan rc=$?"
EOF
  keep r07
  L0=$(tm list-panes -a -F '#{pane_id} #{pane_dead}' | grep -c ' 0$')
  chk "plan only, no --confirm" "! out_of r07 | grep -q 'sealed'"
  chk "panes unchanged" "[ '$L0' -ge 2 ]"
  finish "stop --now" "without --confirm" "--confirm leg is held to the phase's end: it holds the team, and the rows after it would have nothing to drive. See R07b in verdict if run."
fi

if want R10; then begin R10
  fresh; M1=$FP; MN=$FN
  do_in "$ORCH" r10 <<EOF
\$AH roster stream-open r10s --no-worktree --team auditbox-r10 --cwd "\$REPO"; echo "other rc=\$?"
\$AH roster transfer --help; echo "help rc=\$?"
\$AH roster move $MN --new-tab --dry-run --team \$TEAM --cwd "\$REPO"; echo "move rc=\$?"
EOF
  keep r10
  chk "move answered" "out_of r10 | grep -q 'move rc=0'"
  finish "move, transfer" "move a member; transfer a team" "Move is a dry run under tmux: move's tabs are herdr's. Transfer not exercised on a live team: only its help ran. A partial row."
fi

if want R11; then begin R11
  do_in "$ORCH" r11 <<'EOF'
$AH roster doctor --cwd "$REPO"; echo "doctor rc=$?"
$AH roster doctor --check --cwd "$REPO"; echo "check rc=$?"
EOF
  keep r11
  mkdir -p "$HOME/.claude/ah2/hierarchy"; printf 'not json\n' >>"$HOME/.claude/ah2/hierarchy/hook-errors.jsonl"
  kill_pane "$IMPL"
  do_in "$ORCH" r11b <<'EOF'
$AH roster doctor --check --cwd "$REPO"; echo "soft rc=$?"
CLAUDE_PID=999999 $AH roster doctor --check --cwd "$REPO"; echo "broken rc=$?"
EOF
  keep r11b
  chk "named rows owner-proof, release-handshake" "out_of r11 | grep -q 'owner-proof' && out_of r11 | grep -q 'release-handshake'"
  chk "non-zero under --check with a broken thing" "out_of r11b | grep -qE 'broken rc=[1-9]'"
  finish "doctor" "doctor, doctor --check, with one thing broken" "soft rc: a member killed and one bad hook-errors line, not red by design (DoctorVerb.cs:514). broken rc: a dead CLAUDE_PID."
fi

if want R12; then begin R12
  do_in "$ORCH" r12 <<'EOF'
$AH roster teams --json --cwd "$REPO"; echo "teams rc=$?"
$AH roster history --cwd "$REPO"; echo "history rc=$?"
$AH roster resync --dry-run --team $TEAM --cwd "$REPO"; echo "resync rc=$?"
EOF
  keep r12
  chk "verbs exit 0" "out_of r12 | grep -q 'teams rc=0' && out_of r12 | grep -q 'history rc=0' && out_of r12 | grep -q 'resync rc=0'"
  chk "teams row names this team" "out_of r12 | grep -q '$TEAM'"
  finish "teams, history, resync" "live team" "Orphaned-team fields are in R09."
fi

if want R13; then begin R13
  D="$T/ws/r13"; mkdir -p "$D"; git -C "$D" init -q
  {
    "$AH" roster init repo --route peer --cwd "$D"; echo "init rc=$?"
    "$AH" roster add repo --role reviewer --route peer --cwd "$D"; echo "add rc=$?"
    "$AH" roster edit repo --member "$(basename "$D")-reviewer" --effort high --cwd "$D"; echo "edit rc=$?"
    "$AH" roster show repo --cwd "$D"; echo "show rc=$?"
    "$AH" roster add repo --role reviewer --route subagent --cwd "$D"; echo "subagent rc=$?"
    "$AH" roster remove repo --member "$(basename "$D")-reviewer" --cwd "$D"; echo "remove rc=$?"
    "$AH" roster roster copy default audit --level repo --cwd "$D"; echo "copy rc=$?"
    "$AH" roster roster use audit --level repo --cwd "$D"; echo "use rc=$?"
    "$AH" roster roster list --cwd "$D"; echo "list rc=$?"
  } >"$T/r13.out" 2>&1
  scrub <"$T/r13.out" | ev R13 verbs.txt
  do_in "$ORCH" r13b <<'EOF'
$AH roster spawn-one implementor --team $TEAM --dry-run --replace-live --cwd "$REPO"; echo "nomodel rc=$?"
EOF
  keep r13b
  chk "init, add, show exit 0" "grep -q 'init rc=0' '$T/r13.out' && grep -q 'add rc=0' '$T/r13.out' && grep -q 'show rc=0' '$T/r13.out'"
  chk "--route subagent refused for a chain role" "grep -q 'subagent rc=2' '$T/r13.out'"
  chk "no model: member-model-undefined" "out_of r13b | grep -q 'member-model-undefined'"
  finish "roster template verbs" "init, add, edit, remove, show, named roster use; no model set"
fi

if want R08; then begin R08
  fresh; S1=$FP; SN=$FN; SX=$FX; SID=${SPAWNED_SID:-}
  brief_to r08 "$SN" implementor r08-open "Reply DONE and wait."; R8=$(req_of r08); keep r08
  do_in "$ORCH" r08s <<'EOF'
$AH roster snapshot --team $TEAM --cwd "$REPO"; echo "snap rc=$?"
EOF
  keep r08s
  SNAP=$(out_of r08s | sed -nE 's/.*"snapshot": *"([^"]+)".*/\1/p' | head -1)
  kill_pane "$S1"
  A0=$(wc -l <"$T/cap/claude-argv.jsonl")
  do_in "$ORCH" r08r <<EOF
\$AH roster resume "$SNAP" --yes --cwd "\$REPO"; echo "resume rc=\$?"
EOF
  keep r08r; sleep 20
  tail -n +"$((A0 + 1))" "$T/cap/claude-argv.jsonl" | scrub | cut -c1-500 | ev R08 argv-after.txt
  chk "snapshot file" "[ -n '$SNAP' ] && [ -f '$SNAP' ]"
  chk "--resume in argv" "grep -q -- '--resume' '$T/evidence/R08/argv-after.txt'"
  chk "its session id" "grep -q '$SID' '$T/evidence/R08/argv-after.txt'"
  chk "no duplicate team rows" "[ \"\$(grep -c '\"name\": \"$SN\"' '$TF')\" -le 1 ]"
  finish "snapshot, resume" "snapshot, end the sessions, resume <snapshot>" "Only one member's session was ended."
fi

if want R04; then begin R04
  do_in "$ORCH" r04 <<'EOF'
$AH roster disband --plan --team $TEAM --cwd "$REPO"; echo "plan rc=$?"
$AH roster disband --close --confirm --plan-token stale-token --team $TEAM --cwd "$REPO"; echo "close rc=$?"
EOF
  keep r04; sleep 15
  tm list-panes -a -F '#{pane_id} #{pane_dead} #{pane_current_command}' | ev R04 panes-after.txt
  chk "next in the plan" "out_of r04 | grep -q '\"next\"'"
  chk "closed list" "out_of r04 | grep -q '\"closed\"'"
  chk "still_live reported" "out_of r04 | grep -q 'still_live'"
  chk "no surviving member pane" "[ \"\$(grep -c ' 0 ' '$T/evidence/R04/panes-after.txt')\" -le 1 ]"
  finish "disband" "--plan, --close --confirm --plan-token with a stale token" "Docs call --confirm and --plan-token accepted no-ops, so the stale token is not expected to refuse; this row checks what happens."
fi

if want R09; then begin R09
  # orch dies; a new session adopts. Last row: it needs the team file to survive, so a fresh team when R04 closed the first.
  want R04 && [ -n "$AUDIT_FILTER" ] && TEAM=auditbox-4r
  [ -z "$AUDIT_FILTER" ] && TEAM=auditbox-4r
  fresh; TF="$HIER/teams/$TEAM.json"
  OP=$(tm display -p -t "$ORCH" '#{pane_pid}')
  # Negative: a non-owner while the holder lives.
  do_in "$FP" r09n <<EOF
\$AH roster adopt --orchestrator-pid $OP --team $TEAM --cwd "\$REPO"; echo "nonowner rc=\$?"
EOF
  keep r09n
  kill_pane "$ORCH"
  # remain-on-exit keeps the dead pane's session, and start_orch needs the name.
  tm kill-session -t orch 2>/dev/null
  start_orch; NEWPID=$(tm display -p -t "$ORCH" '#{pane_pid}')
  script r09 <<EOF
\$AH roster teams --json --cwd "\$REPO"; echo "teams rc=\$?"
P=\$(ps -o ppid= -p \$PPID | tr -d ' '); [ -n "\$P" ] || P=$NEWPID
\$AH roster adopt --orchestrator-pid $NEWPID --team $TEAM --cwd "\$REPO"; echo "adopt rc=\$?"
EOF
  run_in "$ORCH" r09 120; keep r09
  cp "$TF" "$(ev_dir R09)/team-file.json" 2>/dev/null
  chk "non-owner refused while the holder lives" "out_of r09n | grep -qE 'nonowner rc=[1-9]'"
  chk "adopt answered" "out_of r09 | grep -q 'adopt rc=0'"
  chk "team owned by the new session" "grep -q '\"session_id\": *\"$ORCH_SID\"' '$TF'"
  finish "adopt" "orch dies; new session adopts; a non-owner tries while the holder lives" "That alarms go to the new owner is not staged: partial row."
fi

canaries_or_stop end-4
echo "PHASE 4 DONE: $(cut -f4 "$T/rows.tsv" | sort | uniq -c | tr '\n' ' ')"
