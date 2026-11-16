import { test } from "node:test";
import assert from "node:assert/strict";
import { settle } from "./dom.mjs";
import { mountApp, until, find } from "./mount-app.mjs";

const { fitView, zoomAt, ZOOM, VIEW, setGraph } = await import("../../web/ui/graph.js");

const world = (root) => root.all("g").find((g) => /\bworld\b/.test(g.getAttribute("class") ?? ""));
const viewOf = (root) => {
  const m = /translate\((-?[\d.]+)px, (-?[\d.]+)px\) scale\(([\d.]+)\)/.exec(world(root).style.transform);
  return { x: Number(m[1]), y: Number(m[2]), k: Number(m[3]) };
};
const button = (root, label) => root.all("button").find((b) => b.getAttribute("aria-label") === label || b.textContent === label);
const stage = (root) => root.all("svg").find((s) => /\bstage\b/.test(s.getAttribute("class") ?? ""));
const nodes = (root) => root.all("div.fcard");
// The zoomable stage is Flow's now (Map's zoom is d3's); these tests drive it there.
const mountFlow = async () => {
  const m = await mountApp();
  setGraph(m.store, { mode: "flow" });
  await until(() => nodes(m.root).length === 8, "the Flow cards");
  await settle();
  return m;
};
const near = (a, b) => Math.abs(a - b) < 1e-6;

test("fit centres the graph's box at the largest scale that fits, capped for a small team; no box is the identity", () => {
  const box = { x: 100, y: 50, w: 800, h: 400 };
  const v = fitView(box, 1000, 700, 0);
  assert.equal(v.k, 1.25, "the width decides");
  assert.ok(near(v.x + v.k * (box.x + box.w / 2), 500), "centred across");
  assert.ok(near(v.y + v.k * (box.y + box.h / 2), 350), "centred down");
  assert.equal(fitView({ x: 0, y: 0, w: 100, h: 100 }, 1000, 700).k, ZOOM.fitMax, "a small team is not blown up past the cap");
  assert.equal(fitView({ x: 0, y: 0, w: 8000, h: 4000 }, 1000, 700).k, ZOOM.min, "a huge graph shrinks to the floor");
  assert.deepEqual(fitView(null, 1000, 700), { k: 1, x: 0, y: 0 });
});

test("zoom keeps the point under the cursor still and stays within its limits", () => {
  const v = zoomAt({ k: 1, x: 10, y: 20 }, 2, 300, 200);
  assert.equal(v.k, 2);
  assert.ok(near((300 - v.x) / v.k, 290), "the layout point under the cursor is the same before and after");
  assert.ok(near((200 - v.y) / v.k, 180));
  assert.equal(zoomAt({ k: 3, x: 0, y: 0 }, 10, 0, 0).k, ZOOM.max);
  assert.equal(zoomAt({ k: 0.3, x: 0, y: 0 }, 0.1, 0, 0).k, ZOOM.min);
});

test("the graph fits on load; a joining member re-fits it while the user has not moved it", async () => {
  const m = await mountFlow();
  const first = viewOf(m.root);
  assert.equal(button(m.root, "Fit").getAttribute("aria-pressed"), "true");
  m.model.churn();
  await until(() => nodes(m.root).length === 9, "the new member");
  await settle();
  assert.notDeepEqual(viewOf(m.root), first, "the fit follows the new layout");
  for (let i = 0; i < 5; i += 1) m.model.churn();
  await until(() => nodes(m.root).length === 8, "they left");
});

test("once zoomed, a joining member is laid out but the view stays; Fit, or 0, goes back to fitting", async () => {
  const m = await mountFlow();
  const fitted = viewOf(m.root);
  button(m.root, "Zoom in").fire("click");
  await settle();
  const zoomed = viewOf(m.root);
  assert.ok(near(zoomed.k, fitted.k * ZOOM.step), "one step in");
  assert.equal(button(m.root, "Fit").getAttribute("aria-pressed"), "false");
  m.model.churn();
  await until(() => nodes(m.root).length === 9, "the new member");
  await settle();
  assert.deepEqual(viewOf(m.root), zoomed, "the user's view is kept");
  button(m.root, "Fit").fire("click");
  await settle();
  const refit = viewOf(m.root);
  assert.notDeepEqual(refit, zoomed);
  assert.equal(button(m.root, "Fit").getAttribute("aria-pressed"), "true");
  stage(m.root).fire("keydown", { key: "-" });
  await settle();
  const out = viewOf(m.root);
  assert.ok(out.k < refit.k, "− zooms out");
  stage(m.root).fire("keydown", { key: "ArrowRight" });
  await settle();
  assert.ok(near(viewOf(m.root).x, out.x - ZOOM.pan), "→ pans");
  stage(m.root).fire("keydown", { key: "0", ctrlKey: true });
  await settle();
  assert.notDeepEqual(viewOf(m.root), refit, "ctrl+0 is the browser's, not ours");
  stage(m.root).fire("keydown", { key: "0" });
  await settle();
  assert.deepEqual(viewOf(m.root), refit, "0 fits again");
  for (let i = 0; i < 5; i += 1) m.model.churn();
  await until(() => nodes(m.root).length === 8, "they left");
});

