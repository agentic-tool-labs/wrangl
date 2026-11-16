import { test, afterEach } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

// The Plans board as an icicle (ah-graph-views §2.3, S6) with epic tiers (ah-epics §4, E3). The mock runs with its epics preset.
process.env.AH_MOCK_EPICS = "1";
const { boardRows, partsOf, rankOf, weightOf } = await import("../../web/ui/graph-world.js");
const { settle } = await import("./dom.mjs");
const { mountApp: mount, until, find, text, DIR } = await import("./mount-app.mjs");

// ---- the tree rows (pure)

const COLS = [["next", "Next"], ["progress", "In progress"], ["review", "In review"]];
const card = (key, over = {}) => ({ id: key, key, title: key.toUpperCase(), state: "not_started", dirId: "d1", team: "ship", repo: "r1", stream: "s1", owner: "impl", ...over });
const ids = (rows) => rows.map((r) => `${r.id}<${r.parent ?? ""}`).sort();
const backlogOf = (items, dir = "d1") => new Map([[dir, new Map(items.map((b) => [b.key, b]))]]);
const BACKLOG = [
  { key: "b1", title: "Epic one", kind: "epic", state: "in_progress", holder: "lead" },
  { key: "b2", title: "Deliverable", kind: "deliverable", parent: "b1", state: "in_progress" },
  { key: "b3", title: "Backlog task", parent: "b2", state: "next" },
  { key: "b9", title: "Lonely epic", kind: "epic", state: "not_started", size: "L" },
];

test("state first: the root splits into every column in order, an empty one kept with no weight; team level has no team tier", () => {
  const rows = boardRows({ columns: COLS, cells: { next: [card("a")], progress: [], review: [card("b", { state: "in_progress" })] }, scope: "ship" });
  assert.deepEqual(rows.filter((r) => r.kind === "column").map((r) => [r.id, r.order, r.count]), [["c:next", 0, 1], ["c:progress", 1, 0], ["c:review", 2, 1]]);
  assert.equal(rows.find((r) => r.id === "c:progress").weight, 0);
  assert.deepEqual(ids(rows), ["c:next<root", "c:progress<root", "c:review<root", "next#i:d1/ship|s1|a<next#s:d1/ship|s1", "next#s:d1/ship|s1<c:next", "review#i:d1/ship|s1|b<review#s:d1/ship|s1", "review#s:d1/ship|s1<c:review", "root<"]);
  assert.equal(rows.find((r) => r.id === "root").name, "ship");
});

test("Show epics off, or a backlog with no epic or deliverable, is the plain tree: column → team → stream → item", () => {
  const cells = { next: [card("a", { parent: "b3" })] };
  const plain = boardRows({ columns: COLS, cells, backlogs: null });
  assert.deepEqual(boardRows({ columns: COLS, cells, backlogs: backlogOf([{ key: "b3", title: "t", state: "next" }]) }), plain);
  assert.deepEqual(ids(plain).filter((x) => x.startsWith("next#")), ["next#i:d1/ship|s1|a<next#s:d1/ship|s1", "next#s:d1/ship|s1<next#t:d1/ship", "next#t:d1/ship<c:next"]);
});

test("epics nest inside the team: team → epic → deliverable → item; a backlog task is never a tile", () => {
  const rows = boardRows({ columns: COLS, cells: { next: [card("a", { parent: "b2" }), card("c")] }, backlogs: backlogOf(BACKLOG) });
  const by = new Map(rows.map((r) => [r.id, r]));
  assert.equal(by.get("next#b:d1/ship|b1").parent, "next#t:d1/ship");
  assert.equal(by.get("next#b:d1/ship|b2").parent, "next#b:d1/ship|b1");
  assert.equal(by.get("next#i:d1/ship|s1|a").parent, "next#b:d1/ship|b2");
  assert.equal(by.get("next#i:d1/ship|s1|c").parent, "next#s:d1/ship|s1", "an item with no parent stays under its stream");
  assert.equal(by.get("next#b:d1/ship|b1").holder, "lead");
  assert.ok(!rows.some((r) => /b3|b9/.test(r.id)), "no backlog task, and no epic without items of the team");
});

