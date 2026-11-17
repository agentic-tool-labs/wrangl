// The ah2 Herdr mod's Board data side (spec-herdr-desktop.md § View: board) over the shipped fixture.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { boardModel, boardSubs, itemLog, workLog, LOG_PAGE, trackAfter, nothingChanged } from "../../herdr-desktop/mods/ah2/board-model.js";
import { loadTrack, saveTrack } from "../../herdr-desktop/mods/ah2/plans-seen.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const mod = join(root, "herdr-desktop", "mods", "ah2");
const reply = JSON.parse(readFileSync(join(root, "herdr-desktop", "fixture", "events.json"), "utf8"));
const slices = new Map(reply.slices.map((s) => [s.id, s]));
const NOW = Date.parse(reply.observed_at);

for (const f of ["plans-model.js", "select.js", "plans-seen.js"]) {
  test(`the mod's ${f} is a byte-identical copy of web/ui/${f}`, () => {
    assert.ok(readFileSync(join(mod, f)).equals(readFileSync(join(root, "web", "ui", f))), `copy web/ui/${f} over herdr-desktop/mods/ah2/${f}`);
  });
}

test("the board subscribes per running team and puts the fixture's items in the web's columns, Waiting on you as Q&A cards", () => {
  const subs = boardSubs(slices, NOW);
  assert.ok(subs.includes("plan:a1b2c3d4/ah2-tm4") && subs.includes("plan-items:a1b2c3d4/lane-herdr/herdr"));
  assert.ok(!subs.some((s) => s.includes("old-spike")), "a closed team adds nothing");
  const m = boardModel(slices, NOW);
  const ids = Object.fromEntries(m.columns.map((c) => [c.key, c.cards.map((x) => (x.qa ? `qa:${x.qa.q.id}` : x.id))]));
  assert.deepEqual(ids.waiting, ["qa:core:atp-1140", "qa:d8", "qa:d7"], "the waiting item and the decisions are the Q&A cards, once");
  assert.deepEqual(ids.stuck, ["b341"]);
  assert.equal(m.columns.find((c) => c.key === "stuck").cards[0].blocked, "blocked 3h");
  assert.deepEqual(ids.done.sort(), ["atp-1131", "atp-1180", "b330"]);
  assert.equal(m.count, 3);
});

// One summary changed: the rest of the fixture as is.
const withSummary = (id, patch) => {
  const s = slices.get(`team-summary:${id}`);
  return new Map(slices).set(s.id, { ...s, body: { ...s.body, ...patch } });
};

test("a decision the questions slice does not hold keeps the plain card; the fixture's held ones do not", () => {
  const plainIds = (sl) => boardModel(sl, NOW).columns.find((c) => c.key === "waiting").cards.filter((c) => !c.qa).map((c) => c.key);
  assert.deepEqual(plainIds(slices), [], "every waiting thing is held: no plain decision card");
  assert.deepEqual(plainIds(withSummary("a1b2c3d4/ah2-tm4", { waiting_on_you: 4 })), ["team:a1b2c3d4/ah2-tm4|decision"]);
});

test("a quiet team adds no plan or items subs, but keeps its questions sub", () => {
  const subs = boardSubs(withSummary("a1b2c3d4/ah2-tm4", { quiet_at: "2026-11-08T00:00:00.000Z" }), NOW);
  assert.ok(!subs.some((s) => s.startsWith("plan:a1b2c3d4/ah2-tm4") || s.startsWith("plan-items:a1b2c3d4/ah2-tm4")));
  assert.ok(subs.some((s) => s.includes("a1b2c3d4/ah2-tm4")), "its questions sub stays");
  assert.ok(subs.includes("plan:a1b2c3d4/lane-herdr"), "a team not quiet keeps its plan sub");
});

