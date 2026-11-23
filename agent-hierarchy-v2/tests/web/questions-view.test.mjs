import { test } from "node:test";
import assert from "node:assert/strict";
import { mountApp as mount, until, find, text, DIR } from "./mount-app.mjs";
import { installDom, settle } from "./dom.mjs";
const { html, render } = await import("../../web/ui/h.js");
const { AttentionView, QAView } = await import("../../web/ui/questions-view.js");

const model = await import("../../web/ui/questions-model.js");
const { attentionOf, qaRows, qaEmpty, listRow, answerCommand, answerBody, replyState, toggle, NO_PICK, questionsId, qaListId, ATTENTION_EMPTY, NO_RUN, QA_EMPTY, QA_NO_MATCH } = model;
const { answerReply, RUN } = await import("../../web/mock/questions.mjs");
const { globalKey } = await import("../../web/ui/keys.js");
const { uiOf, hashOf, redirectOf } = await import("../../web/ui/deeplink.js");

const TEAM = `team:${DIR}/ship`;

test("the ids are the team's questions slice and list", () => {
  assert.equal(questionsId("team:d/t"), "questions:d/t");
  assert.equal(qaListId("team:d/t"), "list:questions:d/t");
});

test("the slice folds to open rows oldest first and recent newest first, whatever order they came in", () => {
  const a = attentionOf({
    run: "run-1",
    open: [{ id: "q2", at: 20, header: "item-a", text: "two", options: [{ label: "Yes" }], default: "Yes" }, { id: "q0", text: "no time" }, { id: "q1", at: 10, text: "one", options: [] }],
    recent: [{ id: "r1", at: 1, text: "old", answer: "A", by: "user" }, { id: "r2", at: 5, text: "new", answer: "B", by: "surrogate" }],
    open_count: 3,
  });
  assert.deepEqual(a.open.map((q) => q.id), ["q1", "q2", "q0"], "a row whose time did not parse sorts last");
  assert.deepEqual(a.recent.map((r) => r.id), ["r2", "r1"]);
  assert.equal(a.open[1].def, "Yes", "the default option is kept");
  assert.deepEqual(a.open[1].options, [{ label: "Yes", rec: false }]);
  assert.equal(a.open[1].item, "item-a", "header is the plan item key");
  assert.equal(a.open[0].asker, null, "asker is optional");
  assert.equal(a.run, "run-1");
  assert.equal(a.empty, false);
  assert.equal(a.count, 3);
});

test("open rows sort blocker, soon, eventually before age; a row with no class or an unknown one is soon", () => {
  const a = attentionOf({
    run: "run-1",
    open: [{ id: "q1", at: 10, text: "old soon", class: "soon" }, { id: "q2", at: 30, text: "new blocker", class: "blocker" }],
    items: [{ key: "s:late", since: "1970-01-01T00:00:00.005Z", title: "eventually", class: "eventually" }, { key: "s:none", since: "1970-01-01T00:00:00.020Z", title: "no class" }, { key: "s:odd", since: "1970-01-01T00:00:00.001Z", title: "odd", class: "urgent" }],
    open_total: 2, waiting_total: 5,
  });
  assert.deepEqual(a.open.map((q) => q.id), ["q2", "s:odd", "q1", "s:none", "s:late"], "a newer blocker leads; within soon the oldest first");
  assert.deepEqual(a.open.map((q) => q.cls), ["blocker", "soon", "soon", "soon", "eventually"]);
  assert.equal(model.classLabel("blocker"), "Blocker");
  assert.equal(model.classLabel(undefined), "Soon");
  // negative: with no blocker the order is age alone, as before
  const b = attentionOf({ run: "r", open: [{ id: "q2", at: 20, text: "b" }, { id: "q1", at: 10, text: "a" }], open_total: 2 });
  assert.deepEqual(b.open.map((q) => q.id), ["q1", "q2"]);
});

test("an absent slice is not loaded; no run means no open run; an empty one is empty; a capped one says how many are hidden", () => {
  assert.equal(attentionOf(undefined), null);
  assert.equal(attentionOf({ open: [], recent: [], open_count: 0, truncated: false }).run, null);
  assert.equal(NO_RUN, "The team has no open run");
  const empty = attentionOf({ run: "r", open: [], recent: [], open_count: 0, truncated: false });
  assert.equal(empty.empty, true);
  assert.equal(ATTENTION_EMPTY, "Nothing needs you");
  const capped = attentionOf({ run: "r", open: Array.from({ length: 10 }, (_, i) => ({ id: `d${i}`, at: i })), open_count: 10, open_total: 14, truncated: true });
  assert.equal(capped.open.length, 10);
  assert.equal(capped.count, 14, "open_total, not the capped rows, is the count");
  assert.equal(capped.hidden, 4);
  assert.equal(capped.truncated, true);
});

test("the answer command passes the default option's letter as --choice, else a <letter> placeholder, and names the team", () => {
  const opts = [{ label: "Add" }, { label: "Keep three" }];
  assert.equal(answerCommand({ id: "d1", def: "Keep three", options: opts }, "ship"), "ah msg decision answer d1 --choice b --team ship");
  assert.equal(answerCommand({ id: "d2", def: null, options: opts }, "ship"), "ah msg decision answer d2 --choice <letter> --team ship", "no default: not guessed");
  assert.equal(answerCommand({ id: "d3", def: "c", options: opts }, "ship"), "ah msg decision answer d3 --choice c --team ship", "a letter default is kept");
  assert.equal(answerCommand({ id: "d4", def: "Gone", options: opts }, "ship"), "ah msg decision answer d4 --choice <letter> --team ship", "a default naming no shown option");
  assert.equal(answerCommand({ id: "d5", def: "Core, web, E4 and the shell suites", options: [{ label: "Core" }, { label: "Core, web, E4 and the s…" }] }, "ship"), "ah msg decision answer d5 --choice b --team ship", "a cut label still matches its default");
  assert.equal(answerCommand({ id: "d6", def: "Core, web, E4 and the shell suites", options: [{ label: "Core, web, E4 and the s…" }, { label: "Core, web, E4 and the s…" }] }, "ship"),
    "ah msg decision answer d6 --choice <letter> --team ship", "two cut labels with the same prefix: ambiguous, not guessed");
});

test("capped open rows: options beyond the four sent and a cut text are flagged for the Q&A log", () => {
  const [q] = attentionOf({ run: "r", open: [{ id: "d1", at: 1, text: "long…", options: [{ label: "a" }, { label: "b" }, { label: "c" }, { label: "d" }], options_total: 6 }], open_total: 1 }).open;
  assert.equal(q.more, 2);
  assert.equal(q.cut, true);
  const [whole] = attentionOf({ run: "r", open: [{ id: "d2", at: 1, text: "short", options: [{ label: "a" }] }], open_total: 1 }).open;
  assert.equal(whole.more, 0);
  assert.equal(whole.cut, false);
});

