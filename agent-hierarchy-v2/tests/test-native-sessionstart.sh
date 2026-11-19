#!/bin/bash
# agent-hierarchy — the SessionStart hook's install-side behaviour: the daemon starts for a participant session, the install
# stamp notice, the background rebuild offer, and no hook installs or links anything. Every run is in a sandbox under
# /tmp/ahprobe.*; a fake install.sh records its call. The hook is the native binary run through bin/ah-hook (AH_BIN).
# Usage: bash tests/test-native-sessionstart.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
AH_TEST_DAEMON=1
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
H="$PLUGIN/hooks"
unset WRANGL_TEAM_FILE CLAUDE_PID CLAUDE_CODE_SESSION_ID HERDR_PANE_ID TMUX_PANE AH_DAEMON AHD_VERSION_OVERRIDE AHD_IDLE_EXIT_MS AH_IMPL AH_DISPATCHED AH_PLUGIN_ROOT
T="$(mktemp -d /tmp/ahprobe.XXXXXX)"
case "$T" in /tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting"; exit 1;; esac
T="$(cd "$T" && pwd -P)"
export AH_HOME="$T/ah" HOME="$T/home" AH_TEST=1
PROJ="$T/proj"
for v in AH_HOME HOME; do case "${!v}" in "$T"/*) ;; *) echo "$v is not under $T; aborting"; exit 1;; esac; done
mkdir -p "$AH_HOME" "$HOME/.claude/ah2" "$PROJ"
ah_test_always_on "$HOME"
git -C "$PROJ" init -q
cleanup() {
  local p
  [ -n "$LIVE" ] && ah_signal_child TERM "$LIVE"
  for p in $(daemon_pids); do ah_signal_ours TERM "$p" "$T"; done
  sleep 0.3
  for p in $(daemon_pids); do ah_signal_ours KILL "$p" "$T"; done
  local left; left=$(pgrep -f "$T" 2>/dev/null | grep -v "^$$\$" | tr '\n' ' ')
  [ -n "${left// /}" ] && echo "FAIL: processes still alive under $T: $left"
  [ -n "$AH_TEST_KEEP" ] && return
  case "$T" in /tmp/ahprobe.??????|/private/tmp/ahprobe.??????) rm -rf "$T";; esac
}
daemon_pids() {
  { node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid)+"\n")}catch{}' "$AH_HOME/run/ahd.json"
    cat "$AH_HOME"/logs/ahd.log* 2>/dev/null | sed -n 's/.* pid \([0-9]*\) root .*/\1/p'; } | sort -u
}
hermetic_on_exit cleanup
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (RC=$RC OUT=${OUT:0:400})"; fi; }
# a team with one member whose pane id is p-tm-implementor: a SessionStart in that pane is a participant (a mailbox address)
mkdir -p "$PROJ/.claude/ah2/teams"
sleep 900 & LIVE=$!
TM_DOC=$(printf '{"team_id":"t-tm","orchestrator":{"pid":%s,"session_id":"orch-tm"},"members":[{"role":"implementor","name":"tm-implementor","transport_id":"p-tm-implementor"}]}' "$LIVE")
echo "$TM_DOC" | AH_DAEMON=off ah_seed_team "$PROJ/.claude/ah2/teams/tm.json"
pstart() { # <session> [extra json, default a member's agent_type]: a SessionStart of the team's member, stdout in OUT
  local extra=${2-'"agent_type":"wrangl:implementor"'}
  OUT=$(printf '{"session_id":"%s","cwd":"%s","source":"startup","hook_event_name":"SessionStart"%s}' "$1" "$PROJ" "${extra:+,$extra}" | WRANGL_TEAM_FILE="$PROJ/.claude/ah2/teams/tm.json" HERDR_PANE_ID=p-tm-implementor ah_test_in_harness sh "$PLUGIN/bin/ah-hook" sessionstart 2>"$T/err"); RC=$?
}
start() { # <session> [extra json]: one SessionStart of a session with no team and no role, stdout in OUT
  OUT=$(printf '{"session_id":"%s","cwd":"%s","source":"startup","hook_event_name":"SessionStart"%s}' "$1" "$PROJ" "${2:+,$2}" | ah_test_in_harness sh "$PLUGIN/bin/ah-hook" sessionstart 2>"$T/err"); RC=$?
}
alive() { kill -0 "$1" 2>/dev/null; }
daemon_pid() { node -e 'try{const p=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid;process.kill(p,0);process.stdout.write(String(p))}catch{}' "$AH_HOME/run/ahd.json"; }
waitfor() { local i; for i in $(seq 1 "$2"); do eval "$1" && return 0; sleep 0.1; done; return 1; }

