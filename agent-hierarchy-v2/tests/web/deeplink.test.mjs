import { test } from "node:test";
import assert from "node:assert/strict";
import { uiOf, hashOf, bindHash, redirectOf, teamOfMember } from "../../web/ui/deeplink.js";
import { firstTeam } from "../../web/ui/select.js";

const M = "member:d1/ship/dev";
const T = "team:d1/ship";

const E = encodeURIComponent;
const NONE = { lane: null, epic: null, deliverable: null, item: null, owner: null, sitrep: false };

test("every hash form parses to a view, member, team and params, and hashOf writes it back", () => {
  const cases = [
    ["#/", { view: "teams", selected: null }],
    ["#/overview", { view: "overview", selected: null, team: null, ...NONE }],
    [`#/graph?team=${E(T)}`, { view: "graph", selected: null, team: T, ...NONE }],
    [`#/conversation/${E(M)}`, { view: "conversation", selected: M, team: T, ...NONE }],
    [`#/timeline/${E(M)}?team=${E("team:d2/other")}`, { view: "timeline", selected: M, team: "team:d2/other", ...NONE }],
    [`#/board?team=${E(T)}`, { view: "board", selected: null, team: T, ...NONE }],
    [`#/board?team=${E(T)}&lane=api&item=${E("api/s1")}&owner=${E(M)}`, { view: "board", selected: null, team: T, ...NONE, lane: "api", item: "api/s1", owner: M }],
    [`#/graph?team=${E(T)}&sitrep=1`, { view: "graph", selected: null, team: T, ...NONE, sitrep: true }],
    ["#/board?team=all", { view: "allplans", selected: null, ...NONE }],
    [`#/member/${E(M)}`, { view: "member", selected: M, team: T, ...NONE }],
    ["#/inbox/attention", { view: "inbox", inboxTab: "attention", team: null, selected: null, ...NONE }],
    [`#/inbox/qa?team=${E(T)}`, { view: "inbox", inboxTab: "qa", team: T, selected: null, ...NONE }],
  ];
  for (const [hash, ui] of cases) {
    assert.deepEqual(uiOf(hash), ui, hash);
    if (hash.startsWith("#/timeline/")) {
      continue; // a member's own team wins when the address is written back: the team= beside a member is dropped
    }
    assert.equal(hashOf(uiOf(hash)), hash, `round trip ${hash}`);
    assert.equal(redirectOf(hash), null, `a canonical address stands: ${hash}`);
  }
  assert.equal(hashOf({}), "", "no view yet: no hash");
  assert.equal(hashOf({ view: "teams", team: T }), "#/");
  assert.equal(hashOf({ view: "conversation", selected: M, team: T }), `#/conversation/${E(M)}`, "a member names its team");
  assert.equal(hashOf({ view: "graph", team: T, lane: "api", item: "x", owner: M }), `#/graph?team=${E(T)}`, "lane, item and owner are the Board's alone");
  assert.equal(hashOf({ view: "board", team: T, lane: null, sitrep: false }), `#/board?team=${E(T)}`, "a default param is not written");
  assert.equal(hashOf({ view: "inbox", team: null }), "#/inbox/attention", "Attention is the Inbox's default tab");
  assert.equal(hashOf({ view: "member", selected: "m1" }), "#/", "a malformed member has no page");
});

test("an unknown view, a malformed hash and the sign-in code are not ours", () => {
  assert.equal(uiOf("#/nosuch"), null);
  assert.equal(uiOf("#/teams"), null, "the old All teams address is only a redirect");
  assert.equal(uiOf("#/teams/x"), null, "the nearest non-match of #/teams");
  assert.equal(uiOf("#/conversation/%E0%A4%A"), null);
  assert.equal(uiOf("#c=abc"), null);
  assert.equal(uiOf(""), null);
  assert.equal(redirectOf("#/nosuch"), null, "an unknown view keeps the not-built text");
  assert.equal(redirectOf("#c=abc"), null);
  assert.equal(teamOfMember("m1"), null);
  assert.equal(teamOfMember(M), T);
});

