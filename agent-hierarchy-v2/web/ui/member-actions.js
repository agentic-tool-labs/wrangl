import { html } from "./h.js";
import { Live } from "./live-component.js";
import { Dialog } from "./dialog.js";
import { ActionBar, Help, perform, plan } from "./actions.js";
import { firstTeam } from "./select.js";
import { KEY_TABLE } from "./keys.js";

// A member's served action list in the R6 frame. The list is read from the daemon when the panel or overlay opens and again when
// the member's slice changes (the pid and session copies come and go); it is never read on hover, and nothing is posted.

// The page's answer to each kind of invoke, given the served node path.
export function envFor({ store, memberId, node }) {
  const team = firstTeam(store.state.slices, store.state.ui);
  const suffix = team ? `${team.body.dir_id}/${team.body.name}` : null;
  const view = { help: () => store.setUi({ help: true }) };
  if (suffix) {
    view["switch.plan"] = () => store.setUi({ view: "board", team: `team:${suffix}`, selected: null });
  }
  const go = (target) => {
    if (target.go === "member") {
      // Detail is the member's page (ah-ui-ia §1.3), for the orchestrator as for any member.
      store.setUi({ view: "member", selected: memberId, expand: null });
    } else if (target.go === "messages") {
      store.setUi({ expand: memberId, expandTab: "messages" });
    } else if (target.go === "graph") {
      store.setUi({ view: "graph", expand: null });
    }
  };
  return { node, view, go, clipboard: globalThis.navigator?.clipboard };
}

export const isTextTarget = (el) => {
  const tag = String(el?.localName ?? el?.tagName ?? "").toLowerCase();
  return tag === "input" || tag === "textarea" || tag === "select" || el?.isContentEditable === true;
};

// The served action keys name Enter and the arrows as words (the terminal client's vocabulary); a browser reports them as KeyboardEvent.key values.
const KEY_NAMES = { enter: "Enter", left: "ArrowLeft", right: "ArrowRight" };

// Enter on a focused button or link activates that control, so it is not also a shortcut.
const isActivatable = (el) => ["button", "a"].includes(String(el?.localName ?? el?.tagName ?? "").toLowerCase());

export const keyMatches = (actionKey, eventKey) => actionKey != null && (KEY_NAMES[actionKey] ?? actionKey) === eventKey;

// `?` opens and closes Help, except while focus is in a text field.
export function helpKey(e, store) {
  if (e.key !== "?" || isTextTarget(e.target) || e.ctrlKey || e.metaKey || e.altKey) {
    return false;
  }
  store.setUi({ help: store.state.ui.help !== true });
  return true;
}

export class MemberActions extends Live {
  state = { doc: null, failed: false, pending: null, notice: "" };

  select(snap, { memberId }) {
    return { version: snap.slices.get(memberId)?.version ?? 0 };
  }

  componentDidMount() {
    super.componentDidMount();
    this.fetch();
  }

  componentDidUpdate() {
    if (this.sel.version !== this.fetchedFor) {
      this.fetch();
    }
  }

  componentWillUnmount() {
    super.componentWillUnmount();
    this.ask = (this.ask ?? 0) + 1;
  }

  async fetch() {
    this.fetchedFor = this.sel.version;
    const ask = (this.ask = (this.ask ?? 0) + 1);
    try {
      const doc = await this.props.store.fetchActions(this.props.memberId);
      if (ask === this.ask) {
        this.setState({ doc, failed: false });
      }
    } catch {
      if (ask === this.ask) {
        this.setState({ failed: true });
      }
    }
  }

  // `disabled`: a reason every action is shown disabled with (the member page of a member that is gone).
  env() {
    return { ...envFor({ store: this.props.store, memberId: this.props.memberId, node: this.state.doc?.node?.path }), disabled: this.props.disabled ?? null };
  }

