import { html } from "./h.js";
import { Live } from "./live-component.js";
import { loadD3 } from "./lazy.js";
import { flip, forget } from "./flip.js";
import { memberIds, memberOf, nameIn, summaryIdOf, teamLabel } from "./select.js";
import { Age } from "./ticker.js";
import { ageOf, ageText } from "./store.js";
import { boardRows, glyphOf, partsOf } from "./graph-world.js";
import { TileCard, exchangeRows } from "./graph-panel.js";
import { memberHref } from "./deeplink.js";
import { itemTarget } from "./board-kanban.js";
import { COLUMNS, streamName, STATE_LABEL, chipOf, choose, itemHistory, prefOf, readPref } from "./plans-model.js";

// Plans · Board: the work as a zoomable icicle, state first. The root splits into the board's state columns (COLUMNS, equal and
// fixed, so they never shift); inside each, team → epic → deliverable → item, or team → stream → item. The hierarchy runs down the
// stage and a tile's width is its share of the column's work. Preact draws the tiles once per data change; d3 lays them out, and the
// zoom (breadth only, inside a column) writes their boxes in at most one batch per frame while it moves. Nothing runs at rest.

const MSGS = 14;
// The height of a header strip: the root, the column heads and the focus.
const HEAD = 40;
// The breadth of an empty column: a strip wide enough to name its state.
const EMPTY = 0.08;
// A tile's stripe: the state's mark, so the colour is only a second cue after the status pill.
const MARK = { done: "ok", in_progress: "working", waiting_orch: "working", blocked: "blocked", waiting_user: "blocked", dropped: "gone" };
// The states a column stands for: a tile in one of them needs no pill.
const COL_STATES = { next: ["not_started", "next"], progress: ["in_progress"], review: ["reported", "in_review", "approved", "waiting_orch"], waiting: ["waiting_user"], done: ["done"] };
const COL_MARK = { next: "idle", progress: "working", review: "working", waiting: "blocked", stuck: "blocked", done: "ok" };
const SEEN_SUFFIX = ", changed since your last look";
const NO_BOARD = { cells: {}, caps: {}, stale: [], count: 0 };

const reduced = () => typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;
const ageSince = (ms) => (now) => (ms > 0 ? ageText(ageOf({ as_of: ms }, {}, now)) : "—");
// The thing a tile id names: `<col>#<ent>`, or the root and the columns themselves.
const entOf = (id) => (typeof id === "string" && id.includes("#") ? id.slice(id.indexOf("#") + 1) : id ?? null);
const teamOf = (c) => `${c.dirId}/${c.team}`;

// A container's state from its leaves: done when every leaf is done or dropped, blocked when any is, working when any is under way.
function rollState(leaves) {
  const st = leaves.map((l) => l.data.state).filter(Boolean);
  if (st.length === 0) {
    return "not_started";
  }
  if (st.every((s) => s === "done" || s === "dropped")) {
    return st.every((s) => s === "dropped") ? "dropped" : "done";
  }
  if (st.some((s) => s === "blocked" || s === "waiting_user")) {
    return "blocked";
  }
  return st.some((s) => s === "in_progress" || s === "waiting_orch" || s === "done") ? "in_progress" : "not_started";
}

const markText = (m) => (m?.kind === "moved" ? `${STATE_LABEL[m.from] ?? m.from} → ${STATE_LABEL[m.to] ?? m.to}` : m?.kind === "new" ? "new" : m?.kind === "done" ? "done since" : "changed");

// An item's history as both boards draw it: loading, unavailable, none, or the list, newest first.
export const historyView = (h) => html`<h3>History</h3>
  ${h === null
    ? html`<p class="dim">Loading…</p>`
    : h === "error"
      ? html`<p class="dim">History unavailable</p>`
      : h.length === 0
        ? html`<p class="dim">No history</p>`
        : html`<ol class="pl-history">${h.map((x) => html`<li key=${x.key}><span class="t">${x.text}</span>${x.by ? html`<span class="mono dim">${x.by}</span>` : null}${x.at ? html`<span class="pl-age"><${Age} text=${ageSince(x.at)} /></span>` : null}</li>`)}</ol>`}`;

