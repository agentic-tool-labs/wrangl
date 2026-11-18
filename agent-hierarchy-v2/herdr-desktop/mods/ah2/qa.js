// ah2 in Herdr: the Q&A window (props {view:"qa"}): For you, the questions waiting on the person, and History, how the recent
// ones were answered. Colours are theme() tokens.
// Send rule (P3.3): sendAnswer runs only from send(), and send() only from a Send or Not-needed on_click. Never on load, render,
// poll or focus; a preselected default is local state until Send is pressed.

import { div } from "gpui-kit";
import { v_flex, h_flex, Button, Popover, Tabs, Tab, Input, InputState, TextView } from "gpui-base";
import { ah } from "./ah.js";
import { openView } from "./dock.js";
import { Buffer } from "buffer";
import { qaModel, qaRefresh, canSend, sendAnswer, loadingOf, readThread, descFold, threadStart, historyTeams } from "./model.js";
import { settings } from "./settings.js";
import { descriptionBlocks, inlineParts } from "./plans-model.js";
import { askerLabel } from "./select.js";
import { toggle, DISMISS, DIRECTION, answerText, classLabel } from "./questions-model.js";
import { FILTER0, TIMES, STATES, NO_MATCH, SCOPE, DEBOUNCE_MS, filterActive, historyDirs, historyRead, historyInit, historyAsk, historyMore, historyRefresh, historyTake, hasMore } from "./qa-history-model.js";

// The Herdr runtime has no TextEncoder (probed: typeof TextEncoder is "undefined"), and questions-model's Other byte cap needs
// one. Buffer gives the same UTF-8 bytes.
globalThis.TextEncoder ??= class {
  encode(s) {
    return Buffer.from(s, "utf8");
  }
};

const MONO = "Geist Mono";

