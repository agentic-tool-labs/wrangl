// Dismiss a team from Herdr (ah2-45): what the dialog says, its steps, and the one call it makes. ahd answers the preview and
// re-reads who is running on every close, so this file only words what it was told and sends back the names it showed. No Herdr
// code: exec, the bridge post and sleep are passed in (tests/web/herdr-dismiss-model.test.mjs).
//
// The call is POST /ui/v1/team-close on ahd's bridge (link-model.js Link teamClose), else `ah roster close`, the user's own
// verb: {preview} reads the team, {hold} pauses it (ah roster quit, run by ahd, one short call at a time), {confirm_live} closes it.

import { within, late } from "./model.js";

// "<dirId>/<team>", the id every team row carries.
export const closeBody = (id, more = {}) => {
  const at = id.indexOf("/");
  return { dir: id.slice(0, at), team: id.slice(at + 1), ...more };
};

// herdr's fetch gives a request 30 s (link-model.js): a hold call answers inside 20 s by design, a close that ends sessions may not.
export const CALL_MS = { preview: 15000, hold: 25000, close: 25000 };
// A close that outlived its request: ahd goes on closing, and this many previews, 5 s apart, wait for its marker.
export const LATE_TRIES = 18;
// A pause still winding down is asked again this often; quit's own window (7 minutes) ends it.
export const HOLD_EVERY_MS = 2000;
// A hold call that was not answered in time is sent again (a quit loses nothing to a rerun); this many in a row is an error.
export const HOLD_LATES = 3;

// What the dialog offers, from ahd's preview. `busy` is a team with a running session: only that one asks before it goes.
export function dismissPlan(preview) {
  const work = (m) => [m.status, m.detail].filter(Boolean).join(": ");
  const live = (preview.live ?? []).map((m) => ({ name: m.member, team: m.team ?? null, role: m.role ?? "", work: work(m) }));
  const notes = [
    ...(preview.unknown ?? []).map((m) => `${m.member} may still be running`),
    ...(preview.held ?? []).map((m) => `${m.member}: ${work(m)}`),
    ...(preview.warnings ?? []),
  ];
  const left = preview.left_running ?? [];
  return {
    live,
    notes,
    leftRunning: left,
    busy: live.length > 0,
    confirmLive: live.map((m) => m.name),
    // pause then quit, or force quit, only where something runs; a team nobody runs is still confirmed first (Jim: always confirm)
    buttons: live.length > 0 ? ["pause", "force", "cancel"] : ["dismiss", "cancel"],
  };
}

// ahd's error can be an envelope ({code, message}), not a string: show its words, never "[object Object]"
const words = (v) => (v && typeof v === "object" ? v.message || v.detail || v.reason || v.code || JSON.stringify(v) : v);
const detailOf = (got) => words(got?.reply?.detail) || words(got?.reply?.reason) || words(got?.reply?.error) || (got?.status ? `status ${got.status}` : "no answer");

// The verb's exit and stdout as the bridge would have answered: 0 is the 200 body, 3 the 409 refusal, anything else a failure.
function execReply(r) {
  let reply = null;
  try {
    reply = JSON.parse(r.code === 0 ? r.stdout : r.stderr || r.stdout);
  } catch {
    reply = null;
  }
  return { status: r.code === 0 ? 200 : r.code === 3 ? 409 : 500, reply: reply ?? { detail: (r.stderr ?? "").trim().split("\n")[0] || `ah exited ${r.code}` } };
}

// One call, bounded. `post` (the bridge) first; null from it means nothing reached ahd, so the same body goes through ah, in the
// team's work root. Resolves to {status, reply}; {late: true} when the call was taken and not answered in time (never sent again).
export async function teamClose(exec, post, id, more, { workRoot = null, sleep = null, ms = CALL_MS.close } = {}) {
  const body = closeBody(id, more);
  if (post) {
    let got;
    try {
      got = await post(body, ms);
    } catch {
      return { late: true };
    }
    if (got) return got;
  }
  const args = ["roster", "close", body.team,
    ...(more.preview ? ["--preview"] : []), ...(more.hold ? ["--hold"] : []),
    ...(more.confirm_live ?? []).flatMap((n) => ["--confirm-live", n]), ...(workRoot ? ["--cwd", workRoot] : [])];
  try {
    // ah's own wait on ahd is 90 s; a close that ends sessions can take that long, and exec has no 30 s cap
    return execReply(await within(exec("ah", args, { timeout: more.confirm_live ? 100000 : ms }), sleep, more.confirm_live ? 100000 : ms));
  } catch (e) {
    return late(e) ? { late: true } : { status: 0, reply: { detail: words(e?.down) ?? e?.message ?? String(e) } };
  }
}

// ---- the dialog's steps: a reducer, so every state it can show is one a test can name ----
//
// reading -> (nobody runs) closing -> done
//         -> confirm -> pausing -> closing -> done        "Pause, then quit"
//                    -> closing -> done                   "Force quit"
// `error` keeps the plan it had, so the buttons stay; `late` is a close ahd took and has not answered: it is watched, not resent.

