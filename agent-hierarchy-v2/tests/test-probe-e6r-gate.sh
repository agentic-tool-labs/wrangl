#!/bin/bash
# agent-hierarchy — the E6r-1 safety gate (tests/probe-e6r-gate.mjs), without codex: synthetic reports for each clause (sandbox,
# network, approval) must fail it, one mutant per clause must be killed by them, the gate cannot pass an empty or partial report,
# and a gate module with a clause removed makes the self-check fail.
# Usage: bash tests/test-probe-e6r-gate.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SB="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-e6r-gate-test.XXXXXX")"
[ -n "$SB" ] && [ -d "$SB" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:300})"; fi; }
# <js over the self-check result r>: its value
sc() { node -e 'const r=JSON.parse(require("fs").readFileSync(0,"utf8")); const v=(new Function("r","return ("+process.argv[1]+")"))(r); process.stdout.write(typeof v==="string"?v:JSON.stringify(v))' "$1"; }

OUT=$(node "$PLUGIN/tests/probe-e6r-gate.mjs"); RC=$?
check "the self-check passes on the committed gate" '[ $RC = 0 ] && [ "$(echo "$OUT" | sc r.ok)" = true ]'
check "only the good report passes the gate" '[ "$(echo "$OUT" | sc "Object.entries(r.gate).filter(([k,v])=>v.pass).map(([k])=>k).join()")" = good ]'
for input in network_on network_missing workspace_write danger_full_access approval_on_request approval_missing no_sandbox empty; do
  check "report $input fails the gate" '[ "$(echo "$OUT" | sc "r.gate.$input.pass")" = false ]'
done
check "the sandbox clause is killed by its own mutant (workspace_write)" '[ "$(echo "$OUT" | sc "r.mutants[\"drop sandbox clause\"].killed_by.join()")" = workspace_write ]'
check "the network clause has its own mutant, killed by network_on" 'echo "$OUT" | sc "r.mutants[\"drop network clause\"].killed_by.join()" | grep -q network_on'
check "the weakened network check (not-true) is killed by network_missing" '[ "$(echo "$OUT" | sc "r.mutants[\"network clause: not-true instead of exactly-false\"].killed_by.join()")" = network_missing ]'
check "the approval clause is killed by its own mutant" 'echo "$OUT" | sc "r.mutants[\"drop approval clause\"].killed_by.join()" | grep -q approval_on_request'
check "a vacuous gate (no clauses) is killed" '[ "$(echo "$OUT" | sc "r.mutants[\"drop all clauses (vacuous gate)\"].killed")" = true ]'

# the self-check can fail: a copy of the module with the network clause removed is reported as broken
cp "$PLUGIN/tests/probe-e6r-gate.mjs" "$SB/gate.mjs"
python3 -I - "$SB/gate.mjs" <<'PY'
import sys
p = sys.argv[1]
s = open(p).read()
a = '  network: (f) => Boolean(f.sandbox && f.sandbox.networkAccess === false),\n'
assert a in s
open(p, "w").write(s.replace(a, '', 1))
PY
OUT=$(node "$SB/gate.mjs"); RC=$?
check "a gate without its network clause fails the self-check" '[ $RC = 1 ] && [ "$(echo "$OUT" | sc r.ok)" = false ] && [ "$(echo "$OUT" | sc r.gate.network_on.pass)" = true ]'

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
