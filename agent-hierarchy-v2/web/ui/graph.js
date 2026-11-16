import { html, h, memo, Component } from "./h.js";
import { Live } from "./live-component.js";
import { Age } from "./ticker.js";
import { ageOf, ageText } from "./store.js";
import { hoverProps } from "./hover.js";
import { DUTY, DUTY_TITLE, memberOf, stateWord } from "./select.js";
import { MAX_SPARKS, MODES, glyphOf, modeOf, shortName } from "./graph-world.js";

export { shortName };

// The Graph view's two layouts, Map and Flow, and what they share: the remembered choice, the header toggle and the link kinds.
// Flow's stage lives here too; Map is graph-pack.js.

// ---- the per-viewer choice: localStorage, which may be absent or throw

const PREFS_KEY = "ah-graph";

export function readPrefs() {
  try {
    const p = JSON.parse(globalThis.localStorage?.getItem(PREFS_KEY) ?? "{}");
    return { mode: modeOf(p?.mode), orient: p?.orient === "vertical" ? "vertical" : "horizontal", legend: p?.legend !== false };
  } catch {
    return { mode: "map", orient: "horizontal", legend: true };
  }
}

// Sets one or more of the graph choices in the page and remembers them.
export function setGraph(store, patch) {
  const next = { ...readPrefs(), ...patch };
  try {
    globalThis.localStorage?.setItem(PREFS_KEY, JSON.stringify(next));
  } catch {
    // the choice holds for this page only
  }
  store.setUi({ graphMode: next.mode, graphOrient: next.orient, graphLegend: next.legend });
}

export const graphUi = (ui) => ({ mode: modeOf(ui.graphMode), orient: ui.graphOrient === "vertical" ? "vertical" : "horizontal", legend: ui.graphLegend !== false });

// The segmented Map | Radial | Flow in the header, shown on the Graph tab only. Below 1120 px each shows its initial (r6-graph.css), so
// the header fits a 1024 px window; the button keeps the full name.
export class GraphToggle extends Live {
  select(snap) {
    return snap.ui?.view === "graph" ? { graph: true, mode: graphUi(snap.ui).mode } : { graph: false, mode: null };
  }

  draw({ store }, s) {
    if (!s.graph) {
      return null;
    }
    const pick = (mode) => html`<button key=${mode} class=${s.mode === mode ? "seg on" : "seg"} aria-pressed=${s.mode === mode} title=${`${MODE_LABEL[mode]} (Shift+M)`} aria-label=${MODE_LABEL[mode]} onClick=${() => setGraph(store, { mode })}>${MODE_LABEL[mode][0]}<span class="rest">${MODE_LABEL[mode].slice(1)}</span></button>`;
    return html`<div class="graph-toggle" role="group" aria-label="Graph layout">${MODES.map(pick)}</div>`;
  }
}

const MODE_LABEL = { map: "Map", radial: "Radial", flow: "Flow" };

// Shift+M steps through the modes in toggle order: Map, Radial, Flow, and round.
export const toggleMode = (store) => setGraph(store, { mode: MODES[(MODES.indexOf(graphUi(store.state.ui).mode) + 1) % MODES.length] });

// ---- links

// The width of a link by the messages exchanged on its pair.
export const widthOf = (n) => (n >= 12 ? 4 : n >= 7 ? 3 : n >= 3 ? 2 : n >= 1 ? 1.5 : 1);

// How each pair of members with a brief in flight between them is drawn: "review" when the daemon says one of its briefs is sent for review.
export function sparkKinds(sparks) {
  const out = new Map();
  for (const s of sparks) {
    const key = s.from < s.to ? `${s.from}|${s.to}` : `${s.to}|${s.from}`;
    out.set(key, s.review || out.get(key) === "review" ? "review" : "brief");
  }
  return out;
}

export const pairKey = (a, b) => (a < b ? `${a}|${b}` : `${b}|${a}`);

// "N messages this run · M in the last hour", the hour counted now from the times the server listed.
export const messagesText = (c, now = Date.now()) => `${c.n} messages this run · ${c.recent.filter((t) => t >= now - 3_600_000).length} in the last hour`;

// ---- Flow: the columns by role, and where each card goes

export const COLUMNS = ["Lead", "Advise · design", "Implement", "Review"];

