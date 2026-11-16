#!/bin/bash
# agent-hierarchy — `roster.mjs whoami` output is byte-identical across the identity extraction.
# Runs whoami over every outcome it can produce (env-launched member, pane member, no pane, no team,
# not a member, ambiguous, explicit --team, rejected WRANGL_TEAM_FILE, live orchestrator, registered pid) and
# compares the normalised outputs with tests/fixtures/whoami-golden.txt, recorded before the extraction.
# Usage: bash tests/test-whoami-identity.sh [--record]   (exits 0 iff the output equals the golden)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
H="$PLUGIN/hooks"; GOLDEN="$PLUGIN/tests/fixtures/whoami-golden.txt"
unset WRANGL_TEAM_FILE CLAUDE_PID CLAUDE_CODE_SESSION_ID WRANGL_HIERARCHY_DIR HERDR_PANE_ID TMUX_PANE CLAUDE_CODE_MESSAGING_SOCKET
SB="$(mktemp -d "${TMPDIR:-/tmp}/ah-whoami-id.XXXXXX")"
[ -n "$SB" ] && [ -d "$SB" ] || { echo "mktemp failed"; exit 1; }
sleep 600 & LIVE=$!
hermetic_on_exit 'ah_signal_child TERM $LIVE; rm -rf "$SB"'
SB="$(cd "$SB" && pwd -P)"
FAKEHOME="$SB/home"; PROJ="$SB/myrepo"; HIER="$PROJ/.claude/ah2"; OTHER="$SB/other"
mkdir -p "$FAKEHOME/.claude/ah2" "$HIER/teams" "$OTHER/.claude/ah2/teams" "$SB/socks"
(cd "$PROJ" && git init -q); (cd "$OTHER" && git init -q)
(exit 0) & DEAD=$!; wait "$DEAD"
. "$PLUGIN/tests/lib-seed.sh"
team() { # <file> <team_id> <orch pid> <orch session> <member name> <pane>: seeded through the product, teams being database rows
  HOME="$FAKEHOME" seed_team_file "$1" <<EOF
{"version":1,"team_id":"$2","orchestrator":{"session_id":$4,"pid":$3},"members":[{"role":"reviewer","name":"$5","route":"peer","transport_id":"$6"},{"role":"architect","name":"$5-arch","route":"subagent","transport_id":null}]}
EOF
}
team "$HIER/teams/alpha.json" ta "$LIVE" '"orch-a"' alpha-reviewer P1
team "$HIER/teams/beta.json" tb "$DEAD" null beta-reviewer P2
team "$HIER/teams/gamma.json" tg "$LIVE" null gamma-one P3
team "$HIER/teams/delta.json" td "$LIVE" null delta-one P3
ah_seed_roster "$HIER" '{"role":"reviewer","status":"up","session_id":"sess-1","pid":4242,"ts":"2026-01-01T00:00:00.000Z"}'
# a JSON-lines file that is no team file, for the "not shaped like one" case
printf '{"role":"reviewer","status":"up","session_id":"sess-1","pid":4242,"ts":"2026-01-01T00:00:00.000Z"}\n' > "$HIER/not-a-team.jsonl"
team "$OTHER/.claude/ah2/teams/alien.json" tx "$LIVE" null alien-one P1
printf 'not json' > "$HIER/teams/broken.json"
touch "$SB/socks/$LIVE.sock"
OUTFILE="$SB/actual.txt"; : > "$OUTFILE"
run() { # <label> <env assignments> [args...]
  local label=$1 envs=$2; shift 2
  local out rc
  out=$(env -u HERDR_PANE_ID -u TMUX_PANE -u CLAUDE_PID -u CLAUDE_CODE_MESSAGING_SOCKET -u CLAUDE_CODE_SESSION_ID -u WRANGL_TEAM_FILE HOME="$FAKEHOME" $envs "$PLUGIN/bin/ah" roster whoami "$@" --cwd "$PROJ" 2>&1); rc=$?
  printf '== %s (rc=%s)\n%s\n' "$label" "$rc" "$(printf '%s' "$out" | sed -e "s#$SB#\$SB#g" -e "s#\"pid\": $LIVE#\"pid\": LIVE#g" -e "s#$LIVE\\.sock#LIVE.sock#g" -e "s#\"pid\": $DEAD#\"pid\": DEAD#g")" >> "$OUTFILE"
}
run "no pane" ""
run "pane matches one member" "HERDR_PANE_ID=P1"
run "tmux pane" "TMUX_PANE=P2"
run "pane matches none" "HERDR_PANE_ID=P9"
run "pane matches two teams" "HERDR_PANE_ID=P3"
run "explicit team narrows the ambiguity" "HERDR_PANE_ID=P3" --team gamma
run "explicit nonexistent team" "HERDR_PANE_ID=P1" --team nosuch
run "explicit default team" "HERDR_PANE_ID=P1" --team @default
run "env team file, member by pane" "WRANGL_TEAM_FILE=$HIER/teams/alpha.json HERDR_PANE_ID=P1"
run "env team file, pane not a member" "WRANGL_TEAM_FILE=$HIER/teams/alpha.json HERDR_PANE_ID=P9"
run "env team file, no pane" "WRANGL_TEAM_FILE=$HIER/teams/alpha.json"
run "env team file that does not exist" "WRANGL_TEAM_FILE=$HIER/teams/ghost.json HERDR_PANE_ID=P1"
run "env team file of another repo" "WRANGL_TEAM_FILE=$OTHER/.claude/ah2/teams/alien.json HERDR_PANE_ID=P1"
run "env team file malformed" "WRANGL_TEAM_FILE=$HIER/teams/broken.json HERDR_PANE_ID=P1"
run "env team file not shaped like one" "WRANGL_TEAM_FILE=$HIER/not-a-team.jsonl HERDR_PANE_ID=P1"
run "dead orchestrator pid" "HERDR_PANE_ID=P2"
run "live orchestrator with a socket" "HERDR_PANE_ID=P1 CLAUDE_CODE_MESSAGING_SOCKET=$SB/socks/own.sock"
run "registered pid from the session id" "HERDR_PANE_ID=P1 CLAUDE_CODE_SESSION_ID=sess-1"
run "pid known, no pane env" "CLAUDE_PID=4242"
run "bad flag" "HERDR_PANE_ID=P1" --nope x

if [ "$1" = "--record" ]; then cp "$OUTFILE" "$GOLDEN"; echo "recorded $(grep -c '^== ' "$OUTFILE") scenarios"; exit 0; fi
if diff -u "$GOLDEN" "$OUTFILE" > "$SB/diff" 2>&1; then echo "PASS: whoami output identical to the golden ($(grep -c '^== ' "$OUTFILE") scenarios)"; echo; echo "Passed: 1  Failed: 0"; exit 0; fi
head -40 "$SB/diff"; echo "FAIL: whoami output differs from the golden"; echo; echo "Passed: 0  Failed: 1"; exit 1
