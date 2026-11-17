// The Graph window's data side (props {view:"graph"}): one team as the web's Map draws it, packed circles with bundled message
// arcs, and its links as rows. No herdr or gpui imports, so the fixture render (preview/render.mjs) runs the same code. The reads
// are the web's own: select.js (members, briefs in flight) and graph-world.js (the tree, the flow head's whole-run totals, short
// names, arc widths), both byte-identical copies of web/ui's, kept honest by a test; graph-layout.js is d3's pack and curveBundle.
import "./intl-shim.js"; // before graph-world.js, which needs Intl when it loads
import { laneChildren, memberIds, memberOf, sparksOf } from "./select.js";
import { LABEL_AT, MAX_SPARKS, STEP_MS, TIERS, WINDOWS, airOf, eventsOf, fadeOf, fitPack, flowId, hhmm, neededPages, nowOf, pageId, pagesOf, pairsOfTotals, planId, readout, scrubOf, scrubbed,
  shortName, sideways, streamOf, treeOf, usesTotals, widthOf } from "./graph-world.js";
import { hierarchy, pack, bundle } from "./graph-layout.js";
import { fitView as fitBox, flowActive, flowCardOf, flowLayout, flowLinks, flowNodes } from "./flow-model.js";
import { STATES, sidebarModel, memberPane } from "./model.js";

// The web Map's label tiers, label radius, spark cap, air, fit and arc offset are graph-world.js's TIERS, LABEL_AT, MAX_SPARKS,
// airOf, fitPack and sideways, which both draw with.
export const MAP_PX = 540; // the map's square in the Herdr window

// Magnification (the Graph's + / − / fit, and the wheel): the map is laid out at its frame's size × graphScale and drawn
// inside that frame, offset by its pan (graphPan[group id], {x, y} px; absent is centred). Scale 1 fills the frame: that is fit.
export const SCALE_MIN = 0.25;
export const SCALE_MAX = 4;
export const SCALE_STEP = 1.25;
export const clampScale = (s) => Math.min(SCALE_MAX, Math.max(SCALE_MIN, s));
// The box a map is drawn in: its frame (the square at scale 1), and zoomed in (s > 1) the room the view has for it (ui.graphRoom,
// {w, h} px, measured by the view; less `pad`, its lane's indent) when that is larger, so a zoomed map fills the view, not the square.
export function frameOf(frame, s, room, pad = 0) {
  if (s <= 1 || !room) return { w: frame, h: frame };
  return { w: Math.max(frame, room.w - pad), h: Math.max(frame, room.h) };
}
// A pan that keeps the map on its box: a map larger than the box cannot leave a gap at an edge, a smaller one sits centred.
export function clampPan(pan, frame, s, box = { w: frame, h: frame }) {
  const size = frame * s;
  const axis = (p, room) => (size <= room ? (room - size) / 2 : Math.min(0, Math.max(room - size, p ?? (room - size) / 2)));
  return { x: axis(pan?.x, box.w), y: axis(pan?.y, box.h) };
}
// The pan that keeps the map point under (px, py) (box px) in place when the scale goes from s to s2 and the box from b1 to b2;
// a box sits centred across its row and at its top, so a wider one moves the point right by half the growth.
export function panAround(pan, frame, s, s2, px, py, b1 = { w: frame, h: frame }, b2 = b1) {
  const p = clampPan(pan, frame, s, b1);
  const qx = px + (b2.w - b1.w) / 2;
  return clampPan({ x: qx - ((px - p.x) * s2) / s, y: py - ((py - p.y) * s2) / s }, frame, s2, b2);
}
export function fitView(ui) {
  ui.graphScale = 1;
  ui.graphPan = {};
  ui.graphEase = null; // a drill, a mode switch or a new team ends any eased zoom (graph-view.js easeTo)
  ui.graphDrag = null; // and any drag, whose stage may be gone (graph-view.js grip)
}