const ROWS = [
  { id: "q9", dir: "to_you", at: 9, header: "alarms", text: "Should reports wake?", options: ["Wake", "Wait"], run: "run" },
  { id: "q8", dir: "to_you", at: 8, text: "Keep Plans?", options: ["Keep Plans"], answer: "Keep Plans", by: "surrogate", answered_at: 8, run: "run", class: "D4" },
  { id: "run#3", dir: "decided", at: 7, header: "scroll", text: "Pane scrolls?", options: [], answer: "Pane scrolls", by: "orchestrator", run: "run", rationale: "why" },
].map(listRow);

test("list rows map the wire fields once; the facets keep their rows in list order", () => {
  assert.equal(ROWS[1].cls, "D4", "a surrogate answer keeps its class on the to-you row");
  assert.equal(ROWS[1].item, null, "header is optional");
  assert.equal(ROWS[0].run, "run");
  assert.equal(ROWS[1].answeredAt, 8);
  assert.equal(listRow({ id: "x", body: { id: "x", dir: "decided", text: "t" } }).dir, "decided", "a row wrapped in a body reads the same");
  assert.deepEqual(qaRows(ROWS, "all").map((r) => r.id), ["q9", "q8", "run#3"]);
  assert.deepEqual(qaRows(ROWS, "to_you").map((r) => r.id), ["q9", "q8"]);
  assert.deepEqual(qaRows(ROWS, "decided").map((r) => r.id), ["run#3"]);
});

test("search matches the question, header, options and answer, case-blind, inside the facet", () => {
  assert.deepEqual(qaRows(ROWS, "all", "ALARMS").map((r) => r.id), ["q9"], "the plan item");
  assert.deepEqual(qaRows(ROWS, "all", "wait").map((r) => r.id), ["q9"], "an option");
  assert.deepEqual(qaRows(ROWS, "all", "pane scrolls").map((r) => r.id), ["run#3"], "the text and the answer");
  assert.deepEqual(qaRows(ROWS, "to_you", "pane").map((r) => r.id), [], "the facet still applies");
  assert.deepEqual(qaRows(ROWS, "all", "  ").map((r) => r.id), ["q9", "q8", "run#3"], "a blank query keeps every row");
});

test("the Q&A empty states: loading, no questions at all, none left by the filter", () => {
  assert.equal(qaEmpty(false, 0, 0), "Loading…");
  assert.equal(qaEmpty(true, 0, 0), QA_EMPTY);
  assert.equal(QA_EMPTY, "No questions in this run. Questions appear here when a pipeline run parks one for you.");
  assert.equal(qaEmpty(true, 3, 0), QA_NO_MATCH);
  assert.equal(qaEmpty(true, 3, 1), null);
});

test("9 and 0 are no longer keys: Attention and Q&A are the Inbox's tabs", () => {
  const seen = [];
  const store = { state: { ui: { view: "overview", team: TEAM }, slices: new Map() }, setUi: (p) => seen.push(p) };
  const key = (k) => globalKey({ key: k, target: {}, preventDefault() {} }, store, { body: {} });
  assert.equal(key("9"), false);
  assert.equal(key("0"), false);
  assert.deepEqual(seen, []);
});

test("the two Inbox tabs have addresses, and the old ones redirect to them", () => {
  assert.deepEqual([uiOf("#/inbox/attention?team=" + encodeURIComponent(TEAM)).view, uiOf("#/inbox/attention?team=" + encodeURIComponent(TEAM)).inboxTab], ["inbox", "attention"]);
  assert.equal(uiOf("#/inbox/qa").inboxTab, "qa");
  assert.equal(hashOf({ view: "inbox", inboxTab: "qa", team: TEAM }), `#/inbox/qa?team=${encodeURIComponent(TEAM)}`);
  assert.equal(redirectOf(`#/questions?team=${encodeURIComponent(TEAM)}`), `#/inbox/qa?team=${encodeURIComponent(TEAM)}`);
});

const off = (el) => el.disabled === true || (el.getAttribute("disabled") ?? false) !== false;
const search = (m) => find(m.root, "input").find((i) => i.getAttribute("type") === "search");
const tab = (m, label) => find(m.root, "nav").filter((n) => /\bviews\b/.test(n.getAttribute("class") ?? "")).flatMap((n) => find(n, "button")).find((b) => text(b).startsWith(label));

test("Attention: leases the team's questions slice, draws the open questions oldest first with a command, under the Inbox's Attention tab", async () => {
  const m = await mount({ view: "inbox", ui: { view: "inbox", inboxTab: "attention", team: TEAM } });
  await until(() => find(m.root, "li.qa-card").length === 4, "the open questions");
  assert.ok(m.log.polls.at(-1).split(",").includes(`questions:${DIR}/ship`), "the slice is in the subscription");
  assert.deepEqual(find(m.root, "li.qa-card").map((c) => text(find(c, "span.qa-id")[0])), ["d7", "d9", "d12", "d14"]);
  assert.deepEqual(find(m.root, "input.mono").map((i) => i.value ?? i.getAttribute("value")), ["d7 --choice a", "d9 --choice a", "d12 --choice d", "d14 --choice <letter>"].map((c) => `ah msg decision answer ${c} --team ship --run ${RUN}`));
  assert.equal(find(find(m.root, "ul.qa-recent")[0], "li").length, 3);
  assert.equal(tab(m, "Attention").getAttribute("aria-pressed"), "true");
  assert.equal(find(m.root, "button.inbox-btn").length, 1, "the header's Inbox button");
  const sends = find(m.root, "button").filter((b) => text(b) === "Answer");
  assert.equal(sends.length, 4, "an Answer button per card");
  assert.ok(!off(sends[0]) && off(sends[3]), "the default starts picked (L6): d7 has one and can be answered at once; d14 has none and waits for a choice");
  assert.equal(find(m.root, "input").filter((i) => i.getAttribute("type") === "checkbox").length, 5, "d14 is multi: four options and Other as checkboxes");

  const more = find(m.root, "button.qa-full-btn");
  assert.deepEqual(more.map(text), ["+2 more"], "d12 has six options; the slice sends four");
  more[0].fire("click");
  await until(() => find(m.root, "section.qa-detail").length === 1, "the Q&A log opened on d12");
  assert.match(text(find(m.root, "section.qa-detail")[0]), /Nothing: trust the reviewer/, "every option, from the list");
  assert.equal(m.store.state.ui.qaOpen, null, "the hand-off is used once");

  tab(m, "Board").fire("click");
  await until(() => find(m.root, "li.qa-card").length === 0, "the Attention view unmounted");
  assert.ok(m.store.state.slices.has(`questions:${DIR}/ship`), "the header's Needs-you count still holds the slice (F1)");
});

// A store that holds the given slices and list windows and never moves: enough for a view's empty states.
const still = (slices = [], lists = []) => ({ state: { slices: new Map(slices), lists: new Map(lists), ui: {} }, subscribe: () => () => {}, lease() {}, page: async () => {} });

