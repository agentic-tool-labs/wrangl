import { html } from "./h.js";
import { Live } from "./live-component.js";
import { Dialog } from "./dialog.js";
import { postTeamClose } from "./bridge.js";
import { teamRows } from "./select.js";
import { ageOf, ageText } from "./store.js";

// Close and reopen a team (ah-ui-focus §6). The daemon answers the preview and re-reads who is running on submit, so the dialog only
// draws what it was told and sends back the names it showed.

const bodyOf = (teamId, more) => {
  const [dir, team] = teamId.slice("team:".length).split(/\/(?=[^/]*$)/);
  return { dir, team, ...more };
};

// What the dialog says and offers, from the daemon's preview and the served summary row.
export function closePlan(preview, row, now = Date.now()) {
  const live = (preview.live ?? []).map((m) => ({ name: m.member, doing: [m.status, m.detail].filter(Boolean).join(": ") }));
  const counts = [
    row?.inFlight > 0 ? `${row.inFlight} open ${row.inFlight === 1 ? "exchange" : "exchanges"}` : null,
    row?.itemsTotal > row?.itemsDone ? `${row.itemsTotal - row.itemsDone} open ${row.itemsTotal - row.itemsDone === 1 ? "item" : "items"}` : null,
    row?.waitingOnYou > 0 ? `${row.waitingOnYou} waiting on you` : null,
  ].filter(Boolean);
  const reasons = (preview.held ?? []).map((v) => `${v.member}: ${[v.status, v.detail].filter(Boolean).join(", ")}`);
  const unknown = (preview.unknown ?? []).map((m) => {
    const at = row?.memberLife?.[m.member] ?? 0;
    return `${m.member} may be running, not seen since ${at > 0 ? `${ageText(ageOf({ as_of: at }, {}, now))} ago` : "an unknown time"}`;
  });
  const outstanding = counts.length + reasons.length + unknown.length > 0;
  const n = live.length;
  return {
    live,
    counts,
    reasons,
    unknown,
    leftRunning: preview.left_running ?? [],
    // the served summary ignores the marker for a live member, never for the Orchestrator
    holdsOpen: (preview.left_running ?? []).filter((n) => n !== "orchestrator"),
    outstanding,
    confirm: n > 0 ? `End ${n} ${n === 1 ? "session" : "sessions"} and close` : outstanding ? "Close anyway" : "Close",
    confirmLive: live.map((m) => m.name),
  };
}

export const failureText = (reply) => reply?.body?.detail || reply?.body?.error || (reply?.status ? `status ${reply.status}` : "no answer");

// Close or Reopen for one top-level team: a confirmation always comes first for a close, a reopen is one click.
export class TeamCloseButton extends Live {
  state = { plan: null, error: null, busy: false, changed: false };

  select(snap, { teamId }) {
    const row = teamRows(snap.slices).find((r) => r.id === teamId);
    return { name: row?.label ?? "", closed: (row?.closedAt ?? 0) > 0, lane: row ? row.laneOf !== null : true, noRecord: row?.noRecord === true, row: JSON.stringify(row ?? null) };
  }

  send = (more) => postTeamClose(bodyOf(this.props.teamId, more));

  openPreview = async () => {
    this.setState({ busy: true, error: null, changed: false });
    const reply = await this.send({ preview: true }).catch(() => null);
    this.setState(reply?.status === 200 ? { busy: false, plan: closePlan(reply.body, JSON.parse(this.sel.row)) } : { busy: false, plan: null, error: `Could not read the team: ${failureText(reply)}` });
  };

  close = async () => {
    const { plan } = this.state;
    this.setState({ busy: true, error: null });
    const reply = await this.send({ confirm_live: plan.confirmLive }).catch(() => null);
    if (reply?.status === 200) {
      this.setState({ busy: false, plan: null });
      const notes = [...(reply.body.still_live ?? []).map((n) => `${n} did not stop`), ...(reply.body.left_running ?? []).map((n) => `${n} is still running`)];
      this.props.store.setUi({ closedTeam: { id: this.props.teamId, name: this.sel.name, note: notes.join(" · ") } });
    } else if (reply?.status === 409 && reply.body?.preview) {
      // someone started after the dialog opened: the daemon wrote nothing and sent the table to read again
      this.setState({ busy: false, changed: true, plan: closePlan(reply.body.preview, JSON.parse(this.sel.row)) });
    } else {
      this.setState({ busy: false, error: `Could not close: ${failureText(reply)}` });
    }
  };