test("filters, change marks since the last visit (kept across a restart), and the item sheet's log", () => {
  const cardIds = (m, col) => m.columns.find((c) => c.key === col).cards.map((x) => (x.qa ? `qa:${x.qa.q.id}` : x.id));
  // a first visit has nothing to compare: no marks; leaving saves what it held into the store
  const store = new Map();
  const storage = { getItem: (k) => store.get(k) ?? null, setItem: (k, v) => store.set(k, v) };
  const first = loadTrack(NOW, storage);
  const m0 = boardModel(slices, NOW, { track: first });
  assert.ok(m0.columns.every((c) => c.cards.every((x) => !x.mark)) && m0.since === null, "first visit: no marks");
  assert.ok(saveTrack(trackAfter(first, m0.covered, slices, NOW), storage));
  // after a restart, b338 moved to review and a new item landed in core: both are marked, and Changed since last visit keeps them
  const track = loadTrack(NOW + 60_000, storage);
  const web = slices.get("plan-items:a1b2c3d4/ah2-tm4/web");
  const core = slices.get("plan-items:a1b2c3d4/ah2-tm4/core");
  const moved = new Map(slices)
    .set(web.id, { ...web, body: { ...web.body, items: web.body.items.map((i) => (i.key === "web:b338" ? { ...i, state: "in_review", column: "review" } : i)) } })
    .set(core.id, { ...core, body: { ...core.body, items: [...core.body.items, { key: "core:atp-1150", title: "New thing", state: "next", column: "next", owner: "" }] } });
  const m = boardModel(moved, NOW, { track });
  assert.equal(m.columns.find((c) => c.key === "review").cards.find((x) => x.id === "b338").mark, "in progress → in review");
  assert.equal(m.columns.find((c) => c.key === "next").cards.find((x) => x.id === "atp-1150").mark, "New since your last visit");
  assert.ok(m.anyMarked, "the board says something is marked");
  const ch = boardModel(moved, NOW, { track, changesOnly: true });
  assert.deepEqual(ch.columns.flatMap((c) => c.cards.map((x) => x.id)), ["atp-1150", "b338"]);
  // Mark all seen: the baseline is now, so nothing is marked, and it is stored at once
  saveTrack(trackAfter(track, m.covered, moved, NOW + 120_000, true), storage);
  assert.ok(!boardModel(moved, NOW, { track }).anyMarked && !boardModel(moved, NOW, { track: loadTrack(NOW, storage) }).anyMarked);
  assert.ok(ch.filtered);
  // hiding a team drops its cards and Q&A cards, its chip stays; the owner filter keeps that holder (and that asker) only
  const hid = boardModel(slices, NOW, { hide: new Set(["a1b2c3d4/ah2-tm4"]) });
  assert.deepEqual(cardIds(hid, "stuck"), []);
  assert.ok(!cardIds(hid, "waiting").some((x) => x === "qa:core:atp-1140"));
  assert.equal(hid.teams.find((x) => x.key === "a1b2c3d4/ah2-tm4").on, false);
  assert.deepEqual(boardModel(slices, NOW, { owner: "ux-engineer" }).columns.flatMap((c) => c.cards.filter((x) => !x.qa).map((x) => x.id)).sort(), ["atp-1185", "b330", "b338"]);
  // the sheet's log: the team's plan changes, kept to the item and its stream
  const sel = { dirId: "a1b2c3d4", team: "ah2-tm4", stream: "web", item: "b338" };
  const reply = { slices: [{ id: "list:plan-changes:a1b2c3d4/ah2-tm4", body: { rows: [
    { at: "2026-11-08T17:00:00Z", seq: 2, stream: "web", item: "b338", kind: "state", from: "next", to: "in_progress", by: "orchestrator" },
    { at: "2026-11-08T16:00:00Z", seq: 1, stream: "core", item: "b338", kind: "added" },
    { at: "2026-11-08T15:00:00Z", seq: 1, stream: "web", item: "b341", kind: "added" },
  ] } }] };
  assert.deepEqual(itemLog(reply, sel).map((r) => [r.text, r.by]), [["next → in progress", "orchestrator"]]);
});

// One stream's items changed: the rest of the fixture as is.
const withItems = (id, fn) => {
  const s = slices.get(id);
  return new Map(slices).set(id, { ...s, body: { ...s.body, items: fn(s.body.items) } });
};
const cardsOf = (m, col) => m.columns.find((c) => c.key === col).cards;

test("a plain card carries the web's work id, size and open blockers, and none when the item has none", () => {
  const sl = withItems("plan-items:a1b2c3d4/ah2-tm4/web", (items) => items.map((i) => (i.key === "web:b341" ? { ...i, work_id: "ah2-7", size: "M", blocked_by_open: 2 } : i)));
  const b341 = cardsOf(boardModel(sl, NOW), "stuck").find((x) => x.id === "b341");
  assert.deepEqual([b341.workId, b341.size, b341.blockedOpen], ["ah2-7", "M", 2]);
  const plain = cardsOf(boardModel(slices, NOW), "stuck")[0];
  assert.deepEqual([plain.workId, plain.size, plain.blockedOpen], ["", "", 0]);
});