test("an invalid task → task link and a dangling parent fall back to the stream", () => {
  const rows = boardRows({ columns: COLS, cells: { next: [card("a", { parent: "b3" }), card("d", { parent: "b404" })] }, backlogs: backlogOf(BACKLOG) });
  assert.equal(rows.find((r) => r.ent === "i:d1/ship|s1|a").parent, "next#s:d1/ship|s1");
  assert.equal(rows.find((r) => r.ent === "i:d1/ship|s1|d").parent, "next#s:d1/ship|s1");
  assert.equal(rankOf(undefined), 1);
  assert.equal(rankOf("epic"), 3);
});

test("an epic over two teams and two columns has a tile under each team in each column, holding only those items", () => {
  const cells = { next: [card("a", { parent: "b2" }), card("x", { team: "docs", parent: "b2" })], progress: [card("b", { parent: "b2", state: "in_progress" })] };
  const rows = boardRows({ columns: COLS, cells, backlogs: backlogOf(BACKLOG) });
  assert.deepEqual(rows.filter((r) => r.ent === "b:d1/ship|b1").map((r) => r.id), ["next#b:d1/ship|b1", "progress#b:d1/ship|b1"]);
  assert.deepEqual(rows.filter((r) => r.ent === "b:d1/docs|b1").map((r) => r.parent), ["next#t:d1/docs"]);
  assert.equal(new Set(rows.map((r) => r.id)).size, rows.length, "no two rows share an id");
});

test("teams are keyed by dir and name: two dirs with a same-named team stay apart and are told apart by repo", () => {
  const rows = boardRows({ columns: COLS, cells: { next: [card("a"), card("a", { dirId: "d2", repo: "r2" })] } });
  const teams = rows.filter((r) => r.kind === "team");
  assert.deepEqual(teams.map((r) => [r.id, r.name]), [["next#t:d1/ship", "ship · r1"], ["next#t:d2/ship", "ship · r2"]]);
});

test("the stream filter applies before the Done cap, so a head's \"N of M\" counts only what the filter keeps", () => {
  const done = Array.from({ length: 5 }, (_, i) => card(`d${i}`, { state: "done", stream: i % 2 ? "s2" : "s1" }));
  const head = (rows) => rows.find((r) => r.kind === "column");
  const at = (keep) => head(boardRows({ columns: [["done", "Done today"]], cells: { done }, caps: { done: 2 }, keep }));
  assert.deepEqual([at(null).count, at(null).more], [2, 3]);
  assert.deepEqual([at((c) => c.stream === "s1").count, at((c) => c.stream === "s1").more], [2, 1], "three of s1's, two shown");
  assert.deepEqual([at((c) => c.stream === "s2").count, at((c) => c.stream === "s2").more], [2, 0], "both of s2's, none hidden");
});

test("a decision and a changed stream are leaves; sizes weigh XS 1, S 1, M 2, L 3, XL 5; entity ids survive a stream with a slash", () => {
  const rows = boardRows({ columns: COLS, cells: { next: [card("z", { decision: true, title: "Decision waiting · 2" })], progress: [card("q", { changed: true, stream: "ah/ui-r6", title: "ah/ui-r6 — changed", mark: { kind: "stream" } })] } });
  assert.equal(rows.find((r) => r.kind === "decision").weight, 1);
  assert.equal(rows.find((r) => r.id === "progress#s:d1/ship|ah/ui-r6").weight, 1);
  assert.deepEqual(["XS", "S", "M", "L", "XL", undefined, 5].map(weightOf), [1, 1, 2, 3, 5, 1, 5]);
  assert.deepEqual(partsOf("i:d1/ship|ah/ui-r6|r6a"), { kind: "i", dir: "d1", team: "ship", key: "d1/ship", stream: "ah/ui-r6", item: "r6a" });
  assert.equal(partsOf("c:next"), null);
});

// ---- the board on the mock (epics preset)

let fake = null;
const useStorage = (init) => {
  fake = { data: { ...init }, getItem(k) { return this.data[k] ?? null; }, setItem(k, v) { this.data[k] = v; } };
  Object.defineProperty(globalThis, "localStorage", { value: fake, configurable: true });
};
afterEach(() => {
  delete globalThis.localStorage;
  fake = null;
});

