# UI performance check (`ui-e4.mjs`)

Measures the web page's cost in a headless Chrome against the mock server
(`web/mock/serve.mjs`). It never touches your daemon or your browser profile. It
needs Node and Chrome. `tests/perf/chrome.mjs` picks the binary: `AH_CHROME`, then `CHROME_BIN`, then
`chrome-headless-shell` or Chrome for Testing from the puppeteer or Playwright cache, and the macOS Chrome.app last
(with a warning: while it runs you cannot open your own Chrome).

It reports, on the Graph view:

- page CPU and main-thread busy time over an idle window;
- the frame rate while the graph's motion runs;
- the frame time of a fast transcript scroll;
- the bytes of the long-poll replies, and for the realistic fixture the longest
  request line, the first-load bytes and the plan record's size.

## Run it

```
node tests/perf/ui-e4.mjs --json /tmp/mine.json
```

By default it runs **both fixtures**, one after the other. Defaults: three runs
(`--runs 3`, the median is kept), a 30 s dwell (`--dwell`), a 60 s idle window
(`--idle`), and each run both with and without diagnostics
(`--diagnostics on|off|both`). Other flags: `--label NAME`, `--turns N`,
`--teams-idle SECONDS`, `--web DIR` (measure another checkout's `web/`), `--json FILE`.

An unknown flag exits with status 2 and lists the valid ones. **`--members` is
gone**: it exits 2. The member count comes from the fixture now.

- `--dwell SEC` is the time from the landing view to the start of the Graph idle
  window. The mock keeps publishing, so reply sizes grow with time; every tree is
  measured after the same dwell. Compare runs only when their dwell matches.

## The two fixtures

`--fixture small|realistic|both` picks them. With no flag, the `AH_MOCK_FIXTURE`
environment variable picks one; with neither, both run. The sizes live in one
table, `web/mock/presets.mjs`; the harness keeps no counts of its own.

| Fixture | Members | Open exchanges | Answered exchanges | Message excerpts |
|---|---|---|---|---|
| `small` | 10 | 8 | 33 | the mock's default |
| `realistic` | 50 | 40 | 1,500 | 4 KB, the daemon's cap |

`small` is one modest team and is what the checked-in baseline was recorded at.
`realistic` stands in for a busy home: one team of 50 members. Every result
carries its fixture name and counts, for example `fixture=realistic(50/40/1500)`.

```
node tests/perf/ui-e4.mjs --fixture realistic --json /tmp/real.json
```

## What is gated

**`small`:** page CPU and main-thread busy time, motion frame rate, scroll frame
time and idle long-poll bytes, against the baseline. See the next section.

**`realistic`:**

| Number | Gate |
|---|---|
| Longest request line | at most 16 KB |
| Plan record size | at most 10 KB for the fixture's few streams, only when it holds plan streams. A stream costs about 350 B plus its goal, so this is a per-stream check: teams with many streams exceed 10 KB, as they already did |
| First-load bytes | at most 1.10 times the recorded baseline |
| Idle long-poll bytes | at most 1.15 times the recorded baseline |
| Idle page CPU | reported only, never fails |

The request-line and plan-record limits need no baseline, so a plain run already
judges them: the script exits 1 and prints a `REGRESSION` line when one is over.
The other two rows compare with the recorded baseline.

- **"no baseline"**: until a realistic baseline is recorded, the first-load and
  idle-bytes rows print as informational (`no baseline`) and are not gated.
- If the recorded baseline's own idle bytes varied by more than 10% between its
  runs, the idle-bytes row is informational too, because the number is too noisy
  to hold anyone to.

## Compare two runs

```
node tests/perf/ui-e4.mjs --compare tests/perf/baseline-9a71e90c.json /tmp/mine.json
```

Exit status 0 means no regression, 1 means one. For `small`, a number fails only
when it is **more than 10% worse than the baseline and also worse by more than an
absolute floor**: 0.2 percentage points for CPU and busy time, 1 KB for bytes, 1 ms
for times, 1 for frames per second. The floor stops a 0.10% to 0.14% CPU change
from failing on percentage alone. A mode missing from the new run, or a run whose
dwell differs from the baseline's, also fails. The realistic rows in the table
above are added when the new file has a `realistic` run.

## Record the baseline

Run both fixtures (three runs each, the default) into a file, then record it as the
baseline file:

```
node tests/perf/ui-e4.mjs --fixture both --json /tmp/both.json
node tests/perf/ui-e4.mjs --record-baseline tests/perf/baseline-9a71e90c.json /tmp/both.json --sha <commit>
```

This replaces the whole baseline file: the Chrome binary, the `small` medians and
runs, and a `realistic` entry (medians, spreads, the commit and the date).
`--compare` fails when the run used a different Chrome path from the baseline, and a
new Chrome or Playwright build counts as a different binary, so re-record after one.
Commit the changed file. Do this when the numbers move
on purpose, not to make a failing run pass.

Two self-checks run without Chrome: `--check-floor` and `--check-dwell`.

## Looks, views and the glass budgets

These flags pick what is measured. Each is written to the result JSON.

- `--look house|glass` puts `{"skin": …}` into `ah-optics` before the first navigation. The default is `glass`.
- `--optics '<json>'` puts a whole `ah-optics` object there. `--look`, when given, still sets its `skin`.
- `--theme light|dark` sets `ah-theme`.
- `--dpr N` sets the device scale factor. The default is 1.
- `--view map|radial|board|overview` picks the view for the idle and motion windows. The default is `map`, the Graph
  view's default mode. The run lands on that view on every path and fails if the page is not on it before the idle
  window or after the motion window.

Each window now also reports `gpu_cpu_pct`. That is the CPU of the GPU process, which stands in for GPU cost under
headless software compositing. `idle.dom_nodes` is `document.getElementsByTagName('*').length` on the view.
The `dom_nodes` values in `baseline-*.json` predate this. They are the CDP `Nodes` metric (every node type in the
renderer), so do not compare them with the new count.

The glass budgets (`ah-optical-glass.md` §8) compare a house run with a glass run from the same run set. Run them back to
back, then:

```
node tests/perf/ui-e4.mjs --fixture realistic --dpr 2 --view map --look house --json /tmp/house.json
node tests/perf/ui-e4.mjs --fixture realistic --dpr 2 --view map --look glass --json /tmp/glass.json
node tests/perf/ui-e4.mjs --compare-looks /tmp/house.json /tmp/glass.json
```

The rows are as follows:
- Idle `gpu_cpu_pct` may be at most house + 1.0 point, at DPR 2.
- On map, radial and board, motion `gpu_cpu_pct` may be at most house + 2.0, and motion fps, p95 and frames over 20 ms
  are judged by the S8 rule against house.
- On map and radial, idle `page_cpu_pct` may be at most house × 1.10 + 0.2.
- Scroll p95 and p99 are judged by the S8 rule.
- `dom_nodes` is reported, not gated.

## Readable floor (`optics-floor.mjs`)

`node tests/perf/optics-floor.mjs` runs the glass halo sweep (§5.4, §9.3) and writes `web/ui/optics-floor.json`. It covers
every glass mode, swatch and grid cell, on the realistic Overview and Board List. The page must expose
`globalThis.ahOptics` (`web/ui/optics-boot.js`).

`--check` fails when the committed table is more than 0.1 off a fresh run. `--modes`, `--swatches`, `--cells`, `--all`
and `--out` narrow a run, for probes.

## Fixed-viewport check (`ui-scroll.mjs`)

`node tests/perf/ui-scroll.mjs [--shots DIR]` serves the mock and opens every view in headless Chrome at 1440×900, 1280×800
and 1024×700. A view fails when the page itself scrolls, or when a visible scroll region sits inside another one. With
`--shots`, it also saves each view in dark and light at each size. It starts and stops its own mock server and its own
Chrome on a fresh profile, and exits 1 on any failure.
