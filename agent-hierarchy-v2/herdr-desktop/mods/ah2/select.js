import { questionsId } from "./questions-model.js";
// Reads over the slice Map. Pure: the same slices give the same answer, so a memo'd component fed these by reference can skip drawing.

// The member states, decided here. "review" is a role, never a state.
//   blocked: the hooks wrote blocked (a permission wait) or failed; gone: the member's pid is not live; idle: idle;
//   unknown: there is no activity record to read; else working.
export const BLOCKED = new Set(["blocked", "failed"]);

export const STATES = ["working", "idle", "blocked", "gone", "unknown"];
// A member state as the user reads it (§6 "Gone"); "unknown" reads "No signal", as in the status bar.
export const STATE_WORD = { working: "Working", idle: "Idle", blocked: "Blocked", gone: "Gone", unknown: "No signal" };
export const stateWord = (st, running = false) => (running ? "Running" : STATE_WORD[st] ?? st);
// An Orchestrator records no activity through its turns, so idle or unknown says nothing of it: while its process is live it reads Working
// when the summary says its turn is open (turn_open, ah2-62), else Running.
const orchestratorLive = (id, served) => id.endsWith("/@orchestrator") && served?.liveness === "live" && (served.state === "idle" || served.state === "unknown");
export const orchestratorWorks = (id, served) => orchestratorLive(id, served) && served.turn_open === true;
export const orchestratorRuns = (id, served) => orchestratorLive(id, served) && served.turn_open !== true;

export function stateOf(activity, pidLive) {
  if (pidLive === false) {
    return "gone";
  }
  if (activity === undefined || activity === null) {
    return "unknown";
  }
  if (activity === "idle") {
    return "idle";
  }
  return BLOCKED.has(activity) ? "blocked" : "working";
}


// Whether a team reads quiet now: one comparison against the instant the daemon serves. No instant is never quiet.
export const isQuiet = (quietAt, now) => quietAt > 0 && now >= quietAt;

// The team the page shows: the one `ui.team` names while it is held, else the first team not quiet, else the first team slice.
export function firstTeam(slices, ui, now = Date.now()) {
  const chosen = ui?.team ? slices.get(ui.team) : undefined;
  if (chosen) {
    return chosen;
  }
  let first = null;
  for (const s of slices.values()) {
    if (s.id.startsWith("team:")) {
      if (!isQuiet(Date.parse(slices.get(summaryIdOf(s.id))?.body?.quiet_at ?? ""), now)) {
        return s;
      }
      first ??= s;
    }
  }
  return first;
}

export const teamsOf = (slices) => [...slices.values()].filter((s) => s.id.startsWith("team:"));

// Text the user typed as one inert line, as SafeText.Line does: escape sequences and control and bidi characters removed, a tab or newline a space.
export const inert = (s) => String(s ?? "")
  .replace(/\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\)?|[PX^_][^\x1b]*(?:\x1b\\)?|[@-Z\\-_])?/g, "")
  .replace(/[\x00-\x08\x0b-\x1f\x7f-\x9f‪-‮⁦-⁩]/g, "")
  .replace(/[\t\n]/g, " ");

// A summary's team goal (ah2-53): its own, else a confirmed lane parent's (`team` names whose), as inert lines; null when it has none.
// `full` is the goal and its done-when as one line, for a tooltip. The web UI and the herdr mod both read it from here.
export function goalOf(b) {
  const text = b?.goal ? inert(b.goal.text) : "";
  if (!text) return null;
  const doneWhen = b.goal.done_when ? inert(b.goal.done_when) || null : null;
  return { text, doneWhen, team: typeof b.goal.team === "string" && b.goal.team ? inert(b.goal.team) : b.name, full: doneWhen ? `${text} — done when: ${doneWhen}` : text };
}
// The goal a row of its own shows: a lane's inherited goal is already on its parent's row.
export const ownGoal = (g, name) => (g && g.team === name ? g : null);

