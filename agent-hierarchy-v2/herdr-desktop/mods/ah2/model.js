// The ah2 mod's data side: no herdr or gpui imports, so the fixture render (herdr-desktop/preview/render.mjs) runs the same code.
// Input: the slices `ah view events` serves (spec-herdr-desktop.md §4). Output: plain rows the views draw.
// The Q&A fields are read only by the web's own questions-model.js (shipped beside this file as a byte-identical copy of web/ui's, kept honest by a test), spec P3.1.
import { questionsId, attentionOf, defaultAt, startPick, answerBody, dismissBody, replyState, threadOf } from "./questions-model.js";
import { itemDescId, roleOf } from "./plans-model.js";
import { paneOf } from "./target-model.js";
import { goalOf, ownGoal, memberLabel, teamLabel, realNameNote, displayNameOf, sparksOf, reportsBack } from "./select.js";
import "./intl-shim.js"; // before flow-model.js's graph-world.js, which needs Intl when it loads
import { activityId, jobsUnder, subagentsUnder, underOf } from "./flow-model.js";

// State words and glyphs, as the web UI says them (web/ui/select.js STATE_WORD). `tone` is the Herdr theme() token.
export const STATES = {
  working: { glyph: "●", word: "Working", tone: "working" },
  running: { glyph: "●", word: "Running", tone: "working" },
  idle: { glyph: "○", word: "Idle", tone: "idle" },
  blocked: { glyph: "❚❚", word: "Blocked", tone: "blocked" },
  gone: { glyph: "×", word: "Gone", tone: "unknown" },
  unknown: { glyph: "?", word: "No signal", tone: "unknown" },
  done: { glyph: "✓", word: "Done", tone: "done" },
};

// Sitrep regions in the order the overlay shows them (spec §4.1), keyed by the slice's `list` names.
export const REGION_ORDER = ["waiting_on_you", "blocked", "in_progress", "up_next", "completed"];

// One /events reply into the store (a Map of slice id -> slice). Slices are replaced whole (web/ui/store.js apply).
// Returns true when anything changed; on resync or a new epoch the store is emptied and the cursor reset.
// `st.gone` holds the ids a reply removed (and no later reply brought back), so a vanished team reads as closed.
export function applyReply(st, reply) {
  st.gone ??= new Set();
  if (reply.resync || (st.epoch !== null && reply.epoch !== st.epoch)) {
    st.slices.clear();
    st.gone.clear();
    st.epoch = reply.epoch;
    st.seq = 0;
    return true;
  }
  st.epoch = reply.epoch;
  st.seq = reply.seq;
  for (const s of reply.slices ?? []) {
    st.slices.set(s.id, s);
    st.gone.delete(s.id);
  }
  for (const id of reply.removed ?? []) {
    st.slices.delete(id);
    st.gone.add(id);
  }
  return (reply.slices?.length ?? 0) + (reply.removed?.length ?? 0) > 0;
}

// A window loads only until the first reply that answered its whole sub set (v.ready, set by the poll, never cleared); after
// that a grown sub set keeps the last model drawn while it catches up, so nothing blinks.
export const loadingOf = (v, loaded) => v.status === "loading" || !loaded || !v.ready;

export function age(ms) {
  if (!(ms >= 0)) return "";
  const m = Math.floor(ms / 60000);
  if (m < 1) return "now";
  if (m < 60) return `${m}m`;
  const h = Math.floor(m / 60);
  return h < 24 ? `${h}h` : `${Math.floor(h / 24)}d`;
}

const since = (iso, now) => {
  const t = typeof iso === "number" ? iso : Date.parse(iso ?? "");
  return Number.isFinite(t) ? age(now - t) : "";
};

const body = (slices, id) => slices.get(id)?.body ?? null;
export const memberName = (key) => key.slice(key.lastIndexOf("/") + 1).replace(/^@/, "");

// ah2-73: ahd serves pane_id only where a click may land (a live member, and the orchestrator unless proven dead); an idle
// orchestrator reads "unknown" and must stay clickable. The sidebar's row and the Graph's member (ah2-103) both read it here.
export const memberPane = (key, m) =>
  m && (m.liveness === "live" || (key.endsWith("/@orchestrator") && m.liveness !== "proven_dead")) ? m.pane_id ?? null : null;

