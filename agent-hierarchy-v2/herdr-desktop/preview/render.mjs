#!/usr/bin/env node
// A picture of the ah2 mod without a Herdr window: `node herdr-desktop/preview/render.mjs [out.html]`.
// The rows come from the mod's own model.js over fixture/events.json; only the drawing is HTML here, mirroring
// main.js's sizes. Colours are Herdr's graphite/paper tokens as herdr-app src/settings.rs resolve() derives them
// (the mod itself reads theme() at run time).
// Open `out.html#dark` or `#light`; `?view=qa|qa2|history|thread|thread-open|thread-later|empty|down` shows the Q&A window in its states instead of the sitrep,
// `?view=board` the Board (board.js) in the Desk window, with change dots and an item sheet. Every window view (qa, board, feed,
// graph) is the Desk window on that tab, headed by the Desk's tab row (tabs.js panelTabsView).
// `?view=feed|feed-dock|feed-empty|feed-down` the feed window (feed-dock at the side column's 420 px).
// `?view=graph` the Graph window (graph-view.js) on ah2-tm4 and its lane: the web's Map, then the links, then the scrubber;
// `graph-30` a 30 min window 90 min back, `graph-two` a second stream, `graph-zoom` zoomed into it; `graph-flow` the same team in Flow
// (graph-model.js flowModel: cards by role, links, legend), `graph-flow-v` Flow vertical, `dock-flow` Flow in the docked column.
// `?view=settings` the settings dialog (settings.js) with an update ready and What's new open; the ah2 tab shows its dot.
// `?view=menu` the strip's … popup (tabs.js) with an update ready; `?view=dock` the desk open on the Graph (dock.js, tabs.js
// panelTabsView). Every view shows the desk's status segment (main.js statusSegment) and the Needs-you drawer peeking
// (main.js peek); `?view=peek` shows the drawer slid out, as on hover.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { tmpdir } from "node:os";
import { STATES, counts, deskModel, sidebarModel, sitrepModel, qaModel, canSend, descFold, historyReply, threadStart } from "../mods/ah2/model.js";
import { descriptionBlocks, inlineParts } from "../mods/ah2/plans-model.js";
import { answerText, DIRECTION, threadOf } from "../mods/ah2/questions-model.js";
import { boardModel, ownerOf } from "../mods/ah2/board-model.js";
import { feedModel } from "../mods/ah2/feed-model.js";
import { graphModel, flowModel, flowPan, KNOB, cardStyle, edgeStyle, dotColor, ageText, sheetUi, paneAct, memberPageLink, laneStyle } from "../mods/ah2/graph-model.js";
import { memberModel, SUMMARY_MESSAGES } from "../mods/ah2/member-model.js";
import { glyphOf } from "../mods/ah2/graph-world.js";
import { DUTY, DUTY_TITLE, stateWord } from "../mods/ah2/select.js";
import { FLOW_COLUMN } from "../../web/mock/model.mjs";
import { settingsOf, STRIP, INTERVALS, changelogEntry } from "../mods/ah2/settings-model.js";

const here = dirname(fileURLToPath(import.meta.url));
const out = process.argv[2] ?? join(tmpdir(), "ah2-preview.html");
const reply = JSON.parse(readFileSync(join(here, "..", "fixture", "events.json"), "utf8"));
const NOW = Date.parse(reply.observed_at);
const slices = new Map(reply.slices.map((s) => [s.id, s]));
const side = sidebarModel(slices, NOW);
const team = "a1b2c3d4/ah2-tm4";
const rep = sitrepModel(slices, team, NOW);
const qa = qaModel(slices, NOW);
const qaEmpty = qaModel(new Map([...slices].filter(([id]) => !id.startsWith("questions:"))), NOW);
// The card states the Q&A views show, as qa.js keeps them (seeded the same way as the herdr-app check copies).
const K = "team:a1b2c3d4/ah2-tm4|20261108-150000-k3vd|";
const IT = "team:a1b2c3d4/ah2-tm4|item|core:atp-1140";
const LOCAL = {
  qa: { [`${K}d8`]: { pick: { picked: [0, 2], other: false, text: "" }, reply: { kind: "refused", text: "open run is ah-run-8f2: this question belongs to a run that has closed" } },
    [IT]: { pick: { picked: [], other: true, text: "" }, input: true } },
  qa2: { [`${K}d7`]: { pick: { picked: [0], other: false, text: "" }, sending: true },
    [`${K}d8`]: { pick: { picked: [0], other: false, text: "" }, reply: { kind: "recorded", note: "x", done: true }, given: "Sidebar" },
    [IT]: { descOpen: true, copied: "herdr-desktop/mods/ah2/qa.js" } },
};
// Questions the user typed in a terminal (dir jim), newest in History: one with no reply yet, and one answered at once and then
// after a long turn, whose row carries its first reply's head (historyReply; the questions lane keeps only the time in `recent`
// today). Its thread, as threadOf reads it, is the History thread view: `thread` folded, `thread-open` with every fold shown.
const DOC = { prompt: { id: "p7", text: "Should the sidebar keep its 268 px width once the foot strip has five views?" }, replies: [
  { id: "dp7", at: NOW - 5 * 60000, part: "direct", trigger: "typed", text: "Yes, I think it can. Let me check the strip's padding against the five views before I say so for sure." },
  { id: "t1", at: NOW - 3 * 60000, trigger: "typed", final: "Yes. The strip fits at 268 px once its buttons pad 6 px; a wider sidebar would take room from the terminal.",
    text: "Yes, I think it can. Let me check the strip's padding against the five views before I say so for sure.\n\nThe strip's buttons pad 8 px today, so five of them need 274 px. At 6 px they need 262 px.\n\nYes. The strip fits at 268 px once its buttons pad 6 px; a wider sidebar would take room from the terminal." },
  { id: "t2", at: NOW - 2 * 60000, trigger: "later", final: "Settings can hide a view if the strip still feels tight.",
    text: "Checked the settings dialog too.\n\nSettings can hide a view if the strip still feels tight." },
] };
const THREAD = threadOf(DOC);
const ASKED = { dir: "jim", question: true, replies: 3, at: NOW - 6 * 60000, age: "6m", team: "ah2-tm4", header: null, text: DOC.prompt.text,
  ...historyReply({ reply_at: THREAD.replyAt, reply: THREAD.reply }) };
// `thread-later`: a question whose own turn wrote no reply (a hand-back opened the next turn), answered in a later one.
const LATER_DOC = { prompt: { id: "p8", text: "Did the nightly run pass?" }, replies: [
  { id: "t3", at: NOW - 4 * 60000, trigger: "later", final: "Yes: the nightly run passed, 412 tests, no flakes.", text: "The reviewer's hand-back came in.\n\nYes: the nightly run passed, 412 tests, no flakes." },
] };
const WAITING = { dir: "jim", question: true, replies: 0, at: NOW - 60000, age: "1m", team: "ah2-tm4", header: null, text: "Does the Board keep its filter row across a restart?", ...historyReply(null) };
// agents() is Herdr's live data, not ah's: one plain codex terminal stands in for it.
const others = [{ name: "Codex", space: "bloom", state: "working" }];

const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
// A member's state mark, as marks.js draws it (Herdr's agents list): working spins Herdr's spinner, running is the done dot in
// the working tone, blocked a ! on its tint, idle a ring, done a dot, gone ×, no signal ?.
const SPIN = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3.2" stroke-linecap="round"><circle cx="12" cy="12" r="9.4" stroke-opacity="0.24"/><path d="M12 2.6a9.4 9.4 0 0 1 9.4 9.4"/></svg>';
const mark = (state) => ({
  working: `<i class="mk spin t-working">${SPIN}</i>`, running: '<i class="mk dot run"></i>', blocked: '<i class="mk bl">!</i>',
  idle: '<i class="mk ring"></i>', done: '<i class="mk dot"></i>',
})[state] ?? `<i class="mk tx">${state === "gone" ? "×" : "?"}</i>`;
const glyph = (s) => `<span class="g" aria-hidden="true">${mark(s.state)}</span>`;
const needs = (n, label = "Needs you") => (n > 0 ? `<span class="need">${label} ${n}</span>` : "");

function member(m, pad) {
  const full = `${m.name} · ${m.word} · ${m.what}${m.age ? ` · ${m.age}` : ""}`;
  const quiet = ["idle", "gone", "unknown"].includes(m.state);
  return `<div class="row" style="padding-left:${pad}px" title="${esc(full)}" aria-label="${esc(full)}">${glyph(m)}<span class="role${quiet ? " quiet" : ""}">${esc(m.name)}</span><span class="what">${esc(m.what)}</span><span class="age">${esc(m.age)}</span></div>`;
}

const alarm = (pad) => (a) => `<div class="alarm" style="margin-left:${pad + 26}px" title="${esc(a.text)}"><b aria-hidden="true">⚠</b><span>${esc(a.text)}</span></div>`;

function teamRows(t, lane) {
  const pad = lane ? 22 : 8;
  const parts = [
    `<button class="team" style="padding-left:${pad}px" title="Open the ${esc(t.name)} sitrep">${lane ? '<span class="dim">↳</span>' : ""}<span class="tn">${esc(t.name)}</span>`,
    t.needsYou ? `<span class="cnt" title="${t.needsYou} needs you">${t.needsYou}</span>` : "",
    t.working || t.blocked ? `<span class="tc">${esc(counts({ working: t.working, blocked: t.blocked }))}</span>` : "",
    `<span class="spill" role="button" title="Open the ${esc(t.name)} sitrep" aria-label="Open the ${esc(t.name)} sitrep">Sitrep</span>`,
    "</button>",
    ...t.alerts.filter((a) => !a.member).map(alarm(pad)),
    ...t.members.map((m) => member(m, pad + 6)),
    ...t.alerts.filter((a) => a.member).map(alarm(pad)),
    ...t.lanes.flatMap((l) => teamRows(l, true)),
  ];
  return parts;
}

function region(r, top) {
  const rows = r.rows.map((x) => x.question
    ? `<div class="q"><div>${esc(x.title)}</div><div class="qrow"><span class="qm">${esc([x.key, x.who].filter(Boolean).join(" · "))}</span><button class="qlink" aria-label="Answer ${esc(x.key)} in Q&amp;A">Answer in Q&amp;A</button></div></div>`
    : `<div class="it" title="${esc(x.title)}"><span class="k${x.done ? " t-done" : ""}">${esc(x.key)}</span><span class="ti">${esc(x.title)}</span><span class="who">${esc(x.who)}</span></div>`);
  return `<section class="reg"${top === undefined ? "" : ` style="margin-top:${top}px"`}><h2>${esc(r.label)} <span class="c">${r.total}</span></h2>${rows.join("") || '<div class="none">None</div>'}${r.more ? `<div class="none">+ ${r.more} more</div>` : ""}</section>`;
}