// Text the person can select and copy (atp-1714): a plain div's text cannot be selected, gpui-kit's TextView can. Escaped HTML,
// so what was typed shows as typed (no markdown); line breaks kept. `id` is unique in the window.
const HTML_ESC = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" };
const sel = (id, text) => TextView.html(id, String(text ?? "").replace(/[&<>"]/g, (ch) => HTML_ESC[ch]).replace(/\n/g, "<br>")).selectable(true);

// Local UI state, per card key (team + run + id, or the item key): the pick, whether it is sending, and the last reply.
export function qaInit(v) {
  v.tab = "for";
  v.cards = new Map();
  v.inputs = new Map(); // key -> { state: InputState, focused }; made in a click handler, never in render
  v.nnOpen = null; // the card key whose Not needed menu is open
  v.thread = null; // the History thread open in place of the list: {row, loading, text?, ...threadOf, error?, open?: Set of folds shown}
  v.descOpen = new Set(); // the card keys whose description is open past its fold
  v.copied = null; // the last code span copied, "<card key>|<text>"
  v.hist = historyInit(); // History's search and filters, and their read (qa-history-model.js); kept while the pane is open, never stored
  v.histMenu = null; // the History filter menu open: "team" | "time" | "state"
  v.histTok = 0; // the newest keystroke; a debounced search for an older one does nothing
  v.histFocus = false;
  v.histInput = InputState.new({ placeholder: "Search questions and replies" });
  v.histInput.on("change", (_e, cx) => {
    const tok = ++v.histTok;
    const text = v.histInput.value();
    cx.spawn(async (cx) => {
      await cx.sleep(DEBOUNCE_MS);
      if (tok === v.histTok && text !== v.hist.filter.search) histAsk(v, { ...v.hist.filter, search: text }, cx);
    });
  });
  v.histInput.on("submit", (_e, cx) => {
    v.histTok++;
    histAsk(v, { ...v.hist.filter, search: v.histInput.value() }, cx);
  });
  v.histInput.on("focus", (_e, cx) => {
    v.histFocus = true;
    cx.notify();
  });
  v.histInput.on("blur", (_e, cx) => {
    v.histFocus = false;
    cx.notify();
  });
}

// A new search or filter: the first page again, the old rows dropped; all defaults is the slice list again, with no read (spec A1).
function histAsk(v, filter, cx) {
  v.hist = filterActive(filter) ? historyAsk(v.hist, filter) : { ...historyInit(), seq: v.hist.seq + 1 };
  v.histMenu = null;
  cx.notify();
  if (filterActive(filter)) histFetch(v, cx);
}

// One round of list reads for v.hist as it stands (first page, Load more or refresh), off the UI thread; only the newest lands.
function histFetch(v, cx) {
  const h = v.hist;
  const dirs = historyDirs(historyTeams(v.st.slices), h.filter.team);
  cx.spawn(async (cx) => {
    const sleep = (ms) => cx.sleep(ms);
    const read = v.link ? (path) => v.link.read(path, 15000, sleep) : null;
    v.hist = historyTake(v.hist, h.seq, await historyRead(read, dirs, h.filter, Date.now(), h.more ? h.next : null, ah().exec, sleep));
    cx.notify();
  });
}

// After a poll, the Q&A window: what it draws moves to the new data only with auto-update on or on the Updates available
// click (model.js qaRefresh); until then it keeps the list on screen.
// A filtered History refreshes with its filter when the list moves (`cx`, from the poll or a click).
export function qaTake(v, take = settings().autoQa, cx = null) {
  const was = v.qa?.shown;
  v.qa = qaRefresh(v.qa, v.st.slices, Date.now(), take, !loadingOf(v, true));
  if (v.qa?.shown !== was) {
    qaPrune(v);
    if (cx && v.hist && filterActive(v.hist.filter)) {
      v.hist = historyRefresh(v.hist);
      histFetch(v, cx);
    }
  }
}

// A card whose row left what is drawn goes, with its selection and its Other input (the Q&A window: its shown slices).
export function qaPrune(v) {
  const live = new Set(qaModel(v.qa?.shown ?? v.st.slices, Date.now()).teams.flatMap((tm) => tm.cards.map((c) => c.key)));
  for (const k of v.cards.keys()) if (!live.has(k)) v.cards.delete(k);
  for (const [k, inp] of v.inputs) {
    if (!live.has(k)) {
      inp.state.release();
      v.inputs.delete(k);
    }
  }
  if (v.nnOpen && !live.has(v.nnOpen)) v.nnOpen = null;
  for (const k of v.descOpen) if (!live.has(k)) v.descOpen.delete(k);
}

const cardState = (v, c) => v.cards.get(c.key) ?? { pick: c.start, sending: false, reply: null, given: "" };
const stop = (_e, cx) => cx.stop_propagation(); // a press on a control must not drag the window

// The one caller of sendAnswer. A second press while sending is dropped; no retry, ever.
function send(v, c, how, cx) {
  const cs = cardState(v, c);
  if (cs.sending) return;
  const given = how.dismiss ?? answerText(c.q, cs.pick);
  v.cards.set(c.key, { ...cs, sending: true, reply: null, given });
  v.nnOpen = null;
  cx.notify();
  cx.spawn(async (cx) => {
    const reply = await sendAnswer(ah().exec, c, how, v.link ? (body) => v.link.answer(body, (ms) => cx.sleep(ms)) : null, (ms) => cx.sleep(ms));
    const now = v.cards.get(c.key);
    if (now) v.cards.set(c.key, { ...now, sending: false, reply });
    cx.notify();
  });
}

// Other: the chip flips it (a single choice moves to it); the empty field under it only makes the input and focuses it.
function pickOther(v, c, cx, flip) {
  const cs = cardState(v, c);
  if (!v.inputs.has(c.key)) {
    const state = InputState.new({ placeholder: "Your answer", value: cs.pick.text });
    const inp = { state, focused: false };
    state.on("change", (_e, cx) => {
      const now = cardState(v, c);
      v.cards.set(c.key, { ...now, pick: { ...now.pick, text: state.value() } });
      cx.notify();
    });
    state.on("focus", (_e, cx) => {
      inp.focused = true;
      cx.notify();
    });
    state.on("blur", (_e, cx) => {
      inp.focused = false;
      cx.notify();
    });
    v.inputs.set(c.key, inp);
  }
  const pick = flip ? toggle(c.q, cs.pick, "other") : cs.pick;
  v.cards.set(c.key, { ...cs, pick });
  if (pick.other) v.inputs.get(c.key).state.focus();
  cx.notify();
}

// ---- view ----

export function qaView(v, t, close) {
  const now = Date.now();
  // What was last taken (qaTake): a refresh in flight, a failed one or a resync never blanks it (atp-1708).
  // `m` the live slices (loading only), `d` what is drawn
  const shown = v.qa?.shown ?? null;
  const m = qaModel(v.st.slices, now);
  const d = shown ? qaModel(shown, now) : m;
  // Loading until a reply has answered the whole sub set once: the questions: ids join it only after the first reply names the teams.
  const loading = loadingOf(v, m.loaded);
  let body;
  if (!shown && v.status === "down") body = [note(t, v.reason, "Retrying")];
  else if (!shown && loading) body = [note(t, "Loading questions…")];
  else if (v.tab === "history" && v.thread) body = threadPanel(v, t);
  else if (v.tab === "history" && filterActive(v.hist.filter)) body = histBody(v, t);
  else if (v.tab === "history") body = d.history.length === 0 ? [empty(t, "Nothing in History yet.")] : d.history.map((r, i) => historyRow(v, t, r, i));
  else if (d.empty) body = [empty(t, "Nothing waits on you.", "What you answered is under History.")];
  else body = d.teams.flatMap((tm) => team(v, t, tm));

  const tab = (id, label, on) => Tab.new(`qa-tab-${id}`).selected(on)
    .h(26).px(10).flex().items_center().rounded(6).cursor_pointer().text_size(12)
    .border(1).border_color(on ? t.raised : t.surface).bg(on ? t.raised : t.surface).text_color(on ? t.foreground : t.muted)
    .hover((s) => s.text_color(t.foreground))
    .focus((s) => s.border_color(t.accent))
    .accessibility_label(label)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => {
      v.tab = id;
      cx.notify();
    })
    .child(label);

  return v_flex().id("root").size_full().min_h(0).bg(t.surface).text_size(12.5)
    .track_focus(v.keys)
    .on_key_down((e, cx) => {
      if (e.keystroke !== "escape") return;
      if (v.tab === "history" && v.histFocus && v.histInput.value() !== "") { // Esc in the search box clears it first
        v.histTok++;
        v.histInput.set_value("");
        histAsk(v, { ...v.hist.filter, search: "" }, cx);
      } else if (v.histMenu) {
        v.histMenu = null;
        cx.notify();
      } else if (v.nnOpen) {
        v.nnOpen = null;
        cx.notify();
      } else if (v.tab === "history" && v.thread) { // a thread steps back to History first, as its ← History does
        v.thread = null;
        cx.notify();
      } else close();
    })
    // the Desk's tab row is the header, in the column and in the Desk window (tabs.js panelTabsView)
    .child(Tabs.new("qa-tabs").h(34).px(16).pt(8).gap(2).flex().flex_none().items_center()
      .child(tab("for", d.count > 0 ? `For you ${d.count}` : "For you", v.tab === "for"))
      .child(tab("history", "History", v.tab === "history")))
    .when(Boolean(v.qa?.pending), (el) => el.child(Button.new("qa-updates")
      .mx(16).mt(8).h(28).px(10).flex().flex_none().items_center().rounded(6).cursor_pointer()
      .border(1).border_color(t.raised).bg(t.raised).text_size(12).text_color(t.foreground)
      .hover((s) => s.bg(t.hover)).focus((s) => s.border_color(t.accent))
      .accessibility_label("Updates available. Show them")
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => {
        qaTake(v, true, cx);
        cx.notify();
      })
      .child("Updates available")))
    .when(v.tab === "history" && !v.thread, (el) => el.child(histControls(v, t)))
    .child(v_flex().id("qa-body").flex_1().min_h(0).px(16).pb(12).when(v.tab === "history", (el) => el.pt(8)).overflow_y_scrollbar().children(body));
}

export function note(t, text, detail) {
  return v_flex().py(12).gap(2)
    .child(div().text_size(12).text_color(t.muted).child(text))
    .when(Boolean(detail), (el) => el.child(div().text_size(11).text_color(t.muted).child(detail)));
}

export function empty(t, text, sub) {
  return v_flex().py(40).gap(4).items_center()
    .child(div().text_size(13).font_medium().text_color(t.foreground).child(text))
    .when(Boolean(sub), (el) => el.child(div().text_size(12).text_color(t.muted).child(sub)));
}

function team(v, t, tm) {
  const out = [h_flex().mt(12).mb(6).h(16).gap(4).items_center().text_size(11).font_semibold().text_color(t.muted)
    .when(Boolean(tm.lane), (el) => el.child(div().text_color(t.dim).child("↳")))
    .child(tm.name)];
  for (const c of tm.cards) out.push(card(v, t, tm, c));
  if (tm.hidden > 0 || tm.truncated) {
    out.push(div().mt(2).text_size(11).text_color(t.muted)
      .child(`+ ${tm.hidden > 0 ? `${tm.hidden} ` : ""}more waiting · ah msg decision list --waiting`));
  }
  return out;
}

// An asker reads `role@team`; under its own team's label the team part says nothing.
const asker = (a, tm) => (a ?? "").replace(new RegExp(`@${tm.name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`), "");

// `compact` (the Board's Waiting on you column): the question, the pick as one chip, Send and More… (the full card in Q&A). A card
// whose pick cannot be sent as it stands (pick any, an item with no options) says how many options it has and opens Q&A instead.
export function card(v, t, tm, c, compact = false) {
  const cs = cardState(v, c);
  const r = cs.reply;
  if (r?.done) {
    // done text on paper raised is 4.30:1, so done colours the ✓ only and the words stay foreground (design-note.md)
    const given = r.answer ?? cs.given; // the core's decision, else what was sent
    const full = `Recorded · ${given}${r.note ? " · Orchestrator not told" : ""}`;
    return h_flex().id(`qa-done-${c.key}`).mb(8).min_h(34).px(12).gap(6).items_center().rounded(8).bg(t.raised).text_size(12)
      .tooltip(full).accessibility_label(full)
      .child(div().flex_none().text_color(t.done).child("✓"))
      .child(div().flex_1().min_w(0).text_color(t.foreground).child(sel(`qa-done-t-${c.key}`, `Recorded · ${given}`)))
      .when(Boolean(r.note), (el) => el.child(div().flex_none().text_color(t.muted).child("· Orchestrator not told")));
  }
  const busy = cs.sending;
  const ok = !busy && canSend(c, cs.pick);
  // ah2-220 Amendment 1: a lane's orchestrator asking reads Sub-Orchestrator (select.js askerLabel); else the asker as today
  const who = askerLabel(v.st.slices, tm.id.slice(0, tm.id.indexOf("/")), tm.name, c.asker)?.label ?? asker(c.asker, tm);
  const head = c.header; // an item's title, a decision's item
  const meta = [tm.name, c.kind === "item" ? `item${who ? ` · ${who}` : ""}` : who, c.multi ? "pick any" : ""].filter(Boolean);
  const label = `${classLabel(c.cls)}. ${head ? `${head}: ` : ""}${c.text}. Asked by ${who || "unknown"}${c.age ? `, ${c.age} ago` : ""}`;

  const chip = (i, text, on, rec) => Button.new(`qa-chip-${c.key}-${i}`).selected(on).disabled(busy)
    .h(24).px(8).gap(4).flex().items_center().rounded(6).cursor_pointer().text_size(11.5)
    .border(1).border_color(t.border).bg(on ? t.selected : t.surface).text_color(t.foreground)
    .when(busy, (el) => el.opacity(0.6))
    .when(!busy, (el) => el.hover((s) => s.border_color(t.muted)))
    .focus((s) => s.border_color(t.accent))
    .accessibility_label(`${text}${rec ? ", recommended" : ""}, ${on ? "selected" : "not selected"}`)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => (i === "other" ? pickOther(v, c, cx, true) : pickChip(v, c, i, cx)))
    .when(on, (el) => el.child(div().text_size(10).text_color(t.accent).child("✓")))
    .child(text)
    .when(rec, (el) => el.child(div().ml(2).text_size(10).text_color(t.muted).child("recommended")));

  const inp = v.inputs.get(c.key);
  const other = cs.pick.other && (inp
    ? h_flex().mt(6).h(28).px(8).items_center().rounded(6).border(1).border_color(inp.focused ? t.accent : t.border).bg(t.surface)
      .on_mouse_down("left", stop)
      .child(Input.new(inp.state).flex_1().h_full().text_size(12).text_color(t.foreground))
    // Other on from the start (an item with no options): the input is made on the first press, not in render
    : Button.new(`qa-other-${c.key}`).mt(6).h(28).px(8).flex().items_center().rounded(6).cursor_text().disabled(busy)
      .border(1).border_color(t.border).bg(t.surface).text_size(12).text_color(t.muted)
      .focus((s) => s.border_color(t.accent))
      .accessibility_label("Type your answer")
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => pickOther(v, c, cx, false))
      .child("Your answer"));

  const nn = Popover.new(`qa-nn-${c.key}`).open(v.nnOpen === c.key)
    .on_open_change((open, cx) => {
      v.nnOpen = open && !cardState(v, c).sending ? c.key : null;
      cx.notify();
    })
    .trigger(Button.new(`qa-nn-btn-${c.key}`).disabled(busy)
      .h(24).px(6).flex().items_center().rounded(6).cursor_pointer().text_size(11.5).text_color(t.muted)
      .border(1).border_color(t.raised)
      .when(busy, (el) => el.opacity(0.6))
      .when(!busy, (el) => el.hover((s) => s.text_color(t.foreground)))
      .focus((s) => s.border_color(t.accent))
      .accessibility_label("Not needed: dismiss this question")
      .on_mouse_down("left", stop)
      .child("Not needed ▾"))
    .content(v_flex().p(4).gap(2).rounded(8).border(1).border_color(t.border).bg(t.surface).shadow_md()
      .children(DISMISS.map((reason, i) => Button.new(`qa-nn-${c.key}-${i}`)
        .h(28).px(8).flex().items_center().rounded(6).cursor_pointer().text_size(12).text_color(t.foreground)
        .border(1).border_color(t.surface)
        .hover((s) => s.bg(t.hover))
        .focus((s) => s.border_color(t.accent))
        .accessibility_label(`Dismiss: ${reason}`)
        .on_mouse_down("left", stop)
        .on_click((_e, cx) => send(v, c, { dismiss: reason }, cx))
        .child(reason))));

  // Send is a primary button; its focus ring is a 1 px accent border one pixel outside the fill, so it shows on the accent itself.
  const sendBtn = Button.new(`qa-send-${c.key}`).disabled(!ok)
    .p(1).flex().rounded(7).cursor_pointer().border(1).border_color(t.raised)
    .focus((s) => s.border_color(t.accent))
    .accessibility_label(busy ? "Sending" : `Send${cs.pick.picked.length || cs.pick.other ? `: ${answerText(c.q, cs.pick) || "Other"}` : ""}`)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => {
      if (canSend(c, cardState(v, c).pick)) send(v, c, { pick: cardState(v, c).pick }, cx);
    })
    .child(div().h(22).px(12).flex().items_center().rounded(6).bg(t.accent).text_size(11.5).font_semibold().text_color(t.surface)
      .when(!ok, (el) => el.opacity(0.45))
      .child(busy ? "Sending…" : "Send"));

  const top = h_flex().mb(4).gap(6).items_center().text_size(11).text_color(t.muted)
    .child(div().flex_none().px(5).rounded(4).border(1).border_color(c.cls === "blocker" ? t.blocked : t.border)
      .when(c.cls === "blocker", (el) => el.text_color(t.blocked).font_semibold()).child(classLabel(c.cls)))
    .when(Boolean(head), (el) => el.child(div().flex_none().max_w(compact ? 96 : 160).truncate().font_family(MONO).text_color(t.foreground).child(head)))
    .child(div().min_w(0).truncate().child(`${head ? "· " : ""}${meta.join(" · ")}`))
    .child(div().flex_1())
    .child(div().flex_none().font_family(MONO).child(c.age));
  const refused = (el) => el.child(h_flex().mt(8).gap(4).items_start().text_size(11.5)
    .child(div().flex_none().text_color(t.blocked).child("⚠"))
    .child(div().flex_1().min_w(0).text_color(t.foreground).child(sel(`qa-ref-${c.key}`, r.text))));

  if (compact) {
    const sendable = busy || canSend(c, cs.pick);
    const picked = cs.pick.picked.map((i) => c.options[i]).join(", ");
    const rec = cs.pick.picked.length === 1 && cs.pick.picked[0] === c.recommended;
    const n = c.options.length + (c.more ?? 0);
    const more = Button.new(`qa-more-${c.key}`)
      .h(24).px(6).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(11.5).text_color(t.muted)
      .border(1).border_color(t.raised)
      .hover((s) => s.text_color(t.foreground))
      .focus((s) => s.border_color(t.accent))
      .accessibility_label(sendable ? "More: every option and Not needed, in Q&A" : `${n} options. Answer in Q&A`)
      .on_mouse_down("left", stop)
      .on_click(() => openView("qa"))
      .child(sendable ? "More…" : `${n} options · Answer in Q&A`);
    return v_flex().id(`qa-card-${c.key}`).mb(8).px(12).py(8).rounded(8).bg(t.raised).border(1).border_color(r ? t.blocked : t.raised)
      .accessibility_label(label)
      .child(top)
      .child(div().text_color(t.foreground).child(sel(`qa-q-${c.key}`, c.text)))
      .when(Boolean(r), refused)
      .child(h_flex().mt(8).gap(6).items_center()
        // the pick, shown as the chip it is in Q&A; it changes there (More…), so here it is not a control
        .when(sendable, (el) => el.child(h_flex().h(24).px(8).gap(4).min_w(0).flex_shrink().items_center().rounded(6).text_size(11.5)
          .border(1).border_color(t.border).bg(t.selected).text_color(t.foreground)
          .tooltip(`${picked || "Other"}${rec ? ", recommended" : ""}`)
          .child(div().flex_none().text_size(10).text_color(t.accent).child("✓"))
          .child(div().min_w(0).truncate().child(picked || "Other"))))
        .when(sendable, (el) => el.child(sendBtn))
        .child(more));
  }

  return v_flex().id(`qa-card-${c.key}`).mb(8).px(12).py(10).rounded(8).bg(t.raised).border(1).border_color(r ? t.blocked : t.raised)
    .accessibility_label(label)
    .child(top)
    .child(div().mb(8).text_color(t.foreground).child(sel(`qa-q-${c.key}`, c.text)))
    .when(Boolean(c.desc), (el) => el.child(description(v, t, c)))
    .child(h_flex().flex_wrap().gap(6).items_center()
      .children(c.options.map((o, i) => chip(i, o, cs.pick.picked.includes(i), i === c.recommended)))
      .child(chip("other", "Other…", cs.pick.other, false)))
    .when(Boolean(other), (el) => el.child(other))
    .when(c.more > 0, (el) => el.child(div().mt(6).text_size(11).text_color(t.muted).child(`+ ${c.more} more options · answer in the web Inbox`)))
    .when(Boolean(r), refused)
    .child(h_flex().mt(8).gap(8).items_center().child(sendBtn).child(nn));
}

