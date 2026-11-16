#!/usr/bin/env bash
# tests/e2e/smoke.sh R3 must outlast the relay fallback: the lease the mesh actually defaults to, plus the member's work.
. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
set -u
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
SMOKE="$ROOT/tests/e2e/smoke.sh"
CLAIMS="$ROOT/native/src/Ah.Mesh/Mail/MailClaims.cs"
FAIL=0

fits() { [ "$1" -gt $(($2 + $3)) ]; }
value() { sed -nE "s/^$1=([0-9]+)\$/\\1/p" "$SMOKE"; }

lease_ms=$(sed -nE 's/.*DefaultRelayLeaseMs = ([0-9_]+);.*/\1/p' "$CLAIMS" | tr -d _)
lease=$(value R3_RELAY_LEASE_S)
work=$(value R3_MEMBER_WORK_S)
margin=$(value R3_MARGIN_S)

check() { if eval "$2"; then echo "PASS: $1"; else echo "FAIL: $1"; FAIL=1; fi; }

check "the smoke's lease is the mesh's default" "[ -n '$lease_ms' ] && [ '$((lease_ms / 1000))' = '$lease' ]"
check "the member's work is the 60 s the fallback leaves it" "[ '$work' -ge 60 ]"
check "the report wait outlasts lease plus work" "fits $((lease + work + margin)) $lease $work"
check "the old 200 s wait does not (negative)" "! fits 200 $lease $work"
check "R3 polls the computed wait" "grep -qF 'poll \"\$R3_REPORT_WAIT_S\" ' '$SMOKE'"

exit "$FAIL"
