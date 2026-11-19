import { html } from "./h.js";
import { Live } from "./live-component.js";
import { ageOf, ageText } from "./store.js";
import { Age } from "./ticker.js";
import { busyLabel, laneChildren, memberIds, memberOf, stateWord, teamLabel } from "./select.js";
import { idsOf } from "./transcript-rows.js";
import { streamName } from "./plans-model.js";
import { MemberActions } from "./member-actions.js";
import { eventsOf, flowId, glyphOf, shortName, hhmm, inWindow, kindOf, modeOf, pagesOf, radialView, scrubOf, scrubbed, sentReceived, streamOf } from "./graph-world.js";
import { MemberSummary, exchangeRows, msgs } from "./member-summary.js";

export { exchangeRows };

// The Graph rail while Map or Radial shows: the selected member's summary (member-summary.js) with what only the graph knows (its
// messages in the scrubber's window, Radial's sent and received, busy last hour) and the member's actions; or the card of the container
// last opened (graphCard). With neither, the rail shows the live feed (app.js).
// Both end in the newest 12 messages; a row opens the messages between its two members (the pair sheet). The rail is a polite live
// region, so a new selection is read out.

const LATEST = 12;
const filesIdOf = (t, name) => `files:${t.dir_id}/${t.name}/${name}`;
// What the "files changed" row says: git's error when it could not tell, else the whole count, which the listed files may be cut short of.
export const filesChanged = (body) => (body?.error ? `unknown: ${body.error}` : body?.files_total ?? (Array.isArray(body?.files) ? body.files.length : null));
const STATES = ["working", "blocked", "idle", "gone"];
const short = (id) => (id === "other" ? "other" : shortName(id));

// The flow head and where the scrubber stands, or nulls before the head arrives.
function flowOf(slices, t, ui) {
  const slice = slices.get(flowId(t));
  const head = slice?.body ?? null;
  if (!head) {
    return { head: null };
  }
  const s = scrubOf(ui, head, Date.now());
  const pages = pagesOf(slices, t, head);
  return { head, s, pages, view: modeOf(ui.graphMode) === "radial" ? radialView(head, pages, s) : scrubbed(head, pages, s) };
}

