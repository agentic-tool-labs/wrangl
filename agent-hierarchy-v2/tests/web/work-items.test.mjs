import { test } from "node:test";
import assert from "node:assert/strict";

// Work items on the Board (ah-work-items §10, S5b): the id chip, the blocked-by marker, the item panel's timeline, the external link's
// scheme and rel, and project and goal rows left out of the Backlog column. AH_MOCK_WORK serves the registry's fields.
process.env.AH_MOCK_WORK = "1";
const { backlogColumn } = await import("../../web/ui/board-kanban.js");
const { LOG_PAGE, descriptionBlocks } = await import("../../web/ui/item-log.js");
const { httpUrl, itemLogId } = await import("../../web/ui/plans-model.js");
const { mountApp: mount, until, find, text, DIR } = await import("./mount-app.mjs");

const store = { data: { "ah.plans.layout": "board" }, getItem(k) { return this.data[k] ?? null; }, setItem(k, v) { this.data[k] = v; } };
Object.defineProperty(globalThis, "localStorage", { value: store, configurable: true });

const board = async () => {
  const m = await mount({ view: "board" });
  await until(() => find(m.root, "article.kb-card").length > 0, "the board");
  return m;
};
const panel = (m) => find(m.root, "section.kb-panel")[0];
// Opens the panel of the card or row showing this work id.
const open = async (m, work) => {
  const card = () => find(m.root, "article.kb-card").find((c) => c.all("span.chip").some((s) => text(s) === work));
  await until(card, `the card of ${work}`);
  card().fire("click");
  await until(() => panel(m) && text(panel(m)).includes(work), `the panel of ${work}`);
  return panel(m);
};
const opened = async (m, work) => {
  const p = await open(m, work);
  await until(() => p.all("h3").some((h) => text(h) === "Timeline") && !text(p).includes("Loading…"), "the timeline");
  return panel(m);
};

test("only http(s) is a link", () => {
  for (const good of ["https://example.com/a", "http://example.com/"]) {
    assert.equal(httpUrl(good), good);
  }
  for (const bad of ["javascript:alert(1)", " javascript:alert(1)", "java\tscript:alert(1)", "data:text/html,x", "file:///etc/passwd", "example.com", "", null, undefined]) {
    assert.equal(httpUrl(bad), null, String(bad));
  }
});

test("the slice of a work id is its number, whatever the prefix", () => {
  assert.equal(itemLogId("d", "ah-12"), "item-log:d/12");
  assert.equal(itemLogId("d", "my-repo-3"), "item-log:d/3");
  assert.equal(itemLogId("d", null), null);
  assert.equal(itemLogId(null, "ah-12"), null);
});

test("the Backlog column leaves out project and goal rows, and keeps every other kind", () => {
  const row = (key, kind) => ({ key, kind, title: key, open: true, wave: "now" });
  const rows = backlogColumn({ items: [row("p", "project"), row("g", "goal"), row("e", "epic"), row("d", "deliverable"), row("t", "task"), row("x", undefined)] }).flatMap((g) => g.rows.map((r) => r.key));
  assert.deepEqual(rows, ["e", "d", "t", "x"]);
});

test("mounted: a project row is not drawn in the Backlog column, though the backlog serves it", async () => {
  const m = await board();
  await until(() => find(m.root, "article.kb-brow").length > 0, "the backlog column");
  assert.ok(m.store.state.slices.get(`backlog:${DIR}`).body.items.some((i) => i.kind === "project"), "served");
  const titles = find(m.root, "article.kb-brow").map(text).join("|");
  assert.ok(!titles.includes("The beta") && !titles.includes("Ship it"), titles);
});

test("the Board's Projects and goals button brings those rows into the Backlog column, and negative: pressing it again takes them out", async () => {
  const m = await board();
  await until(() => find(m.root, "article.kb-brow").length > 0, "the backlog column");
  const press = () => find(m.root, "button").find((b) => text(b) === "Projects and goals").fire("click");
  const shown = () => find(m.root, "article.kb-brow").map(text).join("|");
  press();
  await until(() => shown().includes("The beta") && shown().includes("Ship it"), "the project and goal rows");
  press();
  await until(() => !shown().includes("The beta"), "the rows gone");
});

test("a card carries its id chip and, only with open blockers, the blocked-by marker", async () => {
  const m = await board();
  await until(() => find(m.root, "div.kb-flag").some((f) => text(f).includes("blocked by 2")), "the marker");
  const cards = find(m.root, "article.kb-card");
  const marked = cards.filter((c) => c.all("div.kb-flag").some((f) => /blocked by \d/.test(text(f))));
  assert.ok(marked.length > 0 && marked.length < cards.length, "some cards, not all");
  assert.ok(marked.every((c) => /, ah-\d+, blocked by 2/.test(c.getAttribute("aria-label"))), "the name says so too");
});

