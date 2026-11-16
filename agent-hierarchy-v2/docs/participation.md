# The ah participation contract

This guide says how an agent runtime that ah has no adapter for (a *custom harness*) takes part in an ah
team: how to reach the daemon, what a participant must and may implement, and how to check a client. It is
served by the daemon (`GET /v1/contract/guide`, and the same contract as JSON at `GET /v1/contract`), and it
is rendered locally, with the daemon down, by `ahd contract`. The tables in it are
generated from the daemon's own route table and constants; the prose is one file shipped with the plugin.

A participant is a *pull participant*: it reads its mail from the daemon and reports back. Nothing new is
on the wire: everything below is the existing `/v1` HTTP surface.

## Reaching the daemon

The daemon listens only on a unix socket, `<AH_HOME>/run/ahd.sock`; the protocol is HTTP over that socket.
There is no TCP listener.

1. Use `$AH_HOME` if it is set, otherwise `~/.claude/ah2/`.
2. Read `run/ahd.json` in it, and connect to the `socket` it names.
3. `GET /v1/health`. Check that `protocols` contains the major version you were written for.

Every response carries `Ah-Protocol` and `Ah-Daemon-Version` headers. Both sides ignore unknown fields.

| | |
|---|---|
| contract version | `1.0` (`<major>.<minor>`) |
| protocol | `1` (the `/v1` prefix of every route) |

A minor bump of the contract version is additive: a new optional field, route or requirement at `should` or `may`. A major bump changes
a `must`, or removes or renames something, and ships with a new protocol; `/v1` stays served while `protocols` on health lists 1.

## Trust

The only gate is the local user's socket (mode 0600 in a 0700 directory). The checks the daemon makes of a
participant, such as the admission check on `register`, are correctness guards, not a security boundary: any
process that can open the socket is the user and could claim a built-in harness. The contract routes reveal
nothing a same-user process could not read from `AH_HOME` already.

## Becoming a member

A participant is admitted as one member of one team: a row of the team file with `kind: "custom"`,
`harness: "<name>"` and `route: "mail"`, written by the team's Orchestrator. The harness name matches
`[a-z][a-z0-9-]{1,31}`. When ah launches the harness (the row has a `command`), it sets `AH_HOME` and
`AH_ADDRESS=<name@team>` in the environment. When the user starts it, they give it that address. Once the
member is admitted, `GET /v1/contract/join?address=<name@team>` returns exactly the values `register` needs.

The role comes from admission; a participant never declares its own. A harness the user starts without an address may instead
ask the team's Orchestrator to admit it (P-JOIN).

## What a participant implements

Each id below is a requirement id.

### P-ID (must)

The participant has:

- a `session_id` that is unique per agent session and stable for its life (a UUID is enough);
- the harness name;
- the member address it was admitted under.

### P-REG (must)

Call `POST /v1/register` on start with the values `GET /v1/contract/join` returned, plus `session_id`,
`harness`, and `pid` if the harness has one. Register again after a daemon restart: a connection error, or a
change in `pid` or `started_at` on `GET /v1/health`. A `503 busy` answer is not a refusal: register again after a short wait.

### P-PULL (must)

Long-poll `GET /v1/inbox` with `address`, `session_id`, `adapter=<harness>` and `poll_ms`, and poll again
within `poll_ms` of each return. Keep `since` (the byte offset) across polls. The pull is also the heartbeat:
a participant that stops pulling is stale after the heartbeat factor times `poll_ms` (see the rules below),
and from then on its mail is routed `fallback`.

### P-DELIVER (must)

Put each entry's text into the agent's next turn verbatim, marker line included. Entries due together may
share one turn.

### P-ACK (must)

`POST /v1/ack` with `by: "adapter"`, and only after the text has entered a turn. Never ack an entry the agent
was not shown. Acking twice is harmless.

### P-REPORT (must)

