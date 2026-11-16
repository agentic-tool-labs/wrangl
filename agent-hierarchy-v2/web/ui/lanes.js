import { html } from "./h.js";
import { Live } from "./live-component.js";
import { Age } from "./ticker.js";
import { ageText, ageOf } from "./store.js";
import { memberIds } from "./select.js";
import { streamName } from "./plans-model.js";

// A team's place among the lanes: the lane it is (confirmed by its manager), or the lane it only claims to be; the streams it manages,
// from the manager's own file; and where each member's messages go. It reads the team and member slices and shows what they serve,
// as served: the lane's state is the sub-orchestrator's own report, and routing is the daemon's, never worked out here and never taken
// as a sign the target is live. What a lane child writes (its report, its claimed parent) is rendered as text only.

const LANE_STATES = { working: ["◐", "working"], blocked: ["⚠", "blocked"], review: ["◎", "in review"], done: ["✓", "done"] };
const STREAM_STATES = { open: ["◐", "open"], done: ["✓", "done"] };
const VIA = { "dissolved-lane": "the lane was dissolved", none: "the lead was not found" };
const ageSince = (ms) => (now) => ageText(ageOf({ as_of: ms }, {}, now));
const dash = html`<span class="dim">—</span>`;
const text = (v) => (typeof v === "string" && v.length > 0 ? v : null);
const at = (team, name) => `${name}@${team}`;

function stateCell(map, state) {
  const [glyph, word] = map[state] ?? ["·", state ?? "unknown"];
  return html`<span class="cl-glyph" aria-hidden="true">${glyph}</span> ${word}`;
}

function clipped(value, cls = "ln-clip") {
  return html`<span class=${cls} title=${value}>${value}</span>`;
}

export class LanesView extends Live {
  derive(snap, { teamId }) {
    const team = snap.slices.get(teamId);
    const ids = memberIds(team);
    const members = ids.map((id) => snap.slices.get(id)?.body).filter(Boolean);
    return { team, ids, members };
  }

  select(snap, props) {
    const { team, members } = this.derive(snap, props);
    const b = team?.body;
    const model = b
      ? {
          dirId: b.dir_id,
          lane: b.lane ?? null,
          claim: b.lane_claim ?? null,
          streams: b.streams ?? null,
          total: b.streams_total ?? null,
          leads: members.filter((m) => m.lead || m.routes_to).map((m) => ({ name: m.name, lead: m.lead ?? null, routes: m.routes_to ?? null })),
          memberCount: members.length,
        }
      : null;
    return { model: JSON.stringify(model), ready: snap.ready };
  }

  leaseIds(snap, props) {
    return this.derive(snap, props).ids;
  }

  open(team) {
    this.props.store.setUi({ team: `team:${JSON.parse(this.sel.model).dirId}/${team}`, view: "board" });
  }

  teamLink(team) {
    return html`<button type="button" class="ln-link" onClick=${() => this.open(team)}>${team}</button>`;
  }

  draw(_, s) {
    const m = JSON.parse(s.model);
    if (!m) {
      return html`<section class="checklist lanes" aria-label="Lanes"><p class="dim cl-empty">${s.ready ? "The daemon serves no team slice for this team." : "loading…"}</p></section>`;
    }
    const nothing = !m.lane && !m.claim && !m.streams;
    return html`<section class="checklist lanes" aria-label="Lanes">
      <div class="cl-scroll">
        ${m.lane ? this.drawLane(m.lane) : null}
        ${m.claim ? html`<p class="ln-claim"><span class="cl-glyph" aria-hidden="true">?</span> <strong>Claims to be a lane of ${m.claim.parent_team}</strong>, not confirmed by ${m.claim.parent_team}.</p>` : null}
        ${m.streams ? this.drawStreams(m.streams, m.total) : null}
        ${nothing ? html`<p class="dim cl-empty">This team is not a lane and manages no streams.</p>` : null}
        ${this.drawLeads(m.leads, m.memberCount)}
      </div>
    </section>`;
  }

