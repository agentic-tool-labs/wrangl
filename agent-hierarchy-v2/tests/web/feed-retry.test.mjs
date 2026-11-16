import { test } from "node:test";
import assert from "node:assert/strict";
import { mountApp, until, find, text } from "./mount-app.mjs";
import { retryGate } from "../../web/ui/store.js";

const feedReads = (m) => m.log.lists.filter((l) => l.path.startsWith("/list/list:feed:team:"));

test("a team whose feed cannot be read is asked again on the retry gate, not on every snapshot", async () => {
  const m = await mountApp({ view: "teams", failFeeds: true });
  await until(() => feedReads(m).length > 0, "a first feed read");
  await new Promise((r) => setTimeout(r, 1200));
  assert.ok(feedReads(m).length <= 2, `feed reads in 1.2 s: ${feedReads(m).length}`);
});

test("the rail names the team it could not read and the rest of the page still renders", async () => {
  const m = await mountApp({ view: "teams", failFeeds: true });
  await until(() => find(m.root, "p").some((p) => /Couldn't load recent messages/.test(text(p))), "the inline notice");
  assert.ok(find(m.root, "tr").filter((r) => r.attrs.has("data-row")).length > 0, "team rows are drawn");
});

test("a team whose feed reads fine shows no notice", async () => {
  const m = await mountApp({ view: "teams" });
  await until(() => feedReads(m).length > 0, "a first feed read");
  await new Promise((r) => setTimeout(r, 300));
  assert.equal(find(m.root, "p").filter((p) => /Couldn't load/.test(text(p))).length, 0);
});

test("the retry gate doubles its wait up to the cap and a success clears it", () => {
  let t = 0;
  const g = retryGate({ base: 100, cap: 350, now: () => t });
  assert.ok(g.ready("a"));
  g.fail("a");
  assert.ok(!g.ready("a") && g.ready("b"));
  t = 100;
  assert.ok(g.ready("a"));
  g.fail("a");
  t = 299;
  assert.ok(!g.ready("a"));
  t = 300;
  assert.ok(g.ready("a"));
  g.fail("a");
  g.fail("a");
  t = 300 + 349;
  assert.ok(!g.ready("a"));
  t = 300 + 350;
  assert.ok(g.ready("a"));
  g.fail("a");
  g.ok("a");
  assert.ok(g.ready("a"));
});
