# Progress records and the work journal

Every team keeps a record of its work: a checklist per stream, a journal of what each report said, and a rollup of the
streams. The files are in `<team home>/.claude/ah2/progress/<team>/`, next to `msgs/` and `specs/`. They are untracked, like
everything under `.claude/ah2`; nothing here is committed or pushed. A team that has no name (the legacy `team.json`) uses the
folder `_default`; no team name can start with `_`, so it never collides with a named team.

| File | Owner | What it holds |
|---|---|---|
| `rollup.md` | the Orchestrator | the goal, and a table of the streams with their owners, branches and states |
| `<stream>.md` | the Orchestrator | the stream's checklist: Done, In progress, Not started, related work and blockers |
| `<stream>.journal.md` | the ah CLI | one entry per report, appended automatically, never edited by hand |
| `sessions.jsonl` | the ah CLI | one line each time a member registers or the Orchestrator starts |
| `.nudged` | the ah CLI | the (stream, report) pairs the Orchestrator was already nudged about |
| `.off` | you, through `ah progress off\|on <team>` | present = progress is off for the team (a row, not a file) |

Work that is not in a named stream belongs to the stream `main`.

## What happens by itself

- `ah roster create` makes `rollup.md` and `main.md`; `ah roster stream-open <name>` makes `<name>.md` with the owner, branch and
  work tree filled in. An existing file is never overwritten.
- `ah msg send <to> --kind report --ref <response>` appends one entry to the sender's stream journal after the report is
  delivered: the report's `- [1] status:` line, the tagged lines, and a link to the response file. A team created by an older build
  gets its files on its first report. A send never fails because of the journal: if the journal cannot be written, `ah msg send`
  prints one line on stderr (`ah: progress journal not updated (<reason>); the report was delivered.`) and `doctor` shows the
  error under `hook-errors`.
- The same report is journaled once. A report filed again with changed content is journaled again, marked `(refiled)`.
- A note, a request and a brief are not journaled.

## The tags a report uses

In the report's `## [1] status`, a line that starts (after an optional `- ` or `* `) with one of these words and a colon is
copied into the journal entry verbatim:

`Decision:`  `Deviation:`  `Roadblock:`  `Outcome:`  `Detail:`

A line with any other `Word:` is not copied (it stays in the report, which the entry links to), and neither is a tagged line
inside a code block or under another heading. The status line is always journaled.

`ah msg new --type response` writes this rule as the one line under `## [1] status`, so every role that reports sees it when it
writes a report. The line is part of the template: a response that holds only it still counts as unfilled.

## What the Orchestrator does

The Orchestrator edits `rollup.md` and each `<stream>.md` itself: it updates the checklist and its `Updated:` time on every
report, plan change, addition or deviation, adds `(done <ISO UTC time>)` to a Done line, records decisions and deviations in
the rollup, and writes a ruling of the user as a `Decision:` line naming the user.

Checklist items are `- [x] …` under Done and `- [ ] …` under In progress and Not started. Other lines are notes and are not
counted.

## Planning a stream with `ah progress item`

Plan each stream with `ah progress item`: `add` what is planned, `start` at dispatch, `done --commit <sha>` at merge, `drop`
what is abandoned. The checklist is rendered from it; do not edit the Checklist block by hand. A stale stream means a report
is newer than the plan.

```
ah progress item add|start|done|drop|reopen|retitle|link|note <stream> <item> [--title T] [--commit SHA]... [--branch B] [--issue N] [--note T] [--team T]
ah progress item import <stream> [--team T]
```

- Each call appends one row to `<stream>.plan.jsonl`, then re-renders the stream's Checklist block from it and sets `Updated:`.
  The first call for a stream opts it in; a stream with no plan file keeps its hand-edited checklist, byte for byte.
- `<item>` is a slug (`a-z`, `0-9` and `-`, at most 64 characters). `done` needs 1 to 8 commits (7 to 40 lowercase hex
  digits), and each must resolve in the stream's work tree or where the command runs; otherwise nothing is written. A title is
  at most 120 characters and a note at most 300.
- `import` opts an existing checklist in, once: one `add` (and `start` or `done`) per item line, plain `- ` lines included. A
  line with a commit range, or no title, is imported with its state and no commits, and listed in the output.
- Only the Orchestrator that owns the team runs it; a member cannot. A sub-orchestrator plans the sub-team it owns the same way,
  and passes `--team <its sub-team>`.
- A hand edit inside the Checklist block of a planned stream is overwritten by the next call, and `ah roster doctor` warns
  `checklist edited by hand; the plan wins` until then.

## Seeing where a stream is

- `ah roster stream-status` shows, per stream, the Done / In progress / Not started counts, the checklist's `Updated:`, the time of
  the newest journal entry, and `stale` when that entry is more than 30 minutes newer than the checklist's `Updated:`.
- `ah roster stream-done <name>` warns when the checklist still lists In progress items, then proceeds. It changes only the
  `State:` word of the checklist, and only if the checklist has a `State:`.
- `ah roster doctor` has a `progress` row: `ok`, or `warn` naming streams with no checklist or a stale one. It is never `fail`.
- The Orchestrator's Stop hook says, once for each (stream, report), that a stream got a report and its checklist was not
  updated. Members are never nudged.

## Turning it off and on

`ah progress off <team>` turns progress off for that team; `ah progress on <team>` turns it on. Both print
`{"team": …, "off": true|false}` and can be run again safely; `ah progress list` shows `off` per team. While it is off nothing
is created, journaled, nudged or warned about, and `doctor` shows the row as `off`.

```
ah progress off <team>
ah progress on <team>
```

A `progress/<team>/.off` file is ignored: the switch is set only by these verbs.

## The format, and how it grows

Every file the CLI creates starts with a version line: `<!-- ah-progress v1.0 kind=stream -->` in Markdown files,
`# ah-progress v1.0 kind=nudged` in `.nudged`, and `"v":"1.0"` on each line of `sessions.jsonl`. This is format 1.0.

- A file with no version line (a hand-written one) is read as 1.0 and is never given one.
- Readers skip what they do not know: an unknown heading, line, column, tag or JSON field is not an error. The checklist reader
  looks only at the three lists, `State:` and `Updated:`. Each item is a checkbox line, `- [ ] text` or `- [x] text`. A plain
  `- text` line in a list is not counted, and gets a warning naming its line. A `State:` or `Updated:` it cannot read counts as
  absent, with a warning.
- The CLI only appends, creates files, and rewrites the `State:` word at `stream-done`; every other byte of a file is kept.
  One exception: for a stream file whose version line is `<!-- ah-progress v1.1 kind=stream plan=jsonl -->` (format 1.1, a
  stream planned with `ah progress item`), the CLI also rewrites the Checklist block (from `## Checklist` up to the next
  `## ` heading) and the `Updated:` line; the rest of the file is kept. A 1.0 reader ignores the `plan=` word.
- A later minor version (1.x) only adds optional items. A file marked with a major version this build does not know (2.0 or
  later) is read but never written to, and `doctor` names it.