// A wheel event as the Graph reads it, Map and Flow alike (spec herdr-graph-gestures D1, D2): every scroll zooms, a mouse wheel's
// line delta and a trackpad's two-finger pixel delta alike, with no key; it never pans (a drag pans: dragAct). The factors are the
// web's (web/ui/graph.js onWheel); a positive delta.y is a scroll up here (gpui-kit), so up zooms in, in whatever direction the
// system's natural-scrolling setting delivers it. A horizontal delta is ignored. gpui-kit ScrollWheelEvent: delta in px, delta_lines
// when the device sent lines.
export function wheelAct(e) {
  const lines = e.delta_lines?.y;
  if (lines) return { zoom: Math.exp(lines * 0.05) };
  const y = e.delta?.y ?? 0;
  return y ? { zoom: Math.exp(y * 0.002) } : null;
}

// Click and drag pans (spec herdr-graph-gestures D3, D5). d: the press {x0, y0, x, y, dragged} in window px, (x, y) the point last
// applied; at: the pointer now; pan: the drawn pan; clamp: the stage's pan rule (clampPan, flowPan). Under DRAG_PX from the press it
// is still a click: no pan. From there on it is a drag for good, and the pan moves by the pointer's travel since (x, y), clamped.
export const DRAG_PX = 4;
export function dragAct(d, at, pan, clamp) {
  const dragged = d.dragged || Math.hypot(at.x - d.x0, at.y - d.y0) >= DRAG_PX;
  return { dragged, pan: dragged ? clamp({ x: pan.x + at.x - d.x, y: pan.y + at.y - d.y }) : null };
}
// Whether a click is the release of a drag, so it must not click (D5): a drag still held, or one ended (d.end, ms) under CLICK_MS
// ago, since herdr does not promise the order of a button's click and the stage's release. A key press later clicks again.
// ponytail: a time window, not an ordering; a keyboard click within CLICK_MS of a drag's end is dropped.
export const CLICK_MS = 250;
export const wasDrag = (d, now) => Boolean(d?.dragged && (d.live || now - d.end < CLICK_MS));

// The eased zoom: the web's own stage transition, --spring-ms and --spring (web/ui/r6.css:713, on .world at :718).
export const EASE_MS = 400;
// cubic-bezier(.2, 0, 0, 1) at time fraction x: x(u) = 0.6u(1-u)² + u³ solved by bisection, then y(u) = 3u² - 2u³, which stays in 0..1
// (no overshoot).
export function spring(x) {
  if (x <= 0) return 0;
  if (x >= 1) return 1;
  let lo = 0;
  let hi = 1;
  for (let i = 0; i < 24; i++) {
    const u = (lo + hi) / 2;
    if (0.6 * u * (1 - u) ** 2 + u ** 3 < x) lo = u;
    else hi = u;
  }
  const u = (lo + hi) / 2;
  return u * u * (3 - 2 * u);
}
// A wheel's zoom (spec herdr-graph-wheel-follow D1, D2): short, and eased out only, so it is fastest at the event and settles into
// the target. A wheel event restarts it from the drawn scale (easeTo), so a turning wheel never re-enters a slow start.
export const WHEEL_EASE_MS = 120;
export const cubicOut = (x) => (x <= 0 ? 0 : x >= 1 ? 1 : 1 - (1 - x) ** 3);
// The drawn scale at `now` of an eased zoom from `from` to `to` begun at t0: exactly `to` once its time has passed, never past it.
// A button or a drill eases on the web's curve over EASE_MS (D5); a wheel (wheel true) on cubicOut over WHEEL_EASE_MS.
export function easedScale(from, to, t0, now, wheel = false) {
  const x = (now - t0) / (wheel ? WHEEL_EASE_MS : EASE_MS);
  return x >= 1 ? to : from + (to - from) * (wheel ? cubicOut : spring)(x);
}
// A drill in or out (a stream circle, the crumb, Esc, a team tab) starts the map back at fit.
export function drillTo(ui, z) {
  ui.graphZoom = z;
  fitView(ui);
}
// Scale every shown map to s2 (clamped), each about its box's centre, or the one `at` names ({id, x, y}, box px) about that
// point. groups: graphModel's, laid out at the old scale. False when the clamp leaves the scale where it was. `at` moves with its box
// (a box sits centred, so a wider one shifts the point by half the growth), so a run of rescales stays about one point.
export const LANE_PAD = 14; // a lane's indent per depth (graph-view.js group())
export function rescaleTo(ui, groups, s2, at = null) {
  const s = ui.graphScale ?? 1;
  const to = clampScale(s2);
  if (to === s) return false;
  const pan = {};
  for (const g of groups ?? []) {
    if (!g.map) continue;
    const f = g.map.size / s;
    const pad = (g.depth ?? 0) * LANE_PAD;
    const b1 = frameOf(f, s, ui.graphRoom, pad);
    const [px, py] = at?.id === g.id ? [at.x, at.y] : [b1.w / 2, b1.h / 2];
    const b2 = frameOf(f, to, ui.graphRoom, pad);
    pan[g.id] = panAround(ui.graphPan?.[g.id], f, s, to, px, py, b1, b2);
    if (at?.id === g.id) at.x += (b2.w - b1.w) / 2;
  }
  ui.graphScale = to;
  ui.graphPan = pan;
  return true;
}

