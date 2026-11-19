import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const { laneOf } = await import("../../web/ui/select.js");
const { flowLayout } = await import("../../web/ui/graph.js");
const { FLOW_COLUMN } = await import("../../web/mock/model.mjs");

const D = "member:d/top";
const id = (name) => `${D}/${name}`;
const teamWith = (streams) => ({ body: { name: "top", dir_id: "d", orchestrator: id("@orchestrator"), members: [], streams } });
const slicesOf = (rows) => new Map(rows.map(([name, role, lead]) => [id(name), { body: { name, role, ...(lead ? { lead } : {}) } }]));
const ROWS = [
  ["@orchestrator", "orchestrator"],
  ["so-a", "sub-orchestrator", { team: "top", name: "@orchestrator" }],
  ["so-b", "sub-orchestrator"],
  ["so-c", "sub-orchestrator"],
  ["dev-1", "implementor", { team: "top-lane-a", name: "so-a" }],
  ["dev-2", "implementor", { team: "top-lane-a", name: "so-a" }],
  ["dev-3", "implementor", { team: "top-lane-b", name: "so-b" }],
  ["rev", "reviewer", { team: "top-lane-c", name: "so-c" }],
  ["arch", "architect"],
  ["stray", "implementor", { team: "elsewhere", name: "not-a-member" }],
  ["docs", "docs-writer"],
];
const STREAMS = ["a", "b", "c"].map((l) => ({ name: l, state: "open", sub_orchestrator: `so-${l}`, child_team: `top-lane-${l}` }));

const nodesOf = (rows, streams = STREAMS) => {
  const slices = slicesOf(rows);
  const ids = rows.map(([name]) => id(name));
  const lane = laneOf(slices, teamWith(streams), ids);
  return ids.map((i) => ({ id: i, role: slices.get(i).body.role, name: slices.get(i).body.name, lane: lane(i), column: FLOW_COLUMN[slices.get(i).body.role] ?? 2 }));
};
const EDGES = ROWS.slice(1).map(([name]) => ({ from: id("@orchestrator"), to: id(name) }));

test("a member's lane is the leader its served lead names; a stream's sub-orchestrator leads its own lane; the orchestrator and an unknown lead are in none", () => {
  const lane = Object.fromEntries(nodesOf(ROWS).map((n) => [n.name, n.lane]));
  assert.deepEqual(lane, {
    "@orchestrator": "",
    "so-a": id("so-a"),
    "so-b": id("so-b"),
    "so-c": id("so-c"),
    "dev-1": id("so-a"),
    "dev-2": id("so-a"),
    "dev-3": id("so-b"),
    rev: id("so-c"),
    arch: "",
    stray: "",
    docs: "",
  });
});

test("a member a lead in this team names leads a lane with no stream naming it; a lead naming the orchestrator puts no one in a lane", () => {
  const home = ROWS.map(([name, role, lead]) => [name, role, lead && { ...lead, team: "top" }]);
  const lane = Object.fromEntries(nodesOf(home, []).map((n) => [n.name, n.lane]));
  assert.equal(lane["so-a"], id("so-a"), "dev-1 names so-a as its lead; so-a's own lead, the orchestrator, does not put it in a lane");
  assert.equal(lane["dev-1"], id("so-a"));
  assert.equal(lane["so-b"], id("so-b"));
  assert.equal(lane["dev-3"], id("so-b"));
  const unnamed = Object.fromEntries(nodesOf(home.filter(([name]) => name !== "dev-3"), []).map((n) => [n.name, n.lane]));
  assert.equal(unnamed["so-b"], "", "a sub-orchestrator no stream and no lead names leads no lane");
});

