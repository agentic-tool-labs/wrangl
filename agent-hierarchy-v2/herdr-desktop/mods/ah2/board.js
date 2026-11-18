// ah2 in Herdr: the Board (props {view:"board"}): the web Board's state columns over every running team, as a kanban. It is a tab
// of the Desk, in the ah2 side column or the Desk window (dock.js). Colours are theme() tokens only.
// The data is board-model.js's. A card waiting on you is the Q&A window's own card, compact (qa.js card): its Send is the only thing
// here that sends, through model.js sendAnswer, and only from its on_click. A plan card opens the item sheet: its owner, Focus
// terminal and the item's log, read once on the click.

import { div, svg } from "gpui-kit";
import { v_flex, h_flex, Button, Popover, Input, InputState, Link } from "gpui-base";
import { run, props, terminals, shared, setShared } from "herdr";
import { loadTrack, saveTrack } from "./plans-seen.js";
import { ah, target } from "./ah.js";
import { openView, isDocked } from "./dock.js";
import { boardModel, trackAfter, nothingChanged, ownerOf, readItemLog, workLog, whenOf, LOG_PAGE, WAVE_CAP } from "./board-model.js";
import { streamName, inlineParts } from "./plans-model.js";
import { loadingOf, terminalFinder } from "./model.js";
import { card, note, empty, clock } from "./qa.js";
import { markCell } from "./marks.js";

const MONO = "Geist Mono";
const COL_W = 184; // six columns fit about 1200 px; narrower, the board scrolls sideways
const SHEET_W = 300;
// The one sheet frame (the Board's item, backlog and group sheets, and the Graph's agent sheet, graph-view.js): SHEET_W beside the
// content, or the whole column when docked (`full`).
export const sheetColumn = (t, full, id) => v_flex().id(id).when(full, (el) => el.w_full()).when(!full, (el) => el.w(SHEET_W).border_l(1).border_color(t.border))
  .flex_none().h_full().min_h(0).bg(t.surface);
const STRIP_W = 40; // an empty column, as the web's 40px strip
const META_W = COL_W - 26; // the widest a card's chip or text runs: the column less the card's padding and border

const stop = (_e, cx) => cx.stop_propagation(); // a press on a control must not drag the window

// The board's own state, made on first draw: the filters, the change baseline (board-model marker) and the open item sheet.
// `open`: quiet team ids expanded (main.js subscribes their plan and items); `expand`: column keys drawn whole.
const stateOf = (v) => (v.kb ??= { link: () => v.link, hide: new Set(), owner: "", blockedOnly: false, changesOnly: false, showFinished: false, open: new Set(), expand: new Set(), blFold: false, waveAll: new Set(), team: null, lane: null, epic: null, deliv: null, crumbOpen: false, only: null, track: loadTrack(Date.now(), STORE), trackRev: 0, handles: null, ownerOpen: false, sel: null, log: null });

// What the viewer has seen, kept across restarts (D1): plans-seen.js reads and writes it through this, Herdr's persisted shared
// store, the mod's settings store (settings.js), under the web's own keys.
const STORE = { getItem: (k) => (typeof shared(k) === "string" ? shared(k) : null), setItem: (k, val) => setShared(k, val) };

// The visit ends (main.js: the Board tab left, or its window closed or Herdr quit): store what it held, so the next visit's marks
// are what changed since. This visit's own baseline stays.
export function boardLeave(v) {
  const f = v.kb;
  if (f?.track && f.memo) saveTrack(trackAfter(f.track, f.memo.m.covered, v.st.slices, Date.now()), STORE);
}

// The Filter streams field's state: made once with the view (main.js), never in render.
export function boardInit(v) {
  v.kbInput = InputState.new({ placeholder: "Filter streams" });
  v.kbInput.on("change", (_e, cx) => cx.notify());
  v.kbInput.on("focus", (_e, cx) => {
    v.kbFocus = true;
    cx.notify();
  });
  v.kbInput.on("blur", (_e, cx) => {
    v.kbFocus = false;
    cx.notify();
  });
}

export function boardView(v, t, close) {
  const f = stateOf(v);
  const narrow = props()?.dock === true; // the side column: the sheet takes the column's width instead of sitting beside the cards
  // boardModel only when an input changed (as graph-view.js graphOf): the slices (v.sliceGen, bumped by the poll), the filters
  // and the half minute (ages). Herdr redraws every mod on any live change, and a render has 50 ms.
  f.query = v.kbInput?.value() ?? "";
  const key = [v.sliceGen, f.owner, f.blockedOnly, f.changesOnly, f.showFinished, f.query, [...f.hide].join("|"), [...f.open].join("|"), [...f.expand].join("|"), f.blFold, [...f.waveAll].join("|"), f.team, f.lane, f.epic, f.deliv, f.trackRev, f.only?.team, f.only?.stream, Math.floor(Date.now() / 30000)];
  if (!f.memo || key.some((k, i) => k !== f.memo.key[i])) f.memo = { key, m: boardModel(v.st.slices, Date.now(), f) };
  const m = f.memo.m;
  // Loading until a reply has answered the whole sub set once: plans join it after the teams, items after the plans.
  const loading = loadingOf(v, m.loaded);
  let body;
  if (v.status === "down") body = div().px(16).child(note(t, v.reason, "Retrying"));
  else if (loading) body = div().px(16).child(note(t, "Loading board…"));
  else if (f.sel && narrow) body = sheet(v, t, f, true);
  else {
    const cards = m.empty
      ? (f.changesOnly && !f.query.trim() ? empty(t, nothingChanged(m.since, Date.now()), "Change the filters in the row above.")
        : m.filtered ? empty(t, "Nothing matches these filters.", "Change them in the row above.") : empty(t, "Nothing on the board.", "Running teams have no plan items today."))
      : h_flex().id("kb").size_full().min_h(0).gap(12).items_start().overflow_x_scrollbar().child(backlogCol(t, f, m.backlog)).children(m.columns.map((c) => column(v, t, f, c)));
    body = h_flex().size_full().min_h(0)
      .child(v_flex().flex_1().min_w(0).h_full().min_h(0)
        .child(filters(v, t, f, m))
        .when(Boolean(m.laneHead), (el) => el.child(laneHeadRow(t, m.laneHead)))
        .when(m.stale.length + m.unstale.length > 0, (el) => el.child(staleRow(t, f, m.stale, m.unstale)))
        .child(div().id("kb-cols").flex_1().min_h(0).px(16).pt(8).pb(12).child(cards)))
      .when(Boolean(f.sel), (el) => el.child(sheet(v, t, f, false)));
  }

  return v_flex().id("root").size_full().min_h(0).bg(t.surface).text_size(12.5)
    .track_focus(v.keys)
    .on_key_down((e, cx) => {
      if (["up", "down", "left", "right"].includes(e.keystroke)) return arrow(v, f, e.keystroke, cx);
      if (e.keystroke !== "escape") return;
      if (v.kbFocus && f.query !== "") v.kbInput.set_value(""); // Esc in the filter field clears it first
      else if (v.nnOpen || f.ownerOpen || f.crumbOpen) {
        v.nnOpen = null;
        f.ownerOpen = false;
        f.crumbOpen = false;
      } else if (f.sel) f.sel = null;
      else if (props()?.overlay != null || isDocked()) return close(); // a Board herdr tab left from before atp-1728 has nothing to close
      cx.notify();
    })
    // the Desk's tab row is the header, in the column and in the Desk window (tabs.js panelTabsView)
    .child(div().id("kb-body").flex_1().min_h(0).child(body));
}

