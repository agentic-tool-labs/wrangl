// The ah2 mod's Board data side (spec-herdr-desktop.md § View: board): no herdr or gpui imports, so preview/render.mjs runs it.
// The columns and the cards are the web Board's: plans-model.js boardOf over select.js teamRows, both byte-identical copies of
// web/ui's (kept honest by tests/web/herdr-board-model.test.mjs). The Waiting on you column is the Q&A window's own cards (qaModel).
import { COLUMNS, DAY_MS, DONE_CAP, STATE_LABEL, WAVES, boardOf, descriptionBlocks, historyOf, httpUrl, itemLogId, itemsId, planChangesId, planIdOf, queueRows, stalledOf, streamName, teamModel, waveColumns } from "./plans-model.js";
import { isQuiet, teamRows } from "./select.js";
import { questionsId } from "./questions-model.js";
import { qaModel, age, sidebarModel, within, late, NO_ANSWER } from "./model.js";
import { marksOf, snapshotOf } from "./plans-seen.js";
import { eventsQuery } from "./link-model.js";

const dayStartOf = (now) => new Date(now).setHours(0, 0, 0, 0);

// Running teams, closed ones left out (as model.js's sidebar does), with their plan as the web's teamModel reads it.
// Scoped to one team (`mk`, "dir/name"), that team and its lanes' child teams, as the web's team board (plans-view.js derive).
function teams(slices, now, mk = null) {
  const laneTeams = new Set(mk ? lanesOf(slices.get(`team:${mk}`)?.body).map((l) => l.child).filter(Boolean) : []);
  return teamRows(slices)
    .filter((r) => r.running && !slices.get(`team-summary:${r.id.slice("team:".length)}`)?.body?.closed_mark && (!mk || r.id === `team:${mk}` || laneTeams.has(r.id)))
    .map((r) => teamModel(r, slices.get(planIdOf(r))?.body, now));
}

// The Board window's extra subscriptions: per running team its plan and questions, and the items of each stream boardOf can draw
// (a stream done before today has no card). They grow as replies arrive: teams, then plans, then items. A quiet team gets no
// plan or items subs until it is expanded (`open`, team ids), as boardOf draws none of its cards; its questions still feed Waiting on you.
// `log`: the open sheet's item-log: slice (the web's ItemLog lease), held only while that sheet is open on the Board tab. `shown`:
// the Board tab is on screen, so it holds what only the board draws (the web's leases): the backlog of its directory, and per team
// drawn its queue and message counts.
// `team`: the scope's team (its lanes' teams come with it); `hide`: the team keys switched off by their chips, whose queue and
// message counts are not held.
export function boardSubs(slices, now = Date.now(), { open = new Set(), log = null, shown = false, team = null, hide = new Set() } = {}) {
  const day = dayStartOf(now);
  const all = teams(slices, now, team);
  const drawn = all.filter((t) => !t.quiet || open.has(t.id));
  const dir = backlogDir(all, team);
  return [
    ...all.flatMap((t) => [questionsId(t.id), ...(drawn.includes(t) ? [planIdOf(t), ...t.streams.filter((s) => s.state !== "done" || s.asOf >= day).map(itemsId)] : [])]),
    ...(shown ? [...(dir ? [`backlog:${dir}`] : []), ...drawn.filter((t) => !hide.has(`${t.dirId}/${t.name}`)).flatMap((t) => [`queue:${t.dirId}/${t.name}`, `item-msgs:${t.dirId}/${t.name}`])] : []),
    ...(log ? [log] : []),
  ];
}

// The one directory whose backlog the board draws: the scope team's, else the first running team's (the web's All teams board
// takes its first card's).
const backlogDir = (running, mk = null) => (mk ? mk.split("/")[0] : running[0]?.dirId ?? null);