export class BoardIcicle extends Live {
  stored = { plansEpics: readPref("plansEpics") };
  els = new Map();
  hits = new Map();
  tx = { x: 0, k: 1 };
  state = { history: null };

  // The board's cards (PlansView's one board model), parsed once per change.
  parsed(json) {
    if (this.boardFor !== json) {
      this.boardFor = json;
      this.board = JSON.parse(json ?? "null") ?? NO_BOARD;
    }
    return this.board;
  }

  // The team the view is about: the selected tile's, else the focus's, else the team at team level.
  teamKeyOf(ui, teamId) {
    return partsOf(ui.plansSel)?.key ?? partsOf(entOf(ui.plansFocus))?.key ?? (teamId ? teamId.slice("team:".length) : null);
  }

  // Each shown team's message counts; the backlog of each dir when Show epics is on; the holders of the team in view, so an owner
  // shows its state; and the selected item's messages. The items themselves are PlansView's lease.
  leaseIds(snap, props) {
    const { ui, slices } = snap;
    const cards = Object.values(this.parsed(props.board).cells).flat();
    const ids = [...new Set(cards.map((c) => `item-msgs:${teamOf(c)}`))].sort();
    if (prefOf(ui, "plansEpics", this.stored) === "on") {
      ids.push(...new Set(cards.map((c) => `backlog:${c.dirId}`)));
    }
    const focus = this.teamKeyOf(ui, props.teamId);
    if (focus) {
      const team = slices.get(`team:${focus}`);
      const members = team ? new Set(memberIds(team)) : null;
      const backlog = slices.get(`backlog:${focus.split("/")[0]}`)?.body.items ?? [];
      const holders = new Set([...cards.filter((c) => teamOf(c) === focus).map((c) => c.owner), ...backlog.map((b) => b.holder)].filter(Boolean));
      ids.push(...[...holders].sort().map((h) => `member:${focus}/${h}`).filter((id) => !members || members.has(id)));
    }
    if (ui.item?.startsWith("exchange:")) {
      ids.push(ui.item);
    }
    const sel = partsOf(ui.plansSel);
    if (sel?.kind === "i") {
      const msgs = `item-msgs:${sel.key}/${sel.item}`;
      ids.push(msgs, ...(slices.get(msgs)?.body.ids ?? []).slice(0, MSGS).map((x) => `exchange:${sel.dir}/${x}`));
    }
    return ids;
  }

