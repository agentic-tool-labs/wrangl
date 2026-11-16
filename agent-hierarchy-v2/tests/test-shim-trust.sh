#!/bin/bash
# agent-hierarchy — where the sh shims (bin/ah-hook, bin/ahd) may find the native binary: the default home under the passwd entry, or
# a home listed in <default home>/trusted-homes; never an environment-named path outside test mode (rows Z-15, Z-18, Z-20 of the
# trust rules). The selection function lives in bin/ah-trust.sh; its rows run on a sandbox passwd home, so no row touches the
# real home. The end-to-end rows run the shims with the real passwd home, may read it, and never write to it.
# Usage: bash tests/test-shim-trust.sh   (exits 0 iff all rows pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
unset AH_BIN AH_IMPL AHD_NATIVE_BIN AH_PLUGIN_ROOT AH_TEST
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
TESTMODE="$PLUGIN/bin/ah-testmode.sh"; TRUST="$PLUGIN/bin/ah-trust.sh"
T=$(mktemp -d /private/tmp/ahprobe.XXXXXX)
case "$T" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$T'; aborting" >&2; exit 1;; esac
hermetic_on_exit 'rm -rf "$T"'
T=$(cd "$T" && pwd -P)
# the real passwd-home hook-errors log as this suite found it: the end-to-end rows run the shims against the real passwd home and
# must leave it as it was (the guard in lib-hermetic.sh reads the same file)
REAL_LOG=$_AH_REAL_LOG
REAL_UNITS='"hook":"(pretooluse-sendmessage-response|posttooluse-roster)"'
real_unit_rows() { cat "$REAL_LOG" "$REAL_LOG.1" 2>/dev/null | grep -cE "$REAL_UNITS"; }
REAL_ROWS_BEFORE=$(real_unit_rows)
[ -f "$REAL_LOG" ] && cp "$REAL_LOG" "$T/real-log.before" 2>/dev/null
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=${RC:-} OUT=${OUT:0:200} ERR=${ERR:0:200})"; fi
}

PW="$T/pw"; DEF="$PW/.claude/ah2"; OTHER="$T/other"; PROBE_PATH="$T/pathdir"
mkdir -p "$DEF/bin" "$OTHER/bin" "$PROBE_PATH"
chmod 700 "$DEF"
for b in "$DEF/bin/ahd" "$OTHER/bin/ahd" "$PROBE_PATH/ahd" "$T/ahbin"; do printf '#!/bin/sh\nexit 0\n' > "$b"; chmod 755 "$b"; done

# find <passwd home> <AH_TEST> <AH_HOME> <AH_BIN> <caller PATH> [skip dir] -> OUT (the binary or empty), RC
find_bin() {
  OUT=$(env -i PATH=/usr/bin:/bin AH_TEST="$2" AH_HOME="$3" AH_BIN="$4" sh -c '. "$1"; . "$2"; ah_find_binary "$3" "$4" "$5"' sh "$TESTMODE" "$TRUST" "$1" "$5" "${6:-}"); RC=$?
}
trust_file() { printf '%s\n' "$@" > "$DEF/trusted-homes"; chmod 600 "$DEF/trusted-homes"; }
no_trust_file() { rm -f "$DEF/trusted-homes"; }

# ---- strict mode (outside test mode)
no_trust_file
find_bin "$PW" "" "" "" "$PROBE_PATH"
check "the default home's binary is used when AH_HOME is unset" '[ "$OUT" = "$DEF/bin/ahd" ] && [ $RC = 0 ]'
find_bin "$PW" "" "$DEF" "" "$PROBE_PATH"
check "the default home named by AH_HOME is used" '[ "$OUT" = "$DEF/bin/ahd" ]'
find_bin "$PW" "" "$OTHER" "" "$PROBE_PATH"
check "an AH_HOME that is not trusted never supplies the binary: the default home's is used" '[ "$OUT" = "$DEF/bin/ahd" ]'
trust_file "$OTHER"
find_bin "$PW" "" "$OTHER" "" "$PROBE_PATH"
check "an AH_HOME listed in trusted-homes supplies the binary" '[ "$OUT" = "$OTHER/bin/ahd" ]'
trust_file "$T/not-other"
find_bin "$PW" "" "$OTHER" "" "$PROBE_PATH"
check "a trust file that lists another home does not trust this one (nearest non-match of the row above)" '[ "$OUT" = "$DEF/bin/ahd" ]'
trust_file "$OTHER"
chmod 660 "$DEF/trusted-homes"
find_bin "$PW" "" "$OTHER" "" "$PROBE_PATH"
check "a trust file writable by group is ignored" '[ "$OUT" = "$DEF/bin/ahd" ]'
chmod 600 "$DEF/trusted-homes"
chmod 775 "$OTHER/bin/ahd"
find_bin "$PW" "" "$OTHER" "" "$PROBE_PATH"
check "a trusted home's binary writable by group is not used, and nothing else is" '[ -z "$OUT" ] && [ $RC = 1 ]'
chmod 755 "$OTHER/bin/ahd"
chmod 757 "$OTHER/bin/ahd"
find_bin "$PW" "" "$OTHER" "" "$PROBE_PATH"
check "a trusted home's binary writable by other is not used" '[ -z "$OUT" ] && [ $RC = 1 ]'
chmod 755 "$OTHER/bin/ahd"
chmod 644 "$OTHER/bin/ahd"
find_bin "$PW" "" "$OTHER" "" "$PROBE_PATH"
check "a binary that is not executable is not used" '[ -z "$OUT" ]'
chmod 755 "$OTHER/bin/ahd"
find_bin "$PW" "" "$OTHER" "$T/ahbin" "$PROBE_PATH"
check "AH_BIN is ignored outside test mode" '[ "$OUT" = "$OTHER/bin/ahd" ]'
no_trust_file
find_bin "$PW" "" "" "$T/ahbin" "$PROBE_PATH"
check "AH_BIN and a PATH ahd are both ignored outside test mode" '[ "$OUT" = "$DEF/bin/ahd" ]'
rm -f "$DEF/bin/ahd"
find_bin "$PW" "" "" "$T/ahbin" "$PROBE_PATH"
check "with no binary in the default home there is none, whatever AH_BIN and PATH say" '[ -z "$OUT" ] && [ $RC = 1 ]'
if [ "$(id -u)" != 0 ]; then
  rm -f "$DEF/bin/ahd"; ln -s /bin/sh "$DEF/bin/ahd"
  find_bin "$PW" "" "" "" "$PROBE_PATH"
  check "a binary owned by another uid (a link to /bin/sh) is not used" '[ -z "$OUT" ] && [ $RC = 1 ]'
  rm -f "$DEF/bin/ahd"
