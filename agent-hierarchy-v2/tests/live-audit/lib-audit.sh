#!/usr/bin/env bash
# Live audit harness. Sourced by phase-*.sh, never run; run-all.sh only globs test-*.sh.
# Real sessions, real daemon, private sandbox. Never add lane-smoke.sh, `ah msg decision add|answer`, or anything in §3.5.
# A phase script: AUDIT_PHASE=<n>; . lib-audit.sh; audit_init <budget-min>; ...; record ...; exit via audit_exit (trap).
set -u

AUDIT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
AUDIT_TREE=$(cd "$AUDIT_DIR/../.." && pwd -P)
AUDIT_OUT="$AUDIT_TREE/docs/audits/ah-base-audit"
AUDIT_FILTER=${AUDIT_FILTER:-}          # space separated row ids; empty means every row
AUDIT_MAX_SESSIONS=${AUDIT_MAX_SESSIONS:-40}
SUBJECT_LINK=${SUBJECT_LINK:-$HOME/.claude/ah2/dogfood/marketplace/wrangl}
LIVE_HOME=$HOME
REAL_CLAUDE=$(command -v claude)
# Saved before the unset below, for the one real herdr read (real_herdr_hits). Nothing in the sandbox gets these.
REAL_HERDR=${HERDR_BIN_PATH:-$(command -v herdr 2>/dev/null)}
REAL_HERDR_SOCKET=${HERDR_SOCKET_PATH:-$HOME/.config/herdr/herdr.sock}
REAL_PATH=$PATH

# Our own session identity must not cross into the sandbox: AH_MEMBER_NAME and WRANGL_TEAM_FILE would make every sandbox `ah` call
# answer as a live team member. lib-probe.sh unsets only part of this. AUDIT_* survive.
for _v in $(env | cut -d= -f1 | grep -E '^(AH_|AHD_|CLAUDE|ANTHROPIC_|HERDR_|TMUX|MCP_)'); do unset "$_v"; done
unset _v

. "$AUDIT_DIR/../lib-probe.sh"
. "$AUDIT_DIR/lib-checkers.sh"

# ---- live state, read only
live_pid() { python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["pid"])' "$LIVE_HOME/.claude/ah2/run/ahd.json" 2>/dev/null || echo none; }
live_decision_lines() { wc -l <"$LIVE_HOME/.claude/ah2/state/decision-audit.jsonl" 2>/dev/null | tr -d ' ' || echo 0; }
live_db_hits() {
  local n=0 t c q
  for t in teams msg_messages mail_entries; do
    q=$(sqlite3 -readonly "$LIVE_HOME/.claude/ah2/state/ah.db" "select group_concat('coalesce(\"'||name||'\",'''')', '||') from pragma_table_info('$t')") || return 2
    c=$(sqlite3 -readonly "$LIVE_HOME/.claude/ah2/state/ah.db" "select count(*) from \"$t\" where ($q) like '%${1:-auditbox-}%'") || return 2
    n=$((n + c))
  done
  echo "$n"
}
live_hook_error_hits() { grep -cE "auditbox-|${T:-ahprobe-none}" "$LIVE_HOME/.claude/ah2/hierarchy/hook-errors.jsonl" 2>/dev/null || true; }

# ---- evidence and results
ev_dir() { mkdir -p "$T/evidence/$1"; printf '%s' "$T/evidence/$1"; }
# ev <id> <name>: stdin to an evidence file.
ev() { cat >"$(ev_dir "$1")/$2"; }
# token_scrub: stdin to stdout with the login token in $TOKEN_FILE replaced by <token> as a fixed string. The file's whitespace, a carriage
# return among it, is stripped first. The sed script comes through a process substitution from printf, a builtin, so the token is never in
# a process's argv. With no token file, or an empty one, the text passes as it is.
token_scrub() {
  local tok
  tok=$(tr -d '[:space:]' <"${TOKEN_FILE:-/dev/null}" 2>/dev/null | sed 's/[][\.*^$|/]/\\&/g')
  if [ -z "$tok" ]; then cat; return; fi
  sed -f <(printf 's|%s|<token>|g\n' "$tok")
}
want() { [ -z "$AUDIT_FILTER" ] && return 0; case " $AUDIT_FILTER " in *" $1 "*) return 0 ;; esac; return 1; }
# record <id> <function> <scenario> <PASS|FAIL|NOT-TESTED> <verdict text>. A row with no verdict text is refused.
record() {
  local id=$1 fn=$2 scen=$3 res=$4 why=$5
  case "$res" in PASS | FAIL | NOT-TESTED) ;; *) echo "audit: bad result '$res' for $id" >&2; exit 1 ;; esac
  [ -n "$why" ] || { echo "audit: no verdict text for $id" >&2; exit 1; }
  printf '%s\n' "$why" | ev "$id" verdict.txt
  printf '%s\t%s\t%s\t%s\n' "$id" "$fn" "$scen" "$res" >>"$T/rows.tsv"
  echo "ROW $id $res: $why"
}