  select(snap, props) {
    const { ui, slices } = snap;
    const b = this.parsed(props.board);
    const epics = prefOf(ui, "plansEpics", this.stored) === "on";
    // Open in board's stream filter: only that stream's cards, wherever they nest.
    const only = partsOf(ui.plansStream);
    const keep = only ? (c) => teamOf(c) === only.key && c.stream === only.stream : null;
    const dirs = [...new Set(Object.values(b.cells).flat().map((c) => c.dirId))];
    const backlogs = epics ? new Map(dirs.map((d) => [d, new Map((slices.get(`backlog:${d}`)?.body.items ?? []).map((x) => [x.key, x]))])) : null;
    const rows = boardRows({ columns: COLUMNS, cells: b.cells, caps: b.caps ?? {}, backlogs, keep, scope: props.teamId ? teamLabel(slices.get(summaryIdOf(props.teamId))?.body ?? { name: props.teamId.split("/").pop() }) : null, shown: (c) => teamLabel(slices.get(summaryIdOf(`team:${c.dirId}/${c.team}`))?.body ?? { name: c.team }) });
    // A holder's state and role from its member slice; one not held reads as unknown, never as gone.
    const people = new Map();
    const focus = this.teamKeyOf(ui, props.teamId);
    const team = focus ? slices.get(`team:${focus}`) : null;
    team && memberIds(team).filter((id) => slices.has(id)).forEach((id) => {
      const m = memberOf(slices, team, id);
      people.set(`${focus}|${m.name}`, { state: m.state, role: m.role });
    });
    // Messages: an item's own count, a changed stream's by_stream count; unknown (null) until the team's counts arrive.
    const out = rows.map((r) => {
      const c = r.team ? slices.get(`item-msgs:${r.team}`)?.body : undefined;
      const msgs = r.kind === "task" ? (c ? c.counts?.[r.item] ?? 0 : null) : r.kind === "stream" && r.weight !== undefined && !r.item ? (c ? c.by_stream?.[r.stream] ?? 0 : null) : r.kind === "decision" ? 0 : undefined;
      // ah2-220: how the holder reads (a lane's orchestrator is its Sub-Orchestrator); the holder itself stays the key
      const who = r.holder ? people.get(`${r.team}|${r.holder}`) ?? null : null;
      const shown = r.holder && r.team ? nameIn(slices, r.team.slice(0, r.team.lastIndexOf("/")), r.team.slice(r.team.lastIndexOf("/") + 1), r.holder) : { label: r.holder, title: r.holder };
      return { ...r, msgs, who, holderLabel: shown.label, holderTitle: shown.title };
    });
    const sel = ui.plansSel ?? null;
    const sp = partsOf(sel);
    const msgIds = sp?.kind === "i" ? slices.get(`item-msgs:${sp.key}/${sp.item}`)?.body.ids ?? [] : [];
    // An item deep link (§2.6): the card it names, selected once it is on the board; else a note says it is not.
    const all = Object.values(b.cells).flat();
    const hit = ui.item ? all.find((c) => c.id === itemTarget(all, ui.item, slices)) : null;
    return {
      itemEnt: hit ? `i:${teamOf(hit)}|${hit.stream}|${hit.key}` : null,
      itemMissing: ui.item && !hit && all.length > 0 && (!ui.item.startsWith("exchange:") || slices.has(ui.item) || snap.gone?.has(ui.item) === true) ? ui.item : null,
      itemFor: ui.item ?? null,
      rows: JSON.stringify(out),
      focus: ui.plansFocus ?? null,
      sel,
      owner: ui.plansOwner ?? "",
      epics,
      stream: ui.plansStream ?? null,
      msgRows: sp?.kind === "i" ? JSON.stringify(exchangeRows(slices, msgIds.slice(0, MSGS).map((x) => `exchange:${sp.dir}/${x}`), MSGS, ui)) : "null",
    };
  }

  componentDidMount() {
    super.componentDidMount();
    loadD3().then((d3) => {
      this.d3 = d3;
      !this.gone && this.forceUpdate();
    }, () => {
      this.failed = true;
      !this.gone && this.forceUpdate();
    });
  }

  componentDidUpdate() {
    super.componentDidUpdate();
    this.bind();
    this.layout();
    if (this.flipping) {
      this.flipping = false;
      flip(this.box, this.nodes.map((n) => n.id));
    }
    if (this.refocus) {
      this.hits.get(this.refocus)?.focus();
      this.refocus = null;
    }
    this.loadHistory();
    if (this.sel.itemEnt && this.itemDone !== this.sel.itemFor) {
      this.itemDone = this.sel.itemFor;
      this.setUi({ plansSel: this.sel.itemEnt });
    }
  }

  componentWillUnmount() {
    this.gone = true;
    this.unbind();
    globalThis.cancelAnimationFrame?.(this.frame);
    super.componentWillUnmount();
  }

