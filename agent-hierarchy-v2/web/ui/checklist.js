import { html } from "./h.js";
import { Live } from "./live-component.js";
import { teamRows } from "./select.js";
import { Age, EtaChip } from "./ticker.js";
import { ageText, ageOf } from "./store.js";
import { teamModel, planIdOf, itemsId, checklistRows, hasOpenItems, CHECKLIST_STATES, streamName } from "./plans-model.js";

// The Orchestrator's checklist for one team: the open items of its streams, who holds each and the state it is in. It reads the
// plan and plan-items slices the daemon serves. A column no item has is left out; a field only some items have reads "none" on the others.

const BY_STATE = new Map(CHECKLIST_STATES.map((s) => [s.state, s]));
const ageSince = (ms) => (now) => ageText(ageOf({ as_of: ms }, {}, now));
const missing = html`<span class="dim">none</span>`;

export class ChecklistView extends Live {
  derive(snap, { teamId }) {
    if (this.memo?.snap === snap && this.memo.teamId === teamId) {
      return this.memo.out;
    }
    const now = Math.floor(Date.now() / 60_000) * 60_000;
    const rows = teamRows(snap.slices).filter((r) => r.id === teamId);
    const streams = rows.flatMap((r) => teamModel(r, snap.slices.get(planIdOf(r))?.body, now).streams).filter(hasOpenItems);
    const bodies = streams.map((s) => snap.slices.get(itemsId(s))?.body);
    const openListIds = [...new Set(streams.map((s) => `list:open:team:${s.dirId}/${s.team}`))];
    const openIds = new Set(openListIds.flatMap((id) => (snap.slices.get(id)?.body.open ?? []).map((o) => o.id)));
    const linked = (s, refs) => refs.map((ref) => `exchange:${s.dirId}/${ref}`).filter((id) => openIds.has(id));
    const itemsOf = (s) => snap.slices.get(itemsId(s))?.body.items ?? null;
    const out = {
      leases: [...rows.map((r) => planIdOf(r)), ...streams.map(itemsId), ...openListIds, ...streams.flatMap((s) => (itemsOf(s) ?? []).flatMap((i) => linked(s, i.refs ?? [])))],
      model: {
        rows: checklistRows(streams, itemsOf, (s, refs) => {
          const b = linked(s, refs).map((id) => snap.slices.get(id)?.body).find((x) => x?.state);
          return b ? { state: b.state, due_at: b.due_at, overdue_by: b.overdue_by } : null;
        }),
        loading: bodies.some((b) => b === undefined),
        truncated: bodies.some((b) => b?.truncated === true),
        streams: streams.length,
        ready: snap.ready,
      },
    };
    this.memo = { snap, teamId, out };
    return out;
  }

  select(snap, props) {
    return { model: JSON.stringify(this.derive(snap, props).model) };
  }

  leaseIds(snap, props) {
    return this.derive(snap, props).leases;
  }

  draw(_, s) {
    const m = JSON.parse(s.model);
    const waiting = m.rows.filter((r) => r.state === "waiting_orch").length;
    const has = (k) => m.rows.some((r) => r[k]);
    return html`<section class="checklist" aria-label="Checklist">
      <div class="cl-summary"><strong>${m.rows.length}</strong> open${waiting > 0 ? html` · <span class="cl-attn"><strong>${waiting}</strong> waiting-orch</span>` : null}${m.truncated ? " · the newest items only" : ""}</div>
      <div class="cl-scroll">
        ${m.rows.length === 0
          ? html`<p class="dim cl-empty">${!m.ready || m.loading ? "loading…" : m.streams === 0 ? "No plan streams yet." : "Nothing open."}</p>`
          : html`<table class="cl-table">
            <thead><tr><th scope="col">State</th><th scope="col">Item</th><th scope="col">Owner</th><th scope="col">Stream</th>${has("role") ? html`<th scope="col">Role</th>` : null}${has("todo") ? html`<th scope="col">Next action</th>` : null}${has("stateSince") ? html`<th scope="col">In state</th>` : null}${has("updatedAt") ? html`<th scope="col">Updated</th>` : null}<th scope="col">Refs</th></tr></thead>
            <tbody>${m.rows.map((r) => {
              const st = BY_STATE.get(r.state);
              return html`<tr key=${r.key} class=${`cl-row st-${r.state}`}>
                <td class=${st.attn ? "cl-state attn" : "cl-state"}><span class="cl-glyph" aria-hidden="true">${st.glyph}</span> ${st.label}</td>
                <td class="cl-title">${r.title}</td>
                <td class="cl-owner">${r.owner ?? html`<span class="dim">unassigned</span>`}</td>
                <td class="cl-stream mono">${streamName(r.stream)}</td>
                ${has("role") ? html`<td class="cl-role">${r.role ?? missing}</td>` : null}
                ${has("todo") ? html`<td class="cl-todo">${r.todo ?? missing}</td>` : null}
                ${has("stateSince") ? html`<td class="cl-since">${r.stateSince ? html`<${Age} key=${r.stateSince} text=${ageSince(r.stateSince)} />` : missing}</td>` : null}
                ${has("updatedAt") ? html`<td class="cl-updated">${r.updatedAt ? html`<${Age} key=${r.updatedAt} text=${ageSince(r.updatedAt)} />` : missing}</td>` : null}
                <td class="cl-refs">${r.refs.length > 0 ? html`<span class="mono" title=${r.refs.join(", ")}>${r.refs.join(" ")}</span>` : html`<span class="dim">none</span>`}${r.eta ? html` <${EtaChip} b=${r.eta} />` : null}</td>
              </tr>`;
            })}</tbody>
          </table>`}
      </div>
    </section>`;
  }
}