// Every running team, lanes included, each with its depth under the top-level team it nests in.
function flat(teams, depth = 0, out = []) {
  for (const t of teams) {
    out.push({ t, depth });
    flat(t.lanes, depth + 1, out);
  }
  return out;
}

const teamSlice = (slices, id) => slices.get(`team:${id}`);
const openIds = (slices, id) => (slices.get(`list:open:team:${id}`)?.body.open ?? []).map((o) => o.id);

// The window's extra subscriptions, for every running team at once, so a team tab switches with no wait on the long-poll:
// the team (its members), its members, the flow head (the messages per pair), the topology (which briefs are reviews), the
// open list and its exchanges (briefs in flight), and the flow pages the scrubber's window needs where it stands (`ui`: the
// view's graphWindow and graphCursor; none at now over the run). Absent slices are skipped, so the set grows as they arrive.
// A page already held stays subscribed: any change to the set restarts the poll from 0 (main.js fetch), so scrubbing or the
// clock moving off a page must not drop it; the set grows only when the scrubber first needs a page not yet fetched.
export function graphSubs(slices, ui = {}, clock = Date.now()) {
  const ids = [];
  for (const { t } of flat(sidebarModel(slices, 0).teams)) {
    const k = t.id;
    const mids = memberIds(teamSlice(slices, k));
    // the plan names the streams (the Map's circles); a member's activity slice holds its busy share (the ring)
    const acts = mids.map((id) => slices.get(id)?.body.activity).filter(Boolean);
    ids.push(`team:${k}`, `flow:${k}`, `topology:${k}`, `plan:${k}`, `list:open:team:${k}`, ...mids, ...acts, ...openIds(slices, k));
    const head = slices.get(`flow:${k}`)?.body;
    const tb = teamSlice(slices, k)?.body;
    if (head?.run && tb) {
      const need = new Set(neededPages(head, scrubOf(ui, head, clock)));
      (head.pages ?? []).forEach((_, p) => {
        const pid = pageId(tb, head.run, p);
        if (need.has(p) || slices.has(pid)) ids.push(pid);
      });
    }
  }
  return [...new Set(ids)];
}