# ---- sandbox (§3.1, §2)
sandbox_settings() {
  install_payload_hook
  python3 -I - "$HOME/.claude/settings.json" "$T" <<'PY'
import json, sys
path, t = sys.argv[1:3]
doc = json.load(open(path))
doc["permissions"] = {"allow": ["Bash", "Write", "Edit", "Read"], "defaultMode": "default"}
cap = {"type": "command", "command": "tr -d '\\n' >> '%s/cap/pretool.jsonl'; echo >> '%s/cap/pretool.jsonl'" % (t, t)}
doc["hooks"]["PreToolUse"] = [{"matcher": "AskUserQuestion|SendUserFile|PushNotification|ExitPlanMode", "hooks": [cap]}]
json.dump(doc, open(path, "w"))
PY
  install_context_capture
}

trust_dir() { # .claude.json trust flag, so nothing stalls on a dialog
  python3 -I - "$HOME/.claude.json" "$1" <<'PY'
import json, sys
path, d = sys.argv[1:3]
doc = json.load(open(path))
doc.setdefault("projects", {}).setdefault(d, {})["hasTrustDialogAccepted"] = True
json.dump(doc, open(path, "w"))
PY
}

write_shim() {
  cat >"$T/bin/claude" <<PY
#!/usr/bin/env python3
# §3.2 shim: records argv, pins the plugin copy, runs Haiku, keeps the token out of every argv, caps sessions.
import fcntl, json, os, sys
T = '$T'
REAL = '$REAL_CLAUDE'
TOKEN = '$TOKEN_FILE'
MAX = int(os.environ.get("AUDIT_MAX_SESSIONS", "$AUDIT_MAX_SESSIONS"))
argv = sys.argv[1:]
if argv and (argv[0] in ("--version", "-v", "--help", "-h", "plugin", "mcp", "auth", "doctor", "config")):
    os.execv(REAL, [REAL] + argv)
with open(T + "/cap/claude-argv.jsonl", "a+") as fh:
    fcntl.flock(fh, fcntl.LOCK_EX)
    fh.seek(0)
    count = sum(1 for _ in fh)
    fh.write(json.dumps({"argv": argv, "cwd": os.getcwd(), "n": count + 1, "refused": count >= MAX}) + "\n")
if count >= MAX:
    print("audit shim: session cap %d reached" % MAX, file=sys.stderr)
    sys.exit(97)
out, seen_model, i = [], False, 0
while i < len(argv):
    a = argv[i]
    if a == "--model" and i + 1 < len(argv):
        out += ["--model", "haiku"]; seen_model = True; i += 2; continue
    if a.startswith("--model="):
        out += ["--model", "haiku"]; seen_model = True; i += 1; continue
    out.append(a); i += 1
if not seen_model:
    out += ["--model", "haiku"]
if "--plugin-dir" not in out:
    out += ["--plugin-dir", T + "/plugin"]
if "--strict-mcp-config" not in out:
    out += ["--strict-mcp-config"]
# One debug log per session, so a failure can say whether SessionStart fired and what held it.
if not any(a == "--debug-file" or a.startswith("--debug-file=") for a in out):
    out += ["--debug-file", "%s/cap/claude-debug-%d.log" % (T, count + 1)]
os.environ.pop("ANTHROPIC_API_KEY", None)
os.environ.pop("ANTHROPIC_AUTH_TOKEN", None)
os.environ["CLAUDE_CODE_OAUTH_TOKEN"] = open(TOKEN).read().strip()
os.execv(REAL, [REAL] + out)
PY
  chmod +x "$T/bin/claude"
}