// A team's display name (ah2-218): the served display_name as one inert line, trimmed; null when none or blank. Display only: nothing
// finds a team, member, pane or file by it, and no id, address or `--team` value is built from it.
export const displayNameOf = (b) => inert(b?.display_name).trim() || null;
// The name a team is shown by, in both UIs: its own display name, else its real name. A lane's is the lane's own.
export const teamLabel = (b) => displayNameOf(b) ?? b?.name ?? "";
// The real name as the secondary line or tooltip beside a display name; null when the two are the same.
export const realNameNote = (b) => (displayNameOf(b) && displayNameOf(b) !== b?.name ? b.name : null);

// The display name rule (A1), as the rename dialog shows it while it is broken: null when `text` may be saved.
export const DISPLAY_NAME_MAX = 60;
export function displayNameProblem(text) {
  const v = String(text ?? "").trim();
  if (v === "") return "Empty. Type a name, or use Clear.";
  if (/[\p{Cc}\u2028\u2029]/u.test(v)) return "No line breaks or control characters.";
  const n = [...v].length;
  return n > DISPLAY_NAME_MAX ? `${n} characters. At most ${DISPLAY_NAME_MAX}.` : null;
}

// A team's summary slice id from its team id: team:<dirId>/<name> -> team-summary:<dirId>/<name>.
export const summaryIdOf = (teamId) => `team-summary:${teamId.slice("team:".length)}`;

// A team-summary slice as the row All teams, the header and the footer draw. Every value is a field of the slice: nothing comes from
// member rows, which are not held while only summaries are leased. Its alerts become the Needs-you cards, except those whose
// `audience` is "orchestrator" (a stuck task): those are `orch`, drawn apart and never counted as the user's.
export function summaryRow(slice) {
  const b = slice.body;
  const teamId = b.team;
  const cards = (b.alerts ?? []).map((a) => {
    const card = { teamId, team: b.name, teamLabel: teamLabel(b), title: a.title, context: a.detail, at: Date.parse(a.since ?? "") || 0 };
    if (a.kind === "needs_you") {
      return { ...card, key: `needs_you|${teamId}|${a.text}`, kind: "Decision", memberId: null };
    }
    if (a.kind === "stuck") {
      // a stuck open task: its exchange id is the key the Board's item link takes
      return { ...card, key: `stuck|${teamId}|${a.exchange}`, kind: "Stuck", memberId: a.member ?? null, itemKey: a.exchange ?? null };
    }
    if (a.kind === "alarm") {
      // a user alarm the stall watch raised for the team (ah2-180): its own card, the error card's look, no member
      return { ...card, key: `alarm|${teamId}|${a.since}`, kind: "Alarm", memberId: null };
    }
    return a.kind === "error"
      ? { ...card, key: `error|${teamId}|${a.member}|${a.since}`, kind: "Error", memberId: a.member }
      : { ...card, key: `blocked|${teamId}|${a.member}|${a.since}`, kind: "Blocked", memberId: a.member };
  });
  const orchestrators = (b.alerts ?? []).map((a) => a.audience === "orchestrator");
  const items = cards.filter((_, i) => !orchestrators[i]);
  const orch = cards.filter((_, i) => orchestrators[i]);
  return {
    id: teamId,
    name: b.name,
    // what the row is shown by; `name` stays the key (ah2-218)
    label: teamLabel(b),
    displayName: displayNameOf(b),
    realNote: realNameNote(b),
    dirId: teamId.slice("team:".length).split("/")[0],
    branch: b.branch ?? null,
    repo: b.work_root ? b.work_root.split("/").filter(Boolean).pop() : null,
    members: b.members ?? 0,
    working: b.working ?? 0,
    running: b.running === true,
    updated: Date.parse(b.updated_at ?? "") || 0,
    feedNewest: Date.parse(b.feed_newest_at ?? "") || 0,
    inFlight: b.in_flight ?? 0,
    needs: b.needs_you ?? 0,
    waitingOnYou: b.waiting_on_you ?? 0,
    blockers: b.waiting_blockers ?? 0,
    waitingRows: b.waiting_rows ?? [],
    queueWaiting: b.queue_waiting ?? 0,
    streamsOpen: b.streams_open ?? 0,
    itemsDone: b.items_done ?? 0,
    itemsTotal: b.items_total ?? 0,
    planLoaded: b.plan_loaded !== false,
    noRecord: b.no_record === true,
    // the parent team's name when this team is a confirmed lane (U0 b); a lane_claim is no lane
    laneOf: b.lane?.parent_team ?? null,
    laneStream: b.lane?.stream ?? null,
    laneClaim: b.lane?.parent_team ? null : b.lane_claim?.parent_team ?? null,
    blocked: b.blocked ?? 0,
    ended: b.ended === true,
    status: b.status,
    closedAt: Date.parse(b.closed_at ?? "") || 0,
    // member name -> when it last showed life, for a member the summary cannot call live
    memberLife: Object.fromEntries(Object.entries(b.member_states ?? {}).filter(([, m]) => m?.last_life_at).map(([id, m]) => [id.split("/").pop(), Date.parse(m.last_life_at) || 0])),
    quietAt: Date.parse(b.quiet_at ?? "") || 0,
    held: b.held === true,
    goal: goalOf(b),
    // every member the summary lists, gone ones included
    memberCount: Object.keys(b.member_states ?? {}).length || (b.members ?? 0),
    // [member id, state] for each member not gone, from the summary's member_states
    memberStates: Object.entries(b.member_states ?? {}).filter(([, m]) => m?.state && m.state !== "gone").map(([id, m]) => [id, orchestratorWorks(id, m) ? "working" : orchestratorRuns(id, m) ? "running" : m.state]),
    items,
    orch,
  };
}

