import { firstTeam, memberIds } from "./select.js";
import { toggleMode } from "./graph.js";
import { isTextTarget } from "./member-actions.js";
import { cycle, prefOf } from "./plans-model.js";
import { partsOf } from "./graph-world.js";

// The page-wide keys, in two scopes. Inside a member's panel (focus within it) the server's member-action keys own the
// keyboard and only Escape is the page's; everywhere else (the root scope) the whole table acts. A test keeps Escape out of
// the server's key set and checks that the server keys still reach their handler.
export const MEMBER_SCOPE_KEYS = ["Escape"];
// The page keys as the help lists them: the key as pressed, what it does, and the label shown. Every entry of GLOBAL_KEYS appears
// here. `on` lists the views where a key acts and `hint` is its word in the status bar there; the bar's hints are generated
// from this table (hintOf), so they cannot drift from the keys.
const TAB_VIEWS = ["overview", "board", "graph", "timeline", "conversation", "member", "inbox"];
const RAIL_VIEWS = ["teams", "overview", "graph", "timeline", "conversation"];
const MEMBER_VIEWS = ["overview", "graph", "timeline", "conversation", "member"];
const BOARD_VIEWS = ["board", "allplans"];
const EVERY = ["teams", "allplans", ...TAB_VIEWS];
export const KEY_TABLE = [
  { keys: ["1", "2", "3", "4", "5"], label: "1–5", does: "Overview, Board, Graph, Timeline, Conversation", hint: "tabs", on: TAB_VIEWS, team: true },
  { keys: ["ArrowUp", "ArrowDown"], label: "↑↓", does: "Previous or next member", hint: "member", on: MEMBER_VIEWS },
  { keys: ["ArrowUp", "ArrowDown"], label: "↑↓", does: "Previous or next row (a team on Home, a stream on the Board)", hint: "row", on: ["teams", ...BOARD_VIEWS] },
  { keys: ["Enter"], label: "Enter", does: "Open the selected member's conversation", hint: "open", on: ["overview", "graph", "timeline"] },
  { keys: ["Escape"], label: "Esc", does: "Close the menu, then clear the selection", on: EVERY },
  { keys: ["/"], label: "/", does: "Focus the search", hint: "search", on: ["teams", "conversation", ...BOARD_VIEWS] },
  { keys: ["End"], label: "End", does: "Conversation: jump to live and follow", hint: "live", on: ["conversation"] },
  { keys: ["M"], label: "Shift+M", does: "Graph: Map, Radial or Flow", hint: "Map/Radial/Flow", on: ["graph"] },
  { keys: ["F"], label: "Shift+F", does: "Graph: fit the view", hint: "fit", on: ["graph"] },
  { keys: ["G"], label: "Shift+G", does: "Board: group, or open the board tile in Graph", hint: "group", on: BOARD_VIEWS },
  { keys: ["L"], label: "Shift+L", does: "Board: layout", hint: "layout", on: BOARD_VIEWS },
  { keys: ["C"], label: "Shift+C", does: "Board: changes filter", on: BOARD_VIEWS },
  { keys: ["A"], label: "Shift+A", does: "Board: mark all seen", on: BOARD_VIEWS },
  { keys: ["T"], label: "Shift+T", does: "Switch team", hint: "team", on: EVERY },
  { keys: ["D"], label: "Shift+D", does: "Performance strip", on: EVERY },
  { keys: ["]"], label: "]", does: "Show or hide the panel under 1100px, where the view has one", hint: "panel", on: RAIL_VIEWS },
  { keys: [], label: "?", does: "This help", hint: "keys", on: EVERY },
];

// The status bar's hints for a view: "1–5 tabs", that view's own keys, then "? keys". The tab keys are hinted only with a team.
export function hintOf(view, { team = true } = {}) {
  const shown = KEY_TABLE.filter((k) => k.hint && k.on.includes(view) && (team || !k.team));
  const last = shown.filter((k) => k.label === "?");
  return [...shown.filter((k) => k.label !== "?"), ...last].map((k) => `${k.label} ${k.hint}`).join(" · ");
}

export const GLOBAL_KEYS = ["1", "2", "3", "4", "5", "ArrowDown", "ArrowUp", "Enter", "Escape", "/", "T", "D", "M", "F", "G", "L", "C", "A", "End", "]"];

const TEAM_VIEWS = { 1: "overview", 2: "board", 3: "graph", 4: "timeline", 5: "conversation" };

const dialogOpen = (ui) => ui.help === true || ui.rules === true || ui.sitrep === true || Boolean(ui.expand || ui.pair || ui.rename);

const classesOf = (el) => String(el?.getAttribute?.("class") ?? "").split(/\s+/);

// Whether focus is inside the member panel, where the member actions own the keys.
export function inMemberScope(el) {
  for (let n = el; n; n = n.parentNode) {
    const c = classesOf(n);
    if (c.includes("member-actions") || (n.localName === "section" && c.includes("panel"))) {
      return true;
    }
  }
  return false;
}

// Enter on a focused button or link activates that control; it is not also a shortcut.
const activatable = (el) => ["button", "a"].includes(String(el?.localName ?? "").toLowerCase());

