#!/bin/bash
# agent-hierarchy — ah2 and ah v1 do not share state (spec 0118 §7): in a sandbox whose HOME holds planted ah v1 files, a session
# start, a message-file command, a gate, the usage report and the usage collector put everything they write under ah2's
# directories and neither read nor change a v1 file. The variant runs the same with HOME itself a git repository (the project
# directory is then ~/.claude/ah2/project). `ahd home trust` refuses v1's home by path and through a link. Every run is in a
# /private/tmp/ahprobe.* sandbox; the binary is the native one (AH_BIN, else AHD_NATIVE_BIN). Skipped (exit 0) without one.
# Usage: bash tests/test-ah2-v1-isolation.sh   (exits 0 iff all rows pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE CLAUDE_PID CLAUDE_CODE_SESSION_ID HERDR_PANE_ID TMUX_PANE AH_IMPL AH_DISPATCHED AH_PLUGIN_ROOT AH_HIERARCHY_DIR AH_SPAWN_DEPTH CLAUDECODE
BIN=${AH_BIN:-${AHD_NATIVE_BIN:-}}
if [ -z "$BIN" ] || [ ! -x "$BIN" ]; then echo "SKIP: no native binary (AH_BIN / AHD_NATIVE_BIN)"; exit 0; fi
T=$(mktemp -d /private/tmp/ahprobe.XXXXXX)
case "$T" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting" >&2; exit 1;; esac
T=$(cd "$T" && pwd -P)
hermetic_on_exit 'rm -rf "$T"'
export AH_BIN="$BIN" AH_TEST=1
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name"; fi
}

# plant <home>: ah v1's files under <home>/.claude, each with a body no ah2 code could produce.
plant() {
  local c="$1/.claude"
  mkdir -p "$c/hierarchy/scratch" "$c/agent-hierarchy/run" "$c/agent-hierarchy.state"
  printf '%s\n' '{"ts":"2026-01-01T00:00:00.000Z","session_id":"v1-planted","agent_type":"ah:task-runner","input_tokens":1}' > "$c/agent-hierarchy.usage.jsonl"
  printf '%s\n' '{"version":1,"roster":{"v1-planted":true}}' > "$c/agent-hierarchy.json"
  printf '%s\n' '{"version":1,"decisions":{"v1-planted":"ask"}}' > "$c/agent-hierarchy.gate.json"
  printf '%s\n' '{"v1-planted":"orchestrator"}' > "$c/agent-hierarchy.session-roles.json"
  printf '%s\n' 'v1-planted' > "$c/hierarchy/hook-errors.jsonl"
  printf '%s\n' 'v1-planted' > "$c/hierarchy/scratch/team.json"
  printf '%s\n' 'v1-planted' > "$c/agent-hierarchy/trusted-homes"
  printf '%s\n' 'v1-planted' > "$c/agent-hierarchy/run/ahd.json"
  printf '%s\n' 'v1-planted' > "$c/agent-hierarchy.state/marker"
}

# snapshot <dir> <file>: every path under <dir> (directories too) and a checksum of every file, sorted.
snapshot() {
  { (cd "$1" && find . -print | sort); (cd "$1" && find . -type f -print0 | sort -z | xargs -0 cksum 2>/dev/null); } > "$2"
}

# run_all <home> <cwd>: a session start, a message-file command, a gate, the usage report and the usage collector.
run_all() {
  local home=$1 cwd=$2
  export HOME="$home" AH_HOME="$home/.claude/ah2"
  printf '{"session_id":"s-iso","cwd":"%s","source":"startup","hook_event_name":"SessionStart"}' "$cwd" | sh "$PLUGIN/bin/ah-hook" sessionstart >/dev/null 2>&1
  (cd "$cwd" && sh "$PLUGIN/bin/ah" msg new --to implementor --from orchestrator --slug iso --title "Fix test titles" >/dev/null 2>&1)
  (cd "$cwd" && sh "$PLUGIN/bin/ah" msg list --all >/dev/null 2>&1)
  printf '{"session_id":"s-iso","cwd":"%s","hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"ls"}}' "$cwd" | sh "$PLUGIN/bin/ah-hook" pretooluse-chain >/dev/null 2>&1
  printf '{"session_id":"s-iso","cwd":"%s","hook_event_name":"SubagentStop","agent_type":"wrangl:task-runner"}' "$cwd" | sh "$PLUGIN/bin/ah-hook" subagentstop-usage >/dev/null 2>&1
  (cd "$cwd" && sh "$PLUGIN/bin/ah" usage-report >/dev/null 2>&1)
}

# outside_ah2 <snapshot file>: the lines of a snapshot that are not under ./ah2 (the .claude directory itself and ah2 aside).
outside_ah2() { grep -v -E '^\./ah2(/|$)|^[0-9]+ [0-9]+ \./ah2/' "$1"; }

