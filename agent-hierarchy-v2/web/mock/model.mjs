// An in-memory stand-in for the daemon's read model, answering with the same documents:
//   manifest  {epoch, seq, building, slices, pending}
//   poll      {epoch, seq, resync, slices, removed}
//   slice     {id, version, as_of, body}
//   list      {version, total, head_cursor, tail_cursor, rows, next, prev}   (ui-perf.md §12.32; no daemon route exists yet)
import { envelope } from "./envelope.mjs";

// The column the daemon serves on a member, for this stand-in to serve too.
export const FLOW_COLUMN = { orchestrator: 0, "sub-orchestrator": 0, "ultra-advisor": 1, architect: 1, "docs-writer": 1, reviewer: 3, qa: 3 };

import { changesWindow, changesBody } from "./changes.mjs";
import { questionsWindow } from "./questions.mjs";
import { lanesOf } from "./timeline-spans.mjs";
import { memberActions, orchestratorActions, rootActions } from "./actions.mjs";
import { TRANSCRIPT_NAMES, buildTranscript, appendTurn, headerOf, expandOf, transcriptWindow, lazySlices, transcriptIdOf, transcriptListIdOf } from "./transcripts.mjs";
import { TEAM_ID, buildSlices, cursorOf, indexOf, timelineRows, DIR, TEAM, ACTIVITY_NAMES, sessionOf, activityIdOf, exchangeIds, openListId, exchangeBody, etaFields, exchangeText, openEntry, addMember, removeMember, SHIP_TALLY, RUN_MIN, flowId, flowPageId, flowSlices, memberOfParty, MAX_NAMES, SCENARIO_TRAFFIC, SCENARIO_STUCK } from "./fixture.mjs";

// The view names a manifest can be asked for and the id prefixes each holds.
export const VIEWS = {
  summary: ["list:teams", "team-summary:"],
  members: ["activity:"],
  tree: ["team:", "member:", "topology:", "worktree:", "list:teams"],
  exchanges: ["exchange:", "list:exchanges:", "list:feed:", "list:open:"],
  status: ["status:"],
  processes: ["processes:"],
  timeline: ["timeline:"],
};

// Whether an exchange has the member as a party: by name, or by role for the unnamed orchestrator.
const involves = (exchange, member) => member !== undefined && [["to", "to_name"], ["from", "from_name"]].some(([role, name]) => (exchange[name] ?? null) === null ? exchange[role] === member.role && member.role === "orchestrator" : exchange[name] === member.name);

// Plan slices are in no view: the server publishes them only to a client that names them in its subscription.
const isPlan = (id) => /^(plan|plan-items|queue|sitrep|backlog|questions|list:plan|list:plan-changes|list:questions|flow|flow-page|item-msgs|item-log|item-desc):/.test(id);

export const RETENTION_MS = 7 * 24 * 60 * 60 * 1000;
export const MAX_LIST = 200;

// The served mock's live traffic: how many briefs it keeps in flight, and the briefs it sends in turn (from, to, slug).
export const LIVE_OPEN = 5;
export const CHURN_MAX = 3;
const TRAFFIC = SCENARIO_TRAFFIC ?? [
  ["orchestrator", "implementor-1", "ui-graph-comms"],
  ["implementor-1", "task-runner", "run-web-suite"],
  ["orchestrator", "reviewer", "review-comms"],
  ["architect", "orchestrator", "spec-question"],
  ["orchestrator", "docs-writer", "changelog-2.5"],
  ["implementor-2", "task-runner", "run-cli-suite"],
  ["reviewer", "orchestrator", "review-verdict"],
  ["orchestrator", "architect", "design-followup"],
];

// The counter names of the daemon's read-model document (GET /v1/read-model), as the ui-perf spec lists them.
export const READ_MODEL_COUNTERS = ["unclassified_hint", "noncanonical_hint", "drift", "reread_retry", "coalesce_ms", "projector_faults", "transcript_resets", "decision_orphans"];

const matcher = (tokens) => {
  if (tokens === null) {
    return () => true;
  }
  const ids = new Set();
  const prefixes = [];
  for (const token of tokens) {
    if (token.startsWith("view:")) {
      prefixes.push(...(VIEWS[token.slice(5)] ?? []));
    } else {
      ids.add(token);
    }
  }
  return (id) => ids.has(id) || prefixes.some((p) => id.startsWith(p));
};

const BLOCKED = new Set(["blocked", "failed"]);

