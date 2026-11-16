#!/usr/bin/env node
// The web UI performance measurement (the E4 gate): loads the UI from the mock server in a headless Chrome and reports
//   a. page CPU and main-thread busy time over an idle window on the Graph view,
//   b. the frame rate while the graph's motion runs,
//   c. the frame time of a fast transcript scroll,
//   d. the bytes of the long-poll replies.
// A run holds the machine-wide e4 slot (one at a time; a second run waits its turn). --no-slot skips it for a local one-off; the numbers are then not gate-grade.
// Usage: node tests/perf/ui-e4.mjs [--no-slot] [--fixture small|realistic|both] [--label NAME] [--idle SECONDS] [--turns N] [--diagnostics on|off|both] [--runs N] [--web DIR] [--teams-idle SECONDS] [--dwell SECONDS] [--json FILE]
// The fixture is a named preset of the mock (web/mock/presets.mjs); with no --fixture, AH_MOCK_FIXTURE selects one, else both run.
// The small one gates CPU and idle bytes against the baseline; the realistic one gates the request line, first-load bytes and
// idle bytes, and reports CPU only. Every result carries its preset and counts. tests/perf/chrome.mjs picks
// the Chrome binary; no browser package is needed, the script speaks the DevTools protocol itself.
// Compare two runs: node tests/perf/ui-e4.mjs --compare BASE.json NEW.json   (exit 1 when a median is more than 10% worse and also past its absolute floor (CPU/busy % 0.2, bytes 1 KB, ms and fps 1); a mode missing from NEW fails)
// Record the whole baseline (browser, small and realistic entries) from a --fixture both run: node tests/perf/ui-e4.mjs --record-baseline BASE.json RUN.json --sha SHA
import { spawn, spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { constants, tmpdir } from "node:os";
import { basename, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { absoluteRows, baselineEntry, fixtureNames, judge, labelOf, lookRows, realisticRows, rowLine, VIEWS } from "./e4-gates.mjs";
import { alive, chromeBinary, devtools, launchChrome, stopChrome } from "./chrome.mjs";

const args = process.argv.slice(2);
const EPS_PCT = 0.2; // CPU % and main-thread busy %
const EPS_MS = 1;
const EPS_BYTES = 1024;
const EPS_FPS = 1;
const flag = (name, fallback) => (args.includes(`--${name}`) ? args[args.indexOf(`--${name}`) + 1] : fallback);

const VALID_FLAGS = ["fixture", "label", "idle", "turns", "diagnostics", "runs", "web", "teams-idle", "dwell", "json", "sha", "child", "compare", "record-baseline", "check-dwell", "check-floor", "check-slot", "no-slot", "look", "optics", "theme", "dpr", "view", "compare-looks"];
const unknownFlag = args.find((a) => a.startsWith("--") && !VALID_FLAGS.includes(a.slice(2).split("=")[0]));
if (unknownFlag) {
  console.error(`unknown flag ${unknownFlag}; valid: ${VALID_FLAGS.map((f) => `--${f}`).join(", ")}`);
  process.exit(2);
}

// Time from the landing view to the start of the Graph idle window. The mock keeps publishing,
// so slices (and long-poll bodies) grow with it: every tree is measured after the same dwell.
function padMs(dwellS, elapsedMs) {
  return Math.max(0, dwellS * 1000 - elapsedMs);
}

if (args.includes("--check-dwell")) {
  for (const e of [0, 3000, 29000, 30000, 33000]) {
    if (e + padMs(30, e) !== Math.max(30000, e)) throw new Error(`dwell pad wrong at ${e} ms`);
  }
  console.log("dwell ok: every elapsed time is padded up to the dwell, never past it");
  process.exit(0);
}

if (args.includes("--check-floor")) {
  const cases = [
    [0.1, 0.14, EPS_PCT, true, "floor"],
    [5, 7, EPS_PCT, false, "fail"],
    [5, 5.4, EPS_PCT, true, "relative"],
    [0.1, 0.4, EPS_PCT, false, "fail"],
    [60, 59, EPS_FPS, true, "relative"],
    [60, 50, EPS_FPS, false, "fail"],
  ];
  for (const [b, n, eps, ok, rule] of cases) {
    const got = judge(b, n, eps, eps === EPS_FPS);
    if (got.ok !== ok || got.rule !== rule) throw new Error(`floor wrong for ${b} -> ${n}: ${JSON.stringify(got)}`);
  }
  console.log("floor ok: +40% on a 0.10% baseline passes by the floor, +40% on a 5% baseline fails");
  process.exit(0);
}

if (args.includes("--compare")) {
  process.exit(compare(args[args.indexOf("--compare") + 1], args[args.indexOf("--compare") + 2]));
}

// The glass budgets (§8 of ah-optical-glass): a --look house run and a --look glass run of one run set, entry by entry.
if (args.includes("--compare-looks")) {
  const at = args.indexOf("--compare-looks");
  const [house, glass] = [args[at + 1], args[at + 2]].map((f) => JSON.parse(readFileSync(f, "utf8")));
  const key = (m) => [m.fixture?.preset, m.diagnostics, m.view, m.theme, m.dpr].join("|");
  let failed = false;
  for (const g of glass.median) {
    const h = house.median.find((m) => key(m) === key(g));
    if (!h || h.look !== "house" || g.look !== "glass") {
      console.log(`REGRESSION  ${key(g)}: no house entry to compare with, or the files are not house then glass`);
      failed = true;
      continue;
    }
    for (const r of lookRows(h, g)) {
      failed ||= !r.ok;
      console.log(rowLine(r));
    }
  }
  process.exit(failed ? 1 : 0);
}

if (args.includes("--record-baseline")) {
  const [basePath, runPath] = [args[args.indexOf("--record-baseline") + 1], args[args.indexOf("--record-baseline") + 2]];
  const run = JSON.parse(readFileSync(runPath, "utf8"));
  // The whole baseline compare() reads, from one run on one binary: the browser, the small entries and the realistic entry.
  const small = (list) => list.filter((m) => (m.fixture?.preset ?? "small") === "small");
  const real = run.median.filter((m) => m.fixture?.preset === "realistic");
  const bothModes = (list) => ["off", "on"].every((d) => list.some((m) => m.diagnostics === d));
  if (!bothModes(small(run.median)) || !bothModes(real)) {
    console.error(`${runPath} needs both fixtures in both diagnostics modes; record from a --fixture both --diagnostics both run`);
    process.exit(2);
  }
  if (!run.chrome) {
    console.error(`${runPath} does not say which Chrome it ran on; record from a run of this version of ui-e4.mjs`);
    process.exit(2);
  }
  const base = {
    label: run.label, at: run.at, chrome: run.chrome, runs_per_mode: run.runs_per_mode, runs: small(run.runs), median: small(run.median),
    realistic: baselineEntry(run, flag("sha", "unknown"), new Date().toISOString()),
  };
  writeFileSync(basePath, JSON.stringify(base, null, 2) + "\n");
  console.log(`recorded the baseline in ${basePath} on ${base.chrome}: idle bytes spread ${JSON.stringify(base.realistic.idle_bytes_spread_pct)}, idle bytes ${base.realistic.idle_bytes_gated ? "gated" : "informational (spread over the limit)"}`);
  process.exit(0);
}

const LABEL = flag("label", "run");
const IDLE_S = Number(flag("idle", 60));
const TURNS = Number(flag("turns", 400));
const DIAG = flag("diagnostics", "both");
const RUNS = Number(flag("runs", 3));
const MOTION_S = 15;
const DWELL_S = Number(flag("dwell", 30));
const TEAMS_IDLE_S = Number(flag("teams-idle", Math.max(0, DWELL_S - 5)));
// The web tree under test: a checkout's web/ directory (the mock and the UI it serves).
const WEB = resolve(flag("web", join(import.meta.dirname, "..", "..", "web")));
const MEMBER_ID = "member:a1b2c3d4/ship/implementor-1";
const CHILD = args.includes("--child");
const { PRESETS } = await import(pathToFileURL(join(WEB, "mock", "presets.mjs")).href);
// A bad argument is refused before any wait for the slot.
let NAMES;
try {
  NAMES = fixtureNames(args.find((a) => a.startsWith("--fixture="))?.slice("--fixture=".length) ?? flag("fixture", undefined), process.env.AH_MOCK_FIXTURE, PRESETS);
} catch (e) {
  console.error(e.message);
  process.exit(2);
}
// The look under test (ah-optical-glass §8): `ah-optics` and `ah-theme` are seeded before the first navigation; --dpr sets the
// device scale factor; --view picks the view the idle and motion windows run on (map is the Graph view's default mode).
let OPTICS;
try {
  OPTICS = JSON.parse(flag("optics", "{}"));
  if (!OPTICS || typeof OPTICS !== "object" || Array.isArray(OPTICS)) throw new Error("not an object");
} catch (e) {
  console.error(`--optics needs a JSON object: ${e.message}`);
  process.exit(2);
}
const LOOK = args.includes("--look") ? flag("look") : (OPTICS.skin ?? "glass");
const THEME = flag("theme", null);
const DPR = Number(flag("dpr", 1));
const VIEW = flag("view", "map");
const badOption = !["house", "glass"].includes(LOOK) ? `--look ${LOOK}: valid house, glass`
  : THEME !== null && !["light", "dark"].includes(THEME) ? `--theme ${THEME}: valid light, dark`
  : !(DPR > 0) ? `--dpr ${flag("dpr")}: a positive number`
  : !VIEWS[VIEW] ? `--view ${VIEW}: valid ${Object.keys(VIEWS).join(", ")}` : null;
if (badOption) {
  console.error(badOption);
  process.exit(2);
}
const SEED = { "ah-optics": JSON.stringify({ ...OPTICS, skin: LOOK }), ...(THEME ? { "ah-theme": THEME } : null), ...VIEWS[VIEW].seed };

// Two E4 runs on one machine distort each other, so a run holds the machine-wide e4 slot (tests/with-slot.sh, pool e4, one slot)
// for its whole life. The parent re-runs itself under the slot; the re-run finds the held marker and goes on. A --child run is
// started by a parent that already holds it.
const CHILD_RUN = args.includes("--child");
const SLOT_HELD = process.env.AH_SLOT_HELD_E4 === "1";
if (!CHILD_RUN && !SLOT_HELD && !args.includes("--no-slot")) {
  const fixture = args.find((a) => a.startsWith("--fixture="))?.slice("--fixture=".length) ?? flag("fixture", process.env.AH_MOCK_FIXTURE ?? "both");
  const slot = spawn("bash", [join(import.meta.dirname, "..", "with-slot.sh"), "--eta", "large", process.execPath, import.meta.filename, ...args], {
    stdio: "inherit",
    env: {
      ...process.env,
      AH_SLOT_POOL: "e4",
      AH_SLOT_POOL_SLOTS: "1",
      AH_SLOT_LABEL: `ui-e4 ${fixture} ${basename(process.cwd())}`,
      AH_TEST_SLOT_WAIT: process.env.AH_TEST_SLOT_WAIT ?? "1800",
    },
  });
  for (const sig of ["SIGINT", "SIGTERM", "SIGHUP"]) {
    process.on(sig, () => slot.kill(sig));
  }
  slot.on("exit", (code, signal) => process.exit(code ?? (signal ? 130 : 1)));
  await new Promise(() => {});
}
if (!CHILD_RUN && !SLOT_HELD) {
  console.error("--no-slot: no e4 slot is held, so another run may share the machine; these numbers are NOT gate-grade");
}

// A self-check of the slot logic: prints what this process holds, optionally stays for SECONDS so a test can look at the records.
if (args.includes("--check-slot")) {
  const hold = Number(args[args.indexOf("--check-slot") + 1]);
  console.log(`slot ok: ${CHILD_RUN ? "child" : SLOT_HELD ? `held ${process.env.AH_TEST_SLOT_N}` : "none"}`);
  if (hold > 0) {
    await new Promise((ok) => setTimeout(ok, hold * 1000));
  }
  process.exit(0);
}

// The counts come from the preset table alone: a stray override in the environment would make the label untrue.
for (const key of ["AH_MOCK_MEMBERS", "AH_MOCK_OPEN", "AH_MOCK_DONE", "AH_MOCK_EXCERPT_BYTES"]) {
  delete process.env[key];
}
// The preset being measured in this process; the fixture reads AH_MOCK_FIXTURE when the mock is imported.
let CURRENT = null;
const POLL = /\/ui\/v1\/(view\?.*path=%2Fevents|events)|path=\/events|\/events\?/;

const sleep = (ms) => new Promise((ok) => setTimeout(ok, ms));
const tmp = mkdtempSync(join(tmpdir(), "ah-ui-e4."));
let chrome = null;
let mock = null;
let ticker = null;

async function cleanup() {
  clearInterval(ticker);
  await stopChrome(chrome);
  await mock?.close();
  // Chrome's helper processes can still be writing the profile just after the main process goes, which fails a single pass
  // with ENOTEMPTY; the retries give them the moment they need.
  rmSync(tmp, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
}
for (const sig of ["SIGINT", "SIGTERM", "SIGHUP"]) {
  process.on(sig, () => cleanup().then(() => process.exit(128 + constants.signals[sig])));
}
// Whatever ended the process (an uncaught throw included), Chrome does not outlive it.
process.on("exit", () => {
  if (alive(chrome)) {
    chrome.kill("SIGKILL");
  }
  rmSync(tmp, { recursive: true, force: true });
});

async function launch() {
  chrome = launchChrome(join(tmp, "profile"), ["--disable-extensions", "--window-size=1440,900"], { stdio: ["ignore", "ignore", "pipe"] });
  return devtools(chrome);
}

const stats = (values) => {
  const v = [...values].sort((a, b) => a - b);
  const at = (q) => v[Math.min(v.length - 1, Math.floor(q * v.length))] ?? 0;
  return { n: v.length, p50: at(0.5), p95: at(0.95), p99: at(0.99), max: v.at(-1) ?? 0 };
};
function round(n, d = 2) {
  return Math.round(n * 10 ** d) / 10 ** d;
}

async function measureOne(diagnostics) {
  const { createMockServer } = await import(pathToFileURL(join(WEB, "mock", "serve.mjs")).href);
  const { setTurnCount } = await import(pathToFileURL(join(WEB, "mock", "transcripts.mjs")).href);
  setTurnCount("implementor-1", TURNS);
  mock = createMockServer();
  mock.model.setReadModel(diagnostics);
  await mock.listen(0);
  ticker = setInterval(() => { mock.model.tick(); mock.model.traffic(); }, 4000);

  const { browser, page } = await launch();
  const evalIn = async (expression) => {
    const r = await page.send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
    if (r.exceptionDetails) {
      throw new Error(r.exceptionDetails.exception?.description ?? "evaluate failed");
    }
    return r.result.value;
  };

  // Network: the long-poll replies and everything else, bucketed by the phase that was running when they finished.
  let phase = "load";
  const requests = new Map();
  const polls = [];
  let loadBytes = 0;
  let lastSub = [];
  let requestLineMax = 0;
  page.on(async (m) => {
    if (m.method === "Network.requestWillBeSent") {
      requests.set(m.params.requestId, m.params.request.url);
      if (POLL.test(m.params.request.url)) {
        const u = new URL(m.params.request.url);
        requestLineMax = Math.max(requestLineMax, Buffer.byteLength(`${m.params.request.method} ${u.pathname}${u.search} HTTP/1.1`));
      }
      const sub = /[?&]sub=([^&]*)/.exec(m.params.request.url);
      if (sub && /events/.test(m.params.request.url)) {
        lastSub = decodeURIComponent(sub[1]).split(",").filter(Boolean);
      }
    } else if (m.method === "Network.loadingFinished") {
      const url = requests.get(m.params.requestId) ?? "";
      const here = phase;
      if (POLL.test(url)) {
        try {
          const body = await page.send("Network.getResponseBody", { requestId: m.params.requestId });
          const doc = JSON.parse(body.body);
          polls.push({ phase: here, wire: m.params.encodedDataLength, body: Buffer.byteLength(body.body), slices: (doc.slices ?? []).length });
        } catch (e) {
          // a reply whose body is gone (the page reloaded the poll) is not counted
          if (process.env.DEBUG_E4) console.error("poll body lost", here, url, String(e));
        }
      }
      if (here === "load" && !/events/.test(url)) {
        loadBytes += m.params.encodedDataLength;
      }
    }
  });
  await page.send("Network.enable");
  await page.send("Performance.enable");
  await page.send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: DPR, mobile: false });
  await page.send("Page.enable");
  await page.send("Page.addScriptToEvaluateOnNewDocument", { source: `try { for (const [k, v] of Object.entries(${JSON.stringify(SEED)})) localStorage.setItem(k, v); } catch {}` });
  await page.send("Page.navigate", { url: mock.url() });

  const ready = async (test, what) => {
    for (let i = 0; i < 100; i += 1) {
      if (await evalIn(test)) return;
      await sleep(300);
    }
    const seen = await evalIn(`location.hash + " | " + document.body.innerText.slice(0, 300).replace(/\\s+/g, " ") + " | scrollers " + [...document.querySelectorAll(".tr-scroll")].map((e) => e.scrollHeight + "x" + e.clientHeight + " rows " + e.querySelectorAll("li,[role=listitem]").length).join()`);
    throw new Error(`timed out waiting for ${what}; page: ${seen}`);
  };
  await ready(`(!!document.querySelector(".teams") || document.querySelectorAll("svg").length > 0) && !/loading/i.test(document.body.innerText)`, "the landing view");
  const landedOnTeams = await evalIn(`!!document.querySelector(".teams")`);
  const landedAt = Date.now();
  if (!landedOnTeams) await sleep(padMs(DWELL_S, Date.now() - landedAt));

  const metrics = async () => Object.fromEntries((await page.send("Performance.getMetrics")).metrics.map((x) => [x.name, x.value]));
  // CPU seconds of the renderer and of the GPU process (the GPU proxy under headless software compositing).
  const cpuByType = async () => {
    const { processInfo } = await browser.send("SystemInfo.getProcessInfo");
    const sum = (type) => processInfo.filter((p) => p.type === type).reduce((n, p) => n + p.cpuTime, 0);
    return { renderer: sum("renderer"), gpu: sum("GPU") };
  };
  const pctOf = (c0, c1, wall) => ({ page_cpu_pct: round(((c1.renderer - c0.renderer) / wall) * 100), gpu_cpu_pct: round(((c1.gpu - c0.gpu) / wall) * 100) });
  const frames = (on) => evalIn(on
    ? `(() => { const f = (window.__f = { t: [], on: true, last: null }); (function loop(now) { if (!f.on) return; if (f.last !== null) f.t.push(now - f.last); f.last = now; requestAnimationFrame(loop); })(performance.now()); return 0; })()`
    : `window.__f.on = false; window.__f.t`);

  const idleWindow = async (name, seconds) => {
    phase = name;
    const m0 = await metrics();
    const c0 = await cpuByType();
    await sleep(seconds * 1000);
    const m1 = await metrics();
    const c1 = await cpuByType();
    const wall = m1.Timestamp - m0.Timestamp;
    return {
      seconds: round(wall, 1),
      ...pctOf(c0, c1, wall),
      main_thread_busy_pct: round(((m1.TaskDuration - m0.TaskDuration) / wall) * 100),
      script_pct: round(((m1.ScriptDuration - m0.ScriptDuration) / wall) * 100),
      layout_pct: round(((m1.LayoutDuration - m0.LayoutDuration) / wall) * 100),
      style_pct: round(((m1.RecalcStyleDuration - m0.RecalcStyleDuration) / wall) * 100),
      layouts: m1.LayoutCount - m0.LayoutCount,
      style_recalcs: m1.RecalcStyleCount - m0.RecalcStyleCount,
      dom_nodes: await evalIn(`document.getElementsByTagName("*").length`),
      js_heap_mb: round(m1.JSHeapUsedSize / 1048576, 1),
    };
  };

  // a0. idle on All teams, when that is the landing view
  let teamsIdle = null;
  if (landedOnTeams) teamsIdle = await idleWindow("teams_idle", TEAMS_IDLE_S);
  // Every landing path goes to the requested view; a run that cannot show it times out here rather than measure another.
  const { hash, check } = VIEWS[VIEW];
  if (landedOnTeams || !(await evalIn(check))) {
    await evalIn(`location.hash = ${JSON.stringify(hash)}; 0`);
    await ready(`(${check}) && !document.querySelector(".teams") && !/loading/i.test(document.body.innerText)`, `the ${VIEW} view`);
    await sleep(padMs(DWELL_S, Date.now() - landedAt));
  }
  const onView = async (when) => { if (!(await evalIn(check))) throw new Error(`--view ${VIEW}: the page left the view ${when}; location ${await evalIn("location.hash")}`); };
  await onView("before the idle window");
  const preIdleS = round((Date.now() - landedAt) / 1000, 1);

  // a. idle on the Graph view
  const idle = await idleWindow("idle", IDLE_S);

  // b. frame rate while the graph's motion runs
  phase = "motion";
  const mc0 = await cpuByType();
  const mt0 = Date.now();
  await frames(true);
  await sleep(MOTION_S * 1000);
  const motionFrames = await frames(false);
  const motionCpu = pctOf(mc0, await cpuByType(), (Date.now() - mt0) / 1000);
  await onView("during the idle and motion windows");
  const motion = { seconds: MOTION_S, gpu_cpu_pct: motionCpu.gpu_cpu_pct, page_cpu_pct: motionCpu.page_cpu_pct, fps: round(motionFrames.length / (motionFrames.reduce((a, b) => a + b, 0) / 1000), 1), frame_ms: mapRound(stats(motionFrames)), over_20ms: motionFrames.filter((x) => x > 20).length, over_50ms: motionFrames.filter((x) => x > 50).length };

  // c. a fast scroll of the transcript
  phase = "scroll";
  await evalIn(`location.hash = "#/conversation/" + encodeURIComponent(${JSON.stringify(MEMBER_ID)}); 0`);
  await ready(`(() => { const e = document.querySelector(".tr-scroll"); return !!e && e.scrollHeight > e.clientHeight * 2; })()`, "the transcript");
  await sleep(1500);
  const box = await evalIn(`(() => { const r = document.querySelector(".tr-scroll").getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2, rows: document.querySelector(".tr-scroll").scrollHeight, view: document.querySelector(".tr-scroll").clientHeight }; })()`);
  const s0 = await metrics();
  await frames(true);
  // The transcript is windowed: scrolling up pulls older windows in, then the fling comes back down through them.
  const trace = [];
  const flings = [1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, 1, 1, -1, -1];
  for (const up of flings) {
    await page.send("Input.synthesizeScrollGesture", { x: box.x, y: box.y, yDistance: up * 6000, speed: 6000, gestureSourceType: "mouse" });
    await sleep(300);
    trace.push(await evalIn(`(() => { const e = document.querySelector(".tr-scroll"); return e.scrollTop + "/" + e.scrollHeight; })()`));
  }
  const scrollFrames = await frames(false);
  const s1 = await metrics();
  const scroll = { scroll_height_px: box.rows, flings: flings.length, scroll_trace: trace, scroll_height_after_px: await evalIn(`document.querySelector(".tr-scroll").scrollHeight`), frame_ms: mapRound(stats(scrollFrames)), over_20ms: scrollFrames.filter((x) => x > 20).length, over_50ms: scrollFrames.filter((x) => x > 50).length, main_thread_busy_s: round(s1.TaskDuration - s0.TaskDuration), script_s: round(s1.ScriptDuration - s0.ScriptDuration), layout_s: round(s1.LayoutDuration - s0.LayoutDuration) };

  // d. the long-poll replies
  const of = (name) => polls.filter((p) => p.phase === name);
  const poll = (name) => {
    const list = of(name);
    const sum = (k) => list.reduce((n, p) => n + p[k], 0);
    return { replies: list.length, body_bytes: sum("body"), wire_bytes: sum("wire"), max_body_bytes: Math.max(0, ...list.map((p) => p.body)), mean_body_bytes: list.length ? Math.round(sum("body") / list.length) : 0, slices_in_replies: sum("slices") };
  };
  const heldSlices = (await mock.model.poll({ since: 0, epoch: null, sub: lastSub, wait: 0, signal: new AbortController().signal })).slices;
  const held = heldSlices.length;
  const planBytes = heldSlices.filter((s) => s.id.startsWith("plan:")).map((s) => Buffer.byteLength(JSON.stringify(s.body)));
  const questionsBytes = heldSlices.filter((s) => s.id.startsWith("questions:")).map((s) => Buffer.byteLength(JSON.stringify(s.body)));
  const preset = PRESETS[CURRENT];
  const result = { diagnostics: diagnostics ? "on" : "off", look: LOOK, theme: THEME ?? "system", dpr: DPR, view: VIEW, optics: SEED["ah-optics"], dwell_s: DWELL_S, pre_idle_s: preIdleS, ...(teamsIdle ? { teams_idle: teamsIdle } : null), fixture: { preset: CURRENT, members: preset.AH_MOCK_MEMBERS, open: preset.AH_MOCK_OPEN, done: preset.AH_MOCK_DONE, label: labelOf(CURRENT, preset), transcript_turns: TURNS, slices_held_by_the_page: held, subscription_ids: lastSub.length, load_bytes_non_poll: loadBytes }, request_line_max_bytes: requestLineMax, plan_slice_max_bytes: planBytes.length ? Math.max(...planBytes) : null, questions_slice_max_bytes: questionsBytes.length ? Math.max(...questionsBytes) : null, first_load_bytes: loadBytes + poll("load").wire_bytes, idle, motion, scroll, long_poll: { idle: poll("idle"), load: poll("load"), all: poll("idle").body_bytes + poll("load").body_bytes + poll("motion").body_bytes + poll("scroll").body_bytes } };

  page.close();
  browser.close();
  await cleanup();
  chrome = null;
  mock = null;
  return result;
}

function mapRound(o) {
  return Object.fromEntries(Object.entries(o).map(([k, v]) => [k, round(v)]));
}

// The median of every number across runs; the first run's value where it is not a number.
function medianOf(list) {
  const [first] = list;
  if (typeof first === "number") {
    const v = list.slice().sort((a, b) => a - b);
    return v.length % 2 ? v[(v.length - 1) / 2] : round((v[v.length / 2 - 1] + v[v.length / 2]) / 2);
  }
  if (first && typeof first === "object") {
    return Object.fromEntries(Object.keys(first).map((k) => [k, medianOf(list.map((x) => x[k]))]));
  }
  return first;
}

const run = { label: LABEL, at: new Date().toISOString(), chrome: chromeBinary(), runs_per_mode: RUNS, fixtures: NAMES, runs: [], median: [] };

// One preset in this process: the fixture reads AH_MOCK_FIXTURE when the mock is imported, so a second preset needs a second process.
async function runPreset(name) {
  CURRENT = name;
  process.env.AH_MOCK_FIXTURE = name;
  for (const diagnostics of DIAG === "both" ? [false, true] : [DIAG === "on"]) {
    const mode = [];
    for (let i = 0; i < RUNS; i += 1) {
      mode.push(await measureOne(diagnostics));
    }
    run.runs.push(...mode);
    run.median.push(medianOf(mode));
  }
}

function runPresetInChild(name) {
  const passed = [];
  for (let i = 0; i < args.length; i += 1) {
    if (args[i].startsWith("--fixture=")) continue;
    if (args[i] === "--fixture" || args[i] === "--json") {
      i += 1;
      continue;
    }
    passed.push(args[i]);
  }
  const file = join(tmp, `child-${name}.json`);
  const done = spawnSync(process.execPath, [import.meta.filename, ...passed, `--fixture=${name}`, "--child", "--json", file], { stdio: ["ignore", "ignore", "inherit"] });
  if (done.status !== 0) {
    throw new Error(`the ${name} run failed (exit ${done.status})`);
  }
  const part = JSON.parse(readFileSync(file, "utf8"));
  run.runs.push(...part.runs);
  run.median.push(...part.median);
}

try {
  for (const name of NAMES) {
    if (NAMES.length === 1) {
      await runPreset(name);
    } else {
      runPresetInChild(name);
    }
  }
} finally {
  await cleanup();
}
const out = JSON.stringify(run, null, 2);
if (flag("json", null)) {
  writeFileSync(flag("json"), out + "\n");
}
console.log(out);

// A realistic result is judged on the rows that need no baseline; --compare adds the baseline-relative ones.
let failed = false;
if (!CHILD) {
  for (const m of run.median.filter((e) => e.fixture.preset === "realistic")) {
    for (const r of absoluteRows(realisticRows(m.fixture.label, m.diagnostics, m, null))) {
      console.error(rowLine(r));
      failed ||= !r.ok;
    }
  }
}
process.exit(failed ? 1 : 0);

// The numbers where a rise is a regression, per diagnostics setting.
function compare(basePath, newPath) {
  const base = JSON.parse(readFileSync(basePath, "utf8"));
  const next = JSON.parse(readFileSync(newPath, "utf8"));
  // Numbers from two different browser binaries are not comparable; a different path is a different binary.
  if (base.chrome !== next.chrome) {
    console.log(`baseline recorded on ${base.chrome}, this run uses ${next.chrome}; a new Chrome or Playwright build counts as a new binary, so re-record the baseline: a --fixture both run on this binary, then --record-baseline`);
    return 1;
  }
  const rows = [];
  let failed = false;
  const presetOf = (r) => r.fixture?.preset ?? "small";
  const newEntries = next.median ?? next.runs;
  const compared = next.fixtures ?? ["small"];
  for (const b of compared.includes("small") ? (base.median ?? base.runs) : []) {
    const n = newEntries.find((r) => r.diagnostics === b.diagnostics && presetOf(r) === "small");
    if (!n) {
      console.log(`REGRESSION  diag ${b.diagnostics}: missing from ${newPath}`);
      failed = true;
      continue;
    }
    for (const [who, r] of [["baseline", b], ["run", n]]) {
      if (r.pre_idle_s !== undefined && Math.abs(r.pre_idle_s - r.dwell_s) > 1) {
        console.log(`REGRESSION  diag ${b.diagnostics}: ${who} pre-idle ${r.pre_idle_s} s is more than 1 s from its dwell ${r.dwell_s} s`);
        failed = true;
      }
    }
    if (b.dwell_s !== n.dwell_s) {
      console.log(`REGRESSION  diag ${b.diagnostics}: dwell ${b.dwell_s ?? "none"} s in the baseline, ${n.dwell_s ?? "none"} s in the run; byte counts are not comparable`);
      failed = true;
    }
    rows.push([`diag ${b.diagnostics} idle page cpu %`, b.idle.page_cpu_pct, n.idle.page_cpu_pct, judge(b.idle.page_cpu_pct, n.idle.page_cpu_pct, EPS_PCT)]);
    rows.push([`diag ${b.diagnostics} idle main-thread busy %`, b.idle.main_thread_busy_pct, n.idle.main_thread_busy_pct, judge(b.idle.main_thread_busy_pct, n.idle.main_thread_busy_pct, EPS_PCT)]);
    if (b.teams_idle && n.teams_idle) {
      rows.push([`diag ${b.diagnostics} All teams idle page cpu %`, b.teams_idle.page_cpu_pct, n.teams_idle.page_cpu_pct, judge(b.teams_idle.page_cpu_pct, n.teams_idle.page_cpu_pct, EPS_PCT)]);
      rows.push([`diag ${b.diagnostics} All teams idle main-thread busy %`, b.teams_idle.main_thread_busy_pct, n.teams_idle.main_thread_busy_pct, judge(b.teams_idle.main_thread_busy_pct, n.teams_idle.main_thread_busy_pct, EPS_PCT)]);
    }
    rows.push([`diag ${b.diagnostics} motion fps`, b.motion.fps, n.motion.fps, judge(b.motion.fps, n.motion.fps, EPS_FPS, true)]);
    rows.push([`diag ${b.diagnostics} scroll frame p95 ms`, b.scroll.frame_ms.p95, n.scroll.frame_ms.p95, judge(b.scroll.frame_ms.p95, n.scroll.frame_ms.p95, EPS_MS)]);
    rows.push([`diag ${b.diagnostics} long-poll idle bytes`, b.long_poll.idle.body_bytes, n.long_poll.idle.body_bytes, judge(b.long_poll.idle.body_bytes, n.long_poll.idle.body_bytes, EPS_BYTES)]);
    for (const row of rows.filter((r) => !r.labelled)) {
      row[0] = `${n.fixture?.label ?? "fixture=small"} ${row[0]}`;
      row.labelled = true;
    }
  }
  const realisticBase = base.realistic;
  for (const n of newEntries.filter((r) => presetOf(r) === "realistic")) {
    if (!realisticBase) {
      console.log(`${n.fixture.label}: no baseline recorded; the baseline-relative rows are not gated`);
    }
    const baseEntry = realisticBase?.median.find((m) => m.diagnostics === n.diagnostics);
    const gatedRows = realisticRows(n.fixture.label, n.diagnostics, n, baseEntry ? { ...baseEntry, idle_bytes_gated: realisticBase.idle_bytes_gated } : null);
    for (const r of gatedRows) {
      failed ||= !r.ok;
      console.log(rowLine(r));
    }
  }
  for (const [what, b, n, verdict] of rows) {
    failed ||= !verdict.ok;
    console.log(`${verdict.ok ? "ok        " : "REGRESSION"}  ${what}: ${b} -> ${n} (${round(verdict.pct, 1)}% worse; ${verdict.rule === "fail" ? "outside both the 10% and the absolute floor" : `passed by the ${verdict.rule} rule`})`);
  }
  return failed ? 1 : 0;
}