  // The hierarchy for the rows, made once per change of the rows. The columns get equal, fixed breadths; what is inside a column
  // shares its breadth by size.
  tree(rowsJson) {
    if (this.treeFor === rowsJson) {
      return this.root;
    }
    const d3 = this.d3;
    this.treeFor = rowsJson;
    const root = d3.stratify().id((d) => d.id).parentId((d) => d.parent)(JSON.parse(rowsJson));
    root.sum((d) => d.weight ?? 0).sort((a, b) => (a.data.order ?? 0) - (b.data.order ?? 0) || b.value - a.value);
    d3.partition().size([1, 1])(root);
    // A column with cards takes an equal share; an empty one is a narrow strip that still names its state.
    const cols = root.children ?? [];
    const empty = cols.filter((c) => !c.children).length;
    const strip = empty === cols.length ? 1 / cols.length : EMPTY;
    const share = empty === cols.length ? strip : (1 - empty * strip) / (cols.length - empty);
    let x0 = 0;
    for (const c of cols) {
      const w = c.children ? share : strip;
      const span = c.x1 - c.x0;
      for (const n of c.descendants().slice(1)) {
        n.x0 = x0 + (span ? (n.x0 - c.x0) / span : 0) * w;
        n.x1 = x0 + (span ? (n.x1 - c.x0) / span : 0) * w;
      }
      c.x0 = x0;
      c.x1 = x0 += w;
    }
    root.x0 = 0;
    root.x1 = 1;
    root.each((n) => {
      const leaves = n.children ? n.leaves() : [n];
      n.done = leaves.reduce((s, l) => s + (l.data.done ?? 0), 0);
      n.total = leaves.reduce((s, l) => s + (l.data.total ?? 0), 0);
      n.marked = leaves.filter((l) => l.data.mark).length;
      n.state = !n.children || n.data.kind === "epic" || n.data.kind === "deliverable" ? n.data.state ?? "not_started" : rollState(leaves);
    });
    // Messages: a leaf's own count; a container sums what is known of its children, and is unknown when none is known.
    root.eachAfter((n) => {
      const known = (n.children ?? []).map((c) => c.msgs).filter((m) => m !== null);
      n.msgs = !n.children ? (n.data.kind === "column" ? 0 : n.data.msgs ?? null) : known.length || n.children.every((c) => c.msgs !== null) ? known.reduce((s, m) => s + m, 0) : null;
      n.unknown = !n.children ? n.msgs === null : n.children.some((c) => c.unknown);
    });
    this.root = root;
    this.nodes = root.descendants();
    this.byId = new Map(this.nodes.map((n) => [n.id, n]));
    this.maxDepth = Math.max(...this.nodes.map((n) => n.depth));
    return root;
  }

  focusNode() {
    return (this.sel.focus && this.byId?.get(this.sel.focus)) || this.root;
  }

  // Binds the zoom and the size observer to the tile box, and lets go of a box that was replaced.
  bind() {
    const d3 = this.d3;
    if (!d3 || !this.box || this.zoomed === this.box) {
      return;
    }
    this.unbind();
    this.zoomed = this.box;
    // Breadth only, and only inside a column: the overview's columns never move.
    this.zoom = d3.zoom().scaleExtent([1, 24]).extent(() => [[0, 0], [this.w || 1, this.h || 1]])
      .filter((e) => !e.button && e.type !== "dblclick" && this.focusNode() !== this.root)
      .constrain((t) => d3.zoomIdentity.translate(Math.min(0, Math.max(this.w - this.w * t.k, t.x)), 0).scale(t.k))
      .on("zoom", (e) => {
        this.tx = { x: e.transform.x, k: e.transform.k };
        this.schedule();
      });
    d3.select(this.box).call(this.zoom).on("dblclick.zoom", null);
    this.observer = globalThis.ResizeObserver ? new ResizeObserver(([entry]) => {
      this.w = entry.contentRect.width;
      this.h = entry.contentRect.height;
      this.schedule();
    }) : null;
    this.observer?.observe(this.box);
    this.w = this.box.clientWidth;
    this.h = this.box.clientHeight;
  }

  unbind() {
    this.observer?.disconnect();
    this.observer = null;
    this.zoomed && this.d3.select(this.zoomed).on(".zoom", null).interrupt();
    this.zoomed = null;
  }

  schedule() {
    if (!globalThis.requestAnimationFrame) {
      this.layout();
    } else if (!this.frame) {
      this.frame = requestAnimationFrame(() => {
        this.frame = 0;
        this.layout();
      });
    }
  }

