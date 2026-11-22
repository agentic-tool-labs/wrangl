// The Attention and Q&A views' data: the `questions:<dirId>/<team>` slice folded to the rows the Attention tab draws, and the
// `list:questions:<dir>/<team>` rows filtered by facet and search.
// Both cover the team's open pipeline run only. Pure; no DOM.

// `team:<dir>/<team>` -> the slice and list ids of that team's questions.
export const questionsId = (teamId) => `questions:${teamId.slice("team:".length)}`;
export const qaListId = (teamId) => `list:questions:${teamId.slice("team:".length)}`;

export const ATTENTION_EMPTY = "Nothing needs you";
export const INBOX_EMPTY = "Nothing needs you. Decisions, blocked members and stuck tasks appear here.";
export const NO_RUN = "The team has no open run";
export const QA_EMPTY = "No questions in this run. Questions appear here when a pipeline run parks one for you.";
export const QA_NO_MATCH = "No question matches.";

// All is everything with questions badged (the question log's rows too); Q&A is the pipeline run's own rows.
export const FACETS = [["all", "All"], ["questions", "Questions"], ["terminal", "Terminal"], ["qa", "Q&A"]];

// The letter `ah msg decision answer --choice` takes for an option: a to d in option order (the verb takes no other letter).
export const letterOf = (i) => (i < 4 ? String.fromCharCode(97 + i) : "");

// Whether two texts name the same option when either may have been cut short with "…" (labels and the default are capped
// at different lengths).
export const sameText = (a, b) => {
  if (!a || !b) {
    return false;
  }
  const [x, y] = [a, b].map((s) => s.replace(/…$/, ""));
  return a.endsWith("…") || b.endsWith("…") ? x.startsWith(y) || y.startsWith(x) : a === b;
};

// The index of the option the default names, or -1 when none does or when more than one cut label could be it.
export const defaultAt = (q) => {
  const hits = q.options.flatMap((o, i) => (sameText(o.label, q.def) ? [i] : []));
  return hits.length === 1 ? hits[0] : -1;
};

// The existing answer verb for a team and run: one --choice per entry picked (letters in option order, Other last, quoted for the
// shell), or with nothing picked yet the default option's letter. A default that is already a letter is kept; with no default,
// or one naming no option shown, the choice is left as a placeholder rather than guessed.
export function answerCommand(q, team, run = null, pick = NO_PICK) {
  const at = defaultAt(q);
  const fallback = /^[a-z]$/.test(q.def ?? "") ? q.def : at >= 0 ? letterOf(at) : "<letter>";
  const picked = entriesOf(q, pick).map((e) => (e.startsWith("other: ") ? `'${e.replace(/'/g, "'\\''")}'` : e));
  const choices = (picked.length > 0 ? picked : [fallback]).map((c) => `--choice ${c}`).join(" ");
  return `ah msg decision answer ${q.id} ${choices} --team ${team}${run ? ` --run ${run}` : ""}`;
}

// What the user has picked on an open card: option indexes, and whether Other is on with its text. A single-choice card holds at
// most one of the two; a multi card any set.
export const NO_PICK = Object.freeze({ picked: [], other: false, text: "" });
export const OTHER_MAX = 2000;

// The pick after the user (un)checks option `i`, or Other when `i` is "other".
export function toggle(q, pick, i) {
  if (!q.multi) {
    return i === "other" ? { ...pick, picked: [], other: true } : { ...pick, picked: [i], other: false };
  }
  return i === "other" ? { ...pick, other: !pick.other } : { ...pick, picked: pick.picked.includes(i) ? pick.picked.filter((x) => x !== i) : [...pick.picked, i] };
}

// Whether a checked Other's trimmed text is over OTHER_MAX UTF-8 bytes (the cap is bytes, so the route body stays under the
// daemon's 4096-byte limit whatever the script; the input's maxlength counts UTF-16 units and is only a soft guard).
// The encoder is made on first use, not at load: the Herdr mod runtime imports this file and has no TextEncoder.
export const otherOver = (pick) => pick.other && new TextEncoder().encode(pick.text.trim()).length > OTHER_MAX;

