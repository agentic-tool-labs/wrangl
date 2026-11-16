// ah-ui-focus S4: close and reopen a team from Home and the Board header, against a stubbed route.
import { test } from "node:test";
import assert from "node:assert/strict";

process.env.AH_MOCK_FIXTURE = "lanes";
const { mountApp, until, find, text, DIR } = await import("./mount-app.mjs");
const { closePlan, TeamCloseButton } = await import("../../web/ui/team-close.js");
const { summaryRow, teamRows } = await import("../../web/ui/select.js");
const { stateWord } = await import("../../web/ui/teams.js");

const summary = (name, body) => ({ id: `team-summary:${DIR}/${name}`, as_of: Date.now(), body: { team: `team:${DIR}/${name}`, name, running: true, members: 1, working: 1, blocked: 0, needs_you: 0, alerts: [], member_states: {}, ...body } });
const button = (root, label) => find(root, "button").find((b) => b.getAttribute("aria-label") === label || text(b) === label);

const PREVIEW = { ok: true, preview: true, live: [], unknown: [], held: [], left_running: [] };

// The route's replies in order; each request body is kept.
function stubRoute(...replies) {
  const sent = [];
  const real = globalThis.fetch;
  globalThis.fetch = async (url, init) => {
    if (url !== "/ui/v1/team-close") {
      return real(url, init);
    }
    sent.push(JSON.parse(init.body));
    const [status, body] = replies.shift();
    return { status, json: async () => body };
  };
  return { sent, restore: () => (globalThis.fetch = real) };
}

test("a team with no record offers no Close; negative: a team with one does", () => {
  const draw = (no_record) => {
    const row = summaryRow({ id: `team-summary:${DIR}/q`, as_of: 1, body: { team: `team:${DIR}/q`, name: "q", ...(no_record ? { no_record } : {}) } });
    return TeamCloseButton.prototype.draw.call({ state: {} }, null, { name: row.name, closed: false, lane: false, noRecord: row.noRecord });
  };
  assert.equal(draw(true), null);
  assert.notEqual(draw(false), null);
});

test("nobody live and nothing outstanding: one click, Close, Nothing outstanding", () => {
  const plan = closePlan(PREVIEW, null);
  assert.equal(plan.confirm, "Close");
  assert.equal(plan.outstanding, false);
  assert.deepEqual(plan.confirmLive, []);
});

test("outstanding work, nobody live: Close anyway, with the counts and reasons", () => {
  const plan = closePlan({ ...PREVIEW, held: [{ member: "dev", status: "not-running-dirty", detail: "1 file" }] }, { inFlight: 2, itemsTotal: 5, itemsDone: 4, waitingOnYou: 3 });
  assert.equal(plan.confirm, "Close anyway");
  assert.deepEqual(plan.counts, ["2 open exchanges", "1 open item", "3 waiting on you"]);
  assert.deepEqual(plan.reasons, ["dev: not-running-dirty, 1 file"]);
});

test("a live member is listed and the button says it ends sessions; unknown only says may be running", () => {
  const live = closePlan({ ...PREVIEW, live: [{ member: "dev", status: "busy", detail: "" }, { member: "qa" }] }, null);
  assert.equal(live.confirm, "End 2 sessions and close");
  assert.deepEqual(live.confirmLive, ["dev", "qa"]);
  assert.deepEqual(live.live.map((m) => m.doing), ["busy", ""]);

  const now = Date.parse("2026-11-02T12:00:00Z");
  const unknown = closePlan({ ...PREVIEW, unknown: [{ member: "dev" }] }, { memberLife: { dev: now - 3_600_000 } }, now);
  assert.equal(unknown.confirm, "Close anyway", "a member not known live is not offered as ended");
  assert.deepEqual(unknown.confirmLive, []);
  assert.match(unknown.unknown[0], /^dev may be running, not seen since .+ ago$/);
});

test("the Orchestrator is named as left running and holds nothing open; a live member left running does", () => {
  const orchestrator = closePlan({ ...PREVIEW, left_running: ["orchestrator"] }, null);
  assert.deepEqual([orchestrator.leftRunning, orchestrator.holdsOpen], [["orchestrator"], []]);
  const member = closePlan({ ...PREVIEW, left_running: ["orchestrator", "dev"] }, null);
  assert.deepEqual(member.holdsOpen, ["dev"]);
});

