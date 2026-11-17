// The Q&A History tab's search and filters (spec qa-history-search-ui): the list read's query, the dirs it reads, the rows it makes,
// paging reset and the stale answer.
import { test } from "node:test";
import assert from "node:assert/strict";
import { FILTER0, filterActive, sinceOf, historyPath, historyDirs, threadRow, historyRead, historyInit, historyAsk, historyMore, historyRefresh, historyTake, hasMore } from "../../herdr-desktop/mods/ah2/qa-history-model.js";

const NOW = new Date(2026, 10, 17, 15, 30).getTime();
const TEAMS = [{ name: "a", dirId: "d1", hierDir: "/h1" }, { name: "b", dirId: "d1", hierDir: "/h1" }, { name: "c", dirId: "d2", hierDir: "/h2" }];

test("all defaults: no filter is active, and a path would carry only the dir", () => {
  assert.equal(filterActive(FILTER0), false);
  assert.equal(filterActive({ ...FILTER0, search: "   " }), false);
  assert.equal(historyPath("d1", FILTER0, NOW), "/ui/v1/history?dir=d1");
});

test("each control maps to the daemon's parameter, URL-encoded", () => {
  const f = { search: " a&b=c ? ", team: "my team", time: "week", state: "unanswered" };
  const since = NOW - 7 * 86_400_000;
  assert.equal(historyPath("d1", f, NOW, "1:x/y"), `/ui/v1/history?dir=d1&q=a%26b%3Dc%20%3F&team=my%20team&since=${since}&state=unanswered&before=1%3Ax%2Fy`);
  assert.equal(filterActive(f), true);
  assert.equal(sinceOf("today", NOW), new Date(2026, 10, 17).getTime());
  assert.equal(sinceOf("all", NOW), null);
});

test("dirs: each running team's dir once, or only the chosen team's", () => {
  assert.deepEqual(historyDirs(TEAMS, null).map((d) => d.dirId), ["d1", "d2"]);
  assert.deepEqual(historyDirs(TEAMS, "c").map((d) => d.dirId), ["d2"]);
});

test("a list-read thread becomes the row History draws", () => {
  const dir = TEAMS[2];
  const p = threadRow({ prompt: { id: "p1", at: 5, text: "why?", question: true, team: "c" }, replies: [{ id: "r1", at: 6, part: "direct", text: "because\n so" }, { id: "r2", at: 9, text: "also" }], ask: null }, dir, "c");
  assert.deepEqual([p.id, p.dir, p.text, p.question, p.replyAt, p.preview, p.replies, p.team, p.dirId, p.hierDir], ["p1", "jim", "why?", true, 6, "because so", 1, "c", "d2", "/h2"]);
  const a = threadRow({ prompt: null, replies: [], ask: { id: "k1", at: 7, questions: [{ header: "H", question: "Q?" }], answers: { "Q?": "yes" } } }, dir, "c");
  assert.deepEqual([a.dir, a.header, a.text, a.answer], ["terminal", "H", "Q?", "yes"]);
});

test("one round reads every dir and merges newest first; no bridge is an error, not a blank list", async () => {
  const paths = [];
  const read = async (path) => (paths.push(path), JSON.stringify(path.includes("d1") ? { threads: [{ prompt: { id: "x", at: 1, text: "old" }, replies: [] }], next: "1:x" }
    : { threads: [{ prompt: { id: "y", at: 2, text: "new" }, replies: [] }], next: null }));
  const f = { ...FILTER0, search: "e" };
  const got = await historyRead(read, historyDirs(TEAMS, null), f, NOW);
  assert.deepEqual(got.rows.map((r) => r.id), ["y", "x"]);
  assert.deepEqual(got.next, { d1: "1:x" });
  // Load more asks only the dirs that have a cursor, with it
  paths.length = 0;
  await historyRead(read, historyDirs(TEAMS, null), f, NOW, got.next);
  assert.deepEqual(paths, ["/ui/v1/history?dir=d1&q=e&before=1%3Ax"]);
  assert.equal((await historyRead(async () => null, historyDirs(TEAMS, "c"), f, NOW)).error, "Could not read: ahd is not reachable");
  assert.equal((await historyRead(async () => { throw new Error("ah did not answer"); }, historyDirs(TEAMS, "c"), f, NOW)).error, "Could not read: ah did not answer");
});

test("a new filter resets paging and drops old rows; Load more keeps the filter and appends", () => {
  let h = historyAsk(historyInit(), { ...FILTER0, search: "x" });
  h = historyTake(h, h.seq, { rows: [{ at: 3 }, { at: 2 }], next: { d1: "c" } });
  assert.equal(hasMore(h), true);
  h = historyMore(h);
  assert.equal(h.filter.search, "x");
  h = historyTake(h, h.seq, { rows: [{ at: 1 }], next: {} });
  assert.deepEqual([h.rows.map((r) => r.at), hasMore(h)], [[3, 2, 1], false]);
  h = historyAsk(h, { ...FILTER0, search: "y" });
  assert.deepEqual([h.rows, h.next, h.loading], [[], {}, true]);
});

test("an answer for an older request is dropped; a failed read keeps rows and filter; a refresh keeps rows until it answers", () => {
  let h = historyAsk(historyInit(), { ...FILTER0, search: "a" });
  const old = h.seq;
  h = historyAsk(h, { ...FILTER0, search: "ab" });
  assert.equal(historyTake(h, old, { rows: [{ at: 9 }], next: {} }), h);
  h = historyTake(h, h.seq, { rows: [{ at: 2 }], next: { d1: "n" } });
  const more = historyMore(h);
  const failed = historyTake(more, more.seq, { error: "down" });
  assert.deepEqual([failed.rows, failed.filter.search, failed.error], [[{ at: 2 }], "ab", "down"]);
  const r = historyRefresh(h);
  assert.deepEqual(r.rows, [{ at: 2 }]);
  assert.deepEqual(historyTake(r, r.seq, { rows: [{ at: 5 }], next: {} }).rows, [{ at: 5 }]);
});
