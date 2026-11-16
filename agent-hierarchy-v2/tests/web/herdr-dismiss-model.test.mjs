// Dismiss a team from Herdr (ah2-45): mods/ah2/dismiss-model.js, the dialog's plan, steps and its one call, against fakes.
import { test } from "node:test";
import assert from "node:assert/strict";
import { dismissPlan, next, buttonsOf, START, runDismiss, teamClose, closeBody } from "../../herdr-desktop/mods/ah2/dismiss-model.js";

const ok = (reply) => ({ status: 200, reply });
const EMPTY = { ok: true, preview: true, team: "proj", live: [], unknown: [], held: [], left_running: ["orchestrator"], warnings: [] };
const BUSY = { ...EMPTY, live: [{ team: "proj", member: "proj-implementor", role: "implementor", status: "dirty", detail: "2 files not committed" }, { team: "proj", member: "proj-reviewer", role: "reviewer" }] };

// A scripted flow: `replies[kind]` is a list of answers for preview / hold / close calls, `presses` what the user clicks.
async function flow(replies, presses = []) {
  const calls = [];
  const states = [];
  const last = await runDismiss({
    call: async (more) => {
      const kind = more.preview ? "preview" : more.hold ? "hold" : "close";
      calls.push(more);
      return replies[kind].shift();
    },
    sleep: async () => {},
    emit: (s) => states.push(s.step),
    // a plain press is made at a question or an error; {during: "pausing", press} is made while a pause runs. Any other time the
    // user only watches, so the press never comes and the hold call wins the race.
    wait: (st) => {
      const nx = presses[0];
      const mine = typeof nx === "string" ? st.step !== "pausing" : nx?.during === st.step;
      return mine ? Promise.resolve(typeof presses.shift() === "string" ? nx : nx.press) : new Promise(() => {});
    },
  });
  return { last, calls, states };
}

test("a team nobody runs offers Dismiss and Cancel, and a busy one Pause then quit, Force quit and Cancel, naming unsaved work", () => {
  assert.deepEqual(dismissPlan(EMPTY).buttons, ["dismiss", "cancel"]);
  const busy = dismissPlan(BUSY);
  assert.deepEqual(busy.buttons, ["pause", "force", "cancel"]);
  assert.deepEqual(busy.live.map((m) => m.work), ["dirty: 2 files not committed", ""]);
  assert.deepEqual(busy.confirmLive, ["proj-implementor", "proj-reviewer"]);
  assert.deepEqual(closeBody("abcd1234/proj", { preview: true }), { dir: "abcd1234", team: "proj", preview: true });
});

test("a team nobody runs is still confirmed: nothing closes until Dismiss is pressed, and Cancel closes nothing", async () => {
  const { last, calls, states } = await flow({ preview: [ok(EMPTY)], close: [ok({ ok: true, closed: true, still_live: [], left_running: [], warnings: [] })] }, ["dismiss"]);
  assert.equal(last.step, "done");
  assert.deepEqual(calls, [{ preview: true }, { confirm_live: [] }]);
  assert.deepEqual(states, ["reading", "confirm", "closing", "done"]);
  const cancelled = await flow({ preview: [ok(EMPTY)], close: [] }, ["cancel"]);
  assert.equal(cancelled.last.step, "confirm");
  assert.deepEqual(cancelled.calls, [{ preview: true }]);
});

test("pause then quit is two steps: it pauses until ahd says sealed, then closes exactly the members it showed", async () => {
  const { last, calls, states } = await flow({
    preview: [ok(BUSY)],
    hold: [ok({ ok: true, hold: true, sealed: false, waiting_on: ["proj-implementor"] }), ok({ ok: true, hold: true, sealed: true, unanswered: false })],
    close: [ok({ ok: true, closed: true, still_live: [], left_running: ["orchestrator"], warnings: [] })],
  }, ["pause"]);
  assert.deepEqual(states, ["reading", "confirm", "pausing", "pausing", "closing", "done"]);
  assert.deepEqual(calls.map((c) => Object.keys(c)[0]), ["preview", "hold", "hold", "confirm_live"]);
  assert.deepEqual(calls.at(-1).confirm_live, ["proj-implementor", "proj-reviewer"]);
  assert.match(last.note, /Paused: work saved.*orchestrator is still running/);
});