# Orchestrator ruling of 2026-11-01 over §3.4.1: every call exits 99 and is logged; a caught call of any verb is a finding (L16), never a
# stop. So nothing writes herdr-called. The trace holds time, argv and caller.
write_herdr_shim() {
  cat >"$T/bin/herdr" <<SHIM
#!/bin/sh
echo "probe: herdr is forbidden"
{ date -u +%H:%M:%S; echo "argv: \$*"; ps -o pid=,ppid=,command= -p \$PPID | cut -c1-200; } >>"$T/cap/herdr-trace.txt"
exit 99
SHIM
  chmod +x "$T/bin/herdr"
}

fixture_repo() {
  REPO="$T/ws/repo"
  mkdir -p "$REPO"
  git -C "$REPO" init -q -b main
  git -C "$REPO" config user.email audit@example.invalid
  git -C "$REPO" config user.name audit
  echo start >"$REPO/note.txt"
  git -C "$REPO" add note.txt
  git -C "$REPO" commit -q -m fixture
  git init -q --bare "$T/ws/remote.git"
  git -C "$REPO" remote add origin "$T/ws/remote.git"
  trust_dir "$REPO"
}

# audit_init <budget minutes>: pins the subject, builds the sandbox, starts the watchdog. Budget + 50% is the hard stop (§4).
audit_init() {
  local budget=$1 real
  LIVE_PID0=$(live_pid)
  DECISION0=$(live_decision_lines)
  real=$(python3 -I -c 'import os,sys;print(os.path.realpath(sys.argv[1]))' "$SUBJECT_LINK")
  PROBE_AGENT=wrangl:orchestrator
  PROBE_NAME="audit-$AUDIT_PHASE"
  probe_init
  trap audit_exit EXIT
  mkdir -p "$T/evidence" "$T/s" "$T/out"
  : >"$T/rows.tsv"
  cp -Rp "$real" "$T/plugin"
  export AH_TEST=1 AH_PLUGIN_ROOT="$T/plugin" AH_TEST_REAL_HOME="$REAL_HOME"
  PROBE_PLUGIN="$T/plugin"
  export AUDIT_T="$T"
  TEAM="auditbox-$AUDIT_PHASE"
  AH="$T/plugin/bin/ah"
  AHD="$T/plugin/bin/ahd"
  # A4: the binary the plugin's launcher resolves, from the pinned subject's own install.
  mkdir -p "$AH_HOME/bin"
  if [ -x "$LIVE_HOME/.claude/ah2/bin/ahd" ]; then cp -p "$LIVE_HOME/.claude/ah2/bin/ahd" "$AH_HOME/bin/ahd"; fi
  if [ -x "$LIVE_HOME/.claude/ah2/bin/ahd-tui" ]; then cp -p "$LIVE_HOME/.claude/ah2/bin/ahd-tui" "$AH_HOME/bin/ahd-tui"; fi
  export AH_BIN="$AH_HOME/bin/ahd"
  write_shim
  write_herdr_shim
  sandbox_settings
  fixture_repo
  # `ah` on PATH for prompts, by symlink to the copy.
  ln -s "$AH" "$T/bin/ah"; ln -s "$AHD" "$T/bin/ahd"
  perl -e '$p=getppid; $e=time+$ARGV[0]*90; while(time<$e){sleep 5; exit 0 if getppid!=$p} kill "TERM",$p' "$budget" &
  probe_record_pid $!
  mkdir -p "$T/evidence/phase-$AUDIT_PHASE"
  { "$AH" --version 2>&1; git -C "$AUDIT_TREE" rev-parse HEAD; } >"$T/evidence/phase-$AUDIT_PHASE/subject.txt"
  echo "audit: phase $AUDIT_PHASE sandbox up; budget ${budget} min, hard stop at $((budget * 3 / 2)) min"
}

