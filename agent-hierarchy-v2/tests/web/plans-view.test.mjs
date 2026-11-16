import { test, afterEach } from "node:test";
import assert from "node:assert/strict";
import { settle } from "./dom.mjs";
import { mountApp as mount, until, find, text, DIR } from "./mount-app.mjs";

const model = await import("../../web/ui/plans-model.js");
const { streamOf, fullTree, groupsOf, teamModel, readPref, writePref, roleOf, itemsId, DAY_MS } = model;
const { servedStream, servedItems, servedBacklog } = await import("../../web/mock/model.mjs");
const { globalKey } = await import("../../web/ui/keys.js");
const { hashOf, uiOf } = await import("../../web/ui/deeplink.js");

const HOUR = 3_600_000;
const NOW = 1_800_000_000_000;
const ROOT = "/work/ah/.claude/worktrees/main";

// ---- the model

const TEAM = { id: "team:d/t", name: "t", dirId: "d", repo: "r", branch: "b" };
const OMITTED = { stream: "s", owner: "implementor-1", branch: "x", work_tree: "wt" };
const EXPLICIT = { ...OMITTED, goal: "", state: "open", source: "checklist", counts: { done: 0, in_progress: 0, not_started: 0, next: 0, blocked: 0, waiting_user: 0, dropped: 0 }, as_of: 0, blockers: 0, warnings: 0, truncated: false, review_open: false };

test("a stream the mock serves spells out every default, as the daemon does", () => {
  assert.deepEqual(streamOf(servedStream(OMITTED), { work_root: "/w" }, TEAM), streamOf(servedStream(EXPLICIT), { work_root: "/w" }, TEAM));
  const s = streamOf(servedStream(OMITTED), { work_root: "/w" }, TEAM);
  assert.equal(s.review, false);
  assert.equal(s.truncated, false);
  assert.equal(s.blockers, 0);
  assert.equal(s.total, 0);
});

test("the work tree joins its work root: relative under it, '.' for the root, absolute and rootless kept", () => {
  assert.equal(fullTree("/w/main", "docs"), "/w/main/docs");
  assert.equal(fullTree("/w/main/", "docs/x"), "/w/main/docs/x");
  assert.equal(fullTree("/w/main", "."), "/w/main");
  assert.equal(fullTree("/w/main", "/elsewhere/tree"), "/elsewhere/tree");
  assert.equal(fullTree(null, "docs"), "docs");
  assert.equal(fullTree("/w/main", undefined), null);
  assert.equal(fullTree("/w/main", ""), null);
});

test("an owner reads as its role by name: implementor-2 is an implementor, a name that is no role's has none", () => {
  assert.equal(roleOf("implementor-2"), "implementor");
  assert.equal(roleOf("reviewer"), "reviewer");
  assert.equal(roleOf("implementors"), "");
  assert.equal(roleOf(""), "");
});

const row = (over = {}) => ({ id: "team:d/t", name: "t", dirId: "d", repo: "r", branch: "b", members: 2, running: true, needs: 0, updated: NOW, streamsOpen: 1, itemsDone: 0, itemsTotal: 0, status: "live", quietAt: NOW + DAY_MS, ...over });

test("a team reads the served status; only the served quiet instant flips a live team on the clock", () => {
  const model = (over) => teamModel(row(over), null, NOW);
  assert.equal(model({ status: "ended" }).status, "ended");
  assert.equal(model({ status: "stale" }).status, "stale");
  assert.deepEqual([model({ quietAt: NOW + 1 }).status, model({ quietAt: NOW + 1 }).quiet], ["live", false], "before the instant");
  assert.deepEqual([model({ quietAt: NOW }).status, model({ quietAt: NOW }).quiet], ["stale", true], "at the instant");
  assert.deepEqual([model({ quietAt: 0 }).status, model({ quietAt: 0 }).quiet], ["live", false], "no instant is never quiet");
  assert.equal(model({ status: "ended", quietAt: NOW - HOUR }).status, "ended", "the instant moves only a live team");
});

const teamWith = (name, streams) => teamModel(row({ id: `team:d/${name}`, name }), { work_root: ROOT, streams: streams.map((s) => servedStream(s)) }, NOW);

test("two teams in one repo are two groups under Team and carry their team name under every other grouping", () => {
  const a = teamWith("alpha", [{ stream: "main", owner: "o", branch: "b1", counts: { done: 1 }, state: "open" }]);
  const b = teamWith("beta", [{ stream: "main", owner: "o", branch: "b2", counts: { in_progress: 1 }, state: "open" }]);
  const same = { ...a, repo: "r" };
  assert.equal(same.repo, b.repo);
  assert.deepEqual(groupsOf("team", [a, b]).map((g) => g.label), ["alpha", "beta"]);
  for (const by of ["stream", "repo", "status"]) {
    const rows = groupsOf(by, [a, b]).flatMap((g) => g.rows);
    assert.deepEqual(rows.map((r) => r.team).sort(), ["alpha", "beta"], by);
  }
  assert.deepEqual(groupsOf("stream", [a, b]).map((g) => [g.label, g.rows.length]), [["main", 2]]);
  assert.deepEqual(groupsOf("repo", [a, b]).map((g) => [g.label, g.rows.length]), [["r", 2]]);
});

test("grouping by status separates blocked, waiting, in review, in progress and not started; finished streams fold", () => {
  const t = teamWith("t", [
    { stream: "b", counts: { blocked: 1 }, state: "open" },
    { stream: "w", counts: { waiting_user: 1 }, state: "open" },
    { stream: "r", counts: { in_progress: 1 }, state: "open", review_open: true },
    { stream: "p", counts: { in_progress: 1 }, state: "open" },
    { stream: "n", counts: { not_started: 2 }, state: "open" },
    { stream: "d", counts: { done: 2 }, state: "done" },
  ]);
  const groups = groupsOf("status", [t]);
  assert.deepEqual(groups.map((g) => g.label), ["Blocked", "Waiting on you", "In review", "In progress", "Not started", "Done"]);
  assert.deepEqual(groups.at(-1).done.map((s) => s.stream), ["d"]);
  assert.deepEqual(groups.at(-1).rows, [], "a done stream is folded, not listed");
});

test("the filter keeps streams by name, goal, owner or branch, and by an item title only when the items are held", () => {
  const t = teamWith("t", [
    { stream: "alpha", goal: "Ship it", owner: "implementor-1", branch: "ah/one", counts: { done: 1 } },
    { stream: "beta", goal: "", owner: "reviewer", branch: "ah/two", counts: { done: 1 } },
  ]);
  const names = (query, titlesOf) => groupsOf("team", [t], { query, titlesOf }).flatMap((g) => g.rows).map((s) => s.stream);
  assert.deepEqual(names("alp"), ["alpha"]);
  assert.deepEqual(names("ship"), ["alpha"], "the goal");
  assert.deepEqual(names("REVIEWER"), ["beta"], "the owner, in any case");
  assert.deepEqual(names("ah/two"), ["beta"], "the branch");
  assert.deepEqual(names("upgrade"), [], "an item title nobody holds matches nothing");
  assert.deepEqual(names("upgrade", (s) => (s.stream === "beta" ? ["Upgrade guide"] : null)), ["beta"]);
  assert.deepEqual(names("nothing-like-this"), []);
});

test("choices are remembered per viewer, and storage that is absent, throws or holds nonsense gives the defaults", () => {
  const kept = {};
  const store = { getItem: (k) => kept[k] ?? null, setItem: (k, v) => (kept[k] = v) };
  assert.equal(readPref("plansGroup", store), "team");
  assert.equal(writePref("plansGroup", "repo", store), true);
  assert.equal(readPref("plansGroup", store), "repo");
  kept["ah.plans.group"] = "nonsense";
  assert.equal(readPref("plansGroup", store), "team");
  assert.equal(readPref("plansGroup", { getItem: () => { throw new Error("blocked"); } }), "team");
  assert.equal(readPref("plansGroup", undefined), "team");
  assert.equal(writePref("plansGroup", "repo", { setItem: () => { throw new Error("full"); } }), false);
  assert.equal(readPref("plansLayout", store), "list");
});

test("the address of the Board: all teams is team=all, a team's carries the team", () => {
  assert.equal(hashOf({ view: "allplans" }), "#/board?team=all");
  assert.equal(uiOf("#/board?team=all").view, "allplans");
  assert.equal(hashOf({ view: "board", team: "team:d/t" }), "#/board?team=team%3Ad%2Ft");
  assert.deepEqual([uiOf("#/board?team=team%3Ad%2Ft").view, uiOf("#/board?team=team%3Ad%2Ft").team], ["board", "team:d/t"]);
  assert.equal(uiOf("#/teams/other"), null);
});

// ---- the page

let fake = null;
const useStorage = (init) => {
  fake = { data: { ...init }, getItem(k) { return this.data[k] ?? null; }, setItem(k, v) { this.data[k] = v; } };
  Object.defineProperty(globalThis, "localStorage", { value: fake, configurable: true });
};
afterEach(() => {
  delete globalThis.localStorage;
  fake = null;
});

const rows = (m) => find(m.root, "li.pl-row");
const labels = (m) => find(m.root, "button.pl-main").map((b) => b.getAttribute("aria-label"));
const planIds = (m) => [...m.store.state.slices.keys()].filter((id) => /^(plan|plan-items):/.test(id));
const subscribed = (m) => (m.log.polls.at(-1) ?? "").split(",").filter((id) => /^(plan|plan-items):/.test(id));
const pressed = (m) => find(m.root, "span.plans-group")[0].all("button").filter((b) => b.getAttribute("aria-pressed") === "true").map(text);
const click = (m, label) => find(m.root, "button").find((b) => text(b) === label).fire("click");

// A second team in the same repo as ship, as its summary slice would publish it.
const summary = (name, over = {}) => ({
  id: `team-summary:${DIR}/${name}`,
  as_of: Date.now(),
  body: { team: `team:${DIR}/${name}`, name, work_root: ROOT, branch: "ah/rel", running: true, members: 2, working: 1, blocked: 0, needs_you: 0, in_flight: 0, oldest_in_flight_at: null, updated_at: new Date().toISOString(), feed_newest_at: null, alerts: [], streams_open: 1, streams_total: 1, items_done: 1, items_total: 4, ...over },
});

const open = async (view = "allplans") => {
  useStorage({});
  const m = await mount({ view });
  if (view !== "teams") {
    await until(() => rows(m).length >= 2, "the rows of the ship plan");
  }
  return m;
};

