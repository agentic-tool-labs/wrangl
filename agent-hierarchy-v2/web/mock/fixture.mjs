// The mock team: every slice the web client reads, in the shapes the read-model lanes publish
// (ah/ui-r3 TreeComposers, ah/ui-r5b lanes) and ui-perf.md specifies where no lane exists yet.
// Deterministic for a given `now`; no randomness.

import { readFileSync } from "node:fs";
import { PRESETS } from "./presets.mjs";
import { GROOM } from "./backlog.mjs";
import { questionsBody, questionRows } from "./questions.mjs";
import { SCENARIOS } from "./scenarios.mjs";
import { withWork } from "./work.mjs";

export const DIR = "a1b2c3d4";
export const TEAM = "ship";
export const TEAM_ID = `team:${DIR}/${TEAM}`;
export const ORCH = "@orchestrator";

const MIN = 60_000;
// AH_MOCK_FIXTURE=team|lanes: a hand-written team (scenarios.mjs) in place of the tables below; null for every other fixture.
const SCENARIO = Object.hasOwn(SCENARIOS, globalThis.process?.env?.AH_MOCK_FIXTURE ?? "") ? SCENARIOS[process.env.AH_MOCK_FIXTURE] : null;
// A scenario lane's child-team members, name -> [role, child team]: they are no members of the ship team.
const STAFF = new Map(Object.entries(SCENARIO?.lanes ?? {}).flatMap(([l, rows]) => rows.map(([name, role]) => [name, [role, `${TEAM}-lane-${l}`]])));
const iso = (ms) => new Date(ms).toISOString().replace(/\.\d{3}Z$/, "Z");

// name, role, state, tool, minutes since the tool, branch
const MEMBERS = SCENARIO?.members.map((m) => [...m]) ?? [
  ["architect", "architect", "working", "Read", 1, "ah/ui-design"],
  ["implementor-1", "implementor", "working", "Edit", 0, "ah/ui-r6"],
  ["implementor-2", "implementor", "blocked", null, 7, "ah/ui-r2"],
  ["reviewer", "reviewer", "idle", null, 22, "ah/ui-r1"],
  ["docs-writer", "docs-writer", "idle", null, 41, "ah/docs"],
  ["task-runner", "task-runner", "working", "Bash", 0, null],
  ["ultra-advisor", "ultra-advisor", "failed", "WebFetch", 3, null],
];

// model and context tokens per member, as the daemon publishes them: tokens, never a fraction. No entry: no model, 40k tokens.
const CONTEXT = SCENARIO?.context ?? {
  architect: ["claude-opus-4-1-20250805", 84_000],
  "implementor-1": ["claude-sonnet-4-5[1m]", 412_345],
  "implementor-2": ["sonnet", 231_900],
  reviewer: ["claude-opus-4-5", 1_800],
  "docs-writer": ["haiku", 18_400],
  "task-runner": ["gpt-5", 36_200],
};

// The share of the last hour each member spent working, as the daemon publishes it. No entry: the activity carries none.
const WORKING_1H = SCENARIO?.working1h ?? { architect: 0.5, "implementor-1": 0.82, "implementor-2": 0.31, reviewer: 0.12, "task-runner": 0.64 };

const memberId = (name) => `member:${DIR}/${TEAM}/${name}`;
const worktreeId = (name) => `worktree:${DIR}/${TEAM}/${name}`;
const activityId = (session) => `activity:${DIR}/${session}`;
const session = (name) => `sess-${name}`;
const pidOf = (i) => 4100 + i * 11;
const startOf = (i) => `Sun Oct 18 0${i}:00:00 2026`;

const slice = (id, body, asOf) => ({ id, as_of: asOf, body });

// Exchanges: id, slug, from, to, minutes ago created, response minutes ago | null, parent, plan item | absent; a scenario's rows add
// title, brief and report.
const EXCHANGES = SCENARIO?.exchanges.map((e) => [...e]).sort((a, b) => (a[0] < b[0] ? -1 : 1)) ?? [
  ["20261018-0110-aa01", "ui-r6a", "orchestrator", "implementor-1", 52, null, null, "r6a"],
  ["20261018-0112-aa02", "ui-r2-seed", "orchestrator", "implementor-2", 47, null, null],
  ["20261018-0118-aa03", "ah/ui-r6", "orchestrator", "architect", 41, 33, null, "r6c"],
  ["20261018-0125-aa04", "ah/ui-r6", "orchestrator", "reviewer", 36, 21, null, "r6a"],
  ["20261018-0131-aa05", "docs-2.5", "orchestrator", "docs-writer", 30, 12, null],
  ["20261018-0140-aa06", "run-web-tests", "implementor-1", "task-runner", 22, 20, "20261018-0110-aa01", "r6a"],
  ["20261018-0144-aa07", "run-cli-tests", "implementor-2", "task-runner", 18, null, "20261018-0112-aa02", "r3"],
  ["20261018-0148-aa08", "tradeoff-mock", "architect", "ultra-advisor", 14, null, null],
  ["20261018-0151-aa09", "ah/ui-r6", "implementor-1", "orchestrator", 9, 8, "20261018-0110-aa01", "r6a"],
  ["20261018-0153-aa10", "r6-merge-answer", "orchestrator", "implementor-1", 7, 6, null, "r6e"],
];

// A larger team for timing runs: AH_MOCK_MEMBERS sets the member count, AH_MOCK_OPEN the number of open exchanges between
// members and AH_MOCK_DONE the number of answered ones.
// AH_MOCK_FIXTURE names a row of the preset table (presets.mjs); the realistic one is one team, not five.
// AH_MOCK_EXCERPT_BYTES sets the excerpt size alone. With no preset the fixture is the default one.
const NAMED_PRESET = globalThis.process?.env?.AH_MOCK_FIXTURE;
const PRESET = Object.hasOwn(PRESETS, NAMED_PRESET ?? "") ? PRESETS[NAMED_PRESET] : {};
const wanted = (key) => Number(globalThis.process?.env?.[key]) || PRESET[key] || 0;
const STATES = ["working", "idle", "blocked", "failed"];
for (let i = MEMBERS.length; i < wanted("AH_MOCK_MEMBERS"); i += 1) {
  MEMBERS.push([`worker-${String(i).padStart(2, "0")}`, "implementor", STATES[i % STATES.length], i % 2 ? "Edit" : null, i % 9, null]);
}
// The plan items the preset exchanges are about, in turn; every third one is about none.
const ITEM_CYCLE = ["r6a", "r6c", "r3", "guide", "r6e"];
const itemFor = (i) => (i % 3 === 0 ? undefined : ITEM_CYCLE[i % ITEM_CYCLE.length]);
// AH_MOCK_DONE answered exchanges between members, older than the fixture's own: a long feed for timing runs.
for (let i = wanted("AH_MOCK_DONE") - 1; i >= 0; i -= 1) {
  const from = MEMBERS[i % MEMBERS.length][0];
  const to = MEMBERS[(i * 5 + 2) % MEMBERS.length][0];
  EXCHANGES.unshift([`20261017-${String(i).padStart(6, "0")}-d`, `done-${i}`, from, from === to ? "orchestrator" : to, 600 + i, 590 + i, null, itemFor(i)]);
}
for (let i = 0; i < wanted("AH_MOCK_OPEN"); i += 1) {
  const from = MEMBERS[i % MEMBERS.length][0];
  const to = MEMBERS[(i * 7 + 3) % MEMBERS.length][0];
  EXCHANGES.push([`20261018-02${String(i).padStart(2, "0")}-b${String(i).padStart(2, "0")}`, `open-${i}`, from, from === to ? "orchestrator" : to, 5 + (i % 40), null, null, itemFor(i)]);
}