For every brief (an entry carrying `[hierarchy-msg <id>]`), a response is filed for the request (`ah msg fill --id <id>`),
in the shape shown below. It is written either by the agent itself, or by the harness through
`POST /v1/state/msgs/create` with `req` set to the request path and an `op_id`. Then the harness sends the
Orchestrator `POST /v1/send` with `ref` set to the response path, `from` its own address and `session_id` the id it registered with: a send without the registered `session_id` is refused `from_mismatch`.

```
---
id: <id>
type: response
to: <requester's role>
from: <your role>
slug: <slug>
parent: null
reason: null
eta: null
to_name: null
from_name: null
team: <team>
team_file: <team file path>
team_guide: <team guide path>
created: <ISO-8601 time with the local offset>
---

## [0] tldr
- [1] status: ...
- [2] changes: ...
- [3] evidence: ...
- [4] gaps: ...
- [5] open_questions: ...
```

### P-COMPAT (must)

Ignore unknown fields. Refuse to run if `protocols` lacks the major version you were written for.

### P-LIVE-PID (should)

Supply `pid` on register, so liveness can use the process as well as the heartbeat.

### P-ROLE (should)

Load `GET /v1/contract/role?role=<role>` for the admitted role into the agent's instructions.

### P-JOIN (should)

A harness started without `AH_ADDRESS` asks to join before it registers. It takes the team from `AH_TEAM` (or lists
`GET /v1/contract/teams`), then sends `POST /v1/join` with `team`, `harness`, its `session_id` and a `why` the
Orchestrator reads. It polls `GET /v1/join/<join_id>?wait=<s>` until the status is not `pending`. It registers only after
`accepted`, with the address and values the answer carries; while the join is pending, and after `denied`, `expired` or
`failed`, it does not register and does not pull. A `failed` answer carries `error` (the accepted member row was not
written) and sets no cooldown: it may ask again at once. After `denied` or `expired` it asks again no sooner than the cooldown: a refused
`join_cooldown` carries `retry_after` seconds. Asking again while a join of the same session is pending answers with that
join, so a restart does not make a second request.

The Orchestrator reads the request together with what the daemon observed about the process the request names. The pid and
that process's command line are the harness's own choice, so they do not show who started it. The Orchestrator decides from its
own process tree: a harness the Orchestrator launched from its own tool shell is its descendant and could accept its own join
by construction, so a harness meant to join is started with `spawn-ad-hoc --command` or by the user, not from the
Orchestrator's shell.

## What a participant may do

- `POST /v1/send` to the Orchestrator, and to a team member the Orchestrator has linked it to. Its `from` is one of its own addresses. A send the team's link policy forbids is refused with one of the `send_refusals` codes.
- Read the `inspect` and `status` routes.
- Read registered state (`GET /v1/state/...`).

## What a participant must not do

- Write team files, or any state, other than through `msgs.create`.
- Register an address it was not admitted under (the daemon refuses it).
- Answer another member's approval or permission prompts.
- Emit `[ah-entry` text of its own (the daemon neutralises it anyway).

Activity reporting (working, idle, blocked, failed) is not part of this version: a custom participant shows
activity `unknown`. Its briefs are still covered by the Orchestrator's check-ins.

## Where a participant's mail goes

A participant that pulls with a fresh heartbeat has its mail routed `pull`. One that has stopped pulling is
stale, and mail for it is routed `fallback`. A custom harness has no terminal screen profile, so ah never
types into it: it is pull-only.

## Routes

### `GET /v1/health`

Liveness and version of the daemon. Check `protocols` contains your major version.

Response: {pid, version, protocol, protocols[], impl, started_at, root, owns, timer_state, requests, health_calls, unpulled_acks, errors, errors_by_code}

### `GET /v1/inspect/boxes`

The mailboxes on disk, with counts and adapter freshness.

Query:

| field | type | required | notes |
|---|---|---|---|
| `address` | string | no | name@team; one box only |

Response: {boxes[{address, dir, entries, unacked, overdue, adapter, adapter_state, heartbeat_at}]}