test("All teams: the ship plan lists its streams under a team header, each row labelled team · repo · branch", async () => {
  const m = await open();
  assert.deepEqual(find(m.root, "b.pl-title").map(text), ["ship"]);
  assert.match(text(find(m.root, "div.pl-head")[0]), /live · 8 members/);
  assert.match(text(find(m.root, "div.pl-head")[0]), /main · ah\/ui-perf/);
  assert.match(text(find(m.root, "div.pl-head")[0]), /7 \/ 21/, "items done / total from the team summary");
  assert.match(text(find(m.root, "div.pl-head")[0]), /4 waiting on you/, "the open questions; a blocked member is in needs_you, not here");
  assert.deepEqual(labels(m).map((l) => l.split(", ").slice(0, 2).join(", ")), ["ah/ui-r6, ship · main · ah/ui-r6", "docs-2.5, ship · main · ah/docs"]);
  assert.equal(rows(m).length, 2, "the done stream is hidden");
  // Stream aging (§2.4): server-r5 finished before today, so it is hidden until the footer toggle shows it, folded as before.
  assert.equal(find(m.root, "li.pl-more").length, 0);
  click(m, "Show 1 older stream");
  await until(() => find(m.root, "li.pl-more").length === 1, "the done fold back");
  assert.match(text(find(m.root, "li.pl-more")[0]), /1 more stream done/);
  assert.equal(fake.data["ah.board.showFinished"], "on", "remembered, shared by Board and List");
});

test("a row shows its owner, progress chip, branch and age; a stream in review says so", async () => {
  const m = await open();
  const ui = rows(m)[0];
  assert.match(text(ui), /ah\/ui-r6/);
  assert.match(text(ui), /implementor-1/);
  assert.match(text(ui), /blocked · 3\/13/, "done of total, the dropped item not counted");
  const docs = rows(m)[1];
  assert.match(text(docs), /in review · 1\/4/);
  assert.equal(find(ui, "span.pl-owner")[0].getAttribute("data-role"), "implementor");
});

test("the detail panel names the full work tree, the counts that are not zero; the List holds every open stream's items (§2.5)", async () => {
  const m = await open();
  await until(() => planIds(m).some((id) => id.startsWith("plan-items:")), "the open streams' items held for the item rows");
  find(rows(m)[1], "button.pl-main")[0].fire("click");
  await until(() => find(m.root, "aside.pl-detail").length === 1, "the detail panel");
  const detail = text(find(m.root, "aside.pl-detail")[0]);
  assert.match(detail, new RegExp(`${ROOT}/docs`), "the work tree is the work root joined to the stream's path");
  assert.match(detail, /1 warning/);
  assert.doesNotMatch(detail, /blocker/, "no blockers, nothing said");
  await until(() => planIds(m).includes(`plan-items:${DIR}/ship/docs-2.5`), "the items slice held");
  await until(() => find(m.root, "ul.pl-items")[0]?.all("li").length === 4, "four items");
  assert.ok(subscribed(m).includes(`plan-items:${DIR}/ship/docs-2.5`));
  find(m.root, "aside.pl-detail")[0].all("button.close")[0].fire("click");
  await until(() => find(m.root, "aside.pl-detail").length === 0, "closed");
  assert.ok(planIds(m).includes(`plan-items:${DIR}/ship/docs-2.5`), "its rows still show, so its items stay held");
});

test("the blockers chip counts the stream's blocked items, whatever the stored blockers field says", () => {
  const raw = (body) => model.streamOf({ id: "s", stream: "s", owner: "o", ...body }, {}, "t");
  assert.equal(raw({ blockers: 0, counts: { blocked: 4 } }).blockers, 4);
  assert.equal(raw({ blockers: 3, counts: { blocked: 0 } }).blockers, 0, "a counted zero is a zero");
  assert.equal(raw({ blockers: 3, counts: { done: 1 } }).blockers, 3, "no blocked count: the field stands");
});

test("the blockers badge and an absolute work tree show for the stream that has them", async () => {
  const m = await open();
  find(rows(m)[0], "button.pl-main")[0].fire("click");
  await until(() => find(m.root, "aside.pl-detail").length === 1, "the detail panel");
  const detail = text(find(m.root, "aside.pl-detail")[0]);
  assert.match(detail, /1 blocker/);
  assert.match(detail, /\/work\/ah\/\.claude\/worktrees\/ui-r6/);
  assert.doesNotMatch(detail, /warning/);
});

test("a plan body with every optional field spelled out draws the same page as one that leaves them out", async () => {
  const m = await open();
  find(rows(m)[1], "button.pl-main")[0].fire("click");
  await until(() => find(m.root, "ul.pl-items").length === 1 && find(m.root, "ul.pl-items")[0].all("li").length === 4, "the detail panel");
  await settle();
  const page = () => text(find(m.root, "section.plans")[0]);
  const classes = () => find(m.root, "li.pl-row").map((li) => li.className).join("|");
  const before = [page(), classes()];

  const id = `plan:${DIR}/ship`;
  const body = JSON.parse(JSON.stringify(m.store.state.slices.get(id).body));
  const version = m.store.state.slices.get(id).version;
  for (const s of body.streams) {
    s.goal ??= "";
    s.counts = Object.fromEntries(["done", "in_progress", "not_started", "next", "blocked", "waiting_user", "waiting_orch", "dropped"].map((k) => [k, s.counts?.[k] ?? 0]));
    s.blockers ??= 0;
    s.warnings ??= 0;
    s.truncated ??= false;
    s.review_open ??= false;
    s.state ??= "open";
    if (s.work_tree === "." || !s.work_tree.startsWith("/")) {
      s.work_tree = s.work_tree === "." ? ROOT : `${ROOT}/${s.work_tree}`;
    }
  }
  m.model.publish([{ id, as_of: Date.now(), body }]);
  await until(() => m.store.state.slices.get(id).version !== version, "the explicit body");
  await settle();
  assert.deepEqual([page(), classes()], before);
});

test("Hide ended teams is on by default and hides a team with no members and no open stream; unchecking shows it", async () => {
  const m = await open();
  m.model.publish([summary("release", { running: false, members: 0, streams_open: 0, streams_total: 1, updated_at: new Date(Date.now() - 3 * 24 * HOUR).toISOString() })]);
  await settle();
  await settle();
  assert.deepEqual(find(m.root, "b.pl-title").map(text), ["ship"], "the ended team is not listed");
  assert.equal(planIds(m).includes(`plan:${DIR}/release`), false, "and its plan is not held");
  assert.match(text(find(m.root, "label.plans-hide")[0]), /Hide ended teams \(1\)/);
  find(m.root, "label.plans-hide")[0].all("input")[0].fire("change", { target: { checked: false } });
  await until(() => find(m.root, "b.pl-title").map(text).includes("release"), "the ended team listed");
  assert.match(text(find(m.root, "div.pl-head").at(-1)), /ended/);
});

test("a team with no members and an open stream is stale, not ended, and stays listed", async () => {
  const m = await open();
  m.model.publish([summary("release", { running: false, members: 0, streams_open: 1 })]);
  await until(() => find(m.root, "b.pl-title").map(text).includes("release"), "the stale team listed");
  assert.match(text(find(m.root, "div.pl-head").at(-1)), /no members · stale/);
});

test("a held team past its stale time counts its members in the chip and does not read no members", async () => {
  const m = await open();
  m.model.publish([summary("release", { held: true, running: true, members: 2, working: 0, updated_at: new Date(Date.now() - 3 * 24 * HOUR).toISOString() })]);
  await settle();
  await settle();
  find(m.root, "label.plans-hide")[0].all("input")[0].fire("change", { target: { checked: false } });
  await until(() => find(m.root, "b.pl-title").map(text).includes("release"), "the held team listed");
  const head = text(find(m.root, "div.pl-head").at(-1));
  assert.match(head, /Held · 2 members · stale/);
  assert.doesNotMatch(head, /no members/);
});

test("two teams in one repo are told apart by team name in every grouping", async () => {
  const m = await open();
  m.model.publish([summary("release")]);
  await until(() => labels(m).some((l) => l.includes("release ·")), "the second team's rows");
  for (const group of ["team", "stream", "repo", "status"]) {
    m.store.setUi({ plansGroup: group });
    await settle();
    const all = labels(m);
    assert.ok(all.some((l) => l.includes("ship · main · ")), `${group}: ship's rows`);
    assert.ok(all.some((l) => l.includes("release · main · ")), `${group}: release's rows`);
    assert.ok(all.every((l) => / · main · /.test(l)), `${group}: every row says its repo`);
  }
  m.store.setUi({ plansGroup: "team" });
  await settle();
  assert.deepEqual(find(m.root, "b.pl-title").map(text), ["release", "ship"], "two groups, each named");
});

test("the group-by is remembered across a reload, and blocked storage falls back to Team", async () => {
  const m = await open();
  assert.deepEqual(pressed(m), ["Team"]);
  click(m, "Stream");
  await until(() => pressed(m).includes("Stream"), "Stream chosen");
  assert.equal(fake.data["ah.plans.group"], "stream");
  // server-r5 finished before today, so stream aging (§2.4) hides it.
  assert.deepEqual(find(m.root, "b.pl-title").map(text), ["ah/ui-r6", "docs-2.5"], "one group per stream name");

  const again = await mount({ view: "allplans" });
  fake.data["ah.plans.group"] = "stream";
  await until(() => rows(again).length >= 2, "rows");
  assert.deepEqual(pressed(again), ["Stream"]);

  delete globalThis.localStorage;
  Object.defineProperty(globalThis, "localStorage", { get() { throw new Error("blocked"); }, configurable: true });
  const blocked = await mount({ view: "allplans" });
  await until(() => rows(blocked).length >= 2, "rows");
  assert.deepEqual(pressed(blocked), ["Team"]);
  click(blocked, "Repo");
  await until(() => pressed(blocked).includes("Repo"), "a choice still takes effect for this page");
});

test("the filter box narrows the list; an item title matches once that stream's items are held, which the List does at once", async () => {
  const m = await open();
  const filter = find(m.root, "input.plans-search")[0];
  filter.fire("input", { target: { value: "docs-writer" } });
  await until(() => rows(m).length === 1, "one row");
  assert.match(text(rows(m)[0]), /docs-2\.5/);
  filter.fire("input", { target: { value: "no such title anywhere" } });
  await until(() => rows(m).length === 0, "nothing");
  assert.match(text(find(m.root, "section.plans")[0]), /No stream matches/);
  filter.fire("input", { target: { value: "" } });
  await until(() => rows(m).length === 2, "all rows back");
  find(rows(m)[1], "button.pl-main")[0].fire("click");
  await until(() => planIds(m).includes(`plan-items:${DIR}/ship/docs-2.5`) && find(m.root, "ul.pl-items").length === 1, "the items held");
  filter.fire("input", { target: { value: "upgrade guide" } });
  await until(() => rows(m).length > 0 && rows(m).length < 3, "the streams whose items hold the title");
  assert.ok(rows(m).some((r) => /docs-2\.5/.test(text(r))));
});