const asker = (a, tm) => (a ?? "").replace(`@${tm.name}`, "");
// qa.js description(): the item's description under its note, folded to about four lines with More; marks dropped, each code span a copy chip.
function qaDesc(c, cs) {
  if (!c.desc) return "";
  const all = descriptionBlocks(c.desc);
  const fold = descFold(all);
  const plain = (text) => esc(inlineParts(text).map((p) => p.text).join(""));
  const chip = (text, cls = "") => `<button class="copy${cls}" aria-label="${cs.copied === text ? "Copied" : "Copy"}: ${esc(text)}">${cs.copied === text ? "✓ " : ""}${esc(text)}</button>`;
  const block = (b, i) => {
    const codes = b.kind === "code" ? [] : (b.items ?? [b.text]).flatMap((r) => inlineParts(r).filter((p) => p.kind === "code").map((p) => p.text));
    const body = b.kind === "head" ? `<div class="dh${i ? " sp" : ""}">${esc(b.text)}</div>` : b.kind === "code" ? chip(b.text, " blk")
      : b.kind === "para" ? `<div>${plain(b.text)}</div>` : b.items.map((it, j) => `<div class="dl"><span>${b.kind === "olist" ? `${j + 1}.` : "•"}</span><span>${plain(it)}</span></div>`).join("");
    return codes.length ? `<div class="db">${body}<div class="dc">${codes.map((t) => chip(t)).join("")}</div></div>` : body;
  };
  const open = cs.descOpen === true;
  return `<div class="desc">${(open ? all : fold.blocks).map(block).join("")}${fold.cut ? `<button class="nn dmore" aria-expanded="${open}">${open ? "Less" : "More"}</button>` : ""}</div>`;
}
function qaCard(view, tm, c) {
  const cs = { pick: c.start, ...LOCAL[view]?.[c.key] };
  const r = cs.reply;
  if (r?.done) return `<div class="done1"><span class="t-done" aria-hidden="true">✓</span><span>Recorded · ${esc(r.answer ?? cs.given)}</span>${r.note ? '<span class="m">· Orchestrator not told</span>' : ""}</div>`;
  const busy = cs.sending;
  const ok = !busy && canSend(c, cs.pick);
  const who = asker(c.asker, tm);
  const head = c.kind === "item" ? c.q.id : c.header;
  const meta = [tm.name, c.kind === "item" ? `item${who ? ` · ${who}` : ""}` : who, c.multi ? "pick any" : ""].filter(Boolean).join(" · ");
  const chip = (i, text, on, rec) => `<button class="chip${on ? " sel" : ""}"${busy ? " disabled" : ""} aria-pressed="${on}">${on ? '<span class="ck" aria-hidden="true">✓</span>' : ""}${esc(text)}${rec ? '<span class="rec">recommended</span>' : ""}</button>`;
  return `<div class="card${r ? " err" : ""}">
    <div class="meta">${head ? `<span class="mk">${esc(head)}</span>` : ""}<span class="mt">${head ? "· " : ""}${esc(meta)}</span><span class="agec">${esc(c.age)}</span></div>
    <p>${esc(c.text)}</p>
    ${qaDesc(c, cs)}
    <div class="opts">${c.options.map((o, i) => chip(i, o, cs.pick.picked.includes(i), i === c.recommended)).join("")}${chip("other", "Other…", cs.pick.other, false)}</div>
    ${cs.pick.other ? `<div class="inp"><span class="ph">Your answer</span></div>` : ""}
    ${c.more ? `<div class="more">+ ${c.more} more options · answer in the web Inbox</div>` : ""}
    ${r ? `<div class="msg"><b aria-hidden="true">⚠</b><span>${esc(r.text)}</span></div>` : ""}
    <div class="act"><button class="send"${ok ? "" : " disabled"} aria-label="${busy ? "Sending" : `Send${cs.pick.picked.length ? `: ${esc(answerText(c.q, cs.pick))}` : ""}`}"><span>${busy ? "Sending…" : "Send"}</span></button><button class="nn"${busy ? " disabled" : ""}>Not needed ▾</button></div>
  </div>`;
}
const clock = (at) => { const d = new Date(at); return `${String(d.getUTCHours()).padStart(2, "0")}:${String(d.getUTCMinutes()).padStart(2, "0")}`; };
function qaBody(view) {
  if (view === "down") return '<div class="qnote">ahd not running<br><small>Retrying</small></div>';
  if (view === "thread" || view === "thread-open") return threadHtml(DOC, view === "thread-open" ? new Set(["more", "later", "l0"]) : new Set());
  if (view === "thread-later") return threadHtml(LATER_DOC, new Set());
  if (view === "history") return [WAITING, ASKED, ...qa.history].map((r) => {
    const log = r.dir === "jim";
    const by = log ? DIRECTION.jim : r.by === "user" ? "you" : r.by;
    const answer = log ? (r.replyAt ? `replied ${clock(r.replyAt)}` : r.replies > 0 ? `${r.replies} later ${r.replies === 1 ? "reply" : "replies"}` : "no reply yet") : r.answer;
    const quiet = log && !r.replyAt && !(r.replies > 0);
    const full = `${r.team} · ${r.header ? `${r.header} · ` : ""}${log ? `? ${r.text}` : r.text} · ${log ? "" : "answered "}${answer} · ${by}${r.age ? ` · ${r.age} ago` : ""}${log && r.preview ? `. Reply: ${r.preview}` : ""}`;
    // the question and, for a typed one with a reply, its preview as a second muted line in the same cell (qa.js, as main.js's goal line)
    const q = log && r.preview ? `<span class="hq2"><span class="hq">? ${esc(r.text)}</span><span class="hp">${esc(r.preview)}</span></span>` : `<span class="hq">${esc(log ? `? ${r.text}` : r.text)}</span>`;
    const line = `<span class="tm">${clock(r.at)}</span>${log ? `<span class="hk">${esc(r.team)}</span>` : ""}${r.header ? `<span class="hk">${esc(r.header)}</span>` : ""}${q}<span class="achip${quiet ? " quiet" : ""}">${esc(answer)}</span><span class="by">${esc(by)}</span>`;
    if (!log) return `<div class="hr" title="${esc(full)}" aria-label="${esc(full)}">${line}</div>`;
    return `<button class="hr open" title="${esc(full)}" aria-label="${esc(`${full}. Open its thread`)}">${line}</button>`;
  }).join("");
  const m = view === "empty" ? qaEmpty : qa;
  if (m.empty) return '<div class="empty"><b>Nothing waits on you.</b><span>What you answered is under History.</span></div>';
  return m.teams.map((tm) => `<div class="grp">${tm.lane ? '<span class="dim">↳</span> ' : ""}${esc(tm.name)}</div>${tm.cards.map((c) => qaCard(view, tm, c)).join("")}${tm.hidden > 0 || tm.truncated ? `<div class="more">+ ${tm.hidden || ""} more waiting · ah msg decision list --waiting</div>` : ""}`).join("");
}
const hist = (view) => view === "history" || view.startsWith("thread");
// qa.js threadPanel: the question, the reply in full, the final reply when it differs, then the folds. `pressed`: the fold keys
// pressed, each flipping its fold from how threadStart opens it.
function threadHtml(doc, pressed) {
  const th = threadOf(doc);
  const start = threadStart(th);
  const isOpen = (key) => pressed.has(key) !== (key === "later" && start.laterOpen);
  const fold = (key, label) => `<button class="tfold" aria-expanded="${isOpen(key)}" aria-label="${esc(`${label}, ${isOpen(key) ? "shown. Hide it" : "hidden. Show it"}`)}"><span aria-hidden="true">${isOpen(key) ? "▾" : "›"}</span>${esc(label)}</button>`;
  const para = (text) => `<div class="tq">${esc(text)}</div>`;
  const out = ['<button class="tback" aria-label="Back to History">← History</button>', para(doc.prompt.text)];
  if (th.reply) out.push(`<div class="tl">${th.replyFinal ? "Final reply" : "Reply"} ${clock(th.replyAt)}</div>`, para(th.reply));
  else out.push(`<div class="tl">${start.note}</div>`);
  if (th.final) out.push(`<div class="tl">Final reply ${clock(th.finalAt)}</div>`, para(th.final));
  const under = (html) => `<div class="tu">${html}</div>`;
  if (th.more) out.push(fold("more", "More from this turn"), isOpen("more") ? under(para(th.more)) : "");
  if (th.later.length) {
    out.push(fold("later", `Later replies (${th.later.length})`));
    if (isOpen("later")) {
      out.push(under(th.later.map((l, i) => {
        const shown = l.final ?? descFold([{ kind: "para", text: l.text }]).blocks[0]?.text ?? "";
        return `<div class="tl">${clock(l.at)}</div>${para(isOpen(`l${i}`) ? l.text : shown)}${shown.trim() !== l.text.trim() ? fold(`l${i}`, "Whole reply") : ""}`;
      }).join("")));
    }
  }
  return out.join("");
}
const desk = deskModel(slices, NOW);
const ICON = { win: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="2" y="4" width="20" height="16" rx="2"/><path d="M10 4v4"/><path d="M2 8h20"/><path d="M6 4v4"/></svg>',
  dock: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect width="18" height="18" x="3" y="3" rx="2"/><path d="M15 3v18"/></svg>' };
const deskTab = (label, on, n = 0) => `<button role="tab" class="${on ? "on" : ""}" aria-selected="${on}" aria-label="${on ? `${label}, shown` : `Show ${label}`}${n ? `. ${n} waiting on you` : ""}">${label}${n ? `<span class="cnt">${n}</span>` : ""}</button>`;
// The Desk's tab row (tabs.js panelTabsView): `win` draws the Desk window's (44 px, dock button, ×), else the column's (⧉, ×).
const deskTabs = (on, win) => `<div class="dtabs${win ? " dwin" : ""}" role="tablist" aria-label="Desk">${[["qa", "Q&amp;A"], ["board", "Board"], ["feed", "Feed"], ["graph", "Graph"]].map(([v, l]) => deskTab(l, v === on, v === "qa" ? desk.waiting : 0)).join("")}<span class="fill"></span>${win
  ? `<button class="x" title="Dock the desk to the side" aria-label="Dock the desk to the side">${ICON.dock}</button><button class="x" title="Close the desk (esc)" aria-label="Close the desk (esc)">×</button>`
  : `<button class="x" title="Open the desk in a window" aria-label="Open the desk in a window">${ICON.win}</button><button class="x" title="Close the desk" aria-label="Close the desk">×</button>`}</div>`;
const qaWin = (view) => {
  const n = view === "down" ? 0 : (view === "empty" ? qaEmpty : qa).count;
  return `<div class="ov qa" role="dialog" aria-label="Q&amp;A">
  ${deskTabs("qa", true)}
  <div class="vt" role="tablist"><button role="tab" class="${hist(view) ? "" : "on"}">For you${n ? ` ${n}` : ""}</button><button role="tab" class="${hist(view) ? "on" : ""}">History</button></div>
  <div class="ovb">${qaBody(view)}</div>
 </div>`;
};
const VIEWS = ["qa", "qa2", "history", "thread", "thread-open", "thread-later", "empty", "down"];
// The Board tab, as board.js draws it in a Herdr tab: the filter row, the web's columns, compact Q&A cards in Waiting on you, change
// dots (b338 moved and atp-1150 new since the board opened) and the item sheet open on b338 with a sample log.
const seen = new Map();
boardModel(slices, NOW, { seen });
const kbWeb = slices.get("plan-items:a1b2c3d4/ah2-tm4/web"), kbCore = slices.get("plan-items:a1b2c3d4/ah2-tm4/core");
const kbSlices = new Map(slices)
  .set(kbWeb.id, { ...kbWeb, body: { ...kbWeb.body, items: kbWeb.body.items.map((i) => (i.key === "web:b338" ? { ...i, state: "in_review", column: "review" } : i)) } })
  .set(kbCore.id, { ...kbCore, body: { ...kbCore.body, items: [...kbCore.body.items, { key: "core:atp-1150", title: "Board filter row keeps its chips", state: "next", column: "next", owner: "" }] } });
const board = boardModel(kbSlices, NOW, { seen });
const kbSel = board.columns.flatMap((c) => c.cards).find((c) => c.id === "b338").sel;
const kbOwner = ownerOf(kbSlices, kbSel, NOW);
const kbLog = [{ at: NOW - 4 * 60000, text: "in progress → in review", by: "ux-engineer" }, { at: NOW - 52 * 60000, text: "next → in progress", by: "orchestrator" }, { at: NOW - 70 * 60000, text: "added", by: "orchestrator" }];
const kbItem = (c) => {
  const who = [c.team, c.owner].filter(Boolean).join(" · ");
  const full = [c.title, c.ref, who, c.blocked, c.mark].filter(Boolean).join(" · ");
  const on = c.key === kbSel.key;
  const tag = c.sel && !c.ask ? "button" : "div";
  return `<${tag} class="kbc${on ? " on" : ""}" title="${esc(full)}" aria-label="${esc(full)}"><div class="kbt"><span>${esc(c.title)}</span>${c.mark ? '<i class="kdot"></i>' : ""}</div><div class="kbm">${c.done ? '<span class="t-done" aria-hidden="true">✓</span>' : ""}${c.id ? `<span class="kid">${esc(c.id)}</span>` : ""}<span class="kw">${esc(who)}</span></div>${c.blocked ? `<div class="kbf"><b aria-hidden="true">▲</b><span>${esc(c.blocked)}</span></div>` : ""}${c.ask ? `<div><button class="qlink" aria-label="Answer ${esc(c.id || "this")} in Q&amp;A">Answer in Q&amp;A</button></div>` : ""}</${tag}>`;
};
// qa.js card(…, compact): the question, the pick as one chip, Send and More…; a pick that cannot be sent as it stands opens Q&A.
function kbAsk(tm, c) {
  const who = asker(c.asker, tm);
  const head = c.kind === "item" ? c.q.id : c.header;
  const meta = [tm.name, c.kind === "item" ? `item${who ? ` · ${who}` : ""}` : who, c.multi ? "pick any" : ""].filter(Boolean).join(" · ");
  const ok = canSend(c, c.start);
  const picked = c.start.picked.map((i) => c.options[i]).join(", ");
  const n = c.options.length + (c.more ?? 0);
  return `<div class="card kq"><div class="meta">${head ? `<span class="mk" style="max-width:96px">${esc(head)}</span>` : ""}<span class="mt">${head ? "· " : ""}${esc(meta)}</span><span class="agec">${esc(c.age)}</span></div><p>${esc(c.text)}</p>
    <div class="act">${ok ? `<span class="chip sel kpick"><span class="ck" aria-hidden="true">✓</span><span class="kpt">${esc(picked || "Other")}</span></span><button class="send"><span>Send</span></button><button class="nn">More…</button>` : `<button class="nn">${n} options · Answer in Q&amp;A</button>`}</div></div>`;
}
const kbChip = (text, on) => `<button class="chip kf${on ? " sel" : ""}" aria-pressed="${on}">${on ? '<span class="ck" aria-hidden="true">✓</span>' : ""}${esc(text)}</button>`;
const boardWin = `<div class="ov board" role="region" aria-label="Board">
  ${deskTabs("board", true)}
  <div class="kbw"><div class="kbl">
   <div class="kbr">${kbChip("Blocked only", false)}${kbChip("Changes only", false)}<button class="chip kf">Owner: any ▾</button><span class="sep"></span>${board.teams.map((tm) => kbChip(`${tm.lane ? "↳ " : ""}${tm.name}`, tm.on)).join("")}</div>
   <div class="kb">${board.columns.map((col) => `<section class="kbcol${col.key === "waiting" ? " wide" : ""}" aria-label="${esc(col.label)}, ${col.count} cards"><h2>${esc(col.label)} <span class="c">${col.count}</span></h2><div class="kbcards">${col.cards.map((c) => (c.qa ? kbAsk(c.tm, c.qa) : kbItem(c))).join("") || '<div class="none">None</div>'}${col.more ? `<div class="none">+ ${col.more} more</div>` : ""}</div></section>`).join("")}</div>
  </div>
  <aside class="kbs" aria-label="Item sheet"><div class="kbsh"><div class="kbsm"><span class="kid" style="color:var(--foreground)">${esc(kbSel.item)}</span><span class="kw">· ${esc(kbSel.team)} · ${esc(kbSel.stream)}</span><button class="x" aria-label="Close the item sheet">×</button></div>
   <div class="kbst">${esc(kbSel.title)}</div>
   <div class="kbso">${kbOwner ? `<span class="g">${mark(kbOwner.state)}</span>` : ""}<b>${esc(kbSel.owner)}</b>${kbOwner ? `<span>${esc([kbOwner.word.toLowerCase(), kbOwner.age].filter(Boolean).join(" · "))}</span>` : ""}</div>
   <div><button class="sb"${kbOwner ? "" : ` disabled style="color:var(--muted)" title="No live terminal for ${esc(kbSel.owner)}"`}>Focus terminal</button></div></div>
   <div class="kblog"><div class="kbll">Log</div>${kbLog.map((r) => `<div class="kblr"><span class="tm">${clock(r.at)}</span><span class="kx">${esc(r.text)}</span><span class="kby">${esc(r.by)}</span></div>`).join("")}</div></aside></div>
 </div>`;
// The feed (feed.js), from the mod's own feed-model.js over the fixture: last looked 10 minutes ago, one member state change seen
// while open, the feed-view brief hovered (Focus terminal). A row "has a terminal" here when its member serves a pane.
const SEEN = [{ key: "seen|preview", at: NOW - 30000, dir: "a1b2c3d4", team: "ah2-tm4", name: "reviewer", from: "working", to: "blocked" }];
const feed = feedModel(slices, NOW, { looked: NOW - 10 * 60000, seen: SEEN });
const HOVER = "exchange:a1b2c3d4/20261108-171500-q2ka";
const KINDS_ON = ["Messages", "Plan", "Alerts", "Questions", "Members"];
const feedEv = (r) => {
  const tag = r.mb?.pane ? "button" : "div";
  const full = [r.clock, r.title, r.sub, r.team, r.age && `${r.age} ago`].filter(Boolean).join(" · ");
  const g = r.mark ? glyph({ state: r.mark }) : `<span class="g fg t-${r.tone}" aria-hidden="true">${esc(r.glyph)}</span>`;
  const right = r.key === HOVER ? '<span class="fgo">Focus terminal</span>' : `${r.lane ? '<span class="dim">↳</span>' : ""}<span class="fte">${esc(r.team)}</span>`;
  return `<${tag} class="fe${r.key === HOVER ? " hov" : ""}" title="${esc(full)}" aria-label="${esc(full)}${tag === "button" ? ". Focus its terminal" : ""}">${g}<span class="ft">${esc(r.title)}</span><span class="fs">${esc(r.sub)}</span><span class="fr">${right}</span></${tag}>`;
};
const feedNow = () => `<div class="fnow"><div class="fnh"><span>Now</span><span>${counts({ working: feed.now.working, blocked: feed.now.blocked })}</span></div>${feed.now.rows.map((m) => `<${m.pane ? "button" : "div"} class="fe fn">${glyph(m)}<span class="fw">${esc(m.name)}</span><span class="fs">${esc(m.what)}</span><span class="age">${esc(m.age)}</span><span class="fr"><span class="fte">${esc(m.team)}</span></span></${m.pane ? "button" : "div"}>`).join("")}</div>`;
const feedChips = () => `<div class="fch"><button class="chip">All teams</button>${KINDS_ON.map((k) => `<button class="chip sel"><span class="ck" aria-hidden="true">✓</span>${k}</button>`).join("")}<button class="chip off">Internal</button></div>`;
const feedBody = (v) => (v === "feed-down" ? '<div class="qnote">ahd not running<br><small>Retrying</small></div>'
  : v === "feed-empty" ? '<div class="qnote">Nothing has happened yet.<br><small>Briefs, reports, plan moves, alerts and questions show here as they happen.</small></div>'
  : feed.groups.map((g) => (g.divider ? `<div class="fnew"><i></i>${g.divider} new since you looked<i></i></div>`
    : `<div class="fmin"><span class="tm">${esc(g.clock)}</span><div class="frows">${g.rows.map(feedEv).join("")}</div></div>`)).join(""));
const FEEDS = ["feed", "feed-dock", "feed-empty", "feed-down"];
const feedWin = (v) => `<div class="ov fd${v === "feed-dock" ? " fdk" : ""}" data-v="${v}" role="dialog" aria-label="Feed">
  ${deskTabs("feed", v !== "feed-dock")}
  ${v === "feed" || v === "feed-dock" ? feedNow() + feedChips() : ""}<div class="ovb fb">${feedBody(v)}</div>
 </div>`;

// The Graph window, mirroring graph-view.js's sizes: the map from graph-model.js mapOf, arcs and rings as SVG where Herdr paints paths,
// and the scrubber under the maps. GRAPHS: the window live, a 30 min window 90 min back, a zoom (the fixture with the architect moved
// to a second stream, since every fixture team has one), and the docked column (dock.js) live.
const r1 = (v) => Math.round(v * 10) / 10;
const pathD = (cmds) => cmds.map((c) => c[0] + c.slice(1).map(r1).join(",")).join("");
const ringD = (c, r, share) => {
  const at = (f) => `${r1(c + r * Math.sin(f * 2 * Math.PI))},${r1(c - r * Math.cos(f * 2 * Math.PI))}`;
  return share >= 0.999 ? `M${at(0)}A${r1(r)},${r1(r)} 0 0 1 ${at(0.5)}A${r1(r)},${r1(r)} 0 0 1 ${at(0.999)}` : `M${at(0)}A${r1(r)},${r1(r)} 0 ${share > 0.5 ? 1 : 0} 1 ${at(share)}`;
};
function graphMap(g) {
  const m = g.map;
  const px = (v) => `${r1(v)}px`;
  const ctrs = m.containers.map((c) => `<${c.zoom ? "button" : "div"} class="gc${c.zoom ? " zm" : ""}" style="left:${px(c.x - c.r)};top:${px(c.y - c.r)};width:${px(2 * c.r)};height:${px(2 * c.r)}" title="${esc(c.zoom ? `${c.title}. Click to zoom in` : c.title)}"${c.zoom ? ` aria-label="Zoom into ${esc(c.title)}"` : ""}></${c.zoom ? "button" : "div"}>`);
  const arcs = `<svg class="garcs" width="${m.size}" height="${m.size}" aria-hidden="true">${m.arcs.map((a) => `<path class="${a.kind === 1 ? "rep" : ""}" d="${pathD(a.cmds)}" stroke-width="${r1(a.width)}"${a.fade === null ? "" : ` style="stroke-opacity:${a.fade}"`}/>`).join("")}</svg>`;
  const leaves = m.leaves.map((n) => {
    const full = n.full;
    const w = Math.round(n.r * 1.62);
    const across = Math.round(n.r * 2 - 8);
    const tight = n.name.length * 6.2 + 18 > across;
    const ctx = n.ctx ? `<span class="gl val" style="max-width:${w}px">${esc(n.ctx)}</span>` : "";
    const tiers = [
      n.tier >= 2 && tight ? `<span class="gl">${mark(n.state)} ${ctx}</span>` : n.tier >= 1 ? ctx : "",
      n.tier >= 2 ? `<span class="gl" style="max-width:${tight ? Math.round(n.r * 2) : across}px">${tight ? "" : `${mark(n.state)} `}${esc(n.name)}</span>` : "",
      n.tier >= 3 && n.role ? `<span class="gl sub" style="max-width:${w}px">${esc(n.role)}</span>` : "",
      n.tier >= 4 ? `<span class="gl sub" style="max-width:${w}px">${esc([n.model, n.word.toLowerCase()].filter(Boolean).join(" · "))}</span>` : "",
      n.tier >= 5 ? `<span class="gl sub" style="max-width:${w}px">${esc(n.item || "no item")}</span>` : "",
    ].join("");
    return `<div class="gm${n.state === "gone" ? " gone" : ""}" style="left:${px(n.x - n.r)};top:${px(n.y - n.r)};width:${px(2 * n.r)};height:${px(2 * n.r)}" title="${esc(full)}" aria-label="${esc(full)}"><svg width="${r1(2 * n.r)}" height="${r1(2 * n.r)}" aria-hidden="true"><path class="trk" d="${ringD(n.r, n.r * 0.86, 1)}" stroke-width="${r1(n.r * 0.1)}"/><path class="busy t-${n.tone}" d="${ringD(n.r, n.r * 0.86, Math.max(0.04, n.share ?? 0))}" stroke-width="${r1(n.r * 0.1)}"/></svg><div class="gls">${tiers}</div></div>`;
  });
  const flights = m.flights.map((f) => `<i class="gfl" style="left:${px(f.x - 4)};top:${px(f.y - 4)}" title="${esc(f.title)}" aria-label="${esc(f.title)}"></i>`);
  const labels = m.containers.filter((c) => c.label).map((c) => `<div class="gcl" style="left:${px(c.x - c.r)};top:${px(c.y - c.r - 8)};width:${px(2 * c.r)}"><span style="max-width:${px(c.r * 1.6)}">${esc(c.name)}</span></div>`);
  return `<div class="gmap-w" style="padding-left:${g.depth * 14}px"><div class="gmap" style="width:${m.size}px;height:${m.size}px">${[...ctrs, arcs, ...leaves, ...flights, ...labels].join("")}</div></div>`;
}
function graphGroup(g, docked) {
  const pad = g.depth * 14;
  const quiet = (text) => `<div class="none" style="padding-left:${pad}px">${esc(text)}</div>`;
  const label = (text, n) => `<h2 style="padding-left:${pad}px">${esc(text)} <span class="c">${n}</span></h2>`;
  const bw = docked ? 32 : 48;
  const lnk = (l) => {
    const fill = l.n > 0 ? Math.max(2, Math.round((bw * l.n) / g.max)) : 0;
    return `<div class="gr-row" style="padding-left:${pad}px" title="${esc(l.title)}"><span class="gn">${esc(l.a)}</span><span class="ga" aria-hidden="true">${l.arrow}</span><span class="gn">${esc(l.b)}</span><span class="gf">${esc(l.inFlight)}</span><span class="bar" style="width:${bw}px"><i style="width:${fill}px"></i></span><span class="age">${l.n}</span></div>`;
  };
  const head = g.depth > 0 ? `<div class="gr-lane" style="padding-left:${pad - 14}px"><span class="dim">↳</span> ${esc(g.name)}</div>` : "";
  return {
    map: head + (g.members.length ? graphMap(g) : label("Members", 0) + quiet("None")),
    links: (docked ? head : "") + [
      label(g.windowed ? "Links in the window" : g.run ? "Links" : "Links, last hour", g.links.length),
      g.links.length ? g.links.map(lnk).join("") : quiet(g.windowed ? "No messages in the window." : g.run ? "No messages yet this run." : "No open run."),
      g.overflow ? quiet(`+ ${g.overflow} more between other members`) : "",
    ].join(""),
  };
}
function graphScrub(sc) {
  const per = sc.per > 1 ? `messages per ${sc.per} min` : "messages per minute";
  const bars = sc.bars.map((b) => `<span class="${b.in && sc.win !== "run" ? "in" : ""}"><i style="height:${b.n ? Math.max(2, Math.round((40 * b.n) / sc.peak)) : 0}px" class="${b.in ? "on" : ""}"></i></span>`).join("");
  const knob = (100 * sc.knob) / KNOB;
  return `<div class="scrub" role="group" aria-label="Message history">
    <div class="sr1"><div class="vt sw" role="tablist">${sc.wins.map((w) => `<button role="tab" class="${w.id === sc.win ? "on" : ""}" aria-label="${w.label} window">${w.label}</button>`).join("")}</div>
      <span class="rd" title="${esc([sc.range, sc.say].filter(Boolean).join(" · "))}">${esc(sc.say)}</span>${sc.live ? "" : '<button class="now" title="Back to now (end)">Now</button>'}</div>
    <div class="trk2" role="slider" tabindex="0" aria-label="Time, ${per}: ${sc.at}. Left and right arrows step a minute." aria-valuetext="${sc.at}">${bars}<i class="knob" style="left:${r1(knob)}%"></i></div>
    <div class="ax"><span>${sc.from}</span><span>${per}</span><span>${sc.to}</span></div></div>`;
}
const graphOf = (s, ui, docked) => graphModel(s, team, docked ? (d) => (d === 0 ? 300 : 260) : 540, ui, NOW);
const twoStreams = new Map(slices);
const ARCH = "member:a1b2c3d4/ah2-tm4/architect";
twoStreams.set(ARCH, { ...twoStreams.get(ARCH), body: { ...twoStreams.get(ARCH).body, placement: { stream: "design" } } });
const GRAPHS = {
  graph: graphOf(slices, {}),
  "graph-30": graphOf(slices, { graphWindow: "30", graphCursor: NOW - 90 * 60000 }),
  "graph-two": graphOf(twoStreams, {}),
  "graph-zoom": graphOf(twoStreams, { graphZoom: { team, node: "stream:main" } }),
};
const gm = GRAPHS.graph;
const graphBody = (m, docked) => {
  const parts = m.groups.map((g) => graphGroup(g, docked));
  return docked ? parts.map((p) => p.map).join("") + parts.map((p) => p.links).join("") : parts.map((p) => p.map + p.links).join("");
};
const crumbOf = (c) => (c ? `<nav class="crumb" aria-label="Zoomed into ${esc(c.join(", "))}">${c.map((n, i) => (i === c.length - 1 ? `<b>${esc(n)}</b>` : `<button title="Back to all of ${esc(c[0])} (esc)">${esc(n)}</button><span class="dim">›</span>`)).join("")}</nav>` : "");
const gmDock = graphOf(slices, {}, true);
const dockCol = `<aside class="dock" aria-label="Wrangl desk">
  ${deskTabs("graph", false)}
  <div class="vt" role="tablist">${gmDock.tabs.map((t) => `<button role="tab" class="${t.id === gmDock.id ? "on" : ""}">${esc(t.name)}</button>`).join("")}</div>
  ${crumbOf(gmDock.crumb)}<div class="ovb reg">${graphBody(gmDock, true)}</div>${graphScrub(gmDock.scrub)}
 </aside>`;
const ev = desk.newest;
const evAge = ev ? (Math.floor((NOW - ev.at) / 60000) < 1 ? "now" : `${Math.floor((NOW - ev.at) / 60000)}m`) : "";
const statusRow = `<div class="srow2"><span class="ws">my-repo · ⎇ main</span><div class="seg2" aria-label="Wrangl status">
  ${desk.working ? `<span class="st2">${mark("running")}<b>${desk.working}</b>working</span>` : ""}${desk.blocked ? `<span class="st2">${mark("blocked")}<b>${desk.blocked}</b>blocked</span>` : ""}${needs(desk.needsYou)}
  ${ev ? `<span class="ev2" title="${esc(`${ev.team} · ${ev.text} · ${evAge}`)}"><i>${evAge}</i><span>${esc(ev.text)}</span></span>` : ""}</div></div>`;
const drawer = `<aside class="peek" aria-label="Needs you"><div class="pin"><button class="pr"><span class="pt"><b>Needs you</b><small>Open Q&amp;A</small></span><span class="pb">${desk.needsYou}</span></button>${desk.needing.map((tm) => `<button class="pr" title="${esc(tm.name)} · ${tm.n} need you"><span class="pt">${esc(tm.name)}</span><span class="pc"><span class="pb s">${tm.n}</span></span></button>`).join("")}</div></aside>`;
const graphWin = Object.entries(GRAPHS).map(([v, m]) => `<div class="ov gr" data-v="${v}" role="dialog" aria-label="Graph">
  ${deskTabs("graph", true)}
  <div class="vt" role="tablist">${m.tabs.map((t) => `<button role="tab" class="${t.id === m.id ? "on" : ""}" title="Show the ${esc(t.name)} graph">${esc(t.name)}</button>`).join("")}</div>
  ${crumbOf(m.crumb)}<div class="ovb reg">${graphBody(m, false)}</div>${graphScrub(m.scrub)}
 </div>`).join("");

// Flow, mirroring graph-view.js flowBody: the room is the body as graph-view.js measure() takes it (the Desk window 620 × 720, the
// docked column 420 wide), so the stage, the fit and the orientation are the mod's own.
const flowSvg = (cmds) => cmds.map((c) => c.join(" ")).join(" ");
function flowStage(m) {
  if (m.cards.length === 0) return '<h2>Members <span class="c">0</span></h2><div class="none">None</div>';
  const k = m.k;
  const px = (n) => `${r1(n)}px`;
  const fz = (n) => `font-size:${r1(n * k)}px`;
  const bands = m.bands.map((b) => `<div class="fb" style="left:${px(b.x)};top:${px(b.y)};width:${px(b.w)};height:${px(b.h)}"><span style="${fz(11)};max-width:${px(Math.max(0, b.w - 16))}">${esc(b.name)}</span></div>`);
  const links = `<svg class="flk" width="${r1(m.size.w)}" height="${r1(m.size.h)}" aria-hidden="true">${m.links.map((l) => `<path class="${l.kind}" d="${flowSvg(l.cmds)}" stroke-width="${r1(Math.max(1, edgeStyle(l.kind, false).width * k))}"/>`).join("")}</svg>`;
  // V10: lane bands, headers in the lane's colour, a 2 px rule under each (graph-view.js flowBody)
  const lv = (i, n) => ["l", "d"].map((th) => `--${n}-${th}:${laneStyle(i, th === "l")[n === "lb" ? "band" : "color"]}`).join(";");
  const lanes = (m.lanes ?? []).map((l) => `<i class="fln" style="left:${px(l.x - 8 * k)};top:${px(l.y - 4 * k)};width:${px(Math.max(0, l.w + 16 * k))};height:${px(Math.max(0, l.h))};${lv(l.i, "lb")}"></i>`);
  const labels = m.labels.map((l, i) => `<span class="fl" style="left:${px(l.x)};top:${px(l.y)};${fz(11)};${lv(i, "lc")}">${esc(l.text.toUpperCase())}</span><i class="flr" style="left:${px(l.x)};top:${px(l.y + 16 * k)};width:${px(m.lanes?.[i]?.rule ?? 0)};${lv(i, "lc")}"></i>`);
  // the web's colours per theme, as graph-view.js picks them from the surface's lightness (preview tokens: dark #17191d, light #ffffff)
  const vars = (pick) => ["d", "l"].map((th) => Object.entries(pick(th === "l")).filter(([, v]) => v).map(([n, v]) => `--${n}-${th}:${v}`).join(";")).filter(Boolean).join(";");
  const cards = m.cards.map((c) => {
    const s = c.card;
    const reviewing = s.role === "reviewer" && s.state === "working";
    const word = reviewing ? "reviewing" : stateWord(s.state, s.running);
    const duty = DUTY[s.duty] ?? null;
    const ago = s.lastAt > 0 ? ageText(NOW - s.lastAt, m.dots.length > 0) : "";
    const tag = c.active ? (reviewing ? "reviewing" : s.running ? "running" : s.tool ?? s.model) : s.state !== "idle" ? stateWord(s.state).toLowerCase() : !duty && ago ? ago : null;
    const cell = (v, u) => `<span><b>${esc(String(v))}</b>${u ? ` ${u}` : ""}</span>`;
    const stats = c.active ? [s.orchestrator ? cell(c.out, "out") : "", s.orchestrator && s.decisions !== null ? cell(s.decisions, "dec") : "",
      s.role === "implementor" && s.round ? cell(s.round) : "", ago ? `<span>active <b>${esc(ago)}</b></span>` : ""].filter(Boolean) : [];
    const st = (lt) => cardStyle(s, c.active, lt, lt ? "#ffffff" : "#17191d");
    const look = vars((lt) => { const x = st(lt); return { cf: x.fill, cl: x.line, cn: x.name, cm: x.mark, ct: x.tag }; });
    const pad = (y) => `padding:${r1(y * k)}px ${r1(10 * k)}px;gap:${r1(8 * k)}px`;
    const head = `<div class="fh${c.active ? " on" : ""}" style="${pad(8)}"><span class="fg" style="${fz(12)}">${st(false).glyph}</span><span class="fn${st(false).bold ? " b" : ""}" style="${fz(12.5)}">${esc(s.name)}</span>${duty ? `<span class="fx" style="${fz(11)}">${esc(duty)}</span>` : ""}${tag ? `<span class="fx ft" style="${fz(11)}">${esc(tag)}</span>` : ""}</div>`;
    return `<div class="fc${c.active ? " on" : ""}${s.state === "gone" ? " gone" : ""}" style="left:${px(c.rect.x)};top:${px(c.rect.y)};width:${px(c.rect.w)};height:${px(c.rect.h)};${look}" title="${esc([s.full, s.role, word, ago ? `active ${ago}` : null].filter(Boolean).join(" · "))}" aria-label="${esc(`${s.name}, ${duty ? `${DUTY_TITLE[s.duty]}, ` : ""}${word}`)}">${head}${stats.length ? `<div class="fs" style="${pad(6).replace(/gap:[^;]+/, `gap:${r1(12 * k)}px`)};${fz(11)}">${stats.join("")}</div>` : ""}</div>`;
  });
  // the dots, still at the midpoint here (the mod moves them on its clock)
  const dots = m.dots.map((d) => `<i class="gfl fdt" style="left:${px(d.x - 4)};top:${px(d.y - 4)};${vars((lt) => ({ cd: dotColor(d.review, lt) }))}" title="${esc(d.title)}" aria-label="${esc(d.title)}"></i>`);
  const pan = flowPan(null, m.size, m.stage);
  return `<div class="gflow" style="width:${m.stage.w}px;height:${m.stage.h}px" aria-label="${esc(m.said)}"><div class="fin" style="left:${px(pan.x)};top:${px(pan.y)};width:${px(m.size.w)};height:${px(m.size.h)}">${[...lanes, ...bands, links, ...labels, ...cards, ...dots].join("")}</div>${m.rest > 0 ? `<span class="fmore">+${m.rest} more briefs</span>` : ""}</div>
  <div class="flg" role="group" aria-label="Flow legend"><span><i class="kb"></i>brief in flight</span><span><svg width="14" height="2" aria-hidden="true"><path d="M 0 1 L 14 1"/></svg>sent for review</span><span><i class="ke"></i>reporting line</span><span class="fd2">Idle members are one line; a time is the last activity</span></div>`;
}
// The agent's sheet, mirroring graph-view.js agentSheet (Flow: no Map extras). The preview has no ahd port: Open page is disabled.
const APPW = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="2" y="4" width="20" height="16" rx="2"/><path d="M10 4v4M2 8h20M6 4v4"/></svg>';
const XIC = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12"/></svg>';
function sheetHtml(sl, team, id, live, pane, full) {
  const mm = memberModel(sl, sheetUi({ graphTeam: team }), id, SUMMARY_MESSAGES);
  const since = (ms) => ageText(NOW - ms, live).replace(" ago", "");
  const go = paneAct(pane, mm.name, mm.gone);
  const page = memberPageLink(null, id);
  const row = (k, val, mono) => `<div class="shr"><span class="shk">${esc(k)}</span><span class="shv${mono ? " mono" : ""}">${esc(val)}</span></div>`;
  const now = [mm.tool ? row("tool", `${mm.tool}${mm.toolAt ? ` · ${since(Date.parse(mm.toolAt))}` : ""}`) : "", mm.item ? row("item", `${mm.item.key}${mm.item.title ? ` · ${mm.item.title}` : ""}`) : "",
    mm.branch ? row("branch", mm.branch) : "", mm.workRoot ? row("worktree", mm.workRoot, true) : ""].join("");
  const msgs = mm.rows.length === 0 ? '<div class="shq">No messages yet.</div>' : mm.rows.map((r) => `<div class="shm"><span class="mono t">${esc(r.time)}</span><span class="k">${r.kind}</span><span class="b"><span>${esc(`${r.from} → ${r.to}`)}</span>${r.item ? `<span class="i">${esc(r.item)}</span>` : ""}</span></div>`).join("");
  const where = [`team ${mm.team}`, mm.lane ? `lane ${mm.lane.name}` : null, mm.lead ? `takes direction from ${mm.lead.name}` : null].filter(Boolean).join(" · ");
  return `<aside class="sheet${full ? " full" : ""}" aria-label="Member ${esc(mm.name)}"><div class="shh">
    <div class="sht"><b>${esc(mm.name)}</b><button class="shi${go.why ? " off" : " fo"}" title="${esc(go.why ?? "Go to pane")}" aria-label="${esc(go.why ?? "Go to pane")}"${go.why ? " disabled" : ""}>${APPW}</button><button class="shi" title="Close the details (esc)" aria-label="Close the details (esc)">${XIC}</button></div>
    <div class="shs">${esc(mm.role || "member")} · ${mm.model ? esc(mm.model) : '<span class="dim2">model not reported</span>'}</div>
    <div class="shs">${glyphOf(mm.running ? "working" : mm.state)} ${esc(stateWord(mm.state, mm.running))}${mm.since > 0 ? ` <span class="dim2">since ${esc(since(mm.since))}</span>` : ""}</div>
    <div class="shs dim2">${esc(where)}</div></div>
    <div class="shb"><div class="shn">Now</div>${now}<div class="shn">Latest messages</div>${msgs}
    <button class="shp" disabled title="${esc(page.why)}">Open page</button></div></aside>`;
}
const modes = (m) => `<div class="vt gmd" role="tablist" aria-label="Graph layout"><button role="tab" aria-label="Map layout" title="Show the Map">Map</button><button role="tab" class="on" aria-label="Flow layout" title="Show the Flow">Flow</button></div><button class="ori" title="${m.orient === "vertical" ? "Vertical ⇄ Horizontal" : "Horizontal ⇄ Vertical"}" aria-label="Switch between horizontal and vertical">⇄</button><button class="ori" title="Open in browser" aria-label="Open in browser">↗</button>`;
const flowHead = (m) => `<div class="fhd"><div class="vt" role="tablist">${m.tabs.map((t) => `<button role="tab" class="${t.id === m.id ? "on" : ""}" title="Show the ${esc(t.name)} graph">${esc(t.name)}</button>`).join("")}</div>${modes(m)}</div>`;
// The Desk window's graph body: 620 − 2 border − 32 padding across; 720 less the Desk's tab row (44), the header (34), padding and chip.
const ROOM = { w: 586, h: 720 - 2 - 44 - 34 - 12 - 12 - 8 };
// The fixture's member slices carry no column (the daemon serves one); here each takes the column the mock gives its role, so the
// cards spread over the four columns as they do live.
const flowSlices = new Map([...slices].map(([id, sl]) => [id, id.startsWith("member:") ? { ...sl, body: { ...sl.body, column: FLOW_COLUMN[sl.body.role] ?? 2 } } : sl]));
// The team in the user's screenshot of 2026-11-17 (spec herdr-graph-polish): an orchestrator in Agent, three implementors (one idle),
// three reviewers (one reviewing), briefs from the orchestrator to two implementors and one sent for review. `?view=graph-shot`.
const SHOT = "a1b2c3d4/ah2-shot";
const shotOf = (blocked = null, gone = null) => {
  const m = (n) => `member:${SHOT}/${n}`;
  const who = [["@orchestrator", "orchestrator", "working", "Agent", 180], ["implementor", "implementor", "idle", null, 180], ["implementor-2", "implementor", "working", "Bash", 8],
    ["implementor-3", "implementor", blocked === "implementor-3" ? "blocked" : "working", "Bash", 4], ["reviewer", "reviewer", "working", null, 29], ["reviewer-2", "reviewer", "idle", null, 0], ["reviewer-3", "reviewer", "idle", null, 0]];
  const at = new Date(NOW).toISOString();
  const out = new Map(flowSlices);
  const teams = out.get("list:teams");
  out.set("list:teams", { ...teams, body: { teams: [...teams.body.teams, { id: `team:${SHOT}`, name: "ah2-shot", dir_id: "a1b2c3d4" }] } });
  out.set(`team:${SHOT}`, { id: `team:${SHOT}`, as_of: NOW, body: { name: "ah2-shot", dir_id: "a1b2c3d4", orchestrator: m("@orchestrator"), members: who.slice(1).map((w) => m(w[0])), worktrees: [] } });
  out.set(`team-summary:${SHOT}`, { id: `team-summary:${SHOT}`, as_of: NOW, body: { team: `team:${SHOT}`, name: "ah2", running: true, stale_ids: [],
    member_states: Object.fromEntries(who.map((w) => [m(w[0]), { state: w[2], liveness: "live", process: "running", tool: "claude", state_at: at }])) } });
  for (const [n, role, , tool, ago] of who) {
    if (n === gone) continue; // its member slice is gone: the team still names it, so it draws, and reads as gone
    out.set(m(n), { id: m(n), as_of: NOW, body: { id: m(n), name: n, role, column: FLOW_COLUMN[role] ?? 2, placement: { stream: "main" }, model: role === "reviewer" ? "opus" : "sonnet",
      last_exchanges: n === "implementor-2" ? ["exchange:a1b2c3d4/s1", "exchange:a1b2c3d4/s0"] : [], ...(ago ? { activity: `activity:${SHOT}/${n}` } : {}) } });
    if (ago) out.set(`activity:${SHOT}/${n}`, { id: `activity:${SHOT}/${n}`, as_of: NOW - ago * 1000, body: { tool } });
  }
  const ex = (id, a, b, title, mins, back) => out.set(`exchange:a1b2c3d4/${id}`, { id: `exchange:a1b2c3d4/${id}`, as_of: NOW, body: { from: a, to: b, from_member: m(a === "orchestrator" ? "@orchestrator" : a),
    to_member: m(b), title, created: new Date(NOW - mins * 60000).toISOString(), ...(back ? { response_created: new Date(NOW - back * 60000).toISOString() } : {}) } });
  ex("s1", "orchestrator", "implementor-2", "Polish the herdr Graph", 2, 0);
  ex("s0", "orchestrator", "implementor-2", "Fix the Flow legend", 40, 31);
  const brief = (id, a, b) => ({ id: `exchange:a1b2c3d4/${id}`, from_member: m(a), to_member: m(b) });
  out.set(`list:open:team:${SHOT}`, { id: `list:open:team:${SHOT}`, as_of: NOW, body: { team: `team:${SHOT}`, open: [brief("s1", "@orchestrator", "implementor-2"), brief("s2", "@orchestrator", "implementor-3"), brief("s3", "implementor", "reviewer")] } });
  out.set(`topology:${SHOT}`, { id: `topology:${SHOT}`, as_of: NOW, body: { team: `team:${SHOT}`, edges: { brief: [{ id: "exchange:a1b2c3d4/s3", brief: "review" }],
    parent_child: who.slice(1).map((w) => ({ from: m("@orchestrator"), to: m(w[0]) })) } } });
  return out;
};
const shotSlices = shotOf();
const FLOWS = {
  "graph-shot": flowModel(shotSlices, SHOT, "horizontal", 1, { w: 1000, h: 560 }),
  "graph-shot-blocked": flowModel(shotOf("implementor-3"), SHOT, "horizontal", 1, { w: 1000, h: 560 }),
  "graph-flow": flowModel(flowSlices, team, "horizontal", 1, ROOM),
  "graph-flow-v": flowModel(flowSlices, team, "vertical", 1, ROOM),
};
const goneShot = shotOf(null, "reviewer-3");
const SHEETS = { "graph-sheet": [shotSlices, `member:${SHOT}/implementor-2`, "t2"], "graph-sheet-gone": [goneShot, `member:${SHOT}/reviewer-3`, null] };
for (const [v, [sl]] of Object.entries(SHEETS)) FLOWS[v] = flowModel(sl, SHOT, "horizontal", 1, { w: 700, h: 560 });
const sheetOf = (v, full) => (SHEETS[v] ? sheetHtml(SHEETS[v][0], SHOT, SHEETS[v][1], FLOWS[v].dots.length > 0, SHEETS[v][2], full) : "");
const flowWin = Object.entries(FLOWS).map(([v, m]) => `<div class="ov gr gfw" data-v="${v}" role="dialog" aria-label="Graph"${v.startsWith("graph-shot") || SHEETS[v] ? ' style="width:1034px"' : ""}>
  ${deskTabs("graph", true)}
  ${flowHead(m)}${SHEETS[v] ? `<div class="shrow"><div class="ovb reg">${flowStage(m)}</div>${sheetOf(v, false)}</div>` : `<div class="ovb reg">${flowStage(m)}</div>`}
 </div>`).join("");
// Docked: the 420 px column, its height as tall as the preview's window allows; no stored orientation, so the shape picks it.
const fmDock = flowModel(flowSlices, team, null, 1, { w: 420 - 2 - 32, h: 640 });
const dockFlow = `<aside class="dock dfl" aria-label="Wrangl desk">
  ${deskTabs("graph", false)}
  ${flowHead(fmDock)}<div class="ovb reg">${flowStage(fmDock)}</div>
 </aside>`;
// Docked, the open sheet takes the whole column (the Board's sheet, board.js sheetColumn full): `dock-sheet`, `dock-sheet-gone`.
const dockSheets = Object.keys(SHEETS).map((v) => `<aside class="dock dsh" data-v="dock-${v.slice(6)}" aria-label="Wrangl desk">
  ${deskTabs("graph", false)}
  ${flowHead(fmDock)}${sheetOf(v, true)}
 </aside>`).join("");

// The settings dialog, mirroring settings.js's sizes: default settings with one lane hidden, the plugin newer than the mod.
const SET = settingsOf({ hidden: ["a1b2c3d4/lane-herdr"] });
const UPD = { installed: "2.5.0-pre.1", bundled: "2.5.0", notes: changelogEntry(readFileSync(join(here, "..", "..", "CHANGELOG.md"), "utf8"), "2.5.0", 1200) };
const md = (text) => text.split(/\n{2,}/).map((b) => {
  const inline = (x) => esc(x).replace(/\*\*([^*]+)\*\*/g, "<b>$1</b>").replace(/`([^`]+)`/g, "<code>$1</code>").replace(/\[([^\]]+)\]\([^)]*\)/g, "$1");
  if (b.startsWith("### ")) return `<h4>${inline(b.slice(4))}</h4>`;
  if (b.startsWith("- ")) return `<ul>${b.split(/\n- /).map((li) => `<li>${inline(li.replace(/^- /, ""))}</li>`).join("")}</ul>`;
  return `<p>${inline(b)}</p>`;
}).join("");
const sw = (on, label) => `<button class="sw${on ? " on" : ""}" role="switch" aria-checked="${on}" aria-label="${esc(label)}"><i><b></b></i></button>`;
const seg = (label, opts, value) => `<div class="seg" role="radiogroup" aria-label="${esc(label)}">${opts.map(([v, t]) => `<button role="radio" aria-checked="${v === value}" class="${v === value ? "on" : ""}">${esc(t)}</button>`).join("")}</div>`;
const srow = (label, control, hint) => `<div class="srow"><div class="sl"><span class="sn">${label}</span>${control}</div>${hint ? `<div class="sh">${esc(hint)}</div>` : ""}</div>`;
const teamSet = (t, lane) => [srow(`${lane ? '<span class="dim">↳</span> ' : ""}${esc(t.name)}`, sw(!SET.hidden.includes(t.id), `Show ${t.name} in the sidebar`)), ...t.lanes.map((l) => teamSet(l, true))].join("");
const setWin = `<div class="scrim"></div><div class="ov set" role="dialog" aria-label="Wrangl settings">
  <div class="ovh"><h1>Wrangl settings</h1><button class="x" title="Close (esc)" aria-label="Close Wrangl settings">×</button></div>
  <div class="ovb">
   <section class="ss"><h2>Sidebar</h2>${srow("Opens on", seg("Tab shown when the Wrangl layout opens", [["agents-list", "Agents"], ["ah2", "Wrangl"]], SET.tab), "The tab shown each time you switch to the ah2 layout.")}</section>
   <section class="ss"><h2>Tab strip</h2>${STRIP.map(([k, l]) => srow(esc(l), sw(SET.strip[k], `Show ${l} in the tab strip`))).join("")}</section>
   <section class="ss"><h2>Teams and lanes</h2>${srow("Lanes", seg("Lanes", [["nested", "Nested"], ["collapsed", "Collapsed"]], SET.lanes), "Collapsed keeps a lane's header row and hides its members.")}<div class="sub">Shown in the sidebar</div>${side.teams.map((t) => teamSet(t, false)).join("")}<div class="sfoot">A hidden team's questions still count in Needs you and Q&amp;A.</div></section>
   <section class="ss"><h2>Refresh and alerts</h2>${srow("Refresh", seg("Refresh", INTERVALS, SET.interval), "Live updates as soon as anything changes; the others refresh at most that often.")}${srow("Alarms as herdr notices", sw(SET.notify, "Alarms as herdr notices"), "When a team raises an alarm, also post a herdr notice.")}</section>
   <section class="ss"><h2>Updates</h2>${srow("Installed", `<span class="sv">v${UPD.installed}</span>`)}${srow("In the Wrangl plugin", `<span class="sv">v${UPD.bundled}</span>`)}<div class="sst"><i class="udot" aria-hidden="true"></i><span>v${UPD.bundled} is ready to install</span></div>${srow("Auto-update", sw(SET.autoUpdate, "Auto-update"), "Installs a newer plugin's mod as soon as a check finds it. A check runs when the Wrangl layout opens, then hourly.")}
    <div class="sact"><button class="sb pri">Update to v${UPD.bundled}</button><button class="sb">Check now</button><span class="fill"></span><button class="sb gh" aria-label="Hide what's new">Hide what's new</button></div>
    <div class="notes">${md(UPD.notes)}</div>
    <div class="sfoot">Updates come from the Wrangl plugin on this machine. Update and Reinstall run ah herdr install, which keeps any mod file you edited.</div></section>
  </div>
 </div>`;

const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Wrangl Herdr Preview</title>
<style>
:root{--popover:#ffffff;--input:#00000026;--accent-fg:#ffffff;--canvas:#fafaf9;--chrome:#f1f1ee;--surface:#ffffff;--raised:#f6f6f6;--border:#00000017;--hover:#0000000c;--selected:#00000012;--foreground:#1b1b1e;--muted:#69696a;--dim:#969698;--accent:#1f62d0;--working:#9a6700;--blocked:#c93c2c;--done:#2f6fe4;--idle:#a8a8aa;--unknown:#a8a8aa;--page:#e2e2de;--shadow:0 12px 40px #0002;--wb:#0963ce;--wr:#794acf;--grid:#e6e9ed}
:root[data-theme=dark]{--popover:#25272b;--input:#ffffff24;--accent-fg:#17191d;--canvas:#131518;--chrome:#101114;--surface:#17191d;--raised:#2c2e31;--border:#ffffff18;--hover:#ffffff0e;--selected:#ffffff17;--foreground:#eeeeef;--muted:#98999b;--dim:#696a6d;--accent:#96b2d7;--working:#e5c07b;--blocked:#f0705f;--done:#76a8ff;--idle:#696a6d;--unknown:#696a6d;--page:#08090a;--shadow:0 12px 40px #0008;--wb:#4c9aff;--wr:#c9a2ff;--grid:#1f2429}
*{box-sizing:border-box}html,body{margin:0;height:100%}body{background:var(--page);font:13px/1.35 Geist,ui-sans-serif,system-ui,-apple-system,sans-serif;color:var(--foreground);padding:16px}
.win{height:100%;display:flex;background:var(--canvas);border-radius:10px;overflow:hidden;border:1px solid var(--border);position:relative}
.side{width:268px;flex:none;background:var(--chrome);display:flex;flex-direction:column;border-right:1px solid var(--border)}
.brand{height:46px;display:flex;align-items:center;padding:0 14px 0 76px;font-weight:600;font-size:12px;color:var(--muted)}
.lights{position:absolute;left:12px;top:16px;display:flex;gap:8px}.lights i{width:12px;height:12px;border-radius:50%;background:var(--border)}
.spaces{flex:1;min-height:90px;padding:8px 16px;color:var(--muted);font-size:11.5px}
.ah2{flex:1;min-height:90px;display:flex;flex-direction:column;border-top:1px solid var(--border)}
.stabs{height:36px;flex:none;padding:0 8px 0 12px;display:flex;align-items:center;gap:4px;border-top:1px solid var(--border)}.stabs .st{height:24px;padding:0 10px;display:flex;align-items:center;border:1px solid var(--chrome);border-radius:5px;background:var(--chrome);color:var(--muted);font:12px Geist,ui-sans-serif,system-ui,sans-serif;cursor:pointer}.stabs .st:hover{background:var(--hover);color:var(--foreground)}.stabs .st.on{background:var(--raised);border-color:var(--border);color:var(--foreground)}.stabs .st:focus-visible{outline:none;border-color:var(--accent)}.stabs .back{margin-left:auto}
.head{height:41px;padding:14px 8px 5px 16px;display:flex;align-items:center;gap:6px;flex:none}
.head .lbl{flex:1;font-size:11.5px;font-weight:500;color:var(--muted)}
.need{flex:none;height:18px;padding:0 6px;border-radius:9px;border:1px solid var(--blocked);color:var(--foreground);font-size:10.5px;font-weight:600;display:flex;align-items:center}
.back{height:24px;padding:0 8px;display:flex;align-items:center;gap:4px;border-radius:6px;border:1px solid var(--border);background:none;color:var(--muted);font:500 11px Geist,ui-sans-serif,system-ui,sans-serif;cursor:pointer}
.back:hover{background:var(--hover);color:var(--foreground)}.back:focus-visible{outline:none;border-color:var(--accent);color:var(--foreground)}
.list{flex:1;min-height:0;overflow:auto;padding:0 8px}
.team{width:100%;height:30px;padding-right:8px;display:flex;align-items:center;gap:4px;border:1px solid var(--chrome);border-radius:6px;background:none;font:600 11px Geist,ui-sans-serif,system-ui,sans-serif;color:var(--muted);text-align:left;cursor:pointer}
.team:hover{background:var(--hover);color:var(--foreground)}.team:focus-visible,.row:focus-visible,.x:focus-visible{outline:none;border-color:var(--accent)}.tn{flex:1 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.tc{flex:0 100 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-weight:500;color:var(--muted)}.tc.need-n{flex:none;color:var(--foreground)}
.row{height:30px;padding-right:8px;display:flex;align-items:center;gap:8px;border:1px solid var(--chrome);border-radius:6px;font-size:12.5px}
.g{width:14px;flex:none;text-align:center;font-size:10px}
.t-working{color:var(--working)}.t-blocked{color:var(--blocked)}.t-idle{color:var(--idle)}.t-unknown{color:var(--unknown)}.t-done{color:var(--done)}.dim{color:var(--dim)}
.role{width:86px;flex:none;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.role.quiet{color:var(--muted)}
.what{flex:1;min-width:0;color:var(--muted);font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.age{width:28px;flex:none;text-align:right;font:11px "Geist Mono",ui-monospace,Menlo,monospace;color:var(--muted)}
.alarm{margin:2px 8px 4px 0;display:flex;gap:4px;font-size:11px;color:var(--muted)}.alarm b{font-weight:400;color:var(--blocked)}.alarm span{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.grp{height:30px;padding:0 8px;display:flex;align-items:center;font-size:11px;font-weight:600;color:var(--muted)}
.status{height:28px;flex:none;padding:0 16px;display:flex;align-items:center;border-top:1px solid var(--border);font-size:11px;color:var(--muted)}
.foot{height:69px;flex:none;border-top:1px solid var(--border)}
.main{flex:1;min-width:0;display:flex;flex-direction:column}
.tabs{height:42px;flex:none;display:flex;align-items:center;gap:6px;padding:0 12px}.tab{height:26px;padding:0 12px;display:flex;align-items:center;font-size:12px;color:var(--muted);border-radius:13px}.tab.on{background:var(--raised);color:var(--foreground)}
.pane{flex:1;margin:0 8px 8px;border:1px solid var(--border);border-radius:10px;background:var(--canvas);padding:14px 12px;font:13px/1.5 "JetBrains Mono",ui-monospace,Menlo,monospace;color:var(--muted);white-space:pre;overflow:hidden}
.sbar{height:28px;flex:none}
.ov{position:absolute;left:340px;top:72px;width:620px;height:min(600px,calc(100% - 96px));background:var(--surface);border:1px solid var(--border);border-radius:10px;box-shadow:var(--shadow);display:flex;flex-direction:column}
.ovh{height:44px;flex:none;display:flex;align-items:center;gap:10px;padding:0 16px;border-bottom:1px solid var(--border)}.ovh h1{font-size:13px;font-weight:600;margin:0;flex:1}
.x{width:24px;height:24px;border:1px solid var(--surface);border-radius:6px;background:none;color:var(--muted);font-size:15px;cursor:pointer}.x:hover{background:var(--hover);color:var(--foreground)}
.ovb{flex:1;min-height:0;overflow:auto;padding:0 16px 12px;font-size:12.5px}
.reg{margin-top:12px}.reg h2{height:20px;margin:0;display:flex;align-items:center;gap:6px;font-size:11px;font-weight:600;color:var(--muted)}.reg h2 .c{font:500 11px "Geist Mono",ui-monospace,monospace;color:var(--muted)}
.it{min-height:28px;padding:4px 0;display:flex;align-items:center;gap:8px;border-bottom:1px solid var(--border)}
.k{width:64px;flex:none;font:11px "Geist Mono",ui-monospace,monospace;color:var(--muted);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.k.t-done{color:var(--done)}
.ti{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.who{flex:none;font-size:11px;color:var(--muted)}
.q{margin-top:4px;padding:10px 12px;border-radius:8px;background:var(--raised);display:flex;flex-direction:column;gap:4px}.qm{font-size:11px;color:var(--muted)}
.none{padding:4px 0;font-size:11px;color:var(--muted)}
.qrow{display:flex;align-items:center;gap:8px}.qrow .qm{flex:1;min-width:0}.qlink{height:24px;padding:0 6px;border:1px solid var(--raised);border-radius:6px;background:none;color:var(--muted);font:11px Geist,ui-sans-serif,system-ui,sans-serif;cursor:pointer}.qlink:hover{background:var(--hover);color:var(--foreground)}
.cnt{height:16px;padding:0 5px;display:flex;align-items:center;border-radius:8px;border:1px solid var(--blocked);font:10.5px "Geist Mono",ui-monospace,monospace;color:var(--foreground)}
.spill{height:18px;padding:0 6px;flex:none;display:flex;align-items:center;border-radius:9px;border:1px solid var(--border);font-size:10.5px;font-weight:500;color:var(--muted)}.spill:hover{background:var(--raised);color:var(--foreground)}
.gear.dk+.gear{margin-left:0}html[data-view=dock] .gear.dk{background:var(--raised);border-color:var(--border);color:var(--foreground)}
.vt{height:34px;flex:none;padding:8px 16px 0;display:flex;align-items:center;gap:2px}.vt button{height:26px;padding:0 10px;border:1px solid var(--surface);border-radius:6px;background:var(--surface);color:var(--muted);font:12px Geist,ui-sans-serif,system-ui,sans-serif}.vt .on{background:var(--raised);border-color:var(--raised);color:var(--foreground)}
.qa .grp{height:16px;margin:12px 0 6px;padding:0}
.card{margin-bottom:8px;padding:10px 12px;border-radius:8px;background:var(--raised);border:1px solid var(--raised)}.card.err{border-color:var(--blocked)}
.meta{display:flex;gap:6px;align-items:center;margin-bottom:4px;font-size:11px;color:var(--muted)}.meta .mk{max-width:160px;flex:none;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font:11px "Geist Mono",ui-monospace,monospace;color:var(--foreground)}.meta .mt{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.meta .agec{flex:none;font-family:"Geist Mono",ui-monospace,monospace}
.card p{margin:0 0 8px}
.desc{display:flex;flex-direction:column;gap:4px;margin-bottom:8px;font-size:12px}.desc .dh{font-weight:600}.desc .dh.sp{margin-top:4px}.desc .db{display:flex;flex-direction:column;gap:4px}
.desc .dl{display:flex;gap:6px}.desc .dl>span:first-child{color:var(--muted);flex:none}.desc .dc{display:flex;flex-wrap:wrap;gap:4px}
.desc .copy{height:22px;padding:0 6px;border:1px solid var(--border);border-radius:6px;background:var(--surface);color:var(--foreground);font:11px "Geist Mono",ui-monospace,monospace;text-align:left}
.desc .copy.blk{height:auto;padding:6px 8px;font-size:11.5px;white-space:pre}.desc .dmore{align-self:flex-start}
.opts{display:flex;flex-wrap:wrap;gap:6px;align-items:center}
.chip{height:24px;padding:0 8px;display:flex;align-items:center;gap:4px;border:1px solid var(--border);border-radius:6px;background:var(--surface);color:var(--foreground);font:11.5px Geist,ui-sans-serif,system-ui,sans-serif}.chip.sel{background:linear-gradient(var(--selected),var(--selected)),var(--surface)}.chip:disabled{opacity:.6}.chip .ck{font-size:10px;color:var(--accent)}.rec{margin-left:2px;font-size:10px;color:var(--muted)}
.inp{margin-top:6px;height:28px;padding:0 8px;display:flex;align-items:center;border:1px solid var(--border);border-radius:6px;background:var(--surface);font-size:12px}.ph{color:var(--muted)}
.more{margin-top:6px;font-size:11px;color:var(--muted)}
.msg{margin-top:8px;display:flex;gap:4px;font-size:11.5px}.msg b{font-weight:400;color:var(--blocked)}
.act{margin-top:8px;display:flex;gap:8px;align-items:center}
.send{padding:1px;border:1px solid var(--raised);border-radius:7px;background:none}.send span{height:22px;padding:0 12px;display:flex;align-items:center;border-radius:6px;background:var(--accent);color:var(--surface);font:600 11.5px Geist,ui-sans-serif,system-ui,sans-serif}.send:disabled span{opacity:.45}
.nn{height:24px;padding:0 6px;border:1px solid var(--raised);border-radius:6px;background:none;color:var(--muted);font:11.5px Geist,ui-sans-serif,system-ui,sans-serif}.nn:disabled{opacity:.6}
.done1{margin-bottom:8px;min-height:34px;padding:0 12px;display:flex;gap:6px;align-items:center;border-radius:8px;background:var(--raised);font-size:12px}.done1 .m{color:var(--muted)}
.hr:first-child{margin-top:8px}.hr{min-height:32px;padding:6px 0;display:flex;gap:8px;align-items:center;border-bottom:1px solid var(--border);font-size:12px}
.hr .tm{width:38px;flex:none;font:11px "Geist Mono",ui-monospace,monospace;color:var(--muted)}.hr .hk{max-width:96px;flex:none;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font:11px "Geist Mono",ui-monospace,monospace;color:var(--muted)}
.hr .hq{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.achip{max-width:140px;height:20px;padding:0 8px;flex:none;display:flex;align-items:center;border:1px solid var(--border);border-radius:6px;background:var(--surface);font-size:11.5px;overflow:hidden;white-space:nowrap}.hr .by{width:72px;flex:none;text-align:right;font-size:11px;color:var(--muted)}
.hr.open{width:100%;padding:6px 0;border:0;border-bottom:1px solid var(--border);background:none;color:inherit;font:inherit;text-align:left;cursor:pointer}.hr .hq2{flex:1;min-width:0;display:flex;flex-direction:column}.hr .hp{min-width:0;font-size:11.5px;color:var(--muted);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.achip.quiet{color:var(--muted)}.hr.open:hover{background:var(--raised)}.hr.open:focus-visible{outline:none;background:var(--raised);border-bottom-color:var(--accent)}
.tback{margin:8px 0;height:24px;padding:0 8px;display:flex;align-items:center;border:1px solid var(--surface);border-radius:6px;background:none;color:var(--muted);font:12px Geist,ui-sans-serif,system-ui,sans-serif;cursor:pointer}.tback:hover{color:var(--foreground)}.tback:focus-visible{outline:none;border-color:var(--accent)}.tq{font-size:12px;white-space:pre-line}.tl{margin-top:10px;font-size:11px;color:var(--muted)}.tfold{margin-top:10px;height:24px;padding:0 8px;display:flex;gap:6px;align-items:center;border:1px solid var(--surface);border-radius:6px;background:none;color:var(--muted);font:11.5px Geist,ui-sans-serif,system-ui,sans-serif;cursor:pointer}.tfold span{width:10px;flex:none}.tu{margin:4px 0 0 12px;padding-left:10px;border-left:1px solid var(--border)}.tu>.tl:first-child{margin-top:0}.tfold:hover{color:var(--foreground)}.tfold:focus-visible{outline:none;border-color:var(--accent)}
.empty{padding:40px 0;display:flex;flex-direction:column;align-items:center;gap:4px}.empty b{font-size:13px;font-weight:500}.empty span{font-size:12px;color:var(--muted)}
.qnote{padding:12px 0;font-size:12px;color:var(--muted)}.qnote small{font-size:11px}
.ov.board{display:none;left:276px;right:8px;top:42px;bottom:36px;width:auto;height:auto;box-shadow:none}html[data-view=board] .ov.board{display:flex}
.kbtab{display:none!important}html[data-view=board] .kbtab{display:flex!important;background:var(--raised);color:var(--foreground)}html[data-view=board] .tabs .tab.on:not(.kbtab){background:none;color:var(--muted)}
.kbw{flex:1;min-height:0;display:flex}.kbl{flex:1;min-width:0;display:flex;flex-direction:column}.kbr{height:40px;flex:none;display:flex;align-items:center;gap:6px;padding:0 16px;border-bottom:1px solid var(--border);overflow-x:auto}.kbr .sep{width:1px;height:16px;margin:0 4px;flex:none;background:var(--border)}
.chip.kf{flex:none;color:var(--muted);cursor:pointer;white-space:nowrap}.chip.kf.sel{color:var(--foreground)}.chip.kf:hover{border-color:var(--muted);color:var(--foreground)}
.kbcol.wide{min-width:248px}.kbt{display:flex;gap:6px;align-items:flex-start}.kbt span{flex:1;min-width:0}.kdot{flex:none;width:6px;height:6px;margin-top:5px;border-radius:3px;background:var(--accent)}
button.kbc{width:100%;text-align:left;font:inherit;color:inherit;border:1px solid var(--raised);cursor:pointer}button.kbc:hover{border-color:var(--muted)}button.kbc:focus-visible{outline:none;border-color:var(--accent)}.kbc.on,.kbc.on:hover{border-color:var(--accent)}div.kbc{border:1px solid var(--raised)}
.card.kq{padding:8px 12px}.card.kq p{margin:0}.kpick{flex:0 1 auto;min-width:0;background:var(--selected)}.kpt{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.kq .act{gap:6px}
.kbs{width:300px;flex:none;display:flex;flex-direction:column;border-left:1px solid var(--border);min-height:0}.kbsh{padding:8px 16px 12px;display:flex;flex-direction:column;gap:6px;border-bottom:1px solid var(--border)}
.kbsm{height:28px;display:flex;align-items:center;gap:6px;font-size:11px;color:var(--muted)}.kbsm .x{margin-left:auto;display:flex;align-items:center;justify-content:center;padding:0}.kbst{font-size:13.5px;font-weight:600}
.kbso{height:24px;display:flex;align-items:center;gap:8px;font-size:12px}.kbso b{font-weight:500}.kbso span:last-child{color:var(--muted)}.kbs .sb{border-color:var(--border);background:var(--surface)}
.kblog{flex:1;min-height:0;overflow-y:auto;padding:8px 16px}.kbll{height:20px;font-size:11px;font-weight:600;color:var(--muted)}.kblr{min-height:28px;padding:4px 0;display:flex;gap:10px;align-items:flex-start;border-bottom:1px solid var(--border);font-size:12px}.kblr .tm{width:38px;flex:none;padding-top:1px;font:11px "Geist Mono",ui-monospace,monospace;color:var(--muted)}.kblr .kx{flex:1;min-width:0}.kblr .kby{flex:none;font-size:11px;color:var(--muted)}
.kb{flex:1;min-height:0;display:flex;gap:12px;align-items:stretch;padding:8px 16px 12px;overflow-x:auto;font-size:12.5px}.kbcol{flex:1 1 0;min-width:184px;display:flex;flex-direction:column;min-height:0}
.kbcol h2{height:28px;flex:none;margin:0;display:flex;align-items:center;gap:6px;font-size:11px;font-weight:600;color:var(--muted)}.kbcol h2 .c{font:500 11px "Geist Mono",ui-monospace,monospace}
.kbcards{flex:1;min-height:0;overflow-y:auto}.kbc{margin-bottom:8px;padding:8px 12px;border-radius:8px;background:var(--raised);display:flex;flex-direction:column;gap:4px}
.kbm{display:flex;gap:6px;align-items:center;font-size:11px;color:var(--muted)}.kid{flex:none;max-width:96px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font:11px "Geist Mono",ui-monospace,monospace}.kw{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.kbf{display:flex;gap:4px;align-items:center;font-size:11px}.kbf b{font-weight:400;color:var(--blocked)}
.ov.qa,.ov.gr{display:none}${Object.keys(GRAPHS).map((v) => `html[data-view=${v}] .ov.gr[data-v=${v}]{display:flex}`).join("")}html[data-view] .ov.sit{display:none}${VIEWS.map((v) => `html[data-view=${v}] .ov.qa[data-v=${v}]{display:flex}`).join("")}
html[data-view=dock-sheet] .dock.dsh[data-v=dock-sheet],html[data-view=dock-sheet-gone] .dock.dsh[data-v=dock-sheet-gone]{display:flex}
.shrow{flex:1;min-height:0;display:flex}.shrow>.ovb{flex:1;min-width:0}.sheet{width:300px;flex:none;display:flex;flex-direction:column;min-height:0;border-left:1px solid var(--border);background:var(--surface);font:12px Geist,ui-sans-serif,system-ui,sans-serif;color:var(--foreground)}.sheet.full{width:auto;flex:1;border-left:0}
.shh{padding:8px 16px 10px;display:flex;flex-direction:column;gap:4px;border-bottom:1px solid var(--border)}.sht{height:28px;display:flex;align-items:center;gap:6px}.sht b{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:13.5px;font-weight:600}
.shi{width:24px;height:24px;flex:none;display:flex;align-items:center;justify-content:center;border:1px solid var(--surface);border-radius:6px;background:none;color:var(--foreground);padding:0}.shi.off{color:var(--muted)}.shi.fo{border-color:var(--accent)}.shs{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.dim2{color:var(--muted)}
.shb{flex:1;min-height:0;overflow:auto;padding:0 16px 12px}.shn{padding:10px 0 4px;font-size:11px;font-weight:600;color:var(--muted)}.shr{min-height:24px;display:flex;gap:10px;align-items:flex-start}.shk{width:96px;flex:none;padding-top:1px;font-size:11px;color:var(--muted)}.shv{flex:1;min-width:0}
.shm{min-height:24px;padding:2px 0;display:flex;gap:8px;align-items:flex-start;border-bottom:1px solid var(--border)}.shm .t{width:38px;flex:none;padding-top:1px;font-size:11px;color:var(--muted)}.shm .k{width:44px;flex:none;font-size:11px;color:var(--muted)}.shm .b{flex:1;min-width:0;display:flex;flex-direction:column}.shm .b span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.shm .i{font-size:11px;color:var(--muted)}.shq{padding:4px 0;color:var(--muted)}
.shp{margin-top:10px;height:28px;padding:0 10px;border:1px solid var(--border);border-radius:6px;background:none;color:var(--muted);font:12px Geist,ui-sans-serif,system-ui,sans-serif}
.ov.gr{height:min(720px,calc(100% - 96px))}${Object.keys(FLOWS).map((v) => `html[data-view=${v}] .ov.gr[data-v=${v}]{display:flex}`).join("")}html[data-view=dock] .dock.dfl{display:none}html[data-view=dock-flow] .dock.dfl{display:flex}.fhd{height:34px;flex:none;padding:8px 16px 0;display:flex;align-items:center;gap:8px}.fhd .vt{flex:1;min-width:0;height:auto;padding:0}.fhd .gmd{flex:none}.gmd button{height:24px;padding:0 8px;font-size:11.5px}.vt button.on{background:var(--raised);border-color:var(--raised);color:var(--foreground)}.ori{height:24px;min-width:24px;padding:0 6px;border:1px solid var(--input);border-radius:6px;background:none;color:var(--foreground);font:11.5px Geist,ui-sans-serif,system-ui,sans-serif}.gflow{position:relative;margin-top:12px;overflow:hidden;flex:none;background-image:radial-gradient(circle,var(--grid) 1px,transparent 1.5px);background-size:20px 20px}.fin{position:absolute}.fin>*{position:absolute}.fb{border-radius:6px;background:var(--hover)}.fb span{position:absolute;left:8px;top:2px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--muted)}.flk{left:0;top:0;fill:none;overflow:visible}.flk path{stroke:var(--wb)}.flk path.review{stroke:var(--wr)}.flk path.edge{stroke:var(--muted)}.flk path.review{stroke-dasharray:5 4}.fl{white-space:nowrap;color:var(--lc-l,var(--muted))}:root[data-theme=dark] .fl{color:var(--lc-d,var(--muted))}.fln{display:block;border-radius:6px;background:var(--lb-l)}:root[data-theme=dark] .fln{background:var(--lb-d)}.flr{display:block;height:2px;background:var(--lc-l)}:root[data-theme=dark] .flr{background:var(--lc-d)}.fc{box-sizing:border-box;display:flex;flex-direction:column;justify-content:center;overflow:hidden;border:1px solid var(--cl-l,var(--border));border-radius:6px;background:var(--cf-l,var(--raised))}.fc.on{justify-content:flex-start}:root[data-theme=dark] .fc{border-color:var(--cl-d,var(--border));background:var(--cf-d,var(--raised))}.fc.gone{opacity:.4}.fh{display:flex;align-items:center;min-width:0;flex:none}.fh.on{border-bottom:1px solid var(--border)}.fg{flex:none;color:var(--cm-l,var(--blocked))}:root[data-theme=dark] .fg{color:var(--cm-d,var(--blocked))}.fn{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--cn-l,var(--foreground))}.fn.b{font-weight:600}:root[data-theme=dark] .fn{color:var(--cn-d,var(--foreground))}.fx{flex:none;color:var(--muted)}.ft{color:var(--ct-l,var(--muted))}:root[data-theme=dark] .ft{color:var(--ct-d,var(--muted))}.gfl.fdt{background:var(--cd-l)}:root[data-theme=dark] .gfl.fdt{background:var(--cd-d)}.fr{display:block;height:1px;flex:none;background:var(--border)}.fs{display:flex;align-items:center;color:var(--muted)}.fs b{font-weight:600;color:var(--foreground)}.fmore{position:absolute;top:8px;right:8px;font-size:11px;color:var(--muted)}.flg{height:24px;display:flex;align-items:center;gap:12px;font-size:11px;color:var(--muted);white-space:nowrap}.flg span{display:flex;align-items:center;gap:6px;flex:none}.flg i{display:block;flex:none;padding:0;border-radius:0}.flg i.kb{width:14px;height:2px;background:var(--wb)}.flg i.ke{width:14px;height:1px;background:var(--muted)}.flg svg path{stroke:var(--wr);stroke-width:2;stroke-dasharray:5 4}.flg .fd2{flex:1;min-width:0;display:block;overflow:hidden;text-overflow:ellipsis}.gmap-w{margin-top:12px;display:flex;justify-content:center}.gmap{position:relative;flex:none}.gmap>*{position:absolute}
.gc{border-radius:50%;background:var(--raised);border:1px solid var(--muted)}.garcs{left:0;top:0;overflow:visible}.garcs path{fill:none;stroke:var(--accent);stroke-opacity:.6;stroke-linecap:round}:root:not([data-theme=dark]) .garcs path{stroke-opacity:.75}.garcs path.rep{stroke-dasharray:5 4;stroke-linecap:butt}
.gm{border-radius:50%;background:var(--surface);border:1px solid var(--border);box-sizing:border-box}.gm.gone{opacity:.4}.gm>svg{position:absolute;left:-1px;top:-1px}.gm>svg path{fill:none}.gm .trk{stroke:var(--border)}.gm .busy{stroke:currentColor}
.gls{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:1px}.gl{font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.gl b{font-size:9px;font-weight:400}.gl.sub{font-size:11px;color:var(--muted)}.gl.val{font:600 11px "Geist Mono",ui-monospace,monospace}
.gfl{width:8px;height:8px;border-radius:50%;background:var(--accent);border:1px solid var(--surface);box-sizing:border-box}.gcl{height:16px;display:flex;justify-content:center}.gcl span{height:16px;padding:0 4px;display:flex;align-items:center;border-radius:4px;background:var(--surface);font-size:11px;font-weight:600;color:var(--muted);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.dock{display:none;width:420px;flex:none;margin:42px 8px 36px 0;flex-direction:column;background:var(--surface);border:1px solid var(--border);border-radius:10px;overflow:hidden}html[data-view=dock] .dock{display:flex}.dock .gn{width:80px}.dock .ovh{gap:4px}.dock .ovh h1{margin-right:6px}
.dtabs{height:40px;flex:none;padding:0 8px;display:flex;align-items:center;gap:2px;border-bottom:1px solid var(--border)}.dtabs.dwin{height:44px}.dtabs .fill{flex:1}.dtabs [role=tab]{height:26px;padding:0 10px;display:flex;align-items:center;gap:6px;border:1px solid var(--surface);border-radius:6px;background:var(--surface);color:var(--muted);font:12px Geist,ui-sans-serif,system-ui,sans-serif;cursor:pointer}.dtabs [role=tab]:hover{background:var(--hover);color:var(--foreground)}.dtabs .on{background:var(--raised);border-color:var(--border);color:var(--foreground)}.dtabs .x{width:24px;height:24px}
.srow2{position:absolute;left:268px;right:0;bottom:0;height:28px;display:flex;background:var(--chrome);border-top:1px solid var(--border);font-size:11.5px;color:var(--muted)}.srow2 .ws{flex:1;min-width:0;padding:0 14px;display:flex;align-items:center}
.seg2{width:420px;flex:none;padding:0 8px 0 12px;display:flex;align-items:center;gap:12px;border-left:1px solid var(--border)}.st2{flex:none;display:flex;align-items:center;gap:6px}.st2 .mk{width:14px}.st2 b{font:400 11.5px "Geist Mono",ui-monospace,monospace;color:var(--foreground)}
.ev2{flex:1;min-width:0;display:flex;justify-content:flex-end;align-items:center;gap:6px}.ev2 i{flex:none;font:normal 11px "Geist Mono",ui-monospace,monospace}.ev2 span{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--foreground)}
.main{position:relative}.peek{position:absolute;top:54px;right:12px;width:46px;padding:5px 0;display:flex;justify-content:flex-end;overflow:hidden;background:var(--popover);border:1px solid var(--border);border-radius:12px;box-shadow:var(--shadow);font-size:12px}html[data-view=peek] .peek{width:242px}.pin{width:240px;flex:none;padding:2px 0;display:flex;flex-direction:column;gap:2px}
.pr{height:36px;padding:0 8px;display:flex;align-items:center;gap:8px;border:1px solid var(--popover);border-radius:6px;background:none;color:var(--foreground);font:12px Geist,ui-sans-serif,system-ui,sans-serif;text-align:right;cursor:pointer}.pr:hover{background:var(--hover)}.pr:focus-visible{outline:none;border-color:var(--accent)}
.pt{flex:1;min-width:0;display:flex;flex-direction:column;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pt b{font-weight:600}.pt small{font-size:11px;color:var(--muted)}
.pb{width:28px;height:28px;flex:none;display:flex;align-items:center;justify-content:center;border-radius:14px;border:1.5px solid var(--blocked);font:600 12px "Geist Mono",ui-monospace,monospace}.pb.s{width:20px;height:20px;font-size:10.5px}.pc{width:28px;flex:none;display:flex;justify-content:center}
.stabs{position:relative}.gear svg{display:block}.ovh .x{display:flex;align-items:center;justify-content:center;padding:0}.ovh .x+.x{margin-left:-6px}
.pop{display:none;position:absolute;right:8px;top:32px;z-index:5;width:220px;padding:5px;flex-direction:column;background:var(--popover);border:1px solid var(--border);border-radius:10px;box-shadow:var(--shadow)}html[data-view=menu] .pop{display:flex}html[data-view=menu] .tdot{display:inline-block}
.pop button{height:31px;padding:0 8px;display:flex;align-items:center;gap:9px;border:1px solid var(--popover);border-radius:7px;background:none;color:var(--foreground);font:12.8px Geist,ui-sans-serif,system-ui,sans-serif;text-align:left;cursor:pointer}.pop button:first-child{background:var(--hover)}.pop button:focus-visible{outline:none;border-color:var(--accent)}.pop .udot{width:6px;height:6px;margin-left:auto}
.g{display:flex;align-items:center;justify-content:center}.mk{flex:none;display:inline-flex;align-items:center;justify-content:center;font-style:normal}.mk svg{width:12px;height:12px}
.mk.spin svg{animation:mkspin .9s linear infinite}@keyframes mkspin{to{transform:rotate(1turn)}}@media (prefers-reduced-motion:reduce){.mk.spin svg{animation:none}}
.mk.bl{width:14px;height:14px;border-radius:4px;background:color-mix(in srgb,var(--blocked) 15%,transparent);color:var(--blocked);font-size:10.5px;font-weight:700}
.mk.ring{width:7px;height:7px;box-sizing:border-box;border-radius:4px;border:1.5px solid var(--muted)}.mk.dot{width:7px;height:7px;border-radius:4px;background:var(--done)}.mk.dot.run{background:var(--working)}.mk.tx{font-size:10px;color:var(--muted)}.gl .mk{vertical-align:-2px;margin-right:2px}
.scrub{flex:none;height:104px;box-sizing:border-box;padding:8px 16px;display:flex;flex-direction:column;gap:6px;border-top:1px solid var(--border)}.sr1{height:26px;display:flex;align-items:center;gap:8px}.vt.sw{height:auto;padding:0;flex:none;width:max-content;justify-content:flex-start}.vt.sw button{height:24px;padding:0 8px;font-size:11.5px;white-space:nowrap;flex:none}.rd{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font:11.5px "Geist Mono",ui-monospace,monospace;color:var(--foreground)}.now{height:24px;padding:0 8px;border:1px solid var(--input);border-radius:6px;background:none;color:var(--foreground);font:11.5px Geist,ui-sans-serif,system-ui,sans-serif}
.trk2{position:relative;height:44px;display:flex;align-items:flex-end;gap:1px;border-bottom:1px solid var(--border)}.trk2>span{flex:1;height:100%;display:flex;align-items:flex-end}.trk2>span.in{background:var(--selected)}.trk2 i{display:block;width:100%;border-radius:1px;background:var(--muted)}.trk2 i.on{background:var(--accent)}.trk2 .knob{position:absolute;top:0;bottom:-1px;width:12px;margin-left:-6px;height:auto;background:none;border-radius:0}.trk2 .knob::before{content:"";position:absolute;left:5px;top:0;bottom:10px;width:2px;background:var(--foreground)}.trk2 .knob::after{content:"";position:absolute;left:1px;bottom:0;width:10px;height:10px;box-sizing:border-box;border-radius:5px;border:2px solid var(--foreground);background:var(--surface)}.trk2:focus-visible{outline:none;box-shadow:0 0 0 1px var(--accent)}
.ax{height:14px;display:flex;justify-content:space-between;font:10.5px "Geist Mono",ui-monospace,monospace;color:var(--muted)}.ax span:nth-child(2){font-family:Geist,ui-sans-serif,system-ui,sans-serif}
.crumb{height:28px;flex:none;padding:0 16px;display:flex;align-items:center;gap:4px;font-size:12px;color:var(--muted)}.crumb b{color:var(--foreground);font-weight:500}.crumb button{height:24px;padding:0 6px;border:1px solid var(--surface);border-radius:6px;background:none;color:var(--muted);font:inherit}
.gc.zm{cursor:pointer;padding:0}.gc.zm:hover{border-color:var(--foreground)}.gc.zm:focus-visible{outline:none;border-color:var(--accent)}
.ov.set,.scrim{display:none}html[data-view=settings] .ov.set{display:flex}html[data-view=settings] .scrim{display:block}html[data-view=settings] .ov.sit{display:none}
.scrim{position:absolute;inset:0;background:#0006}.ov.set{left:calc(50% - 240px);top:24px;width:480px;height:min(640px,calc(100% - 48px))}
.ss{margin-top:16px}.ss h2{height:20px;margin:0;display:flex;align-items:center;font-size:11px;font-weight:600;color:var(--muted)}
.srow{border-bottom:1px solid var(--border)}.sl{min-height:36px;display:flex;align-items:center;gap:12px}.sn{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.sh{padding-bottom:8px;font-size:11px;color:var(--muted)}
.sw{width:38px;height:24px;flex:none;padding:0;display:flex;align-items:center;justify-content:center;border:1px solid var(--surface);border-radius:12px;background:none;cursor:pointer}.sw:focus-visible{outline:none;border-color:var(--accent)}
.sw i{width:32px;height:18px;padding:0 2px;box-sizing:border-box;display:flex;align-items:center;border-radius:9px;background:var(--input)}.sw b{width:14px;height:14px;border-radius:7px;background:var(--foreground)}.sw.on i{justify-content:flex-end;background:var(--accent)}.sw.on b{background:var(--accent-fg)}
.seg{flex:none;display:flex;gap:2px;padding:2px;border:1px solid var(--border);border-radius:8px;background:var(--chrome)}.seg button{height:24px;padding:0 10px;display:flex;align-items:center;border:1px solid var(--chrome);border-radius:6px;background:var(--chrome);color:var(--muted);font:12px Geist,ui-sans-serif,system-ui,sans-serif;cursor:pointer}.seg button.on{background:var(--raised);border-color:var(--border);color:var(--foreground)}.seg button:hover{color:var(--foreground)}.seg button:focus-visible{outline:none;border-color:var(--accent)}
.sub{margin-top:12px;height:20px;display:flex;align-items:center;font-size:11px;color:var(--muted)}.sfoot{margin-top:8px;font-size:11px;color:var(--muted)}
.sv{flex:none;font:12px "Geist Mono",ui-monospace,monospace;color:var(--muted)}.sst{min-height:36px;display:flex;align-items:center;gap:8px;border-bottom:1px solid var(--border)}.udot{width:8px;height:8px;flex:none;border-radius:4px;background:var(--accent)}
.sact{margin-top:12px;display:flex;align-items:center;gap:8px}.sact .fill{flex:1}.sb{height:28px;padding:0 12px;display:flex;align-items:center;border:1px solid var(--surface);border-radius:7px;background:var(--raised);color:var(--foreground);font:500 12px Geist,ui-sans-serif,system-ui,sans-serif;cursor:pointer}.sb:hover{background:var(--hover)}.sb.pri{background:var(--accent);color:var(--accent-fg)}.sb.pri:hover{opacity:.88}.sb.gh{padding:0 8px;background:none;color:var(--muted);font-weight:400}.sb:focus-visible{outline:none;border-color:var(--accent)}
.notes{margin-top:8px;max-height:220px;overflow:auto;overscroll-behavior:contain;padding:8px 12px;border-radius:8px;background:var(--raised);font-size:12px;line-height:1.45}.notes p{margin:0 0 8px}.notes h4{margin:8px 0 4px;font-size:12px}.notes ul{margin:0 0 8px;padding-left:16px}.notes code{font:11px "Geist Mono",ui-monospace,monospace}
.gear{width:24px;height:24px;flex:none;margin-left:auto;display:flex;align-items:center;justify-content:center;border:1px solid var(--chrome);border-radius:6px;background:none;color:var(--muted);cursor:pointer}.gear:hover{background:var(--hover);color:var(--foreground)}.gear:focus-visible{outline:none;border-color:var(--accent)}.stabs .gear+.back{margin-left:0}
.tdot{width:6px;height:6px;margin-left:6px;border-radius:3px;background:var(--accent);display:none}html[data-view=settings] .tdot{display:inline-block}
.ov.fd{display:none}${FEEDS.map((v) => `html[data-view=${v}] .ov.fd[data-v=${v}]{display:flex}`).join("")}
.fb{padding:4px 12px 12px}.fdk{width:420px}
.fnow{flex:none;padding:8px 12px;border-bottom:1px solid var(--border)}.fnh{height:20px;padding:0 6px;display:flex;align-items:center;justify-content:space-between;font-size:11px;color:var(--muted)}.fnh span:first-child{font-weight:500}
.fch{min-height:40px;flex:none;padding:8px 12px;display:flex;flex-wrap:wrap;gap:6px;align-items:center;border-bottom:1px solid var(--border)}.chip.off{color:var(--muted)}
.fmin{display:flex;align-items:flex-start;padding-top:4px}.fmin .tm{width:44px;height:24px;flex:none;display:flex;align-items:center;font:11px "Geist Mono",ui-monospace,monospace;color:var(--muted)}.frows{flex:1;min-width:0;display:flex;flex-direction:column}
.fe{width:100%;height:24px;padding:0 6px;display:flex;gap:8px;align-items:center;border:1px solid var(--surface);border-radius:5px;background:none;color:var(--foreground);font:12px Geist,ui-sans-serif,system-ui,sans-serif;text-align:left}button.fe{cursor:pointer}button.fe:hover,.fe.hov{background:var(--hover)}button.fe:focus-visible{outline:none;border-color:var(--accent)}
.fe .g{width:14px;flex:none}.fg{font-size:11px;text-align:center}.t-muted{color:var(--muted)}.ft{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.fs{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--muted)}.fw{width:92px;flex:none;font-weight:500;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.fr{width:96px;flex:none;display:flex;justify-content:flex-end;align-items:center;gap:4px;font-size:11px;color:var(--muted)}.fte{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.fgo{height:20px;padding:0 6px;display:flex;align-items:center;border:1px solid var(--border);border-radius:5px;background:var(--surface);color:var(--foreground)}
.fnew{height:24px;display:flex;gap:8px;align-items:center;font-size:11px}.fnew i{height:1px;flex:1;background:var(--accent)}.fnew i:first-child{flex:0 0 24px}
.gr-row{height:28px;display:flex;align-items:center;gap:8px;border-bottom:1px solid var(--border)}.gn{width:120px;flex:none;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.gn.quiet{color:var(--muted)}.gro{width:110px;flex:none;font-size:11px;color:var(--muted);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.ga{width:14px;flex:none;text-align:center;color:var(--muted)}.gf{flex:1;min-width:0;font-size:11px;color:var(--muted);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.bar{width:48px;height:4px;flex:none;border-radius:2px;background:var(--hover);display:flex}.bar i{height:4px;border-radius:2px;background:var(--muted)}.gr-lane{margin-top:16px;height:20px;display:flex;align-items:center;gap:4px;font-size:11px;font-weight:600;color:var(--muted)}.ov.gr .reg h2{margin-top:12px}.ov.gr .ovb{margin-top:0}
</style>
<script>document.documentElement.dataset.theme = location.hash === "#light" ? "light" : "dark";
const v = new URLSearchParams(location.search).get("view"); if (v) document.documentElement.dataset.view = v;</script>
</head><body>
<div class="win">
 <div class="lights"><i></i><i></i><i></i></div>
 <aside class="side">
  <div class="brand">herdr</div>
  <div class="spaces">Spaces</div>
  <div class="stabs" role="tablist" aria-label="Sidebar"><button class="st" role="tab" aria-selected="false" aria-label="Show Agents">Agents</button><button class="st on" role="tab" aria-selected="true" aria-label="Wrangl, shown">Wrangl<i class="tdot" aria-hidden="true"></i></button><button class="gear dk" title="Show the desk: Q&amp;A, Board, Feed, Graph" aria-label="Show the desk">${ICON.dock}</button><button class="gear" title="Wrangl menu" aria-label="Wrangl menu" aria-haspopup="menu"><svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/><circle cx="5" cy="12" r="1"/></svg></button><div class="pop" role="menu" aria-label="ah2 menu"><button role="menuitem">ah2 settings…</button><button role="menuitem">Update to v${UPD.bundled}<i class="udot" aria-hidden="true"></i></button></div></div>
  <div class="ah2">
   <div class="head"><span class="lbl">Wrangl</span>${needs(side.needsYou)}</div>
   <div class="list">
    ${side.teams.flatMap((t) => teamRows(t, false)).join("\n    ")}
    <div class="grp">Other agents</div>
    ${others.map((a) => member({ ...STATES[a.state], state: a.state, name: a.name, what: a.space, age: "" }, 14)).join("")}
   </div>
   <div class="status">ahd · ${side.count} teams · updated now</div>
  </div>
  <div class="foot"></div>
 </aside>
 <section class="main">
  <div class="tabs"><span class="tab on">orchestrator</span><span class="tab">implementor</span><span class="tab">reviewer</span><span class="tab kbtab">Board</span></div>
  <div class="pane">❯ ah msg next
  b337  ah2 inside herdr desktop · lane-herdr  in_progress
✻ Dispatching the lane architect…</div>
  <div class="sbar"></div>
  ${drawer}
 </section>
 ${dockCol}
 ${dockFlow}${dockSheets}
 ${statusRow}
 <div class="ov sit" role="dialog" aria-label="${esc(rep.name)} sitrep">
  <div class="ovh"><h1>${esc(rep.name)} · sitrep</h1>${needs(rep.needsYou)}<button class="x" title="Close (esc)" aria-label="Close the sitrep">×</button></div>
  <div class="ovb">${rep.regions.map(region).join("")}</div>
 </div>
 ${boardWin}
 ${graphWin}
 ${flowWin}
 ${VIEWS.map((v) => qaWin(v).replace('class="ov qa"', `class="ov qa" data-v="${v}"`)).join("\n ")}
 ${FEEDS.map(feedWin).join("\n ")}
 ${setWin}
</div>
</body></html>
`;
writeFileSync(out, html);
console.log(out);
