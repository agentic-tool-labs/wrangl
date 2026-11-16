#!/bin/sh
# ah-call-latency.sh [--dry-run] [--runs N] [--timeout SECONDS] [AH [AHD_DLL]]
#
# Wall time of five ah entry points, N runs each (default 20), one line per entry point on stdout:
#   <name> p50=<ms> p95=<ms> max=<ms>
# A call that times out or exits non-zero still counts at the time it took; the line then ends with " failed=<n>".
#
# How the five were chosen (no activity log is read; this needs none):
#   hooks/hooks.json registers a PreToolUse gate chain on every Bash call, so the gates on that chain are the hook entry points that
#   run most often. One of them is timed here, through the native binary the way bin/ah-hook runs it (`dotnet AHD_DLL hook <name>`,
#   which leaves out ah-hook's own sh start-up):
#     pretooluse-ah-cli
#   The four read-only verbs an agent runs most are timed through the installed wrapper (`AH <verb>`, sh start-up included):
#     --version, roster whoami, roster status, roster doctor
#   The hook rows feed a Bash `true` call on stdin; the gates only decide, but a gate may append to its own gate log under the ah home,
#   which this script cannot prevent.
#
# AH defaults to ~/.local/bin/ah, AHD_DLL to the Release build under agent-hierarchy-v2/native/artifacts/bin/Ah.Cli/release/ahd.dll.
# --dry-run only prints the commands it would time. Each call runs under a hard timeout (default 30 s) and its output goes to
# /dev/null. Scratch is one mktemp directory under $TMPDIR, removed on exit; nothing runs in the background.
# Timing and the timeout are done by perl (in macOS and Linux base installs), so no shell clock is needed.

dry=0
runs=20
limit=30
while [ "$#" -gt 0 ]; do
  case "$1" in
    --dry-run) dry=1; shift ;;
    --runs) runs=$2; shift 2 ;;
    --timeout) limit=$2; shift 2 ;;
    --) shift; break ;;
    -*) echo "ah-call-latency: unknown flag $1" >&2; exit 2 ;;
    *) break ;;
  esac
done

here=$(cd "$(dirname "$0")" && pwd -P) || exit 2
ah=${1:-$HOME/.local/bin/ah}
dll=${2:-$here/../native/artifacts/bin/Ah.Cli/release/ahd.dll}

case "$runs$limit" in
  '' | *[!0-9]*) echo "ah-call-latency: --runs and --timeout take whole numbers" >&2; exit 2 ;;
esac
[ "$runs" -ge 1 ] && [ "$limit" -ge 1 ] || { echo "ah-call-latency: --runs and --timeout must be at least 1" >&2; exit 2; }

gates="pretooluse-ah-cli"

if [ "$dry" -eq 1 ]; then
  for gate in $gates; do
    echo "$gate: dotnet $dll hook $gate < <Bash 'true' PreToolUse payload>   x$runs, timeout ${limit}s"
  done
  for verb in "--version" "roster whoami" "roster status" "roster doctor"; do
    echo "ah $verb: $ah $verb   x$runs, timeout ${limit}s"
  done
  exit 0
fi

[ -x "$ah" ] || { echo "ah-call-latency: $ah is not executable" >&2; exit 2; }
[ -f "$dll" ] || { echo "ah-call-latency: $dll not found (build: dotnet build -c Release)" >&2; exit 2; }
command -v perl >/dev/null 2>&1 || { echo "ah-call-latency: perl is needed for timing" >&2; exit 2; }
command -v dotnet >/dev/null 2>&1 || { echo "ah-call-latency: dotnet is needed for the hook rows" >&2; exit 2; }

base=${TMPDIR:-/tmp}
work=$(mktemp -d "${base%/}/ah-call-latency.XXXXXX") || exit 2
[ -n "$work" ] && [ -d "$work" ] || exit 2
trap 'rm -rf "$work"' EXIT HUP INT TERM

printf '%s\n' '{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"true"},"session_id":"ah-call-latency","cwd":"'"$PWD"'"}' >"$work/payload.json"
: >"$work/empty"

# once TIMEOUT STDIN CMD... : runs CMD with STDIN as its stdin and output to /dev/null; prints "<ms> <ok|failed>".
once() {
  perl -MTime::HiRes=time -e '
    my ($limit, $in, @cmd) = @ARGV;
    my $t0 = time;
    my $pid = fork();
    die "fork: $!" unless defined $pid;
    if (!$pid) {
      open(STDIN, "<", $in) or exit 126;
      open(STDOUT, ">", "/dev/null") or exit 126;
      open(STDERR, ">", "/dev/null") or exit 126;
      exec { $cmd[0] } @cmd or exit 127;
    }
    my $timed_out = 0;
    local $SIG{ALRM} = sub { $timed_out = 1; kill "KILL", $pid; };
    alarm $limit;
    waitpid($pid, 0);
    alarm 0;
    printf "%.1f %s\n", (time - $t0) * 1000, ($timed_out || $? != 0) ? "failed" : "ok";
  ' "$@"
}

# measure NAME STDIN CMD... : runs the call once unrecorded to warm the disk cache, then $runs times, and prints the line.
measure() {
  name=$1
  stdin=$2
  shift 2
  once "$limit" "$stdin" "$@" >/dev/null
  : >"$work/times"
  failed=0
  i=0
  while [ "$i" -lt "$runs" ]; do
    result=$(once "$limit" "$stdin" "$@")
    ms=${result% *}
    case "$result" in *failed) failed=$((failed + 1)) ;; esac
    echo "$ms" >>"$work/times"
    i=$((i + 1))
  done
  sort -n "$work/times" | awk -v n="$runs" -v name="$name" -v failed="$failed" '
    { t[NR] = $1 }
    END {
      p50 = t[int((n + 1) / 2)]
      k = int((n * 95 + 99) / 100)
      p95 = t[k]
      line = sprintf("%s p50=%.1f p95=%.1f max=%.1f", name, p50, p95, t[n])
      if (failed > 0) line = line " failed=" failed
      print line
    }'
}

for gate in $gates; do
  measure "$gate" "$work/payload.json" dotnet "$dll" hook "$gate"
done
measure "ah --version" "$work/empty" "$ah" --version
measure "ah roster whoami" "$work/empty" "$ah" roster whoami
measure "ah roster status" "$work/empty" "$ah" roster status
measure "ah roster doctor" "$work/empty" "$ah" roster doctor