  // Writes every tile's box from the partition, the focus and the zoom. Writes only: the stage size comes from the observer.
  // Rows run down the stage: the focus, the root and the column heads are header strips; the levels below share the rest, and a
  // leaf reaches the bottom.
  layout() {
    const focus = this.focusNode();
    if (!focus || !this.w) {
      return;
    }
    const head = (d) => d === focus.depth || d <= 1;
    const levels = this.maxDepth - focus.depth + 1;
    let heads = 0;
    for (let d = focus.depth; d <= this.maxDepth; d++) {
      heads += head(d) ? 1 : 0;
    }
    const rowH = levels > heads ? Math.max(HEAD, (this.h - heads * HEAD) / (levels - heads)) : HEAD;
    const tops = [];
    for (let d = focus.depth, y = 0; d <= this.maxDepth; d++) {
      tops[d] = y;
      y += head(d) ? HEAD : rowH;
    }
    const kx = (this.w * this.tx.k) / (focus.x1 - focus.x0 || 1);
    for (const n of this.nodes) {
      const el = this.els.get(n.id);
      if (!el) {
        continue;
      }
      const c = n.depth - focus.depth;
      const left = (n.x0 - focus.x0) * kx + this.tx.x;
      const w = (n.x1 - n.x0) * kx;
      const top = c >= 0 ? tops[n.depth] : 0;
      const h = c < 0 ? 0 : n.children || n === focus || n.data.kind === "column" ? (head(n.depth) ? HEAD : rowH) : this.h - top;
      const shown = c >= 0 && w > 1 && left < this.w && left + w > 0;
      el.style.cssText = `top:${top}px;height:${Math.max(0, h)}px;left:${left}px;width:${Math.max(0, w)}px;visibility:${shown ? "visible" : "hidden"};` +
        // A tile that starts left of the stage keeps its label in view.
        (left < 0 ? `padding-left:${Math.max(12, Math.min(12 - left, w - 64))}px` : "");
      el.inert = !shown;
      // A header strip lays its name and numbers in one line; a tile too narrow for a name shows a count, its name in the tooltip.
      el.classList.toggle("hd", h <= HEAD);
      el.classList.toggle("sm", w < 80);
      el.classList.toggle("xs", w < 24);
    }
  }

  setUi = (patch) => this.props.store.setUi(patch);

  // Moves the focus with a FLIP of the tiles: where they are now is recorded, then the next layout animates from there.
  refocusTo(n, { keyFocus = null } = {}) {
    if (this.box && this.nodes) {
      forget(this.box);
      flip(this.box, this.nodes.map((x) => x.id), (f) => f());
      this.flipping = true;
    }
    this.tx = { x: 0, k: 1 };
    this.zoom && this.d3.select(this.box).call(this.zoom.transform, this.d3.zoomIdentity);
    this.refocus = keyFocus;
    this.setUi({ plansFocus: n === this.root ? null : n.id, plansSel: null });
  }

  // A column head opens the column. Any other tile is selected (its tiles light up in every column); a selected container opens on
  // a second press. The focus goes back up.
  click = (n) => {
    const focus = this.focusNode();
    const ent = n.data.ent;
    if (n === focus) {
      n.parent && this.refocusTo(n.parent);
    } else if (n.data.kind === "column") {
      n.children && this.refocusTo(n);
    } else if (n.children && this.sel.sel === ent) {
      this.refocusTo(n);
    } else {
      this.setUi({ plansSel: this.sel.sel === ent ? null : ent });
    }
  };

