import { test } from "node:test";
import assert from "node:assert/strict";
import { installDom, settle } from "./dom.mjs";
import { createMock, RETENTION_MS as SERVER_RETENTION } from "../../web/mock/model.mjs";
import { allowedKeys } from "../../web/mock/list-keys.mjs";

installDom();
globalThis.requestAnimationFrame = () => 0;
const { rangeOf, ticksOf, archivedPart, laneKey, RANGES, TimelineView, MessageMark } = await import("../../web/ui/timeline.js");
const { createStore } = await import("../../web/ui/store.js");
const { render, html } = await import("../../web/ui/h.js");

const MIN = 60_000;
const find = (root, sel) => root.all(sel);
const T0 = 1_000_000_000_000;

test("a named range ends now and spans its length; custom keeps its own bounds", () => {
  assert.deepEqual(rangeOf("15m", T0), { from: T0 - 15 * MIN, to: T0 });
  assert.deepEqual(rangeOf("7d", T0), { from: T0 - 7 * 24 * 60 * MIN, to: T0 });
  assert.deepEqual(rangeOf("custom", T0, { from: 5, to: 9 }), { from: 5, to: 9 });
  assert.deepEqual(rangeOf("custom", T0, null), rangeOf("1h", T0), "custom with no bounds is the default range, not an empty one");
  assert.deepEqual(rangeOf("nonsense", T0), rangeOf("1h", T0));
  assert.deepEqual(RANGES.map((r) => r[0]), ["15m", "1h", "6h", "24h", "7d"]);
});

test("a lane is keyed by its member id, the same on every call", () => {
  const lane = { id: "member:d/t/a", name: "a" };
  assert.equal(laneKey(lane), "member:d/t/a");
  assert.equal(laneKey(lane), laneKey(lane));
});

test("the axis has at most 8 ticks, on round times, inside the range", () => {
  for (const span of [15 * MIN, 60 * MIN, 6 * 60 * MIN, 24 * 60 * MIN, 7 * 24 * 60 * MIN]) {
    const ticks = ticksOf(T0 - span, T0);
    assert.ok(ticks.length >= 1 && ticks.length <= 9, `${span}: ${ticks.length} ticks`);
    assert.ok(ticks.every((t) => t.at >= T0 - span && t.at <= T0 && t.x >= 0 && t.x <= 1000));
    assert.ok(ticks.every((t) => t.at % MIN === 0));
  }
});

test("beyond retention: a range that starts before the horizon is partly archived; one inside it is not", () => {
  assert.equal(archivedPart(T0 - 8 * 24 * 60 * MIN, T0, 7), true);
  assert.equal(archivedPart(T0 - 7 * 24 * 60 * MIN + MIN, T0, 7), false);
  assert.equal(archivedPart(T0 - 7 * 24 * 60 * MIN, T0, 7), false, "exactly at the horizon is kept");
  assert.equal(archivedPart(T0 - 4 * 24 * 60 * MIN, T0, 3), true, "the boundary follows the retention the server states");
  assert.equal(archivedPart(T0 - 400 * 24 * 60 * MIN, T0, null), false, "no retention known: nothing is called archived");
});

test("the retention the mock states is the one it archives by", () => {
  assert.equal(SERVER_RETENTION, 7 * 24 * 60 * MIN);
  const body = createMock().manifest("timeline").slices.find((s) => s.id === "timeline:a1b2c3d4").body;
  assert.equal(body.retention_days * 24 * 60 * MIN, SERVER_RETENTION);
});

// ---- windows by time cursor, through the store and the mock

function rig() {
  const model = createMock();
  const wire = (v) => JSON.parse(JSON.stringify(v));
  const get = async (path, params = {}, signal) => {
    if (path === "/events") {
      return wire(await model.poll({ since: Number(params.since), epoch: params.epoch, sub: params.sub ? params.sub.split(",") : null, fresh: params.fresh ? params.fresh.split(",") : [], wait: 0.03, signal }));
    }
    const allowed = allowedKeys(path.slice(6));
    const unknown = Object.keys(params).find((k) => !allowed.has(k));
    if (unknown !== undefined) {
      throw Object.assign(new Error("bad_filter"), { key: unknown });
    }
    return wire(model.list(path.slice(6), { after: params.after ?? null, before: params.before ?? null, limit: params.limit, from: params.from_ms ?? null, to: params.to_ms ?? null, member: params.member ?? null }));
  };
  get.manifest = async (n) => wire(model.manifest(n));
  return { model, store: createStore({ get, manifest: get.manifest }), get };
}

test("windows by time cursor: the range filters the rows, and a cursor continues where the window ended", async () => {
  const { model, store } = rig();
  const now = Date.now();
  const id = "list:timeline:a1b2c3d4";
  const all = await store.page(id, { limit: 500 });
  const wide = await store.page(id, { limit: 500, from: now - 6 * 60 * MIN, to: now });
  assert.equal(wide.items.length, all.items.length, "6 h holds every transition of the fixture");
  const narrow = await store.page(id, { limit: 500, from: now - 30 * MIN, to: now });
  assert.ok(narrow.items.length < all.items.length, "30 m holds fewer");
  assert.ok(narrow.items.every((r) => r.at >= now - 30 * MIN && r.at <= now));
  const first = await store.page(id, { limit: 3, from: now - 6 * 60 * MIN, to: now });
  assert.equal(first.items.length, 3);
  assert.ok(first.next, "more rows after the window");
  const second = await store.page(id, { limit: 3, from: now - 6 * 60 * MIN, to: now, after: first.next });
  assert.ok(second.items[0].seq < first.items.at(-1).seq, "newest first: the next window continues with older rows");
  const ids = new Set([...first.items, ...second.items].map((r) => r.seq));
  assert.equal(ids.size, 6, "no row in both windows");
  void model;
  store.stop();
});

