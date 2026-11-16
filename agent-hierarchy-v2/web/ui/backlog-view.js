import { html } from "./h.js";
import { Live } from "./live-component.js";
import { teamRows } from "./select.js";
import { WAVES, backlogId, waveColumns, extText, roleOf, httpUrl } from "./plans-model.js";
import { extId } from "./item-log.js";

// Backlog: a team's hierarchy-dir backlog as one ranked outline, read-only. Rows run wave (Now, Next, Later, Untriaged), then rank,
// then key, as the daemon serves them; Rank numbers the whole list 1..n. A row expands to its outcome, issue link and ext ref.
// Done and dropped items are not shown. The issue link is the one link; nothing here writes.

const STATE = { not_started: "not started", next: "next", in_progress: "in progress", reported: "reported, waiting on the Orchestrator", in_review: "in review", testing: "testing", blocked: "blocked", waiting_user: "waiting on you", waiting_orch: "waiting on Orchestrator" };
const STATE_CHIP = { in_progress: "st-in_progress", reported: "st-in_progress", in_review: "st-review", testing: "st-review", waiting_orch: "st-in_progress", blocked: "st-blocked", waiting_user: "st-blocked" };

// The outline of a backlog body: every open item in order with its rank over the whole list and its wave's label; null while not held.
export function outlineOf(body) {
  if (!body) {
    return null;
  }
  const b = waveColumns(body);
  const rows = WAVES.flatMap(([w, label]) => b.cols[w].map((i) => ({ ...i, waveLabel: label })));
  return { rows: rows.map((i, n) => ({ ...i, n: n + 1 })), open: b.open, health: b.health };
}

// What an empty outline says: not held yet, or nothing open.
export const emptyOf = (o) => (o === null ? "Loading…" : o.rows.length === 0 ? "Nothing open in the backlog." : null);

const dirOf = (slices, teamId) => teamRows(slices).find((r) => r.id === teamId)?.dirId ?? null;

const State = ({ state }) => (STATE_CHIP[state]
  ? html`<span class=${`chip bl-chip ${STATE_CHIP[state]}`}>${STATE[state]}</span>`
  : html`<span class="bl-state">${STATE[state] ?? state}</span>`);

const More = ({ i, id }) => html`<tr class="bl-more" id=${id}>
  <td></td>
  <td colspan="6">
    <dl>
      <dt>Outcome</dt><dd>${i.outcome || "—"}</dd>
      <dt>Issue</dt><dd>${httpUrl(i.ext_url) ? extId(extText(i.ext_id ?? i.ext ?? i.ext_url), i.ext_url) : i.issue?.url ? html`<a href=${i.issue.url} target="_blank" rel="noopener noreferrer">${i.ext ? extText(i.ext) : i.issue.url}<span class="sr"> (opens in a new tab)</span></a>` : "—"}</dd>
      <dt>Ext</dt><dd class="mono">${i.ext_id || i.ext || "—"}</dd>
    </dl>
  </td>
</tr>`;

export class BacklogView extends Live {
  state = { open: [] };

  select(snap, { teamId }) {
    const dir = dirOf(snap.slices, teamId);
    return { dir, v: dir ? snap.slices.get(backlogId(dir))?.version ?? -1 : -1 };
  }

  leaseIds(snap, { teamId }) {
    const dir = dirOf(snap.slices, teamId);
    return dir ? [backlogId(dir)] : [];
  }

  toggle = (key) => this.setState(({ open }) => ({ open: open.includes(key) ? open.filter((k) => k !== key) : [...open, key] }));

  draw({ store }, s) {
    const o = s.dir ? outlineOf(store.state.slices.get(backlogId(s.dir))?.body) : null;
    const empty = emptyOf(o);
    return html`<section class="bl" aria-label="Backlog">
      <div class="bl-bar">
        <h2 class="bl-title">Backlog</h2>
        ${o?.health.due ? html`<span class="chip attn">groom due</span>` : null}
        ${o?.health.stale > 0 ? html`<span class="chip">${o.health.stale} stale</span>` : null}
        ${o?.health.untriaged > 0 ? html`<span class="chip">${o.health.untriaged} untriaged</span>` : null}
        <span class="bl-tally">${o ? `${o.open} open` : ""}</span>
      </div>
      <div class="bl-main" aria-busy=${o ? "false" : "true"}>
        ${empty ? html`<p class="dim bl-none">${empty}</p>` : html`<table class="bl-table">
          <thead><tr><th scope="col" class="num">Rank</th><th scope="col">Item</th><th scope="col">Theme</th><th scope="col">Wave</th><th scope="col">Size</th><th scope="col">Holder</th><th scope="col">State</th></tr></thead>
          <tbody>${o.rows.flatMap((i) => {
            const open = this.state.open.includes(i.key);
            const id = `bl-more-${i.key}`;
            const row = html`<tr key=${i.key} class=${open ? "on" : ""}>
              <td class="num bl-rank">${i.n}</td>
              <td class="bl-item"><button class="bl-open" aria-expanded=${open} aria-controls=${open ? id : undefined} onClick=${() => this.toggle(i.key)}><span class="bl-caret" aria-hidden="true">${open ? "▾" : "▸"}</span><span class="bl-t" title=${i.title}>${i.title}</span><span class="bl-id">${i.key}</span></button></td>
              <td class="bl-theme" title=${i.theme ?? ""}>${i.theme ?? "—"}</td>
              <td>${i.waveLabel}</td>
              <td class="bl-size">${i.size ?? "—"}</td>
              <td class="bl-holder" data-role=${roleOf(i.holder ?? "") || undefined} title=${i.holder ?? ""}>${i.holder ?? "—"}</td>
              <td><${State} state=${i.state} /></td>
            </tr>`;
            return open ? [row, html`<${More} key=${`${i.key}+`} i=${i} id=${id} />`] : [row];
          })}</tbody>
        </table>`}
      </div>
    </section>`;
  }
}
