import { test } from "node:test";
import assert from "node:assert/strict";
import { KEY, bootLooks, choose, loadLooks, looksNote, onLooks, rebase, save, stateOf } from "../../web/ui/looks.js";

// looks.js against the looks routes: a stand-in that keeps the routes' contract (UiLooksRouteTests pins the real ones).

const PENDING = "ah-optics-pending";
const look = (id, name = id) => ({
  id, name, theme: "dark", mode: "dark-emissive", builtin: false,
  profiles: { shadow: { accent: "cyan" }, dual: { accent: "cyan" } },
});
const reply = (status, doc) => ({ status, json: async () => doc });

// The server: `file` is null (no file), "corrupt", or a looks object. `script` answers a call in place of the contract.
function fakeServer(file = null) {
  const server = { file, rev: file ? "r0" : "", n: 0, calls: [], down: false, status: null };
  const body = () => ({ rev: server.rev, path: "/home/.claude/ah2/looks.json", corrupt: server.file === "corrupt", looks: server.file === "corrupt" ? null : server.file ?? { custom: [] } });
  server.fetch = async (path, init = {}) => {
    const sent = init.body ? JSON.parse(init.body) : null;
    server.calls.push({ path, method: init.method ?? "GET", sent });
    if (server.down) throw new TypeError("fetch failed");
    if (server.status) return reply(server.status, null);
    if (!init.method) return reply(200, body());
    if (server.file === "corrupt") return reply(409, { ...body(), refused: true, reason: "corrupt" });
    if (path.endsWith("/merge")) {
      const have = server.file?.custom ?? [];
      server.file = { ...server.file, custom: [...have, ...sent.custom.filter((l) => !have.some((h) => h.id === l.id))], active: server.file?.active ?? sent.active };
      server.rev = `r${(server.n += 1)}`;
      return reply(200, body());
    }
    if (sent.rev !== server.rev) return reply(409, { ...body(), refused: true, reason: "stale" });
    server.file = sent.looks;
    server.rev = `r${(server.n += 1)}`;
    return reply(200, body());
  };
  return server;
}

function page(server, stored) {
  const store = new Map(stored === undefined ? [] : [[KEY, JSON.stringify(stored)]]);
  const sets = [];
  const el = () => {
    const attrs = new Map();
    return { style: { setProperty() {}, removeProperty() {} }, setAttribute: (k, v) => attrs.set(k, v), getAttribute: (k) => attrs.get(k) ?? null, removeAttribute: (k) => attrs.delete(k), children: [], prepend() {}, appendChild() {}, classList: { contains: () => false } };
  };
  const doc = { documentElement: el(), body: el(), createElement: el };
  doc.documentElement.ownerDocument = doc;
  doc.documentElement.setAttribute("data-theme", "dark");
  const win = {
    location: {}, fetch: server?.fetch,
    localStorage: { getItem: (k) => store.get(k) ?? null, setItem: (k, v) => { sets.push(k); store.set(k, v); }, removeItem: (k) => store.delete(k) },
    matchMedia: () => ({ matches: false, addEventListener() {} }),
  };
  return { doc, win, store, sets };
}

const settle = async () => {
  for (let i = 0; i < 8; i += 1) await new Promise((done) => setImmediate(done));
};
const posts = (server) => server.calls.filter((c) => c.method === "POST");

test("the file's looks and active choice become the state; a page with nothing stored sends no merge", async () => {
  const server = fakeServer({ active: { light: "light-emissive", dark: "mine" }, technique: "dual", custom: [look("mine")] });
  const { doc, win } = page(server);
  await loadLooks(doc, win);
  const state = stateOf(win);
  assert.deepEqual([state.active.dark, state.technique, state.custom.map((l) => l.id)], ["mine", "dual", ["mine"]]);
  assert.equal(posts(server).length, 0);
  assert.equal(looksNote(win), null);
});

