// The questions of a team's open pipeline run, in the shapes of the `questions:<dirId>/<team>` slice and the
// `list:questions:<dir>/<team>` list. Deterministic for a given `now`.

const MIN = 60_000;
export const RUN = "20261030-233614-ui-w2";

// id, minutes ago, asker, plan item, question, options, default, multi, recommended (served: the marked option indexes, labels already stripped)
const OPEN = [
  ["d7", 95, "sub-orchestrator", "alarms-18", "Item 18(1) says reports wake the device. Should a report raised while the screen is off also wake it, or wait for the next unlock?",
    ["Reports still wake", "Wait for unlock", "Only urgent ones wake"], "Reports still wake"],
  ["d9", 41, "architect", "backlog-waves", "The backlog uses now / next / later. Keep those three names in the new tab, or add a fourth \"someday\" wave for parked ideas?",
    ["Keep three", "Add someday"], "Keep three", false, [1]],
  ["d12", 12, null, null, "Which suites should the merge train run before it lands a branch on main?", ["Core only", "Core and web", "Core, web and E4", "Core, web, E4 and the shell suites", "Everything QA runs", "Nothing: trust the reviewer"], "Core, web, E4 and the shell suites"],
  ["d14", 6, "orchestrator", "nightly", "Which suites should the nightly run cover? Pick every one that applies.", ["Core", "Web", "E4", "Shell suites"], null, true],
];

// id, minutes since asked, minutes since answered, plan item, question, options, answer, by, surrogate class
const ANSWERED = [
  ["d5", 150, 130, "groom-chip", "Should the groom chip show on every team or only the backlog's own dir?", ["Every team", "Only its own dir"], "Only its own dir", "user", null],
  ["d3", 300, 260, null, "Keep the \"Plans\" label or rename the tab to \"Board\"?", ["Keep Plans", "Rename to Board"], "Keep Plans", "user", null],
  ["d2", 430, 410, "e4-retry", "Retry a failed E4 run once before failing the gate?", ["Retry once", "Fail at once"], "Retry once", "surrogate", "D4"],
];

// id, minutes ago, plan item, question, options, decision, decider, rationale
const DECIDED = [
  ["d4", 300, "qa-scroll", "Let the Q&A table scroll inside its pane rather than the page?", [], "Pane scrolls", "orchestrator", "Every other view scrolls inside its pane; the frame never scrolls."],
  ["d1", 500, null, "Show card age from state_since or from the item's creation?", ["state_since", "created"], "state_since", "architect", "The board asks how long an item has sat in its column."],
];

const item = (key) => (key ? { header: key } : null);
// The slice's caps as D1 rules them, in UTF-8 bytes with the cut ending in "…": open ≤ 10 (oldest first) with text 200 and at most
// 4 option labels of 24 (options_total when there are more), header/asker/source/default 40; recent ≤ 10 with text 100, answer 48.
const bytes = (s) => Buffer.byteLength(s, "utf8");
export function cut(s, max) {
  if (bytes(s) <= max) {
    return s;
  }
  let out = [...s];
  while (bytes(`${out.join("")}…`) > max) {
    out.pop();
  }
  return `${out.join("")}…`;
}
const capped = (o, key, max) => (o[key] ? { [key]: cut(o[key], max) } : null);

// `asked` keeps the first that many open questions (a scenario with fewer decisions waiting); the default is all of them.
// `items` are the checklist items waiting on the user (ah-ui-answer §2), drawn with the same form; waiting_total counts both.
export function questionsBody(now, asked = OPEN.length, items = []) {
  const open = OPEN.slice(0, asked);
  return {
    ...(items.length ? { items, waiting_total: open.length + items.length } : null),
    run: RUN,
    open: [...open].sort((a, b) => b[1] - a[1]).slice(0, 10).map(([id, ago, asker, key, text, options, def, multi, recommended]) => ({
      id, at: now - ago * MIN, ...capped({ asker }, "asker", 40), source: "ah msg decision add", ...capped(item(key) ?? {}, "header", 40), text: cut(text, 200),
      options: options.slice(0, 4).map((label) => ({ label: cut(label, 24) })), ...(options.length > 4 ? { options_total: options.length } : null), ...capped({ default: def }, "default", 40),
      ...(multi ? { multi: true } : null), ...(recommended ? { recommended } : null),
    })),
    recent: ANSWERED.slice(0, 10).map(([id, , ago, , text, , answer, by]) => ({ id, text: cut(text, 100), answer: cut(answer, 48), by, at: now - ago * MIN })),
    open_total: open.length,
    truncated: open.length > 10 || ANSWERED.length > 10,
  };
}

