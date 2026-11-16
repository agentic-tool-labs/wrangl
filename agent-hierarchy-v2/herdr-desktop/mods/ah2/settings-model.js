// The settings and the self-update rules (atp-1234). Imports no Herdr code, so node tests drive it (tests/web/herdr-settings-model.test.mjs).
// Settings live in Herdr's shared store under SETTINGS_KEY, what the update check found under UPDATE_KEY (settings.js does the IO).

export const SETTINGS_KEY = "ah2.settings";
export const UPDATE_KEY = "ah2.update";
// When a tab was last clicked: the default tab is not forced over a click that just remounted the strip (tabs.js).
export const CLICK_KEY = "ah2.tab-clicked";

// The tab strip's buttons beside the … menu (tabs.js): the desk toggle; Q&A, Board, Feed and Graph are the desk's own tabs. Sitrep
// moved onto each team's row (atp-1712).
export const STRIP = [["desk", "Desk"]];
export const INTERVALS = [[0, "Live"], [5, "5 s"], [15, "15 s"], [60, "1 min"]];
// The Desk's tabs, in order (atp-1728): it sits whole in the wrangl layout's side column or in one window (`placement`, dock.js).
export const DOCKABLE = ["qa", "board", "feed", "graph"];
// The dock's ids in layouts/wrangl.json: the hidden side column and its stack, one child `ah2-<view>` per view.
export const DOCK = "ah2-panel";
export const DOCK_BODY = "ah2-panel-body";
// What the desk's status segment published for the peek drawer and the desk's tabs (main.js publishDesk): one poll feeds all three.
export const DESK_KEY = "ah2.desk";
// The open Desk window (main.js desk copy): {overlay, view, max} while open, {view, max} (its last tab and size) once closed, null
// never seen. An opener that finds it open adds `want: <tab>` and the window switches to it (reopening the key would replace it).
export const DESK_WINDOW_KEY = "ah2.desk-window";
// The Desk window's size, in one place so it can become a setting (atp-1727).
export const DESK_WINDOW_SIZE = { width: 620, height: 720 };
// Maximized (atp-1727): Herdr reads a size up to 1 as a share of its window (herdr-app src/window.rs open_overlay), so this is
// Herdr's window less a small margin. Herdr sizes an overlay only as it opens: maximize and restore reopen the window.
export const DESK_WINDOW_MAX = { width: 0.95, height: 0.95 };
export const deskWindowOpen = (w) => w?.overlay != null;
// What opens the Desk window on `view`, at its maximized or normal size (dock.js).
export const deskWindowSpec = (view, max = false) =>
  ({ mod: "ah2", props: { view: "desk", tab: view, max }, kind: "window", key: "ah2-desk", ...(max ? DESK_WINDOW_MAX : DESK_WINDOW_SIZE) });

// Where openView takes a Desk tab (spec atp-1728 §2): "dock" (the column), "open" (a new Desk window) or "switch" (the open one).
export function deskRoute(placement, dockable, windowOpen) {
  if (placement === "column" && dockable) return "dock";
  return windowOpen ? "switch" : "open";
}

// The Desk window's one subscription: every tab's slices, deduplicated, first-seen order, so switching a tab needs no new fetch.
export const unionSubs = (...lists) => [...new Set(lists.flat())];

// Q&A shows: in the column, or as the open Desk window's tab. The peek drawer hides while it does (atp-1704).
export const qaShowing = (inColumn, w) => inColumn || (deskWindowOpen(w) && w.view === "qa");

// The peek drawer after a row choice (atp-1704): `d` is {n: the Needs-you count then, qa: Q&A seen open since}. It stays
// dismissed until the count changes, or Q&A, once seen open, closes; null when not dismissed.
export function peekDismissed(d, needsYou, qaOpen) {
  if (!d || needsYou !== d.n) return null;
  if (qaOpen) return d.qa ? d : { ...d, qa: true };
  return d.qa ? null : d;
}

// Closing the drawer (b507): a row choice, its ×, or a press outside it while it is open (`hovered`: herdr's placement()).
// The record peekDismissed reads; a press outside a shut drawer keeps `d`.
export const peekClosed = (needsYou) => ({ n: needsYou, qa: false });
export const peekPressOutside = (d, hovered, needsYou) => (hovered ? peekClosed(needsYou) : d);
const findNode = (node, id) => (node?.id === id ? node : (node?.children ?? []).reduce((hit, c) => hit ?? findNode(c, id), null));

// Whether `view` is what the dock shows in layout `root`: the column open and its stack on that view. Herdr starts a block's
// copy only once shown, then keeps it alive hidden; a docked copy polls only while this holds (main.js poll).
export function dockShown(root, view) {
  const column = findNode(root, DOCK);
  return Boolean(column) && !column.hidden && findNode(column, DOCK_BODY)?.active === `ah2-${view}`;
}

// Whatever is stored, read back as a whole, valid settings object: a missing or odd field takes its default.
export function settingsOf(raw) {
  const r = raw && typeof raw === "object" ? raw : {};
  return {
    tab: r.tab === "agents-list" ? "agents-list" : "ah2",
    strip: Object.fromEntries(STRIP.map(([k]) => [k, r.strip?.[k] !== false])),
    hidden: Array.isArray(r.hidden) ? r.hidden.filter((s) => typeof s === "string") : [],
    lanes: r.lanes === "collapsed" ? "collapsed" : "nested",
    interval: INTERVALS.some(([s]) => s === r.interval) ? r.interval : 0,
    notify: r.notify === true,
    autoUpdate: r.autoUpdate === true,
    autoQa: r.autoQa === true, // Q&A takes new data at once instead of offering Updates available (atp-1708)
    // the whole Desk: the side column or one window; an old per-view `docked` list is not read
    placement: r.placement === "window" ? "window" : "column",
    // where ah runs (target-model.js targetOf): auto, local, machine:<name>, wsl: or wsl:<distro>
    ahOn: typeof r.ahOn === "string" && /^(auto|local|machine:.+|wsl:.*)$/.test(r.ahOn) ? r.ahOn : "auto",
  };
}

