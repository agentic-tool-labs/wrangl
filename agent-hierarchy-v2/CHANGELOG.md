# Wrangl: Agent Hierarchy changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions are the plugin's `version` in `.claude-plugin/plugin.json`. Feature
detail lives in the README and in [docs/](./docs/).

## [2.0.11] - 2026-11-18

### Fixed

- **The daemon keeps the lead Orchestrator from stalling.** An Orchestrator whose turn was interrupted sat idle for
  over an hour with a report and three alarms unread: Claude Code drops plugin prompts in a stopped session, and
  nothing else got through. Now:
  - unread mail in an Orchestrator's or Sub-Orchestrator's box ends in a turn. Alarms count as reports do, the wake
    is decided per box, and a stopped session is woken in its pane two minutes after its oldest unread item with the
    fixed line `ah: mail waiting, run ah msg next`. The wake does not type over a draft, a permission prompt or a
    running turn, and never into a team on hold;
  - the pane wake reaches herdr desktop. It typed through the terminal herdr whatever herdr the session was on;
  - work that waits on the Orchestrator is mail and stays mail: a row reported for ten minutes raises
    `report-waiting`, one idle member is enough for `idle-capacity`, and every twenty minutes `work-still-waiting`
    lists what was told and still waits;
  - when `ah` cannot wake the session, a new user alarm `owner-stalled` says so, and every raised user alarm
    (`owner-down`, `owner-asking`, `owner-stalled`) now shows as an alert in the web UI and the herdr desktop mod,
    with or without an `alarm.command`;
  - an Orchestrator waiting at a permission prompt shows as blocked, as a member does;
  - the stopped session's status line reads `ah: stopped with N unread; type anything to resume`, the doorbell
    trace records every wake attempt, and `ah roster doctor` prints the lead's oldest unread mail and last wake.
- **The daemon probes each member on the herdr it is on.** Its liveness, stall and teardown probes read a team
  without a herdr marker as the terminal herdr; they now follow each member's own stamp.
- **The herdr Graph after its review**: Go to pane finds a member that is not on screen, the agent sheet closes when
  you leave the Graph tab, the dots stop at once on that switch, a reviewer that is not active is not drawn as
  reviewing, and dots are the web's size.
- **`ah` with no arguments lists `where`**, and a refused `ah roster dismiss` says what a clean member is.

### Changed

- **A reviewer under a "reviewers run nothing" rule gives its verdict from reading.** It does not clone, run, order
  a run or ask for a work-tree approval, and lists what was not run for QA.
- The shell suite `test-run-set` is three suites, each well under the per-suite timeout. No product change.

## [2.0.10] - 2026-11-17

### Added

- **The herdr Graph looks like the web's.** The Flow's cards, edges and lanes take the web's styling: an active
  agent's border lights up, a blocked agent shows its glyph, lanes are colour-coded, and a dot travels along an edge
  while a brief is in flight. The dots run on a timer only while a brief is in flight and the Flow is shown.
- **Click an agent in the herdr Graph for its details.** A sheet shows what the web's member summary shows (role,
  model, state, lane, what it is doing now, its latest messages). Go to pane is the sheet's first button; Open page
  opens the member in the browser.
- **Open in browser from the herdr Graph**, scoped to the graph you are looking at.
- **`qa.cadence`** (`item` or `batch`) says when QA runs. `/hierarchy init` asks when it is not set and records your
  own words as a rule of engagement. With `batch`, the per-item "Dispatch QA" prompt is not raised.

### Fixed

- **A drag pans the herdr Graph at every scale.** A drag did nothing unless the graph was zoomed past its frame.
- **The daemon recognises a team's Orchestrator by its role**, as the CLI does, so a resumed Orchestrator session is
  not treated as a stranger by one and the owner by the other.
- **The Board shows shipped work as done.** Finishing the last checklist row of a backlog item closes the item, and
  `ah backlog done` on an item with an open question says the item closed and the question stays open, not
  "not closed".

## [2.0.9] - 2026-11-17

### Fixed

