import { html } from "./h.js";
import { Live } from "./live-component.js";
import { Age, EtaChip } from "./ticker.js";
import { ageText, ageOf, retryGate } from "./store.js";
import { shownOf } from "./msg-filter.js";
import { inboxTeams, isLaneOf, isQuiet, needsCount, needsState, ownGoal, teamRows } from "./select.js";
import { hashOf, memberHref } from "./deeplink.js";
import { streamName } from "./plans-model.js";
import { TeamCloseButton, TeamCloseNote } from "./team-close.js";

// Home: one row per top-level team, its lanes nested beneath, ended teams aged out; and the rail of what needs the operator and what
// happened lately. Everything is derived from slices the page already holds (team summaries, open lists, exchanges), plus the team
// slice of each team with lanes, which names each lane's sub-orchestrator.

const LATER_KEY = "ah-later";
const ENDED_KEY = "ah.home.ended";
const SNOOZE_MS = 24 * 3600_000;
const CARDS_SHOWN = 5;
const DOTS_SHOWN = 8;
const RECENT_PER_TEAM = 10;
const RECENT_SHOWN = 12;

const storage = () => {
  try {
    return globalThis.localStorage ?? null;
  } catch {
    return null;
  }
};

const writeSnooze = (map) => {
  try {
    storage()?.setItem(LATER_KEY, JSON.stringify(map));
  } catch {
    // a blocked store keeps the snooze for this page only
  }
};

// The snoozed Needs-you cards, {card key: until ms}, expired entries dropped. An old "Later" value (a list of keys) reads as snoozed
// until 24 h from this first load, and is written back in the new shape so a reload keeps that time.
export function readSnooze(now = Date.now()) {
  let raw;
  try {
    raw = JSON.parse(storage()?.getItem(LATER_KEY) ?? "{}");
  } catch {
    return {};
  }
  if (Array.isArray(raw)) {
    const out = Object.fromEntries(raw.filter((k) => typeof k === "string").map((k) => [k, now + SNOOZE_MS]));
    writeSnooze(out);
    return out;
  }
  return raw && typeof raw === "object" ? Object.fromEntries(Object.entries(raw).filter(([, until]) => typeof until === "number" && until > now).map(([k, until]) => [k, Math.min(until, now + SNOOZE_MS)])) : {};
}

export const readEnded = () => {
  try {
    return storage()?.getItem(ENDED_KEY) === "1";
  } catch {
    return false;
  }
};

const writeEnded = (on) => {
  try {
    storage()?.setItem(ENDED_KEY, on ? "1" : "0");
  } catch {
    // a blocked store keeps the toggle for this page only
  }
};

const ageSince = (ms) => (now) => (ms > 0 ? ageText(ageOf({ as_of: ms }, {}, now)) : "");

const hhmm = new Intl.DateTimeFormat([], { hour: "2-digit", minute: "2-digit" });

// The newest exchanges across every team, newest first, from the first window each running team's feed list was paged for.
export function recentAcross(lists, rows, ui = null) {
  const out = [];
  for (const r of rows) {
    for (const x of shownOf(lists.get(feedIdOf(r))?.windows[0]?.items ?? [], ui, (i) => i.body).slice(0, RECENT_PER_TEAM)) {
      const b = x.body;
      out.push({ id: x.id, at: Date.parse(b.created ?? "") || 0, team: r.label ?? r.name, text: `${b.open ? "brief" : "done"} ${b.title || b.slug} · ${b.from_name ?? b.from} → ${b.to_name ?? b.to}`, eta: b.open && b.state ? { state: b.state, due_at: b.due_at, overdue_by: b.overdue_by } : null });
    }
  }
  return out.sort((a, b) => b.at - a.at || (a.id < b.id ? 1 : -1)).slice(0, RECENT_SHOWN);
}

const feedIdOf = (r) => `list:feed:team:${r.dirId}/${r.name}`;