// A two-state chip: ✓ and the fill when on. Its label says the state in words too.
function chip(t, id, text, on, said, flip, tip) {
  return Button.new(id).selected(on)
    .h(24).px(8).gap(4).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(11.5)
    .border(1).border_color(t.border).bg(on ? t.selected : t.surface).text_color(on ? t.foreground : t.muted)
    .hover((s) => s.border_color(t.muted).text_color(t.foreground))
    .focus((s) => s.border_color(t.accent))
    .when(Boolean(tip), (el) => el.tooltip(tip))
    .accessibility_label(`${said}, ${on ? "on" : "off"}`)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => {
      flip();
      cx.notify();
    })
    .when(on, (el) => el.child(div().text_size(10).text_color(t.accent).child("✓")))
    .child(text);
}

// The filter row: the Filter streams field, Blocked only and Changes only (boardOf's own filters), the owner menu, the web's
// older streams toggle when there are any, then a chip per running team and lane.
function filters(v, t, f, m) {
  const pickOwner = (o) => (_e, cx) => {
    f.owner = o;
    f.ownerOpen = false;
    cx.notify();
  };
  const ownerItem = (o, i) => Button.new(`kb-owner-${i}`).selected(f.owner === o)
    .h(28).px(8).gap(6).flex().items_center().rounded(6).cursor_pointer().text_size(12).text_color(t.foreground)
    .border(1).border_color(t.surface)
    .hover((s) => s.bg(t.hover))
    .focus((s) => s.border_color(t.accent))
    .accessibility_label(`${o || "Any owner"}${f.owner === o ? ", selected" : ""}`)
    .on_mouse_down("left", stop)
    .on_click(pickOwner(o))
    .child(div().w(10).flex_none().text_size(10).text_color(t.accent).child(f.owner === o ? "✓" : ""))
    .child(o || "Any owner");
  const owner = Popover.new("kb-owner").open(f.ownerOpen)
    .on_open_change((open, cx) => {
      f.ownerOpen = open;
      cx.notify();
    })
    .trigger(Button.new("kb-owner-btn")
      .h(24).px(8).gap(4).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(11.5)
      .border(1).border_color(t.border).bg(f.owner ? t.selected : t.surface).text_color(f.owner ? t.foreground : t.muted)
      .hover((s) => s.border_color(t.muted).text_color(t.foreground))
      .focus((s) => s.border_color(t.accent))
      .accessibility_label(`Owner: ${f.owner || "any"}. Choose an owner`)
      .on_mouse_down("left", stop)
      .child(`Owner: ${f.owner || "any"} ▾`))
    .content(v_flex().id("kb-owners").p(4).gap(2).max_h(320).overflow_y_scrollbar().rounded(8).border(1).border_color(t.border).bg(t.surface).shadow_md()
      .children(["", ...m.owners].map(ownerItem)));
  const flip = (k) => () => (f[k] = !f[k]);
  // the two toggles first: a narrow board scrolls this row sideways, and the team chips are the part that can run long
  const older = m.showFinished ? "Hide older streams" : `Show ${m.agedCount} older ${m.agedCount === 1 ? "stream" : "streams"}`;
  return h_flex().id("kb-filters").h(40).px(16).gap(6).flex_none().items_center().border_b(1).border_color(t.border).overflow_x_scrollbar()
    .child(crumbsRow(t, f, m))
    .when(Boolean(m.stream), (el) => el.child(Button.new("kb-stream").selected(true)
      .h(24).px(8).gap(4).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(11.5)
      .border(1).border_color(t.border).bg(t.selected).text_color(t.foreground)
      .hover((s) => s.border_color(t.muted)).focus((s) => s.border_color(t.accent))
      .accessibility_label(`Showing stream ${m.stream} only. Clear the stream filter`)
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => {
        f.only = null;
        cx.notify();
      })
      .child(`Stream: ${m.stream}`).child(div().text_color(t.muted).child("×"))))
    .child(div().w(1).h(16).mx(4).flex_none().bg(t.border))
    .when(Boolean(v.kbInput), (el) => el.child(h_flex().h(24).px(8).w(160).flex_none().items_center().rounded(6).border(1).border_color(v.kbFocus ? t.accent : t.border).bg(t.surface)
      .accessibility_label("Filter streams")
      .on_mouse_down("left", stop)
      .child(Input.new(v.kbInput).flex_1().h_full().text_size(12).text_color(t.foreground))))
    .child(chip(t, "kb-blocked", "Blocked only", f.blockedOnly, "Blocked only", flip("blockedOnly"), "Only items stuck on someone, and what waits on you"))
    .child(chip(t, "kb-changes", "Changed since last visit", f.changesOnly, "Changed since last visit", flip("changesOnly"), "Only what changed since your last visit"))
    .when(f.changesOnly && m.anyMarked, (el) => el.child(textBtn(t, "kb-seen", "Mark all seen", "Mark all seen: clear every change mark", (cx) => {
      saveTrack(trackAfter(f.track, m.covered, v.st.slices, Date.now(), true), STORE);
      f.trackRev++;
      cx.notify();
    })))
    .child(owner)
    .when(m.agedCount > 0 || m.showFinished, (el) => el.child(chip(t, "kb-older", older, m.showFinished, older, flip("showFinished"), "Streams done before today, or empty and idle for a day")))
    .child(div().w(1).h(16).mx(4).flex_none().bg(t.border))
    .children(m.teams.map((tm) => chip(t, `kb-team-${tm.key}`, `${tm.lane ? "↳ " : ""}${tm.name}`, tm.on, `Show ${tm.name}${tm.lane ? " (lane)" : ""}`, () => {
      if (tm.on) f.hide.add(tm.key);
      else f.hide.delete(tm.key);
    })));
}