// The sidebar's teams once the settings apply: hidden teams leave the list (their questions still count in Needs you and Q&A),
// and collapsed lanes keep their header row only.
export function shownTeams(teams, s) {
  const hide = new Set(s.hidden);
  const walk = (list) => list.filter((t) => !hide.has(t.id)).map((t) => ({ ...t, lanes: walk(t.lanes) }));
  return walk(teams);
}

// Alerts not seen before, keyed by team and alert. The first call (seen null) only seeds: nothing old is announced.
export function newAlerts(teams, seen) {
  const all = [];
  const walk = (list) => list.forEach((t) => { t.alerts.forEach((a) => all.push({ key: `${t.id}|${a.key}`, team: t, alert: a })); walk(t.lanes); });
  walk(teams);
  const fresh = seen ? all.filter((x) => !seen.has(x.key)) : [];
  return { fresh, seen: new Set(all.map((x) => x.key)) };
}

// ---- updates ----

const parse = (v) => {
  const s = String(v).trim().replace(/^v/, "").split("+")[0];
  const i = s.indexOf("-");
  return { core: (i < 0 ? s : s.slice(0, i)).split(".").map((n) => parseInt(n, 10) || 0), pre: i < 0 ? null : s.slice(i + 1).split(".") };
};

// a is a later version than b, semver order: 2.5.0 > 2.5.0-pre.2 > 2.5.0-pre.1.7410 > 2.5.0-pre.1. Unknown on either side: false.
export function versionNewer(a, b) {
  if (!a || !b) return false;
  const x = parse(a), y = parse(b);
  for (let i = 0; i < Math.max(x.core.length, y.core.length); i++) {
    const d = (x.core[i] ?? 0) - (y.core[i] ?? 0);
    if (d) return d > 0;
  }
  if (!x.pre || !y.pre) return !x.pre && !!y.pre;
  for (let i = 0; i < Math.max(x.pre.length, y.pre.length); i++) {
    const p = x.pre[i], q = y.pre[i];
    if (p === undefined || q === undefined) return q === undefined;
    if (p === q) continue;
    const pn = /^\d+$/.test(p), qn = /^\d+$/.test(q);
    return pn && qn ? Number(p) > Number(q) : pn !== qn ? qn : p > q;
  }
  return false;
}

// Line 1 of mods/ah2/.ah2-owned, the plugin version `ah herdr install` wrote (HerdrVerb.cs). Null when not installed by ah.
export const markerVersion = (text) => (text ? text.split("\n")[0].trim() || null : null);

// The CHANGELOG.md entry for one version, without its heading: from `## [<version>]` to the next `## [`. Capped (it is stored).
export function changelogEntry(md, version, cap = 4000) {
  if (!md || !version) return "";
  const lines = md.split("\n");
  const at = lines.findIndex((l) => l.startsWith(`## [${version}]`));
  if (at < 0) return "";
  const end = lines.findIndex((l, i) => i > at && l.startsWith("## ["));
  const text = lines.slice(at + 1, end < 0 ? undefined : end).join("\n").trim();
  return text.length > cap ? `${text.slice(0, cap).replace(/\n[^\n]*$/, "")}\n\n…` : text;
}

export const BUSY_MS = 120e3;

// One check's outcome. prev: the stored UPDATE_KEY value; p: {installed, bundled, notes} as just read. Returns the value to
// store, whether the plugin is newer, whether to install now (auto-update; never twice for a version that failed), and at
// most one notice: "updated" once an install landed (even when Herdr's reload ended the run that started it), else
// "available" once per version. landed: this check follows an install that exited 0; if the mod still reads older (the two
// versions disagree, e.g. a binary built from another plugin), that version is recorded stuck: no auto-install, no Update offer.
export function updateStep(prev, p, now, auto, landed = false) {
  const was = prev && typeof prev === "object" ? prev : {};
  const next = { ...was, installed: p.installed, bundled: p.bundled, notes: p.notes, checked: now };
  const newer = versionNewer(p.bundled, p.installed);
  let notice = null;
  if (landed && newer) {
    next.installing = 0;
    next.failed = { version: p.bundled, stuck: true, text: `the installed mod still reports v${p.installed}` };
    next.told = p.bundled; // the stuck notice is this version's one notice
    return { next, newer, install: false, notice: { kind: "stuck", version: p.bundled, installed: p.installed } };
  }
  const busy = Boolean(was.installing) && now - was.installing < BUSY_MS;
  if (was.installing && p.installed && !newer) {
    next.installing = 0;
    next.failed = null;
    notice = { kind: "updated", version: p.installed, again: was.installed === p.installed };
  } else if (!busy) next.installing = 0;
  const install = newer && auto && !busy && was.failed?.version !== p.bundled;
  if (newer && !install && !busy && was.told !== p.bundled) {
    next.told = p.bundled;
    notice = { kind: "available", version: p.bundled, installed: p.installed };
  }
  return { next, newer, install, notice };
}

// The About section's text, and its version line from the update state the check already holds (nothing is fetched for it).
export const ABOUT = {
  title: "Wrangl: Agent Hierarchy",
  what: "Runs a team of agents from one Claude Code session. The commands are ah and ahd.",
  url: "https://github.com/agentic-tool-labs/wrangl",
};
export const aboutVersions = (u) => `mod ${u?.installed ? `v${u.installed}` : "—"} · ah ${u?.bundled ? `v${u.bundled}` : "—"}`;
