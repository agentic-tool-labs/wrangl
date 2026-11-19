// The ah2 Herdr mod's Q&A data side (spec-herdr-desktop.md P3.1, P3.5, P3.6) over the shipped fixture.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { qaModel, qaSubs, canSend, sendAnswer, readThread, NO_REPLY, applyReply, sitrepModel, loadingOf, sidebarModel, terminalFinder, deskModel, needsMark, counts, historyReply, threadStart, retryMs, QUICK_RETRY_MS } from "../../herdr-desktop/mods/ah2/model.js";
import { answerBody, dismissBody, toggle, NO_PICK, threadOf } from "../../web/ui/questions-model.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const mod = join(root, "herdr-desktop", "mods", "ah2");
const reply = JSON.parse(readFileSync(join(root, "herdr-desktop", "fixture", "events.json"), "utf8"));
const slices = new Map(reply.slices.map((s) => [s.id, s]));
const NOW = Date.parse(reply.observed_at);

test("the mod's questions-model.js is a byte-identical copy of web/ui/questions-model.js", () => {
  assert.ok(readFileSync(join(mod, "questions-model.js")).equals(readFileSync(join(root, "web", "ui", "questions-model.js"))),
    "copy web/ui/questions-model.js over herdr-desktop/mods/ah2/questions-model.js");
});

test("the Q&A model groups the fixture's cards oldest first, counts waiting_on_you, and lists History newest first", () => {
  const m = qaModel(slices, NOW);
  assert.equal(m.count, 3);
  assert.deepEqual(m.teams.map((t) => t.name), ["ah2-tm4"]);
  const cards = m.teams[0].cards;
  assert.deepEqual(cards.map((c) => [c.kind, c.q.id, c.recommended]), [["item", "core:atp-1140", -1], ["decision", "d8", -1], ["decision", "d7", 0]]);
  assert.equal(cards[1].more, 2);
  assert.deepEqual(m.history.map((h) => [h.id, h.header]), [["d6", "atp-1088"], ["d5", "ship-106"], ["d4", "atp-1136"]]);
  assert.ok(qaSubs(slices).includes("questions:a1b2c3d4/ah2-tm4"));
});

test("a blocker card leads its team's cards, and the badge reads n! only while a blocker waits", () => {
  const id = "questions:a1b2c3d4/ah2-tm4";
  const q = slices.get(id);
  const marked = new Map(slices);
  marked.set(id, { ...q, body: { ...q.body, open: q.body.open.map((o) => (o.id === "d7" ? { ...o, class: "blocker" } : o)) } });
  assert.deepEqual(qaModel(marked, NOW).teams[0].cards.map((c) => [c.q.id, c.cls]), [["d7", "blocker"], ["core:atp-1140", "soon"], ["d8", "soon"]]);
  // negative: with no class anywhere every card is soon and the order is age alone
  assert.deepEqual(qaModel(slices, NOW).teams[0].cards.map((c) => c.cls), ["soon", "soon", "soon"]);
  assert.equal(needsMark(3, 1), "3!");
  assert.equal(needsMark(3, 0), "3");
  const sum = [...slices.values()].find((s) => s.id.startsWith("team-summary:"));
  const blocking = new Map(slices);
  blocking.set(sum.id, { ...sum, body: { ...sum.body, waiting_blockers: 1 } });
  assert.equal(deskModel(blocking, NOW).blockers, 1);
  assert.equal(deskModel(slices, NOW).blockers, 0);
  // every Needs you counts what Q&A lists (waiting_on_you 3), not the served needs_you 4; the other one is "blocked" (atp-1724, atp-1736)
  const d = deskModel(slices, NOW);
  const row = sidebarModel(slices, NOW).teams.find((t) => t.name === "ah2-tm4");
  assert.deepEqual([d.needsYou, d.needing.map((x) => x.n), sidebarModel(slices, NOW).needsYou, row.needsYou], [3, [3], 3, 3]);
  assert.equal(counts(row), "3 needs you · 2 working · 1 blocked");
  // negative: a team needing you only through blocked rows (nothing waiting) shows no Needs you anywhere
  const onlyBlocked = new Map(slices);
  onlyBlocked.set(sum.id, { ...sum, body: { ...sum.body, waiting_on_you: 0 } });
  const e = deskModel(onlyBlocked, NOW);
  assert.deepEqual([e.needsYou, e.needing, sidebarModel(onlyBlocked, NOW).needsYou], [0, [], 0]);
});