  // Esc in the stage: deselects, else clears the stream filter, else goes up a level. Outside the stage the page's own Esc is untouched.
  onKeyDown = (e) => {
    if (e.key !== "Escape") {
      return;
    }
    const focus = this.focusNode();
    if (this.sel.sel) {
      this.refocus = this.nodes.find((n) => n.data.ent === this.sel.sel && this.els.get(n.id)?.inert === false)?.id ?? null;
      this.setUi({ plansSel: null });
    } else if (this.sel.stream) {
      this.setUi({ plansStream: null });
    } else if (focus?.parent) {
      this.refocusTo(focus.parent, { keyFocus: focus.id });
    } else {
      return;
    }
    e.preventDefault();
    e.stopPropagation();
  };

  zoomBy(f) {
    this.zoom && this.d3.select(this.box).transition().duration(reduced() ? 0 : 250).call(this.zoom.scaleBy, f, [this.w / 2, 0]);
  }

  fit = () => {
    this.root && this.refocusTo(this.root);
  };

  // The selected item's history: one read of its team's change list, filtered to the item by the server and to its stream here.
  // A list that is absent (404) reads as no history; any other failure says so. A change of the item's state reads it again.
  async loadHistory() {
    const sel = partsOf(this.sel.sel);
    const n = sel?.kind === "i" ? this.nodes?.find((x) => x.data.ent === this.sel.sel) : null;
    const key = n ? `${this.sel.sel}|${n.data.state}` : null;
    if (key === this.histFor) {
      return;
    }
    this.histFor = key;
    this.setState({ history: null });
    if (!n) {
      return;
    }
    const history = await itemHistory(this.props.store, sel);
    if (this.histFor === key && !this.gone) {
      this.setState({ history });
    }
  }

  // The card for the selected thing (all its tiles, over every column), or for the focus when nothing is selected.
  card(ns, msgRows) {
    const d = ns[0].data;
    const leaves = ns.flatMap((n) => (n.children ? n.leaves() : [n]));
    const sum = (f) => ns.reduce((s, n) => s + f(n), 0);
    const item = d.kind === "task";
    const holder = d.holder && (item || d.kind === "epic" || d.kind === "deliverable");
    // Owners by member (team and name), each a link to the member.
    const owners = [...leaves.reduce((m, l) => {
      const key = l.data.holder ? `member:${l.data.team}/${l.data.holder}` : null;
      return key ? m.set(key, [l.data.holderLabel ?? l.data.holder, (m.get(key)?.[1] ?? 0) + 1, memberHref(key)]) : m;
    }, new Map()).values()].sort((a, b) => b[1] - a[1]);
    const state = ns.length > 1 && !(d.kind === "epic" || d.kind === "deliverable") ? rollState(leaves) : ns[0].state;
    const known = ns.filter((n) => n.msgs !== null);
    return {
      name: d.name,
      kind: d.kind === "root" ? "all teams" : d.kind,
      id: d.item ?? "",
      status: d.kind === "root" || d.kind === "team" || d.kind === "column" ? null : chipOf(state),
      owner: holder ? { name: d.holderLabel ?? d.holder, role: d.who?.role ?? "", state: d.who?.state ?? "unknown", href: memberHref(`member:${d.team}/${d.holder}`) } : null,
      stream: d.stream ? streamName(d.stream) : null,
      size: d.weight,
      done: sum((n) => n.done),
      total: sum((n) => n.total),
      owners,
      msgs: known.length ? known.reduce((s, n) => s + n.msgs, 0) : "—",
      rows: item ? msgRows ?? [] : null,
      partial: known.length > 0 && ns.some((n) => n.unknown),
    };
  }

  // What an item's card adds: its change since the last look, and its history.
  itemExtra(n) {
    const m = n.data.mark;
    const h = this.state.history;
    return html`${m ? html`<p class="pl-moved"><span class="pl-mark" aria-hidden="true"></span>${markText(m)}<span class="sr">${SEEN_SUFFIX}</span></p>` : null}
      ${historyView(h)}`;
  }