### `GET /v1/inspect/entries`

The entries of one mailbox.

Query:

| field | type | required | notes |
|---|---|---|---|
| `address` | string | yes | name@team |
| `filter` | string (`pending`, `acked`, `all`) | no | default pending |
| `n` | integer | no | how many, newest last |
| `cwd` | string | yes | caller context: an absolute directory |
| `hierarchy_dir` | string | yes | caller context: an absolute directory |
| `main_hierarchy_dir` | string | no | caller context: an absolute directory |
| `messaging_socket_dir` | string | no | caller context: an absolute directory |

Response: {address, entries[], total}

### `GET /v1/inspect/registrations`

The registrations the daemon holds, live or stale.

Query:

| field | type | required | notes |
|---|---|---|---|
| `since` | integer | no | the token of the last answer, or -1 |

Response: {registrations[{session_id, address, harness, pid, live, adapter}], token with since}, or {unchanged, token} when since is the token

### `GET /v1/inspect/events`

The events waiting in the Orchestrator's mailbox.

Query:

| field | type | required | notes |
|---|---|---|---|
| `address` | string | no | name@team |

Response: {events[]}

### `GET /v1/inspect/timers`

The dispatch clock the daemon keeps for Orchestrator sessions (a dry run).

Query:

| field | type | required | notes |
|---|---|---|---|
| `session` | string | no |  |

Response: {timers[]}

### `GET /v1/status`

The live status document of a hierarchy directory the daemon watches, with its rev.

Query:

| field | type | required | notes |
|---|---|---|---|
| `dir` | string | yes | absolute hierarchy directory |
| `wait` | integer | no | 0 to 25; needs since |
| `since` | integer | no | a rev |

Response: {rev, doc}

### `POST /v1/send`

Send a mailbox entry.

Body:

| field | type | required | notes |
|---|---|---|---|
| `to` | string | yes |  |
| `from` | string | no |  |
| `session_id` | string | no |  |
| `id` | string | no |  |
| `kind` | string | no |  |
| `ref` | string | no |  |
| `text` | string | no |  |
| `event` | string | no |  |
| `data` | object | no |  |
| `reply_to` | string | no |  |
| `to_session` | string | no |  |
| `eta` | string | no |  |
| `supersedes` | string | no | the id of an entry of yours in the recipient's box that this one replaces; refused once that entry is picked up or ended, or when it is another sender's |
| `post_link` | string | no | a link id; only the team's Orchestrator posting into that link's thread uses it, a participant leaves it out |
| `cwd` | string | yes | caller context: an absolute directory |
| `hierarchy_dir` | string | yes | caller context: an absolute directory |
| `main_hierarchy_dir` | string | no | caller context: an absolute directory |
| `messaging_socket_dir` | string | no | caller context: an absolute directory |

Response: the entry as sent: {id, to, route, ...}; refused as {error: {code}} with from_mismatch (403), outside_team (403), not_linked (403), link_exhausted (403), broadcast_orchestrator_only (403), removed_member (403), team_gone (403), team_mismatch (403), team_unreadable (403), team_unresolved (403), mirror_failed (503)

### `GET /v1/inbox`

Long-poll a mailbox. With `adapter` set, each pull is also the heartbeat.

Query:

| field | type | required | notes |
|---|---|---|---|
| `address` | string | yes | name@team |
| `since` | integer | no | byte offset into the mailbox; required with wait, default 0 |
| `wait` | integer | no | seconds to hold the request open for a new entry, 0 to 25; needs since |
| `adapter` | string | no | the harness name; with it the pull also writes the heartbeat |
| `session_id` | string | no | the pulling session |
| `poll_ms` | integer | no | the participant's poll interval in milliseconds, default 5000; the heartbeat is fresh for 3 of them |
| `all` | boolean | no | "1" or "true": include acked entries |
| `overdue` | boolean | no | "1" or "true": only entries past their grace |
| `cwd` | string | yes | caller context: an absolute directory |
| `hierarchy_dir` | string | yes | caller context: an absolute directory |
| `main_hierarchy_dir` | string | no | caller context: an absolute directory |
| `messaging_socket_dir` | string | no | caller context: an absolute directory |

