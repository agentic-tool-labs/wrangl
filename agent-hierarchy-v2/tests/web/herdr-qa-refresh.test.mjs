// The Q&A window's background refresh (atp-1708): mods/ah2/model.js qaRefresh over the shipped fixture.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { qaModel, qaRefresh } from "../../herdr-desktop/mods/ah2/model.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const reply = JSON.parse(readFileSync(join(root, "herdr-desktop", "fixture", "events.json"), "utf8"));
const live = () => new Map(reply.slices.map((s) => [s.id, s]));
const NOW = Date.parse(reply.observed_at);
const cards = (qa) => qaModel(qa.shown, NOW).teams.flatMap((tm) => tm.cards.map((c) => c.key));
// different data: the team's questions slice went (its cards were answered)
const answered = () => { const s = live(); s.delete("questions:a1b2c3d4/ah2-tm4"); return s; };

test("a refresh in flight or a resync keeps the list drawn, not empty", () => {
  const qa = qaRefresh(null, live(), NOW, false, true);
  assert.equal(cards(qa).length, 3);
  assert.equal(qaRefresh(qa, live(), NOW, false, false), qa, "a sub set not yet whole changes nothing");
  assert.equal(qaRefresh(qa, new Map(), NOW, false, true), qa, "a resync that cleared the slices changes nothing");
  assert.equal(qaRefresh(null, new Map(), NOW, false, true), null, "nothing loaded yet: nothing to draw");
});

test("auto-update off: different data keeps the old list and offers it; applying gives the new list", () => {
  const qa = qaRefresh(null, live(), NOW, false, true);
  assert.equal(qaRefresh(qa, live(), NOW + 3600e3, false, true).pending, false, "same data an hour later: no offer");
  const offered = qaRefresh(qa, answered(), NOW, false, true);
  assert.deepEqual([offered.pending, cards(offered).length], [true, 3]);
  const applied = qaRefresh(offered, answered(), NOW, true, true);
  assert.deepEqual([applied.pending, cards(applied).length], [false, 0]);
});

test("auto-update on: new data replaces the list with no empty state between", () => {
  const qa = qaRefresh(null, live(), NOW, true, true);
  assert.equal(cards(qaRefresh(qa, new Map(), NOW, true, true)).length, 3, "a resync between keeps the old list");
  const next = qaRefresh(qa, answered(), NOW, true, true);
  assert.deepEqual([next.pending, cards(next).length], [false, 0]);
});
