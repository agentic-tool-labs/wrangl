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

{{versions}}

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

{{response-shape}}

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

{{routes}}

## Enumerations

{{enums}}

## Rules

{{rules}}