# The only herdr call outside the shim: absolute path, `agent list`, real env values, bounded, one retry. Prints the count of agents
# named auditbox-* or with a cwd under this sandbox, or "absent" or "unproven". Never starts herdr; never records the user's other agents.
real_herdr_hits() {
  [ -n "$REAL_HERDR" ] && [ -S "$REAL_HERDR_SOCKET" ] || { echo absent; return 0; }
  local i out
  for i in 1 2; do
    out=$(env -i HOME="$LIVE_HOME" PATH="$REAL_PATH" HERDR_SOCKET_PATH="$REAL_HERDR_SOCKET" perl -e 'alarm 15; exec @ARGV' "$REAL_HERDR" agent list 2>/dev/null) || continue
    printf '%s' "$out" | python3 -I -c '
import json, sys
t = sys.argv[1]
ags = json.load(sys.stdin)["result"]["agents"]
print(sum(1 for a in ags if str(a.get("name") or "").startswith("auditbox-") or str(a.get("cwd") or "").startswith(t)))' "$T" 2>/dev/null && return 0
  done
  echo unproven
}

# canaries <label>. Stops only on real damage (ruling of 2026-11-01): real DB, real hook-errors, decision-audit, live daemon, real herdr's list.
# Everything else is a NOTE in the evidence. rc 1 on damage.
canaries() {
  local label=$1 f="$T/evidence/canaries-$AUDIT_PHASE-$1.txt" bad=0 n s
  mkdir -p "$T/evidence"
  note() { printf '%s\n' "$*" >>"$f"; }
  damage() { printf 'DAMAGE %s\n' "$*" >>"$f"; echo "CANARY-TRIP $label: $*"; bad=1; }
  note "ahd where: $("$AHD" where 2>&1)"
  s=$("$AH" msg daemon status --cwd "$REPO" 2>&1)
  note "daemon status: $(printf '%s' "$s" | head -3)"
  printf '%s' "$s" | grep -q "^root $T/plugin" || note "NOTE daemon status root not the sandbox copy"
  [ "$(live_pid)" = "$LIVE_PID0" ] || damage "live daemon pid changed: $LIVE_PID0 -> $(live_pid)"
  n=$(live_db_hits) || n=read-failed
  note "real DB auditbox- rows: $n"
  [ "$n" = 0 ] || damage "real DB: $n auditbox- rows"
  n=$(live_hook_error_hits)
  note "real hook-errors lines naming the sandbox: ${n:-0}"
  [ "${n:-0}" = 0 ] || damage "real hook-errors names the sandbox"
  [ "$(live_decision_lines)" = "$DECISION0" ] || damage "decision-audit.jsonl changed"
  n=$(real_herdr_hits)
  note "real herdr agents naming the sandbox: $n"
  case "$n" in 0 | absent | unproven) ;; *) damage "real herdr lists $n sandbox agents" ;; esac
  note "herdr calls caught by the shim so far: $(grep -c '^argv:' "$T/cap/herdr-trace.txt" 2>/dev/null || echo 0)"
  return "$bad"
}
canaries_or_stop() { canaries "$1" && return 0; AUDIT_ABORT="canary $1"; exit 8; }

start_daemon() {
  "$AHD" start >"$T/cap/ahd-start.txt" 2>&1
  poll 20 "[ -S '$AH_HOME/run/ahd.sock' ]" || return 1
  command -v herdr >>"$T/cap/herdr-at-daemon-start.txt"
  [ "$(command -v herdr)" = "$T/bin/herdr" ] || echo "audit: NOTE herdr at daemon start resolves to $(command -v herdr)"
}

# restart_daemon [ENV=VALUE...]: stop, by stamped pid if `ahd stop` leaves it up (seen every run; B08 decides), then start with those env values.
restart_daemon() {
  local dp st i
  dp=$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["pid"])' "$AH_HOME/run/ahd.json" 2>/dev/null)
  st=$(ah_pid_stamp "$dp")
  "$AHD" stop >>"$T/cap/restarts.txt" 2>&1
  for i in 1 2 3 4 5 6 7 8 9 10; do [ "$(ah_pid_stamp "$dp")" = "$st" ] || break; sleep 1; done
  if [ -n "$st" ] && [ "$(ah_pid_stamp "$dp")" = "$st" ]; then
    echo "pid $dp still up 10 s after stop; TERM" >>"$T/cap/restarts.txt"
    ah_signal_stamped TERM "$dp" "$st"
    for i in 1 2 3 4 5 6 7 8 9 10; do [ "$(ah_pid_stamp "$dp")" = "$st" ] || break; sleep 1; done
    [ "$(ah_pid_stamp "$dp")" = "$st" ] && { echo "pid $dp still up after TERM; KILL" >>"$T/cap/restarts.txt"; ah_signal_stamped KILL "$dp" "$st"; sleep 1; }
  fi
  rm -f "$AH_HOME/run/ahd.sock.stale"
  env "$@" "$AHD" start >>"$T/cap/restarts.txt" 2>&1
  poll 20 "[ -S '$AH_HOME/run/ahd.sock' ]"
}