// A waiting item's description under its note, folded to about four lines with More. The text is drawn plain (marks dropped) and
// selectable; each `code` span of a shown block (a path, a command) is also a chip under it that copies it whole in one click.
function description(v, t, c) {
  const open = v.descOpen.has(c.key);
  const all = descriptionBlocks(c.desc);
  const fold = descFold(all);
  const plain = (text) => inlineParts(text).map((p) => p.text).join("");
  const rows = (b) => (b.kind === "list" || b.kind === "olist" ? b.items : [b.text]);
  const block = (b, i) => {
    const codes = b.kind === "code" ? [] : rows(b).flatMap((r) => inlineParts(r).filter((p) => p.kind === "code").map((p) => p.text));
    const id = `qa-d-${c.key}-${i}`;
    const body = b.kind === "head" ? div().mt(i > 0 ? 4 : 0).font_semibold().child(sel(id, b.text))
      : b.kind === "code" ? copyChip(v, t, c, b.text, `${i}`).px(8).py(6).font_family(MONO).text_size(11.5)
      : b.kind === "para" ? div().child(sel(id, plain(b.text)))
      : v_flex().gap(2).children(b.items.map((item, j) => h_flex().gap(6).items_start()
        .child(div().flex_none().text_color(t.muted).child(b.kind === "olist" ? `${j + 1}.` : "•"))
        .child(div().flex_1().min_w(0).child(sel(`${id}-${j}`, plain(item))))));
    return codes.length === 0 ? body : v_flex().gap(4).child(body)
      .child(h_flex().flex_wrap().gap(4).children(codes.map((text, j) => copyChip(v, t, c, text, `${i}-${j}`).h(22).px(6).items_center().font_family(MONO).text_size(11))));
  };
  return v_flex().mb(8).gap(4).text_size(12).text_color(t.foreground)
    .children((open ? all : fold.blocks).map(block))
    .when(fold.cut, (el) => el.child(Button.new(`qa-desc-more-${c.key}`)
      .h(24).px(6).flex().items_center().rounded(6).cursor_pointer().text_size(11.5).text_color(t.muted)
      .border(1).border_color(t.raised)
      .hover((s) => s.text_color(t.foreground))
      .focus((s) => s.border_color(t.accent))
      .accessibility_label(open ? "Less: fold the description" : "More: the whole description")
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => {
        open ? v.descOpen.delete(c.key) : v.descOpen.add(c.key);
        cx.notify();
      })
      .child(open ? "Less" : "More")));
}

