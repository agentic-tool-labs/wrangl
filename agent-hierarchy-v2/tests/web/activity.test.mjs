// ah2-225 Part B: the jobs and subagents under their owners (web/ui/flow-model.js, shared with the mod) and the mod's tree of them
// (herdr-desktop/mods/ah2/model.js teamTree over the fixture). The drawing is not tested here.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const F = await import("../../web/ui/flow-model.js");
const M = await import("../../herdr-desktop/mods/ah2/model.js");
const G = await import("../../herdr-desktop/mods/ah2/graph-model.js");

const NOW = Date.parse("2026-11-18T12:00:00Z");
const ago = (m) => new Date(NOW - m * 60_000).toISOString();
const owners = new Map([["orchestrator", "m/orch"], ["impl", "m/impl"], ["qa", "m/qa"]]);
const src = (rows, more = {}) => ({ rows, owners, header: "team:t", ...more });
const job = (id, owner, kind, o = {}) => ({ id, owner, kind, label: `run ${id}`, threshold_s: 900, sent_at: ago(9), ended_at: null, slot_phase: null, slot_since: null, state: "working", ...o });
const run = (agent_id, owner, owner_kind = "member", m = 3, agent_type = "wrangl:reviewer") => ({ agent_id, agent_type, owner, owner_kind, started_at: ago(m), ttl_s: 3600 });

test("jobsUnder: a job under its owner's key, one whose owner is not on the team under the header, an ended one not at all", () => {
  const u = F.jobsUnder([src([job("a", "impl", "smoke"), job("b", "ghost", "build"), job("c", "impl", "test", { ended_at: ago(1) })])]);
  assert.deepEqual([...u.keys()], ["m/impl", "team:t"]);
  assert.deepEqual(u.get("m/impl").map((j) => j.key), ["job:a"]);
  assert.deepEqual(u.get("team:t").map((j) => j.ownerName), ["ghost"]);
  assert.equal(F.jobsUnder([src([])]).size, 0, "none: nothing to draw");
  assert.equal(F.jobsUnder([]).size, 0);
});

test("a job's time runs against its estimate from the slot it holds; a queued one waits for the slot; overdue is the served state", () => {
  const u = F.jobsUnder([src([job("a", "qa", "test", { slot_phase: "holding", slot_since: ago(8) }), job("q", "qa", "slot", { slot_phase: "queued", slot_since: ago(2) }), job("o", "impl", "bg", { sent_at: ago(31), state: "overdue" })])]);
  const [held, queued] = ["job:a", "job:q"].map((k) => u.get("m/qa").find((j) => j.key === k));
  assert.equal(F.runText(held, NOW), "8m of ~15m");
  assert.equal(F.runText(queued, NOW), "waiting for the test slot");
  assert.equal(u.get("m/impl")[0].overdue, true);
  assert.match(F.underTip(u.get("m/impl")[0], "impl", NOW), /overdue$/, "overdue is said in words, not colour alone");
  const long = F.jobsUnder([src([job("l", "impl", "bg", { label: `a\nb${"x".repeat(80)}` })])]).get("m/impl")[0].label;
  assert.ok(long.length <= 48 && !long.includes("\n") && long.endsWith("…"), "a label is one inert, cut line");
});

test("subagentsUnder: under its owner, a stray one under the header, a lapsed or empty one not at all; a lane's copy of a run wins", () => {
  const parent = src([run("r1", "so-lane", "member"), run("r2", "impl"), run("old", "impl", "member", 61), run("s", "nobody")], { owners: new Map([...owners, ["so-lane", "m/so"]]) });
  const lane = src([run("r1", "orchestrator", "orchestrator")], { owners: new Map([["orchestrator", "m/lane-orch"]]), header: "team:lane" });
  const u = F.subagentsUnder([parent, lane], NOW);
  assert.deepEqual(u.get("m/lane-orch").map((s) => s.key), ["subagent:r1"], "drawn once, under the lane's Sub-Orchestrator");
  assert.equal(u.has("m/so"), false);
  assert.deepEqual(u.get("m/impl").map((s) => s.key), ["subagent:r2"], "a run past its ttl_s is lapsed");
  assert.deepEqual(u.get("team:t").map((s) => s.key), ["subagent:s"]);
  assert.equal(u.get("m/impl")[0].role, "reviewer", "the type's role, after the plugin");
  assert.equal(F.underTip(u.get("m/impl")[0], "impl", NOW), "wrangl:reviewer: subagent of impl, running 3m");
  assert.equal(F.subagentsUnder([src([])], NOW).size, 0);
});

