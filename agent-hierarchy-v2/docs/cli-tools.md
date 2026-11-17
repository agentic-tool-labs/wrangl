# ah and ahd command reference

This page lists the commands Wrangl gives you: `ah` for teams, members, mail and work, and `ahd` for the background
program (the daemon). The first part, [Common tasks](#common-tasks), is for you at a terminal and is grouped by what
you are trying to do. The second part, [Full reference](#full-reference), lists every verb and flag. It is written for
the agents and scripts that run most of these commands, and for anyone who wants the exact rules.

## Running the commands

- **Inside Claude Code**, `ah` and `ahd` are on the PATH of the Bash tool. Type a command after a leading `!` to run it
  yourself.
- **In a terminal**, `/wrangl:hierarchy install` links both commands from `~/.local/bin`. If the terminal says
  `ah: command not found`, add that folder to your `PATH`. `ahd where` shows which install the commands use.
- **Help:** `ah roster --help`, `ah msg --help`, `ah qa --help` and `ahd --help` print the full usage of each group.
  `ah` on its own prints the list of groups. The smaller groups print their usage line when you run them with
  `--help`.
- **Output:** `ah roster` and `ah msg` print JSON. Many commands take `--plain` for text, and `ah roster doctor` takes
  `--brief`.

Many team commands are **owner commands**: they run only from the Orchestrator's own Claude session, and a plain
terminal is refused. In the tables below, those say "ask the Orchestrator". Tell the Orchestrator what you want in plain
words, and it runs them.

## Common tasks

### Check and fix the install

| To | Run | Notes |
|---|---|---|
| check everything at once | `ah roster doctor --brief` | one line per check; changes nothing. Without `--brief` it prints JSON, and `--check` exits 1 when any row is red |
| see which install is in use | `ahd where` | |
| build or rebuild Wrangl's program | `/wrangl:hierarchy install` | inside Claude Code |
| back up Wrangl's database | `ah db backup` | `ah db list` lists the backups; the daemon also takes them by itself |
| check the database | `ah db check` | |
| restore the database | `ah db restore <name> --apply` | without `--apply` it only prints the plan. It refuses while the daemon is running |

### See what your teams are doing

| To | Run | Notes |
|---|---|---|
| list your teams | `ah roster teams` | |
| see each team's members and their work | `ah roster status --plain` | |
| open the web page | `ahd ui` | Ctrl-C closes it. `ahd ui start` keeps it up in the background until `ahd ui stop`. See [The web page](web-ui.md) |
| open the terminal view | `ahd tui` | read-only; `--team <name>` opens one team. See [The terminal view](tui.md) |
| see what is outstanding | `ah roster checklist show` | `--stale` for items with no recent activity |
| see how long each stage took | `ah roster timing` | |
| read back your prompts and the Orchestrator's replies | `ah msg history` | `--search <text>` finds a question or reply by its words; `--questions` keeps only the prompts that look like questions |
| see token use | `ah usage-report --team <name>` | `--fleet` totals every team; `ah usage` shows your rate-limit windows |

### Choose your team

| To | Run | Notes |
|---|---|---|
| add a member to the roster | `/wrangl:agent-roster add --role <role>` | inside Claude Code. Changing the roster starts nothing |
| see the roster | `ah roster show` | |
| list, copy or pick named rosters | `ah roster roster list` · `ah roster roster copy <from> <to>` · `ah roster roster use <name>` | see [Rosters](rosters.md#named-rosters) |
| add a role of your own | `/wrangl:agent-role` | see [Custom roles](custom-roles.md) |
| start the team | ask the Orchestrator: "set up my team" | the `/wrangl:agent-team` skill |

### Steer the work

| To | Run | Notes |
|---|---|---|
| see the team goal | `ah rules goal --team <name>` | |
| set the team goal | `ah rules goal --team <name> --text "<goal>" --done-when "<test>"` | or tell the Orchestrator. Goal at most 160 characters, done-when at most 120. `--clear` removes it. Guidance only |
| add a standing rule | `ah rules add --repo --key <key> --text "<rule>"` | the scope can also be `--team <name>`, `--project <work id>` or `--goal <work id>`. Text at most 300 characters |
| list the rules that apply | `ah rules list` | |
| pause one member | ask the Orchestrator (`ah roster pause-member <member>`) | stops nudges, re-deliveries and alarms for it; closes nothing. `resume-member` ends it |
| let a deputy run the team | ask the Orchestrator (`ah roster duty on <member>`) | `ah roster duty off` takes it back |
| give one session your rights | `ah admin grant <pid>` | in a plain terminal only. `ah admin revoke` ends it; `ah admin show` shows it |
| switch the member launch rules off or on | `ah allow off` · `ah allow on` | on by default. `ah allow list` shows the state |

### Stop, pause and recover

| To | Run | Notes |
|---|---|---|
| save everything before a planned restart | ask the Orchestrator (`ah roster checkpoint --team <name>`) | then `ah roster checkpoint --status --team <name>`: exit 0 means a restart is safe |
| put a team on hold | ask the Orchestrator (`ah roster quit --team <name>`) | each member saves its work and writes a resume plan; nothing is relaunched by itself. `pause` is the same command |
| bring a held team back | ask the Orchestrator (`ah roster resume --hold --team <name> --yes`) | without `--yes` it only prints the plan |
| stop everything now | ask the Orchestrator (`ah roster stop --now --team <name> --confirm`) | emergency stop: members stop their work, no session is killed. Without `--confirm` it only prints what it would do |
| bring the team back after a reboot | from a new Claude Code session in the repository: `ah roster resume --team <name> --yes` | without `--yes` it only prints the plan. See [Recovery](recovery.md) |
| take over a team from a dead session | ask the Orchestrator of the new session (`ah roster adopt --orchestrator-pid <its pid> --team <name>`) | refuses a team another live session holds |
| close one member | ask the Orchestrator (`ah roster dismiss <name>`) | closes a member with nothing unsaved at once, and refuses one with uncommitted or unmerged work |
| close the whole team | `/wrangl:agent-team disband` | shows a plan and asks you first; conversations are kept |
| mark a team closed, or open again | `ah roster close <team>` · `ah roster reopen <team>` | yours alone; deletes nothing. `--preview` shows what it would do |
| find or stop a runaway process | `ah proc list --match <text>` · `ah proc stop <pid>` | only processes from your session or your teams |

### The daemon

| To | Run | Notes |
|---|---|---|
| see whether it is running | `ahd status` | `--plain` for text |
| start, stop or restart it | `ahd start` · `ahd stop` · `ahd restart` | you rarely need to: it starts when needed |
| read its log | `ahd logs -n 50` | 1 to 1000 lines; never follows |
| replace a hung daemon | `ahd heal` | replaces it only once it has proved a hang. `--force` skips the limit of three restarts in 24 hours; run it yourself in a terminal |
| check versions | `ahd version` | |
| keep it running from login | `ahd login on` | `ahd login off`, `ahd login status` |

### herdr

| To | Run | Notes |
|---|---|---|
| add Wrangl to the herdr desktop app | `ah herdr install` | `--uninstall` removes it. See [herdr desktop](../herdr-desktop/README.md) |

## Full reference

The rest of this page is the exact reference: every verb, flag, output and refusal. Each command is a short entry:
what it does, its usage line, then its notes. It is written mostly for the agents that run these commands, so it uses
their form of the command.

> **Reading `<ah>`.** At a terminal, type `ah` wherever an entry says `<ah>`, and `ahd` where it says `<R>/bin/ahd`.
>
> **For agents:** `<ah>` is the `ah` command in your context's `ah CLI root` line; when two such lines disagree, the
> newest wins.

Contents of the reference:

- [Invocation rules](#invocation-rules) and [Permission](#permission)
- [Verbs](#verbs): [messages](#messages-ah-msg), [the QA queue](#the-qa-queue-ah-qa),
  [teams and members](#teams-and-members-ah-roster), [routing, tests and issue intake](#routing-tests-and-issue-intake)
- [Other `ah` commands](#other-ah-commands)
- [The daemon CLI (`ahd`)](#the-daemon-cli-ahd)
- [Machine load](#machine-load), [Self-care](#self-care-any-peer), [Team home and teardown](#team-home-and-teardown)

Every agent-hierarchy operation is a Bash call to the `ah` command this plugin ships. Most of the work goes
through two groups:

```
<ah> roster <verb> [args…] --cwd <absolute cwd>
<ah> msg    <verb> [args…] --cwd <absolute cwd>
```

The top-level verbs of `ah` are `roster` (teams and members), `msg` (messages and mail), `qa` (the QA job queue),
`progress` (a short form of `roster progress`), and the smaller ones described in
[Other `ah` commands](#other-ah-commands): `proc`, `ledger`, `admin`, `alarm`, `looks`, `backlog`, `work`, `adr`, `db`,
`allow`, `rules`, `test`, `usage`, `usage-report`, `view`, `herdr`, `issue-intake` and `hook`. (`gate` and
`push-guard` still exist but only print that they were removed.) `ah` has no top-level `--help`: run `ah roster`,
`ah msg` or `ah qa` with no verb, or with `--help`, for that group's usage. The daemon has its own command, `ahd`
(see [The daemon CLI](#the-daemon-cli-ahd)).

There is no MCP server: a CLI call either runs or prints the shim's own one-line error. For how the plugin and the
`ahd` daemon fit together, see [architecture.md](./architecture.md).

`<AH_ROOT>` is the plugin's installed root. Three independent channels publish it, because no one
of them covers every case:

1. **The skill, agent and command files write the command as `<ah>`**, which each defines as the `ah`
   command in the `ah CLI root` line of the reader's context (channel 2). The files carry no path of their own, so a rule
   on the stable link matches what a role types.
2. **SessionStart and UserPromptSubmit inject a root line**, written by
   one source of truth for both:

   ```text
   ah CLI root (v<version>): <AH_ROOT> — roster: `<ah> roster <verb> --cwd <abs cwd>`, messages: `<ah> msg <verb> --cwd <abs cwd>` (verbs: <AH_ROOT>/docs/cli-tools.md)
   ```

   SessionStart alone is not enough: `/reload-plugins` fires no SessionStart, so a session that
   updates mid-flight would never hear the new root. UserPromptSubmit repeats the line on every
   prompt, which covers that and survives compaction. Both use the same classification — nothing
   for subagents, nothing when the hierarchy is configured-and-disabled. The `(v…)` token is the
   plugin version that emitted the line: when two root lines in one context disagree, the most
   recent one wins.
3. **SubagentStart injects the same line into an Agent-tool subagent** — the only event that fires
   there, and it carries the line only inside its `hookSpecificOutput` envelope.
4. **Hook messages** that tell a role to run something spell the absolute command.

If all four somehow failed you, resolve the root — do not guess it, and never glob the cache dir
(several versions coexist there). Read `~/.claude/plugins/installed_plugins.json`: the key is
`wrangl@<marketplace>` and its value is an **array** of install records;
the root is the `installPath` of the first record. `ahd where` prints the plugin it found, too.

## Invocation rules

- `--cwd <absolute path>` on every verb. The CLIs resolve the hierarchy dir, the git root and the
  worktree/main-checkout relationship from it; a relative path or a missing one is a bug in the
  caller, not a default.
- One simple command. No `cd … &&` prefix, no `VAR=1` prefix, no pipe, no redirection, no `$VAR`
  and no command substitution — `--cwd` and `--orchestrator-pid` exist precisely so nothing needs
  shell expansion. The permission hook below recognises only this form.
- JSON arguments (`--args`, `--verified`, `--created`, `--geometry`, `--body`) travel **single-quoted**.
- `roster` and `msg` print JSON (`msg` prints text with `--plain` where a verb offers it); `qa`, `rules`, `usage`,
  `test plan` and `db` print plain text unless the row says `--json`. A non-zero exit means the
  JSON or the stderr line says why. Exit 3 is a *partial* result (`layout-splits`, `create`, `resume`): real work happened, not
  all of it — read the JSON.
- `ah roster`, `ah msg` and `ah qa` with `--help`, or with no verb at all, print that group's usage block and exit 0.
  `ah` itself with no verb or with `--help` exits 2.
- Identity: both CLIs resolve the orchestrator session pid from `--orchestrator-pid` if given,
  else `CLAUDE_PID`, which every Claude Code session exports into the Bash tool's environment.
  The owner verbs (`create`, `spawn-one`, `spawn-ad-hoc`, `dismiss`, `disband`, `untrack`, `reap`, `adopt`, `move`, `transfer`, `stream-open`,
  `stream-done`, `progress` writes, `close-approve`, `checkpoint`, `lane-caps`, `quit`/`pause` (not `--status`), `stop`, `duty`,
  `pause-member`, `resume-member`, and `resume`, which makes the same checks itself) do not take that claim on trust: they find the session from the process
  above the command (once per invocation), refuse a shell that carries the member marker or runs under a member, and refuse
  with `orchestrator-pid-mismatch` when `--orchestrator-pid` or `CLAUDE_PID` names another process. A verb that acts on a team
  that already exists also needs the proved session to be that team's Orchestrator (`not-team-owner`); `disband` of an
  orphaned team and `adopt` of a team whose holder is gone are the exceptions. Refusals exit 2 with `{status: "refused",
  refused, proof}` on stdout and an `owner proof:` line on stderr. `ah roster doctor` has an `owner-proof` row: `ok` when the
  process table can be read, `warn` when it cannot. It also has a `release-handshake` row, `ok` in a build whose
  `dismiss` and `disband --close` run the [Release check](#release-check);
  a binary without the row predates the check.
  The documented form omits the flag; tests, a human shell running a read verb, and `adopt` (which requires it) use it. A
  plain terminal cannot run an owner verb (`owner-unproven`), except `disband` of an orphaned team: run it from the
  Orchestrator's Claude session.

## Permission

The plugin ships a PreToolUse hook (`ah-hook pretooluse-ah-cli`) that returns `allow` for these
two scripts, so ah calls raise no Bash permission prompt. It allows only the grammar above, only
when the script's realpath is under the hook's own installed root or a sibling of it, and never for
a `dismiss`/`disband … --close` command, which gets the ordinary permission prompt. The roster skill
gate's one-per-session `deny` also outranks the `allow`.

Posture: this grants prompt-free execution of two scripts the plugin itself ships, with arguments
restricted to that grammar. The remaining state-changing verbs (`untrack --commit`, `reap
--commit`, `create --commit`, `remove`) are bookkeeping on ah's own state files and were
auto-allowed as MCP calls too.

With the plugin's hooks on, you add nothing to your settings.

If you run without plugin hooks and still want no prompt, allow the **exact path** of the `ah` you
installed. Do not use a leading wildcard before the program name: it matches any program named `ah` in
any directory, so a planted copy elsewhere would run with no prompt.

The path agents are told to type is the one in the `ah CLI root` line of their context. Once `install.sh` has linked it,
that is the stable link `$HOME/.local/bin/ah`, which leads to the installed plugin's `bin/ah` and is moved to each new
version by every install, so a rule on it survives upgrades. When the link is missing or leads somewhere else, the line
carries the plugin's own path, which `ahd where` prints in its `plugin.root` field (`<AH_ROOT>`). Add one rule per
script to your **user** settings (`~/.claude/settings.json`; user, not project, because the plugin lives under
`~/.claude/plugins/cache`):

```json
{
  "permissions": {
    "allow": [
      "Bash($HOME/.local/bin/ah roster:*)",
      "Bash($HOME/.local/bin/ah msg:*)"
    ]
  }
}
```

Write `$HOME` out in full, as the absolute path. In the plugin-root fallback the rule is `Bash(<AH_ROOT>/bin/ah roster:*)`, written out in full;
that path contains the plugin version, so **rewrite both rules after every plugin upgrade**, because a rule for the old
version no longer matches and the prompts come back.

Spell them exactly like that: the space after `ah` is what keeps an `ahx` binary from matching.

The Orchestrator's lifecycle commands take one rule per verb, spelled with the same path and no `*` before it:

```json
{
  "permissions": {
    "allow": [
      "Bash($HOME/.local/bin/ah roster disband:*)",
      "Bash($HOME/.local/bin/ah roster dismiss:*)",
      "Bash($HOME/.local/bin/ah roster resume:*)",
      "Bash($HOME/.local/bin/ah roster snapshot:*)",
      "Bash($HOME/.local/bin/ah roster spawn-one:*)",
      "Bash($HOME/.local/bin/ah roster stream-open:*)",
      "Bash($HOME/.local/bin/ah roster stream-done:*)",
      "Bash($HOME/.local/bin/ah roster progress:*)",
      "Bash($HOME/.local/bin/ah roster create:*)",
      "Bash($HOME/.local/bin/ah roster spawn-ad-hoc:*)",
      "Bash($HOME/.local/bin/ah roster adopt:*)",
      "Bash($HOME/.local/bin/ah roster untrack:*)",
      "Bash($HOME/.local/bin/ah roster stream-report:*)",
      "Bash($HOME/.local/bin/ah roster close-approve:*)",
      "Bash($HOME/.local/bin/ah roster checkpoint:*)",
      "Bash($HOME/.local/bin/ah roster wake:*)",
      "Bash($HOME/.local/bin/ah roster overdue:*)",
      "Bash($HOME/.local/bin/ah roster stall-diagnose:*)",
      "Bash($HOME/.local/bin/ah roster checklist:*)",
      "Bash($HOME/.local/bin/ah roster move:*)"
    ]
  }
}
```

In the plugin-root fallback each rule starts with `<AH_ROOT>/bin/ah` in place of `$HOME/.local/bin/ah`, and `install.sh --apply
--allow-lifecycle` has to be re-run after each upgrade; it writes these rules for the path in force (see
[Lifecycle permissions](#lifecycle-permissions)).

## Lifecycle permissions

For what the rules allow and do not allow, written for the person installing, see
[lifecycle-permissions.md](./lifecycle-permissions.md). This section is the reference for the command.

`install.sh --apply --allow-lifecycle` adds the twenty rules above to the `permissions.allow` list of
`${CLAUDE_CONFIG_DIR:-$HOME/.claude}/settings.json`, spelled with this install's plugin root.
`install.sh --apply --no-allow-lifecycle` removes the ones ah added. With neither flag, an install at a
terminal asks once (`[y/N]`, default no) when it can offer them; with `--quiet`, without a terminal, with
`--rebuild-only`, in a dry run or after a failed build it never asks and never writes. A "yes" given earlier is
refreshed for the new root on the next install.

`<ah> roster permissions <check|rules|add|sync|remove> [--cwd <abs cwd>]` does the work:

- `check` prints `ask` (offer them), `sync` (rules were added before; refresh them) or `skip: <reason>`. It writes nothing.
  `rules` prints the twenty rules exactly as `add` would write them, one per line; the installer shows that text to the user.
- `add` writes the rules; `sync` replaces the rules ah added for an older plugin root with the current ones and leaves a
  rule the user edited by hand; `remove` deletes only the rules listed in the ledger `<AH_HOME>/permissions.json`
  (`{v, added, plugin_root, at, answer}`). A rule the user already had is not recorded and is never removed.
- The file is rewritten as canonical JSON (key order and layout are not kept), after a copy to `settings.json.ah2-bak`. A
  settings file that is not strict JSON (comments, trailing commas) is refused and left byte for byte as it was.
- `add` and `sync` are offered and written only when `ah roster doctor` shows an `owner-proof` row `ok` with detail
  `ancestry` and a `release-handshake` row `ok`; a binary without the second row is never offered the rules.
- It refuses inside any agent session: `AH_SPAWN_DEPTH` set, `CLAUDECODE` or `CLAUDE_CODE_SESSION_ID` set (outside test
  mode), or a Claude Code harness among its parent processes. A process ancestry that cannot be read refuses too; run
  `install.sh --apply --allow-lifecycle` from a plain terminal, or add the rules by hand. The permission hook also denies a
  Bash command that runs it, to every session. This is not a security boundary: an agent that can run shell commands can
  edit `settings.json` itself. It stops an agent from using ah to grant itself rules; the boundary is Claude Code's own
  permission prompt.
- `add` and `sync` also refuse unless the plugin root is one this plugin's installed-plugins record lists, under the plugin
  cache directory; the root, `bin`, `bin/ah` and every directory up to and including the cache directory are owned like
  your user and not writable by group or others; the root is outside any git work tree; and its real path has none of
  `*`, `(`, `)` or a newline. A failed check names the path and writes nothing. A plugin installed from a local directory
  (outside the plugin cache) is refused, so it cannot use the rules. The rule carries the real path of the root; when the
  configured root is a link, `add` and `sync` report a warning and the `lifecycle-permissions` doctor row warns that the rules
  do not match the CLI path the Orchestrator is told to run. The install record is read from
  `${CLAUDE_CONFIG_DIR:-$HOME/.claude}/plugins/installed_plugins.json`, by this verb and by the `install` doctor row alike.
  `remove` is not gated by these. The rule trusts the integrity of the plugin cache directory: anything that can write
  the plugin's `bin/ah`, or the stable link to it, gets the twenty commands approved without a prompt.
- A `settings.json` that is a link is written through the link, in its target's directory, and stays a link; a link to
  nothing is refused.

`ah roster doctor` has a `lifecycle-permissions` row. It is `ok` (`not asked`, or `answer no`) when the rules were never
added, and `ok` (`N rule(s) present`) when every rule in the ledger is in the settings file. It is `warn` when the ledger
or settings file cannot be read, when the ledger names another plugin root than this version's, when a ledger rule is
missing from the settings file, or when a rule for this version is present that ah did not add (left alone).

An allow rule only skips the permission prompt. Members and subagents are still refused by ah itself (owner proof) for every
owner verb among them. `snapshot` only saves a team snapshot file. A user who never opted in gets none of the twenty
rules and the normal permission prompt for every verb; an earlier yes is refreshed by `sync` for the rules it
recorded, and any rule added since (the newest are `wake`, `overdue`, `stall-diagnose`, `checklist` and `move`) is
written by `add` (`install.sh --apply --allow-lifecycle`).

## Reading the roster

To inspect the roster run `<ah> roster show --cwd <abs cwd>`; never read
`.claude/ah2/agent-hierarchy.json` directly — it misses the worktree/main-checkout and global fallback
resolution that `show` implements.

Write verbs refuse (exit 2) a level file that exists but isn't a JSON object, an empty file
included, and leave it untouched — fix or delete it, then re-run. `untrack` and
`dismiss --also-config` instead warn, leave the file, and finish. Read verbs ignore such a file, as
before.

## After an update, mid-session

`/reload-plugins` fires no SessionStart, so nothing re-announces the root at update time — but the
UserPromptSubmit hook re-states the `ah CLI root:` line on your next prompt, and after a reload that
line comes from the NEW version dir. Take the most recent one in context. Until then the old CLI
keeps running from the old version dir (they coexist under the cache) — stale code, still working.
If the old dir is gone, the shell says `No such file or directory` for the old `ah` path; send another prompt and
read the fresh root line, or resolve it with the recipe above.

## Verbs

`--cwd <abs>` is required on every command below and left out of the usage lines. `<R>` = `<AH_ROOT>`.

### Messages: `ah msg`

#### New message file

```
<ah> msg new --to <role> --from <role> --slug <s> [--title <T>] [--to-name <n>] [--from-name <n>] [--parent <id>] [--reason context|second-opinion|parallel] [--eta small|medium|large] [--type request|response] [--id <id>] [--team <t>] [--req <abs request path>] [--body-file <path>|-]
```

`--item <id>` (a request from the Orchestrator or a lane lead) links the request to one open checklist item (`<stream>/<id>` when the id is in several streams); an unknown, ambiguous, done or dropped item exits 2 and writes nothing, except that an item a ship closed is reopened, with the note `reopened by request <id> after ship <sha8>`.

- The request carries the canonical `item: <stream>/<id>`, the item is owned by the addressee and dispatched, and the request id is added to its refs.
- A response, a paired status message or `msg close` then finds the item and sets it waiting-orch ("answer <member>: <kind>", the kind being the leading STATUS, NEEDS-DECISION, BLOCKED or QUESTION of the response, read again at `msg fill`).
- `--body-file` writes that text (or standard input for `-`) under the frontmatter in the same call, in place of the skeleton: at most 64 KiB, UTF-8, no line that is exactly `---`, and more than the skeleton (a blank or skeleton body exits 2 and writes nothing).
- The frontmatter is the same as without it, a body without a trailing newline gets one, and repeating the call returns the same file (`existing: true`) while a different body is a new request.
- Pass `--to-name` when the brief is for a specific member.
- The UI's per-member view relies on it (the member name exactly as in the team file).
- `--title` (request only) is the exchange's human title: a few plain English words, verb first, naming the outcome (`Make UI slick`, `Fix engram backup appetite`; an issue number may follow in brackets), at most 60 characters (`title-too-long`), at least two tokens with one plain word (letters only: no digits, hyphens, slashes or `=`), never only a slug, id, hash or path (`title-not-readable`), no control or line-break characters, no ` #`, no `---`, not the word `null`.
- It is required when `--from` is `orchestrator` or `sub-orchestrator` (exit 2 without it).
- A request without one is shown under its goal line or first heading, and `ah msg list` prints the title as its last column.

#### Answer a brief (response)

`<ah> msg fill --id <id> --from <your role> --body-file - --cwd <abs cwd>`, then `<ah> msg send <orchestrator address> --kind report --ref <id> --cwd <abs cwd>`; final message `[hierarchy-msg <id> response]` + one status bullet

`--from` is your own role, not the request's `from`; `--to` is derived from the request. A DONE records HEAD only from a clean tree (untracked files count); from a dirty one, name the work with `--sha <commit>`.

#### Partial report (one per deliverable)

`<ah> msg fill --id <id> --part --from <your role> --body-file - --cwd <abs cwd>`, then `<ah> msg send <orchestrator address> --kind report --ref <id> --cwd <abs cwd>` as for any report

Appends the text to the response under `## part <n> · <local ISO time>` and sets `parts: <n>` and `partial: true` in its frontmatter; every earlier part is kept in order. The request stays open (`ah msg list` shows `open (<n> parts)` and a `parts` field), and the sent part counts as the peer speaking for its check-in and silence alarms.

A plain `msg fill` afterwards is the final report: it is appended under `## final · <time>`, drops `partial` and closes the request as any report does; `msg show <id> response` prints every part. A `--part` after a final report is appended too and leaves the request closed. Ignored with `--request`.

#### Answer a note (response file)

```
<ah> msg new --type response --note <note entry id> --from <your role> --cwd <abs cwd>
```

A note has no request file, so this makes the response for the note entry itself. The note is found only in your own boxes (the ones `ah msg inbox` reads) and must be `kind: note`; otherwise `no note <id> in your inbox`.

The file is in `<hier>/msgs/` and is named like any response file, with the note's entry id as its id, the sender's role as its role part and `note` as its slug; its `id` = the note's entry id, `to`/`to_name` = the note's sender, `slug: note`, and the other keys as for any response; an existing msgs file with that id → `id already used by <path>`, nothing written.

Fill it with the Write tool (or `ah msg fill`), then send it with `ah msg send <sender address> --kind report --ref <that path>`: because the file's `id` is a note in your inbox, the sent entry carries `reply_to: <note id>`. `--note` replaces `--id` and `--req`.

This is the way to answer a note from a self-care shell: free `--text` stays capped at 1000 characters and a quoted `--text` stays refused, and the refusal names this form.

#### Write a response body

`<ah> msg new --type response … --body-file <path>|-` writes it in the call that creates the response; otherwise `<ah> msg fill --id <id> --from <your role> [--req <abs request path>] (--body '<JSON string>' | --body-file <path>|-) --cwd <abs cwd>`.

A long report goes in `--body-file` (plain text, same size limit as `--body`), which keeps it off the command line; replaces everything after the frontmatter of the response `new --type response` made beside that request (`fill` never creates one), keeping the frontmatter byte for byte; `--from` must equal the response's `from`.

The body is one JSON string: `\n` for a newline and `\u0027` for a single quote, since a literal `'` cannot sit inside the single quotes; at most 256 KiB, and no line that is exactly `---`. A body without a trailing newline gets one, and one that does not start with an empty line gets an empty line after the frontmatter, as `new`'s skeleton has.

The permission hook runs `fill` without a prompt only for a session whose own role (the one its hook payload names) equals `--from`; a session with no known role, or another role's `--from`, is denied. Repeatable: each run puts the new body first and keeps the earlier one below it. Prints `{id, path, bytes}` (`bytes` is the size of the file written).

Any role may use it for a response whose `from` it names; the Architect and other self-care roles do not (their shell refuses it).

#### List exchanges

```
<ah> msg list [--open|--closed|--all] [--to <role>] [--team <t>] [--slug <s> [--count]] [--plain]
```

`--slug` keeps that slug's exchanges; with `--count` it prints only `{"slug":…,"count":N}`, every exchange of the slug open or closed (a `dec-` slug counts 0). lists the exchanges of every message pool the team uses, not only the one under the current directory.

When the team has more than one pool (the main checkout and a linked work tree), each row also shows the pool it is in: `pool=main`, or `pool=<work tree name>`; `--json` rows carry the same as `pool`. With a single pool there is no pool column.

#### Downstream dispatches

```
<ah> msg downstream [--root-name <n>]
```

#### Index one message

`<ah> msg index <id>`; read a section with `<ah> msg show <id> [response] --section <n>`

#### Where one entry stands

```
<ah> msg status <entry id> [--json] [--cwd <abs cwd>]
```

Read-only. One line: `<entry> <kind> <from> → <to>: <state> [by <who> at <t> via <pull|relay|paste|prompt-hook|stop-hook|ack>] relay needed: yes|no`; `--json` adds `delivered`, `acked`, `superseded_by`, `declined`, `claim` (the relay claim's state, or null) and `relay_reason`.

A claim that is `relay-sent`, `relay-unconfirmed`, `fallback-claimed` or `delivered` reads as `delivered`; one that is `relay-claimed` or `relay-failed` reads as `pending` (a time is printed only when the record has one). `state` is the first that holds of `declined`, `superseded`, `acked`, `delivered`, `pending` (the ladder has started) and `routed`.

`relay needed` is yes while a relay was asked for and `RelayVoid.Check` finds nothing that voids it. An entry outside the boxes this session is registered as, its sender, and the teams it orchestrates, and an id that does not exist, print <!-- ah:not-cmd -->`ah msg status: unknown entry <id>` and exit 2. Any role may run it.

#### Message roster line

```
<ah> msg roster [--team <t>]
```

#### Send to a mailbox

```
<ah> msg send <name@team|name> (--ref <id> | --text <s> | --event <name>) [--kind brief|report|note|event] [--reply-to <entry id>] [--supersedes <entry id>] [--eta <dur>] [--id <entry id>] [--from <address>] [--session <id>] [--json]
```

One durable entry per call. `from` is your own address (`user` if you have none); an Orchestrator that owns several teams has one address per team, and `--from` picks one when the recipient is in a team it does not own. Run it after filling a brief or report.

A relative `--ref` is resolved against `--cwd` (else the current directory) and is then held to the same rule as an absolute one: the file must exist and sit in a hierarchy `msgs/` directory.

A report whose file is unchanged since the same sender delivered it to the same recipient (acked or not) is not sent again: exit 0, `entry <id> → <address> (already delivered; unchanged)` with the existing id, and no new entry; an amended file is a new entry.

`delivery: pull (<adapter>)` → send nothing else; `delivery: fallback — SendMessage "…" to <name>` → do exactly that (the text ends `[ah-entry <id> to <address>]`; keep it, it is how the mailbox learns the entry was carried); `delivery: fallback — ah roster deliver <name>` → run it. Never both. Errors: `error: <code>: <message>` (`unknown_address`, `ambiguous_address`, `bad_address`, `bad_request`), nothing written.

The recipient may also be `<role>@<team>` when no member of that team has that name and exactly one member has that role (a member named like the role wins, and `orchestrator@<team>` keeps its meaning); no role match, or several, is `unknown_address` and its message lists the team's member addresses.

A delivered `--kind report` is also appended, after delivery, to the sender's stream journal under `progress/<team>/` (see [progress.md](./progress.md)); a journal failure leaves one stderr line and a `progress-journal` hook-error row and never changes the send.

`--supersedes <entry id>` replaces an unacked entry of yours in the same box: the old entry's delivery ladder ends with a `superseded` row naming the new one, and a relay already asked for it is shown as no longer needed. It is refused (exit 2, nothing sent) when the entry is unknown, another sender's, in another box, or already picked up or ended.

#### List a mailbox

```
<ah> msg inbox [--address <name@team>] [--all] [--overdue] [--session <id>] [--json]
```

Your unacked entries, oldest first (all of an Orchestrator's addresses when it has several); does not ack. Each entry not from `ahd` ends with its delivery-ladder state, `(rung <n> <state>…)`; `--json` adds `ladder`.

#### Take your next message (Orchestrator)

```
<ah> msg next [--team <t>] [--peek] [--again] [--all]
```

Shows one waiting message at a time. This is what the Orchestrator's doorbell line ("You have N messages. Run `ah msg next`") asks for. `--peek` looks without taking it, `--again` shows the last one again, `--all` shows everything waiting.

#### Ack mailbox entries and filed responses

```
<ah> msg ack <entry id>... [--hold] [--session <id>] [--json]
```

Acking a report is accepting it and moves its checklist item; `--hold` acknowledges with no move. an unknown id is reported and exits non-zero; the others are still acked.

An id that is a request id this session dispatched acknowledges the response filed to it, which clears it from the Orchestrator's "responses filed to you, not yet acknowledged" list (see [comms-protocol.md](./comms-protocol.md#responses-filed-to-you-and-not-yet-acknowledged)). Output: `acked <id>` / `unknown <id>` per id, or with `--json` `{"acked": [...], "unknown": [...]}`.

#### Decline a relay

```
<ah> msg decline <entry id> --reason already-reported|superseded|withdrawn [--note <text>] [--session <id>] [--json]
```

Only the carrier named on the entry's relay request (the Orchestrator, or the sending member when it carries its own relay) may: it ends that relay with a `declined` ladder row, so the relay never re-fires and is not escalated. It does not ack the entry, which stays unread for its recipient.

Exit 2, nothing written, when the caller is not the carrier, the entry has no relay request or is already picked up or ended, the reason is not one of the three, or `--note` is over 200 characters. The carrier's next Stop line says what was recorded.

#### Withdraw or re-address a brief

```
<ah> msg withdraw <entry id> --reason <text> [--to <name@team|name>] [--from <address>] [--json]
```

Only the brief's sender (proven the way `send` proves it) may.

When `<entry id>` is not a mailbox entry, the id names an exchange (a request file, found in any pool `msg list` searches) and the verb withdraws that instead: the caller must be the request's sender (`not_your_session` otherwise, as for `new --type response`), `--to` and `--from` are refused, and a response marked `withdrawn: true` with the reason (body `WITHDRAWN by <sender>: <reason>`) is written beside the request, so the exchange reads as closed in `msg list`, `list:open` and the Stop-hook nudges (the exchange slice carries `withdrawn: true`); an exchange that already has a response is refused, and an id found in neither place is refused naming both.

For a mailbox entry it appends a terminal `withdrawn` ladder row (above an ack, so a later ack leaves the state `withdrawn`), sends the addressee a `note` that names the reason, and ends the addressee's Stop-hook obligation, relay and check-in/peer-silent clock for that request. It works after the brief was read; a report the addressee still sends is accepted as usual.

With `--to` it then mails the same request to that member (same role as the request, else `role_mismatch`) and prints the new send's relay text; the request file is never edited. The output always carries `progress` (`response_drafted`, `in_turn`, `delivered`, `not_delivered`), the ack, and the response file when one exists; the verb never blocks on it.

Exit 1, nothing written: `unknown`, `not_sender`, `bad_request` (not a brief), `already_closed` (withdrawn, superseded or declined), `already_reported`, `role_mismatch`. If the re-address send fails after the row is written, `readdress_failed` names the withdrawn entry. `ah msg list --open` hides a request whose every brief is withdrawn (`--all` shows it as `withdrawn`).

#### Forward an entry with a note

```
<ah> msg forward <entry id> --to <addr> [--note <text> | --note-ref <path>] [--json]
```

An orchestrate-class session (top, lane lead, manager) passes on an entry of its own box: one new entry with the original's kind, `ref`, `text` and `event`, plus `fwd: {of, origin, hops: [{by, at, note | note_ref}]}`, then acks the original for itself. The recipient's mail prompt shows the hop lines newest first, then the original.

The send goes through the mesh rules like any send, so a cross-team forward is allowed only along their edges (`outside_team` / `not_linked` otherwise). Refused: `forward_not_orchestrator` (a member: members reply, not forward), `not_yours` (the entry is not in the caller's box), `forward_cycle` (the target is the origin or already a hop), `forward_hops` (more than 4 hops).

#### Close a request nobody can answer

```
<ah> msg close <request id> --reason superseded|abandoned [--note <text>] [--team <name>] [--json]
```

Only the Orchestrator of the request's team may (`not_your_session` otherwise; a request that names no team needs `--team <team>`, and the proof is then made in that team; without it, usage error, exit 2). The response records `closed_by` (`orchestrator@<team>` plus the caller's session) and `closed_reason`.

It writes a response marked `closed: true` where a reply to the request would go, with the reason and note, so the request reads as closed in `msg list`, `list:open`, the web UI and the Stop-hook check-ins and silence clocks, and works on any open request file, mailed or briefed by SendMessage.

Closing twice, or after a withdrawal, is a no-op (`already: true`); a request that already has a report is refused with `already_reported`. Exit 2 for usage errors; exit 1 for `unknown`. The web UI and `ah roster teams` count only live members as agents, and a request whose addressee is not a live member is stale: counted apart, not in flight.

#### Release a sent relay

```
<ah> msg release <entry id> [--session <id>] [--json]
```

Hands a claimed or sent single-carrier relay to the plugin prompt (a message already queued would then show twice); the receiver's own boxes or the team Orchestrator; anything else is refused.

#### The `ahd` daemon

```
<ah> msg daemon start|status|stop [--json]
```

`start` finds or starts the per-user daemon and prints `ahd <version> pid <pid>`; `status` prints `down` or version, pid, uptime, plugin root, what it owns, live registrations with adapter freshness, the overdue count and a `ladder:` line (entries pending, sending, paused or escalated on the delivery ladder), and never starts it; `stop` drains it (`stopped` or `not running`, exit 0 either way).

`send`, `inbox` and `ack` start and use the daemon when they can and fall back to the files, with the same output and exit code either way. `AH_DAEMON=off` ignores the daemon entirely. After a plugin **rollback** the newer daemon keeps running (older clients never drain a newer daemon), so run `daemon stop`; `status` shows the `root` the running code lives in.

The daemon also keeps the dispatch clock for every live registered Orchestrator session that has an `orchestrator@<team>` box (listed in `owns.timers`; `status` shows each one's last tick and last event; among live `claude` registrations sharing a pid only the newest counts): it mails the findings as `event` entries to that session (`report-landed`, `check-in-due`, `peer-silent`, `member-blocked`, `resume`), and the Orchestrator's Stop hook hands over any event mail left unacked past the pull timeout.

`AHD_WATCH_POLL_MS` (and `AH_WATCH_POLL_MS`) lower the 15 s poll for tests. `GET /v1/status` serves `status.json` live (see `docs/status-file.md`).

#### Ask for a peer link

```
<ah> msg link-request --to <name@team>[,<name@team>] --task <request id> --why <text> [--direction both|out] [--messages N] [--minutes M]
```

Links you to teammate(s) for one task at once (no approval step); the request and its granted response stay on record for status and the UI. Prints the request id; the result's `link` is the link id, or null when the task is closed or the pair already has a run-scoped link. Under the default `star` mesh policy a member's direct send to a teammate is refused without a link.

Members assigned together (one work stream, or open requests the Orchestrator dispatched on one item or slug) need no request: their first `ah msg send` opens the link, which does not count toward the two requested links a member may hold; `link-request` is for a teammate they are not assigned with, and for a review loop.

#### Decide and manage links

`<ah> msg link grant <request id> [--messages N] [--minutes M]`, `link deny <request id> --why <text>`, `link revoke <link id>`, `link extend <request id> [--messages N] [--minutes M]`

Orchestrator only.

#### Ask for more on a link

```
<ah> msg link extend-request <link id> --why <text> [--messages N] [--minutes M]
```

A peer of the link; one request may be pending per link.

#### Read links

`<ah> msg link list`; `link show <link id> [--tail N] [--since N] [--wait S]` (its mirrored messages, `--wait` long-polls up to 25 s); `link post <link id> --text <s>` (Orchestrator only: one message to every peer, not counted)

#### Decide a join

`<ah> msg join accept <join id> --basis user-asked|user-approved|rejoin --why <text> [--name <n>] [--role <r>]`, `join deny <join id> --basis user-declined|no-answer|orchestrator --why <text>`

Orchestrator only.

A join (`POST /v1/join`) is accepted at once with basis `immediate` and answers `status: accepted`; these verbs decide only a join that stayed `pending` because that accept failed; `--basis` and `--why` are required and recorded (a rejoin is checked by the code and writes no row); the first decision wins, a second gets `already_decided`; vet before deciding: `docs/comms-protocol.md` § Vetting a join request.

#### Sweep closed exchanges

```
<ah> msg sweep [--days 7]
```

#### Log a /wrangl:pipeline decision

`<ah> msg decision add --team <t> [--input <path>]` with one JSON object in the `--input` file, else on stdin. The skill always writes `<hier>/pipeline/<anchor id>/decision-input.json` with the Write tool and passes it as `--input`: a pipe or heredoc never runs prompt-free.

The input file must be a regular file of at most 64 KiB whose real path is inside that run directory, holding one JSON object; it is never deleted. Then it appends one line to the run's log, `<hier>/pipeline/<anchor id>/decisions.jsonl`, and prints `{id, path, counts}`. The run is the team's one open `pipeline-run-anchor` exchange; none or several → exit 2.

Fields: `kind` (`decided`|`parked`), `item` (a slug or `run`), `source`, `question`, `options` (2–4 strings, lettered a–d), `default` (a letter or null), `choice` (a letter within `options` or `other: …`; null when parked), `decider` (`{role, name, model}` and no other key, `role` matching `^[a-z0-9-]+$`; null only on a parked line), `rationale`, `revert` (strings), `review`, `dangerous` (booleans), `why_user` (`dangerous`|`unsure`|`cap`; null when decided), `exchange` (a request id or null).

The writer adds `id` (`d<k>`, counting only decided, parked and answered lines), `time`, `run`, `decision` (the chosen option's text) and `qkey` (first 12 hex of sha256 of the item and the trimmed, lowercased, whitespace-collapsed question).

- Refused, exit 2 with `{refused, reason, detail}` on stdout and nothing written: a missing, mistyped or unknown field, or any writer-owned field supplied.
- `kind: merge`.
- `kind: answered` (invalid; use `decision answer`).
- A decided line that is dangerous, has the user as decider, has a `why_user`, chooses off the list, or answers `other: …` without `review: true`.
- An `--input` file outside the run directory, not a regular file, over 64 KiB or not one JSON object.
- A parked line with a choice or no `why_user`.
- `dangerous: true` with a `why_user` other than `dangerous` (all reason `invalid`).
- A decided line for a question already parked in the run (`parked-before`) or already decided (`decided-before`, naming the earlier `d<k>`).
- A decided line over 4 for its item or 20 for the run (`cap`).

#### Log an operational call the Orchestrator made itself

`<ah> msg decision add --team <t>` with `{class: "operational", o: "O1".."O10", question, decision, rationale, revert, decider: {role: "orchestrator", self: true}[, item, source]}`

Appends a `kind: "operational"` line (`o<n>`), outside the caps and never parked; refused with `why_user`, `dangerous` or an R/D class. The summary counts it as `operational`.

#### Record the user's answer to a parked decision

```
<ah> msg decision answer <id> --choice <a|b|c|d|other:<text>> [--team <t>]
```

Appends an `answered` line to the open run's log (`{kind: "answered", answers: <parked id>, choice, decision, answered_by: "user", recorded_by: <the caller's address>, item, question, qkey, options}`) and prints `{id, path, counts}` with `answered` and `waiting` in `counts.run`. A `decided` line is never the user's, so this is a separate kind and the `decided` rules are unchanged.

Refused, exit 2 with `{refused, reason, detail}`: an id that names no parked line (`unknown-id`), a parked line that already has an answer or a decided line (`already-closed`, naming the closing `d<k>`), a choice outside the line's options, an `answered_by` other than `user` (`invalid`).

**Who may run it:** the verb proves its caller itself, before it reads the decision log, from this process's own ancestry (the classifier the `rules` route uses).

Only the user (a terminal, no harness anywhere above the process) or the Orchestrator of the run's team, recording the user's answer, may write the line; a sub-Orchestrator, a member, another team's Orchestrator, a member row of the run's own team, and any caller the ancestry cannot prove are refused with exit 3, `{refused, reason}` on standard output and `ah: only the user, or the run's Orchestrator recording the user's answer, may answer decision <id> (<code>)` on standard error, with the codes `sub-orchestrator`, `member`, `not-the-run-orchestrator` and `unproven`, and nothing written.

The line carries `caller` (`user` or `top-orchestrator`) and `recorded_by` (`user`, or the Orchestrator's address). The Orchestrator may add `--surrogate <D1..D5>` when the run anchor's grant is a confirmed `surrogate`: the line then reads `answered_by: "surrogate"` with `surrogate_class`, otherwise it is refused `surrogate-not-allowed: class|authority`.

A decision parked with `dangerous: true` is answered by the user only: the Orchestrator, with or without `--surrogate`, is refused `dangerous-needs-user` and the refusal prints the user's own command (`ah msg decision answer <id> --choice <letter> --team <team> --cwd <dir>`).

Every attempt, allowed or refused, appends one row to `state/decision-audit.jsonl` under the canonical home, which is `<passwd home>/.claude/ah2` (`HOME` and `AH_HOME` are never read for it), before the outcome is acted on; a row that cannot be written refuses the attempt (`audit-unwritable`).

When the environment's `AH_HOME` is not that canonical home the verb refuses `home-mismatch` (exit 3) before it classifies the caller, with one row (`env_home` set) in the canonical audit, so a user with a custom `AH_HOME` cannot run `decision answer` or `decision add` until the override floor guards ship; a passwd entry or default home that cannot be resolved refuses `home-unresolved`.

A parked line without a boolean `dangerous` (a legacy line) counts as dangerous and needs the user's own command. `decision add` is proved the same way (`action: "add"` in the audit), and every line it writes carries `caller`. The hook layer keeps its text match as an early, friendlier refusal and for a subagent of the Orchestrator, which shares its parent's process ancestry.

**Residual:** a same-user process can defeat an ancestry test on purpose (for example by detaching and allocating a terminal), so the check stops accidental misuse and gate bypasses, not a deliberate local caller with the user's uid; `ah` still cannot prove what the user said when the Orchestrator records an answer, so `recorded_by` and `caller` say who relayed it.

`decision add` takes one JSON object whose `kind` is `"parked"` or `"decided"` and refuses everything else (a missing `kind`, a list, null, `answered`) with exit 2 (`invalid`); only this verb writes the line. Line ids count `answered` lines, so in a run that has answers the later `d<k>` ids differ from the ones a log without answers would number.

#### Park a question for the user

```
<ah> msg decision park --header <h> --question <q> --option <label> --option <label>... --default <what you assume until it is answered> [--for <name@team>] [--class blocker|soon|eventually]
```

A team's Orchestrator only. `--class` (default `soon`) is stored on the `q<k>` line.

`--for` must name a member of the question's own team (a row of its team record, live or not), else it is refused and nothing is written; it records `asked_for` on the `q<k>` line, and its answer's `question-answered` event then also goes to that member (re-checked at answer time; a member no longer on the team, or a failed send, is a note).

`ah roster checklist set <id> --state waiting-user --for <name@team>` does the same for an item, and is refused on any other state. Writes `q<k>` to the parked-question store, without holding the turn as AskUserQuestion does, and prints `{id, parked, next}`; `next` holds the push to send.

The user answers with `<ah> msg decision answer q<k> "<label>"` or in their next typed message, which the Orchestrator relays; the web Inbox does not list `q<k>` questions and cannot answer them.

#### Answer as the web form does, from a GUI

```
<ah> msg decision ui-answer --json '<body>'
```

The web form's answer route for a GUI caller (the Wrangl Herdr mod). `<body>` is byte for byte what the web posts to `POST /ui/v1/answer`: `{dir: <dirId>, team, run, id, choice | choices}` for a run decision, `{dir, team, item, since, choice}` for a waiting item, or either with `dismiss: <reason>` in place of a choice.

It runs the route's own body table and the bridge's answer core, so the line, the refusals and the `question-answered` event are the web's; the stamp reads `caller: "herdr"` (`answered_by: "user"`, `recorded_by: "ui"`). `dir` resolves among the dirs `ah msg decision answer` proves against (this one, the known dirs, and those the live registrations name).

Stdout is the route's reply body: exit 0 `{ok, id, answers, decision, event_id | event_error}`; exit 2 the route's 400 `{error: "bad-request", detail}` or 409 `{refused: true, reason, detail}`; exit 1 an internal error, one line on stderr. No ahd is needed.

**Who may run it:** the user, as the classifier proves for `decision answer` (a terminal, no harness on the line), or a process whose direct parent is `herdr-app` with no harness anywhere on its line. Anyone else, an agent in a Herdr terminal included, gets exit 3 `{refused: true, reason: "unproven", detail}` and nothing written. The residual of `decision answer` applies.

#### Draft or accept a standing decision rule

`<ah> rules propose <scope> --key K --text T --applies A --choose C [--said W]`: any lead may run it. It writes a decision rule that is proposed, not accepted. `ah rules list` shows it as `proposed decision`. It is never injected and never usable by `--rule`. `<ah> rules accept K [<scope>]` is the user's alone (exit 3 for anyone else); with no scope it takes the one decision rule of that key.

The web rules dialog accepts a rule through `POST /ui/v1/rules` `op: "accept"`. `ah rules edit … [--text] [--applies] [--choose]` on a decision rule makes it proposed again. `off` and `remove` revoke it. A rule never expires. Accepted rules are injected under "Your standing decisions", after the guide rules and within the 4000-character block.

#### Apply one of the user's accepted rules

`<ah> msg decision add --team <t> --rule K --json '<decided object>'`. It logs the line with `decider: {role: "user-rule", rule: K}`; a null `choice` becomes `other: <the rule's choose>` and needs no `review`. With no run it goes to the team log. `--summary` counts it under `by_rule` and `rules`, not `decided`, and `--line` says "answered by your rules".

It is refused, exit 2, with nothing logged: `rule-not-accepted` (proposed), `unknown-rule` (no decision rule of that key applies to the caller), `rule-not-for-dangerous` (`dangerous: true`), and `invalid` for a `rule` decider without `--rule`.

#### List what waits on the user

```
<ah> msg decision list --waiting [--team <t>] [--json]
```

Run or no run: the open run's parked, unanswered decisions (`d<k>`), the team's waiting-user items that no open decision names (the same fold as the web list), and the open parked questions (`q<k>`; the web list leaves these out). One line each: kind, id, question or note, the lettered options with the default starred, age, and the command that answers it.

`--json` prints `{waiting: [{kind: "decision"|"item"|"question", id, text, options, default?, since, answer_command}], total}`. Nothing waiting: `nothing waits on you` (`total: 0`), exit 0. Without `--waiting`, `decision list` is unchanged.

#### Answer a waiting-user item

```
<ah> msg decision answer <stream>:<item> (--choice <a|b|c|d|other:<text>> | "<label>") [--since <the item's state time>] [--team <t>]
```

The same write as the web form: the answer, the move back to the item's earlier state and an `answer user: <item>` todo, then one `question-answered` event to `orchestrator@<team>`; prints `{id, answers, decision, recorded_by: "cli"}`. A label is matched to an option (case-insensitive); an item with no options takes the label as `other:` text. The caller is proved as for `decision answer d<k>` (exit 3 otherwise).

Refused, exit 2, nothing written: `unknown-item`, `not-waiting`, `question-changed` (only with a stale `--since`), `unmatched_label`, `bad_request` (a letter beyond the options). An id with `:` is an item; `d<k>` and `q<k>` never hold one.

#### Read a /wrangl:pipeline decision log

```
<ah> msg decision list --team <t> [--run <anchor id>] [--item <slug>] [--summary]
```

The open run's log, or with `--run` that run's, open or closed: `{run, path, decisions, skipped}` in file order. Every valid line is listed with its `kind`; a line that won't parse (a torn last line) is skipped and counted.

`--summary` prints `{decided, flagged, parked, by_item, skipped, line}` instead, `line` reading e.g. `decisions: 7 decided (2 flagged), 1 waiting for you`; once a parked decision has been answered the summary also holds `answered` and `waiting` (parked lines with no answer) and `line` reads e.g. `decisions: 7 decided (2 flagged), 0 waiting for you, 1 answered`; lines of other kinds count nowhere.

No open run and no `--run`, or an unknown `--run`, → exit 2.

### The QA queue: `ah qa`

#### Queue a QA run

```
<ah> qa submit --repo <abs> --sha <40hex> --set <name> [--base <sha>] [--priority gate|lane|adhoc] [--fresh] [--session <id>] [--json]
```

Prints `entry <id>`. The requester is your proven address, never a flag; `--session` only picks among your own sessions. `--repo` must be the team's repository or one of its worktrees. The same commit and set already queued or running adds you as a requester and returns that entry. A stored PASS or FLAKY-PASS gives an entry that is already `done` (`--fresh` skips it). Starts the daemon if it is down.

#### Claim a QA run

```
<ah> qa next [--max-set full|other] [--session <id>] [--json]
```

QA engineers. Claims the first admissible entry and prints `entry <id>` then `repo`, `sha`, `base` (if set), `set`, `fresh` and `priority` lines; prints nothing when none is admissible (`--json` gives `reason`). `other` never claims a `full` set.

#### End a QA run

```
<ah> qa done <id> (--result <path> | --error <one line>) [--session <id>] [--json]
```

The claimer only. The result must be a file directly inside the stored-result directory of that commit and set (`$HOME/.claude/ah2/qa-results/<sha>/<set>/`, or under the daemon's `AH_RESULTS_DIR` when that is under that root or `/private/tmp/ahprobe.*`; symbolic links resolved), and its `status` becomes the verdict.

An error line fails the entry (use `qa-unknown-set: <set>` for a set `tests/run-set.sh` does not know). Prints `entry <id> done|failed`.

#### Give a QA claim back

```
<ah> qa release <id> [--session <id>] [--json]
```

The claimer, the orchestrator of the claimer's team, or the user. Prints `entry <id> queued`.

#### Look at the QA queue

`<ah> qa status [<id>] [--json]`, `<ah> qa list [--all] [--json]`

One entry's fields, or one line per entry: `<id> <state> <priority> <set> <sha7> requesters=<n> claimer=<addr|-> age=<n>s|m|h`. `list` hides `done` and `failed` unless `--all`. `--json` prints the daemon's answer on one line.

#### QA exit codes

`ah qa` exits 0 on success (including an empty `next` or `list`), 1 when the daemon refuses or is down (stderr `error: <code>: <message>`; codes `qa-caller-unproven`, `qa-bad-request`, `qa-bad-result`, `qa-unknown-entry`, `qa-not-claimed`, `not-claimer`, `not-allowed`, `qa-store-busy`, `daemon-unavailable`), 2 on a usage error.

### Teams and members: `ah roster`

#### Show roster

```
<ah> roster show [global|repo|repo-user] [--level <L>] [--roster <r>]
```

Members are shown under the default team name; when a team could not use that name, a `team_name_note` says so.

#### Show a team's record

`<ah> roster team show [--team <name>] [--json]` prints the team's record (its team file: `members[]` with each `name` and `role`), indented, or on one line with `--json`. Reads only.

#### List teams

`<ah> roster teams [--json] [--orchestrator-pid <pid>]`. A row's `life` is `closed` for a closed team, whatever its owner's process is doing, else `owned-live`, `orphaned` or `indeterminate`. The top-level object carries the same `sources` block as `disband`; `untracked_live` rows from `herdr agent list` alone are marked `source: "herdr"`.

An orphaned team's row adds `live_members: [{name, role, last_brief_from}]`, its records that are live (one whose liveness cannot be decided is listed with `indeterminate: true`), and `attributed_live: [{name, role, pane_id, last_brief_from}]`, its `untracked_live` rows: live sessions that claim it through their team file or a `<t>-` Herdr name and match no record (never for the legacy `team.json`: an untagged row and the repo-basename prefix are no claim on it).

Each is absent when empty. A team read from a legacy worktree copy carries `legacy_dir: <that pool>`, and a team recorded in both the home and a worktree pool carries a warning (a team split across home and worktree); the misplaced check uses each member's own `expected_root`.

Every row carries `idle_since: {<member name>: <ISO instant or null>}`, one key per member of the team record: the member's activity record's `state_since` while its state is idle, null when it is working or blocked, has no session id, no record, an unreadable record or none to read. `last_brief_from` is best-effort: the `from_name` (else `from`) of that session's latest peer-pending row, else null.

Each team row's `members` counts the agents: members that are live or whose liveness cannot be ruled out (indeterminate); a dead row is not an agent. `member_rows` is the number of rows in the team file, dead ones included.

#### List teams

`<ah> roster teams [--json] [--orchestrator-pid <pid>] [--members]`. `--members` adds `member_status: [{name, session_id, pane_id, verdict, why}]` to each row: one answer per member, where `verdict` is `live`, `dead`, `unknown`, or what blocks a live member (`blocked-on-job`, `waiting-on-slot`).

The top-level object carries the same `sources` block as `disband`; `untracked_live` rows from `herdr agent list` alone are marked `source: "herdr"`.

An orphaned team's row adds `live_members: [{name, role, last_brief_from}]`, its records that are live (one whose liveness cannot be decided is listed with `indeterminate: true`), and `attributed_live: [{name, role, pane_id, last_brief_from}]`, its `untracked_live` rows: live sessions that claim it through their team file or a `<t>-` Herdr name and match no record (never for the legacy `team.json`: an untagged row and the repo-basename prefix are no claim on it).

Each is absent when empty. A team read from a legacy worktree copy carries `legacy_dir: <that pool>`, and a team recorded in both the home and a worktree pool carries a warning (a team split across home and worktree); the misplaced check uses each member's own `expected_root`.

Every row carries `idle_since: {<member name>: <ISO instant or null>}`, one key per member of the team record: the member's activity record's `state_since` while its state is idle, null when it is working or blocked, has no session id, no record, an unreadable record or none to read. `last_brief_from` is best-effort: the `from_name` (else `from`) of that session's latest peer-pending row, else null.

Each team row's `members` counts the agents: members that are live or whose liveness cannot be ruled out (indeterminate); a dead row is not an agent. `member_rows` is the number of rows in the team file, dead ones included.

#### Hierarchy status document

```
<ah> roster status [--plain] [--now <ISO>]
```

Computes the status document ([status-file.md](status-file.md)), writes `<hier>/status.json` atomically and prints it as JSON. `--plain` prints `ah · <text>` for the timeline entry current now, or an empty line when that entry is not visible. `--now` evaluates as of that instant (tests). With no hierarchy dir it prints a document with `teams: []`, writes nothing and exits 0.

#### Init roster level

```
<ah> roster init [level] [--level <L>] --route peer|pane [--roster <r>]
```

`--route subagent` exits 2: only legwork roles run as subagents, so route an individual legwork member instead.

#### Add roster member

```
<ah> roster add [level] [--level <L>] [--roster <r>] --role <R> [--model <M>] [--effort <E>] [--route peer|subagent|pane] [--kind <K>] [--args '<json>'] [--auto-mode <A>] [--adapter appserver|off] [--on-missing auto]
```

`--route subagent` only for a legwork role (else exit 2); `--on-missing` is `auto`, its default and only value; a model and an effort are stored only when given, so a member added without `--model` is asked for one when it is spawned.

#### Edit roster member

```
<ah> roster edit [level] [--level <L>] [--roster <r>] --member <name> [--role <R>] [--model <M>] [--effort <E>] [--route …] [--kind <K>] [--args '<json>'] [--auto-mode <A>] [--adapter appserver|off] [--on-missing …]
```

`--model ""`, `--effort ""` and `--adapter ""` clear the field; `--adapter off` opts a Codex member out of the app-server adapter, which is on by default once a Codex version has been checked; `--adapter` on another kind is exit 2.

#### Remove roster member

```
<ah> roster remove [level] [--level <L>] [--roster <r>] --member <name>
```

#### List named rosters

```
<ah> roster roster list [--json]
```

Read-only. The `selection`, then the default block and every `rosters.<name>` key visible from cwd, each with the level and path that win (the first with members, else the most specific), the levels it is `shadowed` at, its member count and route, whether it is `selected`, and the `teams` whose file records it (this checkout's hierarchy dir and, from a worktree, the main checkout's).

A legacy `rosters.default` block is listed with a `note`: it can't be selected or copied. Human text without `--json`.

#### Copy a roster

```
<ah> roster roster copy <src> <dst> [--level <L>] [--dry-run]
```

Copies `src` (`default` = the unnamed block) to `rosters.<dst>`, `route` and `members` as stored, at `--level` (default: the level `src` resolves at). Refuses an undefined `src`, a `dst` that is `default` or not a valid name, and a `dst` already defined at that level — in any file at that level, so from a worktree the main checkout's counts too; the refusal names that file.

This is how a roster is made from another; `init --roster` makes an empty one.

#### Delete a roster

```
<ah> roster roster delete <name> [--level <L>] [--dry-run]
```

Removes the `rosters.<name>` key at `--level` (default: the level it resolves at). Refuses `default`, a name any team file here or in the main checkout records (a legacy team keyed by its own name counts; disband or reap it first), and a name `activeRoster` in the same file selects. Warns, and deletes, when `activeRoster` at another level selects it.

From a worktree, a block only the main checkout's file holds at that level is refused, naming that file and the `--cwd <main checkout>` to run it with.

#### Select a roster

```
<ah> roster roster use <name>|default [--level <L>]
… roster use --clear [--level <L>]
```

Writes (or removes) `activeRoster`, at `repo-user` by default inside a git repo, else `global`. Refuses a name no visible level defines, `default` aside. Warns, and still writes, when the target level's readers can't see the roster: `--level global` when the global file doesn't define it (other repos), `--level repo` when only repo-user or global does (others in the repo); `repo-user` never warns.

`--clear` deletes a file left with no keys. Prints the resulting `selection`.

#### Create a team

One call: `<ah> roster create --yes [--team <t>] [--roster <r>] [--mode …] --transport <t> --roster-level <L>` plans, launches and records the team, printing `{plan, spawn, commit}` (a failed launch is left out of the commit) · plan: `<ah> roster create [--plan] [--team <t>] [--roster <r>] [--mode auto|columns|grid] [--roster-level <L>]` · spawn: `… create --spawn [--team <t>] [--roster <r>] [--mode auto|columns|grid] [--roster-level <L>]` · commit: `… create --commit --verified '<json>' --transport <t> --roster-level <L> [--team <t>] [--roster <r>] [--mode auto|columns|grid] [--partial] [--session <orchestrator session id>] [--orchestrator-pid <pid>]` · from history: `… create --from <id|alias> [--team <t>] [--mode …] [--plan|--commit|--spawn]`.

`--team` is the team's name (default: the repo basename, or a legacy `team.json`'s own prefix); `--roster <r>` builds it from `rosters.<r>` (default: the `roster` block) and a missing block is an error; a `rosters.<t>` block matching `--team <t>` without `--roster` is only warned about (`warnings`).

`--mode` defaults to the global `teamLayout` (`~/.claude/ah2/agent-hierarchy.json`), else `auto`; a plan reports `layout: {mode, source}` with source `explicit` | `stored` | `default`, and an explicit `--mode` on `--spawn`/`--commit` is stored as `teamLayout` for future teams (`--plan` stores nothing). The committed team file records `roster` (the block key, `null` for the default) and `layout`.

A roster-driven plan lists `named_rosters` (the valid `rosters.*` keys at every level, sorted) when there are any. Config keys that no longer do anything.

`teamAlias`, a roster block's `layout`, a `teamLayout` outside the global file — are reported in every phase's `warnings` (and by `doctor` and `/wrangl:hierarchy status`), never in session context. A name that cannot be used gets the `team-name-unusable` refusal below.

Every phase also takes `--member-model <name>=<model>` (repeatable: the model that member runs on this time, never written to the roster); an unknown or repeated name, a non-claude member, or a model its class does not allow exits 2.

A plan lists `members_needing_model` (last) when a launched member has no model — entries shaped as in the `member-model-undefined` refusal below — and never refuses on models; `--spawn` refuses.

A claude-kind legwork member with no model is skipped while task-gopher is installed (per `installed_plugins.json`): it is not launched, listed or recorded, never makes the team `partial`, and every phase reports it in `skipped_members: [{name, role, handoff: "task-gopher:task-gopher"}]` with a `message` — its legwork goes to task-gopher subagents.

`--no-legwork-handoff` (every phase) counts task-gopher as not installed, so such a member is listed for a model like any other; a driver that cannot dispatch `task-gopher:task-gopher` passes it. Every phase also takes `--names-in-use <name>` (repeatable): a live session name the driver saw in `ListAgents`, `[ref]` stripped — the CLI reads no session names of its own.

A member whose name exactly matches one is renamed to the first free `<team>-<role>-<n>` and listed in `renamed_members: [{name, role, renamed_from}]` (present only when a member was renamed); `--commit` re-derives the renames from its own `--names-in-use` and records `renamed_from` on each renamed member.

`--partial` is accepted and does nothing: whether a team is partial is derived when it is shown (a pane-route roster member `create` would launch that has no record, by name or `renamed_from`) and never stored. `--commit` also creates `progress/<team>/rollup.md` and `main.md` (output `progress.created`).

#### Adopt an orphaned team

```
<ah> roster adopt --orchestrator-pid <pid> [--team <t>]
```

#### Give a member another folder

There is no verb. `<ah> roster allow`, `revoke` and `gate` were removed: they print a notice, exit 0 and change nothing, so do not run them. A brief names the directories a member works in; to add one for a Claude member, send a new brief. A Codex member's writable directories are fixed at launch: have the work done in its own tree, or give the path at launch.

#### Trust a folder for Codex

`<ah> roster codex-trust [<dir>] [--yes] [--json] [--cwd <abs cwd>]` · pre-approve: `… codex-trust --preapprove <dir> [--parent] [--via install|bootstrap|cli] [--yes]` · `… codex-trust --revoke <dir>` · `… codex-trust --revoke-all [--yes]` · `… codex-trust --list`.

The one writer of Codex's trust for a directory (default: the cwd). Without `--yes` it prints the plan (`result: "plan"`, `next`) and writes nothing; with `--yes` it backs the Codex config up as `<config>.ah2-bak`, edits only the table of the directory's trust root, writes the file atomically, re-reads it the way a launch does and puts the backup back when that does not say trusted.

`--preapprove` records a scope in the user's pre-approval store (`--parent` approves every repository under the folder, refused for `/`; a home-folder scope adds a warning), so a Codex trust screen inside it is answered without asking; `--revoke` removes the scopes naming a folder, `--revoke-all` deletes the store (plan first, `--yes` to do it), `--list` prints the trust state of the directory, the scopes and the auto-answer tail.

The modes are mutually exclusive; `--parent` and `--via` belong to `--preapprove`. A member session is refused (`refused: "not-from-member"`): only the user or an Orchestrator grants trust.

#### Allow a pipeline run on the default branch

`<ah> roster pipeline-branch [show]` · `allow <branch>` · `revoke [<branch>]` `[--cwd <abs cwd>]`: prints `{repo, branches}`: the branches you have said a pipeline run may work on directly, per repository (home document `home.pipeline-branches` of the state database; work trees share their repo's record). A value in `git config ah.pipelineBranch` from an earlier build is taken in once and unset.

It refuses nothing: the pipeline bootstrap reads it and asks you when the branch is not listed.

#### Reap orphaned records

`<ah> roster reap [--commit]`. The plan's `orphans` carry `live_members` and `attributed_live` as in `teams`. `--commit` clears an orphan only when it has neither; any other is kept and listed in `kept: [{team, live_members?, attributed_live?, next, why}]` (present only when non-empty).

With `live_members`, `next` is the `adopt` command for the invoking pid (null when none resolves); with only `attributed_live`, `next` is null.

Adopting would relaunch its dead records beside the live sessions. A team a user closed is left alone and listed in `skipped` with `why: closed`; a retired one (`closed.by: "retired"`) is treated as open, until a user's `close` replaces its marker.

#### Close or reopen a team

`<ah> roster close <team> [--preview] [--hold] [--confirm-live <member>]...`, `<ah> roster reopen <team>`

The user only: a session gets 403 `user_only` from `POST /v1/team/close` ([daemon-api.md](./daemon-api.md)). Close writes a `closed` marker on the team record and each confirmed lane child and deletes nothing. A member running now ends only when named with `--confirm-live`; otherwise exit 3, no marker, and the reply carries the fresh table.

An Orchestrator session is never ended, and while any member other than the Orchestrator has a live verdict the team reads as usual. `--hold` pauses first and closes nothing: the daemon runs `quit` for the team, a few seconds per call, and prints `phase` and `waiting_on`; run it again until `sealed`, then close.

The daemon writes the same marker with `by: "retired"` on a team that has stopped working; `reopen` wins until the team does new work, and it also works on a lane. Exit 0 done; 3 refused (409); 2 anything else.

#### Herdr layout phase

```
<ah> roster layout-splits --mode <m> --pane-count <n> [--self <id>] [--next --created '<json>'] [--apply --target <pane id> --direction right|down]
```

Exit 3 = partial, read the JSON.

#### Disband a team

plan: `<ah> roster disband [--plan] [--team <t>]` · close: `… disband --close [--team <t>]` (`--confirm`, `--plan-token <tok>` and `--allow-global` are accepted no-ops). Every plan and close result carries `sources`

Which of team file / peers.jsonl / `herdr agent list` was consulted, what each yielded, and the name prefix searched under. A plan also carries `next`: the exact close command to run once the user agrees (absolute path, `--confirm --plan-token`, plus `--team`/`--cwd` as given and `--allow-global` only when the close would demand it) — copy it, assemble nothing.

A close result adds `pruned: [name…]` (a nameless row appears as its role), `kept: [{name, why}]` (`why` ∈ `added` | `close-failed` | `live` | `indeterminate`), `team_removed`, and `team_file` (`null` when no team file was involved): `--close` reconciles the team file itself, so no bookkeeping call follows it.

Top-level `closed` is true iff every target is **verified gone** on a fresh re-query (herdr `agent list`, or tmux `list-panes`, polled every 250 ms for up to 3 s) and `not_closable` is empty; a close command that did not throw is no longer enough.

A close result adds `closed_count`, `still_live: [name…]` and `strays`; a plan adds `expected` (the size of `close`), `strays`, `not_closable: [{name, reason: "no pane"}]` (a `terminal`-transport member, or one with no pane id), `team_files: [path…]` (every team file read) and `warnings: [string]`. See [Team home and teardown](#team-home-and-teardown). `--close` with `closed: false` still exits 0: read the JSON.

The resolved team file, when it exists but does not read as a team record (unparseable, or parsing to something with no `members` array), is reported as `team_file_unreadable: <path>` and is never written or removed. A plan always carries `next`, even when no member has a pane: running it then closes nothing and still reconciles the record.

Before it closes anything, `--close` runs the [release check](#release-check) on every member it would close: members it holds are not closed, are listed under `held` (`[{member, status, detail}]`) and in `release` (`statuses`, `closed`, `summary`), and make the result exit **3**; the members that may close are closed as before.

`--ignore-release <name>`, once per member, closes that held member too, with status `ignored` in `release.warnings`. Stream tabs: a stream's recorded herdr tab is closed only when every pane in it is one ah made (a current member's or the stream's `root_pane_id`) and none runs a harness; an unreadable pane list or process table counts as unsafe.

The close summary adds `tabs_closed` (tab ids) and `tabs_kept` (`[{stream, tab, reason, pane}]`, reason one of `panes unknown`, `foreign pane`, `harness running`), each only when not empty — `disband` considers every stream, open or done.

#### Resync member locations

```
<ah> roster resync [--dry-run] [--team <t>] [--bind <b>]
```

#### Move a member's pane

`<ah> roster move <name> --tab <t> [--split right|down]` · `… move <name> --new-tab [--workspace <w>]` · `… move <name> --new-workspace` · all take `[--dry-run] [--allow-global] [--team <t>]` (`--allow-global` is an accepted no-op)

#### Team history

```
<ah> roster history
```

#### Spawn one roster member

```
<ah> roster spawn-one <role> [--member <name>] [--model <M>] [--stream <s>] [--dry-run] [--allow-global] [--allow-main] [--replace-live] [--team <t>] [--orchestrator-pid <pid>] [--names-in-use <name>]
```

- `--stream` joins stream `<s>` (see the stream rows below).
- `--model` launches the member on `M` this time only (a member with no model stored needs it: `member-model-undefined` below).
- `--names-in-use` renames as `create` does, across the role's roster members: each first takes its team record (one renamed away from its derived name, then one under it), a record found not live whose name is in use is renamed in place, and `--member` matches the final names.
- Not skill-gated.
- `--allow-global` is an accepted no-op — under Herdr the member name must be `[a-z][a-z0-9_-]`, at most 32 characters.
- A longer one is refused before any pane opens `--replace-live` spawns into a work root whose recorded member is live or of unknown liveness.
- It is refused (exit 2, `refused: live-member`) otherwise.

#### Spawn an ad hoc member

`<ah> roster spawn-ad-hoc <role> [--model <M>] [--effort <E>] [--kind <K>] [--harness <name> --command <cmd>] [--route peer|pane] [--args '<json>'] [--auto-mode <A>] [--on-missing …] [--stream <s>] [--dry-run] [--allow-global] [--allow-main] [--replace-live] [--team <t>] [--orchestrator-pid <pid>] [--names-in-use <name>]` (`--role <role>` is accepted in place of the positional; `--names-in-use` renames the derived name as `create` does; `--stream` joins stream `<s>`, see below).

- No roster needed.
- Not skill-gated.
- Global roster ignored (`--allow-global` accepted, no-op).
- `--kind custom --harness <name>` admits an agent runtime ah has no adapter for as a `route: mail` member (see `docs/participation.md`), and `--command` also starts it in a new tmux window with `AH_HOME` and `AH_ADDRESS` set — on herdr or a plain terminal `--command` is refused and the user starts the harness themselves — under Herdr the member name must be `[a-z][a-z0-9_-]`, at most 32 characters.
- A longer one is refused before any pane opens `--replace-live` spawns into a work root whose recorded member is live or of unknown liveness.
- It is refused (exit 2, `refused: live-member`) otherwise.

Under herdr, `spawn-one` and `spawn-ad-hoc` take `--here` (split the caller's own pane: right when it is at least twice as wide as tall, else down) or `--tab <t>` (split that tab's first pane), each with an optional `--split right|down`, and every spawn prints `next: {name, terminal, tab, brief}` where `brief` is the `ah msg new --to-name` line to brief it, so spawning and placing a member is one call.

#### Open a stream

```
<ah> roster stream-open <name> [--branch <b>] [--base <ref>] [--no-worktree] [--dry-run] [--team <t>] [--orchestrator-pid <pid>]
```

Records `streams.<name>` in the team file (creating the team, owned as `spawn-ad-hoc`'s would be, when there is none) and spawns nothing.

Unless `--no-worktree`, adds the worktree `<main checkout>/.claude/worktrees/<name>` on branch `--branch` (default `<name>`) off `--base` (default: the branch checked out in the caller's own work tree (a lane's worktree, not the main checkout), else on a detached HEAD the commit checked out there; `origin/HEAD` is not consulted; recorded as that branch or commit, never as `HEAD`; a `--base` you give is tried as given, then as `origin/<base>`, and the first that resolves wins, else the verb refuses with `base-not-found`, and a value starting with `-` with `bad-ref`; the stream record keeps `base` and `base_sha`, the full commit id the base pointed at when the work tree was made, which stays as recorded on a re-run (a different `--base` then only warns, and `base_sha` is `null` for `--no-worktree` or when the branch already existed)), reusing it when it is already that worktree; makes no tab: under herdr the first member that joins with `--stream` creates it (see the join row below).

Re-running is idempotent (`existed: true`); on a done stream it re-opens it (`reopened: true`). Output: `{opened, existed, reopened, stream: {name, state, branch, base, base_sha, worktree, tab_id, opened}, label, worktree_created, branch_created, transport, degraded?, warnings?, next}` — `next` is the two spawn commands.

Re-opening a stream whose record still holds a tab re-checks that tab; if herdr cannot make it, the worktree is kept, `tab_id` becomes `null` and a warning is given. `stream-open` does not retry the tab: the next `spawn-one`/`spawn-ad-hoc` with `--stream` creates it. Outside herdr there is never a tab: `degraded: "no-tabs"`.

`--dry-run` prints `would_run: {git, herdr}` and the `stream` it would record, and executes and writes nothing.

Refusals (exit 2, `ok: false`): `stream-name-invalid` (the name must match `^[a-z][a-z0-9_-]{0,39}$`), `worktree-path-occupied`, `branch-in-use` (checked out in another worktree; `detail` is git's stderr), `git-failed` (`detail`); nothing is written on any of them Also creates `progress/<team>/<name>.md` when absent and records `opened_at` (UTC, once).

#### Work trees under the worktree gate

When the team holds a grants document (`<hierarchy dir>/grants/<team>.json`), `spawn-one`, `spawn-ad-hoc` and `create --commit` record each launched member's work tree in it (the stream's work tree for `--stream`, else the git top level of the launch directory), and `dismiss --close` and `disband --close` drop the record of each member they remove.

`create --commit` writes the team's grants document, in `enforce`, when none exists. A team with no document is left alone and is not judged by the gate.

While the document is in mode `log` or `enforce`, an `implementor` whose work tree would be the repository's main checkout is refused with `main-checkout` before anything launches; `--allow-main` (on `spawn-one`, `spawn-ad-hoc`, `create --spawn` and `resume`) lifts that.

- Each member also gets a private tmp, `<team home>/tmp/<team>/<member>/` (mode 0700; a record made before the team component keeps `<team home>/tmp/<member>/` until the member is dismissed).
- It is made with or without a grants document, and when git does not already ignore the path, ah appends it to the repository's local `info/exclude` (never your `.gitignore`).
- Its short alias `<ah home>/t/<key>` is exported as `TMPDIR` for Claude members (a Codex member's tmp is the adapter's own directory).

The record keeps `tmp: {root, alias, kind}`.

#### Join a stream

`spawn-one <role> --stream <s>` / `spawn-ad-hoc <role> --stream <s>`

Without `--stream` both verbs are unchanged. Under herdr the pane goes into the stream's tab: the first member launches into the tab's own pane, later ones split from the earliest-joined stream member in that tab, with the team's layout mode. A stream with no live tab gets one first: that is where a stream's tab is made, so the first join creates it.

Joins into one stream are serialized by a per-stream lock (`<ah home>/run/stream-<16 hex>.lock`); a spawn that cannot get it within 120 s is refused with `stream-tab-busy` (`stream <name>: another spawn into this stream holds its tab lock — nothing was launched`), and the same spawn can be run again once the other has finished.

The member row gets `stream: "<s>"`, and the tab's id as `tab_id` when the launch reports none; the peer's cwd stays the team's `expected_root`, never the worktree, unless the team holds a worktree-grants document in mode `log` or `enforce`: then the member launches in the stream's work tree (refused with `stream-worktree-missing` when it is gone) and that tree is its recorded `expected_root`.

Output adds `stream: {name, worktree, branch, label?, warnings?}`. Refusals, before any pane opens: `unknown-stream`, `stream-done` (`next`: the `stream-open` that re-opens it), `stream-tab-unavailable`.

#### Open a sub-orchestrator's team

```
<ah> roster create --parent <X> --stream <ws> [--plan|--commit|--spawn] [--team <t>]
```

Run by the sub-orchestrator of stream `<ws>` of manager team `<X>`. The team is named `<X>-<ws>` unless `--team` names it, records `parent: {team: X, stream: ws}`, a copy of the stream, a `workstream` record and a progress checklist, and takes the gate mode of `<X>`'s grants document (`enforce` when `<X>` has none). Without `--parent`, `create` is unchanged.

#### Spawn a sub-orchestrator

```
<ah> roster spawn-one sub-orchestrator --stream <ws> [--team <X>]
```

The member is named `<X>-so-<ws>` (never truncated; the name must match `^[a-z][a-z0-9_-]{0,31}$`), at most one live sub-orchestrator per stream and at most `max_lanes` live per team (default 6, set with `ah roster lane-caps --max-lanes N`; see [team-file.md](team-file.md)), and none from inside a child team.

Refused before any pane opens, with the code `stream-required`, `sub-orchestrator-depth`, `sub-orchestrator-name`, `sub-orchestrator-exists` or `sub-orchestrator-breadth`.

#### Report a workstream

```
<ah> roster stream-report [<lane>] --state working|blocked|review|paused|done --summary "<one line>" [--note "<why>"] [--result <abs path to a --response.md>] [--team <t>]
```

Run by the owner of a child team (or, for `paused` and for leaving `paused`, by the owner of its manager team, who names the child team, or its own team and the lane, or leaves out both when that team has one open lane; otherwise the refusal lists the open lanes); replaces `workstream` in its team file and writes nothing else.

`paused` needs `--note` (refused `paused-needs-note`) and records `paused_by`; a paused lane raises no lane escalation and its charter no check-in until a report with another state. `--result` goes only with `done` and names an existing response file.

#### Set a lane's caps

```
<ah> roster spawn-one sub-orchestrator --stream <ws> --lane-members N --lane-streams M
```

N and M are whole numbers from 1 to 16 (defaults 4 and 8), written to `streams.<ws>.lane` of the manager team in the same write as `sub_orchestrator`; refused as `lane-cap-invalid` outside the range, as `lane-flag-role` for any other role, and as `lane-base-missing` when stream `<ws>` has no recorded base. A lane spawned before this record existed runs on the defaults.

#### Cap the live sub-orchestrators

```
<ah> roster lane-caps --max-lanes N [--team <t>]
```

The top Orchestrator only (not carved). `N` is a whole number from 1 to 16 (default 6), written to the team record's `max_lanes`; anything else is refused `max-lanes-range` and writes nothing; with `--stream` it is refused `max-lanes-is-team-wide`.

Bare `lane-caps` prints `max_lanes`, `max_lanes_default` and `max_lanes_source` (`team`, `roster <block>`, `roster (default)` for the unnamed block, or `default`) with each lane's caps. The cap is the team record's `max_lanes`, else the team's roster block `max_lanes` (read live; an out-of-range block value is skipped and `doctor` warns), else 6; the spawn refusal names the same source.

Lowering it below the live count dismisses no one; it refuses the next sub-orchestrator spawn (`sub-orchestrator-breadth`).

#### Run a lane's streams

A sub-orchestrator runs `<ah> roster stream-open <name> --team <X>-<ws>` (carved, like `spawn-one` and `move`, to its own child team): `<name>` and any `--branch` must start `<ws>-` (`lane-name`), `--base` may only be the lane base, and omitting it uses the lane base (`lane-base-fixed`), and at most `max_streams` child streams may be open (`lane-streams-full`).

Its `spawn-one`/`spawn-ad-hoc` into the child team stop at `max_members` live members (`lane-members-full`). It may also run `stream-done` on its own child team's streams; the manager team's streams stay with the top Orchestrator.

#### Reassign a lane member

```
<ah> roster move <name> --stream <s> --team <X>-<ws>
```

`move` is an owner verb: the top Orchestrator moves members of its own team, a sub-orchestrator moves members of its own child team. A running member whose session is in another tree is refused (`relaunch-needed`): dismiss it and spawn it again into the stream.

A sub-orchestrator may not pass `--replace-live` (`lane-replace-live`) or a pane destination (`--tab`, `--workspace`, `--new-workspace`, `--allow-global`: `lane-pane-destination`).

#### Staff a lane's child team from its parent

```
<ah> roster spawn-one|spawn-ad-hoc|dismiss|move … --team <X>-<ws>
```

Run by the owner of the manager team `<X>` (an unmarked Orchestrator shell). Allowed only for those four verbs and only on a confirmed lane child of the current incarnation of `<X>` (see `parent.team_id`); any other owner verb on the child is `not-team-owner` with text naming the lane, its lead and the four verbs, and anyone else gets today's refusal.

The lane rules apply as they do to the lead (`role-not-for-lanes`, `lane-closed`, `lane-members-full`, `max_members`), and a spawn under a lead whose process is gone is refused `lane-lead-gone` (`dismiss` and `move` stay allowed).

Rows written carry `placed_by`; each action is appended to the child's `parent_actions`, the child's `orchestrator{}` is never written, and removing the last member keeps the child's file with `members: []`. After the write, one `parent-action` event goes to `<lead>@<X>`; if it cannot be delivered the verb still exits 0 and stderr says `note: lane lead … not notified` (the journal holds the action).

`--dry-run` writes and sends nothing. `--kind custom` is refused to the parent owner there as to the lead (`lane-custom-kind`), and a spawn whose launch fails journals `spawn-rollback` after `spawn`, keeps the child's file and sends no note.

#### What a lane lead is told for an owner verb it may not run

The hook refuses a declared sub-orchestrator that names an owner verb outside the carved list with `ah: a lane lead may run only <carved verbs> with --team <its child team>; <verb> stays with the top Orchestrator: ask orchestrator@<manager>.` (the verbs are read from the carved list; every other agent session keeps the general owner-verb refusal). The text only informs; it grants nothing.

#### Write a role definition

```
<ah> roster role set|remove|trust …
```

Refused to an agent session in the hook (a member marker, a subagent, a restricted role) and in the CLI to a caller whose nearest registered session is a member (`member-cannot-role-set|remove|trust`). A plain session outside any team still runs them, and `role list` is open to everyone.

#### Approve a sub-orchestrator's close

```
<ah> roster close-approve --team <child> --verb dismiss|disband [<member>] [--plan-token <t>]
```

Run by the managing team's Orchestrator. Bare, prints the close plan, its release status and the token and writes nothing. With the current token, appends a grant (valid 30 minutes, the newest 20 kept) to `streams.<ws>.close_grants` of the managing team. Refuses an empty set (`untrack` instead) and a stale token. The sub-orchestrator then runs the close with the same `--plan-token`.

#### Stream board

```
<ah> roster stream-status [<name>] [--plain] [--team <t>]
```

Never writes the team file; re-renders each open stream's tab label.

Output: `{team, transport, streams: [{name, state, label, tab: {id, live}|null, branch, base, worktree: {path, exists}|null, opened, closed?, peers: [{name, role, status, pane_id, tab_id, in_stream_tab}], exchanges: [{id, slug, to_name, from_name, age_sec}], waiting_on: [{on, request, age_sec}]}], degraded?}`, open streams first, then done, each by `opened`.

`status` is herdr's `agent_status` (`working`, `idle`, `blocked`, `done`, `unknown`) or `gone`; `exchanges` are the open ones a stream member sent or owes, `waiting_on` one per exchange, `on` its `to_name`. tmux/terminal: `status: "unknown"`, `label` and `tab` null, no herdr call. `--plain`: one line per stream. An unknown `<name>` is `unknown-stream`.

Each stream row carries `progress`: the Done / In progress / Not started counts, `state`, `updated`, `newest_entry` and `stale` (a report more than 30 minutes newer than the checklist's `Updated:`); absent while progress is off.

#### Live stream branches (for scripts)

```
<ah> roster stream-list
```

Read-only, writes nothing, no network. One line per live stream with a branch, over every team of this checkout: `<branch>\t<absolute worktree path or ->`. Empty with exit 0 when there are none; non-zero on any error. `scripts/branch-sweep.sh` reads it for its live set.

#### Wrap up a stream

```
<ah> roster stream-done <name> [--dry-run] [--team <t>]
```

Sets `state: "done"` and `closed`, and labels the tab `✓ <name>`. Closes no member pane and removes no worktree or branch. On herdr it closes the stream's tab only under this rule: a stream's recorded herdr tab is closed only when every pane in it is one ah made (a current member's or the stream's `root_pane_id`) and none runs a harness; an unreadable pane list or process table counts as unsafe.

Output adds `tab_closed: <tab>`, or `tab_kept: "panes unknown"|"foreign pane"|"harness running"` with `tab_kept_pane` (the pane) for the last two. Output: `{done, already_done, stream, label, members, open_exchanges, next, warnings?}` — `next` is a `dismiss` per member, for when the user asks; `open_exchanges` is advisory. Idempotent (`already_done: true`). A tab that is gone is a warning.

`--dry-run` writes and renames nothing. Unknown name: `unknown-stream`. Also records `done_at` (UTC), warns when the checklist still lists In progress items, and sets the checklist's `State:` word to done.

#### Hand a member to a lane

```
<ah> roster transfer <member> --to <team> [--stream <ws>] [--dry-run] [--team <t>]
```

Sets the member row's `lead` to the lane's sub-orchestrator (`--to` a child team of this team whose stream has a live `sub_orchestrator`), or clears it with `--to <this team>`; the member keeps its pane, worktree and briefs. A member of a lane can go to another lane of the same home in one step.

`--stream <ws>` must be the lane's own stream and also moves the member into that stream's tree (as `move --stream`); a running member whose session is in another tree is refused (`relaunch-needed`; Claude Code pins a session to its launch tree, so dismiss it and spawn it again into the stream).

Routing follows the lead at once; the member learns of it at its next turn from one `member-transfer` event (one id, to the member, its old lead and its new lead; with no mailbox the output says no event was sent). A reply to an exchange the member already holds still reaches the old lead; a new request to it does not.

`--dry-run` lists the recipients and the open requests addressed to the member's role and writes and sends nothing. Refusals (exit 2, nothing written): `unknown-member`, `member-is-orchestrator`, `target-not-lane`, `stream-not-lane`, `lane-closed`, `lane-no-so`, `already-there`, `lead-is-member`, `lead-changed`.

Checklist items are not re-owned, and no team-history row is written: the `member-transfer` event is the only audit. Owner verb: a member session cannot run it.

#### Plan a stream

`<ah> progress item add|start|done|drop|reopen|retitle|link|note <stream> <item> [--title <T>] [--commit <sha>]… [--branch <b>] [--issue <n>] [--note <T>] [--team <t>]` · `<ah> progress item import <stream> [--team <t>]` (also `ah roster progress item …`; the group word `progress` is the roster verb).

Owner-only: only the Orchestrator that owns the team runs it, and a member is refused by the hook and by the owner proof (`member-cannot-progress`, `not-team-owner`). The one exception is a registered sub-orchestrator, which may run it on its own child team only and must name that team with `--team` (otherwise: `name the child team with --team`); its own subagents are still refused.

Each call appends a row to `progress/<team>/<stream>.plan.jsonl` (`{v, ts, team, stream, item, event, title?, commits?, branch?, issue?, note?, by, session}`; `by` is `orchestrator@<team>`, `session` is the proved caller's session id or null, and a row written before `session` existed reads as null) and re-renders the stream's Checklist block from the log.

`<item>` is a slug (`[a-z0-9-]`, at most 64); `done` needs 1 to 8 commits (7 to 40 lowercase hex digits) that resolve in the stream's work tree or where the command runs; a title is at most 120 characters and a note at most 300; `import` opts a checklist in once.

Output: `progress: <stream>/<item> → <state>` (`import`: `progress: <stream> → planned (<n> item(s) imported)`, then one `unparsed: <line>` per line it could not read).

Refusals (exit 2, nothing written): a bad slug, event, flag or value, `commit <sha> not found`, `progress is off for <team>`, a stream with no checklist or a newer-major checklist, an `import` over an existing plan, and a row the log would refuse (for example an event for an item never added). Native-only: there is no Node recording of it.

#### Re-render a stream label (hook)

```
<ah> roster stream-label <name> [--self-pane <id> --self-state working|idle|blocked] [--team <t>]
```

What `ah-hook stream-label` runs; harmless by hand. `{rendered: true, label, previous?}`; an unknown or done stream, a stream with no tab or a gone one, or a non-herdr team is `{rendered: false, reason}`, exit 0.

#### Dismiss one member

One call: `<ah> roster dismiss <name> [--team <t>]` closes a clean member at once and refuses one that is not clean (exit 2, `refused: not-clean`, its `reasons` and the `--close` command in `next`), where an untracked file last written before the member was spawned does not count as its dirt · plan: `… dismiss <name> --plan [--team <t>]` · close: `… dismiss <name> --close [--also-config] [--level <L>] [--team <t>]` (`--confirm`, `--plan-token <tok>` and `--allow-global` are accepted no-ops).

Plans and close results carry the same `sources` block as `disband`, and a plan carries the same `next` close command.

With no team file (for instance right after `disband --close` removed it): exit 0 with `dismissed: false, reason: "no active team and no live peers"` when the peer registry offers no closable session; exit 2, listing the live untracked sessions and the name / pane_id / session_id values `dismiss` accepts, when it does.

A plan for the team's last member adds `team_will_be_removed: true`; closing it removes the team file and adds `team_removed: true`. It resolves the member through the same sources as `disband` and returns the same verified-close keys (`closed` means verified gone; `still_live`, `strays`, `warnings`); see [Team home and teardown](#team-home-and-teardown).

A **plan** for a member with no pane (a `terminal`-transport team member, a null `transport_id`, or a live peer record with no `pane_id`) exits 0 with `expected: 0`, no `next`, `not_closable: [{name, reason: "no pane"}]` and a W8 warning; a member with a pane gets `not_closable: []` and no W8. `dismiss --close` on such a member is unchanged: exit 2, "no addressable pane", no JSON.

With the worktree gate, the plan also prints `tmp: {path, kind, size_bytes}`; a verified close removes the member's tmp (only under the team's `tmp/`; a failure is a warning, never a refusal) and its alias, and a `_dismissed/` directory an older version left is deleted after 7 days by the next spawn or dismiss.

`--close` runs the [release check](#release-check) on the member: a held member is not closed, the result carries `held` and `release` and exits **3**; `--ignore-release <name>` closes it anyway, with a warning. A plan prints the member's release status in `release` and exits 0.

**Several members, one confirm:** `… dismiss <a> <b> … [--team <t>]` plans them together (one `members` row each, `release` for all) and prints one `close_token` and one `next` only when every named member may close; a member that may not is listed in `failures` and no token is issued.

The token names the team and the sorted set of panes, so `… dismiss <a> <b> … --close --confirm --plan-token <tok>` closes the same set in any order and refuses a different one with the usual token-mismatch text. Members close one at a time in the order given; the first failure stops the batch (`results` has `closed`, `failed` or `not-attempted` per member) and the exit is 1, and members already closed stay closed.

`--ignore-release <name>` works per name: give it once for each held member; while a named member is still held, nothing closes. One command is one permission prompt (and none with the lifecycle rule for `dismiss`).

Stream tabs: a stream's recorded herdr tab is closed only when every pane in it is one ah made (a current member's or the stream's `root_pane_id`) and none runs a harness; an unreadable pane list or process table counts as unsafe. The close summary adds `tabs_closed` (tab ids) and `tabs_kept` (`[{stream, tab, reason, pane}]`, reason one of `panes unknown`, `foreign pane`, `harness running`), each only when not empty.

Only a done stream whose member it just closed counts.

#### Untrack (close nothing)

`<ah> roster untrack <name>|--all [--plan|--commit] [--keep-sessions] [--also-config] [--level <L>] [--team <t>]`. Idempotent: with no team file it exits 0 with `untracked: false, already_untracked: true, reason: "no team file to forget"`. Untracking the last member removes the team file and adds `team_removed: true` beside `team_empty`.

#### Re-register this session

```
<ah> roster checkin [--team <t>] [--orchestrator-pid <pid>]
```

The output carries `team` when this session is attributed to one (`null` for a legacy `team.json`); a create commit refuses a verified member object whose `team` is not the team being committed.

#### Recover a lane

`<ah> roster recover show|hold|release|now <member> [--team <t>]`, or `enable|disable|status` (owner only; members ask the Orchestrator)

#### Re-link a lane child

`<ah> roster recover --team <parent> --relink-lane <child team> [--relink-lane <child team> ...]`: the parent's owner stamps the parent's current `team_id` into each named child's `parent` link, when the child's link names the parent and the parent's stream records a lane lead. A link that already names another `team_id` is overwritten, and the line printed says so; the owner naming the child is the decision.

Plain `recover --team` never changes a child's link.

#### Track a long job

```
<ah> roster job start --kind test|build|slot|smoke|bg --label <text> [--eta small|medium|large] [--pid N] [--tool-use-id ID] [--background] [--cap-secs N]
end <id> [--exit <code>]
extend <id> --eta small|medium|large
```

#### Wait for ahd's next event (Orchestrator)

```
<ah> roster wake [--cwd <abs cwd>]
```

Run in the background by a team's Orchestrator: blocks until ahd has an unacknowledged event for it (or 20 minutes pass), prints it, and exits. Exit 3: the daemon is down.

#### Failsafe beside wake (Orchestrator)

```
<ah> roster overdue [--cwd <abs cwd>]
```

Run in the background beside `wake`: exits when ahd missed a dispatch step.

#### Why was I not woken

```
<ah> roster stall-diagnose <request id> [--team <t>] [--cwd <abs cwd>]
```

Read-only: why ahd did not wake the Orchestrator for a dispatch (six facts and a verdict).

#### Decide a classifier block (Orchestrator)

```
<ah> roster block-triage <tool_use_id|group key> --decision issue:<#N>|upstream:<url>|guidance|outward-confirm|none --note <why> [--team <t>] [--cwd <abs cwd>]
```

Owner only: appends a `classifier-triaged` peer row for the block, or for every untriaged block of a group key that `roster doctor` lists under "classifier blocks (7 d)".

#### The Orchestrator's outstanding list

`<ah> roster checklist show [--all]` · `add <what> [--role <R>] [--stream <S>] [--todo <T>] [--ref <id>] [--parent b<N>]` · `set <id> [--state <S>] [--owner <M>] [--todo <T>] [--ref <id>] [--note <T>] [--option <label>]… [--class blocker|soon|eventually] [--parent b<N>|none]` (`--option` only with `--state waiting-user`: at most 4, each 1 to 64 characters, no repeats; a re-ask without it clears them; `--class` only with `--state waiting-user`: how urgent the question is, `soon` when not given; Needs-you and `show` sort blocker, soon, eventually) · `done <id> [--note <T>] [--commit <sha>]…` (each with `[--team <t>] [--cwd <abs cwd>]`).

Every plan item of the team's streams plus loose ends in the reserved stream `_run`. Check it first on every wake. Output is JSON by default (`--plain` prints text): `add`/`set`/`done` give `{item, stream, state[, created]}`, `show` gives `{team, items, queue}`. An `add` of the same title in the same stream within 10 minutes returns the existing item with `created:false`.

States: `queued`, `dispatched`, `waiting-orch`, `blocked*`, `waiting-user*` (`waiting_user` is accepted), `dropped` (* needs `--note`); `done` is the verb `done <id>`, not a state. `--parent` links the item to a backlog epic or deliverable; only its shape is checked, not that the item exists.

#### Filter the checklist

`<ah> roster checklist show [--state <S>] [--stale] [--since <ISO or 6h>]` lists items in one state, items with no activity for over 24 hours, or items changed recently. `show <id> --trail` prints an item's history and the command that would undo its last automatic move. States: `next`, `in_progress`, `waiting_orch`, `in_review`, `approved`, `blocked`, `waiting_user`, `dropped`; `done` is its own verb.

#### How long each stage took

```
<ah> roster timing [--item <id or slug> | --batch <sha>] [--plain] [--team <t>]
```

Read-only. Per item and per ship: time in spec, build, review, rework, orchestrator, blocked, QA and waiting to ship, and which item held the ship up. With no flag, the last ship and the open items. The JSON also carries `signals[]`, the bottlenecks found, each `{id, evidence, move}` (for example `S-review` with move `M2`); empty when none fired.

`--plain` prints one `signal <id> (move <move>): <evidence>` line per signal. The Orchestrator reads these to reshape the workflow.

#### A role's testing text

```
<ah> roster testing show
testing set <role> <text> [--level <l>]
```

What a role is told about running tests.

#### Who am I / where is my orchestrator (peer)

```
<ah> roster whoami [--team <t>]
```

Read-only, writes nothing, exit 0 whenever the lookup ran. Matches this session's pane id (`HERDR_PANE_ID`, else this pid's `peers.jsonl` row, else `TMUX_PANE`) against every team record, or only `--team`'s. A launched member knows its team first from `WRANGL_TEAM_FILE`, which the launcher sets on every member it starts; the pane match is the fallback.

The path alone names the team, whether or not its file exists (a member starts before `create --commit` writes it, and outlives a reap, disband or untrack that removes it); with no file there is no record, so `member` is `null` and `reason` is `no-team`.

Output: `member: {name, role, route}|null`, `team` (`null` for the legacy `team.json`), `team_file`, `orchestrator: {pid, session_id, live, send_to}|null`, `answered_by`: `env` | `pane` | `null`, `reason`: `null` | `no-pane-id` | `no-team` | `not-a-member` | `ambiguous` (then `candidates: [{team, name}]`).

An `WRANGL_TEAM_FILE` that is not a team file of this repo is never followed; it is reported, beside that real outcome, as `env_team_invalid: {value, kind, why}` with `kind` `other-repo` (a well-formed team file of another repo's hierarchy dir) or `malformed`, and is absent when the variable is unset or accepted. `session_id` is passed through as recorded, and is `null` on every path the agent-team skill uses.

`send_to` is usable directly as SendMessage `to`, but is best-effort — derived from the pid, non-null only while the orchestrator is alive and its session socket exists. Reply precedence: the brief's `reply-to` (or the orchestrator's session name from `ListAgents`) is authoritative — see [comms-protocol.md](comms-protocol.md); `send_to` is the fallback when that is lost.

The socket directory follows this session's own `CLAUDE_CODE_MESSAGING_SOCKET` when the harness exports it (else `/tmp/cc-socks`); the real directory is harness-owned, so `send_to` stays best-effort.

Every output, whatever its `reason`, also carries `last_observed_brief: {from, from_name, reply_to, ts}|null` — the last obligation row a hook filed for this session in `~/.claude/ah2/agent-hierarchy.peer-pending.jsonl`, any status, with `from_name` `null` when none was recorded.

These values are observed, not verified: they say who last briefed this session, which can differ from the recorded `orchestrator`, and like `send_to` they are a fallback below the brief's `reply-to` and the `ListAgents` name.

`null` is the expected value, not a failure, in two cases: no session id resolves (`CLAUDE_CODE_SESSION_ID`, else the `session_id` on this pid's `peers.jsonl` row), or no row exists — only a sentinel or `[hierarchy-msg …]` brief files one, a plain cross-session message does not, and its absence does not make the session unaddressable. It never changes `reason`.

For a member of a team with a grants document the answer carries `work_tree: {root, stream, mode, moved_or_missing, extras: [{path, note, for, outside}], tmp: {root, alias, alias_ok}}`; otherwise `work_tree` is `null`.

#### Snapshot a team

```
<ah> roster snapshot [--team <t>]
```

Records every member (`name`, `role`, `kind`, `route`, `model`, `effort`, `autoMode`, `args`, `stream`, `expected_root`), its `session_id` (from its live mailbox registration, else its newest peers row; `null` when neither knows it) and `thread_id` (always `null` for now), and the team's transport and layout mode, in `<hierarchy home>/snapshots/<team>-<ts>.json` (the default team is `team`).

The newest 10 per team are kept; older files of that team are deleted, and no other file. Written through the `team` state domain's `snapshot` operation. Taken automatically by `disband --close` before it closes any pane (a failure is a warning, never a reason not to disband) and by the SessionEnd hook when the Orchestrator's session ends owning a team that has members.

Exit 0 with `{snapshot, team, members: [{name, role, session_id}], removed}` With a grants document the snapshot also records each member's `work_root` and the team's `gate_mode`.

#### Checkpoint a team

```
<ah> roster checkpoint [--team <t> | --all] [--note <text>]
```

Owner-only. For each team: saves a snapshot, then asks each live member that is not idle and clean (busy, an open request to or from it, a dirty tree or a detached HEAD) to commit its work as `wip(resume): …`, stop its background processes and answer `Checkpoint: ready branch=<b> sha=<sha> inflight=none` or `Checkpoint: not-ready reason=<text>`.

The request is a message file of slug `checkpoint`, filed and sent to the member as a mail brief by `ah msg send` itself (the same daemon-first send, the same delivery line); a `delivery: relay` line the send printed is repeated in the output's `relay` array for the Orchestrator to act on.

It writes `<hierarchy home>/checkpoints/<team>/<UTC ts>.json` (written once, never replaced; it names the snapshot, the open run anchor, the rollup, each member's branch, work tree, head, classification, request ids and open requests, the Orchestrator's own session, pid and tree, `note`, and `supersedes` — the request ids of the previous record), and appends one `Resume point: <record path>` line to the team's progress rollup when it exists.

It never waits, pushes or closes a session. `--all` means every team the caller owns. Output: `{teams: [{team, record, snapshot, rollup, members: [{name, classification, request?, request_path?, entry?, delivery?, mail_error?}], relay, safe_to_restart: false, next}]}`; exit 1 when a mail send was refused (the record is still written).

#### Is it safe to restart

```
<ah> roster checkpoint --status [--team <t> | --all] [<record>]
```

Owner-only, read-only. Judges the newest record of each team (or the given absolute record path, one team only) against the answers filed since and the work trees now.

Per member: `ready <sha7>`, `idle-clean`, `asked` (unanswered and busy), `unanswered` (unanswered and idle, or no valid `Checkpoint:` line), `not-ready`, `stale` (`dirty`, `sha`, `busy`, or the tree unreadable) and `gone` (no longer live; safe only if it was idle-clean or answered ready, and its tree is clean).

`safe_to_restart` is true only when every member is `ready`, `idle-clean` or a clean `gone`, and the Orchestrator's own work tree is clean. Output: `{teams: [{team, record, ts, members: [{name, status, detail, safe}], orchestrator, safe_to_restart, blockers}], safe_to_restart}`. Exit 0 when safe, 3 when not (a team with no record is not safe). There is no override: whether to restart anyway is the user's call.

#### Quit a team

`<ah> roster quit [--team <t> | --all] [--wait <seconds>] [--note <text>] [--orchestrator-pid <pid>]` (alias `pause`)

The user's order only. The user may type it in a plain terminal with no Claude session above it: the folder's one team is taken, and with several the command names them and asks for `--team <t>` or `--all`. Puts the team and its lanes on hold, asks every member that is not idle and clean to save its work and write a resume plan, waits for the answers, then seals each hold with its resume plan and prints the path.

`--wait` is the whole window from the hold's start; one call waits at most 100 s, then exits 4 with `waiting_on` and `next`, and a rerun continues. `--all` takes every top team this session owns. A held team starts nothing by itself until `resume --hold`. `<ah> roster quit --status [--team <t>]` reads the hold. Nothing here needs the daemon.

#### Stop a team now

```
<ah> roster stop --now [--team <t>|--all] [--confirm] [--grace <seconds>] [--reason <text>] [--orchestrator-pid <pid>]
```

The user's order, or a member's work that is itself doing harm. Like `quit`, the user may run it from a plain terminal. Puts the team on hold, tells every member to stop all its work and stops the background jobs members registered with a verified pid; no member session is killed. Without `--confirm` it only prints what it would do. Resume with `resume --hold --team <t>`. Nothing here needs the daemon.

#### Resume a team

```
<ah> roster resume [<snapshot file or path>|--team <t>] [--yes] [--orchestrator-pid <pid>]
```

The newest snapshot of the team when none is named. After a reboot, `<ah> roster resume --all [--yes]` brings back every top team this session owns, lanes included: quit holds resume, stopped teams are listed with their command, unheld teams are recovered and their missing members relaunched with no brief, and teams it does not own are only listed under `not_owned`.

To resume a held team (after `quit` or `stop --now`) run `<ah> roster resume --hold --team <t> [--yes]`, the user's order only: without `--yes` it prints the plan, with `--yes` it resumes, lanes included, sealing a quit left winding down and taking over a team whose Orchestrator is gone; read the plan or assessment before `--yes` (its own plan). The Orchestrator hands the user the plan path that `quit` prints.

Without `--yes`: exit 0 with `{status: "planned", snapshot, team, to_launch, top_tier, members: [{name, role, model, status, why?}]}` and nothing launched (`top_tier` counts the members to launch on opus or above).

With `--yes`, each member in order, from its own `expected_root`, through the spawn-ad-hoc path, into its recorded session (with the member's agent and name as well); the outcome (`status`) is `resumed`, `fresh` (no session id recorded, or a non-Claude kind: those are relaunched without resuming), `alive` (not launched: the session's registration pid is the process that registered and is alive, or a running `claude` process has the session id on its command line, or the spawn path found the name held by a live session), `no-worktree` (its `expected_root` is gone), `skipped` (a subagent-route member has no pane) or `failed` (`why` has the error; the other members still go ahead).

A registration pid counts as the registering process only if its start time (`ps -o lstart=`) is no later than the registration file's last write plus 2 s: a pid started later was reused, and the command-line scan alone decides; when `ps` fails the member counts as alive.

The guard and the launch of each member run under one per-team lock, so two `resume`s cannot both launch one session (the second sees it `alive`); the lock is taken over as stale after 300 s, so one member's guard-and-launch that takes longer loses the exclusion. When the process list cannot be read (`ps` fails) the member counts as `alive` (`why`: `ps failed`).

A team file held by another live Orchestrator is not taken over (exit 2, nothing launched); otherwise the session running `resume` becomes the team's Orchestrator (`orchestrator: {session_id: null, pid}`, as a spawn records it), unless another live Orchestrator took the team while the members launched: then the record is left as it is and the output carries `ownership_refused`.

Unacked mail is not resent: a resumed session re-registers at SessionStart and its adapter pulls it. Never run automatically A member resumes in its recorded `work_root` (refused as `work-tree-missing` when that tree is gone), rejoins its recorded stream, and a missing grants document is recreated in the recorded mode; extras and tmp contents are not restored. An older snapshot uses `expected_root`.

**`resume --checkpoint [<record>]`** (the newest checkpoint record of the team when none is named; refused with "no checkpoint for" when there is none; `--all` takes every team the proved caller owns that has a record, reports each on its own under `teams`, never takes ownership of any, and exits 2 with "no owned team has a checkpoint; name one with --team" when there is none; `--all` cannot be combined with a record path, `--team` or `--relaunch`) resumes from a `checkpoint` record instead of relaunching a snapshot.

It launches nothing by default.

Each member of the record is verified against its registration (the one liveness predicate) and the team's own view: `live` (registered under the recorded session and running), `pending` (its registration names a process that is gone, or the team lists it but it has not registered again: run `resume` again once the restore settles; a pending row carries `age_seconds`, the time since its registration was written), `missing` (no registration and not listed as running) or `replaced` (running under another session: reported, never touched).

Without `--yes` it plans: `{status: "planned", checkpoint, team, members: [{name, status, why, relaunch?, rebrief?}], anchor, resume_point, open_requests: [{id, owner}]}`.

With `--yes` it makes the session running it the team's Orchestrator (left alone and reported `bound` when the record already names it, `transferred` otherwise, `held` when a live other one keeps the team), then sends each `live` member that had open requests at checkpoint time one brief from its checkpoint response (`rebrief: sent`; a member whose checkpoint request was never answered gets one brief per open request in the record, oldest first, never the checkpoint request itself (`rebrief: unanswered-fallback`; no wip sha exists for it); one with no open request file found is skipped with the reason; a refused send gives `mail_failed` and exit 1).

Idle members get nothing. Only a `missing` member is ever relaunched, and only with `--relaunch <name>` or `--relaunch-missing`, from the snapshot the record names and through the same path as a snapshot resume (`relaunched`, or `relaunch-failed` with `why` and the member's open requests as `orphaned`).

The registration is read again just before a relaunch, and a member that has registered meanwhile is refused (`relaunch-refused`, "restored meanwhile"). `--relaunch <name>` also takes a `pending` member, as an explicit decision that it died for good; `--relaunch-missing` takes missing members only.

Pending and missing are decided by the member's pane in the multiplexer, listed once per run: a member that is not live (no live registration) is `pending` when its pane is there or the multiplexer could not be listed, and `missing` when its pane is gone.

Immediately before any relaunch the member's registration and pane are read again, and it is refused (`relaunch-refused`, with the reason in `why`) when it has registered meanwhile ("restored meanwhile"), when the pane state is unknown, or when any process at or below the pane's root process is a harness session by its command line ("a harness is running in its pane"; a process table that cannot be read counts as running).

A restore cannot land in a pane that is gone, so a missing member's pane is not searched. On herdr, a member relaunched from a pane that is there with no harness in it (a dead terminal) never launches in it.

When herdr core ties the pane to the member (the terminal carries `ah.spawn=<the row's spawn_nonce>`, or is the one terminal labelled `ah.name=<member>`), a new pane is split off it, the old pane is read again and closed, and the member launches in the new pane (`old_pane: "closed:<id>"`; a pane that changed before the close is left open, `old_pane: "changed, left open"`).

A pane with no tie (always on herdr TUI) is neither split nor closed and the member takes normal placement (`old_pane: "untied, left open"`). When the old pane cannot be closed the member is `relaunch-blocked`, with `blocked` and the by-hand lines in `by_hand`, and the other members go ahead.

A relaunched member reports `session` (`resumed:<id>` or `fresh`; the id is the snapshot's, else the team-file row's `session_id`) and `pane` (`new:<id>`). `--relaunch` and `--relaunch-missing` without `--checkpoint` are refused.

#### Pause one member

`<ah> roster pause-member <member> [--cwd <abs cwd>]` or `<ah> roster pause-member --team <t>` (every member of that team)

While a member is paused, ahd sends it no check-in, overdue, resume or idle nudge, re-delivers nothing to it, and raises no alarm on its requests. Each member is told by one mail event (`ah: you are paused by your orchestrator…`). It closes nothing and holds no team, unlike `quit`. A name that is not on the team is refused and nothing changes.

The default (unnamed) team cannot be paused member by member: name it with `--team`. Prints `{verb, paused, team, members, told}`.

#### Resume one member

`<ah> roster resume-member <member> [--cwd <abs cwd>]` or `<ah> roster resume-member --team <t>`

Undoes `pause-member`: ahd nudges and alarms for the member again, and its request clocks restart from that moment. Each member is told by one mail event. Same arguments and refusals as `pause-member`.

#### Brief a pane member (any kind, on herdr or tmux; one line of at most 1000 characters is typed, only into an idle, empty composer; `paste: false` or no pane id gives `not-sent`)

```
<ah> roster deliver <name> --req <abs request path> [--ping <n>] [--wait-only] [--timeout <s>] [--team <t>]
```

Run it in the background. Creates (or reuses) the response file beside the request, sends `[hierarchy-msg <req>]`, `Report to: <response>` and the member's standing-instructions path (a ping sends `Ping <n>/3: …`; `--wait-only` sends nothing and only waits), and waits for the turn to end.

A member that is working or not ready is waited for first, polling, and both waits share one `--timeout` deadline (default 1800 s).

Exit 0 with `{status, sent, name, request, response, agent_status}`; `status` is `reported`, `no-report`, `malformed-report`, `busy` (still working or not ready at the deadline; nothing sent: re-run the same command), `blocked`, `not-live`, `indeterminate`, `timeout` or `not-sent` (`--wait-only` found no response file). `sent` is true only when this run sent its brief or ping, never under `--wait-only`.

A request to a review-class role must state its goal and acceptance: otherwise `deliver` refuses (exit non-zero, nothing sent) and says what is missing. A report is judged against the body the response file is created with, trailing whitespace aside. `no-report` and `timeout` carry `pane_tail`.

`not-live` carries `spawn`: `spawn-one <role> --member <name>` when a roster row derives the name, else `spawn-ad-hoc` with the member's recorded fields and a `spawn_note` that the name may differ.

`blocked` carries `blocked_by` (`trust-dialog`, `login`, `approval`, else `harness-prompt`), `screen` (the member's visible screen, verbatim), `screen_hash`, `options` (`[{id, label, description, grants}]`, only those the recognised option block reads; `[]` for `harness-prompt`) and a `message` with the relay steps.

Nothing is sent to a member that is working, not ready or at a prompt, and a codex member is sent a brief or ping only while its screen is Codex's idle, empty composer: any other screen, read twice, is `blocked` with `harness-prompt` and `options: []`. `--wait-only` sends nothing and has no composer check.

A Claude member, or a name the team does not hold, exits 2; so does an advise-class member when this session has no `session`/`each` Ultra-Advisor decision on record.

#### Cancel a member's prompt

```
<ah> roster answer <name> --cancel --screen-hash <hash> [--team <t>]
```

Presses Esc once on a Claude member, only when its screen still hashes to `<hash>` and it is blocked right now. Statuses: `cancelled`, `still-blocked`, `screen-changed`, `not-blocked`, `not-live`, `indeterminate`, `cancel-unsupported` (not a Claude member, or no herdr pane). Cannot be combined with `--choice` or `--prompt`.

An auto-deny countdown ticking on the prompt is left out of the hash, so it does not make the cancel refuse with `screen-changed`; any other change on the screen still does. The daemon's BLOCKED event renders it.

#### Answer an app-server Codex member's request

```
<ah> roster answer <name> --request <id> (--choice <id> | --answer <qid>=<text> [--answer …]) [--text <s>] [--team <t>]
```

A thin client of the daemon's `POST /v1/codex/answer`, which holds the member's pending request (a command, file-change or permissions approval, a question, or an elicitation) and which the `member-request` mail named. The daemon proves the caller: only the user or the team's Orchestrator, else the verb fails with `not-team-owner`; with the daemon down the verb fails `daemon-unavailable` (there is no file fallback).

Exit 0 with `status`: `answered`, `needs-user` (with `reason`: the Orchestrator's choice is a grant outside its bounds — only a one-time `accept` of a command or file change inside the member's work tree, with `codex_orchestrator_approvals` not false in the user config; nothing was sent), `unknown-request`, `bad-choice`, `secret` (answered in the member's pane), `needs-evidence`, `steered` (a question answered after Codex moved on, delivered as a steer), `disconnected`.

Each approval answer is appended to `$AH_HOME/codex-grants.jsonl`, and `roster doctor` counts them. Cannot be combined with `--cancel`, `--prompt` or `--screen-hash`.

#### Answer a member's prompt

```
<ah> roster answer <name> --prompt <blocked_by> --choice <id> --screen-hash <hash> [--team <t>]
```

Only after asking the user. Sends that option's keys from the kind's table, and only when one `visible` read still shows that prompt with that hash and that option. Exit 0 with `status`: `answered` (`agent_status`, `live`, `prompt_after`, `screen`), `screen-changed` (fresh `blocked_by`, `screen`, `screen_hash`, `options`; nothing sent), `not-live` or `indeterminate`. Takes no keys and no text.

Exit 2 for a Claude member, an unknown `--prompt` or `--choice`, or an option not on the screen.

#### Approve one held call (soft gate)

```
<ah> roster approve <member> <rule> --cwd <abs>
```

Orchestrator-run. Appends one `{type: soft-gate, rule, outcome: approve, member}` row to gates.jsonl; the member's next call that `<rule>` (today `worktree`) would bounce goes through and is recorded `approved`, and the call after that bounces again. An approval written before the member asks also works. Exit 2 when `<member>` is in no team of this hierarchy. Prints `{status: approved, member, rule}`.

#### Declare a non-Claude model's tier

```
<ah> roster tier set <kind> <model> <haiku|sonnet|opus|fable>
… tier remove <kind> <model>
… tier list
```

How that model compares with Claude's tiers, stored only in the global config's `modelTiers`. `list` also carries `warnings` for entries it ignores (kind `claude`, or not a tier). An advise-class member of a kind with a model mapping must run on a model declared `opus` or `fable`.

#### List roles

```
<ah> roster role list [--json]
```

Every built-in and custom role: class, agent, model, level, chain placement (`alt. to <Builtin>: <routes>`, `side`, `legwork`), effective description, and contract status (`shipped`, `ok`, `n warnings`, `UNAVAILABLE: n errors`, or `UNAVAILABLE → reverted to wrangl:<role>` for a failing built-in override). Invalid rows are listed as `excluded` with their reasons.

`--json` adds each role's findings, and `path` names the agent-file copy that was checked. A role adopted from a role pack also shows `from` and its `pin_state` (`trusted`, or its pack reason: `pack-missing`, `pack-invalid`, `pack-ambiguous`, `pack-symlink`, `pack-changed`, `pack-untrusted-here`), and a built-in whose agent a pack overrides has `pack_override: true`; rows without either are unchanged.

#### Define or change a role

```
<ah> roster role set <name> [--class advise|design|review|implement|legwork] [--agent <ref>] [--label <l>] [--description <d>] [--routes <r>] [--model <M>] [--dispatch peer|model] [--level global|repo|repo-user] [--scaffold repo|user] [--dry-run]
```

Upserts one row at one level (default: the level the role is already defined at, else `repo` with `--scaffold repo`, else `global`), seeded from the effective row. A built-in takes only `--agent`, `--model` and `--dispatch`. `--dispatch model` only for a legwork-class role (else exit 2). `--routes ""` / `--description ""` delete the key.

The agent file is validated against the class contract: errors refuse the write and print each finding with its fix; warnings print and the write proceeds. `--scaffold` writes a template that passes the contract, and removes it again if the write fails. `--dry-run` writes nothing.

#### Remove a role

```
<ah> roster role remove <name> [--level <L>]
```

Refused while any roster member uses the role; never deletes an agent file. For a built-in, removes only its `agent` override (a pack override's `from` and `pin` too).

#### Adopt a role from a pack

```
<ah> roster role set <name> --from <plugin>@<marketplace>:<role> [--label <l>] [--description <d>] [--routes <r>] [--model <M>] [--dispatch …] [--level <L>] (--dry-run | --pin sha256:…)
```

`<plugin>:<role>` works when exactly one marketplace offers the plugin. The dry run prints `pack.fields` (verbatim, hidden characters escaped), the findings (contract, pack-agent frontmatter allowlist, pack reasons), `pack.tools` (`effective`, and `flagged`: Bash, Agent, MCP tools, Write, Edit), `pack.also_in_plugin`, `pack.unattended` for an implement-class role with routes, and `pin`.

The commit needs `--pin` equal to the pin computed now, refuses while errors stand, writes only `from`, `pin` and the flags passed, and stores a copy of what was pinned in `~/.claude/ah2/trusted/`. `--class`, `--agent` and `--scaffold` are refused with `--from` and on any adopted row; a name already defined at the level by another row is refused.

A built-in name takes the pack role's agent only, and only from a role of its class.

A commit is asked of the user in a top-level session and denied in subagent, role and peer sessions, with `WRANGL_TEAM_FILE` set, and while a pipeline run is live; a Bash command that isn't one plain ah command is judged as a commit when its text names `ah roster`, then `role`, then `trust` or `--from`, even with `--dry-run`. ah roster itself refuses a commit (exit 2, nothing written) when `WRANGL_TEAM_FILE` is set, or while a pipeline run is live for `--cwd` or its own working directory.

The spawn verbs refuse a member of a pack role whose auto mode is `bypassPermissions` or unset.

#### Re-trust a pack role

```
<ah> roster role trust <name> [--level <L>] (--dry-run | --pin sha256:…)
```

For a row set with `--from`. The dry run shows `fields_changed`, `agent_diff` and `files` (`added`, `removed`, `changed`) against this machine's stored copy — or, with none (`stored_copy: false`), the whole `agent_text` and `files.all` — plus the findings and `pin`. The commit needs that pin, refuses while errors stand, re-pins, keeps the user's own fields, and is gated like adoption.

#### List role packs

```
<ah> roster pack list [--json]
```

Read-only: every installed plugin with `ah-roles.json`, its version and path, its roles (name, class, agent, routes), each role's adoptions (`name`, `level`, `state`), and a count of what else the plugin carries.

#### Show one role pack

`<ah> roster pack show <plugin>[@<marketplace>] [--json]` · `… pack show --path <dir> [--json]` (not installed)

Read-only: the manifest's findings, each role's fields verbatim with its findings and tools, its adoptions (installed only), `also_in_plugin` (root entries other than `ah-roles.json`, `agents/`, `.claude-plugin/` and README/LICENSE/CHANGELOG files; plugin.json keys beyond the descriptive ones; agent files the manifest doesn't name; any agent file that takes a role's agent name, marked `claims: [<role>…]`), and `digest`, the sha256 of the file list, which is the same for the same tree installed or not.

#### Lifecycle allow rules

```
<ah> roster permissions <check|rules|add|sync|remove>
```

Run by `install.sh` for the user's own terminal, never by an agent: see [Lifecycle permissions](#lifecycle-permissions). Refused inside an agent session and denied by the permission hook to every session.

#### Self-check this install

```
<ah> roster doctor [--check]
```

Read-only JSON, one row per thing that can be wrong; `--check` exits 1 on any red row With a grants document it adds `gate-*` rows: mode and the normalisation note (`ok`), a moved or missing work tree and a member without a record (`red`), tmp alias, size over 1 GiB, dismissed leftovers over 7 days and an unignored tmp root (`warn`; over 4 GiB `red`), gate internal errors and a tripped breaker in the last 24 h (`red`), grants documents left in a work tree's own pool (`warn`) and member scratch left in the team home (`ok`).

`teams` adds `gate: {mode, members, would_refuse_24h, deny_24h}` per team, `null` without a document. A `progress` row is `ok`, `off`, or `warn` (never red). The rows `version`, `team`, `peers`, `registrations`, `rejected-requests`, `hooks-syntax` and `marketplace` each print what they checked in their detail.

#### Split decision (tests)

```
<ah> roster next-split --mode <m> --pane-count <N> --self <pane id> --created '<json>' --geometry '<json>'
```

### Routing, tests and issue intake

#### Route

```
<ah> msg route [peers] --session <id>
```

Prints the session route, always `peers`: chain roles run only as peers. Any other value exits 2.

#### Run tests under the test bucket

```
<ah> test run [--tokens k] [--eta small|medium|large|<s>] [--label t] -- <command…>
```

Any repository, any stack: queues until `k` of the machine's CPU tokens are free (default: the test-command registry's row for the command, else half the bucket), runs the command with the grant in `AH_TEST_TOKENS_GRANTED` and the runner's own parallelism setting sized to it, and caps it at 3 x the eta.

`<ah> test plan --capacity B [--tokens k] -- <command…>` prints what it would do (row, tokens, environment, changed arguments). Rows: `policy/test-commands.json`, the user overlay `test-commands.json` in the user's `.claude/ah2` directory (adds, and exempts by id) and `<repo>/.claude/ah2/test-commands.json` (adds only). `AH_TEST_ADAPT=0` skips the registry step.

#### Issue intake

```
<ah> issue-intake --cwd <abs> --out-dir <abs> (--issues <ref,ref,...> | --labelled)
```

Decides which issues of `origin`'s GitHub repo are eligible pipeline input and writes a snapshot of each eligible one to `<out-dir>/issue-<N>.md`: the title, plus the visible text of GitHub's rendered HTML (`bodyHTML`, never the raw markdown) for the body and included comments. A `<ref>` is `N`, `#N` or `https://github.com/<o>/<r>/issues/N`; given order is kept, duplicates dropped.

`--labelled` takes every open issue carrying `issues.trigger_label`, in ascending number. Eligibility is decided from metadata alone, so nothing of an ineligible issue's text is fetched, and of the newest 100 comments only those by `trusted_actors` not edited since the label are. The text is fetched with the metadata again, and an issue that no longer passes, or was edited in between, is `edited-during-intake`.

Snapshot content lines sit behind a `|` gutter, and an ineligible local issue's stale `issue-<N>.md` is removed.

Prints one JSON object, exit 0: `repo`, `trigger_label`, `trusted_actors`, `items` — each `{issue, eligible: true, snapshot, sha256, lines}` or `{issue, eligible: false, reason}`, with `reason` one of `not-found`, `closed`, `other-repo`, `no-trigger-label`, `untrusted-labeler`, `edited-after-label`, `edited-during-intake`, `hidden-content`.

Run-level failure: exit 1 with `{"error": "<code>: <detail>"}`, `<code>` one of `origin-not-github`, `conventions-absent`, `conventions-invalid`, `no-trigger-label`, `org-needs-trusted-actors`, `gh-unavailable` (gh missing, or `gh auth status --hostname github.com` fails), `usage` (bad arguments) or `intake-failed` (any other gh or I/O failure; the detail is fixed text naming the step and issue).

On exit 1 nothing under `--out-dir` is meaningful. gh's own output is never printed. Conventions: [pipeline-conventions.md](pipeline-conventions.md).


A team file that exists but does not read as a team record (unparseable, or parsing to something
with no `members` array) is never written over. `create` (plan included),
`spawn-one` and `spawn-ad-hoc` exit 2 before anything launches when the file they resolve to —
`--team <t>`'s, the default `teams/<prefix>.json`, or a legacy `team.json` — is in that state, naming
its absolute path and the remedy: repair or remove it, or use a different `--team`. Read verbs never
refuse; beside an unparseable legacy `team.json` a bare read verb resolves to that legacy file
rather than to `teams/<prefix>.json`, so it reports that scope.

## Other `ah` commands

The smaller top-level verbs. "You" means a person at a terminal; "agent" means a role in a Claude session. Every
verb takes `--cwd <abs>`.

#### `ah gate`

*Who runs it:* agents.

Removed: prints a notice and exits 0. A `/wrangl:pipeline` run asks once at its start whether it may use an Ultra-Advisor and records `ultra-advisor: yes|no` in its run anchor.

#### `ah push-guard`

*Who runs it:* agents.

Removed: prints a notice and exits 0. ah no longer blocks pushes or merges; it records them.

#### `ah looks`

*Who runs it:* you, agents.

```
<ah> looks import <file>
```

Merge a looks object (`{custom: [...]}`, optionally `active` and `technique`; a raw `ah-optics` value from a browser is one) into `<AH_HOME>/looks.json`. It only adds: any look the file does not hold goes in, including one deleted earlier, and a clashing id or name keeps both, the imported one suffixed. A file that does not read as looks is left alone and the import fails.

#### `ah alarm`

*Who runs it:* you.

```
<ah> alarm test [--team <t>]
```

Runs your `alarm.command` (user config only) once with `ah: alarm test from <host> at <time>.` on stdin and in `AH_ALARM`, through the runner ahd's user alarms use (10 s timeout), and journals the run as `kind: test` in `<AH_HOME>/stall/journal.jsonl`. Touches no alarm episode; works with ahd down and in any stall mode.

Exit 0 prints `alarm.command exited 0 in <ms> ms`; exit 1 names the exit code, a timeout, or that `alarm.command` is not set. ahd itself runs the command when an Orchestrator is down for 90 s with a run open (or, with no run, an open exchange or a `next` checklist item: told once and cleared, never repeated), or waits on a terminal question for 15 min: once at the start (an Orchestrator-down line ends with counts of the unacked ahd events in its box, `N waiting: kind×n, …`, kinds and counts only), again every 60 min (`AH_STALL_ALARM_REPEAT`, seconds) while it lasts, and `ah: team <t>: <kind> cleared (<duration>).` when it ends.

#### `ah proc`

*Who runs it:* you, agents.

```
<ah> proc list [--team <t>] [--match <text>]
<ah> proc stop <pid>… [--grace <0-600 s, default 3>] [--dry-run]
<ah> proc wait <pid>… [--timeout <0-600 s, default 60>]
```

Find and stop a runaway process that you own: one that descends from your session or from a live member of a team you own, a lane you lead (members lent to it included), or a lane of your team whose lead's process is gone. Each stop is logged to `proc-audit.jsonl`. `list --match` keeps only the owned processes whose command line contains the text (case-sensitive), in place of `pgrep -f`; stop one by its pid.

`wait` signals nothing and checks no ownership: it polls every 250 ms until every pid is gone and prints `{gone, alive}`; exit 0 when all are gone, 1 when the timeout passed with some alive.

#### `ah admin`

*Who runs it:* you (`show`: anyone).

```
<ah> admin grant <pid> [--session <id>]
<ah> admin revoke
<ah> admin show
```

Designates one Claude session, by its process id, as an Orchestrator with admin rights: ah treats a call from that session as yours and refuses it nothing (rule writes, a daemon restart). The grant is a row of the state database (home document `home.admin-grant`) and ends when that process ends or you revoke it; one left in `<AH home>/admin.json` by an earlier build is taken in and the file removed.

While the database cannot be read, no session counts as the admin session, and `show` says so. `grant` and `revoke` are refused inside a session: run them in a plain terminal.

#### `ah ledger`

*Who runs it:* Orchestrator.

```
<ah> ledger ship <sha> [--branch <b>] [--team <t>] [--cwd <repo>]
```

Record a ship that happened outside a Claude Code Bash call, and mark done the open items whose `ah/<slug>` branch the commit contains. It merges and pushes nothing.

#### `ah allow`

*Who runs it:* you, Orchestrator.

```
<ah> allow on
<ah> allow off
<ah> allow list [--json]
```

The switch for member launch rules (the config key `member_allow`): permission rules ah writes so spawned members can run their tools without a prompt each time. `off` narrows and any Orchestrator may do it; `on` widens and needs you at a terminal, or an Orchestrator holding your fresh answer to the matching question; members are refused. The rules are on by default and nobody is asked; `off` is the opt-out.

`list` shows the state: given, declined or off.

#### `ah rules` (rules of engagement)`

*Who runs it:* you, Orchestrator (writes); everyone (`list`).

Usage: `add <scope> --key K --text T [--said W]` · `edit <scope> --key K --text T [--said W]` · `off <scope> --key K [--said W]` · `remove <scope> --key K [--said W]` · `list [<scope> | --member name@team | --item <work id>] [--all] [--json]`. A scope is `--repo`, `--team <name>`, `--project <work id>` or `--goal <work id>`

The user's operating principles at four levels; the most granular row of a key wins, an `off` row takes the key out for that scope and below, and `remove` lets the broader rule show through again.

`list` prints the resolved list, by default the caller's, as `{"rules": [{"key", "text", "level", "scope", "by", "said", "updated_ms", "overrides"}], "off": [{"key", "level", "scope"}]}`; `--all` prints every row of the repo unresolved as `{"rows": [...]}`. Writes go through the daemon, and a write says `"live": "members see this at their next session start, compaction or brief"`.

These are injected principles: nothing checks that one was followed, and a rule never widens permissions. Exit codes: 0 ok, 1 not found, 2 refused, 3 conflict. Key `[a-z0-9-]{1,40}`, text at most 300 characters. **Who may write:** you, or the top-level Orchestrator with your own words in `--said` (recorded, not verified); anyone else gets exit 2. `list` is open to everyone.

#### `ah rules goal` (team goal)`

*Who runs it:* you, Orchestrator (writes); everyone (read).

Usage: `goal [--team <name>] [--json]` reads · `goal [--team <name>] --text T [--done-when D] [--said W]` sets or replaces · `goal [--team <name>] --clear [--said W]` removes

One goal per team, guidance and never a gate. `--team` defaults to the caller's team; none found exits 2. Read prints the text, `done when: D` when set, then `(team T, set by B on yyyy-mm-dd)`, or `(from team P, …)` for a parent's goal a lane inherits; `team T has no goal` when none.

`--json` reads `{"goal": {"text", "done_when", "team", "by", "said", "updated_ms"}}` or `{"goal": null}`; a write answers `{code, message, live, goal}`. Text at most 160 characters, done-when at most 120 (exit 2 past either); `--clear` with no goal of the team's own exits 1.

`ah rules list` prints `[goal] T; done when: D (team T)` first, and `list --all` one `[goal]` line per stored goal (`"goal"` and `"goals"` in JSON). The goal line leads every session's rules block and every request's `msg show` footer.

`ah msg new --type request --serves "<how>"` (or `"off-goal: <why>"`) records how a request serves it as `serves:`; it is cleaned and cut to 160, never refused, and a lead's request without it gets a stderr note while a goal applies. Who may write: as for `ah rules add`.

#### `ah rules`

*Who runs it:* agents.

```
<ah> rules who-may-message <name@team> <name@team> [--slug <slug>]
<ah> rules next-step <event>
```

Read-only answers about the protocol: whether the mesh allows one address to message another (`yes`, `no`, or `yes (warn: …)`, then the rule), and what step an event calls for. Exit 2 for an unknown event.

#### `ah test`

*Who runs it:* agents.

```
<ah> test run [--tokens <k>] [--eta small|medium|large|<seconds>] [--label <text>] -- <command…>
<ah> test plan --capacity <b> [--tokens <k>] -- <command…>
<ah> test plan --check -- <command…>
```

`run` runs a command holding CPU tokens from the machine-wide test bucket (capacity `AH_TEST_TOKENS`, default the larger of 2 and the CPU count minus 2), so several teams do not overload the machine. `plan --check` exits 0 when the command may run and 64 with a reason when it may not.

#### `ah usage`

*Who runs it:* you, agents.

```
<ah> usage [--json]
```

The account's five-hour and seven-day rate-limit windows, from the newest statusline sample, with its age (`stale` past 15 minutes). Reads only; nothing in ah acts on it. Exit 0, 2 for a bad argument.

#### `ah usage-report`

*Who runs it:* you.

```
<ah> usage-report [session|day|week|month|all | --team <name> | --fleet] [--json]
```

Token use by subagents and sessions, from the transcripts. With no window it shows the session and the week.

`--team` shows one row per session the team ever had (from its progress session inventory, the StateDb session rows and its record: Orchestrator, peers, respawns, dismissed members and a closed team included), each the whole session with its subagents, with a subtotal per member and a team total; it takes no window. Sessions whose StateDb rows were pruned and that no inventory names are gone; the footer says when.

`--fleet` gives one total row per team in this hierarchy dir and a grand total; a session under two teams counts once.

#### `ah view`

*Who runs it:* Claude Code or the mod, not people.

Usage: `open --kind web` (the band token as one JSON line on stdin) · `open --kind tui --session <id>`

What the band's buttons run. Exit 0 opened, 2 usage, 3 did not open, 4 the daemon refused the token. Opening the page from a shell works with `ahd ui start` instead.

#### `ah view`

*Who runs it:* you, mods.

```
<ah> view events --sub <id,…> [--since N] [--epoch E] [--wait S]
```

One poll of the daemon's slice feed: prints the body `GET /v1/view?path=/events` returns as one JSON document on stdout. `--since` defaults to 0 and `--wait` (seconds, long-poll) to 0. It never starts ahd: no daemon, or any answer but 200, is exit 1 with one line on stderr. Exit 0 printed, 1 down or refused, 2 usage. What the herdr desktop mod polls.

#### `ah herdr`

*Who runs it:* you.

```
<ah> herdr install [--uninstall | --replace-layout]
```

Copies the plugin's `herdr-desktop/mods/ah2/` and `herdr-desktop/layouts/wrangl.json` into the herdr desktop home (`$HERDR_APP_HOME`, else `~/.config/herdr-app`) and writes `mods/ah2/.ah2-owned` holding the plugin version. A reinstall replaces marker-owned files; a target that exists without the marker is left alone and the verb exits 1. `--uninstall` removes only marker-owned paths. `--replace-layout` moves the installed `wrangl.json` to a timestamped backup beside it and writes the shipped one fresh; an install says to run it when the layout is missing the Desk column.

Exit 0 done, 1 refused or failed, 2 usage.

#### `ah issue-intake`

*Who runs it:* `/wrangl:pipeline`.

```
<ah> issue-intake --out-dir <abs> (--issues <ref,…> | --labelled)
```

Reads GitHub issues for `/wrangl:pipeline` and prints one JSON object. Needs `gh` signed in.

#### `ah db`

*Who runs it:* you.

```
<ah> db backup [--label <l>]
<ah> db list
<ah> db restore [<name>] [--apply]
<ah> db check
<ah> db prune --unplaced [--yes]
```

Operates the SQLite state database under `<ah home>/state/`. `restore` only prints a plan unless `--apply`, and refuses while the daemon runs. `prune --unplaced` only reports unless `--yes` and takes a backup first. `init --accept-empty` always refuses: losing data is your decision, made by hand.

#### `ah backlog`

*Who runs it:* anyone.

```
<ah> backlog list [--wave w] [--state s] [--theme t] [--holder h] [--kind k] [--json]
<ah> backlog show <id> [--history] [--json]
<ah> backlog next [--theme t] [--json]
```

The project's backlog, kept in the state database (not per team). `list` orders by wave (now, next, later, untriaged), then rank. `show` adds `children` (the backlog items whose parent it is) when there are any. `next` prints the first unheld item that is not started, not untriaged, not a project or goal, and whose `depends` are all done or dropped; none is exit 1 with no output.

#### `ah backlog`

*Who runs it:* anyone.

```
<ah> backlog add <title> [--outcome o] [--theme t] [--size S|M|L] [--wave w] [--after id] [--depends ids] [--source s]… [--ext ref] [--kind project|goal|epic|deliverable|task] [--parent id|none]
<ah> backlog set <id> [--title] [--outcome] [--theme] [--size] [--wave] [--after id] [--depends] [--source]… [--ext] [--note] [--kind] [--parent]
<ah> backlog claim <id> --holder h
<ah> backlog done <id> --commit sha… [--note n]
<ah> backlog drop <id> --note why
<ah> backlog testing <sha> <id>…
<ah> backlog testing --clear <id>…
```

`list` and `show` read a row's state from the checklist items naming it as parent: only queued keeps the stored state, all waiting on review or ship reads `in_review`, other started work reads `in_progress`. `testing` marks rows as in the ship batch at that sha under QA, since no hook knows that; `--clear` takes it off, and a row done or dropped, or a clear of an unmarked row, is exit 3.

Writes, each one event per field, recorded with `by` (`--by`, else the member name, else `user`). `add` gives the next `b<N>` and lands in `untriaged` unless `--wave` (a project or goal lands in `later`); with `--ext` held by an item it updates that item instead and prints its id. `claim` of an item held by someone else, or an `--ext` held by another item, is exit 3.

A `--parent` must be another backlog item whose kind outranks the item's (project > goal > epic > deliverable > task, none = task), else exit 2 and nothing is written; it may be a work id; `--parent none` clears it. `--depends` also replaces the row's `ah work` blocked-by links.

#### `ah backlog`

*Who runs it:* Orchestrator, groomer.

```
<ah> backlog import <file.json>
<ah> backlog export [--since <ms|ISO>] [--json]
<ah> backlog groomed --summary <json>
```

`import` adds each element of a JSON array through `add`; a re-import adds nothing. `export --json` prints `{as_of_ms, items}`: pass `as_of_ms` back as `--since` to get only later changes. `groomed` records one groom pass.

`health [--json]` computes the groom health now: untriaged, stale (now/next untouched 14 days, in progress 3 days), the last groom, `due` (24 h since the last groom, or anything untriaged or stale) and `loop: STALLED` when the daemon's `backlog-watch` stamp is over 30 minutes old or the groom has been due over 24 hours.

The same block (short form) is status.json's `backlog`, and `ah roster status --plain` adds `backlog: due|ok`.

#### `ah work`

*Who runs it:* `set`, `link`: not architect or reviewer.

```
<ah> work add <title> [--kind k] [--parent id] [--wave w] [--theme t] [--size s]
<ah> work set <id> [--title s] [--parent id|none] [--ext ref] [--url u]
<ah> work link <a> (--blocked-by <b> | --blocks <b>) [--remove]
<ah> work attach <id> <path|url> [--title s] [--remove]
<ah> work export [--since ms] [--json]
<ah> work next [--under id] [--json]
<ah> work list … [--under id|none] [--ready] [--blocked]
```

`add` makes a backlog row; kinds `project` and `goal` are items too, land in `later`, and are closed by hand only. An item sits at any level: `--parent` is any backlog row that outranks it, and `--parent none` returns it to the root. `set` routes by placement, so it also works on a team item, whose parent must still be a backlog row. `link` refuses a self-link or a cycle (exit 2, naming the path).

Links change no state and gate nothing; a closed or gone blocker no longer blocks. A link or an unlink writes a history entry on both items, and a repeat writes none. Between two backlog rows `depends` follows, so `ah backlog next` stays right; it does not see a team item's blocker, and `ah work next` does.

`--ready` is not started or next, no open blocker, not held, not a project or goal, and for a backlog row not untriaged. `--ext` is the tracker's id, at most 128 characters: it resolves as an id, and one held by another item is exit 3. `--url` is http or https, at most 512 characters, and needs an id; `--ext ""` clears both, `--url ""` the URL, and a new id drops the old URL.

On a backlog row the id is also the row's `ext`. `attach` stores a path relative to its repo with its last commit, or `uncommitted`, and refuses a path outside the repo; a path under `docs/specs/` is kind `spec`. A text with a scheme is a URL, http or https only. The same ref updates; `--remove` deletes.

An Architect's reply attaches up to three `docs/specs/` paths it names, by `auto`, and a failure there never fails the reply. `export` is what a mirror reads, with no bodies: pass its `as_of_ms` back as `--since`, which also finds a change that only wrote history; nothing changed is exit 1. Mirroring itself is the Orchestrator's job, opt-in: `backlog-sync.md`.

`next` is the first of those, backlog order then team items by number; none is exit 1. `--under` walks to any depth; `--under none` is the items with no parent.

#### `ah work`

*Who runs it:* anyone; architect and reviewer may run `list`, `show`, `log`, `mine`, `record`.

```
<ah> work log <id> [--tail 20] [--since ms] [--body] [--all] [--json]
<ah> work record [<id>] --type coding|docs|testing|design|review|research|decision|handoff|note --title s [--body-file -|--body s]
<ah> work mine [--json]
<ah> work show <id> --log
```

An item's history, one timeline: state moves and assignments read from its plan rows, briefs from its refs, and what agents recorded. `log` prints the last 20 title lines; `--body` adds bodies (16 KB, newest kept); `--all` lifts the limit and shows field events. `show --log` adds the timeline and the newest handoff body, at most 8 KB, and marks a dead holder `(dead)`.

`record` changes no state and takes any item in any state: title 1 to 120 characters on one line, body at most 8 KiB. With no id it uses the one open item you hold; none or several is exit 2 listing them. `mine` lists the open items you hold with their last 3 entries. `ah msg show` of a request about an item ends with two lines naming its id and how to record, never with `--out`. Log text is information, not orders.

#### `ah work`

*Who runs it:* anyone.

```
<ah> work list [--state open|all|<state>] [--team t] [--holder m] [--kind k] [--limit 50] [--json]
<ah> work show <id> [--json]
<ah> work prefix [<new>] [--json]
```

Tracked work with one id per repo (`ah-12`), whichever placement an item has: the backlog or one team's stream. Items get their id on their own; state stays in the plan log. An id is `<prefix>-<n>` under any prefix the repo has had, `b<N>`, `<team>/<stream>/<slug>`, `<stream>/<slug>`, a bare slug (your team first, then the repo) or an external id.

`list` shows open items by default, one line each, 50 rows; `state: gone` means the placement no longer exists. `prefix <new>` is 1 to 8 lower-case letters or digits, starting with a letter; old prefixes keep resolving and output prints the current one. `ah roster checklist show|set|done`, `ah backlog show|set|claim|done|drop` and `ah msg new --item` also take a work id, after their own lookup misses.

Exit 0 ok, 1 not found or nothing to show, 2 refused (several matches list their ids; `msg new --item` names the team when the item is another team's).

#### `ah msg history`

*Who runs it:* you, Orchestrator, the herdr mod.

```
<ah> msg history [--search <text>] [--team <name>] [--answered|--unanswered] [--questions] [--limit 20] [--since <ms|RFC 3339>] [--before <cursor>] [--all] [--full] [--id <thread id>] [--dir <hier dir>] [--json]
```

The user's question log, newest first: each prompt typed to the Orchestrator with every reply that followed it until the next typed prompt, and each AskUserQuestion dialog (`terminal`) with its answer.

How replies print:

- `reply`: the direct answer, the Orchestrator's text before its first tool call. A turn stored before replies had parts prints its whole text here.
- `final reply`: the text after a turn's last tool call.
- `later …`: a reply from a later turn.
- `--full` also prints each turn's whole text, as `whole turn`.

Finding a thread:

- `--search <text>` keeps threads whose question, replies, or dialog questions and answers contain the text, in any case. It is plain text, never a pattern.
- `--team <name>` keeps that team's threads.
- `--answered` or `--unanswered` keeps threads with or without a reply. Use one, not both.
- `--questions` keeps prompts that look like questions.
- `--all` adds replies with no typed prompt (`Orchestrator, unprompted`).
- `--limit` is at most 100. `--before <cursor>` continues from the `next` value a `--json` page printed.

An empty result prints `no questions match` when a filter is set, otherwise `no questions logged`.

`--id` prints only the thread whose first row has that id (`--json`: `{threads:[{prompt, replies, ask}], dir}`; a list also has `next`, the cursor of the following page, or null); `--dir` reads that hierarchy dir instead of the cwd's. Rows older than 30 days are dropped. Read-only.

#### `ah msg due`

*Who runs it:* Claude Code or the mod, not people.

Usage: `--json` (a request object on stdin)

The Claude Code mod's poll for mail that is due. Not for people.

#### `ah hook`

*Who runs it:* Claude Code or the mod, not people.

```
<ah> hook [--gate] <name>
```

Runs one hook unit with Claude Code's JSON on stdin. Claude Code runs it; a Bash call that invokes it is denied.


`ah db backup` accepts letters, digits, `.`, `_` and `-` in a label. A database that does not exist is exit 1.
All `ah db` usage errors exit 2.

## The daemon CLI (`ahd`)

`<R>/bin/ahd <verb> [flags] [--cwd <abs>] [--plain]` runs, inspects and checks the `ahd`
daemon. It is a client of the daemon's v1 HTTP+JSON protocol; `ah msg daemon start|status|stop` stay as
aliases of the same functions and keep their own text output. Output is one JSON object on stdout with
`"source": "daemon"` or `"files"`; `--plain` renders the same object as text (a `files` answer starts with
`daemon down, showing files`). Errors are `{"ok": false, "error": {"code", "message"}}` (plain: one
`error: <code>: <message>` line on stderr).

**The bare `ahd` command.** The plugin ships `bin/ahd`, which runs the native `ahd` with the same arguments, output and exit code. Claude Code puts a plugin's `bin/` on the PATH of the Bash tool, so inside a Claude session `ahd status` just works, with no path. A plain terminal has no such PATH entry; to use it there, run `/wrangl:hierarchy install` once. It builds the native `ahd` into `<AH_HOME>/bin/ahd` and links it from `~/.local/bin/ahd`. The binary is every verb: `where` shows what it found (the binary, the plugin, the install stamp, the login state, ah's home), and `bin/ahd` only finds and runs it. Without it, run `<R>/bin/ahd <verb>`. Everything below applies to `ahd <verb>` exactly as it does to `<R>/bin/ahd <verb>`.

| Exit | Meaning |
|---|---|
| 0 | success (including "not running" for `stop` and `drain`) |
| 1 | the operation failed (`error.code` says why) |
| 2 | usage: unknown verb or flag, a missing or out-of-range argument |
| 3 | the daemon is down and the verb needs it (`health`; `drain --require`) |
| 4 | `check` found a mismatch |

#### `ahd start`

Reports the running daemon (never restarts it), else starts one and waits up to 3 s; an explicit start ignores the 60 s spawn-failed marker that backs hooks off, makes its one attempt, and rewrites the marker on failure or clears it on success.

*Changes state:* yes

#### `ahd stop`

Drains the daemon and waits up to 5 s; a hung one is stopped by a guarded SIGTERM; `not-running` when down.

*Changes state:* yes

#### `ahd restart`

`stop`, then `start`; reports the old and new pid.

*Changes state:* yes

#### `ahd drain [--require]`

Asks the daemon to exit and returns at once.

*Changes state:* yes

#### `ahd status`

Up/down, version, pid, uptime, root, `requests`, `errors` (non-2xx answers by code), `owns`, live registrations with adapter freshness, overdue count, per-session timer state, and the fallbacks to the files (count and last 10).

*Changes state:* no

#### `ahd health`

The raw `GET /v1/health`; exit 3 when down.

*Changes state:* no

#### `ahd version`

Client and daemon versions and the handshake verdict (`equal`, `client-newer`, `client-older`, `unsupported`), without acting on it.

*Changes state:* no

#### `ahd logs [-n N]`

The last N lines (1-1000, default 50) of `AH_HOME/logs/ahd.log`; there is no follow mode.

*Changes state:* no

#### `ahd heal [--force]`

Replaces the daemon only when it is proven hung: it probes `/v1/health` with a 10 s deadline and needs four misses in a row, 15 s apart, over at least 60 s, on a daemon that has been up 30 s, and it never signals before it has itself watched the daemon for 90 s. Any reply, however slow, ends the run with `ahd is not hung` and nothing is touched.

On a proof it copies the log tail to `logs/ahd-hung-<time>.log`, sends TERM, then KILL after 10 s if that same process is still there, starts a daemon the normal way and checks that it answers. A process it cannot confirm as the one it sent TERM to is not killed and nothing is started over it (`heal-failed-not-killed`, exit 1). One heal runs at a time: a second prints `heal in progress (pid N)` and exits 0.

At most 3 restarts in 24 h, the second at least 10 minutes after the first and the third at least 20 after the second; past that it exits 1 with `heal-capped`. A `run/heal-state.json` that cannot be read counts as capped too. `--force` skips that cap, never the proof, and only the user may pass it (a session is refused before anything runs, and so is a caller ah cannot identify: run it again).

Progress goes to stderr; plain text, no JSON. Exit 0: restarted, not hung, or a heal is already running; 1: capped, failed or refused. `ah ahd heal` is the same command, and the only `ahd` verb `ah` passes on.

*Changes state:* yes

#### `ahd inspect boxes [--address A]`

Per mailbox: directory, entries, unacked, overdue, heartbeat time and adapter.

*Changes state:* no

#### `ahd inspect entries --address A [--pending|--acked|--all] [-n N]`

The entries of one mailbox (default pending, the newest N, 1-200, default 200) with `route`, `acked` and `attempt` (the latest routing attempt, `null` when none).

*Changes state:* no

#### `ahd inspect registrations`

Live and stale registrations.

*Changes state:* no

#### `ahd inspect timers [--session S]`

Per Orchestrator session: its dispatches with check-in and silence deadlines and their `state` exactly as the dispatch clock's evaluation reports it, and the events the dispatch clock would fire now (a dry run: nothing is written or sent).

*Changes state:* no

#### `ahd inspect events [--address A]`

Unacked `from: ahd` event entries.

*Changes state:* no

#### `ahd inspect status --dir D`

The live status document of a hierarchy dir and its `rev` (`<D>/status.json` with `rev: null` when no daemon serves it).

*Changes state:* no

#### `ahd check`

Runs every read-only `inspect` verb and `status` once with `AH_DAEMON=off` and once against a daemon of its own, in a throwaway `/tmp/ahprobe.*` sandbox, and compares; exit 4 names the cases that differ. It never touches your daemon or `AH_HOME`.

*Changes state:* sandbox only

#### `ahd contract`

The participation contract for custom harnesses as JSON (the daemon's `GET /v1/contract` when one is up, else rendered from the same source); `--plain` renders the markdown guide, which is `docs/participation.md`.

*Changes state:* no

#### `ahd tui [--team NAME] [--once]`

A read-only terminal view of your teams, their agents, Sitrep and work items (see below); never starts the daemon; exit 3 when it is down.

*Changes state:* no

#### `ahd ui [--print] [--no-open]`

Opens the browser page of the daemon's view documents (see below): asks the daemon over its socket for a lease on a loopback listener, opens the page, and holds the lease until Ctrl-C or until the daemon closes it. Exit 0 a clean close, 2 the daemon is unreachable (or a usage error), 3 the daemon refused the lease.

*Changes state:* no

#### `ahd ui start [--no-open] [--json]`

Starts the browser page in the background and keeps it until `ahd ui stop`, across daemon restarts: the next daemon start brings it back, with no new link, and an open tab keeps reading. Prints the page's address and opens it.

*Changes state:* yes

#### `ahd ui restart [--no-open] [--json]`

`ui stop`, then `ui start`.

*Changes state:* yes

#### `ahd ui stop [--json]`

Stops the background page. The only command that ends it for good: `ahd stop` does not, and after `ui stop` no daemon start brings it back. It waits up to 10 s for a start that is running, then stops what that start brought.

*Changes state:* yes

#### `ahd ui status [--json]`

Whether the background page runs, its address and why it stopped; exit 3 when it does not run.

*Changes state:* no

#### `ahd where [--plain]`

The binary's path, version and build contract, the plugin it would run (path, source, version, contract), whether the two are `compatible` (equal contracts), and the login-item state. Writes nothing and contacts no daemon.

*Changes state:* no

#### `ahd login on|off|status|run [--plain] [--dry-run]`

The login item that starts the daemon at login. `--dry-run` applies to `on` and `off` only; `run` is what the login item executes and starts the daemon like `start`.

*Changes state:* `on`, `off`

#### `ahd home trust|untrust <path> [--hierarchy]`

The only writers of the two trust files, for a home directory other than the default (`--hierarchy`: a hierarchy directory). For the user, typed in their own terminal; agent sessions are refused.

*Changes state:* yes

#### `ahd --version`

Prints `ahd <version> (contract <n>)`; the contract number is the build's own compatibility number, not the wire contract in [Daemon API](daemon-api.md).

*Changes state:* no

#### `ahd state DOMAIN OP [--hook] [--budget MS]`

One write of the state hub, run in this process: `{"args", "context", "op_id"}` on stdin, the route's body on stdout. Exit 0 success (a digest conflict too), 1 the operation failed, 2 a bad request, an unknown operation or a body over its cap (`too_large`), 75 the domain lock did not clear, 70 any other failure.

*Changes state:* yes

#### `ahd hook EVENT`

Runs one hook unit with the harness's JSON on stdin; Claude Code runs it, a shell does not. A Bash command that invokes it (`ah hook`, `ahd hook`, `ah-hook`, also inside `sh -c` or a heredoc) is denied for every session by `pretooluse-hook-entry-gate` with `ah: ah hook is run by Claude Code, not from a shell.`, because the hook input's `session_id` is the identity a team rebind trusts.

The one exception is a test run: every invocation in the command carries `AH_TEST=1` and `AH_HOME=` an absolute `/tmp/ahprobe.<alnum>` or `/private/tmp/ahprobe.<alnum>` path, and a hook in test mode rebinds only team files whose real path is under such a directory and skips every other state write whose target is outside one, with a `test-mode write outside the sandbox skipped: <dir>` hook-errors row.

*Changes state:* yes


Only `start` and `restart` start a daemon. Read-only verbs never write. With a daemon down they answer
from the files, through the same functions the daemon uses, so up and down give the same JSON apart from
`source`, `rev` and the fields only a live daemon has. Every time a `ah msg` mailbox verb (`send`, `inbox`,
`ack`) falls back to the files it prints one stderr line, `ah msg: warning: ahd unavailable (<reason>); using
files`, and appends `{at, verb, reason}` to `AH_HOME/logs/fallback.jsonl` (rotated at 1 MB). `<reason>` is
`down`, `timeout`, or `http <status> <error code>` (`-` when the answer names none). A held append lock
(`423 busy`) is an answer, not a fallback. `ahd` reads that fall back say so with
`"source": "files"` and record nothing. `status` shows the daemon's `errors` (the count of 5xx answers) and
`errors_by_code` (every non-2xx answer, by code, each also recorded in `<state home>/logs/rejected.jsonl`: time, method, path, status, code, message, field, peer pid and client version, never the request body; one rotated generation at 1 MiB) and lists the fallbacks; `--plain` warns when one with a
reason other than `down` happened while the daemon was up.

### The terminal view (`ahd tui [--team <name>] [--once]`)

A read-only terminal client of the daemon's view API (`GET /v1/view`, a UI route kept out of the participant contract). It opens on your teams; Enter or a click opens a team's agents and Sitrep, then an agent's work items, and a work item opens a centered popup with its log. Esc goes back. Keys and mouse are on the [terminal view page](tui.md).

`--team <name>` opens on that team; an unknown name is an error, exit 2. `--once` (or `TERM=dumb`, or input or output that is not a terminal) prints one plain frame and exits: no escape bytes, so it can be piped. When the daemon is down (the connection fails) it prints `ahd is not running` and exits 3; interactively it says the same and retries, every 2 s at first and backing off to 60 s. When the daemon answers with an error it says so on stderr (exit 1) and does not call itself down. It never starts the daemon and never changes anything: it only reads `GET /v1/view`, `GET /v1/view/summary` and `GET /v1/health`. The text it shows (names, notes) has had control sequences stripped by the client. `NO_COLOR` is honoured. The terminal is restored on quit, SIGINT, SIGTERM, SIGHUP and a crash.


### The browser page (`ahd ui [--print] [--no-open]`)

The daemon serves the same view documents as `ahd tui` to a browser, on a listener it starts for the purpose: `127.0.0.1` only, a port that stays the same from one start to the next, no flag to change the bind. The first start takes an ephemeral port and `<state home>/run/ui-port` remembers it; each later start tries that port first and takes another ephemeral port when it is held, and that port is the one remembered from then on. Nothing listens until `ahd ui` asks, and the listener is closed again when the last `ahd ui` exits, after 15 minutes without a request to a `/ui/v1` route, or when the daemon stops; the port is free and `<state home>/run/ui.json` (`{v, port, lease_pid, started}`, mode 0600) is gone within 2 s of that. A bridge opened with `ahd ui start` returns after a daemon restart until `ahd ui stop`, and an open tab keeps reading across that restart.

- `ahd ui` requests a lease (`POST /v1/ui/lease` on the socket, which is the only place that route exists) and gets a port. It opens `http://127.0.0.1:<port>/ui/` with `open` (macOS) or `xdg-open` (Linux). There is no sign-in: any tab that opens that address is served while the bridge is up.
- The command is `ahd ui`. There is no `ah ui` (`ah ui` answers `unknown command 'ui'`), although the first line it prints starts with `ah ui:`; its error lines start `ahd ui:`.
- Without `--print` the command prints `ah ui: opened in your browser (port <port>); Ctrl-C closes it` and not the URL. `--print` prints the URL; `--no-open` prints it and opens nothing; with neither opener present it prints the URL as `--print` does. There is no `--host`, `--port` or `--bind`.
- A second `ahd ui` while one is live reuses the listener and adds a lease.
- When the remembered port was held, `ahd ui` and `ahd ui start` say so in one line on stderr: the browser keeps theme, filters and zoom per address, so those look missing on the new port. Saved looks and the active look are not per address: they live in `<state home>/looks.json`, outside the plugin, and a write refreshes the copy in `looks.json.bak` at most once an hour. A change the daemon has not confirmed waits in the browser's `ah-optics-pending` key and is replayed on the next load. When the page loads it merges the `ah-optics` looks of the address it is served from. Looks saved under an older port do not migrate on their own, because the browser keeps them under that port's address; `<ah> looks import <file>` takes such an `ah-optics` value from a file. A look deleted after that page merge stays deleted, and importing it from a file brings it back.
- `AH_UI=off` in the daemon's environment refuses every lease (exit 3, `browser UI disabled`).
- `POST /v1/ui/start` (on the socket only) has the daemon do what `ahd ui start` does and hand the page to the opener itself. The user needs no proof. A session is accepted only when the body is `{"token":"<64 lower-case hex characters>"}` and the token is the current one in `<state home>/run/band.token` (mode 0600, written at every daemon start and replaced after each accepted use, before the reply); any other caller, a wrong token or none gets `403 user_only` and the token stays. The reply is `{"status":"started"|"reused","opened":true|false}` and nothing else: the address and the port never reach the caller. The holder the daemon spawns takes its service lease because its parent is the daemon (pid and start time checked).
- `ah view open --kind web` is the client of that route: it reads the one JSON line `{"token":"…"}` from stdin (never an argument or the environment) and exits 0 `Web opened`, 2 for a usage error or no token (nothing is sent), 3 `Web did not open (<reason>); run \`ahd ui start\` in a terminal`, or 4 `web view can only be opened from the band button`. No tool call of any session may name `band.token`, and no content-mode Grep may be rooted at `<state home>/run` or a directory above it (the `pretooluse-band-token-gate`).
- `ah view open --kind tui --session <id>` needs `HERDR_PANE_ID` in its environment, else it prints `Run ahd tui in a terminal` and exits 3. It makes at most three herdr calls within 2 s: `pane get` on the pane recorded in `<state home>/run/view-tui/<id>` (alive: `agent focus`, or `tab focus` when herdr says that pane is no agent), otherwise `pane split --pane $HERDR_PANE_ID --direction right --focus` (the new pane id is read from `pane.pane_id` of its JSON answer) and `pane run <id> <ahd> tui`, then the id is recorded (mode 0600). Output it cannot read, a failed call or a missing `ahd` beside the binary is exit 3 `TUI did not open (<reason>); run ahd tui in a terminal`; nothing is retried and no pane is closed. Exit 0 prints `TUI opened` or `TUI focused`; exit 2 is a session id that is not a plain name.

The listener answers only `GET /ui/` and `GET /ui/<file>` (the page's `.html .js .css .svg .woff2` files from `<plugin root>/web/ui/`), `GET /ui/v1/view` (the daemon's `GET /v1/view` handler with the same query) and `GET`/`POST /ui/v1/looks` and `POST /ui/v1/looks/merge` (the looks file). Everything else, `/v1/*` included, is 404. Every request is checked in this order, and the first failure ends it: `Host` is exactly `127.0.0.1:<port>`, `localhost:<port>` or `[::1]:<port>` (421); `Origin`, if present, is exactly `http://` and one of those (403), on every request; `Sec-Fetch-Site`, if present, is `same-origin` or `none` (403); the route is in the list above (404; a wrong method is 405, and nothing is ever answered with `Access-Control-*`); a POST is `application/json` with an optional `; charset=utf-8` (415) and at most 4 KiB, 1 MiB on the looks routes (413). While the listener is closing every request is 503. Every response carries a strict `Content-Security-Policy`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`, and no `Server` header.

The Host and Origin checks are what keep other web pages in the browser, and DNS rebinding, from driving the daemon through the listener. They do not keep out a local process that sets its own headers. The bridge executes no action: it serves view documents and nothing else.

## Machine load

The daemon samples the machine once per bare-watch pass: the 1-minute load average per processor, the macOS memory-pressure level
(`normal`, `warn` or `critical`; `null` elsewhere or when unreadable), the number of Claude harness processes, and the API errors
(`transport`, `rate_limit`, `server` = `server_error` + `overloaded`, `other`) found in the last ten minutes in the transcripts of the
watched teams' registered sessions. It never stops, quits, holds or signals anything because of load.

The state is `overloaded` after `ticks` consecutive samples with a trigger, and `ok` again after `ticks` consecutive samples with every
trigger under 80% of its threshold (the memory trigger clears when the level is not `critical`). The API trigger needs `api_errors`
transport plus server errors from at least `api_error_sessions` distinct sessions, and stays on while at least 80% of `api_errors`
(rounded up) from at least that many sessions remain. A change of state mails each non-held team's Orchestrator once
(`machine-overloaded`, `machine-load-ok`); members and held teams are never mailed. The reported state and the time of the last admitted
spawn are kept in `<ah home>/run/load-state.json`, so a restart does not alarm again.

`<ah home>/ahd-load.json` (written by the user only; a member's write to it is denied) holds the thresholds; a missing file or key is
the default, and an invalid file gives every default and `load.problem` in `ah msg daemon status`:

| key | default | meaning |
|---|---|---|
| `load1_per_cpu` | `1.5` | load average per processor at or above which the machine is loaded |
| `mem_pressure` | `"critical"` | `"warn"` or `"critical"`: the level at or above which it is loaded |
| `api_errors` | `10` | transport + server API errors in ten minutes |
| `api_error_sessions` | `3` | distinct sessions those errors must come from |
| `ticks` | `3` | consecutive samples a change of state needs |
| `spawn_gap_s` | `300` | least seconds between two admitted spawns while overloaded |

`ah msg daemon status` (up) gains `load`: `{state, since, triggers, load1_per_cpu, mem_pressure, harnesses, api_errors_10m: {transport,
rate_limit, server, other, total, sessions}, thresholds, problem?}` (JSON), and a `load:` line in text; it is `null` when the daemon is down.

While overloaded, `roster spawn-one` and `roster spawn-ad-hoc` ask the daemon (`POST /v1/load/admit-spawn`, body `{team, member,
dry_run}`, answer `{admitted, next_at, triggers}`) and are admitted once per `spawn_gap_s`; otherwise they exit 75 with `machine
overloaded (<triggers>); next spawn allowed after <time>; nothing was launched`. `--dry-run` is answered but not recorded. With no
daemon (or an error from it) the spawn goes ahead and stderr says `load unknown`. Briefs, resumes, relaunches, `dismiss` and `quit`
are never paced.

## Self-care (any peer)

A role that has a shell only for self-care (the built-in Architect, and any custom role whose
registry row says `shell: "self-care"`) may run these commands and nothing else. A hook that runs
before every other Bash hook refuses the rest. It is a role-discipline tripwire, not a security
boundary: your permission settings still apply to every command, and the gate never approves one on
its own.

| CLI | verb | flags | conditions |
|---|---|---|---|
| roster | `checkin` | `--cwd`, `--team` | `--team` must be the team on this session's latest roster row that has one; never `--orchestrator-pid` |
| roster | `whoami` | `--cwd`, `--json`, `--team` | `--json` adds `authority: {mode, run, anchor}`, read live from the team's single open run anchor: `mode` is `surrogate` or `user` as its `authority:` line says, `none` with no open anchor, several, or no valid line. A `surrogate` line counts only when the Orchestrator session's recorded answer to the Authority question (the team file's `orchestrator.session_id`), read from the run anchor's `asks.jsonl`, says "Orchestrator acts for me (this run)"; that answer must have been recorded after the newest closed run anchor and no later than 10 minutes after this anchor was created; otherwise `mode` is `user`. Plain `whoami` prints no authority. See [Who may answer for you](shipping.md#who-may-answer-for-you). |
| roster | `status` | `--cwd` | no `--now`, no `--plain` |
| msg | `new` | `--type`, `--id`, `--to`, `--from`, `--req`, `--note`, `--cwd`, `--to-name`, `--from-name` | `--type response`; `--from` is your own role; either `--req`, an existing `*--request.md` inside this pool's `msgs/` or its team home's, or `--note <note entry id>` (letters, digits, `. _ -`) with no `--id` and no `--req` |
| msg | `list`, `index`, `show`, `downstream`, `roster` | `--cwd` | read-only (`show` without `--out`) |
| ahd-ctl | `status`, `health`, `version`, `logs`, `inspect <boxes\|entries\|registrations\|timers\|events\|status>` | `--cwd`, `--plain`, and each verb's own flags (`-n`, `--address`, `--session`, `--dir`, `--pending`, `--acked`, `--all`) | read-only; `start`, `stop`, `restart`, `drain` and `check` are refused |

The command must be one plain `<abs CLI path> <verb> --flag value ...` line: at most 1024
characters, tokens made only of letters, digits and `. _ / : @ + , -`, every flag followed by its
value (the value may not start with `-`), no flag twice, and `--cwd`, when given, equal to the
session's own directory. No quotes, `=`, pipes, redirection or command prefixes. A plugin path or
working directory containing a space makes self-care unavailable. A subagent of a gated role gets no
shell at all; other subagents (for example a retrieval subagent) are untouched.

## Team home and teardown

**One home.** A team file lives in the hierarchy dir of the repo's main checkout
(`<main>/.claude/ah2/teams/<t>.json`), whatever `--cwd` `spawn-one`, `spawn-ad-hoc`,
`create --spawn` or `create --commit` is given, including a linked worktree. Run from a worktree,
`--cwd <worktree>` and `--cwd <main>` reach the same file, so a team's members can no longer be split
across two dirs. Under `WRANGL_HIERARCHY_DIR`, or outside git, the home is that dir, as before. A
team recorded before this change in a worktree's own dir is read and updated there, in place; nothing
is moved. Worktrees of one repo now share team names, the default `team.json` included: a second
live orchestrator in a sibling worktree that picks a live name meets the existing second-team and
ownership refusals instead of silently getting a private team. See [team-file.md](team-file.md).

**What `disband`, `dismiss` and `teams` read.** The team file from its home (plus a legacy copy in
the invoking worktree's dir; if both exist, both are read and their members merged), `peers.jsonl`
from the invoking dir, the home, and each member's own `expected_root` dir, and `herdr agent list`.

**Strays.** Under herdr, a pane that is not in the team record is closed too, flagged `stray: true`
and listed in `strays`, when all of these hold: its name parses as `<scope prefix>-<role>` (custom
roles included), the role is not `orchestrator`, it is not the invoking pane, it is not a member of
any other readable team file, and its cwd is in this checkout or one of its worktrees. Strays are in
the plan and close with `--close`. **Residual risk:** a stray is matched
by name and checkout only. A live agent from another orchestrator in the same repo or its worktrees
that carries the same name prefix would be closed too. The plan and the close both say so (the stray warning below).

**Verified close.** Before each herdr close the pane is re-queried; one that now carries a different
name is skipped (with the renamed-pane warning below), and one whose re-query fails is skipped too, with a "could not be re-checked" warning instead. After the close the targets are re-queried; `closed` is true only when all are
gone and `not_closable` is empty. Nothing short of that reads as success.

**Warnings.** Each is a sentence naming counts and sources, in `warnings`, on plans and closes:

| | Raised when |
|---|---|
| W1 | no team record for the team in its home (or the worktree dir), but live members were found through other sources |
| W2 | `strays` is non-empty: names them and states the residual risk above |
| W3 | the team has records in both the home and a worktree dir (left by an older release); both were read |
| W4 | the team belongs to another live orchestrator; its members were left alone |
| W5 | a pane now carries a different name than the target; it was not closed |
| W6 | herdr or tmux could not be queried after the close; those targets count as unverified, not closed |
| W7 | after the close, some targets are still live: names them |
| W8 | members have no pane (`not_closable`) and must be closed by hand; text begins `W8:`. On `disband` plans and closes, and on the `dismiss` plan (not `dismiss --close`, which exits 2 for such a target) |

Every one must be shown to the user; never report a teardown as done unless `closed` is true and
`still_live` is empty.

**Spawn warnings.** `spawn-one`, `spawn-ad-hoc` and `create` add to `warnings` (never block) when
the team's file exists in both the home and a worktree dir, or when `--cwd` is in a different checkout
from the calling shell's cwd ("team `<t>` is recorded in `<other>`; teardown run from `<this>` will not
see it"). The second is a heuristic: the shell's cwd is taken to be the orchestrator's.

### Release check

`dismiss --close` and `disband --close` hold a member while it is working now, since closing it loses paid work, and every
member while the message store cannot be read. Every other member closes, and `release.warnings` (and `release.summary`) name what it leaves behind. Closing a pane does not touch
the work tree. Nothing is stored; each run reads the member's activity record, the open requests and its work tree. Statuses:

| status | meaning | closes |
|---|---|---|
| `busy` | its activity record says working, blocked, or waiting on a background task, whatever its liveness reads | no |
| `ready` | idle, no open request, a clean tree, nothing unshipped | yes |
| `not-running` | the session is not running (no live peers row) and its work tree is clean; unshipped commits warn | yes |
| `not-running-dirty` | the session is not running and its work tree has uncommitted changes | yes, warns |
| `liveness-unknown` | its liveness cannot be decided | yes, warns |
| `open-request` | idle, with a request addressed to it that has no response | yes, warns |
| `dirty` | idle, with uncommitted changes; untracked files count | yes, warns |
| `ahead` | idle, with commits on neither the run's branch nor the release ref; the warning gives the count, branch and sha | yes, warns |
| `stale` | no work tree, or git could not read it or could not tell what is unshipped | yes, warns |
| `store-unreadable` | the message store cannot be read, so nothing can say the member is not working: wait, or name it with `--ignore-release <name>` | no |
| `ignored` | a held member named by `--ignore-release <name>`; the detail keeps why it was held | yes, warns |

The release ref is the project's golden branch: `git config ah.releaseRef`, then `origin/HEAD`, then `origin/main` and the rest of that order. A status is the first that applies; the detail lists all of them.

A `busy` hold clears when the member finishes a turn; a member that was interrupted with Esc and never reached its Stop keeps the old `working` record until its next turn ends; `--ignore-release <name>` closes it.

The work tree is the member's recorded work root, else the work root of its `expected_root`; a member with no team row
uses the top level of the checkout holding the `cwd` on its latest peers row. A close that holds members closes the rest,
lists the held ones under `held`, and exits 3. `--ignore-release <name>` closes the member it names through a `busy` or `store-unreadable` hold, with a warning. Without a name it overrides nothing. A sub-orchestrator may not pass it.

**Known limit.** Run from the **main** checkout, a legacy team recorded only in a worktree's own dir is
not seen. Every team written from 0.113.0 on is in the home. Run `disband` from that worktree, or
`--cwd <worktree>`.

Flags are validated per verb: `spawn-one`, `spawn-ad-hoc`, `adopt`, `checkin`, `whoami`, `dismiss`,
`disband`, `untrack`, `resync`, `move`, `transfer`, `reap`, `deliver`, `answer`, `tier`, `roster`, `pack`, `stream-open`, `stream-status`,
`stream-done` and `stream-label` reject any flag not in their own set, so
the lists above are exhaustive for those verbs rather than indicative.

`--team @default` names the default team (`team.json`) as explicitly as any named team, in every verb that
takes `--team` and in `ah msg`: no owned-team fallback, no refusal for several. No team can be named
`@default`, and any other `@` value is refused. Wherever a team name is printed to pass back (refusal
lists, the owned-teams lead line, the directive's commands), the default team shows as `@default`;
`teams --json` keeps `name: null` for it.
`--team <t>` names a live team, never a roster: `show`, `init`, `add`, `edit` and `remove` refuse it
and point at `--roster <r>`. On team verbs, a session that owns exactly one live team and passes no
`--team` acts on that team. Owning several, a verb that names a member (`dismiss <name>`, `untrack <name>`,
`move <name>`, `deliver <name>`, `answer <name>`, `spawn-one`/`spawn-ad-hoc --member <name>`) takes the team
whose members include that name among the owned ones (recorded member names only: not a pane id or
session id). Every other team verb — `create`, `disband`, `resync`, `untrack --all`, `stream-open`,
`stream-status`, `stream-done`, `stream-label`, and `spawn-one <role>` or `spawn-ad-hoc <role>` without
`--member` — refuses, exit 2, listing the owned teams; a named member that no owned team holds, or
that more than one holds, adds that to the message. An explicit `--team <t>` is taken as given. Verbs
that don't act on one team (`teams`, `doctor`, `whoami`, `checkin`, `adopt`, `tier`, `reap`, `history`,
`show` and the other roster verbs) are unaffected. The default team (a legacy `team.json`) counts as
owned, and is `--team @default`. For an owner verb that acts on an existing team (`spawn-one`,
`spawn-ad-hoc`, `dismiss`, `disband`, `untrack`, `stream-open`, `stream-done`, `progress`), ownership is proved by
process ancestry: the nearest registered session above the `ah` process, matched by pid and start time,
must be the Orchestrator the team records, or the verb refuses with `not-team-owner`; `--session` is not
read for this. A process that is not itself a registered session counts as the registered session above it, so a
child of a sub-orchestrator's shell holds that sub-orchestrator's carve on its own streams until the child registers
as a member of its own. For the other team verbs and the `own` field of `teams`, a session owns a team when its
session id matches the one the team file records, or its pid matches the recorded orchestrator pid and no
two known session ids differ; a dead pid proves nothing, and ownership never checks that the owner is
alive. The CLI reads the session id from `--session` where the verb takes one. A team records a session id when `create --commit
--session <id>` wrote it, or when a team verb (`create --commit`, `spawn-one`, `spawn-ad-hoc`,
`adopt`) proves the session of the live process: a daemon registration bound to that pid, or a
`session_pids` row (state database) for it written since the process started. A session id that is only stated
(`--session` or `CLAUDE_CODE_SESSION_ID`) is recorded by those verbs only when one of those proofs
names the same id. A verb that cannot prove it adds a `warnings` entry
(`orchestrator session unknown: …`) and records nothing. A `--resume`d session keeps the
session id its team files record, so the mesh gate still recognises it and its first Stop registers it again;
the team of the same session takes the new process at that session's SessionStart (and at its Stop), so
ownership by pid follows it. A different session, or a team no such restart reached, is re-claimed with
`adopt --orchestrator-pid <its pid> [--team <t>]`, which `ah roster` ownership by pid, the orchestrator-liveness
checks and the backfill above wait for. `adopt`
records the session proven for that pid, or clears the recorded one, and it is one call per team. The `own`
field of `teams` uses the same rule. A `create --commit --session <id>` over a team file that already records
another session replaces it only when that id is proven for the process; otherwise the recorded one stays and
a warning says so. `create --commit`, `spawn-one`, `spawn-ad-hoc`
and `create --spawn` also print a `registered` array of the mailbox addresses they registered.
`reap` judges a team by its recorded pid alone, so a team that only a session id owns, with a dead
pid, is an orphan to `reap --commit` (it deletes the team file unless live members are attributed to
it): `adopt` first. `ah msg new` without `--team`, owning several, takes the team whose members include
`--to-name` and otherwise refuses and writes nothing; `ah msg list` covers every owned team, and each
row carries its `team` (`team=<name>` in `--plain`); `ah msg roster` prints every owned team's table,
each under a line naming the team and its roster (JSON: `teams`). The removed `alias` and `layout`
verbs exit 2 naming their replacements (`create --team`, `create --mode`), as does `init --layout`.

**Which roster a command uses.** `show`, `init`, `add`, `edit`, `remove` and `create` take the first
of: `--roster <r>`; `WRANGL_ROSTER`, when set and not empty; `activeRoster`, a top-level config key read
most specific level first (repo-user, repo, global — from a worktree, the main checkout's files
too); else the default `roster` block. In each, `default` means the unnamed `roster` block, so a
narrower level or one session can undo a wider selection; `init --roster default` and
`roster copy <src> default` are refused. `create --from` ignores the selection, and every other verb
acts on its team's recorded roster. Hooks resolve a session's roster the same way, except that a team
file in scope wins over `WRANGL_ROSTER` and `activeRoster` — even one recording the default block. A
global file holding only `activeRoster` (with or without `teamLayout` and `modelTiers`), and a repo or
repo-user file whose only key is `activeRoster`, configure nothing; their selection still applies.
Otherwise `activeRoster` never changes whether a file counts as configured. An `activeRoster` that is empty or not a string is refused as not a roster name. `show`, `roster list` and `roster use` carry the selection, and so does `create`'s plan
when something is selected (with nothing selected, the plan is unchanged):

    selection: { roster: <name or "default">, source: "flag"|"env"|"activeRoster"|"team"|"default", level: <level or null>, path: <path or null> }

`level` and `path` are set only for source `activeRoster`. A selection from `--roster`, `WRANGL_ROSTER`
or `activeRoster` that names a roster no level defines (a `rosters.<name>` key, even one with no
members) makes every one of those verbs but `init` exit 2, naming the source, the defined rosters
and the fixes (`roster use default`, `roster use <other>`, `init --roster <name>`); `init` creates
it. Hooks use the default block instead and add that message to the session's warnings, and
`doctor` adds a red `roster-selection` row. `create` still refuses a selected roster with no
members, and records the selected key in the team file's `roster` (null for `default`).

**`team-name-unusable`.** When the name a team would be created under cannot be used — it fails the
team-name rule on any transport, or, under herdr, some pane-routed member name it derives breaks
Herdr's `[a-z][a-z0-9_-]{0,31}` — `create` (every phase), and `spawn-one` / `spawn-ad-hoc` when they
would create a team, exit 2 with this JSON on stdout and its `message` on stderr, having launched
and written nothing: `ok: false`, `refused: "team-name-unusable"`, `needs_user_choice`, `verb`,
`name`, `name_source` (`basename` \| `legacy-team` \| `explicit`), `transport`, `why`,
`failing_member: {name, length}\|null`, `suggestion` (a name to OFFER, never applied), `suggestion_why`
(when there is no suggestion), `rerun` (the same command with a literal `--team <TEAM>`, `null` when
`needs_user_choice` is false) and `message`. The name is the user's choice: ask, then re-run with
`<TEAM>` replaced by their answer.

**`member-model-undefined`.** When a claude-kind member that `create --spawn`, `spawn-one` or
`spawn-ad-hoc` would launch (`--dry-run` included) has no model — none stored, none given with
`--member-model` / `--model` — the verb exits 2 the same way, after the team-name check and before
anything is launched or written: `ok: false`, `refused: "member-model-undefined"`, `verb`, `members`
(roster order: `{name, role, class, allowed, fallback}`), `rerun`, `rerun_fallback` and `message`.
`allowed` is the class's model allowlist. A member of a kind with a model mapping (today: codex) is
asked for too: it also carries `kind` and `models_command` (codex: `codex debug models`), its
`allowed` is `null` (any model that harness accepts) — or, for an advise-class member, the models of
its kind declared `opus` or `fable` — and its `fallback` is always `null`: models do not carry
across harnesses. `fallback` is `{model, from}` — the highest-tier model a
claude-kind design, review or implement member holds that the class allows (`inherit` never counts),
first in roster order on a tie — or `null`; a legwork member never borrows one, and is listed only
when task-gopher is not installed or `--no-legwork-handoff` is given (otherwise it is skipped, as
under `create` above; `spawn-one` and `spawn-ad-hoc` launch no legwork role). `rerun` is the same command
with one `<MODEL>` flag per listed member (`--member-model <name>=<MODEL>`; `--member <name> --model
<MODEL>`; `--model <MODEL>`). `rerun_fallback` applies every fallback, and is `null` when any is. The
model is the user's choice: ask, then re-run `rerun` with each `<MODEL>` replaced; only a top-level
session that cannot ask runs `rerun_fallback`, and a subagent never does.

**`model` on a non-claude kind.** A kind with a model mapping (today: codex, `--model <M>`) takes a
`model` on `add`, `edit`, `spawn-one --model`, `create --member-model` and `spawn-ad-hoc --model`,
checked for shape only (no whitespace or control characters) — the harness checks the name at the
first turn. `model` together with a model flag in `args` (codex: `--model`, `-m`, `-c model=…`) is a
hard error, as is `approvals_reviewer` in a codex member's `args`, and any `args` on a non-claude
advise-class member. A kind with no mapping rejects `model`: pass its own flag in `args`. A
non-claude chain member whose auto-mode leaves a read-only sandbox (codex `manual`, `plan`) gets a
warning at `add`, `edit` and spawn: it cannot write its report without an approval in its pane.

**Spawning a non-claude member.** Every spawn writes its standing instructions to
`<hierarchy dir>/instructions/<name>.md` (identity, the role's agent body, how a brief and a report
work outside Claude Code) and, for codex, launches it with `--model <M>`,
`-c model_instructions_file="<that file>"`, `--add-dir <message pool>` when the pool is outside the
cwd, and `-c approvals_reviewer="user"`, ahead of the auto-mode flags and `args`. A member that stops
at a prompt it recognises (trust dialog, sign-in, approval) is launched with a `blocked` object of
`deliver`'s shape to relay; spawn never sends a key.

**`name-in-use`.** When Herdr refuses to start a member because another Herdr agent holds its name
(`agent_name_taken`), `spawn-one` and `spawn-ad-hoc` exit 2 with `ok: false`,
`refused: "name-in-use"`, `name`, `rerun` (the same command plus `--names-in-use <name>`), `detail`
and `message`, having closed the empty pane and written nothing. It is not a launch failure.
`create --spawn` reports it on that member's `launch_result`.

**Per-member refusals under `create --spawn`.** `name-in-use`, `harness-cwd-untrusted` and
`agent-file-not-found` fail that member alone: `launch_status: "failed"` with
`launch_result: {reason: "refused", refused: "<name>", …the refusal's fields}`. They are not launch
failures.

**`agent-file-not-found`.** A non-claude member's contract is a built-in role's `agents/<role>.md` in
the running plugin, or a custom role's agent file found by the role resolver. When it is missing,
spawn exits 2 before any pane opens with `ok: false`, `refused: "agent-file-not-found"`, `member`,
`role`, `ref` and `message`.

**`harness-cwd-untrusted`.** When Codex's own config (`$CODEX_HOME/config.toml`, else
`~/.codex/config.toml`, read-only) says the cwd is not trusted and, once the member is ready, no
prompt is recognised on its screen, spawn closes the pane it opened and exits 2 with `ok: false`,
`refused: "harness-cwd-untrusted"`, `member`, `kind`, `cwd` and `message`. Trusting a directory is
the user's decision: they run `codex` there once and answer its question.

**`advise-model-tier`.** A non-claude advise-class member whose model is not declared `opus` or
`fable` is refused before anything is launched or written: `ok: false`,
`refused: "advise-model-tier"`, `member`, `kind`, `model`, `declared_tier` (`null` or the declared
tier), `needs: ["opus","fable"]`, `rerun_declare` (the `tier set` command with `<TIER>`) and
`message`. A kind with no model mapping can never be declared: `model`, `declared_tier` and
`rerun_declare` are `null`, and `add`/`edit`/`spawn-ad-hoc` reject such a member outright.

**Stale keys and `migrated`.** Keys that do nothing any more are ignored, reported by `status`,
`doctor` and every `create` phase (never in session context), and migrated by the next CLI write
to their file — `init`, `add`, `edit`, `remove`, `role set`, `role remove`, `untrack`/`dismiss
--also-config`, and `create`'s stored `--mode`. Chain-role subagent opt-ins: a top-level `route`
other than `peers` (deleted), a chain role's `dispatch:"model"` (deleted), a chain member's
`route:"subagent"` (deleted), `onMissing` `never`/`prompt` (deleted), and a block `route:"subagent"`
(becomes `peer`; each legwork member that inherited it gets its own `route:"subagent"`). Team keys
a roster no longer holds: `teamAlias`, a block's `layout`, and a `teamLayout` outside the global
file (deleted). A roster `members` element that is not an object (null, an array, a string, a number,
a boolean) is ignored by every reader — reported in `warnings` by `status`, `doctor` and `create` as
``roster `<file>`: `members[<i>]` is not an object (`<type>`); ignored`` — and deleted on the next
write. Only the file being written changes; other keys and their order are kept. The
verb's output then carries `migrated: [{path, key, from, to}]` (`to` is `null` for a deletion),
with one stderr notice per file. `gates.jsonl` is never rewritten: a stale `route` record there is
simply not read.

`/wrangl:agent-team` (teams), `/wrangl:agent-roster` (the roster template) and `/wrangl:agent-role` (custom roles) are the skills that drive these
verbs; their SKILL.md files are the operational protocol, this file is the surface.


### Checkpoint

`ah roster checkpoint` reuses the release check's answer grammar under the `Checkpoint:` prefix: the first `Checkpoint:` line under
`## [1] status` of the member's response, outside a code fence, is the answer (`Checkpoint: ready branch=<b> sha=<7-40 hex> inflight=none` or
`Checkpoint: not-ready reason=<text>`); a `Release:` line is not a checkpoint answer, and the reverse. The release check's behaviour is unchanged.
A member asked to checkpoint commits on its own branch as `wip(resume): <request slug or "idle">`, does not push, stops every background
process it started, and stops working until it is resumed. `--status` reports `safe_to_restart` as described in the verb's row above.
