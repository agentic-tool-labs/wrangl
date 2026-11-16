import { html } from "./h.js";

// The viewer's choice to see internal messages (the server's `class` field, MessageClass in the daemon). Only "internal" hides: a class this
// client does not know, or none, shows, so a new kind can't vanish silently. This is a view; nothing is deleted or changed.

const KEY = "ah.messages.internal";
const ON = "on";

function saved() {
  try {
    return globalThis.localStorage?.getItem(KEY) === ON;
  } catch {
    return false;
  }
}

let remembered = null;

export const showInternal = (ui) => ui?.showInternal ?? (remembered ??= saved());

export const isInternal = (body) => body?.class === "internal";

// The items a viewer sees; `bodyOf` finds the object that carries `class`.
export const shownOf = (items, ui, bodyOf = (x) => x) => (showInternal(ui) ? items : items.filter((x) => !isInternal(bodyOf(x))));

export function setShowInternal(store, on) {
  remembered = on;
  try {
    globalThis.localStorage?.setItem(KEY, on ? ON : "off");
  } catch {
    // Not remembered in a private window; the choice still holds for this page.
  }
  store.setUi({ showInternal: on });
}

// The one control: what it does and how many messages that is. Nothing to show or hide, nothing drawn.
export const internalToggle = (store, on, count) => (count === 0
  ? null
  : html`<button type="button" class="tab internal-toggle" title="Alarms, nudges, check-ins, release checks and notes between agents" onClick=${() => setShowInternal(store, !on)}>${on ? "Hide" : "Show"} ${count} internal</button>`);