test("a History thread is read whole with ah msg history --json --id in the row's dir; a failed read says why", async () => {
  const runs = [];
  const whole = { prompt: { id: "p1", text: "why?" }, replies: [{ at: 1, text: "Looking.", trigger: "typed" }, { at: 2, text: "Found it.", trigger: "later" }], ask: null };
  const ok = async (cmd, args) => (runs.push([cmd, ...args]), { code: 0, stdout: JSON.stringify({ threads: [whole] }), stderr: "" });
  const got = await readThread(ok, { id: "p1", hierDir: "/r/.claude/ah2" });
  assert.deepEqual(runs, [["ah", "msg", "history", "--json", "--id", "p1", "--dir", "/r/.claude/ah2"]]);
  assert.equal(got.text, "why?");
  assert.equal(got.reply, "Looking.");
  assert.deepEqual(got.later.map((l) => l.text), ["Found it."]);
  // negative: no thread back, and no dir to read
  assert.ok((await readThread(async () => ({ code: 0, stdout: '{"threads":[]}', stderr: "" }), { id: "p9", hierDir: "/r" })).error);
  assert.ok((await readThread(ok, { id: "p1", hierDir: null })).error);
});

test("a thread's reply is its direct row, else the typed turn row's final; an old-shape turn row shows its text", () => {
  const prompt = { id: "p1", text: "why?" };
  const turn = { id: "t1", at: 1, text: "Looking.\n\nFound it.", final: "Found it.", trigger: "typed" };
  const direct = { id: "dp1", at: 2, text: "Looking.", part: "direct", trigger: "typed" };
  assert.deepEqual([threadOf({ prompt, replies: [turn, direct] }).reply, threadOf({ prompt, replies: [turn, direct] }).replyAt], ["Looking.", 2]);
  assert.equal(threadOf({ prompt, replies: [turn] }).reply, "Found it.");
  // negative: a row with no part and no final (written before replies had parts) is shown exactly as before
  assert.equal(threadOf({ prompt, replies: [{ at: 1, text: "Looking.", trigger: "typed" }] }).reply, "Looking.");
});

test("a thread splits into its direct reply, the turn's final, the rest of that turn and the later turns (qa-history-replies §8)", () => {
  const prompt = { id: "p1", text: "why?" };
  const direct = { id: "dp1", at: 2, text: "Looking.", part: "direct", trigger: "typed" };
  const turn = { id: "t1", at: 9, text: "Looking.\n\nRead the log.\n\nFound it.", final: "Found it.", trigger: "typed" };
  const next = { id: "t2", at: 20, text: "Also checked CI.\n\nGreen.", final: "Green.", trigger: "later" };
  const all = threadOf({ prompt, replies: [direct, turn, next] });
  assert.deepEqual(all, { reply: "Looking.", replyAt: 2, replyFinal: false, final: "Found it.", finalAt: 9, more: turn.text, moreAt: 9,
    later: [{ at: 20, text: next.text, final: "Green." }] });
  // a final equal to the direct text is shown once
  const once = threadOf({ prompt, replies: [direct, { ...turn, text: "Looking.", final: " Looking. " }] });
  assert.deepEqual([once.reply, once.final, once.more], ["Looking.", null, null]);
  // no direct text: the final is the reply, and the rest of the turn is More
  const late = threadOf({ prompt, replies: [turn] });
  assert.deepEqual([late.reply, late.replyAt, late.replyFinal, late.final, late.more], ["Found it.", 9, true, null, turn.text]);
  // an old row: its text is the reply and nothing else; a later old row stays a later reply
  const old = threadOf({ prompt, replies: [{ at: 1, text: "Looking.", trigger: "typed" }, { at: 5, text: "Done.", trigger: "later" }] });
  assert.deepEqual(old, { reply: "Looking.", replyAt: 1, replyFinal: false, final: null, finalAt: null, more: null, moreAt: null, later: [{ at: 5, text: "Done.", final: null }] });
  // no reply yet, and a thread whose only replies came in later turns
  assert.deepEqual(threadOf({ prompt, replies: [] }), { reply: null, replyAt: null, replyFinal: false, final: null, finalAt: null, more: null, moreAt: null, later: [] });
  assert.deepEqual(threadOf({ prompt, replies: [next] }).later.map((l) => l.final), ["Green."]);
});

