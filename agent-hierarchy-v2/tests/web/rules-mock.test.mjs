// The mock of /ui/v1/rules gives a dialog what the daemon's route gives it (rules-of-engagement §6, P4).
import { test } from "node:test";
import assert from "node:assert/strict";
import { createRules } from "../../web/mock/rules.mjs";

const TEAM = "mock-team";
const write = (rules, body) => rules.write({ team: TEAM, ...body });
const keys = (list) => list.rules.map((r) => r.key);

test("a read has the rows, the answer at each level and the open items", () => {
  const [status, body] = createRules().read(TEAM);
  assert.equal(status, 200);
  assert.equal(body.rows.length, 3);
  assert.deepEqual(keys(body.resolved.repo), ["ship-small"]);
  assert.deepEqual(keys(body.resolved.team).sort(), ["no-new-deps", "ship-small"]);
  const goal = body.resolved.items["mock-12"].rules.find((r) => r.key === "ship-small");
  assert.equal(goal.level, "goal");
  assert.deepEqual(goal.overrides, ["repo"]);
  assert.deepEqual(body.items.map((i) => i.kind), ["project", "goal"]);
});

test("a read with no team or an unknown one is refused", () => {
  assert.equal(createRules().read("")[0], 400);
  assert.equal(createRules().read("ghost")[0], 400);
});

test("add, override, switch off and remove move the resolved list", () => {
  const rules = createRules();
  assert.equal(write(rules, { op: "add", level: "repo", key: "new-rule", text: "Be kind." })[0], 200);
  assert.equal(write(rules, { op: "add", level: "team", key: "new-rule", text: "Be kinder." })[1].rule.scope, TEAM);
  assert.equal(rules.read(TEAM)[1].resolved.team.rules.find((r) => r.key === "new-rule").text, "Be kinder.");

  assert.equal(write(rules, { op: "off", level: "team", key: "new-rule" })[0], 200);
  assert.deepEqual(rules.read(TEAM)[1].resolved.team.off.map((o) => o.key), ["new-rule"]);

  assert.equal(write(rules, { op: "remove", level: "team", key: "new-rule" })[0], 200);
  assert.equal(rules.read(TEAM)[1].resolved.team.rules.find((r) => r.key === "new-rule").text, "Be kind.");
});

test("edit clears off", () => {
  const rules = createRules();
  write(rules, { op: "off", level: "repo", key: "ship-small" });
  const [status, body] = write(rules, { op: "edit", level: "repo", key: "ship-small", text: "Back on." });
  assert.equal(status, 200);
  assert.equal(body.rule.off, false);
});

test("refusals carry the verbs' codes", () => {
  const rules = createRules();
  const cases = [
    [{ op: "add", level: "repo", key: "ship-small", text: "x" }, 409, 3],
    [{ op: "add", level: "repo", key: "BAD KEY", text: "x" }, 400, 2],
    [{ op: "add", level: "repo", key: "k", text: " " }, 400, 2],
    [{ op: "add", level: "repo", key: "k", text: "x".repeat(301) }, 400, 2],
    [{ op: "edit", level: "repo", key: "nope", text: "x" }, 404, 1],
    [{ op: "remove", level: "repo", key: "nope" }, 404, 1],
    [{ op: "off", level: "team", key: "nope" }, 404, 1],
    [{ op: "add", level: "goal", scope: "mock-3", key: "k", text: "x" }, 400, 2],
    [{ op: "add", level: "goal", scope: "mock-99", key: "k", text: "x" }, 404, 1],
    [{ op: "add", level: "nonsense", key: "k", text: "x" }, 400, 2],
    [{ op: "nonsense", level: "repo", key: "k", text: "x" }, 400, 2],
  ];
  for (const [body, status, code] of cases) {
    const [gotStatus, gotBody] = write(rules, body);
    assert.deepEqual([gotStatus, gotBody.code], [status, code], JSON.stringify(body));
  }
  assert.equal(rules.write({ op: "add", team: "ghost", level: "repo", key: "k", text: "x" })[0], 400);
  assert.equal(rules.read(TEAM)[1].rows.length, 3);
});

test("text of exactly the limit is taken", () => {
  assert.equal(write(createRules(), { op: "add", level: "repo", key: "k", text: "x".repeat(300) })[0], 200);
});
