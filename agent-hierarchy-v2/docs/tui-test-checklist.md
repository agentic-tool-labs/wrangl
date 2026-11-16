# Terminal view: manual test checklist

Tick these off with `ahd tui` in front of you. Each line is an action and what
you should see. What the screens are meant to do is in the [TUI page](tui.md);
the texts quoted here are the ones the program shows.

Write down anything that differs, with the text on screen, what you did and
what your terminal and multiplexer were.

## 0. Before you start

- [ ] `ahd status` shows the daemon up and the plugin's version (`ahd start` first if not).
- [ ] You have a real team to look at, with at least three agents and some work items. In tmux, `set -g mouse on` is set.

## 1. Opening and leaving

- [ ] `ahd tui` draws the teams within a second or two. The top line starts `ah`; the bottom line shows keys.
- [ ] `q` leaves. The shell prompt is back, the cursor is visible, the mouse no longer reports clicks, colours are normal.
- [ ] Open again and leave with `Ctrl-C`. Same result. `Esc` on Teams does **not** leave.
- [ ] Open again, then from another terminal `kill -TERM <pid of ahd-tui>`. Your terminal is restored.
- [ ] `ahd tui --once | cat` prints one plain frame with no colour and no escape sequences, and exits.
- [ ] `ahd tui --once --team <name>` prints that team. `ahd tui --once --team nosuch` prints a message and exits 2.
- [ ] `ahd tui --path /x` is a usage error, exit 2.
- [ ] `NO_COLOR=1 ahd tui` has no colour and the same layout.

## 2. Screens by keys

- [ ] `↓` `↑` `j` `k` move the selection; `PgDn` `PgUp` scroll a page; `Home` and `End` go to the first and last row.
- [ ] `Enter` on a team opens it; `Esc` goes back and the selection is where you left it.
- [ ] `Enter` on an agent opens its work items; `Esc` goes back.
- [ ] `Enter` on a Sitrep heading folds it, and again unfolds it. An empty region shows its name and `0`.
- [ ] `Enter` on a work item, from the Sitrep or from an agent, opens the popup with its state, holder and log. The
      arrows scroll it; `Esc` or `Enter` closes it.
- [ ] `e` on Teams shows ended teams, and again hides them.
- [ ] `r` reads again; `?` shows the keys and any key closes them; the page matches this list.
- [ ] Nothing else does anything: `x`, `i`, `m`, `y`, `Alt`+letters, function keys and `Shift`+arrows are ignored.

## 3. Mouse

- [ ] Click a team: it opens. Click an agent: it opens. Click a work item: the popup opens.
- [ ] Click a Sitrep heading: it folds. Click it again: it unfolds.
- [ ] Click a crumb on the top line: that level shows.
- [ ] Click outside the popup: it closes.
- [ ] The wheel scrolls.
- [ ] Right click, middle click and dragging do nothing. A click past the last row changes nothing.
- [ ] Hold Shift and drag: your terminal selects text.

## 4. It follows what happens

- [ ] Start a new agent in the team: its row appears without moving your selection.
- [ ] Open an agent, then dismiss it from the Orchestrator: the view goes up a level.
- [ ] Stop the daemon (`ahd stop`): `ahd is not running`, and no crash. `ahd start`: the screens come back without a
      restart.

## 5. Afterwards

- [ ] `pgrep -fl ahd-tui` finds nothing left over.
- [ ] Your terminal still looks normal after the last exit (cursor, echo, mouse).
