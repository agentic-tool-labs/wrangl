import { html, render, memo, Component } from "./h.js";
import { endedText } from "./bridge.js";
import { Rows } from "./rows.js";
import { createStore, retryGate } from "./store.js";
import { EtaChip } from "./ticker.js";
import { Live } from "./live-component.js";
import { firstTeam, laneChildren, memberIds, sparksOf, partyName, partyIdOf, summaryIdOf, teamIdOf, teamLeaseIds, teamRows, teamSummary } from "./select.js";
import { FlowStage, graphUi, readPrefs, shortName } from "./graph.js";
import { flowActive, flowNodes } from "./flow-model.js";
import { MemberSummary, party } from "./member-summary.js";
import { internalToggle, isInternal, shownOf, showInternal } from "./msg-filter.js";
import { bindHash } from "./deeplink.js";
import { Dialog } from "./dialog.js";
import { PerfStrip } from "./perfstrip.js";
import { HoverCard } from "./hover.js";
import { ExpandOverlay } from "./expand.js";
import { HelpDialog, helpKey } from "./member-actions.js";
import { TeamRenameDialog } from "./team-rename.js";
import { globalKey } from "./keys.js";
import { Header, Footer, applyTheme, storedTheme } from "./shell.js";
import { bootLooks, looksNote, onLooks } from "./looks.js";
import { TeamsView, TeamsRail } from "./teams.js";
import { lazy, sheet } from "./lazy.js";

const TimelineView = lazy(() => import("./timeline.js"), (m) => m.TimelineView);
const GraphMap = lazy(() => import("./graph-pack.js"), (m) => m.GraphMap);
const GraphRadial = lazy(() => import("./graph-radial.js"), (m) => m.GraphRadial);
const GraphPanel = lazy(() => import("./graph-panel.js"), (m) => m.GraphPanel);
const OverviewView = lazy(() => import("./overview.js"), (m) => m.OverviewView);
// ponytail: dead until U4 deletes them. Checklist, Queue, Lanes and Backlog are no longer tabs, keys or addresses (ah-ui-ia
// §5: their old hashes redirect to the Board). Only `#/table/<view>`, linked from nothing, reaches the first, third and fourth; the views, these branches and their NO_RAIL
// entries stay only so U4 can fold the views into the Board and delete them in one change.
const ChecklistView = lazy(() => import("./checklist.js"), (m) => m.ChecklistView, ["/ui/r6-checklist.css", "/ui/r6-shell.css"]);
const LanesView = lazy(() => import("./lanes.js"), (m) => m.LanesView, ["/ui/r6-checklist.css", "/ui/r6-shell.css"]);
const QueueView = lazy(() => import("./queue.js"), (m) => m.QueueView, ["/ui/r6-checklist.css", "/ui/r6-shell.css"]);
const BacklogView = lazy(() => import("./backlog-view.js"), (m) => m.BacklogView, ["/ui/r6-backlog.css", "/ui/r6-shell.css"]);
// The Board's waiting cards draw the Inbox's answer form, so the Plans view brings the questions sheet with it.
const PlansView = lazy(() => Promise.all([import("./plans-view.js"), sheet("/ui/r6-questions.css", "/ui/r6-shell.css")]).then(([m]) => m), (m) => m.PlansView, ["/ui/r6-plans-view.css", "/ui/r6-shell.css"]);
const InboxPage = lazy(() => import("./inbox.js"), (m) => m.InboxPage, ["/ui/r6-questions.css", "/ui/r6-shell.css"]);
const MemberPage = lazy(() => import("./member-page.js"), (m) => m.MemberPage, ["/ui/r6-member.css"]);
// The Sitrep opens the item dialog, whose frame is in the Board's sheet.
const SitrepDialog = lazy(() => sheet("/ui/r6-plans-view.css", "/ui/r6-shell.css").then(() => import("./sitrep.js")), (m) => m.SitrepDialog, ["/ui/r6-sitrep.css"]);
const RulesDialog = lazy(() => import("./rules-dialog.js"), (m) => m.RulesDialog, ["/ui/r6-rules.css"]);
const LooksPage = lazy(() => import("./looks-page.js"), (m) => m.LooksPage, ["/ui/looks-page.css", "/ui/looks.css"]);
const ConversationView = lazy(() => import("./conversation.js"), (m) => m.ConversationView, ["/ui/r6-conversation.css", "/ui/r6-plans.css"]);

