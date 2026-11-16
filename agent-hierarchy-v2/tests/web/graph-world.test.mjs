import test from "node:test";
import assert from "node:assert/strict";

const W = await import("../../web/ui/graph-world.js");
const { readPrefs, graphUi } = await import("../../web/ui/graph.js");

// A run of 9 events: 2 full pages of 3 (page_size 3) and a tail of 3. Members 0..2.
const START = Date.UTC(2026, 9, 31, 12, 0, 0);
const EV = [[10, 0, 1, 0], [20, 1, 0, 1], [30, 0, 2, 0], [40, 2, 0, 1], [50, 0, 1, 0], [60, 1, 0, 1], [70, 0, 1, 0], [80, 0, 2, 0], [90, 2, 0, 1]];
const head = {
  run: "20261031-120000-r",
  start_ms: START,
  oldest_ms: START + 10_000,
  truncated: false,
  names: ["member:d/t/a", "member:d/t/b", "member:d/t/c"],
  totals: [[0, 1, 3, 0], [1, 0, 0, 2], [0, 2, 2, 0], [2, 0, 0, 2]],
  page_size: 3,
  pages: [10, 40],
  tail: EV.slice(6),
};
const pages = new Map([[0, EV.slice(0, 3)], [1, EV.slice(3, 6)]]);
const CLOCK = START + 95_000;
const at = (cursorSec, winId = "run") => W.scrubOf({ graphCursor: START + cursorSec * 1000, graphWindow: winId }, head, CLOCK);

test("the window holds cursor − window < t ≤ cursor: the lower edge is out, the cursor itself is in", () => {
  assert.deepEqual(W.inWindow(EV, 50, 20).map((e) => e[0]), [40, 50]);
  assert.deepEqual(W.inWindow(EV, 50, null).map((e) => e[0]), [10, 20, 30, 40, 50]);
  assert.deepEqual(W.inWindow(EV, 9, null), []);
});

test("at now over the whole run the view uses the totals and asks for no page", () => {
  const s = W.scrubOf({}, head, CLOCK);
  assert.equal(s.live, true);
  assert.deepEqual(W.neededPages(head, s), []);
  const v = W.scrubbed(head, new Map(), s);
  assert.equal(v.events, null);
  assert.equal(v.count, 9);
  // nearest non-match: the same cursor with a 10-minute window reads events, not totals
  assert.notEqual(W.scrubbed(head, pages, W.scrubOf({ graphWindow: "10" }, head, CLOCK)).events, null);
});

test("the page set for a range is the pages whose span meets it, and none once the range is inside the tail", () => {
  assert.deepEqual(W.pagesFor(head, -Infinity, 25), [0]);
  assert.deepEqual(W.pagesFor(head, 35, 55), [0, 1]); // page 0 runs to 40, where page 1 starts: a shared second is kept
  assert.deepEqual(W.pagesFor(head, 65, 90), [1]); // page 1 may hold events up to 70
  assert.deepEqual(W.pagesFor(head, 70, 90), []);
  assert.deepEqual(W.neededPages(head, { live: false, cursor: 60, win: 15 }), [1]);
  assert.deepEqual(W.neededPages(head, at(50)), [0, 1]);
});

test("the pairs over the full range sum to the totals", () => {
  const full = W.pairsOf(W.eventsOf(head, pages));
  const key = (p) => `${p.a}|${p.b}|${p.k}`;
  const byTotals = new Map(W.pairsOfTotals(head).map((p) => [key(p), p.n]));
  assert.deepEqual(new Map(full.map((p) => [key(p), p.n])), byTotals);
});

test("a missing page is a gap the view leaves out, not a crash", () => {
  assert.deepEqual(W.eventsOf(head, new Map([[1, EV.slice(3, 6)]])).map((e) => e[0]), [40, 50, 60, 70, 80, 90]);
});

test("the scrubber spans whole steps back from now, so its right end is exactly now", () => {
  const s = W.spanOf(head, CLOCK);
  assert.equal(s.max, CLOCK);
  assert.equal((s.max - s.min) % W.STEP_MS, 0);
  assert.ok(s.min <= head.start_ms);
  // the head has no clock: a client clock behind the newest event is moved up to it
  assert.equal(W.spanOf(head, START).max, START + 90_000);
});