test("a save goes to the file with the current revision and never to localStorage", async () => {
  const server = fakeServer({ custom: [] });
  const { doc, win, sets } = page(server);
  await loadLooks(doc, win);
  choose("dark", "light-inky", doc, win);
  await settle();
  const [write] = posts(server);
  assert.equal(write.path, "/ui/v1/looks");
  assert.equal(write.sent.rev, "r0");
  assert.equal(write.sent.looks.active.dark, "light-inky");
  assert.ok(!sets.includes(KEY), "ah-optics is never written");
  assert.equal(win.localStorage.getItem(PENDING), null, "confirmed, so nothing waits");
  assert.equal(server.rev, "r1");
});

// F1's trace: the 800 ms wait lets the page mount before the file answers, and the user picks from the header menu.
function slowServer(file, stored) {
  const server = fakeServer(file);
  const answer = server.fetch;
  let open;
  const gate = new Promise((done) => { open = done; });
  server.fetch = async (path, init) => {
    await gate;
    return answer(path, init);
  };
  return { server, open, ...page(server, stored) };
}

test("slow load: a header-menu pick sends nothing until the load ends, and every look in the file is still there", async () => {
  const { server, open, doc, win } = slowServer({ active: { light: "light-inky", dark: "dark-emissive" }, technique: "dual", custom: [look("one"), look("two")] });
  const loading = loadLooks(doc, win);
  choose("dark", "light-emissive", doc, win);
  assert.equal(stateOf(win).active.dark, "light-emissive", "the click shows at once");
  await settle();
  assert.equal(posts(server).length, 0);
  open();
  await loading;
  await settle();
  assert.deepEqual(server.file.custom.map((l) => l.id), ["one", "two"]);
  assert.equal(server.file.active.dark, "light-emissive");
  assert.equal(server.file.technique, "dual", "what the pick did not touch stays the file's");
  assert.deepEqual(stateOf(win).custom.map((l) => l.id), ["one", "two"]);
  assert.equal(posts(server).length, 1);
  assert.equal(posts(server)[0].sent.rev, "r0");
});

test("the merge window: a pick during the merge keeps the file's looks and the merged ones", async () => {
  const server = fakeServer({ custom: [look("filed")] });
  const answer = server.fetch;
  let open;
  const gate = new Promise((done) => { open = done; });
  server.fetch = async (path, init) => {
    if (path.endsWith("/merge")) await gate;
    return answer(path, init);
  };
  const { doc, win } = page(server, { custom: [look("browser")] });
  const loading = loadLooks(doc, win);
  await settle();
  choose("dark", "light-emissive", doc, win);
  assert.equal(posts(server).length, 0, "the merge is held at the gate, and no write has gone past it");
  open();
  await loading;
  await settle();
  assert.deepEqual(server.file.custom.map((l) => l.id).sort(), ["browser", "filed"]);
  assert.equal(server.file.active.dark, "light-emissive");
});

test("a failed save waits in the journal and the next page's load replays it onto the file", async () => {
  const server = fakeServer({ custom: [look("filed")] });
  const first = page(server);
  await loadLooks(first.doc, first.win);
  server.down = true;
  const state = stateOf(first.win);
  save({ ...state, custom: [...state.custom, look("made")] }, first.doc, first.win);
  await settle();
  assert.deepEqual(server.file.custom.map((l) => l.id), ["filed"]);
  assert.match(looksNote(first.win), /waits in this browser/);

  server.down = false;
  server.file = { custom: [look("filed"), look("other")] };
  const next = page(server);
  next.win.localStorage.setItem(PENDING, first.win.localStorage.getItem(PENDING));
  await loadLooks(next.doc, next.win);
  await settle();
  assert.deepEqual(server.file.custom.map((l) => l.id).sort(), ["filed", "made", "other"]);
  assert.deepEqual(stateOf(next.win).custom.map((l) => l.id).sort(), ["filed", "made", "other"]);
  assert.equal(next.win.localStorage.getItem(PENDING), null, "replayed and confirmed, so the queue is empty");
});

test("a save is in the journal before anything is sent, so a tab closed mid-write loses nothing", async () => {
  const server = fakeServer({ custom: [] });
  const { doc, win } = page(server);
  await loadLooks(doc, win);
  server.fetch = () => new Promise(() => {});
  win.fetch = server.fetch;
  const state = stateOf(win);
  save({ ...state, custom: [look("made")] }, doc, win);
  const [entry] = Object.values(JSON.parse(win.localStorage.getItem(PENDING)));
  assert.deepEqual(entry.next.custom.map((l) => l.id), ["made"]);
});