// The R6 frame (D's header, main column, right rail) on the slice store. The Graph view is built; the others say so.
// Each part reads the store itself (see live-component.js), so a change draws only the parts whose values it touched.

export const START_VIEWS = ["summary"];
const NO_EDGES = [];
const TL_LEGEND = html`<span class="tl-legend" aria-hidden="true"><span class="lg brief">brief</span><span class="lg response">response</span></span>`;
const PANEL_HEADS = {
  graph: ["Graph", "dots = briefs on their way"],
  timeline: ["Timeline", null],
};

// One formatter for every row: toLocaleTimeString builds a new one per call, which the feed paid on each live update.
const HHMM = new Intl.DateTimeFormat([], { hour: "2-digit", minute: "2-digit" });
const hhmm = (iso) => (iso ? HHMM.format(new Date(iso)) : "");

export const PAIR_PAGE = 20;
// Empty pages the sheet reads through on its own before it offers "load older" instead: a quiet pair in a long history
// can come back as pages that end on the server's scan budget with no rows.
export const PAIR_EMPTY_PAGES = 4;

// Whether an exchange is between the two members, by the member ids the pair request names.
const betweenPair = (body, pair) => {
  const ends = [partyIdOf(body, "from"), partyIdOf(body, "to")];
  return pair.every((id) => ends.includes(id));
};

// The slice that holds the excerpts of an exchange; the server composes it only while a client names it.
const bodyIdOf = (exchangeId) => `exchange-body:${exchangeId.slice("exchange:".length)}`;

// Saved looks that are not safe yet, on every view: a header-menu change can fail with the Looks page nowhere in sight.
class LooksAlert extends Component {
  componentDidMount() {
    this.off = onLooks(() => this.setState({ note: looksNote() }));
    this.setState({ note: looksNote() });
  }

  componentWillUnmount() {
    this.off();
  }

  render() {
    return this.state.note ? html`<div class="mock-banner looks-alert" role="alert">${this.state.note}</div>` : null;
  }
}

// The exchanges between two members, newest first, through the server's pair filter: one paged request when it opens and
// one per "load older". A row opens to its brief and response, which are leased while it is open and released when it closes.
class PairDialog extends Live {
  state = { rows: [], next: null, loading: true, failed: false, open: new Set() };

  // Live: the team's feed moving (a new exchange, read again from the pair list) and the versions of the rows shown (a response arriving).
  select(snap) {
    const team = firstTeam(snap.slices, snap.ui);
    const base = team ? `${team.body.dir_id}/${team.body.name}` : null;
    return {
      dir: team?.body.dir_id ?? null,
      listId: base ? `list:exchanges:team:${base}` : null,
      newest: teamSummary(snap.slices, teamIdOf(snap.slices, snap.ui))?.feedNewest ?? 0,
      versions: this.state.rows.map((r) => snap.slices.get(r.id)?.version ?? 0).join(","),
      internal: showInternal(snap.ui),
      texts: [...this.state.open].map((id) => snap.slices.get(bodyIdOf(id))?.version ?? 0).join(","),
    };
  }

  // The rows shown keep their exchange slices, so a response arriving updates its row.
  leaseIds() {
    return this.state.rows.slice(0, PAIR_PAGE).map((r) => r.id);
  }

  componentDidMount() {
    super.componentDidMount();
    this.seen = this.sel.newest;
    this.load(null);
  }

  componentWillUnmount() {
    super.componentWillUnmount();
    this.props.store.lease("pair", []);
  }

  componentDidUpdate() {
    this.holdLease(this.props.store.state);
    if (!this.state.loading && this.sel.newest !== this.seen) {
      this.seen = this.sel.newest;
      this.refresh();
    }
  }

  // The newest page of the pair read again, its unseen rows put above the ones shown (an older exchange that only now exists included).
  async refresh() {
    const [member, peer] = this.props.pair;
    try {
      const doc = await this.props.store.fetchList(this.sel.listId, { member, with: peer, limit: PAIR_PAGE });
      this.setState(({ rows }) => {
        const have = new Set(rows.map((r) => r.id));
        const fresh = doc.rows.filter((r) => !have.has(r.id) && betweenPair(r.body, this.props.pair));
        return fresh.length > 0 ? { rows: [...fresh, ...rows].sort((x, y) => (x.id < y.id ? 1 : -1)) } : null;
      });
    } catch {
      // the next movement of the feed reads again
    }
  }

