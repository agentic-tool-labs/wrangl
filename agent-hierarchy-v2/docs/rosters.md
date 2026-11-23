# Rosters

A **roster** is your template: which roles you want and which model each one runs. A **team** is the live set of
sessions built from it. This page is for anyone choosing or changing who is on their team. After reading it you can add
and change members, keep several rosters for different kinds of work, and start more than one team.
[Getting started](getting-started.md) shows the quick path.

## Add and change members

Inside Claude Code:

```
/wrangl:agent-roster add --role reviewer --model opus
/wrangl:agent-roster edit --member <name> --model sonnet
/wrangl:agent-roster remove --member <name>
```

`add` adds one member for a role, `edit` changes a member, and `remove` takes one off. Changing the roster starts and
stops nothing: a running team keeps the roster it was built from. While your session owns a live team, a roster
edit is refused unless you add `--allow-roster-edit` yourself. To see the roster as it resolves for this repository,
run `ah roster show`.

## What a member can set

`/wrangl:agent-roster init` sets the roster's route (peer or subagent) and layout,
then `/wrangl:agent-roster add` adds a member per role. Each member has these keys:

- `kind` — which agent CLI Herdr starts for this member. Omitted means
  `claude`, and that stays true for every roster written before this key
  existed. Any other value (`codex`, `pi`, … — Herdr owns the list; run
  `herdr agent` to see what your install has) makes the member a *non-Claude*
  agent, which changes the keys below.
- `model` — which model that role runs on. For a non-Claude kind with a model
  mapping (today: codex) it is that harness's model name, e.g.
  `{ "role": "architect", "kind": "codex", "model": "gpt-6-astra", "route": "pane" }`;
  any other non-Claude kind takes its model flag in `args`.
- `effort` — reasoning effort, where the model supports it. **`kind: claude` only.**
- `route` — `peer` (a separate live session, reached with SendMessage),
  `subagent` (spawned in-process by the Agent tool), or `pane` (driven through
  its Herdr pane). A non-claude kind **must** be `pane`.
- `auto_mode` — the spawned session's permission mode. `auto`
  (`--permission-mode auto`) is the hands-off mode. `bypassPermissions` is
  still accepted, but that session can get stuck at a startup confirmation
  screen instead of coming up ready — prefer `auto`. For `kind: codex` the
  mode is translated into codex's own vocabulary — a `--sandbox` policy plus
  an `--ask-for-approval` policy, or `--dangerously-bypass-approvals-and-sandbox`
  for `bypassPermissions` — and emitted after `--` ahead of `args`, so an
  explicit native flag in `args` overrides it.
- `args` — native CLI arguments passed verbatim to the agent. **Non-claude
  kinds only** — for a Claude member, `model`/`effort`/`auto_mode` are the
  validated way to set flags, and `args` would bypass that validation.

`effort` is literally a Claude Code CLI flag (`--effort`), which is why it is
*rejected* rather than ignored for another kind, as `model` is for a kind with
no model mapping: a setting that silently affects nothing is worse than one
that refuses.
`auto_mode` is the exception — permission policy exists in the other CLI's
vocabulary too, so it is translated for a kind that has a mapping (today:
codex) and still rejected for one that does not.

A non-claude member requires a Herdr session (`HERDR_ENV=1`) to spawn — tmux
and terminal transports can only start `claude`. It is briefed with
`ah roster deliver`, never SendMessage, and reports only through its response
file. Adding one from a non-Herdr
session still works (rosters are portable); only spawning it needs Herdr.

Your own roles, and members that run in Codex, are covered in
[custom-roles.md](custom-roles.md).

