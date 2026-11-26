---
name: agent-roster
description: Defines, edits, or inspects the agent-hierarchy ROSTER — the template of which roles exist and their model/effort/route/kind. Use for /agent-roster, for "add a reviewer peer", "add a peer to the roster", "change the architect's model", "remove a role from the roster", "what's in my roster", "set up a roster", or listing, copying, deleting or switching named rosters. Standing up, reshaping, or tearing down a LIVE Team is the agent-team skill, not this one.
argument-hint: "[show|init|add|edit|remove|list|copy|delete|use|pack]"
---

# agent-roster

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

The roster is a `roster` block in the existing `agent-hierarchy.json` config,
at one of three levels (§ Levels below). `<ah> roster` does all the file I/O and
validation; this skill is the interactive prose surface that drives it — do
not hand-edit the JSON, and do not duplicate its validation here.

**Roster vs. Team.** The roster is the template; a Team is a live instance
created FROM it. Editing the roster never touches a running Team, and
`<ah> roster` refuses a roster edit while this session owns one. Everything that
touches a LIVE Team (`create`, `spawn-one`, `spawn-ad-hoc`, `dismiss`,
`disband`, `adopt`, `move`, `resync`, `reap`, `teams`, `history`, `checkin`)
lives in `wrangl:agent-team`, and `/agent-roster <those>` behaves identically.
Reach for `add`/`edit`/`remove` only when the roster itself is wrong or missing.

## Levels

| Level | Path | Wins when |
|---|---|---|
| `repo-user` | `~/.claude/ah2/projects/<slug>/agent-hierarchy.json` | always, if present and non-empty |
| `repo` | `<repo-root>/.claude/ah2/agent-hierarchy.json` | no repo-user roster |
| `global` | `~/.claude/ah2/agent-hierarchy.json` | no repo-user or repo roster |

Resolution is **whole-level replace**, not a per-key merge: the winning
level's `roster` block is used in its entirety. To inspect the roster run `<ah> roster show --cwd <abs cwd>`; never read `.claude/ah2/agent-hierarchy.json` directly — it misses the worktree/main-checkout and global fallback resolution that `show` implements. With no
`--level` it prints the resolved roster; with one, that level's raw file, and says if it is shadowed.

Member names are **derived, never stored**: the first member of a role is
`<team-name>-<role>` (e.g. `claudetools-architect`), the team name being the
live Team's (the repo basename by default); a second, third, ... same-role
member gets `-2`, `-3` in array order, and removing an earlier member
re-ordinals the later ones. Under Herdr the whole name must be
`[a-z][a-z0-9_-]`, at most 32 characters, ordinal included; `create` refuses a
name that cannot work, with a suggested one for the user to confirm.

## Command surface

Every verb runs as `<ah> roster <verb> …` with `--cwd <abs cwd>`
through the Bash tool, `${CLAUDE_PLUGIN_ROOT}` taken from this session's
`ah CLI root` line (authoritative; the newest wins). `docs/cli-tools.md` has
the full verb and flag surface; the bullets below are notes on top of it. Level
may be `--level <L>` or the first bare word (`<ah> roster add repo --role
architect` ≡ `--level repo`). Output is always JSON.

