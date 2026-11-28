# Developing the Herdr desktop mod

Notes for people changing the Wrangl mod for the Herdr desktop app. The user guide is [README.md](README.md).

## What is in this folder

- `mods/ah2/`: the mod. `main.js` holds the views, `model.js` holds the data rules (it imports no Herdr code), and
  `mod.json` sets `place: none`, so the mod shows only where a layout puts it.
- `layouts/wrangl.json`: a copy of Herdr's `main` layout. The `agents` block becomes a stack of two blocks, Herdr's
  own `agents` mod and `{ "mod": "ah2", "props": { "view": "sitrep" } }`, under a header the ah2 mod draws
  (`props: { "view": "tabs" }`, `tabs.js`). A tab click is `layout.select`, which Herdr keeps as a click on the
  layout, so the choice survives redraws and restarts. A hidden side column, `ah2-panel`, sits beside Herdr's Files
  and Changes panel and holds the docked Desk views. Nothing else changes.
- `fixture/events.json`: one saved `ah view events` reply, shaped like the real slices. Use it to run the mod
  without a daemon.
- `preview/render.mjs`: draws the mod as HTML from the same `model.js` and the fixture.

## Check and preview

```sh
T=$(mktemp -d /tmp/ahprobe.XXXXXX); mkdir -p "$T/mods"; cp -R mods/ah2 "$T/mods/"
HERDR_APP_HOME="$T" herdr-app check "$T/mods/ah2"; rm -rf "$T"         # the gate: must exit 0
node preview/render.mjs /tmp/ah2.html && open "file:///tmp/ah2.html#light"   # or #dark
```

Preview views: `?view=qa|qa2|history|thread|empty|down` for Q&A, `?view=graph` for the Graph (`graph-30`: a 30 minute
window back in time; `graph-two`, `graph-zoom`: a second stream, zoomed; `graph-flow`, `graph-flow-v`: the team in Flow,
horizontal and vertical; `dock-flow`: Flow docked; `graph-idle`: idle rows with a long task and a 32 character name (ah2-586); `graph-shot`: the team in the 2026-11-17 Herdr-vs-web screenshot, in Flow), `?view=settings` for the settings dialog,
`?view=menu` for the … menu, `?view=dock` for the Graph docked.

To look at the Graph before a ship, capture it:

```sh
node preview/capture.mjs [OUT_DIR]   # default OUT_DIR: a new temp dir; prints each PNG's path
```

It writes 10 PNGs, `<view>-<theme>.png` for `graph-lanes` (the Map with ah2-tm4's lane open, a lane-crossing brief and a
lane report waiting), `graph-lanes-flow` (the same in Flow), `graph-shot-blocked`, `dock` and `dock-flow`, each dark and light,
through headless Chrome (`tests/perf/chrome.mjs`). It pictures the preview's HTML mirror of `graph-view.js`, not Herdr's GPUI
drawing, and needs no Herdr.

- To run the real mod on the fixture, give its layout block the prop
  `"fixture": "<absolute path to fixture/events.json>"`. It reads that one reply and does not poll.
- `herdr-app check` renders only the first frame, which is the "Loading teams…" state. During a check the mod runs no ah and
  reads no file in ah's home (`runnerOf` in `mods/ah2/target-model.js`).
- A symlinked `mods/ah2` passes `check`. Whether the window follows the symlink for hot reload has not been tried.
- `mods/ah2/questions-model.js` is a byte-identical copy of `web/ui/questions-model.js`, kept honest by a test
  (`tests/web/herdr-qa-model.test.mjs`), so the Q&A view reads the questions slice through the web's own code. Edit
  the web file, then copy it here.
- `mods/ah2/flow-model.js` is a byte-identical copy of `web/ui/flow-model.js`, kept honest by a test
  (`tests/web/herdr-graph-model.test.mjs`), so the Graph's Flow lays out and links its cards through the web Flow's own
  code. Edit the web file, then copy it here.
- `mods/ah2/deeplink.js` is a byte-identical copy of `web/ui/deeplink.js`, held by the same test, so the Graph's Open in browser builds the
  web's own Graph address (`#/graph?team=…`). Edit the web file, then copy it here.
- The update check reads the installed version from line 1 of `mods/ah2/.ah2-owned` (written by `ah herdr install`)
  and the plugin's from `ah where`, which reads files only. It runs when the Wrangl layout opens, hourly while it
  shows, and when the settings dialog opens.
- Member state marks (`marks.js`) follow Herdr's agents list: working spins the spinner, running is the done dot in
  the working tone, blocked is a ! on its tint, idle is a ring, done is a dot, gone ×, no signal ?.
- All colours are `theme()` tokens, so every Herdr theme works.

## Third-party files