test("the filter text and the older streams reach boardOf; unfiltered heads are the served counts", () => {
  const all = boardModel(slices, NOW);
  assert.ok(!all.filtered && all.columns.find((c) => c.key === "waiting").head === [...slices.values()].filter((s) => s.id.startsWith("team-summary:")).reduce((n, s) => n + (s.body?.quiet_at ? 0 : s.body?.waiting_on_you ?? 0), 0));
  const none = boardModel(slices, NOW, { query: "no stream is called this" });
  assert.ok(none.filtered && none.columns.every((c) => c.cards.every((x) => x.qa && x.qa.q.kind !== "item")), "only decisions stay under a filter that matches no stream");
  assert.equal(none.columns.find((c) => c.key === "stuck").head, 0, "filtered, the head counts what is shown");
  assert.equal(none.columns.find((c) => c.key === "waiting").head, all.columns.find((c) => c.key === "waiting").head, "a filter text keeps the served heads, as the web");
  assert.equal(boardModel(slices, NOW, { owner: "ux-engineer" }).columns.find((c) => c.key === "progress").head, 2, "the owner filter counts what is shown (served is 0)");
  // an item past its quiet_at is hidden until older streams are shown
  const sl = withItems("plan-items:a1b2c3d4/ah2-tm4/web", (items) => items.map((i) => (i.key === "web:b341" ? { ...i, quiet_at: "2026-11-01T00:00:00Z" } : i)));
  assert.deepEqual(cardsOf(boardModel(sl, NOW), "stuck"), []);
  assert.deepEqual(cardsOf(boardModel(sl, NOW, { showFinished: true }), "stuck").map((x) => x.id), ["b341"]);
});

test("a column past its cap expands whole and folds back; a quiet team's plan is subscribed once expanded", () => {
  const many = withItems("plan-items:a1b2c3d4/ah2-tm4/web", (items) => [...items, ...Array.from({ length: 60 }, (_, n) => ({ key: `web:x${n}`, title: `x${n}`, state: "next", column: "next", owner: "" }))]);
  const capped = boardModel(many, NOW).columns.find((c) => c.key === "next");
  assert.ok(capped.cards.length === 50 && capped.more > 0 && capped.folds && !capped.open);
  const open = boardModel(many, NOW, { expand: new Set(["next"]) }).columns.find((c) => c.key === "next");
  assert.ok(open.cards.length === open.count && open.more === 0 && open.folds && open.open);
  const quiet = withSummary("a1b2c3d4/ah2-tm4", { quiet_at: "2026-11-08T00:00:00.000Z" });
  const id = boardModel(quiet, NOW).stale.find((s) => s.name === "ah2-tm4")?.id;
  assert.ok(id, "the quiet team is folded, not hidden");
  assert.ok(boardSubs(quiet, NOW, { open: new Set([id]) }).includes("plan:a1b2c3d4/ah2-tm4"));
  const shown = boardModel(quiet, NOW, { open: new Set([id]) });
  assert.ok(!shown.stale.some((s) => s.id === id) && shown.unstale.some((s) => s.id === id), "shown again, it offers Hide");
  assert.equal(shown.unstale.find((s) => s.id === id).loading, false, "its plan is held: not loading");
  const unread = new Map(quiet);
  unread.delete("plan:a1b2c3d4/ah2-tm4");
  assert.equal(boardModel(unread, NOW, { open: new Set([id]) }).unstale.find((s) => s.id === id).loading, true, "Loading until its plan is read");
  // Hide is open without it: back on the not shown line, its plan and items subs gone
  assert.ok(boardModel(quiet, NOW).unstale.length === 0 && !boardSubs(quiet, NOW, { open: new Set() }).includes("plan:a1b2c3d4/ah2-tm4"));
});

