// Flow's maths, shared by the web's Flow (graph.js) and the Herdr mod's (herdr-desktop/mods/ah2/graph-model.js): the columns by role,
// where each card goes, the links between them as path commands, and what each card shows. Pure: no DOM. The mod holds a byte-identical
// copy (tests/web/herdr-graph-model.test.mjs), so edit this file and copy it. It also holds the jobs and subagents under their owners
// (ah2-225 B6), which the Herdr sidebar's tree, both Graphs and the web Overview draw.
import { inert, laneOf, memberOf } from "./select.js";
import { MAX_SPARKS, currentItem, itemsId, planId, planStreams, shortName, streamOf } from "./graph-world.js";

// ---- the columns by role, and where each card goes

export const COLUMNS = ["Lead", "Advise · design", "Implement", "Review", "Test", "Sub-orchestrators"];
// ah2-225 B2: QA members and full test runs. The daemon serves a QA member Review's column; Flow moves it here.
export const TEST_COLUMN = 4;
export const columnOf = (role, served) => (role === "qa" ? TEST_COLUMN : served);
// ah2-373: the sub-orchestrators have a column of their own; a deputy chief, on duty or not, sits in Lead under the Orchestrator (and
// so does a member of another role while it is the deputy on duty). Columns are drawn in chain-of-command order, only while a card is in them.
export const SUB_COLUMN = 5;
export const COLUMN_ORDER = [0, SUB_COLUMN, 1, 2, 3, TEST_COLUMN];
export const flowColumn = (role, served, deputy = false) => (role === "sub-orchestrator" ? SUB_COLUMN : deputy || role === "deputy-chief" ? 0 : columnOf(role, served));
// A job or subagent drawn under its owner sits this far in from the owner's edge, across the column.
export const UNDER_INDENT = 20;

// ah2-450 G6: a working card with a work title is `work` taller, the title on its own line between the head and the stats.
export const FLOW = { card: { w: 220, h: 64 }, row: { w: 200, h: 36 }, work: 20, gap: 16, band: 64, lane: 100, top: 90, left: 40 };

