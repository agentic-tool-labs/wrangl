// The ah2 Herdr mod's Graph data side (spec-herdr-desktop.md § View: graph) over the shipped fixture.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { graphModel, graphSubs, KNOB, FAINT, cursorAt, stepCursor, SCALE_MIN, SCALE_MAX, SCALE_STEP, clampScale, clampPan, panAround, drillTo, fitView, rescaleTo, frameOf, wheelAct, easedScale, EASE_MS, WHEEL_EASE_MS, spring, dragAct, DRAG_PX, wasDrag, CLICK_MS } from "../../herdr-desktop/mods/ah2/graph-model.js";
import { hierarchy, pack, bundle } from "../../herdr-desktop/mods/ah2/graph-layout.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const mod = join(root, "herdr-desktop", "mods", "ah2");
const reply = JSON.parse(readFileSync(join(root, "herdr-desktop", "fixture", "events.json"), "utf8"));
const slices = new Map(reply.slices.map((s) => [s.id, s]));

for (const f of ["flow-model.js", "graph-world.js", "plans-model.js", "select.js"]) {
  test(`the mod's ${f} is a byte-identical copy of web/ui/${f}`, () => {
    assert.ok(readFileSync(join(mod, f)).equals(readFileSync(join(root, "web", "ui", f))), `copy web/ui/${f} over herdr-desktop/mods/ah2/${f}`);
  });
}

// Herdr cannot load d3.min.js, so graph-layout.js ports the pieces the web Map uses; it must lay out exactly as the vendored d3 does.
test("graph-layout.js packs and bundles exactly as web/ui/vendor/d3.min.js", () => {
  const d3 = createRequire(import.meta.url)(join(root, "web", "ui", "vendor", "d3.min.js"));
  const m = (i) => ({ id: `m${i}`, type: "member" });
  const tree = { id: "t", children: [{ id: "g", children: [m(1), m(2)] }, { id: "s1", children: [m(3), m(4), m(5)] }, { id: "s2", children: Array.from({ length: 9 }, (_, i) => m(10 + i)) }, { id: "s3", children: [m(30)] }] };
  const pad = (n) => (n.depth === 0 ? 1.2 : 0.6);
  const want = d3.pack().radius(() => 1).padding(pad)(d3.hierarchy(tree).sum((d) => (d.type === "member" ? 1 : 0)));
  const got = pack(hierarchy(tree), () => 1, pad);
  assert.deepEqual(got.descendants().map((n) => [n.data.id, n.x, n.y, n.r]), want.descendants().map((n) => [n.data.id, n.x, n.y, n.r]));
  const cmds = [];
  const ctx = { moveTo: (x, y) => cmds.push(["M", x, y]), lineTo: (x, y) => cmds.push(["L", x, y]), bezierCurveTo: (...v) => cmds.push(["C", ...v]), closePath() {} };
  const [a, b] = [want.leaves()[0], want.leaves().at(-1)];
  d3.line().curve(d3.curveBundle.beta(0.85)).x((d) => d.x).y((d) => d.y).context(ctx)(a.path(b));
  assert.deepEqual(bundle(got.leaves()[0].path(got.leaves().at(-1)), 0.85), cmds);
});

test("the graph shows a team and its lane as members and links: the busier sender first, briefs in flight, the run's counts", () => {
  const m = graphModel(slices, "a1b2c3d4/ah2-tm4");
  assert.deepEqual(m.tabs.map((t) => t.name), ["engram-rc", "ah2-tm4"]);
  const [rc4, lane] = m.groups;
  assert.deepEqual([rc4.depth, lane.name, lane.depth], [0, "lane-herdr", 1]);
  assert.deepEqual(rc4.members.map((x) => `${x.glyph} ${x.name}`), ["● orchestrator", "● implementor", "❚❚ reviewer", "○ architect"]);
  assert.deepEqual(rc4.links.map((l) => `${l.a} ${l.arrow} ${l.b} ${l.n} ${l.inFlight}`.trim()),
    ["orchestrator ⇄ implementor 17 1 in flight", "implementor ⇄ reviewer 7 1 in flight, review", "orchestrator ⇄ architect 6", "orchestrator → reviewer 1"]);
  assert.equal(rc4.overflow, 3);
  // the Map: the members in their stream's circle, inside the square; an arc per pair and kind; a mark per brief in flight
  const { size, containers, leaves, arcs, flights } = rc4.map;
  assert.deepEqual(containers.map((c) => c.name), ["main"]);
  assert.ok(leaves.every((n) => Math.hypot(n.x - containers[0].x, n.y - containers[0].y) + n.r <= containers[0].r && n.x - n.r >= 0 && n.x + n.r <= size));
  assert.deepEqual([leaves.length, arcs.length, flights.length], [4, 7, 2]);
  assert.ok(graphSubs(slices).includes("flow:a1b2c3d4/lane-herdr"));
});

