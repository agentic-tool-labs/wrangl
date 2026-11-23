# Daemon API

This page lists every route the `ahd` daemon serves, with what each one takes and returns. It is for people writing
a client or a custom agent runtime, and for anyone debugging with `curl`. If you only want to run the daemon, read
[The daemon](daemon.md). If you are building a harness that joins a team, start with the
[Participation contract](participation.md); this page is the full list behind it.

- [Reaching the daemon](#reaching-the-daemon)
- [Rules that apply to every route](#rules-that-apply-to-every-route)
- [Health and lifecycle](#health-and-lifecycle)
- [Views, status and inspection](#views-status-and-inspection)
- [State](#state)
- [Mail](#mail)
- [Joining as a custom harness](#joining-as-a-custom-harness)
- [QA queue](#qa-queue)
- [Spawn admission and owner controls](#spawn-admission-and-owner-controls)
- [The browser bridge](#the-browser-bridge)
- [Versions](#versions)
- [When there is no daemon](#when-there-is-no-daemon)

## Reaching the daemon

The API is HTTP/1.1 over a **unix socket**, `<ah home>/run/ahd.sock`. The ah home is `$AH_HOME`, or
`~/.claude/ah2` when that is unset. The socket has mode 0600 inside a 0700 directory, so only your user can open
it. There is no network port for this API, no websocket and no server-sent events. Waiting is done by long
polling (below).

To try a route by hand:

```
curl --unix-socket ~/.claude/ah2/run/ahd.sock http://localhost/v1/health
```

`<ah home>/run/ahd.json` names the running daemon (its pid, version and socket path). A client reads it, calls
`/v1/health`, and trusts the daemon only if the pid in the answer matches the pid in the file.

**Who is calling.** There is no password or token on the socket. The daemon looks at the process that opened the
connection and walks up its process tree to decide whether the caller is you (**user**), a Claude Code session or
member (**session**), or neither (**anonymous**). Routes that need a particular caller say so below. This is a
guard against mistakes, not a wall against another program running as you.

## Rules that apply to every route

- **Paths** start with `/v1/`. A `{name}` segment accepts letters, digits, `.`, `_` and `-`. A trailing `{*rest}`
  segment also accepts `/`, `:` and `@`, up to 512 characters, and no empty, `.` or `..` part.
- **Bodies.** A `POST` body must be a JSON object; an empty body counts as `{}`. Anything else is
  `400 bad_request`. The size limit is 64 KiB, or 256 KiB for `POST /v1/state/msgs/create`, `.../settle` and
  `.../fill`. A larger body is `413 too_large` and the connection closes. A body that takes longer than 90 seconds
  to arrive is `408 request_timeout`.
- **Errors** are `{"error": {"code": "<code>", "message": "<text>"}}`, sometimes with extra fields. A few older
  view routes answer the flat form `{"error": "<code>"}` instead. Common codes: `404 not_found` for an unknown
  path, `405 bad_request` with an `Allow` header for a wrong method, `500 internal`, `503 draining` with
  `Retry-After: 1` while the daemon is shutting down.
- **Headers on every reply:** `Ah-Daemon-Version: <plugin version>`, `Ah-Protocol: 1` and
  `Content-Type: application/json`. Clients send `Ah-Client-Version`; the daemon only records it in its log of
  refused requests (`<ah home>/logs/rejected.jsonl`) and refuses nothing because of it.
- **Long polling.** Routes that wait take `wait`, a whole number of seconds from 0 to 25, and usually `since`, a
  position from an earlier answer. If nothing changed, the answer comes back when `wait` runs out, marked
  unchanged. A `wait` above zero without `since` is a `400`. The daemon answers every held request at once when it
  starts to shut down.
- **Caller context.** Mail and state routes take the caller's directories as `cwd` and `hierarchy_dir` (both
  absolute and required), and optionally `main_hierarchy_dir` and `messaging_socket_dir`.

## Health and lifecycle

| Route | Purpose | Request | Answer |
|---|---|---|---|
| `GET /v1/health` | Is the daemon alive, and which one is it. Needs no caller proof and does not count as a request in its own counters | none | `200` with `pid`, `version`, `protocol` (1), `protocols` (`[1, 2]`), `impl` (`native`), `started_at`, `root`, `owns`, `ladder` (`pending`, `sending`, `paused`, `escalated`), `timer_state`, `requests`, `health_calls`, `unpulled_acks`, `errors` (5xx count), `errors_by_code` and `load`. `503 draining` when shutting down |
| `POST /v1/drain` | Ask the daemon to exit. It answers first, then drains | `{"reason": "<text>"}` (optional) | `200 {"ok": true}`. `403 anonymous` unless the caller is a session or you |
| `POST /v1/ui/lease` | Open a lease on the loopback browser bridge. The reply stays open | query `service=1` for a background lease | `200`, then a first line `{"port": N}` and the connection stays open until the client hangs up. `403 user_only`, `403 ui_disabled` (when `AH_UI=off`), `503 bridge_bind`, `503 draining` |
| `POST /v1/ui/start` | Start the browser page in the background and open it | `{"token": "<64 hex>"}` (needed from a session) | `200 {"status": "started" or "reused", "opened": true/false}`. `403 user_only` or `ui_disabled`; `503` with `ui_unavailable`, `ui_start_timeout` (12 s, which includes the wait for the service lock), `ui_busy` (the lock stayed held for 10 s), `ui_service`, `bridge_bind` or `draining` |

## Views, status and inspection

All are `GET`, need no caller proof, and read only.

| Route | Purpose | Query | Answer |
|---|---|---|---|
| `/v1/view` | One node of the navigable view tree: every Orchestrator, its teams, a team's members. Also `path=/events` for the event log | `path` (default `/`, must start with `/`, up to 1024 characters), `wait`, `since`. For `/events`: `since` is `<epoch>.<seq>`, and `sub` (comma list of slice ids or `view:<name>`) with `epoch` reads slices | `200` view JSON carrying `view_schema: 1`; unchanged: `{view_schema, unchanged: true, rev}`; `404` for an unknown path |
| `/v1/view/{name}` | The manifest of a named view | none | `200`; `404 not_found`. Served only when the read model runs |
| `/v1/list/{*id}` | One page of a list | filters as query keys; a repeated key is `400 bad_filter` | `200`; `404 not_found` |
| `/v1/actions` | What can be done at the root | none | `200 {"node": {"kind", "path"}, "actions"}` |
| `/v1/actions/{*id}` | What can be done for one member (`member:<dir id>/<team>/<member>`) | none | `200`; `400 bad_member`; `404 no_node` |
| `/v1/read-model` | Read-model counters | none | `200 {"counters", "parked", "as_of"}`; `404 no_read_model` |
| `/v1/status` | The live status document of a hierarchy directory | `dir` (absolute, required), `since` (revision), `wait` | `200 {"rev", "doc"}`; `404 not_found` when nothing is served for that directory |
| `/v1/inspect/timers` | A dry run of the dispatch timers | `session` | `200 {"timers": [...]}` |
| `/v1/inspect/events` | Unacknowledged events in the Orchestrator's box | `address` | `200 {"events": [...]}` |
| `/v1/inspect/boxes` | Mailboxes on disk, with counts and heartbeat freshness | `address` | `200 {"boxes": [{address, dir, entries, unacked, overdue, adapter, adapter_state, heartbeat_at}]}` |
| `/v1/inspect/entries` | The entries of one mailbox | `address` (required), `filter` (`pending`, `acked`, `all`; default `pending`), `n`, plus caller context | `200 {"address", "entries", "total"}` |
| `/v1/inspect/registrations` | Registered sessions, live or stale | none | `200 {"registrations": [{session_id, address, harness, pid, live, adapter}]}` |

## State

Every piece of shared state belongs to a domain with named operations (see
[Architecture](architecture.md#the-state-hub)). These two routes reach them.

| Route | Purpose | Request | Answer |
|---|---|---|---|
| `GET /v1/state/{domain}/{op}` | A state read | `args` (a JSON object as a string), plus caller context | `200 {"result"}`; `400` (also for an operation that is a write, or that the daemon does not serve); `404 not_found` |
| `POST /v1/state/{domain}/{op}` | A state write | `{"args": {...}, "context": {"cwd", "hierarchy_dir", ...}, "op_id": "<string>"}`. `op_id` is required and must be fresh for each write; sending the same one again is safe and does not repeat the write | `200 {"result"}`; `400`; `404`; `413`; `422 {"error": {"code": "op_failed", "message", "error_code"}}` when the operation itself failed; `5xx` when the domain lock could not be taken in time |

The daemon runs one write per domain at a time.

## Mail

Mail addresses are `name@team`; the Orchestrator of a team is `orchestrator@<team>`. How delivery works is in
[Mail](mail.md) and the [Comms protocol](comms-protocol.md).

| Route | Purpose | Request | Answer |
|---|---|---|---|
| `POST /v1/send` | Send a mailbox entry. The daemon routes it and starts the delivery steps | `to` (required); optional strings `from`, `session_id`, `id`, `kind` (`brief`, `report`, `note`, `event`), `ref`, `text`, `event`, `reply_to`, `to_session`, `eta`, `supersedes`, `post_link`; optional object `data`; plus caller context | `200` the entry as sent (`id`, `to`, `route`, …). Refusals: `403` with `from_mismatch`, `outside_team`, `not_linked`, `link_exhausted`, `broadcast_orchestrator_only`, `removed_member`, `team_gone`, `team_mismatch`, `team_unreadable` or `team_unresolved`; `503 mirror_failed`; `400`, `404`, `413` |
| `GET /v1/inbox` | Read a mailbox, waiting for new mail if asked. With `adapter`, each pull also counts as a heartbeat | `address` (required), `since` (byte offset; required with `wait`), `wait`, `adapter`, `session_id`, `poll_ms` (default 5000; a heartbeat stays fresh for three polls), `all`, `overdue` | `200 {"address", "entries": [{id, from, kind, text, ref, event, created, …}], "next"}` |
| `POST /v1/ack` | Acknowledge entries that reached a turn | `address`, `ids` (list), `by` (one of `prompt-hook`, `adapter`, `cli`, `sendmessage`, `watcher`, `paste`, `stop-hook`), optional `session_id` | `200 {"acked", "unknown", "fresh"}` |
| `POST /v1/decline` | The carrier of a relay ends it | `address`, `id`, `reason` (`already-reported`, `superseded`, `withdrawn`), optional `note` (up to 200 characters), `session_id` | `200 {"declined", "by", "reason"}`; `decline_refused`; `busy` |
| `POST /v1/register` | Register a session under its addresses. A custom harness must be admitted first | `session_id`, `addresses`, `boxes` (address to absolute directory), optional `team_file`, `harness` (default `claude`), `pid`, `pid_start`, `pane_id`, `cwd` | `200 {"ok": true}` (with a `warning` when `pid` is missing, which leaves the registration unbound); `403 not_admitted` or `pid_occupied`; `400`; `413` |
| `GET /v1/due` | The Claude Code mod's long poll: is any mail due for my session | `in` (a JSON request, required), `wait` (1 to 25, default 25) | `200` the due answer. The caller must be the named session. Also records a heartbeat for that session's boxes |

## Joining as a custom harness

An agent runtime other than Claude Code or Codex can ask to join a team. The rules are in the
[Participation contract](participation.md); the routes are:

| Route | Purpose | Request | Answer |
|---|---|---|---|
| `GET /v1/contract` | The contract as JSON | none | `200 {"contract_version", "protocol", "requirements", "routes", "enums", "rules", "conformance"}` |
| `GET /v1/contract/guide` | The contract as a markdown guide | none | `200` |
| `GET /v1/contract/teams` | Live teams a harness may ask to join | none | `200 [{"team", "orchestrator_live"}]` |
| `GET /v1/contract/join` | What `register` needs for an admitted member | `address` (required) | `200 {"address", "role", "team_file", "boxes", "hierarchy_dir", "poll_ms"}`; `404 not_admitted` |
| `GET /v1/contract/role` | The behavioural text of a built-in role | `role` (required) | `200 {"role", "text"}`; `404` |
| `POST /v1/join` | Ask a team's Orchestrator to admit this harness | `team`, `harness` (a new name: lower-case letters, digits and `-`, 2 to 32 characters, not `claude`, `codex`, `claude-mod`, `stub` or `probe`), `session_id`, `why` (1 to 500 characters); optional `pid`, `name_hint`, `role_wanted` | `202 {"join_id", "status": "pending", "poll"}`; `400`, `404 unknown_team`, `409 no_orchestrator`, `429 join_cooldown` (with `retry_after`) |
| `GET /v1/join/{id}` | The outcome of a join; waits up to `wait` seconds | `wait` | `200 {"join_id", "status": "pending", "accepted", "denied", "expired" or "failed"}` with the details that status carries; `404`. An undecided join expires after 10 minutes |

## QA queue

Six `POST` routes behind `ah qa`. They need a session (or you) as the caller, proven by the process tree, and each
takes a JSON object. Failures use `{"error": {"code", "message"}}`: `403` for `qa-caller-unproven`, `not-claimer`
or `not-allowed`; `404 qa-unknown-entry`; `409 qa-not-claimed`; `503 qa-store-busy`; anything else is a `400`
such as `qa-bad-request`. A success is `{"entry": <job> or null, "reason": <text or null>}`, or
`{"entries": [<job>, …]}` for lists.

A **job** has: `id`, `sha`, `base`, `set`, `repo`, `fresh`, `priority`, `requesters`, `team`, `created`, `state`,
`claimed_by`, `claimed_at`, `result_path`, `verdict`, `ended_at` and `reason`.

| Route | Purpose | Request |
|---|---|---|
| `POST /v1/qa/submit` | Queue a job | `repo` (absolute path of the team's repository or one of its work trees), `sha`, `set` (all required); optional `base`, `fresh`, `priority` (`gate`, `lane`, `adhoc`), `session_id` |
| `POST /v1/qa/next` | Claim the next job. A `null` entry means nothing is waiting | optional `max_set` (`full` or `other`), `session_id` |
| `POST /v1/qa/done` | Finish a claimed job | `id`; `result` or `error`; optional `session_id` |
| `POST /v1/qa/release` | Give a claimed job back. Allowed for you, the session that holds the claim, or the Orchestrator of that session's team | `id`, optional `session_id` |
| `POST /v1/qa/status` | One job by `id`, or the open jobs with no `id` | optional `id` |
| `POST /v1/qa/list` | List jobs | optional `all` (include finished ones) |

## Spawn admission and owner controls

| Route | Purpose | Request | Answer |
|---|---|---|---|
| `POST /v1/load/admit-spawn` | May a new member start now? It only paces starts when the machine is busy and protects nothing. An admitted start is recorded unless it is a dry run | `{"dry_run": true}` (optional; any other field is ignored) | `200 {"admitted", "next_at", "triggers"}`; `400` when the body is not an object |
| `POST /v1/codex/answer` | Answer a request the Codex app-server adapter holds for a member. Only you or that team's Orchestrator | `member`, `team`, `request_id` (all required); optional `choice`, `answers`, `text`, `session_id` | `200 {"status": "answered" or "needs-user", …}`; `400`; `403`. The Orchestrator's answers are limited by configuration and logged to `codex-grants.jsonl` |
| `POST /v1/rules` | Write a rule of engagement. Used by `ah rules add\|edit\|off\|remove\|propose\|accept` | `op`, `dir`, `level`, `key` (all required); `scope`, `text`, `said`, `applies`, `choose` | `200 {"code", "message", "rule"?, "live"?}`, where `code` is the verb's exit code (0 ok, 1 not found, 2 refused, 3 conflict), also for a caller other than you or the top-level Orchestrator, or an Orchestrator with no `said`. `propose` writes a decision rule (`kind: decision`, not accepted) and any lead may send it (you, the top-level Orchestrator or a sub-Orchestrator), with no `said` needed. `accept` is yours alone: any other caller gets `code` 3. Editing a decision rule's `text`, `applies` or `choose` makes it proposed again; `400` (flat error form); `503` when the daemon has no state database |
| `POST /v1/bare/recover` | Control the daemon's relaunch of sessions it did not start (`ah roster recover`) | `hierarchy_dir`, `team`, `member`, `op` (`show`, `hold`, `release`, `now`, `enable`, `disable`, `status`) | `200`; `400`; `403` (`enable` is yours alone); `404`; `503 unavailable` when the bare-session watch is not running |

## The browser bridge

`ahd ui` shows the daemon's views in a browser. That needs a normal web listener, so while at least one lease is
held the daemon opens a **separate** listener on `127.0.0.1`, on the port `<ah home>/run/ui-port` remembers (an
ephemeral one the first time, or when that port is taken). It exists only while a lease is held, and a held lease keeps the daemon from exiting when idle. It is switched off entirely by `AH_UI=off` in the
daemon's environment. The page's address is `http://127.0.0.1:<port>/ui/`; `<ah home>/run/ui.json`
records the port while it is up.

There is no sign-in: any tab on the machine that opens the address (a bookmark, a reload, after a daemon restart) is
served while the bridge is up. What keeps other web pages in the browser out is the request guard. Every request to the
bridge is checked, in this order:

1. `Host` must be `127.0.0.1:<port>`, `localhost:<port>` or `[::1]:<port>`, else `421`. This defeats DNS rebinding.
2. If `Origin` is present it must be `http://` and one of those hosts, and `Sec-Fetch-Site`, if present, must be
   `same-origin` or `none`, else `403`. This holds for every request, not only writes.
3. Every response carries a strict `Content-Security-Policy`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`,
   `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`.

The guard does not keep out a local process that sends its own headers: any process on the machine that can reach
`127.0.0.1:<port>` can read the views and use the write routes below. A daemon that starts and finds
`stopped_reason: daemon_ended` in `run/ui-service.json` spawns the holder itself, with no browser, so open tabs keep
reading.

| Route | Purpose |
|---|---|
| `GET /ui/` and `GET /ui/{file}` | the page's files (`.html`, `.js`, `.css`, `.svg`, `.woff2` only; no directory listing) |
| `GET /ui/v1/view`, `/ui/v1/view/{name}`, `/ui/v1/read-model`, `/ui/v1/list/{*id}`, `/ui/v1/actions`, `/ui/v1/actions/{*id}` | read-only copies of the matching `/v1/...` routes. The page reaches panes, state and mail only through the write routes below |
| `POST /ui/v1/answer` | the user's answer to a parked decision; the request guard is the proof of the user. The 200 reply `{ok, id, answers, decision}` carries `event_id`, the mailbox entry of the `question-answered` event the answer sent (the earlier entry when an identical one is still un-acked), or `event_error` when the Orchestrator could not be told; neither when the event was dropped |
| `GET /ui/v1/answer-status?dir=<dirId>&team=<team>&event=<event_id>` | whether that team's Orchestrator has seen the event: `{seen, seen_at?, state}`, `state` as `ah msg status`. Seen is a delivery into its context or an ack, the earlier time in ms; an ack that superseded or resolved the event, and a declined, withdrawn or superseded entry, are not seen. 404 `not-found` alike for an unknown dirId, an unknown id, and any entry that is not a `question-answered` event in that Orchestrator's box. Reads only |
| `GET /ui/v1/history?dir=<dirId>&id=<thread id>` | one thread of the user's question log: the body `ah msg history --json --id` prints, `{threads, dir}` (no thread of that id is an empty list). A dirId that resolves to nothing, or an empty id, is 404 `{"error":"not-found"}`. Reads only; the herdr mod's History uses it |
| `GET /ui/v1/history?dir=<dirId>[&q=][&team=][&since=][&state=][&questions=][&before=][&limit=]` | with no `id`, a page of the dir's threads, newest first, through the same filter as `ah msg history`: `q` plain text in any case, `team` (a thread with no team shows under every team), `since` epoch ms, `state` `answered` or `unanswered`, `questions` `1`/`true` or `0`/`false`, `before` a cursor `<at>:<id>`, `limit` 1 or more (default 20, at most 100). An empty parameter is absent. Answers `{threads, dir, next}`: each reply's `text` and `final` is cut to 600 UTF-8 bytes and marked `cut: true` (the `id` read gives the whole thread); `next` is the cursor for `before`, or null on the last page. Replies with no typed prompt are left out. A dirId that resolves to nothing is 404; a malformed or repeated parameter is 400 `{"error":"bad-query"}`. Reads only |
| `POST /ui/v1/team-close`, and on the socket `POST /v1/team/close` | close or reopen a team, body `{dir, team, reopen?, preview?, hold?, confirm_live?}`, up to 4 KiB. Closing writes `closed: {at, by}` on the team record and on each confirmed lane child, and deletes nothing. `preview` answers `{live, unknown, left_running, held, warnings}`. Members with a live verdict end only when every one is named in `confirm_live`; otherwise 409 `live-changed` with a fresh `preview` and no marker. An Orchestrator session is never ended, and it does not hold a closed team on show. `hold` pauses and closes nothing: ahd runs `ah roster quit` for the team (members save their work, the hold is sealed with its resume plan), waiting at most 10 s per call, and answers `{hold, phase: "winding-down"\|"held", sealed, waiting_on, unanswered}`; send it again until `sealed`, then close. A quit's own refusal (a stopped team) is a 409 with its words. A reopen that removes a marker records `reopened: {at, by}`. 409 also: `unknown-dir`, `unknown-team`, `lane` (close its parent; a lane can be reopened). On the bridge, the request guard is the proof of the user; on the socket, any caller but the user gets 403 `user_only` |
| `GET /ui/v1/rules?team=<team>`, `POST /ui/v1/rules` | the rules of engagement of the named team's repo, under the session; the client never sends a directory. `GET` answers `{code, team, rows, resolved: {repo, team, items: {<work id>: {rules, off}}}, items: [{id, kind, title}], max_text, live}`: `rows` as `ah rules list --all`, each `{rules, off}` as `ah rules list --json`, `items` the open projects and goals on the backlog or in that team. `POST` takes `{op, team, level, scope?, key, text?, applies?, choose?}`, `op` one of `add`, `edit`, `off`, `remove`, `propose`, `accept`, up to 4 KiB, and writes with `by: "user"` and no `said`; `accept` stamps the decision rule `accepted_by: "user"` and its answer carries `via: "web"`. A decision rule's row adds `kind`, `applies`, `choose`, `proposed`, `accepted_at` and `accepted_by`; a team-level write with no `scope` is the named team's. It answers `{code, message, rule?, live?}`. `code` is the verb's exit code and sets the status: 0 is 200, 1 is 404, 2 is 400, 3 is 409. A team no repo has, or one that more than one repo has, is 400. 503 `unavailable` when not wired |
| `GET /ui/v1/looks`, `POST /ui/v1/looks`, `POST /ui/v1/looks/merge` | the saved looks in `<AH_HOME>/looks.json`, under the session. `GET` answers `{path, rev, corrupt, looks}`. `POST` takes `{rev, looks}` and answers 409 `stale` when `rev` is no longer the file's. The merge takes a looks object and only adds. A file that does not read as looks answers `corrupt` (409 on a write) and is left alone. Bodies up to 1 MiB |

The idle limit for the bridge is 15 minutes with no request to a `/ui/v1` route, unless a background `ahd ui start`
holds it.

## Versions

Three different version numbers exist, and they are easy to confuse.

- **Wire protocol.** `protocol: 1` in `/v1/health`, in the `Ah-Protocol` header and in `ahd.json`. The health
  answer also lists `protocols: [1, 2]`, but the daemon serves protocol 1 only. A client refuses a daemon whose
  `protocols` does not include 1 and uses the files instead. The contract document served at `/v1/contract` has its
  own `contract_version`, currently `1.1`.
- **Plugin version**, for example `2.0.46`: in `/v1/health`, the `Ah-Daemon-Version` header and `ahd.json`. If
  the client is newer than the daemon, a normal client drains the old daemon and starts a fresh one; a hook only
  asks the old one to drain and uses the files this time. A client that is older than, or equal to, the daemon is
  accepted as it is. `ahd version` prints the verdict: `equal`, `client-newer`, `client-older` or `unsupported`.
- **Build contract.** A whole number baked into the binary, printed by `ahd --version` as `(contract 1)` and
  compared by `ahd where` and by `ahd tui`. It is not the wire contract version.

The daemon never refuses a client over a version difference.

## When there is no daemon

A client treats these answers as "use the files instead": the daemon switched off with `AH_DAEMON=off`, not
running or not startable, a connection error, a timeout (30 seconds by default), any `5xx` other than
`mirror_failed`, and a `404` with code `not_found`. Any other answer, including a `403`, is the daemon's real
reply. The client then reads or writes the files directly and records the fallback in
`<ah home>/logs/fallback.jsonl` as `{at, verb, reason}`.

A failed start leaves `<ah home>/run/ahd.spawn-failed`, and the next implicit starts wait 60 seconds. An explicit
`ahd start` ignores the marker.

Only a running daemon can do: the delivery steps, dispatch timers, links, joins by custom harnesses, the QA queue,
spawn admission and the live views.
