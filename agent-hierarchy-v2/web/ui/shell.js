import { html } from "./h.js";
import { Live } from "./live-component.js";
import { Age } from "./ticker.js";
import { freshness } from "./store.js";
import { firstTeam, memberIds, memberOf, needsLeaseIds, needsState, needsText, blockedText, teamIdOf, teamRows } from "./select.js";
import { hintOf } from "./keys.js";
import { TeamRenameButton } from "./team-rename.js";
import { choose, lookFor, looksFor, stateOf } from "./looks.js";

// The frame's 40px header and 24px status bar: the team switcher, the five team tabs, the Panel button, the freshness chip,
// the Inbox button and the Settings menu; and the status bar's context, the members by state, the counts the operator
// watches, and the key hints (generated from KEY_TABLE).

export const TEAM_TABS = [
  ["overview", "Overview", "1"],
  ["board", "Board", "2"],
  ["graph", "Graph", "3"],
  ["timeline", "Timeline", "4"],
  ["conversation", "Conversation", "5"],
];
const TAB_LABEL = Object.fromEntries(TEAM_TABS.map(([id, label]) => [id, label]));

// Views that draw a right rail; the Panel button and `]` act only on these.
export const RAIL_VIEWS = ["teams", "overview", "graph", "timeline", "conversation"];

// The one "Needs you" count the header's Inbox button and the status bar share: the shown team's, or every team's on Home.
export function needsOf(snap) {
  const view = snap.ui.view ?? "teams";
  const id = isLevel0(view) || (view === "inbox" && !snap.ui.team) ? null : teamIdOf(snap.slices, snap.ui);
  const scoped = Boolean(id) || isLevel0(view) || view === "inbox";
  const st = scoped ? needsState(snap.slices, id) : { n: 0, blocked: 0, pending: false, blockers: 0 };
  return { n: st.n, blocked: st.blocked, pending: st.pending, blockers: st.blockers, team: id, leaseIds: scoped ? needsLeaseIds(snap.slices, id) : [] };
}

const needsTitle = (pending) => (pending ? "A plan is not loaded yet; the count may change" : undefined);

// The Inbox, scoped to the shown team when there is one.
export const openInbox = (store, team) => store.setUi({ view: "inbox", inboxTab: "attention", team: team ?? null, selected: null, menu: null });

// The page title: "ah · <team> · <tab>" (P2).
export function titleOf(view, team, inboxTab) {
  const page = view === "teams" ? "Home" : view === "allplans" ? "Board" : view === "inbox" ? (inboxTab === "qa" ? "Inbox · Q&A" : "Inbox") : view === "member" ? "Member" : TAB_LABEL[view] ?? view;
  return ["ah", view === "allplans" ? "All teams" : view === "teams" ? null : team, page].filter(Boolean).join(" · ");
}

const THEME_KEY = "ah-theme";
const THEMES = ["system", "light", "dark"];

export function applyTheme(mode, doc = globalThis.document) {
  if (mode === "system") {
    doc?.documentElement?.removeAttribute("data-theme");
  } else {
    doc?.documentElement?.setAttribute("data-theme", mode);
  }
  try {
    mode === "system" ? globalThis.localStorage?.removeItem(THEME_KEY) : globalThis.localStorage?.setItem(THEME_KEY, mode);
  } catch {
    // a blocked store keeps the choice for this page only
  }
}

export function storedTheme() {
  try {
    const mode = globalThis.localStorage?.getItem(THEME_KEY);
    return THEMES.includes(mode) ? mode : "system";
  } catch {
    return "system";
  }
}

// The two views of All teams; every other view is of one team.
export const isLevel0 = (view) => view === "teams" || view === "allplans";

// a team by its display name (select.js teamLabel), told apart by its repo when two read the same
const teamLabel = (row, rows) => (rows.filter((r) => r.label === row.label).length > 1 ? `${row.label} · ${row.repo ?? row.dirId.slice(0, 6)}` : row.label);

class TeamSwitcher extends Live {
  select(snap) {
    const rows = teamRows(snap.slices);
    return {
      open: snap.ui.menu === "team",
      level0: isLevel0(snap.ui.view ?? "teams"),
      teamId: teamIdOf(snap.slices, snap.ui),
      list: rows.map((r) => `${r.id}|${r.name}|${r.repo ?? ""}|${r.dirId}|${r.running ? 1 : 0}|${r.label.replaceAll("|", "¦")}`).join("\n"),
    };
  }