test("each old address of ah-ui-ia §5 is rewritten once, keeping the member segment and team", () => {
  const rows = [
    ["#/teams", "#/"],
    ["#/teams/plans", "#/board?team=all"],
    [`#/plans?team=${E(T)}`, `#/board?team=${E(T)}`],
    [`#/checklist?team=${E(T)}`, `#/board?team=${E(T)}`],
    [`#/backlog?team=${E(T)}`, `#/board?team=${E(T)}`],
    [`#/queue?team=${E(T)}`, `#/board?team=${E(T)}`],
    [`#/lanes?team=${E(T)}`, `#/board?team=${E(T)}`],
    [`#/plans/${E(M)}`, `#/board/${E(M)}`],
    ["#/plans", "#/board"],
    [`#/attention?team=${E(T)}`, `#/inbox/attention?team=${E(T)}`],
    [`#/questions?team=${E(T)}`, `#/inbox/qa?team=${E(T)}`],
    [`#/attention/${E(M)}`, `#/inbox/attention?team=${E(T)}`],
    ["#/questions", "#/inbox/qa"],
  ];
  for (const [from, to] of rows) {
    assert.equal(redirectOf(from), to, from);
    assert.equal(redirectOf(to), null, `once: ${to} stands`);
  }
});

test("the router's own params are dropped where ah-ui-ia §6 drops them, and the rest is normalised to one canonical form", () => {
  assert.equal(redirectOf("#/inbox"), "#/inbox/attention");
  assert.equal(redirectOf("#/inbox/other"), "#/inbox/attention", "an unknown Inbox tab");
  assert.equal(redirectOf(`#/inbox?team=${E(T)}`), `#/inbox/attention?team=${E(T)}`);
  assert.equal(redirectOf("#/board?team=all&lane=api"), "#/board?team=all", "team=all drops lane");
  assert.equal(redirectOf("#/overview?sitrep=1"), "#/overview", "no team: sitrep dropped");
  assert.equal(redirectOf("#/inbox/attention?sitrep=1"), "#/inbox/attention", "the Inbox takes no sitrep");
  assert.equal(redirectOf(`#/graph?team=${E(T)}&lane=api`), `#/graph?team=${E(T)}`, "lane off the Board");
  assert.equal(redirectOf(`#/board?team=${E(T)}&sitrep=0`), `#/board?team=${E(T)}`, "sitrep only as 1");
  assert.equal(redirectOf(`#/graph?team=${E(T)}&sitrep=1`), null, "the nearest standing input");
  assert.equal(redirectOf(`#/board?lane=api&team=${E(T)}`), `#/board?team=${E(T)}&lane=api`, "param order is normalised");
  assert.equal(redirectOf(`#/board?team=${E(T)}&zzz=1`), `#/board?team=${E(T)}`, "an unknown param is ignored, and the address normalised");
  assert.equal(redirectOf(`#/board?team=${T}`), `#/board?team=${E(T)}`, "an unencoded value is encoded");
  assert.equal(redirectOf(`#/member/${E("m1")}`), "#/", "a malformed member id goes Home");
  assert.equal(uiOf(`#/member/${E("m1")}`).notice, "Member not found");
});

function harness(hash) {
  const listeners = [];
  const history = { state: null, pushes: 0, replaced: [], replaceState(_s, _t, to) { this.replaced.push(to); win.location.hash = to; } };
  const win = { location: { hash }, history, addEventListener: (t, f) => listeners.push([t, f]) };
  const subs = new Set();
  const store = { state: { ui: {} }, setUi(p) { this.state.ui = { ...this.state.ui, ...p }; subs.forEach((f) => f()); }, subscribe: (f) => (subs.add(f), () => subs.delete(f)) };
  bindHash(store, win);
  return { win, store, fire: () => listeners.find(([t]) => t === "hashchange")[1]() };
}

