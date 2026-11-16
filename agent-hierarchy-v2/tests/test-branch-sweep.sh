#!/bin/bash
# agent-hierarchy — scripts/branch-sweep.sh against temp repos built here (spec branch-hygiene §6 "script" rows, §7).
# Usage: bash tests/test-branch-sweep.sh   (exits 0 iff all cases pass)
. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-pid.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SWEEP="${BRANCH_SWEEP_UNDER_TEST:-$PLUGIN/scripts/branch-sweep.sh}"  # override: run the suite against another copy (must-fail checks)
PASS=0; FAIL=0
check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name"; fi
}
SCAN="$(mktemp -d "${TMPDIR:-/tmp}/ah-branch-sweep.XXXXXX")"
[ -n "$SCAN" ] || { echo "FAIL: mktemp"; exit 1; }
hermetic_on_exit "rm -rf \"$SCAN\""

export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@example.invalid GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@example.invalid
OLD="2026-01-01T00:00:00 +0000"
# PATH with git and the basics but no ah and no gitleaks
NOAH="$SCAN/bin"; mkdir -p "$NOAH"
for tool in git sed awk grep find date id rm cat tail head wc sh env mkdir touch ls cut readlink; do
  p=$(command -v "$tool") && ln -sf "$p" "$NOAH/$tool"
done

n=0
repo() {  # a fresh repo with one commit on <trunk> (default main) at $r; $n numbers it
  n=$((n+1)); r="$SCAN/r$n"
  git init -q -b "${1:-main}" "$r" && echo base > "$r/f" && git -C "$r" add f && GIT_COMMITTER_DATE="$OLD" git -C "$r" commit -qm base
}
old_branch() {  # <repo> <branch> [<from>]: a branch whose reflog says it was made long ago
  GIT_COMMITTER_DATE="$OLD" git -C "$1" branch "$2" "${3:-HEAD}"
}
commit_on() {  # <repo> <branch> <file>: one commit on <branch>, made long ago, without checking it out
  local r=$1 b=$2 f=$3 tree blob
  blob=$(echo "$f" | git -C "$r" hash-object -w --stdin)
  tree=$(printf '100644 blob %s\t%s\n' "$blob" "$f" | git -C "$r" mktree)
  GIT_COMMITTER_DATE="$OLD" git -C "$r" update-ref "refs/heads/$b" "$(GIT_COMMITTER_DATE="$OLD" git -C "$r" commit-tree "$tree" -p "refs/heads/$b" -m "$f")"
}
sweep() { PATH="$NOAH" AH_BRANCH_SWEEP_TMP="$SCAN/tmp-$n" sh "$SWEEP" "$@"; }
has_ref() { git -C "$1" rev-parse -q --verify "$2" >/dev/null; }

# ---- golden: the five rules give what ah's stream-open gives (BranchHygieneTests golden rows)
repo; git -C "$r" update-ref refs/remotes/origin/main HEAD; git -C "$r" update-ref refs/remotes/origin/develop HEAD
git -C "$r" symbolic-ref refs/remotes/origin/HEAD refs/remotes/origin/develop
check "golden: origin/HEAD target first" '[ "$(sweep -C "$r" | head -1)" = "golden origin/develop" ]'
repo; git -C "$r" update-ref refs/remotes/origin/master HEAD
check "golden: origin/master before local main" '[ "$(sweep -C "$r" | head -1)" = "golden origin/master" ]'
repo
check "golden: local-only main" '[ "$(sweep -C "$r" | head -1)" = "golden main" ]'
repo; git -C "$r" update-ref refs/remotes/origin/main HEAD; git -C "$r" symbolic-ref refs/remotes/origin/HEAD refs/remotes/origin/main
git -C "$r" branch rel; git -C "$r" config ah.releaseRef refs/heads/rel
check "golden: ah.releaseRef wins over origin/HEAD" '[ "$(sweep -C "$r" | head -1)" = "golden refs/heads/rel" ]'
repo trunk
check "golden: unresolved, steps 1-2 skipped" '[ "$(sweep -C "$r" | head -1)" = "golden-unresolved" ] && ! sweep -C "$r" | grep -q "^branch "'
repo; git -C "$r" config ah.releaseRef origin/nope
sweep -C "$r" >/dev/null 2>&1; rc=$?
check "golden: ah.releaseRef unresolvable exits 3" '[ "$rc" -eq 3 ]'

