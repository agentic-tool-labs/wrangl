// ah2-220: a lane's orchestrator is labelled Sub-Orchestrator in the web UI (select.js memberLabel, the one rule); keys stay orchestrator.
import { test } from "node:test";
import assert from "node:assert/strict";
import { memberLabel, memberOf, nameIn, partyName } from "../../web/ui/select.js";
import { matches, teamModel } from "../../web/ui/plans-model.js";

const team = (name, lane) => ({ id: `team:d1/${name}`, body: { name, dir_id: "d1", orchestrator: `member:d1/${name}/@orchestrator`, members: [], ...(lane ? { lane: { parent_team: lane, stream: name } } : {}) } });
const slicesOf = (...ts) => new Map(ts.flatMap((t) => [[t.id, t], [`team-summary:d1/${t.body.name}`, { body: { name: t.body.name, ...(t.body.lane ? { lane: t.body.lane } : {}) } }]]));

test("the rule: a lane's orchestrator is Sub-Orchestrator, the top one and an ordinary member are untouched, no lane information is Orchestrator", () => {
  assert.deepEqual(memberLabel("member:d1/lane-herdr/@orchestrator", "@orchestrator", "orchestrator", "lane-herdr"),
    { label: "Sub-Orchestrator", role: "sub-orchestrator", title: "Sub-Orchestrator of lane-herdr" });
  assert.deepEqual(memberLabel("member:d1/ah2/@orchestrator", "@orchestrator", "orchestrator", null), { label: "@orchestrator", role: "orchestrator", title: "@orchestrator" });
  assert.deepEqual(memberLabel("member:d1/lane-herdr/architect", "architect", "architect", "lane-herdr"), { label: "architect", role: "architect", title: "architect" });
  for (const lane of [undefined, "", null]) assert.equal(memberLabel("member:d1/x/@orchestrator", "@orchestrator", "orchestrator", lane).label, "@orchestrator");

  const slices = slicesOf(team("ah2"), team("lane-herdr", "ah2"));
  const lanes = memberOf(slices, slices.get("team:d1/lane-herdr"), "member:d1/lane-herdr/@orchestrator");
  const top = memberOf(slices, slices.get("team:d1/ah2"), "member:d1/ah2/@orchestrator");
  assert.deepEqual([lanes.name, lanes.label, lanes.role, lanes.title], ["@orchestrator", "Sub-Orchestrator", "sub-orchestrator", "Sub-Orchestrator of lane-herdr"]);
  assert.deepEqual([top.name, top.label, top.title], ["@orchestrator", "@orchestrator", "@orchestrator"]);
  // a bare owner or party name, and a team the slices say nothing of
  assert.equal(nameIn(slices, "d1", "lane-herdr", "orchestrator").label, "Sub-Orchestrator");
  assert.equal(nameIn(slices, "d1", "unknown-team", "orchestrator").label, "orchestrator");
  assert.equal(partyName({ dir_id: "d1", team: "lane-herdr", from: "orchestrator" }, "from", slices), "Sub-Orchestrator");
  assert.equal(partyName({ dir_id: "d1", team: "lane-herdr", from: "orchestrator" }, "from"), "orchestrator");
});

test("a lane stream's owner shows the label and still matches by its key", () => {
  const row = { id: "team:d1/lane-herdr", name: "lane-herdr", dirId: "d1", repo: null, branch: null, status: "live", laneOf: "ah2" };
  const plan = { streams: [{ stream: "lane-herdr", owner: "orchestrator", goal: "", branch: "", counts: {}, total: 0 }] };
  const [s] = teamModel(row, plan, Date.now()).streams;
  assert.deepEqual([s.owner, s.ownerLabel, s.ownerTitle, s.role], ["orchestrator", "Sub-Orchestrator", "Sub-Orchestrator of lane-herdr", "sub-orchestrator"]);
  assert.equal(matches(s, "orchestrator"), true);
  const [top] = teamModel({ ...row, id: "team:d1/ah2", name: "ah2", laneOf: null }, plan, Date.now()).streams;
  assert.deepEqual([top.ownerLabel, top.role], ["orchestrator", "orchestrator"]);
});

// Amendment 1: "asked by" reads the asker address's own team through the same rule.
test("an asker address of a lane's orchestrator reads Sub-Orchestrator; the top one, a worker and an unknown team read as served", async () => {
  const { askerLabel } = await import("../../web/ui/select.js");
  const slices = slicesOf(team("ah2"), team("lane-herdr", "ah2"));
  assert.deepEqual(askerLabel(slices, "d1", "ah2", "orchestrator@lane-herdr"), { label: "Sub-Orchestrator", title: "Sub-Orchestrator of lane-herdr" });
  assert.deepEqual(askerLabel(slices, "d1", "lane-herdr", "orchestrator"), { label: "Sub-Orchestrator", title: "Sub-Orchestrator of lane-herdr" });
  for (const who of ["orchestrator@ah2", "architect@lane-herdr", "orchestrator@gone-team", "", null]) assert.equal(askerLabel(slices, "d1", "ah2", who), null);
});
