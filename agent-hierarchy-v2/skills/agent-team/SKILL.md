---
name: agent-team
description: Stands up, inspects, reshapes, or tears down a live Team of agent sessions from the existing agent-hierarchy roster. Use for /agent-team, for "set up my team", "set up a team", "spawn the team", "spawn my team", "spin up the team", "start the team", "spawn the architect", "spawn just the reviewer", "add a peer to the running team", "dismiss a member", "dismiss the architect", "remove the architect", "close that session", "close the sessions", "kick the reviewer", "dismiss the team", "close the team", "disband the team", "disband my team", "shut down the team", "tear down the team", "end the team", "stop tracking a member", "leave it running but forget it", "untrack the team", "open a stream", "start a stream", "show stream status", or "wrap up the stream".
argument-hint: "[create [auto|manual]|spawn-one <role>|spawn-ad-hoc <role>|stream-open <name>|stream-status|stream-done <name>|dismiss <name> [<name>…]|disband|untrack|teams|resync|move|adopt|reap|history]"
---

# agent-team

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

A **Team** is a live instance: real sessions, real panes, recorded in a team
file under `.claude/ah2/teams/`. It is created FROM the roster, which is
a template. This skill owns every process lifecycle operation — create, spawn,
dismiss, disband, untrack, move, resync, adopt, reap — and writes only the team file.

**It never edits the roster.** Changing WHO belongs on the roster is the
`wrangl:agent-roster` skill's job (`init`/`add`/`edit`/`remove`), and those commands
edit a template for FUTURE teams. They are refused outright while this session
owns a live team. To add a member to the RUNNING team, including one that
diverges from the roster or a role the roster does not define, use
`spawn-ad-hoc`.

If a roster already exists and you just need a live Team — including at a
worktree, which usually inherits the repo's existing roster — go straight to
`create`. You do NOT need to add/edit/remove roster members first. Inspect the
roster with `<ah> roster show --cwd <abs cwd>`.
Roster levels and their resolution order are documented once, in the
`wrangl:agent-roster` skill's § Levels.

## One peer, zero ceremony

When the request names (or clearly implies) ONE role, skip everything below:

- `<ah> roster spawn-ad-hoc <role> --cwd <abs>`
  works in any repo, roster or not — no skill load. It prints the member's `name` (renamed or not).
  Add `--here` (your own tab) or `--tab <t> [--split right|down]` to place it; its `next.brief` line briefs it. One call, no `move`.
