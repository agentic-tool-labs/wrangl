import { test } from "node:test";
import assert from "node:assert/strict";
import { installDom, settle } from "./dom.mjs";
import { until, find, text, mountApp as mount } from "./mount-app.mjs";

const { PerfStrip, summarize, COUNTERS_READ, POLL_MS, FPS_EVERY_MS, FPS_WINDOW_MS } =await import("../../web/ui/perfstrip.js");
const { html, render } = await import("../../web/ui/h.js");
const { createMock, READ_MODEL_COUNTERS } = await import("../../web/mock/model.mjs");

const doc = (counters) => ({ counters: { drift: 0, reread_retry: 0, coalesce_ms: 8, projector_faults: 0, ...counters }, parked: [], as_of: 1 });

test("native frame and timer defaults keep their global receiver during measurement and cleanup", () => {
  const names = ["requestAnimationFrame", "cancelAnimationFrame", "setInterval", "clearInterval"];
  const saved = new Map(names.map((name) => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  const calls = [];
  try {
    for (const name of names) {
      globalThis[name] = function () {
        assert.equal(this, globalThis, `${name} receiver`);
        calls.push(name);
        return 41;
      };
    }
    const strip = new PerfStrip();
    strip.props = { now: () => 100, tabVisible: () => true };
    assert.doesNotThrow(() => strip.measure());
    assert.equal(strip.frame, 41);
    strip.env.setInterval(() => {}, 10);
    strip.env.clearInterval(41);
    assert.doesNotThrow(() => strip.end());
    for (const name of names) assert.ok(calls.includes(name), name);
  } finally {
    for (const [name, descriptor] of saved) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor);
      else delete globalThis[name];
    }
  }
});

test("injected functions stay unchanged and absent native functions stay absent", () => {
  const strip = new PerfStrip();
  const injected = { observer: class Observer {}, raf() {}, caf() {}, setInterval() {}, clearInterval() {}, now() {} };
  strip.props = injected;
  const env = strip.env;
  for (const name of ["raf", "caf", "setInterval", "clearInterval", "now"]) assert.equal(env[name], injected[name]);
  assert.equal(env.Observer, injected.observer);
  const names = ["requestAnimationFrame", "cancelAnimationFrame", "setInterval", "clearInterval"];
  const saved = new Map(names.map((name) => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  const dateNow = Date.now;
  try {
    for (const name of names) globalThis[name] = undefined;
    Date.now = function () {
      assert.equal(this, Date);
      return 123;
    };
    strip.props = {};
    const missing = strip.env;
    for (const name of ["raf", "caf", "setInterval", "clearInterval"]) assert.equal(missing[name], undefined);
    assert.equal(missing.now(), 123);
  } finally {
    Date.now = dateNow;
    for (const [name, descriptor] of saved) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor);
      else delete globalThis[name];
    }
  }
});

// A strip on fake timers, observer, frames and clock, so every cadence is driven by the test.
function rig({ replies = [doc({})] } = {}) {
  const root = installDom();
  const env = { reads: 0, intervals: new Map(), cleared: [], frames: [], cancelled: [], clock: 1_000_000, observers: [], disconnected: 0 };
  let next = 1;
  class Observer {
    constructor(cb) {
      this.cb = cb;
      env.observers.push(this);
    }
    observe() {}
    disconnect() {
      env.disconnected += 1;
    }
  }
  const read = async () => {
    const reply = replies[Math.min(env.reads, replies.length - 1)];
    env.reads += 1;
    if (reply instanceof Error) {
      throw reply;
    }
    return reply;
  };
  const props = {
    read,
    observer: Observer,
    setInterval: (fn, ms) => (env.intervals.set(next, { fn, ms }), next++),
    clearInterval: (id) => (env.intervals.delete(id), env.cleared.push(id)),
    raf: (fn) => (env.frames.push(fn), env.frames.length),
    caf: (id) => env.cancelled.push(id),
    now: () => env.clock,
  };
  render(html`<${PerfStrip} ...${props} />`, root);
  const see = (on) => env.observers[0].cb([{ isIntersecting: on }]);
  return { root, env, see, ticks: (ms) => [...env.intervals.values()].filter((i) => i.ms === ms) };
}

// ---- the summary

