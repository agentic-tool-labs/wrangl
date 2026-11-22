// The Q&A History tab's search and filters (spec qa-history-search-ui): the list read's query, the dirs it reads, the rows it makes,
// paging reset and the stale answer.
import { test } from "node:test";
import assert from "node:assert/strict";
import { FILTER0, NO_TEAM, filterActive, sinceOf, historyPath, historyArgv, historyDirs, threadRow, historyRead, historyReads, historyInit, historyAsk, historyMore, historyRefresh, historyTake, hasMore } from "../../herdr-desktop/mods/ah2/qa-history-model.js";

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
  assert.deepEqual(got.next, { "/h1": "1:x" });
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

test("no bridge: each dir is read through ah msg history with the filter's flags, and its threads become rows (A6)", async () => {
  const calls = [];
  const exec = async (cmd, argv, opts) => (calls.push([cmd, argv, opts.timeout]), { code: 0, stderr: "",
    stdout: JSON.stringify(argv.includes("/h1") ? { threads: [{ prompt: { id: "x", at: 1, text: "old email" }, replies: [] }], next: "1:x" }
      : { threads: [{ prompt: { id: "y", at: 2, text: "new email" }, replies: [] }], next: null }) });
  const f = { search: " my email ", team: null, time: "week", state: "answered" };
  const since = String(NOW - 7 * 86_400_000);
  const got = await historyRead(async () => null, historyDirs(TEAMS, null), f, NOW, null, exec);
  assert.deepEqual(calls, [["ah", ["msg", "history", "--json", "--dir", "/h1", "--search", "my email", "--since", since, "--answered"], 15000],
    ["ah", ["msg", "history", "--json", "--dir", "/h2", "--search", "my email", "--since", since, "--answered"], 15000]]);
  assert.deepEqual([got.rows.map((r) => r.id), got.rows.map((r) => r.hierDir), got.next], [["y", "x"], ["/h2", "/h1"], { "/h1": "1:x" }]);
  // Load more and the other flags: the cursor, the team, Not answered; read null (no Link at all) falls back too
  calls.length = 0;
  await historyRead(null, historyDirs(TEAMS, null), { ...FILTER0, team: "a", state: "unanswered" }, NOW, got.next, exec);
  assert.deepEqual(calls.map((c) => c[1]), [["msg", "history", "--json", "--dir", "/h1", "--team", "a", "--unanswered", "--before", "1:x"]]);
  assert.deepEqual(historyArgv("/h", { ...FILTER0, time: "today" }, NOW), ["msg", "history", "--json", "--dir", "/h", "--since", String(new Date(2026, 10, 17).getTime())]);
  // a team with a hierDir but no dirId is still read, through ah
  assert.deepEqual(historyDirs([{ name: "n", dirId: null, hierDir: "/h9" }], null).map((d) => d.hierDir), ["/h9"]);
  // one hierDir is one dir, whether or not each team carries its dir_id (A12)
  assert.deepEqual(historyDirs([{ name: "a", dirId: "d1", hierDir: "/h1" }, { name: "b", dirId: null, hierDir: "/h1" }], null).map((d) => d.name), ["a"]);
});

test("no bridge and ah failing, or no hierDir: the error, never an empty list (A7)", async () => {
  const f = { ...FILTER0, search: "e" };
  const fail = async () => ({ code: 2, stdout: "", stderr: "ah msg: no hierarchy at /h2\nmore" });
  assert.deepEqual(await historyRead(async () => null, historyDirs(TEAMS, "c"), f, NOW, null, fail), { error: "Could not read: ah msg: no hierarchy at /h2" });
  assert.deepEqual(await historyRead(async () => null, historyDirs(TEAMS, "c"), f, NOW, null, async () => { throw new Error("ah did not answer"); }),
    { error: "Could not read: ah did not answer" });
  assert.match((await historyRead(async () => null, historyDirs(TEAMS, "c"), f, NOW, null, async () => ({ code: 0, stdout: "not json", stderr: "" }))).error, /^Could not read: /);
  assert.deepEqual(await historyRead(async () => null, [{ name: "z", dirId: "d9", hierDir: null }], f, NOW, null, fail), { error: "Could not read: ahd is not reachable" });
});