test("a journal held through a corrupt file is not sent, and not lost; once the file reads it goes in", async () => {
  const server = fakeServer("corrupt");
  const first = page(server);
  await loadLooks(first.doc, first.win);
  const state = stateOf(first.win);
  save({ ...state, custom: [look("made")] }, first.doc, first.win);
  await settle();
  assert.equal(posts(server).length, 0);
  const waiting = first.win.localStorage.getItem(PENDING);
  assert.ok(waiting);

  server.file = { custom: [look("repaired")] };
  server.rev = "r9";
  const next = page(server);
  next.win.localStorage.setItem(PENDING, waiting);
  await loadLooks(next.doc, next.win);
  await settle();
  assert.deepEqual(server.file.custom.map((l) => l.id).sort(), ["made", "repaired"]);
});

test("a confirmed change is not replayed: a look deleted afterwards stays deleted", async () => {
  const server = fakeServer({ custom: [] });
  const first = page(server);
  await loadLooks(first.doc, first.win);
  const state = stateOf(first.win);
  save({ ...state, custom: [look("made")] }, first.doc, first.win);
  await settle();
  assert.equal(first.win.localStorage.getItem(PENDING), null);
  server.file = { custom: [] };
  server.rev = "r9";
  const next = page(server);
  await loadLooks(next.doc, next.win);
  await settle();
  assert.deepEqual(server.file.custom, []);
});

test("a junk journal is ignored and does not stop the load", async () => {
  for (const junk of ["{bad", '{"x":null}', '{"x":3}', "[]"]) {
    const server = fakeServer({ custom: [look("filed")] });
    const { doc, win } = page(server);
    win.localStorage.setItem(PENDING, junk);
    await loadLooks(doc, win);
    assert.deepEqual(stateOf(win).custom.map((l) => l.id), ["filed"], junk);
    assert.equal(looksNote(win), null, junk);
  }
});

test("a save during a load that fails does not revert the file's edit of a look this browser also stores", async () => {
  const server = fakeServer({ active: { light: "light-inky", dark: "dark-emissive" }, custom: [look("same", "edited in the file")] });
  server.down = true;
  const { doc, win } = page(server, { custom: [look("same", "old in storage")] });
  const loading = loadLooks(doc, win);
  choose("dark", "light-emissive", doc, win);
  await loading;
  server.down = false;
  await settle();
  // The flush that follows the failed load may itself have failed; the next change sends it.
  choose("light", "light-emissive", doc, win);
  await settle();
  assert.deepEqual(server.file.custom.map((l) => l.name), ["edited in the file"]);
  assert.equal(server.file.active.dark, "light-emissive");
  assert.equal(server.file.active.light, "light-emissive");
});

test("a save during a failed load with nothing stored still keeps every look in the file", async () => {
  const server = fakeServer({ custom: [look("filed")] });
  server.down = true;
  const { doc, win } = page(server);
  const loading = loadLooks(doc, win);
  choose("dark", "light-emissive", doc, win);
  await loading;
  server.down = false;
  await settle();
  choose("light", "light-emissive", doc, win);
  await settle();
  assert.deepEqual(server.file.custom.map((l) => l.id), ["filed"]);
});

test("a load that hangs times out: built-ins and a note, and every request carries the deadline", async () => {
  const seen = [];
  const hung = { fetch: (_, init) => new Promise((_, fail) => {
    seen.push(init.signal);
    init.signal.addEventListener("abort", () => fail(init.signal.reason));
  }) };
  const { doc, win } = page(hung, { custom: [look("stored")] });
  let told = 0;
  onLooks(() => { told += 1; }, win);
  await loadLooks(doc, win);
  assert.ok(seen[0] instanceof AbortSignal);
  assert.deepEqual(stateOf(win).custom, []);
  assert.match(looksNote(win), /did not answer/);
  assert.ok(told > 0, "the banner hears of it");
});

