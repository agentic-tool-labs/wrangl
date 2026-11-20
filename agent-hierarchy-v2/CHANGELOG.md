# Wrangl: Agent Hierarchy changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions are the plugin's `version` in `.claude-plugin/plugin.json`. Feature
detail lives in the README and in [docs/](./docs/).

## [2.0.33] - 2026-11-20

### Fixed

- **A second herdr desktop can spawn members.** In a herdr desktop started with `HERDR_APP_HOME` set, a stream spawn
  was refused `stream-tab-unavailable`, because ah removed that variable before it ran `herdr-app` and the request
  went to the default desktop. ah now keeps `HERDR_APP_HOME` on those calls.

## [2.0.32] - 2026-11-20

### Added

- **A lone test run gets more of the machine.** When nothing else is testing, `ah test run` without `--tokens` gives a
  run sized `half` up to three quarters of the test allowance (at most 12) instead of 2, and re-plans its parallel
  settings to match.
- **QA launches without a model set.** `spawn-one qa` uses `sonnet` when the roster names no model, as docs-writer and
  ux-engineer already use their defaults.

### Changed

- **An Implementor may run its project's whole test suite when nothing else is testing.** When something is, the
  refusal no longer suggests `--tokens 2`.
- **A member's send to a teammate opens its own link** when the team's auto-grant setting covers the pair on the
  current task, so an Implementor's handoff to its Reviewer needs no `ah msg link-request` first. Other pairs get the
  refusal with the reason and the command to request a link.
- **A brief's section markers are accepted in any heading form:** `[1] goal` as a heading of any level, a bare line or
  bold. A marker inside a closed code fence does not count.
- **Several checklist items may carry the same `--ref`.** A lookup by ref alone takes the newest open item.

### Fixed

- **An empty `ah msg list` says why:** one line on standard error with the team it filtered on, the pools it read, and
  how many exchanges the team filter hid.

## [2.0.31] - 2026-11-19

### Added

- **`ah work done <id>`** closes a work item wherever it lives: a backlog item through `ah backlog done`, a team's
  checklist item through `ah roster checklist done`. It takes `--commit` and `--note`.
- **A Recommended badge in Q&A.** An option whose label ends in "(Recommended)" shows a Recommended badge in the web
  page's questions, the board's item dialog and herdr desktop's Q&A. More than one option may be marked.
- **An alarm when a QA entry waits with no QA member.** After 10 minutes, the Orchestrator is told which entry is
  waiting and how to start a QA member.

### Changed

- **`done` is green everywhere.** The Claude Code mod and the shared status table now give `done` the `ok` tone the
  terminal view already used, and a test keeps the terminal view and the table in step.

### Fixed

- **Progress records name a report by its message id** (`<id> (ah msg show <id> response)`) instead of a path under
  `.claude/ah2/msgs/` that never existed, and read reports back from the message store.

## [2.0.30] - 2026-11-19

### Changed

- **The documentation site's landing page reads more plainly.** Same facts and commands, simpler wording.

## [2.0.29] - 2026-11-19

### Added

- **Order, pin and collapse teams in herdr desktop's Wrangl pane.** Each team row has a `⋯` menu: Pin to top, Unpin,
  Move up, Move down, Collapse or Expand, and Dismiss. A ▾/▸ arrow also collapses a team to its row, which still shows
  its counts and alerts. The order, pins and collapsed state are kept in herdr's settings, per user and per machine.

### Changed

- **Team rows in the Wrangl pane.** A click on a team row no longer opens its sitrep; the Sitrep pill does. Dismiss
  moved from the row's × into the `⋯` menu. The "Waiting on you" line under a team is gone; the team's count badge
  already shows it.

## [2.0.28] - 2026-11-19

### Fixed

- **`ah roster approve <member> worktree` now lifts the gate it approved.** The gate finds the member through the
  team's home, and a plain `git clone` the approval covers is let through.
