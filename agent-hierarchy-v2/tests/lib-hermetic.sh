#!/usr/bin/env bash
# agent-hierarchy — the shared hermetic preamble. Every tests/test-*.sh sources this as its first executable line.
#
# After it, a test cannot reach the user's real herdr, tmux, claude or codex, and cannot leave a tmux server behind:
#   - every HERDR_* and HC_* (herdr desktop) variable and TMUX / TMUX_PANE / WRANGL_TEAM_FILE / CLAUDE_PID is unset (a file may set any of them
#     again for its own fixtures);
#   - TMUX_TMPDIR is a fresh directory, so a tmux a test starts gets a private server (a test that passes tmux -S puts
#     the socket under it);
#   - AH_TEST_HERMETIC=1 turns on the guard in hooks/lib-mux.mjs, the one place herdr and tmux are executed and the
#     check on the shell strings that launch a member. With it, a guarded binary runs only when it resolves, on the
#     PATH of the call, into a directory listed in AH_TEST_FAKE_BIN (colon-separated absolute directories). Anything
#     else is the real binary: the call is written to AH_TEST_TRIPWIRE, reported on stderr, the process AH_TEST_PID is
#     sent SIGTERM, and nothing runs.
# A test that has a fake herdr, tmux, claude or codex exports AH_TEST_FAKE_BIN=<its bin dir> after creating it. PATH is
# not changed.
#
# This file also owns how a test ends. A test never sets its own trap: it registers cleanup with
#   hermetic_on_exit '<command>'
# and the handler below runs those commands in order on every way out (normal exit, a failed command, SIGTERM, SIGINT,
# SIGHUP), then kills any tmux server still listening on a socket under TMUX_TMPDIR (reporting it as a leak and
# failing the test), then removes TMUX_TMPDIR.
#
# TMPDIR is kept short. A Claude Code session's scratch directory is over 100 characters, and a daemon socket under a
# sandbox below it passes the 103-byte limit, so the daemon refuses to start and a test waits for it until the suite
# times out. A TMPDIR longer than 40 characters is replaced by a fresh /tmp/ahprobe.XXXXXX directory that this shell
# owns and removes on the way out; 40 leaves room for the longest sandbox template of a suite that starts a daemon
# (44 characters with its slash) plus `/ah/run/ahd.sock` (16) under the limit. A TMPDIR of 40 or fewer, or unset, is
# left as it is (run-all.sh gives each test one of about 36).
#
# AH_TEST_HERMETIC is for the test suite only; nothing else sets it. Sourcing this twice in one shell changes nothing.

if [ "${AH_TEST_HERMETIC:-}" = 1 ] && [ "${AH_TEST_PID:-}" = "$$" ]; then
  return 0 2>/dev/null || true
fi

for _ah_v in $(compgen -v HERDR_) $(compgen -v HC_); do unset "$_ah_v"; done
unset _ah_v TMUX TMUX_PANE WRANGL_TEAM_FILE CLAUDE_PID AH_TEST_FAKE_BIN
unset AH_SPAWN_DEPTH WRANGL_ROSTER WRANGL_EXPECTED_ROOT AH_MEMBER_NAME
# a direct run of a suite that starts a run-all of its own must not inherit the lock or abort controls of an outer run
unset AH_RUN_ALL_LOCK AH_RUN_ALL_LOCK_WAIT AH_RUN_ALL_LOCK_FAST AH_RUN_ALL_NOABORT AH_RUN_ALL_FAILFAST
# Whatever the launching Claude or Codex session exports is not the test's. CODEX_HOME and CLAUDE_CONFIG_DIR are the two a
# run-all.sh test legitimately has (an empty directory of its own); they are handled with AH_HOME below.
for _ah_v in $(compgen -e | grep -E '^(CLAUDE_|CODEX_|CLAUDECODE$)' | grep -vxE 'CODEX_HOME|CLAUDE_CONFIG_DIR'); do unset "$_ah_v"; done
unset _ah_v

