# Wrangl: Agent Hierarchy

<img src="assets/wrangl.svg" width="96" alt="Wrangl mascot: a groundhog in a cowboy hat">

Wrangl is a Claude Code plugin that turns one Claude Code session into a small software team. Your session becomes the
**Orchestrator** (in full, the Chief Orchestrator). It breaks a job into pieces and hands each piece to a specialist: an
Architect who writes the design, an Implementor who builds it, a Reviewer who checks it, and others. Each specialist runs
on the model that suits its job, so design gets a strong model and routine legwork gets a cheap one. A small background
program on your machine, the daemon, carries messages between them. The commands are `ah` and `ahd`.

This page tells you what Wrangl is, how to install it, and how to get a first team running in about ten minutes.

Wrangl also does the unglamorous work around a team: it passes messages between members, keeps them from writing
outside their own work area, tells you when someone is stuck, and brings the team back after a crash or reboot.

> [!WARNING]
> **Early testing: use with caution.** Wrangl 2.0.31 is an early public release and is still being tested. It starts
> and controls other agent sessions that edit files and run commands in your repository, so use it only on work you
> have committed or backed up. Expect bugs, and expect commands, settings and screens to change between releases.
> Wrangl is provided "as is", without warranties of any kind; see the [LICENSE](../LICENSE). Report problems at
> <https://github.com/agentic-tool-labs/wrangl/issues>, with the output of `ah roster doctor --brief`.

## Who it is for

You use Claude Code for changes that are too big for one conversation, and you want several agents working in
parallel without losing track of them or letting them trample each other. You are comfortable in a terminal.

You do not need it for small edits. The Orchestrator handles a typo or a config change itself.

## The team

| Role | What it does | Never does |
|---|---|---|
| **Chief Orchestrator** | your own session; splits the work, hands it out, collects the results | design or build big changes itself |
| **Architect** | writes the design (a "spec" file) | build or run anything |
| **Implementor** | builds exactly what the spec says | make design decisions |
| **Reviewer** | checks the work against the spec | edit or run anything |
| **Ultra-Advisor** | settles the hardest calls; you can tell the Orchestrator not to use it | build anything |
| **Docs-Writer** | writes READMEs, guides and changelogs | edit source code |
| **UX-Engineer** | builds web UI and proves it with screenshots | touch the daemon or data |
| **QA** | runs the heavy test suites and reports failures | edit anything |
| **Sub-Orchestrator** | runs one separate line of work (a "lane") for the Orchestrator | push or merge |
| **Task-Runner** | cheap legwork: run builds, search, read logs | think or decide |
| **Backlog-Groomer** | grooms the backlog: triage, ranking, dedupe, proven-done items | edit code or dispatch |

How the roles fit together, and what happens to a request, is in [How it works](docs/how-it-works.md).

## Install

Installing takes four steps: check what you need, install the plugin, build the `ah` and `ahd` commands, and turn
the hierarchy on. The one-command installer does the first three for you.

### What you need

