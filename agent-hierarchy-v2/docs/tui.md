# The terminal view

This page is for anyone who wants to watch their teams from a terminal. After reading it you will be able to open the
terminal view, move around it with the keys or the mouse, and read what each screen shows.

`ahd tui` shows your teams in a terminal, with no browser. It is a good fit for a second pane beside Claude Code or
over SSH. It only reads: to answer a question, run an action or see the other views, use the [web page](web-ui.md).

## Open it

```
ahd tui
```

It never starts the daemon. If the daemon is down it says `ahd is not running` and tries again (every two seconds at
first, then less often).

| Flag | Does |
|---|---|
| `--team <name>` | open on that team; an unknown name is an error, exit 2 |
| `--once` | print one plain frame and exit: the teams, or the team with `--team`. Exit 3 if the daemon is down |
| `--help` | usage |

With `TERM=dumb`, or when input or output is not a terminal, it behaves as `--once`. In a narrow window the rightmost
columns drop first.

Roles, states, counts and errors are drawn in the web UI's colours, at the depth the terminal says it has, read once at
start: 24-bit when `COLORTERM` is `truecolor` or `24bit`, the 256-colour palette when `TERM` contains `256color`, and
the basic 16 otherwise, each the nearest to the web colour. With `NO_COLOR` set and non-empty there is no colour; bold,
dim and inverse stay. A selected row is drawn without colour so it stays readable.

The first frames show the mascot above the screen, until the first key or mouse event; a terminal under 24 rows, or
narrower than the art, skips it.

From the Claude Code band, **View → TUI** opens `ahd tui` in a pane beside your session (needs herdr; otherwise it
tells you to run `ahd tui` yourself).

## What you see

- **Teams**, the first screen: one row per team with its state, what needs you, what is working or blocked, items
  done and when it last changed. A lane sits indented under its parent. Ended teams stay hidden until you press `e`.
- **A team**: its agents, then its Sitrep. The Orchestrator's row reads working while its turn is open. The Sitrep stacks Completed, In progress and Up next, each with a heading
  and a count. Waiting on you, Blocked, Report in, Stale and Up next, old follow when they have rows, and the last
  three start folded. A lane's Sitrep is on its top-level team.
- **An agent**: its status, then the work items it holds.
- **A work item**: a popup in the middle of the screen with its state, holder, note and log. It opens the same way
  from the Sitrep and from an agent.

## Keys

| Key | Does |
|---|---|
| arrows, `j`/`k` | move; in a work item, scroll |
| `Home`, `End` | first, last |
| `PgUp`, `PgDn` | scroll a page |
| `Enter` | open; on a Sitrep heading, fold or unfold it; in a work item, close it |
| `Esc` | back one screen, or close a work item |
| `e` | show or hide ended teams, on Teams |
| `r` | read everything again |
| `?` | keys; any key closes them |
| `q`, Ctrl-C | quit |

## Mouse

Click a row to open it, a Sitrep heading to fold it, or a crumb on the top line to go to that level. The wheel scrolls.
Every click has a key that does the same. A right click does nothing.

While the mouse is on, your terminal's own text selection needs Shift. `AH_TUI_MOUSE=off` turns the mouse off. Under
tmux, turn mouse support on (`set -g mouse on`).