test("with no team named the first tab shows; a team that stopped running reads closed", () => {
  const first = graphModel(slices);
  assert.equal(first.id, "9f8e7d6c/engram-rc");
  assert.deepEqual([first.groups[0].run, first.groups[0].links.length], [false, 0]);
  assert.equal(graphModel(slices, "a1b2c3d4/old-spike").closed, true);
});

test("a closed team adds no graph subs; a running one does", () => {
  const subs = graphSubs(slices);
  assert.ok(!subs.some((s) => s.includes("old-spike")));
  assert.ok(subs.includes("team:a1b2c3d4/ah2-tm4"));
});

test("the scrubber: a 30 min window counts only its messages, fades the arcs outside it, and steps by the minute; a stream circle zooms", () => {
  const now = Date.parse(reply.observed_at);
  const live = graphModel(slices, "a1b2c3d4/ah2-tm4", 540, {}, now);
  assert.deepEqual([live.scrub.live, live.scrub.knob, live.scrub.say], [true, KNOB, "now · 40 messages"]);
  assert.ok(live.groups[0].map.arcs.every((a) => a.fade === null));
  const back = graphModel(slices, "a1b2c3d4/ah2-tm4", 540, { graphWindow: "30", graphCursor: now - 90 * 60000 }, now);
  const [rc4] = back.groups;
  assert.equal(rc4.windowed, true);
  assert.equal(rc4.links.reduce((n, l) => n + l.n, 0), rc4.count);
  assert.ok(rc4.count < 31 && rc4.map.arcs.some((a) => a.fade === FAINT) && rc4.links.every((l) => l.flight === 0));
  assert.ok(back.scrub.bars.some((b) => b.in) && back.scrub.bars.some((b) => !b.in));
  assert.equal(stepCursor(back.scrub, 1), back.scrub.cursorMs + 60000);
  assert.equal(stepCursor(live.scrub, 1), null);
  assert.equal(cursorAt(live.scrub, KNOB), null);
  // a second stream: the team's map shows both circles, either zooms in alone, and the crumb walks back out
  const two = new Map(slices);
  const arch = "member:a1b2c3d4/ah2-tm4/architect";
  two.set(arch, { ...two.get(arch), body: { ...two.get(arch).body, placement: { stream: "design" } } });
  const whole = graphModel(two, "a1b2c3d4/ah2-tm4", 540, {}, now).groups[0];
  assert.deepEqual(whole.map.containers.map((c) => [c.name, c.zoom]), [["design", true], ["main", true]]);
  const z = graphModel(two, "a1b2c3d4/ah2-tm4", 540, { graphZoom: { team: "a1b2c3d4/ah2-tm4", node: "stream:main" } }, now);
  assert.deepEqual([z.groups.length, z.crumb, z.groups[0].map.leaves.length], [1, ["ah2-tm4", "main"], 3]);
  assert.ok(z.groups[0].links.every((l) => l.a !== "architect" && l.b !== "architect"));
});

test("a scrub keeps the graph subs, so the poll cursor is kept: a page already fetched stays subscribed", () => {
  const now = Date.parse(reply.observed_at);
  const s = new Map(slices);
  const id = "flow:a1b2c3d4/ah2-tm4";
  const head = { ...s.get(id).body, page_size: 1, pages: [100, 400] };
  s.set(id, { ...s.get(id), body: head });
  const back = graphSubs(s, { graphWindow: "30", graphCursor: head.start_ms + 30 * 60000 }, now);
  const pages = back.filter((x) => x.startsWith("flow-page:a1b2c3d4/ah2-tm4/"));
  assert.equal(pages.length, 2);
  assert.ok(!graphSubs(s, { graphWindow: "30" }, now).some((x) => pages.includes(x)));
  pages.forEach((p, k) => s.set(p, { id: p, body: { events: [[head.pages[k], 0, 1, 0]] } }));
  assert.deepEqual(graphSubs(s, { graphWindow: "30" }, now + 60000), back);
});

