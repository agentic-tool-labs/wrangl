#!/bin/bash
# agent-hierarchy — runs a suite twice, once with the daemon off and once with a sandbox daemon up, so what the suite
# proves holds on both paths. Sourced by a test that sets AH_TEST_DAEMON=1 on its first line, right after lib-test-env.sh.
#
#   suite_modes "$0"            the driver: re-runs the suite under each mode (SUITE_MODE=off, then on) and exits with their combined status
#   suite_daemon_up <sandbox> [home]   in a mode run: off exports AH_DAEMON=off; on starts a daemon with its AH_HOME in a short /tmp
#                               directory and HOME=[home] (default: a directory beside it), so HOME-global files the daemon reads are the suite's
#   suite_daemon_down           stops the daemon it started and fails if one is left
#
# The daemon's AH_HOME and HOME are inside <sandbox>, so nothing under the real HOME is read or written.

suite_modes() {
  [ -n "${SUITE_MODE:-}" ] && return 0
  local rc=0 m
  for m in off on; do
    echo "=== daemon $m ==="
    SUITE_MODE=$m bash "$@" || rc=1
  done
  exit $rc
}

suite_daemon_pid() {
  node -e 'try{process.stdout.write(String(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid))}catch{}' "$SUITE_AH_HOME/run/ahd.json"
}

suite_daemon_up() {
  if [ "$SUITE_MODE" != on ]; then export AH_DAEMON=off; return 0; fi
  unset AH_DAEMON
  # A socket path has a short limit, so the daemon's home is a short directory of its own. A hierarchy-dir override a suite
  # sets is trusted only inside the sandbox AH_HOME lies in, so that directory is made inside the ahprobe.* directory the
  # suite's own sandbox is under; with none, it is a new one.
  local anc="${1:-}" under=""
  while [ -n "$anc" ] && [ "$anc" != / ]; do
    case "$(dirname "$anc")" in
      /tmp|/private/tmp) case "$(basename "$anc")" in ahprobe.[A-Za-z0-9]*) under=$anc;; esac; break ;;
    esac
    anc=$(dirname "$anc")
  done
  if [ -n "$under" ]; then
    SUITE_ROOT="$(mktemp -d "$under/s.XXXXXX")"
    case "$SUITE_ROOT" in "$under"/s.??????) ;; *) echo "suite: mktemp returned '$SUITE_ROOT'; aborting" >&2; exit 1;; esac
  else
    SUITE_ROOT="$(mktemp -d /tmp/ahprobe.XXXXXX)"
    case "$SUITE_ROOT" in /tmp/ahprobe.??????) ;; *) echo "suite: mktemp returned '$SUITE_ROOT'; aborting" >&2; exit 1;; esac
  fi
  SUITE_AH_HOME="$SUITE_ROOT/ah"
  SUITE_DHOME="${2:-$SUITE_ROOT/dhome}"
  mkdir -p "$SUITE_AH_HOME" "$SUITE_DHOME/.claude/ah2" "$SUITE_ROOT/dproj"
  export AH_HOME="$SUITE_AH_HOME"
  git -C "$SUITE_ROOT/dproj" init -q
  (cd "$SUITE_ROOT/dproj" && HOME="$SUITE_DHOME" "$PLUGIN/bin/ah" msg daemon start >"$SUITE_ROOT/daemon-start.out" 2>&1)
  SUITE_DPID=$(suite_daemon_pid)
  [ -n "$SUITE_DPID" ] && kill -0 "$SUITE_DPID" 2>/dev/null || { echo "suite: the sandbox daemon did not start: $(cat "$SUITE_ROOT/daemon-start.out")" >&2; exit 1; }
}

suite_daemon_down() {
  [ "${SUITE_MODE:-}" = on ] || return 0
  local p="${SUITE_DPID:-}" i rc=0
  if [ -n "$p" ]; then
    ah_signal_ours TERM "$p" "$SUITE_ROOT"
    for i in 1 2 3 4 5 6 7 8 9 10; do ah_pid_named "$p" "$SUITE_ROOT" || break; sleep 0.2; done
    if ah_pid_named "$p" "$SUITE_ROOT"; then ah_signal_ours KILL "$p" "$SUITE_ROOT"; echo "suite: the sandbox daemon $p had to be killed" >&2; rc=1; fi
  fi
  case "${SUITE_ROOT:-}" in /tmp/ahprobe.??????|/private/tmp/ahprobe.*/s.??????) rm -rf "$SUITE_ROOT";; esac
  return $rc
}