A repo can keep more than one template: named rosters are `rosters.<name>`
blocks beside the default `roster` block. `show`, `init`, `add`, `edit` and
`remove` work on the **selection**: `--roster <name>`, else `WRANGL_ROSTER`, else
the most specific `activeRoster` (set with `roster use`), else the default
block; `default` names the default block everywhere. When the user names no
roster, work on the selection without asking: say in one line which roster it
is and where it comes from (the `selection` in `show`'s output), and that
`/agent-roster use <name>` or `--roster <name>` switches it. A selection naming
a roster no level defines makes every verb but `init` exit 2 with the fixes —
relay them. A Team's name is not a roster selector: these verbs refuse the team
flag and point at `--roster`.

- `show [--level global|repo|repo-user] [--roster <r>]` — resolved roster, or one level's raw file.
- `init --level <L> --route peer [--roster <r>] [--pack <plugin>@<marketplace>]…` — replaces that level's block wholesale. `--route subagent` is refused: only legwork roles run as subagents. `--pack` (repeatable, or a comma list) names the role packs whose roles every team built from this roster gets; a pack not installed here is written anyway, with a note. Change them later with `roster pack` (`reference/named-rosters.md`).
- `add --role <R> [--level L] [--roster <r>] [--model M] [--effort E] [--route peer|subagent|pane] [--kind K] [--args '<json>'] [--auto-mode A]` — writes the template row and spawns nothing; `--route subagent` only for a legwork role.
- `edit --member <NAME> [--level L] [--roster <r>] [--role R] [--model M] [--effort E] [--route ...] [--auto-mode A]` — `--model ""` and `--effort ""` clear the field, leaving the model to be chosen at each spawn.
- `edit --orchestrator-model M [--level L] [--roster <r>]` — sets the roster block's `orchestrator_model`, the model `ahd new` starts an orchestrator with (`opus`, `sonnet`, `fable` or `inherit`); `--orchestrator-model ""` removes it. Not combinable with `--member`.
- `remove --member <NAME> [--level L] [--roster <r>]` — edits the roster **template**, not a live Team; `/agent-team`'s `dismiss` is the live-Team equivalent.

`add`/`edit`/`remove` with no `--level` operate on whichever level currently
resolves (repo-user > repo > global) and print which level they picked — say
that back to the user in one line. `edit` and `remove` with no roster resolving
at any level fail and name `init`; run `init` first (asking the user per
§ `init` below).

## `/agent-roster` bare, or `show`

Run `show` and print its output, naming the selected roster and its source.
`roster: null` (nothing configured at any level) → say so and offer `init`.

## Named rosters: `list`, `copy`, `delete`, `use`

These are `<ah> roster roster list|copy|delete|use|pack …`. Read
`reference/named-rosters.md` before running one. `delete` asks the user to
confirm first.

## `init`

1. **Level.** If not given, ask via AskUserQuestion: `global` (all repos),
   `repo` (this repo, committable), `repo-user` (this repo, this machine
   only — not committed). One line each on what the level means.
2. **Destructive check.** If that level already has a roster (`show --level
   <L>` returns members), confirm before replacing — `init` always replaces
   the whole level's block, never merges into it.
3. Run `<ah> roster init --level <L> --route peer`. Chain roles run only as
   peers; only a legwork member (task-runner, or a custom legwork role) can be
   routed `subagent`, with its own `add --route subagent`.
4. **Pick the roles.** Ask AskUserQuestion with `multiSelect: true`:
   "Which roles should this roster include?", one option per role with a
   one-line description. A question holds at most 4 options, so split the
   roles over as many questions as needed (up to 4 questions per call):
   `architect` (design authority — specs, never implements), `implementor`
   (builds exactly what the spec says), `docs-writer` (writes standalone
   docs — READMEs, guides, changelogs; never edits source), `ux-engineer`
   (web UI work — CSS, markup, tokens, layout, accessibility; proves it with
   screenshots in both themes), `reviewer`
   (validates an Implementor's diff against the spec), `task-runner` (cheap runner for
   tests/builds/log-sifting/search), `ultra-advisor` (deepest-reasoning
   escalation for hard or high-stakes calls). Run `<ah> roster role list` first:
   each custom role it shows with status `ok` or `n warnings` is offered too,
   described by its class and placement (`alt. to <Builtin>` or `side`); an
   `UNAVAILABLE` role is left out and named in one line. For each role picked,
   ask (batched into calls of up to 4 questions) its model, effort and
   auto-mode. The model question's first option is `Undefined —
   choose at each spawn`, followed by models valid for the role's class, with
   the rest via Other; no model option is marked `(Recommended)` and none is
   preselected, except for a legwork role (task-runner or a custom legwork role):
   `reference/legwork.md`, and for `docs-writer` and `ux-engineer`, which preselect
   `sonnet (shipped default)` and `opus (shipped default)` and are not asked again at spawn. The effort question likewise starts with `Undefined`. The
   auto-mode options are exactly `auto (Recommended)`, `acceptEdits`, `plan`,
   and `default (none)` — `bypassPermissions` is never offered, and is
   accepted only if the user types it into Other. For each picked role, run
   `<ah> roster add --level <L> --role <role> [--model ...] [--effort ...]
   [--auto-mode ...]`, passing `--model` and `--effort` only for values the
   user picked: an undefined model is asked for when the member is spawned.
   Several instances of a role are a follow-up ask, not part of the multiselect.
5. Run `show` and echo the result.

`init` only ever writes the one level's file you asked for.

## `add` / `edit` / `remove`

Prompt for any field the user didn't already state (role for `add`; the
target member's derived name for `edit`/`remove`), then call the CLI and report
its exact result, including the level it defaulted to. For a legwork role with
no model stated, ask it per `reference/legwork.md` first.

`add` auto-creates a minimal roster when none exists — repo level
when inside a git repo, or the explicit `--level` — and says so with the
file's path; `init` is not a prerequisite. The one exception is `--roster <X>`:
a named roster's container is created only by `init --roster X`.

**`add` writes the roster and stops.** It launches nothing, whatever the
member's route or kind. Report what was written and, from its `next_step`,
which `/agent-team` command starts the member: `spawn-one <role>` for a
roster-conforming member, `spawn-ad-hoc` for a divergent or ad hoc one.

Do not reach for `add` when what the user wants is a member of the *running*
team: if you own a live Team the CLI refuses the edit outright and names
`spawn-ad-hoc`. That refusal is not an obstacle to route around: the
`--allow-roster-edit` override is the **user's**, never one an agent adds to
get its call through.

`--on-missing auto` (peer-routed members only; the default and the only value,
`prompt` and `never` are refused) means a role with no live peer is started
with `spawn-one` — **spawn without asking**.

`--kind` or `--args` on `add`/`edit` (non-Claude members): read
`reference/kinds.md`.

## Question tag

Every `AskUserQuestion` in this file carries `metadata.source` `ah:bootstrap:agent-roster:configure`; the hook refuses an untagged ask from the Orchestrator.