test("the plan leases are released when Plans unmounts, and the team-level view leases only its own team", async () => {
  const m = await open();
  assert.deepEqual(planIds(m).filter((id) => id.startsWith("plan:")), [`plan:${DIR}/ship`]);
  find(rows(m)[0], "button.pl-main")[0].fire("click");
  await until(() => planIds(m).some((id) => id.startsWith("plan-items:")), "an items lease");
  m.store.setUi({ view: "teams" });
  await until(() => planIds(m).length === 0, "every plan slice dropped");
  await until(() => subscribed(m).length === 0, "no plan id still named in the poll");
  m.model.publish([summary("release")]);
  await until(() => m.store.state.slices.has(`team-summary:${DIR}/release`), "the second team's summary");
  m.store.setUi({ view: "board", team: `team:${DIR}/release` });
  await until(() => planIds(m).filter((id) => id.startsWith("plan:")).length === 1, "one plan");
  assert.deepEqual(planIds(m).filter((id) => id.startsWith("plan:")), [`plan:${DIR}/release`]);
  assert.deepEqual(find(m.root, "b.pl-title").map(text), ["release"]);
  assert.equal(find(m.root, "label.plans-hide").length, 0, "a team's own view has no hide-ended box");
});

test("the keys: 2 opens a team's Board and is nothing on Home, Shift+G cycles the grouping, a plain g does nothing", async () => {
  const m = await open("teams");
  const key = (k) => globalKey({ key: k, target: globalThis.document.body, preventDefault() {} }, m.store, globalThis.document);
  assert.equal(key("2"), false, "Home has no tabs");
  m.store.setUi({ view: "allplans" });
  assert.equal(key("G"), true);
  assert.equal(m.store.state.ui.plansGroup, "stream");
  assert.equal(fake.data["ah.plans.group"], "stream");
  assert.equal(key("G"), true);
  assert.equal(m.store.state.ui.plansGroup, "repo");
  assert.equal(key("g"), false);
  m.store.setUi({ view: "overview", team: `team:${DIR}/ship` });
  await until(() => m.store.state.slices.has(`team:${DIR}/ship`), "the team");
  assert.equal(key("G"), false, "outside the Board the key is not ours");
  assert.equal(key("2"), true);
  assert.equal(m.store.state.ui.view, "board");
});

test("the Board tab is a team's, and All teams in the picker is the Board of all teams while the Board is current", async () => {
  const m = await open("teams");
  const tab = (label) => find(find(m.root, "nav.views")[0], "button").find((b) => text(b) === label);
  assert.equal(tab("Board"), undefined, "Home has no tabs");
  m.store.setUi({ view: "overview", team: `team:${DIR}/ship` });
  await until(() => tab("Board"), "the team's tabs");
  tab("Board").fire("click");
  await until(() => m.store.state.ui.view === "board", "the team's Board");
  await until(() => tab("Board").getAttribute("aria-pressed") === "true", "the tab marked");
  find(m.root, "button.switch-btn")[0].fire("click");
  await until(() => find(m.root, "ul.menu").length === 1, "the team menu");
  find(m.root, "ul.menu")[0].all("button").find((b) => text(b) === "All teams").fire("click");
  assert.equal(m.store.state.ui.view, "allplans");
  m.store.setUi({ view: "graph", team: `team:${DIR}/ship` });
  await settle();
  find(m.root, "button.switch-btn")[0].fire("click");
  await until(() => find(m.root, "ul.menu").length === 1, "the team menu");
  find(m.root, "ul.menu")[0].all("button").find((b) => text(b) === "All teams").fire("click");
  assert.equal(m.store.state.ui.view, "teams", "from any other tab, All teams is Home");
});

test("itemsId names the lease the way the daemon names the slice", () => {
  assert.equal(itemsId({ dirId: "d", team: "t", stream: "main" }), "plan-items:d/t/main");
});

// ---- the board

const { DONE_CAP, COLUMNS } = model;
const DAY = 24 * HOUR;
const servedColumn = (item, stream) => servedItems({ items: [item] }, stream.review).items[0].column;
// The items as the daemon serves them, each placed in its column.
const boardOf = (teams, { itemsOf, ...rest }) => model.boardOf(teams, { ...rest, itemsOf: (s) => {
  const items = itemsOf(s);
  return items && servedItems({ items }, s.review, s.owner).items;
} });

test("mock owner: a dash holder or stream owner says nobody, as the daemon's; a named one is kept", () => {
  const owner = (holder, stream) => servedItems({ items: [{ holder, state: "in_progress" }] }, false, stream).items[0].owner;
  assert.equal(owner("—", "lead"), "lead");
  assert.equal(owner("—", "—"), "");
  assert.equal(owner(undefined, "—"), "");
  assert.equal(owner("impl", "lead"), "impl");
});

test("the mock's item column mirrors the daemon's rule; a stream in review moves its in-progress items; blocked is stuck, as PlanItemsLane.ColumnOf has it", () => {
  const plain = { review: false };
  const review = { review: true };
  const col = (state, stream = plain, done_seen_at = null) => servedColumn({ state, done_seen_at }, stream);
  assert.equal(col("not_started"), "next");
  assert.equal(col("next"), "next");
  assert.equal(col("in_progress"), "progress");
  assert.equal(col("blocked"), "stuck");
  assert.equal(col("waiting_user"), "waiting");
  assert.equal(col("in_progress", review), "review");
  assert.equal(col("not_started", review), "next", "only in-progress items move to review");
  assert.equal(col("blocked", review), "stuck");
  assert.equal(col("done", plain, NOW), "done");
  assert.equal(col("done", plain, null), null, "done before the daemon started: not shown");
  assert.equal(col("dropped"), null);
  assert.equal(col("something-new"), null);
});

const liveTeam = (name, streams, over = {}) => ({ ...teamModel(row({ id: `team:d/${name}`, name, ...over }), { work_root: ROOT, streams: streams.map((s) => servedStream(s)) }, NOW), label: name });
const done = (n, at) => Array.from({ length: n }, (_, i) => ({ key: `d${i}`, title: `Done ${i}`, state: "done", done_seen_at: at + i }));

test("Done today holds only items first seen done since the start of the day, newest first, and caps a lane at 20", () => {
  const dayStart = NOW - 6 * HOUR;
  const t = liveTeam("t", [{ stream: "s", owner: "o", counts: { done: 25 } }]);
  const items = [...done(25, dayStart + 10), { key: "old", title: "Old", state: "done", done_seen_at: dayStart - 1 }, { key: "none", title: "None", state: "done", done_seen_at: null }];
  const board = boardOf([t], { itemsOf: () => items, lanes: "none", dayStart });
  const cell = board.lanes[0].cells.done;
  assert.equal(cell.length, 25, "yesterday's and unknown ones are left out");
  assert.equal(board.lanes[0].doneMore, 25 - DONE_CAP);
  assert.equal(cell[0].title, "Done 24", "newest first");
  assert.ok(!cell.some((c) => c.title === "Old" || c.title === "None"));
});

test("a team with something waiting on you gets one decision card; none without", () => {
  const withNeed = liveTeam("a", [], { needs: 3, waitingOnYou: 3 });
  const without = liveTeam("b", [], { needs: 2, waitingOnYou: 0 });
  const board = boardOf([withNeed, without], { itemsOf: () => [], lanes: "team", dayStart: 0 });
  assert.deepEqual(board.lanes.map((l) => l.label), ["a"], "an empty lane is not drawn; blocked members in needs_you are no decision card");
  assert.deepEqual(board.lanes[0].cells.waiting.map((c) => [c.title, c.decision]), [["Waiting on you · 3", true]]);
});

test("an item row in waiting_rows has its own card from plan-items, so the decision card counts only what is left", () => {
  const rows = [{ kind: "item", key: "s:q" }];
  const none = liveTeam("a", [], { needs: 1, waitingOnYou: 1, waitingRows: rows });
  const more = liveTeam("b", [], { needs: 2, waitingOnYou: 2, waitingRows: rows });
  assert.deepEqual(boardOf([none], { itemsOf: () => [], lanes: "team", dayStart: 0 }).lanes, []);
  assert.deepEqual(boardOf([more], { itemsOf: () => [], lanes: "team", dayStart: 0 }).lanes[0].cells.waiting.map((c) => c.title), ["Waiting on you · 1"]);
});

test("a stale item, served with quiet_at, is hidden unless finished work is asked for; a stuck item with none stays", () => {
  const t = liveTeam("t", [{ stream: "s", owner: "o", counts: { in_progress: 2 } }]);
  const items = [{ key: "s:old", title: "old", owner: "o", state: "in_progress", column: "stuck", quiet_at: "2026-01-01T00:00:00.000Z" }, { key: "s:no", title: "no", owner: "o", state: "in_progress", column: "stuck" }];
  const stuck = (showFinished) => model.boardOf([t], { itemsOf: () => items, lanes: "none", dayStart: 0, showFinished }).lanes[0].cells.stuck.map((c) => c.title);
  assert.deepEqual(stuck(false), ["no"]);
  assert.deepEqual(stuck(true), ["old", "no"]);
});

test("swimlanes: none is one lane, team one per team, stream one per stream, holder one per holder (over the stream's owner)", () => {
  const a = liveTeam("a", [{ stream: "s", owner: "implementor-1", counts: { not_started: 2 } }]);
  const b = liveTeam("b", [{ stream: "s", owner: "reviewer", counts: { not_started: 1 } }]);
  const items = [{ key: "1", title: "One", state: "not_started" }, { key: "2", title: "Two", state: "in_progress", holder: "architect" }];
  const by = (lanes) => boardOf([a, b], { itemsOf: () => items, lanes, dayStart: 0 }).lanes.map((l) => [l.label, Object.values(l.cells).flat().length]);
  assert.deepEqual(by("none"), [["", 4]]);
  assert.deepEqual(by("team"), [["a", 2], ["b", 2]]);
  assert.deepEqual(by("holder"), [["architect", 2], ["implementor-1", 1], ["reviewer", 1]]);
  assert.deepEqual(by("stream"), [["s", 2], ["s", 2]], "one lane per stream, two teams' streams of one name apart");
});

test("a quiet team folds into one line unless expanded; a done stream puts no card on the board", () => {
  const stale = liveTeam("old", [{ stream: "s", owner: "o", counts: { not_started: 1 } }], { members: 0, streamsOpen: 1, itemsDone: 1, itemsTotal: 3, status: "stale", quietAt: NOW - 1 });
  const finished = liveTeam("fin", [{ stream: "s", owner: "o", state: "done", counts: { done: 1 } }]);
  const itemsOf = () => [{ key: "1", title: "One", state: "not_started" }];
  assert.equal(stale.status, "stale");
  const closed = boardOf([stale, finished], { itemsOf, lanes: "team", dayStart: 0 });
  assert.equal(closed.cards, 0);
  assert.deepEqual(closed.stale, [{ id: "team:d/old", name: "old", updated: stale.updated, streams: 1, done: 1, total: 3 }]);
  const opened = boardOf([stale, finished], { itemsOf, lanes: "team", dayStart: 0, expanded: ["team:d/old"] });
  assert.equal(opened.cards, 1);
  assert.deepEqual(opened.stale, []);
});