test("Attention: no open question says Nothing needs you, no open run says so, a slice not yet held says Loading", async () => {
  const noRun = installDom();
  render(html`<${AttentionView} store=${still([[`questions:${DIR}/ship`, { version: 1, body: { open: [], recent: [], open_count: 0, truncated: false } }]])} teamId=${TEAM} />`, noRun);
  await settle();
  assert.equal(text(find(find(noRun, "div.qa-main")[0], "p.qa-none")[0]), "The team has no open run");
  const root = installDom();
  render(html`<${AttentionView} store=${still([[`questions:${DIR}/ship`, { version: 1, body: { run: "r", open: [], recent: [], open_count: 0, truncated: false } }]])} teamId=${TEAM} />`, root);
  await settle();
  assert.equal(text(find(find(root, "div.qa-main")[0], "p.qa-none")[0]), "Nothing needs you");
  assert.equal(text(find(find(root, "aside.qa-side")[0], "p.qa-none")[0]), "None yet");
  const cold = installDom();
  render(html`<${AttentionView} store=${still()} teamId=${TEAM} />`, cold);
  await settle();
  assert.equal(text(find(cold, "p.skeleton")[0]), "Loading…");
});

const SHIP = { key: "s:ship", item: "ship", stream: "s", title: "Ship it", text: "Ship now?", options: ["Now", "Later"], since: "2026-11-02T11:00:00Z", owner: "impl" };
const FREE = { ...SHIP, key: "s:free", text: "What name?", options: [], since: "2026-11-02T12:00:00Z" };

test("attentionOf merges waiting items with the open questions, oldest first, and counts the fold's total", () => {
  const a = attentionOf({ run: "r", open: [{ id: "d1", at: Date.parse("2026-11-02T11:30:00Z"), text: "q", options: [] }], items: [FREE, SHIP], recent: [], open_total: 1, waiting_total: 5, truncated: true });
  assert.deepEqual(a.open.map((q) => q.id), ["s:ship", "d1", "s:free"]);
  assert.equal(a.open[0].kind, "item");
  assert.equal(a.open[0].text, "Ship now?");
  assert.deepEqual(a.open[0].options, [{ label: "Now", rec: false }, { label: "Later", rec: false }]);
  assert.equal(a.count, 5);
  assert.equal(a.hidden, 2, "Showing 3 of 5");
  assert.deepEqual(a.decisions.map((q) => q.id), ["d1"], "a decision card reads decisions alone");
  assert.equal(a.decisionCount, 1);
});

test("negative: a slice with no waiting_total counts its open questions, as an older daemon's does", () => {
  assert.equal(attentionOf({ run: "r", open: [{ id: "d1", at: 1, text: "q", options: [] }], recent: [], open_total: 1, truncated: false }).count, 1);
});

test("a terminal_only card has no answer or dismiss body; negative: the same card unmarked sends as before (ah2-387 T2)", () => {
  const [q] = attentionOf({ run: "r", open: [{ id: "q4", options: [{ label: "Yes" }], terminal_only: true }], recent: [] }).open;
  const pick = toggle(q, NO_PICK, 0);
  assert.equal(answerBody(TEAM, "r", q, pick), null);
  assert.equal(model.dismissBody(TEAM, "r", q, "Already done"), null);
  const { terminal_only: _, ...plain } = q;
  assert.deepEqual(answerBody(TEAM, "r", plain, pick), { dir: DIR, team: "ship", run: "r", id: "q4", choice: "a" });
});

test("an item's answer body names the item and its since, needs no run, and refuses nothing picked", () => {
  const [q] = attentionOf({ open: [], items: [SHIP], recent: [] }).open;
  assert.deepEqual(answerBody(TEAM, null, q, toggle(q, NO_PICK, 1)), { dir: DIR, team: "ship", item: "s:ship", since: SHIP.since, choice: "b" });
  assert.equal(answerBody(TEAM, null, q, NO_PICK), null, "nothing picked");
  assert.deepEqual(answerBody(TEAM, null, { id: "q1", options: [], multi: false }, { ...NO_PICK, other: true, text: "x" }), { dir: DIR, team: "ship", id: "q1", choice: "other: x" }, "a decision with no run: the decision form without run");
});

test("an item with no options starts on Other; negative: one with options, and a decision with none, start on nothing", () => {
  const [free, ship] = [FREE, SHIP].map((i) => attentionOf({ open: [], items: [i], recent: [] }).open[0]);
  assert.equal(model.startPick(free).other, true);
  assert.equal(model.startPick(ship).other, false);
  assert.equal(model.startPick({ id: "d1", options: [], multi: false, def: null }).other, false);
});

test("an item's asker is who set it waiting; negative: its owner alone names no asker", () => {
  const asked = (i) => attentionOf({ open: [], items: [i], recent: [] }).open[0];
  assert.equal(asked({ ...SHIP, asker: "orchestrator@ship" }).asker, "orchestrator@ship");
  assert.equal(asked(SHIP).asker, null);
});

test("Attention: an item waiting with no run is listed and answerable: no NO_RUN, no command line", async () => {
  const root = installDom();
  render(html`<${AttentionView} store=${still([[`questions:${DIR}/ship`, { version: 1, body: { open: [], items: [SHIP], recent: [], open_total: 0, waiting_total: 1, truncated: false } }]])} teamId=${TEAM} />`, root);
  await settle();
  assert.equal(find(root, "li.qa-card").length, 1);
  assert.equal(find(find(root, "div.qa-main")[0], "p.qa-none").length, 0, "no NO_RUN");
  assert.equal(find(root, "label.qa-cmd").length, 0, "no such command for an item");
  assert.equal(find(root, "span.qa-id").length, 0, "an item's raw key leads no row");
  assert.equal(find(root, "input").filter((i) => i.getAttribute("type") === "radio").length, 3, "two options and Other");
});

test("Attention: a waiting item's card lists both its attachments, a link only for http(s) (lane-questions slice 3)", async () => {
  const root = installDom();
  const attachments = [{ ref: "docs/a.md", kind: "file", title: "Candidate A", sha: null }, { ref: "https://example.com/b", kind: "url", title: "Candidate B", sha: null }];
  render(html`<${AttentionView} store=${still([
    [`questions:${DIR}/ship`, { version: 1, body: { open: [], items: [{ ...SHIP, work_id: "ah-9" }], recent: [], open_total: 0, waiting_total: 1, truncated: false } }],
    [`item-desc:${DIR}/9`, { version: 1, body: { id: "ah-9", description: null, attachments } }],
  ])} teamId=${TEAM} />`, root);
  await settle();
  const list = find(root, "ul.qa-attach")[0];
  assert.equal(find(list, "li").length, 2);
  assert.ok(text(list).includes("docs/a.md") && text(list).includes("Candidate B"), text(list));
  assert.deepEqual(find(list, "a").map((a) => a.getAttribute("href")), ["https://example.com/b"]);
});