- **Check-in alarms count from delivery.** A request's age runs from when it was sent, `ah msg send --ref <bare id>`
  records the send, a response holding only parts counts as filed, and an open review quiets the request it reviews.
  A request that was never sent gets one "was not sent" note.
- **A test run started after `cd` or a variable is joined to its test slot.**
- **QA and review prompts match by item**, the latest report decides whether QA is owed, and the QA cadence question
  is asked once.
- **Dismissed members leave the roster and the alarms.** A dismiss whose close is not confirmed no longer reports
  success.
- **The rerun line a refusal prints runs as printed.**
- **A member's TMPDIR is a real directory outside any checkout**, removed when the member is dismissed.
- **QA has a fallback when a repo has no `run-set.sh`.**
- **Three `ah msg` rough edges:** clearer send lines, and a text-only note with an empty `--ref` is sent.
- **A test killed at the time cap mails the Orchestrator.**
- **The Architect's shell nudge is checked** and stays a nudge, never a refusal.

## [2.0.27] - 2026-11-19

### Added

- **Colour in the terminal view.** `ahd tui` paints each member in its role's colour (the web page's palette), and
  working, blocked, waiting-on-you and done in their state colours. It uses 24-bit colour when the terminal says it
  can, otherwise 256 or 16 colours. `NO_COLOR`, `TERM=dumb` and `--once` are unchanged.
- **The mascot at launch.** An interactive `ahd tui` shows the Wrangl mascot above the first screen until your first
  key or click. It is skipped on a terminal with fewer than 24 rows.

### Changed

- **The documentation site's theme button is a glass switch** with a pixel sun and moon. The old button read "Dark
  theme" whatever the theme was.
- **Reviewer wording defers to a "reviewers run nothing" rule.** Where the directive, the role notice and the default
  reviewer order tell a Reviewer to delegate a run or ask for one, they now add: unless a rule of engagement says
  reviewers run nothing.

### Fixed

- **`ahd tui --once --team` could print a screen missing rows** on a busy machine: it judged the data as loaded on the
  live feed, then drew from an older copy. It now judges the copy it draws.
- **A rebuild lock held by another user's process is no longer taken over.** Only a process that does not exist counts
  as gone.
- **A background rebuild that cannot be started is recorded as failed at once**, where it used to read "in progress"
  for 15 minutes. `ah roster doctor --fix` says "rebuild could not start" with the reason.

## [2.0.26] - 2026-11-19

### Added

- **A 43-second tour video on the documentation site's landing page**, linked from the README. It shows the roles
  and hierarchy, one request from brief to merge, `/wrangl:pipeline`, the web page, the terminal view and the Herdr
  desktop layout. The plugin itself is unchanged from 2.0.25.

## [2.0.25] - 2026-11-19

### Added

- **A simpler theme menu in the web UI.** One light/dark toggle, then a list of looks and colour presets with the
  active one marked. The presets come from well-known terminal palettes and draw flat; the existing looks keep their
  names. "Customize…" opens the looks editor. A stored choice from an earlier version shows the same colours.
- **The web UI has a favicon**, the Wrangl icon.
- **`ah roster doctor --fix` rebuilds a stale ahd build.** When the `ahd-build` row says the binary is behind, `--fix`
  starts the same background rebuild session start does, also when `auto_rebuild` is off. Run doctor again to see
  the result.

### Fixed

- **A stale rebuild lock no longer makes "ahd is being rebuilt" repeat for ever.** Where `flock(1)` is absent, a lock
  older than 10 minutes whose process is gone is now taken over. When that process is still alive the rebuild fails
  with a line naming the lock and the pid, and session start shows the failure instead.

## [2.0.24] - 2026-11-19

### Added

- **`ah roster doctor` checks the ahd build.** A new `ahd-build` row compares the installed binary's stamp with the
  plugin. When the binary is behind it gives the reason no background rebuild started and the command that fixes it.
  It only reads.

