import { test, afterEach } from "node:test";
import assert from "node:assert/strict";

// The Board as a kanban (ah-ui-ia U4): lane partition, Backlog column, item deep link, markers, and the mounted board on the mock's
// `lanes` scenario.
process.env.AH_MOCK_FIXTURE = "lanes";
process.env.AH_MOCK_EPICS = "1";
const kanban = await import("../../web/ui/board-kanban.js");
const { lanesOf, laneOfCard, partition, chainOf, itemTarget, markerOf, stepZoom, clampZoom, wheelZoom } = kanban;
const { queueRows } = await import("../../web/ui/plans-model.js");
const { servedBacklog } = await import("../../web/mock/model.mjs");
// The backlog column of a body as the daemon serves it, with these cards' parents planned.
const backlogColumn = (body, cards, root) => kanban.backlogColumn(servedBacklog(body, cards.map((c) => c.parent).filter(Boolean)), root);
const fixture = await import("../../web/mock/fixture.mjs");
const { mountApp: mount, until, find, text, DIR } = await import("./mount-app.mjs");

const NOW = Date.now();
const slices = new Map(fixture.buildSlices(NOW).map((s) => [s.id, s]));
const MANAGER = `${DIR}/ship`;
// Every plan item of the fixture as the card fields the partition reads.
const cards = [...slices.values()].filter((s) => s.id.startsWith("plan-items:")).flatMap((s) => {
  const [dir, team, stream] = s.id.slice("plan-items:".length).split("/");
  return s.body.items.map((i) => ({ id: `${stream}|${i.key}`, key: i.key.slice(stream.length + 1), dirId: dir, team, stream, parent: i.parent ?? null, column: "next" }));
});
const lanes = lanesOf(slices.get(`team:${MANAGER}`).body);

test("the served counts: one team, else every team that is not quiet", () => {
  const sum = (id, waiting_on_you, in_progress_now, quiet_at = null) => [`team-summary:${id}`, { id: `team-summary:${id}`, body: { waiting_on_you, in_progress_now, quiet_at } }];
  const held = new Map([sum("d/a", 2, 1), sum("d/b", 1, 3), sum("d/quiet", 5, 5, "2026-01-01T00:00:00Z"), ["team:d/a", { id: "team:d/a", body: { waiting_on_you: 9 } }]]);
  const now = Date.parse("2026-06-01T00:00:00Z");
  assert.deepEqual(kanban.servedCounts(held, null, now), { waiting: 3, progress: 4, pending: false });
  assert.deepEqual(kanban.servedCounts(held, "team:d/b", now), { waiting: 1, progress: 3, pending: false });
});

test("a plan on show not yet read marks the counts, and negative: a plan read, or one on a team not on show, does not", () => {
  const sum = (id, plan_loaded) => [`team-summary:${id}`, { id: `team-summary:${id}`, body: { waiting_on_you: 0, in_progress_now: 0, quiet_at: null, plan_loaded } }];
  const held = new Map([sum("d/a", true), sum("d/b", false)]);
  const now = Date.parse("2026-06-01T00:00:00Z");
  assert.equal(kanban.servedCounts(held, null, now).pending, true);
  assert.equal(kanban.servedCounts(held, "team:d/b", now).pending, true);
  assert.equal(kanban.servedCounts(held, "team:d/a", now).pending, false);
  const quiet = new Map([sum("d/a", true), [`team-summary:d/q`, { id: `team-summary:d/q`, body: { waiting_on_you: 0, in_progress_now: 0, quiet_at: "2026-01-01T00:00:00Z", plan_loaded: false } }]]);
  assert.equal(kanban.servedCounts(quiet, null, now).pending, true, "an unread team reads as quiet, and may hold the cards");
  assert.equal(kanban.servedCounts(new Map([sum("d/a", true)]), null, now).pending, false);
});

