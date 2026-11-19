import { html } from "./h.js";
import { Live } from "./live-component.js";
import { DUTY, DUTY_TITLE, memberIds, memberOf, sparksOf, stateWord, teamLabel } from "./select.js";
import { hoverProps } from "./hover.js";
import { loadD3 } from "./lazy.js";
import { GraphToggle, UNDER_GLYPH, setGraph } from "./graph.js";
import { graphSources, jobsUnder, lapseAt, runText, subagentsUnder, underOf, underTip } from "./flow-model.js";
import { ticker } from "./ticker.js";
import {
  WINDOWS, STEP_MS, currentItem, eventsOf, fadeOf, flowId, flowLeases, hhmm, itemsId, kindOf, lastEventOf, pagesOf, planId, planStreams, readout,
  scrubOf, scrubbed, shortName, streamOf, treeOf, widthOf, airOf, fitPack, sideways, TIERS, LABEL_AT, MAX_SPARKS, radiusOf,
} from "./graph-world.js";

// Graph · Map: the team as packed circles (streams side by side, members inside), message arcs bundled through the circles, a busy ring
// per member, a spark per brief in flight, semantic zoom, and a scrubber over the run's messages. d3 lays it out and zooms it; Preact
// draws it. The zoom writes the world's transform and, once per frame while it moves, which label tiers show: nothing runs at rest.

// Keyboard zoom and pan on the focused map, as the old Map had: + and − zoom, 0 fits, the arrows pan.
const ZOOM_KEYS = { "+": 1.25, "=": 1.25, "-": 1 / 1.25, _: 1 / 1.25 };
const PAN_KEYS = { ArrowLeft: [1, 0], ArrowRight: [-1, 0], ArrowUp: [0, 1], ArrowDown: [0, -1] };
const PAN = 60;

const reduced = () => globalThis.matchMedia?.("(prefers-reduced-motion: reduce)").matches === true;
const round = (n) => Math.round(n * 10) / 10;

// A member as Map draws it, one flat row; the key of all of them is what tells Map to draw again.
function memberRowsOf(slices, team) {
  const t = team.body;
  return memberIds(team).map((id) => {
    const m = memberOf(slices, team, id);
    const stream = streamOf(slices, t, slices.get(id)?.body.placement?.stream ?? null);
    const ctx = m.state === "gone" ? null : m.ctx;
    const item = currentItem(slices, t, stream, m);
    return { id, name: shortName(id, m.label), title: m.title, role: m.role, state: m.running ? "running" : m.state, word: stateWord(m.state, m.running).toLowerCase(), duty: m.duty === "away" ? DUTY_TITLE.away : m.duty === "on" ? `${m.role} · ${DUTY.on}` : null, share: m.share, ctx: ctx?.text ?? null, model: m.model ?? "", stream, item: item?.key ?? null };
  });
}

export class GraphMap extends Live {
  leaseIds(snap, { teamId }) {
    const team = snap.slices.get(teamId);
    if (!team) {
      return [];
    }
    const t = team.body;
    return [...flowLeases(snap.slices, t, snap.ui, Date.now(), this.flowCap()), planId(t), ...planStreams(snap.slices, t).map((s) => itemsId(t, s))];
  }

  select(snap, { teamId }) {
    const team = snap.slices.get(teamId);
    if (!team) {
      return { ready: false };
    }
    const t = team.body;
    const head = snap.slices.get(flowId(t));
    const open = snap.slices.get(`list:open:team:${t.dir_id}/${t.name}`)?.body.open ?? [];
    const overdue = new Set(open.filter((o) => snap.slices.get(o.id)?.body.state === "overdue").map((o) => o.id));
    return {
      ready: true,
      rows: JSON.stringify(memberRowsOf(snap.slices, team)),
      // ah2-225: the team's jobs and subagents, under their owners (Map draws no lane, so none of a lane's)
      under: JSON.stringify([...this.underOf(snap.slices, team)]),
      head: head ? `${head.as_of}|${head.body.pages_rev ?? ""}` : "",
      pages: (head?.body.pages ?? []).map((_, k) => snap.slices.get(`flow-page:${t.dir_id}/${t.name}/${head.body.run}/${k}`)?.as_of ?? "").join(","),
      sparks: sparksOf(snap.slices, team).map((s) => `${s.id}|${s.from}|${s.to}|${overdue.has(s.id) ? 1 : 0}`).join("\n"),
      selected: snap.ui.selected ?? "",
      focus: snap.ui.graphFocus ?? "",
      cursor: snap.ui.graphCursor ?? -1,
      win: snap.ui.graphWindow ?? "run",
    };
  }