  draw({ store }, s) {
    const rows = s.list === "" ? [] : s.list.split("\n").map((line) => {
      const [id, name, repo, dirId, running, label] = line.split("|");
      return { id, name, repo: repo || null, dirId, running: running === "1", label };
    });
    const here = s.level0 ? null : rows.find((r) => r.id === s.teamId);
    const current = s.level0 ? "All teams" : here ? teamLabel(here, rows) : "…";
    // a display name hides the real one, which commands and addresses still use: it is the tooltip
    const real = (r) => (r && r.label !== r.name ? r.name : undefined);
    const choose = (patch) => store.setUi({ menu: null, selected: null, ...patch });
    // From Home a team opens on its Overview, from the Board of all teams on its Board, and from a tab on that tab. "All teams"
    // is the Board of all teams while the Board is current, else Home.
    const view = store.state.ui.view ?? "teams";
    const teamView = view === "teams" || view === "member" ? "overview" : view === "allplans" ? "board" : view;
    const allView = view === "board" || view === "allplans" ? "allplans" : "teams";
    return html`<span class="switcher">
      <button class="switch-btn" title=${real(here)} aria-haspopup="menu" aria-expanded=${s.open} onClick=${() => store.setUi({ menu: s.open ? null : "team" })}>${current} <span aria-hidden="true">▾</span></button>
      ${s.open ? html`<button class="menu-scrim" aria-label="Close menu" onClick=${() => store.setUi({ menu: null })}></button>
        <ul class="menu" role="menu" aria-label="Teams">
          <li role="none"><button role="menuitem" class=${s.level0 ? "on" : ""} onClick=${() => choose({ view: allView })}>All teams</button></li>
          ${rows.map((r) => html`<li key=${r.id} role="none"><button role="menuitem" class=${!s.level0 && r.id === s.teamId ? "on" : ""} title=${real(r)} onClick=${() => choose({ team: r.id, view: teamView })}>
            <span class=${r.running ? "dot live" : "dot"} aria-hidden="true"></span>${teamLabel(r, rows)}${r.running ? "" : html` <span class="muted">ended</span>`}</button></li>`)}
        </ul>` : null}
    </span>`;
  }
}

// The Settings menu (P1): focus moves to the first item when it opens, ↑ ↓ move within it, and focus returns to the button
// when it closes. Theme is a three-way radio here until glass G2 moves it to the top bar's "Theme / Optics" button.
const THEME_LABEL = { system: "System", light: "Light", dark: "Dark" };

class SettingsMenu extends Live {
  state = { theme: storedTheme(), optics: stateOf() };

  select(snap, { paused }) {
    return { open: snap.ui.menu === "more", diagnostics: snap.ui.diagnostics === true, paused };
  }

  componentDidUpdate() {
    if (this.sel.open && !this.wasOpen) {
      this.list?.querySelectorAll?.("[role^=menuitem]")[0]?.focus?.();
      // The Looks page may have saved, renamed or deleted a look since the menu last drew.
      this.setState({ optics: stateOf() });
    } else if (!this.sel.open && this.wasOpen && this.list === null && (!globalThis.document?.activeElement || globalThis.document.activeElement === globalThis.document.body)) {
      this.btn?.focus();
    }
    this.wasOpen = this.sel.open;
  }

  pick = (theme) => {
    applyTheme(theme);
    this.setState({ theme });
  };

  pickLook = (theme, id) => {
    this.setState({ optics: choose(theme, id) });
  };

  onKey = (e) => {
    if (e.key !== "ArrowDown" && e.key !== "ArrowUp" && e.key !== "Home" && e.key !== "End") {
      return;
    }
    const items = [...this.list.querySelectorAll("[role^=menuitem]")];
    const at = items.indexOf(globalThis.document.activeElement);
    const to = e.key === "Home" ? 0 : e.key === "End" ? items.length - 1 : (at + (e.key === "ArrowDown" ? 1 : -1) + items.length) % items.length;
    items[to]?.focus();
    e.preventDefault();
  };

