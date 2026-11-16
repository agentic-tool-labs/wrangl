#!/bin/bash
# agent-hierarchy — a session whose cwd is a linked worktree, briefing a teammate of the
# main checkout's Team: the request lands in that Team's pool, names its team file, and
# passes the msg gate from the worktree. HOME-redirected; real state untouched.
# Usage: bash tests/test-msg-worktree-team.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
. "$PLUGIN/tests/lib-intent.sh"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
H="$PLUGIN/hooks"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-wtteam-test.XXXXXX")"
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
FAKEHOME="$SANDBOX/home"
MAIN="$SANDBOX/main"
WT="$SANDBOX/wt"
mkdir -p "$FAKEHOME/.claude/ah2" "$MAIN/.claude/ah2/teams"
unset WRANGL_HIERARCHY_DIR
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:400} ERR=${ERR:0:300})"; fi
}

git -C "$MAIN" init -q
git -C "$MAIN" -c user.email=t@t -c user.name=t commit -q --allow-empty -m init
git -C "$MAIN" worktree add -q "$WT" -b wt-branch

cat > "$MAIN/.claude/ah2/agent-hierarchy.json" <<EOF
{ "version": 1, "enabled": true, "roles": { "reviewer": { "model": "opus", "dispatch": "peer" } } }
EOF
TEAM_FILE="$MAIN/.claude/ah2/teams/main.json"
cat > "$TEAM_FILE" <<EOF
{ "team_id": "t1", "roster_level": null, "orchestrator": { "session_id": "s-main" }, "members": [ { "role": "reviewer", "name": "main-reviewer", "transport_id": "P7" } ] }
EOF
ah_test_prove_self orchestrator main "$TEAM_FILE" "" s1 > /dev/null  # this shell is the Orchestrator the messages below are written as, under the session id its hooks carry

msg() { OUT=$(HOME="$FAKEHOME" "$PLUGIN/bin/ah" msg "$@" 2>"$SANDBOX/err"); RC=$?; ERR=$(cat "$SANDBOX/err"); }
path_of() { printf '%s' "$OUT" | ah_msg_path; }

msg new --cwd "$WT" --to reviewer --from orchestrator --slug wt-task --title "Fix test titles" --to-name main-reviewer
REQ=$(path_of)
fill_intent "$REQ"
check "request to a main-checkout teammate, written from the worktree, lands in the Team's pool" \
  'case "$REQ" in "$MAIN/.claude/ah2/msgs/"*) true;; *) false;; esac'
check "it names the Team's file" 'ah_msg_text "$REQ" --cwd "$MAIN" | grep -qxF "team_file: $TEAM_FILE"'
check "stderr says where it went and why" 'echo "$ERR" | grep -qF "$MAIN/.claude/ah2"'

OUT=$(node -e 'const[c,t,m]=process.argv.slice(1);process.stdout.write(JSON.stringify({session_id:"s1",cwd:c,tool_name:"SendMessage",tool_input:{to:t,message:m,notify_when_idle:true}}));' \
  "$WT" main-reviewer "[hierarchy-peer-brief reply-to=\"sender\" task=\"wt-task\"]
[hierarchy-msg $REQ]" | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" pretooluse-msg-gate 2>&1); RC=$?
check "msg gate, session cwd in the worktree: the brief passes" '[ $RC -eq 0 ] && [ -z "$OUT" ]'