export const openTeam = (store, id) => store.setUi({ team: id, view: "overview", selected: null });

const sortRows = (rows, by) => [...rows].sort((a, b) => Number(b.running) - Number(a.running) || (by === "name" ? a.name.localeCompare(b.name) : b.updated - a.updated || a.name.localeCompare(b.name)));

// The state word of a team or lane: Closed, Held, Ended, Working (anyone working), Blocked (nobody working, someone blocked) or Idle.
export const stateWord = (r) => (r.closedAt > 0 ? "Closed" : r.held ? "Held" : r.ended ? "Ended" : r.working > 0 ? "Working" : r.blocked > 0 ? "Blocked" : "Idle");

const COUNTS = [["working", "working"], ["running", "running"], ["blocked", "blocked"], ["idle", "idle"], ["unknown", "no signal"]];

// "3 working · 1 blocked · 2 idle" from the summary's member states; without them, from its working and blocked counts.
export function countsText(r) {
  const n = { working: 0, running: 0, blocked: 0, idle: 0, unknown: 0 };
  if (r.memberStates.length > 0) {
    for (const [, st] of r.memberStates) n[st in n ? st : "unknown"] += 1;
  } else {
    Object.assign(n, { working: r.working, blocked: r.blocked, idle: Math.max(0, r.members - r.working - r.blocked) });
  }
  return COUNTS.filter(([k]) => n[k] > 0).map(([k, word]) => `${n[k]} ${word}`).join(" · ") || "no members";
}

const memberName = (id) => id.split("/").pop();

// The member id of a lane's sub-orchestrator: the parent team slice's stream row whose child team is the lane.
const subOrchestratorOf = (slices, top, lane) => {
  const s = (slices.get(top.id)?.body.streams ?? []).find((x) => x?.child_team === lane.name && typeof x.sub_orchestrator === "string");
  return s ? `member:${top.dirId}/${top.name}/${s.sub_orchestrator}` : null;
};

// Home's rows: each top-level team (no confirmed lane) with its lanes, and how many of them (teams and lanes) are quiet.
export function homeRows(slices, { query = "", sort = "activity", now = Date.now() } = {}) {
  const all = teamRows(slices);
  const q = query.trim().toLowerCase();
  const tops = sortRows(all.filter((r) => r.laneOf === null && (q === "" || `${r.name} ${r.label} ${r.repo ?? ""} ${r.branch ?? ""}`.toLowerCase().includes(q))), sort);
  const rows = tops.map((t) => ({ t, lanes: all.filter((r) => r !== t && isLaneOf(r, t)) }));
  return {
    // what the toggle hides: quiet lanes, and each quiet team none of whose lanes is still on show
    quiet: rows.reduce((n, { t, lanes }) => n + Number(isQuiet(t.quietAt, now) && lanes.every((l) => isQuiet(l.quietAt, now))) + lanes.filter((l) => isQuiet(l.quietAt, now)).length, 0),
    rows: rows.map(({ t, lanes }) => ({
      id: t.id,
      name: t.name,
      label: t.label,
      real: t.realNote,
      sub: [t.repo, t.branch].filter(Boolean).join(" · "),
      word: stateWord(t),
      ended: t.ended,
      quiet: isQuiet(t.quietAt, now),
      claim: t.laneClaim,
      needs: needsCount(slices, t.id),
      blocked: needsState(slices, t.id).blocked,
      queueWaiting: t.queueWaiting,
      dots: t.memberStates.slice(0, DOTS_SHOWN),
      more: Math.max(0, t.memberStates.length - DOTS_SHOWN),
      counts: t.ended ? (t.memberCount > 0 ? `${t.memberCount} ${t.memberCount === 1 ? "member" : "members"}` : "no members") : countsText(t),
      inFlight: t.inFlight,
      updated: t.updated,
      done: t.itemsDone,
      total: t.itemsTotal,
      planLoaded: t.planLoaded,
      goal: ownGoal(t.goal, t.name),
      lanes: lanes.map((l) => ({ id: l.id, raw: l.laneStream ?? l.name, stream: l.displayName ?? streamName(l.laneStream ?? l.name), word: stateWord(l), ended: l.ended, quiet: isQuiet(l.quietAt, now), so: subOrchestratorOf(slices, t, l), goal: ownGoal(l.goal, l.name) })),
    })),
  };
}

