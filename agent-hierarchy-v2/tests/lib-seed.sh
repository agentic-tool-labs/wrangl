# agent-hierarchy — seeding state from a suite through the native one-shot verb. Sourced after PLUGIN is set.
#
#   seed_state <domain> <op> <args-json> <hierarchy dir> [<cwd>]
#       One write through `ahd state <domain> <op>` (the request body on stdin, a fresh op id), the way a suite puts down state
#       it used to write by importing a Node library. The shim finds the binary through the test-mode environment the runner
#       gives (AH_BIN, AH_TEST=1, an AH_HOME under /tmp/ahprobe.*). HOME is the caller's: `HOME="$FAKEHOME" seed_session_role ...`.
#   seed_session_role <session id> <role>
#       The persisted role of a session (the `session-roles` domain, op `set`).

seed_state() {
  local cwd=${5:-$PWD}
  printf '{"args":%s,"context":{"cwd":"%s","hierarchy_dir":"%s"},"op_id":"seed-%s-%s"}' "$3" "$cwd" "$4" "$$" "$RANDOM" \
    | (cd "$cwd" && sh "$PLUGIN/bin/ahd" state "$1" "$2" --cwd "$cwd") >/dev/null
}

seed_session_role() {
  seed_state session-roles set "{\"session_id\":\"$1\",\"role\":\"$2\"}" "${HD:-${TMPDIR:-/tmp}}"
}

# seed_team_file <team file>: the team document on stdin, through `ah db test-seed teams`, so the plant follows the home's authority.
seed_team_file() {
  sh "$PLUGIN/bin/ah" db test-seed teams --path "$1" >/dev/null
}

# seed_session: the `mailbox register` arguments on stdin ({session_id, boxes: [{address, dir}], team_file, ...}).
seed_session() {
  sh "$PLUGIN/bin/ah" db test-seed sessions >/dev/null
}

# seed_mail <box dir>: one mail entry on stdin.
seed_mail() {
  sh "$PLUGIN/bin/ah" db test-seed mail --dir "$1" >/dev/null
}