  drawLane(lane) {
    const r = lane.reported ?? null;
    const updated = Date.parse(r?.updated ?? "") || null;
    return html`<div class="ln-lane">
      <p><span class="ln-badge">lane ${streamName(lane.stream)}</span> of ${this.teamLink(lane.parent_team)}${lane.state ? html` <span class="dim">·</span> stream ${stateCell(STREAM_STATES, lane.state)} <span class="dim">(the manager's record)</span>` : null}</p>
      ${r
        ? html`<p class="ln-reported"><span class="dim">Reported by its sub-orchestrator:</span> <span class=${r.state === "blocked" ? "cl-state attn" : "cl-state"}>${stateCell(LANE_STATES, r.state)}</span>${updated ? html` <span class="dim">·</span> <${Age} key=${updated} text=${ageSince(updated)} />` : null}</p>
          ${text(r.summary) ? html`<p class="ln-summary">${r.summary}</p>` : null}
          ${text(r.result) ? html`<p class="ln-result"><span class="dim">Result:</span> <span class="mono">${r.result}</span></p>` : null}`
        : html`<p class="dim">No report from its sub-orchestrator yet.</p>`}
    </div>`;
  }

  drawStreams(streams, total) {
    const open = streams.filter((x) => x.state === "open").length;
    return html`<div class="ln-streams">
      <div class="cl-summary"><strong>${open}</strong> open · ${streams.length}${total && total > streams.length ? html` of <strong>${total}</strong>` : null} streams <span class="dim">(the manager's own record)</span></div>
      <table class="cl-table">
        <thead><tr><th scope="col">Stream</th><th scope="col">State</th><th scope="col">Branch</th><th scope="col">Base</th><th scope="col">Sub-orchestrator</th><th scope="col">Lane team</th></tr></thead>
        <tbody>${streams.map((x) => html`<tr key=${x.name} class="cl-row">
          <td class="cl-title mono">${clipped(x.name)}</td>
          <td class="cl-state">${stateCell(STREAM_STATES, x.state)}</td>
          <td class="cl-role mono">${text(x.branch) ? clipped(x.branch) : dash}</td>
          <td class="cl-role mono">${text(x.base) ?? dash}</td>
          <td class="cl-owner">${text(x.sub_orchestrator) ?? dash}</td>
          <td class="cl-owner">${text(x.child_team) ? this.teamLink(x.child_team) : dash}</td>
        </tr>`)}</tbody>
      </table>
    </div>`;
  }

  drawLeads(leads, count) {
    if (leads.length === 0) {
      return count > 0 ? html`<p class="dim cl-empty">No member here takes direction from a lead.</p>` : null;
    }
    return html`<div class="ln-leads">
      <div class="cl-summary"><strong>${leads.length}</strong> of ${count} members have a lead or a route</div>
      <table class="cl-table">
        <thead><tr><th scope="col">Member</th><th scope="col">Lead</th><th scope="col">Messages go to</th></tr></thead>
        <tbody>${leads.map((l) => html`<tr key=${l.name} class="cl-row">
          <td class="cl-title">${l.name}</td>
          <td class="cl-owner mono">${l.lead ? at(l.lead.team, l.lead.name) : dash}</td>
          <td class=${l.routes && l.routes.via !== "lead" ? "cl-state attn" : "cl-owner"}>${!l.routes
            ? html`<span class="dim">no route served</span>`
            : l.routes.via === "lead"
              ? html`the lead`
              : html`<span class="cl-glyph" aria-hidden="true">⚠</span> ${text(l.routes.name) && text(l.routes.team) ? html`<span class="mono">${at(l.routes.team, l.routes.name)}</span> ` : html`nowhere `}<span class="ln-why">(${VIA[l.routes.via] ?? l.routes.via})</span>`}</td>
        </tr>`)}</tbody>
      </table>
    </div>`;
  }
}
