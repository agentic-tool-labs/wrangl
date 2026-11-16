# Upgrading with a live team

How to move to a new Wrangl release while a team is running, without losing the
members' conversations. If you have no team running, only steps 1 to 3 apply.

An upgrade has two halves, and an update of the plugin alone is not enough:

- the **plugin files** (skills, agents, hook configuration), which `/plugin`
  copies into the plugin cache;
- the **native build** (`ahd`, the daemon, and `ah-hook`, the hooks' program),
  which is built on your machine. The daemon and the hooks must be the same
  build. A daemon of an older build, next to newer hooks, can show relayed mail
  twice.

The release notes in [CHANGELOG.md](../CHANGELOG.md) say whether a release needs
a native rebuild. Expect most releases to need one. After a rebuild, restart your Orchestrator session as well as the
members, so that its directive comes from the same build.

In the commands below, `ah` stands for `<AH_ROOT>/bin/ah`, and every call takes
`--cwd <the team's repo, absolute path>`, except `ah db check`, which takes no `--cwd`; see [cli-tools.md](./cli-tools.md).
`<AH_ROOT>` is on the session's `ah CLI root` line.

Notes for 2.0.0, coming from an earlier build:

- The hooks now run as a mod (`hooks/register.ts`). A running member keeps its old hooks until step 4. Under managed Claude Code
  with `allowManagedHooksOnly`, the hooks run only from a managed install.
- State files are copied into the database once by an import pass and are not read again. A team's progress switched off with an
  old `.off` file is on again: run `ah progress off <team>`.
- The QA job queue, usage history, recovery records, stall state and the watch timeline now live in the database, and their
  old files are not imported: they start empty after the upgrade.

## 1. Update the plugin

In Claude Code:

```
/plugin                # update wrangl
/reload-plugins
```

This only copies files into `~/.claude/plugins/cache/<marketplace>/wrangl/<version>/`.
It builds nothing and does not touch the running daemon.

`<marketplace>` is the name of the marketplace you installed Wrangl from. The
`/plugin` list shows it next to Wrangl (for example `wrangl@wrangl`). Copy the
name from the list rather than from this page.

## 2. Build and install the executable

You need the .NET 10 SDK on the machine, as for the first install.

```
sh ~/.claude/plugins/cache/<marketplace>/wrangl/<version>/scripts/install.sh --apply --rebuild-only
```

This builds `ahd` and `ahd-tui` into `~/.claude/ah2/bin`. It does not touch your
`PATH`, your shell rc files or the login item. If a daemon of a lower version is
running, or the installed binary changed, it restarts the daemon for you.

If the plugin's automatic rebuild is on (the install's default), the next
session start does this in the background. Running it yourself is quicker and
shows you any build error.