  // A tile is a box holding a full-size press target and, apart from it, the holder's link (a link cannot sit inside a button).
  // A hidden tile is inert, so neither is in the tab order.
  tile(n, focus, s) {
    const d = n.data;
    const box = (el) => (el ? this.els.set(n.id, el) : this.els.delete(n.id));
    const hit = (el) => (el ? this.hits.set(n.id, el) : this.hits.delete(n.id));
    const dot = n.marked ? html`<span class="pl-mark" aria-hidden="true"></span>` : null;
    if (d.kind === "column") {
      // The head counts exactly the cards drawn under it; Done says how many of all it shows.
      const all = d.count + (d.more ?? 0);
      const label = `${d.name}, ${d.more ? `${d.count} of ${all} cards shown` : `${d.count} ${d.count === 1 ? "card" : "cards"}`}${n.marked ? SEEN_SUFFIX : ""}`;
      return html`<div key=${n.id} ref=${box} class=${`ice-tile col mk-${COL_MARK[d.col] ?? "idle"}`}>
        <button type="button" class="ice-hit" ref=${hit} aria-label=${label} onClick=${() => this.click(n)}><span class="t">${dot}${d.name}</span></button>
        <span class="num" aria-hidden="true">${d.more ? `${d.count} of ${all}` : d.count}</span>
      </div>`;
    }
    const isBox = Boolean(n.children);
    const st = n.state;
    const on = Boolean(s.sel) && d.ent === s.sel;
    const dim = s.owner && !(isBox ? n.leaves() : [n]).some((l) => l.data.holder === s.owner);
    const holder = d.holder && (d.kind === "task" || d.kind === "epic" || d.kind === "deliverable");
    const statusText = STATE_LABEL[st] ?? st;
    // The pill only when the state says more than the column already does; a team or stream is in the column's state by being there.
    const pill = (d.kind === "task" || d.kind === "epic" || d.kind === "deliverable") && !COL_STATES[d.col]?.includes(st);
    const msgsText = n.msgs === null ? "messages unknown" : `${n.msgs} ${n.msgs === 1 ? "message" : "messages"}`;
    const changed = n.marked ? (!isBox && d.mark?.kind === "moved" ? `, moved from ${STATE_LABEL[d.mark.from] ?? d.mark.from}` : SEEN_SUFFIX) : "";
    const kind = d.kind === "root" ? "all teams" : d.kind;
    const status = d.kind === "root" || d.kind === "team" || d.kind === "column" || (d.kind === "stream" && isBox) ? "" : `, ${statusText}`;
    return html`<div key=${n.id} ref=${box} class=${`ice-tile mk-${MARK[st] ?? "idle"}${st === "dropped" ? " dropped" : ""}${on ? " on" : ""}${dim ? " dim" : ""}`}>
      <button type="button" class="ice-hit" ref=${hit} title=${d.item ? `${d.name} · ${d.item}` : d.name}
        aria-pressed=${n === focus ? undefined : on}
        aria-label=${`${kind} ${d.name}${status}${holder ? `, owner ${d.holderTitle ?? d.holder}` : ""}, ${msgsText}${changed}, level ${n.depth + 1}${on && isBox ? ", press again to open" : ""}`}
        onClick=${() => this.click(n)}><span class="t">${dot}${d.name}</span></button>
      <span class="m">
        ${d.kind === "epic" || d.kind === "deliverable" ? html`<span class="k">${d.kind}</span>` : null}
        ${pill ? html`<span class=${`chip pl-chip ${chipOf(st).cls}`}>${statusText}</span>` : null}
        ${holder ? html`<a class="who" href=${memberHref(`member:${d.team}/${d.holder}`)} title=${`Open ${d.holderTitle ?? d.holder}`} aria-label=${`Open member ${d.holderTitle ?? d.holder}`}>${d.who?.state && d.who.state !== "unknown" ? html`<span class=${`ice-glyph st-${d.who.state}`} aria-hidden="true">${glyphOf(d.who.state)} </span>` : null}${d.holderLabel ?? d.holder}</a>` : null}
        ${isBox ? html`<span class="num">${n.done}/${n.total} done</span>` : null}
        ${n.msgs > 0 ? html`<span class="num" title=${`${n.msgs} ${n.msgs === 1 ? "message" : "messages"}`}><span aria-hidden="true">✉</span> ${n.msgs}</span>` : null}
      </span>
      ${isBox ? html`<span class="c num" aria-hidden="true">${n.leaves().length}</span>` : null}
      ${isBox ? html`<span class="bar" aria-hidden="true"><i style=${{ width: `${n.total ? (100 * n.done) / n.total : 0}%` }}></i></span>` : null}
    </div>`;
  }