// A member's row. An orchestrator that is live but idle/unknown reads "Working" while its turn is open (turn_open) and "Running"
// between turns, as in the web UI (select.js orchestratorRuns).
function memberRow(key, m, now) {
  const name = memberName(key);
  let state = STATES[m.state] ? m.state : "unknown";
  if (key.endsWith("/@orchestrator") && m.liveness === "live" && (state === "idle" || state === "unknown")) state = m.turn_open === true ? "working" : "running";
  const s = STATES[state];
  const what = m.item || (state === "blocked" ? "blocked" : s.word.toLowerCase());
  return { key, name, state, ...s, what, age: since(m.state_at, now), at: Date.parse(m.state_at ?? "") || 0,
    pane: memberPane(key, m), served: typeof m.role === "string" ? m.role : "", column: Number.isInteger(m.column) ? m.column : null };
}

// ah2-207 Amendment 1: a member's role, for its icon and hue. The served role; else the orchestrator (a lane's is its sub-orchestrator);
// else the role its name carries (plans-model.js roleOf, as the Board reads it); else "" (the generic icon, the neutral hue).
const roleIn = (mb, lane) => mb.served || (mb.name === "orchestrator" ? (lane ? "sub-orchestrator" : "orchestrator") : roleOf(mb.name));

// ah2-207 D1: one team's tree. Its orchestrator is the root, with the other members, then the lanes, under it; a lane is a node
// that draws its header row, with the lane's own tree under it. A team without an orchestrator row has its members as roots.
// A member's own alerts ride on its node. Node: { kind: "member" | "lane", key, depth, role, children, ...the member row or `team` }.
// ah2-225 B1/B3: a member's jobs and subagents (`tm.under`, sidebarModel's) are leaf nodes first under it, { kind: "job" | "subagent",
// key, depth, item }; those whose owner is no one on the team lead the team's own rows, under its header.
// ah2-220 Amendment 2: a team with a parent team is a lane even drawn at the top level (its parent not running).
export function teamTree(tm, depth = 0, lane = Boolean(tm.parent)) {
  const orch = tm.members.find((mb) => mb.name === "orchestrator");
  const extras = (key, d) => (tm.under?.get(key) ?? []).map((x) => ({ kind: x.kind, key: x.key, depth: d, role: x.role ?? "", item: x, alerts: [], children: [] }));
  const node = (mb, d) => ({ kind: "member", ...mb, depth: d, ...memberLabel(mb.key, mb.name, roleIn(mb, lane), lane ? tm.name : null), alerts: tm.alerts.filter((a) => a.member === mb.key), children: extras(mb.key, d + 1) });
  const under = (d) => [
    ...tm.members.filter((mb) => mb !== orch).map((mb) => node(mb, d)),
    ...tm.lanes.map((l) => ({ kind: "lane", key: `lane:${l.id}`, depth: d, team: l, children: teamTree(l, d + 1, true) })),
  ];
  const stray = extras(`team:${tm.id}`, depth);
  if (!orch) return [...stray, ...under(depth)];
  const root = node(orch, depth);
  return [...stray, { ...root, children: [...root.children, ...under(depth + 1)] }];
}

// The rows a tree draws, in order, with what the connectors need: a row at depth d > 0 draws d columns, its `rails` (one per level
// between the root and it, true where that ancestor's line runs on past the row to a later sibling) and its own elbow; `last`, whether
// it is the last of its siblings (the elbow stops there). `open(node)` says whether a node with
// children shows them. Deep rows keep only the INDENT_CAP innermost columns, so the name keeps its room in a narrow pane.
export const INDENT_CAP = 4;
export function treeRows(roots, open) {
  const out = [];
  const walk = (nodes, rails) => nodes.forEach((n, i) => {
    const last = i === nodes.length - 1;
    const kids = n.children.length > 0;
    const shown = kids && open(n);
    out.push({ node: n, rails: n.depth > 0 ? rails.slice(1 - INDENT_CAP) : [], last, kids, open: shown });
    if (shown) walk(n.children, n.depth > 0 ? [...rails, !last] : []);
  });
  walk(roots, []);
  return out;
}

