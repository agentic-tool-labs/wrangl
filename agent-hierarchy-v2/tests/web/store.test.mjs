import { test, afterEach } from "node:test";
import assert from "node:assert/strict";
import { createMock } from "../../web/mock/model.mjs";
import { createStore, backoffMs, BACKOFF_MS, AUTH_GIVE_UP_MS, MAX_WINDOWS, ageOf, ageText, freshness } from "../../web/ui/store.js";

// The store is driven through the mock model, over a transport that round-trips every document through JSON like the wire does.
const wire = (v) => JSON.parse(JSON.stringify(v));
const LIST = "list:feed:team:a1b2c3d4/ship";

function rig({ fail = () => false, noResyncFlag = false, stale = () => null } = {}) {
  const model = createMock();
  const log = { gets: [], manifests: [], timers: [], signals: [] };
  const get = async (path, params = {}, signal) => {
    log.gets.push({ path, params });
    if (fail()) {
      throw new Error("down");
    }
    if (path === "/events") {
      log.signals.push(signal);
      const reply = wire(await model.poll({ since: Number(params.since), epoch: params.epoch, sub: params.sub ? params.sub.split(",") : null, fresh: params.fresh ? params.fresh.split(",") : [], wait: 0.03, signal }));
      if (stale()) {
        reply.data_stale = true;
        reply.observed_at = stale();
      }
      if (noResyncFlag) {
        delete reply.resync;
        await new Promise((r) => setTimeout(r, 1));
      }
      return reply;
    }
    const id = path.slice("/list/".length);
    return wire(model.list(id, { after: params.after ?? null, before: params.before ?? null, limit: params.limit }));
  };
  const manifest = async (name) => {
    log.manifests.push(name);
    if (fail()) {
      throw new Error("down");
    }
    return wire(model.manifest(name));
  };
  const setTimer = (fn, ms) => {
    log.timers.push(ms);
    return setTimeout(fn, 1);
  };
  const store = createStore({ get, manifest, setTimer, clearTimer: clearTimeout, random: () => 0.5 });
  open.add(store);
  return { model, log, store };
}

// A test that fails before its own stop() must not leave a poll running.
const open = new Set();
afterEach(() => {
  for (const s of open) {
    s.stop();
  }
  open.clear();
});

