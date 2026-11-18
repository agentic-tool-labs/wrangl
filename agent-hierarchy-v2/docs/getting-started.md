# Getting started

This page is for someone installing Wrangl for the first time. Follow it from top to bottom and you will end with a
running team that has done a first job, in about ten minutes. Words you have not met yet are in the
[Glossary](glossary.md).

## 1. Check what you need

- Claude Code 2.1.289 or later.
- A .NET 10 SDK. Wrangl builds its program once on your machine. If you have none, the installer offers to install one
  just for Wrangl, in `~/.claude/ah2/dotnet`. It can only ask in a terminal; see
  [What you need](../README.md#what-you-need).
- A C toolchain for the build: the Xcode command line tools on macOS (`xcode-select --install`), or clang and the zlib
  development headers on Linux. The installer checks and prints the command for your system.
- macOS or Linux. There is no Windows install.
- Optional: herdr, a terminal multiplexer for coding agents, so each member gets its own pane. Without it Wrangl uses
  tmux if a tmux server is running, and plain terminal windows otherwise.
- Optional: [gitleaks](https://github.com/gitleaks/gitleaks).

Wrangl is in early testing: use it with caution, and only on work you have committed or backed up. See
[Early testing](../README.md) at the top of the README.

## 2. Turn off the older `ah` plugin, if you have it

Skip this step if you never installed `ah` (v1). Running v1 and Wrangl together runs every hook twice. Inside Claude
Code:

```
/plugin disable ah@agent-tools
```

Then restart Claude Code, or run `/reload-plugins`. If v1 still answers in one project, look in that repository's
`.claude/settings.json`: an `enabledPlugins` entry for `ah@agent-tools` turns it back on there. To go back to v1 later,
see [Wrangl and ah v1](../README.md#ah2-and-ah-v1).

## 3. Install Wrangl

In a terminal:

```
curl -fsSL https://raw.githubusercontent.com/agentic-tool-labs/wrangl/main/install.sh | bash
```

It shows its plan and asks before it changes anything, installs the `wrangl` plugin into Claude Code, and builds and
links the `ah` and `ahd` commands. To read the script before you run it, or to see what each step prints, see
[Install with one command](../README.md#install-with-one-command).

Or step by step, inside Claude Code:

```
/plugin marketplace add agentic-tool-labs/wrangl
/plugin install wrangl@wrangl
/wrangl:hierarchy install
```

`/wrangl:hierarchy install` asks three questions: where to link the commands (default `~/.local/bin`), whether a
plugin update may rebuild the program in the background (default yes), and whether to start the daemon at login
(default no). Then it shows the installer's plan, and writes nothing until you agree. The details, and the terminal
form of the same installer, are in [Install step by step](../README.md#install-step-by-step).

Check the build. In a new terminal, or in Claude Code with a leading `!`:

```
ahd --version
```

It prints `ahd 2.0.10 (contract 1)`. If the terminal says `ahd` is not found, add the folder the installer printed (by
default `~/.local/bin`) to your `PATH`.

Optional: if you run `scripts/install.sh` yourself in a terminal, it can also add Claude Code allow rules so the
Orchestrator runs team commands without asking you each time. Nothing is added unless you agree. See
[Lifecycle permissions](lifecycle-permissions.md) for exactly what they allow.

## 4. Turn the hierarchy on

```
/wrangl:hierarchy init
```

It asks two things:

- **Where the setting applies:** **user** (every repository) or **project** (this repository only; the file can be
  committed).
- **How hand-offs work:** `auto` (the Orchestrator moves work between roles and tells you) or `confirm` (it asks you
  before each hand-off to a reasoning role). Change it any time with `/wrangl:hierarchy flow auto` or
  `/wrangl:hierarchy flow confirm`.

When it finishes, run `/clear` or start a new session. The Orchestrator's instructions load when a session starts, so
the session you ran `init` in does not follow them yet.

Check everything, in a terminal inside the repository:

```
ah roster doctor --brief
```

It prints one line per check and changes nothing. No line should start with `red`, and the `config` line should read
`ok config`. A `red` line names its own fix; if it does not make sense, look it up in
[Troubleshooting](troubleshooting.md).

## 5. Choose your team

With no roster yet, `/wrangl:hierarchy init` hands you on to the `/wrangl:agent-roster` wizard. It asks which roles you
want and which model each one runs. If you finished the wizard, go to step 6.

To skip the wizard, or to add a member later, use `add`. Each line adds one member to the roster you have, or starts a
roster for this repository if you have none:

```
/wrangl:agent-roster add --role architect
/wrangl:agent-roster add --role implementor
/wrangl:agent-roster add --role reviewer
```

The roster is only a template: changing it starts nothing. Setting the model per member, and keeping several rosters,
are in [Rosters](rosters.md). To add roles of your own, see [Custom roles](custom-roles.md).

## 6. Start the team

Say it in plain words to the Orchestrator: "set up my team". The `/wrangl:agent-team` skill asks how to lay out the
panes, then starts a session for each member on the roster.

Check that they are up:

```
ah roster teams
ah roster status --plain
```

`ah roster teams` lists your teams. `ah roster status --plain` shows each team's members and what they are doing.

## 7. Say what the team is for (optional)

Tell the Orchestrator the goal in one sentence, for example: "our goal is a working login page, done when the login
tests pass". It records your words as the **team goal**. Every member sees the goal at the start of its session and
with each brief. The goal is guidance: nothing is refused because of it. To see it:

```
ah rules goal --team <name>
```

## 8. Give it a job

Describe the change you want. A typo or a config edit the Orchestrator does itself. For a real change it:

1. asks the **Architect** for a design (a spec),
2. gives the spec to the **Implementor**, who builds it,
3. sends the result to the **Reviewer**, who checks it, and
4. merges it after the tests pass. [Shipping work](shipping.md) has the details.

Each hand-off is a written **brief**, kept as a file in `.claude/ah2/msgs/` so it survives a crash. You will see lines
like `[hierarchy-msg 20261105-060918-jz6b]` in the conversation; that is normal. It names the brief.

To watch the work:

| Command | What opens |
|---|---|
| `ahd ui` | the [web page](web-ui.md) in your browser; press Ctrl-C in the terminal to close it |
| `ahd tui` | the [terminal view](tui.md), read-only, good for a second pane |

To run a whole plan or a list of GitHub issues without sitting in front of it, use `/wrangl:pipeline`
([Shipping work](shipping.md#pipeline)).

## 9. Finish

When the work is merged, ask the Orchestrator to wrap up the stream and dismiss the team. `/wrangl:agent-team disband`
shows a plan, asks you, and then closes the member sessions. Your conversations are kept. In the herdr desktop app you
can also dismiss a team from its row in the sidebar ([herdr desktop](../herdr-desktop/README.md)).

To forget the team's records but leave the sessions running:

```
ah roster untrack --all --keep-sessions --commit
```

## You now have

- Wrangl installed and checked with `ah roster doctor --brief`.
- A roster that says which roles you want.
- A running team that has taken a job from brief to review.

## Where next

| I want to | Read |
|---|---|
| understand the pieces | [How it works](how-it-works.md) |
| look up a word | [Glossary](glossary.md) |
| set models, or keep several rosters | [Rosters](rosters.md) |
| look up a command | [CLI reference](cli-tools.md) |
| move to a new release with a live team | [Upgrading](upgrading.md) |
| get a team back after a crash | [Recovery](recovery.md) |
| fix something | [Troubleshooting](troubleshooting.md) |