// ah2-239: the briefs in flight to each member, by member id, from the Graph's own rule (select.js sparksOf, over every running team's
// open list): the first one (by id) is the one its row's dot draws. D6: a folded lane shows the one to its Sub-Orchestrator.
export function inFlightTo(slices) {
  const out = new Map();
  for (const { t } of runningTeams(slices)) {
    for (const s of sparksOf(slices, { body: { dir_id: t.dir_id, name: t.name } })) if (!out.has(s.to)) out.set(s.to, s);
  }
  return out;
}
// ah2-265 D1: the reports waiting to be read from each member, by member id, from the shared rule (select.js reportsBack): the first
// one (by id) is the one its row's dot draws.
export function reportsFrom(slices) {
  const out = new Map();
  for (const { t } of runningTeams(slices)) {
    for (const s of reportsBack(slices, { body: { dir_id: t.dir_id, name: t.name } })) if (!out.has(s.from)) out.set(s.from, s);
  }
  return out;
}
// The member a tree row's dots belong to: its own, or a folded lane's Sub-Orchestrator; null at depth 0 (no connector) and on a job's
// or subagent's row.
function dotMember(r) {
  const n = r.node;
  if (n.depth === 0) return null;
  return n.kind === "member" ? n : n.kind === "lane" && !r.open ? n.children.find((c) => c.kind === "member" && c.key.endsWith("/@orchestrator")) ?? null : null;
}
// The dot a tree row draws (ah2-239 D1/D6): { key, review, role, column } of the brief in flight to its member, or to a folded lane's
// Sub-Orchestrator; null at depth 0 (no connector), on a job's or subagent's row, or with nothing in flight.
export function rowDot(r, inFlight) {
  const to = dotMember(r);
  const s = to && inFlight.get(to.key);
  return s ? { key: s.id, review: s.review === true, role: to.role, column: to.column ?? null } : null;
}
// ah2-265 D1/D4: the report a tree row's member has filed and its briefer not yet read, by the same rows as rowDot: { key, role,
// column }, the key told apart from a brief's on the one clock.
export function rowReport(r, reports) {
  const from = dotMember(r);
  const s = from && reports.get(from.key);
  return s ? { key: `back|${s.id}`, role: from.role, column: from.column ?? null } : null;
}
// The dot's track (ah2-239 D2), in main.js treeLead's coordinates: down the row's own elbow from its parent's rail, then across to
// the disclosure (a row with children) or, at a leaf, on through the carried elbow to the icon. Lines are 1 px; the track is their centre.
export function rowTrack(r, step) {
  const x = r.rails.length * step + 7.5;
  const end = (r.rails.length + 1) * step + (r.kids ? 0 : step - 2);
  return [["M", x, 0], ["L", x, 14.5], ["L", end, 14.5]];
}

// ah2-252: what the pane's tree draws, built only when an input changes (graph-view.js graphOf's kind), so a dot frame computes only
// dotAt. `key`: the slices' generation (bumped by the poll), the age clock (the 30 s timer's count: ages are the build's, as fresh
// as that timer keeps them), the folds and the settings; never the time. `shown(teams)` is the settings' filter, `open(tm)` a team's
// fold rule, `track(r, back)` a row's dot track (back: reversed). Per team: { tm, rows: [{ r, d (rowDot), pts (track, only with a
// dot), b (rowReport), bpts (the track reversed, only with a report), parent }] }.
export function paneTree(memo, key, slices, now, { shown, open, track }) {
  if (memo.key?.length === key.length && key.every((k, i) => k === memo.key[i])) return memo.m;
  memo.builds = (memo.builds ?? 0) + 1;
  const m = sidebarModel(slices, now);
  const inFlight = inFlightTo(slices);
  const reports = reportsFrom(slices);
  const teams = shown(m.teams);
  const of = (tm) => {
    const tree = teamTree(tm);
    // a job's or subagent's parent row, for its tooltip: the member it is a child of; none under the team's header
    const parentOf = new Map();
    const walk = (list, p) => list.forEach((x) => { if (x.item) parentOf.set(x.key, p); walk(x.children, x.kind === "member" ? x.name : null); });
    walk(tree, null);
    return { tm, rows: treeRows(tree, open(tm)).map((r) => {
      const d = rowDot(r, inFlight);
      const b = rowReport(r, reports);
      return { r, d, pts: d && track(r), b, bpts: b && track(r, true), parent: parentOf.get(r.node.key) ?? null };
    }) };
  };
  memo.key = key;
  return (memo.m = { m, teams, trees: teams.map(of) });
}

// Text the user typed as one inert line, and a summary's goal: select.js's (a byte-identical copy of web/ui's), shared with the web UI.
export { inert } from "./select.js";

// ah2-225: the jobs: and subagents: slices of every running team, the tree's one added subscription (Amendment 1).
export const activitySubs = (slices) => runningTeams(slices).flatMap(({ id }) => [activityId("jobs", id), activityId("subagents", id)]);
// ah2-239: what the tree's dots read, slices the Graph already polls (graph-model.js graphSubs): the open list, and the topology (reviews).
export const dotSubs = (slices) => runningTeams(slices).flatMap(({ id }) => [`list:open:team:${id}`, `topology:${id}`]);