function modelOf(slices, ui, teamId) {
  const team = slices.get(teamId);
  if (!team) {
    return null;
  }
  const t = team.body;
  const ids = memberIds(team);
  const f = flowOf(slices, t, ui);
  const idx = new Map((f.head?.names ?? []).map((id, i) => [id, i]));
  const sel = ui.selected && ids.includes(ui.selected) ? ui.selected : null;
  if (sel) {
    const m = memberOf(slices, team, sel);
    const i = idx.get(sel);
    let count = null;
    if (f.head) {
      count = i === undefined ? 0 : f.view.events
        ? f.view.events.filter((e) => e[1] === i || e[2] === i).length
        : (f.head.totals ?? []).filter(([a, b]) => a === i || b === i).reduce((n, r) => n + r[2] + r[3], 0);
    }
    // Radial traces a member's sent and received paths; its card says how many of each.
    const dir = f.head && modeOf(ui.graphMode) === "radial" ? sentReceived(f.head, f.view, i) : null;
    const { hid } = idsOf({ slices, ui }, sel);
    const usage = hid ? slices.get(hid)?.body.turn_usage : undefined;
    return {
      kind: "member", id: sel, state: m.state, stream: streamOf(slices, t, slices.get(sel)?.body.placement?.stream ?? null),
      ctx: m.state === "gone" ? "×" : m.ctx.text,
      ctxAt: m.state === "gone" ? 0 : m.ctx.at ?? 0,
      busy: typeof m.share === "number" && m.shareWindowMs !== 0 && (m.shareWindowMs ?? 60_000) >= 60_000 ? `${Math.round(m.share * 100)}%` : "—",
      busyLabel: busyLabel(m.shareWindowMs),
      count, dir, scope: f.view?.partial ? `in the newest ${f.view.partial}` : "in the window",
      tokens: usage ? usage.input_tokens + usage.output_tokens : null, files: filesChanged(slices.get(filesIdOf(t, m.name))?.body),
      task: m.slug ?? (m.running ? "—" : "idle"), plan: m.orchestrator ? `${t.dir_id}/${t.name}` : null,
    };
  }
  const focus = ui.graphCard ?? null;
  const stream = focus?.startsWith?.("stream:") ? focus.slice("stream:".length) : null;
  const members = ids.map((id) => ({ id, m: memberOf(slices, team, id), stream: streamOf(slices, t, slices.get(id)?.body.placement?.stream ?? null) })).filter((x) => stream === null || x.stream === stream);
  const counts = Object.fromEntries(STATES.map((st) => [st, members.filter((x) => x.m.state === st && !x.m.running).length]));
  const running = members.filter((x) => x.m.running).length;
  const inside = new Set(members.map((x) => idx.get(x.id)).filter((i) => i !== undefined));
  let rows = [];
  if (f.head) {
    const shown = inWindow(eventsOf(f.head, f.pages), f.s.cursor, f.s.win).filter((e) => stream === null || inside.has(e[1]) || inside.has(e[2]));
    const idOf = (i) => (f.head.names[i] && f.head.names[i] !== "other" ? f.head.names[i] : null);
    rows = shown.slice(-LATEST).reverse().map(([sec, a, b, k]) => ({ time: hhmm(f.head.start_ms + sec * 1000), kind: kindOf(k), from: short(f.head.names[a] ?? "?"), to: short(f.head.names[b] ?? "?"), pair: idOf(a) && idOf(b) ? [idOf(a), idOf(b)] : null }));
  }
  // Map draws the team's own members; members of child-team lanes are left out, and the team card says how many.
  const lanes = stream === null ? laneChildren(slices, team, ids).length : 0;
  return { kind: "container", type: stream === null ? "team" : "stream", name: stream === null ? teamLabel(t) : streamName(stream), n: members.length, counts, running, rows, lanes };
}

export class GraphPanel extends Live {
  // A selected member's files and transcript header (tokens this turn); its newest exchanges are the summary's lease.
  leaseIds(snap, { teamId }) {
    const sel = snap.ui.selected;
    const team = snap.slices.get(teamId);
    if (!sel || !team || !memberIds(team).includes(sel)) {
      return [];
    }
    const { hid } = idsOf(snap, sel);
    const name = snap.slices.get(sel)?.body.name;
    return [...(hid ? [hid] : []), ...(name ? [filesIdOf(team.body, name)] : [])];
  }

  select(snap, { teamId }) {
    return { model: JSON.stringify(modelOf(snap.slices, snap.ui, teamId)) };
  }

