import { test } from "node:test";
import assert from "node:assert/strict";
import { countRenders, settle } from "./dom.mjs";
import { mountApp, until } from "./mount-app.mjs";
import { turnsFromArgs } from "../../web/mock/serve.mjs";
import { VIEW, injected, mountPane, px, viewportOffset, scrollTo } from "./pane-mount.mjs";

const { setTurnCount } = await import("../../web/mock/transcripts.mjs");
const { offsetsOf, windowAt, anchorAt, topOfAnchor, estimateOf, OVERSCAN_PX } = await import("../../web/ui/transcript-window.js");

test("window math: prefix sums, binary-searched window, anchor and its restore; a row outside the margin is left out", () => {
  const rows = Array.from({ length: 10 }, (_, i) => ({ key: `r${i}`, kind: "other" }));
  const heights = new Map(rows.map((r, i) => [r.key, 100 + i]));
  const offsets = offsetsOf(rows, heights);
  assert.equal(offsets[10], 1045);
  assert.deepEqual(windowAt(offsets, 500, 100, 0), [4, 6], "rows 4 and 5 meet [500, 600]");
  assert.deepEqual(windowAt(offsets, 0, 100, 0), [0, 1]);
  assert.deepEqual(windowAt(new Float64Array(1), 0, 100), [0, 0], "no rows");
  const a = anchorAt(rows, offsets, 450);
  assert.deepEqual(a, { key: "r4", delta: 450 - offsets[4] });
  heights.set("r0", 300);
  assert.equal(topOfAnchor(rows, offsetsOf(rows, heights), a), 450 + 200);
  assert.equal(topOfAnchor(rows, offsets, { key: "gone", delta: 0 }), null);
  assert.equal(anchorAt(rows, offsets, 5000), null);
  assert.equal(estimateOf({ kind: "text" }), 56);
  assert.equal(estimateOf({ kind: "tool" }), 36);
  assert.equal(estimateOf({ kind: "thinking" }), 28);
});

test("P2: prefix sums and windows over 600 rows, 1000 times, stay far from quadratic", () => {
  const rows = Array.from({ length: 600 }, (_, i) => ({ key: `k${i}`, kind: ["text", "tool", "other"][i % 3] }));
  const heights = new Map();
  const start = performance.now();
  for (let i = 0; i < 1000; i += 1) {
    const offsets = offsetsOf(rows, heights);
    windowAt(offsets, (i * 37) % 9000, VIEW);
  }
  assert.ok(performance.now() - start < 200, `${performance.now() - start} ms`);
});

test("W1: a text row renders its whole excerpt and has the measured height, which the padding accounts for", async () => {
  const m = await mountApp();
  const long = Array.from({ length: 10 }, (_, i) => `line ${i}`).join("\n");
  const heightOf = (key) => (ref.pane?.rows.find((r) => r.key === key)?.kind === "text" ? 240 : 30);
  const ref = {};
  const mounted = await mountPane(m, heightOf);
  ref.pane = mounted.pane;
  const { pane, root } = mounted;
  const target = [...pane.entries].reverse().find((e) => e.blocks.some((b) => b.kind === "text" && !e.turn_start));
  target.blocks.find((b) => b.kind === "text").excerpt = long;
  pane.forceUpdate();
  await settle();
  await settle();
  const li = root.all("li.k-text").find((el) => el.textContent.includes("line 9"));
  assert.ok(li, "the whole excerpt is in the row");
  assert.equal(li.all(".ellipsis").length + li.all(".clamp").length, 0);
  const sum = pane.rows.reduce((n, r) => n + (pane.heights.get(r.key) ?? estimateOf(r)), 0);
  const pads = px(root.all(".tr-list")[0].style.paddingTop) + px(root.all(".tr-list")[0].style.paddingBottom);
  const shown = pane.rows.slice(...pane.mounted).reduce((n, r) => n + heightOf(r.key), 0);
  assert.equal(pads + shown, sum, "padding plus the mounted heights is the total");
  assert.equal(pane.heights.get(pane.rows.find((r) => r.label === long).key), 240);
  mounted.unmount();
});