fi
printf '#!/bin/sh\nexit 0\n' > "$DEF/bin/ahd"; chmod 755 "$DEF/bin/ahd"
find_bin "" "" "" "" "$PROBE_PATH"
check "an unknown passwd home yields no binary" '[ -z "$OUT" ] && [ $RC = 1 ]'

# ---- test mode: AH_TEST=1 and an AH_HOME under an ahprobe sandbox
SB="$T/ah"; mkdir -p "$SB/bin"; printf '#!/bin/sh\nexit 0\n' > "$SB/bin/ahd"; chmod 755 "$SB/bin/ahd"
find_bin "$PW" 1 "$SB" "$T/ahbin" "$PROBE_PATH"
check "test mode: AH_BIN comes first" '[ "$OUT" = "$T/ahbin" ]'
find_bin "$PW" 1 "$SB" "" "$PROBE_PATH"
check "test mode: then <AH_HOME>/bin/ahd" '[ "$OUT" = "$SB/bin/ahd" ]'
rm -f "$SB/bin/ahd"
find_bin "$PW" 1 "$SB" "" "$PROBE_PATH"
check "test mode: then ahd on the caller's PATH" '[ "$OUT" = "$PROBE_PATH/ahd" ]'
find_bin "$PW" 1 "$SB" "" "$PROBE_PATH" "$PROBE_PATH"
check "test mode: an ahd on PATH inside the skipped directory is not used" '[ -z "$OUT" ]'
find_bin "$PW" 1 "$SB/new/../x" "$T/ahbin" "$PROBE_PATH"
check "a .. in the part of AH_HOME that does not exist is not test mode: AH_BIN is ignored" '[ "$OUT" = "$DEF/bin/ahd" ]'
OUTSIDE=$(mktemp -d /private/tmp/ahout.XXXXXX); mkdir -p "$OUTSIDE/h"
find_bin "$PW" 1 "$OUTSIDE/h" "$T/ahbin" "$PROBE_PATH"
check "AH_TEST=1 with an AH_HOME outside an ahprobe sandbox is not test mode: AH_BIN is ignored" '[ "$OUT" = "$DEF/bin/ahd" ]'
rm -rf "$OUTSIDE"

# ---- the shims themselves: stat the hostile-PATH row Z-18 and the planted-binary row Z-20
CANARY="$T/canary"
HOSTILE="$T/hostile"; mkdir -p "$HOSTILE"
for tool in id find readlink stat wc dirname cd pwd uname; do
  printf '#!/bin/sh\necho %s-ran >> "%s"\nexec /usr/bin/%s "$@"\n' "$tool" "$CANARY" "$tool" > "$HOSTILE/$tool"; chmod 755 "$HOSTILE/$tool"
done
SBX="$T/ah-e2e"; mkdir -p "$SBX/bin"
printf '#!/bin/sh\necho planted-ran >> "%s"\nexit 0\n' "$CANARY" > "$SBX/bin/ahd"; chmod 755 "$SBX/bin/ahd"
cp "$SBX/bin/ahd" "$T/bin-ahd-planted"
mkdir -p "$T/planted-path"; cp "$SBX/bin/ahd" "$T/planted-path/ahd"

rm -f "$CANARY"
# a gate unit given a call it allows (a tool the gate does not look at): with no binary in the real default home the shim denies and
# writes no row, and with one the binary allows it silently, so this row never adds to the real hook-errors log whichever is installed
UNIT=pretooluse-sendmessage-response
REAL_ERRORS="$(eval echo "~$(id -un)")/.claude/ah2/hierarchy/hook-errors.jsonl"
unknown_rows() { grep -c "\"hook\":\"$UNIT\".*not implemented natively" "$REAL_ERRORS" 2>/dev/null; }
BEFORE=$(unknown_rows)
OUT=$(printf '{"hook_event_name":"PreToolUse","tool_name":"Read","tool_input":{"file_path":"/dev/null"}}' | env -u AH_TEST -u AH_HOME AH_IMPL=node PATH="$HOSTILE:/usr/bin:/bin" sh "$PLUGIN/bin/ah-hook" "$UNIT" 2>"$T/err"); RC=$?
check "Z-18 ah-hook: a hostile PATH's id, find, readlink, stat and wc are never run" '[ ! -e "$CANARY" ]'
check "Z-18 ah-hook: the unit it runs is one the installed binary has, so the real hook-errors log gets no row" '[ "$(unknown_rows)" = "$BEFORE" ]'
OUT=$(env -u AH_TEST AH_HOME="$SBX" AH_IMPL=node PATH="$HOSTILE:/usr/bin:/bin" sh "$PLUGIN/bin/ahd" where </dev/null 2>"$T/err"); RC=$?
check "Z-18 ahd: a hostile PATH's id, find, readlink, stat and wc are never run" '[ ! -e "$CANARY" ]'

