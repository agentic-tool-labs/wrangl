#!/bin/bash
# agent-hierarchy — spawning a codex member that opted into the app-server adapter (roster.mjs add/edit --adapter,
# spawn-one, close): the version gate, the member's own detached server, the --remote attach, the team row, and the
# sandbox contract on the launch arguments. A stub `codex` (version text, and a fake app-server for `app-server
# --listen`) and a compact fake herdr stand in for the real ones; nothing real is ever started.
# Usage: bash tests/test-codex-appserver-spawn.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
H="$PLUGIN/hooks"
# A Unix socket path is limited to about 104 bytes on macOS, so the sandbox lives at a short real path.
# SPAWN_TEST_SANDBOX: a sandbox directory the caller made and names (the kill suite does, so it never has to read the path out of a
# process listing); it is removed at exit like one made here.
SANDBOX="${SPAWN_TEST_SANDBOX:-$(mktemp -d /private/tmp/ahprobe.XXXXXX)}"
[ -n "$SANDBOX" ] && [ -d "$SANDBOX" ] || { echo "mktemp failed"; exit 1; }
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
case "$SANDBOX" in /private/tmp/ahprobe.?*) ;; *) echo "refusing sandbox $SANDBOX"; exit 1 ;; esac
# Everything this run starts names its sandbox: the fake app-servers the launches spawn detached, and the helpers of a case. The
# cleanup stops those and nothing else, whether the run ends, fails or is terminated (a termination signal runs it too, once the
# command in flight returns).
cleanup() {
  hermetic_on_exit -
  for p in $(cat "$SANDBOX"/servers.pid 2>/dev/null); do stop_ours "$p"; done
  for p in $(pgrep -f -- "$SANDBOX/" 2>/dev/null); do [ "$p" = "$$" ] || ah_signal_ours TERM "$p" "$SANDBOX/"; done
  for p in $(jobs -p 2>/dev/null); do ah_signal_child TERM "$p"; done
  # what was signalled needs a moment to exit and stop writing; removing the sandbox under it would leave files behind
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
    [ -z "$(pgrep -f -- "$SANDBOX/" 2>/dev/null | grep -v "^$$\$")" ] && break
    sleep 0.25
  done
  # a killed process is gone a moment later, and one a stopped process started in its last moments is seen only by a later pass
  for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
    [ -z "$(pgrep -f -- "$SANDBOX/" 2>/dev/null | grep -v "^$$\$")" ] && break
    for p in $(pgrep -f -- "$SANDBOX/" 2>/dev/null); do [ "$p" = "$$" ] || ah_signal_ours KILL "$p" "$SANDBOX/"; done
    sleep 0.25
  done
  rm -rf "$SANDBOX" 2>/dev/null
  if [ -e "$SANDBOX" ]; then sleep 0.5; rm -rf "$SANDBOX"; fi
}
# a recorded server is signalled only while its command line still names this sandbox: a number that outlived its process can be
# another process's by now
stop_ours() { ah_signal_ours TERM "$1" "$SANDBOX/"; }
hermetic_on_exit cleanup
unset HERDR_ENV HERDR_PANE_ID TMUX_PANE TMUX WRANGL_TEAM_FILE CLAUDE_PID AH_HOME AH_DAEMON
FAKEHOME="$SANDBOX/home"; PROJ="$SANDBOX/myrepo"; BIN="$SANDBOX/bin"; STATE="$SANDBOX/state"
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2" "$BIN" "$STATE"; export AH_TEST_FAKE_BIN="$BIN${AH_TEST_FAKE_BIN:+:$AH_TEST_FAKE_BIN}"
(cd "$PROJ" && git init -q)
NODE_BIN="$(command -v node)"
NODE_DIR="$(dirname "$NODE_BIN")"
TEAM_FILE="$PROJ/.claude/ah2/teams/$(basename "$PROJ").json"
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC adapter_note=$(field "${ROW:-}" adapter_note) appserver_pid=$(field "${ROW:-}" appserver_pid) OUT=${OUT:0:6000})"; fi
}

# A compact fake herdr: a one-pane layout, agent start (logged), pane close (logged).
printf '#!%s\n' "$NODE_BIN" > "$BIN/herdr"
cat >> "$BIN/herdr" <<'EOF'
const fs = require("fs");
const a = process.argv.slice(2);
const st = process.env.FAKE_STATE_DIR;
const log = (o) => fs.appendFileSync(st + "/herdr.log", JSON.stringify(o) + "\n");
if (a[0] === "pane" && a[1] === "layout") {
  console.log(JSON.stringify({ result: { layout: { area: { width: 180, height: 42, x: 0, y: 0 }, focused_pane_id: "p0", panes: [{ focused: true, pane_id: "p0", rect: { width: 180, height: 42, x: 0, y: 0 } }], splits: [], tab_id: "t1", workspace_id: "w1", zoomed: false } } }));
  process.exit(0);
}
if (a[0] === "agent" && a[1] === "start") {
  log({ argv: a });
  if (process.env.FAKE_HERDR_FAIL_RESUME && a.includes("resume")) { console.log(JSON.stringify({ error: { code: "start_failed", message: "no" } })); process.exit(1); }
  console.log(JSON.stringify({ result: { pane: { pane_id: "target" }, agent: { name: a[2], ready: true } } }));
  process.exit(0);
}
if (a[0] === "pane" && a[1] === "close") { log({ argv: a }); console.log("{}"); process.exit(0); }
if (a[0] === "pane" && a[1] === "split") { console.log(JSON.stringify({ result: { pane: { pane_id: "p1" } } })); process.exit(0); }
if (a[0] === "agent" && a[1] === "get") { console.log(JSON.stringify({ error: { code: "agent_not_found" } })); process.exit(1); }
if (a[0] === "agent" && a[1] === "read") { console.log(JSON.stringify({ result: { read: { text: "› " } } })); process.exit(0); }
process.stderr.write("fake herdr: unhandled " + JSON.stringify(a) + "\n");
process.exit(1);
EOF
chmod +x "$BIN/herdr"