test("the open form and the log's Options badge only the options the slice marks Recommended; negative: an unmarked row has no badge", async () => {
  const badged = (el) => find(el, "label.qa-opt").map((l) => [text(find(l, "span.qa-label")[0]), find(l, "span.qa-badge").map(text)]);
  const root = installDom();
  const open = [{ id: "q1", at: 10, text: "Ship?", options: [{ label: "Go" }, { label: "Hold" }], recommended: [0], default: "Hold" }, { id: "q2", at: 20, text: "Plain?", options: [{ label: "Yes" }, { label: "No" }], default: "Yes" }];
  render(html`<${AttentionView} store=${still([[`questions:${DIR}/ship`, { version: 1, body: { run: "r", open, items: [{ ...SHIP, recommended: [1] }], recent: [], open_total: 2, waiting_total: 3, truncated: false } }]])} teamId=${TEAM} />`, root);
  await settle();
  const cards = find(root, "li.qa-card");
  assert.equal(cards.length, 3);
  const byLabels = Object.fromEntries(cards.map((c) => [badged(c).map(([l]) => l).join("|"), badged(c)]));
  assert.deepEqual(byLabels["Go|Hold|Other"], [["Go", ["Recommended"]], ["Hold", []], ["Other", []]]);
  assert.deepEqual(byLabels["Yes|No|Other"], [["Yes", []], ["No", []], ["Other", []]], "an unmarked question draws no badge");
  assert.deepEqual(byLabels["Now|Later|Other"], [["Now", []], ["Later", ["Recommended"]], ["Other", []]], "an item's marked option");

  const items = [{ id: "d1", dir: "to_you", at: 2, text: "Ship?", options: ["Now", "Later"], recommended: [1], answer: "Later", run: "r" }, { id: "d2", dir: "to_you", at: 1, text: "Plain?", options: ["Yes", "No"], run: "r" }];
  const log = installDom();
  render(html`<${QAView} store=${still([], [[`list:questions:${DIR}/ship`, { windows: [{ items, next: null }], loading: false }]])} teamId=${TEAM} />`, log);
  await settle();
  const options = async (id) => {
    const opener = find(log, "button.qa-open").find((b) => b.getAttribute("data-qa") === id);
    opener.fire("click", { currentTarget: opener });
    await until(() => find(log, "section.qa-detail").length === 1 && text(find(find(log, "section.qa-detail")[0], "span.qa-id")[0]) === id, `the ${id} panel`);
    return find(find(find(log, "section.qa-detail")[0], "ol.qa-options")[0], "li").map((li) => [text(find(li, "span.qa-label")[0]), find(li, "span.qa-badge").map(text), find(li, "span.qa-default").map(text)]);
  };
  assert.deepEqual(await options("d1"), [["Now", [], []], ["Later", ["Recommended"], ["answer"]]]);
  assert.deepEqual(await options("d2"), [["Yes", [], []], ["No", [], []]]);
});

test("the board item dialog badges the options the slice marks Recommended; negative: an unmarked option has none", async () => {
  const { ItemDialog } = await import("../../web/ui/item-dialog.js");
  const root = installDom();
  const ask = attentionOf({ open: [{ id: "q1", at: 1, text: "Ship?", options: [{ label: "Go" }, { label: "Hold" }], recommended: [1], default: "Go" }], recent: [] }).open[0];
  render(html`<${ItemDialog} p=${{ kind: "Decision", title: "Ship?", rows: [], ask }} store=${still()} onClose=${() => {}} />`, root);
  await settle();
  const lis = find(find(root, "ul.kb-list")[0], "li");
  assert.deepEqual(lis.map((li) => [text(li).trim(), find(li, "span.qa-badge").map(text)]), [["Go (default)", []], ["Hold Recommended", ["Recommended"]]]);
});

test("Attention: an item whose question is its title names it once, as words", async () => {
  const root = installDom();
  const named = { ...SHIP, key: "_run:ship-it", title: "ship it", text: null };
  render(html`<${AttentionView} store=${still([[`questions:${DIR}/ship`, { version: 1, body: { open: [], items: [named], recent: [], open_total: 0, waiting_total: 1, truncated: false } }]])} teamId=${TEAM} />`, root);
  await settle();
  assert.equal(text(find(root, "h3.qa-header")[0]), "ship it");
  assert.doesNotMatch(text(find(root, "p.qa-meta")[0]), /ship it|_run/);
});

test("Attention: an item with no options shows the text box alone", async () => {
  const root = installDom();
  render(html`<${AttentionView} store=${still([[`questions:${DIR}/ship`, { version: 1, body: { open: [], items: [FREE], recent: [], open_total: 0, waiting_total: 1, truncated: false } }]])} teamId=${TEAM} />`, root);
  await settle();
  assert.equal(find(root, "input").filter((i) => i.getAttribute("type") === "radio").length, 0, "no radio");
  assert.equal(find(root, "input.qa-other").length, 1);
});

// A store whose one questions slice can move, as a slice push moves it.
const moving = (item) => {
  const subs = new Set();
  const store = { state: { slices: new Map(), lists: new Map(), ui: {} }, subscribe: (f) => (subs.add(f), () => subs.delete(f)), lease() {}, page: async () => {} };
  let version = 0;
  store.push = (next) => {
    store.state = { ...store.state, slices: new Map([[`questions:${DIR}/ship`, { version: ++version, body: { open: [], items: [next], recent: [], open_total: 0, waiting_total: 1, truncated: false } }]]) };
    subs.forEach((f) => f(store.state));
  };
  store.push(item);
  return store;
};
const answerOff = (root) => off(find(root, "button").find((b) => text(b) === "Answer"));
const pickSecond = async (root) => {
  find(root, "input").filter((i) => i.getAttribute("type") === "radio")[1].fire("change");
  await settle();
};

test("a re-asked item gets a new form: the old pick does not carry onto the new options", async () => {
  const root = installDom();
  const store = moving(SHIP);
  render(html`<${AttentionView} store=${store} teamId=${TEAM} />`, root);
  await settle();
  await pickSecond(root);
  assert.equal(answerOff(root), false, "picked, so it can be sent");
  store.push({ ...SHIP, text: "Ship where?", options: ["EU", "US"], since: "2026-11-02T13:00:00Z" });
  await settle();
  assert.equal(answerOff(root), true, "nothing picked on the new question");
});

test("a re-ask with no options shows the text box", async () => {
  const root = installDom();
  const store = moving(SHIP);
  render(html`<${AttentionView} store=${store} teamId=${TEAM} />`, root);
  await settle();
  await pickSecond(root);
  store.push({ ...SHIP, text: "What, then?", options: [], since: "2026-11-02T13:00:00Z" });
  await settle();
  assert.equal(find(root, "input.qa-other").length, 1);
});

test("negative: a note-only edit keeps the since, so the form and its pick stay", async () => {
  const root = installDom();
  const store = moving(SHIP);
  render(html`<${AttentionView} store=${store} teamId=${TEAM} />`, root);
  await settle();
  await pickSecond(root);
  store.push({ ...SHIP, text: "Ship now, really?" });
  await settle();
  assert.equal(answerOff(root), false, "the pick survives");
});

test("the status says when the answer was recorded but the Orchestrator was not told; negative: otherwise no note", () => {
  assert.equal(replyState(200, { ok: true, decision: "Now", event_error: "unknown_address" }).note, "The Orchestrator was not told.");
  assert.equal(replyState(200, { ok: true, decision: "Now", event_error: "unknown_address" }).done, true);
  assert.equal(replyState(200, { ok: true, decision: "Now" }).note, null);
});