test("the board folds on the served instant alone: an ended team just gone keeps its card, one past the instant folds", () => {
  const stream = [{ stream: "s", owner: "o", counts: { not_started: 1 } }];
  const itemsOf = () => [{ key: "1", title: "One", state: "not_started" }];
  const recent = liveTeam("recent", stream, { status: "ended", quietAt: NOW + 1 });
  const none = liveTeam("none", stream, { status: "stale", quietAt: 0 });
  const past = liveTeam("past", stream, { status: "ended", quietAt: NOW - 1 });
  const board = boardOf([recent, none, past], { itemsOf, lanes: "team", dayStart: 0 });
  assert.equal(board.cards, 2);
  assert.deepEqual(board.stale.map((t) => t.name), ["past"]);
});

test("the filter keeps the cards of the streams it matches", () => {
  const t = liveTeam("t", [{ stream: "alpha", owner: "o", counts: { not_started: 1 } }, { stream: "beta", owner: "o", counts: { not_started: 1 } }]);
  const board = boardOf([t], { itemsOf: () => [{ key: "1", title: "One", state: "not_started" }], lanes: "none", query: "beta", dayStart: 0 });
  assert.deepEqual(board.lanes[0].cells.next.map((c) => c.stream), ["beta"]);
});

// The board's cards (the kanban is the Board's only look since board-r2).
const tiles = (m) => find(m.root, "article.kb-card");
const tileNames = (m) => tiles(m).map((b) => text(b.all("h3.kb-title")[0]));

const itemsBodies = () => {
  const now = Date.now();
  return [
    [`plan-items:${DIR}/ship/ah/ui-r6`, [
      { key: "a", title: "A today", state: "done", done_seen_at: now - 1000 },
      { key: "b", title: "B unknown", state: "done", done_seen_at: null },
      { key: "c", title: "C long ago", state: "done", done_seen_at: now - 2 * DAY },
      { key: "d", title: "D blocked", state: "blocked", done_seen_at: null },
      { key: "e", title: "E working", state: "in_progress", done_seen_at: null },
      { key: "f", title: "F waiting", state: "waiting_user", done_seen_at: null },
      { key: "g", title: "G next", state: "next", done_seen_at: null },
      { key: "h", title: "H dropped", state: "dropped", done_seen_at: null },
    ]],
    [`plan-items:${DIR}/ship/docs-2.5`, [
      { key: "i", title: "I in review", state: "in_progress", done_seen_at: null },
      { key: "j", title: "J todo", state: "not_started", done_seen_at: null },
      { key: "k", title: "K today", state: "done", done_seen_at: now - 5000 },
    ]],
  ];
};

const openCards = async (view = "board", extra = {}) => {
  useStorage({ "ah.plans.layout": "board", ...extra });
  const m = await mount({ view });
  await until(() => tiles(m).length > 0, "tiles");
  return m;
};

// A team's board with its two open streams' items replaced by a known set.
const openBoard = async (extra = {}) => {
  const m = await openCards("board", extra);
  await until(() => planIds(m).filter((id) => id.startsWith("plan-items:")).length === 2, "the items of both open streams");
  m.model.publish(itemsBodies().map(([id, items]) => ({ id, as_of: Date.now(), body: { stream: id.split("/").slice(2).join("/"), items, truncated: false } })));
  await until(() => tileNames(m).includes("G next"), "the controlled items");
  await settle();
  return m;
};

test("the List | Board choice is remembered, Shift+L toggles it, and Shift+G on the board with nothing selected opens Graph", async () => {
  const m = await openCards("allplans");
  assert.deepEqual(find(m.root, "span.plans-layout")[0].all("button").filter((b) => b.getAttribute("aria-pressed") === "true").map(text), ["Board"]);
  const key = (k) => globalKey({ key: k, target: globalThis.document.body, preventDefault() {} }, m.store, globalThis.document);
  assert.equal(key("G"), true);
  assert.equal(m.store.state.ui.view, "graph");
  assert.equal(m.store.state.ui.plansGroup, undefined, "the board's G does not touch the list's grouping");
  m.store.setUi({ view: "allplans" });
  await until(() => tiles(m).length > 0, "the board again");
  assert.equal(key("L"), true);
  await until(() => find(m.root, "section.pl-group").length > 0, "the list");
  assert.equal(fake.data["ah.plans.layout"], "list");
  assert.equal(key("G"), true);
  assert.equal(m.store.state.ui.plansGroup, "stream", "in the list G cycles the grouping");
  assert.equal(key("L"), true);
  assert.equal(fake.data["ah.plans.layout"], "board");
});

test("blocked storage opens the List grouped by Team, and a choice still holds for the page", async () => {
  Object.defineProperty(globalThis, "localStorage", { get() { throw new Error("blocked"); }, configurable: true });
  const m = await mount({ view: "allplans" });
  await until(() => rows(m).length >= 2, "rows");
  assert.equal(find(m.root, "div.pl-ice").length, 0);
  assert.deepEqual(pressed(m), ["Team"]);
  click(m, "Board");
  await until(() => tiles(m).length > 0, "the board");
});

test("the arrow keys and L outside Plans are not ours", async () => {
  const m = await open();
  const key = (k) => globalKey({ key: k, target: globalThis.document.body, preventDefault() {} }, m.store, globalThis.document);
  assert.equal(key("ArrowRight"), false, "the list has no columns");
  m.store.setUi({ view: "overview" });
  assert.equal(key("ArrowRight"), false);
  assert.equal(key("L"), false);
});

test("the columns are Next, In progress, In review, Waiting on you, Stuck and Done today", () => {
  assert.deepEqual(COLUMNS.map(([, label]) => label), ["Next", "In progress", "In review", "Waiting on you", "Stuck", "Done today"]);
});

// ---- stale and ended in the list

const quiet = (over = {}) => summary("release", { running: false, members: 0, streams_open: 1, streams_total: 1, items_done: 1, items_total: 4, ...over });
const groupOf = (m, name) => find(m.root, "section.pl-group").find((g) => g.getAttribute("aria-label") === name);

test("a stale team sorts under the live ones, dimmed and folded to its header; its toggle opens the streams", async () => {
  const m = await open();
  m.model.publish([quiet()]);
  await until(() => groupOf(m, "release"), "the stale group");
  assert.deepEqual(find(m.root, "section.pl-group").map((g) => g.getAttribute("aria-label")), ["ship", "release"], "live first");
  assert.ok(groupOf(m, "release").className.includes("stale"));
  assert.ok(!groupOf(m, "ship").className.includes("stale"));
  assert.equal(groupOf(m, "release").all("li.pl-row").length, 0, "folded: no rows");
  const fold = groupOf(m, "release").all("button.pl-fold")[0];
  assert.match(text(fold), /1 stream/);
  assert.equal(fold.getAttribute("aria-expanded"), "false");
  fold.fire("click");
  await until(() => groupOf(m, "release").all("li.pl-row").length === 1, "the stream listed");
  assert.ok(groupOf(m, "release").all("li.pl-row")[0].className.includes("stale"));
  assert.equal(groupOf(m, "ship").all("button.pl-fold").length, 0, "a live team has no fold");
  groupOf(m, "release").all("button.pl-fold")[0].fire("click");
  await until(() => groupOf(m, "release").all("li.pl-row").length === 0, "folded again");
});

test("a team that is quiet for more than a day is stale even with members; one quiet for an hour is live", async () => {
  const m = await open();
  const old = new Date(Date.now() - 30 * HOUR).toISOString();
  m.model.publish([summary("release", { updated_at: old, working: 0 })]);
  await until(() => groupOf(m, "release") && m.store.state.slices.has(`plan:${DIR}/release`), "the group and its plan");
  const plan = m.store.state.slices.get(`plan:${DIR}/release`);
  const quietPlan = JSON.parse(JSON.stringify(plan.body));
  quietPlan.as_of = Date.now() - 30 * HOUR;
  quietPlan.streams.forEach((x) => (x.as_of = quietPlan.as_of));
  m.model.publish([{ id: plan.id, as_of: Date.now(), body: quietPlan }]);
  await until(() => groupOf(m, "release").className.includes("stale"), "stale: members, idle 30 h");
  m.model.publish([summary("release", { updated_at: new Date(Date.now() - HOUR).toISOString() })]);
  await until(() => !groupOf(m, "release").className.includes("stale"), "live again");
  assert.match(text(groupOf(m, "release").all("div.pl-head")[0]), /live · 2 members/);
});

test("an ended team, shown on request, is dimmer and folded like a stale one", async () => {
  const m = await open();
  m.model.publish([quiet({ streams_open: 0 })]);
  await settle();
  assert.equal(groupOf(m, "release"), undefined, "hidden by default");
  find(m.root, "label.plans-hide")[0].all("input")[0].fire("change", { target: { checked: false } });
  await until(() => groupOf(m, "release"), "the ended group");
  assert.ok(groupOf(m, "release").className.includes("ended"));
  assert.equal(groupOf(m, "release").all("li.pl-row").length, 0);
  assert.equal(groupOf(m, "release").all("div.pl-head")[0].className.includes("ended"), true);
  assert.match(text(groupOf(m, "release").all("div.pl-head")[0]), /ended/);
});

test("grouped by stream, the rows of a stale team come after the live ones and are dimmed", async () => {
  const m = await open();
  m.model.publish([quiet()]);
  await until(() => groupOf(m, "release"), "the stale team");
  m.store.setUi({ plansGroup: "stream", plansStale: [`team:${DIR}/release`] });
  await until(() => groupOf(m, "docs"), "the docs group (both teams' docs streams)");
  const all = find(m.root, "li.pl-row");
  const label = (li) => find(li, "button.pl-main")[0].getAttribute("aria-label");
  const release = all.filter((li) => label(li).includes("release ·"));
  assert.ok(release.length > 0);
  assert.ok(release.every((li) => li.className.includes("stale")));
  assert.ok(!all.filter((li) => label(li).includes("ship ·")).some((li) => li.className.includes("stale")));
  const docsRows = groupOf(m, "docs").all("li.pl-row").map(label);
  assert.ok(docsRows.length >= 1);
});

test("a team's own Plans view shows its streams even when the team is stale", async () => {
  const m = await open();
  m.model.publish([quiet()]);
  await until(() => m.store.state.slices.has(`team-summary:${DIR}/release`), "the summary");
  m.store.setUi({ view: "board", team: `team:${DIR}/release` });
  await until(() => find(m.root, "li.pl-row").length === 1, "the stream, not folded");
  assert.equal(find(m.root, "button.pl-fold").length, 0);
});

