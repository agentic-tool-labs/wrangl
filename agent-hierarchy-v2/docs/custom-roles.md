# Custom roles, and members in other harnesses

Define your own role, put it on the roster, and — if you want — run a member
on Codex instead of Claude. Assumes you already know the roster, a Team and
peers ([getting-started.md](./getting-started.md)).

Every command is `<AH_ROOT>/bin/ah roster <verb> … --cwd <abs cwd>`
(`R` below); the full flag list is in [cli-tools.md](./cli-tools.md).
`/wrangl:agent-role` drives the `role` verbs for you, so you rarely type them.

## 1. What a custom role is

A `roles.<name>` row in `agent-hierarchy.json` with two things that matter: a
**class** and an **agent file**. `ah roster role` does every read, check and
write — don't hand-edit the JSON.

The class says which step of the chain the role may stand in for, and what its
agent file must look like. The contract is checked when you define the role:

| Class | Stands in for | Tool contract (errors) | Conventions (warnings) |
|---|---|---|---|
| `advise` | Escalate (Ultra-Advisor); every dispatch needs your approval | Read, SendMessage; an explicit `fable` or `opus` model | no Edit, NotebookEdit, advisor |
| `design` | Design (Architect); the tier rule applies | Read, SendMessage, Write | no Bash, NotebookEdit, advisor |
| `review` | Review (Reviewer) | Read, SendMessage; must **not** have Edit, Write, NotebookEdit | has Bash; no advisor |
| `implement` | Implement (Implementor) | Read, SendMessage, Edit or Write, Write or Bash | has Bash; no advisor |
| `legwork` | nothing — errands any role can dispatch | none | none |

Errors refuse the write; warnings print and the write goes ahead.

A bare `agent` name resolves to `<repo>/.claude/agents/<name>.md`, then
`~/.claude/agents/<name>.md`; the first that exists wins.

## 2. Where it sits in the chain

Two placements, for the chain classes (`advise`, `design`, `review`,
`implement`):

- **Alternative** — the row has `routes`, a sentence saying what work it takes.
  It is an in-chain alternative to the built-in for that step, for that work
  only. Example: a Tech-Writer with `routes: "standalone docs: READMEs, docs/,
  guides, changelogs"` takes the docs while the Implementor keeps the code.
  Both are live at once. The Docs-Writer (`docs-writer`) and the UX-Engineer
  (`ux-engineer`, for web UI work; see [ux-engineer.md](./ux-engineer.md)) ship
  built in as exactly this kind of alternative to the Implementor, so you do not
  define them yourself; the examples below use a role of your own.
- **Side role** — no `routes`. Dispatched only when you ask for it or its
  description fits.

`routes` on a `legwork` role warns and is ignored.

**Who runs as what.** A custom role runs as a subagent only if it is
`legwork`-class (`--dispatch model`, or a roster `route: subagent`). Every other
custom role runs as a peer session. A built-in can be marked subagent-only in the built-in
role table (`SubagentOnly` in `native/src/Ah.Config/Core/ConfigRegistry.cs`) to
run only as a subagent with no peer session and no links; no built-in is marked
that way now (QA is a peer role).

## 3. Defining one

Ask for it, and answer the questions:

```
/wrangl:agent-role add
```

It suggests a name, the class, placement (and a drafted `routes`), the agent
file, the model, a one-line description and the level, then runs a dry run,
then the write.

