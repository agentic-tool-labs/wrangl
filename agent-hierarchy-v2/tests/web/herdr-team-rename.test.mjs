// ah2-218 B: a team's display name in the herdr pane. The rows the sidebar and sitrep draw (model.js, through select.js teamLabel),
// and the Rename dialog's rule and its one call (mods/ah2/rename-model.js), against fakes.
import { test } from "node:test";
import assert from "node:assert/strict";
import { sidebarModel, sitrepModel, deskModel } from "../../herdr-desktop/mods/ah2/model.js";
import { teamRename, renameForm, renameOutcome } from "../../herdr-desktop/mods/ah2/rename-model.js";

const NOW = Date.parse("2026-11-18T12:00:00Z");
const summary = (name, extra = {}) => ({ name, running: true, member_states: {}, alerts: [], ...extra });
const slicesOf = (bodies) => new Map([
  ["list:teams", { id: "list:teams", body: { teams: Object.keys(bodies).map((name) => ({ dir_id: "d1", name })) } }],
  ...Object.entries(bodies).map(([name, b]) => [`team-summary:d1/${name}`, { id: `team-summary:d1/${name}`, body: b }]),
]);

test("the sidebar row shows the display name and keeps the real one for the tooltip and every key; a lane uses its own", () => {
  const m = sidebarModel(slicesOf({
    mgr: summary("mgr", { display_name: "Payments", waiting_on_you: 1 }),
    "mgr-ui": summary("mgr-ui", { lane: { parent_team: "mgr", stream: "ui" } }),
  }), NOW);
  const top = m.teams.find((t) => t.name === "mgr");
  assert.deepEqual([top.id, top.name, top.label, top.real], ["d1/mgr", "mgr", "Payments", "mgr"]);
  const lane = top.lanes[0] ?? m.teams.find((t) => t.name === "mgr-ui");
  assert.deepEqual([lane.label, lane.real], ["mgr-ui", null], "a lane inherits nothing");
  assert.deepEqual(deskModel(slicesOf({ mgr: summary("mgr", { display_name: "Payments", waiting_on_you: 1 }) }), NOW).needing.map((t) => [t.id, t.name]), [["d1/mgr", "Payments"]]);
  // negative: no display name reads as today
  const plain = sidebarModel(slicesOf({ mgr: summary("mgr", { display_name: "  " }) }), NOW).teams[0];
  assert.deepEqual([plain.label, plain.real], ["mgr", null]);
});

test("the sitrep header names the display name and hands the dialog the real name and the current value", () => {
  const slices = slicesOf({ mgr: summary("mgr", { display_name: "Payments" }) });
  slices.set("sitrep:d1/mgr", { id: "sitrep:d1/mgr", body: { regions: [] } });
  const s = sitrepModel(slices, "d1/mgr", NOW);
  assert.deepEqual([s.name, s.realName, s.displayName, s.real], ["Payments", "mgr", "Payments", "mgr"]);
  slices.set("team-summary:d1/mgr", { id: "team-summary:d1/mgr", body: summary("mgr") });
  const none = sitrepModel(slices, "d1/mgr", NOW);
  assert.deepEqual([none.name, none.displayName, none.real], ["mgr", null, null]);
});

test("the dialog's rule: empty only once touched, too long and a line break at once; Clear only when one is set", () => {
  assert.deepEqual(renameForm(""), { problem: null, canSave: false, clear: false });
  assert.match(renameForm("  ", { touched: true }).problem, /^Empty/);
  assert.equal(renameForm("x".repeat(61)).problem, "61 characters. At most 60.");
  assert.match(renameForm("a\nb").problem, /line breaks/);
  assert.deepEqual(renameForm(" Payments ", { current: "Pay" }), { problem: null, canSave: true, clear: true });
});

test("the call: the bridge first with the real team; through ah only when nothing reached ahd; a failure is a reason, not a close", async () => {
  const posted = [];
  const ran = [];
  const exec = async (cmd, args) => {
    ran.push([cmd, ...args]);
    return { code: 0, stdout: JSON.stringify({ ok: true, team: "mgr", display_name: "Payments", previous: null }), stderr: "" };
  };
  const bridge = await teamRename(exec, async (body) => (posted.push(body), { status: 200, reply: { ok: true } }), "d1/mgr", "Payments");
  assert.deepEqual([bridge.status, posted, ran], [200, [{ dir: "d1", team: "mgr", display_name: "Payments" }], []]);

  const viaAh = await teamRename(exec, async () => null, "d1/mgr", "Payments", { workRoot: "/w" });
  assert.equal(viaAh.status, 200);
  assert.deepEqual(ran.at(-1), ["ah", "roster", "rename", "Payments", "--team", "mgr", "--cwd", "/w"]);
  await teamRename(exec, null, "d1/mgr", null);
  assert.deepEqual(ran.at(-1), ["ah", "roster", "rename", "--clear", "--team", "mgr"]);

  const refused = await teamRename(async () => ({ code: 1, stdout: "", stderr: "rename: the display name is empty after trimming; nothing was changed\n" }), null, "d1/mgr", " ");
  assert.deepEqual(renameOutcome(refused), { done: false, error: "Could not rename: rename: the display name is empty after trimming; nothing was changed" });
  const invalid = { status: 400, reply: { error: "invalid", detail: "the display name has a control character or a line break" } };
  assert.equal(renameOutcome(invalid).error, "Could not rename: the display name has a control character or a line break");
  const lost = await teamRename(exec, async () => { throw new Error("no answer"); }, "d1/mgr", "Payments");
  assert.equal(renameOutcome(lost).done, false, "a call ahd did not answer keeps the dialog open");
  assert.deepEqual(renameOutcome({ status: 200, reply: {} }), { done: true, error: null });
});
