import { test } from "node:test";
import assert from "node:assert/strict";
import { mountApp, until, find, text, DIR, wire } from "./mount-app.mjs";
import { settle } from "./dom.mjs";
import { createStore } from "../../web/ui/store.js";
import { urlOf } from "../../web/ui/bridge.js";
import { START_VIEWS } from "../../web/ui/app.js";
import { railCards } from "../../web/ui/teams.js";
import { needsCount, teamRows, teamIdOf, teamLeaseIds, summaryIdOf } from "../../web/ui/select.js";

// L3: the page starts on the summary view and every component leases the slices it shows.

const SUMMARY = `team-summary:${DIR}/ship`;
const TEAM = `team:${DIR}/ship`;
const rowsOf = (root) => find(root, "tr").filter((r) => r.attrs.has("data-row"));
const subOf = (m) => m.log.polls.at(-1) ?? "";
const held = (m, prefix) => [...m.store.state.slices.keys()].filter((id) => id.startsWith(prefix));

test("the page starts on the summary view alone and reads one manifest", async () => {
  assert.deepEqual(START_VIEWS, ["summary"]);
  const names = [];
  const store = createStore({ get: () => new Promise(() => {}), manifest: async (v) => (names.push(v), { epoch: "e", seq: 1, slices: [], pending: [] }) });
  store.start(START_VIEWS);
  await until(() => store.state.ready, "ready");
  assert.deepEqual(names, ["summary"]);
  store.stop();
});

test("All teams holds the summaries and nothing of any team's members, exchanges or activity", async () => {
  const m = await mountApp({ view: "teams" });
  await until(() => rowsOf(m.root).length > 0, "rows");
  await settle();
  // besides the view, only each team's questions slice, which the one Needs-you count reads (F1)
  assert.deepEqual(subOf(m).split(",").filter((s) => !s.startsWith("view:")), [`questions:${DIR}/ship`], `only the view and the count's questions: ${subOf(m)}`);
  for (const prefix of ["team:", "member:", "activity:", "exchange:", "topology:", "list:open:", "list:feed:"]) {
    assert.deepEqual(held(m, prefix), [], prefix);
  }
  assert.ok(held(m, "team-summary:").length >= 1);
});

test("the Needs-you rail shows the alerts of every summary, and the header counts them", async () => {
  const m = await mountApp({ view: "teams" });
  await until(() => find(m.root, "article.need-card").length > 0, "cards");
  const summary = () => m.store.state.slices.get(SUMMARY).body;
  // the cards are the Inbox's set (F2): a decision card per team with open questions, then each blocked, failed or stuck alert
  await until(() => find(m.root, "article.need-card").length === railCards(m.store.state.slices).length, "a card per row of the set");
  assert.ok(summary().alerts.filter((x) => x.kind !== "needs_you").every((x) => find(m.root, "article.need-card").some((c) => text(c).includes(x.text))), "every alert row is a card");
  // the header reads the served count (ah-ui-focus §3), and the decision card follows it
  await until(() => text(find(find(m.root, "button.needs")[0], "strong")[0]) === String(needsCount(m.store.state.slices, null)), "the status bar shows the one count");
  const team = m.model.slice(TEAM).body;
  m.model.publish([{ id: TEAM, as_of: Date.now(), body: { ...team, pipeline: { ...team.pipeline, waiting_on_user: 7 } } }]);
  await until(() => find(m.root, "article.need-card").some((c) => /Waiting on you · 7/.test(text(c))), "the card follows the summary");
});

test("the Recent rail pages a team again only when feed_newest_at moves", async () => {
  const m = await mountApp({ view: "teams" });
  const feeds = () => m.log.lists.filter((l) => l.path === `/list/list:feed:team:${DIR}/ship`).length;
  await until(() => feeds() === 1, "the first page");
  await until(() => find(m.root, "ul").some((u) => u.attrs.get("class") === "recent-list"), "recent rows");
  const before = m.store.state.slices.get(SUMMARY).body;
  m.model.tick();
  await until(() => m.store.state.slices.get(SUMMARY).body.updated_at !== before.updated_at || m.store.state.slices.get(SUMMARY).body.working !== before.working, "the summary moved without the feed");
  await settle();
  await settle();
  assert.equal(m.store.state.slices.get(SUMMARY).body.feed_newest_at, before.feed_newest_at, "the feed did not move");
  assert.equal(feeds(), 1, "updated_at and counts do not page the feed");
  m.model.traffic();
  await until(() => feeds() === 2, "a page for the moved feed");
});

