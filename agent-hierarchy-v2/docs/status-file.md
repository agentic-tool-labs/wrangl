# The hierarchy status file

`ah` keeps one document describing the live hierarchy in a pool: its live teams, their members,
the work dispatched to them, and any `/wrangl:pipeline` run. Display surfaces (claude-tui-line's `ah`
items, the `ah` mod) read it and show it. They never work anything out for themselves.

- **Where:** `<hier>/status.json`, beside `msgs/` and `peers.jsonl`. The hierarchy dir's own
  `.gitignore` covers it. A linked worktree is its own pool: its file is
  `<worktree>/.claude/ah2/status.json`, and a worktree with no pool has no file. Nothing
  falls back to the main checkout's.
- **Who writes it:** only the status writer in `Ah.Hierarchy`. `ah roster status` computes it, writes it and
  prints it. A write is atomic (a per-process temp file, then a rename; the temp file is created exclusively, after anything already at its name is removed, so a write never goes through a link). Nothing is written when the
  hierarchy dir does not exist, and the dir is never created. A failed write never changes the
  outcome of the command or hook that triggered it.
- **Whose `enabled` an event write publishes.** `ah roster status` (which `/wrangl:hierarchy on|off` runs through)
  always publishes the `enabled` of the config of the process that ran it. Every other write is an event write,
  made by a hook or by a command that changed team state. An event write by a process whose own config is
  enabled publishes `enabled: true`. One by a process whose config is disabled keeps the `enabled` already
  published, when that document is readable and unexpired (the reading rules below) and has a boolean there;
  with no such document it publishes `false`. Teams, members and dispatches always come from the pool, never from
  the document on disk, so a disabled process still publishes every change at once.
  - Accepted limits. (C1) A disabled process can leave `enabled: false` where there was no usable document;
    the next event write from an enabled process publishes `true` again. (C2) Disabling the hierarchy by
    hand-editing a config, without `/wrangl:hierarchy off`, leaves the published view visible while event writes keep
    refreshing it; use `/wrangl:hierarchy off` or `ah roster status`. (C3) After `/wrangl:hierarchy off`, an event write from a
    process whose own config is enabled publishes `true`. (C4) A disabled event write can race an explicit
    `/wrangl:hierarchy off`: if the event write read `true`, then the explicit write saved `false`, then the event write's
    rename lands, the document says `true` again and later disabled event writes keep it. The window is
    milliseconds; re-run `/wrangl:hierarchy off` to recover.
- **Cost to read:** one `stat`, a read of a file usually under 8 KB, one JSON parse.

## Reading it

Every reader follows the same four steps.

1. Treat the document as absent if the file is missing, unreadable, over 256 KB, not JSON, has a
   `schema` that is not the integer `1` (see Schema), or `now ≥ expires_at`.
   - 256 KB is 262,144 bytes. A larger file is absent; a file of exactly 262,144 bytes is read.
     Check the size before parsing.
   - "Unreadable" means the path is not a non-empty regular file: it is
     itself a symbolic link (even one leading to a regular file), a
     directory, a FIFO, a socket or a device, or it is empty; or opening
     or reading it fails. Check with a stat that does not follow a final
     symbolic link, before opening the file, and never open a path that
     fails: opening a FIFO blocks, and a link can lead to a terminal. A
     reader that holds an open handle may check the handle instead. Only
     the last path component is checked.
   - It is also absent when a field the reader reads is missing, has the wrong JSON type, or, for a
     timestamp, does not parse as an ISO-8601 instant. The fields are `expires_at`;
     `member_sessions` (an array of strings); `timeline` (a non-empty array, every entry's `at`
     parseable); and, in the entry picked in step 2, `visible` (a boolean) and `tone`, `text` and
     `short` (strings). A missing `member_sessions` is therefore absent, not empty, so a malformed
     document shows nothing. A reader does not check fields it does not read, and ignores unknown
     fields.
2. Take the `timeline` entry current at `now`: the last entry with `at ≤ now`, or the first entry
   when `now` is earlier than all of them.
