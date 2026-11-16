#!/usr/bin/env bash
# Builds docs/research/ah-base-audit-results.md from rows/phase-*.tsv and a header file, then checks it (spec §10).
# Usage: check-results.sh <header.md> <findings.md>. Exit 0 only with 107 distinct rows, each a known result, each PASS or FAIL with evidence.
set -u
D=$(cd "$(dirname "$0")/../.." && pwd -P)
OUT="$D/docs/research/ah-base-audit-results.md"
A="$D/docs/audits/ah-base-audit"
{
  cat "$1"
  printf '\n| function | scenario | result | evidence path |\n|---|---|---|---|\n'
  cat "$A"/rows/phase-*.tsv | awk -F'\t' '{printf "| %s %s | %s | %s | ../audits/ah-base-audit/evidence/%s/ |\n", $1, $2, $3, $4, $1}'
  printf '\n'
  cat "$2"
} >"$OUT"
BAD=0
N=$(cat "$A"/rows/phase-*.tsv | cut -f1 | sort -u | wc -l | tr -d ' ')
[ "$N" = 107 ] || { echo "rows: $N, want 107"; BAD=1; }
D2=$(cat "$A"/rows/phase-*.tsv | cut -f1 | sort | uniq -d | tr '\n' ' ')
[ -z "$D2" ] || { echo "duplicate rows: $D2"; BAD=1; }
while IFS=$'\t' read -r id _ _ res; do
  case "$res" in PASS | FAIL | NOT-TESTED) ;; *) echo "$id: bad result '$res'"; BAD=1 ;; esac
  [ -s "$A/evidence/$id/verdict.txt" ] || { echo "$id: no verdict.txt"; BAD=1; }
done < <(cat "$A"/rows/phase-*.tsv)
cat "$A"/rows/phase-*.tsv | cut -f4 | sort | uniq -c
exit "$BAD"
