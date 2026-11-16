#!/bin/bash
# agent-hierarchy — hook state writes are not silently dropped. With the daemon down a usage row is written in-process; with the
# daemon up a HOME-scoped write needs no project directory, a project-scoped write in an enabled repository creates it first,
# and every request the daemon refuses is recorded in logs/rejected.jsonl. Every run is in a sandbox under /tmp/ahprobe.*.
# Usage: bash tests/test-hook-state-writes.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
AH_TEST_DAEMON=1
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
export AH_MEMBER_NAME=suite-member  # these sessions are team members; the hooks of one nothing identifies stay silent
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE CLAUDE_PID CLAUDE_CODE_SESSION_ID HERDR_PANE_ID TMUX_PANE AH_DAEMON AHD_VERSION_OVERRIDE AHD_IDLE_EXIT_MS AH_IMPL AH_DISPATCHED AH_PLUGIN_ROOT
T="$(mktemp -d /tmp/ahprobe.XXXXXX)"
case "$T" in /tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting"; exit 1;; esac
T="$(cd "$T" && pwd -P)"
export AH_HOME="$T/ah" HOME="$T/home" AH_TEST=1
for v in AH_HOME HOME; do case "${!v}" in "$T"/*) ;; *) echo "$v is not under $T; aborting"; exit 1;; esac; done
mkdir -p "$AH_HOME" "$HOME/.claude/ah2"
daemon_pids() {
  { node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid)+"\n")}catch{}' "$AH_HOME/run/ahd.json"
    cat "$AH_HOME"/logs/ahd.log* 2>/dev/null | sed -n 's/.* pid \([0-9]*\) root .*/\1/p'; } | sort -u
}
cleanup() {
  local p
  for p in $(daemon_pids); do ah_signal_ours TERM "$p" "$T"; done
  sleep 0.3
  for p in $(daemon_pids); do ah_signal_ours KILL "$p" "$T"; done
  local left; left=$(pgrep -f "$T" 2>/dev/null | grep -v "^$$\$" | tr '\n' ' ')
  [ -n "${left// /}" ] && echo "FAIL: processes still alive under $T: $left"
  [ -n "$AH_TEST_KEEP" ] && return
  case "$T" in /tmp/ahprobe.??????|/private/tmp/ahprobe.??????) rm -rf "$T";; esac
}
hermetic_on_exit cleanup
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (RC=$RC OUT=${OUT:0:400})"; fi; }
TODAY="$(date -u +%Y-%m-%d)"
REJECTED="$AH_HOME/logs/rejected.jsonl"
HOOKERRS="$HOME/.claude/ah2/hierarchy/hook-errors.jsonl"

# a finished subagent in a repository: its transcript is under the session's subagents directory
new_repo() { # <name>: a git repo with a subagent transcript; prints its path
  local r="$T/$1"
  mkdir -p "$r/sess/subagents"
  git -C "$r" init -q
  cat > "$r/sess/subagents/agent-id1.jsonl" <<EOF
{"type":"assistant","timestamp":"${TODAY}T10:00:01.000Z","message":{"model":"claude-opus-5","usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}
EOF
  printf '%s' "$r"
}
usage_hook() { # <repo>: SubagentStop for the repository's transcript, in OUT
  local r=$1
  OUT=$(printf '{"hook_event_name":"SubagentStop","agent_id":"id1","agent_type":"wrangl:architect","session_id":"sess","transcript_path":"%s/sess.jsonl","cwd":"%s"}' "$r" "$r" \
    | sh "$PLUGIN/bin/ah-hook" subagentstop-usage 2>/dev/null); RC=$?
}
rows() { [ -f "$1" ] && wc -l < "$1" | tr -d ' ' || echo 0; }
usage_rows() { ah_usage_rows | grep -c . ; }

# ---- 5a: no daemon at all: the usage row is written in-process under HOME
R=$(new_repo repo-nodaemon)
ah_forget_usage
AH_DAEMON=off usage_hook "$R"
check "5a: usage with no daemon exits 0" '[ "$RC" -eq 0 ]'
check "5a: the row is in the usage rows" '[ "$(usage_rows)" -eq 1 ]'
check "5a: no daemon was started" '[ -z "$(daemon_pids)" ]'

# ---- the daemon is up for what follows
OUT=$(sh "$PLUGIN/bin/ah" msg daemon start 2>&1); RC=$?
check "daemon starts in the sandbox" '[ "$RC" -eq 0 ] && [ -n "$(daemon_pids)" ]'

# ---- 2: a HOME-scoped write needs no project hierarchy directory
R=$(new_repo repo-home)
ah_forget_usage
rm -f "$REJECTED"
usage_hook "$R"
check "2: usage write with the daemon up and no project dir exits 0" '[ "$RC" -eq 0 ]'
check "2: the row landed in the usage rows" '[ "$(usage_rows)" -eq 1 ]'
check "2: no project hierarchy directory was created" '[ ! -e "$R/.claude/ah2" ]'
check "2: the daemon rejected nothing" '[ "$(rows "$REJECTED")" -eq 0 ]'

# ---- 3: a project-scoped write in an enabled repository creates the directory first
R=$(new_repo repo-project)
printf '{"enabled":true}\n' > "$HOME/.claude/ah2/agent-hierarchy.json"
OUT=$(printf '{"hook_event_name":"UserPromptSubmit","session_id":"sess-proj","cwd":"%s","prompt":"hello"}' "$R" \
  | sh "$PLUGIN/bin/ah-hook" userpromptsubmit-peer-tracking 2>/dev/null); RC=$?
check "3: the project-scoped hook exits 0" '[ "$RC" -eq 0 ]'
check "3: no hierarchy_dir ... does not exist row in hook-errors" '! grep -q "does not exist" "$HOOKERRS" 2>/dev/null'
check "3: the daemon rejected nothing for it" '! grep -q "hierarchy_dir" "$REJECTED" 2>/dev/null'
check "3: whatever the hook created is self-ignoring (the creation itself is proved by StateCallsProjectDirTests)" \
  '[ ! -e "$R/.claude/ah2" ] || [ "$(cat "$R/.claude/ah2/.gitignore")" = "*" ]'

# ---- 3n: a repository nobody enabled is never given a project directory
rm -f "$HOME/.claude/ah2/agent-hierarchy.json"
R=$(new_repo repo-unenabled)
OUT=$(printf '{"hook_event_name":"UserPromptSubmit","session_id":"sess-un","cwd":"%s","prompt":"hello"}' "$R" \
  | sh "$PLUGIN/bin/ah-hook" userpromptsubmit-peer-tracking 2>/dev/null); RC=$?
check "3n: an unconfigured repo is not given a hierarchy directory" '[ ! -e "$R/.claude/ah2" ]'

echo "----"
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