// Running teams of every dir, in list order, with their summary body; closed and ended teams left out.
function runningTeams(slices) {
  const out = [];
  for (const t of body(slices, "list:teams")?.teams ?? []) {
    const id = `${t.dir_id}/${t.name}`;
    const b = body(slices, `team-summary:${id}`);
    if (b && b.running && !b.closed_mark) out.push({ t, id, b });
  }
  return out;
}

// The sidebar: running teams of every dir, lanes nested under their parent (same dir), closed and ended teams left out.
export function sidebarModel(slices, now) {
  const teams = [];
  for (const { t, id, b } of runningTeams(slices)) {
    // ah2-220: each row's label and tooltip (select.js memberLabel): a lane's orchestrator reads Sub-Orchestrator; `name` stays the key
    const lane = b.lane?.parent_team ? b.name ?? t.name : null;
    const members = Object.entries(b.member_states ?? {})
      .map(([k, m]) => memberRow(k, m, now))
      .map((mb) => (({ label, title }) => ({ ...mb, label, title }))(memberLabel(mb.key, mb.name, mb.served, lane)))
      .sort((x, y) => (x.name === "orchestrator" ? -1 : y.name === "orchestrator" ? 1 : 0));
    // Orchestrator-audience stuck alerts are the orchestrator's, not the user's (select.js summaryRow).
    // `member`: the member slice id a member's own alert names; null for a team-level one (Waiting on you), drawn with the team header (ah2-133 D4).
    const alerts = (b.alerts ?? []).filter((a) => a.audience !== "orchestrator").map((a) => ({ key: `${a.kind}|${a.since ?? a.text ?? a.title}`, kind: a.kind, text: a.text || a.title, age: since(a.since, now), at: Date.parse(a.since ?? "") || 0, member: a.member ?? null }));
    // ah2-53: only the team's own goal; a lane's inherited one is already on its parent's row above
    const own = ownGoal(goalOf(b), b.name ?? t.name);
    teams.push({ id, dir: t.dir_id, name: b.name ?? t.name, label: teamLabel({ ...b, name: b.name ?? t.name }), real: realNameNote({ ...b, name: b.name ?? t.name }), parent: b.lane?.parent_team ?? null, needsYou: b.waiting_on_you ?? 0, blockers: b.waiting_blockers ?? 0, working: b.working ?? 0, blocked: b.blocked ?? 0, members, alerts, lanes: [], goal: own?.text ?? null, goalFull: own?.full ?? null });
  }
  // ah2-225: every team's jobs and subagents under their owners' keys (one global map: a member's key names its team), a stray one
  // under its team's header key; a run in a lane's slice and its parent's is drawn once, in the lane (flow-model.js subagentsUnder)
  const src = (kind) => teams.map((tm) => ({ rows: body(slices, activityId(kind, tm.id))?.rows ?? [], owners: new Map(tm.members.map((mb) => [mb.name, mb.key])), header: `team:${tm.id}` }));
  const under = underOf(jobsUnder(src("jobs")), subagentsUnder(src("subagents"), now));
  for (const t of teams) t.under = under;
  const byName = new Map(teams.map((t) => [`${t.dir}/${t.name}`, t]));
  const top = [];
  for (const t of teams) {
    const parent = t.parent && byName.get(`${t.dir}/${t.parent}`);
    if (parent && parent !== t) parent.lanes.push(t);
    else top.push(t);
  }
  // Every "Needs you" counts what Q&A lists (waiting_on_you), not the served needs_you, which also counts blocked rows Q&A never
  // shows (a badge over an empty Q&A, atp-1724); those show as "blocked" (atp-1736), the served `blocked`, as the web's (atp-1749).
  const needsYou = teams.reduce((n, t) => n + t.needsYou, 0);
  return { teams: top, count: teams.length, needsYou, blocked: teams.reduce((n, t) => n + t.blocked, 0) };
}

// The live terminal a member row focuses: the one a live member registered from (the orchestrator's own, which ah never spawned
// and so never labelled; a gone or stale member serves no pane), else, E3 (docs/herdr-desktop/evidence.md), the one live
// terminal labelled ah.name=<member>; null otherwise. On a herdr machine `target` (target-model.js), ah's pane id is the address with
// the machine's prefix stripped; the address returned is the one terminals() gave.
export function terminalFinder(terms, target = null) {
  const live = new Map();
  const byName = new Map();
  for (const term of terms) {
    if (term.state !== "live") continue;
    live.set(paneOf(target, term.address), term.address);
    const n = term.labels?.["ah.name"];
    if (n) byName.set(n, byName.has(n) ? null : term.address);
  }
  return (mb) => live.get(mb.pane) ?? byName.get(mb.name) ?? null;
}

