#!/usr/bin/env bash
# Phase 0: sandbox proof and canaries (spec §5, as ruled 2026-11-01). Gate, no rows.
# Exit 0 when the steps and the negative control hold. A6 and step 7 may end unresolved without holding anything up; the gate file says so.
AUDIT_PHASE=0
. "$(dirname "$0")/lib-audit.sh"

G=
gate() { printf '%s\n' "$*" >>"$G"; echo "GATE $*"; }
stop() {
  gate "STOP: $*"
  [ -n "${ORCH:-}" ] && cap "$ORCH" | scrub | ev phase-0 screen-orch.txt
  [ -n "${IMPL:-}" ] && cap "$IMPL" | scrub | ev phase-0 screen-impl.txt
  exit 9
}

audit_init 20
mkdir -p "$T/evidence/phase-0"
G="$T/evidence/phase-0/gate.txt"
gate "subject: $("$AH" --version 2>&1) from $(python3 -I -c 'import os,sys;print(os.path.realpath(sys.argv[1]))' "$SUBJECT_LINK")"
gate "tree tip: $(git -C "$AUDIT_TREE" rev-parse HEAD)"
gate "plugin name: $(python3 -I -c 'import json,sys;print(json.load(open(sys.argv[1]))["name"])' "$T/plugin/.claude-plugin/plugin.json") (A5)"
start_daemon || stop "A4 daemon socket never appeared: $(head -c 300 "$T/cap/ahd-start.txt")"
gate "A4 ahd binary from AH_BIN copy: ok; herdr at daemon start: $(cat "$T/cap/herdr-at-daemon-start.txt")"
canaries_or_stop start
gate "canaries before any session: ok"
sqlite3 -readonly "$AH_HOME/state/ah.db" "select key,value from meta where key like 'mode.%'" 2>&1 | ev phase-0 meta-modes.txt

# Step 3: orch launches, says one word, exits. A3 token, A5 agent name, A7 strict-mcp with plugin-dir.
start_orch || stop "A3/A5 orch never reached composer"
require_plugin
say "$ORCH" "Reply with exactly one word: ready" || stop "orch composer not idle"
poll 120 "grep -q '\"role\":\"assistant\"' '$ORCH_TX'" || stop "A3 no assistant reply"
gate "A3 token works under temp HOME: ok; A5 --agent wrangl:orchestrator: ok; A7 strict-mcp + plugin-dir hooks fired: ok"
tokens_total | ev phase-0 unit-cost.txt
gate "unit cost, one word from orch: $(cat "$T/evidence/phase-0/unit-cost.txt")"
tm send-keys -t "$ORCH" -l "/exit"; sleep 0.4; tm send-keys -t "$ORCH" Enter
poll 30 "pane_dead '$ORCH'" || gate "orch /exit did not end the pane within 30 s"
tm kill-session -t orch 2>/dev/null

# Step 4: new orch spawns impl. A1 env reaches member, A2 member lands on the private server, A4, A8 no prompts.
start_orch || stop "second orch never reached composer"
gate "orch session $ORCH_SID"
spawn_member implementor --allow-main || stop "A2/A8 impl spawn failed: $(out_of spawn1 | scrub | tail -5)"
IMPL=$SPAWNED_PANE; IMPL_NAME=$SPAWNED_NAME; TXI=$SPAWNED_TX
out_of spawn1 | scrub | ev phase-0 spawn.txt
tm list-panes -a -F '#{session_name} #{window_name} #{pane_id} #{pane_pid} #{pane_current_command}' | scrub | ev phase-0 panes.txt
gate "A2 member $IMPL_NAME pane on tmux -L ahprobe: ok"
tail -n 1 "$T/cap/claude-argv.jsonl" | scrub | ev phase-0 argv-member.txt
script env-impl <<'EOF'
echo "AH_HOME=$AH_HOME"; echo "AH_TEST=$AH_TEST"; echo "HOME=$HOME"; echo "AH_PLUGIN_ROOT=$AH_PLUGIN_ROOT"
echo "herdr=$(command -v herdr)"
$AH roster whoami --cwd "$REPO"
EOF
run_in "$IMPL" env-impl 120 || stop "driver: impl did not run env script (A8 prompt?)"
out_of env-impl | scrub | ev phase-0 env-impl.txt
out_of env-impl | grep -q "^AH_HOME=$AH_HOME\$" || stop "A1 AH_HOME did not reach member"
out_of env-impl | grep -q '^AH_TEST=1$' || stop "A1 AH_TEST did not reach member"
out_of env-impl | grep -q "^HOME=$HOME\$" || stop "A1 HOME did not reach member"
grep -hoE '"model":"[^"]*"' "$TXI" | sort -u | ev phase-0 member-model.txt
grep -q haiku "$T/evidence/phase-0/member-model.txt" || stop "member transcript is not on Haiku: $(cat "$T/evidence/phase-0/member-model.txt")"
gate "member runs on Haiku per its transcript; A1 env reaches member: ok; A4 member ah works; A8 no prompt with allow list: ok"
out_of env-impl | grep -q "\"name\": \"$IMPL_NAME" || gate "whoami did not name $IMPL_NAME (see env-impl.txt)"

