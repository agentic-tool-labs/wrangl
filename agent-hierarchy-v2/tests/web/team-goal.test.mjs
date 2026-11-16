// ah2-53 sitting 3: the team goal on the web dashboard. Home's row (one muted clipped line, own goal only), the team's Overview and its
// Sitrep (a "Goal" / "Done when" block; a lane's inherited goal says whose), all as inert text. On the mock `lanes` scenario.
import { test } from "node:test";
import assert from "node:assert/strict";

process.env.AH_MOCK_FIXTURE = "lanes";
const { mountApp, until, find, text, DIR } = await import("./mount-app.mjs");
const { homeRows } = await import("../../web/ui/teams.js");
const { goalOf, summaryRow } = await import("../../web/ui/select.js");

const summary = (name, body = {}) => ({ id: `team-summary:${DIR}/${name}`, as_of: Date.now(), body: { team: `team:${DIR}/${name}`, name, running: true, members: 1, working: 1, blocked: 0, needs_you: 0, alerts: [], member_states: {}, updated_at: new Date().toISOString(), ...body } });
const slicesOf = (...ss) => new Map(ss.map((s) => [s.id, s]));
// An element as markup, attributes in order, so two renders can be compared whole.
const markup = (n) => (n.nodeType === 3 ? n.data : `<${n.localName}${[...n.attrs].map(([k, v]) => ` ${k}="${v}"`).join("")}>${n.childNodes.map(markup).join("")}</${n.localName}>`);
const rowOf = (root, name) => find(root, "tr").find((r) => r.attrs.has("data-row") && !r.attrs.has("data-lane") && find(r, "a.name").some((a) => text(a) === name));
const laneRows = (root) => find(root, "tr").filter((r) => r.attrs.has("data-lane"));
const laneOfShip = (store) => [...store.state.slices.values()].find((s) => s.id.startsWith("team-summary:") && s.body.lane?.parent_team === "ship");

test("the model: a team's own goal is its row's; a lane's inherited goal is not; no goal is null", () => {
  const rows = homeRows(slicesOf(
    summary("mgr", { goal: { text: "ship the row", done_when: "all green", team: "mgr" } }),
    summary("mgr-a", { lane: { parent_team: "mgr", stream: "a" }, goal: { text: "ship the row", done_when: "all green", team: "mgr" } }),
    summary("mgr-b", { lane: { parent_team: "mgr", stream: "b" }, goal: { text: "lane aim", done_when: null, team: "mgr-b" } }),
    summary("plain"),
  )).rows;
  const mgr = rows.find((r) => r.name === "mgr");
  assert.deepEqual(mgr.goal, { text: "ship the row", doneWhen: "all green", team: "mgr", full: "ship the row — done when: all green" });
  assert.equal(mgr.lanes.find((l) => l.raw === "a").goal, null);
  assert.equal(mgr.lanes.find((l) => l.raw === "b").goal.text, "lane aim");
  assert.equal(rows.find((r) => r.name === "plain").goal, null);
});

test("goal text is inert: escapes, controls and bidi overrides go, a newline is a space, markup characters stay text", () => {
  const g = goalOf({ name: "t", goal: { text: "ship\nit\x1b[31m red\x07‮<b>x</b>", done_when: "\x07", team: "t" } });
  assert.deepEqual([g.text, g.doneWhen, g.full], ["ship it red<b>x</b>", null, "ship it red<b>x</b>"]);
  assert.equal(goalOf({ name: "t", goal: { text: "\x1b[0m", team: "t" } }), null, "a goal that is only control characters is no goal");
  assert.equal(summaryRow(summary("t")).goal, null);
});

test("Home: a goal is one muted line under the name, its whole text the tooltip and read out; markup characters are shown as typed", async () => {
  const m = await mountApp({ view: null });
  await until(() => rowOf(m.root, "ship"), "the ship row");
  m.model.publish([summary("aimed", { goal: { text: "Land <b>rc4</b> & tag it", done_when: "main is green", team: "aimed" } })]);
  await until(() => rowOf(m.root, "aimed"), "the aimed row");
  const row = rowOf(m.root, "aimed");
  const [line] = find(row, ".t-goal");
  assert.ok(line, "the goal line");
  assert.equal(line.getAttribute("class"), "t-sub t-goal");
  assert.equal(line.getAttribute("title"), "Land <b>rc4</b> & tag it — done when: main is green");
  assert.equal(text(line), "Goal: Land <b>rc4</b> & tag it — done when: main is green");
  assert.equal(find(line, "b").length, 0, "no element made from the text");
  assert.equal(row.getAttribute("aria-label"), "aimed, Working, goal: Land <b>rc4</b> & tag it");
  // the line sits right under the team name
  const cell = find(row, "td")[0];
  assert.equal(cell.children[1], line);
});