- **The rest of `ah` follows the herdr your session is on.** `ah roster move`, resume and teardown, and the daemon's
  relaunch and wake paths, no longer read a team record without a herdr marker as the terminal herdr: a member's own
  stamp decides, then the session's herdr environment. `ah roster dismiss` on such a team no longer reports "team
  transport is not herdr" for a member that is on herdr; a team whose record names no transport at all now says so.
- **An Orchestrator with an admin grant sends its briefs as the Orchestrator.** They were stored as from `user`, so
  members read "brief from user" and the check-in alarm said "No delivery recorded" for briefs that had arrived. The
  daemon accepts that sender only for a session the admin holds that itself holds the address; when it does not, the
  send goes out as `user`, as before.

### Changed

- The web tests' stand-in DOM now lets the looks boot run, so a passing run no longer prints hundreds of
  "looks: boot failed" lines. No product change.

## [2.0.8] - 2026-11-17

### Fixed

- **`ah` talks to the herdr your session is on.** On herdr desktop, a team re-formed by a spawn carried no herdr
  marker and was read as the terminal herdr, so spawns warned `no herdr server is running`, `ah roster resync` said
  the team had no panes and `ah roster teams` matched none of its members. The session's herdr environment now decides
  when the team record does not say, a spawn completes the record, `resync` and `move` accept an older record, and
  members working in a stream's work tree are matched.
- **Dismissing the last member keeps the team.** The Orchestrator used to lose its team, and with it its mail address
  (`unknown_address`, `not_your_session`), until the next spawn. The team now stays with no members; `ah roster
  disband` and `ah roster untrack --all` remove it as before.
- **The herdr mod's read never touches the daemon.** `ah view events` no longer runs the schema-upgrade wait, which
  could start or drain `ahd` from a poll.
- **`ah roster teams` no longer lists the Orchestrator's own session as untracked** after a restart of that session.

## [2.0.7] - 2026-11-17

### Changed

- **The Orchestrator's start text is indexed.** At session start the Orchestrator now receives a short resident core
  plus an index of on-demand sections, in four outputs that each fit Claude Code's limit, instead of one long text it
  had to read from a file: about 16,400 characters in a default setup, down from about 28,800. A section is read with
  `ah rules show <key>` when its trigger comes up (`ah rules index` lists them), and a resident rule says to do that
  before acting on anything an index entry relates to. Rules that nothing enforces at the moment they would be broken
  stay resident. Sub-Orchestrator, Deputy and member texts are unchanged.

### Fixed

- **Wheel zoom in the herdr desktop Graph follows the wheel.** A wheel or two-finger scroll now moves the view at
  once and eases only as it stops (about 120 ms), instead of restarting a slow-starting 400 ms animation on every
  wheel event. The − / + / Fit buttons are unchanged. Run `ah herdr install` again to get it.

## [2.0.6] - 2026-11-17

### Fixed

- **`ahd` comes back after a plugin update on a busy machine.** The hook that replaces an older daemon could read the
  old daemon's health reply before it had marked itself stopping, conclude nothing had changed, and start nothing;
  the old daemon then exited. A daemon now marks itself stopping before it answers the stop request, and the hook
  waits for the daemon it just stopped to exit, within its existing time limit, before starting the new one.
- **A brief's mail names the request.** Mail for a brief always carries the request id and the command that reads it;
  it is no longer cut to "brief from X" with a mailbox entry id. `ah msg show <entry id>` says which request the
  entry is for. A file path is printed only when the file exists.
- **A brief to a busy member is no longer reported as delivery-failed.** The daemon keeps the delivery while the
  member's session is still checking in, and fails it only when that stops.
- **herdr desktop: a member that has not had a turn shows as idle,** not "No signal", and the team's "Waiting on
  you" line sits under the team name instead of under the last member.

### Added

