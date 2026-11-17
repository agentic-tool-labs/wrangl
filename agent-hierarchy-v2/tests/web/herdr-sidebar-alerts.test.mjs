// ah2-133 D4: the team's "Waiting on you" line sits with the team header, above the member rows; a member's own alert stays after them.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { sidebarModel } from "../../herdr-desktop/mods/ah2/model.js";

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

test("the sidebar draws team-level alerts before the member rows and member alerts after them", () => {
  const src = readFileSync(join(dirname(fileURLToPath(import.meta.url)), "..", "..", "herdr-desktop", "mods", "ah2", "main.js"), "utf8");
  const head = src.indexOf("for (const a of tm.alerts) if (!a.member) rows.push(alert(a));");
  const members = src.indexOf("for (const mb of tm.members) rows.push(this.member(");
  const tail = src.indexOf("for (const a of tm.alerts) if (a.member) rows.push(alert(a));");
  assert.ok(head > 0 && members > 0 && tail > 0, "the three loops are present");
  assert.ok(head < members, "the waiting line is drawn above the members");
  // negative: member alerts are not drawn with the header
  assert.ok(tail > members, "member alerts stay after the members");
});