# A stub codex: `--version` prints FAKE_CODEX_VERSION; `app-server --listen unix://P` becomes the fake app-server (its
# command line keeps the word app-server, as the real one's does) unless FAKE_CODEX_NOSERVER is set.
cat > "$BIN/codex" <<EOF
#!/bin/sh
if [ "\$1" = --version ]; then echo x >> "$SANDBOX/version.calls"; echo "codex-cli \${FAKE_CODEX_VERSION:-0.161.0}"; exit 0; fi
server=; sock=
for a in "\$@"; do
  case "\$a" in
    app-server) server=1 ;;
    unix://*) sock=\${a#unix://} ;;
  esac
done
[ -n "\$server" ] || exit 1
[ -n "\$FAKE_CODEX_NOSERVER" ] && exit 0
printf '%s\n' "\$*" >> "$SANDBOX/server.args"
env > "$SANDBOX/server.env"
echo \$\$ >> "$SANDBOX/servers.pid"
opts=\${FAKE_CODEX_OPTS:-}; [ -n "\$opts" ] || opts='{}'
exec $NODE_BIN "$PLUGIN/tests/fake-codex-appserver.mjs" "\$sock" "$STATE/server.log" "\$opts" "\$@"
EOF
chmod +x "$BIN/codex"

export AH_HOME="$SANDBOX/ah"
# the fake herdr never reports a ready agent, and spawn would otherwise wait out its 15 s ready wait and a 2 s screen re-read for every launch
export AH_HERDR_READY_WAIT_MS="${AH_HERDR_READY_WAIT_MS:-0}" AH_COMPOSER_REREAD_MS="${AH_COMPOSER_REREAD_MS:-0}"
# RO_AS=1 runs the command as the Orchestrator of the repo's team, proven in a subshell, which an owner verb such as spawn-one requires
export AH_TEST_RUNS_HOOKS=1
PROOF="$SANDBOX/proof/teams/$(basename "$PROJ").json"
ah_test_team "$SANDBOX/proof" "$(basename "$PROJ")"
ro_env() { env HOME="$FAKEHOME" AH_HOME="$AH_HOME" AH_TEST=1 PATH="$BIN:$NODE_DIR:/usr/bin:/bin" FAKE_STATE_DIR="$STATE" "$@" 2>&1; }
ro() {
  local as=(); [ -n "$RO_AS" ] && as=(ah_test_run_as orchestrator "$(basename "$PROJ")" "$PROOF")
  OUT=$("${as[@]}" ro_env "$@"); RC=$?
}
rost() { ro "$PLUGIN/bin/ah" roster "$@" --cwd "$PROJ"; }
spawn1() { # <extra env...> -- <member>
  local envs=()
  while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  RO_AS=1 ro env HERDR_ENV=1 HERDR_PANE_ID=p0 "${envs[@]}" "$PLUGIN/bin/ah" roster spawn-one "$@" --split --cwd "$PROJ"
}
# the proof team lives in the store under AH_HOME, so a reset that empties the home stores it again
reset() { drop_project_state "$PROJ"; rm -rf "$STATE" "$AH_HOME"; mkdir -p "$STATE"; ah_test_team "$SANDBOX/proof" "$(basename "$PROOF" .json)"; for p in $(cat "$SANDBOX"/servers.pid 2>/dev/null); do stop_ours "$p"; done; : > "$SANDBOX/servers.pid"; }
row() { ah_team_query "$TEAM_FILE" 'JSON.stringify(t.members.find(x=>x.role==="'"$1"'")??"")' | sed 's/^""$//'; }
field() { node -e 'const m=JSON.parse(process.argv[1]||"{}");const v=m[process.argv[2]];console.log(v===undefined?"(absent)":String(v))' "$1" "$2"; }
start_line() { tail -1 <(grep '"start"' "$STATE/herdr.log"); }

# a codex member's native legwork children need a codex model declared at the haiku tier
rost tier set codex gpt-5 haiku >/dev/null

# ---- the flag: add / edit validation
reset
rost init --level repo --route pane >/dev/null
rost add --no-spawn --level repo --role implementor --kind codex --route pane --model gpt-5 --adapter appserver
check "add: --adapter appserver on a codex member is stored in the roster" '[ "$RC" -eq 0 ] && grep -q "\"adapter\": *\"appserver\"" "$PROJ/.claude/ah2/agent-hierarchy.json"'
rost add --no-spawn --level repo --role reviewer --kind codex --route pane --adapter nonsense
check "add: any other adapter value is a usage error and writes nothing" '[ "$RC" -ne 0 ] && ! grep -q nonsense "$PROJ/.claude/ah2/agent-hierarchy.json"'
rost add --no-spawn --level repo --role architect --route pane --adapter appserver
check "add: --adapter on a claude member is a usage error" '[ "$RC" -ne 0 ] && echo "$OUT" | grep -q "applies only to kind codex"'
rost add --no-spawn --level repo --role architect --route pane --adapter off
check "add: --adapter off on a claude member is a usage error too, and writes nothing" '[ "$RC" -ne 0 ] && echo "$OUT" | grep -q "applies only to kind codex" && ! grep -q "\"off\"" "$PROJ/.claude/ah2/agent-hierarchy.json"'
NAME=$(node -e 'const j=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));const find=(o)=>Array.isArray(o)?o.flatMap(find):o&&typeof o==="object"?[...(o.role==="implementor"&&o.kind==="codex"?[o]:[]),...Object.values(o).flatMap(find)]:[];console.log(find(j).length)' "$PROJ/.claude/ah2/agent-hierarchy.json")
check "add: only the valid codex member was added (the two bad adds wrote nothing)" '[ "$NAME" = 1 ]'