// The answer's entries as the verb and the route take them: letters in option order, then `other: <trimmed text>` last. A checked
// Other with no text (or over OTHER_MAX bytes) gives no Other entry.
function entriesOf(q, pick) {
  const letters = [...pick.picked].sort((a, b) => a - b).map(letterOf).filter(Boolean);
  const text = pick.other && !otherOver(pick) ? pick.text.trim() : "";
  return [...letters, ...(text ? [`other: ${text}`] : [])];
}

// The POST /ui/v1/answer body for a pick, or null when it cannot be sent yet: nothing picked, or Other on without text.
// `teamId` is `team:<dir>/<team>`; a multi question sends `choices`, a single one `choice`.
export function answerBody(teamId, run, q, pick) {
  const entries = entriesOf(q, pick);
  const otherBlank = pick.other && entries.every((e) => !e.startsWith("other: "));
  const isItem = q.kind === "item";
  if (entries.length === 0 || otherBlank) {
    return null;
  }
  const [dir, team] = teamId.slice("team:".length).split(/\/(?=[^/]*$)/);
  if (isItem) {
    return { dir, team, item: q.id, since: q.since, choice: entries[0] };
  }
  return { dir, team, ...(run ? { run } : {}), id: q.id, ...(q.multi ? { choices: entries } : { choice: entries[0] }) };
}

// The two ways to dismiss a waiting row (spec ah-ui-answer §6): one click each, no confirm; the label is the reason sent.
export const DISMISS = ["Already done", "No longer needed"];

// The POST /ui/v1/answer body that dismisses a row with `reason` in place of a choice; a decision with no open run carries no `run`.
export function dismissBody(teamId, run, q, reason) {
  const [dir, team] = teamId.slice("team:".length).split(/\/(?=[^/]*$)/);
  if (q.kind === "item") {
    return { dir, team, item: q.id, since: q.since, dismiss: reason };
  }
  return { dir, team, ...(run ? { run } : {}), id: q.id, dismiss: reason };
}

// The pick a card starts with: its default option, or for an item with no options Other, the only way to answer it.
export const startPick = (q) => (q.kind === "item" && q.options.length === 0 ? { ...NO_PICK, other: true } : defaultAt(q) >= 0 ? toggle(q, NO_PICK, defaultAt(q)) : NO_PICK);

// The answer a pick gives, in words, for the confirm step and the collapsed status: the picked labels in option order, then
// Other's trimmed text, joined by "; ".
export function answerText(q, pick) {
  const labels = [...pick.picked].sort((a, b) => a - b).map((i) => q.options[i]?.label).filter(Boolean);
  const text = pick.other ? pick.text.trim() : "";
  return [...labels, ...(text ? [text] : [])].join("; ");
}

// The route's reply -> what the card says. 200: recorded, with the answer the core wrote (the card stays collapsed on it until the
// slice moves the row); 409: the core's own detail; anything else: could not send. `done` keeps the card collapsed and locked.
// No served signal says the Orchestrator has read the answer, so the ladder ends at Recorded.
export function replyState(status, body) {
  if (status === 200 && body?.ok) {
    return { kind: "recorded", text: "Recorded", answer: body.decision ?? null, note: body.event_error ? "The Orchestrator was not told." : null, event: body.event_id ?? null, done: true };
  }
  if (status === 409) {
    return { kind: "refused", text: body?.detail || body?.reason || "Refused", done: false };
  }
  return { kind: "error", text: `Could not send: ${body?.detail || body?.error || (status ? `status ${status}` : "no answer")}`, done: false };
}

// After Recorded, a card with an event id polls /ui/v1/answer-status this often while it is mounted and its tab visible, and gives up
// after `cap`, staying at Recorded (answer-ack §3). The Attention list keeps such a card, collapsed, for `hold` after Recorded even once
// the slice has dropped its row, so Seen can show; a stop rule lets it go at once. Tests shorten all three.
export const SEEN_POLL = { every: 5000, cap: 15 * 60_000, hold: 60_000 };
export const SEEN = "Seen by the Orchestrator";
// Not seen, and never will be: a declined, withdrawn or superseded entry, or an ack that superseded or resolved the event.
const VOID = new Set(["declined", "withdrawn", "superseded", "acked"]);