export const FLOW = { card: { w: 220, h: 64 }, row: { w: 200, h: 36 }, gap: 16, band: 64, lane: 100, top: 90, left: 40 };

// Cards positioned by column: {pos: id -> {x, y, w, h}, labels: [{text, x, y}], box}. Horizontal has the columns left to right; vertical has them
// top to bottom. A column's members keep name order; the lead column is centred on the tallest one. With lanes (a node's `lane` is its
// leader's id) the cards form bands across the columns: first the members in no lane, then one band per leader in name order, holding the
// leader and its lane's members.
export function flowLayout(nodes, active, orient) {
  const horizontal = orient !== "vertical";
  const byName = [...nodes].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const leaders = byName.filter((n) => n.lane === n.id);
  const bandOf = new Map([["", 0], ...leaders.map((l, i) => [l.id, i + 1])]);
  const bands = [[], ...leaders.map(() => [])];
  byName.forEach((n) => bands[bandOf.get(n.lane || "") ?? 0].push(n));
  const size = (n) => (active.has(n.id) ? FLOW.card : FLOW.row);
  const along = (n) => (horizontal ? size(n).h : size(n).w);
  const step = horizontal ? FLOW.card.w + FLOW.lane : FLOW.card.h + FLOW.lane;
  const pos = new Map();
  const labels = COLUMNS.map((text, ci) => (horizontal ? { text, x: FLOW.left + ci * step, y: FLOW.top - 32 } : { text, x: FLOW.left, y: FLOW.top + ci * step - 24 }));
  const backdrops = [];
  const reach = (COLUMNS.length - 1) * step + (horizontal ? FLOW.card.w : FLOW.card.h);
  let offset = 0;
  for (const [bi, band] of bands.entries()) {
    if (band.length === 0) {
      continue;
    }
    const cols = COLUMNS.map(() => []);
    band.forEach((n) => (cols[n.column] ?? cols[2]).push(n));
    const spans = cols.map((c) => c.reduce((sum, n) => sum + along(n) + FLOW.gap, -FLOW.gap));
    const tallest = Math.max(0, ...spans);
    cols.forEach((c, ci) => {
      let at = (ci === 0 ? (tallest - Math.max(0, spans[ci])) / 2 : 0) + FLOW.top + offset;
      const base = FLOW.left + ci * step;
      c.forEach((n) => {
        const { w, h } = size(n);
        pos.set(n.id, horizontal ? { x: base, y: at, w, h } : { x: FLOW.left + (at - FLOW.top), y: FLOW.top + ci * step, w, h });
        at += along(n) + FLOW.gap;
      });
    });
    if (bi > 0) {
      const leader = leaders[bi - 1];
      const start = FLOW.top + offset;
      const pad = FLOW.gap;
      backdrops.push(horizontal
        ? { id: leader.id, name: shortName(leader.id, leader.name), x: FLOW.left - pad / 2, y: start - pad - FLOW.gap, w: reach + pad, h: tallest + 2 * pad + FLOW.gap }
        : { id: leader.id, name: shortName(leader.id, leader.name), x: FLOW.left + offset - pad / 2, y: FLOW.top - pad - FLOW.gap, w: tallest + pad, h: reach + 2 * pad + FLOW.gap });
    }
    offset += tallest + FLOW.band;
  }
  let x0 = Infinity;
  let y0 = Infinity;
  let x1 = -Infinity;
  let y1 = -Infinity;
  for (const p of [...pos.values(), ...backdrops, ...labels.map((l) => ({ x: l.x, y: l.y, w: 120, h: 14 }))]) {
    x0 = Math.min(x0, p.x);
    y0 = Math.min(y0, p.y);
    x1 = Math.max(x1, p.x + p.w);
    y1 = Math.max(y1, p.y + p.h);
  }
  return { pos, labels, bands: backdrops, box: x0 === Infinity ? null : { x: x0, y: y0, w: x1 - x0, h: y1 - y0 } };
}

// How a link goes round the cards between its ends: its distance from the end cards' edges, its clearance from a card, its corner radius.
const AROUND = { gap: 24, clear: 8, r: 8 };

