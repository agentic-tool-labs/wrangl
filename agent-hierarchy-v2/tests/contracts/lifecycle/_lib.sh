#!/bin/bash
# Shared by the K5 scenario scripts (sourced; not a scenario itself). Every scenario runs under G3: a fresh
# /private/tmp/ahprobe.XXXXXX holds HOME, AH_HOME and TMPDIR; AH_TEST=1; the only removal is that directory, checked first.
# Both sides are the native binary: AHD_NATIVE_BIN (the native `ahd`, with its `.contract` sidecar beside it). Without it the
# scenario exits 77 and the runner reports it skipped.

LC_PLUGIN="${CONTRACT_PLUGIN:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)}"
IMPL_CLIENT=native
IMPL_DAEMON=native
LC_PASS=0; LC_FAIL=0; LC_PIDS=""

# The scenario supports a native client and a native daemon. Exits 77 when a native side is asked for without a binary.
lc_runtimes() {
  if [ "$IMPL_CLIENT" = native ] || [ "$IMPL_DAEMON" = native ]; then
    if [ -z "${AHD_NATIVE_BIN:-}" ] || [ ! -x "$AHD_NATIVE_BIN" ]; then echo "no native binary (AHD_NATIVE_BIN unset or not executable)"; exit 77; fi
  fi
}

# One `msg` verb, run from the project: `ahd ah msg`.
lc_msg() {
  (cd "$PROJ" && "$AHD_NATIVE_BIN" ah msg "$@" --cwd "$PROJ")
}

# One state operation through the native one-shot `ahd state` (request body on stdin, a fresh op id: the writer refuses a write
# without one). Args: <domain> <op> <args json> <hierarchy dir>. A background caller is killed by its subshell's pid, so the
# writer replaces the subshell.
lc_state() {
  ( cd "$PROJ"; exec "$AHD_NATIVE_BIN" state "$1" "$2" <<<"{\"args\":$3,\"context\":{\"cwd\":\"$PROJ\",\"hierarchy_dir\":\"$4\"},\"op_id\":\"lc-$$-$RANDOM\"}" )
}

lc_skip() { echo "SKIP: $1 ($2)"; }

# Re-runs the scenario with its shell named `claude` (the same pid after the exec), so the scenario's own shell ($$) is a
# Claude harness process: the routes admit its registrations and the caller-identity walk proves its session.
lc_as_harness() {
  [ "${LC_AS_HARNESS:-}" = 1 ] && return 0
  export LC_AS_HARNESS=1
  exec -a claude bash "$0" "$@"
}

# The start time of process <pid> as a registration and a team record record it: `ps -o lstart=` in the C locale, in UTC.
lc_start_of() { LC_ALL=C TZ=UTC ps -o lstart= -p "$1" | sed 's/^ *//;s/ *$//'; }

# The mailbox directory of member <name> of team file <file>: the team file's stem, `~`, the first 10 hex of the sha256 of
# its real path.
lc_box_dir() {
  node -e 'const c=require("crypto"),fs=require("fs"),p=require("path");const f=process.argv[1];let r=f;try{r=fs.realpathSync(f)}catch{}console.log(p.join(process.env.AH_HOME,"mailbox",p.basename(f).replace(/\.json$/,"")+"~"+c.createHash("sha256").update(r).digest("hex").slice(0,10),process.argv[2]))' "$1" "$2"
}

# A live registration of session <sid> holding <name>@<team> of team file <file>, for this scenario's shell: its pid and
# start time, the box directory the mailbox would give it.
lc_register_self() {
  local sid="$1" name="$2" team="$3" file="$4" box
  box="$(lc_box_dir "$file" "$name")"
  mkdir -p "$box"
  # the stores never read a planted file: the team file the scenario wrote and the registration go in through `ah db test-seed`
  [ -f "$file" ] && AH_DAEMON=off sh "$LC_PLUGIN/bin/ah" db test-seed teams --path "$file" <"$file" >/dev/null
  printf '{"session_id":"%s","boxes":[{"address":"%s@%s","dir":"%s"}],"team_file":"%s","harness":"claude","pid":%s,"pid_start":"%s","pane_id":null,"cwd":"%s","at":"2026-01-01T00:00:00.000Z"}\n' \
    "$sid" "$name" "$team" "$box" "$file" "$$" "$(lc_start_of $$)" "$PROJ" | AH_DAEMON=off sh "$LC_PLUGIN/bin/ah" db test-seed sessions >/dev/null
}

