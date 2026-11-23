# How Wrangl works

This page is for anyone who has installed Wrangl, or is deciding whether to, and wants to know what it does and why.
After reading it you will know what each part is for and what happens to a request. It has no commands to learn: those
are in the [CLI reference](cli-tools.md). Each term is defined in the [Glossary](glossary.md).

- [A request, start to finish](#a-request-start-to-finish)
- [The picture](#the-picture)
- [Roles and the hierarchy](#roles-and-the-hierarchy)
- [Lanes: sub-orchestrators](#lanes-sub-orchestrators)
- [Briefs and mail](#briefs-and-mail)
- [The daemon](#the-daemon)
- [Streams and work trees](#streams-and-work-trees)
- [The team goal and your rules](#the-team-goal-and-your-rules)
- [The checklist and the Sitrep](#the-checklist-and-the-sitrep)
- [What keeps members in bounds](#what-keeps-members-in-bounds)
- [QA and shipping](#qa-and-shipping)
- [What the Orchestrator tells you](#what-the-orchestrator-tells-you)
- [Recovery after a crash or reboot](#recovery-after-a-crash-or-reboot)

## A request, start to finish

Say you type "add a dark mode to the settings page" into your Claude Code session, which is the **Orchestrator**.

1. The Orchestrator opens a **stream**: a new git branch with its own folder, so the work stays off your main
   checkout.
2. It writes a **brief** for the **Architect**: the goal, the files, and what counts as done. It mails the Architect a
   short note that the brief is waiting.
3. The Architect writes a design (a **spec**) and files its **report** beside the brief.
4. The Orchestrator briefs the **Implementor** with the spec. The Implementor builds it in the stream's folder and
   commits on the stream's branch.
5. The **Reviewer** checks the change against the spec while **QA** runs the full tests.
6. When both pass, the Orchestrator merges the change and tells you.

All the way through, the **daemon** delivers the mail, notices when someone goes quiet or gets stuck, and the
**checklist** keeps track of what is still open. The rest of this page explains each of these parts.

## The picture

```
                        you
                         │  talk to
                         ▼
                 ┌────────────────────┐
                 │ Chief Orchestrator │  your Claude Code session
                 └───┬───────┬────────┘
        briefs and   │       │   briefs and
        reports      │       │   reports
       ┌─────────────┘       └──────────────┐
       ▼                                     ▼
 ┌────────────┐  ┌────────────┐     ┌───────────────────┐
 │ Architect  │  │ Implementor│ ... │ Sub-Orchestrator  │  runs one lane
 │ Reviewer   │  │ QA, Docs   │     │ and its own team  │
 └────────────┘  └────────────┘     └───────────────────┘

 Behind all of it:
   message files   the written briefs and answers, kept in your repository
   mail            short notes that tell a member "you have a brief"
   ahd (daemon)    delivers mail, watches for stuck members, serves the status views
   hooks + rules   checks on what a member may run, and who may ask you questions
```

Everything lasting is stored on disk, so you can read it, and so a crash loses nothing.

## Roles and the hierarchy

The **Chief Orchestrator** is the Claude Code session you are typing into. It never does large pieces of work itself.
It splits the request, writes a short written **brief** for each piece, sends each brief to the right role, and
puts the answers together.

"Orchestrator" on its own means whoever is running the team at the moment. That is the Chief Orchestrator, unless a
Deputy Chief is on duty while it is away. The mailbox is `orchestrator@<team>` either way.

The roles under it:

| Role | Job | Default model |
|---|---|---|
| Architect | designs the change and writes the spec file; never builds or runs anything | opus |
| Implementor | builds exactly what the spec says; makes no design choices | your session's model |
| Reviewer | checks the diff against the spec; labels each problem as a code defect or a spec defect; edits nothing but its own answer file | opus |
| Ultra-Advisor | settles the hardest calls; `/wrangl:pipeline` asks once at the start whether it may be used | fable |
| Docs-Writer | writes documentation, never source code | sonnet |
| UX-Engineer | builds web UI and proves it with screenshots in light and dark themes | opus |
| QA | runs the heavy test sets and reports failures, including whether a failure came from the change | sonnet |
| Sub-Orchestrator | runs one lane (see below) | sonnet |
| Task-Runner | cheap legwork: run a build, search the code, read a log; carries out explicit orders and does not decide | haiku |
| Backlog-Groomer | keeps the project backlog tidy: ranks, merges duplicates, marks items done when a merge proves it; never edits code | sonnet |

One more role, the **Deputy Chief** (sonnet), can run the team while the Chief Orchestrator is away. It never asks you
anything and does not push or merge. A member of that role is put on duty with `ah roster duty on <member>`.

You can change the model, effort and other settings of each member in your roster with `/wrangl:agent-roster`;
`/wrangl:agent-role` adds roles of your own. Reasoning and building roles run as separate live sessions
(**peers**), each in its own pane. Only the Task-Runner and the Backlog-Groomer run as short-lived helpers inside
another session (**subagents**).

A normal request goes like this: the Orchestrator asks the Architect for a spec, the Implementor builds it, the
Reviewer checks it. If the Reviewer says the code is wrong, the work goes back to the Implementor. If the
Reviewer says the spec is wrong, it goes back to the Architect. After two round trips without agreement the
question goes up to the Ultra-Advisor instead of looping. A small, fully specified change skips the Architect.
A trivial edit skips the chain.

## Lanes: sub-orchestrators

A big job can have several parallel lines of work. A **lane** is one of them: its own branch, its own work tree,
and its own **Sub-Orchestrator**, which acts as a manager for that line. The Sub-Orchestrator staffs its own small
team (up to the member and stream limits the Orchestrator sets), hands out work, and reports back to the
Orchestrator when a piece is done. It does not push or merge. It returns the branch, the commit and the test and
review results, and the top Orchestrator merges. Members of a lane can message their lane lead directly.
A Sub-Orchestrator never asks you anything directly. For a simple pick inside its lane, such as which of four candidates
to keep, it can park the question on your Needs-you list and keep working. You answer when you choose, and the answer
goes to the lane lead with a copy to the top Orchestrator, which is also told when the question is parked. Bigger
questions (policy, product direction, anything risky or reserved) still go up through the top Orchestrator. That split is
guidance to the lane lead, not something the software checks. Nothing is pushed to you for a lane question.

## Briefs and mail

Two things carry work between members, and it helps to keep them apart.

**Message files** hold the content. A brief is a "request" file; the answer is a "response" file beside it. Both
sit in the `msgs/` folder under `.claude/ah2/` in your repository. A request names who it is for, how big the
job is (small, medium or large), the goal, the context, the files involved and what counts as done. The response
starts with a status: done, partial or blocked. A request with a response is finished. You can read these files.

**Mail** is the short "go and look" note. When the Orchestrator sends a brief, it mails the member a pointer to the
request file. The member reads the file, does the work, writes the response and mails a pointer back. Mail has four
kinds: brief, report, note and event (an automatic alarm from the daemon).

The Orchestrator gets its mail differently from members: a short line such as "You have 2 messages (1 question, 1 report). Run
`ah msg next`" (the **doorbell**), rung once after things go quiet so one busy minute does not cause ten wake-ups.
Urgent mail rings at once. `ah msg next` shows one message at a time.

Mail is delivered in steps (the **delivery ladder**), so a note is never silently lost. First the recipient is
expected to pick it up itself. If it does not within a short time, a carrier is asked to pass it on. If that fails
too, Wrangl types it into the member's pane (only into the pane recorded for that member, and only if it is safe). If
even that fails, the Orchestrator gets a "delivery failed" event. Details: [Mail](mail.md).

Who may message whom is also checked. By default members talk to their Orchestrator. Two members may talk directly
when they have a **link**:

- **Members assigned to the same work are linked by that fact.** Two members in one stream, or two members the
  Orchestrator briefed on the same checklist item, can message each other with no request and no wait. The link ends
  when the shared work does.
- **Any other pair asks for one** with `ah msg link-request`. That link is for one task and lasts for a set number of
  messages or minutes.

Every link is on record. The Orchestrator can read the messages sent over it and revoke it.

## The daemon

`ahd` is a small background program that starts when it is needed and quits after 30 minutes with nothing to do.
You do not start it yourself. It does the jobs that need a program running all the time:

- delivers mail promptly and walks undelivered mail through the steps above;
- keeps a clock on every open brief, and tells the Orchestrator when a member is late, silent, blocked at a
  prompt, or down;
- recovers members after an API error and holds the retries during an outage;
- serves the live views: the terminal view (`ahd tui`) and the browser page (`ahd ui`);
- admits agent programs other than Claude Code that want to join a team.

It is optional. If it is not running, commands read and write the files directly, and a note in the log says so.
What waits for the daemon is only what needs a running program: the delivery steps, late and stuck alarms, and
the live views. If the daemon stops answering, `ahd heal` replaces it, but only once it has proved the daemon is hung.
See [The daemon](daemon.md).

## Streams and work trees

A **stream** is one line of work with its own git branch and its own folder (a git **work tree**) under
`.claude/worktrees/`. Giving each stream a folder means two members never edit the same files at the same
moment, and your main checkout stays clean. A member launched into a stream starts in that folder.

Every brief opens with a line naming the member's work tree and branch, and ends the same way: commit on that
branch, do not push. Pushing and opening pull requests belong to the Orchestrator. When a stream is finished the
Orchestrator wraps it up, which marks it done and closes its tab if every pane in it was made by Wrangl. Once every
member of the stream is gone, a clean work tree is removed and the branch is kept under an archive name
(`refs/archive/<date>/<branch>`). A branch with commits that were never shipped is not wrapped up unless the
Orchestrator says why.

## The team goal and your rules

You can give a team one **goal**: a sentence such as "a working login page, done when the login tests pass". Tell the
Orchestrator; it records your own words with `ah rules goal`. Every member sees the goal at the start of its session,
after a compaction and with each brief, and a brief can say how it serves the goal. The goal is guidance: a member
that thinks a task is off-goal says so and still does it.

Your standing operating principles, such as "never add a dependency without asking", are **rules of engagement**, kept
with `ah rules add`. A rule can apply everywhere (`--global`), to every team built from one roster in any repo (`--roster`), to the whole
repository, one team, one project or one goal, and the most specific one wins. A repo rule beats a roster rule, and a
roster rule beats a global one. Members see rules the same way as the goal. Nothing checks that a rule was followed, and a rule never widens
what a member is allowed to do. You can also manage rules on the [web page](web-ui.md#rules).

## The checklist and the Sitrep

The **checklist** is the Orchestrator's written list of what is outstanding: every item of every stream, plus
loose ends it adds itself. Each item has a state (not started, queued, in progress, waiting on the Orchestrator, in review,
approved, blocked, waiting on you, done or dropped) and an owner. Items move by themselves as things happen: a
member's "done" reply puts its item in review, and a Reviewer's pass approves it. When your release branch moves,
the items whose commits it now carries are closed. A merge or push to any other branch closes nothing. The
Orchestrator can also set a state by hand.

```
ah roster checklist show
ah roster checklist show --stale
```

The **Sitrep** ("situation report") is the one-screen answer to "where are we?". Wrangl builds it from the checklist,
the ship records and the queue; nobody writes it. It always shows **Completed**, **In progress** and **Up next**, and
adds **Waiting on you**, **Blocked**, **Report in, no one on it**, **Stale** and **Up next, old** while they have rows.
A long region shows its first rows and says how many there are in all. Open it from the
[web page](web-ui.md#the-sitrep). To see how long each stage of a change took, run `ah roster timing`.

## What keeps members in bounds

Members run with launch rules: an allow list that includes `Bash(*)`, so a member may run any shell command without
asking. The rules are on by default; `ah allow off` switches them off and `ah allow on` brings them back. Other limits:

- A member's brief names its work tree and branch and ends with "commit on this branch, do not push"; that is an
  instruction, and no gate enforces it. Pushing and merging belong to the Orchestrator, and a Sub-Orchestrator leaves
  the merge to the top Orchestrator.
- Only the Orchestrator may ask you questions. Routine operational questions it settles itself. A question that is
  yours waits on its work item while the rest of the work goes on (in a `/wrangl:pipeline` run it is parked), and the
  Orchestrator stops to ask only when nothing else can move or waiting would do harm.
- The Ultra-Advisor is for the hardest calls. A `/wrangl:pipeline` run asks you once at its start whether it may be
  used; at any other time, tell the Orchestrator not to use it and it will not.
- Test runs go through a shared queue (`ah test run`) so several teams do not overload the machine.

Wrangl does not block a push or a merge itself; it records them. The real protection for your main branch is branch
protection on GitHub.

## QA and shipping

When an Implementor reports done, the Orchestrator merges its branch into a throwaway "ship" branch, and at the
same moment asks QA to run the full tests on that merge and the Reviewer to review the branch. Only when both
pass does the working line move to the tested commit. The steps, and what `/wrangl:pipeline` adds, are in
[Shipping work](shipping.md).

## What the Orchestrator tells you

The daemon watches the team and mails the Orchestrator an **event** when something needs a decision. Events you
may see named in the Orchestrator's messages:

| Event | Meaning |
|---|---|
| `report-landed` | a member wrote its answer but never sent the mail; read it now |
| `check-in-due` | a brief has run past its expected time; the Orchestrator asks for status |
| `peer-silent` | the member did not answer the check-in; you are told |
| `member-blocked` | a member is stuck at a permission prompt or question |
| `stalled-after-compact` | a member compacted its conversation and then stopped |
| `member-down` / `member-recovered` | a member's session died, or came back |
| `api-failed`, `outage` | API errors in a member, or several at once; Wrangl retries with growing waits |
| `job-overdue` | a long build or test run is past its expected time |
| `queue-stall`, `member-waiting` | queued work is not being picked up, or a member waits on the Orchestrator |
| `lane-escalated` / `lane-recovered` | a lane's lead has gone quiet or died, and the manager steps in; and its recovery |
| `member-bare`, `member-relaunched` | a Claude session sits in a member's pane that Wrangl did not start, and Wrangl relaunched it |

Expected times for a brief are 5 minutes for small, 10 for medium and 20 for large. The `context-high` and
`context-critical` alerts exist but do not fire in this release, because Claude Code does not report the size of
a context window.

## Recovery after a crash or reboot

What is lost is only the running sessions. Team records, briefs and answers, undelivered mail, snapshots and the
alarms' bookkeeping are all on disk, so they survive.

- **The Orchestrator session died, but the machine is fine:** restart it with `claude --resume <session id>`.
  The team follows the new process.
- **The machine rebooted:** start Claude Code in the same repository and run
  `ah roster resume --team <name> --yes`. It relaunches each member into its own recorded conversation from the
  newest snapshot and reports each one as resumed, fresh, alive, no-worktree, skipped or failed.
- **A different session should take over a live team:** `ah roster adopt --orchestrator-pid <pid> --team <name>`.
  It refuses a dead pid or a team another live process holds.
- **Before a planned restart:** `ah roster checkpoint --team <name>`, then `ah roster checkpoint --status` to see
  that it is ready.
- **The daemon:** if it was upgraded and `ah roster doctor` shows a red `daemon-contract` row, run `ahd restart`.

Wrangl also repairs one case on its own: if a Claude session was started by hand in a member's pane (so Wrangl did not
launch it), the daemon waits until it is idle, stops it once with a polite signal and relaunches it under Wrangl. It
never does this to a busy session, to the Orchestrator, or while the team is on hold. This repair is off until you
turn it on with `ah roster recover enable --team <name>`. The full procedure and each outcome are in
[Recovery](recovery.md) and [Upgrading](upgrading.md).
