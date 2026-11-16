#!/bin/bash
# agent-hierarchy — the shared body of the contract shard suites. The recorded contract set runs through one runner
# (tests/contract-tools/run.mjs); a shard is one slice of it, picked by `--shard K/N`, and each slice is a suite of its own
# (tests/test-contracts-s<K>.sh) so the slices run side by side. N is set here and nowhere else: tests/test-contracts.sh checks that
# there is one suite file per shard.
#
#   . "$(dirname "$0")/lib-hermetic.sh"
#   . "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
#   . "$(cd "$(dirname "$0")" && pwd)/lib-contracts.sh"
#   contracts_shard_main <K>
#
# This is the gate on the native binary: a skip that is not a named divergence is a failure (--require-native), whichever shard holds
# CONTRACT_REQUIRED must have passed there, and a shard that leaves a process running under its sandbox is red. AH_CONTRACTS_ONLY
# narrows the shard to one --only glob, for tests/test-contracts.sh's own checks of this file.

CONTRACT_SHARDS=4
CONTRACT_REQUIRED=lifecycle/13-trust-verbs-pty

# What a shard left running is found by its own sandbox, never by the binary: the binary is shared by every run that uses it, so a sweep
# on its path would also stop other tests' processes. Kills them and prints how many there were.
contracts_leaked() { # <shard sandbox>
  local sb=$1 found left p
  case "$sb" in /private/tmp/ahprobe.??????) ;; *) echo 0; return;; esac
  found="$(pgrep -f -- "$sb/" 2>/dev/null)"
  [ -n "$found" ] || { echo 0; return; }
  # shellcheck disable=SC2086
  for p in $found; do ah_signal_ours TERM "$p" "$sb/"; done
  sleep 1
  left="$(pgrep -f -- "$sb/" 2>/dev/null)"
  # shellcheck disable=SC2086
  for p in $left; do ah_signal_ours KILL "$p" "$sb/"; done
  echo "$found" | wc -w | tr -d ' '
}

# Copies a shard's failure diffs out of its sandbox, which goes when the shard ends, to the directory the runner names in AH_TEST_FAIL_DIR.
# Bounded so a systemic failure cannot fill the result store: the first 20 diffs, 64 KiB each.
contracts_keep_diffs() { # <sandbox fail dir>
  local from=$1 to=${AH_TEST_FAIL_DIR:-} f rel
  [ -n "$to" ] && [ -d "$from" ] || return 0
  while IFS= read -r f; do
    rel=${f#"$from"/}
    mkdir -p "$to/$(dirname "$rel")" && head -c 65536 "$f" > "$to/$rel"
  done < <(find "$from" -type f -name '*.diff' | LC_ALL=C sort | head -20)
}

contracts_shard_main() {
  local k=$1 PLUGIN TOOLS SUMMARY DIVERGED PASSED TOTAL ST LEAKED RAW ONLY=()
  PLUGIN="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
  TOOLS="$PLUGIN/tests/contract-tools"
  [ -z "${AH_CONTRACTS_ONLY:-}" ] || ONLY=(--only "$AH_CONTRACTS_ONLY")
  PASS=0; FAIL=0
  check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n (OUT=${OUT:0:600})"; fi; }
  ST="$(mktemp -d /private/tmp/ahprobe.XXXXXX)"
  case "$ST" in /private/tmp/ahprobe.??????) ;; *) echo "mktemp returned '$ST'; aborting"; return 1;; esac
  RAW=$(T="$ST" node "$TOOLS/run.mjs" --impl native --require-native --shard "$k/$CONTRACT_SHARDS" ${ONLY[@]+"${ONLY[@]}"} 2>&1); RC=$?
  LEAKED="$(contracts_leaked "$ST")"
  contracts_keep_diffs "$ST/fail"
  case "$ST" in /private/tmp/ahprobe.??????) rm -rf "$ST";; esac
  OUT=$RAW
  SUMMARY=$(echo "$OUT" | grep -E '^contracts: [0-9]+/[0-9]+ \(native\)' | head -1)
  DIVERGED=$(echo "$OUT" | sed -n 's/^contracts: [0-9]* pass + \([0-9]*\) diverged of [0-9]*$/\1/p' | head -1)
  OUT=$(printf '%s\n' "$OUT" | grep -E '^(FAIL|contracts:)' | head -8)
  # a ruled divergence from the Node oracle is counted apart from a pass and is not a failure; a pass and a divergence make the total
  PASSED=$(echo "$SUMMARY" | sed -n 's/^contracts: \([0-9]*\)\/.*/\1/p')
  TOTAL=$(echo "$SUMMARY" | sed -n 's/^contracts: [0-9]*\/\([0-9]*\) .*/\1/p')
  check "C0-4: shard $k of $CONTRACT_SHARDS of run.mjs --impl native passes every case that is not skipped or a named divergence, and a named divergence is not a failure" \
    '[ $RC = 0 ] && [ -n "$TOTAL" ] && [ "$TOTAL" -gt 0 ] && [ $((PASSED + ${DIVERGED:-0})) = "$TOTAL" ] && ! echo "$OUT" | grep -q "^FAIL"'
  check "...and there is a real slice behind it: at least 50 cases ran" '[ "${PASSED:-0}" -ge 50 ]'
  check "...and it left nothing running under its sandbox (left $LEAKED, killed)" '[ "$LEAKED" = 0 ]'
  if node "$TOOLS/run.mjs" --impl native --list --shard "$k/$CONTRACT_SHARDS" ${ONLY[@]+"${ONLY[@]}"} 2>/dev/null | grep -qx "$CONTRACT_REQUIRED"; then
    check "...and the case the gate requires, $CONTRACT_REQUIRED, passed in this shard" 'printf "%s\n" "$RAW" | grep -q "^ok $CONTRACT_REQUIRED\b"'
  fi
  echo
  echo "RESULT: $PASS passed, $FAIL failed"
  [ "$FAIL" -eq 0 ]
}