// A team's confirmed lanes: the summaries in its dir whose lane names it as the parent (by name, or by team id).
export const isLaneOf = (row, parent) => row.laneOf !== null && row.dirId === parent.dirId && (row.laneOf === parent.name || row.laneOf === parent.id);

// A lane's parent as it is shown (ah2-218): the parent row's display name, else the served parent name; null for a top-level team.
export const parentLabel = (rows, lane) => (lane.laneOf === null ? null : rows.find((p) => p !== lane && isLaneOf(lane, p))?.label ?? lane.laneOf);

// The teams the Inbox scopes to, each with its alerts: one team and its lanes, or every team (teamId null), lanes after their parent.
export function inboxTeams(slices, teamId) {
  const rows = teamRows(slices);
  const tops = teamId ? rows.filter((r) => r.id === teamId) : rows.filter((r) => !rows.some((p) => p !== r && isLaneOf(r, p)));
  return tops.flatMap((t) => [t, ...rows.filter((r) => r !== t && isLaneOf(r, t))]);
}

// What one team's Attention draws, by the daemon's counts: what waits on the user (`waitingOnYou`, answered through the questions slice), and its
// blocked or failed rows (the summary's alerts; orchestrator-audience alerts are `orch`, not counted). `n`, every "Needs you", is the served
// waiting_on_you; `blocked` is the rest of the served needs_you (the served `blocked`), never the alert rows, so herdr shows the same number.
// Rulings atp-1736 (Orchestrator, request 20261113-152814-1cyj) and atp-1749 (Orchestrator, 2026-11-13).
export function teamNeeds(row) {
  const alerts = row.items.filter((c) => c.kind !== "Decision").length;
  const blocked = (row.needs ?? 0) - (row.waitingOnYou ?? 0);
  return { n: row.waitingOnYou, blocked, waitingOnYou: row.waitingOnYou, blockers: row.blockers ?? 0, alerts, pending: row.planLoaded === false };
}

// N as a count that may not be whole yet: a plan not read says so, and is never drawn as 0. A blocker among them adds "!".
export const needsText = (n, pending, blockers = 0) => (pending ? (n > 0 ? `${n}…` : "…") : String(n)) + (blockers > 0 ? "!" : "");
// " · B blocked" after a Needs you count, nothing at zero.
export const blockedText = (b) => (b > 0 ? ` · ${b} blocked` : "");

// The one "Needs you" count (B4, C7, F1): the served waiting_on_you over the Inbox's scope, and the blocked rows beside it. The
// header's Inbox button, the status bar, the Overview tile and the Attention tab all read this.
export function needsState(slices, teamId) {
  const all = inboxTeams(slices, teamId).map((r) => teamNeeds(r));
  const sum = (k) => all.reduce((n, t) => n + t[k], 0);
  return { n: sum("n"), blocked: sum("blocked"), pending: all.some((t) => t.pending), blockers: sum("blockers") };
}
export const needsCount = (slices, teamId) => needsState(slices, teamId).n;
// The questions slices the count reads: one per team in scope.
export const needsLeaseIds = (slices, teamId) => inboxTeams(slices, teamId).map((r) => questionsId(r.id));