test("the old plan popup and list dialog are gone: their ui flags draw nothing", async () => {
  const m = await open();
  m.store.setUi({ planList: true, planTeam: `${DIR}/ship` });
  await settle();
  assert.equal(find(m.root, "section.dialog").length, 0);
  assert.equal(planIds(m).some((id) => id.startsWith("list:plan")), false);
});

// ---- Done today keeps a stream that finishes, and the view moves with the clock

const realNow = Date.now;
let ticks = [];
const captureTicks = () => {
  ticks = [];
  globalThis.setInterval = (fn, ms) => (ms === 60_000 ? ticks.push(fn) : 0);
};
afterEach(() => {
  Date.now = realNow;
  globalThis.setInterval = () => 0;
});
const tick = () => ticks.at(-1)();
const iso = (ms) => new Date(ms).toISOString();

const planBody = (m, team) => JSON.parse(JSON.stringify(m.store.state.slices.get(`plan:${DIR}/${team}`).body));
const publishPlan = (m, team, body) => m.model.publish([{ id: `plan:${DIR}/${team}`, as_of: Date.now(), body }]);

test("a done stream last touched yesterday is not leased; one touched today is", async () => {
  captureTicks();
  const m = await openBoard();
  const id = `plan-items:${DIR}/ship/server-r5`;
  const body = planBody(m, "ship");
  const server = body.streams.find((s) => s.stream === "server-r5");
  server.as_of = Date.now() - 2 * DAY;
  publishPlan(m, "ship", body);
  await settle();
  assert.equal(planIds(m).includes(id), false, "yesterday's");
  server.as_of = Date.now();
  publishPlan(m, "ship", body);
  await until(() => planIds(m).includes(id), "today's, held");
  assert.ok(subscribed(m).includes(id));
});

const releaseSummary = (updated) => summary("release", { updated_at: iso(updated), working: 0 });

test("crossing local midnight releases the items of a stream that finished the day before", async () => {
  captureTicks();
  const m = await openCards("allplans");
  m.store.setUi({ plansFocus: "t:release" });
  const midnight = new Date(realNow()).setHours(24, 0, 0, 0);
  const before = midnight - 10 * 60_000;
  const after = midnight + 10 * 60_000;
  Date.now = () => before;
  const plan = { team: "release", dir_id: DIR, work_root: ROOT, streams: [{ stream: "old", owner: "docs-writer", branch: "ah/old", state: "done", source: "checklist", counts: { done: 1 }, as_of: before - 1000 }], counts: {}, as_of: before - 1000, decisions: { by_user: 0, by_team: 0, waiting: 0, scope: "dir" } };
  m.model.publish([releaseSummary(before - 60_000), { id: `plan:${DIR}/release`, as_of: before, body: plan }, { id: `plan-items:${DIR}/release/old`, as_of: before, body: { stream: "old", items: [{ key: "old:x", title: "Shipped late", state: "done", done_seen_at: before - 1000 }], truncated: false } }]);
  await until(() => m.store.state.slices.has(`team-summary:${DIR}/release`) && m.store.state.slices.has(`plan:${DIR}/release`), "the release team");
  tick();
  await until(() => planIds(m).includes(`plan-items:${DIR}/release/old`), "today's done stream held");
  await until(() => tileNames(m).includes("Shipped late"), "its tile");

  Date.now = () => after;
  m.model.publish([releaseSummary(after - 60_000)]);
  await settle();
  tick();
  await until(() => !planIds(m).includes(`plan-items:${DIR}/release/old`), "yesterday's done stream released");
  await until(() => !subscribed(m).includes(`plan-items:${DIR}/release/old`), "and no longer named in the poll");
  assert.ok(!tileNames(m).includes("Shipped late"), "back to a stream tile sized by its plan");
});

test("a team crossing the 24 h stale line flips on the tick, with no slice changing", async () => {
  captureTicks();
  useStorage({});
  const m = await mount({ view: "allplans" });
  await until(() => rows(m).length >= 2, "rows");
  const base = realNow();
  m.model.publish([releaseSummary(base - 23 * HOUR)]);
  await until(() => m.store.state.slices.has(`plan:${DIR}/release`) && groupOf(m, "release"), "the release group");
  const plan = planBody(m, "release");
  plan.as_of = base - 23 * HOUR;
  plan.streams.forEach((s) => (s.as_of = plan.as_of));
  publishPlan(m, "release", plan);
  await settle();
  assert.ok(!groupOf(m, "release").className.includes("stale"), "23 h quiet is live");
  const versions = () => ["team-summary", "plan"].map((p) => m.store.state.slices.get(`${p}:${DIR}/release`).version).join(",");
  const held = versions();
  Date.now = () => base + 2 * HOUR;
  tick();
  await until(() => groupOf(m, "release").className.includes("stale"), "stale at 25 h");
  assert.equal(versions(), held, "no slice moved");
});

test("the tick asks for nothing: no lease call, no list read, when the leases stand", async () => {
  captureTicks();
  const m = await openBoard();
  const lease = m.store.lease;
  let leaseCalls = 0;
  m.store.lease = (...a) => (leaseCalls += 1, lease.apply(m.store, a));
  const reads = m.log.lists.length;
  let fetched = 0;
  const fetchList = m.store.fetchList;
  m.store.fetchList = (...a) => (fetched += 1, fetchList.apply(m.store, a));
  tick();
  tick();
  await settle();
  assert.equal(leaseCalls, 0, "the lease set did not change");
  assert.equal(fetched, 0);
  assert.equal(m.log.lists.length, reads);
  assert.equal(ticks.length, 1, "one timer for the view");
});

test("the timer is cleared when Plans unmounts", async () => {
  const cleared = [];
  captureTicks();
  globalThis.clearInterval = (id) => cleared.push(id);
  try {
    const m = await open();
    m.store.setUi({ view: "teams" });
    await until(() => cleared.length > 0, "the timer cleared");
  } finally {
    globalThis.clearInterval = () => {};
  }
});

// ---- C4: what changed since the viewer last looked

const seenMod = await import("../../web/ui/plans-seen.js");
const { marksOf, loadTrack, saveTrack, capped, snapshotOf, hashOf: itemHash, SEEN_CAP } = seenMod;
const { buildSlices } = await import("../../web/mock/fixture.mjs");

const stream = (over = {}) => ({ stream: "s", asOf: 1000, ...over });
const snap = (pairs) => ({ a: 1, i: Object.fromEntries(Object.entries(pairs).map(([k, v]) => [k, v])) });
const it = (key, state, done_seen_at = null) => ({ key: `s:${key}`, title: key, state, done_seen_at });

test("first visit: nothing is marked, whatever the stream and items look like", () => {
  const m = marksOf(stream({ asOf: 9e12 }), { since: null, snapshot: undefined, items: [it("a", "done", 9e12), it("b", "in_progress")] });
  assert.equal(m.changed, false);
  assert.equal(m.stream, false);
  assert.equal(m.items.size, 0);
  assert.equal(m.removed, 0);
});

test("a stream whose plan moved after the last look is marked; one at or before it is not", () => {
  assert.equal(marksOf(stream({ asOf: 1001 }), { since: 1000, snapshot: undefined, items: null }).stream, true);
  assert.equal(marksOf(stream({ asOf: 1000 }), { since: 1000, snapshot: undefined, items: null }).changed, false, "equal is not after");
  assert.equal(marksOf(stream({ asOf: 999 }), { since: 1000, snapshot: undefined, items: null }).changed, false);
});

test("an item whose state differs from the snapshot is moved, from → to", () => {
  const m = marksOf(stream({ asOf: 0 }), { since: 1000, snapshot: snap({ a: "p", b: "n" }), items: [it("a", "done"), it("b", "not_started")] });
  assert.deepEqual([...m.items], [["s:a", { kind: "moved", from: "in_progress", to: "done" }]]);
  assert.equal(m.changed, true, "an item moved, though the stream's own time is old");
});

test("an item absent from a snapshot is new; with no snapshot nothing is new", () => {
  const items = [it("a", "not_started"), it("fresh", "not_started")];
  assert.deepEqual([...marksOf(stream({ asOf: 0 }), { since: 1000, snapshot: snap({ a: "n" }), items }).items], [["s:fresh", { kind: "new" }]]);
  assert.equal(marksOf(stream({ asOf: 0 }), { since: 1000, snapshot: undefined, items }).items.size, 0);
  assert.equal(marksOf(stream({ asOf: 0 }), { since: 1000, snapshot: { a: 1, i: null }, items }).items.size, 0, "a stream marked seen without items has no baseline");
});

test("a key in the snapshot that is gone now counts as removed", () => {
  const m = marksOf(stream({ asOf: 0 }), { since: 1000, snapshot: snap({ a: "n", gone: "n", also: "d" }), items: [it("a", "not_started")] });
  assert.equal(m.removed, 2);
  assert.equal(m.changed, true);
  assert.equal(marksOf(stream({ asOf: 0 }), { since: 1000, snapshot: snap({ a: "n" }), items: [it("a", "not_started")] }).removed, 0);
  assert.equal(marksOf(stream({ asOf: 0 }), { since: 1000, snapshot: snap({ a: "n", gone: "n" }), items: null }).removed, 0, "nothing is said of removals while the items are not held");
});

test("an item first seen done after the last look is marked, with or without a snapshot; one done before is not", () => {
  const items = [it("late", "done", 2000), it("early", "done", 1000), it("never", "done", null)];
  const without = marksOf(stream({ asOf: 0 }), { since: 1000, snapshot: undefined, items });
  assert.deepEqual([...without.items.keys()], ["s:late"]);
  assert.deepEqual(without.items.get("s:late"), { kind: "done" });
  const withSnap = marksOf(stream({ asOf: 0 }), { since: 1000, snapshot: snap({ late: "d", early: "d", never: "d" }), items });
  assert.deepEqual([...withSnap.items.keys()], ["s:late"]);
});

test("a state move wins over done-since for the same item", () => {
  const m = marksOf(stream({ asOf: 0 }), { since: 1000, snapshot: snap({ a: "p" }), items: [it("a", "done", 5000)] });
  assert.equal(m.items.get("s:a").kind, "moved");
});

test("an item key is told by its hash: the stream prefix is not stored", () => {
  assert.equal(itemHash("s", "s:abc123"), "abc123");
  assert.equal(itemHash("s", "other:abc"), "other:abc");
  assert.deepEqual(snapshotOf("s", 7, [it("abc", "in_progress")]), { a: 7, i: { abc: "p" } });
  assert.deepEqual(snapshotOf("s", 7, null), { a: 7, i: null });
});

const memStore = (init = {}) => ({ data: { ...init }, getItem(k) { return this.data[k] ?? null; }, setItem(k, v) { this.data[k] = v; } });

