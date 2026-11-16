import { streamName } from "./plans-model.js";

// The graph views' world, pure: no DOM and no d3. Slices in, the tree, the events in a scrubber window and the pairs drawn out.
// An event is [t, a, b, k]: seconds since the run started, sender and receiver as indexes into the flow head's names, 0 brief, 1 report.

export const MODES = ["map", "radial", "flow"];
// A stored mode the page does not know (an older or newer build, or garbage) opens Map.
export const modeOf = (v) => (MODES.includes(v) ? v : "map");

// A state's glyph, so a state never rests on its colour alone.
const GLYPH = { working: "●", running: "●", blocked: "❚❚", idle: "○", gone: "×", unknown: "?" };
export const glyphOf = (state) => GLYPH[state] ?? GLYPH.unknown;

// A member's name on the graph, from its slice id (member:<dir>/<team>/<name>): without the team, which every member of it shares, so what
// tells two members apart is what shows. `name` is the served name, when there is one. The full name stays on the hover card and in the title.
export function shortName(id, name = id.split("/").pop()) {
  const team = id.split("/").at(-2);
  const bare = name.replace(/^@/, "");
  return team && bare.startsWith(`${team}-`) && bare.length > team.length + 1 ? bare.slice(team.length + 1) : bare;
}

export const KINDS = ["brief", "report"];
export const kindOf = (k) => KINDS[k] ?? "brief";

// The scrubber's windows, in seconds; null is the whole run up to the cursor.
export const WINDOWS = [
  { id: "run", label: "Run", s: null },
  { id: "60", label: "60 min", s: 3600 },
  { id: "30", label: "30 min", s: 1800 },
  { id: "10", label: "10 min", s: 600 },
];
export const windowOf = (id) => WINDOWS.find((w) => w.id === id) ?? WINDOWS[0];

export const STEP_MS = 60_000;

// The newest event's time in ms from the tail, or null when the tail is empty (the newest paged event is not known without the page;
// the clock covers it).
export function newestMs(head) {
  const tail = head.tail ?? [];
  return tail.length ? head.start_ms + tail[tail.length - 1][0] * 1000 : null;
}

// Now, for the scrubber: the client clock, never earlier than the newest event (the head carries no clock of its own).
export const nowOf = (head, clock) => Math.max(clock, newestMs(head) ?? -Infinity);

// The range the scrubber spans, in ms: from the oldest held event (when the lane cap evicted the run's start) or the run start, to now.
// The left end is moved back to a whole number of steps before now, so the right end, which a range input snaps to, is exactly now.
export function spanOf(head, clock) {
  const max = nowOf(head, clock);
  const from = head.truncated && Number.isFinite(head.oldest_ms) ? head.oldest_ms : head.start_ms;
  const steps = Math.max(1, Math.ceil((max - from) / STEP_MS));
  return { min: max - steps * STEP_MS, max };
}

// Seconds since the run start for a time in ms.
export const secOf = (head, ms) => (ms - head.start_ms) / 1000;

// The events held so far: every loaded page (k -> events) then the tail, in order. A page not loaded is a gap the caller asked to fill.
export function eventsOf(head, pages = new Map()) {
  const out = [];
  for (let k = 0; k < (head.pages?.length ?? 0); k++) {
    const p = pages.get(k);
    if (p) {
      out.push(...p);
    }
  }
  out.push(...(head.tail ?? []));
  return out;
}

// The same, each with its index in the run ([i, event]): page k's j-th event is k·page_size + j, the tail follows the full pages.
export function indexedEvents(head, pages = new Map()) {
  const ps = head.page_size ?? 0;
  const n = head.pages?.length ?? 0;
  const out = [];
  for (let k = 0; k < n; k++) {
    pages.get(k)?.forEach((e, j) => out.push([k * ps + j, e]));
  }
  (head.tail ?? []).forEach((e, j) => out.push([n * ps + j, e]));
  return out;
}