// The rows Home draws: with quiet teams folded, a team stays while it or any of its lanes is not quiet, and only its quiet lanes go.
export const shownRows = (rows, showQuiet) =>
  showQuiet ? rows : rows.map((r) => ({ ...r, lanes: r.lanes.filter((l) => !l.quiet) })).filter((r) => !r.quiet || r.lanes.length > 0);

// A row's own goal (ah2-53) as one quiet line under its name, clipped; the whole of it, done-when too, is its tooltip and what a screen reader reads.
const goalLine = (g) => (g ? html`<div class="t-sub t-goal" title=${g.full}><span class="sr">Goal: </span>${g.text}${g.doneWhen ? html`<span class="sr"> — done when: ${g.doneWhen}</span>` : null}</div>` : null);
const goalLabel = (g) => (g ? `, goal: ${g.text}` : "");

// Whether a click on a row landed on one of its own links or buttons, which then act alone.
const onControl = (e) => Boolean(e.target?.closest?.("a, button, input, label"));

export class TeamsView extends Live {
  state = { showEnded: readEnded() };

  // The team slice of each top-level team with lanes: its streams name the sub-orchestrators.
  leaseIds(snap) {
    const rows = teamRows(snap.slices);
    return rows.filter((t) => t.laneOf === null && rows.some((r) => isLaneOf(r, t))).map((t) => t.id);
  }

  select(snap) {
    const sort = snap.ui.teamSort ?? "activity";
    const query = snap.ui.teamQuery ?? "";
    const all = teamRows(snap.slices);
    const home = homeRows(snap.slices, { query, sort });
    return { ready: snap.ready, error: snap.error, total: all.length, running: all.filter((r) => r.running).length, sort, query, home: JSON.stringify(home) };
  }

  open = (id) => openTeam(this.props.store, id);

  toggleEnded = (e) => {
    writeEnded(e.target.checked);
    this.setState({ showEnded: e.target.checked });
  };

  board = (team, lane) => this.props.store.setUi({ team, view: "board", selected: null, lane });

  sitrep = (id) => this.props.store.setUi({ team: id, view: "overview", selected: null, sitrep: true });

