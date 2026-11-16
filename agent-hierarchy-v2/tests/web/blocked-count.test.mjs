// atp-1749 (Orchestrator ruling, 2026-11-13; review 20261113-155641-1lcc findings 1-2): one blocked rule on web and herdr, the served
// `blocked` (needs_you − waiting_on_you), never the alert rows; a team shown only for blocked members with no alert row draws one line.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { installDom, settle } from "./dom.mjs";
import { find, text } from "./mount-app.mjs";
import { needsState, blockedText as webBlocked } from "../../web/ui/select.js";
import { deskModel, sidebarModel, sitrepModel, blockedText as herdrBlocked } from "../../herdr-desktop/mods/ah2/model.js";
const { html, render } = await import("../../web/ui/h.js");
const { InboxPage } = await import("../../web/ui/inbox.js");

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const reply = JSON.parse(readFileSync(join(root, "herdr-desktop", "fixture", "events.json"), "utf8"));
const NOW = Date.parse(reply.observed_at);

test("more alert rows than blocked members: web and herdr show the same served blocked count", () => {
  const slices = new Map(reply.slices.map((s) => [s.id, s]));
  const sum = slices.get("team-summary:a1b2c3d4/ah2-tm4");
  const since = "2026-11-01T12:00:00Z";
  const alerts = ["impl-1", "impl-2", "impl-3"].map((m) => ({ kind: "blocked", member: `member:a1b2c3d4/ah2-tm4/${m}`, since, text: `${m} waits` }));
  slices.set(sum.id, { ...sum, body: { ...sum.body, alerts } });
  assert.equal(sum.body.blocked, 1, "the fixture serves one blocked member");
  const web = needsState(slices, null).blocked;
  assert.equal(web, 1, "negative: three alert rows do not make three blocked");
  assert.equal(deskModel(slices, NOW).blocked, web, "herdr status bar and drawer");
  assert.equal(sidebarModel(slices, NOW).blocked, web, "herdr sidebar header");
  assert.equal(deskModel(slices, NOW).needing.find((t) => t.name === "ah2-tm4").blocked, web, "herdr drawer team row");
  assert.equal(herdrBlocked(web), webBlocked(web));
  assert.equal(herdrBlocked(1), " · 1 blocked");
  assert.equal(herdrBlocked(0), "", "negative: nothing at zero");
});

test("herdr's sitrep header reads the served blocked", () => {
  const id = "a1b2c3d4/ah2-tm4";
  const slices = new Map([[`team-summary:${id}`, { id: `team-summary:${id}`, body: { name: "ah2-tm4", running: true, waiting_on_you: 1, needs_you: 3, blocked: 2 } }],
    [`sitrep:${id}`, { id: `sitrep:${id}`, body: { regions: [] } }]]);
  assert.equal(sitrepModel(slices, id, NOW).blocked, 2);
});

// A store that holds the given summaries and never moves.
const still = (bodies) => ({ state: { slices: new Map(bodies.map((b) => [`team-summary:d/${b.name}`, { id: `team-summary:d/${b.name}`, body: { team: `team:d/${b.name}`, ...b } }])), lists: new Map(), ui: {} }, subscribe: () => () => {}, lease() {}, page: async () => {} });
const attention = async (bodies) => {
  const dom = installDom();
  render(html`<${InboxPage} store=${still(bodies)} teamId=${null} tab="attention" />`, dom);
  await settle();
  return dom;
};

test("a team that ended with blocked members and no alert row draws one line, so the cards equal waiting + blocked", async () => {
  const dom = await attention([{ name: "gone", running: false, ended: true, needs_you: 2, waiting_on_you: 0, blocked: 2, alerts: [] }]);
  assert.deepEqual(find(dom, "p.inbox-ended").map((p) => text(p).trim()), ["Ended with 2 blocked members"]);
  assert.match(text(find(dom, "h2.inbox-head")[0]), /Needs you 0 · 2 blocked/);
});

test("negative: a team whose blocked member has an alert row draws the row, not the ended line", async () => {
  const dom = await attention([{ name: "live", running: true, needs_you: 1, waiting_on_you: 0, blocked: 1,
    alerts: [{ kind: "blocked", member: "member:d/live/impl-1", since: "2026-11-01T12:00:00Z", text: "Waiting on a permission" }] }]);
  assert.equal(find(dom, "p.inbox-ended").length, 0);
  assert.equal(find(dom, "li.inbox-row").length, 1);
});