test("the states carry the buttons: a pause can be forced or left, and force quit closes at once", async () => {
  assert.deepEqual(buttonsOf(next(next(START, { type: "preview", got: ok(BUSY) }), { type: "pause" })), ["force", "leave"]);
  const forced = await flow({ preview: [ok(BUSY)], close: [ok({ ok: true, closed: true })] }, ["force"]);
  assert.deepEqual(forced.states, ["reading", "confirm", "closing", "done"]);
  assert.equal(forced.calls.some((c) => c.hold), false);
  const left = await flow({ preview: [ok(BUSY)], hold: [new Promise(() => {})] }, ["pause", { during: "pausing", press: "leave" }]);
  assert.equal(left.last.step, "pausing", "left paused: nothing is closed");
  assert.equal(left.calls.some((c) => c.confirm_live), false);
});

test("negative: a failed read or pause is an error that never spins, and a busy team's error still offers force quit", async () => {
  const unread = await flow({ preview: [{ status: 409, reply: { refused: true, reason: "lane", detail: "ws1 is a lane of proj: close that team" } }] }, ["cancel"]);
  assert.equal(unread.last.step, "error");
  assert.match(unread.last.error, /lane of proj/);
  assert.deepEqual(buttonsOf(unread.last), ["cancel"]);
  // a hold call not answered in time is sent again; three in a row is the error
  const paused = await flow({ preview: [ok(BUSY)], hold: [{ late: true }, { late: true }, { late: true }] }, ["pause", "leave"]);
  assert.equal(paused.calls.filter((c) => c.hold).length, 3);
  assert.equal(paused.last.step, "error");
  assert.deepEqual(buttonsOf(paused.last), ["force", "leave"]);
});

test("someone who started after the dialog opened sends it back to the question with the fresh table", async () => {
  const more = { ...BUSY, live: [...BUSY.live, { team: "proj", member: "proj-qa", role: "qa" }] };
  const { last } = await flow({ preview: [ok(BUSY)], close: [{ status: 409, reply: { refused: true, reason: "live-changed", preview: more } }] }, ["force", "cancel"]);
  assert.equal(last.step, "confirm");
  assert.equal(last.changed, true);
  assert.equal(last.plan.live.length, 3);
});

test("a close ahd took and did not answer in time is watched, not sent again", async () => {
  const { last, calls } = await flow({ preview: [ok(BUSY), ok(BUSY), ok({ ...EMPTY, closed_at: "2026-11-14T20:00:00.000Z" })], close: [{ late: true }] }, ["force"]);
  assert.equal(last.step, "done");
  assert.equal(calls.filter((c) => c.confirm_live).length, 1);
});

test("the call goes to the bridge first, and through ah roster close in the team's work root when nothing reached ahd", async () => {
  const posted = [];
  const viaBridge = await teamClose(() => assert.fail("no exec"), async (body, ms) => (posted.push([body, ms]), ok(EMPTY)), "abcd1234/proj", { preview: true }, { ms: 15000 });
  assert.equal(viaBridge.status, 200);
  assert.deepEqual(posted, [[{ dir: "abcd1234", team: "proj", preview: true }, 15000]]);

  const ran = [];
  const exec = async (cmd, args, opts) => (ran.push([cmd, args, opts.timeout]), { code: 3, stdout: "", stderr: JSON.stringify({ refused: true, reason: "live-changed" }) });
  const viaAh = await teamClose(exec, async () => null, "abcd1234/proj", { confirm_live: ["a", "b"] }, { workRoot: "/work/proj" });
  assert.deepEqual(ran, [["ah", ["roster", "close", "proj", "--confirm-live", "a", "--confirm-live", "b", "--cwd", "/work/proj"], 100000]]);
  assert.equal(viaAh.status, 409);
  assert.equal(viaAh.reply.reason, "live-changed");
  // a post that threw may have reached ahd: late, and ah is not tried
  assert.deepEqual(await teamClose(() => assert.fail("no exec"), async () => { throw new Error("ahd did not answer"); }, "abcd1234/proj", { hold: true }), { late: true });
});

test("a lane's sitrep carries lane: true, so its header draws no Dismiss; a top-level team's does not", async () => {
  const { sitrepModel } = await import("../../herdr-desktop/mods/ah2/model.js");
  const slices = (lane) => new Map([
    ["team-summary:d1/t", { id: "team-summary:d1/t", body: { name: "t", running: true, ...(lane ? { lane: { parent_team: "top" } } : {}) } }],
    ["sitrep:d1/t", { id: "sitrep:d1/t", body: { regions: [] } }],
  ]);
  assert.equal(sitrepModel(slices(true), "d1/t", 0).lane, true);
  assert.equal(sitrepModel(slices(false), "d1/t", 0).lane, false);
});

test("an error that arrives as an object is shown in words, never as [object Object]", () => {
  const s = next(START, { type: "preview", got: { status: 409, reply: { refused: true, detail: { code: "unknown-dir", message: "no such project" } } } });
  assert.equal(s.error, "Could not read the team: no such project");
});
