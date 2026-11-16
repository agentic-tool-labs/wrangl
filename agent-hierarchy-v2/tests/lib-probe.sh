#!/usr/bin/env bash
# Shared sandbox and tmux helpers for the live probe scripts (probe-r15-resume.sh).
# Sourced, never run. The probes launch real claude and codex sessions and cost tokens, so run-all.sh never runs them.
# The one test that sources this is test-probe-cleanup.sh, which drives only the sandbox, login-copy and cleanup helpers
# with a fake login and no session.
#
# Every rule below is mandatory: any failed check exits before a session starts.
#   exit 0 done, 1 script/sandbox error, 2 MISSING <tool>, 3 NO-AUTH <kind> (token present but rejected),
#   4 NO-PLUGIN, 5 NO-TOKEN <reason>. 2-5 are STOPs.
#   Exit 1 is a hard STOP too, and a leak check that exits 1 is never overridden by a verdict line: a login value outside
#   Codex's own store under the throwaway home (a capture, a log, a fixture) fails the run, whatever else it proved.
#
# Auth: a long-lived token the user stored in ~/.config/ah/probe-token (or AH_PROBE_TOKEN_FILE). This file never
# creates, writes, copies or prints it. The token reaches claude only as a shell assignment on the pane's own
# launch line, which names the file and never the token. The script never exports it, and never runs under set -x.

PROBE_TESTS=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
PROBE_AH=$(cd "$PROBE_TESTS/.." && pwd -P)
PROBE_PLUGIN=$PROBE_AH
. "$PROBE_TESTS/lib-pid.sh"
PIDS=()
PIDSTAMPS=()
FIXTURES=()
SCREEN=
HAVE_CODEX=1

LOGIN_RE_claude='select login method|please run /login|not logged in|sign in to|log in to|anthropic console'
LOGIN_RE_codex='sign in with chatgpt|provide your own api key|log in with|sign in to codex'
TRUST_RE='do you trust|trust (this|the) (folder|files|contents|directory)'
FIRSTRUN_RE="choose the text style|let's get started|press enter to continue|security notes|enter to confirm|use recommended settings"

tm() { tmux -L ahprobe "$@"; }