rm -f "$CANARY"
OUT=$(env -u AH_TEST AH_HOME="$SBX" AH_BIN="$T/bin-ahd-planted" PATH="$T/planted-path:/usr/bin:/bin" sh "$PLUGIN/bin/ahd" where </dev/null 2>"$T/err"); RC=$?
check "Z-20 ahd: a planted ahd in AH_HOME/bin, AH_BIN and first on PATH is not run outside test mode" '[ ! -e "$CANARY" ]'

rm -f "$CANARY"
OUT=$(AH_TEST=1 AH_HOME="$SBX" PATH="/usr/bin:/bin" sh "$PLUGIN/bin/ahd" where </dev/null 2>"$T/err"); RC=$?
check "Z-20 ahd: in test mode the AH_HOME/bin/ahd is run (the positive beside the negatives)" '[ "$(cat "$CANARY" 2>/dev/null)" = planted-ran ]'
rm -f "$CANARY"
OUT=$(AH_TEST=1 AH_HOME="$SBX" PATH="/usr/bin:/bin" sh "$PLUGIN/bin/ah-hook" zzprobe </dev/null 2>"$T/err"); RC=$?
check "Z-20 ah-hook: in test mode the AH_HOME/bin/ahd is run" '[ "$(cat "$CANARY" 2>/dev/null)" = planted-ran ]'

# ---- the plugin root: the shim's own outside test mode, handed over as --plugin-root; a test's own variable in test mode
RECORD="$T/record"
printf '#!/bin/sh\n{ printf "root=%%s\\n" "${AH_PLUGIN_ROOT-unset}"; printf "arg=%%s\\n" "$@"; } > "%s"\nexit 0\n' "$RECORD" > "$SBX/bin/ahd"; chmod 755 "$SBX/bin/ahd"
ROOT_REAL=$(cd "$PLUGIN" && pwd -P)
mkdir -p "$T/fakenode"
rm -f "$RECORD"
AH_TEST=1 AH_HOME="$SBX" AH_PLUGIN_ROOT="$T/some-root" PATH="/usr/bin:/bin" sh "$PLUGIN/bin/ahd" where </dev/null >/dev/null 2>&1
check "Z-16 ahd: in test mode a test's AH_PLUGIN_ROOT is kept" '[ "$(sed -n 1p "$RECORD" 2>/dev/null)" = "root=$T/some-root" ]'

# the binary is handed the root as a leading option, and only the shim's own root
rm -f "$RECORD"
AH_TEST=1 AH_HOME="$SBX" PATH="/usr/bin:/bin" sh "$PLUGIN/bin/ahd" where </dev/null >/dev/null 2>&1
check "Z-16 ahd: the binary is exec'd as <bin> --plugin-root <root> where" '[ "$(sed -n 2,4p "$RECORD" | tr "\n" " ")" = "arg=--plugin-root arg=$ROOT_REAL arg=where " ]'
rm -f "$RECORD"
AH_TEST=1 AH_HOME="$SBX" AH_PLUGIN_ROOT="$T/some-root" PATH="/usr/bin:/bin" sh "$PLUGIN/bin/ahd" where </dev/null >/dev/null 2>&1
check "Z-16 ahd: a test's own AH_PLUGIN_ROOT travels as the variable, with no option" '[ "$(sed -n 1,2p "$RECORD" | tr "\n" " ")" = "root=$T/some-root arg=where " ]'
rm -f "$RECORD"
AH_TEST=1 AH_HOME="$SBX" PATH="/usr/bin:/bin" sh "$PLUGIN/bin/ah-hook" zzprobe </dev/null >/dev/null 2>&1
check "Z-16 ah-hook: the binary is exec'd as <bin> --plugin-root <root> hook <name>" '[ "$(sed -n 2,5p "$RECORD" | tr "\n" " ")" = "arg=--plugin-root arg=$ROOT_REAL arg=hook arg=zzprobe " ]'
rm -f "$RECORD"
AH_TEST=1 AH_HOME="$SBX" AH_PLUGIN_ROOT="$T/some-root" PATH="/usr/bin:/bin" sh "$PLUGIN/bin/ah-hook" zzprobe </dev/null >/dev/null 2>&1
check "Z-16 ah-hook: a test's own AH_PLUGIN_ROOT travels as the variable, with no option" '[ "$(sed -n 1,3p "$RECORD" | tr "\n" " ")" = "root=$T/some-root arg=hook arg=zzprobe " ]'
# through a symlink the root is the link's target's plugin, not the link's directory
mkdir -p "$T/links"; ln -sf "$PLUGIN/bin/ahd" "$T/links/ahd"; ln -sf "$PLUGIN/bin/ah-hook" "$T/links/ah-hook"
rm -f "$RECORD"
AH_TEST=1 AH_HOME="$SBX" PATH="/usr/bin:/bin" sh "$T/links/ah-hook" zzprobe </dev/null >/dev/null 2>&1
check "Z-16 ah-hook through a symlink: the root is the target's plugin" '[ "$(sed -n 3p "$RECORD")" = "arg=$ROOT_REAL" ]'
rm -f "$RECORD"
AH_TEST=1 AH_HOME="$SBX" PATH="/usr/bin:/bin" sh "$T/links/ahd" where </dev/null >/dev/null 2>&1
check "Z-16 ahd through a symlink: the root is the target's plugin" '[ "$(sed -n 3p "$RECORD")" = "arg=$ROOT_REAL" ]'