# ---- driving (§3.2). Scripted prompts only; the commands live in a script so one turn carries one scenario.
# script <name>: stdin becomes $T/s/<name>.sh. Output and rc land in $T/out/<name>.{out,rc} when it runs.
script() {
  { echo '#!/bin/bash'; echo "AH='$AH'; AHD='$AHD'; REPO='$REPO'; TEAM='$TEAM'; T='$T'"; echo "exec >'$T/out/$1.out' 2>&1"; echo "trap 'echo \$? >\"$T/out/$1.rc\"' EXIT"; cat; } >"$T/s/$1.sh"
  chmod +x "$T/s/$1.sh"
  rm -f "$T/out/$1.rc" "$T/out/$1.out"
}
# poll <seconds> <shell test>: 0 when the test passes before the timeout.
poll() {
  local end=$((SECONDS + $1))
  shift
  while [ "$SECONDS" -lt "$end" ]; do
    eval "$*" >/dev/null 2>&1 && return 0
    sleep 2
  done
  return 1
}
# say <pane> <text>: submit once the composer is idle; a false return means the session never reached it.
say() { wait_ready claude "$1" "${3:-120}" || return 1; submit "$1" "$2"; }
# run_in <pane> <name> [seconds]: has the session run $T/s/<name>.sh. Re-prompts once (§3.2). 0 ran, 1 driver failure.
run_in() {
  local pane=$1 name=$2 cap=${3:-120}
  say "$pane" "Run exactly this one Bash command, then reply DONE: $T/s/$name.sh" || return 1
  poll "$cap" "[ -f '$T/out/$name.rc' ]" && return 0
  say "$pane" "Run exactly this one Bash command, then reply DONE: $T/s/$name.sh" 60 || return 1
  poll "$cap" "[ -f '$T/out/$name.rc' ]"
}
out_of() { cat "$T/out/$1.out" 2>/dev/null; }
rc_of() { cat "$T/out/$1.rc" 2>/dev/null; }
# do_in <pane> <name>: stdin as a script, run in that session, 0 when it ran. Output by out_of.
do_in() { script "$2"; run_in "$1" "$2" "${3:-120}"; }

# Base team: orch in a pane under the private tmux server. Roster template first: SessionStart reads it.
start_orch() {
  local extra=${1:-} p0
  [ -f "$REPO/.claude/ah2/agent-hierarchy.json" ] || "$AH" roster init repo --route peer --cwd "$REPO" >/dev/null 2>&1
  p0=$(payload_count)
  launch orch "$REPO" "$(claude_cmd "$extra")"
  ORCH=$PANE
  reach_ready claude "$ORCH" 120 || return 1
  poll 30 "[ \"\$(payload_after $p0)\" != none ]" || return 1
  ORCH_SID=$(payload_after "$p0" | cut -f1)
  ORCH_TX="$HOME/.claude/projects/$(printf '%s' "$REPO" | tr '/.' '--')/$ORCH_SID.jsonl"
}