Response: {address, entries[{id, from, kind, text, ref?, event?, created, ...}], next}

### `POST /v1/ack`

Acknowledge entries that entered a turn.

Body:

| field | type | required | notes |
|---|---|---|---|
| `address` | string | yes | name@team |
| `ids` | string[] | yes | entry ids |
| `by` | string (`prompt-hook`, `adapter`, `cli`, `sendmessage`, `watcher`, `paste`, `stop-hook`) | yes |  |
| `session_id` | string | no | the acking session |
| `cwd` | string | yes | caller context: an absolute directory |
| `hierarchy_dir` | string | yes | caller context: an absolute directory |
| `main_hierarchy_dir` | string | no | caller context: an absolute directory |
| `messaging_socket_dir` | string | no | caller context: an absolute directory |

Response: {acked[], unknown[], fresh[]}

### `POST /v1/register`

Register a session under its address(es). A harness that is not built in must be admitted (a team-file row of kind custom naming the same harness) or the answer is 403 not_admitted.

Body:

| field | type | required | notes |
|---|---|---|---|
| `session_id` | string | yes | [A-Za-z0-9._-]+, unique per agent session and stable for its life |
| `addresses` | string[] | yes | the member addresses (name@team) this session serves |
| `boxes` | object | yes | address -> absolute mailbox directory, for every address |
| `team_file` | string | no | absolute path of the team file the addresses belong to |
| `harness` | string | no | the harness name; default claude |
| `pid` | integer | no | the session's process id; with it liveness uses the process as well as the heartbeat |
| `pane_id` | string | no | a terminal pane the session runs in, if any |
| `cwd` | string | no | the session's working directory (an existing absolute directory, else ignored) |

Response: {ok: true}

### `GET /v1/contract`

This contract as JSON.

Response: {contract_version, protocol, requirements[], routes[], enums, rules}

### `GET /v1/contract/guide`

This contract as a markdown guide.

Response: text/markdown

### `GET /v1/contract/join`

For an admitted custom member: the values register needs.

Query:

| field | type | required | notes |
|---|---|---|---|
| `address` | string | yes | name@team |

Response: {address, role, team_file, boxes{address: dir}, hierarchy_dir, poll_ms}; 404 not_admitted otherwise

### `GET /v1/contract/role`

The behavioural contract text of a role (read-only).

Query:

| field | type | required | notes |
|---|---|---|---|
| `role` | string | yes | a built-in role name |

Response: {role, text}; 404 for an unknown role

### `POST /v1/join`

Ask a team's Orchestrator to admit this harness as a custom member. Answered 202 with a new join id, or 200 with the id of a join of the same harness session still pending.

Body:

| field | type | required | notes |
|---|---|---|---|
| `team` | string | yes | the team to join (see GET /v1/contract/teams) |
| `harness` | string | yes | the harness name; a custom name, not a built-in one |
| `session_id` | string | yes | [A-Za-z0-9._-]+, the id the harness will register with |
| `why` | string | yes | one to 500 characters the Orchestrator reads when it decides |
| `pid` | integer | no | the harness process id, shown to the Orchestrator |
| `name_hint` | string | no | the member name asked for: [a-z][a-z0-9-]{0,31} |
| `role_wanted` | string | no | a known role name |
| `cwd` | string | no | caller context: an absolute directory; optional, the daemon finds the team without it |
| `hierarchy_dir` | string | no | caller context: an absolute directory; optional, the daemon finds the team without it |
| `main_hierarchy_dir` | string | no | caller context: an absolute directory; optional, the daemon finds the team without it |
| `messaging_socket_dir` | string | no | caller context: an absolute directory; optional, the daemon finds the team without it |