test("the sign-in code is never read or rewritten", () => {
  const h = harness("#c=abc");
  assert.deepEqual(h.store.state.ui, {});
  h.store.setUi({ selected: M });
  assert.equal(h.win.location.hash, "#c=abc");
  h.store.setUi({ view: "overview", selected: null });
  assert.equal(h.win.location.hash, "#/overview", "once the operator navigates, the address follows");
});

test("a page whose view is cleared clears one of our addresses, and leaves one that is not ours", () => {
  const ours = harness("#/overview");
  ours.store.setUi({ view: undefined });
  assert.equal(ours.win.location.hash, "", "our hash goes when there is no view to name");
  const theirs = harness("#/other/place");
  theirs.store.setUi({ view: undefined });
  assert.equal(theirs.win.location.hash, "#/other/place", "the nearest non-match: a hash that is not ours stays");
});

test("the old conversation link opens that member, a view change rewrites the hash and a hashchange moves the page", () => {
  const h = harness(`#/conversation/${encodeURIComponent(M)}`);
  assert.deepEqual([h.store.state.ui.view, h.store.state.ui.selected, h.store.state.ui.team], ["conversation", M, T]);
  h.store.setUi({ view: "graph", selected: null });
  assert.equal(h.win.location.hash, `#/graph?team=${encodeURIComponent(T)}`);
  h.win.location.hash = "#/teams";
  h.fire();
  assert.equal(h.store.state.ui.view, "teams");
  assert.equal(h.win.location.hash, "#/", "the old All teams address is replaced, not pushed");
  h.win.location.hash = "";
  h.store.setUi({ view: "graph" });
  h.win.location.hash = "";
  h.fire();
  assert.equal(h.store.state.ui.view, "teams", "back to an empty address is All teams");
});

test("an old address is replaced in history on load and on a hashchange, so Back skips it", () => {
  const h = harness(`#/checklist?team=${E(T)}`);
  assert.deepEqual(h.win.history.replaced, [`#/board?team=${E(T)}`]);
  assert.deepEqual([h.store.state.ui.view, h.store.state.ui.team], ["board", T]);
  h.win.location.hash = `#/questions?team=${E(T)}`;
  h.fire();
  assert.deepEqual([h.store.state.ui.view, h.store.state.ui.inboxTab, h.store.state.ui.team], ["inbox", "qa", T]);
  assert.equal(h.win.location.hash, `#/inbox/qa?team=${E(T)}`);
  const bad = harness("#/member/m1");
  assert.deepEqual([bad.store.state.ui.view, bad.store.state.ui.notice, bad.win.location.hash], ["teams", "Member not found", "#/"]);
});

test("a new address clears the old one's params, and the store's params are written back", () => {
  const h = harness(`#/board?team=${E(T)}&lane=api&item=x`);
  assert.equal(h.store.state.ui.lane, "api");
  h.win.location.hash = `#/board?team=${E(T)}`;
  h.fire();
  assert.deepEqual([h.store.state.ui.lane, h.store.state.ui.item], [null, null]);
  h.store.setUi({ owner: M });
  assert.equal(h.win.location.hash, `#/board?team=${E(T)}&owner=${E(M)}`);
  h.store.setUi({ sitrep: true });
  assert.equal(h.win.location.hash, `#/board?team=${E(T)}&owner=${E(M)}&sitrep=1`);
  h.store.setUi({ view: "allplans" });
  assert.equal(h.win.location.hash, `#/board?team=all&owner=${E(M)}`);
});

