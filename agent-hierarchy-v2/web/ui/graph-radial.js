import { html } from "./h.js";
import { hoverProps } from "./hover.js";
import { GraphMap } from "./graph-pack.js";
import { fadeOf, glyphOf, kindOf, RADIAL_CAP, radialView, treeOf } from "./graph-world.js";

// Graph · Radial: the same team, data, panel, scrubber, keys and breadcrumbs as Map (it is Map with another layout and world), drawn as
// a ring. Members sit on the ring, a band per stream outside it, and one bundled path per message in the scrubber's view, through the
// tree's middle. A band click fits its sector; a member click traces its paths, sent solid and received dashed.

export const CAP = RADIAL_CAP;
const DEPTH_R = [0, 0.3, 0.55]; // internal nodes pulled in, as a share of R, so the bundles bend through the middle
const LABEL_ROOM = 168; // R leaves this much of the half-stage for the labels and the bands
const BAND = { stream: 120 }; // a band's inner radius past R; the root is the team, so it has no band (breadcrumbs and Fit cover it)
const BAND_W = 18;
const DOT = 9;
const HIT = 12; // the dot's target: 24 px across at rest
const LABEL_GAP = 16;
const SECTOR = [-20, 162]; // a fitted sector holds its dots from R − 20 to the outer band's edge
const round = (n) => Math.round(n * 10) / 10;

export class GraphRadial extends GraphMap {
  scaleExtent() {
    return [0.6, 12];
  }

  flowCap() {
    return CAP;
  }

  flyMs() {
    return 600;
  }

  viewOf(head, pages, sc) {
    return radialView(head, pages, sc, CAP);
  }

  stageClass() {
    return "gpack gradial";
  }

  svgLabel() {
    return "Team ring: members on a ring, a band per stream, one path per message. + and − zoom, 0 fits, arrows pan";
  }

  legendItems(selected) {
    return selected
      ? html`<span class="keys"><span><i class="sw sent" aria-hidden="true"></i>sent</span><span><i class="sw recv" aria-hidden="true"></i>received</span></span>`
      : html`<span class="keys"><span><i class="sw brief" aria-hidden="true"></i>brief</span><span><i class="sw report" aria-hidden="true"></i>report</span></span>`;
  }

  // No label tiers: labels hold one screen size through --k.
  semantic() {
    this.svg?.style.setProperty("--k", String(this.k ?? 1));
  }

  // The ring is laid out in stage pixels, so at rest the zoom is the identity and the labels have their room whatever the stage size.
  layout(rows, team) {
    const W = this.w || 1000;
    const H = this.h || 700;
    const struct = `${W}x${H}\n${rows.map((r) => `${r.id}|${r.stream ?? ""}`).join("\n")}`;
    if (struct === this.struct) {
      return;
    }
    const d3 = this.d3;
    this.struct = struct;
    const R = Math.max(40, Math.min(W, H) / 2 - LABEL_ROOM);
    const root = d3.cluster().size([2 * Math.PI, R]).separation((a, b) => (a.parent === b.parent ? 1 : 1.6))(d3.hierarchy(treeOf(team.id ?? "team", team.body.name, rows)));
    const [cx, cy] = [W / 2, H / 2];
    root.each((n) => {
      n.a = n.x;
      n.rr = n.children ? R * (DEPTH_R[n.depth] ?? DEPTH_R[DEPTH_R.length - 1]) : R;
      [n.x, n.y] = this.at(n.a, n.rr, cx, cy);
    });
    // a sector's edge is half the gap between neighbouring members
    const angles = root.leaves().map((l) => l.a).sort((a, b) => a - b);
    const gap = Math.min(2 * Math.PI, ...angles.slice(1).map((a, i) => a - angles[i]));
    this.geo = { R, cx, cy, pad: gap / 2 };
    this.root = root;
    this.byId = new Map(root.descendants().map((n) => [n.data.id, n]));
    this.line = d3.line().curve(d3.curveBundle.beta(0.85)).x((d) => d.x).y((d) => d.y);
    this.arc = d3.arc();
    this.paths = new Map();
    this.refit = true;
  }

  at(a, r, cx = this.geo.cx, cy = this.geo.cy) {
    return [cx + r * Math.sin(a), cy - r * Math.cos(a)];
  }

  // A container's sector: its first member's angle to its last, padded half a gap each side.
  sectorOf(n) {
    const l = n.leaves();
    return [l[0].a - this.geo.pad, l[l.length - 1].a + this.geo.pad];
  }

  // The root is the whole ring at rest; a container fits its sector, its dots to its band, at most 4×.
  transformOf(n) {
    const d3 = this.d3;
    if (n === this.root) {
      return d3.zoomIdentity;
    }
    const { R } = this.geo;
    const pts = n.leaves().flatMap((l) => [this.at(l.a, R + SECTOR[0]), this.at(l.a, R + SECTOR[1])]);
    const xs = pts.map((p) => p[0]);
    const ys = pts.map((p) => p[1]);
    const [x0, x1, y0, y1] = [Math.min(...xs), Math.max(...xs), Math.min(...ys), Math.max(...ys)];
    const s = Math.min(4, 0.9 * Math.min(this.w / Math.max(1, x1 - x0), this.h / Math.max(1, y1 - y0)));
    return d3.zoomIdentity.translate(this.w / 2, this.h / 2).scale(s).translate(-(x0 + x1) / 2, -(y0 + y1) / 2);
  }