  async load(after, empties = 0) {
    const [member, peer] = this.props.pair;
    this.setState({ loading: true });
    try {
      const doc = await this.props.store.fetchList(this.sel.listId, { member, with: peer, limit: PAIR_PAGE, ...(after ? { after } : {}) });
      const rows = [...(after ? this.state.rows : []), ...doc.rows];
      const next = doc.next ?? null;
      if (doc.rows.length === 0 && next && empties + 1 < PAIR_EMPTY_PAGES) {
        this.setState({ rows, next });
        return this.load(next, empties + 1);
      }
      this.setState({ rows, next, loading: false, failed: false });
    } catch {
      this.setState({ loading: false, failed: true });
    }
  }

  toggle = (id) => {
    this.setState(
      ({ open }) => {
        const next = new Set(open);
        next.delete(id) || next.add(id);
        return { open: next };
      },
      () => this.props.store.lease("pair", [...this.state.open].map(bodyIdOf)),
    );
  };

  draw({ pair, onClose }, s) {
    const { rows: all, next, loading, failed, open } = this.state;
    const rows = s.internal ? all : all.filter((r) => !isInternal(r.body));
    return html`<${Dialog} title=${`Messages · ${shortName(pair[0])} ↔ ${shortName(pair[1])}`} cls="pair" onClose=${onClose}>
      <ul class="pair-list">
        ${rows.map((r) => {
          const b = this.props.store.state.slices.get(r.id)?.body ?? r.body;
          const on = open.has(r.id);
          const text = on ? this.props.store.state.slices.get(bodyIdOf(r.id))?.body : null;
          return html`<li key=${r.id} class=${`pair-row${on ? " open" : ""}`}>
            <button class="pair-head" aria-expanded=${on} onClick=${() => this.toggle(r.id)}>
              <span class="t">${hhmm(b.created)}</span>
              <span class=${`kind ${b.open ? "brief" : "response"}`}>${b.open ? "brief" : "done"}</span>
              <span class="slug">${b.slug}</span>
              <span class="route">${partyName(b, "from", this.props.store.state.slices)} → ${partyName(b, "to", this.props.store.state.slices)}</span>
            </button>
            ${on ? html`<div class="pair-body" aria-busy=${text ? "false" : "true"}>
              ${text?.gone ? html`<p class="dim">no longer available</p>` : text ? html`<h3>Brief</h3>
              <p class="prose">${text.request_excerpt ?? "—"}</p>
              <h3>Response</h3>
              ${b.has_response ? html`<p class="prose">${text.response_excerpt ?? "—"}</p>` : html`<p class="dim">No response yet.</p>`}
              ${text.excerpt_truncated ? html`<p class="dim">Excerpts only: the daemon does not serve the full text.</p>` : null}` : html`<p class="dim">Loading…</p>`}
            </div>` : null}
          </li>`;
        })}
      </ul>
      ${loading ? html`<p class="dim">Loading…</p>` : failed ? html`<p class="dim">Messages unavailable.</p>` : rows.length === 0 ? html`<p class="dim">${next ? "None in the newest messages searched." : "No messages between them."}</p>` : null}
      ${internalToggle(this.props.store, s.internal, all.filter((r) => isInternal(r.body)).length)}
      ${!loading && next ? html`<button class="tab pair-older" onClick=${() => this.load(next)}>load older</button>` : null}
    </${Dialog}>`;
  }
}

const FeedRow = memo(function FeedRow({ r, lanes = "" }) {
  const b = r.body;
  const lane = b.team && lanes.split("|").includes(`${b.dir_id}/${b.team}`) ? b.team : null;
  return html`<li class=${`feed-row${b.open ? " open" : ""}`}>
    <span class="t">${hhmm(b.created)}</span>
    <span class=${`kind ${b.open ? "brief" : "response"}`}>${b.open ? "brief" : "done"}</span>
    <span class="slug">${b.slug}</span>
    <span class="route">${party(b, "from", lane)} → ${party(b, "to", lane)}</span>
    ${b.open ? html`<${EtaChip} b=${b} />` : null}
  </li>`;
});
// ctx.lanes: a string, so each row redraws only when its row or the lane list changed (rows.js Row)
const feedRow = (r, { lanes }) => html`<${FeedRow} r=${r} lanes=${lanes} />`;

