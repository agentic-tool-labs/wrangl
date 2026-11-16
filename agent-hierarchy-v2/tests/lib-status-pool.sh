# agent-hierarchy — stages a throwaway hierarchy pool for the status-document tests and fixtures.
# Sourced, not run. Every write lands under one sandbox: `pool <dir>` points HD (the hierarchy dir),
# FAKEHOME and PROJ at it, and `status` runs `ah roster status` against that pool only.
# Needs PLUGIN set by the sourcing script.

H="$PLUGIN/hooks"

# <dir>: start a fresh pool in <dir> (created; must be empty or absent).
pool() {
  POOL="$1"
  HD="$POOL/hier"; FAKEHOME="$POOL/home"; PROJ="$POOL/proj"
  PENDING="$FAKEHOME/.claude/ah2/agent-hierarchy.peer-pending.jsonl"
  mkdir -p "$HD/msgs" "$HD/activity" "$FAKEHOME/.claude/ah2" "$PROJ/.claude/ah2"
  [ -z "${STATUS_POOL_IN:-}" ] || printf '{"alwaysOrchestrator":true}\n' > "$FAKEHOME/.claude/ah2/agent-hierarchy.json"
}

# <ms offset> from REF (an ISO instant the caller sets): that instant as ISO.
at() { node -e 'process.stdout.write(new Date(Date.parse(process.argv[1]) + Number(process.argv[2])).toISOString())' "$REF" "$1"; }

# <team name or -> <members json array>: a live team (this shell owns it), created now so the 24 h
# age rule holds; `-` is the default team (team.json).
team() {
  local path="$HD/team.json"
  [ "$1" != - ] && { mkdir -p "$HD/teams"; path="$HD/teams/$1.json"; }
  node -e 'const [members, pid] = process.argv.slice(1); console.log(JSON.stringify({ team_id: "t-demo", created: new Date().toISOString(), orchestrator: { session_id: null, pid: Number(pid) }, members: JSON.parse(members) }))' "$2" "$$" | ah_seed_team "$path"
}

# <session id>: the session record binding that session to this test shell's pid, the owner of every team `team` writes.
session() {
  # the session_pids table (per-dir-files-to-db S1), through the domain's append
  printf '{"args":{"dir":"%s","session_id":"%s","pid":%s},"context":{"cwd":"%s","hierarchy_dir":"%s"},"op_id":"sp-%s-%s"}' "$HD" "$1" "$$" "$HD" "$HD" "$RANDOM$RANDOM" "$$" \
    | HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" "$PLUGIN/bin/ahd" state session-pids append >/dev/null 2>&1
}

# <member name> <role> <session id> <pane id> [<pid>]: that member's live `up` row, attributed by pane; the pid is this shell's unless given.
up() {
  ah_seed_roster "$HD" "$(node -e 'const [sid, pid, role, pane] = process.argv.slice(1); process.stdout.write(JSON.stringify({ type: "peer", status: "up", session_id: sid, pid: Number(pid), role, pane_id: pane, ts: new Date().toISOString() }))' "$3" "${5:-$$}" "$2" "$4")"
}

# <file stem> <activity> <at iso> [blocked_by] [note]: an activity record.
activity() {
  ah_seed_activity "$HD" "$1" "$(node -e 'const [a, at, by, note] = process.argv.slice(1); process.stdout.write(JSON.stringify({ activity: a, at, blocked_by: by || null, note: note || null }))' "$2" "$3" "${4:-}" "${5:-}")"
}

# <id> <to> <slug> <created iso> [eta] [to_name] [team] [from]: a request file.
request() {
  cat > "$HD/msgs/$1--$2--$3--request.md" <<EOF
---
id: $1
type: request
to: $2
from: ${8:-orchestrator}
slug: $3
parent: null
reason: null
eta: ${5:-small}
to_name: ${6:-null}
from_name: null
team: ${7:-null}
created: $4
---

## [0] tldr
- none
EOF
  ah_seed_msgs "$HD"
}