// A window whose history is a real stack: setting the hash pushes, replaceState replaces, back() moves and fires hashchange.
function stacked(hash) {
  const listeners = [];
  const entries = [hash];
  let at = 0;
  const counts = { push: 0, replace: 0 };
  const location = {
    get hash() { return entries[at]; },
    set hash(h) { if (h === entries[at]) return; entries.splice(at + 1); entries.push(h); at += 1; counts.push += 1; },
  };
  const history = { state: null, replaceState(_s, _t, h) { entries[at] = h; counts.replace += 1; } };
  const win = { location, history, addEventListener: (t, f) => listeners.push([t, f]) };
  const subs = new Set();
  const store = { state: { ui: {} }, setUi(p) { this.state.ui = { ...this.state.ui, ...p }; subs.forEach((f) => f()); }, subscribe: (f) => (subs.add(f), () => subs.delete(f)) };
  const fire = () => listeners.filter(([t]) => t === "hashchange").forEach(([, f]) => f());
  bindHash(store, win);
  return { win, store, counts, entries: () => [...entries], at: () => at, go: (h) => { location.hash = h; fire(); }, back: () => { at -= 1; fire(); } };
}

test("each non-canonical address is normalised by exactly one replaceState and no push, then stands through a store change", () => {
  for (const [from, to] of [
    [`#/checklist?team=${E(T)}`, `#/board?team=${E(T)}`],
    [`#/board?lane=api&team=${E(T)}`, `#/board?team=${E(T)}&lane=api`],
    [`#/board?team=${T}`, `#/board?team=${E(T)}`],
    [`#/graph?team=${E(T)}&zzz=1`, `#/graph?team=${E(T)}`],
  ]) {
    const h = stacked(from);
    assert.deepEqual([h.win.location.hash, h.counts.replace, h.counts.push], [to, 1, 0], from);
    h.store.setUi({ railOpen: true });
    assert.deepEqual([h.win.location.hash, h.counts.replace, h.counts.push], [to, 1, 0], `${from}: a store change that moves no routed field writes nothing`);
  }
  const ok = stacked(`#/board?team=${E(T)}&lane=api`);
  assert.deepEqual([ok.counts.replace, ok.counts.push], [0, 0], "the nearest input: a canonical address is not rewritten");
});

test("Back after a redirect and a store change goes back, and the page does not push forward again", () => {
  const h = stacked(`#/overview?team=${E(T)}`);
  h.go(`#/queue?team=${E(T)}`);
  assert.deepEqual(h.entries(), [`#/overview?team=${E(T)}`, `#/board?team=${E(T)}`], "the old address was replaced where it was pushed");
  h.store.setUi({ railOpen: true, plansSel: "x" });
  assert.equal(h.counts.push, 1, "only the link itself pushed");
  h.back();
  assert.deepEqual([h.win.location.hash, h.store.state.ui.view, h.at(), h.counts.push], [`#/overview?team=${E(T)}`, "overview", 0, 1], "Back lands, and nothing is pushed on top of it");
  const empty = stacked("");
  empty.go(`#/graph?team=${E(T)}`);
  empty.back();
  assert.deepEqual([empty.win.location.hash, empty.store.state.ui.view, empty.at(), empty.counts.push], ["#/", "teams", 0, 1], "Back to the empty address is Home, replaced, not pushed");
});

test("opening and closing the Sitrep adds no history entry, and Back after the close does not re-open it", () => {
  const h = stacked("#/");
  h.go(`#/overview?team=${E(T)}`);
  h.store.setUi({ sitrep: true });
  assert.equal(h.win.location.hash, `#/overview?team=${E(T)}&sitrep=1`);
  h.store.setUi({ sitrep: false });
  assert.deepEqual(h.entries(), ["#/", `#/overview?team=${E(T)}`], "one entry for the page, none for the dialog");
  assert.equal(h.counts.push, 1, "only the link pushed");
  h.back();
  assert.deepEqual([h.win.location.hash, h.store.state.ui.view, Boolean(h.store.state.ui.sitrep)], ["#/", "teams", false], "Back leaves the team; the dialog does not come back");
});