// Every team's row, running teams first, then by name. A team whose summary is not held yet has no row.
export const teamRows = (slices) =>
  [...slices.values()].filter((s) => s.id.startsWith("team-summary:")).map(summaryRow).sort((a, b) => Number(b.running) - Number(a.running) || a.name.localeCompare(b.name));

// One team's row by team id; null while its summary is not held.
export const teamSummary = (slices, teamId) => {
  const slice = teamId ? slices.get(summaryIdOf(teamId)) : undefined;
  return slice ? summaryRow(slice) : null;
};

// The team the page shows, by id, known as soon as its summary is held: the one `ui.team` names, else the first summary's.
export function teamIdOf(slices, ui) {
  if (ui?.team && slices.has(summaryIdOf(ui.team))) {
    return ui.team;
  }
  return teamRows(slices)[0]?.id ?? null;
}

const teamKey = (team) => `${team.body.dir_id}/${team.body.name}`;

// The slice ids a view of one team shows, by what it shows. Absent slices are skipped, so the set grows as the team's own slices arrive.
//   members: the topology, the member rows, their activity and newest exchange (the sub-label) and their work trees.
//   open: the open list and the exchange of every brief in it.
export function teamLeaseIds(slices, team, { members = false, open = false, pairs = false, lanes = false } = {}) {
  if (!team) {
    return [];
  }
  const key = teamKey(team);
  const ids = [];
  if (pairs) {
    ids.push(`list:feed:team:${key}`);
  }
  if (members) {
    const topo = slices.get(`topology:${key}`);
    ids.push(`topology:${key}`);
    for (const id of memberIds(team)) {
      ids.push(id);
      const row = slices.get(id)?.body;
      if (row?.activity) {
        ids.push(row.activity);
      }
      if (row?.last_exchanges?.[0]) {
        ids.push(row.last_exchanges[0]);
      }
    }
    ids.push(...(topo?.body.nodes?.worktrees ?? []));
  }
  if (lanes) {
    for (const child of openLaneTeams(slices, team)) {
      ids.push(child);
      for (const id of slices.get(child)?.body.members ?? []) {
        const row = slices.get(id)?.body;
        ids.push(id, ...(row?.activity ? [row.activity] : []), ...(row?.last_exchanges?.[0] ? [row.last_exchanges[0]] : []));
      }
    }
  }
  if (open) {
    // ah2-367 L2: with its lanes, each open lane's open list too
    for (const k of [key, ...(lanes ? openLaneLeads(slices, team).map((l) => `${team.body.dir_id}/${l.name}`) : [])]) {
      ids.push(`list:open:team:${k}`, ...(slices.get(`list:open:team:${k}`)?.body.open ?? []).map((o) => o.id));
    }
  }
  return [...new Set(ids)];
}

// How a member's context reads when nothing is served for it.
export const NO_CONTEXT = { text: "—", title: "context not reported", over: false, at: 0 };

const HOUR_MS = 3_600_000;
const RECENT_CAP = 100;

// A pair's exchanges created in the last hour, from the creation times (epoch ms) its list carries, counted at `nowMs`. The list
// holds at most 100 times, so a full list means there may be more: it reads "100+". Call it when the reading is shown, not on a timer.
export function recentCount(times, nowMs) {
  const n = (times ?? []).filter((t) => t >= nowMs - HOUR_MS).length;
  return n >= RECENT_CAP ? "100+" : String(n);
}

// A member's last exchanges (the member slice's last_exchanges, newest first) as one string a component can compare:
// one line per exchange, "name|open|from|to", where the name is the title, or the slug when there is none.
export function recentOf(slices, ids) {
  return (ids ?? [])
    .map((id) => {
      const b = slices.get(id)?.body;
      return b ? `${String(b.title || b.slug).replace(/[|\n]/g, " ")}|${b.open ? 1 : 0}|${partyName(b, "from", slices)}|${partyName(b, "to", slices)}` : null;
    })
    .filter(Boolean)
    .join("\n");
}