# ---- branches
repo; old_branch "$r" shipped; old_branch "$r" wip; commit_on "$r" wip wip.txt; git -C "$r" branch fresh
git -C "$r" tag v1.0
before=$(git -C "$r" for-each-ref)
out=$(sweep -C "$r")
check "default lists and changes nothing" '[ "$before" = "$(git -C "$r" for-each-ref)" ] && echo "$out" | grep -q "^branch would-archive shipped refs/archive/"'
out=$(sweep -C "$r" --commit)
check "shipped old branch is archived" '! has_ref "$r" refs/heads/shipped && has_ref "$r" "refs/archive/$(date +%Y-%m-%d)/shipped"'
check "branch with + lines is unshipped and untouched" 'echo "$out" | grep -q "^branch kept wip unshipped 1" && has_ref "$r" refs/heads/wip'
check "branch made just now is too-new" 'echo "$out" | grep -q "^branch kept fresh too-new" && has_ref "$r" refs/heads/fresh'
check "tag untouched" 'has_ref "$r" refs/tags/v1.0'
check "golden local name kept" 'echo "$out" | grep -q "^branch kept main golden"'

repo; old_branch "$r" caught; commit_on "$r" caught c.txt
GIT_COMMITTER_DATE="$OLD" git -C "$r" cherry-pick -x caught >/dev/null; commit_on "$r" main other.txt; git -C "$r" reset -q --hard
git -C "$r" worktree add -q "$SCAN/wt-merge-$n" caught; GIT_COMMITTER_DATE="$OLD" git -C "$SCAN/wt-merge-$n" merge -q --no-edit -X theirs main
git -C "$r" worktree remove "$SCAN/wt-merge-$n"
out=$(sweep -C "$r")
check "a catch-up merge commit does not count as unshipped" 'echo "$out" | grep -q "^branch would-archive caught "'
# the per-commit re-check (git cherry <golden> <sha>) fails: fail closed
REALGIT=$(command -v git)
printf '#!/bin/sh\ncase "$3 $5" in "cherry "????????????????????????????????????????) exit 1 ;; esac\nexec %s "$@"\n' "$REALGIT" > "$SCAN/git-failing"
chmod +x "$SCAN/git-failing"; ln -sf "$SCAN/git-failing" "$NOAH/git"
out=$(sweep -C "$r")
ln -sf "$REALGIT" "$NOAH/git"
check "a failed per-commit re-check counts the commit as unshipped" 'echo "$out" | grep -q "^branch kept caught unshipped 1"'

repo; old_branch "$r" chan; old_branch "$r" side; git -C "$r" config --add ah.keepBranch chan
git -C "$r" checkout -q side
out=$(sweep -C "$r" --commit)
check "keep-listed branch kept" 'echo "$out" | grep -q "^branch kept chan keep-listed" && has_ref "$r" refs/heads/chan'
check "main-checkout branch kept" 'echo "$out" | grep -q "^branch kept side main-checkout" && has_ref "$r" refs/heads/side'

# ---- work trees (ah absent: every checked-out branch is live, nothing is removed)
repo; old_branch "$r" done1; old_branch "$r" dirty1
git -C "$r" worktree add -q "$SCAN/wt-clean-$n" done1
git -C "$r" worktree add -q "$SCAN/wt-dirty-$n" dirty1; echo mine > "$SCAN/wt-dirty-$n/notes"
out=$(sweep -C "$r" --commit)
check "ah absent: U=0 branch in a clean work tree kept checked-out" 'echo "$out" | grep -q "^branch kept done1 checked-out" && has_ref "$r" refs/heads/done1 && [ -d "$SCAN/wt-clean-$n" ]'
check "dirty work tree kept dirty" 'echo "$out" | grep -q "^worktree kept .*/wt-dirty-$n dirty" && [ -f "$SCAN/wt-dirty-$n/notes" ]'
git -C "$r" config status.showUntrackedFiles no
out=$(sweep -C "$r")
check "untracked file reads dirty even when the repo hides untracked files" 'echo "$out" | grep -q "^worktree kept .*/wt-dirty-$n dirty"'
repo trunk
check "golden unresolved: scratch step skipped" 'sweep -C "$r" | grep -q "^scratch kept golden-unresolved"'

