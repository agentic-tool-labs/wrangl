#!/bin/bash
# agent-hierarchy — the daemon-leak rule (tests/lib-daemon-leak.sh, shared with run-all's end-of-run report): a leaked process
# is reported with its pid, parent, start time, AH_HOME and command line, and one rule says which processes a run owns (its
# marker, or this checkout and a temp home; a native daemon by its plugin root). Planted processes stand in for daemons.
# Usage: bash tests/test-daemon-leak-check.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
. "$PLUGIN/tests/lib-daemon-leak.sh"
SB="$(mktemp -d /tmp/ahprobe.XXXXXX)"
[ -n "$SB" ] && [ -d "$SB" ] || { echo "mktemp failed"; exit 1; }
SB="$(cd "$SB" && pwd -P)"
case "$SB" in /tmp/ahprobe.??????|/private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
PLANTS=""
hermetic_on_exit 'for p in $PLANTS; do ah_signal_child TERM "$p"; done; rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:500})"; fi; }

# A process whose command line names <file> and does nothing for <ms>; its pid in PLANT.
plant() { # <file> <ms> [env assignment...]
  local f=$1 ms=$2; shift 2
  env "$@" node -e "setTimeout(() => {}, $ms)" "$f" >/dev/null 2>&1 &
  PLANT=$!; PLANTS="$PLANTS $PLANT"
  sleep 0.4
}

# ---- attribution
plant "$SB/x/ahd.mjs" 60000 AH_HOME=/x/home-a
OUT=$(daemon_attribution "$PLANT")
check "attribution names the pid, parent, start time, AH_HOME of its environment and the command line" \
  'case "$OUT" in "pid=$PLANT ppid=$$ start=\""?*"\" AH_HOME=/x/home-a argv=node -e "*"$SB/x/ahd.mjs") true;; *) false;; esac'
plant "$SB/x/ahd.mjs" 60000 -u AH_HOME
OUT=$(daemon_attribution "$PLANT")
check "a process with no AH_HOME in its environment shows - (nearest non-match)" 'case "$OUT" in *" AH_HOME=- argv=node "*) true;; *) false;; esac'
ah_signal_child TERM "$PLANT"; wait "$PLANT" 2>/dev/null; sleep 0.2
OUT=$(daemon_attribution "$PLANT")
check "a pid that has gone is reported with - fields, not an error" '[ "$OUT" = "pid=$PLANT ppid=- start=\"-\" AH_HOME=- argv=-" ]'

# ---- ownership (one rule for every leak check): each plant sets its marker explicitly
leak_mark_run
AHD="$PLUGIN/hooks/ahd.mjs"
OTHER_HOME="$SB/l-home"
# <home> <ms> [env assignment...]: a process whose command line names this checkout's ahd.mjs with `--ah-home <home>`.
plant_home() {
  local home=$1 ms=$2; shift 2
  env "$@" node -e "setTimeout(() => {}, $ms)" "$AHD" --ah-home "$home" >/dev/null 2>&1 &
  PLANT=$!; PLANTS="$PLANTS $PLANT"
  sleep 0.4
}
plant_home /x/home-real 60000 -u AHD_LEAK_RUN
check "L1: this checkout, no marker, a home that is not a temp dir: a candidate, not this run's" \
  'leak_candidates | grep -qx "$PLANT" && [ "$(leak_owner "$PLANT")" = "not /x/home-real" ]'