drop_team() { drop_project_state "$PROJ"; rm -rf "$STATE"; mkdir -p "$STATE"; for p in $(cat "$SANDBOX"/servers.pid 2>/dev/null); do stop_ours "$p"; done; : > "$SANDBOX/servers.pid"; : > "$SANDBOX/server.args"; rm -rf "$AH_HOME"; ah_test_team "$SANDBOX/proof" "$(basename "$PROOF" .json)"; }
IN_RANGE="AH_CODEX_APPSERVER_RANGE=0.160.0,0.163.0"
NO_RANGE="AH_CODEX_APPSERVER_RANGE=none"
NOSOCK='! ls "$AH_HOME"/run/codex/*.sock >/dev/null 2>&1'

# ---- no tested range at all: every version launches plain, the row says why
drop_team
spawn1 "$NO_RANGE" -- implementor
ROW=$(row implementor)
check "empty range: spawn succeeds, launched plain (no --remote), no server socket" '[ "$RC" -eq 0 ] && ! start_line | grep -q -- "--remote" && eval "$NOSOCK"'
check "empty range: the row has no adapter and says why" '[ "$(field "$ROW" adapter)" = "(absent)" ] && field "$ROW" adapter_note | grep -q "outside the tested app-server range (none yet); launched without the app-server adapter"'
check "empty range: the spawn output carries the note as a warning" 'echo "$OUT" | grep -q "outside the tested app-server range"'

# ---- in range: own server, attach, row
drop_team
spawn1 "$IN_RANGE" -- implementor
ROW=$(row implementor); NAMEM=$(field "$ROW" name); SOCK="$AH_HOME/run/codex/myrepo-$NAMEM.sock"; PID=$(field "$ROW" appserver_pid)
check "in range: spawn succeeds and herdr was told to resume the created thread attached to the member's socket" '[ "$RC" -eq 0 ] && start_line | grep -q -- "\"resume\",\"th-[0-9]*\",\"--remote\",\"unix://$SOCK\""'
check "in range: the terminal argv is exactly resume <id> --remote <sock>: no sandbox, approval, add-dir, -c or model" '! start_line | grep -qE -- "--sandbox|--ask-for-approval|--add-dir|\"-c\"|--model|-m\""'
check "in range: the row records the thread id the spawn created" 'field "$ROW" appserver_thread_id | grep -q "^th-[0-9]*$"'
check "in range: thread/start had cwd only, then the warm-up text, then thread/resume (the server's log)" '[ "$(grep -o "\"method\":\"[a-z/]*\"" "$STATE/server.log" | grep -v initialize | head -3 | tr "\n" " ")" = "\"method\":\"thread/start\" \"method\":\"turn/start\" \"method\":\"thread/resume\" " ] && grep "\"thread/start\"" "$STATE/server.log" | head -1 | grep -q "\"params\":{\"cwd\":\"[^\"]*\"}" && grep "\"turn/start\"" "$STATE/server.log" | head -1 | grep -q "Reply with the single word READY and nothing else. Do not run any tools."'
check "in range: the row says adapter appserver with a live appserver_pid, and no note" '[ "$(field "$ROW" adapter)" = appserver ] && kill -0 "$PID" 2>/dev/null && [ "$(field "$ROW" adapter_note)" = "(absent)" ]'
check "in range: the server socket exists in a 0700 directory" '[ -e "$SOCK" ] && [ "$(stat -f %Lp "$(dirname "$SOCK")")" = 700 ]'
check "in range: the launch carries no --allow-unix-socket and no writable dir under AH_HOME" '! start_line | grep -q -- "--allow-unix-socket" && ! start_line | grep -q -- "--add-dir[^,]*$AH_HOME"'
check "in range: the server outlives the spawn command (it is not a child of it)" 'kill -0 "$PID" 2>/dev/null'

# disband needs a plan first and its token to close live panes.
disband_close() {
  rost disband --ignore-release --team myrepo
  local tok
  tok=$(printf '%s' "$OUT" | grep -o 'close_token": *"[^"]*"' | head -1 | cut -d'"' -f3)
  rost disband --close --confirm --ignore-release --plan-token "$tok" --team myrepo
}

# ---- close kills the member's server by its recorded pid
kill -0 "$PID" 2>/dev/null || echo "setup: the server is not running before the close"
disband_close
check "close: the member's recorded server is stopped" '[ "$RC" -eq 0 ] && sleep 0.5 && ! kill -0 "$PID" 2>/dev/null'

# a recorded pid that is no longer an app-server (reused by another process) is left alone
drop_team
spawn1 "$IN_RANGE" -- implementor
sleep 300 & OTHER=$!
ah_team_doc "$TEAM_FILE" | node -e 'const t=JSON.parse(require("fs").readFileSync(0,"utf8"));t.members[0].appserver_pid=Number(process.argv[1]);console.log(JSON.stringify(t))' "$OTHER" | ah_seed_team "$TEAM_FILE"
disband_close
check "close: a recorded pid that is not an app-server is not signalled (and the close itself went through)" '[ "$RC" -eq 0 ] && kill -0 "$OTHER" 2>/dev/null'
ah_signal_child TERM "$OTHER"

# ---- in range but the server never comes up: plain launch and the row says so
drop_team
spawn1 "$IN_RANGE" FAKE_CODEX_NOSERVER=1 -- implementor
ROW=$(row implementor)
check "no server: launched plain, no adapter on the row, and the note says it did not come up" '[ "$RC" -eq 0 ] && ! start_line | grep -q -- "--remote" && [ "$(field "$ROW" adapter)" = "(absent)" ] && field "$ROW" adapter_note | grep -q "did not come up"'