// A link that would pass behind a card reads as starting from that card, so it runs round instead: `cards` are the rects it must not pass
// behind. Everything is in (u along the columns, w across them). Returns the rail's w, or null when the plain curve is clear.
function railOf(u1, w1, u2, w2, cards) {
  const between = cards.filter((c) => c.u >= u1 && c.u + c.len <= u2);
  const lo = Math.min(w1, w2);
  const hi = Math.max(w1, w2);
  const blocking = between.filter((c) => c.w < hi && c.w + c.size > lo);
  if (blocking.length === 0) {
    return null;
  }
  const clearOf = (start, dir) => {
    let at = start;
    for (let hit = between.find((c) => c.w - AROUND.clear < at && c.w + c.size + AROUND.clear > at); hit; hit = between.find((c) => c.w - AROUND.clear < at && c.w + c.size + AROUND.clear > at)) {
      at = dir < 0 ? hit.w - AROUND.clear - 1 : hit.w + hit.size + AROUND.clear + 1;
    }
    return at;
  };
  const above = clearOf(Math.min(...blocking.map((c) => c.w)) - AROUND.clear - 1, -1);
  const below = clearOf(Math.max(...blocking.map((c) => c.w + c.size)) + AROUND.clear + 1, 1);
  const trip = (rail) => Math.abs(w1 - rail) + Math.abs(rail - w2);
  return trip(above) <= trip(below) ? above : below;
}

// A link from one card to the next: right edge to left edge, or bottom to top when vertical. A cubic, unless a card in `cards` stands in
// its way: then it leaves the first card, runs along a rail clear of those cards, and comes into the second.
export function flowPath(a, b, orient, cards = []) {
  const vertical = orient === "vertical";
  const axes = (r) => (vertical ? { u: r.y, len: r.h, w: r.x, size: r.w } : { u: r.x, len: r.w, w: r.y, size: r.h });
  const [A, B] = [axes(a), axes(b)];
  const [u1, w1, u2, w2] = [A.u + A.len, A.w + A.size / 2, B.u, B.w + B.size / 2];
  const rail = railOf(u1, w1, u2, w2, cards.filter((c) => c !== a && c !== b).map(axes));
  if (rail !== null) {
    const at = (u, w) => (vertical ? `${w} ${u}` : `${u} ${w}`);
    const [g1, g2] = [u1 + AROUND.gap, u2 - AROUND.gap];
    const turn = (from, to) => Math.min(AROUND.r, Math.abs(to - from) / 2) * Math.sign(to - from);
    const [r1, r2] = [turn(w1, rail), turn(rail, w2)];
    const [e1, e2] = [Math.abs(r1), Math.abs(r2)];
    return `M ${at(u1, w1)} L ${at(g1 - e1, w1)} Q ${at(g1, w1)} ${at(g1, w1 + r1)} L ${at(g1, rail - r1)} Q ${at(g1, rail)} ${at(g1 + e1, rail)} L ${at(g2 - e2, rail)} Q ${at(g2, rail)} ${at(g2, rail + r2)} L ${at(g2, w2 - r2)} Q ${at(g2, w2)} ${at(g2 + e2, w2)} L ${at(u2, w2)}`;
  }
  if (vertical) {
    const x1 = a.x + a.w / 2;
    const y1 = a.y + a.h;
    const x2 = b.x + b.w / 2;
    const y2 = b.y;
    const m = (y1 + y2) / 2;
    return `M ${x1} ${y1} C ${x1} ${m} ${x2} ${m} ${x2} ${y2}`;
  }
  const x1 = a.x + a.w;
  const y1 = a.y + a.h / 2;
  const x2 = b.x;
  const y2 = b.y + b.h / 2;
  const m = (x1 + x2) / 2;
  return `M ${x1} ${y1} C ${m} ${y1} ${m} ${y2} ${x2} ${y2}`;
}

// Whether a member draws as a card: it is working or blocked, or it is an orchestrator the Overview calls Running.
export const isActive = (m) => m.running === true || m.state === "working" || m.state === "blocked";

const modelTag = (model) => (typeof model === "string" ? (/opus|sonnet|haiku|fable/i.exec(model)?.[0]?.toLowerCase() ?? model) : null);
const ageFrom = (ms) => html`<${Age} text=${(now) => ageText(ageOf({ as_of: ms }, {}, now))} />`;