test("lanes come from the manager's streams; the partition is disjoint and All is Top plus every lane", () => {
  assert.deepEqual(lanes.map((l) => [l.stream, l.child]), [["lane-board", `team:${DIR}/ship-lane-board`], ["lane-delivery", `team:${DIR}/ship-lane-delivery`]]);
  const all = partition(cards, lanes, MANAGER, null);
  const top = partition(cards, lanes, MANAGER, "top");
  const each = lanes.map((l) => partition(cards, lanes, MANAGER, l.stream));
  const ids = [top, ...each].flatMap((cs) => cs.map((c) => c.id));
  assert.equal(new Set(ids).size, ids.length, "disjoint");
  assert.deepEqual([...ids].sort(), all.map((c) => c.id).sort(), "All = Top ∪ lanes");
  const fixtureCount = [...slices.values()].filter((s) => s.id.startsWith("plan-items:")).reduce((n, s) => n + s.body.items.length, 0);
  assert.equal(all.length, fixtureCount);
  assert.ok(top.length > 0 && each.every((cs) => cs.length > 0), "each part has the fixture's items");
  assert.ok(top.every((c) => c.stream === "merge-train"));
  // A child team's own card is its lane's, whatever its stream.
  assert.equal(laneOfCard({ dirId: DIR, team: "ship-lane-board", stream: "anything" }, lanes, MANAGER), "lane-board");
  // An unconfirmed lane (no child team) holds only the manager's items in its stream.
  const unconfirmed = lanesOf({ dir_id: DIR, streams: [{ name: "lane-x", sub_orchestrator: "so-x", state: "open" }] });
  assert.equal(unconfirmed[0].child, null);
  assert.equal(laneOfCard({ dirId: DIR, team: "ship", stream: "lane-x" }, unconfirmed, MANAGER), "lane-x");
});

test("Backlog column: a row with a planned child is absent from the column and present as a tier; rows go by wave", () => {
  const body = slices.get(`backlog:${DIR}`).body;
  const bl = new Map(body.items.map((r) => [r.key, r]));
  const planned = cards.find((c) => c.parent && bl.has(c.parent));
  assert.ok(planned, "the fixture has a planned item");
  const shown = backlogColumn(body, cards).flatMap((g) => g.rows.map((r) => r.key));
  assert.ok(!shown.includes(planned.parent), "its parent is not in Backlog");
  const { deliverable, epic } = chainOf(planned, bl);
  assert.ok(deliverable || epic, "it is drawn under its tier");
  for (const k of [deliverable?.key, epic?.key].filter(Boolean)) {
    assert.ok(!shown.includes(k), `${k} is a tier, not a Backlog row`);
  }
  // With no plan items at all, every open row is in Backlog, by wave order.
  const groups = backlogColumn(body, []);
  const open = body.items.filter((r) => r.state !== "done" && r.state !== "dropped").length;
  assert.equal(groups.reduce((n, g) => n + g.rows.length, 0), open);
  const order = ["now", "next", "later", "untriaged"];
  assert.deepEqual(groups.map((g) => g.wave), order.filter((w) => groups.some((g) => g.wave === w)));
  // Under an epic, only its descendants.
  const e = body.items.find((r) => r.kind === "epic");
  const under = backlogColumn(body, [], e.key).flatMap((g) => g.rows);
  assert.ok(under.every((r) => r.key !== e.key));
});

test("item= takes a plan key, bare or stream-qualified, or an exchange id through the exchange's item; else null", () => {
  const c = cards[0];
  assert.equal(itemTarget(cards, c.key, new Map()), c.id);
  assert.equal(itemTarget(cards, `${c.stream}:${c.key}`, new Map()), c.id);
  const ex = new Map([["exchange:d/1", { body: { item: c.key } }], ["exchange:d/2", { body: {} }]]);
  assert.equal(itemTarget(cards, "exchange:d/1", ex), c.id);
  assert.equal(itemTarget(cards, "exchange:d/2", ex), null);
  assert.equal(itemTarget(cards, "nope", ex), null);
});