test("the Graph's scale clamps at both ends, fit fills the frame, and a drill in or out goes back to fit", () => {
  const now = Date.parse(reply.observed_at);
  const team = "a1b2c3d4/ah2-tm4";
  const ui = {};
  drillTo(ui, null);
  const groups = (s) => graphModel(slices, team, 540 * s, ui, now).groups;
  const g0 = groups(1);
  assert.equal(g0[0].map.size, 540);
  // a step inside the range moves; past either end the scale stops there, and a further step is refused
  assert.ok(rescaleTo(ui, g0, 1 * SCALE_STEP));
  assert.equal(ui.graphScale, 1.25);
  assert.ok(rescaleTo(ui, groups(ui.graphScale), 100));
  assert.equal(ui.graphScale, SCALE_MAX);
  assert.equal(rescaleTo(ui, groups(ui.graphScale), SCALE_MAX * SCALE_STEP), false);
  assert.equal(ui.graphScale, SCALE_MAX);
  assert.ok(rescaleTo(ui, groups(ui.graphScale), 0.001));
  assert.equal(ui.graphScale, SCALE_MIN);
  assert.equal(rescaleTo(ui, groups(ui.graphScale), SCALE_MIN / SCALE_STEP), false);
  assert.equal(clampScale(SCALE_MIN + 0.01), SCALE_MIN + 0.01);
  // smaller than its frame the map sits centred; larger, it cannot leave a gap at an edge, and a point off the edge is kept inside
  assert.deepEqual(clampPan({ x: 0, y: 0 }, 540, 0.5), { x: 135, y: 135 });
  assert.deepEqual(clampPan({ x: 50, y: -5000 }, 540, 2), { x: 0, y: -540 });
  assert.deepEqual(clampPan({ x: -10, y: -530 }, 540, 2), { x: -10, y: -530 });
  // a wheel zoom about a point keeps the map point under it in place
  const p = panAround({ x: -100, y: -200 }, 540, 2, 3, 300, 100);
  assert.deepEqual([(300 - p.x) / 3, (100 - p.y) / 3], [(300 + 100) / 2, (100 + 200) / 2]);
  // fit: scale 1, the map laid out at the frame's size and drawn at its corner
  rescaleTo(ui, groups(ui.graphScale), 2, { id: team, x: 10, y: 10 });
  assert.notDeepEqual(ui.graphPan[team], clampPan(undefined, 540, 2));
  fitView(ui);
  assert.equal(ui.graphScale, 1);
  assert.deepEqual(clampPan(ui.graphPan[team], 540, ui.graphScale), { x: 0, y: 0 });
  assert.equal(groups(ui.graphScale)[0].map.size, 540);
  // a drill in, then out, each start back at fit
  for (const z of [{ team, node: "stream:main" }, null]) {
    rescaleTo(ui, groups(ui.graphScale), 3);
    ui.graphPan[team] = { x: -40, y: -40 };
    drillTo(ui, z);
    assert.deepEqual([ui.graphZoom, ui.graphScale, ui.graphPan], [z, 1, {}]);
  }
});

test("a zoomed map fills the view's room, not its square, and pans to its far corner", () => {
  const room = { w: 900, h: 700 };
  assert.deepEqual(frameOf(540, 4, room), { w: 900, h: 700 });
  assert.deepEqual(frameOf(540, 4, room, 14), { w: 886, h: 700 });
  // not zoomed in, or a room smaller than the square, or none measured yet: the square
  assert.deepEqual(frameOf(540, 1, room), { w: 540, h: 540 });
  assert.deepEqual(frameOf(540, 4, { w: 300, h: 300 }), { w: 540, h: 540 });
  assert.deepEqual(frameOf(540, 4, undefined), { w: 540, h: 540 });
  // the 2160 px map pans until its far corner meets the box's, not the square's
  assert.deepEqual(clampPan({ x: -9999, y: -9999 }, 540, 4, room), { x: 900 - 2160, y: 700 - 2160 });
  // growing from the square into the room keeps the point under the pointer (the box widens about its centre)
  const near = { w: 600, h: 600 };
  const b1 = frameOf(540, 1, near);
  const b2 = frameOf(540, 2, near);
  const p = panAround({ x: 0, y: 0 }, 540, 1, 2, 400, 400, b1, b2);
  assert.deepEqual([(400 + (b2.w - b1.w) / 2 - p.x) / 2, (400 - p.y) / 2], [400, 400]);
});

