import { html } from "./h.js";
import { isInternal, showInternal } from "./msg-filter.js";
import { Live } from "./live-component.js";
import { firstTeam, memberLabel, memberOf, partyIdOf, partyName, teamOfMember, stateWord } from "./select.js";
import { hashOf, memberHref, teamOfMember as teamIdOfMember } from "./deeplink.js";
import { Age } from "./ticker.js";
import { ageOf, ageText } from "./store.js";
import { currentItem, glyphOf, hhmm, itemsId, planId, planStreams, streamOf } from "./graph-world.js";

// One member summary (ah-ui-ia §4, P6): the header, Now, the newest 3 messages and "Open page". Every rail that shows a member
// draws this; the member page (member-page.js) is the same header and Now, expanded with the rest of §4.

export const SUMMARY_MESSAGES = 3;

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
    const item = b.title || b.item || b.slug || ""; // the title as people read it, never the raw _run/<slug>
    const pair = [partyIdOf(b, "from"), partyIdOf(b, "to")];
    b.created && rows.push({ at: Date.parse(b.created), kind: "brief", from, to, item, pair });
    b.response_created && rows.push({ at: Date.parse(b.response_created), kind: "report", from: to, to: from, item, pair });
  }
  return rows.sort((a, b) => b.at - a.at).slice(0, n).map((r) => ({ ...r, time: Number.isFinite(r.at) ? hhmm(r.at) : "—" }));
}

// The message rows a card ends in: time, kind, "from → to" and the item. With a store, a row with both parties opens their pair sheet.
export const msgs = (rows, store) => (rows.length === 0
  ? html`<p class="dim">No messages yet.</p>`
  : html`<ul class="gm-msgs">${rows.map((r, i) => {
    const body = html`<time class="mono">${r.time}</time><span class=${`kind ${r.kind}`}>${r.kind}</span><span class="route-line">${r.from} → ${r.to}${r.item ? html`<span class="route">${r.item}</span>` : null}</span>`;
    return html`<li key=${i}>${r.pair && store
      ? html`<button type="button" class="gm-row" title=${`Messages between ${r.from} and ${r.to}`} aria-label=${`${r.time} ${r.kind}, ${r.from} to ${r.to}${r.item ? `, ${r.item}` : ""}. Open the messages between ${r.from} and ${r.to}`} onClick=${() => store.setUi({ pair: r.pair })}>${body}</button>`
      : html`<div class="gm-row">${body}</div>`}</li>`;
  })}</ul>`);

// A member name as a link to its page; a name with no member id stays text.
export const memberLink = (id, name) => (id ? html`<a class="mlink" href=${memberHref(id)}>${name}</a>` : name);

// An exchange party as a link to its member's page, when it resolves to a member; else its name.
// `lane`: the exchange's team name when that team is a lane (ah2-220: its orchestrator reads Sub-Orchestrator), else null.
export const party = (b, side, lane = null) => memberLink(partyIdOf(b, side), memberLabel(partyName(b, side), partyName(b, side), "", lane).label);

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
    team: t.name, teamId, dir: t.dir_id, stream,
    teamHref: hashOf({ view: "overview", team: teamId }),
    lane: lane && typeof lane.stream === "string" && typeof lane.parent_team === "string"
      ? { name: lane.stream, href: hashOf({ view: "board", team: `team:${t.dir_id}/${lane.parent_team}`, lane: lane.stream }) }
      : null,
    lead: lead ? { name: lead.name, href: memberHref(`member:${t.dir_id}/${lead.team}/${lead.name}`) } : null,
    item: item ? { key: item.key, title: item.title, href: hashOf({ view: "board", team: teamId, item: item.full }) } : null,
    rows: exchangeRows(slices, (body?.last_exchanges ?? []).slice(0, n), n, ui),
  };
}

// A member whose team is not held: what its id says, state gone, so a rail or page shows it rather than loading forever.
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
  // Only a stream the plan names has an items slice; one it does not know would be asked for and refused.
  return [...(slices.get(id)?.body.last_exchanges ?? []).slice(0, n), planId(t), ...(stream && planStreams(slices, t).includes(stream) ? [itemsId(t, stream)] : [])];
}