test("All teams shows no question tabs", async () => {
  const m = await mount({ view: "teams" });
  assert.equal(tab(m, "Attention"), undefined);
  assert.equal(tab(m, "Q&A"), undefined);
});

test("Q&A: pages the list, filters by facet and search, and a row opens a read-only panel that Escape closes", async () => {
  const m = await mount({ view: "inbox", ui: { view: "inbox", inboxTab: "qa", team: TEAM } });
  await until(() => find(m.root, "td.qa-id").length > 0, "the list rows");
  assert.ok(m.log.lists.some((l) => String(l.id ?? l).includes(`list:questions:${DIR}/ship`)) || m.store.state.lists.has(`list:questions:${DIR}/ship`), "the list was paged");
  const ids = () => find(m.root, "td.qa-id").map(text);
  assert.equal(ids().length, 9);
  find(find(m.root, "span.qa-facets")[0], "button").find((b) => text(b) === "Terminal").fire("click");
  await until(() => find(m.root, "p.qa-none").length === 1, "no terminal rows in a run's list");
  find(find(m.root, "span.qa-facets")[0], "button").find((b) => text(b) === "Q&A").fire("click");
  await until(() => ids().length === 9, "the run's rows are all Q&A");
  find(find(m.root, "span.qa-facets")[0], "button").find((b) => text(b) === "All").fire("click");
  search(m).fire("input", { target: { value: "wave" } });
  await until(() => ids().length === 1, "the search");
  assert.deepEqual(ids(), ["d9"]);
  search(m).fire("input", { target: { value: "zzz" } });
  await until(() => find(m.root, "p.qa-none").length === 1, "no match");
  assert.equal(text(find(m.root, "p.qa-none")[0]), "No question matches.");
  search(m).fire("input", { target: { value: "" } });
  await until(() => ids().length === 9, "every row back");

  const opener = find(m.root, "button.qa-open").find((b) => b.getAttribute("data-qa") === "d4");
  opener.fire("click", { currentTarget: opener });
  await until(() => find(m.root, "section.qa-detail").length === 1, "the panel");
  const panel = find(m.root, "section.qa-detail")[0];
  await until(() => globalThis.document.activeElement === find(panel, "button.close")[0], "focus moved into the panel, where Escape is handled");
  assert.match(text(panel), /Every other view scrolls inside its pane/);
  assert.match(text(panel), /20261030-233614-ui-w2/);
  assert.match(text(panel), /qa-scroll/);
  panel.fire("keydown", { key: "Escape", stopPropagation() {} });
  await until(() => find(m.root, "section.qa-detail").length === 0, "the panel closed");
  assert.ok(globalThis.document.activeElement === opener, "focus back on the row");
});

test("History renders a question-log thread: badge, prompt, the turn's reply, the later replies folded; unprompted rows stay out", async () => {
  const items = [
    { id: "p1", dir: "jim", at: 2000, text: "why did it fail?", question: true, reply: "Dispatched.", reply_at: 2100, later: [{ at: 9000, text: "It was a miss…" }], replies: 2, cut: true },
    { id: "r9", dir: "unprompted", at: 1000, text: "", later: [{ at: 1000, text: "forwarded" }] },
  ];
  // the row was cut to heads, so the panel reads the thread whole
  const fetched = [];
  // a direct reply and its turn's row: the web panel draws no Final or More yet, so the turn's text stays a later reply there
  const whole = { prompt: { id: "p1", text: "why did it fail?" }, replies: [{ id: "dp1", at: 2100, text: "Dispatched.", part: "direct", trigger: "typed" },
    { id: "t1", at: 2500, text: "Dispatched.\n\nChecked the build.", final: "Checked the build.", trigger: "typed" }, { id: "r2", at: 9000, text: "It was a missing using.", trigger: "later" }], ask: null };
  const store = { ...still([], [[`list:questions:${DIR}/ship`, { windows: [{ items, next: null }], loading: false }]]), fetchList: async (id) => (fetched.push(id), whole) };
  const root = installDom();
  render(html`<${QAView} store=${store} teamId=${TEAM} />`, root);
  await settle();
  assert.deepEqual(find(root, "td.qa-id").map(text), ["p1"]);
  assert.equal(text(find(root, "span.qa-badge")[0]), "question");
  const opener = find(root, "button.qa-open")[0];
  opener.fire("click", { currentTarget: opener });
  await until(() => find(root, "section.qa-detail").length === 1, "the thread");
  const panel = find(root, "section.qa-detail")[0];
  assert.match(text(panel), /asked by you/);
  assert.match(text(panel), /Dispatched\./);
  await until(() => /It was a missing using\./.test(text(find(root, "details.qa-later")[0] ?? { textContent: "" })), "the whole thread");
  assert.deepEqual(fetched, [`list:qa-thread:${DIR}/p1`]);
  const later = find(find(root, "section.qa-detail")[0], "details.qa-later")[0];
  assert.equal(text(find(later, "summary")[0]), "2 later replies");
  assert.match(text(later), /Checked the build\./);
});

test("Q&A for a team with no questions says so, and when they appear (C8)", async () => {
  const root = installDom();
  render(html`<${QAView} store=${still([], [[`list:questions:${DIR}/ship`, { windows: [{ items: [], next: null }], loading: false }]])} teamId=${TEAM} />`, root);
  await settle();
  assert.equal(text(find(root, "p.qa-none")[0]), "No questions in this run. Questions appear here when a pipeline run parks one for you.");
});

test("Attention past the open cap says how many it shows and offers the Q&A log, which holds them all", async () => {
  const root = installDom();
  const store = { ...still([[`questions:${DIR}/ship`, { version: 1, body: { run: "r", open: Array.from({ length: 10 }, (_, i) => ({ id: `d${i}`, at: i + 1, text: `q${i}`, options: [] })), recent: [], open_count: 10, open_total: 14, truncated: true } }]]), seen: [] };
  store.setUi = (p) => store.seen.push(p);
  render(html`<${AttentionView} store=${store} teamId=${TEAM} />`, root);
  await settle();
  assert.equal(find(root, "li.qa-card").length, 10);
  const note = find(find(root, "div.qa-main")[0], "p.qa-none")[0];
  assert.match(text(note), /^Showing 10 of 14, oldest first\./);
  find(note, "button")[0].fire("click");
  assert.deepEqual(store.seen, [{ view: "inbox", inboxTab: "qa" }]);
  assert.equal(text(find(root, "span.qa-count")[0]), "14");
});

const D7 = { id: "d7", def: "Reports still wake", options: [{ label: "Reports still wake" }, { label: "Wait for unlock" }, { label: "Only urgent ones wake" }], multi: false };
const D14 = { id: "d14", def: null, options: ["Core", "Web", "E4", "Shell suites"].map((label) => ({ label })), multi: true };

