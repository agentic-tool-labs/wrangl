import { html } from "./h.js";
import { Live } from "./live-component.js";
import { Age } from "./ticker.js";
import { firstTeam, memberOf, teamOfMember, stateWord } from "./select.js";
import { idsOf } from "./transcript-rows.js";

// Hovering a member shows a card (graph) or a strip (tree, timeline). It reads only what the store already holds:
// entering and leaving make no request and change no subscription.

export const CARD = { w: 240, h: 132 };
const GAP = 8;

// Where the card goes, computed once on enter: beside the element, flipped to its other side near the window edge.
export function placeCard(rect, win, kind) {
  if (kind === "strip") {
    const below = rect.bottom + 4;
    return { x: Math.max(0, Math.min(rect.left, win.innerWidth - CARD.w - GAP)), y: below + CARD.h > win.innerHeight ? Math.max(0, rect.top - CARD.h - 4) : below };
  }
  const right = rect.right + GAP;
  const x = right + CARD.w > win.innerWidth ? Math.max(0, rect.left - GAP - CARD.w) : right;
  return { x, y: Math.max(0, Math.min(rect.top, win.innerHeight - CARD.h - GAP)) };
}

// The element the card was opened from: when a poll or redraw finds it detached (its row scrolled out or its view went) the card is not drawn.
const hoveredBy = new WeakMap();

export function hoverOn(store, id, el, kind = "card", win = globalThis) {
  const at = placeCard(el.getBoundingClientRect(), win, kind);
  hoveredBy.set(store, el);
  store.setUi({ hover: { id, kind, ...at } });
}

export function hoverOff(store) {
  hoveredBy.delete(store);
  if (store.state.ui.hover) {
    store.setUi({ hover: null });
  }
}

// The handlers a hoverable element takes: pointer and keyboard focus both open it, leaving or blurring closes it.
export const hoverProps = (store, id, kind) => ({
  onMouseEnter: (e) => hoverOn(store, id, e.currentTarget, kind),
  onMouseLeave: () => hoverOff(store),
  onFocusIn: (e) => hoverOn(store, id, e.currentTarget, kind),
  onFocusOut: () => hoverOff(store),
});

export class HoverCard extends Live {
  select(snap) {
    const h = snap.ui.hover ?? null;
    const hovered = hoveredBy.get(this.props.store);
    if (!h || (hovered && !globalThis.document?.body?.contains(hovered))) {
      return { id: null };
    }
    const team = teamOfMember(snap.slices, firstTeam(snap.slices, snap.ui), h.id);
    const m = team ? memberOf(snap.slices, team, h.id) : null;
    const { hid } = idsOf(snap, h.id);
    const usage = hid ? snap.slices.get(hid)?.body.turn_usage : undefined;
    return { id: h.id, kind: h.kind, x: h.x, y: h.y, name: m?.label ?? h.id.split("/").pop(), role: m?.role ?? "", ctx: m ? m.ctx : null, state: m?.state ?? "gone", running: m?.running ?? false, tool: m?.tool ?? null, toolAt: m?.toolAt ?? null, slug: m?.slug ?? null, process: (team && snap.slices.get(`team-summary:${team.body.dir_id}/${team.body.name}`)?.body.member_states?.[h.id]?.process) ?? "unknown", tokens: usage ? usage.input_tokens + usage.output_tokens : null };
  }

  draw(_, s) {
    if (s.id === null) {
      return null;
    }
    const elapsed = (now) => (s.toolAt ? `${Math.max(0, Math.floor((now - Date.parse(s.toolAt)) / 1000))}s` : "");
    return html`<aside class=${`hover-${s.kind}`} role="tooltip" style=${{ left: `${s.x}px`, top: `${s.y}px`, width: `${CARD.w}px` }}>
      <strong data-role=${s.role || undefined}>${s.name}</strong> <span class=${`pill st-${s.state}`}>${stateWord(s.state, s.running)}</span>
      <div>${s.tool ?? "no tool"}${s.toolAt ? html` · <${Age} text=${elapsed} />` : null}</div>
      <div>process: ${s.process}</div>
      <div>tokens this turn: ${s.tokens ?? "—"}</div>
      ${s.ctx ? html`<div>context: ${s.ctx.text}${s.ctx.text === "—" ? null : html` <span class="dim">${s.ctx.title}</span>`}</div>` : null}
      <div class="dim">${s.slug ?? "idle"}</div>
    </aside>`;
  }
}
