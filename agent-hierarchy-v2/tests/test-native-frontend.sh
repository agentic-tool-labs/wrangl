#!/bin/bash
# agent-hierarchy — the real native front end (native/src/Ah.Cli, AOT-published once into a sandbox): `where`, the
# argv[0] namespace (which verbs a link named ah and a binary named ahd serve), the refusal of an unknown verb, the one-shot
# `state` verb and the native hook verb. Nothing runs through Node. Skips visibly when no .NET 10 SDK is installed. Nothing here
# touches the real home or daemon.
# Usage: bash tests/test-native-frontend.sh   (exits 0 iff every row passes)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(dirname "$0")/../scripts/lib-tree-root.sh"
PLUGIN="$(ah_tree_root "$(dirname "$0")/..")"
NATIVE="$PLUGIN/native"

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo "SKIP: test-native-frontend — no .NET 10 SDK"
  exit 0
fi

# The sandbox is /tmp/ahprobe.<id>: test mode (HOME and AH_HOME honoured, the binary's rows written under it) holds only for a
# directory of that name, and outside test mode the binary writes its refusals to the real home's hook-errors log.
T="$(mktemp -d /tmp/ahprobe.XXXXXX)"
[ -n "$T" ] && [ -d "$T" ] || { echo "mktemp failed"; exit 1; }
T="$(cd "$T" && pwd -P)"
case "$T" in /tmp/ahprobe.??????|/private/tmp/ahprobe.??????) ;; *) echo "refusing sandbox $T"; exit 1 ;; esac
hermetic_on_exit 'case "$T" in /tmp/ahprobe.??????|/private/tmp/ahprobe.??????) rm -rf "$T" ;; esac'

FAIL=0
ok()  { echo "ok   $1"; }
bad() { echo "FAIL $1"; FAIL=1; }
check() { local name="$1"; shift; if "$@" >/dev/null 2>&1; then ok "$name"; else bad "$name"; fi; }
# both <exit code wanted> <exit code got> <grep pattern> <text>: the exit code is the wanted one AND the text holds the pattern
# (a `test ... && grep ...` written after `check name` would put the grep outside the check).
both() { [ "$2" -eq "$1" ] && grep -q -- "$3" <<<"$4"; }
same() { [ "$1" = "$2" ]; }

export HOME="$T/home"
export AH_HOME="$HOME/.claude/ah2"
export AH_TEST=1
mkdir -p "$HOME" "$T/bin"
ah_test_always_on "$HOME"
unset AH_PLUGIN_ROOT
check "P-0 the sandbox is a test-mode sandbox" bash -c '. "$0/bin/ah-testmode.sh" && ah_test_mode' "$PLUGIN"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1
export UseSharedCompilation=false
RID="$(dotnet --info | sed -n 's/^ *RID: *//p' | head -1)"
LOG="$T/publish.log"
if ! dotnet publish "$NATIVE/src/Ah.Cli" -c Release -r "$RID" -o "$T/out" \
    -p:AhVersion=7.8.9 -p:AhContract=3 -p:AhPluginName=agent-hierarchy > "$LOG" 2>&1; then
  bad "publish Ah.Cli"; tail -20 "$LOG"; exit 1
fi
AHD="$T/out/ahd"
ok "publish Ah.Cli ($RID)"
check "the publish had no warnings" test -z "$(grep -i 'warning' "$LOG")"

# A sandbox plugin: the root marker is bin/ah-hook, and nothing else of a plugin is needed.
P="$T/plugin"
mkdir -p "$P/bin" "$P/.claude-plugin" "$P/native"
printf '{"name":"agent-hierarchy","version":"7.8.9"}\n' > "$P/.claude-plugin/plugin.json"
printf '{"contract":3}\n' > "$P/native/cutover.json"
printf '#!/bin/sh\n' > "$P/bin/ah-hook"
export AH_PLUGIN_ROOT="$P"
ln -s "$AHD" "$T/bin/ah"

# --- P-2: the build identity ----------------------------------------------------------------------------------
out="$("$AHD" where)"; rc=$?
check "P-2 where exits 0" test "$rc" -eq 0
check "P-2 binary.version, contract and plugin_name are the build properties" \
  grep -q '"binary":{"path":"[^"]*","version":"7.8.9","contract":3,"plugin_name":"agent-hierarchy"}' <<<"$out"
check "P-2 the plugin is the override and is compatible" \
  grep -q '"plugin":{"root":"'"$P"'","version":"7.8.9","contract":3,"source":"env"},"compatible":true' <<<"$out"
check "P-2 where reports no node and no install stamp" bash -c '! grep -q "\"node\"\\|\"stamp\"" <<<"$0"' "$out"
check "P-2 where is one line" test "$(printf '%s\n' "$out" | wc -l | tr -d ' ')" -eq 1
check "P-2 --json is refused as an unknown flag" bash -c '"$0" where --json >/dev/null 2>&1; [ $? -eq 2 ]' "$AHD"
check "P-2 --plain is text" bash -c '"$0" where --plain | grep -q "^compatible: yes$"' "$AHD"

# --- P-8: nothing installed ------------------------------------------------------------------------------------
snap() { (cd "$1" && find . | sort); }
before="$(snap "$HOME")"
out="$(env -u AH_PLUGIN_ROOT PATH="/usr/bin:/bin" "$AHD" where)"; rc=$?
check "P-8 where with no plugin exits 0" test "$rc" -eq 0
check "P-8 ... with plugin null and not compatible" grep -q '"plugin":null,"compatible":false' <<<"$out"
check "P-8 ... and writes nothing" same "$before" "$(snap "$HOME")"