# The positive case above passes just as well when the hook's session check never runs (a stale binary on PATH), so the negatives and
# the logged refusal below are what show the check is live.
gate_as() {
  OUT=$(node -e 'const[s,c,t,m]=process.argv.slice(1);process.stdout.write(JSON.stringify({session_id:s,cwd:c,tool_name:"SendMessage",tool_input:{to:t,message:m,notify_when_idle:true}}));' \
    "$1" "$WT" main-reviewer "[hierarchy-peer-brief reply-to=\"sender\" task=\"wt-task\"]
[hierarchy-msg $REQ]" | HOME="$FAKEHOME" AH_MEMBER_NAME=main-sender sh "$PLUGIN/bin/ah-hook" pretooluse-msg-gate 2>&1); RC=$?
}
REFUSAL="this hook's session does not belong to its process"
HOOK_ERRORS="$FAKEHOME/.claude/ah2/hierarchy/hook-errors.jsonl"
gate_as s-unproven
check "msg gate: a payload session this process did not register is refused" '[ $RC -eq 0 ] && echo "$OUT" | grep -qF "\"permissionDecision\":\"deny\"" && echo "$OUT" | grep -qF "$REFUSAL"'
check "msg gate: ...and the hook-error log holds the does-not-belong record for it" 'grep -F "does not belong to the hook" "$HOOK_ERRORS" | grep -F "s-unproven" | grep -qF "its effects were refused"'
# a registration on this pid whose start time is one second off is registered but not proven
SKEW=$(ah_session_doc s1 | node -e 'const r=JSON.parse(require("fs").readFileSync(0,"utf8"));process.stdout.write(r.pid_start.replace(/:(\d\d) /,(m,x)=>":"+String(+x===59?58:+x+1).padStart(2,"0")+" "))')
# regs <sql with @T for each registration table>: runs it against sessions and sessions_unplaced
regs() { local t; for t in sessions sessions_unplaced; do sqlite3 "$AH_HOME/state/ah.db" "${1//@T/$t}"; done; }
regs "CREATE TABLE skew_@T AS SELECT * FROM @T WHERE session_id = 's1'; UPDATE skew_@T SET session_id = 's-skew', pid_start = '$SKEW', doc = json_set(doc, '\$.session_id', 's-skew', '\$.pid_start', '$SKEW'); INSERT INTO @T SELECT * FROM skew_@T; DROP TABLE skew_@T"
gate_as s-skew
check "msg gate: a session registered with a start time one second off is refused" '[ $RC -eq 0 ] && echo "$OUT" | grep -qF "\"permissionDecision\":\"deny\"" && echo "$OUT" | grep -qF "$REFUSAL"'
# with this process's own registration gone it proves no session, so the skewed registration is a live one that nothing proves: still refused
regs "CREATE TABLE held_@T AS SELECT * FROM @T WHERE session_id = 's1'; DELETE FROM @T WHERE session_id = 's1'"
gate_as s-skew
check "msg gate: a live registered payload session that this process holds none of is refused" '[ $RC -eq 0 ] && echo "$OUT" | grep -qF "\"permissionDecision\":\"deny\"" && echo "$OUT" | grep -qF "$REFUSAL"'
ah_forget_sessions s-skew
# and a payload session nobody registered, from a process that holds none, is a non-participant rather than a mismatch
gate_as s-unproven
check "msg gate: a session with no registration, from a process that holds none, is not refused" '[ $RC -eq 0 ] && ! echo "$OUT" | grep -qF "$REFUSAL"'
regs "INSERT INTO @T SELECT * FROM held_@T; DROP TABLE held_@T"

msg new --cwd "$WT" --to reviewer --from orchestrator --slug local-task --title "Fix test titles" --to-name nobody-here
REQ2=$(path_of)
check "the Orchestrator of the Team files a request to a name no Team holds in the Team home, not the worktree pool" \
  'case "$REQ2" in "$MAIN/.claude/ah2/msgs/"*) true;; *) false;; esac'
check "and names the sender's own team file, the scope it was written from" 'ah_msg_text "$REQ2" --cwd "$MAIN" | grep -qxF "team_file: $TEAM_FILE"'
mkdir -p "$SANDBOX/elsewhere"
msg new --cwd "$SANDBOX/elsewhere" --to reviewer --from orchestrator --slug no-scope-task --title "Fix test titles" --to-name nobody-here
REQ3=$(path_of)
check "from a cwd whose default scope has no team file, it names none" 'ah_msg_text "$REQ3" --cwd "$SANDBOX/elsewhere" | grep -qx "team_file: null"'

OUT=$(env -u TMUX_PANE -u CLAUDE_PID -u CLAUDE_CODE_SESSION_ID HOME="$FAKEHOME" HERDR_PANE_ID=P7 "$PLUGIN/bin/ah" roster whoami --cwd "$WT" 2>&1); RC=$?
check "whoami from the worktree finds the member in the main checkout's Team and reports that file" \
  'node -e "const o=JSON.parse(process.argv[1]);process.exit(o.member&&o.member.name===\"main-reviewer\"&&o.team_file===process.argv[2]?0:1)" "$OUT" "$TEAM_FILE"'

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
