import { html } from "./h.js";
import { Dialog } from "./dialog.js";
import { memberLink } from "./member-summary.js";
import { ItemDescription, ItemLog, extId } from "./item-log.js";

// The one work item detail: a glass dialog filling the middle of the app, on the Board and over the Sitrep alike. Each surface hands it a
// model `p`: kind, title, rows, holder, note and the rest are optional, and `log` is the item-log slice. `onPick` follows a link to another
// selection; `children` is what only the caller can read (the Board's history); `lead` goes first, under the kind (the Board's answer
// form, whose pick group is named by the dialog's title, id `kb-panel-title`).
export const optionsText = (q) => [...q.options.map((o) => o.label), ...(q.more > 0 ? [`${q.more} more`] : [])].join(" · ");
const more = (n) => `${n} more ${n === 1 ? "option" : "options"}`;

export function ItemDialog({ p, store, onClose, onPick = null, returnTo, children = null, lead = null }) {
  const link = (x) => (onPick ? html`<button type="button" class="kb-link" onClick=${() => onPick(x.id)}>${x.text}</button>` : x.text);
  return html`<${Dialog} title=${p.title} titleId="kb-panel-title" cls="item-detail pl-detail kb-panel" returnTo=${returnTo} onClose=${onClose}>
    <div class="id-body" tabindex="0" role="group" aria-label=${`${p.kind} details`}>
      <p class="pl-where mono">${p.kind}</p>
      ${lead}
      ${p.ask?.options.length ? html`<h3>Options</h3><ul class="kb-list">${p.ask.options.map((o) => html`<li key=${o.label}>${o.label}${o.label === p.ask.def ? html` <span class="kb-sub">(default)</span>` : null}</li>`)}${p.ask.more > 0 ? html`<li class="kb-sub">${more(p.ask.more)}</li>` : null}</ul>` : null}
      ${p.answer ? html`<a class="kb-answer" href=${p.answer.href}>${p.answer.text}</a>` : null}
      ${p.outcome && !p.outcomeLabel ? html`<p class="kb-outcome">${p.outcome}</p>` : null}
      <dl>
        ${p.rows.map(([k, v, cls]) => html`<dt key=${`t${k}`}>${k}</dt><dd key=${`d${k}`} class=${cls ?? undefined}>${k === "Sub-orchestrator" ? memberLink(p.holder.id, p.holder.name) : v}</dd>`)}
        ${p.holder && !p.rows.some(([k]) => k === "Sub-orchestrator") ? html`<dt>Owner</dt><dd>${memberLink(p.holder.id, p.holder.name)}</dd>` : null}
        ${p.group ? html`<dt>${p.group.label}</dt><dd>${link(p.group)}</dd>` : null}
        ${p.parent ? html`<dt>${p.parent.label ?? "Parent"}</dt><dd>${link(p.parent)}</dd>` : null}
        ${p.epic ? html`<dt>Epic</dt><dd>${link(p.epic)}</dd>` : null}
        ${p.ext ? html`<dt>Ext id</dt><dd class="mono">${extId(p.ext.id, p.ext.url)}</dd>` : null}
      </dl>
      ${p.log ? html`<${ItemDescription} key=${p.log} store=${store} id=${p.log} />` : null}
      ${p.note ? html`<h3>Notes</h3><p class="kb-outcome id-note">${p.note}</p>` : null}
      ${p.also?.length ? html`<h3>Also waiting</h3><ul class="kb-list">${p.also.map((q) => html`<li key=${q.id}>${q.text}${q.asker ? html` <span class="kb-sub">asked by ${q.asker}</span>` : null}${q.options.length ? html`<p class="kb-opts">${optionsText(q)}</p>` : null}</li>`)}</ul>` : null}
      ${p.members?.length ? html`<h3>Members</h3><p>${p.members.map((x, i) => html`${i ? ", " : ""}${memberLink(x.id, x.name)}`)}</p>` : null}
      ${p.outcome && p.outcomeLabel ? html`<h3>${p.outcomeLabel}</h3><p class="kb-outcome">${p.outcome}</p>` : null}
      ${p.kids?.length ? html`<h3>Children</h3><ul class="kb-list">${p.kids.map((k) => html`<li key=${k.id}>${link(k)}</li>`)}</ul>` : null}
      ${children}
      ${p.log ? html`<${ItemLog} key=${p.log} store=${store} id=${p.log} kids=${!p.kids?.length} />` : null}
      ${p.items ? html`<h3>Plan items</h3>${p.items.length ? html`<ul class="kb-list">${p.items.map((k) => html`<li key=${k.id}>${link(k)} <span class="kb-sub">${k.col}</span></li>`)}</ul>` : html`<p class="kb-outcome">No items in the plan yet.</p>`}` : null}
    </div>
  </${Dialog}>`;
}
