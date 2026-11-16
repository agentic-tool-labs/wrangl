import { html } from "./h.js";
import { Live, differ } from "./live-component.js";
import { Age, spanText } from "./ticker.js";
import { ageText, ageOf } from "./store.js";
import { teamRows } from "./select.js";
import { queueRows } from "./plans-model.js";

// The Orchestrator's queue for one team: the items waiting for a member, how long each has waited and whether it has passed the time the
// daemon says it stalls at. The eligible members are the daemon's hint. The stall state moves with the clock, so the view works it out again
// every half minute. The stalled state is the viewer's clock against the daemon's stall_at, so a viewer whose clock is skewed from the
// daemon's can disagree with the daemon's own stalled count (`checklist show`) by the skew.

const TICK_MS = 30_000;
const ageSince = (ms) => (now) => ageText(ageOf({ as_of: ms }, {}, now));

export class QueueView extends Live {
  derive(snap, { teamId }) {
    const row = teamRows(snap.slices).find((r) => r.id === teamId);
    const id = row ? `queue:${row.dirId}/${row.name}` : null;
    const body = id ? snap.slices.get(id)?.body : undefined;
    return { id, rows: Array.isArray(body) ? queueRows(body, Date.now()) : null, ready: snap.ready, pending: id !== null && snap.pending.has(id) };
  }

  select(snap, props) {
    const { rows, ready, pending } = this.derive(snap, props);
    return { model: JSON.stringify({ rows, loading: !ready || pending }) };
  }

  leaseIds(snap, props) {
    const { id } = this.derive(snap, props);
    return id ? [id] : [];
  }

  tick = () => {
    const next = this.select(this.props.store.state, this.props);
    if (differ(next, this.sel ?? {})) {
      this.sel = next;
      this.forceUpdate();
    }
  };

  componentDidMount() {
    super.componentDidMount();
    this.timer = setInterval(this.tick, TICK_MS);
  }

  componentWillUnmount() {
    super.componentWillUnmount();
    clearInterval(this.timer);
  }

  draw(_, s) {
    const m = JSON.parse(s.model);
    const now = Date.now();
    const stalled = m.rows?.filter((r) => r.stalled).length ?? 0;
    return html`<section class="checklist queue" aria-label="Queue">
      <div class="cl-summary"><strong>${m.rows?.length ?? 0}</strong> queued${stalled > 0 ? html` · <span class="cl-attn"><strong>${stalled}</strong> stalled</span>` : null}</div>
      <div class="cl-scroll">
        ${m.rows === null || m.rows.length === 0
          ? html`<p class="dim cl-empty">${m.rows === null ? (m.loading ? "loading…" : "The daemon serves no queue for this team.") : "Nothing is queued."}</p>`
          : html`<table class="cl-table">
            <thead><tr><th scope="col">Stall</th><th scope="col">Item</th><th scope="col">Role</th><th scope="col">Queued</th><th scope="col">Eligible members (hint)</th><th scope="col">Last dispatch</th></tr></thead>
            <tbody>${m.rows.map((r) => html`<tr key=${r.key} class=${r.stalled ? "cl-row q-stalled" : "cl-row"}>
              <td class=${r.stalled ? "cl-state attn" : "cl-state"}>${r.stallAt === null ? html`<span class="dim">no stall time</span>` : html`<span class="cl-glyph" aria-hidden="true">${r.stalled ? "⚠" : "◔"}</span> ${r.stalled ? "stalled" : "stalls in"} ${spanText(Math.abs(r.stallAt - now))}`}</td>
              <td class="cl-title mono">${r.item}</td>
              <td class="cl-role">${r.role ?? html`<span class="dim">—</span>`}</td>
              <td class="cl-since">${r.queuedSince ? html`<${Age} key=${r.queuedSince} text=${ageSince(r.queuedSince)} />` : html`<span class="dim">—</span>`}</td>
              <td class="cl-owner">${r.eligible.length > 0 ? r.eligible.join(", ") : html`<span class="dim">none idle</span>`}</td>
              <td class="cl-updated">${r.lastDispatch ? html`<${Age} key=${r.lastDispatch} text=${ageSince(r.lastDispatch)} />` : html`<span class="dim">never</span>`}</td>
            </tr>`)}</tbody>
          </table>`}
      </div>
    </section>`;
  }
}