test("a drag pans by the pointer's travel, a wobble under the threshold does not; two fingers pinch; a trackpad scroll pans", async () => {
  const m = await mountFlow();
  const svg = stage(m.root);
  svg.rect = { left: 0, top: 0, width: VIEW.w, height: VIEW.h, right: VIEW.w, bottom: VIEW.h };
  const before = viewOf(m.root);
  svg.fire("pointerdown", { pointerId: 1, button: 0, clientX: 200, clientY: 200 });
  svg.fire("pointermove", { pointerId: 1, clientX: 203, clientY: 201 });
  await settle();
  assert.deepEqual(viewOf(m.root), before, "3 px is a click, not a drag");
  svg.fire("pointermove", { pointerId: 1, clientX: 260, clientY: 230 });
  await settle();
  const after = viewOf(m.root);
  assert.ok(near(after.x - before.x, 60) && near(after.y - before.y, 30));
  svg.fire("pointerup", { pointerId: 1 });
  svg.fire("pointerdown", { pointerId: 1, button: 0, clientX: 100, clientY: 100 });
  svg.fire("pointerdown", { pointerId: 2, button: 0, clientX: 200, clientY: 100 });
  svg.fire("pointermove", { pointerId: 2, clientX: 300, clientY: 100 });
  await settle();
  const pinched = viewOf(m.root);
  assert.ok(near(pinched.k, after.k * 2), "two fingers twice as far apart zoom in twice");
  svg.fire("pointerup", { pointerId: 1 });
  svg.fire("pointerup", { pointerId: 2 });
  svg.fire("wheel", { deltaX: 4, deltaY: 30.5, deltaMode: 0, ctrlKey: false, clientX: 0, clientY: 0 });
  await settle();
  const scrolled = viewOf(m.root);
  assert.ok(near(scrolled.y, pinched.y - 30.5) && near(scrolled.k, pinched.k), "a trackpad scroll pans, not zooms");
  svg.fire("wheel", { deltaX: 0, deltaY: -10, deltaMode: 0, ctrlKey: true, clientX: 0, clientY: 0 });
  await settle();
  assert.ok(viewOf(m.root).k > scrolled.k, "a pinch (ctrl wheel) zooms");
});

test("the first measurement snaps into place, and the spring is back for the refits after it", async () => {
  const observers = [];
  globalThis.ResizeObserver = class {
    constructor(cb) { this.cb = cb; observers.push(this); }
    observe() {}
    disconnect() {}
  };
  try {
    const m = await mountFlow();
    const cls = () => world(m.root).getAttribute("class");
    observers.at(-1).cb([{ contentRect: { width: 900, height: 600 } }]);
    await settle();
    assert.match(cls(), /\binstant\b/, "first paint at the measured size does not animate");
    await until(() => !/\binstant\b/.test(cls()), "the spring returns", 1000);
    observers.at(-1).cb([{ contentRect: { width: 700, height: 600 } }]);
    await settle();
    assert.doesNotMatch(cls(), /\binstant\b/, "a resize refit springs");
  } finally {
    delete globalThis.ResizeObserver;
  }
});

// The svg that holds the edges in both Map and Flow fills its pane at any height, not a fixed share of the window.
const assertStageFillsPane = async () => {
  const { readFileSync } = await import("node:fs");
  const read = (f) => readFileSync(new URL(`../../web/ui/${f}`, import.meta.url), "utf8");
  const decls = (css, selector) => {
    const out = {};
    for (const m of css.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
      if (m[1].split(",").some((s) => s.trim() === selector)) {
        for (const d of m[2].split(";")) {
          const i = d.indexOf(":");
          i > 0 && (out[d.slice(0, i).trim()] = d.slice(i + 1).trim());
        }
      }
    }
    return out;
  };
  const r6 = read("r6.css");
  const graph = decls(r6, ".r6 .graph");
  assert.equal(graph.display, "flex");
  assert.equal(graph["flex-direction"], "column");
  const svg = decls(r6, ".r6 .graph > .stage");
  assert.equal(svg.flex, "1 1 0", "the svg takes the whole height of the graph");
  assert.equal(svg.height, "auto", "no fixed height caps the svg below its pane");
  assert.equal(svg["min-height"], "0", "a short pane shrinks the svg instead of clipping it");
  assert.equal(decls(read("r6-shell.css"), ".r6 .stage-wrap > .graph").overflow, "hidden", "the graph pane is not a scroll region");
};

test("the graph's stage fills its pane at any height, not a fixed share of the window", async () => {
  const m = await mountFlow();
  assert.equal(stage(m.root).parentNode.getAttribute("class"), "graph gflow");
  await assertStageFillsPane();
});

test("in Flow, horizontal and vertical, the edges are drawn in that same stage, so they reach the bottom of the pane", async () => {
  const m = await mountApp();
  for (const orient of ["horizontal", "vertical"]) {
    setGraph(m.store, { mode: "flow", orient });
    await until(() => find(m.root, ".gflow").length === 1 && find(m.root, ".orient")[0]?.textContent.startsWith(orient === "vertical" ? "Vertical" : "Horizontal"), orient);
    const svg = stage(m.root);
    assert.equal(svg.parentNode.getAttribute("class"), "graph gflow", orient);
    assert.ok(svg.all("path").length > 0, `${orient}: the Flow links are in the stage`);
  }
  await assertStageFillsPane();
});