// Cards positioned by column: {pos: id -> {x, y, w, h}, labels: [{text, x, y}], box}. Horizontal has the columns left to right; vertical has them
// top to bottom, in COLUMN_ORDER, a column with no card left out (ah2-373 R6). A column's members keep name order, an orchestrator first;
// the lead column is centred on the tallest one. With lanes (a node's `lane` is its leader's id) the cards form bands across the columns:
// first the members in no lane, then one band per leader in name order, holding the leader and its lane's members; each card of a lane
// carries `band`, its band's extent across the columns. With lanes or a deputy every card carries `routed`, which has flowLinks route
// round them (laneRoutes).
// k: the zoom the layout is drawn at; below WORK_MIN_K no card draws its work line, so none is taller for it (ah2-456 N1).
export function flowLayout(nodes, active, orient, k = 1) {
  const horizontal = orient !== "vertical";
  const byName = [...nodes].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  const leaders = byName.filter((n) => n.lane === n.id);
  const bandOf = new Map([["", 0], ...leaders.map((l, i) => [l.id, i + 1])]);
  const bands = [[], ...leaders.map(() => [])];
  byName.forEach((n) => bands[bandOf.get(n.lane || "") ?? 0].push(n));
  // a full test run in the Test column is a card; any other job or subagent a row (flowExtras)
  const size = (n) => (n.item ? (n.after ? FLOW.row : n.item.kind === "job" && n.column === TEST_COLUMN ? FLOW.card : FLOW.row) : active.has(n.id) ? (drawsWork(n, active, k) ? { w: FLOW.card.w, h: FLOW.card.h + FLOW.work } : FLOW.card) : FLOW.row);
  const along = (n) => (horizontal ? size(n).h : size(n).w);
  const step = horizontal ? FLOW.card.w + FLOW.lane : FLOW.card.h + FLOW.lane;
  const pos = new Map();
  const colOf = (n) => (COLUMNS[n.column] !== undefined ? n.column : 2);
  const shown = COLUMN_ORDER.filter((ci) => nodes.some((n) => !n.after && colOf(n) === ci));
  const slot = new Map(shown.map((ci, i) => [ci, i]));
  const labels = shown.map((ci, i) => (horizontal ? { text: COLUMNS[ci], x: FLOW.left + i * step, y: FLOW.top - 32 } : { text: COLUMNS[ci], x: FLOW.left, y: FLOW.top + i * step - 24 }));
  const backdrops = [];
  const reach = (Math.max(1, shown.length) - 1) * step + (horizontal ? FLOW.card.w : FLOW.card.h);
  // a deputy (a card other than an orchestrator in Lead) or a lane: the links route round the cards
  const routed = leaders.length > 0 || nodes.some((n) => !n.after && colOf(n) === 0 && n.role !== "orchestrator");
  let offset = 0;
  for (const [bi, band] of bands.entries()) {
    if (band.length === 0) {
      continue;
    }
    const cols = COLUMNS.map(() => []);
    band.forEach((n) => !n.after && cols[colOf(n)].push(n));
    cols[0].sort((a, b) => (b.role === "orchestrator") - (a.role === "orchestrator"));
    // a job or subagent under its owner goes right after the owner and the owner's earlier ones
    band.forEach((n) => {
      if (!n.after) return;
      const col = cols.find((c) => c.some((m) => m.id === n.after)) ?? cols[colOf(n)];
      const at = col.reduce((last, m, i) => (m.id === n.after || m.after === n.after ? i : last), -1);
      at < 0 ? col.push(n) : col.splice(at + 1, 0, n);
    });
    const spans = cols.map((c) => c.reduce((sum, n) => sum + along(n) + FLOW.gap, -FLOW.gap));
    const tallest = Math.max(0, ...spans);
    const leader = bi > 0 ? leaders[bi - 1] : null;
    cols.forEach((c, ci) => {
      let at = (ci === 0 ? (tallest - Math.max(0, spans[ci])) / 2 : 0) + FLOW.top + offset;
      const base = FLOW.left + slot.get(ci) * step;
      c.forEach((n) => {
        const { w, h } = size(n);
        const inset = n.after ? UNDER_INDENT : 0;
        const r = horizontal ? { x: base + inset, y: at, w, h } : { x: FLOW.left + (at - FLOW.top), y: FLOW.top + slot.get(ci) * step + inset, w, h };
        pos.set(n.id, routed ? { ...r, routed } : r);
        at += along(n) + FLOW.gap;
      });
    });
    if (leader) {
      const start = FLOW.top + offset;
      const pad = FLOW.gap;
      const b = horizontal
        ? { id: leader.id, name: shortName(leader.id, leader.name), x: FLOW.left - pad / 2, y: start - pad - FLOW.gap, w: reach + pad, h: tallest + 2 * pad + FLOW.gap }
        : { id: leader.id, name: shortName(leader.id, leader.name), x: FLOW.left + offset - pad / 2, y: FLOW.top - pad - FLOW.gap, w: tallest + pad, h: reach + 2 * pad + FLOW.gap };
      backdrops.push(b);
      const extent = horizontal ? [b.y, b.y + b.h] : [b.x, b.x + b.w];
      band.forEach((n) => (pos.get(n.id).band = extent));
    }
    offset += tallest + FLOW.band;
  }
  let x0 = Infinity;
  let y0 = Infinity;
  let x1 = -Infinity;
  let y1 = -Infinity;
  // routed, room before the Lead column for the trunk from the Orchestrator to the deputy under it (laneRoutes)
  const room = (start) => Math.min(start, bandStart(!horizontal)) - ROUTE.trunk - ROUTE.step;
  const trunks = !routed ? [] : [horizontal ? { x: room(FLOW.left), y: FLOW.top, w: 1, h: 1 } : { x: FLOW.left, y: room(FLOW.top), w: 1, h: 1 }];
  for (const p of [...pos.values(), ...backdrops, ...trunks, ...labels.map((l) => ({ x: l.x, y: l.y, w: 120, h: 14 }))]) {
    x0 = Math.min(x0, p.x);
    y0 = Math.min(y0, p.y);
    x1 = Math.max(x1, p.x + p.w);
    y1 = Math.max(y1, p.y + p.h);
  }
  return { pos, labels, bands: backdrops, box: x0 === Infinity ? null : { x: x0, y: y0, w: x1 - x0, h: y1 - y0 } };
}

// ---- links

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

// A link from one card to the next, as commands ["M", x, y], ["L", x, y], ["Q", cx, cy, x, y], ["C", c1x, c1y, c2x, c2y, x, y] in screen
// order: right edge to left edge, or bottom to top when vertical. A cubic, unless a card in `cards` stands in its way: then it leaves the
// first card, runs along a rail clear of those cards (rounded corners), and comes into the second.
export function flowCmds(a, b, orient, cards = []) {
  const vertical = orient === "vertical";
  const axes = (r) => (vertical ? { u: r.y, len: r.h, w: r.x, size: r.w } : { u: r.x, len: r.w, w: r.y, size: r.h });
  const [A, B] = [axes(a), axes(b)];
  const [u1, w1, u2, w2] = [A.u + A.len, A.w + A.size / 2, B.u, B.w + B.size / 2];
  const rail = railOf(u1, w1, u2, w2, cards.filter((c) => c !== a && c !== b).map(axes));
  if (rail !== null) {
    const at = (u, w) => (vertical ? [w, u] : [u, w]);
    const [g1, g2] = [u1 + AROUND.gap, u2 - AROUND.gap];
    const turn = (from, to) => Math.min(AROUND.r, Math.abs(to - from) / 2) * Math.sign(to - from);
    const [r1, r2] = [turn(w1, rail), turn(rail, w2)];
    const [e1, e2] = [Math.abs(r1), Math.abs(r2)];
    return [
      ["M", ...at(u1, w1)], ["L", ...at(g1 - e1, w1)], ["Q", ...at(g1, w1), ...at(g1, w1 + r1)], ["L", ...at(g1, rail - r1)], ["Q", ...at(g1, rail), ...at(g1 + e1, rail)],
      ["L", ...at(g2 - e2, rail)], ["Q", ...at(g2, rail), ...at(g2, rail + r2)], ["L", ...at(g2, w2 - r2)], ["Q", ...at(g2, w2), ...at(g2 + e2, w2)], ["L", ...at(u2, w2)],
    ];
  }
  if (vertical) {
    const x1 = a.x + a.w / 2;
    const y1 = a.y + a.h;
    const x2 = b.x + b.w / 2;
    const y2 = b.y;
    const m = (y1 + y2) / 2;
    return [["M", x1, y1], ["C", x1, m, x2, m, x2, y2]];
  }
  const x1 = a.x + a.w;
  const y1 = a.y + a.h / 2;
  const x2 = b.x;
  const y2 = b.y + b.h / 2;
  const m = (x1 + x2) / 2;
  return [["M", x1, y1], ["C", m, y1, m, y2, x2, y2]];
}

