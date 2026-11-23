// The sidebar's tab strip (b374): the header of layouts/wrangl.json's `agents` stack, props {view:"tabs"}. Two exclusive tabs,
// Agents (Herdr's own agents mod) and ah2 (the team sitrep); a click is `layout.select`, which Herdr keeps as a click on the
// layout, so the choice outlives redraws and restarts. Then the … menu (atp-1256): ah2 settings (settings.js), and Update when one
// is ready. Layouts switch with Herdr's own switcher (ctrl-shift-l). The strip is mounted whenever the wrangl layout shows, so it
// applies the default tab and runs the update check (a dot on the ah2 tab when the plugin carries a newer mod).

import { div, image, svg } from "gpui-kit";
import { h_flex, v_flex, Button } from "gpui-base";
import { run, layout, shared, setShared, props, openOverlay, closeOverlay } from "herdr";
import { CLICK_KEY, DOCKABLE, DOCK, DOCK_BODY, DESK_KEY, deskWindowOpen } from "./settings-model.js";
import { settings, updateState, checkUpdate, openSettings } from "./settings.js";
import { dock, undock, hasDesk, deskOpen, toWindow, toColumn, columnHere, deskWindow, toggleDeskWindow, resizeDeskWindow } from "./dock.js";

export const STACK = "agents";
export const TABS = [["agents-list", "Agents"], ["ah2", "Wrangl"]];

const find = (node, id) => (node?.id === id ? node : (node?.children ?? []).reduce((hit, c) => hit ?? find(c, id), null));

// The shown child of the stack; the last tab when the layout names none (the ah2 block is what this layout is for).
export const activeTab = (root) => {
  const stack = find(root, STACK);
  return TABS.some(([id]) => id === stack?.active) ? stack.active : TABS.at(-1)[0];
};

// On mount (the wrangl layout opened): the default tab, unless a tab click just now is what remounted the strip; then the update
// check, and again hourly.
export function tabsInit(cx) {
  cx.spawn(async (cx) => {
    await cx.sleep(0);
    const tab = settings().tab;
    if (Date.now() - (Number(shared(CLICK_KEY)) || 0) > 5000 && activeTab(layout()) !== tab) run("layout.select", { stack: STACK, child: tab });
    await checkUpdate();
  });
  cx.timer.every(3600e3, () => { checkUpdate(); });
}

