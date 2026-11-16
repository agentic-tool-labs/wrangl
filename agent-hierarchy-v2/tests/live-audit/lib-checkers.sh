#!/usr/bin/env bash
# Row checkers shared between phases. Sourced by lib-audit.sh.

# ---- one row: begin <id>; chk <label> <shell test>...; finish <function> <scenario> [note]. The checks file is the row's evidence.
FAILS=
chk() {
  local l=$1; shift
  if eval "$*" >/dev/null 2>&1; then echo "ok: $l" >>"$T/chk"; else echo "NOT: $l" >>"$T/chk"; FAILS="$FAILS; $l"; fi
}
begin() { CUR=$1; FAILS=; SPAWN_BROKEN=; : >"$T/chk"; T0=$SECONDS; }
finish() {
  local n=${3:-}
  cp "$T/chk" "$(ev_dir "$CUR")/checks.txt"
  if [ -n "$SPAWN_BROKEN" ]; then record "$CUR" "$1" "$2" NOT-TESTED "driver: $SPAWN_BROKEN"
  elif [ -z "$FAILS" ]; then record "$CUR" "$1" "$2" PASS "all checks held in $((SECONDS - T0)) s. $n"
  else record "$CUR" "$1" "$2" FAIL "failed:${FAILS#;} ($((SECONDS - T0)) s). $n"; fi
}
driver() { record "$CUR" "$1" "$2" NOT-TESTED "driver: $3"; }
keep() { out_of "$1" | scrub | ev "$CUR" "$1.txt"; }
# txlines <tx> <fixed text> <evidence name>: excerpt of the transcript lines holding the text.
txlines() { grep -F -- "$2" "$1" 2>/dev/null | scrub | cut -c1-1500 | head -20 | ev "$CUR" "$3"; }

# note_to <pane of sender> <script> <addressee name> <text>: a note, by a script in that session. entry_of <script> gives its id.
note_to() {
  script "$2" <<EOF
\$AH msg send "$3@\$TEAM" --text "$4" --kind note --json --cwd "\$REPO"
EOF
  run_in "$1" "$2" 120
}
# brief_to <script> <member name> <role> <slug> <body text>: orch files and sends a brief. req_of and entry_of <script> give the ids.
brief_to() {
  printf '%s\n' "$5" >"$T/b-$1"
  script "$1" <<EOF
N=\$(\$AH msg new --to $3 --from orchestrator --slug $4 --to-name $2 --title "Run the audit step" --team \$TEAM --eta small --body-file "$T/b-$1" --cwd "\$REPO")
echo "\$N"
ID=\$(printf '%s' "\$N" | sed -nE 's/.*"id": *"([^"]+)".*/\1/p' | head -1)
\$AH msg send "$2@\$TEAM" --ref "\$ID" --kind brief --json --cwd "\$REPO"
EOF
  run_in "$ORCH" "$1" 120
}
req_of() { out_of "$1" | sed -nE 's/.*"id": *"([0-9]{8}-[0-9]{6}-[a-z0-9]{4})".*/\1/p' | head -1; }

# set_gate_mode <advisory|enforce|any text>: merges into the user config, other keys kept. Each hook call reads it afresh.
set_gate_mode() { python3 -I - "$HOME/.claude/ah2/agent-hierarchy.json" "$1" <<'PY'
import json, os, sys
p, v = sys.argv[1:3]
d = json.load(open(p)) if os.path.exists(p) else {}
d["gate_mode"] = v
os.makedirs(os.path.dirname(p), exist_ok=True)
json.dump(d, open(p, "w"))
PY
}

# msg status answers only for the caller's own boxes and teams (not_your_session from a plain shell), so status evidence runs
# inside a session (status_in <pane> <entry> <name>) and the polled proof is the receiver's own transcript.

# m04_check <entry> <receiver transcript>: 0 only when that receiver's transcript shows `[ah-entry <id>` as a user turn, or as a
# queued_command attachment (how mail lands in a busy turn: Phase 1 saw it land that way and the first form alone called it lost).
# Negative controls: Phase 0 and M16 run this against a dead member; it must return non-zero there or every PASS it gives is void.
m04_lines() { grep -F "[ah-entry $1" "$2" 2>/dev/null | grep -E '"type":"user"|"type":"queued_command"'; }
m04_check() {
  [ -n "$1" ] && [ -n "$2" ] && [ -f "$2" ] || return 1
  m04_lines "$1" "$2" | grep -q .
}

# status_in <pane> <entry> <script name>: `ah msg status --json` run in that session; output in out_of <script name>.
status_in() {
  script "$3" <<EOF
\$AH msg status $2 --json --cwd "\$REPO"
EOF
  run_in "$1" "$3" 120
}

# first_entry <text>: the first entry id in the text.
first_entry() { printf '%s' "$1" | grep -oE '[0-9]{8}-[0-9]{6}-[a-z0-9]{4}' | head -1; }

# entries_in <text>: every entry id from `msg send --json` answers ({"entry":{"v":1,"id":"..."}), one per line. A request id never matches.
entries_in() { printf '%s' "$1" | grep -oE '"entry":\{"v":[0-9]+,"id":"[^"]+"' | sed -E 's/.*"id":"([^"]+)"/\1/'; }
entry_of() { entries_in "$(out_of "$1")" | head -1; }

# tool_text <tx> <tool> [text in its input]: the newest matching tool result's text.
tool_text() { tx_tool_results "$@" | tail -1 | cut -d' ' -f2-; }

# direct_in <pane> <tx> <command> [seconds]: one literal command as the session's own Bash call, for roles held to self-care, which
# will not run a script. 0 when its tool result exists. Output by tool_text.
direct_in() {
  say "$1" "Run exactly this one Bash command, then reply DONE: $3" || return 1
  poll "${4:-90}" "tool_done '$2' Bash '$3'"
}

# tx_tool_results <tx> <tool name> [text in its input]: for each matching tool_use, one line "<id> <result json>", or "<id> PENDING".
tx_tool_results() {
  [ -f "$1" ] || return 0
  python3 -I - "$@" <<'PY'
import json, sys
f, name = sys.argv[1:3]
needle = sys.argv[3] if len(sys.argv) > 3 else None
uses, res = {}, {}
for line in open(f, errors="replace"):
    try:
        d = json.loads(line)
    except Exception:
        continue
    m = d.get("message") or {}
    c = m.get("content") if isinstance(m, dict) else None
    if not isinstance(c, list):
        continue
    for b in c:
        if not isinstance(b, dict):
            continue
        if b.get("type") == "tool_use" and b.get("name") == name and (needle is None or needle in json.dumps(b.get("input"))):
            uses[b.get("id")] = 1
        if b.get("type") == "tool_result" and b.get("tool_use_id"):
            res[b["tool_use_id"]] = {"content": b.get("content"), "is_error": b.get("is_error"), "toolUseResult": d.get("toolUseResult")}
for u in uses:
    print(u, json.dumps(res[u]) if u in res else "PENDING")
PY
}
tool_done() { local r; r=$(tx_tool_results "$@"); [ -n "$r" ] && ! printf '%s\n' "$r" | grep -q ' PENDING$'; }

# in_tx <tx> <fixed text>: the text appears in a user turn of the transcript.
in_tx() { [ -f "$1" ] && grep -F -- "$2" "$1" | grep -q '"type":"user"'; }
