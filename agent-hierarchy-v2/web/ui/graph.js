import { html, h, memo, Component } from "./h.js";
import { Live } from "./live-component.js";
import { Age } from "./ticker.js";
import { ageOf, ageText } from "./store.js";
import { hoverProps } from "./hover.js";
import { DUTY, DUTY_TITLE, stateWord } from "./select.js";
import { MAX_SPARKS, MODES, glyphOf, modeOf, shortName } from "./graph-world.js";
import { COLUMNS, FLOW, ZOOM, fitView, flowCardOf, flowLayout, flowLinks, flowPath, isActive, pairKey, pathText, sparkKinds } from "./flow-model.js";

export { shortName, COLUMNS, FLOW, ZOOM, fitView, flowLayout, flowPath, isActive, pairKey, sparkKinds };

// The Graph view's two layouts, Map and Flow, and what they share: the remembered choice, the header toggle and the link kinds.
// Flow's stage lives here too; Map is graph-pack.js. Flow's maths is flow-model.js, shared with the Herdr mod.

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

// "N messages this run · M in the last hour", the hour counted now from the times the server listed.
export const messagesText = (c, now = Date.now()) => `${c.n} messages this run · ${c.recent.filter((t) => t >= now - 3_600_000).length} in the last hour`;

const ageFrom = (ms) => html`<${Age} text=${(now) => ageText(ageOf({ as_of: ms }, {}, now))} />`;

// One member in Flow: a card with what is known of it when it is working or running, a single row when it is not. A field with no source
// is not drawn. The time is the member's last activity; a Running orchestrator records none through its turns, so it has no time.
export class FlowCard extends Live {
  select(snap, { teamId, id }) {
    return flowCardOf(snap.slices, teamId, id);
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

// Flow's links: reporting lines from card to card, a brief in flight accent with a dot, a brief sent for review dashed.
const FlowLinks = memo(function FlowLinks({ edges, sparks, rects, orient }) {
  const { links, dots } = flowLinks(edges, sparks, rects, orient);
  return html`<g class="links">
    <g class="edges">${links.map((l) => html`<path key=${l.key} class=${`edge${l.kind === "review" ? " edge-review" : l.kind === "brief" ? " edge-live" : ""}`} d=${pathText(l.cmds)} />`)}</g>
    <g class="dots">${dots.map((d) => html`<circle key=${d.key} class=${`dot${d.review ? " dot-review" : ""}`} r="3" style=${{ offsetPath: `path("${pathText(d.cmds)}")` }} />`)}</g>
  </g>`;
});

const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));

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
