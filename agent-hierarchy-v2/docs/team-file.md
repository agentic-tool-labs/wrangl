# The team file

A message's frontmatter names two absolute paths: `team_file`, the live Team this
exchange belongs to, and `team_guide`, this document. Read this when you hold a
`team_file` path and need to know what to do with it.

**To see the record, run `ah roster team show [--team <name>] [--cwd <dir>]`; never open
the path.** Once a home's teams are on the state database (`authority.teams=db`), the
database holds the record and the file is only an export of it.

## What it is

One record per live Team, named by the path `<hierarchy dir>/teams/<name>.json`, or the
legacy `<hierarchy dir>/team.json`. It is the authoritative record of who is on the Team:
the orchestrator that owns it and every member, under the names they are addressed by.
The message pool is its sibling: `<hierarchy dir>/msgs/`.
With no `--team`, the default team file is `teams/<repo basename>.json`, and peer dispatch resolves names from the team file first.

## Where it lives

A team file has one home: the hierarchy dir of the repo's **main checkout**. A team spawned
from a linked worktree is recorded there too, whatever `--cwd` the spawn was given, so a team
whose members run in the main checkout and in worktrees is still one file. Under
`WRANGL_HIERARCHY_DIR`, or outside git, the home is that dir. `msgs/`, `peers.jsonl`, gates and
status stay per pool.

A `session_pids` row (state database) is written for the pool **and**, from a linked worktree, to the main checkout's dir: a session
records its row at SessionStart and again at every prompt (skipped when the session's newest row already has the
same pid), and the main checkout's `status.json` is refreshed too. The Pane reads the `status.json` of the pool the
session works in first, since its exchanges and dispatches are that pool's, and looks at the main checkout's only
when the pool's document is absent, expired, unreadable or does not list the viewer as a member or an owner.

A team recorded in a worktree's own dir by an older release is **legacy**: it is read and
updated where it is (never moved), and teardown and `teams` also read it when a team has
records in both places. Worktrees of one repo share team names, so a name that is already live in
a sibling worktree is refused or collides like any same-checkout name.

The path in the message is absolute on purpose. Every other way of finding the team
file derives it from the session's cwd, and a cwd inside a git worktree or another
repo resolves to a different hierarchy dir with no Team in it. **When the cwd-derived
answer and `team_file` disagree, `team_file` is right.**

## What is in it

