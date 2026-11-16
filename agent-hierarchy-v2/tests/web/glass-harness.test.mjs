import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { deflateSync } from "node:zlib";
import { lookRows, VIEWS } from "../perf/e4-gates.mjs";
import { decodePng, pixelsIn } from "../perf/png.mjs";
import { floorTable, tableDiff, worstByTier } from "../perf/optics-floor.mjs";

// The glass harness pieces (ah-optical-glass §8, §9.3) that run without a browser.

const entry = (look, view, over = {}) => ({
  look, view, theme: "dark", dpr: 2, diagnostics: "off", fixture: { label: "fixture=realistic(50/40/1500)" },
  idle: { gpu_cpu_pct: 5, page_cpu_pct: 2, dom_nodes: 900, ...over.idle },
  motion: { gpu_cpu_pct: 6, fps: 60, frame_ms: { p95: 17 }, over_20ms: 2, ...over.motion },
  scroll: { frame_ms: { p95: 17, p99: 20 }, ...over.scroll },
});
const failing = (rows) => rows.filter((r) => !r.ok).map((r) => r.metric.split(" (")[0]);

test("the glass GPU gate allows house + 1.0 point idle and house + 2.0 in motion, and fails just past them", () => {
  assert.deepEqual(failing(lookRows(entry("house", "map"), entry("glass", "map", { idle: { gpu_cpu_pct: 6 }, motion: { gpu_cpu_pct: 8 } }))), []);
  assert.deepEqual(failing(lookRows(entry("house", "map"), entry("glass", "map", { idle: { gpu_cpu_pct: 6.01 } }))), ["idle gpu cpu %"]);
  assert.deepEqual(failing(lookRows(entry("house", "radial"), entry("glass", "radial", { motion: { gpu_cpu_pct: 8.01 } }))), ["motion gpu cpu %"]);
});

test("the renderer row is house x 1.10 + 0.2 on the Graph views only, and the motion rows skip a view without motion", () => {
  assert.deepEqual(failing(lookRows(entry("house", "map"), entry("glass", "map", { idle: { page_cpu_pct: 2.4 } }))), []);
  assert.deepEqual(failing(lookRows(entry("house", "map"), entry("glass", "map", { idle: { page_cpu_pct: 2.41 } }))), ["idle page cpu %"]);
  const overview = lookRows(entry("house", "overview"), entry("glass", "overview", { idle: { page_cpu_pct: 9 }, motion: { gpu_cpu_pct: 99, fps: 1 } }));
  assert.deepEqual(failing(overview), []);
  assert.ok(!overview.some((r) => r.metric.startsWith("motion")));
});

test("the GPU rows need DPR 2; the motion fps gate follows the S8 rule (10% or 1 fps)", () => {
  const dpr1 = lookRows({ ...entry("house", "map"), dpr: 1 }, { ...entry("glass", "map", { idle: { gpu_cpu_pct: 50 } }), dpr: 1 });
  assert.ok(!dpr1.some((r) => r.metric.includes("gpu")));
  assert.deepEqual(failing(lookRows(entry("house", "board"), entry("glass", "board", { motion: { fps: 54 } }))), []);
  assert.deepEqual(failing(lookRows(entry("house", "board"), entry("glass", "board", { motion: { fps: 53 } }))), ["motion fps"]);
});

// A PNG built by hand: a 2x2 RGB image, rows filtered with Sub then Up.
function png(width, height, rows) {
  const chunk = (type, data) => {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    return Buffer.concat([len, Buffer.from(type, "latin1"), data, Buffer.alloc(4)]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr.set([8, 2, 0, 0, 0], 8);
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(Buffer.from(rows.flat()))), chunk("IEND", Buffer.alloc(0))]);
}

test("optics-floor refuses --optics that is not a JSON object before it starts a browser", () => {
  const floor = new URL("../perf/optics-floor.mjs", import.meta.url).pathname;
  for (const bad of ["{bad", "[]", "null"]) {
    const done = spawnSync(process.execPath, [floor, "--optics", bad], { encoding: "utf8", timeout: 20000 });
    assert.equal(done.status, 2, `${bad}: ${done.stderr}`);
    assert.match(done.stderr, /--optics needs a JSON object/);
  }
});

test("each --view check holds on its own view only, and none holds on the Teams landing", () => {
  // A page as the set of selectors that match on it.
  const pages = {
    map: [".stage-wrap.view-graph"],
    radial: [".stage-wrap.view-graph", ".stage-wrap .gr-body", ".stage-wrap.view-graph .gr-body"],
    // the Board's view is `board` and its look the kanban since U4/board-r2
    board: [".stage-wrap.view-board", ".stage-wrap:is(.view-plans, .view-board) :is(.kb-card, .ice-tile)"],
    overview: [".stage-wrap.view-overview"],
    teams: [".teams"],
  };
  const holds = (check, page) => new Function("document", `return ${check}`)({ querySelector: (s) => (pages[page].includes(s) ? {} : null) });
  for (const [view, { check }] of Object.entries(VIEWS)) {
    assert.deepEqual(Object.keys(pages).filter((p) => holds(check, p)), [view], `--view ${view}`);
  }
});

test("the PNG decoder undoes the row filters and the box read clips to the image", () => {
  // Row 0, Sub: (10,20,30) then +(5,5,5) -> (15,25,35). Row 1, Up: +(1,1,1) on each pixel above.
  const img = decodePng(png(2, 2, [[1, 10, 20, 30, 5, 5, 5], [2, 1, 1, 1, 1, 1, 1]]));
  assert.deepEqual(pixelsIn(img, [0, 0, 2, 2]), [[10, 20, 30], [15, 25, 35], [11, 21, 31], [16, 26, 36]]);
  assert.deepEqual(pixelsIn(img, [1, 1, 9, 9]), [[16, 26, 36]]);
  assert.throws(() => decodePng(Buffer.from("not a png at all")), /not a PNG/);
});

test("the floor ratio is the 98th-percentile worst halo pixel per tier, and the table keeps only cells under 4.5:1", () => {
  // 100 white pixels and one black one: the single black pixel is past the 2% tail, so white ink reads as 1:1.
  const data = Buffer.alloc(101 * 4, 255);
  data.set([0, 0, 0, 255], 0);
  const img = { width: 101, height: 1, data };
  const w = worstByTier(img, [{ tier: "T2", ink: "rgb(0, 0, 0)", box: [0, 0, 101, 1] }, { tier: "T3", ink: "rgb(255, 255, 255)", box: [0, 0, 101, 1] }]);
  assert.equal(w.T2, 21);
  assert.equal(w.T3, 1);
  const table = floorTable({ "light/obsidian": { "10/80/85/18": { T2: 6, T3: 5 }, "1/200/35/18": { T2: 3.9, T3: 6 } } });
  assert.deepEqual(table, { "light/obsidian": { "1/200/35/18": 3.9 } });
});

test("--check finds a cell that appeared or a ratio that moved by more than 0.1, and accepts 0.1", () => {
  const a = { "light/obsidian": { "1/200/35/18": 3.9 } };
  assert.deepEqual(tableDiff(a, { "light/obsidian": { "1/200/35/18": 4.0 } }), []);
  assert.equal(tableDiff(a, { "light/obsidian": { "1/200/35/18": 4.05 } }).length, 1);
  assert.equal(tableDiff(a, { ...a, "emissive/forest": { "1/20/35/18": 4.1 } }).length, 1);
});
