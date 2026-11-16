import { test } from "node:test";
import assert from "node:assert/strict";
import { settle } from "./dom.mjs";
import { mountApp, until, find, text, DIR } from "./mount-app.mjs";
import { fixture, served } from "./lib.mjs";
import { buildSlices } from "../../web/mock/fixture.mjs";
import { globalKey } from "../../web/ui/keys.js";

// The Lanes view shows the lane facts the team and member slices serve, as served: confirmed lane, bare claim, the manager's streams
// (cut at 100 with a total) and where each member's messages go. The mock's lane facts come from the daemon's recorded bodies.

const teamId = `team:${DIR}/ship`;
const section = (m) => find(m.root, "section.lanes")[0];
const within = (m, outer, inner) => (find(m.root, outer)[0]?.all(inner) ?? []);

async function withTeam(patch) {
  const m = await mountApp({ view: "lanes" });
  await until(() => section(m) && find(m.root, "div.ln-leads").length === 1, "the lanes view");
  const base = m.store.state.slices.get(teamId).body;
  const body = { ...base, ...patch };
  for (const k of ["lane", "lane_claim", "streams", "streams_total"]) {
    if (!(k in patch)) {
      delete body[k];
    }
  }
  m.model.publish([{ id: teamId, as_of: Date.now(), body }]);
  await settle();
  return m;
}

const GOLDEN = Object.fromEntries(served("lane-slices.json").slices.map((x) => [x.id, x.body]));
const golden = (suffix) => Object.entries(GOLDEN).find(([id]) => id.endsWith(suffix))[1];
const keys = (o) => Object.keys(o);

test("the mock's lane facts have exactly the shape the daemon records: streams rows, the lane with its report, and member lead and routes_to", () => {
  const slices = buildSlices(Date.now());
  const body = (id) => slices.find((x) => x.id === id).body;
  const ship = body(teamId);
  const manager = golden("/mgr");
  assert.deepEqual(ship.streams.map(keys), manager.streams.map(keys));
  assert.deepEqual(ship.streams, manager.streams);
  assert.equal("lane" in ship || "lane_claim" in ship, false, "a manager is not itself a lane here");
  const lane = golden("/mgr-s1").lane;
  assert.deepEqual(keys(lane), ["stream", "parent_team", "state", "manager_rev", "reported"]);
  assert.deepEqual(keys(lane.reported).filter((k) => k !== "result"), ["state", "summary", "updated"]);
  const dev = golden("/mgr/dev");
  const gone = golden("/mgr/gone");
  assert.deepEqual(keys(dev.routes_to), ["team", "name", "via"]);
  assert.deepEqual(keys(dev.lead), ["team", "name"]);
  assert.equal("lead" in gone, false, "a dissolved lane's member is routed with no lead");
  assert.deepEqual(body(`member:${DIR}/ship/implementor-1`).routes_to, dev.routes_to);
  assert.deepEqual(body(`member:${DIR}/ship/implementor-2`).routes_to, gone.routes_to);
  assert.equal("lead" in body(`member:${DIR}/ship/implementor-2`), false);
});

test("a manager lists its streams with state by word and glyph, and only a stream with a lane team links to it", async () => {
  const m = await mountApp({ view: "lanes" });
  await until(() => within(m, "div.ln-streams", "tr.cl-row").length === 2, "the streams");
  const rows = within(m, "div.ln-streams", "tr.cl-row");
  assert.match(text(rows[0]), /s1\s*◐ open\s*b-s1\s*main\s*so-s1\s*mgr-s1/);
  assert.equal(rows[0].all("button.ln-link").length, 1);
  assert.equal(rows[1].all("button.ln-link").length, 0, "no lane team, no link");
  assert.match(text(rows[1].all("td.cl-state")[0]), /✓ done/);
  assert.match(text(within(m, "div.ln-streams", "div.cl-summary")[0]), /1 open · 2 streams/);
  assert.doesNotMatch(text(section(m)), / of \d+ streams/, "not cut, no total");
  assert.equal(find(m.root, "div.ln-lane").length + find(m.root, "p.ln-claim").length, 0);
});

test("a member's lead is shown raw, and routes_to is shown as served when it is not the lead, never worked out by the view", async () => {
  const m = await mountApp({ view: "lanes" });
  await until(() => within(m, "div.ln-leads", "tr.cl-row").length === 2, "the routed members");
  const by = (name) => within(m, "div.ln-leads", "tr.cl-row").find((r) => text(r.all("td")[0]) === name);
  assert.match(text(by("implementor-1")), /so-s1@mgr-s1\s*the lead/);
  assert.match(text(by("implementor-2")), /—\s*⚠ orchestrator@mgr \(the lane was dissolved\)/, "routed with no lead");
  assert.match(by("implementor-2").all("td")[2].className, /\battn\b/);
  assert.match(text(within(m, "div.ln-leads", "div.cl-summary")[0]), /2 of \d+ members have a lead or a route/);
  assert.doesNotMatch(text(section(m)), /\blive\b/i, "routing is never shown as liveness");
  const reviewerId = `member:${DIR}/ship/reviewer`;
  const reviewer = m.store.state.slices.get(reviewerId).body;
  m.model.publish([{ id: reviewerId, as_of: Date.now(), body: { ...reviewer, lead: { team: "no-such-team", name: "ghost" }, routes_to: { team: "no-such-team", name: "ghost", via: "none" } } }]);
  await until(() => by("reviewer") !== undefined, "the unresolved lead");
  assert.match(text(by("reviewer")), /ghost@no-such-team\s*⚠ ghost@no-such-team \(the lead was not found\)/);
});