  // One bundled path from a to b through the tree, the same for every message on the pair.
  pathOf(a, b) {
    const key = `${a.data.id}|${b.data.id}`;
    let d = this.paths.get(key);
    if (d === undefined) {
      d = this.line(a.path(b));
      this.paths.set(key, d);
    }
    return d;
  }

  drawWorld({ rows, root, byId, head, sc, view, selected, focus }) {
    const { R, cx, cy } = this.geo;
    const rowsById = new Map(rows.map((r) => [r.id, r]));
    const nodeAt = (i) => byId.get(head.names[i]) ?? root;
    const selIdx = selected && head ? head.names.indexOf(selected) : -1;

    // the newest CAP messages in the view, one path each
    const drawn = view.drawn ?? [];
    const flows = drawn.map(([, [t, a, b, k], i]) => {
      const from = nodeAt(a);
      const to = nodeAt(b);
      if (from === to) {
        return null;
      }
      const own = selIdx >= 0 && (a === selIdx || b === selIdx);
      const fade = fadeOf(sc.cursor, t, sc.win);
      return html`<path key=${i} class=${`gr-flow ${kindOf(k)}${own ? (b === selIdx ? " on in" : " on") : ""}`} d=${this.pathOf(from, to)} style=${fade === null ? undefined : { "--fade": round(fade * 100) / 100 }} />`;
    });
    const note = view.count > drawn.length ? ` · newest ${drawn.length} of ${view.count}` : "";

    const bands = root.descendants().filter((n) => n.depth > 0 && n.children && BAND[n.data.type] !== undefined).map((n, i) => {
      const [a0, a1] = this.sectorOf(n);
      const r0 = R + BAND[n.data.type];
      const r = r0 + BAND_W / 2;
      // the name runs along the band; on the lower half its path runs backwards so the text stays upright
      const mid = (a0 + a1) / 2;
      const flip = mid > Math.PI / 2 && mid < (3 * Math.PI) / 2;
      const [x0, y0] = this.at(flip ? a1 : a0, r);
      const [x1, y1] = this.at(flip ? a0 : a1, r);
      const big = a1 - a0 > Math.PI ? 1 : 0;
      return { n, i, d: this.arc({ innerRadius: r0, outerRadius: r0 + BAND_W, startAngle: a0, endAngle: a1 }), label: `M${round(x0)},${round(y0)}A${round(r)},${round(r)} 0 ${big} ${flip ? 0 : 1} ${round(x1)},${round(y1)}` };
    });
    const focused = focus === root ? null : focus;
    const members = root.leaves().filter((n) => n.data.type === "member");

    const body = html`<g class="gr-body"><defs>${bands.map((b) => html`<path key=${b.n.data.id} id=${`gr-bp-${b.i}`} d=${b.label} />`)}</defs>
      <g class="gr-bands">${bands.map(({ n, i, d }) => {
        const go = () => this.onContainer(n);
        const on = n === focused;
        const name = `${n.data.type} ${n.data.name}, ${n.leaves().filter((l) => l.data.type === "member").length} members. ${on ? "Zoom out" : "Zoom in"}`;
        return html`<g key=${n.data.id} class=${`gr-band ${n.data.type}${on ? " on" : ""}`} tabindex="0" role="button" aria-label=${name} onClick=${(e) => (e.stopPropagation(), go())} onKeyDown=${this.activate(go)}>
          <path class="gr-arcband" d=${d} transform=${`translate(${round(cx)},${round(cy)})`} />
          <text class="lbl band-l"><textPath href=${`#gr-bp-${i}`} startOffset="50%">${n.data.name}</textPath></text>
        </g>`;
      })}</g>
      <g class=${`gr-flows${selIdx >= 0 ? " focus" : ""}`}>${flows}</g>
      <g class="gr-mems">${members.map((n) => {
        const r = rowsById.get(n.data.id) ?? n.data.m;
        const pick = () => this.onMember(n.data.id);
        const left = n.a > Math.PI;
        const deg = (n.a * 180) / Math.PI - 90;
        const out = focused && !n.ancestors().includes(focused);
        return html`<g key=${n.data.id} class=${`gr-mem st-${r.state}${selected === n.data.id ? " selected" : ""}${out ? " out" : ""}`} transform=${`translate(${round(n.x)},${round(n.y)})`} tabindex="0" role="button" aria-pressed=${selected === n.data.id} aria-label=${`${r.role || "member"} ${r.name}, ${r.word}${r.ctx ? `, context ${r.ctx}` : ""}`} ...${hoverProps(this.store, n.data.id, "card")} onClick=${(e) => (e.stopPropagation(), pick())} onKeyDown=${this.activate(pick)}>
          <circle class="hit" r=${HIT} />
          <circle class="dot" r=${DOT} />
          <text class=${`lbl ${left ? "end" : "start"}`} transform=${`rotate(${round(left ? deg + 180 : deg)}) translate(${left ? -LABEL_GAP : LABEL_GAP},0)`}>${`${glyphOf(r.state)} ${r.name}`}</text>
        </g>`;
      })}</g></g>`;
    return { body, note };
  }
}
