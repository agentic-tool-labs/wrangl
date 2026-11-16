#!/bin/bash
# agent-hierarchy — nothing in the repo deletes /tmp/ahprobe.* by glob. Every probe and test sandbox lives under a shared
# /tmp/ahprobe.<alnum> directory that concurrent runs of other members also use; a cleanup that removes the glob deletes the
# sandbox of a run that is still going. A cleanup removes the one path its own mktemp returned.
# Usage: bash tests/test-no-ahprobe-glob-rm.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
TOP="$(git -C "$PLUGIN" rev-parse --show-toplevel)"
PASS=0; FAIL=0
check() { local n=$1; shift; if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $n"; else FAIL=$((FAIL+1)); echo "FAIL: $n"; fi; }

# an rm whose argument is a path through ahprobe. that carries a wildcard, or a find over ahprobe.* that deletes
GLOB_RM='(^|[^[:alnum:]_-])rm[[:space:]]+[^;|&)]*ahprobe\.[^[:space:];|&)]*[*?[]|find[[:space:]][^;|&]*ahprobe[^;|&]*(-delete|-exec[[:space:]]+rm)'
hits() { grep -E "$GLOB_RM"; }

check "a recursive rm of the ahprobe. glob is a hit" 'printf "%s\n" "rm -rf /tmp/ahprobe.*" | hits >/dev/null'
check "an rm with a wildcard past the six-character suffix is a hit" 'printf "%s\n" "  rm -rf \"\$X\" /private/tmp/ahprobe.??????" | hits >/dev/null'
check "a find over ahprobe. that deletes is a hit" 'printf "%s\n" "find /tmp -maxdepth 1 -name \"ahprobe.*\" -exec rm -rf {} +" | hits >/dev/null'
check "an rm of the one recorded variable is not a hit" '! printf "%s\n" "case \"\$T\" in /tmp/ahprobe.??????) rm -rf \"\$T\" ;; esac" | hits >/dev/null'
check "an rm of one named ahprobe. directory is not a hit" '! printf "%s\n" "rm -rf /tmp/ahprobe.ABCDEF" | hits >/dev/null'
check "a find that only lists ahprobe. directories is not a hit" '! printf "%s\n" "find /tmp/ -maxdepth 1 -name ahprobe.?????? -type d -newer m" | hits >/dev/null'

# The exempt files: MemberPermissionsGenerator.cs holds member allow-rule strings ("Bash(rm -rf /tmp/ahprobe.*)"), which the user
# asked for ("Add the allow rule on the agents"); it runs no delete. The three roster goldens are that generator's recorded
# output. Every other tracked file stays under the guard.
EXEMPT=(
  'agent-hierarchy-v2/native/src/Ah.Hooks/Surface/MemberPermissionsGenerator.cs'
  'agent-hierarchy-v2/tests/contracts/cli/cases/roster/create/plan.json'
  'agent-hierarchy-v2/tests/contracts/cli/cases/roster/spawn-one/dry-run.json'
  'agent-hierarchy-v2/tests/contracts/cli/cases/roster/spawn-ad-hoc/dry-run.json'
)
FOUND=$(git -C "$TOP" grep -nIE "$GLOB_RM" -- . ':!agent-hierarchy-v2/tests/test-no-ahprobe-glob-rm.sh' "${EXEMPT[@]/#/:!}" 2>/dev/null | cut -c1-200)
check "each exemption is needed: every exempt file's rule text is a hit, so its exemption is what clears it" \
  '( for e in "${EXEMPT[@]}"; do [ -n "$(git -C "$TOP" grep -nIE "$GLOB_RM" -- "$e" 2>/dev/null)" ] || exit 1; done )'
check "the exemption names those files alone: the same rule text in another path is still a hit" \
  'printf "%s\n" "\"Bash(rm -rf /tmp/ahprobe.*)\"" | hits >/dev/null'
check "no tracked file deletes /tmp/ahprobe.* by glob" '[ -z "$FOUND" ]'
[ -z "$FOUND" ] || printf '%s\n' "$FOUND"

echo "passed $PASS, failed $FAIL"
[ "$FAIL" -eq 0 ]