# Step 5: A6. The tool result is waited for by its tool_use id. A miss after one re-prompt takes the false branch; it holds nothing up.
a6_has() { tool_done "$TXI" ListAgents; }
say "$IMPL" "Load the ListAgents tool with ToolSearch if it is deferred, call ListAgents once, then reply DONE." || stop "impl composer not idle"
poll 120 a6_has || { say "$IMPL" "Call ListAgents once now, then reply DONE." 60; poll 120 a6_has; }
tx_tool_results "$TXI" ListAgents | ev phase-0 A6-raw.txt
if a6_has; then
  if grep -qE 'ah2-rc[0-9]|orchestrator@engram|ah-orchestrator' "$T/evidence/phase-0/A6-raw.txt"; then
    gate "A6 FALSE: live names visible in sandbox ListAgents; finding; prompts never name them"
  else
    gate "A6 holds: no live team name in the result ($(wc -c <"$T/evidence/phase-0/A6-raw.txt" | tr -d ' ') bytes); an empty or caller-only list shows only that"
  fi
else
  gate "A6 UNRESOLVED: no ListAgents result after one re-prompt; false branch taken; finding"
fi
A9=$("$AHD" inspect entries --address "$IMPL_NAME@$TEAM" --all 2>&1); printf '%s\n' "$A9" | scrub | ev phase-0 A9.txt
case "$A9" in *'"ok":true'*) gate "A9 inspect entries from a plain shell: ok" ;; *) gate "A9 false: polling reads tx only" ;; esac

# Step 6: M04's checker. Positive first (so a checker that always says no is caught too), then the negative control.
send_mail() { # send_mail <script name> <to> <text>
  script "$1" <<EOF
\$AH msg send "$2@$TEAM" --text "$3" --kind note --json --cwd "\$REPO"
EOF
  run_in "$ORCH" "$1" 120
}
send_mail m-live "$IMPL_NAME" "audit probe live" || stop "driver: orch did not send to live member"
E1=$(first_entry "$(out_of m-live)")
[ -n "$E1" ] || stop "no entry id: $(out_of m-live | scrub | head -5)"
if poll 120 "m04_check '$E1' '$TXI'"; then gate "checker positive on live member, entry $E1: delivered"; else stop "checker never saw delivery to a live idle member, entry $E1"; fi
status_in "$ORCH" "$E1" st-live && out_of st-live | scrub | ev phase-0 status-live.txt

gate "killing impl pane $IMPL"
kill_pane "$IMPL" || stop "impl did not die"
send_mail m-dead "$IMPL_NAME" "audit probe dead" || stop "driver: orch did not send to dead member"
E2=$(first_entry "$(out_of m-dead)")
[ -n "$E2" ] || gate "send to dead member refused with no entry: $(out_of m-dead | scrub | head -5)"
sleep 60
if [ -n "$E2" ] && m04_check "$E2" "$TXI"; then
  stop "NEGATIVE CONTROL FAILED: checker reports delivered for entry $E2 to a dead member"
fi
[ -n "$E2" ] && status_in "$ORCH" "$E2" st-dead && out_of st-dead | scrub | ev phase-0 status-dead.txt
gate "negative control: checker reports NOT delivered for dead member, entry ${E2:-none}: ok"

# Step 7: gate_mode path, by behaviour, a fresh architect per reading (a nudged one probably won't run a second command). Unproven holds nothing up.
CFG="$HOME/.claude/ah2/agent-hierarchy.json"
reading() { # reading <label> <dir>: spawn arch; scripted ls as its first Bash call; evidence from that tool_use.
  local label=$1 dir=$2 tries
  mkdir -p "$dir"
  for tries in 1 2; do
    spawn_member architect || continue
    say "$SPAWNED_PANE" "Run exactly this one Bash command, then reply DONE: ls $dir" || continue
    poll 90 "tool_done '$SPAWNED_TX' Bash '$dir'" || continue
    tx_tool_results "$SPAWNED_TX" Bash "$dir" | scrub | ev phase-0 "reading-$label.txt"
    return 0
  done
  return 1
}
set_mode() { python3 -I - "$CFG" "$1" <<'PY'
import json, os, sys
p, v = sys.argv[1:3]
d = json.load(open(p)) if os.path.exists(p) else {}
d["gate_mode"] = v
json.dump(d, open(p, "w"))
PY
}
STEP7=unproven
if reading advisory "$T/s7a"; then
  set_mode enforce
  if reading enforce "$T/s7e"; then
    if grep -q 'ah-nudge' "$T/evidence/phase-0/reading-enforce.txt" && ! grep -q '"is_error": true' "$T/evidence/phase-0/reading-advisory.txt"; then STEP7=proven; else STEP7=no-change; fi
  fi
  set_mode advisory
fi
cp "$REPO/.claude/ah2/gates.jsonl" "$T/evidence/phase-0/gates.jsonl" 2>/dev/null
cp "$CFG" "$T/evidence/phase-0/config-after.json" 2>/dev/null
gate "step 7 gate_mode at $CFG: $STEP7 (reading-*.txt)"

canaries_or_stop gate-end
gate "PHASE 0 PASS"
