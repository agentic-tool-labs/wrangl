#!/bin/bash
# agent-hierarchy — puts a state backup (scripts/state-backup.sh) back where it came from.
#
# Usage: scripts/state-restore.sh --from DIR [--apply]
#   --from DIR   a finished backup (it holds BACKUP.txt and payload/)
#   --apply      does it; without it the plan is printed and nothing changes
#
# Every directory or file the backup recorded is first moved aside, next to where it was, to
# <name>.native.<UTC timestamp> (with a counter when that name is taken), and then the backup's copy is put in its
# place. Nothing is deleted: state written since the backup stays in the moved-aside paths, and a restore can be
# undone by moving them back.
#
# Refuses (nothing changes): a live daemon or lock under any recorded path; a --from without BACKUP.txt, or whose
# payload does not match what BACKUP.txt recorded. Exit codes: 0 done or planned; 1 refused or a step failed;
# 2 usage error or --from is not a backup.

set -u

TAB=$'\t'
apply=false
from=""

usage() { sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'; }
die() { echo "state-restore: $1" >&2; exit "${2:-2}"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --apply) apply=true ;;
    --from) [ $# -ge 2 ] || die "--from needs a directory"; from=$2; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "state-restore: unknown option $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

[ -n "$from" ] || die "--from DIR is required"
[ -d "$from" ] || die "--from ($from) is not a directory"
from_abs=$(cd "$from" && pwd -P)
[ -f "$from_abs/BACKUP.txt" ] || die "$from_abs has no BACKUP.txt: not a finished backup"
[ -d "$from_abs/payload" ] || die "$from_abs has no payload/: not a finished backup"

# entries: kind<TAB>id<TAB>role<TAB>path<TAB>files=N
entries=$(awk -F '\t' '$1 == "entry" { print $2 "\t" $3 "\t" $4 "\t" $5 "\t" $6 }' "$from_abs/BACKUP.txt")
[ -n "$entries" ] || die "BACKUP.txt records no entries"

pid_alive_in() { # <file>
  local pid
  pid=$(sed -n 's/.*"pid"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$1" 2>/dev/null | head -1)
  [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null
}

# the payload must hold what BACKUP.txt says
bad=""
while IFS="$TAB" read -r kind id role path count; do
  [ -n "$id" ] || continue
  want=${count#files=}
  if [ ! -e "$from_abs/payload/$id" ]; then bad="$bad
$id: missing from payload/"; continue; fi
  have=$(cd "$from_abs/payload" && find "$id" -type f | wc -l | tr -d ' ')
  [ "$have" = "$want" ] || bad="$bad
$id: $have files, BACKUP.txt says $want"
done <<<"$entries"
if [ -n "$(printf '%s' "$bad" | tr -d '[:space:]')" ]; then
  echo "state-restore: refusing: the payload does not match BACKUP.txt:" >&2
  printf '%s\n' "$bad" | sed '/^$/d; s/^/  /' >&2
  exit 1
fi

# a live daemon or lock under any recorded path
live=""
while IFS="$TAB" read -r kind id role path count; do
  [ "$kind" = dir ] && [ -d "$path" ] || continue
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    if pid_alive_in "$f"; then live="$live
$f"; fi
  done < <(find "$path" \( -name .domain.lock -o -name ahd.lock -o -name ahd.json \) -type f 2>/dev/null)
done <<<"$entries"
if [ -n "$(printf '%s' "$live" | tr -d '[:space:]')" ]; then
  echo "state-restore: refusing: a live daemon or lock holds state:" >&2
  printf '%s\n' "$live" | sed '/^$/d; s/^/  /' >&2
  echo "state-restore: stop it first (close every session, then 'ahd stop'); nothing changed." >&2
  exit 1
fi

ts=$(date -u +%Y%m%dT%H%M%SZ)
aside_name() { # <path> -> an unused <path>.native.<ts>[.N]
  local base="$1.native.$ts" n=0 cand
  cand=$base
  while [ -e "$cand" ] || [ -L "$cand" ]; do n=$((n+1)); cand="$base.$n"; done
  printf '%s\n' "$cand"
}

backup_time=$(sed -n 's/^time=//p' "$from_abs/BACKUP.txt" | head -1)
echo "state-restore: backup $from_abs (taken ${backup_time:-unknown}, plugin $(sed -n 's/^plugin_version=//p' "$from_abs/BACKUP.txt" | head -1))"
while IFS="$TAB" read -r kind id role path count; do
  [ -n "$id" ] || continue
  if [ -e "$path" ] || [ -L "$path" ]; then
    echo "  move aside  $path  ->  $(aside_name "$path")"
  fi
  echo "  restore     payload/$id  ->  $path  ($count)"
done <<<"$entries"

if ! $apply; then
  echo "state-restore: dry run, nothing changed. Pass --apply to do it."
  exit 0
fi

failed=false
moved=""
while IFS="$TAB" read -r kind id role path count; do
  [ -n "$id" ] || continue
  if [ -e "$path" ] || [ -L "$path" ]; then
    aside=$(aside_name "$path")
    if mv "$path" "$aside"; then
      moved="$moved
$path  ->  $aside"
    else
      echo "state-restore: could not move $path aside; stopping." >&2
      failed=true
      break
    fi
  fi
  mkdir -p "$(dirname "$path")" || { failed=true; break; }
  if [ "$kind" = dir ]; then
    mkdir -p "$path" || { failed=true; break; }
    (cd "$from_abs/payload/$id" && find . -print0 | cpio -0 -pdm "$path" >/dev/null 2>&1) || { echo "state-restore: copying $id failed" >&2; failed=true; break; }
    diff -rq "$from_abs/payload/$id" "$path" >/dev/null 2>&1 || { echo "state-restore: $path differs from payload/$id after the copy" >&2; failed=true; break; }
  else
    cp -p "$from_abs/payload/$id" "$path" || { failed=true; break; }
  fi
done <<<"$entries"

if $failed; then
  echo "state-restore: stopped before finishing. Moved aside so far (move them back to undo):" >&2
  printf '%s\n' "$moved" | sed '/^$/d; s/^/  /' >&2
  exit 1
fi
echo "state-restore: restored. State written since the backup is in:"
printf '%s\n' "$moved" | sed '/^$/d; s/^/  /'
echo "state-restore: a native binary installed under AH_HOME/bin is whatever the backup held; run scripts/install.sh --apply to build it again."
exit 0