test("the readout names the cursor and the count, and says when history is cut or no run is open", () => {
  assert.match(W.readout(head, CLOCK, 9, true), /^now · 9 messages$/);
  assert.match(W.readout({ ...head, truncated: true }, CLOCK, 1, true), /^history from \d\d:\d\d · now · 1 message$/);
  assert.match(W.readout({ ...head, run: null }, CLOCK, 0, false), /^last hour · \d\d:\d\d · 0 messages$/);
});

test("pages are judged by their own start and size, never by as_of: the daemon's head is always newer than its full pages", () => {
  const t = { dir_id: "d", name: "t" };
  // the daemon's rule: a page's as_of is the newest of its own events; the head's is the newest event of all
  const asOf = (events) => START + events[events.length - 1][0] * 1000;
  const slices = new Map([
    [W.pageId(t, head.run, 0), { as_of: asOf(EV.slice(0, 3)), body: { events: EV.slice(0, 3) } }],
    [W.pageId(t, head.run, 1), { as_of: asOf(EV.slice(3, 6)), body: { events: EV.slice(3, 6) } }],
  ]);
  const h = { ...head, pages_rev: "b" };
  assert.deepEqual([...W.pagesOf(slices, t, h).keys()], [0, 1]);
  // the lane cap evicted event 0: the head renumbers (pages now start at 20 and 50) and page 0 still holds the old numbering
  const renumbered = { ...h, pages_rev: "c", pages: [20, 50] };
  assert.deepEqual([...W.pagesOf(slices, t, renumbered).keys()], []);
  // the republished page arrives: it is taken, whatever its as_of
  slices.set(W.pageId(t, head.run, 0), { as_of: 1, body: { events: EV.slice(1, 4) } });
  assert.deepEqual([...W.pagesOf(slices, t, renumbered).keys()], [0]);
  // a short page (not yet full) is not taken
  slices.set(W.pageId(t, head.run, 1), { as_of: 1, body: { events: EV.slice(4, 6) } });
  assert.equal(W.pagesOf(slices, t, renumbered).has(1), false);
});

test("now is the client clock when the tail is empty: a page's first event is not the newest", () => {
  const empty = { ...head, tail: [] };
  assert.equal(W.newestMs(empty), null);
  assert.equal(W.nowOf(empty, START + 5_000), START + 5_000);
});

test("an unknown or absent ah-graph mode opens Map", () => {
  assert.equal(W.modeOf("garbage"), "map");
  assert.equal(W.modeOf(undefined), "map");
  assert.equal(W.modeOf("flow"), "flow");
  assert.equal(W.modeOf("radial"), "radial");
  assert.equal(graphUi({ graphMode: "radial-ish" }).mode, "map");
  const saved = globalThis.localStorage;
  globalThis.localStorage = { getItem: () => JSON.stringify({ mode: 7 }) };
  try {
    assert.equal(readPrefs().mode, "map");
  } finally {
    globalThis.localStorage = saved;
  }
});

test("the tree puts the loose members in their own group first, then one circle per stream in name order", () => {
  const tree = W.treeOf("team:d/t", "t", [{ id: "a", stream: null }, { id: "b", stream: "zeta" }, { id: "c", stream: "alpha" }]);
  assert.deepEqual(tree.children.map((c) => c.id), ["loose:team:d/t", "stream:alpha", "stream:zeta"]);
  assert.equal(tree.children[0].type, "group");
  assert.deepEqual(tree.children[0].children.map((c) => c.id), ["a"]);
  assert.deepEqual(tree.children[1].children.map((c) => c.id), ["c"]);
  // with no streams there is nothing to keep apart from: the members sit at the top
  assert.deepEqual(W.treeOf("team:d/t", "t", [{ id: "a", stream: null }]).children.map((c) => c.id), ["a"]);
});

test("arc width is 1 px for one message and stays within 2 px", () => {
  assert.equal(W.widthOf(1), 1);
  assert.ok(W.widthOf(16) > 1 && W.widthOf(16) < 2);
  assert.equal(W.widthOf(10_000), 2);
});

test("windowed arcs fade with age; the run window does not", () => {
  assert.equal(W.fadeOf(100, 100, 600), 0.9);
  assert.ok(W.fadeOf(100, 0, 600) < 0.9);
  assert.equal(W.fadeOf(100, 0, null), null);
});

