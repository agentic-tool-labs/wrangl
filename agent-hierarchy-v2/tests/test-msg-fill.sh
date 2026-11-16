#!/bin/bash
# agent-hierarchy — spec 0090: `msg.mjs fill` writes a report body into the response file `new --type response` made, keeping the
# frontmatter byte for byte. A valid fill, a repeat, the trailing newline, the body encoding (`\n`, `'`), every refusal with
# nothing written, the 256 KiB edge, and a worktree --cwd against a main-checkout request. Each refusal case also checks the `fill:`
# message, since an unknown verb exits 2 as well.
# HOME-redirected; real state untouched.
# Usage: bash tests/test-msg-fill.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-fill-test.XXXXXX")"
[ -n "$SANDBOX" ] && [ -d "$SANDBOX" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
FAKEHOME="$SANDBOX/home"
mkdir -p "$FAKEHOME/.claude/ah2"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:500})"; fi
}

MAIN="$SANDBOX/main"; WT="$SANDBOX/wt"
mkdir -p "$MAIN/.claude/ah2"
(cd "$MAIN" && git init -q && git config user.email t@t.com && git config user.name t && git commit -q --allow-empty -m init)
git -C "$MAIN" worktree add -q "$WT" -b wt-branch >/dev/null
mkdir -p "$WT/.claude/ah2"
MSGS="$MAIN/.claude/ah2/msgs"
# this shell is the Orchestrator that mints requests; a call made --from a member runs in a subshell proven as that member (an Orchestrator session owns no member box)
HD="$MAIN/.claude/ah2"; mkdir -p "$HD/teams"
printf '{"team_id":"t-fl","transport":"tmux","orchestrator":{"session_id":"s-fl"},"members":[{"role":"reviewer","name":"reviewer","transport_id":"p1"},{"role":"architect","name":"architect","transport_id":"p2"}]}\n' > "$HD/teams/fl.json"
ah_test_prove_self orchestrator fl "$HD/teams/fl.json" >/dev/null

