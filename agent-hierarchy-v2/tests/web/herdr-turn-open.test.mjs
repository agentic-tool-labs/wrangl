// ah2-62: the herdr sidebar's Orchestrator row spins while its turn is open (turn_open) and rests between turns.
import { test } from "node:test";
import assert from "node:assert/strict";
import { sidebarModel } from "../../herdr-desktop/mods/ah2/model.js";

const NOW = Date.parse("2026-11-15T12:00:00Z");
const O = "member:d1/t/@orchestrator";
const rowOf = (id, served) => {
  const slices = new Map([
    ["list:teams", { id: "list:teams", body: { teams: [{ dir_id: "d1", name: "t" }] } }],
    ["team-summary:d1/t", { id: "team-summary:d1/t", body: { name: "t", running: true, alerts: [], member_states: { [id]: { state_at: "2026-11-15T11:00:00Z", ...served } } } }],
  ]);
  const m = sidebarModel(slices, NOW).teams[0].members[0];
  return [m.state, m.word];
};

test("a live Orchestrator with an open turn reads Working, idle or unknown", () => {
  assert.deepEqual(rowOf(O, { state: "idle", liveness: "live", turn_open: true }), ["working", "Working"]);
  assert.deepEqual(rowOf(O, { state: "unknown", liveness: "live", turn_open: true }), ["working", "Working"]);
});

test("negative: a closed turn rests as Running; a state from the daemon, a dead one or a member's flag keeps what it shows", () => {
  assert.deepEqual(rowOf(O, { state: "idle", liveness: "live" }), ["running", "Running"]);
  assert.deepEqual(rowOf(O, { state: "blocked", liveness: "live", turn_open: true }), ["blocked", "Blocked"]);
  assert.deepEqual(rowOf(O, { state: "gone", liveness: "proven_dead", turn_open: true }), ["gone", "Gone"]);
  assert.deepEqual(rowOf(O, { state: "idle", liveness: "unknown", turn_open: true }), ["idle", "Idle"]);
  assert.deepEqual(rowOf("member:d1/t/dev", { state: "idle", liveness: "live", turn_open: true }), ["idle", "Idle"]);
});