# token_preflight: exit 5 NO-TOKEN <reason> unless the token file passes every check. Metadata only, the file is never read.
token_preflight() {
  TOKEN_FILE=${AH_PROBE_TOKEN_FILE:-$REAL_HOME/.config/ah/probe-token}
  local uid dir real repo common
  uid=$(id -u)
  [ -e "$TOKEN_FILE" ] || { echo "NO-TOKEN missing: $TOKEN_FILE"; exit 5; }
  [ -L "$TOKEN_FILE" ] && { echo "NO-TOKEN symlink: $TOKEN_FILE"; exit 5; }
  [ -f "$TOKEN_FILE" ] || { echo "NO-TOKEN not a regular file: $TOKEN_FILE"; exit 5; }
  [ "$(stat -f %u "$TOKEN_FILE")" = "$uid" ] || { echo "NO-TOKEN file not owned by uid $uid"; exit 5; }
  [ "$(stat -f %Lp "$TOKEN_FILE")" = 600 ] || { echo "NO-TOKEN file mode is $(stat -f %Lp "$TOKEN_FILE"), need 600"; exit 5; }
  dir=$(dirname "$TOKEN_FILE")
  [ "$(stat -f %u "$dir")" = "$uid" ] || { echo "NO-TOKEN directory not owned by uid $uid: $dir"; exit 5; }
  [ "$(stat -f %Lp "$dir")" = 700 ] || { echo "NO-TOKEN directory mode is $(stat -f %Lp "$dir"), need 700: $dir"; exit 5; }
  real=$(python3 -I -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "$TOKEN_FILE")
  repo=$(git -C "$PROBE_AH" rev-parse --show-toplevel 2>/dev/null)
  common=$(git -C "$PROBE_AH" rev-parse --git-common-dir 2>/dev/null)
  common=$(cd "$PROBE_AH" && cd "$common/.." 2>/dev/null && pwd -P)
  case "$real" in
    /private/tmp/ahprobe.*) echo "NO-TOKEN real path is under a probe sandbox"; exit 5 ;;
  esac
  for r in "$repo" "$common"; do
    [ -n "$r" ] && case "$real" in "$r"/*) echo "NO-TOKEN real path is inside the repository"; exit 5 ;; esac
  done
}

# What a script needs, set by the caller before probe_init:
#   PROBE_NEED_CLAUDE=1       the Claude token pre-flight and the claude binary (default)
#   PROBE_NEED_CODEX=1        the codex binary (MISSING codex, exit 2, when absent)
#   PROBE_NEED_CODEX_AUTH=1   the user's Codex login is copied into the throwaway CODEX_HOME (exit 6 pre-flight)
PROBE_NEED_CLAUDE=${PROBE_NEED_CLAUDE:-1}
PROBE_NEED_CODEX=${PROBE_NEED_CODEX:-0}
PROBE_NEED_CODEX_AUTH=${PROBE_NEED_CODEX_AUTH:-0}
CODEX_COPIED=0
CODEX_SHA0=

# codex_auth_preflight: exit 6 NO-CODEX-AUTH <reason> unless the user's Codex login passes every check. Metadata only: the
# file is never opened here, and the reason names the failed check, never the file's content.
codex_auth_preflight() {
  CODEX_AUTH_SRC=$REAL_HOME/.codex/auth.json
  local mode
  [ -e "$CODEX_AUTH_SRC" ] || { echo "NO-CODEX-AUTH missing"; exit 6; }
  [ -L "$CODEX_AUTH_SRC" ] && { echo "NO-CODEX-AUTH symlink"; exit 6; }
  [ -f "$CODEX_AUTH_SRC" ] || { echo "NO-CODEX-AUTH not a regular file"; exit 6; }
  [ "$(stat -f %u "$CODEX_AUTH_SRC")" = "$(id -u)" ] || { echo "NO-CODEX-AUTH not owned by the current user"; exit 6; }
  mode=$(stat -f %Lp "$CODEX_AUTH_SRC")
  [ $(( 8#$mode & 8#077 )) -eq 0 ] || { echo "NO-CODEX-AUTH group- or world-accessible (mode $mode)"; exit 6; }
}

# codex_auth_copy: the only place the login is copied. cp takes paths only; the copy is 0600 in a 0700 CODEX_HOME. The
# source is only ever read, never written back.
codex_auth_copy() {
  chmod 700 "$T/codex"
  cp -p "$CODEX_AUTH_SRC" "$T/codex/auth.json" && chmod 0600 "$T/codex/auth.json" || { echo "probe: the Codex login copy failed" >&2; exit 1; }
  CODEX_SHA0=$(shasum -a 256 "$T/codex/auth.json" | cut -d' ' -f1)
  CODEX_COPIED=1
}

# codex_auth_remove: first thing on every exit path (and again after the processes are stopped, in case one refreshed the login
# after the first removal). Adds the login's values to CODEX_PATTERNS, a shell variable that never touches the disk, notes
# whether Codex rotated its login during the run, and deletes the copy. Prints no value. The values are every string in the
# file of 16 characters or more that is not a timestamp, whatever its key: a token, an API key, an id token, an account id.
# A value holding line breaks is matched line by line, and a line under 16 characters is dropped: it would match unrelated
# files, and an empty pattern would match every file.
# CODEX_PAT_ERR=1 when the file could not be read or held none, so the leak check cannot pass vacuously.
# CODEX_PATTERNS holds one `<key name>\t<value line>` per line: the key name (the value's own key in the file) is what a
# leak report prints, never the value.
CODEX_PATTERNS=
CODEX_PAT_ERR=0
codex_auth_remove() {
  [ "$CODEX_COPIED" = 1 ] || return 0
  local sha1 vals
  if [ -f "$T/codex/auth.json" ]; then
    sha1=$(shasum -a 256 "$T/codex/auth.json" | cut -d' ' -f1)
    if vals=$(node -e 'const fs=require("fs");const out=[];(function w(o,k){if(typeof o==="string"){if(o.length>=16&&!/^\d{4}-\d{2}-\d{2}T[\d:.+Z-]+$/.test(o))for(const l of o.split(/\r?\n/))if(l.length>=16)out.push(String(k||"value").replace(/[^A-Za-z0-9_.-]/g,"_")+"\t"+l);}else if(o&&typeof o==="object")for(const x of Object.keys(o))w(o[x],x);})(JSON.parse(fs.readFileSync(process.argv[1],"utf8")),"");process.stdout.write(out.join("\n"));' "$T/codex/auth.json" 2>/dev/null) && [ -n "$vals" ]; then
      CODEX_PATTERNS="$CODEX_PATTERNS$vals"$'\n'
    else
      CODEX_PAT_ERR=1
    fi
    [ "$sha1" != "$CODEX_SHA0" ] && CODEX_ROTATED=1
    rm -f "$T/codex/auth.json"
  fi
}

# codex_leak_check: after every probe process is gone. Paths and key names only, never a value. Codex's own store (`$T/codex`, the
# throwaway CODEX_HOME) is out of scope: Codex keeps login-derived values there itself, and it goes with the sandbox, the login
# copy having been removed first. Everything else is in scope, and a hit there fails the run (returns 1): the rest of `$T`, the
# fixtures this run writes and tests/fixtures. A matching file outside `$T` is deleted. A login with nothing extractable fails the
# check instead of passing it. The store's own hits are one info line (key names and a count) that leaves the result alone.
codex_leak_check() {
  [ "$CODEX_COPIED" = 1 ] || return 0
  if [ "$CODEX_PAT_ERR" = 1 ] || [ -z "${CODEX_PATTERNS//[$'\n']/}" ]; then
    echo "probe: CODEX LOGIN LEAK CHECK NOT DONE: no value could be extracted from the copied login" >&2
    return 1
  fi
  local roots=() d key val f report="" info_keys="" info_n=0
  for d in "$T"/* "$T"/.[!.]*; do
    [ -e "$d" ] || continue
    [ "$d" = "$T/codex" ] && continue
    roots+=("$d")
  done
  roots+=("${FIXTURES[@]}" "$PROBE_AH/tests/fixtures")
  while IFS=$'\t' read -r key val; do
    [ "${#val}" -ge 16 ] || continue
    while IFS= read -r f; do
      [ -n "$f" ] || continue
      report="$report  $f  key=$key"$'\n'
      case "$f" in "$T"/*) ;; *) rm -f "$f" ;; esac
    done < <(grep -rlF -f <(printf '%s\n' "$val") "${roots[@]}" 2>/dev/null | sort -u)
    if [ -d "$T/codex" ]; then
      n=$(grep -rlF -f <(printf '%s\n' "$val") "$T/codex" 2>/dev/null | wc -l | tr -d ' ')
      if [ "$n" -gt 0 ]; then
        info_n=$((info_n + n))
        case " $info_keys " in *" $key "*) ;; *) info_keys="$info_keys $key" ;; esac
      fi
    fi
  done <<<"$CODEX_PATTERNS"
  if [ "$info_n" -gt 0 ]; then
    echo "probe: info: Codex's own store under the throwaway home holds login-derived values in $info_n file(s) (keys:$info_keys); out of scope, removed with the sandbox" >&2
  fi
  [ -z "$report" ] && return 0
  echo "probe: CODEX LOGIN LEAK, a file outside Codex's own store holds a login value. Paths and key names only:" >&2
  printf '%s' "$report" >&2
  return 1
}

probe_init() {
  REAL_HOME=$HOME
  local tool
  for tool in tmux; do
    command -v "$tool" >/dev/null 2>&1 || { echo "MISSING $tool"; exit 2; }
  done
  [ "$PROBE_NEED_CLAUDE" = 1 ] && { command -v claude >/dev/null 2>&1 || { echo "MISSING claude"; exit 2; }; }
  command -v codex >/dev/null 2>&1 || HAVE_CODEX=0
  [ "$PROBE_NEED_CODEX" = 1 ] && [ "$HAVE_CODEX" = 0 ] && { echo "MISSING codex"; exit 2; }
  [ "$PROBE_NEED_CLAUDE" = 1 ] && token_preflight
  [ "$PROBE_NEED_CODEX_AUTH" = 1 ] && codex_auth_preflight
  T=$(mktemp -d /private/tmp/ahprobe.XXXXXX)
  case "$T" in
    /private/tmp/ahprobe.?*) ;;
    *) echo "probe: refusing sandbox dir '$T'" >&2; exit 1 ;;
  esac
  trap probe_cleanup EXIT
  trap 'exit 130' INT TERM HUP
  unset TMUX TMUX_PANE HERDR_ENV HERDR_PANE_ID HERDR_TAB_ID HERDR_WORKSPACE_ID CLAUDE_CONFIG_DIR CLAUDE_CODE_OAUTH_TOKEN
  export AH_TEST=1 HOME="$T/home" AH_HOME="$T/ah" CODEX_HOME="$T/codex" TMUX_TMPDIR="$T/tmux"
  mkdir -p "$HOME" "$AH_HOME" "$CODEX_HOME" "$TMUX_TMPDIR" "$T/ws" "$T/bin" "$T/cap"
  chmod 700 "$TMUX_TMPDIR"
  [ "$PROBE_NEED_CODEX_AUTH" = 1 ] && codex_auth_copy
  # A setting, not a credential: suppresses claude's onboarding screens (assumption: it does; the first-run
  # dialog handler in reach_ready is the fallback).
  printf '{"hasCompletedOnboarding": true}\n' >"$HOME/.claude.json"
  cat >"$T/bin/herdr" <<SHIM
#!/bin/sh
echo "probe: herdr is forbidden"
echo "called: \$*" >>"$T/herdr-called"
exit 99
SHIM
  chmod +x "$T/bin/herdr"
  export PATH="$T/bin:$PATH"
}

# Token leak check: grep reads its patterns from the token file (a CR stripped, as token_scrub does, and blank lines dropped,
# since an empty pattern matches everything), so the token is never in an argv. Matching fixtures are deleted; only paths are printed.
leak_check() {
  local hits f rc=0
  hits=$(grep -rlF -f <(tr -d '\r' <"$TOKEN_FILE" | grep -v '^[[:space:]]*$') "$T" "${FIXTURES[@]}" 2>/dev/null)
  [ -z "$hits" ] && return 0
  echo "probe: TOKEN LEAK, a file holds the token. Paths only:" >&2
  for f in $hits; do
    echo "  $f" >&2
    case "$f" in "$T"/*) ;; *) rm -f "$f" ;; esac
  done
  return 1
}

# Idempotent: runs on every exit path. Keeps the run's own exit code unless cleanup itself fails.
# Every process check is scoped to this run: its sandbox path and the pids it recorded.
probe_cleanup() {
  local rc=$? left p
  trap '' INT TERM HUP
  trap - EXIT
  [ -n "${T:-}" ] || exit "$rc"
  case "$T" in /private/tmp/ahprobe.?*) ;; *) exit "$rc" ;; esac
  codex_auth_remove
  tm kill-server >/dev/null 2>&1
  local i
  for i in "${!PIDS[@]}"; do ah_signal_stamped TERM "${PIDS[$i]}" "${PIDSTAMPS[$i]:-}"; done
  if [ -S "$AH_HOME/run/ahd.sock" ]; then
    for p in $(lsof -t "$AH_HOME/run/ahd.sock" 2>/dev/null); do ah_signal_ours TERM "$p" "$AH_HOME"; done
  fi
  sleep 1
  left=$(pgrep -f "$T" | tr '\n' ' ')
  if [ -n "$left" ]; then
    echo "probe: processes still matching the sandbox, killing: $left" >&2
    for p in $left; do ah_signal_ours TERM "$p" "$T"; done
    sleep 1
    left=$(pgrep -f "$T" | tr '\n' ' ')
    [ -n "$left" ] && { echo "probe: STILL ALIVE after kill: $left" >&2; rc=1; }
  fi
  left=
  for i in "${!PIDS[@]}"; do
    p=${PIDS[$i]}
    if [ -n "$p" ] && [ -n "${PIDSTAMPS[$i]:-}" ] && [ "$(ah_pid_stamp "$p")" = "${PIDSTAMPS[$i]}" ]; then
      left="$left $p"; ah_signal_stamped KILL "$p" "${PIDSTAMPS[$i]}"
    fi
  done
  [ -n "$left" ] && { echo "probe: recorded pids still alive (killed -9):$left" >&2; rc=1; }
  # Last, with nothing left to refresh the login: a value written after the first removal is read before the file goes.
  codex_auth_remove
  if [ -s "$T/herdr-called" ]; then
    echo "probe: herdr shim was called (exit 99): $(tr '\n' ';' <"$T/herdr-called")" >&2
    rc=1
  fi
  [ "$PROBE_NEED_CLAUDE" = 1 ] && { leak_check || rc=1; }
  codex_leak_check || rc=1
  if [ "${CODEX_ROTATED:-0}" = 1 ]; then
    echo "codex-auth-rotated: the probe's Codex session refreshed its login; if Codex later asks you to log in, run 'codex login'" >&2
  fi
  case "$T" in /private/tmp/ahprobe.?*) rm -rf "$T" ;; esac
  if [ "$CODEX_COPIED" = 1 ] && [ -e "$T" ]; then
    echo "codex auth copy survived at $T/codex/auth.json" >&2
    rc=1
  fi
  exit "$rc"
}

scrub() {
  sed -E -e "s|$T|<T>|g" -e "s|$REAL_HOME|<HOME>|g" \
    -e 's/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/<email>/g'
}

sha() { shasum -a 256 | cut -d' ' -f1; }

versions_json_kv() {
  printf 'claude\t%s\ntmux\t%s\ncodex\t%s\n' "$(command -v claude >/dev/null 2>&1 && claude --version 2>&1 | head -1 || echo none)" "$(tmux -V)" \
    "$([ "$HAVE_CODEX" = 1 ] && codex --version 2>&1 | head -1 || echo not-installed)"
}

# probe_record_pid <pid>: remembers a process for cleanup with its start time, so cleanup signals it only while it is still that process.
probe_record_pid() { PIDS+=("$1"); PIDSTAMPS+=("$(ah_pid_stamp "$1")"); }

# launch <session> <cwd> <command string> -> pane id in $PANE; the pane pid is recorded for cleanup.
launch() {
  local sess=$1 dir=$2 cmd=$3
  mkdir -p "$dir"
  PANE=$(tm new-session -d -s "$sess" -x "${PROBE_COLS:-200}" -y 50 -c "$dir" -P -F '#{pane_id}' "$cmd")
  [ -n "$PANE" ] || { echo "probe: tmux new-session failed for $sess" >&2; exit 1; }
  tm set-option -w -t "$PANE" remain-on-exit on
  probe_record_pid "$(tm display -p -t "$PANE" '#{pane_pid}')"
}

# claude_launch <claude args...>: the pane's launch line. Names the token file, never the token, and leaves the token
# as the only credential the session sees.
claude_launch() {
  echo "unset ANTHROPIC_API_KEY ANTHROPIC_AUTH_TOKEN; CLAUDE_CODE_OAUTH_TOKEN=\"\$(cat '$TOKEN_FILE')\" exec claude $*"
}

claude_cmd() { # extra flags...
  claude_launch "--agent ${PROBE_AGENT:-ah:architect} --name probe-$PROBE_NAME --plugin-dir '$PROBE_PLUGIN' --model haiku $*"
}

codex_cmd() {
  local m=; [ -n "${PROBE_CODEX_MODEL:-}" ] && m="-m $PROBE_CODEX_MODEL"
  # Production's `manual` mode (read-only, on-request): the denied `touch` needs approval, which is e4r state 4.
  echo "exec codex --sandbox read-only --ask-for-approval on-request $m"
}

pane_dead() { [ "$(tm display -p -t "$1" '#{pane_dead}' 2>/dev/null)" != 0 ]; }
cap() { tm capture-pane -p -t "$1"; }

# settle <pane> [cap-seconds]: two captures 500 ms apart identical -> 0, else 1. Screen in $SCREEN.
settle() {
  local pane=$1 end=$((SECONDS + ${2:-30})) a b
  a=$(cap "$pane")
  while :; do
    sleep 0.5
    b=$(cap "$pane")
    [ "$a" = "$b" ] && { SCREEN=$b; return 0; }
    a=$b
    [ "$SECONDS" -ge "$end" ] && { SCREEN=$b; return 1; }
  done
}

has() { printf '%s' "$SCREEN" | grep -qiE -e "$1"; }

# An idle composer: the prompt glyph followed by text or nothing (a numbered menu is a prompt, not the composer).
is_ready() {
  local kind=$1
  has "$TRUST_RE" && return 1
  has "$FIRSTRUN_RE" && return 1
  case "$kind" in
    # Claude draws a non-breaking space after the glyph; read it as a plain space here only, fixtures stay exact.
    claude) printf '%s' "$SCREEN" | sed $'s/\xc2\xa0/ /g' | grep -qE '^[[:space:]│]*❯ *([^0-9[:space:]│]|│|$)' ;;
    codex) printf '%s' "$SCREEN" | sed $'s/\xc2\xa0/ /g' | grep -qE '^[[:space:]│]*(›|»)[[:space:]]*([^0-9[:space:]│]|│|$)' ;;
  esac
}

# DIALOG_CB <kind> <flavor trust|firstrun> is called with $SCREEN set before the Enter that dismisses a dialog.
DIALOG_CB=
FIRSTRUN_SEEN=0

# reach_ready <kind> <pane> [cap-seconds]
# Handles trust / first-run dialogs by pressing Enter (sandbox pane only), at most 5 first-run screens.
# 0 ready, 1 not reached, 2 pane died. A login screen exits 3 (the token was present but rejected), so call this
# from the main shell, never inside $( ).
reach_ready() {
  local kind=$1 pane=$2 end=$((SECONDS + ${3:-90})) re trust_tries
  re=LOGIN_RE_$kind
  while [ "$SECONDS" -lt "$end" ]; do
    settle "$pane" 30
    if has "${!re}"; then
      printf '%s\n' "$SCREEN" | scrub >&2
      echo "NO-AUTH $kind"
      exit 3
    fi
    if pane_dead "$pane"; then return 2; fi
    if has "$TRUST_RE"; then
      [ -n "$DIALOG_CB" ] && "$DIALOG_CB" "$kind" trust
      # Claude's trust dialog opens with "No, exit" highlighted and a bare Enter quits: move the highlight to
      # "Yes, I trust this folder" and confirm it on screen before the Enter. Never a bare Enter onto "No".
      trust_tries=0
      until printf '%s' "$SCREEN" | sed $'s/\xc2\xa0/ /g' | grep -qE '^[[:space:]]*(❯|›) *([0-9]\. *)?Yes'; do
        if [ "$trust_tries" -ge 3 ]; then
          printf '%s\n' "$SCREEN" | scrub >&2
          echo "probe: the trust dialog highlight never reached 'Yes'; no Enter sent" >&2
          exit 1
        fi
        tm send-keys -t "$pane" Down
        settle "$pane" 5
        trust_tries=$((trust_tries + 1))
      done
      tm send-keys -t "$pane" Enter
      continue
    fi
    if is_ready "$kind"; then return 0; fi
    if has "$FIRSTRUN_RE"; then
      FIRSTRUN_SEEN=$((FIRSTRUN_SEEN + 1))
      [ "$FIRSTRUN_SEEN" -gt 5 ] && return 1
      [ -n "$DIALOG_CB" ] && "$DIALOG_CB" "$kind" firstrun
      tm send-keys -t "$pane" Enter
      continue
    fi
    sleep 1
  done
  return 1
}

# wait_ready <kind> <pane> [cap-seconds]: settled screen with an idle composer.
wait_ready() {
  local kind=$1 pane=$2 end=$((SECONDS + ${3:-60}))
  while [ "$SECONDS" -lt "$end" ]; do
    settle "$pane" 10
    pane_dead "$pane" && return 2
    is_ready "$kind" && return 0
  done
  return 1
}

# submit <pane> <text>: type literally, then Enter.
submit() {
  tm send-keys -t "$1" -l "$2"
  sleep 0.4
  tm send-keys -t "$1" Enter
}

# Hooks ran in the sandbox iff SessionStart left a trace: a hook payload, a mailbox registration under $AH_HOME
# (written only when the session resolves a mailbox address), a roster row (a peers.jsonl, or the roster_records table), or the session-roles
# file. Exit 4 NO-PLUGIN when none appears.
hook_trace() {
  [ -s "$T/hook-payloads.jsonl" ] && return 0
  ls "$AH_HOME"/registrations/*.json >/dev/null 2>&1 && return 0
  [ -n "$(find "$T" -name peers.jsonl 2>/dev/null | head -1)" ] && return 0
  # since per-dir-files-to-db S7 the roster row is a roster_records row
  [ -n "$(sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT 1 FROM roster_records LIMIT 1" 2>/dev/null)" ] && return 0
  [ -f "$HOME/.claude/ah2/agent-hierarchy.session-roles.json" ]
}
require_plugin() {
  local end=$((SECONDS + 20))
  while [ "$SECONDS" -lt "$end" ]; do
    hook_trace && return 0
    sleep 1
  done
  {
    echo "--- screen at NO-PLUGIN ---"
    cap "$PANE" | scrub
    echo "--- files under the sandbox (scrubbed) ---"
    find "$T" -not -path "$T/bin/*" -not -path "$T/tmux/*" -not -name '*.jsonl' 2>/dev/null | head -60 | scrub
    echo "--- session transcript lines (scrubbed, first 3) ---"
    head -3 "$(newest_transcript)" 2>/dev/null | cut -c1-400 | scrub
  } >&2
  echo "NO-PLUGIN: no SessionStart trace (payload, registration, peers.jsonl row, session-roles file) after the session reached its composer"
  exit 4
}

newest_transcript() { ls -t "$HOME"/.claude/projects/*/*.jsonl 2>/dev/null | head -1; }