test("W2: the mounted rows are the rows intersecting the view plus or minus 600 px, for mixed heights", async () => {
  const m = await mountApp();
  const mounted = await mountPane(m);
  const { pane, root } = mounted;
  for (const top of [0, 700, 1500, 1_000_000]) {
    await scrollTo(pane, top);
    const { offsets } = pane.layout;
    const at = pane.state.following ? Math.max(0, offsets.at(-1) - VIEW) : pane.state.top;
    const meeting = pane.rows.filter((_, i) => offsets[i + 1] > at - OVERSCAN_PX && offsets[i] < at + VIEW + OVERSCAN_PX).length;
    const shown = root.all("li.tr-row").length;
    assert.ok(shown <= meeting && shown > 0, `${shown} mounted, ${meeting} intersect at ${top}`);
    assert.ok(shown < pane.rows.length, "not the whole list");
  }
  mounted.unmount();
});

async function bigMount() {
  setTurnCount("architect", 400);
  try {
    return await mountApp();
  } finally {
    setTurnCount("architect", null);
  }
}

test("W3: paused, three load-olders leave the anchor row where it was in the viewport", async () => {
  const m = await bigMount();
  const mounted = await mountPane(m);
  const { pane } = mounted;
  await pane.loadOlder();
  await settle();
  await settle();
  const { offsets } = pane.layout;
  await scrollTo(pane, offsets[Math.floor(pane.rows.length * 0.2)] + 7);
  assert.equal(pane.state.following, false);
  const { key, delta } = pane.anchor;
  for (let i = 0; i < 2; i += 1) {
    await pane.loadOlder();
    await settle();
    await settle();
    assert.equal(pane.anchor.key, key);
    assert.ok(pane.rows.findIndex((r) => r.key === key) >= pane.mounted[0], "rows were added above the anchor");
    assert.ok(Math.abs(viewportOffset(mounted, key) + delta) <= 1, `load ${i + 2}: ${viewportOffset(mounted, key)} vs ${-delta}`);
  }
  assert.ok(m.store.state.lists.get(pane.sel.lid).windows.length <= 3);
  await pane.loadOlder();
  await settle();
  await settle();
  assert.equal(pane.rows.some((r) => r.key === key), false, "a fourth window evicted the anchor's window");
  assert.ok(pane.mounted[1] > pane.mounted[0], "the list still draws rows");
  mounted.unmount();
});

test("W4: heights corrected above the anchor leave its viewport offset alone", async () => {
  const m = await bigMount();
  const override = new Map();
  const heightOf = (key) => override.get(key) ?? injected(key);
  const mounted = await mountPane(m, heightOf);
  const { pane } = mounted;
  await pane.loadOlder();
  await settle();
  await scrollTo(pane, pane.layout.offsets[Math.floor(pane.rows.length * 0.5)] + 11);
  const { key, delta } = pane.anchor;
  const i = pane.rows.findIndex((r) => r.key === key);
  const above = pane.rows.slice(Math.max(pane.mounted[0], 0), i).map((r) => r.key);
  assert.ok(above.length >= 2, "rows are mounted above the anchor");
  above.forEach((k) => override.set(k, injected(k) + 25));
  pane.forceUpdate();
  await settle();
  await settle();
  assert.ok(above.every((k) => pane.heights.get(k) === injected(k) + 25), "the corrections were taken");
  assert.equal(pane.anchor.key, key);
  assert.ok(Math.abs(viewportOffset(mounted, key, heightOf) + delta) <= 1);
  mounted.unmount();
});

test("W5: following, an appended turn leaves the end in view", async () => {
  const m = await mountApp();
  const mounted = await mountPane(m);
  const { pane } = mounted;
  const newest = pane.entries.at(-1).offset;
  m.model.appendTranscript("architect");
  await until(() => pane.entries.at(-1).offset > newest, "the appended rows");
  await settle();
  await settle();
  assert.equal(pane.state.following, true);
  assert.ok(pane.el.scrollTop + VIEW >= pane.layout.offsets.at(-1) - 1);
  mounted.unmount();
});

async function rendersForAppend(heightOf) {
  const m = await mountApp();
  const ref = {};
  const mounted = await mountPane(m, (key) => heightOf(ref.pane, key));
  ref.pane = mounted.pane;
  const newest = ref.pane.entries.at(-1).offset;
  const counter = countRenders();
  m.model.appendTranscript("architect");
  await until(() => ref.pane.entries.at(-1).offset > newest, "the appended rows");
  await settle();
  await settle();
  const drawn = counter.counts.get("TranscriptPane") ?? 0;
  counter.stop();
  mounted.unmount();
  return drawn;
}