test("a lead naming a member counts only from this team or the named sub-orchestrator's own stream team", () => {
  const rows = [...ROWS, ["dev-4", "implementor", { team: "some-other-team", name: "so-b" }], ["dev-5", "implementor", { team: "top-lane-a", name: "so-b" }]];
  const lane = Object.fromEntries(nodesOf(rows).map((n) => [n.name, n.lane]));
  assert.equal(lane["dev-4"], "", "an unrelated team");
  assert.equal(lane["dev-5"], "", "the child team of another sub-orchestrator's stream");
  assert.equal(lane["dev-3"], id("so-b"));
  const unconfirmed = Object.fromEntries(nodesOf(ROWS, STREAMS.map(({ child_team: _c, ...s }) => s)).map((n) => [n.name, n.lane]));
  assert.equal(unconfirmed["dev-1"], "", "a stream with no child_team served (no confirmed lane) vouches for no lead");
  assert.equal(unconfirmed["so-a"], id("so-a"), "its sub-orchestrator still leads the stream");
});

const polar = (p) => ({ r: Math.hypot((p.x - 500) / 1.35, p.y - 350), a: Math.atan2(p.y - 350, (p.x - 500) / 1.35) });
const gap = (a, b) => Math.abs(Math.atan2(Math.sin(a - b), Math.cos(a - b)));

test("Flow with lanes: one band per leader holding its lane's members, after the band of members in no lane; columns stay by role", () => {
  for (const orient of ["horizontal", "vertical"]) {
    const nodes = nodesOf(ROWS);
    const { pos } = flowLayout(nodes, new Set(), orient);
    const across = (name) => (orient === "horizontal" ? pos.get(id(name)).y : pos.get(id(name)).x);
    const span = (names) => [Math.min(...names.map(across)), Math.max(...names.map(across))];
    const bands = [["@orchestrator", "arch", "stray", "docs"], ["so-a", "dev-1", "dev-2"], ["so-b", "dev-3"], ["so-c", "rev"]].map(span);
    for (let i = 1; i < bands.length; i++) {
      assert.ok(bands[i][0] > bands[i - 1][1], `${orient}: band ${i} lies past band ${i - 1}`);
    }
    const along = (name) => (orient === "horizontal" ? pos.get(id(name)).x : pos.get(id(name)).y);
    assert.equal(along("so-a"), along("@orchestrator"), `${orient}: a leader stays in the Lead column`);
    assert.equal(along("dev-1"), along("dev-3"), `${orient}: implementors share their column across lanes`);
  }
});

test("a team with no lanes lays out exactly as the graph did before lanes, Flow both ways (golden from c3291604; ah2-225 adds the Test label)", () => {
  const g = JSON.parse(readFileSync(new URL("./fixtures/graph-plain-layout.json", import.meta.url), "utf8"));
  const rows = g.nodes.map((n) => [n.name, n.role]);
  const nodes = nodesOf(rows, []);
  assert.ok(nodes.every((n) => n.lane === ""));
  const obj = (m) => Object.fromEntries(m);
  for (const [orient, want] of [["horizontal", g.flowHorizontal], ["vertical", g.flowVertical]]) {
    const f = flowLayout(nodes, new Set(g.active), orient);
    // the cards are where they were; the fifth column's label (B2) is the one addition, and the box grows to hold it
    assert.deepEqual({ pos: obj(f.pos), labels: f.labels.slice(0, 4) }, { pos: want.pos, labels: want.labels }, orient);
    assert.equal(f.labels[4].text, "Test", orient);
  }
});

const { laneChildren, teamLeaseIds } = await import("../../web/ui/select.js");
const CHILD = "member:d/top-lane-a";
const managerWith = (streams) => ({ id: "team:d/top", body: { name: "top", dir_id: "d", orchestrator: id("@orchestrator"), members: ROWS.slice(1).map(([n]) => id(n)), streams } });
const childSlices = () => {
  const slices = slicesOf(ROWS);
  slices.set("team:d/top-lane-a", { body: { name: "top-lane-a", dir_id: "d", orchestrator: `${CHILD}/@orchestrator`, members: [`${CHILD}/a-impl`, `${CHILD}/a-rev`, id("dev-1")] } });
  slices.set(`${CHILD}/a-impl`, { body: { name: "a-impl", role: "implementor", activity: "activity:d/s1", last_exchanges: ["exchange:d/x1"] } });
  slices.set(`${CHILD}/a-rev`, { body: { name: "a-rev", role: "reviewer" } });
  slices.set("team:d/top-lane-b", { body: { name: "top-lane-b", dir_id: "d", members: ["member:d/top-lane-b/b-impl"] } });
  slices.set("team:d/top-lane-c", { body: { name: "top-lane-c", dir_id: "d", members: ["member:d/top-lane-c/c-impl"] } });
  return slices;
};
const LANE_STREAMS = [
  { name: "a", state: "open", sub_orchestrator: "so-a", child_team: "top-lane-a" },
  { name: "b", state: "done", sub_orchestrator: "so-b", child_team: "top-lane-b" },
  { name: "c", state: "open", sub_orchestrator: "so-c" },
  { name: "x", state: "open", sub_orchestrator: "not-a-member", child_team: "top-lane-c" },
];

