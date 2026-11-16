import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join, dirname } from "node:path";
import { settle } from "./dom.mjs";
import { mountApp, until, find, text } from "./mount-app.mjs";
import { buildSlices } from "../../web/mock/fixture.mjs";
import { checklistRows, CHECKLIST_STATES, hasOpenItems } from "../../web/ui/plans-model.js";
import { servedItems } from "../../web/mock/model.mjs";

// The rows of items as the daemon serves them, each with its checklist rank.
const rowsOf = (streams, itemsOf, ...rest) => checklistRows(streams, (s) => {
  const items = itemsOf(s);
  return items && servedItems({ items }, false).items;
}, ...rest);

// The Checklist shows the open items of a team's streams from the plan-items slices; what the daemon serves on an item is pinned here.

const lane = readFileSync(join(dirname(fileURLToPath(import.meta.url)), "..", "..", "native", "src", "Ah.Watch", "Model", "ProgressLane.cs"), "utf8");
const SERVED = ["key", "title", "state", "holder", "done_seen_at", "role", "todo", "refs", "state_since", "updated_at"];

test("a mock plan-items item has exactly the fields the daemon serves, each only when the item has it, and refs only when not empty", () => {
  for (const f of SERVED) {
    assert.ok(lane.includes(`["${f}"]`) || lane.includes(`"${f}"`), `the daemon's progress lane names ${f}`);
  }
  assert.match(lane, /plan\.Refs is \{ Count: > 0 \}/, "the daemon leaves refs out when empty");
  const items = buildSlices(Date.now()).filter((s) => s.id.startsWith("plan-items:")).flatMap((s) => s.body.items);
  assert.ok(items.length > 0);
  for (const item of items) {
    assert.deepEqual(Object.keys(item).filter((k) => !SERVED.includes(k)), []);
    assert.equal("refs" in item && item.refs.length === 0, false);
  }
  assert.ok(items.some((i) => i.todo && i.role && i.state_since && i.updated_at), "an item with the fields");
  assert.ok(items.some((i) => !("todo" in i) && !("role" in i) && !("state_since" in i) && !("updated_at" in i)), "an item without them");
  assert.ok(items.some((i) => "refs" in i) && items.some((i) => !("refs" in i)), "refs present on some and absent on others");
});

test("the checklist lists open items only, waiting-orch first, then blocked, waiting on you, dispatched and queued, each stream in its own order", () => {
  const streams = [{ id: "a", stream: "s1", owner: "x" }, { id: "b", stream: "s2", owner: "y" }];
  const items = {
    a: [{ key: "1", title: "q1", state: "not_started" }, { key: "2", title: "d1", state: "in_progress", holder: "m" }, { key: "3", title: "gone", state: "done" }, { key: "4", title: "w1", state: "waiting_user" }],
    b: [{ key: "5", title: "o1", state: "waiting_orch", holder: "n" }, { key: "6", title: "b1", state: "blocked" }, { key: "7", title: "q2", state: "next" }, { key: "8", title: "drop", state: "dropped" }],
  };
  const rows = rowsOf(streams, (s) => items[s.id]);
  assert.deepEqual(rows.map((r) => r.title), ["o1", "b1", "w1", "d1", "q1", "q2"]);
  assert.equal(rows[0].owner, "n");
  assert.equal(rows[1].owner, null, "no holder is unassigned, not the stream's owner");
  assert.equal(rowsOf(streams, () => null).length, 0, "items not held yet make no rows");
  const withFields = rowsOf([streams[0]], () => [{ key: "1", title: "t", state: "blocked", role: "implementor", todo: "x", refs: ["r1"], state_since: "2026-10-21T12:00:00Z", updated_at: "2026-10-21T12:05:00Z" }], (s, refs) => ({ refs }))[0];
  assert.deepEqual([withFields.role, withFields.todo, withFields.refs, withFields.stateSince, withFields.updatedAt, withFields.eta], ["implementor", "x", ["r1"], Date.parse("2026-10-21T12:00:00Z"), Date.parse("2026-10-21T12:05:00Z"), { refs: ["r1"] }]);
  const bare = rowsOf([streams[0]], () => [{ key: "1", title: "t", state: "blocked" }])[0];
  assert.deepEqual([bare.role, bare.todo, bare.refs, bare.stateSince, bare.updatedAt, bare.eta], [null, null, [], null, null, null]);
  assert.deepEqual([...new Set(CHECKLIST_STATES.map((s) => s.glyph))].length, 8, "queued share a glyph; every other state has its own");
  const after = rowsOf([streams[0]], () => ["next", "approved", "testing", "in_review", "in_progress"].map((state, i) => ({ key: String(i), title: "t", state }))).map((r) => r.state);
  assert.deepEqual(after, ["in_progress", "in_review", "testing", "approved", "next"], "review, testing and approved rank after in progress, and before queued");
});