const feedKey = (r) => r.id;

const FEED_ROWS = 20;

// The team's newest exchanges, paged cold and read again when the team's summary says its feed moved.
class LiveFeed extends Live {
  rows = [];
  paged = null;
  gate = retryGate();

  page(snap) {
    const row = teamSummary(snap.slices, teamIdOf(snap.slices, snap.ui));
    const feedId = row ? `list:feed:team:${row.dirId}/${row.name}` : null;
    const stamp = row ? `${feedId}|${row.feedNewest}` : null;
    if (stamp !== null && stamp !== this.paged && this.gate.ready(feedId)) {
      this.paged = stamp;
      this.props.store.page(feedId, { limit: FEED_ROWS }).then(
        () => this.gate.ok(feedId),
        () => {
          this.paged = null;
          this.gate.fail(feedId);
        },
      );
    }
    return feedId;
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

  select(snap) {
    const feedId = this.page(snap);
    const win = feedId ? snap.lists.get(feedId)?.windows[0] : undefined;
    this.all = win?.items ?? [];
    this.rows = shownOf(this.all, snap.ui, (r) => r.body);
    const summary = teamSummary(snap.slices, teamIdOf(snap.slices, snap.ui));
    const overdue = summary ? snap.slices.get(`list:open:team:${summary.dirId}/${summary.name}`)?.body.overdue ?? 0 : 0;
    // ah2-220: the lane teams by dir and name, one primitive, so a feed row reads a lane's orchestrator as its Sub-Orchestrator
    const lanes = teamRows(snap.slices).filter((t) => t.laneOf).map((t) => `${t.dirId}/${t.name}`).sort().join("|");
    return { win: win ?? null, overdue, internal: showInternal(snap.ui), lanes };
  }

  draw(_, s) {
    const rows = this.rows;
    return html`<section class="feed" aria-label="Live feed">
      <h2 data-text="Live feed">Live feed${s.overdue > 0 ? html` <span class="eta overdue feed-overdue"><span class="eta-glyph" aria-hidden="true">⚠</span>${s.overdue} overdue</span>` : null}</h2>
      ${rows.length === 0 ? html`<p class="dim">no messages yet</p>` : html`<${Rows} cls="feed-list" rows=${rows} keyOf=${feedKey} row=${feedRow} ctx=${{ lanes: s.lanes }} />`}
      ${internalToggle(this.props.store, s.internal, this.all.filter((r) => isInternal(r.body)).length)}
    </section>`;
  }
}

// The Graph tab's stage by mode: Map (graph-pack.js), Radial (graph-radial.js) or Flow (graph.js).
export function GraphStage(props) {
  if (props.mode === "flow") {
    return html`<${FlowStage} ...${props} />`;
  }
  const Stage = props.mode === "radial" ? GraphRadial : GraphMap;
  return html`<${Stage} key=${props.mode} store=${props.store} teamId=${props.teamId} fitNonce=${props.fitNonce} legend=${props.legend} />`;
}

// What each team view shows beyond the always-held summaries; the team slice itself is leased by every team view.
const TEAM_VIEW_LEASES = {
  overview: { members: true, open: true },
  graph: { members: true, open: true, lanes: true },
  timeline: { members: true, timeline: true },
  conversation: { members: true },
  member: { members: true, open: true },
};

// Views with no right rail (the Panel button and `]` skip them). checklist, queue, lanes and backlog: dead until U4 (above).
// The member page is the member summary expanded, so it has no rail of its own.
const NO_RAIL = new Set(["allplans", "board", "inbox", "member", "looks", "checklist", "queue", "lanes", "backlog"]);

// Whether `sitrep=1` opens the dialog: only on a top-level team (no confirmed lane, no lane claim; ah-ui-ia §3.1). "drop" removes
// the param once the team's summary shows it is not one (§6); null while it is not asked for or the summary is not held yet.
const sitrepOf = (slices, ui) => {
  const row = ui.sitrep === true && ui.team ? teamSummary(slices, ui.team) : null;
  return row ? (row.laneOf === null && row.laneClaim === null ? "open" : "drop") : null;
};

// The slice ids of the team view on show, held while it is: it draws nothing and unmounts with the view.
class TeamLease extends Live {
  select() {
    return {};
  }

