#!/usr/bin/env bash
# Phase 2: hooks and gates (spec §5). Gates run under advisory and enforce. Members are spawned before a switch to enforce, since enforce also
# bears on orch's own spawn call. Fresh member per nudge-sensitive leg: a nudged Haiku does not run a second command.
AUDIT_PHASE=2
. "$(dirname "$0")/lib-audit.sh"
audit_init 60
start_daemon || { echo "daemon failed"; exit 9; }
canaries_or_stop start
HIER="$REPO/.claude/ah2"; GATES="$HIER/gates.jsonl"; PEERS="$HIER/peers.jsonl"
start_orch || { echo "orch failed"; exit 9; }

fresh() { # fresh <role> [flags]: FP pane, FN name, FX tx. rc from the spawn.
  local rc=0
  spawn_member "$@" || rc=1
  FP=$SPAWNED_PANE; FN=$SPAWNED_NAME; FX=$SPAWNED_TX
  return $rc
}
slash() { wait_ready claude "$1" 120; tm send-keys -t "$1" -l "$2"; sleep 0.5; tm send-keys -t "$1" Enter; }
gates_since() { tail -n +"$(( $1 + 1 ))" "$GATES" 2>/dev/null; }
gcount() { wc -l <"$GATES" 2>/dev/null | tr -d ' ' || echo 0; }
# one_session <dir> <agent or -> <prompt or ->: a throwaway session outside the team. Sets OS_TX. Kills its session after.
one_session() {
  local d=$1 a=$2 p=$3 p0 flag=
  mkdir -p "$d"; git -C "$d" init -q 2>/dev/null; trust_dir "$d"
  [ "$a" = - ] || flag="--agent $a"
  p0=$(payload_count)
  launch "one$RANDOM" "$d" "exec claude $flag"
  OS_P=$PANE
  reach_ready claude "$OS_P" 90
  poll 30 "[ \"\$(payload_after $p0)\" != none ]"
  OS_TX="$HOME/.claude/projects/$(printf '%s' "$d" | tr '/.' '--')/$(payload_after "$p0" | cut -f1).jsonl"
  if [ "$p" != - ]; then say "$OS_P" "$p"; poll 90 "grep -q '\"role\":\"assistant\"' '$OS_TX'"; fi
}

fresh implementor --allow-main || { echo "impl failed"; exit 9; }
IMPL=$FP; IMPL_NAME=$FN; TXI=$FX

if want H01; then begin H01
  # Its own member: a transcript exists only after a first prompt, /compact needs a conversation, /clear rolls the session over.
  fresh implementor --allow-main; AP=$FP; AX=$FX
  say "$AP" "Reply with exactly one word: ok"; poll 90 "grep -q '\"role\":\"assistant\"' '$AX'"
  slash "$AP" "/compact"; poll 150 "grep -q '\"source\":\"compact\"' '$T/hook-payloads.jsonl'"
  sleep 5
  grep -c 'MAIN session of this Claude Code instance' "$AX" | ev H01 directive-count.txt
  slash "$AP" "/clear"; poll 60 "grep -q '\"source\":\"clear\"' '$T/hook-payloads.jsonl'"
  tm kill-pane -t "$AP" 2>/dev/null
  one_session "$REPO" - -; tm kill-pane -t "$OS_P" 2>/dev/null
  launch resume1 "$REPO" "exec claude --agent wrangl:orchestrator --resume $ORCH_SID --fork-session"; RP=$PANE; reach_ready claude "$RP" 60; sleep 3; tm kill-pane -t "$RP" 2>/dev/null
  grep -oE '"source":"[a-z]+"' "$T/hook-payloads.jsonl" | sort | uniq -c | ev H01 sources.txt
  ls "$T/plugin/tests/contracts/hooks/sessionstart" 2>/dev/null | ev H01 goldens.txt
  chk "startup seen" "grep -q startup '$T/evidence/H01/sources.txt'"
  chk "clear seen" "grep -q clear '$T/evidence/H01/sources.txt'"
  chk "compact seen" "grep -q compact '$T/evidence/H01/sources.txt'"
  chk "resume or fork seen" "grep -qE 'resume|fork' '$T/evidence/H01/sources.txt'"
  chk "directive in impl tx more than once" "[ \"\$(cat '$T/evidence/H01/directive-count.txt')\" -ge 2 ]"
  finish "SessionStart by source" "startup, /clear, /compact, resume with fork" "Wording checked for the directive's first sentence, not line by line against the goldens (goldens.txt lists them)."
fi

