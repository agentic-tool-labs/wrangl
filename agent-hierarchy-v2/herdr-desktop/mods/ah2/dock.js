// The Desk (atp-1257, atp-1728): Q&A, Board, Feed and Graph, as tabs, in one of two places: the wrangl layout's own side column,
// the way Herdr's Files and Changes sit in its `panel` column, or one Herdr window (props {view:"desk"}, main.js) that swaps the
// four inside one copy. layouts/wrangl.json holds a hidden column DOCK beside `panel`, a stack DOCK_BODY in it, and one block per
// view, `ah2-<view>`, props {view, dock: true}. Not Herdr's own panel-body: its tab bar restores that stack per space and only to
// Files or Changes, so a docked view there would close on every space switch. Not an overlay with the column's layout either:
// Herdr's layout.select reaches the main layout only (herdr-app src/window.rs layout.select), so its tabs could not switch.
// Which place is the `placement` setting. A layout without the column (an older or edited wrangl.json) opens the Desk as a window.

import { run, layout, props, closeOverlay, openOverlay, shared, setShared } from "herdr";
import { settings, setSetting } from "./settings.js";
import { DOCK, DOCK_BODY, DOCKABLE, DESK_WINDOW_KEY, deskWindowSpec, deskWindowOpen, deskRoute, dockShown } from "./settings-model.js";

export const block = (view) => `ah2-${view}`;

const find = (node, id) => (node?.id === id ? node : (node?.children ?? []).reduce((hit, c) => hit ?? find(c, id), null));
const dockable = (view) => Boolean(find(layout(), block(view)));
export const shownIn = (view) => dockShown(layout(), view);
// The desk (the column) in this layout, and whether it is open: the tab strip's Desk toggle.
export const hasDesk = () => Boolean(find(layout(), DOCK));
export const deskOpen = () => {
  const column = find(layout(), DOCK);
  return Boolean(column) && !column.hidden; // layout.show drops `hidden`
};
export const dock = (view) => {
  run("layout.select", { stack: DOCK_BODY, child: block(view) });
  run("layout.show", { id: DOCK });
};
export const undock = () => run("layout.hide", { id: DOCK });

// A copy drawn in the column, not in a window.
export const isDocked = () => props()?.dock === true;
// The Desk window's copy.
export const isDeskWindow = () => props()?.view === "desk";
export const deskWindow = () => shared(DESK_WINDOW_KEY);

// A new Desk window on `view`, at the size it last had. Only when none is open: Herdr replaces a window opened again under its
// key, and its copy's state (an unsent answer) with it.
const openDeskWindow = (view, max = deskWindow()?.max === true) => openOverlay(deskWindowSpec(view, max));

// The window's maximize/restore (atp-1727): reopen it on the same tab at the other size. Lossy by design: the replaced copy's
// scroll, selection and unsent Q&A answer go with it. The key is written first, so the old copy's close() (its own overlay id
// only) leaves it alone.
export function resizeDeskWindow(view, max) {
  setShared(DESK_WINDOW_KEY, { view, max });
  openDeskWindow(view, max);
}
// The open Desk window shows `view`: it reads `want` on its next render (main.js) and takes the keyboard.
const switchDeskWindow = (view) => setShared(DESK_WINDOW_KEY, { ...deskWindow(), want: view });

// Open a Desk tab where the Desk is (deskRoute). `toggle` (a toggle button): a docked view already showing closes its column, as
// Herdr's Files button does.
export function openView(view, toggle = false) {
  const route = deskRoute(settings().placement, dockable(view), deskWindowOpen(deskWindow()));
  if (route === "dock") {
    if (toggle && shownIn(view)) undock();
    else dock(view);
  } else if (route === "switch") switchDeskWindow(view);
  else openDeskWindow(view);
}

// How a view's close (× or Esc) closes it: the column hides, or the window closes.
export const closeOf = () => (isDocked() ? undock : () => closeOverlay(props().overlay));

// The column's ⧉: the whole Desk moves to a window on the tab shown, remembered for the next open.
export function toWindow(view) {
  setSetting({ placement: "window" });
  undock();
  if (deskWindowOpen(deskWindow())) switchDeskWindow(view);
  else openDeskWindow(view);
}

// The window's dock button: the whole Desk moves back to the column on the window's tab, and the window closes.
export function toColumn(view) {
  setSetting({ placement: "column" });
  dock(view);
  closeOverlay(props().overlay);
}

// Whether this layout has the column at all (the window draws its dock button only then).
export const columnHere = () => DOCKABLE.some(dockable);

// The tab strip's Desk toggle in window placement: closes the open window (and forgets a stale one), else opens it on its last
// tab, else Q&A.
export function toggleDeskWindow() {
  const w = deskWindow();
  if (deskWindowOpen(w)) {
    closeOverlay(w.overlay);
    setShared(DESK_WINDOW_KEY, { view: w.view, max: w.max });
  } else openDeskWindow(DOCKABLE.includes(w?.view) ? w.view : "qa");
}