// The web board's backlog helpers (board-kanban.js; a Preact file, so restated, not shared): a card's deliverable and epic up its
// `parent` chain, and the Backlog column, the open rows not planned, by wave and rank, project and goal rows left out.
const CONTAINERS = ["project", "goal"];
export const WAVE_CAP = 4;
function chainOf(c, bl) {
  let deliverable = null;
  let epic = null;
  for (let k = c.parent, n = 0; k && n < 8; n++) {
    const r = bl.get(k);
    if (!r) break;
    if (r.kind === "deliverable" && !deliverable && !epic) deliverable = r;
    else if (r.kind === "epic" && !epic) epic = r;
    k = r.parent;
  }
  return { deliverable, epic };
}
// Under an epic or deliverable scope (`root`), only that row's descendants.
function backlogColumn(body, root = null) {
  const byKey = new Map((body?.items ?? []).map((r) => [r.key, r]));
  const under = (r) => {
    for (let k = r.parent, n = 0; k && n < 8; n++, k = byKey.get(k)?.parent) if (k === root) return true;
    return false;
  };
  const { cols } = waveColumns(body);
  return WAVES.map(([wave, label]) => ({ wave, label, rows: cols[wave].filter((r) => !r.planned && !CONTAINERS.includes(r.kind) && (!root || under(r))) })).filter((g) => g.rows.length > 0);
}
// A duration as the web board says it: "40 min", "46 h", "3 d".
const durText = (ms) => {
  const m = Math.max(0, Math.round(ms / 60_000));
  return m < 60 ? `${m} min` : m < 2880 ? `${Math.round(m / 60)} h` : `${Math.round(m / 1440)} d`;
};
// A Next card's queue marker (board-kanban.js markerOf, its queue branch): stalled when the server's stall time has passed with an
// idle eligible member, else waiting since it was queued.
function queueFlag(q, now) {
  const since = q.queuedSince ? ` ${durText(now - q.queuedSince)}` : "";
  return stalledOf(q.stallAt, q.eligible, now)
    ? { glyph: "■", text: `stalled${since} · no owner, ${q.eligible.length} idle`, tip: "Ready, unclaimed, and an eligible member is idle", stalled: true }
    : { glyph: "◷", text: `waiting${since}${q.eligible.length === 0 ? ` · no idle ${q.role ?? "member"}` : ""}`, tip: "Ready and not yet claimed", stalled: false };
}
const UNDER_WAY = new Set(["in_progress", "in_review", "testing"]);
// No epic or deliverable: the web's key for it in a scope.
export const NONE = "~";
const REPORT = { working: "Working", blocked: "Blocked", review: "In review", in_review: "In review", done: "Done", dissolved: "Dissolved" };

// A manager team's lanes, from its `streams[]` (board-kanban.js lanesOf): every row that names a sub-orchestrator; one with a
// child team is confirmed. The lane a card belongs to (laneOfCard): a child team's card is its lane's, a manager card in a lane's
// stream is that lane's, else "top".
function lanesOf(body) {
  const rows = Array.isArray(body?.streams) ? body.streams : [];
  return rows.filter((s) => typeof s?.sub_orchestrator === "string" && typeof s.name === "string").map((s) => ({
    stream: s.name, child: typeof s.child_team === "string" ? `team:${body.dir_id}/${s.child_team}` : null, so: s.sub_orchestrator,
    state: s.state ?? "open", branch: s.branch ?? null, base: s.base ?? null,
  })).sort((a, b) => a.stream.localeCompare(b.stream));
}
function laneOfCard(c, lanes, mk) {
  const key = `${c.dirId}/${c.team}`;
  if (key !== mk) return lanes.find((l) => l.child === `team:${key}`)?.stream ?? "top";
  return lanes.some((l) => l.stream === c.stream) ? c.stream : "top";
}

// Change marks since the viewer's last visit, kept across restarts (the web's plans-seen.js, a byte-identical copy): `track` is its
// loadTrack, read from Herdr's shared store (board.js). Marks are the difference between what that visit held and what is held
// now; no track, no marks.
const said = (st) => STATE_LABEL[st] ?? st;
const markText = (m) => (!m ? "" : m.kind === "moved" ? `${said(m.from)} → ${said(m.to)}` : m.kind === "new" ? "New since your last visit" : m.kind === "done" ? "Done since your last visit" : "Changed since your last visit");
const scopeKey = (t) => `${t.dirId}/${t.name}`;

// Changed since last visit with nothing changed (the web's nothing()): a first visit, a look under a minute old, or since when.
export const nothingChanged = (since, now) =>
  since === null ? "Nothing to compare yet: this is your first visit" : now - since < 60_000 ? "Nothing changed since just now" : `Nothing changed since ${age(now - since)} ago`;

// What a visit leaves behind (the web's PlansView save): the time of this look for each team whose plan was held, and each held
// stream's items. A stream whose items are not held keeps what was stored, unless `reset` (Mark all seen) records it with none.
// Only `reset` changes the baseline this visit compares against; otherwise a copy is written.
export function trackAfter(track, covered, slices, now, reset = false) {
  const out = reset ? track : { look: { ...track.look }, seen: JSON.parse(JSON.stringify(track.seen)) };
  for (const t of covered) {
    if (!t.hasPlan) continue;
    out.look[scopeKey(t)] = now;
    for (const s of t.streams) {
      const items = slices.get(itemsId(s))?.body?.items ?? null;
      if (items !== null || reset) (out.seen[scopeKey(t)] ??= {})[s.stream] = snapshotOf(s.stream, s.asOf, items);
    }
  }
  if (reset) track.mountedAt = now;
  return out;
}
const nameOf = (asker) => (asker ?? "").split("@")[0];