// One team's members and links. The links merge the messages in the scrubber's window (the flow head's ordered totals at now
// over the run) into one row per pair, with the briefs in flight between them (at now only). `pending` while the team slice
// has not arrived. o: { ui, clock, zoom }, zoom the id of the circle this team's map is zoomed into, or null.
function group(slices, t, depth, size, o = {}) {
  const team = teamSlice(slices, t.id);
  if (!team) return { id: t.id, name: t.name, depth, pending: true };
  const served = slices.get(`team-summary:${t.id}`)?.body.member_states ?? {};
  const members = memberIds(team).map((id) => {
    const m = memberOf(slices, team, id);
    const state = m.running ? "running" : STATES[m.state] ? m.state : "unknown";
    const name = shortName(id, m.name ?? undefined);
    const stream = streamOf(slices, team.body, slices.get(id)?.body.placement?.stream ?? null);
    const ctx = m.state === "gone" ? null : m.ctx?.text ?? null;
    // a role that only repeats the name says nothing; a lane orchestrator's "sub-orchestrator" does
    return { id, name, role: m.role === name ? "" : m.role, state, ...STATES[state], item: served[id]?.item ?? "", stream, share: m.share, ctx, model: m.model ?? "", pane: memberPane(id, served[id]) };
  });

  const head = slices.get(flowId(team.body))?.body ?? null;
  const names = head?.names ?? [];
  const sc = head ? scrubOf(o.ui ?? {}, head, o.clock ?? Date.now()) : null;
  const live = !sc || sc.live;
  const windowed = Boolean(sc) && !usesTotals(sc);
  const view = sc ? scrubbed(head, pagesOf(slices, team.body, head), sc) : { pairs: [], events: null, count: 0 };
  const by = new Map();
  const link = (a, b) => {
    const [x, y] = a < b ? [a, b] : [b, a];
    const k = `${x}|${y}`;
    if (!by.has(k)) by.set(k, { key: k, x, y, sent: { [x]: [0, 0], [y]: [0, 0] }, flight: [] });
    return by.get(k);
  };
  for (const p of view.pairs) {
    const a = names[p.a];
    const b = names[p.b];
    if (a && b && a !== b) link(a, b).sent[a][p.k] += p.n;
  }
  const sparks = live ? sparksOf(slices, team) : [];
  for (const s of sparks) link(s.from, s.to).flight.push(s);
  const overflow = windowed ? 0 : (head?.totals ?? []).filter(([a]) => a < 0).reduce((n, r) => n + r[2] + r[3], 0);
  const map = mapOf(slices, team, t.id, members, head, size, { pairs: view.pairs, sc, sparks, zoom: o.zoom ?? null });
  // zoomed into a circle, only the links between the members inside it
  const inside = o.zoom && map ? new Set(map.leaves.map((n) => n.id)) : null;

  const short = (id) => shortName(id);
  const links = [...by.values()].filter((l) => !inside || (inside.has(l.x) && inside.has(l.y))).map((l) => {
    const nx = l.sent[l.x][0] + l.sent[l.x][1];
    const ny = l.sent[l.y][0] + l.sent[l.y][1];
    // The busier sender reads first; a one-way pair reads "→", both ways "⇄", a pair with only a brief in flight "→" from its sender.
    const [a, b] = ny > nx || (nx === 0 && ny === 0 && l.flight[0]?.from === l.y) ? [l.y, l.x] : [l.x, l.y];
    const both = nx > 0 && ny > 0;
    const say = (from, to) => {
      const [briefs, reports] = l.sent[from];
      if (briefs + reports === 0) return null;
      return `${short(from)} → ${short(to)}: ${briefs} ${briefs === 1 ? "brief" : "briefs"}, ${reports} ${reports === 1 ? "report" : "reports"}`;
    };
    const review = l.flight.some((s) => s.review);
    const flight = l.flight.length;
    const inFlight = flight ? `${flight} in flight${review ? ", review" : ""}` : "";
    const title = [say(a, b), say(b, a), ...l.flight.map((s) => `in flight: ${s.slug} (${short(s.from)} → ${short(s.to)})`)].filter(Boolean).join("\n");
    return { key: l.key, a: short(a), b: short(b), arrow: both ? "⇄" : "→", n: nx + ny, flight, review, inFlight, title };
  }).sort((p, q) => q.flight - p.flight || q.n - p.n || (p.a + p.b < q.a + q.b ? -1 : 1));

  return { id: t.id, name: t.name, depth, members, links, max: Math.max(1, ...links.map((l) => l.n)), overflow, run: head ? head.run !== null : true,
    windowed, head, events: head ? eventsOf(head, pagesOf(slices, team.body, head)) : [], count: view.count, map };
}

