import { test } from "node:test";
import assert from "node:assert/strict";
import { settle } from "./dom.mjs";
import { mountApp, until, member, DIR } from "./mount-app.mjs";

const { shortName } = await import("../../web/ui/graph.js");

const ORCH = `member:${DIR}/ship/@orchestrator`;
const slugsOf = (root) => root.all("li.pair-row").map((li) => li.all(".slug")[0].textContent);
const pairPages = (m) => m.log.lists.filter((l) => l.params.with !== undefined);

test("shortName drops the member id's prefix and the orchestrator's @", () => {
  assert.equal(shortName(ORCH), "orchestrator");
  assert.equal(shortName(member("reviewer")), "reviewer");
});

test("opening a pair asks for that pair once and lists only their exchanges", async () => {
  const m = await mountApp();
  m.store.setUi({ pair: [ORCH, member("implementor-1")] });
  await until(() => m.root.all("li.pair-row").length > 0, "the pair's rows");
  const asked = pairPages(m);
  assert.equal(asked.length, 1, "one paged request per open");
  assert.equal(asked[0].path, `/list/list:exchanges:team:${DIR}/ship`);
  assert.deepEqual([asked[0].params.member, asked[0].params.with].sort(), [ORCH, member("implementor-1")].sort());
  const slugs = slugsOf(m.root);
  assert.ok(slugs.includes("r6-merge-answer") && slugs.includes("ui-r6a"), slugs.join(", "));
  assert.ok(!slugs.includes("run-web-tests"), "implementor-1 → task-runner is not this pair");
  assert.equal(m.root.all("section.pair")[0].getAttribute("aria-label"), "Messages · orchestrator ↔ implementor-1");
  m.store.setUi({ pair: null });
});

test("a row opens to its brief excerpt; a member click opens no pair", async () => {
  const m = await mountApp();
  m.root.all("g.gm-leaf")[1].fire("click");
  await settle();
  assert.equal(pairPages(m).length, 0);
  m.store.setUi({ pair: [ORCH, member("reviewer")] });
  await until(() => m.root.all("li.pair-row").length > 0, "rows");
  assert.equal(m.root.all("li.pair-row")[0].all("div.pair-body").length, 0, "collapsed by default");
  m.root.all("li.pair-row")[0].all("button.pair-head")[0].fire("click");
  await settle();
  const row = m.root.all("li.pair-row")[0];
  assert.equal(row.all("button.pair-head")[0].getAttribute("aria-expanded"), "true");
  assert.match(row.all("div.pair-body")[0].textContent, /Work tree for /);
  m.store.setUi({ pair: null });
});

test("load older continues from the first page's cursor with the same pair; a page that is not full offers none", async () => {
  const m = await mountApp();
  const { PAIR_PAGE } = await import("../../web/ui/app.js");
  m.store.setUi({ pair: [member("reviewer"), ORCH] });
  await until(() => m.root.all("li.pair-row").length > 0, "rows");
  assert.equal(m.root.all("button.pair-older").length, 0, "two exchanges fit one page");
  m.store.setUi({ pair: null });
  await settle();
  for (let i = 0; i < 400; i += 1) {
    m.model.traffic();
  }
  m.store.setUi({ pair: [ORCH, member("implementor-1")] });
  await until(() => m.root.all("li.pair-row").length === PAIR_PAGE, "a full page");
  m.root.all("button.pair-older")[0].fire("click");
  await until(() => m.root.all("li.pair-row").length > PAIR_PAGE, "the second page");
  const second = pairPages(m).find((l) => l.params.member === ORCH && l.params.after);
  assert.ok(second.params.after, "after the first page's cursor");
  assert.equal(second.params.with, member("implementor-1"));
  assert.ok(slugsOf(m.root).length > PAIR_PAGE);
  m.store.setUi({ pair: null });
});

