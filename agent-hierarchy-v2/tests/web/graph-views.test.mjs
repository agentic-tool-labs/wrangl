import { test, afterEach } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { settle } from "./dom.mjs";
import { mountApp as mount, until, find, text, member, DIR } from "./mount-app.mjs";

const G = await import("../../web/ui/graph.js");
const { FLOW_COLUMN } = await import("../../web/mock/model.mjs");
const { globalKey } = await import("../../web/ui/keys.js");
const { FlowStage } = G;

const css = readFileSync(new URL("../../web/ui/r6-graph.css", import.meta.url), "utf8");

// ---- the remembered choice (localStorage may be absent, blocked, or hold nonsense)

const fake = (initial = null) => {
  const kept = new Map(initial === null ? [] : [["ah-graph", initial]]);
  Object.defineProperty(globalThis, "localStorage", { value: { getItem: (k) => kept.get(k) ?? null, setItem: (k, v) => kept.set(k, v), removeItem: (k) => kept.delete(k) }, configurable: true });
  return kept;
};
afterEach(() => {
  delete globalThis.localStorage;
});

test("the choice defaults to Map, horizontal, legend shown, and survives nonsense and a blocked store", () => {
  assert.deepEqual(G.readPrefs(), { mode: "map", orient: "horizontal", legend: true });
  fake("not json");
  assert.deepEqual(G.readPrefs(), { mode: "map", orient: "horizontal", legend: true });
  fake(JSON.stringify({ mode: "sideways", orient: 3, legend: "no" }));
  assert.deepEqual(G.readPrefs(), { mode: "map", orient: "horizontal", legend: true }, "an unknown value falls back to the default");
  Object.defineProperty(globalThis, "localStorage", { get() { throw new Error("blocked"); }, configurable: true });
  assert.deepEqual(G.readPrefs(), { mode: "map", orient: "horizontal", legend: true });
  G.setGraph({ setUi() {} }, { mode: "flow" });
});

test("a saved Flow, vertical, legend dismissed is read back; anything else is the default", () => {
  fake(JSON.stringify({ mode: "flow", orient: "vertical", legend: false }));
  assert.deepEqual(G.readPrefs(), { mode: "flow", orient: "vertical", legend: false });
  fake(JSON.stringify({ mode: "map", orient: "horizontal", legend: true }));
  assert.deepEqual(G.readPrefs(), { mode: "map", orient: "horizontal", legend: true });
});

// ---- the pure parts

test("link widths are 1 / 1.5 / 2 / 3 / 4 px by messages, each bucket's edges on both sides", () => {
  assert.deepEqual([0, 1, 2, 3, 6, 7, 11, 12, 99].map(G.widthOf), [1, 1.5, 1.5, 2, 2, 3, 3, 4, 4]);
});

test("the hour is counted now from the listed times; a message exactly an hour old still counts, a second older does not", () => {
  const now = 10_000_000;
  assert.equal(G.messagesText({ n: 5, recent: [now - 60_000, now - 3_600_000, now - 3_601_000] }, now), "5 messages this run · 2 in the last hour");
  assert.equal(G.messagesText({ n: 0, recent: [] }, now), "0 messages this run · 0 in the last hour");
});

test("a pair is sent for review when the daemon says one of its briefs is, whichever came first; any other pair is a brief in flight", () => {
  const kinds = G.sparkKinds([{ from: "i", to: "r", review: true }, { from: "o", to: "i" }, { from: "r", to: "i" }, { from: "i", to: "a" }]);
  assert.equal(kinds.get("i|r"), "review", "a later brief on the pair does not undo it");
  assert.equal(kinds.get("i|o"), "brief");
  assert.equal(kinds.get("a|i"), "brief");
  assert.equal(G.sparkKinds([{ from: "r", to: "i" }]).get("i|r"), "brief", "a brief the daemon does not mark is not a review");
});

const people = (spec) => spec.map(([id, role, name]) => ({ id, role, name: name ?? id, column: FLOW_COLUMN[role] ?? 2 }));

test("Flow places a card in the column the daemon serves, whatever its role, and one with none served in Implement", () => {
  const f = G.flowLayout([{ id: "a", role: "reviewer", name: "a", column: 1 }, { id: "b", role: "reviewer", name: "b", column: 3 }, { id: "c", role: "orchestrator", name: "c" }, { id: "d", role: "implementor", name: "d", column: 2 }], new Set(), "horizontal");
  assert.ok(f.pos.get("a").x < f.pos.get("d").x && f.pos.get("d").x < f.pos.get("b").x, "the role does not move a card");
  assert.equal(f.pos.get("c").x, f.pos.get("d").x);
});