// Newest first, over the open run: every question put to the user (open or answered, a surrogate's answer included) and
// every decision the team took on its own.
export function questionRows(now, open = OPEN.length) {
  const asked = OPEN.slice(0, open).map(([id, ago, , key, text, options, , , recommended]) => ({ id, dir: "to_you", at: now - ago * MIN, ...item(key), text, options, ...(recommended ? { recommended } : null), run: RUN }));
  const answered = ANSWERED.map(([id, asked, ago, key, text, options, answer, by, cls]) => ({
    id, dir: "to_you", at: now - asked * MIN, ...item(key), text, options, answer, by, answered_at: now - ago * MIN, run: RUN, ...(cls ? { class: cls } : null),
  }));
  const decided = DECIDED.map(([id, ago, key, text, options, answer, by, rationale]) => ({ id, dir: "decided", at: now - ago * MIN, ...item(key), text, options, answer, by, run: RUN, rationale }));
  return [...asked, ...answered, ...decided].sort((a, b) => b.at - a.at);
}

// One window of the list by cursor, newest first: `after` is the id of the last row the caller holds.
export function questionsWindow(rows, { after = null, cap }) {
  const from = after === null ? 0 : rows.findIndex((r) => r.id === after) + 1;
  const page = rows.slice(from, from + cap);
  return { rows: page, next: from + cap < rows.length ? page[page.length - 1].id : null };
}

// POST /ui/v1/answer as D2 answers it (§9.4), over the fixture: [status, body]. d9 is the one 409 fixture, answered elsewhere
// first; every other open question takes a valid answer. Nothing is written: the slice does not move.
export const CLOSED = { d9: "Already answered (d10): Keep three" };
export function answerReply(b) {
  const bad = (detail) => [400, { error: "bad-request", detail }];
  const refused = (reason, detail) => [409, { refused: true, reason, detail }];
  if (!b || typeof b !== "object" || Array.isArray(b)) {
    return bad("body is not a JSON object");
  }
  const dismiss = "dismiss" in b;
  if (dismiss && (typeof b.dismiss !== "string" || !b.dismiss.trim() || Buffer.byteLength(b.dismiss, "utf8") > 64 || "choice" in b || "choices" in b)) {
    return bad("dismiss must be a reason of 1 to 64 bytes, in place of choice");
  }
  // an item answer or dismissal (ah-ui-answer §4, §6): nothing is written, so every waiting item takes it
  if ("item" in b) {
    const gone = ["dir", "team", "item", "since"].find((k) => typeof b[k] !== "string" || b[k] === "");
    if (gone || ["run", "id", "choices"].some((k) => k in b) || (!dismiss && typeof b.choice !== "string")) {
      return bad(gone ? `missing ${gone}` : "not an item answer");
    }
    return [200, { ok: true, id: b.item, answers: b.item, decision: dismiss ? `Dismissed: ${b.dismiss}` : b.choice.replace(/^other: /, "") }];
  }
  const missing = ["dir", "team", "run", "id"].find((k) => typeof b[k] !== "string" || b[k] === "");
  if (missing) {
    return bad(`missing ${missing}`);
  }
  if (dismiss) {
    return b.run !== RUN ? refused("stale-run", `open run is ${RUN}`) : CLOSED[b.id] ? refused("already-closed", CLOSED[b.id]) : [200, { ok: true, id: "d20", answers: b.id, decision: `Dismissed: ${b.dismiss}` }];
  }
  if (("choice" in b) === ("choices" in b)) {
    return bad("exactly one of choice or choices");
  }
  const entries = "choice" in b ? [b.choice] : b.choices;
  if (!Array.isArray(entries) || entries.length === 0 || !entries.every((e) => typeof e === "string")) {
    return bad("choices must be one or more strings");
  }
  if (b.run !== RUN) {
    return refused("stale-run", `open run is ${RUN}`);
  }
  if (CLOSED[b.id]) {
    return refused("already-closed", CLOSED[b.id]);
  }
  const q = OPEN.find((r) => r[0] === b.id);
  if (!q) {
    return refused("unknown-id", `${b.id} is not an open parked question`);
  }
  if ("choices" in b && !q[7]) {
    return refused("not-multi", `${b.id} takes one choice`);
  }
  if (new Set(entries).size !== entries.length) {
    return refused("duplicate-choice", "a choice is given twice");
  }
  if (entries.filter((e) => e.startsWith("other:")).length > 1) {
    return refused("invalid-choice", "at most one Other");
  }
  const texts = entries.map((e) => (/^[a-d]$/.test(e) ? q[5][e.charCodeAt(0) - 97] : /^other: \S/.test(e) ? e.slice(7).trim() : undefined));
  const wrong = entries.find((_, i) => texts[i] === undefined);
  if (wrong !== undefined) {
    return refused("invalid-choice", `${JSON.stringify(wrong)} is not a choice of ${b.id}`);
  }
  return [200, { ok: true, id: "d20", answers: b.id, decision: texts.join("; ") }];
}