  // ah2-225: the team's jobs and subagents under their owners; `lapse` when the first subagent lapses, drawn again then on the age tick
  underOf(slices, team) {
    const under = underOf(jobsUnder(graphSources(slices, team, memberIds(team), [], "jobs")), subagentsUnder(graphSources(slices, team, memberIds(team), [], "subagents"), Date.now()));
    this.lapse = lapseAt(under);
    return under;
  }

  componentDidMount() {
    super.componentDidMount();
    this.offLapse = ticker.subscribe((now) => now >= (this.lapse ?? Infinity) && ((this.lapse = Infinity), this.forceUpdate()));
    loadD3().then((d3) => {
      this.d3 = d3;
      !this.gone && this.forceUpdate();
    }, () => {
      this.failed = true;
      !this.gone && this.forceUpdate();
    });
    // Off screen, the sparks stop: their SMIL clock pauses and they are not drawn.
    if (typeof IntersectionObserver === "function" && this.base) {
      this.io = new IntersectionObserver((entries) => {
        const off = !entries[entries.length - 1].isIntersecting;
        this.base?.classList?.toggle("offscreen", off);
        off ? this.svg?.pauseAnimations?.() : this.svg?.unpauseAnimations?.();
      });
      this.io.observe(this.base);
    }
    if (typeof ResizeObserver === "function") {
      this.ro = new ResizeObserver(([e]) => {
        const w = Math.round(e.contentRect.width);
        const h = Math.round(e.contentRect.height);
        if (w > 0 && h > 0 && (w !== this.w || h !== this.h)) {
          this.w = w;
          this.h = h;
          this.refit = true;
          this.forceUpdate();
        }
      });
    }
  }

  componentDidUpdate(prev) {
    super.componentDidUpdate?.(prev);
    this.wire();
    if (prev.fitNonce !== this.props.fitNonce) {
      this.fit();
    }
  }

  componentWillUnmount() {
    super.componentWillUnmount();
    this.offLapse?.();
    this.gone = true;
    this.ro?.disconnect();
    this.io?.disconnect();
    clearInterval(this.timer);
    this.raf && cancelAnimationFrame(this.raf);
    this.zoom && this.d3.select(this.svg).on(".zoom", null).interrupt();
  }

  get store() {
    return this.props.store;
  }

  // What Radial (graph-radial.js, a subclass) changes (each hook returns one element: an array would draw as a Fragment of its own): the zoom range, the events it leases (Map draws pairs: none capped), the
  // stage's name and legend, the layout, the fit and the drawn world.
  scaleExtent() {
    return [0.6, 30];
  }

  flowCap() {
    return 0;
  }

  stageClass() {
    return "gpack";
  }

  svgLabel() {
    return "Team map: streams and members as nested circles, message arcs between members. + and − zoom, 0 fits, arrows pan";
  }

  legendItems() {
    return html`<span class="keys"><span><i class="sw brief" aria-hidden="true"></i>brief</span><span><i class="sw report" aria-hidden="true"></i>report</span><span><i class="sw ring" aria-hidden="true"></i>busy, last hour</span><span><i class="sw sub" aria-hidden="true"></i>subagent</span><span><b class="sw-job" aria-hidden="true">◷</b>job</span></span>`;
  }

  // Binds d3.zoom to the drawn svg once, and flies to the focus when the layout or the stage size changed.
  wire() {
    const d3 = this.d3;
    if (!d3 || !this.svg || !this.root) {
      return;
    }
    if (this.ro && this.observed !== this.svg) {
      this.observed = this.svg;
      this.ro.observe(this.svg);
    }
    if (this.zoomed !== this.svg) {
      this.zoomed = this.svg;
      this.zoom = d3.zoom().scaleExtent(this.scaleExtent()).on("zoom", (e) => {
        this.world.setAttribute("transform", e.transform.toString());
        this.k = e.transform.k;
        this.schedule();
      });
      d3.select(this.svg).call(this.zoom).on("dblclick.zoom", null);
      this.refit = true;
    }
    // A focus or card from another team (ui keeps them across a team switch) is dropped, back to the team and the feed.
    const ui = this.store.state.ui;
    const stale = (id) => id && id !== "team" && !this.byId?.has(id);
    this.root && (stale(ui.graphFocus) || stale(ui.graphCard)) && this.store.setUi({ graphFocus: null, graphCard: null });
    if (this.refit && this.w) {
      this.refit = false;
      this.zoomTo(this.nodeOf(this.store.state.ui.graphFocus) ?? this.root, 0, null);
    }
    this.semantic();
  }

