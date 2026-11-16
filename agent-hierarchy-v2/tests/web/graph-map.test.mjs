import test from "node:test";
import assert from "node:assert/strict";
import { DIR, find, member, mountApp, text, until } from "./mount-app.mjs";

// The Map stage's own interactions: Esc, keyboard zoom and pan, container fly-in and out, Enter on a member, the scrubber and Play,
// and that d3 is fetched by Map only.

const stage = (m) => find(m.root, "div.gpack")[0];
const svg = (m) => find(m.root, "svg.gm-svg")[0];
const leaf = (m, name) => find(m.root, "g.gm-leaf").find((g) => g.getAttribute("aria-label").split(", ")[0].endsWith(` ${name}`));
const key = (el, k, extra = {}) => {
  let prevented = false;
  el.fire("keydown", { key: k, preventDefault() { prevented = true; }, ...extra });
  return prevented;
};
// the shim matches tag.class only, so an id is looked up by attribute
const byId = (m, tag, id) => find(m.root, tag).filter((e) => e.getAttribute("id") === id);
const attr = (el, name) => el.getAttribute(name) ?? el[name];
const disabled = (el) => {
  const v = el.disabled ?? el.getAttribute("disabled");
  return v === true || v === "" || v === "true" || v === "disabled";
};
// The test fixture serves no flow head, so the scrub tests put one in the store: 30 minutes of run, one brief a minute.
const withHead = (m) => {
  const start = Date.now() - 30 * 60_000;
  const tail = Array.from({ length: 30 }, (_, i) => [i * 60 + 30, 0, 1, i % 2]);
  const body = { run: "r1", start_ms: start, oldest_ms: start, truncated: false, names: [member("architect"), member("reviewer")], totals: [[0, 1, 15, 15]], page_size: 250, pages: [], tail, pages_rev: "a" };
  m.store.state.slices.set(`flow:${DIR}/ship`, { id: `flow:${DIR}/ship`, as_of: start + tail[29][0] * 1000, body });
  m.store.setUi({ graphWindow: null });
};
const headReady = (m) => (withHead(m), until(() => byId(m, "input", "gm-time").length === 1 && !disabled(byId(m, "input", "gm-time")[0]), "the flow head"));
const d3Scripts = () => (globalThis.document.head.children ?? globalThis.document.head.childNodes ?? []).filter((c) => c.localName === "script" && String(c.src).includes("d3"));

test("Esc in the stage clears the selection first, then flies out of a container, then closes the card; elsewhere it leaves the map alone", async () => {
  const m = await mountApp();
  const streams = find(m.root, "g.gm-node");
  assert.ok(streams.length > 0, "the mock team has stream circles");
  streams[0].fire("click");
  const id = m.store.state.ui.graphFocus;
  assert.ok(id?.startsWith("stream:"), `focus on the stream, got ${id}`);
  assert.equal(m.store.state.ui.graphCard, id, "its card shows");
  await until(() => find(m.root, "section.gm-panel").length === 1, "the container card");
  leaf(m, "architect").fire("click");
  assert.equal(m.store.state.ui.selected, member("architect"));
  // outside the stage (the crumbs are above it), the page's own Esc clears the selection only; the map's focus and card stay
  find(m.root, "nav.gm-crumbs")[0].fire("keydown", { key: "Escape" });
  assert.equal(m.store.state.ui.graphFocus, id);
  m.store.setUi({ selected: member("architect") });
  assert.equal(key(stage(m), "Escape"), true);
  assert.equal(m.store.state.ui.selected, null, "first Esc: the selection");
  assert.equal(m.store.state.ui.graphFocus, id);
  key(stage(m), "Escape");
  assert.equal(m.store.state.ui.graphFocus, null, "second Esc: out to the team");
  assert.equal(m.store.state.ui.graphCard, null, "flying out closes the card");
  await until(() => find(m.root, "section.feed").length === 1, "the live feed is back in the rail");
  assert.equal(key(stage(m), "Escape"), false, "at the team with nothing open, Esc is left to the page");
  // the team's crumb shows the team card; Esc closes it
  find(m.root, "nav.gm-crumbs")[0].all("button")[0].fire("click");
  assert.equal(m.store.state.ui.graphCard, "team");
  assert.equal(key(stage(m), "Escape"), true);
  assert.equal(m.store.state.ui.graphCard, null);
});