test("a thread answered only in a later turn says so and opens with its Later replies shown; every other thread starts folded", () => {
  const prompt = { id: "p1", text: "why?" };
  const direct = { id: "dp1", at: 2, text: "Looking.", part: "direct", trigger: "typed" };
  const next = { id: "t2", at: 20, text: "Also checked CI.\n\nGreen.", final: "Green.", trigger: "later" };
  assert.deepEqual(threadStart(threadOf({ prompt, replies: [next] })), { note: "Answered in a later turn", laterOpen: true });
  // negative: a reply in its own turn keeps Later replies folded, and nothing at all is "No reply yet"
  assert.deepEqual(threadStart(threadOf({ prompt, replies: [direct, next] })), { note: null, laterOpen: false });
  assert.deepEqual(threadStart(threadOf({ prompt, replies: [] })), { note: "No reply yet", laterOpen: false });
});

test("a History row of a typed question carries its first reply's time and a one-line head of it, when the slice row has one", () => {
  const id = "questions:a1b2c3d4/ah2-tm4";
  const s = new Map(slices);
  const recent = [
    { id: "p2", dir: "jim", at: NOW - 60000, text: "why?", question: true, replies: 2, reply_at: NOW - 50000, reply: "Looking.\n\n  The log  says so." },
    { id: "p1", dir: "jim", at: NOW - 120000, text: "still there?", question: true, replies: 0 },
  ];
  s.set(id, { ...s.get(id), body: { ...s.get(id).body, recent } });
  const rows = qaModel(s, NOW).history.filter((r) => r.dir === "jim");
  assert.deepEqual(rows.map((r) => [r.id, r.replyAt, r.preview]), [["p2", NOW - 50000, "Looking. The log says so."], ["p1", null, null]]);
  // negative: a whitespace-only head is no preview, and a decision row gets none
  assert.deepEqual(historyReply({ reply_at: 0, reply: " \n " }), { replyAt: null, preview: null });
  assert.equal(qaModel(slices, NOW).history.find((r) => r.id === "d6").preview, null);
});

test("each card's answer is the web's own body, sent as argv to ah msg decision ui-answer", async () => {
  const cards = qaModel(slices, NOW).teams[0].cards;
  const [item, multi, single] = cards;
  const runs = [];
  const exec = async (cmd, args) => {
    runs.push([cmd, ...args]);
    return { code: 0, stdout: '{"ok":true,"id":"d9","answers":"d7","decision":"Ship it"}', stderr: "" };
  };
  const pick = (c, i) => toggle(c.q, NO_PICK, i);
  const sent = [
    [single, { pick: single.start }, answerBody(single.teamId, single.run, single.q, single.start)],
    [multi, { pick: toggle(multi.q, pick(multi, 0), 2) }, answerBody(multi.teamId, multi.run, multi.q, toggle(multi.q, pick(multi, 0), 2))],
    [item, { pick: pick(item, 1) }, answerBody(item.teamId, item.run, item.q, pick(item, 1))],
    [single, { dismiss: "Already done" }, dismissBody(single.teamId, single.run, single.q, "Already done")],
  ];
  for (const [card, how] of sent) {
    assert.equal((await sendAnswer(exec, card, how)).kind, "recorded");
  }
  assert.deepEqual(runs, sent.map(([, , body]) => ["ah", "msg", "decision", "ui-answer", "--json", JSON.stringify(body)]));
});