test("opening a team leases its slices, leaving it releases them, and All teams holds none again", async () => {
  const m = await mountApp({ view: "teams" });
  await until(() => rowsOf(m.root).length > 0, "rows");
  m.store.setUi({ team: TEAM, view: "overview" });
  await until(() => held(m, "member:").length > 0 && held(m, "list:open:").length === 1, "the overview's slices");
  for (const id of [TEAM, `topology:${DIR}/ship`, `list:open:team:${DIR}/ship`, `processes:${DIR}`]) {
    assert.ok(subOf(m).split(",").includes(id) || m.log.polls.some((p) => p.split(",").includes(id)), `${id} is named`);
  }
  m.store.setUi({ view: "teams" });
  await until(() => held(m, "member:").length === 0 && held(m, "team:").length === 0 && held(m, "exchange:").length === 0, "everything released");
  await until(() => subOf(m) === `view:summary,questions:${DIR}/ship`, `the subscription is the view and the count's questions again: ${subOf(m)}`);
});

test("a view that hides unmounts and releases what only it showed", async () => {
  const m = await mountApp({ view: "teams" });
  await until(() => rowsOf(m.root).length > 0, "rows");
  m.store.setUi({ team: TEAM, view: "overview" });
  await until(() => held(m, "list:open:").length === 1, "open list held by the overview");
  m.store.setUi({ view: "conversation" });
  await until(() => held(m, "list:open:").length === 0, "the conversation does not show the open list");
  assert.ok(held(m, "member:").length > 0, "it still shows the members");
});

test("a switch between team views restarts the poll once", async () => {
  const m = await mountApp({ view: "teams" });
  m.store.setUi({ team: TEAM, view: "overview" });
  await until(() => held(m, "member:").length > 0 && held(m, "list:open:").length === 1, "the overview's slices");
  await settle();
  const aborted = () => new Set(m.log.signals.filter((s) => s.aborted)).size;
  await new Promise((r) => setTimeout(r, 80));
  const before = aborted();
  m.store.setUi({ view: "timeline" });
  await until(() => held(m, "list:open:").length === 0, "the switch");
  await new Promise((r) => setTimeout(r, 80));
  assert.equal(aborted() - before, 1, "one restart for the whole view switch");
});

test("an expired window: a reload fetches one manifest and the leased ids come back through fresh=", async () => {
  const m = await mountApp({ view: "teams" });
  m.store.setUi({ team: TEAM, view: "overview" });
  await until(() => held(m, "member:").length > 0, "members");
  const manifests = m.log.manifests.length;
  const fresh = [];
  const poll = m.model.poll.bind(m.model);
  m.model.poll = (args) => (args.fresh?.length && fresh.push(...args.fresh), poll(args));
  m.model.restart();
  await until(() => m.log.manifests.length === manifests + 1, "one manifest read");
  await until(() => held(m, "member:").length > 0 && held(m, "list:open:").length === 1, "leased slices came back");
  assert.equal(m.log.manifests.length, manifests + 1, "exactly one manifest per reload");
  assert.ok(fresh.includes("*") || (fresh.includes(TEAM) && fresh.some((id) => id.startsWith("member:"))), "asked for fresh");
});

test("selectors tolerate absent slices", () => {
  assert.deepEqual(teamRows(new Map()), []);
  assert.equal(teamIdOf(new Map(), {}), null);
  assert.equal(teamIdOf(new Map(), { team: TEAM }), null, "no summary held: no team yet");
  assert.deepEqual(teamLeaseIds(new Map(), null, { members: true, open: true, feed: true }), []);
  const bare = { id: TEAM, body: { name: "ship", dir_id: DIR, orchestrator: null, members: [`member:${DIR}/ship/a`] } };
  const ids = teamLeaseIds(new Map(), bare, { members: true, open: true, feed: true });
  assert.ok(ids.includes(`member:${DIR}/ship/a`) && ids.includes(`list:open:team:${DIR}/ship`), "the member and the lists are named before they arrive");
  assert.ok(!ids.some((id) => id.startsWith("exchange:") || id.startsWith("activity:")), "what is learned from absent slices is not named");
  assert.equal(summaryIdOf(TEAM), SUMMARY);
});

