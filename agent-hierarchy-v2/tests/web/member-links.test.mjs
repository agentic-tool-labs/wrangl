// ah-ui-ia U3: every surface that renders a member name renders it as a link to #/member/<id>, checked over the rendered DOM of
// each view on the `lanes` preset; and the member page itself (§4, §6). Nodes are compared with assert.ok(a === b), never assert.equal.
import { test } from "node:test";
import assert from "node:assert/strict";

process.env.AH_MOCK_FIXTURE = "lanes";
const { mountApp: mount, until, find, text, member, DIR } = await import("./mount-app.mjs");
const { memberHref, uiOf } = await import("../../web/ui/deeplink.js");
const { briefOf } = await import("../../web/ui/member-page.js");

test("the current brief is a shown one, else a hidden one; negative: never the run anchor", () => {
  const row = (slug, open = true) => ({ b: { slug, open } });
  const [work, release, anchor] = [row("fix"), row("release"), row("pipeline-run-anchor")];
  assert.equal(briefOf([work], [release, work]), work);
  assert.equal(briefOf([], [release]), release);
  assert.equal(briefOf([], [anchor]), undefined);
});

const LANE = `${DIR}/ship-lane-delivery`;
const kid = `member:${LANE}/delivery-impl-1`;
const isMemberHref = (h) => typeof h === "string" && h.startsWith("#/member/member%3A");
const memberLinks = (el) => (el ? el.all("a") : []).filter((a) => isMemberHref(a.getAttribute("href")));

test("memberHref is the member page, and the page's address reads back as that member", () => {
  const id = member("architect");
  assert.equal(memberHref(id), `#/member/${encodeURIComponent(id)}`);
  assert.deepEqual([uiOf(memberHref(id)).view, uiOf(memberHref(id)).selected], ["member", id]);
});

test("Overview: every member row's name, and the feed's from and to, link to the member's page", async () => {
  const m = await mount({ view: "overview" });
  await until(() => find(m.root, "li.ov-member").length > 0, "the member rows");
  for (const row of find(m.root, "li.ov-member")) {
    const links = memberLinks(row);
    assert.equal(links.length, 1, `one name link in ${text(row)}`);
    assert.ok(text(row).startsWith(text(links[0])) || text(row).includes(text(links[0])), "the link is the name");
  }
  await until(() => memberLinks(find(m.root, "section.feed")[0]).length > 0, "a feed row's member link");
});

test("Graph: the member summary's name links to the page, and so does Open page", async () => {
  const m = await mount({ view: null });
  m.store.setUi({ view: "graph", selected: member("architect") });
  await until(() => find(m.root, "section.msum").length === 1, "the summary");
  const sum = find(m.root, "section.msum")[0];
  assert.equal(sum.all("h2")[0].all("a")[0].getAttribute("href"), memberHref(member("architect")));
  assert.equal(sum.all("a.msum-open")[0].getAttribute("href"), memberHref(member("architect")));
});

test("Timeline: every lane label links to its member's page", async () => {
  const m = await mount({ view: "timeline" });
  await until(() => find(m.root, "a.lane-name").length > 0, "the lane labels");
  for (const a of find(m.root, "a.lane-name")) {
    assert.ok(isMemberHref(a.getAttribute("href")), `${text(a)} links to a member page`);
  }
});

test("Conversation: every rail name links to its member's page; the row still picks the conversation", async () => {
  const m = await mount({ view: "conversation" });
  await until(() => find(m.root, "li.rail-row").length > 0, "the rail");
  for (const row of find(m.root, "li.rail-row")) {
    assert.equal(memberLinks(row).length, 1, `a name link in ${text(row)}`);
  }
  const second = find(m.root, "li.rail-row")[1];
  const id = decodeURIComponent(second.all("a")[0].getAttribute("href").slice("#/member/".length));
  second.all("button.rail-hit")[0].fire("click");
  await until(() => m.store.state.ui.selected === id, "the second member picked");
});

test("Inbox: the blocked-member row links to the member's page", async () => {
  const m = await mount({ view: null });
  m.store.setUi({ view: "inbox", inboxTab: "attention", team: null });
  await until(() => find(m.root, "li.inbox-row").some((r) => memberLinks(r).length > 0), "a blocked member's link");
});