test("saves while one is flying collapse to the newest, and the next one carries the new revision", async () => {
  const server = fakeServer({ custom: [] });
  const { doc, win } = page(server);
  await loadLooks(doc, win);
  const state = stateOf(win);
  save({ ...state, technique: "dual" }, doc, win);
  save({ ...state, technique: "shadow" }, doc, win);
  save({ ...state, technique: "dual" }, doc, win);
  await settle();
  assert.equal(posts(server).length, 2);
  assert.equal(server.file.technique, "dual");
  assert.equal(posts(server)[1].sent.rev, "r1");
});

test("another tab's save is not lost: a stale write is redone on the file as it now is", async () => {
  const server = fakeServer({ custom: [look("old")] });
  const { doc, win } = page(server);
  await loadLooks(doc, win);
  server.file = { custom: [look("old"), look("theirs")] };
  server.rev = "elsewhere";
  const state = stateOf(win);
  save({ ...state, custom: [...state.custom, look("mine")] }, doc, win);
  await settle();
  assert.deepEqual(server.file.custom.map((l) => l.id).sort(), ["mine", "old", "theirs"]);
  assert.deepEqual(stateOf(win).custom.map((l) => l.id).sort(), ["mine", "old", "theirs"]);
});

test("rebase: my delete and edit win, theirs stand, an untouched slot takes theirs", () => {
  const a = look("a");
  const b = look("b");
  const base = { active: { light: "x", dark: "y" }, technique: "shadow", custom: [a, b] };
  const edited = { ...a, name: "edited" };
  const next = { active: { light: "mine", dark: "y" }, technique: "shadow", custom: [edited, look("new")] };
  const theirs = { active: { light: "x", dark: "their" }, technique: "dual", custom: [a, b, look("t")] };
  const out = rebase(base, next, theirs);
  assert.deepEqual(out.custom.map((l) => l.id), ["a", "t", "new"]);
  assert.equal(out.custom[0].name, "edited");
  assert.deepEqual(out.active, { light: "mine", dark: "their" });
  assert.equal(out.technique, "dual");
});

test("rebase: a look I edited that they deleted survives", () => {
  const a = look("a");
  const base = { active: { light: "x", dark: "y" }, technique: "shadow", custom: [a] };
  const edited = { ...a, name: "edited" };
  const out = rebase(base, { ...base, custom: [edited] }, { ...base, custom: [] });
  assert.deepEqual(out.custom.map((l) => l.name), ["edited"]);
});

test("rebase: a look they deleted that I did not touch stays deleted", () => {
  const a = look("a");
  const base = { active: { light: "x", dark: "y" }, technique: "shadow", custom: [a] };
  const out = rebase(base, base, { ...base, custom: [] });
  assert.deepEqual(out.custom, []);
});

test("a corrupt file: built-ins, a note naming it, and neither a write nor a merge is sent", async () => {
  const server = fakeServer("corrupt");
  const { doc, win, sets } = page(server, { active: { light: "x", dark: "x" }, custom: [look("stored")] });
  await loadLooks(doc, win);
  assert.deepEqual(stateOf(win).custom, []);
  assert.equal(stateOf(win).active.dark, "dark-emissive");
  assert.match(looksNote(win), /looks\.json could not be read/);
  choose("dark", "light-inky", doc, win);
  await settle();
  assert.equal(posts(server).length, 0);
  assert.ok(!sets.includes(KEY));
  assert.equal(stateOf(win).active.dark, "light-inky", "the click still holds for this page");
});

test("migration: stored looks go to the merge route with the stored active choice, and the key stays as it was", async () => {
  const stored = { active: { light: "light-inky", dark: "b1" }, technique: "dual", custom: [look("b1", "Browser")] };
  const server = fakeServer();
  const { doc, win, store, sets } = page(server, stored);
  const before = store.get(KEY);
  await loadLooks(doc, win);
  const [merge] = posts(server);
  assert.equal(merge.path, "/ui/v1/looks/merge");
  assert.deepEqual(merge.sent.custom.map((l) => l.id), ["b1"]);
  assert.equal(merge.sent.active.dark, "b1");
  assert.equal(merge.sent.technique, "dual");
  assert.deepEqual(stateOf(win).custom.map((l) => l.id), ["b1"]);
  assert.equal(store.get(KEY), before);
  assert.deepEqual(sets, []);
});

