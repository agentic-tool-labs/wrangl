// Rename a team from Herdr (ah2-218 B2): what the dialog shows while a name is typed, and the one call it makes. No Herdr code: exec,
// the bridge post and sleep are passed in (tests/web/herdr-team-rename.test.mjs). The display name is display only: the call names
// the team by its real name, and the sidebar shows the new name when the served summary carries it.
//
// The call is POST /ui/v1/team-rename on ahd's bridge (link-model.js Link teamRename), else `ah roster rename`, the user's own verb.

import { within, late } from "./model.js";
import { closeBody, execReply, detailOf } from "./dismiss-model.js";
import { displayNameProblem } from "./select.js";

export const CALL_MS = 15000;

// One call, bounded: `displayName` a text to set, or null to clear. `post` (the bridge) first; null from it means nothing reached ahd,
// so the same change goes through ah, in the team's work root. Resolves to {status, reply}.
export async function teamRename(exec, post, id, displayName, { workRoot = null, sleep = null, ms = CALL_MS } = {}) {
  const body = closeBody(id, { display_name: displayName });
  if (post) {
    let got;
    try {
      got = await post(body, ms);
    } catch {
      return { status: 0, reply: { detail: "ahd did not answer; the name may still change" } };
    }
    if (got) return got;
  }
  const args = ["roster", "rename", ...(displayName === null ? ["--clear"] : [displayName]), "--team", body.team, ...(workRoot ? ["--cwd", workRoot] : [])];
  try {
    return execReply(await within(exec("ah", args, { timeout: ms }), sleep, ms));
  } catch (e) {
    return { status: 0, reply: { detail: late(e) ? "ah did not answer in time; the name may still change" : e?.message ?? String(e) } };
  }
}

// What the dialog shows for the typed text: the rule as it is broken (an empty field only once typed in, or on a Save), whether Save
// may send, and whether Clear is offered (a display name is set).
export function renameForm(text, { touched = false, current = "" } = {}) {
  const problem = displayNameProblem(text);
  return { problem: problem && (touched || String(text ?? "").trim() !== "") ? problem : null, canSave: problem === null, clear: current !== "" };
}

// A call's answer: done closes the dialog; anything else keeps it open with the reason.
export const renameOutcome = (got) => (got?.status === 200 ? { done: true, error: null } : { done: false, error: `Could not rename: ${detailOf(got)}` });