test("the item sheet holds its item-log: slice only while open, and reads it as the web's item dialog", () => {
  const sl = withItems("plan-items:a1b2c3d4/ah2-tm4/web", (items) => items.map((i) => (i.key === "web:b341" ? { ...i, work_id: "ah2-7", note: "Waits on the API", ext_id: "GH-12", ext_url: "https://example.com/12" } : i)));
  const sel = cardsOf(boardModel(sl, NOW), "stuck").find((x) => x.id === "b341").sel;
  assert.equal(sel.log, "item-log:a1b2c3d4/7");
  assert.ok(boardSubs(sl, NOW, { log: sel.log }).includes(sel.log), "held while the sheet is open");
  assert.ok(!boardSubs(sl, NOW).some((s) => s.startsWith("item-log:")), "dropped once it closes");
  assert.deepEqual(sel.rows.map(([k]) => k), ["Work id", "Column", "State", "Marker", "Stream", "Branch"], "the web dialog's rows, in its order, empty ones left out");
  assert.deepEqual(sel.group, { key: "kt:a1b2c3d4/ah2-tm4", text: "ah2-tm4", label: "Team" }, "a team card's team is the group link, not a row");
  assert.equal(sel.rows[0][1], "ah2-7");
  assert.deepEqual([sel.note, sel.ext], ["Waits on the API", { id: "GH-12", url: "https://example.com/12" }]);
  // the slice: loading until held, then newest first, LOG_PAGE of them, links and attachments (only http(s) is a link)
  assert.equal(workLog(sl, new Set(), sel.log).state, "loading");
  assert.equal(workLog(sl, new Set([sel.log]), sel.log).state, "none");
  const entries = Array.from({ length: 25 }, (_, n) => ({ ts_ms: Date.UTC(2026, 10, 17, 12, n), type: "note", title: `e${n}`, by: "ux-engineer", body: n === 24 ? "body" : "" }));
  const held = new Map(sl).set(sel.log, { id: sel.log, body: { id: "ah2-7", description: "# Why\n- one", entries, more: 3, links: { blocked_by: ["ah2-5"], blocks: [] }, attachments: [{ ref: "javascript:x", kind: "url" }, { ref: "docs/a.md", kind: "file", sha: "abcdef123" }], children: [] } });
  const w = workLog(held, new Set(), sel.log);
  assert.deepEqual([w.total, w.entries.length, w.entries[0].title, w.more], [25, LOG_PAGE, "e24", 3]);
  assert.equal(workLog(held, new Set(), sel.log, true).entries.length, 25);
  assert.deepEqual(w.links, [["Blocked by", "ah2-5"]]);
  assert.deepEqual(w.attachments.map((a) => [a.url, a.facts]), [[null, "url"], [null, "file · abcdef1"]]);
  assert.deepEqual(w.desc.map((b) => b.kind), ["head", "list"]);
  assert.equal(workLog(held, new Set(), null), null, "no work id, no timeline");
});

test("the Board tab holds its directory's backlog and each team's queue and message counts; nothing when it is not shown", () => {
  const shownSubs = boardSubs(slices, NOW, { shown: true });
  assert.ok(shownSubs.includes("backlog:a1b2c3d4"));
  assert.ok(shownSubs.includes("queue:a1b2c3d4/ah2-tm4") && shownSubs.includes("item-msgs:a1b2c3d4/ah2-tm4"));
  assert.ok(!boardSubs(slices, NOW).some((s) => /^(backlog|queue|item-msgs):/.test(s)), "another tab on screen: none held");
});

