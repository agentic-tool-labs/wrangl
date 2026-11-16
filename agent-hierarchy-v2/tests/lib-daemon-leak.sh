#!/bin/bash
# agent-hierarchy — daemon leak checks, sourced by tests/run-all.sh, tests/run-suite.sh and tests/test-no-daemon-leak.sh.
#
# One rule decides which daemons a run owns. A runner calls `leak_mark_run` before it takes its baseline: that exports a
# run marker in AHD_LEAK_RUN and keeps one that is already set, so a check nested in a runner shares the runner's marker. A
# new process whose command line names an ahd.mjs, or whose argv is `<...>/ahd [--plugin-root <dir>] serve ...` (a native
# daemon), is this run's when
#   (i)  its environment carries this run's marker, whatever its home or checkout path; or
#   (ii) it carries no marker, names this checkout (its hooks/ahd.mjs, or `--plugin-root` this checkout's root for a native
#        daemon), and its home (the --ah-home value, else AH_HOME from its environment) is under a temp root. A native
#        daemon's spawn passes a fixed set of variables and not the marker, so (ii) is what attributes it.
# Anything else that is new (another run's marker, another checkout's file, an unmarked daemon on a real home) is not this
# run's: `foreign_daemons` lists it so it can be printed, and it never fails a check.

# True when "$1" is under a test temp root.
leak_temp_home() {
  case "$1" in
    /tmp/*|/private/tmp/*|/var/folders/*|/private/var/folders/*|*ahprobe*|*ahsuite*) return 0;;
  esac
  return 1
}

# Export the run marker (value: printable, unique per run, no path); keep one that is already set.
leak_mark_run() {
  [ -n "${AHD_LEAK_RUN:-}" ] || AHD_LEAK_RUN="r$$-$(date +%s)-$RANDOM"
  export AHD_LEAK_RUN
}

LEAK_CHECKOUT_AHD="$(cd "$(dirname "${BASH_SOURCE[0]}")/../hooks" && pwd -P)/ahd.mjs"
LEAK_CHECKOUT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"

# The pid of every running process whose command line has a word that is, or ends in /, ahd.mjs.
leak_candidates() {
  ps -axo pid=,command= 2>/dev/null | awk '
    $2 == "awk" { next }
    # a native daemon is a binary run as `<...>/ahd serve ...`: its own name and the verb right after it
    { n = split($2, seg, "/"); j = 3; if ($j == "--plugin-root") j += 2; if (seg[n] == "ahd" && $j == "serve") { print $1; next } }
    { for (i = 2; i <= NF; i++) if ($i ~ /(^|\/)ahd\.mjs$/) { print $1; break } }'
}

# `leak_owner <pid>` prints "mine <home>" or "not <home>" by the rule above ("-" when no home can be read).
leak_owner() {
  local pid=$1 line toks marker home
  line=$(ps eww -p "$pid" 2>/dev/null | tail -n +2)
  toks=$(printf '%s\n' "$line" | tr ' ' '\n')
  marker=$(printf '%s\n' "$toks" | sed -n 's/^AHD_LEAK_RUN=//p' | head -1)
  home=$(printf '%s\n' "$toks" | awk '$0 == "--ah-home" { getline; print; exit }')
  [ -n "$home" ] || home=$(printf '%s\n' "$toks" | sed -n 's/^AH_HOME=//p' | head -1)
  if [ -n "$marker" ]; then
    if [ -n "${AHD_LEAK_RUN:-}" ] && [ "$marker" = "$AHD_LEAK_RUN" ]; then echo "mine ${home:--}"; else echo "not ${home:--}"; fi
    return
  fi
  local ours=
  printf '%s\n' "$toks" | grep -qxF -- "$LEAK_CHECKOUT_AHD" && ours=1
  [ -n "$ours" ] || [ "$(printf '%s\n' "$toks" | awk '$0 == "--plugin-root" { getline; print; exit }')" = "$LEAK_CHECKOUT_ROOT" ] && ours=1
  if [ -n "$ours" ] && [ -n "$home" ] && leak_temp_home "$home"; then
    echo "mine ${home}"
  else
    echo "not ${home:--}"
  fi
}

# `leaked_daemons [baseline-pids...]` prints "<pid> <home>" for every new process (not in the baseline) that is this run's.
leaked_daemons() {
  local skip=" $* " p own
  for p in $(leak_candidates); do
    case "$skip" in *" $p "*) continue;; esac
    own=$(leak_owner "$p")
    case "$own" in "mine "*) echo "$p ${own#mine }";; esac
  done
}

# `foreign_daemons [baseline-pids...]` prints "<pid> <home>" for every new process that is not this run's.
foreign_daemons() {
  local skip=" $* " p own
  for p in $(leak_candidates); do
    case "$skip" in *" $p "*) continue;; esac
    own=$(leak_owner "$p")
    case "$own" in "not "*) echo "$p ${own#not }";; esac
  done
}

# `checkout_daemons <ahd.mjs path> [skip-pids...]` prints the pid of every process whose command line names that file, wherever
# its AH_HOME is (a daemon started without the --ah-home tag included), except the listed pids.
checkout_daemons() {
  local path=$1 p; shift
  local skip=" $* "
  pgrep -f "$path" 2>/dev/null | while read -r p; do
    case "$skip" in *" $p "*) ;; *) echo "$p";; esac
  done
}

# `daemon_attribution <pid>` prints one line saying where a process came from: pid, parent pid, start time, the AH_HOME of its
# environment ("-" where ps cannot read it, as for a system binary or a process that has gone) and its full command line, last.
daemon_attribution() {
  local pid=$1 ppid start argv home
  ppid=$(ps -o ppid= -p "$pid" 2>/dev/null | tr -d ' ')
  start=$(ps -o lstart= -p "$pid" 2>/dev/null | sed 's/^ *//; s/ *$//')
  argv=$(ps -o command= -p "$pid" 2>/dev/null)
  home=$(ps eww -p "$pid" 2>/dev/null | tail -n +2 | tr ' ' '\n' | sed -n 's/^AH_HOME=//p' | head -1)
  printf 'pid=%s ppid=%s start="%s" AH_HOME=%s argv=%s\n' "$pid" "${ppid:--}" "${start:--}" "${home:--}" "${argv:--}"
}

# `leak_attribution "<leaked_daemons output>"` prints the attribution line of every pid in it.
leak_attribution() { printf '%s\n' "$1" | awk 'NF {print $1}' | while read -r p; do daemon_attribution "$p"; done; }

# `leak_foreign_report [baseline-pids...]` prints the new processes that are not this run's, under a heading, with attribution.
# Prints nothing when there are none. It never changes a verdict.
leak_foreign_report() {
  local f
  f=$(foreign_daemons "$@")
  [ -n "$f" ] || return 0
  echo "not this run's (new ahd.mjs processes, not counted):"
  leak_attribution "$f"
}