// One member in Flow: a card with what is known of it when it is working or running, a single row when it is not. A field with no source
// is not drawn. The time is the member's last activity; a Running orchestrator records none through its turns, so it has no time.
export class FlowCard extends Live {
  select(snap, { teamId, id }) {
    const team = snap.slices.get(teamId);
    const m = team ? memberOf(snap.slices, team, id) : { id, name: id.split("/").pop(), role: "", state: "gone", running: false, tool: null, model: null, slug: null, lastAt: 0, orchestrator: false };
    const items = team?.body.pipeline?.items ?? [];
    const item = m.slug ? items.find((i) => i.slug === m.slug) : null;
    return {
      name: shortName(id, m.name),
      full: m.name,
      role: m.role,
      state: m.state,
      running: m.running === true,
      tool: m.tool,
      model: modelTag(m.model),
      lastAt: m.lastAt,
      orchestrator: m.orchestrator,
      duty: m.duty ?? null,
      decisions: team?.body.pipeline?.decisions_count ?? null,
      round: item && Number.isFinite(item.rounds) ? `r${item.rounds}/${item.round_cap ?? 3}` : null,
    };
  }

  draw({ store, id, rect, selected, onSelect, out }, s) {
    const reviewing = s.role === "reviewer" && s.state === "working";
    const active = isActive(s);
    const cls = `fcard${active ? " active" : " idle"}${reviewing ? " reviewing" : ""}${selected ? " selected" : ""}`;
    const place = { left: `${rect.x}px`, top: `${rect.y}px`, width: `${rect.w}px` };
    const open = (e) => (e.key === "Enter" || e.key === " ") && (e.preventDefault(), onSelect(id));
    const cell = (value, unit) => html`<span><b>${value}</b>${unit ? ` ${unit}` : ""}</span>`;
    const stats = [
      s.orchestrator ? cell(out, "out") : null,
      s.orchestrator && s.decisions !== null ? cell(s.decisions, "dec") : null,
      s.role === "implementor" && s.round ? cell(s.round) : null,
      s.lastAt > 0 ? html`<span title="Last activity">active <b>${ageFrom(s.lastAt)}</b></span>` : null,
    ].filter(Boolean);
    const tag = reviewing ? "reviewing" : s.running ? "running" : s.tool ?? s.model;
    const duty = DUTY[s.duty] ?? null;
    const badge = duty ? html`<span class="duty-badge" title=${DUTY_TITLE[s.duty]}>${duty}</span>` : null;
    return html`<div class=${cls} data-role=${s.role || undefined} data-state=${s.state} data-id=${id} style=${place} tabindex="0" role="button" ...${hoverProps(store, id, "card")} aria-label=${`${s.name}, ${duty ? `${DUTY_TITLE[s.duty]}, ` : ""}${reviewing ? "reviewing" : stateWord(s.state, s.running)}`} onClick=${() => onSelect(id)} onKeyDown=${open}>
      ${active
        ? html`<div class="fhead"><span class="glyph" aria-hidden="true">${reviewing ? "◆" : "◐"}</span><b title=${s.full}>${s.name}</b>${badge}${tag ? html`<span class="tag">${tag}</span>` : null}</div>${stats.length > 0 ? html`<div class="fstats">${stats}</div>` : null}`
        : html`<div class="fhead"><span class="glyph" aria-hidden="true">${glyphOf(s.state)}</span><span class="fname" title=${s.full}>${s.name}</span>${badge}${s.state !== "idle" ? html`<span class="tag">${stateWord(s.state).toLowerCase()}</span>` : !badge && s.lastAt > 0 ? html`<span class="tag" title="Last activity">${ageFrom(s.lastAt)}</span>` : null}</div>`}
    </div>`;
  }
}

// ---- Flow's stage: pan and zoom over the cards, the links between them

export const VIEW = { w: 1000, h: 700 };

const round = (n) => Math.round(n * 10) / 10;