  draw({ store }, s) {
    const m = JSON.parse(s.model);
    if (!m) {
      return html`<section class="panel gm-panel" aria-live="polite"><p class="dim">Loading…</p></section>`;
    }
    if (m.kind === "member") {
      const extra = html`<dl class="gm-extra">
        <dt>stream</dt><dd>${m.stream ? streamName(m.stream) : "—"}</dd>
        <dt>task</dt><dd>${m.task}</dd>
        <dt>context used</dt><dd>${m.ctx}${m.ctxAt > 0 ? html` · <${Age} text=${(now) => ageText(ageOf({ as_of: m.ctxAt }, {}, now))} />` : ""}</dd>
        <dt>${m.busyLabel}</dt><dd>${m.busy}</dd>
        <dt>messages</dt><dd>${m.count ?? "—"}<span class="dim"> ${m.scope}</span></dd>
        ${m.dir ? html`<dt>sent</dt><dd>${m.dir.sent}</dd><dt>received</dt><dd>${m.dir.received}</dd>` : null}
        <dt>tokens</dt><dd>${m.tokens ?? "—"}</dd>
        <dt>files changed</dt><dd>${m.files ?? "—"}</dd>
      </dl>`;
      return html`<${MemberSummary} key=${m.id} store=${store} id=${m.id} messages=${LATEST} live=${true} onClose=${() => store.setUi({ selected: null })} extra=${extra}>
        <${MemberActions} key=${m.id} store=${store} memberId=${m.id} disabled=${m.state === "gone" ? "member is gone" : null} />
        <div class="gm-buttons">
          <button class="tab" onClick=${() => store.setUi({ view: "conversation" })}>Open conversation</button>
          ${m.plan ? html`<button class="tab" onClick=${() => store.setUi({ view: "board", team: `team:${m.plan}`, selected: null })}>Plan</button>` : null}
          <button class="tab" onClick=${() => store.setUi({ expand: m.id })}>Expand</button>
        </div>
      <//>`;
    }
    const rows = msgs(m.rows, store);
    return html`<section class="panel gm-panel" aria-live="polite" aria-label=${`${m.type} ${m.name}`}>
      <h2>${m.name}</h2>
      <p class="sub">${m.type} · ${m.n} ${m.n === 1 ? "member" : "members"}${m.lanes ? ` · ${m.lanes} lane ${m.lanes === 1 ? "member is" : "members are"} in Flow` : ""}</p>
      <p class="gm-counts">${m.running > 0 ? html`<span class="pill st-working"><span aria-hidden="true">${glyphOf("running")} </span>${m.running} running</span>` : null}${STATES.map((st) => html`<span key=${st} class=${`pill st-${st}`}><span aria-hidden="true">${glyphOf(st)} </span>${m.counts[st]} ${stateWord(st).toLowerCase()}</span>`)}</p>
      <h3>Latest messages</h3>
      ${rows}
    </section>`;
  }
}

// The Plans board's card for a tile: an item (owner, stream, size, and the messages about it) or a container (done N of M and owners).
// t: { name, kind, id, status: {cls, text}, owner: {name, role, state, href} | null, stream, size, done, total, owners: [[name, n, href]], msgs,
// rows: message rows | null (not an item), partial: counts cover loaded teams only }.
export const TileCard = ({ t, cls = "", onClose, store, children = null }) => html`<aside class=${`gm-panel ${cls}`} aria-live="polite" aria-label=${`${t.kind} ${t.name}`}>
  ${onClose ? html`<button class="close" onClick=${onClose} aria-label="Close panel">×</button>` : null}
  <h2>${t.name}</h2>
  <p class="sub">${t.kind}${t.id ? html` · <span class="mono">${t.id}</span>` : null}</p>
  ${t.status ? html`<p><span class=${`chip pl-chip ${t.status.cls}`}>${t.status.text}</span></p>` : null}
  <dl>
    ${t.owner
      // Only what is known: no glyph or state while the member is not held, no role that only repeats the name.
      ? html`<dt>owner</dt><dd>${t.owner.state !== "unknown" ? html`<span class=${`ice-glyph st-${t.owner.state}`} aria-hidden="true">${glyphOf(t.owner.state)} </span>` : null}${t.owner.href ? html`<a href=${t.owner.href}>${t.owner.name}</a>` : t.owner.name}${[t.owner.role && t.owner.role !== t.owner.name ? t.owner.role : "", t.owner.state !== "unknown" ? stateWord(t.owner.state) : ""].filter(Boolean).map((x) => ` · ${x}`).join("")}</dd>`
      : null}
    ${t.rows
      ? html`${t.stream ? html`<dt>stream</dt><dd>${t.stream}</dd>` : null}${t.size ? html`<dt>size</dt><dd>${t.size}</dd>` : null}`
      : html`<dt>done</dt><dd>${t.done} of ${t.total}</dd>${t.owners.length ? html`<dt>owners</dt><dd>${t.owners.map(([n, c, href], i) => html`${i ? ", " : ""}${href ? html`<a href=${href}>${n}</a>` : n} (${c})`)}</dd>` : null}`}
    ${t.msgs === "—" || t.msgs === null ? null : html`<dt>messages</dt><dd>${t.msgs}</dd>`}
  </dl>
  ${t.partial ? html`<p class="dim">Counts from loaded teams.</p>` : null}
  ${t.rows ? html`<h3>Messages about this</h3>${msgs(t.rows, store)}` : null}
  ${children}
</aside>`;