### Fixed

- **A stale ahd build is no longer silent.** When the binary is behind the plugin and no background rebuild can start,
  session start now says why in one line and names `/wrangl:hierarchy update`.
- **The rebuild check runs whether or not the session registered with the daemon.** A session that had not registered
  at the moment of a plugin reload skipped the check and started no rebuild.
- **A background rebuild that stalled is retried after 15 minutes**, not left for 24 hours.
- **A failed background rebuild is recorded as failed.** The hook wrote the install stamp in a form the installer could
  not patch, so a failed rebuild stayed "started" and nothing reported it.

## [2.0.23] - 2026-11-19

### Added

- **`/wrangl:hierarchy update`.** Rebuilds the native binaries for the installed plugin version and updates the
  install stamp, with no questions. For after a plugin update when the background rebuild did not start. It leaves
  the `ahd` link, PATH and the login item alone.

### Changed

- **The Orchestrator's crown is gold in the herdr pane.** The top Orchestrator's icon was the theme's text colour.
  A Sub-Orchestrator's stays purple and members keep their lane colours.

## [2.0.22] - 2026-11-19

### Added

- **Answer a question on its herdr Board card.** A card in the Board's "Waiting on you" column is now the full
  question card: every option, Other with your own text, Send, and Not needed (Already done, No longer needed). It
  sends the same way the Q&A window does.
- **Item details from a waiting card.** A waiting item's card has a Details button that opens the item's sheet, which
  starts with who asked. An item that is not registered as work says so instead of showing an empty sheet.
- **Agent details on the herdr Board.** A member's name in an item's owner, a team's Members list or a lane's header
  opens the member's sheet in the Board: its state, pane and page links as on the Graph, and a History of the items
  it holds, each linking to its item sheet. The Graph's own agent sheet is unchanged.

### Fixed

- **Role icons and colours show for a real team's members in the herdr pane.** A member named `<team>-<role>` was
  drawn with the generic icon and no colour, because the pane was not sent the member's role and could only read it
  from a name that began with the role. The daemon now serves each member's role with its state, and the pane also
  reads the role from a team-prefixed name.

## [2.0.21] - 2026-11-19

### Added

- **The herdr pane shows a report going back.** While a member's report waits to be taken up, a dot travels from the
  member back up its line toward whoever briefed it. The daemon serves the waiting reports as `edges.report` on a
  team's topology. The Graph window does not draw this dot yet.

### Fixed

- **A Q&A History row stays readable in a narrow herdr pane.** In a pane about 400 px wide the question text was
  squeezed into a column a few characters wide and wrapped one or two letters per line. The row now wraps: time,
  header and source on the first line, the question on its own line, the answer under it. A long answer is cut to
  the chip's width with the full text in the tooltip; answers in History are no longer selectable text.

### Changed

- **The machine counts as overloaded on real CPU use, not the load average.** The load trigger was the 1-minute load
  average per CPU, which counts waiting threads and could read high on a machine whose CPUs were mostly idle. The
  trigger is now CPU use of 90% or more held for 180 seconds, read from the system's own CPU counters on macOS and
  Linux. The load average is still shown in `ah msg daemon status` and triggers nothing. In `ahd-load.json`,
  `cpu_busy` and `cpu_busy_seconds` set the rule; `load1_per_cpu` is accepted and ignored. Where CPU counters cannot
  be read there is no CPU trigger, and status says so.
- **Role icons in the herdr pane take their Graph lane's colour.** A member's icon is drawn in the colour of the
  lane the Graph puts its role in (Lead, Advise · design, Implement, Review, Test) in every state, so the pane and
  the Graph agree. A gone or no-signal member's icon is dimmed.
- **The herdr pane does less work while a dispatch dot moves.** The team tree's model was rebuilt on every frame of
  the dot, about 30 times a second. It is now built once per change of the data, the folds or the age clock.