# Sandbox-only SessionStart hook: appends each hook payload (stdin JSON) as one line to $T/hook-payloads.jsonl.
# Written into the sandboxed HOME, never the user's settings or the repo.
install_payload_hook() {
  mkdir -p "$HOME/.claude/ah2"
  cat >"$HOME/.claude/settings.json" <<JSON
{"hooks": {"SessionStart": [{"hooks": [{"type": "command", "command": "tr -d '\\\\n' >> '$T/hook-payloads.jsonl'; echo >> '$T/hook-payloads.jsonl'"}]}]}}
JSON
}

# Sandbox-only Stop hook for the context-window capture (key names and numbers only, never content): at each Stop it
# records the key names of the hook payload, then the model id, the key names under message.usage and the three input
# token counts of the newest assistant entry of the transcript, read at once and again 500 ms later (the flush race).
# One JSON line per Stop goes to $T/context-capture.jsonl. Added to the sandboxed HOME's settings, never the user's.
install_context_capture() {
  mkdir -p "$HOME/.claude/ah2"
  cat >"$T/bin/context-capture.py" <<'PY'
import json, sys, time

def newest(path):
    out = {"read": False, "assistant_with_usage": False}
    try:
        with open(path, "rb") as fh:
            fh.seek(0, 2)
            size = fh.tell()
            fh.seek(max(0, size - 262144))
            lines = fh.read().decode("utf-8", "replace").splitlines()
    except Exception:
        return out
    out["read"] = True
    for line in reversed(lines):
        try:
            entry = json.loads(line)
        except Exception:
            continue
        msg = entry.get("message") if isinstance(entry, dict) else None
        if not isinstance(msg, dict) or msg.get("role") != "assistant" or not isinstance(msg.get("usage"), dict):
            continue
        usage = msg["usage"]
        out.update({
            "assistant_with_usage": True,
            "model": msg.get("model") if isinstance(msg.get("model"), str) else None,
            "usage_keys": sorted(usage.keys()),
            "input_tokens": usage.get("input_tokens") if isinstance(usage.get("input_tokens"), int) else None,
            "cache_read_input_tokens": usage.get("cache_read_input_tokens") if isinstance(usage.get("cache_read_input_tokens"), int) else None,
            "cache_creation_input_tokens": usage.get("cache_creation_input_tokens") if isinstance(usage.get("cache_creation_input_tokens"), int) else None,
        })
        return out
    return out

try:
    payload = json.load(sys.stdin)
except Exception:
    payload = {}
path = payload.get("transcript_path") if isinstance(payload, dict) else None
rec = {"payload_keys": sorted(payload.keys()) if isinstance(payload, dict) else [], "has_transcript_path": isinstance(path, str) and bool(path)}
rec["at_stop"] = newest(path) if rec["has_transcript_path"] else None
time.sleep(0.5)
rec["after_500ms"] = newest(path) if rec["has_transcript_path"] else None
print(json.dumps(rec))
PY
  python3 -I - "$HOME/.claude/settings.json" "$T" <<'PY'
import json, os, sys
path, t = sys.argv[1:3]
try:
    doc = json.load(open(path))
except Exception:
    doc = {}
hook = {"type": "command", "command": "python3 -I '%s/bin/context-capture.py' >> '%s/context-capture.jsonl'" % (t, t)}
doc.setdefault("hooks", {}).setdefault("Stop", []).append({"hooks": [hook]})
json.dump(doc, open(path, "w"))
PY
}

payload_count() { grep -c . "$T/hook-payloads.jsonl" 2>/dev/null || echo 0; }

# payload_after <n>: "<session_id>\t<agent_type or ->\t<source or ->" of the newest payload line after line n, or "none".
# Only the fields the probe uses are read out.
payload_after() {
  python3 -I -c '
import json, sys
f, n = sys.argv[1], int(sys.argv[2])
rows = []
try:
    for line in open(f):
        if line.strip():
            rows.append(line)
except OSError:
    pass
new = rows[n:]
if not new:
    print("none")
else:
    try:
        d = json.loads(new[-1])
        print("%s\t%s\t%s" % (d.get("session_id") or "-", d.get("agent_type") or "-", d.get("source") or "-"))
    except Exception:
        print("none")' "$T/hook-payloads.jsonl" "$1"
}
