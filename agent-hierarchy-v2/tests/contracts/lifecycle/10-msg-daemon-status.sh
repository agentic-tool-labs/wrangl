#!/bin/bash
# K5 scenario 10: `msg daemon status` against a running daemon. The up form carries the daemon's identity and counters (pid, version,
# uptime, root, owns, ladder) next to the registrations and the overdue count; the text form is the long-standing multi-line layout;
# a stopped daemon reads `down`. The client is `msg daemon status` of IMPL_CLIENT, the daemon is of IMPL_DAEMON.
. "$(dirname "$0")/_lib.sh"
lc_as_harness "$@"
lc_runtimes
lc_init
printf '{"team_id":"t-tm","orchestrator":{"pid":%s,"pid_start":"%s","session_id":"orch-tm"},"members":[{"role":"implementor","name":"tm-implementor","transport_id":"p1"}]}\n' "$$" "$(lc_start_of $$)" >"$PROJ/.claude/ah2/teams/tm.json"
lc_register_self orch-tm orchestrator tm "$PROJ/.claude/ah2/teams/tm.json"

msg() {
  (cd "$PROJ" && "$AHD_NATIVE_BIN" ah msg "$@" --cwd "$PROJ")
}

lc_ctl start >/dev/null 2>&1
PID=$(lc_info_pid)
lc_check "a daemon is up" 'lc_alive "$PID"'

JSON=$(msg daemon status --json 2>"$T/status.err")
lc_check "the up form names every field in order" 'printf %s "$JSON" | node -e '"'"'
const s = JSON.parse(require("fs").readFileSync(0, "utf8"));
const keys = ["up","version","pid","uptime_s","root","owns","ladder","timer_state","registrations","overdue","load"];
process.exit(JSON.stringify(Object.keys(s)) === JSON.stringify(keys) ? 0 : 1);'"'"
lc_check "...up is true and pid is the daemon's own" 'printf %s "$JSON" | node -e '"'"'
const s = JSON.parse(require("fs").readFileSync(0, "utf8"));
process.exit(s.up === true && s.pid === Number(process.argv[1]) ? 0 : 1);'"'"' "$PID"'
lc_check "...version and root are text, uptime and overdue are numbers" 'printf %s "$JSON" | node -e '"'"'
const s = JSON.parse(require("fs").readFileSync(0, "utf8"));
process.exit(typeof s.version === "string" && typeof s.root === "string" && typeof s.uptime_s === "number" && s.uptime_s >= 0 && typeof s.overdue === "number" ? 0 : 1);'"'"
lc_check "...owns, ladder and timer_state are objects, registrations is a list" 'printf %s "$JSON" | node -e '"'"'
const s = JSON.parse(require("fs").readFileSync(0, "utf8"));
const obj = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
process.exit(obj(s.owns) && obj(s.ladder) && obj(s.timer_state) && Array.isArray(s.registrations) ? 0 : 1);'"'"

TEXT=$(msg daemon status 2>"$T/status-text.err")
lc_check "the text form opens with up and names version, root, owns, registrations, overdue, the ladder and the load" 'printf "%s\n" "$TEXT" | node -e '"'"'
const lines = require("fs").readFileSync(0, "utf8").split("\n").filter(Boolean);
const want = [/^up$/, /^version \S+  pid \d+  uptime \d+s$/, /^root /, /^owns mailbox=/];
const ok = want.every((re, i) => re.test(lines[i] || ""))
  && lines.some((l) => l.startsWith("registrations: "))
  && lines.some((l) => /^overdue: \d+$/.test(l))
  && /^ladder: /.test(lines[lines.length - 2])
  && /^load: /.test(lines[lines.length - 1]);
process.exit(ok ? 0 : 1);'"'"

lc_ctl stop >/dev/null 2>&1
lc_wait_for 8 bash -c "! kill -0 $PID 2>/dev/null"
DOWN=$(msg daemon status 2>/dev/null)
lc_check "a stopped daemon reads down" '[ "$DOWN" = down ]'
DOWNJSON=$(msg daemon status --json 2>/dev/null)
lc_check "...and its JSON form is up false with the registrations, overdue and a null load only" '[ "$DOWNJSON" = "{\"up\":false,\"registrations\":[{\"address\":\"orchestrator@tm\",\"harness\":\"claude\",\"adapter\":\"none\"}],\"overdue\":0,\"load\":null}" ]'
lc_done