// The Map as graph-pack.js lays it out (layout, pathOf, drawWorld) at `size` px: the tree packed with every member at radius 1,
// fitted to the square and transposed; one arc per pair and kind, bundled through the circles the two sit in, briefs and reports
// side by side; a mark per brief in flight where the web runs its spark.
// o: { pairs, sc, sparks, zoom }. pairs: the scrubber's pairs (the run's totals when absent); an arc of the run with no message in
// the window stays, faded to FAINT, and one in it fades with its newest message as the web's does (fadeOf; null: full). The square
// fits the focus: the circle zoomed into (`zoom`, a stream's id), else the team's only circle when it has one, else the team; arcs
// and marks reach only members inside it. Label tiers follow the fitted radius.
export const FAINT = 0.12;
export function mapOf(slices, team, id, members, head, size = MAP_PX, o = {}) {
  if (members.length === 0) return null;
  const root = pack(hierarchy(treeOf(id, team.body.name, members)), () => 1, airOf);
  const only = root.children?.length === 1 && root.children[0].children ? root.children[0] : root;
  const top = (o.zoom && root.descendants().find((n) => n.data.id === o.zoom && n.children)) || only;
  fitPack(top, size);
  const all = top.descendants();
  const byId = new Map(all.map((n) => [n.data.id, n]));
  const path = (a, b, kind) => bundle(sideways(a.path(b), a, b, kind, size), 0.85);

  const names = head?.names ?? [];
  // a name with no member here (it left the team) ends at the focus's centre, as the web's does at the root; zoomed, it is not drawn
  const nodeAt = (i) => byId.get(names[i]) ?? (top === root ? root : null);
  const merge = (list) => {
    const both = new Map();
    for (const p of list) {
      const [a, b] = p.a < p.b ? [p.a, p.b] : [p.b, p.a];
      const key = `${a}|${b}|${p.k}`;
      const q = both.get(key);
      if (q) [q.n, q.last] = [q.n + p.n, Math.max(q.last ?? -Infinity, p.last ?? -Infinity)];
      else both.set(key, { ...p, a, b });
    }
    return both;
  };
  const run = merge(pairsOfTotals(head ?? {}));
  const shown = o.pairs ? merge(o.pairs) : run;
  const arcs = [];
  for (const [key, p] of new Map([...run, ...shown])) {
    const a = nodeAt(p.a);
    const b = nodeAt(p.b);
    if (!a || !b || a === b) continue;
    const q = shown.get(key);
    const fade = !q ? FAINT : o.sc ? fadeOf(o.sc.cursor, q.last, o.sc.win) : null;
    arcs.push({ key, kind: p.k, width: widthOf(q?.n ?? p.n), fade, cmds: path(a, b, p.k) });
  }
  const flights = [];
  for (const s of (o.sparks ?? sparksOf(slices, team)).slice(0, MAX_SPARKS)) {
    const a = byId.get(s.from);
    const b = byId.get(s.to);
    if (!a || !b || a === b) continue;
    const c = path(a, b, 0);
    const at = c[Math.floor(c.length / 2)];
    flights.push({ key: s.id, x: at.at(-2), y: at.at(-1), title: `in flight: ${s.slug} (${shortName(s.from)} → ${shortName(s.to)})${s.review ? ", review" : ""}` });
  }

  // the focus's own circle draws too when it is a stream (it fills the square); a stream inside it can be zoomed into
  const containers = all.filter((n) => n.data.type === "stream").map((n) => {
    const count = n.leaves().length;
    return { id: n.data.id, type: n.data.type, name: n.data.name, x: n.x, y: n.y, r: n.r, label: n.r >= LABEL_AT[n.data.type], zoom: n !== top,
      title: `${n.data.type} ${n.data.name}, ${count} ${count === 1 ? "member" : "members"}` };
  });
  const leaves = top.leaves().filter((n) => n.data.type === "member")
    .map((n) => {
      const m = n.data.m;
      // the ring's share in words too, so it never rests on the drawing alone
      const busy = typeof m.share === "number" ? `busy ${Math.round(m.share * 100)}% of the last hour` : "";
      return { ...m, full: [m.name, m.role, m.word, m.item, busy].filter(Boolean).join(" · "), x: n.x, y: n.y, r: n.r, tier: TIERS.filter((v) => n.r >= v).length };
    });
  return { size, containers, arcs, flights, leaves, focus: top === root ? null : { id: top.data.id, name: top.data.name, zoomed: top !== only } };
}