// The quiet teams: one line, a Show per folded team (the web's icicle board line) and a Hide per team shown again (the web
// List's "▾ hide"). Showing one subscribes its plan and items; hiding it drops them.
function staleRow(t, f, stale, unstale) {
  const flip = (id, on) => (cx) => {
    if (on) f.open.add(id);
    else f.open.delete(id);
    cx.notify();
  };
  return h_flex().id("kb-stale").min_h(32).px(16).gap(6).flex_none().flex_wrap().items_center().border_b(1).border_color(t.border).text_size(11.5).text_color(t.muted)
    .when(stale.length > 0, (el) => el.child(`${stale.length} stale ${stale.length === 1 ? "team" : "teams"} not shown:`))
    .children(stale.map((s) => textBtn(t, `kb-stale-${s.id}`, `Show ${s.name}`, `Show ${s.name}, a stale team`, flip(s.id, true))))
    .children(unstale.flatMap((s) => [
      ...(s.loading ? [div().flex_none().child(`Loading ${s.name}…`)] : []),
      textBtn(t, `kb-unstale-${s.id}`, `▾ Hide ${s.name}`, `Hide ${s.name}, a stale team`, flip(s.id, false)),
    ]));
}

// A small text button: Show all, Show fewer, + N more, Show <team>.
const textBtn = (t, id, text, label, act) => Button.new(id)
  .h(24).px(6).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(11).text_color(t.muted)
  .border(1).border_color(t.raised)
  .hover((s) => s.bg(t.hover).text_color(t.foreground))
  .focus((s) => s.border_color(t.accent))
  .accessibility_label(label)
  .on_mouse_down("left", stop)
  .on_click((_e, cx) => act(cx))
  .child(text);

function column(v, t, f, col) {
  const capped = col.cards.length < col.count;
  const label = capped ? `${col.label}, ${col.cards.length} of ${col.count} shown` : `${col.label}, ${col.count} ${col.count === 1 ? "card" : "cards"}`;
  if (col.count === 0) return strip(v_flex().id(`kb-col-${col.key}`), t, col.label, 0, `${col.label}, nothing here`);
  const flip = (cx) => {
    if (col.open) f.expand.delete(col.key);
    else f.expand.add(col.key);
    cx.notify();
  };
  const more = col.open ? "Show fewer" : col.key === "done" ? `Show all ${col.count} done today` : `+ ${col.more} more`;
  return v_flex().id(`kb-col-${col.key}`).flex_1().min_w(col.key === "waiting" ? COL_W + 64 : COL_W).h_full().min_h(0)
    .child(h_flex().h(28).gap(6).flex_none().items_center().text_size(11).font_semibold().text_color(t.muted)
      .accessibility_label(label)
      .child(col.label)
      .child(div().font_family(MONO).font_medium().child(capped ? `${col.cards.length} of ${col.count}` : String(col.head))))
    .child(v_flex().id(`kb-cards-${col.key}`).flex_1().min_h(0).overflow_y_scrollbar()
      .children(col.cards.map((c, i) => (c.qa ? card(v, t, c.tm, c.qa, true) : item(t, f, col.key, c, i))))
      .when(col.folds, (el) => el.child(h_flex().pb(4).child(textBtn(t, `kb-more-${col.key}`, more, col.open ? `Show fewer ${col.label} cards` : `${more}, ${col.label}`, flip)))));
}

// Open a card's sheet (the same card again closes it) and read its log once.
function select(f, sel, cx) {
  if (f.sel?.key === sel.key) {
    f.sel = null;
    cx.notify();
    return;
  }
  f.sel = sel;
  f.log = null;
  f.logAll = false; // the timeline's Show all
  f.bodies = new Set(); // the timeline entries whose Details are open
  cx.notify();
  if (sel.bl || sel.group) return; // a backlog row, team or lane has no plan-changes history (the web's panel reads none)
  cx.spawn(async (cx) => {
    const sleep = (ms) => cx.sleep(ms);
    const link = f.link?.();
    const got = await readItemLog(ah().exec, sel, sleep, link ? (path, timeout) => link.read(path, timeout, sleep) : null);
    if (f.sel === sel) f.log = got;
    cx.notify();
  });
}

// A plain card: the title (it wraps) with the change dot, then the web's meta in its order (work id, team, owner, size, stream /
// item), wrapping, then the markers (blocked, waiting on the Orchestrator, blocked by N open) or the way into Q&A. A plan item's
// card is a button that opens its sheet. A missing work id, size or blocker draws nothing.
const metaChip = (t, text, tip, mono) => div().h(18).px(6).flex().flex_none().items_center().max_w(META_W).rounded(4).border(1).border_color(t.border)
  .when(mono, (el) => el.font_family(MONO)).tooltip(tip).child(div().min_w(0).truncate().child(text));
const metaText = (text, mono) => div().flex_none().max_w(META_W).truncate().when(mono, (el) => el.font_family(MONO)).child(text);
// `attn`: the glyph in the blocked tone (a stalled queue item); the words say the state either way
const flagRow = (t, glyph, text, tip, attn = false) => h_flex().gap(4).items_center().text_size(11).tooltip(tip)
  .child(div().flex_none().text_color(attn ? t.blocked : t.muted).child(glyph))
  .child(div().min_w(0).truncate().text_color(t.foreground).child(text));