test("the verb's exit maps to the card states, and a 30 s cap is No reply", async () => {
  const single = qaModel(slices, NOW).teams[0].cards[2];
  const as = (r) => sendAnswer(async () => r, single, { pick: single.start });
  assert.equal((await as({ code: 2, stdout: '{"refused":true,"reason":"stale-run","detail":"open run is r2"}', stderr: "" })).text, "open run is r2");
  assert.equal((await as({ code: 3, stdout: '{"refused":true,"reason":"unproven","detail":"not the user"}', stderr: "" })).text, "Could not send: not the user");
  assert.equal((await as({ code: 1, stdout: "", stderr: "ui-answer: internal error\n" })).text, "Could not send: ui-answer: internal error");
  const late = await sendAnswer(async () => { throw new Error("ah took longer than 30 s and was stopped"); }, single, { pick: single.start });
  assert.deepEqual(late, NO_REPLY);
});

test("negative: a multi with nothing on and an item with no pick cannot send, and nothing is executed", async () => {
  const [item, multi] = qaModel(slices, NOW).teams[0].cards;
  assert.equal(canSend(multi, multi.start), false);
  assert.equal(canSend(item, item.start), false);
  let ran = 0;
  const state = await sendAnswer(async () => { ran++; }, multi, { pick: multi.start });
  assert.equal(ran, 0);
  assert.equal(state.kind, "error");
});

test("a team whose sitrep slices were removed reads as closed, not loading; one never seen still loads", () => {
  const id = "a1b2c3d4/ah2-tm4";
  const st = { slices: new Map(), epoch: null, seq: 0 };
  assert.equal(sitrepModel(st.slices, id, NOW, st.gone), null);
  applyReply(st, { ...reply, slices: reply.slices.filter((s) => s.id.endsWith(id)) });
  assert.ok(sitrepModel(st.slices, id, NOW, st.gone).regions);
  applyReply(st, { epoch: reply.epoch, seq: reply.seq + 1, slices: [], removed: [`team-summary:${id}`, `sitrep:${id}`] });
  assert.deepEqual(sitrepModel(st.slices, id, NOW, st.gone), { closed: true });
  assert.equal(sitrepModel(st.slices, "a1b2c3d4/other", NOW, st.gone), null);
});