// A team header's counts: needs you first, then working and blocked; zeros say nothing.
export function counts(t) {
  return [t.needsYou && `${t.needsYou} needs you`, t.working && `${t.working} working`, t.blocked && `${t.blocked} blocked`].filter(Boolean).join(" · ");
}

// A Needs-you count as a badge reads it: the total, with "!" when a blocker is among them.
export const needsMark = (n, blockers = 0) => `${n}${blockers > 0 ? "!" : ""}`;
// " · B blocked" after a Needs you count, nothing at zero (the web's blockedText, atp-1749).
export const blockedText = (b) => (b > 0 ? ` · ${b} blocked` : "");

// The desk (layouts/wrangl.json): what its status segment, peek drawer and Q&A tab show, from the one view:summary poll. Totals over
// every running team and lane; `needing` the teams with anything needing you; `newest` the latest member state change or alert.
// Plain data with times, not ages, so the published copy changes only when the data does.
export function deskModel(slices, now) {
  const m = sidebarModel(slices, now);
  const all = [];
  const walk = (list) => list.forEach((t) => { all.push(t); walk(t.lanes); });
  walk(m.teams);
  let newest = null;
  const seen = (at, text, team) => { if (at > (newest?.at ?? 0)) newest = { at, text, team }; };
  for (const t of all) {
    for (const mb of t.members) seen(mb.at, mb.what === mb.word.toLowerCase() ? `${mb.label} ${mb.what}` : `${mb.label} ${mb.word.toLowerCase()}: ${mb.what}`, t.label);
    for (const a of t.alerts) seen(a.at, `⚠ ${a.text}`, t.label);
  }
  return {
    working: all.reduce((n, t) => n + t.working, 0),
    blocked: all.reduce((n, t) => n + t.blocked, 0),
    needsYou: m.needsYou,
    blockers: all.reduce((n, t) => n + t.blockers, 0),
    waiting: qaModel(slices, now).count,
    needing: all.filter((t) => t.needsYou > 0).map((t) => ({ id: t.id, name: t.label, n: t.needsYou, blockers: t.blockers, blocked: t.blocked })),
    newest,
  };
}

// The overlay: one team's sitrep regions. null while the slice has not arrived; { closed: true } once the team is gone,
// including when its summary or sitrep slice was removed (`gone`, applyReply's st.gone).
export function sitrepModel(slices, id, now, gone = null) {
  const sum = body(slices, `team-summary:${id}`);
  if (sum && (sum.closed_mark || !sum.running)) return { closed: true };
  if (gone?.has(`team-summary:${id}`) || gone?.has(`sitrep:${id}`)) return { closed: true };
  const b = body(slices, `sitrep:${id}`);
  if (!b) return null;
  const labels = new Map((b.regions ?? []).map((r) => [r.list, r]));
  const regions = REGION_ORDER.filter((k) => labels.has(k)).map((k) => {
    const r = labels.get(k);
    const rows = (b[k] ?? []).map((x) => sitrepRow(k, x, now));
    const total = r.total ? b[r.total] ?? rows.length : rows.length;
    return { list: k, label: r.label, optional: r.optional, rows, total, more: Math.max(0, total - rows.length) };
  }).filter((r) => !r.optional || r.rows.length > 0);
  // `lane`: a lane closes with its parent team, never alone, so its sitrep offers no Dismiss
  return { name: sum ? teamLabel(sum) : id.split("/")[1], realName: sum?.name ?? id.split("/")[1], real: sum ? realNameNote(sum) : null, displayName: displayNameOf(sum), lane: Boolean(sum?.lane?.parent_team), needsYou: sum?.waiting_on_you ?? 0, blocked: sum?.blocked ?? 0, regions };
}

function sitrepRow(list, x, now) {
  if (list === "completed") {
    if (x.kind === "item") return { key: "✓", title: x.title, who: since(x.done_at, now), done: true };
    const what = x.kind === "ship" ? "Shipped" : "Merged";
    return { key: "✓", title: `${what} ${x.ref ?? ""} · ${(x.sha ?? "").slice(0, 7)}`.trim(), who: since(x.at, now), done: true };
  }
  if (x.kind === "decision") return { key: x.key, title: x.title, who: since(x.since, now), question: true };
  const key = (x.key ?? "").split(":").pop();
  if (list === "up_next") {
    const who = x.stall_at ? `queued ${since(x.queued_since, now)} · no one` : x.role || "next";
    return { key, title: x.title, who };
  }
  // a row waiting on the user names who asked (its team is the section's), never the member who last held it
  const who = [x.asker?.split("@")[0] || x.lane || x.holder || x.role, since(x.since, now)].filter(Boolean).join(" · ");
  return { key, title: x.title, who };
}