test("the Backlog column by wave, cards take their epic, inherited size, queue marker and message count, in the web's order", () => {
  const rows = [
    { key: "E1", kind: "epic", title: "Board parity", open: true, wave: "now", rank: 1, planned: true },
    { key: "D1", kind: "deliverable", title: "Cards", parent: "E1", size: "L", open: true, wave: "now", planned: true },
    ...Array.from({ length: 6 }, (_, n) => ({ key: `T${n}`, kind: "task", title: `Task ${n}`, open: true, wave: "now", rank: n, work_id: n === 0 ? "ah2-9" : undefined })),
    { key: "G1", kind: "goal", title: "Goal row", open: true, wave: "later" },
    { key: "U1", title: "Untriaged one", open: true, wave: "untriaged", state: "in_progress", holder: "ux-engineer" },
  ];
  const web = "plan-items:a1b2c3d4/ah2-tm4/web";
  const sl = withItems(web, (items) => items.map((i) => (i.key === "web:b338" ? { ...i, parent: "D1", column: "next", state: "next" } : i)))
    .set("backlog:a1b2c3d4", { id: "backlog:a1b2c3d4", body: { items: rows } })
    .set("queue:a1b2c3d4/ah2-tm4", { id: "queue:a1b2c3d4/ah2-tm4", body: [{ item: "web/b338", queued_since: new Date(NOW - 40 * 60_000).toISOString(), eligible_members: [], role: "implementor" }] })
    .set("item-msgs:a1b2c3d4/ah2-tm4", { id: "item-msgs:a1b2c3d4/ah2-tm4", body: { counts: { b338: 3 } } });
  const m = boardModel(sl, NOW);
  assert.deepEqual(m.backlog.waves.map((g) => [g.wave, g.total, g.rows.length]), [["now", 6, 4], ["untriaged", 1, 1]], "planned and goal rows stay out; four a wave");
  assert.deepEqual([m.backlog.count, m.backlog.total], [5, 7]);
  assert.equal(boardModel(sl, NOW, { waveAll: new Set(["now"]) }).backlog.waves[0].rows.length, 6);
  assert.equal(boardModel(sl, NOW, { blFold: true }).backlog.count, 0, "folded: a strip");
  assert.deepEqual(boardModel(sl, NOW, { blockedOnly: true }).backlog.waves, [], "Blocked only leaves the backlog out, as the web's");
  assert.equal(m.backlog.waves[1].rows[0].under, "in progress");
  const b338 = m.columns.find((c) => c.key === "next").cards.find((x) => x.id === "b338");
  assert.deepEqual([b338.epic?.name, b338.size, b338.sizeFrom, b338.msgs, b338.queue?.text], ["Board parity", "L", "D1", 3, "waiting 40 min · no idle implementor"]);
  assert.ok(b338.sel.rows.some(([k, v]) => k === "Size" && v === "L (deliverable)") && b338.sel.rows.some(([k, v]) => k === "Messages" && v === "3"));
  // the web's order inside a column: team, then epic rank, an epic-less card last
  const next = m.columns.find((c) => c.key === "next").cards.filter((x) => x.team === "ah2-tm4").map((x) => x.id);
  assert.ok(next.length > 1 && next[0] === "b338", `the card with an epic leads its team: ${next}`);
  assert.deepEqual(m.columns.find((c) => c.key === "next").cards.map((x) => x.team), [...m.columns.find((c) => c.key === "next").cards.map((x) => x.team)].sort((a, b) => a.localeCompare(b)), "teams in name order");
  // the backlog sheet: its rows, children and the plan items under it
  const d1 = m.blSheet("D1");
  assert.deepEqual([d1.kind, d1.parent.key, d1.items.map((i) => i.id)], ["deliverable", "E1", [b338.key]]);
  assert.deepEqual(m.blSheet("E1").kids.map((k) => k.key), ["D1"]);
});

// ah2-tm4 runs the herdr lane (child team lane-herdr), as a manager's streams row names it.
const withLane = () => {
  const t = slices.get("team:a1b2c3d4/ah2-tm4");
  return new Map(slices).set(t.id, { ...t, body: { ...t.body, streams: [{ name: "herdr", sub_orchestrator: "sub-orchestrator", child_team: "lane-herdr", branch: "herdr", base: "main", state: "open" }] } });
};

test("a team scope holds that team and its lanes' teams only; a team's chip switched off drops its queue and message counts", () => {
  const sl = withLane();
  const all = boardSubs(sl, NOW, { shown: true });
  const scoped = boardSubs(sl, NOW, { shown: true, team: "a1b2c3d4/ah2-tm4" });
  assert.ok(all.some((s) => s.includes("9f8e7d6c/engram-rc")), "the All teams board holds every running team");
  assert.ok(!scoped.some((s) => s.includes("9f8e7d6c/engram-rc")), "scoped: another team's subs go");
  assert.ok(scoped.includes("plan-items:a1b2c3d4/lane-herdr/herdr") && scoped.includes("queue:a1b2c3d4/lane-herdr"), "the lane's team comes with it");
  const hid = boardSubs(sl, NOW, { shown: true, hide: new Set(["a1b2c3d4/ah2-tm4"]) });
  assert.ok(!hid.includes("queue:a1b2c3d4/ah2-tm4") && !hid.includes("item-msgs:a1b2c3d4/ah2-tm4") && hid.includes("plan:a1b2c3d4/ah2-tm4"));
});