  draw({ store, onPause }, s) {
    const close = (fn) => () => {
      store.setUi({ menu: null });
      fn();
    };
    return html`<span class="switcher settings">
      <button class="more-btn" aria-haspopup="menu" aria-expanded=${s.open} ref=${(el) => (this.btn = el)} onClick=${() => store.setUi({ menu: s.open ? null : "more" })}>Settings</button>
      ${s.open ? html`<button class="menu-scrim" aria-label="Close menu" tabindex="-1" onClick=${() => store.setUi({ menu: null })}></button>
        <ul class="menu right" role="menu" aria-label="Settings" ref=${(el) => (this.list = el)} onKeyDown=${this.onKey}>
          <li role="none"><button role="menuitemcheckbox" aria-checked=${s.paused} onClick=${close(onPause)}>Pause motion</button></li>
          <li role="none"><button role="menuitemcheckbox" aria-checked=${s.diagnostics} onClick=${close(() => store.setUi({ diagnostics: !s.diagnostics }))}>Performance strip</button></li>
          <li role="none"><button role="menuitem" onClick=${close(() => store.setUi({ help: true }))}>Keys and actions</button></li>
          <li role="none"><button role="menuitem" onClick=${close(() => store.setUi({ rules: true }))}>Rules…</button></li>
          <li role="separator" class="menu-sep"></li>
          <li role="none" class="menu-label" id="theme-label">Theme</li>
          <li role="none"><ul class="menu-group" role="group" aria-labelledby="theme-label">
            ${THEMES.map((t) => html`<li key=${t} role="none"><button role="menuitemradio" aria-checked=${this.state.theme === t} onClick=${() => this.pick(t)}>${THEME_LABEL[t]}</button></li>`)}
          </ul></li>
          <li role="none"><button role="menuitem" onClick=${close(() => store.setUi({ view: "looks" }))}>Looks…</button></li>
          ${["light", "dark"].map((slot) => html`<li key=${slot} role="none" class="menu-label" id=${`look-${slot}-label`}>${slot === "light" ? "Light look" : "Dark look"}</li>
          <li key=${`${slot}-group`} role="none"><ul class="menu-group" role="group" aria-labelledby=${`look-${slot}-label`}>
            ${looksFor(this.state.optics, slot).map((l) => html`<li key=${l.id} role="none"><button role="menuitemradio" aria-checked=${lookFor(this.state.optics, slot).id === l.id} onClick=${() => this.pickLook(slot, l.id)}>${l.name}</button></li>`)}
          </ul></li>`)}
        </ul>` : null}
    </span>`;
  }
}

// Under 1100px the right rail is behind this button (the key is `]`); wider, CSS hides it. It is not drawn on a view with no rail (L2).
class RailToggle extends Live {
  select(snap) {
    return { open: snap.ui.railOpen === true, rail: RAIL_VIEWS.includes(snap.ui.view ?? "teams") };
  }

  draw({ store }, s) {
    return s.rail ? html`<button class="rail-toggle" aria-pressed=${s.open} title="Show or hide the panel (])" onClick=${() => store.setUi({ railOpen: !s.open })}>Panel</button>` : null;
  }
}

export class Header extends Live {
  // The questions slices the one Needs-you count reads, for every team in its scope (F1).
  leaseIds(snap) {
    return needsOf(snap).leaseIds;
  }

  select(snap, { paused }) {
    const view = snap.ui.view ?? "teams";
    const rows = teamRows(snap.slices);
    const id = teamIdOf(snap.slices, snap.ui);
    const mine = id ? rows.find((r) => r.id === id) : null;
    const needs = needsOf(snap);
    return {
      view,
      // The team tabs show wherever a team is in scope: its tabs, its member pages and its Inbox.
      tabs: !isLevel0(view) && !(view === "inbox" && !snap.ui.team) && Boolean(mine),
      branch: isLevel0(view) ? null : mine?.branch ?? null,
      // the team (or lane) the header names, which its Rename acts on (ah2-218 B2)
      renameId: !isLevel0(view) && !(view === "inbox" && !snap.ui.team) && mine ? mine.id : null,
      // the Sitrep button: on a top-level team's tabs only (ah-ui-ia §1.2, §3.1)
      sitrep: Boolean(mine) && TAB_LABEL[view] !== undefined && mine.laneOf === null && mine.laneClaim === null,
      amber: snap.errorSince !== null || snap.ended,
      groom: [...new Set((mine ? [mine] : rows).map((r) => r.dirId))].some((d) => snap.slices.get(`backlog:${d}`)?.body?.health?.due === true),
      needs: needs.n,
      needsPending: needs.pending,
      needsBlockers: needs.blockers,
      needsTeam: needs.team,
      title: titleOf(view, isLevel0(view) || (view === "inbox" && !snap.ui.team) ? null : mine?.label ?? null, snap.ui.inboxTab),
      paused,
    };
  }