if want H02; then begin H02
  one_session "$T/ws/nocfg" - "Reply with exactly one word: ok"; cp "$OS_TX" "$T/nocfg.jsonl" 2>/dev/null; tm kill-pane -t "$OS_P" 2>/dev/null
  grep -c 'roster' "$T/nocfg.jsonl" | ev H02 nocfg-mentions.txt
  mkdir -p "$T/ws/off/.claude/ah2"; printf '{"enabled":false}\n' >"$T/ws/off/.claude/ah2/agent-hierarchy.json"
  one_session "$T/ws/off" - "Reply with exactly one word: ok"; cp "$OS_TX" "$T/off.jsonl" 2>/dev/null; tm kill-pane -t "$OS_P" 2>/dev/null
  # The plugin's agent and skill listings name the roles whatever the config says, so only injected hook text counts as noise.
  grep -oE 'ah CLI root|HIERARCHY STATE|agent-hierarchy Orchestrator protocol|roster init|hook_additional_context.{0,120}' "$T/off.jsonl" | scrub | ev H02 off-matches.txt
  grep -c 'ah CLI root\|HIERARCHY STATE\|agent-hierarchy Orchestrator protocol' "$T/off.jsonl" | ev H02 off-mentions.txt
  say "$IMPL" "Reply with exactly one word: ok"; poll 90 "grep -q '\"role\":\"assistant\"' '$TXI'"
  chk "no config prints an init nudge" "[ \"\$(cat '$T/evidence/H02/nocfg-mentions.txt')\" -ge 1 ]"
  chk "enabled:false is silent" "[ \"\$(cat '$T/evidence/H02/off-mentions.txt')\" = 0 ]"
  chk "Orchestrator text does not reach a member" "! grep -q 'You are the Orchestrator' '$TXI'"
  chk "member gets the member directive" "grep -q 'Implementor contract' '$TXI'"
  finish "SessionStart role variants" "--agent top-level; no config; enabled:false" "Subagent leg is under H03."
fi