test("a container click flies in and shows its card; clicking it again flies back out to the feed", async () => {
  const m = await mountApp();
  const node = find(m.root, "g.gm-node")[0];
  node.fire("click");
  const id = m.store.state.ui.graphFocus;
  assert.ok(id);
  await until(() => find(m.root, "section.gm-panel").length === 1, "the card");
  assert.match(text(find(m.root, "section.gm-panel")[0]), /stream ·/);
  assert.equal(find(m.root, "nav.gm-crumbs")[0].all("button").length, 2, "team / stream");
  find(m.root, "g.gm-node")[0].fire("click");
  assert.equal(m.store.state.ui.graphFocus, null);
  await until(() => find(m.root, "section.feed").length === 1, "the feed");
});

test("Enter and Space on a member select it; the background deselects and closes any card", async () => {
  const m = await mountApp();
  assert.equal(key(leaf(m, "reviewer"), "Enter"), true);
  assert.equal(m.store.state.ui.selected, member("reviewer"));
  key(leaf(m, "reviewer"), " ");
  assert.equal(m.store.state.ui.selected, null, "Space toggles it off");
  m.store.setUi({ selected: member("reviewer"), graphCard: "stream:x" });
  svg(m).fire("click");
  assert.equal(m.store.state.ui.selected, null);
  assert.equal(m.store.state.ui.graphCard, null);
});