// The team's summary slice body, from the slices held now: the same fields and rules as the daemon's summary composer.
// `memo` keeps the times a state or a waiting decision began, so the time of an unchanged state does not move.
export function summaryBody(slices, memo, now) {
  const team = slices.get(TEAM_ID);
  if (!team) {
    return null;
  }
  const pids = slices.get(`processes:${team.body.dir_id}`)?.body.pids ?? [];
  const members = [team.body.orchestrator, ...team.body.members].filter(Boolean).map((id) => {
    const m = slices.get(id)?.body;
    const act = m?.activity ? slices.get(m.activity) : undefined;
    const pid = m?.liveness?.pid;
    const gone = pid != null && !pids.some((p) => p.pid === pid);
    const state = gone ? "gone" : BLOCKED.has(act?.body.activity) ? act.body.activity : act?.body.activity === "idle" ? "idle" : act === undefined ? "unknown" : "working";
    if (memo.state.get(id) !== state) {
      memo.state.set(id, state);
      memo.at.set(id, memo.state.size > 0 && memo.seen ? now : act?.as_of ?? now);
    }
    return { id, state };
  });
  memo.seen = true;
  const open = slices.get(openListId())?.body.open ?? [];
  const exchanges = [...slices.values()].filter((x) => x.id.startsWith("exchange:"));
  const feedNewest = exchanges.reduce((n, x) => Math.max(n, x.as_of), 0);
  const waiting = Number(team.body.pipeline?.waiting_on_user) || 0;
  if (waiting > 0 && !memo.waitingSince) {
    memo.waitingSince = now;
  } else if (waiting === 0) {
    memo.waitingSince = null;
  }
  const alerts = members
    .filter((m) => BLOCKED.has(m.state))
    .map((m) => ({ kind: m.state === "failed" ? "error" : "blocked", member: m.id, since: iso(memo.at.get(m.id)), text: m.state === "failed" ? "Last turn failed" : "Waiting on a permission" }));
  if (waiting > 0) {
    alerts.push({ kind: "needs_you", since: iso(memo.waitingSince), text: `Waiting on you · ${waiting}` });
  }
  alerts.sort((a, b) => (a.since < b.since ? 1 : -1));
  // stuck open tasks: the daemon appends them after the composed alerts, oldest stuck first, as the orchestrator's and not in needs_you (ReadModel ProjectSummary)
  const stuck = SCENARIO_STUCK.filter(([id]) => open.some((o) => o.id === id)).map(([id, min]) => ({ id, since: iso(now - min * 60_000), min, x: slices.get(id)?.body, to: open.find((o) => o.id === id).to_member }));
  alerts.push(...stuck.map((k) => ({ kind: "stuck", audience: "orchestrator", exchange: k.id, member: k.to ?? "", since: k.since, text: `No activity for ${k.min >= 60 ? `${Math.floor(k.min / 60)} h` : `${k.min} min`}${k.x?.title ? ` · ${k.x.title}` : ""}` })));
  const lead = members[0] ? slices.get(members[0].id)?.body : undefined;
  const stateTimes = members.filter((m) => m.state !== "gone").map((m) => memo.at.get(m.id) ?? 0);
  const created = open.map((o) => Date.parse(slices.get(o.id)?.body.created ?? "")).filter(Number.isFinite);
  return {
    team: TEAM_ID,
    name: team.body.name,
    display_name: team.body.display_name ?? null,
    work_root: slices.get(team.body.worktrees?.[0])?.body.work_root ?? null,
    branch: lead?.placement?.stream ?? null,
    running: members.some((m) => m.state !== "gone"),
    members: members.length,
    working: members.filter((m) => m.state === "working").length,
    blocked: members.filter((m) => BLOCKED.has(m.state)).length,
    needs_you: waiting + members.filter((m) => BLOCKED.has(m.state)).length,
    waiting_on_you: waiting,
    waiting_rows: [],
    // the daemon's count of `working` items: in progress with a holder
    in_progress_now: [...slices.values()].filter((s) => s.id.startsWith(`plan-items:${team.body.dir_id}/${team.body.name}/`)).flatMap((s) => s.body.items ?? []).filter((i) => i.state === "in_progress" && i.holder).length,
    stuck: stuck.length,
    // as the daemon counts: an open brief to a gone member is stale, not in flight
    in_flight: open.filter((o) => !members.some((m) => m.id === o.to_member && m.state === "gone")).length,
    ...SHIP_TALLY,
    oldest_in_flight_at: created.length ? iso(Math.min(...created)) : null,
    updated_at: iso(Math.max(team.as_of, feedNewest, ...stateTimes)),
    feed_newest_at: feedNewest ? iso(feedNewest) : null,
    alerts,
    // the served state word (SummaryData.TeamState as ReadModel recomputes it, from needs_you without stuck tasks):
    // needs_you, else ended (has members, none running), else working, else idle. Failed reads as blocked.
    state: waiting + members.filter((m) => BLOCKED.has(m.state)).length > 0 ? "needs_you" : members.length > 0 && !members.some((m) => m.state !== "gone") ? "ended" : members.some((m) => m.state === "working") ? "working" : "idle",
    member_states: Object.fromEntries(members.map((m) => [m.id, { state: BLOCKED.has(m.state) ? "blocked" : m.state, liveness: m.state === "gone" ? "proven_dead" : "live" }])),
    // ah-ui-ia U0 (b): the team slice's lane facts, cut as the daemon cuts them
    ...(team.body.lane ? { lane: { parent_team: team.body.lane.parent_team, stream: team.body.lane.stream } } : team.body.lane_claim ? { lane_claim: team.body.lane_claim } : null),
  };
}
const iso = (ms) => new Date(ms).toISOString();