// AH_MOCK_INTERNAL=1: a busy team, with the exchanges the daemon classes internal. `class` here is fixture data, not the rule (MessageClass in the
// daemon); tests/web/msg-filter.test.mjs holds these slugs to its golden.
export const INTERNAL_SLUGS = ["pipeline-run-anchor", "release", "checkpoint", "ln-20261021-094825-15ri"];
const INTERNAL_IDS = new Set();
if (globalThis.process?.env?.AH_MOCK_INTERNAL === "1") {
  const internal = [
    ["20261018-0102-in01", INTERNAL_SLUGS[0], "orchestrator", "implementor-1", 58, null, null],
    ["20261018-0116-in02", INTERNAL_SLUGS[1], "orchestrator", "implementor-1", 44, 43, null],
    ["20261018-0126-in03", INTERNAL_SLUGS[2], "orchestrator", "reviewer", 34, 33, null],
    ["20261018-0127-in04", INTERNAL_SLUGS[3], "architect", "implementor-1", 33, 32, null],
  ];
  internal.forEach((e) => INTERNAL_IDS.add(e[0]));
  EXCHANGES.push(...internal);
  EXCHANGES.sort((a, b) => (a[0] < b[0] ? -1 : 1));
}

// A scenario exchange's own title, brief and report, by id.
const TEXT = Object.fromEntries(EXCHANGES.filter((e) => e.length > 8).map((e) => [e[0], e.slice(8)]));
// The team an exchange is in: a lane's child team when either party is its staff, else the ship team.
const teamOfParties = (from, to) => STAFF.get(from)?.[1] ?? STAFF.get(to)?.[1] ?? TEAM;

// A worker joining or leaving the team, for the served mock's --churn; buildSlices reflects it from then on.
export const addMember = (name, role = "implementor") => MEMBERS.push([name, role, "working", "Edit", 0, null]);
export const removeMember = (name) => {
  const i = MEMBERS.findIndex((m) => m[0] === name);
  i >= 0 && MEMBERS.splice(i, 1);
};

// The 3 newest exchanges a member is a party to (either direction), as slice ids, newest first.
const lastExchanges = (name) =>
  EXCHANGES.filter((e) => e[2] === name || e[3] === name)
    .map((e) => e[0])
    .sort()
    .reverse()
    .slice(0, 3)
    .map((id) => `exchange:${DIR}/${id}`);

// An exchange party is published as its role (to/from) and, when it has one, its member name (to_name/from_name); the orchestrator has no name.
const roleOf = (who) => (who === "orchestrator" ? who : MEMBERS.find((m) => m[0] === who)?.[1] ?? STAFF.get(who)[0]);
const nameOf = (who) => (who === "orchestrator" ? null : who);

// The team's open exchanges, newest first, each with the member ids the server resolved its parties to (null where nobody).
// The messages each unordered pair of members shares: the team exchange list's `pairs` (names in name order, a count for the run, and
// the creation times, newest first, of those made in the last hour) and `unresolved`. `t(minutes)` is the time that long ago.
function pairsOf(t, rows = EXCHANGES) {
  const counts = new Map();
  for (const [, , from, to, createdMin, respMin] of rows) {
    const [a, b] = [from, to].map((x) => (x === "orchestrator" ? "@orchestrator" : x)).sort();
    const c = counts.get(`${a}|${b}`) ?? { a, b, n: 0, recent_1h: [], last_response: null };
    c.n += 1;
    if (respMin !== null && respMin !== undefined) {
      c.last_response = Math.max(c.last_response ?? 0, t(respMin));
    }
    if (createdMin <= 60) {
      c.recent_1h.push(t(createdMin));
    }
    counts.set(`${a}|${b}`, c);
  }
  return { pairs: [...counts.values()].map((c) => ({ ...c, recent_1h: c.recent_1h.sort((x, y) => y - x) })), unresolved: { n: 0 } };
}

export const openListId = () => `list:open:team:${DIR}/${TEAM}`;
export const memberOfParty = (who) => (who === "orchestrator" ? `member:${DIR}/${TEAM}/${ORCH}` : STAFF.has(who) ? `member:${DIR}/${STAFF.get(who)[1]}/${who}` : memberId(who));
export const openList = (openIds, overdue = 0) => ({
  team: TEAM_ID,
  overdue,
  open: openIds.map((id) => {
    const e = EXCHANGES.find((x) => x[0] === id);
    return openEntry(id, e[2], e[3]);
  }),
});

const ETA_SECONDS = { small: 300, medium: 600, large: 1200 };
const ETA_OF = SCENARIO?.eta ?? { "run-cli-tests": "large", "tradeoff-mock": "large" };

// What the daemon adds to an open exchange when it serves the slice (ReadModel.Projected): nothing has been heard, so the base is `created`.
export function etaFields(createdMs, nowMs, eta = "small") {
  const span = ETA_SECONDS[eta] * 1000;
  const due = createdMs + span;
  const over = nowMs >= due;
  const soon = !over && nowMs >= due - Math.max(60_000, 0.2 * span);
  return {
    state: over ? "overdue" : soon ? "due-soon" : "on-time",
    due_at: iso(due),
    overdue_by: over ? Math.floor((nowMs - due) / 1000) : null,
    next_nudge: { step: over ? "silent" : "check-in", at: iso(over ? due + span : due) },
  };
}

// An exchange slice body; `from` and `to` are member names or "orchestrator". `eta` is etaFields for an open exchange, null for an answered one.
// `item` is the plan item the exchange is about, left out when it is about none.
export const exchangeBody = (id, slug, from, to, created, parent = null, responded = false, responseCreated = null, eta = null, item = undefined) => ({
  dir_id: DIR,
  id,
  slug,
  class: INTERNAL_IDS.has(id) ? "internal" : "work",
  ...(item ? { item } : null),
  title: TEXT[id]?.[0] ?? `Task ${slug}`,
  title_source: "title",
  to: roleOf(to),
  from: roleOf(from),
  to_name: nameOf(to),
  from_name: nameOf(from),
  team: teamOfParties(from, to),
  parent,
  created,
  response_created: responded ? responseCreated : null,
  has_request: true,
  has_response: responded,
  open: !responded,
  withdrawn: false,
  preview: exchangeText(slug, responded, id).request_excerpt.replace(/\s+/g, " ").slice(0, 160),
  request_bytes: Math.max(280, EXCERPT_BYTES),
  response_bytes: responded ? Math.max(160, wanted("AH_MOCK_EXCERPT_BYTES")) : null,
  excerpt_truncated: false,
  ...(responded ? null : eta),
});

// The excerpts of an exchange, which the server serves as the `exchange-body:` slice while a client holds it.
const EXCERPT_BYTES = wanted("AH_MOCK_EXCERPT_BYTES");
const sized = (text) => {
  const size = EXCERPT_BYTES;
  return size > text.length ? `${text} ${"lorem ipsum dolor sit amet ".repeat(Math.ceil(size / 27))}`.slice(0, size) : text;
};
export const exchangeText = (slug, responded, id = null) => ({
  request_excerpt: sized(TEXT[id]?.[1] ?? `Work tree for ${slug}. Implement the spec; report gaps up.`),
  response_excerpt: responded ? sized(TEXT[id]?.[2] ?? `${slug}: done, see the response file.`) : null,
});

// The open-list entry for an exchange between two parties.
export const openEntry = (id, from, to) => ({ id: `exchange:${DIR}/${id}`, to_member: memberOfParty(to), from_member: memberOfParty(from) });