// ---- Q&A (spec P3.1-P3.3) ----

// The Q&A window's extra subscriptions: one `questions:` slice per running team, and with `descs` the small `item-desc:` slice of each
// waiting item registered as work, whose description its card shows (the feed draws none, so it leaves them out). The sidebar copy never adds these.
const descId = (t, item) => itemDescId(t.dir_id, item.work_id);
export const qaSubs = (slices, descs = true) => runningTeams(slices).flatMap(({ t, id }) =>
  [questionsId(`team:${id}`), ...(descs ? body(slices, questionsId(`team:${id}`))?.items ?? [] : []).map((i) => descId(t, i)).filter(Boolean)]);

const HISTORY_CAP = 20;

// A question-log row's first reply as its History row shows it (spec qa-history-replies §8): its time (`reply_at`), and its head
// folded to one line (`reply`, when the slice row carries one; the questions lane keeps only the time in `recent` today).
export const historyReply = (x) => ({
  replyAt: x?.reply_at || null,
  preview: typeof x?.reply === "string" && x.reply.trim() !== "" ? x.reply.replace(/\s+/g, " ").trim() : null,
});

// The Q&A window: For you cards grouped by team (oldest first within a team), History across teams (newest first, at most 20),
// and the count, the sum of `waiting_on_you` (never `needs_you`, which also counts blocked members). A team with nothing open
// or no slice yet has no group. `loaded` is false until the team list has arrived.
export function qaModel(slices, now) {
  const all = runningTeams(slices);
  const teams = [];
  const history = [];
  let count = 0;
  for (const { t, id, b } of all) {
    count += b.waiting_on_you ?? 0;
    const teamId = `team:${id}`;
    const raw = body(slices, questionsId(teamId));
    const q = attentionOf(raw);
    if (!q) continue;
    // the web's recentRow drops `header` and the first reply's time and head; History shows them, so they are carried from the raw
    // row by id. The question log's rows come with or without an open run.
    const raws = new Map((raw.recent ?? []).map((x) => [x.id, x]));
    for (const r of q.recent) {
      const x = raws.get(r.id);
      history.push({ ...r, header: x?.header ?? null, ...historyReply(x), team: b.name ?? t.name, age: since(r.at, now), hierDir: raw.dir ?? null, dirId: t.dir_id ?? null });
    }
    // no run is no reason to hide: a waiting item (a groom's question, a solo Orchestrator's park) has no run behind it
    if (q.open.length === 0 && q.hidden === 0) continue;
    teams.push({
      id, name: b.name ?? t.name, label: teamLabel({ ...b, name: b.name ?? t.name }), lane: b.lane?.parent_team ?? null, run: q.run,
      cards: q.open.map((c) => qaCard(teamId, q.run, c, now, c.workId ? body(slices, itemDescId(t.dir_id, c.workId))?.description : null)),
      hidden: q.hidden, truncated: q.truncated,
    });
  }
  history.sort((x, y) => (y.at ?? -Infinity) - (x.at ?? -Infinity));
  return { loaded: slices.has("list:teams"), count, teams, history: history.slice(0, HISTORY_CAP), empty: teams.length === 0 };
}

// The running teams the History filter offers and reads (qa-history-model.js historyDirs): name, dirId, and the hierarchy dir its
// questions: slice names (the ah fallback's --dir).
export const historyTeams = (slices) => runningTeams(slices).map(({ t, id, b }) =>
  ({ name: b.name ?? t.name, dirId: t.dir_id ?? null, hierDir: body(slices, questionsId(`team:${id}`))?.dir ?? null }));

// The Q&A window's list never blanks on a refresh (atp-1708). `qa` is {shown, pending}: `shown` the slices drawn (a copy, so
// ages stay live), `pending` whether the live slices give a different list ("Updates available"). Live slices that are not a
// whole answer yet (`ready` false: first load, a resync that cleared them) change nothing. `take` (auto-update on, or the
// Updates available click) takes them at once.
const qaSig = (m) => JSON.stringify([m.count, m.teams, m.history], (k, x) => (k === "age" ? undefined : x));
export function qaRefresh(qa, slices, now, take, ready) {
  const next = qaModel(slices, now);
  if (!ready || !next.loaded) return qa;
  if (!qa?.shown || take) return { shown: new Map(slices), pending: false };
  return { shown: qa.shown, pending: qaSig(qaModel(qa.shown, now)) !== qaSig(next) };
}

