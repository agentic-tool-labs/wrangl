import { test } from "node:test";
import assert from "node:assert/strict";
import { mountApp, until, find, text, member } from "./mount-app.mjs";

// The status bar carries the counts the operator watches; the header no longer does.

// Keyed by the state's class; the word beside each count is checked on its own (ah-ui-ia §7: "working", "no signal").
const states = (root) => Object.fromEntries(find(root, ".st").map((el) => [el.getAttribute("class").split(/\s+/)[1], Number(text(el).trim().split(/\s+/)[0])]));
const words = (root) => find(root, ".st").map((el) => text(el).trim().replace(/^\d+\s*/, ""));

test("the status bar counts the team's members by state, and Needs you and In flight; the header carries no counts", async () => {
  const m = await mountApp({ view: "overview" });
  await until(() => find(m.root, ".st").length === 4, "the four states");
  assert.equal(find(m.root, "header.shell-head")[0].all(".counts").length, 0);
  assert.match(text(find(m.root, "footer.shell-foot")[0].all(".counts")[0]), /Needs you\s*\d+(\s*· \d+ blocked)?\s*\d+ briefs? open/);
  const before = states(m.root);
  assert.deepEqual(Object.keys(before), ["busy", "blocked", "idle", "unknown"]);
  assert.deepEqual(words(m.root), ["working", "blocked", "idle", "no signal"]);
  assert.ok(before.busy > 0, "the fixture has busy members");

  const busy = ["architect", "implementor-1", "implementor-2", "reviewer", "task-runner", "docs-writer", "ultra-advisor"]
    .map((n) => m.model.slice(member(n))?.body.activity)
    .find((id) => id && m.model.slice(id)?.body.activity === "working");
  const act = m.model.slice(busy);
  m.model.publish([{ id: busy, as_of: Date.now(), body: { ...act.body, activity: "blocked" } }]);
  await until(() => states(m.root).blocked === before.blocked + 1, "the new blocked member counted");
  assert.equal(states(m.root).busy, before.busy - 1);
  assert.match(find(m.root, ".st").find((el) => /\bblocked\b/.test(el.getAttribute("class"))).getAttribute("class"), /\battn\b/, "a blocked count above zero is marked");
});

test("on the Teams view the status bar has no member states: it counts teams", async () => {
  const m = await mountApp({ view: "teams" });
  await until(() => /\d+ teams?/.test(text(find(m.root, "footer.shell-foot")[0])), "the team count");
  assert.equal(find(m.root, ".st").length, 0);
});

test("a view that holds no member slices shows no states, never four zeros", async () => {
  const m = await mountApp({ view: "plans" });
  await until(() => /Needs you/.test(text(find(m.root, "footer.shell-foot")[0])), "the status bar");
  assert.equal(find(m.root, ".st").length, 0);
});
