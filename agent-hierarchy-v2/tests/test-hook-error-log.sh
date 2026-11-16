#!/bin/bash
# agent-hierarchy — the hook error log.
#
# Claude Code ignores a hook's invalid JSON on exit 0 and treats exit 1 as non-blocking, both
# visible only under --debug: a hook that crashed and a hook that decided to stay quiet look
# identical from the outside. This log is the only difference, so the assertions are that every
# hook unit writes to it when it fails, that writing never changes what the hook decided, and that
# the logger itself cannot throw. Every row drives the native hook through bin/ah-hook.
# HOME-redirected; real state untouched.
# Usage: bash tests/test-hook-error-log.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
export AH_DAEMON=off  # not a daemon test: a lazily started ahd would outlive it
unset WRANGL_TEAM_FILE HERDR_PANE_ID  # every session roster launches carries these; a test must not inherit them
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-hook-error-log-test.XXXXXX")"
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
FAKEHOME="$SANDBOX/home"
PROJ="$SANDBOX/proj"
LOG="$FAKEHOME/.claude/ah2/hierarchy/hook-errors.jsonl"
BAD_TEAM="$SANDBOX/bad-team.json"
mkdir -p "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2"
printf 'this is not a team file' > "$BAD_TEAM"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:300})"; fi
}

lines() { [ -f "$LOG" ] && wc -l < "$LOG" | tr -d ' ' || echo 0; }

# A hook that throws: a stream member's Stop whose team file cannot be parsed makes the stream-label hook fail.
boom() {
  OUT=$(printf '{"hook_event_name":"Stop","session_id":"s1","cwd":"%s"}' "$PROJ" \
    | HOME="$FAKEHOME" HERDR_PANE_ID=p1 WRANGL_TEAM_FILE="$BAD_TEAM" sh "$PLUGIN/bin/ah-hook" stream-label 2>&1); RC=$?
}