// A code span or block that copies its text to the clipboard on a click (gpui-kit's clipboard write: no process, every platform).
function copyChip(v, t, c, text, id) {
  const done = v.copied === `${c.key}|${text}`;
  return Button.new(`qa-copy-${c.key}-${id}`).flex().rounded(6).cursor_pointer()
    .border(1).border_color(t.border).bg(t.surface).text_color(t.foreground)
    .hover((s) => s.border_color(t.muted))
    .focus((s) => s.border_color(t.accent))
    .tooltip(done ? "Copied" : "Copy")
    .accessibility_label(`${done ? "Copied" : "Copy"}: ${text}`)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => {
      v.copied = `${c.key}|${text}`;
      cx.notify();
      cx.write_to_clipboard(text);
    })
    .child(div().min_w(0).child(done ? `✓ ${text}` : text));
}

function pickChip(v, c, i, cx) {
  const cs = cardState(v, c);
  v.cards.set(c.key, { ...cs, pick: toggle(c.q, cs.pick, i) });
  cx.notify();
}

export const clock = (at) => {
  const d = new Date(at);
  return Number.isFinite(at) ? `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}` : "";
};
const BY = { user: "you" };

// A question-log row's whole thread, read on its click (never on render). The History body scrolls, so the panel does too.
function openThread(v, r, cx) {
  v.thread = { row: r, loading: true };
  cx.notify();
  cx.spawn(async (cx) => {
    const sleep = (ms) => cx.sleep(ms);
    const got = await readThread(ah().exec, r, sleep, v.link ? (path, timeout) => v.link.read(path, timeout, sleep) : null);
    if (v.thread?.row === r) v.thread = { row: r, loading: false, ...got };
    cx.notify();
  });
}