// The board: the web's columns in its order. A card is plain ({ key, title, ... }) or a Q&A card ({ qa, tm }) that answers in place.
// Waiting on you holds every Q&A card first; a waiting plan item or a team's decision card the questions slice does not cover stays
// plain, with the way into Q&A. Done today keeps the newest DONE_CAP.
// Filters (the filter row): `hide` team keys (dir/name) left out, `owner` the one holder kept (a Q&A card's asker), `blockedOnly` and
// `changesOnly` as boardOf takes them (a Q&A card stays under Changes only when its own plan item is marked). `seen`: marker's map.
// `query` the Filter streams text and `showFinished` the older streams, as the web's plans-view.js passes them; `open` the quiet
// team ids expanded (boardOf `expanded`); `expand` the column keys drawn whole.
// The most cards a column draws until it is expanded; the header still counts them all and the column ends "+ N more".
export const COLUMN_CAP = 50;

// The web's servedCounts (board-kanban.js): Waiting on you and In progress as the team summaries count them, over every team
// that is not quiet; `pending` while a plan is unread, so a served zero reads "…" rather than 0.
function servedCounts(slices, now, mk = null) {
  const every = [...slices.values()].filter((s) => s.id.startsWith("team-summary:"));
  const summaries = every.filter((s) => (mk ? s.id === `team-summary:${mk}` : !isQuiet(Date.parse(s.body?.quiet_at ?? ""), now)));
  return {
    waiting: summaries.reduce((n, s) => n + (s.body?.waiting_on_you ?? 0), 0),
    progress: summaries.reduce((n, s) => n + (s.body?.in_progress_now ?? 0), 0),
    pending: (mk ? summaries : every).some((s) => s.body?.plan_loaded === false),
  };
}