// The scrubber under the maps, over every shown team's flow: the span from the oldest run start to now in whole minutes, the
// messages per bar (a minute, or more when the run is longer than BARS minutes), which bars the window covers, and the slider's
// knob as a share of the span in 0..KNOB. Null when no shown team has a flow head yet.
export const BARS = 60;
export const KNOB = 1000;
export function scrubberOf(groups, ui = {}, clock = Date.now()) {
  const heads = groups.filter((g) => g.head);
  if (heads.length === 0) return null;
  const max = Math.max(...heads.map((g) => nowOf(g.head, clock)));
  const from = Math.min(...heads.map((g) => (g.head.truncated && Number.isFinite(g.head.oldest_ms) ? g.head.oldest_ms : g.head.start_ms)));
  const steps = Math.max(1, Math.ceil((max - from) / STEP_MS));
  const min = max - steps * STEP_MS;
  const live = ui.graphCursor == null || ui.graphCursor >= max;
  const cursorMs = live ? max : Math.max(min, ui.graphCursor);
  const win = WINDOWS.find((w) => w.id === ui.graphWindow) ?? WINDOWS[0];
  const per = Math.ceil(steps / BARS); // minutes per bar
  const bar = per * STEP_MS;
  const counts = new Array(Math.ceil(steps / per)).fill(0);
  const put = (ms, n) => {
    const i = Math.floor((ms - min) / bar);
    if (i >= 0 && i < counts.length) counts[i] += n;
  };
  for (const g of heads) {
    const h = g.head;
    for (const e of g.events) put(h.start_ms + e[0] * 1000, 1);
    // ponytail: a page the window does not need is not loaded, so its page_size messages are spread evenly from its first
    // event to the next page's; subscribe every page if the bars must be exact far from the cursor.
    const starts = h.pages ?? [];
    const held = new Set(g.events.map((e) => e[0]));
    starts.forEach((t0, k) => {
      if (held.has(t0)) return;
      const t1 = k + 1 < starts.length ? starts[k + 1] : h.tail?.[0]?.[0] ?? t0;
      const n = Math.max(1, Math.ceil(((t1 - t0) * 1000) / bar));
      for (let j = 0; j < n; j++) put(h.start_ms + t0 * 1000 + (j * (t1 - t0) * 1000) / n, h.page_size / n);
    });
  }
  const lo = win.s === null ? -Infinity : cursorMs - win.s * 1000;
  const bars = counts.map((n, i) => ({ n: Math.round(n), in: min + (i + 1) * bar > lo && min + i * bar < cursorMs }));
  const count = groups.reduce((n, g) => n + (g.count ?? 0), 0);
  const head = heads[0].head;
  const span = max - min;
  return { min, max, steps, live, cursorMs, win: win.id, wins: WINDOWS.map((w) => ({ id: w.id, label: w.label })), bars, per,
    peak: Math.max(1, ...bars.map((b) => b.n)), knob: Math.round((KNOB * (cursorMs - min)) / span),
    from: hhmm(min), to: hhmm(max), say: readout(head, cursorMs, count, live), at: live ? "now" : hhmm(cursorMs),
    range: win.s === null ? null : `${hhmm(Math.max(min, lo))}–${live ? "now" : hhmm(cursorMs)}` };
}

// The cursor (ms, or null for now) at a knob value 0..KNOB, snapped to whole minutes.
export function cursorAt(sc, knob) {
  const ms = sc.min + Math.round((knob / KNOB) * sc.steps) * STEP_MS;
  return ms >= sc.max ? null : ms;
}

// The cursor one minute (STEP_MS) earlier or later (by -1 / 1); null back at now.
export function stepCursor(sc, by) {
  const ms = Math.min(sc.max, Math.max(sc.min, (sc.live ? sc.max : sc.cursorMs) + by * STEP_MS));
  return ms >= sc.max ? null : ms;
}