// The events in the window: cursor − window < t ≤ cursor, all of them up to the cursor when the window is the run (null).
export function inWindow(events, cursor, win) {
  const lo = win === null ? -Infinity : cursor - win;
  return events.filter((e) => e[0] <= cursor && e[0] > lo);
}

// Whether the view needs only the head's totals: the cursor at now with the whole-run window.
export const usesTotals = (s) => s.live && s.win === null;

// The page numbers whose events can fall in (lo, hi] seconds. Page k starts at pages[k] and runs to the next page's start, or the tail's.
// A page can hold events up to the next start, inclusive, so events sharing a second across a boundary are never missed; the window's
// lower edge is exclusive, so a page that ends exactly there is not needed.
export function pagesFor(head, lo, hi) {
  const starts = head.pages ?? [];
  const tail0 = head.tail?.length ? head.tail[0][0] : Infinity;
  const out = [];
  starts.forEach((t0, k) => {
    const end = k + 1 < starts.length ? starts[k + 1] : tail0;
    if (t0 <= hi && end > lo) {
      out.push(k);
    }
  });
  return out;
}

// The pages the scrubber needs where it stands (scrubOf): none at now over the run, else those covering the window.
export function neededPages(head, s) {
  return usesTotals(s) ? [] : pagesFor(head, s.win === null ? -Infinity : s.cursor - s.win, s.cursor);
}

// Radial draws one path per event, capped to the newest `cap` it can show: the pages it needs where the scrubber stands. A window
// needs the pages covering it, as Map does off now; the run window needs only the newest pages that hold `cap` events up to the
// cursor (every page is full, page_size events). The page the cursor falls inside is counted as holding none, since only some of it
// is up to the cursor, so the newest `cap` are always loaded (at most one page more than needed).
export function newestPages(head, s, cap) {
  if (s.win !== null) {
    return pagesFor(head, s.cursor - s.win, s.cursor);
  }
  const starts = head.pages ?? [];
  const tail0 = head.tail?.length ? head.tail[0][0] : Infinity;
  let held = (head.tail ?? []).filter((e) => e[0] <= s.cursor).length;
  const out = [];
  for (let k = starts.length - 1; k >= 0 && held < cap; k--) {
    if (starts[k] <= s.cursor) {
      out.unshift(k);
      held += (k + 1 < starts.length ? starts[k + 1] : tail0) > s.cursor ? 0 : head.page_size;
    }
  }
  return out;
}

// Radial's path ceiling: one SVG path per message, the newest RADIAL_CAP in the view.
// ponytail: SVG path count ceiling; raise or aggregate if runs grow.
export const RADIAL_CAP = 600;

// Radial's view: Map's (scrubbed), plus the newest `cap` events up to the cursor as [t, event, run index] (drawn). Off now over the run
// only the newest pages are loaded, so the count is the run index of the newest event up to the cursor, plus one (events are numbered
// from 0 in t order), and the panel counts what is drawn: partial says how many that is, 0 when the counts cover the whole view.
export function radialView(head, pages, s, cap = RADIAL_CAP) {
  const base = scrubbed(head, pages, s);
  const shown = inWindow(indexedEvents(head, pages).map(([i, e]) => [e[0], e, i]), s.cursor, s.win);
  const drawn = shown.slice(-cap);
  if (usesTotals(s)) {
    return { ...base, drawn, partial: 0 };
  }
  const count = s.win === null && shown.length ? shown[shown.length - 1][2] + 1 : shown.length;
  const partial = count > shown.length;
  return { ...base, events: (partial ? drawn : shown).map((x) => x[1]), count, drawn, partial: partial ? drawn.length : 0 };
}

// Events grouped per ordered pair and kind: [{a, b, k, n, last}], last = the newest t, for the fade.
export function pairsOf(events) {
  const by = new Map();
  for (const [t, a, b, k] of events) {
    const key = `${a}|${b}|${k}`;
    const p = by.get(key);
    if (p) {
      p.n += 1;
      p.last = Math.max(p.last, t);
    } else {
      by.set(key, { a, b, k, n: 1, last: t });
    }
  }
  return [...by.values()];
}