export function boardModel(slices, now, { hide = new Set(), owner = "", blockedOnly = false, changesOnly = false, track = null, query = "", showFinished = false, open = new Set(), expand = new Set(), blFold = false, waveAll = new Set(), team = null, lane: laneAsk = null, epic: scopeEpic = null, deliv = null, only = null } = {}) {
  // The scope (the web's breadcrumb): a team (and its lanes' teams), then one lane or the top team, then an epic, then a deliverable.
  const running = teams(slices, now, team);
  const allLanes = team ? lanesOf(slices.get(`team:${team}`)?.body) : [];
  const lane = team && allLanes.length > 0 && laneAsk ? laneAsk : null;
  const scopeDeliv = scopeEpic ? deliv : null;
  const keyOf = (t) => `${t.dirId}/${t.name}`;
  // Stream aging as the web's (plans-view.js): done before today, or empty with no holder and no update for a day. Hidden unless
  // older streams are shown; a stream done today stays, so its items reach Done today.
  const dayStart = dayStartOf(now);
  const aged = (s) => (s.state === "done" && s.asOf < dayStart) || (s.total === 0 && !s.owner && now - s.asOf > DAY_MS);
  const visible = running.filter((t) => !hide.has(keyOf(t)));
  const agedCount = visible.reduce((n, t) => n + t.streams.filter(aged).length, 0);
  // marks per stream against the last visit (none before a first visit), and each stream's changed flag for boardOf
  const sinceOf = (t) => (!track || track.blocked ? null : track.look[scopeKey(t)] ?? null);
  const itemsHeld = (s) => slices.get(itemsId(s))?.body?.items ?? null;
  const marks = new Map(running.flatMap((t) => t.streams.map((s) => [s.id, marksOf(s, { since: sinceOf(t), snapshot: track?.seen[scopeKey(t)]?.[s.stream], items: itemsHeld(s) })])));
  const withMark = (t) => ({ ...t, streams: t.streams.map((s) => ({ ...s, mark: { changed: marks.get(s.id)?.changed ?? false } })) });
  const shown = (showFinished ? visible : visible.map((t) => ({ ...t, streams: t.streams.filter((s) => !aged(s)) }))).map(withMark);
  const itemsOf = (s) => slices.get(itemsId(s))?.body?.items ?? null;
  const dir = backlogDir(running, team);
  const blBody = dir ? slices.get(`backlog:${dir}`)?.body : null;
  const bl = new Map((blBody?.items ?? []).map((r) => [r.key, r]));
  const queue = new Map();
  const b = boardOf(shown, { itemsOf, lanes: "none", query, dayStart, expanded: [...open], markOf: (s, item) => marks.get(s.id)?.items.get(item.key) ?? null, blockedOnly, changesOnly, showFinished });
  const cells = b.lanes[0]?.cells ?? {};
  const qa = qaModel(slices, now);
  const ownerOk = (o) => !owner || o === owner;
  // A team's decision card is covered only when the slice holds as many cards as the team's waiting_on_you (items included:
  // waiting_rows may not list them); a decision the slice does not hold keeps the plain card with the way into Q&A.
  const held = new Map(qa.teams.map((tm) => [tm.id, tm.cards.length]));
  const waits = new Map(shown.map((t) => [`${t.dirId}/${t.name}`, t.waitingOnYou ?? 0]));
  const asked = new Set(qa.teams.flatMap((tm) => tm.cards.map((c) => `${tm.id}|${c.q.id}`)));
  const covered = (c) => {
    const k = `${c.dirId}/${c.team}`;
    return c.decision ? (held.get(k) ?? 0) >= (waits.get(k) ?? Infinity) : asked.has(`${k}|${c.stream}:${c.key}`);
  };
  // a card's deliverable and epic (its own directory's backlog only), its lane, and where it sits (team, or lane), once each
  const chains = new Map();
  const chainFor = (c) => {
    if (!chains.has(c.id)) chains.set(c.id, c.dirId === dir ? chainOf(c, bl) : { deliverable: null, epic: null });
    return chains.get(c.id);
  };
  const epicKey = (c) => chainFor(c).epic?.key ?? NONE;
  const delivKey = (c) => chainFor(c).deliverable?.key ?? NONE;
  const laneOf = (c) => (team ? laneOfCard(c, allLanes, team) : "top");
  const whereOf = (c) => {
    const l = laneOf(c);
    const ln = l === "top" ? null : allLanes.find((x) => x.stream === l);
    return ln ? { id: `kl:${l}`, name: ln.child ? ln.child.split("/").pop() : l, kind: "lane", tip: `lane · runs ${l}${ln.child ? "" : " (not confirmed)"}`, order: 1 }
      : { id: `kt:${c.dirId}/${c.team}`, name: c.team, kind: "team", tip: `team ${c.team}`, order: 0 };
  };
  // `only`: the stream filter ({team: "dir/name", stream}), set from the item sheet's Stream row (Amendment 1; the web's List chip)
  const scopeOk = (c) => (!only || (`${c.dirId}/${c.team}` === only.team && c.stream === only.stream)) && (!lane || laneOf(c) === lane) && (!scopeEpic || epicKey(c) === scopeEpic) && (!scopeDeliv || delivKey(c) === scopeDeliv);
  // per card: its epic and inherited size (its own directory's backlog only), queue row and message count, as the web's cardOut
  const queueOf = (c) => {
    const k = `${c.dirId}/${c.team}`;
    if (!queue.has(k)) queue.set(k, new Map(queueRows(slices.get(`queue:${k}`)?.body ?? [], now).map((q) => [q.item, q])));
    return queue.get(k).get(`${c.stream}/${c.key}`) ?? null;
  };
  const plain = (c) => {
    const { deliverable, epic } = chainFor(c);
    const where = whereOf(c);
    const q = c.column === "next" && !c.decision && !c.changed ? queueOf(c) : null;
    const x = {
      size: c.size ?? deliverable?.size ?? "",
      sizeFrom: c.size ? "" : deliverable?.key ?? "",
      epic: epic ? { key: epic.key, rank: epic.rank ?? 1e9, name: epic.title ?? epic.key, tip: `epic ${epic.key}${epic.size ? ` · size ${epic.size}` : ""}` } : null,
      msgs: c.decision || c.changed ? null : slices.get(`item-msgs:${c.dirId}/${c.team}`)?.body?.counts?.[c.key] ?? null,
      queue: q ? queueFlag(q, now) : null,
      where,
      // the item sheet's links (board-kanban.js panel): its team or lane, its deliverable and epic, each opening its own sheet
      group: { key: where.id, text: where.name, label: where.kind === "lane" ? "Lane" : "Team" },
      parent: deliverable ? { key: `bl:${deliverable.key}`, text: deliverable.title ?? deliverable.key, log: itemLogId(dir, deliverable.work_id) } : null,
      epicLink: epic ? { key: `bl:${epic.key}`, text: epic.title ?? epic.key, log: itemLogId(dir, epic.work_id) } : null,
    };
    return card(c, x);
  };
  const card = (c, x) => ({
    key: c.id,
    mark: markText(c.mark),
    // the item sheet's selection: a plan item only (a decision card answers in Q&A, a changed stream has no single item)
    sel: c.decision || c.changed ? null : { key: c.id, dirId: c.dirId, team: c.team, stream: c.stream, item: c.key, title: c.title, owner: c.owner || "", log: itemLogId(c.dirId, c.workId), rows: rowsOf(c, x, now), group: x.group, parent: x.parent, epic: x.epicLink, note: c.note || "", goal: c.goal || "", ext: c.extId || c.extUrl ? { id: c.extId ?? c.extUrl, url: httpUrl(c.extUrl) } : null },
    title: c.title,
    id: c.decision ? "" : c.key,
    ref: c.decision ? "" : `${streamName(c.stream)} / ${c.key}`,
    team: c.team,
    owner: c.owner || "",
    workId: c.workId ?? "",
    ...x,
    blockedOpen: c.blockedOpen ?? 0,
    orch: c.state === "waiting_orch",
    done: c.column === "done",
    blocked: c.state === "blocked" ? `blocked${c.since ? ` ${age(now - c.since)}` : ""}` : "",
    ask: c.column === "waiting",
  });
  // the web's order inside a column: team (herdr has no lanes yet), then epic by backlog rank, then epic key, none last
  const rank = (c) => c.epic?.rank ?? Infinity;
  const order = (a, b) => a.where.order - b.where.order || a.where.name.localeCompare(b.where.name) || (rank(a) - rank(b) || 0) || (a.epic?.key ?? "~").localeCompare(b.epic?.key ?? "~");
  // the served heads stay under a Filter streams text, as the web's gate (board-kanban.js) leaves the query out
  // a chip switched off narrows only when its team is in scope
  const narrowed = running.some((t) => hide.has(keyOf(t))) || Boolean(only) || Boolean(owner) || blockedOnly || changesOnly || Boolean(scopeEpic) || Boolean(lane);
  const filtered = narrowed || query.trim() !== "";
  const served = narrowed ? {} : servedCounts(slices, now, team);
  const sels = new Map(); // every drawn card's sheet selection, by card key: the backlog sheet's plan items open these
  const columns = COLUMNS.map(([key, label]) => {
    const all = (cells[key] ?? []).filter((c) => ownerOk(c.owner || "") && scopeOk(c));
    let cards;
    if (key === "waiting") {
      const marked = new Set(all.map((c) => `${c.dirId}/${c.team}|${c.stream}:${c.key}`));
      // Under a filter text an item's question stays only with its item on the board; a decision stays, as boardOf keeps it.
      // In a scope the same: an item's question with its item; a decision as its team's decision card would be scoped.
      const inScope = new Set(running.map(keyOf));
      const decisionOk = (id) => scopeOk({ id: `${id}|decision`, dirId: id.split("/")[0], team: id.split("/").slice(1).join("/"), stream: "", decision: true });
      const asks = qa.teams.filter((tm) => !hide.has(tm.id) && inScope.has(tm.id)).flatMap((tm) => tm.cards
        .filter((c) => ownerOk(nameOf(c.asker)) && (!changesOnly || marked.has(`${tm.id}|${c.q.id}`))
          && (c.q.kind === "item" ? (!query.trim() && !lane && !scopeEpic && !only) || marked.has(`${tm.id}|${c.q.id}`) : decisionOk(tm.id)))
        .map((c) => ({ qa: c, tm })));
      cards = [...asks, ...all.filter((c) => !covered(c)).map(plain).sort(order)];
    }
    else cards = all.map(plain);
    // every column draws at most its cap (Done today its newest DONE_CAP, then in the web's order) until expanded: each card is
    // elements built on Herdr's UI thread
    const cap = expand.has(key) ? Infinity : key === "done" ? DONE_CAP : COLUMN_CAP;
    const drawn = key === "waiting" ? cards.slice(0, cap) : key === "done" ? cards.slice(0, cap).sort(order) : cards.sort(order).slice(0, cap);
    for (const c of cards) if (c.sel) sels.set(c.key, c.sel); // every card's, drawn or past the cap: a sheet's link opens any
    // the head: the served count, unfiltered (… while a plan is unread and it serves 0); else what the column holds
    const head = served[key] === undefined ? cards.length : served[key] === 0 && served.pending ? "…" : served[key];
    return { key, label, cards: drawn, count: cards.length, head, more: Math.max(0, cards.length - cap), open: expand.has(key), folds: (key === "done" ? DONE_CAP : COLUMN_CAP) < cards.length };
  });
  // The filter row's choices: every running team (a chip never vanishes when switched off), and the holders on the cards in reach.
  const isLane = (t) => Boolean(slices.get(`team-summary:${keyOf(t)}`)?.body?.lane?.parent_team);
  const owners = new Set([...Object.values(cells).flat().map((c) => c.owner), ...qa.teams.flatMap((tm) => tm.cards.map((c) => nameOf(c.asker)))]);
  if (owner) owners.add(owner);
  owners.delete("");
  // The Backlog column (board-kanban.js): left out under Blocked only, Changes only or an owner, as the web's; WAVE_CAP rows a wave
  // until its Show all; folded, a strip. Each row opens its backlog sheet.
  const waves = blockedOnly || changesOnly || owner ? [] : backlogColumn(blBody, scopeDeliv ?? scopeEpic);
  const blTotal = waves.reduce((n, g) => n + g.rows.length, 0);
  const blRow = (r) => ({ id: `bl:${r.key}`, key: r.key, kind: r.kind ?? "", title: r.title, size: r.size ?? "", state: r.state, under: UNDER_WAY.has(r.state) ? STATE_LABEL[r.state] : "", holder: r.holder ?? "", workId: r.work_id ?? "", blockedOpen: r.blocked_by_open ?? 0, log: itemLogId(dir, r.work_id) });
  const backlog = {
    folded: blFold, total: blTotal,
    count: blFold ? 0 : waves.reduce((n, g) => n + (waveAll.has(g.wave) ? g.rows.length : Math.min(WAVE_CAP, g.rows.length)), 0),
    waves: waves.map((g) => ({ wave: g.wave, label: g.label, total: g.rows.length, open: waveAll.has(g.wave), rows: (waveAll.has(g.wave) ? g.rows : g.rows.slice(0, WAVE_CAP)).map(blRow) })),
  };
  // The backlog sheet's model (board-kanban.js panel, a bl: selection), made only for the open one: its rows, parent, children
  // and the plan items under it. Every card the board holds is checked, drawn or not.
  const cardsAll = Object.values(cells).flat().filter((c) => !c.decision && !c.changed);
  const blSheet = (key) => {
    const r = bl.get(key);
    if (!r) return null;
    const under = (c) => { const ch = c.dirId === dir ? chainOf(c, bl) : {}; return c.parent === r.key || ch.deliverable?.key === r.key || ch.epic?.key === r.key; };
    return {
      kind: r.kind ?? "Backlog row", title: r.title, outcome: r.outcome ?? "", note: r.note ?? "",
      rows: [["Key", r.key, true], ["Work id", r.work_id, true], ["Blocked by", r.blocked_by_open > 0 ? `${r.blocked_by_open} open` : null], ["State", STATE_LABEL[r.state] ?? r.state], ["Size", r.size], ["Wave", r.wave], ["Theme", r.theme]].filter(([, v]) => v),
      parent: r.parent && bl.has(r.parent) ? { key: r.parent, text: bl.get(r.parent).title, log: itemLogId(dir, bl.get(r.parent).work_id) } : null,
      kids: [...bl.values()].filter((k) => k.parent === r.key).map((k) => ({ key: k.key, text: `${k.key} · ${k.title}`, log: itemLogId(dir, k.work_id) })),
      items: cardsAll.filter(under).map((c) => ({ id: c.id, text: `${c.key} · ${c.title}`, col: COLUMN_LABEL[c.column] ?? c.column })),
      ext: r.ext_id ? { id: r.ext_id, url: httpUrl(r.ext_url) ?? httpUrl(r.issue?.url) } : null,
      log: itemLogId(dir, r.work_id),
    };
  };
  // The breadcrumb and its next level's menu (board-kanban.js), over every card the board holds before the owner filter.
  const every = Object.values(cells).flat();
  const inLane = every.filter((c) => !lane || laneOf(c) === lane);
  const inEpic = scopeEpic ? inLane.filter((c) => epicKey(c) === scopeEpic) : inLane;
  const blRank = (k) => (k === NONE ? Infinity : bl.get(k)?.rank ?? 1e9);
  const epicName = (k) => (k === NONE ? "No epic" : bl.get(k)?.title ?? k);
  const delivName = (k) => (k === NONE ? "No deliverable" : bl.get(k)?.title ?? k);
  const crumbs = [{ depth: 0, kind: "", name: "All teams" }];
  if (team) {
    crumbs.push({ depth: 1, kind: "team", name: slices.get(`team:${team}`)?.body?.name ?? team.split("/").pop() });
    if (lane) crumbs.push({ depth: 2, kind: lane === "top" ? "" : "lane", name: lane === "top" ? "Top team" : lane });
  }
  if (scopeEpic) crumbs.push({ depth: 3, kind: "epic", name: epicName(scopeEpic) });
  if (scopeDeliv) crumbs.push({ depth: 4, kind: "deliverable", name: delivName(scopeDeliv) });
  const depth = crumbs.at(-1).depth;
  const tally = (list, f) => list.filter(f).length;
  let menu = [];
  let menuHead = "";
  if (depth === 0) {
    menuHead = "Teams";
    menu = [...new Set(every.map((c) => `${c.dirId}/${c.team}`))].sort().map((k) => ({ key: k, kind: "", name: k.split("/").pop(), n: tally(every, (c) => `${c.dirId}/${c.team}` === k), to: "team" }));
  } else if (depth === 1 && allLanes.length > 0) {
    menuHead = "Lanes";
    menu = [{ key: "top", kind: "", name: "Top team", n: tally(every, (c) => laneOf(c) === "top"), to: "lane" },
      ...allLanes.filter((l) => showFinished || l.state !== "done").map((l) => ({ key: l.stream, kind: "lane", name: `${l.stream}${l.child ? "" : " (not confirmed)"}${l.state === "done" ? " (finished)" : ""}`, n: tally(every, (c) => laneOf(c) === l.stream), to: "lane" }))];
  } else if (depth < 3) {
    menuHead = "Epics";
    menu = [...new Set(inLane.map(epicKey))].sort((a, b) => blRank(a) - blRank(b)).map((k) => ({ key: k, kind: "", name: epicName(k), n: tally(inLane, (c) => epicKey(c) === k), to: "epic" }));
  } else if (depth === 3) {
    menuHead = "Deliverables";
    menu = [...new Set(inEpic.map(delivKey))].sort((a, b) => blRank(a) - blRank(b)).map((k) => ({ key: k, kind: "", name: delivName(k), n: tally(inEpic, (c) => delivKey(c) === k), to: "deliv" }));
  }
  // The lane header: one line about the chosen lane, from the manager's streams row and the child team.
  const ln = lane && lane !== "top" ? allLanes.find((l) => l.stream === lane) : null;
  const child = ln?.child ? slices.get(ln.child)?.body : null;
  const memberNames = (body) => (Array.isArray(body?.members) ? body.members : []).filter((id) => typeof id === "string").map((id) => slices.get(id)?.body?.name ?? id.split("/").pop());
  const laneHead = ln ? {
    stream: ln.stream, so: ln.so, members: memberNames(child), branch: ln.branch ? `${ln.branch} → ${ln.base ?? "main"}` : "", state: ln.state,
    report: child?.lane?.reported?.state ? REPORT[child.lane.reported.state] ?? child.lane.reported.state : ln.child ? "" : "Not confirmed",
  } : null;
  // The team and lane sheets (board-kanban.js panel kt:/kl:): members, a lane's branch, state and sub-orchestrator, and its cards.
  const groupSheet = (key) => {
    const l = key.startsWith("kl:") ? allLanes.find((x) => x.stream === key.slice(3)) : null;
    if (key.startsWith("kl:") && !l) return null;
    const body = slices.get(l ? l.child : `team:${key.slice(3)}`)?.body;
    const cs = cardsAll.filter((c) => (l ? laneOf(c) === l.stream : `kt:${c.dirId}/${c.team}` === key));
    return {
      kind: l ? "Lane" : "Team", title: l ? l.stream : body?.name ?? key.split("/").pop(),
      rows: l ? [["Branch", l.branch ? `${l.branch} → ${l.base ?? "main"}` : null, true], ["State", l.state], ["Sub-orchestrator", l.so]].filter(([, v]) => v) : [],
      members: memberNames(body),
      items: cs.map((c) => ({ id: c.id, text: `${c.key} · ${c.title}`, col: COLUMN_LABEL[c.column] ?? c.column })),
    };
  };
  return {
    loaded: qa.loaded, count: qa.count, columns, backlog, blSheet, groupSheet, sels, crumbs, menu, menuHead, laneHead,
    empty: columns.every((c) => c.cards.length === 0) && blTotal === 0,
    filtered, agedCount, showFinished, stale: b.stale,
    // the visit's save covers the teams in scope; Mark all seen shows when anything is marked; since: the oldest last look, or null
    covered: running, anyMarked: [...marks.values()].some((x) => x.changed),
    since: running.length === 0 || running.some((t) => sinceOf(t) === null) ? null : Math.min(...running.map(sinceOf)),
    stream: only ? streamName(only.stream) : "",
    // the arrow keys' grid (D2): each column's drawn cards and rows, in display order, the Q&A cards left out (they are qa.js's)
    // only cards drawn as Buttons (they open a sheet), so a focus handle always has an element to track
    nav: [backlog.waves.flatMap((g) => g.rows.map((r) => r.id)), ...columns.map((c) => c.cards.filter((x) => !x.qa && x.sel && !x.ask).map((x) => x.key))].filter((col) => col.length > 0),
    // the quiet teams shown again, each with its Hide (open drops it, and boardSubs its plan and items); loading until its plan is read
    unstale: visible.filter((t) => t.quiet && open.has(t.id)).map((t) => ({ id: t.id, name: t.name, loading: !slices.has(planIdOf(t)) })),
    teams: running.map((t) => ({ key: keyOf(t), name: t.name, lane: isLane(t), on: !hide.has(keyOf(t)) })),
    owners: [...owners].sort(),
  };
}

