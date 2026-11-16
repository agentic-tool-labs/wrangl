import { test } from "node:test";
import assert from "node:assert/strict";
import { installDom } from "./dom.mjs";
import { createMock } from "../../web/mock/model.mjs";

installDom();
globalThis.requestAnimationFrame = () => 0;
const { flattenTree, workRoots, rowKey } = await import("../../web/ui/treeview.js");
const { firstTeam } = await import("../../web/ui/select.js");

const model = createMock();
const load = () => new Map(["tree", "members", "exchanges", "processes", "summary"].flatMap((v) => model.manifest(v).slices).map((s) => [s.id, s]));
const find = (root, sel) => root.all(sel);

test("the tree is team, worktrees grouped by work root, members, then the open briefs addressed to each member", () => {
  const slices = load();
  const rows = flattenTree(slices, firstTeam(slices));
  assert.equal(rows[0].kind, "team");
  assert.match(rows[0].detail, /^8 members · \d worktrees$/);
  const kinds = rows.map((r) => r.kind);
  assert.ok(kinds.indexOf("worktree") > 0);
  const design = rows.findIndex((r) => r.kind === "worktree" && r.label === "design");
  assert.deepEqual(rows.slice(design + 1, design + 3).map((r) => r.label), ["architect", "reviewer"], "architect and reviewer share one worktree; the brief the architect SENT is not listed under it");
  const impl1 = rows.findIndex((r) => r.kind === "member" && r.label === "implementor-1");
  assert.equal(rows[impl1].count, 1);
  assert.deepEqual([rows[impl1 + 1].kind, rows[impl1 + 1].label], ["brief", "ui-r6a"], "the brief addressed to implementor-1, though it is older than its last 3 exchanges");
  assert.equal(rows.find((r) => r.kind === "member" && r.label === "orchestrator").count, 0, "the orchestrator sent briefs; none is addressed to it");
  const briefs = rows.filter((r) => r.kind === "brief").map((r) => r.label);
  assert.deepEqual(briefs.sort(), ["run-cli-tests", "ui-r2-seed", "ui-r6a"], "only open briefs, each under the member it is addressed to; the one to the gone member is stale");
});

test("the team row counts the same worktrees as the header, though a worktree-less group is also listed", () => {
  const slices = load();
  const team = firstTeam(slices);
  const rows = flattenTree(slices, team);
  const n = workRoots(slices, team).size;
  assert.match(rows[0].detail, new RegExp(`· ${n} worktrees$`));
  assert.equal(rows.filter((r) => r.kind === "worktree").length, n + 1, "the group of members without a worktree is a group, not a worktree");
});

test("the worktree count is derived from the data: another work root makes it one more", () => {
  const slices = load();
  const team = firstTeam(slices);
  const n = workRoots(slices, team).size;
  assert.equal(workRoots(slices, team).size, n);
  const more = new Map(slices);
  more.set("worktree:a1b2c3d4/ship/extra", { id: "worktree:a1b2c3d4/ship/extra", body: { id: "x", work_root: "/elsewhere/new", stream: "b" } });
  assert.equal(workRoots(more, team).size, n + 1);
  const same = new Map(slices);
  same.set("worktree:a1b2c3d4/ship/dup", { id: "worktree:a1b2c3d4/ship/dup", body: { id: "x", work_root: [...workRoots(slices, team)][0], stream: "b" } });
  assert.equal(workRoots(same, team).size, n, "a second worktree slice on an existing root adds none");
  const other = new Map(slices);
  other.set("worktree:zzzz/other/m", { id: "worktree:zzzz/other/m", body: { id: "x", work_root: "/another/team", stream: "b" } });
  assert.equal(workRoots(other, team).size, n, "another team's worktree is not this team's");
  assert.match(flattenTree(more, team)[0].detail, new RegExp(`${n + 1} worktrees`));
});

test("with no worktree slices the members group by their served work root, so the count and the tree agree", () => {
  const slices = new Map([...load()].filter(([id]) => !id.startsWith("worktree:")));
  const team = firstTeam(slices);
  const sumId = `team-summary:${team.body.dir_id}/${team.body.name}`;
  const ids = team.body.members.slice(0, 2);
  const states = Object.fromEntries(ids.map((id, i) => [id, { state: "idle", work_root: `/w/tree${i}` }]));
  slices.set(sumId, { id: sumId, body: { ...slices.get(sumId)?.body, worktrees: 2, member_states: states } });
  const rows = flattenTree(slices, team);
  assert.match(rows[0].detail, /· 2 worktrees$/);
  assert.deepEqual(rows.filter((r) => r.kind === "worktree").map((r) => r.label).filter((l) => l.startsWith("tree")).sort(), ["tree0", "tree1"]);
  // negative: a member with no served root stays in the group without one
  assert.ok(rows.some((r) => r.kind === "worktree" && r.label === "(no worktree)"));
});

test("collapsing a row hides exactly its children", () => {
  const slices = load();
  const team = firstTeam(slices);
  const all = flattenTree(slices, team);
  const wt = all.find((r) => r.kind === "worktree" && r.label === "design");
  const cut = flattenTree(slices, team, new Set([wt.key]));
  assert.equal(all.length - cut.length, 2, "the two members of the design worktree");
  assert.equal(cut.find((r) => r.key === wt.key).open, false);
  assert.equal(flattenTree(slices, team, new Set([team.id])).length, 1, "a collapsed team is one row");
  const m = all.find((r) => r.kind === "member" && r.label === "implementor-1");
  assert.equal(all.length - flattenTree(slices, team, new Set([m.key])).length, 1, "its one open brief");
});

test("a row is keyed by its own key, the same on every call", () => {
  const row = { key: "m:member:d/t/a", kind: "member" };
  assert.equal(rowKey(row), "m:member:d/t/a");
  assert.equal(rowKey(row), rowKey(row));
});

test("every row has a unique stable key (not an index)", () => {
  const slices = load();
  const rows = flattenTree(slices, firstTeam(slices));
  assert.equal(new Set(rows.map((r) => r.key)).size, rows.length);
  assert.ok(rows.every((r) => typeof r.key === "string" && !/^\d+$/.test(r.key)));
  assert.deepEqual(rows.map((r) => r.key), flattenTree(slices, firstTeam(slices)).map((r) => r.key), "the same data gives the same keys");
});