plant_home /x/home-real 60000 AHD_LEAK_RUN="$AHD_LEAK_RUN"
check "L2: this checkout, this run's marker, a home that is not a temp dir: counted" '[ "$(leak_owner "$PLANT")" = "mine /x/home-real" ]'
plant_home "$OTHER_HOME/ah" 60000 -u AHD_LEAK_RUN
check "L3: this checkout, no marker, a temp home: counted" '[ "$(leak_owner "$PLANT")" = "mine $OTHER_HOME/ah" ]'
plant_home "$OTHER_HOME/ah" 60000 AHD_LEAK_RUN=another-run-marker
check "L4: this checkout, another run's marker, a temp home: not counted" '[ "$(leak_owner "$PLANT")" = "not $OTHER_HOME/ah" ]'
env -u AHD_LEAK_RUN node -e "setTimeout(() => {}, 60000)" /x/other/hooks/ahd.mjs --ah-home "$OTHER_HOME/ah" >/dev/null 2>&1 &
PLANT=$!; PLANTS="$PLANTS $PLANT"; sleep 0.4
check "L5: another checkout's ahd.mjs, no marker, a temp home: not counted" '[ "$(leak_owner "$PLANT")" = "not $OTHER_HOME/ah" ]'
env -u AHD_LEAK_RUN node -e "setTimeout(() => {}, 60000)" "$SB/p/hooks/not-the-daemon.mjs" --ah-home "$OTHER_HOME/ah" >/dev/null 2>&1 &
PLANT=$!; PLANTS="$PLANTS $PLANT"; sleep 0.4
check "a process that does not name an ahd.mjs is not a candidate at all (nearest non-match)" '! leak_candidates | grep -qx "$PLANT"'

# L6: the real spawn. A native daemon is attributed by its plugin root and its temp home (its spawn passes the marker on to no
# one); a daemon of another plugin root, under the same kind of home, is not this run's.
L6="$(mktemp -d /tmp/ahprobe.XXXXXX)"
if [ -n "$L6" ] && [ -d "$L6" ]; then
  L6="$(cd "$L6" && pwd -P)"
  mkdir -p "$L6/ah" "$L6/home" "$L6/proj"
  (cd "$L6/proj" && env -i PATH="$PATH" HOME="$L6/home" AH_HOME="$L6/ah" AH_TEST=1 AHD_LEAK_RUN="$AHD_LEAK_RUN" ${AH_BIN:+AH_BIN="$AH_BIN"} "$PLUGIN/bin/ah" msg daemon start >/dev/null 2>&1)
  L6PID=$(node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid))}catch{}' "$L6/ah/run/ahd.json")
  PLANTS="$PLANTS $L6PID"
  OUT=$(ps eww -p "$L6PID" 2>/dev/null | tail -n +2 | tr ' ' '\n' | grep -x "AHD_LEAK_RUN=.*")
  check "L6: a daemon spawned by ah msg daemon start is a candidate and the rule counts it (this plugin root, a temp home)" \
    '[ -n "$L6PID" ] && leak_candidates | grep -qx "$L6PID" && [ "$(leak_owner "$L6PID")" = "mine $L6/ah" ]'
  # nearest non-match: the same daemon started from another copy of the plugin
  mkdir -p "$L6/other/ah" "$L6/other/proj"
  cp -R "$PLUGIN/bin" "$PLUGIN/.claude-plugin" "$L6/other/"
  mkdir -p "$L6/other/home"
  (cd "$L6/other/proj" && env -i PATH="$PATH" HOME="$L6/other/home" AH_HOME="$L6/other/ah" AH_TEST=1 ${AH_BIN:+AH_BIN="$AH_BIN"} sh "$L6/other/bin/ah" msg daemon start >/dev/null 2>&1)
  L6OTHER=$(node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid))}catch{}' "$L6/other/ah/run/ahd.json")
  PLANTS="$PLANTS $L6OTHER"
  check "L6: a daemon of another plugin root under a temp home is a candidate and is not this run's (nearest non-match)" \
    '[ -n "$L6OTHER" ] && leak_candidates | grep -qx "$L6OTHER" && [ "$(leak_owner "$L6OTHER")" = "not $L6/other/ah" ]'
  [ -n "$L6OTHER" ] && ah_signal_ours TERM "$L6OTHER" "$L6"
  [ -n "$L6PID" ] && ah_signal_ours TERM "$L6PID" "$L6"
  for _ in $(seq 1 30); do gone=$(ps -p "$L6PID" -o pid= 2>/dev/null; ps -p "$L6OTHER" -o pid= 2>/dev/null); [ -z "$gone" ] && break; sleep 0.2; done
  case "$L6" in /tmp/ahprobe.??????|/private/tmp/ahprobe.??????) rm -rf "$L6";; esac
else
  FAIL=$((FAIL+1)); echo "FAIL: L6: mktemp failed"
fi

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
