#!/bin/bash
# agent-hierarchy — the .contract sidecar scripts/install.sh and scripts/publish-native.sh both write, through one function
# (ah_write_contract_sidecar in scripts/lib-contract.sh):
#   it writes <binary>.contract as {"version":V,"contract":C} plus a newline, replaces an older sidecar whole, leaves no temporary
#   file, and fails when the directory cannot be written; both scripts call it and neither writes a sidecar itself.
# Every run is in a sandbox under /private/tmp/ahprobe.*.
# Usage: bash tests/test-contract-sidecar.sh   (exits 0 iff all cases pass)
. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SB="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
case "$SB" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$SB'; aborting"; exit 1;; esac
hermetic_on_exit 'rm -rf "$SB"'
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n"; fi; }

. "$PLUGIN/scripts/lib-contract.sh"

mkdir -p "$SB/out"
ah_write_contract_sidecar "$SB/out/ahd" "2.5.0-pre.1" 7
check "1 the sidecar holds the version and the contract" '[ "$(cat "$SB/out/ahd.contract")" = "{\"version\":\"2.5.0-pre.1\",\"contract\":7}" ]'
check "2 the sidecar ends in one newline" '[ "$(wc -l < "$SB/out/ahd.contract" | tr -d " ")" = 1 ]'
check "3 no temporary file is left" '[ -z "$(ls "$SB/out" | grep "\.tmp")" ]'

ah_write_contract_sidecar "$SB/out/ahd" "2.6.0" 8
check "4 a newer sidecar replaces the older whole" '[ "$(cat "$SB/out/ahd.contract")" = "{\"version\":\"2.6.0\",\"contract\":8}" ]'

ah_write_contract_sidecar "$SB/out/ahd-tui" "2.6.0" 8
check "5 each binary gets its own sidecar" '[ -f "$SB/out/ahd-tui.contract" ] && [ -f "$SB/out/ahd.contract" ]'

check "6 a directory that does not exist is a failure" '! ah_write_contract_sidecar "$SB/nowhere/ahd" "2.6.0" 8 2>/dev/null'
check "7 and no sidecar appears for it" '[ ! -e "$SB/nowhere" ]'

check "6b an empty version is refused and nothing is written" '! ah_write_contract_sidecar "$SB/out/empty-v" "" 8 2>/dev/null && [ ! -e "$SB/out/empty-v.contract" ]'
check "6c a contract that is not a number is refused and nothing is written" '! ah_write_contract_sidecar "$SB/out/bad-c" "2.6.0" "x1" 2>/dev/null && [ ! -e "$SB/out/bad-c.contract" ]'
check "6d an empty contract is refused and nothing is written" '! ah_write_contract_sidecar "$SB/out/no-c" "2.6.0" "" 2>/dev/null && [ ! -e "$SB/out/no-c.contract" ]'
check "6e a whole-number contract and a version still write" 'ah_write_contract_sidecar "$SB/out/ok" "2.6.0-pre.1" 12 && [ "$(cat "$SB/out/ok.contract")" = "{\"version\":\"2.6.0-pre.1\",\"contract\":12}" ]'
check "6f publish-native.sh writes sidecars only when both version and contract are given" 'grep -F "[ -n \"\$version\" ] && [ -n \"\$contract\" ]" "$PLUGIN/scripts/publish-native.sh" | grep -q .'

check "8 install.sh writes its sidecars through the function" '[ "$(grep -c "ah_write_contract_sidecar " "$PLUGIN/scripts/install.sh")" = 2 ]'
check "9 publish-native.sh writes its sidecars through the function" 'grep -q "ah_write_contract_sidecar " "$PLUGIN/scripts/publish-native.sh"'
check "10 neither script writes a sidecar of its own" '! grep -F "contract.\$\$.tmp" "$PLUGIN/scripts/install.sh" "$PLUGIN/scripts/publish-native.sh" | grep -q .'

echo "contract-sidecar: $PASS passed, $FAIL failed"
[ "$FAIL" = 0 ]