test("summarize: the reread rate is the retry delta per second between two samples; a first sample, a missing counter, or a counter reset gives no negative rate", () => {
  const first = summarize(doc({ reread_retry: 10 }), null, 1000);
  assert.equal(first.rereads, null);
  assert.deepEqual(first.sample, { at: 1000, retries: 10 });
  assert.equal(summarize(doc({ reread_retry: 30 }), first.sample, 6000).rereads, 4, "20 retries over 5 s");
  assert.equal(summarize(doc({ reread_retry: 2 }), first.sample, 6000).rereads, 0, "a reset reads as zero, not negative");
  assert.equal(summarize({ counters: {} }, first.sample, 6000).rereads, null);
  const s = summarize(doc({ drift: 3, coalesce_ms: 40, projector_faults: 2 }), null, 1);
  assert.deepEqual([s.drift, s.coalesce, s.faults], [3, 40, 2]);
  assert.deepEqual([summarize({ counters: {} }, null, 1).drift, summarize({}, null, 1).faults], [null, null], "absent counters are not zero");
});

test("every counter the strip reads is one the daemon's read-model document carries, and the strip shows no lag", () => {
  const served = new Set(Object.keys(createMock().readModel().counters));
  for (const name of COUNTERS_READ) {
    assert.ok(served.has(name), `${name} is not a read-model counter`);
  }
  assert.deepEqual([...served].sort(), [...READ_MODEL_COUNTERS].sort());
  assert.equal(served.has("lag_p95_ms"), false);
  assert.equal("lag" in summarize(doc({}), null, 1), false);
});

// ---- cadence

test("off screen the strip reads nothing, polls nothing and asks for no frame", async () => {
  const { env } = rig();
  await settle();
  assert.equal(env.reads, 0);
  assert.equal(env.intervals.size, 0);
  assert.equal(env.frames.length, 0);
});

test("on screen: one read now, a poll every 5 s and a frame sample every 10 s", async () => {
  const { env, see, ticks } = rig();
  see(true);
  await settle();
  assert.equal(env.reads, 1);
  assert.equal(ticks(POLL_MS).length, 1, "a 5 s poll");
  assert.equal(ticks(FPS_EVERY_MS).length, 1, "a 10 s frame sample");
  assert.equal(POLL_MS, 5000);
  assert.equal(FPS_EVERY_MS, 10000);
  ticks(POLL_MS)[0].fn();
  await settle();
  assert.equal(env.reads, 2);
});

test("leaving the screen clears both timers and cancels the pending frame", async () => {
  const { env, see } = rig();
  see(true);
  await settle();
  see(false);
  assert.equal(env.intervals.size, 0, "no timer survives");
  assert.ok(env.cancelled.length >= 1, "the pending frame is cancelled");
});

test("a reply in flight when the strip leaves the screen is not shown", async () => {
  const root = installDom();
  let resolve;
  const observers = [];
  render(
    html`<${PerfStrip}
      read=${() => new Promise((r) => (resolve = r))}
      observer=${class {
        constructor(cb) {
          observers.push(cb);
        }
        observe() {}
        disconnect() {}
      }}
      setInterval=${() => 1}
      clearInterval=${() => {}}
      raf=${() => 1}
      caf=${() => {}}
      now=${() => 5}
    />`,
    root,
  );
  observers[0]([{ isIntersecting: true }]);
  observers[0]([{ isIntersecting: false }]);
  resolve(doc({ drift: 99 }));
  await settle();
  assert.equal(text(find(root, "footer")[0]).includes("99"), false);
});

test("unmounting disconnects the observer and clears the timers", async () => {
  const { env, see, root } = rig();
  see(true);
  await settle();
  render(null, root);
  assert.equal(env.disconnected, 1);
  assert.equal(env.intervals.size, 0);
});

// ---- what it shows

test("it shows rereads/s, drift, coalesce_ms, projector faults and fps, and no lag; a counter the daemon omits shows a dash", async () => {
  const { see, env, ticks, root } = rig({ replies: [doc({ reread_retry: 0 }), doc({ reread_retry: 10, drift: 2, projector_faults: 4 }), { counters: { drift: 1 }, parked: [], as_of: 1 }] });
  see(true);
  await settle();
  const strip = () => text(find(root, "footer")[0]);
  assert.equal(strip().includes("lag"), false);
  assert.match(strip(), /projector faults 0/);
  assert.match(strip(), /rereads\/s —/);
  env.clock += 5000;
  ticks(POLL_MS)[0].fn();
  await settle();
  assert.match(strip(), /rereads\/s 2/, "10 retries in 5 s");
  assert.match(strip(), /drift 2/);
  assert.match(strip(), /coalesce 8 ms/);
  assert.match(strip(), /projector faults 4/);
  env.clock += 5000;
  ticks(POLL_MS)[0].fn();
  await settle();
  assert.match(strip(), /projector faults —/);
  assert.match(strip(), /coalesce —/);
});

