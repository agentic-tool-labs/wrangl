# Shipping work

This page is for anyone who wants a change to go from a request to merged code. After reading it you will know how
the Orchestrator ships one finished change, and how `/wrangl:pipeline` runs a whole plan or a list of GitHub issues
for you while you are away. Words you have not met yet are in the [Glossary](glossary.md).

- [How one change ships](#how-one-change-ships)
- [How finished work is marked done](#how-finished-work-is-marked-done)
- [Run a whole plan with /wrangl:pipeline](#pipeline)

## How one change ships

You do not run a command to ship. When a member finishes a change, the Orchestrator does these steps with ordinary
git, and Wrangl records them:

1. The Implementor reports that the change is done on its branch.
2. The Orchestrator merges that branch into a temporary branch, `ah/ship/<slug>`, in a scratch work tree.
3. At the same time it asks **QA** to run the full tests on that merge and the **Reviewer** to review the change.
4. If something fails:
   - A merge conflict goes back to the Implementor, who brings its branch up to date and reports again.
   - A test failure in the changed code goes back to the Implementor, whatever the Reviewer said.
   - A test failure outside the changed code is the Orchestrator's to sort out.
5. When the tests and the review both pass, your working branch moves to the tested commit. If either branch moved
   in the meantime, the Orchestrator merges again and QA tests again first.

The temporary branch and the scratch work tree stay on your machine and are deleted afterwards. These steps push
nothing.

QA runs tests through a queue shared by every team on your machine, so several teams do not overload it. A test
that fails is run once more on its own; if it passes then, QA reports it as flaky. QA never edits code.

To see where the time went for each piece of work (spec, build, review, rework, QA and waiting to ship):

```
ah roster timing
```

Add `--item <id>` for one piece of work, or `--batch <sha>` for one ship.

## How finished work is marked done

When your release branch moves on the remote, Wrangl closes every item on the checklist whose reported commit is now
on that branch. You do nothing for this. The [daemon](daemon.md) checks once a minute, and a move made while it was
not running is seen when it next starts.

- **Which branch counts:** the remote's default branch (usually `main`) and any `ah/release-*` branches. To use a
  different branch, set it once per repository:

  ```
  git config ah.releaseRef <branch>
  ```

- **It does not fetch.** Wrangl reads your remote-tracking branches, so a push made from another clone is seen after
  your next `git fetch`.
- **A rebase or squash changes the commit,** so the item stays open. Close it by hand:

  ```
  ah roster checklist done <id>
  ```

A merge or push to any other branch closes nothing.

<a id="pipeline"></a>

## Run a whole plan with `/wrangl:pipeline`

`/wrangl:pipeline` takes a list of work and drives every item through design, build and review without you in the
chair. The Implementor starts the next item while the Reviewer checks the last one, so the work keeps moving.

### Start a run

Give it one of these, inside Claude Code:

```
/wrangl:pipeline <plan-file>
/wrangl:pipeline <issue> [<issue> ...]
/wrangl:pipeline --labelled
```

- **A plan, spec or list of acceptance criteria** (a file path). You can also type the acceptance criteria into your
  request; the run saves them to a file first.
- **GitHub issues** by number (`42` or `#42`) or by URL. Each issue becomes one item.
- **`--labelled`**: every open issue that carries your repository's trigger label. Issue runs read their settings,
  including that label, from a committed file described in [Pipeline conventions](pipeline-conventions.md). An issue
  run needs `gh` signed in.

Optional flags:

| Flag | What it does |
|---|---|
| `--branch <name>` | plan runs only: work on this branch. Without it, a plan run makes a new branch, `ah/pipeline-<plan name>`, from where you are. |
| `--budget low\|med\|high\|xhigh\|max` | how many member sessions the run may have open at once: 8, 12 (the default), 16, 20, or no limit. |
| `--auto-merge` | issue runs only: merges into `main` are approved for this run, and the run does not ask. |
| `--adopt` | turn the work the Orchestrator already has in flight into a run. Members are not interrupted. |

For a run that does not stop at permission prompts, use Claude Code's auto mode (`--permission-mode auto`).

### What you are asked at the start

Everything you are asked comes at the start, in one go. After that the run does not stop to ask you anything.

- **Decisions:** whether the Ultra-Advisor may decide questions for you during this run, or the strongest member
  of the team (else a fresh helper on the Orchestrator's model). See [Decisions made for you](#decisions-made-for-you).
- **Authority:** whether members ask you through the Orchestrator (the default), or the Orchestrator may answer
  some of their questions for you during this run. See [Who may answer for you](#who-may-answer-for-you).
- **Auto-merge** (issue runs only, unless you passed `--auto-merge`): may the run merge its pull requests into
  `main` itself? The default is No.
- **Branch** (only when `--branch` names your default or release branch, such as `main`): may the run commit and
  push straight there? You are asked once per repository, and a yes is remembered. See
  [Running on main](#running-on-main).

No answer means the safer choice: no Ultra-Advisor, members ask you, nothing merges into `main` by itself. Each run
asks again; an answer never carries over to the next run, except the branch answer.

Before the run starts, the Orchestrator also creates the team and checks that pushing is possible. If your Claude
Code allow list is missing the rule the run needs to push, it prints the rule for you to add; it never writes a
permission rule itself.

### What happens without you

- Each item goes through the usual chain: Architect, then Implementor, then Reviewer. Code defects go back to the
  Implementor and spec defects to the Architect.
- **An item that is still not agreed after three rounds stops**, and you are told. The rest of the run carries on.
- The run commits after each step and pushes to its own branch: a plan run to its run branch, an issue run to one
  branch per issue (`ah/issue-<N>`), with a pull request for each. Pull requests open as drafts unless your
  conventions file says otherwise.
- It never pushes a build that fails its checks. A failing build stops the run and you are told.
- If the run cannot tell that your CI does not deploy from any branch, it pushes nothing during the run: it commits
  locally and, at the end, asks you once what to push. If a push is refused, the run carries on locally and lists
  the commands for you to run at the end.
- Members whose work is signed off and committed are closed as the run goes, to keep the session count down.

### Decisions made for you

When a question comes up that the plan does not answer:

- A **safe** question is decided for you by the decider you chose at the start. Safe means every option stays inside
  the run's own branch, can be undone there, and stays within an item already in the plan.
- Anything else is **dangerous** and waits for you: anything destructive, remote or merge-related, about security,
  cost or scope, or a question the run cannot classify. A dangerous question stops only its own item; the rest of
  the run goes on, and you answer at the end.

There are caps: at most 4 decisions per item and 20 per run. Past a cap, the question waits for you. Every decision
is logged with its reason and how to undo it. To read the log:

```
ah msg decision list --team <name>
```

To answer a question that is waiting for you, tell the Orchestrator in the session, or answer it on the
[web page](web-ui.md) or in the [terminal view](tui.md).

### Who may answer for you

Members stop and ask when something is yours to decide. By default each question reaches you through the
Orchestrator. If you answer **Authority** with "Orchestrator acts for me (this run)", the Orchestrator may answer
routine matters itself for that run only: stopping or cleaning up a member's own work, scope inside an item,
retries, and rule exceptions you approved in writing. Each such answer is recorded and reported to you.

These always come to you, whatever the answer: pushing to `main` or a protected branch, force-pushes and merges,
your data, credentials and secrets, writing outside the repository, installing software, permissions and hooks,
posting anything outside, spending money, and anything your own standing rules keep for you.

### Watching a run

You do not need to watch, but you can:

- **In the session**, the Orchestrator writes one short `tick` line from time to time: what finished, what started,
  and how many members are live.
- **On the [web page](web-ui.md) or in the [terminal view](tui.md)**, each item of the run is a line on the
  checklist and in the Sitrep, and moves as the item moves.
- **The decision count**, at any time:

  ```
  ah msg decision list --team <name> --summary
  ```

  It prints a line such as `decisions: 7 decided (2 flagged), 1 waiting for you`.

The Orchestrator sends you one notification when the run starts (its branch, its session budget and who decides
for you). After that it notifies you only when something needs you: an item hit the three-round limit, the run is
blocked, a build failed, a question is waiting for you, or a merge into `main` is waiting.

### How a run ends

When every item is done and the tests pass, the Reviewer reviews the whole body of work, not just the last change,
and the Architect signs off on that review. Then:

- **A plan run** sends one final message: the outcome, then "Decisions made on your behalf" and "Waiting for you".
  It opens no pull request; its work is on the run branch for you to merge.
- **An issue run** sends one final message listing each issue with its outcome and pull request. Pull requests
  into `main` merge only if you said yes at the start (or passed `--auto-merge`) and their checks pass. Otherwise
  the message lists each one with the command to merge it yourself, for example:

  ```
  gh pr merge <N> --match-head-commit <sha> --merge
  ```

- The run closes its members before the final message. A member that is still working is not closed; the message
  names it.

Wrangl does not block a push or a merge; it records them. Protect `main` with branch protection on GitHub.

### Running on main

To run a pipeline straight on `main` (or your default branch), pass `--branch main`. The first time, the run asks
whether that is what you want. A yes is remembered for the repository, so later runs do not ask. To see or take
back what you allowed:

```
ah roster pipeline-branch show
ah roster pipeline-branch revoke [<branch>]
```

`revoke` with no branch takes back every branch for the repository.