mcli() { # <cwd> <msg.mjs argv...>: stdout and stderr together in OUT; a call --from a member is made as that member
  local at=$1; shift
  local who=orchestrator i j
  for ((i = 1; i <= $#; i++)); do [ "${!i}" = --from ] && { j=$((i + 1)); who=${!j}; }; done
  who=${PROVE_AS:-$who}  # PROVE_AS: the caller is this member whatever --from claims
  OUT=$([ "$who" = orchestrator ] || ah_test_prove_self "$who" fl "$HD/teams/fl.json" "" "" "$BASHPID" >/dev/null; HOME="$FAKEHOME" "$PLUGIN/bin/ah" msg "$@" --cwd "$at" 2>&1); RC=$?
  [ -n "${ID:-}" ] && [ -n "${RESP:-}" ] && snap "$ID" response "$RESP" && snap "$ID" request "$REQ"
  return 0
}
# <id> <type> <file>: the stored message as a file, so its bytes can be asserted; a message that is not stored leaves no file.
snap() { rm -f "$3"; HOME="$FAKEHOME" "$PLUGIN/bin/ah" msg show "$1" "$2" --out "$3" --cwd "$MAIN" >/dev/null 2>&1; return 0; }
json_id() { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.stdout.write(JSON.parse(s.split("\n").find(x=>x.startsWith("{"))).id))'; }
jstr() { printf %s "$1" | node -e 'process.stdout.write(JSON.stringify(require("fs").readFileSync(0, "utf8")))'; }
# <slug>: a request to the reviewer and the response `new` made for it. Sets ID, and REQ and RESP, files that mcli keeps equal to the stored messages.
mint() {
  ID=""; RESP=""
  mcli "$MAIN" new --to reviewer --from orchestrator --slug "$1" --title "Fix test titles"
  ID=$(printf '%s' "$OUT" | json_id); REQ="$SANDBOX/$1.req.md"; RESP="$SANDBOX/$1.resp.md"
  mcli "$MAIN" new --type response --id "$ID" --from reviewer
}
# The frontmatter block of a response: through its closing fence.
fm_of() { awk 'NR==1 && $0=="---" {print; infm=1; next} infm {print} infm && $0=="---" {exit}' "$1"; }
fill() { # <body text> [extra fill args...]: fill RESP's request with the JSON string of the text
  local text=$1; shift
  mcli "$MAIN" fill --id "$ID" --from reviewer --body "$(jstr "$text")" "$@"
}
BODY=$'## [0] tldr\n- [1] status: PASS\n\n## [1] findings\n- none\n'

# ---- F1: a valid fill on a response made by `new`.
mint f1
fm_of "$RESP" > "$SANDBOX/fm.before"
fill "$BODY"
check "F1: a valid fill exits 0 and prints {id, type, path, rev, bytes} in that order" '[ $RC = 0 ] && [ "$(printf %s "$OUT" | node -e "const o=JSON.parse(require(\"fs\").readFileSync(0,\"utf8\"));process.stdout.write(Object.keys(o).join())")" = "id,type,path,rev,bytes" ]'
check "F1: the type is response, and bytes is the size of the file written" '[ "$(printf %s "$OUT" | node -e "process.stdout.write(JSON.parse(require(\"fs\").readFileSync(0,\"utf8\")).type)")" = "response" ] && [ "$(printf %s "$OUT" | node -e "process.stdout.write(String(JSON.parse(require(\"fs\").readFileSync(0,\"utf8\")).bytes))")" = "$(wc -c < "$RESP" | tr -d " ")" ]'
fm_of "$RESP" > "$SANDBOX/fm.after"
check "F1: the frontmatter bytes are identical before and after (cmp)" 'cmp -s "$SANDBOX/fm.before" "$SANDBOX/fm.after" && [ -s "$SANDBOX/fm.before" ]'
check "F1 (r2): the line after the closing fence is empty when the body starts with ## [0], as new's skeleton has it" '[ -z "$(sed -n "$(( $(wc -l < "$SANDBOX/fm.after") + 1 ))p" "$RESP")" ] && [ "$(sed -n "$(( $(wc -l < "$SANDBOX/fm.after") + 2 ))p" "$RESP")" = "## [0] tldr" ]'
check "F1: everything after that empty line is the body, and the skeleton's empty sections are gone" '[ "$(tail -n +$(( $(wc -l < "$SANDBOX/fm.after") + 2 )) "$RESP")" = "${BODY%$'"'"'\n'"'"'}" ] && ! grep -q "^## \[2\]" "$RESP"'

# ---- F2 (b485): a repeat puts the new body first and keeps the first body below `## earlier report`.
cp "$RESP" "$SANDBOX/f2.before"
fill $'## [0] tldr\n- [1] status: CHANGES REQUIRED\n'
check "F2: the second fill keeps the first body below ## earlier report, frontmatter still identical" '[ $RC = 0 ] && [ "$(tail -n +$(( $(wc -l < "$SANDBOX/fm.after") + 1 )) "$RESP")" = "$(printf "\n## [0] tldr\n- [1] status: CHANGES REQUIRED\n## earlier report\n"; tail -n +$(( $(wc -l < "$SANDBOX/fm.after") + 1 )) "$SANDBOX/f2.before")" ] && fm_of "$RESP" | cmp -s - "$SANDBOX/fm.before"'
check "F2: status and tldr read from the second report" '[ "$(grep -m1 -- "- \[1\] status:" "$RESP")" = "- [1] status: CHANGES REQUIRED" ] && HOME="$FAKEHOME" "$PLUGIN/bin/ah" msg show "$ID" response --section 0 --cwd "$MAIN" 2>&1 | grep -q "status: CHANGES REQUIRED"'

# ---- F3: the single quote, the encoding of newlines, and the trailing newline.
QUOTED=$'- it\'s fine\n- a "quoted" word and a \\ backslash'
fill "$QUOTED"
check "F3: a body containing it's lands as it's, with the double quote and the backslash" '[ $RC = 0 ] && grep -qF -- "- it'"'"'s fine" "$RESP" && grep -qF -- "a \"quoted\" word and a \\ backslash" "$RESP"'
check "F3: a body without a trailing newline is written with exactly one" '[ "$(tail -c 1 "$RESP" | od -An -c | tr -d " ")" = "\\n" ] && [ "$(tail -c 2 "$RESP" | od -An -c | tr -d " ")" != "\\n\\n" ]'
# The form the Reviewer sends: a newline is backslash-n, a single quote is backslash-u0027 (a literal one cannot sit inside the shell's single quotes).
BS=$(printf '\134')
mint f3  # b485: a repeat keeps the earlier body at the end, so the tail check needs a first fill
mcli "$MAIN" fill --id "$ID" --from reviewer --body "\"line one${BS}nline two${BS}u0027s${BS}n\""
WANT=$(printf 'line one\nline two'"'"'s')
check "F3: the escaped form decodes (newline, single quote), and a body already ending in a newline gets no second one" '[ $RC = 0 ] && [ "$(tail -n 2 "$RESP")" = "$WANT" ] && [ "$(tail -c 2 "$RESP" | od -An -c | tr -d " ")" != "\\n\\n" ]'
mcli "$MAIN" fill --id "$ID" --from reviewer --body "$(jstr "$QUOTED")"
check "F3: ...and the same text as a plain JSON string with a raw single quote lands the same way" '[ $RC = 0 ] && grep -qF -- "- it'"'"'s fine" "$RESP"'
mcli "$MAIN" fill --id "$ID" --from reviewer --body "$(jstr "$BODY")" --plain
check "F3: --plain prints \`<path>  <id>\`, as msg new does" '[ $RC = 0 ] && [ "$(printf "%s" "$OUT" | awk "{print \$2}")" = "$ID" ] && [ -n "$(printf "%s" "$OUT" | awk "{print \$1}")" ]'

# ---- R: refusals. Exit 2, the `fill:` message, and the response untouched.
mint r1
cp "$RESP" "$SANDBOX/r1.before"
refused() { # <label> <expected text in the message>
  local label=$1 want=$2
  check "R: $label is refused (exit 2, '$want'), the response untouched" '[ $RC = 2 ] && printf %s "$OUT" | grep -qF -- "$want" && printf %s "$OUT" | grep -q "fill:" && cmp -s "$RESP" "$SANDBOX/r1.before"'
}
mcli "$MAIN" fill --id "$ID" --from architect --body "$(jstr "$BODY")"
refused "--from architect on a from: reviewer response" 'is from "reviewer", not "architect"'
mcli "$MAIN" fill --id "$ID" --from reviewerrefused "a missing --body" '--body is required'
mcli "$MAIN" fill --id "$ID" --from reviewer --body 'not json'
refused "a --body that is not JSON" '--body must be a JSON string'
mcli "$MAIN" fill --id "$ID" --from reviewer --body '42'
refused "a JSON number" 'not another JSON value'
mcli "$MAIN" fill --id "$ID" --from reviewer --body '{"a":1}'
refused "a JSON object" 'not another JSON value'
mcli "$MAIN" fill --id "$ID" --from reviewer --body '"   "'
refused "a body of only spaces" '--body is empty'
mcli "$MAIN" fill --id "$ID" --from reviewer --body '"\n \n"'
refused "a body of only newlines and spaces" '--body is empty'
mcli "$MAIN" fill --id "$ID" --from reviewer --body "$(jstr $'## [0] tldr\n---\n- [1] status: PASS\n')"
refused "a body with a line that is exactly ---" 'exactly ---'
BIG=$(node -e 'process.stdout.write(JSON.stringify("a".repeat(262145)))')
mcli "$MAIN" fill --id "$ID" --from reviewer --body "$BIG"
refused "a body of 262145 bytes" 'over 262144 bytes'
BIGMB=$(node -e 'process.stdout.write(JSON.stringify("é".repeat(131073)))')
mcli "$MAIN" fill --id "$ID" --from reviewer --body "$BIGMB"
refused "a body of 131073 two-byte characters (262146 bytes: the size is bytes, not characters)" 'over 262144 bytes'

# ---- N: the nearest inputs that must still work.
fill $'| a | b |\n|---|---|\n----\n--- x\n- ok\n'
check "N: a table separator, a four-dash line and '--- x' are not a bare --- line: written" '[ $RC = 0 ] && grep -qF "|---|---|" "$RESP" && grep -qxF -- "----" "$RESP" && grep -qxF -- "--- x" "$RESP"'
mcli "$MAIN" fill --id "$ID" --from reviewer --body "$(node -e 'process.stdout.write(JSON.stringify("a".repeat(261000)))')"
check "N: a body that fits a sealed message with its frontmatter is written" '[ $RC = 0 ] && [ "$(wc -c < "$RESP" | tr -d " ")" -gt 261000 ]'
check "N: ...and the frontmatter is still the one new wrote" '[ -s "$SANDBOX/r1.before" ] && [ "$(fm_of "$RESP")" = "$(fm_of "$SANDBOX/r1.before")" ]'
# b485: the 261000-byte report above is kept below a repeat, so a 2000-byte fill over it passes the 256 KiB message cap
cp "$RESP" "$SANDBOX/big.before"
mcli "$MAIN" fill --id "$ID" --from reviewer --body "$(node -e 'process.stdout.write(JSON.stringify("b".repeat(2000)))')"
check "K (b485): a fill refused for size over stacked reports says they are kept and names msg new --parent, the response untouched" '[ $RC = 2 ] && printf %s "$OUT" | grep -qF "earlier reports are kept; file this one as a new request with \`ah msg new --parent $ID\`" && cmp -s "$RESP" "$SANDBOX/big.before"'
mint k2
mcli "$MAIN" fill --id "$ID" --from reviewer --body "$(node -e 'process.stdout.write(JSON.stringify("a".repeat(262145)))')"
check "K (b485): an over-cap first fill (nothing stacked) is refused without that hint" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "over 262144 bytes" && ! printf %s "$OUT" | grep -q "earlier reports are kept"'

# ---- A: authorisation. A caller may only fill as an address it owns; the refusal is not_your_session and nothing is written.
cp "$RESP" "$SANDBOX/a.before"
PROVE_AS=implementor mcli "$MAIN" fill --id "$ID" --from reviewer --body "$(jstr "$BODY")"
check "A: a member proven as implementor filling --from reviewer is refused with not_your_session, the response untouched" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "not_your_session: --from must be" && cmp -s "$RESP" "$SANDBOX/a.before"'
PROVE_AS=orchestrator mcli "$MAIN" fill --id "$ID" --from reviewer --body "$(jstr "$BODY")"
check "A: the Orchestrator shell filling --from reviewer is refused with not_your_session, the response untouched" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "not_your_session: --from must be" && cmp -s "$RESP" "$SANDBOX/a.before"'

# ---- no response yet: fill --id alone creates it (the request found by id), then fills it.
mcli "$MAIN" new --to reviewer --from orchestrator --slug r2 --title "Fix test titles"
ID2=$(printf '%s' "$OUT" | json_id)
RESP2="$SANDBOX/r2.resp.md"
mcli "$MAIN" fill --id "$ID2" --from reviewer --body "$(jstr "$BODY")"
snap "$ID2" response "$RESP2"
check "C: no response yet: fill --id with no --req creates it and fills it" '[ $RC = 0 ] && [ -f "$RESP2" ] && grep -qx "type: response" "$RESP2" && grep -qF -- "- [1] status: PASS" "$RESP2"'
mcli "$MAIN" show "$ID2" response
check "C: show <id> response prints the filled response, byte for byte" '[ $RC = 0 ] && [ "$OUT" = "$(cat "$RESP2")" ]'
mcli "$MAIN" show "$ID2" response --section 1
check "C: show --section 1 prints the frontmatter and [1] only" '[ $RC = 0 ] && printf %s "$OUT" | grep -q "^## \[1\]" && ! printf %s "$OUT" | grep -q "^## \[0\]" && printf %s "$OUT" | grep -qx "type: response"'
mcli "$MAIN" show 20990101-000000-zzzz response
check "C: show of an unknown id exits 1 with no response <id>" '[ $RC = 1 ] && printf %s "$OUT" | grep -q "no response 20990101-000000-zzzz"'
mcli "$MAIN" fill --id "$ID2" --from reviewer --body "$(jstr "$BODY")"
snap "$ID2" response "$RESP2"
check "R: ...whereas after new the same fill works (nearest non-match)" '[ $RC = 0 ] && grep -q "status: PASS" "$RESP2"'

# ---- W: the request is in the main checkout's msgs, --cwd is a worktree.
mint w1
fm_of "$RESP" > "$SANDBOX/w1.fm"
mcli "$WT" fill --id "$ID" --from reviewer --body "$(jstr "$BODY")"
check "W: with --cwd a worktree and the request in the main checkout, fill stores the response in the main checkout pool" '[ $RC = 0 ] && grep -q "status: PASS" "$RESP" && fm_of "$RESP" | cmp -s - "$SANDBOX/w1.fm"'

# ---- r2: a body that already starts with an empty line gets no second one.
mint b1
fill $'\n## [0] tldr\n- ok\n'
check "N (r2): a body that starts with an empty line is written as is: exactly one empty line after the closing fence" '[ $RC = 0 ] && [ -z "$(sed -n "$(( $(fm_of "$RESP" | wc -l) + 1 ))p" "$RESP")" ] && [ "$(sed -n "$(( $(fm_of "$RESP" | wc -l) + 2 ))p" "$RESP")" = "## [0] tldr" ]'

# ---- r2: the hints that told the Reviewer to fill with Edit now name fill.
check "H (r2): the built-in reviewer body carries the fill command" 'grep -q "<ah> msg fill --id <id> --from <your role> --body-file" "$PLUGIN/agents/reviewer.md"'

# ---- S/Q/M: fill by id: --section splice, --request amend, --req naming another id.
mint s1
fill "$BODY"
mcli "$MAIN" fill --id "$ID" --from reviewer --section 1 --body "$(jstr $'- changed\n')"
check "S: --section 1 replaces only that section's body" '[ $RC = 0 ] && grep -qx -- "- changed" "$RESP" && ! grep -qx -- "- none" "$RESP" && grep -qx "## \[1\] findings" "$RESP" && grep -qx -- "- \[1\] status: PASS" "$RESP"'
check "S (b485): a --section fill after a fill does not duplicate the body" '! grep -q "^## earlier report" "$RESP" && [ "$(grep -c "^## \[0\] tldr" "$RESP")" = 1 ]'
mcli "$MAIN" fill --id "$ID" --from reviewer --section 3 --body "$(jstr $'- added\n')"
check "S: --section 3 when absent is appended at the end, the rest kept" '[ $RC = 0 ] && [ "$(tail -n 2 "$RESP")" = "$(printf "## [3]\n- added")" ] && grep -qx -- "- changed" "$RESP"'
cp "$REQ" "$SANDBOX/req.before"
mcli "$MAIN" fill --id "$ID" --from reviewer --request --body "$(jstr $'## [0] tldr\n- hijack\n')"
check "Q: fill --request by someone other than the request's from is refused, the request untouched" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "only its author" && cmp -s "$REQ" "$SANDBOX/req.before"'
mcli "$MAIN" fill --id "$ID" --from orchestrator --request --body "$(jstr $'## [0] tldr\n- amended\n')"
check "Q: fill --request by the request's from rewrites the brief, frontmatter kept" '[ $RC = 0 ] && grep -qx -- "- amended" "$REQ" && grep -qx "type: request" "$REQ" && printf %s "$OUT" | grep -q "\"type\":\"request\""'
# a closed exchange's brief is not amended: the closed mark lives on its stored response, which only a planted fixture sets without a close flow
ID=20261030-000000-clsd; REQ="$SANDBOX/clsd.req.md"; RESP="$SANDBOX/clsd.resp.md"
mkdir -p "$MSGS"
CFM='slug: clsd\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: null\ncreated: 2026-10-30T00:00:00+00:00'
printf -- "---\nid: $ID\ntype: request\nto: reviewer\nfrom: orchestrator\n$CFM\n---\n\n## [0] tldr\n- brief\n" > "$MSGS/$ID--reviewer--clsd--request.md"
printf -- "---\nid: $ID\ntype: response\nto: orchestrator\nfrom: reviewer\n$CFM\nclosed: true\n---\n\n## [1] status\n- done\n" > "$MSGS/$ID--orchestrator--clsd--response.md"
ah_seed_msgs "$(dirname "$MSGS")"
snap "$ID" request "$REQ"
cp "$REQ" "$SANDBOX/req.before"
mcli "$MAIN" fill --id "$ID" --from orchestrator --request --body "$(jstr $'## [0] tldr\n- late\n')"
check "Q: fill --request on a closed exchange is refused, the request untouched" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "closed" && cmp -s "$REQ" "$SANDBOX/req.before"'

# ---- O/T: show --out guards, one id with two recipients in one pool.
mkdir -p "$SANDBOX/out"
mcli "$MAIN" show "$ID" --out "$SANDBOX/out"
check "O: --out naming a directory is refused" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "is a directory"'
mcli "$MAIN" show "$ID" --out "$SANDBOX/out/copy.md"
check "O: --out leaves no temp file beside the copy" '[ $RC = 0 ] && [ -z "$(ls -A "$SANDBOX/out" | grep "\.tmp$")" ]'
# msg new refuses a second request with the same id, so the twins (left by an older writer) are planted
mkdir -p "$MSGS"
for to in reviewer architect; do
  printf -- '---\nid: 20261030-000000-twin\ntype: request\nto: %s\nfrom: orchestrator\nslug: twin\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: null\ncreated: 2026-10-30T00:00:00+00:00\n---\n\n## [0] tldr\n- brief\n' "$to" > "$MSGS/20261030-000000-twin--$to--twin--request.md"
done
ah_seed_msgs "$(dirname "$MSGS")"
mcli "$MAIN" show 20261030-000000-twin
check "T: one id with two recipients in one pool lists both and exits non-zero" '[ $RC != 0 ] && printf %s "$OUT" | grep -q "to reviewer" && printf %s "$OUT" | grep -q "to architect"'
mcli "$MAIN" show 20261030-000000-twin --to architect
check "T: --to picks one of them" '[ $RC = 0 ] && printf %s "$OUT" | grep -qx "to: architect"'

# ---- the verb is listed in the usage text
mcli "$MAIN" --help
check "U: --help documents fill" 'printf %s "$OUT" | grep -q "ah msg fill --id"'
mcli "$MAIN" bogus
check "U: an unknown verb's usage line lists fill" '[ $RC = 2 ] && printf %s "$OUT" | grep -q "new|fill|show|list"'

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