# spawn_member <role> [extra spawn-one flags]: orch spawns it. Sets SPAWNED_NAME, SPAWNED_PANE, SPAWNED_TX. Names come from the spawn answer (§4).
SPAWN_N=0
SPAWN_BROKEN=
# A failed spawn prints why, leaves dead values so the row runs through, and finish then records NOT-TESTED driver.
spawn_member() {
  _spawn_member "$@" && return 0
  echo "audit: spawn of $1 failed: $(tail -5 "$T/out/spawn$SPAWN_N.out" 2>/dev/null | scrub | cut -c1-300)"
  SPAWN_BROKEN="spawn of $1 failed"; SPAWNED_PANE=%none; SPAWNED_NAME=spawn-failed; SPAWNED_TX=/dev/null; SPAWNED_SID=none
  return 1
}
_spawn_member() {
  local role=$1 extra=${2:-} name before p0 sid
  SPAWN_N=$((SPAWN_N + 1)); name="spawn$SPAWN_N"
  before=$(tm list-panes -a -F '#{pane_id}' | sort)
  p0=$(payload_count)
  script "$name" <<EOF
\$AH roster add repo --role $role --route peer --cwd "\$REPO" >/dev/null 2>&1
O=\$(\$AH roster spawn-one $role --team "\$TEAM" --model inherit $extra --cwd "\$REPO" 2>&1)
case "\$O" in *"already live"*) O=\$(\$AH roster spawn-ad-hoc $role --team "\$TEAM" --model inherit --replace-live $extra --cwd "\$REPO" 2>&1) ;; esac
printf '%s\n' "\$O"
EOF
  run_in "$ORCH" "$name" 180 || { echo "audit: $name: orch did not finish the script"; return 1; }
  SPAWNED_NAME=$(grep -oE "\"(name|member)\": *\"$TEAM-[^\"]*\"" "$T/out/$name.out" | head -1 | sed -E 's/.*: *"([^"]*)"/\1/')
  [ -n "$SPAWNED_NAME" ] || { echo "audit: spawn gave no name: $(tail -5 "$T/out/$name.out" | scrub)"; return 1; }
  local end=$((SECONDS + 60))
  SPAWNED_PANE=
  while [ "$SECONDS" -lt "$end" ] && [ -z "$SPAWNED_PANE" ]; do
    SPAWNED_PANE=$(tm list-panes -a -F '#{pane_id}' | sort | comm -13 <(printf '%s\n' "$before") - | head -1)
    sleep 2
  done
  [ -n "$SPAWNED_PANE" ] || { echo "audit: $name: no new pane"; return 1; }
  reach_ready claude "$SPAWNED_PANE" 120 || { echo "audit: $name: pane never ready"; return 1; }
  poll 30 "[ \"\$(payload_after $p0)\" != none ]" || { echo "audit: $name: no SessionStart payload"; return 1; }
  sid=$(payload_after "$p0" | cut -f1)
  SPAWNED_SID=$sid
  SPAWNED_TX="$HOME/.claude/projects/$(printf '%s' "$REPO" | tr '/.' '--')/$sid.jsonl"
}

# Kill by pane pid and its children; pids are ours, from our private server.
kill_pane() {
  local pp p; pp=$(tm display -p -t "$1" '#{pane_pid}')
  for p in $(pgrep -P "$pp" 2>/dev/null); do kill -9 "$p" 2>/dev/null; done
  kill -9 "$pp" 2>/dev/null
  poll 30 "pane_dead '$1'"
}

# ---- cost (§3.2): summed from transcript usage, de-duplicated by message id.
tokens_total() {
  python3 -I - "$HOME/.claude/projects" <<'PY'
import glob, json, os, sys
seen, tot = {}, {"input_tokens": 0, "output_tokens": 0, "cache_read_input_tokens": 0, "cache_creation_input_tokens": 0}
files = glob.glob(os.path.join(sys.argv[1], "**", "*.jsonl"), recursive=True)
for f in files:
    for line in open(f, errors="replace"):
        try:
            m = json.loads(line).get("message") or {}
        except Exception:
            continue
        u = m.get("usage") if isinstance(m, dict) else None
        if isinstance(u, dict) and m.get("id"):
            seen[m["id"]] = u
for u in seen.values():
    for k in tot:
        v = u.get(k)
        if isinstance(v, int):
            tot[k] += v
print(" ".join("%s=%d" % kv for kv in tot.items()), "transcripts=%d messages=%d" % (len(files), len(seen)))
PY
}

