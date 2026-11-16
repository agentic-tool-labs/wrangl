// ah2 in Herdr: the Board (props {view:"board"}): the web Board's state columns over every running team, as a kanban. It is a tab
// of the Desk, in the ah2 side column or the Desk window (dock.js). Colours are theme() tokens only.
// The data is board-model.js's. A card waiting on you is the Q&A window's own card, compact (qa.js card): its Send is the only thing
// here that sends, through model.js sendAnswer, and only from its on_click. A plan card opens the item sheet: its owner, Focus
// terminal and the item's log, read once on the click.

import { div, svg } from "gpui-kit";
import { v_flex, h_flex, Button, Popover } from "gpui-base";
import { run, props, terminals } from "herdr";
import { ah, target } from "./ah.js";
import { openView, isDocked } from "./dock.js";
import { boardModel, ownerOf, readItemLog } from "./board-model.js";
import { streamName } from "./plans-model.js";
import { loadingOf, terminalFinder } from "./model.js";
import { card, note, empty, clock } from "./qa.js";
import { markCell } from "./marks.js";

const MONO = "Geist Mono";
const COL_W = 184; // six columns fit about 1200 px; narrower, the board scrolls sideways
const SHEET_W = 300;

const stop = (_e, cx) => cx.stop_propagation(); // a press on a control must not drag the window

// The board's own state, made on first draw: the filters, the change baseline (board-model marker) and the open item sheet.
const stateOf = (v) => (v.kb ??= { link: () => v.link, hide: new Set(), owner: "", blockedOnly: false, changesOnly: false, seen: new Map(), ownerOpen: false, sel: null, log: null });

