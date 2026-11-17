// Flow's maths, shared by the web's Flow (graph.js) and the Herdr mod's (herdr-desktop/mods/ah2/graph-model.js): the columns by role,
// where each card goes, the links between them as path commands, and what each card shows. Pure: no DOM. The mod holds a byte-identical
// copy (tests/web/herdr-graph-model.test.mjs), so edit this file and copy it.
import { laneOf, memberOf } from "./select.js";
import { MAX_SPARKS, shortName } from "./graph-world.js";

// ---- the columns by role, and where each card goes

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
  const cmdsOf = (from, to) => {
    const a = rects.get(from);
    const b = rects.get(to);
    return a && b ? ((orient === "vertical" ? a.y <= b.y : a.x <= b.x) ? flowCmds(a, b, orient, cards) : flowCmds(b, a, orient, cards)) : null;
  };
  const shown = sparks.slice(0, MAX_SPARKS);
  const pairs = [...new Map([...edges.map((e) => [pairKey(e.from, e.to), [e.from, e.to]]), ...shown.map((s) => [pairKey(s.from, s.to), [s.from, s.to]])]).values()];
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

// The shown team's nodes: its members (`ids`), then its lanes' members (`children`, laneChildren's), each {id, role, name, lane, team, column}.
export function flowNodes(slices, team, ids, children) {
  const lane = laneOf(slices, team, ids);
  const node = (id, laneId, teamOf) => ({ id, role: slices.get(id)?.body.role ?? "", name: slices.get(id)?.body.name ?? id.split("/").pop(), lane: laneId, team: teamOf, column: slices.get(id)?.body.column });
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
  const m = team ? memberOf(slices, team, id) : { id, name: id.split("/").pop(), role: "", state: "gone", running: false, tool: null, model: null, slug: null, lastAt: 0, orchestrator: false };
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

// ---- the view that fits

export const ZOOM = { min: 0.25, max: 4, fitMax: 1.4, step: 1.25, pan: 60 };
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