// The window: a tab per top-level running team, and the shown team (`team`, else the first) as groups: the team, then its
// lanes, each its members then its links, and the scrubber over them. `closed` when the shown team stopped running; `loaded`
// is false until the team list. ui: the view's graphWindow, graphCursor and graphZoom ({ team, node }: a stream circle of the
// team or lane `team` zoomed into, which then shows alone, with `crumb` the way back out).
// size: px, or a function of the group's depth (the docked lanes draw smaller).
export function graphModel(slices, team = null, size = MAP_PX, ui = {}, clock = Date.now()) {
  const top = sidebarModel(slices, 0).teams;
  const tabs = top.map((t) => ({ id: t.id, name: t.name }));
  const all = flat(top);
  const id = team ?? top[0]?.id ?? null;
  const at = all.find((x) => x.t.id === id);
  const base = { loaded: slices.has("list:teams"), tabs, id, empty: top.length === 0 };
  if (!at) return { ...base, closed: id !== null, groups: [], scrub: null, crumb: null };
  const shown = flat([at.t]);
  const z = ui.graphZoom && shown.find((x) => x.t.id === ui.graphZoom.team) ? ui.graphZoom : null;
  const groups = (z ? shown.filter((x) => x.t.id === z.team) : shown)
    .map(({ t, depth }) => group(slices, t, z ? 0 : depth, typeof size === "function" ? size(z ? 0 : depth) : size, { ui, clock, zoom: z?.node ?? null }));
  const focus = z && groups[0]?.map?.focus;
  const crumb = focus?.zoomed ? [at.t.name, ...(z.team === at.t.id ? [] : [groups[0].name]), focus.name] : null;
  return { ...base, closed: false, name: at.t.name, groups, pending: groups.some((g) => g.pending), scrub: scrubberOf(groups, ui, clock), crumb };
}

// ---- Flow: the web's Flow (flow-model.js, a byte-identical copy of web/ui's) for one team, in drawn px

// The stored Map | Flow choice (Herdr's shared store, GRAPH_KEY) read to {mode, orient}: "flow" only when stored so, else "map" (herdr
// has no Radial); orient "vertical" or "horizontal" only when stored so, else null: the stage's shape picks it (flowModel).
export function graphChoice(stored) {
  const mode = stored?.mode === "flow" ? "flow" : "map";
  const orient = stored?.orient === "vertical" || stored?.orient === "horizontal" ? stored.orient : null;
  return { mode, orient };
}

export const LEGEND_H = 24; // the legend row under Flow's stage

// Flow's pan {x, y} on the stage: content no larger than the stage sits centred across and at the top; larger content is clamped so no
// gap shows at either edge (absent: centred across, at the top).
export function flowPan(pan, size, stage) {
  const axis = (p, s, room, rest) => (s <= room ? rest : Math.min(0, Math.max(room - s, p ?? rest)));
  return { x: axis(pan?.x, size.w, stage.w, (stage.w - size.w) / 2), y: axis(pan?.y, size.h, stage.h, 0) };
}

// The pan that keeps the content point under the stage point (px, py) where it is when the drawn scale goes from k to k2.
export function flowPanAround(pan, size, stage, k, k2, px, py) {
  const p = flowPan(pan, size, stage);
  const f = k2 / k;
  return flowPan({ x: px - (px - p.x) * f, y: py - (py - p.y) * f }, { w: size.w * f, h: size.h * f }, stage);
}

// Flow's − / + / wheel: the scale to s2 (clamped) about the stage point (px, py), else its centre. m: flowModel's frame at the old
// scale. False when the clamp leaves the scale where it was.
export function flowRescale(ui, m, s2, px = null, py = null) {
  const s = ui.graphScale ?? 1;
  const to = clampScale(s2);
  if (to === s || !m?.stage) return false;
  const k2 = m.box ? clampScale(m.fit * to) : 1;
  ui.graphScale = to;
  ui.graphPan = { flow: flowPanAround(ui.graphPan?.flow, m.size, m.stage, m.k, k2, px ?? m.stage.w / 2, py ?? m.stage.h / 2) };
  return true;
}