test("markers: blocked since; a Next card's wait, stalled only past stall_at with an idle eligible member", () => {
  const H = 3_600_000;
  const row = (over) => queueRows([{ item: "s/a", role: "implementor", queued_since: new Date(NOW - 46 * H).toISOString(), stall_at: new Date(NOW - H).toISOString(), eligible_members: ["implementor-1"], idle_eligible: true, ...over }], NOW)[0];
  const next = { state: "next", column: "next" };
  assert.equal(markerOf(next, row({}), NOW).text, "■ stalled 46 h · no owner, 1 idle");
  assert.equal(markerOf(next, row({ eligible_members: [], idle_eligible: false }), NOW).text, "◷ waiting 46 h · no idle implementor");
  assert.equal(markerOf(next, row({ stall_at: new Date(NOW + H).toISOString() }), NOW).text, "◷ waiting 46 h");
  assert.equal(markerOf({ state: "blocked", column: "progress", since: NOW - 3 * H }, null, NOW).text, "▲ blocked 3 h");
  assert.equal(markerOf({ state: "in_progress", column: "progress" }, null, NOW), null);
});

// ---- mounted

let fake = null;
const useStorage = (init) => {
  fake = { data: { ...init }, getItem(k) { return this.data[k] ?? null; }, setItem(k, v) { this.data[k] = v; } };
  Object.defineProperty(globalThis, "localStorage", { value: fake, configurable: true });
};
afterEach(() => {
  delete globalThis.localStorage;
  fake = null;
});
const kcards = (m) => find(m.root, "article.kb-card");
const heads = (m) => find(m.root, "div.kb-head");

test("the kanban is the Board's only look: each column's head counts the cards drawn in it, a card carries its lane, no group rows", async () => {
  useStorage({ "ah.plans.layout": "board", "ah.board.variant": "icicle" });
  const m = await mount({ view: "board" });
  await until(() => kcards(m).length > 0 && find(m.root, "section.kb-blcol").length === 1, "the kanban");
  assert.equal(find(m.root, "div.pl-ice").length, 0, "no icicle, even with the old choice stored");
  assert.ok(!find(m.root, "button").some((b) => ["Kanban", "Icicle"].includes(text(b))), "no look switch");
  const cols = find(m.root, "section.kb-col");
  assert.ok(cols.length > 1);
  for (const col of cols) {
    const label = text(col.all("span.kb-label")[0]);
    const n = Number(text(col.all("span.kb-count")[0]).split(" ")[0]);
    const got = col.all("article.kb-card").length;
    // Waiting on you and In progress head with the served counts, as in the TUI; see servedCounts below.
    assert.ok(n === got || ["Waiting on you", "In progress"].includes(label), `${label}: head ${n}, drawn ${got}`);
  }
  assert.equal(find(m.root, "div.kb-grp").length + find(m.root, "div.kb-cell").length, 0, "no swimlane or epic rows");
  assert.equal(find(m.root, "div.plans-bar").length, 0, "one bar: the List's filters are in the board's bar");
  const bar = find(m.root, "div.kb-bar")[0];
  for (const want of ["Board", "Changed since last visit", "Blocked only", "Zoom in"]) {
    assert.ok(bar.all("button").some((b) => text(b) === want || b.getAttribute("aria-label") === want), `${want} in the bar`);
  }
  assert.equal(bar.all("nav.kb-crumbs").length, 1, "the breadcrumb in the bar");
  const lanes = [...new Set(find(m.root, "span.kb-where").filter((x) => x.getAttribute("class").includes("is-lane")).map(text))].sort();
  assert.deepEqual(lanes, ["ship-lane-board", "ship-lane-delivery"]);
  assert.ok(!kcards(m).some((c) => c.all("span.pl-chip").length > 0), "no state chip repeats the column");
});