  nodeOf(id) {
    return id ? this.byId?.get(id) ?? null : null;
  }

  schedule() {
    this.raf ||= requestAnimationFrame(() => {
      this.raf = 0;
      this.semantic();
    });
  }

  // Semantic zoom: which labels show at the on-screen radius r·k. Runs once per frame while the zoom moves and after a draw.
  semantic() {
    const k = this.k ?? 1;
    if (!this.svg) {
      return;
    }
    this.svg.style.setProperty("--k", String(k));
    for (const el of this.svg.querySelectorAll(".gm-leaf, .gm-under")) {
      const px = Number(el.getAttribute("data-r")) * k;
      el.setAttribute("data-z", String(TIERS.filter((t) => px >= t).length));
    }
    for (const el of this.svg.querySelectorAll(".gm-clabel")) {
      el.setAttribute("data-show", Number(el.getAttribute("data-r")) * k >= LABEL_AT[el.getAttribute("data-type")] ? "1" : "0");
    }
  }

  // Flies to a container: its box (for the root, the box of its children) fitted to the stage. The focus is written first, so the
  // breadcrumbs and panel follow even before the stage has a size. `card` also sets the rail's card: true shows this container's
  // ("team" for the root), false closes any card, null leaves it.
  zoomTo(n, ms = this.flyMs(), card = true) {
    const id = n === this.root ? null : n.data.id;
    const ui = this.store.state.ui;
    const patch = {};
    (ui.graphFocus ?? null) !== id && (patch.graphFocus = id);
    const cardId = card === true ? id ?? "team" : card === false ? null : undefined; // null: leave the card as it is
    cardId !== undefined && (ui.graphCard ?? null) !== cardId && (patch.graphCard = cardId);
    Object.keys(patch).length && this.store.setUi(patch);
    if (!this.zoom || !this.w) {
      return;
    }
    const sel = this.d3.select(this.svg);
    (ms && !reduced() ? sel.transition().duration(ms) : sel.interrupt()).call(this.zoom.transform, this.transformOf(n));
  }

  // The zoom that fits a container's box (for the root, the box of its children) to the stage.
  transformOf(n) {
    const kids = n === this.root ? n.children ?? [n] : [n];
    const x0 = Math.min(...kids.map((d) => d.x - d.r));
    const x1 = Math.max(...kids.map((d) => d.x + d.r));
    const y0 = Math.min(...kids.map((d) => d.y - d.r));
    const y1 = Math.max(...kids.map((d) => d.y + d.r));
    const s = 0.92 * Math.min(this.w / (x1 - x0), this.h / (y1 - y0));
    return this.d3.zoomIdentity.translate(this.w / 2, this.h / 2).scale(s).translate(-(x0 + x1) / 2, -(y0 + y1) / 2);
  }

  // How long a fly to a container takes, in ms.
  flyMs() {
    return 750;
  }

  // What the scrubber shows where it stands: the pairs to draw and how many messages they hold.
  viewOf(head, pages, sc) {
    return scrubbed(head, pages, sc);
  }

  fit = () => {
    this.store.setUi({ selected: null, graphCard: null });
    this.root && this.zoomTo(this.root, this.flyMs(), false);
  };

  // A click on the background deselects: no member, no container card (the rail goes back to the feed).
  onBackground = () => this.store.setUi({ selected: null, graphCard: null });

  scaleBy(f) {
    this.zoom && this.d3.select(this.svg).transition().duration(reduced() ? 0 : 250).call(this.zoom.scaleBy, f);
  }

  // + − 0 and the arrows on the focused map; any modifier leaves the key to the page.
  onMapKey = (e) => {
    if (e.altKey || e.ctrlKey || e.metaKey) {
      return;
    }
    if (ZOOM_KEYS[e.key]) {
      this.scaleBy(ZOOM_KEYS[e.key]);
    } else if (e.key === "0") {
      this.fit();
    } else if (PAN_KEYS[e.key]) {
      const [dx, dy] = PAN_KEYS[e.key];
      const k = this.k ?? 1;
      this.zoom && this.d3.select(this.svg).transition().duration(reduced() ? 0 : 150).call(this.zoom.translateBy, (dx * PAN) / k, (dy * PAN) / k);
    } else {
      return;
    }
    e.preventDefault();
  };