// A thread's folded part (More from this turn, Later replies, one later reply's whole text): a disclosure button. The keys pressed
// are kept on the open thread (made in the click, never in render); a press flips the fold from how it started (threadStart).
// Enter and Space press it as a click does. › closed and ▾ open are the mod's own glyphs (graph-view.js's path, Not needed ▾).
function fold(v, t, key, label, open) {
  return Button.new(`qa-th-fold-${key}`).mt(10).h(24).px(8).gap(6).flex().items_center().rounded(6).cursor_pointer().text_size(11.5).text_color(t.muted)
    .border(1).border_color(t.surface)
    .hover((s) => s.text_color(t.foreground))
    .focus((s) => s.border_color(t.accent))
    .accessibility_label(`${label}, ${open ? "shown. Hide it" : "hidden. Show it"}`)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => {
      const o = (v.thread.open ??= new Set());
      o.has(key) ? o.delete(key) : o.add(key);
      cx.notify();
    })
    .child(div().w(10).flex_none().child(open ? "▾" : "›"))
    .child(label);
}

// The question; its reply in full (the direct reply, else the turn's final); the final reply when it differs; then, folded, the rest
// of that turn and the later turns' replies, each shown by its final (else the head of its text) and opened to its whole text.
function threadPanel(v, t) {
  const th = v.thread;
  const start = threadStart(th);
  const open = (key) => (th.open?.has(key) ?? false) !== (key === "later" && start.laterOpen);
  // each text keeps its own id, so opening a fold never moves text under another one's id (a live selection stays where it was)
  const para = (id, text, color = t.foreground) => div().text_size(12).text_color(color).child(sel(`qa-th-${id}`, text));
  const label = (text, top = 10) => div().mt(top).text_size(11).text_color(t.muted).child(text);
  const out = [Button.new("qa-thread-back").mt(8).mb(8).h(24).px(8).flex().items_center().rounded(6).cursor_pointer().text_size(12).text_color(t.muted)
    .border(1).border_color(t.surface)
    .hover((s) => s.text_color(t.foreground))
    .focus((s) => s.border_color(t.accent))
    .accessibility_label("Back to History")
    .on_click((_e, cx) => {
      v.thread = null;
      cx.notify();
    })
    .child("← History")];
  out.push(para("q", th.text ?? th.row.text, t.foreground));
  if (th.loading) out.push(para("note", "Loading the thread…", t.muted));
  else if (th.error) out.push(para("note", th.error, t.muted));
  else {
    const later = th.later ?? [];
    if (th.reply) out.push(label(`${th.replyFinal ? "Final reply" : "Reply"} ${clock(th.replyAt)}`), para("reply", th.reply));
    else out.push(label(start.note));
    if (th.final) out.push(label(`Final reply ${clock(th.finalAt)}`), para("final", th.final));
    // what a fold shows hangs under it on a rule, so it reads as that fold's and not as the reply
    const under = (children) => v_flex().mt(4).ml(12).pl(10).border_l(1).border_color(t.border).children(children);
    if (th.more) {
      out.push(fold(v, t, "more", "More from this turn", open("more")));
      if (open("more")) out.push(under([para("more", th.more)]));
    }
    if (later.length > 0) {
      out.push(fold(v, t, "later", `Later replies (${later.length})`, open("later")));
      if (open("later")) {
        out.push(under(later.flatMap((l, i) => {
          const head = descFold([{ kind: "para", text: l.text }]);
          const shown = l.final ?? head.blocks[0]?.text ?? "";
          const whole = open(`l${i}`);
          return [label(clock(l.at), i === 0 ? 0 : 10), para(`l${i}`, whole ? l.text : shown),
            ...(shown.trim() !== l.text.trim() ? [fold(v, t, `l${i}`, "Whole reply", whole)] : [])];
        })));
      }
    }
  }
  return out;
}

