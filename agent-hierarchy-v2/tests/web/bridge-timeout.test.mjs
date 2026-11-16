import { test } from "node:test";
import assert from "node:assert/strict";

// The first load must say what it waits on and give up on a request nobody answers, instead of showing a spinner for ever.

const { connect } = await import("../../web/ui/bridge.js");

const hangs = (_url, init) =>
  new Promise((_resolve, reject) => {
    init.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
  });

function page({ hash = "", fetchImpl }) {
  const store = new Map();
  globalThis.sessionStorage = { getItem: (k) => store.get(k) ?? null, setItem: (k, v) => store.set(k, v) };
  globalThis.location = { hash, pathname: "/ui/", search: "" };
  globalThis.history = { replaceState() {} };
  globalThis.fetch = fetchImpl;
}

const reply = (status, body = {}) => ({ ok: status >= 200 && status < 300, status, headers: { get: () => null }, json: async () => body });

test("a manifest read that is never answered fails naming the view and the wait", { timeout: 3000 }, async () => {
  page({ fetchImpl: hangs });
  const get = await connect({ timeoutMs: 40 });
  await assert.rejects(get.manifest("members"), /view members: no answer after 0\.04 s/);
});

test("a manifest read that answers in time resolves (nearest non-match)", async () => {
  page({ fetchImpl: async () => reply(200, { ok: true }) });
  const get = await connect({ timeoutMs: 40 });
  assert.deepEqual(await get.manifest("members"), { ok: true });
});

test("a manifest read the daemon refuses reports its status, not a missing answer (nearest non-match)", async () => {
  page({ fetchImpl: async () => reply(500) });
  const get = await connect({ timeoutMs: 40 });
  await assert.rejects(get.manifest("members"), /view members: 500/);
});

test("the events long-poll is not cut by the first-load timeout (nearest non-match)", async () => {
  page({
    fetchImpl: () => new Promise((resolve) => setTimeout(() => resolve(reply(200, { events: [] })), 120)),
  });
  const get = await connect({ timeoutMs: 40 });
  assert.deepEqual(await get("/events", { since: 0 }), { events: [] });
});

test("connecting makes no request of its own: the first fetch is the first read", async () => {
  const calls = [];
  page({ fetchImpl: async (url) => (calls.push(url), reply(200, { slices: [] })) });
  const get = await connect();
  assert.equal(calls.length, 0);
  assert.deepEqual(await get.manifest("tree"), { slices: [] });
  assert.deepEqual(calls, ["/ui/v1/view/tree"]);
});