// Commands as an SVG path's d: "<cmd> <n> <n> …", single spaces, each number as String(n).
export const pathText = (cmds) => cmds.map((c) => c.join(" ")).join(" ");

export const flowPath = (a, b, orient, cards = []) => pathText(flowCmds(a, b, orient, cards));

// ---- links in a team with lanes (ah2-373 R2-R5)

// How links route when the team has lanes: a trunk's distance before the Lead column and the step between trunks; the step between ports
// on one card edge and between risers sharing a gutter; how far a port keeps from a card's corner; how many steps a port may move to clear.
const ROUTE = { trunk: 12, step: 8, inset: 8, reach: 4 };

// Where a lane's block starts along the columns (flowLayout's backdrops): a trunk runs before it, clear of its label.
const bandStart = (vertical) => (vertical ? FLOW.top - 2 * FLOW.gap : FLOW.left - FLOW.gap / 2);
// The middle of the gap between the i-th drawn column and the next, along the columns: clear of every card.
const gutterOf = (i, vertical) => (vertical ? FLOW.top : FLOW.left) + i * ((vertical ? FLOW.card.h : FLOW.card.w) + FLOW.lane) + (vertical ? FLOW.card.h : FLOW.card.w) + FLOW.lane / 2;

// An orthogonal polyline [[u, w], …] as path commands with rounded corners (AROUND.r, less where a leg is short); `at` maps (u, w) to (x, y).
function roundedCmds(pts, at) {
  const p = pts.filter((q, i) => i === 0 || q[0] !== pts[i - 1][0] || q[1] !== pts[i - 1][1])
    .filter((q, i, all) => i === 0 || i === all.length - 1 || !((all[i - 1][0] === q[0] && q[0] === all[i + 1][0]) || (all[i - 1][1] === q[1] && q[1] === all[i + 1][1])));
  const len = (a, b) => Math.abs(a[0] - b[0]) + Math.abs(a[1] - b[1]);
  const toward = (from, to, d) => [from[0] + Math.sign(to[0] - from[0]) * d, from[1] + Math.sign(to[1] - from[1]) * d];
  const cmds = [["M", ...at(...p[0])]];
  for (let i = 1; i < p.length - 1; i++) {
    const r = Math.min(AROUND.r, len(p[i - 1], p[i]) / 2, len(p[i], p[i + 1]) / 2);
    cmds.push(["L", ...at(...toward(p[i], p[i - 1], r))], ["Q", ...at(...p[i]), ...at(...toward(p[i], p[i + 1], r))]);
  }
  cmds.push(["L", ...at(...p[p.length - 1])]);
  return cmds;
}