// The shown team as Flow draws it, every position in drawn px (world × k from the layout box's corner). orient: the stored or chosen
// one, or null: then it is picked from the stage's shape, vertical when the stage is narrower than the horizontal layout, and
// `picked` says which (null before the first measure, when the frame draws horizontal). room: the view's measured body, or null.
export function flowModel(slices, team = null, orient = null, scale = 1, room = null) {
  const top = sidebarModel(slices, 0).teams;
  const tabs = top.map((t) => ({ id: t.id, name: t.name }));
  const id = team ?? top[0]?.id ?? null;
  const at = flat(top).find((x) => x.t.id === id);
  const base = { loaded: slices.has("list:teams"), tabs, id, empty: top.length === 0, scrub: null, crumb: null };
  if (!at) return { ...base, closed: id !== null };
  const teamId = `team:${id}`;
  const tm = slices.get(teamId);
  if (!tm) return { ...base, closed: false, name: at.t.name, pending: true };
  const ids = memberIds(tm);
  const nodes = flowNodes(slices, tm, ids, laneChildren(slices, tm, ids));
  const active = flowActive(slices, tm, nodes);
  const stage = room ? { w: room.w, h: Math.max(0, room.h - LEGEND_H) } : { w: MAP_PX, h: MAP_PX };
  let picked = null;
  let o = orient;
  let layout = flowLayout(nodes, active, o ?? "horizontal");
  if (!o && room && layout.box && stage.w < layout.box.w) {
    picked = "vertical";
    layout = flowLayout(nodes, active, picked);
  } else if (!o && room) picked = "horizontal";
  o = o ?? picked ?? "horizontal";
  const box = layout.box;
  const fit = box ? fitBox(box, stage.w, stage.h).k : 1;
  const k = box ? clampScale(fit * scale) : 1;
  const X = (x) => (x - (box?.x ?? 0)) * k;
  const Y = (y) => (y - (box?.y ?? 0)) * k;
  const rect = (r) => ({ x: X(r.x), y: Y(r.y), w: r.w * k, h: r.h * k });
  const scaled = (cmds) => cmds.map((c) => [c[0], ...c.slice(1).map((n, i) => (i % 2 === 0 ? X(n) : Y(n)))]);
  const sparks = sparksOf(slices, tm);
  const edges = slices.get(`topology:${id}`)?.body.edges?.parent_child ?? [];
  const fl = flowLinks(edges, sparks, layout.pos, o);
  // a lane's member reads its pane from its lane's summary, as the sidebar's row does (memberPane)
  const paneAt = (n) => memberPane(n.id, slices.get((n.team || teamId).replace(/^team:/, "team-summary:"))?.body.member_states?.[n.id]);
  const cards = nodes.filter((n) => layout.pos.has(n.id)).map((n) => ({ id: n.id, rect: rect(layout.pos.get(n.id)), card: flowCardOf(slices, n.team || teamId, n.id),
    active: active.has(n.id), out: sparks.filter((s) => s.from === n.id).length, pane: paneAt(n) }));
  const links = fl.links.map((l) => ({ key: l.key, kind: l.kind, cmds: scaled(l.cmds) }));
  const dots = fl.dots.map((d) => {
    const cmds = scaled(d.cmds);
    const mid = cmds[Math.floor(cmds.length / 2)];
    return { key: d.key, x: mid.at(-2), y: mid.at(-1), review: d.review, title: `in flight: ${d.slug} (${shortName(d.from)} → ${shortName(d.to)})${d.review ? ", review" : ""}` };
  });
  const lines = links.filter((l) => l.kind === "edge").length;
  const said = `${at.t.name} Flow: ${cards.length} members, ${lines} reporting lines, ${dots.length} briefs in flight${fl.rest > 0 ? `, ${fl.rest} more not drawn` : ""}`;
  return { ...base, closed: false, name: at.t.name, pending: false, orient: o, picked, stage, fit, k, box,
    size: box ? { w: box.w * k, h: box.h * k } : { w: 0, h: 0 },
    cards, labels: layout.labels.map((l) => ({ text: l.text, x: X(l.x), y: Y(l.y) })), bands: layout.bands.map((b) => ({ id: b.id, name: b.name, ...rect(b) })),
    links, dots, rest: fl.rest, said };
}
