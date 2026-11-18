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
