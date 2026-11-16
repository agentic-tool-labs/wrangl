#!/usr/bin/env bash
# Schema upgrade check: the install-to-swap window of one schema bump, on a copy of the newest real backup. Run by hand; not in the ship gate.
#   tests/e2e/schema-upgrade.sh <old-sha> <new-sha>
# Builds both shas into a probe root /tmp/ahprobe.*, restores the backup copy with the OLD binary, starts the old ahd, runs the NEW CLI
# against it, swaps to the new ahd, then restores the pre-migrate backup the swap made and migrates it forward again.
# The real home is only read: the newest backup pair is copied out of ~/.claude/ah2/state/backups. Every ahd runs under sandbox-exec with
# writes denied outside the probe root. One PASS/FAIL line per step. Exit 0 every step PASS, 1 a FAIL, 2 bad argument or setup failure.
OLD=${1:-}; NEW=${2:-}
[ -n "$OLD" ] && [ -n "$NEW" ] || { echo "usage: schema-upgrade.sh <old-sha> <new-sha>" >&2; exit 2; }
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
REPO=$(git -C "$HERE" rev-parse --show-toplevel) || exit 2
OLD=$(git -C "$REPO" rev-parse --short=9 "$OLD^{commit}") || exit 2
NEW=$(git -C "$REPO" rev-parse --short=9 "$NEW^{commit}") || exit 2
REAL_BACKUPS="$HOME/.claude/ah2/state/backups"
# rewrite_db_paths, the one implementation of moving a real database copy's paths under the probe root (the test-mode start gate), and
# lib-pid.sh's stamped signals. Its top level sets HOME to the real home and set -u; HOME is put back, as the builds run outside the sandbox.
_home=$HOME
. "$HERE/../live-audit/user-rows.sh"
HOME=$_home

# Test mode holds only for an AH_HOME under /tmp/ahprobe.* (TestModePredicate), so not under $TMPDIR.
T=$(mktemp -d /tmp/ahprobe.XXXXXX)
[ -n "$T" ] && [ -d "$T" ] || { echo "schema-upgrade: no probe dir" >&2; exit 2; }
T=$(cd "$T" && pwd -P)
AH_HOME="$T/ah"
mkdir -p "$T/home" "$T/ah/state/backups" "$T/proj/.claude/ah2" "$T/tmp" "$T/log"
printf '(version 1)\n(allow default)\n(deny file-write* (subpath "/Users") (subpath "/tmp") (subpath "/private/tmp") (subpath "/private/var"))\n(allow file-write* (subpath "%s"))\n' "$T" >"$T/box.sb"
FAILS=0
step() { echo "$2 $1: $3"; [ "$2" = PASS ] || FAILS=$((FAILS + 1)); }