// ---- Flow (graph-model.js flowModel over flow-model.js, the web Flow's own maths)

const FM = await import("../../herdr-desktop/mods/ah2/graph-model.js");
const F = await import("../../herdr-desktop/mods/ah2/flow-model.js");
const SEL = await import("../../herdr-desktop/mods/ah2/select.js");
const TM4 = "a1b2c3d4/ah2-tm4";
const WIDE = { w: 1400, h: 700 };
const inside = (r, b) => r.x >= b.x && r.y >= b.y && r.x + r.w <= b.x + b.w && r.y + r.h <= b.y + b.h;

test("Flow draws one card per member of the shown team; a lane's members sit inside their band", () => {
  const m = FM.flowModel(slices, TM4, "horizontal", 1, WIDE);
  const ids = SEL.memberIds(slices.get(`team:${TM4}`));
  assert.deepEqual(m.cards.map((c) => c.id).sort(), [...ids].sort());
  assert.equal(new Set(m.cards.map((c) => c.id)).size, m.cards.length);
  // the architect leads an open lane whose child team is lane-herdr
  const laned = new Map(slices);
  const t = laned.get(`team:${TM4}`);
  laned.set(t.id, { ...t, body: { ...t.body, streams: [{ state: "open", child_team: "lane-herdr", sub_orchestrator: "architect" }] } });
  for (const orient of ["horizontal", "vertical"]) {
    const l = FM.flowModel(laned, TM4, orient, 1, WIDE);
    assert.equal(l.bands.length, 1, orient);
    const kids = slices.get("team:a1b2c3d4/lane-herdr").body.members;
    for (const id of [`member:${TM4}/architect`, ...kids]) {
      const c = l.cards.find((x) => x.id === id);
      assert.ok(c && inside(c.rect, l.bands[0]), `${id} inside the band, ${orient}`);
    }
  }
});

test("Flow's links are the reporting lines and the first 24 briefs in flight, one per pair, a brief's kind winning over the line", () => {
  const m = FM.flowModel(slices, TM4, "horizontal", 1, WIDE);
  const team = slices.get(`team:${TM4}`);
  const edges = slices.get(`topology:${TM4}`).body.edges.parent_child;
  const sparks = SEL.sparksOf(slices, team);
  assert.ok(edges.length > 0 && sparks.length > 0, "the fixture has both");
  const carded = new Set(m.cards.map((c) => c.id));
  const kinds = F.sparkKinds(sparks);
  const want = new Map();
  for (const [a, b] of [...edges.map((e) => [e.from, e.to]), ...sparks.slice(0, 24).map((s) => [s.from, s.to])]) {
    if (carded.has(a) && carded.has(b)) want.set(F.pairKey(a, b), kinds.get(F.pairKey(a, b)) ?? "edge");
  }
  assert.deepEqual(new Map(m.links.map((l) => [l.key, l.kind])), want);
  assert.ok(m.links.some((l) => l.kind !== "edge" && edges.some((e) => F.pairKey(e.from, e.to) === l.key)), "a pair that is both is drawn once, as the brief");
  // more than MAX_SPARKS: 24 drawn, the rest counted; an end with no card drops its link and dot
  const rects = new Map([["a", { x: 0, y: 0, w: 10, h: 10 }], ["b", { x: 100, y: 0, w: 10, h: 10 }]]);
  const many = Array.from({ length: 30 }, (_, i) => ({ id: `s${i}`, from: "a", to: i < 29 ? "b" : "ghost", slug: `x${i}`, review: i === 3 }));
  const fl = F.flowLinks([{ from: "a", to: "b" }], many, rects, "horizontal");
  assert.equal(fl.dots.length, 24);
  assert.equal(fl.rest, 6);
  assert.deepEqual(fl.links.map((l) => [l.key, l.kind]), [["a|b", "review"]]);
});

