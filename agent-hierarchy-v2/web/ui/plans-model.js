// The Plans view's data, as pure functions of the held slices: no component, no clock, no storage unless one is handed in.
// The daemon spells out every field of a `plan:` stream, places each item in its column and serves rows in display order; nothing here holds those rules.

import { isQuiet, memberLabel } from "./select.js";

export const COUNT_KEYS = ["done", "in_progress", "reported", "in_review", "approved", "not_started", "next", "blocked", "waiting_user", "waiting_orch", "dropped"];
export const GROUPS = [
  ["team", "Team"],
  ["stream", "Stream"],
  ["repo", "Repo"],
  ["status", "Status"],
];
export const DAY_MS = 24 * 3_600_000;

const ROLE_NAMES = ["sub-orchestrator", "orchestrator", "architect", "implementor", "ux-engineer", "reviewer", "ultra-advisor", "qa", "docs-writer", "task-runner"];

// The role an owner name carries (`implementor-2` is an implementor); "" for a name that is no role's.
export const roleOf = (owner) => ROLE_NAMES.find((r) => owner === r || owner.startsWith(`${r}-`)) ?? "";

// The full path of a stream's work tree: the stream carries it relative to the team's work root ("." for the root itself).
export function fullTree(workRoot, tree) {
  if (!tree) {
    return null;
  }
  if (tree.startsWith("/") || !workRoot) {
    return tree;
  }
  const root = workRoot.replace(/\/+$/, "");
  return tree === "." ? root : `${root}/${tree}`;
}

const num = (v) => (Number.isFinite(v) ? v : 0);

// An owner's role, label and tooltip: memberLabel over the role its name carries.
const ownerShown = (owner, lane) => (({ label, role, title }) => ({ role, ownerLabel: label, ownerTitle: title }))(memberLabel(owner, owner, roleOf(owner), lane));

// One `plan:` stream as the view reads it. `team` is the team row the stream belongs to.
export function streamOf(raw, plan, team, teamStatus = "live") {
  const { counts, total, owner } = raw;
  return {
    id: `${team.id}|${raw.stream}`,
    teamStatus,
    teamId: team.id,
    team: team.name,
    teamLabel: team.shown ?? team.name,
    dirId: team.dirId,
    repo: team.repo,
    stream: raw.stream,
    owner,
    // ah2-220: how the owner reads (a lane's orchestrator is its Sub-Orchestrator); `owner` stays the key
    ...ownerShown(owner, team.lane ?? null),
    lane: team.lane ?? null,
    branch: raw.branch,
    goal: raw.goal,
    state: raw.state,
    source: raw.source,
    counts,
    total,
    asOf: raw.as_of,
    blockers: typeof raw.counts?.blocked === "number" ? raw.counts.blocked : raw.blockers,
    warnings: raw.warnings,
    truncated: raw.truncated,
    review: raw.review_open,
    status: raw.status,
    workTree: fullTree(plan.work_root ?? null, raw.work_tree ?? null),
  };
}

// An item's state in words, and as a chip: its words always, its colour only as a second cue. An in-progress item of a stream in
// review reads "in review".
export const STATE_LABEL = { in_progress: "in progress", not_started: "not started", next: "next", blocked: "blocked", waiting_user: "waiting on you", waiting_orch: "waiting on Orchestrator", reported: "reported, waiting on the Orchestrator", in_review: "in review", approved: "approved", testing: "testing", done: "done", dropped: "dropped" };
export const CHIP_CLS = { in_progress: "st-in_progress", waiting_orch: "st-in_progress", reported: "st-in_progress", in_review: "st-review", approved: "st-review", testing: "st-review", blocked: "st-blocked", waiting_user: "st-blocked", done: "st-done" };
export const chipOf = (state, column) => (column === "review" ? { cls: "st-review", text: "in review" } : { cls: CHIP_CLS[state] ?? "", text: STATE_LABEL[state] ?? state });

export const STATUS_LABEL = { blocked: "Blocked", waiting: "Waiting on you", review: "In review", in_progress: "In progress", not_started: "Not started", done: "Done" };
const STATUS_ORDER = Object.keys(STATUS_LABEL);
const TEAM_ORDER = { live: 0, stale: 1, ended: 2 };