// Every link of a team with lanes, Map(pairKey -> commands), so that a line is traceable around the lanes:
// - two cards in one column join by an elbow: in Lead (the Orchestrator and the deputy under it) on a trunk before the column, a nearer
//   card's trunk inside a farther one's, so they nest and never cross; in any later column in the gutter just before it;
// - otherwise straight when its ports can line up clear of the cards, else two right-angle bends through the gap between two columns,
//   the nearest gap to the earlier card that is clear, the ports moved along the card edges if that is what clears it; only when no
//   such route exists, the rail round the cards in the way (four bends);
// - the links on one card edge leave from ports spread along it, and risers sharing a gutter are stepped apart, so no two lines lie on one run.
// A link between two cards of one lane runs at their own ports' heights and in a gap between them, so it stays inside the lane's block.
function laneRoutes(pairs, rects, orient) {
  const vertical = orient === "vertical";
  const ax = (r) => (vertical ? { u: r.y, len: r.h, w: r.x, size: r.w, r } : { u: r.x, len: r.w, w: r.y, size: r.h, r });
  const at = (u, w) => (vertical ? [w, u] : [u, w]);
  const mid = (c) => c.w + c.size / 2;
  const all = [...rects.values()].map(ax);
  const links = [];
  for (const [from, to] of pairs) {
    const [p, q] = [rects.get(from), rects.get(to)];
    if (!p || !q) continue;
    const [A, B] = (vertical ? p.y <= q.y : p.x <= q.x) ? [ax(p), ax(q)] : [ax(q), ax(p)];
    links.push({ key: pairKey(from, to), A, B, same: B.u < A.u + A.len });
  }
  // ports: the ends on one side of one card, ordered so the lines leave without crossing, spread ROUTE.step apart about the middle
  const sides = new Map();
  const side = (c, k) => (sides.has(`${k}`) ? sides.get(`${k}`) : sides.set(`${k}`, { c, ends: [] }).get(`${k}`));
  const keyOf = new Map(all.map((c, i) => [c.r, i]));
  for (const l of links) {
    const d = mid(l.B) - mid(l.A);
    // a trunk's farther card takes the port farther from it, so its elbow runs outside the nearer one's
    side(l.A, `${keyOf.get(l.A.r)}${l.same ? "<" : ">"}`).ends.push({ l, end: "w1", by: l.same ? Math.sign(d) * (1e6 - Math.abs(d)) : mid(l.B) });
    side(l.B, `${keyOf.get(l.B.r)}<`).ends.push({ l, end: "w2", by: mid(l.A) });
  }
  for (const side of sides.values()) {
    const { c, ends } = side;
    // ROUTE.step apart, closer where the edge is crowded
    side.gap = Math.min(ROUTE.step, (c.size - 2 * ROUTE.inset) / Math.max(1, ends.length - 1));
    ends.sort((x, y) => x.by - y.by);
    ends.forEach((e, i) => {
      e.l[`${e.end}Side`] = side;
      e.l[e.end] = Math.round(mid(c) + (i - (ends.length - 1) / 2) * side.gap);
    });
  }
  const clear = (u0, u1, w, c) => !(c.w - AROUND.clear < w && w < c.w + c.size + AROUND.clear && Math.min(u0, u1) < c.u + c.len + AROUND.clear && Math.max(u0, u1) > c.u - AROUND.clear);
  const across = (u, w0, w1, c) => !(c.u - AROUND.clear < u && u < c.u + c.len + AROUND.clear && Math.min(w0, w1) < c.w + c.size + AROUND.clear && Math.max(w0, w1) > c.w - AROUND.clear);
  const portsOf = (c, pref) => {
    const out = [pref];
    for (let d = ROUTE.step; d <= ROUTE.reach * ROUTE.step; d += ROUTE.step) out.push(pref - d, pref + d);
    return out.filter((w) => w >= c.w + ROUTE.inset && w <= c.w + c.size - ROUTE.inset);
  };
  const gutters = COLUMNS.slice(1).map((_, i) => gutterOf(i, vertical));
  // trunks: per lead, its same-column links nearest first, each a step further out
  const trunkRank = new Map();
  for (const l of links.filter((x) => x.same).sort((x, y) => Math.abs(mid(x.B) - mid(x.A)) - Math.abs(mid(y.B) - mid(y.A)))) {
    const u = Math.min(l.A.u, l.B.u);
    // a column after the first turns in the gutter just before it, spread with that gutter's risers; the first, on trunks before it
    const before = gutters.filter((g) => g < u).pop();
    if (before !== undefined) {
      [l.pts, l.risers] = [[[l.A.u, l.w1], [before, l.w1], [before, l.w2], [l.B.u, l.w2]], [1]];
      continue;
    }
    const n = trunkRank.get(l.A.r) ?? 0;
    trunkRank.set(l.A.r, n + 1);
    const t = Math.min(u, bandStart(vertical)) - ROUTE.trunk - ROUTE.step * n;
    l.pts = [[l.A.u, l.w1], [t, l.w1], [t, l.w2], [l.B.u, l.w2]];
    l.trunk = true;
  }
  // a port another link on the same card edge already holds is not free
  const free = (l, end, w) => l[`${end}Side`].ends.every((o) => o.l === l || Math.abs(o.l[o.end] - w) >= Math.floor(l[`${end}Side`].gap));
  const within = (c, w) => w >= c.w + ROUTE.inset && w <= c.w + c.size - ROUTE.inset;
  // a jog shorter than two corners reads as a kink: the ports line up instead, or the line turns properly
  const cost = (l, [w1, w2]) => Math.abs(w1 - l.w1) + Math.abs(w2 - l.w2) + (w1 === w2 ? -1 : Math.abs(w1 - w2) < 2 * AROUND.r ? 1e3 : 0);
  for (const l of links.filter((x) => !x.same)) {
    const { A, B } = l;
    const others = all.filter((c) => c.r !== A.r && c.r !== B.r);
    const [u1, u2] = [A.u + A.len, B.u];
    const combos = [];
    for (const w1 of portsOf(A, l.w1).filter((w) => free(l, "w1", w))) {
      for (const w2 of [w1, ...portsOf(B, l.w2)].filter((w) => within(B, w) && free(l, "w2", w))) combos.push([w1, w2]);
    }
    combos.sort((x, y) => cost(l, x) - cost(l, y));
    const inGap = gutters.filter((g) => g > u1 + AROUND.r && g < u2 - AROUND.r);
    found: for (const [w1, w2] of combos) {
      if (w1 === w2) {
        if (others.every((c) => clear(u1, u2, w1, c))) {
          [l.pts, l.w1, l.w2] = [[[u1, w1], [u2, w2]], w1, w2];
          break;
        }
        continue;
      }
      for (const g of inGap) {
        if (others.every((c) => clear(u1, g, w1, c) && across(g, w1, w2, c) && clear(g, u2, w2, c))) {
          [l.pts, l.w1, l.w2, l.risers] = [[[u1, w1], [g, w1], [g, w2], [u2, w2]], w1, w2, [1]];
          break found;
        }
      }
    }
    // no two-bend route: the rail round the cards in the way, from the link's own ports and turning in the gutters where there are some
    const rail = l.pts ? null : railOf(u1, l.w1, u2, l.w2, others);
    if (rail !== null) {
      const [g1, g2] = [inGap[0] ?? u1 + AROUND.gap, inGap[inGap.length - 1] ?? u2 - AROUND.gap];
      [l.pts, l.risers] = [[[u1, l.w1], [g1, l.w1], [g1, rail], [g2, rail], [g2, l.w2], [u2, l.w2]], inGap.length > 0 ? [1, 3] : []];
    } else if (!l.pts) {
      l.cmds = flowCmds(A.r, B.r, orient, [...rects.values()]);
    }
  }
  // a riser: the leg of link l from pts[i] to pts[i + 1], across the columns
  const span = ({ l, i }) => [Math.min(l.pts[i][1], l.pts[i + 1][1]), Math.max(l.pts[i][1], l.pts[i + 1][1])];
  const move = ({ l, i }, u) => (l.pts[i][0] = l.pts[i + 1][0] = u);
  // trunks that share a run step outwards
  const trunks = links.filter((l) => l.trunk).map((l) => ({ l, i: 1 }));
  trunks.forEach((r, k) => {
    const [lo, hi] = span(r);
    const taken = (u) => trunks.slice(0, k).some((o) => Math.abs(o.l.pts[1][0] - u) < 1 && Math.min(hi, span(o)[1]) - Math.max(lo, span(o)[0]) > 1);
    while (taken(r.l.pts[1][0])) move(r, r.l.pts[1][0] - ROUTE.step);
  });
  // risers in one gutter whose spans overlap spread across it, ordered so they nest rather than cross: of those heading to larger w,
  // the one from the larger w turns first; of those heading back, the one from the smaller
  const byGutter = new Map();
  for (const l of links) for (const i of l.risers ?? []) push(byGutter, l.pts[i][0], { l, i });
  const key = ({ l, i }) => (l.pts[i + 1][1] > l.pts[i][1] ? -l.pts[i][1] : l.pts[i][1] + 1e6);
  for (const list of byGutter.values()) {
    list.sort((x, y) => span(x)[0] - span(y)[0]);
    for (let a = 0; a < list.length;) {
      let b = a + 1;
      for (let hi = span(list[a])[1]; b < list.length && span(list[b])[0] < hi; b++) hi = Math.max(hi, span(list[b])[1]);
      const group = list.slice(a, b).sort((x, y) => key(x) - key(y));
      group.forEach((r, k) => move(r, r.l.pts[r.i][0] + (k - (group.length - 1) / 2) * ROUTE.step));
      a = b;
    }
  }
  return new Map(links.map((l) => [l.key, l.cmds ?? roundedCmds(l.pts, at)]));
}