test("a card with no registry fields looks as it did: no chip, no marker, no timeline", async () => {
  const m = await board();
  const id = [...m.store.state.slices.keys()].find((k) => k.startsWith("plan-items:") && m.store.state.slices.get(k).body.items.some((i) => i.work_id && i.state !== "done"));
  const body = JSON.parse(JSON.stringify(m.store.state.slices.get(id).body));
  body.items = body.items.map(({ work_id, blocked_by_open, ext_id, ext_url, ...rest }) => ({ ...rest, title: `plain ${rest.title}` }));
  m.model.publish([{ id, as_of: Date.now(), body }]);
  const plain = () => find(m.root, "article.kb-card").find((c) => text(c).includes("plain "));
  await until(plain, "the plain card");
  assert.equal(plain().all("span.chip").filter((s) => /^ah-/.test(text(s))).length, 0);
  assert.ok(!text(plain()).includes("blocked by"));
  plain().fire("click");
  await until(() => panel(m), "the panel");
  assert.ok(!panel(m).all("h3").some((h) => text(h) === "Timeline"));
  assert.ok(panel(m).all("h3").some((h) => text(h) === "History"), "the existing history stays");
  assert.ok(!panel(m).all("dt").some((d) => ["Work id", "Ext id"].includes(text(d))));
});

test("the panel: Timeline beside History, newest first and capped, Show all, and what is not served", async () => {
  const m = await board();
  const p = await opened(m, "ah-100");
  const heads = p.all("h3").map(text);
  assert.ok(heads.includes("History") && heads.includes("Timeline") && heads.indexOf("History") < heads.indexOf("Timeline"), heads.join());
  const rows = () => panel(m).all("ol.wi-log")[0].all("li");
  assert.equal(rows().length, LOG_PAGE, "capped");
  const times = rows().map((r) => Date.parse(r.all("time")[0].getAttribute("datetime")));
  assert.deepEqual(times, [...times].sort((a, b) => b - a), "newest first");
  assert.ok(text(p).includes("37 older not shown"));
  assert.ok(text(p).includes("ah work log ah-100 --all"), "a command that does show them");
  panel(m).all("button").find((b) => text(b) === "Show all 200").fire("click");
  await until(() => rows().length === 200, "all entries");
});

test("an entry's row stays its own when a new entry shifts the capped window", async () => {
  const m = await board();
  await opened(m, "ah-100");
  const id = `item-log:${DIR}/100`;
  const entry = (i) => ({ ts_ms: 1_000_000 + i, type: "note", title: `entry ${i}`, by: null, body: "b" });
  const log = (from) => ({ id: "ah-100", entries: Array.from({ length: 200 }, (_, i) => entry(from + i)), more: 0, links: { blocked_by: [], blocks: [] }, attachments: [], children: [] });
  const row = (i) => panel(m).all("ol.wi-log")[0].all("li").find((r) => text(r).includes(`entry ${i}D`));
  m.model.publish([{ id, as_of: Date.now(), body: log(0) }]);
  await until(() => row(195), "the first window");
  const before = row(195);
  m.model.publish([{ id, as_of: Date.now(), body: log(1) }]);
  await until(() => row(200), "the new entry");
  assert.equal(row(195), before, "the same row, so its open details stays on this entry");
});

test("the panel: links and attachments; only an http(s) attachment is a link, with rel", async () => {
  const m = await board();
  const p = await opened(m, "ah-100");
  const dt = p.all("dt").map(text);
  assert.ok(dt.includes("Blocked by") && dt.includes("Blocks") && dt.includes("Children"), dt.join());
  const links = p.all("ul.wi-attach")[0].all("a");
  assert.deepEqual(links.map((a) => a.getAttribute("href")), ["https://example.com/design"]);
  assert.equal(links[0].getAttribute("rel"), "noopener noreferrer");
  assert.equal(links[0].getAttribute("target"), "_blank");
  assert.ok(text(p).includes("javascript:alert(1)"), "shown as text");
});

test("the ext id: a link with rel when ext_url is http(s), otherwise plain text", async () => {
  const m = await board();
  const dd = (p) => {
    const i = p.all("dt").findIndex((d) => text(d) === "Ext id");
    return p.all("dd")[i];
  };
  const safe = await open(m, "ah-100");
  const a = dd(safe).all("a")[0];
  assert.equal(a.getAttribute("href"), "https://linear.app/example/issue/ABC-1");
  assert.equal(a.getAttribute("rel"), "noopener noreferrer");
  assert.equal(a.getAttribute("target"), "_blank");
  const evil = await open(m, "ah-101");
  assert.equal(dd(evil).all("a").length, 0, "javascript: is no link");
  assert.equal(text(dd(evil)), "evil:1");
  const plain = await open(m, "ah-102");
  assert.equal(dd(plain).all("a").length, 0);
  assert.equal(text(dd(plain)), "jira:OPS-9");
});

