import { test } from "node:test";
import assert from "node:assert/strict";
import { settle } from "./dom.mjs";
import { mountApp, until, member, DIR } from "./mount-app.mjs";

const ORCH = `member:${DIR}/ship/@orchestrator`;
const BODY = "exchange-body:";
const bodiesIn = (sub) => (sub ?? "").split(",").filter((t) => t.startsWith(BODY));
const lastPoll = (m) => m.log.polls.at(-1);
const rows = (m) => m.root.all("li.pair-row");
const open = (m, i) => rows(m)[i].all("button.pair-head")[0].fire("click");

const bodyText = (m) => rows(m)[0].all("div.pair-body")[0]?.textContent ?? "";

async function openPair(m, peer = "reviewer") {
  m.store.setUi({ pair: [member(peer), ORCH] });
  await until(() => rows(m).length > 0, "the pair's rows");
}

test("the exchange slices a client holds carry a preview and byte counts, never the excerpts", async () => {
  const m = await mountApp();
  const exchanges = [...m.store.state.slices.values()].filter((s) => s.id.startsWith("exchange:"));
  assert.ok(exchanges.length > 0);
  for (const { body } of exchanges) {
    assert.equal("request_excerpt" in body, false);
    assert.equal("response_excerpt" in body, false);
    assert.equal(typeof body.preview, "string");
    assert.equal(typeof body.request_bytes, "number");
  }
  assert.equal([...m.store.state.slices.keys()].some((id) => id.startsWith(BODY)), false, "no body is held until one is opened");
});

test("a pair row leases its body when it opens, shows loading until it arrives, and releases it when it closes", async () => {
  const m = await mountApp();
  await openPair(m);
  await settle();
  assert.deepEqual(bodiesIn(lastPoll(m)), [], "collapsed rows lease nothing");

  const id = rows(m)[0].getAttribute("key") ?? null;
  open(m, 0);
  await until(() => bodiesIn(lastPoll(m)).length === 1, "the body is named in the subscription");
  const leased = bodiesIn(lastPoll(m))[0];
  assert.match(leased, /^exchange-body:[^/]+\/[\w-]+$/);
  assert.ok(id === null || leased.endsWith(id.split("/").pop()));
  await until(() => m.store.state.slices.has(leased), "the body slice arrives");
  await until(() => /Work tree for /.test(bodyText(m)), "the excerpt is drawn");
  assert.equal(rows(m)[0].all("div.pair-body")[0].getAttribute("aria-busy"), "false");

  open(m, 0);
  await until(() => bodiesIn(lastPoll(m)).length === 0, "closing the row releases its lease");
  await until(() => !m.store.state.slices.has(leased), "the store drops the released body");
  m.store.setUi({ pair: null });
});

test("a row shows loading while its body has not arrived", async () => {
  const m = await mountApp();
  await openPair(m);
  const lease = m.store.lease.bind(m.store);
  m.store.lease = (owner, ids) => lease(owner, owner === "pair" ? [] : ids);
  open(m, 0);
  await settle();
  const body = rows(m)[0].all("div.pair-body")[0];
  assert.equal(body.getAttribute("aria-busy"), "true");
  assert.match(body.textContent, /Loading/);
  assert.doesNotMatch(body.textContent, /Work tree for /);
  m.store.setUi({ pair: null });
});

test("two open rows lease two bodies; closing the dialog releases both", async () => {
  const m = await mountApp();
  await openPair(m, "implementor-1");
  assert.ok(rows(m).length >= 2, "the pair has two exchanges");
  open(m, 0);
  open(m, 1);
  await until(() => bodiesIn(lastPoll(m)).length === 2, "both bodies are named");
  const [a, b] = bodiesIn(lastPoll(m));
  assert.notEqual(a, b);

  m.store.setUi({ pair: null });
  await until(() => bodiesIn(lastPoll(m)).length === 0, "closing the dialog releases every body");
  await until(() => ![...m.store.state.slices.keys()].some((id) => id.startsWith(BODY)), "no body stays held");
});

test("a body whose exchange is gone says so instead of loading", async () => {
  const m = await mountApp();
  await openPair(m);
  const exchange = rows(m)[0].getAttribute("key") ?? [...m.store.state.slices.keys()].find((id) => id.startsWith("exchange:"));
  const first = rows(m)[0].all("button.pair-head")[0];
  assert.ok(first && exchange);
  const present = [...m.store.state.slices.keys()].filter((id) => id.startsWith("exchange:"));
  m.model.publish([], present);
  await settle();
  open(m, 0);
  await until(() => /no longer available/.test(bodyText(m)), "the gone message");
  assert.doesNotMatch(bodyText(m), /loading/);
  assert.equal(rows(m)[0].all("div.pair-body")[0].getAttribute("aria-busy"), "false");
  m.store.setUi({ pair: null });
});

test("an exchange that is there is never shown as gone", async () => {
  const m = await mountApp();
  await openPair(m);
  open(m, 0);
  await until(() => /Work tree for /.test(bodyText(m)), "the excerpt");
  assert.doesNotMatch(bodyText(m), /no longer available/);
  m.store.setUi({ pair: null });
});

test("an open body whose exchange is archived turns to the gone message", async () => {
  const m = await mountApp();
  await openPair(m);
  open(m, 0);
  await until(() => /Work tree for /.test(bodyText(m)), "the excerpt");
  const present = [...m.store.state.slices.keys()].filter((id) => id.startsWith("exchange:"));
  m.model.publish([], present);
  await until(() => /no longer available/.test(bodyText(m)), "the gone message");
  assert.doesNotMatch(bodyText(m), /Work tree for /);
  m.store.setUi({ pair: null });
});