const ST = ["done", "in_progress", "next", "blocked", "waiting_user", "waiting_orch", "not_started", "dropped"];
// area, item, title, state, holder, rank
const PLAN = SCENARIO?.plan ?? [
  ["ui", "r6a", "R6a data core", "in_progress", "implementor-1", 1],
  ["ui", "r6b", "R6b frame", "next", null, 2],
  ["ui", "r6c", "R6c graph view", "next", null, 3],
  ["ui", "r6d", "R6d hover, click, expand", "not_started", null, 4],
  ["ui", "r6e", "R6e tree view", "waiting_orch", "implementor-1", 5],
  ["ui", "r6f", "R6f timeline view", "not_started", null, 6],
  ["server", "r2", "R2 exchange lanes", "done", null, 1],
  ["server", "r3", "R3 tree composers", "in_progress", "implementor-2", 2],
  ["server", "r4", "R4 timeline store", "done", null, 3],
  ["server", "r5b", "R5b progress lane", "blocked", null, 4],
  ["docs", "guide", "Upgrade guide for 2.5", "waiting_user", "docs-writer", 1],
  ["docs", "changelog", "2.5 changelog", "done", null, 2],
  ["docs", "screens", "Screenshots", "dropped", null, 3],
  ["release", "tag", "Tag 2.5.0", "not_started", null, 1],
];

// The open run the team's pipeline is on: it started RUN_MIN before the fixture's now, and its anchor id is stamped with that local time.
export const RUN_MIN = 180;
const two = (n) => String(n).padStart(2, "0");
export const runIdOf = (start) => {
  const d = new Date(start);
  return `${d.getFullYear()}${two(d.getMonth() + 1)}${two(d.getDate())}-${two(d.getHours())}${two(d.getMinutes())}${two(d.getSeconds())}-${SCENARIO?.run ?? "r6ui"}`;
};

// The flow slices (ah-graph-views §1.2): a head and one immutable page per FLOW_PAGE events.
export const FLOW_PAGE = 200;
export const FLOW_EVENTS = 700;
export const MAX_NAMES = 64;
const MAX_TOTALS = 200;
export const flowId = () => `flow:${DIR}/${TEAM}`;
export const flowPageId = (run, k) => `flow-page:${DIR}/${TEAM}/${run}/${k}`;

// `names`: at most MAX_NAMES members; the parties past the cap fold to "other", appended last.
export const flowNames = (parties) => (parties.length > MAX_NAMES ? [...parties.slice(0, MAX_NAMES), "other"] : [...parties]);

// FLOW_EVENTS synthetic events between the parties (orchestrator first), spread over the run: half briefs, each answered by a report
// one to three minutes later. Deterministic; drawn from the members, not from the exchange slices. `pairs` ([from, to] name indexes):
// the briefs cycle through them instead, so only those pairs talk.
export function flowEvents(start, now, names, pairs = null) {
  const span = Math.floor((now - start) / 1000) - 300;
  const n = names.length;
  const events = [];
  for (let j = 0; j < FLOW_EVENTS / 2; j += 1) {
    const t = Math.floor((j * span) / (FLOW_EVENTS / 2));
    const a = pairs ? pairs[j % pairs.length][0] : (j * 7) % n;
    const b = pairs ? pairs[j % pairs.length][1] : (j * 13 + 3) % n === a ? (a === 0 ? 1 % n : 0) : (j * 13 + 3) % n;
    events.push([t, a, b, 0, j], [t + 60 + (j % 5) * 30, b, a, 1, j]);
  }
  return events.sort((x, y) => x[0] - y[0] || x[4] - y[4] || x[3] - y[3]).map(([t, a, b, k]) => [t, a, b, k]);
}

// The head and the full pages for a run's events.
export function flowSlices({ run, start, now, names, events }) {
  const full = run === null ? 0 : Math.floor(events.length / FLOW_PAGE);
  const pairs = new Map();
  for (const [, a, b, k] of events) {
    const row = pairs.get(`${a},${b}`) ?? [a, b, 0, 0];
    row[2 + k] += 1;
    pairs.set(`${a},${b}`, row);
  }
  const rows = [...pairs.values()].sort((x, y) => y[2] + y[3] - (x[2] + x[3]) || x[0] - y[0] || x[1] - y[1]);
  const totals = rows.length > MAX_TOTALS ? [...rows.slice(0, MAX_TOTALS - 1), rows.slice(MAX_TOTALS - 1).reduce((o, r) => [-1, -1, o[2] + r[2], o[3] + r[3]], [-1, -1, 0, 0])] : rows;
  const pages = Array.from({ length: full }, (_, k) => events.slice(k * FLOW_PAGE, (k + 1) * FLOW_PAGE));
  // oldest_ms is event 0's own time: synthetic events sit on whole seconds from start, so start + t·1000 is exact for them.
  const head = {
    run,
    start_ms: start,
    oldest_ms: events.length ? start + events[0][0] * 1000 : null,
    truncated: false,
    names,
    totals,
    page_size: FLOW_PAGE,
    // ponytail: the mock's pages only ever grow, so the count is enough to change pages_rev whenever a full page does.
    pages_rev: String(full),
    pages: pages.map((p) => p[0][0]),
    tail: events.slice(full * FLOW_PAGE),
  };
  return [slice(flowId(), head, now), ...pages.map((p, k) => slice(flowPageId(run, k), { run, k, events: p }, start + p.at(-1)[0] * 1000))];
}

// The item-msgs slices (§1.3): message counts per plan item and per stream, and each item's newest exchange ids. Like the daemon, only an
// exchange whose slug is a plan stream counts at all, keys are in ordinal order, and an item with no messages has no slice.
function itemMsgsSlices(streams, t) {
  const counted = EXCHANGES.filter((e) => streams.includes(e[1]));
  const tally = (key) => Object.fromEntries(Object.entries(Object.groupBy(counted.filter((e) => e[key]), (e) => e[key])).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)).map(([k, rows]) => [k, rows.length]));
  const counts = tally(7);
  return [
    slice(`item-msgs:${DIR}/${TEAM}`, { counts, by_stream: tally(1), truncated: false }, t(5)),
    ...Object.entries(counts).map(([item, n]) => slice(`item-msgs:${DIR}/${TEAM}/${item}`, { item, n, ids: counted.filter((e) => e[7] === item).map((e) => e[0]).sort().reverse().slice(0, 14) }, t(5))),
  ];
}

