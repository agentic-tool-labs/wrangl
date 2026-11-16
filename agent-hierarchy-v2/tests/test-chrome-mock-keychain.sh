#!/bin/bash
# agent-hierarchy — every tracked line that launches headless Chrome passes --use-mock-keychain. Without it, Chrome on a fresh
# --user-data-dir tries to create its "Chrome Safe Storage" keychain item and macOS pops "Keychain Not Found" on the user's desktop.
# Usage: bash tests/test-chrome-mock-keychain.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
TOP="$(git -C "$PLUGIN" rev-parse --show-toplevel)"
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n"; fi; }

# a headless Chrome launch, whatever the binary is called: --headless=<any value>, or a bare --headless on a line that names
# Chrome/Chromium or passes --user-data-dir. A bare --headless alone is not one: godot and `spawn-one --headless` use it.
LAUNCH='--headless=|chrom(e|ium)[^;|&]*--headless|--headless[^;|&]*chrom(e|ium)|--user-data-dir[^;|&]*--headless|--headless[^;|&]*--user-data-dir'
hits() { grep -iE -- "$LAUNCH"; }

check "a --headless=new flag list is a launch" 'printf "%s\n" "  \`--headless=new --user-data-dir=<dir> --no-first-run\`" | hits >/dev/null'
check "Google Chrome with a bare --headless is a launch" 'printf "%s\n" "\"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome\" --headless --dump-dom x" | hits >/dev/null'
check "chromium --headless is a launch" 'printf "%s\n" "chromium --headless --screenshot" | hits >/dev/null'
check "chrome-headless-shell run with --headless is a launch" 'printf "%s\n" "\"\$AH_CHROME\" --headless --dump-dom # chrome-headless-shell" | hits >/dev/null'
check "a chrome-headless-shell path alone is not a launch" '! printf "%s\n" "newest(\"*/*/chrome-headless-shell\")" | hits >/dev/null'
check "a bare --headless with --user-data-dir under any binary name is a launch" 'printf "%s\n" "spawn(BIN, [\"--headless\", \"--user-data-dir=/tmp/p\"])" | hits >/dev/null'
check "--headless=<any value> is a launch" 'printf "%s\n" "spawn(BIN, [\"--headless=shell\"])" | hits >/dev/null'
check "godot --headless is not a launch" '! printf "%s\n" "godot --headless -s check.gd" | hits >/dev/null'
check "spawn-one --headless is not a launch" '! printf "%s\n" "ah roster spawn-one implementor --headless --prompt x" | hits >/dev/null'
check "prose about headless Chrome is not a launch" '! printf "%s\n" "Measures the page in a headless Chrome against the mock" | hits >/dev/null'

# Per line, not per file: a comment that names the flag must not clear a launch that drops it.
LAUNCHES=$(git -C "$TOP" grep -nIiE -- "$LAUNCH" -- . ':!agent-hierarchy-v2/tests/test-chrome-mock-keychain.sh' 2>/dev/null)
MISSING=$(printf '%s\n' "$LAUNCHES" | grep -vF -- --use-mock-keychain | grep . | cut -c1-200)
check "the guard sees the shared perf launcher" 'printf "%s\n" "$LAUNCHES" | grep -q "^agent-hierarchy-v2/tests/perf/chrome.mjs:"'
check "every line that launches headless Chrome passes --use-mock-keychain" '[ -z "$MISSING" ]'
[ -z "$MISSING" ] || printf 'missing --use-mock-keychain: %s\n' "$MISSING"

echo "passed $PASS, failed $FAIL"
[ "$FAIL" -eq 0 ]