# ---- every failure of the bring-up: plain launch byte-identical to a plain member's, the note, and the server stopped
drop_team
spawn1 "$NO_RANGE" -- implementor
PLAIN_LINE=$(start_line)
fallback_case() { # <label> <extra env...> -- <text the note must say>
  local label=$1; shift
  local envs=()
  while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  local need=$1
  drop_team
  spawn1 "$IN_RANGE" "${envs[@]}" -- implementor
  ROW=$(row implementor)
  local pid_after
  check "fallback: $label -> plain launch byte-identical to a plain member, the note says it, no adapter on the row" '[ "$RC" -eq 0 ] && [ "$(start_line)" = "$PLAIN_LINE" ] && [ "$(field "$ROW" adapter)" = "(absent)" ] && field "$ROW" adapter_note | grep -q -- "$need" && [ "$(field "$ROW" appserver_thread_id)" = "(absent)" ]'
  check "fallback: $label -> the spawn output warns with the note, and no server of this run is left" 'echo "$OUT" | grep -q -- "$need" && sleep 0.4 && [ -z "$(for p in $(cat "$SANDBOX/servers.pid" 2>/dev/null); do kill -0 "$p" 2>/dev/null && echo "$p"; done)" ]'
}
fallback_case "thread/start fails" "FAKE_CODEX_OPTS={\"threadStartError\":true}" -- "thread/start failed"
fallback_case "the server did not take the pins (the gate)" "FAKE_CODEX_OPTS={\"sandboxType\":\"dangerFullAccess\"}" -- "the app-server did not apply ah's pins"
fallback_case "the warm-up turn errors (not logged in)" "FAKE_CODEX_OPTS={\"turnError\":\"401 not logged in\"}" -- "warm-up turn failed (not-logged-in)"
fallback_case "no rollout after the warm-up" "FAKE_CODEX_OPTS={\"noRollout\":true}" -- "no rollout after warm-up"
fallback_case "the server reports the /tmp exclusion off (the gate, r12)" "FAKE_CODEX_OPTS={\"excludeSlashTmp\":false}" -- "the app-server did not apply ah's pins"
fallback_case "the server reports the TMPDIR exclusion off (the gate, r12)" "FAKE_CODEX_OPTS={\"excludeTmpdirEnvVar\":false}" -- "the app-server did not apply ah's pins"
fallback_case "the resume reports a wider sandbox (the second gate)" "FAKE_CODEX_OPTS={\"resumeSandboxType\":\"dangerFullAccess\"}" -- "the app-server did not apply ah's pins"
fallback_case "the terminal's resume is refused at launch" FAKE_HERDR_FAIL_RESUME=1 -- "codex resume refused"
check "fallback: the server's text and the sentinel never reach the row, the warning or the output" '! echo "$OUT" | grep -q SENTINEL-TOKEN'
drop_team
spawn1 "$IN_RANGE" "FAKE_CODEX_OPTS={\"turnError\":\"boom\",\"sentinel\":\"SENTINEL-TOKEN-9f3a\"}" -- implementor
check "sentinel: neither the sentinel nor the server's error text is in the output, the row or the team file" '[ "$RC" -eq 0 ] && ! echo "$OUT" | grep -q -e SENTINEL -e boom && ! ah_team_doc "$TEAM_FILE" | grep -q -e SENTINEL -e boom'

# ---- the member's temp directory: made by the spawn under the ah home, and its absence is a fallback
drop_team
# both launches run in ONE proven Orchestrator process, so the relaunch is the same team owner's second spawn-one into the existing team
two_spawns() {
  ro_env env HERDR_ENV=1 HERDR_PANE_ID=p0 "$IN_RANGE" "$PLUGIN/bin/ah" roster spawn-one implementor --split --cwd "$PROJ" > "$SANDBOX/first.out"; echo $? > "$SANDBOX/first.rc"
  touch "$AH_HOME/run/codex/tmp/myrepo-$(field "$(row implementor)" name)/keep"
  # a relaunch needs the member shown gone: it is registered under a harness-looking process that has since exited
  ah_test_harness_peer "$(field "$(row implementor)" name)" myrepo
  ah_signal_ours TERM "$AH_TEST_HARNESS_PID" 'claude '; wait "$AH_TEST_HARNESS_PID" 2>/dev/null
  ro_env env HERDR_ENV=1 HERDR_PANE_ID=p0 "$IN_RANGE" "$PLUGIN/bin/ah" roster spawn-one implementor --split --cwd "$PROJ" > "$SANDBOX/second.out"; echo $? > "$SANDBOX/second.rc"
}
ah_test_run_as orchestrator "$(basename "$PROJ")" "$PROOF" two_spawns
OUT=$(cat "$SANDBOX/first.out"); RC=$(cat "$SANDBOX/first.rc")
MTMP="$AH_HOME/run/codex/tmp/myrepo-$(field "$(row implementor)" name)"
check "temp dir: the spawn made <ah home>/run/codex/tmp/<team>-<name> with mode 0700, and it is the server's TMPDIR and first writable root" '[ "$RC" -eq 0 ] && [ -d "$MTMP" ] && [ "$(stat -f %Lp "$MTMP")" = 700 ] && tail -1 "$SANDBOX/server.args" | grep -qF -- "writable_roots=[\"$MTMP\"" && tail -1 "$SANDBOX/server.args" | grep -qF -- "shell_environment_policy.set.TMPDIR=\"$MTMP\""'
check "temp dir: the system temp exclusions are on the server argv" 'tail -1 "$SANDBOX/server.args" | grep -q "exclude_slash_tmp=true" && tail -1 "$SANDBOX/server.args" | grep -q "exclude_tmpdir_env_var=true"'
check "temp dir: it is not on the terminal's argv (the terminal carries no pin)" '! start_line | grep -q "run/codex/tmp"'
OUT=$(cat "$SANDBOX/second.out"); RC=$(cat "$SANDBOX/second.rc")
check "temp dir: a relaunch reuses it and removes nothing in it (nearest non-match of a fresh directory)" '[ "$RC" -eq 0 ] && [ -f "$MTMP/keep" ]'
drop_team
spawn1 "$NO_RANGE" -- implementor
PLAIN_LINE=$(start_line)
drop_team
mkdir -p "$AH_HOME/run/codex"
: > "$AH_HOME/run/codex/tmp"
spawn1 "$IN_RANGE" -- implementor
ROW=$(row implementor)
check "temp dir: one that cannot be made is a fallback: plain launch byte-identical to a plain member's, the note says so, no server" '[ "$RC" -eq 0 ] && [ "$(start_line)" = "$PLAIN_LINE" ] && [ "$(field "$ROW" adapter_note)" = "member temp dir unavailable; launched without the app-server adapter" ] && [ "$(field "$ROW" appserver_pid)" = "(absent)" ] && eval "$NOSOCK"'