test("dropping sitrep=1 on a lane team is a replace, so Back is not trapped", () => {
  const h = stacked("#/");
  h.go(`#/overview?team=${E(T)}&sitrep=1`);
  h.store.setUi({ sitrep: false });
  assert.deepEqual([h.entries(), h.counts.push, h.counts.replace], [["#/", `#/overview?team=${E(T)}`], 1, 1]);
  h.back();
  assert.equal(h.win.location.hash, "#/", "Back goes where it came from");
});

test("a view change still pushes; a panel selection on a tab replaces; a member page change pushes", () => {
  const h = stacked(`#/overview?team=${E(T)}&sitrep=1`);
  h.store.setUi({ view: "board", sitrep: false });
  assert.deepEqual([h.counts.push, h.win.location.hash], [1, `#/board?team=${E(T)}`], "view change: a push, sitrep dropped with it");
  h.store.setUi({ view: "overview", selected: M });
  assert.equal(h.counts.push, 2);
  h.store.setUi({ selected: null });
  assert.deepEqual([h.counts.push, h.win.location.hash], [2, `#/overview?team=${E(T)}`], "closing the selection is a replace");
  h.store.setUi({ team: "team:d9/other" });
  assert.equal(h.counts.push, 3, "a team change pushes");
  const mp = stacked(`#/member/${E(M)}`);
  mp.store.setUi({ view: "member", selected: `${M}x`, team: T });
  assert.equal(mp.counts.push, 1, "on the member page the member is the route");
});

test("a hash that is not ours is left alone", () => {
  const h = harness("#/other/place");
  h.store.setUi({ selected: "x" });
  assert.equal(h.win.location.hash, "#/other/place");
});

test("the team selector returns the chosen team while it is held and the first otherwise", () => {
  const slices = new Map([["team:d1/a", { id: "team:d1/a", body: {} }], ["team:d1/b", { id: "team:d1/b", body: {} }]]);
  assert.equal(firstTeam(slices, { team: "team:d1/b" }).id, "team:d1/b");
  assert.equal(firstTeam(slices, { team: "team:d1/gone" }).id, "team:d1/a", "an unknown team falls back to the first");
  assert.equal(firstTeam(slices, {}).id, "team:d1/a");
  assert.equal(firstTeam(slices).id, "team:d1/a");
  assert.equal(firstTeam(new Map(), { team: "x" }), null);
});

test("a Board scope round-trips: lane, epic and deliverable survive a reload, at a team and at All teams", () => {
  const scoped = { view: "board", team: T, selected: null, ...NONE, lane: "lane-board", epic: "b9", deliverable: "b20", owner: "board-ux" };
  const hash = hashOf(scoped);
  assert.equal(hash, `#/board?team=${E(T)}&lane=lane-board&epic=b9&deliverable=b20&owner=board-ux`);
  assert.deepEqual(uiOf(hash), scoped);
  const all = hashOf({ view: "allplans", epic: "b9", deliverable: "~", item: null, owner: null });
  assert.equal(all, "#/board?team=all&epic=b9&deliverable=~");
  assert.equal(uiOf(all).epic, "b9");
  assert.equal(uiOf(all).deliverable, "~");
  // Another view drops the scope.
  assert.equal(uiOf(`#/graph?team=${E(T)}`).epic, null);
});

test("the standalone tables each have an address, and negative: their old hashes still go to the Board and an unknown table has none", () => {
  for (const view of ["backlog", "checklist", "queue"]) {
    const hash = `#/table/${view}?team=${encodeURIComponent("team:d/t")}`;
    const ui = uiOf(hash);
    assert.deepEqual([ui.view, ui.team], [view, "team:d/t"]);
    assert.equal(hashOf(ui), hash);
    assert.equal(redirectOf(hash), null);
  }
  assert.equal(redirectOf(`#/checklist?team=x`), "#/board?team=x");
  assert.equal(uiOf("#/table/lanes"), null);
});
