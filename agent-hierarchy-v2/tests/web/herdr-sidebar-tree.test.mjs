// ah2-207: the Wrangl pane draws each team as a tree (model.js teamTree, treeRows), each row in the Graph's colours (graph-model.js rowMark).
import { test } from "node:test";
import assert from "node:assert/strict";
import { sidebarModel, teamTree, treeRows } from "../../herdr-desktop/mods/ah2/model.js";
import { cardStyle, rowMark } from "../../herdr-desktop/mods/ah2/graph-model.js";
import { isActive } from "../../herdr-desktop/mods/ah2/flow-model.js";

const NOW = Date.parse("2026-11-18T12:00:00Z");
const live = (state) => ({ state, liveness: "live", state_at: "2026-11-18T11:58:00Z" });
const summary = (name, members, lane = null) => [`team-summary:d1/${name}`, { id: `team-summary:d1/${name}`,
  body: { name, running: true, member_states: members, ...(lane ? { lane: { parent_team: lane } } : {}) } }];
const slicesOf = (...teams) => new Map([
  ["list:teams", { id: "list:teams", body: { teams: teams.map(([id]) => ({ dir_id: "d1", name: id.split("/").pop() })) } }],
  ...teams,
]);

test("a team with an orchestrator, two members and a lane with one member is a tree of those shapes, depths and roles", () => {
  const slices = slicesOf(
    summary("ah2", { "d1/ah2/@orchestrator": live("working"), "d1/ah2/@implementor": live("working"), "d1/ah2/@reviewer-2": { ...live("idle"), role: "reviewer" } }),
    summary("lane-a", { "d1/lane-a/@orchestrator": live("idle"), "d1/lane-a/@architect": live("blocked") }, "ah2"),
  );
  const [tm] = sidebarModel(slices, NOW).teams;
  const shape = (n) => [n.kind, n.depth, n.kind === "lane" ? n.team.name : n.name, n.role ?? null, n.children.map(shape)];
  assert.deepEqual(teamTree(tm).map(shape), [
    ["member", 0, "orchestrator", "orchestrator", [
      ["member", 1, "implementor", "implementor", []],
      ["member", 1, "reviewer-2", "reviewer", []],
      ["lane", 1, "lane-a", null, [
        ["member", 2, "orchestrator", "sub-orchestrator", [
          ["member", 3, "architect", "architect", []],
        ]],
      ]],
    ]],
  ]);
  // the rows a fully open tree draws: the lane is the last child, so nothing runs on past it at depth 1
  const rows = treeRows(teamTree(tm), () => true);
  assert.deepEqual(rows.map((r) => [r.node.depth, r.rails, r.last, r.kids]), [
    [0, [], true, true], [1, [], false, false], [1, [], false, false], [1, [], true, true], [2, [false], true, true], [3, [false, false], true, false]]);
  // negative: a folded lane hides its subtree and keeps its own row
  assert.equal(treeRows(teamTree(tm), (n) => n.kind !== "lane").length, 4);
});

test("a team with only an orchestrator is one node; a name that is no role's is the generic role", () => {
  const [solo] = sidebarModel(slicesOf(summary("solo", { "d1/solo/@orchestrator": live("idle") })), NOW).teams;
  const tree = teamTree(solo);
  assert.equal(tree.length, 1);
  assert.deepEqual([tree[0].children, treeRows(tree, () => true).length], [[], 1]);
  const [odd] = sidebarModel(slicesOf(summary("odd", { "d1/odd/@orchestrator": live("idle"), "d1/odd/@scout": live("idle") })), NOW).teams;
  assert.equal(teamTree(odd)[0].children[0].role, "");
});

// ah2-272 D2: a name carrying its team's prefix still gives the role when none is served; negative: a prefixed name that is no role's.
test("a member named <team>-<role>-N with no served role has that role; <team>-helper has none", () => {
  const [tm] = sidebarModel(slicesOf(summary("ah2", { "d1/ah2/@orchestrator": live("idle"), "d1/ah2/@ah2-ux-engineer-2": live("idle"), "d1/ah2/@ah2-helper": live("idle") })), NOW).teams;
  const roles = Object.fromEntries(teamTree(tm)[0].children.map((n) => [n.name, n.role]));
  assert.deepEqual(roles, { "ah2-ux-engineer-2": "ux-engineer", "ah2-helper": "" });
});