  run = async (action) => {
    this.setState({ pending: null });
    try {
      const result = await perform(action, this.env());
      this.setState({ notice: result.done ? (result.notice ?? "") : result.reason });
    } catch {
      this.setState({ notice: "could not copy" });
    }
  };

  pick = (action) => (action.confirm ? this.setState({ pending: action }) : this.run(action));

  // A key acts on the action that names it, while focus is inside the panel and not in a text field.
  onKeyDown = (e) => {
    if (isTextTarget(e.target) || e.ctrlKey || e.metaKey || e.altKey || (e.key === "Enter" && isActivatable(e.target))) {
      return;
    }
    const env = this.env();
    const action = (this.state.doc?.actions ?? []).find((a) => keyMatches(a.key, e.key) && plan(a, env).enabled);
    if (action) {
      e.preventDefault();
      this.pick(action);
    }
  };

  draw() {
    const { doc, failed, pending, notice } = this.state;
    return html`<div class="member-actions" onKeyDown=${this.onKeyDown}>
      ${doc ? html`<${ActionBar} actions=${doc.actions} handlers=${this.env()} pending=${pending} onPick=${this.pick} onConfirm=${this.run} onCancel=${() => this.setState({ pending: null })} />` : failed ? html`<p class="dim">${this.props.disabled ? `actions disabled: ${this.props.disabled}` : "actions unavailable"}</p>` : null}
      ${notice ? html`<p class="notice" role="status">${notice}</p>` : null}
    </div>`;
  }
}

// Help over the open member's actions, or the root list when none is open.
// The words a newcomer meets with no other explanation.
const GLOSSARY = [
  ["brief", "A task one agent sends to another."],
  ["stale", "A brief whose addressee has gone, so nobody is working on it."],
  ["dispatched", "Sent to an agent to work on."],
  ["waiting-orch", "Waiting on the Orchestrator, the agent that leads the team."],
  ["groom due", "The backlog is due for a tidy: sorting, ranking and clearing what is done."],
  ["sitrep", "A short summary of where a team's work stands."],
  ["surrogate", "The Orchestrator answered for you, on something you let it decide."],
];

export class HelpDialog extends Live {
  state = { doc: null, failed: false };

  select(snap) {
    return { memberId: snap.ui.expand ?? snap.ui.selected ?? null };
  }

  componentDidMount() {
    super.componentDidMount();
    this.fetch();
  }

  componentDidUpdate() {
    if (this.sel.memberId !== this.fetchedFor) {
      this.fetch();
    }
  }

  async fetch() {
    this.fetchedFor = this.sel.memberId;
    const ask = (this.ask = (this.ask ?? 0) + 1);
    try {
      const doc = await this.props.store.fetchActions(this.sel.memberId);
      if (ask === this.ask) {
        this.setState({ doc, failed: false });
      }
    } catch {
      if (ask === this.ask) {
        this.setState({ doc: null, failed: true });
      }
    }
  }

  componentWillUnmount() {
    super.componentWillUnmount();
    this.ask = (this.ask ?? 0) + 1;
  }

  draw({ onClose, store }) {
    const { doc, failed } = this.state;
    const env = envFor({ store, memberId: this.sel.memberId, node: doc?.node?.path });
    return html`<${Dialog} title="Help" cls="help-dialog" onClose=${onClose}>
      <section class="glossary" aria-label="Words used here"><h3>Words used here</h3><dl>${GLOSSARY.map(([word, means]) => html`<div key=${word}><dt>${word}</dt><dd>${means}</dd></div>`)}</dl></section>
      <section class="page-keys" aria-label="Page keys"><h3>Page keys</h3><dl>${KEY_TABLE.map((k) => html`<div key=${k.label}><dt><kbd>${k.label}</kbd></dt><dd>${k.does}</dd></div>`)}</dl></section>
      ${doc ? html`<${Help} actions=${doc.actions} handlers=${env} />` : failed ? html`<p class="dim">actions unavailable</p>` : html`<p class="dim">Loading…</p>`}
    <//>`;
  }
}
