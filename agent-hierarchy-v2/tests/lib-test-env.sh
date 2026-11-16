# agent-hierarchy — the shared test environment. Every tests/test-*.sh sources this as its first command.
#
# It exports AH_DAEMON=off, so no test starts the `ahd` daemon by accident, however the test is started
# (tests/run-suite.sh or `bash tests/test-x.sh` directly). Only the daemon's own tests opt in, by setting
# AH_TEST_DAEMON=1 on the line before sourcing this file; an opted-in test gets a daemon-enabled environment even when
# its caller (run-suite.sh) exported AH_DAEMON=off. They run inside their own sandbox and stop what they start.
# tests/test-test-env.sh fails any test file that does not source this first.

if [ "${AH_TEST_DAEMON:-}" = 1 ]; then unset AH_DAEMON; else export AH_DAEMON=off; fi

# A test run from a member session (a gate run by an Implementor, say) inherits that session's member marker, which makes every
# owner verb it runs refuse as a member's. A test that wants the marker sets it itself after this line.
unset AH_MEMBER_NAME
unset CLAUDE_CODE_SESSION_ID  # a test names its own session per command; the ambient one has no registration under the test's AH_HOME

# A test never reads or writes the user's own AH_HOME (its run directory, spawn-failed marker, mailbox, stamp): when the caller gave none
# (tests/run-all.sh and run-suite.sh give one per test), the run gets a private one in its own /tmp/ahprobe.* sandbox, which is what
# makes it a test home (see AH_TEST below) and is removed at exit.
if [ -z "${AH_HOME:-}" ]; then
  _ate_root=$(mktemp -d /tmp/ahprobe.XXXXXX)
  AH_HOME="$_ate_root/ah-home"
  mkdir -p "$AH_HOME" && export AH_HOME
  # Test mode refuses a write outside the sandbox, so a test's own mktemp directories go inside it, as they do under run-all.
  mkdir -p "$_ate_root/tmp" && export TMPDIR="$_ate_root/tmp"
  if declare -F hermetic_on_exit >/dev/null; then hermetic_on_exit "rm -rf \"$_ate_root\""; fi
fi

# A test started directly is in test mode like a run-all worker, whenever its AH_HOME is inside an ahprobe sandbox: a hook it runs through
# the shim is then not refused as an untrusted home, and nothing it runs logs a row to the real hook-errors log. A caller's own AH_TEST
# value is kept. The predicate is the shim's, evaluated in a subshell so none of its names reach the test.
_ate_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
if [ -z "${AH_TEST:-}" ] && [ -f "$_ate_dir/../bin/ah-testmode.sh" ]; then
  if ( AH_TEST=1; . "$_ate_dir/../bin/ah-testmode.sh" && ah_test_mode ) >/dev/null 2>&1; then export AH_TEST=1; fi
fi
unset _ate_dir

# A test started directly runs the binary under test, not whichever `ahd` the shim finds on PATH (an installed build that may lack
# the code under test): AHD_NATIVE_BIN, the ahd file run-all.sh and run-suite.sh pin, becomes AH_BIN here too. A caller's own AH_BIN
# is kept, and a value that is not an executable file (a directory, say) pins nothing.
if [ -z "${AH_BIN:-}" ] && [ -f "${AHD_NATIVE_BIN:-}" ] && [ -x "${AHD_NATIVE_BIN:-}" ]; then export AH_BIN="$AHD_NATIVE_BIN"; fi

. "$(dirname "${BASH_SOURCE[0]}")/lib-pid.sh"

# Every sqlite3 a suite runs waits for a writer's lock instead of failing at once: a suite's live ahd writes the store, and a read
# that met its lock printed "database is locked" and nothing else, so a helper such as ah_team_doc read an empty team (b482).
sqlite3() { command sqlite3 -cmd '.timeout 5000' "$@"; }

# ah_seed_msgs <pool>...: stores the message files planted under each pool's msgs/, through the test-only `ah db test-seed msgs`
# (ah-msgs-db-only §17). The store never reads a hand-planted file, and a file already stored is left alone.
_ate_ah="$(dirname "${BASH_SOURCE[0]}")/../bin/ah"
ah_seed_msgs() {
  local _asm_args=() _asm_p
  for _asm_p in "$@"; do _asm_args+=(--pool "$_asm_p"); done
  "$_ate_ah" db test-seed msgs "${_asm_args[@]}" >/dev/null || { echo "ah_seed_msgs: could not store the messages planted in $*" >&2; return 1; }
}