// One For you card. `key` keeps a card's local state (selection, sending) across polls: team + run + id, or the item key and the
// time it was asked, so a re-asked item is a new card and not the old one's recorded answer.
// `recommended` is the default's option index, -1 when it names none or several; `start` is the pick the card opens with.
function qaCard(teamId, run, c, now, desc = null) {
  const item = c.kind === "item";
  return {
    key: item ? `${teamId}|item|${c.id}@${c.since}` : `${teamId}|${run}|${c.id}`,
    teamId, run, q: c, kind: item ? "item" : "decision", cls: c.cls,
    header: c.item, text: c.text, asker: c.asker, age: since(c.at, now),
    options: c.options.map((o) => o.label), multi: c.multi, more: c.more,
    recommended: item ? -1 : defaultAt(c), start: startPick(c),
    desc: typeof desc === "string" && desc.trim() !== "" ? desc : null,
  };
}

// What a folded description shows: its blocks (plans-model descriptionBlocks) up to about `lines` lines of `cols` characters, the
// last row cut with "…", and `cut` when anything was left out. Herdr cannot measure text, so the lines are estimated.
export function descFold(blocks, lines = 4, cols = 80) {
  const rowsOf = (b) => (b.kind === "list" || b.kind === "olist" ? b.items : b.text.split("\n"));
  const height = (r) => Math.max(1, Math.ceil(r.length / cols));
  if (blocks.flatMap(rowsOf).reduce((n, r) => n + height(r), 0) <= lines) return { blocks, cut: false };
  const out = [];
  let left = lines;
  for (const b of blocks) {
    const take = [];
    for (const r of rowsOf(b)) {
      if (left <= 0) break;
      take.push(height(r) > left ? `${r.slice(0, left * cols - 1)}…` : r);
      left -= height(r);
    }
    if (take.length > 0) out.push(b.kind === "list" || b.kind === "olist" ? { ...b, items: take } : { ...b, text: take.join("\n") });
    if (left <= 0) break;
  }
  // a fold never ends on a heading with nothing under it
  while (out.length > 1 && out.at(-1).kind === "head") out.pop();
  return { blocks: out, cut: true };
}

// Whether Send can go for this pick (a multi with nothing on, or an item with no pick, cannot).
export const canSend = (card, pick) => answerBody(card.teamId, card.run, card.q, pick) !== null;

// How an open History thread starts (ah2-58 S2 ruling): a thread with no reply in its own turn says what it has instead, and when
// it was answered in a later turn its Later replies start open; every other fold starts closed.
export function threadStart(th) {
  const later = (th?.later?.length ?? 0) > 0;
  return { note: th?.reply ? null : later ? "Answered in a later turn" : "No reply yet", laterOpen: !th?.reply && later };
}

// A History row of the question log, whole: GET /ui/v1/history on ahd's bridge (`read`, link-model.js Link read), else
// `ah msg history --json --id` in the row's hierarchy dir, as argv with no shell; both give the same body. Called only from the
// row's on_click. Resolves to {text, ...threadOf}: reply, final, more and later with their times, or {error}.
export async function readThread(exec, row, sleep = null, read = null) {
  if (!row.hierDir) return { error: "No directory for this row" };
  let r = null;
  let text = null;
  try {
    if (read && row.dirId) text = await read(`/ui/v1/history?dir=${encodeURIComponent(row.dirId)}&id=${encodeURIComponent(row.id)}`, 15000);
    if (text === null) {
      r = await within(exec("ah", ["msg", "history", "--json", "--id", row.id, "--dir", row.hierDir], { timeout: 15000 }), sleep, 15000);
      text = r.stdout;
    }
  } catch (e) {
    return { error: `Could not read: ${e?.down ?? (late(e) ? NO_ANSWER : e?.message ?? String(e))}` };
  }
  let doc = null;
  try {
    doc = JSON.parse(text)?.threads?.[0] ?? null;
  } catch {
    doc = null;
  }
  if ((r && r.code !== 0) || !doc) return { error: (r?.stderr ?? "").trim().split("\n")[0] || "Thread not found" };
  return { text: doc.prompt?.text ?? "", ...threadOf(doc) };
}

export const NO_REPLY = { kind: "timeout", text: "No reply after 30 s. Check History before sending again.", done: false };