// The item sheet's rows, the web item dialog's in its order (board-kanban.js panel): label, value, mono. Empty ones are left out.
// Marker as the card's (blocked since, the Next queue's, waiting on the Orchestrator); Size says when it is the deliverable's.
const COLUMN_LABEL = Object.fromEntries(COLUMNS);
function rowsOf(c, x, now) {
  const marker = c.state === "blocked" ? `▲ blocked${c.since ? ` ${age(now - c.since)}` : ""}` : x.queue ? `${x.queue.glyph} ${x.queue.text}` : c.state === "waiting_orch" ? "◷ waiting on the Orchestrator" : null;
  return [
    ["Work id", c.workId, true],
    ["Blocked by", c.blockedOpen > 0 ? `${c.blockedOpen} open` : null],
    ["Column", COLUMN_LABEL[c.column] ?? c.column],
    ["State", STATE_LABEL[c.state] ?? null],
    ["Marker", marker],
    ["Messages", x.msgs === null ? null : String(x.msgs)],
    ["Size", x.size ? `${x.size}${x.sizeFrom ? " (deliverable)" : ""}` : null],
    ["Stream", c.stream ? streamName(c.stream) : null, true],
    ["Team", x.where.kind === "team" ? null : c.team],
    ["Branch", c.branch, true],
    ["Changes", c.mark ? markText(c.mark) : null],
  ].filter(([, v]) => v);
}