// ah2-373 R10: while a deputy is on duty (the node flowNodes marks `duty`) it is the team's acting lead, so every link end at
// the Orchestrator is drawn at the deputy; the link between the two stays, the Chief's one connection. Drawing only: the ids the
// server resolved are not changed anywhere else. With no deputy on duty, `edges` and `sparks` as given.
export function onDuty(nodes, edges, sparks) {
  const chief = nodes.find((n) => !n.item && !n.lane && n.role === "orchestrator")?.id;
  const deputy = nodes.find((n) => n.duty === true)?.id;
  if (!chief || !deputy) return { edges, sparks };
  const move = (x) => (x.from === deputy || x.to === deputy ? x : { ...x, from: x.from === chief ? deputy : x.from, to: x.to === chief ? deputy : x.to });
  return { edges: edges.map(move), sparks: sparks.map(move) };
}

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

// Flow's links: one per pair over the reporting lines and the first MAX_SPARKS briefs in flight, kind "review" / "brief" / "edge"; its
// ends ordered the earlier along the columns first; a pair with an end not laid out is dropped. A dot per shown brief; `rest` not shown.
export function flowLinks(edges, sparks, rects, orient) {
  const kinds = sparkKinds(sparks);
  const cards = [...rects.values()];
  const shown = sparks.slice(0, MAX_SPARKS);
  const pairs = [...new Map([...edges.map((e) => [pairKey(e.from, e.to), [e.from, e.to]]), ...shown.map((s) => [pairKey(s.from, s.to), [s.from, s.to]])]).values()];
  // with lanes or a deputy (the cards carry `routed`) every link is routed together, so they can share out ports and gutters; else as before
  const routed = cards.some((c) => c.routed) ? laneRoutes(pairs, rects, orient) : null;
  const cmdsOf = (from, to) => {
    if (routed) return routed.get(pairKey(from, to)) ?? null;
    const a = rects.get(from);
    const b = rects.get(to);
    return a && b ? ((orient === "vertical" ? a.y <= b.y : a.x <= b.x) ? flowCmds(a, b, orient, cards) : flowCmds(b, a, orient, cards)) : null;
  };
  const links = [];
  for (const [from, to] of pairs) {
    const cmds = cmdsOf(from, to);
    if (cmds) {
      links.push({ key: pairKey(from, to), kind: kinds.get(pairKey(from, to)) ?? "edge", cmds });
    }
  }
  const dots = [];
  for (const s of shown) {
    const cmds = cmdsOf(s.from, s.to);
    if (cmds) {
      dots.push({ key: s.id, from: s.from, to: s.to, review: kinds.get(pairKey(s.from, s.to)) === "review", slug: s.slug, cmds });
    }
  }
  return { links, dots, rest: sparks.length - Math.min(sparks.length, MAX_SPARKS) };
}