test("every team view draws with only the summary and the team slice held", async () => {
  for (const view of ["overview", "graph", "timeline", "conversation"]) {
    const m = await mountApp({ view: "teams" });
    await until(() => rowsOf(m.root).length > 0, "rows");
    m.store.setUi({ team: TEAM, view });
    await settle();
    await until(() => m.root.all("main").length > 0, `${view} main`);
    assert.equal(find(m.root, ".toast").length, 0, view);
    m.store.stop();
  }
});

// ---- rev 1.2

const flush = () => new Promise((r) => queueMicrotask(r));

// A store on a manifest with a few slices and a poll that records what it was asked for.
async function recorded() {
  const polls = [];
  const signals = [];
  const slice = (id) => ({ id, version: 1, as_of: 0, body: { id } });
  const store = createStore({
    get: (path, params, signal) => (polls.push({ sub: params.sub.split(","), fresh: params.fresh ? params.fresh.split(",") : [] }), signals.push(signal), new Promise(() => {})),
    manifest: async () => ({ epoch: "e", seq: 1, slices: [slice("team-summary:d/t"), slice("team:d/t"), slice("member:d/t/a"), slice("member:d/t/b"), slice("activity:d/s1"), slice("topology:d/t")], pending: [] }),
  });
  store.start(["summary"]);
  await until(() => store.state.ready && polls.length > 0, "first poll");
  const restarts = () => new Set(signals.filter((s) => s.aborted)).size;
  return { store, polls, restarts, held: () => [...store.state.slices.keys()] };
}

test("G3: a view switch drops no id both views lease, asks for none of them fresh, and restarts once", async () => {
  const r = await recorded();
  r.store.lease("graph", ["team:d/t", "member:d/t/a", "member:d/t/b"]);
  await flush();
  await flush();
  const before = r.restarts();
  const polls = r.polls.length;
  r.store.lease("graph", []);
  r.store.lease("overview", ["team:d/t", "member:d/t/a", "member:d/t/b", "topology:d/t"]);
  await flush();
  assert.ok(["team:d/t", "member:d/t/a", "member:d/t/b"].every((id) => r.held().includes(id)), "common ids were never dropped");
  assert.equal(r.restarts() - before, 1, "one restart");
  const last = r.polls.at(-1);
  assert.ok(r.polls.length > polls);
  assert.ok(!last.fresh.some((id) => ["team:d/t", "member:d/t/a", "member:d/t/b"].includes(id)), "none of the common ids asked for again");
  r.store.stop();
});

test("G3: ids only the old view leased are dropped, ids only the new view leases are asked for fresh", async () => {
  const r = await recorded();
  r.store.lease("graph", ["member:d/t/a", "activity:d/s1"]);
  await flush();
  await flush();
  assert.ok(r.held().includes("activity:d/s1"));
  r.store.lease("graph", []);
  r.store.lease("timeline", ["member:d/t/a", "topology:d/t"]);
  await flush();
  assert.ok(!r.held().includes("activity:d/s1"), "only the old view's id is dropped");
  assert.ok(r.held().includes("member:d/t/a"));
  await until(() => r.polls.at(-1).sub.includes("topology:d/t"), "the poll names the new id");
  assert.ok(r.polls.at(-1).fresh.includes("topology:d/t") || r.held().includes("topology:d/t"));
  assert.ok(!r.polls.at(-1).fresh.includes("member:d/t/a"));
  r.store.stop();
});

test("G3: a lone unmount with no remount still drops in the same microtask", async () => {
  const r = await recorded();
  r.store.lease("graph", ["member:d/t/a"]);
  await flush();
  assert.ok(r.held().includes("member:d/t/a"));
  r.store.lease("graph", []);
  await flush();
  assert.ok(!r.held().includes("member:d/t/a"));
  r.store.stop();
});

test("G3: a batch that ends where it began changes nothing and restarts nothing", async () => {
  const r = await recorded();
  r.store.lease("graph", ["member:d/t/a"]);
  await flush();
  await flush();
  const before = r.restarts();
  r.store.lease("graph", []);
  r.store.lease("graph", ["member:d/t/a"]);
  await flush();
  await flush();
  assert.equal(r.restarts(), before);
  assert.ok(r.held().includes("member:d/t/a"));
  r.store.stop();
});

