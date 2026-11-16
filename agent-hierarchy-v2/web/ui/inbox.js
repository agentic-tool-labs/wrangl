import { html } from "./h.js";
import { Live } from "./live-component.js";
import { Age } from "./ticker.js";
import { ageText, ageOf } from "./store.js";
import { hashOf } from "./deeplink.js";
import { inboxTeams, needsState, needsText, blockedText, teamNeeds } from "./select.js";
import { AttentionView, QAView } from "./questions-view.js";
import { INBOX_EMPTY } from "./questions-model.js";

// The Inbox (§1.5): Attention | Q&A, for one team and its lanes, or for every team grouped by team (no team in scope).
// Attention lists everything the one Needs-you count counts: what waits on the user (decisions and checklist items, answerable in place), blocked or
// failed members (each a link to its member page). Stuck open tasks (each a link to its item on the Board) are the orchestrator's:
// listed after them under their own heading, and never in the count.

const ageSince = (ms) => (now) => (ms > 0 ? ageText(ageOf({ as_of: ms }, {}, now)) : "");

// The row a member or task alert draws, and where its link goes.
const hrefOf = (c) => (c.kind === "Stuck" ? hashOf({ view: "board", team: c.teamId, item: c.itemKey }) : c.memberId ? hashOf({ view: "member", selected: c.memberId }) : null);

const AlertRow = ({ c }) => {
  const href = hrefOf(c);
  return html`<li class="inbox-row">
    <span class=${c.kind === "Error" ? "need-kind err" : "need-kind"}>${c.kind}</span>
    ${href ? html`<a class="inbox-link" href=${href}>${c.title}</a>` : html`<span class="inbox-link">${c.title}</span>`}
    <span class="inbox-ctx" title=${c.context}>${c.context}</span>
    <span class="inbox-age num">${c.at > 0 ? html`<${Age} text=${ageSince(c.at)} />` : null}</span>
  </li>`;
};

const lanesNote = (r) => (r.laneOf ? html` <span class="inbox-lane">lane of ${r.laneOf}</span>` : null);

class InboxTabs extends Live {
  select(snap, { teamId }) {
    const st = needsState(snap.slices, teamId);
    return { n: st.n, blocked: st.blocked, pending: st.pending };
  }

  draw({ store, tab }, s) {
    const pick = (inboxTab) => store.setUi({ inboxTab });
    return html`<nav class="views inbox-tabs" aria-label="Inbox">
      <button class=${tab === "qa" ? "tab" : "tab on"} aria-pressed=${tab !== "qa"} onClick=${() => pick("attention")}>Attention · <span class="num">${s.pending ? `${s.n}…` : s.n}</span>${blockedText(s.blocked)}</button>
      <button class=${tab === "qa" ? "tab on" : "tab"} aria-pressed=${tab === "qa"} onClick=${() => pick("qa")}>Q&A</button>
    </nav>`;
  }
}

class Attention extends Live {
  select(snap, { teamId }) {
    return { rows: JSON.stringify(inboxTeams(snap.slices, teamId).map((r) => ({ id: r.id, name: r.name, laneOf: r.laneOf, items: r.items, orch: r.orch, ...teamNeeds(r) }))) };
  }

  draw({ store, teamId }, s) {
    const rows = JSON.parse(s.rows);
    // The scoped team always shows (its questions say when nothing waits); a lane, or a team on the all-teams page, only when it draws a row.
    const shown = rows.filter((r) => r.id === teamId || r.n > 0 || r.blocked > 0 || r.orch.length > 0);
    const grouped = !teamId || shown.length > 1;
    if (shown.length === 0) {
      return html`<p class="dim qa-none inbox-none">${INBOX_EMPTY}</p>`;
    }
    return html`<div class="inbox-groups">${shown.map((r) => {
      const alerts = r.items.filter((c) => c.kind !== "Decision");
      const waiting = r.id === teamId || r.waitingOnYou > 0;
      return html`<section key=${r.id} class="inbox-team">
        ${grouped ? html`<h2 class="inbox-head">${r.name}${lanesNote(r)} <span class="inbox-needs">Needs you <span class="num">${needsText(r.n, r.pending)}</span>${blockedText(r.blocked)}</span></h2>` : null}
        ${alerts.length > 0 ? html`<ul class="inbox-rows" aria-label=${`Blocked and failed members in ${r.name}`}>${alerts.map((c) => html`<${AlertRow} key=${c.key} c=${c} />`)}</ul>` : null}
        ${alerts.length === 0 && r.blocked > 0 ? html`<p class="dim inbox-ended">Ended with ${r.blocked} blocked ${r.blocked === 1 ? "member" : "members"}</p>` : null}
        ${waiting ? html`<${AttentionView} key=${r.id} store=${store} teamId=${r.id} />` : null}
        ${r.orch.length > 0 ? html`<h3 class="inbox-orch-h" id=${`inbox-orch-${r.id}`}>For the orchestrator · not counted</h3>
          <ul class="inbox-rows" aria-labelledby=${`inbox-orch-${r.id}`}>${r.orch.map((c) => html`<${AlertRow} key=${c.key} c=${c} />`)}</ul>` : null}
      </section>`;
    })}</div>`;
  }
}

class QA extends Live {
  select(snap, { teamId }) {
    return { rows: JSON.stringify(inboxTeams(snap.slices, teamId).map((r) => [r.id, r.name, r.laneOf])) };
  }

  draw({ store, teamId }, s) {
    const rows = JSON.parse(s.rows);
    if (rows.length === 1 && rows[0][0] === teamId) {
      return html`<${QAView} store=${store} teamId=${teamId} />`;
    }
    return html`<div class="inbox-groups">${rows.map(([id, name, laneOf]) => html`<section key=${id} class="inbox-team">
      <h2 class="inbox-head">${name}${lanesNote({ laneOf })}</h2>
      <${QAView} store=${store} teamId=${id} />
    </section>`)}</div>`;
  }
}

export function InboxPage({ store, teamId, tab }) {
  return html`<div class="inbox">
    <${InboxTabs} store=${store} teamId=${teamId} tab=${tab} />
    ${tab === "qa" ? html`<${QA} key=${teamId ?? "all"} store=${store} teamId=${teamId} />` : html`<${Attention} key=${teamId ?? "all"} store=${store} teamId=${teamId} />`}
  </div>`;
}
