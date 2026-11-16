import { test } from "node:test";
import assert from "node:assert/strict";
import { createStore } from "../../web/ui/store.js";
import { mountApp as mount, until, find, text } from "./mount-app.mjs";

const manifest = async () => ({ epoch: "e1", seq: 1, building: false, slices: [{ id: "team:d/t", version: 1, as_of: 0, body: { name: "t" } }], pending: [] });
const reply = (seq) => ({ epoch: "e1", seq, resync: false, slices: [], removed: [] });

function rig(pollMs = 20) {
  const polls = [];
  const get = async (path, params) => {
    polls.push(path);
    await new Promise((r) => setTimeout(r, pollMs));
    return reply(params.since + 1);
  };
  const store = createStore({ get, manifest });
  return { store, polls };
}

const errors = () => {
  const seen = [];
  const original = console.error;
  console.error = (...a) => seen.push(a);
  return { seen, restore: () => (console.error = original) };
};

test("a throwing listener does not stop the healthy one or the poll, and the poll is not retried as an error", async () => {
  const log = errors();
  const { store, polls } = rig();
  let healthy = 0;
  store.subscribe(() => {
    throw new Error("boom");
  });
  store.subscribe(() => (healthy += 1));
  store.start(["summary"]);
  await new Promise((r) => setTimeout(r, 300));
  store.stop();
  log.restore();
  assert.ok(healthy > 3, `the healthy listener kept updating (${healthy})`);
  assert.equal(store.state.error, null, "a listener fault is not a poll error");
  assert.equal(store.state.errorSince, null);
  assert.equal(store.state.fault, "boom");
  assert.ok(polls.length >= 8 && polls.length <= 16, `about one poll per 20 ms, not a retry storm or a stall (${polls.length})`);
});

test("each listener's message is logged once", async () => {
  const log = errors();
  const { store } = rig();
  store.subscribe(() => {
    throw new Error("same");
  });
  store.start(["summary"]);
  await new Promise((r) => setTimeout(r, 200));
  store.stop();
  log.restore();
  assert.equal(log.seen.length, 1);
});

test("a listener that throws a different message each time is reported for each", async () => {
  const log = errors();
  const { store } = rig();
  const messages = ["m1", "m2", "m2", "m9"];
  let n = 0;
  store.subscribe(() => {
    throw new Error(messages[Math.min(n++, messages.length - 1)]);
  });
  store.start(["summary"]);
  await new Promise((r) => setTimeout(r, 200));
  store.stop();
  log.restore();
  assert.deepEqual(log.seen.map((a) => a[1].message), ["m1", "m2", "m9"]);
});

test("a listener that throws once and then succeeds clears the fault", async () => {
  const log = errors();
  const { store } = rig();
  let n = 0;
  store.subscribe(() => {
    if (n++ === 1) {
      throw new Error("once");
    }
  });
  store.start(["summary"]);
  await until(() => store.state.fault === "once", "the fault");
  await until(() => store.state.fault === null, "the fault cleared");
  store.stop();
  log.restore();
  assert.equal(log.seen.length, 1);
});

test("a listener that stops listening takes its fault with it", async () => {
  const log = errors();
  const { store } = rig();
  const off = store.subscribe(() => {
    throw new Error("gone");
  });
  store.start(["summary"]);
  await until(() => store.state.fault === "gone", "the fault");
  off();
  await until(() => store.state.fault === null, "the fault cleared");
  store.stop();
  log.restore();
});

test("100 distinct messages from one listener: a bounded set, at most 20 logs and 21 re-notifies", async () => {
  const log = errors();
  const { store } = rig(1);
  let n = 0;
  store.subscribe(() => {
    throw new Error(`id ${n++}`);
  });
  const healthy = [];
  store.subscribe((s) => healthy.push(s.fault));
  store.start(["summary"]);
  await until(() => n >= 100, "100 distinct throws", 5000);
  store.stop();
  log.restore();
  assert.equal(log.seen.length, 20);
  const changes = healthy.filter((f, i) => f !== healthy[i - 1]).length;
  assert.ok(changes <= 22, `fault changed ${changes} times`);
});

test("the page shows a failed part", async () => {
  const log = errors();
  const m = await mount({ view: null });
  m.store.subscribe((s) => {
    if (s.ready && !s.fault) {
      throw new Error("pane broke");
    }
  });
  m.store.setUi({ view: "teams" });
  await until(() => find(m.root, ".notice").some((n) => /pane broke/.test(text(n))), "the fault notice");
  log.restore();
});

test("no fault, no notice", async () => {
  const m = await mount({ view: null });
  assert.equal(find(m.root, ".notice").filter((n) => /failed/.test(text(n))).length, 0);
});