test("the graph leases only each member's newest exchange; processes belong to the team shell", () => {
  const slices = new Map();
  const ids = [`member:${DIR}/ship/a`, `member:${DIR}/ship/b`];
  ids.forEach((id, i) => slices.set(id, { id, body: { name: "n", activity: `activity:${DIR}/s${i}`, last_exchanges: [`exchange:${DIR}/x${i}1`, `exchange:${DIR}/x${i}2`, `exchange:${DIR}/x${i}3`] } }));
  const team = { id: TEAM, body: { name: "ship", dir_id: DIR, orchestrator: null, members: ids } };
  const got = teamLeaseIds(slices, team, { members: true, open: true });
  assert.ok(got.includes(`exchange:${DIR}/x01`) && got.includes(`exchange:${DIR}/x11`));
  assert.ok(!got.includes(`exchange:${DIR}/x02`) && !got.includes(`exchange:${DIR}/x03`), "the older two are not leased");
  assert.ok(!got.includes(`processes:${DIR}`), "not in a view's set");
});

test("the feed rail pages 20 rows cold, holds no feed slice or exchange slice, and pages again only when feed_newest_at moves", async () => {
  const m = await mountApp({ view: "overview" });
  m.store.setUi({ team: TEAM, view: "overview" });
  const feeds = () => m.log.lists.filter((l) => l.path === `/list/list:feed:team:${DIR}/ship`);
  await until(() => feeds().length >= 1 && find(m.root, "li.feed-row").length > 0, "the rail's rows");
  assert.equal(feeds()[0].params.limit, 20);
  assert.deepEqual(held(m, "list:feed:"), [], "the feed list slice is not leased");
  const n = feeds().length;
  m.model.tick();
  await settle();
  await settle();
  assert.equal(feeds().length, n, "a tick moves no feed");
  m.model.traffic();
  await until(() => feeds().length === n + 1, "one page for the moved feed");
});

test("an open pair sheet keeps receiving the pair's new exchanges after a member is selected (the rail swaps to the panel)", async () => {
  const m = await mountApp();
  const orch = `member:${DIR}/ship/@orchestrator`;
  const impl = `member:${DIR}/ship/implementor-1`;
  m.store.setUi({ team: TEAM, pair: [orch, impl], graphMode: "flow" }); // the feed rail is Flow's; Map's rail is GraphPanel
  const slugs = () => find(m.root, "span").filter((s) => s.attrs.get("class") === "slug").map((s) => text(s));
  await until(() => find(m.root, "li").some((li) => li.attrs.get("class")?.startsWith("pair-row")), "the sheet's rows");
  m.store.setUi({ selected: impl });
  await until(() => find(m.root, "section").some((s) => /\bmsum\b/.test(s.attrs.get("class") ?? "")), "the member summary replaces the feed rail");
  assert.equal(find(m.root, "section").filter((s) => s.attrs.get("class") === "feed").length, 0);
  m.model.traffic();
  await until(() => slugs().includes("ui-graph-comms"), "the new pair exchange");
});

test("an older exchange of the pair that exists only after a newer one is shown too", async () => {
  const m = await mountApp();
  const orch = `member:${DIR}/ship/@orchestrator`;
  const impl = `member:${DIR}/ship/implementor-1`;
  m.store.setUi({ team: TEAM, pair: [orch, impl] });
  const slugs = () => find(m.root, "span").filter((s) => s.attrs.get("class") === "slug").map((s) => text(s));
  await until(() => slugs().length > 0, "rows");
  const ids = m.model.list(`list:exchanges:team:${DIR}/ship`, { limit: 200 }).rows.map((r) => r.id.split("/").pop());
  const body = (x, slug) => ({ dir_id: DIR, id: x, slug, from: "orchestrator", from_name: null, to: "implementor", to_name: "implementor-1", team: "ship", created: new Date().toISOString(), open: true, has_request: true, has_response: false });
  const publishList = (all) => m.model.publish([{ id: `list:exchanges:team:${DIR}/ship`, as_of: Date.now(), body: { team: TEAM, ids: all } }, { id: `list:feed:team:${DIR}/ship`, as_of: Date.now(), body: { team: TEAM, ids: all } }]);
  m.model.publish([{ id: `exchange:${DIR}/29990101-newer`, as_of: Date.now(), body: body("29990101-newer", "newer-one") }]);
  publishList(["29990101-newer", ...ids]);
  await until(() => slugs().includes("newer-one"), "the newer one");
  m.model.publish([{ id: `exchange:${DIR}/29980101-older`, as_of: Date.now(), body: body("29980101-older", "older-one") }]);
  publishList(["29990101-newer", "29980101-older", ...ids]);
  await until(() => slugs().includes("older-one"), "the older one that arrived later");
});

