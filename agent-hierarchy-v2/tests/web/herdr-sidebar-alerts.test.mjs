// ah2-133 D4: the team's "Waiting on you" line sits with the team header, above the member rows; a member's own alert stays after them.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { sidebarModel } from "../../herdr-desktop/mods/ah2/model.js";
import { summaryRow as modSummaryRow } from "../../herdr-desktop/mods/ah2/select.js";

const NOW = Date.parse("2026-11-17T20:45:00Z");
const slices = new Map([
  ["list:teams", { id: "list:teams", body: { teams: [{ dir_id: "d1", name: "ah2" }] } }],
  ["team-summary:d1/ah2", { id: "team-summary:d1/ah2", body: { name: "ah2", running: true,
    member_states: { "d1/ah2/@reviewer-2": { state: "idle", liveness: "live" }, "d1/ah2/@dev": { state: "blocked", liveness: "live" } },
    alerts: [
      { kind: "needs_you", since: "2026-11-17T20:40:00Z", text: "Waiting on you · 2" },
      { kind: "blocked", member: "d1/ah2/@dev", since: "2026-11-17T20:41:00Z", text: "Waiting on a permission" },
    ] } }],
]);

test("the team-level waiting line carries no member; a member's own alert names its member", () => {
  const [team] = sidebarModel(slices, NOW).teams;
  assert.equal(team.alerts.find((a) => a.kind === "needs_you").member, null);
  // negative: a member's blocked alert is not taken for the team's
  assert.equal(team.alerts.find((a) => a.kind === "blocked").member, "d1/ah2/@dev");
});

test("ah2-180: a raised user alarm is a team-level sidebar alert and the mod's own card for it is Alarm with no member", () => {
  const since = "2026-11-17T20:42:00Z";
  const withAlarm = new Map([...slices]);
  const summary = slices.get("team-summary:d1/ah2");
  withAlarm.set("team-summary:d1/ah2", { ...summary, body: { ...summary.body, alerts: [...summary.body.alerts,
    { kind: "alarm", audience: "user", since, text: "orchestrator@ah2 has 2 unread" },
    { kind: "stuck", audience: "orchestrator", since, text: "task stuck" }] } });
  const [team] = sidebarModel(withAlarm, NOW).teams;
  const alarm = team.alerts.find((a) => a.kind === "alarm");
  assert.equal(alarm.member, null);
  assert.equal(alarm.text, "orchestrator@ah2 has 2 unread");
  // negative: the orchestrator-audience alert is still dropped
  assert.equal(team.alerts.some((a) => a.kind === "stuck"), false);
  const card = modSummaryRow({ id: "team-summary:d1/ah2", as_of: 1, body: { team: "team:d1/ah2", name: "ah2", alerts: [{ kind: "alarm", audience: "user", since, text: "x", title: "x", detail: "Alarm" }] } }).items[0];
  assert.deepEqual([card.kind, card.key, card.memberId], ["Alarm", `alarm|team:d1/ah2|${since}`, null]);
});

test("the sidebar draws team-level alerts before the tree and a member's own alert right after its row (ah2-207 D5)", () => {
  const src = readFileSync(join(dirname(fileURLToPath(import.meta.url)), "..", "..", "herdr-desktop", "mods", "ah2", "main.js"), "utf8");
  const head = src.indexOf("teamAlerts(tm, null);");
  const tree = src.indexOf("for (const r of treeRows(teamTree(tm), open))");
  const members = src.indexOf("rows.push(this.member(t, n, lead,");
  const tail = src.indexOf("for (const a of n.alerts) rows.push(alert(tm.id, a, r));");
  assert.ok(head > 0 && tree > 0 && members > 0 && tail > 0, "the loops are present");
  assert.ok(head < tree, "the waiting line is drawn above the tree");
  // negative: a member alert is not drawn with the header, and follows its own member's row
  assert.ok(tail > members, "a member alert follows its member's row");
  assert.ok(!/if \(!a\.member\) rows\.push/.test(src.slice(tree, tail)), "no team-level alert inside the tree loop");
});