// The team's duty block ({deputy, since, cause, member}) while a deputy is on duty; null otherwise.
const dutyOf = (team) => (typeof team?.body.duty?.member === "string" ? team.body.duty : null);

// What a part reads as: the badge (DUTY) and its full text (DUTY_TITLE).
export const DUTY = { on: "on duty", away: "with user" };
export const DUTY_TITLE = { on: "On duty while the chief is with the user", away: "Chief Orchestrator: with user" };

// A member's part in the duty: "on" for the deputy on duty, "away" for the chief while it is with the user, else null.
const dutyPart = (t, id) => (typeof t.duty?.member !== "string" ? null : id === t.duty.member ? "on" : id === t.orchestrator ? "away" : null);

// The ids of the team's members, orchestrator first; while a deputy is on duty it comes next, once.
export const memberIds = (team) => {
  const d = dutyOf(team);
  return team ? [team.body.orchestrator, ...(d ? [d.member] : []), ...team.body.members.filter((id) => id !== d?.member)].filter(Boolean) : [];
};

// The confirmed, open lanes of a manager team: its streams that are open and name a child team (served only for a confirmed lane) and a
// sub-orchestrator. These, and only these, have their child team's members drawn with the manager's.
const openLanes = (t) => (Array.isArray(t?.streams) ? t.streams : []).filter((s) => s?.state === "open" && typeof s.child_team === "string" && typeof s.sub_orchestrator === "string");
// Each such lane whose sub-orchestrator is a member of the manager: its child team's name and id, and the sub-orchestrator's member id.
const openLaneLeads = (slices, team) => {
  const open = openLanes(team?.body);
  if (open.length === 0) {
    return [];
  }
  const byName = new Map(memberIds(team).map((id) => [slices.get(id)?.body.name ?? id.split("/").pop(), id]));
  return open.filter((s) => byName.has(s.sub_orchestrator)).map((s) => ({ name: s.child_team, id: `team:${team.body.dir_id}/${s.child_team}`, lead: byName.get(s.sub_orchestrator) }));
};
const openLaneTeams = (slices, team) => openLaneLeads(slices, team).map((l) => l.id);

// The members of each confirmed, open lane's child team, as [{id, team, lane}]: lane is the id of the lane's sub-orchestrator among the
// manager's members. A lane whose sub-orchestrator is no member of the manager draws no child members; a member already listed is skipped.
export function laneChildren(slices, team, ids) {
  const t = team?.body;
  const byName = new Map(ids.map((id) => [slices.get(id)?.body.name ?? id.split("/").pop(), id]));
  const seen = new Set(ids);
  const out = [];
  for (const s of openLanes(t)) {
    const lane = byName.get(s.sub_orchestrator);
    const childId = `team:${t.dir_id}/${s.child_team}`;
    const members = slices.get(childId)?.body.members;
    for (const id of lane && Array.isArray(members) ? members : []) {
      if (typeof id === "string" && !seen.has(id)) {
        seen.add(id);
        out.push({ id, team: childId, lane });
      }
    }
  }
  return out;
}

// The team a member's own data lives under: its lane's child team when it is a lane child of the manager team, else the manager team.
export function teamOfMember(slices, team, id) {
  const child = team ? laneChildren(slices, team, memberIds(team)).find((c) => c.id === id) : null;
  return (child && slices.get(child.team)) || team;
}

// Each member's lane, as a function of its id: the id of its lane leader, its own id when it leads one, else "". A leader is a member named
// as a stream's sub_orchestrator in the team's streams[], or the member another member's served lead names. A lead counts only when its
// team is this team, or the child team of the stream that the lead's member runs as sub-orchestrator. The orchestrator is in no lane.
export function laneOf(slices, team, ids) {
  const t = team?.body;
  const nameOf = (id) => slices.get(id)?.body.name ?? id.split("/").pop();
  const byName = new Map(ids.map((id) => [nameOf(id), id]));
  const streams = Array.isArray(t?.streams) ? t.streams : [];
  const streamLeads = new Set(streams.filter((s) => s?.state === "open").map((s) => s?.sub_orchestrator));
  const ours = (lead) => lead.team === t?.name || streams.some((s) => s?.state === "open" && s?.sub_orchestrator === lead.name && typeof s.child_team === "string" && s.child_team === lead.team);
  const leadOf = (id) => {
    const lead = slices.get(id)?.body.lead;
    const to = typeof lead?.name === "string" && ours(lead) ? byName.get(lead.name) : undefined;
    return to && to !== id && to !== t?.orchestrator ? to : "";
  };
  const leaders = new Set(ids.filter((id) => streamLeads.has(nameOf(id))));
  ids.forEach((id) => leadOf(id) && leaders.add(leadOf(id)));
  return (id) => (id === t?.orchestrator ? "" : leaders.has(id) ? id : leadOf(id));
}