// ---- what is shown

// Whether a member draws as a card: it is working or blocked, or it is an orchestrator the Overview calls Running.
export const isActive = (m) => m.running === true || m.state === "working" || m.state === "blocked";

export const modelTag = (model) => (typeof model === "string" ? (/opus|sonnet|haiku|fable/i.exec(model)?.[0]?.toLowerCase() ?? model) : null);

// The shown team's nodes: its members (`ids`), then its lanes' members (`children`, laneChildren's), each {id, role, name, lane, team, column}:
// a sub-orchestrator in SUB_COLUMN, a deputy chief and the deputy on duty in Lead, anyone else by columnOf; the deputy on duty carries `duty`.
export function flowNodes(slices, team, ids, children) {
  const lane = laneOf(slices, team, ids);
  const deputy = team?.body.duty?.member;
  const node = (id, laneId, teamOf) => {
    const role = slices.get(id)?.body.role ?? "";
    const base = { id, role, name: slices.get(id)?.body.name ?? id.split("/").pop(), lane: laneId, team: teamOf, column: flowColumn(role, slices.get(id)?.body.column, id === deputy) };
    // ah2-450 G6: a member with a work title, whose card (when working) is FLOW.work taller
    const out = workTitle(slices, teamOf ? slices.get(teamOf) : team, id) ? { ...base, work: true } : base;
    return id === deputy ? { ...out, duty: true } : out;
  };
  return [...ids.map((id) => node(id, lane(id), "")), ...children.map((c) => node(c.id, c.lane, c.team))];
}

// The ids of the nodes that draw as cards; a lane member is read in its own team, and is not active when that team's slice is absent.
export function flowActive(slices, team, nodes) {
  const out = new Set();
  for (const n of nodes) {
    const tm = n.team ? slices.get(n.team) : team;
    if (tm && isActive(memberOf(slices, tm, n.id))) {
      out.add(n.id);
    }
  }
  return out;
}

// One card's data: what is known of the member, a field with no source null. A member of a team not (yet) held reads as gone.
export function flowCardOf(slices, teamId, id) {
  const team = slices.get(teamId);
  const m = team ? memberOf(slices, team, id) : { id, name: id.split("/").pop(), label: id.split("/").pop(), title: id.split("/").pop(), role: "", state: "gone", running: false, tool: null, model: null, slug: null, lastAt: 0, orchestrator: false };
  const items = team?.body.pipeline?.items ?? [];
  const item = m.slug ? items.find((i) => i.slug === m.slug) : null;
  return {
    name: shortName(id, m.label),
    full: m.title,
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
    work: workTitle(slices, team, id),
  };
}

// ah2-450: the slices workTitle's item fallback reads, for teams by slice id: each one's plan (its stream names) and plan-items.
export const workLeaseIds = (slices, teamIds) => [...new Set(teamIds)].flatMap((id) => {
  const t = slices.get(id)?.body;
  return t ? [planId(t), ...planStreams(slices, t).map((s) => itemsId(t, s))] : [];
});

// ah2-450 G2: what a member works on, one line of text: the title of its newest open request (the brief's --title), else the title
// of the plan item it holds, else "". Both from slices the Graphs already lease: the team's open list with its exchanges, plan-items.
export function workTitle(slices, team, id) {
  const t = team?.body;
  if (!t) return "";
  for (const o of slices.get(`list:open:team:${t.dir_id}/${t.name}`)?.body.open ?? []) {
    const b = o.to_member === id ? slices.get(o.id)?.body : null;
    if (b?.title && !b.withdrawn) return inert(b.title).trim();
  }
  const m = slices.get(id)?.body;
  const stream = m ? streamOf(slices, t, m.placement?.stream ?? null) : null;
  return inert(currentItem(slices, t, stream, { id, name: m?.name ?? id.split("/").pop() })?.title ?? "").trim();
}

// ---- jobs and subagents under their owners (ah2-225 B1-B3, B6)

