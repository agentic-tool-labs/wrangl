#!/bin/bash
# Lane smoke: a real member Claude session runs the whole member lifecycle with zero denials.
# It is launched sandboxed on freshly generated settings by `ah roster spawn-one --headless`.
# It spends real model tokens, so it runs only when asked:
#   bash <abs>/lane-smoke.sh              the smoke; exit 0 when every assertion holds
#   bash <abs>/lane-smoke.sh --negative   drops the ah path from excludedCommands; exit 0 when the smoke FAILS as it must
set -u

AH="${AH:-$HOME/.local/bin/ah}"
TIMEOUT_SECS=600
NEGATIVE=0
[ "${1:-}" = "--negative" ] && NEGATIVE=1

T=$(mktemp -d /tmp/ahprobe.XXXXXX)
[ -n "$T" ] && [ -d "$T" ] || { echo "no scratch dir" >&2; exit 2; }
REPO="$T/repo"
# The default ah home is used: ah refuses an untrusted AH_HOME. Isolation is a unique team and a fixture repo whose own
# .claude/ah2 holds the project state.
TEAM="smoke-$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')"
STARTED=""   # pids this script started
FAILS=0
ROWS=""

PSTART=""    # ps lstart of the member at launch; a signal goes only to a pid that still has this start time
TEAM_MADE=0
DISBANDED=0

same_proc() { [ -n "$PSTART" ] && [ "$(ps -o lstart= -p "$1" 2>/dev/null | sed 's/^ *//')" = "$PSTART" ]; }
safe_kill() { same_proc "$2" && kill "-$1" "$2" 2>/dev/null; }

disband_team() {
  [ "$TEAM_MADE" -eq 1 ] && [ "$DISBANDED" -eq 0 ] || return 0
  DISBANDED=1
  PLAN=$(cd "$REPO" && "$AH" roster disband --plan --team $TEAM --cwd "$REPO" 2>&1)
  TOK=$(printf '%s' "$PLAN" | grep -oE '"plan_token": *"[^"]*"' | head -1 | cut -d'"' -f4)
  [ -n "$TOK" ] && (cd "$REPO" && "$AH" roster disband --close --confirm --plan-token "$TOK" --team $TEAM --cwd "$REPO" > "$T/disband.out" 2>&1)
  return 0
}

cleanup() {
  disband_team
  for p in $STARTED; do safe_kill TERM "$p"; done
  sleep 2
  for p in $STARTED; do safe_kill KILL "$p"; done
  # anything still running out of the scratch dir
  pgrep -f "$T" 2>/dev/null | grep -vx "$$" | while read -r p; do kill -TERM "$p" 2>/dev/null; done
  sleep 1
  pgrep -f "$T" 2>/dev/null | grep -vx "$$" | while read -r p; do kill -KILL "$p" 2>/dev/null; done
  rm -rf "$T"
}
trap cleanup EXIT

