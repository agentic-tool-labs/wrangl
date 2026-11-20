#!/bin/bash
# agent-hierarchy — the size of the machine-wide test token bucket, in one place for tests/with-slot.sh and every runner that sizes
# itself to its grant.
#
#   ah_bucket_capacity   the default pool's capacity B: 0 when the limiter is off (AH_TEST_SLOTS=0), else AH_TEST_TOKENS, else the
#                        legacy AH_TEST_SLOTS, else max(2, ncpu - 2)
#   ah_bucket_heavy      the tokens a whole-solution or whole-suite run asks for: min(12, ceil(3B / 4)), at least 1. With
#                        AH_TEST_HEAVY_SPLIT=1, one of two heavy runs meant to overlap: min(8, ceil(B / 2)), since two of the larger
#                        grants do not fit in one bucket and would run one after the other. A runner reads it, then unsets it, so the
#                        suites it starts do not inherit it.

ah_ncpu() { sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4; }

ah_bucket_capacity() {
  local n
  if [ "${AH_TEST_SLOTS:-}" = 0 ]; then echo 0; return; fi
  if [ -n "${AH_TEST_TOKENS:-}" ]; then echo "$AH_TEST_TOKENS"; return; fi
  if [ -n "${AH_TEST_SLOTS:-}" ]; then echo "$AH_TEST_SLOTS"; return; fi
  n=$(( $(ah_ncpu) - 2 )); [ "$n" -lt 2 ] && n=2
  echo "$n"
}

ah_bucket_heavy() { # [B]: the bucket to size for (default ah_bucket_capacity)
  local b k
  b=${1:-$(ah_bucket_capacity)}
  case "$b" in ''|*[!0-9]*) b=2 ;; esac
  if [ "${AH_TEST_HEAVY_SPLIT:-}" = 1 ]; then
    k=$(( (b + 1) / 2 )); [ "$k" -gt 8 ] && k=8
  else
    k=$(( (3 * b + 3) / 4 )); [ "$k" -gt 12 ] && k=12
  fi
  [ "$k" -lt 1 ] && k=1
  echo "$k"
}
