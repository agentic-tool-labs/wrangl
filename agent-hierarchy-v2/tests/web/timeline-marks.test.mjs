import { test } from "node:test";
import assert from "node:assert/strict";
import { installDom } from "./dom.mjs";

installDom();
globalThis.requestAnimationFrame = () => 0;
const { TimelineView } = await import("../../web/ui/timeline.js");
const { render, html } = await import("../../web/ui/h.js");

// The Timeline on a fake store whose slices and served lanes the test moves itself.

const MIN = 60_000;
const D = "d";
const T = "t";
const TEAM_ID = `team:${D}/${T}`;
const LIST = `list:timeline-spans:${D}/${T}`;
const memberId = (n) => `member:${D}/${T}/${n}`;
const find = (root, sel) => root.all(sel);

const waitFor = async (pred, what) => {
  const end = Date.now() + 3000;
  while (!pred()) {
    assert.ok(Date.now() < end, `timed out: ${what}`);
    await new Promise((r) => setTimeout(r, 5));
  }
};
const settleMs = (ms) => new Promise((r) => setTimeout(r, ms));

const lane = (n, marks = [], extra = {}) => ({ id: memberId(n), name: n, spans: [{ state: "working", from: Date.now() - 30 * MIN, to: Date.now() }], marks, ...extra });
const mark = (slug, kind, minAgo, cls = null) => ({ at: Date.now() - minAgo * MIN, kind, slug, class: cls, key: `${kind}:${slug}` });
const page = (rows, extra = {}) => ({ rows, next: null, from_ms: Date.now() - 60 * MIN, to_ms: Date.now(), archived: false, more: false, ...extra });

function fakeStore(fetchList) {
  const slices = new Map();
  slices.set(TEAM_ID, { id: TEAM_ID, version: 1, as_of: Date.now(), body: { dir_id: D, name: T, orchestrator: memberId("a"), members: [memberId("b")] } });
  for (const n of ["a", "b"]) {
    slices.set(memberId(n), { id: memberId(n), version: 1, as_of: Date.now(), body: { name: n, role: "implementor", session: `sess-${n}`, activity: null, last_exchanges: [] } });
  }
  const listeners = new Set();
  const store = {
    state: { slices, ui: {}, lists: new Map() },
    subscribe: (fn) => (listeners.add(fn), () => listeners.delete(fn)),
    setUi(patch) {
      store.state = { ...store.state, ui: { ...store.state.ui, ...patch } };
      store.notify();
    },
    notify: () => listeners.forEach((fn) => fn(store.state)),
    fetchList,
    newest(name, ids) {
      const cur = slices.get(memberId(name));
      slices.set(cur.id, { ...cur, version: cur.version + 1, body: { ...cur.body, last_exchanges: ids } });
      store.notify();
    },
    transition() {
      const id = `timeline:${D}`;
      slices.set(id, { id, version: (slices.get(id)?.version ?? 0) + 1, as_of: Date.now(), body: { retention_days: 7, total: 0 } });
      store.notify();
    },
  };
  return store;
}

function mount(fetchList, props = {}) {
  const store = fakeStore(fetchList);
  const root = installDom();
  render(html`<${TimelineView} store=${store} teamId=${TEAM_ID} ...${props} />`, root);
  return { store, root, stop: () => render(null, root) };
}

test("the Timeline reads the served spans list of its team, from the range's start, and negative a named range sends no end, so the daemon holds quiet members at their activity", async () => {
  const asked = [];
  const m = mount(async (id, params) => (asked.push({ id, params }), page([lane("a"), lane("b")])));
  await waitFor(() => asked.length > 0, "a read");
  assert.ok(asked.every((a) => a.id === LIST), asked.map((a) => a.id).join());
  assert.equal(asked[0].params.to_ms, undefined);
  assert.equal(asked[0].params.limit, 200);
  assert.ok(Math.abs(Date.now() - 60 * MIN - asked[0].params.from_ms) <= MIN, "last hour by default");
  m.stop();
});

test("a custom range sends its end", async () => {
  const asked = [];
  const m = mount(async (id, params) => (asked.push(params), page([])));
  await waitFor(() => asked.length > 0, "the first read");
  m.store.setUi({ range: "custom", custom: { from: 5 * MIN, to: 9 * MIN } });
  await waitFor(() => asked.some((p) => p.to_ms === 9 * MIN && p.from_ms === 5 * MIN), "the custom read");
  m.stop();
});