# ---- work trees with ah's stream listing (D14) and the HEAD guard (B1)
AHBIN="$SCAN/ahbin"; mkdir -p "$AHBIN"
for f in "$NOAH"/*; do ln -sf "$(readlink "$f")" "$AHBIN/${f##*/}"; done
printf '#!/bin/sh\ncase "$2" in stream-list) [ -f "$FAKE_LISTING" ] && cat "$FAKE_LISTING"; exit "${FAKE_LISTING_RC:-0}" ;;\n  teams) d=$5; [ "$3" = --worktrees ] || exit 9; [ -f "$d/.claude/ah2/fake-teams-fail" ] && exit 1; f="$d/.claude/ah2/fake-teams"; [ -f "$f" ] || f="$FAKE_TEAMS"; if [ -f "$f" ]; then cat "$f"; else printf "{\\n  \\"teams\\": []\\n}\\n"; fi; exit "${FAKE_TEAMS_RC:-0}" ;; esac\nexit 0\n' > "$AHBIN/ah"; chmod +x "$AHBIN/ah"
sweep_ah() { PATH="$AHBIN" AH_BRANCH_SWEEP_TMP="$SCAN/tmp-$n" FAKE_LISTING="$SCAN/listing-$n" FAKE_TEAMS="$SCAN/teams-$n" sh "$SWEEP" "$@"; }

repo; old_branch "$r" gone1; git -C "$r" worktree add -q "$SCAN/wt-gone1-$n" gone1
git -C "$r" worktree add -q --detach "$SCAN/wt-det-$n"
: > "$SCAN/listing-$n"
out=$(sweep_ah -C "$r" --commit)
check "listed: shipped branch in a clean work tree is removed and archived" 'echo "$out" | grep -q "^worktree removed .*/wt-gone1-$n" && echo "$out" | grep -q "^branch archived gone1 " && [ ! -d "$SCAN/wt-gone1-$n" ] && ! has_ref "$r" refs/heads/gone1'
check "listed: clean detached work tree at golden is removed" 'echo "$out" | grep -q "^worktree removed .*/wt-det-$n" && [ ! -d "$SCAN/wt-det-$n" ]'

repo; old_branch "$r" livebr; old_branch "$r" livewt
git -C "$r" worktree add -q "$SCAN/wt-live-$n" livewt
printf 'livebr\t-\nlivewt\t%s\n' "$SCAN/wt-live-$n" > "$SCAN/listing-$n"
out=$(sweep_ah -C "$r" --commit)
check "listed: a live stream branch checked out nowhere is kept live-stream" 'echo "$out" | grep -q "^branch kept livebr live-stream" && has_ref "$r" refs/heads/livebr'
check "listed: a live stream work tree is kept" 'echo "$out" | grep -q "^branch kept livewt live-stream" && [ -d "$SCAN/wt-live-$n" ]'

repo; mkdir -p "$SCAN/sym-a-$n"; git -C "$r" worktree add -q --detach "$SCAN/sym-a-$n/wt"
mv "$SCAN/sym-a-$n" "$SCAN/sym-b-$n"; ln -s "$SCAN/sym-b-$n" "$SCAN/sym-a-$n"
printf 'other\t%s\n' "$(cd "$SCAN/sym-b-$n/wt" && pwd -P)" > "$SCAN/listing-$n"
out=$(sweep_ah -C "$r" --commit)
check "listed: a live work tree git spells through a symlink is kept" 'echo "$out" | grep -q "^worktree kept .*/sym-a-$n/wt live-stream" && [ -d "$SCAN/sym-b-$n/wt" ]'

# ---- a work tree some process stands in (b35)
repo; old_branch "$r" held; old_branch "$r" nested; old_branch "$r" heldsib
git -C "$r" worktree add -q "$SCAN/wt-held-$n" held
git -C "$r" worktree add -q "$SCAN/wt-nest-$n" nested; mkdir "$SCAN/wt-nest-$n/below"
git -C "$r" worktree add -q "$SCAN/wt-held-$n-sib" heldsib
: > "$SCAN/listing-$n"
(cd "$SCAN/wt-held-$n" && exec sleep 60) & HOLD1=$!
(cd "$SCAN/wt-nest-$n/below" && exec sleep 60) & HOLD2=$!
sleep 1
preview=$(sweep_ah -C "$r")
out=$(sweep_ah -C "$r" --commit)
check "a work tree that is a process's cwd is kept and names the pid" 'echo "$out" | grep -q "^worktree kept .*/wt-held-$n cwd-in-use $HOLD1\$" && echo "$out" | grep -q "^branch kept held checked-out" && [ -d "$SCAN/wt-held-$n" ] && has_ref "$r" refs/heads/held'
check "a cwd below a work tree keeps it too" 'echo "$out" | grep -q "^worktree kept .*/wt-nest-$n cwd-in-use $HOLD2\$" && [ -d "$SCAN/wt-nest-$n/below" ]'
check "the listing says the same as --commit" 'echo "$preview" | grep -q "^worktree kept .*/wt-held-$n cwd-in-use $HOLD1\$"'
check "negative: a sibling whose name extends the held tree's is still removed" 'echo "$out" | grep -q "^worktree removed .*/wt-held-$n-sib\$" && [ ! -d "$SCAN/wt-held-$n-sib" ]'
ah_signal_child TERM "$HOLD1"; ah_signal_child TERM "$HOLD2"; wait "$HOLD1" "$HOLD2" 2>/dev/null
out=$(sweep_ah -C "$r" --commit)
check "negative: once the process has gone the tree is removed" 'echo "$out" | grep -q "^worktree removed .*/wt-held-$n\$" && [ ! -d "$SCAN/wt-held-$n" ]'