// One team's served rows, for jobsUnder and subagentsUnder: `rows` its jobs: or subagents: slice's rows; `owners` maps an owner name as
// the slice gives it (a member's name, "orchestrator" for the team's Orchestrator) to the key of the row or node it is drawn under;
// `header` is the key a row whose owner is no one here is drawn under (the team's header).
const ownerIn = (src, name) => src.owners.get(name) ?? src.header;
const msOf = (iso) => Date.parse(iso ?? "") || 0;
const LABEL_MAX = 48;
const lineOf = (s) => {
  const t = inert(s).trim();
  return t.length > LABEL_MAX ? `${t.slice(0, LABEL_MAX - 1)}…` : t;
};
const byStart = (out) => {
  for (const list of out.values()) list.sort((a, b) => a.start - b.start || (a.key < b.key ? -1 : 1));
  return out;
};
const push = (out, k, v) => (out.has(k) ? out.get(k).push(v) : out.set(k, [v]));

// The open jobs, as Map(key -> [job]) in start order; a row that carries ended_at is not drawn. A job's clock starts when it holds the
// test slot, else when it was sent; `est` is its eta in seconds; `overdue` is the daemon's served state (JobProjection).
export function jobsUnder(sources) {
  const out = new Map();
  for (const src of sources) {
    for (const r of src.rows ?? []) {
      if (r?.ended_at || typeof r?.id !== "string") continue;
      const queued = r.slot_phase === "queued";
      const start = !queued && r.slot_phase && r.slot_since ? msOf(r.slot_since) : msOf(r.sent_at);
      push(out, ownerIn(src, r.owner), { key: `job:${r.id}`, kind: "job", jobKind: r.kind ?? "", owner: ownerIn(src, r.owner), ownerName: inert(r.owner ?? ""),
        label: lineOf(r.label) || r.kind || "job", start, est: Number(r.threshold_s) || 0, queued, overdue: r.state === "overdue" });
    }
  }
  return byStart(out);
}

// The running subagents, as Map(key -> [subagent]) in start order; a run older than its ttl_s is lapsed and not drawn. One run is drawn
// once (Amendment 2): a Sub-Orchestrator's run is in its parent team's slice and its lane's, and the lane's copy (owner_kind
// orchestrator) wins. `role` is the agent type's role (after any "plugin:"), the generic icon when it is no role.
export function subagentsUnder(sources, now) {
  const runs = new Map();
  for (const src of sources) {
    for (const r of src.rows ?? []) {
      if (typeof r?.agent_id !== "string") continue;
      const start = msOf(r.started_at);
      if (Number(r.ttl_s) > 0 && now - start > r.ttl_s * 1000) continue;
      const had = runs.get(r.agent_id);
      if (had && !(r.owner_kind === "orchestrator" && had.kind !== "orchestrator")) continue;
      const type = inert(r.agent_type ?? "") || "subagent";
      runs.set(r.agent_id, { kind: r.owner_kind, item: { key: `subagent:${r.agent_id}`, kind: "subagent", owner: ownerIn(src, r.owner), ownerName: inert(r.owner ?? ""),
        type, role: type.slice(type.lastIndexOf(":") + 1), label: type, start, end: Number(r.ttl_s) > 0 ? start + r.ttl_s * 1000 : Infinity } });
    }
  }
  const out = new Map();
  for (const { item } of runs.values()) push(out, item.owner, item);
  return byStart(out);
}

// Amendment 3: the jobs: slice is keyed by the team's progress folder (HierarchyModule.ProgressFolderOf), so the default team's is
// _default; subagents: takes the team's name. `team` is "<dirId>/<team name>".
const progressFolder = (name) => (!name || name === "default" || name === "null" ? "_default" : name);
export function activityId(kind, team) {
  const at = team.indexOf("/");
  return kind === "jobs" ? `jobs:${team.slice(0, at)}/${progressFolder(team.slice(at + 1))}` : `${kind}:${team}`;
}

// The jobs: and subagents: slice ids (`kinds`) of a team and of its lanes drawn with it (`children`, laneChildren's).
export function activityIds(team, children = [], kinds = ["jobs", "subagents"]) {
  const t = team?.body;
  if (!t) return [];
  return [`${t.dir_id}/${t.name}`, ...new Set(children.map((c) => c.team.slice("team:".length)))].flatMap((k) => kinds.map((kind) => activityId(kind, k)));
}

// A Graph's sources (`kind` "jobs" or "subagents"): the team's members by name, the Orchestrator as "orchestrator", a stray row under
// the team (key ""); then each lane drawn with it, whose Orchestrator, and any stray row, is its leader's node.
export function graphSources(slices, team, ids, children, kind) {
  const t = team?.body;
  if (!t) return [];
  const nameOf = (id) => slices.get(id)?.body.name ?? id.split("/").pop();
  const rows = (k) => slices.get(activityId(kind, k))?.body.rows ?? [];
  const own = new Map(ids.map((id) => [nameOf(id), id]));
  if (t.orchestrator) own.set("orchestrator", t.orchestrator);
  const out = [{ rows: rows(`${t.dir_id}/${t.name}`), owners: own, header: "" }];
  const lanes = new Map();
  for (const c of children) {
    if (!lanes.has(c.team)) lanes.set(c.team, { rows: rows(c.team.slice("team:".length)), owners: new Map([["orchestrator", c.lane]]), header: c.lane });
    lanes.get(c.team).owners.set(nameOf(c.id), c.id);
  }
  return [...out, ...lanes.values()];
}