// What the daemon composes onto every slice: SummaryData.ApplyStatus, SummaryData.AlertCard, PlanSummaryComposer.StatusOf.
const STALE_AFTER_MS = 24 * 3_600_000;
const alertCard = (a) => {
  const who = (a.member ?? "").split("/").pop();
  return a.kind === "needs_you" ? { title: a.text, detail: "Waiting on you" }
    : a.kind === "stuck" ? { title: `${who ? `${who}: ` : ""}task stuck`, detail: a.text }
    : { title: a.kind === "error" ? `${who} failed` : `${who} is blocked`, detail: a.text };
};
const WINDOW_1M = 1_000_000;
const WINDOW_200K = 200_000;
const grouped = (n) => n.toLocaleString("en-US");
const compact = (n) => (n >= 1_000_000 ? `${Math.floor(n / 100_000) / 10}M` : n >= 1000 ? `${Math.floor(n / 1000)}k` : String(n));
export function windowOf(model) {
  const id = typeof model === "string" ? model.trim().toLowerCase() : "";
  if (id.endsWith("[1m]") || id.split("-").slice(1).includes("1m")) {
    return WINDOW_1M;
  }
  return id.startsWith("claude-") || /^(opus|sonnet|haiku|fable)(?![a-z])/.test(id) ? WINDOW_200K : null;
}
// ContextReading.Of: {pct, text, title, over}.
export function contextReading(model, used) {
  if (typeof used !== "number" || !Number.isFinite(used)) {
    return { pct: null, text: "—", title: "context not reported", over: false };
  }
  let window = windowOf(model);
  const inferred = window === WINDOW_200K && used > WINDOW_200K;
  if (inferred) {
    window = WINDOW_1M;
  }
  if (window === null) {
    return { pct: null, text: compact(used), title: `${grouped(used)} tokens · window unknown`, over: false };
  }
  const exact = `${grouped(used)} of ${grouped(window)} tokens · ${model ?? "model not named"}${inferred ? " · window inferred" : ""}`;
  const pct = Math.floor((used / window) * 100);
  return used > window
    ? { pct, text: "100%+", title: `${exact}. Above the assumed window: the model may have a larger window than this table knows`, over: true }
    : { pct, text: used > 0 && pct < 1 ? "<1%" : `${pct}%`, title: exact, over: false };
}
// SummaryData.ProcessLine.
const processLine = (m) => ({ live: "running", proven_dead: "not running", unknown: "unknown" })[m.liveness] ?? (m.state === "gone" ? "not running" : "running");
// SummaryData.QuietAt.
const endedOf = (b) => b.state === "ended" || (b.state === "needs_you" && !b.running && Object.keys(b.member_states ?? {}).length > 0);
const quietAt = (b, staleAt) => (b.waiting_on_you > 0 || b.in_progress_now > 0 || (!b.held && b.working > 0) ? null : b.held || endedOf(b) || b.members === 0 ? b.updated_at : staleAt);
const servedSummary = (b, planAsOf, open) => ({
  ...b,
  held: b.held === true,
  quiet_at: quietAt(b, iso(Math.max(Date.parse(b.updated_at ?? "") || 0, planAsOf) + STALE_AFTER_MS)),
  member_states: Object.fromEntries(Object.entries(b.member_states ?? {}).map(([id, m]) => [id, { ...m, process: processLine(m) }])),
  stale_ids: open.filter((o) => o.to_member && b.member_states?.[o.to_member]?.state === "gone").map((o) => o.id),
  alerts: (b.alerts ?? []).map((a) => ({ ...alertCard(a), ...a })),
  stale_at: iso(Math.max(Date.parse(b.updated_at ?? "") || 0, planAsOf) + STALE_AFTER_MS),
  ended: endedOf(b),
  status: b.members > 0 ? "live" : b.streams_open > 0 ? "stale" : "ended",
});
export const servedStream = (s, teamBranch = null) => {
  const n = (state) => s.counts?.[state] ?? 0;
  const status = s.state === "done" ? "done"
    : n("blocked") > 0 ? "blocked"
    : n("waiting_user") > 0 ? "waiting"
    : s.review_open === true ? "review"
    : n("in_progress") > 0 || n("waiting_orch") > 0 || n("done") > 0 ? "in_progress"
    : "not_started";
  const counts = Object.fromEntries(["done", "in_progress", "not_started", "next", "blocked", "waiting_user", "waiting_orch", "dropped"].map((k) => [k, n(k)]));
  const total = Object.entries(counts).reduce((sum, [k, v]) => (k === "dropped" ? sum : sum + v), 0);
  return { owner: "", goal: "", state: "open", source: "checklist", as_of: 0, blockers: 0, warnings: 0, truncated: false, review_open: false, ...s, owner: s.owner ?? "", goal: s.goal ?? "", branch: s.branch ?? teamBranch ?? "", counts, total, status };
};
// The mock's mirror of PlanItemsLane.Placed, BacklogLane.BodyOf and QueueLane's order.
const COLUMN_BY_STATE = { not_started: "next", next: "next", blocked: "stuck", waiting_orch: "progress", waiting_user: "waiting", in_review: "review", approved: "review" };
const RANK_BY_STATE = { waiting_orch: 0, blocked: 1, waiting_user: 2, in_progress: 3, in_review: 4, testing: 5, approved: 6, next: 7, not_started: 7 };
export const servedItems = (body, review, streamOwner = "") => ({
  ...body,
  items: body.items.map((i) => ({
    ...i,
    owner: i.holder && i.holder !== "—" ? i.holder : streamOwner === "—" ? "" : streamOwner,
    column: i.state === "in_progress" ? (review ? "review" : "progress") : i.state === "done" ? (i.done_seen_at > 0 ? "done" : null) : COLUMN_BY_STATE[i.state] ?? null,
    checklist_rank: RANK_BY_STATE[i.state] ?? null,
  })),
});
export const servedBacklog = (body, plannedParents = []) => {
  const parentOf = new Map(body.items.map((i) => [i.key, i.parent]));
  const planned = new Set();
  for (let key of plannedParents) {
    for (; key && !planned.has(key); key = parentOf.get(key)) {
      planned.add(key);
    }
  }
  const items = [...body.items]
    .sort((a, b) => (a.rank ?? Infinity) - (b.rank ?? Infinity) || a.key.localeCompare(b.key, undefined, { numeric: true, sensitivity: "base" }))
    .map((i) => ({ ...i, open: i.state !== "done" && i.state !== "dropped", wave: ["now", "next", "later"].includes(i.wave?.toLowerCase()) ? i.wave.toLowerCase() : "untriaged", planned: planned.has(i.key) }));
  return { ...body, items, open: items.filter((i) => i.open).length, total: items.length, health: { untriaged: 0, stale: 0, due: false, ...body.health } };
};
export const servedQueue = (rows) => [...rows].sort((a, b) => (Date.parse(a.stall_at ?? "") || Infinity) - (Date.parse(b.stall_at ?? "") || Infinity));