  draw({ store, onView, paused, onPause }, s) {
    if (globalThis.document && globalThis.document.title !== s.title) {
      globalThis.document.title = s.title;
    }
    return html`<header class="shell-head">
      <div class="crumbs"><span class="brand-word">Wrangl</span><span class="sep" aria-hidden="true">/</span><${TeamSwitcher} store=${store} />${s.branch ? html`<span class="branch mono" title=${s.branch}>${s.branch}</span>` : null}${s.renameId ? html`<${TeamRenameButton} store=${store} teamId=${s.renameId} />` : null}</div>
      <${RailToggle} store=${store} />
      ${s.groom ? html`<span class="chip amber" title="The backlog is due for grooming">groom due</span>` : null}
      <span class=${s.amber ? "chip amber fresh" : "chip fresh"} title=${freshness(store.state, Date.now()).title}><${Age} text=${(now) => freshness(store.state, now).text} /></span>
      ${s.view === "teams" ? html`<button class="tab search-btn" onClick=${() => globalThis.document?.querySelector("[data-search]")?.focus()}>Search</button>` : null}
      <nav class="views" aria-label="Tabs">
        ${s.tabs ? TEAM_TABS.map(([id, label, key]) => html`<button key=${id} class=${s.view === id ? "tab on" : "tab"} aria-pressed=${s.view === id} title=${`${label} (${key})`} onClick=${() => onView(id)}>${label}</button>`) : null}
      </nav>
      ${s.sitrep ? html`<button class="tab sitrep-btn" aria-haspopup="dialog" onClick=${() => store.setUi({ sitrep: true })}>Sitrep</button>` : null}
      <button class=${s.view === "inbox" ? "tab inbox-btn on" : "tab inbox-btn"} aria-pressed=${s.view === "inbox"} onClick=${() => openInbox(store, s.needsTeam)}>Inbox · <span class="num" title=${needsTitle(s.needsPending)}>${needsText(s.needs, s.needsPending, s.needsBlockers)}</span></button>
      <${SettingsMenu} store=${store} paused=${paused} onPause=${onPause} />
    </header>`;
  }
}

// The members of the shown team by state, in the order the bar draws them. Gone members have left the team and are not counted,
// and a view that holds no member slices (Plans) shows no states rather than four zeros.
const STATES = [["working", "busy", "working"], ["running", "running", "running"], ["blocked", "blocked", "blocked"], ["idle", "idle", "idle"], ["unknown", "unknown", "no signal"]];

export class Footer extends Live {
  select(snap) {
    const view = snap.ui.view ?? "teams";
    const rows = teamRows(snap.slices);
    const id = teamIdOf(snap.slices, snap.ui);
    const mine = id ? rows.find((r) => r.id === id) : null;
    const scope = isLevel0(view) ? rows : mine ? [mine] : [];
    const needs = needsOf(snap);
    const team = isLevel0(view) ? null : firstTeam(snap.slices, snap.ui);
    const states = memberIds(team).map((id) => memberOf(snap.slices, team, id)).map((m) => (m.running ? "running" : m.state));
    return {
      view,
      teams: rows.length,
      running: rows.filter((r) => r.running).length,
      team: mine?.label ?? null,
      members: mine?.members ?? 0,
      byState: states.some((x) => x !== "gone") ? STATES.map(([state]) => states.filter((x) => x === state).length).join("|") : "",
      needs: needs.n,
      needsPending: needs.pending,
      needsBlockers: needs.blockers,
      needsBlocked: needs.blocked,
      needsTeam: needs.team,
      inFlight: scope.reduce((n, r) => n + r.inFlight, 0),
      hints: hintOf(view, { team: Boolean(mine) && !(view === "inbox" && !snap.ui.team) }),
    };
  }

  draw({ store }, s) {
    const left = isLevel0(s.view) ? `${s.teams} ${s.teams === 1 ? "team" : "teams"} · ${s.running} running` : s.team ? `${s.team} · ${s.members} members` : "";
    const counts = s.byState === "" ? [] : s.byState.split("|").map(Number);
    return html`<footer class="shell-foot" aria-label="Status">
      <span class="seg">${left}</span>
      ${counts.length > 0 ? html`<span class="seg states">${STATES.map(([, cls, word], i) => (cls === "running" && counts[i] === 0 ? null : html`<span key=${cls} class=${counts[i] > 0 && cls === "blocked" ? `st ${cls} attn` : `st ${cls}`}><strong>${counts[i]}</strong> ${word}</span>`))}</span>` : null}
      <span class="seg counts"><button class=${s.needs > 0 ? "count needs attn" : "count needs"} onClick=${() => openInbox(store, s.needsTeam)}>Needs you <strong title=${needsTitle(s.needsPending)}>${needsText(s.needs, s.needsPending, s.needsBlockers)}</strong>${blockedText(s.needsBlocked)}</button><span class="count"><strong>${s.inFlight}</strong> ${s.inFlight === 1 ? "brief" : "briefs"} open</span></span>
      <span class="seg hints" title=${s.hints}>${s.hints}</span>
    </footer>`;
  }
}