test("the dialog says the Orchestrator is left running and not that the team stays visible", async () => {
  const route = stubRoute([200, { ...PREVIEW, left_running: ["orchestrator"] }]);
  try {
    const m = await mountApp({ view: null });
    await until(() => button(m.root, "Close ship"), "the Close button");
    button(m.root, "Close ship").fire("click");
    await until(() => find(m.root, ".dialog").length === 1, "the dialog");
    const dialog = text(find(m.root, ".dialog")[0]);
    assert.match(dialog, /Left running: orchestrator\./);
    assert.doesNotMatch(dialog, /stays visible/);
  } finally {
    route.restore();
  }
});

test("a live member left running keeps the dialog's stays-visible line", async () => {
  const route = stubRoute([200, { ...PREVIEW, left_running: ["orchestrator", "dev"] }]);
  try {
    const m = await mountApp({ view: null });
    await until(() => button(m.root, "Close ship"), "the Close button");
    button(m.root, "Close ship").fire("click");
    await until(() => find(m.root, ".dialog").length === 1, "the dialog");
    assert.match(text(find(m.root, ".dialog")[0]), /The team stays visible until dev stops\./);
  } finally {
    route.restore();
  }
});

test("a lane has no Close, and one the daemon retired has a Reopen in its header", async () => {
  const route = stubRoute([200, { ok: true, reopened: true }]);
  try {
    const m = await mountApp({ view: null });
    await until(() => teamRows(m.store.state.slices).some((r) => r.laneOf !== null), "a lane");
    const lane = teamRows(m.store.state.slices).find((r) => r.laneOf !== null);
    m.store.setUi({ view: "board", team: lane.id, plansLayout: "board" });
    await until(() => find(m.root, ".plans-hide").length === 0 && find(m.root, "button").some((b) => /Blocked/.test(text(b))), "the lane's board");
    assert.equal(button(m.root, `Close team ${lane.name}`), undefined);

    const { body } = m.store.state.slices.get(`team-summary:${DIR}/${lane.name}`);
    m.model.publish([{ id: `team-summary:${DIR}/${lane.name}`, as_of: Date.now(), body: { ...body, ended: true, closed_at: "2026-11-02T00:00:00.000Z" } }]);
    await until(() => button(m.root, `Reopen team ${lane.name}`), "the lane's Reopen");
    button(m.root, `Reopen team ${lane.name}`).fire("click");
    await until(() => route.sent.length === 1, "the reopen");
    assert.deepEqual(route.sent[0], { dir: DIR, team: lane.name, reopen: true });
  } finally {
    route.restore();
  }
});

test("a closed team reads Closed, and an ended one that was never closed still reads Ended", () => {
  const closed = summaryRow(summary("t", { ended: true, closed_at: "2026-11-02T00:00:00.000Z" }));
  assert.ok(closed.closedAt > 0);
  assert.equal(stateWord(closed), "Closed");
  assert.equal(stateWord(summaryRow(summary("t", { ended: true }))), "Ended");
});

test("Home: Close opens the daemon's preview, confirming sends the names shown, and Undo reopens", async () => {
  const route = stubRoute(
    [200, { ...PREVIEW, live: [{ member: "dev", status: "busy" }] }],
    [200, { ok: true, closed: true, still_live: [], left_running: ["orchestrator"] }],
    [200, { ok: true, reopened: true }],
  );
  try {
    const m = await mountApp({ view: null });
    await until(() => button(m.root, "Close ship"), "the Close button");
    button(m.root, "Close ship").fire("click");
    await until(() => find(m.root, ".dialog").length === 1, "the dialog");
    const dialog = find(m.root, ".dialog")[0];
    assert.match(text(dialog), /Closing ends these running sessions/);
    assert.match(text(dialog), /dev · busy/);
    assert.equal(route.sent.length, 1, "nothing but the preview before a confirmation");
    assert.deepEqual(route.sent[0], { dir: DIR, team: "ship", preview: true });

    button(dialog, "End 1 session and close").fire("click");
    await until(() => find(m.root, ".dialog").length === 0, "the dialog gone");
    assert.deepEqual(route.sent[1], { dir: DIR, team: "ship", confirm_live: ["dev"] });

    await until(() => /Closed ship\. orchestrator is still running\./.test(text(m.root)), "the note");
    button(m.root, "Undo").fire("click");
    await until(() => route.sent.length === 3, "the reopen");
    assert.deepEqual(route.sent[2], { dir: DIR, team: "ship", reopen: true });
    await until(() => !/Closed ship\./.test(text(m.root)), "the note gone");
  } finally {
    route.restore();
  }
});