// The held plan of a team (null while its slice is not held), as the team row and the streams in it.
export function teamModel(row, plan, now) {
  const team = { id: row.id, name: row.name, shown: row.label ?? row.name, dirId: row.dirId, repo: row.repo, branch: row.branch, lane: row.laneOf ? row.name : null };
  const asOf = num(plan?.as_of);
  const quiet = isQuiet(row.quietAt, now);
  const status = row.status === "live" && quiet ? "stale" : row.status;
  const streams = (plan?.streams ?? []).map((s) => streamOf(s, plan, team, status));
  return {
    ...team,
    quiet,
    held: row.held,
    members: row.members,
    running: row.running,
    needs: row.needs,
    waitingOnYou: row.waitingOnYou,
    // the served waiting count less the item rows, whose cards come from plan-items: what is left has no card of its own
    decisionsWaiting: Math.max(0, (row.waitingOnYou ?? 0) - (row.waitingRows ?? []).filter((r) => r.kind === "item").length),
    status,
    updated: Math.max(row.updated, asOf),
    streamsOpen: row.streamsOpen,
    itemsDone: row.itemsDone,
    itemsTotal: row.itemsTotal,
    planLoaded: row.planLoaded,
    hasPlan: plan !== undefined && plan !== null,
    streams,
  };
}

// Streams of live teams first, then open ones, then the newest.
const byLive = (a, b) => TEAM_ORDER[a.teamStatus] - TEAM_ORDER[b.teamStatus] || Number(a.state === "done") - Number(b.state === "done") || b.asOf - a.asOf || a.stream.localeCompare(b.stream) || a.team.localeCompare(b.team);

// Whether a stream matches the filter text: its name, goal, owner and branch, and the titles of the items held for it.
export function matches(stream, query, titles) {
  const q = query.trim().toLowerCase();
  if (q === "") {
    return true;
  }
  return [stream.stream, stream.goal, stream.owner, stream.branch, ...(titles ?? [])].some((v) => v.toLowerCase().includes(q));
}

const keyOf = {
  team: (s) => [s.teamId, s.team],
  stream: (s) => [s.stream, s.stream],
  repo: (s) => [s.repo ?? "", s.repo ?? "no repo"],
  status: (s) => [s.status, STATUS_LABEL[s.status]],
};

// The groups of the list. Each is {key, label, team?, rows (open streams), done (finished streams, folded)}.
// Grouping by team keeps a team that has no streams; the others have only the groups that have a stream.
export function groupsOf(by, teams, { query = "", titlesOf = () => null, onlyMatches = false } = {}) {
  const shown = (s) => matches(s, query, titlesOf(s));
  const split = (streams) => {
    const live = streams.filter(shown).sort(byLive);
    return { rows: live.filter((s) => s.state !== "done"), done: live.filter((s) => s.state === "done") };
  };
  if (by === "team") {
    return teams
      .map((t) => ({ key: t.id, label: t.label ?? t.shown ?? t.name, team: t, ...split(t.streams) }))
      .filter((g) => (query.trim() === "" && !onlyMatches) || g.rows.length + g.done.length > 0);
  }
  const groups = new Map();
  for (const t of teams) {
    for (const s of t.streams) {
      const [key, label] = keyOf[by](s);
      const g = groups.get(key) ?? { key, label, team: null, all: [] };
      g.all.push(s);
      groups.set(key, g);
    }
  }
  const out = [...groups.values()].map(({ all, ...g }) => ({ ...g, ...split(all) })).filter((g) => g.rows.length + g.done.length > 0);
  return by === "status"
    ? out.sort((a, b) => STATUS_ORDER.indexOf(a.key) - STATUS_ORDER.indexOf(b.key))
    : out.sort((a, b) => a.label.localeCompare(b.label));
}

// Live teams first, then stale, then ended; by name within each.
export const sortTeams = (teams) => [...teams].sort((a, b) => TEAM_ORDER[a.status] - TEAM_ORDER[b.status] || a.name.localeCompare(b.name));

export const itemsId = (s) => `plan-items:${s.dirId}/${s.team}/${s.stream}`;
export const planIdOf = (row) => `plan:${row.dirId}/${row.name}`;

// ---- the viewer's choices, in localStorage (which may be absent or throw)

const storageOf = () => {
  try {
    return globalThis.localStorage;
  } catch {
    return undefined;
  }
};

