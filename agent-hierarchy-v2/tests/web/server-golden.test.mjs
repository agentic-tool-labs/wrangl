import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const { allowedKeys } = await import("../../web/mock/list-keys.mjs");
const { ENVELOPE_KEYS } = await import("../../web/mock/envelope.mjs");
const { createMock, MAX_LIST } = await import("../../web/mock/model.mjs");
const { sparksOf } = await import("../../web/ui/select.js");
const { urlOf } = await import("../../web/ui/bridge.js");
const { createMockServer } = await import("../../web/mock/serve.mjs");

// What the server's own tests write: the /list envelope and the open-exchange list, read from the native goldens.
const GOLDENS = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "native", "tests", "Ah.Watch.Tests", "Goldens");
const golden = (name) => JSON.parse(readFileSync(join(GOLDENS, name), "utf8"));
const envelope = golden("list-envelope.json");

// ---- the /list envelope and key table

test("the mock's envelope keys, shared query keys and limit are the server's", () => {
  assert.deepEqual(ENVELOPE_KEYS, envelope.envelope_keys);
  assert.deepEqual([...allowedKeys("list:no-such-kind:x")].sort(), [...envelope.shared_keys].sort());
  assert.equal(MAX_LIST, envelope.limit.max);
});

test("each kind the server lists takes its shared keys plus exactly its filters, under every prefix the server names", () => {
  assert.ok(Object.keys(envelope.kinds).length >= 6);
  for (const [kind, spec] of Object.entries(envelope.kinds)) {
    for (const prefix of spec.prefixes) {
      assert.deepEqual([...allowedKeys(`${prefix}x/y`)].sort(), [...envelope.shared_keys, ...spec.filters].sort(), `${kind}: ${prefix}`);
    }
  }
});

test("a mock page of each kind has the server's envelope keys, with the timeline's seed beside them", () => {
  const model = createMock();
  const ids = {
    exchanges: "list:exchanges:team:a1b2c3d4/ship",
    feed: "list:feed:team:a1b2c3d4/ship",
    open: "list:open:team:a1b2c3d4/ship",
    timeline: "list:timeline:a1b2c3d4",
    "timeline-spans": "list:timeline-spans:a1b2c3d4/ship",
    "plan-changes": "list:plan-changes:a1b2c3d4/ship",
  };
  for (const [kind, id] of Object.entries(ids)) {
    const page = model.list(id, { limit: 5 });
    assert.ok(page, `${kind} is served`);
    const fixed = (k) => !["seed", "archived", "from_ms", "to_ms", "more"].includes(k);
    assert.deepEqual(Object.keys(page).filter(fixed).sort(), [...envelope.envelope_keys].sort(), kind);
    assert.deepEqual(Object.keys(envelope.kinds[kind].sample).filter(fixed).sort(), [...envelope.envelope_keys].sort(), `${kind} golden sample`);
  }
});

test("an exchange list row is {id, body} in the server's sample and in the mock's page", () => {
  const model = createMock();
  const sample = envelope.kinds.exchanges.sample.rows[0];
  assert.deepEqual(Object.keys(sample).sort(), ["body", "id"]);
  assert.deepEqual(Object.keys(model.list("list:exchanges:team:a1b2c3d4/ship", { limit: 1 }).rows[0]).sort(), ["body", "id"]);
});

// ---- the plan-changes slice

test("the plan-changes slice body is {rows, total, tail_cursor} in the server's golden and in the mock, with no team or changes key", async () => {
  const lane = golden("plan-slices.json").slices.find((s) => s.id.startsWith("list:plan-changes:")).body;
  assert.deepEqual(Object.keys(lane).sort(), ["rows", "tail_cursor", "total"]);
  assert.ok(lane.total >= lane.rows.length);
  assert.equal(typeof lane.tail_cursor, "string");
  const model = createMock();
  const id = "list:plan-changes:a1b2c3d4/ship";
  const reply = await model.poll({ since: model.seq, epoch: model.epoch, sub: [id], wait: 0 });
  const body = reply.slices.find((s) => s.id === id)?.body;
  assert.ok(body, "the lane is published when a client names it");
  assert.deepEqual(Object.keys(body).sort(), Object.keys(lane).sort());
  assert.equal(Array.isArray(body.rows), true);
});

// ---- the open-exchange list the graph's sparks read

test("sparksOf over the server's open-exchange list draws one spark, for the exchange whose two ends are resolved", () => {
  const slices = golden("tree-open-exchanges.json").slices;
  const map = new Map(Object.entries(slices).map(([id, body]) => [id, { id, body }]));
  const team = { body: { dir_id: "f28aaefa", name: "default" } };
  assert.deepEqual(sparksOf(map, team), [
    { id: "exchange:f28aaefa/a", from: "member:f28aaefa/default/@orchestrator", to: "member:f28aaefa/default/dev", slug: "exchange:f28aaefa/a" },
  ]);
  assert.deepEqual(sparksOf(map, { body: { dir_id: "f28aaefa", name: "other" } }), [], "another team's open list is not this team's");
});

// ---- the routes the daemon serves

test("lists, actions and the read model go to their own routes; every other path is a view read", () => {
  assert.equal(urlOf("/actions"), "/ui/v1/actions");
  assert.equal(urlOf("/actions/member:d1/ship/dev"), "/ui/v1/actions/member:d1/ship/dev");
  assert.equal(urlOf("/list/list:exchanges:team:d1/ship", { member: "member:d1/ship/dev", limit: 50 }), "/ui/v1/list/list:exchanges:team:d1/ship?member=member%3Ad1%2Fship%2Fdev&limit=50");
  assert.equal(urlOf("/actions/member:d1/ship/@orchestrator"), "/ui/v1/actions/member:d1/ship/@orchestrator", "the daemon's router refuses a raw %40");
  assert.equal(urlOf("/list/list:exchanges:team:d1/ship", { member: "member:d1/ship/@orchestrator" }), "/ui/v1/list/list:exchanges:team:d1/ship?member=member%3Ad1%2Fship%2F%40orchestrator", "query values stay fully encoded");
  assert.equal(urlOf("/read-model"), "/ui/v1/read-model");
  assert.equal(urlOf("/events", { since: 3 }), "/ui/v1/view?path=%2Fevents&since=3");
  assert.equal(urlOf("/listing"), "/ui/v1/view?path=%2Flisting", "a path that only starts with the word list is a view read");
  assert.equal(urlOf("/actionsx"), "/ui/v1/view?path=%2Factionsx");
});

test("the mock serves the daemon's own routes for actions, lists and the read model", async () => {
  const mock = createMockServer();
  const port = await mock.listen(0);
  try {
    const call = (route) => fetch(`http://127.0.0.1:${port}${route}`);
    const root = await call("/ui/v1/actions");
    assert.equal(root.status, 200);
    assert.ok(Array.isArray((await root.json()).actions));
    const list = await call("/ui/v1/list/list:exchanges:team:a1b2c3d4/ship?limit=2");
    assert.equal(list.status, 200);
    assert.equal((await list.json()).rows.length, 2);
    assert.equal((await call("/ui/v1/read-model")).status, 200);
    assert.equal((await call("/ui/v1/actions/not-a-member")).status, 400);
    assert.equal((await call("/ui/v1/list/list:exchanges:team:a1b2c3d4/ship?color=red")).status, 400);
  } finally {
    await mock.close();
  }
});