test("migration: a harness skin seed alone is not looks, so no merge; the seed still applies", async () => {
  const server = fakeServer({ custom: [] });
  const { doc, win } = page(server, { skin: "house" });
  await loadLooks(doc, win);
  assert.equal(posts(server).length, 0);
  assert.equal(stateOf(win).skin, "house");
});

test("a source with no looks route (the mock): storage is the store, as before, and nothing is noted", async () => {
  const server = fakeServer();
  server.status = 404;
  const { doc, win, sets } = page(server, { active: { light: "x", dark: "light-emissive" }, custom: [] });
  await loadLooks(doc, win);
  assert.equal(looksNote(win), null);
  choose("dark", "dark-emissive", doc, win);
  await settle();
  assert.equal(posts(server).length, 0);
  assert.deepEqual(sets, [KEY]);
});

test("a daemon that does not answer: built-ins and a note, not stale storage; the next save reaches it and clears the note", async () => {
  const server = fakeServer({ custom: [look("filed")] });
  server.down = true;
  const { doc, win, sets } = page(server, { custom: [look("stored")] });
  await loadLooks(doc, win);
  assert.deepEqual(stateOf(win).custom, []);
  assert.match(looksNote(win), /did not answer/);
  server.down = false;
  const state = stateOf(win);
  save({ ...state, custom: [look("made")] }, doc, win);
  await settle();
  assert.deepEqual(server.file.custom.map((l) => l.id).sort(), ["filed", "made"]);
  assert.equal(looksNote(win), null);
  assert.ok(!sets.includes(KEY));
});

test("a refused write says so and keeps the change for the next try", async () => {
  const server = fakeServer({ custom: [] });
  const { doc, win } = page(server);
  await loadLooks(doc, win);
  server.status = 500;
  choose("dark", "light-inky", doc, win);
  await settle();
  assert.match(looksNote(win), /not saved yet/);
  server.status = null;
  choose("light", "light-emissive", doc, win);
  await settle();
  assert.equal(server.file.active.dark, "light-inky");
  assert.equal(server.file.active.light, "light-emissive");
  assert.equal(looksNote(win), null);
});

test("a save made while the file is loading waits for it, and is not lost", async () => {
  const server = fakeServer({ custom: [look("filed")] });
  const { doc, win } = page(server);
  const loading = loadLooks(doc, win);
  choose("dark", "light-inky", doc, win);
  assert.equal(server.calls.filter((c) => c.method === "POST").length, 0);
  await loading;
  await settle();
  assert.equal(server.file.active.dark, "light-inky");
});

test("bootLooks loads the file once; a page with no fetch stays on storage and asks nothing", async () => {
  const server = fakeServer({ custom: [] });
  const { doc, win } = page(server);
  bootLooks(doc, win);
  await loadLooks(doc, win);
  assert.equal(server.calls.length, 1);
  const bare = page(undefined, { custom: [look("stored")] });
  bootLooks(bare.doc, bare.win);
  await loadLooks(bare.doc, bare.win);
  assert.deepEqual(stateOf(bare.win).custom.map((l) => l.id), ["stored"]);
});

test("a look saved with Dual and the graph option reaches the file, and a reload draws with them", async () => {
  const server = fakeServer({ custom: [] });
  const first = page(server);
  await loadLooks(first.doc, first.win);
  const saved = { ...look("c1"), profile: "dual", graphShadow: true };
  save({ ...stateOf(first.win), active: { light: "light-inky", dark: "c1" }, custom: [saved] }, first.doc, first.win);
  assert.equal(first.doc.documentElement.getAttribute("data-look-tech"), "dual");
  await settle();
  assert.deepEqual([server.file.custom[0].profile, server.file.custom[0].graphShadow], ["dual", true]);
  const again = page(server);
  await loadLooks(again.doc, again.win);
  assert.deepEqual([stateOf(again.win).custom[0].profile, stateOf(again.win).custom[0].graphShadow], ["dual", true]);
  assert.equal(again.doc.documentElement.getAttribute("data-look-tech"), "dual");
  assert.equal(again.doc.documentElement.getAttribute("data-look-graph"), "shadow");
});