# ---- a codex outside the range: plain, note names the range
drop_team
spawn1 "$IN_RANGE" FAKE_CODEX_VERSION=0.163.0 -- implementor
ROW=$(row implementor)
check "version just above the range: plain launch, the note names the range" '[ "$RC" -eq 0 ] && ! start_line | grep -q -- "--remote" && field "$ROW" adapter_note | grep -q ">=0.160.0 <0.163.0"'
drop_team
spawn1 "$IN_RANGE" FAKE_CODEX_VERSION=0.159.9 -- implementor
check "version just below the range: plain launch" '[ "$RC" -eq 0 ] && ! start_line | grep -q -- "--remote"'

# ---- the range override only exists in a test sandbox
drop_team
# ---- the shipped range: exactly one version, with an exclusive top
drop_team
spawn1 FAKE_CODEX_VERSION=0.162.0-alpha.2 -- implementor
ROW=$(row implementor)
APID=$(field "$ROW" appserver_pid); NOTE=$(field "$ROW" adapter_note); WHY=""
[ "$RC" -eq 0 ] || WHY="$WHY spawn exited $RC;"
start_line | grep -q -- "--remote" || WHY="$WHY no --remote on the start line;"
kill -0 "$APID" 2>/dev/null || WHY="$WHY appserver_pid is not alive;"
[ "$NOTE" = "(absent)" ] || WHY="$WHY adapter_note is set;"
[ -z "$WHY" ] || OUT="failed:$WHY appserver_pid=$APID adapter_note=$NOTE
$OUT"
check "shipped range: codex 0.162.0-alpha.2 with no setting gets the adapter by default, no note" '[ -z "$WHY" ]'
for v in 0.162.0-alpha.1 0.162.0-alpha.3 0.162.0 0.163.0; do
  drop_team
  spawn1 FAKE_CODEX_VERSION=$v -- implementor
  ROW=$(row implementor)
  check "shipped range: codex $v is plain, byte-identical, with the note naming the one tested version" '[ "$RC" -eq 0 ] && ! start_line | grep -q -- "--remote" && [ "$(field "$ROW" adapter)" = "(absent)" ] && field "$ROW" adapter_note | grep -qF -- "codex $v is outside the tested app-server range >=0.162.0-alpha.2 <0.162.0-alpha.3; launched without the app-server adapter (mail is pasted into its pane)"'
done
# Outside a test sandbox the range override is not read at all: the shipped range holds. A run without AH_TEST is refused here
# (an untrusted AH_HOME), so that rule is held by Ah.Codex.Tests/RangeOverrideEdgeTests.TheOverrideIsIgnoredOutsideATestSandbox.

# ---- the server's environment: the caller's session identity, test settings and pane are not passed on; what codex needs is
drop_team
rm -f "$SANDBOX/server.env"
spawn1 "$IN_RANGE" CLAUDECODE=1 CLAUDE_CODE_SESSION_ID=caller-session CLAUDE_CODE_ENTRYPOINT=cli TMUX_PANE=%9 AH_SPAWN_DEPTH=2 AHD_IDLE_EXIT_MS=1 OPENAI_API_KEY=sk-env-probe LANG=C -- implementor
ROW=$(row implementor)
check "server env: the spawn still comes up (server running, adapter on the row)" '[ "$RC" -eq 0 ] && [ "$(field "$ROW" adapter)" = appserver ] && kill -0 "$(field "$ROW" appserver_pid)" 2>/dev/null'
check "server env: the caller's session markers, test settings and pane are absent" '[ -s "$SANDBOX/server.env" ] && ! grep -qE "^(CLAUDECODE|CLAUDE_CODE_[A-Z_]*|CLAUDE_PID|HERDR_PANE_ID|HERDR_ENV|TMUX_PANE|TMUX|AH_TEST[A-Z_]*|WRANGL_TEAM_FILE|AH_ADDRESS|AH_SPAWN_DEPTH|AH_DAEMON|AHD_[A-Z_]*|AH_CODEX_APPSERVER_RANGE|AH_HERDR_[A-Z_]*)=" "$SANDBOX/server.env"'
check "server env: PATH, HOME, locale, credentials and the state home are passed through" 'grep -q "^PATH=" "$SANDBOX/server.env" && grep -q "^HOME=$FAKEHOME$" "$SANDBOX/server.env" && grep -q "^LANG=C$" "$SANDBOX/server.env" && grep -q "^OPENAI_API_KEY=sk-env-probe$" "$SANDBOX/server.env" && grep -q "^AH_HOME=$AH_HOME$" "$SANDBOX/server.env"'