test("a confirmed, open lane's child-team members join that lane; a done stream, an unconfirmed one and a lane whose leader is no member add none", () => {
  const slices = childSlices();
  const team = managerWith(LANE_STREAMS);
  const ids = [team.body.orchestrator, ...team.body.members];
  assert.deepEqual(laneChildren(slices, team, ids), [
    { id: `${CHILD}/a-impl`, team: "team:d/top-lane-a", lane: id("so-a") },
    { id: `${CHILD}/a-rev`, team: "team:d/top-lane-a", lane: id("so-a") },
  ], "dev-1, already a manager member, is not drawn twice");
});

test("the graph leases only the child teams, and their members, of confirmed, open lanes whose leader it draws", () => {
  const slices = childSlices();
  const team = managerWith(LANE_STREAMS);
  const without = new Set(teamLeaseIds(slices, team, { members: true }));
  const extra = teamLeaseIds(slices, team, { members: true, lanes: true }).filter((x) => !without.has(x));
  assert.deepEqual(extra.sort(), ["team:d/top-lane-a", `${CHILD}/a-impl`, "activity:d/s1", "exchange:d/x1", `${CHILD}/a-rev`].sort());
});

test("child-team members sit in their lane's band in Flow", () => {
  const nodes = [...nodesOf(ROWS), { id: `${CHILD}/a-impl`, role: "implementor", name: "a-impl", lane: id("so-a"), team: "team:d/top-lane-a", column: 2 }];
  const { pos: f } = flowLayout(nodes, new Set(), "horizontal");
  const free = Math.max(...["@orchestrator", "arch", "stray", "docs"].map((n) => f.get(id(n)).y));
  const y = f.get(`${CHILD}/a-impl`).y;
  assert.ok(y > free && y < f.get(id("so-b")).y && y < f.get(id("dev-1")).y, "in so-a's band: past the band of members in no lane, before so-b's");
});

test("a lane's label and a child member's name are untrusted text: shown literally, creating no element, in Flow", async () => {
  const { installDom, settle } = await import("./dom.mjs");
  const { render, html } = await import("../../web/ui/h.js");
  const { GraphStage } = await import("../../web/ui/app.js");
  const evil = "<img src=x onerror=alert(1)>";
  const nodes = nodesOf(ROWS).map((n) => (n.name === "so-a" ? { ...n, name: evil } : n));
  nodes.push({ id: `${CHILD}/x`, role: "implementor", name: evil, lane: id("so-a"), team: "team:d/top-lane-a" });
  const store = { state: { slices: new Map(), ui: {} }, subscribe: () => () => {}, lease: () => {} };
  for (const mode of ["flow"]) {
    const root = installDom();
    render(html`<${GraphStage} store=${store} teamId="t" nodes=${nodes} edges=${EDGES} sparks=${[]} selected=${null} onSelect=${() => {}} mode=${mode} />`, root);
    await settle();
    assert.equal(root.all("img").length, 0, `${mode}: no element from the name`);
    const labels = root.all("span").filter((s) => s.getAttribute("class") === "flane-label");
    assert.ok(labels.some((l) => l.textContent === evil), `${mode}: the label reads the literal text`);
    render(null, root);
  }
});