test("the zoom steps by 10% marks within 50% to 150%", () => {
  assert.equal(stepZoom(1, 1), 1.1);
  assert.equal(stepZoom(1, -1), 0.9);
  assert.equal(stepZoom(0.95, 1), 1);
  assert.equal(stepZoom(0.95, -1), 0.9);
  assert.equal(stepZoom(1.5, 1), 1.5);
  assert.equal(stepZoom(0.5, -1), 0.5);
  assert.equal(clampZoom(3), 1.5);
  assert.equal(clampZoom(0.123), 0.5);
});

test("a run of small Ctrl-wheel deltas (a trackpad pinch) changes the zoom; nothing is lost to rounding", () => {
  let z = 1;
  for (let i = 0; i < 20; i++) {
    z = wheelZoom(z, -2);
  }
  assert.ok(z > 1.03 && z < 1.05, `20 pinch steps of -2: ${z}`);
  let back = z;
  for (let i = 0; i < 20; i++) {
    back = wheelZoom(back, 2);
  }
  assert.ok(Math.abs(back - 1) < 1e-9, `and back: ${back}`);
  assert.equal(wheelZoom(1.49, -1000), 1.5);
});

test("the breadcrumb scopes to a lane with its header; item= opens the panel, or says it is not on the board", async () => {
  useStorage({ "ah.plans.layout": "board" });
  const m = await mount({ view: "board" });
  await until(() => kcards(m).length > 0, "the kanban");
  const before = kcards(m).length;
  m.store.setUi({ lane: "lane-board" });
  await until(() => find(m.root, "p.kb-lanehead").length === 1, "the lane header");
  assert.match(text(find(m.root, "p.kb-lanehead")[0]), /lane-board.*so-lane-board.*ah\/lane-board → main/);
  assert.ok(kcards(m).length < before);
  assert.ok(find(m.root, "nav.kb-crumbs")[0].all("li").map(text).join(" ").includes("lane-board"));
  m.store.setUi({ lane: null, item: "lb1" });
  await until(() => find(m.root, "section.kb-panel").length === 1, "the item's panel");
  m.store.setUi({ item: "no-such-item", boardSel: null });
  await until(() => find(m.root, "p.kb-note").some((p) => /no-such-item is not on this board/.test(text(p))), "the note");
});

// ---- the List (§2.5), the icicle's lane cut and item focus, the kanban's history

test("the List: C4 header, Backlog first, one row per open item, a column empty everywhere is hidden, the lane filter cuts streams", async () => {
  useStorage({ "ah.plans.layout": "list" });
  const m = await mount({ view: "board" });
  await until(() => find(m.root, "tr.pl-irow").length > 0 && find(m.root, "p.pl-c4").length === 1, "the item rows");
  const open = [...m.store.state.slices.values()].filter((x) => x.id.startsWith("plan-items:")).flatMap((x) => x.body.items).filter((i) => !["done", "dropped"].includes(i.state));
  const itemRows = () => find(m.root, "table.pl-itable").filter((t) => !t.getAttribute("class").includes("pl-btable")).flatMap((t) => t.all("tr.pl-irow"));
  assert.equal(itemRows().length, open.length, "every open item, once");
  assert.match(text(find(m.root, "p.pl-c4")[0]), new RegExp(`^${open.length} open items · .*\\d+ dispatched · \\d+ queued`));
  const sections = find(m.root, "section").map((x) => x.getAttribute("class"));
  assert.ok(sections.indexOf("pl-backlog") < sections.indexOf("pl-group"), "Backlog first");
  // C1: a shown column has a value in some row.
  for (const t of find(m.root, "table.pl-itable").filter((x) => !x.getAttribute("class").includes("pl-btable"))) {
    const heads = t.all("th").map(text);
    heads.forEach((h, i) => assert.ok(itemRows().some((r) => text(r.all("td")[i] ?? { textContent: "—" }) !== "—"), `${h} has a value`));
  }
  assert.ok(!find(m.root, "th").some((h) => /Waiting|Could take it/.test(text(h))), "no queued row, no queue columns");
  m.store.setUi({ lane: "lane-board" });
  await until(() => rows(m).length === 1, "the lane's stream only");
  assert.match(text(rows(m)[0]), /lane-board/);
});