// The head's whole-run totals as pairs. The overflow row (-1, -1) has no ends to draw between, so it is left out.
export function pairsOfTotals(head) {
  const out = [];
  for (const [a, b, briefs, reports] of head.totals ?? []) {
    if (a < 0 || b < 0) {
      continue;
    }
    briefs > 0 && out.push({ a, b, k: 0, n: briefs, last: null });
    reports > 0 && out.push({ a, b, k: 1, n: reports, last: null });
  }
  return out;
}

// What the scrubber shows: the pairs to draw and how many messages they hold, from the totals or from the windowed events.
export function scrubbed(head, pages, s) {
  if (usesTotals(s)) {
    const pairs = pairsOfTotals(head);
    const overflow = (head.totals ?? []).filter(([a]) => a < 0).reduce((n, r) => n + r[2] + r[3], 0);
    return { pairs, events: null, count: pairs.reduce((n, p) => n + p.n, 0) + overflow };
  }
  const events = inWindow(eventsOf(head, pages), s.cursor, s.win);
  return { pairs: pairsOf(events), events, count: events.length };
}

// The arc width for n messages.
// 1 px for a single message, rising gently to 2 px at 256: the count reads, the bundle stays thin.
export const widthOf = (n) => 1 + Math.min(1, Math.log2(Math.max(1, n)) / 8);

// The opacity of a pair in a windowed view: newer is stronger. The run window does not fade.
export const fadeOf = (cursor, t, win) => (win === null || t === null ? null : Math.max(0.2, 0.9 - (0.7 * (cursor - t)) / win));

// The packed tree: the team at the root, then one circle per stream holding the stream's members. When the team has streams, its
// team-level (loose) members sit together in an undrawn group of their own, so they pack apart from the stream circles.
// members: [{id, stream}], in the order to draw. Streams sort by name.
export function treeOf(teamId, teamName, members) {
  const streams = new Map();
  const top = [];
  for (const m of members) {
    if (m.stream) {
      streams.set(m.stream, [...(streams.get(m.stream) ?? []), m]);
    } else {
      top.push(m);
    }
  }
  const leaf = (m) => ({ id: m.id, type: "member", name: m.name ?? m.id, m });
  return {
    id: teamId,
    type: "team",
    name: teamName,
    children: [
      ...(streams.size && top.length ? [{ id: `loose:${teamId}`, type: "group", name: teamName, children: top.map(leaf) }] : top.map(leaf)),
      ...[...streams.keys()].sort().map((s) => ({ id: `stream:${s}`, type: "stream", name: streamName(s), children: streams.get(s).map(leaf) })),
    ],
  };
}

// The Map's packing air, fit and arc offset, one source for the web's Map (graph-pack.js) and Herdr's Graph (graph-model.js).
// The gap between sibling circles, as a share of a member's radius: every member packs at radius 1, so siblings, and a member and
// its circle's edge, sit AIR apart, the team's top-level circles twice that.
export const AIR = 0.6;
export const airOf = (n) => (n.depth === 0 ? 2 * AIR : AIR);

// On-screen radius (px) at which each member label tier shows: ctx %, name, role, "model · state", "item · last Nm".
export const TIERS = [14, 34, 64, 84, 150];
// On-screen radius (px) at which a team's or stream's circle shows its name.
export const LABEL_AT = { team: 40, stream: 70 };
// At most this many briefs in flight draw a spark.
export const MAX_SPARKS = 24;

// Scales a packed root to a `size` square, centred, and transposed (x and y swapped) in place.
export function fitPack(root, size) {
  const k = size / 2 / root.r;
  const [cx, cy] = [root.x, root.y];
  for (const n of root.descendants()) {
    [n.x, n.y, n.r] = [size / 2 + (n.y - cy) * k, size / 2 + (n.x - cx) * k, n.r * k];
  }
}