# run <old|new> <timeout s> <ahd args...>: one sandboxed call against the probe home; output to $T/log/last.
run() {
  local which=$1 secs=$2; shift 2
  ( cd "$T/proj" && env -i PATH="/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin" HOME="$T/home" AH_HOME="$AH_HOME" AH_TEST=1 \
      AH_TEST_REAL_HOME="$REAL_USER_HOME" WRANGL_HIERARCHY_DIR="$T/proj/.claude/ah2" TMPDIR="$T/tmp/" DOTNET_CLI_TELEMETRY_OPTOUT=1 \
      perl -e 'alarm shift; exec @ARGV' "$secs" sandbox-exec -f "$T/box.sb" "$T/$which-bin/ahd" --plugin-root "$T/$which/agent-hierarchy-v2" "$@" ) >"$T/log/last" 2>&1
}
schema() { sqlite3 -readonly "$AH_HOME/state/ah.db" "select value from meta where key='schema_version'" 2>/dev/null; }
pid() { python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["pid"])' "$AH_HOME/run/ahd.json" 2>/dev/null; }
# The pre-migrate backups this run made: the copied real backup can carry the same label, so it is left out.
migrated() { ls "$AH_HOME/state/backups" | grep -E -- "-v$OLD_V-pre-migrate-v$NEW_V\.db$" | grep -cvxF -- "$name"; }
# Every daemon this run saw start, as "pid<TAB>start stamp", so a pid the next start overwrites in ahd.json is still stopped at exit.
DAEMONS=
note_daemon() { local p; p=$(pid); [ -n "$p" ] && DAEMONS+="$p	$(ah_pid_stamp "$p")"$'\n'; }
# stop_stamped <pid> <stamp>: TERM, then KILL after 15 s, only while the pid still has that start stamp. `ah proc stop` cannot: it stops
# only a descendant of a registered session, and the daemon is detached.
stop_stamped() {
  local i
  for i in $(seq 15); do [ "$(ah_pid_stamp "$1")" = "$2" ] || return 0; [ "$i" = 1 ] && ah_signal_stamped TERM "$1" "$2"; sleep 1; done
  ah_signal_stamped KILL "$1" "$2"; sleep 1
  [ "$(ah_pid_stamp "$1")" != "$2" ]
}
# stop_daemon <which>: `ahd stop`, then the stamped signals if the recorded daemon is still up 10 s later.
stop_daemon() {
  local p s i; p=$(pid); s=$(ah_pid_stamp "$p")
  run "$1" 30 stop
  [ -n "$s" ] || return 0
  for i in $(seq 10); do [ "$(ah_pid_stamp "$p")" = "$s" ] || return 0; sleep 1; done
  stop_stamped "$p" "$s"
}
start_daemon() {
  local i rc=1
  rm -f "$AH_HOME/run/ahd.json"
  if run "$1" 60 start; then
    for i in $(seq 20); do [ -n "$(pid)" ] && kill -0 "$(pid)" 2>/dev/null && { rc=0; break; }; sleep 1; done
  fi
  note_daemon
  return $rc
}
# On every exit: stop each daemon noted, and one a timed-out start left in ahd.json. A run that passed leaves nothing (each probe root
# holds several copies of the database); a failed one keeps its root for the logs.
cleanup() {
  local p s
  trap '' INT TERM HUP
  note_daemon
  while IFS=$'\t' read -r p s; do [ -n "$p" ] && stop_stamped "$p" "$s"; done <<<"$DAEMONS"
  if [ "$FAILS" = 0 ] && [ -n "${OLD_V:-}" ]; then rm -rf "$T"; else echo "probe root kept: $T"; fi
}
trap cleanup EXIT
trap 'FAILS=$((FAILS + 1)); echo "schema-upgrade: interrupted"; exit 130' INT TERM HUP

# 1. build both shas
for which in old new; do
  sha=$OLD; [ "$which" = new ] && sha=$NEW
  mkdir -p "$T/$which"
  git -C "$REPO" archive "$sha" agent-hierarchy-v2 | tar -x -C "$T/$which" || { echo "schema-upgrade: archive of $sha failed" >&2; exit 2; }
  if MSBUILDDISABLENODEREUSE=1 bash "$T/$which/agent-hierarchy-v2/tests/publish-bin.sh" --out "$T/$which-bin" --log-dir "$T/log/build-$which" >"$T/log/build-$which.txt" 2>&1; then
    step "build $which $sha" PASS "$T/$which-bin/ahd"
  else
    step "build $which $sha" FAIL "see $T/log/build-$which.txt"; exit 1
  fi
done
OLD_V=$(sed -nE 's/.*const int SchemaVersion = ([0-9]+);.*/\1/p' "$T/old/agent-hierarchy-v2/native/src/Ah.StateDb/StateDbModule.cs")
NEW_V=$(sed -nE 's/.*const int SchemaVersion = ([0-9]+);.*/\1/p' "$T/new/agent-hierarchy-v2/native/src/Ah.StateDb/StateDbModule.cs")
echo "schema: $OLD $OLD_V -> $NEW $NEW_V"
[ -n "$OLD_V" ] && [ -n "$NEW_V" ] && [ "$NEW_V" -gt "$OLD_V" ] || { echo "schema-upgrade: no schema bump between $OLD and $NEW" >&2; exit 2; }

# 2. copy the newest backup pair at the old schema, restore it with the old binary, move its paths under the probe root
name=$(ls -t "$REAL_BACKUPS" | grep -E -- "-v$OLD_V-.*\.db$" | head -1)
[ -n "$name" ] || { echo "schema-upgrade: no v$OLD_V backup in $REAL_BACKUPS" >&2; exit 2; }
cp -p "$REAL_BACKUPS/$name" "$REAL_BACKUPS/${name%.db}.jsonl" "$AH_HOME/state/backups/" 2>/dev/null || cp -p "$REAL_BACKUPS/$name" "$AH_HOME/state/backups/" || exit 2
if run old 120 ah db restore "$name" --apply && [ "$(schema)" = "$OLD_V" ]; then
  rewrite_db_paths
  step "restore $name" PASS "schema $(schema), paths moved under $T"