test("a sub-orchestrator leads a lane only while its stream is open: a done or closed stream gives it none, an open one does", () => {
  const rows = ROWS.filter(([name]) => !["dev-3", "rev"].includes(name));
  const lane = Object.fromEntries(nodesOf(rows, STREAMS.map((s) => (s.name === "b" ? { ...s, state: "done" } : s.name === "c" ? { ...s, state: "closed" } : s))).map((n) => [n.name, n.lane]));
  assert.equal(lane["so-b"], "", "done stream");
  assert.equal(lane["so-c"], "", "closed stream");
  assert.equal(lane["so-a"], id("so-a"), "open stream");
});

test("a member whose served lead names a done stream's child team and sub-orchestrator is in no lane, and neither is that sub-orchestrator", () => {
  const lane = Object.fromEntries(nodesOf(ROWS, STREAMS.map((s) => (s.name === "b" ? { ...s, state: "done" } : s))).map((n) => [n.name, n.lane]));
  assert.equal(lane["dev-3"], "", "lead {top-lane-b, so-b}: the stream is done");
  assert.equal(lane["so-b"], "");
  assert.equal(lane["dev-1"], id("so-a"), "the same shape against an open stream still gives a lane");
});

test("a lane child member shows the same state in the panel and the hover card as on its node, though the manager's summary serves member_states without it", async () => {
  const { memberModel } = await import("../../web/ui/member-summary.js");
  const { HoverCard } = await import("../../web/ui/hover.js");
  const { memberOf } = await import("../../web/ui/select.js");
  const kid = "member:d/top-lane-a/dev-1";
  const slice = (sid, body) => [sid, { id: sid, body }];
  const slices = new Map([
    slice("team:d/top", { name: "top", dir_id: "d", orchestrator: id("@orchestrator"), members: [id("so-a")], streams: [{ name: "a", state: "open", sub_orchestrator: "so-a", child_team: "top-lane-a" }] }),
    slice("team:d/top-lane-a", { name: "top-lane-a", dir_id: "d", orchestrator: id("so-a"), members: [kid] }),
    slice(id("@orchestrator"), { name: "@orchestrator", role: "orchestrator" }),
    slice(id("so-a"), { name: "so-a", role: "sub-orchestrator" }),
    slice(kid, { name: "dev-1", role: "implementor" }),
    slice("team-summary:d/top", { member_states: { [id("so-a")]: { state: "idle", process: "live" } } }),
    slice("team-summary:d/top-lane-a", { member_states: { [kid]: { state: "blocked", process: "live" } } }),
  ]);
  const snap = { slices, ui: { team: "team:d/top", hover: { id: kid, kind: "card", x: 0, y: 0 } } };
  const onNode = memberOf(slices, slices.get("team:d/top-lane-a"), kid);
  assert.equal(onNode.state, "blocked");
  const panel = memberModel(slices, snap.ui, kid);
  assert.equal(panel.state, "blocked", "summary");
  assert.equal(panel.teamId, "team:d/top-lane-a", "the member's team is the child team");
  const card = HoverCard.prototype.select.call({ props: { store: {} } }, snap);
  assert.equal(card.state, "blocked", "hover card");
  assert.equal(card.process, slices.get("team-summary:d/top-lane-a").body.member_states[kid].process, "the process line reads the child team's summary");
  const { ExpandOverlay } = await import("../../web/ui/expand.js");
  const filesId = "files:d/top-lane-a/dev-1";
  const withFiles = { ...snap, slices: new Map(slices).set(filesId, { id: filesId, body: { files: [], head: null } }) };
  const ctx = {};
  const expand = ExpandOverlay.prototype.select.call(ctx, withFiles, { memberId: kid });
  assert.equal(expand.state, "blocked", "expand overlay");
  assert.equal(expand.filesId, filesId, "the files id is the child team's");
  assert.equal(ctx.team, "d/top-lane-a");
  const own = memberModel(slices, snap.ui, id("so-a"));
  assert.equal(own.state, "idle", "a manager member still resolves through the manager team");
  assert.equal(own.teamId, "team:d/top");
});