# The launching shell's state home and hierarchy directory never reach a test: a value that is not inside a /tmp/ahprobe.<id>
# sandbox (what run-all.sh and the tests' own sandboxes hand over) is dropped, so a test that forgets to set its own talks to its
# default sandbox and never to whatever the caller exported. A `..` component never counts as inside.
for _ah_v in AH_HOME WRANGL_HIERARCHY_DIR CODEX_HOME CLAUDE_CONFIG_DIR; do
  eval "_ah_val=\${$_ah_v-}"
  case "$_ah_val" in
    '') ;;
    */.. | */../*) unset "$_ah_v" ;;
    /tmp/ahprobe.[A-Za-z0-9]* | /private/tmp/ahprobe.[A-Za-z0-9]*) ;;
    *) unset "$_ah_v" ;;
  esac
done
unset _ah_v _ah_val

# The real tmux, found before a test prepends fakes to PATH. A value inherited from a parent test is kept.
if [ -z "${AH_REAL_TMUX+x}" ]; then
  AH_REAL_TMUX=$(command -v tmux 2>/dev/null || true)
fi
export AH_REAL_TMUX

# A short root keeps the unix socket path under the platform limit.
_ah_t=$(mktemp -d /tmp/ahtt.XXXXXX) || return 1
export TMUX_TMPDIR="$_ah_t"
export AH_TEST_HERMETIC=1
export AH_TEST_PID=$$
export AH_TEST_TRIPWIRE="$_ah_t/tripwire"
: >"$AH_TEST_TRIPWIRE"
_AH_HERMETIC_DIR="$_ah_t"
_AH_HERMETIC_FILE="${BASH_SOURCE[1]:-$0}"
_AH_HERMETIC_FILE_ABS=$(cd "$(dirname "$_AH_HERMETIC_FILE")" 2>/dev/null && printf %s "$PWD/$(basename "$_AH_HERMETIC_FILE")")
unset _ah_t

_AH_HERMETIC_TMP=""
_ah_cur=${TMPDIR:-}
if [ "${#_ah_cur}" -gt 40 ]; then
  _ah_tt=$(mktemp -d /tmp/ahprobe.XXXXXX) || return 1
  [ -n "$_ah_tt" ] && [ -d "$_ah_tt" ] || return 1
  export TMPDIR="$_ah_tt"
  _AH_HERMETIC_TMP="$_ah_tt"
fi
unset _ah_cur _ah_tt

# The real hook-errors log (under the password database's home, never $HOME): where a shim run outside test mode writes. A test
# run must leave nothing there that a test produced, so its size is taken now and the rows appended since are checked on exit.
# AH_HERMETIC_REAL_LOG lets the guard's self-tests inspect a log under a probe sandbox instead.
_AH_REAL_LOG=${AH_HERMETIC_REAL_LOG:-}
case "$_AH_REAL_LOG" in
  */.. | */../*) _AH_REAL_LOG= ;;
  /tmp/ahprobe.[A-Za-z0-9]*/* | /private/tmp/ahprobe.[A-Za-z0-9]*/*) ;;
  *) _AH_REAL_LOG= ;;
esac
if [ -z "$_AH_REAL_LOG" ]; then
  _ah_pwuser=$(id -un 2>/dev/null || true)
  case "$_ah_pwuser" in
    '' | *[!A-Za-z0-9._-]*) ;;
    *)
      _ah_pwhome=$(eval "printf %s ~$_ah_pwuser" 2>/dev/null || true)
      case "$_ah_pwhome" in /*) _AH_REAL_LOG="$_ah_pwhome/.claude/ah2/hierarchy/hook-errors.jsonl" ;; esac
      ;;
  esac
fi
_AH_REAL_LOG_SIZE=0
_AH_REAL_LOG_INODE=
if [ -n "$_AH_REAL_LOG" ] && [ -f "$_AH_REAL_LOG" ]; then
  _AH_REAL_LOG_SIZE=$(wc -c <"$_AH_REAL_LOG" | tr -d ' ')
  _AH_REAL_LOG_INODE=$(ls -di "$_AH_REAL_LOG" 2>/dev/null | awk '{print $1}')
fi
unset _ah_pwuser _ah_pwhome

AH_HERMETIC_EXIT_CMDS=()
hermetic_on_exit() { AH_HERMETIC_EXIT_CMDS+=("$1"); }

_hermetic_exit() {
  local _hx_rc=$? _hx_cmd _hx_sock _hx_leaked=0
  trap - EXIT TERM INT HUP
  set +e
  for _hx_cmd in ${AH_HERMETIC_EXIT_CMDS[@]+"${AH_HERMETIC_EXIT_CMDS[@]}"}; do
    eval "$_hx_cmd"
  done
  if [ -n "$AH_REAL_TMUX" ] && [ -d "$_AH_HERMETIC_DIR" ]; then
    _hx_socks=$(find "$_AH_HERMETIC_DIR" -type s 2>/dev/null)
    while IFS= read -r _hx_sock; do
      [ -n "$_hx_sock" ] || continue
      if "$AH_REAL_TMUX" -S "$_hx_sock" kill-server >/dev/null 2>&1; then
        echo "ah: test hermeticity: $_AH_HERMETIC_FILE left a tmux server running on $_hx_sock; it was killed" >&2
        echo "FAIL: hermetic exit: tmux server left under TMUX_TMPDIR"
        _hx_leaked=1
      fi
    done <<EOF
$_hx_socks
EOF
  fi
  # Rows a test left in the real hook-errors log: a probe unit (zz*) or the shim's refusal text. Real hook errors that arrive from
  # the user's own sessions during the run are not these and are left alone.
  # A log that rotated during the run is a new file (a different inode): all of it is this run's, and the old one now lives at
  # .1, where only what was appended past the recorded size is.
  # Every run on the machine shares that log, so a row is this test's only when the unit it names appears in this test file; a row
  # for a unit the file does not name came from a run beside it and is reported without failing this one. A file that cannot be
  # read (a probe run from a string) answers for every row.
  if [ -n "$_AH_REAL_LOG" ] && [ -f "$_AH_REAL_LOG" ]; then
    _hx_pat='"hook":"zz|so ah did not run'
    _hx_inode=$(ls -di "$_AH_REAL_LOG" 2>/dev/null | awk '{print $1}')
    _hx_now=$(wc -c <"$_AH_REAL_LOG" | tr -d ' ')
    _hx_rows=
    if [ "$_hx_inode" = "$_AH_REAL_LOG_INODE" ]; then
      if [ "$_hx_now" -gt "$_AH_REAL_LOG_SIZE" ]; then
        _hx_rows=$(tail -c "+$((_AH_REAL_LOG_SIZE + 1))" "$_AH_REAL_LOG" | grep -E "$_hx_pat")
      fi
    else
      _hx_rows=$(grep -E "$_hx_pat" "$_AH_REAL_LOG")
      if [ -z "$_hx_rows" ] && [ -n "$_AH_REAL_LOG_INODE" ] && [ -f "$_AH_REAL_LOG.1" ] \
        && [ "$(ls -di "$_AH_REAL_LOG.1" 2>/dev/null | awk '{print $1}')" = "$_AH_REAL_LOG_INODE" ]; then
        _hx_rows=$(tail -c "+$((_AH_REAL_LOG_SIZE + 1))" "$_AH_REAL_LOG.1" | grep -E "$_hx_pat")
      fi
    fi
    if [ -n "$_hx_rows" ]; then
      _hx_own=; _hx_ownunits=; _hx_other=; _hx_otherunits=
      while IFS= read -r _hx_row; do
        _hx_unit=$(printf '%s' "$_hx_row" | sed -n 's/.*"hook":"\([^"]*\)".*/\1/p')
        if [ ! -r "$_AH_HERMETIC_FILE_ABS" ] || [ -z "$_hx_unit" ] || grep -qF -- "$_hx_unit" "$_AH_HERMETIC_FILE_ABS" 2>/dev/null; then
          _hx_own="$_hx_own$_hx_row"$'\n'; _hx_ownunits="$_hx_ownunits $_hx_unit"
        else
          _hx_other="$_hx_other$_hx_row"$'\n'; _hx_otherunits="$_hx_otherunits $_hx_unit"
        fi
      done <<<"$_hx_rows"
      if [ -n "$_hx_own" ]; then
        echo "ah: test hermeticity: $_AH_HERMETIC_FILE appended test rows to the real hook-errors log $_AH_REAL_LOG (unit: $(printf '%s\n' $_hx_ownunits | sort -u | tr '\n' ' ')):" >&2
        printf '%s' "$_hx_own" | head -3 | cut -c1-200 >&2
        _hx_count=$(printf '%s' "$_hx_own" | wc -l | tr -d ' ')
        _hx_units=$(printf '%s\n' $_hx_ownunits | sort -u | tr '\n' ' ')
        echo "FAIL: hermetic exit: $_hx_count hook-error rows from this run (${_hx_units% })"
        printf '%s' "$_hx_own" | head -1 | cut -c1-200
        _hx_leaked=1
      fi
      if [ -n "$_hx_other" ]; then
        echo "ah: test hermeticity: note: rows for unit: $(printf '%s\n' $_hx_otherunits | sort -u | tr '\n' ' ')were appended to the real hook-errors log $_AH_REAL_LOG while $_AH_HERMETIC_FILE ran; the file does not name that unit, so a run beside it wrote them and this run is not failed:" >&2
        printf '%s' "$_hx_other" | head -1 | cut -c1-200 >&2
      fi
    fi
  fi
  case "$_AH_HERMETIC_DIR" in /tmp/ahtt.?*) chmod -R u+w "$_AH_HERMETIC_DIR" 2>/dev/null; rm -rf "$_AH_HERMETIC_DIR" ;; esac
  case "$_AH_HERMETIC_TMP" in /tmp/ahprobe.??????) chmod -R u+w "$_AH_HERMETIC_TMP" 2>/dev/null; rm -rf "$_AH_HERMETIC_TMP" ;; esac
  [ "$_hx_leaked" = 1 ] && _hx_rc=1
  exit "$_hx_rc"
}
trap _hermetic_exit EXIT
trap 'exit 143' TERM
trap 'exit 130' INT
trap 'exit 129' HUP
