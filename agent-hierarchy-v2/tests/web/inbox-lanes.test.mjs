// ah-ui-ia U2 round 1 (F1), ah-ui-focus §3: the Needs-you count is the daemon's. On the mock `lanes` scenario the top team has 4 open
// questions and lane-delivery has one blocked member.
import { test } from "node:test";
import assert from "node:assert/strict";

process.env.AH_MOCK_FIXTURE = "lanes";
const { mountApp, until, find, text, DIR } = await import("./mount-app.mjs");

const TEAM = `team:${DIR}/ship`;
const num = (el) => text(el).trim();
const cards = (m) => find(m.root, "li.qa-card").length + find(m.root, "li.inbox-row").length;

// atp-1736 (Orchestrator ruling, request 20261113-152814-1cyj): Needs you counts what waits on you (the 4 cards); the blocked row
// shows beside it as "1 blocked", and the list holds both.
test("F1: on the lanes preset the status bar, Inbox · N and the Attention tab say Needs you 4 · 1 blocked over the 5 rows", async () => {
  const m = await mountApp({ view: "inbox", ui: { view: "inbox", inboxTab: "attention", team: TEAM } });
  await until(() => cards(m) === 5 && !num(find(find(m.root, "button.inbox-btn")[0], "span.num")[0]).endsWith("…"), "4 decision cards, 1 blocked row and a settled count");
  assert.equal(m.store.state.slices.get(`team-summary:${DIR}/ship`).body.needs_you, 4, "the count is the daemon's");
  assert.equal(num(find(find(m.root, "button.inbox-btn")[0], "span.num")[0]), "4", "header Inbox · N");
  assert.equal(num(find(find(m.root, "button.needs")[0], "strong")[0]), "4", "status bar Needs you N");
  assert.match(text(find(m.root, "button.needs")[0]), /· 1 blocked/, "status bar blocked");
  const attention = find(find(m.root, "nav.inbox-tabs")[0], "button")[0];
  assert.equal(num(find(attention, "span.num")[0]), "4", "the Attention tab");
  assert.match(text(attention), /· 1 blocked/, "the Attention tab's blocked");
});

test("F1: the all-teams Inbox shows a team's cards under the served count", async () => {
  const m = await mountApp({ view: "inbox", ui: { view: "inbox", inboxTab: "attention", team: null } });
  await until(() => find(m.root, "li.qa-card").length === 4, "the top team's 4 cards");
  const heads = find(m.root, "h2.inbox-head").map((h) => text(h));
  assert.ok(heads.some((h) => h.startsWith("ship") && h.includes("Needs you 4")), heads.join(" | "));
  assert.ok(heads.some((h) => h.startsWith("ship-lane-delivery") && h.includes("Needs you 0 · 1 blocked")), heads.join(" | "));
});