# ah_forget_msgs <id>...: takes stored messages out again, for a suite that reuses a pool between cases; no verb deletes a message.
ah_forget_msgs() {
  local _afm_id
  for _afm_id in "$@"; do
    sqlite3 "$AH_HOME/state/ah.db" "DELETE FROM msg_exchanges WHERE id = '$_afm_id'; DELETE FROM msg_bodies WHERE msg_pk IN (SELECT pk FROM msg_messages WHERE id = '$_afm_id'); DELETE FROM msg_messages WHERE id = '$_afm_id'" || return 1
  done
}
# ah_team_doc <team file>: the stored team document as JSON; nothing when no such team is stored. A team is keyed by the file's path, not held in it.
ah_team_doc() {
  local _atd_dir _atd_name
  case "$1" in
    */teams/*.json) _atd_dir=$(dirname "$(dirname "$1")"); _atd_name=$(basename "$1" .json) ;;
    *) _atd_dir=$(dirname "$1"); _atd_name=team.json ;;
  esac
  _atd_dir=$(cd "$_atd_dir" 2>/dev/null && pwd -P) || return 0
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT doc FROM teams WHERE hier_dir = '$_atd_dir' AND team = '$_atd_name'"
}

# ah_team_stored <team file>: whether that team is stored.
ah_team_stored() { [ -n "$(ah_team_doc "$1")" ]; }

# ah_forget_team <team file>: takes a stored team out again, for a suite that starts a case with no team; a command that disbands is what a case under test uses.
ah_forget_team() {
  local _aft_dir _aft_name
  case "$1" in
    */teams/*.json) _aft_dir=$(dirname "$(dirname "$1")"); _aft_name=$(basename "$1" .json) ;;
    *) _aft_dir=$(dirname "$1"); _aft_name=team.json ;;
  esac
  _aft_dir=$(cd "$_aft_dir" 2>/dev/null && pwd -P) || return 0
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 "$AH_HOME/state/ah.db" "DELETE FROM members WHERE hier_dir = '$_aft_dir' AND team = '$_aft_name'; DELETE FROM teams WHERE hier_dir = '$_aft_dir' AND team = '$_aft_name'; UPDATE domain_seq SET seq = seq + 1 WHERE domain = 'teams'"
}

# ah_forget_teams <hierarchy dir>: takes every stored team of that hierarchy directory out again.
ah_forget_teams() {
  local _afts_dir
  _afts_dir=$(cd "$1" 2>/dev/null && pwd -P) || return 0
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 "$AH_HOME/state/ah.db" "DELETE FROM members WHERE hier_dir = '$_afts_dir'; DELETE FROM teams WHERE hier_dir = '$_afts_dir'; UPDATE domain_seq SET seq = seq + 1 WHERE domain = 'teams'"
}

# ah_session_doc <session id>: the stored registration of that session as JSON; nothing when none is stored.
ah_session_doc() {
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT doc FROM sessions WHERE session_id = '$1' UNION ALL SELECT doc FROM sessions_unplaced WHERE session_id = '$1'"
}

# ah_forget_sessions [<session id>]: takes that stored registration out again, or every one.
ah_forget_sessions() {
  local _afs_where=""
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  [ -n "${1:-}" ] && _afs_where=" WHERE session_id = '$1'"
  sqlite3 "$AH_HOME/state/ah.db" "DELETE FROM sessions$_afs_where; DELETE FROM sessions_unplaced$_afs_where"
}

# ah_team_query <team file> <js expression over t, the team>: prints the value (a string as is, anything else as JSON); empty when no such team is stored.
ah_team_query() {
  ah_team_doc "$1" | node -e 'const s=require("fs").readFileSync(0,"utf8");if(!s.trim())process.exit(0);const t=JSON.parse(s);const v=eval(process.argv[1]);process.stdout.write(typeof v==="string"?v:JSON.stringify(v)??"")' "$2"
}

# ah_seed_team <team file>: the team document on stdin, stored through the real writer.
ah_seed_team() {
  local _ast_dir _ast_path=$1
  # the store keys a team by its real hierarchy directory; a teams/ dir need not exist
  case "$1" in
    */teams/*.json) _ast_dir=$(cd "$(dirname "$(dirname "$1")")" 2>/dev/null && pwd -P) && _ast_path="$_ast_dir/teams/$(basename "$1")" ;;
    *) _ast_dir=$(cd "$(dirname "$1")" 2>/dev/null && pwd -P) && _ast_path="$_ast_dir/$(basename "$1")" ;;
  esac
  sh "$(dirname "$_ate_ah")/ah" db test-seed teams --path "$_ast_path" >/dev/null
}

# ah_seed_peer <row json>: appends one peer record through the real writer.
ah_seed_peer() {
  printf '{"args":{"rec":%s},"context":{"cwd":"%s","hierarchy_dir":"%s"},"op_id":"seed-%s-%s-%s"}' "$1" "$PWD" "$PWD" "$$" "$RANDOM" "$RANDOM" \
    | sh "$(dirname "$_ate_ah")/ahd" state peer-records append --cwd "$PWD" >/dev/null
}

# ah_seed_peers: appends each line on stdin as one peer record.
ah_seed_peers() {
  local _asp_line
  while IFS= read -r _asp_line; do [ -n "$_asp_line" ] && ah_seed_peer "$_asp_line"; done
  return 0
}

# ah_peer_rewrite <js statement over r, one row>: edits stored peer records in place (a test's clock surgery); only the raw row changes.
ah_peer_rewrite() {
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 -readonly -separator $'\t' "$AH_HOME/state/ah.db" "SELECT seq, CAST(raw AS TEXT) FROM peer_records ORDER BY seq" \
    | node -e 'const lines=require("fs").readFileSync(0,"utf8").split("\n").filter(Boolean);const out=[];
      for (const l of lines){const t=l.indexOf("\t");const seq=l.slice(0,t);const before=l.slice(t+1);let r;try{r=JSON.parse(before);}catch{continue;}
        eval(process.argv[1]);const after=JSON.stringify(r);if(after!==before)out.push(`UPDATE peer_records SET raw = X\x27${Buffer.from(after).toString("hex")}\x27 WHERE seq = ${seq};`);}
      process.stdout.write(out.join("\n"));' "$1" \
    | sqlite3 "$AH_HOME/state/ah.db"
}

# ah_peer_rows: the peer records in order, one JSON row per line.
# ah_box_entries <box dir, or a bare member name>: the stored entries of that mailbox box, one JSON line each, oldest first.
ah_box_entries() {
  local _abe_box=${1##*/mailbox/}
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT CAST(raw AS TEXT) FROM mail_entries WHERE box = '$_abe_box' OR box LIKE '%/$_abe_box' ORDER BY pk"
}

ah_peer_rows() {
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT CAST(raw AS TEXT) FROM peer_records ORDER BY seq"
}

# ah_gate_rows: the store's gate log (per-dir-files-to-db S3: the gates table), one row body per line, by dir then append order.
ah_gate_rows() {
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT body FROM gates ORDER BY hier_dir, seq" 2>/dev/null
}