export function tabsView(t) {
  const on = activeTab(layout());
  const u = updateState();
  const ready = u.newer && !u.installing && !u.stuck;
  const tab = ([id, label]) => {
    const shown = id === on;
    const dot = ready && id === "ah2";
    return Button.new(`ah2-tab-${id}`)
      .h(24).px(10).gap(6).flex().flex_none().items_center().rounded(5).cursor_pointer()
      .border(1).border_color(shown ? t.border : t.chrome).bg(shown ? t.raised : t.chrome).text_color(shown ? t.foreground : t.muted)
      .hover((el) => el.bg(t.hover).text_color(t.foreground))
      .focus((el) => el.border_color(t.accent).text_color(t.foreground))
      .when(dot, (el) => el.tooltip(`Wrangl v${u.bundled} is available: update from the … menu`))
      .accessibility_label(`${shown ? `${label}, shown` : `Show ${label}`}${dot ? `. Wrangl v${u.bundled} is available` : ""}`)
      .on_click(() => {
        if (shown) return;
        setShared(CLICK_KEY, Date.now());
        run("layout.select", { stack: STACK, child: id });
      })
      .when(id === "ah2", (el) => el.child(image("mods/ah2/wrangl-color.svg").size(14).flex_none()))
      .child(label)
      .when(dot, (el) => el.child(div().size(6).flex_none().rounded(3).bg(t.accent)));
  };
  // the Desk toggle: in the column placement the layout's column (lit while open); in the window placement the Desk window
  const win = settings().placement === "window";
  const desk = settings().strip.desk && (win || hasDesk());
  const open = desk && (win ? deskWindowOpen(deskWindow()) : deskOpen());
  const what = win ? "the desk window" : "the desk";
  return h_flex().id("ah2-tabs").size_full().pl(12).pr(8).gap(4).items_center().bg(t.chrome).border_t(1).border_color(t.border).text_size(12)
    .children(TABS.map(tab))
    .child(div().flex_1())
    // The desk toggle (atp-1712: moved here from the sitrep's foot strip): shows or hides the side column with Q&A, Board, Feed
    // and Graph (its own tab row picks the view).
    .when(desk, (el) => el.child(Button.new("ah2-desk")
      .size(24).flex().flex_none().items_center().justify_center().rounded(6).cursor_pointer()
      .border(1).border_color(open ? t.border : t.chrome).bg(open ? t.raised : t.chrome).text_color(open ? t.foreground : t.muted)
      .hover((el) => el.bg(t.hover).text_color(t.foreground))
      .focus((el) => el.border_color(t.accent).text_color(t.foreground))
      .tooltip(open ? `Hide ${what}: Q&A, Board, Feed, Graph` : `Show ${what}: Q&A, Board, Feed, Graph`)
      .accessibility_label(open ? `Desk, shown. Hide ${what}` : `Show ${what}`)
      .on_click(() => (win ? toggleDeskWindow() : run("layout.toggle", { id: DOCK })))
      .child(svg("mods/ah2/panel-right.svg").size(14))))
    .child(Button.new("ah2-menu")
      .size(24).flex().flex_none().items_center().justify_center().rounded(6).cursor_pointer()
      .border(1).border_color(t.chrome).text_color(t.muted)
      .hover((el) => el.bg(t.hover).text_color(t.foreground))
      .focus((el) => el.border_color(t.accent).text_color(t.foreground))
      .tooltip(ready ? `Wrangl menu · v${u.bundled} is available` : "Wrangl menu")
      .accessibility_label(ready ? `Wrangl menu. v${u.bundled} is available` : "Wrangl menu")
      .on_click(() => openOverlay({ mod: "ah2", props: { view: "menu" }, kind: "popup", width: 220, height: ready ? 72 : 41 }))
      .child(svg("mods/ah2/ellipsis.svg").size(16)));
}

// The … popup: a row per action, Enter or a click runs it and closes the popup; Esc closes it (Herdr closes popups on Esc too).
export function menuView(v, t) {
  const u = updateState();
  const ready = u.newer && !u.installing && !u.stuck;
  const close = () => closeOverlay(props().overlay);
  const item = (id, label, go, dot = false) => Button.new(id)
    .h(31).px(8).gap(9).flex().items_center().rounded(7).cursor_pointer()
    .border(1).border_color(t.popover).text_color(t.foreground)
    .hover((el) => el.bg(t.hover))
    .focus((el) => el.border_color(t.accent))
    .accessibility_label(label)
    .on_click(() => {
      close();
      go();
    })
    .child(div().flex_1().min_w(0).truncate().child(label))
    .when(dot, (el) => el.child(div().size(6).flex_none().rounded(3).bg(t.accent)));
  return v_flex().id("ah2-menu-list").size_full().p(5).bg(t.popover).text_size(12.8)
    .track_focus(v.keys)
    .on_key_down((e, _cx) => {
      if (e.keystroke === "escape") close();
    })
    .child(item("ah2-menu-settings", "Wrangl settings…", openSettings))
    .when(ready, (el) => el.child(item("ah2-menu-update", `Update to v${u.bundled}`, () => checkUpdate({ install: true }), true)));
}

// ---- the desk's tab row: the header of layouts/wrangl.json's ah2-panel-body stack (props {view:"panel-tabs"}), and of the Desk
// window (main.js desk copy, `win` = {view, select(view, cx), close}) ----
// Q&A, Board, Feed, Graph: a click shows that tab. Column: ⧉ moves the whole Desk to a window, × closes the column. Window: the
// dock button moves it back (only where the layout has the column), × closes the window. The Q&A count is the desk model the
// status segment publishes (main.js publishDesk); no poll of its own.

const DESK_LABEL = { qa: "Q&A", board: "Board", feed: "Feed", graph: "Graph" };
const MONO = "Geist Mono";
const stop = (_e, cx) => cx.stop_propagation(); // in the window the row drags it: a press on a control must not