test("the mock's team-summary carries every field of the composed summary and follows its fixture", async () => {
  const { createMock } = await import("../../web/mock/model.mjs");
  const model = createMock();
  const fields = ["team", "name", "work_root", "branch", "running", "members", "working", "blocked", "needs_you", "waiting_on_you", "waiting_rows", "in_progress_now", "stuck", "in_flight", "streams_open", "streams_total", "items_done", "items_total", "oldest_in_flight_at", "updated_at", "feed_newest_at", "alerts", "state", "member_states", "stale_ids", "stale_at", "ended", "status", "held", "quiet_at"];
  const body = model.slice(SUMMARY).body;
  assert.deepEqual(Object.keys(body).sort(), [...fields].sort());
  assert.equal(body.team, TEAM);
  const seq = model.seq;
  model.tick();
  assert.ok(model.seq > seq);
  const idle = model.slice(SUMMARY).version;
  model.publish([{ id: `activity:${DIR}/none`, as_of: Date.now(), body: { activity: "idle" } }]);
  assert.equal(model.slice(SUMMARY).version, idle, "a publish that changes no summary field does not republish it");
  const open = body.in_flight;
  model.traffic();
  assert.ok(model.slice(SUMMARY).body.in_flight !== open || model.slice(SUMMARY).body.feed_newest_at !== body.feed_newest_at, "a brief moves in_flight or the feed");
});

// ---- rev 1.3: the request line

const hexOf = (n, k) => Array.from({ length: n }, (_, i) => ((i * 7 + k) % 16).toString(16)).join("");

// A team of `size` members with ids of the lengths a live home has, and every id one view of it leases.
function bigTeam(size) {
  const dir = "9f3a1c7e5b2d4a68";
  const names = Array.from({ length: size }, (_, i) => (i === 0 ? "@orchestrator" : `implementor-${i}`));
  const mid = (n) => `member:${dir}/ship-ui/${n}`;
  const ex = (i) => `exchange:${dir}/20261018-${1000 + i}-${hexOf(4, i)}`;
  const slices = new Map();
  names.forEach((n, i) => slices.set(mid(n), { id: mid(n), body: { name: n, activity: `activity:${dir}/${hexOf(8, i)}-${hexOf(4, i)}-${hexOf(4, i)}-${hexOf(4, i)}-${hexOf(12, i)}`, last_exchanges: [ex(i), ex(i + 30), ex(i + 60)] } }));
  slices.set(`list:open:team:${dir}/ship-ui`, { body: { open: Array.from({ length: 8 }, (_, i) => ({ id: ex(100 + i) })) } });
  slices.set(`topology:${dir}/ship-ui`, { body: { nodes: { worktrees: names.map((n) => `worktree:${dir}/ship-ui/${n}`) } } });
  const team = { id: `team:${dir}/ship-ui`, body: { name: "ship-ui", dir_id: dir, orchestrator: mid(names[0]), members: names.slice(1).map(mid) } };
  const ids = [team.id, `processes:${dir}`, ...teamLeaseIds(slices, team, { members: true, open: true })];
  return { ids, slices };
}

// Polls a store that leases `ids` against a server that holds none of them, then holds all of them; the request line of each poll.
async function lines(ids) {
  const seen = [];
  const slice = (id) => ({ id, version: 1, as_of: 0, body: {} });
  let first = true;
  const store = createStore({
    get: (path, params) => {
      if (params.sub.split(",").length < 3) {
        return new Promise(() => {});
      }
      seen.push(`GET ${urlOf(path, params)} HTTP/1.1`);
      if (first) {
        first = false;
        return Promise.resolve({ epoch: "e", seq: 2, resync: false, slices: ids.map(slice), removed: [] });
      }
      return new Promise(() => {});
    },
    manifest: async () => ({ epoch: "e", seq: 1, slices: [], pending: [] }),
  });
  store.start(["summary"]);
  store.lease("page", ids);
  await until(() => seen.length >= 2, "first and steady polls");
  store.stop();
  return seen;
}

for (const [size, cap] of [[25, 8192], [50, 16384]]) {
  test(`the first and the steady poll of a ${size}-member team stay within ${cap / 1024} KB of request line`, async () => {
    const [first, steady] = await lines(bigTeam(size).ids);
    assert.match(first, /fresh=\*/, "the first poll asks for everything with one star");
    assert.ok(first.length <= cap, `first poll ${first.length} B`);
    assert.ok(steady.length <= cap, `steady poll ${steady.length} B`);
    assert.doesNotMatch(steady, /fresh=/, "nothing asked for again once held");
  });
}

