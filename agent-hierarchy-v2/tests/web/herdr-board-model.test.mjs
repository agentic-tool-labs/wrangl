// The ah2 Herdr mod's Board data side (spec-herdr-desktop.md § View: board) over the shipped fixture.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { boardModel, boardSubs, itemLog } from "../../herdr-desktop/mods/ah2/board-model.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const mod = join(root, "herdr-desktop", "mods", "ah2");
const reply = JSON.parse(readFileSync(join(root, "herdr-desktop", "fixture", "events.json"), "utf8"));
const slices = new Map(reply.slices.map((s) => [s.id, s]));
const NOW = Date.parse(reply.observed_at);

for (const f of ["plans-model.js", "select.js"]) {
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

test("filters, change dots since the board opened, and the item sheet's log", () => {
  const cardIds = (m, col) => m.columns.find((c) => c.key === col).cards.map((x) => (x.qa ? `qa:${x.qa.q.id}` : x.id));
  const seen = new Map();
  assert.ok(boardModel(slices, NOW, { seen }).columns.every((c) => c.cards.every((x) => !x.mark)), "the open is the baseline: no dots");
  // b338 moves to review and a new item lands in core: both get a dot, and Changes only keeps just them
  const web = slices.get("plan-items:a1b2c3d4/ah2-tm4/web");
  const core = slices.get("plan-items:a1b2c3d4/ah2-tm4/core");
  const moved = new Map(slices)
    .set(web.id, { ...web, body: { ...web.body, items: web.body.items.map((i) => (i.key === "web:b338" ? { ...i, state: "in_review", column: "review" } : i)) } })
    .set(core.id, { ...core, body: { ...core.body, items: [...core.body.items, { key: "core:atp-1150", title: "New thing", state: "next", column: "next", owner: "" }] } });
  const m = boardModel(moved, NOW, { seen });
  assert.equal(m.columns.find((c) => c.key === "review").cards.find((x) => x.id === "b338").mark, "Moved from in progress to in review");
  assert.equal(m.columns.find((c) => c.key === "next").cards.find((x) => x.id === "atp-1150").mark, "New since you opened the board");
  const ch = boardModel(moved, NOW, { seen, changesOnly: true });
  assert.deepEqual(ch.columns.flatMap((c) => c.cards.map((x) => x.id)), ["atp-1150", "b338"]);
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