- Brief it with `<ah> msg new --to <role> --from <your role> … --body-file <file>` (an Orchestrator or Sub-Orchestrator must also give `--title "<few plain words, verb first>"` naming the outcome, e.g. 'Make UI slick': no slugs, ids or file names, an issue number optionally in brackets)
  (the brief's text goes in the same call), then run `<ah> msg send <address> --ref <id> --kind brief` and do exactly
  what its `delivery:` line says, and no more.
- A subagent may spawn and brief a peer, but the reply is delivered to its
  parent session, never to the subagent — so the subagent must not wait for it.
- When the user names the team, pass that name as `--team <name>`; it is the
  member-name prefix. Do not ask a PEER NAME CONFIRMATION for it.
- If the launch itself fails (exit 2 with a launch error, not a structured
  `refused`), read `reference/escalation.md`.
- The formal path (the rest of this skill) applies only when no single role is
  named, when a whole team is wanted, or for lifecycle ops.

## Rules that hold everywhere

- A brief travels by mailbox: `<ah> msg new …`, then `<ah> msg send <address> --ref <id> --kind brief`
  and what its delivery line says. A report goes back the same way (`--kind report --ref <id>`). A bare
  SendMessage pointer is tracked when it can be, and denied with the command to run when it cannot.

- Never read `.claude/ah2/agent-hierarchy.json` directly: only `show` applies
  its worktree and global fallbacks.
- Under Herdr every session name must be `[a-z][a-z0-9_-]`, at most 32
  characters. Never run `herdr pane split` / `herdr agent start` by hand to
  work around a refusal.
- The roles are `architect`, `docs-writer`, `implementor`, `reviewer`,
  `task-runner`, `ultra-advisor`, `ux-engineer` and custom roles (`<ah> roster role list`; define them with
  `/wrangl:agent-role`). `orchestrator` is rejected by the CLI: it is whatever
  session runs `create`, never a member.
- `dismiss` and `disband` CLOSE sessions; `untrack` closes nothing. Bare
  `dismiss <name>` closes a clean member, else refuses with reasons; `--close`
  closes anyway; `--plan` and bare `disband` only plan. No AskUserQuestion:
  the permission prompt is the user's confirmation.
- "close", "kill", "shut down", "tear down", "get rid of" mean
  `dismiss`/`disband`; "leave it up", "just stop tracking it" mean `untrack`.
  When the words are genuinely ambiguous, ask; do not pick.
- A raw `herdr agent prompt` or `send-keys` to a non-Claude member is denied,
  so every key that reaches one is a relay. Never answer Yes or a granting
  option for a member on your own, and never type free text into a pane. The
  exceptions are named in `reference/escalation.md` (§ The exceptions to
  never granting for a member); there are no others.
- An Ultra-Advisor needs no per-escalation approval (`/pipeline` asks once at
  run start); never declare a model tier yourself.
- There is no cross-role step: never hand one role's work to another role.
- Misplaced peer: relocation comes first; the respawn fallback (`dismiss`, then
  `spawn-one <name> --cwd <expected_root>`) discards context, so use it only
  after relocation failed, and never on a peer `misplaced_unattributed` named.
- Leave a stalled pane running, never closed.

### Refusals

Two refusals always go back to the user. Neither is a launch failure.

**`refused: "team-name-unusable"`** — the team name is the user's choice.

1. Ask with AskUserQuestion. Options: the refusal's `suggestion` first, marked
   "(Recommended)", then Other.
2. Re-run the refusal's `rerun` with `<TEAM>` replaced by the answer.
3. Never pick or sanitize a name yourself.

**`refused: "member-model-undefined"`** — the member has no model and its model
is the user's choice. A role with a shipped member default (today only
`docs-writer` on `sonnet` and `ux-engineer` on `opus`) is not asked.

1. Ask with AskUserQuestion. Options, in this order: the member's `fallback`
   when it has one, then its `allowed` models. Mark none "(Recommended)",
   except for a legwork role (task-runner or a custom legwork role), which gets
   `haiku (Recommended)` first.
2. Re-run the member's `rerun` with `<MODEL>` replaced by the answer.
3. Only a top-level session that cannot ask the user runs `rerun_fallback`; a
   subagent returns the refusal to its caller; with no fallback, stop and report.
4. Never choose a model any other way.

## Where members work

Every member works in the work tree assigned to its work, always; the lead opens it,
never the member. Each piece of work that commits on its own branch gets its own stream, even with one team:
`stream-open <name>`, then `spawn-one <role> --stream <name>` or
`spawn-ad-hoc <role> --stream <name>`; its launch and allowed
directory is then the stream's worktree. A hand-made worktree,
`create --cwd <work tree>`, or switching a member's branch needs a reason you
state in the brief. Without `--stream`, the launch directory is `--cwd`.
Where a member works, scratch and `--allow-main`: `reference/worktree.md`.

**Every member brief opens with this line** (nothing else replaces it):

> `Work tree: <abs path> (branch <b>). Write and run only there; your tmp is $TMPDIR; probes: reference/worktree.md § Probes. The spec and response paths below are the only exceptions. Need another directory? Report BLOCKED with the path; do not ask a peer and do not script around the gate. Commit on <b>; do not push. Parent: <name@team>.`

A member that needs another directory reports BLOCKED to you naming the path and why. Never ask another peer to do the work
and never route around a denial with a different tool, a script or a subagent.

## When an implementor reports done