**The name.** Lowercase letters, digits and `-`, starting with a letter, at
most 31 characters (`/^[a-z][a-z0-9-]{0,30}$/`). It can't end in `-` or
`-<digits>` (that suffix is a peer's instance number), and it can't be a
built-in role's name or `orchestrator`, `advisor` or `other`. It also becomes
part of the peer name (`<team-prefix>-<name>`), which Herdr caps at 32
characters in total (`[a-z][a-z0-9_-]{0,31}`) — keep it short, especially for
a global role, since every repo has its own prefix.

The command underneath:

```
R role set tech-writer --class implement \
  --routes "standalone docs: READMEs, docs/, guides, changelogs" \
  --scaffold user --dry-run
```

| Flag | Meaning |
|---|---|
| `--class advise\|design\|review\|implement\|legwork` | the class (see [What a custom role is](#1-what-a-custom-role-is)) |
| `--agent <ref>` | the agent file: a bare name, or `plugin:agent` |
| `--routes "<sentence>"` | makes it an alternative; `--routes ""` demotes it to a side role |
| `--description`, `--label`, `--model` | as named; `--description ""` deletes the key |
| `--dispatch model` | legwork only (exit 2 otherwise) |
| `--params a,b` | the [parameters](#role-parameters) a member needs; `--params ""` deletes the key |
| `--level global\|repo\|repo-user` | where the row lives; default is where the role is already defined, else `repo` with `--scaffold repo`, else `global` |
| `--scaffold user\|repo` | write a template agent file that passes the contract: `~/.claude/agents/<agent>.md` or `<repo>/.claude/agents/<agent>.md`. The file has no `tools:` list, so in a [roster kit](rosters.md#share-a-roster) the role imports flagged (`inherits every tool, MCP servers included`) and the pin approves it. Such an agent can use every tool and MCP server the importing machine has; add a `tools:` list to restrict it, and read the preview of a kit you did not make |
| `--dry-run` | write nothing; print the findings |

Drop `--dry-run` to commit. If the agent file is repo-level, define the role at
`repo` too — a global row pointing at it is unavailable in every other repo.

**The fix loop.** Each error finding prints its fix. In `/wrangl:agent-role` you
choose: let it apply a frontmatter fix for you (only to your own bare-name
file, never a plugin's), change the role's class to the one it suggests, or
fix it yourself. To restrict a tool, remove it from `tools` or add it to
`disallowedTools` — never a scoped entry like `Bash(git:*)`, which grants the
whole tool. Then it dry-runs again until there are no errors.

**The Specialisation section.** A scaffolded file is a short identity
paragraph, then `## Specialisation` with one placeholder line. Replace that
line with what makes this role yours; the hierarchy contract arrives by
injection, so the file never restates it. `/wrangl:agent-role` asks you for the
text and edits only that line.

Check and inspect:

```
R role list            # class, agent, model, placement, contract status
R role list --json     # adds each role's findings
R role remove <name>   # refused while a roster member uses it; never deletes a file
```

A role that fails its contract shows `UNAVAILABLE: n errors`.

### Self-care shell for a role without one

A role whose agent file denies `Bash` cannot run any command, so it cannot run `checkin` or write
its own response with `ah msg`. To give it the self-care commands only (see "Self-care (any peer)"
in `cli-tools.md`):

1. remove `Bash` from the agent file's `disallowedTools`;
2. mark the registry row: `R role set <name> --shell self-care` (or `"shell": "self-care"` in the row).

The marker is the exact string `self-care`; `role set` rejects any other value, and rejects the flag
on a built-in. A role with the marker whose agent file still denies `Bash` simply has no shell;
nothing is gated. The built-in Architect is gated by default.

### Role parameters

A role can say "a member of me needs the value `engine-profile` for this repo". The role names it; you set the value once.

```
R role set game-dev --params engine-profile,asset-root
R param set engine-profile /work/engine/profile.json
```

Names are 1 to 8 per role, lowercase letters, digits and `-`. A built-in role can declare them too. Values are kept in the `params` object of the config files and the most specific level wins: repo-user, then repo, then global. `param set` writes repo-user in a repo unless you pass `--level`. When you create a team, the plan lists each parameter with its value or `NOT SET`, and `create --param <name>=<value>` sets it in the same call.

A member sees its parameters at the start of its session (also after a compaction) and in every request addressed to its role. A missing one reads `NOT SET`, and the member is told to say so in its report, not to guess. Nothing is ever refused or delayed for a missing value, and ah never opens or checks the value as a path. A role run as a subagent gets no session start, so it sees them only in a request. A member in a custom harness started with `spawn-ad-hoc` gets no warning at spawn, but its requests still carry the block. Commands: [`roster param`](cli-tools.md#set-a-role-parameter).

### A custom `docs-writer` from before the role shipped

If your config still holds a custom role named `docs-writer`, the built-in
replaces it. `ah roster role list` and `ah roster doctor` print one notice that
names the config file the old row is in and your agent file. Nothing is deleted
and your agent file is no longer used.

To keep your own, define it under another name with `/wrangl:agent-role`, pointing
at the same agent file and routes:

```
R role set my-docs-writer --class implement --agent docs-writer \
  --routes "standalone docs: READMEs, docs/, guides, changelogs"
```

To clear the notice, remove the old row. This removes the old custom keys
(`class`, `label`, `description`, `routes`, `agent`, `from`, `pin`) from the
`docs-writer` entry at that level, keeps a `model`, `dispatch` or `peer` on it,
deletes the entry if nothing is left, and touches no other level:

```
R role remove docs-writer --level <global|repo|repo-user>
```

## 4. Using it

Add it to the roster, then start it:

```
R add --role tech-writer --model opus
R spawn-one tech-writer          # a roster-conforming member
R spawn-ad-hoc tech-writer       # a member the roster doesn't describe
R create --spawn                 # or the whole team
```

The spawn verbs also take `--dry-run`, which prints the launch line and starts
nothing. `add` only writes the template; it launches nothing. A live Team is not
changed by a roster edit — use `spawn-one` or `spawn-ad-hoc` to add a member.
`/wrangl:agent-team` and `/wrangl:agent-roster` drive these.

**Models.** A member's model is stored only when you set one. Add it without
`--model` and you are asked at spawn; answer with `--model <M>` on `spawn-one`,
or `--member-model <name>=<M>` on `create`, for that spawn only. Without one,
the spawn is refused with `member-model-undefined` and lists the models the
class allows. The built-in docs-writer, qa and ux-engineer roles launch on
their default model when none is set. `edit
--model ""` clears a stored one. An `advise` role always needs an explicit
`fable` or `opus`.

### A role that makes paid calls

If a role calls a paid outside service (image, voice or model APIs), tell it in its directive to count each call with `ah spend add <n> <unit> --what "<what>"`. This is guidance: nothing checks that it was followed, and a call that is not counted is not seen. If you set a spend cap, every session in its scope is already told this by the cap rule. A cap only asks you a question when it is reached; it never stops a call. See `ah spend` and `ah rules cap` in `cli-tools.md`.

### Milestone gates in a roster

A project can carry gates: named checkpoints with a list of artifacts that must be attached before the next phase. A role takes part by what its brief tells it to fill, for example `ah work attach <gate> <path> --as build`, or by sending a review report that is attached by its message id (`msg:<id>`). No role setting is needed, and no role is blocked or allowed by a gate.

A gate warns and logs. It never holds or refuses work. When an item starts past an open gate, `ah` prints a note naming the gate and what is missing, and logs it. The work still starts. Only the top Orchestrator or the user can put the gate to the user (`ah work gate <gate> --ask`), which parks one Pass or Not yet question. Who may fill needs or close a gate is not checked. The commands are in `cli-tools.md` under `ah work`, "Milestone gates".

### Resources a role uses

If a role drives one live thing that only one session can use at a time (an editor, a modelling tool, a device, a port), have it claim the thing by name. See [Claim a named resource](./cli-tools.md#claim-a-named-resource). Claiming is cooperative: nothing stops a member that skips it.

Tell the roles what to claim with a standing rule, written by you or by the Orchestrator with `--said`:

```
ah rules add --team <team> --key claim-blender --text "Claim resource blender (ah roster resource claim blender) before using it; release it when done."
```

The rule reaches every member session and subagent of that team at start. A rule must name each resource, so a member claims only what a rule names. To say it in the role itself, add one line to the role's agent body, for example: `Before you use Blender, run ah roster resource claim blender. Release it when you finish.` A claim waits while someone else holds the name; for a wait that may pass 2 minutes, run it in the background. The hold ends when the session's process is gone, so a resumed session claims again.

### Artifact review

A review-class role can judge something with no diff: an image, a sound, a build, a document. The review is a verdict against the brief, and the evidence files ride on the report.

1. **The brief** is an ordinary review request (`ah msg new … --item`). It names each artifact (a path, a URL or a `msg:<id>`) and lists the acceptance items.
2. **The reviewer makes the evidence.** It can write files only in its own `$TMPDIR`, so it writes them there or has its runner produce them (screenshots, captures, measurements) in its `$TMPDIR`. Tools that save to their own paths work too.
3. **It files the report with the files attached:**

```
<ah> msg fill --id <id> --from <your role> --body-file - --evidence "$TMPDIR/title-screen.png" --evidence "$TMPDIR/boot.wav" --cwd <abs cwd>
```

The files are copied out of `$TMPDIR` into `<AH_HOME>/evidence/<id>/`, so they survive the member being dismissed. The limits and what is never written are under [Attach evidence to a report](cli-tools.md#attach-evidence-to-a-report).

4. **The first line is the usual verdict**: `VERDICT PASS <slug>@-` or `VERDICT CHANGES REQUIRED <slug>@-`. `@-` stands for no commit, which is what an artifact has; the verdict parser reads only the leading word. Then one line per acceptance item, citing its evidence as `[e1]`.

What to know: the reviewer's own shell rules are unchanged. Every review-class role, whatever its name, gets the built-in reviewer's no-write rule: a file write is bounced to `ah msg fill --from <its role>`, except inside the member's own scratch directory (its `$TMPDIR`, under `<AH_HOME>/t/`), which every review-class member, the built-in reviewer included, may write with nothing declared. A request answered by SendMessage has no message id, so it cannot carry evidence.

#### Write folders for a review-class role

A custom review-class role that must save its own files, such as a tester's scripts, can declare the folders it may write, so it still can't edit the code it judges:

```
ah roster role set tester --writes 'tests/automation/**' --cwd <abs cwd>
```

- Each glob is relative to the checkout root, with `/` separators and only `*` and `**` as wildcards. A leading `/`, a `..`, `.` or empty segment, a `~` or an empty glob is a usage error (exit 2) and nothing is written. Give several globs separated by commas; `--writes ""` clears them. A glob that matches every path (`**`, `*`, `**/*`) is accepted with a warning.
- A Write, Edit, MultiEdit or NotebookEdit goes through only when its target, with links and `..` resolved, lies inside the member's own work tree and matches a glob. Any other write is bounced, and the bounce names the folders.
- `--writes` applies only to a custom review-class role. The built-in `reviewer` and roles of every other class refuse it.
- A roster kit carries `writes` on a custom review-class row. The dry run shows it as `may write: <globs>`, and a bad glob is a finding.
- The rule covers the file tools only. It is not a sandbox: a shell command can still write anywhere.

## 5. A member in another harness (Codex)

A roster member's `kind` picks the agent CLI Herdr starts. Omitted means
`claude`. Set `kind: codex` and it runs in Codex:

```
R add --role architect --kind codex --model <codex-model> --route pane
```

Which model names are valid depends on your account; `codex debug models`
lists them.

What changes for a non-Claude member:

- **`route` must be `pane`**, and spawning needs a Herdr session
  (`HERDR_ENV=1`). `add` and `edit` work anywhere.
- **`model`** is the harness's own model name, checked for shape only; the
  harness checks it at the first turn. `effort` is rejected. `auto_mode` is
  translated into Codex's sandbox and approval flags. `args` passes native
  flags verbatim, but not a model flag if `model` is set, nor Codex's approvals
  setting (`approvals_reviewer`, which every member launches with its own), and
  not at all on an advise-class member.
- **Its contract** is not loaded the way a Claude agent's is. Each spawn writes
  standing instructions to `<hierarchy dir>/instructions/<name>.md` — identity,
  the role's agent body, how a brief and a report work outside Claude Code —
  and launches Codex with them.
- A Codex member whose `auto_mode` leaves a read-only sandbox (`manual`,
  `plan`) gets a warning: it can't write its report without an approval in its
  pane.

**Tiers.** The hierarchy ranks models by Claude's tiers. For a Codex model,
you declare where it stands:

```
R tier set codex <codex-model> opus
R tier list
R tier remove codex <codex-model>
```

It is stored only in the global config. An **Ultra-Advisor** (advise class) on
a non-Claude kind must run on a model declared `opus` or `fable`; otherwise the
spawn is refused with `advise-model-tier` and prints the `tier set` command to
re-run. The tier is your call — an agent asks you rather than declaring it.

**Briefing it.** Claude Code's SendMessage can't reach a non-Claude member. The
Orchestrator briefs it with `deliver`, in the background:

```
R deliver <name> --req <abs request path>
```

It sends the request's `[hierarchy-msg …]` line, a `Report to:` path and the
standing-instructions path, waits for the turn to end, and returns a `status`
(`reported`, `no-report`, `busy`, `blocked`, …). The report is only the
response file. Nothing is sent to a member that is working or at a prompt. A
brief to an Ultra-Advisor needs your approval for the session, as it does for a
Claude one.

**Prompts.** Codex can stop at a trust dialog, a sign-in or an approval. Spawn
and `deliver` report it as `blocked` with the screen verbatim and the options
it recognises. The Orchestrator asks you, then sends your choice:

```
R answer <name> --prompt <blocked_by> --choice <id> --screen-hash <hash>
```

It sends only when the screen still shows that prompt and option, and never
free text. If no option is offered, or you'd rather do it yourself, answer in
the pane and the Orchestrator re-runs. Trusting a directory is always yours.
If Codex shows its trust dialog, that is a prompt like any other: it comes back
`blocked` with trust and distrust options, and you pick. Only when no prompt is
recognised *and* Codex's own config says the directory is untrusted is the
spawn refused (`harness-cwd-untrusted`) and its pane closed; run `codex` there
once and answer its question.

**Limits.**

- **Tool limits are advisory outside Claude.** A Codex member's agent file
  can't enforce `disallowedTools`; Codex's own sandbox is the only real limit,
  and it follows your Codex configuration.
- **Keys are pinned to the tested Codex version.** The prompt options and the
  idle-composer check match `codex-cli 0.154.0-alpha.6.2`. A changed label or
  layout is not offered or sent (it fails closed): the brief comes back
  `blocked` with `harness-prompt` and no options, and you answer in the pane.

### A member that takes part through the daemon

A member of `kind: custom` is an agent runtime ah has no adapter for. It reads its mail from the daemon,
answers briefs and reports back, following the [participation contract](./participation.md). Its team-file
row has `route: "mail"` and a `harness` name, and no model, effort or args. Admitting one and how the
Orchestrator vets a runtime that asks to join are in
[the daemon guide](./daemon.md#bringing-your-own-agent-runtime).

## 6. Role packs

A role pack is how you share roles with someone else. It is an ordinary
Claude Code plugin that also has an `ah-roles.json` at its root. Claude Code
installs, updates and removes it. Installing it is how you say you trust it:
ah asks for no second approval. You then name the pack on a roster, and every
team built from that roster can use its roles. Roles only you use can stay where they are, in
`~/.claude/agents/` or a repo's `.claude/agents/`.

### The format

```json
{
  "version": 1,
  "roles": {
    "godot-implementor": {
      "class": "implement",
      "agent": "godot-implementor",
      "label": "Godot Implementor",
      "description": "Builds Godot 4 features in GDScript",
      "routes": "Godot scenes, GDScript and shaders",
      "model": "opus"
    }
  }
}
```

- `version` must be 1, or the whole pack is unreadable.
- Each role takes the custom-role fields: `class` (required), `agent`,
  `label`, `description`, `routes`, `model`, `dispatch` and `params`. `peer`
  isn't allowed, because it is a local setting, and a role that has it is
  unavailable. Unknown keys are warned about and ignored.
- A `label`, `description`, `routes`, `model` or `dispatch` that breaks the
  rules for your own rows (one line, at most 160 characters, no backticks, and
  so on) is dropped with a warning, and the role stays.
- A role may be named after a built-in (`reviewer`, `architect`, …). Under a
  roster that names the pack, it [replaces that built-in's agent](#which-role-wins).
  Its `class` must be the built-in's class.
- `agent` names a file in the plugin, `agents/<agent>.md`, and defaults to the
  role's name. It can't point at another plugin's agent or at a path.
- No hidden text: control characters other than newline and tab, invisible
  formatting characters (bidirectional controls, zero-width characters),
  Unicode tag characters, variation selectors, the line and paragraph
  separators, and invisible filler characters. In `class` or `agent` they make
  the role unavailable; in another manifest field the field is dropped. In the
  agent file ah warns, naming the character, its line and column, and warns too
  when the file isn't valid UTF-8. An emoji written with a variation selector
  (⚠️, say) counts: write the plain character instead.

#### What ah warns about in a pack agent

Claude Code runs a pack's agent file exactly as installed, so ah's checks on it
are warnings. The role stays available. `ah roster pack show` and
`ah roster role list --json` list them.

- Frontmatter that goes beyond `name`, `description`, `model`, `tools`,
  `disallowedTools`, `color`, `effort` and `maxTurns`, each once, as a plain
  unquoted key. That includes `permissionMode`, `hooks`, MCP server settings,
  `skills`, `memory` and YAML anchors.
- No `tools` list, or a tool entry that isn't a plain name (quotes, a `[…]`
  list, a `Tool(scope)` form, `*`).
- Text ah can't be sure YAML reads the same way. A value continues onto more
  lines only as `- item` lines under an empty `tools` or `disallowedTools`, all
  at the first item's indentation, or as a `|` or `>` block under
  `description`, indented at least as far as its first line. A value holding
  `: `, or starting with a YAML indicator such as `@`, `!`, `[` or `- `, needs
  quotes, and a quoted value can't hold an escape (`\` or `''`). A value or tool
  entry that YAML reads as null or a boolean (`null`, `~`, `true`, `false`,
  `yes`, `no`, `on`, `off`, `y`, `n`, in any case) is flagged: `tools: null`
  would mean every tool. `maxTurns` is a plain decimal integer. Tabs in the
  indentation and `#` comments are flagged. Outside the description's text the
  frontmatter is printable ASCII; the description may hold other characters, but
  not a space other than the ordinary one, a private-use character, U+FFFE or
  U+FFFF.
- Tools the role's class shouldn't hold, such as `Edit` on a review role, or a
  grant of tools from an MCP server.
- Another agent file in the plugin that takes the role's agent name. Claude Code
  registers a plugin agent under its frontmatter `name`, not its file name, so
  it might launch that file instead. ah checks every `.md` under `agents/` and
  under the paths in `plugin.json`'s `agents`.

Your own agent files aren't held to the frontmatter rules. Agent files carried
in a [roster kit](rosters.md#share-a-roster) are, and there they refuse the
import.

### Writing a pack

A pack is a plugin directory with this shape:

    my-pack/
      .claude-plugin/plugin.json    # name, version, description (ah doesn't require it)
      ah-roles.json                 # the roles, as above
      agents/godot-implementor.md   # one file per role: agents/<agent>.md

ah finds a pack through `~/.claude/plugins/installed_plugins.json`, so a pack is whatever
Claude Code has installed that holds an `ah-roles.json`; a plugin directory that isn't installed
can be checked, but its roles aren't available (`ah roster pack show --path <dir>`). The agent ref at run time is
`<plugin>:<agent>`, with the plugin name from the install record.

An agent file with no warnings:

```markdown
---
name: godot-implementor
description: Builds Godot 4 features in GDScript
model: opus
tools: Read, Grep, Glob, Edit, Write, Bash
---
You build Godot 4 features …
```

- `name` is the agent's name, equal to `agent` in the manifest.
- List the tools in `tools`, on one line or as `- item` lines. Leaving it out
  gives the agent every tool, MCP tools included.
- Check it before you publish: `ah roster pack show --path <dir>` runs the
  manifest, the class contract and the frontmatter checks for every role and
  prints each finding.

When you update a pack, people who have it get the change at each member's next
start, with [a report of what changed](#pack-updates). Keep packs small, and keep
hooks and MCP servers out of them: users see everything the plugin carries before
they install it, under "also in this plugin".

### Installing a pack

Run this in Claude Code. `<source>` is a git URL, `owner/repo` or a local
directory:

    /wrangl:agent-role install <source>

It shows you the pack's roles and everything else the plugin carries before
anything is installed, installs it, checks that what got installed is what you
reviewed, and offers to put the pack on a roster. Sessions that were already
running need a restart before they can use the new agents as subagents.

`/wrangl:agent-role update <plugin>` and `uninstall <plugin>` drive Claude Code's
own plugin commands. Uninstalling also offers to take the pack off the rosters
that name it.

To look at packs at any time:

    ah roster pack list
    ah roster pack show <plugin>@<marketplace>

### Give a roster its packs

A [named roster](rosters.md#named-rosters) lists the packs it uses. Create one
with its pack, add a member for a pack role, and check the result:

    ah roster init --level global --route peer --roster game-factory --pack game-factory@bear-poppa
    ah roster add --level global --roster game-factory --role playtester --model opus
    ah roster role list --roster game-factory

`role list` shows the pack's roles for that roster. `playtester` here stands for
any role the pack offers; you use the pack's own role name.

`init` replaces that level's roster block, so use it only for a new roster. To
change the packs of one you already have, leaving its members alone:

    ah roster roster pack game-factory --add <plugin>@<marketplace>
    ah roster roster pack game-factory --remove <plugin>@<marketplace>

Things to know:

- A pack role needs no row, approval or setup of its own. A roster in your global
  config works in every repository.
- The roles exist only under that roster. Select it with `--roster`,
  `WRANGL_ROSTER` or `ah roster roster use`; see
  [Named rosters](rosters.md#named-rosters). A team uses the roster it was built
  from.
- ah reads the pack list from the roster's config each time, so a change also
  reaches teams already built from that roster.
- A roster may name several packs. The default roster can name packs too:
  `ah roster roster pack default --add <plugin>@<marketplace>`.
- A pack that isn't installed on this machine stays listed. Its roles are absent,
  with a warning, until you install it.

### Which role wins

Under a roster that names packs, a name resolves in this order:

1. a role from the roster's packs (when two packs offer the same name, the one
   listed first);
2. a row in your config;
3. the built-in.

One exception: an [old-style row](#roles-adopted-before-rosters-named-packs) that
points at that same pack role is used as it is, with your own settings on it.

A pack role named after a built-in replaces that built-in's agent, if its class is
the built-in's class. Your model, dispatch and peer settings for the built-in
stay. With any other class the role is unavailable and the shipped agent is used.

### Pack updates

A changed pack takes effect at each member's next start. Nothing is paused and
there is nothing to approve. ah compares the installed files with what it saw last
when you run `ah roster role list` or `ah roster doctor`, and when it starts a
single member. After a change:

- `role list` shows `updated <date>: files +a −r ~c, roles ~z` beside the role
  (files added, removed and changed, and roles changed);
- `ah roster doctor` has an `ok` row named `pack-updated`;
- if the change was first noticed on behalf of a team, its Orchestrator gets one
  `pack-updated` mail. Noticed outside a team, no mail is sent.

The line stays until the next change replaces it. The first time ah sees a pack it
only records it. `plugin.json`'s version doesn't matter: any file change counts,
except inside the plugin's `.git` directory.

### Pipeline runs

A pack role fills any slot its class allows in `/wrangl:pipeline` too: it can
design, implement or review, give a gate its sign-off, and replace the built-in
Reviewer or Architect. A run doesn't change a roster's packs on its own.

A run pushes branches and opens pull requests (drafts by default), and a merge
into `main` waits for a person unless you approved it at the start of the run.
Either way the merge rests on the reviewing role's verdict. If a pack supplies
your designer, builder and reviewer, their independence is whatever the pack's
author built.

### Roles adopted before rosters named packs

A config row with `from` (made with the old `role set --from`) still works. Its
`pin` and `slots` are ignored, and `~/.claude/ah2/trusted/` is no longer read;
you may delete it. You don't have to move. To move to the roster form:

    ah roster roster pack <roster> --add <plugin>@<marketplace>
    ah roster role remove <name>

Run the second line for each old row with the same name as a pack role.
`role remove` refuses while any roster member uses the name: take those members
off with `ah roster remove`, remove the row, then add them again.

If an old row gave the pack role a different name, it is left out under a roster
that supplies the same pack role, and the pack's own name is used there. (An old
row for a built-in gives way too: under that roster the built-in goes back to its
shipped agent.) Rename the roster's members with `ah roster edit --member <name>
--role <role>`; ah doesn't do it for you.

### What this does and doesn't protect

- Installing a plugin is the trust decision. Its agents are then available in
  every session as subagent types, and its hooks and MCP servers run in every
  session, whatever ah does. That's why ah shows all of it before install, and why
  packs that carry only roles are best.
- Permissions are Claude Code's: your settings and each member's permission mode
  decide what a pack agent may do. ah doesn't refuse a pack member for its
  permission mode, `bypassPermissions` included.
- An update reaches your teams without a review step. The report tells you what
  changed after the fact.
- Only roles of packs on the selected roster (and old `from` rows) are used by the
  hierarchy.
- Tool limits are role discipline, not a sandbox: a reviewer with Bash can still
  write files, and a pack's review role may be given `Edit`, with only a warning.
- The fields that reach every session's context keep the one-line, 160-character,
  no-backtick limits.

## Where to go next

- [cli-tools.md](./cli-tools.md) — every verb and flag.
- [`agent-role`](../skills/agent-role/SKILL.md),
  [`agent-roster`](../skills/agent-roster/SKILL.md),
  [`agent-team`](../skills/agent-team/SKILL.md) — the protocols behind the
  commands.
- [troubleshooting.md](./troubleshooting.md) — symptom → cause → fix.