// Every slice of the fixture as {id, as_of, body}, ordered as the store publishes them. `runStart` defaults to RUN_MIN before `now`;
// a rebuild later in the same run passes the first one, so the run id holds still.
export function buildSlices(now, runStart = now - RUN_MIN * MIN) {
  const out = [];
  const t = (minutesAgo) => now - minutesAgo * MIN;

  // activity, one per session
  for (const [name, , state, tool, toolMin] of MEMBERS) {
    const toolAt = tool ? t(toolMin) : null;
    out.push(
      slice(
        activityId(session(name)),
        {
          dir_id: DIR,
          subject: session(name),
          activity: state,
          tool,
          tool_at: toolAt === null ? null : iso(toolAt),
          turn_at: iso(t(toolMin + 1)),
          context: { used: CONTEXT[name]?.[1] ?? 40_000, window_source: "unknown", at: iso(t(toolMin)) },
          ...(WORKING_1H[name] === undefined ? null : { working_1h: WORKING_1H[name], working_window_ms: 3_600_000 }),
        },
        t(toolMin),
      ),
    );
  }
  out.push(slice(activityId(session("orchestrator")), { dir_id: DIR, subject: session("orchestrator"), activity: SCENARIO?.leadIdle ? "idle" : "working", tool: SCENARIO?.leadIdle ? null : "Task", tool_at: iso(t(SCENARIO?.leadIdle ? 500 : 0)), turn_at: iso(t(SCENARIO?.leadIdle ? 501 : 1)), context: { used: 55_000, window_source: "unknown", model: "claude-opus-4-5", at: iso(t(0)) } }, t(0)));

  // members, worktrees
  const memberIds = [];
  const worktreeIds = [];
  const make = (name, role, index, branch) => {
    const id = name === ORCH ? `member:${DIR}/${TEAM}/${ORCH}` : memberId(name);
    const sess = session(name === ORCH ? "orchestrator" : name);
    out.push(
      slice(
        id,
        {
          id,
          name: name === ORCH ? "orchestrator" : name,
          role,
          ...(CONTEXT[name] ? { model: CONTEXT[name][0] } : {}),
          ...(LEADS[name] ?? {}),
          session: sess,
          activity: activityId(sess),
          liveness: { at: iso(t(60)), pid: pidOf(index), pid_start: startOf(index), session_start: null },
          placement: { transport_id: `tx-${index}`, stream: branch },
          last_exchanges: lastExchanges(name === ORCH ? "orchestrator" : name),
          node_path: `/t/${DIR}/${TEAM}/m/${encodeURIComponent(name)}`,
        },
        t(60),
      ),
    );
    if (branch !== null || name === ORCH) {
      const wt = worktreeId(name === ORCH ? "orchestrator" : name);
      out.push(slice(wt, { id: wt, team: TEAM, assigned_member: name === ORCH ? "orchestrator" : name, grant_state: "off", work_root: `/work/ah/.claude/worktrees/${name === ORCH ? "main" : name === "architect" || name === "reviewer" ? "design" : name}`, stream: branch ?? "ah/ui-perf" }, t(60)));
      worktreeIds.push(wt);
    }
    return id;
  };
  const orchId = make(ORCH, "orchestrator", 0, SCENARIO ? null : "ah/ui-perf");
  MEMBERS.forEach(([name, role, , , , branch], i) => memberIds.push(make(name, role, i + 1, branch)));
  if (LANE_MODE === "lanes") {
    for (const l of LIVE_LANES) {
      const child = `${TEAM}-lane-${l}`;
      const ids = (LANE_STAFF[l] ?? []).map(([name, role, state, tool, stream], j) => {
        const id = `member:${DIR}/${child}/${name}`;
        const sess = session(`${child}-${name}`);
        out.push(slice(activityId(sess), { dir_id: DIR, subject: sess, activity: state, tool, tool_at: iso(t(j)), turn_at: iso(t(j + 1)), context: { used: 30_000 + j * 9_000, window_source: "unknown", model: "claude-sonnet-4-5", at: iso(t(j)) } }, t(j)));
        out.push(slice(id, { id, name, role, session: sess, activity: activityId(sess), lead: { team: child, name: `so-lane-${l}` }, ...(stream ? { placement: { transport_id: `tx-${child}-${j}`, stream } } : null), last_exchanges: [] }, t(60)));
        return id;
      });
      out.push(slice(`team:${DIR}/${child}`, { name: child, dir_id: DIR, orchestrator: `member:${DIR}/${child}/@orchestrator`, members: ids, worktrees: [], lane: { stream: `lane-${l}`, parent_team: TEAM, state: "open", manager_rev: "f85fa77eb378", reported: { state: "working", summary: `lane ${l} in progress`, updated: iso(t(3)) } } }, t(7)));
      // a lane team has no open run of its own: its questions slice says so (the Inbox counts its decision cards from it)
      out.push(slice(`questions:${DIR}/${child}`, { run: null, open: [], recent: [], open_total: 0, truncated: false }, t(3)));
      // ah-ui-ia U0 (b): the lane team's summary names its lane, so Home can nest it under its parent
      const staff = LANE_STAFF[l] ?? [];
      const count = (state) => staff.filter(([, , s]) => s === state).length;
      out.push(slice(`team-summary:${DIR}/${child}`, { team: `team:${DIR}/${child}`, name: child, work_root: null, branch: staff[0]?.[4] ?? null, running: staff.length > 0, members: staff.length, working: count("working"), blocked: count("blocked"), needs_you: count("blocked"), state: count("blocked") > 0 ? "needs_you" : count("working") > 0 ? "working" : "idle", in_flight: 0, stale: 0, streams_open: 1, streams_total: 1, items_done: 0, items_total: 0, oldest_in_flight_at: null, updated_at: iso(t(3)), feed_newest_at: null, alerts: staff.filter(([, , st]) => st === "blocked").map(([name]) => ({ kind: "blocked", member: `member:${DIR}/${child}/${name}`, since: iso(t(9)), text: "Waiting on a permission" })), member_states: Object.fromEntries(staff.map(([name, , st]) => [`member:${DIR}/${child}/${name}`, { state: st }])), lane: { parent_team: TEAM, stream: `lane-${l}` } }, t(3)));
    }
  }

  const parentChild = memberIds.map((to) => ({ from: orchId, to }));
  const memberWorktree = [orchId, ...memberIds].flatMap((m) => {
    const wt = worktreeId(m.split("/").pop() === ORCH ? "orchestrator" : m.split("/").pop());
    return worktreeIds.includes(wt) ? [{ from: m, to: wt }] : [];
  });
  // AH_MOCK_DUTY=<member>: that member is the deputy on duty (b368), as the team slice serves it
  const onDuty = globalThis.process?.env?.AH_MOCK_DUTY;
  const deputy = onDuty ? memberIds.find((id) => id.endsWith(`/${onDuty}`)) : undefined;
  const duty = deputy ? { duty: { deputy: deputy.split("/").pop(), since: iso(t(25)), cause: "user", member: deputy } } : {};
  out.push(slice(TEAM_ID, { name: TEAM, dir_id: DIR, orchestrator: orchId, members: memberIds, ...duty, ...laneFacts(t), pipeline: { run: runIdOf(runStart), waiting_on_user: questionsBody(now, SCENARIO?.decisions).open_total, decisions_count: 14, last_decision_at: iso(t(7)), scope: "dir", items: SCENARIO?.pipelineItems ?? [{ slug: "ui-r6a", rounds: 2, round_cap: 3 }] }, worktrees: worktreeIds }, t(7)));
  out.push(slice(`topology:${DIR}/${TEAM}`, { team: TEAM_ID, nodes: { members: [orchId, ...memberIds], worktrees: worktreeIds }, edges: { parent_child: parentChild, member_worktree: memberWorktree } }, t(60)));
  out.push(slice("list:teams", { teams: [{ id: TEAM_ID, name: TEAM, dir_id: DIR }, ...(SCENARIO ? [] : [{ id: `team:${DIR}/${OTHER_TEAM}`, name: OTHER_TEAM, dir_id: DIR }])] }, t(7)));

  // processes: the ultra-advisor's pid is gone
  const live = [{ pid: pidOf(0), start: startOf(0) }];
  MEMBERS.forEach(([name], i) => {
    if (name !== "ultra-advisor") {
      live.push({ pid: pidOf(i + 1), start: startOf(i + 1) });
    }
  });
  out.push(slice(`processes:${DIR}`, { pids: live.sort((a, b) => a.pid - b.pid) }, 0));

  // exchanges
  const ids = [];
  for (const [id, slug, from, to, createdMin, respMin, parent, item] of EXCHANGES) {
    const responded = respMin !== null;
    ids.push(id);
    out.push(slice(`exchange:${DIR}/${id}`, exchangeBody(id, slug, from, to, iso(t(createdMin)), parent, responded, responded ? iso(t(respMin)) : null, responded ? null : etaFields(t(createdMin), now, ETA_OF[slug]), item), t(respMin ?? createdMin)));
  }
  const newestFirst = [...ids].reverse();
  out.push(slice(`list:exchanges:dir:${DIR}`, { dir_id: DIR, ids: newestFirst }, t(6)));
  out.push(slice(`list:feed:dir:${DIR}`, { dir_id: DIR, ids: newestFirst }, t(6)));
  // A team's exchange, feed and open lists, over the exchanges in that team.
  const rowsOf = (team) => EXCHANGES.filter((e) => teamOfParties(e[2], e[3]) === team);
  const teamLists = (team) => ["exchanges", "feed"].map((kind) => slice(`list:${kind}:team:${DIR}/${team}`, { team: `team:${DIR}/${team}`, ids: rowsOf(team).map((e) => e[0]).reverse(), ...pairsOf(t, rowsOf(team)) }, t(6)));
  const openOf = (team) => {
    const open = rowsOf(team).filter((e) => e[5] === null);
    const body = openList(open.map((e) => e[0]).reverse(), open.filter((e) => etaFields(t(e[4]), now, ETA_OF[e[1]]).state === "overdue").length);
    return slice(`list:open:team:${DIR}/${team}`, { ...body, team: `team:${DIR}/${team}` }, t(6));
  };
  out.push(...teamLists(TEAM));
  out.push(slice(`queue:${DIR}/${TEAM}`, queueRows(t), t(3)));
  out.push(slice(`sitrep:${DIR}/${TEAM}`, sitrepBody(t, now), t(3)));
  out.push(openOf(TEAM));
  for (const child of new Set([...STAFF.values()].map(([, team]) => team))) {
    out.push(...teamLists(child), openOf(child));
  }

  // status: the status.json whitelist, one team
  out.push(
    slice(
      `status:${DIR}`,
      {
        schema: 1,
        written_at: iso(t(0)),
        expires_at: iso(now + 60 * MIN),
        enabled: true,
        member_sessions: MEMBERS.map(([n]) => session(n)),
        teams: [
          {
            team: TEAM,
            members: MEMBERS.map(([name, role, state, tool, toolMin]) => ({ name, role, live: name !== "ultra-advisor", activity: state, activity_at: iso(t(toolMin)), last_tool: tool, last_tool_at: tool ? iso(t(toolMin)) : null })),
            dispatches: [],
            dispatches_truncated: 0,
            pipeline: null,
          },
        ],
        timeline: [],
        owners: [],
      },
      t(0),
    ),
  );

  // timeline summary (rows are served by /list)
  const rows = timelineRows(now);
  out.push(slice(`timeline:${DIR}`, { total: rows.length, retention_days: 7, head_cursor: rows[0].seq, tail_cursor: rows[rows.length - 1].seq, oldest_at: rows[rows.length - 1].at, newest_at: rows[0].at }, 0));

  // plan
  const counts = Object.fromEntries(ST.map((s) => [s, PLAN.filter((p) => p[3] === s).length]));
  const decisions = { by_user: 5, by_team: 9, waiting: 2, scope: "dir" };
  const ship = shipStreams(now);
  out.push(slice(`plan:${DIR}/${TEAM}`, { team: TEAM, dir_id: DIR, work_root: WORK_ROOT, streams: ship.streams, counts, as_of: t(5), decisions }, t(5)));
  out.push(slice(`list:plan:${DIR}/${TEAM}`, { team: TEAM, items: planItems(now) }, t(5)));
  out.push(slice(`list:plan-changes:${DIR}/${TEAM}`, { team: TEAM, changes: planChanges(now) }, t(5)));
  for (const [stream, items] of Object.entries(ship.items)) {
    out.push(slice(`plan-items:${DIR}/${TEAM}/${stream}`, { stream, items: items.map((item, i) => parented(item, EPICS.checklist[stream]?.[i])), truncated: false }, t(5)));
  }
  out.push(...itemMsgsSlices(Object.keys(ship.items), t));
  const names = flowNames([orchId, ...memberIds]);
  // A scenario's flow follows its own ship-team exchanges, so only the pairs that brief each other have arcs.
  const pairs = SCENARIO ? rowsOf(TEAM).map((e) => [names.indexOf(memberOfParty(e[2])), names.indexOf(memberOfParty(e[3]))]) : null;
  out.push(...flowSlices({ run: runIdOf(runStart), start: runStart, now, names, events: flowEvents(runStart, now, names, pairs) }));

  // a second team, so the all-plans list has more than one row; a scenario is one team
  if (!SCENARIO) {
    const other = otherPlan(now);
    out.push(slice(`plan:${DIR}/${OTHER_TEAM}`, { team: OTHER_TEAM, dir_id: DIR, work_root: WORK_ROOT, streams: [{ stream: "docs", owner: "docs-writer", branch: "ah/docs", work_tree: "docs", goal: "Release notes", state: "open", source: "checklist", counts: { done: 1, in_progress: 1, next: 1, dropped: 1 }, as_of: t(90) }], counts: Object.fromEntries(ST.map((s) => [s, other.items.filter((p) => p.state === s).length])), as_of: t(90), decisions }, t(90)));
    out.push(slice(`plan-items:${DIR}/${OTHER_TEAM}/docs`, { stream: "docs", items: other.items.filter((p) => p.state !== "dropped").map((p) => ({ key: `docs:${p.item}`, title: p.title, state: p.state, ...(p.holder ? { holder: p.holder } : null), done_seen_at: null })), truncated: false }, t(90)));
    out.push(slice(`list:plan:${DIR}/${OTHER_TEAM}`, { team: OTHER_TEAM, items: other.items }, t(90)));
    out.push(slice(`list:plan-changes:${DIR}/${OTHER_TEAM}`, { team: OTHER_TEAM, changes: other.changes }, t(90)));
  }
  // the project's backlog: one slice per hierarchy dir. Two items are claimed so the board shows a holder and a state.
  const CLAIMED = { b1: ["in_progress", "implementor-1"], b8: ["blocked", "ux-engineer"] };
  const items = GROOM.map(([key, title, wave, size, theme, rank, outcome, issue]) => ({
    key, title, state: CLAIMED[key]?.[0] ?? "not_started", ...(CLAIMED[key] ? { holder: CLAIMED[key][1] } : null), wave, size, theme, rank, ...(outcome ? { outcome } : null),
    ...(issue ? { ext: `github:example/ah2#${issue}` } : null),
    ...EPICS.backlog[key],
    ...(issue ? { issue: { number: issue, url: `https://github.com/example/ah2/issues/${issue}` } } : null),
  }));
  out.push(slice(`backlog:${DIR}`, { items, health: { untriaged: 0, stale: 2, due: true, due_since: iso(t(180)) } }, t(30)));
  // the questions put to each team (leased by the Attention tab) and their history (the Q&A list)
  // AH_MOCK_WAITING=1: the default fixture's one waiting checklist item, answerable from its Board card and the Inbox (off by default,
  // so the Inbox's four decisions stay the tests' baseline)
  const waiting = SCENARIO || globalThis.process?.env?.AH_MOCK_WAITING !== "1" ? [] : [{ key: "ah/ui-r6:guide", item: "guide", stream: "ah/ui-r6", title: "Upgrade guide for 2.5", text: "Which font should the guide's code samples use?", options: ["JetBrains Mono", "System monospace"], since: iso(t(40)), owner: "docs-writer" }];
  out.push(slice(`questions:${DIR}/${TEAM}`, questionsBody(now, SCENARIO?.decisions, waiting), t(12)));
  out.push(slice(`list:questions:${DIR}/${TEAM}`, { rows: questionRows(now, SCENARIO?.decisions) }, t(12)));
  return wanted("AH_MOCK_WORK") ? withWork(out, slice, DIR, now) : out;
}

