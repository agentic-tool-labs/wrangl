# cadence — the tick

Read this before every tick (SKILL.md § Cadence). `<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins). Every command is
`<ah> roster … --cwd <main checkout>`, as in the skill. Read every JSON
output whole.

Contents
- Trigger and stamp; Tidy A: mark finished streams done
- Tidy B: retire idle members (signed off, the checks, the close)
- Plan complete: the final tick; Parallel; The summary line

## Trigger and stamp

- **When:** after you finish handling a report, and at every `check-in-due` or
  `peer-silent` wake, whenever `progress/<team>/rollup.md` has no `Tick: <ISO UTC>`
  line or that line is 10 minutes old or more. Also once, unconditionally, right before
  the run's final message (plan runs: § Completion gate; issue runs: end of run). That
  one is the **final tick**.
- **No new timer and no `ScheduleWakeup`.** Between reports nothing changes.
- **The stamp:** after the tick, rewrite (do not append) the rollup's `Tick:` line with
  the current UTC time. Never infer the last tick time from memory: a compacted session
  re-reads the stamp.
- **Progress records off:** there is no rollup, so tick after every report and wake.
- The tick runs only while the run anchor is open.

## Tidy A: mark finished streams done

1. Run `stream-status` (JSON) once. That snapshot is the tick's **before** list.
2. A stream whose `state` is not `done` is **finished** only when all hold:
   - **(a) No open work:** `exchanges` and `waiting_on` are empty.
   - **(b) Checklist complete:** `progress` shows In progress = 0 and Not started = 0
     (skipped when progress records are off; (c) carries the weight).
   - **(c) Merged:** for a stream with a branch, `git -C <main checkout> merge-base
     --is-ancestor <branch> <target>` exits 0 for at least one merge target, **and**
     `git rev-list --count <base>..<branch>` is above 0 (a stream that never committed
     is not merged). Merge targets: the anchor's `branch:` (plan runs),
     `origin/<default branch>`, and any local or `origin/` branch whose name contains
     `release`. Run `git fetch --prune origin` once per tick first; if it fails, use the
     local refs and say `fetch failed` in the summary. In an issue run, an item whose run
     record says its PR merged also satisfies (c) (squash merges defeat ancestry). A
     `--no-worktree` stream satisfies (c) when every peer `status` is `idle`, `done` or
     `gone`.
3. Run `stream-done <name>` for each finished stream. A warning about In progress items
   means (b) was misread: report it in the summary. `stream-done` closes no **member**
   pane. It closes the stream's tab (`tab_closed`) only when every pane in it is ah-made (a
   member's or the stream's root pane) and none runs a harness; else it reports `tab_kept`
   with `panes unknown`, `foreign pane` or `harness running`, and `tab_kept_pane`. A merged
   stream whose members are still live therefore stays open until they are dismissed. Refusals, archiving, the sweep: `skills/agent-team/reference/streams.md` § Streams 6.
4. A stream that fails any test stays open, and so do its members.

## Tidy B: retire idle members

**Candidates:** every peer of a stream whose `state` is `done` with `status` not `gone`;
and every member of the run's team in no stream whose `status` is `idle` and that has
nothing queued for it or its role (no not-started or rework item routed to it, not the
reviewer of an item in implementation, not the next item's pipelined Implementor). A
retired non-stream role is respawned later through § Elastic membership if work appears.

**Never a candidate:** the Orchestrator; any member with an open exchange (it appears in
a stream's `exchanges`, or `<ah> msg list --team <team>` shows an open request to it or
from it); any member whose `status` is `working` or `blocked`.

**No wait.** The user: "If an agent has completed its work and it is signed off on and
committed, we should dismiss those agents. New work should get freshly spawned agents, not
reuse idle agents on new work." A candidate is closed as soon as its work is signed off
(review passed or the Orchestrator accepted it) and committed, and it is idle, on top of
every check below. The hook says `ah: <member> is idle and
clean — dismiss it if its work is signed off` once per idle spell when a member passes the release check.
Idle means `idle_since[<member>]` in the member's team row of `<ah> roster teams --json`
is set: the activity record's `state_since` while the member's state is idle, and `null`
otherwise (working, blocked, no record, an unreadable record, or one with no
`state_since`). A `null` or missing value keeps the member; say so in the summary.

| situation | outcome |
|---|---|
| signed off, committed, idle, every other check passes | a candidate |
| signed off, uncommitted work | kept |
| idle, review still open or rework due | kept |
| `idle_since` null or missing | kept |
| member working, whatever its earlier idle time | kept (`idle_since` is null) |

For each candidate, in order:

1. **Plan.** Run `dismiss <name> --plan` (bare `dismiss <name>` closes a clean member at once). Read `release.status`.
2. **Act on the status:**

   | status | action this tick | counts as |
   |---|---|---|
   | `busy` | Keep it: it is working now. | kept |
   | any other | Close (step 4). Give each entry of `release.warnings` in the summary: it names unshipped commits by branch and sha, uncommitted work or an open request. | — |
3. **No merge check.** The user: "You should be able to close down idle members even if their
   release hasn't shipped. That gate should just be a nudge not a block." Closing a pane does
   not touch the work tree or its branch.
4. **Close.** Run `dismiss <name> --close` (one call). The harness
   permission prompt fires unless the user opted into the lifecycle permission rules (`install.sh
   --allow-lifecycle`), whose `Bash(<ah> roster dismiss:*)` rule allows
   it without a prompt. Either way this tick's own checks are the safeguard
   (the sign-off, the release check, the `dismiss` plan, "never a
   candidate" and never `--ignore-release`). The conversational confirmation in
   `skills/agent-team/reference/teardown.md` step 2 is satisfied by the user's standing
   directive (#146), only for members that passed steps 2 and 3. Classify the result only
   this way: **closed** (exit 0, `closed: true`, `still_live` empty); **held** (exit 3: kept);
   **refused** (any other exit, no JSON, a stale-token refusal, a denied prompt).
   - On the first refusal from a denied permission prompt, stop closing for the rest of
     this tick. The next tick tries again.
   - A stale-token refusal: re-plan that member once in this tick.
5. **Verify against the live list.** The live list is `<ah> roster teams --json` (the team
   row's `members` count and `idle_since` map) together with the harness session list
   (`ListAgents`), not `stream-status`, which omits members in no stream. Take it once
   before the first close (the **before** list) and again after all closes (the **after**
   list). A member counts as closed only if the verb said closed **and** it is absent from both
   the team row and `ListAgents`; if those two disagree, it is not closed: report it. The verb said closed but it is still present: report
   `still-live <name>`. Absent but the verb said refused: closed-by-other. Every number in
   the summary comes from comparing the before and after lists.

Never pass `--ignore-release`: it closes a member that is working, and that call is the user's.

## Tidy C: sweep work trees

Due after any tick that shipped, dismissed or abandoned something, and whenever a `worktrees-piling-up` alarm arrives. Run `<plugin root>/scripts/branch-sweep.sh -C <repo> --target <R>`, where `<plugin root>` is the directory that holds `bin/ah` and
R is the run's integration branch (the stream's `base`; with no streams, the branch you ship onto). The default run is the plan.
When it prints any `would-remove`, `would-archive` or `would-prune` line, run it again with `--commit`. There is no prompt: the
script keeps every tree that is dirty, unmerged or in use; every tree a live or held team uses (its Orchestrator's or a
member's tree, or the tree its hierarchy dir lives in); and branches younger than 72 h. It never forces. A removed tree takes its
ignored files with it, `.claude/ah2` included: a finished team's files are reaped with its tree. Put the totals
line in the tick's log. Do not act on `kept` lines. Run it once per qualifying tick (Tidy step), not on every tick.

## Plan complete: the final tick

The user: "The exception to the rule is if all work has completed entirely. Plan complete.
That can be a trigger to close all teams who have completed."

It is plan complete only at the final tick, and only when all hold:
- **Plan runs:** § Completion gate ran and **passed** in this run; the run is about to send
  its final message and close its anchor. A gate that failed, stopped or did not run is
  not plan complete.
- **Issue runs:** the run is at its end of run and every item's record is finished (PR
  opened or merged). An item stopped, parked, at the round cap or still open is not plan
  complete.
- **Either kind:** every stream's `progress` shows In progress = 0 and Not started = 0
  (skipped when progress records are off); no stream has an open exchange; `<ah> msg decision
  list --team <team> --summary` shows no decision waiting for the user.

If any is missing it is not plan complete and the final tick is an ordinary tick.

When it is plan complete the final tick widens only the scope (below); there is no wait to
skip. Every other check still applies unchanged: part A first; the candidate rules and "never a candidate";
the release check and its status table; never `--ignore-release`; the
two-phase close; stop closing on a denied prompt; the before/after verification.

Scope: every member of the run's team that passes, plus every child team of a done stream
(sub-orchestrator teams), closed through that team's existing close path with the same
checks. The run's own team record is not disbanded; the Orchestrator lives in it.

Whatever does not pass is kept and reported in the final message as well as the `tick:`
line, with its reason (`not-ready`, `unmerged <sha7>`, `held`, `refused (denied)`). There is
no next tick, so the final message says it is still up and why.

## Parallel

Runs after tidy, so freed members count toward the budget. First run `<ah> roster checklist show`, copy its `Queue:` lines into the rollup and act on each (`dispatch <item> to <member>`, `answer <member> …`), or note on the item why not.

1. **Budget.** `live` = members of the run's team that the after list (`<ah> roster teams
   --json` with the harness session list, as in Tidy B step 5) shows live, Orchestrator
   excluded and members in no stream included. `budget` = the anchor's `session-budget:` level
   (written per `reference/run-anchor.md`), mapped low 8 / med 12 / high 16 / xhigh 20 / max
   uncapped; a missing line means med. `live` at or above `budget`: no growth; say
   `parallel: at budget`. At `max` the budget sets no growth limit, and the one new stream per
   tick in step 3 still applies, as do all the eligibility rules; the summary shows `live <n>/max`.
2. **Eligible work:** not started (rework is never eligible: it belongs to the item's own
   implementer); dependencies done; no live member assigned; **files disjoint** from every
   in-flight item's "files to touch". An item with no known file list is eligible for
   design only (an Architect writes its spec); a second implement step never runs on
   unknown files.
3. **Growth limit:** one new stream per tick, and only while `live` is below `budget`.
4. **Start it as a stream:** `stream-open <item slug> --base <base>` (the anchor's `branch:`
   in a plan run, the item base in an issue run), then `spawn-one`/`spawn-ad-hoc <role>
   --stream <item slug>` per peer, then brief it as `skills/agent-team/reference/streams.md`
   says. Never start parallel work with a plain `spawn-one` into an existing tab.
5. **Where it lands.** Issue runs: the stream's branch is the item's branch. Plan runs: after
   the item passes review, the Orchestrator merges the stream's branch into the anchor branch
   with `git merge --no-ff`; that commit falls under the whole push regime
   (`reference/push-regime.md`). A merge conflict stops that item and notifies; never
   resolve it silently.
6. The pipelining rule decides first: the Implementor that finishes N gets N+1 **when N+1
   depends on N or changes N's files**; otherwise N+1 gets a fresh spawn. Parallel is only
   for work no live member can take. Never invent work.

## The summary line

One line, plain text in the session, never through `PushNotification`. Sent every tick.
Fields that are zero are left out, except `live`:

```
tick 14:32Z — done: feat-a, feat-b · closed 3 (x, y, z) · asked 2 · kept: m (not-ready: "WIP parser"), n (unmerged 1a2b3c4) · refused 1 (denied) · parallel: opened feat-c (#152) · live 7/12
```

`closed` is the verified count from step 5; `still-live: <names>` shows any the verb called
closed that are still up; a no-op tick is `tick 14:32Z — nothing to do · live 7/12`. When the
line also answers a mid-run status question, end it with `<ah> msg decision list --team <team> --summary`'s `line`.
