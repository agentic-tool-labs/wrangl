#!/bin/bash
# agent-hierarchy — spec 0028 §4 report-back completion: T7, T8, T8b, T9b, T9c, T10, T27.
# HOME-redirected; real config and real state are never touched.
# Usage: bash tests/test-report-back-completion.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
H="$PLUGIN/hooks"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-report-back-test.XXXXXX")"
hermetic_on_exit 'rm -rf "$SANDBOX"'
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/proj"
HIER_DIR="$SANDBOX/hier"
PASS=0; FAIL=0
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2" "$HIER_DIR"
ah_test_always_on "$FAKEHOME"

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:300})"; fi
}

# the suite runs hooks under payload sessions, so its own shell stays unregistered and each msg call is proven in a subshell as its --from
export AH_TEST_RUNS_HOOKS=1 AH_TEST_ORCH_SUBSHELL=1
ah_test_team "$SANDBOX/proof" rbc architect
msgcli() { local o rc; o=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HIER_DIR" ah_test_run_as_from rbc "$SANDBOX/proof/teams/rbc.json" "$PLUGIN/bin/ah" msg "$@" --cwd "$PROJ" 2>&1); rc=$?; ah_drop_checklist_hint <<< "$o"; return $rc; }

# ---------------------------------------------------------------- T7-T9: SubagentStop
TRANSCRIPT="$SANDBOX/agent-transcript.jsonl"

# subagentstop_hook <agent_id> <request-path> <last-assistant-message>
# agent_id is unique per call — the hook's one-shot "nudge" gate is keyed on it,
# so reusing an id across cases would let an earlier block silently satisfy a
# later one.
subagentstop_hook() {
  local aid=$1 req=$2 last=$3
  printf '{"type":"user","message":{"role":"user","content":"[hierarchy-msg %s] brief"}}\n' "$req" > "$TRANSCRIPT"
  printf '{"type":"assistant","message":{"role":"assistant","content":"working"}}\n' >> "$TRANSCRIPT"
  OUT=$(printf '{"agent_id":"%s","agent_type":"wrangl:architect","cwd":"%s","agent_transcript_path":"%s","last_assistant_message":"%s"}' \
        "$aid" "$PROJ" "$TRANSCRIPT" "$last" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HIER_DIR" sh "$PLUGIN/bin/ah-hook" subagentstop-msg-nudge 2>&1); RC=$?
}
is_block() { case "$OUT" in *'"decision":"block"'*) true;; *) false;; esac; }
is_empty() { [ -z "$OUT" ]; }

REQ_LINE=$(msgcli new --to architect --from orchestrator --slug t7 --title "Fix test titles" --plain)
REQ_ID=$(printf '%s' "$REQ_LINE" | awk '{print $2}')
REQ_PATH=$(printf '%s' "$REQ_LINE" | awk '{print $1}')

# T7: token present, response file MISSING -> block (falsifiable: the missing
# file must actually be the failure mode, not any other gate condition)
subagentstop_hook a7 "$REQ_PATH" "[hierarchy-msg $HIER_DIR/msgs/${REQ_ID}--orchestrator--t7--response.md]"
check "T7: response file missing is blocked" 'is_block'
check "T7: block names the missing file's response command" 'case "$OUT" in *"msg new --type response"*) true;; *) false;; esac'

RESP_STUB="$HIER_DIR/msgs/${REQ_ID}--orchestrator--t7--response.md"

# T8b (was T9, INVERTED — r4 finding 1): token present, response file exists
# but is an UNEDITED CLI-produced skeleton (no author content anywhere in the
# body) -> must BLOCK. As shipped this asserted allow, encoding the very
# defect r4 fixes; the assertion is inverted rather than left standing beside
# a new test, so the suite is never self-contradictory about what "filled"
# means.
msgcli new --type response --to orchestrator --from architect --id "$REQ_ID" >/dev/null
subagentstop_hook a8b "$REQ_PATH" "[hierarchy-msg $RESP_STUB]"
check "T8b: falsifiable — an unedited CLI-produced skeleton is blocked, not allowed" 'is_block'

# T9b: genuine authored content in the body -> allow
msgcli fill --id "$REQ_ID" --from architect --body '"## [1] status\n- fixed the bug in foo.py:42\n"' >/dev/null
subagentstop_hook a9b "$REQ_PATH" "[hierarchy-msg $RESP_STUB]"
check "T9b: OUTCOME — genuinely authored content allows the stop" 'is_empty'