export const OTHER_TEAM = "release";

// The epics data (AH_MOCK_EPICS=1; not a PRESETS row, so the E4 harness does not run it): backlog kind/parent and checklist parents, placed where the daemon puts them (backlog: after ext;
// plan-items: before done_seen_at). Two epics, three deliverables (b19 has no parent), backlog tasks under deliverables, checklist items
// parented across two streams, and one invalid link: b4 is a task under the task b3. Without the knob both tables are empty. A scenario
// brings its own.
export const EPICS = SCENARIO
  ? SCENARIO.epics
  : wanted("AH_MOCK_EPICS")
  ? {
      backlog: {
        b1: { kind: "epic" },
        b7: { kind: "epic" },
        b2: { kind: "deliverable", parent: "b1" },
        b8: { kind: "deliverable", parent: "b7" },
        b19: { kind: "deliverable" },
        b3: { parent: "b2" },
        b5: { parent: "b2" },
        b13: { parent: "b8" },
        b4: { parent: "b3" },
      },
      checklist: { "ah/ui-r6": ["b2", "b2", "b8"], "docs-2.5": ["b19", "b8"] },
    }
  : { backlog: {}, checklist: {} };

function parented(item, parent) {
  if (!parent) {
    return item;
  }

  const { done_seen_at, ...rest } = item;
  return { ...rest, parent, done_seen_at };
}

