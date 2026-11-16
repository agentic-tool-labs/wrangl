// The ah2 Herdr mod's Graph data side (spec-herdr-desktop.md § View: graph) over the shipped fixture.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { graphModel, graphSubs, KNOB, FAINT, cursorAt, stepCursor, SCALE_MIN, SCALE_MAX, SCALE_STEP, clampScale, clampPan, panAround, drillTo, fitView, rescaleTo, frameOf } from "../../herdr-desktop/mods/ah2/graph-model.js";
import { hierarchy, pack, bundle } from "../../herdr-desktop/mods/ah2/graph-layout.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const mod = join(root, "herdr-desktop", "mods", "ah2");
const reply = JSON.parse(readFileSync(join(root, "herdr-desktop", "fixture", "events.json"), "utf8"));
const slices = new Map(reply.slices.map((s) => [s.id, s]));

for (const f of ["graph-world.js", "plans-model.js", "select.js"]) {
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