- **The "idle and clean" hint reads a team once per check.** Its roster and open-exchange reads were repeated for
  every idle member inside the prompt hook. They are shared across the members of one check; the answers are the same.
- **The Orchestrator can override load pacing.** When a spawn is put off because the machine counts as overloaded,
  the Orchestrator that owns the team is told each trigger with its value, how many Claude sessions run and when the
  next paced spawn is allowed. Re-running `spawn-one` or `spawn-ad-hoc` with `--despite-load` spawns anyway, prints
  that it did, and writes a `load-override` row to the daemon's journal. Anyone else passing the flag is still paced.

## [2.0.20] - 2026-11-19

### Added

- **The herdr pane shows a dispatch in flight.** While a member is being briefed or reviewed, a small dot in that
  member's role colour travels down its connector line in the team tree, and the line takes the same colour, until
  the brief is picked up or answered. It follows the same rule and timing as the Graph window's dots; a dispatch
  into a folded sub-team shows on that sub-team's row. Nothing moves, and no timer runs, when nothing is in flight.

### Changed

- **A member reads Idle by default; "No signal" means it cannot be located.** A member with no activity record yet
  (just spawned, between heartbeats, or after a daemon restart) read "No signal". It now reads Idle. "No signal" is
  shown only for an idle member that nothing locates: no running registered process, no fresh heartbeat, no herdr
  pane showing an agent, and no sign of life for 30 minutes. Blocked and Gone are unchanged, and no stuck or blocked
  alert changes.