# ah_usage_rows: the subagent usage records (b397 group 2: the usage_rows table), one body per line, in append order.
ah_usage_rows() {
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT body FROM usage_rows ORDER BY seq" 2>/dev/null
}

# ah_forget_usage: drops every usage record.
ah_forget_usage() {
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 "$AH_HOME/state/ah.db" "DELETE FROM usage_rows"
}

# ah_seed_home_doc <key> <json>: stores a home document (home.session-roles, home.ultra-gate, home.permissions) as given.
ah_seed_home_doc() { printf '%s' "$2" | AH_DAEMON=off sh "$PLUGIN/bin/ah" db test-seed home-docs --key "$1" >/dev/null; }

# ah_seed_gate <hierarchy dir> <row json>: appends one row to that dir's gate log, as given.
ah_seed_gate() { printf '%s' "$2" | AH_DAEMON=off sh "$PLUGIN/bin/ah" db test-seed gates --dir "$1" >/dev/null; }

# ah_gate_ts <request id> <iso> [<nth>]: sets the ts of that request's nth (0-based, default every) liveness-nudge row.
ah_gate_ts() {
  local _agt_where="json_extract(body, '\$.type') = 'liveness-nudge' AND json_extract(body, '\$.request_id') = '$1'"
  if [ -n "${3:-}" ]; then
    _agt_where="rowid = (SELECT rowid FROM gates WHERE $_agt_where ORDER BY hier_dir, seq LIMIT 1 OFFSET $3)"
  fi
  sqlite3 "$AH_HOME/state/ah.db" "UPDATE gates SET body = json_set(body, '\$.ts', '$2') WHERE $_agt_where"
}

# _ah_dirs <dir>: the dir and its real path, quoted for SQL, for a row stored under either spelling.
_ah_dirs() { printf "'%s','%s'" "$1" "$(cd "$1" 2>/dev/null && pwd -P || printf '%s' "$1")"; }

# ah_seed_activity <hierarchy dir> <subject> <record json>: stores that activity record (per-dir-files-to-db S2: the activity table).
ah_seed_activity() { printf '%s' "$3" | AH_DAEMON=off sh "$PLUGIN/bin/ah" db test-seed activity --dir "$1" --subject "$2" >/dev/null; }

# ah_seed_job <hierarchy dir> <team folder> <id> <record json>: stores that tracked-job record (per-dir-files-to-db S8: the jobs table).
ah_seed_job() { printf '%s' "$4" | AH_DAEMON=off sh "$PLUGIN/bin/ah" db test-seed jobs --dir "$1" --team "$2" --id "$3" >/dev/null; }

# ah_activity <hierarchy dir> <subject>: the stored activity record as JSON; nothing when none.
ah_activity() {
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT body FROM activity WHERE hier_dir IN ($(_ah_dirs "$1")) AND session_id = '$2' LIMIT 1" 2>/dev/null
}

# ah_activity_at <hierarchy dir> <subject>: when the record was last written (unix ms); nothing when none.
ah_activity_at() {
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT updated_at FROM activity WHERE hier_dir IN ($(_ah_dirs "$1")) AND session_id = '$2' LIMIT 1" 2>/dev/null
}

# ah_activity_age <hierarchy dir> <subject> <seconds>: backdates the record's write time by that many seconds.
ah_activity_age() {
  sqlite3 "$AH_HOME/state/ah.db" "UPDATE activity SET updated_at = updated_at - $3 * 1000 WHERE hier_dir IN ($(_ah_dirs "$1")) AND session_id = '$2'"
}

# ah_session_pids <hierarchy dir>: the stored session pid rows (per-dir-files-to-db S1), one `session_id pid` per line, oldest first.
ah_session_pids() {
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 -readonly -separator ' ' "$AH_HOME/state/ah.db" "SELECT session_id, pid FROM session_pids WHERE hier_dir IN ($(_ah_dirs "$1")) ORDER BY at, rowid" 2>/dev/null
}

