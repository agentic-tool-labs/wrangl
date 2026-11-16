#!/bin/bash
# agent-hierarchy — copies the state directories to a backup directory, so a rollback can put them back.
#
# Usage: scripts/state-backup.sh [--apply --out DIR] [--hierarchy-dir DIR]...
#   (no --apply)         prints the plan and writes nothing
#   --apply              copies; needs --out
#   --out DIR            the backup directory: absent or empty, and not equal to, inside or above any source
#   --hierarchy-dir DIR  one more hierarchy dir to include (repeatable); per-project dirs of other repos are
#                        found only when named here, via WRANGL_HIERARCHY_DIR, or from the current directory
#
# What is copied, each only when it exists:
#   - AH_HOME (default ~/.claude/ah2): registrations/, mailbox/, logs/, trusted/ and the rest of it,
#     without run/ (daemon socket, pid file, lock, paste payloads) and without sockets, fifos and .domain.lock files
#   - the hierarchy dirs: WRANGL_HIERARCHY_DIR; ~/.claude/ah2/hierarchy; the current checkout's .claude/ah2 and the
#     main checkout's when the current one is a linked worktree; every --hierarchy-dir
#   - ~/.claude/ah2/trusted when AH_HOME points elsewhere
#   - ~/.claude/ah2/trusted-hierarchy-dirs when it exists (a later release may create it); its absence is
#     never an error
#   - the files ~/.claude/ah2/agent-hierarchy.* (config, peer-pending, usage, usage cache, gate, session roles)
# Files that live inside a repository (a repo's .claude/ah2/agent-hierarchy.json, standing instructions, scaffolds, intake
# output) belong to that repository and are not copied. The plan names every directory it will copy and, on separate
# lines under "not found / not copied", every place it looked at and did not find and everything it leaves out.
#
# Layout of DIR: BACKUP.txt (written last: a directory without it is not a finished backup) and payload/ with one
# entry per source (d0, d1 ... directories; f0, f1 ... files). BACKUP.txt records the plugin version, git rev,
# time, and every source with its original path, plus every path left out.
#
# Refuses (nothing is written): a live daemon or lock (a pid in run/ahd.json, run/ahd.lock or a .domain.lock that
# is alive); an --out that is not empty. Exit codes: 0 done or planned; 1 refused because of live state or a
# non-empty --out; 2 usage error or an --out that is /, the home dir, or overlaps a source.
# This script deletes nothing and does not touch the daemon.

set -u

TAB=$'\t'
script_dir=$(cd "$(dirname "$0")" && pwd -P)
plugin_root=$(dirname "$script_dir")
apply=false
out=""
extra_dirs=""