// One member as a flat object of primitives (so a component can compare it field by field): what the graph and panel draw.
// The sub-label (`slug`) is the newest of the member's last_exchanges.
// The busy share of an activity body as of now: worked out from its spans, so an open screen sees it decay with no new slice. No spans: what was served.
export function workingShareOf(body, now = Date.now()) {
  const spans = body?.working_spans;
  const seen = body?.working_seen_from;
  if (!Array.isArray(spans) || typeof seen !== "number") {
    return { share: typeof body?.working_1h === "number" ? body.working_1h : null, shareWindowMs: typeof body?.working_window_ms === "number" ? body.working_window_ms : null };
  }
  const start = now - HOUR_MS;
  const covered = now - Math.min(Math.max(seen, start), now);
  const worked = spans.reduce((n, [from, to]) => n + Math.max(0, Math.min(to ?? now, now) - Math.max(from, start)), 0);
  return { share: covered > 0 ? Math.round((worked / covered) * 100) / 100 : 0, shareWindowMs: Math.floor(covered / 60_000) * 60_000 };
}

// The label of that share: what it covers, which is less than an hour for a member seen for less.
export function busyLabel(windowMs) {
  return windowMs === null || windowMs === undefined || windowMs >= HOUR_MS ? "busy, last hour" : windowMs < 60_000 ? "busy" : `busy, last ${Math.round(windowMs / 60_000)} min`;
}

// ah2-220: how a member reads. A team's orchestrator (its key `…/@orchestrator`, or the bare name an owner or a party carries) is
// "Sub-Orchestrator" (role "sub-orchestrator") when the team is a lane, `lane` being the lane team's name (the team has a parent team);
// every other member, the top team's Orchestrator and a team the slice does not say is a lane included, keeps the name and role it has.
// `title` is the tooltip: the full label and the lane's name. Display only: keys, addresses, filters' values and lookups keep `orchestrator`.
export function memberLabel(id, name, role, lane) {
  if (typeof lane === "string" && lane !== "" && typeof id === "string" && /(^|\/)@?orchestrator$/.test(id)) {
    return { label: "Sub-Orchestrator", role: "sub-orchestrator", title: `Sub-Orchestrator of ${lane}` };
  }
  return { label: name, role, title: name };
}

// ah2-220 Amendment 1: a served asker address ("name@team", or a bare name in `team`) as the rule reads it: { label, title } when the
// address's team is a lane the slices know and the name is its orchestrator, else null (the caller shows the asker as today).
export function askerLabel(slices, dir, team, who) {
  if (typeof who !== "string" || who === "") return null;
  const at = who.indexOf("@");
  const [name, own] = at < 0 ? [who, team] : [who.slice(0, at), who.slice(at + 1)];
  const shown = nameIn(slices, dir, own, name);
  return shown.label === name ? null : { label: shown.label, title: shown.title };
}

// The lane a team is, for memberLabel: the team's name when its body or its summary names a parent team, else null.
const laneName = (slices, dir, team, lane) => (lane?.parent_team || slices?.get(`team-summary:${dir}/${team}`)?.body?.lane?.parent_team ? team : null);
export const laneNameOf = (slices, t) => laneName(slices, t?.dir_id, t?.name, t?.lane);

// A bare name in a team (a card's owner, an exchange party) as memberLabel reads it: { label, title }.
export const nameIn = (slices, dir, team, name) => memberLabel(name, name, "", typeof name === "string" ? laneName(slices, dir, team) : null);

