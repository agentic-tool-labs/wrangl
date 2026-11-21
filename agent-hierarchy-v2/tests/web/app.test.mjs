import { test, afterEach } from "node:test";
import assert from "node:assert/strict";
import { installDom, countRenders, settle } from "./dom.mjs";

// The page's globals first: a DOM, a counted requestAnimationFrame, and a captured setInterval (the age ticker's).
installDom();
const raf = { calls: 0 };
globalThis.requestAnimationFrame = () => {
  raf.calls += 1;
  return 0;
};
const intervals = [];
globalThis.setInterval = (fn, ms) => {
  intervals.push({ fn, ms });
  return intervals.length;
};
globalThis.clearInterval = () => {};
// d3 as the page's classic script leaves it, so Map draws at once.
globalThis.d3 = (await import("node:module")).createRequire(import.meta.url)("../../web/ui/vendor/d3.min.js");

const { createMock } = await import("../../web/mock/model.mjs");
const { start } = await import("../../web/ui/app.js");
const { render, html } = await import("../../web/ui/h.js");
const { Rows, windowOf, VIRTUAL_OVER } = await import("../../web/ui/rows.js");
const { MAX_SPARKS } = await import("../../web/ui/graph-world.js");
const { GraphStage } = await import("../../web/ui/app.js");
const { flowLayout } = await import("../../web/ui/graph.js");