test("Flow's drawn scale is the fit times the scale, clamped, and every rect is the world rect × k from the box's corner", () => {
  for (const [scale, orient, room] of [[1, "horizontal", WIDE], [2, "vertical", { w: 420, h: 600 }], [4, "horizontal", { w: 300, h: 300 }], [0.25, "vertical", WIDE]]) {
    const m = FM.flowModel(slices, TM4, orient, scale, room);
    const team = slices.get(`team:${TM4}`);
    const ids = SEL.memberIds(team);
    const nodes = F.flowNodes(slices, team, ids, SEL.laneChildren(slices, team, ids));
    const lay = F.flowLayout(nodes, F.flowActive(slices, team, nodes), orient);
    const stage = { w: room.w, h: room.h - FM.LEGEND_H };
    const k = FM.clampScale(F.fitView(lay.box, stage.w, stage.h).k * scale);
    assert.equal(m.k, k);
    for (const c of m.cards) {
      const r = lay.pos.get(c.id);
      assert.deepEqual(c.rect, { x: (r.x - lay.box.x) * k, y: (r.y - lay.box.y) * k, w: r.w * k, h: r.h * k });
    }
  }
  assert.equal(FM.flowModel(slices, TM4, null, 1, null).stage.w, FM.MAP_PX, "before the first measure the stage is the map's square");
});

test("the stored Map | Flow choice: Flow only when stored so; an orientation only when stored so, else the stage's shape picks it", () => {
  for (const stored of [undefined, null, {}, "flow", 7, { mode: "radial" }, { mode: "FLOW" }]) assert.equal(FM.graphChoice(stored).mode, "map", JSON.stringify(stored));
  assert.deepEqual(FM.graphChoice({ mode: "flow", orient: "vertical" }), { mode: "flow", orient: "vertical" });
  assert.deepEqual(FM.graphChoice({ mode: "flow", orient: "diagonal" }), { mode: "flow", orient: null });
  assert.deepEqual(FM.graphChoice({ mode: "map", orient: "horizontal" }), { mode: "map", orient: "horizontal" });
  // nothing stored: a narrow stage opens vertical, a wide one horizontal; a stored orientation is kept whatever the shape
  assert.equal(FM.flowModel(slices, TM4, null, 1, { w: 420, h: 700 }).orient, "vertical");
  assert.equal(FM.flowModel(slices, TM4, null, 1, { w: 420, h: 700 }).picked, "vertical");
  assert.equal(FM.flowModel(slices, TM4, null, 1, { w: 1600, h: 700 }).orient, "horizontal");
  assert.equal(FM.flowModel(slices, TM4, "horizontal", 1, { w: 420, h: 700 }).orient, "horizontal");
  assert.equal(FM.flowModel(slices, TM4, "horizontal", 1, { w: 420, h: 700 }).picked, null);
  assert.equal(FM.flowModel(slices, TM4, "vertical", 1, { w: 1600, h: 700 }).orient, "vertical");
});

test("Flow's pan: smaller content centred across and at the top; larger content clamped at both edges; a zoom keeps the point under it", () => {
  const stage = { w: 400, h: 300 };
  assert.deepEqual(FM.flowPan({ x: -50, y: 40 }, { w: 200, h: 100 }, stage), { x: 100, y: 0 });
  const big = { w: 800, h: 600 };
  assert.deepEqual(FM.flowPan(undefined, big, stage), { x: -200, y: 0 });
  assert.deepEqual(FM.flowPan({ x: 50, y: 50 }, big, stage), { x: 0, y: 0 });
  assert.deepEqual(FM.flowPan({ x: -900, y: -900 }, big, stage), { x: -400, y: -300 });
  const p = { x: -100, y: -100 };
  const [px, py] = [150, 120];
  const q = FM.flowPanAround(p, big, stage, 1, 1.25, px, py);
  assert.deepEqual([(px - q.x) / 1.25, (py - q.y) / 1.25], [px - p.x, py - p.y], "the content point under (px, py) stays there");
});