const until = async (pred, what, ms = 3000) => {
  const end = Date.now() + ms;
  while (!pred()) {
    assert.ok(Date.now() < end, `timed out waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 5));
  }
};
const VIEWS = ["tree", "members", "exchanges"];
const ACT = "activity:a1b2c3d4/sess-architect";

test("R6a: the first manifest seeds the store from every view", async () => {
  const { store } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  assert.ok(store.state.slices.has("team:a1b2c3d4/ship"));
  assert.ok(store.state.slices.has(ACT));
  assert.ok(store.state.slices.has("exchange:a1b2c3d4/20261018-0110-aa01"));
  store.stop();
});

test("R6a: an epoch change replaces the store wholesale and keeps the UI state", async () => {
  const { store, model } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  store.setUi({ view: "graph", selected: "member:a1b2c3d4/ship/architect", tab: "overview" });
  const before = store.state;
  const oldEpoch = before.epoch;
  model.restart();
  await until(() => store.state.epoch !== oldEpoch, "the new epoch");
  assert.equal(store.state.epoch, model.epoch);
  assert.notEqual(store.state.slices, before.slices, "the slice Map was replaced");
  assert.deepEqual(store.state.ui, { view: "graph", selected: "member:a1b2c3d4/ship/architect", tab: "overview" });
  store.stop();
});

test("R6a: an epoch change reloads the manifests once per view", async () => {
  const { store, model, log } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  const seeded = log.manifests.length;
  model.restart();
  await until(() => log.manifests.length > seeded, "the reload");
  assert.equal(log.manifests.length - seeded, VIEWS.length);
  store.stop();
});

test("R6a: a reply carrying a new epoch reloads the store even when it does not say resync", async () => {
  const { store, model, log } = rig({ noResyncFlag: true });
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  const seeded = log.manifests.length;
  const oldEpoch = store.state.epoch;
  model.restart();
  await new Promise((r) => setTimeout(r, 300));
  assert.notEqual(model.epoch, oldEpoch, "the model really restarted");
  assert.equal(store.state.epoch, model.epoch, "the poll's epoch mismatch reloaded the store");
  assert.equal(log.manifests.length - seeded, VIEWS.length, "one manifest read per view");
  store.stop();
});

test("R6a: a tombstone removes the slice, and nothing else", async () => {
  const { store, model } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  const other = store.state.slices.get("activity:a1b2c3d4/sess-reviewer");
  model.remove(ACT);
  await until(() => !store.state.slices.has(ACT), "the removal");
  assert.equal(store.state.slices.get("activity:a1b2c3d4/sess-reviewer"), other, "an unrelated slice kept its reference");
  store.stop();
});

test("R6a: reconnect backoff is 0.5, 1, 2, 4, 8, 16 then 30 s, within +-20%", () => {
  assert.deepEqual(BACKOFF_MS, [500, 1000, 2000, 4000, 8000, 16000, 30000]);
  for (const [attempt, base] of BACKOFF_MS.entries()) {
    assert.equal(backoffMs(attempt, () => 0.5), base);
    assert.equal(backoffMs(attempt, () => 0), Math.round(base * 0.8));
    assert.equal(backoffMs(attempt, () => 1), Math.round(base * 1.2));
  }
  assert.equal(backoffMs(99, () => 0.5), 30000, "capped at 30 s");
  assert.notEqual(backoffMs(3, () => 0.1), backoffMs(3, () => 0.9), "jittered");
});

test("R6a: a failing read backs off along the schedule, goes amber, and recovers", async () => {
  let down = false;
  const { store, log } = rig({ fail: () => down });
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  down = true;
  await until(() => log.timers.length >= 3, "three retries");
  assert.deepEqual(log.timers.slice(0, 3), [500, 1000, 2000]);
  assert.notEqual(store.state.errorSince, null);
  assert.equal(freshness(store.state, Date.now()).amber, true);
  assert.match(freshness(store.state, Date.now()).text, /^reconnecting · data \d+ s old$/);
  assert.equal(freshness(store.state, Date.now()).title, undefined, "negative: an amber chip vouches for nothing");
  assert.ok(store.state.slices.size > 0, "data stays visible while down");
  down = false;
  await until(() => store.state.errorSince === null, "recovery");
  assert.equal(freshness(store.state, Date.now()).amber, false);
  assert.match(freshness(store.state, Date.now()).title, /process reading.*file change it missed/, "a calm chip says what it covers");
  store.stop();
});

test("#355: a 401 or 503 mid-session keeps polling, shows it, and recovers without a reload", async () => {
  class Ended extends Error {}
  for (const [status, text] of [[401, /^session expired · run ahd ui start$/], [503, /^reconnecting/]]) {
    let down = false;
    const { store, log } = rig({ fail: () => { if (down) throw Object.assign(new Ended(String(status)), { status }); return false; } });
    store.start(VIEWS);
    await until(() => store.state.ready, "ready");
    down = true;
    await until(() => log.timers.length >= 2, "two retries");
    assert.equal(store.state.ended, false);
    assert.equal(freshness(store.state, Date.now()).amber, true);
    assert.match(freshness(store.state, Date.now()).text, text);
    assert.ok(store.state.slices.size > 0, "data stays visible while down");
    down = false;
    await until(() => store.state.errorSince === null, "recovery");
    assert.equal(freshness(store.state, Date.now()).amber, false);
    assert.ok(log.gets.some((g) => g.path === "/events" && g.params.wait === 0), "a retry does not long-poll");
    store.stop();
  }
});

test("b276: a 401 that outlasts AUTH_GIVE_UP_MS ends the poll; a 503 as long does not", async () => {
  const model = createMock();
  class Ended extends Error {}
  for (const status of [401, 503]) {
    let t = 0;
    let retries = 0;
    const store = createStore({
      get: async () => {
        throw Object.assign(new Ended(String(status)), { status });
      },
      manifest: async (n) => wire(model.manifest(n)),
      now: () => t,
      setTimer: (fn) => ((retries += 1), (t += 60_000), setTimeout(fn, 1)),
      clearTimer: clearTimeout,
      random: () => 0.5,
    });
    open.add(store);
    store.start(VIEWS);
    if (status === 401) {
      await until(() => store.state.ended, "the stale-token end");
      assert.equal(store.state.endedWhy, "401");
      assert.ok(t >= AUTH_GIVE_UP_MS);
    } else {
      await until(() => retries >= 15, "fifteen 503 retries");
      assert.equal(store.state.ended, false, "negative: a long 503 is a daemon down, and keeps polling");
    }
    store.stop();
  }
});

test("R6a: an ended session stops the poll", async () => {
  const model = createMock();
  class Ended extends Error {}
  let calls = 0;
  const store = createStore({
    get: async () => {
      calls += 1;
      throw new Ended();
    },
    manifest: async (n) => wire(model.manifest(n)),
  });
  store.start(VIEWS);
  await until(() => store.state.ended, "ended");
  const seen = calls;
  await new Promise((r) => setTimeout(r, 50));
  assert.equal(calls, seen, "no further reads");
});

test("C6: a reply of changed slices keeps every unchanged slice by reference", async () => {
  const { store, model } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  // The store writes a poll into its map in place, so the comparison is against a copy taken before it.
  const before = new Map(store.state.slices);
  const rev = store.state.rev;
  const ids = [...before.keys()];
  model.tick();
  await until(() => store.state.rev !== rev, "the change");
  let changed = 0;
  for (const id of ids) {
    if (store.state.slices.get(id) !== before.get(id)) {
      changed += 1;
    }
  }
  assert.equal(changed, 1, "one activity slice changed; the other references are the same");
  store.stop();
});

test("C6: a slice republished with the same body keeps its body by reference", async () => {
  const { store, model } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  const before = store.state.slices.get(ACT);
  const seq = store.state.seq;
  model.publish([{ id: ACT, as_of: before.as_of + 1, body: JSON.parse(JSON.stringify(before.body)) }]);
  await until(() => store.state.seq > seq, "the republish");
  const after = store.state.slices.get(ACT);
  assert.notEqual(after.version, before.version, "the slice did change (its version moved)");
  assert.equal(after.body, before.body, "the body object is the old one");
  assert.equal(after.body.context, before.body.context);
});

test("C6: 50 changed slices re-render at most 50 slices' worth, the rest by reference", async () => {
  const { store, model } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  const before = new Map(store.state.slices);
  const ex = [...before.values()].filter((s) => s.id.startsWith("exchange:"));
  model.publish(Array.from({ length: 50 }, (_, i) => ({ id: `exchange:a1b2c3d4/bulk-${i}`, as_of: 1, body: { id: `bulk-${i}`, open: true } })));
  await until(() => store.state.slices.size >= before.size + 50, "the batch");
  for (const s of ex) {
    assert.equal(store.state.slices.get(s.id), s, `${s.id} kept its reference`);
  }
  store.stop();
});

test("each changed poll moves rev by one and names exactly the ids it changed or removed; a poll that changes nothing moves neither", async () => {
  const { store, model } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  const rev = store.state.rev;
  model.tick();
  await until(() => store.state.rev === rev + 1, "one change");
  assert.deepEqual(store.state.touched, { rev: rev + 1, ids: [ACT] });
  const exchange = [...store.state.slices.keys()].find((id) => id.startsWith("exchange:"));
  model.remove(exchange);
  await until(() => store.state.rev === rev + 2, "a removal");
  assert.deepEqual(store.state.touched, { rev: rev + 2, ids: [exchange] });
  const touched = store.state.touched;
  const seq = store.state.seq;
  await new Promise((r) => setTimeout(r, 120));
  assert.equal(store.state.rev, rev + 2, "no change, no rev");
  assert.equal(store.state.touched, touched);
  assert.equal(store.state.seq, seq);
  store.stop();
});

test("C6: a reply with no changed slice leaves the slice Map untouched", async () => {
  const { store } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  const map = store.state.slices;
  const seq = store.state.seq;
  await new Promise((r) => setTimeout(r, 120));
  assert.equal(store.state.slices, map, "the same Map instance");
  assert.equal(store.state.seq, seq);
  store.stop();
});

test("C7: at steady state the store schedules no timers of its own", async () => {
  const { store, log } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  await new Promise((r) => setTimeout(r, 200));
  assert.deepEqual(log.timers, []);
  store.stop();
});

test("R6a: changing the subscription restarts the one poll; the old request is aborted", async () => {
  const { store, log } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready && log.signals.length > 0, "first poll");
  const first = log.signals[0];
  store.subscribeIds(["worktree:a1b2c3d4/ship/architect"]);
  await until(() => log.gets.some((g) => g.path === "/events" && g.params.sub.includes("worktree:a1b2c3d4/ship/architect")), "the new sub");
  assert.equal(first.aborted, true, "the previous request was aborted, not left running");
  assert.match(log.gets.at(-1).params.sub, /^view:tree,view:members,view:exchanges,worktree:/);
  store.stop();
});

test("R6a: the poll carries since, epoch and the union of the views", async () => {
  const { store, log, model } = rig();
  store.start(VIEWS);
  await until(() => log.gets.some((g) => g.path === "/events"), "a poll");
  const poll = log.gets.find((g) => g.path === "/events");
  assert.equal(poll.params.epoch, model.epoch);
  assert.equal(poll.params.since, model.seq);
  assert.equal(poll.params.sub, "view:tree,view:members,view:exchanges");
  store.stop();
});

const flushed = () => new Promise((r) => queueMicrotask(r));

test("a lease release moves rev and names the released ids in touched at that rev", async () => {
  const { store } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  const id = "transcript:a1b2c3d4/sess-architect";
  store.lease("t", [id]);
  await flushed();
  store.lease("t", []);
  await flushed();
  assert.deepEqual(store.state.touched, { rev: store.state.rev, ids: [id] });
  store.stop();
});

test("leases changed in one task restart the poll once: a release then a lease is one restart", async () => {
  const { store, log } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  await new Promise((r) => setTimeout(r, 40));
  store.lease("old", ["transcript:a1b2c3d4/sess-architect"]);
  await new Promise((r) => setTimeout(r, 40));
  const Real = globalThis.AbortController;
  let restarts = 0;
  globalThis.AbortController = class extends Real {
    constructor() {
      super();
      restarts += 1;
    }
  };
  try {
    store.lease("old", []);
    store.lease("new", ["transcript:a1b2c3d4/sess-reviewer"]);
    assert.equal(restarts, 0, "no poll restarts inside the task that changed the leases");
    await Promise.resolve();
    assert.equal(restarts, 1, "the two changes restart the poll once");
  } finally {
    globalThis.AbortController = Real;
  }
  store.stop();
});

test("C8: at most 3 windows of a list are held; the farthest drops", async () => {
  const { store } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  let win = await store.page(LIST, { limit: 2 });
  for (let i = 0; i < 4 && win.next; i += 1) {
    win = await store.page(LIST, { after: win.next, limit: 2 });
  }
  const held = store.lists(LIST).windows;
  assert.equal(held.length, MAX_WINDOWS);
  const ids = held.flatMap((w) => w.items.map((x) => x.id));
  assert.equal(ids.length, 6);
  assert.equal(new Set(ids).size, 6, "no row twice");
  assert.ok(!ids.includes("exchange:a1b2c3d4/20261018-0153-aa10"), "the newest window, farthest from the last, was dropped");
  store.stop();
});

test("C8: scrolling back up drops the window at the far end instead", async () => {
  const { store } = rig();
  store.start(VIEWS);
  await until(() => store.state.ready, "ready");
  let win = await store.page(LIST, { limit: 2 });
  win = await store.page(LIST, { after: win.next, limit: 2 });
  win = await store.page(LIST, { after: win.next, limit: 2 });
  const head = store.lists(LIST).windows[0];
  await store.page(LIST, { after: win.next, limit: 2 });
  assert.equal(store.lists(LIST).windows.length, 3);
  await store.page(LIST, { before: store.lists(LIST).windows[0].prev, limit: 2 });
  const held = store.lists(LIST).windows;
  assert.equal(held.length, 3);
  assert.equal(held[0].items[0].id, head.items[0].id, "the newest window is back at the head");
  store.stop();
});

test("the age model: as_of 0 reads as stream freshness; any other as_of as now minus it", () => {
  const now = 1_000_000;
  assert.deepEqual(ageOf({ as_of: 0 }, { freshAt: now - 7000 }, now), { live: true, seconds: 7 });
  assert.deepEqual(ageOf({ as_of: now - 125_000 }, { freshAt: now }, now), { live: false, seconds: 125 });
  assert.equal(ageText({ live: true, seconds: 7 }), "live · 7 s");
  assert.equal(ageText({ live: false, seconds: 125 }), "2 m ago");
  assert.equal(ageText({ live: false, seconds: 40 }), "40 s ago");
  assert.equal(ageText({ live: true, seconds: null }), "—");
  assert.equal(ageText({ live: false, seconds: 7200 }), "2 h ago");
});

test("a poll reply's data freshness reaches the store, and a later reply clears it", async () => {
  let observed = "2026-11-03T12:00:00Z";
  const { store } = rig({ stale: () => observed });
  store.start(VIEWS);
  await until(() => store.state.dataStale === true, "stale from a poll");
  assert.equal(store.state.observedAt, observed);
  observed = null;
  await until(() => store.state.dataStale === false, "fresh from a later poll");
  store.stop();
});

test("the chip reads the served data freshness, not whether a reply arrived", () => {
  const now = Date.parse("2026-11-03T12:10:00Z");
  const up = { freshAt: now, errorSince: null };
  assert.deepEqual(freshness({ ...up, dataStale: true, observedAt: "2026-11-03T12:00:00Z" }, now), { amber: true, text: "data stale · observed 10 m ago" });
  assert.deepEqual(freshness({ ...up, dataStale: true, observedAt: null }, now), { amber: true, text: "data stale" });
  // Negative: a current observation, or a server that serves no flag, is live.
  const calm = freshness({ ...up, dataStale: false, observedAt: "2026-11-03T12:09:59Z" }, now);
  assert.deepEqual([calm.amber, calm.text], [false, "live"]);
  assert.deepEqual([freshness(up, now).amber, freshness(up, now).text], [false, "live"]);
  // A lost connection outranks it.
  assert.match(freshness({ ...up, errorSince: now, dataStale: true }, now).text, /^reconnecting/);
});