// Flow's links: reporting lines from card to card, a brief in flight accent with a dot, a brief sent for review dashed.
const FlowLinks = memo(function FlowLinks({ edges, sparks, rects, orient }) {
  const kinds = sparkKinds(sparks);
  const cards = [...rects.values()];
  const draw = (from, to) => {
    const a = rects.get(from);
    const b = rects.get(to);
    return a && b ? ((orient === "vertical" ? a.y <= b.y : a.x <= b.x) ? flowPath(a, b, orient, cards) : flowPath(b, a, orient, cards)) : null;
  };
  const shown = sparks.slice(0, MAX_SPARKS);
  const pairs = [...new Map([...edges.map((e) => [pairKey(e.from, e.to), [e.from, e.to]]), ...shown.map((s) => [pairKey(s.from, s.to), [s.from, s.to]])]).values()];
  return html`<g class="links">
    <g class="edges">${pairs.map(([from, to]) => {
      const d = draw(from, to);
      const kind = kinds.get(pairKey(from, to));
      return d ? html`<path key=${pairKey(from, to)} class=${`edge${kind === "review" ? " edge-review" : kind === "brief" ? " edge-live" : ""}`} d=${d} />` : null;
    })}</g>
    <g class="dots">${shown.map((s) => {
      const d = draw(s.from, s.to);
      return d ? html`<circle key=${s.id} class=${`dot${kinds.get(pairKey(s.from, s.to)) === "review" ? " dot-review" : ""}`} r="3" style=${{ offsetPath: `path("${d}")` }} />` : null;
    })}</g>
  </g>`;
});

export const ZOOM = { min: 0.25, max: 4, fitMax: 1.4, step: 1.25, pan: 60 };
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));

// The view {k, x, y} (scale, then offset in stage pixels) that centres the box in a w×h stage at the largest scale that fits,
// up to ZOOM.fitMax so a small team is not blown up.
export function fitView(box, w, h, pad = 24) {
  if (!box) {
    return { k: 1, x: 0, y: 0 };
  }
  const k = clamp(Math.min((w - 2 * pad) / box.w, (h - 2 * pad) / box.h), ZOOM.min, ZOOM.fitMax);
  return { k: round(k * 1000) / 1000, x: round(w / 2 - k * (box.x + box.w / 2)), y: round(h / 2 - k * (box.y + box.h / 2)) };
}

// The view scaled by `factor` about the stage point (px, py), which stays put.
export function zoomAt(v, factor, px, py) {
  const k = clamp(v.k * factor, ZOOM.min, ZOOM.max);
  const f = k / v.k;
  return { k, x: px - (px - v.x) * f, y: py - (py - v.y) * f };
}

const ZOOM_KEYS = { "+": ZOOM.step, "=": ZOOM.step, "-": 1 / ZOOM.step, _: 1 / ZOOM.step };
const PAN_KEYS = { ArrowLeft: [1, 0], ArrowRight: [-1, 0], ArrowUp: [0, 1], ArrowDown: [0, -1] };

// A wheel event from a pinch (ctrl set by the browser) or a mouse wheel (line steps, or whole vertical notches) zooms;
// a trackpad's two-finger scroll pans.
// ponytail: a mouse that reports pixel deltas in fine steps pans instead of zooming; it still zooms with ctrl or the buttons.
const wheelZooms = (e) => e.ctrlKey || e.metaKey || e.deltaMode === 1 || (e.deltaX === 0 && Number.isInteger(e.deltaY) && Math.abs(e.deltaY) >= 50);


// Flow: the old Graph stage, unchanged for Flow; Map is graph-pack.js.
export class FlowStage extends Component {
  // view: the user's own view once they zoom or pan (until Fit), else null and the graph fits the stage.
  state = { size: null, view: null, instant: false };

  componentDidMount() {
    if (typeof IntersectionObserver === "function") {
      this.io = new IntersectionObserver((entries) => this.base?.classList.toggle("offscreen", !entries[entries.length - 1].isIntersecting));
      this.io.observe(this.base);
    }
    if (typeof ResizeObserver === "function") {
      this.ro = new ResizeObserver(([e]) => {
        const size = { w: Math.round(e.contentRect.width), h: Math.round(e.contentRect.height) };
        if (size.w > 0 && size.h > 0 && (size.w !== this.state.size?.w || size.h !== this.state.size?.h)) {
          const first = this.state.size === null;
          this.setState({ size, instant: first });
          first && this.unsnap();
        }
      });
      this.ro.observe(this.svg);
    }
  }

  componentWillUnmount() {
    this.io?.disconnect();
    this.ro?.disconnect();
    clearTimeout(this.settle);
  }

  // The layout is computed again only when the set of members, the edges, the orientation or which members are working change.
  layout(nodes, edges, orient, activeKey) {
    const c = this.cached;
    if (c?.nodes !== nodes || c.edges !== edges || c.orient !== orient || c.activeKey !== activeKey) {
      const f = flowLayout(nodes, new Set(activeKey === "" ? [] : activeKey.split("\n")), orient);
      this.cached = { nodes, edges, orient, activeKey, pos: f.pos, labels: f.labels, bands: f.bands, box: f.box };
    }
    return this.cached;
  }