test("empty pages that still have a cursor are read through before the sheet says empty; an empty last page says no messages", async () => {
  const m = await mountApp();
  const real = m.store.fetchList;
  let calls = 0;
  m.store.fetchList = async (id, params) => {
    if (params.with === undefined) return real(id, params);
    calls += 1;
    return calls < 3 ? { rows: [], next: `c${calls}` } : real(id, { member: params.member, with: params.with, limit: params.limit });
  };
  m.store.setUi({ pair: [ORCH, member("implementor-1")] });
  await until(() => slugsOf(m.root).length > 0, "rows after two empty pages");
  assert.equal(calls, 3);
  assert.equal(m.root.all("p.dim").filter((p) => /No messages/.test(p.textContent)).length, 0);
  m.store.setUi({ pair: null });
  await settle();
  calls = 0;
  m.store.fetchList = async () => ((calls += 1), { rows: [], next: calls < 10 ? `c${calls}` : null });
  m.store.setUi({ pair: [ORCH, member("reviewer")] });
  await until(() => m.root.all("button.pair-older").length === 1, "the read-through stops and offers load older");
  const { PAIR_EMPTY_PAGES } = await import("../../web/ui/app.js");
  assert.equal(calls, PAIR_EMPTY_PAGES, "bounded");
  assert.ok(m.root.all("p.dim").some((p) => /None in the newest/.test(p.textContent)), "not 'no messages' while a cursor remains");
  m.store.setUi({ pair: null });
  await settle();
  m.store.fetchList = async () => ({ rows: [], next: null });
  m.store.setUi({ pair: [ORCH, member("docs-writer")] });
  await until(() => m.root.all("p.dim").some((p) => p.textContent === "No messages between them."), "empty and done");
  assert.equal(m.root.all("button.pair-older").length, 0);
  m.store.setUi({ pair: null });
});

test("an open sheet is live: a new exchange between the pair appears with one page read, another pair's does not; a response updates its row", async () => {
  const m = await mountApp();
  m.store.setUi({ pair: [ORCH, member("implementor-1")] });
  await until(() => slugsOf(m.root).length > 0, "rows");
  const asked = pairPages(m).length;
  m.model.traffic();
  await until(() => slugsOf(m.root)[0] === "ui-graph-comms", "orchestrator → implementor-1 arrives at the top");
  m.model.traffic();
  await settle();
  await settle();
  assert.ok(!slugsOf(m.root).includes("run-web-suite"), "implementor-1 → task-runner is another pair");
  assert.ok(pairPages(m).length >= asked + 1, "the feed moving read the newest page again");
  const id = [...m.store.state.slices.keys()].find((k) => m.store.state.slices.get(k).body?.slug === "ui-graph-comms");
  const cur = m.store.state.slices.get(id);
  m.model.publish([{ id, as_of: Date.now(), body: { ...cur.body, open: false, has_response: true, response_excerpt: "ui-graph-comms: done." } }]);
  await until(() => m.root.all("li.pair-row")[0].all(".kind")[0].textContent === "done", "the row turns done");
  m.store.setUi({ pair: null });
});

test("a live exchange joins the sheet by member identity: same names in another team or directory do not", async () => {
  const { partyIdOf } = await import("../../web/ui/select.js");
  const body = { dir_id: DIR, team: "ship", from: "orchestrator", from_name: null, to: "implementor", to_name: "implementor-1" };
  assert.equal(partyIdOf(body, "from"), ORCH);
  assert.equal(partyIdOf(body, "to"), member("implementor-1"));
  assert.equal(partyIdOf({ ...body, team: null }, "to"), `member:${DIR}/default/implementor-1`);
  assert.equal(partyIdOf({ ...body, to_name: null, to: "reviewer" }, "to"), null, "an unnamed non-orchestrator resolves to nobody");
  const m = await mountApp();
  m.store.setUi({ pair: [ORCH, member("implementor-1")] });
  await until(() => slugsOf(m.root).length > 0, "rows");
  const feed = `list:feed:team:${DIR}/ship`;
  const add = (x, extra) => {
    const ids = m.model.list(feed, { limit: 200 }).rows.map((r) => r.id.split("/").pop());
    m.model.publish([
      { id: `exchange:${DIR}/${x}`, as_of: Date.now(), body: { dir_id: DIR, id: x, slug: x, from: "orchestrator", from_name: null, to: "implementor", to_name: "implementor-1", team: "ship", created: new Date().toISOString(), open: true, has_request: true, has_response: false, request_excerpt: "r", response_excerpt: null, excerpt_truncated: false, ...extra } },
      { id: feed, as_of: Date.now(), body: { team: `team:${DIR}/ship`, ids: [x, ...ids] } },
      { id: `list:exchanges:team:${DIR}/ship`, as_of: Date.now(), body: { team: `team:${DIR}/ship`, ids: [x, ...ids] } },
    ]);
  };
  add("20261019-000001-xteam", { team: "other" });
  await settle();
  await settle();
  assert.ok(!slugsOf(m.root).includes("20261019-000001-xteam"), "another team's implementor-1");
  add("20261019-000002-same", {});
  await until(() => slugsOf(m.root)[0] === "20261019-000002-same", "this pair's");
  m.store.setUi({ pair: null });
});