// A tile's press target (its name and state); .parentNode is the positioned box.
const tiles = (m) => find(m.root, "button.ice-hit");
const tile = (m, name) => tiles(m).find((b) => text(b.all("span.t")[0]) === name);
const names = (m) => tiles(m).map((b) => text(b.all("span.t")[0]));
const leased = (m) => (m.log.polls.at(-1) ?? "").split(",");
const panel = (m) => find(m.root, "aside.ice-panel")[0];

// ponytail: board-r2 made the Board kanban only, so the mounted icicle cannot be opened in the app; these tests stay, skipped, until
// the icicle's cleanup deletes the component and them.
const UNREACHABLE = { skip: "board-r2: the Board is kanban only; the icicle is unreachable until its cleanup" };

const openTeamBoard = async (store = {}) => {
  useStorage({ "ah.plans.layout": "board", "ah.board.variant": "icicle", ...store });
  const m = await mount({ view: "board" });
  await until(() => tiles(m).length > 0, "tiles");
  return m;
};

test("the team board shows epic → deliverable → item and leases backlog, plan-items and item-msgs", UNREACHABLE, async () => {
  const m = await openTeamBoard();
  await until(() => names(m).includes("R6a data core"), "the epic's checklist item");
  await until(() => tiles(m).some((b) => b.getAttribute("aria-label").startsWith("epic ")), "an epic tile");
  const ids = leased(m);
  assert.ok(ids.includes(`backlog:${DIR}`));
  assert.ok(ids.includes(`item-msgs:${DIR}/ship`));
  assert.ok(ids.includes(`plan-items:${DIR}/ship/ah/ui-r6`));
  const label = tiles(m).find((b) => b.getAttribute("aria-label").startsWith("task R6a data core")).getAttribute("aria-label");
  assert.match(label, /^task R6a data core, [a-z ]+, owner [\w-]+, \d+ messages?, level \d$/);
  assert.equal(find(m.root, "[role=tree]").length + find(m.root, "[role=treeitem]").length, 0, "no tree roles without a tree's keys");
});

test("Show epics off gives the stream tree and lets the backlog go", UNREACHABLE, async () => {
  const m = await openTeamBoard();
  await until(() => leased(m).includes(`backlog:${DIR}`), "the backlog");
  find(m.root, "button").find((b) => text(b) === "Show epics").fire("click");
  await until(() => !leased(m).includes(`backlog:${DIR}`), "the backlog released");
  assert.equal(fake.data["ah.plans.epics"], "off");
  await until(() => !tiles(m).some((b) => /^(epic|deliverable) /.test(b.getAttribute("aria-label"))), "no epic tiles");
  assert.ok(tiles(m).some((b) => b.getAttribute("aria-label").startsWith("stream ") && b.getAttribute("title").endsWith(" · ah/ui-r6")), "the stream is back as the items' parent");
});

const heads = (m) => tiles(m).filter((b) => /\bcol\b/.test(b.parentNode.className));
const stage = (m) => find(m.root, "div.ice-stage")[0];
const esc = (m) => {
  let stopped = false;
  stage(m).fire("keydown", { key: "Escape", preventDefault() {}, stopPropagation() { stopped = true; } });
  return stopped;
};
const crumbs = (m) => find(m.root, "nav.gm-crumbs")[0].all("button").map(text).join("/");

test("the columns are the board's states, in order, each with its head and count even when empty", UNREACHABLE, async () => {
  const m = await openTeamBoard({ "ah.plans.epics": "off" });
  await until(() => heads(m).length === 5, "five column heads");
  assert.deepEqual(heads(m).map((b) => text(b.all("span.t")[0])), ["Next", "In progress", "In review", "Waiting on you", "Done today"]);
  assert.ok(heads(m).every((b) => /^[\w ]+, \d+ cards?/.test(b.getAttribute("aria-label"))));
  // Changes on a first visit: nothing to compare, so no card at all.
  m.store.setUi({ plansChanges: "on" });
  await until(() => tiles(m).filter((b) => !/\bcol\b/.test(b.parentNode.className)).length === 1, "only the root left");
  assert.equal(heads(m).length, 5, "every head stays");
  assert.ok(heads(m).every((b) => /, 0 cards/.test(b.getAttribute("aria-label"))));
  await until(() => find(m.root, "p.ice-note").length === 1 && /first visit/.test(text(find(m.root, "p.ice-note")[0])), "the note");
});

