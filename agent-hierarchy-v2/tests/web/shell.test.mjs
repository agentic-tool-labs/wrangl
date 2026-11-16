import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { settle } from "./dom.mjs";
import { mountApp, until, find, text, member } from "./mount-app.mjs";

const { globalKey, GLOBAL_KEYS } = await import("../../web/ui/keys.js");
const { teamSummary, teamRows } = await import("../../web/ui/select.js");
const { freshness, DISCONNECT_MS } = await import("../../web/ui/store.js");

const click = (el) => el.fire("click");
const menu = (root) => find(root, "ul.menu").flatMap((u) => u.all("button"));
const rowsOf = (root) => find(root, "tr").filter((r) => r.attrs.has("data-row"));
const button = (root, label) => find(root, "button").find((b) => text(b).trim() === label || text(b).trim().startsWith(label));
const key = (store, k, extra = {}) => globalKey({ key: k, target: globalThis.document.body, preventDefault() {}, ...extra }, store, globalThis.document);

test("the page lands on All teams: a row per team, the Needs you rail, the footer count and no perf strip", async () => {
  const m = await mountApp({ view: null });
  await until(() => rowsOf(m.root).length > 0, "the team rows");
  assert.ok(rowsOf(m.root).some((r) => /ship/.test(text(r))));
  assert.equal(find(m.root, "aside.teams-rail").length, 1);
  assert.match(text(find(m.root, "footer.shell-foot")[0]), /\d+ teams? · \d+ running/);
  assert.equal(find(m.root, "section.feed").length, 0, "the live feed belongs to a team");
  assert.equal(find(m.root, ".perf-strip").length, 0);
  assert.equal(find(m.root, "header.shell-head").length, 1);
  assert.match(text(find(m.root, ".switch-btn")[0]), /^All teams/);
});

test("a team row opens that team's Overview and keeps no selection", async () => {
  const m = await mountApp({ view: null });
  await until(() => rowsOf(m.root).length > 0, "the team rows");
  m.store.setUi({ selected: member("reviewer") });
  const row = rowsOf(m.root).find((r) => /ship/.test(text(r)));
  click(row);
  assert.equal(m.store.state.ui.view, "overview");
  assert.equal(m.store.state.ui.team, "team:a1b2c3d4/ship");
  assert.equal(m.store.state.ui.selected, null);
  await until(() => find(m.root, "li.ov-member").length > 0, "the Overview");
});

test("the switcher lists All teams and the teams, keeps the view on a team change and clears the selection", async () => {
  const m = await mountApp({ view: "graph" });
  m.store.setUi({ selected: member("reviewer") });
  click(find(m.root, "button.switch-btn")[0]);
  await until(() => find(m.root, "ul.menu").length === 1, "the menu");
  const items = menu(m.root).map((b) => text(b).trim());
  assert.equal(items[0], "All teams");
  assert.ok(items.some((t) => t.startsWith("ship")));
  click(menu(m.root).find((b) => text(b).trim().startsWith("ship")));
  assert.equal(m.store.state.ui.view, "graph", "choosing a team keeps the current view");
  assert.equal(m.store.state.ui.team, "team:a1b2c3d4/ship");
  assert.equal(m.store.state.ui.selected, null, "a team change clears the selection");
  assert.equal(m.store.state.ui.menu, null);
  await until(() => find(m.root, "ul.menu").length === 0, "the menu to close");
  click(find(m.root, "button.switch-btn")[0]);
  await until(() => find(m.root, "ul.menu").length === 1, "the menu again");
  click(menu(m.root)[0]);
  assert.equal(m.store.state.ui.view, "teams");
});

test("a team that is not listed opens All teams with a toast; a listed one does not", async () => {
  const m = await mountApp({ view: "graph" });
  m.store.setUi({ team: "team:a1b2c3d4/ship" });
  await settle();
  assert.equal(m.store.state.ui.view, "graph");
  assert.equal(find(m.root, "p.toast").length, 0);
  m.store.setUi({ team: "team:a1b2c3d4/nosuch" });
  await until(() => m.store.state.ui.view === "teams", "All teams");
  await until(() => find(m.root, "p.toast").length === 1, "the toast");
  assert.match(text(find(m.root, "p.toast")[0]), /team nosuch not found/);
  assert.equal(m.store.state.ui.team, null);
});