if [ ! -e /proc/self/cwd ]; then
  repo; old_branch "$r" nolist; git -C "$r" worktree add -q "$SCAN/wt-nolist-$n" nolist
  : > "$SCAN/listing-$n"
  printf '#!/bin/sh\nexit 1\n' > "$AHBIN/lsof"; chmod +x "$AHBIN/lsof"
  out=$(sweep_ah -C "$r" --commit)
  rm -f "$AHBIN/lsof"
  check "processes that cannot be listed keep the tree cwd-unknown" 'echo "$out" | grep -q "^worktree kept .*/wt-nolist-$n cwd-unknown" && [ -d "$SCAN/wt-nolist-$n" ]'
fi

repo; git -C "$r" worktree add -q --detach "$SCAN/wt-stray-$n"
echo s > "$SCAN/wt-stray-$n/s"; git -C "$SCAN/wt-stray-$n" add s; GIT_COMMITTER_DATE="$OLD" git -C "$SCAN/wt-stray-$n" commit -qm stray
: > "$SCAN/listing-$n"
out=$(sweep_ah -C "$r" --commit)
check "guard: detached work tree with a commit on no ref is kept" 'echo "$out" | grep -q "^worktree kept .*/wt-stray-$n unshipped 1" && [ -d "$SCAN/wt-stray-$n" ]'
GIT_COMMITTER_DATE="$OLD" git -C "$r" cherry-pick -x "$(git -C "$SCAN/wt-stray-$n" rev-parse HEAD)" >/dev/null
out=$(sweep_ah -C "$r" --commit)
check "guard: shipped by patch-id but the commit itself is on no ref: kept unreachable-commits" 'echo "$out" | grep -q "^worktree kept .*/wt-stray-$n unreachable-commits 1" && [ -d "$SCAN/wt-stray-$n" ]'

repo; old_branch "$r" paused; git -C "$r" worktree add -q "$SCAN/wt-paused-$n" paused
mkdir -p "$(git -C "$SCAN/wt-paused-$n" rev-parse --path-format=absolute --git-path rebase-merge)"
: > "$SCAN/listing-$n"
out=$(sweep_ah -C "$r" --commit)
check "guard: paused rebase keeps the work tree and its branch" 'echo "$out" | grep -q "^branch kept paused checked-out" && echo "$out" | grep -q "^worktree kept .*/wt-paused-$n op-in-progress rebase" && [ -d "$SCAN/wt-paused-$n" ] && has_ref "$r" refs/heads/paused'

repo; old_branch "$r" fallback; git -C "$r" worktree add -q "$SCAN/wt-fb-$n" fallback
: > "$SCAN/listing-$n"
out=$(FAKE_LISTING_RC=1 sweep_ah -C "$r" --commit)
check "listing fails: ah-absent mode, nothing removed" 'echo "$out" | grep -q "^branch kept fallback checked-out" && [ -d "$SCAN/wt-fb-$n" ]'

# ---- --target: the run's integration branch stands in for golden (worktree-cleanup §3.1)
repo; old_branch "$r" int; commit_on "$r" int i.txt; old_branch "$r" feat int
git -C "$r" worktree add -q "$SCAN/wt-feat-$n" feat
: > "$SCAN/listing-$n"
out=$(sweep_ah -C "$r" --target int)
check "--target: a tree merged into the target but not golden is would-remove" 'echo "$out" | head -1 | grep -qx "golden int" && echo "$out" | grep -q "^worktree would-remove .*/wt-feat-$n\$"'
check "negative: without --target the same tree is kept unshipped" 'sweep_ah -C "$r" | grep -q "^branch kept feat unshipped 1"'
sweep -C "$r" --target nope >/dev/null 2>&1; rc=$?
check "--target that does not resolve exits 3, nothing touched" '[ "$rc" -eq 3 ] && [ -d "$SCAN/wt-feat-$n" ] && has_ref "$r" refs/heads/feat'