test("a pick becomes the request body: letters in option order, Other trimmed and last, nothing sendable without a choice", () => {
  const pick = (q, ...steps) => steps.reduce((p, s) => (typeof s === "string" && s !== "other" ? { ...p, text: s } : toggle(q, p, s)), NO_PICK);
  assert.equal(answerBody(TEAM, "r1", D7, NO_PICK), null, "nothing picked");
  assert.deepEqual(answerBody(TEAM, "r1", D7, pick(D7, 1)), { dir: DIR, team: "ship", run: "r1", id: "d7", choice: "b" });
  assert.deepEqual(answerBody(TEAM, "r1", D7, pick(D7, 0, 2)).choice, "c", "a radio holds one");
  assert.equal(answerBody(TEAM, "r1", D7, pick(D7, 0, "other")), null, "Other with no text");
  assert.equal(answerBody(TEAM, "r1", D7, pick(D7, "other", "   ")), null, "Other with only spaces");
  assert.equal(answerBody(TEAM, "r1", D7, pick(D7, "other", "  later  ")).choice, "other: later", "trimmed and normalised");
  assert.equal(answerBody(TEAM, "r1", D7, pick(D7, "other", "x".repeat(2001))), null, "over the 2000-char cap");
  assert.equal(answerBody(TEAM, "r1", D7, pick(D7, "other", "é".repeat(1001))), null, "1001 chars but 2002 UTF-8 bytes: over the byte cap");
  assert.equal(answerBody(TEAM, "r1", D7, pick(D7, "other", "é".repeat(1000))).choice, `other: ${"é".repeat(1000)}`, "2000 bytes exactly is allowed");
  assert.equal(model.otherOver({ other: true, text: "😀".repeat(501) }), true, "4-byte characters count as 4");
  assert.equal(model.otherOver({ other: false, text: "😀".repeat(501) }), false, "an unchecked Other is never over");
  assert.deepEqual(answerBody(TEAM, null, D7, pick(D7, 1)), { dir: DIR, team: "ship", id: "d7", choice: "b" }, "no run: the same body without run");
  assert.equal(answerBody(TEAM, null, D7, NO_PICK), null, "negative: no run and nothing picked");
  assert.equal(answerBody(TEAM, null, D7, pick(D7, "other", "   ")), null, "negative: no run and Other with only spaces");
  assert.deepEqual(model.dismissBody(TEAM, null, D7, "Already done"), { dir: DIR, team: "ship", id: "d7", dismiss: "Already done" }, "no run: Not needed sends without run");
  assert.deepEqual(model.dismissBody(TEAM, "r1", D7, "Already done"), { dir: DIR, team: "ship", run: "r1", id: "d7", dismiss: "Already done" }, "negative: with a run the dismiss body is today's");
  assert.deepEqual(answerBody(TEAM, "r1", D14, pick(D14, 2, "other", " Lint ", 0)).choices, ["a", "c", "other: Lint"], "multi: option order, Other last");
  assert.equal(answerBody(TEAM, "r1", D14, pick(D14, 2, 2)), null, "checked twice is unchecked");
  assert.equal(answerBody(TEAM, "r1", D14, pick(D14, 1, "other")), null, "a checked Other needs text");
  assert.equal(answerBody(TEAM, "r1", D14, pick(D14, "other", "x", "other")), null, "Other unchecked again: nothing left");
});

test("the command follows the pick, one --choice per entry, Other quoted for the shell, and carries --run", () => {
  assert.equal(answerCommand(D7, "ship", "r1"), "ah msg decision answer d7 --choice a --team ship --run r1", "nothing picked: the default's letter");
  assert.equal(answerCommand(D7, "ship", "r1", toggle(D7, NO_PICK, 2)), "ah msg decision answer d7 --choice c --team ship --run r1");
  assert.equal(answerCommand(D14, "ship", "r1", { picked: [3, 0], other: true, text: "it's ok" }), "ah msg decision answer d14 --choice a --choice d --choice 'other: it'\\''s ok' --team ship --run r1");
});

test("a reply becomes the card's state: 200 recorded, 409 the core's detail, anything else could not send", () => {
  assert.deepEqual(replyState(200, { ok: true, id: "d20", answers: "d7", decision: "Reports still wake" }), { kind: "recorded", text: "Recorded", answer: "Reports still wake", note: null, event: null, done: true });
  assert.deepEqual(replyState(409, { refused: true, reason: "already-closed", detail: "Already answered (d10): Keep three" }), { kind: "refused", text: "Already answered (d10): Keep three", done: false });
  assert.deepEqual(replyState(400, { error: "bad-request", detail: "missing run" }), { kind: "error", text: "Could not send: missing run", done: false });
  assert.equal(replyState(0, { detail: "answer: no answer after 15 s" }).text, "Could not send: answer: no answer after 15 s");
  assert.equal(replyState(500, null).text, "Could not send: status 500");
});

test("the mock answers the route as D2 does: 200, the d9 409 fixture, stale run, not-multi, duplicates, bad requests", () => {
  const base = { dir: DIR, team: "ship", run: RUN };
  assert.deepEqual(answerReply({ ...base, id: "d14", choices: ["a", "c", "other: x, y"] }), [200, { ok: true, id: "d20", answers: "d14", decision: "Core; E4; x, y" }]);
  assert.equal(answerReply({ ...base, id: "d9", choice: "a" })[1].reason, "already-closed");
  assert.equal(answerReply({ ...base, run: "old", id: "d7", choice: "a" })[1].reason, "stale-run");
  assert.equal(answerReply({ ...base, id: "d7", choices: ["a"] })[1].reason, "not-multi");
  assert.equal(answerReply({ ...base, id: "d14", choices: ["a", "a"] })[1].reason, "duplicate-choice");
  assert.equal(answerReply({ ...base, id: "d7", choice: "e" })[1].reason, "invalid-choice");
  assert.equal(answerReply({ ...base, id: "q3", choice: "a" })[1].reason, "unknown-id");
  assert.equal(answerReply({ ...base, id: "d7" })[0], 400, "neither choice nor choices");
  assert.equal(answerReply({ ...base, id: "d14", choices: [] })[0], 400);
  assert.equal(answerReply({ dir: DIR, id: "d7", choice: "a" })[0], 400, "missing run");
});

// fetch as the mock server answers POST /ui/v1/answer; every body sent is kept.
function mockFetch(sent) {
  return async (url, init) => {
    assert.equal(url, "/ui/v1/answer");
    assert.equal(init.method, "POST");
    const body = JSON.parse(init.body);
    sent.push(body);
    const [status, reply] = answerReply(body);
    return { status, json: async () => reply };
  };
}