// History's control row (spec D2): the search box, then Team, Time and State menus (board.js's owner menu), and Clear while any is
// set. Tab order is the row's order, then the list.
function histControls(v, t) {
  const f = v.hist.filter;
  const on = filterActive(f);
  const pick = (k, x) => (_e, cx) => histAsk(v, { ...v.hist.filter, [k]: x }, cx);
  const teams = [...new Set(historyTeams(v.st.slices).map((tm) => tm.name))];
  const menu = (key, label, opts, now, k) => {
    const shown = opts.find(([x]) => x === now)?.[1] ?? String(now);
    const dflt = now === opts[0][0];
    return Popover.new(`qa-hf-${key}`).open(v.histMenu === key)
      .on_open_change((open, cx) => {
        v.histMenu = open ? key : null;
        cx.notify();
      })
      .trigger(Button.new(`qa-hf-${key}-btn`)
        .h(24).px(8).gap(4).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(11.5)
        .border(1).border_color(t.border).bg(dflt ? t.surface : t.selected).text_color(dflt ? t.muted : t.foreground)
        .hover((s) => s.border_color(t.muted).text_color(t.foreground))
        .focus((s) => s.border_color(t.accent))
        .accessibility_label(`${label}: ${shown}. Choose ${label.toLowerCase()}`)
        .on_mouse_down("left", stop)
        .child(div().max_w(140).truncate().child(`${key === "state" ? "State: " : ""}${shown} ▾`)))
      .content(v_flex().id(`qa-hf-${key}-list`).p(4).gap(2).max_h(320).overflow_y_scrollbar().rounded(8).border(1).border_color(t.border).bg(t.surface).shadow_md()
        .children(opts.map(([x, text], i) => Button.new(`qa-hf-${key}-${i}`).selected(x === now)
          .h(28).px(8).gap(6).flex().items_center().rounded(6).cursor_pointer().text_size(12).text_color(t.foreground)
          .border(1).border_color(t.surface)
          .hover((s) => s.bg(t.hover))
          .focus((s) => s.border_color(t.accent))
          .accessibility_label(`${text}${x === now ? ", selected" : ""}`)
          .on_mouse_down("left", stop)
          .on_click(pick(k, x))
          .child(div().w(10).flex_none().text_size(10).text_color(t.accent).child(x === now ? "✓" : ""))
          .child(text))));
  };
  return h_flex().id("qa-hf").h(40).px(16).gap(6).flex_none().items_center().border_b(1).border_color(t.border)
    .child(h_flex().h(24).px(8).flex_1().min_w(120).items_center().rounded(6).border(1).border_color(v.histFocus ? t.accent : t.border).bg(t.surface)
      .accessibility_label("Search questions and replies")
      .on_mouse_down("left", stop)
      .child(Input.new(v.histInput).flex_1().h_full().text_size(12).text_color(t.foreground)))
    .child(menu("team", "Team", [[null, "All teams"], ...teams.map((n) => [n, n])], f.team, "team"))
    .child(menu("time", "Time", TIMES, f.time, "time"))
    .child(menu("state", "State", STATES, f.state, "state"))
    .when(on, (el) => el.child(Button.new("qa-hf-clear")
      .h(24).px(8).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(11.5).text_color(t.muted)
      .border(1).border_color(t.surface)
      .hover((s) => s.text_color(t.foreground))
      .focus((s) => s.border_color(t.accent))
      .accessibility_label("Clear the search and filters")
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => {
        v.histTok++;
        v.histInput.set_value("");
        histAsk(v, FILTER0, cx);
      })
      .child("Clear")));
}

