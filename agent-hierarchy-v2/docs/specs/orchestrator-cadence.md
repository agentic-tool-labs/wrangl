# Orchestrator cadence during autonomous pipeline runs: tidy, parallel and tabs (#146), rev 4

Implementer: implementor
Reviewer: reviewer

Status: **rev 4.4**, 2026-10-17. Rev 4.4 adds the named session budget (#158): low 8 / med 12 (default) / high 16 / xhigh 20 / max uncapped. It ships in 2.5.0-pre.1 with this directive, as decided by the Orchestrator. Earlier: **rev 4.3** (see the change log). Rev 4.3 makes three edits only:
- §3.1 now quotes the shipped § Cadence text;
- §4.2's pipelining guards move into "Never a candidate";
- §4.2 step 5 gains the closed rule (absent from both the team row and `ListAgents`).

This copy includes rev 4.2 (the Orchestrator's edits on the branch, 9847efec), merged in, so it can replace the branch copy. Design only. Issue #146, with daemon load from #143.
Paths are relative to `agent-hierarchy-v2/`.
- **The base (rev 4, decided):** the 2.5.0-pre.1 tip `6bbfe531`, on branch `ah/orchestrator-cadence`. Not `ah/followups-241`.
- **The canonical copy of this spec** is this file, `.claude/ah2/specs/orchestrator-cadence.md` in the main checkout. Copy it over `docs/specs/orchestrator-cadence.md` on `ah/orchestrator-cadence`, which holds rev 3.
- The main checkout's `ah/v2` is older (2.3.0); do not build against it.

## 1. Goal

The user's words: "Let's make both of those things something the top level orchestrator does on
a regular cadence: 1. Checks for workstreams that are completed and determine if the teammembers
can be shut down. 2. Check if more work can be done in parallel. These should be on a regular
cadence during any autonomous pipeline. Also, creating new workstreams needs to be preferred to
go to a new tab." Then: "it needs to become a top level directive for the autonomous pipeline."

The evidence from 2026-10-17:
- 29 members sat idle on old hooks for hours after their streams merged. Their hook traffic fed
  the daemon churn in #143.
- 20 finished streams were never marked done.
- Release status of the idle members: most `not-asked`, two `not-ready` with real uncommitted
  work, two `stale`.
- An Orchestrator loop counted refused closes as closed.

What this spec delivers:
1. A **tick**: a fixed procedure the Orchestrator runs on a cadence during every pipeline run,
   plan and issue alike. It has three parts:
   - **tidy**: retire finished streams and close members whose work is safely released;
   - **parallel**: start queued work that can run alongside, within a session budget;
   - **summary**: one line to the user.
2. **New tab per workstream**: every new parallel workstream is opened with `stream-open`, so it
   gets its own herdr tab. No CLI change is needed (§5).

Everything uses existing verbs and events. There is no new CLI verb, no new daemon event and no
new config key. A read-only `ah roster tidy` verb was considered and rejected for now (§10, Q1).

## 2. Facts this design rests on (verified by reading)

| fact | source |
|---|---|
| `dismiss --close` runs the release check itself. A held member is not closed, and the result carries `held` and exits 3. `--ignore-release` overrides it. A plan (no `--close`) prints `release` and exits 0. | `docs/cli-tools.md:260`, `:493-519` |
| Release statuses: `ready` and `not-running` close. `not-running-dirty`, `not-ready`, `unanswered`, `not-asked` and `stale` do not. The release check asks with `ah msg new --to <role> --to-name <member> --slug release`. A stale answer is never re-asked automatically. The released sha "need not be pushed". | `docs/cli-tools.md:495-519` |
| `closed: true` means verified gone on a fresh re-query. Success is reported only when `closed: true` and `still_live` is empty. | `docs/cli-tools.md:248`, `skills/agent-team/reference/teardown.md:72-80`, `:221-223` |
| `dismiss --close` sits behind the always-ask harness permission gate. The skill also requires a conversational confirmation first. | `skills/agent-team/reference/teardown.md:207-209`, `:49-51` |
| `stream-open` adds the worktree and branch, records the stream, and under herdr opens its own tab without focus. `spawn-one/spawn-ad-hoc --stream` lands in that tab, and "a stream with no live tab gets one first". | `docs/cli-tools.md:254`, `:256`; `skills/agent-team/reference/streams.md:16-22` |
| `stream-status` reports each stream's `state`, `branch`, `base`, `worktree`, `peers[].status` (`working/idle/blocked/done/unknown/gone`), open `exchanges`, `waiting_on` and `progress` counts. | `docs/cli-tools.md:257` |
| `stream-done` marks the stream done and labels its tab ✓. It closes no member pane and removes no worktree or branch; it closes the tab only when every pane in it is one ah made and none runs a harness. | `docs/cli-tools.md:258` |
| Today `streams.md:30` says "Dismiss members only when the user asks". SKILL.md § Elastic membership says never to dismiss or close a member mid-task. | as cited |
| `check-in-due` and `peer-silent` events wake the Orchestrator through the Stop hook. They exist only while dispatches are in flight. | `native/src/Ah.Hooks/Lifecycle/StopOrchestratorLiveness.cs:191`, `native/src/Ah.Watch/DispatchClock.cs:109` |
| The Orchestrator owns `progress/<team>/rollup.md` and edits it itself. | `skills/agent-team/reference/streams.md:55` |
| **SKILL.md at 6bbfe531 (rev 4):** `skills/autonomous-pipeline/SKILL.md` is **433 lines / 23,807 bytes**, under its test ceiling of 500 lines / 30,720 bytes (`tests/test-skill-content-defects.sh:52-53`). | gopher `git show 6bbfe531` |
| **Its headings, by line:** `## Liveness` 26, `## Bootstrap` 50, `### Elastic membership` 129, `## Routing` 145, `### The pipelining rule` 187, `## Decisions on the user's behalf` 201, `### Parked questions` 234, `## Ultra-Advisor escalation — three hops, no direct channel` 246, `## The 3-round cap — per item, counted by slug` 257, `## Completion gate` 275, `## Push regime` 295, `### The run anchor, and the halt` 311, `## Notification` 336, `## Invocation` 393, `## Rules that hold everywhere` 421. | gopher `git show 6bbfe531` |
| **Its reference files:** `decisions.md`, `issue-runs.md`, `model-secret-scan.md`, `push-regime.md`, `run-anchor.md`. f85157a1 moved the push regime and the anchor text out of SKILL.md. `tests/test-skill-content-defects.sh:57` requires every reference file to be named in SKILL.md; the read-before list is at SKILL.md:17-24. | gopher `git show 6bbfe531` |
| `reference/run-anchor.md` at 6bbfe531 has `## Writing the anchor` (21), `## Finding it again` (48) and `## Before writing a new one` (68). Line 44 places the anchor's close "after § Completion gate" in a plan run and at the end of run in an issue run. | gopher |
| The plan run's end, at SKILL.md:288-291: after § Completion gate it sends its final message, then closes its anchor. issue-runs.md:391: the whole-run Completion gate is not run for issue runs. | gopher |
| **Idle-since:** the activity/status record's `state_since`, an ISO instant. HierLib.Status.cs:386-390 stamps it when the session's activity state changes and carries it forward otherwise (5b486857). The view reads it through `ViewSource.ActivityOf` (Ah.Watch/ViewSource.cs:110-121). Records written before 5b486857 have no `state_since`. | gopher `git show 6bbfe531` |
| `agents/orchestrator.md` is 1,823 bytes against a 1,700-byte ceiling (`tests/test-peer-progress.sh:97`). | gopher `wc` |

## 3. Where the directive lives

- **The directive goes in `skills/autonomous-pipeline/SKILL.md`** as a new top-level section,
  `## Cadence`, placed directly after `## Liveness` (and so before `## Bootstrap`). That is "the
  autonomous-pipeline rules the Orchestrator always carries" during a run.
- **The procedure goes in a new `skills/autonomous-pipeline/reference/cadence.md`.** SKILL.md
  names it in its read-before list (SKILL.md:17-24): "`reference/cadence.md` before every tick".
- **Not `agents/orchestrator.md`.** It is already over its 1,700-byte ceiling, and the user scoped
  this to pipeline runs.

### 3.1 The SKILL.md section, as shipped (rev 4.3: matches `ah/orchestrator-cadence` 9847efec, SKILL.md:51-71)

This is the shipped text. Later edits may reflow it, but must not drop any clause.

```
## Cadence

Every run, plan and issues alike, runs a **tick**. Run one after you handle a
report, and at every `check-in-due` or `peer-silent` wake, whenever the rollup's
`Tick:` line is missing or at least 10 minutes old, and once more right before
the final message. Read `reference/cadence.md` first. A tick does three things
in order:

1. **Tidy.** Mark finished streams done. Close an idle member of a done stream,
   or an idle member outside any stream with nothing queued for it, only once
   its work has been done over 1 hour and it has been idle over 1.5 hours (the
   final tick of a completed run skips that wait), after the release check,
   through the two-phase `dismiss`. Never close a member with uncommitted or
   unmerged work, and never pass `--ignore-release`. Count a member closed only
   when the live list no longer shows it.
2. **Parallel.** Start queued work that has no live member and touches no file
   in flight, as a new stream (`stream-open`, so its own tab), within the
   anchor's `session-budget`.
3. **Summary.** One `tick:` line to the user in the session (not a push
   notification).
```

"A completed run" in the shipped text means §4.2a's plan complete. "The live list" means the
team row together with `ListAgents` (§4.2 step 5, rev 4.3).

### 3.2 Size (rev 4: no moves)

- At 6bbfe531, SKILL.md is 433 lines / 23,807 bytes. With § Cadence (≤ 14 lines, ≤ 1,300
  bytes) and the §3.3 sentences, it stays under 460 lines and 26,000 bytes.
- **Move no text out of SKILL.md in this change.** Rev 2's moves (the Ultra-Advisor escalation,
  the gitleaks paragraph) are dropped.
- The size test is green before and after. Do not raise the ceiling.

### 3.3 Edits elsewhere

| file | change |
|---|---|
| `skills/autonomous-pipeline/SKILL.md` § Elastic membership (heading at 129; the mid-task sentence is at about 137) | Keep the existing "dismiss or close a member mid-task" sentence word for word, whatever its exact text at 6bbfe531. Append: "A member of a done stream with no open request is not mid-task; the tick retires it (§ Cadence)." |
| `skills/autonomous-pipeline/SKILL.md` § Notification (336) | Add one sentence after its no-running-commentary sentence: "The tick's `tick:` line is a session message, not a notification." If that sentence is absent at 6bbfe531, add the new sentence at the end of the section's prose, before the list. The push-notification list does not change. |
| `skills/autonomous-pipeline/reference/run-anchor.md` § Writing the anchor (21) | **`session-budget:` lives here (rev 4; value form rev 4.4, #158).** The anchor's constraints block always gets the line `session-budget: <level> (<N>)`, written at bootstrap step 6, which already reads this file. Examples: `session-budget: med (12)`, `session-budget: max (uncapped)`. The levels are **low = 8, med = 12 (default), high = 16, xhigh = 20, max = uncapped**. When reading it, the **level word decides**, and the number in parentheses is only for people to read. An anchor without the line, from a run started before rev 4.4, is read as `med (12)`. SKILL.md § The run anchor, and the halt (311) is unchanged. |
| `skills/autonomous-pipeline/SKILL.md` § Invocation (393) (rev 4.4, #158) | Add the option. **Flag:** `/pipeline … --budget low|med|high|xhigh|max`. **Plain language** is mapped to the same five words, for example "budget high", "high session budget", "use up to 16 sessions" (the nearest level at or below the number: 16 → high, 10 → low), "no session cap" or "unlimited sessions" (→ max). Anything else → ask once, with the five levels. No flag and no phrase → `med`. An unknown flag value → refuse at bootstrap and list the five. |
| `skills/autonomous-pipeline/SKILL.md` § Notification, the run-start notification (rev 4.4) | The run-start notification line adds `budget <level> (<N>)`, for example `budget med (12)` or `budget max (uncapped)`. That is one field, with no new notification. |
| `skills/agent-team/reference/streams.md:30-31` | Replace with: "Dismiss members when the user asks, or, in an autonomous pipeline run, through the tick in `skills/autonomous-pipeline/reference/cadence.md`, with the usual two-phase `dismiss`; `stream-done`'s `next` lists the commands." |
| `skills/agent-team/reference/streams.md` §Streams, first paragraph | Add: "Every new parallel workstream gets its own stream, and so its own tab. Do not start parallel work with a plain `spawn-one`/`spawn-ad-hoc` into an existing tab; that is only for adding a role to work already under way." |
| `skills/autonomous-pipeline/reference/issue-runs.md` end-of-run "Members not closed" (about :613-617 on 2.4.3; locate it at 6bbfe531) | Add one sentence pointing at the tick for the mechanics: "The final tick (`reference/cadence.md`) does this." Keep "Never pass `--ignore-release`". |

## 4. `reference/cadence.md`: the tick procedure (normative)

Every command is `<ah root>/bin/ah roster … --cwd <main checkout>`, as in the skill. Read
every JSON output whole.

### 4.0 Trigger and stamp

- **When it runs:** after you finish handling a report, and at every `check-in-due` or
  `peer-silent` wake, whenever `progress/<team>/rollup.md` has no `Tick: <ISO UTC>` line or that
  line is ≥ 10 minutes old. Also once, unconditionally, right before the run's final message
  (plan runs: § Completion gate; issue runs: end of run). That one is the **final tick** (§4.2a).
- **No new timer and no `ScheduleWakeup`.** Between reports nothing changes, so a run with no
  reports and no dispatches in flight has nothing to tick for.
- **The stamp:** after the tick, rewrite (do not append) the rollup's `Tick:` line with the
  current UTC time.
- **When progress records are off:** there is no rollup, so tick after every report and at
  every wake, with no rate limit.
- **Compaction:** the stamp is on disk, so a compacted session re-reads it. Never infer the last
  tick time from memory.

### 4.1 Tidy, part A: mark finished streams done

1. Run `stream-status` (JSON) once. That snapshot is the tick's **before** list.
2. A stream with `state` other than `done` is **finished** only when all of the following hold:
   - **(a) No open work:** `exchanges` is empty and `waiting_on` is empty.
   - **(b) Checklist complete:** `progress` shows In progress = 0 and Not started = 0. With
     progress off, (b) is skipped and (c) carries the weight.
   - **(c) Merged:** for a stream with a branch, the branch tip is merged into a **merge
     target**. That means `git -C <main checkout> merge-base --is-ancestor <branch> <target>`
     exits 0 for at least one target, **and** the branch has at least one commit not on its
     `base` (`git rev-list --count <base>..<branch>` > 0). The second check stops a stream that
     never committed from reading as merged.

     Merge targets: the anchor's `branch:` (plan runs), `origin/<default branch>`, and any
     local or `origin/` branch whose name contains `release`. Run `git fetch --prune origin`
     once per tick first; if the fetch fails, use the local refs and say so in the summary.
     In an issue run, an item whose run record says its PR merged also satisfies (c) for that
     item's stream, which covers squash merges, where ancestry fails.

     A `--no-worktree` stream (no branch) satisfies (c) when every peer `status` is `idle`,
     `done` or `gone`.
3. For each finished stream, run `stream-done <name>`. A warning about In progress items means
   (b) was misread: report it in the summary.
4. A stream that fails any test stays open. That is the safe direction: its members stay up.

### 4.2 Tidy, part B: retire idle members

**Candidates:**
- every peer of every stream whose `state` is `done` (just marked or earlier) with `status` ≠
  `gone`;
- every member of the run's team that is in **no** stream (`main`, from the team file /
  `ah roster teams`) with `status` `idle`, **and** has no queued item routed to it or its role:
  no not-started or rework item whose resolved implementer, reviewer or designer is that
  member or role (§ Routing). The two pipelining guards are under "Never a candidate" (rev 4.3).

  A retired non-stream role is re-spawned later through § Elastic membership if work for it
  appears.

**Buffer (rev 3, the user's words: "We can't be too aggressive on the closures. Because related work could come back to the agent soon after."):**
a candidate is closed only when **both** hold, in addition to every check below and in steps 1-3.
The exception is §4.2a.
- **Done for more than 1 hour:** `now - work-done time > 60 min`. Work-done time is the later of:
  - the member's last report or DONE message (the `created` of its latest response file, from `ah msg list --team <team>`);
  - for a member of a done stream, the time `stream-done` marked that stream done.

  A member with neither is not past the buffer.
- **Idle for more than 1.5 hours:** `now - idle-since > 90 min`.
  - **(rev 4, confirmed) Idle-since is the activity record's `state_since`** (HierLib.Status.cs:386-390, 5b486857), and only while that record's current state is idle. If the state is anything else, the member is not idle.
  - `cadence.md` names the read surface the Orchestrator uses for it. The implementor verifies which existing `ah roster`/`ahd` read shows `state_since` for a member (the view carries it through `ViewSource.ActivityOf`).
  - **Amended (rev 4.1, 2026-10-17, on implementor-28 43de4ed8):** no CLI read prints `state_since` today. **Add one read-only key, `idle_since`, to each team row of `ah roster teams --json`: a map {member name: `state_since` when idle, else null} (rev 4.2).**
    - Its value is the activity record's `state_since` when that record's current state is idle, otherwise `null`. It is also `null` when the record is missing or unreadable, or has no `state_since`.
    - Read it through the existing activity read (the same source `ViewSource.ActivityOf` uses). Never write it.
    - This is the only CLI change in this spec. There is no new verb, and nothing else in `teams` changes. Contract recordings for `teams --json` that gain the field are re-recorded one case at a time, with the reason "additive idle_since".
    - **`last_report_at` is not added:** the work-done time already comes from `ah msg list --team` (above).
    - `cadence.md` reads `idle_since` from `teams --json`.
    - Tests: an idle member with `state_since` gives that value; a working member gives null; a missing record gives null; there is no other key change in the row (golden diff).
  - **Otherwise add no new verb or field.** Fail safe, keeping the member, when:
    - no existing read shows it;
    - the record has no `state_since` (written before 5b486857);
    - the record is unreadable.

    The implementor reports which case held.

Both tests are strict (`>`): 60 min done exactly, or 89 min idle, is kept. A member inside the
buffer is kept and, with nothing else to report, adds nothing to the summary beyond the `kept`
count.

**Never a candidate:**
- the Orchestrator;
- any member that owes or is owed an open exchange (it appears in any stream's `exchanges`
  `to_name`/`from_name`, or `ah msg list --team <team>` shows an open request addressed to it);
- any member whose `status` is `working` or `blocked`;
- (rev 4.3, moved from the candidate list so they bind every candidate, in a stream or not) the
  **next item's pipelined Implementor**;
- (rev 4.3) the **Reviewer of an item currently in implementation**.

For each candidate, in order:

1. **Plan.** Run `dismiss <name>` with no `--close`. Read `release.status`.
2. **Act on the status:**

   | status | action this tick | counts as |
   |---|---|---|
   | `not-asked` | Send a release request: `ah msg new --to <role> --to-name <member> --slug release …`, then `SendMessage` the `[hierarchy-msg <path>]`. Do not close; the next tick re-plans. | asked |
   | `unanswered` | Nothing. Do not re-ask: one request per member stands until answered. | waiting |
   | `ready`, `not-running` | Run the merge check (step 3), then close (step 4). | — |
   | `not-ready`, `not-running-dirty` | Keep it. Put the member and its reason in the summary. Never close it, never pass `--ignore-release`, and never re-ask in the same run unless the member reports again. | kept |
   | `stale` | Keep it. Send **one** fresh release request only if the stale reason is `busy now` or `sha` (HEAD moved). For `dirty`, `no work tree` or a git read failure, report it and leave it. | kept |
3. **Merge check (in addition to the release check).** The released sha "need not be pushed", so
   the release check alone does not satisfy the user's "never close a member with unmerged
   work".
   - For `ready`, take the sha from the member's `Release:` line. For `not-running`, take its
     work tree's `HEAD`.
   - That sha must be reachable from a merge target (§4.1 (c)) or from some `origin/*` ref
     (`git branch -r --contains <sha>` non-empty).
   - If it is not, keep the member, report `unmerged <sha7>`, and do not close.
4. **Close.** Run the plan's `next` command (`dismiss <name> --close --confirm --plan-token
   <tok>`), copied, never assembled. The harness permission prompt fires every time; that is
   the user's gate and stays.
   - **The conversational confirmation in `teardown.md` step 2 is satisfied by the user's
     standing directive (#146), only for members that passed steps 2-3.** The implementor
     writes that sentence into `cadence.md`.
   - Classify the result, and only this way:
     - **closed:** exit 0, `closed: true`, `still_live` empty;
     - **held:** exit 3 (the release check changed between plan and close). Treat as kept;
     - **refused:** any other exit, no JSON, a stale-token refusal, or a denied permission
       prompt.
   - **On the first refusal caused by a denied permission prompt, stop closing for the rest of
     this tick.** Do not put prompt after prompt in front of a user who just said no. The next
     tick tries again.
   - A stale-token refusal → re-plan that member once in this tick.
5. **Verify against the live list, never the verb's output alone.** The live list is
   `ah roster teams --json` (the team row's `members` count and `idle_since` map) together with the
   harness session list (`ListAgents`). It is not `stream-status`, which omits members in no stream
   (rev 4.2, orchestrator). Take it before the first close (**before**) and after all closes
   (**after**). A member counts as **closed** in the summary only if the verb said closed **and**
   the after list no longer shows it live.
   - **(rev 4.3, decided)** "No longer live" means absent from **both** the team row
     (`teams --json`) **and** `ListAgents`. If the two disagree (for example gone from
     the team row but still listed by `ListAgents`, or the reverse), the member is **not**
     counted closed. Report it as `disagree <name> (team: <state>, ListAgents: <state>)`, and
     never close or re-close it in that tick.
   - The verb said closed but the member is still present → report `still-live <name>`.
   - The member is absent but the verb said refused → report it as closed-by-other.
   - Every number in the summary comes from comparing the before and after lists. This is the
     fix for 2026-10-17's miscount.

### 4.2a Plan complete: the final tick skips the buffer (rev 4, user)

The user's words: "The exception to the rule is if all work has completed entirely. Plan
complete. That can be a trigger to close all teams who have completed."

- **How "plan complete" is detected.** All of the following hold at the final tick (§4.0), and
  at no other tick:
  - **Plan runs:** § Completion gate (SKILL.md:275) **has passed** in this run. That means the
    gate's checks ran and passed, and the run is about to send its final message (SKILL.md:288)
    and close its anchor (SKILL.md:291; run-anchor.md:44). A gate that failed, stopped or was
    not run is not plan complete.
  - **Issue runs** (no whole-run gate, issue-runs.md:391): the run has reached its end of run,
    and **every** item's record is in a finished state (its PR opened or merged, as issue-runs.md
    defines finished). Any item stopped, parked, at the round cap or still open means not plan
    complete.
  - **Either kind:**
    - every stream's `progress` shows In progress = 0 and Not started = 0 (skipped when progress
      records are off);
    - no open exchange in any stream;
    - no parked decision waiting for the user (`ah msg decision list --team <team> --summary` shows none waiting).
  - If any one is missing, it is not plan complete. The final tick then runs as normal, **with**
    the buffer.
- **What changes when it is plan complete:** only the §4.2 buffer is skipped (the 60-minute done
  and 90-minute idle tests). Everything else applies unchanged:
  - part A marks finished streams done first;
  - the candidate rules;
  - "never a candidate" (open exchanges, `working`/`blocked`, the Orchestrator);
  - the release check and its status table;
  - the merge check (a clean, merged tree);
  - never `--ignore-release`;
  - the two-phase close;
  - stop on a denied prompt;
  - the before/after live-list verification.
- **Scope ("all teams who have completed"):** every member of the run's team that passes, plus
  every child team of a done stream (sub-orchestrator teams, 0067), closed through that team's
  existing close path with the same checks. The run's own team record is not disbanded, because
  the Orchestrator lives in it.
- **Anything that does not pass is kept and reported** in the final message as well as in the
  `tick:` line, with its reason (`not-ready`, `unmerged <sha7>`, `held`, `refused (denied)`, …).
  There is no next tick, so the final message says the member is still up and why.
- **Prompts:** until friction-batch-1 F9 (batch close) lands, each close still raises its own
  harness prompt.

### 4.3 Parallel

Runs after tidy, so the members tidy freed count toward the budget.

1. **Budget.**
   - `live` = members of the run's team that the after list (`ah roster teams --json` with the
     harness session list, §4.2 step 5) shows live, Orchestrator excluded, members in no stream
     included (rev 4.2, orchestrator).
   - `budget` = the anchor's `session-budget:` level (run-anchor.md § Writing the anchor),
     mapped low 8 / med 12 / high 16 / xhigh 20 / max uncapped (rev 4.4, #158). A missing line
     means med.
   - `live ≥ budget` → no growth this tick; say `parallel: at budget` in the summary.
   - **`max`:** the budget sets **no** growth limit. The step 3 limit of **one new stream per tick
     still applies**, and so do all the eligibility rules. The summary shows `live <n>/max`.
2. **Eligible work**, all of the following:
   - not started (a plan item or issue item in state not started; rework is never eligible,
     because it belongs to the item's own implementer under § Pipelining);
   - its dependencies are done;
   - no live member is assigned to it;
   - **its files are disjoint** from every in-flight item's files. "Files" means the
     "files to touch" list in the item's spec (or the plan's own file list for that item).
     An item with no known file list is eligible **for design only**: an Architect writes
     only its spec, which touches no code. A second implement step never runs on unknown files.
3. **Growth limit:** at most **one new stream per tick**, and only while `live < budget`. Each
   session adds daemon load (#143: health stalls grow with the registration count).
4. **Start it as a stream, so it opens in its own tab:**
   - `stream-open <item slug> --base <base>`, where `<base>` is the anchor's `branch:` in a
     plan run and the issue run's item base in an issue run;
   - then `spawn-one`/`spawn-ad-hoc <role> --stream <item slug>` per peer;
   - then brief it as `streams.md` step 3 says.

   **Never start parallel work with a plain `spawn-one` into an existing tab.**
5. **Where the parallel branch lands:**
   - **Issue runs:** each item already ends in its own PR, so the stream's branch is the
     item's branch.
   - **Plan runs:** they push to one anchor branch. A parallel stream's branch merges back into
     the anchor branch only after that item passes review, done by the Orchestrator as an
     ordinary `git merge --no-ff` on the anchor branch.
     - The merge is a commit on the anchor branch, so the whole push regime
       (`reference/push-regime.md`) applies to its push: Guard 1 scan, the anchor/HEAD agreement
       check, no red build, never force.
     - A merge conflict stops that item (as at a round-cap stop) and notifies; never resolve
       it silently.
     - Decided yes in Q4.
6. **The pipelining rule still decides first.** When the Implementor finishes N, it gets N+1.
   Parallel is only for work no live member can take. "Never invent work" still holds: no
   eligible item means no growth.

### 4.4 Summary line

One line, sent in the session as plain text, never through `PushNotification`. Sent every
tick, including one that did nothing. Fields that are zero are left out, except `live`:

```
tick 14:32Z — done: feat-a, feat-b · closed 3 (x, y, z) · asked 2 · kept: m (not-ready: "WIP parser"), n (unmerged 1a2b3c4) · refused 1 (denied) · parallel: opened feat-c (#152) · live 7/12
```

Rules for the line:
- `closed` is the verified count from §4.2 step 5.
- Anything still live that the verb called closed is shown as `still-live: <names>`.
- A no-op tick is `tick 14:32Z — nothing to do · live 7/12`.
- When the line also answers a mid-run status question, it ends with `ah msg decision list --team <team> --summary`'s
  `line`, as § Notification requires.

## 5. "New workstreams prefer a new tab"

- **The CLI already does this.** `stream-open` opens a tab, `--stream` spawns land in it, and a
  stream with no live tab gets one first. The gap is behaviour: parallel work was started with a
  plain `spawn-one`, which splits an existing tab.
- **The fix is the directive** (§3.1 step 2, §3.3 streams.md, §4.3 step 4). There is no CLI
  change.
- `spawn-one` without `--stream` keeps its current layout. Changing it would move every
  ordinary non-stream spawn (Q5).

## 6. Decisions I made

| decision | why |
|---|---|
| The trigger is "after a report, plus `check-in-due`/`peer-silent` wakes, rate-limited by a `Tick:` stamp in the rollup". | It is existing machinery. The stamp survives compaction. Nothing changes between reports, so a timer would only add load. |
| Tidy closes members of **done** streams, and idle non-stream members with nothing queued for them (Q2). It never closes an idle member of a live stream. | An idle member of a live stream may be about to get rework or the next item. A non-stream member with nothing routed to it is the "idle for hours" case from 2026-10-17. |
| Merged = ancestry into a merge target plus ≥ 1 own commit, or an issue record of a merged PR. | Ancestry alone calls a stream with no commits merged. Squash merges defeat ancestry, so the issue record covers them. |
| An extra merge check on top of the release check. | The release check accepts an unpushed sha. The user said never close with unmerged work. |
| Never `--ignore-release`. Stop closing on the first denied prompt. | One is safety. The other avoids bombarding the user with prompts. |
| The closed count comes from a before/after `stream-status` diff. | It fixes the miscount. The verb's `closed` is necessary but not sufficient as a count. |
| `session-budget` is a named level (rev 4.4, #158): low 8 / **med 12 (default)** / high 16 / xhigh 20 / max uncapped. One new stream per tick at every level, `max` included. (Rev 1–4.3: default 8, superseded.) | #143 measured 2.1-2.5 s health stalls at ~40 registrations, with restarts tracking session count. 8 per team plus the Orchestrator keeps one team well under that. It is a guess, so it is Q3. |
| The summary is a session line, not a push notification. | § Notification's push list is deliberately closed. The user asked to "see" the tick, not to be paged by it. |
| The directive goes in SKILL.md and the procedure in a reference file; `session-budget` goes in run-anchor.md (rev 4). | SKILL.md carries only the always-on rule; the anchor's format already lives in run-anchor.md at 6bbfe531. |
| (rev 4) Plan complete is detected only at the final tick, from the passed Completion gate (plan runs) or all items finished (issue runs), and it skips only the buffer. | It is the user's exception, and every safety check must still hold. |

## 7. Invariants and negative cases

**Must not change:**
- the `dismiss`/`disband` two-call contract and its harness gate;
- release-check semantics;
- `stream-done` closing nothing;
- the fail-closed push rule;
- the 3-round cap;
- § Notification's push list;
- non-pipeline Orchestrator behaviour (the tick runs only while a run anchor is open);
- `spawn-one`'s non-stream layout;
- `--ignore-release` stays available to the user, never to the tick;
- no text moved out of SKILL.md (rev 4).

| input | expected |
|---|---|
| A stream merged, but a member has an open request (to or from it) | That member is not a candidate; the stream is not finished at all, by (a). Nothing closes. |
| A stream merged, checklist complete, a member `working` | Not a candidate. The stream may still be marked done; that member stays. |
| A stream whose branch has 0 commits beyond base | Not finished. Not marked done. |
| A stream squash-merged in an issue run (ancestry fails) | Finished through the issue record of the merged PR. |
| A stream squash-merged in a plan run | Not finished (no record). It stays open. That is safe, and the tick reports nothing for it. |
| Release `ready`, but the sha is on no target and no `origin/*` ref | Kept, `unmerged`. Not closed. |
| Release `ready`, tree dirty since the answer | The CLI says `stale` (`dirty`). Kept, not re-asked. |
| Release `not-running`, tree clean, HEAD unpushed and unmerged | Kept, `unmerged`. |
| Release `not-running-dirty` | Kept, reported. |
| `not-ready` (two such members in tonight's evidence) | Kept, its reason shown, never closed in this run unless the member reports again and answers `ready`. |
| `not-asked` | Asked once. Closed at a later tick only if the answer is `ready` and passes the merge check. |
| `unanswered` across many ticks | Nothing new each tick; listed as `asked`/waiting. |
| The close permission prompt is denied | `refused 1 (denied)`. No more closes this tick. The next tick retries. |
| The close exits 3 | Held = kept, never counted closed. |
| The verb returns `closed: true` but the member is still in the after list | `still-live <name>`; not counted. |
| A member outside any stream, idle, nothing queued for it or its role | A candidate: release check, merge check, close. |
| A member outside any stream, idle, but it is the next item's pipelined Implementor or the reviewer of an item in implementation | Not a candidate. |
| An idle member of a live (not done) stream | Not a candidate. |
| Done 59 min ago, idle 2 h | Kept (inside the done buffer). |
| Done 2 h ago, idle 89 min | Kept (inside the idle buffer). |
| Done 61 min ago, idle 91 min, every other check passes | A candidate. |
| Done and idle times unreadable | Kept. |
| Activity record has no `state_since` (pre-5b486857) | Kept. |
| Activity record's state is not idle, `state_since` 3 h ago | Not idle, so kept. |
| **Plan complete** (gate passed), member done 10 min, idle 5 min, release `ready`, merged | **Closed** at the final tick (buffer skipped). |
| Plan complete, member `not-ready` | Kept; reported in the final message with its reason. |
| Plan complete, member's sha unmerged | Kept; `unmerged <sha7>` in the final message. |
| Plan complete, the close prompt is denied | No more closes; the final message lists the members still up. |
| Completion gate failed or stopped | Not plan complete; the final tick applies the buffer. |
| Issue run, end of run, one item stopped at the round cap | Not plan complete; the buffer applies. |
| Plan complete but one parked decision still waiting for the user | Not plan complete; the buffer applies. |
| A tick other than the final one, with every item done | No exception; the buffer applies. |
| Plan complete, child team of a done stream, its members clean | Closed through its existing close path, with the same checks. |
| A parallel stream in a plan run hits a merge conflict merging back into the anchor | That item stops and the user is notified; nothing is pushed. |
| The Ultra-Advisor or a decider mid-question | It has an open exchange, so it is not a candidate. |
| `live ≥ budget` | No `stream-open`; `parallel: at budget`. |
| Two eligible items | One stream this tick; the next tick takes the other. |
| An eligible item with an unknown file list | Design dispatch only (Architect in a stream); no parallel implement. |
| An item whose files overlap an in-flight item | Not eligible. |
| Rework on item N | Never parallel; it goes to N's implementer queue. |
| Progress records off | Tick on every report and wake; part A's (b) is skipped. |
| `git fetch` fails | Local refs are used; the summary says `fetch failed`. |
| tmux/terminal transport (no tabs) | `stream-open` degrades (`degraded: "no-tabs"`); everything else is unchanged. |
| An ordinary (non-pipeline) Orchestrator session | No tick. `streams.md`'s "when the user asks" still governs. |

## 8. Verification

Tests the implementor adds. They are content tests in the house style of
`tests/test-skill-content-defects.sh`; no tick runs in a test.

1. `tests/test-skill-content-defects.sh`:
   - the SKILL.md bound stays at 500 lines / 30,720 bytes (do not raise it) and passes before
     and after;
   - `reference/cadence.md` exists and SKILL.md names it, extending the line-57 check;
   - SKILL.md has a `## Cadence` heading **before** `## Bootstrap`;
   - the Cadence section contains `--ignore-release` in a "never" clause, `stream-open`,
     `session-budget` and `tick:`;
   - `reference/run-anchor.md` contains `session-budget`;
   - (rev 4.4) run-anchor.md names all five levels with their numbers (`low`/8, `med`/12,
     `high`/16, `xhigh`/20, `max`/uncapped), says med is the default, and says a missing line reads
     as med;
   - (rev 4.4) SKILL.md § Invocation names `--budget` and the five levels; § Notification's
     run-start line names `budget`;
   - (rev 4.4) cadence.md's parallel step names `max` with "one new stream per tick" still
     applying.
2. A new check that `reference/cadence.md` contains each of the following:
   - `Tick:`;
   - `merge-base --is-ancestor`;
   - `rev-list --count`;
   - every release status name (`ready`, `not-running`, `not-running-dirty`, `not-ready`,
     `unanswered`, `not-asked`, `stale`);
   - `branch -r --contains`;
   - "stop closing" with "denied";
   - a before/after `stream-status` comparison;
   - "one new stream per tick";
   - the close buffer: `60` minutes done and `90` minutes idle (59 min done and 89 min idle are
     kept);
   - `state_since` as the idle-since source, with "keep" when unreadable;
   - the plan-complete exception: `Completion gate` with the buffer-skip clause, a clause that
     every other check still applies, and "final message" for the kept list;
   - `PushNotification` in a "not" clause.
3. No text moves out of SKILL.md, so no assertion is repointed. A red assertion about moved text
   means scope crept in.
4. `tests/test-roster-stream.sh` is unchanged and still green. There is no CLI change, so a red
   result there means scope crept in.
5. `streams.md` no longer says "only when the user asks" without the pipeline-tick exception. A
   grep check is enough.
6. The full `tests/` suite is green. The implementor or task-runner reports this; the Architect
   cannot observe it.

## 9. Risks for the implementor

- SKILL.md is under its bound at 6bbfe531, so the size test is green before and after. If it is
  red on the base, stop and report.
- `agents/orchestrator.md` is over its 1,700-byte ceiling (1,823). That is out of scope here. Do
  not touch the file, and report whether `test-peer-progress.sh:97` is red on the base.
- Keep `cadence.md` self-contained but short (≤ ~200 lines with §4.2a). Do not restate
  `teardown.md`; cite it.
- The idle-since read surface (§4.2) is unverified. If none exists, the buffer keeps everyone
  until plan complete; report it, and do not add a verb.

## 10. Decisions on the rev 1 questions

The Orchestrator decided these on 2026-10-17, under the user's rule that such calls are the
Orchestrator's and recorded.
- **Q1 no.** There is no `ah roster tidy` verb now. Closes are counted by the before/after
  `stream-status` / live-list diff. Revisit if miscounts recur.
- **Q2 yes.** Idle non-stream members are retired under the same rule (§4.2).
- **Q3.** The budget is 8 **per team**, set by the anchor's `session-budget:`.
- **Q4 yes.** Plan runs may merge a reviewed parallel stream back into the anchor branch, under
  the push regime (§4.3 step 5).
- **Q5 no CLI change.** New work always goes through `stream-open`.
- **Q6 yes.** A batched close goes into friction-batch-1 (F9), a separate spec. Until it lands,
  each close raises its own prompt.

The original questions, kept for the record:

- **Q1. A read-only `ah roster tidy` verb?** It would compute, deterministically, the finished
  streams, candidates with release and merge evidence, held members with their reasons, and the
  live count against the budget, and return the exact `stream-done`/`dismiss` `next` commands.
  It would take the counting and git checks out of model judgment, where tonight's miscount
  happened, but it is a new public CLI surface with its own tests. Rev 1 does without it.
  Recommended as rev 2 if the content-only version miscounts again.
- **Q2.** Should tidy also retire idle members **outside** any stream (main-checkout members)
  when the run's queue has nothing routed to their role? Rev 1 says no.
- **Q3.** `session-budget` default 8 and one new stream per tick: is that the right ceiling for
  your machine? Should it count all teams' sessions (daemon load is global) rather than this
  team's? Rev 1 counts this team's only. A global count needs a cross-team live count, which
  `ah roster teams` gives.
- **Q4.** In plan runs, may the Orchestrator run parallel streams that merge back into the anchor
  branch (§4.3 step 5)? This is new behaviour; today plan runs are strictly one branch. If no,
  the parallel step in plan runs only **reports** eligible items and opens nothing.
- **Q5.** Should plain `spawn-one`/`spawn-ad-hoc` (no `--stream`) also default to a new tab
  during a pipeline run? Rev 1 keeps the CLI as it is and forbids the pattern in the directive.
- **Q6.** Each close raises a harness permission prompt, so a tick that retires 6 members asks
  6 times. Acceptable, or do you want a batched close (a stream-scoped `dismiss`) in the
  friction batch?

## Change log

- rev 4.4 (2026-10-17, architect; #158, the user's request; ships in 2.5.0-pre.1, decided by the Orchestrator):
  - a named session budget: low 8 / med 12 (default) / high 16 / xhigh 20 / max uncapped;
  - the `/pipeline --budget` flag and its plain-language forms (SKILL.md § Invocation);
  - the anchor line `session-budget: <level> (<N>)`, with a missing line read as med;
  - the run-start notification field;
  - §4.3 reads the level; `max` removes only the budget cap, and one new stream per tick stays;
  - §6 and §8 are updated.

- rev 4.3 (2026-10-17, architect, request 20261017-104326-19h1; review 20261017-102759-7qr3 N1/N2):
  - N1: §3.1 quotes the shipped § Cadence text (9847efec SKILL.md:51-71);
  - N2: the next item's pipelined Implementor and an in-flight item's Reviewer move into "Never
    a candidate";
  - decided: closed means absent from both the team row and `ListAgents`, and a disagreement is
    reported, not closed.
- rev 4.2 (2026-10-17, orchestrator): `idle_since` is a per-team map on each team row of
  `ah roster teams --json` ({member name: `state_since` when idle, else null}); there are no member rows
  in `teams --json` (§4.2). The live list for close verification and the session budget is
  `teams --json` plus `ListAgents`, not `stream-status`. `ah msg decision list --team <team> --summary`
  is spelled in full.
- rev 1 (2026-10-17): first version.
- rev 2 (2026-10-17): folded in the Orchestrator's decisions on Q1-Q6.
  - §4.2 adds idle non-stream members as candidates, guarded so the next item's pipelined
    Implementor and an active reviewer are never retired (Q2).
  - §4.3 step 5 makes plan-run merge-back definite, under the push regime, with conflicts
    stopping the item (Q4).
  - §3.1, §6 and §7 are updated to match.
  - No other behaviour changed.
- rev 3 (2026-10-17, user): close buffer. A member is a close candidate only after its work has
  been done more than 1 hour and it has been idle more than 1.5 hours (§4.2 Buffer; §7 rows);
  the directive and tests follow.
- rev 4 (2026-10-17, architect, on implementor-28's BLOCKED report 20261017-101309-wpug):
  - the base is 2.5.0-pre.1 `6bbfe531` (decided (a)), and §2 facts were re-read there;
  - §3.2's moves are dropped (SKILL.md is 433 lines);
  - §3.3 is re-mapped to the pre.1 headings, and `session-budget:` now lives in
    `reference/run-anchor.md` § Writing the anchor;
  - §4.2's idle-since is the activity record's `state_since`, with a fail-safe keep;
  - new §4.2a, the user's plan-complete exception: the final tick skips only the buffer, with
    detection rules for plan and issue runs;
  - §6, §7, §8 and §9 are updated;
  - the canonical copy is this file.