test("Flow places each column right of the last, idle members as short rows, and the lead centred on the tallest column", () => {
  const nodes = people([["o", "orchestrator"], ["a", "architect"], ["i1", "implementor"], ["i2", "implementor"], ["i3", "implementor"], ["r", "reviewer"]]);
  const f = G.flowLayout(nodes, new Set(["i1", "r"]), "horizontal");
  const x = (id) => f.pos.get(id).x;
  assert.ok(x("o") < x("a") && x("a") < x("i1") && x("i1") < x("r"));
  assert.equal(x("i1"), x("i2"));
  assert.deepEqual([f.pos.get("i1").w, f.pos.get("i1").h], [G.FLOW.card.w, G.FLOW.card.h], "a working member is a card");
  assert.deepEqual([f.pos.get("i2").w, f.pos.get("i2").h], [G.FLOW.row.w, G.FLOW.row.h], "an idle member is a row");
  const column = ["i1", "i2", "i3"].map((id) => f.pos.get(id));
  const middle = (column[0].y + column[2].y + column[2].h) / 2;
  assert.ok(Math.abs(f.pos.get("o").y + f.pos.get("o").h / 2 - middle) <= 1, "the lead is centred on the tallest column");
  assert.deepEqual(f.labels.map((l) => l.text), ["Lead", "Advise · design", "Implement", "Review"], "ah2-373 R6: a column with no card is not drawn");
  assert.equal(G.flowLayout([], new Set(), "horizontal").box, null, "ah2-373 R6: no card, no column, nothing to fit");
  assert.equal(G.flowLayout([], new Set(), "horizontal").pos.size, 0);
});

test("vertical Flow puts the columns top to bottom", () => {
  const nodes = people([["o", "orchestrator"], ["i", "implementor"], ["r", "reviewer"]]);
  const f = G.flowLayout(nodes, new Set(["i"]), "vertical");
  assert.ok(f.pos.get("o").y < f.pos.get("i").y && f.pos.get("i").y < f.pos.get("r").y);
  const h = G.flowLayout(nodes, new Set(["i"]), "horizontal");
  assert.ok(h.pos.get("o").x < h.pos.get("i").x, "the same team lays out sideways when horizontal");
});

test("a Flow link leaves the right edge of one card and enters the left edge of the next; vertical uses bottom and top", () => {
  const a = { x: 0, y: 0, w: 100, h: 40 };
  const b = { x: 300, y: 100, w: 100, h: 40 };
  assert.match(G.flowPath(a, b, "horizontal"), /^M 100 20 C 200 20 200 120 300 120$/);
  assert.match(G.flowPath(a, { x: 0, y: 200, w: 100, h: 40 }, "vertical"), /^M 50 40 C 50 120 50 120 50 200$/);
});

// ---- the page

const mountGraph = async () => {
  const m = await mount({ view: null, ui: { view: "graph" } });
  await until(() => find(m.root, ".graph").length === 1 && find(m.root, ".graph").length > 0 && (find(m.root, "g.gm-leaf").length === 8 || find(m.root, ".fcard").length === 8), "the graph");
  await settle();
  return m;
};
const click = (el) => el.fire("click");
const withClass = (els, name) => els.filter((e) => new RegExp(`\\b${name}\\b`).test(e.getAttribute("class") ?? ""));
const button = (root, label) => find(root, "button").find((b) => b.getAttribute("aria-label") === label || b.textContent === label);
const lastSub = (m) => m.log.polls.at(-1) ?? "";
const feedIds = `flow:${DIR}/ship`; // Map now leases the team's message-flow head (and the pages it scrubs), not the feed list

test("Map is the default; the header toggle is a labelled group of three pressed-state buttons, Map | Radial | Flow", async () => {
  fake();
  const m = await mountGraph();
  assert.equal(find(m.root, ".gpack").length, 1);
  assert.equal(find(m.root, ".gflow").length, 0);
  const group = find(m.root, ".graph-toggle")[0];
  assert.equal(group.getAttribute("aria-label"), "Graph layout");
  assert.deepEqual(find(group, "button").map((b) => [b.textContent, b.getAttribute("aria-pressed")]), [["Map", "true"], ["Radial", "false"], ["Flow", "false"]]);
});