| field | meaning |
|---|---|
| `team_id` | identifies this Team instance |
| `orchestrator.pid` | the owning session's process; dead pid = orphaned Team |
| `expected_root` | the directory the Team was created from; a member's own `expected_root` (below) takes precedence for that member |
| `roster_level` | which roster level the Team was stood up from (`null` for an ad hoc one) |
| `roster` | which roster block it was built from: `rosters.<roster>`, or `null` for the default `roster` block (and for a Team replayed from history). A file without the field predates it and is read by the old rule, `rosters.<team name>` then the default |
| `layout` | the Team's pane layout, `auto`, `columns` or `grid`; later `spawn-one` panes use it. A file without it uses `auto` |
| `transport` | `herdr`, `tmux`, or `terminal` |
| `partial` | true when some member never checked in — the Team is degraded |
| `members[]` | one row per member: `role` (a built-in, or a custom role name from `ah roster role list`), `name`, `route`, `model`, `effort`, `autoMode`, `transport_id` (its pane; the delivery ladder's paste types only into this recorded pane and never searches for another), `expected_root` (the directory this member was launched in; a row without it uses the team's); `kind` and `args` only for a non-Claude member; `kind: "custom"` members (an agent runtime ah has no adapter for, admitted with `spawn-ad-hoc --kind custom`) carry `route: "mail"`, `harness` (a lowercase name of 2 to 32 characters: letters, digits and hyphens, starting with a letter), and optionally `command` and have no model, effort or args; `appserver_pid` and `appserver_thread_id` (the thread the spawn created on that server, which `ahd` resumes; a row without it is not adapted) only for a Codex member on the `herdr` transport that was launched attached to its own app-server; `adapter` is the member's own setting copied as it was (`"appserver"`, `"off"` or absent, set with `ah roster add/edit --adapter`), so a member that got the adapter by default has none, or `adapter_note` saying why a member that asked for it launched without (a launch argument outside the adapter allow-list is named without its value; the allow-list is `--model`, `--sandbox` workspace-write or read-only, `--ask-for-approval`, `--add-dir` for a path unrelated to ah's home, and `-c` for `model_instructions_file`, `approvals_reviewer` or `model_reasoning_effort`, each at most once except `--add-dir` and `-c`; an adapter member's terminal and server run with the sandbox network off and no extra writable roots whatever its Codex config says, so extra writable directories go in `args` as `--add-dir`); `tab_id`/`workspace_id` under herdr; `stream` (a `streams` key) only for a member spawned with `--stream`; `paste: false` (optional) opts the member out of rung-4 paste, so a message to it escalates instead of being typed into its pane `args` are stored in the team row and visible in the process list; keep credentials in the environment or Codex's own config, not in `args`. |
| `streams` | only once a stream is opened: `{<name>: {state, branch, base, worktree, tab_id, opened, closed?}}` — `state` is `open` or `done`; `branch`/`base`/`worktree` are null for a `--no-worktree` stream; `tab_id` is null off herdr or when no tab could be opened; `closed` is present only while done. Absent means no streams |
| `parent` | only in a sub-orchestrator's team: `{team, stream}`, the manager team and the stream this team does the work of. Set by `create --parent <X> --stream <ws>`; its presence makes the team a child team |
| `parent.team_id` | only in a child team made after the field existed: the `team_id` of the manager team when the child was created. The parent owner's staffing verbs (`spawn-one`, `spawn-ad-hoc`, `dismiss`, `move`) accept the child only while this equals the manager's current `team_id`, because `disband` and `reap` of a manager leave its children behind. A child without it is accepted only when both teams have `created` and the child's is not earlier than the manager's |
| `members[].session_id` | the Claude session of the process now holding the row. Written at that member's SessionStart, in the same write as `pid`/`pid_start` and only when the pid is accepted; a `/clear` overwrites it. `ah roster resume` uses it to relaunch the member on its own session (`--resume <id>`) when no snapshot or checkpoint has the id, as after a restart |
| `members[].placed_by` | on a row the owner of the manager team wrote into a lane's child team: `orchestrator@<manager>`. Absent means the child's own orchestrator placed the row |
| `parent_actions` | only in a child team the manager's owner has acted on: the last 20 actions, newest last, each `{verb, member, by, at, stream?}` with `by` = `orchestrator@<manager>`. Written in the same update as the row change, so it holds the action even when the lead's `parent-action` event could not be delivered; the row of a dismissed member is gone, so this is the only record of it |
| `workstream` | only in a child team: `{state, summary, updated, result?}`. `state` is `working`, `blocked`, `review` or `done`; `summary` is one line of at most 160 characters; `result` is the absolute path of the response file, only with `done`. Written by `ah roster stream-report`, which replaces the whole record |
| `streams.<ws>.sub_orchestrator` | in the manager's team: the member name of the sub-orchestrator running that stream. Written by `spawn-one sub-orchestrator --stream <ws>` in the same write as the member row. The mesh gives the leader of a lane automatic messaging (to sibling lane leaders and to its own lane's members, without a link) only when this member's role is a built-in orchestrate-class role (`sub-orchestrator`); a lane leader with a custom orchestrate-class role gets none automatically and needs a link |
| `max_lanes` | the most live `sub-orchestrator` members the team may have: an integer from 1 to 16, default 6 when absent. Set by the top Orchestrator's `ah roster lane-caps --max-lanes N` (team-wide; refused with `--stream`). When the team record has none, the `max_lanes` of the roster block the team was built from applies (read live, never copied into the team record; a named roster's own block for a team built from it, the unnamed block otherwise); else 6. A recorded value outside 1..16 or not a whole number, at either level, is skipped (the next level applies) and `ah roster doctor` warns naming the block and file. Lowering it below the live count dismisses no one; it refuses the next sub-orchestrator spawn. |
| `streams.<ws>.lane` | in the manager's team: `{ "max_members": N, "max_streams": M }`, the caps of the lane the sub-orchestrator runs. Written by `spawn-one sub-orchestrator --stream <ws> [--lane-members N] [--lane-streams M]`, in the same write as `sub_orchestrator`, and changed afterwards only by the top Orchestrator's `ah roster lane-caps --stream <ws> [--members N] [--streams M]` (1..16; a lane leader cannot run it; lowering a cap dismisses no one, the next spawn is refused); read from here through the child team's `parent`, never copied into the child. Absent in a team written before lanes: 4 members and 8 streams apply. The lane base is the stream's `base` |
| `members[].lead` | on a member row of the team that lent it: `{team, name}`, the lane team it works for and that lane's sub-orchestrator. Written by `ah roster transfer <member> --to <lane>`; the row stays in this (home) file, exactly one row, and the lane team's `members[]` is untouched. It does not count toward the lane's member cap. Views derive two markers from it and write neither: `ah roster teams` shows `lent_to` on the home team and `lent_from` on the lane team, and `ah roster doctor` reports `lent-member` and, when the lane is missing, closed or no longer run by the named sub-orchestrator, `lead-dangling` (mail then falls back to the home Orchestrator) |
| `streams.<ws>.close_grants` | in the manager's team: the close green lights the manager recorded for that stream, newest 20 kept. Written only by `ah roster close-approve` |
| `links` | not a field of this file: the links the Orchestrator granted between members of this team are kept beside it, in `links/<team>.json` in the same hierarchy directory |
| `mesh` | optional, how members may message each other: `{policy, auto_grant}`. `policy` is `star` (the default when the field is absent: members talk to their Orchestrator, and to a peer only through a link the Orchestrator granted), `star-warn` (the same rule, but a send that `star` would refuse is delivered, logged as `would_refuse`, and, for a peer send without a link, mirrored to the task; a member broadcast to `*` and a send over a spent link are still refused) or `open` (no checks). `auto_grant` is a list of `[role, role]` pairs whose link requests the daemon grants itself; absent means implementor↔reviewer and architect↔implementor, `[]` means none. Written by hand; `ah roster` passes it through unchanged. A team created before 0.131 has no `mesh` field and so gets `star`; see the 0.131.0 upgrade notes in [CHANGELOG.md](../CHANGELOG.md). See `comms-protocol.md` |