test("the Performance strip is off until the Settings menu turns it on; focus moves into the menu and the theme is a radio", async () => {
  const m = await mountApp({ view: "graph" });
  assert.equal(find(m.root, ".perf-strip").length, 0);
  click(find(m.root, "button.more-btn")[0]);
  await until(() => find(m.root, "ul.menu").length === 1, "the menu");
  assert.equal(text(find(m.root, "button.more-btn")[0]), "Settings");
  // ok(a === b), never equal(a, b): a failing equal on two DOM nodes diffs their whole (circular) tree without bound.
  assert.ok(globalThis.document.activeElement === menu(m.root)[0], "focus moves to the first item (P1)");
  const radios = find(m.root, "ul.menu")[0].all("button").filter((b) => b.getAttribute("role") === "menuitemradio");
  // The theme, then the look for each theme slot (ah-ui-looks §6): House is in both.
  assert.deepEqual(radios.map((b) => text(b).trim()), ["System", "Light", "Dark", "House", "Light Inky", "Light Emissive", "House", "Dark Emissive"]);
  const checked = (list) => list.filter((b) => b.getAttribute("aria-checked") === "true").map((b) => text(b).trim());
  assert.equal(checked(radios.slice(0, 3)).length, 1, "one theme is checked");
  assert.deepEqual([checked(radios.slice(3, 6)), checked(radios.slice(6))], [["Light Inky"], ["Dark Emissive"]], "each slot checks its default look");
  assert.equal(menu(m.root).some((b) => /Diagnostics|Theme:/.test(text(b))), false);
  click(menu(m.root).find((b) => text(b).trim() === "Performance strip"));
  await until(() => find(m.root, ".perf-strip").length === 1, "the strip");
  assert.equal(m.store.state.ui.diagnostics, true);
});

test("1–5 switch the five tabs and 6–0 are not keys; text fields, modifiers and dialogs are left alone", async () => {
  const m = await mountApp({ view: "graph" });
  const { store } = m;
  for (const [k, view] of [["1", "overview"], ["2", "board"], ["3", "graph"], ["4", "timeline"], ["5", "conversation"]]) {
    assert.equal(key(store, k), true);
    assert.equal(store.state.ui.view, view);
  }
  for (const k of ["6", "7", "8", "9", "0"]) {
    assert.equal(key(store, k), false, `${k} is not a page key`);
  }
  assert.equal(store.state.ui.view, "conversation");
  assert.deepEqual(find(find(m.root, "nav.views")[0], "button").map((b) => text(b)), ["Overview", "Board", "Graph", "Timeline", "Conversation"]);
  store.setUi({ view: "conversation", help: true });
  assert.equal(key(store, "1"), false, "a dialog owns the keys");
  assert.equal(store.state.ui.view, "conversation");
  store.setUi({ help: false });
  assert.equal(key(store, "1", { target: { localName: "input" } }), false, "a text field keeps its digits");
  assert.equal(key(store, "1", { ctrlKey: true }), false);
  assert.equal(key(store, "1", { metaKey: true }), false);
  assert.equal(key(store, "1", { altKey: true }), false);
  assert.equal(key(store, "p"), false, "a plain letter is never ours");
  assert.equal(store.state.ui.view, "conversation");
});