Value spaces and validation rules are in
[SKILL.md — Levels](../skills/agent-roster/SKILL.md#levels) and
[SKILL.md — `add` / `edit` / `remove`](../skills/agent-roster/SKILL.md#add--edit--remove).

## Named rosters

A config level can hold more than one roster: named rosters are
`rosters.<name>` blocks beside the default `roster` block — a `game-dev` team
and a `docs` team in one repo, say. `/wrangl:agent-roster list` shows them, `copy`
makes one from another, and `delete` removes one no team uses.

Commands that read or edit a roster (`show`, `init`, `add`, `edit`, `remove`,
and `create` when it builds a team) use the first of:

1. `--roster <name>` on the command;
2. the `WRANGL_ROSTER` environment variable, when set and not empty;
3. `activeRoster`, a config key: the repo-user level wins over repo, and repo
   over global;
4. the default `roster` block.

`default` names the default block in each of these, so a narrower level or a
single session can undo a wider choice. `WRANGL_ROSTER=default` gives one session
the default block in a repo whose `activeRoster` is `game-dev`.

- **One repo, just you:** `/wrangl:agent-roster use game-dev` writes `activeRoster`
  to your own file for the repo (repo-user).
- **One repo, everyone:** `use game-dev --level repo`, in the committed file.
- **This machine:** `use game-dev --level global`.
- **One session:** start it with `WRANGL_ROSTER=game-dev`.

A running team keeps the roster it was built from; changing the selection
affects only teams created later.

Different rosters for different workloads is the use: a lean one for routine
fixes, a fuller one for a feature.

```
/wrangl:agent-roster init repo-user --route peer --roster docs      # an empty `docs` roster
/wrangl:agent-roster add repo-user --roster docs --role implementor # ...then fill it
/wrangl:agent-roster copy default game-dev                          # or start from the default block
/wrangl:agent-roster use game-dev                                   # build the next team from it
/wrangl:agent-roster list                                           # what exists, where, and which is selected
```

A name is 1–32 letters, digits or `-`, starting with a letter or digit;
`default` is reserved for the unnamed block, so it can't be created, copied
to or deleted. `delete` refuses a roster a live team was built from: disband
the team first. `--team` names a live team, never a roster; to build a team
from `rosters.docs`, pass `--roster docs` to `create`.

### Rules for a roster

A [rule of engagement](cli-tools.md#ah-rules-rules-of-engagement) can be tied to a roster name:
`ah rules add --roster game-dev --key <key> --text "<rule>"`. Every team built from a roster
called `game-dev` gets it, in any repo, and the roster does not have to exist yet. `--roster default`
reaches teams built from the default block. A repo rule of the same key wins over it.

### Parameters for a roster

A role in a roster can declare values its members need, such as an engine-profile path. The plan from `create` lists them, and `ah roster create --param <name>=<value>` sets them. See
[Role parameters](custom-roles.md#role-parameters).

If the selection names a roster no level defines, commands exit 2 with the
roster's source and the fixes; hooks fall back to the default block and warn;
`doctor` shows a red `roster-selection` row. See
[troubleshooting.md](troubleshooting.md#named-rosters). Roles other people
publish come separately, as [role packs](custom-roles.md#6-role-packs).


## Share a roster

A roster kit is one JSON file (1 MiB at most) that packs a named roster so
another machine can import it. Export it, send the file, and the other person
reads a dry run before anything is written.

**What a kit carries:** the roster block (`route`, `members`, `max_lanes`,
`orchestrator_model`); the row of each custom role a member uses, with the text
of its agent file; a reference (`from` and `pin`) for each pack role; the row of
a built-in role only if you changed it. Per-team lane limits are not part of a
roster and are not carried.

**What a kit never carries:** param values, roster rules in this version, pack
agent text, permission rules, hooks, settings or setup commands.

```
ah roster kit export game-dev --out game-dev.kit.json
ah roster kit import game-dev.kit.json --dry-run
ah roster kit import game-dev.kit.json --pin sha256:<the pin the dry run printed>
```

The export refuses to replace an existing file unless you add `--force`, and it
prints the same listing a dry run would.

**The dry run writes nothing.** It lists each file and key an import would
write, marked `new`, `identical` or `conflict`. It prints every agent file in
full, with findings and flagged tools, every pack reference with the command to
adopt it, and the kit's pin last. Read the agent text: it becomes a file a
session will run as.

**An import writes nothing unless the dry run is clean and the pin matches.**
Any finding or conflict refuses the whole import, and nothing is written. Fix
the kit or the clash, run the dry run again, and use the new pin.

- **Pack roles come first.** An import never adopts a pack. If a member uses a
  pack role you have not adopted, the import is refused and the dry run prints
  `ah roster role set <role> --from <plugin>@<marketplace>:<role> --dry-run`.
  Run that, adopt the pack, then import.
- **Conflicts.** A roster or role name that exists with a different row, or an
  agent file that exists with different text, is a conflict. `--as <name>`
  renames the imported roster. Roles are not renamed: remove or rename the
  local one, or edit the kit. Importing the same kit twice is safe: the second
  time everything reads `identical` and nothing is written.
- **Agent files** are checked by the same rules as plugin agents. A file with
  `hooks`, MCP server settings or `permissionMode`, or with hidden characters, is
  refused. A file with no `tools:` list is not refused but **flagged**: the dry
  run prints `inherits every tool, MCP servers included` for it, plus
  `(except: …)` when it has `disallowedTools`, in a `flags` block above the
  writes, and the commit prints the flags again. Committing with `--pin` is the
  approval. Roles made with `--scaffold` write no `tools:` list, so they import
  flagged. Such an agent can use every tool and MCP server the importing machine
  has, so read the preview of a kit you did not make. (A pack agent with no
  `tools:` list is still refused at adopt and trust.)
- **Members** with `command`, `args`, `kind`, `harness`, `adapter` or `autoMode`
  are refused, and so is any key a kit does not list.
- **Where it writes.** Config rows go to the `--level` file (default `global`);
  `--level repo` writes the committed repo file and the dry run says so. Agent
  files go to `~/.claude/agents` (`--agents user`, the default) or
  `<repo>/.claude/agents` (`--agents repo`). An agent file is never replaced. If
  the config write fails, the agent files this import created are removed again.

**It never writes** a permission rule, hook or setting, a param value, an
`activeRoster` choice (run `ah roster roster use <name>` yourself), a pack
adoption, or a team. It runs nothing.

**Who can commit.** A team member session, or any session while a pipeline run
is live in the checkout, cannot commit an import; run it from your own
top-level session. The dry run works anywhere. The pin only proves the file did
not change after you read the dry run: nothing else asks you to confirm, so
read the listing before you pass it. See
[`ah roster kit` in the CLI reference](cli-tools.md#export-a-roster-kit).

## Starting a team, or several

`/wrangl:agent-roster create` starts the resolved roster as a live **team**: the roster is the definition, the team
is the running sessions built from it (see the [Glossary](glossary.md)). Asking the Orchestrator to "set up my team"
does the same. Layout confirmation
is asked every time, `auto` or `manual` alike — `auto` only skips the
per-member placement prompt; `manual` walks you through each member's
placement individually.

**Several teams.** One request can build more than one team: "create team foo
from roster foo-named-roster and create team bar from roster bar-named-roster".
The Orchestrator asks only what the request doesn't already say. Afterwards,
name the team when you ask for something ("disband foo"); a member's name
already says its team ("dismiss bar-reviewer").

Phrasings it reads without asking:

- "create team foo from roster x and team bar from roster y": foo from x, bar
  from y.
- "a team for each of x and y": each team takes its roster's name.
- "create teams foo and bar, both from x": both use x.
- "create team docs": the selected roster, as for one team.

What it does ask, once for everyone: a name you didn't give, a roster that
can't be worked out, a member with no model, each question naming its team.
It plans every team, asks, then launches, checks in and commits team by team;
one team failing doesn't undo the others, and it asks once whether to retry.
You end up owning every team it built.

Owning several teams changes how you address them. A command that names a
member (`dismiss bar-reviewer`, `move foo-architect …`) finds the team from
the name. A command about a whole team (`disband`, `untrack --all`, the
stream commands) needs `--team foo`; without it the command refuses and lists
the teams you own, and in conversation the Orchestrator asks which you mean.
Messages follow the same rule: `ah msg new` takes the team from `--to-name`,
and `ah msg list` shows every owned team with a `team` on each row. The
session's start-up note says which teams you own; the legacy default team, if
you have one, is `@default` (`--team @default`). A team is yours by the pid of
the session that created it. The same session, restarted with `claude --resume
<session id>`, keeps its teams; a different session owns none of them until it
re-claims each with `ah roster adopt --orchestrator-pid <pid> --team
<name>` ([recovery.md](recovery.md)). Details:
[cli-tools.md](cli-tools.md); when it goes wrong,
[troubleshooting.md](troubleshooting.md#several-owned-teams).