check() { # name, ok(0/1), detail
  if [ "$2" -eq 0 ]; then ROWS="$ROWS
PASS  $1  $3"; else ROWS="$ROWS
FAIL  $1  $3"; FAILS=$((FAILS + 1)); fi
}
die() { echo "lane-smoke: $*" >&2; exit 2; }
json() { python3 -c 'import json,sys
raw=sys.stdin.read()
try:
    d=json.loads(raw)
except ValueError:
    sys.stderr.write("not JSON: " + (raw[:300] or "<empty>") + "\n"); sys.exit(1)
for k in sys.argv[1:]:
    d=d.get(k) if isinstance(d,dict) else None
print("" if d is None else d)' "$@"; }
# files under the project and the ah home written since the run started
recent_files() { find "$REPO/.claude" "$HOME/.claude/ah2" -type f -newer "$T/start-mark" 2>/dev/null; }
first_id() { grep -oE '[0-9]{8}-[0-9]{6}-[a-z0-9]{4}' | head -1; }

# ---- fixture repo: one commit, note.txt, a test the member runs
mkdir -p "$REPO" "$T/bin"
git -C "$REPO" init -q -b main
git -C "$REPO" config user.email smoke@example.invalid
git -C "$REPO" config user.name smoke
echo "start" > "$REPO/note.txt"
printf '#!/bin/sh\nexit 0\n' > "$REPO/test.sh"
git -C "$REPO" add note.txt test.sh
git -C "$REPO" commit -q -m "fixture"

# ---- a claude shim: records the settings the launch really used and, for --negative, drops the ah path from excludedCommands
REAL_CLAUDE=$(command -v claude) || { echo "no claude on PATH" >&2; exit 2; }
cat > "$T/bin/claude" <<'PY'
#!/usr/bin/env python3
import json, os, sys
argv = sys.argv[1:]
out = os.environ["LS_CAPTURE"]
brk = os.environ.get("LS_BREAK") == "1"
def load(v):
    return json.load(open(v)) if os.path.isfile(v) else json.loads(v)
def drop(node):
    if isinstance(node, dict):
        for k in list(node):
            if k == "excludedCommands" and isinstance(node[k], list):
                node[k] = [e for e in node[k] if not (e.rstrip("*").strip().endswith("/ah"))]
            else:
                drop(node[k])
    elif isinstance(node, list):
        for x in node:
            drop(x)
for i, a in enumerate(argv):
    if a == "--settings" and i + 1 < len(argv):
        s = load(argv[i + 1])
        json.dump(s, open(out, "w"))
        if brk:
            drop(s)
            broken = out + ".broken"
            json.dump(s, open(broken, "w"))
            argv[i + 1] = broken
os.execv(os.environ["LS_REAL_CLAUDE"], [os.environ["LS_REAL_CLAUDE"]] + argv)
PY
chmod +x "$T/bin/claude"
export LS_CAPTURE="$T/launch-settings.json" LS_REAL_CLAUDE="$REAL_CLAUDE"
[ "$NEGATIVE" -eq 1 ] && export LS_BREAK=1
export PATH="$T/bin:$PATH"

# ---- team, stream, member name
TEAM_MADE=1
(cd "$REPO" && "$AH" roster stream-open s --no-worktree --team $TEAM --cwd "$REPO" > "$T/stream.out" 2>&1) || die "stream-open failed: $(head -c 400 "$T/stream.out")"
(cd "$REPO" && "$AH" roster spawn-one implementor --team $TEAM --stream s --headless --prompt x --dry-run --model haiku --cwd "$REPO" > "$T/dry.out" 2>&1) || die "spawn-one --dry-run failed: $(head -c 400 "$T/dry.out")"
MEMBER=$(json name < "$T/dry.out" 2> "$T/dry.err") || die "spawn-one --dry-run gave no JSON: $(head -c 400 "$T/dry.err")"
[ -n "$MEMBER" ] || die "no member name in the dry run: $(head -c 400 "$T/dry.out")"

# ---- the brief, sent before the launch; the delivery line is the -p prompt
cat > "$T/brief.txt" <<EOF
Run these in order, each as its own plain Bash call, then report.
1. Append one line to $REPO/note.txt and commit it: git -C $REPO add note.txt && git -C $REPO commit -m smoke
2. Run exactly: $AH --version
3. Run exactly: $AH --version; echo chained-probe   (this one is expected to be refused; carry on)
4. Run: $AH test run -- sh $REPO/test.sh
5. Write your response with: $AH msg new --type response, fill it, send it, and finish with: DONE smoke
EOF
"$AH" msg new --to implementor --from orchestrator --slug lane-smoke --to-name "$MEMBER" --title "Run the lane smoke" --team $TEAM --eta small --body-file "$T/brief.txt" --cwd "$REPO" > "$T/new.out" 2>&1 || die "msg new failed: $(head -c 400 "$T/new.out")"
REQ_ID=$(json id < "$T/new.out" 2> "$T/new.err") || die "msg new gave no JSON: $(head -c 400 "$T/new.err")"
REQ=$(find "$REPO" -path "*/msgs/$REQ_ID--*--request.md" 2>/dev/null | head -1)
[ -n "$REQ" ] && [ -n "$REQ_ID" ] || die "msg new gave no path or id: $(head -c 400 "$T/new.out")"
"$AH" msg send "$MEMBER@$TEAM" --ref "$REQ" --kind brief --from orchestrator@$TEAM --json --cwd "$REPO" > "$T/send.out" 2>&1 || die "msg send failed: $(head -c 400 "$T/send.out")"
ENTRY=$(first_id < "$T/send.out")
[ -n "$ENTRY" ] || die "msg send printed no entry id: $(head -c 400 "$T/send.out")"
LINE="ah mail from orchestrator@$TEAM to $MEMBER@$TEAM (brief): [hierarchy-msg $REQ] [ah-entry $ENTRY to $MEMBER@$TEAM]"

# hook-errors is shared, so its row count before the run is the baseline
HOOK_LOG="$HOME/.claude/ah2/hierarchy/hook-errors.jsonl"
HOOKS_BEFORE=$(wc -l < "$HOOK_LOG" 2>/dev/null || echo 0)
touch "$T/start-mark"

# ---- launch on freshly generated settings
(cd "$REPO" && "$AH" roster spawn-one implementor --team $TEAM --stream s --headless --prompt "$LINE" --model haiku --cwd "$REPO" > "$T/launch.out" 2>&1) || die "spawn-one --headless failed: $(head -c 600 "$T/launch.out")"
PID=$(grep -oE 'headless:[0-9]+' "$T/launch.out" | head -1 | cut -d: -f2)
[ -n "$PID" ] || die "the launch printed no headless pid: $(head -c 600 "$T/launch.out")"
PSTART=$(ps -o lstart= -p "$PID" 2>/dev/null | sed 's/^ *//')
[ -n "$PSTART" ] || die "the launched pid $PID has no start time; it is not running"
STARTED="$PID"

# ---- the launch file's excludedCommands holds both ah forms
python3 - "$LS_CAPTURE" > "$T/excl.out" 2>&1 <<'PY'
import json, sys
def find(n):
    if isinstance(n, dict):
        for k, v in n.items():
            if k == "excludedCommands": yield v
            else: yield from find(v)
    elif isinstance(n, list):
        for x in n: yield from find(x)
try:
    s = json.load(open(sys.argv[1]))
except Exception as e:
    print("no captured settings:", e); sys.exit(1)
ex = [e for l in find(s) for e in l]
bare = [e for e in ex if e.endswith("/ah") and (e + " *") in ex]
print("excludedCommands:", ex)
sys.exit(0 if bare else 1)
PY
check "excludedCommands has AhPath and AhPath *" $? "$(head -c 300 "$T/excl.out")"

# ---- wait for the member, with a timeout
waited=0
while kill -0 "$PID" 2>/dev/null && [ "$waited" -lt "$TIMEOUT_SECS" ]; do sleep 2; waited=$((waited + 2)); done
kill -0 "$PID" 2>/dev/null || STARTED=""   # gone: its pid may be reused, so nothing is signalled later
if [ -n "$STARTED" ]; then check "(a) member exited" 1 "still running after ${TIMEOUT_SECS}s"; else check "(a) member exited" 0 "after ${waited}s"; fi
# claude -p is not our child, so its exit code is not observable here; the transcript and log carry the outcome

# ---- the member's row still holds the spawned pid (the member's own SessionStart must not rewrite it)
TEAMFILE=$(find "$REPO/.claude" "$HOME/.claude/ah2" -name "$TEAM.json" -path '*teams*' 2>/dev/null | head -1)
ROWPID=$(python3 - "${TEAMFILE:-/nonexistent}" "$MEMBER" 2>/dev/null <<'PY'
import json, sys
try:
    for m in json.load(open(sys.argv[1])).get("members", []):
        if m.get("name") == sys.argv[2]:
            print(m.get("pid", ""))
except (OSError, ValueError):
    pass
PY
)
check "row pid is the spawned pid" "$([ "$ROWPID" = "$PID" ]; echo $?)" "row=${ROWPID:-none} spawned=$PID team_file=${TEAMFILE:-none}"

# ---- (b) denial rows
DENIED=$(recent_files | xargs grep -hE 'CLASSIFIER-DENIED|CLASSIFIER-NO-VERDICT|AH-DENIED' 2>/dev/null | grep -c "$MEMBER")
check "(b) zero CLASSIFIER/AH-DENIED rows" "$([ "$DENIED" -eq 0 ]; echo $?)" "rows=$DENIED"

# ---- (c) hook errors added during the run
HOOKS_AFTER=$(wc -l < "$HOOK_LOG" 2>/dev/null || echo 0)
check "(c) zero hook errors" "$([ "$HOOKS_AFTER" -le "$HOOKS_BEFORE" ]; echo $?)" "before=$HOOKS_BEFORE after=$HOOKS_AFTER"

# ---- (d) transcript: no denied tool_result except the one deliberate chained call; bare ah passes, chained fails
TRANSCRIPT=$(find "$HOME/.claude/projects" -name '*.jsonl' -newer "$T/start-mark" -print0 2>/dev/null | xargs -0 grep -l "$REPO" 2>/dev/null | head -1)
python3 - "${TRANSCRIPT:-/nonexistent}" "$AH" > "$T/tr.out" 2>&1 <<'PY'
import json, re, sys
path, ah = sys.argv[1], sys.argv[2]
bad = re.compile(r"permission|denied|not allowed|operation not permitted|exit code 126", re.I)
uses, results = {}, {}
try:
    for line in open(path):
        try: d = json.loads(line)
        except ValueError: continue
        c = (d.get("message") or {}).get("content")
        if not isinstance(c, list): continue
        for b in c:
            if b.get("type") == "tool_use": uses[b["id"]] = (b.get("input") or {}).get("command", "")
            if b.get("type") == "tool_result": results[b["tool_use_id"]] = (bool(b.get("is_error")), json.dumps(b.get("content")))
except OSError as e:
    print("no transcript:", e); sys.exit(1)
problems, bare_ok, chained_refused = [], False, False
for tid, (err, text) in results.items():
    cmd = uses.get(tid, "")
    if cmd.strip() == ah + " --version": bare_ok = not err
    elif "chained-probe" in cmd: chained_refused = err
    elif err and bad.search(text): problems.append(cmd[:80])
print("denied:", problems, "bare_ok:", bare_ok, "chained_refused:", chained_refused)
sys.exit(0 if not problems and bare_ok and chained_refused else 1)
PY
check "(d) no denied tool_result; bare ah passes, chained fails" $? "$(head -c 300 "$T/tr.out")"

# ---- (e) the commit happened
COMMITS=$(git -C "$REPO" rev-list --all --count)
NOTE_LINES=$(wc -l < "$REPO/note.txt")
check "(e) note.txt changed and committed" "$([ "$COMMITS" -ge 2 ] && [ "$NOTE_LINES" -ge 2 ] && git -C "$REPO" diff --quiet HEAD -- note.txt; echo $?)" "commits=$COMMITS lines=$NOTE_LINES"

# ---- (f) the test-run record
TESTREC=$(recent_files | grep -iE 'test.?run' | head -1)
check "(f) test-run record exists" "$([ -n "$TESTREC" ]; echo $?)" "${TESTREC:-none}"

# ---- (g) the response exists and the exchange is closed
RESP=$(find "$(dirname "$REQ")" -name "*${REQ_ID}*--response.md" 2>/dev/null | head -1)
CLOSED=$("$AH" msg list --closed --team $TEAM --json --cwd "$REPO" 2>/dev/null | grep -c "$REQ_ID")
check "(g) response exists and exchange closed" "$([ -n "$RESP" ] && [ "$CLOSED" -gt 0 ]; echo $?)" "response=${RESP:-none} closed_rows=$CLOSED"

# ---- (h) no FAILSAFE for the request
FAILSAFE=$(recent_files | xargs grep -h 'FAILSAFE' 2>/dev/null | grep -c "$REQ_ID")
check "(h) no FAILSAFE line" "$([ "$FAILSAFE" -eq 0 ]; echo $?)" "lines=$FAILSAFE"

# ---- teardown: disband the smoke team, then leave nothing running
disband_team
for p in $STARTED; do safe_kill TERM "$p"; done; sleep 2; for p in $STARTED; do safe_kill KILL "$p"; done
LEFT=$(pgrep -f "$T" | grep -vxc "$$")
check "no leftover processes" "$([ "$LEFT" -eq 0 ]; echo $?)" "pgrep -f scratch=$LEFT"

echo "lane smoke ($([ "$NEGATIVE" -eq 1 ] && echo negative || echo live)) member=$MEMBER pid=$PID$ROWS"
if [ "$NEGATIVE" -eq 1 ]; then
  [ "$FAILS" -gt 0 ] && { echo "NEGATIVE OK: the smoke failed with excludedCommands broken ($FAILS rows)"; exit 0; }
  echo "NEGATIVE BAD: the smoke passed with excludedCommands broken"; exit 1
fi
[ "$FAILS" -eq 0 ]
