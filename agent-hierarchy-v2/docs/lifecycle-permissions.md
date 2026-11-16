# Lifecycle permissions

Wrangl can ask Claude Code to stop prompting you each time your Orchestrator runs
an `ah` command or sends a message. This page says exactly what that allows, what it
does not, where it is written, and how to turn it off. It is **off until you
turn it on**, and you can turn it off again at any time.

If you only want the commands, jump to [Turn it on](#turn-it-on),
[Check what is set](#check-what-is-set) or [Turn it off](#turn-it-off).

## What it is

It is twenty-three Claude Code *allow rules*: one per team lifecycle command, one
for every `ah` command, one for `SendMessage`, and one for `cd`. Together they let the
Orchestrator run `ah` and message its team without a permission prompt, and,
because the calls match an allow rule, without going through the auto-mode
classifier either. An allow rule does one thing: it skips the "may I run this?"
question for a call that matches it. It does not add commands, remove checks, or
give any other tool access.

Wrangl writes the rules into your Claude Code **user** settings file, under
`permissions.allow`. Nothing is written to a project file.

## The rules

Each rule has this shape, with the plugin directory of the Wrangl version you have
installed:

```
Bash(<plugin root>/bin/ah roster <verb>:*)
```

`<plugin root>` is a path like `~/.claude/plugins/cache/<marketplace>/wrangl/<version>`
written out in full. It contains the version number, so the rules are tied to one
version (see [When you upgrade](#when-you-upgrade)). To see the exact text for your
install, run:

```
ah roster permissions rules
```

There is one rule per lifecycle verb, plus three more:

- `Bash(<plugin root>/bin/ah:*)`: every `ah` command run by that path, the
  verbs below included.
- `SendMessage`: Claude Code's tool for messaging another agent.
- `Bash(cd:*)`: changing directory, so a `cd <dir> && ah ...` call is not held up
  by a prompt on the `cd` part.

The lifecycle verbs:

| Verb | What it does | What it can change |
|---|---|---|
| `disband` | Ends a team. With `--plan` it only shows a plan; with `--close --confirm` and the plan's token it closes the members' sessions. | Closes sessions. It holds a member that is working now, unless `--ignore-release <name>` names it; any other member closes with a warning. |
| `dismiss` | Does the same for one named member. | Closes one session. With `--also-config` it also removes the member from your roster file. |
| `move` | Moves a member's pane to another tab, a new tab or a new workspace. | Changes where a live pane sits and records it in the team file; starts and closes no session. |
| `resume` | Relaunches the members saved in a snapshot. Without `--yes` it prints the plan and launches nothing. | Starts sessions; makes the resuming session the team's Orchestrator. |
| `snapshot` | Saves the team's launch settings and session ids to a snapshot file. | Writes one snapshot file (the newest 10 per team are kept). Not owner-gated: anyone may run it. |
| `spawn-one` | Starts one member defined in your roster. | Starts a session; records it in the team file. |
| `spawn-ad-hoc` | Starts a member your roster does not define, or one with different settings. | Starts a session; writes only the team file, never your roster. |
| `create` | Plans, launches (`--spawn`) and records (`--commit`) a team. | Starts sessions and writes the team file. |
| `adopt` | Re-claims a team for the current session. | Changes which session the team file names as its owner. |
| `untrack` | Drops a member's tracking record without closing its session. | Edits the team file only. |
| `stream-open` | Records a stream and gives it its own git work tree and branch. | Writes the team file, and creates a git work tree and branch under `<main checkout>/.claude/worktrees/`. Starts no session. |
| `stream-done` | Marks a stream finished. | Edits the team file; on herdr it also closes the stream's tab, only when every pane in it is one ah made and none runs a harness; otherwise it reports `tab_kept` and why. It closes no member pane and removes no work tree or branch. |
| `progress` | Records a stream's plan items and renders its checklist. | Writes the stream's plan log. |
| `stream-report` | A sub-orchestrator's status line for its own stream. | Writes one status record in its own team file, nothing else. |
| `close-approve` | A managing Orchestrator approves a sub-orchestrator closing its child team. Without a token it prints the plan and writes nothing. | With the current token, records the approval in the managing team's file. |
| `checkpoint` | Brings a team to a state a restart can survive: saves a snapshot, asks every member that is not idle and clean to commit its work and stop its background processes, and writes a record. With `--status` it only reads. | Writes a snapshot file and a checkpoint record, files one request per asked member and sends it as mail, and adds one `Resume point:` line to the team's progress rollup. It never pushes and never closes a session. |
| `wake` | The background waiter: it blocks until ahd has an unacknowledged event for the Orchestrator or 20 minutes pass, prints it, and exits so the session is re-invoked. Orchestrator only. | Writes its own wake file and a list of event ids it has printed under the ah home. It makes no daemon request and acknowledges nothing. |
| `overdue` | The failsafe timer: it reads the request files and the Orchestrator's mail files and exits when ahd missed a dispatch step by two minutes. Orchestrator only. | Writes its own wake file, its memory of what it reported, the ahd health file, and journal lines under the ah home. It never connects to the daemon. |
| `stall-diagnose` | Reads the same files for one dispatch and prints why ahd did not wake the Orchestrator, as six facts and one verdict. Any role. | Appends one line to the stall journal under the ah home. |
| `checklist` | The Orchestrator's durable list of what is outstanding: `show` prints the open items, `add`, `set` and `done` change them. Every plan item of the team's streams plus the loose-end stream `_run`. Orchestrator only. | Appends rows to the stream plan logs (`<stream>.plan.jsonl`, `_run.plan.jsonl`) and re-renders the stream checklist. |

"Starts sessions" means new agent sessions on your machine, running with the
settings the roster gives them. The allow rule only removes the prompt; you still
decide what goes in the roster.

## What it does not allow

- **`ah` by any other path.** The all-of-ah rule covers `msg`, `role` and
  every other `ah` command, but only as run by the installed path the rule
  names (see the next point).
- **The same command by another path.** A rule matches the command text as
  written. Claude Code's documentation says a Bash rule "doesn't match the same
  program invoked in a different form", so an `ah` found on your `PATH`, or
  another plugin directory, does not match these rules. The rules name the
  installed plugin's own path only.
- **Chained commands.** Claude Code's documentation says a rule "must match each
  subcommand independently", and lists `&&`, `||`, `;`, `|`, `|&`, `&` and newlines as
  separators. `ah roster disband … && rm -rf x` does not become allowed because
  its first half is: the `rm` part still asks.
- **Other tools.** Apart from `SendMessage`, the rules are `Bash(...)` rules. They
  say nothing about reading or editing files, web access or other tools.
- **Skipping ah's own checks.** ah still refuses a member or subagent that is not
  the team's owner, for every owner command in the table. ah also installs hooks
  that deny some of these commands to members and subagents. Claude Code
  documents that a hook which blocks a call with exit code 2 takes precedence over
  allow rules. ah's hooks answer with a `permissionDecision`, not an exit code, and
  Claude Code's documentation does not say outright that this form beats an allow
  rule. **Unverified:** that ordering. It does not weaken ah's own owner check,
  which runs inside the command whatever Claude Code decided.

One more thing to know: the rules point at a program file. Anything that can
write to `<plugin root>/bin/ah` could then run these twenty commands without a
prompt. ah therefore refuses to write the rules unless the plugin directory is an
installed version under Claude Code's plugin cache, owned by you, not writable by
anyone else, and not inside a git work tree. A plugin installed from a local
folder is refused.

## Turn it on

You choose. The installer never turns this on for you: it needs a flag, or a
"yes" you type.

Run the installer from a normal terminal, **not** from inside a Claude Code
session. ah refuses to write its own permissions from inside a session, so an
agent cannot grant itself rules.

Add the rules without being asked:

```
sh ~/.claude/plugins/cache/<marketplace>/wrangl/<version>/scripts/install.sh --apply --allow-lifecycle
```

Or run the installer with neither flag and answer the question:

```
sh ~/.claude/plugins/cache/<marketplace>/wrangl/<version>/scripts/install.sh --apply
```

At a terminal it lists the rules, tells you in three lines what they allow and do
not allow, points you at this page, and then asks `[y/N]`. The default is No:
pressing Enter adds nothing. Without a terminal, or with `--quiet`, it asks
nothing and adds nothing.

Every time the installer changes your settings it prints a line about it, even
with `--quiet`. The line names the rules added, the settings file, the backup
file and how to undo it. Using both `--allow-lifecycle` and
`--no-allow-lifecycle` is refused before anything is built or written.

`--rebuild-only` skips this step, so the upgrade command in
[upgrading.md](./upgrading.md) never changes your permissions.

If you are never asked, run `ah roster permissions check`. It prints `ask`,
`sync`, or `skip:` followed by the reason. ah offers the rules only when its own
checks pass; `ah roster doctor` shows them as the `owner-proof` and
`release-handshake` rows.

## Where it is written

- **Settings file:** `~/.claude/settings.json`, or `settings.json` inside
  `CLAUDE_CONFIG_DIR` when you have set that. Claude Code documents that
  variable as the place it keeps your settings, history and plugins. The rules go
  in the `permissions.allow` list.
- **Backup:** `settings.json.ah2-bak`, next to the settings file. It is a copy of
  the file as it was before the write, and each write replaces it, so it holds
  only the latest previous copy.
- **Ledger:** `permissions.json` in Wrangl's home (`~/.claude/ah2` unless you moved
  it). It records which rules ah added, for which plugin directory, when, and
  whether you said yes or no. `remove` uses it to delete only what ah added, and
  the upgrade refresh uses it to know you opted in.

Writing the settings file reformats it: key order and layout are not kept. A file
with comments or trailing commas is not strict JSON; ah refuses it and leaves it
exactly as it was. If `settings.json` is a link, ah writes through the link and
leaves it a link.

A rule you already had in the file is not recorded, and is never removed by ah.

## Check what is set

```
ah roster permissions rules
```

Prints the twenty-three rules exactly as they would be written for this install.

```
ah roster permissions check
```

Prints `ask` (the rules have not been offered or answered), `sync` (you said yes;
the installer refreshes or confirms the rules) or `skip:` and a reason. It writes nothing.

```
ah roster doctor
```

Its `lifecycle-permissions` row says whether the rules in your settings match the
ledger and this version's path.

You can also open the settings file and look in `permissions.allow` for entries
that end in `/bin/ah roster <verb>:*)`.

## Turn it off

```
sh ~/.claude/plugins/cache/<marketplace>/wrangl/<version>/scripts/install.sh --apply --no-allow-lifecycle
```

or, in a terminal:

```
ah roster permissions remove
```

Both remove the rules ah added and record your "no", so you are not asked again
and no later upgrade puts them back. To add them again later, use `--allow-lifecycle`
as above, or:

```
ah roster permissions add
```

All of these refuse to run inside a Claude Code session. Run them from a plain
terminal.

## When you upgrade

The rules name one plugin version's directory, so a new version needs new rules.
If you opted in earlier, a full install (`install.sh --apply`, without
`--rebuild-only`) refreshes your rules for the new version without asking again,
and prints a line saying it did, the date the rules were last set, and how to revoke. It
replaces only rules ah added; it does not add anything you did not choose. If a
new version brings more lifecycle commands than you have rules for, it tells you
and leaves them off until you run the `--allow-lifecycle` command again. If you
never opted in, or said no, nothing is refreshed or written.

Until the refresh runs, the old rules no longer match and the prompts come back.
`ah roster doctor` reports this.

## See also

- [cli-tools.md](./cli-tools.md#lifecycle-permissions): the reference for the
  `permissions` command, for agents and scripts.
- [upgrading.md](./upgrading.md): the upgrade steps.
- Claude Code's own documentation of permission rules:
  <https://code.claude.com/docs/en/permissions>