function item(t, f, col, c, i) {
  const by = c.blockedOpen > 0 ? `blocked by ${c.blockedOpen}` : "";
  const full = [c.title, c.workId, c.ref, c.team, c.owner && `owner ${c.ownerTitle}`, c.epic && `epic ${c.epic.name}`, c.size && `size ${c.size}`, c.msgs > 0 && `${c.msgs} ${c.msgs === 1 ? "msg" : "msgs"}`, c.blocked, c.queue?.text, c.orch && "waiting on the Orchestrator", by, c.mark].filter(Boolean).join(" · ");
  const on = f.sel?.key === c.key;
  const opens = Boolean(c.sel) && !c.ask;
  const inner = v_flex().flex_1().min_w(0).gap(4)
    .child(h_flex().gap(6).items_start()
      .child(div().flex_1().min_w(0).text_color(t.foreground).child(c.title))
      // the change dot: the shape says "changed"; what changed is in the tooltip and the label
      .when(Boolean(c.mark), (el) => el.child(div().mt(5).size(6).flex_none().rounded(3).bg(t.accent))))
    .child(h_flex().flex_wrap().gap(6).items_center().text_size(11).text_color(t.muted)
      // done colours the ✓ only, as in Q&A: done text on paper raised is 4.30:1
      .when(c.done, (el) => el.child(div().flex_none().text_color(t.done).child("✓")))
      .when(Boolean(c.workId), (el) => el.child(metaChip(t, c.workId, `Work id ${c.workId}`, true)))
      .when(Boolean(c.where?.name ?? c.team), (el) => el.child(metaText(c.where?.name ?? c.team).tooltip(c.where?.tip ?? `team ${c.team}`)))
      .when(Boolean(c.epic), (el) => el.child(metaText(c.epic.name).tooltip(`${c.epic.name} (${c.epic.tip})`)))
      .when(Boolean(c.owner), (el) => el.child(metaText(c.ownerLabel)))
      .when(Boolean(c.size), (el) => el.child(metaChip(t, c.size, c.sizeFrom ? `Size ${c.size}, from deliverable ${c.sizeFrom}` : `Size ${c.size}`, false)))
      .when(c.msgs > 0, (el) => el.child(metaText(`${c.msgs} ${c.msgs === 1 ? "msg" : "msgs"}`, true)))
      .when(Boolean(c.ref), (el) => el.child(metaText(c.ref, true))))
    .when(Boolean(c.blocked), (el) => el.child(h_flex().gap(4).items_center().text_size(11)
      .child(div().flex_none().text_color(t.blocked).child("▲"))
      .child(div().min_w(0).truncate().text_color(t.foreground).child(c.blocked))))
    .when(Boolean(c.queue), (el) => el.child(flagRow(t, c.queue.glyph, c.queue.text, c.queue.tip, c.queue.stalled)))
    .when(c.orch, (el) => el.child(flagRow(t, "◷", "waiting on the Orchestrator", "Waiting on the Orchestrator")))
    .when(Boolean(by), (el) => el.child(flagRow(t, "⊘", by, `${c.blockedOpen} ${c.blockedOpen === 1 ? "blocker" : "blockers"} still open`)))
    .when(c.ask, (el) => el.child(h_flex().child(Button.new(`kb-${col}-${i}-qa`)
      .h(24).px(6).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(11).text_color(t.muted)
      .border(1).border_color(t.raised)
      .hover((s) => s.bg(t.hover).text_color(t.foreground))
      .focus((s) => s.border_color(t.accent))
      .accessibility_label(`Answer ${c.id || "this"} in Q&A`)
      .on_mouse_down("left", stop)
      .on_click(() => openView("qa"))
      .child("Answer in Q&A"))));
  const box = (el) => el.mb(8).px(12).py(8).flex().flex_none().rounded(8).bg(t.raised).border(1).border_color(on ? t.accent : t.raised)
    .tooltip(full).child(inner);
  if (!opens) return box(h_flex().id(`kb-${col}-${i}`)).accessibility_label(full);
  return box(Button.new(`kb-${col}-${i}`)).when(Boolean(f.handles?.get(c.key)), (el) => el.track_focus(f.handles.get(c.key))).cursor_pointer()
    .hover((s) => s.border_color(on ? t.accent : t.muted))
    .focus((s) => s.border_color(t.accent))
    .accessibility_label(`${full}. ${on ? "Close" : "Open"} its sheet`)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => select(f, c.sel, cx));
}

// The item sheet: what the item is, its owner with Focus terminal (when exactly one live terminal is theirs), then the web item
// dialog's detail in its order: rows, Ext id, Description, Notes, Stream goal, History (the one plan-changes read), Timeline, Links,
// Attachments. The last four come from the item-log: slice, held while the sheet is open (board-model boardSubs); an item with no
// work id has none, as on the web.
function sheet(v, t, f, full) {
  if (f.sel.bl) return blSheetView(v, t, f, full);
  if (f.sel.group) return groupSheetView(v, t, f, full);
  // the card as drawn now (state, column, marks move under an open sheet); the opening snapshot when it is past a column's cap
  const s = f.memo?.m.sels.get(f.sel.key) ?? f.sel;
  const mb = ownerOf(v.st.slices, s, Date.now());
  const term = mb ? terminalFinder(terminals(), target())(mb) : null;
  const log = f.log;
  const wl = workLog(v.st.slices, v.st.gone, s.log, f.logAll);
  const quiet = (text) => div().py(6).text_size(12).text_color(t.muted).child(text);
  let rows;
  if (!log) rows = [quiet("Loading…")];
  else if (log.error) rows = [quiet(log.error)];
  else if (log.rows.length === 0) rows = [quiet("No history")];
  else rows = log.rows.map((r) => h_flex().id(`kb-log-${r.key}`).min_h(28).py(4).gap(10).items_start().border_b(1).border_color(t.border).text_size(12)
    .child(div().w(38).flex_none().pt(1).font_family(MONO).text_size(11).text_color(t.muted).child(clock(r.at)))
    .child(div().flex_1().min_w(0).text_color(t.foreground).child(r.text))
    .when(Boolean(r.by), (el) => el.child(div().flex_none().max_w(96).truncate().text_size(11).text_color(t.muted).child(r.by))));

  const shut = closeBtn(v, t, f);
  const focusLabel = term ? `Focus ${s.ownerTitle}'s terminal` : s.owner ? `No live terminal for ${s.ownerTitle}` : "No owner";
  const focusBtn = v.ring(t, Button.new("kb-sheet-focus"), t.surface).disabled(!term)
    .h(28).px(10).flex().flex_none().items_center().rounded(6).text_size(12)
    .bg(t.surface).border_color(t.border).text_color(term ? t.foreground : t.muted)
    .when(Boolean(term), (el) => el.cursor_pointer().hover((s2) => s2.bg(t.hover)).on_click(() => run("pane.focus", { terminal: term })))
    .tooltip(focusLabel)
    .accessibility_label(focusLabel)
    .on_mouse_down("left", stop)
    .child("Focus terminal");

  return sheetColumn(t, full, "kb-sheet")
    .child(v_flex().px(16).pt(8).pb(12).gap(6).flex_none().border_b(1).border_color(t.border)
      .child(h_flex().h(28).gap(6).items_center().text_size(11).text_color(t.muted)
        .child(div().flex_none().max_w(120).truncate().font_family(MONO).text_color(t.foreground).child(s.item))
        .child(div().flex_1().min_w(0).truncate().child(`· ${s.team} · ${streamName(s.stream)}`))
        .child(shut))
      .child(div().text_size(13.5).font_semibold().text_color(t.foreground).child(s.title))
      .child(h_flex().h(24).gap(8).items_center().text_size(12)
        .when(Boolean(mb), (el) => el.child(markCell(t, mb.state)))
        .child(div().flex_none().max_w(140).truncate().font_medium().text_color(t.foreground).child(s.ownerLabel || "No owner"))
        .when(Boolean(mb), (el) => el.child(div().flex_1().min_w(0).truncate().text_color(t.muted).child([mb.word.toLowerCase(), mb.age].filter(Boolean).join(" · ")))))
      .child(h_flex().mt(2).child(focusBtn)))
    .child(v_flex().id("kb-log").flex_1().min_h(0).px(16).py(8).overflow_y_scrollbar()
      .child(facts(t, s, [
        s.group && { id: "kb-sheet-group", label: s.group.label, text: s.group.text, act: (cx) => select(f, { key: s.group.key, group: true }, cx) },
        s.parent && { id: "kb-sheet-deliv", label: "Deliverable", text: s.parent.text, act: (cx) => select(f, { key: s.parent.key, bl: true, blKey: s.parent.key.slice(3), log: s.parent.log }, cx) },
        s.epic && { id: "kb-sheet-epic", label: "Epic", text: s.epic.text, act: (cx) => select(f, { key: s.epic.key, bl: true, blKey: s.epic.key.slice(3), log: s.epic.log }, cx) },
      ].filter(Boolean), { Stream: textBtn(t, "kb-sheet-only", "Show only this stream", `Show only stream ${streamName(s.stream)} on the board`, (cx) => {
        f.only = { team: `${s.dirId}/${s.team}`, stream: s.stream };
        cx.notify();
      }) }))
      .when(wl?.state === "ok" && Boolean(wl.desc), (el) => el.child(head(t, "Description")).child(descView(t, wl.desc)))
      .when(Boolean(s.note), (el) => el.child(head(t, "Notes")).child(para(t, s.note)))
      .when(Boolean(s.goal), (el) => el.child(head(t, "Stream goal")).child(para(t, s.goal)))
      .child(head(t, "History"))
      .children(rows)
      .when(Boolean(wl), (el) => el.children(timeline(t, f, wl))));
}