// An arc's points from a to b, the inner ones offset sideways: briefs (k 0) one way, reports the other, 2 units in a 1000 world,
// close enough to read as one bundle.
export function sideways(pts, a, b, k, size) {
  const o = ((k === 0 ? 2 : -2) * size) / 1000;
  const dx = b.x - a.x;
  const dy = b.y - a.y;
  const len = Math.hypot(dx, dy) || 1;
  return pts.map((n, i) => (i === 0 || i === pts.length - 1 ? n : { x: n.x - (dy / len) * o, y: n.y + (dx / len) * o }));
}

// "HH:MM" in local time.
const HHMM = new Intl.DateTimeFormat([], { hour: "2-digit", minute: "2-digit", hour12: false });
export const hhmm = (ms) => HHMM.format(new Date(ms));

// The scrubber's readout: "now · N messages" or "HH:MM · N messages", prefixed when the lane cap cut the history short,
// and "last hour" when no run is open.
export function readout(head, cursorMs, count, live) {
  const when = live ? "now" : hhmm(cursorMs);
  const prefix = head.run === null ? "last hour · " : head.truncated ? `history from ${hhmm(head.oldest_ms)} · ` : "";
  return `${prefix}${when} · ${count} ${count === 1 ? "message" : "messages"}`;
}

// ---- slice ids and reads (the slices Map in, plain values out)

export const flowId = (t) => `flow:${t.dir_id}/${t.name}`;
export const pageId = (t, run, k) => `flow-page:${t.dir_id}/${t.name}/${run}/${k}`;
export const itemsId = (t, stream) => `plan-items:${t.dir_id}/${t.name}/${stream}`;
export const planId = (t) => `plan:${t.dir_id}/${t.name}`;
// The plan's stream names, which name the plan-items slices.
export const planStreams = (slices, t) => (slices.get(planId(t))?.body.streams ?? []).map((s) => s.stream).filter(Boolean);
// A member's stream as the plan names it: placement.stream may hold the stream's name or its branch. One the plan does not know keeps
// its own name, so it still gets a circle.
export function streamOf(slices, t, raw) {
  if (!raw) {
    return null;
  }
  const ps = slices.get(planId(t))?.body.streams ?? [];
  return (ps.find((s) => s.stream === raw) ?? ps.find((s) => s.branch === raw))?.stream ?? raw;
}

// ponytail: freshness is judged by the page's first t and its size, not a version. A renumbered page whose first event lands on the
// same second as the new pages[k], or a withdrawal inside a page (first t unchanged), shows its old events until the new version
// arrives. Pages carry no pages_rev to compare; add one to the page slice if that gap ever shows.
// The loaded pages of the run, k -> events. A page is taken only when it is full and starts where the head says page k starts: after the
// lane cap renumbers the events (pages_rev changes), a page still holding the old numbering fails that check and waits for its new
// version. An unchanged page is never republished, and it passes the check as it stands.
export function pagesOf(slices, t, head) {
  const out = new Map();
  if (head?.run) {
    const size = head.page_size;
    (head.pages ?? []).forEach((t0, k) => {
      const events = slices.get(pageId(t, head.run, k))?.body.events;
      events && events.length === size && events[0]?.[0] === t0 && out.set(k, events);
    });
  }
  return out;
}

// Where the scrubber stands: the cursor (null = now, following the head), the window, and both in run seconds.
export function scrubOf(ui, head, clock) {
  const win = windowOf(ui.graphWindow);
  const span = spanOf(head, clock);
  const live = ui.graphCursor == null || ui.graphCursor >= span.max;
  const cursorMs = live ? span.max : Math.max(span.min, ui.graphCursor);
  return { live, cursorMs, cursor: secOf(head, cursorMs), win: win.s, winId: win.id, span };
}

// The slice ids the scrubber needs at the cursor and window the page shows.
// With `cap`, the pages Radial needs (newestPages); without, Map's.
export function flowLeases(slices, t, ui, clock, cap = 0) {
  const head = slices.get(flowId(t))?.body;
  if (!head?.run) {
    return [flowId(t)];
  }
  const s = scrubOf(ui, head, clock);
  return [flowId(t), ...(cap ? newestPages(head, s, cap) : neededPages(head, s)).map((k) => pageId(t, head.run, k))];
}