test("the stored look and snapshots read back; absent, nonsense and throwing storage are told apart", () => {
  const store = memStore();
  const track = loadTrack(10, store);
  assert.deepEqual([track.blocked, track.look, track.seen, track.mountedAt], [false, {}, {}, 10]);
  track.look["d/t"] = 5;
  track.seen["d/t"] = { s: { a: 1, i: { x: "n" } } };
  assert.equal(saveTrack(track, store), true);
  const again = loadTrack(20, store);
  assert.deepEqual([again.look, again.seen], [{ "d/t": 5 }, { "d/t": { s: { a: 1, i: { x: "n" } } } }]);
  assert.equal(loadTrack(1, memStore({ "ah.plans.look": "nonsense{" })).blocked, false, "nonsense reads as nothing stored");
  assert.deepEqual(loadTrack(1, memStore({ "ah.plans.look": "[1]" })).look, {});
  assert.deepEqual(loadTrack(1, memStore({ "ah.plans.look": '{"d/t":"soon","d/u":3}' })).look, { "d/u": 3 });
  assert.equal(loadTrack(1, { getItem: () => { throw new Error("blocked"); } }).blocked, true);
  assert.equal(loadTrack(1, undefined).blocked, true);
  assert.equal(saveTrack(track, { setItem: () => { throw new Error("full"); } }), false);
  assert.equal(saveTrack(track, undefined), false);
});

test("the stored snapshots stay under the cap, the streams with the oldest plan time going first", () => {
  const items = Object.fromEntries(Array.from({ length: 60 }, (_, i) => [`k${String(i).padStart(10, "0")}`, "n"]));
  const seen = { "d/t": Object.fromEntries(Array.from({ length: 10 }, (_, i) => [`s${i}`, { a: 100 + i, i: items }])) };
  const size = JSON.stringify(seen).length;
  assert.deepEqual(Object.keys(capped(seen, size)["d/t"]).length, 10, "exactly at the cap: nothing goes");
  const small = capped(seen, size - 1);
  assert.deepEqual(Object.keys(small["d/t"]), ["s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8", "s9"], "only the oldest went");
  assert.ok(JSON.stringify(small).length <= size - 1);
  const tight = capped(seen, size / 2);
  assert.ok(JSON.stringify(tight).length <= size / 2);
  assert.ok(Object.keys(tight["d/t"]).includes("s9") && !Object.keys(tight["d/t"]).includes("s0"));
  assert.deepEqual(capped(seen, 1), {}, "a cap nothing fits under empties it");
  assert.equal(SEEN_CAP, 200_000);
});

test("the cap is applied when the snapshots are written", () => {
  const store = memStore();
  const track = loadTrack(1, store);
  const big = Object.fromEntries(Array.from({ length: 5000 }, (_, i) => [`k${String(i).padStart(10, "0")}`, "n"]));
  track.seen["d/t"] = Object.fromEntries(Array.from({ length: 12 }, (_, i) => [`s${i}`, { a: i, i: big }]));
  assert.equal(saveTrack(track, store), true);
  assert.ok(store.data["ah.plans.seen"].length <= SEEN_CAP);
  const kept = Object.keys(JSON.parse(store.data["ah.plans.seen"])["d/t"]);
  assert.ok(kept.includes("s11") && !kept.includes("s0"));
});

test("a changed stream whose items are not held is one card of its own, only under the Changes filter", () => {
  const t = { ...liveTeam("t", [{ stream: "s", owner: "o", counts: { in_progress: 1 }, as_of: 5 }]) };
  t.streams = t.streams.map((x) => ({ ...x, mark: { changed: true, stream: true } }));
  const filtered = boardOf([t], { itemsOf: () => null, lanes: "none", dayStart: 0, changesOnly: true });
  assert.deepEqual(filtered.lanes[0].cells.progress.map((c) => c.title), ["s — changed (open to see items)"]);
  assert.equal(boardOf([t], { itemsOf: () => null, lanes: "none", dayStart: 0 }).cards, 0, "without the filter an unheld stream has no card");
  const quietStream = { ...t, streams: t.streams.map((x) => ({ ...x, mark: { changed: false } })) };
  assert.equal(boardOf([quietStream], { itemsOf: () => null, lanes: "none", dayStart: 0, changesOnly: true }).cards, 0);
});

// ---- C4 in the page

const SCOPE = `${DIR}/ship`;
const mockItems = (name) => buildSlices(Date.now()).find((s) => s.id === `plan-items:${DIR}/ship/${name}`).body.items;
const baseline = (name, tweak = (i) => i) => {
  const snapshot = snapshotOf(name, 1, mockItems(name));
  tweak(snapshot.i);
  return snapshot;
};
const storeWith = ({ look, seen } = {}) => ({
  ...(look === undefined ? null : { "ah.plans.look": JSON.stringify({ [SCOPE]: look }) }),
  ...(seen === undefined ? null : { "ah.plans.seen": JSON.stringify({ [SCOPE]: seen }) }),
});
const MINUTE = 60_000;
const marked = (m) => find(m.root, "li.pl-row").filter((li) => li.all("span.pl-mark").length > 0).map((li) => text(li.all("b")[0]));
const openWith = async (init, view = "allplans") => {
  useStorage(init);
  const m = await mount({ view });
  await until(() => (init["ah.plans.layout"] === "board" ? tiles(m).length > 0 : rows(m).length >= 2), "rows");
  return m;
};

test("first visit: no row carries a mark; the plan and the open streams' items are held", async () => {
  const m = await openWith({});
  await settle();
  assert.deepEqual(marked(m), []);
  assert.deepEqual(planIds(m).filter((id) => id.startsWith("plan:")), [`plan:${DIR}/ship`]);
  assert.ok(planIds(m).every((id) => id.startsWith("plan:") || id.startsWith("plan-items:")));
});

test("a stream whose plan moved since the last look gets a dot and a changed name; the others do not", async () => {
  const m = await openWith(storeWith({ look: Date.now() - 8 * MINUTE }));
  await until(() => marked(m).length > 0, "a mark");
  assert.deepEqual(marked(m), ["ah/ui-r6"], "ui-r6 moved 5 minutes ago, docs-2.5 12 minutes ago");
  const label = (name) => find(m.root, "button.pl-main").find((b) => b.getAttribute("aria-label").startsWith(`${name},`)).getAttribute("aria-label");
  assert.match(label("ah/ui-r6"), /, changed since your last look$/);
  assert.doesNotMatch(label("docs-2.5"), /changed since/);
  assert.match(text(find(m.root, "span.pl-changed")[0]), /1 changed/, "the group says how many");
});

test("a changed stream that was never opened shows its changed items: moved, new and removed", async () => {
  const seen = baseline("ah/ui-r6", (i) => {
    i.r6a = "n";
    delete i.r6b;
    i.vanished = "n";
  });
  const m = await openWith(storeWith({ look: Date.now() - 8 * MINUTE, seen: { "ah/ui-r6": seen } }));
  await until(() => planIds(m).includes(`plan-items:${DIR}/ship/ah/ui-r6`), "its items held without opening");
  assert.equal(find(m.root, "aside.pl-detail").length, 0, "nothing is open");
  await until(() => /1 new · 1 moved · 1 removed/.test(text(rows(m)[0])), "the counts on the row");
  find(rows(m)[0], "button.pl-main")[0].fire("click");
  await until(() => find(m.root, "ul.pl-items").length === 1, "the items");
  const li = (title) => find(m.root, "ul.pl-items")[0].all("li").find((x) => text(x).includes(title));
  assert.match(text(li("R6a data core")), /not started → in progress/);
  assert.match(text(li("R6a data core")), /moved from not started/);
  assert.match(text(li("R6b frame")), /new/);
  assert.equal(li("R6c graph view").all("span.pl-mark").length, 0, "an unchanged item has no dot");
  assert.match(text(find(m.root, "aside.pl-detail")[0]), /1 removed/);
});

test("the List holds the items of every open stream, changed or not, past the old changed-stream cap", async () => {
  const m = await openWith(storeWith({ look: Date.now() - 60 * MINUTE }));
  const body = JSON.parse(JSON.stringify(m.store.state.slices.get(`plan:${DIR}/ship`).body));
  const t = Date.now();
  body.streams = Array.from({ length: 30 }, (_, i) => ({ stream: `n${String(i).padStart(2, "0")}`, owner: "o", branch: "b", state: "open", counts: { in_progress: 1 }, as_of: t - i * 1000 }));
  publishPlan(m, "ship", body);
  const leased = () => subscribed(m).filter((id) => id.startsWith("plan-items:"));
  await until(() => leased().length === 30, "30 item leases");
  await settle();
  assert.equal(marked(m).length, 30, "all 30 are marked at stream level");
});

test("item leases go when List is left; opening and closing a stream keeps them, and its marks stay", async () => {
  const seen = baseline("ah/ui-r6", (i) => (i.r6a = "n"));
  const m = await openWith(storeWith({ look: Date.now() - 8 * MINUTE, seen: { "ah/ui-r6": seen } }));
  const id = `plan-items:${DIR}/ship/ah/ui-r6`;
  await until(() => planIds(m).includes(id), "held");
  await until(() => /1 moved/.test(text(rows(m)[0])), "its moved item known");
  find(rows(m)[0], "button.pl-main")[0].fire("click");
  await until(() => find(m.root, "aside.pl-detail").length === 1, "opened");
  find(m.root, "aside.pl-detail")[0].all("button.close")[0].fire("click");
  await until(() => find(m.root, "aside.pl-detail").length === 0, "closed");
  assert.ok(planIds(m).includes(id), "still held: its item rows show");
  assert.match(text(rows(m)[0]), /1 moved/, "the mark outlives the lease");
  assert.deepEqual(marked(m), ["ah/ui-r6"]);
  m.store.setUi({ view: "teams" });
  await until(() => planIds(m).length === 0, "everything released on leaving");
});

test("the look is fixed for the visit: a later update does not clear a mark", async () => {
  const m = await openWith(storeWith({ look: Date.now() - 8 * MINUTE }));
  await until(() => marked(m).length === 1, "the mark");
  const body = JSON.parse(JSON.stringify(m.store.state.slices.get(`plan:${DIR}/ship`).body));
  body.streams.find((s) => s.stream === "docs-2.5").as_of = Date.now();
  publishPlan(m, "ship", body);
  await until(() => marked(m).length === 2, "another stream moved");
  assert.deepEqual(marked(m).sort(), ["ah/ui-r6", "docs-2.5"], "the first mark is still there");
  const items = JSON.parse(JSON.stringify(m.store.state.slices.get(`plan-items:${DIR}/ship/ah/ui-r6`)?.body ?? { stream: "ah/ui-r6", items: mockItems("ah/ui-r6"), truncated: false }));
  items.items[0].state = "done";
  m.model.publish([{ id: `plan-items:${DIR}/ship/ah/ui-r6`, as_of: Date.now(), body: items }]);
  await settle();
  assert.ok(marked(m).includes("ah/ui-r6"));
});