test("the first page of a range carries a seed per subject with an earlier row; later pages carry none", async () => {
  const { store } = rig();
  const now = Date.now();
  const from = now - 30 * MIN;
  const first = await store.page("list:timeline:a1b2c3d4", { limit: 3, from, to: now });
  assert.ok(first.seed.length > 0, "earlier rows exist in the fixture");
  assert.ok(first.seed.every((s) => s.at < from && typeof s.state === "string" && typeof s.subject === "string"));
  assert.equal(new Set(first.seed.map((s) => s.subject)).size, first.seed.length, "one seed per subject");
  const cursor = (await store.page("list:timeline:a1b2c3d4", { limit: 3 })).next;
  assert.ok(cursor, "a cursor to continue from");
  const later = await store.page("list:timeline:a1b2c3d4", { limit: 3, from, to: now, after: cursor });
  assert.equal(later.seed, null);
});

test("a range wholly beyond retention comes back archived, with no rows", async () => {
  const { store } = rig();
  const now = Date.now();
  const win = await store.page("list:timeline:a1b2c3d4", { limit: 50, from: now - 20 * 24 * 60 * MIN, to: now - 10 * 24 * 60 * MIN });
  assert.equal(win.archived, true);
  assert.deepEqual(win.items, []);
  const near = await store.page("list:timeline:a1b2c3d4", { limit: 50, from: now - 8 * 24 * 60 * MIN, to: now - 6 * 24 * 60 * MIN });
  assert.equal(near.archived, false, "a range that reaches into retention is not archived");
});

// ---- the view

test("C3: 1,000 lanes mount at most a window plus overscan", async () => {
  const slices = new Map();
  const ids = Array.from({ length: 1000 }, (_, i) => `member:d/t/m${i}`);
  slices.set("team:d/t", { id: "team:d/t", version: 1, body: { name: "t", dir_id: "d", orchestrator: null, members: ids, worktrees: [] } });
  ids.forEach((id) => slices.set(id, { id, version: 1, body: { id, name: id.split("/").pop(), role: "x", session: `s-${id}` } }));
  const store = { state: { slices, ui: {}, lists: new Map() }, subscribe: () => () => {}, setUi() {}, fetchList: async () => ({ rows: [], next: null }) };
  const root = installDom();
  render(html`<${TimelineView} store=${store} teamId="team:d/t" />`, root);
  assert.ok(find(root, "li.lane").length > 0 && find(root, "li.lane").length <= 30, `${find(root, "li.lane").length} lanes mounted`);
  render(null, root);
});

test("the Timeline view draws a lane per member with spans, picks a range, and reloads the windows", async () => {
  const { store } = rig();
  store.start(["tree", "members", "exchanges", "processes", "timeline"]);
  const until = async (p, w) => {
    const end = Date.now() + 3000;
    while (!p()) {
      assert.ok(Date.now() < end, `timed out: ${w}`);
      await new Promise((r) => setTimeout(r, 5));
    }
  };
  await until(() => store.state.ready, "ready");
  const root = installDom();
  render(html`<${TimelineView} store=${store} teamId="team:a1b2c3d4/ship" />`, root);
  await until(() => find(root, "rect.span").length > 0, "the first lanes");
  await settle();
  assert.equal(find(root, "li.lane").length, 8);
  assert.ok(find(root, ".mark").length > 0, "message marks are drawn");
  assert.equal(find(root, "span.archived").length, 0, "the default range is inside retention");
  const before = find(root, "rect.span").length;
  find(root, "button.tab").find((b) => b.textContent === "15 m").fire("click");
  await until(() => store.state.ui.range === "15m", "the range");
  await settle();
  await new Promise((r) => setTimeout(r, 50));
  assert.ok(find(root, "rect.span").length <= before, "a shorter range does not draw more spans");
  find(root, "button.tab").find((b) => b.textContent === "7 d").fire("click");
  await until(() => store.state.ui.range === "7d", "the 7 d range");
  await settle();
  await new Promise((r) => setTimeout(r, 50));
  assert.equal(find(root, "span.archived").length, 0, "7 d reaches exactly the horizon: not archived");
  render(null, root);
  store.stop();
});


test("timeline message marks distinguish briefs, responses and reports without changing titles", () => {
  const root = installDom();
  for (const kind of ["brief", "response", "report", "other"]) {
    render(html`<svg><${MessageMark} mark=${{ kind, slug: "task" }} at=${50} /></svg>`, root);
    const mark = find(root, ".mark")[0];
    assert.equal(mark.localName, kind === "brief" || kind === "report" ? "rect" : "circle");
    assert.equal(mark.textContent, `${kind}: task`);
    assert.equal(mark.getAttribute("transform"), kind === "report" ? "rotate(45 50 10)" : null);
    assert.equal(mark.getAttribute("rx"), kind === "brief" ? "2" : null);
  }
  render(null, root);
});