else
  step "restore $name" FAIL "$(head -c 300 "$T/log/last")"; exit 1
fi

# 3. old daemon, new CLI, then the swap
if start_daemon old; then step "start old ahd" PASS "pid $(pid)"; else step "start old ahd" FAIL "$(head -c 300 "$T/log/last")"; exit 1; fi
OLD_PID=$(pid)
printf '\nSwap window probe.\n' >"$T/body.txt"
# window <label> <answer on the merits> <ah args...>: PASS on rc 0, or on that answer (a regex; the sandbox has no registered session,
# so msg new and send can only get as far as the identity check), FAIL on anything else or no answer in 30 s.
window() {
  local label=$1 merits=$2 rc; shift 2
  if run new 30 ah "$@" --cwd "$T/proj"; then rc=0; else rc=$?; fi
  if [ "$rc" = 142 ]; then step "window: $label" FAIL "wedged: no answer in 30 s"
  elif [ "$rc" != 0 ] && ! grep -qE "$merits" "$T/log/last"; then step "window: $label" FAIL "rc=$rc $(head -c 300 "$T/log/last" | tr '\n' ' ')"
  else step "window: $label" PASS "rc=$rc $(head -c 120 "$T/log/last" | tr '\n' ' ')"; fi
}
NONE='^no merits answer$'
window "msg list" "$NONE" msg list
window "msg new" 'not_your_session' msg new --to implementor --from orchestrator --slug swap-probe --title "Probe the swap window" --body-file "$T/body.txt"
window "msg send" 'unknown_address' msg send user --text "swap window probe"
window "rules list" "$NONE" rules list
window "roster checklist show" "$NONE" roster checklist show
if [ "$(pid)" = "$OLD_PID" ] && kill -0 "$OLD_PID" 2>/dev/null && [ "$(schema)" = "$OLD_V" ] && [ "$(migrated)" = 0 ]; then
  step "window: old ahd still serving" PASS "pid $OLD_PID, schema $OLD_V, no migration yet"
else
  step "window: old ahd still serving" FAIL "pid $(pid) (was $OLD_PID), schema $(schema), migrations $(migrated)"
fi
if stop_daemon old; then step "stop old ahd" PASS "pid $OLD_PID gone"; else step "stop old ahd" FAIL "pid $OLD_PID still up"; fi
if start_daemon new; then step "start new ahd" PASS "pid $(pid)"; else step "start new ahd" FAIL "$(head -c 300 "$T/log/last")"; fi

# 4. migrated once, at the new schema; the fresh pre-migrate backup restores and migrates forward again
run new 30 ah msg list --cwd "$T/proj"
if [ "$(schema)" = "$NEW_V" ] && [ "$(migrated)" = 1 ]; then step "migration" PASS "schema $NEW_V, one pre-migrate-v$NEW_V backup"
else step "migration" FAIL "schema $(schema), $(migrated) pre-migrate-v$NEW_V backups"; fi
fresh=$(ls -t "$AH_HOME/state/backups" | grep -E -- "-v$OLD_V-pre-migrate-v$NEW_V\.db$" | head -1)
stop_daemon new
if [ -n "$fresh" ] && run new 120 ah db restore "$fresh" --apply && [ "$(schema)" = "$OLD_V" ] && start_daemon new && run new 30 ah msg list --cwd "$T/proj" && [ "$(schema)" = "$NEW_V" ]; then
  step "fresh v$OLD_V backup restores and migrates" PASS "$fresh -> schema $NEW_V"
else
  step "fresh v$OLD_V backup restores and migrates" FAIL "${fresh:-no backup}: schema $(schema); $(head -c 300 "$T/log/last" | tr '\n' ' ')"
fi
stop_daemon new
[ "$FAILS" = 0 ] && { echo "schema-upgrade: PASS"; exit 0; }
echo "schema-upgrade: $FAILS FAIL"; exit 1
