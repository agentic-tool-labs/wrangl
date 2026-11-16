import { test } from "node:test";
import assert from "node:assert/strict";
import { mountApp as mount, until, find, text, DIR } from "./mount-app.mjs";

const view = await import("../../web/ui/backlog-view.js");
const { emptyOf } = view;
const { servedBacklog } = await import("../../web/mock/model.mjs");
// The outline of a body as the daemon serves it.
const outlineOf = (body) => view.outlineOf(body && servedBacklog(body));
const { redirectOf, uiOf } = await import("../../web/ui/deeplink.js");

const TEAM = `team:${DIR}/ship`;
// The body rows: every <tr> under the table but the header row and an expanded row's detail.
const rows = (m) => find(m.root, "tr").filter((r) => r.all("td").length > 0 && r.className !== "bl-more");

test("the outline runs wave, then rank, then key; Rank numbers the whole list; done and dropped are hidden; no wave reads Untriaged", () => {
  const o = outlineOf({
    items: [
      { key: "b10", title: "J", state: "next", wave: "later", rank: 1 },
      { key: "b3", title: "C", state: "not_started", wave: "Now", rank: 2 },
      { key: "b1", title: "A", state: "in_progress", wave: "now", rank: 1 },
      { key: "b2", title: "B", state: "done", wave: "now", rank: 0 },
      { key: "b4", title: "D", state: "dropped", wave: "next" },
      { key: "b9", title: "I", state: "next" },
      { key: "b5", title: "E", state: "next", wave: "next" },
      { key: "b11", title: "K", state: "next", wave: "next" },
    ],
    health: { untriaged: 1, stale: 2, due: true },
  });
  assert.deepEqual(o.rows.map((r) => `${r.n}:${r.key}:${r.waveLabel}`), ["1:b1:Now", "2:b3:Now", "3:b5:Next", "4:b11:Next", "5:b10:Later", "6:b9:Untriaged"]);
  assert.equal(o.open, 6);
  assert.deepEqual(o.health, { untriaged: 1, stale: 2, due: true });
});

test("an unheld backlog is loading; one with nothing open says so; one with rows says nothing", () => {
  assert.equal(outlineOf(undefined), null);
  assert.equal(emptyOf(null), "Loading…");
  assert.equal(emptyOf(outlineOf({ items: [{ key: "b1", title: "A", state: "done" }] })), "Nothing open in the backlog.");
  assert.equal(emptyOf(outlineOf({ items: [{ key: "b1", title: "A", state: "next" }] })), null);
});

test("the old backlog address redirects to the team's Board (ah-ui-ia §5)", () => {
  assert.equal(redirectOf(`#/backlog?team=${encodeURIComponent(TEAM)}`), `#/board?team=${encodeURIComponent(TEAM)}`);
  assert.equal(uiOf(redirectOf(`#/backlog?team=${encodeURIComponent(TEAM)}`)).view, "board");
});

test("the Backlog tab: 23 ranked rows, the columns, a row expands to outcome, issue link and ext, groom due in the header, and Plans has no backlog", async () => {
  const m = await mount({ view: "backlog", ui: { view: "backlog", team: TEAM } });
  await until(() => rows(m).length === 23, "23 rows");
  assert.deepEqual(find(m.root, "th").map(text), ["Rank", "Item", "Theme", "Wave", "Size", "Holder", "State"]);
  const first = rows(m)[0];
  assert.match(text(first), /^1▸DB speed.*b1DB\/perfNowLimplementor-1in progress$/);
  assert.equal(text(rows(m)[22].all("td")[0]), "23", "rank runs over the whole list");
  assert.match(text(find(m.root, "div.bl-bar")[0]), /groom due.*2 stale.*23 open/);
  assert.ok(find(find(m.root, "header.shell-head")[0], "span.chip").some((c) => text(c) === "groom due"), "groom due in the header");
  assert.ok(!find(find(m.root, "nav.views")[0], "button").some((b) => text(b) === "Backlog"), "Backlog is not a tab");

  const open = find(first, "button.bl-open")[0];
  assert.equal(open.getAttribute("aria-expanded"), "false");
  assert.equal(find(m.root, "tr.bl-more").length, 0);
  open.fire("click");
  await until(() => find(m.root, "tr.bl-more").length === 1, "the expanded row");
  const more = find(m.root, "tr.bl-more")[0];
  assert.equal(find(m.root, "button.bl-open")[0].getAttribute("aria-expanded"), "true");
  assert.equal(find(m.root, "button.bl-open")[0].getAttribute("aria-controls"), more.getAttribute("id"));
  assert.match(text(more), /Outcome.*SQLite.*Issue.*#458.*Ext.*github:/);
  const link = find(more, "a")[0];
  assert.match(link.getAttribute("href"), /^https:\/\/github\.com\/example\/ah2\/issues\/458$/);
  assert.equal(link.getAttribute("rel"), "noopener noreferrer");
  find(m.root, "button.bl-open")[0].fire("click");
  await until(() => find(m.root, "tr.bl-more").length === 0, "collapsed again");

  m.store.setUi({ view: "board" });
  await until(() => find(m.root, "section.plans").length === 1, "the Plans view");
  assert.equal(find(m.root, "section.bl").length, 0, "Plans keeps no backlog section");
});
