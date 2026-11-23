import { html } from "./h.js";
import { Live } from "./live-component.js";
import { descriptionBlocks, httpUrl, inlineParts } from "./plans-model.js";

export { descriptionBlocks };

// The item panel's work record (ah-work-items §10): the merged timeline, links, attachments and children of `item-log:<dirId>/<n>`.
// The slice is leased only while this is mounted, that is while a registered item's panel is open. A body can reach 200 x 8 KiB, so it
// shows the newest entries first, draws again only on a new slice version, keeps entry bodies in a closed <details> (no layout until
// opened) and runs no timer: times are absolute.
export const LOG_PAGE = 20;
const WHEN = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });

// Untrusted text: only http(s) is ever a link.
const outLink = (url, text) => html`<a href=${url} target="_blank" rel="noopener noreferrer">${text}<span class="sr"> (opens in a new tab)</span></a>`;

// An external id: a link when its URL is http(s), otherwise plain text.
export const extId = (id, url) => {
  const safe = httpUrl(url);
  return safe ? outLink(safe, id ?? url) : id ?? url ?? null;
};

const Ids = ({ label, ids }) => (ids?.length ? html`<dt>${label}</dt><dd class="mono">${ids.join(", ")}</dd>` : null);

const Entry = ({ e }) => html`<li>
  <span class="mono dim">${e.type}</span>
  <span class="t">${e.title}</span>
  ${e.by ? html`<span class="mono dim">${e.by}</span>` : null}
  <time class="pl-age" datetime=${new Date(e.ts_ms).toISOString()}>${WHEN.format(e.ts_ms)}</time>
  ${e.body ? html`<details><summary>Details<span class="sr"> of ${e.title}</span></summary><pre>${e.body}</pre></details>` : null}
</li>`;

export const Attachment = ({ a }) => {
  const safe = a.kind === "url" ? httpUrl(a.ref) : null;
  const facts = [a.kind, a.kind === "url" ? null : a.sha ? a.sha.slice(0, 7) : "uncommitted"].filter(Boolean).join(" · ");
  return html`<li>${safe ? outLink(safe, a.title ?? a.ref) : html`<span class="mono">${a.ref}</span>`}${a.title && !safe ? ` ${a.title}` : ""} <span class="kb-sub">${facts}</span></li>`;
};

// A gate's needs (adr-38), in the `ah work gate --json` shape: each need met or not, a reviewed need's verdict, its fills, and the asked line.
// The mark is a glyph plus words, so nothing rests on colour.
const Need = ({ n }) => html`<li>
  <span class=${n.filled ? "wi-mark ok" : "wi-mark no"} aria-hidden="true">${n.filled ? "✓" : "✗"}</span><span class="sr">${n.filled ? "Filled: " : "Not filled: "}</span>
  <span class="mono">${n.name}</span>${n.title ? ` ${n.title}` : ""}
  ${n.review ? html` <span class="kb-sub">review: ${n.review_state ?? "missing"}</span>` : null}
  ${n.fills?.length ? html` <span class="kb-sub mono">${n.fills.join(", ")}</span>` : null}
</li>`;

const Gate = ({ g }) => html`<h3>Gate</h3>
  <p class="kb-sub wi-gate-sum"><span class="tnum">${g.filled}/${g.total}</span> needs filled · decided by ${g.decider}${g.asked ? ` · asked: ${g.asked}` : ""}</p>
  ${g.needs?.length ? html`<ul class="kb-list wi-gate">${g.needs.map((n) => html`<${Need} key=${n.name} n=${n} />`)}</ul>` : html`<p class="dim">No needs declared</p>`}`;

// On a gate, its own rows (need declarations, the decider, the parked question) and the fills shown under their need leave the Attachments list.
const GATE_KINDS = new Set(["need", "need-review", "gate", "ask"]);
const plainAttachments = (b) => {
  if (!b.gate) {
    return b.attachments ?? [];
  }
  const filled = new Set((b.gate.needs ?? []).flatMap((n) => n.fills ?? []));
  return (b.attachments ?? []).filter((a) => !GATE_KINDS.has(a.kind) && !filled.has(a.ref));
};

