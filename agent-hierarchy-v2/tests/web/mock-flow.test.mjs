// The mock's graph-view slices (ah-graph-views §1.2-1.4): flow head and pages, item-msgs, the run anchor.
import { test } from "node:test";
import assert from "node:assert/strict";
import { buildSlices, flowSlices, flowNames, flowEvents, flowId, flowPageId, runIdOf, RUN_MIN, FLOW_PAGE, TEAM_ID, DIR, TEAM } from "../../web/mock/fixture.mjs";
import { createMock, VIEWS } from "../../web/mock/model.mjs";

const NOW = Date.parse("2026-10-31T15:00:00Z");
const START = NOW - RUN_MIN * 60_000;
const byId = (slices) => new Map(slices.map((s) => [s.id, s]));
const bytes = (body) => Buffer.byteLength(JSON.stringify(body));
const synth = (n, parties = 4) => Array.from({ length: n }, (_, i) => [i, i % parties, (i + 1) % parties, i % 2]);

const loadRealistic = async () => {
  const before = process.env.AH_MOCK_FIXTURE;
  process.env.AH_MOCK_FIXTURE = "realistic";
  try {
    return await import("../../web/mock/fixture.mjs?realistic-flow");
  } finally {
    if (before === undefined) {
      delete process.env.AH_MOCK_FIXTURE;
    } else {
      process.env.AH_MOCK_FIXTURE = before;
    }
  }
};

test("the flow head is on the team's open run: the anchor id, stamped in local time, started RUN_MIN ago", () => {
  const s = byId(buildSlices(NOW));
  const head = s.get(flowId()).body;
  assert.equal(head.run, s.get(TEAM_ID).body.pipeline.run);
  assert.match(head.run, /^\d{8}-\d{6}-[a-z0-9]{4}$/);
  const [, y, mo, d, h, mi, se] = head.run.match(/^(\d{4})(\d\d)(\d\d)-(\d\d)(\d\d)(\d\d)/).map(Number);
  assert.equal(new Date(y, mo - 1, d, h, mi, se).getTime(), Math.floor(START / 1000) * 1000);
  assert.equal(head.start_ms, START);
  assert.ok(!("now_ms" in head), "the head carries no clock, as the daemon's does not");
  assert.equal(head.truncated, false);
  assert.equal(head.oldest_ms, START + (head.pages[0] ?? head.tail[0][0]) * 1000);
  assert.equal(head.pages_rev, String(head.pages.length));
});

test("with no events, oldest_ms is null", () => {
  assert.equal(flowSlices({ run: "r", start: START, now: NOW, names: [], events: [] })[0].body.oldest_ms, null);
});

test("the default fixture has ~700 events in full pages of page_size and a short tail, all inside the run", () => {
  const s = byId(buildSlices(NOW));
  const head = s.get(flowId()).body;
  assert.equal(head.page_size, FLOW_PAGE);
  assert.ok(head.pages.length >= 2, `pages: ${head.pages.length}`);
  assert.ok(head.tail.length < FLOW_PAGE);
  const events = [...head.pages.flatMap((_, k) => s.get(flowPageId(head.run, k)).body.events), ...head.tail];
  assert.equal(events.length, 700);
  head.pages.forEach((t, k) => {
    const page = s.get(flowPageId(head.run, k)).body;
    assert.deepEqual([page.run, page.k, page.events.length, page.events[0][0]], [head.run, k, FLOW_PAGE, t]);
  });
  for (let i = 1; i < events.length; i += 1) {
    assert.ok(events[i][0] >= events[i - 1][0], `event ${i} is out of order`);
  }
  assert.ok(events.every(([t, a, b, k]) => t >= 0 && t * 1000 <= NOW - START && a !== b && head.names[a] && head.names[b] && (k === 0 || k === 1)));
  assert.equal(head.totals.reduce((n, r) => n + r[2] + r[3], 0), 700);
  assert.equal(head.names[0], `member:${DIR}/${TEAM}/@orchestrator`);
  assert.ok(head.names.slice(1).every((n) => s.has(n)), "names are member slice ids");
});

