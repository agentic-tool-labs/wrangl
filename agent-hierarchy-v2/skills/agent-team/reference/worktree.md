# worktree — agent-team reference

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Read this when you spawn a member into a work tree, a member reports BLOCKED on a path, or an item ships or is abandoned.

Contents

- Work trees and spawning
- Where a member works
- Scratch directories
- Probes
- A Reviewer's work tree
- Ship cleanup

## Work trees and spawning

- Every member works in the work tree assigned to its work, always. The lead opens it, because a member cannot run
  `git worktree`; the member only works in it and never creates its own. A work tree is a git worktree (open one with
  `stream-open <name>`), or the main checkout only for work with no worktree. Spawn a member into it with
  `spawn-one <role> --stream <name>` or `spawn-ad-hoc <role> --stream <name>`: the member's launch directory is then the
  stream's worktree. Without `--stream`, the launch directory is `--cwd`, which must be the work tree
  you mean. `create --cwd <work tree>` binds the whole team to one work tree; for a stream, spawn each member with
  `--stream <name>`. A role that writes code is refused in the main checkout unless you pass `--allow-main`.
- You, the orchestrator, edit, commit, merge and build a team's repository only inside a named work tree, with absolute
  paths or `git -C <work tree>`; your own session directory need not be that tree. Say which tree in every brief.

## Where a member works

A member writes only inside its work tree, its own tmp directory, and what its brief names (its response file; for
Architect and Ultra-Advisor, the specs directory). That is an instruction in the brief; ah has no write gate and refuses
no write.

There are no grants or gate modes. `<ah> roster allow`, `revoke` and `gate` were removed: they print a notice, exit 0 and
change nothing, so do not run them. A member that needs another directory reports BLOCKED naming the path and why. To let
a Claude member use the directory, send a new brief that names the path as an exception. A new brief does not reach a Codex
member, whose writable directories are fixed at launch: have the work done in its own tree, or give the path at launch.

## Scratch directories

Scratch and verification output go in `$TMPDIR` (the member's own tmp, the real directory `<ah home>/t/<key>`, outside
every checkout). Probes use
`mktemp -d /tmp/ahprobe.XXXXXX` (see Probes below). A verified `dismiss` close removes the member's tmp and drops its
work-tree record; `disband` does that for every member it closes.

## Probes

Throwaway probes and test homes go in `mktemp -d /tmp/ahprobe.XXXXXX`. A Codex member is launched without `/tmp` write access, so it cannot make those directories: QA runs the tests that need such
homes, and the member reports BLOCKED naming the test.

Setting `HOME` alone does not move a probe. Outside test mode (`AH_TEST` on a test home, as `tests/lib-test-env.sh` sets up) `ahd` and `ah` take the home from the passwd entry, so `HOME=<probe> ahd stop` stops the real daemon. Never run them against a probe home without test mode.

Only the real daemon runs in the real home. To test on real data, copy the home under the probe root, leaving out `tmp/` (it
holds 67 GB of scratch) and, unless you are testing them, `run/ui-service.json`: a copied record of a
page the daemon ended makes the test daemon open a listener and skip its idle exit. A test-mode daemon refuses to start while the copy's registrations, known-dirs or database name a path
outside the root, so rewrite or strip those paths; copy the repo's `.claude/ah2` under the root too if you need its teams to
appear. The check runs at start only: a path added after that is not caught. A test-mode daemon's tmux and herdr sockets are inside
the root, so a pane id in the copy cannot reach a real pane from it.

## A Reviewer's work tree

A Reviewer builds and tests in its own clone of the commit you name in its brief; name the branch and sha, and have the implementor commit first.

## Ship cleanup

`ah/ship/<slug>` and its scratch work tree are local and run-created, and are never pushed. Remove both in the turn the item
ships or is abandoned:

1. `git worktree remove <scratch tree>`, never `--force`. If git refuses (dirty or locked), leave the tree, say so in the ship
   line, and let the sweep (cadence Tidy C) decide later.
2. Shipped (R now contains M): check `git merge-base --is-ancestor ah/ship/<slug> <R>`, then
   `git update-ref -d refs/heads/ah/ship/<slug> <M>`. If the check fails, leave the branch.
3. Abandoned: archive it, then delete it: `git update-ref refs/archive/<YYYY-MM-DD>/ah/ship/<slug> <M>`, then
   `git update-ref -d refs/heads/ah/ship/<slug> <M>`. The restore recipe is the one in `scripts/branch-sweep.sh`.

The member's own work tree and its branch B are not removed here. They belong to the member until it is dismissed (Tidy B), and
then the sweep removes them once B is in R.
