# Wrangl in Herdr desktop

This page is for people who use the Herdr desktop app and want to see their Wrangl teams in it. After reading it you
will be able to install the Wrangl sidebar, switch to it, and read and act on what it shows: who is working, what
waits on you, and how the work moves. Words you have not met yet are in the [Glossary](../docs/glossary.md).

The Wrangl sidebar lives in Herdr's left sidebar, sharing the agents list's place behind two tabs, **Agents** and
**Wrangl**. Each running team is a group, with its lanes nested under it, and each member is a row. Beside it, the
**Desk** holds four larger views: **Q&A**, **Board**, **Feed** and **Graph**.

Wrangl 2.0.0 is a beta, and so is this sidebar. If you have the older `ah` plugin (v1), keep it turned off while you
use Wrangl (see [Wrangl and ah v1](../README.md#ah2-and-ah-v1)).

## Install

Run this once, in a terminal:

```sh
ah herdr install
```

It copies the Wrangl mod and the `wrangl` layout into Herdr's home folder (`$HERDR_APP_HOME`, else
`~/.config/herdr-app`). It writes nothing else, and it never overwrites a mod it did not install or a file you
edited. Herdr picks up the new files by itself, so there is no restart.

To remove it again:

```sh
ah herdr install --uninstall
```

If you edited the `wrangl` layout and it has lost the Desk column, the install says so. To put the shipped layout
back (your edited one is kept as a backup beside it):

```sh
ah herdr install --replace-layout
```

**`ah` must be on Herdr's PATH.** The sidebar runs `ah` to read your teams. Herdr started from the Finder loads your
login shell's PATH. If `ah` is not on it, the sidebar says "ah is not on Herdr's PATH". The installer links `ah` into
`~/.local/bin` by default.

## Switch to the Wrangl layout

- **Into Wrangl:** press `ctrl-shift-l` to step through Herdr's layouts until you reach `wrangl` (it can take more than
  one press), or go there directly:

  ```sh
  herdr-app do layout.switch --name wrangl
  ```

  To give it one key, add a line to Herdr's `keys.json`: `"global": { "<your key>": "layout.switch:wrangl" }`.
- **Back:** the same way, with `ctrl-shift-l` or `herdr-app do layout.switch --name main`. The Wrangl layout has no
  button of its own for it.

## The sidebar

### Tab strip

- **Agents** shows Herdr's own agents list; **Wrangl** shows your teams. One is shown at a time, and the choice is kept
  across restarts.
