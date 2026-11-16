import { test } from "node:test";
import assert from "node:assert/strict";

const { createMockServer } = await import("../../web/mock/serve.mjs");
const { DIR, member } = await import("./mount-app.mjs");

// The mock's list route answers like the daemon's: a filter it does not know is a 400 naming the key, and a member filter
// takes the full member slice id.

async function withServer(fn) {
  const mock = createMockServer();
  const port = await mock.listen(0);
  try {
    await fn((query) => fetch(`http://127.0.0.1:${port}/ui/v1/view?${query}`));
  } finally {
    await mock.close();
  }
}

const EXCHANGES = `list:exchanges:team:${DIR}/ship`;
const listQuery = (extra = "") => `path=${encodeURIComponent(`/list/${EXCHANGES}`)}${extra}`;

test("a list request with an unknown query key is a 400 bad_filter naming that key; the known keys are accepted", async () => {
  await withServer(async (get) => {
    const bad = await get(listQuery("&colour=red"));
    assert.equal(bad.status, 400);
    assert.deepEqual(await bad.json(), { error: "bad_filter", key: "colour" });
    for (const ok of ["", "&limit=5", `&member=${encodeURIComponent(member("architect"))}`]) {
      assert.equal((await get(listQuery(ok))).status, 200, ok);
    }
  });
});

test("the actions route serves a member's, the orchestrator's and the root list; a bare name is 400 bad_member and an unknown member 404 no_node", async () => {
  await withServer(async (get) => {
    const at = (id) => get(`path=${encodeURIComponent(id === null ? "/actions" : `/actions/${id}`)}`);
    const mine = await (await at(member("architect"))).json();
    assert.equal(mine.node.kind, "member");
    assert.ok(mine.actions.some((a) => a.id === "member.detail" && a.invoke.get === `/detail${mine.node.path}`), "its detail route names its own node");
    assert.ok(mine.actions.every((a) => a.id && a.label && a.invoke));
    const orch = await (await at(member("@orchestrator"))).json();
    assert.equal(orch.node.kind, "orchestrator");
    assert.ok(orch.actions.some((a) => a.invoke.view === "switch.plan"));
    const root = await at(null);
    assert.equal(root.status, 200);
    assert.equal((await root.json()).node.kind, "root");
    const bare = await at("architect");
    assert.equal(bare.status, 400);
    assert.deepEqual(await bare.json(), { error: "bad_member" });
    const unknown = await at(member("nobody"));
    assert.equal(unknown.status, 404);
    assert.deepEqual(await unknown.json(), { error: "no_node" });
  });
});

test("asking for a page both after and before a cursor is a 400 bad_cursor; either alone is served", async () => {
  await withServer(async (get) => {
    const both = await get(listQuery("&after=1&before=2"));
    assert.equal(both.status, 400);
    assert.deepEqual(await both.json(), { error: "bad_cursor" });
    assert.notEqual((await get(listQuery("&after=1"))).status, 400);
    assert.notEqual((await get(listQuery("&before=1"))).status, 400);
  });
});

test("the member filter takes the percent-encoded full member slice id; a bare name is a 400 bad_member", async () => {
  await withServer(async (get) => {
    const all = await (await get(listQuery())).json();
    const mine = await get(listQuery(`&member=${encodeURIComponent(member("architect"))}`));
    assert.equal(mine.status, 200);
    const page = await mine.json();
    assert.ok(page.rows.length > 0 && page.rows.length <= all.rows.length);
    const bare = await get(listQuery("&member=architect"));
    assert.equal(bare.status, 400);
    assert.deepEqual(await bare.json(), { error: "bad_member" });
  });
});

test("the served mock keeps briefs in flight: traffic opens a brief between two members until LIVE_OPEN are open, then answers the oldest", async () => {
  const { createMock, LIVE_OPEN } = await import("../../web/mock/model.mjs");
  const model = createMock();
  const open = () => model.manifest("exchanges").slices.find((s) => s.id === `list:open:team:${DIR}/ship`).body.open;
  const feedHead = () => model.manifest("exchanges").slices.find((s) => s.id === `list:feed:team:${DIR}/ship`).body.ids[0];
  for (let i = 0; i < 10 && open().length > 0; i += 1) {
    model.closeOneBrief();
  }
  assert.equal(open().length, 0, "the fixture's own briefs drain");
  model.traffic();
  const [first] = open();
  assert.equal(first.from_member, `member:${DIR}/ship/@orchestrator`);
  assert.equal(first.to_member, member("implementor-1"));
  assert.equal(`exchange:${DIR}/${feedHead()}`, first.id, "the new brief heads the feed");
  const body = model.manifest("exchanges").slices.find((s) => s.id === first.id).body;
  assert.equal(body.open, true);
  while (open().length < LIVE_OPEN) {
    model.traffic();
  }
  const before = open().map((o) => o.id);
  const head = feedHead();
  model.traffic();
  assert.deepEqual(open().map((o) => o.id), before.slice(0, -1), "at LIVE_OPEN the oldest is answered and no brief is opened");
  assert.equal(feedHead(), head);
  assert.equal(model.manifest("exchanges").slices.find((s) => s.id === first.id).body.open, false);
  model.closeOneBrief();
});

test("with= keeps the exchanges between member and peer in either direction; without member it is a 400 bad_member", async () => {
  await withServer(async (get) => {
    const page = async (extra) => (await (await get(listQuery(extra))).json()).rows.map((r) => [r.body.from_name ?? r.body.from, r.body.to_name ?? r.body.to]);
    const pair = await page(`&member=${encodeURIComponent(member("implementor-1"))}&with=${encodeURIComponent(member("task-runner"))}`);
    assert.ok(pair.length > 0);
    assert.ok(pair.every((p) => p.includes("implementor-1") && p.includes("task-runner")), JSON.stringify(pair));
    const one = await page(`&member=${encodeURIComponent(member("implementor-1"))}`);
    assert.ok(one.some((p) => !p.includes("task-runner")), "the nearest non-match, implementor-1 with someone else, is left out of the pair");
    const reversed = await page(`&member=${encodeURIComponent(member("task-runner"))}&with=${encodeURIComponent(member("implementor-1"))}`);
    assert.deepEqual(reversed, pair, "order-free");
    const orch = await page(`&member=${encodeURIComponent(`member:${DIR}/ship/@orchestrator`)}&with=${encodeURIComponent(member("implementor-1"))}`);
    assert.ok(orch.length > 0 && orch.every((p) => p.includes("implementor-1") && p.includes("orchestrator")));
    const lone = await get(listQuery(`&with=${encodeURIComponent(member("task-runner"))}`));
    assert.equal(lone.status, 400);
    assert.deepEqual(await lone.json(), { error: "bad_member" });
    assert.equal((await get(listQuery(`&member=${encodeURIComponent(member("task-runner"))}&with=task-runner`))).status, 400, "a bare name");
    assert.deepEqual(await page(`&member=${encodeURIComponent(member("task-runner"))}&with=${encodeURIComponent(member("nobody"))}`), [], "an unknown peer is an empty page");
  });
});