  reopen = async () => {
    this.setState({ busy: true, error: null });
    const reply = await this.send({ reopen: true }).catch(() => null);
    this.setState({ busy: false, error: reply?.status === 200 ? null : `Could not reopen: ${failureText(reply)}` });
  };

  draw(props, s) {
    const word = props?.word ?? "";
    // a lane closes with its team, but the daemon can retire one on its own, and that needs its own way back
    if ((s.lane && !s.closed) || !s.name || s.noRecord) {
      return null;
    }
    const { plan, error, busy, changed } = this.state;
    const dismiss = () => this.setState({ plan: null, error: null });
    // Home draws this inside a clickable row, and the dialog has no portal: a click on its text or backdrop must stop here.
    return html`<span class="team-close" onClick=${(e) => e.stopPropagation()}>
      ${s.closed
        ? html`<button type="button" class="tab" disabled=${busy} aria-label=${`Reopen${word} ${s.name}`} onClick=${this.reopen}>Reopen${word}</button>`
        : html`<button type="button" class="tab" disabled=${busy} aria-label=${`Close${word} ${s.name}`} onClick=${this.openPreview}>Close${word}</button>`}
      ${error && !plan ? html`<span class="team-close-err" role="alert">${error}</span>` : null}
      ${plan
        ? html`<${Dialog} title=${`Close ${s.name}?`} cls="team-close-dialog" onClose=${dismiss}>
            ${changed ? html`<p class="attn" role="alert">Someone started after this opened. Read it again.</p>` : null}
            ${plan.live.length > 0
              ? html`<p><strong>Closing ends these running sessions:</strong></p><ul class="team-close-live">${plan.live.map((m) => html`<li key=${m.name}><span class="mono">${m.name}</span>${m.doing ? ` · ${m.doing}` : ""}</li>`)}</ul>`
              : null}
            ${plan.leftRunning.length > 0 ? html`<p>Left running: <span class="mono">${plan.leftRunning.join(", ")}</span>.${plan.holdsOpen.length > 0 ? ` The team stays visible until ${plan.holdsOpen.join(", ")} ${plan.holdsOpen.length === 1 ? "stops" : "stop"}.` : ""}</p>` : null}
            ${plan.counts.length > 0 ? html`<p>${plan.counts.join(" · ")}</p>` : null}
            ${plan.reasons.length > 0 ? html`<ul>${plan.reasons.map((r) => html`<li key=${r}>${r}</li>`)}</ul>` : null}
            ${plan.unknown.length > 0 ? html`<ul>${plan.unknown.map((r) => html`<li key=${r}>${r}</li>`)}</ul>` : null}
            <p>${plan.outstanding || plan.live.length > 0 ? "Nothing is deleted. Reopen brings the team back." : "Nothing outstanding."}</p>
            ${error ? html`<p class="team-close-err" role="alert">${error}</p>` : null}
            <div class="need-actions"><button type="button" class="tab primary" disabled=${busy} onClick=${this.close}>${plan.confirm}</button><button type="button" class="tab" onClick=${dismiss}>Cancel</button></div>
          <//>`
        : null}
    </span>`;
  }
}

// The line after a close, with Undo: it lives in the ui state because the row it came from has just left the page.
export class TeamCloseNote extends Live {
  state = { error: null };

  select(snap) {
    return { closed: JSON.stringify(snap.ui.closedTeam ?? null) };
  }

  dismiss = () => {
    this.setState({ error: null });
    this.props.store.setUi({ closedTeam: null });
  };

  undo = async (closed) => {
    const reply = await postTeamClose(bodyOf(closed.id, { reopen: true })).catch(() => null);
    if (reply?.status === 200) {
      this.dismiss();
    } else {
      this.setState({ error: `Could not reopen: ${failureText(reply)}` });
    }
  };

  draw(_, s) {
    const closed = JSON.parse(s.closed);
    if (!closed) {
      return null;
    }
    return html`<div class="snooze-note team-close-note" role="status">
      <span>Closed ${closed.name}.${closed.note ? ` ${closed.note}.` : ""}${this.state.error ? ` ${this.state.error}` : ""}</span>
      <button type="button" class="tab" onClick=${() => this.undo(closed)}>Undo</button>
      <button type="button" class="tab" onClick=${this.dismiss}>Dismiss</button>
    </div>`;
  }
}
