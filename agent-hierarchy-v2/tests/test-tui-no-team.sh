#!/bin/bash
# agent-hierarchy — a live Claude session in a repo with ah2 enabled and no team of its own is listed by the daemon's root view as
# "<repo> · pid N" with its implicit one-member team (ah2-34), from the session's SessionStart and UserPromptSubmit hooks alone, against a real daemon. A repo that is not enabled lists
# nothing, and a session that began before the repo was enabled is listed once its next prompt runs.
# Everything runs in a sandbox under /tmp/ahprobe.* with its own daemon, which the script stops; a pgrep check at the end proves
# nothing of it is left. The hooks are the native binary run through bin/ah-hook (AH_BIN or ahd on PATH).
# Usage: bash tests/test-tui-no-team.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
AH_TEST_DAEMON=1
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE CLAUDE_PID CLAUDE_CODE_SESSION_ID HERDR_PANE_ID TMUX_PANE AH_DAEMON AHD_VERSION_OVERRIDE AHD_IDLE_EXIT_MS AH_IMPL AH_DISPATCHED AH_PLUGIN_ROOT
T="$(mktemp -d /tmp/ahprobe.XXXXXX)"
case "$T" in /tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting"; exit 1;; esac
T="$(cd "$T" && pwd -P)"
export AH_HOME="$T/ah" HOME="$T/home" AH_TEST=1
for v in AH_HOME HOME; do case "${!v}" in "$T"/*) ;; *) echo "$v is not under $T; aborting"; exit 1;; esac; done
mkdir -p "$AH_HOME" "$HOME/.claude/ah2"
ENABLED="$T/enabled-repo"
LATER="$T/later-repo"
OFF="$T/off-repo"
for d in "$ENABLED" "$LATER" "$OFF"; do mkdir -p "$d/.claude/ah2" && git -C "$d" init -q; done
printf '{"enabled":true}\n' > "$ENABLED/.claude/ah2/agent-hierarchy.json"
printf '{"enabled":false}\n' > "$OFF/.claude/ah2/agent-hierarchy.json"
# Listed means an Orchestrator with no team yet, so this home makes its sessions Orchestrators; T1b is a session nothing identifies.
printf '{"alwaysOrchestrator":true}\n' > "$HOME/.claude/ah2/agent-hierarchy.json"
mkdir -p "$T/barehome/.claude/ah2"
# the daemon drains only for a session it can identify or the user, so `ahd stop` runs in a subshell proven as a team's Orchestrator;
# this shell runs hooks and stays unregistered
export AH_TEST_RUNS_HOOKS=1
ah_test_team "$T/proof" tn
ahd_stop() { ah_test_run_as orchestrator tn "$T/proof/teams/tn.json" sh "$AHD" stop >/dev/null 2>&1; }
AHD="$PLUGIN/bin/ahd"
HK="$PLUGIN/bin/ah-hook"
PASS=0; FAIL=0

daemon_pids() {
  { node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid)+"\n")}catch{}' "$AH_HOME/run/ahd.json"
    cat "$AH_HOME"/logs/ahd.log* 2>/dev/null | sed -n 's/.* pid \([0-9]*\) root .*/\1/p'; } | sort -u
}
cleanup() {
  local p
  ahd_stop
  for p in $HOLDERS; do ah_signal_child TERM "$p"; done
  for p in $(daemon_pids); do ah_signal_ours TERM "$p" "$T"; done
  sleep 0.3
  for p in $(daemon_pids); do ah_signal_ours KILL "$p" "$T"; done
  local left; left=$(pgrep -f "$T" 2>/dev/null | grep -v "^$$\$" | tr '\n' ' ')
  [ -n "${left// /}" ] && echo "FAIL: processes still alive under $T: $left"
  [ -n "$AH_TEST_KEEP" ] && return
  case "$T" in /tmp/ahprobe.??????|/private/tmp/ahprobe.??????) rm -rf "$T";; esac
}
hermetic_on_exit cleanup
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:600})"; fi; }
waitfor() { local i; for i in $(seq 1 "$2"); do eval "$1" && return 0; sleep 0.2; done; return 1; }
view() { OUT=$(curl -s --max-time 10 --unix-socket "$AH_HOME/run/ahd.sock" 'http://x/v1/view?path=/' 2>&1); }

