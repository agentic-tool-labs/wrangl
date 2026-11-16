#!/bin/bash
# agent-hierarchy — member_sessions answers "is this session a team member" whatever the member's liveness (S8-S12), and a
# running member whose latest roster row is a false `down` re-registers at its next prompt or stop, only from the process
# that registered it (S14-S22). Every write lands in a sandbox pool; the hook runs as a child of a wrapper whose pid is the
# registered pid (or another one).
# Usage: bash tests/test-member-sessions.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it

PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
. "$PLUGIN/tests/lib-seed.sh"
. "$PLUGIN/tests/lib-status-pool.sh"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-member-sessions-test.XXXXXX")"
[ -n "$SANDBOX" ] && [ -d "$SANDBOX" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (OUT=${OUT:0:300})"; fi
}

# ---- S8-S12: member_sessions
pool "$SANDBOX/p"
OLD="2020-01-01T00:00:00.000Z"
row() { ah_seed_roster "$HD" "$1"; }
mem() { printf '{"role":"%s","name":"%s","route":"peer","kind":"claude","transport_id":"%s"}' "$1" "$2" "$3"; }
status() { (cd "$SANDBOX" && HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" "$PLUGIN/bin/ah" roster status --cwd "$PROJ" >/dev/null 2>&1); }
ms() { node -e 'try { const o = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); console.log(JSON.stringify(o.member_sessions)); } catch { console.log("none"); }' "$(vdoc)"; }
live_of() { node -e 'try { const o = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); console.log(JSON.stringify(o.teams[0].members.find((m) => m.name === process.argv[2]).live)); } catch { console.log("none"); }' "$(vdoc)" "$1"; }

# S8: a member whose latest record is a down
team - "[$(mem implementor demo-down w1:p1)]"
row "{\"type\":\"peer\",\"status\":\"up\",\"role\":\"implementor\",\"session_id\":\"sess-down\",\"pid\":$$,\"pane_id\":\"w1:p1\",\"ts\":\"$OLD\"}"
row "{\"type\":\"peer\",\"status\":\"down\",\"role\":\"implementor\",\"session_id\":\"sess-down\",\"pid\":$$,\"pane_id\":\"w1:p1\",\"ts\":\"2020-01-01T00:00:01.000Z\"}"
status; OUT=$(ms)
check "S8: a member whose latest record is down is not live, and its session id is in member_sessions" '[ "$(live_of demo-down)" = false ] && [ "$OUT" = "[\"sess-down\"]" ]'

# S9: a member whose latest record is a stale seen
ah_clear_roster "$HD"
team - "[$(mem reviewer demo-seen w1:p2)]"
row "{\"type\":\"peer\",\"status\":\"seen\",\"name\":\"demo-seen\",\"role\":\"reviewer\",\"session_id\":\"sess-seen\",\"ts\":\"$OLD\"}"
status; OUT=$(ms)
check "S9: a member whose latest record is a seen older than the fresh window is not live, and its session id is listed" '[ "$(live_of demo-seen)" = false ] && [ "$OUT" = "[\"sess-seen\"]" ]'
ah_clear_roster "$HD"
row "{\"type\":\"peer\",\"status\":\"briefed\",\"name\":\"demo-seen\",\"role\":\"reviewer\",\"session_id\":\"sess-brief\",\"ts\":\"$OLD\"}"
status; OUT=$(ms)
check "S9: and so is a stale briefed" '[ "$OUT" = "[\"sess-brief\"]" ]'

# S10: nothing to list
ah_clear_roster "$HD"
team - "[$(mem reviewer demo-seen w1:p2)]"
status; OUT=$(ms)
check "S10: a member with no roster record contributes nothing" '[ "$OUT" = "[]" ]'
for sid in '""' 'null' '123' '["x"]'; do
  ah_clear_roster "$HD"
  row "{\"type\":\"peer\",\"status\":\"seen\",\"name\":\"demo-seen\",\"role\":\"reviewer\",\"session_id\":$sid,\"ts\":\"$OLD\"}"
  status; OUT=$(ms)
  check "S10: a latest record whose session_id is $sid contributes nothing, and nothing throws" '[ "$OUT" = "[]" ]'
done
ah_clear_roster "$HD"
row "{\"type\":\"peer\",\"status\":\"seen\",\"name\":\"demo-seen\",\"role\":\"reviewer\",\"ts\":\"$OLD\"}"
status; OUT=$(ms)
check "S10: a latest record with no session_id contributes nothing" '[ "$OUT" = "[]" ]'

# S11: an orchestrator session that is no member
ah_clear_roster "$HD"
team - "[$(mem implementor demo-down w1:p1)]"
row "{\"type\":\"peer\",\"status\":\"up\",\"role\":\"orchestrator\",\"session_id\":\"sess-orch\",\"pid\":$$,\"pane_id\":\"w9:p9\",\"ts\":\"$OLD\"}"
row "{\"type\":\"peer\",\"status\":\"down\",\"role\":\"implementor\",\"session_id\":\"sess-down\",\"pid\":$$,\"pane_id\":\"w1:p1\",\"ts\":\"$OLD\"}"
status; OUT=$(ms)
check "S11: a session that is no team member is not in member_sessions, whatever its own records" '[ "$OUT" = "[\"sess-down\"]" ]'

# S12: a restart lists only the new session
ah_clear_roster "$HD"
row "{\"type\":\"peer\",\"status\":\"up\",\"role\":\"implementor\",\"session_id\":\"sess-old\",\"pid\":$$,\"pane_id\":\"w1:p1\",\"ts\":\"2020-01-01T00:00:00.000Z\"}"
row "{\"type\":\"peer\",\"status\":\"down\",\"role\":\"implementor\",\"session_id\":\"sess-old\",\"pid\":$$,\"pane_id\":\"w1:p1\",\"ts\":\"2020-01-01T00:00:01.000Z\"}"
row "{\"type\":\"peer\",\"status\":\"up\",\"role\":\"implementor\",\"session_id\":\"sess-new\",\"pid\":$$,\"pane_id\":\"w1:p1\",\"ts\":\"2020-01-01T00:00:02.000Z\"}"
status; OUT=$(ms)
check "S12: a restarted member lists only its new session id" '[ "$OUT" = "[\"sess-new\"]" ]'
team - "[$(mem implementor demo-a w1:p1),$(mem reviewer demo-b w1:p2)]"
ah_clear_roster "$HD"
for n in 1 2 3 4; do row "{\"type\":\"peer\",\"status\":\"up\",\"role\":\"implementor\",\"session_id\":\"sess-r$n\",\"pid\":$$,\"pane_id\":\"w1:p1\",\"ts\":\"2020-01-01T00:00:0$n.000Z\"}"; done
row "{\"type\":\"peer\",\"status\":\"up\",\"role\":\"reviewer\",\"session_id\":\"sess-b\",\"pid\":$$,\"pane_id\":\"w1:p2\",\"ts\":\"2020-01-01T00:00:09.000Z\"}"
status; OUT=$(ms)
check "S12: the list is bounded by the member count, with duplicates removed" '[ "$(node -e "console.log(new Set(JSON.parse(process.argv[1])).size === JSON.parse(process.argv[1]).length && JSON.parse(process.argv[1]).length <= 2)" "$OUT")" = true ]'

# ---- S14-S22: the self-heal
pool "$SANDBOX/h"
SID="sess-heal"
HOME="$FAKEHOME" seed_session_role "$SID" implementor
ROSTER="$HD/peers.jsonl"
# S21: an unreadable roster
rm -rf "$ROSTER"; mkdir -p "$ROSTER"
OUT=$(printf '{"hook_event_name":"UserPromptSubmit","session_id":"%s","cwd":"%s"}' "$SID" "$PROJ" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" activity 2>&1); RC=$?
check "S21: an unreadable roster: no throw, exit 0, no stdout" '[ "$RC" = 0 ] && [ -z "$OUT" ]'
rm -rf "$ROSTER"
printf 'not json\n{"type":"peer"\n' > "$ROSTER"
OUT=$(printf '{"hook_event_name":"Stop","session_id":"%s","cwd":"%s"}' "$SID" "$PROJ" | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ah-hook" activity 2>&1); RC=$?
check "S21: a garbled roster: no throw, exit 0, no stdout" '[ "$RC" = 0 ] && [ -z "$OUT" ]'

echo "----"
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