# ---- a held member's work tree (team member_worktrees, worktree-cleanup §3.2)
repo; old_branch "$r" mem; old_branch "$r" other; git -C "$r" worktree add -q "$SCAN/wt-mem-$n" mem; git -C "$r" worktree add -q "$SCAN/wt-other-$n" other
: > "$SCAN/listing-$n"
teams_doc() {  # <path>...: a teams document with one row listing each path in member_worktrees
  printf '{\n  "teams": [\n    {\n      "life": "%s",\n      "member_worktrees": [' "${LIFE:-orphaned}"
  sep=; for p in "$@"; do printf '%s\n        "%s"' "$sep" "$p"; sep=,; done
  printf '\n      ]\n    }\n  ]\n}\n'
}
teams_doc "$SCAN/wt-mem-$n" > "$SCAN/teams-$n"
out=$(sweep_ah -C "$r" --commit)
check "a member's recorded work tree is kept live-member; an unlisted one goes" 'echo "$out" | grep -q "^branch kept mem checked-out" && echo "$out" | grep -q "^worktree kept .*/wt-mem-$n live-member" && [ -d "$SCAN/wt-mem-$n" ] && [ ! -d "$SCAN/wt-other-$n" ]'
repo; old_branch "$r" mem2; git -C "$r" worktree add -q "$SCAN/wt-mem2-$n" mem2
: > "$SCAN/listing-$n"
out=$(FAKE_TEAMS_RC=1 sweep_ah -C "$r" --commit)
check "teams listing fails: every tree kept members-unknown" 'echo "$out" | grep -q "^worktree kept .*/wt-mem2-$n members-unknown" && [ -d "$SCAN/wt-mem2-$n" ]'
printf '{\n  "teams": [\n    {\n      "life": "held"\n    }\n  ]\n}\n' > "$SCAN/teams-$n"
out=$(sweep_ah -C "$r" --commit)
check "a team row without member_worktrees (an older ah): every tree kept members-unknown" 'echo "$out" | grep -q "^worktree kept .*/wt-mem2-$n members-unknown" && [ -d "$SCAN/wt-mem2-$n" ]'

# ---- a linked tree's own hierarchy dir is asked too: a held team keeps its trees, a finished (closed) team's are reaped
repo; printf '.claude/\n' > "$r/.gitignore"; git -C "$r" add .gitignore; GIT_COMMITTER_DATE="$OLD" git -C "$r" commit -qm ignore
for b in ops lanemem done donemem; do old_branch "$r" "$b"; git -C "$r" worktree add -q "$SCAN/wt-$b-$n" "$b"; done
mkdir -p "$SCAN/wt-ops-$n/.claude/ah2" "$SCAN/wt-done-$n/.claude/ah2"
teams_doc "$SCAN/wt-lanemem-$n" > "$SCAN/wt-ops-$n/.claude/ah2/fake-teams"
LIFE=closed teams_doc "$SCAN/wt-donemem-$n" > "$SCAN/wt-done-$n/.claude/ah2/fake-teams"
: > "$SCAN/listing-$n"; rm -f "$SCAN/teams-$n"
out=$(sweep_ah -C "$r" --commit)
check "a held team's tree holding its .claude/ah2 is kept live-team, its member's tree live-member" 'echo "$out" | grep -q "^worktree kept .*/wt-ops-$n live-team" && echo "$out" | grep -q "^worktree kept .*/wt-lanemem-$n live-member" && [ -d "$SCAN/wt-ops-$n/.claude/ah2" ] && [ -d "$SCAN/wt-lanemem-$n" ]'
check "negative: a finished team's tree and its member's tree are removed, .claude/ah2 with them" 'echo "$out" | grep -q "^worktree removed .*/wt-done-$n\$" && echo "$out" | grep -q "^worktree removed .*/wt-donemem-$n\$" && [ ! -d "$SCAN/wt-done-$n" ] && [ ! -d "$SCAN/wt-donemem-$n" ]'
repo; for b in broken bystander; do old_branch "$r" "$b"; git -C "$r" worktree add -q "$SCAN/wt-$b-$n" "$b"; done
mkdir -p "$SCAN/wt-broken-$n/.claude/ah2"; : > "$SCAN/wt-broken-$n/.claude/ah2/fake-teams-fail"
: > "$SCAN/listing-$n"; rm -f "$SCAN/teams-$n"
out=$(sweep_ah -C "$r" --commit 2>"$SCAN/err-$n")
check "a linked tree whose teams listing fails is named and keeps every tree members-unknown" 'grep -q "members-unknown .*/wt-broken-$n" "$SCAN/err-$n" && echo "$out" | grep -q "^worktree kept .*/wt-broken-$n members-unknown" && echo "$out" | grep -q "^worktree kept .*/wt-bystander-$n members-unknown" && [ -d "$SCAN/wt-bystander-$n" ]'