const WORK_ROOT = "/work/ah/.claude/worktrees/main";

// The checklist fields the daemon adds to a plan item, each only when the item has it; refs only when it has any. Most items have none.
const CHECKLIST = SCENARIO?.checklist ?? {
  r6a: { role: "implementor", todo: "land the data core", refs: ["20261018-0110-aa01"], stateMin: 300, updatedMin: 5 },
  r6e: { role: "implementor", todo: "answer implementor-1: STATUS", stateMin: 40, updatedMin: 40 },
  r5b: { role: "implementor", todo: "unblock on the R3 merge", stateMin: 90, updatedMin: 12 },
};

function checklistFields(item, t) {
  const c = CHECKLIST[item];
  return c ? { role: c.role, todo: c.todo, ...(c.refs ? { refs: c.refs } : null), state_since: iso(t(c.stateMin)), updated_at: iso(t(c.updatedMin)) } : null;
}

// The team's queue slice is the daemon's own recorded body (the Ah.Watch queue-slice golden) with its times moved so the newest item has
// been queued three minutes: the first row is then past its stall_at and the second is not.
const QUEUE_GOLDEN = JSON.parse(readFileSync(new URL("../../native/tests/Ah.Watch.Tests/Goldens/queue-slice.json", import.meta.url), "utf8"));

function queueRows(t) {
  const times = ["queued_since", "stall_at", "last_dispatch_at"];
  const newest = Math.max(...QUEUE_GOLDEN.map((r) => Date.parse(r.queued_since)));
  const shift = t(3) - newest;
  return QUEUE_GOLDEN.map((r) => ({ ...r, ...Object.fromEntries(times.filter((k) => r[k] !== null).map((k) => [k, iso(Date.parse(r[k]) + shift)])) }));
}

// The team's Sitrep (ah-ui-ia §3.3, §3.3a), from the same plan items and queue rows the other slices serve: done items with their
// done times, ship and merge records interleaved with them (one unconfirmed; one from a lane ledger when the scenario has lanes),
// the open items newest last activity first in In progress, Blocked and Stale (ah-sitrep-events §4), and the queue rows then a
// backlog row. SITREP_SHOW stages, for the Sitrep only, the states and idle times the plan items do not have: in_review and approved
// rows, and items idle past the stale line. Times are epoch ms, as the daemon serves them.
const STALE_MIN = 24 * 60;
const SITREP_SHOW = {
  "ah/ui-r6:r3": { state: "in_review" },
  "docs-2.5:d1": { state: "approved" },
  "docs-2.5:d2": { idleMin: 31 * 60 },
  "lane-board:lb1": { state: "in_review" },
  "lane-delivery:ld1": { state: "approved" },
  "lane-board:lb2": { idleMin: 26 * 60 },
};

function sitrepBody(t, now) {
  const laneStreams = new Set(LANE_MODE === "lanes" ? LIVE_LANES.map((l) => `lane-${l}`) : []);
  const laneOf = (stream) => (laneStreams.has(stream) ? stream : null);
  const all = Object.entries(shipStreams(now).items).flatMap(([stream, items]) => items.map((i) => ({ ...i, stream })));
  const done = all.filter((i) => i.state === "done").map((i, n) => ({ ...i, at: i.done_seen_at ?? t(90 + n * 30) })).sort((a, b) => b.at - a.at || (a.key < b.key ? -1 : 1));
  const top = done.slice(0, 10);
  const floor = top.length ? top[top.length - 1].at : -Infinity;
  const firstLane = [...laneStreams][0] ?? null;
  // record k sits between done items k and k+1, or just after the newest when there are too few, so all stay above the floor
  const between = (k) => (top.length > k + 1 ? Math.round((top[k].at + top[k + 1].at) / 2) : (top[0]?.at ?? t(60)) + (k + 1) * 30_000);
  const records = [
    { kind: "ship", key: "ship:4e02a21b3c5d:main", sha: "4e02a21b3c5d", ref: "main", tag: null, confirmed: true, team: TEAM, lane: null, at: between(0) },
    { kind: "merge", key: "merge:9e9b4455a1f0:ah/graph-batch", sha: "9e9b4455a1f0", ref: "ah/graph-batch", into: "main", team: TEAM, lane: null, at: between(1) },
    { kind: "ship", key: "ship:b4e5de963aa7:ah/release-2.5.0-pre.1", sha: "b4e5de963aa7", ref: "ah/release-2.5.0-pre.1", tag: true, confirmed: false, team: TEAM, lane: null, at: between(2) },
    ...(firstLane ? [{ kind: "ship", key: `ship:6b8160b4d2e1:ah/${firstLane}`, sha: "6b8160b4d2e1", ref: `ah/${firstLane}`, tag: null, confirmed: true, team: `${TEAM}-${firstLane}`, lane: firstLane, at: between(3) }] : []),
  ].filter((r) => r.at >= floor);
  const completed = [
    ...top.map((i) => ({ at: i.at, record: false, row: { kind: "item", key: i.key, title: i.title, team: TEAM, stream: i.stream, lane: laneOf(i.stream), holder: i.holder ?? null, done_at: i.at } })),
    ...records.map((r) => ({ at: r.at, record: true, row: r })),
  ].sort((a, b) => b.at - a.at || a.record - b.record || (a.row.key < b.row.key ? -1 : 1)).map((x) => x.row);
  const since = (i) => Date.parse(i.state_since ?? i.updated_at ?? "") || null;
  const MOVING = ["in_progress", "waiting_orch", "in_review", "approved"];
  const HELD = ["blocked", "waiting_user"];
  // an item with no time of its own gets a staged recent one, so every open row has a last activity
  const open = all.map((i) => ({ ...i, ...SITREP_SHOW[i.key] })).filter((i) => MOVING.includes(i.state) || HELD.includes(i.state))
    .map((i, n) => ({ ...i, last: i.idleMin ? t(i.idleMin) : (Date.parse(i.updated_at ?? "") || null) ?? t(15 + n * 9) }))
    .sort((a, b) => b.last - a.last || (a.key < b.key ? -1 : 1));
  const isStale = (i) => now - i.last > STALE_MIN * MIN;
  // the daemon's ChecklistWork, the mock's holder of a recently active item standing in for a live session
  const workOf = (i) => (i.state === "waiting_user" ? "waiting_you"
    : isStale(i) ? "stale"
      : i.state === "in_progress" && i.holder ? "working"
        : i.state === "blocked" ? "blocked"
          : i.state === "in_progress" ? "no_one" : "with_lead");
  const inWork = (...works) => open.filter((i) => works.includes(workOf(i)));
  const waiting = inWork("waiting_you");
  const moving = inWork("working");
  const held = inWork("blocked");
  const notMoving = inWork("no_one", "with_lead");
  const stale = inWork("stale");
  const rowOf = (i) => ({ key: i.key, title: i.title, team: TEAM, stream: i.stream, lane: laneOf(i.stream), holder: i.holder ?? null, role: i.role ?? null, state: i.state, work: workOf(i), note: i.note ?? null, since: since(i) ?? i.last, last_activity: i.last });
  const queue = queueRows(t).map((r) => {
    const [stream, item] = r.item.split("/");
    return { key: `${stream}:${item}`, title: r.title, team: TEAM, stream, lane: laneOf(stream), role: r.role, queued_since: Date.parse(r.queued_since), stall_at: Date.parse(r.stall_at), idle_eligible: r.idle_eligible, could_take: r.eligible_members, source: "queue" };
  });
  const backlog = [{ key: "bl-sitrep-export", title: "Export the Sitrep as a status note", team: null, stream: null, lane: null, role: "implementor", queued_since: null, stall_at: null, idle_eligible: false, could_take: [], source: "backlog" }];
  // as_of over every row read, not only the rows shown
  const times = [...completed.map((r) => r.done_at ?? r.at), ...open.map((i) => i.last), ...queue.map((r) => r.queued_since)].filter((v) => v !== null);
  return {
    regions: [
      { col: "c", list: "completed", total: "completed_total", label: "Completed", optional: false, folded: false },
      { col: "p", list: "in_progress", total: "in_progress_total", label: "In progress", optional: false, folded: false },
      { col: "n", list: "up_next", total: null, label: "Up next", optional: false, folded: false },
      { col: "w", list: "waiting_on_you", total: "waiting_on_you_total", label: "Waiting on you", optional: true, folded: false },
      { col: "b", list: "blocked", total: "blocked_total", label: "Blocked", optional: true, folded: false },
      { col: "m", list: "not_moving", total: "not_moving_total", label: "Report in, no one on it", optional: true, folded: true },
      { col: "s", list: "stale", total: "stale_total", label: "Stale", optional: true, folded: true },
      { col: "o", list: "up_next_old", total: "up_next_old_total", label: "Up next, old", optional: true, folded: true },
    ],
    as_of: times.length ? Math.max(...times) : null,
    completed_total: done.length,
    in_progress_total: moving.length,
    blocked_total: held.length,
    stale_total: stale.length,
    waiting_on_you_total: waiting.length,
    not_moving_total: notMoving.length,
    completed,
    waiting_on_you: waiting.slice(0, 10).map(rowOf),
    not_moving: notMoving.slice(0, 10).map(rowOf),
    in_progress: moving.slice(0, 10).map(rowOf),
    blocked: held.slice(0, 10).map(rowOf),
    stale: stale.slice(0, 10).map(rowOf),
    up_next: [...queue, ...backlog].slice(0, 10),
  };
}