- **The herdr desktop Board matches the web Board.** Run `ah herdr install` again to get it.
  - Cards show the work id, size (the deliverable's when the item has none), epic, "blocked by N", queue markers
    and message counts, in the web's order.
  - A Backlog column by wave; a breadcrumb scope (All teams, team, lane, epic, deliverable); lane headers.
  - The item sheet shows what the web's item dialog shows: rows, description, notes, history, timeline, links and
    attachments. Backlog rows, teams and lanes open sheets too.
  - "Filter streams", "Show N older streams", "Show all N done today", expandable columns, stale teams shown on
    request, "Show only this stream", "Changed since last visit" kept across restarts with "Mark all seen", and
    arrow keys between cards.
  - Not included: zoom (herdr cannot scale a laid-out board) and the web's `item=` link.

## [2.0.5] - 2026-11-17

### Fixed

- **herdr desktop no longer says "ahd not running" across a plugin update.** After an update the new `ah` could run
  beside the previous `ahd` until a session replaced it, and the Wrangl mod's reads were refused in that time. The
  mod's read is now answered by a running daemon of an older version (same protocol); nothing else is, and no read
  stops or starts a daemon. After any failure the mod also retries at most every 8 s for its first two minutes down.
- **`ah where` exists.** It prints what `ahd where` prints. The herdr mod has always asked `ah where`, so until now it
  never found the daemon's local HTTP port, and the build notices added in 2.0.4 did not appear in herdr desktop.
  Run `ah herdr install` again to get the mod's part of this.

## [2.0.4] - 2026-11-17

### Added

- **Two-finger scroll zooms the herdr desktop Graph, and click and drag pans it.** A trackpad two-finger scroll now
  zooms about the pointer, as a mouse wheel does, with no key held; herdr gives mods no pinch gesture. Press and drag
  pans a zoomed Map or an oversized Flow. A plain click on a member still opens its pane. Run `ah herdr install`
  again to get it.
- **Notices while a new `ahd` is built.** After a plugin update `ahd` is rebuilt in the background. Claude Code now
  shows a toast, and herdr desktop a notice, when the build starts ("building ahd <version>…"), when it is ready, and
  if it fails (with the path of the build log). Each shows once per build.
- **Click the herdr "available" notice to update the Wrangl mod.** When the mod installed in herdr desktop is older
  than the plugin, the notice herdr already showed now says "Click to install", and a click runs `ah herdr install`.
  Nothing is installed without the click unless the existing auto-update setting is on. The notice waits for a running
  `ahd` build to finish. The "… → Update" menu item is unchanged and does the same thing.

### Changed

- **`ah where` names ah's home.** Its JSON has a new `home` field and `--plain` a `home:` line. The herdr desktop mod
  uses it to find the install stamp; with an older `ahd` it shows no build notices.

## [2.0.3] - 2026-11-17

### Added

- **Jump from the herdr desktop Graph to a member's pane.** In Map and Flow, a click on a member with a live pane
  focuses that pane, as its row in the Wrangl pane does; Tab reaches the members and Enter or Space opens the pane.
  Run `ah herdr install` again to get it.

### Fixed

- **`ahd` stayed down after a plugin update.** A prompt hook that met an older daemon told it to stop and left the
  start to "the next call", but the session's later calls never start a daemon, so herdr desktop said `ahd` was not
  running until `ahd start` was run by hand. The hook now starts the replacement within its time budget. `ahd stop`
  still leaves the daemon down.
- **A short false "Waiting on a permission" warning in herdr desktop.** 2.0.2 stopped the false mail, but until the
  watcher's next screen read (up to 15 seconds) a working member still showed as blocked and raised a desktop notice.
  The watcher now publishes which members it reads and records what each read showed. A member it reads shows working
  until a read sees a prompt, and a real prompt shows within one 15-second check. A member it does not read (not a
  Claude member, proven gone, or on a team with no live Orchestrator) shows blocked at once, as before, so it can still
  raise the false notice.

## [2.0.2] - 2026-11-17

Fixes and one addition from the first day of use on herdr desktop.

### Added

- **The Flow diagram in the herdr desktop Graph tab.** `Map | Flow` tabs in the Graph header. Flow shows the team's
  members as cards in columns by role (Lead, Advise · design, Implement, Review) with the links between them, and a
  button that flips it between horizontal and vertical. It starts vertical in a narrow pane. The choice is remembered.
  The web UI's Flow is unchanged; both now use one layout file. Run `ah herdr install` again to get it.
- **Mouse-wheel zoom in the herdr desktop Graph tab, with fine, eased steps.** A mouse wheel zooms about the pointer
  with no key held; ctrl or cmd + scroll zooms too; a trackpad two-finger scroll still pans. The steps are the web UI's,
  and wheel, − / + and Fit ease along the web's own transition. Herdr gives mods no pinch gesture, so pinch still does
  nothing.
- **A notice when the herdr desktop mod is older than the plugin.** After a plugin update `ahd` rebuilds itself, but
  nothing refreshed the mod `ah herdr install` copied. A session start now says so in one line and names the command.
  Nothing is written to the herdr config.

### Fixed

- **Searching the Q&A History tab found nothing.** The filtered list only read through the daemon's HTTP bridge. Where
  the mod talks to the daemon through the `ah` command, every search failed. It now falls back to
  `ah msg history`, as opening a thread already did. Search text that starts with `--` is sent with one leading dash,
  so a few extra rows can match.
- **Every working member in herdr desktop was shown as blocked.** The Wrangl pane said "Waiting on a permission" and
  the Orchestrator got "may be blocked" mail within seconds of a member starting. Members of the Orchestrator's
  automatic team had no transport recorded, so the daemon never read their screens. It now reads them, and the pane
  follows each read: no prompt on screen shows working within one 15-second check, and a real prompt shows blocked.

## [2.0.1] - 2026-11-17

Fixes found on the first clean installs of 2.0.0.

### Added

- **Search and filters on the Q&A History tab in herdr desktop.** A search box and Team, Time and State menus. With
  any of them set, the list comes from the stored question history of the running teams, newest first, with "Load
  more"; answered decisions are not included in a search. Run `ah herdr install` again to get it.

### Fixed

- **Members were slow: about 25 seconds per tool call.** The check for new mail that runs between a member's tool calls was
  held by the daemon for its full 25-second wait whenever no mail was due. The daemon now answers it at once.
- **First-run setup was refused.** `/wrangl:hierarchy init` hands off to the roster wizard, and `ah roster init` and
  `ah roster add` refused because the session already owned the empty team Wrangl creates for an Orchestrator. An empty
  team of that kind no longer blocks roster edits.
- **Hooks could hang for 30 seconds while the daemon was being replaced.** A hook's question to a daemon that is
  shutting down no longer waits for it to exit, and a hook reads its standard input for 5 seconds at most.
- **`ah roster dismiss` and `ah roster disband --close` could not close members started in herdr desktop** when the
  team was the Orchestrator's automatic one. They now close.
- **The Orchestrator could not be clicked in the herdr desktop Wrangl pane while it was idle or waiting at a
  question.** Its row keeps its pane until the session is proven gone. Run `ah herdr install` again to get the mod's half.
- **A "member is blocked at a permission prompt" alarm was sent with no screen to confirm it.** It now says it is
  unconfirmed.

### Changed

- The user, and the session made admin with `ah admin grant`, are no longer refused by: roster edits while a team is
  live, `dismiss` of a member that is not clean (it warns, then closes), `spawn-one` of a second member of a role on
  another stream (it spawns one, as `spawn-ad-hoc` does), `ah msg close`, and the "agent-team skill is not loaded" gate.
- A member's launch rules name its work tree and its scratch directory outright for the file tools.
- The installer, run in a herdr desktop terminal, offers to run `ah herdr install`. It asks first and never installs
  without a yes.
- The two hooks that run every turn (prompt tracking, Orchestrator liveness) have a 10-second limit.

## [2.0.0] - 2026-11-14

> **Early testing: use with caution.** Wrangl 2.0.0 is the first public release and is still being tested. Use it only on work you
> have committed or backed up, and expect changes between releases. Report problems at
> <https://github.com/agentic-tool-labs/wrangl/issues>, and include the output of `ah roster doctor --brief`.
>
> **ah v1.** `ah` (v1) and Wrangl are separate plugins. Disable v1 while you use Wrangl:
> `/plugin disable ah@agent-tools`. See [ah2 and ah v1](./README.md#ah2-and-ah-v1).

> **Name.** Before this release the product was called ah2 and lived in agent-tools. It is now Wrangl: Agent Hierarchy, and it ships from its own repository,
> <https://github.com/agentic-tool-labs/wrangl>, no longer as part of agent-tools. The commands are still `ah` and `ahd`, and
> the home `~/.claude/ah2` has not moved. The plugin id is now `wrangl`: install with
> `/plugin marketplace add agentic-tool-labs/wrangl` then `/plugin install wrangl@wrangl`; slash commands are `/wrangl:…`
> and agent types are `wrangl:…`. See [upgrading](./docs/upgrading.md#renamed-plugin-id).

Wrangl is a Claude Code plugin that turns one Claude Code session into a small software team. Your session becomes the Chief
Orchestrator, which splits a job up and hands each piece to a specialist. This entry lists what is in the first release.

**What you need.** Claude Code 2.1.289 or later and a .NET 10 SDK (Wrangl builds its program once on your machine). herdr and
gitleaks are optional. There is nothing to upgrade from: follow [Getting started](./docs/getting-started.md).

### The team

- **The admin grant and the pipeline branch answer are database rows.** `ah admin grant` records the admin session in the state database, not in `admin.json`; a grant left in that file is taken in once. The branches you allow a pipeline run to work on directly are kept per repository in the database too (`ah roster pipeline-branch show|allow|revoke`); a value in `git config ah.pipelineBranch` is taken in once and unset.
- **Eleven roles.** Chief Orchestrator, Architect, Implementor, Reviewer, Ultra-Advisor, Docs-Writer, UX-Engineer, QA,
  Sub-Orchestrator, Task-Runner and Backlog-Groomer. Each runs on the model that suits its job. The
  [README](./README.md#the-team) says what each one does and never does.
- **Your own roles.** Define a role of your own, or install a role pack. See [custom-roles.md](./docs/custom-roles.md).
- **A deputy for the Orchestrator.** A member in the `deputy-chief` role can run the team while you are away:
  `ah roster duty on <member>` puts it on duty and `ah roster duty off` takes it back. A deputy never asks you anything (a
  question goes to the chief as a `QUESTION:` note), does not push or merge, and cannot be put on duty on a lane team.
- **Rules of engagement.** `ah rules add|edit|off|remove|list` keeps your operating principles at four levels (the repo, a
  team, a project, a goal) and puts them in front of every member at its start, after a compaction and with each brief. They
  are guidance. A rule never widens permissions.
- **Capacity.** The Orchestrator follows a written rule for how many members to run and when to dismiss them. It is a rule the
  Orchestrator follows; ah does not enforce it.
- **Lanes.** A Sub-Orchestrator runs one separate line of work for the Orchestrator.
- **An Orchestrator with several teams.** The Orchestrator of a run can log its decisions while it owns more than one team.
- **The Architect's legwork.** A lookup by the Architect is nudged to a gopher instead of blocked, and the legwork subagents
  it starts are never gated as the Architect.

### Mail, the Board and the Sitrep

- **Members talk through mail.** `ah msg` sends briefs and reports, forwards an entry with a note (`ah msg forward`), takes a
  report in parts (`ah msg fill --part`), and closes a request nobody can answer (`ah msg withdraw`, `ah msg close`).
- **Members on the same work message each other directly.** Two members in one work stream, or holding open requests the
  Orchestrator dispatched on one item, are linked by that fact: the first `ah msg send` between them opens their link, with no request and no Orchestrator
  turn. The link is on record, the Orchestrator can read and revoke it, and it ends when the shared work does. `ah msg
  link-request` is for a teammate a member is not assigned with.
- **The Board follows what happens.** A member's `DONE` reply puts its item in review, a Reviewer's or QA's pass approves it,
  and a merge or ship closes the items it carried. `ah roster checklist show --trail <id>` lists an item's history and prints the
  command that undoes the last automatic move. `ah roster checklist ship <sha>` and `ah ledger ship <sha>` record a ship.
- **The Sitrep** shows what is waiting on you, blocked, in progress, up next and completed. Work items carry a description
  written for a person (`ah work add "<title>" --body-file -`). In progress lists the members at work and the jobs running,
  and a reported or waiting row more than an hour old says it is stale.
- **A follow-up re-arms the exchange.** A plain Orchestrator follow-up to a member records the dispatch and puts its item back
  to dispatched.
- **A brief is delivered once.** A brief the member has read or reported on is not delivered to it again.

### Keeping members in bounds, and getting them back

- **Work-tree gate and launch rules.** Members write only inside their own work area. Member launch rules are on by default;
  `ah allow off` opts out. ah does not turn on Claude Code's sandbox for a Claude member: it runs under the sandbox setting of
  your own Claude Code settings. A Codex member started through its app-server runs in Codex's sandbox: `workspace-write` unless its
  launch asks for `read-only`, with no network either way. ah is silent in a session that is not part of a team.
- **Alarms.** ah tells you when someone is stuck, silent, overdue or idle while work waits. With herdr installed, an alarm
  shows as a herdr notification with no setup; a set `alarm.command` wins. A user alarm repeats about every 60 minutes while a
  problem lasts, and says when it cleared. `ah alarm test` shows which route it used. If automatic recovery stays paused for
  10 minutes, the Orchestrator gets one message naming the cause. A Claude member is reported blocked only when its screen
  shows a prompt.
- **A hung daemon is replaced.** `ahd heal` replaces the daemon only when it has proven a hang itself: no reply to four
  health probes, after watching it for at least a minute and a half. A daemon that answers, however slowly, is never
  touched. A write that gives up on a busy database and the Orchestrator's overdue timer start the heal in the background when the daemon does not answer;
  hooks never do. It restarts the daemon at most three times in 24 hours, and `ahd heal --force` (yours alone) skips
  that cap, never the proof. `ah roster doctor` shows what the last heal did. Not in this release: a heal for a daemon
  that answers its health check while it holds a database write open, and `ahd stop --heal` / `ahd restart --heal`.
- **Pause one member.** `ah roster pause-member <member>` (or `--team <T>` for every member) stops ahd's nudges, re-deliveries
  and alarms for that member until `ah roster resume-member`, which restarts its request clocks. Each member is told by mail.
  Unlike `ah roster quit`, it closes nothing.
- **Recovery.** After a crash or reboot, `ah roster resume --all` brings back every team you own. `ah roster quit` holds a team
  so it is not relaunched on its own. `ah roster dismiss <name>` closes a clean member, and dismissing a member or disbanding a
  team closes the briefs it still owed. `resume` works on a home that has no `run` directory yet, and `dismiss` counts a member
  branch merged with `--no-ff` into a pushed train as shipped. An Orchestrator restarted with `--agent` registers when its
  session starts, so the views show its state at once. The recovery sweep skips a member whose record the database refuses
  and goes on with the others.
- **Hooks run as a mod.** Wrangl's hooks are one mod module (`hooks/register.ts`), so Wrangl works where an organization manages
  Claude Code and mods still run. Where the managed settings set `allowManagedHooksOnly`, Wrangl needs a managed install by your
  administrator. See [Managed Claude Code](./README.md#managed-claude-code).
- **Standing decision rules.** A rule can be proposed and then accepted with your proof; a rule that changed since it was shown
  is refused.
- **A team in another repo.** When `--team` names a team whose home is in another repo, that repo supplies the member launch
  settings, the consent records and the member-allow switch (`ah allow list --team` reads it there too).

### Decision records (ADRs)

- **`ah adr` keeps durable decisions** in the state database: `add`, `show`, `list` (`--waiting`, `--governs`), `settle`,
  `supersede`, `deprecate`, `link` and `sync`. Each record keeps its own history.
- **Members see the decisions that govern their work.** Every member's rules block ends with the standing ADR line, a brief's
  footer names the accepted and proposed ADRs that govern its item, and the Orchestrator's session start lists the ADRs
  waiting on you.

### One database, with backups

- **ah keeps its state in one database,** `<ah home>/state/ah.db` (by default `~/.claude/ah2/state/ah.db`): teams, member
  sessions, mail and peer records, and also the gate log, the question log, tracked jobs, decision logs, checkpoints, consent
  asks, team holds, progress documents (`ah progress list|show`) and work-tree grants. If a team's hold cannot be read, the
  team is treated as held.
- **The hierarchy's state is in the database only.** State files left by an earlier build are copied in once by an import pass
  and not read again; state locks live under `<ah home>/state/locks`. `ah progress off|on <team>` is a database switch, and an
  old `.off` file is ignored. The QA job queue, the known-dirs index, usage rows, the ultra-gate, session roles, the
  permissions ledger, stall and overdue state, wake stamps, recovery records, archived holds and the watch timeline are tables
  of the database too; their old files are not imported. So are the recovery switch and the Codex trust pre-approvals, and a
  fresh home writes no fallback files. Every folder is recorded under one canonical path, so a folder reached through a symlink,
  or as both `/tmp` and `/private/tmp`, is one record. Read connections cache their prepared statements, and a retried write whose result was too large
  is answered from its record, not run again.
- **Backups are made for you.** The daemon takes a scheduled backup about every hour when something changed, and one before
  every database upgrade. `ah db backup`, `ah db list`, `ah db check` and `ah db restore` are the tools. A restore only prints
  a plan until you add `--apply`, and it refuses while the daemon is running.
- **A broken database says so.** If the database is missing or unreadable, `ah` stops with a plain message that points at
  `ah db restore`. `ah roster doctor` reports it.

### Seeing it

- **The terminal view** (`ahd tui`) shows the teams and each member's state, process id and work tree.
- **The web page** (`ahd ui start`) has Overview, Board, Graph, Timeline and Conversation tabs and needs no sign-in. A card
  waiting on you shows its question with an answer form, plus **Already done** and **No longer needed**, and the item's
  description, folded to a few lines with **More** (in herdr's Q&A too).
- **Wrangl inside the herdr desktop app.** `ah herdr install` adds a sidebar with an **Agents** tab and a **Wrangl** tab, and five
  views: Sitrep, Q&A, Board, Feed and Graph. Q&A, Board, Feed and Graph open in a desk column beside the terminals or each in
  its own window, and the Graph has a scrubber to look back over the run. Every wait on `ah` is bounded, and when `ah` does
  not answer the view says why, so it cannot stall herdr. A team can be dismissed from its row or its sitrep: one with nothing
  running goes at once, and one still working offers **Pause, then quit** (members save their work and the team can be resumed)
  or **Force quit**. `ah herdr install --uninstall` removes it. See
  [herdr-desktop/README.md](./herdr-desktop/README.md).
- **Under herdr,** `ah roster spawn-one` and `spawn-ad-hoc` take `--here` or `--tab <tab id>` to place a new member, and
  `ah roster create --yes` plans, launches and records a team in one call. A team can have members on the herdr desktop app
  and on herdr in a terminal at the same time.
- **Cost and timing.** `ah usage-report --team <name>` and `--fleet` show tokens per member or per team. `ah roster timing`
  shows how long each stage of an item took.
- **Processes.** `ah proc list --match <text>`, `ah proc wait <pid>…` and `ah proc stop <pid>…` find, wait for and stop the
  processes you started.

### Known issues

- **Capacity rules are not enforced.** The Orchestrator follows them; ah does not stop a spawn that breaks them.
- **Labelled backups pile up.** No verb removes a backup with a label (`pre-migrate-…`, `pre-restore`, or one you took with
  `--label`). Check `ah db list` and the size of `<ah home>/state/`.
- **The daemon can be heavy.** On a very busy home it can use a noticeable share of one CPU core and a lot of memory. This
  release reduced its memory use: in a soak test its heap fell from 310–505 MB to 196–245 MB, and it no longer keeps every
  mail delivery attempt and several copies of each exchange listing in memory (measured live, 57,000 cached attempt rows fell
  to 23). To do less work when nothing changes, the status band and the views now show a member's liveness up to 5 seconds
  old (it was 2), the band asks the daemon every 4 seconds (it was 2), and the daemon looks for new dispatches of a session
  that has none once a minute or as soon as one is written. A status document that nothing has changed is served again
  with its times rewritten, not computed again; one whose change that check missed is at most 60 seconds stale. More work is under way.
- **herdr desktop sidebar.** The **‹ main** button always goes back to the `main` layout, even if you normally use another one.
  The sidebar needs `ah` on the PATH the herdr app sees.
- **A deputy cannot be put on duty on a lane team** (refused with `duty-lane-team`).
- **Managed Claude Code gaps.** Under the mod, a subagent's stop hook cannot send a finished subagent back to work (it is logged
  only), `transcript_path` is worked out from where Claude Code keeps transcripts, and `permission_mode` is not passed to the
  hooks.