test("a page is published only once full: 199 events are all tail, 200 make one page and no tail", () => {
  const names = ["a", "b", "c", "d"];
  const [h199, ...p199] = flowSlices({ run: "r", start: START, now: NOW, names, events: synth(199) });
  assert.deepEqual([h199.body.pages, h199.body.tail.length, p199.length], [[], 199, 0]);
  const [h200, ...p200] = flowSlices({ run: "r", start: START, now: NOW, names, events: synth(200) });
  assert.deepEqual([h200.body.pages, h200.body.tail.length, p200.map((p) => p.id)], [[0], 0, [flowPageId("r", 0)]]);
});

test("with no open run there are no pages: everything is in the head", () => {
  const [head, ...pages] = flowSlices({ run: null, start: START, now: NOW, names: ["a", "b"], events: synth(450, 2) });
  assert.deepEqual([head.body.run, head.body.pages, head.body.tail.length, pages.length], [null, [], 450, 0]);
});

test("names fold past 64 parties to a trailing \"other\"; 64 parties fold nothing", () => {
  const parties = (n) => Array.from({ length: n }, (_, i) => `member:x/t/m${i}`);
  const folded = flowNames(parties(65));
  assert.deepEqual([folded.length, folded.at(-1), folded.at(-2)], [65, "other", "member:x/t/m63"]);
  assert.deepEqual(flowNames(parties(64)), parties(64));
});

test("totals keep 199 pairs and sum the rest into one [-1,-1] row, 200 rows in all; 200 pairs have no overflow row", () => {
  const pairs = (n) => Array.from({ length: n }, (_, i) => [i, i, 1000 + i, 0]);
  const over = flowSlices({ run: "r", start: START, now: NOW, names: [], events: pairs(201) })[0].body.totals;
  assert.equal(over.length, 200);
  assert.deepEqual(over.at(-1), [-1, -1, 2, 0]);
  const exact = flowSlices({ run: "r", start: START, now: NOW, names: [], events: pairs(200) })[0].body.totals;
  assert.equal(exact.length, 200);
  assert.ok(exact.every((r) => r[0] !== -1));
});

test("totals with the same count are ordered by a, then b", () => {
  const events = [[0, 2, 0, 0], [0, 1, 3, 0], [0, 1, 2, 0], [0, 0, 1, 0], [1, 0, 1, 1]];
  const totals = flowSlices({ run: "r", start: START, now: NOW, names: [], events })[0].body.totals;
  assert.deepEqual(totals, [[0, 1, 1, 1], [1, 2, 1, 0], [1, 3, 1, 0], [2, 0, 1, 0]]);
});

test("synthetic events never pair a party with itself, even in a two-party team", () => {
  const events = flowEvents(START, NOW, ["o", "m"]);
  assert.ok(events.every(([, a, b]) => a !== b));
});

test("the realistic fixture has at least two full pages, and every flow and item-msgs body is under the 10240 B gate", async () => {
  const fixture = await loadRealistic();
  const all = fixture.buildSlices(NOW);
  const head = all.find((s) => s.id === fixture.flowId()).body;
  assert.ok(head.pages.length >= 2);
  const gated = all.filter((s) => /^(flow|flow-page|item-msgs):/.test(s.id));
  assert.ok(gated.length > head.pages.length + 1);
  for (const s of gated) {
    assert.ok(bytes(s.body) <= 10240, `${s.id}: ${bytes(s.body)} B`);
  }
});

test("item-msgs counts exchanges by item and by stream slug; no item means by_stream only; a non-stream slug is in neither", () => {
  const s = byId(buildSlices(NOW));
  const body = s.get(`item-msgs:${DIR}/${TEAM}`).body;
  assert.deepEqual(body, { counts: { r6a: 2, r6c: 1 }, by_stream: { "ah/ui-r6": 3, "docs-2.5": 1 }, truncated: false });
  assert.equal(s.get(`exchange:${DIR}/20261018-0131-aa05`).body.item, undefined);
  assert.equal(s.get(`exchange:${DIR}/20261018-0110-aa01`).body.item, "r6a", "aa01 has an item but a non-stream slug, so it is not counted");
  assert.ok(!("tradeoff-mock" in body.by_stream));
  assert.deepEqual(Object.keys(body.by_stream), [...Object.keys(body.by_stream)].sort(), "keys in ordinal order");
  assert.deepEqual(s.get(`item-msgs:${DIR}/${TEAM}/r6a`).body, { item: "r6a", n: 2, ids: ["20261018-0151-aa09", "20261018-0125-aa04"] });
  assert.equal(s.get(`item-msgs:${DIR}/${TEAM}/r6b`), undefined, "an item with no messages has no slice");
  assert.equal(s.get(`item-msgs:${DIR}/${TEAM}/r3`), undefined, "an item whose only exchange has a non-stream slug has no slice");
});