  // Click on a container: fly to it, or out to its parent when it is the focus. The selection clears.
  onContainer = (n) => {
    this.store.setUi({ selected: null });
    const focus = this.nodeOf(this.store.state.ui.graphFocus) ?? this.root;
    // flying out of a container closes its card (back to the feed); flying in shows the new one
    n === focus && n.parent ? this.zoomTo(n.parent, this.flyMs(), false) : this.zoomTo(n);
  };

  onMember = (id) => this.store.setUi({ selected: this.store.state.ui.selected === id ? null : id });

  // Esc inside the stage: clear the selection, else fly out a level. Outside the stage the page's own Esc is untouched.
  onKeyDown = (e) => {
    if (e.key !== "Escape" || e.altKey || e.ctrlKey || e.metaKey) {
      return;
    }
    const ui = this.store.state.ui;
    const focus = this.nodeOf(ui.graphFocus);
    if (ui.selected) {
      this.store.setUi({ selected: null });
    } else if (focus?.parent) {
      this.zoomTo(focus.parent, this.flyMs(), false);
    } else if (ui.graphCard) {
      this.store.setUi({ graphCard: null });
    } else {
      return;
    }
    e.stopPropagation();
    e.preventDefault();
  };

  activate = (fn) => (e) => {
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      fn();
    }
  };

  // ---- the scrubber

  onCursor = (e, span) => {
    const v = Number(e.currentTarget.value);
    this.store.setUi({ graphCursor: v >= span.max ? null : v });
  };

  onWindow = (e) => this.store.setUi({ graphWindow: e.currentTarget.value });

  // Play steps the cursor a 24th of the window every 400 ms up to now, then stops. Off at the start of the run if it was at now.
  onPlay = (head) => {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = 0;
      this.forceUpdate();
      return;
    }
    const step = () => {
      const h = this.headBody;
      if (!h) {
        return;
      }
      const s = scrubOf(this.store.state.ui, h, Date.now());
      const by = s.win === null ? (s.span.max - s.span.min) / 24 : (s.win * 1000) / 24;
      const next = Math.ceil((s.cursorMs + by) / STEP_MS) * STEP_MS;
      if (next >= s.span.max) {
        clearInterval(this.timer);
        this.timer = 0;
        this.store.setUi({ graphCursor: null });
      } else {
        this.store.setUi({ graphCursor: next });
      }
    };
    const s = scrubOf(this.store.state.ui, head, Date.now());
    if (s.live) {
      this.store.setUi({ graphCursor: s.span.min });
    }
    this.timer = setInterval(step, 400);
    this.forceUpdate();
  };

  // ---- layout

  layout(rows, team, under = null) {
    const struct = `${rows.map((r) => `${r.id}|${r.stream ?? ""}`).join("\n")}\n${under ? [...under].map(([k, list]) => `${k}:${list.map((x) => x.key).join(",")}`).join("\n") : ""}`;
    if (struct === this.struct) {
      return;
    }
    const d3 = this.d3;
    this.struct = struct;
    const tree = treeOf(team.id ?? "team", teamLabel(team.body), rows, under);
    // Air scales with the member circles, not with the canvas (graph-world.js airOf), then the whole is fitted to the 1000-unit square.
    // A job or subagent packs smaller, beside its owner (graph-world.js treeOf, radiusOf).
    const root = d3.pack().radius(radiusOf).padding(airOf)(d3.hierarchy(tree).sum((d) => (d.type === "member" ? 1 : 0)));
    fitPack(root, 1000);
    this.root = root;
    this.byId = new Map(root.descendants().map((n) => [n.data.id, n]));
    this.line = d3.line().curve(d3.curveBundle.beta(0.85)).x((d) => d.x).y((d) => d.y);
    this.arc = d3.arc();
    this.paths = new Map();
    this.refit = true;
  }

  // One bundled path from a to b through the circles they sit in, offset sideways: briefs one way, reports the other.
  pathOf(a, b, k) {
    const key = `${a.data.id}|${b.data.id}|${k}`;
    let d = this.paths.get(key);
    if (d === undefined) {
      d = this.line(sideways(a.path(b), a, b, k, 1000));
      this.paths.set(key, d);
    }
    return d;
  }

  // Map's world: container circles, the bundled arcs, sparks, members with their busy rings and label tiers, container labels.
  drawWorld({ s, rows, root, byId, head, sc, view, pages, selected, focus, clock }) {
    const nodeAt = (i) => byId.get(head.names[i]) ?? root;
    const rowsById = new Map(rows.map((r) => [r.id, r]));
    const held = head ? eventsOf(head, pages) : [];
    const nameIdx = new Map((head?.names ?? []).map((id, i) => [id, i]));
    const lastOf = (id) => {
      const at = nameIdx.has(id) ? lastEventOf(held, nameIdx.get(id)) : null;
      return at === null ? null : `${Math.max(0, Math.round((clock - (head.start_ms + at * 1000)) / 60_000))}m`;
    };

    // One path per pair and kind, whichever way the messages ran: two directions drawn side by side read as a dotted tangle.
    const both = new Map();
    for (const p of view.pairs) {
      const [a, b] = p.a < p.b ? [p.a, p.b] : [p.b, p.a];
      const key = `${a}|${b}|${p.k}`;
      const q = both.get(key);
      q ? ((q.n += p.n), (q.last = Math.max(q.last ?? -Infinity, p.last ?? -Infinity))) : both.set(key, { ...p, a, b });
    }
    const arcs = [...both.values()].map((p) => {
      const a = nodeAt(p.a);
      const b = nodeAt(p.b);
      if (a === b) {
        return null;
      }
      const on = selected !== null && (a.data.id === selected || b.data.id === selected);
      const fade = sc ? fadeOf(sc.cursor, p.last, sc.win) : null;
      return html`<path key=${`${p.a}|${p.b}|${p.k}`} class=${`gm-arc ${kindOf(p.k)}${on ? " on" : ""}`} d=${this.pathOf(a, b, p.k)} stroke-width=${round(widthOf(p.n))} style=${fade === null ? undefined : { "--fade": round(fade * 100) / 100 }} />`;
    });

    const allSparks = sc?.live && s.sparks ? s.sparks.split("\n").map((line) => line.split("|")) : [];
    const sparkRows = allSparks.slice(0, MAX_SPARKS);
    const moreSparks = allSparks.length - sparkRows.length;
    const perPair = new Map();
    const sparks = sparkRows.map(([id, from, to, over]) => {
      const a = byId.get(from);
      const b = byId.get(to);
      if (!a || !b || a === b) {
        return null;
      }
      const pair = `${from}|${to}`;
      const j = perPair.get(pair) ?? 0;
      perPair.set(pair, j + 1);
      const pid = `gm-sp-${id.replace(/[^\w-]/g, "_")}`;
      return html`<g key=${id}><path id=${pid} class="gm-spark-path" d=${this.pathOf(a, b, 0)} /><circle class=${`spark${over === "1" ? " over" : ""}`} r="4"><animateMotion dur="3s" begin=${`${round(j * 0.7)}s`} repeatCount="indefinite"><mpath href=${`#${pid}`} /></animateMotion></circle></g>`;
    });

    const containers = root.descendants().filter((n) => n.children && n.data.type !== "group");
    const leaves = root.leaves().filter((n) => n.data.type === "member");
    // ah2-225: jobs and subagents, beside their owner (an undrawn group); a subagent dashed, with a dashed line to its owner. Not a target.
    const unders = root.leaves().filter((n) => n.data.type === "job" || n.data.type === "subagent");
    const ownerOf = (n) => (n.parent?.data.type === "group" && n.parent.data.id.startsWith("with:") ? byId.get(n.parent.data.id.slice("with:".length)) : null);
    const nameOf = (o) => (o ? rowsById.get(o.data.id)?.name ?? o.data.name : null);
    const ringOf = (n, share) => this.arc({ innerRadius: n.r * 0.81, outerRadius: n.r * 0.91, startAngle: 0, endAngle: Math.max(0.04, share ?? 0) * 2 * Math.PI });
    const body = html`<g class="gm-body">
            <g class="gm-ctrs">${containers.map((n) => {
              const name = `${n.data.type} ${n.data.name}, ${n.leaves().filter((l) => l.data.type === "member").length} members. ${n === focus && n.parent ? "Zoom out" : "Zoom in"}`;
              const go = () => this.onContainer(n);
              return n.depth === 0
                ? html`<circle key=${n.data.id} class="ctr root" cx=${n.x} cy=${n.y} r=${n.r} />`
                : html`<g key=${n.data.id} class=${`gm-node ${n.data.type}`} tabindex="0" role="button" aria-label=${name} onClick=${(e) => (e.stopPropagation(), go())} onKeyDown=${this.activate(go)}><circle class=${`ctr ${n.data.type}`} cx=${n.x} cy=${n.y} r=${n.r} /></g>`;
            })}</g>
            <g class=${`gm-arcs${selected ? " focus" : ""}`}>${arcs}</g>
            <g class="gm-sparks">${sparks}</g>
            <g class="gm-under-links">${unders.filter((n) => n.data.x.kind === "subagent" && ownerOf(n)).map((n) => html`<line key=${n.data.id} class="gm-under-link" x1=${round(n.x)} y1=${round(n.y)} x2=${round(ownerOf(n).x)} y2=${round(ownerOf(n).y)} />`)}</g>
            <g class="gm-leaves">${leaves.map((n) => {
              const r = rowsById.get(n.data.id) ?? n.data.m;
              const pick = () => this.onMember(n.data.id);
              return html`<g key=${n.data.id} class=${`gm-leaf st-${r.state}${selected === n.data.id ? " selected" : ""}`} data-r=${round(n.r)} transform=${`translate(${round(n.x)},${round(n.y)})`} tabindex="0" role="button" aria-pressed=${selected === n.data.id} aria-label=${`${r.role || "member"} ${r.name}, ${r.duty ? `${r.duty}, ` : ""}${r.word}${r.ctx ? `, context ${r.ctx}` : ""}`} ...${hoverProps(this.store, n.data.id, "card")} onClick=${(e) => (e.stopPropagation(), pick())} onKeyDown=${this.activate(pick)}>
                <circle class="body" r=${round(n.r)} />
                <circle class="track" r=${round(n.r * 0.86)} stroke-width=${round(n.r * 0.1)} />
                <path class="busy" d=${ringOf(n, r.share)} />
                <text class="lbl val t1" dy="-.8em">${r.state === "gone" ? "×" : r.ctx ?? ""}</text>
                <text class="lbl t2" dy=".45em">${r.name}</text>
                <text class="lbl sub t3" dy="1.75em">${r.duty ?? r.role}</text>
                <text class="lbl sub t4" dy="2.95em">${[r.model, r.word].filter(Boolean).join(" · ")}</text>
                <text class="lbl sub t5" dy="4.6em">${[r.item ?? "no item", lastOf(n.data.id) && `last message ${lastOf(n.data.id)}`].filter(Boolean).join(" · ")}</text>
              </g>`;
            })}</g>
            <g class="gm-unders">${unders.map((n) => {
              const x = n.data.x;
              const o = ownerOf(n);
              const tip = underTip(x, nameOf(o), clock);
              return html`<g key=${n.data.id} class=${`gm-under k-${x.kind}${x.overdue ? " over" : ""}`} data-r=${round(n.r)} transform=${`translate(${round(n.x)},${round(n.y)})`} role="img" aria-label=${tip}>
                <title>${tip}</title>
                <circle class="body" r=${round(n.r)} />
                <text class="lbl val t1" dy=".1em">${x.overdue ? "⚠" : UNDER_GLYPH[x.kind]}</text>
                <text class="lbl sub t2" dy="1.6em">${x.kind === "subagent" ? "subagent" : runText(x, clock)}</text>
              </g>`;
            })}</g>
            <g class="gm-clabels">${containers.filter((n) => n.depth > 0).map((n) => html`<text key=${n.data.id} class="lbl ctrl gm-clabel" x=${round(n.x)} y=${round(n.y - n.r)} data-r=${round(n.r)} data-type=${n.data.type}>${n.data.name}</text>`)}</g>
          </g>`;
    return { body, more: moreSparks > 0 ? `+${moreSparks} more briefs in flight` : null };
  }

  draw({ teamId, legend = true }, s) {
    if (!s.ready) {
      return html`<p class="dim skeleton">Loading…</p>`;
    }
    if (this.failed) {
      return html`<p class="dim skeleton">Could not load the map.</p>`;
    }
    if (!this.d3) {
      return html`<div class=${`graph ${this.stageClass()}`}><p class="dim skeleton">Loading the map…</p></div>`;
    }
    const slices = this.store.state.slices;
    const team = slices.get(teamId);
    const t = team.body;
    const rows = JSON.parse(s.rows);
    const under = new Map(JSON.parse(s.under ?? "[]"));
    this.layout(rows, { ...team, id: teamId }, under.size ? under : null);
    const root = this.root;
    const byId = this.byId;
    const headSlice = slices.get(flowId(t));
    const head = headSlice?.body ?? null;
    this.headBody = head;
    const clock = Date.now();
    const sc = head ? scrubOf(this.store.state.ui, head, clock) : null;
    const pages = head ? pagesOf(slices, t, head) : new Map();
    const view = head ? this.viewOf(head, pages, sc) : { pairs: [], count: 0 };
    const selected = s.selected || null;
    const focus = this.nodeOf(s.focus) ?? root;
    const w = this.drawWorld({ s, rows, root, byId, head, sc, pages, view, selected, focus, clock });
    const playing = Boolean(this.timer);
    const still = reduced();
    const span = sc?.span;

    return html`<div class=${`graph ${this.stageClass()}`} onKeyDown=${this.onKeyDown}>
      <div class="gm-head">
        <nav class="gm-crumbs" aria-label="Zoom path">${focus.ancestors().reverse().map((n, i, all) => html`${i ? html`<span class="sep" aria-hidden="true">/</span>` : null}<button key=${n.data.id} type="button" aria-current=${i === all.length - 1 ? "location" : undefined} onClick=${() => this.zoomTo(n)}>${n.data.name}</button>`)}</nav>
        ${legend
          ? html`<span class="gm-legend" role="group" aria-label="Legend">${this.legendItems(selected)}<button type="button" class="x" aria-label="Dismiss legend" title="Dismiss legend" onClick=${() => setGraph(this.store, { legend: false })}>×</button></span>`
          : null}
      </div>
      <div class="gm-stage">
        <svg class="gm-svg" viewBox=${`0 0 ${this.w || 1000} ${this.h || 700}`} role="group" tabindex="0" aria-label=${this.svgLabel()} ref=${(el) => (this.svg = el)} onClick=${this.onBackground} onKeyDown=${(e) => e.target === this.svg && this.onMapKey(e)}>
          <g ref=${(el) => (this.world = el)}>${w.body}          </g>
        </svg>
        ${w.more ? html`<p class="gm-more">${w.more}</p>` : null}
        <div class="graph-controls" role="group" aria-label="Graph toolbar">
          <${GraphToggle} store=${this.store} />
          ${legend ? null : html`<button type="button" class="legend-show" onClick=${() => setGraph(this.store, { legend: true })}>Legend</button>`}
          <button type="button" aria-label="Zoom in" title="Zoom in" onClick=${() => this.scaleBy(1.5)}>+</button>
          <button type="button" aria-label="Zoom out" title="Zoom out" onClick=${() => this.scaleBy(1 / 1.5)}>−</button>
          <button type="button" class=${focus === root ? "fit on" : "fit"} title="Fit the team (Shift+F)" onClick=${this.fit}>Fit</button>
        </div>
      </div>
      <div class="gm-scrub" role="group" aria-label="Message history">
        <button type="button" class="gm-play" aria-pressed=${playing} disabled=${!head || still} title=${still ? "Play is off while reduced motion is on" : undefined} onClick=${() => this.onPlay(head)}>${playing ? "Pause" : "Play"}</button>
        <label for="gm-time">Time</label>
        <input id="gm-time" type="range" min=${span?.min ?? 0} max=${span?.max ?? 0} step=${STEP_MS} value=${sc?.cursorMs ?? 0} disabled=${!head} aria-describedby="gm-out" aria-valuetext=${head ? (sc.live ? "now" : hhmm(sc.cursorMs)) : "no history"} onInput=${(e) => span && this.onCursor(e, span)} />
        <span class="gm-end">${!sc || sc.live ? "" : `now +${Math.round((span.max - sc.cursorMs) / 60_000)}m`}</span>
        <output id="gm-out" for="gm-time">${head ? `${readout(head, sc.cursorMs, view.count, sc.live)}${w.note ?? ""}` : "no message history yet"}</output>
        <label for="gm-window">Window</label>
        <select id="gm-window" value=${sc?.winId ?? "run"} disabled=${!head} onChange=${this.onWindow}>${WINDOWS.map((w) => html`<option key=${w.id} value=${w.id}>${w.label}</option>`)}</select>
      </div>
    </div>`;
  }
}