test("dash-led text on the ah path: the search goes with one leading dash, never a bare --search; another dash-led value is the error (A9, A11)", async () => {
  const calls = [];
  const exec = async (cmd, argv) => (calls.push(argv), { code: 0, stderr: "", stdout: JSON.stringify({ threads: [], next: null }) });
  await historyRead(async () => null, historyDirs(TEAMS, "c"), { ...FILTER0, search: "  --force " }, NOW, null, exec);
  assert.deepEqual(calls, [["msg", "history", "--json", "--dir", "/h2", "--search", "-force"]]);
  // the bridge keeps the text as typed
  assert.equal(historyPath("d2", { ...FILTER0, search: "--force" }, NOW), "/ui/v1/history?dir=d2&q=--force");
  calls.length = 0;
  assert.deepEqual(await historyRead(async () => null, [{ name: "--all", dirId: "d2", hierDir: "/h2" }], { ...FILTER0, team: "--all" }, NOW, null, exec),
    { error: 'Could not read: ah cannot take --team "--all"' });
  assert.deepEqual(calls, []);
  // a late exec reads as readThread's does
  assert.deepEqual(await historyRead(async () => null, historyDirs(TEAMS, "c"), { ...FILTER0, search: "e" }, NOW, null, async () => { throw new Error("ah took longer than 15000 ms"); }),
    { error: "Could not read: ah did not answer" });
});

test("a refresh after two Load mores re-reads three rounds and keeps every row; a dir that ran out is read once (b71 D1)", async () => {
  // d1 pages back three times (cursor 1, 2, then none); d2 has one page
  const paths = [];
  const read = async (path) => {
    paths.push(path);
    if (path.includes("d2")) return JSON.stringify({ threads: [{ prompt: { id: "z", at: 50, text: "z" }, replies: [] }], next: null });
    const n = path.includes("before=2") ? 3 : path.includes("before=1") ? 2 : 1;
    return JSON.stringify({ threads: [{ prompt: { id: `p${n}`, at: 40 - n, text: "p" }, replies: [] }], next: n < 3 ? String(n) : null });
  };
  const dirs = historyDirs(TEAMS, null);
  const f = { ...FILTER0, search: "p" };
  const fetch = async (h) => historyTake(h, h.seq, await historyReads(read, dirs, f, NOW, h.more ? h.next : null, h.more ? 1 : h.rounds));
  let h = await fetch(historyAsk(historyInit(), f));
  // a Load more that fails does not grow the count
  const failed = historyTake(historyMore(h), h.seq + 1, { error: "down" });
  assert.equal(failed.rounds, 1);
  h = await fetch(historyMore(failed));
  const two = h; // two pages loaded, d1 still has more
  h = await fetch(historyMore(h));
  assert.deepEqual([h.rows.map((r) => r.id), h.rounds], [["z", "p1", "p2", "p3"], 3]);
  paths.length = 0;
  h = await fetch(historyRefresh(h));
  assert.deepEqual([h.rows.map((r) => r.id), h.next, h.rounds], [["z", "p1", "p2", "p3"], {}, 3]);
  assert.deepEqual(paths.map((p) => p.replace("/ui/v1/history?", "")), ["dir=d1&q=p", "dir=d2&q=p", "dir=d1&q=p&before=1", "dir=d1&q=p&before=2"]);
  // round 2 failing is the read's error: rows and next kept (d1's cursor survives)
  let n = 0;
  const flaky = async (path) => (++n === 3 ? (() => { throw new Error("gone"); })() : read(path));
  const r = historyRefresh(two);
  const kept = historyTake(r, r.seq, await historyReads(flaky, dirs, f, NOW, null, r.rounds));
  assert.deepEqual([kept.error, kept.rows.map((x) => x.id), kept.next, kept.rounds], ["Could not read: gone", ["z", "p1", "p2"], { "/h1": "2" }, 2]);
});

test("no team to search: the no-team note, not an empty list (b71 D2)", async () => {
  assert.deepEqual(await historyRead(async () => "{}", historyDirs(TEAMS, "gone"), { ...FILTER0, search: "e" }, NOW), { error: NO_TEAM });
  assert.equal(NO_TEAM, "No running team can be searched.");
});

test("a list-read row carries its age for the read's now; no time is no age (b71 D3)", () => {
  assert.equal(threadRow({ prompt: { id: "p", at: NOW - 3 * 3_600_000, text: "t" }, replies: [] }, TEAMS[2], "c", NOW).age, "3h");
  assert.equal(threadRow({ prompt: { id: "p", at: null, text: "t" }, replies: [] }, TEAMS[2], "c", NOW).age, "");
});