test("Escape clears the selection, or closes a menu first; the arrows step through the members", async () => {
  const m = await mountApp({ view: "graph" });
  const { store } = m;
  assert.equal(key(store, "ArrowDown"), true);
  const first = store.state.ui.selected;
  assert.ok(first);
  key(store, "ArrowDown");
  assert.notEqual(store.state.ui.selected, first);
  key(store, "ArrowUp");
  assert.equal(store.state.ui.selected, first);
  store.setUi({ menu: "more" });
  assert.equal(key(store, "Escape"), true);
  assert.equal(store.state.ui.menu, null);
  assert.equal(store.state.ui.selected, first, "the menu closed, the selection stayed");
  assert.equal(key(store, "Escape"), true);
  assert.equal(store.state.ui.selected, null);
  assert.equal(key(store, "Escape"), false, "nothing left to clear");
});

const { inMemberScope, MEMBER_SCOPE_KEYS } = await import("../../web/ui/keys.js");
const { keyMatches } = await import("../../web/ui/member-actions.js");

const el = (localName, cls = "", parent = null) => ({ localName, parentNode: parent, getAttribute: (k) => (k === "class" ? cls : null) });
const panelButton = () => el("button", "", el("div", "member-actions", el("section", "panel", el("aside", "rail"))));

test("global keys are not acted on inside the member panel, except Escape; the same keys act at the root", async () => {
  const m = await mountApp({ view: "graph" });
  const { store } = m;
  const inPanel = panelButton();
  assert.equal(inMemberScope(inPanel), true);
  assert.equal(inMemberScope(globalThis.document.body), false, "the nearest non-member element");
  assert.equal(inMemberScope(el("section", "feed")), false, "a section that is not the panel");
  store.setUi({ selected: member("reviewer"), diagnostics: false, menu: null });
  const before = JSON.stringify(store.state.ui);
  for (const k of GLOBAL_KEYS.filter((k) => !MEMBER_SCOPE_KEYS.includes(k))) {
    assert.equal(key(store, k, { target: inPanel }), false, `${k} in the panel`);
  }
  assert.equal(JSON.stringify(store.state.ui), before, "nothing changed");
  assert.equal(key(store, "D"), true, "the same key at the root acts");
  assert.equal(store.state.ui.diagnostics, true);
  assert.equal(key(store, "Escape", { target: inPanel }), true, "Escape is the page's inside the panel too");
  assert.equal(store.state.ui.selected, null);
});

