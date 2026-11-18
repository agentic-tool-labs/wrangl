---
description: Enable/disable the hierarchy and its handoff flow, or inspect it. Model/roster assignment lives in /wrangl:agent-roster.
argument-hint: "[init|status|on|off|always-on [on|off]|flow [auto|confirm]|qa [item|batch]|dispatch [peers|subagents|prefer-peers|prefer-subagents]|usage [day|week|month|all]|msgs [open|closed|all|off|required]|peers|sweep [days]|install|uninstall]"
---

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

The user ran `/hierarchy` with argument: `$ARGUMENTS`

agent-hierarchy stores its config as JSON at `.claude/ah2/agent-hierarchy.json` in
one of two scopes:

- **User** — `~/.claude/ah2/agent-hierarchy.json` (applies to every repo)
- **Project** — `<repo>/.claude/ah2/agent-hierarchy.json` (this repo only, committable)

Config shape (`version` is the schema version, always `1`):

```json
{
  "version": 1,
  "enabled": true,
  "roles": {
    "ultra-advisor": { "model": "fable", "dispatch": "peer", "peer": "auto" },
    "architect":     { "model": "opus" },
    "reviewer":      { "model": "opus", "dispatch": "peer", "peer": "custom-reviewer-peer" },
    "implementor":   { "model": "inherit" },
    "task-runner":   { "model": "haiku", "delegate": "task-gopher" }
  }
}
```

A config written before Ultra-Advisor existed is still valid: a missing
`ultra-advisor` key resolves to the shipped default (`fable`) with no warning,
so `version` stays `1`. Same for `dispatch`/`peer`, added later: a role with
neither key (every config written before this feature existed) resolves to
`"dispatch": "peer", "peer": "auto"` — the named-peer-session route via the
`<repo>-<role>` convention, exactly what every role already did by default
before `dispatch` existed. `dispatch: "model"` is for a legwork-class role
only: a chain role (Ultra-Advisor, Architect, Reviewer, Implementor, or a
custom chain role) runs only as a peer, so never write `"model"` for one — a
`"model"` already there is ignored, reported by `status` and `doctor`, and
removed at the next `ah roster` write to that file. There is no route opt-in
either: never write a top-level `route` other than `"peers"`. `peer` is
only consulted when `dispatch` is `"peer"`: `"auto"` uses the `<repo>-<role>`
convention name; any other string (see `reviewer` above) is an explicit peer
session name. `task-runner` has no `dispatch`/`peer` concept — it keeps its
own `delegate: "task-gopher"` mechanism.

A role's `peer` name does not have to be settled through this wizard: this
document's steps 6-9 below run the settling once, up front. A role that
reaches a session with `peer` still `"auto"` — never run through `init`, or
hand-added to the config — gets settled lazily instead, the first time the
Orchestrator actually needs to dispatch it: the injected protocol's PEER NAME
CONFIRMATION section walks it through the same ranking, the same
AskUserQuestion shape, and writes the same recorded value back to config.
Either path is a one-time question per role per repo, not a per-dispatch one.

Resolution rules you must respect in everything below:

- Merge is **shallow per role**: a role object in the project config replaces the
  user-scope role object entirely — never merge key by key.
- `enabled`: **most-specific scope wins** (project overrides user).
- Valid models are **per role**. `architect`, `reviewer`, and `implementor` are
  reasoning roles: `opus`, `sonnet`, `fable`, `inherit` only — **`haiku` is
  never valid for a reasoning role** and must never be offered or written for
  one. `task-runner` (legwork, no reasoning) additionally allows `haiku`.
  `ultra-advisor` is the escalation apex and is **top-tier only: `fable` or
  `opus`** — not `sonnet`, and not `inherit` (inheriting a lesser session model
  would make the tier meaningless), so its model is always explicit.
  `inherit` means "omit the `model` parameter on the Agent call" — it is a
  legal value in this JSON file only, and must never be passed to the Agent
  tool literally.