test("Flow: a QA member and a full test run go to the Test column; a smoke test and a subagent sit right under their owner", () => {
  assert.equal(F.COLUMNS[F.TEST_COLUMN], "Test");
  const slices = new Map([["m/orch", { body: { name: "orchestrator", role: "orchestrator", column: 0 } }], ["m/impl", { body: { name: "impl", role: "implementor", column: 2 } }],
    ["m/qa", { body: { name: "qa", role: "qa", column: 3 } }], ["m/z", { body: { name: "z-impl", role: "implementor", column: 2 } }]]);
  const nodes = F.flowNodes(slices, { body: {} }, ["m/orch", "m/impl", "m/qa", "m/z"], []);
  assert.equal(nodes.find((n) => n.id === "m/qa").column, F.TEST_COLUMN, "the daemon's Review column for QA moves to Test");
  const under = F.underOf(F.jobsUnder([src([job("full", "orchestrator", "test"), job("smk", "impl", "smoke")])]), F.subagentsUnder([src([run("s1", "impl")])], NOW));
  const extras = F.flowExtras(nodes, under);
  const by = (k) => extras.find((n) => n.id === k);
  assert.deepEqual([by("job:full").column, by("job:full").after], [F.TEST_COLUMN, null], "a full run is a card in Test, its owner named");
  assert.deepEqual([by("job:smk").column, by("job:smk").after], [2, "m/impl"]);
  const f = F.flowLayout([...nodes, ...extras], new Set(), "horizontal");
  const [impl, sub, smk, z] = ["m/impl", "subagent:s1", "job:smk", "m/z"].map((k) => f.pos.get(k));
  assert.ok(impl.y < smk.y && smk.y < sub.y && sub.y < z.y, "under the owner in start order, before the next member");
  assert.equal(sub.x, impl.x + F.UNDER_INDENT);
  assert.equal(f.pos.get("job:full").x, f.pos.get("m/qa").x, "in the Test column with the QA member");
  assert.equal(F.underLinks([...nodes, ...extras], f.pos, "horizontal").length, 1, "one dashed link, the subagent's to its owner");
  assert.equal(F.flowExtras(nodes, new Map()).length, 0, "none: the members alone");
});

test("the mod's tree: jobs and subagents are leaves under their owner, a stray job under the team header; counts do not change", () => {
  const reply = JSON.parse(readFileSync(new URL("../../herdr-desktop/fixture/events.json", import.meta.url), "utf8"));
  const slices = new Map(reply.slices.map((s) => [s.id, s]));
  const now = Date.parse(reply.observed_at);
  const tm4 = M.sidebarModel(slices, now).teams.find((t) => t.name === "ah2-tm4");
  const rows = M.treeRows(M.teamTree(tm4), () => true).map((r) => [r.node.depth, r.node.kind, r.node.name ?? r.node.item?.key ?? r.node.team?.name]);
  assert.deepEqual(rows.slice(0, 4), [[0, "job", "job:j-ghost"], [0, "member", "orchestrator"], [1, "job", "job:j-qa1"], [1, "subagent", "subagent:a-exp1"]]);
  assert.ok(!rows.some((r) => r[2] === "job:j-done" || r[2] === "subagent:a-old"), "an ended job and a lapsed run are not drawn");
  const lane = rows.findIndex((r) => r[1] === "lane");
  assert.deepEqual(rows.slice(lane + 1, lane + 3).map((r) => r[2]), ["orchestrator", "subagent:a-rev1"], "the lane's run under the lane's orchestrator");
  const bare = M.sidebarModel(new Map([...slices].filter(([id]) => !/^(jobs|subagents):/.test(id))), now).teams.find((t) => t.name === "ah2-tm4");
  assert.deepEqual([tm4.working, tm4.blocked, tm4.needsYou, tm4.members.length], [bare.working, bare.blocked, bare.needsYou, bare.members.length]);
  assert.ok(M.treeRows(M.teamTree(bare), () => true).every((r) => !r.node.item), "none: the tree as it was");
  assert.ok(M.activitySubs(slices).includes("jobs:a1b2c3d4/ah2-tm4") && G.graphSubs(slices).includes("subagents:a1b2c3d4/lane-herdr"), "one added subscription per surface");
});

test("Amendment 3: the default team's jobs are read from its _default progress folder; a subagent's lapse time is known for the age tick", () => {
  assert.equal(F.activityId("jobs", "d1/default"), "jobs:d1/_default");
  assert.equal(F.activityId("jobs", "d1/ah2-tm4"), "jobs:d1/ah2-tm4");
  assert.equal(F.activityId("subagents", "d1/default"), "subagents:d1/default", "subagents: is left as it is");
  const team = { body: { dir_id: "d1", name: "default", members: [] } };
  assert.deepEqual(F.activityIds(team, [], ["jobs"]), ["jobs:d1/_default"], "the Overview leases jobs: only");
  const slices = new Map([["jobs:d1/_default", { body: { rows: [job("a", "impl", "smoke")] } }]]);
  const srcs = F.graphSources(slices, team, [], [], "jobs");
  assert.equal(srcs[0].rows.length, 1, "the default team's job reaches the model");
  const u = F.subagentsUnder([src([run("r", "impl", "member", 3)])], NOW);
  assert.equal(F.lapseAt(u), NOW - 3 * 60_000 + 3600_000);
  assert.equal(F.lapseAt(new Map()), Infinity);
});