export function panelTabsView(t, win = null) {
  const on = win ? win.view : (find(layout(), DOCK_BODY)?.active ?? "").replace(/^ah2-/, "");
  // a team's Desk counts its own scope (main.js deskTeam); the all-teams Desk and the column, DESK_KEY's
  const waiting = win?.team ? win.waiting ?? 0 : shared(DESK_KEY)?.waiting ?? 0;
  const icon = (id, label, path, go) => Button.new(id)
    .size(24).flex().flex_none().items_center().justify_center().rounded(6).cursor_pointer()
    .border(1).border_color(t.surface).text_color(t.muted)
    .hover((el) => el.bg(t.hover).text_color(t.foreground))
    .focus((el) => el.border_color(t.accent).text_color(t.foreground))
    .tooltip(label)
    .accessibility_label(label)
    .on_mouse_down("left", stop)
    .on_click(go)
    .child(svg(path).size(14));
  const tab = (view) => {
    const shown = view === on;
    const label = view === "sitrep" ? "Sitrep" : DESK_LABEL[view];
    const n = view === "qa" ? waiting : 0;
    return Button.new(`ah2-desk-${view}`)
      .h(26).px(10).gap(6).flex().flex_none().items_center().rounded(6).cursor_pointer()
      .border(1).border_color(shown ? t.border : t.surface).bg(shown ? t.raised : t.surface).text_color(shown ? t.foreground : t.muted)
      .hover((el) => el.bg(t.hover).text_color(t.foreground))
      .focus((el) => el.border_color(t.accent).text_color(t.foreground))
      .accessibility_label(`${shown ? `${label}, shown` : `Show ${label}`}${n ? `. ${n} waiting on you` : ""}`)
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => {
        if (shown) return;
        if (win) win.select(view, cx);
        else dock(view);
      })
      .child(label)
      .when(n > 0, (el) => el.child(div().h(16).px(5).flex().items_center().rounded(8).border(1).border_color(t.blocked)
        .font_family(MONO).text_size(10.5).text_color(t.foreground).child(String(n))));
  };
  // A team's Desk (win.team, desk-per-team D5): the team's name first, no dock button (it cannot dock, D2), titles naming it.
  const team = win?.team ?? null;
  const row = h_flex().id("ah2-desk-tabs").px(8).gap(2).items_center().bg(t.surface).border_b(1).border_color(t.border).text_size(12)
    .when(Boolean(team), (el) => el.child(div().id("ah2-desk-team").min_w(0).max_w(140).truncate().mr(6).text_size(12).font_semibold().text_color(t.muted)
      .when(Boolean(win?.real), (x) => x.tooltip(win.real)).child(win.name)))
    .children((team ? ["sitrep", ...DOCKABLE] : DOCKABLE).map(tab)) // Sitrep first, team Desks only (D6)
    .child(div().flex_1());
  // the window's row is 44 px (graph-view.js reserves 44 for a header when not docked)
  if (win) return row.h(44).flex_none()
    .when(!team && columnHere(), (el) => el.child(icon("ah2-desk-dock", "Dock the desk to the side", "mods/ah2/panel-right.svg", () => toColumn(on))))
    // ⤢ / ⤡ (atp-1727): reopens the window at the other size, on this tab
    .child(win.max
      ? icon("ah2-desk-restore", team ? `Restore the ${win.name} desk` : "Restore the desk window", "mods/ah2/minimize.svg", () => resizeDeskWindow(on, false, team))
      : icon("ah2-desk-maximize", team ? `Maximize the ${win.name} desk` : "Maximize the desk window", "mods/ah2/maximize.svg", () => resizeDeskWindow(on, true, team)))
    .child(icon("ah2-desk-close", team ? `Close the ${win.name} desk (esc)` : "Close the desk (esc)", "mods/ah2/x.svg", win.close));
  return row.size_full()
    .when(Boolean(DESK_LABEL[on]), (el) => el.child(icon("ah2-desk-window", "Open the desk in a window", "mods/ah2/app-window.svg", () => toWindow(on))))
    .child(icon("ah2-desk-close", "Close the desk", "mods/ah2/x.svg", undock));
}
