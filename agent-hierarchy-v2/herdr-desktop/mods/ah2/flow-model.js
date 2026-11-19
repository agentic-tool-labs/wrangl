// Flow's maths, shared by the web's Flow (graph.js) and the Herdr mod's (herdr-desktop/mods/ah2/graph-model.js): the columns by role,
// where each card goes, the links between them as path commands, and what each card shows. Pure: no DOM. The mod holds a byte-identical
// copy (tests/web/herdr-graph-model.test.mjs), so edit this file and copy it. It also holds the jobs and subagents under their owners
// (ah2-225 B6), which the Herdr sidebar's tree, both Graphs and the web Overview draw.
import { inert, laneOf, memberOf } from "./select.js";
import { MAX_SPARKS, shortName } from "./graph-world.js";

// ---- the columns by role, and where each card goes

export const COLUMNS = ["Lead", "Advise · design", "Implement", "Review", "Test"];
// ah2-225 B2: QA members and full test runs. The daemon serves a QA member Review's column; Flow moves it here.
export const TEST_COLUMN = 4;
const columnOf = (role, served) => (role === "qa" ? TEST_COLUMN : served);
// A job or subagent drawn under its owner sits this far in from the owner's edge, across the column.
export const UNDER_INDENT = 20;

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
  // a full test run in the Test column is a card; any other job or subagent a row (flowExtras)
  const size = (n) => (n.item ? (n.after ? FLOW.row : n.item.kind === "job" && n.column === TEST_COLUMN ? FLOW.card : FLOW.row) : active.has(n.id) ? FLOW.card : FLOW.row);
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
    band.forEach((n) => !n.after && (cols[n.column] ?? cols[2]).push(n));
    // a job or subagent under its owner goes right after the owner and the owner's earlier ones
    band.forEach((n) => {
      if (!n.after) return;
      const col = cols.find((c) => c.some((m) => m.id === n.after)) ?? cols[n.column] ?? cols[2];
      const at = col.reduce((last, m, i) => (m.id === n.after || m.after === n.after ? i : last), -1);
      at < 0 ? col.push(n) : col.splice(at + 1, 0, n);
    });
    const spans = cols.map((c) => c.reduce((sum, n) => sum + along(n) + FLOW.gap, -FLOW.gap));
    const tallest = Math.max(0, ...spans);
    cols.forEach((c, ci) => {
      let at = (ci === 0 ? (tallest - Math.max(0, spans[ci])) / 2 : 0) + FLOW.top + offset;
      const base = FLOW.left + ci * step;
      c.forEach((n) => {
        const { w, h } = size(n);
        const inset = n.after ? UNDER_INDENT : 0;
        pos.set(n.id, horizontal ? { x: base + inset, y: at, w, h } : { x: FLOW.left + (at - FLOW.top), y: FLOW.top + ci * step + inset, w, h });
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
  const node = (id, laneId, teamOf) => ({ id, role: slices.get(id)?.body.role ?? "", name: slices.get(id)?.body.name ?? id.split("/").pop(), lane: laneId, team: teamOf, column: columnOf(slices.get(id)?.body.role, slices.get(id)?.body.column) });
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
  };
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