// A member's messages in the scrubber's view, sent and received: from the windowed events, else from the run's totals (ordered pairs).
export function sentReceived(head, view, idx) {
  if (idx === undefined) {
    return { sent: 0, received: 0 };
  }
  if (view.events) {
    return { sent: view.events.filter((e) => e[1] === idx).length, received: view.events.filter((e) => e[2] === idx).length };
  }
  const rows = (head.totals ?? []).filter(([a, b]) => a >= 0 && b >= 0);
  const sum = (side) => rows.filter((r) => r[side] === idx).reduce((n, r) => n + r[2] + r[3], 0);
  return { sent: sum(0), received: sum(1) };
}

// A member's current item: the first not-done item its stream's plan holds for it, by name or id. Its key less the stream prefix.
export function currentItem(slices, t, stream, member) {
  if (!stream) {
    return null;
  }
  const items = slices.get(itemsId(t, stream))?.body.items ?? [];
  const it = items.find((i) => (i.holder === member.name || i.holder === member.id) && i.state !== "done" && i.state !== "dropped");
  return it ? { key: it.key.startsWith(`${stream}:`) ? it.key.slice(stream.length + 1) : it.key, full: it.key, title: it.title ?? "" } : null;
}

// The newest event a member sent or received, in run seconds, or null.
export function lastEventOf(events, idx) {
  for (let i = events.length - 1; i >= 0; i--) {
    if (events[i][1] === idx || events[i][2] === idx) {
      return events[i][0];
    }
  }
  return null;
}

// ---- the Plans board (icicle): slices in, flat rows out, ready for d3.stratify. Pure, so the tree is node-testable.

// A parent link counts only when the parent outranks the child (ah-epics E-D3); absent or unknown is a task. Strict layering means a
// cycle can never reach stratify.
const RANK = { epic: 3, deliverable: 2, task: 1 };
export const rankOf = (kind) => RANK[kind] ?? 1;

// A leaf's weight: a numeric size, a backlog T-shirt size, else 1.
const TSHIRT = { XS: 1, S: 1, M: 2, L: 3, XL: 5 };
export const weightOf = (size) => (Number(size) > 0 ? Number(size) : TSHIRT[size] ?? 1);

// An entity id's parts. Stream names may hold "/", so the fields after the team key are split by "|":
// t:<dir>/<team>, d:<dir>/<team> (a decision), s:<dir>/<team>|<stream>, i:<dir>/<team>|<stream>|<item>, b:<dir>/<team>|<backlog key>.
export function partsOf(ent) {
  if (typeof ent !== "string" || !/^[tdsib]:/.test(ent)) {
    return null;
  }
  const [head, a = null, b = null] = ent.slice(2).split("|");
  const [dir, team] = head.split("/");
  const k = ent[0];
  return { kind: k, dir, team, key: `${dir}/${team}`, stream: k === "s" || k === "i" ? a : null, item: k === "i" ? b : k === "b" ? a : null };
}