let epochCounter = 0;

export function createMock({ now = Date.now } = {}) {
  const slices = new Map();
  const tombstones = [];
  const waiters = new Set();
  let seq = 0;
  let epoch = `mock-${++epochCounter}-${now().toString(36)}`;
  let turn = 0;
  // The fixture's own "now": the timeline rows keep their times between requests.
  const startedAt = now();

  const wake = () => {
    for (const w of [...waiters]) {
      w();
    }
  };

  const summaryMemo = { state: new Map(), at: new Map(), seen: false, waitingSince: null };
  let summarising = false;
  const publish = (changes, removed = []) => {
    publishRaw(changes, removed);
    if (!summarising) {
      summarising = true;
      const raw = summaryBody(slices, summaryMemo, now());
      const id = `team-summary:${TEAM_ID.slice("team:".length)}`;
      const body = raw && served(id, raw);
      if (body && JSON.stringify(slices.get(id)?.body) !== JSON.stringify(body)) {
        publishRaw([{ id, as_of: now(), body }], []);
      }
      summarising = false;
    }
  };
  const planAsOf = (summaryId) => slices.get(summaryId.replace(/^team-summary:/, "plan:"))?.body.as_of ?? 0;
  const openRows = (summaryId) => slices.get(summaryId.replace(/^team-summary:/, "list:open:team:"))?.body.open ?? [];
  const servedMember = (body) => {
    const context = slices.get(body.activity ?? "")?.body.context;
    const r = contextReading(body.model ?? context?.model ?? null, context?.used);
    return { ...body, column: FLOW_COLUMN[body.role] ?? 2, context_pct: r.pct, context_text: r.text, context_title: r.title, context_over: r.over };
  };
  const servedTopology = (id, body) => ({
    ...body,
    nodes: { ...body.nodes, bands: body.nodes.bands ?? {} },
    edges: {
      ...body.edges,
      brief: openRows(id.replace(/^topology:/, "team-summary:")).filter((o) => o.from_member && o.to_member).map((o) => ({
        id: o.id,
        from: o.from_member,
        to: o.to_member,
        brief: slices.get(o.from_member)?.body.role === "implementor" && slices.get(o.to_member)?.body.role === "reviewer" ? "review" : "in_flight",
      })),
    },
  });
  const served = (id, body) =>
    id.startsWith("topology:") ? servedTopology(id, body)
      : id.startsWith("team-summary:") ? servedSummary(body, planAsOf(id), openRows(id))
      : id.startsWith("member:") ? servedMember(body)
      : id.startsWith("list:open:team:") && Array.isArray(body.open) ? { ...body, open: body.open.map((o) => ({ ...o, unattended_at: o.to_member ? null : slices.get(o.id)?.body.created ?? iso(now()) })) }
      : id.startsWith("plan:") && Array.isArray(body.streams) ? { ...body, streams: body.streams.map((s) => servedStream(s, slices.get(id.replace(/^plan:/, "team-summary:"))?.body.branch ?? null)) }
      : id.startsWith("plan-items:") && Array.isArray(body.items) ? servedItems(body, reviewOf(id), streamOf(id)?.owner ?? "")
      : id.startsWith("backlog:") && Array.isArray(body.items) ? servedBacklog(body, plannedParentsOf(id, body))
      : id.startsWith("queue:") && Array.isArray(body) ? servedQueue(body)
      : body;
  const streamOf = (itemsId) => {
    const [dirTeam, stream] = [itemsId.slice("plan-items:".length, itemsId.lastIndexOf("/")), itemsId.slice(itemsId.lastIndexOf("/") + 1)];
    return slices.get(`plan:${dirTeam}`)?.body.streams?.find((s) => s.stream === stream);
  };
  const reviewOf = (itemsId) => streamOf(itemsId)?.review_open === true;
  // The rows the directory's checklist items plan, as DaemonServices hands BacklogLane: an item names the row as parent and the row outranks it (E-D3).
  const kindRank = (kind) => ({ project: 5, goal: 4, epic: 3, deliverable: 2 })[kind] ?? 1;
  const plannedParentsOf = (backlogId, body) => {
    const rows = new Map(body.items.map((i) => [i.key, i]));
    return [...slices.values()]
      .filter((s) => s.id.startsWith(`plan-items:${backlogId.slice("backlog:".length)}/`))
      .flatMap((s) => s.body.items ?? [])
      .filter((i) => rows.has(i.parent) && kindRank(rows.get(i.parent).kind) > kindRank(i.kind))
      .map((i) => i.parent);
  };
  const reserve = (id) => {
    const held = slices.get(id);
    const body = held && served(id, held.body);
    if (held && JSON.stringify(body) !== JSON.stringify(held.body)) {
      slices.set(id, { ...held, version: seq, body });
    }
  };
  const publishRaw = (changes, removed = []) => {
    seq += 1;
    for (const c of changes) {
      slices.set(c.id, { id: c.id, version: seq, as_of: c.as_of, body: served(c.id, c.body) });
    }
    // What the daemon recomposes when these move: a plan pushes its team's stale line, an open list its stale ids, an activity its member's context.
    for (const c of changes) {
      if (c.id.startsWith("plan-items:")) {
        reserve(`backlog:${c.id.slice("plan-items:".length).split("/")[0]}`);
      }
      if (/^(plan|list:open:team):/.test(c.id)) {
        reserve(c.id.replace(/^(plan|list:open:team):/, "team-summary:"));
        if (c.id.startsWith("plan:")) {
          // A review opening or closing moves its stream's items.
          for (const s of [...slices.keys()].filter((x) => x.startsWith(`plan-items:${c.id.slice("plan:".length)}/`))) {
            reserve(s);
          }
        }
      } else if (c.id.startsWith("activity:")) {
        for (const s of [...slices.values()].filter((x) => x.id.startsWith("member:") && x.body.activity === c.id)) {
          reserve(s.id);
        }
      } else if (c.id.startsWith("member:")) {
        reserve(c.id);
      }
    }
    for (const id of removed) {
      if (slices.delete(id)) {
        tombstones.push({ id, version: seq });
      }
      const held = id.replace(/^exchange:/, "exchange-body:");
      if (held !== id && slices.has(held)) {
        slices.set(held, { id: held, version: seq, as_of: 0, body: { id: id.split("/").pop(), gone: true } });
      }
    }
    wake();
  };

  // Slices the server publishes only while a client names them in its subscription (transcript, files, tests).
  const transcripts = new Map();
  const lazy = new Map();
  // The full change record of each plan-changes lane; its slice holds only the newest rows.
  const lanes = new Map();
  let hasReadModel = true;
  let readModelReads = 0;
  // When the run the fixture is on began; set by each load, so a rebuild in the same epoch keeps the run id.
  let runStart = 0;

  const load = () => {
    slices.clear();
    summaryMemo.state.clear();
    summaryMemo.at.clear();
    summaryMemo.seen = false;
    summaryMemo.waitingSince = null;
    tombstones.length = 0;
    seq = 0;
    const at = now();
    runStart = at - RUN_MIN * 60_000;
    const built = buildSlices(at, runStart);
    publish(built.filter((s) => !isPlan(s.id)));
    transcripts.clear();
    lazy.clear();
    lanes.clear();
    for (const s of built.filter((x) => isPlan(x.id))) {
      const isLane = s.id.startsWith("list:plan-changes:");
      if (isLane) {
        lanes.set(s.id, s.body.changes);
      }
      lazy.set(s.id, { id: s.id, as_of: s.as_of, body: isLane ? changesBody(s.body.changes) : s.body });
    }
    for (const name of TRANSCRIPT_NAMES) {
      transcripts.set(name, buildTranscript(name));
    }
    for (const s of lazySlices(now(), transcripts)) {
      lazy.set(s.id, s);
    }
    const head = lazy.get(flowId()).body;
    flow.names = [...head.names];
    flow.events = [...head.pages.flatMap((_, k) => lazy.get(flowPageId(head.run, k)).body.events), ...head.tail];
  };

  // The run's flow events, which the traffic appends to: the head re-versions, and a page is added each time the tail fills one.
  const flow = { names: [], events: [] };
  const flowAppend = (from, to, k) => {
    const head = lazy.get(flowId()).body;
    const at = now();
    const index = (who) => {
      const id = memberOfParty(who);
      if (!flow.names.includes(id)) {
        const members = flow.names.length - (flow.names.includes("other") ? 1 : 0);
        if (members < MAX_NAMES) {
          flow.names.push(id);
        } else if (!flow.names.includes("other")) {
          flow.names.push("other");
        }
      }
      return flow.names.includes(id) ? flow.names.indexOf(id) : flow.names.indexOf("other");
    };
    flow.events.push([Math.floor((at - head.start_ms) / 1000), index(from), index(to), k]);
    const changed = flowSlices({ run: head.run, start: head.start_ms, now: at, names: [...flow.names], events: flow.events }).filter((s) => !lazy.has(s.id) || s.id === flowId());
    for (const s of changed) {
      lazy.set(s.id, s);
    }
    const held = changed.filter((s) => slices.has(s.id));
    if (held.length) {
      publish(held);
    }
  };

  // The excerpts of the exchange an `exchange-body:` id names, built from the exchange slice the way the server reads the files.
  const bodySlice = (id) => {
    const source = slices.get(id.replace(/^exchange-body:/, "exchange:"));
    if (!id.startsWith("exchange-body:")) {
      return null;
    }
    if (!source) {
      return { id, as_of: 0, body: { id: id.split("/").pop(), gone: true } };
    }
    return { id, as_of: source.as_of, body: { id: source.body.id, ...exchangeText(source.body.slug, source.body.has_response, source.body.id),excerpt_truncated: false } };
  };

  // Publishes the lazy slices a poll names and withdraws the ones it no longer names, like a lease starting and lapsing.
  const lease = (sub) => {
    if (sub === null) {
      return;
    }
    const want = new Set(sub);
    const add = [...lazy.values()].filter((s) => want.has(s.id) && !slices.has(s.id));
    for (const id of want) {
      const body = bodySlice(id);
      if (body && !slices.has(id)) {
        add.push(body);
      }
    }
    const drop = [...slices.keys()].filter((id) => (lazy.has(id) || id.startsWith("exchange-body:")) && !want.has(id));
    if (add.length || drop.length) {
      publish(add, drop);
    }
  };
  load();

  const docs = (match, since = -1) =>
    [...slices.values()]
      .filter((s) => s.version > since && match(s.id))
      .sort((a, b) => (a.id < b.id ? -1 : 1));

  // Marks an open exchange answered and drops it from the team's open list, so its spark disappears.
  const answer = (open) => {
    const list = slices.get(openListId()).body;
    publish([
      { id: open.id, as_of: now(), body: { ...open.body, open: false, has_response: true, response_bytes: 160 } },
      { id: openListId(), as_of: now(), body: { ...list, open: list.open.filter((o) => o.id !== open.id) } },
      ...[bodySlice(open.id.replace(/^exchange:/, "exchange-body:"))].filter((b) => b && slices.has(b.id) && (b.body.response_excerpt = b.body.response_excerpt ?? `${open.body.slug}: done.`)),
    ]);
    flowAppend(open.body.to_name ?? "orchestrator", open.body.from_name ?? "orchestrator", 1);
  };
  let briefs = 0;
  const joined = [];
  let joinedCount = 0;
  let leaving = false;

  const listItems = (id) => {
    const body = slices.get(id)?.body;
    if (!/^list:(exchanges|feed|open):team:/.test(id) || !body) {
      return null;
    }
    // A page row is the exchange's id and body; the slice's version and as_of stay out of it.
    const full = id.startsWith("list:open:") ? (body.open ?? []).map((o) => o.id) : (body.ids ?? []).map((x) => `exchange:${DIR}/${x}`);
    return full.map((x) => slices.get(x)).filter(Boolean).map((s) => ({ id: s.id, body: s.body }));
  };

  return {
    get epoch() {
      return epoch;
    },
    // One held slice, as the model stores it.
    slice: (id) => slices.get(id),
    get seq() {
      return seq;
    },

    manifest(view) {
      const prefixes = VIEWS[view];
      if (!prefixes) {
        return null;
      }
      const match = matcher([`view:${view}`]);
      return { epoch, seq, building: false, slices: docs(match), pending: [] };
    },

    // Waits up to `wait` seconds for the first change after `since`, like the daemon's long-poll.
    async poll({ since, epoch: asked, sub, fresh: named = [], wait = 0, signal }) {
      const fresh = named.includes("*") ? [...named.filter((f) => f !== "*"), ...(sub ?? []).filter((id) => !id.startsWith("view:") && !id.startsWith("list:"))] : named;
      const gone = asked != null && asked !== epoch;
      if (gone || since > seq) {
        return { epoch, seq, resync: true, slices: [], removed: [] };
      }
      lease(sub);
      const match = matcher(sub);
      const read = () => ({
        epoch,
        seq,
        resync: false,
        slices: [...docs(match, since), ...fresh.filter((id) => match(id) && slices.get(id)?.version <= since).map((id) => slices.get(id))],
        removed: tombstones.filter((t) => t.version > since && match(t.id)).map((t) => t.id),
      });
      let page = read();
      if (page.slices.length || page.removed.length || wait <= 0) {
        return page;
      }
      await new Promise((done) => {
        const finish = () => {
          clearTimeout(timer);
          waiters.delete(finish);
          signal?.removeEventListener("abort", finish);
          done();
        };
        const timer = setTimeout(finish, wait * 1000);
        waiters.add(finish);
        signal?.addEventListener("abort", finish);
      });
      page = read();
      return page;
    },

    // A window of a list by cursor; null for an id that is not a list.
    list(id, { after = null, before = null, limit = 50, from = null, to = null, expand = null, member = null, with: peer = null, item = null } = {}) {
      const cap = Math.max(1, Math.min(MAX_LIST, Number(limit) || 50));
      const owner = TRANSCRIPT_NAMES.find((n) => id === transcriptListIdOf(n));
      if (owner) {
        return expand !== null ? expandOf(transcripts.get(owner), expand) : transcriptWindow(transcripts.get(owner), { after, before, cap });
      }
      if (id === `list:timeline:${DIR}`) {
        return timelineWindow({ now: now(), rowsAt: startedAt, after, before, cap, from, to });
      }
      if (id === `list:timeline-spans:${DIR}/${TEAM}`) {
        const at = now();
        const start = from === null ? at - RETENTION_MS : Number(from);
        const end = to === null ? at : Number(to);
        const team = slices.get(TEAM_ID)?.body;
        const exchanges = listItems(`list:exchanges:team:${DIR}/${TEAM}`) ?? [];
        const members = [team?.orchestrator, ...(team?.members ?? [])].filter(Boolean).map((m) => {
          const body = slices.get(m)?.body;
          return { id: m, name: body?.name ?? m.split("/").pop(), session: body?.session ?? null, tail: body?.activity ? slices.get(body.activity)?.body.activity ?? null : null, exchanges: exchanges.filter((s) => involves(s.body, body)) };
        });
        // Beyond retention nothing is held; the daemon's slack is a minute, for a client's range floored to the minute.
        const gone = end < at - RETENTION_MS;
        const rows = gone ? members.map((m) => ({ id: m.id, name: m.name, spans: [], marks: [] })) : lanesOf({ members, rows: timelineRows(startedAt), from: start, to: end, tailed: end >= at });
        return envelope({ rows: rows.slice(0, cap), total: rows.length, extra: { from_ms: start, to_ms: end, archived: start < at - RETENTION_MS - 60_000, more: false } });
      }
      if (id.startsWith("list:questions:")) {
        const held = lazy.get(id);
        return held ? questionsWindow(held.body.rows, { after, cap }) : null;
      }
      if (id.startsWith("list:plan-changes:")) {
        const held = lanes.get(id);
        return held ? changesWindow(held, { after, before, cap, item }) : null;
      }
      const all = listItems(id);
      if (all === null) {
        return null;
      }
      // With a peer, a row must name both: the exchanges between two members, either direction.
      const named = (s, id) => involves(s.body, slices.get(id)?.body);
      return window(member === null ? all : all.filter((s) => named(s, member) && (peer === null || named(s, peer))), { after, before, cap });
    },

    // The live ticker: one member's turn flips between busy and idle.
    tick() {
      const name = ACTIVITY_NAMES[turn++ % ACTIVITY_NAMES.length];
      if (name === "ultra-advisor") {
        return this.tick();
      }
      const id = activityIdOf(sessionOf(name));
      const cur = slices.get(id);
      if (!cur) {
        return seq;
      }
      const busy = cur.body.activity !== "working";
      const at = now();
      const iso = new Date(at).toISOString().replace(/\.\d{3}Z$/, "Z");
      publish([{ id, as_of: at, body: { ...cur.body, activity: busy ? "working" : "idle", tool: busy ? "Edit" : null, tool_at: busy ? iso : null, turn_at: iso } }]);
      if (busy) {
        this.appendTranscript(name);
      }
      if (turn % 5 === 0) {
        this.closeOneBrief();
      }
      return seq;
    },

    // One more turn at the end of a member's transcript; the header is republished if a client holds it.
    appendTranscript(name) {
      const entries = transcripts.get(name);
      appendTurn(entries, name);
      const id = transcriptIdOf(name);
      const next = { id, as_of: now(), body: headerOf(entries, now()) };
      lazy.set(id, next);
      if (slices.has(id)) {
        publish([next]);
      }
    },

    // Replaces a plan-changes lane's full record; its slice keeps the newest rows and is republished if a client holds it.
    setPlanChanges(id, changes) {
      lanes.set(id, changes);
      const next = { id, as_of: now(), body: changesBody(changes) };
      lazy.set(id, next);
      if (slices.has(id)) {
        publish([next]);
      }
    },

    // GET /v1/actions/<member id>, and /v1/actions for the root: the served action document, or null when the id names no member.
    actions(id) {
      if (id === null) {
        return rootActions();
      }
      const team = [...slices.values()].find((s) => s.id.startsWith("team:"));
      const m = slices.get(id)?.body;
      if (!team || !m) {
        return null;
      }
      const pid = m.liveness?.pid ?? null;
      return team.body.orchestrator === id
        ? orchestratorActions({ path: "/o/o.1", pid, team: team.body.name })
        : memberActions({ name: m.name, path: `/o/o.1/t/${team.body.name}/m/${m.name}`, pid, session: m.session ?? null, team: team.body.name });
    },

    // GET /v1/read-model: the counters, or null when the daemon runs with no read model (the route answers 404 no_read_model).
    readModel() {
      if (!hasReadModel) {
        return null;
      }
      readModelReads += 1;
      return { counters: { ...Object.fromEntries(READ_MODEL_COUNTERS.map((name) => [name, 0])), reread_retry: readModelReads * 3, coalesce_ms: 8 }, parked: [], as_of: now() };
    },

    setReadModel(on) {
      hasReadModel = on;
    },

    // The conversation is over: its header is tombstoned and never published again.
    endTranscript(name) {
      this.withdraw(transcriptIdOf(name));
    },

    // A leased slice the server stops publishing, with a tombstone to the clients holding it.
    withdraw(id) {
      lazy.delete(id);
      if (slices.has(id)) {
        publish([], [id]);
      }
    },

    // Answers the oldest open brief, so a spark disappears.
    closeOneBrief() {
      const open = exchangeIds.map((x) => slices.get(`exchange:${DIR}/${x}`)).find((s) => s?.body.open);
      if (!open) {
        return;
      }
      answer(open);
    },

    // A member joins or leaves (the served mock's --churn): up to CHURN_MAX workers join one at a time, then leave one at a
    // time. The team, its topology and the joined or departed member's slices are republished; nothing else changes.
    churn() {
      if (joined.length === CHURN_MAX) {
        leaving = true;
      } else if (joined.length === 0) {
        leaving = false;
      }
      if (leaving) {
        removeMember(joined.pop());
      } else {
        const name = `worker-${String(++joinedCount).padStart(2, "0")}`;
        joined.push(name);
        addMember(name);
      }
      const built = buildSlices(now(), runStart);
      const ids = new Set(built.map((s) => s.id));
      const fresh = built.filter((s) => /^(team:|team-summary:|topology:|processes:)/.test(s.id) || (/^(member:|activity:)/.test(s.id) && !slices.has(s.id)));
      const gone = [...slices.keys()].filter((id) => /^(member:|activity:)/.test(id) && !ids.has(id));
      publish(fresh, gone);
      return seq;
    },

    // Steady brief traffic for the served mock: a new brief while fewer than LIVE_OPEN are open, else the oldest open one is answered.
    traffic() {
      const list = slices.get(openListId()).body;
      if (list.open.length >= LIVE_OPEN) {
        answer(slices.get(list.open.at(-1).id));
        return seq;
      }
      const [from, to, slug] = TRAFFIC[briefs % TRAFFIC.length];
      const at = now();
      const stamp = new Date(at).toISOString().replace(/[-:]/g, "");
      const id = `${stamp.slice(0, 8)}-${stamp.slice(9, 15)}-t${String(briefs).padStart(3, "0")}`;
      briefs += 1;
      const created = new Date(at).toISOString().replace(/\.\d{3}Z$/, "Z");
      const lists = [`list:exchanges:dir:${DIR}`, `list:feed:dir:${DIR}`, `list:exchanges:team:${DIR}/${TEAM}`, `list:feed:team:${DIR}/${TEAM}`];
      publish([
        { id: `exchange:${DIR}/${id}`, as_of: at, body: exchangeBody(id, slug, from, to, created, null, false, null, etaFields(at, at)) },
        ...lists.map((l) => ({ id: l, as_of: at, body: { ...slices.get(l).body, ids: [id, ...slices.get(l).body.ids] } })),
        { id: openListId(), as_of: at, body: { ...list, open: [openEntry(id, from, to), ...list.open] } },
      ]);
      flowAppend(from, to, 0);
      return seq;
    },

    // A removal is a tombstone: the bare id arrives in `removed`.
    remove(id) {
      publish([], [id]);
    },

    // A new daemon instance: another epoch, the publish counter back at the start.
    restart() {
      epoch = `mock-${++epochCounter}-${now().toString(36)}`;
      load();
    },

    publish,

    // POST /ui/v1/team-rename on the mock (ah2-218): the fixture team only, its display name set or cleared; false for any other team.
    rename(team, displayName) {
      const t = slices.get(TEAM_ID);
      if (!t || t.body.name !== team) {
        return false;
      }
      publish([{ id: TEAM_ID, as_of: now(), body: { ...t.body, display_name: displayName } }]);
      return true;
    },
  };
}