// The lane facts the daemon adds to team and member slices, from its own recorded bodies (the Ah.Watch lane-slices golden): a manager's
// `streams`, a confirmed lane's `lane`, and members' `lead` and `routes_to`. The ship team is the golden's manager; AH_MOCK_LANES picks
// what it shows instead: lane (the golden's confirmed lane), claim (`lane_claim` alone), truncated (100 of 150 streams) or lanes (a
// manager with three lane sub-orchestrators among its members and members lent to them, the shape of a live team running lanes).
const LANE_GOLDEN = Object.fromEntries(JSON.parse(readFileSync(new URL("../../native/tests/Ah.Watch.Tests/Goldens/lane-slices.json", import.meta.url), "utf8")).slices.map((s) => [s.id, s.body]));
const goldenOf = (suffix) => Object.entries(LANE_GOLDEN).find(([id]) => id.endsWith(suffix))[1];
// A scenario decides alone: lanes when it has them, else none (no lane facts at all).
const LANE_MODE = SCENARIO ? (SCENARIO.lanes ? "lanes" : "none") : process.env.AH_MOCK_LANES ?? "manager";

function laneFacts(t) {
  if (LANE_MODE === "none") {
    return {};
  }
  const manager = goldenOf("/mgr");
  if (LANE_MODE === "lane") {
    const lane = goldenOf("/mgr-s1").lane;
    return { lane: { ...lane, reported: { ...lane.reported, updated: iso(t(4)) } } };
  }
  if (LANE_MODE === "claim") {
    return { lane_claim: { parent_team: manager.name } };
  }
  if (LANE_MODE === "truncated") {
    const [row] = manager.streams;
    const many = Array.from({ length: 100 }, (_, i) => {
      const { child_team: _child, ...rest } = row;
      return { ...rest, name: `s${String(i + 1).padStart(3, "0")}`, state: i % 7 === 0 ? "done" : "open", branch: `b-s${i + 1}`, sub_orchestrator: `so-s${i + 1}` };
    });
    return { streams: many, streams_total: 150 };
  }
  if (LANE_MODE === "lanes") {
    const [row] = manager.streams;
    return { streams: LIVE_LANES.map((l) => ({ ...row, name: `lane-${l}`, branch: `ah/lane-${l}`, sub_orchestrator: `so-lane-${l}`, child_team: `${TEAM}-lane-${l}` })) };
  }
  return { streams: manager.streams };
}

// Members' routing, from the golden's members: one led by a lane's sub-orchestrator, one whose lane was dissolved (no lead, routed home).
const routing = ({ lead, routes_to }) => ({ ...(lead ? { lead } : {}), ...(routes_to ? { routes_to } : {}) });
const LIVE_LANES = SCENARIO ? Object.keys(SCENARIO.lanes ?? {}) : ["db", "ops", "ui"];
// The members each lane spawned into its own child team, beside the members lent to it. A scenario's rows add the member's stream.
const LANE_STAFF = SCENARIO?.lanes ?? { db: [["db-impl-1", "implementor", "working", "Edit"]], ui: [["ui-impl-1", "implementor", "working", "Edit"], ["ui-rev-1", "reviewer", "idle", null]] };
const lentTo = (l) => routing({ lead: { team: `${TEAM}-lane-${l}`, name: `so-lane-${l}` }, routes_to: { team: `${TEAM}-lane-${l}`, name: "orchestrator", via: "lead" } });
// A scenario lends no members: its lanes are staffed by their own child teams.
const LEADS = SCENARIO
  ? {}
  : LANE_MODE === "lanes"
  ? { reviewer: lentTo("db"), "implementor-1": lentTo("ui"), "implementor-2": lentTo("ui"), "implementor-3": lentTo("db"), "implementor-4": lentTo("ops"), "implementor-5": lentTo("ops"), "reviewer-2": lentTo("ui") }
  : { "implementor-1": routing(goldenOf("/mgr/dev")), "implementor-2": routing(goldenOf("/mgr/gone")) };
if (LANE_MODE === "lanes" && !SCENARIO) {
  LIVE_LANES.forEach((l, i) => MEMBERS.push([`so-lane-${l}`, "sub-orchestrator", i === 1 ? "idle" : "working", i === 1 ? null : "Task", i * 4, `ah/lane-${l}`]));
  [["implementor-3", "working", "Edit"], ["implementor-4", "working", "Bash"], ["implementor-5", "idle", null], ["implementor-6", "idle", null], ["reviewer-2", "working", "Read"]].forEach(([n, st, tool], i) => MEMBERS.push([n, n.replace(/-\d+$/, ""), st, tool, i * 3, null]));
}

