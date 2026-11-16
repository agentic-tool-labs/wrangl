import { html } from "./h.js";

// The served action list drives every button and the help. An action's `invoke` says what it does; the web
// does what it can do in a page and shows the rest disabled with the reason.
export const BROWSER = "browser";
const NOT_IN_WEB = "not available in the web";

const KIND_ORDER = ["read", "view", "session", "copy", "owner"];
const KINDS = new Set(KIND_ORDER);
const kindOf = (k) => (KINDS.has(k) ? k : "other");

// What a served `get` route becomes in the page. Only a route naming the action list's own node is followed: its detail is the
// member's page, its inbox the messages tab, its graph the graph view. Any other route (another node, status, pane) is not.
const OWN_ROUTES = [
  ["/detail", "member"],
  ["/inbox", "messages"],
  ["/graph", "graph"],
];

export function getTarget(route, own) {
  if (typeof route !== "string" || typeof own !== "string" || own === "") {
    return null;
  }
  const found = OWN_ROUTES.find(([prefix]) => route === `${prefix}${own}`);
  return found ? { go: found[1] } : null;
}

// { enabled, reason } for one action. `handlers.node` is the served node path; `handlers.view` names the view actions the page performs;
// `handlers.disabled` is a reason that disables every action (a member that is gone).
export function plan(action, handlers = {}) {
  if (handlers.disabled) {
    return { enabled: false, reason: handlers.disabled };
  }
  const invoke = action && typeof action.invoke === "object" && action.invoke !== null ? action.invoke : {};
  if (typeof invoke.copy === "string") {
    return { enabled: true, reason: null };
  }
  if (invoke.ask_orchestrator && typeof invoke.ask_orchestrator.command === "string") {
    return { enabled: true, reason: null };
  }
  if (typeof invoke.get === "string") {
    return getTarget(invoke.get, handlers.node) ? { enabled: true, reason: null } : { enabled: false, reason: NOT_IN_WEB };
  }
  if (typeof invoke.view === "string") {
    if (invoke.view === "quit" || invoke.view === "open_file") {
      return { enabled: false, reason: BROWSER };
    }
    return handlers.view && typeof handlers.view[invoke.view] === "function" ? { enabled: true, reason: null } : { enabled: false, reason: NOT_IN_WEB };
  }
  return { enabled: false, reason: NOT_IN_WEB };
}

// Runs an enabled action. `env` supplies what the page can do: clipboard, go(target), view{name: fn}.
export async function perform(action, env) {
  const state = plan(action, env);
  if (!state.enabled) {
    return { done: false, reason: state.reason };
  }
  const { invoke } = action;
  if (typeof invoke.copy === "string") {
    await env.clipboard.writeText(invoke.copy);
    return { done: true, notice: `copied: ${invoke.copy}` };
  }
  if (invoke.ask_orchestrator) {
    await env.clipboard.writeText(invoke.ask_orchestrator.command);
    return { done: true, notice: `copied: ${invoke.ask_orchestrator.command}` };
  }
  if (typeof invoke.get === "string") {
    env.go(getTarget(invoke.get, env.node));
    return { done: true, notice: null };
  }
  await env.view[invoke.view](invoke);
  return { done: true, notice: null };
}

export function ActionBar({ actions, handlers, pending, onPick, onConfirm, onCancel }) {
  const list = Array.isArray(actions) ? actions : [];
  if (list.length === 0) {
    return null;
  }
  return html`<div class="action-bar">
    <div class="actions" role="group" aria-label="actions">
      ${list.map((a) => {
        const s = plan(a, handlers);
        return html`<button key=${a.id} type="button" class=${`action action-${kindOf(a.kind)}`} disabled=${!s.enabled} title=${s.enabled ? (a.help ?? "") : s.reason} onClick=${() => onPick(a)}>
          ${a.key ? html`<kbd>${a.key}</kbd> ` : null}${a.label}${s.enabled ? null : html` <span class="note">${s.reason}</span>`}
        </button>`;
      })}
    </div>
    ${pending
      ? html`<p class="confirm" role="alert">${pending.confirm}
          <button type="button" onClick=${() => onConfirm(pending)}>Confirm</button>
          <button type="button" onClick=${onCancel}>Cancel</button>
        </p>`
      : null}
  </div>`;
}

// `?` help, built from the same list: each action's key and label, grouped by kind.
export function Help({ actions, handlers }) {
  const list = Array.isArray(actions) ? actions : [];
  const kinds = [...new Set([...KIND_ORDER, ...list.map((a) => kindOf(a.kind))])];
  return html`<aside class="help" aria-label="help">
    <h2>Keys and actions</h2>
    ${kinds.map((k) => {
      const rows = list.filter((a) => kindOf(a.kind) === k);
      return rows.length === 0
        ? null
        : html`<section key=${k}>
            <h3>${k}</h3>
            <ul class="list">${rows.map((a) => html`<li key=${a.id}>${a.key ? html`<kbd>${a.key}</kbd> ` : html`<span class="dim">—</span> `}${a.label}${a.help ? html` <span class="note">${a.help}</span>` : null}${handlers && !plan(a, handlers).enabled ? html` <span class="note unavailable">${plan(a, handlers).reason}</span>` : null}</li>`)}</ul>
          </section>`;
    })}
  </aside>`;
}