test("the toggle shows on the Graph tab only (the nearest tab that must not show it)", async () => {
  fake();
  const m = await mountGraph();
  assert.equal(find(m.root, ".graph-toggle").length, 1);
  m.store.setUi({ view: "overview" });
  await until(() => find(m.root, ".graph-toggle").length === 0, "the toggle gone");
});

test("a member's busy ring spans the share of the last hour, and a member without one has the minimum sliver only", async () => {
  fake();
  const m = await mountGraph();
  const leaf = (name) => find(m.root, "g.gm-leaf").find((n) => n.getAttribute("aria-label").includes(` ${name},`));
  const busy = (name) => find(leaf(name), "path.busy")[0].getAttribute("d");
  const largeArc = (d) => /A[\d.]+,[\d.]+,0,(\d),/.exec(d)?.[1];
  assert.equal(largeArc(busy("implementor-1")), "1", "the 0.82 share is more than half a turn");
  assert.equal(largeArc(busy("docs-writer")), "0", "no share published: only the sliver, nothing estimated");
  assert.equal(find(leaf("docs-writer"), "circle.track").length, 1);
});

test("Map holds the team's message-flow lease while it shows and gives it back for Flow, for another tab and on unmount", async () => {
  fake();
  const m = await mountGraph();
  await until(() => lastSub(m).includes(feedIds), "the flow leased by Map");
  G.setGraph(m.store, { mode: "flow" });
  await until(() => !lastSub(m).includes(feedIds), "the flow released for Flow");
  G.setGraph(m.store, { mode: "map" });
  await until(() => lastSub(m).includes(feedIds), "the flow leased again");
  m.store.setUi({ view: "overview" });
  await until(() => !lastSub(m).includes(feedIds), "the flow released by another tab");
});

test("the layout is computed again when the working members change, once, and not for polls that change nothing", async () => {
  fake(JSON.stringify({ mode: "flow", orient: "horizontal", legend: true }));
  const original = FlowStage.prototype.layout;
  let computed = 0;
  FlowStage.prototype.layout = function layout(...args) {
    const before = this.cached;
    const result = original.apply(this, args);
    computed += result !== before ? 1 : 0;
    return result;
  };
  try {
    const m = await mountGraph();
    const first = computed;
    assert.ok(first >= 1);
    const polls = m.log.polls.length;
    m.model.tick();
    await until(() => m.log.polls.length > polls + 1, "a poll after the tick");
    await settle();
    assert.equal(computed, first + 1, "the tick flips a member busy/idle: one layout for the new working set");
    await settle();
    await settle();
    assert.equal(computed, first + 1, "nothing else changed: no further layout");
    G.setGraph(m.store, { mode: "map" });
    await settle();
    G.setGraph(m.store, { mode: "flow" });
    await until(() => computed === first + 2, "one layout for a fresh Flow stage");
    await settle();
    assert.equal(computed, first + 2);
  } finally {
    FlowStage.prototype.layout = original;
  }
});

test("Flow draws a card for each working member and a row for each idle one, with the lead's numbers and a dash where nothing is published", async () => {
  fake();
  const m = await mountGraph();
  click(button(m.root, "Flow"));
  await until(() => find(m.root, ".gflow").length === 1, "Flow");
  assert.equal(find(m.root, "g.gm-leaf").length, 0, "Map's circles are gone");
  const cards = find(m.root, ".fcard");
  assert.equal(cards.length, 8);
  const active = withClass(cards, "active");
  const idleCards = withClass(cards, "idle");
  assert.ok(active.length >= 1 && idleCards.length >= 1, `active ${active.length}, idle ${idleCards.length}`);
  const lead = cards.find((c) => c.getAttribute("data-id") === member("@orchestrator"));
  assert.match(text(lead), /14 dec/);
  assert.match(text(lead), /2 out/, "the open briefs the orchestrator sent");
  assert.doesNotMatch(text(lead), /—|\/turn/, "a field with no source is not drawn");
  assert.doesNotMatch(text(find(m.root, ".gflow")[0]), /—|turns/, "no card shows an empty field");
  const idle = idleCards[0];
  assert.equal(find(idle, ".fstats").length, 0, "an idle member is one row");
  assert.deepEqual(find(m.root, ".flabel").map((l) => text(l)), ["Lead", "Advise · design", "Implement", "Review"], "ah2-373 R6: no Test or Sub-orchestrators column with no card");
  assert.equal(button(m.root, "Flow").getAttribute("aria-pressed"), "true");
});

