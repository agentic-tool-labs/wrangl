#!/usr/bin/env bash
# End-to-end smoke test of one ship batch: real claude (Haiku), the real plugin at that build, real herdr, a real daemon.
#   tests/e2e/smoke.sh <built plugin tree | sha of a dogfood build>
#   tests/e2e/smoke.sh --self-test     the predicates only; no session, no daemon, no tokens
# Before go-live, use the tree form on a checkout of the commit: it copies the tree and builds the daemon inside the sandbox. The sha form finds only a
# build scripts/dogfood.sh already exported, and dogfood puts that build live on the next client, so it can only test what is already live.
# A checkout with no builds.jsonl row compares the mod's bytes to its own HEAD, so a mod edited and not committed fails row 1. A tree that is neither cannot compare them: row 1 ends NOT-TESTED and exit 4 is the best such a run can give.
# One line per row, and a results file. Exits:
#   0  all eight rows PASS
#   1  a FAIL: something is broken
#   2  bad argument
#   3  every row PASS but row 6, which opens the UI bridge: the daemon gives that only to a user at a terminal, so an agent's shell always ends here
#   4  the run proved nothing about a row, and no row failed: the session did not run a driver script, the real database is newer than the build,
#      the tree's version names no commit, the real daemon changed during the run (row 8), or the run was cut short by its budget or a signal.
#      Read the NOT-TESTED lines and run again; two in a row should be read as broken.
# Sandbox: a copy of the real home under /private/tmp/ahprobe.XXXXXX, a sandbox AH_TEST=1 daemon, and its own herdr server (XDG_CONFIG_HOME under
# the root, session "smoke"). Nothing here signals the real daemon. The user's herdr is only read, one `agent list`, by the canary.
# The one write outside the root: a daemon build restores into the real ~/.nuget/packages, a package cache.
# The daemon is built from the tree unless the installed binary reports the same version, so a tree with changed code at an installed version
# tests the installed binary: give the tree form a built version of its own.
SMOKE_BUDGET_S=${SMOKE_BUDGET_S:-1200}
# A driver that declines the relay leaves the brief to the fallback: relay_lease_ms (MailClaims.DefaultRelayLeaseMs), then about 60 s of member work, then a margin for the idle due cycles the claim waits for.
R3_RELAY_LEASE_S=180
R3_MEMBER_WORK_S=60
R3_MARGIN_S=60
R3_REPORT_WAIT_S=$((R3_RELAY_LEASE_S + R3_MEMBER_WORK_S + R3_MARGIN_S))
# The member waits up to 540 s for go-report (item body), longer than this plus the wait_ready calls before it.
ORCH_IDLE_WAIT_S=300
AUDIT_MAX_SESSIONS=4
SMOKE_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)
. "$SMOKE_DIR/../live-audit/user-rows.sh"
PROBE_NEED_CLAUDE=1
AUDIT_PHASE=smoke
TEAM=smoke-team
SESSION=smoke
START_S=$SECONDS

# ---- the subject
BUILDS="$REAL_AH/dogfood/builds.jsonl"
# build_field <match field> <value> <wanted field>: from the newest builds.jsonl row with that value.
build_field() {
  python3 -I - "$BUILDS" "$1" "$2" "$3" <<'PY'
import json, sys
path, key, val, want = sys.argv[1:]
hit = ""
for line in open(path):
    try:
        row = json.loads(line)
    except ValueError:
        continue
    if row.get(key) == val:
        hit = row.get(want, "")
print(hit)
PY
}
plugin_version() { python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]+"/.claude-plugin/plugin.json"))["version"])' "$1" 2>/dev/null; }
# resolve_subject <arg>: a tree holding a plugin, or a sha named in builds.jsonl and still in the plugin cache. Prints the tree.
resolve_subject() {
  local sha v
  if [ -d "$1" ]; then (cd "$1" && pwd -P); return; fi
  sha=$(git -C "$SMOKE_DIR" rev-parse --verify -q "$1^{commit}") || return 1
  v=$(build_field sha "$sha" version)
  [ -n "$v" ] && [ -d "$REAL_USER_HOME/.claude/plugins/cache/wrangl-dogfood/wrangl/$v" ] || return 1
  printf '%s\n' "$REAL_USER_HOME/.claude/plugins/cache/wrangl-dogfood/wrangl/$v"
}