- **The Orchestrator's dismiss is never refused as not clean.** `ah roster dismiss <member>` run by the Orchestrator
  that owns the team (or the top Orchestrator for a sub-team's member) closes the member and returns the reasons it
  was not clean as `warnings`. The work tree, its branch and uncommitted files stay. Other callers are still refused
  and use `--close`.

### Fixed

- **A member that is starting up no longer flashes Blocked.** A permission notice during a member's first turn was
  read as blocked for a few seconds and could raise an alert. While a herdr member is starting it reads Working; a
  prompt actually seen on its screen still reads Blocked.
- **A question asked again shows as an open card in the herdr pane.** When the same checklist row was set to wait on
  the user again with a new question, the pane kept the closed card with the earlier answer.
- **The "idle and clean" hint agrees with dismiss.** The hint used a different check from the dismiss it suggested, so
  it could name a member that a bare dismiss then refused, and it suggested `--close`. It now runs the same check and
  names the bare dismiss.
- **Dismiss works in a repository with no `origin` remote.** Whether a member's work is shipped was judged only against
  `origin`, so without one no member was ever clean. It is now judged against the local main line.
- **Machine load no longer wakes Orchestrators.** Every change of the machine-load state (overloaded, ok) mailed each
  team's Orchestrator and started a turn, so a machine hovering near the threshold woke every Orchestrator again and
  again with nothing to act on. A change is now written to the daemon's journal and shown in `ah msg daemon status`,
  and nobody is mailed. Spawn pacing is unchanged.
- **An idle-capacity alarm is told once.** The 20-minute `work-still-waiting` reminder repeated the idle-capacity
  alarm for as long as the same members stayed idle. The reminder no longer lists it, and no reminder is sent when a
  team has no outstanding work.

## [2.0.19] - 2026-11-18

### Added

- **Running jobs are shown on the team.** A test run or other background job a member or the Orchestrator started
  now appears under whoever started it, in the web UI's tree, Map, Flow and Overview and in the herdr pane, so a
  long QA run no longer looks like nothing is happening.
- **The Flow has a Test column.** Work that is being tested is drawn in its own column between build and review.
- **Subagents are shown, and told apart from peers.** A subagent the Orchestrator or a member is running appears
  under its parent while it runs and leaves when it ends. Subagents are marked as such, and a peer's row and card say
  "peer session" in their tooltip.

## [2.0.18] - 2026-11-18

### Added

- **A team can be given a display name.** `ah roster rename "<name>"` (and `--clear`) stores a name of up to 60
  characters that both UIs show wherever the team is named. The web UI has a Rename button in the team's header and
  the herdr pane has one beside Dismiss in the team's sitrep window; each opens a small dialog with Save, Clear and
  Cancel. Only what is shown changes: member names, mail addresses, `--team` values and records keep the team's real
  name, which stays visible as a tooltip.

### Fixed

- **A member's waiter always ends.** A member that ran a background test and then waited with `ah roster wake` could
  wait for over an hour: the job was tied to the waiter's own process, so the waiter waited on itself, kept the
  member's session busy and blocked its dismiss. A waiter is never taken as a job's process, never counts a job tied
  to itself, and a member's waiter now ends at the same 20-minute limit as an Orchestrator's.
- **A job started with `ah test run` is tied to its real process.** It was matched by the word `ah`, which the
  wrapper script and the test command do not carry, so it took the next `ah` command the session ran, or none, and
  was then reported overdue or ended as unseen while the test ran on. It now binds the wrapper script's own process,
  the outer one when a test starts a nested run, and a label that begins with `cd <dir>` is read correctly.

- **No tall gap around a team's alert in the herdr pane.** A team-level alert such as "Waiting on you" grew to fill
  the free height of the pane, leaving a large empty space between the team header and its first row. It is one line
  high again.

## [2.0.17] - 2026-11-18

### Added

- **The team commands are put in front of the Orchestrator.** Its instructions now carry a short "Team commands"
  block (spawn a member, brief it, read its report, dismiss it, and what to run when a command refuses). It gets a
  one-time note the first time it runs a chain role through the Agent tool while peers are the setting, starts a
  session with raw herdr or tmux commands, or spawns or dismisses a member without having read the team rules. When
  the user asks it to start an agent that matches the hierarchy, it gets the exact spawn command with that prompt. A
  refused or failed spawn ends by naming `ah rules show peer-roster`, and each chain role's description says how the
  role is started. All of these are notes: no call is held.
- **A lane's Orchestrator is labelled "Sub-Orchestrator"** wherever the web UI and the herdr pane name it, with the
  lane in the tooltip. Addresses, filters and pane lookups still use the real name.

### Fixed

- **The "unreviewed" alarm counts a review the Orchestrator dispatched.** It fired for a report that had been
  reviewed, whenever the review was sent as its own request to a reviewer and not as a link from the reporting member.
  A review request on the same checklist item now counts, and an item already done or dropped is never alarmed.

- **An update restarts the daemon without an error.** After placing a new binary the installer asks the running daemon
  to restart. Run by the background rebuild or from the herdr mod, that request was refused with
  `error: drain_refused: a drain needs a caller the daemon can name`, and the old daemon kept running until the next
  `ah` command from a registered session. The daemon now accepts a restart from an unnamed caller when it can see that
  its own binary file was replaced, and still refuses one when nothing changed. Where a restart is refused anyway, the
  installer prints a plain warning and no longer exits with an error. On platforms other than macOS on Apple silicon
  the daemon cannot read that file's identity yet, so the restart still waits for the next `ah` command there.

## [2.0.16] - 2026-11-18

### Added

- **The Wrangl pane in herdr desktop is a tree.** Each team shows its Orchestrator at the root, its members under it and
  each lane as a nested subtree, with connector lines and a control to fold a branch. Every row has a role icon in the
  role's colour and the same state mark the Graph uses, so a role and its state look the same in both places.
- **Checklist items can be ordered.** `ah roster checklist add ... --after <item>` (or `--after last` for the item just
  added) and `checklist set <item> --after <item>` say an item waits for another. A waiting item shows what it waits on,
  becomes ready when that item is done or dropped, and draws no staffing alarm meanwhile. The staffing alarm's text also
  offers `--after` as the way out. Nothing is blocked: a waiting item can still be dispatched.

### Fixed

- **A session whose hooks are off is told so.** When the native binary is not built, or the state home is refused,
  every session is told at start, not only a team member, and the status line says "Wrangl: not built, hooks off".
  The session start is then retried on each prompt, so once the binary works the session gets its directive and is
  registered without a restart.
- **`checklist set --state` shows what was asked.** When the state is shown under another name, the answer carries both
  and says so in one line (for example, `in_progress` is shown as `dispatched`).

## [2.0.15] - 2026-11-18

### Added

- **You choose how the Orchestrator runs its roles.** A new `dispatch` setting has four values: `peers` (each role in
  its own session, the default and the behaviour until now), `subagents` (each role inside the Orchestrator's session),
  `prefer-peers` and `prefer-subagents` (a default the Orchestrator may depart from, saying so). `/hierarchy init` asks,
  and `/hierarchy dispatch <value>` changes it later. A subagent starts faster and needs no terminal, but it cannot be
  messaged while it works and the daemon's timers, mail and alerts do not cover it. Legwork roles stay subagents and
  sub-orchestrators stay peers under every value.
- **The Orchestrator's own subagents show on the Graph**, as nodes under the Orchestrator with their agent type, the
  way a member's subagents already did.

### Fixed

- **An Orchestrator session that was never registered can start its team.** When the session-start hook had not run
  for a session (for example, the native binary was not built yet when the session started), `ah roster spawn-one` and
  the other owner commands refused with `owner-unproven`. The session is now registered when it runs one, and the
  command carries on. A team member is still refused, and a team another live session owns is still not taken over.
  The refusals that remain say what to run next.

## [2.0.14] - 2026-11-18

### Changed

- **The installer makes sure the .NET SDK can build this version.** When the installed .NET 10 SDK is out of date (a
  NuGet advisory names one of the packages it supplies), the installer says so with the SDK's version and path. At a
  terminal it offers to install the current .NET 10 SDK privately under the Wrangl home and build with that; it offers
  the same when a build fails with the system SDK. Nothing is installed without a yes, and the build already made is
  kept if that update fails. Without a terminal it prints the command to run. A private SDK is used before the system
  one, and a private SDK that cannot build falls back to the system SDK.

## [2.0.13] - 2026-11-18

### Added

- **An update dialog in herdr desktop.** When the daemon is rebuilt after a plugin update, or the herdr mod is
  installed, a dialog shows the Wrangl logo, the two steps with their state, and a scrolling pane of the build log and
  the installer's output. Closing it stops nothing. With auto-update off it offers an "Install now" button once the
  daemon is built. It replaces the "Building ahd" and "ahd is ready" notices while it is open.

### Fixed

- **Installing no longer fails on a NuGet advisory.** The build treated every warning as an error, so once an advisory
  was published for the compiler package of an older .NET 10 SDK (`NU1903` on `Microsoft.DotNet.ILCompiler`), `ahd`
  could not be built on a machine with that SDK. Advisories are now reported as warnings. Updating the .NET SDK clears
  the warning.

### Changed

- **A ship leaves a backlog item open while it has an open sub-item.** A release used to mark an epic done when its own
  rows shipped, even with work open under it. It now stays open, and closing or dropping its last open sub-item closes
  it, unless it still has an open checklist row in any team. `ah backlog done` on the epic itself still closes it.

## [2.0.12] - 2026-11-18

### Fixed

- **The herdr desktop Graph opens again.** It failed with "This view could not be re-rendered" because it made focus
  handles while drawing. They are now made when the view starts and when a node is clicked.
- **The Orchestrator's own permission prompts no longer raise alerts.** 2.0.11 recorded each one as a blocked member,
  so every prompt in the user's own terminal raised a "waiting on a permission" alert. Members' prompts still do.

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