lc_init() {
  unset WRANGL_TEAM_FILE CLAUDE_PID CLAUDE_CODE_SESSION_ID HERDR_PANE_ID TMUX_PANE AH_DAEMON AHD_VERSION_OVERRIDE AHD_IDLE_EXIT_MS AHD_LEAK_RUN
  T="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
  case "$T" in /private/tmp/ahprobe.??????) ;; *) echo "lifecycle: mktemp returned '$T'; aborting" >&2; exit 2;; esac
  T="$(cd "$T" && pwd -P)"
  export HOME="$T/home" AH_HOME="$T/ah" TMPDIR="$T/tmp" AH_TEST=1
  PROJ="$T/proj"
  mkdir -p "$HOME" "$AH_HOME" "$TMPDIR" "$PROJ/.claude/ah2/teams"
  git -C "$PROJ" init -q
  export AH_PLUGIN_ROOT="$LC_PLUGIN"
  export AH_BIN="$AHD_NATIVE_BIN"
  trap lc_cleanup EXIT
}

# Stops every daemon this scenario started (by the pid in ahd.json and the ones it noted), then removes the sandbox.
lc_cleanup() {
  local p
  for p in $LC_PIDS "$(lc_info_pid)"; do [ -n "$p" ] && kill "$p" 2>/dev/null; done
  sleep 0.3
  for p in $(lc_daemon_pids); do kill "$p" 2>/dev/null; done
  sleep 0.3
  case "$T" in /private/tmp/ahprobe.??????) rm -rf "$T";; esac
}

lc_ctl() {
  (cd "$PROJ" && "$AHD_NATIVE_BIN" "$@" --cwd "$PROJ")
}
# The daemon started directly (no client, no lock), as the daemon's own entry point.
lc_direct_daemon() {
  "$AHD_NATIVE_BIN" serve --ah-home "$AH_HOME"
}
# Every daemon process of this AH_HOME.
lc_daemon_pids() { pgrep -f " serve --ah-home $AH_HOME" 2>/dev/null; }
lc_info_pid() { node -e 'try{console.log(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")).pid)}catch{}' "$AH_HOME/run/ahd.json" 2>/dev/null; }
lc_info_field() { node -e 'try{console.log(JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"))[process.argv[2]])}catch{}' "$AH_HOME/run/ahd.json" "$1" 2>/dev/null; }
lc_alive() { [ -n "$1" ] && kill -0 "$1" 2>/dev/null; }
lc_daemons() { lc_daemon_pids | wc -l | tr -d ' '; }
lc_health() { node -e '
const http=require("http");
const r=http.get({socketPath:process.argv[1],path:"/v1/health",timeout:1500},(res)=>{let b="";res.on("data",c=>b+=c);res.on("end",()=>{try{console.log(JSON.stringify(JSON.parse(b)))}catch{console.log("")}})});
r.on("error",()=>console.log(""));r.on("timeout",()=>{r.destroy();console.log("")});' "$AH_HOME/run/ahd.sock"; }
lc_wait_for() { # <seconds> <command...>: polls every 100 ms until the command succeeds
  local n=$(( $1 * 10 )); shift
  while [ "$n" -gt 0 ]; do "$@" && return 0; sleep 0.1; n=$((n-1)); done
  return 1
}
lc_check() { local n=$1; shift; if eval "$@"; then LC_PASS=$((LC_PASS+1)); echo "PASS: $n"; else LC_FAIL=$((LC_FAIL+1)); echo "FAIL: $n"; fi; }
lc_done() { echo "scenario: $LC_PASS passed, $LC_FAIL failed"; [ "$LC_FAIL" -eq 0 ]; }
lc_now_ms() { node -e 'console.log(Date.now())'; }
