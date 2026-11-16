#!/bin/bash
# agent-hierarchy — the sh test-mode predicate (bin/ah-testmode.sh) against tests/contracts/home-predicate.json, the table the
# C# predicate (IAhEnvironment.TestMode) runs too. Usage: bash tests/test-home-predicate.sh   (exits 0 iff every row passes)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
here="$(cd "$(dirname "$0")" && pwd)"
node "$here/contract-tools/home-predicate-sh.mjs" || rc=1
node "$here/contract-tools/trusted-homes-sh.mjs" || rc=1
exit "${rc:-0}"