// Each entry keyed on itself, so an open <details> stays on its entry when the capped window shifts. Identical entries are told apart by count.
const keyed = (entries) => {
  const seen = new Map();
  return entries.map((e) => {
    const k = `${e.ts_ms}|${e.type}|${e.by}|${e.title}`;
    const n = (seen.get(k) ?? 0) + 1;
    seen.set(k, n);
    return { e, key: `${k}|${n}` };
  });
};

// **bold** and `code` inside a line (a code span selects whole on a click, so a path copies in one go).
const inline = (text) => inlineParts(text).map((p) => (p.kind === "bold" ? html`<strong>${p.text}</strong>` : p.kind === "code" ? html`<code>${p.text}</code>` : p.text));

export const Block = ({ b }) =>
  b.kind === "head" ? html`<h4>${b.text}</h4>`
  : b.kind === "list" ? html`<ul class="kb-list">${b.items.map((item) => html`<li>${inline(item)}</li>`)}</ul>`
  : b.kind === "olist" ? html`<ol class="kb-list">${b.items.map((item) => html`<li>${inline(item)}</li>`)}</ol>`
  : b.kind === "code" ? html`<pre><code>${b.text}</code></pre>`
  : html`<p>${inline(b.text)}</p>`;

// The item's description, from the same slice as its timeline (leases are per owner, so the two hold it together). Nothing is shown when
// it has none or while the slice loads. It draws only when the text changes, not on each new timeline entry.
export class ItemDescription extends Live {
  leaseIds(_, { id }) {
    return [id];
  }

  select(snap, { id }) {
    const text = snap.slices.get(id)?.body?.description;
    return { text: typeof text === "string" && text.trim() !== "" ? text : null };
  }

  draw(_, { text }) {
    return text === null ? null : html`<h3>Description</h3><div class="id-desc">${descriptionBlocks(text).map((b) => html`<${Block} b=${b} />`)}</div>`;
  }
}

export class ItemLog extends Live {
  state = { all: false };

  leaseIds(_, { id }) {
    return [id];
  }

  select(snap, { id }) {
    const held = snap.slices.get(id);
    return { v: held?.version ?? -1, none: (held !== undefined && !held.body) || snap.gone?.has(id) === true };
  }

  draw({ store, id, kids = true }, s) {
    const b = store.state.slices.get(id)?.body;
    if (!b) {
      return html`<h3>Timeline</h3><p class="dim">${s.none ? "No timeline" : "Loading…"}</p>`;
    }
    const total = b.entries.length;
    const shown = this.state.all ? total : Math.min(LOG_PAGE, total);
    const newest = keyed(b.entries.slice(total - shown)).reverse();
    const linked = b.links?.blocked_by?.length || b.links?.blocks?.length || (kids && b.children?.length);
    const attachments = plainAttachments(b);
    return html`<h3>Timeline</h3>
      ${total === 0 ? html`<p class="dim">No entries</p>` : html`<ol class="pl-history wi-log">${newest.map(({ e, key }) => html`<${Entry} key=${key} e=${e} />`)}</ol>`}
      ${total > LOG_PAGE ? html`<div class="kb-more"><button type="button" class="tab" onClick=${() => this.setState({ all: !this.state.all })}>${this.state.all ? "Show fewer" : `Show all ${total}`}</button></div>` : null}
      ${b.more > 0 ? html`<p class="kb-sub">${b.more} older not shown: <span class="mono">ah work log ${b.id} --all</span></p>` : null}
      ${linked ? html`<h3>Links</h3><dl><${Ids} label="Blocked by" ids=${b.links?.blocked_by} /><${Ids} label="Blocks" ids=${b.links?.blocks} />${kids ? html`<${Ids} label="Children" ids=${b.children} />` : null}</dl>` : null}
      ${b.gate ? html`<${Gate} g=${b.gate} />` : null}
      ${attachments.length ? html`<h3>Attachments</h3><ul class="kb-list wi-attach">${attachments.map((a) => html`<${Attachment} key=${a.ref} a=${a} />`)}</ul>` : null}`;
  }
}