test("the Backlog page: ext_url links with rel; not http(s) or absent, the issue link stays", async () => {
  const m = await mount({ view: "backlog" });
  await until(() => find(m.root, "button.bl-open").length > 0, "the backlog page");
  const rowOf = (key) => find(m.root, "button.bl-open").find((b) => b.all("span.bl-id").some((s) => text(s) === key));
  for (const key of ["b1", "b2", "b3"]) {
    rowOf(key).fire("click");
  }
  await until(() => find(m.root, "tr.bl-more").length === 3, "the open rows");
  const [one, two, three] = find(m.root, "tr.bl-more");
  const a = one.all("a")[0];
  assert.equal(a.getAttribute("href"), "https://linear.app/example/issue/ABC-1");
  assert.equal(a.getAttribute("rel"), "noopener noreferrer");
  assert.ok(text(a).startsWith("ABC-1"));
  assert.equal(two.all("a").length, 0, "javascript: is no link, and b2 has no issue");
  assert.ok(text(two).includes("evil:1"));
  assert.equal(three.all("a")[0].getAttribute("href"), "https://github.com/example/ah2/issues/258", "the issue link stays");
  assert.ok(text(three).includes("jira:OPS-9"));
});

test("no such item reads as no timeline; the slice is leased while the panel is open and released on close", async () => {
  const m = await board();
  const id = `item-log:${DIR}/100`;
  await opened(m, "ah-100");
  await until(() => m.log.polls.at(-1)?.split(",").includes(id), "the lease");
  m.model.publish([{ id, as_of: Date.now(), body: null }]);
  await until(() => panel(m) && text(panel(m)).includes("No timeline"), "the null body");
  panel(m).all("button.close")[0].fire("click");
  await until(() => !m.log.polls.at(-1)?.split(",").includes(id), "the release");
});

test("a description: all-bold lines are headings, '- ' lines a list, other lines keep their breaks, inline ** is bold, a lone ** stays", () => {
  assert.deepEqual(descriptionBlocks("**Summary**\nOne line\nand a next\n\n- a\n- **b** c\n\n**x** and **y**\n2 ** 3"), [
    { kind: "head", text: "Summary" },
    { kind: "para", text: "One line\nand a next" },
    { kind: "list", items: ["a", "**b** c"] },
    { kind: "para", text: "**x** and **y**\n2 ** 3" },
  ]);
  assert.deepEqual(descriptionBlocks("a\r\n\r\n\r\nb"), [{ kind: "para", text: "a" }, { kind: "para", text: "b" }]);
});

test("the description sits above Notes in the dialog, and HTML in it is only text", async () => {
  const m = await board();
  const p = await opened(m, "ah-100");
  const id = `item-log:${DIR}/100`;
  const evil = "<img src=x onerror=alert(1)>";
  const body = (description) => ({ id: "ah-100", description, entries: [], more: 0, links: { blocked_by: [], blocks: [] }, attachments: [], children: [] });
  m.model.publish([{ id, as_of: Date.now(), body: body(`**Why ${evil}**\n${evil}\nline two\n\n- **${evil}** item`) }]);
  await until(() => panel(m).all("div.id-desc").length === 1 && text(panel(m).all("div.id-desc")[0]).includes("line two"), "the section");
  const d = panel(m).all("div.id-desc")[0];
  assert.equal(d.all("img").length, 0, "no element from the text");
  assert.equal(d.all("a").length, 0, "no link");
  assert.equal(d.all("h4")[0].textContent, `Why ${evil}`);
  assert.equal(d.all("p")[0].textContent, `${evil}\nline two`, "its line breaks kept");
  assert.equal(d.all("li")[0].all("strong")[0].textContent, evil);
  const titles = panel(m).all("h3").map(text);
  assert.ok(titles.includes("Description"), titles.join());
  assert.ok(!titles.includes("Notes") || titles.indexOf("Description") < titles.indexOf("Notes"), titles.join());
  assert.ok(titles.indexOf("Description") < titles.indexOf("Timeline"), titles.join());
  assert.ok(p, "opened");
});

for (const [label, description] of [["null", null], ["blank", "  \n "], ["absent", undefined]]) {
  test(`a ${label} description draws no section`, async () => {
    const m = await board();
    await opened(m, "ah-100");
    const id = `item-log:${DIR}/100`;
    m.model.publish([{ id, as_of: Date.now(), body: { id: "ah-100", description, entries: [], more: 0, links: { blocked_by: [], blocks: [] }, attachments: [], children: [] } }]);
    await until(() => text(panel(m)).includes("No entries"), "the new body");
    assert.equal(panel(m).all("div.id-desc").length, 0);
    assert.ok(!panel(m).all("h3").map(text).includes("Description"));
  });
}

test("closing the dialog releases the slice both parts held", async () => {
  const m = await board();
  const id = `item-log:${DIR}/100`;
  await opened(m, "ah-100");
  await until(() => m.log.polls.at(-1)?.split(",").includes(id), "the lease");
  panel(m).all("button.close")[0].fire("click");
  await until(() => !m.log.polls.at(-1)?.split(",").includes(id), "the release");
});