export const PREFS = {
  plansGroup: ["ah.plans.group", GROUPS.map(([k]) => k), "team"],
  plansLayout: ["ah.plans.layout", ["list", "board"], "list"],
  // No fallback here: the default lane depends on the level (laneChoice).
  plansChanges: ["ah.plans.changes", ["off", "on"], "off"],
  plansEpics: ["ah.plans.epics", ["off", "on"], "on"],
  // The Board's look: the kanban (ah-ui-ia U4, the default) or the S6 icicle, kept reachable for a later swap.
  boardVariant: ["ah.board.variant", ["kanban", "icicle"], "kanban"],
  // Finished lanes on the Board (§2.4), shared by Board and List.
  boardFinished: ["ah.board.showFinished", ["off", "on"], "off"],
};

export function readPref(name, storage = storageOf()) {
  const [key, allowed, fallback] = PREFS[name];
  try {
    const v = storage?.getItem(key);
    return allowed.includes(v) ? v : fallback;
  } catch {
    return fallback;
  }
}

export function writePref(name, value, storage = storageOf()) {
  try {
    storage.setItem(PREFS[name][0], value);
    return true;
  } catch {
    return false;
  }
}

// ---- the board

// A stream's name as people read it: the run's own checklist is the team's to-dos, never "_run".
export const streamName = (stream) => (stream === "_run" ? "Team to-dos" : stream);

export const COLUMNS = [
  ["next", "Next"],
  ["progress", "In progress"],
  ["review", "In review"],
  ["waiting", "Waiting on you"],
  ["stuck", "Stuck"],
  ["done", "Done today"],
];
export const LANES = [
  ["team", "Team"],
  ["stream", "Stream"],
  ["holder", "Holder"],
  ["none", "None"],
];

// The swimlanes a level offers: one team's board has no Team lanes. The first is the default.
export const lanesFor = (teamLevel) => (teamLevel ? LANES.slice(1) : LANES);

// The lane choice in force: a stored choice the level does not offer (or one no longer offered at all, like `owner`) reads as the default.
export const laneChoice = (value, teamLevel) => (lanesFor(teamLevel).some(([k]) => k === value) ? value : lanesFor(teamLevel)[0][0]);

// The states "Blocked only" keeps: everything that is stuck on someone.
export const BLOCKED_STATES = ["blocked", "waiting_user", "waiting_orch"];

// An item's own id within its stream: `ah/ui-r6:r6a` reads `r6a`.
export const itemIdOf = (key, stream) => (key.startsWith(`${stream}:`) ? key.slice(stream.length + 1) : key);

// Milliseconds of an ISO time, or null.
export const msOf = (iso) => Date.parse(iso ?? "") || null;
export const DONE_CAP = 20;

// Changed streams whose items the List holds without being opened; beyond this the rest show the stream mark only.
export const CHANGED_LEASE_CAP = 25;

const cardOf = (item, stream, column, laneKey, mark) => ({
  mark,
  id: `${stream.id}|${item.key}`,
  key: itemIdOf(item.key, stream.stream),
  state: item.state,
  since: msOf(item.state_since),
  column,
  lane: laneKey,
  title: item.title,
  streamId: stream.id,
  stream: stream.stream,
  team: stream.team,
  repo: stream.repo,
  owner: item.owner,
  ...ownerShown(item.owner, stream.lane ?? null),
  branch: stream.branch,
  attn: item.state === "blocked",
  at: column === "done" ? item.done_seen_at : msOf(item.state_since) ?? stream.asOf,
  // For the board icicle: the team's dir, the stream's goal, the backlog parent and the size.
  dirId: stream.dirId,
  goal: stream.goal,
  parent: item.parent ?? null,
  size: item.size ?? null,
  // The work registry's fields, absent until the item is registered.
  workId: item.work_id ?? null,
  note: item.note ?? null,
  blockedOpen: item.blocked_by_open ?? 0,
  extId: item.ext_id ?? null,
  extUrl: item.ext_url ?? null,
});