# ---- 1. HOME outside any repository, the working directory a repository elsewhere
H="$T/h1"; PROJ="$T/proj1"
mkdir -p "$H" "$PROJ"; git -C "$PROJ" init -q
plant "$H"
mkdir -p "$PROJ/.claude/hierarchy" && printf '%s\n' 'v1-planted' > "$PROJ/.claude/hierarchy/team.json"
printf '%s\n' '{"version":1}' > "$PROJ/.claude/agent-hierarchy.json"
snapshot "$H/.claude" "$T/h1.before"; snapshot "$PROJ/.claude" "$T/p1.before"
run_all "$H" "$PROJ"
snapshot "$H/.claude" "$T/h1.after"; snapshot "$PROJ/.claude" "$T/p1.after"
check "ah2 wrote something under ~/.claude/ah2 (the run did run)" '[ -d "$H/.claude/ah2" ] && [ -n "$(ls -A "$H/.claude/ah2" 2>/dev/null)" ]'
check "the state directory did not exist before, and the usage cache is persisted in it" '! grep -q "ah2" "$T/h1.before" && [ -s "$H/.claude/ah2/agent-hierarchy.usage-cache.json" ]'
check "nothing under ~/.claude outside ah2 changed or appeared" 'diff <(outside_ah2 "$T/h1.before") <(outside_ah2 "$T/h1.after") >/dev/null'
check "the planted v1 usage file is byte-identical" 'grep -q "v1-planted" "$H/.claude/agent-hierarchy.usage.jsonl" && [ "$(wc -l < "$H/.claude/agent-hierarchy.usage.jsonl" | tr -d " ")" = 1 ]'
check "the planted v1 config, gate, session-roles and hook-error files are byte-identical" 'diff <(outside_ah2 "$T/h1.before" | grep -E "agent-hierarchy\.(json|gate|session)|hook-errors") <(outside_ah2 "$T/h1.after" | grep -E "agent-hierarchy\.(json|gate|session)|hook-errors") >/dev/null'
check "the planted v1 home and v1 project directory are byte-identical" 'diff <(grep -E "agent-hierarchy/|agent-hierarchy\.state|hierarchy/scratch" "$T/h1.before") <(grep -E "agent-hierarchy/|agent-hierarchy\.state|hierarchy/scratch" "$T/h1.after") >/dev/null'
check "the repository's .claude outside ah2 is unchanged" 'diff <(outside_ah2 "$T/p1.before") <(outside_ah2 "$T/p1.after") >/dev/null'
check "the repository's state is under <repo>/.claude/ah2, not <repo>/.claude/hierarchy" '[ ! -e "$PROJ/.claude/hierarchy/peers.jsonl" ] && [ ! -e "$PROJ/.claude/hierarchy/msgs" ]'
check "the v1 usage file was not read: the report counts none of its rows" '! (cd "$PROJ" && HOME="$H" AH_HOME="$H/.claude/ah2" sh "$PLUGIN/bin/ah" usage-report 2>&1) | grep -q "v1-planted"'

cp "$T/h1.after" "$T/h1.mut"; printf '%s\n' '0 0 ./agent-hierarchy.usage.jsonl' './hierarchy/peers.jsonl' >> "$T/h1.mut"
check "control: the comparison sees a v1 file that changed and a file that appeared outside ah2" '! diff <(outside_ah2 "$T/h1.before") <(outside_ah2 "$T/h1.mut") >/dev/null'

# ---- 2. HOME is itself a git repository: the project directory is ~/.claude/ah2/project
H="$T/h2"
mkdir -p "$H"; git -C "$H" init -q
plant "$H"
snapshot "$H/.claude" "$T/h2.before"
run_all "$H" "$H"
snapshot "$H/.claude" "$T/h2.after"
check "a home repository: nothing under ~/.claude outside ah2 changed or appeared" 'diff <(outside_ah2 "$T/h2.before") <(outside_ah2 "$T/h2.after") >/dev/null'
check "a home repository: the planted v1 files are byte-identical" 'diff <(grep -v "ah2" "$T/h2.before") <(grep -v "ah2" "$T/h2.after") >/dev/null'
check "a home repository: nothing sits directly under ~/.claude/ah2/ that is a project entry (they are under project/)" '[ ! -e "$H/.claude/ah2/teams" ] && [ ! -e "$H/.claude/ah2/msgs" ]'

# ---- 3. `ahd home trust` refuses v1's home by path, under it, and through a link
H="$T/h3"; mkdir -p "$H"; plant "$H"
export HOME="$H" AH_HOME="$H/.claude/ah2"; mkdir -p "$AH_HOME"; chmod 700 "$AH_HOME"
ln -s "$H/.claude/agent-hierarchy" "$T/link-to-v1"
ln -s "$H/.claude/agent-hierarchy/run" "$T/link-to-v1-under"
mkdir -p "$T/not-v1"; chmod 700 "$T/not-v1"
if command -v script >/dev/null 2>&1; then
  trust() { OUT=$(script -q /dev/null sh "$PLUGIN/bin/ahd" home trust "$1" 2>&1 </dev/null); RC=$?; }
  trust "$H/.claude/agent-hierarchy"
  check "trust refuses v1's home by path and writes nothing" 'echo "$OUT" | grep -q "ah v1.s home" && [ ! -e "$AH_HOME/trusted-homes" ]'
  trust "$H/.claude/agent-hierarchy/run"
  check "trust refuses a directory under v1's home" 'echo "$OUT" | grep -q "ah v1.s home" && [ ! -e "$AH_HOME/trusted-homes" ]'
  trust "$T/link-to-v1"
  check "trust refuses a link to v1's home" 'echo "$OUT" | grep -q "ah v1.s home" && [ ! -e "$AH_HOME/trusted-homes" ]'
  trust "$T/link-to-v1-under"
  check "trust refuses a link to a directory under v1's home" 'echo "$OUT" | grep -q "ah v1.s home" && [ ! -e "$AH_HOME/trusted-homes" ]'
  trust "$T/not-v1"
  check "trust accepts an ordinary directory (the refusals above are about v1's home)" '[ -f "$AH_HOME/trusted-homes" ] && grep -q "not-v1" "$AH_HOME/trusted-homes"'
else
  echo "SKIP: no script(1) to give the trust verb a terminal"
fi

echo "$PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
