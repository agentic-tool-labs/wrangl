import { test } from "node:test";
import assert from "node:assert/strict";
import { mountApp as mount, until, find, text, member } from "./mount-app.mjs";
import { settle } from "./dom.mjs";

const { allowedKeys } = await import("../../web/mock/list-keys.mjs");

// The query keys each kind of list accepts, as the server's list pagers take them (after, before and limit everywhere; member on the
// exchange kinds; item on plan-changes; expand on a transcript; from_ms and to_ms on the timeline). The mock's 400 bad_filter uses this one table.
const TABLE = [
  ["list:timeline:a1b2c3d4", ["after", "before", "limit", "from_ms", "to_ms"]],
  ["list:transcript:a1b2c3d4/sess-architect", ["after", "before", "limit", "expand"]],
  ["list:exchanges:team:a1b2c3d4/ship", ["after", "before", "limit", "member", "with"]],
  ["list:feed:team:a1b2c3d4/ship", ["after", "before", "limit", "member", "with"]],
  ["list:plan-changes:a1b2c3d4/ship", ["after", "before", "limit", "item"]],
  ["list:open:team:a1b2c3d4/ship", ["after", "before", "limit", "member"]],
  ["list:plan:a1b2c3d4/ship", ["after", "before", "limit"]],
];

test("the key table: each kind takes the common keys and only its own extra, and an unknown kind takes only the common ones", () => {
  for (const [id, keys] of TABLE) {
    assert.deepEqual([...allowedKeys(id)].sort(), [...keys].sort(), id);
  }
  assert.equal(allowedKeys("list:timeline:x").has("from"), false, "from and to are not the timeline's keys");
  assert.equal(allowedKeys("timeline:x").has("from_ms"), false, "the timeline slice id is not a pager");
  assert.equal(allowedKeys("list:exchanges:team:x").has("expand"), false);
  assert.equal(allowedKeys("list:transcript:x").has("member"), false);
  assert.equal(allowedKeys("list:open:team:x").has("with"), false, "the open list has no pair filter");
  assert.deepEqual([...allowedKeys("list:unheard-of:x")].sort(), ["after", "before", "limit"]);
});

test("every list request the page makes carries only keys its kind accepts", async () => {
  const m = await mount();
  m.store.setUi({ view: "timeline" });
  await until(() => m.log.lists.some((l) => l.path.startsWith("/list/list:timeline-spans:")), "a timeline page");
  m.store.setUi({ view: "conversation", selected: member("architect") });
  await until(() => m.log.lists.some((l) => l.path.startsWith("/list/list:transcript:")), "a transcript page");
  m.store.setUi({ view: "member", selected: member("architect") });
  await until(() => m.log.lists.some((l) => l.params.member !== undefined && l.path.includes("list:exchanges:")), "a member-filtered exchange page");
  await settle();
  assert.ok(m.log.lists.length >= 4);
  const kinds = new Set();
  for (const { path, params } of m.log.lists) {
    const id = path.slice("/list/".length);
    kinds.add(id.split(":").slice(0, 2).join(":"));
    const allowed = allowedKeys(id);
    for (const key of Object.keys(params)) {
      assert.ok(allowed.has(key), `${id} was asked with ${key}`);
    }
  }
  for (const kind of ["list:timeline-spans", "list:transcript", "list:exchanges"]) {
    assert.ok(kinds.has(kind), `the run reached ${kind}`);
  }
});