const sinceText = (ms) => (now) => ageText(ageOf({ as_of: ms }, {}, now)).replace(" ago", "");
const elapsed = (at) => (now) => `${Math.max(0, Math.floor((now - Date.parse(at)) / 1000))}s`;

// Name, role and model, state and since, team, lane and lead. `Tag` is the name's heading level; `link` makes the name the page link.
export const MemberHead = ({ m, Tag = "h2", link = false }) => html`
  <${Tag} class="mh-name">${link ? html`<a href=${memberHref(m.id)} title=${m.title !== m.label ? m.title : undefined}>${m.label}</a>` : html`<span title=${m.title !== m.label ? m.title : undefined}>${m.label}</span>`}<//>
  <p class="sub">${m.role || "member"} · ${m.model ?? html`<span class="dim">model not reported</span>`}</p>
  <p class="mh-state"><span class=${`pill st-${m.state}`}><span aria-hidden="true">${glyphOf(m.running ? "working" : m.state)} </span>${stateWord(m.state, m.running)}</span>${m.since > 0 ? html`<span class="dim"> since <${Age} text=${sinceText(m.since)} /></span>` : null}</p>
  <p class="mh-where">team <a href=${m.teamHref}>${m.team}</a>${m.lane ? html` · lane <a href=${m.lane.href}>${m.lane.name}</a>` : null}${m.lead ? html` · takes direction from <a href=${m.lead.href}>${m.lead.name}</a>` : null}</p>`;

// What the member is doing: the tool and how long, the current item (a Board link), the branch and the work tree.
export const MemberNow = ({ m }) => html`<dl class="mh-now">
  <dt hidden=${!m.tool}>tool</dt><dd hidden=${!m.tool}>${m.tool}${m.toolAt ? html` · <${Age} text=${elapsed(m.toolAt)} />` : null}</dd>
  <dt hidden=${!m.item}>item</dt><dd hidden=${!m.item}>${m.item ? html`<a href=${m.item.href}>${m.item.key}${m.item.title ? ` · ${m.item.title}` : ""}</a>` : null}</dd>
  <dt hidden=${!m.branch}>branch</dt><dd hidden=${!m.branch}>${m.branch}</dd>
  <dt hidden=${!m.workRoot}>worktree</dt><dd class="mono" hidden=${!m.workRoot}>${m.workRoot}</dd>
</dl>`;

// The rail's member summary. `live` makes it a polite live region (the Graph rail, where a new selection is read out).
export class MemberSummary extends Live {
  leaseIds(snap, { id, messages = SUMMARY_MESSAGES }) {
    return memberLeaseIds(snap.slices, snap.ui, id, messages);
  }

  select(snap, { id, messages = SUMMARY_MESSAGES }) {
    return { model: JSON.stringify(memberModel(snap.slices, snap.ui, id, messages)) };
  }

  // `extra` follows Now and `children` precede Open page: the Graph rail adds its window counts there, and its actions (graph-panel.js).
  draw({ store, onClose, live = false, extra = null, children = null }, s) {
    const m = JSON.parse(s.model);
    if (!m) {
      return html`<section class="panel msum"><p class="dim">Loading…</p></section>`;
    }
    return html`<section class="panel msum gm-panel" data-role=${m.role || undefined} data-state=${m.state} aria-live=${live ? "polite" : undefined} aria-label=${`Member ${m.title}`}>
      ${onClose ? html`<button class="close" onClick=${onClose} aria-label="Close panel">×</button>` : null}
      <${MemberHead} m=${m} link=${true} />
      <h3>Now</h3>
      <${MemberNow} m=${m} />
      ${extra}
      <h3>Latest messages</h3>
      ${msgs(m.rows, store)}
      ${children}
      <a class="tab msum-open" href=${memberHref(m.id)}>Open page</a>
    </section>`;
  }
}