test("a column head opens the column; a container is selected, then opens on a second press; a leaf toggles; Esc deselects, then goes up", UNREACHABLE, async () => {
  const m = await openTeamBoard({ "ah.plans.epics": "off" });
  await until(() => heads(m).some((b) => !/, 0 cards/.test(b.getAttribute("aria-label"))), "a column with cards");
  const head = heads(m).find((b) => !/, 0 cards/.test(b.getAttribute("aria-label")));
  const name = text(head.all("span.t")[0]);
  head.fire("click");
  await until(() => m.store.state.ui.plansFocus?.startsWith("c:"), "focused on the column");
  const col = m.store.state.ui.plansFocus;
  await until(() => crumbs(m) === `ship/${name}`, "the zoom path");
  const stream = tiles(m).find((b) => b.getAttribute("aria-label").startsWith("stream "));
  stream.fire("click");
  await until(() => m.store.state.ui.plansSel?.startsWith("s:"), "the stream selected, not opened");
  assert.equal(m.store.state.ui.plansFocus, col);
  await until(() => stream.getAttribute("aria-pressed") === "true" && /press again to open/.test(stream.getAttribute("aria-label")), "pressed");
  stream.fire("click");
  await until(() => m.store.state.ui.plansFocus?.includes("#s:"), "opened on the second press");
  const leaf = tiles(m).find((b) => b.getAttribute("aria-label").startsWith("task "));
  leaf.fire("click");
  await until(() => m.store.state.ui.plansSel?.startsWith("i:"), "the item selected");
  await until(() => panel(m) && find(panel(m), "h3").map(text).join(",") === "Messages about this,History", "the item card with its history");
  await until(() => !/Loading…/.test(text(panel(m))), "the history read");
  assert.ok(find(panel(m), "ol.pl-history").length === 1 || /No history/.test(text(panel(m))));
  assert.ok(esc(m), "the page's own Esc does not also run");
  await until(() => !m.store.state.ui.plansSel, "deselected");
  esc(m);
  await until(() => m.store.state.ui.plansFocus === col, "up to the column");
  esc(m);
  await until(() => !m.store.state.ui.plansFocus, "up to all columns");
  assert.equal(esc(m), false, "at the top Esc is the page's");
  head.fire("click");
  await until(() => m.store.state.ui.plansFocus === col, "in again");
  head.fire("click");
  await until(() => !m.store.state.ui.plansFocus, "the focus click goes up");
});