// The board's rows, state first: the root, then one tile per state column (every column, in order, even when empty), then inside
// each column team → epic → deliverable → item (Show epics on) or team → stream → item. An item with no valid backlog parent sits
// under its stream. An epic or team with items in several columns has a tile in each, holding only that column's items.
// columns: [[key, label]] (COLUMNS); cells: { key: [board cards] } (boardOf, already filtered and capped); more: { key: n not shown }.
// backlogs: Map dirId → backlog items, or null when Show epics is off. scope: the team's name at team level (the root; no team
// tier), or null for All teams.
// A row: { id, ent, parent, kind, name, col, order, state, holder, weight (leaves), done, total, team, stream, item, mark }. id is
// unique per column (`<col>#<ent>`); ent names the thing, the same in every column (partsOf reads it). Team keys are `<dirId>/<team>`.
// keep: a card filter (the stream filter today; a team or lane filter the same way), or null for every card.
export function boardRows({ columns, cells, caps = {}, backlogs = null, keep = null, scope = null }) {
  const rows = [{ id: "root", ent: "root", parent: null, kind: scope === null ? "root" : "team", name: scope ?? "All teams" }];
  const added = new Set();
  const add = (r) => (added.has(r.id) ? null : (added.add(r.id), rows.push(r)));
  const all = Object.values(cells).flat().filter((c) => !keep || keep(c));
  const dirsOf = new Map();
  all.forEach((c) => dirsOf.set(c.team, new Set([...(dirsOf.get(c.team) ?? []), c.dirId])));
  const labelOf = (c) => (dirsOf.get(c.team).size > 1 ? `${c.team} · ${c.repo ?? c.dirId.slice(0, 6)}` : c.team);
  columns.forEach(([col, label], order) => {
    const cid = `c:${col}`;
    // The filter first, then the cap (newest first, as boardOf sorts Done), so "N of M" counts only what the filter keeps.
    const kept = keep ? (cells[col] ?? []).filter(keep) : cells[col] ?? [];
    const cards = caps[col] ? kept.slice(0, caps[col]) : kept;
    rows.push({ id: cid, ent: cid, parent: "root", kind: "column", name: label, col, order, count: cards.length, more: kept.length - cards.length });
    for (const c of cards) {
      const tk = `${c.dirId}/${c.team}`;
      const at = (ent) => `${col}#${ent}`;
      const base = { team: tk, col };
      let above = cid;
      if (scope === null) {
        above = at(`t:${tk}`);
        add({ ...base, id: above, ent: `t:${tk}`, parent: cid, kind: "team", name: labelOf(c) });
      }
      if (c.decision) {
        rows.push({ ...base, id: at(`d:${tk}`), ent: `d:${tk}`, parent: above, kind: "decision", name: c.title, state: "waiting_user", weight: 1, done: 0, total: 0 });
        continue;
      }
      const sEnt = `s:${tk}|${c.stream}`;
      if (c.changed) {
        rows.push({ ...base, id: at(sEnt), ent: sEnt, parent: above, kind: "stream", name: c.title, stream: c.stream, state: "in_progress", mark: c.mark, weight: 1, done: 0, total: 0 });
        continue;
      }
      // The item's backlog chain, nearest first; a link counts only when the parent outranks the child.
      const byKey = backlogs?.get(c.dirId);
      const up = (key, kind) => {
        const p = key ? byKey?.get(key) : undefined;
        return p && rankOf(p.kind) > rankOf(kind) ? p : null;
      };
      const chain = [];
      for (let p = up(c.parent, "task"); p; p = up(p.parent, p.kind)) {
        chain.unshift(p);
      }
      for (const p of chain) {
        const ent = `b:${tk}|${p.key}`;
        add({ ...base, id: at(ent), ent, parent: above, kind: p.kind, name: p.title ?? p.key, state: p.state ?? "not_started", holder: p.holder ?? null, item: p.key });
        above = at(ent);
      }
      if (chain.length === 0) {
        // A stream reads by its goal; its slug is the tooltip and the card's id.
        add({ ...base, id: at(sEnt), ent: sEnt, parent: above, kind: "stream", name: c.goal || streamName(c.stream), stream: c.stream, item: c.stream === "_run" ? undefined : c.stream });
        above = at(sEnt);
      }
      const ent = `i:${tk}|${c.stream}|${c.key}`;
      rows.push({ ...base, id: at(ent), ent, parent: above, kind: "task", name: c.title, state: c.state, holder: c.owner || null, stream: c.stream, item: c.key, mark: c.mark ?? null, weight: weightOf(c.size), done: c.state === "done" ? 1 : 0, total: 1 });
    }
  });
  // A column with no cards has nothing under it: no weight, though the board still draws its header.
  const parents = new Set(rows.map((r) => r.parent));
  return rows.map((r) => (parents.has(r.id) || r.weight !== undefined ? r : { ...r, weight: 0, done: 0, total: 0 }));
}
