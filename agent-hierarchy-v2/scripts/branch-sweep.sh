#!/bin/sh
# agent-hierarchy — after a merge or ship to golden, archives local branches whose every commit is in golden, prunes work-tree
# entries, runs ah's scratch sweep and removes old probe dirs. Plain git; ah is optional.
#
# Usage: branch-sweep.sh [--commit] [-C <repo>] [--target <ref>]
#   (default)    lists what each step would do and changes nothing
#   --commit     acts
#   -C <repo>    the repository (default: the current directory)
#   --target <ref>  use <ref> (a run's integration branch) as golden for this run; unresolvable: exit 3
#
# Steps, with --commit:
#   1 branches   archive refs/heads/<b> to refs/archive/<YYYY-MM-DD>/<b> when it is not live, `git cherry <golden> <b>` has no
#                '+' lines, it is checked out nowhere, and it is older than 72 h. A branch with '+' lines is listed, never touched.
#   2 worktrees  git worktree remove (never forced) a clean, non-live work tree whose branch step 1 archives, or that is detached
#                with no '+' lines, when it passes the HEAD guard (no git operation in progress; HEAD is the branch tip or on
#                some branch, tag, remote or archive ref). Live streams come from `ah roster stream-list`; with no ah, or when
#                the listing fails, every checked-out branch counts as live and nothing is removed. A team member's recorded
#                work tree in a team that is not closed (`ah roster teams --worktrees` of every hierarchy dir, held members
#                and Orchestrators too) is kept `live-member`, and a tree whose own .claude/ah2 holds such a team `live-team`.
#                When any dir's listing fails or lacks the field on a row (named on stderr), every tree is kept
#                `members-unknown`; branches and prune still run. A removed tree takes its
#                ignored files with it (git's clean check does not count them). A tree that is any process's
#                working directory is kept `cwd-in-use <pid>`; when the processes cannot be listed, `cwd-unknown`
#   3 prune     git worktree prune (it keeps locked entries)
#   4 scratch    ah roster scratch-sweep --commit, when ah is on PATH
#   5 probes     ${AH_BRANCH_SWEEP_TMP:-/tmp}/ahprobe.* owned by you, untouched for 72 h, holding no registered work tree
# Nothing is pushed: archive refs stay in this clone.
# Live, never touched: golden's local name, every `git config --get-all ah.keepBranch` value, the main work tree's branch and
# every branch checked out in any work tree. Tags are never touched. Nothing is ever forced.
#
# Golden: git config ah.releaseRef (set but unresolvable: exit 3), else origin/HEAD's target, else origin/main, origin/master,
# else local main, master. None: steps 1-2 are skipped.
#
# Restore an archived branch:
#   git branch <b> refs/archive/<date>/<b> && git update-ref -d refs/archive/<date>/<b>
#
# Output: one line per item, `<category> <action|kept> <name> [<reason>]`, then a totals line.
# Exit: 0 done or listed (also when items were kept), 2 usage or not a git repository, 3 release-ref-invalid.