  leaseIds(snap, { teamId, view }) {
    const team = snap.slices.get(teamId);
    const shown = TEAM_VIEW_LEASES[view] ?? {};
    return [teamId, `processes:${teamId.slice("team:".length).split("/")[0]}`, ...teamLeaseIds(snap.slices, team, shown), ...(shown.timeline && team ? [`timeline:${team.body.dir_id}`] : [])];
  }

  draw() {
    return null;
  }
}

export class App extends Live {
  state = { toast: "", paused: false };
  nodes = [];
  sparks = [];
  nodesKey = "";
  sparksKey = "";

  select(snap) {
    const slices = snap.slices;
    const team = firstTeam(slices, snap.ui);
    const ids = memberIds(team);
    const children = snap.ui.view === "graph" ? laneChildren(slices, team, ids) : [];
    const nodes = flowNodes(slices, team, ids, children);
    const nodesKey = ids.length === 0 ? "" : JSON.stringify(nodes.map((n) => [n.id, n.role, n.name, n.lane, n.team, n.column]));
    const shownIds = [...ids, ...children.map((c) => c.id)];
    const topo = team ? slices.get(`topology:${team.body.dir_id}/${team.body.name}`) : null;
    const sparksKey = sparksOf(slices, team).map((s) => `${s.id}|${s.from}|${s.to}|${s.review ? 1 : 0}|${s.slug}`).join("\n");
    const selected = snap.ui.selected ?? null;
    return { large: snap.large === true, loading: !snap.ready || snap.busy > 0, error: snap.error, fault: snap.fault ?? null, ended: snap.ended, endedWhy: snap.endedWhy ?? null, teamId: teamIdOf(slices, snap.ui), nodesKey, sparksKey, edges: topo?.body.edges.parent_child ?? NO_EDGES, selected, selectedGone: snap.ui.view !== "member" && selected !== null && ids.length > 0 && !shownIds.includes(selected), view: snap.ui.view ?? "teams", teamMissing: snap.ready && Boolean(snap.ui.team) && !slices.has(summaryIdOf(snap.ui.team)), diagnostics: snap.ui.diagnostics === true, expand: snap.ui.expand ?? null, help: snap.ui.help === true, rules: snap.ui.rules === true, rename: snap.ui.rename ?? null, pair: snap.ui.pair ?? null, gMode: graphUi(snap.ui).mode, gOrient: graphUi(snap.ui).orient, gLegend: graphUi(snap.ui).legend, fitNonce: snap.ui.fitNonce ?? 0, inboxTab: snap.ui.inboxTab ?? "attention", inboxTeam: snap.ui.view === "inbox" && snap.ui.team ? teamIdOf(slices, snap.ui) : null, notice: snap.ui.notice ?? null, sitrep: sitrepOf(slices, snap.ui), railOpen: snap.ui.railOpen === true, selectedOwn: selected !== null && ids.includes(selected), graphCard: snap.ui.graphCard ?? null, activeKey: snap.ui.view === "graph" && snap.ui.graphMode === "flow" && team ? [...flowActive(slices, team, nodes)].join("\n") : "" };
  }

  componentDidMount() {
    super.componentDidMount();
    this.onKey = (e) => helpKey(e, this.props.store) || globalKey(e, this.props.store);
    globalThis.document?.addEventListener?.("keydown", this.onKey);
  }

  componentWillUnmount() {
    super.componentWillUnmount();
    globalThis.document?.removeEventListener?.("keydown", this.onKey);
  }