test("Flow remembers its choice, and Shift+M steps Map → Radial → Flow → Map (a key outside the Graph does nothing)", async () => {
  const kept = fake();
  const m = await mountGraph();
  const press = (k) => globalKey({ key: k, target: globalThis.document.body, preventDefault() {} }, m.store, globalThis.document);
  assert.equal(press("M"), true);
  await until(() => find(m.root, ".gradial").length === 1, "Radial by key");
  assert.equal(JSON.parse(kept.get("ah-graph")).mode, "radial");
  assert.equal(press("M"), true);
  await until(() => find(m.root, ".gflow").length === 1, "Flow by key");
  assert.equal(JSON.parse(kept.get("ah-graph")).mode, "flow");
  assert.equal(press("M"), true);
  await until(() => find(m.root, ".gpack").length === 1, "Map by key");
  assert.equal(JSON.parse(kept.get("ah-graph")).mode, "map");
  m.store.setUi({ view: "overview" });
  assert.equal(press("M"), false, "on another tab the key is not the page's");
  assert.equal(JSON.parse(kept.get("ah-graph")).mode, "map");
});

test("a page started with Flow saved comes up in Flow", async () => {
  fake(JSON.stringify({ mode: "flow", orient: "horizontal", legend: true }));
  const m = await mountGraph();
  assert.equal(find(m.root, ".gflow").length, 1);
  assert.equal(button(m.root, "Map").getAttribute("aria-pressed"), "false");
});

test("Horizontal ⇄ Vertical turns Flow's columns into rows and is remembered", async () => {
  const kept = fake(JSON.stringify({ mode: "flow", orient: "horizontal", legend: true }));
  const m = await mountGraph();
  const pos = (name) => {
    const { style } = find(m.root, ".fcard").find((c) => c.getAttribute("data-id") === member(name));
    const [, left, top] = /translate\(([-\d.]+)px, ([-\d.]+)px\)/.exec(style.transform).map(Number);
    return { left, top };
  };
  assert.ok(pos("@orchestrator").left < pos("reviewer").left);
  click(button(m.root, "Switch between horizontal and vertical"));
  await until(() => JSON.parse(kept.get("ah-graph")).orient === "vertical", "vertical remembered");
  await settle();
  assert.ok(pos("@orchestrator").top < pos("reviewer").top);
});

test("a blocked store still shows Map and lets the choice change for this page", async () => {
  Object.defineProperty(globalThis, "localStorage", { get() { throw new Error("blocked"); }, configurable: true });
  const m = await mountGraph();
  assert.equal(find(m.root, ".gpack").length, 1);
  click(button(m.root, "Flow"));
  await until(() => find(m.root, ".gflow").length === 1, "Flow with no storage");
});

test("Shift+F fits the graph again after a zoom; on another tab the key is not taken", async () => {
  fake(JSON.stringify({ mode: "flow", orient: "horizontal", legend: true }));
  const m = await mountGraph();
  click(button(m.root, "Zoom in"));
  await until(() => button(m.root, "Fit").getAttribute("aria-pressed") === "false", "zoomed");
  const press = (k) => globalKey({ key: k, target: globalThis.document.body, preventDefault() {} }, m.store, globalThis.document);
  assert.equal(press("F"), true);
  await until(() => button(m.root, "Fit").getAttribute("aria-pressed") === "true", "fitted by key");
  m.store.setUi({ view: "timeline" });
  assert.equal(press("F"), false);
});