- Built-in roles are `ultra-advisor`, `architect`, `reviewer`, `implementor`,
  `task-runner`. The **Orchestrator is not configurable** — it is always the
  session agent itself.
- **Custom roles** are further `roles.<name>` rows with a required `class`
  (`advise`, `design`, `review`, `implement`, `legwork`); the class sets every
  gate and the model allowlist above (advise = the ultra-advisor list, design /
  review / implement = the reasoning list, legwork = + `haiku`). Optional keys:
  `agent` (default: the name), `label`, `description`, `model` (default
  `inherit`; advise needs `fable` or `opus`), `dispatch` (`model` only for
  legwork), `peer`, and `routes`,
  which makes a chain-class role an in-chain **alternative** to its step's
  built-in for that work (without it, a side role). A built-in row may set
  `agent` to override the agent it launches. Every custom or overridden agent
  file must pass its class's **tool contract** (review may not Edit or Write;
  design needs Write; every chain class needs Read and SendMessage); a failing
  one is marked unavailable. Never hand-write these rows — use `/wrangl:agent-role`
  (it drives `ah roster role list|set|remove`).

The resolved table is printed by the plugin's own resolver, so it can never
drift from what the hook injects. Run it with Bash from the repo you care about,
using the absolute root from this session's `ah CLI root` line:

```
<ah> roster doctor --cwd <abs cwd>
```

`doctor` is read-only and reports the resolved config alongside version,
install path, identity, runtime dir, team, peers, hook errors and hook syntax.
If no root line is in context, resolve the root with the one recipe in
[docs/cli-tools.md](../docs/cli-tools.md).

Pick the ONE case matching the argument:

---

## empty or `init` — enable + handoff flow

This wizard only turns the hierarchy on and sets its handoff flow. Model and
roster assignment (which roles exist, their model/effort/route) lives in
**`/agent-roster init`** — see the handoff in step 6 below.

1. Tell the user the **Orchestrator is fixed**: it is this session's own agent,
   whatever model the session runs on.
2. Check for existing config: read `~/.claude/ah2/agent-hierarchy.json` and
   `<cwd>/.claude/ah2/agent-hierarchy.json` (either may be absent). Also check
   whether task-gopher is installed — the presence of a `task-gopher:task-gopher` agent type.
3. **Task-Runner is decided by the task-gopher check, not by a question:**
   - **task-gopher installed** → do NOT ask about Task-Runner. Auto-assign
     `roles.task-runner`: `{ "model": "haiku", "delegate": "task-gopher" }`
     and tell the user: "Task-Runner: deferring to task-gopher (already
     installed)." An existing explicit Task-Runner value in the config being
     prefilled still wins — the user chose it once; keep it and say so.
   - **task-gopher NOT installed** → ask one question: options
     `Install task-gopher (recommended)`, `haiku`, `sonnet`, `opus`. If they
     pick the install option: write `roles.task-runner`:
     `{ "model": "haiku", "delegate": "task-gopher" }` and tell them to run
     these two lines themselves, in order (you cannot run `/plugin`):
     `/plugin marketplace add agentic-tool-labs/agent-tools`, then
     `/plugin install task-gopher@agent-tools`. It is optional. Until it is
     installed, the directive's fallback rule routes
     Task-Runner work to the bundled `wrangl:task-runner`, so
     nothing breaks in the meantime. Any other model typed via "Other" is
     accepted as-is (`haiku` is valid for this legwork-only role).