usage() { sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'; }
die() { echo "state-backup: $1" >&2; exit "${2:-2}"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --apply) apply=true ;;
    --out) [ $# -ge 2 ] || die "--out needs a directory"; out=$2; shift ;;
    --hierarchy-dir) [ $# -ge 2 ] || die "--hierarchy-dir needs a directory"; extra_dirs="$extra_dirs
$2"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "state-backup: unknown option $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

if $apply && [ -z "$out" ]; then die "--apply needs --out DIR"; fi

home_dir=${HOME:-}
[ -n "$home_dir" ] || die "HOME is not set"
home_phys=$(cd "$home_dir" 2>/dev/null && pwd -P) || die "HOME is not a directory"

# absolute, with every existing ancestor resolved physically
abspath() {
  local p=$1 rest="" base
  case "$p" in /*) ;; *) p="$PWD/$p" ;; esac
  while [ "${#p}" -gt 1 ] && [ "${p%/}" != "$p" ]; do p=${p%/}; done
  while [ ! -d "$p" ] && [ "$p" != "/" ]; do
    base=${p##*/}
    rest="/$base$rest"
    p=${p%/*}
    [ -n "$p" ] || p="/"
  done
  if [ "$p" = "/" ]; then printf '%s\n' "$rest"; else printf '%s%s\n' "$(cd "$p" && pwd -P)" "$rest"; fi
}
under() { # under <path> <dir>: the path is the dir or inside it
  case "$1" in "$2"|"$2"/*) return 0 ;; esac
  return 1
}

# ---- sources
ah_home_raw=${AH_HOME:-}
if [ -z "$(printf '%s' "$ah_home_raw" | tr -d '[:space:]')" ]; then ah_home_raw="$home_phys/.claude/ah2"; fi
ah_home=$(abspath "$ah_home_raw")

found=""   # role<TAB>path per line
missing="" # places looked at and not there, one per line
add_source() { # <role> <path>
  if [ ! -e "$2" ]; then
    missing="$missing
$2"
    return 0
  fi
  local p
  if [ -d "$2" ]; then p=$(cd "$2" && pwd -P); else p="$(cd "$(dirname "$2")" && pwd -P)/$(basename "$2")"; fi
  found="$found
$1$TAB$p"
}
add_source ah_home "$ah_home"
[ -n "${WRANGL_HIERARCHY_DIR:-}" ] && add_source hierarchy "$WRANGL_HIERARCHY_DIR"
add_source hierarchy "$home_phys/.claude/ah2/hierarchy"
if git_root=$(git rev-parse --show-toplevel 2>/dev/null) && [ -n "$git_root" ]; then
  add_source hierarchy "$git_root/.claude/ah2"
  if common=$(git rev-parse --git-common-dir 2>/dev/null) && [ -n "$common" ]; then
    case "$common" in /*) ;; *) common="$PWD/$common" ;; esac
    case "$common" in */.git) add_source hierarchy "$(dirname "$common")/.claude/ah2" ;; esac
  fi
fi
while IFS= read -r d; do [ -n "$d" ] && add_source hierarchy "$d"; done <<<"$extra_dirs"
add_source trusted "$home_phys/.claude/ah2/trusted"
add_source file "$home_phys/.claude/ah2/trusted-hierarchy-dirs"
for f in "$home_phys"/.claude/ah2/agent-hierarchy.*; do [ -f "$f" ] && add_source file "$f"; done

# drop an entry that repeats an earlier one or sits inside another directory entry
sources=""
while IFS="$TAB" read -r role path; do
  [ -n "$path" ] || continue
  drop=false
  while IFS="$TAB" read -r r2 p2; do
    [ -n "$p2" ] || continue
    if [ -d "$p2" ] && [ "$p2" != "$path" ] && under "$path" "$p2"; then drop=true; break; fi
  done <<<"$found"
  case "$sources"$'\n' in *"$TAB$path$TAB"$'\n'*) drop=true ;; esac
  $drop || sources="$sources
$role$TAB$path$TAB"
done <<<"$found"
sources=$(printf '%s\n' "$sources" | sed '/^$/d; s/'"$TAB"'$//')

[ -n "$sources" ] || die "no state directory found (looked at $ah_home and $home_phys/.claude)" 1

# ---- --out guards
out_abs=""
if [ -n "$out" ]; then
  out_abs=$(abspath "$out")
  { [ "$out_abs" != "/" ] && [ -n "$out_abs" ]; } || die "--out is the filesystem root"
  [ "$out_abs" != "$home_phys" ] || die "--out is the home directory itself"
  while IFS="$TAB" read -r role path; do
    [ -n "$path" ] || continue
    if under "$out_abs" "$path"; then die "--out ($out_abs) is inside a source ($path)"; fi
    if under "$path" "$out_abs"; then die "--out ($out_abs) contains a source ($path)"; fi
  done <<<"$sources"
fi