// The server's member-action keys, read from the daemon's source, as e.key values.
const NAMED = { enter: "Enter", left: "ArrowLeft", right: "ArrowRight" };
const serverKeys = () => {
  const source = readFileSync(new URL("../../native/src/Ah.Watch/ViewBuilder.Actions.cs", import.meta.url), "utf8");
  const raw = new Set();
  for (const m of source.matchAll(/(?:Read|Copy|Terminal|Ask|NodeView)Action\("[^"]+", "[^"]+", "([^"]+)"/g)) {
    raw.add(m[1]);
  }
  for (const m of source.matchAll(/^\s*\("[\w.]+", "[^"]+", "([^"]+)", (?:null|"([^"]+)"),/gm)) {
    raw.add(m[1]);
    if (m[2]) {
      raw.add(m[2]);
    }
  }
  return raw;
};

test("Escape is not a server action key, and the check can fail", () => {
  const server = serverKeys();
  assert.ok(server.has("enter") && server.has("y") && server.has("S") && server.has("H"), "the source was read");
  assert.equal([...server].map((k) => NAMED[k] ?? k).includes("Escape"), false);
  assert.deepEqual(MEMBER_SCOPE_KEYS, ["Escape"]);
  assert.equal([...server, "Escape"].map((k) => NAMED[k] ?? k).includes(MEMBER_SCOPE_KEYS[0]), true, "a clash would be seen");
});

test("every server action key reaches the member-action handler: the page's handler leaves it alone in the panel", () => {
  const store = { state: { ui: {}, slices: new Map() }, setUi() { throw new Error("the page acted"); } };
  const inPanel = panelButton();
  for (const raw of serverKeys()) {
    const e = NAMED[raw] ?? raw;
    assert.equal(keyMatches(raw, e), true, `${raw} matches the event key ${e}`);
    assert.equal(globalKey({ key: e, target: inPanel, preventDefault() {} }, store, globalThis.document), false, `${e} is not taken by the page in the panel`);
  }
  assert.equal(keyMatches("y", "Y"), false, "a different letter does not match");
});

test("root scope: Enter opens the selected member's conversation, / focuses the search, T opens the switcher, D toggles Diagnostics, M does nothing yet", async () => {
  const m = await mountApp({ view: "overview" });
  const { store } = m;
  assert.equal(key(store, "Enter"), false, "no member selected");
  store.setUi({ selected: member("reviewer") });
  assert.equal(key(store, "Enter", { target: el("button") }), false, "Enter on a focused button is that button's");
  assert.equal(store.state.ui.view, "overview");
  assert.equal(key(store, "Enter"), true);
  assert.equal(store.state.ui.view, "conversation");
  store.setUi({ view: "teams" });
  assert.equal(key(store, "Enter"), false, "on All teams a row's own Enter opens it");
  let focused = 0;
  const doc = { body: globalThis.document.body, querySelector: (s) => (s === "[data-search]" ? { focus: () => (focused += 1) } : null) };
  assert.equal(globalKey({ key: "/", target: doc.body, preventDefault() {} }, store, doc), true);
  assert.equal(focused, 1);
  assert.equal(globalKey({ key: "/", target: doc.body, preventDefault() {} }, store, { body: doc.body, querySelector: () => null }), false, "no search box, no key");
  assert.equal(key(store, "T"), true);
  assert.equal(store.state.ui.menu, "team");
  assert.equal(key(store, "T"), true);
  assert.equal(store.state.ui.menu, null);
  assert.equal(key(store, "D"), true);
  assert.equal(key(store, "D"), true);
  assert.equal(store.state.ui.diagnostics, false);
  const before = JSON.stringify(store.state.ui);
  assert.equal(key(store, "M"), false);
  assert.equal(key(store, "t"), false, "a plain letter is never ours");
  assert.equal(JSON.stringify(store.state.ui), before);
});

const summaryOf = (body) => ({ id: "team-summary:d/t", version: 1, as_of: 1, body: { team: "team:d/t", name: "t", work_root: "/w/repo", branch: "ah/x", running: true, members: 2, working: 1, blocked: 0, needs_you: 0, in_flight: 3, updated_at: "2026-06-01T10:00:00.000Z", feed_newest_at: null, alerts: [], ...body } });

test("D11: a team's row is the fields of its summary slice, with no member row or activity slice held", () => {
  const slices = new Map([["team-summary:d/t", summaryOf({})]]);
  const row = teamSummary(slices, "team:d/t");
  assert.deepEqual([row.id, row.name, row.dirId, row.repo, row.branch, row.members, row.working, row.running, row.inFlight], ["team:d/t", "t", "d", "repo", "ah/x", 2, 1, true, 3]);
  assert.equal(row.updated, Date.parse("2026-06-01T10:00:00.000Z"));
  assert.deepEqual(teamRows(slices).map((r) => r.id), ["team:d/t"]);
  assert.equal(teamSummary(slices, "team:d/none"), null, "a team whose summary is not held has no row");
  assert.equal(teamSummary(slices, null), null);
  assert.deepEqual(teamRows(new Map()), [], "no summary held: no rows");
});

test("D13: alerts are the Needs-you items; none is none", () => {
  const slices = new Map([["team-summary:d/t", summaryOf({ needs_you: 3, blocked: 1, alerts: [
    { kind: "needs_you", since: "2026-06-01T10:00:00.000Z", text: "Decision waiting · 2", title: "Decision waiting · 2", detail: "Waiting on you" },
    { kind: "blocked", member: "member:d/t/dev", since: "2026-06-01T10:01:00.000Z", text: "Waiting at Write", title: "dev is blocked", detail: "Waiting at Write" },
    { kind: "error", member: "member:d/t/ops", since: "2026-06-01T10:02:00.000Z", text: "Last turn failed", title: "ops failed", detail: "Last turn failed" },
  ] })]]);
  const row = teamSummary(slices, "team:d/t");
  assert.equal(row.needs, 3);
  assert.deepEqual(row.items.map((i) => [i.kind, i.title, i.context, i.memberId]), [
    ["Decision", "Decision waiting · 2", "Waiting on you", null],
    ["Blocked", "dev is blocked", "Waiting at Write", "member:d/t/dev"],
    ["Error", "ops failed", "Last turn failed", "member:d/t/ops"],
  ]);
  assert.equal(new Set(row.items.map((i) => i.key)).size, 3, "each card has its own key");
  slices.set("team-summary:d/t", summaryOf({}));
  assert.deepEqual(teamSummary(slices, "team:d/t").items, [], "no alerts: nothing to do");
});

test("Snooze 24 h hides a card until the item changes; Undo brings it back; Show snoozed lists it", async () => {
  const stored = new Map();
  globalThis.localStorage = { getItem: (k) => stored.get(k) ?? null, setItem: (k, v) => stored.set(k, v), removeItem: (k) => stored.delete(k) };
  try {
    const m = await mountApp({ view: null });
    await until(() => rowsOf(m.root).length > 0, "the team rows");
    const tid = "team:a1b2c3d4/ship";
    // a decision card counts the summary's waiting_on_you (ah-ui-focus §3); its key carries the count
    const publish = (n) => {
      const team = m.model.slice(tid).body;
      m.model.publish([{ id: tid, as_of: Date.now(), body: { ...team, pipeline: { ...team.pipeline, waiting_on_user: n } } }]);
    };
    publish(1);
    const card = (n = 1) => find(m.root, "article.need-card").find((c) => new RegExp(`Waiting on you · ${n}`).test(text(c)));
    await until(() => card(), "the card");
    click(button(card(), "Snooze 24 h"));
    await until(() => !card(), "the card to hide");
    const until24 = Object.values(JSON.parse(stored.get("ah-later")))[0];
    assert.ok(until24 > Date.now() + 23 * 3600_000, "held for this viewer as {key: until}");
    assert.match(text(find(m.root, ".snooze-note")[0]), /Snoozed for 24 h/);
    click(button(find(m.root, ".snooze-note")[0], "Undo"));
    await until(() => card(), "Undo brings it back");
    assert.deepEqual(JSON.parse(stored.get("ah-later")), {});
    click(button(card(), "Snooze 24 h"));
    await until(() => !card(), "snoozed again");
    click(button(m.root, "Show snoozed (1)"));
    await until(() => card() && /Snoozed until/.test(text(card())), "the snoozed card, listed");
    click(button(m.root, "Show snoozed (1)"));
    await until(() => !card(), "hidden again");
    publish(2);
    await until(() => card(2), "the changed item");
  } finally {
    delete globalThis.localStorage;
  }
});

test("the connection chip says disconnected, with the retry countdown, once nothing has been read for 30 s", () => {
  const base = { ended: false, errorSince: 1000, freshAt: 1000, retryAt: 41_000 };
  assert.match(freshness(base, 1000 + DISCONNECT_MS).text, /^reconnecting/, "at 30 s it is still reconnecting");
  assert.equal(freshness(base, 1000 + DISCONNECT_MS + 1).text.startsWith("✕ disconnected · retry in "), true);
  assert.equal(freshness({ ...base, retryAt: 38_000 }, 31_001).text, "✕ disconnected · retry in 7 s");
  assert.equal(freshness(base, 41_000).text, "✕ disconnected · retrying…", "a retry in flight is not a zero countdown");
  assert.equal(freshness(base, 50_000).text, "✕ disconnected · retrying…");
  assert.equal(freshness(base, 40_999).text, "✕ disconnected · retry in 1 s", "the nearest moment before the retry is due");
  const ok = { ...base, errorSince: null };
  assert.equal(freshness(ok, 1000 + 10_000).text, "live", "live up to 10 s");
  assert.equal(freshness(ok, 1000 + 10_999).text, "live", "the nearest age that is still live");
  assert.equal(freshness(ok, 1000 + 12_000).text, "updated 12 s ago", "then the age, as an age (C12)");
  assert.equal(freshness(ok, 1000 + 120_000).text, "updated 2 m ago");
  assert.equal(freshness({ ...base, ended: true }, 99_000).text, "session ended");
});

test("a key a control already handled is not the page's: the focused graph's 0 leaves the address alone (B1)", async () => {
  const m = await mountApp({ view: "graph" });
  const before = m.store.state.ui.view;
  assert.equal(globalKey({ key: "1", target: globalThis.document.body, defaultPrevented: true, preventDefault() {} }, m.store, globalThis.document), false);
  assert.equal(globalKey({ key: "0", target: globalThis.document.body, preventDefault() {} }, m.store, globalThis.document), false);
  assert.equal(m.store.state.ui.view, before);
});

test("the status bar's hints are the ones generated from KEY_TABLE on every view (B6)", async () => {
  const { hintOf } = await import("../../web/ui/keys.js");
  const m = await mountApp({ view: "graph" });
  for (const view of ["overview", "board", "graph", "timeline", "conversation"]) {
    m.store.setUi({ view });
    await until(() => text(find(find(m.root, "footer.shell-foot")[0], "span.hints")[0]) === hintOf(view), `the ${view} hints`);
    assert.match(hintOf(view), /^1–5 tabs · .* · \? keys$/, view);
  }
  assert.equal(hintOf("graph"), "1–5 tabs · ↑↓ member · Enter open · Shift+M Map/Radial/Flow · Shift+F fit · Shift+T team · ] panel · ? keys");
  assert.equal(hintOf("board"), "1–5 tabs · ↑↓ row · / search · Shift+G group · Shift+L layout · Shift+T team · ? keys", "no panel where there is no rail");
  assert.equal(hintOf("teams").includes("1–5"), false, "Home has no tabs");
  assert.equal(hintOf("inbox", { team: false }).includes("1–5"), false, "the all-teams Inbox has no tabs");
});

test("] and the Panel button act only where a rail exists (L2)", async () => {
  const m = await mountApp({ view: "board" });
  await until(() => find(find(m.root, "nav.views")[0], "button").length === 5, "the tabs");
  assert.equal(find(m.root, "button.rail-toggle").length, 0, "no Panel button on the Board");
  assert.equal(key(m.store, "]"), false);
  assert.notEqual(m.store.state.ui.railOpen, true);
  m.store.setUi({ view: "overview" });
  await until(() => find(m.root, "button.rail-toggle").length === 1, "the Panel button");
  assert.equal(text(find(m.root, "button.rail-toggle")[0]), "Panel");
  assert.equal(key(m.store, "]"), true);
  assert.equal(m.store.state.ui.railOpen, true);
});

test("Inbox · N and Needs you N show the same count and open the team's Inbox; the chip is not a live region (C12)", async () => {
  const m = await mountApp({ view: "overview" });
  await until(() => find(m.root, "button.inbox-btn").length === 1, "the Inbox button");
  const n = Number(text(find(find(m.root, "button.inbox-btn")[0], "span.num")[0]));
  assert.equal(text(find(find(m.root, "button.needs")[0], "strong")[0]), String(n));
  assert.equal(find(find(m.root, "header.shell-head")[0], "span").filter((e) => e.getAttribute("role") === "status").length, 0, "nothing in the header is announced every second");
  const team = m.store.state.ui.team ?? teamRows(m.store.state.slices)[0].id;
  click(find(m.root, "button.needs")[0]);
  assert.deepEqual([m.store.state.ui.view, m.store.state.ui.inboxTab], ["inbox", "attention"]);
  assert.equal(m.store.state.ui.team, team, "scoped to the shown team");
  m.store.setUi({ view: "overview" });
  click(find(m.root, "button.inbox-btn")[0]);
  assert.equal(m.store.state.ui.view, "inbox");
  await until(() => /^ah · ship · Inbox$/.test(globalThis.document.title), "the page title names the team and the page (P2)");
});
