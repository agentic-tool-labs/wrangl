import { test } from "node:test";
import assert from "node:assert/strict";
import { contextReading, windowOf } from "../../web/mock/model.mjs";
import { settle } from "./dom.mjs";
import { mountApp as mount, until, find, text } from "./mount-app.mjs";

// What the mock serves mirrors ContextReading; these readings are the ones the page shows.
const contextOf = (model, used) => {
  const { pct: _pct, ...shown } = contextReading(model, used);
  return shown;
};

test("windowOf: a 1m marker is 1M, a Claude id or alias is 200k, anything else is unknown", () => {
  for (const id of ["claude-sonnet-4-5[1m]", "Claude-Opus-4-1M", "claude-x-1m-20260101"]) assert.equal(windowOf(id), 1_000_000, id);
  for (const id of ["claude-opus-4-1-20250805", "claude-haiku-4-5", "opus", "sonnet-4-5", "haiku", "fable"]) assert.equal(windowOf(id), 200_000, id);
  for (const id of ["gpt-5", "gemini-1.5-pro", "opusx", "claude", "11m", "", null, undefined]) assert.equal(windowOf(id), null, String(id));
});

test("contextOf floors the percentage and shows <1% only above zero", () => {
  const m = "claude-opus-4-5";
  assert.equal(contextOf(m, 0).text, "0%");
  assert.equal(contextOf(m, 1).text, "<1%");
  assert.equal(contextOf(m, 1999).text, "<1%");
  assert.equal(contextOf(m, 2000).text, "1%");
  assert.equal(contextOf(m, 199_999).text, "99%");
  assert.equal(contextOf(m, 200_000).text, "100%");
  assert.equal(contextOf(m, 200_000).over, false);
});

test("contextOf past the window reads 100%+ in attention and says why", () => {
  const c = contextOf("claude-opus-4-5", 1_000_001);
  assert.equal(c.text, "100%+");
  assert.equal(c.over, true);
  assert.match(c.title, /1,000,001 of 1,000,000 tokens · claude-opus-4-5 · window inferred\. Above the assumed window/);
  assert.equal(contextOf("claude-opus-4-5", 1_000_000).over, false);
  assert.equal(contextOf("gpt-5", 5_000_000).over, false, "an unknown model never reads over");
});

test("a 200k Claude window that used has outgrown is read as 1M, and says so", () => {
  assert.equal(contextOf("claude-opus-4-5", 200_000).text, "100%", "at 200k exactly it is still the 200k window");
  assert.equal(contextOf("claude-opus-4-5", 200_000).title.includes("inferred"), false);
  const c = contextOf("claude-opus-4-5", 412_000);
  assert.equal(c.text, "41%");
  assert.match(c.title, /412,000 of 1,000,000 tokens · claude-opus-4-5 · window inferred/);
  assert.equal(contextOf("claude-sonnet-4-5[1m]", 412_000).title.includes("inferred"), false, "a 1M id is not inferred");
});

test("the invariant readings: 55k on a Claude model is 27%, 412k is 41% inferred, no model shows tokens", () => {
  assert.equal(contextOf("claude-opus-4-5", 55_000).text, "27%");
  assert.equal(contextOf("claude-opus-4-5", 412_000).text, "41%");
  assert.deepEqual(contextOf(null, 55_000), { text: "55k", title: "55,000 tokens · window unknown", over: false });
});

test("the member's model wins, then the activity record's, then tokens", async () => {
  const { memberOf, firstTeam } = await import("../../web/ui/select.js");
  const m = await mount();
  await until(() => m.store.state.slices.size > 0, "slices");
  const slices = m.store.state.slices;
  const team = firstTeam(slices, {});
  const orch = memberOf(slices, team, team.body.orchestrator);
  assert.equal(orch.model, "claude-opus-4-5", "no member model: the activity record's");
  const withMember = new Map(slices);
  const row = slices.get(team.body.orchestrator);
  withMember.set(row.id, { ...row, body: { ...row.body, model: "claude-haiku-4-5" } });
  assert.equal(memberOf(withMember, team, row.id).model, "claude-haiku-4-5", "the member's own model first");
  const act = slices.get(row.body.activity);
  const none = new Map(slices);
  none.set(act.id, { ...act, body: { ...act.body, context: { ...act.body.context, model: undefined } } });
  assert.equal(memberOf(none, team, row.id).model, null);
  assert.equal(contextOf(memberOf(none, team, row.id).model, orch.contextUsed).text, "55k");
});

test("contextOf with an unknown model shows tokens, with no reading shows a dash", () => {
  assert.deepEqual(contextOf("gpt-5", 36_200), { text: "36k", title: "36,200 tokens · window unknown", over: false });
  assert.equal(contextOf(null, 900).text, "900");
  assert.equal(contextOf("claude-opus-4-5", null).text, "—");
  assert.equal(contextOf("claude-opus-4-5", NaN).text, "—");
  assert.equal(contextOf("claude-sonnet-4-5[1m]", 412_345).title, "412,345 of 1,000,000 tokens · claude-sonnet-4-5[1m]");
});

test("the tree, rail and graph show the context of the mock members", async () => {
  const m = await mount();
  m.store.setUi({ view: "overview" });
  await until(() => find(m.root, "span.ctx").length > 0, "member context");
  const tree = find(m.root, "span.ctx").map(text);
  assert.ok(tree.includes("41%"), `implementor-1 412,345 of 1M is 41%: ${tree}`);
  assert.ok(tree.includes("36k"), "an unknown model shows tokens");
  assert.ok(tree.includes("<1%"), "the reviewer is under 1%");
  m.store.setUi({ view: "graph" });
  await until(() => find(m.root, "g.gm-leaf").length > 0, "graph leaves");
  await settle();
  const leaf = find(m.root, "g.gm-leaf").find((g) => g.getAttribute("aria-label").includes(" implementor-1,"));
  assert.match(leaf.getAttribute("aria-label"), /, context 41%$/, "the leaf says 41% in its label");
  assert.ok(find(leaf, "text.t1").map(text).includes("41%"), "the leaf's centre reads 41%");
});

test("recentCount counts the times inside the last hour at the moment it is asked, and a full list reads 100+", async () => {
  const { recentCount } = await import("../../web/ui/select.js");
  const now = 10_000_000_000;
  assert.equal(recentCount([], now), "0");
  assert.equal(recentCount(undefined, now), "0");
  assert.equal(recentCount([now, now - 3_600_000, now - 3_600_001], now), "2", "exactly an hour old counts, a millisecond older does not");
  assert.equal(recentCount([now - 3_599_000], now + 2000), "0", "the same list is older when asked later");
  const hundred = Array.from({ length: 100 }, (_, i) => now - i);
  assert.equal(recentCount(hundred, now), "100+");
  assert.equal(recentCount(hundred.slice(1), now), "99", "99 entries are a count, not 100+");
  assert.equal(recentCount(hundred, now + 3_600_000 - 49), "50", "100 entries of which 50 aged out read as a count");
});
