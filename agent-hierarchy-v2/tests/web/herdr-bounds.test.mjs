// The ah2 Herdr mod never waits for good (spec herdr-ui-thread.md F2, F4): the poll's exec is raced against a deadline and
// capped, and the Graph's enclose stops on degenerate geometry.
import { test } from "node:test";
import assert from "node:assert/strict";
import { pollExec, ABANDON_CAP, STUCK } from "../../herdr-desktop/mods/ah2/model.js";
import { hierarchy, pack } from "../../herdr-desktop/mods/ah2/graph-layout.js";

const never = () => new Promise(() => {});
const now = () => Promise.resolve();
const held = () => ({ abandoned: 0 });
const ok = (stdout) => () => Promise.resolve({ code: 0, stdout });

test("an exec that never settles is given up on, and at the cap no third one starts", async () => {
  let calls = 0;
  const start = () => (calls++, never());
  const v = held();
  for (let i = 0; i < ABANDON_CAP; i++) await assert.rejects(pollExec(start, now, 35000, v), { message: "ah did not answer" });
  assert.equal(v.abandoned, 2);
  await assert.rejects(pollExec(start, now, 35000, v), { message: STUCK });
  assert.equal(calls, 2);
});

test("below the cap a new exec still starts", async () => {
  const r = await pollExec(ok("{}"), never, 35000, { abandoned: ABANDON_CAP - 1 });
  assert.equal(r.stdout, "{}");
});

test("an abandoned exec that settles late frees its place and its result is dropped", async () => {
  let settle;
  const v = held();
  await assert.rejects(pollExec(() => new Promise((done) => (settle = done)), now, 10000, v), { message: "ah did not answer" });
  assert.equal(v.abandoned, 1);
  settle({ code: 0, stdout: "late" });
  await new Promise((done) => setImmediate(done));
  assert.equal(v.abandoned, 0);
});

test("the deadline is the exec timeout plus 5 s, and an exec that answers first is not abandoned", async () => {
  let asked;
  const v = held();
  const r = await pollExec(ok("ok"), (ms) => ((asked = ms), never()), 35000, v);
  assert.equal(r.stdout, "ok");
  assert.equal(asked, 40000);
  assert.equal(v.abandoned, 0);
});

test("the down reasons: herdr's timeout, any other throw, a non-zero exit", async () => {
  const fail = (e) => () => Promise.reject(new Error(e));
  await assert.rejects(pollExec(fail("ah took longer than 35 s and was stopped"), never, 35000, held()), { message: "ah did not answer" });
  await assert.rejects(pollExec(fail("No such file or directory"), never, 35000, held()), { message: "ah is not on Herdr's PATH" });
  await assert.rejects(pollExec(() => { throw new Error("sync"); }, never, 35000, held()), { message: "ah is not on Herdr's PATH" });
  await assert.rejects(pollExec(() => Promise.resolve({ code: 1, stdout: "" }), never, 35000, held()), { message: "ahd not running" });
});

// pack with every radius 0 puts the circles on one point; an infinite radius makes the enclosing circle non-finite.
test("pack on degenerate circles returns or throws, never loops", () => {
  const kids = (n) => ({ children: Array.from({ length: n }, () => ({})) });
  assert.equal(pack(hierarchy(kids(4)), () => 0, () => 0).r, 0);
  assert.throws(() => pack(hierarchy(kids(3)), () => Infinity, () => 0), /pack: no enclosing basis/);
});

test("pack on normal circles still lays out (the limit does not fire)", () => {
  const root = pack(hierarchy({ children: Array.from({ length: 12 }, () => ({})) }), () => 1, () => 0);
  assert.ok(Number.isFinite(root.r) && root.r > 1);
});

// ---- a click's one call to ah, and the Board's columns (ah2-14) ----
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { readThread, sendAnswer, qaModel, NO_REPLY } from "../../herdr-desktop/mods/ah2/model.js";
import { readItemLog, boardModel, COLUMN_CAP } from "../../herdr-desktop/mods/ah2/board-model.js";

const fixture = JSON.parse(readFileSync(fileURLToPath(new URL("../../herdr-desktop/fixture/events.json", import.meta.url)), "utf8"));
const NOW = Date.parse("2026-11-08T17:20:00Z");

test("a click's exec that never settles is given up on: the thread, the item log and the answer each say so", async () => {
  const slices = new Map(fixture.slices.map((s) => [s.id, s]));
  assert.deepEqual(await readThread(never, { id: "p1", hierDir: "/r/.claude/ah2" }, now), { error: "Could not read: ah did not answer" });
  assert.deepEqual(await readItemLog(never, { dirId: "a1b2c3d4", team: "ah2-tm4" }, now), { error: "ah did not answer" });
  const card = qaModel(slices, NOW).teams[0].cards[2]; // the single-choice question, as herdr-qa-model.test.mjs sends it
  assert.deepEqual(await sendAnswer(never, card, { pick: card.start }, null, now), NO_REPLY);
  // herdr's own timeout reads the same, and an exec that answers is passed through
  assert.deepEqual(await readItemLog(() => Promise.reject(new Error("ah took longer than 15 s and was stopped")), { dirId: "a1b2c3d4", team: "ah2-tm4" }, never), { error: "ah did not answer" });
});

test("a Board column draws at most COLUMN_CAP cards, counts them all and says how many more", () => {
  const slices = new Map(fixture.slices.map((s) => {
    if (!s.id.startsWith("plan-items:") || !Array.isArray(s.body?.items)) return [s.id, s];
    const items = Array.from({ length: 400 }, (_, i) => ({ ...s.body.items[i % s.body.items.length], key: `k${i}`, id: `i${i}` }));
    return [s.id, { ...s, body: { ...s.body, items } }];
  }));
  const cols = boardModel(slices, NOW).columns;
  assert.ok(cols.some((c) => c.more > 0), "the grown plan overflows a column");
  for (const c of cols) {
    assert.ok(c.cards.length <= COLUMN_CAP || c.key === "done", `${c.key} is capped`);
    assert.equal(c.count, c.cards.length + c.more, `${c.key} counts every card`);
  }
});