test("Flow's frame: a team not arrived is pending, a stopped one closed, an empty one has no stage and no throw", () => {
  const noTeam = new Map([...slices].filter(([id]) => id !== `team:${TM4}`));
  assert.equal(FM.flowModel(noTeam, TM4, null, 1, WIDE).pending, true);
  assert.equal(FM.flowModel(slices, "a1b2c3d4/gone-team", null, 1, WIDE).closed, true);
  const empty = new Map(slices);
  const t = empty.get(`team:${TM4}`);
  empty.set(t.id, { ...t, body: { ...t.body, orchestrator: null, members: [] } });
  const m = FM.flowModel(empty, TM4, null, 1, WIDE);
  assert.equal(m.cards.length, 0, "the view shows Members 0 / None");
  assert.deepEqual([m.links.length, m.dots.length, m.bands.length], [0, 0, 0]);
});

test("the wheel: every scroll zooms, a line delta or a trackpad's pixel delta with no key, at the web's factors; never a pan", () => {
  const none = { control: false, platform: false };
  assert.deepEqual(wheelAct({ delta: { x: 0, y: 40 }, delta_lines: { x: 0, y: 2 }, modifiers: none }), { zoom: Math.exp(2 * 0.05) });
  assert.deepEqual(wheelAct({ delta: { x: 3, y: -12 }, modifiers: none }), { zoom: Math.exp(-12 * 0.002) });
  assert.deepEqual(wheelAct({ delta: { x: 0, y: 30 }, modifiers: none }), { zoom: Math.exp(30 * 0.002) }, "up zooms in");
  assert.deepEqual(wheelAct({ delta: { x: 0, y: -50 }, modifiers: { control: true, platform: false } }), { zoom: Math.exp(-50 * 0.002) });
  assert.deepEqual(wheelAct({ delta: { x: 0, y: 30 }, modifiers: { control: false, platform: true } }), { zoom: Math.exp(30 * 0.002) });
  assert.equal(wheelAct({ delta: { x: 25, y: 0 }, modifiers: none }), null, "a horizontal scroll is ignored");
  assert.equal(wheelAct({ delta: { x: 0, y: 0 }, modifiers: none }), null);
  for (const e of [{ delta: { x: 7, y: 9 }, modifiers: none }, { delta: { x: 0, y: 1 }, delta_lines: { x: 0, y: 1 }, modifiers: none }]) {
    assert.equal(wheelAct(e).pan, undefined, "a scroll never pans");
  }
});

test("the drag: no pan under DRAG_PX, a clamped pan from there on, and its release is not a click", () => {
  const clamp = (p) => clampPan(p, 100, 2); // a 200 px map in a 100 px frame: pan x, y in -100..0
  const press = { x0: 50, y0: 50, x: 50, y: 50, dragged: false };
  assert.deepEqual(dragAct(press, { x: 52, y: 52 }, { x: -50, y: -50 }, clamp), { dragged: false, pan: null }, "2.8 px is a click");
  assert.deepEqual(dragAct(press, { x: 50 + DRAG_PX, y: 50 }, { x: -50, y: -50 }, clamp), { dragged: true, pan: { x: -46, y: -50 } });
  assert.deepEqual(dragAct(press, { x: 90, y: 20 }, { x: -50, y: -50 }, clamp), { dragged: true, pan: { x: -10, y: -80 } });
  assert.deepEqual(dragAct(press, { x: 200, y: -200 }, { x: -50, y: -50 }, clamp), { dragged: true, pan: { x: 0, y: -100 } }, "clamped");
  const held = { x0: 50, y0: 50, x: 80, y: 80, dragged: true };
  assert.deepEqual(dragAct(held, { x: 81, y: 80 }, { x: -20, y: -20 }, clamp), { dragged: true, pan: { x: -19, y: -20 } }, "a drag stays one back under DRAG_PX");
  const fits = (p) => clampPan(p, 100, 0.5); // a map smaller than its frame cannot be dragged out of place
  assert.deepEqual(dragAct(press, { x: 90, y: 10 }, { x: 25, y: 25 }, fits).pan, { x: 25, y: 25 });
  assert.equal(wasDrag({ dragged: true, live: true, end: 0 }, 1000), true);
  assert.equal(wasDrag({ dragged: true, live: false, end: 1000 }, 1000 + CLICK_MS - 1), true, "the release of a drag does not click");
  assert.equal(wasDrag({ dragged: true, live: false, end: 1000 }, 1000 + CLICK_MS), false, "a key press later clicks");
  assert.equal(wasDrag({ dragged: false, live: false, end: 1000 }, 1001), false, "a click is a click");
  assert.equal(wasDrag(undefined, 1), false);
  const ui = { graphDrag: { dragged: true, live: true, end: 0 } };
  fitView(ui);
  assert.equal(wasDrag(ui.graphDrag, 1), false, "a drill, mode or team switch ends a drag whose stage may be gone");
});