# --- verbs the binary has no implementation for: never handed to Node ----------------------------------------------------
"$AHD" >/dev/null 2>"$T/err"; rc=$?
check "P-5 no argument is a usage error (exit 2)" both 2 "$rc" '^usage: ahd' "$(cat "$T/err")"
"$AHD" bogus --x >/dev/null 2>"$T/err"; rc=$?
check "P-5 an unknown verb is exit 2 with the message, and no node is started" both 2 "$rc" "^ahd: unknown command 'bogus'" "$(cat "$T/err")"
# --- argv[0]: the verb namespace comes from the basename of the link ---------------------------------------------------------
# Run from a directory with no team and none of the session's variables.
mkdir -p "$T/work"
nativeof() { # <exe> <args...>: stdout, stderr and exit code, in $T/nat.{out,err,rc}
  local e="$1"; shift
  (cd "$T/work" && env -u CLAUDE_PID -u WRANGL_TEAM_FILE "$e" "$@" </dev/null >"$T/nat.out" 2>"$T/nat.err"); echo $? > "$T/nat.rc"
}

nativeof "$T/bin/ah" roster whoami
check "P-7 a link named ah runs 'ah roster whoami' natively: exit 0 and no identity" \
  both 0 "$(cat "$T/nat.rc")" '"reason": "no-pane-id"' "$(cat "$T/nat.out")"
check "P-7 ... with nothing on stderr" test ! -s "$T/nat.err"
nativeof "$T/bin/ah" msg list
check "P-7 a link named ah runs 'ah msg list' natively: exit 0 and an empty list" \
  test "$(cat "$T/nat.rc")" -eq 0 -a "$(cat "$T/nat.out")" = "[]"
check "P-7 ... with nothing on stderr" test ! -s "$T/nat.err"
cp "$T/nat.out" "$T/ah-msg-list.out"; cp "$T/nat.rc" "$T/ah-msg-list.rc"
nativeof "$AHD" ah msg list
check "P-7 'ahd ah msg list' is the same as 'ah msg list' (stdout and exit)" bash -c 'cmp -s "$0" "$1" && cmp -s "$2" "$3"' "$T/nat.out" "$T/ah-msg-list.out" "$T/nat.rc" "$T/ah-msg-list.rc"
"$AHD" msg list >/dev/null 2>"$T/err"; rc=$?
check "P-7 a binary named ahd does not take the ah verbs: 'ahd msg list' is exit 2, unknown command" both 2 "$rc" "^ahd: unknown command 'msg'" "$(cat "$T/err")"
"$AHD" roster whoami >/dev/null 2>"$T/err"; rc=$?
check "P-7 ... nor roster, though the roster verb is registered: 'ahd roster whoami' is exit 2, unknown command" both 2 "$rc" "^ahd: unknown command 'roster'" "$(cat "$T/err")"

# --- the one-shot state verb, through the real binary -------------------------------------------------------------------
out="$(printf '{"args":{},"context":{"cwd":"%s","hierarchy_dir":"%s"},"op_id":"op-1"}' "$T" "$T" | "$AHD" state nodomain noop)"; rc=$?
check "P-9 'ahd state' with an unknown domain: the route's not_found body and exit 2" \
  test "$rc" -eq 2 -a "$out" = '{"error":{"code":"not_found","message":"no state operation nodomain.noop"}}'
printf 'not json' | "$AHD" state nodomain noop >/dev/null 2>&1; rc=$?
check "P-9 a body that is not JSON is exit 2" test "$rc" -eq 2
"$AHD" state nodomain >/dev/null 2>&1 </dev/null; rc=$?
check "P-9 'ahd state' with one argument is a usage error (exit 2)" test "$rc" -eq 2

# --- the hook verb is native ---------------------------------------------------------------------------------------------------
EVENT=$(printf '{"hook_event_name":"SessionStart","session_id":"s1","cwd":"%s","source":"startup"}' "$T/work")
out="$(printf '%s' "$EVENT" | ah_test_in_harness "$T/bin/ah" hook sessionstart 2>"$T/err")"; rc=$?
check "P-7 'ah hook sessionstart' runs the native hook: exit 0 and a SessionStart decision on stdout" \
  both 0 "$rc" '"hookEventName":"SessionStart"' "$out"
"$T/bin/ah" hook "../ctl" >/dev/null 2>&1; rc=$?
check "P-7 an unknown hook exits 2" test "$rc" -eq 2
out="$(printf '%s' "$EVENT" | ah_test_in_harness env PATH="/usr/bin:/bin" "$T/bin/ah" hook sessionstart 2>&1)"; rc=$?
check "P-6 the native verbs need no node: with only /usr/bin and /bin on PATH 'ah hook sessionstart' still runs (exit 0)" \
  both 0 "$rc" '"hookEventName":"SessionStart"' "$out"

# A closed reader: the native binary neither hangs nor prints a crash trace.
printf '%s' "$EVENT" | "$T/bin/ah" hook sessionstart 2>"$T/pipe.err" | { exec 0<&-; sleep 0.3; }
st=${PIPESTATUS[1]}
check "P-5b a native writer to a closed pipe ends quietly (exit 0 or 141) with no crash trace" \
  bash -c '{ [ "$0" = 0 ] || [ "$0" = 141 ]; } && ! grep -qi "unhandled exception\|^   at " "$1"' "$st" "$T/pipe.err"

# Nothing this run did reached the real home's log: no row in it names this sandbox.
if [ -n "${_AH_REAL_LOG:-}" ] && [ -f "$_AH_REAL_LOG" ]; then
  check "P-0b the real hook-errors log holds no row naming this sandbox" bash -c '! grep -qF -- "$0" "$1"' "$T" "$_AH_REAL_LOG"
else
  ok "P-0b no real hook-errors log to hold a row"
fi

[ "$FAIL" -eq 0 ]