test("Attention: Answer asks to confirm; Confirm locks the card, sends once and collapses it to Sending…, then Recorded; 409 unlocks it with the detail", async () => {
  const sent = [];
  const saved = globalThis.fetch;
  let release;
  const gate = new Promise((r) => (release = r));
  const real = mockFetch(sent);
  globalThis.fetch = async (url, init) => (await gate, real(url, init));
  try {
    const m = await mount({ view: "inbox", ui: { view: "inbox", inboxTab: "attention", team: TEAM } });
    await until(() => find(m.root, "li.qa-card").length === 4, "the open questions");
    const card = (id) => find(m.root, "li.qa-card").find((c) => text(find(c, "span.qa-id")[0]) === id);
    const button = (id, label) => find(card(id), "button").find((b) => text(b) === label);
    const status = (id) => find(card(id), "p.qa-status")[0];
    const inputs = (id) => find(card(id), "input").filter((i) => /radio|checkbox/.test(i.getAttribute("type")));

    inputs("d7")[1].fire("change");
    await until(() => !off(button("d7", "Answer")), "Answer enabled once a choice is made");
    find(card("d7"), "form")[0].fire("submit");
    await until(() => button("d7", "Confirm"), "the confirm step");
    assert.match(text(find(card("d7"), "p.qa-ask")[0]), /Send this answer\? Wait for unlock/, "it names the answer");
    assert.ok(globalThis.document.activeElement === button("d7", "Confirm"), "focus on Confirm");
    assert.ok(off(find(card("d7"), "fieldset")[0]), "the pick is frozen while confirming");
    assert.equal(sent.length, 0, "Answer alone sends nothing");

    // the lock: a second press of Confirm, before the first send answers, posts nothing more
    const confirm = button("d7", "Confirm");
    confirm.fire("click");
    confirm.fire("click");
    await until(() => text(status("d7")).startsWith("Sending…"), "the collapsed Sending… line");
    assert.equal(find(card("d7"), "form").length, 0, "nothing left to edit or press");
    assert.equal(find(status("d7"), "span.qa-mark.spin").length, 1, "a spinner");
    assert.ok(globalThis.document.activeElement === status("d7"), "focus on the status line");
    release();
    await until(() => text(status("d7")) === "Recorded Wait for unlock", "the 200 answer");
    assert.equal(sent.length, 1, "one post for two presses");
    assert.deepEqual(sent[0], { dir: DIR, team: "ship", run: RUN, id: "d7", choice: "b" });
    assert.equal(status("d7").getAttribute("role"), "status");
    assert.match(card("d7").getAttribute("class"), /sent/);

    inputs("d9").at(-1).fire("change");
    await until(() => find(card("d9"), "input.qa-other").length === 1, "Other reveals its text input");
    assert.ok(off(button("d9", "Answer")), "Other needs text");
    find(card("d9"), "input.qa-other")[0].fire("input", { target: { value: "é".repeat(1001) } });
    await until(() => find(card("d9"), "p.qa-hint").length === 1, "the over-limit hint");
    assert.ok(off(button("d9", "Answer")), "over 2000 bytes: Answer stays disabled");
    assert.equal(find(card("d9"), "input.qa-other")[0].getAttribute("aria-invalid"), "true");
    assert.equal(find(card("d9"), "input.qa-other")[0].getAttribute("aria-describedby"), "qo-d9");
    find(card("d9"), "input.qa-other")[0].fire("input", { target: { value: " someday " } });
    await until(() => !off(button("d9", "Answer")), "Other with text enables Answer");
    assert.equal(find(card("d9"), "p.qa-hint").length, 0, "the hint goes once the text fits");
    assert.match(find(card("d9"), "input.mono")[0].getAttribute("value"), /--choice 'other: someday'/, "the command follows the pick");
    find(card("d9"), "form")[0].fire("submit");
    await until(() => button("d9", "Confirm"), "the confirm step");
    button("d9", "Confirm").fire("click");
    await until(() => text(status("d9")) === "Already answered (d10): Keep three", "the 409 detail inline");
    assert.equal(sent.at(-1).choice, "other: someday");
    assert.ok(!off(button("d9", "Answer")) && !off(find(card("d9"), "fieldset")[0]), "inputs re-enabled after a refusal");
    assert.match(status("d9").getAttribute("class"), /err/);

    inputs("d14")[2].fire("change");
    inputs("d14")[0].fire("change");
    await until(() => !off(button("d14", "Answer")), "one checked is enough");
    find(card("d14"), "form")[0].fire("submit");
    await until(() => button("d14", "Back"), "the confirm step");
    button("d14", "Back").fire("click");
    await until(() => !off(find(card("d14"), "fieldset")[0]), "Back unfreezes the pick");
    assert.ok(globalThis.document.activeElement === button("d14", "Answer"), "focus back on Answer");
    find(card("d14"), "form")[0].fire("submit");
    await until(() => button("d14", "Confirm"), "the confirm step again");
    button("d14", "Confirm").fire("click");
    await until(() => text(status("d14")) === "Recorded Core; E4", "the multi answer");
    assert.deepEqual(sent.at(-1).choices, ["a", "c"], "option order, whatever order they were checked in");
  } finally {
    globalThis.fetch = saved;
  }
});

test("Attention: a failed send unlocks the card with the error, keeps the pick, and a re-confirm posts exactly once more", async () => {
  const saved = globalThis.fetch;
  let posts = 0;
  globalThis.fetch = async () => { posts += 1; throw new Error("network down"); };
  try {
    const root = installDom();
    const body = { run: "r", open: [{ id: "d1", at: 1, text: "q", options: [{ label: "Yes" }, { label: "No" }] }], recent: [], open_total: 1, truncated: false };
    render(html`<${AttentionView} store=${still([[`questions:${DIR}/ship`, { version: 1, body }]])} teamId=${TEAM} />`, root);
    await settle();
    find(root, "input").find((i) => i.getAttribute("type") === "radio").fire("change");
    await settle();
    find(root, "form")[0].fire("submit");
    await until(() => find(root, "button").some((b) => text(b) === "Confirm"), "the confirm step");
    find(root, "button").find((b) => text(b) === "Confirm").fire("click");
    await until(() => text(find(root, "p.qa-status")[0]) === "Could not send: network down", "the failure inline");
    assert.ok(!off(find(root, "fieldset")[0]), "the pick is editable again");
    assert.deepEqual(find(root, "label").map((l) => l.getAttribute("class")), ["qa-opt on", "qa-opt", "qa-opt", "qa-cmd"], "the pick is kept");
    assert.ok(!off(find(root, "button").find((b) => text(b) === "Answer")), "can send again");
    assert.equal(posts, 1, "one post for the first confirm");
    find(root, "form")[0].fire("submit");
    await until(() => find(root, "button").some((b) => text(b) === "Confirm"), "the confirm step again");
    const confirm = find(root, "button").find((b) => text(b) === "Confirm");
    confirm.fire("click");
    confirm.fire("click");
    await until(() => text(find(root, "p.qa-status")[0]) === "Could not send: network down" && find(root, "form").length === 1, "the second failure");
    assert.equal(posts, 2, "the retry posts exactly once more");
  } finally {
    globalThis.fetch = saved;
  }
});

