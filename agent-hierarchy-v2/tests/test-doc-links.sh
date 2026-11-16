#!/bin/bash
# agent-hierarchy doc-lint: every relative markdown link in README.md,
# docs/*.md (excluding docs/specs/ and docs/retired/, which are historical
# records, not living docs) and the skills' SKILL.md and reference/*.md must
# resolve to a real file, and a #anchor into a markdown file to one of its headings. Broken relative
# links are the cheapest, most common defect in a docs commit.
# Usage: bash tests/test-doc-links.sh   (exits 0 iff all links resolve)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
unset WRANGL_TEAM_FILE  # every session roster.mjs launches carries one; a test must not inherit it
unset CLAUDE_PID  # every Claude session exports one; a test must not inherit it
PASS=0; FAIL=0

check_file() {
  local doc="$1"
  local dir
  dir="$(dirname "$doc")"
  # [text](path) links, excluding http(s)/mailto and pure #anchors.
  while IFS= read -r link; do
    [ -z "$link" ] && continue
    local target="${link%%#*}"   # drop a trailing #anchor
    [ -z "$target" ] && continue
    local resolved
    resolved="$(cd "$dir" 2>/dev/null && node -e "console.log(require('path').resolve(process.argv[1]))" "$target" 2>/dev/null)"
    local frag=""
    case "$link" in *'#'*) frag="${link#*#}" ;; esac
    if [ -n "$resolved" ] && [ -e "$resolved" ]; then
      if [ -n "$frag" ] && [[ "$resolved" == *.md ]] && ! node -e '
        const fs = require("fs");
        const slug = (h) => h.trim().toLowerCase().replace(/`/g, "").replace(/[^\p{L}\p{N}\s_-]/gu, "").replace(/\s/g, "-");
        const text = fs.readFileSync(process.argv[1], "utf8");
        const heads = text.split("\n").filter((l) => /^#{1,6}\s/.test(l)).map((l) => slug(l.replace(/^#+\s+/, "")));
        // an explicit anchor (<a id="x"> or <a name="x">) is a target too: it keeps an old link working after a heading is reworded
        const marks = [...text.matchAll(/<a\s+(?:id|name)="([^"]+)"/g)].map((m) => m[1]);
        process.exit(heads.includes(process.argv[2]) || marks.includes(process.argv[2]) ? 0 : 1);
      ' "$resolved" "$frag"; then
        FAIL=$((FAIL+1))
        echo "FAIL: $doc -> $link (no heading #$frag in $resolved)"
      else
        PASS=$((PASS+1))
      fi
    else
      FAIL=$((FAIL+1))
      echo "FAIL: $doc -> $link (resolved: $resolved)"
    fi
  done < <(grep -oE '\]\([^)]+\)' "$doc" | sed -E 's/^\]\((.*)\)$/\1/' | grep -vE '^(https?:|mailto:)')
}

FILES=("$PLUGIN/README.md")
for f in "$PLUGIN"/docs/*.md; do
  [ -f "$f" ] || continue
  FILES+=("$f")
done

for f in "$PLUGIN"/skills/*/SKILL.md "$PLUGIN"/skills/*/reference/*.md; do
  [ -f "$f" ] && FILES+=("$f")
done

for f in "${FILES[@]}"; do
  check_file "$f"
done

echo "----"
echo "SUMMARY: $PASS passed, $FAIL failed"
[ $FAIL -eq 0 ]
