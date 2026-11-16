#!/bin/bash
# agent-hierarchy — `ah msg` CLI and the runtime dir it resolves: request/response
# skeletons, list, index, sweep, dir resolution. HOME- and
# WRANGL_HIERARCHY_DIR-redirected; real state untouched.
# Usage: bash tests/test-msg-cli.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-msg-test.XXXXXX")"
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)" # normalized: paths below are compared byte-for-byte against node's resolve()
FAKEHOME="$SANDBOX/home"
HD="$SANDBOX/hier"
mkdir -p "$FAKEHOME/.claude/ah2" "$HD/teams"
PASS=0; FAIL=0
ah_test_team "$HD" mc implementor:repo-implementor
ah_test_prove_self orchestrator mc "$HD/teams/mc.json" > /dev/null  # the Orchestrator the first sections write as: this shell

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:300})"; fi
}

msg() { OUT=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" "$PLUGIN/bin/ah" msg "$@" 2>&1); RC=$?; OUT=$(ah_drop_checklist_hint <<< "$OUT"); }

hier_of() { # <cwd> [WRANGL_HIERARCHY_DIR]: the runtime dir a `msg new` from <cwd> writes under; OUT is that dir
  local made
  made=$(env -u WRANGL_HIERARCHY_DIR HOME="$FAKEHOME" ${2:+WRANGL_HIERARCHY_DIR="$2"} "$PLUGIN/bin/ah" msg new --to implementor --from orchestrator --slug dir --title "Fix test titles" --cwd "$1" 2>&1); RC=$?; made=$(ah_drop_checklist_hint <<< "$made")
  local id file
  id=$(node -e 'try{process.stdout.write(JSON.parse(process.argv[1]).id)}catch{}' "$made")
  file=$([ -n "$id" ] && printf '%s' "$made" | ah_msg_path)
  OUT=$([ -n "$file" ] && dirname "$(dirname "$file")" || printf '%s' "$made")
}

# ---- 1: new request — skeleton, frontmatter, tldr index, .gitignore
msg new --to implementor --from orchestrator --slug peer-roster --title "Fix test titles" --to-name repo-implementor --reason context
check "new request: exit 0" '[ $RC -eq 0 ]'
ID=$(node -e 'const o=JSON.parse(process.argv[1]);process.stdout.write(o.id)' "$OUT")
REQ_PATH=$(printf '%s' "$OUT" | ah_msg_path)
# <id> <type> <file>: the stored message written to <file>, replacing what is there, so its bytes can be asserted
snap() { rm -f "$3"; msg show "$1" "$2" --out "$3"; }
REQ="$SANDBOX/req.md"; snap "$ID" request "$REQ"
check "new request: prints {id,path}" '[ -n "$ID" ] && [ -n "$REQ_PATH" ] && [ -f "$REQ" ]'
check "new request: id shape YYYYMMDD-HHMMSS-xxxx" 'echo "$ID" | grep -Eq "^[0-9]{8}-[0-9]{6}-[0-9a-z]{4}$"'
check "new request: filename <id>--<to>--<slug>--request.md" '[ "$(basename "$REQ_PATH")" = "$ID--implementor--peer-roster--request.md" ]'
check "new request: under <dir>/msgs/" '[ "$(dirname "$REQ_PATH")" = "$HD/msgs" ]'
check "frontmatter: flat, opens with ---" '[ "$(head -1 "$REQ")" = "---" ]'
check "frontmatter: type: request" 'grep -q "^type: request$" "$REQ"'
check "frontmatter: to/from" 'grep -q "^to: implementor$" "$REQ" && grep -q "^from: orchestrator$" "$REQ"'
check "frontmatter: to_name recorded" 'grep -q "^to_name: repo-implementor$" "$REQ"'
check "frontmatter: reason recorded" 'grep -q "^reason: context$" "$REQ"'
check "frontmatter: parent null" 'grep -q "^parent: null$" "$REQ"'
check "frontmatter: no nesting/lists" '! grep -Eq "^\s+-|^\s+[a-z_]+:" "$REQ"'
check "body: all 7 request anchors at column 0" '[ "$(grep -c "^## \[" "$REQ")" -eq 7 ]'
check "body: anchors in order" '[ "$(grep "^## \[" "$REQ" | tr "\n" " ")" = "## [0] tldr ## [1] goal ## [2] context ## [3] constraints ## [4] files ## [5] acceptance ## [6] want_back " ]'
check "body: tldr has one - [N] key: line per section" '[ "$(grep -c "^- \[[1-6]\] [a-z_]*: " "$REQ")" -eq 6 ]'
check "body: - none placeholders" '[ "$(grep -c "^- none$" "$REQ")" -eq 6 ]'
check ".gitignore written with *" '[ "$(cat "$HD/.gitignore")" = "*" ]'
check "layout: specs exists" '[ -d "$HD/specs" ]'

# ---- 2: arg validation
msg new --to nobody --from orchestrator --slug x --title "Fix test titles"
check "new: bad --to exits non-zero with one stderr line" '[ $RC -ne 0 ] && [ "$(echo "$OUT" | wc -l | tr -d " ")" = 1 ]'
msg new --to implementor --from orchestrator --slug "Bad Slug" --title "Fix test titles"
check "new: bad slug rejected" '[ $RC -ne 0 ]'
msg new --to implementor --from orchestrator --slug ok --title "Fix test titles" --reason whim
check "new: bad reason rejected" '[ $RC -ne 0 ]'
msg new --type response
check "response: --id required" '[ $RC -ne 0 ] && echo "$OUT" | grep -q -- "--id"'
msg new --type response --id 20990101-000000-zzzz
check "response: missing request rejected" '[ $RC -ne 0 ] && echo "$OUT" | grep -q "no request"'

# ---- 3: list open, then response closes it
msg list --plain
check "list --plain: id to slug age open" 'echo "$OUT" | grep -Eq "^$ID  implementor  peer-roster  [0-9]+[smhd]  open  Fix test titles$"'
msg list
check "list (json): open exchange present" 'echo "$OUT" | grep -q "\"state\":\"open\""'
msg list --closed --plain
check "list --closed: empty before response" '[ -z "$OUT" ]'
msg new --type response --id "$ID"
check "response: exit 0" '[ $RC -eq 0 ]'
RESP_PATH=$(printf '%s' "$OUT" | ah_msg_path)
RESP="$SANDBOX/resp.md"; snap "$ID" response "$RESP"
check "response: filename <id>--<orig from>--<slug>--response.md" '[ "$(basename "$RESP_PATH")" = "$ID--orchestrator--peer-roster--response.md" ]'
check "response: to/from swapped" 'grep -q "^to: orchestrator$" "$RESP" && grep -q "^from: implementor$" "$RESP"'
check "response: to_name/from_name swapped from request" 'grep -q "^from_name: repo-implementor$" "$RESP"'
check "response: 6 anchors" '[ "$(grep "^## \[" "$RESP" | tr "\n" " ")" = "## [0] tldr ## [1] status ## [2] changes ## [3] evidence ## [4] gaps ## [5] open_questions " ]'
msg new --type response --id "$ID"
check "response: duplicate rejected" '[ $RC -ne 0 ]'
# A member-addressed exchange closes only once its response holds a report.
fill_as() { OUT=$(HOME="$FAKEHOME" WRANGL_HIERARCHY_DIR="$HD" ah_test_run_as repo-implementor mc "$HD/teams/mc.json" "$PLUGIN/bin/ah" msg fill --id "$1" --from implementor --section 1 --body "$2" 2>&1); RC=$?; }
fill_as "$ID" '"- done: roster shipped\n"'
check "fill: the member files its report" '[ $RC -eq 0 ]'
snap "$ID" response "$RESP"
msg list --plain
check "list default (--open): closed exchange gone" '[ -z "$OUT" ]'
msg list --closed --plain
check "list --closed: shows it closed" 'echo "$OUT" | grep -Eq "^$ID  implementor  peer-roster  [0-9]+[smhd]  closed  Fix test titles$"'
msg list --all --plain --to implementor
check "list --all --to: filters by to" 'echo "$OUT" | grep -q "^$ID"'
msg list --all --plain --to reviewer
check "list --to other role: empty" '[ -z "$OUT" ]'

# ---- 4: index matches grep -n
msg index --plain "$ID" response
check "index --plain: matches grep -n '^## \[' exactly" '[ "$OUT" = "$(grep -n "^## \[" "$RESP")" ]'
msg index "$ID" response
check "index (json): line numbers present" 'echo "$OUT" | grep -q "\"line\":"'

# ---- 4b: show by id
msg show "$ID"
NL=$'\n'
check "show <id>: prints the request, frontmatter and body, then the two lines of the work footer" '[ $RC -eq 0 ] && [ "${OUT%%"$NL"work: *}" = "$(cat "$REQ")" ] && [ "${OUT%%"$NL"work: *}" != "$OUT" ] && [ "$(printf "%s\n" "$OUT" | tail -1 | cut -c1-16)" = "Record progress:" ]'
msg show "$ID" --section 0
check "show --section 0: the frontmatter and only section [0]" '[ $RC -eq 0 ] && echo "$OUT" | grep -qx "type: request" && echo "$OUT" | grep -q "^## \[0\]" && [ "$(echo "$OUT" | grep -c "^## \[")" = 1 ]'
msg show 20990101-000000-zzzz response
check "show of an unknown id: exit 1, no response <id>" '[ $RC -eq 1 ] && echo "$OUT" | grep -q "no response 20990101-000000-zzzz"'
msg show "$ID" --out "$SANDBOX/copy.md"
check "show --out: writes the message bytes" '[ $RC -eq 0 ] && cmp -s "$REQ" "$SANDBOX/copy.md"'
msg show "$ID" --out "$SANDBOX/copy.md"
check "show --out onto an existing file without --force: refused" '[ $RC -eq 2 ] && echo "$OUT" | grep -q -- "--force"'

# ---- 5: sweep archives only closed pairs older than N days
msg new --to reviewer --from orchestrator --slug still-open --title "Fix test titles"
OPEN_ID=$(node -e 'process.stdout.write(JSON.parse(process.argv[1]).id)' "$OUT")
msg sweep --days 0
check "sweep --days 0: archives the closed pair only" 'echo "$OUT" | grep -q "\"archived\":1"'
check "sweep: closed pair archived in the store" '[ "$(ah_msg_state "$ID")" = archived ]'
check "sweep: open request untouched" '[ -n "$(ah_msg_state "$OPEN_ID")" ] && ! ah_msg_state "$OPEN_ID" | grep -q archived'
msg new --to implementor --from orchestrator --slug fresh --title "Fix test titles"
FID=$(node -e 'const o=JSON.parse(process.argv[1]);process.stdout.write(o.id)' "$OUT")
msg new --type response --id "$FID"
fill_as "$FID" '"- done: reviewed\n"'
msg sweep --days 7
check "sweep --days 7: a fresh closed pair stays" 'echo "$OUT" | grep -q "\"archived\":0"'
msg sweep --plain --days 0
check "sweep --plain: prints the count" '[ "$OUT" = "1" ]'

# ---- 6: dir resolution — env override, project (.git walk-up), user fallback
mkdir -p "$SANDBOX/anything"
hier_of "$SANDBOX/anything"
check "hierarchy dir: no git, no env -> ~/.claude/ah2/hierarchy/<basename>" '[ "$OUT" = "$FAKEHOME/.claude/ah2/hierarchy/anything" ]'
mkdir -p "$SANDBOX/repo/.git" "$SANDBOX/repo/sub/deep"
hier_of "$SANDBOX/repo/sub/deep"
check "hierarchy dir: walks up to .git dir -> <root>/.claude/ah2" '[ "$OUT" = "$SANDBOX/repo/.claude/ah2" ]'
mkdir -p "$SANDBOX/wt/inner" && echo "gitdir: elsewhere" > "$SANDBOX/wt/.git"
hier_of "$SANDBOX/wt/inner"
check "hierarchy dir: .git FILE (worktree) counts" '[ "$OUT" = "$SANDBOX/wt/.claude/ah2" ]'
hier_of "$SANDBOX/repo/sub" "$SANDBOX/override"
check "hierarchy dir: WRANGL_HIERARCHY_DIR wins over git root" '[ "$OUT" = "$SANDBOX/override" ]'
hier_of "$SANDBOX/repo/sub"; hier_of "$SANDBOX/repo/sub"
check "hierarchy dir: ensured twice, project .gitignore = *" '[ "$(cat "$SANDBOX/repo/.claude/ah2/.gitignore")" = "*" ]'
hier_of "$SANDBOX/repo/sub"
check "hierarchy dir: a repeat resolves the same dir" '[ "$OUT" = "$SANDBOX/repo/.claude/ah2" ]'

# ---- 7: a report whose file is unchanged since it was delivered is sent once
msgd() { OUT=$(HOME="$FAKEHOME" AH_HOME="$SANDBOX/ah" AH_DAEMON=off WRANGL_HIERARCHY_DIR="$HD" ah_test_run_as_from dd "$HD/teams/dd.json" "$PLUGIN/bin/ah" msg "$@" 2>&1); RC=$?; OUT=$(ah_drop_checklist_hint <<< "$OUT"); }
rm -f "$HD/teams/mc.json"
mkdir -p "$HD/teams" "$SANDBOX/ah"
AH_TEST_TEAM_MESH_POLICY=open AH_TEST_TEAM_ORCH_PID=$$ AH_HOME="$SANDBOX/ah" ah_test_team "$HD" dd implementor
AH_HOME="$SANDBOX/ah" ah_test_prove_self orchestrator dd "$HD/teams/dd.json" > /dev/null
msgd new --to orchestrator --from implementor --slug dedupe
DID=$(node -e 'process.stdout.write(JSON.parse(process.argv[1]).id)' "$OUT")
msgd new --type response --id "$DID"
msgd fill --id "$DID" --from orchestrator --body '"## [0] tldr\n- [1] status: done\n\n## [1] findings\n- none\n"'
msgd send orchestrator@dd --ref "$DID" --kind report --id dd-1 --json
check "report send: first send delivers" '[ $RC -eq 0 ] && [ "$(node -e "process.stdout.write(JSON.parse(process.argv[1]).entry.id)" "$OUT")" = dd-1 ]'
msgd send orchestrator@dd --ref "$DID" --kind report --id dd-2
check "report send: the unchanged report is not sent again" '[ $RC -eq 0 ] && [ "$OUT" = "entry dd-1 → orchestrator@dd (already delivered; unchanged)" ]'
msgd fill --id "$DID" --from orchestrator --body '"## [0] tldr\n- [1] status: amended\n\n## [1] findings\n- none\n"'
msgd send orchestrator@dd --ref "$DID" --kind report --id dd-3 --json
check "report send: an amended report is delivered as a new entry" '[ $RC -eq 0 ] && [ "$(node -e "process.stdout.write(JSON.parse(process.argv[1]).entry.id)" "$OUT")" = dd-3 ]'
msgd send orchestrator@dd --ref "$DID" --kind report --id dd-4
check "report send by id: --ref <id> --kind report names the same response (unchanged, not sent again)" '[ $RC -eq 0 ] && [ "$OUT" = "entry dd-3 → orchestrator@dd (already delivered; unchanged)" ]'
msgd send orchestrator@dd --ref 20990101-000000-zzzz --kind report --id dd-5
check "report send by an unknown id: exit 1, no response <id>" '[ $RC -eq 1 ] && echo "$OUT" | grep -q "no response 20990101-000000-zzzz"'

# ---- a request file with no title line (written before titles existed) lists with no title column
UT_ID=$(date -u +%Y%m%d-%H%M%S)-uttl
mkdir -p "$HD/msgs"
printf -- '---\nid: %s\ntype: request\nto: implementor\nfrom: orchestrator\nslug: untitled\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: dd\ncreated: %s\n---\n\n## [0] tldr\n- none\n' \
  "$UT_ID" "$(date -u +%Y-%m-%dT%H:%M:%S+00:00)" > "$HD/msgs/$UT_ID--implementor--untitled--request.md"
AH_HOME="$SANDBOX/ah" ah_seed_msgs "$HD"
msgd list --plain
check "list --plain: a request with no title line ends at its state" 'echo "$OUT" | grep -Eq "^$UT_ID  implementor  untitled  [0-9]+[smhd]  open$"'

# ---- the test helper fails loudly rather than run as a caller it could not prove
OUT=$(ah_test_run_as_from mc "$HD/teams/mc.json" true --from ghost 2>&1); RC=$?
check "a --from naming no member of the team: the identity proof fails (exit 97) and says which" '[ $RC -eq 97 ] && echo "$OUT" | grep -q "identity proof failed for ghost@mc"'
OUT=$(AH_TEST_RUNS_HOOKS=1 ah_test_prove_self orchestrator mc "$HD/teams/mc.json" 2>&1); RC=$?
check "a suite that runs hooks cannot register its own shell" '[ $RC -ne 0 ] && echo "$OUT" | grep -q "AH_TEST_RUNS_HOOKS"'

# Frontmatter parsing and the model tier map are library functions with no CLI surface; the native unit tests own them
# (see tests/contracts/z-retired-suites.json, test-msg-cli).

# ah_drop_checklist_hint drops only the §2.2 hint, in both its forms; any other note, and the JSON, survive.
HINTS=$(printf '%s\n' '{"id":"x"}' 'note: 20261101-010437-1qpz was not on the checklist; added item _run/a and linked it.' \
  'note: 20261101-010437-1qpz is not on the checklist (progress is off for t). Link it: x.' 'ah msg: note — something else' \
  'note: 20261101-010437-1qpz is on the checklist' 'xnote: 20261101-010437-1qpz was not on the checklist' | ah_drop_checklist_hint)
check "checklist-hint filter: drops the two hint forms, keeps every other line" \
  '[ "$HINTS" = "$(printf "%s\n" "{\"id\":\"x\"}" "ah msg: note — something else" "note: 20261101-010437-1qpz is on the checklist" "xnote: 20261101-010437-1qpz was not on the checklist")" ]'

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
