# invocation — autonomous-pipeline reference

Read this before parsing any invocation: every form, every flag, its default and the refusals.

- `/pipeline <path> [--branch <name>]` → a plan/spec run, unchanged; with no
  `--branch`, on `ah/pipeline-<stem>` (§ Bootstrap step 6). `--branch main`, or any default branch: § A run on the default branch.
- ACs typed into the request with no file → write them verbatim, one per
  line, to `<root>/.claude/ah2/specs/acs-<YYYYMMDD-HHMM>.md`
  (gitignored scratch), then run a plan run on that path, so its default
  branch is e.g. `ah/pipeline-acs-20260930-2045`; the run-start notification
  names the file.
- `/pipeline <ref> [<ref> ...]` → an issue run over those issues, in that
  order. A `<ref>` is `N`, `#N` or `https://github.com/<o>/<r>/issues/N`;
  a bare integer is an issue unless written `./N`.
- `/pipeline --labelled` → an issue run over every open issue carrying the
  trigger label.
- `/pipeline --adopt [--branch <name>] [request text]`
  → a plan run on the work this Orchestrator already has in flight. Members
  are not interrupted and get no new mail. Build the plan from:
  - every open request this Orchestrator sent: `<ah> msg list --team <team>`
    in JSON mode, then keep the rows whose request file has `from: orchestrator`
    in its frontmatter, the anchor excepted (JSON rows carry no `from`). Slug
    = the request's slug; owner = its `to` role;
  - every in-progress item a member holds: for each `members[].name` in
    `<ah> roster team show`, `<ah> work list --holder <name> --state in_progress --json`.
    Slug = its work id; owner = that member's role. An item that is an
    adopted request's work (the request's `item:` line names it) is listed
    once, under the request's slug.

  Each becomes a plan item in state in progress. Its round count is the
  usual `<ah> msg list --slug <slug> --count`, so the exchanges it already has
  count. Write the plan to `<root>/.claude/ah2/specs/adopt-<YYYYMMDD-HHMM>.md`
  (gitignored scratch): the adopted items, one per line, then the request
  text verbatim; the anchor's `plan:` is that path and the run-start
  notification names it. Its branch is a plan run's: with no `--branch`,
  `ah/pipeline-adopt-<YYYYMMDD-HHMM>` (the plan file's stem) created off HEAD,
  with the same halt when it already exists (§ Bootstrap step 6), never the
  current branch; `--branch` behaves as for any run. It pushes as any run
  does (`reference/push-regime.md`). An open old
  anchor is handled as in `reference/run-anchor.md` § Before writing a new one.
- `--auto-merge` on an issue run, or plain language with the same meaning
  ("auto-merge approved for this run", "with auto-merge", "and merge them
  once I approve") → merges into main are auto-approved for this run, with no
  question (§ Merge on your approval). Feature-branch merges never wait.
- `--budget low|med|high|xhigh|max` on any run sets the session budget (low 8,
  med 12, high 16, xhigh 20, max uncapped; § Cadence). Plain language maps to
  the same five words: "budget high", "high session budget", "use up to 16
  sessions" (the nearest level at or below the number: 16 is high, 10 is low, and a number below 8 is low),
  "no session cap" or "unlimited sessions" (max). Anything else that names a
  budget: ask once, listing the five levels. No flag and no phrase: med. An
  unknown flag value: refuse at bootstrap and list the five.
- Refuse with one line: a path mixed with issue refs; `--branch` with issue
  input; `--adopt` with a path or issue refs; `--auto-merge` on a plan run: "auto-merge needs an issue run — plan
  runs open no PR".

## A run on the default branch

A run whose `--branch` is the repo's default branch (`git symbolic-ref --short refs/remotes/origin/HEAD` without its
`origin/`; with no remote head, `main` or `master`) or its release ref (`git config --get ah.releaseRef`) commits and
pushes straight there. ah never refuses it. The user decides once per repo, and the answer is kept (§ Bootstrap step 3e):

1. `<ah> roster pipeline-branch show --cwd <abs cwd>` prints `{repo, branches}`: the branches the user has already
   allowed for this repo, kept in ah's database. The run's branch is listed: ask nothing and go on.
2. Otherwise ask once, at bootstrap, with `AskUserQuestion` (it can share step 3a's call; asked alone its tag is
   `ah:bootstrap:autonomous-pipeline:branch-main`), header "Branch": "Run this pipeline on `<branch>`? Its commits and
   pushes go straight there." Options, in this order: "Yes, run on `<branch>` and don't ask again for this repo";
   "No, use `ah/pipeline-<stem>`".
3. Yes: record it with `<ah> roster pipeline-branch allow <branch> --cwd <abs cwd>` (one record per repo, which its
   work trees share), then go on. No, or no answer: the run uses `ah/pipeline-<stem>`, as if `--branch` had not been given.
4. The anchor records the branch as for any run, and the run-start notification names it. The user takes the answer
   back with `<ah> roster pipeline-branch revoke [<branch>] --cwd <abs cwd>` (no branch: every branch of the repo).

Nothing else changes for such a run: the push regime, the caps and the merge rules are the same.

**Issue runs:** read `reference/issue-runs.md` before bootstrap (intake,
planning, ordering, records, per-item execution and gate, PRs, `on_item_done`,
exceptions, degraded approval, merge on approval, end of run). A plan/spec run
behaves exactly as the sections above.
