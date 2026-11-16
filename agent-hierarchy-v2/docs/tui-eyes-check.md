# Native `ahd-tui` — eyes check

One page, about ten minutes, run by a person. The automated rows prove bytes and exit codes on a pseudo-terminal; they cannot
prove that a real terminal *looks* right. Nothing below has been checked visually by an agent.

The client only reads; running it against your normal daemon changes nothing.

```
AHD_TUI=<install dir>/ahd-tui        # the binary beside `ahd`
```

Repeat the numbered checks in each terminal: **Terminal.app**, **iTerm2**, and a **herdr** pane. Mark each cell ✓ / ✗ and
write what you saw for a ✗.

| # | Do | Look for | T.app | iTerm2 | herdr |
|---|----|----------|:-----:|:------:|:-----:|
| 1 | Type `echo before`, then run `$AHD_TUI` | Screen switches to a fresh full-screen view (title `ah › teams` on the first line, footer with keys on the last); the cursor is not blinking in the body | | | |
| 2 | Press `q` | Original screen returns with `echo before` still above the prompt; cursor visible; typing `echo after` echoes normally | | | |
| 3 | Run it again, press `Ctrl-C` | Same restore as 2 (no stray `^C`, no mouse garbage when you move the mouse afterwards) | | | |
| 4 | Run it; from another tab `kill -TERM <pid>` (and again with `-HUP`) | The terminal is restored exactly as in 2; shell prompt works | | | |
| 5 | Move the mouse and click around after a quit | No `[<0;12;5M` text appears in the shell | | | |
| 6 | Run it; `↑` `↓` `j` `k` | The `▸` marker moves one row per press and stops at the first and last row | | | |
| 7 | `Enter` on a row, then `Esc` | Opens the row's screen, the crumbs on line 1 extend, and `Esc` returns to the parent | | | |
| 8 | Click a row; click a crumb on line 1 | A row click opens that row; a crumb click goes to that level | | | |
| 9 | Click a Sitrep heading | The region folds and its mark changes from `[-]` to `[+]` | | | |
| 10 | Open a work item | A centered popup draws over the screen with no stray characters; `Esc` leaves the screen as it was | | | |
| 11 | Mouse wheel | The screen scrolls | | | |
| 12 | Drag the window narrow, then back | The rightmost columns drop; back at full size they return with no leftover characters | | | |
| 13 | Drag the window taller and shorter | The list refits; no line wraps onto the next row | | | |
| 14 | `?` then any key | The keys page opens and closes; `q` on it quits | | | |
| 15 | `NO_COLOR=1 $AHD_TUI` | No colour at all; the selection is still visible | | | |
| 16 | Stop the daemon (`ahd stop`), watch the screen | `ahd is not running`, not a crash; start the daemon again and the screen comes back without restarting the client | | | |
| 17 | `$AHD_TUI --once` | Plain text, no escape sequences, exit 0 (`echo $?`) | | | |

## What a ✗ most likely means

- **1/2/3 wrong** — the alternate-screen or cursor sequences are not honoured by that terminal; note the terminal and version.
- **5 shows `[<0;…M`** — mouse reporting was left on after exit; the leave sequence was lost or the process was killed hard.
- **8/9/11 do nothing** — that terminal is not forwarding SGR mouse reports (`?1000`, `?1006`).
- **10/12 leave stray characters** — the redraw did not clear what the last frame drew.

Report the cells (terminal + number + what you saw) back to the Orchestrator; do not fix by editing the client.