export function memberOf(slices, team, id) {
  const t = team.body;
  const s = slices.get(id);
  if (!s) {
    const name = id.split("/").pop();
    return { id, name, ...memberLabel(id, name, "", laneNameOf(slices, t)), state: "gone", running: false, lastAt: 0, tool: null, toolAt: null, contextUsed: null, model: null, ctx: NO_CONTEXT, slug: null, recent: "", branch: null, workRoot: null, orchestrator: id === t.orchestrator, duty: dutyPart(t, id) };
  }
  const recent = recentOf(slices, s.body.last_exchanges);
  const m = s.body;
  const act = m.activity ? slices.get(m.activity) : null;
  const wt = slices.get(`worktree:${t.dir_id}/${t.name}/${m.name}`)?.body;
  const served = slices.get(`team-summary:${t.dir_id}/${t.name}`)?.body.member_states?.[id];
  const running = orchestratorRuns(id, served);
  return {
    id,
    name: m.name,
    ...memberLabel(id, m.name, m.role ?? "", laneNameOf(slices, t)),
    state: orchestratorWorks(id, served) ? "working" : served?.state ?? "unknown",
    running,
    // The served facts in one line: only what is known.
    facts: [served?.tool, served?.item, served?.branch, served?.work_root?.split("/").pop()].filter(Boolean).join(" · ") || null,
    tool: act?.body.tool ?? null,
    toolAt: act?.body.tool_at ?? null,
    contextUsed: act?.body.context?.used ?? null,
    model: m.model ?? act?.body.context?.model ?? null,
    ctx: typeof m.context_text === "string" ? { text: m.context_text, title: m.context_title ?? "", over: m.context_over === true, at: Date.parse(m.context_at ?? "") || 0 } : NO_CONTEXT,
    ...workingShareOf(act?.body),
    since: running ? 0 : Date.parse(served?.state_at ?? "") || (act ? Number(act.body.state_at) || act.as_of || 0 : 0),
    // The last thing the member did: the activity slice's time is the newest of its record's own stamps. `since` is when the state began.
    lastAt: running ? 0 : act?.as_of ?? 0,
    slug: recent === "" ? null : recent.split("|")[0],
    recent,
    branch: wt?.stream ?? m.placement?.stream ?? served?.branch ?? null,
    workRoot: wt?.work_root ?? served?.work_root ?? null,
    orchestrator: id === t.orchestrator,
    duty: dutyPart(t, id),
  };
}

// The messages each pair of members shares, from the team's feed list (its `pairs`: resolved member names, a count for the run, and the
// times of those created in the last hour). Keyed by the two member ids in order, "a|b"; a name that is no member is not drawn.
export function pairCountsOf(slices, team) {
  const t = team?.body;
  const pairs = t ? slices.get(`list:feed:team:${t.dir_id}/${t.name}`)?.body.pairs : null;
  const out = new Map();
  for (const p of Array.isArray(pairs) ? pairs : []) {
    const a = `member:${t.dir_id}/${t.name}/${p.a}`;
    const b = `member:${t.dir_id}/${t.name}/${p.b}`;
    if (memberIds(team).includes(a) && memberIds(team).includes(b) && Number.isFinite(p.n)) {
      out.set(a < b ? `${a}|${b}` : `${b}|${a}`, { n: p.n, recent: (p.recent_1h ?? []).filter(Number.isFinite) });
    }
  }
  return out;
}

export const membersOf = (slices, team) => memberIds(team).map((id) => memberOf(slices, team, id));

// The rows of the team's open list that are neither served as stale (stale_ids) nor unattended: the sparks and the open briefs.
export function briefsInFlight(slices, team) {
  return inFlight(slices, `${team.body.dir_id}/${team.body.name}`).filter((o) => o.to_member);
}

// The rows of a team's open list not served as stale (stale_ids), unattended ones included.
const inFlight = (slices, key) => {
  const stale = new Set(slices.get(`team-summary:${key}`)?.body.stale_ids ?? []);
  return (slices.get(`list:open:team:${key}`)?.body.open ?? []).filter((o) => !stale.has(o.id));
};