# ---- exit: end canaries, teardown, leak check on evidence, copy scrubbed text out, then probe_cleanup.
audit_exit() {
  local rc=$? f rel dest sessions dp i st
  trap - EXIT
  [ -n "${T:-}" ] || exit "$rc"
  if [ -n "${AH:-}" ] && [ -x "${AH:-/nonexistent}" ] && [ -S "$AH_HOME/run/ahd.sock" ]; then
    canaries end || { rc=8; AUDIT_ABORT=${AUDIT_ABORT:-canary end}; }
  fi
  if [ -n "${AUDIT_DEBUG_HOLD:-}" ]; then
    # Development aid: keep the sandbox up for inspection until the named file is removed, at most 10 minutes. Its rows do not count.
    printf '%s\n' "$T" >"$AUDIT_DEBUG_HOLD"; echo "audit: holding sandbox $T"
    poll 600 "[ ! -f '$AUDIT_DEBUG_HOLD' ]"
  fi
  tm kill-server >/dev/null 2>&1
  dp=$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["pid"])' "$AH_HOME/run/ahd.json" 2>/dev/null)
  if [ -n "$dp" ]; then
    # ah_signal_ours refuses $T as a needle (the binary lives under it), so the pid is signalled with its start time checked.
    st=$(ah_pid_stamp "$dp")
    "$AHD" stop >/dev/null 2>&1
    for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do [ "$(ah_pid_stamp "$dp")" = "$st" ] || break; sleep 1; done
    if [ -n "$st" ] && [ "$(ah_pid_stamp "$dp")" = "$st" ]; then
      echo "audit: sandbox daemon $dp still up 20 s after stop, KILL"
      ah_signal_stamped KILL "$dp" "$st"; sleep 1
    fi
    echo "audit: stopped sandbox daemon pid $dp"
  fi
  [ "$(live_pid)" = "$LIVE_PID0" ] || echo "audit: LIVE DAEMON PID CHANGED $LIVE_PID0 -> $(live_pid)"
  mkdir -p "$T/evidence/phase-$AUDIT_PHASE"
  sessions=$(grep -c . "$T/cap/claude-argv.jsonl" 2>/dev/null || echo 0)
  # §3.2: one line per run, reruns add up.
  { cat "$AUDIT_OUT/evidence/phase-$AUDIT_PHASE/tokens.txt" 2>/dev/null; echo "$(date -u +%FT%TZ) filter='$AUDIT_FILTER' $(tokens_total) sessions=$sessions"; } >"$T/evidence/phase-$AUDIT_PHASE/tokens.txt"
  cp "$T/cap/herdr-at-daemon-start.txt" "$T/evidence/phase-$AUDIT_PHASE/" 2>/dev/null
  cp "$T/cap/restarts.txt" "$T/evidence/phase-$AUDIT_PHASE/" 2>/dev/null
  [ -f "$T/cap/herdr-trace.txt" ] && cp "$T/cap/herdr-trace.txt" "$T/evidence/phase-$AUDIT_PHASE/herdr-trace.txt"
  [ -f "$T/cap/claude-argv.jsonl" ] && cp "$T/cap/claude-argv.jsonl" "$T/evidence/phase-$AUDIT_PHASE/argv.jsonl"
  if [ -n "${TOKEN_FILE:-}" ] && leak_check; then
    (cd "$T/evidence" && find . -type f) | while IFS= read -r f; do
      rel=${f#./}
      dest="$AUDIT_OUT/evidence/$rel"
      mkdir -p "$(dirname "$dest")"
      case "$rel" in */tokens.txt) cat "$T/evidence/$rel" ;; *) head -c 65536 "$T/evidence/$rel" ;; esac | LC_ALL=C sed -E -e "s|$T|<T>|g" -e "s|$REAL_HOME|<HOME>|g" \
        -e 's/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/<email>/g' >"$dest"
    done
    mkdir -p "$AUDIT_OUT/rows"
    if [ -s "$T/rows.tsv" ] && [ -z "${AUDIT_DEBUG_HOLD:-}" ]; then
      if [ -n "$AUDIT_FILTER" ] && [ -f "$AUDIT_OUT/rows/phase-$AUDIT_PHASE.tsv" ]; then
        # A filtered re-run replaces only the rows it ran.
        cut -f1 "$T/rows.tsv" >"$T/rerun-ids"
        awk -F'\t' 'NR==FNR{drop[$1]=1; next} !($1 in drop)' "$T/rerun-ids" "$AUDIT_OUT/rows/phase-$AUDIT_PHASE.tsv" >"$T/kept"
        cat "$T/kept" "$T/rows.tsv" >"$AUDIT_OUT/rows/phase-$AUDIT_PHASE.tsv"
      else
        cp "$T/rows.tsv" "$AUDIT_OUT/rows/phase-$AUDIT_PHASE.tsv"
      fi
    fi
  else
    echo "audit: token leak check failed or no token file; evidence NOT copied" >&2
    rc=1
  fi
  (exit "$rc")
  probe_cleanup
}