# ah_msg_state <id>: the states the store holds for that id's messages, one per line.
ah_msg_state() { sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT DISTINCT state FROM msg_messages WHERE id = '$1'"; }
# ah_msg_stored_path <id> <type>: the path the store names that message by.
ah_msg_stored_path() { sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT COALESCE(export_path, draft_path) FROM msg_messages WHERE id = '$1' AND type = '$2' LIMIT 1"; }
# ah_msg_path: the `path` of the JSON line `msg new` prints on stdin; it names the message, and no file is there.
ah_msg_path() { node -e 'const l=require("fs").readFileSync(0,"utf8").split("\n").find(x=>x.startsWith("{"));process.stdout.write(JSON.parse(l).path)'; }
# ah_msg_text <message path> [show args]...: the stored text of the message that path names.
ah_msg_text() { local base=${1##*/} type=request; case "$base" in *--response.md) type=response;; esac; shift; "$_ate_ah" msg show "${base%%--*}" "$type" "$@"; }

# ah_drop_checklist_hint: stdin to stdout without the one stderr line `msg new` adds after an Orchestrator request sent without --item
# (ah-checklist-upkeep §2.2: "note: <id> was|is not on the checklist…"), for a helper that captures 2>&1 and then parses the output.
ah_drop_checklist_hint() { grep -vE '^note: [0-9]{8}-[0-9]{6}-[0-9a-z]{4} (was|is) not on the checklist' || true; }

# skill_text <skill dir> <var>: sets <var> to a file holding that skill's SKILL.md followed by every reference/*.md, so
# a test asserting on a skill's rules reads the same text wherever the rule lives. The file sits in the test's private
# directory, which the hermetic exit handler removes.
skill_text() {
  local _st_f="${_AH_HERMETIC_DIR:-${TMPDIR:-/tmp}}/skill-$(basename "$1").md"
  cat "$1/SKILL.md" "$1"/reference/*.md > "$_st_f" 2>/dev/null
  printf -v "$2" '%s' "$_st_f"
}

# ah_test_prove_self <name> <team> [<team file>] [<box dir>] [<session id>] [<pid>]: records this test's own shell as <name>@<team>, provably. An owner verb
# (and `ah msg --from`) accepts a caller only when an ancestor's pid and start time are recorded: a live registration under AH_HOME,
# and, for the Orchestrator, the team file's orchestrator pid and pid_start. This writes both for $$ with its real start time, which
# CLAUDE_PID=$$ or a pid-only team file does not. The optional fifth argument names the session id to register it under; the optional
# sixth proves that pid instead of $$ (a subshell's $BASHPID for an owner that must exit before the test looks at its team). The start time is formatted by tests/contract-tools/lib-case.mjs, the code the
# contract runner uses. Prints the session id.
ah_test_prove_self() {
  local _atps_session
  # A hook refuses a payload session that is not the one registered for its parent process, so a suite that runs hooks must not
  # register its own shell: it sets AH_TEST_RUNS_HOOKS and proves callers in subshells with ah_test_run_as.
  if [ -n "${AH_TEST_RUNS_HOOKS:-}" ] && [ "${6:-$$}" = "$$" ]; then
    echo "ah_test_prove_self: this suite runs hooks (AH_TEST_RUNS_HOOKS), so it must not register its own shell; use ah_test_run_as" >&2
    return 1
  fi
  _atps_session=$(node "$(dirname "${BASH_SOURCE[0]}")/contract-tools/prove-self.mjs" "$1" "$2" "${6:-$$}" "${AH_HOME:?}" "${3:-}" "${4:-$AH_HOME/boxes/$1@$2}" "${5:-}") || return 1
  printf '%s\n' "$_atps_session"
}

# ah_test_run_as_from <team> <team file> <command…>: runs the command as the caller its own `--from <name>` argument names, provably.
# `--from orchestrator` (or no --from) runs as this shell, which a suite proves once as the Orchestrator with ah_test_prove_self; any
# other name runs in a subshell proven as that member of the team, since a member's caller must be one of the team's members. An
# Orchestrator session owns no member box, so one shell cannot stand for both. The member must be in the team file under that name.
ah_test_run_as_from() {
  local _atr_team=$1 _atr_file=$2 _atr_who=orchestrator _atr_i _atr_j
  shift 2
  for ((_atr_i = 1; _atr_i <= $#; _atr_i++)); do
    if [ "${!_atr_i}" = --from ]; then _atr_j=$((_atr_i + 1)); _atr_who=${!_atr_j}; fi
  done
  if [ "$_atr_who" = orchestrator ] && [ -z "${AH_TEST_ORCH_SUBSHELL:-}" ]; then "$@"; return $?; fi
  ah_test_run_as "$_atr_who" "$_atr_team" "$_atr_file" "$@"
}
# ah_test_run_as <name> <team> <team file> <command…>: runs the command in a subshell proven as <name>@<team>, for a suite whose own
# shell must stay unregistered (it runs hooks under several payload sessions, and a hook refuses a payload session that is not the
# one registered for its parent process). With AH_TEST_ORCH_SUBSHELL set, ah_test_run_as_from does the same for `--from orchestrator`.
# AH_TEST_AS_SESSION=<id> registers the identity under that session id, for running a hook whose payload carries it.
# AH_TEST_BOXES=<dir> puts the caller's box at <dir>/<name>, for a suite whose mail is delivered to a box path it computes itself.
ah_test_run_as() {
  local _atr_who=$1 _atr_team=$2 _atr_file=$3
  shift 3
  # exit 97 and a stderr line when the identity cannot be proven, so a check made as that caller never passes for want of a caller
  ( if [ "$_atr_who" != orchestrator ] && ! ah_team_doc "$_atr_file" | node -e 'const t = JSON.parse(require("fs").readFileSync(0, "utf8")); process.exit((t.members || []).some((m) => m.name === process.argv[1]) ? 0 : 1)' "$_atr_who" 2>/dev/null; then
      echo "identity proof failed for $_atr_who@$_atr_team: no member of that name in $_atr_file" >&2; exit 97
    fi
    ah_test_prove_self "$_atr_who" "$_atr_team" "$_atr_file" "${AH_TEST_BOXES:+$AH_TEST_BOXES/$_atr_who}" "${AH_TEST_AS_SESSION:-}" "$BASHPID" >/dev/null || { echo "identity proof failed for $_atr_who@$_atr_team" >&2; exit 97; }
    "$@"; exit $? )  # the exit keeps bash from exec-ing the command, which would leave the proven pid as the command itself, not its parent
}

# ah_test_team <hierarchy dir> <name> <role[:member name]>…: stores the team of <hierarchy dir>/teams/<name>.json, with the Orchestrator
# and those members (a member listed as just a role is named after it), for ah_test_prove_self / ah_test_run_as to prove callers in.
# AH_TEST_TEAM_MESH_POLICY=<policy> adds mesh.policy; AH_TEST_TEAM_ORCH_PID=<pid> adds orchestrator.pid.
ah_test_team() {
  local _att_hd=$1 _att_name=$2 _att_m _att_members=""
  shift 2
  mkdir -p "$_att_hd/teams"
  for _att_m in "$@"; do
    _att_members="$_att_members${_att_members:+,}{\"role\":\"${_att_m%%:*}\",\"name\":\"${_att_m##*:}\",\"transport_id\":\"p-${_att_m##*:}\"}"
  done
  printf '{"team_id":"t-%s","transport":"tmux",%s"orchestrator":{"session_id":"s-%s"%s},"members":[%s]}\n' "$_att_name" \
    "${AH_TEST_TEAM_MESH_POLICY:+\"mesh\":{\"policy\":\"$AH_TEST_TEAM_MESH_POLICY\"\},}" "$_att_name" "${AH_TEST_TEAM_ORCH_PID:+,\"pid\":$AH_TEST_TEAM_ORCH_PID}" \
    "$_att_members" | ah_seed_team "$_att_hd/teams/$_att_name.json"
}

# drop_project_state <repo>: removes a repo's per-run state from its project dir (teams, messages, peers, pipeline runs) and keeps the
# roster, the conventions file and the other agent-hierarchy* files, which a suite sets up once and its scenarios share.
drop_project_state() {
  find "$1/.claude/ah2" -mindepth 1 -maxdepth 1 ! -name 'agent-hierarchy*' ! -name ah-conventions.json -exec rm -rf {} + 2>/dev/null
  return 0
}

# ah_test_install_fake_herdr <bin dir>: puts tests/fake-herdr.mjs on a test's PATH as `herdr`. The fake keeps its state in
# $FAKE_STATE_DIR (the header of fake-herdr.mjs lists its knobs); ah_test_seed_fake_herdr writes a starting state.
ah_test_install_fake_herdr() {
  mkdir -p "$1"
  printf '#!/bin/sh\nexec %s %s "$@"\n' "$(command -v node)" "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/fake-herdr.mjs" > "$1/herdr"
  chmod +x "$1/herdr"
}

# ah_test_seed_fake_herdr <state dir> <width> <height> [<cwd>]: one workspace w1 with one tab w1:t1 holding the caller's pane w1:p0,
# which fills the area. Prints the pane variables a herdr pane gives its session (HERDR_PANE_ID, HERDR_TAB_ID, HERDR_WORKSPACE_ID)
# as words for a test to pass to `env`; whether a call runs on herdr (HERDR_ENV=1) is the call's to say.
ah_test_seed_fake_herdr() {
  local state=$1 w=$2 h=$3 cwd=${4:-}
  rm -rf "$state"; mkdir -p "$state"
  node -e 'const [f,w,h,cwd]=process.argv.slice(1);const area={x:0,y:0,width:+w,height:+h};require("fs").writeFileSync(f,JSON.stringify({next:10,self:"w1:p0",area,tabs:{"w1:t1":{label:"1",ws:"w1"}},panes:{"w1:p0":{tab:"w1:t1",ws:"w1",cwd:cwd||null,rect:{...area}}},agents:{}}))' "$state/herdr.json" "$w" "$h" "$cwd"
  printf 'HERDR_PANE_ID=w1:p0 HERDR_TAB_ID=w1:t1 HERDR_WORKSPACE_ID=w1'
}

# ah_test_fake_herdr_agent <state dir> <name> <pane id> <cwd>: a live agent the fake herdr reports in a pane of the caller's tab.
ah_test_fake_herdr_agent() {
  node -e 'const fs=require("fs");const [d,name,pane,cwd]=process.argv.slice(1);const f=d+"/herdr.json";const s=JSON.parse(fs.readFileSync(f,"utf8"));s.panes[pane]={tab:"w1:t1",ws:"w1",cwd,rect:{x:0,y:0,width:10,height:10}};s.agents[pane]={name,status:"idle"};fs.writeFileSync(f,JSON.stringify(s))' "$1" "$2" "$3" "$4"
}

# The fake herdr's state and call log are read and changed only through these; a test never opens herdr.json or calls/ itself.
# ah_test_fake_herdr_state <state dir> <js expression over s, the state>: prints the value (a string as is, anything else as JSON).
# The state is {next, self, area, tabs: {id: {label, ws}}, panes: {id: {tab, ws, cwd, rect}}, agents: {pane id: {name, status}}}.
ah_test_fake_herdr_state() {
  node -e 'const s=JSON.parse(require("fs").readFileSync(process.argv[1]+"/herdr.json","utf8"));const v=(new Function("s","return ("+process.argv[2]+");"))(s);console.log(typeof v==="string"?v:JSON.stringify(v))' "$1" "$2"
}
# ah_test_fake_herdr_holds <state dir> <js boolean over s>: succeeds iff the expression is truthy.
ah_test_fake_herdr_holds() {
  node -e 'const s=JSON.parse(require("fs").readFileSync(process.argv[1]+"/herdr.json","utf8"));process.exit((new Function("s","return ("+process.argv[2]+");"))(s)?0:1)' "$1" "$2"
}
# ah_test_fake_herdr_edit <state dir> <js statements over s>: changes the state.
ah_test_fake_herdr_edit() {
  node -e 'const fs=require("fs");const f=process.argv[1]+"/herdr.json";const s=JSON.parse(fs.readFileSync(f,"utf8"));(new Function("s",process.argv[2]))(s);fs.writeFileSync(f,JSON.stringify(s))' "$1" "$2"
}
# ah_test_fake_herdr_rename_agent <state dir> <pane id> <name>: the agent in that pane now carries another name.
ah_test_fake_herdr_rename_agent() { ah_test_fake_herdr_edit "$1" "s.agents['$2'].name='$3'"; }
# ah_test_fake_herdr_calls <state dir>: every herdr call so far, one argv as a JSON array per line, in call order.
ah_test_fake_herdr_calls() {
  node -e 'const fs=require("fs");const d=process.argv[1]+"/calls";let rows=[];try{rows=fs.readdirSync(d).map(f=>JSON.parse(fs.readFileSync(d+"/"+f,"utf8")))}catch{}rows.sort((a,b)=>{const x=BigInt(a.t_ns||0),y=BigInt(b.t_ns||0);return x<y?-1:x>y?1:0});for(const r of rows)console.log(JSON.stringify(r.argv))' "$1"
}
# ah_test_fake_herdr_reset_calls <state dir>: forgets the calls so far, and the count of agent lists the knobs key on.
ah_test_fake_herdr_reset_calls() { rm -rf "$1/calls" "$1/listcount"; }

# ---- harness-shaped processes: what the product's own liveness checks look for
# A claude-kind member counts as live only when its pid's command line starts with `claude` (or `node`/`bun` running a claude script),
# and a SessionStart hook runs only when its parent's command line does. A bare `sleep` or a test's own shell is neither. These make a
# real local process whose argv[0] is `claude`, so the checks run unchanged against it. Everything started here is stopped when the
# test exits, and each sleeps for a bounded time anyway.
_AH_TEST_HARNESS_REGS=()
# Every process started here is listed, with its start time, in a file named for the test's shell, so a fixture started from a
# subshell (the right side of a pipe, say) is stopped too.
ah_test_harness_track() { printf '%s|%s\n' "$1" "$(ah_test_pid_start "$1")" >> "${TMPDIR:-/tmp}/ah-harness-pids.$$"; }
# ah_test_harness_stop: stops every process the fixtures started and removes the registrations ah_test_harness_peer wrote, so a later
# case does not find members live that it never seeded. A listed pid is signalled only while it still has the claude command line and the
# start time it was listed with. The test's own EXIT handler runs it too.
ah_test_harness_stop() {
  local _ath_list="${TMPDIR:-/tmp}/ah-harness-pids.$$" _ath_pid _ath_start
  if [ -f "$_ath_list" ]; then
    while IFS='|' read -r _ath_pid _ath_start; do
      if [ "$(ah_test_pid_start "$_ath_pid" 2>/dev/null)" = "$_ath_start" ]; then
        ah_signal_ours TERM "$_ath_pid" 'claude '
      fi
    done < "$_ath_list"
    rm -f "$_ath_list"
  fi
  [ "${#_AH_TEST_HARNESS_REGS[@]}" -eq 0 ] || rm -f "${_AH_TEST_HARNESS_REGS[@]}"
  _AH_TEST_HARNESS_REGS=()
}
if declare -F hermetic_on_exit >/dev/null; then hermetic_on_exit ah_test_harness_stop; else trap ah_test_harness_stop EXIT; fi

# ah_test_harness_peer [<name> <team> [<session id>]]: starts `exec -a claude sleep` in the background and sets AH_TEST_HARNESS_PID to
# its pid. Given a name and a team it also registers that pid as <name>@<team> with its real start time, the way ah_test_prove_self
# registers a shell. Call it directly, not inside $( ): the process has to be this shell's child.
ah_test_harness_peer() {
  local _ath_name=${1:-} _ath_team=${2:-} _ath_session=${3:-} _ath_i=0
  bash -c 'exec -a claude sleep "$1"' _ "${AH_TEST_HARNESS_SECONDS:-600}" &
  AH_TEST_HARNESS_PID=$!
  ah_test_harness_track "$AH_TEST_HARNESS_PID"
  until ps -ww -o command= -p "$AH_TEST_HARNESS_PID" 2>/dev/null | grep -q '^claude '; do
    _ath_i=$((_ath_i + 1))
    [ "$_ath_i" -le 100 ] || { echo "ah_test_harness_peer: pid $AH_TEST_HARNESS_PID never took the claude command line" >&2; return 1; }
    sleep 0.05
  done
  [ -z "$_ath_name" ] && return 0
  _ath_session=$(ah_test_prove_self "$_ath_name" "$_ath_team" "" "" "$_ath_session" "$AH_TEST_HARNESS_PID") || return 1
  _AH_TEST_HARNESS_REGS+=("${AH_HOME:?}/registrations/$_ath_session.json")
}

# ah_test_pid_start <pid> [<skew seconds>]: the start time the product records as a process's pid_start, from the formatter the
# contract runner and ah_test_prove_self use; with a skew, the start of a process that began that many seconds later.
ah_test_pid_start() {
  node -e 'import("file://" + process.argv[1]).then((m) => console.log(m.processStart(Number(process.argv[2]), Number(process.argv[3] || 0))))' "$(dirname "${BASH_SOURCE[0]}")/contract-tools/lib-case.mjs" "$1" "${2:-0}"
}

# ah_test_always_on <dir>: every session under this HOME or repo is in, as the unit Rig's default config has it. A suite whose sessions
# name no role and own no team calls it: without the key they are bare, and the hooks say nothing to them.
ah_test_always_on() { mkdir -p "$1/.claude/ah2" && printf '{"alwaysOrchestrator":true}\n' >"$1/.claude/ah2/agent-hierarchy.json"; }

# ah_test_in_harness <command…>: runs the command as a child of a process whose argv[0] is `claude`, with stdin, stdout and the exit
# status passed through. A hook run this way sees a harness parent. The trailing wait keeps bash from replacing itself with the command.
ah_test_in_harness() { ( exec -a claude bash -c '"$@"; _rc=$?; wait; exit $_rc' _ "$@" ); }
# ah_test_in_harness_alive <command…>: like ah_test_in_harness, but the claude-named parent stays alive (as a bounded `sleep`, stopped with
# the other fixtures) after the command returns, for a case that reads the pid the hook recorded for its parent later. Stdin passes through.
ah_test_in_harness_alive() {
  local _ath_done _ath_i=0
  _ath_done=$(mktemp -u "${TMPDIR:-/tmp}/ah-harness-done.XXXXXX") || return 1
  AH_HARNESS_DONE=$_ath_done AH_HARNESS_SECONDS=${AH_TEST_HARNESS_SECONDS:-600} \
    bash -c 'exec -a claude bash -c '\''"$@"; : > "$AH_HARNESS_DONE"; exec -a claude sleep "$AH_HARNESS_SECONDS"'\'' _ "$@"' _ "$@" <&0 &
  ah_test_harness_track "$!"
  until [ -e "$_ath_done" ]; do
    _ath_i=$((_ath_i + 1))
    [ "$_ath_i" -le 1200 ] || { echo "ah_test_in_harness_alive: the command never returned" >&2; return 1; }
    sleep 0.05
  done
  rm -f "$_ath_done"
}
# ah_test_as_harness <command…>: runs the command as the claude-named process itself, for a driver that spawns the hook and so is its parent.
ah_test_as_harness() { ( exec -a claude "$@" ); }

# ah_test_wait_for <seconds> <description> <command…>: polls the command every 0.1 s until it succeeds; on timeout it says what
# it was waiting for on stderr and returns 1, so a condition that never came true fails the suite instead of passing by luck.
ah_test_wait_for() {
  local _atw_limit=$(( ${1:?seconds} * 10 )) _atw_what=${2:?description} _atw_i
  shift 2
  for ((_atw_i = 0; _atw_i < _atw_limit; _atw_i++)); do
    "$@" && return 0
    sleep 0.1
  done
  echo "ah_test_wait_for: timed out after ${_atw_limit}x0.1s waiting for: $_atw_what" >&2
  return 1
}

# ah_test_detached <command…>: runs the command in a session of its own whose parent is the init process, so none of the caller's
# ancestors (a Claude session, say) is an ancestor of it. That is what a process started from a plain terminal looks like to a product
# check that refuses to admit a harness running under a Claude session. The command's stdout and stderr come back on this function's,
# its exit status is returned (124 when it outlives AH_TEST_DETACHED_SECONDS, default 600), and stdin is empty. A command still running
# when the test exits is stopped.
_AH_TEST_DETACHED_DIRS=()
ah_test_detached_stop() {
  local _atd_d _atd_p
  for _atd_d in "${_AH_TEST_DETACHED_DIRS[@]}"; do
    [ -f "$_atd_d/pid" ] || continue
    _atd_p=$(cat "$_atd_d/pid" 2>/dev/null)
    [ -n "$_atd_p" ] && ah_signal_ours TERM "$_atd_p" "$_atd_d"
  done
  _AH_TEST_DETACHED_DIRS=()
}
if declare -F hermetic_on_exit >/dev/null; then hermetic_on_exit ah_test_detached_stop; else trap ah_test_detached_stop EXIT; fi
ah_test_detached() {
  local _atd_dir _atd_i=0 _atd_rc
  _atd_dir=$(mktemp -d "${TMPDIR:-/tmp}/ah-detached.XXXXXX") || return 1
  _AH_TEST_DETACHED_DIRS+=("$_atd_dir")
  perl -e 'use POSIX qw(setsid); my ($d, @c) = @ARGV; defined(my $p = fork) or die "fork: $!"; exit 0 if $p; setsid();
    open STDIN, "<", "/dev/null"; open STDOUT, ">", "$d/out"; open STDERR, ">", "$d/err";
    open(my $f, ">", "$d/pid"); print $f $$; close $f;
    my $s = system(@c); my $rc = $s == -1 ? 127 : ($s & 127) ? 128 + ($s & 127) : $s >> 8;
    open(my $r, ">", "$d/rc.tmp"); print $r $rc; close $r; rename "$d/rc.tmp", "$d/rc";' "$_atd_dir" "$@"
  until [ -e "$_atd_dir/rc" ]; do
    _atd_i=$((_atd_i + 1))
    if [ "$_atd_i" -gt $(( ${AH_TEST_DETACHED_SECONDS:-600} * 10 )) ]; then
      ah_test_detached_stop; cat "$_atd_dir/out" 2>/dev/null; cat "$_atd_dir/err" >&2 2>/dev/null; rm -rf "$_atd_dir"; return 124
    fi
    sleep 0.1
  done
  _atd_rc=$(cat "$_atd_dir/rc")
  cat "$_atd_dir/out"; cat "$_atd_dir/err" >&2
  rm -rf "$_atd_dir"
  return "$_atd_rc"
}

# ah_test_release_ready <team file> <member> [no-tree|read-only|tree [<work tree>]]: gives the release check the input it reads and nothing
# more. The Orchestrator of the team files a `release` request addressed to the member, and the member's response carries the answer line
# `Release: ready ...` under `## [1] status`: `no-tree` (the member has no work tree, the default), `read-only` (a role that cannot
# commit; the check then reads its work tree), or `tree` (branch and HEAD of the given work tree). The check itself still runs on every
# close: a member that was never asked, or whose tree moved since, is held. The team file is <project>/.claude/ah2/teams/<team>.json; the
# member's role is read from it, or, for a member the file does not name, taken from the member name's last `-`-separated word.
ah_test_release_ready() {
  local _atrr_file=$1 _atrr_member=$2 _atrr_form=${3:-no-tree} _atrr_tree=${4:-}
  local _atrr_team _atrr_proj _atrr_role _atrr_plugin _atrr_out _atrr_id _atrr_req _atrr_line _atrr_body
  _atrr_team=$(basename "$_atrr_file" .json)
  _atrr_proj=$(cd "$(dirname "$_atrr_file")/../../.." && pwd -P) || return 1
  _atrr_plugin=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
  _atrr_role=$(node -e 'try{const t=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));const m=(t.members||[]).find((x)=>x.name===process.argv[2]);console.log(m&&m.role||"")}catch{console.log("")}' "$_atrr_file" "$_atrr_member")
  # a member the file does not name: the role of a role@session name, else AH_TEST_RELEASE_ROLE, else the name's last `-` word
  [ -n "$_atrr_role" ] || _atrr_role=${AH_TEST_RELEASE_ROLE:-}
  [ -n "$_atrr_role" ] || case "$_atrr_member" in *@*) _atrr_role=${_atrr_member%@*} ;; *) _atrr_role=${_atrr_member##*-} ;; esac
  case "$_atrr_form" in
    no-tree) _atrr_line="Release: ready no-tree inflight=none" ;;
    read-only) _atrr_line="Release: ready read-only inflight=none" ;;
    tree) _atrr_line="Release: ready branch=$(git -C "$_atrr_tree" rev-parse --abbrev-ref HEAD) sha=$(git -C "$_atrr_tree" rev-parse HEAD) inflight=none" ;;
    *) echo "ah_test_release_ready: unknown form $_atrr_form" >&2; return 1 ;;
  esac
  _atrr_out=$(ah_test_run_as orchestrator "$_atrr_team" "$([ -f "$_atrr_file" ] && echo "$_atrr_file")" "$_atrr_plugin/bin/ah" msg new --to "$_atrr_role" --to-name "$_atrr_member" --from orchestrator \
    --slug release --title "Check release readiness" --team "$_atrr_team" --cwd "$_atrr_proj" 2> "${TMPDIR:-/tmp}/ah-release-ready.err.$$") \
    || { echo "ah_test_release_ready: request failed: $_atrr_out $(cat "${TMPDIR:-/tmp}/ah-release-ready.err.$$" 2>/dev/null)" >&2; rm -f "${TMPDIR:-/tmp}/ah-release-ready.err.$$"; return 1; }
  rm -f "${TMPDIR:-/tmp}/ah-release-ready.err.$$"
  _atrr_id=$(printf '%s' "$_atrr_out" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.parse(s).id||"")}catch{console.log("")}})')
  _atrr_req=$(printf '%s' "$_atrr_out" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{console.log(JSON.parse(s).path||"")}catch{console.log("")}})')
  [ -n "$_atrr_req" ] || { echo "ah_test_release_ready: no request path in: $_atrr_out" >&2; return 1; }
  _atrr_body=$(printf '## [1] status\n\n%s\n' "$_atrr_line")
  # The answer is filed by the member, so the call runs proven as it: a member the team file names through ah_test_run_as, any other
  # (a live session the file never recorded) in a subshell registered under its name.
  if node -e 'try{const t=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));process.exit((t.members||[]).some((x)=>x.name===process.argv[2])?0:1)}catch{process.exit(1)}' "$_atrr_file" "$_atrr_member"; then
    _atrr_out=$(printf '%s' "$_atrr_body" | ah_test_run_as "$_atrr_member" "$_atrr_team" "$_atrr_file" "$_atrr_plugin/bin/ah" msg new --type response --id "$_atrr_id" --from "$_atrr_role" \
      --req "$_atrr_req" --team "$_atrr_team" --cwd "$_atrr_proj" --body-file - 2>&1) || { echo "ah_test_release_ready: response failed: $_atrr_out (id=$_atrr_id req=$_atrr_req)" >&2; return 1; }
  else
    _atrr_out=$(printf '%s' "$_atrr_body" | ( ah_test_prove_self "$_atrr_role" "$_atrr_team" "" "" "" "$BASHPID" > /dev/null || exit 97
      "$_atrr_plugin/bin/ah" msg new --type response --id "$_atrr_id" --from "$_atrr_role" --req "$_atrr_req" --team "$_atrr_team" --cwd "$_atrr_proj" --body-file - 2>&1; exit $? )) \
      || { echo "ah_test_release_ready: response failed: $_atrr_out (id=$_atrr_id req=$_atrr_req)" >&2; return 1; }
  fi
  # Filing the request proved a subshell as the team's Orchestrator, which moved the team file's owner to it. A suite that acts as the
  # Orchestrator itself sets AH_TEST_RELEASE_REPROVE=1 to take the file back.
  if [ "${AH_TEST_RELEASE_REPROVE:-}" = 1 ] && [ -f "$_atrr_file" ]; then ah_test_prove_self orchestrator "$_atrr_team" "$_atrr_file" > /dev/null || return 1; fi
}

# per-dir-files-to-db S7: the peer roster is roster_records rows, not <dir>/peers.jsonl.
# ah_seed_roster <hierarchy dir> <row json>...: appends each row to that dir's roster, as given.
ah_seed_roster() {
  local _asr_dir="$1"; shift
  for _asr_row in "$@"; do printf '%s' "$_asr_row" | AH_DAEMON=off sh "$PLUGIN/bin/ah" db test-seed peers --dir "$_asr_dir" >/dev/null || return 1; done
}
# ah_flush_roster <dir>/peers.jsonl: stores each line a suite just wrote to that file as a row of <dir>'s roster, then removes the file.
ah_flush_roster() {
  [ -f "$1" ] || return 0
  local _afr_rows; _afr_rows=$(cat "$1"); rm -f "$1"
  while IFS= read -r _afr_row; do [ -n "$_afr_row" ] && { ah_seed_roster "$(dirname "$1")" "$_afr_row" || return 1; }; done <<< "$_afr_rows"
  return 0
}
# ah_roster_rows <hierarchy dir>: that dir's roster rows, one JSON row per line, in append order ("" when none).
ah_roster_rows() {
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 -readonly "$AH_HOME/state/ah.db" "SELECT CAST(raw AS TEXT) FROM roster_records WHERE pool_dir IN ($(_ah_dirs "$1")) ORDER BY seq" 2>/dev/null
}
# ah_clear_roster <hierarchy dir>: removes every roster row of that dir (a suite's reset between steps).
ah_clear_roster() {
  [ -f "$AH_HOME/state/ah.db" ] || return 0
  sqlite3 "$AH_HOME/state/ah.db" "DELETE FROM roster_records WHERE pool_dir IN ($(_ah_dirs "$1"))"
}
