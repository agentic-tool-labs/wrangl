#!/bin/bash
# agent-hierarchy — roster.mjs `create --commit --verified` (spec 0025 §3/§4): validates an
# array of member objects, or hydrates an array of member-name strings from the resolved roster;
# rejects malformed/mixed/unresolvable input before team.json is ever written.
# HOME-redirected; real state untouched.
# Usage: bash tests/test-roster-commit.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
H="$PLUGIN/hooks"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-roster-commit-test.XXXXXX")"
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
# No test may reach the real herdr or tmux: a stub that fails every call sits first on PATH (a failing herdr or tmux is what a
# machine without a running multiplexer gives), and the guard in lib-hermetic.sh is told it is a fake.
mkdir -p "$SANDBOX/nolaunch"; printf '#!/bin/sh\nexit 1\n' > "$SANDBOX/nolaunch/herdr"; cp "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"; chmod +x "$SANDBOX/nolaunch/herdr" "$SANDBOX/nolaunch/tmux"
export PATH="$SANDBOX/nolaunch:$PATH"; export AH_TEST_FAKE_BIN="$SANDBOX/nolaunch"
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/myrepo"
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2"
(cd "$PROJ" && git init -q)
PASS=0; FAIL=0
ah_test_prove_self orchestrator "$(basename "$PROJ")" > /dev/null  # this shell is the Orchestrator of the team the verbs below create

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:400})"; fi
}

run() { OUT=$(HOME="$FAKEHOME" "$PLUGIN/bin/ah" roster "$@" --cwd "$PROJ" 2>&1); RC=$?; }

# Spec 0044 §1.1: a team with no `--team` now lives at `teams/<effective-prefix>.json`,
# not the shared `team.json`. The prefix is the repo basename here, so the path is derived
# rather than spelled out — a renamed sandbox repo must not silently stop being checked.
TEAM_FILE="$PROJ/.claude/ah2/teams/$(basename "$PROJ").json"

run init --level repo --route peer
run add --no-spawn --level repo --role reviewer --model opus
run add --no-spawn --level repo --role implementor --model sonnet
# roster now defines: myrepo-reviewer, myrepo-implementor

# ---- 1: names present in the roster -> hydrated, transport_id null, needs_resync true
ah_forget_team "$TEAM_FILE"
run create --commit --verified '["myrepo-reviewer","myrepo-implementor"]' --transport terminal --roster-level repo --orchestrator-pid "$$"
check "1: create --commit with roster names succeeds" '[ "$RC" -eq 0 ]'
check "1: team.json holds two well-formed members with transport_id null" \
  'ah_team_doc "$TEAM_FILE" | node -e "const t=JSON.parse(require(\"fs\").readFileSync(0,\"utf8\"));process.exit(t.members.length===2&&t.members.every(m=>m.role&&m.name&&m.route&&m.transport_id===null)?0:1)"'
check "1: output carries needs_resync true" \
  'echo "$OUT" | grep -q "\"needs_resync\": true"'

# ---- 2: one name absent from the roster -> non-zero exit, message names it and lists roster names
ah_forget_team "$TEAM_FILE"
run create --commit --verified '["myrepo-reviewer","no-such-member"]' --transport terminal --roster-level repo --orchestrator-pid "$$"
check "2: unknown name -> non-zero exit" '[ "$RC" -ne 0 ]'
check "2: message names the unknown name" 'echo "$OUT" | grep -q "no-such-member"'
check "2: message lists the roster names" 'echo "$OUT" | grep -q "myrepo-reviewer" && echo "$OUT" | grep -q "myrepo-implementor"'

# ---- 3: mixed shape (object + string) -> non-zero exit
ah_forget_team "$TEAM_FILE"
run create --commit --verified '[{"name":"myrepo-reviewer","role":"reviewer","route":"peer"},"oops"]' --transport terminal --roster-level repo --orchestrator-pid "$$"
check "3: mixed --verified -> non-zero exit" '[ "$RC" -ne 0 ]'

# ---- 4: name absent from the roster -> team.json is NOT written (failure precedes the write)
ah_forget_team "$TEAM_FILE"
run create --commit --verified '["nope-not-a-member"]' --transport terminal --roster-level repo --orchestrator-pid "$$"
check "4: unresolvable name -> non-zero exit" '[ "$RC" -ne 0 ]'
check "4: team.json was not written" '! ah_team_stored "$TEAM_FILE"'