test("a finished stream stays on the checklist only while it has open items, as checklist show lists it", () => {
  assert.equal(hasOpenItems({ state: "done", counts: { done: 2, next: 1 } }), true);
  assert.equal(hasOpenItems({ state: "done", counts: { done: 2, dropped: 1, next: 0 } }), false);
});

test("the Checklist view lists the team's open items with owner and state by name, attention states bold, and a not-served-yet slot", async () => {
  const m = await mountApp({ view: "checklist" });
  await until(() => find(m.root, "tr.cl-row").length > 0, "the checklist rows");
  await settle();
  const rows = find(m.root, "tr.cl-row");
  const states = rows.map((r) => r.className.match(/st-(\w+)/)[1]);
  assert.equal(states[0], "waiting_orch", "waiting-orch comes first");
  const rank = (s) => ["waiting_orch", "blocked", "waiting_user", "in_progress", "in_review", "approved", "next"].indexOf(s === "not_started" ? "next" : s);
  assert.deepEqual(states.map(rank), states.map(rank).sort((a, b) => a - b), "ordered by state");
  assert.equal(states.includes("done") || states.includes("dropped"), false, "done and dropped items are not open");
  const first = rows[0];
  assert.match(text(first), /waiting-orch/);
  assert.match(first.all("td")[0].className, /\battn\b/, "an attention state is bold, not colour alone");
  assert.match(text(first.all("td.cl-owner")[0]), /\S/);
  assert.match(text(first.all("td.cl-todo")[0]), /answer implementor-1: STATUS/, "a served field is shown");
  assert.match(text(first.all("td.cl-since")[0]), /ago/);
  assert.match(text(first.all("td.cl-refs")[0]), /none/, "refs absent means none, not not-served");
  const bare = rows.find((r) => /R6b frame/.test(text(r)));
  assert.match(text(bare.all("td.cl-todo")[0]), /none/, "a field only some items have reads none on the others");
  assert.ok(!text(bare).includes("not served yet"));
  const linkedRow = () => find(m.root, "tr.cl-row").find((r) => /R6a data core/.test(text(r)));
  assert.match(text(linkedRow().all("td.cl-refs")[0]), /20261018-0110-aa01/);
  await until(() => linkedRow().all("td.cl-refs")[0].all("span.eta").length === 1, "the linked exchange's ETA chip");
  assert.match(text(find(m.root, "div.cl-summary")[0]), /\d+ open · \d+ waiting-orch/);
  // Negative: once no item has a role, the column goes.
  m.model.publish(buildSlices(Date.now()).filter((x) => x.id.startsWith("plan-items:")).map((x) => ({ ...x, body: { ...x.body, items: x.body.items.map(({ role: _r, ...i }) => i) } })));
  await until(() => !find(m.root, "th").some((h) => text(h) === "Role"), "the Role column gone");
  assert.ok(find(m.root, "tr.cl-row").length > 0);
});

const rgb = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
const lin = (c) => ((c /= 255) <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
const lum = (hex) => rgb(hex).reduce((n, c, i) => n + [0.2126, 0.7152, 0.0722][i] * lin(c), 0);
const ratio = (a, b) => (Math.max(lum(a), lum(b)) + 0.05) / (Math.min(lum(a), lum(b)) + 0.05);

test("the state colours and the muted text meet 4.5:1 on the page and the header band in both themes", async () => {
  const { fixture } = await import("./lib.mjs");
  const tokens = fixture("visual-tokens.json");
  for (const theme of ["dark", "light"]) {
    for (const fg of ["review", "attn", "working", "muted"]) {
      for (const bg of ["bg", "bar"]) {
        assert.ok(ratio(tokens[theme][fg], tokens[theme][bg]) >= 4.5, `${theme} ${fg} on ${bg}`);
      }
    }
  }
});