test("a served lane draws its spans and both kinds of mark, and negative an internal mark is hidden until internal messages are shown", async () => {
  const m = mount(async () => page([lane("a", [mark("task", "brief", 20), mark("task", "response", 5), mark("sys", "brief", 3, "internal")]), lane("b")]));
  await waitFor(() => find(m.root, "rect.span").length > 0, "spans");
  await waitFor(() => find(m.root, ".mark").length === 2, "marks");
  assert.deepEqual(find(m.root, ".mark").map((c) => c.textContent).sort(), ["brief: task", "response: task"]);
  assert.equal(find(m.root, "circle.response").length, 1);
  m.stop();
});

test("the served band draws a hatched rect with its note, and negative a lane with none draws neither", async () => {
  const m = mount(async () => page([lane("a", [], { band: Date.now() - 20 * MIN }), lane("b")]));
  await waitFor(() => find(m.root, "rect.band").length > 0, "the band");
  assert.equal(find(m.root, "rect.band").length, 1);
  assert.deepEqual(find(m.root, "span.lane-note").map((n) => n.textContent), ["earlier messages not shown"]);
  m.stop();
});

test("the envelope's flags reach the header, and negative they do not without them", async () => {
  const flagged = mount(async () => page([lane("a"), lane("b")], { archived: true, more: true }));
  await waitFor(() => find(flagged.root, "span.archived").length === 1, "the archived chip");
  assert.ok(find(flagged.root, "span.chip").some((c) => c.textContent.includes("narrow the range")));
  flagged.stop();

  const plain = mount(async () => page([lane("a"), lane("b")]));
  await waitFor(() => find(plain.root, "rect.span").length > 0, "spans");
  assert.equal(find(plain.root, "span.archived").length, 0);
  assert.ok(!find(plain.root, "span.chip").some((c) => c.textContent.includes("narrow the range")));
  plain.stop();
});

test("a burst of new exchanges or transitions reads again at most once per coalescing interval, and negative a quiet store reads nothing", async () => {
  const reads = [];
  const m = mount(async () => (reads.push(Date.now()), page([lane("a"), lane("b")])), { coalesceMs: 80 });
  await waitFor(() => reads.length === 1, "the first read");
  await settleMs(120);
  assert.equal(reads.length, 1, "nothing new, nothing read");
  for (let i = 1; i <= 10; i += 1) {
    m.store.newest("b", [`ex${i}`]);
    m.store.transition();
    await settleMs(2);
  }
  await settleMs(250);
  assert.ok(reads.length >= 2, "the burst read again");
  // Judged by the gaps, not a count: a loaded machine stretches the burst over more intervals. 78, as a timer can land a tick early.
  const gaps = reads.slice(1).map((at, i) => at - reads[i]);
  assert.ok(gaps.every((gap) => gap >= 78), `gaps between reads: ${gaps.join()} ms`);
  m.stop();
});

test("a range change drops what was shown until the new range's lanes arrive, and a late answer for the old range is ignored", async () => {
  let release;
  const slow = new Promise((r) => (release = r));
  let reads = 0;
  const m = mount(async () => {
    reads += 1;
    if (reads === 2) {
      await slow;
      return page([lane("a", [mark("stale", "brief", 5)]), lane("b")]);
    }
    return page([lane("a", [mark(reads === 1 ? "first" : "third", "brief", 5)]), lane("b")]);
  });
  await waitFor(() => find(m.root, ".mark").some((c) => c.textContent.includes("first")), "the first marks");
  m.store.setUi({ range: "15m" });
  await waitFor(() => reads === 2 && find(m.root, ".mark").length === 0, "the marks to clear");
  m.store.setUi({ range: "6h" });
  await waitFor(() => find(m.root, ".mark").some((c) => c.textContent.includes("third")), "the new range's marks");
  release();
  await settleMs(30);
  assert.ok(!find(m.root, ".mark").some((c) => c.textContent.includes("stale")));
  m.stop();
});

test("a read that fails keeps what was served, and negative lanes still draw by name before anything is served", async () => {
  let fail = true;
  const m = mount(async () => {
    if (fail) {
      throw new Error("down");
    }
    return page([lane("a", [mark("kept", "brief", 5)]), lane("b")]);
  }, { coalesceMs: 5 });
  await waitFor(() => find(m.root, "li.lane").length === 2, "lanes by name");
  assert.equal(find(m.root, "rect.span").length, 0);
  fail = false;
  m.store.transition();
  await waitFor(() => find(m.root, ".mark").length === 1, "the marks");
  fail = true;
  m.store.transition();
  await settleMs(40);
  assert.equal(find(m.root, ".mark").length, 1);
  m.stop();
});

test("past one page the lanes of the next are read too", async () => {
  const asked = [];
  const m = mount(async (id, params) => {
    asked.push(params.after ?? null);
    return params.after ? page([lane("b")]) : { ...page([lane("a")]), next: "more" };
  });
  await waitFor(() => find(m.root, "rect.span").length === 2, "both lanes");
  assert.deepEqual(asked, [null, "more"]);
  m.stop();
});