test("a row's glyph, mark and role hue for each state are the ones the Graph's card gives", () => {
  for (const light of [false, true]) {
    for (const [state, running] of [["working", false], ["running", true], ["blocked", false], ["idle", false], ["gone", false], ["unknown", false]]) {
      for (const role of ["implementor", "reviewer", "orchestrator", ""]) {
        const s = { role, state, running };
        const card = cardStyle(s, isActive(s), light, "#808080");
        const { glyph, mark } = rowMark(s, light);
        assert.deepEqual({ glyph, mark }, { glyph: card.glyph, mark: card.mark }, `${state} ${role} ${light}`);
        // the role hue is the active card's name colour, whatever the row's state
        assert.equal(rowMark(s, light).hue, cardStyle({ role, state: "working" }, true, light, "#808080").name);
      }
    }
  }
  // negative: a reviewer at work is the review mark, not the working one
  assert.notEqual(rowMark({ role: "reviewer", state: "working" }, false).mark, rowMark({ role: "implementor", state: "working" }, false).mark);
});

// ah2-220: a lane's orchestrator reads Sub-Orchestrator in the tree; the top team's reads as before; the key and name stay orchestrator.
test("a lane's orchestrator row is labelled Sub-Orchestrator, the top one and ordinary members are untouched, the key still matches", () => {
  const slices = slicesOf(
    summary("ah2", { "d1/ah2/@orchestrator": live("working"), "d1/ah2/@implementor": live("working") }),
    summary("lane-herdr", { "d1/lane-herdr/@orchestrator": live("idle") }, "ah2"),
  );
  const [tm] = sidebarModel(slices, NOW).teams;
  const [root] = teamTree(tm);
  const lane = root.children.find((n) => n.kind === "lane").children[0];
  assert.deepEqual([root.label, root.title, root.role], ["orchestrator", "orchestrator", "orchestrator"]);
  assert.deepEqual([lane.label, lane.title, lane.role], ["Sub-Orchestrator", "Sub-Orchestrator of lane-herdr", "sub-orchestrator"]);
  assert.equal(root.children[0].label, "implementor");
  // display only: the name and key a lookup uses keep orchestrator
  assert.deepEqual([lane.name, lane.key], ["orchestrator", "d1/lane-herdr/@orchestrator"]);
  assert.equal(tm.lanes[0].members.find((m) => m.name === "orchestrator")?.key, lane.key);
});

// ah2-239: a tree row draws a moving dot exactly when the Graph draws one ending at its member (model.js inFlightTo over select.js sparksOf).
test("a brief in flight to a member gives its row a dot; an answered one, a subagent row and the depth-0 orchestrator none", async () => {
  const { inFlightTo, rowDot, rowTrack } = await import("../../herdr-desktop/mods/ah2/model.js");
  const open = (list) => [`list:open:team:d1/ah2`, { id: "list:open:team:d1/ah2", body: { open: list } }];
  const brief = (id, to) => ({ id, from_member: "d1/ah2/@orchestrator", to_member: `d1/ah2/@${to}` });
  const team = summary("ah2", { "d1/ah2/@orchestrator": live("working"), "d1/ah2/@implementor": live("working"), "d1/ah2/@reviewer-2": { ...live("idle"), role: "reviewer" } });
  const subagents = ["subagents:d1/ah2", { id: "subagents:d1/ah2", body: { rows: [{ agent_id: "a1", owner: "implementor", agent_type: "task-runner", started_at: "2026-11-18T11:59:00Z" }] } }];
  const rowsOf = (slices) => {
    const [tm] = sidebarModel(slices, NOW).teams;
    const inFlight = inFlightTo(slices);
    return treeRows(teamTree(tm), () => true).map((r) => [r.node.kind, r.node.name ?? r.node.kind, rowDot(r, inFlight)?.key ?? null]);
  };
  const flying = rowsOf(slicesOf(team, open([brief("x1", "implementor"), brief("x2", "orchestrator")]), subagents));
  assert.deepEqual(flying.filter(([, , k]) => k), [["member", "implementor", "x1"]]); // the depth-0 orchestrator has no connector
  assert.ok(flying.some(([kind, , k]) => kind === "subagent" && k === null), "the subagent row is drawn, with no dot");
  // answered: it leaves the open list, and the dot goes with it
  assert.deepEqual(rowsOf(slicesOf(team, open([]), subagents)).filter(([, , k]) => k), []);
  // the track runs down the row's own elbow and across to its icon (a leaf at depth 1: no rails)
  assert.deepEqual(rowTrack({ rails: [], kids: false }, 16), [["M", 7.5, 0], ["L", 7.5, 14.5], ["L", 30, 14.5]]);
});