// When the first drawn subagent lapses (Infinity when none will): a surface draws again then, on its age tick (Amendment 3).
export const lapseAt = (under) => Math.min(Infinity, ...[...under.values()].flat().map((x) => x.end ?? Infinity));

// Both kinds under one key, in start order: what a surface draws under a row.
export function underOf(jobs, subagents) {
  const out = new Map();
  for (const m of [subagents, jobs]) for (const [k, list] of m) for (const x of list) push(out, k, x);
  return byStart(out);
}

// A full test run: kind test or slot. A bg job's served label cannot tell a full run apart, so it is not one (B2).
export const isFullRun = (x) => x.kind === "job" && (x.jobKind === "test" || x.jobKind === "slot");

const minutes = (ms) => {
  const m = Math.max(1, Math.round(ms / 60_000));
  return m < 120 ? `${m}m` : `${Math.round(m / 60)}h`;
};
// "8m of ~15m", "waiting for the test slot", or the age of a subagent's run.
export function runText(x, now) {
  if (x.kind === "subagent") return minutes(now - x.start);
  if (x.queued) return "waiting for the test slot";
  return x.est > 0 ? `${minutes(now - x.start)} of ~${minutes(x.est * 1000)}` : minutes(now - x.start);
}
// A row's tooltip and accessible name. `parent` is the drawn name of the row it sits under, or null under a team header.
export function underTip(x, parent, now) {
  if (x.kind === "subagent") return `${x.type}: subagent of ${parent ?? (x.ownerName || "the team")}, running ${minutes(now - x.start)}`;
  return `job ${x.label}${parent ? ` of ${parent}` : x.ownerName ? ` of ${x.ownerName}` : ""} · ${runText(x, now)}${x.overdue ? " · overdue" : ""}`;
}

// Flow's nodes for the jobs and subagents (`under`, underOf's): a full test run is a card in the Test column with its owner named;
// any other is a row right under its owner, in the owner's column and band (`after` the owner's id). One whose key is no node here
// (a team header's) sits at the foot of the Lead column. {id, role, name, lane, team, column, after, item}.
export function flowExtras(nodes, under) {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const out = [];
  for (const [k, list] of under) {
    const o = byId.get(k);
    for (const x of list) {
      const test = isFullRun(x);
      out.push({ id: x.key, role: x.kind === "subagent" ? x.role : "", name: x.key, lane: o?.lane ?? "", team: o?.team ?? "", column: test ? TEST_COLUMN : o ? o.column : 0,
        after: o && !test ? o.id : null, item: x });
    }
  }
  return out;
}

// The dashed connector from each subagent's owner to it (B3), as path commands: down the owner's near edge, then in to the row.
export function underLinks(nodes, rects, orient) {
  const out = [];
  for (const n of nodes) {
    const a = n.after && n.item?.kind === "subagent" ? rects.get(n.after) : null;
    const b = rects.get(n.id);
    if (!a || !b) continue;
    out.push({ key: `under:${n.id}`, kind: "subagent", cmds: orient === "vertical"
      ? [["M", a.x + a.w, a.y + 10], ["L", b.x + b.w / 2, a.y + 10], ["L", b.x + b.w / 2, b.y]]
      : [["M", a.x + 10, a.y + a.h], ["L", a.x + 10, b.y + b.h / 2], ["L", b.x, b.y + b.h / 2]] });
  }
  return out;
}

// ---- the view that fits

export const ZOOM = { min: 0.25, max: 4, fitMax: 1.4, step: 1.25, pan: 60 };
// ah2-450 G4: below this zoom a card's work line (11 px text) is under 7 px, too small to read, and is hidden.
export const WORK_MIN_K = 0.65;
// ah2-456 N1: whether a node's card draws its work line, and so is FLOW.work taller: it is working, it has a title, and zoom k shows it.
export const drawsWork = (n, active, k = 1) => active.has(n.id) && Boolean(n.work) && k >= WORK_MIN_K;
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const round = (n) => Math.round(n * 10) / 10;

// The view {k, x, y} (scale, then offset in stage pixels) that centres the box in a w×h stage at the largest scale that fits,
// up to ZOOM.fitMax so a small team is not blown up.
export function fitView(box, w, h, pad = 24) {
  if (!box) {
    return { k: 1, x: 0, y: 0 };
  }
  const k = clamp(Math.min((w - 2 * pad) / box.w, (h - 2 * pad) / box.h), ZOOM.min, ZOOM.fitMax);
  return { k: round(k * 1000) / 1000, x: round(w / 2 - k * (box.x + box.w / 2)), y: round(h / 2 - k * (box.y + box.h / 2)) };
}