const rows = (m) => find(m.root, "li.pl-row");

test("a queued item in the List: owner 'waiting for <role>' when nobody holds it, and the Waiting and Could take it columns", async () => {
  useStorage({ "ah.plans.layout": "list" });
  const m = await mount({ view: "board" });
  await until(() => find(m.root, "tr.pl-irow").length > 0, "the item rows");
  const id = `plan-items:${DIR}/ship/merge-train`;
  const body = JSON.parse(JSON.stringify(m.store.state.slices.get(id).body));
  body.items.push({ key: "merge-train:mtq", title: "Queued thing", state: "next" });
  const H = 3_600_000;
  m.model.publish([
    { id, as_of: Date.now(), body },
    { id: `queue:${DIR}/ship`, as_of: Date.now(), body: [{ item: "merge-train/mtq", role: "implementor", queued_since: new Date(Date.now() - 46 * H).toISOString(), stall_at: new Date(Date.now() - H).toISOString(), eligible_members: ["implementor-1"], last_dispatch_at: null, title: "Queued thing", idle_eligible: true }] },
  ]);
  const row = () => find(m.root, "tr.pl-irow").find((r) => text(r).includes("Queued thing"));
  await until(() => row() && /stalled 46 h/.test(text(row())), "the queued row");
  assert.match(text(row()), /waiting for implementor/);
  assert.match(text(row()), /implementor-1/);
  assert.ok(find(m.root, "th").some((h) => text(h) === "Could take it"));
});

test("the kanban panel shows the item's history and its lane, which opens the lane's panel", async () => {
  useStorage({ "ah.plans.layout": "board" });
  const k = await mount({ view: "board" });
  await until(() => kcards(k).length > 0, "the kanban");
  k.store.setUi({ item: "lb1" });
  await until(() => find(k.root, "section.kb-panel").some((a) => a.all("h3").some((h) => text(h) === "History")), "the history block");
  const panel = find(k.root, "section.kb-panel")[0];
  const dts = panel.all("dt").map(text);
  assert.ok(dts.includes("Lane"), "the lane, as its swimlane row named it");
  const lane = panel.all("button.kb-link")[0];
  assert.equal(text(lane), "ship-lane-board");
  lane.fire("click");
  await until(() => find(k.root, "section.kb-panel").some((a) => /^Lane/.test(text(a.all("p.pl-where")[0]))), "the lane's panel");
});

// ---- review round 1

const queueRow = (over = {}) => {
  const H = 3_600_000;
  return { item: "merge-train/mtq", role: "implementor", queued_since: new Date(Date.now() - 46 * H).toISOString(), stall_at: new Date(Date.now() + H).toISOString(), eligible_members: [], last_dispatch_at: new Date(Date.now() - 2 * H).toISOString(), title: "Queued thing", idle_eligible: false, ...over };
};
const withQueued = async (row) => {
  useStorage({ "ah.plans.layout": "list" });
  const m = await mount({ view: "board" });
  await until(() => find(m.root, "tr.pl-irow").length > 0, "the item rows");
  const id = `plan-items:${DIR}/ship/merge-train`;
  const body = JSON.parse(JSON.stringify(m.store.state.slices.get(id).body));
  body.items.push({ key: "merge-train:mtq", title: "Queued thing", state: "next" });
  m.model.publish([{ id, as_of: Date.now(), body }, { id: `queue:${DIR}/ship`, as_of: Date.now(), body: [row] }]);
  const tr = () => find(m.root, "tr.pl-irow").find((r) => text(r).includes("Queued thing"));
  await until(() => tr() && find(m.root, "th").some((h) => text(h) === "Waiting"), "the queued row");
  return { m, tr };
};