test("fresh=* is sent only when every exact id is asked for; a partly held subscription sends the delta", async () => {
  const seen = [];
  const store = createStore({
    get: (path, params) => (seen.push(params), new Promise(() => {})),
    manifest: async () => ({ epoch: "e", seq: 1, slices: [{ id: "member:d/t/a", version: 1, as_of: 0, body: {} }], pending: [] }),
  });
  store.start(["summary"]);
  store.lease("page", ["member:d/t/a", "member:d/t/b", "list:open:team:d/t"]);
  await until(() => seen.length > 0 && seen.at(-1).sub.includes("member:d/t/b"), "the poll");
  assert.equal(seen.at(-1).fresh, "member:d/t/b,list:open:team:d/t", "a is held: the explicit delta, no star");
  store.stop();
});

test("a request line the server refuses as too long is retried once with the reduced leases, with a notice", async () => {
  const subs = [];
  const store = createStore({
    get: (path, params) => {
      subs.push(params.sub.split(","));
      if (params.sub.includes("worktree:")) {
        return Promise.reject(Object.assign(new Error("view /events: 414"), { status: 414 }));
      }
      return new Promise(() => {});
    },
    manifest: async () => ({ epoch: "e", seq: 1, slices: [], pending: [] }),
  });
  store.start(["summary"]);
  store.lease("page", ["team:d/t", "member:d/t/a", "activity:d/s1", "worktree:d/t/a", "exchange:d/x1"]);
  await until(() => subs.length >= 2, "the retry");
  const retry = subs.at(-1);
  assert.deepEqual(retry.filter((id) => id.startsWith("worktree:") || id.startsWith("exchange:")), [], "worktrees and exchange labels left out");
  assert.ok(["team:d/t", "member:d/t/a", "activity:d/s1"].every((id) => retry.includes(id)), "the team shell, member and activity rows stay");
  assert.equal(store.state.large, true);
  assert.equal(store.state.error, null, "no error when the reduced request goes through");
  store.stop();
});

test("a reduced request that is still refused ends as an error row naming the size, never silently", async () => {
  const store = createStore({
    get: () => Promise.reject(Object.assign(new Error("view /events: 431"), { status: 431 })),
    manifest: async () => ({ epoch: "e", seq: 1, slices: [], pending: [] }),
    setTimer: (fn) => setTimeout(fn, 1000000),
  });
  store.start(["summary"]);
  store.lease("page", ["team:d/t", "member:d/t/a"]);
  await until(() => store.state.error !== null, "the error");
  assert.match(store.state.error, /^subscription too large \(\d+ ids\)$/);
  assert.equal(store.state.large, true);
  store.stop();
});

test("no notice is drawn while the request fits", async () => {
  const m = await mountApp({ view: "teams" });
  await until(() => rowsOf(m.root).length > 0, "rows");
  assert.equal(find(m.root, "p").filter((p) => p.attrs.get("class") === "notice").length, 0);
});

test("two overlapping refreshes of an open pair sheet add a row once", async () => {
  const m = await mountApp();
  const orch = `member:${DIR}/ship/@orchestrator`;
  const impl = `member:${DIR}/ship/implementor-1`;
  m.store.setUi({ team: TEAM, pair: [orch, impl] });
  const slugs = () => find(m.root, "span").filter((s) => s.attrs.get("class") === "slug").map((s) => text(s));
  await until(() => slugs().length > 0, "rows");
  const row = { id: `exchange:${DIR}/29990101-dup`, body: { dir_id: DIR, id: "29990101-dup", slug: "dup-row", from: "orchestrator", from_name: null, to: "implementor", to_name: "implementor-1", team: "ship", created: new Date().toISOString(), open: true } };
  const waiting = [];
  m.store.fetchList = () => new Promise((done) => waiting.push(() => done({ rows: [row], next: null })));
  m.model.traffic();
  await until(() => waiting.length === 1, "the first refresh");
  m.model.traffic();
  await until(() => waiting.length === 2, "the second refresh, overlapping");
  waiting.forEach((resolve) => resolve());
  await until(() => slugs().includes("dup-row"), "the row");
  await settle();
  assert.equal(slugs().filter((s) => s === "dup-row").length, 1, "shown once");
});