// Moves focus among the rows a view marks `data-row`, from the focused one (or from the top).
function moveRow(doc, step) {
  const rows = [...doc.querySelectorAll("[data-row]")];
  if (rows.length === 0) {
    return false;
  }
  const at = rows.indexOf(doc.activeElement);
  rows[Math.min(rows.length - 1, Math.max(0, at < 0 ? (step > 0 ? 0 : rows.length - 1) : at + step))].focus();
  return true;
}

// Returns whether the key was one of the page's. Ignored in text fields, with ctrl, meta or alt, and while a dialog owns the keys.
export function globalKey(e, store, doc = globalThis.document) {
  // A control that handled the key (the focused graph's 0 fits the view) keeps it (B1).
  if (e.defaultPrevented || !GLOBAL_KEYS.includes(e.key) || isTextTarget(e.target) || e.ctrlKey || e.metaKey || e.altKey) {
    return false;
  }
  if (inMemberScope(e.target) && !MEMBER_SCOPE_KEYS.includes(e.key)) {
    return false;
  }
  const { ui, slices } = store.state;
  if (dialogOpen(ui) || e.target.closest?.('[aria-modal="true"]')) {
    return false;
  }
  const team = firstTeam(slices, ui);
  const level = ui.view ?? "teams";
  const isPlans = BOARD_VIEWS.includes(level);
  const rowLevel = level === "teams" || isPlans;
  const board = isPlans && prefOf(ui, "plansLayout", {}) === "board";
  switch (e.key) {
    case "Escape":
      if (ui.menu) {
        store.setUi({ menu: null });
        return true;
      }
      if (ui.selected) {
        store.setUi({ selected: null });
        return true;
      }
      return false;
    case "ArrowDown":
    case "ArrowUp": {
      const step = e.key === "ArrowDown" ? 1 : -1;
      if (rowLevel) {
        if (e.target !== doc.body && !e.target?.hasAttribute?.("data-row")) {
          return false;
        }
        return moveRow(doc, step) && (e.preventDefault(), true);
      }
      if (e.target !== doc.body || !team) {
        return false;
      }
      const ids = memberIds(team);
      const at = ids.indexOf(ui.selected);
      store.setUi({ selected: ids[Math.min(ids.length - 1, Math.max(0, at < 0 ? (step > 0 ? 0 : ids.length - 1) : at + step))] ?? null });
      e.preventDefault();
      return true;
    }
    case "Enter":
      if (rowLevel || !ui.selected || activatable(e.target)) {
        return false;
      }
      store.setUi({ view: "conversation" });
      return true;
    case "/": {
      const search = doc.querySelector?.("[data-search]");
      if (!search) {
        return false;
      }
      e.preventDefault();
      search.focus();
      return true;
    }
    case "T":
      store.setUi({ menu: ui.menu === "team" ? null : "team" });
      return true;
    case "D":
      store.setUi({ diagnostics: ui.diagnostics !== true });
      return true;
    case "C":
      if (!isPlans) {
        return false;
      }
      cycle(store, "plansChanges", prefOf(ui, "plansChanges", {}));
      return true;
    case "A":
      if (!isPlans) {
        return false;
      }
      store.setUi({ plansMarkSeen: (ui.plansMarkSeen ?? 0) + 1 });
      return true;
    case "G":
    case "L":
      if (!isPlans) {
        return false;
      }
      if (e.key === "L") {
        cycle(store, "plansLayout", prefOf(ui, "plansLayout", {}));
        return true;
      }
      if (board) {
        // The selected tile's team (and stream), else the focus's, in Graph.
        const focus = ui.plansFocus?.includes("#") ? ui.plansFocus.slice(ui.plansFocus.indexOf("#") + 1) : null;
        const p = partsOf(ui.plansSel) ?? partsOf(focus);
        store.setUi({ view: "graph", team: p ? `team:${p.key}` : ui.team ?? null, graphCard: p?.stream ? `stream:${p.stream}` : null, selected: null });
        return true;
      }
      cycle(store, "plansGroup", prefOf(ui, "plansGroup", {}));
      return true;
    case "M":
      if (ui.view !== "graph") {
        return false;
      }
      toggleMode(store);
      return true;
    case "End":
      if (ui.view !== "conversation") {
        return false;
      }
      e.preventDefault();
      store.setUi({ liveNonce: (ui.liveNonce ?? 0) + 1 });
      return true;
    case "]":
      if (!RAIL_VIEWS.includes(level)) {
        return false;
      }
      store.setUi({ railOpen: ui.railOpen !== true });
      return true;
    case "F":
      if (ui.view !== "graph") {
        return false;
      }
      store.setUi({ fitNonce: (ui.fitNonce ?? 0) + 1 });
      return true;
    default:
      break;
  }
  // The tab keys need a team; Home and the Board of all teams have no tabs.
  if (level === "teams" || level === "allplans" || (level === "inbox" && !ui.team) || !team) {
    return false;
  }
  store.setUi({ view: TEAM_VIEWS[e.key] });
  return true;
}