3. Show nothing if the viewing session's id is in `member_sessions`. Otherwise, when the
   document has `owners`, take the first usable `owners` entry whose `sessions` lists the viewing
   session's id and show only that entry: read its `timeline` instead of the top-level one, and
   only the `teams[]` objects whose `team` its `teams` names. Show nothing when no entry lists the
   session (this is a display filter, not access control: the file stays readable by every
   session). An entry is usable when `sessions` is an array of strings, `teams` an array of
   strings or `null`, and `timeline` passes step 1's check; skip the others. An `owners` that is
   not an array counts as an array with no usable entry. A document without `owners` was written
   by an older release: show the pool-wide view. Then show nothing if the entry has `visible: false`. A reader that shows a visible document which lists no team says so in a line of its own
   rather than drawing nothing, and a reader that has nothing to show may say why (no file, unreadable, expired,
   the hierarchy off, not visible, a member session, no team owned by this session). Member sessions see nothing; every other session in the checkout sees the
   view: the Orchestrator's, plain sessions, and `--agent` sessions on no team. The document does
   not name an Orchestrator, and `member_sessions` is the only signal, so a reader adds no rule of
   its own (hiding on the session's agent name, say, would also hide an Orchestrator launched with
   `--agent wrangl:orchestrator`).
4. Fall back on an enumerated value you do not know; never reject the document for one. An unknown
   `tone` string renders in the `idle` colour. A `tone` that is not a string is malformed (step 1).

A reader never re-derives eta, stall, liveness, counts or text. Changes that only depend on time
are already in the document as scheduled `at` instants, so the file does not need rewriting just
because time has passed.

## Schema (`schema: 1`)

Fields may be added without changing `schema`, and so may new values of an enumerated field such
as `tone` (readers fall back on them, step 4). Removing or redefining a field makes it `schema: 2`,
which today's readers treat as absent.