const head = (t, text) => div().mt(12).h(20).flex().items_center().text_size(11).font_semibold().text_color(t.muted).child(text);
const para = (t, text) => div().text_size(12).text_color(t.foreground).child(text);
const mono = (el, on) => (on ? el.font_family(MONO) : el);

// The web dialog's <dl>: a muted label column and its value; Ext id a link when its URL is http(s) (Link opens the system browser).
// `links`: rows whose value opens another sheet (the web's group, Deliverable, Epic), after the plain rows, before Ext id.
// `extra`: a control beside one row's value, by its label (the Stream row's Show only this stream).
function facts(t, s, links = [], extra = {}) {
  const row = (k, val) => h_flex().min_h(24).gap(10).items_start().text_size(12)
    .child(div().w(88).flex_none().pt(1).text_size(11).text_color(t.muted).child(k))
    .child(div().flex_1().min_w(0).child(val));
  const ext = s.ext ? (s.ext.url
    ? Link.new("kb-sheet-ext").href(s.ext.url).font_family(MONO).text_color(t.accent).cursor_pointer()
      .hover((el) => el.underline()).focus((el) => el.underline())
      .tooltip(s.ext.url).accessibility_label(`${s.ext.id}, opens in the browser`).on_mouse_down("left", stop).child(s.ext.id)
    : div().font_family(MONO).text_color(t.foreground).child(s.ext.id)) : null;
  return v_flex().id("kb-facts").gap(2)
    .children((s.rows ?? []).map(([k, val, m]) => row(k, h_flex().gap(8).flex_wrap().items_center()
      .child(mono(div().min_w(0).text_color(t.foreground), m).child(val)).when(Boolean(extra[k]), (el) => el.child(extra[k])))))
    .children(links.map((l) => row(l.label, linkBtn(t, l.id, l.text, l.act))))
    .when(Boolean(ext), (el) => el.child(row("Ext id", ext)));
}

// A description as the web's blocks: heads in bold, lists with their marks, code in mono; **bold** and `code` marks drop to text.
function descView(t, blocks) {
  const plain = (text) => inlineParts(text).map((p) => p.text).join("");
  return v_flex().gap(6).text_size(12).text_color(t.foreground).children(blocks.map((b) =>
    b.kind === "head" ? div().font_semibold().child(b.text)
    : b.kind === "code" ? div().px(8).py(6).rounded(6).bg(t.raised).font_family(MONO).text_size(11.5).child(b.text)
    : b.kind === "list" || b.kind === "olist" ? v_flex().gap(2).children(b.items.map((item, j) => h_flex().gap(6).items_start()
      .child(div().flex_none().text_color(t.muted).child(b.kind === "olist" ? `${j + 1}.` : "•"))
      .child(div().flex_1().min_w(0).child(plain(item)))))
    : div().child(plain(b.text))));
}

// Timeline (newest first, LOG_PAGE then Show all; an entry's body behind its Details), the older-not-shown line, Links, Attachments.
function timeline(t, f, wl, kids = true) {
  const quiet = (text) => div().py(6).text_size(12).text_color(t.muted).child(text);
  if (wl.state !== "ok") return [head(t, "Timeline"), quiet(wl.state === "none" ? "No timeline" : "Loading…")];
  const out = [head(t, "Timeline")];
  if (wl.total === 0) out.push(quiet("No entries"));
  out.push(...wl.entries.map((e, i) => {
    const k = `${e.ts_ms}|${e.type}|${i}`;
    const open = f.bodies?.has(k);
    return v_flex().id(`kb-tl-${i}`).py(4).gap(2).border_b(1).border_color(t.border).text_size(12)
      .child(h_flex().gap(8).items_start()
        .child(div().flex_none().font_family(MONO).text_size(11).text_color(t.muted).child(e.type ?? ""))
        .child(div().flex_1().min_w(0).text_color(t.foreground).child(e.title ?? ""))
        .when(Boolean(e.by), (el) => el.child(div().flex_none().max_w(96).truncate().font_family(MONO).text_size(11).text_color(t.muted).child(e.by))))
      .child(h_flex().gap(8).items_center().text_size(11).text_color(t.muted)
        .child(div().font_family(MONO).child(whenOf(e.ts_ms)))
        .when(Boolean(e.body), (el) => el.child(textBtn(t, `kb-tl-${i}-body`, open ? "Hide details" : "Details", `${open ? "Hide the details" : "Details"} of ${e.title}`, (cx) => {
          f.bodies ??= new Set();
          open ? f.bodies.delete(k) : f.bodies.add(k);
          cx.notify();
        }))))
      .when(Boolean(open && e.body), (el) => el.child(div().mt(2).px(8).py(6).rounded(6).bg(t.raised).font_family(MONO).text_size(11).text_color(t.foreground).child(e.body)));
  }));
  if (wl.total > LOG_PAGE) out.push(h_flex().pt(4).child(textBtn(t, "kb-tl-all", f.logAll ? "Show fewer" : `Show all ${wl.total}`, f.logAll ? "Show fewer timeline entries" : `Show all ${wl.total} timeline entries`, (cx) => {
    f.logAll = !f.logAll;
    cx.notify();
  })));
  if (wl.more > 0) out.push(div().pt(4).text_size(11).text_color(t.muted).child(`${wl.more} older not shown: ah work log ${wl.id} --all`));
  const links = wl.links.filter(([k]) => kids || k !== "Children"); // a sheet that lists its children does not repeat their ids
  if (links.length > 0) out.push(head(t, "Links"), ...links.map(([k, ids]) => h_flex().min_h(24).gap(10).items_start().text_size(12)
    .child(div().w(88).flex_none().text_size(11).text_color(t.muted).child(k))
    .child(div().flex_1().min_w(0).font_family(MONO).text_color(t.foreground).child(ids))));
  if (wl.attachments.length > 0) out.push(head(t, "Attachments"), ...wl.attachments.map((a, i) => h_flex().min_h(24).gap(6).items_center().flex_wrap().text_size(12)
    .child(a.url
      ? Link.new(`kb-att-${i}`).href(a.url).text_color(t.accent).cursor_pointer().hover((el) => el.underline()).focus((el) => el.underline())
        .tooltip(a.url).accessibility_label(`${a.text}, opens in the browser`).on_mouse_down("left", stop).child(a.text)
      : div().font_family(MONO).text_color(t.foreground).child(a.text))
    .when(Boolean(a.title), (el) => el.child(div().text_color(t.foreground).child(a.title)))
    .child(div().text_size(11).text_color(t.muted).child(a.facts))));
  return out;
}