if want H03; then begin H03
  say "$IMPL" "Use the Agent tool once with subagent_type general-purpose and the prompt: reply OK. Then reply DONE."
  poll 150 "ls '${TXI%.jsonl}'/subagents/*.jsonl"; D=$?
  V=$("$AH" --version | grep -oE '[0-9]+\.[0-9]+\.[0-9]+-[a-z.0-9]+')
  SUB=$(ls "${TXI%.jsonl}"/subagents/*.jsonl 2>/dev/null | head -1)
  say "$IMPL" "Reply with exactly one word: ok"; sleep 15
  grep -oE 'ah CLI root \(v[^)]*\)' "$TXI" | sort | uniq -c | ev H03 member-root.txt
  grep -oE 'ah CLI root \(v[^)]*\)' "$SUB" 2>/dev/null | sort | uniq -c | ev H03 subagent-root.txt
  if [ $D != 0 ]; then driver "root line" "each prompt; subagent via SubagentStart" "impl did not dispatch a subagent"; else
  chk "member root line" "[ -s '$T/evidence/H03/member-root.txt' ]"
  chk "version matches subject $V" "grep -qF '$V' '$T/evidence/H03/member-root.txt'"
  chk "subagent root line" "[ -s '$T/evidence/H03/subagent-root.txt' ]"
  finish "root line" "each prompt; subagent via SubagentStart"; fi
fi

if want H04; then begin H04
  # A filtered run reaches here with no transcript yet: a transcript and a conversation to compact both need a first prompt.
  say "$IMPL" "Reply with exactly one word: ok"; poll 90 "grep -q '\"role\":\"assistant\"' '$TXI'"
  brief_to h04 "$IMPL_NAME" implementor h04-open "Reply DONE and wait; no report yet."; keep h04
  sleep 20
  G0=$(gcount)
  C0=$(grep -c '"source":"compact"' "$T/hook-payloads.jsonl")
  slash "$IMPL" "/compact"; poll 180 "[ \"\$(grep -c '\"source\":\"compact\"' '$T/hook-payloads.jsonl')\" -gt $C0 ]"
  sleep 15
  gates_since "$G0" | scrub | cut -c1-400 | ev H04 gates.txt
  chk "HIERARCHY STATE in tx" "grep -q 'HIERARCHY STATE' '$TXI'"
  chk "open exchange listed" "grep 'HIERARCHY STATE' '$TXI' | grep -q 'h04-open\|$(req_of h04)'"
  chk "gates outcome reset" "grep -q 'reset' '$T/evidence/H04/gates.txt'"
  # The spec's "same in orch" leg: SessionStart.cs:96 gives a role member the role notice, orch the state block.
  C1=$(grep -c '"source":"compact"' "$T/hook-payloads.jsonl")
  slash "$ORCH" "/compact"; poll 180 "[ \"\$(grep -c '\"source\":\"compact\"' '$T/hook-payloads.jsonl')\" -gt $C1 ]"
  sleep 15
  cat "$(dirname "$ORCH_TX")"/*.jsonl | grep -F 'HIERARCHY STATE' | tail -1 | sed -E 's/.*HIERARCHY STATE/HIERARCHY STATE/' | scrub | cut -c1-3000 | ev H04 orch-state.txt
  chk "orch: HIERARCHY STATE" "[ -s '$T/evidence/H04/orch-state.txt' ]"
  chk "orch: open exchange listed" "grep -q 'h04-open\|$(req_of h04)' '$T/evidence/H04/orch-state.txt'"
  finish "compaction re-injection" "/compact in impl with an open brief" "A role member gets the role notice, not HIERARCHY STATE (SessionStart.cs:96): the first two checks fail on that, orch's two are the same text in orch."
fi

if want H05; then begin H05
  fresh implementor --allow-main; HP=$FP; HN=$FN; HX=$FX
  brief_to h05 "$HN" implementor h05-owe "Reply DONE and stop. Do not send a report."; keep h05
  HS=$(basename "$HX" .jsonl)
  # The row names the brief in task (StopPeerNudge.cs:240); its store is by mode, so both are read.
  R05=$(req_of h05)
  nudge_rows() { grep -rhaE '"type": *"(mail|file)-nudge"' "$AH_HOME" "$HIER" 2>/dev/null | grep -aF -e "$HS" -e "$R05"; }
  poll 180 "[ -n \"\$(nudge_rows)\" ]"
  sleep 60
  grep -cE 'you have a brief to answer' "$HX" | ev H05 nudge-lines.txt
  nudge_rows | scrub | cut -c1-300 | tail -5 | ev H05 peers.txt
  N=$(cat "$T/evidence/H05/nudge-lines.txt")
  chk "a nudge row for this member" "[ -s '$T/evidence/H05/peers.txt' ]"
  chk "session idle at the composer, not trapped" "wait_ready claude '$HP' 30"
  finish "stop-peer-nudge" "member owes a reply and stops" "transcript lines naming the unit: $N"
fi

if want H06; then begin H06
  fresh implementor --allow-main; JP=$FP; JX=$FX
  G0=$(gcount)
  say "$JP" "Run exactly this Bash command with run_in_background true, then reply DONE and end your turn: sleep 300"
  poll 120 "grep -q 'stop-member-liveness' '$JX' || gates_since $G0 | grep -q wake-nudge"; D=$?
  sleep 20
  gates_since "$G0" | scrub | cut -c1-300 | ev H06 gates.txt
  txlines "$JX" stop-member-liveness tx.txt
  chk "blocks or nudges for the job" "[ $D = 0 ]"
  chk "wake-nudge in gates" "grep -q wake-nudge '$T/evidence/H06/gates.txt'"
  chk "not trapped" "wait_ready claude '$JP' 60"
  finish "stop-member-liveness" "member stops with a background job and no waiter"
fi

if want H07; then begin H07
  G0=$(gcount)
  brief_to h07 "$IMPL_NAME" implementor h07-flight "Reply DONE and stop."; keep h07
  poll 120 "grep -q 'stop-orchestrator-liveness' '$ORCH_TX'"; D=$?
  sleep 30
  txlines "$ORCH_TX" stop-orchestrator-liveness tx.txt
  N=$(grep -c 'stop-orchestrator-liveness' "$ORCH_TX")
  chk "fires with work in flight" "[ $D = 0 ]"
  chk "cap of 3 blocks per stop ($N lines over the phase)" "wait_ready claude '$ORCH' 60"
  finish "stop-orchestrator-liveness, wake rule" "orch stops with work in flight and no waiter"
fi

if want H08; then begin H08
  brief_to h08 "$IMPL_NAME" implementor h08-sub "Reply DONE."; R=$(req_of h08); keep h08
  wait_ready claude "$IMPL" 120
  say "$IMPL" "Use the Agent tool once with subagent_type general-purpose and this prompt, verbatim: [hierarchy-msg $R] Reply OK and stop. Do not respond to the message. Then reply DONE."
  poll 150 "ls '${TXI%.jsonl}'/subagents/*.jsonl | xargs grep -l '$R'"; D=$?
  sleep 20
  U="$HOME/.claude/ah2/agent-hierarchy.usage.jsonl"
  tail -5 "$U" 2>/dev/null | scrub | cut -c1-300 | ev H08 usage.txt
  if [ $D != 0 ]; then driver "SubagentStop nudge, usage" "member dispatches a subagent with [hierarchy-msg]" "impl did not dispatch it"; else
  chk "usage row" "[ -s '$T/evidence/H08/usage.txt' ]"
  chk "nudge in the subagent's transcript" "ls '${TXI%.jsonl}'/subagents/*.jsonl | xargs grep -l 'hierarchy-msg $R' | xargs grep -lE 'response|block'"
  finish "SubagentStop nudge, usage" "member dispatches a subagent with [hierarchy-msg]"; fi
fi

if want H09; then begin H09
  fresh reviewer; EP=$FP; EN=$FN; ES=$(basename "$FX" .jsonl)
  slash "$EP" "/exit"; poll 30 "pane_dead '$EP'"
  sleep 5
  # Peer rows carry the session id and role, not the member name.
  grep -F "$ES" "$PEERS" | scrub | cut -c1-300 | tail -4 | ev H09 peers.txt
  do_in "$ORCH" h09 <<EOF
\$AH roster status --plain --cwd "\$REPO"
\$AH msg roster --team \$TEAM --cwd "\$REPO"
EOF
  keep h09
  sqlite3 -readonly "$AH_HOME/state/ah.db" "select * from sessions" 2>/dev/null | grep -F "$EN" | scrub | cut -c1-300 | ev H09 db.txt
  chk "row updated at session end" "grep -qiE '\"status\":\"(down|ended|exited|dead|closed)\"' '$T/evidence/H09/peers.txt' || grep -qiE 'end|exit|dead|closed' '$T/evidence/H09/db.txt'"
  finish "SessionEnd roster" "/exit a member"
fi

conduit_leg() { # conduit_leg <mode>: one fresh member tries all four. Denied means a tool result with is_error and the rule in gates.
  local m=$1 tool res
  set_gate_mode "$m"
  for tool in AskUserQuestion SendUserFile PushNotification ExitPlanMode; do
    say "$CP" "Call the $tool tool once now, loading it with ToolSearch if deferred, with any minimal valid input. Then reply DONE."
    poll 90 "tool_done '$CX' $tool"; res=$?
    tx_tool_results "$CX" "$tool" | tail -1 | scrub | cut -c1-400 >>"$(ev_dir H10)/$m-$tool.txt"
    [ $res = 0 ] && { chk "$m $tool denied" "grep -qE 'is_error\": true|ah-nudge|deny|denied|may not|conduit' '$T/evidence/H10/$m-$tool.txt'"; RAN=$((RAN + 1)); }
  done
}
if want H10; then begin H10
  RAN=0; G0=$(gcount)
  fresh implementor --allow-main; CP=$FP; CX=$FX
  conduit_leg advisory
  fresh_failed=0; set_gate_mode advisory; fresh implementor --allow-main || fresh_failed=1; CP=$FP; CX=$FX
  [ $fresh_failed = 0 ] && conduit_leg enforce
  set_gate_mode advisory
  gates_since "$G0" | grep -F conduit | scrub | cut -c1-300 | ev H10 gates.txt
  if [ $RAN = 0 ]; then driver "conduit gate" "member calls AskUserQuestion, SendUserFile, PushNotification, ExitPlanMode, both modes" "Haiku made none of the calls"; else
  chk "gates rule=conduit" "[ -s '$T/evidence/H10/gates.txt' ]"
  finish "conduit gate" "member calls the four conduit tools under both modes" "$RAN of 8 calls were made; subagent leg not run."; fi
fi

if want H11; then begin H11
  S=
  for leg in "ls:ls /tmp" "compound:ls /tmp; ls /private" "show:$AH msg list --cwd $REPO" "wait:$AH msg wait --cwd $REPO"; do
    lab=${leg%%:*}; c=${leg#*:}
    G0=$(gcount); fresh architect || continue
    [ "$lab" = show ] && { brief_to h11 "$FN" architect h11-brief "Read this and reply DONE."; R=$(req_of h11); keep h11; c="$AH msg show $R --cwd $REPO"; wait_ready claude "$FP" 60; }
    direct_in "$FP" "$FX" "$c" 90 || S="$S $lab"
    tool_text "$FX" Bash "$c" | scrub | cut -c1-500 | ev H11 "$lab.txt"
    gates_since "$G0" | grep -F self-care | scrub | cut -c1-300 | ev H11 "gates-$lab.txt"
  done
  chk "ls drew the self-care rule" "[ -s '$T/evidence/H11/gates-ls.txt' ]"
  chk "compound drew the rule" "[ -s '$T/evidence/H11/gates-compound.txt' ]"
  chk "msg show, required by brief intake, is not nudged (K3)" "[ ! -s '$T/evidence/H11/gates-show.txt' ]"
  chk "msg wait is not a verb on the allowlist's say-so" "! grep -q 'msg|.*wait\|wait|' '$T/evidence/H11/gates-ls.txt'"
  finish "self-care rule" "arch runs ls, a compound command, ah msg show, ah msg wait" "Haiku skipped legs:${S:- none}. Advisory mode; the enforce reading is H15."
fi

if want H12; then begin H12
  G0=$(gcount); set_gate_mode enforce
  set_gate_mode advisory; fresh reviewer; RP=$FP; RX=$FX; RN=$FN; set_gate_mode enforce
  say "$RP" "Use the Edit tool once to replace the word start with begin in $REPO/note.txt. Then reply DONE."
  poll 90 "tool_done '$RX' Edit note.txt"; A=$?
  tx_tool_results "$RX" Edit note.txt | scrub | cut -c1-500 | ev H12 edit-source.txt
  say "$RP" "Use the Write tool once to create $HIER/msgs/h12--response.md containing the word ok. Then reply DONE."
  poll 90 "tool_done '$RX' Write response.md"; B=$?
  tx_tool_results "$RX" Write response.md | scrub | cut -c1-500 | ev H12 edit-response.txt
  set_gate_mode advisory
  gates_since "$G0" | grep -E 'reviewer|write' | scrub | cut -c1-300 | ev H12 gates.txt
  if [ $A != 0 ]; then driver "reviewer-write" "rev edits a source file, then a --response.md" "rev made no Edit call"; else
  chk "source edit bounced under enforce" "grep -qE 'is_error\": true|ah-nudge' '$T/evidence/H12/edit-source.txt'"
  chk "note.txt untouched" "grep -q start '$REPO/note.txt'"
  chk "response passes" "[ $B = 0 ] && ! grep -q 'ah-nudge' '$T/evidence/H12/edit-response.txt'"
  finish "reviewer-write" "rev edits a source file, then a --response.md, under enforce"; fi
fi

if want H13; then begin H13
  G0=$(gcount)
  direct_in "$ORCH" "$ORCH_TX" "$AH roster dismiss nobody --cwd $REPO" 90; A=$?
  tool_text "$ORCH_TX" Bash "roster dismiss nobody" | scrub | cut -c1-600 | ev H13 dismiss.txt
  set_gate_mode enforce
  direct_in "$ORCH" "$ORCH_TX" "herdr agent start 'bad name!!'" 90; B=$?
  set_gate_mode advisory
  tool_text "$ORCH_TX" Bash "herdr agent start" | scrub | cut -c1-600 | ev H13 herdr.txt
  gates_since "$G0" | scrub | cut -c1-300 | ev H13 gates.txt
  cat "$T/cap/herdr-trace.txt" 2>/dev/null | grep 'agent start' | ev H13 shim-starts.txt
  if [ $A != 0 ]; then driver "roster-skill, herdr-name" "orch runs dismiss without the skill; bad herdr name under enforce" "orch made no call"; else
  chk "roster skill rule in gates" "grep -qE 'roster|skill' '$T/evidence/H13/gates.txt'"
  chk "bad herdr name stopped before the shim" "[ ! -s '$T/evidence/H13/shim-starts.txt' ]"
  finish "roster-skill, herdr-name" "orch runs dismiss without the skill; bad herdr name under enforce, first attempt" "Advisory leg of the herdr name did not run (Q6)."; fi
fi

if want H14; then begin H14
  G0=$(gcount); fresh implementor --allow-main; WP=$FP; WX=$FX
  direct_in "$WP" "$WX" "git -C $REPO switch -c h14-branch" 90; A=$?
  tool_text "$WX" Bash "switch -c h14" | scrub | cut -c1-500 | ev H14 switch.txt
  gates_since "$G0" | scrub | cut -c1-300 | ev H14 gates.txt
  git -C "$REPO" switch -q main 2>/dev/null
  if [ $A != 0 ]; then driver "ultra-advisor, worktree" "member runs git switch" "member made no call"; else
  chk "worktree change noticed" "grep -qE 'worktree|switch|branch' '$T/evidence/H14/gates.txt' || grep -qE 'ah-nudge|ah advisory' '$T/evidence/H14/switch.txt'"
  finish "ultra-advisor, worktree" "member runs git switch" "Ultra-advisor anchor leg and roster approve not run; they need a pipeline anchor. Partial row."; fi
fi

if want H15; then begin H15
  # Fresh arch, spawned BEFORE the switch: Phase 0's enforce leg died on the spawn.
  G0=$(gcount); fresh architect; AP=$FP; AX=$FX
  direct_in "$AP" "$AX" "ls $T/s" 90; A=$?
  grep -qF "\"command\":\"ls $T/s" "$AX" && A=0
  tx_tool_results "$AX" Bash "ls $T/s" | scrub | cut -c1-500 | ev H15 advisory.txt
  P0=$(wc -l <"$PEERS")
  fresh architect; BP=$FP; BX=$FX; G1=$(gcount)
  set_gate_mode enforce
  direct_in "$BP" "$BX" "ls $T/out" 90; B=$?
  # A denied call is what this leg is after, and it may leave no finished tool call: the attempt counts.
  grep -qF "\"command\":\"ls $T/out" "$BX" && B=0
  tm capture-pane -p -t "$BP" | scrub | tail -30 | ev H15 enforce-screen.txt
  tx_tool_results "$BX" Bash "ls $T/out" | scrub | cut -c1-500 | ev H15 enforce-1.txt
  for i in 2 3; do say "$BP" "Run exactly the same Bash command again, then reply DONE: ls $T/out"; sleep 25; done
  tx_tool_results "$BX" Bash "ls $T/out" | scrub | cut -c1-500 | ev H15 enforce-all.txt
  gates_since "$G1" | scrub | cut -c1-300 | ev H15 gates-enforce.txt
  set_gate_mode "bad-value"
  fresh_ok=1; set_gate_mode advisory; fresh architect || fresh_ok=0; DP=$FP; DX=$FX; set_gate_mode "bad-value"
  [ $fresh_ok = 1 ] && direct_in "$DP" "$DX" "ls $T/cap" 90; C=$?
  grep -qF "\"command\":\"ls $T/cap" "$DX" && C=0
  tx_tool_results "$DX" Bash "ls $T/cap" | scrub | cut -c1-500 | ev H15 badvalue.txt
  set_gate_mode advisory
  { grep -rhaE '"type": *"advisory"' "$AH_HOME" "$HIER" 2>/dev/null; sqlite3 -readonly "$AH_HOME/state/ah.db" "select cast(raw as text) from peer_records where type='advisory'" 2>/dev/null; } | scrub | cut -c1-300 | tail -3 | ev H15 peers-advisory.txt
  grep -n 'off.*log.*enforce\|"off"' "$T/plugin/docs/cli-tools.md" | grep -i gate | head -2 | cut -c1-200 | ev H15 vocab.txt
  if [ $A$B != 00 ]; then record H15 "bounce mechanism, gate_mode" "identical call 3 times; each mode; bad value" NOT-TESTED "driver: advisory rc=$A enforce rc=$B"; else
  chk "advisory: the ls ran" "! grep -q 'is_error\": true' '$T/evidence/H15/advisory.txt'"
  chk "advisory: type advisory row in peers" "[ -s '$T/evidence/H15/peers-advisory.txt' ]"
  chk "enforce: first call denied, NOT DONE" "grep -q 'NOT DONE' '$T/evidence/H15/enforce-1.txt'"
  chk "enforce: a bounce recorded in gates" "grep -q bounce '$T/evidence/H15/gates-enforce.txt'"
  chk "bad value reads as enforce" "[ '$C' = 0 ] && grep -q 'NOT DONE' '$T/evidence/H15/badvalue.txt'"
  finish "bounce mechanism, gate_mode" "identical call 3 times; each mode; bad value" "Third-attempt-passes is in enforce-all.txt, read by eye only if Haiku repeated. Vocabulary note: roster gate takes off|log|enforce (worktree gate, a different knob) while gate_mode takes advisory|enforce."; fi
fi

if want H16; then begin H16
  G0=$(gcount)
  say "$ORCH" "Use the Agent tool once with subagent_type wrangl:reviewer and the prompt: reply OK. Then reply DONE."
  poll 120 "tool_done '$ORCH_TX' Agent 'reply OK'"; A=$?
  tx_tool_results "$ORCH_TX" Agent "reply OK" | scrub | cut -c1-500 | ev H16 agent.txt
  say "$ORCH" "Load SendMessage if deferred, then send $IMPL_NAME this exact message once, then reply DONE: Please review the login code and report back."
  poll 120 "tool_done '$ORCH_TX' SendMessage 'login code'"; B=$?
  tx_tool_results "$ORCH_TX" SendMessage "login code" | scrub | cut -c1-500 | ev H16 sendmessage.txt
  gates_since "$G0" | scrub | cut -c1-300 | ev H16 gates.txt
  grep -E 'route|sendmessage|msg-gate|pretooluse-(route|msg)' "$ORCH_TX" | grep -E 'advisory|nudge' | scrub | cut -c1-400 | tail -3 | ev H16 advice.txt
  if [ $A$B != 00 ]; then driver "route, msg, sendmessage-response gates" "chain-role Agent dispatch; SendMessage brief with no [hierarchy-msg]" "agent rc=$A send rc=$B"; else
  chk "Agent dispatch of a chain role advised or denied" "grep -qE 'ah-nudge|advisory' '$T/evidence/H16/agent.txt' || [ -s '$T/evidence/H16/advice.txt' ]"
  chk "brief with no [hierarchy-msg] advised or denied" "grep -qE 'ah-nudge|advisory' '$T/evidence/H16/sendmessage.txt' || [ -s '$T/evidence/H16/advice.txt' ]"
  finish "route, msg, sendmessage-response gates" "chain-role Agent dispatch; SendMessage brief with no [hierarchy-msg]" "advisory mode; msgs:off leg not run."; fi
fi

if want H17; then begin H17
  # No allow list for this row: a separate session on settings with the allow list taken out.
  python3 -I -c '
import json, sys
p = sys.argv[1]
d = json.load(open(p)); d["permissions"]["allow"] = []
json.dump(d, open(p, "w"))' "$HOME/.claude/settings.json"
  fresh implementor --allow-main; NP=$FP; NX=$FX
  # Control: without a prompt on an ordinary command the allow list was not really out, and the row says nothing.
  say "$NP" "Run exactly this one Bash command, then reply DONE: touch $REPO/h17-control"; poll 60 "cap '$NP' | grep -qiE 'do you want|allow this'"; H17C=$?
  cap "$NP" | scrub | ev H17 screen-control.txt
  tm send-keys -t "$NP" Escape; sleep 2
  say "$NP" "Run exactly this one Bash command, then reply DONE: $AH msg list --cwd $REPO"; sleep 30; cap "$NP" | scrub | ev H17 screen-list.txt
  tool_done "$NX" Bash "msg list"; L=$?
  tm send-keys -t "$NP" Escape; sleep 2
  say "$NP" "Run exactly this one Bash command, then reply DONE: $AH msg list --cwd $REPO; echo chained" 20; sleep 30; cap "$NP" | scrub | ev H17 screen-chained.txt
  tm send-keys -t "$NP" Escape
  python3 -I -c '
import json, sys
p = sys.argv[1]
d = json.load(open(p)); d["permissions"]["allow"] = ["Bash", "Write", "Edit", "Read"]
json.dump(d, open(p, "w"))' "$HOME/.claude/settings.json"
  chk "plain verb raises no prompt, its result exists" "[ $L = 0 ]"
  chk "chained form is not auto-allowed: a prompt shows" "grep -qiE 'do you want|proceed|allow' '$T/evidence/H17/screen-chained.txt'"
  if [ "$H17C" != 0 ]; then record H17 "ah-cli permission hook" "no allow list: ah msg list; chained form" NOT-TESTED "sandbox-cannot-host: an ordinary command raised no permission prompt with allow:[] (screen-control.txt), so the hook's decision is not observable."; else
  finish "ah-cli permission hook" "no allow list: ah msg list; chained form" "dismiss --close and roster permissions legs not run: one needs a prompt answered on a live team, the other is off limits (§3.5)."; fi
fi

if want H18; then begin H18
  git -C "$REPO" push -q origin main 2>/dev/null
  G0=$(gcount); L0=$(find "$HIER" -name 'ledger*.jsonl' -exec cat {} + 2>/dev/null | wc -l | tr -d ' ')
  do_in "$IMPL" h18 <<'EOF'
cd "$REPO"
git switch -q -c h18 && git commit -q --allow-empty -m h18-one && git switch -q main
git merge -q --no-ff -m "h18 merge" h18; echo "merge rc=$?"
EOF
  keep h18
  direct_in "$IMPL" "$TXI" "git -C $REPO push origin main" 90; A=$?
  tool_text "$TXI" Bash "push origin main" | scrub | cut -c1-500 | ev H18 push.txt
  sleep 5
  find "$HIER" -name 'ledger*.jsonl' -exec tail -3 {} + 2>/dev/null | scrub | cut -c1-300 | ev H18 ledger.txt
  gates_since "$G0" | scrub | cut -c1-300 | ev H18 gates.txt
  git -C "$T/ws/remote.git" log --oneline main 2>/dev/null | head -3 | ev H18 remote.txt
  grep -n -i 'push' "$T/plugin/docs/how-it-works.md" | head -3 | cut -c1-200 | ev H18 docs-claim.txt
  if [ $A != 0 ]; then driver "push gate, merge-record" "git push and a merge against the sandbox remote" "impl made no push"; else
  chk "docs' guard acted on the push" "grep -qE 'ah-nudge|ah advisory|guard' '$T/evidence/H18/push.txt'"
  finish "push gate, merge-record" "git push and a merge against the sandbox remote" "Q2: code says the guard is gone (Gates/PushGuard.cs:7); the docs say otherwise. A FAIL here is against the docs. Merge-record leg not run: it fires only on the pinned gh pr merge of a pipeline run (PostToolUseMergeRecord.cs:8) and gh is off limits."; fi
fi

if want H19; then begin H19
  python3 -I - "$HOME/.claude/settings.json" <<'PY'
import json, sys
p = sys.argv[1]
d = json.load(open(p))
txt = "This git checkout is not enrolled for code indexing. Ask the user whether to enroll it, and record their answer."
out = json.dumps({"hookSpecificOutput": {"hookEventName": "SessionStart", "additionalContext": txt}})
d["hooks"]["SessionStart"].append({"hooks": [{"type": "command", "command": "printf '%s' '" + out + "'"}]})
json.dump(d, open(p, "w"))
PY
  A0=$(grep -c AskUserQuestion "$T/cap/pretool.jsonl" 2>/dev/null || echo 0)
  fresh implementor --allow-main; Y1=$FX; YP=$FP
  say "$YP" "Follow your session-start context now, then reply DONE."; poll 120 "grep -q '\"role\":\"assistant\"' '$Y1'"; sleep 25
  fresh architect; Y2=$FX; YQ=$FP
  say "$YQ" "Follow your session-start context now, then reply DONE."; poll 120 "grep -q '\"role\":\"assistant\"' '$Y2'"; sleep 25
  A1=$(grep -c AskUserQuestion "$T/cap/pretool.jsonl" 2>/dev/null || echo 0)
  python3 -I - "$HOME/.claude/settings.json" <<'PY'
import json, sys
p = sys.argv[1]
d = json.load(open(p)); d["hooks"]["SessionStart"] = d["hooks"]["SessionStart"][:1]
json.dump(d, open(p, "w"))
PY
  # inspect entries carries no text: the note's text is in orch's inbox and transcript.
  { "$AH" msg inbox --address "orchestrator@$TEAM" --all --session "$ORCH_SID" --cwd "$REPO" 2>&1; cat "$ORCH_TX"; cat "$Y1" "$Y2" 2>/dev/null | grep -E '"command":"[^"]*msg send'; } | scrub | grep -iE 'enroll' | cut -c1-500 | tail -5 | ev H19 notes-to-parent.txt
  tx_tool_results "$Y1" AskUserQuestion | scrub | cut -c1-400 | ev H19 ask-impl.txt
  tx_tool_results "$Y2" AskUserQuestion | scrub | cut -c1-400 | ev H19 ask-arch.txt
  for y in "$Y1" "$Y2"; do grep -E '"role":"assistant"' "$y" | tail -3 | scrub | cut -c1-500; done | ev H19 last-turns.txt
  chk "no AskUserQuestion reached the user: none allowed through" "! grep -q 'is_error\": false' '$T/evidence/H19/ask-impl.txt' && ! grep -q 'is_error\": false' '$T/evidence/H19/ask-arch.txt'"
  chk "directive line addressed to the lead in tx" "grep -q 'addressed to the lead' '$Y1' && grep -q 'addressed to the lead' '$Y2'"
  chk "note with default no to the parent" "[ -s '$T/evidence/H19/notes-to-parent.txt' ]"
  finish "third-party opt-in prompt reaching a member" "fixture hook emits an enrollment prompt to impl and arch" "AskUserQuestion attempts at the hook: $((A1 - A0)). Haiku's choice is apart from the gate: H10 proves the gate."
fi

if want H20; then record H20 "PermissionDenied alarm" "auto mode classifier refusal" NOT-TESTED "sandbox-cannot-host: A8 runs members in default mode, and no way to make the classifier refuse a harmless command was found. Not attempted, so whether Haiku supports auto stays unknown."; fi

if want H21; then begin H21
  cat >"$T/bin/stub529.py" <<'PY'
import http.server, sys
class H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("content-length", 0)); self.rfile.read(n)
        open(sys.argv[1], "a").write(self.path + "\n")
        self.send_response(529); self.send_header("content-type", "application/json"); self.end_headers()
        self.wfile.write(b'{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}')
    def log_message(self, *a): pass
s = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
open(sys.argv[2], "w").write(str(s.server_port))
s.serve_forever()
PY
  python3 -I "$T/bin/stub529.py" "$T/cap/stub-hits.txt" "$T/cap/stub-port" & probe_record_pid $!
  poll 10 "[ -s '$T/cap/stub-port' ]"
  P0=$(payload_count)
  tm set-environment -g ANTHROPIC_BASE_URL "http://127.0.0.1:$(cat "$T/cap/stub-port")"
  fresh implementor --allow-main; R1=$?; SP=$FP; SX=$FX
  tm set-environment -gu ANTHROPIC_BASE_URL
  if [ $R1 = 0 ]; then
    say "$SP" "Reply with exactly one word: ok"
    poll 240 "ls '$HIER'/rewake/* '$HIER'/activity/*"; sleep 90
    find "$HIER/rewake" "$HIER/activity" -type f 2>/dev/null | scrub | ev H21 files.txt
    wc -l <"$T/cap/stub-hits.txt" 2>/dev/null | ev H21 stub-hits.txt
    cap "$SP" | scrub | ev H21 screen.txt
    grep -rl StopFailure "$SX" 2>/dev/null | scrub | ev H21 tx-hit.txt
    chk "stub reached more than once: a resume was tried" "[ \"\$(cat '$T/evidence/H21/stub-hits.txt' | tr -d ' ')\" -ge 2 ]"
    chk "wake or activity record" "grep -q 'rewake\|activity' '$T/evidence/H21/files.txt'"
    finish "StopFailure, rewake" "member on a local stub returning 529, no tokens spent" "Live-check 11 in a worktree not run."
  else driver "StopFailure, rewake" "member on a local stub returning 529" "member never reached a composer on the stub"; fi
fi

canaries_or_stop end-2
echo "PHASE 2 DONE: $(cut -f4 "$T/rows.tsv" | sort | uniq -c | tr '\n' ' ')"