test("on the focused map + − zoom, 0 fits, the arrows pan; a modifier or another key is left to the page", async () => {
  const m = await mountApp();
  for (const k of ["+", "=", "-", "_", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"]) {
    assert.equal(key(svg(m), k), true, k);
  }
  assert.equal(key(svg(m), "+", { ctrlKey: true }), false, "ctrl + is the browser's");
  assert.equal(key(svg(m), "a"), false);
  find(m.root, "g.gm-node")[0].fire("click");
  assert.ok(m.store.state.ui.graphFocus);
  assert.equal(key(svg(m), "0"), true);
  assert.equal(m.store.state.ui.graphFocus, null, "0 fits the team");
  assert.match(attr(svg(m), "aria-label"), /\+ and − zoom, 0 fits, arrows pan/);
});

test("the scrubber: the cursor at its right end follows now; earlier it holds; the window select sets the window", async () => {
  const m = await mountApp();
  await headReady(m);
  const range = byId(m, "input", "gm-time")[0];
  const max = Number(attr(range, "max"));
  const min = Number(attr(range, "min"));
  assert.ok(max > min);
  range.value = String(max - 5 * 60_000);
  range.fire("input");
  assert.equal(m.store.state.ui.graphCursor, max - 5 * 60_000);
  await until(() => /now \+5m/.test(text(find(m.root, "span.gm-end")[0])), "the end label says how far now is");
  // the clock has moved since: the right end is the max the slider shows now
  const range2 = byId(m, "input", "gm-time")[0];
  range2.value = String(attr(range2, "max"));
  range2.fire("input");
  assert.equal(m.store.state.ui.graphCursor, null, "at the right end the view is live");
  const win = byId(m, "select", "gm-window")[0];
  win.value = "10";
  win.fire("change");
  assert.equal(m.store.state.ui.graphWindow, "10");
});

test("Play steps the cursor from the run start to now in whole-minute steps on a 400 ms interval and stops there; it is off under reduced motion", async () => {
  const m = await mountApp();
  await headReady(m);
  const realSet = globalThis.setInterval;
  let tick = null;
  let every = 0;
  globalThis.setInterval = (fn, ms) => ((tick = fn), (every = ms), 7);
  try {
    find(m.root, "button.gm-play")[0].fire("click");
    assert.equal(every, 400);
    assert.notEqual(m.store.state.ui.graphCursor, null, "from now, Play starts at the run start");
    await until(() => text(find(m.root, "button.gm-play")[0]) === "Pause", "Pause shows");
    const seen = [m.store.state.ui.graphCursor];
    for (let i = 0; i < 40 && m.store.state.ui.graphCursor !== null; i++) {
      tick();
      m.store.state.ui.graphCursor !== null && seen.push(m.store.state.ui.graphCursor);
    }
    assert.equal(m.store.state.ui.graphCursor, null, "it stops at now");
    assert.ok(seen.length >= 10 && seen.every((v, i) => i === 0 || (v % 60_000 === 0 && v > seen[i - 1])), "rising whole-minute steps of about a 24th of the run");
    await until(() => text(find(m.root, "button.gm-play")[0]) === "Play", "Play shows again");
  } finally {
    globalThis.setInterval = realSet;
  }
  const realMM = globalThis.matchMedia;
  globalThis.matchMedia = () => ({ matches: true });
  try {
    m.store.setUi({ graphWindow: "30" });
    await until(() => disabled(find(m.root, "button.gm-play")[0]), "Play disabled under reduced motion");
  } finally {
    globalThis.matchMedia = realMM;
  }
});

test("d3 is fetched by Map only: overview and Flow append no d3 script, Map appends one", async () => {
  const saved = globalThis.d3;
  delete globalThis.d3;
  try {
    const m = await mountApp({ view: "overview" });
    await new Promise((r) => setTimeout(r, 30));
    assert.equal(d3Scripts().length, 0, "overview");
    m.store.setUi({ view: "graph", graphMode: "flow" });
    await until(() => find(m.root, "div.fcard").length > 0, "Flow cards");
    assert.equal(d3Scripts().length, 0, "Flow");
    m.store.setUi({ graphMode: "map" });
    await until(() => d3Scripts().length === 1, "Map asks for d3 once");
  } finally {
    globalThis.d3 = saved;
  }
});

test("the first fit, once the stage has a size, opens no card: the rail stays on the live feed", async () => {
  const realRO = globalThis.ResizeObserver;
  let resized = null;
  globalThis.ResizeObserver = class { constructor(fn) { resized = fn; } observe() {} disconnect() {} };
  globalThis.SVGElement ??= class {}; // d3.zoom asks whether the stage is an SVGElement; the shim has none
  try {
    const m = await mountApp();
    assert.ok(resized, "Map watches its stage size");
    resized([{ contentRect: { width: 900, height: 600 } }]);
    await new Promise((r) => setTimeout(r, 30));
    assert.equal(m.store.state.ui.graphCard ?? null, null);
    assert.equal(find(m.root, "section.feed").length, 1);
  } finally {
    globalThis.ResizeObserver = realRO;
  }
});

const num = (el, name) => Number(el.getAttribute(name));
const leafCircles = (m) => find(m.root, "g.gm-leaf").map((g) => {
  const [, x, y] = /translate\(([-\d.]+),([-\d.]+)\)/.exec(g.getAttribute("transform"));
  return { id: g.getAttribute("aria-label"), x: Number(x), y: Number(y), r: num(g, "data-r") };
});

test("members have air: half a member radius between any two, and none touches a circle it is not in", async () => {
  const m = await mountApp();
  const leaves = leafCircles(m);
  const ctrs = find(m.root, "circle.ctr").filter((c) => !/\broot\b/.test(c.getAttribute("class"))).map((c) => ({ x: num(c, "cx"), y: num(c, "cy"), r: num(c, "r") }));
  assert.ok(ctrs.length > 0, "the fixture has stream circles");
  const minR = Math.min(...leaves.map((l) => l.r));
  for (const [i, a] of leaves.entries()) {
    for (const b of leaves.slice(i + 1)) {
      assert.ok(Math.hypot(a.x - b.x, a.y - b.y) - a.r - b.r >= 0.5 * minR - 1, `${a.id} and ${b.id} too close`);
    }
    for (const c of ctrs) {
      const d = Math.hypot(a.x - c.x, a.y - c.y);
      const inside = d < c.r;
      assert.ok(inside ? c.r - d - a.r >= 0.5 * minR - 1 : d - a.r - c.r >= 0.5 * minR - 1, `${a.id} crowds a stream outline`);
    }
  }
});

test("one arc per pair and kind, whichever way the messages ran", async () => {
  const m = await mountApp();
  const withHeadBoth = () => {
    const start = Date.now() - 10 * 60_000;
    const tail = [[60, 0, 1, 0], [120, 1, 0, 0], [180, 1, 0, 1]];
    const body = { run: "r1", start_ms: start, oldest_ms: start, truncated: false, names: [member("architect"), member("reviewer")], totals: [[0, 1, 1, 0], [1, 0, 1, 1]], page_size: 250, pages: [], tail, pages_rev: "a" };
    m.store.state.slices.set(`flow:${DIR}/ship`, { id: `flow:${DIR}/ship`, as_of: start + 180_000, body });
    m.store.setUi({ graphWindow: "30" });
  };
  withHeadBoth();
  await until(() => find(m.root, "path.gm-arc").length > 0, "arcs");
  const arcs = find(m.root, "path.gm-arc");
  assert.equal(arcs.filter((a) => /\bbrief\b/.test(a.getAttribute("class"))).length, 1, "both directions of briefs share one arc");
  assert.equal(arcs.filter((a) => /\breport\b/.test(a.getAttribute("class"))).length, 1);
});

test("a focus or card the team does not have (left from another team) is dropped", async () => {
  const m = await mountApp();
  m.store.setUi({ graphFocus: "stream:not-here", graphCard: "stream:not-here" });
  await until(() => (m.store.state.ui.graphCard ?? null) === null && (m.store.state.ui.graphFocus ?? null) === null, "the stale card dropped");
  await until(() => find(m.root, "section.feed").length === 1, "the feed");
});