// An empty or folded column: a narrow strip, its count over its name (the kit has no vertical text). `el` is a div, or a Button.
const strip = (el, t, label, n, tip) => el.w(STRIP_W).flex_none().pt(6).gap(6).flex().flex_col().items_center().text_size(10.5).text_color(t.muted)
  .tooltip(tip).accessibility_label(tip)
  .child(div().font_family(MONO).font_medium().child(String(n)))
  .child(div().w_full().text_center().child(label));

// The sheet's close button (the x, Esc does the same).
const closeBtn = (v, t, f) => v.ring(t, Button.new("kb-sheet-close"), t.surface)
  .size(24).flex().flex_none().items_center().justify_center().rounded(6).cursor_pointer().text_color(t.muted)
  .hover((el) => el.bg(t.hover).text_color(t.foreground))
  .tooltip("Close the sheet (esc)")
  .accessibility_label("Close the item sheet")
  .on_mouse_down("left", stop)
  .on_click((_e, cx) => {
    f.sel = null;
    cx.notify();
  })
  .child(svg("mods/ah2/x.svg").size(14));

// The Backlog column (board-kanban.js grid): its head folds it to a strip; by wave, WAVE_CAP rows each until Show all. Empty, or
// folded, it is a strip; folded, the strip unfolds it.
function backlogCol(t, f, b) {
  const fold = (cx) => {
    f.blFold = !f.blFold;
    cx.notify();
  };
  if (b.count === 0) {
    return b.total > 0
      ? strip(Button.new("kb-bl-strip"), t, "Backlog", b.total, `Show Backlog, ${b.total} rows`).cursor_pointer().rounded(6)
        .hover((s) => s.bg(t.hover).text_color(t.foreground)).focus((s) => s.border(1).border_color(t.accent))
        .on_mouse_down("left", stop).on_click((_e, cx) => fold(cx))
      : strip(v_flex().id("kb-col-backlog"), t, "Backlog", 0, "Backlog, nothing here");
  }
  const capped = b.count < b.total;
  const tw = Button.new("kb-bl-fold").size(20).flex().flex_none().items_center().justify_center().rounded(4).cursor_pointer()
    .hover((s) => s.bg(t.hover).text_color(t.foreground)).focus((s) => s.border(1).border_color(t.accent))
    .tooltip("Fold Backlog to a strip").accessibility_label("Fold Backlog to a strip")
    .on_mouse_down("left", stop).on_click((_e, cx) => fold(cx)).child("▾");
  return v_flex().id("kb-col-backlog").flex_1().min_w(COL_W).h_full().min_h(0)
    .child(h_flex().h(28).gap(6).flex_none().items_center().text_size(11).font_semibold().text_color(t.muted)
      .accessibility_label(capped ? `Backlog, ${b.count} of ${b.total} shown` : `Backlog, ${b.count} ${b.count === 1 ? "row" : "rows"}`)
      .child(tw).child("Backlog")
      .child(div().min_w(0).truncate().font_normal().child("· whole directory"))
      .child(div().flex_none().font_family(MONO).font_medium().child(capped ? `${b.count} of ${b.total}` : String(b.count))))
    .child(v_flex().id("kb-cards-backlog").flex_1().min_h(0).overflow_y_scrollbar()
      .children(b.waves.flatMap((g) => [
        h_flex().h(24).gap(6).flex_none().items_center().text_size(11).text_color(t.muted)
          .child(div().font_semibold().child(g.label))
          .child(div().font_family(MONO).child(g.rows.length === g.total ? String(g.total) : `${g.rows.length} of ${g.total}`)),
        ...g.rows.map((r) => blCard(t, f, r)),
        ...(g.total > WAVE_CAP ? [h_flex().pb(4).child(textBtn(t, `kb-wave-${g.wave}`, g.open ? "Show fewer" : `Show all ${g.total}`, g.open ? `Show fewer ${g.label} rows` : `Show all ${g.total} ${g.label} rows`, (cx) => {
          g.open ? f.waveAll.delete(g.wave) : f.waveAll.add(g.wave);
          cx.notify();
        }))] : []),
      ])));
}