# <id> <mtime iso> [<report line>]: a planted response to that planted request, stored with that time, which the store takes from the
# file's mtime; path in RESP. With no report line it stays a skeleton, which the store keeps as a draft.
plant_response() {
  local req base to slug from
  req=$(ls "$HD/msgs/$1--"*--request.md)
  base=${req##*/}; to=${base#*--}; slug=${to#*--}; slug=${slug%--request.md}; to=${to%%--*}
  from=$(sed -n 's/^from: //p' "$req" | head -1)
  RESP="$HD/msgs/$1--$from--$slug--response.md"
  sed -e 's/^type: request$/type: response/' -e "s/^to: .*/to: $from/" -e "s/^from: .*/from: $to/" -e '/^## \[0\] tldr$/,$d' "$req" > "$RESP"
  printf '## [1] status\n%s\n' "${3:-}" >> "$RESP"
  mtime "$RESP" "$2"
  ah_seed_msgs "$HD"
}
# <id> [<mtime iso>]: a skeleton response to that request, stored with that time when given.
stub() {
  if [ -n "${2:-}" ]; then plant_response "$1" "$2"; return; fi
  HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" "$PLUGIN/bin/ah" msg new --type response --id "$1" --cwd "$PROJ" > /dev/null
}
# <id> <mtime iso>: a response holding a report, stored with that time.
filled() { plant_response "$1" "$2" '- done: reported'; }
mtime() { node -e 'const t = new Date(process.argv[2]); require("fs").utimesSync(process.argv[1], t, t)' "$1" "$2"; }

# <id> <to> <created iso> [session]: a dispatch row for that request.
dispatch() { ah_seed_peer "$(printf '{"type":"dispatch","session_id":"%s","request_id":"%s","to":"%s","created":"%s"}' "${4:-orch}" "$1" "$2" "$3")"; }
# <id> <ts iso>: a liveness-nudge gate row for that request.
nudge() { ah_seed_gate "$HD" "$(printf '{"type":"liveness-nudge","session_id":"orch","request_id":"%s","ts":"%s"}' "$1" "$2")"; }
# <anchor id> <json line>: a decision-log row for that run.
# decision <run id> <row json>: one row of the run's decision log, through the decisions state op (the log is a database table;
# a decisions.jsonl file is not read). DECISION_SEEDED says whether the op took it.
decision() {
  local body
  # the op wants every field of a row: the ones a suite leaves out get the plainest valid value for the row's kind
  body=$(node -e '
    const [hd, run, row, proj, id] = process.argv.slice(1), r = JSON.parse(row), parked = r.kind === "parked";
    const full = { source: "architect", options: ["use A", "use B"], default: "a", choice: parked ? null : "a",
      decider: parked ? null : { role: "architect", name: "demo-architect", model: "opus" }, rationale: parked ? "" : "simpler", revert: parked ? "" : "switch back",
      review: false, why_user: parked ? "unsure" : null, dangerous: false, exchange: null, ...r };
    process.stdout.write(JSON.stringify({ args: { path: hd + "/pipeline/" + run + "/decisions.jsonl", run, input: full }, context: { cwd: proj, hierarchy_dir: hd }, op_id: "dec-" + id }));
  ' "$HD" "$1" "$2" "$PROJ" "$$-$RANDOM")
  DECISION_SEEDED=$(printf '%s' "$body" | (cd "$PROJ" && HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" sh "$PLUGIN/bin/ahd" state decisions add --cwd "$PROJ") 2>&1)
}

disable() { printf '{"enabled": false}\n' > "$FAKEHOME/.claude/ah2/agent-hierarchy.json"; }

# <now iso> [--plain]: the status document as of that instant, in OUT; RC its exit code.
status() { OUT=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" "$PLUGIN/bin/ah" roster status --now "$1" ${2:+"$2"} --cwd "$PROJ" 2>&1); RC=$?; }
# files-off-disk F3a: no status.json any more. [<hierarchy dir>, default HD]: its status document from `ah view status`, saved to a
# file whose path is printed (an empty file when the verb fails), for the checks that read the document from a path.
vdoc() { HOME="$FAKEHOME" "$PLUGIN/bin/ah" view status --dir "${1:-$HD}" > "$POOL/view-status.json" 2>/dev/null; echo "$POOL/view-status.json"; }
# <js expression over the document o>: its value from OUT, a string as-is, anything else as JSON.
jq_doc() { node -e 'const o = JSON.parse(process.argv[1]); const v = (new Function("o", "return (" + process.argv[2] + ")"))(o); process.stdout.write(typeof v === "string" ? v : JSON.stringify(v))' "$OUT" "$1" 2>/dev/null; }

# phook <env assignments...> -- <payload json>...: runs the hook unit named by HOOK once per payload from one driver process whose pid is
# every run's parent, and prints that pid. A SessionStart driver runs as the claude-named process (ah_test_as_harness), because that hook
# runs only under a harness parent. With DRV_TEAM=<path> the driver first writes a live default team there (when the directory exists),
# owned by its own pid. The driver exits with the last hook run's status.
phook() {
  local envs=() wrap=()
  while [ "$1" != -- ]; do envs+=("$1"); [ "$1" = HOOK=sessionstart ] && wrap=(ah_test_as_harness); shift; done; shift
  ( export "${envs[@]}" AH_HOOK_SH="$PLUGIN/bin/ah-hook" AH_SEED_SH="$(dirname "$_ate_ah")/ah"; ${wrap[@]+"${wrap[@]}"} node -e '
    const { spawnSync } = require("child_process");
    const fs = require("fs");
    const t = process.env.DRV_TEAM;
    if (t && fs.existsSync(t.slice(0, t.lastIndexOf("/")))) spawnSync("sh", [process.env.AH_SEED_SH, "db", "test-seed", "teams", "--path", t], { input: JSON.stringify({ team_id: "t-drv", created: new Date().toISOString(), orchestrator: { session_id: null, pid: process.pid }, members: [] }), stdio: ["pipe", "ignore", "ignore"] });
    let status = 0;
    for (const p of process.argv.slice(1)) status = spawnSync("sh", [process.env.AH_HOOK_SH, process.env.HOOK], { input: p, env: process.env, stdio: ["pipe", "ignore", "ignore"] }).status;
    process.stdout.write(String(process.pid));
    process.exitCode = status;' "$@" )
}

# with_proof <team> <team file or ""> <name>... -- <command>...: runs the command as a shell that provably holds every <name>@<team> (one
# session; the Orchestrator's proof also lands in the team file), then drops the registration, because a registered shell makes the hooks
# the test runs afterwards see a session that is not their payload's.
with_proof() {
  local team=$1 tfile=$2 names=() sess="" n rc
  shift 2
  while [ "$1" != -- ]; do names+=("$1"); shift; done; shift
  for n in "${names[@]}"; do sess=$(ah_test_prove_self "$n" "$team" "$tfile" "" "$sess") || return 1; done
  CLAUDE_CODE_SESSION_ID="$sess" CLAUDE_PID=$$ "$@"; rc=$?
  ah_forget_sessions "$sess"
  return $rc
}