// One spark per open exchange in the team's open list and in each of its open lanes' (ah2-367 L2, L5), on the ends the server resolved.
// An end it could not resolve (null) lands on its team's lead (L6): the team's orchestrator node, for a lane its sub-orchestrator's node,
// as does a lane's own @orchestrator end. The client never matches roles or names itself.
export function sparksOf(slices, team) {
  if (!team) {
    return [];
  }
  const reviews = new Set((slices.get(`topology:${team.body.dir_id}/${team.body.name}`)?.body.edges?.brief ?? []).filter((b) => b.brief === "review").map((b) => b.id));
  const lanes = openLaneLeads(slices, team).map((l) => ({ key: `${team.body.dir_id}/${l.name}`, own: `member:${team.body.dir_id}/${l.name}/@orchestrator`, lead: l.lead }));
  const end = (id, own, lead) => (id === null || id === undefined || id === own ? lead : id);
  return [{ key: `${team.body.dir_id}/${team.body.name}`, own: null, lead: team.body.orchestrator ?? null }, ...lanes]
    .flatMap(({ key, own, lead }) => inFlight(slices, key).map((o) => ({ ...o, from_member: end(o.from_member, own, lead), to_member: end(o.to_member, own, lead) })))
    // an exchange both of whose ends land on one node has no line to draw (the Map drew none either)
    .filter((o) => o.from_member && o.to_member && o.from_member !== o.to_member)
    .map((o) => ({ id: o.id, from: o.from_member, to: o.to_member, slug: slices.get(o.id)?.body.slug ?? o.id, ...(reviews.has(o.id) ? { review: true } : {}) }))
    .sort((a, b) => (a.id < b.id ? -1 : 1));
}

// ah2-265 D1/D2: one dot per report waiting to be read (the topology's edges.report: the brief's sender and receiver), run the other way,
// from the member who reported (the brief's receiver) back to the one who briefed it. A party the server could not resolve lands on the
// team's orchestrator node (ah2-367 L6).
export function reportsBack(slices, team) {
  if (!team) {
    return [];
  }
  const lead = team.body.orchestrator ?? null;
  return (slices.get(`topology:${team.body.dir_id}/${team.body.name}`)?.body.edges?.report ?? [])
    .map((o) => ({ ...o, from_member: o.from_member ?? lead, to_member: o.to_member ?? lead }))
    .filter((o) => o.from_member && o.to_member && o.from_member !== o.to_member)
    .map((o) => ({ id: o.id, from: o.to_member, to: o.from_member, slug: slices.get(o.id)?.body.slug ?? o.id, back: true }))
    .sort((a, b) => (a.id < b.id ? -1 : 1));
}

// The open briefs addressed to a member, as exchange slices: the entries of the team's open list the server resolved to it.
export function openBriefsOf(slices, team, memberId) {
  return briefsInFlight(slices, team).filter((o) => o.to_member === memberId).map((o) => slices.get(o.id)).filter(Boolean);
}

// An exchange party for display: the server's name when it has one, else the role; with `slices`, as nameIn reads it in the exchange's
// team (ah2-220: a lane's orchestrator is its Sub-Orchestrator).
export const partyName = (body, side, slices = null) => {
  const name = body[`${side}_name`] ?? body[side];
  return slices ? nameIn(slices, body.dir_id, body.team ?? "default", name).label : name;
};

// The member id an exchange party resolves to, as the server's pair filter resolves it: the party's name in the exchange's
// directory and team (a team-less exchange belongs to the team named default), the orchestrator by its role; else null. With `slices`,
// a sub-orchestrator in a team they say is a lane is that lane's orchestrator (ah2-367 L1), as the server resolves it.
export const partyIdOf = (body, side, slices = null) => {
  const team = body.team ?? "default";
  const lead = body[side] === "sub-orchestrator" && laneName(slices, body.dir_id, team) !== null;
  // ah2-367 L4: with `slices`, an orchestrator end names its member only when the team slice lists that member (the deputy on duty)
  const named = body[`${side}_name`];
  const stray = slices && body[side] === "orchestrator" && typeof named === "string" && (named === "orchestrator" || !(slices.get(`team:${body.dir_id}/${team}`)?.body.members ?? []).includes(`member:${body.dir_id}/${team}/${named}`));
  const name = lead || stray ? "@orchestrator" : named ?? (body[side] === "orchestrator" ? "@orchestrator" : null);
  return name === null ? null : `member:${body.dir_id}/${team}/${name}`;
};