// A backlog row (board-kanban.js blCard): key / kind in mono with its work id and size, the title, the state and holder while
// under way, blocked by N. It opens its backlog sheet.
function blCard(t, f, r) {
  const on = f.sel?.key === r.id;
  const label = `Backlog ${r.key}, ${r.title}${r.under ? `, ${r.under}` : ""}${r.workId ? `, ${r.workId}` : ""}${r.blockedOpen > 0 ? `, blocked by ${r.blockedOpen}` : ""}`;
  return Button.new(`kb-bl-${r.key}`).when(Boolean(f.handles?.get(r.id)), (el) => el.track_focus(f.handles.get(r.id))).mb(8).px(12).py(8).flex().flex_col().gap(4).flex_none().rounded(8).bg(t.raised).border(1).border_color(on ? t.accent : t.raised)
    .cursor_pointer().hover((s) => s.border_color(on ? t.accent : t.muted)).focus((s) => s.border_color(t.accent))
    .tooltip(label).accessibility_label(`${label}. ${on ? "Close" : "Open"} its sheet`)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => select(f, { key: r.id, bl: true, blKey: r.key, log: r.log }, cx))
    .child(h_flex().flex_wrap().gap(6).items_center().text_size(11).text_color(t.muted)
      .child(metaText(`${r.key}${r.kind ? ` / ${r.kind}` : ""}`, true))
      .when(Boolean(r.workId), (el) => el.child(metaChip(t, r.workId, `Work id ${r.workId}`, true)))
      .when(Boolean(r.size), (el) => el.child(metaChip(t, r.size, `Size ${r.size}`, false))))
    .child(div().text_color(t.foreground).child(r.title))
    .when(Boolean(r.under), (el) => el.child(h_flex().flex_wrap().gap(6).items_center().text_size(11).text_color(t.muted)
      .child(metaText(r.under)).when(Boolean(r.holder), (x) => x.child(metaText(r.holder)))))
    .when(r.blockedOpen > 0, (el) => el.child(flagRow(t, "⊘", `blocked by ${r.blockedOpen}`, `${r.blockedOpen} ${r.blockedOpen === 1 ? "blocker" : "blockers"} still open`)));
}

// A backlog row's sheet (board-kanban.js panel, bl:): kind and title, the outcome, its rows, Parent, Ext id, then Description,
// Notes, Children, the item-log Timeline and the plan items under it. A parent or child opens its own sheet; a plan item drawn on
// the board opens the item's. The All teams board names no team, so no Owner, as on the web.
function blSheetView(v, t, f, full) {
  const m = f.memo?.m;
  const p = m?.blSheet(f.sel.blKey);
  const wl = workLog(v.st.slices, v.st.gone, f.sel.log, f.logAll);
  const openBl = (key, log) => (cx) => select(f, { key: `bl:${key}`, bl: true, blKey: key, log }, cx);
  const top = v_flex().px(16).pt(8).pb(12).gap(6).flex_none().border_b(1).border_color(t.border)
    .child(h_flex().h(28).gap(6).items_center().text_size(11).text_color(t.muted)
      .child(div().flex_1().min_w(0).truncate().font_family(MONO).child(p?.kind ?? "Backlog row"))
      .child(closeBtn(v, t, f)))
    .child(div().text_size(13.5).font_semibold().text_color(t.foreground).child(p?.title ?? f.sel.blKey));
  const body = v_flex().id("kb-log").flex_1().min_h(0).px(16).py(8).overflow_y_scrollbar();
  const out = !p ? body.child(div().py(6).text_size(12).text_color(t.muted).child("Not in the backlog")) : body
    .when(Boolean(p.outcome), (el) => el.child(para(t, p.outcome).mb(8)))
    .child(facts(t, p))
    .when(Boolean(p.parent), (el) => el.child(h_flex().min_h(24).gap(10).items_start().text_size(12)
      .child(div().w(88).flex_none().pt(1).text_size(11).text_color(t.muted).child("Parent"))
      .child(linkBtn(t, "kb-bl-parent", p.parent.text, openBl(p.parent.key, p.parent.log)))))
    .when(wl?.state === "ok" && Boolean(wl.desc), (el) => el.child(head(t, "Description")).child(descView(t, wl.desc)))
    .when(Boolean(p.note), (el) => el.child(head(t, "Notes")).child(para(t, p.note)))
    .when(p.kids.length > 0, (el) => el.child(head(t, "Children")).children(p.kids.map((k, i) => h_flex().min_h(24).items_center().text_size(12)
      .child(linkBtn(t, `kb-bl-kid-${i}`, k.text, openBl(k.key, k.log))))))
    .when(Boolean(wl), (el) => el.children(timeline(t, f, wl, p.kids.length === 0)))
    .child(head(t, "Plan items"))
    .when(p.items.length === 0, (el) => el.child(para(t, "No items in the plan yet.").text_color(t.muted)))
    .children(p.items.map((it, i) => {
      const sel = m.sels.get(it.id);
      return h_flex().min_h(24).gap(6).items_center().text_size(12)
        .child(sel ? linkBtn(t, `kb-bl-item-${i}`, it.text, (cx) => select(f, sel, cx)) : div().min_w(0).truncate().text_color(t.foreground).child(it.text))
        .child(div().flex_none().text_size(11).text_color(t.muted).child(it.col));
    }));
  return sheetColumn(t, full, "kb-sheet").child(top).child(out);
}

// A link to another sheet: accent text, underlined on hover and focus.
const linkBtn = (t, id, text, act) => Button.new(id).flex().min_w(0).cursor_pointer().text_color(t.accent)
  .hover((s) => s.underline()).focus((s) => s.underline())
  .accessibility_label(text).on_mouse_down("left", stop).on_click((_e, cx) => act(cx)).child(div().min_w(0).truncate().child(text));