// One open card answered through Answer and Confirm, the answer route replying with `eventId` and the status route with `seen` in
// turn (its last reply repeated); SEEN_POLL shortened. Returns the root, the status calls and the status line.
async function seenRig(eventId, seen, { hidden = false } = {}) {
  const calls = [];
  const saved = { fetch: globalThis.fetch, every: model.SEEN_POLL.every };
  model.SEEN_POLL.every = 10;
  globalThis.fetch = async (url, init) => {
    if (url === "/ui/v1/answer") {
      return { status: 200, json: async () => ({ ok: true, id: "d1", answers: "d1", decision: "Yes", ...(eventId ? { event_id: eventId } : {}) }) };
    }
    calls.push(url);
    const [status, body] = seen[Math.min(calls.length, seen.length) - 1];
    return { status, json: async () => body };
  };
  const root = installDom();
  let onVisible = null;
  globalThis.document.hidden = hidden;
  globalThis.document.addEventListener = (name, fn) => name === "visibilitychange" && (onVisible = fn);
  globalThis.document.removeEventListener = () => {};
  const body = { run: "r", open: [{ id: "d1", at: 1, text: "q", options: [{ label: "Yes" }, { label: "No" }] }], recent: [], open_total: 1, truncated: false };
  render(html`<${AttentionView} store=${still([[`questions:${DIR}/ship`, { version: 1, body }]])} teamId=${TEAM} />`, root);
  await settle();
  find(root, "input").find((i) => i.getAttribute("type") === "radio").fire("change");
  await settle();
  find(root, "form")[0].fire("submit");
  await until(() => find(root, "button").some((b) => text(b) === "Confirm"), "the confirm step");
  find(root, "button").find((b) => text(b) === "Confirm").fire("click");
  await until(() => text(find(root, "p.qa-status")[0]).startsWith("Recorded"), "Recorded");
  const restore = () => {
    render(null, root);
    globalThis.fetch = saved.fetch;
    model.SEEN_POLL.every = saved.every;
  };
  return { root, calls, line: () => text(find(root, "p.qa-status")[0]), show: () => { globalThis.document.hidden = false; onVisible?.(); }, restore };
}
const quiet = () => new Promise((r) => setTimeout(r, 80));

test("Recorded polls answer-status through a 5xx until the Orchestrator has seen the event, then stops", async () => {
  const r = await seenRig("e1", [[503, { error: "unavailable" }], [200, { seen: false, state: "routed" }], [200, { seen: true, state: "delivered", seen_at: 5 }]]);
  try {
    await until(() => r.line().startsWith("Seen by the Orchestrator"), "Seen");
    assert.equal(r.line(), "Seen by the Orchestrator Yes");
    assert.equal(r.calls[0], `/ui/v1/answer-status?dir=${DIR}&team=ship&event=e1`);
    await quiet();
    assert.equal(r.calls.length, 3, "no poll after seen");
  } finally {
    r.restore();
  }
});

test("negative: a 404 stops the poll at Recorded; an acked-not-seen state does too", async () => {
  for (const reply of [[404, { error: "not-found" }], [200, { seen: false, state: "acked" }]]) {
    const r = await seenRig("e1", [reply]);
    try {
      await until(() => r.calls.length === 1, "the first poll");
      await quiet();
      assert.equal(r.calls.length, 1, `${reply[0]} ${reply[1].state ?? ""}: one poll, then none`);
      assert.equal(r.line(), "Recorded Yes");
    } finally {
      r.restore();
    }
  }
});

test("negative: no poll while the tab is hidden, and none at all without an event id", async () => {
  const r = await seenRig("e1", [[200, { seen: true, state: "acked", seen_at: 5 }]], { hidden: true });
  try {
    await quiet();
    assert.equal(r.calls.length, 0, "hidden: no poll");
    r.show();
    await until(() => r.line().startsWith("Seen by the Orchestrator"), "Seen once the tab shows");
  } finally {
    r.restore();
  }
  const none = await seenRig(null, [[200, { seen: true, state: "acked" }]]);
  try {
    await quiet();
    assert.equal(none.calls.length, 0, "no event id: no status call");
    assert.equal(none.line(), "Recorded Yes");
  } finally {
    none.restore();
  }
});

test("a just-answered card stays, collapsed, after the slice drops its row: it reaches Seen, then goes at the hold's end", async () => {
  const saved = { fetch: globalThis.fetch, every: model.SEEN_POLL.every, hold: model.SEEN_POLL.hold };
  Object.assign(model.SEEN_POLL, { every: 10, hold: 300 });
  let polls = 0;
  globalThis.fetch = async (url) => {
    if (url === "/ui/v1/answer") {
      return { status: 200, json: async () => ({ ok: true, id: "d1", answers: "d1", decision: "Yes", event_id: "e1" }) };
    }
    polls += 1;
    return { status: 200, json: async () => (polls < 3 ? { seen: false, state: "routed" } : { seen: true, state: "delivered", seen_at: 5 }) };
  };
  const root = installDom();
  try {
    const id = `questions:${DIR}/ship`;
    const body = { run: "r", open: [{ id: "d1", at: 1, text: "q", options: [{ label: "Yes" }, { label: "No" }] }], recent: [], open_total: 1, truncated: false };
    const subs = [];
    const store = { state: { slices: new Map([[id, { version: 1, body }]]), lists: new Map(), ui: {} }, subscribe: (f) => (subs.push(f), () => {}), lease() {}, page: async () => {} };
    render(html`<${AttentionView} store=${store} teamId=${TEAM} />`, root);
    await settle();
    find(root, "input").find((i) => i.getAttribute("type") === "radio").fire("change");
    await settle();
    find(root, "form")[0].fire("submit");
    await until(() => find(root, "button").some((b) => text(b) === "Confirm"), "the confirm step");
    find(root, "button").find((b) => text(b) === "Confirm").fire("click");
    await until(() => text(find(root, "p.qa-status")[0] ?? { textContent: "" }).startsWith("Recorded"), "Recorded");
    // the answer's write moves the row out of open, as the daemon's slice does
    store.state.slices.set(id, { version: 2, body: { ...body, open: [], open_total: 0 } });
    subs.forEach((f) => f(store.state));
    await settle();
    assert.equal(find(root, "li.qa-card").length, 1, "the card outlives the slice's drop");
    await until(() => text(find(root, "p.qa-status")[0]).startsWith("Seen by the Orchestrator"), "Seen on the kept card");
    await until(() => find(root, "li.qa-card").length === 0, "the card goes at the hold's end");
    assert.equal(text(find(root, "p.qa-none")[0]), ATTENTION_EMPTY);
  } finally {
    render(null, root);
    globalThis.fetch = saved.fetch;
    Object.assign(model.SEEN_POLL, { every: saved.every, hold: saved.hold });
  }
});

test("F3: Enter on a focused option, on Other or in its text does not answer; only the Answer button submits", async () => {
  const m = await mount({ view: "inbox", ui: { view: "inbox", inboxTab: "attention", team: TEAM } });
  await until(() => find(m.root, "li.qa-card").length === 4, "the open questions");
  const card = find(m.root, "li.qa-card")[0];
  const picks = find(card, "input").filter((i) => ["radio", "checkbox"].includes(i.getAttribute("type")));
  assert.ok(picks.length >= 2, "the options and Other");
  for (const el of picks) {
    let blocked = 0;
    el.fire("keydown", { key: "Enter", preventDefault: () => (blocked += 1) });
    assert.equal(blocked, 1, `Enter on ${el.getAttribute("value")} is held back from implicit submission`);
    let other = 0;
    el.fire("keydown", { key: " ", preventDefault: () => (other += 1) });
    assert.equal(other, 0, "Space still picks");
  }
});