# holder <repo> <session> <gate file or ->: starts a long-lived process standing in for the Claude process. The hooks run as its
# children, so their recorded parent pid is the holder's pid ($HOLDER). With a gate file, the prompt hook waits for that file.
HOLDERS=""
holder() {
  local repo=$1 session=$2 gate=$3
  HK="$HK" REPO="$repo" SESSION="$session" GATE="$gate" DONE="$T/done-$session" sh -c '
    printf "{\"session_id\":\"%s\",\"cwd\":\"%s\",\"source\":\"startup\",\"hook_event_name\":\"SessionStart\"}" "$SESSION" "$REPO" | sh "$HK" sessionstart >/dev/null 2>&1
    if [ "$GATE" != - ]; then while [ ! -e "$GATE" ]; do sleep 0.1; done; fi
    printf "{\"session_id\":\"%s\",\"cwd\":\"%s\",\"hook_event_name\":\"UserPromptSubmit\",\"prompt\":\"hi\"}" "$SESSION" "$REPO" | sh "$HK" activity >/dev/null 2>&1
    : > "$DONE"
    exec sleep 600' &
  HOLDER=$!
  HOLDERS="$HOLDERS $HOLDER"
}

sh "$AHD" start >/dev/null 2>&1
check "the sandbox daemon is running" 'waitfor "[ -n \"\$(daemon_pids)\" ]" 50'

holder "$ENABLED" sess-enabled -
H_ENABLED=$HOLDER
holder "$OFF" sess-off -
H_OFF=$HOLDER
waitfor '[ -e "$T/done-sess-enabled" ] && [ -e "$T/done-sess-off" ]' 100
# ah2-34: a solo Orchestrator gets an implicit team named after its folder, so the row is '<repo> · pid N' with one team
check "T1 an enabled repo's solo session is listed as '<repo> · pid N' with its implicit team" \
  'waitfor "view; printf %s \"\$OUT\" | grep -q \"\\\"label\\\":\\\"enabled-repo · pid $H_ENABLED\\\"[^}]*\\\"summary\\\":{\\\"teams\\\":1,\"" 50'
check "T2 a repo with enabled:false lists no session" '! printf %s "$OUT" | grep -q "pid $H_OFF"'
HOME="$T/barehome" holder "$ENABLED" sess-bare -
H_BARE=$HOLDER
waitfor '[ -e "$T/done-sess-bare" ]' 100
view
check "T1b negative: an enabled repo's session that nothing identifies is not listed" '! printf %s "$OUT" | grep -q "pid $H_BARE"'

GATE="$T/go-later"
holder "$LATER" sess-later "$GATE"
H_LATER=$HOLDER
sleep 1
view
check "T3 a session that started before the repo was enabled is not listed" '! printf %s "$OUT" | grep -q "pid $H_LATER"'
printf '{"enabled":true}\n' > "$LATER/.claude/ah2/agent-hierarchy.json"
: > "$GATE"
waitfor '[ -e "$T/done-sess-later" ]' 100
check "T4 once the repo is enabled its session is listed after its next prompt" \
  'waitfor "view; printf %s \"\$OUT\" | grep -q \"\\\"label\\\":\\\"later-repo · pid $H_LATER\\\"\"" 50'

ahd_stop
waitfor '[ -z "$(for p in $(daemon_pids); do kill -0 $p 2>/dev/null && echo $p; done)" ]' 50
check "T5 the sandbox daemon is gone" '[ -z "$(for p in $(daemon_pids); do kill -0 $p 2>/dev/null && echo $p; done)" ]'

echo "tui-no-team: $PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