The Orchestrator makes a ship merge and runs QA beside the review, so a full-suite failure shows before the ship gate:

1. In a scratch work tree, take the run's integration branch tip R (the release branch the run ships to; for a Sub-Orchestrator, its
   lane branch) and `git merge --no-ff <member branch B>` onto a local branch `ah/ship/<slug>`: the commit is sha M.
2. In the same turn dispatch QA `full` at M (base R) and the reviewer on B, in parallel.
3. A merge conflict is a finding for the implementor (merge R into B, report again); the reviewer is not waited on. A QA `FAIL` in range
   goes back to the implementor whatever the review verdict; an out-of-range failure is the Orchestrator's own triage.
4. At ship, M itself is shipped (R fast-forwards to M) only if R's tip is still M's first parent and B's tip is still M's second parent;
   the push then rests on `M/full/result.json`. Otherwise a new merge is made and QA `full` runs on it first. (ah has no push gate; this is the Orchestrator's own rule.)
5. `ah/ship/<slug>` and its scratch tree: never pushed; remove both, unforced, at ship or abandon (`reference/worktree.md`).

A Sub-Orchestrator whose guards refuse the merge asks the top Orchestrator to make it.

## Global flags and `--team`

Every verb runs as `<ah> roster <verb> … --cwd <abs cwd>`
through the Bash tool, with `${CLAUDE_PLUGIN_ROOT}` taken from this session's
`ah CLI root` line (authoritative when the two disagree; the newest wins) and
the cwd the absolute repo path. The plugin's PreToolUse hook allows those calls
without a prompt; a `--close` call still prompts, by design. Output is JSON.
Full verb/flag reference: `docs/cli-tools.md`.

`--team <name>` names a live Team, so one repo can host several, owned by one
orchestrator session or several: it points every verb that reads or writes the
team file at `teams/<name>.json`, and its members are named `<name>-<role>[-N]`.
It never selects a roster block; `create --roster <r>` does.

- **Omitted**, a team verb acts on the Team this session owns, only while this session owns just that one team. With none, the default is
  `teams/<repo basename>.json`, never a shared `team.json`. A pre-existing
  `team.json` keeps working, named by its own members.

**Owning more than one team.** Pass `--team` to every `<ah> roster` team verb
and to `<ah> msg new`; a verb that names a member (`dismiss bar-reviewer`) takes
the Team from the name. `<ah> msg list` covers every owned team and `--team`
only narrows it. The default team is `--team @default`; in
`<ah> roster teams --json` it is the row whose `name` is `null`.

- **Which Team?** When the user's words don't say ("disband the team"), ask one
  AskUserQuestion listing the owned Teams (`<ah> roster teams`, rows with
  `own: true`), multi-select when the request can cover several. Each disband
  keeps its own confirmation.
- **A second Team**: a bare `create` that collides with another live
  orchestrator's default Team refuses with a candidate name; see
  `reference/create.md`. Every later step then needs the same `--team <name>`.
- `<ah> msg new` / `<ah> msg list` also auto-resolve the active team from this
  Team's own orchestrator process (`CLAUDE_PID`, matched against the recorded
  `orchestrator.pid`); pass `--team <name>` whenever that may not fire, such as
  tooling outside the orchestrator's own process.

## Command surface

Read the named file before running the verb. Each file opens with a trigger
line and, past 100 lines, a Contents list.