test("the eased scale leaves its start, moves one way only, and lands exactly on the target at EASE_MS", () => {
  const at = (ms) => easedScale(1, 2, 1000, 1000 + ms);
  assert.equal(at(0), 1);
  const steps = [40, 100, 200, 300, 399].map(at);
  steps.reduce((prev, s) => (assert.ok(s > prev && s < 2), s), 1);
  assert.ok(at(100) - 1 > 0.5, "ease-out: past half way within a quarter of the time");
  assert.equal(at(EASE_MS), 2);
  assert.equal(at(EASE_MS * 3), 2);
  for (let ms = 0; ms <= EASE_MS; ms += 10) {
    const s = easedScale(SCALE_MAX, SCALE_MIN, 0, ms);
    assert.ok(s >= SCALE_MIN && s <= SCALE_MAX, `${s} at ${ms} ms`);
  }
});

// ah2-138: a wheel's ease has no slow start, so a turning wheel keeps up with the hand; it lands exactly at WHEEL_EASE_MS.
test("the wheel's ease is fastest at its start, shrinks every frame, and lands exactly on the target at WHEEL_EASE_MS", () => {
  const at = (ms) => easedScale(1, 2, 1000, 1000 + ms, true);
  assert.equal(at(0), 1);
  const steps = [];
  for (let ms = 8; ms <= WHEEL_EASE_MS; ms += 8) steps.push(at(ms) - at(ms - 8));
  steps.reduce((prev, d) => (assert.ok(d > 0 && d < prev, `step ${d} after ${prev}`), d), Infinity);
  assert.equal(at(WHEEL_EASE_MS), 2);
  assert.equal(at(WHEEL_EASE_MS * 3), 2);
  assert.ok(at(WHEEL_EASE_MS - 1) < 2, "never past the target");
  assert.ok(WHEEL_EASE_MS < EASE_MS);
  assert.equal(easedScale(1, 2, 1000, 1100), 1 + spring(100 / EASE_MS), "a button keeps the web's curve");
});

// ah2-103: a Graph member opens its pane by the sidebar's path, so the Map leaf and the Flow card carry the pane the sidebar's row does.
test("the Map leaf and the Flow card carry the member's pane when it has one, none when it does not", () => {
  const panes = (list) => Object.fromEntries(list.map((x) => [x.id.split("/").pop(), x.pane]));
  const want = { "@orchestrator": "t1", implementor: null, reviewer: null, architect: null };
  assert.deepEqual(panes(graphModel(slices, TM4).groups[0].map.leaves), want);
  assert.deepEqual(panes(FM.flowModel(slices, TM4, "horizontal", 1, WIDE).cards), want);
  // a lane's member reads its lane's summary; a proven-dead member has none
  const laned = new Map(slices);
  const t = laned.get(`team:${TM4}`);
  laned.set(t.id, { ...t, body: { ...t.body, streams: [{ state: "open", child_team: "lane-herdr", sub_orchestrator: "architect" }] } });
  const ls = laned.get("team-summary:a1b2c3d4/lane-herdr");
  const arch = "member:a1b2c3d4/lane-herdr/architect";
  laned.set(ls.id, { ...ls, body: { ...ls.body, member_states: { ...ls.body.member_states, [arch]: { ...ls.body.member_states[arch], pane_id: "t8" } } } });
  assert.equal(FM.flowModel(laned, TM4, "horizontal", 1, WIDE).cards.find((c) => c.id === arch).pane, "t8");
  const dead = new Map(slices);
  const sum = dead.get("team-summary:9f8e7d6c/engram-rc");
  const ms = sum.body.member_states;
  dead.set(sum.id, { ...sum, body: { ...sum.body, member_states: { ...ms, "member:9f8e7d6c/engram-rc/implementor": { ...ms["member:9f8e7d6c/engram-rc/implementor"], pane_id: "t9" } } } });
  assert.equal(graphModel(dead, "9f8e7d6c/engram-rc").groups[0].map.leaves.find((n) => n.id.endsWith("/implementor")).pane, null);
});