const wire = (v) => JSON.parse(JSON.stringify(v));
const until = async (pred, what, ms = 3000) => {
  const end = Date.now() + ms;
  while (!pred()) {
    assert.ok(Date.now() < end, `timed out waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 5));
  }
};
const text = (el) => el.textContent;
const find = (root, sel) => root.all(sel);
const click = (el) => el.fire("click");

const mounts = [];
afterEach(() => {
  for (const m of mounts.splice(0)) {
    m.store.stop();
    render(null, m.root);
  }
});

// Mounts the whole page on a mock model, through the same start() the page uses.
// Flow is the layout whose rail is still the feed, and whose cards are Live components of their own; Map has its own mount below.
async function mount({ mode = "flow" } = {}) {
  const model = createMock();
  const get = async (path, params = {}, signal) => {
    if (path === "/events") {
      return wire(await model.poll({ since: Number(params.since), epoch: params.epoch, sub: params.sub ? params.sub.split(",") : null, fresh: params.fresh ? params.fresh.split(",") : [], wait: 0.03, signal }));
    }
    return wire(model.list(path.slice("/list/".length), { after: params.after ?? null, before: params.before ?? null, limit: params.limit }));
  };
  get.manifest = async (name) => wire(model.manifest(name));
  const root = installDom();
  const store = start(get, root, true);
  store.setUi({ view: "graph", graphMode: mode });
  const m = { model, store, root };
  mounts.push(m);
  await until(() => store.state.ready && find(root, mode === "flow" ? "div.fcard" : "g.gm-leaf").length === 8, "the graph");
  mode === "flow" && (await until(() => find(root, "li.feed-row").length === 10, "the feed"));
  await settle();
  return m;
}

test("the page shows MOCK DATA, the header, 8 Flow cards and the feed", async () => {
  const { root } = await mount();
  assert.match(text(find(root, ".mock-banner")[0]), /MOCK DATA/);
  assert.equal(find(root, "div.fcard").length, 8);
  assert.match(text(find(root, ".switch-btn")[0]), /^ship/);
  assert.equal(find(root, "li.feed-row").length, 10);
  assert.equal(find(root, "circle.dot").length, 3, "one dot per open exchange in the open list whose addressee is not gone (the fixture has 4 open, one to the gone member)");
});

test("the header has no accent swatches, no perf strip and no worktrees chip", async () => {
  const { root } = await mount();
  assert.equal(find(root, ".swatch").length, 0);
  assert.equal(find(root, ".perf-strip").length, 0);
  assert.equal(find(root, ".chip.worktrees").length, 0);
});

// With nothing selected the Map's rail is the live feed, which an activity change does not redraw (C2).
test("C1: one member's activity change draws the Map once and the status bar's state counts, and nothing else", async () => {
  const { root, model, store } = await mount({ mode: "map" });
  const counter = countRenders();
  const seq = store.state.seq;
  model.tick();
  await until(() => store.state.seq > seq, "the change");
  await settle();
  const drawn = Object.fromEntries([...counter.counts].sort());
  counter.stop();
  assert.deepEqual(drawn, { Footer: 1, GraphMap: 1, S: 1 });
  assert.equal(find(root, "g.gm-leaf").length, 8);
});

test("C2: an activity change leaves the feed's rows as the same elements and draws no feed row", async () => {
  const { root, model, store } = await mount();
  const rows = find(root, "li.feed-row");
  const counter = countRenders();
  const seq = store.state.seq;
  model.tick();
  await until(() => store.state.seq > seq, "the change");
  await settle();
  const drawn = counter.counts.get("FeedRow") ?? 0;
  counter.stop();
  assert.equal(drawn, 0, "no feed row drew");
  const after = find(root, "li.feed-row");
  assert.equal(after.length, rows.length);
  after.forEach((li, i) => assert.equal(li, rows[i], `row ${i} kept its element`));
});

test("C1: an activity change of a selected member draws the Map and its panel once each, and nothing more", async () => {
  const { root, model, store } = await mount({ mode: "map" });
  const architect = "member:a1b2c3d4/ship/architect";
  store.setUi({ selected: architect });
  await until(() => find(root, "section.msum").length === 1, "the member summary");
  await settle();
  const counter = countRenders();
  const seq = store.state.seq;
  model.tick();
  await until(() => store.state.seq > seq, "the change");
  await settle();
  const drawn = Object.fromEntries([...counter.counts].sort());
  counter.stop();
  // GraphPanel adds the window counts to the summary; S is the two Age tickers (since, tool), once each.
  assert.deepEqual(drawn, { Footer: 1, GraphMap: 1, GraphPanel: 1, MemberHead: 1, MemberNow: 1, MemberSummary: 1, S: 2 });
});

test("C1: selecting a member draws that card only; the other 7 cards' props did not change", async () => {
  const { root, store } = await mount();
  const counter = countRenders();
  store.setUi({ selected: "member:a1b2c3d4/ship/reviewer" });
  await until(() => find(root, "section.panel").length === 1, "the panel");
  await settle();
  const nodes = counter.counts.get("FlowCard") ?? 0;
  counter.stop();
  assert.equal(nodes, 1, `${nodes} cards drew`);
});

test("C1b: the age tick draws no component", async () => {
  const before = intervals.length;
  const { root } = await mount();
  assert.equal(intervals.length - before, 1, "one timer for the whole page");
  const tick = intervals.at(-1);
  assert.equal(tick.ms, 1000);
  const counter = countRenders();
  assert.ok(find(root, "span.age").length >= 1, "ages are on screen");
  tick.fn();
  tick.fn();
  await settle();
  assert.equal(counter.total(), 0);
  counter.stop();
});

test("C7: at steady state no animation frame is requested and no second timer exists", async () => {
  const { store } = await mount();
  const seq = store.state.seq;
  raf.calls = 0;
  const timers = intervals.length;
  await new Promise((r) => setTimeout(r, 200));
  assert.equal(raf.calls, 0, "no frame requested while idle");
  assert.equal(store.state.seq, seq, "nothing changed");
  assert.equal(raf.calls, 0);
  assert.equal(intervals.length, timers);
});

const moveFeed = (model, store, ids, body) => {
  model.publish([...body, { id: "list:feed:team:a1b2c3d4/ship", as_of: Date.now(), body: { team: "team:a1b2c3d4/ship", ids } }]);
};

test("C2: a feed row keeps its element when a new row arrives above it (keys are ids, not indexes)", async () => {
  const { root, model, store } = await mount();
  const rowsBefore = new Map(find(root, "li.feed-row").map((li) => [text(find(li, ".slug")[0]), li]));
  const ids = model.list("list:feed:team:a1b2c3d4/ship", { limit: 200 }).rows.map((r) => r.id.split("/").pop());
  moveFeed(model, store, ["20261018-0200-zz99", ...ids], [
    { id: "exchange:a1b2c3d4/20261018-0200-zz99", as_of: Date.now(), body: { dir_id: "a1b2c3d4", id: "20261018-0200-zz99", slug: "brand-new", to: "reviewer", from: "orchestrator", parent: null, created: new Date().toISOString(), open: true, has_response: false, team: "ship" } },
  ]);
  await until(() => find(root, "li.feed-row").some((li) => text(li).includes("brand-new")), "the new row");
  const rowsAfter = new Map(find(root, "li.feed-row").map((li) => [text(find(li, ".slug")[0]), li]));
  for (const [slug, li] of rowsBefore) {
    assert.equal(rowsAfter.get(slug), li, `${slug} is the same element`);
  }
});

test("C3: a 1,000-row list mounts at most a window plus overscan rows", () => {
  const root = installDom();
  const rows = Array.from({ length: 1000 }, (_, i) => ({ id: `r${i}` }));
  render(html`<${Rows} cls="l" rows=${rows} keyOf=${(r) => r.id} row=${(r) => html`<li class="x">${r.id}</li>`} />`, root);
  const [from, to] = windowOf(1000, 0);
  assert.equal(find(root, "li.x").length, to - from);
  assert.ok(find(root, "li.x").length <= 30, `${find(root, "li.x").length} rows mounted`);
  assert.ok(1000 > VIRTUAL_OVER);
  render(null, root);
});

test("C3: a feed of 610 rows is paged as 20 and mounts at most a window plus overscan", async () => {
  const { root, store, model } = await mount();
  const ids = Array.from({ length: 600 }, (_, i) => `bulk-${String(i).padStart(4, "0")}`);
  const existing = model.list("list:feed:team:a1b2c3d4/ship", { limit: 200 }).rows.map((r) => r.id.split("/").pop());
  moveFeed(model, store, [...ids, ...existing], ids.map((id) => ({ id: `exchange:a1b2c3d4/${id}`, as_of: Date.now(), body: { dir_id: "a1b2c3d4", id, slug: id, to: "reviewer", from: "orchestrator", parent: null, created: new Date().toISOString(), open: false, has_response: true, team: "ship" } })));
  await until(() => find(root, "li.feed-row").some((li) => text(li).includes("bulk-0000")), "the new rows");
  assert.ok(find(root, "li.feed-row").length <= 20, `${find(root, "li.feed-row").length} rows mounted`);
});

test("the Pause motion toggle puts motion-off on the body and back", async () => {
  const { root } = await mount();
  const pause = async () => {
    click(find(root, "button.more-btn")[0]);
    await until(() => find(root, "ul.menu").length === 1, "the menu");
    await settle();
    click(find(root, "ul.menu").flatMap((u) => u.all("button")).find((b) => text(b) === "Pause motion"));
    await settle();
  };
  await pause();
  assert.match(document.body.attrs.get("class") ?? "", /motion-off/);
  await pause();
  assert.doesNotMatch(document.body.attrs.get("class") ?? "", /motion-off/);
});

test("a click on a node selects the member and the rail shows its panel; the panel's × returns to the feed", async () => {
  const { root } = await mount();
  const node = find(root, "div.fcard").find((g) => g.attrs.get("data-id") === "member:a1b2c3d4/ship/reviewer");
  click(node);
  await until(() => find(root, "section.panel").length === 1, "the panel");
  assert.match(text(find(root, "section.panel")[0]), /reviewer/);
  assert.equal(find(root, "section.feed").length, 0);
  click(find(root, "button.close")[0]);
  await until(() => find(root, "section.feed").length === 1, "the feed back");
});

test("a tombstoned selected member closes its panel back to the feed, with a toast", async () => {
  const { root, store, model } = await mount();
  const id = "member:a1b2c3d4/ship/reviewer";
  click(find(root, "div.fcard").find((g) => g.attrs.get("data-id") === id));
  await until(() => find(root, "section.panel").length === 1, "the panel");
  const team = store.state.slices.get("team:a1b2c3d4/ship");
  model.publish([{ id: team.id, as_of: Date.now(), body: { ...team.body, members: team.body.members.filter((m) => m !== id) } }], []);
  model.remove(id);
  await until(() => find(root, "section.feed").length === 1, "back to the feed");
  assert.equal(find(root, "section.panel").length, 0);
  assert.equal(store.state.ui.selected, null);
  await until(() => find(root, ".toast").length === 1, "the toast");
  assert.equal(text(find(root, ".toast")[0]), "reviewer left the team");
  // ah2-373 R8: its fading copy (.fleave) stays for the leave transition; the card itself is gone
  assert.equal(find(root, "div.fcard").filter((g) => !(g.attrs.get("class") ?? "").includes("fleave")).length, 7, "the card left the graph");
});

// ---- the layout and the spark cap

const nodesOf = (n) => Array.from({ length: n }, (_, i) => ({ id: `m${i}`, name: `n${String(i).padStart(2, "0")}`, role: i % 3 === 0 ? "implementor" : "reviewer" }));
const star = (nodes) => nodes.slice(1).map((n) => ({ from: nodes[0].id, to: n.id }));

function stage(sparkCount, memberCount = 8) {
  const store = { state: { slices: new Map() }, subscribe: () => () => {}, lease: () => {} };
  const nodes = [{ id: "o", name: "orchestrator", role: "orchestrator" }, ...nodesOf(memberCount - 1)];
  const sparks = Array.from({ length: sparkCount }, (_, i) => ({ id: `e${i}`, from: "o", to: nodes[1 + (i % (memberCount - 1))].id, slug: `s${i}` }));
  const root = installDom();
  render(html`<${GraphStage} mode="flow" store=${store} teamId="t" nodes=${nodes} edges=${star(nodes)} sparks=${sparks} selected=${null} onSelect=${() => {}} />`, root);
  return root;
}

test("more than 24 open briefs draw 24 dots and a +N badge", () => {
  const root = stage(30);
  assert.equal(find(root, "circle.dot").length, MAX_SPARKS);
  assert.equal(text(find(root, "text.more")[0]), "+6 more briefs");
  render(null, root);
});

test("exactly 24 open briefs draw 24 dots and no badge (the nearest input that must not match)", () => {
  const root = stage(24);
  assert.equal(find(root, "circle.dot").length, 24);
  assert.equal(find(root, "text.more").length, 0);
  render(null, root);
});

test("a dot travels along its edge by an offset path, with no animated attribute", () => {
  const root = stage(2);
  const dot = find(root, "circle.dot")[0];
  const edge = find(root, "path.edge")[0];
  assert.equal(dot.style.offsetPath, `path("${edge.getAttribute("d")}")`);
  assert.match(edge.getAttribute("d"), /^M .* C /);
  assert.equal(dot.getAttribute("cx"), null);
  render(null, root);
});

test("Map draws its 8 members as leaves with no gradients", async () => {
  const { root } = await mount({ mode: "map" });
  assert.equal(find(root, "g.gm-leaf").length, 8);
  assert.equal(find(root, "radialGradient").length, 0);
});

test("reverse sparks share an edge path and missing endpoints draw nothing", () => {
  const store = { state: { slices: new Map() }, subscribe: () => () => {}, lease: () => {} };
  const nodes = [{ id: "o", name: "o", role: "orchestrator" }, { id: "a", name: "a", role: "reviewer" }, { id: "b", name: "b", role: "reviewer" }];
  const edges = [{ from: "o", to: "a" }];
  const sparks = [{ id: "reverse", from: "a", to: "o" }, { id: "unrelated", from: "o", to: "b" }, { id: "missing", from: "o", to: "absent" }];
  const root = installDom();
  render(html`<${GraphStage} mode="flow" store=${store} teamId="t" nodes=${nodes} edges=${edges} sparks=${sparks} onSelect=${() => {}} />`, root);
  const paths = find(root, "circle.dot");
  const drawn = find(root, "path.edge").map((p) => p.getAttribute("d"));
  assert.equal(paths.length, 2);
  assert.equal(drawn.length, 2, "one path for the edge and its reverse spark, one for the unrelated pair");
  assert.deepEqual(paths.map((p) => p.style.offsetPath), drawn.map((d) => `path("${d}")`));
  render(null, root);
});
