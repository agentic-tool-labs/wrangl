#!/bin/bash
# Export agent-hierarchy-v2 at <sha> for the public release, minus the paths in release-exclude.txt (read at
# that same sha). Extracts into <out-dir>/agent-hierarchy-v2. Reads the repo only; writes only under <out-dir>.
# Usage: bash scripts/release-export.sh <sha> <out-dir>
set -euo pipefail
[ $# -eq 2 ] || { echo "usage: release-export.sh <sha> <out-dir>" >&2; exit 2; }
SHA=$1; OUT=$2
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
TOP="$(git -C "$PLUGIN" rev-parse --show-toplevel)"
PREFIX="$(git -C "$PLUGIN" rev-parse --show-prefix)"; PREFIX="${PREFIX%/}"
LIST="$(git -C "$TOP" show "$SHA:$PREFIX/release-exclude.txt")"
mkdir -p "$OUT"

excludes=(); keeps=()
while IFS= read -r line; do
  line="${line%%#*}"; line="${line//[[:space:]]/}"
  [ -z "$line" ] && continue
  case "$line" in
    !*) keeps+=("$PREFIX/${line#!}") ;;
    *) excludes+=(":(exclude)$PREFIX/$line") ;;
  esac
done <<< "$LIST"

git -C "$TOP" archive "$SHA" -- "$PREFIX" "${excludes[@]}" | tar -x -C "$OUT"
[ ${#keeps[@]} -eq 0 ] || git -C "$TOP" archive "$SHA" -- "${keeps[@]}" | tar -x -C "$OUT"
