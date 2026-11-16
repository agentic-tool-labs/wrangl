import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { firstTeam, memberOf, memberIds, partyName, openBriefsOf, summaryRow } from "../../web/ui/select.js";
import { flattenTree } from "../../web/ui/treeview.js";
import { backlogColumn } from "../../web/ui/board-kanban.js";

// The slices below are the committed C# golden (TreeLanesGoldenTests), i.e. what the server really publishes.
const golden = JSON.parse(readFileSync(new URL("../../native/tests/Ah.Watch.Tests/Goldens/tree-exchange-slices.json", import.meta.url), "utf8"));
const map = new Map(golden.slices.map((s) => [s.id, s]));
const team = firstTeam(map);
const D = "f28aaefa";
const ex = (n) => `exchange:${D}/${n}`;
const mem = (n) => `member:${D}/default/${n}`;

test("the golden has the real shapes the client reads", () => {
  assert.equal(team.id, `team:${D}/default`);
  assert.deepEqual(map.get(`list:feed:team:${D}/default`).body, {
    team: `team:${D}/default`,
    ids: ["x5", "x3", "x2", "x1"],
    pairs: [
      { a: "@orchestrator", b: "dev", n: 2, recent_1h: [], last_response: null },
      { a: "@orchestrator", b: "qa", n: 1, recent_1h: [], last_response: null },
      { a: "dev", b: "qa", n: 1, recent_1h: [], last_response: null },
    ],
    unresolved: { n: 0 },
  }, "a team list is {team, ids, pairs, unresolved}, newest first, with no page cursors");
  const x1 = map.get(ex("x1")).body;
  assert.equal(x1.to, "developer", "to is a role");
  assert.equal(x1.to_name, "dev", "to_name is the member name");
  assert.equal(x1.from_name, null, "a party can have no name");
  for (const key of ["title", "title_source", "response_created", "withdrawn"]) {
    assert.ok(key in x1, `an exchange body carries ${key}`);
  }
});

test("a member's sub-label and recent lines come from its last_exchanges, with the server's names", () => {
  const dev = memberOf(map, team, mem("dev"));
  assert.equal(dev.slug, "s");
  assert.deepEqual(dev.recent.split("\n"), ["s|1|orchestrator|dev", "s|1|dev|qa", "s|1|orchestrator|dev"]);
  assert.equal(memberOf(map, team, mem("qa")).slug, "done", "qa's newest is the answered x3");
});

// TODO: assert sparks from the golden's list:open:team slice once the server publishes it and the golden is regenerated.
test("an exchange of another team is on no member; one with no team belongs to the team named default, so its two ends hold it", () => {
  for (const id of memberIds(team)) {
    assert.ok(!map.get(id).body.last_exchanges.includes(ex("x4")), id);
  }
  assert.equal(map.get(ex("x5")).body.team, null);
  const holders = memberIds(team).filter((id) => map.get(id).body.last_exchanges.includes(ex("x5")));
  assert.deepEqual(holders.sort(), [mem("@orchestrator"), mem("dev")].sort());
});

// TODO: assert the tree's brief rows from the golden's list:open:team slice once the server publishes it and the golden is regenerated.
test("without the open list the tree lists no briefs: it never derives an addressee from the exchange bodies", () => {
  assert.deepEqual(flattenTree(map, team).filter((r) => r.kind === "brief"), []);
  assert.deepEqual(openBriefsOf(map, team, mem("qa")), []);
});

test("the summary golden both front ends read: its row, and the daemon's status fields", () => {
  const slice = map.get(`team-summary:${D}/default`);
  assert.equal(slice.body.ended, false);
  assert.equal(slice.body.status, "live");
  assert.equal(slice.body.stale_at, "2026-06-02T11:00:00.000Z");
  const row = summaryRow(slice);
  assert.deepEqual([row.name, row.ended, row.status, row.quietAt, row.held, row.members, row.needs], ["default", false, "live", Date.parse(slice.body.quiet_at ?? "") || 0, false, 3, 0]);
  assert.ok("quiet_at" in slice.body && "held" in slice.body, "both are served");
});

test("the served member and brief fields both front ends read: context, process, stale_ids, unattended_at", () => {
  const dev = map.get(mem("dev")).body;
  assert.deepEqual([dev.context_pct, dev.context_text, dev.context_over], [null, "—", false]);
  const summary = map.get(`team-summary:${D}/default`).body;
  assert.equal(summary.member_states[mem("dev")].process, "unknown");
  assert.deepEqual(summary.stale_ids, []);
  const open = JSON.parse(readFileSync(new URL("../../native/tests/Ah.Watch.Tests/Goldens/tree-open-exchanges.json", import.meta.url), "utf8")).slices[`list:open:team:${D}/default`].open;
  assert.ok(open.some((o) => o.to_member === null && o.unattended_at));
  assert.ok(open.filter((o) => o.to_member).every((o) => !("unattended_at" in o)), "a row with an addressee carries no unattended_at");
});

test("partyName prefers the name and never matches names against roles", () => {
  assert.equal(partyName(map.get(ex("x2")).body, "to"), "qa");
  assert.equal(partyName(map.get(ex("x1")).body, "from"), "orchestrator");
});

// The same golden and values as BoardViewTests.TheServedGoldenFillsTheBoardsFields.
test("the board's golden gives both front ends the same cards", () => {
  const plan = new Map(JSON.parse(readFileSync(new URL("../../native/tests/Ah.Watch.Tests/Goldens/plan-slices.json", import.meta.url), "utf8")).slices.map((s) => [s.id, s.body]));
  const [x, y] = plan.get(`plan-items:${D}/t/a`).items;
  assert.deepEqual([x.title, x.column, x.state, x.owner], ["Build x", "progress", "in_progress", "impl-1"]);
  assert.deepEqual([y.title, y.column, y.state, y.owner], ["Build y", "next", "not_started", "qa-1"]);
  const stream = plan.get(`plan:${D}/t`).streams.find((s) => s.stream === "a");
  assert.deepEqual([stream.owner, stream.branch, stream.goal], ["impl-1", "ah/a", "Goal A"]);
  assert.deepEqual(backlogColumn(plan.get(`backlog:${D}`)).flatMap((g) => g.rows).map((i) => i.title), ["Golden now", "Golden later"]);
});