// The answer call: the web's own body for the pick (or for a dismiss reason), sent through `ah msg decision ui-answer` as argv
// with no shell. Called only from a Send or Not-needed on_click. `exec` is herdr's (passed in, so this file stays free of herdr).
// Resolves to the card state: replyState's recorded / refused / error, or NO_REPLY when the 30 s exec cap stopped it.
// `post` (link-model.js Link answer) sends the same body to the bridge first: its reply goes through replyState with the HTTP status,
// as the web's does; null (nothing reached ahd) sends through ah instead; a throw (no reply in time) is NO_REPLY, never sent again.
export async function sendAnswer(exec, card, { pick = null, dismiss = null }, post = null, sleep = null) {
  const body = dismiss ? dismissBody(card.teamId, card.run, card.q, dismiss) : answerBody(card.teamId, card.run, card.q, pick);
  if (!body) return { kind: "error", text: "Could not send: nothing picked", done: false };
  if (post) {
    let got;
    try {
      got = await post(body);
    } catch {
      return NO_REPLY;
    }
    if (got) return replyState(got.status, got.reply);
  }
  let r;
  try {
    r = await within(exec("ah", ["msg", "decision", "ui-answer", "--json", JSON.stringify(body)], { timeout: 30000 }), sleep, 30000);
  } catch (e) {
    return late(e) ? NO_REPLY : { kind: "error", text: `Could not send: ${e?.message ?? String(e)}`, done: false };
  }
  return answerState(r);
}

// The verb's exit and stdout -> the card state, through the web's replyState: exit 0 is the 200 body, exit 2 with `refused` the
// 409, any other exit (2 bad request, 3 unproven, 1 internal) a could-not-send with the body's detail or the stderr line.
export function answerState(r) {
  let reply = null;
  try {
    reply = JSON.parse(r.stdout);
  } catch {
    reply = null;
  }
  const status = r.code === 0 ? 200 : r.code === 2 && reply?.refused ? 409 : 400;
  return replyState(status, reply ?? { detail: (r.stderr ?? "").trim().split("\n")[0] || `ah exited ${r.code}` });
}

// The poll's one call to ah (main.js fetch), bounded twice: `start` runs herdr's exec with `timeout` ms, after which herdr stops
// `ah`, and a deadline 5 s later gives up on an exec that never settles (herdr joins ah's pipe readers, so a grandchild holding
// the pipe would hold the exec for good). A given-up exec cannot be cancelled: it counts in `held.abandoned` until it settles,
// and its late result is dropped. At ABANDON_CAP `start` is not called. `sleep` is herdr's (passed in, so this file stays free
// of herdr). Resolves to the exec result; throws an Error whose message is the down reason.
export const ABANDON_CAP = 2;
export const STUCK = "ah is stuck (restart ahd)";
export const NO_ANSWER = "ah did not answer";
// herdr's own timeout ("took longer than"), or the deadline below.
export const late = (e) => /longer than|did not answer/.test(e?.message ?? String(e));

// A click's one call to ah (a thread, an item's log, an answer), bounded as the poll's is: herdr's exec `timeout` stops ah, and
// this deadline 5 s later gives up on an exec that never settles. `sleep` is herdr's cx.sleep; without one (a node test) the
// call is awaited as it is.
export async function within(call, sleep, timeout) {
  if (!sleep) return call;
  const won = await Promise.race([Promise.resolve(call).then((r) => ({ r }), (e) => ({ e })), sleep(timeout + 5000).then(() => null)]);
  if (!won) throw new Error(NO_ANSWER);
  if (won.e) throw won.e;
  return won.r;
}
export async function pollExec(start, sleep, timeout, held) {
  if (held.abandoned >= ABANDON_CAP) throw new Error(STUCK);
  const call = Promise.resolve().then(start).then((r) => ({ r }), (e) => ({ e }));
  const won = await Promise.race([call, sleep(timeout + 5000).then(() => null)]);
  if (!won) {
    held.abandoned++;
    call.then(() => held.abandoned--);
    throw new Error(NO_ANSWER);
  }
  // a runner's own reason (target-model.js: where ah runs is unset, or WSL is down) is shown as it is
  if (won.e) throw new Error(won.e?.down ?? (/longer than/.test(won.e?.message ?? String(won.e)) ? NO_ANSWER : "ah is not on Herdr's PATH"));
  if (won.r.code !== 0) throw new Error("ahd not running");
  return won.r;
}

// The wait before the next poll after a failed one (ah2-122 D5): the `backoff` step for `fails`, held to 8 s while the mod has been
// down for less than 2 minutes (`downMs`), so a daemon back within that is seen within 8 s.
export const QUICK_RETRY_MS = 120000;
export const retryMs = (backoff, fails, downMs) => Math.min(backoff[Math.min(fails, backoff.length - 1)], downMs < QUICK_RETRY_MS ? 8000 : Infinity);
