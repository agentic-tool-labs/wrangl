import { test } from "node:test";
import assert from "node:assert/strict";
import { settle } from "./dom.mjs";
import { mountApp as mount, until, find, text, member } from "./mount-app.mjs";

const { resolveAnchor } = await import("../../web/ui/transcript-window.js");

const rows = (...keys) => keys.map((key) => ({ key }));
const offs = (n) => Array.from({ length: n + 1 }, (_, i) => i * 10);

test("resolveAnchor: the anchor row survives, so it keeps its place and delta", () => {
  const all = rows("a", "b", "c", "d");
  const kept = rows("b", "d");
  assert.deepEqual(resolveAnchor(all, kept, offs(2), { key: "d", delta: 4 }), { top: 14, anchor: { key: "d", delta: 4 } });
});

test("resolveAnchor: a removed anchor falls to the nearest earlier surviving row", () => {
  const all = rows("a", "b", "c", "d");
  const kept = rows("a", "b", "d");
  assert.deepEqual(resolveAnchor(all, kept, offs(3), { key: "c", delta: 4 }), { top: 10, anchor: { key: "b", delta: 0 } });
});

test("resolveAnchor: nothing earlier survives, so it jumps to live (a later survivor does not count)", () => {
  const all = rows("a", "b", "c");
  assert.equal(resolveAnchor(all, rows("c"), offs(1), { key: "b", delta: 0 }), "live");
  assert.equal(resolveAnchor(all, rows(), offs(0), { key: "a", delta: 0 }), "live");
});

test("resolveAnchor: no anchor means no answer", () => {
  assert.equal(resolveAnchor(rows("a"), rows("a"), offs(1), null), null);
});

test("the Follow checkbox mirrors the follow state; unticking pauses, ticking resumes", async () => {
  const m = await mount({ view: null });
  m.store.setUi({ view: "conversation", selected: member("architect") });
  await until(() => find(m.root, "li.tr-row").length > 0, "rows");
  await settle();
  const box = () => find(find(m.root, ".cv-follow")[0], "input")[0];
  const ticked = () => (box().checked !== undefined ? box().checked === true : box().getAttribute("checked") !== null);
  assert.equal(ticked(), true);
  assert.equal(find(m.root, ".jump").length, 0);
  box().fire("change", { currentTarget: { checked: false } });
  await until(() => find(m.root, ".jump").length === 1, "paused by the checkbox");
  assert.equal(ticked(), false);
  box().fire("change", { currentTarget: { checked: true } });
  await until(() => find(m.root, ".jump").length === 0, "following again");
});

test("only the Errors segment carries a count, and only when there are errors", async () => {
  const m = await mount({ view: null });
  m.store.setUi({ view: "conversation", selected: member("architect") });
  await until(() => find(m.root, "li.tr-row").length > 0, "rows");
  const buttons = find(find(m.root, ".cv-bar")[0], "button");
  const by = (label) => buttons.find((b) => text(b).startsWith(label));
  assert.equal(find(by("Errors"), ".err-count").length, 1, "Errors carries the count");
  assert.ok(Number(text(find(by("Errors"), ".err-count")[0])) > 0);
  assert.equal(find(by("All"), ".err-count").length, 0);
  assert.equal(find(by("Text"), ".err-count").length, 0);
});

test("the pinned Jump to live sits outside the scroll container", async () => {
  const m = await mount({ view: null });
  m.store.setUi({ view: "conversation", selected: member("architect") });
  await until(() => find(m.root, "li.tr-row").length > 0, "rows");
  find(m.root, ".tr-scroll")[0].fire("scroll", { currentTarget: { scrollTop: 100 } });
  await until(() => find(m.root, ".jump").length === 1, "paused");
  assert.equal(find(find(m.root, ".tr-scroll")[0], ".jump").length, 0);
  assert.match(text(find(m.root, ".jump")[0]), /^↓ Jump to live/);
});

test("the Details rail groups Item, Round, Brief age, Model, Context used (L5), Session, then Files changed, Tests and Usage", async () => {
  const m = await mount({ view: null });
  m.store.setUi({ view: "conversation", selected: member("architect") });
  await until(() => find(m.root, "aside.ctx").length > 0, "the rail");
  const rail = find(m.root, "aside.ctx")[0];
  assert.deepEqual(find(rail, "dt").map(text), ["Item", "Round", "Brief age", "Model", "Context used", "Session", "This turn", "Per turn", "Turn time"]);
  assert.deepEqual(find(rail, "h3").map((h) => text(h).split(" ")[0]), ["Files", "Tests", "Usage"]);
  const dd = (label) => text(find(rail, "dd")[find(rail, "dt").findIndex((d) => text(d) === label)]);
  assert.equal(dd("Round"), "—", "a member whose item is not in the pipeline has no round");
});

test("Round reads 'N of cap' only when the pipeline lists the member's item", async () => {
  const m = await mount({ view: null });
  m.store.setUi({ view: "conversation", selected: member("architect") });
  await until(() => find(m.root, "aside.ctx").length > 0, "the rail");
  const team = m.store.state.slices.get("team:a1b2c3d4/ship");
  const slug = text(find(find(m.root, "aside.ctx")[0], "dd")[0]);
  m.model.publish([{ id: team.id, as_of: Date.now(), body: { ...team.body, pipeline: { ...team.body.pipeline, items: [{ slug, rounds: 2, round_cap: 3 }] } } }]);
  await until(() => text(find(find(m.root, "aside.ctx")[0], "dd")[1]) === "2 of 3", "the round");
});