`members[].name` is the address: it is the `to` of a SendMessage and the `--to-name`
of `ah msg new`. It is `<team name>-<role>[-N]`: for `teams/<name>.json` the team name
is `<name>`; the legacy `team.json` records none, so its name is read off its own members
(the first whose name ends in `-<its role>`), never recomputed from config.

## How a session knows its Team

Every member the launcher starts is given this file's absolute path as `WRANGL_TEAM_FILE`
(through `claude --settings`, so its hooks and its Bash commands all see it). A session's
Team is resolved in this order: the Team it owns as orchestrator; else `WRANGL_TEAM_FILE`,
accepted only when it names `<hierarchy dir>/teams/<name>.json` or `<hierarchy dir>/team.json`
of this repo (or its main checkout); else the one live Team whose member row holds this
session's pane. The launcher also sets `WRANGL_EXPECTED_ROOT`, the directory the member was launched in. A member's session
resolves its expected root as: its member row's `expected_root`, else `WRANGL_EXPECTED_ROOT` (when it knew its
Team from `WRANGL_TEAM_FILE`), else the team's. The env value covers the first `SessionStart`, which can fire
before the orchestrator has written the member row; without it a main-checkout member of a team created from
a worktree could be flagged as misplaced. `whoami` says which answered (`answered_by`) and reports a rejected
`WRANGL_TEAM_FILE` in `env_team_invalid`, as another repo's team file or as malformed.

The launcher also sets `AH_MEMBER_NAME`, the member's own name, in every launch shape: in the `--settings`
environment of a claude member, in the environment prefix of a custom member's command, and as a
`shell_environment_policy.set` setting of a Codex member. A process that carries it is a member whatever its
process ancestry says; the owner-only verbs and the worktree gate read it before they look at ancestry.

## Snapshots

`ah roster snapshot` (and `disband --close`, and the Orchestrator's SessionEnd) writes the team's members to
`<hierarchy home>/snapshots/<team>-<ts>.json` so `ah roster resume` can bring them back into their sessions
after a reboot or a closed terminal. It does not change the team file's format: a snapshot is a separate file
(`{v: 1, kind: "ah-team-snapshot", team, team_id, taken_at, transport, layout, members: […]}`), written through
the `team` state domain, and the newest 10 per team are kept. A member row's `session_id` is `null` when no
live registration or peers row knew it; `resume` relaunches such a member fresh and says so. Codex members are
recorded but relaunched fresh until the Codex app-server resume exists. The default team's snapshots are named
`team-<ts>.json`.

## What to do with it

- **Read it with `ah roster team show`; never edit it.** Every write goes through `ah roster` (`spawn-one`,
  `spawn-ad-hoc`, `dismiss`, `disband`, `untrack`, `resync`, `move`, `adopt`,
  `stream-open`, `stream-done`; `stream-label` and `stream-status` only read it).
- **Find a teammate's name or role:** `members[]` in `ah roster team show`.
- **Find who to reply to:** the brief's `reply-to` is authoritative. If it is lost,
  `ah roster whoami` derives the orchestrator's address from this record.
- **The state store is busy:** a read says `state store busy, try again` and does nothing.
  That is not a missing or damaged Team; run it again.
- **Run a CLI verb against this Team from a drifted cwd:** pass
  `--cwd <expected_root>` so the verb resolves the same hierarchy dir this file is in,
  plus `--team <name>` when the file is `teams/<name>.json`.
- **Reply to a request:** `ah msg fill --id <id> --from <your role> --body-file -`, then `ah msg send <orchestrator address> --kind report --ref <id>`
  writes the response beside the request, in this Team's pool, whatever your cwd is.
- **A gate says it found no team record, or that a file is outside the message pool:**
  compare the directory it names with the directory of `team_file`. If they differ,
  your cwd has moved; the message and the Team are fine.

`team_file: null` means the message was written where no team file existed — an
exchange outside any Team. There is nothing to look up.