# The stamp rows below compare the stamp with what the binary says this plugin needs, so the binary must carry the plugin's
# identity (a build with no AhVersion/AhContract reports contract 0 and can judge no stamp). It is stated up front, once.
BINID=$(sh "$PLUGIN/bin/ahd" where 2>/dev/null)
BINID_OK=$(printf '%s' "$BINID" | node -e '
  const fs = require("fs"), p = process.argv[1];
  let o = {}; try { o = JSON.parse(fs.readFileSync(0, "utf8")).binary || {}; } catch {}
  const v = JSON.parse(fs.readFileSync(p + "/.claude-plugin/plugin.json", "utf8")).version;
  const c = JSON.parse(fs.readFileSync(p + "/native/cutover.json", "utf8")).contract;
  process.stdout.write(o.version === v && o.contract === c ? "yes" : "no");' "$PLUGIN" 2>/dev/null)
OUT=$BINID
check "the binary carries the plugin's version and contract (publish it with -p:AhVersion/-p:AhContract; a bare build says contract 0)" '[ "$BINID_OK" = yes ]'

# ---------------------------------------------------------------- S-1 (P-16): the daemon starts for a participant
# A participant is a non-subagent session with a mailbox address (it has a member row). Any other session leaves no state
# behind: no registration, no daemon, no writer stamp, no rebuild.
# the baseline: what an unconfigured session is told with the daemon off and no stamp (the success path adds nothing to it)
AH_DAEMON=off start sess-base
BASE=$OUT
check "P-16 precondition: no daemon yet, and the baseline is the existing unconfigured notice" '[ -z "$(daemon_pid)" ] && [[ "$BASE" == *"not configured"* ]]'
LIST0=$(cd "$AH_HOME" && find . | sort)
start sess-plain
sleep 1
check "P-16 an unregistered, non-subagent session (no team, no role) starts no daemon and leaves the AH_HOME listing unchanged" '[ -z "$(daemon_pid)" ] && [ "$(cd "$AH_HOME" && find . | sort)" = "$LIST0" ]'
check "P-16 and its stdout is byte-identical to the daemon-off baseline" '[ "$OUT" = "$BASE" ] && [ $RC = 0 ]'
pstart sess-member
check "P-16 a member session (a mailbox address) starts the daemon" 'waitfor "[ -n \"\$(daemon_pid)\" ]" 30'
D1=$(daemon_pid)
pstart sess-member-2
check "P-16 a second SessionStart does not spawn a second daemon (same pid, one live daemon)" '[ "$(daemon_pid)" = "$D1" ] && [ "$(daemon_pids | while read p; do kill -0 $p 2>/dev/null && echo $p; done | wc -l | tr -d " ")" = 1 ]'
ah_signal_ours TERM "$D1" "$T"; waitfor '! alive '"$D1" 40
pstart sess-sub '"agent_id":"sub-1","agent_type":"Explore"'
sleep 1
check "P-16 a subagent session, even in the member's pane, does not start the daemon" '[ -z "$(daemon_pid)" ]'
AH_DAEMON=off pstart sess-off
sleep 1
check "P-16 AH_DAEMON=off does not start it for a member" '[ -z "$(daemon_pid)" ]'

# ---------------------------------------------------------------- S-2 (P-17): the stamp notice (the daemon is off from here)
export AH_DAEMON=off
PLUGIN_VERSION=$(node -e 'console.log(JSON.parse(require("fs").readFileSync(process.argv[1]+"/.claude-plugin/plugin.json","utf8")).version)' "$PLUGIN")
CONTRACT=$(node -e 'console.log(JSON.parse(require("fs").readFileSync(process.argv[1]+"/native/cutover.json","utf8")).contract)' "$PLUGIN")
stamp() { # <version> <contract> <auto_rebuild> [last_rebuild json]
  local last="${4:-}"
  node -e 'const fs=require("fs");const o={version:process.argv[1],contract:Number(process.argv[2]),binary:process.env.AH_HOME+"/bin/ahd",link:"/sandbox/bin/ahd",plugin_root:process.argv[5],node:"node",built_at:"2026-01-01T00:00:00.000Z",auto_rebuild:process.argv[3]==="true",last_rebuild:process.argv[4]?JSON.parse(process.argv[4]):null};fs.writeFileSync(process.env.AH_HOME+"/install.json",JSON.stringify(o,null,2)+"\n")' "$1" "$2" "$3" "$last" "$PLUGIN"
}
notice_lines() { printf '%s' "$OUT" | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8")||"{}");const c=(o.hookSpecificOutput&&o.hookSpecificOutput.additionalContext)||"";process.stdout.write(c.split("\n").filter((l)=>/^ahd (in|is)|^the background rebuild of ahd/.test(l)).join("\n"))'; }
AH_DAEMON=off pstart s2base
PBASE=$OUT
stamp 0.1.0 "$CONTRACT" false
start s2u
check "P-17 an unregistered session gets the stamp line too (D7)" '[ "$(notice_lines)" = "ahd in \"/sandbox/bin/ahd\" is older than the plugin (0.1.0 < $PLUGIN_VERSION); run /wrangl:hierarchy install to rebuild it" ]'
pstart s2a
L=$(notice_lines)
check "P-17 an older stamp, auto_rebuild false: exactly one version notice" '[ "$L" = "ahd in \"/sandbox/bin/ahd\" is older than the plugin (0.1.0 < $PLUGIN_VERSION); run /wrangl:hierarchy install to rebuild it" ]'
stamp "$PLUGIN_VERSION" $((CONTRACT + 1)) false
pstart s2b
L=$(notice_lines)
check "P-17 a contract mismatch gives the contract line, not the version line" '[ "$L" = "ahd in \"/sandbox/bin/ahd\" is built for contract $((CONTRACT + 1)); this plugin needs $CONTRACT; run /wrangl:hierarchy install to rebuild it" ]'
stamp 0.1.0 $((CONTRACT + 1)) false
pstart s2c
L=$(notice_lines)
check "P-17 both behind: the contract line wins and only one line is added" '[[ "$L" == *"is built for contract"* ]] && [ "$(printf %s "$L" | wc -l | tr -d " ")" = 0 ]'
stamp "$PLUGIN_VERSION" "$CONTRACT" false
pstart s2d
check "P-17 equal versions and contracts: nothing added (the member's notice is unchanged)" '[ -z "$(notice_lines)" ] && [ "$(printf %s "$OUT" | sed "s/s2d/s2base/g")" = "$PBASE" ]'
mv "$AH_HOME/install.json" "$AH_HOME/install.json.off"
pstart s2e
check "P-17 no stamp: nothing added (the member's notice is unchanged)" '[ -z "$(notice_lines)" ] && [ "$(printf %s "$OUT" | sed "s/s2e/s2base/g")" = "$PBASE" ]'
printf 'not json' > "$AH_HOME/install.json"
pstart s2f
check "P-17 an unreadable stamp: nothing added, exit 0" '[ -z "$(notice_lines)" ] && [ $RC = 0 ]'
mv "$AH_HOME/install.json.off" "$AH_HOME/install.json"

# ---------------------------------------------------------------- S-3 (P-18): the background rebuild
# A sandbox plugin root whose scripts/install.sh records its argv, cwd and environment keys.
SP="$T/plugin"; mkdir -p "$SP"; cp -R "$PLUGIN/hooks" "$PLUGIN/bin" "$PLUGIN/.claude-plugin" "$SP/" 2>/dev/null
mkdir -p "$SP/native"; cp "$PLUGIN/native/cutover.json" "$SP/native/"
mkdir -p "$SP/scripts"
cat > "$SP/scripts/install.sh" <<'EOF'
#!/bin/sh
{ echo "argv: $*"; echo "cwd: $(pwd)"; echo "env: $(env | cut -d= -f1 | sort | tr '\n' ' ')"; } >> "$AH_HOME/rebuild-calls.txt"
echo "rebuild ran"
EOF
chmod +x "$SP/scripts/install.sh"
RB="$AH_HOME/rebuild-calls.txt"
rb_calls() { grep -c "^argv:" "$RB" 2>/dev/null || echo 0; }
startsp() { OUT=$(printf '{"session_id":"%s","cwd":"%s","source":"startup","hook_event_name":"SessionStart","agent_type":"wrangl:implementor"}' "$1" "$PROJ" | WRANGL_TEAM_FILE="$PROJ/.claude/ah2/teams/tm.json" HERDR_PANE_ID=p-tm-implementor AH_PLUGIN_ROOT="$SP" ah_test_in_harness sh "$SP/bin/ah-hook" sessionstart 2>"$T/err"); RC=$?; }
startsp_plain() { OUT=$(printf '{"session_id":"%s","cwd":"%s","source":"startup","hook_event_name":"SessionStart"}' "$1" "$PROJ" | AH_PLUGIN_ROOT="$SP" ah_test_in_harness sh "$SP/bin/ah-hook" sessionstart 2>"$T/err"); RC=$?; }
stamp 0.1.0 "$CONTRACT" true
startsp_plain s3u
check "P-18 an unregistered session with a behind stamp and auto_rebuild true starts the rebuild and gets the line (D7)" 'waitfor "[ \"\$(rb_calls)\" = 1 ]" 30 && [ "$(notice_lines)" = "ahd is being rebuilt in the background for $PLUGIN_VERSION" ]'
# reset: the member rows below count their own starts from a fresh stamp
rm -f "$RB"; stamp 0.1.0 "$CONTRACT" true
startsp s3a
L=$(notice_lines)
check "P-18 auto_rebuild true and behind: one line, and the rebuild starts once with --apply --rebuild-only --quiet" 'waitfor "[ \"\$(rb_calls)\" = 1 ]" 30 && [ "$L" = "ahd is being rebuilt in the background for $PLUGIN_VERSION" ] && grep -q "argv: --apply --rebuild-only --quiet" "$RB"'
check "P-18 the child ran in AH_HOME with an environment of HOME, PATH, AH_HOME and AH_SPAWN_DEPTH only; its output is in logs/rebuild.log" 'grep -q "^cwd: $AH_HOME\$" "$RB" && [ "$(sed -n "s/^env: //p" "$RB" | tr " " "\n" | grep -v "^\$" | grep -vx -e HOME -e PATH -e AH_HOME -e AH_SPAWN_DEPTH -e PWD -e SHLVL -e _ -e OLDPWD | wc -l | tr -d " ")" = 0 ] && grep -q "rebuild ran" "$AH_HOME/logs/rebuild.log"'
S3=$(node -e 'const o=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));process.stdout.write(o.last_rebuild.state+" "+o.last_rebuild.version+" "+(o.version))' "$AH_HOME/install.json")
check "P-18 the stamp records last_rebuild {version, at, state: started} before the start; the rest of the stamp is kept" '[ "$S3" = "started $PLUGIN_VERSION 0.1.0" ]'
startsp s3b
check "P-18 a second SessionStart within 24 h does not start another rebuild (still one recorded call)" 'sleep 0.5; [ "$(rb_calls)" = 1 ] && [ -z "$(notice_lines)" ]'
node -e 'const fs=require("fs");const f=process.env.AH_HOME+"/install.json";const o=JSON.parse(fs.readFileSync(f,"utf8"));o.last_rebuild.state="failed";fs.writeFileSync(f,JSON.stringify(o)+"\n")'
startsp s3c
L=$(notice_lines)
check "P-18 a failed last rebuild within 24 h: the failure line and no new start" 'sleep 0.5; [ "$L" = "the background rebuild of ahd failed; see \"$AH_HOME/logs/rebuild.log\"" ] && [ "$(rb_calls)" = 1 ]'
node -e 'const fs=require("fs");const f=process.env.AH_HOME+"/install.json";const o=JSON.parse(fs.readFileSync(f,"utf8"));o.last_rebuild.at=new Date(Date.now()-25*3600*1000).toISOString();fs.writeFileSync(f,JSON.stringify(o)+"\n")'
startsp s3d
check "P-18 a rebuild last started 25 h ago may start again" 'waitfor "[ \"\$(rb_calls)\" = 2 ]" 30'
stamp 0.1.0 "$CONTRACT" true
mv "$SP/scripts/install.sh" "$SP/scripts/install.sh.off"
startsp s3e
check "P-18 no install script: nothing started, one line naming the missing script and the fix (D3), exit 0" 'L=$(notice_lines); [[ "$L" == "ahd in \"/sandbox/bin/ahd\" is older than the plugin"*"; it was not rebuilt in the background because the install script "*" is missing; run /wrangl:hierarchy update" ]] && [ "$(printf %s "$L" | wc -l | tr -d " ")" = 0 ] && [ $RC = 0 ] && [ "$(rb_calls)" = 2 ]'
mv "$SP/scripts/install.sh.off" "$SP/scripts/install.sh"
stamp 0.1.0 "$CONTRACT" false
startsp s3f
check "P-18 auto_rebuild false never starts a rebuild (the notice line only)" 'sleep 0.5; [ "$(rb_calls)" = 2 ] && [[ "$(notice_lines)" == *"run /wrangl:hierarchy install"* ]]'

# ---------------------------------------------------------------- S-1, the failure path: the previous spawn failed
# A spawn that never became healthy leaves a marker `{at, pid, reason}` in run/. While it is fresh a member's SessionStart
# says so once per start and does not spawn again; once it is stale the start is tried again.
export AH_HOME="$T/ah2"; unset AH_DAEMON
mkdir -p "$AH_HOME/run"
printf '{"writer":"native","gen":1,"source":"user","version":"%s"}\n' "$PLUGIN_VERSION" > "$AH_HOME/state-writer.json"
echo "$TM_DOC" | AH_DAEMON=off ah_seed_team "$PROJ/.claude/ah2/teams/tm.json"  # the team lives in each home's store
mark() { # <seconds ago>: a spawn-failed marker written that long ago
  node -e 'const fs=require("fs");fs.writeFileSync(process.argv[1]+"/run/ahd.spawn-failed",JSON.stringify({at:new Date(Date.now()-Number(process.argv[2])*1000).toISOString(),pid:null,reason:"exited at once"})+"\n")' "$AH_HOME" "$1"
}
fail_line() { printf '%s' "$OUT" | node -e 'const o=JSON.parse(require("fs").readFileSync(0,"utf8")||"{}");const c=(o.hookSpecificOutput&&o.hookSpecificOutput.additionalContext)||"";process.stdout.write(c.split("\n").filter((l)=>/^ahd failed to start \(/.test(l)).join("\n"))'; }
mark 5
start f0
check "S-1 failure: an unregistered session adds no failure line and starts no daemon" '[ -z "$(fail_line)" ] && [ -z "$(daemon_pid)" ]'
pstart f1
L=$(fail_line)
check "S-1 failure: a fresh marker: a member's SessionStart surfaces exactly one line naming the reason and the retry time" '[[ "$L" =~ ^ahd\ failed\ to\ start\ \(exited\ at\ once\)\;\ retrying\ after\ 20[0-9]{2}-[0-9T:.-]+Z$ ]]'
check "S-1 failure: ...and no daemon is spawned while the marker is fresh" 'sleep 1; [ -z "$(daemon_pid)" ]'
pstart f2
check "S-1 failure: the next start says the same again" '[ "$(fail_line)" = "$L" ]'
mark 86400
pstart f3
check "S-1 failure: a stale marker: no failure line" '[ -z "$(fail_line)" ]'
check "S-1 failure: ...and the start is tried again" 'waitfor "[ -n \"\$(daemon_pid)\" ]" 30'
export AH_DAEMON=off

# ---------------------------------------------------------------- S-4 (P-19): no hook installs or links anything
OUT=$(grep -rnE 'launchctl|systemctl|LaunchAgents|ln -s|\.local/bin' "$PLUGIN/native/src/Ah.Hooks" "$PLUGIN/bin/ah-hook" --include='*.cs' --include=ah-hook | head -3)
check "P-19 static: no hook source (native/src/Ah.Hooks, bin/ah-hook) contains launchctl, systemctl, LaunchAgents, ln -s or .local/bin" '[ -z "$OUT" ] && [ -d "$PLUGIN/native/src/Ah.Hooks" ]'

echo "----"
echo "SUMMARY: $PASS passed, $FAIL failed"
[ $FAIL -eq 0 ]