  componentDidUpdate() {
    const { selected, selectedGone, teamMissing, notice } = this.sel;
    if (notice) {
      this.props.store.setUi({ notice: null });
      this.setState({ toast: notice });
      setTimeout(() => this.setState({ toast: "" }), 4000);
    } else if (teamMissing) {
      const name = this.props.store.state.ui.team.split("/").pop();
      this.props.store.setUi({ view: "teams", team: null, selected: null });
      this.setState({ toast: `team ${name} not found` });
      setTimeout(() => this.setState({ toast: "" }), 4000);
    } else if (this.sel.sitrep === "drop") {
      this.props.store.setUi({ sitrep: false });
    } else if (selectedGone) {
      this.props.store.setUi({ selected: null });
      this.setState({ toast: `${selected.split("/").pop()} left the team` });
      setTimeout(() => this.setState({ toast: "" }), 4000);
    }
  }

  onSelect = (id) => this.props.store.setUi({ selected: this.props.store.state.ui.selected === id ? null : id });
  onView = (view) => this.props.store.setUi({ view });
  onClose = () => this.props.store.setUi({ selected: null });
  onExpandClose = () => this.props.store.setUi({ expand: null });
  onHelpClose = () => this.props.store.setUi({ help: false });
  onRulesClose = () => this.props.store.setUi({ rules: false });
  onPair = (from, to) => this.props.store.setUi({ pair: [from, to] });
  onPairClose = () => this.props.store.setUi({ pair: null });
  onSitrepClose = () => this.props.store.setUi({ sitrep: false });
  onPause = () => {
    const paused = !this.state.paused;
    document.body.classList.toggle("motion-off", paused);
    this.setState({ paused });
  };