test("epic and deliverable tiles show their holder and state pill, and the panel names the owner", UNREACHABLE, async () => {
  const m = await openTeamBoard();
  await until(() => tiles(m).some((b) => /^(epic|deliverable) .*, owner /.test(b.getAttribute("aria-label"))), "a held epic or deliverable");
  const t = tiles(m).find((b) => /^(epic|deliverable) .*, owner /.test(b.getAttribute("aria-label")));
  assert.equal(t.parentNode.all("span.pl-chip").length + t.parentNode.all("a.who").length, 2, "the pill (the state is not the column's) and the holder's link");
  assert.match(t.parentNode.all("a.who")[0].getAttribute("href"), /^#\/member\/member%3A/, "the holder links to the member");
  t.fire("click");
  await until(() => panel(m) && find(panel(m), "dt").map(text).includes("owner"), "the owner in the card");
});

test("selecting a team at All teams lights its tiles in every column", UNREACHABLE, async () => {
  const m = await openTeamBoard({ "ah.plans.epics": "off" });
  m.store.setUi({ view: "allplans" });
  await until(() => names(m).includes("All teams"), "the root");
  await until(() => tiles(m).filter((b) => b.getAttribute("aria-label").startsWith("team ship,")).length > 1, "ship in more than one column");
  assert.ok(leased(m).some((id) => id.startsWith(`plan-items:${DIR}/ship/`)), "a live team's items are held at All teams");
  const ship = tiles(m).filter((b) => b.getAttribute("aria-label").startsWith("team ship,"));
  ship[0].fire("click");
  await until(() => m.store.state.ui.plansSel === `t:${DIR}/ship`, "selected");
  await until(() => ship.every((b) => /\bon\b/.test(b.parentNode.className)), "every ship tile lit");
  await until(() => panel(m) && find(panel(m), "a").some((a) => /^#\/member\/member%3A/.test(a.getAttribute("href") ?? "")), "the team card's owners link to their members");
});

test("List → Open in board filters the board to the stream, shown as a chip that a click clears", async () => {
  useStorage({ "ah.plans.epics": "off" });
  const m = await mount({ view: "board" });
  await until(() => find(m.root, "button.pl-toboard").length > 0, "the List rows");
  find(m.root, "button.pl-toboard").find((b) => b.getAttribute("aria-label") === "Open docs-2.5 in board").fire("click");
  const cards = () => find(m.root, "article.kb-card").filter((c) => !c.getAttribute("class").includes("kb-brow"));
  await until(() => cards().length > 0, "the board");
  assert.equal(fake.data["ah.plans.layout"], "board");
  assert.equal(m.store.state.ui.plansStream, `s:${DIR}/ship|docs-2.5`);
  const chip = () => find(m.root, "div.kb-bar")[0]?.all("button").find((b) => /^Stream: docs-2\.5/.test(text(b)));
  await until(() => chip(), "the chip");
  const refs = cards().flatMap((c) => c.all("span.kb-ref")).map(text);
  assert.ok(refs.length > 0, "the filtered cards name their stream");
  assert.ok(refs.every((r) => /^docs-2\.5 \//.test(r)), `no other stream: ${refs.join(", ")}`);
  chip().fire("click");
  await until(() => !m.store.state.ui.plansStream, "a click clears it");
});

test("Shift+G on the board opens Graph on the selected tile's team and stream", UNREACHABLE, async () => {
  const { globalKey } = await import("../../web/ui/keys.js");
  const m = await openTeamBoard({ "ah.plans.epics": "off" });
  await until(() => tiles(m).some((b) => b.getAttribute("aria-label").startsWith("task ")), "an item");
  tiles(m).find((b) => b.getAttribute("aria-label").startsWith("task ")).fire("click");
  await until(() => m.store.state.ui.plansSel?.startsWith("i:"), "selected");
  const stream = m.store.state.ui.plansSel.split("|")[1];
  assert.equal(globalKey({ key: "G", target: globalThis.document.body, preventDefault() {} }, m.store, globalThis.document), true);
  assert.equal(m.store.state.ui.view, "graph");
  assert.equal(m.store.state.ui.team, `team:${DIR}/ship`);
  assert.equal(m.store.state.ui.graphCard, `stream:${stream}`);
  await settle();
});

test("layout: equal columns, size tiers by width, hidden tiles leave the tab order; unmount lets go of the observer, the frame and the zoom", UNREACHABLE, async () => {
  const observers = [];
  const frames = new Map();
  const cancelled = [];
  globalThis.ResizeObserver = class {
    constructor(cb) {
      this.cb = cb;
      observers.push(this);
    }
    observe(el) {
      this.el = el;
    }
    disconnect() {
      this.off = true;
    }
  };
  globalThis.requestAnimationFrame = (f) => (frames.set(frames.size + 1 + cancelled.length, f), frames.size + cancelled.length);
  globalThis.cancelAnimationFrame = (id) => cancelled.push(id);
  const run = () => [...frames.entries()].forEach(([id, f]) => (frames.delete(id), f()));
  try {
    const m = await openTeamBoard({ "ah.plans.epics": "off" });
    await until(() => observers.some((o) => o.el), "observed");
    const ro = observers.find((o) => o.el);
    ro.cb([{ contentRect: { width: 1000, height: 600 } }]);
    run();
    const box = (b) => Object.fromEntries([...b.parentNode.style.cssText.matchAll(/(\w+):([^;]+);/g)].map(([, k, v]) => [k, v]));
    const px = (v) => Math.round(Number.parseFloat(v) * 1000) / 1000;
    // Equal, fixed columns; a head is a 40px strip, and an empty column (a leaf) runs to the bottom.
    assert.deepEqual(heads(m).map((b) => [px(box(b).left), px(box(b).width)]), [0, 200, 400, 600, 800].map((x) => [x, 200]));
    assert.ok(heads(m).every((b) => px(box(b).height) === (/, 0 cards/.test(b.getAttribute("aria-label")) ? 560 : 40)));
    const check = () => tiles(m).forEach((b) => {
      const w = px(box(b).width);
      assert.equal(/\bsm\b/.test(b.parentNode.className), w < 80, `sm at ${w}`);
      assert.equal(/\bxs\b/.test(b.parentNode.className), w < 24, `xs at ${w}`);
      assert.equal(b.parentNode.inert, box(b).visibility !== "visible");
    });
    check();
    const head = heads(m).find((b) => !/, 0 cards/.test(b.getAttribute("aria-label")));
    head.fire("click");
    await until(() => m.store.state.ui.plansFocus?.startsWith("c:"), "a column open");
    run();
    check();
    assert.ok(tiles(m).some((b) => b.parentNode.inert), "the other columns are out of the tab order");
    esc(m);
    await until(() => !m.store.state.ui.plansFocus, "back up");
    run();
    assert.ok(globalThis.document.activeElement === head, "Esc puts the keyboard back on the column it left");
    const tilesBox = find(m.root, "div.ice-tiles")[0];
    ro.cb([{ contentRect: { width: 900, height: 600 } }]);
    const pending = frames.size + cancelled.length;
    m.store.setUi({ view: "conversation" });
    await until(() => ro.off, "the observer let go");
    assert.ok(cancelled.includes(pending), "the pending frame cancelled");
    assert.equal((tilesBox.__on ?? []).filter((o) => o.name === "zoom").length, 0, "the zoom listeners removed");
  } finally {
    delete globalThis.ResizeObserver;
    delete globalThis.requestAnimationFrame;
    delete globalThis.cancelAnimationFrame;
  }
});

test("the owner filter dims the tiles that own nothing of the holder's", UNREACHABLE, async () => {
  const m = await openTeamBoard({ "ah.plans.epics": "off" });
  await until(() => tiles(m).some((b) => b.getAttribute("aria-label").includes("owner docs-writer")), "docs-writer's items");
  m.store.setUi({ plansOwner: "docs-writer" });
  await until(() => tiles(m).some((b) => /\bdim\b/.test(b.parentNode.className)), "dimmed");
  const leaves = tiles(m).filter((b) => b.getAttribute("aria-label").startsWith("task "));
  assert.ok(leaves.every((b) => /\bdim\b/.test(b.parentNode.className) === !b.getAttribute("aria-label").includes("owner docs-writer,")), "a leaf is dimmed exactly when someone else holds it");
});

test("a tile says no more than it must: no pill that repeats its column, no empty message count, the full name on hover", UNREACHABLE, async () => {
  const m = await openTeamBoard({ "ah.plans.epics": "off" });
  await until(() => tiles(m).some((b) => b.getAttribute("aria-label").startsWith("task ")), "items");
  const COL = { "Next": ["not started", "next"], "In progress": ["in progress", "waiting on orchestrator"], "In review": ["in progress"], "Waiting on you": ["waiting on you"], "Done today": ["done"] };
  for (const head of heads(m)) {
    head.fire("click");
    await until(() => m.store.state.ui.plansFocus?.startsWith("c:") || /, 0 cards/.test(head.getAttribute("aria-label")), "opened");
    const name = text(head.all("span.t")[0]);
    for (const b of tiles(m).filter((x) => x.getAttribute("aria-label").startsWith("task ") && !x.parentNode.inert)) {
      const chips = b.parentNode.all("span.pl-chip").map(text);
      assert.ok(chips.every((c) => !(COL[name] ?? []).includes(c.toLowerCase())), `${name}: ${chips}`);
      assert.ok(b.getAttribute("title")?.startsWith(text(b.all("span.t")[0])), "the full name as a tooltip");
    }
    m.store.setUi({ plansFocus: null });
  }
  assert.ok(tiles(m).every((b) => !/✉ (0|—)/.test(text(b.parentNode))), "no ✉ 0 and no ✉ —");
  assert.ok(heads(m).every((b) => Number(text(b.parentNode.all("span.num")[0]).split(" ")[0]) === Number(/, (\d+)/.exec(b.getAttribute("aria-label"))[1])), "a head counts the cards it shows");
});

test("no kanban leftovers: pl-board and pl-cell are gone from the Plans view and its sheet", () => {
  for (const f of ["../../web/ui/plans-view.js", "../../web/ui/r6-plans-view.css"]) {
    const src = readFileSync(new URL(f, import.meta.url), "utf8");
    assert.equal(/pl-board|pl-cell/.test(src), false, f);
  }
});