- The **Desk** button shows or hides the Desk (see [The Desk](#the-desk)).
- The **…** menu has **Wrangl settings…**, and **Update to vX** when the Wrangl plugin carries a newer version of the
  mod. A dot on the **Wrangl** tab means an update is ready.

### Header

The mascot, **Wrangl**, and the **Needs you N** badge: questions waiting on you plus blocked rows. Click the badge to
open Q&A. Q&A and the Board count only the questions, as **Waiting on you N**.

### Team rows

Each team row shows:

- the team's name (`↳` marks a lane, nested under its parent team);
- its **goal**, when the team has one of its own, as a second, muted line under the name. Hover the row to read the
  whole goal, with its "done when" if it has one;
- the counts: `N needs you`, then `N working · N blocked`;
- a **Sitrep** pill;
- on a top-level team, a small **×** to dismiss the team (see [Dismiss a team](#dismiss-a-team)).

Click the row or the **Sitrep** pill, or press Enter on it, to open that team's sitrep in a window.

### Member rows

Each member row shows a state mark, the member's name, what it is working on, and for how long.

| Mark | State |
|---|---|
| spinner | **Working** |
| steady dot | **Running**: the Orchestrator is live and between turns |
| ! | **Blocked** |
| ring | **Idle** |
| × | **Gone** |
| ? | **No signal** |

The Orchestrator's own row shows **Working** with the spinner while its turn is open, and the steady **Running** dot
when it is at rest between turns, so a quiet Orchestrator never looks like a stuck spinner.

When exactly one live Herdr terminal belongs to the member, clicking the row focuses that terminal. Otherwise the row
is not clickable.

### Alerts, other agents and the status line

- **Alert lines** start with `⚠`. Alerts meant only for the Orchestrator are left out, as on the
  [web page](../docs/web-ui.md).
- **Other agents** lists Herdr agents that are not Wrangl members, so nothing the built-in list showed is lost. Click
  one to focus it.
- **The status line** at the foot reads `ahd · N teams · updated <age>`, or says why it cannot read your teams (see
  [If something looks wrong](#if-something-looks-wrong)).

## The sitrep window

A team's sitrep opens in a window with five regions: **Waiting on you**, **Blocked**, **In progress**, **Up next** and
**Completed**. Each region shows at most 10 rows, then `+ N more`. A decision waiting on you has an **Answer in Q&A**
link. Close the window with `esc` or **×**.

## Dismiss a team

Open the dialog from a team row's **×** or the sitrep window's **Dismiss**.

- A team with no running session is dismissed at once, and a notice says so.
- A team that is still working lists its running members, with any uncommitted or unmerged work found, and offers:
  - **Pause, then quit:** each member is asked to save its work and the team is put on hold so it can be resumed
    later; then the sessions close. While it pauses you can still force quit, or leave the team paused.
  - **Force quit:** close the sessions now.
  - **Cancel.**

Dismissing marks the team closed and deletes nothing. The Orchestrator's own session is never closed. To bring a team
back:

```sh
ah roster reopen <team>
ah roster resume --hold --team <team>
```

The second command is only for a team you paused.

## The Desk

The Desk holds **Q&A** (with the count waiting on you), **Board**, **Feed** and **Graph** as tabs. It opens in one of
two places, and it remembers which one you used last:

- **The side column** (the default), beside Herdr's Files and Changes panel. **⧉** moves the whole Desk to a window on
  the tab shown; **×** closes the column.
- **A window.** The dock button moves it back to the column, the maximize button makes it larger (and restores it),
  and **×** or `esc` closes it.

Older or edited layouts without the side column open the Desk as a window. Settings can replace such a layout (and
keep a backup).

### Q&A

Two tabs:

- **For you:** each question waiting on you, grouped by team, with the recommended answer already picked. Nothing is
  sent until you press **Send**, or pick a **Not needed** reason.
- **History:** the 20 newest entries across your running teams, newest first: questions that were answered, and the
  things you asked the Orchestrator. A row for something you asked shows how many replies it got; click it to open
  its thread. The thread shows your words, then the **Reply** that answered them directly, then the rest of that turn
  and anything said later, each under **Later reply** with its time. `esc` or **← History** goes back.
  Above the list, a search box and **Team**, **Time** (All time, Today, Last 7 days) and **State** (All, Answered,
  Not answered) menus narrow it. ahd does the search, over your questions and their replies only: answered decisions
  are not included. Typing searches after a short pause, `enter` at once, `esc` clears the box, **Clear** resets
  everything, and **Load more** fetches older matches. The choice lasts while the window is open.

### Board

The web page's Board columns across every running team: **Next**, **In progress**, **In review**, **Waiting on you**,
**Stuck** and **Done today**. Filters along the top: **Blocked only**, **Changes only**, an owner menu, and a chip per
running team and lane.

- A card in **Waiting on you** is a Q&A card: answer it right there with **Send** or **Not needed**.
- A work item's card opens its sheet: what the item is, its owner (with **Focus terminal** when exactly one live
  terminal is theirs) and the item's log.

### Feed

What is happening now, across your running teams:

- a **Now** strip at the top: who is working on what, working members first, then blocked ones;
- filter chips: one team at a time or all, one chip per kind of event, and **Internal** (off by default);
- then one stream, newest first and grouped by minute: briefs and reports, checklist moves, alerts, questions waiting
  on you, and member state changes seen while the Feed was open, with "N new since you looked".

A row whose member has a live terminal is a button: click it, or press Enter, to focus that terminal. Nothing in the
Feed sends anything.

### Graph

The web page's Map, one team at a time, with a tab per running team. The team, then its lanes, are drawn as packed
circles: streams holding members, a busy ring per member, message arcs between them (briefs solid, reports dashed) and
a dot per brief in flight. Under each map are its links in words: who messages whom, how much, and what is in flight.

Under the maps is a time scrubber: messages per minute over the run, the **Run**, **60 min**, **30 min** and **10 min**
windows, and a knob to drag back in time. `←` and `→` step a minute; End or **Now** returns to now. Arcs outside the
window fade, and the links count only that window's messages. Click a stream's circle to show that stream alone; the
crumb above it, or `esc`, goes back out. The Graph is read only.

## Status bar and the Needs-you drawer

- **Status bar:** to the right of Herdr's own status, Wrangl adds working and blocked counts across every team, the
  **Needs you** badge (click it to open Q&A) and the newest event: a member's state change or an alert.
- **Needs-you drawer:** a small tab on the right edge of the terminals shows the Needs you count. Hover it to slide out
  the teams that need you; click it to open Q&A. It hides when nothing needs you or when Q&A is showing in the Desk.
  Drag it to another edge if it is in the way.

## Settings and About

Open **Wrangl settings…** from the **…** menu. Settings are kept by Herdr, not in files.

| Section | What you can set |
|---|---|
| Layout | shown only when your layout has no Desk column: **Replace layout (keeps a backup)** |
| Sidebar | **Opens on**: the tab shown each time you switch to the Wrangl layout, Agents or Wrangl |
| Tab strip | show or hide the Desk button |
| Teams and lanes | **Lanes** nested or collapsed (header row only), and which teams the sidebar lists. A hidden team's questions still count in Needs you and Q&A |
| Refresh and alerts | **Refresh**: Live, or at most every 5 s, 15 s or 1 min. **Alarms as herdr notices**. **Auto-update Q&A**: off keeps what you are reading and offers "Updates available"; on replaces the list as new questions arrive |
| Where ah runs | **ah runs on**: Auto, This computer, or a named Herdr machine (and WSL on Windows). Auto is this computer, except on Windows. **Link** shows how the sidebar reaches the daemon |
| Updates | the installed version against the Wrangl plugin's, **What's new**, **Update** or **Reinstall** (each runs `ah herdr install`, which keeps any mod file you edited), **Check now** and **Auto-update** |
| About | the mascot, what Wrangl is, the mod and `ah` versions, and the repository address |

A check for updates runs when the Wrangl layout opens, then hourly.

## If something looks wrong

| The sidebar says | What to do |
|---|---|
| ah is not on Herdr's PATH | Put the folder that holds `ah` (by default `~/.local/bin`) on your login shell's PATH, then restart Herdr. |
| ahd not running | The sidebar retries by itself, waiting a little longer each time, up to 30 seconds. On this computer it never starts the daemon itself: run `ahd start`, or see [The daemon](../docs/daemon.md). |
| ah did not answer | The daemon is slow or busy. It retries by itself. |
| ah is stuck (restart ahd) | Run `ahd restart`. |

For anything else, start with `ah roster doctor` and [Troubleshooting](../docs/troubleshooting.md).

## For contributors

How the mod is built, how to preview it without Herdr, and its time limits are in [DEVELOPING.md](DEVELOPING.md).
