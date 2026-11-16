# How Wrangl fits together

This page is for anyone who wants to know how the pieces of Wrangl work together: what runs where,
where each piece of state lives, how a message gets from one agent to another, and where each feature lives
in the code. It describes the plugin as it is today.

If you only want to use the plugin, start with [getting-started.md](getting-started.md); [how-it-works.md](how-it-works.md)
explains the roles, briefs, gates and recovery in plain terms. The daemon's user guide is [daemon.md](daemon.md),
every daemon route is in [daemon-api.md](daemon-api.md), the command reference is [cli-tools.md](cli-tools.md), and
the exact message rules are in [comms-protocol.md](comms-protocol.md).

Paths in `code` below are relative to the plugin directory (`agent-hierarchy-v2/`).

- [The two layers](#the-two-layers)
- [The programs and how they connect](#the-programs-and-how-they-connect)
- [The hook host](#the-hook-host)
- [Where state lives](#where-state-lives)
- [The route table](#the-route-table)
- [Mailbox and delivery](#mailbox-and-delivery)
- [The peer mesh](#the-peer-mesh)
- [Custom harnesses: the participation contract](#custom-harnesses-the-participation-contract)
- [The Codex app-server adapter](#the-codex-app-server-adapter)
- [Context alerts](#context-alerts)
- [Status and views](#status-and-views)
- [The command surface](#the-command-surface)
- [The safety model and its limits](#the-safety-model-and-its-limits)
- [How it is tested](#how-it-is-tested)
- [Where each feature lives](#where-each-feature-lives)

## The two layers

Wrangl has two layers that share one set of files.

```
  each Claude Code session                          once per user
 ┌────────────────────────────────────┐          ┌──────────────────────────┐
 │ hooks: gates, prompt, stop, ...    │  unix    │ ahd  (the daemon)        │
 │ CLIs:  ah roster, ah msg, ahd      │ ───────▶ │ optional; started on     │
 └─────────────────┬──────────────────┘  socket  │ demand, exits when idle  │
                   │                              └────────────┬─────────────┘
       reads always│direct; writes go direct                   │ reads and writes
       when no daemon answers                                  │ the same state
                   ▼                                            ▼
 ┌─────────────────────────────────────────────────────────────────────────────┐
 │ the state database (source of truth): teams, members, sessions, messages,   │
 │ mailboxes, peer records; files on disk: links, status document, snapshots   │
 └─────────────────────────────────────────────────────────────────────────────┘
```

**The session layer** is what Claude Code runs inside each session: the hooks registered in
`hooks/hooks.json` (the PreToolUse gates on `Agent`, `SendMessage`, `Bash` and others; the SessionStart,
UserPromptSubmit, Stop and SessionEnd hooks) and the command-line tools the roles call (`ah roster`,
`ah msg`, `ahd`, and the other `ah` verbs in [cli-tools.md](cli-tools.md)). It is short-lived: each hook runs,
decides, and exits. A plain top-level session stays bare, with no context and no nudges, until it opts in: by
`/wrangl:orchestrator`, by any Wrangl command, skill or `ah` call of its own, or through the `alwaysOrchestrator` setting.
Role sessions, team members and team owners are always in. `OrchestratorIdentity.In` is the one predicate.

**The daemon layer** is `ahd` (the native daemon host, `Ah.Daemon`), one long-running process per OS user. It listens on a unix
socket, `<ah home>/run/ahd.sock`, and serves a small HTTP/JSON interface over it. It is what lets mail reach a
session as soon as it is sent, walks undelivered mail down the delivery ladder, keeps dispatch timers, ends
links on time, admits custom harnesses, and serves the terminal view.

**The files are the source of truth, and the daemon is optional.** The daemon keeps no durable state of its
own: everything it knows it reads from, or writes to, the same files the session layer uses, so a crash or a
restart loses nothing. Every client treats "no daemon" as an ordinary answer and falls back to the files:

- Gates never ask the daemon. They read the state files directly, so a gate's verdict does not depend on
  the daemon being up.
- `ah msg send`, `inbox` and `ack` try the daemon first. When it does not answer they print one warning line,
  use the mailbox files directly, and record the fallback in `<ah home>/logs/fallback.jsonl`.
- State writes go through the daemon when it is up and run directly otherwise (see
  [Where state lives](#where-state-lives)).
- `AH_DAEMON=off` in a command's environment keeps that command off the daemon entirely.

What genuinely waits for a daemon is the work only a long-running process can do: the delivery ladder, links
lapsing on their own, joins by custom harnesses, and the terminal view.

### Starting and stopping

Nobody has to start the daemon by hand. A session that has a mailbox address (an Orchestrator or a team
member) starts it at SessionStart or when it opts in, with a short time budget, and carries on without it if that fails. `ahd
start` and `ah msg`'s mail commands also start it. The state hub never starts it: it uses a daemon that is
already running, or the files.

- **One per user.** A start takes `<ah home>/run/ahd.lock`. The running daemon writes `<ah home>/run/ahd.json`
  (pid, version, socket) and checks every second that this file still names it and that the socket is still
  the one it bound; if not, it exits at once without removing anything, so two daemons never fight over the
  socket.
- **A clean environment.** The daemon is started detached with only `PATH`, `HOME` and `AH_HOME` (plus the `AHD_*`
  settings and test variables), and its output goes to `<ah home>/logs/ahd.log`.
- **Idle exit.** It exits by itself after 30 minutes with no requests, no pending timers and no live
  registrations. A stop drains in-flight work for up to five seconds, then removes its socket and run file
  only if they are still its own.
- **The ah home** is `$AH_HOME`, or `~/.claude/ah2/` when that is unset. It holds `run/`
  (the socket, lock, run file, band token and browser-page record), `logs/`, `state/`
  (the SQLite database and its backups, see below), `recover/` (the switch and records for relaunching sessions ah
  did not start), `wake/`, `overdue/` and `stall/` (the Orchestrator's wake-up and alarm bookkeeping), `bin/` (the
  built program) and a few per-user files.

## The programs and how they connect

Wrangl is one native program written in C# (.NET 10), split into projects under `native/src/`. One executable, built
from `Ah.Cli`, is called `ah` or `ahd` depending on the name it is run as. The shell shims `bin/ah`, `bin/ahd` and
`bin/ah-hook` find the built program in `<ah home>/bin/` and run it. A second program, `ahd-tui`, draws the terminal
view; `ahd tui` runs it from beside the main program.

| Project | What it is for |
|---|---|
| `Ah.Abstractions`, `Ah.Core` | shared interfaces and pure helpers: JSON, text, time and liveness rules, shell parsing |
| `Ah.Platform` | operating-system adapters: starting processes, the unix-socket HTTP transport, peer credentials, locks, file I/O, trust checks |
| `Ah.Config` | configuration and the role registry (which roles exist and their classes), the text injected at session start |
| `Ah.Roster` | team files, members, work-tree isolation and permissions |
| `Ah.Hierarchy` | everything in the hierarchy directory: message files, the status document, streams and lanes, the progress plan, the QA queue |
| `Ah.Mesh` | mailboxes, the delivery ladder, the mesh rule, links, joins, the route table and contract |
| `Ah.Codex` | the Codex app-server adapter |
| `Ah.Launch` | placing and starting panes, the hardened paste, teardown |
| `Ah.Watch` | the daemon's watchers: dispatch clock and alarms, stuck-member and relaunch handling, the view builder |
| `Ah.State`, `Ah.StateDb` | the state hub, and the SQLite state database with its backups |
| `Ah.DaemonClient` | finding, starting and talking to the daemon |
| `Ah.Daemon` | the `ahd` server: the unix-socket HTTP server, the route registry, state routes, and the browser bridge |
| `Ah.Commands` | the `ah roster`, `ahd` control, state and migration verbs |
| `Ah.Hooks` | the Claude Code hook units and the gates |
| `Ah.Cli` | the one entry point that picks hooks, `ah` or `ahd` |
| `Ah.Tui` | the terminal view program |
| `Ah.Generators` | a build-time code generator for the route registry |

Dependencies run one way. `Ah.Abstractions` and `Ah.Core` depend on no other project. The domain projects
(`Ah.Roster`, `Ah.Hierarchy`, `Ah.Mesh`, `Ah.Watch`) build on those and on each other in that order. `Ah.Daemon`,
`Ah.Commands` and `Ah.Hooks` sit above the domain projects and call into them, and `Ah.Cli` ties them together.
`Ah.Tui` is separate: it knows the daemon only through `Ah.DaemonClient`.

### The hook host

`hooks/hooks.json` registers the plugin's hooks, about sixty entries across the Claude Code events (`SessionStart`,
`PreToolUse`, `PostToolUse`, `UserPromptSubmit`, `Stop`, `SubagentStart`, `SubagentStop`, `PermissionDenied`,
`StopFailure`, `Notification`, `SessionEnd` and `PostToolUseFailure`). Each entry runs `bin/ah-hook <name>`, which
runs `ahd hook <name>`: one short program run per hook call, not a request to the daemon. For a session that has not
opted in, the hooks that talk to the model stay silent and register nothing; `posttooluse-implicit-opt-in` and the
prompt hook are what opt it in, and the delivery goes through the same path as SessionStart.

- **The entry checks the binary first.** `bin/ah-hook` runs the built program only from the default home, or from a
  home you listed as trusted (`ahd home trust`). With no usable program, every gate refuses with a message telling
  you to run `scripts/install.sh --apply --auto-rebuild`, and other hooks stay silent but log the error.
- **Gates read files, not the daemon.** A gate is a `PreToolUse` hook that can refuse a tool call. It reads the
  state files directly, so it works with the daemon down.
- **Failing closed, mostly.** If a gate cannot decide (bad input, an exception, or more than 15 seconds), it refuses.
  Seven gates let the call through instead and log the error: the question gate, both work-tree gates, the message
  gate, the report check, the `ah` command allow check and the unwrapped-test gate. Hooks that are not gates are
  silent on failure and write a row to `hook-errors.jsonl`.
- **What the gates are** is described in [how-it-works.md](how-it-works.md#what-keeps-members-in-bounds). The one with its own page
  is [pipeline conventions](pipeline-conventions.md).

## Where state lives

There are three places:

| Where | What |
|---|---|
| The **hierarchy directory** of a repository: `<repo>/.claude/ah2/` (or `WRANGL_HIERARCHY_DIR`) | links (`links/<team>.json`), `peers.jsonl`, `gates.jsonl`, per-session activity (`activity/`), the status document (`status.json`), pipeline decisions, snapshots |
| The **ah home** (`~/.claude/ah2/` or `AH_HOME`) | the daemon's socket, lock and logs |
| A few per-user files in `~/.claude/ah2/` | session roles, the escalation-gate record, usage |
| The **state database**, `<ah home>/state/ah.db` | a SQLite database, and the source of truth for teams, members, sessions (the registrations), messages, mailboxes and peer records; see [The state database](#the-state-database). A team's path such as `teams/<name>.json` and a message's path under `msgs/` name its record there; no file is written |

A hierarchy directory also holds, beyond the files named above: `asks.jsonl` (your recorded answers to questions),
`grants/` (work-tree grants), `holds/`, `jobs/` (tracked long jobs), `progress/` (each stream's work journal and plan),
`checkpoints/`, `specs/` and `tmp/` (a scratch folder per member). Each message pool lives in
`msgs/`, and a work tree can have its own pool; readers read all of a team's pools.

A linked git worktree shares its team with the main checkout's hierarchy directory.

### The state hub

Every piece of shared state belongs to one **domain** in a single registry, the domain set `Ah.State` serves:
`session-roles`, `team`, `peers`, `msgs`, `peer-records`, `gates`, `links`, `ultra-gate`, `decisions`,
`activity`, `status`, `usage`, `mailbox`, and `worktree-grants`, and others added since (including `checkpoint`, `jobs`,
`progress`, `qa` and `session-pids`). Each domain declares its operations and whether each one reads or
writes. One client, `StateClient`, is the way in:

- A **write** goes to the daemon as `POST /v1/state/<domain>/<op>` when the daemon is up. The daemon runs one
  write per domain at a time. When the daemon is not up, the same library code runs directly under the
  domain's file lock. Either way the result is the same, and an operation id that has already run is not run
  twice.
- A **read** always runs the library directly. The daemon also serves `GET /v1/state/<domain>/<op>`, but the
  session layer does not need it.
- The `mailbox` domain has no operations of its own: mail goes through the mail routes
  (`/v1/send`, `/v1/inbox`, `/v1/ack`) or, without a daemon, through the mailbox engine in `Ah.Mesh`. It is registered
  so that a static check can still forbid any other code from writing mailbox files.

### The state database

`<ah home>/state/ah.db` is a SQLite database (schema version 19) with tables for teams, members, sessions, messages,
mailboxes and peer records, a record of writes already applied (so a write is never applied twice), and a few bookkeeping tables. It is
the source of truth for all of them. A database that goes missing or is swapped is refused (`store-missing`,
`store-swapped`), and `ah roster doctor` reports it. Backups are made before every migration and by `ah db backup`; `ah db restore` brings one back.

## The route table

The generated route registry in `Ah.Mesh` is the one authoritative table of the daemon's HTTP interface. Each entry is keyed
`"METHOD /path"` and states its purpose, its query and its response. The state routes are derived from the
domain registry, so they never need listing by hand.

Two things keep the table honest:

- **Drift fails the build.** Tests compare the routes the daemon serves with the table: a route that is
  served but not listed, or listed but not served, fails them (`EveryRouteOfTheMeshIsRegisteredOnce`).
- **The participation contract is generated from it.** The guide for custom-harness authors is written as
  prose in `hooks/participation-contract.md`; the contract data in `Ah.Mesh` fills in its tables from the route
  table and the state registry. The daemon serves the result as `GET /v1/contract` (JSON) and
  `GET /v1/contract/guide` (markdown), `ahd contract --plain` prints it with no daemon, and the committed copy
  is [participation.md](participation.md). The generated guide is pinned against a recorded golden.

Routes marked `hidden` are left out of the contract because only ah's own tools use them: the view and read-model
routes, `due`, `drain`, the QA routes, spawn admission, the Codex answer route and bare-session
recovery. Every route is described in [daemon-api.md](daemon-api.md). Where the code
registers them is not one place: `Ah.Daemon` serves the health, lifecycle, view and state routes, `Ah.Mesh` the mail,
contract and join routes, and `Ah.Cli` adds the QA, spawn-admission, Codex, bare-recovery and database routes.

## Mailbox and delivery

### The mailbox

Every addressable session has a durable mailbox. An address is `name@team`; a team's Orchestrator is
`orchestrator@<team>`. A box is named `<ah home>/mailbox/<team>~<hash>/<name>`, where the hash is taken
from the team file's real path, so two repositories that both have a team called `tm` never share a box. The name
keys the box's rows in the state database; no directory is written.

A box holds:

- entries (`mail_entries`): the messages, append-only. Each entry has an id, `from`, `to`, a kind (`brief`,
  `report`, `note` or `event`), a route, and either a reference to a message, a short text or an event.
- acknowledgements (`mail_acks`): an entry is acknowledged once it has one; the first acknowledgement wins.
- attempts (`mail_attempts`): the history of routing, pastes and the delivery ladder for the box's entries.
- adapter heartbeats (`mail_adapters`): the heartbeat of whatever is pulling this box.

The mailbox engine in `Ah.Mesh` is the only code that writes these rows; the daemon serves the same functions over
its socket. An append is one locked write, idempotent on the entry id.

When an entry is written it is given a **route**: `pull` if something has heartbeated on the box recently
(within three polling intervals), otherwise `fallback`. `ah msg send` then prints one delivery line telling
the sender what, if anything, it still has to do (see [comms-protocol.md](comms-protocol.md)).

Entries are acknowledged by whoever actually hands them over: the recipient's prompt hook when a mail prompt
arrives, an adapter, the daemon's own event drain, a SendMessage relay to the right recipient, or a confirmed
paste.

### The delivery ladder

With the daemon up, an entry that nobody acknowledges is not left waiting. Every 15 seconds the daemon
derives each unacknowledged entry's position from its ladder rows in `attempts.jsonl` and the clock, and takes
at most one step (`DeliveryLadder` in `Ah.Mesh`):

```
 rung 2  pull         the recipient is expected to read it itself
   │     not acknowledged within its pull timeout (3 × poll interval + 30 s)
   ▼
 rung 3  relay        a carrier is asked to pass it on with SendMessage
   │     no carrier, the request failed, or 10 minutes passed
   ▼
 rung 4  paste        typed into the member's recorded pane, up to 4 tries, 5 minutes apart
   │     no pane, paste disabled, retries used up, or a paste that could not be confirmed
   ▼
 rung 5  escalated    the Orchestrator gets one delivery-failed event

 an acknowledgement at any rung ends the ladder: delivered
```

With `single_carrier` on (the default), an entry to a Claude receiver has one carrier fixed at creation and rung 3 writes
no relay request and rung 4 never pastes to it; see [comms-protocol.md](comms-protocol.md#one-carrier-per-entry).

Rung 2 applies only to an entry routed `pull` (its recipient had a fresh heartbeat when it was sent). An entry
routed `fallback`, as for a member with no pull adapter, starts at rung 3.

- **Carriers (rung 3).** The daemon picks who owes the relay: the sender itself if it is a live Claude
  session and the entry was routed `fallback` (it already holds the line to send); otherwise the sender, by a
  `relay-needed` event, if its own adapter is fresh; otherwise the team's Orchestrator, by a `relay-needed`
  event. A recipient that is not a Claude session (a Codex or custom member) has no carrier, and the entry goes
  straight to rung 4.
- **The hardened paste (rung 4)** lives in `Ah.Launch` (the paste path) and is the same paste `ah roster deliver`
  uses. It types only into the pane **recorded** for the member in the team file (its transport and
  `transport_id`); it never searches for a pane or uses the caller's own terminal. Before typing it reads the
  pane and refuses if the member is busy, blocked on a prompt, not live, or the screen cannot be recognised.
  The text must be a single line of at most 500 characters, and only one paste per member runs at a time.
  After typing it presses Enter once, at most, and watches for up to five seconds for proof that the turn
  started or that the recipient acknowledged. A paste it cannot confirm is never repeated, so a message is not
  typed twice.
- **Holds and pauses.** A member whose activity is `blocked` holds its entries at rung 4 without using up an
  attempt; one whose activity is `failed` pauses the ladder. Events sent by the daemon itself are never on the
  ladder.
- **The crash rule.** A rung-4 `sending` row with no outcome, left by a process that is gone, means nobody
  knows whether the paste landed. The daemon records it as `unconfirmed` (reason `crash`) and escalates it; it
  never pastes that entry again. An acknowledgement that arrives later still marks it delivered.
- **Watching it.** `ah msg inbox` shows each entry's rung and state; `ahd status` shows a one-line count.

Without a daemon nothing climbs the ladder: entries wait in the box until they are pulled, and the ladder
resumes from the files when a daemon starts.

## The peer mesh

The mesh decides who may message whom. Its policy is the team file's `mesh.policy`:

- `star` (the default): members talk to their Orchestrator; two members talk to each other only over a link.
- `star-warn`: the same rule, but what `star` would refuse is delivered and logged instead, except a member broadcast to `*` and a send over a spent link, which are still refused.
- `open`: no checks.

**One rule function decides every send.** The mesh rule in `Ah.Mesh` is the single implementation, and
all three places a message can leave from call it:

```
  ah msg send / POST /v1/send ───┐
  SendMessage (PreToolUse gate) ─┼──▶  decide()  ──▶ allow / refuse with a reason
  ah roster deliver ─────────────┘     Ah.Mesh (MeshRule)
```

In outline: an Orchestrator may talk to its own members and to any other Orchestrator; a member may talk to
its own Orchestrator and reply to whoever briefed it; only a team's Orchestrator may broadcast to `*@<team>`;
anything across teams goes Orchestrator to Orchestrator, except that a member and its lane lead (a Sub-Orchestrator)
may message each other across teams without a link; and a member-to-member send inside a team needs an
active link, which members assigned together get without asking. A refusal comes back to the sender with a code (`outside_team`, `not_linked`, `link_exhausted`,
`broadcast_orchestrator_only`, and for a team that has gone or cannot be found `removed_member`, `team_gone`,
`team_mismatch`, `team_unreadable` and `team_unresolved`). The sender is the caller's registered session, never a name it claims.

**Links** let two members of one team message each other for one task. Members who are assigned together are
linked by that fact: when both are in one work stream (`spawn-one --stream`), or both hold an open request the Orchestrator
dispatched to them under one checklist item or slug, the first `ah msg send` between them opens their link (a request a
member files itself links nobody, and an Ultra-Advisor is never linked this way). Such a link is marked `opened by assignment` in `ah msg link list`, takes any message with or without
`--ref`, is extended at its caps for as long as the assignment lasts, and ends when the stream is done, either request is
answered, or a member leaves. If the Orchestrator revokes it, it stays closed until the pair is assigned to something
else. For a teammate it is not assigned with, a member asks with
`ah msg link-request`; the daemon grants the common pairs itself (implementor with reviewer, architect with
implementor) when the task has not been closed or withdrawn and the limits are the defaults, and anything else goes to the Orchestrator
as a request. A link allows 12 messages over 30 minutes, and a member may hold two at once (a link opened by assignment is not counted). The exception is the link between an implementer and its reviewer: it defaults to 120 minutes and 3 review rounds, keeps the 12-message cap, and the third round closes it. It ends when its
task is closed or withdrawn (`ah msg close` / `ah msg withdraw`), not merely answered, the cap or the time is used up, a member leaves, or the Orchestrator revokes it; members may ask
for an extension. Links are stored in `<hierarchy dir>/links/<team>.json`.

**Every linked message is mirrored.** It is counted, then written as a `link-note` file beside the task
before it is delivered; if the note cannot be written the send is refused and the count rolled back. The
Orchestrator reads an exchange with `ah msg link show <link id>`. The receiver sees a first line saying the
message comes from a peer and is not the user's approval: a link carries delivery, never permission.

**What is logged.** Under `star-warn`, a send that `star` would have refused is delivered and logged as
`would_refuse` in the recipient's box; a member broadcast to `*` and a send over a spent link are refused as
under `star`. Under `star` or `star-warn`, a member's SendMessage to a name no team knows is allowed and
logged as `unclassified_recipient` in the Orchestrator's box. A refusal under `star` is returned to the sender
only.

## Custom harnesses: the participation contract

Any agent runtime can be a team member, not just Claude Code and Codex. Such a member is a row with
`kind: "custom"`, a `harness` name and `route: "mail"`, and it takes part entirely through the daemon's
interface: it registers, pulls its mail, acknowledges, and writes reports. The full set of requirements, each
marked must or should, is [participation.md](participation.md).

**Admission.** `POST /v1/register` from a custom harness is accepted only if every address it registers is a
member row in that team file which admits that harness, and its mailbox paths are the ones ah would compute.
Anything else is refused `not_admitted`.

**Restart recovery.** A harness that only has the route (Codex or a custom harness) may restart with the same
`session_id` and a new `pid`, and register again. The register is accepted when it names a `pid` that the route
binds (the caller's own process, or one of its ancestors, with that start time) and the record already held for
that `session_id` names a process that is gone: the process is dead, or its pid was reused by a process with
another start time. A record whose start time cannot be compared counts as still running. The other cases are
refused `not_admitted`:

- the held record's process is still running on a different `pid` (`session <id> is registered to another
  process`);
- the register has no `pid` and a record exists: a register without `pid` can only create a record, never
  replace one;
- the `pid` is not one the route binds, so it cannot take the id.

Registering with the same `pid` the record already holds is not affected.

**Joining.** A harness started without an address can ask to join:

```
 harness ──POST /v1/join──▶ ahd ──request file + join-request event──▶ team's Orchestrator
                                                                         │ shows the user what the
                                                                         │ daemon observed, apart from
                                                                         │ what the harness claims
 harness ◀──GET /v1/join/<id>── ahd ◀── ah msg join accept | deny ──────┘
```

- The request file separates **facts** the daemon observed (whether the process the request names is alive,
  and its command line) from **claims** the harness made (its reason, name and wanted role). The harness's own
  text is stripped of control characters and shown as one inert quoted line.
- Only the team's own Orchestrator can decide, and it is recognised from the **process tree**: the deciding
  command must run in the Orchestrator's own process or a descendant of it, and the Orchestrator's recorded pid
  must be alive and registered as `orchestrator@<team>`. No flag, environment variable or session id grants
  it.
- A decision needs a basis and a reason. It is made by exclusively creating the response file, so a second
  decision is refused, and that file is the audit record of who decided, on what basis and why. An accept adds
  the custom row to the team file.
- An undecided request expires after 10 minutes, and a harness that was denied or expired may ask again after
  a 10-minute cooldown. A harness the team admitted before may be re-admitted, without a new row, only if it
  comes back with the identical command line.

## The Codex app-server adapter

A Codex member normally gets its mail typed into its pane by the ladder. The adapter is an alternative
that hands mail to the Codex turn over Codex's own app-server connection instead, so nothing is typed into the
pane. It lives in `Ah.Codex`, and
the daemon starts it.

- **On by default for Codex members on `herdr`, on a Codex version that has been checked.** A member opts out
  with `adapter: "off"` on its `kind: "codex"` roster row (`ah roster edit --member <name> --adapter off`);
  `adapter: "appserver"` asks for it explicitly. Only the `herdr` transport can use it.
- **Version-gated.** The adapter is used only for Codex versions it has been checked against. That list is
  one version in this release (`codex-cli 0.162.0-alpha.2`, up to the next prerelease); any other
  Codex launches the ordinary way and the spawn output says so, once, with how to silence it.
- **Create, then attach.** A spawn starts the member's own server, creates the thread on it, runs one
  warm-up turn so the thread exists on disk, resumes it, and only then launches the terminal as
  `codex resume <thread> --remote <socket>`. The thread id is recorded on the member's row, and the daemon
  resumes exactly that thread; it never searches for one. Every sandbox and approval setting is on the server.
- **Checked on every connect.** The thread's reported sandbox, network and approval settings must equal what
  ah set. If they do not, no turn is sent, the row says so, the Orchestrator gets one `adapter-refused` event,
  and the mail stays on the ordinary ladder.
- **Allow-listed launch.** When the adapter is used, the member's launch arguments are checked against a short
  allow-list (model, sandbox mode, approval mode, extra directories unrelated to the ah home, and three named
  config keys), and fixed sandbox settings are pinned. Anything else means the member launches without the
  adapter.
- **No typing.** The adapter never pastes or presses keys. It starts or steers a turn on the thread the
  member's terminal already shows, acknowledges an entry only when Codex reports that exact text as a user
  message, never re-sends, and never answers an approval prompt. If it cannot connect, it stops
  heartbeating and the ordinary ladder takes over.

## Context alerts

The Stop hook reads the newest usage figures from the end of a session's transcript (the `ah-hook activity` unit)
and stores the session's context in its activity record as absolute tokens. A transcript does not carry the
window size, so there is no percentage and no context alert fires in this release. The evaluation in `Ah.Watch`
acts only on a reading that carries a percentage: it compares the fill of every Claude member of an
Orchestrator's teams (never the Orchestrator itself) with two fixed thresholds and mails the Orchestrator a
`context-high` event at 70% and a `context-critical` event at 85%, naming the member and its role. Each threshold
fires once until the fill drops ten points below it; a reading older than two hours is ignored. An alert is
advice only: nothing is compacted or stopped.

## Status and views

Two read-only surfaces show what is going on. Neither is part of the participation contract.

**The status document.** `<hierarchy dir>/status.json` is a small JSON summary of the teams, their members, open
dispatches and a timeline. Only the status writer in `Ah.Hierarchy` writes it (`ah roster status`, and the state hub's
`status` domain on events), atomically. The daemon never writes it; it serves it as `GET /v1/status` with a
revision number and long-polling. The ah mod's band, Pane and toasts read it through the daemon and fall back
to the file. Its schema and reading rules are in [status-file.md](status-file.md).

**The view model and the terminal view.** `GET /v1/view?path=<path>` returns one node of a navigable tree,
built by the view builder in `Ah.Watch` from the registrations and team files:

```
 /                                  every Orchestrator the daemon can find, across repositories
 /o/<oid>                           one Orchestrator's teams
 /o/<oid>/t/<team>                  one team's members and open work
 /o/<oid>/t/<team>/m/<member>       one member
```

Each response carries a `view_schema` version, a revision for long-polling, the node's breadcrumb, a summary,
its children (each with the path to open it) and a body. Ids in paths are opaque; a client follows the
`children` paths rather than building them. The views are presentation-free JSON: states are values, not
glyphs or colours, and every string has had control characters stripped before it leaves the daemon. That
keeps the drawing entirely in the client, so another client, such as a web page, could draw the same views
without any change to the daemon.

There are two clients. `ahd tui` runs `ahd-tui` (the `Ah.Tui` project, a separate program that sits beside `ahd`).
It holds only its own navigation state, long-polls the view, never starts the daemon, and never changes anything.
How to use it is in [tui.md](tui.md). `ahd ui` is the other: a browser page (files under `web/ui/`)
that draws the same views as a grid, graph, tree and plan. It reaches the daemon through the browser bridge, a
loopback listener that serves read-only copies of the view routes and nothing else; see
[daemon-api.md](daemon-api.md#the-browser-bridge). The Claude Code mod (the band and Pane) is a third reader, of the
status document.

## The command surface

| Command | What it is for | Where |
|---|---|---|
| `ah roster <verb>` | teams and members: create, add, spawn, disband, streams, lanes, recovery, deliver, status, check-in | `bin/ah roster` |
| `ah msg <verb>` | message files, mail (`send`, `inbox`, `ack`, `close`), links, joins | `bin/ah msg` |
| `ah qa <verb>` | the QA job queue | `bin/ah qa` |
| `ah progress`, `ah test`, `ah allow`, `ah rules`, `ah usage`, `ah usage-report`, `ah db`, `ah view`, `ah issue-intake` | plan view, the test queue, member launch rules, protocol answers, rate-limit and token reports, the state database, the band's buttons, `/wrangl:pipeline` intake | `bin/ah` |
| `ahd <verb>` | the daemon: start, stop, status, logs, inspect, check, contract, tui, ui, login, where | `bin/ahd` |
| `ah-hook <name>` | one Claude Code hook unit (run by Claude Code only) | `bin/ah-hook` |

Inside a Claude Code session the plugin's `bin/` directory is on the Bash tool's `PATH`, so `ahd` works as
typed; [daemon.md](daemon.md#the-ahd-command) shows how to link it for a plain terminal. Every verb is listed in
[cli-tools.md](cli-tools.md). The read-only `ahd` verbs answer from the files when no daemon is up and say
which source they used.

## The safety model and its limits

The checks in Wrangl keep cooperating software from making mistakes: a member messaging where it
should not, a harness admitting itself, a paste landing on the wrong screen, an agent's text posing as part of
the interface. They are **correctness guards, not a security boundary** against another program running as
the same OS user.

- **The boundary is your OS user.** The daemon's API listens only on a unix socket (mode 0600, in a 0700
  directory). The one other listener is the browser bridge on `127.0.0.1`, which exists only while the browser page
  is open, serves read-only views, and requires matching `Host` and `Origin` headers plus a one-time sign-in code
  and a session cookie. Anything that can open the socket, or read and write the mailbox and team files, is you as
  far as ah can tell.
- **The mesh** checks the sender's registered session. A process that writes the mailbox files directly is
  outside it, and a caller with no registered session counts as the user.
- **Join decisions** are tied to the Orchestrator's process tree. That has a known limit: a harness launched
  from the Orchestrator's own tool shell is the Orchestrator's descendant and could accept its own join. Launch
  custom harnesses with `ah roster spawn-ad-hoc --command` (tmux only), or start them yourself in another
  terminal. A
  recycled pid left in a stale registration is also outside the check.
- **Direct team-file writes.** Any process of the same user can edit a team file and so add or change members.
  Nothing prevents that; the checks only make sure ah's own commands do the right thing.
- **Claims stay claims.** A harness's pid, command line, name and stated reason are its own claims, which is
  why a join goes to you through the Orchestrator. A link carries delivery, not permission. A context alert is
  advice.
- **Text is made inert.** Text from agents and harnesses shown in join requests and in the views has its
  control characters stripped, so it cannot rewrite your terminal.

## How it is tested

Run the whole solution with `scripts/dotnet-test.sh`: it builds and tests `native/Ah.sln` through the machine-wide test
bucket (`ah test run`: it holds CPU tokens, capacity `AH_TEST_TOKENS`, default the larger of 2 and the CPU count
minus 2; `AH_TEST_SLOTS` is an older name that still works) and prints the wait; every `dotnet test` it runs carries `--blame-hang-timeout 20m`; a run that names `--filter`, a project, a solution or a directory skips the slot (a `--filter` alone runs against the solution).

The suite is the native xUnit projects under `native/tests/` (`dotnet test native/Ah.sln`), the recorded
contract cases under `tests/contracts/` run against the built binary, and shell suites under `tests/`, run with:

```
AHD_NATIVE_BIN=<the built ahd> bash tests/run-all.sh
```

- **Hermetic by default.** Every `tests/test-*.sh` file sources `tests/lib-hermetic.sh` as its first command.
  It clears the herdr and tmux session variables and makes the guard in `Ah.Launch` (`MuxGuard`) refuse any herdr,
  tmux, claude or codex that is not in a fake directory the test lists in `AH_TEST_FAKE_BIN`; cleanup goes
  through `hermetic_on_exit`, which also kills any tmux server a test leaves. Next the file sources
  `tests/lib-test-env.sh`, which turns the daemon off (`AH_DAEMON=off`) for that file; a test checks that every
  file does. A file that needs a daemon opts in by setting `AH_TEST_DAEMON=1` between the two lines, only files
  on an allow-list may, and they run their daemon in a throwaway home under `/tmp`. After the run, the suite
  fails if any test daemon was left behind.
- **Same answer through every route.** The recorded state-op cases run over the daemon's route and through
  the one-shot `ahd state` verb, and the results must agree with each other and with the recording.
- **Contract.** The contract tests in `Ah.Mesh.Tests` check the contract routes, the admission guard and the route-table drift
  check against recorded goldens.
- **Docs pinned to code.** `tests/test-cli-tools-doc.sh` fails if [cli-tools.md](cli-tools.md) and the verbs
  and flags of `ah roster` and `ah msg` disagree. The status fixtures and the mod's fixtures are compared
  against the recorded ones.
- **Tests that can fail.** A test counted as coverage must have been seen failing against a deliberately
  broken implementation; the standard is in [spec-process.md](spec-process.md). Several checks also plant
  their own failure to prove they can catch one.

## Where each feature lives

| Feature | Main code | Read more |
|---|---|---|
| Daemon process, socket, lifecycle | `Ah.Daemon`, `Ah.DaemonClient` | [daemon.md](daemon.md) |
| Route table, contract generation | `Ah.Mesh` (route registry, contract data) | [participation.md](participation.md) |
| State hub and domains | `Ah.State` | this page |
| Mailbox | `Ah.Mesh` (mailbox), `Ah.DaemonClient` | [comms-protocol.md](comms-protocol.md) |
| Delivery ladder, carriers, context alerts | `Ah.Mesh` (delivery), `Ah.Watch` | [daemon.md](daemon.md#how-mail-is-delivered) |
| Hardened paste | `Ah.Launch` | [comms-protocol.md](comms-protocol.md) |
| Mesh rule, gates, links | `Ah.Mesh` (mesh rule, gate, links) | [comms-protocol.md](comms-protocol.md), [team-file.md](team-file.md) |
| Joins and vetting | `Ah.Mesh` (join) | [comms-protocol.md](comms-protocol.md), [participation.md](participation.md) |
| Codex app-server adapter | `Ah.Codex` | [daemon.md](daemon.md#the-codex-app-server-adapter) |
| Context fill | `Ah.Watch`, `ah-hook activity` | [daemon.md](daemon.md#context-alerts) |
| Status document | `Ah.Hierarchy` (status) | [status-file.md](status-file.md) |
| View model and terminal view | `Ah.Watch` (view builder), `Ah.Tui` | [daemon.md](daemon.md#the-terminal-view-ahd-tui) |
| Hooks and gates | `Ah.Hooks`, `hooks/hooks.json`, `bin/ah-hook` | [how-it-works.md](how-it-works.md#what-keeps-members-in-bounds) |
| Roles and the role registry | `Ah.Config` (registry), `agents/*.md` | [how-it-works.md](how-it-works.md#roles-and-the-hierarchy), [custom-roles.md](custom-roles.md) |
| Streams, lanes, message files, progress plan | `Ah.Hierarchy`, `Ah.Commands` | [how-it-works.md](how-it-works.md#streams-and-work-trees), [progress.md](progress.md) |
| QA queue | `Ah.Hierarchy` (queue and scheduler), `Ah.Cli` (routes) | [daemon-api.md](daemon-api.md#qa-queue) |
| State database | `Ah.StateDb` | this page |
| Browser page | `Ah.Daemon` (bridge), `web/ui/` | [daemon-api.md](daemon-api.md#the-browser-bridge) |
| Stuck-member alarms and relaunch | `Ah.Watch` | [how-it-works.md](how-it-works.md#what-the-orchestrator-tells-you) |
| Commands | `bin/ah`, `bin/ahd`, `bin/ah-hook` | [cli-tools.md](cli-tools.md) |