  componentDidUpdate(prev) {
    const p = this.props;
    if (prev.fitNonce !== p.fitNonce || prev.orient !== p.orient) {
      this.fit();
    }
  }

  size() {
    return this.state.size ?? { w: VIEW.w, h: VIEW.h };
  }

  current() {
    const { w, h } = this.size();
    return this.state.view ?? fitView(this.cached?.box ?? null, w, h);
  }

  // Sets the user's view; `instant` skips the spring, for views that follow a finger or a wheel.
  look(view, instant) {
    this.setState({ view, instant });
    clearTimeout(this.settle);
    instant && this.unsnap();
  }

  // The spring comes back once the snapped view has been drawn.
  unsnap() {
    clearTimeout(this.settle);
    this.settle = setTimeout(() => this.setState({ instant: false }), 160);
  }

  zoom = (factor) => {
    const { w, h } = this.size();
    this.look(zoomAt(this.current(), factor, w / 2, h / 2), false);
  };

  fit = () => this.setState({ view: null, instant: false });

  // A client point in stage pixels.
  point(e) {
    const r = this.svg.getBoundingClientRect();
    const { w, h } = this.size();
    return { x: ((e.clientX - r.left) / (r.width || w)) * w, y: ((e.clientY - r.top) / (r.height || h)) * h };
  }

