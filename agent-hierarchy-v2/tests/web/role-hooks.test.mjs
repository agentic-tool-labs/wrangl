import { test } from "node:test";
import assert from "node:assert/strict";
import { settle } from "./dom.mjs";
import { mountApp as mount, until, find, text, member, DIR } from "./mount-app.mjs";
import * as G from "../../web/ui/graph.js";

// The role and state hooks the colour rules select on: every graph node, tree member row and rail row carries its member's role and state.

const roleOf = (m, id) => m.store.state.slices.get(id)?.body.role || undefined;

// The hooks are Flow's cards'; Map's leaves carry state in a st- class and the role in their label.
const flow = async (m) => {
  m.store.setUi({ graphMode: "flow" });
  await until(() => find(m.root, "div.fcard").length > 1, "the nodes");
  await settle();
};

test("a graph card carries its member's role and state", async () => {
  const m = await mount();
  await flow(m);
  const nodes = find(m.root, "div.fcard");
  for (const node of nodes) {
    assert.equal(node.attrs.get("data-role"), roleOf(m, node.attrs.get("data-id")), `role of ${node.attrs.get("data-id")}`);
    assert.match(node.attrs.get("data-state"), /^(working|idle|blocked|gone)$/);
  }
  assert.ok(nodes.some((n) => n.attrs.get("data-role") !== undefined), "the mock has at least one member with a role");
});

test("a tree member row carries role and state, and a worktree row carries neither", async () => {
  const m = await mount();
  m.store.setUi({ view: "overview" });
  const rows = (kind) => find(m.root, kind === "k-member" ? "li.ov-member" : "div.ln").filter((r) => kind === "k-member" || r.attrs.get("class").split(" ").includes(kind));
  await until(() => rows("k-member").length > 0, "the member rows");
  for (const row of rows("k-member")) {
    assert.match(row.attrs.get("data-state"), /^(working|idle|blocked|gone)$/);
    assert.equal(row.attrs.get("data-role") !== undefined, true);
  }
  assert.ok(rows("k-worktree").length > 0);
  for (const row of rows("k-worktree")) {
    assert.equal(row.attrs.get("data-role"), undefined);
    assert.equal(row.attrs.get("data-state"), undefined);
  }
});

test("a sub-orchestrator member is a graph card of its own role, named as itself, in the Orchestrator's column", async () => {
  const m = await mount();
  await flow(m);
  const id = member("sub-orchestrator");
  const team = m.store.state.slices.get(`team:${DIR}/ship`);
  m.model.publish([
    { id, as_of: Date.now(), body: { id, name: "sub-orchestrator", role: "sub-orchestrator", session: "sess-sub", activity: `activity:${DIR}/sess-sub`, liveness: {}, placement: { stream: null }, last_exchanges: [] } },
    { id: team.id, as_of: Date.now(), body: { ...team.body, members: [id, ...team.body.members] } },
  ]);
  await until(() => find(m.root, "div.fcard").some((n) => n.attrs.get("data-id") === id), "the sub-orchestrator node");
  const node = find(m.root, "div.fcard").find((n) => n.attrs.get("data-id") === id);
  assert.equal(node.attrs.get("data-role"), "sub-orchestrator");
  assert.match(text(node), /sub-orchestrator/);
});
