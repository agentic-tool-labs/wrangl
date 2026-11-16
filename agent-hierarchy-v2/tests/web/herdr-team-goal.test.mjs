// ah2-53 sitting 2: the herdr team row's goal line (sidebarModel), its own goal only, as one inert line.
import { test } from "node:test";
import assert from "node:assert/strict";
import { sidebarModel, inert } from "../../herdr-desktop/mods/ah2/model.js";

const NOW = Date.parse("2026-11-15T12:00:00Z");
const summary = (name, extra = {}) => ({ name, running: true, member_states: {}, alerts: [], ...extra });
const slicesOf = (bodies) => new Map([
  ["list:teams", { id: "list:teams", body: { teams: Object.keys(bodies).map((name) => ({ dir_id: "d1", name })) } }],
  ...Object.entries(bodies).map(([name, b]) => [`team-summary:d1/${name}`, { id: `team-summary:d1/${name}`, body: b }]),
]);

test("a team's own goal is its row's line and the tooltip text carries the done-when", () => {
  const m = sidebarModel(slicesOf({ mgr: summary("mgr", { goal: { text: "ship the row", done_when: "all green", team: "mgr" } }) }), NOW);
  const row = m.teams[0];
  assert.equal(row.goal, "ship the row");
  assert.equal(row.goalFull, "ship the row — done when: all green");
});

test("a lane showing its parent's goal has no line; one with its own goal shows that", () => {
  const parent = summary("mgr", { goal: { text: "parent aim", done_when: null, team: "mgr" } });
  const inherits = sidebarModel(slicesOf({ mgr: parent, "mgr-s1": summary("mgr-s1", { lane: { parent_team: "mgr" }, goal: { text: "parent aim", done_when: null, team: "mgr" } }) }), NOW);
  const lane = inherits.teams[0].lanes[0];
  assert.equal(inherits.teams[0].goal, "parent aim");
  assert.equal(inherits.teams[0].goalFull, "parent aim");
  // negative: the inherited goal is not repeated on the lane's row
  assert.equal(lane.goal, null);
  assert.equal(lane.goalFull, null);
  const own = sidebarModel(slicesOf({ mgr: parent, "mgr-s1": summary("mgr-s1", { lane: { parent_team: "mgr" }, goal: { text: "lane aim", done_when: null, team: "mgr-s1" } }) }), NOW);
  assert.equal(own.teams[0].lanes[0].goal, "lane aim");
});

test("a team with no goal has no line", () => {
  const row = sidebarModel(slicesOf({ tb: summary("tb") }), NOW).teams[0];
  assert.equal(row.goal, null);
  assert.equal(row.goalFull, null);
});

test("goal text is one inert line: escapes, controls and bidi overrides go, a newline or tab is a space, markup stays text", () => {
  assert.equal(inert("ship\nit\tnow\x1b[31m red\x07‮<b>x</b>"), "ship it now red<b>x</b>");
  // negative: plain text is unchanged
  assert.equal(inert("ship v1 — done"), "ship v1 — done");
  const row = sidebarModel(slicesOf({ mgr: summary("mgr", { goal: { text: "a\nb", done_when: "c‮d", team: "mgr" } }) }), NOW).teams[0];
  assert.equal(row.goalFull, "a b — done when: cd");
});