function window(items, { after, before, cap }) {
  let start = 0;
  let end = items.length;
  if (after !== null) {
    start = (indexOf(after) ?? -1) + 1;
    end = Math.min(items.length, start + cap);
  } else if (before !== null) {
    end = indexOf(before) ?? 0;
    start = Math.max(0, end - cap);
  } else {
    end = Math.min(items.length, cap);
  }
  const page = items.slice(start, end);
  return envelope({
    rows: page,
    total: items.length,
    head: items.length ? cursorOf(0) : null,
    tail: items.length ? cursorOf(items.length - 1) : null,
    next: page.length ? (end < items.length ? cursorOf(end - 1) : null) : after,
    prev: page.length ? cursorOf(start) : before,
  });
}

// Timeline rows, newest first, by time range then cursor. A range wholly before the retention horizon is archived: no rows.
function timelineWindow({ now, rowsAt, after, before, cap, from, to }) {
  if (to !== null && Number(to) < now - RETENTION_MS) {
    return envelope({ rows: [], total: 0, extra: { archived: true } });
  }
  const all = timelineRows(rowsAt);
  const rows = all.filter((r) => (from === null || r.at >= Number(from)) && (to === null || r.at <= Number(to)));
  const page = window(rows, { after, before, cap });
  if (after === null && before === null && from !== null) {
    // The first page of a range also names, per subject with rows, its newest row before the range.
    const newest = new Map();
    for (const r of all) {
      if (r.at < Number(from) && (newest.get(r.subject)?.at ?? -Infinity) < r.at) {
        newest.set(r.subject, r);
      }
    }
    page.seed = [...newest.values()].map((r) => ({ subject: r.subject, state: r.to_state, at: r.at }));
  }
  return page;
}

export { TEAM };