# ---- what is copied, what is left out, and live state
# entries to copy under a directory source, relative and NUL separated
copy_list() { # <role> <dir>
  if [ "$1" = ah_home ]; then
    (cd "$2" && find . \( -path ./run -o -name .domain.lock \) -prune -o \( -type f -o -type d \) -print0)
  else
    (cd "$2" && find . -name .domain.lock -prune -o \( -type f -o -type d \) -print0)
  fi
}
# the paths left out
left_list() { # <role> <dir>
  if [ "$1" = ah_home ]; then
    (cd "$2" && find . \( -path ./run -o -name .domain.lock \) -prune -print -o \( -type s -o -type p \) -print)
  else
    (cd "$2" && find . \( -name .domain.lock -o -type s -o -type p \) -print)
  fi
}
count_files() { copy_list "$1" "$2" | tr '\0' '\n' | while IFS= read -r e; do [ -f "$2/$e" ] && echo x; done | wc -l | tr -d ' '; }
pid_alive_in() { # <file>
  local pid
  pid=$(sed -n 's/.*"pid"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' "$1" 2>/dev/null | head -1)
  [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null
}

live=""
while IFS="$TAB" read -r role path; do
  [ -d "$path" ] || continue
  while IFS= read -r f; do
    [ -n "$f" ] || continue
    if pid_alive_in "$f"; then live="$live
$f"; fi
  done < <(find "$path" \( -name .domain.lock -o -name ahd.lock -o -name ahd.json \) -type f 2>/dev/null)
done <<<"$sources"
if [ -n "$(printf '%s' "$live" | tr -d '[:space:]')" ]; then
  echo "state-backup: refusing: a live daemon or lock holds state:" >&2
  printf '%s\n' "$live" | sed '/^$/d; s/^/  /' >&2
  echo "state-backup: stop it first (close every session, then 'ahd stop'); nothing was written." >&2
  exit 1
fi

# ---- plan
echo "state-backup: sources (found from AH_HOME=$ah_home, HOME=$home_phys):"
di=0; fi_=0; total=0
plan=""
while IFS="$TAB" read -r role path; do
  [ -n "$path" ] || continue
  if [ -d "$path" ]; then
    n=$(count_files "$role" "$path")
    echo "  d$di  $role  $path  ($n files)"
    plan="$plan
d$di$TAB$role$TAB$path"
    di=$((di+1))
  else
    n=1
    echo "  f$fi_  $role  $path  (1 file)"
    plan="$plan
f$fi_$TAB$role$TAB$path"
    fi_=$((fi_+1))
  fi
  total=$((total + n))
done <<<"$sources"
plan=$(printf '%s\n' "$plan" | sed '/^$/d')
echo "state-backup: $total files"
echo "state-backup: not found / not copied:"
# the places that were looked at and are not there; a path already copied through a directory above it is not "missing"
printf '%s\n' "$missing" | sed '/^$/d' | LC_ALL=C sort -u | while IFS= read -r m; do
  echo "  not found:  $m"
done
echo "  not copied: $ah_home/run/ (the daemon's pid file, lock, socket and paste payloads)"
echo "  not copied: sockets, fifos and .domain.lock files under any copied directory"
echo "  not copied: <repo>/.claude/ah2/agent-hierarchy.json (a repo's own config; it belongs to the repository)"
echo "  not copied: standing instructions and role scaffolds (files inside a repository)"
echo "  not copied: the issue-intake output directory (regenerable)"

if ! $apply; then
  echo "state-backup: dry run, nothing written. Pass --apply --out DIR to copy."
  exit 0
fi

# ---- copy
if [ -e "$out_abs" ]; then
  [ -d "$out_abs" ] || die "--out exists and is not a directory" 1
  if [ -n "$(ls -A "$out_abs" 2>/dev/null)" ]; then die "--out ($out_abs) is not empty" 1; fi
fi
mkdir -p "$out_abs/payload" || die "cannot create $out_abs" 1
left_out=""
failed=false
while IFS="$TAB" read -r id role path; do
  [ -n "$id" ] || continue
  if [ -d "$path" ]; then
    mkdir -p "$out_abs/payload/$id"
    copy_list "$role" "$path" | (cd "$path" && cpio -0 -pdm "$out_abs/payload/$id" >/dev/null 2>&1) || failed=true
    while IFS= read -r s; do
      [ -n "$s" ] && left_out="$left_out
$path/${s#./}"
    done < <(left_list "$role" "$path" 2>/dev/null)
  else
    cp -p "$path" "$out_abs/payload/$id" || failed=true
  fi
done <<<"$plan"
if $failed; then
  echo "state-backup: a copy failed; $out_abs holds a partial backup without BACKUP.txt and must not be used." >&2
  exit 1
fi

plugin_version=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$plugin_root/.claude-plugin/plugin.json" 2>/dev/null | head -1)
git_rev=$(git -C "$plugin_root" rev-parse HEAD 2>/dev/null || echo unknown)
{
  echo "# agent-hierarchy state backup"
  echo "plugin_version=${plugin_version:-unknown}"
  echo "git_rev=$git_rev"
  echo "time=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "home=$home_phys"
  echo "ah_home=$ah_home"
  while IFS="$TAB" read -r id role path; do
    [ -n "$id" ] || continue
    if [ -d "$path" ]; then kind=dir; else kind=file; fi
    n=$(cd "$out_abs/payload" && find "$id" -type f | wc -l | tr -d ' ')
    printf 'entry\t%s\t%s\t%s\t%s\tfiles=%s\n' "$kind" "$id" "$role" "$path" "$n"
  done <<<"$plan"
  while IFS= read -r s; do
    [ -n "$s" ] && printf 'skipped\t%s\n' "$s"
  done <<<"$left_out"
} > "$out_abs/BACKUP.txt"

echo "state-backup: copied $total files to $out_abs (BACKUP.txt written)"
exit 0