test("a confirmed lane shows its stream badge, a link to its manager, the manager's stream state and the sub-orchestrator's report as reported", async () => {
  const summary = `${"x".repeat(279)}…`;
  const m = await withTeam({ lane: { stream: "ui-r6", parent_team: "release", state: "open", manager_rev: "abc", reported: { state: "blocked", summary, updated: new Date(Date.now() - 4 * 60_000).toISOString(), result: "/w/out.md" } } });
  await until(() => find(m.root, "div.ln-lane").length === 1, "the lane");
  const lane = find(m.root, "div.ln-lane")[0];
  assert.match(text(lane.all("span.ln-badge")[0]), /lane ui-r6/);
  assert.equal(lane.all("button.ln-link").length, 1);
  assert.match(text(lane), /Reported by its sub-orchestrator:\s*⚠ blocked/);
  assert.equal(text(lane.all("p.ln-summary")[0]), summary, "the daemon's cut text is shown whole, wrapped");
  assert.match(text(lane.all("p")[0]), /stream ◐ open \(the manager's record\)/);
  assert.match(text(lane.all("p.ln-result")[0]), /\/w\/out\.md/);
  assert.equal(find(m.root, "div.ln-streams").length, 0);
});

test("a claim the manager does not confirm reads as a claim, with no badge, no link and no streams entry", async () => {
  const m = await withTeam({ lane_claim: { parent_team: "release" } });
  await until(() => find(m.root, "p.ln-claim").length === 1, "the claim");
  assert.match(text(find(m.root, "p.ln-claim")[0]), /\?\s*Claims to be a lane of release, not confirmed by release\./);
  assert.equal(find(m.root, "span.ln-badge").length, 0);
  assert.equal(find(m.root, "p.ln-claim")[0].all("button").length, 0);
  assert.equal(find(m.root, "div.ln-lane").length + find(m.root, "div.ln-streams").length, 0);
});

test("streams cut at 100 say how many of the total are shown", async () => {
  const streams = Array.from({ length: 100 }, (_, i) => ({ name: `s${i}`, state: "open", branch: null, base: null, sub_orchestrator: `so-${i}` }));
  const m = await withTeam({ streams, streams_total: 150 });
  await until(() => within(m, "div.ln-streams", "tr.cl-row").length === 100, "100 rows");
  assert.match(text(within(m, "div.ln-streams", "div.cl-summary")[0]), /100 open · 100 of 150 streams/);
  assert.match(text(within(m, "div.ln-streams", "tr.cl-row")[0].all("td")[2]), /—/, "a null branch is a dash, not invented");
});

test("a top-level team says it is not a lane and manages no streams", async () => {
  const m = await withTeam({});
  await until(() => /not a lane and manages no streams/.test(text(section(m))), "the empty state");
});

test("text a lane child writes renders as literal text and creates no element", async () => {
  const evil = "<img src=x onerror=alert(1)>";
  const m = await withTeam({ lane: { stream: "s1", parent_team: "mgr", state: "open", manager_rev: "abc", reported: { state: "working", summary: evil, updated: new Date().toISOString(), result: evil } } });
  await until(() => find(m.root, "div.ln-lane").length === 1, "the lane");
  const lane = find(m.root, "div.ln-lane")[0];
  assert.equal(text(lane.all("p.ln-summary")[0]), evil);
  assert.match(text(lane.all("p.ln-result")[0]), /<img src=x onerror=alert\(1\)>/);
  assert.equal(find(m.root, "img").length, 0, "no element from the summary or result");
  const claimed = await withTeam({ lane_claim: { parent_team: evil } });
  await until(() => find(claimed.root, "p.ln-claim").length === 1, "the claim");
  assert.match(text(find(claimed.root, "p.ln-claim")[0]), /Claims to be a lane of <img src=x onerror=alert\(1\)>/);
  assert.equal(find(claimed.root, "img").length, 0, "no element from the claimed parent");
  assert.equal(find(claimed.root, "p.ln-claim")[0].all("[title]").length, 0, "no attribute carries it");
});

const rgb = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
const lin = (c) => ((c /= 255) <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
const lum = (c) => c.reduce((n, v, i) => n + [0.2126, 0.7152, 0.0722][i] * lin(v), 0);
const ratio = (a, b) => (Math.max(lum(a), lum(b)) + 0.05) / (Math.min(lum(a), lum(b)) + 0.05);

test("the team links meet 4.5:1 on the page and the row hover in both themes", () => {
  const tokens = fixture("visual-tokens.json");
  for (const theme of ["dark", "light"]) {
    const set = tokens[theme];
    for (const bg of ["bg", "row-hi"]) {
      assert.ok(ratio(rgb(set.accent), rgb(set[bg])) >= 4.5, `${theme} link on ${bg}`);
      assert.ok(ratio(rgb(set.working), rgb(set[bg])) >= 4.5, `${theme} link hover on ${bg}`);
    }
  }
});