test("finding 1: a queued row shows 'Last sent to <role>', and 'never' when nothing was sent", async () => {
  const { m, tr } = await withQueued(queueRow());
  assert.ok(find(m.root, "th").some((h) => text(h) === "Last sent to implementor"), "the heading names the role");
  assert.match(text(tr()), /2 h ago/);
  const again = await withQueued(queueRow({ last_dispatch_at: null }));
  assert.match(text(again.tr()), /never/);
});

test("finding 4: Waiting counts down to the stall time, and says when none is served", async () => {
  const { tr } = await withQueued(queueRow());
  assert.match(text(tr()), /waiting 46 h · stalls in 1 h/);
  const none = await withQueued(queueRow({ stall_at: null }));
  assert.match(text(none.tr()), /waiting 46 h · no stall time/);
});

test("finding 2: the List's 'Work of' select lists a finished lane once Show finished is on", async () => {
  useStorage({ "ah.plans.layout": "list" });
  const m = await mount({ view: "board" });
  await until(() => find(m.root, "label.plans-hide").length > 0, "the Work of select");
  const id = `team:${DIR}/ship`;
  const body = JSON.parse(JSON.stringify(m.store.state.slices.get(id).body));
  body.streams[0].state = "done";
  m.model.publish([{ id, as_of: Date.now(), body }]);
  const options = () => find(m.root, "label.plans-hide")[0]?.all("option").map(text) ?? [];
  await until(() => !options().includes(body.streams[0].name), "the finished lane hidden");
  const toggle = () => find(m.root, "p.pl-foot")[0]?.all("button")[0];
  await until(() => toggle(), "the Show finished toggle");
  toggle().fire("click");
  await until(() => options().includes(body.streams[0].name), "the finished lane offered");
});

test("finding 3: item= naming an exchange the daemon removed says it is not on the board", async () => {
  useStorage({ "ah.plans.layout": "board" });
  const m = await mount({ view: "board" });
  await until(() => kcards(m).length > 0, "the kanban");
  const ex = `exchange:${DIR}/20991231-0000-gone`;
  m.store.setUi({ item: ex });
  await until(() => m.log.polls.at(-1)?.includes(ex), "the exchange leased");
  // Served, it maps to its item; then the daemon removes it, so it is gone, not merely unheld.
  m.model.publish([{ id: ex, as_of: Date.now(), body: { item: "lb1" } }]);
  await until(() => find(m.root, "section.kb-panel").length === 1, "mapped to its item");
  m.store.setUi({ boardSel: null });
  m.model.publish([], [ex]);
  await until(() => m.store.state.gone?.has(ex), "the removal seen");
  await until(() => find(m.root, "p.kb-note").some((p) => text(p).includes("is not on this board")), "the note");
});

test("finding 5: the kanban has an Owner control that sets the owner filter", async () => {
  useStorage({ "ah.plans.layout": "board" });
  const m = await mount({ view: "board" });
  await until(() => kcards(m).length > 0, "the kanban");
  const sel = () => find(m.root, "label.kb-owner")[0]?.all("select")[0];
  await until(() => sel(), "the Owner select");
  const owner = sel().all("option").map((o) => o.getAttribute("value")).find(Boolean);
  const before = kcards(m).length;
  sel().fire("change", { target: { value: owner } });
  await until(() => m.store.state.ui.owner === owner && kcards(m).length < before, "filtered");
});

test("finding 10: deliverable= without epic= is dropped", async () => {
  const { uiOf } = await import("../../web/ui/deeplink.js");
  assert.equal(uiOf(`#/board?team=${encodeURIComponent(`team:${DIR}/ship`)}&deliverable=b2`).deliverable, null);
  assert.equal(uiOf(`#/board?team=${encodeURIComponent(`team:${DIR}/ship`)}&epic=b1&deliverable=b2`).deliverable, "b2");
  assert.equal(uiOf("#/board?team=all&deliverable=b2").deliverable, null);
});