// A status reply -> seen (with its time when served), wait (poll again: not seen yet, a 5xx, or no reply, status 0) or stop (a 404 or
// other refusal, or a terminal not-seen state).
export function seenOf(status, body) {
  if (status === 0 || status >= 500) {
    return { kind: "wait" };
  }
  if (status !== 200 || !body) {
    return { kind: "stop" };
  }
  if (body.seen === true) {
    return { kind: "seen", at: body.seen_at ?? null };
  }
  return VOID.has(body.state) ? { kind: "stop" } : { kind: "wait" };
}

// How urgent a waiting row is, most urgent first: a member cannot move until it is answered, it becomes a blocker if it waits, or it
// can wait. A row with no class, or one not listed, is `soon`.
export const CLASSES = [["blocker", "Blocker"], ["soon", "Soon"], ["eventually", "Eventually"]];
export const classOf = (c) => (CLASSES.some(([k]) => k === c) ? c : "soon");
export const classLabel = (c) => CLASSES.find(([k]) => k === classOf(c))[1];
const classRank = (r) => CLASSES.findIndex(([k]) => k === classOf(r.cls));

// The one place the daemon's field names are read: each raw row -> the fields the views draw. A change to the §2 shapes is an
// edit here and nowhere else.
// `at` is absent when the daemon could not parse the time; such a row sorts last.
export const openRow = (q) => ({
  id: q.id, at: q.at ?? null, asker: q.asker ?? null, item: q.header ?? null, text: q.text ?? "",
  // `rec`: the daemon marked the option as the agent's recommendation (it strips the mark from the label)
  options: (q.options ?? []).map((o, i) => ({ label: typeof o === "string" ? o : o.label, rec: (q.recommended ?? []).includes(i) })), def: q.default ?? null, multi: q.multi === true,
  // capped rows: the options not sent, and whether the text was cut; both are whole in the Q&A list
  more: Math.max(0, (q.options_total ?? 0) - (q.options ?? []).length), cut: (q.text ?? "").endsWith("…"), cls: classOf(q.class),
});
// A checklist item waiting on the user, drawn with the same form: its key answers it, and `since` proves the question is still the one read.
// Its asker is who set it waiting on the user, not its owner; `workId` names the registered item, whose description the card reads.
export const itemRow = (i) => ({
  kind: "item", id: i.key, since: i.since ?? null, at: Date.parse(i.since ?? "") || null, asker: i.asker ?? null, item: i.title ?? null, text: i.text ?? i.title ?? "",
  options: (i.options ?? []).map((label, n) => ({ label, rec: (i.recommended ?? []).includes(n) })), def: null, multi: false, more: 0, cut: false, workId: i.work_id ?? null, cls: classOf(i.class),
});
// A question-log row (dir jim or terminal) also carries the question badge and how many replies its thread has.
export const recentRow = (r) => ({ id: r.id, at: r.at ?? null, text: r.text ?? "", answer: r.answer ?? "", by: r.by ?? null, dir: r.dir ?? null, question: r.question === true, replies: r.replies ?? 0 });
export const listRow = (raw) => {
  const r = raw.body ?? raw;
  return {
    id: r.id ?? raw.id, dir: r.dir, at: r.at ?? null, item: r.header ?? null, text: r.text ?? "", options: r.options ?? [], recommended: r.recommended ?? [],
    answer: r.answer ?? null, by: r.by ?? null, answeredAt: r.answered_at ?? null, run: r.run ?? null, cls: r.class ?? null, rationale: r.rationale ?? null,
    question: r.question === true, reply: r.reply ?? null, replyAt: r.reply_at ?? null, later: (r.later ?? []).map((l) => ({ at: l.at ?? null, text: l.text ?? "" })),
    cut: r.cut === true,
  };
};

// A question-log thread whole, read on demand: the list carries reply heads only (`cut` marks a row with a longer one).
export const threadListId = (teamId, id) => `list:qa-thread:${teamId.slice("team:".length).split("/")[0]}/${id}`;