  draw({ store }, s) {
    const { quiet, rows: every } = JSON.parse(s.home);
    const { showEnded } = this.state;
    const rows = shownRows(every, showEnded);
    const set = (patch) => store.setUi(patch);
    return html`<section class="teams" aria-label="All teams">
      <div class="teams-bar">
        <span class="teams-count">${s.total} on this machine · ${s.running} running</span>
        <input data-search type="search" class="teams-search" placeholder="Filter teams" aria-label="Filter teams" value=${s.query} onInput=${(e) => set({ teamQuery: e.target.value })} />
        <label>Sort <select aria-label="Sort" value=${s.sort} onChange=${(e) => set({ teamSort: e.target.value })}><option value="activity">activity</option><option value="name">name</option></select></label>
      </div>
      <${TeamCloseNote} store=${store} />
      ${rows.length === 0
        ? html`<p class="dim skeleton">${!s.ready ? (s.error ? `could not read the data: ${s.error}` : "Loading…") : s.total === 0 ? "No teams on this machine. Start one with /agent-team." : s.query.trim() !== "" ? `No team matches "${s.query.trim()}". Clear the search to see every team.` : "No running teams. The toggle below shows the ended ones."}</p>`
        : html`<table class="teams-table home-table">
          <thead><tr><th>Team</th><th>State</th><th>Health</th><th>Members</th><th class="num">Progress</th><th class="num">In flight</th><th class="num">Updated</th><th class="c-act"><span class="sr">Sitrep</span></th></tr></thead>
          ${rows.map((r) => html`<tbody key=${r.id}>
            <tr data-row tabindex="0" class=${r.ended ? "ended" : ""} aria-label=${`${r.label}, ${r.word}${goalLabel(r.goal)}`} onClick=${(e) => onControl(e) || this.open(r.id)} onKeyDown=${(e) => e.key === "Enter" && e.target === e.currentTarget && this.open(r.id)}>
              <td class="t-name">
                <a class="home-link name" href=${hashOf({ view: "overview", team: r.id })} title=${r.real ?? undefined}>${r.label}</a>
                ${goalLine(r.goal)}
                ${r.sub ? html`<div class="t-sub mono" title=${r.sub}>${r.sub}</div>` : null}
                ${r.claim ? html`<div class="t-note">says it is a lane of ${r.claim} (not confirmed)</div>` : null}
              </td>
              <td class=${`t-state w-${r.word.toLowerCase()}`}>${r.word}</td>
              <td class="t-health">${r.needs > 0 || r.blocked > 0 || r.queueWaiting > 0
                ? html`${r.needs > 0 ? html`<a class="home-link attn" href=${hashOf({ view: "inbox", inboxTab: "attention", team: r.id })}>Needs you ${r.needs}</a>` : null}${r.needs > 0 && r.blocked > 0 ? " · " : null}${r.blocked > 0 ? html`<span>${r.blocked} blocked</span>` : null}${(r.needs > 0 || r.blocked > 0) && r.queueWaiting > 0 ? " · " : null}${r.queueWaiting > 0 ? html`<span>${r.queueWaiting} waiting</span>` : null}`
                : html`<span class="dim">${r.planLoaded === false ? "not loaded yet" : "Nothing waiting"}</span>`}</td>
              <td class="t-members">
                ${r.dots.length > 0 ? html`<div class="mdots">${r.dots.map(([id, st]) => html`<a key=${id} class="mdot" href=${memberHref(id)} title=${`${memberName(id)} · ${st === "unknown" ? "no signal" : st}`} aria-label=${`${memberName(id)}, ${st === "unknown" ? "no signal" : st}`}><span class=${`dot st-${st}`} aria-hidden="true"></span></a>`)}${r.more > 0 ? html`<span class="dim">+${r.more}</span>` : null}</div>` : null}
                <div class="t-counts">${r.counts}</div>
              </td>
              <td class="num">${r.planLoaded === false ? "not loaded yet" : r.total > 0 ? `${r.done} of ${r.total} items done` : html`<span class="dim">No items</span>`}</td>
              <td class="num">${r.inFlight}</td>
              <td class="num"><${Age} text=${ageSince(r.updated)} /></td>
              <td class="c-act">${r.claim ? null : html`<button type="button" class="tab" aria-label=${`Sitrep, ${r.label}`} onClick=${() => this.sitrep(r.id)}>Sitrep</button>`} <${TeamCloseButton} store=${store} teamId=${r.id} /></td>
            </tr>
            ${r.lanes.map((l) => html`<tr key=${l.id} data-row data-lane tabindex="0" class=${l.ended ? "lane-row ended" : "lane-row"} aria-label=${`Lane ${l.stream} of ${r.label}, ${l.word}${goalLabel(l.goal)}`} onClick=${(e) => onControl(e) || this.board(r.id, l.raw)} onKeyDown=${(e) => e.key === "Enter" && e.target === e.currentTarget && this.board(r.id, l.raw)}>
              <td class="t-name t-lane">
                <a class="home-link" href=${hashOf({ view: "board", team: r.id, lane: l.raw })}><span class="sr">Lane of ${r.label}: </span>${l.stream}</a>
                ${goalLine(l.goal)}
                ${l.so ? html`<div class="t-sub">sub-orchestrator <a class="home-link" href=${memberHref(l.so)}>${memberName(l.so)}</a></div>` : null}
              </td>
              <td class=${`t-state w-${l.word.toLowerCase()}`}>${l.word}</td>
              <td colspan="6"></td>
            </tr>`)}
          </tbody>`)}
        </table>`}
      ${quiet > 0 ? html`<div class="home-foot"><label class="home-toggle"><input type="checkbox" checked=${showEnded} onChange=${this.toggleEnded} /> Show ${quiet} quiet ${quiet === 1 ? "team" : "teams"}</label></div>` : null}
    </section>`;
  }
}

