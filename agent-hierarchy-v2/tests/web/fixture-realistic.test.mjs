import { test } from "node:test";
import assert from "node:assert/strict";

const load = async (env, tag) => {
  const before = { ...process.env };
  Object.assign(process.env, env);
  try {
    return await import(`../../web/mock/fixture.mjs?${tag}`);
  } finally {
    for (const key of Object.keys(env)) {
      if (before[key] === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = before[key];
      }
    }
  }
};

const exchanges = (fixture) => fixture.buildSlices(Date.now()).filter((s) => s.id.startsWith("exchange:"));

test("the realistic fixture has many exchanges and members, and excerpts at the 4 KB cap", async () => {
  const fixture = await load({ AH_MOCK_FIXTURE: "realistic" }, "realistic");
  const list = exchanges(fixture);
  assert.ok(list.length >= 1500, `exchanges: ${list.length}`);
  const text = fixture.exchangeText("done-1", true);
  assert.equal(text.request_excerpt.length, 4096);
  assert.equal(text.response_excerpt.length, 4096);
  assert.equal(fixture.exchangeText("open-1", false).response_excerpt, null);
  const body = list[0].body;
  assert.ok(body.preview.length <= 160);
  assert.equal(body.request_bytes, 4096);
  assert.equal("request_excerpt" in body, false);
});

test("the default fixture is unchanged: few exchanges, short excerpts", async () => {
  const fixture = await load({ AH_MOCK_FIXTURE: "", AH_MOCK_MEMBERS: "", AH_MOCK_OPEN: "", AH_MOCK_DONE: "", AH_MOCK_EXCERPT_BYTES: "" }, "default");
  assert.ok(exchanges(fixture).length < 100);
  assert.ok(fixture.exchangeText("x", true).request_excerpt.length < 100);
});

const sliceOf = (slices, id) => slices.find((s) => s.id === id)?.body;
const planItemSlices = (slices) => slices.filter((s) => s.id.startsWith("plan-items:"));

test("the epics data has two epics, three deliverables, parented tasks, checklist parents in two streams and one task-under-task link", async () => {
  const fixture = await load({ AH_MOCK_FIXTURE: "", AH_MOCK_EPICS: "1" }, "epics");
  const slices = fixture.buildSlices(Date.now());
  const backlog = sliceOf(slices, `backlog:${fixture.DIR}`).items;
  const byKey = Object.fromEntries(backlog.map((i) => [i.key, i]));
  assert.deepEqual(backlog.filter((i) => i.kind === "epic").map((i) => i.key), ["b1", "b7"]);
  const deliverables = backlog.filter((i) => i.kind === "deliverable");
  assert.equal(deliverables.length, 3);
  assert.equal(deliverables.filter((i) => !i.parent).length, 1);
  assert.ok(backlog.some((i) => !i.kind && byKey[i.parent]?.kind === "deliverable"), "a backlog task under a deliverable");
  assert.ok(backlog.some((i) => !i.kind && i.parent && !byKey[i.parent].kind), "a task under a task");
  assert.ok(!backlog.some((i) => "kind" in i && i.kind === "task"), "task is never written");
  const parentedStreams = planItemSlices(slices).filter((s) => s.body.items.some((i) => i.parent)).map((s) => s.body.stream);
  assert.ok(parentedStreams.length >= 2, `streams with parented items: ${parentedStreams}`);
  const item = planItemSlices(slices).flatMap((s) => s.body.items).find((i) => i.parent);
  assert.deepEqual(Object.keys(item).slice(-2), ["parent", "done_seen_at"]);
});

test("without the epics knob no backlog or plan-items entry has a kind or parent", async () => {
  for (const [env, tag] of [[{ AH_MOCK_FIXTURE: "", AH_MOCK_EPICS: "" }, "no-epics"], [{ AH_MOCK_FIXTURE: "small", AH_MOCK_EPICS: "0" }, "small-no-epics"]]) {
    const fixture = await load(env, tag);
    const slices = fixture.buildSlices(Date.now());
    const entries = [...sliceOf(slices, `backlog:${fixture.DIR}`).items, ...planItemSlices(slices).flatMap((s) => s.body.items)];
    assert.ok(entries.length > 0);
    assert.ok(entries.every((i) => !("kind" in i) && !("parent" in i)), tag);
  }
});

test("a size alone changes only the excerpts", async () => {
  const fixture = await load({ AH_MOCK_FIXTURE: "", AH_MOCK_EXCERPT_BYTES: "1000" }, "sized");
  assert.ok(exchanges(fixture).length < 100);
  assert.equal(fixture.exchangeText("x", true).request_excerpt.length, 1000);
});