# ---- prune
repo
git -C "$r" worktree add -q --detach "$SCAN/wt-locked-$n"; git -C "$r" worktree lock "$SCAN/wt-locked-$n"; rm -rf "$SCAN/wt-locked-$n"
git -C "$r" worktree add -q --detach "$SCAN/wt-gone-$n"; rm -rf "$SCAN/wt-gone-$n"
out=$(sweep -C "$r" --commit)
check "locked entry with its dir missing is kept by prune" 'echo "$out" | grep -q "^prune kept .*/wt-locked-$n locked" && git -C "$r" worktree list | grep -q "wt-locked-$n"'
check "unlocked missing entry is pruned" 'echo "$out" | grep -q "^prune pruned .*/wt-gone-$n" && ! git -C "$r" worktree list | grep -q "wt-gone-$n"'

# ---- probes
repo; T="$SCAN/tmp-$n"; mkdir -p "$T/ahprobe.old" "$T/ahprobe.new" "$T/ahprobe.wt" "$T/other.old"
touch -t 202601010000 "$T/ahprobe.old" "$T/other.old"
git -C "$r" worktree add -q --detach "$T/ahprobe.wt/c"; git -C "$r" worktree lock "$T/ahprobe.wt/c"
find "$T/ahprobe.wt" -exec touch -t 202601010000 {} +
out=$(sweep -C "$r")
check "probe list mode removes nothing" '[ -d "$T/ahprobe.old" ] && echo "$out" | grep -q "^probe would-remove $T/ahprobe.old"'
out=$(sweep -C "$r" --commit)
check "old own probe removed" '[ ! -d "$T/ahprobe.old" ] && echo "$out" | grep -q "^probe removed $T/ahprobe.old"'
check "probe modified just now kept" '[ -d "$T/ahprobe.new" ] && echo "$out" | grep -q "^probe kept $T/ahprobe.new recent"'
check "probe holding a registered work tree kept" '[ -d "$T/ahprobe.wt" ] && echo "$out" | grep -q "^probe kept $T/ahprobe.wt worktree"'
check "non-probe dir untouched" '[ -d "$T/other.old" ]'
mkdir -p "$T/ahprobe.sp ace"; git -C "$r" worktree add -q --detach "$T/ahprobe.sp ace/c"; git -C "$r" worktree lock "$T/ahprobe.sp ace/c"
find "$T/ahprobe.sp ace" -exec touch -t 202601010000 {} +
out=$(sweep -C "$r" --commit)
check "probe holding a registered work tree whose path has a space kept" '[ -d "$T/ahprobe.sp ace/c" ] && echo "$out" | grep -qF "probe kept $T/ahprobe.sp ace worktree"'
mkdir -p "$T/ahprobe.mine"; touch -t 202601010000 "$T/ahprobe.mine"
printf '#!/bin/sh\necho 4242424\n' > "$SCAN/id"; chmod +x "$SCAN/id"; ln -sf "$SCAN/id" "$NOAH/id"
out=$(sweep -C "$r" --commit)
ln -sf "$(PATH=/usr/bin:/bin command -v id)" "$NOAH/id"
check "probe owned by another uid kept" '[ -d "$T/ahprobe.mine" ] && echo "$out" | grep -q "^probe kept $T/ahprobe.mine other-owner"'

# ---- usage
sweep --bogus >/dev/null 2>&1; rc=$?
check "unknown argument is a usage error" '[ "$rc" -eq 2 ]'
check "--help prints the restore lines" 'sweep --help | grep -q "git branch <b> refs/archive/<date>/<b>"'
sweep -C "$SCAN/not-a-repo" >/dev/null 2>&1; rc=$?
check "not a git repository exits 2" '[ "$rc" -eq 2 ]'

echo "branch-sweep: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
