import test from "node:test";
import assert from "node:assert/strict";
import { DIR, find, member, mountApp, text, until } from "./mount-app.mjs";

const { idsOf } = await import("../../web/ui/transcript-rows.js");

const { laneChildren, memberIds, firstTeam } = await import("../../web/ui/select.js");
const { filesChanged } = await import("../../web/ui/graph-panel.js");

test("files changed says git's error and the whole count, never the cut-short list", () => {
  assert.equal(filesChanged({ files: [], files_total: 0, error: "git timed out" }), "unknown: git timed out");
  assert.equal(filesChanged({ files: ["a"], files_total: 812, truncated: true, error: null }), 812);
});

test("negative: files changed with no error is the count, and no slice says nothing", () => {
  assert.equal(filesChanged({ files: [], files_total: 0, error: null }), 0);
  assert.equal(filesChanged({ files: ["a", "b"] }), 2);
  assert.equal(filesChanged(undefined), null);
});

const panel = (m) => find(m.root, "section.gm-panel")[0];
const inPanel = (m, sel) => (panel(m) ? panel(m).all(sel) : []);
const leaf = (m, name) => find(m.root, "g.gm-leaf").find((g) => g.getAttribute("aria-label").includes(` ${name},`));

test("selecting a member in Map opens the member summary: its name and Open page link to the member page; closing it shows the feed", async () => {
  const m = await mountApp();
  leaf(m, "architect").fire("click");
  await until(() => find(m.root, "section.msum").length === 1, "the summary");
  const href = `#/member/${encodeURIComponent(member("architect"))}`;
  assert.equal(inPanel(m, "h2")[0].all("a")[0].getAttribute("href"), href, "the name is the page link");
  assert.equal(inPanel(m, "a.msum-open")[0].getAttribute("href"), href, "Open page");
  inPanel(m, "button.close")[0].fire("click");
  await until(() => find(m.root, "section.msum").length === 0, "closed");
});

// ah-ui-ia U3 round 1 (ruling on F2): the Graph rail's summary keeps everything the S5 card showed.
test("selecting a member in Map leases its files and transcript header, shows tokens and worktree; closing releases them", async () => {
  const m = await mountApp();
  const files = `files:${DIR}/ship/architect`;
  const { hid } = idsOf(m.store.state, member("architect"));
  assert.ok(hid, "the mock member has a transcript header id");
  assert.equal(m.store.state.slices.has(files), false);
  leaf(m, "architect").fire("click");
  await until(() => m.store.state.slices.has(files) && m.store.state.slices.has(hid), "files and header leased");
  await until(() => /tokens\s*\d/.test(text(panel(m))), "tokens from the leased header");
  assert.match(text(panel(m)), /worktree/);
  inPanel(m, "button.close")[0].fire("click");
  await until(() => !m.store.state.slices.has(files) && !m.store.state.slices.has(hid), "released on close");
});

test("the Graph rail's summary keeps the S5 card: stream, task, context used, busy last hour, messages in the window, files changed, 12 messages, actions and its buttons", async () => {
  const m = await mountApp();
  m.store.setUi({ selected: member("@orchestrator") });
  await until(() => find(m.root, "section.msum").length === 1, "the summary");
  const dts = () => inPanel(m, "dt").map(text);
  for (const label of ["stream", "task", "context used", "busy, last hour", "messages", "tokens", "files changed"]) {
    assert.ok(dts().includes(label), `${label} in the Graph rail`);
  }
  await until(() => /\d+ in the (window|newest)/.test(text(panel(m))), "the message count in the scrubber's window");
  await until(() => inPanel(m, "button.action").length > 0, "the member's actions");
  const tabs = inPanel(m, "button.tab").map(text);
  for (const label of ["Open conversation", "Plan", "Expand"]) {
    assert.ok(tabs.includes(label), `${label} button`);
  }
  await until(() => inPanel(m, "button.gm-row").length > 3, "more than the short form's 3 messages");
  assert.ok(inPanel(m, "button.gm-row").length <= 12);
});

test("outside the Graph rail the summary keeps P6's short form: no window counts and no actions", async () => {
  const m = await mountApp();
  m.store.setUi({ view: "overview", selected: member("architect") });
  await until(() => find(m.root, "section.msum").length === 1, "the summary");
  assert.equal(inPanel(m, "dl.gm-extra").length, 0);
  assert.equal(inPanel(m, ".member-actions").length, 0);
  assert.ok(inPanel(m, "button.gm-row").length <= 3);
});

test("a message row in the Map panel opens the messages between its two members", async () => {
  const m = await mountApp();
  leaf(m, "architect").fire("click");
  await until(() => inPanel(m, "button.gm-row").length > 0, "a message row");
  const row = inPanel(m, "button.gm-row")[0];
  assert.match(row.getAttribute("aria-label"), /Open the messages between /);
  row.fire("click");
  const pair = m.store.state.ui.pair;
  assert.ok(Array.isArray(pair) && pair.length === 2 && pair.every((id) => id.startsWith("member:")), `a member pair, got ${JSON.stringify(pair)}`);
  assert.ok(pair.includes(member("architect")), "the selected member is one end");
});

test("the team card (from the team's crumb) says how many lane members Map leaves out, and says nothing when there are none", async () => {
  const m = await mountApp();
  find(m.root, "nav.gm-crumbs")[0].all("button")[0].fire("click");
  const team = firstTeam(m.store.state.slices, m.store.state.ui);
  const hidden = laneChildren(m.store.state.slices, team, memberIds(team)).length;
  await until(() => find(m.root, "section.gm-panel").length === 1, "the team card");
  const sub = text(inPanel(m, "p.sub")[0]);
  if (hidden > 0) {
    assert.match(sub, new RegExp(`${hidden} lane members? (is|are) in Flow`));
  } else {
    assert.doesNotMatch(sub, /lane member/);
  }
});