test("the dots and the labels stop with reduced motion, Pause motion and an off-screen graph", () => {
  assert.match(css, /@media \(prefers-reduced-motion: reduce\) \{ \.r6 \.graph \.dot \{ animation: none; \} \}/);
  assert.match(css, /\.r6\.motion-off \.graph \.dot, \.r6 \.graph\.offscreen \.dot \{ animation: none; \}/);
  assert.doesNotMatch(css, /transition:/, "no transition of its own that would outlive a paused page");
  // ah2-373 R8: Flow's enter, move and leave transitions (r6.css, with the other springs) are instant without motion
  const r6 = readFileSync(new URL("../../web/ui/r6.css", import.meta.url), "utf8");
  assert.match(r6, /\.r6\.motion-off \.flow-world :is\(\.fcard, \.flane, \.flabel\) \{ transition: none; \}/);
  assert.match(r6, /@media \(prefers-reduced-motion: reduce\) \{\n  \.r6 \.flow-world :is\(\.fcard, \.flane, \.flabel\) \{ transition: none; \}/);
});

test("the Graph's own sheet uses tokens and no literal colour", () => {
  assert.deepEqual(css.match(/#[0-9a-fA-F]{3,8}\b/g) ?? [], []);
  assert.deepEqual(css.match(/\brgba?\(/g) ?? [], []);
});

test("a member's graph name drops its team, which every member shares, and keeps role and number", () => {
  const id = (team, name) => `member:d/${team}/${name}`;
  assert.equal(G.shortName(id("ah2-tm4", "ah2-tm4-implementor-89")), "implementor-89");
  assert.equal(G.shortName(id("ah2-tm4", "ah2-tm4-implementor-96")), "implementor-96");
  assert.equal(G.shortName(id("ah2-tm4", "@orchestrator")), "orchestrator");
  assert.equal(G.shortName(id("ship", "reviewer")), "reviewer", "no prefix: as it is");
  assert.equal(G.shortName(id("ship", "ship-")), "ship-", "never an empty name");
  assert.equal(G.shortName(id("ship", "shipper-1")), "shipper-1", "only the whole team name is a prefix");
});

// Lead, then an Implement column that fills the straight way across, then the Review column.
const LEAD = { x: 0, y: 200, w: 220, h: 64 };
const TARGET = { x: 640, y: 0, w: 220, h: 64 };
const MIDDLE = [0, 100, 200, 300].map((y) => ({ x: 320, y, w: 220, h: 64 }));
const pointsOf = (d) => [...d.matchAll(/(-?[\d.]+) (-?[\d.]+)/g)].map((p) => [Number(p[1]), Number(p[2])]);
// Whether a polyline through the path's points enters a card: its corners are close enough to the line for this check.
const crosses = (d, r) => pointsOf(d).some(([x, y], i, all) => {
  const [px, py] = all[i - 1] ?? [x, y];
  const [x0, x1, y0, y1] = [Math.min(px, x), Math.max(px, x), Math.min(py, y), Math.max(py, y)];
  return x1 > r.x && x0 < r.x + r.w && y1 > r.y && y0 < r.y + r.h;
});

test("a reporting line that a card would hide goes round it, so it does not read as starting from that card", () => {
  const cards = [LEAD, TARGET, ...MIDDLE];
  const d = G.flowPath(LEAD, TARGET, "horizontal", cards);
  assert.match(d, /^M 220 232 /, "it leaves the lead's edge");
  assert.match(d, / L 640 32$/, "and arrives at the reviewer's");
  MIDDLE.forEach((c) => assert.equal(crosses(d, c), false, `clear of the card at y ${c.y}`));
  const below = { x: 640, y: 300, w: 220, h: 64 };
  const under = G.flowPath(LEAD, below, "horizontal", [LEAD, below, ...MIDDLE]);
  MIDDLE.forEach((c) => assert.equal(crosses(under, c), false, `below too: clear of the card at y ${c.y}`));
  const clear = G.flowPath(LEAD, TARGET, "horizontal", [LEAD, TARGET]);
  assert.match(clear, / C /, "nothing in the way: the plain curve");
});

test("the same line goes round when Flow is vertical", () => {
  const swap = (r) => ({ x: r.y, y: r.x, w: r.h, h: r.w });
  const [a, b] = [swap(LEAD), swap(TARGET)];
  const middle = MIDDLE.map(swap);
  const d = G.flowPath(a, b, "vertical", [a, b, ...middle]);
  middle.forEach((c) => assert.equal(crosses(d, c), false, `clear of the card at x ${c.x}`));
  assert.match(d, /^M 232 220 /, "it leaves the lead's edge");
  assert.match(d, / L 32 640$/, "and arrives at the reviewer's");
});

test("Flow's path commands (flow-model.js, shared with the Herdr mod) serialise to the very d strings the web drew before the move", async () => {
  const { flowCmds, pathText } = await import("../../web/ui/flow-model.js");
  const swap = (r) => ({ x: r.y, y: r.x, w: r.h, h: r.w });
  const [a, b] = [swap(LEAD), swap(TARGET)];
  const cases = [
    [[{ x: 0, y: 0, w: 100, h: 40 }, { x: 300, y: 100, w: 100, h: 40 }, "horizontal"], "M 100 20 C 200 20 200 120 300 120"],
    [[{ x: 0, y: 0, w: 100, h: 40 }, { x: 0, y: 200, w: 100, h: 40 }, "vertical"], "M 50 40 C 50 120 50 120 50 200"],
    [[LEAD, TARGET, "horizontal", [LEAD, TARGET, ...MIDDLE]], "M 220 232 L 236 232 Q 244 232 244 224 L 244 -1 Q 244 -9 252 -9 L 608 -9 Q 616 -9 616 -1 L 616 24 Q 616 32 624 32 L 640 32"],
    [[a, b, "vertical", [a, b, ...MIDDLE.map(swap)]], "M 232 220 L 232 236 Q 232 244 224 244 L -1 244 Q -9 244 -9 252 L -9 608 Q -9 616 -1 616 L 24 616 Q 32 616 32 624 L 32 640"],
  ];
  for (const [args, d] of cases) {
    const cmds = flowCmds(...args);
    assert.equal(pathText(cmds), d);
    assert.equal(G.flowPath(...args), d);
    assert.equal(cmds.some((c) => c[0] === "Q"), args.length > 3, "a railed link turns its corners with Q; a plain one does not");
  }
});

// One FlowCard on the slices a member's state comes from: `served` is its row in the team summary, `activity` its activity slice.
async function flowCard(name, { role = "implementor", served, activity, team = "ah2-tm4", pipeline = {} }) {
  const { installDom, settle: drawn } = await import("./dom.mjs");
  const { render, html } = await import("../../web/ui/h.js");
  const id = `member:d/${team}/${name}`;
  const teamBody = { name: team, dir_id: "d", orchestrator: `member:d/${team}/@orchestrator`, members: [id], pipeline };
  const slices = new Map([
    [`team:d/${team}`, { id: `team:d/${team}`, body: teamBody }],
    [id, { id, body: { name, role, activity: activity ? "activity:a" : null, last_exchanges: [] } }],
    [`team-summary:d/${team}`, { id: `team-summary:d/${team}`, body: { member_states: { [id]: served } } }],
    ...(activity ? [["activity:a", { id: "activity:a", as_of: activity.as_of, body: { activity: served.state, tool: activity.tool ?? null, tool_at: null } }]] : []),
  ]);
  const store = { state: { slices, ui: {} }, subscribe: () => () => {}, lease: () => {} };
  const root = installDom();
  render(html`<${G.FlowCard} store=${store} teamId=${`team:d/${team}`} id=${id} rect=${{ x: 0, y: 0, w: 220, h: 64 }} selected=${false} onSelect=${() => {}} out=${2} />`, root);
  await drawn();
  return find(root, ".fcard")[0];
}

test("a working card names the member by role and number and shows its last activity, not a brief's age, and no empty field", async () => {
  const card = await flowCard("ah2-tm4-implementor-89", { served: { state: "working", liveness: "live", state_at: new Date(Date.now() - 83 * 60_000).toISOString() }, activity: { as_of: Date.now() - 5000, tool: "Bash" } });
  assert.equal(card.getAttribute("aria-label"), "implementor-89, Working");
  assert.match(text(card), /implementor-89/);
  assert.doesNotMatch(text(card), /ah2-tm4|—|turns/);
  assert.match(text(card), /Bash/);
  assert.equal(find(card, ".fstats").length, 1, "the last activity is the one field known");
});

test("a working card with nothing known of it draws no stats row", async () => {
  const card = await flowCard("implementor-2", { served: { state: "working", liveness: "live" } });
  assert.equal(find(card, ".fstats").length, 0);
});

test("an orchestrator the Overview calls Running is a Running card, with no time: its record is silent through its turns", async () => {
  const card = await flowCard("@orchestrator", { role: "orchestrator", served: { state: "idle", liveness: "live" }, activity: { as_of: Date.now() - 8 * 3_600_000 } });
  assert.equal(card.getAttribute("aria-label"), "orchestrator, Running");
  assert.match(card.getAttribute("class"), /active/);
  assert.match(text(card), /running/);
  assert.doesNotMatch(text(card), /ago/);
  const dead = await flowCard("@orchestrator", { role: "orchestrator", served: { state: "idle", liveness: "unknown" } });
  assert.equal(dead.getAttribute("aria-label"), "orchestrator, Idle", "not live: the rule does not hold");
});

test("no signal is not idle: the row says so, with its own glyph", async () => {
  const card = await flowCard("implementor-7", { served: { state: "unknown", liveness: "unknown" }, activity: { as_of: Date.now() - 3_600_000 } });
  assert.equal(card.getAttribute("aria-label"), "implementor-7, No signal");
  assert.match(text(card), /\?/);
  assert.match(text(card), /no signal/);
});