// A timeline entry's time as the web's ("Nov 17, 14:05"); Herdr's runtime has no Intl.
const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
const pad2 = (n) => String(n).padStart(2, "0");
export const whenOf = (ms) => {
  const d = new Date(ms);
  return Number.isFinite(ms) ? `${MONTHS[d.getMonth()]} ${d.getDate()}, ${pad2(d.getHours())}:${pad2(d.getMinutes())}` : "";
};

// The newest timeline entries the sheet shows before Show all, as the web's item-log.js.
export const LOG_PAGE = 20;

// The sheet's work record from the item-log: slice (the web's ItemDescription and ItemLog): "loading" until held, "none" when held
// empty or gone; else the description blocks, the newest entries (LOG_PAGE, or all), links and attachments. Only http(s) is a link.
export function workLog(slices, gone, id, all = false) {
  if (!id) return null;
  const held = slices.get(id);
  const b = held?.body;
  if (!b) return { state: (held !== undefined && !b) || gone?.has(id) ? "none" : "loading" };
  const entries = b.entries ?? [];
  const desc = typeof b.description === "string" && b.description.trim() !== "" ? descriptionBlocks(b.description) : null;
  return {
    state: "ok",
    id: b.id ?? "",
    desc,
    total: entries.length,
    entries: entries.slice(all ? 0 : Math.max(0, entries.length - LOG_PAGE)).reverse(),
    more: b.more ?? 0,
    links: [["Blocked by", b.links?.blocked_by], ["Blocks", b.links?.blocks], ["Children", b.children]].filter(([, ids]) => ids?.length).map(([k, ids]) => [k, ids.join(", ")]),
    attachments: (b.attachments ?? []).map((a) => {
      const url = a.kind === "url" ? httpUrl(a.ref) : null;
      return { url, text: url ? a.title ?? a.ref : a.ref, title: a.title && !url ? a.title : "", facts: [a.kind, a.kind === "url" ? null : a.sha ? a.sha.slice(0, 7) : "uncommitted"].filter(Boolean).join(" · ") };
    }),
  };
}