test("a click inside the close control stops at it: the row under it does not open the team", async () => {
  const route = stubRoute([200, PREVIEW]);
  try {
    const m = await mountApp({ view: null });
    await until(() => button(m.root, "Close ship"), "the Close button");
    button(m.root, "Close ship").fire("click");
    await until(() => find(m.root, ".dialog").length === 1, "the dialog");
    let stopped = false;
    find(m.root, ".team-close")[0].fire("click", { stopPropagation: () => (stopped = true) });
    assert.ok(stopped, "the dialog sits in the row's cell, so a click on its text or backdrop must not reach the row");
  } finally {
    route.restore();
  }
});

test("the Board's team header offers Close on the board layout, and no team has one on the all-teams Board", async () => {
  const m = await mountApp({ view: null });
  m.store.setUi({ view: "board", team: `team:${DIR}/ship`, plansLayout: "board" });
  await until(() => button(m.root, "Close ship"), "the header's Close");
  m.store.setUi({ view: "allplans", team: null });
  await until(() => !button(m.root, "Close ship"), "no Close without a team");
});

test("someone started after the dialog opened: the fresh table replaces it and the close did not happen", async () => {
  const route = stubRoute(
    [200, PREVIEW],
    [409, { refused: true, reason: "live-changed", detail: "running: dev", preview: { ...PREVIEW, live: [{ member: "dev", status: "busy" }] } }],
  );
  try {
    const m = await mountApp({ view: null });
    await until(() => button(m.root, "Close ship"), "the Close button");
    button(m.root, "Close ship").fire("click");
    await until(() => find(m.root, ".dialog").length === 1, "the dialog");
    find(find(m.root, ".dialog")[0], ".primary")[0].fire("click");
    await until(() => /Someone started after this opened/.test(text(m.root)), "the fresh table");
    const dialog = find(m.root, ".dialog")[0];
    assert.match(text(dialog), /dev · busy/);
    assert.ok(button(dialog, "End 1 session and close"));
    assert.ok(!/Closed ship\./.test(text(m.root)));
  } finally {
    route.restore();
  }
});

test("Cancel sends nothing; a closed team offers Reopen with no dialog; a lane has no button", async () => {
  const route = stubRoute([200, PREVIEW], [200, { ok: true, reopened: true }]);
  try {
    const m = await mountApp({ view: null });
    await until(() => button(m.root, "Close ship"), "the Close button");
    assert.equal(find(m.root, "tr").filter((r) => r.attrs.has("data-lane")).flatMap((r) => find(r, "button")).filter((b) => /^(Close|Reopen)/.test(b.getAttribute("aria-label") ?? "")).length, 0);
    button(m.root, "Close ship").fire("click");
    await until(() => find(m.root, ".dialog").length === 1, "the dialog");
    button(find(m.root, ".dialog")[0], "Cancel").fire("click");
    await until(() => find(m.root, ".dialog").length === 0, "the dialog gone");
    assert.equal(route.sent.length, 1);

    m.model.publish([summary("old", { running: false, members: 2, working: 0, state: "ended", closed_at: "2026-11-02T00:00:00.000Z", updated_at: "2026-11-02T00:00:00.000Z" })]);
    await until(() => find(m.root, ".home-toggle").length > 0, "the ended fold");
    find(find(m.root, ".home-toggle")[0], "input")[0].fire("change", { target: { checked: true } });
    await until(() => button(m.root, "Reopen old"), "the Reopen button");
    assert.equal(button(m.root, "Close old"), undefined);
    assert.ok(/Closed/.test(text(find(m.root, "tr").find((r) => /old/.test(text(r))))), "the row reads Closed");
    button(m.root, "Reopen old").fire("click");
    await until(() => route.sent.length === 2, "the reopen");
    assert.deepEqual(route.sent[1], { dir: DIR, team: "old", reopen: true });
    assert.equal(find(m.root, ".dialog").length, 0, "a reopen asks nothing");
  } finally {
    route.restore();
  }
});