// The scope breadcrumb (board-kanban.js crumbs): All teams › team › lane › epic › deliverable. A pill above the last goes up to
// its level; the last opens the next level's menu (Teams, Lanes, Epics, Deliverables) with each choice's card count. A step
// closes the open sheet, as the web's.
function crumbsRow(t, f, m) {
  const last = m.crumbs.length - 1;
  const up = (depth) => (cx) => {
    if (depth < 4) f.deliv = null;
    if (depth < 3) f.epic = null;
    if (depth < 2) f.lane = null;
    if (depth < 1) f.team = null;
    f.sel = null;
    cx.notify();
  };
  const down = (c) => (_e, cx) => {
    if (c.to === "team") Object.assign(f, { team: c.key, lane: null, epic: null, deliv: null });
    else if (c.to === "lane") f.lane = c.key;
    else if (c.to === "epic") Object.assign(f, { epic: c.key, deliv: null });
    else f.deliv = c.key;
    f.sel = null;
    f.crumbOpen = false;
    cx.notify();
  };
  const pill = (el, it, on) => el.h(24).px(8).gap(4).flex().flex_none().items_center().max_w(180).rounded(12).cursor_pointer().text_size(11.5)
    .border(1).border_color(t.border).bg(on ? t.selected : t.surface).text_color(on ? t.foreground : t.muted)
    .hover((s) => s.border_color(t.muted).text_color(t.foreground)).focus((s) => s.border_color(t.accent))
    .tooltip(it.name).on_mouse_down("left", stop)
    .when(Boolean(it.kind), (x) => x.child(div().flex_none().text_size(10.5).text_color(t.muted).child(it.kind)))
    .child(div().min_w(0).truncate().child(it.name));
  const item = (c, i) => Button.new(`kb-crumb-m-${i}`)
    .h(28).px(8).gap(8).flex().items_center().rounded(6).cursor_pointer().text_size(12).text_color(t.foreground)
    .border(1).border_color(t.surface).hover((s) => s.bg(t.hover)).focus((s) => s.border_color(t.accent))
    .accessibility_label(`${c.kind ? `${c.kind} ` : ""}${c.name}, ${c.n} ${c.n === 1 ? "card" : "cards"}`)
    .on_mouse_down("left", stop).on_click(down(c))
    .when(Boolean(c.kind), (x) => x.child(div().flex_none().text_size(10.5).text_color(t.muted).child(c.kind)))
    .child(div().flex_1().min_w(0).truncate().child(c.name))
    .child(div().flex_none().font_family(MONO).text_size(11).text_color(t.muted).child(String(c.n)));
  return h_flex().id("kb-crumbs").gap(4).flex_none().items_center().accessibility_label("Board scope")
    .children(m.crumbs.map((it, i) => {
      const sep = i ? [div().flex_none().text_color(t.muted).child("›")] : [];
      if (i < last) return h_flex().gap(4).flex_none().items_center().children(sep)
        .child(pill(Button.new(`kb-crumb-${it.depth}`), it, false).accessibility_label(`Go up to ${it.name}`).on_click((_e, cx) => up(it.depth)(cx)));
      const here = pill(Button.new("kb-crumb-here"), it, true);
      if (m.menu.length === 0) return h_flex().gap(4).flex_none().items_center().children(sep).child(here.accessibility_label(`${it.name}, you are here`));
      return h_flex().gap(4).flex_none().items_center().children(sep).child(Popover.new("kb-crumb-menu").open(f.crumbOpen)
        .on_open_change((open, cx) => {
          f.crumbOpen = open;
          cx.notify();
        })
        .trigger(here.accessibility_label(`${it.name}, you are here. Open the next level`).child(div().flex_none().child("▾")))
        .content(v_flex().id("kb-crumb-items").p(4).gap(2).min_w(200).max_h(320).overflow_y_scrollbar().rounded(8).border(1).border_color(t.border).bg(t.surface).shadow_md()
          .child(div().px(8).h(24).flex().items_center().text_size(11).font_semibold().text_color(t.muted).child(m.menuHead))
          .children(m.menu.map(item))));
    }));
}

// The lane header (board-kanban.js laneHead): the lane's stream, its sub-orchestrator, members, branch to base, state, reported.
function laneHeadRow(t, h) {
  return h_flex().id("kb-lanehead").min_h(32).px(16).gap(12).flex_none().flex_wrap().items_center().border_b(1).border_color(t.border).text_size(11.5).text_color(t.muted)
    .child(div().font_family(MONO).font_semibold().text_color(t.foreground).child(h.stream))
    .child(div().child(`sub-orchestrator ${h.so}`))
    .when(h.members.length > 0, (el) => el.child(div().child(`members ${h.members.join(", ")}`)))
    .when(Boolean(h.branch), (el) => el.child(div().font_family(MONO).child(h.branch)))
    .child(div().child(h.state))
    .when(Boolean(h.report), (el) => el.child(div().h(18).px(6).flex().items_center().rounded(4).border(1).border_color(t.border).text_color(t.foreground).child(h.report)));
}

// A team's or lane's sheet (board-kanban.js panel kt:/kl:): its rows (a lane's branch, state, sub-orchestrator), members, and the
// plan items it holds, each opening its item sheet.
function groupSheetView(v, t, f, full) {
  const m = f.memo?.m;
  const p = m?.groupSheet(f.sel.key);
  const top = v_flex().px(16).pt(8).pb(12).gap(6).flex_none().border_b(1).border_color(t.border)
    .child(h_flex().h(28).gap(6).items_center().text_size(11).text_color(t.muted)
      .child(div().flex_1().min_w(0).truncate().font_family(MONO).child(p?.kind ?? (f.sel.key.startsWith("kl:") ? "Lane" : "Team")))
      .child(closeBtn(v, t, f)))
    .child(div().text_size(13.5).font_semibold().text_color(t.foreground).child(p?.title ?? f.sel.key.split("/").pop()));
  const body = v_flex().id("kb-log").flex_1().min_h(0).px(16).py(8).overflow_y_scrollbar();
  const out = !p ? body.child(para(t, "Not on this board").text_color(t.muted)) : body
    .when(p.rows.length > 0, (el) => el.child(facts(t, p)))
    .when(p.members.length > 0, (el) => el.child(head(t, "Members")).child(para(t, p.members.join(", "))))
    .child(head(t, "Plan items"))
    .when(p.items.length === 0, (el) => el.child(para(t, "No items in the plan yet.").text_color(t.muted)))
    .children(p.items.map((it, i) => {
      const sel = m.sels.get(it.id);
      return h_flex().min_h(24).gap(6).items_center().text_size(12)
        .child(sel ? linkBtn(t, `kb-grp-item-${i}`, it.text, (cx) => select(f, sel, cx)) : div().min_w(0).truncate().text_color(t.foreground).child(it.text))
        .child(div().flex_none().text_size(11).text_color(t.muted).child(it.col));
    }));
  return sheetColumn(t, full, "kb-sheet").child(top).child(out);
}

// The arrow keys (D2; board-kanban.js onKey): from the focused card or backlog row to the next one up or down its column, or to
// the same place in the next column that has any; Enter or Space opens it (the Button's own activation). The Q&A cards are qa.js's
// and take no part. Focus handles are made here, in the handler, never in render; the first press focuses the open card, else the
// first. Arrows typed into a field or a menu stay there.
function arrow(v, f, key, cx) {
  if (v.kbFocus || f.ownerOpen || f.crumbOpen || v.nnOpen || [...(v.inputs?.values() ?? [])].some((i) => i.focused)) return;
  const nav = f.memo?.m.nav ?? [];
  if (nav.length === 0) return;
  const keys = nav.flat();
  f.handles ??= new Map();
  for (const [k, h] of f.handles) if (!keys.includes(k)) {
    h.release();
    f.handles.delete(k);
  }
  for (const k of keys) if (!f.handles.has(k)) f.handles.set(k, cx.focus_handle());
  const cur = keys.find((k) => f.handles.get(k).is_focused());
  let to;
  if (!cur) to = keys.includes(f.sel?.key) ? f.sel.key : keys[0];
  else {
    const ci = nav.findIndex((col) => col.includes(cur));
    const ri = nav[ci].indexOf(cur);
    if (key === "up" || key === "down") to = nav[ci][Math.max(0, Math.min(nav[ci].length - 1, ri + (key === "down" ? 1 : -1)))];
    else {
      const next = nav[ci + (key === "right" ? 1 : -1)];
      to = next ? next[Math.min(ri, next.length - 1)] : cur;
    }
  }
  f.handles.get(to).focus();
  cx.notify();
}
