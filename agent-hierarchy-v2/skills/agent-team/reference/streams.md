# streams — agent-team reference

Read this before `stream-open`, `stream-status` or `stream-done`. `<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Contents

- Streams (open, board, wrap up; the unshipped-commits refusal, archiving, the branch sweep)

## Streams

A stream is one parallel line of work: its own branch and worktree and, under
Herdr, its own tab that its peers spawn into. Open one for every piece of work
that will commit on its own branch, even when the team has only one, and for
work that runs beside other work with peers of its own. A hand-made worktree or
a branch switch needs a stated reason. A single consult or a one-off question
(an Ultra-Advisor call, say) is a plain `spawn-ad-hoc`. A parallel effort of
roles only, with no code, is `stream-open <name> --no-worktree`. Every new
parallel workstream gets its own stream, and so its own tab. Do not start
parallel work with a plain `spawn-one`/`spawn-ad-hoc` into an existing tab; that
is only for adding a role to work already under way. A spawn without `--stream`
never goes in your own tab: it joins the tab of a live member of the same role or
opens a `<team>: <role>` tab. `--split` splits your own pane and is the only way to
get that.

Every command is `<ah> roster … --cwd <repo root>`:

1. `stream-open <name> [--branch <b>]` adds the worktree
   `.claude/worktrees/<name>` on branch `<name>` (or `--branch`) off `main`,
   records the stream in the team file and opens its tab. It spawns nothing,
   and re-running it is safe.
2. `spawn-one <role> --stream <name>` or `spawn-ad-hoc <role> --stream <name>`
   per peer. The first lands in the tab's own pane, later ones beside it. The
   output's `stream` carries the `worktree` and `branch`.
   Members of one stream may message each other directly with `ah msg send`:
   being in the stream links them, and you open no link for them.
3. Open each peer's brief with the work-tree line from SKILL.md § Where members
   work, filled with the stream's `worktree` path and `branch`. Push and PR are
   the Orchestrator's, and main is protected.
4. A design-class peer (Architect, Ultra-Advisor) in a stream writes its spec
   inside the stream's worktree, at a path you name in its brief (the repo's
   own spec folder, e.g. `<worktree>/agent-hierarchy-v2/docs/specs/<name>.md`
   here). `.claude/ah2/specs/` does not work from a stream: the main checkout's
   copy is outside the peer's tree, and the worktree's copy is ah control
   state. It cannot commit; you commit the spec on the stream's branch when its
   report arrives, before you dispatch the implementor.
5. `stream-status` is the board (`--plain`: one line per stream): each
   stream's glyph, peers, and who it is waiting on.
6. `stream-done <name>` at wrap-up puts ✓ on the tab. It closes no member pane. It
   refuses `unshipped-commits` when the branch has commits not in golden (by patch-id):
   land the work, or pick `--covered <reason>`, `--merged-into <branch>` or
   `--abandon <reason>` because it is true, never just to get past the refusal. Once
   every member of the stream is dismissed, re-run `stream-done` to remove the clean
   worktree and move the branch to `refs/archive/<date>/<branch>`. Restore one with
   `git branch <b> refs/archive/<d>/<b> && git update-ref -d refs/archive/<d>/<b>`.
   If golden looks wrong (e.g. origin/HEAD names an old default), suggest
   `git remote set-head origin -a` or `git config ah.releaseRef <ref>` to the user.
   After a merge or ship to golden, run `scripts/branch-sweep.sh` (it lists), show the
   list, then run it with `--commit` (it pushes nothing). In a hold summary, report each held stream's
   `git cherry <golden> <branch> | grep -c '^+'` when non-zero; a hold archives nothing.
   `herdr tab close` kills every pane in the tab, so a
   stream's recorded tab is closed only when every pane in it is ah-made (a member's, or
   the stream's root pane) and none runs a harness; an unreadable pane list or process
   table counts as unsafe. `stream-done` reports `tab_closed`, or `tab_kept` with the
   reason (`panes unknown`, `foreign pane`, `harness running`) and `tab_kept_pane`.
   `dismiss --close` closes the tab of a done stream once its last member is gone, and
   `disband --close` closes every stream's tab that passes; both report `tabs_closed` (tab
   ids) and `tabs_kept` (`stream`, `tab`, `reason`, `pane`). `stream-status` shows
   `tab.member_panes`, `tab.closable` (same decision), `tab.kept`, `tab.kept_pane`.
7. Dismiss members when the user asks, or, in an autonomous pipeline run, through the
   tick in `skills/autonomous-pipeline/reference/cadence.md`, with the usual two-phase
   `dismiss`; `stream-done`'s `next` lists the commands.

Peers start in the stream's worktree and are bound to it. Find a brief by the
`[hierarchy-msg <id>]` in the mail line (`ah msg show <id>` resolves it across pools): a request addressed by name
to a member rooted in a worktree may be written in that worktree's own pool (a
note on stderr says so), and a response is written beside its request.

| glyph | the stream |
|---|---|
| ◐ | a member is working |
| ○ | its members are idle, waiting on input: the user, the Orchestrator, or another stream |
| × | a member is blocked at a prompt, or a dead member owes a report: it needs the Orchestrator |
| ✓ | is done |
| · | has no live member, or no status to read |

**Progress records.** Each stream has `progress/<team>/<stream>.md` (your checklist: Done, In progress, Not started,
each item a `- [ ]` / `- [x]` checkbox line, plus `State:` and `Updated:`) and `<stream>.journal.md` (the CLI appends
one entry per report; you never edit it). Work outside a named stream belongs to `main`. After every report, plan
change, addition or deviation, update the stream's checklist and `Updated:`; `stream-status` shows the counts and
`stale` when a report is newer than the checklist by more than 30 minutes. The Stop nudge says once per report when a
checklist is behind. `stream-done` warns when items are still In progress. `docs/progress.md` has the formats and how to
turn the records off.

**Testing a stream.** Before a member reports DONE it builds the whole project, not only the parts it changed (here:
`dotnet build agent-hierarchy-v2/native/Ah.sln`, exit 0), and runs the tests its brief names. The full and mod suites
are slow: you run them once, on the integration branch, after merging a batch of streams and before release, not in each
stream.

What you keep current, and how:

- You own `rollup.md` and every `<stream>.md`; edit them yourself. The CLI writes only the `.journal.md` files.
- On every report, plan change, addition or deviation, update the stream's checklist and its `Updated:` time.
- Put each Done item's commit sha, spec or issue after it, then `(done <ISO UTC time>)`.
- Record decisions in the rollup with their reason, and deviations too.
- When the user rules on something, write it as a `Decision:` line that names the user.
- In the autonomous pipeline, each item is a checklist line in its stream; the item's anchor links to it.

What a role that reports does: in the `## [1] status` section of its response, put each decision, deviation, roadblock and
outcome on its own line starting `Decision:`, `Deviation:`, `Roadblock:` or `Outcome:` (`Detail:` for any other detail worth
keeping). `<ah> msg send` copies those lines into the stream's journal. Plain words only; no secrets and no pasted peer text.
`<ah> msg new --type response` puts this rule under `## [1] status`; filling the section replaces it.

Labels update on their own when a peer's turn starts or ends and at a
permission prompt, and `stream-status` corrects them. Never rename a stream
tab by hand: the next update overwrites it.

**tmux or terminal:** no tab and no glyph, only the worktree and the record.