# ---- 6a: a member with name:null is refused by the store, which keys members by name; nothing is stored
ah_forget_team "$TEAM_FILE"
run create --commit --verified '[{"name":null,"role":"reviewer","route":"subagent"}]' --transport terminal --roster-level repo --orchestrator-pid "$$"
check "6a: subagent member with name null is refused, naming the reason" '[ "$RC" -ne 0 ] && echo "$OUT" | grep -q "a member has no name"'
check "6a: no team is stored" '! ah_team_stored "$TEAM_FILE"'

# ---- 6b: subagent-routed member with a real derived name (--spawn auto-mode shape) -> also succeeds
ah_forget_team "$TEAM_FILE"
run create --commit --verified '[{"name":"myrepo-reviewer-2","role":"reviewer","route":"subagent"}]' --transport terminal --roster-level repo --orchestrator-pid "$$"
check "6b: subagent member with a real name succeeds" '[ "$RC" -eq 0 ]'

# ---- 6c: name:"" is invalid on every route
ah_forget_team "$TEAM_FILE"
run create --commit --verified '[{"name":"","role":"reviewer","route":"subagent"}]' --transport terminal --roster-level repo --orchestrator-pid "$$"
check "6c: subagent member with name \"\" -> non-zero exit" '[ "$RC" -ne 0 ]'
ah_forget_team "$TEAM_FILE"
run create --commit --verified '[{"name":"","role":"reviewer","route":"peer"}]' --transport terminal --roster-level repo --orchestrator-pid "$$"
check "6c: peer member with name \"\" -> non-zero exit" '[ "$RC" -ne 0 ]'

# ---- 6d: the carve-out is route-scoped -- a PEER-routed member with name:null still fails
ah_forget_team "$TEAM_FILE"
run create --commit --verified '[{"name":null,"role":"reviewer","route":"peer"}]' --transport terminal --roster-level repo --orchestrator-pid "$$"
check "6d: peer member with name null -> non-zero exit" '[ "$RC" -ne 0 ]'

# ---- 7: commit registers the orchestrator and a member whose pane has a live roster row (item 0: no manual checkin)
ah_forget_team "$TEAM_FILE"
ah_seed_roster "$PROJ/.claude/ah2" "$(printf '{"type":"peer","status":"up","role":"reviewer","session_id":"mem-sess-7","pid":%s,"pane_id":"%%7","ts":"2026-01-01T00:00:00.000Z"}' "$$")"
run create --commit --verified '[{"name":"myrepo-reviewer","role":"reviewer","route":"peer","transport_id":"%7"}]' --transport terminal --roster-level repo --orchestrator-pid "$$" --session orch-sess-7
check "7: commit succeeds" '[ "$RC" -eq 0 ]'
check "7: the member registration holds name@team" 'ah_session_doc mem-sess-7 | grep -q "myrepo-reviewer@myrepo"'
check "7: the orchestrator registration holds orchestrator@team" 'ah_session_doc orch-sess-7 | grep -q "orchestrator@myrepo"'
check "7: the output lists the addresses registered" 'echo "$OUT" | grep -q "\"registered\"" && echo "$OUT" | grep -q "myrepo-reviewer@myrepo"'

# ---- 7n: a member with no roster row is not registered, and the commit still succeeds
ah_forget_team "$TEAM_FILE"; ah_forget_sessions mem-sess-7
run create --commit --verified '[{"name":"myrepo-reviewer","role":"reviewer","route":"peer","transport_id":"%8"}]' --transport terminal --roster-level repo --orchestrator-pid "$$" --session orch-sess-7
check "7n: commit succeeds" '[ "$RC" -eq 0 ]'
check "7n: no member registration was written" '[ -z "$(ah_session_doc mem-sess-7)" ]'