// The P3.3 send rule over the source: exec( only in main.js fetch (the poll), model.js sendAnswer and settings.js's update check
// and install (atp-1234, never at render); sendAnswer( only in qa.js
// send(); send( only on an on_click line (or the line after it). Returns the violations.
// herdr's exec is a bare call; a RegExp's `.exec(` (plans-model.js) is not a process run. A runner call (ah.js `ah().exec(`, `.read(`,
// `.install(`, and link-model.js's `r.exec(`) is one too: it runs where ah runs (target-model.js, the one module that calls herdr's
// exec for ah).
const EXEC = /(?<!\.)\bexec\(|\b(?:ah\(\)|r)\.(?:exec|read|install)\(/g;
function sendRuleViolations(src) {
  const out = [];
  const region = (text, from, to) => [text.indexOf(from), text.indexOf(to, text.indexOf(from) + 1)];
  const within = (i, [a, b]) => a >= 0 && i > a && (b < 0 || i < b);
  // readThread is the History thread's read: an exec too, and like sendAnswer reached only from a click
  // main.js rebuildRead: the sidebar's build check, its `ah where` once per pane and the stamp's file read (ah2-111)
  const allowed = { "main.js": [["async fetch(", "async poll("], ["async rebuildRead(", "// ---- the desk"]], "model.js": [["export async function sendAnswer", "\nexport "], ["export async function readThread", "\nexport "]],
    "board-model.js": [["export async function readItemLog", "\n}\n"]], "target-model.js": [["export function runnerOf", "\n}\n"]],
    "link-model.js": [["export class Link", "\n}\n"]],
    // historyRead's ah fallback (A6, A10): reached from qa.js histFetch only, on a filter change, Load more and the filtered tab's refresh
    "qa-history-model.js": [["export async function historyRead", "\n}\n"]],
    "dismiss-model.js": [["export async function teamClose", "\n}\n"]],
    // the Rename dialog's ah fallback (ah2-218 B2): reached from rename.js renameSend only, on a Save or Clear press or the field's submit
    "rename-model.js": [["export async function teamRename", "\n}\n"]],
    "settings.js": [["// ---- the update check and install ----", "// ---- the dialog ----"]],
    // the update dialog's own task (ah2-192): `ah where` once, then the build log's read while the build runs; never in render
    "update.js": [["export function updateInit", "\nexport function updateClose"]] };
  for (const [f, text] of Object.entries(src)) {
    for (const m of text.matchAll(EXEC)) {
      if (!(allowed[f] ?? []).some((r) => within(m.index, region(text, ...r)))) out.push(`${f}: exec( at ${m.index}`);
    }
    for (const m of text.matchAll(/(?<!function )\bsendAnswer\(/g)) {
      if (f !== "qa.js" || !within(m.index, region(text, "function send(", "\nfunction "))) out.push(`${f}: sendAnswer( at ${m.index}`);
    }
    for (const m of text.matchAll(/(?<!function )\breadThread\(/g)) {
      if (f !== "qa.js" || !within(m.index, region(text, "function openThread(", "\nfunction "))) out.push(`${f}: readThread( at ${m.index}`);
    }
  }
  const lines = src["qa.js"].split("\n");
  lines.forEach((l, i) => {
    if (/(?<!function )\b(send|openThread)\(v,/.test(l) && !/\.on_click\(/.test(l) && !/\.on_click\(/.test(lines[i - 1] ?? "")) out.push(`qa.js:${i + 1}: send(`);
  });
  return out;
}

test("exec runs only in the poll, sendAnswer and the filtered History read, and send() only from an on_click (P3.3)", () => {
  // every script of the mod, so a new view is covered without editing this list
  const src = Object.fromEntries(readdirSync(mod).filter((f) => f.endsWith(".js")).map((f) => [f, readFileSync(join(mod, f), "utf8")]));
  assert.deepEqual(sendRuleViolations(src), []);
  assert.equal(Object.values(src).reduce((n, t) => n + (t.match(EXEC) ?? []).length, 0), 20, "exec( twenty times: update.js updateInit (ah where, the build log's read), main.js rebuildRead (ah where once per pane, the stamp's read; the sidebar's build check), dismiss-model.js teamClose (ah roster close, on a Dismiss click), rename-model.js teamRename (ah roster rename, on a Rename dialog's Save or Clear), target-model.js runnerOf (machine, WSL, this computer, a local install), link-model.js Link (the exec poll, ah where, the service lease; reached from main.js fetch), model.js sendAnswer and readThread, qa-history-model.js historyRead (the ah fallback of a filtered History: a filter change, Load more, the tab's refresh), board-model.js readItemLog (on a card click), settings.js probe (ah where, the CHANGELOG read) and install");
  // the check can fail: a render-time exec, and a send outside a click
  const broken = { ...src, "qa.js": src["qa.js"].replace("const m = qaModel(v.st.slices, now);", "const m = qaModel(v.st.slices, now);\n  exec(\"ah\", []);\n  send(v, null, {}, null);") };
  assert.equal(sendRuleViolations(broken).length, 2);
});

test("a window loads until its first full reply, then stays drawn while a grown sub set catches up", () => {
  assert.equal(loadingOf({ status: "up", ready: false }, true), true, "before the first full reply");
  assert.equal(loadingOf({ status: "up", ready: true, gotKey: "view:summary" }, true), false, "a grown sub set does not blink");
});

test("sidebar rows focus a live orchestrator's registered terminal, a lane's too, never a gone one's, and members by their ah.name label (atp-1259)", () => {
  const top = sidebarModel(slices, NOW).teams.find((t) => t.name === "ah2-tm4");
  const lane = top.lanes.find((l) => l.name === "lane-herdr");
  const row = (t, name) => t.members.find((m) => m.name === name);
  const of = terminalFinder([
    { address: "t1", state: "live", labels: {} },
    { address: "t2", state: "live", labels: { "ah.name": "implementor" } },
    { address: "t7", state: "finished", labels: {} },
  ]);
  assert.equal(of(row(top, "orchestrator")), "t1");
  assert.equal(of(row(top, "implementor")), "t2");
  assert.equal(of(row(lane, "orchestrator")), null, "a terminal that is not live links nothing");
  assert.equal(terminalFinder([{ address: "t7", state: "live" }])(row(lane, "orchestrator")), "t7");
  const gone = structuredClone(reply.slices);
  gone.find((x) => x.id === "team-summary:a1b2c3d4/ah2-tm4").body.member_states["member:a1b2c3d4/ah2-tm4/@orchestrator"].liveness = "proven_dead";
  const stale = sidebarModel(new Map(gone.map((x) => [x.id, x])), NOW).teams.find((t) => t.name === "ah2-tm4");
  assert.equal(of(row(stale, "orchestrator")), null, "a gone orchestrator's row is not clickable");
});

test("a team with waiting items and no pipeline run still gets its For you cards", () => {
  const s = new Map([
    ["list:teams", { id: "list:teams", body: { teams: [{ id: "team:d1/solo", name: "solo", dir_id: "d1" }] } }],
    ["team-summary:d1/solo", { id: "team-summary:d1/solo", body: { name: "solo", running: true, waiting_on_you: 1 } }],
    ["questions:d1/solo", { id: "questions:d1/solo", body: { open: [], recent: [], open_total: 0, waiting_total: 1,
      items: [{ key: "_run:ship-now", item: "ship-now", stream: "_run", title: "Ship now?", text: "Ship now?", options: ["Yes", "No"], since: "2026-11-14T19:48:22Z" }] } }],
  ]);
  const m = qaModel(s, Date.parse("2026-11-14T20:00:00Z"));
  assert.equal(m.count, 1);
  assert.equal(m.teams.length, 1);
  assert.equal(m.teams[0].cards.length, 1);
  assert.equal(m.teams[0].cards[0].kind, "item");
});

test("after a failed poll the retry stays at or under 8 s for the first 2 minutes down, then takes the 16 s and 30 s steps (ah2-122 D5)", () => {
  const steps = [2000, 4000, 8000, 16000, 30000];
  assert.deepEqual([0, 1, 2, 3, 4, 9].map((f) => retryMs(steps, f, 0)), [2000, 4000, 8000, 8000, 8000, 8000]);
  assert.equal(retryMs(steps, 4, QUICK_RETRY_MS - 1), 8000, "the last moment of the quick window");
  // the nearest inputs that must not be held: 2 minutes down exactly, and later
  assert.equal(retryMs(steps, 3, QUICK_RETRY_MS), 16000);
  assert.equal(retryMs(steps, 4, QUICK_RETRY_MS), 30000);
  assert.equal(retryMs(steps, 9, 10 * QUICK_RETRY_MS), 30000);
  assert.equal(retryMs(steps, 0, QUICK_RETRY_MS), 2000, "early steps are not raised");
});
