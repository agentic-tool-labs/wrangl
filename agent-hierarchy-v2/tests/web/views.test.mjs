import { test } from "node:test";
import assert from "node:assert/strict";
import { countRenders, settle } from "./dom.mjs";
import { mountApp as mount, until, find, text, member, DIR } from "./mount-app.mjs";

// C1 and C2 for the Tree and Timeline views, on the whole page.

const MIN = 60_000;

async function open(view) {
  const m = await mount();
  m.store.setUi({ view });
  await until(() => find(m.root, view === "overview" ? "li.ov-member" : "li.lane").length > 0, `the ${view}`);
  await settle();
  return m;
}

async function drawn(m, change) {
  const counter = countRenders();
  const seq = m.store.state.seq;
  change();
  await until(() => m.store.state.seq > seq, "the change");
  await settle();
  const out = Object.fromEntries([...counter.counts].sort());
  counter.stop();
  return out;
}

const statusSlice = (m) => [...m.store.state.slices.values()].find((s) => s.id.startsWith("processes:"));

test("C1 Overview: an activity change draws the Overview once and no other view", async () => {
  const m = await open("overview");
  const { S, Age, ...rest } = await drawn(m, () => m.model.tick());
  assert.deepEqual(rest, { Footer: 1, OverviewView: 1 }, "the Overview draws once, the status bar recounts the states, and no other view component does (S is Preact's Fragment, Age the member ages)");
});

test("C1 Tree: a slice no tree row reads draws nothing", async () => {
  const m = await open("overview");
  const p = statusSlice(m);
  const stranger = { pid: 999_999, start: "never" };
  assert.deepEqual(await drawn(m, () => m.model.publish([{ id: p.id, as_of: Date.now(), body: { pids: [...p.body.pids, stranger] } }])), {}, "a process no member owns changes no row");
});

test("C1 Timeline: an activity change of members that have timeline rows draws nothing", async () => {
  const m = await open("timeline");
  assert.deepEqual(await drawn(m, () => m.model.tick()), { Footer: 1 }, "only the status bar's state counts");
});

test("Timeline: a quiet lane (no rows in a range that includes now) follows its member's activity when a transition is recorded", async () => {
  const m = await open("timeline");
  const id = member("quiet-one");
  const team = m.store.state.slices.get(`team:${DIR}/ship`);
  const actId = `activity:${DIR}/sess-quiet`;
  const activity = (state) => ({ id: actId, as_of: Date.now(), body: { dir_id: DIR, subject: "sess-quiet", activity: state, tool: null, tool_at: null, turn_at: null, context: null } });
  m.model.publish([
    activity("idle"),
    { id, as_of: Date.now(), body: { id, name: "quiet-one", role: "implementor", session: "sess-quiet", activity: actId, liveness: {}, placement: { stream: null }, last_exchanges: [] } },
    { id: team.id, as_of: Date.now(), body: { ...team.body, members: [id, ...team.body.members] } },
  ]);
  await until(() => find(m.root, "li.lane").some((li) => text(li).includes("quiet-one")), "the quiet lane");
  await settle();
  const quiet = () => find(m.root, "li.lane").find((li) => text(li).includes("quiet-one"));
  const timeline = m.store.state.slices.get(`timeline:${DIR}`);
  m.model.publish([activity("working"), { id: timeline.id, as_of: Date.now(), body: { ...timeline.body, total: timeline.body.total + 1 } }]);
  await until(() => quiet().all("rect.span").some((r) => r.attrs.get("class").includes("st-working")), "the lane to read again");
});

test("C2 Tree: a row keeps its element when a member row is added above it", async () => {
  const m = await open("overview");
  const rowOf = (label) => find(m.root, "li.ov-member").find((li) => text(li).includes(label));
  const keep = ["implementor-1", "reviewer", "docs-writer"].map((n) => [n, rowOf(n)]);
  assert.ok(keep.every(([, li]) => li));
  const team = m.store.state.slices.get(`team:${DIR}/ship`);
  const id = member("aaa-new");
  m.model.publish([
    { id, as_of: Date.now(), body: { id, name: "aaa-new", role: "implementor", session: "sess-new", activity: null, liveness: {}, placement: { stream: null }, last_exchanges: [] } },
    { id: team.id, as_of: Date.now(), body: { ...team.body, members: [id, ...team.body.members] } },
  ]);
  await until(() => rowOf("aaa-new"), "the new row");
  for (const [n, li] of keep) {
    assert.equal(rowOf(n), li, `${n} is the same element`);
  }
});

test("C2 Timeline: a lane keeps its element when a member lane is added above it", async () => {
  const m = await open("timeline");
  const laneOf = (label) => find(m.root, "li.lane").find((li) => text(li).includes(label));
  const keep = ["architect", "reviewer"].map((n) => [n, laneOf(n)]);
  assert.ok(keep.every(([, li]) => li));
  const team = m.store.state.slices.get(`team:${DIR}/ship`);
  const id = member("aaa-new");
  m.model.publish([
    { id, as_of: Date.now(), body: { id, name: "aaa-new", role: "implementor", session: "sess-new", activity: null, liveness: {}, placement: { stream: null }, last_exchanges: [] } },
    { id: team.id, as_of: Date.now(), body: { ...team.body, members: [id, ...team.body.members] } },
  ]);
  await until(() => laneOf("aaa-new"), "the new lane");
  for (const [n, li] of keep) {
    assert.equal(laneOf(n), li, `${n} is the same element`);
  }
});

test("Timeline: Custom puts the bounds on screen; a custom range beyond retention shows archived and loads no rows; an inside range does not", async () => {
  const m = await open("timeline");
  find(m.root, "button.tab").find((b) => text(b) === "Custom").fire("click");
  await until(() => find(m.root, "input").length >= 2, "the bounds");
  const now = Date.now();
  m.store.setUi({ custom: { from: now - 20 * 24 * 60 * MIN, to: now - 10 * 24 * 60 * MIN } });
  await until(() => find(m.root, "span.archived").length === 1, "the archived chip");
  assert.ok(find(m.root, "rect.span").every((r) => r.attrs.get("class").includes("st-unknown")), "beyond retention no state is known: every lane is hatched unknown");
  m.store.setUi({ custom: { from: now - 2 * 60 * MIN, to: now } });
  await until(() => find(m.root, "span.archived").length === 0, "the chip to go");
});

test("Timeline: an edit that leaves the custom range backwards is ignored", async () => {
  const m = await open("timeline");
  find(m.root, "button.tab").find((b) => text(b) === "Custom").fire("click");
  await until(() => find(m.root, "input").length >= 2, "the bounds");
  const before = m.store.state.ui.custom;
  const t = before.from - 60 * MIN;
  const iso = new Date(t - new Date(t).getTimezoneOffset() * MIN).toISOString().slice(0, 16);
  find(m.root, "input")[1].fire("change", { currentTarget: { value: iso } });
  assert.deepEqual(m.store.state.ui.custom, before);
});