  onWheel = (e) => {
    e.preventDefault();
    const v = this.current();
    if (wheelZooms(e)) {
      const p = this.point(e);
      this.look(zoomAt(v, Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.002)), p.x, p.y), true);
    } else {
      this.look({ ...v, x: v.x - e.deltaX, y: v.y - e.deltaY }, true);
    }
  };

  // Pointers: one drags the graph, two pinch and pan it about their midpoint. A drag of more than a few pixels
  // swallows the click that ends it, so a node or edge under the finger does not open.
  pointers = new Map();

  onPointerDown = (e) => {
    if (e.button !== undefined && e.button !== 0) {
      return;
    }
    this.pointers.set(e.pointerId, this.point(e));
    this.gesture = { view: this.current(), start: new Map(this.pointers) };
    this.dragged = false;
  };

  onPointerMove = (e) => {
    if (!this.pointers.has(e.pointerId) || !this.gesture) {
      return;
    }
    this.pointers.set(e.pointerId, this.point(e));
    const ids = [...this.gesture.start.keys()].filter((id) => this.pointers.has(id));
    const v = this.gesture.view;
    if (ids.length >= 2) {
      const [a0, b0] = ids.slice(0, 2).map((id) => this.gesture.start.get(id));
      const [a1, b1] = ids.slice(0, 2).map((id) => this.pointers.get(id));
      const mid0 = { x: (a0.x + b0.x) / 2, y: (a0.y + b0.y) / 2 };
      const mid1 = { x: (a1.x + b1.x) / 2, y: (a1.y + b1.y) / 2 };
      const z = zoomAt(v, Math.hypot(a1.x - b1.x, a1.y - b1.y) / (Math.hypot(a0.x - b0.x, a0.y - b0.y) || 1), mid0.x, mid0.y);
      this.dragged = true;
      this.look({ ...z, x: z.x + mid1.x - mid0.x, y: z.y + mid1.y - mid0.y }, true);
    } else if (ids.length === 1) {
      const s0 = this.gesture.start.get(ids[0]);
      const s1 = this.pointers.get(ids[0]);
      if (this.dragged || Math.hypot(s1.x - s0.x, s1.y - s0.y) > 4) {
        if (!this.dragged) {
          this.svg.setPointerCapture?.(e.pointerId);
        }
        this.dragged = true;
        this.look({ ...v, x: v.x + s1.x - s0.x, y: v.y + s1.y - s0.y }, true);
      }
    }
  };

  onPointerUp = (e) => {
    this.pointers.delete(e.pointerId);
    this.gesture = this.pointers.size > 0 ? { view: this.current(), start: new Map(this.pointers) } : null;
  };

  onClickCapture = (e) => {
    if (this.dragged) {
      this.dragged = false;
      e.stopPropagation();
      e.preventDefault();
    }
  };

  onKeyDown = (e) => {
    if (e.altKey || e.ctrlKey || e.metaKey) {
      return;
    }
    const v = this.current();
    if (ZOOM_KEYS[e.key]) {
      e.preventDefault();
      this.zoom(ZOOM_KEYS[e.key]);
    } else if (e.key === "0") {
      e.preventDefault();
      this.fit();
    } else if (PAN_KEYS[e.key]) {
      e.preventDefault();
      const [dx, dy] = PAN_KEYS[e.key];
      this.look({ ...v, x: v.x + dx * ZOOM.pan, y: v.y + dy * ZOOM.pan }, false);
    }
  };

  render({ store, teamId, nodes, edges, sparks, selected, onSelect, orient = "horizontal", activeKey = "" }) {
    const { pos, labels, bands } = this.layout(nodes, edges, orient, activeKey);
    const { w, h: ht } = this.size();
    const v = this.current();
    const rest = sparks.length - Math.min(sparks.length, MAX_SPARKS);
    const world = { transform: `translate(${v.x}px, ${v.y}px) scale(${v.k})` };
    const instant = this.state.instant ? " instant" : "";
    return html`<div class="graph gflow">
      <svg class="stage" viewBox=${`0 0 ${w} ${ht}`} role="group" aria-label="Team graph, Flow: + and − zoom, 0 fits, arrows pan" tabindex="0" ref=${(el) => (this.svg = el)} onWheel=${this.onWheel} onPointerDown=${this.onPointerDown} onPointerMove=${this.onPointerMove} onPointerUp=${this.onPointerUp} onPointerCancel=${this.onPointerUp} onClickCapture=${this.onClickCapture} onKeyDown=${this.onKeyDown}>
        <defs><pattern id="flow-dots" width="20" height="20" patternUnits="userSpaceOnUse"><circle class="dot-grid" cx="10" cy="10" r="1" /></pattern></defs><rect width="100%" height="100%" fill="url(#flow-dots)" pointer-events="none" />
        <g class=${`world${instant}`} style=${world}>
          <${FlowLinks} edges=${edges} sparks=${sparks} rects=${pos} orient=${orient} />
        </g>
        ${rest > 0 ? html`<text class="more" x=${w - 20} y="30" text-anchor="end">+${rest} more briefs</text>` : null}
      </svg>
      <div class="flow-layer"><div class=${`world flow-world${instant}`} style=${world}>
        ${bands.map((b) => html`<div key=${`lane:${b.id}`} class="flane" style=${{ left: `${b.x}px`, top: `${b.y}px`, width: `${b.w}px`, height: `${b.h}px` }}><span class="flane-label">${b.name}</span></div>`)}
        ${labels.map((l) => html`<span key=${l.text} class="flabel" style=${{ left: `${l.x}px`, top: `${l.y}px` }}>${l.text}</span>`)}
        ${nodes.map((n) => (pos.has(n.id) ? h(FlowCard, { key: n.id, store, teamId: n.team || teamId, id: n.id, rect: pos.get(n.id), selected: selected === n.id, onSelect, out: sparks.filter((s) => s.from === n.id).length }) : null))}
      </div></div>
      <div class="flow-legend" role="group" aria-label="Flow legend"><span><i class="k-brief"></i>brief in flight</span><span><i class="k-review"></i>sent for review</span><span><i class="k-edge"></i>reporting line</span><span class="dim">Idle members collapse to one line; a time is the member's last activity</span></div>
      <div class="graph-controls" role="group" aria-label="Graph toolbar">
        <${GraphToggle} store=${store} />
        <button class="orient" aria-label="Switch between horizontal and vertical" title="Horizontal ⇄ Vertical" onClick=${() => setGraph(store, { orient: orient === "vertical" ? "horizontal" : "vertical" })}>${orient === "vertical" ? "Vertical ⇄ Horizontal" : "Horizontal ⇄ Vertical"}</button>
        <button aria-label="Zoom in" title="Zoom in (+)" onClick=${() => this.zoom(ZOOM.step)}>+</button>
        <button aria-label="Zoom out" title="Zoom out (−)" onClick=${() => this.zoom(1 / ZOOM.step)}>−</button>
        <button class=${this.state.view ? "fit" : "fit on"} aria-pressed=${this.state.view === null} title="Fit the graph (Shift+F, 0)" onClick=${this.fit}>Fit</button>
      </div>
    </div>`;
  }
}