# ---- the route: the verdict first, on every route (ah_route), and the verbs the binary runs whatever the home is (ah_verb_free)
route() { # <AH_TEST> <AH_HOME> <AH_IMPL> -> OUT, RC   (passwd home $PW, caller PATH the probe dir)
  OUT=$(env -i PATH=/usr/bin:/bin AH_TEST="$1" AH_HOME="$2" AH_IMPL="$3" sh -c '. "$1"; . "$2"; ah_route "$3" "$4" ""' sh "$TESTMODE" "$TRUST" "$PW" "$PROBE_PATH"); RC=$?
}
no_trust_file
route "" "" ""
check "route: the default home and a binary: the binary" '[ "$OUT" = "bin $DEF/bin/ahd" ] && [ $RC = 0 ]'
route "" "" node
check "route: AH_IMPL=node on an accepted home is ignored: the binary" '[ "$OUT" = "bin $DEF/bin/ahd" ] && [ $RC = 0 ]'
route "" "$OTHER" node
check "route: AH_IMPL=node on a refused home does not reach Node: the default home's binary refuses it" '[ "$OUT" = "bin $DEF/bin/ahd" ] && [ $RC = 0 ]'
route "" "$OTHER" ""
check "route: a refused home goes to the default home's binary" '[ "$OUT" = "bin $DEF/bin/ahd" ]'
mv "$DEF/bin/ahd" "$DEF/bin/ahd.away"
route "" "$OTHER" node
check "route: a refused home with no binary and AH_IMPL=node is refused with the reason" '[ "$OUT" = "refuse AH_HOME is not the default home and is not trusted" ] && [ $RC = 1 ]'
route "" "$OTHER" ""
check "route: a refused home with no binary is refused" '[ "${OUT%% *}" = refuse ] && [ $RC = 1 ]'
route "" "" ""
check "route: an accepted home with no binary: none (the shim says so and runs nothing)" '[ "$OUT" = none ] && [ $RC = 0 ]'
mv "$DEF/bin/ahd.away" "$DEF/bin/ahd"
trust_file "$OTHER"
route "" "$OTHER" ""
check "route: a trusted home goes to its own binary" '[ "$OUT" = "bin $OTHER/bin/ahd" ] && [ $RC = 0 ]'
chmod 775 "$OTHER/bin/ahd"
route "" "$OTHER" ""
check "route: a trusted home whose binary is writable by group: none, never the default home's (nearest non-match)" '[ "$OUT" = none ] && [ $RC = 0 ]'
chmod 755 "$OTHER/bin/ahd"
no_trust_file
route 1 "$SB" node
check "route: test mode with AH_IMPL=node is ignored: not a Node route" '[ "$OUT" != node ] && [ "${OUT%% *}" != refuse ]'
trust_file "$OTHER"
route "" "$OTHER" node
check "route: a trusted home with AH_IMPL=node is ignored: not a Node route (nearest non-match of the refused row)" '[ "$OUT" != node ] && [ "${OUT%% *}" != refuse ]'
no_trust_file

verb_free() { env -i PATH=/usr/bin:/bin sh -c '. "$1"; shift; ah_verb_free "$@"' sh "$TRUST" "$@"; }
for v in home where check hook help --help -h --version; do
  check "verb-free: $v runs whatever the home is" 'verb_free "$v" x'
done
check "verb-free: no verb at all" 'verb_free'
check "verb-free: ah home" 'verb_free ah home trust /x'
check "verb-free: login status" 'verb_free login status'
for v in status start stop drain serve msg; do
  check "verb-free: $v does not run on a refused home" '! verb_free "$v" x'
done
check "verb-free: login run does not" '! verb_free login run'
check "verb-free: -hx does not (nearest non-match of -h)" '! verb_free -hx'
check "verb-free: ah status does not" '! verb_free ah status'
check "verb-free: a bare ah does not" '! verb_free ah'

# end to end: the recovery verb is not stopped at the shim on an untrusted home; any other verb is
OUT=$(env -u AH_TEST AH_HOME="$SBX" AH_IMPL=node PATH="$T/fakenode:/usr/bin:/bin" sh "$PLUGIN/bin/ahd" status </dev/null 2>&1); RC=$?
check "bin/ahd refuses status on an untrusted AH_HOME at the shim" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "so ah did not run"'

# ---- a hook whose home was refused and that no binary answers for: every hook, gate or not, is silent and leaves one row
MSG="ah: AH_HOME is not the default home and is not trusted, so ah did not run. Ask the user to trust this home from a terminal, or to unset AH_HOME."
HLOG="$PW/.claude/ah2/hierarchy/hook-errors.jsonl"
refusal() { # <passwd home> <name> -> OUT, RC
  OUT=$(env -i PATH=/usr/bin:/bin sh -c '. "$1"; ah_hook_refusal "$2" "$3" "$4"' sh "$TRUST" "$1" "$2" "$MSG"); RC=$?
}
rm -rf "$PW/.claude/ah2/hierarchy"
refusal "$PW" pretooluse-sendmessage-response
check "refusal: a gate passes the call: no output, exit 0, exactly one row" '[ $RC = 0 ] && [ -z "$OUT" ] && [ "$(wc -l < "$HLOG" | tr -d " ")" = 1 ] && grep -q "\"hook\":\"pretooluse-sendmessage-response\"" "$HLOG"'
for unit in sessionstart userpromptsubmit-x posttooluse-transcript stop-nudge notification pretooluse xpretooluse-a; do
  rm -rf "$PW/.claude/ah2/hierarchy"
  refusal "$PW" "$unit"
  check "refusal: $unit is silent with exit 0 and exactly one row naming $unit" '[ -z "$OUT" ] && [ $RC = 0 ] && [ "$(wc -l < "$HLOG" | tr -d " ")" = 1 ] && node -e "