test("an exchange body carries item when it is about one, and no item key when it is not", () => {
  const s = byId(buildSlices(NOW));
  assert.equal(s.get(`exchange:${DIR}/20261018-0110-aa01`).body.item, "r6a");
  assert.ok(!("item" in s.get(`exchange:${DIR}/20261018-0112-aa02`).body));
});

test("flow, flow-page and item-msgs are leased: in no view manifest, served only to a client that names them", async () => {
  const mock = createMock({ now: () => NOW });
  const lazyId = /^(flow|flow-page|item-msgs):/;
  for (const view of Object.keys(VIEWS)) {
    assert.ok(mock.manifest(view).slices.every((s) => !lazyId.test(s.id)), view);
  }
  assert.ok(mock.manifest("tree").slices.some((s) => s.id === TEAM_ID), "the team slice is still in its view");
  const unnamed = await mock.poll({ since: 0, sub: ["view:tree", "view:exchanges"] });
  assert.ok(unnamed.slices.every((s) => !lazyId.test(s.id)));
  const named = await mock.poll({ since: 0, sub: [flowId(), `item-msgs:${DIR}/${TEAM}`] });
  assert.deepEqual(named.slices.map((s) => s.id).sort(), [flowId(), `item-msgs:${DIR}/${TEAM}`].sort());
});

test("traffic appends to the flow tail: a brief re-versions the head only, its answer appends the report, and a filled tail adds a page", async () => {
  let clock = NOW;
  const mock = createMock({ now: () => clock });
  await mock.poll({ since: 0, sub: [flowId(), flowPageId(runIdOf(START), 0)] });
  const head0 = mock.slice(flowId());
  const page0 = mock.slice(flowPageId(head0.body.run, 0));
  const names = head0.body.names;
  clock += 4_000;
  mock.traffic();
  const head1 = mock.slice(flowId());
  assert.ok(head1.version > head0.version);
  assert.equal(head1.body.tail.length, head0.body.tail.length + 1);
  assert.deepEqual(head1.body.tail.at(-1), [Math.floor((clock - START) / 1000), 0, names.indexOf(`member:${DIR}/${TEAM}/implementor-1`), 0]);
  assert.equal(mock.slice(flowPageId(head0.body.run, 0)).version, page0.version, "a full page is never republished");
  clock += 4_000;
  mock.traffic();
  const report = mock.slice(flowId()).body.tail.at(-1);
  assert.equal(report[3], 1);
  assert.equal(mock.slice(flowId()).body.pages_rev, head0.body.pages_rev, "an append that fills no page keeps pages_rev");
  const before = mock.slice(flowId()).body;
  for (let i = before.tail.length; i < FLOW_PAGE; i += 1) {
    clock += 4_000;
    mock.traffic();
  }
  const after = mock.slice(flowId()).body;
  assert.deepEqual([after.pages.length, after.tail.length], [before.pages.length + 1, 0]);
  assert.notEqual(after.pages_rev, before.pages_rev, "a new full page changes pages_rev");
  const fresh = await mock.poll({ since: 0, sub: [flowId(), flowPageId(after.run, before.pages.length)] });
  assert.equal(fresh.slices.find((s) => s.id === flowPageId(after.run, before.pages.length)).body.events.length, FLOW_PAGE);
});

test("a churn rebuild keeps the run id", () => {
  let clock = NOW;
  const mock = createMock({ now: () => clock });
  const run = mock.slice(TEAM_ID).body.pipeline.run;
  clock += 90_000;
  mock.churn();
  assert.equal(mock.slice(TEAM_ID).body.pipeline.run, run);
});