// A filtered History (spec A2, A3): the scope line, a failed read's note over the rows it kept, the rows, and Load more.
function histBody(v, t) {
  const h = v.hist;
  const out = [div().pb(4).text_size(11).text_color(t.muted).child(SCOPE)];
  if (h.error) out.push(note(t, h.error));
  if (h.rows.length === 0) {
    if (h.loading) out.push(note(t, "Searching…"));
    else if (!h.error) out.push(empty(t, NO_MATCH));
    return out;
  }
  out.push(...h.rows.map((r, i) => historyRow(v, t, r, i)));
  if (hasMore(h) || (h.loading && h.more)) {
    out.push(Button.new("qa-hf-more").mt(8).h(28).px(10).flex().flex_none().items_center().self_start().rounded(6).cursor_pointer()
      .border(1).border_color(t.raised).bg(t.raised).text_size(12).text_color(t.foreground).disabled(h.loading)
      .when(!h.loading, (el) => el.hover((s) => s.bg(t.hover)))
      .focus((s) => s.border_color(t.accent))
      .accessibility_label(h.loading ? "Loading more" : "Load more questions")
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => {
        if (v.hist.loading) return;
        v.hist = historyMore(v.hist);
        cx.notify();
        histFetch(v, cx);
      })
      .child(h.loading ? "Loading…" : "Load more"));
  }
  return out;
}