export function boardView(v, t, close) {
  const f = stateOf(v);
  const narrow = props()?.dock === true; // the side column: the sheet takes the column's width instead of sitting beside the cards
  // boardModel only when an input changed (as graph-view.js graphOf): the slices (v.sliceGen, bumped by the poll), the filters
  // and the half minute (ages). Herdr redraws every mod on any live change, and a render has 50 ms.
  const key = [v.sliceGen, f.owner, f.blockedOnly, f.changesOnly, [...f.hide].join("|"), Math.floor(Date.now() / 30000)];
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
      ? (m.filtered ? empty(t, "Nothing matches these filters.", "Change them in the row above.") : empty(t, "Nothing on the board.", "Running teams have no plan items today."))
      : h_flex().id("kb").size_full().min_h(0).gap(12).items_start().overflow_x_scrollbar().children(m.columns.map((c) => column(v, t, f, c)));
    body = h_flex().size_full().min_h(0)
      .child(v_flex().flex_1().min_w(0).h_full().min_h(0)
        .child(filters(t, f, m))
        .child(div().id("kb-cols").flex_1().min_h(0).px(16).pt(8).pb(12).child(cards)))
      .when(Boolean(f.sel), (el) => el.child(sheet(v, t, f, false)));
  }

  return v_flex().id("root").size_full().min_h(0).bg(t.surface).text_size(12.5)
    .track_focus(v.keys)
    .on_key_down((e, cx) => {
      if (e.keystroke !== "escape") return;
      if (v.nnOpen || f.ownerOpen) {
        v.nnOpen = null;
        f.ownerOpen = false;
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

// The filter row: Blocked only and Changes only (boardOf's own filters), the owner menu, then a chip per running team and lane.
function filters(t, f, m) {
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
  return h_flex().id("kb-filters").h(40).px(16).gap(6).flex_none().items_center().border_b(1).border_color(t.border).overflow_x_scrollbar()
    .child(chip(t, "kb-blocked", "Blocked only", f.blockedOnly, "Blocked only", flip("blockedOnly"), "Only items stuck on someone, and what waits on you"))
    .child(chip(t, "kb-changes", "Changes only", f.changesOnly, "Changes only: items new or moved since you opened the board", flip("changesOnly"), "Only items new or moved since you opened the board"))
    .child(owner)
    .child(div().w(1).h(16).mx(4).flex_none().bg(t.border))
    .children(m.teams.map((tm) => chip(t, `kb-team-${tm.key}`, `${tm.lane ? "↳ " : ""}${tm.name}`, tm.on, `Show ${tm.name}${tm.lane ? " (lane)" : ""}`, () => {
      if (tm.on) f.hide.add(tm.key);
      else f.hide.delete(tm.key);
    })));
}

function column(v, t, f, col) {
  const label = `${col.label}, ${col.count} ${col.count === 1 ? "card" : "cards"}`;
  return v_flex().id(`kb-col-${col.key}`).flex_1().min_w(col.key === "waiting" ? COL_W + 64 : COL_W).h_full().min_h(0)
    .child(h_flex().h(28).gap(6).flex_none().items_center().text_size(11).font_semibold().text_color(t.muted)
      .accessibility_label(label)
      .child(col.label)
      .child(div().font_family(MONO).font_medium().child(String(col.count))))
    .child(v_flex().id(`kb-cards-${col.key}`).flex_1().min_h(0).overflow_y_scrollbar()
      .when(col.cards.length === 0, (el) => el.child(div().py(4).text_size(11).text_color(t.muted).child("None")))
      .children(col.cards.map((c, i) => (c.qa ? card(v, t, c.tm, c.qa, true) : item(t, f, col.key, c, i))))
      .when(col.more > 0, (el) => el.child(div().py(4).text_size(11).text_color(t.muted).child(`+ ${col.more} more`))));
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
  cx.notify();
  cx.spawn(async (cx) => {
    const sleep = (ms) => cx.sleep(ms);
    const link = f.link?.();
    const got = await readItemLog(ah().exec, sel, sleep, link ? (path, timeout) => link.read(path, timeout, sleep) : null);
    if (f.sel === sel) f.log = got;
    cx.notify();
  });
}

// A plain card: the title (it wraps) with the change dot, then the item id in mono and team · owner, then a blocked marker or the
// way into Q&A. A plan item's card is a button that opens its sheet.
function item(t, f, col, c, i) {
  const who = [c.team, c.owner].filter(Boolean).join(" · ");
  const full = [c.title, c.ref, who, c.blocked, c.mark].filter(Boolean).join(" · ");
  const on = f.sel?.key === c.key;
  const opens = Boolean(c.sel) && !c.ask;
  const inner = v_flex().flex_1().min_w(0).gap(4)
    .child(h_flex().gap(6).items_start()
      .child(div().flex_1().min_w(0).text_color(t.foreground).child(c.title))
      // the change dot: the shape says "changed"; what changed is in the tooltip and the label
      .when(Boolean(c.mark), (el) => el.child(div().mt(5).size(6).flex_none().rounded(3).bg(t.accent))))
    .child(h_flex().gap(6).items_center().text_size(11).text_color(t.muted)
      // done colours the ✓ only, as in Q&A: done text on paper raised is 4.30:1
      .when(c.done, (el) => el.child(div().flex_none().text_color(t.done).child("✓")))
      .when(Boolean(c.id), (el) => el.child(div().flex_none().max_w(96).truncate().font_family(MONO).child(c.id)))
      .child(div().flex_1().min_w(0).truncate().child(who)))
    .when(Boolean(c.blocked), (el) => el.child(h_flex().gap(4).items_center().text_size(11)
      .child(div().flex_none().text_color(t.blocked).child("▲"))
      .child(div().min_w(0).truncate().text_color(t.foreground).child(c.blocked))))
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
  return box(Button.new(`kb-${col}-${i}`)).cursor_pointer()
    .hover((s) => s.border_color(on ? t.accent : t.muted))
    .focus((s) => s.border_color(t.accent))
    .accessibility_label(`${full}. ${on ? "Close" : "Open"} its sheet`)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => select(f, c.sel, cx));
}

// The item sheet: what the item is, its owner with Focus terminal (when exactly one live terminal is theirs), and its log.
function sheet(v, t, f, full) {
  const s = f.sel;
  const mb = ownerOf(v.st.slices, s, Date.now());
  const term = mb ? terminalFinder(terminals(), target())(mb) : null;
  const log = f.log;
  const quiet = (text) => div().py(6).text_size(12).text_color(t.muted).child(text);
  let rows;
  if (!log) rows = [quiet("Loading the log…")];
  else if (log.error) rows = [quiet(log.error)];
  else if (log.rows.length === 0) rows = [quiet("No changes recorded.")];
  else rows = log.rows.map((r) => h_flex().id(`kb-log-${r.key}`).min_h(28).py(4).gap(10).items_start().border_b(1).border_color(t.border).text_size(12)
    .child(div().w(38).flex_none().pt(1).font_family(MONO).text_size(11).text_color(t.muted).child(clock(r.at)))
    .child(div().flex_1().min_w(0).text_color(t.foreground).child(r.text))
    .when(Boolean(r.by), (el) => el.child(div().flex_none().max_w(96).truncate().text_size(11).text_color(t.muted).child(r.by))));

  const shut = v.ring(t, Button.new("kb-sheet-close"), t.surface)
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
  const focusLabel = term ? `Focus ${s.owner}'s terminal` : s.owner ? `No live terminal for ${s.owner}` : "No owner";
  const focusBtn = v.ring(t, Button.new("kb-sheet-focus"), t.surface).disabled(!term)
    .h(28).px(10).flex().flex_none().items_center().rounded(6).text_size(12)
    .bg(t.surface).border_color(t.border).text_color(term ? t.foreground : t.muted)
    .when(Boolean(term), (el) => el.cursor_pointer().hover((s2) => s2.bg(t.hover)).on_click(() => run("pane.focus", { terminal: term })))
    .tooltip(focusLabel)
    .accessibility_label(focusLabel)
    .on_mouse_down("left", stop)
    .child("Focus terminal");

  return v_flex().id("kb-sheet").when(full, (el) => el.w_full()).when(!full, (el) => el.w(SHEET_W).border_l(1).border_color(t.border))
    .flex_none().h_full().min_h(0).bg(t.surface)
    .child(v_flex().px(16).pt(8).pb(12).gap(6).flex_none().border_b(1).border_color(t.border)
      .child(h_flex().h(28).gap(6).items_center().text_size(11).text_color(t.muted)
        .child(div().flex_none().max_w(120).truncate().font_family(MONO).text_color(t.foreground).child(s.item))
        .child(div().flex_1().min_w(0).truncate().child(`· ${s.team} · ${streamName(s.stream)}`))
        .child(shut))
      .child(div().text_size(13.5).font_semibold().text_color(t.foreground).child(s.title))
      .child(h_flex().h(24).gap(8).items_center().text_size(12)
        .when(Boolean(mb), (el) => el.child(markCell(t, mb.state)))
        .child(div().flex_none().max_w(140).truncate().font_medium().text_color(t.foreground).child(s.owner || "No owner"))
        .when(Boolean(mb), (el) => el.child(div().flex_1().min_w(0).truncate().text_color(t.muted).child([mb.word.toLowerCase(), mb.age].filter(Boolean).join(" · ")))))
      .child(h_flex().mt(2).child(focusBtn)))
    .child(v_flex().id("kb-log").flex_1().min_h(0).px(16).py(8).overflow_y_scrollbar()
      .child(div().h(20).text_size(11).font_semibold().text_color(t.muted).child("Log"))
      .children(rows));
}