test("the breadcrumb scopes the board: teams, then lanes with the lane header, then epics; team and lane sheets", () => {
  const sl = withLane();
  const top = boardModel(sl, NOW);
  assert.deepEqual([top.menuHead, top.crumbs.map((c) => c.name)], ["Teams", ["All teams"]]);
  assert.ok(top.menu.some((c) => c.key === "a1b2c3d4/ah2-tm4" && c.n > 0));
  const tm = boardModel(sl, NOW, { team: "a1b2c3d4/ah2-tm4" });
  assert.deepEqual([tm.menuHead, tm.menu.map((c) => c.key)], ["Lanes", ["top", "herdr"]]);
  assert.ok(tm.columns.every((c) => c.cards.every((x) => x.qa || x.team !== "engram-rc")), "scoped: no other team's card");
  const herdrCard = tm.columns.flatMap((c) => c.cards).find((x) => !x.qa && x.team === "lane-herdr");
  assert.deepEqual(herdrCard.where, { id: "kl:herdr", name: "lane-herdr", kind: "lane", tip: "lane · runs herdr", order: 1 }, "a lane team's card sits in its lane");
  const ln = boardModel(sl, NOW, { team: "a1b2c3d4/ah2-tm4", lane: "herdr" });
  assert.ok(ln.columns.every((c) => c.cards.every((x) => x.qa || x.team === "lane-herdr")), "the lane holds only its cards");
  assert.deepEqual([ln.laneHead.stream, ln.laneHead.so, ln.laneHead.branch], ["herdr", "sub-orchestrator", "herdr → main"]);
  assert.deepEqual([ln.menuHead, ln.crumbs.map((c) => c.name)], ["Epics", ["All teams", "ah2-tm4", "herdr"]]);
  assert.deepEqual(ln.menu.map((c) => c.key), ["~"], "no backlog: every card is No epic");
  assert.ok(boardModel(sl, NOW, { team: "a1b2c3d4/ah2-tm4", lane: "herdr", epic: "E9" }).columns.every((c) => c.cards.length === 0), "an epic with no cards empties the board");
  const lane = tm.groupSheet("kl:herdr");
  assert.deepEqual([lane.kind, lane.rows.map(([k]) => k), lane.members.length > 0, lane.items.length > 0], ["Lane", ["Branch", "State", "Sub-orchestrator"], true, true]);
  assert.equal(top.groupSheet("kt:a1b2c3d4/ah2-tm4").kind, "Team");
  // every card's sheet is in sels, drawn or past a column's cap
  const many = withItems("plan-items:a1b2c3d4/ah2-tm4/web", (items) => [...items, ...Array.from({ length: 60 }, (_, n) => ({ key: `web:x${n}`, title: `x${n}`, state: "next", column: "next", owner: "" }))]);
  const cm = boardModel(many, NOW);
  const drawnKeys = new Set(cm.columns.flatMap((c) => c.cards.map((x) => x.key)));
  assert.ok(cm.columns.find((c) => c.key === "next").more > 0 && [...cm.sels.keys()].some((k) => !drawnKeys.has(k)), "a card past the cap still has its sheet");
});

test("Show only this stream keeps that stream's cards; a chip off outside the scope keeps the served heads; the arrow grid", () => {
  const all = boardModel(slices, NOW);
  const only = boardModel(slices, NOW, { only: { team: "a1b2c3d4/ah2-tm4", stream: "web" } });
  const plainCards = (m) => m.columns.flatMap((c) => c.cards.filter((x) => !x.qa));
  assert.ok(plainCards(only).length > 0 && plainCards(only).every((x) => x.ref.startsWith("web /")), "only the web stream's cards");
  assert.ok(plainCards(all).some((x) => !x.ref.startsWith("web /")));
  assert.equal(only.stream, "web");
  assert.ok(only.filtered && only.columns.find((c) => c.key === "waiting").cards.every((x) => !x.qa || x.qa.q.kind === "item"), "a stream filter drops the decisions, as the web's");
  // N-2b: a team hidden by its chip but outside the team scope does not narrow the heads
  const scoped = boardModel(withLane(), NOW, { team: "a1b2c3d4/ah2-tm4", hide: new Set(["9f8e7d6c/engram-rc"]) });
  assert.ok(!scoped.filtered, "the hidden team is not in scope");
  // the arrow keys' grid: one list per drawn column, in display order, only cards that open a sheet (a Button to focus)
  assert.deepEqual(all.nav, all.columns.map((c) => c.cards.filter((x) => !x.qa && x.sel && !x.ask).map((x) => x.key)).filter((col) => col.length > 0));
  const plainAsk = boardModel(withSummary("a1b2c3d4/ah2-tm4", { waiting_on_you: 4 }), NOW);
  const askKey = plainAsk.columns.find((c) => c.key === "waiting").cards.find((x) => !x.qa)?.key;
  assert.ok(askKey && !plainAsk.nav.flat().includes(askKey), "a card with no Button (the plain decision card) is not on the grid");
  // Changed since last visit, nothing changed: first visit, under a minute, or since when
  assert.deepEqual([nothingChanged(null, NOW), nothingChanged(NOW - 30_000, NOW), nothingChanged(NOW - 3 * 3_600_000, NOW)],
    ["Nothing to compare yet: this is your first visit", "Nothing changed since just now", "Nothing changed since 3h ago"]);
});
