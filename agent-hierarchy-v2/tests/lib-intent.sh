# Helpers for tests that dispatch to a review-class role, sourced by the suites.

# fill_intent <request path> [goal] [acceptance]: puts text on the `[1] goal` and `[5] acceptance` tldr lines of a fresh stored skeleton.
fill_intent() {
  local base=${1##*/}
  sqlite3 "$AH_HOME/state/ah.db" "UPDATE msg_bodies SET bytes = CAST(replace(replace(CAST(bytes AS TEXT), '- [1] goal: ', '- [1] goal: ${2-deliver the stated change}'), '- [5] acceptance: ', '- [5] acceptance: ${3-the stated checks pass}') AS BLOB) WHERE msg_pk IN (SELECT pk FROM msg_messages WHERE id = '${base%%--*}' AND type = 'request')"
}