4. **AskUserQuestion call — scope, flow, QA and dispatch.** Two questions in
   one call, plus one each for QA and dispatch when not configured:
   - **Scope**: "User (all repos)" and "Project (this repo, committable)". If a
     config already exists at either scope, say so in the question header and
     mark that scope's option as the existing one.
   - **Handoff flow**: "Automatic handoffs (Recommended)" — the Orchestrator
     runs the chain itself and reports; and "Confirm each handoff" — before
     every reasoning-role dispatch (Architect, Implementor, Reviewer,
     Ultra-Advisor) the user is asked to approve, redirect, or skip it. Say in
     the description that this is switchable at ANY time afterwards — via
     `/hierarchy flow` or just by telling the Orchestrator mid-session — so
     nothing is locked in here. Prefill from an existing config's `handoffs`
     key if present.
   - **QA** (asked only when it is not configured: the resolver output has no
     `qa cadence` line): "When should QA run?" — "On each item's merge" (full
     QA when each item merges; the Orchestrator is prompted after every
     implementor DONE) and "Once per release batch" (full QA once, on the
     release batch's final commit; no per-item QA prompt). When a value
     exists, do not ask: state it in one line (the `qa cadence` line). Either
     way QA can still be dispatched by hand at any time.
   - **Dispatch** (asked only when no config has the `dispatch` key: the
     resolver output has no `dispatch` line): "How should the Orchestrator
     run its roles?" —
     "Always peer sessions" (each role in its own terminal; you can watch it,
     answer its prompts, and it is tracked by the daemon: timers, mail,
     alerts, Graph, Board);
     "Always subagents" (each role runs inside the Orchestrator's session;
     cheaper to start and no extra terminals, but it cannot be messaged while
     it works and the daemon's timers, mail and alerts do not cover it);
     "Prefer peers" (the Orchestrator may use a subagent when it judges it
     better); "Prefer subagents" (the Orchestrator may use a peer when it
     judges it better). When a value exists, do not ask: state it in one line
     (the `dispatch` line).
5. Write the JSON with the Write tool to the chosen scope's path (`mkdir -p`
   the `.claude` directory first if needed), preserving every other existing
   key at that scope. Set `"version": 1`, `"enabled": true`, `"handoffs"` to
   the flow answer (write it explicitly even for `"auto"`, so the file
   documents the choice), `cadence` inside the `qa` object to the QA answer
   (`"item"` for "On each item's merge", `"batch"` for "Once per release
   batch") when it was asked — create the `qa` object if absent and preserve
   every other key in it (the QA scheduler's `parallel`, `admit_load_ratio`,
   `admit_free_mem_gib`) — `"dispatch"` to the dispatch answer (`"peers"`,
   `"subagents"`, `"prefer-peers"`, `"prefer-subagents"`, in the order the
   choices are listed) when it was asked — and `roles.task-runner` from step 3. If the user answered the QA question
   in their own words (its Other), follow the **Recording the user's QA
   rule** paragraph under `qa` below as well. Refresh the
   hierarchy status file with Bash,
   `<ah> roster status --plain --cwd <abs cwd>`; its
   one-line output need not be shown. Echo the
   resolved effective table (resolver command above), and close with the
   propagation note: This session's hooks pick it up from your next message
   (the `.claude/ah2` directory, the `ahd tui` listing, state writes). The
   SessionStart guidance loads at a session's next start, `/clear` or
   compaction, in this session too; run `/clear` if you want it now (it clears
   the conversation). Other live sessions pick everything up the same way.
6. **Initial Setup trigger.** Run `resolveRoster(cwd)` (or read `.roster`/
   `.rosterLevel` off the resolver output). If it is `null` at all three
   levels (global, repo, repo-user — no roster configured anywhere), hand off
   directly into `/agent-roster init`'s flow now: its level question first,
   then its per-role walk. If a roster already resolves, just say so and
   point at `/agent-roster show` — do not re-run its wizard.

---

## `status`

Run the resolver command above and show its output verbatim: whether the plugin
is configured/on/off, which file each scope resolved to, the effective model for
each role, which scope each value came from, any shadowing or validation
warning, and the propagation note. Do not recompute the table yourself.

The resolver output also carries a **roster** section (winning level, path,
each member's name/role/model/effort/route/auto-mode) when a roster resolves,
and a **Team** section (active `team_id`, or "none active") when `team.json`
exists — both are printed as part of the same verbatim output, nothing extra
to run.

---

## `set <role> <model>` — moved

Superseded by `/agent-roster edit`, which operates on roster members instead
of the legacy `roles` table. Point the user at
`/agent-roster edit --member <name> --model <model>` (or `/agent-roster add`
if the role isn't in the roster yet).

---

## `on` / `off`

Set `"enabled": true` / `false` in the most specific config that already exists
(project if present, else user), preserving all other keys. If neither exists,
say so and suggest `/hierarchy init`. Then refresh the hierarchy status file
with Bash, `<ah> roster status --plain --cwd <abs cwd>` (its
one-line output need not be shown), and echo the resolved table and the
propagation note.

---

## `always-on on` / `always-on off`

Set `"alwaysOrchestrator": true` / `false` in the most specific config that
already exists (project if present, else user), preserving all other keys. With
it on, a plain session in this scope gets the directive at every start, without
`/wrangl:orchestrator`. If neither exists, say so and suggest `/hierarchy init`.
Then refresh the hierarchy status file with Bash,
`<ah> roster status --plain --cwd <abs cwd>` (its one-line output need not be
shown), and echo the resolved table and the propagation note.

Note when turning it **off**: the SessionStart hook goes fully silent — no
protocol directive and no setup nudge — from the next session start onward, and
for the rest of *this* session you should stop routing work through the role
chain and handle it yourself.

---

## `flow [auto|confirm]`

Who advances the chain. `auto` — the Orchestrator performs handoffs itself and
reports (the default). `confirm` — before each reasoning-role dispatch
(Architect, Implementor, Reviewer, Ultra-Advisor, including review-loop
re-dispatches) the user is asked to approve, redirect, or skip. Legwork
dispatches (Task-Runner / task-gopher) are never gated — errands are not
handoffs.

- **No argument**: run the resolver command above and report the `handoff flow`
  line, plus one sentence on what each mode means.
- **With `auto` or `confirm`**: set `"handoffs": "<value>"` in the most
  specific config that already exists (project if present, else user),
  preserving every other key, with the Write tool. If neither config exists,
  say so and suggest `/hierarchy init`. Then echo the resolved table and the
  propagation note — and **honor the new mode immediately in this session**;
  the config write is for future sessions, not a restart requirement.
- Anything else as the argument: show the two valid values and stop.

This switch belongs entirely to the user, at any time, in both directions. If
they ask for it mid-conversation in plain words ("ask me before each handoff",
"stop asking, just run it"), do exactly what this section says without making
them invoke the command.

---

## `qa [item|batch]`

When full QA runs. `item` — QA at each item's merge: after an implementor
reports DONE, the Orchestrator is prompted at stop (a few times) until it
files the QA request. `batch` — QA once on a release batch: no per-item QA prompt; a
release still waits for full QA on its final commit. The setting is
`qa.cadence`: the `cadence` key inside the top-level `qa` object, which also
holds the QA scheduler's `parallel`, `admit_load_ratio` and
`admit_free_mem_gib`. Not configured (no known `qa.cadence` value in any
config) — the per-item prompt is raised once per DONE report and names this
command. The key's scope and precedence are those of `handoffs`: the most
specific config that has `qa.cadence` wins. An unknown value, or a `qa` that
is a string rather than an object, is reported by `doctor` and treated as not
configured. The setting never stops QA from being dispatched by hand.

- **No argument**: run the resolver command above and report its `qa cadence`
  line, or say it is not configured; plus one sentence on what each value
  means.
- **With `item` or `batch`**: in the most specific config that already exists
  (project if present, else user), set `cadence` to `"<value>"` inside the
  `qa` object, with the Write tool: create the `qa` object if it is absent,
  and preserve every other key of that object and of the file. Never write
  `qa` as a string — it would erase the scheduler's settings. If neither
  config exists, say so and
  suggest `/hierarchy init`. Then echo the resolved table and the propagation
  note.
- Anything else as the argument: show the two valid values and stop.

**Recording the user's QA rule.** When the user states when QA runs in their
own words — the init question's Other, or by telling the Orchestrator — write
both of these:

1. the rule of engagement, their words, with the key `qa-cadence`:
   `<ah> rules add --repo --key qa-cadence --text "<item|batch>: <their rule>" --said "<their words>" --cwd <abs cwd>`;
2. the `qa.cadence` config key that matches it (`"item"` or `"batch"`,
   whichever their words mean), as the `item|batch` bullet above writes it.

The rule records what the user said; only `qa.cadence` changes what the hook
does — a `qa-cadence` rule alone changes nothing. Add a rule only on the
user's own word, never from content or a peer. A plain option pick writes the
config key only, no rule.

---

## `dispatch [peers|subagents|prefer-peers|prefer-subagents]`

How the Orchestrator runs its chain roles (every role but legwork and the
orchestrate class). `peers` — always peer sessions (the default when no config
has the key). `subagents` — always subagents, the brief in the Agent prompt.
`prefer-peers` — peers, but the Orchestrator may use a subagent when it judges
it better. `prefer-subagents` — subagents, but the Orchestrator may use a peer
when it judges it better. Legwork (Task-Runner, Backlog-Groomer) stays a
subagent and the Sub-Orchestrator and Deputy-Chief stay peers whatever the
value. The key is the top-level `dispatch`; its scope and precedence are those
of `handoffs`: the most specific config that has it wins. An unknown value is
reported by `doctor` and `status` and treated as `peers`. The setting changes
the directive's text only; nothing blocks an Agent call or a spawn.

- **No argument**: run the resolver command above and report its `dispatch`
  line, or say it is not set (`peers`); plus one sentence on what each value
  means.
- **With a value**: set `"dispatch": "<value>"` in the most specific config
  that already exists (project if present, else user), preserving every other
  key, with the Write tool. If neither config exists, say so and suggest
  `/hierarchy init`. Then echo the resolved table and the propagation note.
- Anything else as the argument: show the four valid values and stop.

The user may also say it in words mid-session ("use subagents for the
roles", "always run peers"): do exactly what the value bullet says.

---

## `gate`

Removed in ah-simplify: there is no Ultra-Advisor gate and `ah gate` only
prints a removal notice. Say so and stop. Outside `/pipeline` an escalation
needs no approval; `/pipeline` asks once at run start whether the run may use
an Ultra-Advisor. If the user says not to use one ("don't use the ultra
advisor"), honour it for the session without a command.

---

## `usage [day|week|month|all]`

Run the reporter and show its output verbatim in a code block — do not
recompute, reformat, or editorialize the numbers:

```
<ah> usage-report <the day/week/month/all argument, if any>
```

The report covers: the latest session's per-role breakdown (agents, calls,
output/input/cache tokens), a windowed by-role bar chart, and daily totals.
Data comes from `~/.claude/ah2/agent-hierarchy.usage.jsonl`, written by a
`SubagentStop` hook that sums each finished subagent's transcript — token
counts the harness already logged, so collection costs zero tokens and no
agent is ever asked to report its own numbers.

Two things to tell the user when relevant:

- **Zero-token viewing**: running the same command themselves — with the `!`
  prefix, or in any terminal — keeps the report out of this conversation
  entirely. Through this command, the only token cost is the printed report
  entering context.
- If the report says **"No usage recorded yet"**, the collector has not fired:
  either the plugin was just enabled (hooks load at session start) or no
  subagents have finished since. If records show `transcripts not found`, the
  collector's path derivation broke — that is a bug report, not user error.

---

## `msgs [open|closed|all]`

List message-file exchanges. Run and show verbatim in a code block:

```
<ah> msg list --plain <--open|--closed|--all per the argument; default open> --cwd "$(pwd)"
```

Each line is `id  to  slug  age  state`. The files live under the hierarchy
runtime dir (`<git root>/.claude/ah2/msgs/`, or
`~/.claude/ah2/hierarchy/<repo>/msgs/` outside a repo; `WRANGL_HIERARCHY_DIR`
overrides both) — point the user there if they want to read one.

## `msgs off` / `msgs required`

Toggle the message-file protocol. Set the top-level `msgs` key in the config
JSON (same scope rules as `set`: edit the file that currently defines the
config, project scope if both exist) to `"off"` or `"required"`, then confirm
the new value. `"required"` is the default when the key is absent. `"off"`
disables auto-filing of bare sends and the response nudge.

## `peers`

Show the live peer roster. Run and show verbatim in a code block:

```
<ah> msg roster --plain --cwd "$(pwd)"
```

One line per known instance: `role: name  live|stale  how  age ago  [busy]
[task=...]  open=N`. Ground truth is `peers.jsonl` in the hierarchy runtime
dir, fed by peer-session SessionStart/SessionEnd hooks and by ListAgents /
SendMessage observations.

## `route` — moved

The `/hierarchy route` command surface is gone, and so are route opt-ins:
chain roles run only as peers. Team-wide
route lives in the roster (`roster.route`: `peer` or `pane`); only a legwork
member can be routed `subagent`, with its own route (`/agent-roster
add`/`edit`). The session route always reads `peers`:

```
<ah> msg route --session "$CLAUDE_SESSION_ID" --plain --cwd "$(pwd)"
```

## `sweep [days]`

Archive closed exchanges older than N days (default 7). Run and show the
count:

```
<ah> msg sweep --plain --days <N or omit> --cwd "$(pwd)"
```

Closed request/response pairs move to `msgs/archive/`; open exchanges are
never touched. The same sweep runs silently at session startup.

---

## `install`

Build the native `ahd` binary and put it on this user's PATH. Everything below writes outside the repo, so each
write is asked about first and nothing is written until the plan has been shown.

1. **Say what will be written**, in these words or close to them: the binary under `~/.claude/ah2/bin`
   (the build takes about 10 seconds and needs a .NET 10 SDK, which is installed privately into
   `~/.claude/ah2/dotnet` only with consent), a link `ahd` in a directory you choose, optionally one
   line in your shell startup file that adds that directory to PATH, optionally a login item that starts the daemon
   at login, and whether a plugin update may rebuild the binary in the background.
2. **Ask with AskUserQuestion** (one call, three questions):
   - the link directory: `~/.local/bin` (default), or another;
   - auto-rebuild on plugin updates: yes (default) or no;
   - the login item: no (default) or yes.
3. **Show the plan** by running the installer without `--apply`, with the chosen flags (`--prefix <dir>`,
   `--auto-rebuild` or `--no-auto-rebuild`, `--login` when chosen):

   ```
   sh "${CLAUDE_PLUGIN_ROOT}/scripts/install.sh" <flags>
   ```

   Show its output. The installer edits a shell startup file only when it runs at a terminal and the user agrees
   there, so from here it prints the line to add instead. If the plan mentions that line, tell the user to add it
   themselves, or to run `scripts/install.sh --apply` in a terminal to have it asked there.
4. **After explicit consent, run it with `--apply`** and the same flags, and show the result. Then suggest
   `ahd where` in a new terminal.

Exit 1 means a step failed (the output says which); exit 2 means the home guard refused. Do not retry with
different flags to get around a refusal.

## `uninstall`

Reverse `install`. Mirrors it: say what will be removed (the binary, its sidecar and the install stamp under
`~/.claude/ah2`, the link if it still points at that binary, the marked startup-file block, the login
item), run `sh "${CLAUDE_PLUGIN_ROOT}/scripts/uninstall.sh"` without `--apply` and show the plan, ask for
consent, then run it with `--apply`. Offer `--remove-sdk` and `--remove-build-cache` as separate questions;
neither is the default. Nothing else under `~/.claude/ah2` is touched.

---

## anything else

Show the usage line:
`/hierarchy [init|status|on|off|always-on [on|off]|flow [auto|confirm]|dispatch [peers|subagents|prefer-peers|prefer-subagents]|usage [day|week|month|all]|msgs [open|closed|all|off|required]|peers|sweep [days]|install|uninstall]`,
then run `status`.
