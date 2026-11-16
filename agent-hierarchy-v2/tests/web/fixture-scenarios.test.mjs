// The hand-written mock teams (web/mock/scenarios.mjs): AH_MOCK_FIXTURE=team and =lanes.
import { test } from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { PRESETS } from "../../web/mock/presets.mjs";
import { fixtureNames } from "../perf/e4-gates.mjs";

const { laneChildren } = await import("../../web/ui/select.js");

const load = async (env, tag) => {
  const before = { ...process.env };
  Object.assign(process.env, env);
  try {
    return await import(`../../web/mock/fixture.mjs?scenario-${tag}`);
  } finally {
    for (const key of Object.keys(env)) {
      if (before[key] === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = before[key];
      }
    }
  }
};

const NOW = Date.parse("2026-10-31T15:00:00Z");
const bytes = (body) => Buffer.byteLength(JSON.stringify(body));
const built = (fixture) => new Map(fixture.buildSlices(NOW).map((s) => [s.id, s]));
const ofKind = (slices, prefix) => [...slices.values()].filter((s) => s.id.startsWith(prefix));
// The members of a team slice, orchestrator first, as their member slices.
const membersOf = (slices, team) => [team.orchestrator, ...team.members].map((id) => slices.get(id)?.body).filter(Boolean);
const byStream = (members) => Object.groupBy(members.filter((m) => m.placement?.stream), (m) => m.placement.stream);

test("team: one team of seven with the typical roster, in three streams of one to three, and no lanes", async () => {
  const f = await load({ AH_MOCK_FIXTURE: "team" }, "team");
  const s = built(f);
  const team = s.get(f.TEAM_ID).body;
  const members = membersOf(s, team);
  assert.equal(members.length, 7);
  assert.deepEqual(members.map((m) => m.role).sort(), ["architect", "implementor", "implementor", "orchestrator", "qa", "reviewer", "ux-engineer"]);
  const streams = byStream(members);
  assert.equal(Object.keys(streams).length, 3);
  assert.deepEqual(Object.values(streams).map((m) => m.length).sort(), [1, 2, 3]);
  assert.equal(team.streams, undefined);
  assert.equal(ofKind(s, "team:").length, 1);
  assert.deepEqual(s.get("list:teams").body.teams.map((t) => t.name), [f.TEAM]);
  assert.match(team.pipeline.run, /^\d{8}-\d{6}-msg-board$/);
});

test("team: a few dozen exchanges with real briefs and reports, an answered question, and open briefs", async () => {
  const f = await load({ AH_MOCK_FIXTURE: "team" }, "team-x");
  const s = built(f);
  const ex = ofKind(s, "exchange:").map((x) => x.body);
  assert.ok(ex.length >= 24, `exchanges: ${ex.length}`);
  assert.ok(ex.every((e) => !e.title.startsWith("Task ")));
  assert.ok(ex.some((e) => e.title.startsWith("Question:") && e.has_response));
  assert.ok(ex.filter((e) => e.open).length >= 4);
  const q = ex.find((e) => e.title.startsWith("Question:"));
  assert.match(f.exchangeText(q.slug, true, q.id).response_excerpt, /\S/);
  assert.notEqual(f.exchangeText(q.slug, true, q.id).request_excerpt, f.exchangeText(q.slug, true).request_excerpt);
  assert.ok(ex.every((e) => e.team === f.TEAM));
});

test("team: plan items per stream, epics with deliverables, and checklist items parented to the deliverables", async () => {
  const f = await load({ AH_MOCK_FIXTURE: "team" }, "team-p");
  const s = built(f);
  const plan = s.get(`plan:${f.DIR}/${f.TEAM}`).body;
  assert.equal(plan.streams.length, 3);
  for (const { stream } of plan.streams) {
    assert.ok(s.get(`plan-items:${f.DIR}/${f.TEAM}/${stream}`).body.items.length >= 3, stream);
  }
  const backlog = s.get(`backlog:${f.DIR}`).body.items;
  const epics = backlog.filter((i) => i.kind === "epic").map((i) => i.key);
  assert.ok(epics.length >= 1 && epics.length <= 2);
  assert.ok(epics.every((e) => backlog.some((i) => i.kind === "deliverable" && i.parent === e)));
  const parented = ofKind(s, "plan-items:").flatMap((x) => x.body.items).filter((i) => i.parent);
  assert.ok(parented.length >= 10);
});

test("lanes: a top team of four plus two lanes, each a child team of three, about 14 members in all", async () => {
  const f = await load({ AH_MOCK_FIXTURE: "lanes" }, "lanes");
  const s = built(f);
  const top = s.get(f.TEAM_ID);
  const roles = membersOf(s, top.body).map((m) => m.role);
  assert.deepEqual(roles.filter((r) => r !== "sub-orchestrator").sort(), ["architect", "orchestrator", "qa", "reviewer"]);
  assert.equal(roles.filter((r) => r === "sub-orchestrator").length, 2);
  const ids = [top.body.orchestrator, ...top.body.members];
  const children = laneChildren(s, top, ids);
  assert.equal(children.length, 6);
  assert.equal(new Set(children.map((c) => c.lane)).size, 2);
  const total = ids.length + children.length;
  assert.ok(total >= 12 && total <= 14, `members: ${total}`);
  for (const c of children) {
    const m = s.get(c.id).body;
    assert.ok(["implementor", "ux-engineer", "reviewer"].includes(m.role));
    assert.ok(m.placement.stream.startsWith("ah/lane-"));
    assert.equal(m.lead.team, c.team.split("/").pop());
  }
});