# build_daemon: install.sh refuses a probe HOME, so its publish step and sidecar directly, into the sandbox's bin.
build_daemon() {
  local v c stage="$T/tmp/stage" n
  v=$(plugin_version "$T/plugin")
  [ "$("$AH_BIN" --version 2>/dev/null | awk '{print $2}')" = "$v" ] && return 0
  c=$(sed -n 's/.*"contract"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$T/plugin/native/cutover.json" | head -1)
  echo "smoke: building the daemon from the tree"
  DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false \
    NUGET_PACKAGES="$REAL_USER_HOME/.nuget/packages" perl -e 'alarm 480; exec @ARGV' bash "$T/plugin/scripts/publish-native.sh" --dotnet dotnet \
    --native "$T/plugin/native" --rid "osx-$(uname -m)" --out "$stage" --version "$v" --contract "$c" --name "$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["name"])' "$T/plugin/.claude-plugin/plugin.json")" \
    --artifacts "$stage/artifacts" >"$T/cap/build.txt" 2>&1 || return 1
  for n in ahd-tui ahd; do
    mv -f "$stage/$n" "$AH_HOME/bin/$n" || return 1
    printf '{"version":"%s","contract":%s}\n' "$v" "$c" >"$AH_HOME/bin/$n.contract"
  done
}

# ---- results: one line per row, in one file
record() {
  printf '%s\n' "$5" | ev "$1" verdict.txt
  printf '%-10s %-24s %s\n' "$4" "$1" "$5" | tee -a "$RESULTS"
}
not_run() { local why=$1 r; shift; for r in "$@"; do record "$r" - - NOT-TESTED "$why"; done; }

# ---- herdr: every call pinned under the root, and refused if it would reach a socket outside it
write_herdr_wrapper() {
  cat >"$T/bin/herdr" <<SHIM
#!/bin/sh
case "\${HERDR_SOCKET_PATH:-}" in "" | "$T"/*) ;; *) echo "herdr \$*" >>"$T/herdr-called"; echo "smoke: socket outside the root" >&2; exit 99 ;; esac
XDG_CONFIG_HOME="$T/config" exec "$REAL_HERDR" "\$@"
SHIM
  chmod +x "$T/bin/herdr"
}
h() { HERDR_SOCKET_PATH="$H_SOCK" "$T/bin/herdr" "$@"; }
pane_ids() { h pane list | python3 -I -c 'import json,sys; [print(p["pane_id"]) for p in json.load(sys.stdin)["result"]["panes"]]'; }
start_herdr() {
  export XDG_CONFIG_HOME="$T/config" SHELL=/bin/bash TERM=xterm-256color
  H_SOCK="$XDG_CONFIG_HOME/herdr/sessions/$SESSION/herdr.sock"
  mkdir -p "$XDG_CONFIG_HOME"
  # The client only holds the session open: a private tmux pane, so no window.
  rtm new-session -d -s herdr -x 200 -y 50 -c "$REPO" "'$T/bin/herdr' --session $SESSION"
  poll 20 "[ -S '$H_SOCK' ] && pane_ids | grep -q ." || return 1
  H_SERVER=$(lsof -t "$H_SOCK" 2>/dev/null | while read -r p; do ps -o command= -p "$p" | grep -q 'herdr server' && echo "$p"; done | head -1)
  [ -n "$H_SERVER" ] || return 1
  probe_record_pid "$H_SERVER"
}

# The audit lib drives tmux panes; here a session's pane is a herdr pane. The helpers above it (say, run_in, reach_ready, spawn_member) stay as they are.
rtm() { tmux -L ahprobe "$@"; }
cap() { h pane read "$1" --source visible 2>/dev/null; }
pane_dead() { pane_ids | grep -qxF "$1" && return 1; return 0; }
tm() {
  case "$1" in
    send-keys)
      date +%s000 | tr -d '\n' >>"$T/cap/typed.log"; echo " $3" >>"$T/cap/typed.log"
      if [ "$4" = -l ]; then h pane send-text "$3" "$5"; else h pane send-keys "$3" "$4"; fi ;;
    list-panes) pane_ids ;;
    *) rtm "$@" ;;
  esac
}

# ---- reading what happened
start_orch_herdr() {
  local pane
  "$AH" roster init repo --route peer --cwd "$REPO" >/dev/null 2>&1
  ORCH_P0=$(payload_count)
  pane=$(pane_ids | head -1)
  ORCH=$pane
  h pane run "$pane" "claude --agent wrangl:orchestrator --name smoke-orch" >/dev/null || return 1
  reach_ready claude "$ORCH" 120 || return 1
  orch_payload 30 || return 2
}
# orch_payload <seconds>: waits that long for the Orchestrator's SessionStart payload, then names its session and transcript.
orch_payload() {
  poll "$1" "[ \"\$(payload_after $ORCH_P0)\" != none ]" || return 1
  ORCH_SID=$(payload_after "$ORCH_P0" | cut -f1)
  ORCH_TX="$HOME/.claude/projects/$(printf '%s' "$REPO" | tr '/.' '--')/$ORCH_SID.jsonl"
}
# stall_summary <debug log>...: one line of what Claude Code's own detector logged of its main loop being held ("[event-loop-stall] blocked
# for <n>ms ..."), with the slowest ~/.claude.json write (its temp file to "written atomically"). Exits 0 when a stall was logged.
stall_summary() {
  cat "$@" 2>/dev/null | awk '
    function ms(ts,  a) { split(substr(ts, 12, 12), a, ":"); return (a[1] * 3600 + a[2] * 60 + a[3]) * 1000 }
    /\[event-loop-stall\] blocked for [0-9]+ms/ { match($0, /blocked for [0-9]+ms/); v = substr($0, RSTART + 12, RLENGTH - 14) + 0; n++; sum += v; if (v > max) max = v }
    /Writing to temp file: .*\.claude\.json\.tmp/ { w = ms($1) }
    /\.claude\.json written atomically/ && w != "" { d = ms($1) - w; if (d > wmax) wmax = d; w = "" }
    END { printf "event-loop stalls: %d (longest %d ms, total %d ms); slowest .claude.json write %d ms\n", n, max, sum, wmax; exit (n > 0 ? 0 : 1) }'
}
# orch_retry <start's exit>: after a payload miss, one more 60 s wait, only when the session's debug log shows its main loop stalled, so a
# miss from machine load is not read as hooks that did not run.
orch_retry() { [ "$1" = 2 ] && stall_summary "$T"/cap/claude-debug-*.log >/dev/null && orch_payload 60; }
# orch_turn_ended: the Orchestrator's last transcript row is the end of a turn (a stop_hook_summary may be followed by a continued turn, so not that).
orch_turn_ended() {
  python3 -I - "$ORCH_TX" <<'PY'
import json, sys
last = None
for line in open(sys.argv[1], errors="replace"):
    try:
        row = json.loads(line)
    except ValueError:
        continue
    if row.get("type") in ("assistant", "user", "system"):
        last = row
sys.exit(0 if last and last.get("type") == "system" and last.get("subtype") == "turn_duration" else 1)
PY
}
# wake_of <transcript> <entry>: for the user turn carrying the entry, "<seconds since the line before> <1 if the engine marks it a plugin prompt, else 0>"; "-1 0" when absent.
wake_of() {
  python3 -I - "$1" "$2" <<'PY'
import datetime, json, sys
path, entry = sys.argv[1:3]
prev = None
def when(row):
    t = row.get("timestamp")
    return datetime.datetime.fromisoformat(t.replace("Z", "+00:00")) if t else None
for line in open(path, errors="replace"):
    try:
        row = json.loads(line)
    except ValueError:
        continue
    t = when(row)
    if row.get("type") == "user" and "[ah-entry " + entry in line and t:
        print(int((t - prev).total_seconds()) if prev else -1, 1 if row.get("pluginPrompt") else 0)
        sys.exit(0)
    if t and row.get("type") in ("assistant", "user", "system"):
        prev = t
print(-1, 0)
PY
}
# Mail lives in the database, so it is read through ahd, one entry per line. A report's ref is its response file, whose name starts with the request id.
orch_entries() { "$AHD" inspect entries --address "orchestrator@$TEAM" --all 2>/dev/null | python3 -I -c 'import json,sys; [print(json.dumps(e,separators=(",",":"))) for e in json.load(sys.stdin)["entries"]]' 2>/dev/null; }
# `ahd inspect entries` gives only the latest attempt, without its `by`, and row 4 asserts `by`; no verb gives it, so this one reads the sandbox's database.
orch_attempts() { sqlite3 -readonly "$AH_HOME/state/ah.db" 'SELECT raw FROM mail_attempts' 2>/dev/null; }
report_entry() { orch_entries | grep -F "\"kind\":\"report\"" | grep -F "/$1--" | head -1 | sed -E 's/.*"id":"([^"]+)".*/\1/'; }
# shows_member_report: stdin is `ah msg show <request> response`. A line opening with the bullet; the brief quotes that bullet mid-line, so the brief's text is not a report.
shows_member_report() { grep -q '^- DONE'; }
# view_member_live <hierarchy dir>: stdin is /v1/view. 0 when that dir's Orchestrator row has an implementor that is not notlive (ViewBuilder MemberGraphState).
view_member_live() {
  python3 -I -c '
import json, sys
rows = json.load(sys.stdin).get("children") or []
sys.exit(0 if any(r.get("dir") == sys.argv[1] and any(m.get("role") == "implementor" and m.get("state") in ("live", "busy", "idle", "blocked") for m in r.get("cluster") or []) for r in rows) else 1)' "$1"
}
# run_exit <results file>: the exit code the header promises. Only the bridge's own refusal of an agent's shell gives 3.
run_exit() {
  if grep -q '^FAIL' "$1"; then echo 1
  elif [ "$(grep -cE '^PASS +R[1-8] ' "$1")" = 8 ]; then echo 0
  elif [ "$(grep -cE '^PASS +R[1-578] ' "$1")" = 7 ] && grep -qE '^NOT-TESTED +R6 .*opens only for a user' "$1"; then echo 3
  else echo 4; fi
}
# item_moved_by <member>: stdin is `checklist show --trail`. 0 when that member's report took the item out of in_progress: a DONE goes to reported, any other reply waits on the Orchestrator.
item_moved_by() {
  python3 -I -c '
import json, sys
rows = json.load(sys.stdin)["rows"]
sys.exit(0 if any(r["by"] == sys.argv[1] and r["from"] == "in_progress" and r["to"] in ("reported", "waiting_orch") for r in rows) else 1)' "$1"
}
# question_body <item>: the POST /ui/v1/answer body for that item, as web/ui/questions-model.js answerBody builds it. Empty when the daemon shows no such question.
question_body() {
  local d
  # ActivityLane.DirId: the first 8 hex of the SHA-256 of the hierarchy dir.
  d=$(printf '%s' "$REPO/.claude/ah2" | shasum -a 256 | cut -c1-8)
  # The page's own read (web/ui/store.js): subscribe to the slice on the events route. The first read takes the lease, a later one has the rows.
  local i
  for i in 1 2 3; do
    http --max-time 8 --unix-socket "$AH_HOME/run/ahd.sock" "http://x/v1/view?path=/events&sub=questions:$d/$TEAM&fresh=questions:$d/$TEAM&wait=2" >"$(ev_dir R6)/slice.json"
    grep -qF "$1" "$T/evidence/R6/slice.json" && break
    sleep 2
  done
  python3 -I - "$T/evidence/R6/slice.json" "$1" "$TEAM" "$d" <<'PY'
import json, sys
slice_path, item, team, d = sys.argv[1:]
qs = []
def walk(o):
    if isinstance(o, dict):
        if item in (o.get("item"), o.get("id")) and o.get("since"):
            qs.append(o)
        for v in o.values():
            walk(v)
    elif isinstance(o, list):
        for v in o:
            walk(v)
try:
    walk(json.load(open(slice_path)))
except ValueError:
    pass
if qs:
    q = qs[0]
    print(json.dumps({"dir": d, "team": team, "item": q.get("key") or q.get("id"), "since": q["since"], "choice": "a"}))
PY
}
trail() { script "$1" <<EOF
\$AH roster checklist show $2 --trail --team "\$TEAM" --cwd "\$REPO"
EOF
  run_in "$ORCH" "$1" 120
}

# mod_source <version> <tree>: "<commit> <mod blob>" at the commit this build came from: its builds.jsonl row, else the tree's own HEAD when the tree is a checkout. Fails when neither names one.
mod_source() {
  local sha blob
  sha=$(build_field version "$1" sha)
  if [ -n "$sha" ]; then
    blob=$(git -C "$SMOKE_DIR" rev-parse -q --verify "$sha:agent-hierarchy-v2/mod/register.tsx" 2>/dev/null) || return 1
  else
    sha=$(git -C "$2" rev-parse -q --verify HEAD 2>/dev/null) || return 1
    blob=$(git -C "$2" rev-parse -q --verify HEAD:./mod/register.tsx 2>/dev/null) || return 1
  fi
  echo "$sha $blob"
}

# ---- rows
row_1() {
  local v dv sha want_mod plugin_line
  begin R1
  v=$(plugin_version "$T/plugin"); dv=$("$AHD" --version 2>/dev/null | awk '{print $2}')
  { echo "subject: $SUBJECT"; echo "plugin.json: $v"; echo "ahd: $dv"; "$AH" msg daemon status --cwd "$REPO" 2>&1 | scrub; } | ev R1 versions.txt
  chk "plugin.json has a version" "[ -n '$v' ]"
  chk "the daemon's build is the plugin's ($dv vs $v)" "[ '$v' = '$dv' ]"
  chk "the daemon's plugin root is this copy" "'$AH' msg daemon status --cwd '$REPO' | grep -qF '$T/plugin'"
  plugin_line="ah CLI root (v$v): $T/plugin"
  chk "the session's own context names this root and version" "grep -qF '$plugin_line' '$ORCH_TX'"
  # The mod has no version of its own, so the one proof is its bytes against the file at the commit this build came from.
  if read -r sha want_mod < <(mod_source "$v" "$SUBJECT") && [ -n "$want_mod" ]; then
    chk "the session's mod is the file at $sha" "[ \"\$(git hash-object --no-filters '$T/plugin/mod/register.tsx')\" = '$want_mod' ]"
    finish smoke "plugin version, daemon build, mod bytes"
  elif [ -n "$FAILS" ]; then
    finish smoke "plugin version, daemon build, mod bytes" "$v is in no builds.jsonl row and $SUBJECT is no checkout, so the mod bytes were not compared"
  else
    record R1 - - NOT-TESTED "$v is in no builds.jsonl row and $SUBJECT is no checkout, so nothing names the commit to compare the mod to"
  fi
}

row_2() {
  begin R2
  local p0 reason=
  NAME=
  p0=$(payload_count)
  script spawn <<EOF
\$AH roster add repo --role implementor --route peer --cwd "\$REPO" >/dev/null 2>&1
\$AH roster spawn-one implementor --team "\$TEAM" --model inherit --allow-main --cwd "\$REPO" 2>&1
EOF
  run_in "$ORCH" spawn 180 || { driver smoke spawn "the Orchestrator did not run the spawn script"; return 1; }
  keep spawn
  if out_of spawn | grep -q 'newer than this binary'; then
    driver smoke spawn "the real database is newer than this build, so its copy cannot run: $(out_of spawn | head -1 | scrub | cut -c1-160)"
    return 1
  fi
  local name pane
  name=$(out_of spawn | sed -nE 's/^ *"name": *"([^"]+)".*/\1/p' | head -1)
  pane=$(out_of spawn | sed -nE 's/^ *"transport_id": *"([^"]+)".*/\1/p' | head -1)
  { [ -n "$name" ] && [ -n "$pane" ]; } || reason="the spawn answer named no member or pane"
  [ -n "$reason" ] || reach_ready claude "$pane" 120 || reason="the member's pane never reached its prompt"
  [ -n "$reason" ] || poll 30 "[ \"\$(payload_after $p0)\" != none ]" || reason="no SessionStart from the member"
  if [ -n "$reason" ]; then
    { h pane list; [ -z "$pane" ] || cap "$pane"; } 2>&1 | scrub | token_scrub | ev R2 screen.txt
    chk "$reason" false
    finish smoke "spawn one member" "spawn.txt, screen.txt"
    return 1
  fi
  NAME=$name; PANE_M=$pane
  SID_M=$(payload_after "$p0" | cut -f1)
  TX_M="$HOME/.claude/projects/$(printf '%s' "$REPO" | tr '/.' '--')/$SID_M.jsonl"
  # registrations and teams live in the state database: read them through ahd and ah, never as files
  MEMBER_PID=$("$AHD" inspect registrations 2>/dev/null | python3 -I -c 'import json,sys; print(next((str(r["pid"]) for r in json.load(sys.stdin)["registrations"] if r.get("session_id")==sys.argv[1] and r.get("pid")), ""))' "$SID_M" 2>/dev/null)
  "$AH" roster team show --team "$TEAM" --json --cwd "$REPO" >"$T/cap/team.json" 2>&1
  http --unix-socket "$AH_HOME/run/ahd.sock" 'http://x/v1/view' >"$T/cap/view.json" 2>&1
  { h pane list; cat "$T/cap/view.json"; } 2>&1 | scrub | ev R2 live.txt
  chk "a herdr pane of the member" "pane_ids | grep -qxF '$PANE_M'"
  chk "a registration with a live pid" "[ -n '$MEMBER_PID' ] && kill -0 '$MEMBER_PID'"
  chk "the team names it" "python3 -I -c 'import json,sys; d=json.load(open(sys.argv[1])); sys.exit(0 if any(m.get(\"name\")==sys.argv[2] for m in d.get(\"members\") or []) else 1)' '$T/cap/team.json' '$NAME'"
  chk "the daemon's view shows the member live" "view_member_live '$REPO/.claude/ah2' <'$T/cap/view.json'"
  finish smoke "spawn one member"
}

row_3() {
  begin R3
  ITEM_BODY="$T/b-smoke"
  cat >"$ITEM_BODY" <<EOF
Smoke test. Do no other work. Read this brief with: ah msg show <this brief's id>
Then wait for the go signal: run this one Bash command with timeout 600000: for i in \$(seq 270); do [ -f $T/go-report ] && break; sleep 2; done; ls $T/go-report
Then file your report as one bullet, "- DONE smoke", the way your contract says: ah msg fill, then ah msg send to orchestrator@$TEAM with --kind report --ref <this brief's id>.
EOF
  script brief <<EOF
A=\$(\$AH roster checklist add "smoke report item" --role implementor --team "\$TEAM" --cwd "\$REPO"); echo "\$A"
I=\$(printf '%s' "\$A" | sed -nE 's/.*"item": *"([^"]+)".*/\1/p' | head -1); echo "item=\$I"
N=\$(\$AH msg new --to implementor --from orchestrator --slug smoke-report --to-name $NAME --title "Run the smoke step" --team "\$TEAM" --eta small --item "_run/\$I" --body-file "$ITEM_BODY" --cwd "\$REPO"); echo "\$N"
ID=\$(printf '%s' "\$N" | sed -nE 's/.*"id": *"([^"]+)".*/\1/p' | head -1); echo "request=\$ID"
\$AH msg send "$NAME@\$TEAM" --ref "\$ID" --kind brief --json --cwd "\$REPO"
EOF
  if ! run_in "$ORCH" brief 120; then driver smoke brief "the Orchestrator did not run the brief script"; return 1; fi
  keep brief
  ITEM=$(out_of brief | sed -n 's/^item=//p'); REQ=$(out_of brief | sed -n 's/^request=//p')
  [ -n "$REQ" ] || { chk "ah msg new gave a request id" false; finish smoke "brief through the mailbox, report back" "brief.txt"; return 1; }
  wait_ready claude "$ORCH" 60
  # run_in told the driver to run one command and reply DONE, so it may not act on the relay that command printed. Asked here so it need not wait out the lease.
  say "$ORCH" "If that command's output has a line starting 'delivery: relay', make the SendMessage it names now, then reply DONE. Otherwise reply DONE." || true
  wait_ready claude "$ORCH" 60
  # R4 needs the Orchestrator idle when the report lands; under load its post-DONE turn (the liveness watchers) can outlast the member, so the member waits for go-report.
  if ! poll "$ORCH_IDLE_WAIT_S" "orch_turn_ended && wait_ready claude '$ORCH' 30 && orch_turn_ended"; then driver smoke idle "the Orchestrator did not go idle in ${ORCH_IDLE_WAIT_S}s after the brief"; return 1; fi
  touch "$T/go-report"
  MARK_MS=$(date +%s000)
  poll "$R3_REPORT_WAIT_S" "[ -n \"\$(report_entry '$REQ')\" ]"
  E_REPORT=$(report_entry "$REQ")
  grep -F 'msg show' "$TX_M" | scrub | cut -c1-600 | head -3 | ev R3 show-lines.txt
  # with no report, what the member did instead: its screen and its last tool calls, so a stuck member is told from a lost mail
  if [ -z "$E_REPORT" ]; then
    cap "$PANE_M" 2>&1 | scrub | token_scrub | ev R3 member-screen.txt
    python3 -I -c 'import json,sys
for l in open(sys.argv[1]):
    try: j=json.loads(l)
    except ValueError: continue
    c=(j.get("message") or {}).get("content")
    for b in c if isinstance(c, list) else []:
        if b.get("type")=="tool_use": print(b.get("name"), json.dumps(b.get("input"))[:300])
        elif b.get("type")=="tool_result": print("  ->", str(b.get("content"))[:300].replace("\n"," | "))' "$TX_M" 2>&1 | tail -40 | scrub | token_scrub | ev R3 member-tools.txt
  fi
  orch_entries | grep -F "/$REQ--" | scrub | ev R3 entries.txt
  chk "the member ran ah msg show on the brief" "tool_done '$TX_M' Bash 'msg show'"
  "$AH" msg show "$REQ" response --cwd "$REPO" 2>&1 | scrub | cut -c1-600 | ev R3 response.txt
  chk "ah msg show <request> response returns the member's body" "'$AH' msg show '$REQ' response --cwd '$REPO' 2>/dev/null | shows_member_report"
  chk "a report entry from the member" "grep -qF '$NAME@$TEAM' '$T/evidence/R3/entries.txt' && [ -n '$E_REPORT' ]"
  finish smoke "brief through the mailbox, report back"
}

# The driver types nothing into the Orchestrator after the brief script: tm logs every key, so that is checked, not assumed.
row_4() {
  local wake gap plugin typed
  begin R4
  if [ -z "${E_REPORT:-}" ]; then driver smoke wake "no report to ring with"; return; fi
  poll 180 "m04_lines '$E_REPORT' '$ORCH_TX' | grep -q '\"type\":\"user\"'"
  typed=$(awk -v m="$MARK_MS" -v p="$ORCH" '$1 > m && $2 == p' "$T/cap/typed.log" | wc -l | tr -d ' ')
  wake=$(wake_of "$ORCH_TX" "$E_REPORT"); gap=${wake% *}; plugin=${wake#* }
  m04_lines "$E_REPORT" "$ORCH_TX" | scrub | cut -c1-1500 | ev R4 tx-lines.txt
  { echo "typed into the Orchestrator since the brief script: $typed"; echo "idle seconds before the prompt: $gap"; } | ev R4 idle.txt
  chk "the report arrived as a user turn in the Orchestrator's transcript" "m04_lines '$E_REPORT' '$ORCH_TX' | grep -q '\"type\":\"user\"'"
  chk "nothing was typed since the brief" "[ '$typed' = 0 ]"
  chk "the session was idle first (${gap}s, so not a Stop-hook hand-off)" "[ '$gap' -ge 2 ]"
  chk "the engine marks the turn a plugin prompt" "[ '$plugin' = 1 ]"
  # The delivered row lands on a daemon tick after the prompt.
  poll 60 "orch_attempts | grep -F '$E_REPORT' | grep -q '\"state\":\"delivered\"'"
  orch_attempts | grep -F "$E_REPORT" | scrub | ev R4 attempts.txt
  chk "the daemon records it delivered by the prompt hook, not a paste or relay" "grep -q '\"state\":\"delivered\",\"by\":\"prompt-hook\"' '$T/evidence/R4/attempts.txt'"
  finish smoke "idle Orchestrator woken by the report"
}

row_5() {
  local ok=1
  begin R5
  if [ -z "${ITEM:-}" ] || [ -z "${E_REPORT:-}" ]; then driver smoke item "no item or no report"; return; fi
  trail trail5 "$ITEM" || ok=0
  keep trail5
  script close5 <<EOF
A=\$(\$AH roster checklist add "smoke close item" --role implementor --team "\$TEAM" --cwd "\$REPO"); echo "\$A"
I=\$(printf '%s' "\$A" | sed -nE 's/.*"item": *"([^"]+)".*/\1/p' | head -1)
N=\$(\$AH msg new --to implementor --from orchestrator --slug smoke-close --to-name $NAME --title "Close this smoke step" --team "\$TEAM" --eta small --item "_run/\$I" --body-file "$ITEM_BODY" --cwd "\$REPO")
ID=\$(printf '%s' "\$N" | sed -nE 's/.*"id": *"([^"]+)".*/\1/p' | head -1)
\$AH msg close "\$ID" --reason abandoned --team "\$TEAM" --json --cwd "\$REPO"
\$AH roster checklist show "\$I" --trail --team "\$TEAM" --cwd "\$REPO"
EOF
  run_in "$ORCH" close5 120 || ok=0
  keep close5
  if [ "$ok" = 0 ]; then driver smoke item "the Orchestrator did not run the checklist scripts"; return; fi
  # Neither script nor driver sets these states, so a move in the trail is the daemon's own.
  chk "the report moved the item, as its member" "out_of trail5 | item_moved_by '$NAME@$TEAM'"
  chk "the close moved its item to wait on the Orchestrator" "out_of close5 | grep -q '\"to\": \"waiting_orch\"'"
  finish smoke "item moves on report and on close"
}

# Opens the bridge and answers a waiting item the way the page does. Needs a user at a terminal: user-rows.sh row_d07 is the same flow.
row_6() {
  local out="$T/cap/ui.out" err="$T/cap/ui.err" pid stamp url port code base st item_q body ans
  begin R6
  script ask6 <<EOF
A=\$(\$AH roster checklist add "smoke question item" --role implementor --team "\$TEAM" --cwd "\$REPO"); echo "\$A"
I=\$(printf '%s' "\$A" | sed -nE 's/.*"item": *"([^"]+)".*/\1/p' | head -1); echo "item=\$I"
\$AH roster checklist set "\$I" --state waiting-user --note "Smoke: pick one" --option Yes --option No --team "\$TEAM" --cwd "\$REPO"
EOF
  run_in "$ORCH" ask6 120 || { driver smoke answer "the Orchestrator did not ask"; return; }
  keep ask6
  item_q=$(out_of ask6 | sed -n 's/^item=//p')
  sleep 3
  body=$(question_body "$item_q")
  [ -n "$body" ] || { chk "the daemon's read model holds the question" false; finish smoke "UI answer"; return; }
  printf '%s\n' "$body" | ev R6 body.json
  "$AHD" ui --print --no-open >"$out" 2>"$err" &
  pid=$!; stamp=$(ah_pid_stamp "$pid"); probe_record_pid "$pid"
  poll 15 "grep -q '/ui/' '$out' || ! kill -0 $pid"
  scrub <"$err" | ev R6 ui-err.txt
  if ! grep -q '/ui/' "$out"; then
    stop_job "$pid" "$stamp"
    if gate_refused <"$err"; then record R6 - - NOT-TESTED "the question is up, but the daemon's bridge opens only for a user at a terminal; run smoke.sh from your own terminal"
    else chk "the bridge printed a link in 15 s" false; finish smoke "UI answer" "$(head -1 "$err" | scrub | cut -c1-160)"; fi
    return
  fi
  url=$(grep -oE 'http://127\.0\.0\.1:[0-9]+/ui/' "$out" | head -1)
  port=$(printf '%s' "$url" | sed -E 's|^http://127\.0\.0\.1:([0-9]+)/.*|\1|'); base="http://127.0.0.1:$port"
  ans=$(http -w ' %{http_code}' -H 'Content-Type: application/json' -d "$body" "$base/ui/v1/answer")
  printf '%s\n' "$ans" | ev R6 answer.txt
  chk "the route answered 200" "printf '%s' '$ans' | grep -q ' 200\$'"
  poll 120 "orch_entries | grep -E '\"from\":\"user' | grep -q ."
  orch_entries | grep -E '"from":"user' | scrub | ev R6 entries.txt
  chk "question-answered mail from the user" "[ -s '$T/evidence/R6/entries.txt' ]"
  poll 120 "grep -F 'question-answered' '$ORCH_TX' | grep -q '\"type\":\"user\"'"
  chk "it reached the Orchestrator's transcript" "grep -F 'question-answered' '$ORCH_TX' | grep -q '\"type\":\"user\"'"
  trail trail6 "$item_q"; keep trail6
  chk "the item left waiting-user" "out_of trail6 | grep -qE 'ui-answer'"
  stop_job "$pid" "$stamp"
  finish smoke "UI answer through the bridge"
}

row_7() {
  begin R7
  if [ -z "${NAME:-}" ]; then driver smoke dismiss "no member"; teardown; return; fi
  script dismiss <<EOF
\$AH roster dismiss "$NAME" --close --team "\$TEAM" --cwd "\$REPO"
EOF
  run_in "$ORCH" dismiss 180 || { driver smoke dismiss "the Orchestrator did not run dismiss"; teardown; return; }
  keep dismiss
  poll 30 "[ -n '${MEMBER_PID:-}' ] && ! kill -0 '${MEMBER_PID:-}' 2>/dev/null"
  chk "dismiss answered clean" "[ \"\$(rc_of dismiss)\" = 0 ] && out_of dismiss | grep -q '\"closed\"'"
  # an unknown pid fails here: `kill -0 0` would test this script's own process group
  chk "the member's process ended" "[ -n '${MEMBER_PID:-}' ] && ! kill -0 '${MEMBER_PID:-}'"
  chk "its herdr pane closed" "! pane_ids | grep -qxF '${PANE_M:-none}'"
  teardown
  local left; left=$(pgrep -f "${T##*/}" | grep -vx "$$" | tr '\n' ' ')
  { echo "left: $left"; ps -o pid=,command= -p "${left// /,}" 2>/dev/null; } | scrub | cut -c1-200 | ev R7 left.txt
  chk "no process names the root after teardown" "[ -z '$left' ]"
  chk "the herdr server is gone" "! kill -0 '${H_SERVER:-0}'"
  chk "the sandbox daemon is gone" "! daemon_alive"
  finish smoke "dismiss the member; nothing left running"
}

# real_db_verdict <schema before> <after> <size+mtime before> <after> <backups before> <after> <daemon before> <after>: PASS, FAIL or NOT-TESTED, then why.
# The real daemon writes that file itself, so a moved mtime alone proves nothing while the same daemon is up. A migration always leaves a backup.
real_db_verdict() {
  if [ "$1" != "$2" ]; then echo "FAIL schema went $1 -> $2"
  elif [ -n "$(labeled_added "$5" "$6")" ]; then echo "FAIL a labeled database backup appeared during the run ($(labeled_added "$5" "$6")), so something migrated or restored it"
  elif [ "$3" = "$4" ]; then echo "PASS schema $2 and size+mtime unchanged"
  elif [ -n "$7" ] && [ "$7" = "$8" ]; then echo "PASS schema $2 unchanged, no new backup; mtime moved, as the same real daemon writes it ($3 -> $4)"
  else echo "NOT-TESTED schema $2 unchanged, but the real daemon changed during the run and mtime moved ($3 -> $4), so nothing separates its writes from ours"
  fi
}
real_schema() { sqlite3 -readonly "$REAL_AH/state/ah.db" "select value from meta where key='schema_version'" 2>/dev/null; }
# The labeled backups (ah-<ts>-v<N>-<label>.db: pre-migrate, pre-restore, pre-ship…), one name per line. The real daemon's hourly
# backups carry no label, and its retention removes old ones, so neither a new hourly name nor a removed name says anything about this run.
real_backups() { ls "$REAL_AH/state/backups" 2>/dev/null | grep -E '^ah-[0-9]{8}T[0-9]{6}Z-v[0-9]+-.+\.db$' | sort; }
# labeled_added <before> <after>: the labeled names in after that were not in before
labeled_added() { comm -13 <(printf '%s\n' "$1" | sed '/^$/d') <(printf '%s\n' "$2" | sed '/^$/d') | tr '\n' ' ' | sed 's/ $//'; }
row_8() {
  local v
  v=$(real_db_verdict "$REAL_SCHEMA0" "$(real_schema)" "$REAL_STAT0" "$(real_db_stat)" "$REAL_BACKUPS0" "$(real_backups)" "$REAL_STAMP0" "$(ah_pid_stamp "$(live_pid)")")
  record R8 - - "${v%% *}" "${v#* }"
}

# Predicates only, each against the nearest input that must not match. No session, no daemon, no tokens.
self_test() {
  local bad=0 t; t=$(mktemp -d "${TMPDIR:-/tmp}/smoke-st.XXXXXX") || return 1
  st() { if [ "$2" = "$3" ]; then echo "self-test ok: $1"; else echo "self-test BAD: $1: want '$3', got '$2'"; bad=1; fi; }
  first() { printf '%s' "${1%% *}"; }
  st "db: nothing moved" "$(first "$(real_db_verdict 12 12 a a 1 1 d d)")" PASS
  st "db: schema moved" "$(first "$(real_db_verdict 12 13 a a 1 1 d d)")" FAIL
  st "db: schema back with a new backup" "$(first "$(real_db_verdict 12 12 a b 1 3 d d)")" FAIL
  st "db: mtime moved, same daemon" "$(first "$(real_db_verdict 12 12 a b 1 1 d d)")" PASS
  st "db: mtime moved, another daemon" "$(first "$(real_db_verdict 12 12 a b 1 1 d e)")" NOT-TESTED
  st "db: mtime moved, no daemon" "$(first "$(real_db_verdict 12 12 a b 1 1 d '')")" NOT-TESTED
  orch_entries() { cat "$t/entries"; }
  printf '%s\n' '{"id":"e-note","kind":"note","ref":"/m/r1--orchestrator--s--response.md"}' '{"id":"e-other","kind":"report","ref":"/m/r2--orchestrator--s--response.md"}' '{"id":"e-mine","kind":"report","ref":"/m/r1--orchestrator--s--response.md"}' >"$t/entries"
  st "report: the request's own" "$(report_entry r1)" e-mine
  st "report: another request's is not this one's" "$(report_entry r3)" ""
  rc_of() { "$@"; echo $?; }
  st "response: the member's bullet" "$(printf '[hierarchy-msg r1 response]\n- DONE smoke\n' | rc_of shows_member_report)" 0
  st "response: the brief's own words are not a report" "$(printf 'Then file your report as one bullet, "- DONE smoke", the way your contract says\n' | rc_of shows_member_report)" 1
  st "response: empty" "$(printf '' | rc_of shows_member_report)" 1
  cat >"$t/tx" <<'EOF'
{"type":"assistant","timestamp":"2026-01-01T00:00:00Z"}
{"type":"user","timestamp":"2026-01-01T00:00:10Z","pluginPrompt":true,"message":"[ah-entry e-plugin]"}
{"type":"assistant","timestamp":"2026-01-01T00:00:20Z"}
{"type":"user","timestamp":"2026-01-01T00:00:21Z","message":"[ah-entry e-typed]"}
{"type":"queued_command","timestamp":"2026-01-01T00:00:30Z","message":"[ah-entry e-queued]"}
EOF
  st "wake: a plugin prompt after 10 s idle" "$(wake_of "$t/tx" e-plugin)" "10 1"
  st "wake: a typed prompt is not a plugin prompt" "$(wake_of "$t/tx" e-typed)" "1 0"
  st "wake: a queued line is no user turn" "$(wake_of "$t/tx" e-queued)" "-1 0"
  st "wake: an entry never seen" "$(wake_of "$t/tx" e-none)" "-1 0"
  moved() { if printf '%s' "$1" | item_moved_by m@t; then echo yes; else echo no; fi; }
  st "item: the member reported it" "$(moved '{"rows":[{"by":"m@t","from":"in_progress","to":"reported"}]}')" yes
  st "item: a DONE no longer goes to review" "$(moved '{"rows":[{"by":"m@t","from":"in_progress","to":"in_review"}]}')" no
  st "item: the member moved it to wait" "$(moved '{"rows":[{"by":"m@t","from":"in_progress","to":"waiting_orch"}]}')" yes
  st "item: the Orchestrator moved it" "$(moved '{"rows":[{"by":"o@t","from":"in_progress","to":"reported"}]}')" no
  st "item: the member touched it without moving it" "$(moved '{"rows":[{"by":"m@t","from":"in_progress","to":"in_progress"}]}')" no
  live() { if printf '%s' "$1" | view_member_live /d; then echo yes; else echo no; fi; }
  st "view: the member live" "$(live '{"children":[{"dir":"/d","cluster":[{"role":"implementor","state":"live"}]}]}')" yes
  st "view: the member idle" "$(live '{"children":[{"dir":"/d","cluster":[{"role":"implementor","state":"idle"}]}]}')" yes
  st "view: the member busy" "$(live '{"children":[{"dir":"/d","cluster":[{"role":"implementor","state":"busy"}]}]}')" yes
  st "view: the member blocked" "$(live '{"children":[{"dir":"/d","cluster":[{"role":"implementor","state":"blocked"}]}]}')" yes
  st "view: the member notlive" "$(live '{"children":[{"dir":"/d","cluster":[{"role":"implementor","state":"notlive"}]}]}')" no
  st "view: live in another dir" "$(live '{"children":[{"dir":"/e","cluster":[{"role":"implementor","state":"live"}]}]}')" no
  st "view: another role live" "$(live '{"children":[{"dir":"/d","cluster":[{"role":"reviewer","state":"live"}]}]}')" no
  rows() { local r; for r in 1 2 3 4 5 7 8; do echo "PASS       R$r    ok"; done; }
  { rows; echo "PASS       R6    ok"; } >"$t/r"; st "exit: all pass" "$(run_exit "$t/r")" 0
  { rows; echo "NOT-TESTED R6    the bridge opens only for a user at a terminal"; } >"$t/r"; st "exit: only the bridge's gate" "$(run_exit "$t/r")" 3
  { rows; echo "NOT-TESTED R6    driver: the Orchestrator did not ask"; } >"$t/r"; st "exit: R6 not run for another reason" "$(run_exit "$t/r")" 4
  { rows | grep -v 'R2 '; echo "NOT-TESTED R2    driver: no script"; echo "NOT-TESTED R6    opens only for a user"; } >"$t/r"; st "exit: another row not tested too" "$(run_exit "$t/r")" 4
  { rows | grep -v 'R2 '; echo "FAIL       R2    broken"; echo "NOT-TESTED R6    opens only for a user"; } >"$t/r"; st "exit: a product break beside the gate" "$(run_exit "$t/r")" 1
  { rows; echo "PASS       R6    ok"; echo "FAIL       canary.real-daemon x"; } >"$t/r"; st "exit: a canary FAIL" "$(run_exit "$t/r")" 1
  { rows; echo "PASS       R6    ok"; echo "NOT-TESTED canary.real-db x"; } >"$t/r"; st "exit: a canary not tested does not count" "$(run_exit "$t/r")" 0
  : >"$t/r"; st "exit: no rows at all" "$(run_exit "$t/r")" 4
  local BUILDS="$t/builds.jsonl"; : >"$BUILDS"
  mkdir -p "$t/tree/mod" "$t/export/mod"
  echo mod >"$t/tree/mod/register.tsx"; echo mod >"$t/export/mod/register.tsx"
  git -C "$t/tree" init -q && git -C "$t/tree" add mod && git -C "$t/tree" -c user.name=s -c user.email=s@s commit -qm m
  st "mod source: a checkout's own HEAD" "$(mod_source 9.9.9 "$t/tree")" "$(git -C "$t/tree" rev-parse HEAD) $(git hash-object --no-filters "$t/tree/mod/register.tsx")"
  git -C "$t/export" init -q
  st "mod source: a tree with no commit names none" "$(mod_source 9.9.9 "$t/export")" ""
  printf 'tok.123\r\n' >"$t/token"
  st "token: a CR after it is stripped and the token goes" "$(printf 'a tok.123 b\n' | TOKEN_FILE="$t/token" token_scrub)" "a <token> b"
  st "token: a near miss is left (negative)" "$(printf 'tokX123\n' | TOKEN_FILE="$t/token" token_scrub)" "tokX123"
  st "token: both screen captures go through it" "$(grep -cE 'scrub \| token_scrub \| ev (R2|run\.orch) screen\.txt' "$SMOKE_DIR/smoke.sh")" 2
  mkdir -p "$t/st/cap"
  cat >"$t/st/cap/claude-debug-1.log" <<'EOF'
2026-11-05T20:00:00.000Z [DEBUG] Writing to temp file: /h/.claude.json.tmp.1.a
2026-11-05T20:00:02.300Z [DEBUG] File /h/.claude.json written atomically
2026-11-05T20:00:03.000Z [WARN] [event-loop-stall] blocked for 4200ms monotonic (wall drift 0ms, clock jump 0ms, expected 500ms). Total stalls: 1, cumulative: 4200ms monotonic / 4200ms wall
2026-11-05T20:00:09.000Z [WARN] [event-loop-stall] blocked for 1800ms monotonic (wall drift 0ms, clock jump 0ms, expected 500ms). Total stalls: 2, cumulative: 6000ms monotonic / 6000ms wall
EOF
  printf '%s\n' '2026-11-05T20:00:00.000Z [DEBUG] Writing to temp file: /h/.claude.json.tmp.1.a' '2026-11-05T20:00:00.010Z [DEBUG] File /h/.claude.json written atomically' >"$t/st/cap/quiet.log"
  st "stalls: named from the debug log" "$(stall_summary "$t/st/cap/claude-debug-1.log")" "event-loop stalls: 2 (longest 4200 ms, total 6000 ms); slowest .claude.json write 2300 ms"
  st "stalls: a quiet log names none (negative)" "$(stall_summary "$t/st/cap/quiet.log" | tr -d '\n'; echo " rc=${PIPESTATUS[0]}")" "event-loop stalls: 0 (longest 0 ms, total 0 ms); slowest .claude.json write 10 ms rc=1"
  st "retry: a payload miss with stalls waits once more" "$(T="$t/st"; orch_payload() { echo "waited $1"; }; orch_retry 2)" "waited 60"
  st "retry: a session that never reached its prompt does not, stalls or not" "$(T="$t/st"; orch_payload() { echo "waited $1"; }; orch_retry 1; echo "rc=$?")" "rc=1"
  st "retry: a miss with no stall does not (negative)" "$(T="$t/st"; mv "$t/st/cap/claude-debug-1.log" "$t/st/stalled.log"; orch_payload() { echo "waited $1"; }; orch_retry 2; echo "rc=$?")" "rc=1"
  mkdir -p "$t/lk/out"
  printf 'tok-abc\r\n' >"$t/lk/token"
  printf 'x tok-abc y\n' >"$t/lk/out/leaked.txt"
  st "leak: a CRLF token file still finds the bare token" "$(T="$t/lk/out"; FIXTURES=(); TOKEN_FILE="$t/lk/token" leak_check 2>/dev/null; echo "rc=$?")" "rc=1"
  printf 'x tok-ab y\n' >"$t/lk/out/leaked.txt"
  st "leak: a file without the token is clean (negative)" "$(T="$t/lk/out"; FIXTURES=(); TOKEN_FILE="$t/lk/token" leak_check 2>/dev/null; echo "rc=$?")" "rc=0"
  st "note: a retry's note row does not change the exit" "$({ rows; echo "PASS       R6    ok"; echo "NOTE       run.orch                 the SessionStart payload came on the one retry"; } >"$t/r"; run_exit "$t/r")" 0
  rm -rf "$t"
  return "$bad"
}

# ---- teardown: our own recorded pids, TERM then KILL. The root's unique name finds any child that outlived its parent.
teardown() {
  [ -n "${TORN:-}" ] && return
  [ -S "${H_SOCK:-/nonexistent}" ] && h session stop "$SESSION" >/dev/null 2>&1
  rtm kill-server >/dev/null 2>&1
  stop_own
  local i s p
  for s in TERM KILL; do
    for i in "${!PIDS[@]}"; do [ "${PIDS[$i]}" = "$WATCHDOG" ] || ah_signal_stamped "$s" "${PIDS[$i]}" "${PIDSTAMPS[$i]:-}"; done
    for p in $(pgrep -f "${T##*/}" | grep -vx "$$"); do ah_signal_stamped "$s" "$p" "$(ah_pid_stamp "$p")"; done
    sleep 2
  done
  # Last: a signal during the loops leaves the exit trap to run them again.
  TORN=1
}

smoke_exit() {
  local rc=$? hits
  trap - EXIT
  trap '' INT TERM HUP
  case "${T:-}" in /private/tmp/ahprobe.?*) ;; *) exit "$rc" ;; esac
  teardown
  ah_signal_stamped TERM "${WATCHDOG:-}" "$(ah_pid_stamp "${WATCHDOG:-}")"
  if [ -n "${RESULTS:-}" ]; then
    row_8
    canary_end
    hits=$(real_herdr_hits)
    judge canary.real-herdr "[ '$hits' = 0 ] || [ '$hits' = absent ]" "the user's herdr lists no agent of this run" "the user's herdr answered: $hits"
    judge run.no-outside-herdr "[ ! -s '$T/herdr-called' ]" "no herdr call aimed outside the root" "a herdr call aimed outside: $(head -2 "$T/herdr-called" 2>/dev/null | tr '\n' ';')"
    leak_check; judge run.token "[ '$?' = 0 ]" "no file under the root holds the login token" "a file holds the login token"
    echo "run time: $((SECONDS - START_S)) s; tokens: $(tokens_total)" | tee -a "$RESULTS"
    rc=$(run_exit "$RESULTS")
    if [ "$rc" = 1 ]; then echo "A check failed, so the copy is kept: $T"
    else rm -rf "${T:?}/home" "${T:?}/ah" "${T:?}/plugin" "${T:?}/ws" "${T:?}/config" "${T:?}/tmux" "${T:?}/codex" "${T:?}/bin" "${T:?}/cap" "${T:?}/tmp" "${T:?}/out" "${T:?}/s"; fi
    echo "Results: $RESULTS"
    echo "Evidence: $T/evidence"
  fi
  exit "$rc"
}

main() {
  [ "${1:-}" = --self-test ] && { self_test; exit $?; }
  [ $# = 1 ] || { echo "usage: smoke.sh <built plugin tree | sha of a dogfood build> | --self-test" >&2; exit 2; }
  SUBJECT=$(resolve_subject "$1") || { echo "smoke: $1 is neither a directory nor a sha of a dogfood build still in the plugin cache" >&2; exit 2; }
  [ -x "$SUBJECT/bin/ah" ] && [ -f "$SUBJECT/mod/register.tsx" ] && [ -f "$SUBJECT/.claude-plugin/plugin.json" ] || { echo "smoke: no built plugin at $SUBJECT" >&2; exit 2; }
  [ -x "$REAL_HERDR" ] || { echo "smoke: no herdr" >&2; exit 2; }
  canary_start
  REAL_SCHEMA0=$(real_schema); REAL_BACKUPS0=$(real_backups)
  RESULTS=
  probe_init
  export AH_HOME="$HOME/.claude/ah2" AH_TEST=1 AH_TEST_REAL_HOME="$REAL_USER_HOME"
  local v
  for v in "$HOME" "$AH_HOME"; do
    resolves_real "$v" && refuse "$v is the real home"
    case "$v" in "$T"/*) ;; *) refuse "$v is outside $T" ;; esac
  done
  trap smoke_exit EXIT
  mkdir -p "$AH_HOME" "$T/evidence" "$T/out" "$T/s" "$T/tmp" "$T/config"
  RESULTS="$T/results.txt"
  export TMPDIR="$T/tmp"
  perl -e '$p=getppid; $e=time+$ARGV[0]; while(time<$e){sleep 5; exit 0 if getppid!=$p} kill "TERM",$p' "$SMOKE_BUDGET_S" &
  WATCHDOG=$!; probe_record_pid "$WATCHDOG"
  write_refusing_shims
  write_shim
  write_herdr_wrapper
  ln -s "$T/plugin/bin/ah" "$T/bin/ah"; ln -s "$T/plugin/bin/ahd" "$T/bin/ahd"
  sandbox_settings
  fixture_repo
  echo "smoke: copying the real home to $AH_HOME"
  copy_home || { check run.copy FAIL "the copy failed"; exit 1; }
  cp -Rp "$SUBJECT" "$T/plugin"
  export AH_PLUGIN_ROOT="$T/plugin" AH_BIN="$AH_HOME/bin/ahd"
  PROBE_PLUGIN="$T/plugin"; AH="$T/plugin/bin/ah"; AHD="$T/plugin/bin/ahd"
  # The real home's binary follows whichever build installed last, so the daemon under test is built from this tree unless it already is that build.
  build_daemon || { scrub <"$T/cap/build.txt" | tail -20 | ev run build.txt; check run.build FAIL "the tree did not build: build.txt"; exit 1; }
  cd "$REPO" || exit 1
  start_daemon || { check run.daemon FAIL "the sandbox daemon did not start: $(scrub <"$T/cap/ahd-start.txt" | cut -c1-200)"; exit 1; }
  start_herdr || { check run.herdr FAIL "the herdr test session did not come up"; exit 1; }
  local orch_fail=0
  start_orch_herdr || orch_fail=$?
  if orch_retry "$orch_fail"; then
    # A note row, which run_exit does not count, so a slow load stays in the results.
    record run.orch - - NOTE "the SessionStart payload came on the one retry, after $(stall_summary "$T"/cap/claude-debug-*.log)"
    orch_fail=0
  fi
  case $orch_fail in
    0) ;;
    *)
      # The whole screen and the session's debug log, so the next failure names its screen and whether SessionStart fired.
      cap "$ORCH" | scrub | token_scrub | ev run.orch screen.txt
      for f in "$T"/cap/claude-debug-*.log; do [ -f "$f" ] && scrub <"$f" | token_scrub | ev run.orch "${f##*/}"; done
      if [ "$orch_fail" = 2 ]; then
        check run.orch FAIL "the Orchestrator reached its prompt but no SessionStart payload came (30 s, and 60 s more when the log shows stalls); $(stall_summary "$T"/cap/claude-debug-*.log); see evidence/run.orch"
      else
        check run.orch FAIL "the Orchestrator session never reached its prompt: $(cap "$ORCH" | scrub | tail -5 | tr '\n' ' ' | cut -c1-240); see evidence/run.orch"
      fi
      exit 1 ;;
  esac
  # Row 1 reads the session's hook context, which reaches the transcript with its first prompt: the spawn script's.
  row_2; local spawned=$?
  row_1
  if [ "$spawned" = 0 ]; then
    if row_3; then row_4; row_5; else not_run "no brief went out" R4 R5; fi
  else
    not_run "no member" R3 R4 R5
  fi
  row_6
  row_7
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then main "$@"; fi