// The cards of the board, in lanes. `itemsOf(stream)` is the held item list of a stream (null while not held); `dayStart` is
// the start of today in the viewer's time. Teams that are stale fold into `stale` unless `expanded` holds their id.
// `blockedOnly` keeps only items in BLOCKED_STATES (a team's decision card stays: it is waiting on you).
export function boardOf(teams, { itemsOf, lanes, query = "", dayStart, expanded = [], markOf = () => null, changesOnly = false, blockedOnly = false, showFinished = false }) {
  const open = new Set(expanded);
  const shown = teams.filter((t) => !t.quiet || open.has(t.id));
  const stale = teams.filter((t) => t.quiet && !open.has(t.id)).map((t) => ({ id: t.id, name: t.label ?? t.shown ?? t.name, updated: t.updated, streams: t.streams.length, done: t.itemsDone, total: t.itemsTotal }));
  const bucket = new Map();
  const lane = (key, label, team) => bucket.get(key) ?? bucket.set(key, { key, label, team, cells: Object.fromEntries(COLUMNS.map(([c]) => [c, []])) }).get(key);
  const laneOf = (team, owner, stream) =>
    lanes === "team"
      ? [team.id, team.label ?? team.name, { ...team, streams: undefined }]
      : lanes === "holder"
        ? [owner || "unowned", owner || "unowned", null]
        : lanes === "stream"
          ? stream ? [stream.id, stream.stream, null] : [`${team.id}|decisions`, "Decisions", null]
          : ["all", "", null];
  for (const t of shown) {
    for (const s of t.streams) {
      // A stream finished today still feeds Done today, and nothing else; an older one has no card.
      const finished = s.state === "done";
      if (finished && !(s.asOf >= dayStart)) {
        continue;
      }
      const items = itemsOf(s);
      if (!matches(s, query, items?.map((i) => i.title))) {
        continue;
      }
      if (changesOnly && items === null && s.mark?.changed) {
        // A changed stream whose items are not held is one card of its own; its items' states are unknown, so Blocked only leaves it out.
        if (blockedOnly) {
          continue;
        }
        const [key, label, team] = laneOf(t, s.owner, s);
        lane(key, label, team).cells.progress.push({ id: `${s.id}|changed`, column: "progress", lane: key, title: `${streamName(s.stream)} — changed (open to see items)`, changed: true, dirId: s.dirId, streamId: s.id, stream: s.stream, team: s.team, repo: s.repo, owner: s.owner, role: s.role, branch: s.branch, attn: false, at: s.asOf, mark: { kind: "stream" } });
        continue;
      }
      for (const item of items ?? []) {
        // The served column, except that "today" is the viewer's: a done item seen before their midnight has no card.
        // A stale item carries quiet_at, served once it has passed: it is hidden unless finished work is asked for.
        const column = (item.column === "done" && !(item.done_seen_at >= dayStart)) || (item.quiet_at && !showFinished) ? null : item.column;
        const mark = markOf(s, item);
        if (column !== null && (!finished || column === "done") && (!changesOnly || mark !== null) && (!blockedOnly || BLOCKED_STATES.includes(item.state))) {
          const [key, label, team] = laneOf(t, item.owner, s);
          lane(key, label, team).cells[column].push(cardOf(item, s, column, key, mark));
        }
      }
    }
    if (t.decisionsWaiting > 0 && !changesOnly) {
      const [key, label, team] = laneOf(t, "you");
      lane(key, label, team).cells.waiting.push({ id: `${t.id}|decision`, column: "waiting", lane: key, decision: true, title: `Waiting on you · ${t.decisionsWaiting}`, dirId: t.dirId, team: t.name, teamLabel: t.label ?? t.shown ?? t.name, repo: t.repo, stream: "", owner: "", role: "", branch: t.branch ?? "", attn: true, at: t.updated, streamId: null });
    }
  }
  const out = [...bucket.values()].map((l) => {
    const done = l.cells.done.sort((a, b) => b.at - a.at);
    return { ...l, doneMore: Math.max(0, done.length - DONE_CAP), cells: { ...l.cells, done } };
  });
  return {
    lanes: lanes === "holder" ? out.sort((a, b) => a.label.localeCompare(b.label)) : out,
    stale,
    // Each column's card count over every lane, for the column header.
    counts: Object.fromEntries(COLUMNS.map(([c]) => [c, out.reduce((n, l) => n + l.cells[c].length, 0)])),
    cards: out.reduce((n, l) => n + Object.values(l.cells).reduce((m, c) => m + c.length, 0), 0),
  };
}

export const planChangesId = (dirId, team) => `list:plan-changes:${dirId}/${team}`;

// One item's history from `list:plan-changes` rows (newest first, as served), keeping only its own stream's rows: item ids are
// unique within a stream, not across a team. `label(state)` names a state.
export function historyOf(rows, stream, label = (s) => s) {
  const said = { added: () => "added", dropped: () => "dropped", priority: (r) => `priority ${r.from ?? "—"} → ${r.to ?? "—"}`, state: (r) => (r.from ? `${label(r.from)} → ${label(r.to)}` : label(r.to)) };
  return rows
    .filter((r) => r.stream === stream)
    .map((r) => ({ key: `${r.stream}|${r.seq}`, at: msOf(r.at), text: (said[r.kind] ?? (() => r.kind))(r), by: r.by ?? "" }));
}