| Field | Meaning |
|---|---|
| `schema` | the JSON integer `1`, the only spelling `ah` writes. A reader that sees the number's text treats `1.0`, `1e0` or any other spelling as a different schema, so the document is absent. A JavaScript reader cannot tell them apart (`JSON.parse` gives the same number) and accepts them. The two differ only on a hand-made file. |
| `written_at` | when the document was computed (ISO-8601 UTC, ms) |
| `expires_at` | `written_at` + 24 h |
| `enabled` | the hierarchy's `enabled` setting |
| `member_sessions` | the session id of each Claude team member's latest roster registration, live or not, so a member's own session never gets the orchestrator view while it is marked gone; each id once; a session that belongs to no team is not listed |
| `teams[]` | one entry per live team |
| `teams[].team` | the team's name; `null` for the default team (`team.json`), shown as `default` |
| `teams[].members[]` | `name`, `role`, `label`, `kind`, `route`, `session_id`, `live`, `activity`, `activity_at`, `last_tool`, `last_tool_at`, `stream`, `focusable`, `blocked_by`, `blocked_note`, and `context_pct` when measured. Optional, absent from producers before this field set: `work_tree` (absolute path of the member's work folder), `branch` (short branch name read from that folder's HEAD file; `null` when detached or unreadable), `last_report_line` and `last_report_at` (the report line and time of its newest answered dispatch) |
| `teams[].members[].focusable` | `true` only when the team file's record for the member has `transport: herdr`, a non-empty `transport_id` and a name that passes herdr's agent-name rule; `false` otherwise. It decides only whether a reader may offer a click to focus the member's terminal pane, and a reader acts only on exactly `true` |
| `teams[].members[].stream` | the member's workstream from the team file (`spawn-one --stream`), cleaned and cut like a name; `null` for a member with none. A reader groups members by it |
| `teams[].members[].last_tool`, `last_tool_at` | a Claude member's last finished tool and when it finished: the tool name only, never its arguments, paths or output; `null` for a member with none and for a pane member |
| `teams[].members[].context_pct` | not written in this release: a transcript does not carry the context window size, so there is no percentage to record (a member's context is in tokens in its activity record, and the terminal view shows them). A reader shows the value after the member's activity when a document carries it |
| `teams[].members[].route` | how `ah` reaches the member: `pane` only for a pane-driven (non-Claude) member, else `peer`; a Claude member running in a pane reads `peer` |
| `teams[].dispatches[]` | `id`, `slug`, `to`, `to_name`, `member`, `label`, `eta`, `eta_ms`, `created`, `sent_at`, `checkins`, `reported_at`, `states[]`. Optional, absent from producers before this field set: `title` (the request's title, one line, at most 120 characters), `title_source` (`frontmatter` when the request wrote it, `auto` when it was derived), `item` (the checklist item `<stream>/<id>` from the request), `report_line` (the answer's `## [0] tldr` `- [1] status:` bullet once a response exists, at most 160 characters). Text fields have control and escape characters removed and are cut with an ellipsis. The schema stays 1 |
| `teams[].dispatches_truncated` | how many dispatches were left off the list (it holds at most 50) |
| `teams[].pipeline` | `null`, or `{anchor_id, started, round_cap, waiting_on_user, items[]}`; an item is `{slug, rounds, open, to}` |
| `teams[].resources` | absent when empty, else the live named-resource holds and waits of the team: `{name, role, who, since, note}` with `role` `hold` or `wait`, `since` in ms. It lists rows whose `who` is a member of the team, every hold by `user`, and the hold of a name a member waits on that is held outside the team, marked `outside: true`. A hold whose holder process is gone is left out |
| `timeline[]` | `{at, visible, tone, live, out, blocked, overdue, stalled, text, short}`, sorted by `at`; the first `at` is `written_at`. Pool-wide: it counts every live team |
| `owners[]` | one entry per orchestrator process that owns at least one live team in the pool. Every `teams[]` object is in exactly one entry. The top-level keys are unchanged and stay pool-wide |
| `owners[].sessions` | the session ids recorded for the owning process (the state database's `session_pids` table, written at every session start and by a team-writing command run from that process), plus the `orchestrator.session_id` of each of its team files when set; each id once, newest first, at most 32; `[]` when none is known, and then no session sees those teams |
| `owners[].teams` | the `team` value of each of its `teams[]` objects (`null` for the default team) |
| `owners[].timeline` | the same shape as `timeline[]`, counting only its own teams |

Every string read from a file is cleaned before it is written. Escape sequences and C0/C1 control
characters are removed. Names and labels are cut at 64 characters, slugs at 32 and notes at 80,
with no ellipsis added.

## Rules

`T` is a dispatch's eta threshold: small 5 min, medium 10 min, large 20 min. An absent or unknown
eta counts as small. The thresholds and the check-in cadence are the same constants the Stop hook's
liveness check-ins use.

### Teams and members

- **Teams:** only live teams are listed. A team is live when its orchestrator's pid is alive and the
  team file is at most 24 h old. Which session writes the document makes no difference.
- **Members:** every member of a listed team; the orchestrator is not one.
- **`live` for a Claude member (`route: peer`):** the liveness of its attributed `peers.jsonl` row
  (`up` with a live pid, or `seen`/`briefed` within 30 min). With no row it is `false`.
- **`live` for a pane member (`route: pane`):** `false` when its activity record is `unknown`
  (Herdr no longer has the agent), `true` for any other record, and `null` when it has none.
- **`activity`:** from the member's record in `<hier>/activity/`. It is `unknown` when there is no
  record. `failed` means its last turn ended on an API error: the record then also holds `error` (the kind the
  harness named, or `unknown`), `error_details` (one line, at most 300 characters), `failed_at`, `source`
  (`stopfailure`, or `transcript` when the daemon found it from the transcript) and `streak` (one plus
  the automatic resumes already sent to that session in the previous 45 minutes; a normal end of turn clears the
  displayed value, not the count behind the next failure's streak). `failed` is not shown as `blocked`; a reader that
  does not know it sees an unfamiliar activity. A member with `live: false` is shown as gone.
- **`last_tool`:** written by the member's PostToolUse hook, at most every 15 s while its activity is unchanged. It is the last
  *finished* tool that won a write, so it can lag by up to 15 s plus one tool, and a long-running tool leaves the previous one
  showing with a growing age. It is kept when the member goes idle; a reader shows it only while the member is `working` and
  `last_tool_at` is not before `activity_at`.
- **`label`:** the member's role, unless another live member of the team has the same role; then
  its name.

### Dispatches

A dispatch is a request addressed to a member (not to `orchestrator`) that has been sent: it has a
dispatch row, written when the Orchestrator SendMessages it or runs `ah roster deliver`.

- **Included:** dispatches that are open, plus those reported within the last 10 min. A request that
  was written but never sent is not included.
- **Excluded:** a request whose team is not live. Orchestrator-addressed exchanges (the pipeline's
  run records) are never dispatches.
- **Clock:** `sent_at` is the earliest dispatch row for the request, from any session, and every
  elapsed time is measured from it. If every row has an unreadable time, the request's own `created`
  is used instead.
- **Reported:** the exchange is closed under the one open rule: its response holds an authored
  report, or has a broken frontmatter. `reported_at` is the response file's mtime.
- **`member`:** the team member named by `to_name`; failing that, the single member whose role is
  `to`; failing that, `null`. With no member, `label` is `to_name`, else `to`.
- **`checkins`:** the number of liveness check-ins the Stop hook has sent for the request.
- **Order and size:** stalled, blocked, overdue, working, reported, then oldest `sent_at` first. At
  most 50 per team.

`states` comes from the first of these rules that applies when the document is written:

| # | Condition | `states` |
|---|---|---|
| 1 | reported | `reported` at `written_at`, then `expired` at `reported_at` + 10 min |
| 2 | the member exists and `live` is `false` | `stalled` at `written_at`, reason `member-gone` |
| 3 | the member's activity is `blocked` | `blocked` at `written_at` |
| 4 | otherwise | `working` at `sent_at`, `overdue` at `sent_at + T`, `stalled` at `sent_at + 1.5T`, reason `no-report` |

Rule 4's instants are the first two check-ins of the cadence. A late check-in does not move them.
Blocked wins over time: once the member is unblocked, the next write goes back to rule 4.

### Pipeline

- **When present:** a team's `pipeline` is set only when that team has exactly one open
  `pipeline-run-anchor`.
- **`started`:** the anchor's `created`.
- **`round_cap`:** 3.
- **`waiting_on_user`:** the run's parked decision count.
- **`items`:** one per distinct slug among the team's member-addressed exchanges created at or
  after `started`, leaving out `dec-` slugs.
  - `rounds` counts every exchange in the pool with that slug, open or closed, at any time.
  - `open` is true when any of them is open.
  - `to` is the newest one's `to`.
- **Item order:** newest first, at most 20.

### Timeline

The counts are evaluated at `written_at` and at every later instant in any dispatch's `states`. An
entry is emitted each time they change.

- **`live`:** members whose `live` is not `false`.
- **`blocked`:** those live members whose activity is `blocked`.
- **`out`:** dispatches not `reported` or `expired`.
- **`overdue`, `stalled`:** dispatches in that state.
- **`text`:** `{live} live · {out} out`, then `· {n} blocked`, `· {n} overdue` and `· {n} stalled`
  for each that is non-zero. For example, `3 live · 1 out · 1 blocked`.
- **`short`:** `{live}/{out}`, then ` {n}b`, ` {n}o` and ` {n}s` for each that is non-zero. For
  example, `3/1 1b`.
- **`tone`:** `bad` if overdue + stalled > 0, else `warn` if blocked > 0, else `work` if out > 0,
  else `idle`.
- **`visible`:** `enabled`, and either something is live or out, or a team has a pipeline.

## `ah roster status`

```
ah roster status [--plain] [--now <ISO>] --cwd <abs>
```

It prints the document as JSON. `--plain` prints `ah · <text>` for the entry current at `now`, or an
empty line when that entry is not visible. `--now` computes the document as of that instant, for
tests. Liveness (pids, team age) is still read at the real current time. With no hierarchy dir it
prints a document with `teams: []`, writes nothing and exits 0.

## Fixtures

`tests/fixtures/status/` holds eight status documents. They are the shared test data for every
reader. Each reader keeps its own expected outputs next to its own tests.

| Case | What it shows |
|---|---|
| `idle` | two live members, nothing out; one entry, tone `idle` |
| `work` | one dispatch whose timeline runs working → overdue → stalled (tones `work`, `bad`, `bad`) |
| `warn` | a blocked member, with its dispatch held at `blocked`; tone `warn` |
| `bad` | one overdue and one stalled dispatch, the stalled one with a check-in sent; tone `bad` |
| `hidden` | the `work` pool with the hierarchy disabled; every entry `visible: false` |
| `pipeline` | a named team with an open run: one item over two rounds, and a parked decision |
| `member-session` | a live Claude member, so `member_sessions` is not empty; its reader must show nothing |
| `foreign` | the `work` pool, owned by a process that no recorded session belongs to; the viewing session owns nothing, so its reader must show nothing |

Every case but `foreign` lists the viewing session `sess-orch` as the owner of its teams.

Each case is exactly what `ah roster status --now 2026-01-01T12:00:00.000Z` prints for a pool that
`tests/test-status-fixtures.sh` stages. That test regenerates every case and fails unless each
matches the committed file byte for byte. A rule change that alters a document therefore fails it.
Run `AH_UPDATE_FIXTURES=1 bash tests/test-status-fixtures.sh` to regenerate the files, then update
the readers' expectations. The fixtures hold no absolute paths, real session ids or real names.

## The mod as a mail adapter

Besides reading this document, the Claude mod (`mod/register.tsx`) is the `claude-mod` pull adapter for the
mailbox (see `docs/comms-protocol.md`). The terminal view (`ahd tui`) does not read this document: it uses a separate, hidden daemon route, `GET /v1/view`, with its own schema (`view_schema`). In an interactive session with a registration
(`<AH_HOME>/registrations/<session id>.json`) it asks the native CLI what mail is due and hands the answer
to the model. The mod holds no mail rules of its own and imports no ah library code:

- **Push, when the daemon is up.** The mod sends one `GET /v1/due` over the daemon socket and the daemon holds it
  until something is due (or about 25 s), checking the session's boxes and registration five times a second; the
  mod sends the next request as soon as it has handled an answer, so mail arrives within about a fifth of a
  second and nothing is started or polled. The socket is only ever the one a CLI answer reported after the
  plugin's own home check, never one taken from the environment. A request too large for the daemon's request
  line goes through the CLI instead and the mod stays connected.
- **Timer, otherwise.** Every 5 s the mod runs this plugin's own `bin/ah msg due --json` with the request on stdin
  (the session, the claim map and the cursor it was last given). The verb asks the daemon's `GET /v1/inbox` for each box
  when it answers and reads `entries.jsonl` and the `acks/` files itself when it does not, writing the
  `claude-mod` heartbeat in the session's own mailbox directory. An answer that names a live daemon moves the mod
  to push; any failure in push moves it back, and it waits 5, 10, 20, 40, then 60 s before trying push again.
- Everything due goes to the model in **one** `$.prompt.submit`, oldest first, with one `[ah-entry <id>,…]`
  marker line. ah's `UserPromptSubmit` command hook sees that prompt when its turn starts and acks those ids,
  so an ack means the text entered a turn. The mod never acks.
- A first hand-over is not gated on the session being idle; only a repeat waits for idle. The re-submit clock starts when the submit resolves (its turn started), not at hand-over: an
  id whose submit is still pending is never handed over again, and one whose submit resolved 60 s ago, still
  unacked with the session idle, is handed over once more. Only the submit that owns an id's record changes it: a
  rejected submit puts the id back in the state it had (a fresh id is due at the next request), and the only thing that
  releases a record is an answer that lists the id as acked (an ack file exists); an answer that lacks the id or a failed read
  releases nothing. The prompt hook blocks a mail prompt that acks nothing
  new when every id it names already has a `prompt-hook` ack, so a copy that slips through enters no second turn.
  A registration naming several addresses (an Orchestrator owning several teams) is asked for every one.
- It reads this document from `ahd` (`GET /v1/status`, over the same CLI-reported socket) and falls back to
  `status.json`; the band and Pane are identical either way. A `-p` or SDK session does none of this.

The `UserPromptSubmit` command hooks of the same plugin see a plugin-submitted prompt like any other, so a mail
turn is tracked like any prompt. Residual risk: a submit that is dropped while
the session is idle leaves its entry unacked until the re-submit rule above hands it over again.

## Live updates through `ahd`

`GET /v1/status?dir=<abs hierarchy dir>&since=<rev>&wait=<s>` on the daemon socket returns
`{rev, doc}`: `doc` is the parsed `<dir>/status.json` and `rev` rises each time the file changes (it
starts from the file's mtime in microseconds, so a restarted daemon never reports a lower one). With
`since` equal to the current `rev` and `wait` from 1 to 25, the request is held until the next change.
`wait` without `since` is `bad_request`. Only the hierarchy dir of a live registration that has a
`status.json` is served; any other dir, a deleted file or an unreadable one is `404 not_found`. The
daemon never composes or writes the document: the status writer stays the only writer, and the file
stays the fallback for readers without the daemon.