test("closing the view writes the look and the held items' states; a stream never held keeps what it had", async () => {
  const before = baseline("docs-2.5");
  const m = await openWith(storeWith({ look: Date.now() - 8 * MINUTE, seen: { "docs-2.5": before } }));
  const started = Date.now();
  await until(() => planIds(m).includes(`plan-items:${DIR}/ship/ah/ui-r6`), "ui-r6 items held");
  await settle();
  m.store.setUi({ view: "teams" });
  await until(() => planIds(m).length === 0, "closed");
  const look = JSON.parse(fake.data["ah.plans.look"]);
  assert.ok(look[SCOPE] >= started, "the look is now");
  const seen = JSON.parse(fake.data["ah.plans.seen"])[SCOPE];
  assert.equal(Object.keys(seen["ah/ui-r6"].i).length, 14, "ui-r6's items were held");
  assert.equal(seen["ah/ui-r6"].i.r6a, "p");
  assert.ok(seen["docs-2.5"].a > before.a, "docs-2.5's items are held by the List too, so its snapshot is now");
});

test("pagehide writes the same", async () => {
  const listeners = {};
  globalThis.addEventListener = (type, fn) => (listeners[type] = fn);
  globalThis.removeEventListener = (type) => delete listeners[type];
  try {
    const m = await openWith(storeWith({ look: Date.now() - 8 * MINUTE }));
    await until(() => planIds(m).includes(`plan-items:${DIR}/ship/ah/ui-r6`), "held");
    await settle();
    assert.equal(typeof listeners.pagehide, "function");
    const stamp = Date.now();
    delete fake.data["ah.plans.seen"];
    listeners.pagehide({ type: "pagehide" });
    assert.ok(JSON.parse(fake.data["ah.plans.look"])[SCOPE] >= stamp);
    assert.ok(JSON.parse(fake.data["ah.plans.seen"])[SCOPE]["ah/ui-r6"]);
    m.store.setUi({ view: "teams" });
    await until(() => planIds(m).length === 0, "closed");
    assert.equal(listeners.pagehide, undefined, "the listener is removed on unmount");
  } finally {
    delete globalThis.addEventListener;
    delete globalThis.removeEventListener;
  }
});

test("the board marks a changed item's tile and its containers, says how in the card, and Mark all seen clears them", async () => {
  const seen = baseline("ah/ui-r6", (i) => (i.r6a = "n"));
  const m = await openWith({ ...storeWith({ look: Date.now() - 8 * MINUTE, seen: { "ah/ui-r6": seen } }), "ah.plans.layout": "board", "ah.plans.epics": "off" });
  // The kanban (the Board's only look since board-r2): the card says Changed, the panel says how.
  const dotted = () => tiles(m).filter((b) => b.all("span.kb-changed").length > 0);
  await until(() => dotted().some((b) => text(b.all("h3.kb-title")[0]).includes("R6a data core")), "the item's mark");
  const r6a = dotted().find((b) => text(b.all("h3.kb-title")[0]).includes("R6a data core"));
  assert.match(r6a.getAttribute("aria-label"), /, changed since your last visit$/);
  r6a.fire("click");
  const moved = () => (find(m.root, "section.kb-panel")[0]?.all("dd") ?? []).filter((d) => text(d).includes("→"));
  await until(() => moved().length === 1, "the panel says so");
  assert.match(text(moved()[0]), /not started → in progress/i);
  // Mark all seen is offered only while the filter is on (C9).
  assert.ok(!find(m.root, "button").some((b) => text(b) === "Mark all seen"));
  click(m, "Changed since last visit");
  await until(() => find(m.root, "button").some((b) => text(b) === "Mark all seen"), "Mark all seen");
  find(m.root, "button").find((b) => text(b) === "Mark all seen").fire("click");
  await until(() => dotted().length === 0, "all clear");
});

test("Mark all seen clears every mark at once and persists, and Shift+A does the same", async () => {
  const seen = baseline("ah/ui-r6", (i) => (i.r6a = "n"));
  const m = await openWith(storeWith({ look: Date.now() - 8 * MINUTE, seen: { "ah/ui-r6": seen } }));
  await until(() => marked(m).length === 1 && /1 moved/.test(text(rows(m)[0])), "marks");
  const stamp = Date.now();
  click(m, "Changed since last visit");
  await until(() => find(m.root, "button").some((b) => text(b) === "Mark all seen"), "Mark all seen");
  find(m.root, "button").find((b) => text(b) === "Mark all seen").fire("click");
  click(m, "Changed since last visit");
  await until(() => marked(m).length === 0 && rows(m).length > 0, "all clear");
  assert.doesNotMatch(text(rows(m)[0]), /moved/);
  assert.ok(JSON.parse(fake.data["ah.plans.look"])[SCOPE] >= stamp, "written at once");
  const stored = JSON.parse(fake.data["ah.plans.seen"])[SCOPE];
  assert.equal(stored["ah/ui-r6"].i.r6a, "p", "the snapshot is the current items");
  assert.ok(stored["docs-2.5"].i !== null, "every open stream's items are held by the List, so each is recorded with its items");

  const second = await openWith(storeWith({ look: Date.now() - 8 * MINUTE }));
  await until(() => marked(second).length === 1, "marked again");
  const key = (k) => globalKey({ key: k, target: globalThis.document.body, preventDefault() {} }, second.store, globalThis.document);
  assert.equal(key("A"), true);
  await until(() => marked(second).length === 0, "Shift+A clears");
});

test("blocked storage: no marks, and the Changes filter says tracking needs storage", async () => {
  Object.defineProperty(globalThis, "localStorage", { get() { throw new Error("blocked"); }, configurable: true });
  const m = await mount({ view: "allplans" });
  await until(() => rows(m).length >= 2, "rows");
  await settle();
  assert.deepEqual(marked(m), []);
  click(m, "Changed since last visit");
  await until(() => /Change tracking needs browser storage/.test(text(find(m.root, "section.plans")[0])), "the message");
  assert.equal(rows(m).length, 0);
});

test("the Changes filter lists only changed streams and, in the detail, only changed items until Show all items", async () => {
  const seen = baseline("ah/ui-r6", (i) => (i.r6a = "n"));
  const m = await openWith(storeWith({ look: Date.now() - 8 * MINUTE, seen: { "ah/ui-r6": seen } }));
  await until(() => /1 moved/.test(text(rows(m)[0])), "marks");
  click(m, "Changed since last visit");
  await until(() => rows(m).length === 1, "only the changed stream");
  assert.equal(fake.data["ah.plans.changes"], "on", "remembered like the grouping");
  assert.match(text(rows(m)[0]), /ah\/ui-r6/);
  find(rows(m)[0], "button.pl-main")[0].fire("click");
  await until(() => find(m.root, "ul.pl-items").length === 1, "the items");
  assert.equal(find(m.root, "ul.pl-items")[0].all("li").length, 1, "only the moved item");
  click(m, "Show all items");
  await until(() => find(m.root, "ul.pl-items")[0].all("li").length === 14, "all items");
  click(m, "Changed since last visit");
  await until(() => rows(m).length === 2, "the filter off");
});

test("with the Changes filter on and nothing changed, the list says since when", async () => {
  const m = await openWith(storeWith({ look: Date.now() + MINUTE }));
  click(m, "Changed since last visit");
  await until(() => /Nothing changed since/.test(text(find(m.root, "section.plans")[0])), "the message");
  assert.equal(rows(m).length, 0);
  assert.equal(find(m.root, "section.pl-group").length, 0, "no empty group is listed");
});

test("on a first visit the Changes filter says there is nothing to compare yet", async () => {
  const m = await openWith({});
  click(m, "Changed since last visit");
  await until(() => /first visit/.test(text(find(m.root, "section.plans")[0])), "the message");
});

test("Shift+C toggles the Changes filter and Shift+A marks all seen, in Plans only", async () => {
  const m = await openWith(storeWith({ look: Date.now() - 8 * MINUTE }));
  const key = (k) => globalKey({ key: k, target: globalThis.document.body, preventDefault() {} }, m.store, globalThis.document);
  assert.equal(key("C"), true);
  await until(() => rows(m).length === 1, "filtered");
  assert.equal(fake.data["ah.plans.changes"], "on");
  assert.equal(key("C"), true);
  await until(() => rows(m).length === 2, "unfiltered");
  m.store.setUi({ view: "overview" });
  assert.equal(key("C"), false);
  assert.equal(key("A"), false);
});

test("C4 adds no lease ids on a first visit: the Board's set is C3's", async () => {
  const m = await openWith({ "ah.plans.layout": "board" }, "board");
  await until(() => planIds(m).filter((id) => id.startsWith("plan-items:")).length === 2, "the board's leases");
  assert.deepEqual(planIds(m).sort(), [`plan-items:${DIR}/ship/ah/ui-r6`, `plan-items:${DIR}/ship/docs-2.5`, `plan:${DIR}/ship`]);
});

// ---- C4 review fixes

test("blocked storage: a stream that changes during the visit is not marked", async () => {
  Object.defineProperty(globalThis, "localStorage", { get() { throw new Error("blocked"); }, configurable: true });
  const m = await mount({ view: "allplans" });
  await until(() => rows(m).length >= 2, "rows");
  const body = JSON.parse(JSON.stringify(m.store.state.slices.get(`plan:${DIR}/ship`).body));
  body.streams.find((s) => s.stream === "docs-2.5").as_of = Date.now() + 5000;
  publishPlan(m, "ship", body);
  await settle();
  await settle();
  assert.deepEqual(marked(m), []);
});

test("pagehide saves a copy: the marks of the visit survive a page restored afterwards", async () => {
  const listeners = {};
  globalThis.addEventListener = (type, fn) => (listeners[type] = fn);
  globalThis.removeEventListener = (type) => delete listeners[type];
  try {
    const m = await openWith(storeWith({ look: Date.now() - 8 * MINUTE }));
    await until(() => marked(m).length === 1, "the mark");
    listeners.pagehide({ type: "pagehide" });
    const body = JSON.parse(JSON.stringify(m.store.state.slices.get(`plan:${DIR}/ship`).body));
    body.streams.find((s) => s.stream === "docs-2.5").as_of = Date.now() + 5000;
    publishPlan(m, "ship", body);
    await until(() => marked(m).includes("docs-2.5"), "the next update");
    assert.ok(marked(m).includes("ah/ui-r6"), "the earlier mark is still there");
  } finally {
    delete globalThis.addEventListener;
    delete globalThis.removeEventListener;
  }
});