test("W6: stable heights cost at most two draws beyond a data change, and a sub-half-pixel difference costs none", async () => {
  const exact = (pane, key) => (pane ? estimateOf(pane.rows.find((r) => r.key === key)) : 0);
  const base = await rendersForAppend(exact);
  const corrected = await rendersForAppend((_pane, key) => injected(key));
  const near = await rendersForAppend((pane, key) => (pane ? exact(pane, key) + 0.4 : 0));
  assert.ok(corrected - base <= 2, `${corrected} draws against ${base}`);
  assert.equal(near, base, "a 0.4 px difference is not corrected");
});

test("the mock's turn count comes from --turns <member>=<count>; a malformed or unknown option is refused", () => {
  assert.deepEqual(turnsFromArgs(["--port", "1", "--turns", "architect=2000", "--turns", "reviewer=5"]), [["architect", 2000], ["reviewer", 5]]);
  assert.deepEqual(turnsFromArgs(["--no-tick"]), []);
  for (const bad of ["architect", "architect=0", "architect=1.5", "architect=-3", "nobody=5", "architect=5=6", ""]) {
    assert.throws(() => turnsFromArgs(["--turns", bad]), /--turns wants/, bad);
  }
  assert.throws(() => turnsFromArgs(["--turns"]), /--turns wants/);
});

test("P1: on a 2,000-turn member, paging from live to the oldest entry keeps windows, mounted rows and measure work bounded", async () => {
  setTurnCount("architect", 2000);
  let m;
  try {
    m = await mountApp();
  } finally {
    setTurnCount("architect", null);
  }
  let calls = 0;
  const mounted = await mountPane(m, (key) => ((calls += 1), injected(key)));
  const { pane, root } = mounted;
  const orig = pane.settleLayout.bind(pane);
  let worst = 0;
  pane.settleLayout = () => {
    const before = calls;
    orig();
    worst = Math.max(worst, (calls - before) - (pane.mounted[1] - pane.mounted[0]));
  };
  const bound = () => {
    const { offsets } = pane.layout;
    const at = pane.state.following ? Math.max(0, offsets.at(-1) - VIEW) : pane.layout.top ?? pane.state.top;
    return pane.rows.filter((_, i) => offsets[i + 1] > at - OVERSCAN_PX && offsets[i] < at + VIEW + OVERSCAN_PX).length;
  };
  const lid = pane.sel.lid;
  let loads = 0;
  while (pane.sel.more && loads < 400) {
    pane.el.scrollTop = 0;
    pane.el.fire("scroll");
    await pane.loadOlder();
    await settle();
    loads += 1;
    assert.ok(m.store.state.lists.get(lid).windows.length <= 3, "windows held");
    assert.ok(root.all("li.tr-row").length <= bound() + 1, `mounted rows ${root.all("li.tr-row").length} against ${bound()}`);
    assert.ok(pane.heights.size <= 2 * pane.rows.length + 64, "measured heights are pruned");
  }
  assert.ok(loads > 100, `${loads} pages`);
  assert.equal(pane.sel.more, false, "reached the oldest");
  assert.equal(pane.entries[0].offset, 0);
  assert.ok(worst <= 0, "a measurement pass reads only mounted rows");
  const reads = m.log.lists.filter((l) => l.path.includes("list:transcript:"));
  assert.ok(reads.every((l) => Number(l.params.limit) === 60 || l.params.expand !== undefined), "no unbounded read");
  mounted.unmount();
});

test("leaving the view while an older page loads sets no state afterwards (nearest non-match: a load that finishes while mounted clears the spinner)", async () => {
  const m = await bigMount();
  const mounted = await mountPane(m);
  const { pane } = mounted;
  await pane.loadOlder();
  await settle();
  assert.equal(pane.state.loadingOlder, false, "a finished load clears the flag");
  const calls = [];
  const set = pane.setState.bind(pane);
  pane.setState = (s) => (calls.push(s), set(s));
  const pending = pane.loadOlder();
  mounted.unmount();
  calls.length = 0;
  await pending;
  await settle();
  assert.deepEqual(calls.filter((s) => "loadingOlder" in s), [], "no setState after unmount");
});
