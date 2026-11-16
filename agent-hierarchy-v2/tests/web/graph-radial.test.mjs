import test from "node:test";
import assert from "node:assert/strict";
import { DIR, find, member, mountApp, text, until } from "./mount-app.mjs";

// Radial: Map's chrome over a ring. A path per message (capped), a band per stream that fits its sector, a member trace, the panel's
// sent and received counts, and the mode in the toggle and on Shift+M.

const { setGraph } = await import("../../web/ui/graph.js");
const { CAP } = await import("../../web/ui/graph-radial.js");

const mem = (m, name) => find(m.root, "g.gr-mem").find((g) => g.getAttribute("aria-label").split(", ")[0].endsWith(` ${name}`));
const cls = (el) => el.getAttribute("class") ?? "";
const withTail = (m, tail, totals) => {
  const start = Date.now() - 30 * 60_000;
  const body = { run: "r1", start_ms: start, oldest_ms: start, truncated: false, names: [member("architect"), member("reviewer")], totals, page_size: 250, pages: [], tail, pages_rev: "a" };
  m.store.state.slices.set(`flow:${DIR}/ship`, { id: `flow:${DIR}/ship`, as_of: start + 1, body });
  m.store.setUi({ graphWindow: null, graphCursor: null });
};
const radial = async () => {
  const m = await mountApp();
  setGraph(m.store, { mode: "radial" });
  await until(() => find(m.root, "div.gradial").length === 1 && find(m.root, "g.gr-mem").length > 0, "the ring");
  return m;
};

test("Radial draws one path per message in the view, briefs and reports apart, under Map's scrubber", async () => {
  const m = await radial();
  withTail(m, [[60, 0, 1, 0], [120, 1, 0, 1], [180, 0, 1, 0]], [[0, 1, 2, 0], [1, 0, 0, 1]]);
  await until(() => find(m.root, "path.gr-flow").length === 3, "three paths");
  const flows = find(m.root, "path.gr-flow");
  assert.equal(flows.filter((p) => /\breport\b/.test(cls(p))).length, 1);
  assert.equal(find(m.root, "input").filter((e) => e.getAttribute("id") === "gm-time").length, 1, "Map's scrubber");
  assert.equal(find(m.root, "path.gm-arc").length, 0, "no Map arcs");
});

test("over the cap, Radial draws the newest and says so in the output", async () => {
  const m = await radial();
  const n = CAP + 5;
  withTail(m, Array.from({ length: n }, (_, i) => [i + 1, 0, 1, 0]), [[0, 1, n, 0]]);
  await until(() => find(m.root, "path.gr-flow").length === CAP, "capped");
  assert.match(text(find(m.root, "output")[0]), new RegExp(`newest ${CAP} of ${n}`));
});

test("a member click traces: its paths on, received ones marked in; the panel counts sent and received", async () => {
  const m = await radial();
  withTail(m, [[60, 0, 1, 0], [120, 1, 0, 1], [180, 0, 1, 0]], [[0, 1, 2, 0], [1, 0, 0, 1]]);
  await until(() => find(m.root, "path.gr-flow").length === 3, "paths");
  mem(m, "architect").fire("click");
  assert.equal(m.store.state.ui.selected, member("architect"));
  await until(() => find(m.root, "g.gr-flows.focus").length === 1, "the trace");
  const flows = find(m.root, "path.gr-flow");
  assert.equal(flows.filter((p) => /\bon\b/.test(cls(p))).length, 3);
  assert.equal(flows.filter((p) => /\bin\b/.test(cls(p))).length, 1, "the report it received");
  assert.deepEqual(find(m.root, "i.sw").map(cls), ["sw sent", "sw recv"], "the legend keys direction, as the paths do");
  await until(() => /sent\s*2/.test(text(find(m.root, "section.gm-panel")[0] ?? { textContent: "" })), "sent count");
  assert.match(text(find(m.root, "section.gm-panel")[0]), /received\s*1/);
});

test("a band is a button that fits its stream and dims the rest; clicking it again backs out", async () => {
  const m = await radial();
  const band = find(m.root, "g.gr-band")[0];
  assert.ok(band, "a stream band");
  assert.equal(band.getAttribute("role"), "button");
  assert.equal(band.getAttribute("tabindex"), "0");
  band.fire("click");
  const id = m.store.state.ui.graphFocus;
  assert.match(id ?? "", /^stream:/);
  await until(() => /\bon\b/.test(cls(find(m.root, "g.gr-band")[0])), "the band on");
  assert.equal(find(m.root, "nav.gm-crumbs")[0].all("button").length, 2, "team / stream");
  find(m.root, "g.gr-band")[0].fire("click");
  assert.equal(m.store.state.ui.graphFocus, null);
});

test("Shift+M steps Map → Radial → Flow → Map", async () => {
  const m = await mountApp();
  const { toggleMode, graphUi } = await import("../../web/ui/graph.js");
  const seen = [];
  for (let i = 0; i < 3; i++) {
    toggleMode(m.store);
    seen.push(graphUi(m.store.state.ui).mode);
  }
  assert.deepEqual(seen, ["radial", "flow", "map"]);
});
