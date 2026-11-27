# Troubleshooting

This page is for when something in Wrangl does not work as you expect. Start with the self-check below, then find your
symptom in the tables. Each row says the likely cause and what to run. The sections after the tables explain the longer
cases.

## Start here: run the self-check

In a terminal, or in Claude Code with a leading `!`:

```
ah roster doctor --brief
```

It prints one line per check: `ok <name>` when the check passes, or the status, the name and a short reason when it
does not. It changes nothing. A red row names its own fix.

| Form | Use it for |
|---|---|
| `ah roster doctor --brief` | reading the result yourself |
| `ah roster doctor` | the full result as JSON, one row per check, to attach to a bug report |
| `ah roster doctor --check` | a pass or fail: exit 1 when any row is red |

The checks cover the version and install path, the session, the configuration, your teams and members, hook errors in
the last 24 hours, the daemon and more.

If you report a problem, open an issue at <https://github.com/agentic-tool-labs/wrangl/issues> and include the output
of `ah roster doctor`.

## Install and setup

| Symptom | Likely cause | What to do |
|---|---|---|
| `ah` or `ahd` is not found in a terminal | the commands are on the PATH only inside Claude Code sessions until they are linked | run `/wrangl:hierarchy install` once; it links them from `~/.local/bin`. Add that folder to your `PATH`. `ahd where` shows what it found |
| `ah` says `no native binary is installed`, and Wrangl's hooks do nothing | Wrangl is installed but its program is not built yet. Until it is, the hooks let every tool call through and log an error | run `sh <plugin root>/scripts/install.sh --apply` in a terminal, then start a new session |
| `scripts/install.sh` says `No such file or directory` | the plugin was not updated first | [update the plugin, then run it again](#the-install-script-says-no-such-file-or-directory) |
| A hook seems to do nothing | a hook failed; Claude Code shows that only under `--debug` | the `hook-errors` row of `ah roster doctor`, or read `~/.claude/ah2/hierarchy/hook-errors.jsonl` |
| A permission prompt appears for `…/bin/ah roster` | the plugin's allow hook did not recognise the command | [Permission prompts for `ah`](#permission-prompts-for-ah) |
| `ah` (v1) still answers in one project | that repository's `.claude/settings.json` turns v1 back on under `enabledPlugins` | remove `ah@agent-tools` there; see [Wrangl and ah v1](../README.md#ah2-and-ah-v1) |

## Teams and sessions

| Symptom | Likely cause | What to do |
|---|---|---|
| The team looks gone after a reboot | the running sessions did not survive; the team's records, briefs, undelivered mail and snapshots did | start Claude Code in the repository, list teams with `ah roster teams`, then `ah roster resume --team <name> --yes`. See [Recovery](recovery.md) |
| The team is orphaned: the Orchestrator session died | the team record points at a process that is gone | from your new session: `ah roster adopt --orchestrator-pid <this session's pid> --team <name>`. It refuses a team that another live session still holds |
| `ah: this verb runs only through the orchestrator's session`, right after `resume --yes` | the team was resumed by an older build | [After an upgrade, a resume or the gate](#after-an-upgrade-a-resume-or-the-gate) |
| A command says `this session owns N live teams (…) — pass --team <name> to say which` | you own several teams and the command does not say which one | add `--team <name>`; see [Several owned teams](#several-owned-teams) |
| A lane child is refused with `lane-owner-unproven` ("links to a lane … that no longer exists") although its lane lead is live | the lane's link names an earlier record of its parent team | after checking it is the same lane, the parent's owner runs `ah roster recover --team <parent> --relink-lane <child team>` |
| A lane lead is refused with `… may run only <verbs> …` | the command is one only the top Orchestrator may run | ask the top Orchestrator to run it |
| A member is offline, or a dispatch says there is no live peer | the member's session has ended | `/wrangl:hierarchy peers` to see who is up; `ah roster spawn-one <role>` to start just that role again |
| The Orchestrator is woken for a member "BLOCKED at a prompt" | a member sat at a permission dialog or a question; members never wait for you | the Orchestrator cancels it with `ah roster answer <name> --cancel` and tells you. A cancel that answers `still-blocked` leaves the dialog up: the Orchestrator passes it to you |
| A member "stopped on an API error" and was not resumed | the weekly usage limit was reached, the member is not a Claude session, or it is a subagent. Any other API error is resumed by itself | the Orchestrator tells you in one line; resume it only when you decide to |
| A team name is refused or collides, or `create` finds a team you did not make, from another work tree | work trees of one repository share one set of team names | pick another `--team <name>`, or use the existing team's owner |
| `closed: false` with `not_closable`, or a `W8:` warning | those members have no pane Wrangl can close (for example a member started in a plain terminal) | close them by hand; `disband` and `dismiss` cannot |
| A disband warns it will close agents "not in the team record" | herdr panes named `<team>-<role>` in this repository or its work trees are treated as strays and closed | read the listed names in the plan before you confirm. Another Orchestrator's member with the same name would be closed too |
| `disband` in a work tree finds no team | a team recorded by an older release only in a work tree's own folder is not seen from the main checkout | run `ah roster disband --cwd <that work tree>`; see [Team home and teardown](cli-tools.md#team-home-and-teardown) |
| Two checkouts, or a work tree and the main checkout, see different rosters | a work tree resolves its own roster | point `WRANGL_HIERARCHY_DIR` at the same folder in both if you want them joined |
| Members in different repositories cannot message each other | each repository has its own hierarchy folder | set `WRANGL_HIERARCHY_DIR` to the same path in both sessions |

## Mail and messages

| Symptom | Likely cause | What to do |
|---|---|---|
| Mail was sent but the recipient never saw it | it is still on the delivery ladder, or the recipient is blocked or has failed | `ah msg inbox --address <name@team>` shows the step it is on and what comes next. See [Mail](mail.md) |
| A member finished and wrote its report but you never heard | the member ended its turn without sending the mail | the Orchestrator's next turn, an idle notice or the daemon's mail brings it up. If nothing arrives, check `ah msg daemon status` |
| `ah msg: warning: ahd unavailable (down); using files` | the daemon is not running; mail still works from the files, but it is not chased and links do not expire by themselves | `ahd start`, then see [The daemon](#the-daemon) below |
| A report is denied with "not in its request's directory" or "outside this session's message pool" | the report file is not beside the brief it answers | [Response pointers](#response-pointers) |
| No alarm reached you while the Orchestrator was dead | the Orchestrator's own alarms sit in its mailbox; only your `alarm.command` (user config) reaches you then, and it may be unset | the `alarm` row of `ah roster doctor`; set `alarm.command` in your user config, then run `ah alarm test` |

## The daemon and the views

| Symptom | Likely cause | What to do |
|---|---|---|
| `ahd status` says `daemon down` | the daemon is not running | `ahd start`; `ahd logs -n 50` says why if it will not start |
| `ahd status` waits and then says the daemon does not answer, or a write fails with `database busy … ahd pid N may be hung` | the daemon is hung | `ahd heal`; see [The daemon](#the-daemon) |
| `ah roster doctor` has a red `daemon-contract` row | the running daemon is older than the hooks | `ahd restart` |
| `ahd tui` shows `ahd is not running` | the daemon is down; the view never starts it | `ahd start` in another terminal; the view reconnects by itself |
| A custom agent program that asked to join gets `no_orchestrator`, `join_cooldown` or nothing | no live Orchestrator for that team; a request was denied or expired less than ten minutes ago; or the Orchestrator has not answered yet (an undecided request expires after ten minutes) | start the team's Orchestrator, wait out the ten minutes, or answer the Orchestrator's question. See [Bringing your own agent runtime](daemon.md#bringing-your-own-agent-runtime) |
| The usage report shows nothing, or less than expected | the plain report counts subagents only; a peer is its own session, not a subagent | `ah usage-report --team <name>` counts every session the team had; `ah usage-report --fleet` totals every team |

## Rosters and roles

| Symptom | Likely cause | What to do |
|---|---|---|
| The roster looks stale, or the wrong members appear | a roster at a more specific level replaces the broader one entirely; levels do not merge | `ah roster show --level <level>` to inspect each level (see [Rosters](rosters.md)); `ah roster resync` to re-read the live layout |
| `… selects roster "<name>", but there is no rosters.<name> block at any level` | `--roster`, `WRANGL_ROSTER` or `activeRoster` names a roster that no level defines | [Named rosters](#named-rosters) |
| A custom or pack role is missing, or `ah roster role list` shows `UNAVAILABLE` with a `pack-…` reason | the pack is missing or broken, two installs of it differ, or the selected roster doesn't name it | [Role packs](#role-packs) |

## The daemon

The daemon (`ahd`) is optional, so most trouble with it is "it is not running", which costs nothing but speed. The
full list is in [The daemon](daemon.md#troubleshooting).

- **Daemon down.** `ahd status` says `daemon down`. Run `ahd start`; `ahd logs -n 50` says why if it will not.
- **Daemon hung.** `ahd status` waits and then says the daemon does not answer, or an `ah` write fails with
  `database busy … ahd pid N may be hung`. Run `ahd heal`. It probes the daemon for at least a minute and a half and
  replaces it only when no probe is answered. A daemon that is merely slow is left alone, and the command says so.

  Often there is nothing to run: a failed write and the Orchestrator's overdue timer start the same heal by
  themselves in the background. Wait a minute or two and try again. The `ahd-heal` row of `ah roster doctor` says what
  the last heal did, and its log is in the `logs/` folder of the Wrangl home (`~/.claude/ah2` unless you set
  `AH_HOME`), as `heal-<time>.log`. The hung daemon's own log is kept there as `ahd-hung-<time>.log`.

  Heal restarts the daemon at most three times in 24 hours. When the row says `capped`, or that
  `run/heal-state.json` cannot be read, run `ahd heal --force` yourself in a terminal. A Claude session cannot run it.
- **`ah msg: warning: ahd unavailable (<reason>); using files`.** The message reached the mailbox files directly and
  will be seen when the recipient next looks. `ahd status` lists the recent fallbacks.
- **Mail stuck on one step of the delivery ladder.** `ah msg inbox --address <name@team>` shows the step and when the
  next one is due. A member blocked on a prompt holds it, and one that has failed pauses it.
- **Version mismatch.** `ahd version` prints `verdict: equal`, `client-newer`, `client-older` or `unsupported`. After
  you upgrade the plugin, the next mailbox command (`ah msg send`, `inbox`, `ack` and the like) stops the older daemon
  and starts the new one. `ahd version`, `ahd status` and `ahd start` only report the mismatch. To replace it at once,
  run `ahd restart`. A link to `ahd` you made by hand to an old plugin folder keeps restarting the old version until
  `/wrangl:hierarchy install` replaces it.

**A member never receives mail.** Look at `ahd inspect boxes` for the member's mailbox (entries, unacked, overdue, last
heartbeat), then `ah msg inbox --address <name@team>` for the step its entries are on. A member with no recorded pane
cannot have mail typed into it, so its mail goes to the Orchestrator at once instead of waiting; a member whose row
says `paste: false` is the same.

**A member that stopped on an API error.** The daemon resumes it with one urgent `resume` message in the member's own
mailbox, delivered the ordinary way. A newer resume of the same session withdraws an older one that is still unread, a
resume left unread for 6 hours is withdrawn, and `ah roster quit` withdraws a member's pending resumes. The Orchestrator
is told of each member resume once, with a quiet `member-resume` note. Errors the daemon does not resume are reported
to the Orchestrator.

**A turn ended on an API error and nothing resumed it.** The file `logs/turn-ends.jsonl` in the Wrangl home has one
line for each failed turn end that was found and for each resume decision (a normal turn end writes nothing). Each line
has the time, the session, the error kind and an `action`: `recorded`, `resumed`, `held:outage`, `skipped:<reason>`
(`subagent`, `no-session`, `backoff`, `dedupe` or `no-resume`) or `error:<message>`. One turn end is resumed once,
even when it is found twice. The file rotates to `.1` past 1 MB.

## The ah CLI

### Permission prompts for `ah`

Wrangl's own hook approves its `ah roster` and `ah msg` commands, so they raise no prompt. If one prompts anyway, either plugin hooks are off in this session, or the
path you ran is not the installed one (a copy under `/tmp`, or your own checkout while the installed plugin lives in
the plugin cache). Each session prints an `ah CLI root:` line; run that exact path. A command with a `cd … &&` prefix,
a pipe, a `$VAR` or a redirection is not recognised either, by design. Without plugin hooks, add the allow rules in
[Permission](cli-tools.md#permission). A `dismiss --close` or `disband --close` always prompts: that prompt is the
close gate, and it is meant to.

**"`cannot resolve this session's pid — CLAUDE_PID is unset`."** The command is not running inside a Claude Code Bash
tool (a plain terminal, a CI job, a wrapper that strips the environment). Pass `--orchestrator-pid <pid>` yourself.

**"`…/bin/ah: No such file or directory`."** The version folder your session was told about was removed by an update.
Old and new version folders normally live side by side, so the old command keeps working until the session ends. When
it does not, start a new session and use the `ah CLI root:` line it prints.

## After an upgrade, a resume or the gate

How to do these in the first place: [Upgrading](upgrading.md) and [Recovery](recovery.md).

**"`ah: this verb runs only through the orchestrator's session: ask the orchestrator to run it.`"** Some commands, the
owner commands, run only from the team's Orchestrator session, and Wrangl checks that by looking at the processes. A
member, a plain shell or another session is refused, and a second line, `owner proof: …`, says which step failed. Run
`ah roster resume --team <name> --yes` from inside the Orchestrator's own Claude session, and the command then works
in that same session. `resume` refuses from a plain terminal (`owner-unproven`) and from a member's session
(`member-cannot-resume`); see [Recovery](recovery.md#bring-the-members-back-resume).

An older build's `resume --yes` did not record when the session that ran it started, so that session was not
recognised right after the resume. If an older build resumed your team, either run
`ah roster resume --team <name> --yes` once more in the Orchestrator's session, or end the session and start it again
with `claude --resume <session id>`. The team follows the new process and the command then works.

**`ah roster doctor` shows `daemon-contract` red.** The running daemon is older than the hooks, so a relayed message
could show twice. Run `ahd restart`, then run `ah roster doctor` again. After every rebuild of Wrangl's program the
daemon needs this, unless `install.sh` already restarted it.

### The install script says "No such file or directory"

`sh …/scripts/install.sh --apply --rebuild-only` says `No such file or directory` when the version folder in the path
is not in the plugin cache yet, because the plugin has not been updated. Update the plugin first (`/plugin`, update
`wrangl`, then `/reload-plugins`), list `~/.claude/plugins/cache/<marketplace>/wrangl/` to see the version folder that exists,
and run the script from there.

## Several owned teams

One session can own several teams. Ownership rests on each team's record, plus the live process and its start time
that the record names. A record whose start time is missing or unreadable is not accepted as yours. How to start
several teams: [Rosters](rosters.md#starting-a-team-or-several).

**"`<verb>: this session owns N live teams ("bar", "foo") — pass --team <name> to say which`."** You own more than one
team and the command acts on one whole team, or names no member. Add `--team foo`. If the message goes on with
`… "x" is a member of none of them`, the member you named is in none of your teams' records (a name not yet recorded,
a pane id and a session id never match); if it says `is a member of more than one of them`, name the team too.
`ah msg new` says the same for a recipient it cannot place. It writes nothing, so run it again with `--team`, or with
`--to-name` set to a member's name.

**Naming the legacy default team.** A team kept in the old single `team.json` file counts as owned and appears as
`@default` in refusals, start-up notes and commands. Pass `--team @default` to any command that takes `--team`,
`ah msg` included. The name is reserved: no team can be called that, and any other `@…` value is refused.
`ah roster teams --json` still shows that team's `name` as `null`.

**A team I created is not listed as mine, or another session owns nothing.** A team belongs to the process and start
time of the Claude session that created it. The owner commands find that session from the process above the command;
`--orchestrator-pid` and `CLAUDE_PID` are only checked against it, and `--session` does not make a caller the owner.
After a `/clear` in the same process the session keeps the team. The same session restarted with
`claude --resume <session id>` takes over its team's new process by itself when it starts. A different session owns
none of the teams until it claims each one:

```
ah roster adopt --orchestrator-pid <this session's pid> --team <name>
```

`adopt` changes only the recorded process, refuses a process that is not live, and refuses a team still owned by
another live process. It takes one team per call (`--team @default` for the old `team.json`). `ah roster teams` lists
every team, with `own` true when it is yours by the same rule.

**`reap --commit` removed, or wants to remove, a team I own.** `reap` judges a team by its recorded process alone, so a
team whose recorded process is gone looks orphaned, and `reap --commit` deletes its record unless live members belong
to it. Run `adopt` for it first. A reap that keeps a team lists it under `kept`, with the `adopt` command to run.

**Which teams do I own?** The session's start-up note begins with `You own N live teams: …`, and so does
`/wrangl:hierarchy status`. `ah roster doctor` lists each owned team with its roster. A selected roster that does not
exist is a red `roster-selection` row naming the team.

## Named rosters

How a command picks a roster, and the commands that change it: [Rosters](rosters.md#named-rosters).

**"`… selects roster "<name>", but there is no rosters.<name> block at any level`."** The message names where the
choice came from (`--roster`, `WRANGL_ROSTER`, or `activeRoster at <level> in <path>`) and lists the rosters that are
defined. `show`, `add`, `edit`, `remove` and `create` exit 2 on it; `init` does not, because it creates the roster.
Hooks do not fail: they use the default roster and put the message in the session's warnings, which is how you can
meet it without having run a command. `ah roster doctor` reports it as a red `roster-selection` row. Fix it one of
three ways, all named in the message: `ah roster roster use default`, `ah roster roster use <other>`, or
`ah roster init --roster <name>`. If the cause is `WRANGL_ROSTER`, unset the variable in that shell instead.

**"`roster delete`: … uses it — nothing was deleted."** A team record here, or in the main checkout, uses that roster.
Disband the team (`ah roster disband`), or clear an orphaned record with `ah roster reap --commit`. A running team
keeps the roster it was built from, whatever you select later.

**"`roster delete`: … `activeRoster` in <path> selects it."** Select another roster first
(`ah roster roster use <other> --level <level>`), or clear the selection with
`ah roster roster use --clear --level <level>`. A selection at a different level does not stop the delete: the roster
is deleted, and the command warns that commands there will refuse until that selection changes.

**"`roster delete`: … is in the main checkout's file."** From a work tree, you cannot delete a roster that only the
main checkout's file holds at that level. The message gives the command: the same `roster delete` with `--cwd` set to
the main checkout.

**`roster copy` refuses the destination.** A roster cannot be called `default` (that name means the unnamed roster),
must be 1 to 32 characters of letters, digits and `-` starting with a letter or digit, and cannot already exist at
that level (from a work tree, in either checkout's file). Delete it first, or pick another name.

**A `rosters.<team>` block is ignored by `create --team <team>`.** `--team` names only the team. Pass
`--roster <team>` to build from that block; `create` warns when it sees the block and you did not.

## Role packs

How packs work: [Custom roles](custom-roles.md#6-role-packs). Start with the roster you mean:

```
ah roster role list --roster <name>
```

**A pack role is not listed at all.**

- The roster doesn't name the pack. Add it:
  `ah roster roster pack <name> --add <plugin>@<marketplace>`. Check which roster is selected
  ([Named rosters](#named-rosters)).
The warnings quoted below appear in the session's start-up note and in `/wrangl:hierarchy status`.

- A warning says `pack <plugin>@<marketplace> is not installed on this machine`: run
  `/wrangl:agent-role install <source>`.
- A warning says `… its roles are absent`: the pack's `ah-roles.json` can't be read. Fix the pack and update it.
- It is listed under `EXCLUDED` with a reason. A `pack-invalid` reason is explained below.
  `its pack role is supplied by roster <name> as <role>` means an old row gave the pack role another name: use
  `<role>` under that roster, or see
  [the move to the roster form](custom-roles.md#roles-adopted-before-rosters-named-packs).

**A pack role is `UNAVAILABLE` or `EXCLUDED` with a `pack-…` reason.** It is left out of routing (a built-in whose agent a pack replaces goes back to its
shipped agent), the session's start-up note lists it, and `ah roster doctor` shows a `warn` row. A pack role already
running as a member is not touched. There are three reasons:

| Reason | Cause | Fix |
|---|---|---|
| `pack-missing` | the plugin is not installed from that marketplace, or its `ah-roles.json` does not offer the role. A plugin of the same name from another marketplace never stands in | reinstall the pack, or `ah roster role remove <role>` for an old `from` row |
| `pack-invalid` | the manifest is unreadable or its `version` isn't 1; the role has no known `class`, sets `peer`, or breaks the naming rules; `agents/<agent>.md` is missing; or a role named after a built-in has another class | fix the pack and update it, or take it off the roster |
| `pack-ambiguous` | two installs of the plugin differ (two marketplaces, or user and project scope), so which one Claude Code loads cannot be known | uninstall all but one |

To see what is wrong with a pack, `ah roster pack show <plugin>@<marketplace>` lists every finding, and
`ah roster pack show --path <dir>` checks a pack that is not installed.

**A pack role shows warnings.** It still works. Findings such as `pack-agent-key`, `pack-agent-tools`,
`pack-agent-line`, `pack-hidden-chars`, `name-mismatch` (the frontmatter `name` is not the agent's name) or a second
file taking the agent's name are for the pack's author. See
[What ah warns about](custom-roles.md#what-ah-warns-about-in-a-pack-agent).

**`role list` says `updated <date>`.** The pack changed since ah last saw it. There is nothing to approve; members use
the new files from their next start. The line stays until the next change. See [Pack updates](custom-roles.md#pack-updates).

**`role trust` or `role set --from` stopped working.** Both were removed and exit 2. An installed pack is trusted; put
it on a roster with `ah roster roster pack <roster> --add <plugin>@<marketplace>`. `--pin` and `--slot` are no longer
accepted. Rows made the old way keep working.

## Response pointers

A member files its report with `ah msg fill --id <id> --from <your role> --body-file -`, which puts it beside the brief
it answers, whichever checkout the member is working in. The mail it then sends, `[hierarchy-msg <id> response]`, is
accepted when the report is in the brief's folder, in the member's own folder, or in the home folder of the team.

**"`response is not in its request's directory (<dir>) — file it with `ah msg fill --id <id> --from <your role>
--body-file -`, which places it beside the request`."** The report the mail names is somewhere else. Do not move the
session's working folder; file the report with `ah msg fill --id <id>` and point at that. A brief's own mail is
different: a member whose working folder drifted (into a work tree, another repository) gets "file is outside this
session's message pool … the file is fine and the cwd is wrong", and there the fix is the working folder.

## Tracked bare sends

When the Orchestrator sends a team member a message with Claude Code's `SendMessage` and no `[hierarchy-msg …]`
pointer (or a member replies naming its report), Wrangl's hook tracks it: it files the brief or report, records the
dispatch, files the mailbox entry and rewrites the message to carry the pointer plus an `[ah-entry <id> to <address>]`
marker. The rewrite is the `auto_track.rewrite` setting (default `true`). With `"auto_track": {"rewrite": false}` the
message goes out as typed, and the hook confirms and acknowledges the entry after it is sent instead.

**"`auto-track rewrite not honoured (<permission_mode>)`" in the hook-errors log.** The hook rewrote the message but
Claude Code sent the original text, so the entry was settled the other way: the same confirm and acknowledge, nothing
lost. The rewrite is known to take effect in Claude Code 2.1.310 in the `auto` permission mode; other permission modes
were not confirmed. If the line appears for every send, set `auto_track.rewrite` to `false`.

**"`… without its mailbox entry … Reason this call was denied: bare-brief`" (or `bare-report`).** The message names a
brief (or report) of this team but the hook could not file its entry, so it held the message rather than deliver it
untracked. Run the `ah msg send <recipient> --kind brief|report --ref <id>` line the denial prints, then send again
with the marker it returns.

## Streams

**"`stream <name>: another spawn into this stream holds its tab lock — nothing was launched`" (`stream-tab-busy`).**
Another `spawn-one` or `spawn-ad-hoc` into the same stream was making the stream's tab, and held its lock for the whole
120 seconds this one waited. Nothing was started. Run the same command again once the other one has finished.

**A stream has no tab after `stream-open`.** That is by design: `stream-open` makes the work tree and branch and no
tab. Under herdr the first member that joins with `--stream <name>` creates the tab. Outside herdr there is no tab at
all.

**`ah roster doctor` warns `stream-empty`: "open for over 30 min with no member: <name>
(`ah roster stream-done <name>`)".** A stream was opened, nobody joined it, and 30 minutes have passed. Start a member
in it (`ah roster spawn-one <role> --stream <name>`) or close it with the command the warning prints. The row is never
an error, and a stream younger than 30 minutes is not listed.

**`cannot create member tmp <path>: <reason>`.** A member's private temporary folder could not be made or excluded
from git. `create` also says `no team file or grants document was written`, so you can fix the cause (a team folder you
cannot write to, a full disk) and run it again.