export const START = { step: "reading", plan: null, error: null, waitingOn: [], changed: false, note: "", lates: 0 };

export function next(state, ev) {
  switch (ev.type) {
    case "preview": {
      if (ev.got?.status !== 200) return { ...state, step: "error", error: `Could not read the team: ${detailOf(ev.got)}` };
      const plan = dismissPlan(ev.got.reply);
      if (ev.got.reply.closed_at && state.step === "late") return { ...state, step: "done", plan };
      if (state.step === "late") return state; // still closing: keep waiting
      return { ...state, plan, error: null, step: "confirm" };
    }
    case "pause":
      return { ...state, step: "pausing", error: null, waitingOn: state.plan?.confirmLive ?? [] };
    case "hold": {
      if (ev.got?.late) {
        return state.lates + 1 >= HOLD_LATES
          ? { ...state, step: "error", error: "ahd did not answer the pause. The team may be paused; force quit, or leave it." }
          : { ...state, step: "pausing", lates: state.lates + 1 };
      }
      if (ev.got?.status !== 200) return { ...state, step: "error", error: `Could not pause: ${detailOf(ev.got)}` };
      const r = ev.got.reply;
      if (!r.sealed) return { ...state, step: "pausing", lates: 0, waitingOn: r.waiting_on ?? [] };
      return { ...state, step: "closing", waitingOn: [], note: r.unanswered ? "Paused; some members did not answer in time." : "Paused: work saved, the team can be resumed." };
    }
    case "force":
      return { ...state, step: "closing", error: null };
    case "closed": {
      if (ev.got?.late) return { ...state, step: "late", error: null };
      if (ev.got?.status === 200) {
        const r = ev.got.reply;
        const notes = [...(r.still_live ?? []).map((n) => `${n} did not stop`), ...(r.left_running ?? []).map((n) => `${n} is still running`), ...(r.warnings ?? [])];
        return { ...state, step: "done", error: null, note: [state.note, ...notes].filter(Boolean).join(" · ") };
      }
      // someone started after the dialog opened: ahd wrote nothing and sent the table to read again
      if (ev.got?.status === 409 && ev.got.reply?.preview) return { ...state, step: "confirm", changed: true, plan: dismissPlan(ev.got.reply.preview), error: null };
      return { ...state, step: "error", error: `Could not dismiss: ${detailOf(ev.got)}` };
    }
    case "gave-up":
      return { ...state, step: "error", error: "ahd is still closing the sessions. The team leaves the list when it is done." };
    default:
      return state;
  }
}

// The buttons a state shows. An error keeps what the plan offered, so a failed pause can still be forced or left.
export function buttonsOf(state) {
  if (state.step === "confirm") return state.plan.buttons;
  if (state.step === "pausing") return ["force", "leave"];
  if (state.step === "error") return state.plan?.busy ? ["force", "leave"] : state.plan ? ["dismiss", "cancel"] : ["cancel"];
  if (state.step === "done") return ["ok"];
  return []; // reading, closing, late: nothing to press but the header's ×
}

// The whole flow as one bounded task: `emit(state)` on every step. `actions` is what the buttons resolve: wait() returns the next
// press ("pause" | "force" | "leave" | "dismiss" | "cancel"), or null when the dialog closed. Returns the last state.
export async function runDismiss({ call, sleep, emit, wait }) {
  let s = START;
  const set = (ev) => emit((s = next(s, ev)));
  emit(s);
  set({ type: "preview", got: await call({ preview: true }, CALL_MS.preview) });
  for (;;) {
    if (s.step === "confirm" || s.step === "error") {
      const press = await wait(s);
      if (press === "pause") set({ type: "pause" });
      else if (press === "force" || press === "dismiss") set({ type: "force" });
      else return s; // cancel, leave, or the dialog closed
    } else if (s.step === "pausing") {
      // one waiter for the whole pause, so a press between two hold calls is not lost; a press wins over the call in flight
      const pressed = wait(s).then((press) => ({ press }));
      let won = null;
      while (s.step === "pausing") {
        won = await Promise.race([pressed, call({ hold: true }, CALL_MS.hold).then((got) => ({ got }))]);
        if (!won.got) break;
        set({ type: "hold", got: won.got });
        if (s.step !== "pausing") break;
        won = await Promise.race([pressed, sleep(HOLD_EVERY_MS).then(() => ({ slept: true }))]);
        if (!won.slept) break;
      }
      if (won && "press" in won) {
        if (won.press === "force") set({ type: "force" });
        else return s; // leave, or the dialog closed: the team stays paused
      }
    } else if (s.step === "closing") {
      set({ type: "closed", got: await call({ confirm_live: s.plan.confirmLive }, CALL_MS.close) });
    } else if (s.step === "late") {
      for (let i = 0; i < LATE_TRIES && s.step === "late"; i++) {
        await sleep(5000);
        set({ type: "preview", got: await call({ preview: true }, CALL_MS.preview) });
        if (s.step === "error") return s;
      }
      if (s.step === "late") set({ type: "gave-up" });
    } else return s; // done
  }
}