# ---- the sandbox contract: a launch with no sandbox, or one that grants a socket or ah's directory, is refused the adapter
violation_case() { # <label> <edit flag> <value> <text the note must name>
  drop_team
  rost edit --member myrepo-implementor "$2" "$3" >/dev/null
  spawn1 "$IN_RANGE" -- implementor
  ROW=$(row implementor)
  local need=$4
  check "contract: $1 -> plain launch, no server, no adapter on the row, and the note names it" '[ "$RC" -eq 0 ] && ! start_line | grep -q -- "--remote" && eval "$NOSOCK" && [ "$(field "$ROW" adapter)" = "(absent)" ] && field "$ROW" adapter_note | grep -q -- "$need"'
}
violation_case "auto mode bypassPermissions (the bypass flag)" --auto-mode bypassPermissions "--dangerously-bypass-approvals-and-sandbox"
rost edit --member myrepo-implementor --auto-mode manual >/dev/null
violation_case "--sandbox danger-full-access (the value is not echoed)" --args '["--sandbox","danger-full-access"]' "--sandbox (bad value)"
violation_case "a sandbox_mode override" --args '["-c","sandbox_mode=workspace-write"]' "-c sandbox_mode"
violation_case "network_access=true" --args '["-c","sandbox_workspace_write.network_access=true"]' "-c sandbox_workspace_write.network_access"
violation_case "--profile" --args '["--profile","wide"]' "allow-list: --profile; launched"
violation_case "--allow-unix-socket" --args '["--allow-unix-socket","/tmp/x.sock"]' "--allow-unix-socket"
violation_case "a writable dir under AH_HOME" --args "[\"--add-dir\",\"$AH_HOME/run\"]" "--add-dir (bad value)"
violation_case "an unknown flag" --args '["--full-auto"]' "--full-auto"
violation_case "a positional argument" --args '["do the thing"]' "positional argument"
violation_case "a second --sandbox, even with the same value" --args '["--sandbox","read-only"]' "--sandbox (repeated)"
violation_case "a -c key outside the list, with a secret value" --args '["-c","foo_token=SECRETVALUE"]' "-c foo_token"
check "contract: the secret value is not in the note, the adapter_note field or the warning line" '! echo "$OUT" | grep "app-server adapter was not used" | grep -q SECRETVALUE && ! field "$(row implementor)" adapter_note | grep -q SECRETVALUE'
violation_case "a short flag with a secret value attached" --args '["-sSECRETVALUE"]' "-s (attached value)"
check "contract: the attached value is not in the note, the adapter_note field or the warning line (short flag)" '! echo "$OUT" | grep "app-server adapter was not used" | grep -q SECRETVALUE && ! field "$(row implementor)" adapter_note | grep -q SECRETVALUE'
violation_case "a -c key and secret value attached to the flag" --args '["-cfoo_token=SECRETVALUE"]' "-c (attached value)"
check "contract: the attached value is not in the note, the adapter_note field or the warning line (-c)" '! echo "$OUT" | grep "app-server adapter was not used" | grep -q SECRETVALUE && ! field "$(row implementor)" adapter_note | grep -q SECRETVALUE'
violation_case "a long flag with =secret" --args '["--flag=SECRETVALUE"]' "--flag"
check "contract: a long flag with =value names the flag alone" '! echo "$OUT" | grep "app-server adapter was not used" | grep -q SECRETVALUE && ! field "$(row implementor)" adapter_note | grep -q SECRETVALUE'
# no redaction: the row args and the plain launch line keep the member's own args, secret included
drop_team
rost edit --member myrepo-implementor --args '["-c","foo_token=SECRETVALUE"]' >/dev/null
spawn1 "$IN_RANGE" -- implementor
REJECTED_LINE=$(start_line); REJECTED_ROW=$(row implementor)
drop_team
rost edit --member myrepo-implementor --args '["-c","foo_token=SECRETVALUE"]' >/dev/null
spawn1 "$NO_RANGE" -- implementor
PLAIN_LINE=$(start_line)
check "no redaction: the team row args equal the member's args verbatim" 'field "$REJECTED_ROW" args | grep -q "foo_token=SECRETVALUE"'
check "no redaction: the launch line of a refused adapter is byte-identical to the plain launch line, secret included" '[ -n "$PLAIN_LINE" ] && [ "$REJECTED_LINE" = "$PLAIN_LINE" ] && printf %s "$PLAIN_LINE" | grep -q SECRETVALUE'
check "doc: team-file.md tells the user to keep credentials out of args" 'grep -q "keep credentials in the environment or Codex.s own config, not in .args." "$H/../docs/team-file.md"'
drop_team
rost edit --member myrepo-implementor --args '["-c","model_reasoning_effort=high"]' >/dev/null
spawn1 "$IN_RANGE" -- implementor
check "contract: an allowed -c key beside the autoMode sandbox: the adapter is used (nearest non-match)" '[ "$RC" -eq 0 ] && start_line | grep -q -- "--remote" && [ "$(field "$(row implementor)" adapter)" = appserver ]'
check "pins: the terminal argv is the attach alone: nothing of the checked list is on it" '! start_line | grep -qE -- "--sandbox|--ask-for-approval|model_reasoning_effort|network_access|writable_roots|--add-dir"'
check "pins: the server argv is the four pins, then the checked list's settings, then app-server on the socket" '[ "$(tail -1 "$SANDBOX/server.args" | sed "s/ -c model=[^ ]*//; s/ -c model_instructions_file=[^ ]*//; s/ -c approvals_reviewer=[^ ]*//; s/ -c model_reasoning_effort=high//")" = "-c sandbox_mode=\"read-only\" -c sandbox_workspace_write.network_access=false -c sandbox_workspace_write.writable_roots=[\"$AH_HOME/run/codex/tmp/myrepo-$(field "$(row implementor)" name)\"] -c approval_policy=\"on-request\" -c sandbox_workspace_write.exclude_slash_tmp=true -c sandbox_workspace_write.exclude_tmpdir_env_var=true -c shell_environment_policy.set.TMPDIR=\"$AH_HOME/run/codex/tmp/myrepo-$(field "$(row implementor)" name)\" -c shell_environment_policy.set.AH_MEMBER_NAME=\"$(field "$(row implementor)" name)\" app-server --listen unix://$AH_HOME/run/codex/myrepo-$(field "$(row implementor)" name).sock" ] && tail -1 "$SANDBOX/server.args" | grep -q -- "-c model_reasoning_effort=high app-server"'
check "pins: ah's mapped settings (model, instructions, reviewer) moved to the server argv" 'tail -1 "$SANDBOX/server.args" | grep -q -- "-c model=" && tail -1 "$SANDBOX/server.args" | grep -q -- "-c model_instructions_file=" && tail -1 "$SANDBOX/server.args" | grep -q -- "-c approvals_reviewer=\"user\""'
drop_team
rost edit --member myrepo-implementor --auto-mode acceptEdits --args "" >/dev/null
spawn1 "$IN_RANGE" -- implementor
check "pins: acceptEdits runs the server in workspace-write with on-request (nearest non-match of read-only)" '[ "$RC" -eq 0 ] && tail -1 "$SANDBOX/server.args" | grep -q "^-c sandbox_mode=\"workspace-write\" .*-c approval_policy=\"on-request\" "'
drop_team
rost edit --member myrepo-implementor --auto-mode plan >/dev/null
spawn1 "$IN_RANGE" -- implementor
check "pins: plan runs the server read-only with approval never" '[ "$RC" -eq 0 ] && tail -1 "$SANDBOX/server.args" | grep -q "^-c sandbox_mode=\"read-only\" .*-c approval_policy=\"never\" "'
drop_team
rost edit --member myrepo-implementor --auto-mode acceptEdits --args "[\"--add-dir\",\"$SANDBOX/extra\"]" >/dev/null
spawn1 "$IN_RANGE" -- implementor
check "pins: an --add-dir root is the writable_roots value on the server and not on the terminal" '[ "$RC" -eq 0 ] && tail -1 "$SANDBOX/server.args" | grep -q "writable_roots=\[\"$AH_HOME/run/codex/tmp/[^\"]*\",\"$SANDBOX/extra\"\]" && ! start_line | grep -q -- "--add-dir[^,]*extra"'
rost edit --member myrepo-implementor --args "" >/dev/null
drop_team
rost edit --member myrepo-implementor --auto-mode manual >/dev/null
spawn1 "$IN_RANGE" FAKE_CODEX_VERSION=0.163.0 -- implementor
check "pins: a plain launch (out of range) carries no pin token" '[ "$RC" -eq 0 ] && ! start_line | grep -q "sandbox_workspace_write"'
rost edit --member myrepo-implementor --args "" >/dev/null