// The item sheet's owner: the member of that team with the owner's name, as the sidebar draws it (state, word, age, pane); null if
// the team lists no such member.
export function ownerOf(slices, sel, now) {
  if (!sel?.owner) return null;
  const find = (list) => list.reduce((hit, t) => hit ?? (`${t.dir}/${t.name}` === `${sel.dirId}/${sel.team}` ? t : find(t.lanes)), null);
  return find(sidebarModel(slices, now).teams)?.members.find((m) => m.name === sel.owner) ?? null;
}

// The item sheet's log: one read of its team's plan changes (newest 200), on the card's click, never on render or poll. `exec` is
// the runner's (ah.js: herdr's exec, run where ah runs; passed in, so this file stays free of herdr). `read` (link-model.js Link
// read) asks ahd's bridge the same poll first, so no ah starts while the bridge is up. Resolves to historyOf rows (newest first),
// or { error }.
export async function readItemLog(exec, sel, sleep = null, read = null) {
  const id = planChangesId(sel.dirId, sel.team);
  let text = null;
  try {
    if (read) text = await read(`/ui/v1/view?${eventsQuery({ sub: id, since: 0, epoch: null, wait: "0" })}`, 15000);
    if (text === null) {
      const r = await within(exec("ah", ["view", "events", "--sub", id, "--since", "0", "--wait", "0"], { timeout: 15000 }), sleep, 15000);
      if (r.code !== 0) return { error: "ahd not running" };
      text = r.stdout;
    }
  } catch (e) {
    return { error: e?.down ?? (late(e) ? NO_ANSWER : "ah is not on Herdr's PATH") };
  }
  try {
    return { rows: itemLog(JSON.parse(text), sel) };
  } catch {
    return { error: "Could not read the log" };
  }
}

export const itemLog = (reply, sel) =>
  historyOf((reply.slices ?? []).find((s) => s.id === planChangesId(sel.dirId, sel.team))?.body?.rows?.filter((r) => r.item === sel.item) ?? [], sel.stream, said);