`spinner.svg` and `x.svg` are copied from herdr-app's `defaults/mods/workspace-chrome/`, which redistributes them
under the Apache License 2.0, copyright the herdr-app authors; the licence text is in
[LICENSES/herdr-app-Apache-2.0.txt](./LICENSES/herdr-app-Apache-2.0.txt). `x.svg`, `ellipsis.svg`, `panel-right.svg`,
`app-window.svg` and `pin.svg` are Lucide icons (`x`, `ellipsis`, `panel-right`, `app-window` and `pin`, the same path data); `x` and
`ellipsis` (Feather's `more-horizontal`) are on Lucide's list of icons derived from Feather. Lucide's ISC licence,
with its Feather MIT section, is in [LICENSES/lucide-ISC.txt](./LICENSES/lucide-ISC.txt).

## What can wait, and for how long

Herdr runs a mod's JavaScript on its UI thread and stops a call that overruns: a render has 50 ms, an event handler
or a task step 500 ms (Herdr's sandbox budgets). So the mod can never hold the thread for good; the rule here is that
it also never waits on `ah` without an end, and that a render stays far inside its budget. Every call the mod makes,
by kind:

| Call | Where it runs | Can it wait? | Bound |
|---|---|---|---|
| `theme`, `props`, `agents`, `terminals`, `layout`, `shared`, `machines`, `window`, `placement`, `appHome` | UI thread, in render | No: reads of state Herdr already holds | none needed |
| `run`, `setShared`, `setAgentOrder`, `openOverlay`, `closeOverlay`, `notify` | UI thread, from a click or a task | No: they return at once (a `setShared` redraws every mod) | none needed |
| The poll: `ah view events --wait 25`, or the same over ahd's loopback page | a task (`main.js` `poll`), never render | Yes | 35 s exec timeout (10 s for the first reply), a deadline 5 s later, and at two given-up calls no third starts (`pollExec`); Herdr's fetch is 30 s plus the same deadline. The view says "ah did not answer" or "ah is stuck (restart ahd)" |
| Finding the loopback page: `ah where`, `ahd ui start` | the poll's task | Yes | 10 s and 30 s |
| An answer: the loopback POST, else `ah msg decision ui-answer` | a task from Send's click | Yes | 30 s plus a 5 s deadline; the card says "No reply after 30 s" |
| A History thread: `GET /ui/v1/history` on the loopback page, else `ah msg history` | a task from the row's click | Yes | 15 s plus a 5 s deadline on either path; the panel says "ah did not answer". A request ahd took and did not answer is not sent again through `ah` |
| An item's log: the poll's route with `wait=0` on the loopback page, else `ah view events --wait 0` | a task from the card's click | Yes | 15 s plus a 5 s deadline on either path; the sheet says "ah did not answer" |
| Dismiss a team: `POST /ui/v1/team-close` on the loopback page (preview, hold, close), else `ah roster close` | the dialog's own task (`dismiss.js`), from the click that opened it | Yes | 15 s for the preview and 25 s for a hold or a close, plus a 5 s deadline. A pause is one short call at a time (ahd waits 10 s per call), sent again until the hold is sealed; one not answered in time is sent again, and three in a row is an error with Force quit and Leave it. A close ahd took and did not answer is not sent again: the dialog reads the team every 5 s, 18 times, then says ahd is still closing. Through `ah` a close has 100 s |
| The update check: `ah where`, two file reads, `ah herdr install` | a task on mount, hourly, or from Settings | Yes | 10 s and 60 s for the commands; a check older than 3 minutes no longer holds the next one or the Settings button |
| `readTextFile` (the install marker, the changelog, a fixture) | inside those tasks | Yes, on a remote machine | Herdr gives it no timeout: only the 3-minute rule above covers it |
| `cx.sleep`, `cx.timer.every` (30 s for ages, hourly for the update check) | Herdr's timers | No | none needed |

What a render computes, on the fixture and on the same data grown to 160 teams of 500 items a stream (Node; Herdr's
QuickJS has no JIT and is slower, which is why the two heavy ones are kept):

| Render work | Fixture | Grown | Kept or capped |
|---|---|---|---|
| Sidebar, status bar, peek, sitrep models | under 0.05 ms | under 0.05 ms | regions show 10 rows, then `+ N more` |
| Feed model | 0.1 ms | 0.6 ms | 60 rows, 6 members in the Now strip |
| Board model | 0.5 ms | 22 ms | kept until the data, a filter or the half minute changes; every column draws at most 50 cards (Done today 20), then `+ N more` |
| Graph model and layout | | | kept until an input changes (`graphOf`); 600 paths; the packing loop has a step limit |

Two things the mod cannot bound by itself: an exec in flight when a view closes runs to its own timeout (Herdr has
no way to end one), and a first or resync reply is as large as ahd makes it, parsed in one task step.

The Graph's layout is the web's d3 pack and curveBundle, ported to `graph-layout.js` because Herdr cannot load
`d3.min.js`; a test holds the port to the vendored d3.