test("a hidden tab skips the poll and the frame sample; a shown tab does not", async () => {
  let shown = true;
  const root = installDom();
  const env = { reads: 0, frames: 0, ticks: [] };
  let observer;
  render(
    html`<${PerfStrip}
      read=${async () => (env.reads++, doc({}))}
      tabVisible=${() => shown}
      observer=${class {
        constructor(cb) {
          observer = cb;
        }
        observe() {}
        disconnect() {}
      }}
      setInterval=${(fn) => (env.ticks.push(fn), env.ticks.length)}
      clearInterval=${() => {}}
      raf=${() => (env.frames++, 1)}
      caf=${() => {}}
      now=${() => 5}
    />`,
    root,
  );
  observer([{ isIntersecting: true }]);
  await settle();
  assert.deepEqual([env.reads, env.frames], [1, 1], "shown: one read, one frame request");
  shown = false;
  env.ticks[0]();
  env.ticks[1]();
  await settle();
  assert.deepEqual([env.reads, env.frames], [1, 1], "hidden: neither runs");
  shown = true;
  env.ticks[0]();
  env.ticks[1]();
  await settle();
  assert.deepEqual([env.reads, env.frames], [2, 2], "shown again: both run");
});

test("a poll tick while the previous read is still in flight starts no second read, and one after it settles does", async () => {
  const root = installDom();
  const env = { reads: 0, ticks: [], resolve: null };
  let observer;
  render(
    html`<${PerfStrip}
      read=${() => (env.reads++, new Promise((r) => (env.resolve = r)))}
      observer=${class {
        constructor(cb) {
          observer = cb;
        }
        observe() {}
        disconnect() {}
      }}
      setInterval=${(fn) => (env.ticks.push(fn), env.ticks.length)}
      clearInterval=${() => {}}
      raf=${() => 1}
      caf=${() => {}}
      now=${() => 5}
    />`,
    root,
  );
  observer([{ isIntersecting: true }]);
  await settle();
  env.ticks[0]();
  env.ticks[0]();
  await settle();
  assert.equal(env.reads, 1, "ticks during a slow read start nothing");
  env.resolve(doc({}));
  await settle();
  env.ticks[0]();
  await settle();
  assert.equal(env.reads, 2, "after the reply the next tick reads");
});

test("a read that fails frees the in-flight slot, so the next tick tries again", async () => {
  const { see, env, ticks } = rig({ replies: [new Error("boom"), doc({})] });
  see(true);
  await settle();
  ticks(POLL_MS)[0].fn();
  await settle();
  assert.equal(env.reads, 2);
});

test("a failed read says the read model is unavailable, and the next good read clears it", async () => {
  const { see, ticks, root } = rig({ replies: [new Error("404 no_read_model"), doc({})] });
  see(true);
  await settle();
  assert.match(text(find(root, "footer")[0]), /read model unavailable/);
  ticks(POLL_MS)[0].fn();
  await settle();
  assert.equal(text(find(root, "footer")[0]).includes("unavailable"), false);
});

test("fps: frames are counted for one second and then no more are requested", async () => {
  const { see, env, root } = rig();
  see(true);
  await settle();
  assert.equal(env.frames.length, 1, "the first frame of the sample");
  for (let i = 0; i < 59; i += 1) {
    env.clock += 16;
    env.frames.shift()();
  }
  env.clock += 100;
  env.frames.shift()();
  const asked = env.frames.length;
  assert.equal(asked, 0, "no frame is requested once the window is over");
  await settle();
  assert.match(text(find(root, "footer")[0]), /fps 5\d/, "about 60 frames over about 1.04 s");
  assert.equal(FPS_WINDOW_MS, 1000);
});

// ---- the mock route and the page

test("the mock serves the counters, and no read model is the 404 case", () => {
  const model = createMock();
  const a = model.readModel();
  assert.deepEqual(Object.keys(a).sort(), ["as_of", "counters", "parked"]);
  assert.ok(a.counters.coalesce_ms >= 0);
  model.setReadModel(false);
  assert.equal(model.readModel(), null);
});

test("on the page the strip is absent until Diagnostics is on and, with nothing observing it, makes no request", async () => {
  const m = await mount();
  assert.equal(find(m.root, "footer.perf-strip").length, 0);
  m.store.setUi({ diagnostics: true });
  await until(() => find(m.root, "footer.perf-strip").length === 1, "the strip");
  assert.match(text(find(m.root, "footer.perf-strip")[0]), /fps —/);
  assert.equal(m.log.lists.some((l) => l.path === "/read-model"), false);
});