// One item's history, for any board that shows an item: one read of its team's change list, filtered to the item by the server and
// to its stream by historyOf. A list that is absent (404) reads as no history; any other failure is "error".
export async function itemHistory(store, { dir, team, stream, item }) {
  try {
    return historyOf((await store.fetchList(planChangesId(dir, team), { item, limit: 50 })).rows ?? [], stream, (st) => STATE_LABEL[st] ?? st);
  } catch (e) {
    return e?.status === 404 ? [] : "error";
  }
}

// The chosen value of a remembered preference: this page's choice, else the viewer's stored one.
export const prefOf = (ui, name, stored) => ui[name] ?? stored[name] ?? readPref(name);

export function choose(store, name, value) {
  writePref(name, value);
  store.setUi({ [name]: value });
}

export function cycle(store, name, current) {
  const all = PREFS[name][1];
  choose(store, name, all[(all.indexOf(current) + 1) % all.length]);
}

// The Orchestrator's checklist: every open item of a team's streams, in the order the Orchestrator works them. Each state is shown by
// its checklist name and a glyph, and the attention states are also bold, so no state rests on colour. `queued` and `dispatched`
// are the checklist's names for the plan states next / not_started and in_progress.
export const CHECKLIST_STATES = [
  { state: "waiting_orch", label: "waiting-orch", glyph: "◉", attn: true },
  { state: "reported", label: "reported, waiting on the Orchestrator", glyph: "◉", attn: true },
  { state: "blocked", label: "blocked", glyph: "⚠", attn: true },
  { state: "waiting_user", label: "waiting on you", glyph: "◎", attn: true },
  { state: "in_progress", label: "dispatched", glyph: "◐", attn: false },
  { state: "in_review", label: "in review", glyph: "◑", attn: false },
  { state: "testing", label: "testing", glyph: "◒", attn: false },
  { state: "approved", label: "approved", glyph: "✓", attn: false },
  { state: "next", label: "queued", glyph: "○", attn: false },
  { state: "not_started", label: "queued", glyph: "○", attn: false },
];

// Whether a stream has anything on the checklist: `checklist show` lists a finished stream's open items too.
export const hasOpenItems = (s) => s.state !== "done" || Object.entries(s.counts ?? {}).some(([k, n]) => k !== "done" && k !== "dropped" && n > 0);

// Rows of the items on the checklist across `streams`, from `itemsOf(stream)` (the stream's served items, or null while they are not held), by their served
// `checklist_rank`, then stream and item order. The daemon adds role, todo, refs, state_since and updated_at to an item only when it has them.
// `etaOf(stream, refs)` is the ETA of the open exchange the item's refs link, or null.
export function checklistRows(streams, itemsOf, etaOf = () => null) {
  const rows = [];
  streams.forEach((s, si) => {
    (itemsOf(s) ?? []).forEach((item, ii) => {
      if (item.checklist_rank !== null && item.checklist_rank !== undefined) {
        const refs = item.refs ?? [];
        rows.push({ key: `${s.id}|${item.key}`, title: item.title, state: item.state, owner: item.holder ?? null, stream: s.stream, role: item.role ?? null, todo: item.todo ?? null, refs, stateSince: Date.parse(item.state_since ?? "") || null, updatedAt: Date.parse(item.updated_at ?? "") || null, eta: etaOf(s, refs), rank: item.checklist_rank, order: si * 100_000 + ii });
      }
    });
  });
  return rows.sort((a, b) => a.rank - b.rank || a.order - b.order);
}

// Stalled (§2.3; Orchestrator ruling, U6): the server's stall time reached while an eligible member is idle to take it. The one
// rule: the Board's marker and the Sitrep both call it, and nothing in the UI holds its own threshold.
export const stalledOf = (stallAt, eligible, nowMs) => stallAt !== null && stallAt !== undefined && nowMs >= stallAt && eligible.length > 0;

// The Queue: a team's queued items from the queue slice, in the order the daemon serves them, the stalled ones first. The daemon
// serves stall_at and the view compares its own clock with it; nothing here holds a threshold.
export function queueRows(rows, nowMs) {
  return rows.map((r) => {
    const stallAt = Date.parse(r.stall_at ?? "") || null;
    return { key: r.item, item: r.item, role: r.role ?? null, queuedSince: Date.parse(r.queued_since ?? "") || null, stallAt, stalled: stallAt !== null && nowMs >= stallAt, eligible: r.eligible_members ?? [], lastDispatch: Date.parse(r.last_dispatch_at ?? "") || null };
  });
}