test("closing the view does not stamp a team whose plan never arrived", async () => {
  const m = await openWith(storeWith({ look: Date.now() - 8 * MINUTE }));
  m.model.publish([summary("late", { updated_at: new Date().toISOString() })]);
  await until(() => groupOf(m, "late"), "the team without a plan");
  m.store.setUi({ view: "teams" });
  await until(() => planIds(m).length === 0, "closed");
  const look = JSON.parse(fake.data["ah.plans.look"]);
  assert.ok(look[SCOPE] > 0, "the team with a plan is stamped");
  assert.equal(`${DIR}/late` in look, false);
});

test("a waiting_orch item sits in In progress with its own label, and a stream holding only those is in progress, not waiting on you", () => {
  const stream = (counts) => ({ counts: { done: 0, in_progress: 0, not_started: 0, next: 0, blocked: 0, waiting_user: 0, waiting_orch: 0, dropped: 0, ...counts }, state: "open" });
  assert.equal(servedColumn({ state: "waiting_orch" }, { review: false }), "progress");
  assert.equal(servedStream(stream({ waiting_orch: 1 })).status, "in_progress");
  assert.equal(servedStream(stream({ waiting_orch: 1, waiting_user: 1 })).status, "waiting");
});

test("a stream holding only waiting_orch items draws its whole bar as work, none as attention, and no waiting-on-you chip", async () => {
  const m = await open();
  const id = `plan:${DIR}/ship`;
  const version = m.store.state.slices.get(id).version;
  const body = JSON.parse(JSON.stringify(m.store.state.slices.get(id).body));
  const s = body.streams[0];
  s.counts = { done: 0, in_progress: 0, not_started: 0, next: 0, blocked: 0, waiting_user: 0, waiting_orch: 4, dropped: 0 };
  s.state = "open";
  m.model.publish([{ id, as_of: Date.now(), body }]);
  await until(() => m.store.state.slices.get(id).version !== version, "the waiting_orch stream");
  await settle();
  const widths = find(rows(m)[0], "span.pl-bar")[0].all("i").map((i) => `${i.className}:${i.style?.width ?? i.attrs.get("style")}`);
  assert.deepEqual(widths, ["ok:0%", "work:100%", "attn:0%"]);
  assert.doesNotMatch(text(rows(m)[0]), /waiting on you/);
});

test("a served backlog groups into wave columns by rank; done, dropped and unknown waves are handled; the filter reads id, title and theme", () => {
  const items = [
    { key: "b3", title: "C", state: "not_started", wave: "Now", rank: 2 },
    { key: "b1", title: "A", state: "in_progress", wave: "now", rank: 1, theme: "DB/perf" },
    { key: "b2", title: "B", state: "done", wave: "now", rank: 0 },
    { key: "b4", title: "D", state: "dropped", wave: "later" },
    { key: "b5", title: "E", state: "next" },
    { key: "b6", title: "F", state: "next", wave: "someday" },
  ];
  const b = model.waveColumns(servedBacklog({ items, health: { stale: 2, due: true } }));
  assert.deepEqual(Object.fromEntries(Object.entries(b.cols).map(([w, c]) => [w, c.map((i) => i.key)])), { now: ["b1", "b3"], next: [], later: [], untriaged: ["b5", "b6"] });
  assert.deepEqual([b.open, b.total, b.health], [4, 6, { untriaged: 0, stale: 2, due: true }]);
  assert.deepEqual(model.waveColumns(servedBacklog({ items }), "db/").cols.now.map((i) => i.key), ["b1"]);
  assert.equal(model.waveColumns(undefined).open, 0);
  assert.deepEqual(["github:o/r#12", "linear:ABC-1", "raw"].map(model.extText), ["#12", "ABC-1", "raw"]);
});

// ---- W1: lanes per level, Blocked only, the card, the item panel

const { lanesFor, laneChoice, historyOf, itemIdOf, BLOCKED_STATES, cycle } = model;

test("a team's board offers Stream, Holder and None (Stream first); All teams adds Team (first); a stored lane the level lacks reads as its default", () => {
  assert.deepEqual(lanesFor(true).map(([k]) => k), ["stream", "holder", "none"]);
  assert.deepEqual(lanesFor(false).map(([k]) => k), ["team", "stream", "holder", "none"]);
  assert.equal(laneChoice(null, true), "stream");
  assert.equal(laneChoice(null, false), "team");
  assert.equal(laneChoice("owner", true), "stream", "the old owner lane falls back");
  assert.equal(laneChoice("owner", false), "team");
  assert.equal(laneChoice("team", true), "stream", "a team's board has no Team lanes");
  assert.equal(laneChoice("team", false), "team");
  assert.equal(laneChoice("holder", true), "holder");
});

test("the List names the run's own checklist Team to-dos, never _run", async () => {
  useStorage({});
  const m = await mount({ view: "board" });
  await until(() => rows(m).length > 0, "rows");
  const id = `plan:${DIR}/ship`;
  const body = JSON.parse(JSON.stringify(m.store.state.slices.get(id).body));
  body.streams.push({ ...body.streams[0], stream: "_run", goal: "" });
  m.model.publish([{ id, as_of: Date.now(), body }]);
  await until(() => rows(m).some((li) => text(li).includes("Team to-dos")), "the run's row");
  const row = rows(m).find((li) => text(li).includes("Team to-dos"));
  const main = find(row, "button.pl-main")[0];
  assert.match(main.getAttribute("aria-label"), /^Team to-dos, /);
  assert.equal(find(row, "button.pl-toboard")[0].getAttribute("aria-label"), "Open Team to-dos in board");
  main.fire("click");
  await until(() => find(m.root, "aside.pl-detail").length === 1, "the detail");
  assert.equal(text(find(m.root, "aside.pl-detail")[0].all("h2")[0]), "Team to-dos");
  assert.ok(!rows(m).some((li) => text(li).includes("_run") || /_run/.test(find(li, "button.pl-main")[0].getAttribute("aria-label"))), "no raw _run anywhere in the list");
  assert.equal(find(m.root, "aside.pl-detail")[0].getAttribute("aria-label"), "Stream Team to-dos", "the detail's name too");
  // The board's card for one of its items names the stream the same way.
  const items = mockItems(body.streams[0].stream);
  m.model.publish([{ id: `plan-items:${DIR}/ship/_run`, as_of: Date.now(), body: { items } }]);
  find(find(m.root, "section.plans")[0], "button").find((b) => text(b) === "Board").fire("click");
  await until(() => tiles(m).length > 0, "the board");
  m.store.setUi({ item: `_run:${items[0].key}` });
  await until(() => find(m.root, "section.kb-panel").length === 1, "the item card");
  const dd = find(m.root, "section.kb-panel")[0].all("dd").map(text);
  assert.ok(dd.includes("Team to-dos"), dd.join(" | "));
  assert.ok(!dd.some((x) => x.includes("_run")));
});

test("Blocked only keeps blocked, waiting-on-you and waiting-on-Orchestrator items; in progress and the rest go; the decision card stays", () => {
  const t = liveTeam("t", [{ stream: "s", owner: "o", counts: { blocked: 1 } }], { needs: 1, waitingOnYou: 1 });
  const items = ["blocked", "waiting_user", "waiting_orch", "in_progress", "next", "not_started"].map((state, i) => ({ key: `s:${i}`, title: state, state }));
  const shown = (blockedOnly) => boardOf([t], { itemsOf: () => items, lanes: "none", dayStart: 0, blockedOnly }).lanes.flatMap((l) => Object.values(l.cells).flat()).map((c) => c.title).sort();
  assert.deepEqual(BLOCKED_STATES, ["blocked", "waiting_user", "waiting_orch"]);
  const b = boardOf([t], { itemsOf: () => items, lanes: "none", dayStart: 0, blockedOnly: true });
  assert.deepEqual(b.counts, { next: 0, progress: 1, review: 0, waiting: 2, stuck: 1, done: 0 }, "the column header counts what the board shows");
  assert.deepEqual(shown(true), ["Waiting on you · 1", "blocked", "waiting_orch", "waiting_user"]);
  assert.equal(shown(false).length, 7);
});

test("a card carries its item id and its age from state_since; with no state_since it has no age", () => {
  const t = liveTeam("t", [{ stream: "ah/x", owner: "o", counts: { in_progress: 2 } }]);
  const since = new Date(NOW - 3 * HOUR).toISOString();
  const items = [{ key: "ah/x:r1", title: "One", state: "in_progress", state_since: since }, { key: "ah/x:r2", title: "Two", state: "in_progress" }];
  const cards = boardOf([t], { itemsOf: () => items, lanes: "stream", dayStart: 0 }).lanes[0].cells.progress;
  assert.deepEqual(cards.map((c) => [c.key, c.since, c.state]), [["r1", NOW - 3 * HOUR, "in_progress"], ["r2", null, "in_progress"]]);
  assert.equal(itemIdOf("other", "ah/x"), "other", "a key without its stream's prefix is kept whole");
});

test("an open card's time is its own item's, and only with none is it the stream's", () => {
  const t = liveTeam("t", [{ stream: "ah/x", owner: "o", as_of: NOW - HOUR, counts: { in_progress: 2 } }]);
  const since = new Date(NOW - 3 * HOUR).toISOString();
  const items = [{ key: "ah/x:r1", title: "One", state: "in_progress", state_since: since }, { key: "ah/x:r2", title: "Two", state: "in_progress" }];
  const cards = boardOf([t], { itemsOf: () => items, lanes: "stream", dayStart: 0 }).lanes[0].cells.progress;
  assert.deepEqual(cards.map((c) => c.at), [NOW - 3 * HOUR, t.streams[0].asOf]);
});

test("an item's history keeps its own stream's rows, newest first as served, in words", () => {
  const rows = [
    { at: "2026-10-30T10:00:00Z", seq: 3, stream: "s", item: "a", kind: "state", from: "next", to: "in_progress", by: "orchestrator@t" },
    { at: "2026-10-30T09:00:00Z", seq: 9, stream: "other", item: "a", kind: "state", from: null, to: "next", by: "x" },
    { at: "2026-10-30T08:00:00Z", seq: 2, stream: "s", item: "a", kind: "priority", from: "3", to: "1", by: "orchestrator@t" },
    { at: "2026-10-30T07:00:00Z", seq: 1, stream: "s", item: "a", kind: "added", from: null, to: null },
  ];
  const h = historyOf(rows, "s", (st) => st.replace("_", " "));
  assert.deepEqual(h.map((r) => r.text), ["next → in progress", "priority 3 → 1", "added"]);
  assert.deepEqual(h.map((r) => r.key), ["s|3", "s|2", "s|1"]);
  assert.equal(h[0].at, Date.parse("2026-10-30T10:00:00Z"));
  assert.equal(h[2].by, "");
  assert.deepEqual(historyOf([], "s"), []);
});