# ---- off herdr a codex member does not launch at all, so the adapter can never be applied there
printf '#!/bin/sh\ncase "$1" in list-sessions) exit 0;; new-window|split-window) echo %%9; exit 0;; *) exit 0;; esac\n' > "$BIN/tmux"; chmod +x "$BIN/tmux"
drop_team
RO_AS=1 ro env "$IN_RANGE" "$PLUGIN/bin/ah" roster spawn-one implementor --split --cwd "$PROJ"
check "tmux: an opted-in codex member is refused (it needs herdr), no row is written and no server started" '[ "$RC" -ne 0 ] && echo "$OUT" | grep -q "requires the herdr transport" && [ -z "$(row implementor 2>/dev/null)" ] && eval "$NOSOCK"'
rm -f "$BIN/tmux"

# ---- edit
rost edit --member myrepo-implementor --adapter ""
check "edit: --adapter \"\" removes the field" '[ "$RC" -eq 0 ] && ! grep -q "\"adapter\"" "$PROJ/.claude/ah2/agent-hierarchy.json"'
# ---- default-on (a codex member with no adapter field) and the "off" opt-out
vcalls() { wc -l < "$SANDBOX/version.calls" | tr -d ' '; }
DEFNOTE='codex 0.163.0 is outside the tested app-server range >=0.160.0 <0.163.0; launched without the app-server adapter (mail is pasted into its pane). Set adapter "off" to silence this.'
drop_team; : > "$SANDBOX/version.calls"
spawn1 "$NO_RANGE" -- implementor
ROW=$(row implementor)
DEFAULT_PLAIN_LINE=$(start_line)
check "default: absent field + empty range -> plain, no note, no codex --version call, no server" '[ "$RC" -eq 0 ] && ! start_line | grep -q -- "--remote" && [ "$(field "$ROW" adapter)" = "(absent)" ] && [ "$(field "$ROW" adapter_note)" = "(absent)" ] && ! echo "$OUT" | grep -q "outside the tested" && [ "$(vcalls)" = 0 ] && eval "$NOSOCK"'
drop_team
spawn1 "$IN_RANGE" -- implementor
ROW=$(row implementor)
check "default: absent field + range set + in range -> the adapter (server pid and thread on the row), no note" '[ "$RC" -eq 0 ] && start_line | grep -q -- "--remote" && kill -0 "$(field "$ROW" appserver_pid)" 2>/dev/null && field "$ROW" appserver_thread_id | grep -q "^th-" && [ "$(field "$ROW" adapter_note)" = "(absent)" ]'
check "default: ...and the row records no opt-in the member never made (its adapter field stays absent)" '[ "$(field "$ROW" adapter)" = "(absent)" ]'
drop_team
spawn1 "$IN_RANGE" FAKE_CODEX_VERSION=0.163.0 -- implementor
ROW=$(row implementor)
check "default: absent field + out of range -> plain launch byte-identical to a plain member's, the row carries the note" '[ "$RC" -eq 0 ] && [ "$(start_line)" = "$DEFAULT_PLAIN_LINE" ] && [ "$(field "$ROW" adapter)" = "(absent)" ] && [ "$(field "$ROW" adapter_note)" = "$DEFNOTE" ] && eval "$NOSOCK"'
check "default: absent field + out of range -> the spawn output's warnings hold the note exactly once" '[ "$(printf %s "$OUT" | grep -c "^ *\"codex 0\.163\.0 is outside the tested app-server range")" = 1 ]'
drop_team
spawn1 "$IN_RANGE" FAKE_CODEX_VERSION=garbage -- implementor
ROW=$(row implementor)
check "default: an unparseable version says (version unknown) in the same sentence" '[ "$RC" -eq 0 ] && field "$ROW" adapter_note | grep -q "^codex (version unknown) is outside the tested app-server range >=0.160.0 <0.163.0; launched without the app-server adapter (mail is pasted into its pane). Set adapter \"off\" to silence this.$"'
rost edit --member myrepo-implementor --adapter off
check "edit: --adapter off is stored" '[ "$RC" -eq 0 ] && grep -q "\"adapter\": *\"off\"" "$PROJ/.claude/ah2/agent-hierarchy.json"'
drop_team; : > "$SANDBOX/version.calls"
spawn1 "$IN_RANGE" -- implementor
ROW=$(row implementor)
check "off: in range -> plain launch, no note, no codex --version call, no server" '[ "$RC" -eq 0 ] && [ "$(start_line)" = "$DEFAULT_PLAIN_LINE" ] && [ "$(field "$ROW" adapter)" = "(absent)" ] && [ "$(field "$ROW" adapter_note)" = "(absent)" ] && [ "$(vcalls)" = 0 ] && eval "$NOSOCK"'
drop_team
spawn1 "$IN_RANGE" FAKE_CODEX_VERSION=0.163.0 -- implementor
ROW=$(row implementor)
check "off: out of range -> still plain with no note (the opt-out silences it)" '[ "$RC" -eq 0 ] && [ "$(field "$ROW" adapter_note)" = "(absent)" ] && ! echo "$OUT" | grep -q "outside the tested"'
drop_team
spawn1 "$NO_RANGE" -- implementor
check "off: empty range -> plain, no note" '[ "$RC" -eq 0 ] && [ "$(field "$(row implementor)" adapter_note)" = "(absent)" ]'
rost edit --member myrepo-implementor --adapter ""
rost edit --member myrepo-implementor --kind claude --model opus
rost edit --member myrepo-implementor --adapter off
check "edit: --adapter off on a member that is not codex is a usage error" '[ "$RC" -ne 0 ]'
rost edit --member myrepo-implementor --kind codex --model gpt-5
printf '#!/bin/sh\ncase "$1" in list-sessions) exit 0;; new-window|split-window) echo %%9; exit 0;; *) exit 0;; esac\n' > "$BIN/tmux"; chmod +x "$BIN/tmux"
drop_team
RO_AS=1 ro env "$IN_RANGE" "$PLUGIN/bin/ah" roster spawn-one implementor --split --cwd "$PROJ"
check "tmux: a codex member with no adapter field is refused the same way: no row, no server, no adapter note" '[ "$RC" -ne 0 ] && echo "$OUT" | grep -q "requires the herdr transport" && [ -z "$(row implementor 2>/dev/null)" ] && eval "$NOSOCK" && ! echo "$OUT" | grep -q "outside the tested"'
rm -f "$BIN/tmux"
drop_team
spawn1 "$IN_RANGE" -- implementor
check "default: the member is back on the default after the opt-out is removed and gets the adapter" '[ "$RC" -eq 0 ] && start_line | grep -q -- "--remote"'
rost edit --member myrepo-implementor --adapter appserver
rost edit --member myrepo-implementor --kind claude --model opus
check "edit: switching away from codex clears the adapter, with a notice" '[ "$RC" -eq 0 ] && echo "$OUT" | grep -q "cleared adapter" && ! grep -q "\"adapter\"" "$PROJ/.claude/ah2/agent-hierarchy.json"'
rost edit --member myrepo-implementor --adapter appserver
check "edit: --adapter on a member that is not codex is a usage error" '[ "$RC" -ne 0 ]'

