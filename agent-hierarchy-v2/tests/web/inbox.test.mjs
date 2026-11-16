// ah-ui-ia U2: the Inbox. On the mock `team` scenario: one decision waiting, one blocked member and one stuck open task (the orchestrator's).
import { test } from "node:test";
import assert from "node:assert/strict";

process.env.AH_MOCK_FIXTURE = "team";
const { mountApp, until, find, text, DIR } = await import("./mount-app.mjs");
const { needsCount, needsState, inboxTeams } = await import("../../web/ui/select.js");

const TEAM = `team:${DIR}/ship`;
const num = (el) => Number(text(el).replace(/\D+/g, ""));
const tab = (m, label) => find(find(m.root, "nav.inbox-tabs")[0], "button").find((b) => text(b).trim().startsWith(label));

// atp-1736 (Orchestrator ruling, request 20261113-152814-1cyj): Needs you is waiting_on_you; the blocked member shows as "1 blocked".
test("U2: with one decision, one blocked member and one stuck task, the status bar, the header button and the Inbox all show Needs you 1 · 1 blocked", async () => {
  const m = await mountApp({ view: "inbox", ui: { view: "inbox", inboxTab: "attention", team: TEAM } });
  await until(() => find(m.root, "li.qa-card").length === 1 && find(m.root, "li.inbox-row").length === 2, "the decision card and the two alert rows");
  assert.equal(needsCount(m.store.state.slices, TEAM), 1, "the decision; the blocked member is blocked, the stuck task the orchestrator's");
  assert.equal(needsState(m.store.state.slices, TEAM).blocked, 1);
  assert.equal(num(find(find(m.root, "button.inbox-btn")[0], "span.num")[0]), 1, "header Inbox · N");
  assert.equal(num(find(find(m.root, "button.needs")[0], "strong")[0]), 1, "status bar Needs you N");
  assert.match(text(find(m.root, "button.needs")[0]), /· 1 blocked/, "status bar blocked");
  assert.equal(num(find(tab(m, "Attention"), "span.num")[0]), 1, "the Attention tab");
  assert.match(text(tab(m, "Attention")), /· 1 blocked/, "the Attention tab's blocked");
  const orchRows = find(find(m.root, "ul").find((u) => (u.getAttribute("aria-labelledby") ?? "").startsWith("inbox-orch-")), "li.inbox-row");
  assert.equal(orchRows.length, 1, "the stuck task under For the orchestrator");
  assert.equal(find(m.root, "li.qa-card").length + find(m.root, "li.inbox-row").length - orchRows.length, 2, "the Inbox list");
});

test("U2: a blocked member links to its member page, a stuck task to its item on the Board", async () => {
  const m = await mountApp({ view: "inbox", ui: { view: "inbox", inboxTab: "attention", team: TEAM } });
  await until(() => find(m.root, "a.inbox-link").length === 2, "the two linked rows");
  const hrefs = find(m.root, "a.inbox-link").map((a) => a.getAttribute("href"));
  assert.ok(hrefs.includes(`#/member/${encodeURIComponent(`member:${DIR}/ship/implementor-2`)}`), hrefs.join(" "));
  assert.ok(hrefs.includes(`#/board?team=${encodeURIComponent(TEAM)}&item=${encodeURIComponent(`exchange:${DIR}/20261031-1254-td18`)}`), hrefs.join(" "));
  const kinds = find(m.root, "li.inbox-row").flatMap((r) => find(r, "span.need-kind")).map(text);
  assert.deepEqual(kinds.sort(), ["Blocked", "Stuck"], "each row names its kind in words, not colour alone");
});

test("U2: a decision card starts with its default option picked, so Answer is enabled (L6)", async () => {
  const m = await mountApp({ view: "inbox", ui: { view: "inbox", inboxTab: "attention", team: TEAM } });
  await until(() => find(m.root, "li.qa-card").length === 1, "the decision card");
  const on = find(m.root, "label.qa-opt").filter((l) => l.getAttribute("class") === "qa-opt on");
  assert.equal(on.length, 1, "one option picked");
  assert.ok(text(on[0]).includes("(default)"), "the picked one is the default");
  const answer = find(m.root, "button").find((b) => b.getAttribute("type") === "submit");
  assert.equal(answer.getAttribute("class"), "action primary");
  assert.equal(answer.disabled === true || answer.attrs?.has("disabled"), false, "Answer is enabled");
});

test("U2: the all-teams Inbox is built: grouped by team, with each team's count, and no placeholder", async () => {
  const m = await mountApp({ view: "inbox", ui: { view: "inbox", inboxTab: "attention", team: null } });
  await until(() => find(m.root, "h2.inbox-head").length > 0, "a team heading");
  assert.equal(text(m.root).includes("not built yet"), false);
  assert.ok(text(find(m.root, "h2.inbox-head")[0]).includes("Needs you 1 · 1 blocked"));
  assert.equal(num(find(find(m.root, "button.inbox-btn")[0], "span.num")[0]), needsCount(m.store.state.slices, null));
});

test("U2: inboxTeams scopes to a team and its confirmed lanes; all teams puts each lane after its parent", () => {
  const sum = (name, lane) => [`team-summary:d/${name}`, { id: `team-summary:d/${name}`, body: { team: `team:d/${name}`, name, running: true, needs_you: 1, waiting_on_you: 1, alerts: [], ...(lane ? { lane: { parent_team: lane, stream: "s" } } : null) } }];
  const slices = new Map([sum("top"), sum("top-lane-a", "top"), sum("other"), sum("other-lane", "nope")]);
  assert.deepEqual(inboxTeams(slices, "team:d/top").map((r) => r.name), ["top", "top-lane-a"]);
  assert.equal(needsCount(slices, "team:d/top"), 2);
  assert.deepEqual(inboxTeams(slices, null).map((r) => r.name), ["other", "other-lane", "top", "top-lane-a"]);
  assert.equal(needsCount(slices, null), 4, "every team counted once");
});

test("an orchestrator-audience alert is not the user's: it does not count toward Needs you and draws no Attention row", () => {
  const since = "2026-11-01T12:00:00Z";
  const body = { team: "team:d/top", name: "top", running: true, needs_you: 1, alerts: [
    { kind: "blocked", member: "member:d/top/impl-1", since, text: "Waiting on a permission" },
    { kind: "stuck", audience: "orchestrator", exchange: "exchange:d/x", member: "member:d/top/impl-2", since, text: "No activity for 3 h" },
  ] };
  const slices = new Map([["team-summary:d/top", { id: "team-summary:d/top", body }]]);
  assert.equal(needsCount(slices, "team:d/top"), 0, "nothing waits on you");
  assert.equal(needsState(slices, "team:d/top").blocked, 1, "the blocked member; the stuck task is not counted");
  const [row] = inboxTeams(slices, "team:d/top");
  assert.deepEqual(row.items.map((c) => c.kind), ["Blocked"]);
  assert.deepEqual(row.orch.map((c) => c.kind), ["Stuck"], "kept for the orchestrator section");
});