  draw({ store, note = null, onStale = null }, s) {
    if (this.failed) {
      return html`<p class="dim skeleton">Could not load the board.</p>`;
    }
    if (!this.d3) {
      return html`<p class="dim skeleton">Loading…</p>`;
    }
    const root = this.tree(s.rows);
    const focus = this.focusNode();
    const picked = s.sel ? this.nodes.filter((n) => n.data.ent === s.sel) : [];
    const owners = [...new Set(root.leaves().map((l) => l.data.holder).filter(Boolean))].sort();
    const only = partsOf(s.stream);
    const stale = this.board.stale ?? [];
    return html`<div class="pl-ice">
      <div class="ice-bar">
        <nav class="gm-crumbs" aria-label="Zoom path">${focus.ancestors().reverse().map((n, i, all) => html`${i ? html`<span class="sep" aria-hidden="true">/</span>` : null}<button key=${n.id} type="button" aria-current=${i === all.length - 1 ? "location" : undefined} title=${n.data.name} onClick=${() => n !== focus && this.refocusTo(n)}>${n.data.name}</button>`)}</nav>
        ${only ? html`<button type="button" class="tab on ice-filter" aria-label=${`Showing stream ${streamName(only.stream)} only. Clear the stream filter`} onClick=${() => this.setUi({ plansStream: null })}>Stream: ${streamName(only.stream)} <span aria-hidden="true">×</span></button>` : null}
        <span class="ice-tools">
          <button type="button" class=${s.epics ? "tab on" : "tab"} aria-pressed=${s.epics} onClick=${() => choose(store, "plansEpics", s.epics ? "off" : "on")}>Show epics</button>
          <label class="ice-owner">Owner <select value=${s.owner} onChange=${(e) => this.setUi({ plansOwner: e.target.value || null })}><option value="">everyone</option>${owners.map((o) => html`<option key=${o} value=${o}>${o}</option>`)}</select></label>
          <button type="button" class="tab" aria-label="Zoom in" disabled=${focus === root} onClick=${() => this.zoomBy(1.5)}>+</button>
          <button type="button" class="tab" aria-label="Zoom out" disabled=${focus === root} onClick=${() => this.zoomBy(1 / 1.5)}>−</button>
          <button type="button" class="tab" onClick=${this.fit}>Fit</button>
        </span>
      </div>
      ${stale.length && onStale ? html`<p class="ice-stale">${stale.length} stale ${stale.length === 1 ? "team" : "teams"} not shown:${stale.map((t) => html` <button key=${t.id} type="button" class="tab" onClick=${() => onStale(t.id)}>Show ${t.name}</button>`)}</p>` : null}
      ${note ? html`<p class="dim ice-note" role="status">${note}</p>` : null}
      ${s.itemMissing ? html`<p class="ice-note" role="status">Item ${s.itemMissing} is not on this board.</p>` : null}
      <div class="ice-body">
        <div class="ice-stage" onKeyDown=${this.onKeyDown}>
          <div class="ice-tiles" role="group" aria-label="Work by state. Width is the item's size." ref=${(el) => (this.box = el)}>${this.nodes.map((n) => this.tile(n, focus, s))}</div>
        </div>
        ${picked.length ? html`<${TileCard} cls="pl-detail ice-panel" t=${this.card(picked, JSON.parse(s.msgRows))} store=${store} onClose=${() => this.setUi({ plansSel: null })}>
          ${picked[0].data.kind === "task" ? this.itemExtra(picked[0]) : null}
        </${TileCard}>` : null}
      </div>
    </div>`;
  }
}