const row = JSON.parse(require(\"fs\").readFileSync(process.argv[1], \"utf8\").trim());
const keys = Object.keys(row).join(\",\");
process.exit(keys === \"ts,hook,event,session_id,message,stack\" && row.hook === process.argv[2] && row.event === null && row.session_id === null && row.message === process.argv[3] && Array.isArray(row.stack) && /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z$/.test(row.ts) ? 0 : 1);" "$HLOG" "$unit" "$MSG"'
done
refusal "$PW" posttooluse-a; refusal "$PW" posttooluse-b
check "refusal: rows are appended, not replaced" '[ "$(wc -l < "$HLOG" | tr -d " ")" = 3 ]'
rm -rf "$PW/.claude/ah2/hierarchy"; mkdir -p "$PW/.claude/ah2/hierarchy"
head -c 1100000 /dev/zero | tr '\0' 'x' > "$HLOG"
refusal "$PW" posttooluse-a
check "refusal: a log over 1 MiB moves to .1 first" '[ -f "$HLOG.1" ] && [ "$(wc -l < "$HLOG" | tr -d " ")" = 1 ]'
rm -rf "$PW/.claude/ah2/hierarchy"
refusal "" posttooluse-a
check "refusal: with no passwd home nothing is written and the answer is still silence" '[ -z "$OUT" ] && [ $RC = 0 ] && [ ! -e "$HLOG" ]'
check "refusal: bin/ah-hook answers a refused home with ah_hook_refusal and exit 0, never Node" \
  'awk "/^  \"refuse \"\*\)/,/;;/" "$PLUGIN/bin/ah-hook" | grep -q "ah_hook_refusal" && awk "/^  \"refuse \"\*\)/,/;;/" "$PLUGIN/bin/ah-hook" | grep -q "exit 0"'

# ---- a unit with no binary to run it is never handed to anything: every hook, gate or not, is silent with one row
PLUG="$T/plug"; mkdir -p "$PLUG" "$T/home"; cp -R "$PLUGIN/bin" "$PLUG/"
NLOG="$T/home/.claude/ah2/hierarchy/hook-errors.jsonl"
SB2="$T/ah-nativeonly"; mkdir -p "$SB2"
shim_run() { # <unit> [env assignments...] -> OUT, RC ; test mode, plugin copy, HOME sandboxed
  local unit=$1; shift
  rm -f "$RECORD" "$NLOG"
  OUT=$(env -i PATH="/usr/bin:/bin" HOME="$T/home" AH_TEST=1 AH_HOME="$SB2" "$@" sh "$PLUG/bin/ah-hook" "$unit" </dev/null 2>"$T/err"); RC=$?
}
shim_run pretooluse-trust-gate AH_IMPL=node
check "gate with no binary and AH_IMPL=node: silent, exit 0, one row" '[ $RC = 0 ] && [ -z "$OUT" ] && [ "$(wc -l < "$NLOG" | tr -d " ")" = 1 ] && grep -q "native ahd binary is required" "$NLOG"'
shim_run pretooluse-chain
check "the chain, bare session, no binary: the tool passes, one row" '[ $RC = 0 ] && [ -z "$OUT" ] && [ "$(wc -l < "$NLOG" | tr -d " ")" = 1 ] && grep -q "\"hook\":\"pretooluse-chain\"" "$NLOG"'
shim_run pretooluse-chain WRANGL_TEAM_FILE=/x/t.json
check "the chain, member, no binary: the tool passes, one row" '[ $RC = 0 ] && [ -z "$OUT" ] && [ "$(wc -l < "$NLOG" | tr -d " ")" = 1 ]'
shim_run posttooluse-roster
check "advisory unit with no binary: silent, exit 0, one row" '[ $RC = 0 ] && [ -z "$OUT" ] && [ "$(wc -l < "$NLOG" | tr -d " ")" = 1 ] && grep -q "\"hook\":\"posttooluse-roster\"" "$NLOG"'
# sessionstart with no binary tells a member, with the plugin root it resolved and the command to run in a terminal; anyone else hears nothing
PLUG_REAL=$(cd "$PLUG" && pwd -P)
# notice_ok <json> <root>: one JSON document whose systemMessage and additionalContext are the same text, naming the command
notice_ok() {
  printf %s "$1" | node -e '
const d = JSON.parse(require("fs").readFileSync(0, "utf8"));
const h = d.hookSpecificOutput;
const want = "sh \"" + process.argv[1] + "/scripts/install.sh\" --apply --auto-rebuild";
process.exit(h.hookEventName === "SessionStart" && typeof d.systemMessage === "string" && d.systemMessage === h.additionalContext
  && d.systemMessage.includes("not built") && d.systemMessage.includes(want) && d.systemMessage.includes("in a terminal")
  && !("permissionDecision" in h) ? 0 : 1);' "$2"
}
shim_run sessionstart
check "sessionstart, bare session, no binary: no output, one row" '[ $RC = 0 ] && [ -z "$OUT" ] && [ "$(wc -l < "$NLOG" | tr -d " ")" = 1 ] && grep -q "\"hook\":\"sessionstart\"" "$NLOG"'
shim_run sessionstart WRANGL_TEAM_FILE=/x/t.json
check "sessionstart, WRANGL_TEAM_FILE, no binary: the not-built notice with the resolved root, exit 0" '[ $RC = 0 ] && notice_ok "$OUT" "$PLUG_REAL"'
check "sessionstart, WRANGL_TEAM_FILE, no binary: the notice claims no blocking" '! printf %s "$OUT" | grep -qi "block"'
check "sessionstart, WRANGL_TEAM_FILE, no binary: still exactly one hook-errors row" '[ "$(wc -l < "$NLOG" | tr -d " ")" = 1 ] && grep -q "\"hook\":\"sessionstart\"" "$NLOG"'
shim_run sessionstart AH_MEMBER_NAME=m1
check "sessionstart, AH_MEMBER_NAME, no binary: the not-built notice" '[ $RC = 0 ] && notice_ok "$OUT" "$PLUG_REAL"'
shim_run sessionstart-x WRANGL_TEAM_FILE=/x/t.json
check "a unit that only starts with sessionstart stays silent" '[ $RC = 0 ] && [ -z "$OUT" ] && [ "$(wc -l < "$NLOG" | tr -d " ")" = 1 ]'
shim_run pretooluse-trust-gate
check "gate with no binary: the row names the terminal command and the resolved root" \
  '[ $RC = 0 ] && [ -z "$OUT" ] && grep -q "in a terminal" "$NLOG" && grep -q -- "--apply --auto-rebuild" "$NLOG" && grep -qF "$PLUG_REAL" "$NLOG"'
QPLUG="$T/q\"p"; mkdir -p "$QPLUG"; cp -R "$PLUGIN/bin" "$QPLUG/"
OUT=$(env -i PATH="/usr/bin:/bin" HOME="$T/home" AH_TEST=1 AH_HOME="$SB2" WRANGL_TEAM_FILE=/x/t.json sh "$QPLUG/bin/ah-hook" sessionstart </dev/null 2>/dev/null); RC=$?
check "a plugin root with a double quote in it still yields one valid JSON notice naming it" '[ $RC = 0 ] && notice_ok "$OUT" "$(cd "$QPLUG" && pwd -P)"'
printf '#!/bin/sh\nexit 0\n' > "$SB2/bin-ahd.tmp"; mkdir -p "$SB2/bin"; mv "$SB2/bin-ahd.tmp" "$SB2/bin/ahd"; chmod 755 "$SB2/bin/ahd"
shim_run pretooluse-trust-gate
check "native-only gate with a binary: the binary is run, no refusal" '[ $RC = 0 ] && ! printf %s "$OUT" | grep -q "native ahd binary is required"'
shim_run sessionstart
check "sessionstart with a binary: the binary is run and no not-built notice is printed" '[ $RC = 0 ] && ! printf %s "$OUT" | grep -q "not built" && [ ! -e "$NLOG" ]'
rm -rf "$SB2/bin"

# ---- an exempt verb on a refused home with no binary to answer is refused at the shim, never handed to Node
# bin/ahd reads the passwd entry, which the environment cannot move: these rows run a copy of it that is given a sandbox passwd home.
NOBIN_PW="$T/pw-nobin"; mkdir -p "$NOBIN_PW"
for variant in nobin bin; do
  if [ $variant = nobin ]; then fixed=$NOBIN_PW; else fixed=$PW; fi
  sed "s#^passwd=\$(ah_passwd_home) || passwd=\$#passwd=$fixed#" "$PLUG/bin/ahd" > "$PLUG/bin/ahd-pw-$variant"
  chmod 755 "$PLUG/bin/ahd-pw-$variant"
done
check "the passwd-home substitution took in both copies" 'grep -q "^passwd=$NOBIN_PW\$" "$PLUG/bin/ahd-pw-nobin" && grep -q "^passwd=$PW\$" "$PLUG/bin/ahd-pw-bin"'
rm -f "$RECORD"
OUT=$(env -u AH_TEST AH_HOME="$SBX" PATH="$T/fakenode:/usr/bin:/bin" sh "$PLUG/bin/ahd-pw-nobin" where </dev/null 2>"$T/err"); RC=$?; ERR=$(cat "$T/err")
check "exempt verb, refused home, no binary: exit 2 with the reason and the install hint" '[ $RC = 2 ] && [ -z "$OUT" ] && printf %s "$ERR" | grep -q "is not the default home and is not trusted" && printf %s "$ERR" | grep -q "no native ahd binary could be run"'
check "...and nothing else was run for it" '[ ! -e "$RECORD" ]'
OUT=$(env -u AH_TEST AH_HOME="$SBX" PATH="$T/fakenode:/usr/bin:/bin" sh "$PLUG/bin/ahd-pw-bin" where </dev/null 2>"$T/err"); RC=$?; ERR=$(cat "$T/err")
check "exempt verb, refused home, the default home has a binary: it is run (nearest non-match)" '[ $RC = 0 ] && ! printf %s "$ERR" | grep -q "no native ahd binary could be run"'
rm -f "$RECORD"
OUT=$(env -u AH_TEST -u AH_HOME PATH="$T/fakenode:/usr/bin:/bin" sh "$PLUG/bin/ahd-pw-nobin" where </dev/null 2>"$T/err"); RC=$?; ERR=$(cat "$T/err")
check "an accepted home with no binary exits 1 naming install.sh (nearest non-match: the refusal needs a refused home)" '[ $RC = 1 ] && printf %s "$ERR" | grep -q "no native binary is installed" && printf %s "$ERR" | grep -q "scripts/install.sh --apply"'
OUT=$(env -u AH_TEST AH_HOME="$SBX" AH_IMPL=node PATH="$T/fakenode:/usr/bin:/bin" sh "$PLUG/bin/ahd-pw-bin" home trust /nonexistent/x </dev/null 2>&1); RC=$?
check "bin/ahd lets home trust through the shim on an untrusted AH_HOME, to the default home's binary" '[ $RC = 0 ] && ! printf %s "$OUT" | grep -q "so ah did not run"'

# ---- outside test mode the environment cannot choose the binary or the root: a gate and an advisory hook alike go to the default home's binary
GATE_PW="$T/pw-gate"; GATE_DEF="$GATE_PW/.claude/ah2"; GATE_REC="$T/gate-record"
mkdir -p "$GATE_DEF/bin"; chmod 700 "$GATE_DEF"
printf '#!/bin/sh\n{ printf "root=%%s\\n" "${AH_PLUGIN_ROOT-unset}"; printf "arg=%%s\\n" "$@"; } > "%s"\nexit 0\n' "$GATE_REC" > "$GATE_DEF/bin/ahd"; chmod 755 "$GATE_DEF/bin/ahd"
sed "s#^ah_load_passwd_home \&\& passwd=\$_ah_ph\$#passwd=$GATE_PW#" "$PLUG/bin/ah-hook" > "$PLUG/bin/ah-hook-pwgate"; chmod 755 "$PLUG/bin/ah-hook-pwgate"
check "the gate row's passwd-home substitution took" 'grep -q "^passwd=$GATE_PW\$" "$PLUG/bin/ah-hook-pwgate"'
gate_run() { # [env assignments...]: $UNIT, strict mode against the sandbox passwd home, the binary recorded by the fake
  rm -f "$RECORD" "$GATE_REC"
  OUT=$(env -u AH_TEST -u AH_HOME PATH="$T/fakenode:/usr/bin:/bin" "$@" sh "$PLUG/bin/ah-hook-pwgate" "$UNIT" </dev/null 2>"$T/err"); RC=$?
}
UNIT=pretooluse-reviewer-write-gate; gate_run AH_IMPL=node
check "a gate with AH_IMPL=node outside test mode goes to the binary" '[ -e "$GATE_REC" ]'
UNIT=posttooluse-roster; gate_run AH_IMPL=node
check "an advisory hook with AH_IMPL=node outside test mode goes to the binary too" '[ -e "$GATE_REC" ]'
UNIT=pretooluse-reviewer-write-gate; gate_run AH_PLUGIN_ROOT=/evil
PLUG_REAL=$(cd "$PLUG" && pwd -P)
check "Z-16 outside test mode an injected AH_PLUGIN_ROOT is replaced by the shim's own root, passed as --plugin-root" '[ "$(sed -n 2,3p "$GATE_REC" | tr "\n" " ")" = "arg=--plugin-root arg=$PLUG_REAL " ] && ! grep -q "evil" "$GATE_REC"'
UNIT=pretooluse-reviewer-write-gate
GATE_TESTHOME="$T/ah-gate-test"; mkdir -p "$GATE_TESTHOME/bin"
printf '#!/bin/sh\necho ran > "%s"\nexit 0\n' "$GATE_REC" > "$GATE_TESTHOME/bin/ahd"; chmod 755 "$GATE_TESTHOME/bin/ahd"
rm -f "$RECORD" "$GATE_REC"
OUT=$(env AH_TEST=1 AH_HOME="$GATE_TESTHOME" AH_IMPL=node PATH="$T/fakenode:/usr/bin:/bin" sh "$PLUG/bin/ah-hook-pwgate" "$UNIT" </dev/null 2>"$T/err"); RC=$?
check "in test mode, with a binary there, AH_IMPL=node is ignored: the gate goes to the binary" '[ -e "$GATE_REC" ]'
rm -f "$RECORD" "$GATE_REC"
OUT=$(env AH_TEST=1 AH_HOME="$GATE_TESTHOME" PATH="$T/fakenode:/usr/bin:/bin" sh "$PLUG/bin/ah-hook-pwgate" "$UNIT" </dev/null 2>"$T/err"); RC=$?
check "in test mode without AH_IMPL the same gate goes to the binary (nearest non-match)" '[ -e "$GATE_REC" ]'

# ---- the launcher's held ids and reused verdict: nothing the environment holds changes who is trusted, and a refused home stays refused
UNIT=pretooluse-reviewer-write-gate; gate_run _ah_uid=0 _ah_user=root _ah_ph=/evil
check "an injected uid, user and passwd home do not change the binary a gate runs" '[ -e "$GATE_REC" ]'
chmod 775 "$GATE_DEF/bin/ahd"; gate_run _ah_uid="$(id -u)"
check "a binary its group can write is still not run, with the right uid injected" '[ ! -e "$GATE_REC" ]'
chmod 755 "$GATE_DEF/bin/ahd"
REFUSED="$T/not-trusted-home"; mkdir -p "$REFUSED"; chmod 700 "$REFUSED"
for verb_env in "" "_ah_uid=0" "verdict=0" "reason=$REFUSED"; do
  OUT=$(env -u AH_TEST AH_HOME="$REFUSED" ${verb_env:+"$verb_env"} sh "$PLUGIN/bin/ah" roster status 2>&1); RC=$?
  check "a refused home is refused by bin/ah, exit 2 and the reason (env: ${verb_env:-none})" '[ $RC = 2 ] && case "$OUT" in *"AH_HOME is not the default home and is not trusted, so ah did not run."*) true;; *) false;; esac'
done
OUT=$(env -u AH_TEST -u AH_HOME sh -c '. "$1"; . "$2"; ah_route "$3" "" "" 1 "held reason"' sh "$TESTMODE" "$TRUST" "$T/pw-none"); RC=$?
check "a route given a refusing verdict with no binary refuses in those words, not a fresh verdict's" '[ $RC = 1 ] && [ "$OUT" = "refuse held reason" ]'
OUT=$(env -u AH_TEST -u AH_HOME sh -c '. "$1"; . "$2"; ah_route "$3" "" "" 0 "$3/home"' sh "$TESTMODE" "$TRUST" "$T/pw-none"); RC=$?
check "a route given an accepting verdict with no binary is none (nearest non-match)" '[ $RC = 0 ] && [ "$OUT" = none ]'
OUT=$(env -u AH_TEST -u AH_HOME sh -c '. "$1"; . "$2"; ah_route "$3" "" ""' sh "$TESTMODE" "$TRUST" "$T/pw-none"); RC=$?
check "a route with no verdict given still works one out" '[ $RC = 0 ] && [ "$OUT" = none ]'
OUT=$(env _ah_uid=0 sh -c '. "$1"; [ -z "$_ah_uid" ] && ah_passwd_home' sh "$TESTMODE"); RC=$?
check "the password home comes from id and the database, with an injected uid dropped at load" '[ $RC = 0 ] && [ "$OUT" = "$(eval "printf %s ~$(id -un)")" ]'
OUT=$(AH_TEST=1 AH_HOME="$T/ph-ah" HOME="$T/ph-home" sh -c '. "$1"; ah_passwd_home' sh "$TESTMODE"); RC=$?
check "test mode takes a sandboxed HOME as the home, as the native binary does" '[ $RC = 0 ] && [ "$OUT" = "$T/ph-home" ]'
OUT=$(env -u AH_TEST AH_HOME="$T/ph-ah" HOME="$T/ph-home" sh -c '. "$1"; ah_passwd_home' sh "$TESTMODE"); RC=$?
check "outside test mode a sandboxed HOME is ignored for the password home (nearest non-match)" '[ $RC = 0 ] && [ "$OUT" = "$(eval "printf %s ~$(id -un)")" ]'
OUT=$(sh -c 'id() { printf "uid=501 gid=20\n"; }; . "$1"; ah_passwd_home' sh "$TESTMODE"); RC=$?
check "an id line with no user name gives no password home" '[ $RC = 1 ] && [ -z "$OUT" ]'

# ---- a PATH entry that is a symlink to the shim is never the native binary (it would exec itself forever)
SHIMBIN="$T/shimroot/bin"; mkdir -p "$SHIMBIN" "$T/linkdir" "$T/realdir"
cp "$PLUGIN/bin/ahd" "$SHIMBIN/ahd"; chmod 755 "$SHIMBIN/ahd"
SHIMBIN=$(cd "$SHIMBIN" && pwd -P)
ln -s "$SHIMBIN/ahd" "$T/linkdir/ahd"
printf '#!/bin/sh\nexit 0\n' > "$T/realdir/ahd"; chmod 755 "$T/realdir/ahd"
find_bin "$PW" 1 "$SB" "" "$T/linkdir:$T/realdir" "$SHIMBIN"
check "a symlink to the shim on PATH is skipped and the real ahd after it is chosen" '[ "$OUT" = "$T/realdir/ahd" ]'
find_bin "$PW" 1 "$SB" "" "$T/linkdir" "$SHIMBIN"
check "with only a symlink to the shim on PATH there is no binary" '[ -z "$OUT" ] && [ $RC = 1 ]'
find_bin "$PW" 1 "$SB" "" "$T/realdir:$T/linkdir" "$SHIMBIN"
check "a real ahd on PATH is still chosen (nearest non-match)" '[ "$OUT" = "$T/realdir/ahd" ]'

# ---- static: POSIX sh, and the one library is the only file that reads the trust file
check "bin/ahd, bin/ah-hook and the libraries are POSIX sh (no [[ ]], function, local, process substitution, echo -e, source, ==, arithmetic)" \
  '[ -z "$(grep -nE "\[\[|\bfunction\b|\$\(\(|<\(|\blocal\b|\becho -e\b|\bsource\b|==" "$PLUGIN/bin/ahd" "$PLUGIN/bin/ah-hook" "$TESTMODE" "$TRUST" | grep -v ":[0-9]*:[[:space:]]*#")" ]'
check "the shims start with #!/bin/sh and are executable" 'head -1 "$PLUGIN/bin/ahd" | grep -qx "#!/bin/sh" && head -1 "$PLUGIN/bin/ah-hook" | grep -qx "#!/bin/sh" && [ -x "$PLUGIN/bin/ahd" ] && [ -x "$PLUGIN/bin/ah-hook" ]'
check "only the library reads trusted-homes among the shims" '! grep -n "trusted-homes" "$PLUGIN/bin/ahd" "$PLUGIN/bin/ah-hook" | grep -v ":[0-9]*:[[:space:]]*#" | grep -q .'

# ---- the real hook-errors log is left as it was
if [ -n "$REAL_LOG" ] && [ -f "$T/real-log.before" ] && [ -f "$REAL_LOG" ] && [ "$(ls -di "$REAL_LOG" | awk '{print $1}')" = "$_AH_REAL_LOG_INODE" ]; then
  check "the real hook-errors log still begins with exactly the bytes it had when the suite began" 'head -c "$(wc -c < "$T/real-log.before" | tr -d " ")" "$REAL_LOG" | cmp -s - "$T/real-log.before"'
fi
check "no row for a unit this suite runs against the real passwd home was added to it" '[ "$(real_unit_rows)" -le "$REAL_ROWS_BEFORE" ]'

echo "---"; echo "pass=$PASS fail=$FAIL"
[ "$FAIL" = 0 ]