// The rail's cards: the Inbox's own set (inboxTeams, teamNeeds), so they and "Open Inbox (N)" never disagree. Each team with
// decisions waiting gets one card for them, then a card per blocked or failed row; orchestrator-audience alerts are `orch`, not counted.
export function railCards(slices) {
  return inboxTeams(slices, null).flatMap((r) => {
    const waiting = r.waitingOnYou;
    const alert = r.items.find((c) => c.kind === "Decision");
    const rest = r.items.filter((c) => c.kind !== "Decision");
    return waiting > 0 ? [{ key: `decision|${r.id}|${waiting}`, kind: "Decision", teamId: r.id, team: r.name, teamLabel: r.label, title: `Waiting on you · ${waiting}`, context: null, at: alert?.at ?? 0, memberId: null }, ...rest] : rest;
  });
}

export class TeamsRail extends Live {
  state = { snoozed: readSnooze(), showSnoozed: false, undo: null, failed: [] };
  paged = new Map();
  gate = retryGate();

  // A team's newest rows are read again only when its summary says its feed moved, never on a change of anything else.
  // A failed read waits out the retry gate; the rail names the team whose feed could not be read and the others carry on.
  page(snap) {
    for (const r of teamRows(snap.slices)) {
      if (r.running && this.paged.get(r.id) !== r.feedNewest && this.gate.ready(r.id)) {
        this.paged.set(r.id, r.feedNewest);
        this.props.store.page(feedIdOf(r), { limit: RECENT_PER_TEAM }).then(
          () => {
            this.gate.ok(r.id);
            this.mark(r.label, false);
          },
          () => {
            this.paged.delete(r.id);
            this.gate.fail(r.id);
            this.mark(r.label, true);
          },
        );
      }
    }
  }

  mark(name, failed) {
    if (failed === this.state.failed.includes(name)) return;
    this.setState({ failed: failed ? [...this.state.failed, name] : this.state.failed.filter((n) => n !== name) });
  }

  componentDidMount() {
    super.componentDidMount();
    this.pageOff = this.props.store.subscribe((snap) => this.page(snap));
    this.page(this.props.store.state);
  }

  componentWillUnmount() {
    super.componentWillUnmount();
    this.pageOff?.();
  }

  // After a snooze, focus goes to its Undo; after an Undo, to the card it brought back.
  componentDidUpdate() {
    super.componentDidUpdate?.();
    const want = this.focusNext;
    if (!want) return;
    this.focusNext = null;
    const el = want === "undo" ? this.base?.querySelector?.(".snooze-note button") : [...(this.base?.querySelectorAll?.("[data-card]") ?? [])].find((c) => c.getAttribute("data-card") === want)?.querySelector?.("button");
    el?.focus?.();
  }

  select(snap) {
    const rows = teamRows(snap.slices);
    return {
      cards: JSON.stringify(railCards(snap.slices)),
      recent: JSON.stringify(recentAcross(snap.lists, rows, snap.ui)),
      needs: needsCount(snap.slices, null),
    };
  }

  setSnoozed(snoozed, patch) {
    writeSnooze(snoozed);
    this.setState({ snoozed, ...patch });
  }

  snooze = (c) => {
    this.focusNext = "undo";
    this.setSnoozed({ ...this.state.snoozed, [c.key]: Date.now() + SNOOZE_MS }, { undo: { key: c.key, title: c.title } });
  };