test("Radial leases the newest pages that hold its cap up to the cursor over the run, and the window's pages otherwise", () => {
  const at = (sec, win = null) => ({ cursor: sec, win, live: false });
  assert.deepEqual(W.newestPages(head, at(90), 3), [], "the tail alone holds 3");
  assert.deepEqual(W.newestPages(head, at(90), 4), [1], "one page more");
  assert.deepEqual(W.newestPages(head, at(90), 9), [0, 1]);
  assert.deepEqual(W.newestPages(head, at(90), 600), [0, 1], "all there are");
  assert.deepEqual(W.newestPages(head, at(30), 2), [0], "a page past the cursor is not needed");
  assert.deepEqual(W.newestPages(head, at(60, 15), 600), W.pagesFor(head, 45, 60), "a window needs what covers it");
});

test("events carry their run index, page k's j-th at k·page_size + j, the tail after the full pages, gaps kept", () => {
  const pages = new Map([[1, EV.slice(3, 6)]]);
  assert.deepEqual(W.indexedEvents(head, pages).map(([i]) => i), [3, 4, 5, 6, 7, 8]);
  assert.deepEqual(W.indexedEvents(head, pages)[0][1], EV[3]);
});

test("a member's sent and received count from the windowed events, else from the ordered totals", () => {
  const events = [[1, 0, 1, 0], [2, 1, 0, 1], [3, 0, 2, 0]];
  assert.deepEqual(W.sentReceived(head, { events }, 0), { sent: 2, received: 1 });
  const h = { totals: [[0, 1, 3, 1], [1, 0, 2, 2], [-1, -1, 5, 5]] };
  assert.deepEqual(W.sentReceived(h, { events: null }, 0), { sent: 4, received: 4 }, "the overflow row has no ends");
  assert.deepEqual(W.sentReceived(h, { events: null }, undefined), { sent: 0, received: 0 });
});

test("off now over the run, Radial counts every message up to the cursor though only the newest pages are loaded", () => {
  // 2000 events in 8 full pages of 250 and a tail of 50, one a second; the cursor at 1500 s falls inside page 5.
  const ev = (i) => [i + 1, i % 2, (i + 1) % 2, i % 2];
  const big = { run: "r", start_ms: 0, page_size: 250, pages: Array.from({ length: 8 }, (_, k) => k * 250 + 1), tail: Array.from({ length: 50 }, (_, j) => ev(2000 + j)), names: ["a", "b"], totals: [] };
  const s = { cursor: 1500, win: null, live: false };
  const keys = W.newestPages(big, s, 600);
  assert.deepEqual(keys, [2, 3, 4, 5], "the page the cursor is in counts as none, so the cap is always loaded");
  const pages = new Map(keys.map((k) => [k, Array.from({ length: 250 }, (_, j) => ev(k * 250 + j))]));
  const v = W.radialView(big, pages, s, 600);
  assert.equal(v.drawn.length, 600);
  assert.equal(v.drawn[599][2], 1499, "the newest drawn is the event at the cursor");
  assert.equal(v.count, 1500, "every message up to the cursor, not the 1000 loaded");
  assert.equal(v.partial, 600, "the panel counts what is drawn, and says so");
  assert.equal(v.events.length, 600);
  assert.deepEqual(W.sentReceived(big, v, 0), { sent: 300, received: 300 });
  const win = W.radialView(big, pages, { cursor: 1500, win: 300, live: false }, 600);
  assert.deepEqual([win.count, win.partial], [300, 0], "a window's pages cover it: its count is exact");
});

// The Map's fit and arc offset, shared by the web Map and Herdr's Graph: a change to either number moves both drawings.
test("fitPack scales, centres and transposes a pack; sideways offsets an arc's inner points 2 units in 1000, briefs one way", () => {
  const root = { x: 5, y: 3, r: 2 };
  const leaf = { x: 6, y: 3, r: 1 };
  root.descendants = () => [root, leaf];
  W.fitPack(root, 100);
  assert.deepEqual([root, leaf].map((n) => [n.x, n.y, n.r]), [[50, 50, 50], [50, 75, 25]]);
  const pts = [{ x: 0, y: 0 }, { x: 5, y: 0 }, { x: 10, y: 0 }];
  assert.deepEqual(W.sideways(pts, pts[0], pts[2], 0, 1000), [{ x: 0, y: 0 }, { x: 5, y: 2 }, { x: 10, y: 0 }]);
  assert.deepEqual(W.sideways(pts, pts[0], pts[2], 1, 500)[1], { x: 5, y: -1 });
});