  draw({ store, mock }, s) {
    if (s.nodesKey !== this.nodesKey) {
      this.nodesKey = s.nodesKey;
      this.nodes = s.nodesKey === "" ? [] : JSON.parse(s.nodesKey).map(([id, role, name, lane, team, column]) => ({ id, role, name, lane, team, column }));
    }
    if (s.sparksKey !== this.sparksKey) {
      this.sparksKey = s.sparksKey;
      this.sparks = s.sparksKey === "" ? [] : s.sparksKey.split("\n").map((line) => {
        const [id, from, to, review, slug] = line.split("|");
        return { id, from, to, slug, ...(review === "1" ? { review: true } : {}) };
      });
    }
    const { paused, toast } = this.state;
    return html`<div class=${`frame${paused ? " paused" : ""}`}>
      ${s.loading && !s.ended ? html`<div class="loading-bar" role="progressbar" aria-label="Loading"></div>` : null}
      ${mock ? html`<div class="mock-banner" role="note">MOCK DATA — fixtures served by web/mock, not your team</div>` : null}
      <${LooksAlert} />
      ${s.large ? html`<p class="notice" role="status">Large team: some live details refresh on open.</p>` : null}
      ${s.fault ? html`<p class="notice" role="alert">A part of the page failed: ${s.fault}</p>` : null}
      ${s.error?.startsWith("subscription too large") ? html`<p class="notice" role="alert">${s.error}</p>` : null}
      <${Header} store=${store} onView=${this.onView} paused=${paused} onPause=${this.onPause} />
      <div class=${`frame-body${s.railOpen ? " rail-open" : ""}`}>
        <div class="main-col">
        <main class=${`stage-wrap view-${s.view}`}>
          ${PANEL_HEADS[s.view] ? html`<div class="panel-head"><span data-text=${PANEL_HEADS[s.view][0]}>${PANEL_HEADS[s.view][0]}</span>${PANEL_HEADS[s.view][1] ? html`<span class="hint mono">${PANEL_HEADS[s.view][1]}</span>` : null}${s.view === "timeline" ? TL_LEGEND : null}</div>` : null}
          ${s.view !== "teams" && s.view !== "allplans" && s.view !== "looks" && (s.view === "inbox" ? s.inboxTeam : s.teamId) ? html`<${TeamLease} store=${store} teamId=${s.teamId} view=${s.view} />` : null}
          ${s.view === "teams"
            ? html`<${TeamsView} store=${store} />`
            : s.view === "allplans"
            ? html`<${PlansView} store=${store} teamId=${null} />`
            : s.view === "looks"
            ? html`<${LooksPage} store=${store} />`
            : s.view === "inbox"
            ? html`<${InboxPage} store=${store} teamId=${s.inboxTeam} tab=${s.inboxTab} />`
            : !s.teamId
            ? html`<p class="dim skeleton">${s.ended ? endedText(s.endedWhy) : s.error === "401" || s.error === "503" ? endedText(s.error) : s.error ? `could not read the data: ${s.error}` : "Loading…"}</p>`
            : s.view === "graph"
              ? html`<${GraphStage} store=${store} teamId=${s.teamId} nodes=${this.nodes} edges=${s.edges} sparks=${this.sparks} selected=${s.selected} onSelect=${this.onSelect} onPair=${this.onPair} mode=${s.gMode} orient=${s.gOrient} legend=${s.gLegend} activeKey=${s.activeKey} fitNonce=${s.fitNonce} />`
              : s.view === "member"
                ? html`<${MemberPage} key=${s.selected} store=${store} id=${s.selected} />`
                : s.view === "overview"
                ? html`<${OverviewView} store=${store} teamId=${s.teamId} />`
                : s.view === "timeline"
                  ? html`<${TimelineView} store=${store} teamId=${s.teamId} />`
                  : s.view === "conversation"
                    ? html`<${ConversationView} store=${store} />`
                    : s.view === "board"
                    ? html`<${PlansView} store=${store} teamId=${s.teamId} />`
                    : s.view === "checklist"
                    ? html`<${ChecklistView} store=${store} teamId=${s.teamId} />`
                    : s.view === "queue"
                    ? html`<${QueueView} store=${store} teamId=${s.teamId} />`
                    : s.view === "lanes"
                    ? html`<${LanesView} store=${store} teamId=${s.teamId} />`
                    : s.view === "backlog"
                    ? html`<${BacklogView} store=${store} teamId=${s.teamId} />`
                    : html`<p class="dim placeholder">The ${s.view} view is not built yet.</p>`}
        </main>
        ${s.diagnostics ? html`<${PerfStrip} read=${store.readModel} />` : null}
        </div>
        ${s.view === "teams"
          ? html`<${TeamsRail} store=${store} />`
          : NO_RAIL.has(s.view)
          ? null
          : s.view === "graph" && s.gMode !== "flow" && s.teamId && (s.selectedOwn || (!s.selected && s.graphCard))
          ? html`<aside class="rail"><${GraphPanel} store=${store} teamId=${s.teamId} /></aside>`
          : html`<aside class="rail">
          ${s.selected && !s.selectedGone ? html`<${MemberSummary} key=${s.selected} store=${store} id=${s.selected} onClose=${this.onClose} live=${s.view === "graph"} />` : html`<${LiveFeed} store=${store} />`}
        </aside>`}
      </div>
      <${Footer} store=${store} />
      <${HoverCard} store=${store} />
      ${s.expand ? html`<${ExpandOverlay} key=${s.expand} store=${store} memberId=${s.expand} onClose=${this.onExpandClose} />` : null}
      ${s.help ? html`<${HelpDialog} store=${store} onClose=${this.onHelpClose} />` : null}
      ${s.rename ? html`<${TeamRenameDialog} key=${s.rename} store=${store} teamId=${s.rename} />` : null}
      ${s.rules ? html`<${RulesDialog} key=${s.teamId} store=${store} team=${s.teamId?.split("/").pop() ?? null} onClose=${this.onRulesClose} />` : null}
      ${s.sitrep === "open" ? html`<${SitrepDialog} key=${store.state.ui.team} store=${store} teamId=${store.state.ui.team} name=${store.state.ui.team.split("/").pop()} onClose=${this.onSitrepClose} />` : null}
      ${s.pair ? html`<${PairDialog} key=${s.pair.join("|")} store=${store} pair=${s.pair} onClose=${this.onPairClose} />` : null}
      ${toast ? html`<p class="toast" role="status">${toast}</p>` : null}
    </div>`;
  }
}

// Starts the data core on the views the frame needs, loads the feed's newest window, and mounts the app.
export function start(get, root, mock) {
  const store = createStore({ get, manifest: get.manifest });
  store.start(START_VIEWS);
  applyTheme(storedTheme());
  bootLooks();
  const prefs = readPrefs();
  store.setUi({ graphMode: prefs.mode, graphOrient: prefs.orient, graphLegend: prefs.legend });
  if (typeof location !== "undefined") {
    bindHash(store, globalThis);
  }
  render(html`<${App} store=${store} mock=${mock} />`, root);
  return store;
}