// A whole thread ({prompt, replies, ask}, from list:qa-thread or `ah msg history --json --id`) -> what its panel shows (spec
// qa-history-replies §4.7, §8). `reply` is the direct row, else the own turn row's final (`replyFinal`), else its text: an old row
// (no part, no final) gives its text and nothing else. `final` is the own turn's final when it differs from the direct text; `more`
// the own turn's whole text when it is more than the reply and final shown; `later` every other turn, oldest first, with its final.
export function threadOf(doc) {
  const replies = doc?.replies ?? [];
  const direct = doc?.prompt ? replies.find((r) => r.part === "direct") ?? null : null;
  const turn = doc?.prompt ? replies.find((r) => r.part !== "direct" && r.trigger === "typed") ?? null : null;
  const same = (a, b) => (a ?? "").trim() === (b ?? "").trim();
  const reply = direct?.text ?? turn?.final ?? turn?.text ?? null;
  const final = direct && turn?.final && !same(turn.final, direct.text) ? turn.final : null;
  const more = turn?.text && !same(turn.text, reply) && !same(turn.text, final) ? turn.text : null;
  return {
    reply, replyAt: (direct ?? turn)?.at ?? null, replyFinal: !direct && Boolean(turn?.final),
    final, finalAt: final ? turn.at ?? null : null, more, moreAt: more ? turn.at ?? null : null,
    later: replies.filter((r) => r !== direct && r !== turn).map((r) => ({ at: r.at ?? null, text: r.text ?? "", final: r.final ?? null })),
  };
}

const oldest = (a, b) => (a.at ?? Infinity) - (b.at ?? Infinity);
// blocker, then soon, then eventually; oldest first within a class
const byClass = (a, b) => classRank(a) - classRank(b) || oldest(a, b);
const newest = (a, b) => (b.at ?? -Infinity) - (a.at ?? -Infinity);

// The slice body -> what the Attention tab draws. Open rows by class (blocker, soon, eventually), oldest first within one, and recent newest first whatever order they arrived in;
// an absent body (not held yet) is `null`; with no `run` the team has no open run; an empty one has `empty` set.
// Every open question of the run, not only the capped rows sent (the header's badge reads it too).
export const openTotal = (body) => body?.open_total ?? body?.open?.length ?? null;

export function attentionOf(body) {
  if (!body) {
    return null;
  }
  const decisions = (body.open ?? []).map(openRow).sort(byClass);
  const open = [...decisions, ...(body.items ?? []).map(itemRow)].sort(byClass);
  const recent = (body.recent ?? []).map(recentRow).sort(newest);
  // open[] and items[] are capped; waiting_total is the fold's count of everything waiting, the one number the Inbox and the Board show.
  const count = body.waiting_total ?? openTotal(body);
  return { run: body.run ?? null, open, decisions, decisionCount: openTotal(body), recent, count, hidden: Math.max(0, count - open.length), truncated: body.truncated === true, empty: open.length === 0 };
}

const haystack = (r) => [r.id, r.item, r.text, ...(r.options ?? []), r.answer, r.reply, ...(r.later ?? []).map((l) => l.text)].filter(Boolean).join("\n").toLowerCase();

// Which rows a facet keeps: questions are the typed prompts that look like questions, Q&A the run's rows; replies with no typed
// prompt (dir unprompted) are in no facet. Any other facet is a dir.
const inFacet = (r, facet) =>
  facet === "all" ? r.dir !== "unprompted" : facet === "questions" ? r.dir === "jim" && r.question : facet === "qa" ? r.dir === "to_you" || r.dir === "decided" : r.dir === facet;

// The list rows (already through listRow) a facet and a search leave, in the order the list gave them (newest first).
export function qaRows(rows, facet = "all", query = "") {
  const q = query.trim().toLowerCase();
  return rows.filter((r) => inFacet(r, facet) && (q === "" || haystack(r).includes(q)));
}

// A typed reply longer than this many lines starts collapsed behind "show all".
export const REPLY_LINES = 12;

// What the Q&A table says when it has no row to draw: nothing loaded yet, nothing at all, or nothing left by the filter.
export function qaEmpty(loaded, all, shown) {
  return !loaded ? "Loading…" : all === 0 ? QA_EMPTY : shown === 0 ? QA_NO_MATCH : null;
}

export const DIRECTION = { to_you: "To you", decided: "Decided", jim: "asked by you", terminal: "terminal", unprompted: "Orchestrator, unprompted" };