// The ship team's `plan:` streams and their `plan-items:`. Fields that are zero, empty or false are left out here; the mock spells them out as the daemon does (`servedStream`).
function shipStreams(now) {
  const t = (m) => now - m * MIN;
  const count = (state) => PLAN.filter((p) => p[3] === state).length;
  const compact = (o) => Object.fromEntries(Object.entries(o).filter(([, v]) => v !== 0 && v !== "" && v !== false));
  if (SCENARIO) {
    const items = Object.fromEntries(SCENARIO.streams.map(({ stream }) => [stream, PLAN.filter((p) => p[0] === stream).map(([, item, title, state, holder], i) => ({ key: `${stream}:${item}`, title, state, ...(holder ? { holder } : null), ...checklistFields(item, t), done_seen_at: state === "done" ? t(60 + i) : null }))]));
    const of = (stream, state) => items[stream].filter((i) => i.state === state).length;
    return {
      streams: SCENARIO.streams.map((s) => ({ ...s, work_tree: `/work/ah/.claude/worktrees/${s.stream}`, state: "open", source: "checklist", counts: compact(Object.fromEntries(ST.map((k) => [k, of(s.stream, k)]))), as_of: t(5), ...compact({ blockers: of(s.stream, "blocked") }) })),
      items,
    };
  }
  const ui = PLAN.map(([, item, title, state, holder], i) => ({ key: `ah/ui-r6:${item}`, title, state, ...(holder ? { holder } : null), ...checklistFields(item, t), done_seen_at: state === "done" ? t(60 + i) : null }));
  const docs = [
    ["Draft the 2.5 changelog", "done"],
    ["Upgrade guide", "in_progress"],
    ["Screenshots", "in_progress"],
    ["Proofread", "not_started"],
  ].map(([title, state], i) => ({ key: `docs-2.5:d${i}`, title, state, done_seen_at: state === "done" ? t(20) : null }));
  const server = ["R2 exchange lanes", "R3 tree composers", "R4 timeline store"].map((title, i) => ({ key: `server-r5:s${i}`, title, state: "done", done_seen_at: null }));
  return {
    streams: [
      { stream: "ah/ui-r6", owner: "implementor-1", branch: "ah/ui-r6", work_tree: "/work/ah/.claude/worktrees/ui-r6", goal: "Web UI redesign", state: "open", source: "jsonl", counts: compact(Object.fromEntries(ST.map((k) => [k, count(k)]))), as_of: t(5), ...compact({ blockers: 1 }) },
      { stream: "docs-2.5", owner: "docs-writer", branch: "ah/docs", work_tree: "docs", goal: "2.5 release notes and guide", state: "open", source: "checklist", counts: { done: 1, in_progress: 2, not_started: 1 }, as_of: t(12), review_open: true, warnings: 1 },
      { stream: "server-r5", owner: "implementor-2", branch: "ah/r5", work_tree: ".", state: "done", source: "checklist", counts: { done: 3 }, as_of: t(3000) },
    ],
    items: { "ah/ui-r6": ui, "docs-2.5": docs, "server-r5": server },
  };
}

// What team-summary says about the ship plan's streams.
export const SHIP_TALLY = (() => {
  const { streams, items } = shipStreams(0);
  const all = Object.values(items).flat();
  return { streams_open: streams.filter((x) => x.state === "open").length, streams_total: streams.length, items_done: all.filter((i) => i.state === "done").length, items_total: all.length };
})();

function otherPlan(now) {
  const t = (m) => iso(now - m * MIN);
  const item = (n, title, state, rank, extra = {}) => ({ item: n, stream: "docs", seq: rank, area: "docs", title, state, holder: null, rank, added_at: t(3000), started_at: null, done_at: null, note: null, commits: [], branch: null, issue: null, ...extra });
  return {
    items: [
      item("notes", "Write the 2.5 release notes", "in_progress", 1, { holder: "docs-writer", started_at: t(200) }),
      item("tag", "Tag 2.5.0", "next", 2),
      item("old", "Old changelog format", "dropped", 3),
      item("shots", "Screenshots", "done", 4, { done_at: t(2500) }),
    ],
    changes: [
      { at: t(150), seq: 4, stream: "docs", item: "notes", kind: "state", from: "next", to: "in_progress", by: "docs-writer@release" },
      { at: t(2500), seq: 3, stream: "docs", item: "shots", kind: "state", from: "in_progress", to: "done", by: "docs-writer@release" },
    ],
  };
}

export const cursorOf = (index) => `c${String(index).padStart(6, "0")}`;
export const indexOf = (cursor) => (/^c(\d{6})$/.test(cursor ?? "") ? Number(cursor.slice(1)) : null);

function planItems(now) {
  const t = (m) => iso(now - m * MIN);
  return PLAN.map(([area, item, title, state, holder, rank], i) => ({
    item,
    stream: SCENARIO ? area : "ah/ui-r6",
    seq: i + 1,
    area,
    title,
    state,
    holder,
    rank,
    added_at: t(600 - i * 20),
    started_at: state === "in_progress" || state === "done" ? t(300 - i * 10) : null,
    done_at: state === "done" ? t(60 + i) : null,
    note: state === "blocked" ? "waiting on the R3 merge" : state === "waiting_user" ? "needs the font decision" : state === "waiting_orch" ? "needs the merge order" : null,
    commits: state === "done" ? [`c0ffee${i}`] : [],
    branch: state === "in_progress" ? "ui-r6" : null,
    issue: null,
  }));
}

function planChanges(now) {
  const t = (m) => iso(now - m * MIN);
  if (SCENARIO) {
    // A scenario's changes: each item that moved past next, newest first.
    const moved = PLAN.filter((p) => !["next", "not_started"].includes(p[3]));
    return moved.map(([stream, item, , state], i) => ({ at: t(5 + i * 12), seq: moved.length - i, stream, item, kind: "state", from: state === "done" ? "in_progress" : "next", to: state, by: `orchestrator@${TEAM}` }));
  }
  return [
    { at: t(5), seq: 9, stream: "ah/ui-r6", item: "r6a", kind: "state", from: "next", to: "in_progress", by: "orchestrator@ship" },
    { at: t(12), seq: 8, stream: "ah/ui-r6", item: "r5b", kind: "state", from: "in_progress", to: "blocked", by: "orchestrator@ship" },
    { at: t(30), seq: 7, stream: "ah/ui-r6", item: "r6c", kind: "priority", from: "5", to: "3", by: "orchestrator@ship" },
    { at: t(55), seq: 6, stream: "ah/ui-r6", item: "tag", kind: "added", from: null, to: "Tag 2.5.0", by: "orchestrator@ship" },
    { at: t(70), seq: 5, stream: "ah/ui-r6", item: "r6d", kind: "priority", from: "2", to: "4", by: "orchestrator@ship" },
    { at: t(80), seq: 4, stream: "ah/ui-r6", item: "r6e", kind: "priority", from: "7", to: "5", by: "orchestrator@ship" },
    { at: t(300), seq: 3, stream: "ah/ui-r6", item: "screens", kind: "dropped", from: "not_started", to: "dropped", by: "orchestrator@ship" },
  ];
}

// The activity-state rows /list serves for the timeline: newest first. Field names follow the spec's columns.
export function timelineRows(now) {
  const rows = [];
  let seq = 1;
  for (const [name, , state] of MEMBERS) {
    const base = now - (90 + rows.length * 3) * MIN;
    rows.push({ seq: seq++, subject: session(name), from_state: "idle", to_state: "working", at: base });
    rows.push({ seq: seq++, subject: session(name), from_state: "working", to_state: state === "working" ? "idle" : state, at: base + 25 * MIN });
    if (state === "working") {
      rows.push({ seq: seq++, subject: session(name), from_state: "idle", to_state: "working", at: base + 50 * MIN });
    }
  }
  return rows.sort((a, b) => b.seq - a.seq);
}

// What the activity changes to when a member's turn ends or starts, for the live ticker.
export const ACTIVITY_NAMES = MEMBERS.map(([n]) => n);
export const sessionOf = session;
export const activityIdOf = activityId;
export const exchangeIds = EXCHANGES.map((e) => e[0]);
// A scenario's live brief traffic ([from, to, slug] between ship-team parties), or null for the model's own.
export const SCENARIO_TRAFFIC = SCENARIO?.traffic ?? null;
// A scenario's stuck open tasks, [exchange slice id, minutes since its last activity]: the daemon's StuckClock decides these at
// serve time; the mock takes them as given. ponytail: declared, not derived from the 10-minute rule.
export const SCENARIO_STUCK = (SCENARIO?.stuck ?? []).map(([id, min]) => [`exchange:${DIR}/${id}`, min]);