# ---- the thread id lands only on the terminal's resume argument, whatever the team and member are called
OLD_PROJ=$PROJ; OLD_TEAM_FILE=$TEAM_FILE
PROJ="$SANDBOX/ahthreadid"; mkdir -p "$PROJ/.claude/ah2"; (cd "$PROJ" && git init -q)
TEAM_FILE="$PROJ/.claude/ah2/teams/ahthreadid.json"
rost init --level repo --route pane >/dev/null
rost add --no-spawn --level repo --role implementor --kind codex --route pane --model gpt-5 >/dev/null
rm -rf "$STATE" "$AH_HOME"; mkdir -p "$STATE"; ah_test_team "$SANDBOX/proof" "$(basename "$PROOF" .json)"
spawn1 "$IN_RANGE" -- implementor
check "name: a member and team called ahthreadid keep their names; the id is the resume argument only" '[ "$RC" -eq 0 ] && start_line | grep -q "\"ahthreadid-implementor\"" && start_line | grep -q "\"resume\",\"th-[0-9]*\",\"--remote\"" && ! start_line | grep -qE "th-[0-9]+-implementor|\"th-[0-9]+\",\"--kind\""'
PROJ=$OLD_PROJ; TEAM_FILE=$OLD_TEAM_FILE

# ---- hygiene: nothing this run started is still running
drop_team
sleep 0.3
check "no process names the sandbox but this shell (the servers the launches started are stopped)" '[ -z "$(pgrep -f -- "$SANDBOX/" | grep -v "^$$\$")" ]'

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