# ---- 1: a failure is one appended line under the user's own .claude, not the project
boom
check "the failing hook still exits 0 and prints nothing" '[ "$RC" -eq 0 ] && [ -z "$OUT" ]'
check "one failure is one line in ~/.claude/ah2/hierarchy/hook-errors.jsonl" '[ "$(lines)" = 1 ]'
check "nothing is written under the project" '[ -z "$(find "$PROJ" -name "hook-errors*" 2>/dev/null)" ]'
check "the line is JSON with ts, hook, event, session_id, message and stack" \
  'node -e "
     const e = JSON.parse(require(\"node:fs\").readFileSync(process.argv[1], \"utf8\").trim());
     const ok = typeof e.ts === \"string\" && /^20[0-9]{2}-/.test(e.ts) && e.hook === \"stream-label\" && e.event === \"Stop\"
       && e.session_id === \"s1\" && typeof e.message === \"string\" && e.message.length > 0 && Array.isArray(e.stack);
     process.exit(ok ? 0 : 1);
   " "$LOG"'
boom
check "a second failure appends a second line" '[ "$(lines)" = 2 ]'

# ---- 2: the logger never throws — the one place with an empty catch
rm -rf "$FAKEHOME/.claude/ah2/hierarchy"
: > "$FAKEHOME/.claude/ah2/hierarchy"   # a FILE where the directory must go
boom
check "a log that cannot be written is swallowed: exit 0, no output" '[ "$RC" -eq 0 ] && [ -z "$OUT" ]'
rm -f "$FAKEHOME/.claude/ah2/hierarchy"

# ---- 3: the size cap rotates to .1 rather than growing without bound
mkdir -p "$(dirname "$LOG")"
node -e 'require("node:fs").writeFileSync(process.argv[1], "x".repeat(1024*1024 + 10))' "$LOG"
boom
check "past the cap: the old log is rotated to .1" '[ -f "$LOG.1" ]'
check "past the cap: the live log holds only the new entry" '[ "$(lines)" = 1 ]'
rm -f "$LOG" "$LOG.1"
node -e 'require("node:fs").writeFileSync(process.argv[1], "x".repeat(1024*1024))' "$LOG"
boom
check "exactly at the cap: the log is appended to, not rotated" '[ ! -f "$LOG.1" ]'
rm -f "$LOG" "$LOG.1"

# ---- 4: an unparseable stdin payload is itself logged — the {} it degrades to is
# indistinguishable from a plain main session, which is exactly the silent case.
OUT=$(printf 'not json at all' | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" sessionstart 2>&1); RC=$?
check "unparseable payload: the hook still exits 0" '[ "$RC" -eq 0 ]'
check "unparseable payload: readHookInput logs it" 'grep -q "readHookInput" "$LOG"'
check "unparseable payload: the log says the payload would not parse" 'grep -q "unparseable payload" "$LOG"'

# ---- 5: every hook unit the plugin registers writes a row when it cannot use its input. A unit
# whose failure stays bare is invisible again, which is the whole class this closes.
UNITS=$(node -e '
  const h = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).hooks;
  const units = new Set();
  for (const groups of Object.values(h)) for (const g of groups) for (const c of g.hooks || []) {
    const m = String(c.command).match(/\/bin\/ah-hook"? ([a-z0-9-]+)$/);
    if (m) units.add(m[1]);
  }
  process.stdout.write([...units].sort().join(" "));
' "$PLUGIN/hooks/registry.json")
# The PreToolUse gates run inside the pretooluse-chain unit; each is also a unit of its own that ah-hook runs by name.
# Three rows name their unit by a UnitName constant in the gate's own file rather than by a literal in GateUnits.cs.
GATE_UNITS=$({ grep -o 'new("pretooluse-[a-z-]*"' "$PLUGIN/native/src/Ah.Hooks/Gates/GateUnits.cs" | sed 's/new("//; s/"$//'
  grep -ho 'UnitName = "pretooluse-[a-z-]*"' "$PLUGIN"/native/src/Ah.Hooks/Gates/*.cs | sed 's/.*= "//; s/"$//'; } | sort -u | tr '\n' ' ')
UNITS="$UNITS $GATE_UNITS"
check "hooks.json registers hook units" '[ -n "$UNITS" ]'
SILENT=""
for u in $UNITS; do
  rm -f "$LOG"
  OUT=$(printf 'not json at all' | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" "$u" 2>&1); RC=$?
  [ "$RC" -eq 0 ] && [ "$(lines)" -ge 1 ] || SILENT="$SILENT $u"
done
check "every registered hook unit exits 0 and logs a row for an unusable payload" '[ -z "$SILENT" ]'

# ---- 6: logging does not change a hook's decision — a healthy hook writes nothing
rm -f "$LOG"
OUT=$(printf '{"hook_event_name":"SubagentStart","cwd":"%s","session_id":"s1","agent_id":"a1"}' "$PROJ" \
  | HOME="$FAKEHOME" sh "$PLUGIN/bin/ah-hook" subagentstart-cli-root 2>&1); RC=$?
check "a hook that does not throw logs nothing" '[ "$RC" -eq 0 ] && [ ! -s "$LOG" ]'

# ---- 7: the doctor reads the log through a 24 h window and survives an unparseable line
git -C "$PROJ" init -q
doctor_row() { # the hook-errors row's detail from `ah roster doctor`, in OUT
  OUT=$(HOME="$FAKEHOME" sh "$PLUGIN/bin/ah" roster doctor --cwd "$PROJ" 2>&1 \
    | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{const r=JSON.parse(s).rows.find((x)=>x.name==="hook-errors");process.stdout.write(r?r.detail:"(no row)")}catch{process.stdout.write("(unparseable)")}})'); RC=$?
}
mkdir -p "$(dirname "$LOG")"
OLD_TS=$(node -e 'process.stdout.write(new Date(Date.now() - 48*3600*1000).toISOString())')
printf '{"ts":"%s","hook":"old.mjs","message":"ancient"}\n' "$OLD_TS" > "$LOG"
printf 'this line is not json at all\n' >> "$LOG"
printf '{"ts":"%s","hook":"new.mjs","message":"fresh"}\n' "$(node -e 'process.stdout.write(new Date().toISOString())')" >> "$LOG"
doctor_row
check "the doctor counts only the entry inside the window, past an unparseable line" '[[ "$OUT" == "1 hook error(s) in 24 h"* ]] && [[ "$OUT" == *"new.mjs: fresh"* ]] && [[ "$OUT" != *"old.mjs"* ]]'
rm -f "$LOG"
doctor_row
check "an absent log reads as no errors" '[[ "$OUT" == "no hook errors in the last 24 h"* ]]'

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