# T9c: falsifiable — only ONE section filled in, the rest still skeleton -> allow
# (pins the boundary: this heuristic accepts partial-but-real completion,
# not just a fully-filled skeleton)
REQ_LINE=$(msgcli new --to architect --from orchestrator --slug t9c --title "Fix test titles" --plain)
REQ_ID=$(printf '%s' "$REQ_LINE" | awk '{print $2}')
REQ_PATH=$(printf '%s' "$REQ_LINE" | awk '{print $1}')
RESP_STUB="$HIER_DIR/msgs/${REQ_ID}--orchestrator--t9c--response.md"
msgcli new --type response --to orchestrator --from architect --id "$REQ_ID" >/dev/null
msgcli fill --id "$REQ_ID" --from architect --section 1 --body '"- done\n"' >/dev/null
subagentstop_hook a9c "$REQ_PATH" "[hierarchy-msg $RESP_STUB]"
check "T9c: falsifiable — one authored section, rest still skeleton, allows the stop" 'is_empty'

# ---------------------------------------------------------------- T27: subagent silent give-up is recorded (r4 finding 2)
REQ_PATH=$(msgcli new --to architect --from orchestrator --slug t27 --title "Fix test titles" --plain | awk '{print $1}')
# First SubagentStop: no response token satisfied -> blocks, records the one-shot "nudge" gate.
subagentstop_hook a27 "$REQ_PATH" "no pointer here"
check "T27: first stop (unsatisfied) blocks" 'is_block'
# Second SubagentStop, same agent_id, still unsatisfied: the one-shot bound
# means this must allow — but it must ALSO record the silent give-up now,
# where it previously recorded nothing.
subagentstop_hook a27 "$REQ_PATH" "no pointer here"
check "T27: second stop (still unsatisfied, already nudged) allows" 'is_empty'
check "T27: falsifiable — the silent give-up is recorded as nudge-unmet" \
  "ah_gate_rows | grep -q '\"type\":\"nudge-unmet\"' && ah_gate_rows | grep -q '\"agent_id\":\"a27\"'"

# ---------------------------------------------------------------- T10: peer Stop, unmet obligation on the record
PENDING="$FAKEHOME/.claude/ah2/agent-hierarchy.peer-pending.jsonl"
mkdir -p "$(dirname "$PENDING")"
now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
{
  printf '{"session_id":"s10","from":"peer-addr","from_name":"orchestrator-peer","reply_to":"sender","task":"t10","ts":"%s","status":"pending","nudges":2}\n' "$(now)"
  printf '{"type":"turn","session_id":"s10","status":"armed","ts":"%s"}\n' "$(now)"
} | ah_seed_peers

OUT=$(printf '{"session_id":"s10"}' | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" stop-peer-nudge 2>&1); RC=$?
check "T10: falsifiable — at MAX_NUDGES the stop is allowed (no output)" 'is_empty'
check "T10: an unmet-obligation record was written to the peer store" \
  "ah_peer_rows | grep '\"session_id\":\"s10\"' | grep -q '\"status\":\"waived\"'"
OUT=$(printf '{"session_id":"s10"}' | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" stop-peer-nudge 2>&1); RC=$?
check "T10: obligation no longer counts as pending: a second stop of the session is allowed with no output" 'is_empty'

# ---------------------------------------------------------------- T10b: peer Stop, last-attempt escalation text (§4.3)
PENDING_B="$FAKEHOME/.claude/ah2/agent-hierarchy.peer-pending.jsonl"
{
  printf '{"session_id":"s10b","from":"peer-addr","from_name":"orchestrator-peer","reply_to":"sender","task":"t10b","ts":"%s","status":"pending","nudges":1}\n' "$(now)"
  printf '{"type":"turn","session_id":"s10b","status":"armed","ts":"%s"}\n' "$(now)"
} | ah_seed_peers

OUT=$(printf '{"session_id":"s10b"}' | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" stop-peer-nudge 2>&1); RC=$?
check "T10b: falsifiable — nudges=1 (about to hit MAX_NUDGES) blocks with the last-attempt text" \
  'case "$OUT" in *"This is the last reminder; after it, the report is marked not delivered."*) true;; *) false;; esac'

echo "----"
echo "SUMMARY: $PASS passed, $FAIL failed"
[ $FAIL -eq 0 ]