Response: {join_id, status: "pending", poll}; refused as {error: {code}} with bad_request (400), unknown_team (404), no_orchestrator (409), join_cooldown (429); join_cooldown carries retry_after seconds, unknown_team the live teams

### `GET /v1/join/{id}`

The outcome of a join. Long-polls up to wait seconds while it is pending; an undecided join expires 10 minutes after it was asked.

Query:

| field | type | required | notes |
|---|---|---|---|
| `wait` | integer | no | 0 to 25 seconds |
| `cwd` | string | no | caller context: an absolute directory; optional, the daemon finds the team without it |
| `hierarchy_dir` | string | no | caller context: an absolute directory; optional, the daemon finds the team without it |
| `main_hierarchy_dir` | string | no | caller context: an absolute directory; optional, the daemon finds the team without it |
| `messaging_socket_dir` | string | no | caller context: an absolute directory; optional, the daemon finds the team without it |

Response: {join_id, status: pending|accepted|denied|expired|failed}; accepted carries the values of GET /v1/contract/join (address, role, team_file, boxes, hierarchy_dir, poll_ms); denied carries why; failed carries error

### `GET /v1/contract/teams`

The live teams a harness may ask to join.

Response: [{team, orchestrator_live}]

### `POST /v1/state/msgs/create`

Create a message file exclusively (a response beside its request). A write needs an op_id: repeating one is safe.

Body:

| field | type | required | notes |
|---|---|---|---|
| `context` | object | yes | the caller context fields |
| `op_id` | string | yes | a fresh id per write |

`args` fields:

| field | type | required | notes |
|---|---|---|---|
| `dir` | string | yes | the hierarchy directory the file lands in |
| `path` | string | yes | absolute path of the message file |
| `content` | string | yes | the whole file text |
| `req` | string | no | for a response: the absolute path of its request, beside which the file is written |
| `id` | string | no | the message id |

Response: {result: {ok: true}}

### `GET /v1/state/{domain}/{op}`

A registered state read (the operations of the state registry whose kind is read and which the daemon serves).

Query:

| field | type | required | notes |
|---|---|---|---|
| `cwd` | string | yes | caller context: an absolute directory |
| `hierarchy_dir` | string | yes | caller context: an absolute directory |
| `main_hierarchy_dir` | string | no | caller context: an absolute directory |
| `messaging_socket_dir` | string | no | caller context: an absolute directory |

Response: {result}

## Enumerations

- `ack_by` (the `by` of an ack): `prompt-hook`, `adapter`, `cli`, `sendmessage`, `watcher`, `paste`, `stop-hook`. A participant uses `adapter`.
- `send_keys` (the body keys of `POST /v1/send`): `to`, `from`, `session_id`, `id`, `kind`, `ref`, `text`, `event`, `data`, `reply_to`, `to_session`, `eta`, `supersedes`, `post_link`.
- `send_refusals` (the `error.code` of a refused `POST /v1/send`, with its HTTP status): `from_mismatch` (403), `outside_team` (403), `not_linked` (403), `link_exhausted` (403), `broadcast_orchestrator_only` (403), `removed_member` (403), `team_gone` (403), `team_mismatch` (403), `team_unreadable` (403), `team_unresolved` (403), `mirror_failed` (503).
- `entry_filters` (the `filter` of `inspect/entries`): `pending`, `acked`, `all`.
- A harness name matches `^[a-z][a-z0-9-]{1,31}$`.
- The built-in harnesses, which register without admission: `claude`, `codex`, `claude-mod`, `stub`, `probe`.
- A requirement's level is one of `must`, `should`, `may`.

## Rules

| rule | value |
|---|---|
| heartbeat fresh factor | a heartbeat is fresh while its age is at most 3 x `poll_ms` |
| default `poll_ms` | 5000 |
| longest `wait` of a long-poll | 25 s |
| largest request body | 65536 bytes |
| entry marker | `[ah-entry <id> to <name@team>]` |