function historyRow(v, t, r, i) {
  // a question-log row: who asked (DIRECTION's label) in the by column; a typed prompt also shows its team
  const log = r.dir === "jim" || r.dir === "terminal";
  const by = log ? DIRECTION[r.dir] : BY[r.by] ?? r.by ?? "";
  const row = r;
  // a typed question's chip is its first reply's time, a quiet "no reply yet" when it has none (a thread whose only replies came in
  // later turns counts them), and under its question the first reply's head on one line, when the slice row carries one
  const waiting = r.dir === "jim" && !r.replyAt && !(r.replies > 0);
  if (r.dir === "jim") {
    r = { ...r, text: r.question ? `? ${r.text}` : r.text,
      answer: r.replyAt ? `replied ${clock(r.replyAt)}` : r.replies > 0 ? `${r.replies} later ${r.replies === 1 ? "reply" : "replies"}` : "no reply yet" };
  }
  const preview = r.dir === "jim" ? r.preview : null;
  const full = `${r.team} · ${r.header ? `${r.header} · ` : ""}${r.text} · ${log ? "" : "answered "}${r.answer}${by ? ` · ${by}` : ""}${r.age ? ` · ${r.age} ago` : ""}${preview ? `. Reply: ${preview}` : ""}`;
  // a row that opens its thread is a button, so the keyboard reaches it; its focus is the hover fill with an accent rule under it
  const open = row.dir === "jim";
  return (open ? Button.new(`qa-h-${i}`).flex() : h_flex().id(`qa-h-${i}`)).min_h(32).py(6).gap(8).items_center().border_b(1).border_color(t.border).text_size(12)
    .tooltip(full).accessibility_label(open ? `${full}. Open its thread` : full)
    .when(open, (el) => el.cursor_pointer().hover((s) => s.bg(t.raised)).focus((s) => s.bg(t.raised).border_color(t.accent))
      .on_click((_e, cx) => openThread(v, row, cx)))
    .child(div().w(38).flex_none().font_family(MONO).text_size(11).text_color(t.muted).child(clock(r.at)))
    .when(open, (el) => el.child(div().flex_none().max_w(96).truncate().font_family(MONO).text_size(11).text_color(t.muted).child(r.team)))
    .when(Boolean(r.header), (el) => el.child(div().flex_none().max_w(96).truncate().font_family(MONO).text_size(11).text_color(t.muted).child(r.header)))
    // a row that opens its thread stays one line, and its reply's preview a second muted one under the question, as the sidebar's
    // team row carries its goal (main.js); its thread panel is selectable. The others' question and answer are selectable.
    .child(!open ? div().flex_1().min_w(0).text_color(t.foreground).child(sel(`qa-h-${i}-q`, r.text))
      : preview ? v_flex().flex_1().min_w(0).text_color(t.foreground)
        .child(div().min_w(0).truncate().child(r.text))
        .child(div().min_w(0).truncate().text_size(11.5).text_color(t.muted).child(preview))
      : div().flex_1().min_w(0).text_color(t.foreground).truncate().child(r.text))
    .child(div().flex_none().max_w(140).min_h(20).px(8).flex().items_center().rounded(6).border(1).border_color(t.border).bg(t.surface)
      .text_size(11.5).text_color(waiting ? t.muted : t.foreground).child(open ? div().truncate().child(r.answer) : sel(`qa-h-${i}-a`, r.answer)))
    .child(div().w(72).flex_none().text_right().text_size(11).text_color(t.muted).child(by));
}
