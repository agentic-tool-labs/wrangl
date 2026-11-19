# The ah daemon (`ahd`)

`ahd` is a small background program that runs on your machine, once per user. Teams, message files and the
roster work from plain files without it. Timers, wake-ups, the delivery ladder, links, custom runtimes and the
terminal view need it, and waking an idle Orchestrator also needs a Claude Code new enough for ah's mod. When the
daemon is up, mail reaches a session as soon as it is sent, undelivered mail is chased on a ladder instead of
waiting, links between teammates are counted and ended on time, custom agent runtimes can join a team, and there
is a live terminal view of everything the daemon knows.

This page is the user guide. The command reference is [cli-tools.md](cli-tools.md); every route the daemon
serves is in [daemon-api.md](daemon-api.md); the rules a custom agent
runtime follows are in [participation.md](participation.md); how the pieces fit together is in
[architecture.md](architecture.md).

- [What it does and what happens without it](#what-it-does-and-what-happens-without-it)
- [Starting, stopping and checking it](#starting-stopping-and-checking-it)
- [The `ahd` command](#the-ahd-command)
- [The terminal view: `ahd tui`](#the-terminal-view-ahd-tui)
- [Looking inside: `ahd inspect`](#looking-inside-ahd-inspect)
- [How mail is delivered](#how-mail-is-delivered)
- [Teammates talking to each other: the mesh and links](#teammates-talking-to-each-other-the-mesh-and-links)
- [Bringing your own agent runtime](#bringing-your-own-agent-runtime)
- [Context alerts](#context-alerts)
- [The Codex app-server adapter](#the-codex-app-server-adapter)
- [Troubleshooting](#troubleshooting)
- [What the safety checks do and do not protect against](#what-the-safety-checks-do-and-do-not-protect-against)

## What it does and what happens without it

What each feature needs, and what it does without it:

| Feature | Needs | Without it |
|---|---|---|
| Dispatch timers: check-in, "this peer went quiet", landed | the daemon | The Orchestrator's Stop hook still raises a check-in when its turn ends, from the files. Nothing fires while it is idle. |
| A member blocked at a prompt: the BLOCKED wake | the daemon | Not produced. |
| Resume after an API error | the daemon **and** the mod | A desktop notification only; the session is not resumed. |
| Waking an idle Orchestrator with a daemon event | the daemon **and** the mod (Claude Code 2.1.289 or later) | The event is delivered at the Orchestrator's next turn end. Mail already in a box is still found by the mod's own poll (`ah msg due`), without a daemon. |
| Context alerts | the daemon | None in this release: a session's context is shown in tokens only, with no percentage and no alert. |
| The mail delivery ladder (rungs 2 to 5) | the daemon | Entries wait in the box and are read at the next pull or turn end. Nothing is lost. |
| Links expiring, custom-runtime joins, the terminal view (`ahd tui`) | the daemon | Not available. |
| Hook state writes (usage, gates, peers, activity) | nothing; the daemon is optional | Written directly to files. With a daemon that is slow (a hook waits 0.3 seconds) or answers with a server error, the write is made directly too. |
| Team files, message files, roster verbs, gates | nothing | Unchanged. |

The mod is the Claude Code extension this plugin ships (`mod/`): it pulls mail for an Orchestrator and wakes it
while it is idle. Hooks restart a gone daemon at a prompt, at a Stop and at SessionStart, but only for a session
that is registered with it, and within a budget of 1.5 seconds; a session that is not registered does not start one.

Without the daemon, `ah msg send`, `inbox` and `ack` fall back to the mailbox files, print one warning line
(`ah msg: warning: ahd unavailable (<reason>); using files`) and record the fallback in `logs/fallback.jsonl` under
the ah home directory. It all resumes when a daemon is up.

To keep a command off the daemon entirely, set `AH_DAEMON=off` in its environment.

## Starting, stopping and checking it

You rarely start it yourself. A session that has a mailbox address (an Orchestrator or a team member) starts
the daemon when it begins, and the daemon exits by itself once it has had no requests for 30 minutes and no
session is registered and no timers are pending, and no browser page is open. Any request, even a health check,
counts as activity. If you turned on the login item (`ahd login on`), it never exits when idle. You can still run it by hand:

```
ahd start      # reports the running daemon, else starts one and waits up to 3 seconds
ahd status     # up or down, version, pid, uptime, live registrations, the delivery ladder, fallbacks
ahd health     # the daemon's own health report as JSON (exit 3 when it is down)
ahd version    # client and daemon versions and whether they agree
ahd logs -n 20 # the last 20 lines of the daemon log (there is no follow mode)
ahd stop       # asks it to exit and waits up to 5 seconds
ahd restart    # stop, then start
```

Add `--plain` to any of these to get text instead of JSON. Example (`ahd status --plain` with a daemon up):

```
up  version 2.0.20  pid 49124  uptime 0s  requests 0
root /path/to/the/plugin
owns mailbox=true status=false timers=
errors 0 (5xx)
registrations: none
overdue: 0
ladder: none
fallbacks to the files: 0
```

With the daemon down, `ahd status --plain` starts with `daemon down, showing files` and shows what the files
say. Exit codes: 0 success, 1 the operation failed, 2 a mistake in how the command was given, 3 the daemon is
down and the command needs it, 4 a check found a mismatch.

All of the daemon's files live under the **ah home**, `~/.claude/ah2/` by default, or the
directory in `AH_HOME` if you set it: the socket (`run/ahd.sock`), the logs (`logs/ahd.log`,
`logs/fallback.jsonl`), the registrations of live sessions and every mailbox. The daemon's API listens on that
unix socket only, and the socket is readable by your user only. The one exception is the browser page: while it
is open the daemon also listens on a loopback port (`127.0.0.1`), protected as described in
[daemon-api.md](daemon-api.md#the-browser-bridge). Other files in `run/` are `ahd.json` (which daemon is running),
`ahd.lock` (held while one starts), `ahd.spawn-failed` (a failed start; implicit starts then wait 60 seconds),
`band.token` and `ui.json`.

## The `ahd` command

Everything above and below is `ahd <verb>`. The plugin ships it as `bin/ahd`:

- **Inside a Claude Code session** the plugin's `bin/` directory is on the PATH of the Bash tool, so
  `ahd status` works as typed.
- **In a plain terminal** it is not on the PATH. Run `/wrangl:hierarchy install` once. It builds the native `ahd`
  into `<AH_HOME>/bin/ahd` (about ten seconds, with a .NET 10 SDK), links it from `~/.local/bin/ahd`, and, with
  your consent, puts `~/.local/bin` on your PATH. From a terminal, `scripts/install.sh` does the same and only
  prints its plan until you add `--apply`. `scripts/uninstall.sh` reverses it, and removes only what the install
  put there.

  The binary is every verb; the plugin's `bin/ahd`, `bin/ah` and `bin/ah-hook` only find and run it. A plugin
  update that changes the native sources therefore needs a rebuild: with auto-rebuild on (the install's
  default) the next SessionStart rebuilds it in the background, or re-run `/wrangl:hierarchy install`
  (`scripts/install.sh --rebuild-only`). `ahd where` prints which plugin the binary found, the install stamp,
  whether the binary's contract matches the plugin's (`compatible`), and ah's home as it resolves it (`home`). A reinstall also replaces a link made
  by hand to a plugin directory's `bin/ahd`.

Without the install you can always run the same program as `<plugin root>/bin/ahd <verb>`.

**Start at login (optional, off by default).** `ahd login on` installs a one-shot login item (a launchd agent on
macOS, a systemd user unit on Linux) that runs `ahd login run`, which starts the daemon once and exits. It also
writes `<AH_HOME>/login.json` `{"stay": true, ...}`: a daemon that reads it at startup never idle-exits, whoever
started it. `ahd login off` removes the item and the file; a running daemon keeps the mode it started with until it
next exits. `ahd login status` reports `on`, `off` or `broken` with the reason. The item serves the default
`AH_HOME` only, and `login on|off` refuse when `$HOME` is not this user's passwd home.

## The terminal view: `ahd tui`

```
ahd tui
```

A live view of your teams, each team's agents and Sitrep, and each agent's work items, with a popup for an item's
log. It never starts the daemon (with none running it says so and retries) and never changes anything. Screens, keys,
mouse and flags are on the [terminal view page](tui.md).

`ahd tui --once` prints one plain frame and exits, so you can pipe it; `--team` picks the team. Anything printed
through a pipe has no colour or escape sequences. When the daemon is down it prints one line and exits 3. Colour
follows `NO_COLOR`.

The names and notes it shows have had control characters stripped, so a member cannot dress its text up to look like
part of the interface.

## Looking inside: `ahd inspect`

These commands only read, and answer from the files when no daemon is up:

```
ahd inspect registrations        # live sessions the daemon knows, and stale ones
ahd inspect boxes                # every mailbox: entries, unacked, overdue, last heartbeat
ahd inspect entries --address <name>@<team> --all   # the entries of one member's mailbox
ahd inspect timers               # the dispatch timers and what they would fire now (a dry run)
ahd inspect events               # events the daemon sent that are still unacked
ahd check                        # compare "daemon up" with "daemon down" answers in a throwaway sandbox
```

`ahd inspect entries` takes `--pending` (the default), `--acked` or `--all`, and `-n` to limit the count.
`ahd check` never touches your own daemon or home directory; it builds a scratch one under `/tmp`.

## How mail is delivered

Roles and members talk through a durable mailbox (`ah msg send`, `inbox`, `ack`; the details are in
[comms-protocol.md](comms-protocol.md)). With the daemon up, a message that nobody acknowledges is not simply
left there. It walks a short ladder, one step at a time, and stops the moment anyone acknowledges it. The rungs
are numbered the way `ah msg inbox` prints them:

- **Rung 2, pull.** The recipient's session or adapter is told about the message and is expected to read it. If
  it does not acknowledge within its pull timeout (three times its polling interval plus 30 seconds), the
  ladder moves on. A message is on this rung only when the recipient has a live pull adapter (a fresh
  heartbeat) when it is sent.
- **Rung 3, relay.** A message sent to a recipient without one (it is "routed `fallback`") starts here, at
  once. The sender, if it is a live Claude session, or else the team's Orchestrator, is asked to pass the
   message along with an ordinary `SendMessage` and is given the exact text to send. The ladder waits ten
   minutes for that.
- **Rung 4, paste.** The daemon types the message into the member's terminal pane, through the same checked paste
   `ah roster deliver` uses. It tries up to four times, five minutes apart, and gives up at once for a member
   that has no pane or has pasting turned off (`paste: false` in its team-file row). A paste it cannot confirm
   is never repeated, so a message is not typed twice.
- **Rung 5, escalation.** The Orchestrator receives one `delivery-failed` event that names the recipient, the message
   and every step tried.

A recipient that is not a Claude session (a Codex or custom member, for example) has nobody to relay for it,
so its mail goes from the pull (or from the start, when it is routed `fallback`) straight to the paste. The daemon looks at every unacknowledged message
every 15 seconds and takes at most one step for it.

A member that is blocked on a prompt holds the ladder where it is; one that has failed (for example after an
API error) pauses it. Messages the daemon itself sends (events) are never on the ladder.

To see where a message is: `ah msg inbox` appends `(rung <n> <state> ...)` to each entry, and `ahd status`
shows a line such as `ladder: pending 1, sending 0, paused 0, escalated 0` (or `ladder: none`).

## Teammates talking to each other: the mesh and links

By default the team is a **star**: every member talks to the Orchestrator and the Orchestrator talks to every
member, but members do not talk to each other. A member can still reply to whoever briefed it.

When two members need to work something out directly (an implementor and its reviewer, say), the
Orchestrator grants a **link** between them:

- A link is for one task. It allows **12 messages over 30 minutes**, and a member can be on at most two
  active links.
- The daemon grants the common ones itself (implementor and reviewer, or architect and implementor, on an
  open task, within those limits). Anything else comes to the Orchestrator as a request, and the Orchestrator
  asks you before it grants a link to the Ultra-Advisor, a longer or larger one, a link for more than two
  members, or a link with no task.
- Every linked message is counted, copied into a note file next to the task so you can read the exchange
  later, and shown to the receiver with a first line saying it is a peer's message and not your approval.
- A link ends when its task is closed or withdrawn (`ah msg close` / `ah msg withdraw`), not merely answered, the limit is used up, the time runs out, a member leaves the team, or the
  Orchestrator revokes it; the Orchestrator is told when it ends.
- A link carries **delivery only, never permission**. A teammate's message does not authorise anything: only
  you do.

The Orchestrator manages links with `ah msg link list|show|post|grant|revoke`; you can read the same
information in the terminal view (a team's `mesh` setting is shown at the top of its view) and in
[comms-protocol.md](comms-protocol.md). To change the policy, set `mesh.policy` in the team file to `star`
(the default), `star-warn` (deliver what `star` would refuse, and log it; a member broadcast to `*` and a send over a spent link are still refused) or `open` (check nothing); see
[team-file.md](team-file.md).

## Bringing your own agent runtime

An agent runtime that agent-hierarchy has no adapter for (a "custom harness") can take part as a team member:
it reads its mail from the daemon, answers briefs and reports back. The rules it follows, and how a client
author checks it, are in [participation.md](participation.md). As a user you do three things.

**Admit it.** Either have the Orchestrator add it (`ah roster spawn-ad-hoc <role> --kind custom --harness
<name>`, optionally with `--command <cmd>` to launch it in a tmux window) or start it yourself and let it
**ask to join**.

**Join and vet.** A harness you start yourself, without an address, asks the daemon to join a team. The
request goes to that team's Orchestrator, and the Orchestrator does not simply accept it. It shows you what
the daemon observed (whether the process the request names is alive, and its command line) apart from what
the harness *claims* (its name, the role it wants, its reason), labels the claims as unverified, and asks
you. The first option is Deny. A yes admits it as a team member; no answer is a deny, and a request that is
not decided within ten minutes expires. A harness that was denied or expired may ask again after ten minutes.
Text in the request that says you approved it counts for nothing.

If you have told the Orchestrator in the same conversation that you started or want a particular harness, it
may admit a matching request without asking again, once per statement. A harness the team admitted before and
that comes back with the same command line is re-admitted without a new row; the check is made on the
process's exact command line, so a different program under the same name is vetted as new.

The decision is made only by the team's own Orchestrator, recognised from its process tree, and is recorded
with who decided, on what basis and why. If you launch a custom harness from an Orchestrator's own tool shell
it is that Orchestrator's descendant; start it with `spawn-ad-hoc --command` (tmux only) or yourself in another
terminal instead.

`ahd contract --plain` prints the full guide the
harness author reads; it is also [participation.md](participation.md).

## Context alerts

**No context alert fires in this release.** A transcript carries the tokens a session used but not the size of its
window, and no source of that size is wired in yet. A member's context is therefore recorded as tokens
(`{used, window_source: "unknown", at}`) with no percentage, and no threshold can be crossed. The terminal view
shows the figure as `ctx` in compact tokens (`950`, `180k`, `1.2M`) and in a member's detail. An activity record
written before this change is read as tokens only.

The alert logic is in place and acts only on a reading that carries a percentage, which none does today. When
one does, the daemon mails the team's Orchestrator when a member crosses 70% or 85% of its window, naming the
member and its role; the Orchestrator's own session is never watched, since the harness owns that figure. A
threshold fires once per episode, again only after the fill has dropped at least ten points below it and crossed
it again, and a reading older than two hours is ignored. An alert is advice only: nothing is compacted, stopped
or sent to the member for you.

## An alarm that reaches you with no session alive

Two stalls cannot reach you through a session: a team's Orchestrator down with a run open, and an Orchestrator waiting
on a question in its terminal, which holds its whole team. To hear about them, set `alarm.command` yourself in your
user-level `agent-hierarchy.json`. `ahd` never sets it (without it, the herdr notification below is the only fallback):

```json
{ "alarm": { "command": ["/usr/bin/curl", "-d@-", "https://ntfy.sh/<your topic>"] } }
```

It is an argv array, run with no shell. The alarm line arrives on stdin and in `AH_ALARM`, and only `HOME` and `PATH`
are passed besides. The command has 10 seconds. The line names the team and what happened, not the question's text, but
it leaves your machine if your command sends it. The question fires after 15 minutes
(`AHD_STALL_ASK_ALARM`, in seconds), the down Orchestrator after the same wait as a member. While the problem lasts the alarm
repeats about every 60 minutes, and a "cleared" line is sent when it ends. A team with queued work but no run open is told once.
With no `alarm.command`, and the terminal `herdr` command on the daemon's PATH, the line is shown as a herdr notification
instead. `ah alarm test` runs whichever applies. Each run is a row in `<ah home>/stall/journal.jsonl` with its exit status.

## Terminal labels for herdr desktop mods

Under herdr core, every member terminal and the Orchestrator's terminal carry `ah.*` labels that a mod can read from
`hc ls --json` (`.labels`). These keys are the contract.

| Key | On | Value |
|---|---|---|
| `ah.name` | member | the member's name, set at spawn |
| `ah.spawn` | member | the spawn's nonce, set at spawn |
| `ah.item` | member | the work of its current request: the checklist item's backlog parent (`b<N>`), else the item id; empty when the request names no item |
| `ah.title` | member | the current request's `--title` |
| `ah.req` | member | the current request's id |
| `ah.title` | Orchestrator | `<team> · <N> working · <M> waiting on you`: N members with an open request, M checklist items waiting on you; one `;`-separated part per owned team |

A member's current request is its oldest open one. When it is reported, withdrawn or closed the labels move to the next
open one, and when none is left `ah.item`, `ah.title` and `ah.req` are removed. `ahd` reconciles the labels every watch
tick (15 s by default) and writes a terminal only when its labels changed, so labels left stale while `ahd` was down are
put right on its first tick. The Orchestrator's label is written only when its roster record names its terminal. Off
herdr core, or with no `hc`, nothing is written; a failed write is logged and that terminal is skipped for 5 minutes. A member whose
process is known to be dead is never labelled, since its old address may belong to another terminal.

## The Codex app-server adapter

Codex members normally get their mail by being typed into a terminal pane. The **adapter** lets a Codex
member receive it over Codex's own app-server connection instead, so mail reaches the turn that is running
without typing into the pane. It is **on by default for Codex members on the `herdr` transport, on a Codex
version it has been checked against**. That list holds one version in this release,
`codex-cli 0.162.0-alpha.2` (up to the next prerelease); any other Codex launches the ordinary way until a
live check passes on it.

The roster setting is `adapter`, and only on the `herdr` transport:

```
ah roster edit --member <name> --adapter off         # opt a Codex member out
ah roster edit --member <name> --adapter appserver   # ask for it explicitly
ah roster edit --member <name> --adapter ""          # back to the default
```

| `adapter` | no tested version at all | a tested version exists (this release) |
|---|---|---|
| absent | launches the ordinary way, no note | the adapter if this Codex is in the range, else the ordinary launch with the note |
| `"appserver"` | ordinary launch, with a note | the same as absent |
| `"off"` | ordinary launch, no note | ordinary launch, no note, `codex --version` is not run |

When the adapter is used, a spawn does the following, in order. Nothing is typed into a pane at any step.

1. It makes the member's temp directory, then starts the member's own app-server on a socket, with the sandbox
   mode, network setting, writable roots, approval policy and temp-space settings set on that server. These
   cannot be set from a remote terminal. A temp directory that cannot be made is a fallback to the ordinary
   launch, with the note `member temp dir unavailable`.
2. It creates the thread on that server and checks the thread's reported sandbox, network and approval
   settings against what it set.
3. It runs one fixed **warm-up turn** (a single harmless reply, up to 90 seconds) and resumes the thread,
   because a thread has no record on disk until a turn has completed. This costs one small model call on the
   user's Codex account at every spawn and relaunch, and shows in the member's history. The reply is never
   recorded.
4. It writes `appserver_thread_id` on the member's row, then launches the terminal as
   `codex resume <thread> --remote <socket>` with no other argument.

The server is owned by the member and not by the daemon, so restarting the daemon never kills a member. The
daemon resumes the recorded thread each time it connects, checks the same settings again, and sends no turn
if they differ: the row gets a note, the Orchestrator gets one `adapter-refused` event, and the mail stays on
the ordinary ladder. A row without a recorded thread is not adapted.

When the adapter cannot be used at any step, the member launches the ordinary way, once, and the spawn output
and the member's row carry one note naming the step, for example `codex 0.x.y is outside the tested
app-server range >=<min> <<max>; launched without the app-server adapter (mail is pasted into its pane). Set
adapter "off" to silence this.` Notes carry a fixed phrase and never server text.

The launch is checked against a strict allow-list. Only `--model`, `--sandbox` (workspace-write or read-only),
`--ask-for-approval` (`on-request` or `never`), `--add-dir` for a path unrelated to ah's own directory, and a
`-c` setting of `model_instructions_file`, `approvals_reviewer` or `model_reasoning_effort` are admitted;
anything else (a profile, any sandbox config key, a positional argument, an unknown flag, an approval value
this Codex rejects) means no adapter, with a note that names the argument but never its value. The server is
started with fixed sandbox settings (no network from the sandbox, only the member's own writable folders), so a
Codex config file cannot widen them. Extra writable folders for such a member go in its `args` as
`--add-dir`; they become the server's writable folders. The adapter never reads, copies or logs Codex
credentials: the server runs on the user's own Codex home.

**Temp space.** Codex's workspace-write sandbox allows `/tmp` and `$TMPDIR` by default, which would let a member
write where other sessions keep their sockets and scratch files. The server is started with both defaults
turned off, and each member gets a private directory instead: `<ah home>/run/codex/tmp/<team>-<name>`, mode
0700, its commands' `TMPDIR` and the first of its writable folders. The thread must report both exclusions
as on or the adapter is refused, at spawn and on every reconnect. Nothing deletes that directory; a tool
normally cleans up after itself, and a relaunch reuses it. Cost: a script that writes a hardcoded `/tmp/...`
path fails with a permission error inside the member and has to use `$TMPDIR`. Read-only members are
unaffected. Whether members may write the shared `/tmp` is a product tradeoff and this is the default.

## Troubleshooting

**`ahd status` says the daemon is down.** Start it with `ahd start`. If it will not start, `ahd logs -n 50`
shows why. Everything keeps working from files meanwhile.

**The daemon is running but does not answer.** Run `ahd heal`. It probes the daemon for at least a minute and a half and
replaces it only when no probe is answered; a slow daemon is left alone. A failed write and the overdue timer start
the same heal in the background. See [troubleshooting](./troubleshooting.md#the-daemon).

**Nothing is delivered, or `ah msg` prints `ah msg: warning: ahd unavailable (<reason>); using files`.** The sender could not reach the
daemon and wrote the mailbox directly; the recipient only sees the message when it next looks at its mailbox.
`ahd status` shows the fallbacks (count and last ten), and `logs/fallback.jsonl` under the ah home records
each with its reason (`down`, `timeout` or an HTTP status).

**A member shows as live but is not.** `ahd inspect registrations` lists live and stale registrations. A
session that ended abnormally can leave a stale registration; it is listed as stale and no longer counts as
live.

**Mail sits at one rung of the ladder.** `ah msg inbox` shows the rung and when the next step is due. A
member that is blocked on a prompt or has failed holds the ladder; answer or fix the member.

**The terminal view says `ahd is not running`.** The daemon is down. `ahd tui` never starts it; run
`ahd start` in another terminal and the view reconnects within two seconds. If it shows a status and error
code instead, the daemon is up and answered with an error: `ahd logs` has the detail.

**`ah ui` says `unknown command 'ui'`.** The command is `ahd ui`, which opens the browser page (the grid, graph, tree and plan
views). `ahd ui start` keeps the page up in the background until `ahd ui stop`. `ahd ui` exits 2 with `the daemon is not reachable` when the daemon is down, and exits 3 with
`browser UI disabled` when the daemon was started with `AH_UI=off`. See
[the browser page](./cli-tools.md#the-browser-page-ahd-ui---print---no-open).

**The two parts disagree on a version.** `ahd version` prints `verdict: equal`, `client-newer`,
`client-older` or `unsupported`. After upgrading the plugin, the next mailbox call (`ah msg send`, `inbox`, `ack`
and the like) finds the older daemon, drains it and starts this version, and a new session with a mailbox
address asks it to drain. `ahd version`, `ahd status` and `ahd start` only report the mismatch and leave the
older daemon running. `ahd restart` (or `ah msg daemon start`) replaces it at once. After an update, run
`ahd restart` from a session, or from a terminal with the installed `ahd` (a link made by hand to an old plugin
directory restarts the previous version until `/wrangl:hierarchy install` replaces it).

**A new binary finds an older database.** Only the daemon migrates the state database, after its `pre-migrate` backup.
When an update raises the schema and the old daemon is still running, any `ah` verb other than `ah hook`, `ah db` and
`ah proc` prints one line (`replacing the daemon, which backs it up and migrates it`), drains the older daemon, starts
this version and waits up to 20 seconds for the database to catch up. A hook never waits: it makes one short attempt at
its write and carries on, and a session's hook may ask the older daemon to drain. The daemon only accepts a drain from a
session or the user, so a detached script or a session with no registration waits out the 20 seconds and is then refused,
with `the state database is schema N, older than this binary's M; run ahd restart`. Nothing is queued: the verb fails and
says so, and running it again after the restart makes the write.

**Who counts as the user.** The daemon names each caller by its process: a session (an agent's own
process tree) or the user. `ahd drain` and `ahd stop` accept either one and refuse anything else
(the log says `drain refused: caller pid N is neither a session nor the user`). A shell you open
yourself counts as the user. So does a command run from a plain terminal pane that no agent
started, **including a detached tmux pane**: the daemon cannot tell it from you at the keyboard.
This is accepted on purpose and there is no extra check. If you run an automated job from such a
pane, it can drain or stop the daemon.

**Check the whole thing without touching your own daemon.** `ahd check` builds a throwaway daemon under
`/tmp` and compares every read-only answer with and without it.

## What the safety checks do and do not protect against

The checks in this design keep well-meaning software from making honest mistakes. They are **not** a security
boundary against another program running as you.

- The daemon's API listens only on a unix socket under the ah home directory, readable by you alone. Anything that
  can open that socket, or read and write the mailbox files, is you as far as ah can tell, and can do what you
  can. While the browser page is open there is also a loopback listener (`127.0.0.1`, a random port). It accepts a
  request only with the right `Host` and `Origin`, which keeps other web pages in your browser out; there is no
  sign-in, so a local process that sets those headers itself gets in. It serves views and the page's own writes
  (answers, team close, rules, looks), never mail or panes.
- The peer mesh stops a member sending where the policy does not allow, by checking the sender's registered
  session. A program that can write the mailbox files directly can bypass it.
- The process tree identifies the Orchestrator that decides a join. A process running as you can still
  imitate one by other means, and a recycled process id left in a stale record is outside the check. A harness
  you launch from the Orchestrator's own tool shell is that Orchestrator's descendant and could accept its own
  join, which is why such a harness is started another way.
- Team files are plain files: any process running as you can edit one and so add a member without any join
  request, vetting or record.
- A harness's pid, command line, name and stated reason are its own claims. The daemon records what it
  observed about the process the request names, which is a fact about that process, not proof of who started
  it. That is why the Orchestrator asks you.
- A link carries delivery, not authority; an alert is advice; the allow-list for the Codex adapter and its
  pinned settings narrow what a launch can do, but do not replace the sandbox you chose for the member.
- Text shown in the terminal view and in join requests has had control characters stripped, so it cannot
  rewrite the screen or pose as a message of ah's own.