  unsnooze = () => {
    const { [this.state.undo.key]: _, ...rest } = this.state.snoozed;
    this.focusNext = this.state.undo.key;
    this.setSnoozed(rest, { undo: null });
  };

  open = (item) => {
    const { store } = this.props;
    item.memberId ? store.setUi({ team: item.teamId, view: "conversation", selected: item.memberId }) : openTeam(store, item.teamId);
  };

  draw(_, s) {
    const now = Date.now();
    const { snoozed, showSnoozed, undo } = this.state;
    // ponytail: a snooze that runs out while the page sits still shows again on the next slice push, not on the minute
    const all = JSON.parse(s.cards).map((c) => ({ ...c, until: (snoozed[c.key] ?? 0) > now ? snoozed[c.key] : 0 }));
    const asleep = all.filter((c) => c.until > 0).length;
    const cards = all.filter((c) => showSnoozed || c.until === 0).slice(0, CARDS_SHOWN);
    const recent = JSON.parse(s.recent);
    return html`<aside class="rail teams-rail" aria-label="Needs you and recent">
      <section aria-label="Needs you">
        <h2 data-text="Needs you">Needs you</h2>
        <div class="snooze-note" role="status">${undo ? html`<span>Snoozed for 24 h: ${undo.title}</span><button type="button" class="tab" onClick=${this.unsnooze}>Undo</button><button type="button" class="tab" onClick=${() => this.setState({ undo: null })}>Dismiss</button>` : null}</div>
        ${cards.length === 0 ? html`<p class="dim">${asleep > 0 ? `All ${asleep} snoozed.` : "Nothing is waiting on you."}</p>` : cards.map((c) => html`<article class="need-card" key=${c.key} data-card=${c.key}>
          <div class="need-meta"><span class=${c.kind === "Error" || c.kind === "Alarm" ? "need-kind err" : "need-kind"}>${c.kind}</span> · ${c.teamLabel ?? c.team}${c.at > 0 ? html` · <${Age} text=${ageSince(c.at)} />` : null}</div>
          <div class="need-title">${c.title}</div>
          ${c.context ? html`<div class="need-ctx">${c.context}</div>` : null}
          <div class="need-actions"><button type="button" class="tab primary" onClick=${() => this.open(c)}>Open</button>${c.until > 0 ? html`<span class="need-asleep">Snoozed until ${hhmm.format(new Date(c.until))}</span>` : html`<button type="button" class="tab" onClick=${() => this.snooze(c)}>Snooze 24 h</button>`}</div>
        </article>`)}
        ${s.needs > 0 || asleep > 0
          ? html`<div class="need-more">${s.needs > 0 ? html`<a class="home-link" href=${hashOf({ view: "inbox", inboxTab: "attention" })}>Open Inbox (${s.needs})</a>` : null}${asleep > 0 ? html`<button type="button" class="tab" aria-pressed=${showSnoozed ? "true" : "false"} onClick=${() => this.setState({ showSnoozed: !showSnoozed })}>Show snoozed (${asleep})</button>` : null}</div>`
          : null}
      </section>
      <section aria-label="Recent across teams">
        <h2 data-text="Recent across teams">Recent across teams</h2>
        ${this.state.failed.map((n) => html`<p class="dim" key=${"failed-" + n}>Couldn't load recent messages for ${n}.</p>`)}
        ${recent.length === 0 ? html`<p class="dim">No messages yet.</p>` : html`<ul class="recent-list">${recent.map((r) => html`<li key=${r.id}><span class="t mono">${r.at > 0 ? hhmm.format(new Date(r.at)) : ""}</span> <span class="team">${r.team}</span> <span class="text">${r.text}</span>${r.eta ? html` <${EtaChip} b=${r.eta} />` : null}</li>`)}</ul>`}
      </section>
    </aside>`;
  }
}