test("Home: a team without a goal draws exactly the markup it drew before one was set", async () => {
  const m = await mountApp({ view: null });
  m.model.publish([summary("plain")]);
  await until(() => rowOf(m.root, "plain"), "the plain row");
  const before = markup(rowOf(m.root, "plain"));
  assert.equal(find(rowOf(m.root, "plain"), ".t-goal").length, 0);
  assert.equal(rowOf(m.root, "plain").getAttribute("aria-label"), "plain, Working");
  m.model.publish([summary("plain", { goal: { text: "an aim", done_when: null, team: "plain" } })]);
  await until(() => find(rowOf(m.root, "plain"), ".t-goal").length === 1, "the goal line, live");
  m.model.publish([summary("plain")]);
  await until(() => find(rowOf(m.root, "plain"), ".t-goal").length === 0, "the goal line gone, live");
  assert.equal(markup(rowOf(m.root, "plain")), before);
});

test("Home: a lane's row shows only its own goal, never its parent's", async () => {
  const m = await mountApp({ view: null });
  await until(() => laneRows(m.root).length === 2 && laneOfShip(m.store), "the lane rows");
  const lane = laneOfShip(m.store);
  m.model.publish([{ ...lane, as_of: Date.now(), body: { ...lane.body, goal: { text: "the parent's aim", done_when: null, team: "ship" } } }]);
  await until(() => m.store.state.slices.get(lane.id).body.goal, "the inherited goal held");
  assert.equal(find(m.root, ".t-goal").length, 0, "the inherited goal is on no lane row");
  m.model.publish([{ ...lane, as_of: Date.now(), body: { ...lane.body, goal: { text: "the lane's own aim", done_when: null, team: lane.body.name } } }]);
  await until(() => laneRows(m.root).some((r) => find(r, ".t-goal").length === 1), "the lane's own goal line");
  const row = laneRows(m.root).find((r) => find(r, ".t-goal").length === 1);
  assert.equal(text(find(row, ".t-goal")[0]), "Goal: the lane's own aim");
  assert.match(row.getAttribute("aria-label"), /, goal: the lane's own aim$/);
});

const goalBlock = (root) => find(root, "section").find((s) => (s.getAttribute("class") ?? "").split(" ").includes("team-goal")) ?? null;
const pairs = (block) => find(block, "div").map((d) => [text(find(d, "dt")[0]), text(find(d, "dd")[0])]);

test("Overview: a lane's page shows its inherited goal, labelled whose, with its done-when; markup characters stay text", async () => {
  const m = await mountApp({ view: null });
  await until(() => laneOfShip(m.store), "the lane's summary");
  const lane = laneOfShip(m.store);
  m.store.setUi({ view: "overview", team: lane.body.team });
  await until(() => find(m.root, "li.ov-member").length > 0, "the lane's Overview");
  assert.equal(goalBlock(m.root), null, "no goal, no block");
  m.model.publish([{ ...lane, as_of: Date.now(), body: { ...lane.body, goal: { text: "Ship <i>it</i>", done_when: "tests pass", team: "ship" } } }]);
  await until(() => goalBlock(m.root), "the goal block, live");
  const block = goalBlock(m.root);
  assert.equal(block.getAttribute("class"), "team-goal ov-goal");
  assert.equal(block.getAttribute("aria-label"), "Team goal");
  assert.deepEqual(pairs(block), [["Goal from team ship", "Ship <i>it</i>"], ["Done when", "tests pass"]]);
  assert.equal(find(block, "i").length, 0);
  m.model.publish([{ ...lane, as_of: Date.now(), body: { ...lane.body, goal: { text: "own aim", done_when: null, team: lane.body.name } } }]);
  await until(() => pairs(goalBlock(m.root))[0]?.[1] === "own aim", "the lane's own goal");
  assert.deepEqual(pairs(goalBlock(m.root)), [["Goal", "own aim"]], "its own goal has no 'from' and no done-when row");
});

test("Sitrep: a top-level team's goal is under the dialog's status line", async () => {
  const m = await mountApp({ view: null });
  m.model.publish([summary("aimed", { goal: { text: "one aim", done_when: "done", team: "aimed" } })]);
  await until(() => m.store.state.slices.has(`team-summary:${DIR}/aimed`), "the summary");
  m.store.setUi({ view: "overview", team: `team:${DIR}/aimed`, sitrep: true });
  await until(() => goalBlock(m.root), "the Sitrep's goal block");
  const block = goalBlock(m.root);
  assert.equal(block.getAttribute("class"), "team-goal sr-goal");
  assert.deepEqual(pairs(block), [["Goal", "one aim"], ["Done when", "done"]]);
});

test("a goal whose team is not a string is read as the team's own, never handed on as an object", () => {
  const g = goalOf({ name: "ship", goal: { text: "Ship it", team: { not: "a name" } } });
  assert.equal(g.team, "ship");
  assert.equal(goalOf({ name: "ship", goal: { text: "Ship it", team: "par‮ent" } }).team, "parent");
});