- `create [auto|manual]` — setting up a whole team: `reference/create.md` § Create.
- `spawn-one <role>` — a role is dead or was never launched: `reference/spawn.md` § `spawn-one`.
- `spawn-ad-hoc <role>` — a member the roster does not define: `reference/spawn.md` § `spawn-ad-hoc`.
- `resync` — re-derives pane locations from herdr: `reference/spawn.md` § `resync` / `move`.
- `move <name>` — relocates a member's pane: `reference/spawn.md` § `resync` / `move`.
- `disband` — closes every member, `untrack --all` for the record only: `reference/teardown.md` § `disband`.
- `dismiss <name>` — closes one member: `reference/teardown.md` § `dismiss`.
- How many members, reuse or fresh spawn, when to dismiss: `reference/capacity.md`.
- `untrack <name>|--all` — forgets a record, closes nothing: `reference/teardown.md` § `untrack`.
- `adopt [--orchestrator-pid <pid>]` — re-stamps `orchestrator.pid` on an ORPHANED team; recovery only, it
  refuses to hijack a live team. `reap [--commit]` lists orphaned team records, or removes them with `--commit`,
  except one live sessions still depend on (`kept`). Read `reference/teardown.md` § An orphan that live sessions depend on.
- A close plan came back empty: `reference/teardown.md` § Plan came back empty — search before you say so.
- A peer reports it is misplaced or asks for a respawn, or HIERARCHY STATE shows `misplaced peer(s)`: `reference/teardown.md` § Misplaced peer: relocation fallback.
- `deliver <name>`, `answer <name>` — briefing a `route: pane` member: `reference/pane-members.md` § Dispatching to a `route: pane` member.
- A member stopped at a prompt (`blocked`): `reference/pane-members.md` § Relaying a prompt.
- `stream-open`, `stream-status`, `stream-done`: `reference/streams.md` § Streams.
- A member's scratch directory, or a member reports BLOCKED on a path: `reference/worktree.md`.
- A release was pushed and the build must be reinstalled: `reference/dogfood.md`.
- A role could not launch: `reference/escalation.md` § When a role can't take the work.
- A design, review or implement role cannot launch: `reference/escalation.md` § Design, review and implement roles (custom included).
- An Ultra-Advisor escalation: `reference/escalation.md` § Ultra-Advisor escalation (advise class, custom included).
- A peer that never replies: `reference/escalation.md` § A stalled peer: three pings, then take over.
- The user declined a spawn command: `reference/escalation.md` § Declined spawn.

Verbs that need no file:

- `teams [--cwd <path>]` — read-only; every team file with member count,
  orchestrator pid and liveness, and whether it is this session's own. Rows
  carry `untracked_live` (live peers attributed to that team with no row;
  close one with `dismiss <pane_id | role@sid8>` or `disband`; entries marked
  `source: "herdr"` are herdr agents named by convention with no registry row)
  and a top-level `untracked_live` for peers attributed to no team.
  `misplaced_members` lists peers confirmed relocated away from where the Team
  expects them; `misplaced_unattributed` counts misplaced peers this session
  could not safely attribute: do not guess one. A misplaced peer is never
  refused.
- `history [--json]` — recent team-history entries, the input to `create --from`.
- `checkin [--team <T>] [--cwd <path>]` — re-registers the current session's cwd.
- `whoami [--team <T>] [--cwd <path>]` — read-only, for a peer: which team
  member this session's pane is, its team file, and its orchestrator's
  `pid`/`live`/`send_to`. `reason` says why a lookup came back empty:
  `no-pane-id`, `no-team`, `not-a-member`, or `ambiguous` (with `candidates`;
  re-run with `--team`). The brief's `reply-to` (or the orchestrator's session
  name from `ListAgents`) is authoritative; `send_to` is a best-effort
  fallback and is `null` whenever the orchestrator is not provably reachable.
- `tier set <kind> <model> <haiku|sonnet|opus|fable>`, `tier remove <kind>
  <model>`, `tier list` — how a non-Claude model compares with Claude's tiers,
  kept in the global config only.

Before any push outside a pipeline run, follow `skills/autonomous-pipeline/reference/model-secret-scan.md` (chunked scan).

To hand a whole workstream to a sub-orchestrator (its own sub-team, one status record, one green-lit close), follow `skills/agent-team/reference/workstreams.md`.

## Question tag

Every `AskUserQuestion` in this file carries `metadata.source` `ah:bootstrap:agent-team:refusal`; the hook refuses an untagged ask from the Orchestrator.
