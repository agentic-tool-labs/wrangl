import { test } from "node:test";
import assert from "node:assert/strict";

const { briefsInFlight, openBriefsOf, sparksOf } = await import("../../web/ui/select.js");

const team = { body: { dir_id: "d", name: "t", orchestrator: "m:o", members: ["m:live", "m:dead"] } };
const entry = (id, to, from = "m:o") => ({ id, to_member: to, from_member: from, unattended_at: to ? null : "2026-11-01T00:00:00Z" });
const slices = new Map([
  ["m:live", { body: { name: "live", role: "implementor" } }],
  ["m:dead", { body: { name: "dead", role: "reviewer" } }],
  ["team-summary:d/t", { body: { stale_ids: ["x2"] } }],
  ["x1", { body: { slug: "to-live" } }],
  ["x2", { body: { slug: "to-dead" } }],
  ["list:open:team:d/t", { body: { open: [entry("x1", "m:live"), entry("x2", "m:dead"), entry("x3", "m:dismissed"), entry("x4", null)] } }],
]);

test("a brief is in flight unless the daemon serves it as stale or unattended", () => {
  assert.deepEqual(briefsInFlight(slices, team).map((o) => o.id), ["x1", "x3"], "x2 is served stale and x4 unattended; x3 names a member whose slice is not loaded");
});

test("a request to a gone or unresolved addressee is neither an open brief nor a spark", () => {
  assert.equal(openBriefsOf(slices, team, "m:dead").length, 0, "the dead member has no open briefs");
  assert.deepEqual(openBriefsOf(slices, team, "m:live").map((b) => b.body.slug), ["to-live"]);
  assert.deepEqual(sparksOf(slices, team).map((s) => s.id), ["x1", "x3"]);
});

test("the same list with nothing served stale keeps every addressed brief in flight (the nearest non-match)", () => {
  const all = new Map(slices).set("team-summary:d/t", { body: { stale_ids: [] } });
  assert.deepEqual(briefsInFlight(all, team).map((o) => o.id), ["x1", "x2", "x3"]);
});
