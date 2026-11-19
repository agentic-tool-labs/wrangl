// A member's details for the Graph's sheet (spec herdr-graph-agent-details): web/ui/member-summary.js's pure half (memberModel,
// goneModel, memberLeaseIds, exchangeRows, teamFor) and the member branch of web/ui/graph-panel.js modelOf (the Map rail's extras).
// Those web files import Preact, so they cannot be copied whole: this is their code, line for line, without the drawing, and a test
// (tests/web/herdr-graph-model.test.mjs) holds every function here to the web's own output on the fixture. Internal messages are
// hidden, the web's default (msg-filter.js isInternal / showInternal; the mod has no toggle). No herdr or gpui imports.
import { busyLabel, firstTeam, memberOf, partyIdOf, partyName, teamOfMember, teamLabel } from "./select.js";
import { hashOf, memberHref, teamOfMember as teamIdOfMember } from "./deeplink.js";
import { currentItem, flowId, hhmm, itemsId, modeOf, pagesOf, planId, planStreams, radialView, scrubbed, scrubOf, sentReceived, streamOf } from "./graph-world.js";

export const SUMMARY_MESSAGES = 3;
const isInternal = (body) => body?.class === "internal";
const showInternal = (ui) => ui?.showInternal ?? false;

// A member's newest messages from its held exchanges: the brief at created, the report at response_created, newest first.
export function exchangeRows(slices, ids, n = 12, ui = null) {
  const rows = [];
  const internal = showInternal(ui);
  for (const id of ids) {
    const b = slices.get(id)?.body;
    if (!b || b.withdrawn || (!internal && isInternal(b))) {
      continue;
    }
    const from = partyName(b, "from", slices) ?? "";
    const to = partyName(b, "to", slices) ?? "";
    const item = b.title || b.item || b.slug || "";
    const pair = [partyIdOf(b, "from"), partyIdOf(b, "to")];
    b.created && rows.push({ at: Date.parse(b.created), kind: "brief", from, to, item, pair });
    b.response_created && rows.push({ at: Date.parse(b.response_created), kind: "report", from: to, to: from, item, pair });
  }
  return rows.sort((a, b) => b.at - a.at).slice(0, n).map((r) => ({ ...r, time: Number.isFinite(r.at) ? hhmm(r.at) : "—" }));
}

// The team a member's own data lives under: the team its id names when that is held, else the page's team (or its lane child).
export const teamFor = (slices, ui, id) => slices.get(teamIdOfMember(id) ?? "") ?? teamOfMember(slices, firstTeam(slices, ui), id);

// Everything the header and Now draw, as plain values. `n` is how many newest messages to keep.
export function memberModel(slices, ui, id, n = SUMMARY_MESSAGES) {
  const team = teamFor(slices, ui, id);
  if (!team) {
    return goneModel(id);
  }
  const t = team.body;
  const teamId = `team:${t.dir_id}/${t.name}`;
  const m = memberOf(slices, team, id);
  const body = slices.get(id)?.body;
  const stream = streamOf(slices, t, body?.placement?.stream ?? null);
  const item = currentItem(slices, t, stream, m);
  const lane = slices.get(`team-summary:${t.dir_id}/${t.name}`)?.body.lane ?? null;
  const lead = typeof body?.lead?.name === "string" && typeof body.lead.team === "string" ? body.lead : null;
  const ctx = m.ctx;
  return {
    id, name: m.name, label: m.label, title: m.title, role: m.role, model: m.model ?? null, state: m.state, running: m.running, since: m.since ?? 0, gone: m.state === "gone",
    tool: m.tool, toolAt: m.toolAt, branch: m.branch, workRoot: m.workRoot, slug: m.slug,
    ctx: ctx.text, ctxTitle: ctx.title, ctxOver: ctx.over, contextUsed: m.contextUsed ?? null,
    team: t.name, teamShown: teamLabel(t), teamId, dir: t.dir_id, stream,
    teamHref: hashOf({ view: "overview", team: teamId }),
    lane: lane && typeof lane.stream === "string" && typeof lane.parent_team === "string"
      ? { name: lane.stream, href: hashOf({ view: "board", team: `team:${t.dir_id}/${lane.parent_team}`, lane: lane.stream }) }
      : null,
    lead: lead ? { name: lead.name, href: memberHref(`member:${t.dir_id}/${lead.team}/${lead.name}`) } : null,
    item: item ? { key: item.key, title: item.title, href: hashOf({ view: "board", team: teamId, item: item.full }) } : null,
    rows: exchangeRows(slices, (body?.last_exchanges ?? []).slice(0, n), n, ui),
  };
}

