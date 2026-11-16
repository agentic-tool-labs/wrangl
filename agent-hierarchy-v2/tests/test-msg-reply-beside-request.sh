#!/bin/bash
# agent-hierarchy — spec 0037, in the store: a response is stored in its request's pool whichever worktree the responder runs from, a
# response to a request the responder cannot find is a loud failure that stores nothing, and a response that contradicts its request is refused.
# HOME-redirected; real state untouched.
# Usage: bash tests/test-msg-reply-beside-request.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-reply-beside-test.XXXXXX")"
hermetic_on_exit 'rm -rf "$SANDBOX"'
SANDBOX="$(cd "$SANDBOX" && pwd -P)"
FAKEHOME="$SANDBOX/home"
mkdir -p "$FAKEHOME/.claude/ah2"
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (RC=$RC OUT=${OUT:0:500})"; fi
}

# Fixture: main checkout with a registered worktree. Requests are always minted in MAIN's pool; responders run from elsewhere.
MAIN="$SANDBOX/main"; WT="$SANDBOX/wt"
mkdir -p "$MAIN/.claude/ah2"
(cd "$MAIN" && git init -q && git config user.email t@t.com && git config user.name t && git commit -q --allow-empty -m init)
git -C "$MAIN" worktree add -q "$WT" -b wt-branch >/dev/null
mkdir -p "$WT/.claude/ah2"
MAIN_MSGS="$MAIN/.claude/ah2/msgs"
# this shell is the Orchestrator that mints requests; a call made --from the member runs in a subshell proven as that member
HD="$MAIN/.claude/ah2"
ah_test_team "$HD" rb implementor
ah_test_prove_self orchestrator rb "$HD/teams/rb.json" >/dev/null

mcli() { # <cwd> <msg.mjs argv...>
  local at=$1; shift
  OUT=$(HOME="$FAKEHOME" ah_test_run_as_from rb "$HD/teams/rb.json" "$PLUGIN/bin/ah" msg "$@" --cwd "$at" 2>&1); RC=$?
}
json_field() { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.stdout.write(String(JSON.parse(s.split("\n").find(x=>x.startsWith("{")))[process.argv[1]])))' "$1"; }
mint_request() { # <slug> -> sets ID
  mcli "$MAIN" new --to implementor --from orchestrator --to-name main-implementor --from-name main-orchestrator --slug "$1" --title "Fix test titles"
  ID=$(printf '%s' "$OUT" | json_field id)
}
stored_response() { ah_msg_stored_path "$1" response; }

# ---- T1: responder in the WORKTREE: the response is stored in MAIN's pool. Fails against an implementation that resolves the pool from cwd.
mint_request t1
mcli "$WT" new --type response --id "$ID" --to orchestrator --from implementor
check "T1: worktree responder exits 0" '[ "$RC" -eq 0 ]'
check "T1: the response is stored under MAIN's pool" '[ "$(stored_response "$ID")" = "$MAIN_MSGS/$ID--orchestrator--t1--response.md" ]'

# ---- T3: request absent from the resolved pool: exit non-zero and nothing stored.
ID=20000101-000000-none
mcli "$WT" new --type response --id "$ID" --to orchestrator --from implementor
check "T3: request absent exits non-zero" '[ "$RC" -ne 0 ]'
check "T3: nothing stored" '[ -z "$(stored_response "$ID")" ]'

# ---- T4: the common local flow.
mint_request t4
mcli "$MAIN" new --type response --id "$ID" --to orchestrator --from implementor
check "T4: local flow exits 0 and the response is stored" '[ "$RC" -eq 0 ] && [ "$(stored_response "$ID")" = "$MAIN_MSGS/$ID--orchestrator--t4--response.md" ]'

# ---- T7: a response that contradicts its request. Both values quoted, nothing stored.
mint_request t7
mcli "$WT" new --type response --id "$ID" --to architect --from implementor
check "T7b: --to ≠ request.from exits non-zero" '[ "$RC" -ne 0 ]'
check "T7b: both values quoted" 'echo "$OUT" | grep -qF "\"architect\"" && echo "$OUT" | grep -qF "\"orchestrator\""'
mcli "$WT" new --type response --id "$ID" --to orchestrator --from implementor --to-name wrong-name
check "T7c: --to-name ≠ request.from_name exits non-zero, both quoted" '[ "$RC" -ne 0 ] && echo "$OUT" | grep -qF "\"wrong-name\"" && echo "$OUT" | grep -qF "\"main-orchestrator\""'
check "T7: nothing stored by any mismatch" '[ -z "$(stored_response "$ID")" ]'

# ---- Duplicate: a second write for the same id returns the response already stored.
mint_request dup
mcli "$WT" new --type response --id "$ID" --to orchestrator --from implementor
FIRST_PATH=$(printf '%s' "$OUT" | json_field path)
mcli "$WT" new --type response --id "$ID" --to orchestrator --from implementor
check "DUP: a second response for the same id exits 0, names the same message and marks it existing" '[ "$RC" -eq 0 ] && [ "$(printf %s "$OUT" | json_field path)" = "$FIRST_PATH" ] && echo "$OUT" | grep -q "\"existing\":true"'

# ---- Spec 0090: fill from a worktree against a main-checkout request.
mint_request fill
mcli "$WT" new --type response --id "$ID" --to orchestrator --from implementor
mcli "$WT" fill --id "$ID" --from implementor --body '"## [0] tldr\n- [1] status: done\n"'
check "FILL: a worktree --cwd with a main-checkout request exits 0 and fills the stored response" '[ "$RC" -eq 0 ] && HOME="$FAKEHOME" "$PLUGIN/bin/ah" msg show "$ID" response --cwd "$MAIN" | grep -q "status: done"'

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