test("Board: a held tile's holder links to the member's page", async () => {
  Object.defineProperty(globalThis, "localStorage", { value: { data: { "ah.plans.layout": "board" }, getItem(k) { return this.data[k] ?? null; }, setItem(k, v) { this.data[k] = v; } }, configurable: true });
  try {
    const m = await mount({ view: null });
    m.store.setUi({ view: "board", team: `team:${DIR}/ship` });
    const holders = () => find(m.root, "span.kb-who").flatMap((w) => w.all("a"));
    await until(() => holders().length > 0, "a holder link");
    assert.ok(holders().every((a) => isMemberHref(a.getAttribute("href"))));
  } finally {
    delete globalThis.localStorage;
  }
});

test("the member page of a lane member: header with team, lane and lead links, Now, sections and links", async () => {
  const m = await mount({ view: null });
  m.store.setUi({ view: "member", selected: kid, team: `team:${LANE}` });
  await until(() => find(m.root, "article.member-page").length === 1, "the page");
  const page = () => find(m.root, "article.member-page")[0];
  await until(() => /takes direction from/.test(text(page())), "the lead");
  assert.equal(find(m.root, "aside.rail").length, 0, "the page has no rail");
  const where = page().all("p.mh-where")[0];
  const hrefs = where.all("a").map((a) => a.getAttribute("href"));
  assert.equal(hrefs[0], `#/overview?team=${encodeURIComponent(`team:${LANE}`)}`, "the team links to its Overview");
  assert.equal(hrefs[1], `#/board?team=${encodeURIComponent(`team:${DIR}/ship`)}&lane=lane-delivery`, "the lane links to the lane-filtered Board");
  assert.equal(hrefs[2], memberHref(`member:${LANE}/so-lane-delivery`), "the lead links to its page");
  assert.equal(page().all("h1").length, 1);
  for (const h of ["Now", "Context", "Current brief", "Latest report", "Messages", "Files changed", "History", "Links", "Actions"]) {
    assert.ok(page().all("h2").some((x) => text(x).startsWith(h)), `section ${h}`);
  }
  const links = page().all("ul.mp-links")[0].all("a").map((a) => a.getAttribute("href"));
  assert.deepEqual(links, [
    `#/conversation/${encodeURIComponent(kid)}`,
    `#/graph/${encodeURIComponent(kid)}`,
    `#/timeline/${encodeURIComponent(kid)}`,
    `#/board?team=${encodeURIComponent(`team:${LANE}`)}&owner=delivery-impl-1`,
  ]);
  assert.match(page().all("p.mp-back")[0].all("a")[0].getAttribute("href"), /^#\/overview\?team=/, "back to the team's Overview");
});

test("the member page reads the member's messages through the member filter, and a row opens the pair sheet", async () => {
  const m = await mount({ view: null });
  m.store.setUi({ view: "member", selected: member("architect") });
  await until(() => m.log.lists.some((l) => l.params.member === member("architect") && l.path.includes("list:exchanges:team:")), "the member-filtered list");
  await until(() => find(m.root, "button.mp-msg").length > 0, "a message row");
  find(m.root, "button.mp-msg")[0].fire("click");
  await until(() => Array.isArray(m.store.state.ui.pair) && m.store.state.ui.pair[0] === member("architect"), "the pair sheet");
});

test("a gone member's page renders from what is known, with state gone and its actions disabled with the reason", async () => {
  const m = await mount({ view: null });
  const gone = member("left-yesterday");
  m.store.setUi({ view: "member", selected: gone });
  await until(() => find(m.root, "article.member-page").length === 1, "the page");
  const page = find(m.root, "article.member-page")[0];
  assert.equal(page.getAttribute("data-state"), "gone");
  await until(() => /member is gone/.test(text(page)), "the reason the actions are disabled");
  assert.ok(page.all("button.action").every((b) => b.disabled === true || b.getAttribute("disabled") !== null), "every action disabled");
  assert.equal(m.store.state.ui.view, "member", "the page stays: no 'left the team' bounce");
});

test("a member whose team is not held reads as gone from its id, not as loading (review N3)", async () => {
  const { memberModel } = await import("../../web/ui/member-summary.js");
  const m = memberModel(new Map(), {}, "member:d/t/x");
  assert.equal(m.state, "gone");
  assert.equal(m.name, "x");
  assert.equal(m.teamId, "team:d/t");
  assert.equal(m.teamHref, `#/overview?team=${encodeURIComponent("team:d/t")}`);
});