- **macOS** (Apple silicon or Intel) or **Linux** (x64 or arm64). There is no Windows install.
- **Claude Code 2.1.289 or later.** `claude --version` shows yours.
- **A .NET 10 SDK.** Wrangl builds its program once on your machine. If you have none, the installer offers to
  install one just for Wrangl, in `~/.claude/ah2/dotnet` (a few hundred MB; your `PATH` is not changed). It can only
  ask you in a terminal: run from inside Claude Code without an SDK, it stops and says so, and you run the
  [terminal form](#build-from-a-terminal-instead) once.
- **A C toolchain for the build.** On macOS, the Xcode command line tools (`xcode-select --install`). On Linux, clang
  and the zlib development headers (for example `sudo apt-get install clang zlib1g-dev`). The installer checks this
  first and prints the command for your system.
- Optional: **herdr**, a terminal multiplexer for coding agents, so each member gets its own pane. Without it Wrangl
  uses tmux if a tmux server is running, and plain terminal windows otherwise.
- Optional: [gitleaks](https://github.com/gitleaks/gitleaks).

**If you have the older `ah` plugin (v1), turn it off before you install.** Running both runs every hook twice.
Inside Claude Code:

```
/plugin disable ah@agent-tools
```

Then restart Claude Code, or run `/reload-plugins`. More, and how to go back, in [Wrangl and ah v1](#ah2-and-ah-v1).

### Install with one command

In a terminal:

```
curl -fsSL https://raw.githubusercontent.com/agentic-tool-labs/wrangl/main/install.sh | bash
```

Or, from a clone of the repository:

```
bash install.sh
```

The script:

1. checks that Claude Code is installed;
2. shows its plan and asks before it changes anything (`--yes` skips the question, `--plan` only prints the plan; see
   `install.sh --help`);
3. adds the `wrangl` marketplace and installs the `wrangl` plugin into Claude Code;
4. runs the plugin's own installer, which builds `ahd` and links `ah` and `ahd` onto your `PATH`;
5. prints the next steps: [turn the hierarchy on](#turn-the-hierarchy-on).

It never edits your Claude Code settings and never uses `sudo`. Run it again to update.

Piping a script from the internet into `bash` runs code you have not read. To read it first:

```
curl -fsSL https://raw.githubusercontent.com/agentic-tool-labs/wrangl/main/install.sh -o wrangl-install.sh
less wrangl-install.sh
bash wrangl-install.sh
```

### Install step by step

Use these steps if you would rather not run the script, or if it stopped part way.

**1. Add the marketplace and install the plugin.** Inside Claude Code:

```
/plugin marketplace add agentic-tool-labs/wrangl
/plugin install wrangl@wrangl
```

Claude Code confirms each one. `/plugin` then lists `wrangl@wrangl` as installed. If `/wrangl:hierarchy` is not
offered when you type it, run `/reload-plugins`.

**2. Build the commands.** Inside Claude Code:

```
/wrangl:hierarchy install
```

It tells you what it will write and asks three questions:

| Question | Default |
|---|---|
| where to link the `ah` and `ahd` commands | `~/.local/bin` |
| may a plugin update rebuild the program in the background | yes |
| start the daemon when you log in | no |

Then it shows the installer's plan. **Nothing is written until you agree to that plan.** When you do, it builds the
program into `~/.claude/ah2/bin` (`ahd` and its terminal view `ahd-tui`) and links `ah` and `ahd` into the folder you
chose. If that folder is not on your `PATH`, the plan prints the line to add to your shell startup file; from inside
Claude Code it does not edit that file for you.

<a id="build-from-a-terminal-instead"></a>

**Build from a terminal instead.** The same installer runs in a terminal, where it can also ask you about the .NET
SDK and your shell startup file. Without `--apply` it only prints its plan:

```
sh ~/.claude/plugins/cache/wrangl/wrangl/2.0.31/scripts/install.sh
sh ~/.claude/plugins/cache/wrangl/wrangl/2.0.31/scripts/install.sh --apply
```

`2.0.31` is the version you installed; `ls ~/.claude/plugins/cache/wrangl/wrangl/` shows it. `--prefix DIR` picks
the link folder, `--login` adds the login item, and `--help` lists every flag. In a terminal the installer may also
offer allow rules so the Orchestrator can run team commands without a prompt each time; nothing is added unless you
say yes ([Lifecycle permissions](docs/lifecycle-permissions.md)). It also installs Anthropic's `frontend-design`
skill for the UX-Engineer role unless you pass `--no-design-skills` ([UX-Engineer](docs/ux-engineer.md)).

**3. Check the build.** In a new terminal (or inside Claude Code with a leading `!`):

```
ahd --version
ahd where --plain
```

`ahd --version` prints `ahd 2.0.31 (contract 1)`. `ahd where --plain` prints where the program and the plugin are,
and includes the line `compatible: yes` when the two match. If the terminal says `ah` or `ahd` is not found, add the
link folder (by default `~/.local/bin`) to your `PATH`.

### Turn the hierarchy on

Inside Claude Code:

```
/wrangl:hierarchy init
```

It asks whether the setting applies to every repository (**user**) or only this one (**project**), and whether
hand-offs between roles are automatic or confirmed by you each time. With no roster yet, it then hands you on to the
`/wrangl:agent-roster` wizard to pick your team ([Quick start](#quick-start-about-ten-minutes), step 2).

When it finishes, run `/clear` or start a new session. The Orchestrator's instructions load when a session starts, so
the session you ran `init` in does not follow them yet. `/clear` clears the conversation.

Then check everything, in a terminal inside the repository:

```
ah roster doctor --brief
```

It prints one line per check and changes nothing. A healthy install has no line starting with `red`, and shows
`ok config`. A `warn` line is advice; a `red` line names its own fix. Before `init`, the `config` line reads
`warn config: not configured`. If a line does not make sense, look it up in [Troubleshooting](docs/troubleshooting.md).

<a id="ah2-and-ah-v1"></a>

### Wrangl and ah v1

`ah` (v1) and Wrangl are two separate plugins. Wrangl does not change or remove v1, but both react to the same Claude Code
events, so running them together in one session doubles every hook. Keep only one enabled at a time.

Turn v1 off while you use Wrangl. It takes effect from your next session (restart Claude Code or run `/reload-plugins`):

```
/plugin disable ah@agent-tools
```

A repository's `.claude/settings.json` can re-enable v1 for that project with `enabledPlugins`; check it if v1 still
answers. `ah roster doctor --brief` warns when v1 is still enabled.

To go back to v1, turn Wrangl off and v1 on:

```
/plugin disable wrangl@wrangl
/plugin enable ah@agent-tools
```

Disabling a plugin keeps it installed. Wrangl keeps its own state in `~/.claude/ah2` and `<repo>/.claude/ah2`.

### Managed Claude Code

If your organization manages Claude Code for you, Wrangl works as long as mods are allowed to run: its hooks run as a
mod. If the managed settings set `allowManagedHooksOnly`, hooks from a plugin you installed yourself do not run at all.
Ask your administrator to install Wrangl as a managed plugin.

Running as a mod has three small limits:

- When a subagent finishes without filing its report, Wrangl logs it but cannot send the subagent back to work.
- Wrangl works out where a session's transcript is from where Claude Code keeps transcripts, because a mod is not told.
- Wrangl's hooks are not told the session's permission mode.

### Uninstall

1. Dismiss any running team first (see [Getting started](docs/getting-started.md#9-finish)).
2. Inside Claude Code, remove the program:

   ```
   /wrangl:hierarchy uninstall
   ```

   It shows a plan and asks first. It turns off the login item, stops the daemon, and removes the program and the
   install record from `~/.claude/ah2`, the `ahd` link, and the `PATH` block the installer added to your shell
   startup file. It asks separately whether to remove the private .NET SDK and the build cache; neither is removed
   by default.
3. Remove the plugin, and the marketplace if you want:

   ```
   /plugin uninstall wrangl@wrangl
   /plugin marketplace remove wrangl
   ```

What is left behind, on purpose: Wrangl's database, settings and records in `~/.claude/ah2`, and each repository's
`<repo>/.claude/ah2`. Delete those folders yourself if you want them gone; this cannot be undone. Also left: the `ah`
link in your link folder (remove it with `rm ~/.local/bin/ah`), the `frontend-design` skill if the installer added it,
and any allow rules you accepted (remove those **before** you uninstall, see
[Lifecycle permissions](docs/lifecycle-permissions.md#turn-it-off)).

### Optional: Wrangl in the herdr desktop app

If you use the herdr desktop app, this adds Wrangl's sidebar and layout to it:

```
ah herdr install
```

`ah herdr install --uninstall` removes it again. See [Wrangl in Herdr desktop](herdr-desktop/README.md).

## Quick start (about ten minutes)

1. **Turn the hierarchy on.** Run `/wrangl:hierarchy init` once, then `/clear` or start a new session. See
   [Turn the hierarchy on](#turn-the-hierarchy-on).
2. **Pick who is on your team and what model each runs.** With no roster yet, `/wrangl:hierarchy init` hands you on to the
   `/wrangl:agent-roster` wizard, which asks which roles you want and which model each runs. To add a member later, or
   to skip the wizard, use `add`, for example:

   ```
   /wrangl:agent-roster add --role architect
   /wrangl:agent-roster add --role implementor
   /wrangl:agent-roster add --role reviewer
   ```

3. **Start the team.** Ask in plain words: "set up my team". The `/wrangl:agent-team` skill starts a session for each
   member on the roster.
4. **Say what the team is for (optional).** Tell the Orchestrator the goal in one sentence, for example "our goal is
   a working login page". It records your words as the team goal, and every member sees it. It is guidance: nothing
   is refused because of it. You can also set it yourself in a terminal (`ah roster teams` lists the team names):

   ```
   ah rules goal --team <name> --text "A working login page" --done-when "the login tests pass"
   ```

5. **Give the Orchestrator a job.** Describe the change you want. For a small job it does it itself. For a bigger
   one it writes a brief for the Architect, hands the spec to the Implementor, and sends the result to the
   Reviewer. You see a short status line as work moves.
6. **Check on the team** at any time:

   ```
   ah roster teams
   ah roster status --plain
   ```

   To quiet one member for a while, pause it. `ah roster pause-member <member>` (or `--team <name>` for every member)
   stops the daemon's nudges, re-deliveries and alarms for that member, and each one is told by mail. It closes nothing, unlike
   `ah roster quit`. `ah roster resume-member <member>` (or `--team <name>`) ends the pause and restarts the member's
   request clocks.

7. **Wrap up.** When the work is merged, ask the Orchestrator to wrap up the stream and dismiss the team.

To run a whole plan or a list of GitHub issues without sitting in front of it, use `/wrangl:pipeline`. See
[Shipping work](docs/shipping.md#pipeline).

## If something goes wrong

- Run `ah roster doctor`. It names what is wrong and how to fix it.
- A session died or the machine restarted: see [Recovery](docs/recovery.md). The short version is
  `ah roster resume --team <name> --yes`, run from a new Claude Code session in the same repository.
- A member is stuck at a prompt or its mail seems lost: see [Mail](docs/mail.md).
- Everything else: [Troubleshooting](docs/troubleshooting.md).

## Reading order

New here? Read these in order:

1. This page.
2. [Getting started](docs/getting-started.md): install, build a team, give it a job.
3. [How it works](docs/how-it-works.md): roles, briefs and mail, the checklist and Sitrep, shipping.
4. [Glossary](docs/glossary.md), whenever a word is new.

Then pick what you need:

| I want to | Read |
|---|---|
| see my team live in a browser (tabs, Sitrep, looks) | [The web page](docs/web-ui.md) |
| see my team in a terminal | [The terminal view](docs/tui.md) |
| set models, or keep several rosters | [Rosters](docs/rosters.md) |
| add roles of my own | [Custom roles](docs/custom-roles.md) |
| see how a change gets merged, or run a whole plan | [Shipping work](docs/shipping.md) |
| look up a command | [CLI reference](docs/cli-tools.md) |
| understand message delivery | [Mail](docs/mail.md) |
| understand the background program | [The daemon](docs/daemon.md) |
| let the Orchestrator run team commands without a prompt each time | [Lifecycle permissions](docs/lifecycle-permissions.md) |
| move to a new release with a live team | [Upgrading](docs/upgrading.md) |
| get a team back after a crash | [Recovery](docs/recovery.md) |
| fix something | [Troubleshooting](docs/troubleshooting.md) |
| see what changed | [CHANGELOG.md](CHANGELOG.md) |
| know what is not shipped yet | [What is coming](docs/coming-soon.md) |

For contributors and integrators: [Architecture](docs/architecture.md), [Daemon API](docs/daemon-api.md) and the
[Participation contract](docs/participation.md) for running an agent program other than Claude Code on a team.

## License

Apache-2.0. See [LICENSE](../LICENSE).