// A member whose team is not held: what its id says, state gone, so the sheet shows it rather than loading forever.
function goneModel(id) {
  const teamId = teamIdOfMember(id);
  const [dir, team] = teamId ? teamId.slice("team:".length).split("/") : [null, null];
  return {
    id, name: id.split("/").pop(), role: "", model: null, state: "gone", since: 0, gone: true, tool: null, toolAt: null, branch: null, workRoot: null, slug: null,
    ctx: "—", ctxTitle: "context not reported", ctxOver: false, contextUsed: null, team: team ?? "", teamId, dir, stream: null,
    teamHref: teamId ? hashOf({ view: "overview", team: teamId }) : "#/", lane: null, lead: null, item: null, rows: [],
  };
}

// The slices the header and Now read beyond the team's own: the newest exchanges, the plan and the member's stream's items.
export function memberLeaseIds(slices, ui, id, n = SUMMARY_MESSAGES) {
  const team = teamFor(slices, ui, id);
  if (!team) {
    return [];
  }
  const t = team.body;
  const stream = streamOf(slices, t, slices.get(id)?.body.placement?.stream ?? null);
  return [...(slices.get(id)?.body.last_exchanges ?? []).slice(0, n), planId(t), ...(stream && planStreams(slices, t).includes(stream) ? [itemsId(t, stream)] : [])];
}

// The Map rail's extras (graph-panel.js modelOf, its member branch): stream, task, context used, busy share, messages in the scrubber's
// window, Radial's sent and received. Tokens and files changed read slices the mod does not hold (the transcript header and files:),
// so they are null, which the sheet draws as the web's "—". ui: the web's graph ui ({ graphWindow, graphCursor, graphMode }).
export function graphExtras(slices, ui, teamId, sel) {
  const team = slices.get(teamId);
  if (!team) return null;
  const t = team.body;
  const head = slices.get(flowId(t))?.body ?? null;
  const f = head ? (() => {
    const s = scrubOf(ui, head, Date.now());
    const pages = pagesOf(slices, t, head);
    return { head, s, pages, view: modeOf(ui.graphMode) === "radial" ? radialView(head, pages, s) : scrubbed(head, pages, s) };
  })() : { head: null };
  const idx = new Map((f.head?.names ?? []).map((id, i) => [id, i]));
  const m = memberOf(slices, team, sel);
  const i = idx.get(sel);
  let count = null;
  if (f.head) {
    count = i === undefined ? 0 : f.view.events
      ? f.view.events.filter((e) => e[1] === i || e[2] === i).length
      : (f.head.totals ?? []).filter(([a, b]) => a === i || b === i).reduce((n, r) => n + r[2] + r[3], 0);
  }
  const dir = f.head && modeOf(ui.graphMode) === "radial" ? sentReceived(f.head, f.view, i) : null;
  return {
    kind: "member", id: sel, state: m.state, stream: streamOf(slices, t, slices.get(sel)?.body.placement?.stream ?? null),
    ctx: m.state === "gone" ? "×" : m.ctx.text,
    ctxAt: m.state === "gone" ? 0 : m.ctx.at ?? 0,
    busy: typeof m.share === "number" && m.shareWindowMs !== 0 && (m.shareWindowMs ?? 60_000) >= 60_000 ? `${Math.round(m.share * 100)}%` : "—",
    busyLabel: busyLabel(m.shareWindowMs),
    count, dir, scope: f.view?.partial ? `in the newest ${f.view.partial}` : "in the window",
    tokens: null, files: null,
    task: m.slug ?? (m.running ? "—" : "idle"), plan: m.orchestrator ? `${t.dir_id}/${t.name}` : null,
  };
}