// ---- the backlog: one `backlog:<dir>` slice per hierarchy dir, its items in wave columns

export const WAVES = [
  ["now", "Now"],
  ["next", "Next"],
  ["later", "Later"],
  ["untriaged", "Untriaged"],
];
export const backlogId = (dirId) => `backlog:${dirId}`;

// The open items of a backlog body under their served waves, in the order served. `query` matches the id, title and theme.
export function waveColumns(body, query = "") {
  const q = query.trim().toLowerCase();
  const open = (body?.items ?? []).filter((i) => i.open);
  const shown = q === "" ? open : open.filter((i) => [i.key, i.title, i.theme ?? ""].some((v) => v.toLowerCase().includes(q)));
  const cols = Object.fromEntries(WAVES.map(([w]) => [w, []]));
  for (const i of shown) {
    cols[i.wave].push(i);
  }
  return { cols, open: body?.open ?? 0, total: body?.total ?? 0, shown: shown.length, health: body?.health ?? { untriaged: 0, stale: 0, due: false } };
}

// The short text of an external ref (`github:o/r#12` reads `#12`, `linear:ABC-1` reads `ABC-1`).
export const extText = (ext) => ext.replace(/^github:[^#]*/, "").replace(/^[a-z]+:/, "");

// An untrusted URL when it is http(s), else null: nothing else may become a link.
export const httpUrl = (url) => {
  try {
    const u = new URL(url);
    return u.protocol === "http:" || u.protocol === "https:" ? u.href : null;
  } catch {
    return null;
  }
};

// The item-log slice of a work id: `ah-12` in a directory is `item-log:<dir>/12`. The number is the identity; the prefix is display.
export const itemLogId = (dir, workId) => {
  const n = /-(\d+)$/.exec(workId ?? "")?.[1];
  return dir && n ? `item-log:${dir}/${n}` : null;
};

// A waiting card's description alone, `item-desc:<dir>/<n>` ({id, description}): the card never leases the whole log.
export const itemDescId = (dir, workId) => itemLogId(dir, workId)?.replace(/^item-log:/, "item-desc:") ?? null;

// A description as blocks, the markdown agents write: a "#" line or one that is all **bold** is a heading, "- " or "* " lines a list,
// "1. " lines a numbered list, a ``` fence a code block kept as written, other runs of lines a paragraph that keeps its line breaks.
// Nothing else means anything. Agents write this text, so it only ever becomes text nodes.
export function descriptionBlocks(text) {
  const blocks = [];
  let code = null;
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (code) {
      line.startsWith("```") ? (code = null) : code.lines.push(raw.trimEnd());
      continue;
    }
    const last = blocks.at(-1);
    const head = /^#{1,6}\s+(.+?)[\s#]*$/.exec(line) ?? /^\*\*((?:(?!\*\*).)+)\*\*$/.exec(line);
    const numbered = /^\d+[.)]\s+(.*)$/.exec(line);
    if (line.startsWith("```")) {
      blocks.push((code = { kind: "code", lines: [] }));
    } else if (line === "") {
      blocks.push({ kind: "gap" });
    } else if (head) {
      blocks.push({ kind: "head", text: head[1] });
    } else if (/^[-*]\s/.test(line)) {
      const item = line.replace(/^[-*]\s+/, "");
      last?.kind === "list" ? last.items.push(item) : blocks.push({ kind: "list", items: [item] });
    } else if (numbered) {
      last?.kind === "olist" ? last.items.push(numbered[1]) : blocks.push({ kind: "olist", items: [numbered[1]] });
    } else if (last?.kind === "para") {
      last.text += `\n${raw.trimEnd()}`;
    } else {
      blocks.push({ kind: "para", text: raw.trimEnd() });
    }
  }
  return blocks.filter((b) => b.kind !== "gap").map((b) => (b.kind === "code" ? { kind: "code", text: b.lines.join("\n") } : b));
}

// A line's marks: `code` (a path or a command, copied whole) and **bold**; a lone ** or ` stays as written.
export const inlineParts = (text) =>
  text.split(/(`[^`\n]+`)/).flatMap((s, i) => (i % 2 ? [{ kind: "code", text: s.slice(1, -1) }] : s.split(/\*\*(.+?)\*\*/).map((p, j) => ({ kind: j % 2 ? "bold" : "text", text: p })))).filter((p) => p.text !== "");