test("lanes: traffic inside each lane and between the lanes and the top team, each listed under its own team", async () => {
  const f = await load({ AH_MOCK_FIXTURE: "lanes" }, "lanes-x");
  const s = built(f);
  const ex = ofKind(s, "exchange:").map((x) => x.body);
  const inLane = ex.filter((e) => e.team !== f.TEAM);
  assert.deepEqual([...new Set(inLane.map((e) => e.team))].sort(), [`${f.TEAM}-lane-board`, `${f.TEAM}-lane-delivery`]);
  // nearest miss: a sub-orchestrator's exchange with the top orchestrator is the top team's, not its lane's
  const laneToTop = ex.filter((e) => [e.from, e.to].includes("sub-orchestrator") && [e.from, e.to].includes("orchestrator"));
  assert.ok(laneToTop.length >= 4);
  assert.ok(laneToTop.every((e) => e.team === f.TEAM));
  for (const team of [f.TEAM, `${f.TEAM}-lane-board`, `${f.TEAM}-lane-delivery`]) {
    const list = s.get(`list:exchanges:team:${f.DIR}/${team}`).body;
    assert.deepEqual(list.ids, ex.filter((e) => e.team === team).map((e) => e.id).sort().reverse(), team);
    assert.ok(s.get(`list:open:team:${f.DIR}/${team}`).body.open.every((o) => s.get(o.id).body.team === team), team);
  }
});

for (const name of ["team", "lanes"]) {
  test(`${name}: every slice is under the 10240 B gate, and the flow has full pages over the team's own pairs only`, async () => {
    const f = await load({ AH_MOCK_FIXTURE: name }, `${name}-gate`);
    const s = built(f);
    for (const x of s.values()) {
      assert.ok(bytes(x.body) <= 10240, `${x.id}: ${bytes(x.body)} B`);
    }
    const head = s.get(f.flowId()).body;
    assert.ok(head.pages.length >= 2);
    assert.ok(ofKind(s, "item-msgs:").length > 1);
    const talk = new Set(ofKind(s, "exchange:").map((x) => x.body).filter((e) => e.team === f.TEAM).map((e) => `${f.memberOfParty(e.from_name ?? "orchestrator")}|${f.memberOfParty(e.to_name ?? "orchestrator")}`));
    for (const [a, b] of head.totals) {
      assert.ok(talk.has(`${head.names[a]}|${head.names[b]}`) || talk.has(`${head.names[b]}|${head.names[a]}`), `${head.names[a]} ↔ ${head.names[b]}`);
    }
  });
}

test("a fixture name that is no scenario keeps the default team: small, a case-different name, an inherited key", async () => {
  for (const [name, tag] of [["small", "small"], ["Team", "case"], ["toString", "proto"]]) {
    const f = await load({ AH_MOCK_FIXTURE: name }, `miss-${tag}`);
    const s = built(f);
    assert.ok(s.has(`member:${f.DIR}/${f.TEAM}/docs-writer`), name);
    assert.equal(s.get("list:teams").body.teams.length, 2, name);
    assert.match(s.get(f.TEAM_ID).body.pipeline.run, /-r6ui$/, name);
  }
});

test("team ignores AH_MOCK_LANES: a scenario without lanes shows no lane facts even when the knob asks for lanes", async () => {
  const f = await load({ AH_MOCK_FIXTURE: "team", AH_MOCK_LANES: "lanes" }, "team-lanes-knob");
  const s = built(f);
  assert.equal(s.get(f.TEAM_ID).body.streams, undefined);
  assert.equal(ofKind(s, "team:").length, 1);
});

test("E4 measures only small and realistic: the scenarios are no PRESETS rows", () => {
  assert.deepEqual(fixtureNames("both", undefined, PRESETS), ["small", "realistic"]);
  for (const name of ["team", "lanes"]) {
    assert.throws(() => fixtureNames(name, undefined, PRESETS), /unknown fixture/);
  }
});

test("the served model runs live ticks and traffic on each scenario", () => {
  const model = fileURLToPath(new URL("../../web/mock/model.mjs", import.meta.url));
  const script = `const { createMock } = await import(${JSON.stringify(model)}); const m = createMock(); for (let i = 0; i < 20; i += 1) { m.tick(); m.traffic(); } console.log("ok");`;
  for (const name of ["team", "lanes"]) {
    const out = execFileSync(process.execPath, ["--input-type=module", "-e", script], { env: { ...process.env, AH_MOCK_FIXTURE: name }, encoding: "utf8", timeout: 30_000 });
    assert.equal(out.trim(), "ok", name);
  }
});
