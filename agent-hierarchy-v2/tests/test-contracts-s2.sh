#!/bin/bash
# agent-hierarchy — the recorded contracts, shard 2: every 4th case of the set (from place 2), through the one contract runner. The
# body is tests/lib-contracts.sh's; tests/test-contracts.sh holds the checks on the whole set.
# Usage: bash tests/test-contracts-s2.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-contracts.sh"
contracts_shard_main 2