usage() { sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'; }

COMMIT=0
REPO=.
TARGET=
while [ $# -gt 0 ]; do
  case $1 in
    --commit) COMMIT=1 ;;
    -C) [ $# -ge 2 ] || { echo "branch-sweep: -C needs a repository" >&2; exit 2; }; REPO=$2; shift ;;
    --target) [ $# -ge 2 ] && [ -n "$2" ] || { echo "branch-sweep: --target needs a ref" >&2; exit 2; }; TARGET=$2; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "branch-sweep: unknown argument $1 (see --help)" >&2; exit 2 ;;
  esac
  shift
done

G() { git -C "$REPO" "$@"; }
G rev-parse --git-dir >/dev/null 2>&1 || { echo "branch-sweep: $REPO is not a git repository" >&2; exit 2; }

ARCHIVED=0; PRUNED=0; PROBES=0; KEPT=0
say() { echo "$*"; }
kept() { KEPT=$((KEPT + 1)); say "$1 kept $2${3:+ $3}"; }
resolves() { case $1 in -*) return 1 ;; esac; G rev-parse --verify -q "$1^{commit}" >/dev/null 2>&1; }
short() { case $1 in refs/remotes/*) echo "${1#refs/remotes/}" ;; refs/heads/*) echo "${1#refs/heads/}" ;; *) echo "$1" ;; esac; }

# ---- golden (§2): the same five rules as ah's stream-open and stream-done
GOLDEN=
CONFIGURED=$(G config --get ah.releaseRef 2>/dev/null)
[ -z "$TARGET" ] || CONFIGURED=$TARGET
if [ -n "$CONFIGURED" ]; then
  resolves "$CONFIGURED" || { echo "release-ref-invalid $CONFIGURED" >&2; exit 3; }
  GOLDEN=$CONFIGURED
else
  for candidate in $(G symbolic-ref -q refs/remotes/origin/HEAD 2>/dev/null) refs/remotes/origin/main refs/remotes/origin/master refs/heads/main refs/heads/master; do
    if resolves "$candidate"; then GOLDEN=$(short "$candidate"); break; fi
  done
fi

if [ -z "$GOLDEN" ]; then
  say "golden-unresolved"
  GOLDEN_LOCAL=
else
  say "golden $GOLDEN"
  case $GOLDEN in
    refs/heads/*) GOLDEN_LOCAL=${GOLDEN#refs/heads/} ;;
    refs/remotes/*) GOLDEN_LOCAL=${GOLDEN#refs/remotes/*/} ;;
    */*) if G rev-parse --verify -q "refs/remotes/$GOLDEN" >/dev/null 2>&1; then GOLDEN_LOCAL=${GOLDEN#*/}; else GOLDEN_LOCAL=$GOLDEN; fi ;;
    *) GOLDEN_LOCAL=$GOLDEN ;;
  esac
fi

NL='
'
WORKTREES=$(G worktree list --porcelain 2>/dev/null)
# every branch checked out in some work tree, one per line
CHECKED_OUT=$(printf '%s\n' "$WORKTREES" | sed -n 's|^branch refs/heads/||p')
MAIN_BRANCH=$(printf '%s\n' "$WORKTREES" | sed -n '1,/^$/s|^branch refs/heads/||p')
KEEP=$(G config --get-all ah.keepBranch 2>/dev/null | sed 's|^refs/heads/||')

in_list() { printf '%s\n' "$2" | grep -qxF -- "$1"; }