test("finding 9: item= in the List marks and scrolls its row, and does not switch to the Board", async () => {
  useStorage({ "ah.plans.layout": "list" });
  const m = await mount({ view: "board" });
  await until(() => find(m.root, "tr.pl-irow").length > 0, "the item rows");
  m.store.setUi({ item: "lb1" });
  await until(() => find(m.root, "tr.pl-irow").some((r) => r.getAttribute("aria-current") === "true"), "the row marked");
  assert.equal(find(m.root, "div.kb-wrap").length, 0, "still the List");
  m.store.setUi({ item: "no-such" });
  await until(() => find(m.root, "p.pl-c4").some((p) => /no-such is not in this list/.test(text(p))), "the note");
});

// S2, S3 (ah-ui-answer §5, §6): a waiting item's card links to the Inbox until its row is held, then answers or dismisses it in place.
test("a waiting item card answers in place once the questions slice holds it; Already done dismisses it through the answer route", async () => {
  useStorage({ "ah.plans.layout": "board" });
  const { answerReply } = await import("../../web/mock/questions.mjs");
  const sent = [];
  const saved = globalThis.fetch;
  globalThis.fetch = async (_, init) => {
    sent.push(JSON.parse(init.body));
    const [status, reply] = answerReply(sent.at(-1));
    return { status, json: async () => reply };
  };
  try {
    const m = await mount({ view: "board" });
    await until(() => kcards(m).length > 0, "the kanban");
    const pid = [...m.store.state.slices.keys()].find((k) => k.startsWith(`plan-items:${DIR}/ship/`) && m.store.state.slices.get(k).body.items.length > 0);
    const pbody = m.store.state.slices.get(pid).body;
    const it = pbody.items[0];
    const since = "2026-11-02T11:00:00Z";
    m.model.publish([{ id: pid, as_of: Date.now(), body: { ...pbody, items: [{ ...it, state: "waiting_user", column: "waiting", state_since: since }, ...pbody.items.slice(1)] } }]);
    const mine = () => kcards(m).find((c) => text(find(c, "h3.kb-title")[0]) === it.title);
    await until(() => mine() && find(mine(), "a.kb-answer").length === 1, "the waiting card with its Inbox link");
    assert.equal(text(find(mine(), "a.kb-answer")[0]), "Answer in Inbox", "before the row is held: the link");

    const qid = `questions:${DIR}/ship`;
    await until(() => m.store.state.slices.has(qid), "the waiting card leases the team's questions");
    const qbody = m.store.state.slices.get(qid).body;
    m.model.publish([{ id: qid, as_of: Date.now(), body: { ...qbody, items: [{ key: it.key, title: it.title, text: "Which font?", options: ["Mono", "Sans"], since, owner: "docs-writer" }], waiting_total: (qbody.open_total ?? 0) + 1 } }]);
    await until(() => find(mine(), "form.qa-answer-form").length === 1, "the form on the card");
    assert.equal(find(mine(), "a.kb-answer").length, 0);
    assert.equal(text(find(mine(), "p.kb-ask")[0]), "Which font?", "the question under the item's title");
    assert.deepEqual(find(mine(), "span.qa-label").map(text), ["Mono", "Sans", "Other"]);

    find(mine(), "button").find((b) => text(b) === "Already done").fire("click");
    await until(() => find(mine(), "p.qa-progress").length === 1, "the dismissal");
    assert.deepEqual(sent, [{ dir: DIR, team: "ship", item: it.key, since, dismiss: "Already done" }], "one click, no confirm step");
    assert.equal(text(find(mine(), "span.qa-state")[0]), "Recorded");
    assert.equal(text(find(mine(), "span.qa-given")[0]), "Dismissed: Already done");
    assert.equal(find(mine(), "form").length, 0, "a dismissed card collapses to its status");
  } finally {
    globalThis.fetch = saved;
  }
});