Run this after step 1, never before. If you run it first it fails with
`No such file or directory`, because the new version's directory is not in the
plugin cache yet. Update the plugin, then run it again. See
[troubleshooting.md](./troubleshooting.md#after-an-upgrade-a-resume-or-the-gate).

If you let ah add allow rules for the Orchestrator's lifecycle commands, they name the plugin root of one version. A full
`install.sh --apply` (without `--rebuild-only`) refreshes them for the new root and prints a line saying so, with the date
the rules were last set and how to revoke; the `--rebuild-only` command above leaves them alone, so the old rules stop matching until
you do. See [lifecycle-permissions.md](./lifecycle-permissions.md#when-you-upgrade).

## 3. Check the install

```
ah roster doctor
```

Read the rows. `daemon-contract` must not be red. If it is, run `ahd restart`
and check again. A red `daemon-contract` means the running daemon is an older
build than the hooks.

## 4. Move the running members onto the new hooks

A member session that was already running keeps the hooks it started with. To
put each member on the new hooks without losing its conversation, close the team
and bring it back into the same sessions. Do this from the session that is, or
should become, the team's Orchestrator.

```
ah roster snapshot --team T
ah roster disband --team T
```

`snapshot` records every member's launch settings and its session id.
`disband` on its own is a plan and closes nothing. It lists what would close and
gives the exact close command to run next, with a plan token. Claude Code asks
you to approve that close every time.

```
ah roster disband --close --confirm --plan-token <token> --team T
ah roster resume --team T
ah roster resume --team T --yes
```

The first `resume` is a dry run: it lists each member and what would happen to
it. With `--yes` each member is launched again from the directory it worked in,
into its own recorded session, so it keeps its conversation. Each member ends
as one of:

| status | meaning |
|---|---|
| `resumed` | relaunched into its recorded session |
| `fresh` | relaunched with no old conversation: no session id was recorded, or it is not a Claude session |
| `alive` | not launched: that session is already running |
| `no-worktree` | not launched: its directory is gone |
| `failed` | the launch failed; the other members still went ahead |

Things to know:

- **An implementor that works in the main checkout.** When the team
  holds a grants document, an implementor in the main checkout is refused
  (`main-checkout`). Pass `--allow-main` to `resume` for that case.
- **Mail is not lost.** Mail nobody had acknowledged stays in the mailboxes. A
  resumed session registers again at its start and picks it up.
- **A snapshot is also taken for you** when `disband --close` runs and when the
  Orchestrator's session ends owning a team. The newest 10 per team are kept.
- **If another live Orchestrator holds the team,** `resume` refuses (exit 2) and
  launches nothing. See [recovery.md](./recovery.md).
- **Run `resume --yes` inside the Orchestrator's own Claude session.** It finds
  that session from the process above the command and records it as the team's
  Orchestrator, so the owner-only verbs work in that
  same session straight away. From a plain terminal, or from a member's session,
  it refuses and launches nothing. See [recovery.md](./recovery.md#bring-the-members-back-resume).
  If the team was resumed by an older build, `resume --yes` recorded no start time for the
  session that ran it, and the owner-only verbs answer
  `this verb runs only through the orchestrator's session` there until you ended
  that session and started it again with `claude --resume <session id>` (or
  `claude --resume` and pick it from the list). That restart is still the way to
  repair such a team, if you would rather not run
  `resume --yes` once more. See
  [troubleshooting.md](./troubleshooting.md#after-an-upgrade-a-resume-or-the-gate).

## 5. Optional: arrange the panes

Under herdr, `ah roster move` puts a member's pane where you want it. A
relaunched team often lands in one tab.

```
ah roster move <member> --new-tab
ah roster move <member> --tab <tab id> --split right
```

`--split` takes `right` or `down` and is required with `--tab`. Add
`--dry-run` to print the herdr command without running it. To label a tab by
workstream, use herdr itself:

```
herdr tab rename <tab id> <label>
```

## Rolling back

After a plugin rollback the newer daemon keeps running, because older clients
never drain a newer daemon. Run `ah msg daemon stop`, then `ah msg daemon
status`, whose `root` shows which plugin the running daemon's code lives in.
See [daemon.md](./daemon.md).

## Renamed environment variables

Four variables that ah v1 also uses have new names, so a session can never pick up v1's values:

| Old name | New name |
|---|---|
| `AH_TEAM_FILE` | `WRANGL_TEAM_FILE` |
| `AGENT_HIERARCHY_DIR` | `WRANGL_HIERARCHY_DIR` |
| `AH_ROSTER` | `WRANGL_ROSTER` |
| `AH_EXPECTED_ROOT` | `WRANGL_EXPECTED_ROOT` |

The old names are ignored. If you set `AGENT_HIERARCHY_DIR` or `AH_ROSTER` yourself, set the new name instead. A team launched before the upgrade must be disbanded and created again.

## Renamed plugin id

The plugin and its marketplace are now called `wrangl`; they were `ah2`.

- Slash commands are `/wrangl:…` (for example `/wrangl:hierarchy`), and agent types are `wrangl:…` (for example
  `wrangl:implementor`). The old `/ah2:…` names no longer exist.
- Remove the old install first: `/plugin uninstall ah2@<its marketplace>`. Then run
  `/plugin marketplace add agentic-tool-labs/wrangl` and `/plugin install wrangl@wrangl`.
- The home `~/.claude/ah2` is unchanged: the database, roster, rules and settings stay where they are. The commands are
  still `ah` and `ahd`.
- Run `ah roster permissions sync` again. The allow rules name the plugin's install path, and that path changed.
- A roster or role that names an agent as `ah2:<name>` must be edited to `wrangl:<name>`.