MAIN_PATH=$(printf '%s\n' "$WORKTREES" | sed -n '1s|^worktree ||p')
# every linked work tree that still exists, as <path>\t<branch or empty when detached>
WT_PAIRS=$(printf '%s\n\n' "$WORKTREES" | awk '
  /^worktree / { p = substr($0, 10); b = ""; next }
  /^branch refs\/heads\// { b = substr($0, 19); next }
  /^$/ { if (p != "") print p "\t" b; p = "" }' | sed 1d)

# live streams from ah's read-only listing (D14): <branch>\t<worktree or ->. No ah, or the listing fails: ah-absent mode, in which
# every checked-out branch is live and step 2 removes nothing.
AH_LISTED=0
LIVE_BRANCHES=
LIVE_TREES=
if command -v ah >/dev/null 2>&1 && ABS_REPO=$(cd "$REPO" && pwd) && LISTING=$(ah roster stream-list --cwd "$ABS_REPO" 2>/dev/null); then
  AH_LISTED=1
  LIVE_BRANCHES=$(printf '%s\n' "$LISTING" | cut -f1)
  # both spellings of each live work tree: as ah recorded it and resolved (git lists resolved paths)
  LIVE_TREES=$(printf '%s\n' "$LISTING" | cut -f2 | grep -vxF -- - | while IFS= read -r l_p; do
    echo "$l_p"; (cd "$l_p" 2>/dev/null && pwd -P)
  done)
fi

# every team member's recorded work tree, held members and the latest checkpoint's Orchestrator included (they have no process),
# from `ah roster teams --worktrees` (member_worktrees, both spellings; that dir's own teams only, no liveness reads), asked of
# every hierarchy dir: the repo's, the main checkout's and each linked tree's own .claude/ah2. A call that fails, prints no teams
# document, has a team row without member_worktrees, or holds a path JSON had to escape (a backslash) is named on stderr,
# MEMBERS_OK=0, and step 2 keeps every tree members-unknown (its teams' member trees could be anywhere).
MEMBERS_OK=0
MEMBER_TREES=
TEAM_TREES=
if [ "$AH_LISTED" -eq 1 ]; then
  MEMBERS_OK=1
  M_DIRS=$(printf '%s\n%s\n' "$ABS_REPO" "$MAIN_PATH"; printf '%s\n' "$WT_PAIRS" | cut -f1 | while IFS= read -r m_wt; do
    [ -n "$m_wt" ] && [ -d "$m_wt/.claude/ah2" ] && echo "$m_wt"
  done)
  M_RAW=
  while IFS= read -r m_dir; do
    [ -n "$m_dir" ] || continue
    m_live=
    if ! m_teams=$(ah roster teams --worktrees --cwd "$m_dir" 2>/dev/null </dev/null) || ! printf '%s\n' "$m_teams" | grep -q '^  "teams": \[' \
      || [ "$(printf '%s\n' "$m_teams" | grep -c '^      "life": ')" -ne "$(printf '%s\n' "$m_teams" | grep -c '^      "member_worktrees": ')" ]; then
      m_live='\'
    else
      # only teams that are not closed (live or held) protect anything; a tree whose own hierarchy dir has one is kept live-team
      m_live=$(printf '%s\n' "$m_teams" | awk '
        /^      "life": / { closed = ($0 ~ /"closed"/); if (!closed) live = 1; next }
        /"member_worktrees": \[\]/ { next }
        /"member_worktrees": \[/ { on = !closed; next }
        on && /^ *\]/ { on = 0; next }
        on { sub(/^ *"/, ""); sub(/",?$/, ""); print }
        END { if (live) print "\001live" }')
    fi
    if printf '%s\n' "$m_live" | grep -qF '\'; then
      echo "branch-sweep: members-unknown $m_dir (ah roster teams --worktrees failed or was unparseable there; every work tree is kept this run)" >&2
      MEMBERS_OK=0; break
    fi
    if printf '%s\n' "$m_live" | grep -qx "$(printf '\001')live"; then TEAM_TREES="$TEAM_TREES$NL$m_dir$NL$(cd "$m_dir" 2>/dev/null && pwd -P)"; fi
    M_RAW="$M_RAW$NL$(printf '%s\n' "$m_live" | grep -vx "$(printf '\001')live")"
  done <<EOF
$(printf '%s\n' "$M_DIRS" | awk 'NF && !seen[$0]++')
EOF
  if [ "$MEMBERS_OK" -eq 1 ]; then
    MEMBER_TREES=$(printf '%s\n' "$M_RAW" | grep -v '^$' | while IFS= read -r m_p; do
      echo "$m_p"; (cd "$m_p" 2>/dev/null && pwd -P)
    done)
  fi
fi

# '+' commits of <rev> against golden; a failed git call counts as unshipped (fail closed, prints "fail")
unshipped() {
  if ! u_cherry=$(G cherry "$GOLDEN" "$1" 2>/dev/null); then echo fail; return; fi
  # after a catch-up merge of golden into the branch, golden's copies are reachable from the branch and git cherry no longer
  # compares against them: re-check each '+' commit against golden from that commit itself
  u_plus=0
  u_merged=$(G rev-list --merges -n 1 "$GOLDEN..$1" 2>/dev/null)
  for u_s in $(printf '%s\n' "$u_cherry" | sed -n 's/^+ //p'); do
    if [ -z "$u_merged" ]; then u_plus=$((u_plus + 1)); continue; fi
    if ! u_alone=$(G cherry "$GOLDEN" "$u_s" 2>/dev/null) || printf '%s\n' "$u_alone" | grep -qxF "+ $u_s"; then u_plus=$((u_plus + 1)); fi
  done
  echo "$u_plus"
}

# the §3.2 step 6 work-tree HEAD guard: prints nothing when <wt> may go, else op-in-progress <op>, unreachable-commits <n> or guard-failed
head_guard() {
  for g_pair in rebase-merge:rebase rebase-apply:rebase MERGE_HEAD:merge CHERRY_PICK_HEAD:cherry-pick REVERT_HEAD:revert BISECT_LOG:bisect; do
    g_path=$(git -C "$1" rev-parse --path-format=absolute --git-path "${g_pair%%:*}" 2>/dev/null) || { echo guard-failed; return; }
    if [ -e "$g_path" ]; then echo "op-in-progress ${g_pair#*:}"; return; fi
  done
  g_head=$(git -C "$1" rev-parse HEAD 2>/dev/null) || { echo guard-failed; return; }
  if [ -n "$2" ] && [ "$g_head" = "$2" ]; then return; fi
  g_n=$(git -C "$1" rev-list --count HEAD --not --branches --tags --remotes '--glob=refs/archive/*' 2>/dev/null) || { echo guard-failed; return; }
  [ "$g_n" -eq 0 ] || echo "unreachable-commits $g_n"
}

# why branch <b> is kept, ignoring where it is checked out; empty when step 1 may archive it (sets B_SHA, B_REF, B_EXISTING)
branch_why() {
  if [ "$1" = "$GOLDEN_LOCAL" ]; then echo golden; return; fi
  if in_list "$1" "$KEEP"; then echo keep-listed; return; fi
  if [ "$1" = "$MAIN_BRANCH" ]; then echo main-checkout; return; fi
  if [ "$AH_LISTED" -eq 1 ] && in_list "$1" "$LIVE_BRANCHES"; then echo live-stream; return; fi
  if [ "$AH_LISTED" -eq 0 ] && in_list "$1" "$CHECKED_OUT"; then echo checked-out; return; fi
  w_u=$(unshipped "refs/heads/$1")
  if [ "$w_u" = fail ]; then echo git-failed; return; fi
  if [ "$w_u" -gt 0 ]; then echo "unshipped $w_u"; return; fi
  w_born=$(G reflog show --date=unix --format=%gd "refs/heads/$1" -- 2>/dev/null | tail -n 1 | sed -n 's/.*@{\([0-9]*\)}$/\1/p')
  [ -n "$w_born" ] || w_born=$(G log -1 --format=%ct "refs/heads/$1" 2>/dev/null)
  if [ -z "$w_born" ] || [ $((NOW - w_born)) -lt 259200 ]; then echo too-new; return; fi
  B_SHA=$(G rev-parse --verify -q "refs/heads/$1")
  B_REF="refs/archive/$DATE/$1"
  B_EXISTING=$(G rev-parse --verify -q "$B_REF" 2>/dev/null)
  if [ -n "$B_EXISTING" ] && [ "$B_EXISTING" != "$B_SHA" ]; then echo archive-ref-exists; fi
}

# every process's working directory, one `<pid> <path>` per line (Linux: /proc, elsewhere lsof); empty when it cannot be told
# ponytail: a partial listing (lsof killed, another uid's process) counts as complete; `git worktree remove` is unforced, so dirty trees stay safe. Check the pipeline's exit if a clean tree in use is ever removed.
process_cwds() {
  if [ -e /proc/self/cwd ]; then
    for c_p in /proc/[0-9]*; do c_d=$(readlink "$c_p/cwd" 2>/dev/null) && echo "${c_p#/proc/} $c_d"; done
  else
    c_lsof=$(command -v lsof) || c_lsof=/usr/sbin/lsof
    "$c_lsof" -d cwd -Fn 2>/dev/null | awk '/^p/ { p = substr($0, 2) } /^n/ { print p, substr($0, 2) }'
  fi
}

# listed once per run, not per candidate tree (lsof per tree is too slow at hundreds of trees); callers run in subshells, so
# the listing is taken here, in the main shell, whenever step 2 can see a linked tree
CWDS=
[ -z "$GOLDEN" ] || [ -z "$WT_PAIRS" ] || CWDS=$(process_cwds)

# why work tree <path> must stay because a process stands in it or below it; empty when none does
cwd_why() {
  [ -n "$CWDS" ] || { echo cwd-unknown; return; }
  printf '%s\n' "$CWDS" | CWD_TREE="$(cd "$1" 2>/dev/null && pwd -P)/" awk '
    { i = index($0, " ") }
    index(substr($0, i + 1) "/", ENVIRON["CWD_TREE"]) == 1 { print "cwd-in-use", substr($0, 1, i - 1); exit }'
}

# why work tree <path> (holding <branch>, empty when detached) is kept; empty when step 2 may remove it
worktree_why() {
  t_dirty=$(git -C "$1" status --porcelain --untracked-files=all 2>/dev/null) || { echo git-failed; return; }
  if [ "$AH_LISTED" -eq 0 ]; then if [ -n "$t_dirty" ]; then echo dirty; else echo checked-out; fi; return; fi
  # git's spelling may be unresolved (a symlink made after the registration): compare it resolved too
  if in_list "$1" "$LIVE_TREES" || in_list "$(cd "$1" 2>/dev/null && pwd -P)" "$LIVE_TREES"; then echo live-stream; return; fi
  if [ "$MEMBERS_OK" -eq 0 ]; then echo members-unknown; return; fi
  if in_list "$1" "$MEMBER_TREES" || in_list "$(cd "$1" 2>/dev/null && pwd -P)" "$MEMBER_TREES"; then echo live-member; return; fi
  if in_list "$1" "$TEAM_TREES" || in_list "$(cd "$1" 2>/dev/null && pwd -P)" "$TEAM_TREES"; then echo live-team; return; fi
  if [ -n "$2" ]; then
    [ -z "$(branch_why "$2")" ] || { echo branch-kept; return; }
    t_want=$(G rev-parse --verify -q "refs/heads/$2")
  else
    t_u=$(unshipped "$(git -C "$1" rev-parse HEAD 2>/dev/null)")
    if [ "$t_u" = fail ]; then echo git-failed; return; fi
    if [ "$t_u" -gt 0 ]; then echo "unshipped $t_u"; return; fi
    t_want=
  fi
  if [ -n "$t_dirty" ]; then echo dirty; return; fi
  t_cwd=$(cwd_why "$1")
  if [ -n "$t_cwd" ]; then echo "$t_cwd"; return; fi
  head_guard "$1" "$t_want"
}

remove_worktree() {  # <path>: prints the worktree line; fails when git refuses
  if [ "$COMMIT" -eq 1 ]; then
    G worktree remove "$1" 2>/dev/null || { kept worktree "$1" remove-failed; return 1; }
    say "worktree removed $1"
  else
    say "worktree would-remove $1"
  fi
  REMOVED="$REMOVED$NL$1"
}

REMOVED=
# ---- step 1: branches (a checked-out branch only when step 2 removes its work tree, guard included)
if [ -n "$GOLDEN" ]; then
  NOW=$(date +%s)
  DATE=$(date +%Y-%m-%d)
  for b in $(G for-each-ref --format='%(refname)' refs/heads/ | sed 's|^refs/heads/||'); do
    why=$(branch_why "$b")
    if [ -n "$why" ]; then kept branch "$b" "$why"; continue; fi
    branch_why "$b" >/dev/null  # sets B_SHA, B_REF, B_EXISTING in this shell
    wt=$(printf '%s\n' "$WT_PAIRS" | awk -F '\t' -v b="$b" '$2 == b { print $1; exit }')
    if [ -n "$wt" ]; then
      if [ -n "$(worktree_why "$wt" "$b")" ]; then kept branch "$b" checked-out; continue; fi
      remove_worktree "$wt" || { kept branch "$b" checked-out; continue; }
    fi
    if [ "$COMMIT" -eq 1 ]; then
      if [ -z "$B_EXISTING" ] && ! G update-ref -m "branch-sweep archive" "$B_REF" "$B_SHA" "" 2>/dev/null; then kept branch "$b" archive-failed; continue; fi
      if ! G update-ref -d "refs/heads/$b" "$B_SHA" 2>/dev/null; then kept branch "$b" delete-failed; continue; fi
      say "branch archived $b $B_REF"
    else
      say "branch would-archive $b $B_REF"
    fi
    ARCHIVED=$((ARCHIVED + 1))
  done
fi

# ---- step 2: work trees not handled in step 1
if [ -n "$GOLDEN" ]; then
  while IFS='	' read -r wt b; do
    [ -n "$wt" ] && [ -d "$wt" ] || continue
    in_list "$wt" "$REMOVED" && continue
    why=$(worktree_why "$wt" "$b")
    if [ -n "$why" ]; then kept worktree "$wt" "$why"; continue; fi
    [ -z "$b" ] || { kept worktree "$wt" checked-out; continue; }
    remove_worktree "$wt" || true
  done <<EOF
$WT_PAIRS
EOF
fi

# ---- step 3: prune (locked entries stay: git's own rule)
ACT=would-prune
[ "$COMMIT" -eq 1 ] && ACT=pruned
PRUNE_LINES=$(printf '%s\n\n' "$WORKTREES" | awk '
  /^worktree / { entry = substr($0, 10); locked = 0; prunable = 0; next }
  /^locked( |$)/ { locked = 1; next }
  /^prunable( |$)/ { prunable = 1; next }
  /^$/ { if (entry != "") print locked "\t" prunable "\t" entry; entry = "" }' | while IFS='	' read -r locked prunable entry; do
    if [ "$locked" = 1 ] && [ ! -d "$entry" ]; then echo "prune kept $entry locked"
    elif [ "$prunable" = 1 ] && [ "$locked" = 0 ]; then echo "prune $ACT $entry"
    fi
  done)
[ "$COMMIT" -eq 1 ] && G worktree prune 2>/dev/null
[ -n "$PRUNE_LINES" ] && say "$PRUNE_LINES"
PRUNED=$(printf '%s\n' "$PRUNE_LINES" | grep -c "^prune $ACT ")
KEPT=$((KEPT + $(printf '%s\n' "$PRUNE_LINES" | grep -c '^prune kept ')))

# ---- step 4: ah's own scratch sweep
if [ -z "$GOLDEN" ]; then
  say "scratch kept golden-unresolved"
elif command -v ah >/dev/null 2>&1; then
  if [ "$COMMIT" -eq 1 ]; then
    ah roster scratch-sweep --commit --cwd "$REPO" >/dev/null 2>&1 && say "scratch swept ah" || say "scratch kept ah failed"
  else
    say "scratch would-sweep ah"
  fi
else
  say "scratch kept ah absent"
fi

# ---- step 5: probe dirs
TMPROOT=${AH_BRANCH_SWEEP_TMP:-/tmp}
UID_NOW=$(id -u)
REGISTERED=$(printf '%s\n' "$WORKTREES" | sed -n 's|^worktree ||p')
for d in "$TMPROOT"/ahprobe.*; do
  [ -d "$d" ] && [ ! -L "$d" ] || continue
  if [ -z "$(find "$d" -prune -user "$UID_NOW" 2>/dev/null)" ]; then kept probe "$d" other-owner; continue; fi
  real=$(cd "$d" 2>/dev/null && pwd -P)
  held=$(printf '%s\n' "$REGISTERED" | while IFS= read -r wt; do
    case $wt in ("$d"/*|"$real"/*) echo 1; break ;; esac
  done)
  held=${held:-0}
  if [ "$held" -eq 1 ]; then kept probe "$d" worktree; continue; fi
  if [ -n "$(find "$d" -mtime -3 2>/dev/null | head -n 1)" ]; then kept probe "$d" recent; continue; fi
  if [ "$COMMIT" -eq 1 ]; then
    if rm -rf -- "$d"; then say "probe removed $d"; else kept probe "$d" delete-failed; continue; fi
  else
    say "probe would-remove $d"
  fi
  PROBES=$((PROBES + 1))
done

say "totals archived=$ARCHIVED pruned=$PRUNED probes=$PROBES kept=$KEPT committed=$COMMIT"
exit 0