# ---- 8: a proven shell's commit records the session that a session-pids row ties to that process
session_of_team() { ah_team_doc "$TEAM_FILE" | node -e 'const t=JSON.parse(require("fs").readFileSync(0,"utf8"));process.stdout.write(String(t.orchestrator.session_id))'; }
commit_as_pid_only() { OUT=$(env -u CLAUDE_CODE_SESSION_ID CLAUDE_PID=$$ HOME="$FAKEHOME" "$PLUGIN/bin/ah" roster create --commit --verified '[]' --transport terminal --roster-level repo --cwd "$PROJ" 2>&1); RC=$?; }
# files-off-disk F3a: <repo> <session id> <pid>: a session_pids row under <repo>'s hierarchy dir, through session-pids.append (stamped now)
pid_row() { printf '{"args":{"dir":"%s","session_id":"%s","pid":%s},"context":{"cwd":"%s","hierarchy_dir":"%s"},"op_id":"row-%s-%s"}' "$1/.claude/ah2" "$2" "$3" "$1" "$1/.claude/ah2" "$RANDOM$RANDOM" "$$" | HOME="$FAKEHOME" "$PLUGIN/bin/ahd" state session-pids append >/dev/null 2>&1; }
clear_pid_rows() { [ -f "$AH_HOME/state/ah.db" ] && sqlite3 "$AH_HOME/state/ah.db" "DELETE FROM session_pids WHERE hier_dir IN ($(_ah_dirs "$PROJ/.claude/ah2"))"; }
ah_forget_team "$TEAM_FILE"; clear_pid_rows; ah_forget_sessions
ah_test_prove_self orchestrator "$(basename "$PROJ")" > /dev/null
pid_row "$PROJ" row-sess-8 "$$"
commit_as_pid_only
check "8: a proven shell's commit succeeds" '[ "$RC" -eq 0 ]'
check "8: the team file records the session the row names" '[ "$(session_of_team)" = "row-sess-8" ]'

# ---- 8n: a row older than the process's start is not that process's session (pid reuse); the commit warns and succeeds
ah_forget_team "$TEAM_FILE"; clear_pid_rows; ah_forget_sessions
ah_test_prove_self orchestrator "$(basename "$PROJ")" > /dev/null
# the append stamps now; the row is then aged in place, as no op writes a past time
pid_row "$PROJ" row-old-8 "$$"; sqlite3 "$AH_HOME/state/ah.db" "UPDATE session_pids SET at = '2001-01-01T00:00:00.000Z' WHERE session_id = 'row-old-8'"
commit_as_pid_only
check "8n: a proven shell's commit succeeds" '[ "$RC" -eq 0 ]'
check "8n: no session is recorded" '[ "$(session_of_team)" = "null" ]'
check "8n: the output warns the session is unknown" 'echo "$OUT" | grep -q "orchestrator session unknown"'

# ---- 8s: a row planted in the directory of an UNBOUND registration (a claimed pid and cwd) is not that process's session
OTHER="$SANDBOX/other-repo"; mkdir -p "$OTHER"; git -C "$OTHER" init -q
ah_forget_team "$TEAM_FILE"; clear_pid_rows; ah_forget_sessions
ah_test_prove_self orchestrator "$(basename "$PROJ")" > /dev/null
run create --commit --verified '[]' --transport terminal --roster-level repo --orchestrator-pid "$$" --session planted-8s
for t in sessions sessions_unplaced; do
  sqlite3 "$AH_HOME/state/ah.db" "UPDATE $t SET cwd = '$OTHER', doc = json_set(doc, '\$.bound', json('false'), '\$.cwd', '$OTHER') WHERE session_id = 'planted-8s'"
done
mkdir -p "$OTHER/.claude/ah2"
pid_row "$OTHER" planted-8s "$$"
ah_forget_team "$TEAM_FILE"
commit_as_pid_only
check "8s: the planted registration is unbound" 'ah_session_doc planted-8s | grep -q "\"bound\":false"'
check "8s: a proven shell's commit succeeds" '[ "$RC" -eq 0 ]'
check "8s: the session of the unbound registration's directory is not recorded" '[ "$(session_of_team)" = "null" ]'

# ---- 9: a shell no registration proves is not admitted as the Orchestrator, even when CLAUDE_PID names its own pid
ah_forget_team "$TEAM_FILE"
mkdir -p "$SANDBOX/ah-unproven"
OUT=$(env -u CLAUDE_CODE_SESSION_ID CLAUDE_PID=$$ AH_HOME="$SANDBOX/ah-unproven" HOME="$FAKEHOME" "$PLUGIN/bin/ah" roster create --commit --verified '[]' --transport terminal --roster-level repo --cwd "$PROJ" 2>&1); RC=$?
check "9: the unproven shell is refused as owner-unproven" '[ "$RC" -eq 2 ] && echo "$OUT" | grep -q "owner-unproven"'
check "9: no team file is written" '! ah_team_stored "$TEAM_FILE"'

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