// ah2-252: a dot frame reuses the tree model (model.js paneTree) and moves only the dot; a changed input builds it again.
test("renders with the same inputs and a later now build the tree model once and move the dot; a changed input builds it again", async () => {
  const { paneTree, rowTrack } = await import("../../herdr-desktop/mods/ah2/model.js");
  const { dotAt, trackOf } = await import("../../herdr-desktop/mods/ah2/graph-model.js");
  const open = ["list:open:team:d1/ah2", { id: "list:open:team:d1/ah2", body: { open: [{ id: "x1", from_member: "d1/ah2/@orchestrator", to_member: "d1/ah2/@implementor" }] } }];
  const slices = slicesOf(summary("ah2", { "d1/ah2/@orchestrator": live("working"), "d1/ah2/@implementor": live("working") }), open);
  const fns = { shown: (l) => l, open: () => () => true, track: (r) => trackOf(rowTrack(r, 16)) };
  const memo = {};
  const key = (gen, age = 0, folds = "{}") => [gen, age, folds, "nested", ""];
  // one second of the dot clock's 33 ms frames: before ah2-252 each one built the model (30 builds a second); now one does
  const at = [];
  for (let f = 0; f < 30; f++) {
    const { trees } = paneTree(memo, key(1), slices, NOW + f * 33, fns);
    const row = trees[0].rows.find((x) => x.d);
    at.push(dotAt(row.pts, NOW, NOW + f * 33, row.d.review));
  }
  assert.equal(memo.builds, 1);
  assert.notDeepEqual(at[0], at[15], "the dot moves between frames");
  // negative: a poll (the generation), the age clock and a fold each build it again
  paneTree(memo, key(2), slices, NOW, fns);
  paneTree(memo, key(2, 1), slices, NOW, fns);
  paneTree(memo, key(2, 1, '{"x":false}'), slices, NOW, fns);
  assert.equal(memo.builds, 4);
});

// ah2-265: a report filed and not yet read (the topology's edges.report) runs back from its member's row; each icon takes its Flow lane's
// colour (graph-model.js laneHue over the served column) whatever the member's state.
test("an unread report gives its member's row a dot back to its briefer and a read one none; icons take their lane's colour in every state", async () => {
  const { paneTree, rowTrack } = await import("../../herdr-desktop/mods/ah2/model.js");
  const { laneHue, laneStyle, trackOf, WEB } = await import("../../herdr-desktop/mods/ah2/graph-model.js");
  const topo = (report) => ["topology:d1/ah2", { id: "topology:d1/ah2", body: { edges: { brief: [], report } } }];
  const col = { reviewer: 3, implementor: 2, qa: 3, "ux-engineer": 2, orchestrator: 0 };
  const states = (state) => Object.fromEntries(Object.entries(col).map(([r, c]) => [`d1/ah2/@${r}`, { ...live(state), role: r, column: c }]));
  const fns = { shown: (l) => l, open: () => () => true, track: (r, back) => trackOf(rowTrack(r, 16), back) };
  const rowsOf = (slices) => paneTree({}, [1], slices, NOW, fns).trees[0].rows;
  const report = { id: "r1", from_member: "d1/ah2/@orchestrator", to_member: "d1/ah2/@reviewer" };
  const waiting = rowsOf(slicesOf(summary("ah2", states("idle")), topo([report, { id: "r2", from_member: null, to_member: "d1/ah2/@qa" }])));
  assert.deepEqual(waiting.filter((x) => x.b).map((x) => [x.r.node.name, x.b.key]), [["reviewer", "back|r1"]]); // a null party draws none
  const row = waiting.find((x) => x.b);
  assert.deepEqual(row.bpts[0], { ...trackOf(rowTrack(row.r, 16)).at(-1), d: 0 }, "the report starts at the member's icon end of the track");
  // read: it leaves edges.report, and the dot goes with it
  assert.deepEqual(rowsOf(slicesOf(summary("ah2", states("idle")), topo([]))).filter((x) => x.b), []);
  // the icon colour of a reviewer, an implementor and a QA member is their lane's (Review, Implement, Test) in every state and theme
  for (const state of ["working", "idle", "blocked", "gone"]) {
    const nodes = rowsOf(slicesOf(summary("ah2", states(state)), topo([]))).map((x) => x.r.node);
    for (const light of [false, true]) {
      const hue = (name) => { const n = nodes.find((x) => x.name === name); return laneHue(n.role, n.column, light); };
      assert.equal(hue("reviewer"), laneStyle(3, light).color);
      assert.equal(hue("implementor"), laneStyle(2, light).color);
      assert.equal(hue("qa"), laneStyle(4, light).color);
      assert.equal(hue("ux-engineer"), laneStyle(2, light).color, "a role in the table takes its lane's, not its own hue");
      assert.equal(hue("orchestrator"), null, "the top orchestrator keeps the ink");
    }
  }
  // negative: a role not in the table, or no column served (an older ahd), keeps its role hue
  assert.equal(laneHue("custom-role", 2, false), WEB.dark.role.neutral);
  assert.equal(laneHue("reviewer", null, false), WEB.dark.role.reviewer);
});
