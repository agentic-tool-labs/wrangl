import { html, Component } from "./h.js";
import { Dialog } from "./dialog.js";
import { getRules, postRule } from "./bridge.js";

// The rules of engagement popup: pick a level, read what applies there, change it. The server resolves; this only draws its answer.

const LEVELS = [["repo", "Repo"], ["team", "Team"], ["project", "Project"], ["goal", "Goal"]];
const WORD = Object.fromEntries(LEVELS);
const NOTE = "Live members see changes at their next start, compaction or brief.";
const NO_ANSWER = "No answer from the server. The change may have been saved; check the list.";
const WHEN = new Intl.DateTimeFormat([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
// A decision rule that is in force: accepted by the user (spec-b19 slice 3). A proposed one stays in the main list until then.
const standing = (r) => r.kind === "decision" && !r.proposed;

// A key from the text: its first words, lower case, joined by -, in at most 40 characters.
export function suggestKey(text) {
  const words = text.toLowerCase().split(/[^a-z0-9]+/).filter(Boolean).slice(0, 4);
  return words.reduce((key, w) => (!key ? w.slice(0, 40) : key.length + w.length < 40 ? `${key}-${w}` : key), "");
}

// One row per key that reaches this level: its text and where it won, and whether it is owned here.
export function rowsAt(data, level, scope) {
  const answer = level === "repo" || level === "team" ? data.resolved[level] : data.resolved.items[scope];
  const own = (r) => r.level === level && (level === "repo" || r.scope === scope);
  return [
    ...(answer?.rules ?? []).map((r) => ({ ...r, off: false, own: own(r) })),
    ...(answer?.off ?? []).map((r) => ({ key: r.key, text: "", level: r.level, overrides: [], off: true, own: own(r) })),
  ];
}

// The text a key would have here without its own row: this team's, then the repo's. A switch off clears the row's own text, so that is all there is to offer.
function broaderText(data, key, level, team) {
  const textAt = (l, scope) => data.rows.find((r) => r.key === key && !r.off && r.level === l && r.scope === scope)?.text;
  return (level === "repo" ? null : (level === "team" ? null : textAt("team", team)) ?? textAt("repo", "")) ?? "";
}

const refusal = (reply) => `Refused: ${reply?.body?.message ?? NO_ANSWER}`;
const accepted = (reply) => reply?.status === 200 && reply.body?.code === 0;

// The text, its counter and a key: the add form when `fixedKey` is absent, a row's edit form when it is present. Escape leaves a row's form and stays in the dialog.
class RuleForm extends Component {
  state = { text: this.props.text ?? "", key: "", typedKey: false, error: "", busy: false };

  submit = async (e) => {
    e.preventDefault();
    if (this.state.busy) {
      return;
    }
    const { text, key, typedKey } = this.state;
    this.setState({ busy: true, error: "" });
    const error = await this.props.onSubmit(text, this.props.fixedKey ?? (typedKey ? key : suggestKey(text)));
    if (this.done) {
      return;
    }
    this.setState(error ? { busy: false, error } : { busy: false, text: "", key: "", typedKey: false });
  };

  componentWillUnmount() {
    this.done = true;
  }

  onKeyDown = (e) => {
    if (e.key === "Escape" && this.props.onCancel) {
      e.stopPropagation();
      this.props.onCancel();
    }
  };

  render({ fixedKey, max, label, action, onCancel, id }, { text, key, typedKey, error }) {
    const over = text.length > max;
    return html`<form class="ru-form" onSubmit=${this.submit} onKeyDown=${this.onKeyDown}>
      <label class="ru-field"><span>${label}</span>
        <textarea rows="3" value=${text} aria-invalid=${error ? "true" : null} aria-describedby=${`${id}-count ${id}-error`} onInput=${(e) => this.setState({ text: e.target.value })}></textarea>
      </label>
      <p class=${`ru-count${over ? " over" : ""}`} id=${`${id}-count`}>${text.length} of ${max} characters${over ? ", too long" : ""}</p>
      ${fixedKey ? null : html`<label class="ru-field"><span>Key</span>
        <input type="text" value=${typedKey ? key : suggestKey(text)} autocomplete="off" spellcheck="false" onInput=${(e) => this.setState({ key: e.target.value, typedKey: true })} />
      </label>`}
      <p class="ru-error" id=${`${id}-error`} role="alert">${error}</p>
      <div class="ru-acts">
        <button class="action" type="submit">${action}</button>
        ${onCancel ? html`<button class="action" type="button" onClick=${onCancel}>Cancel</button>` : null}
      </div>
    </form>`;
  }
}

export class RulesDialog extends Component {
  state = { data: null, loadError: "", level: "team", item: null, editing: null, message: "", refused: false, rowError: null };

  componentDidMount() {
    this.load();
  }

  componentWillUnmount() {
    this.gone = true;
  }

  // After a write or a cancel the control that had focus may be gone, and focus on the page would leave the dialog's keys behind.
  componentDidUpdate() {
    if (this.focusRow === undefined || !this.state.data) {
      return;
    }
    const key = this.focusRow;
    this.focusRow = undefined;
    const row = [...(this.body?.querySelectorAll?.("[data-key]") ?? [])].find((n) => n.getAttribute("data-key") === key);
    (row?.querySelector("textarea, button") ?? this.status)?.focus?.();
  }

  load = async () => {
    if (!this.props.team) {
      return;
    }
    const reply = await getRules(this.props.team).catch(() => null);
    if (this.gone) {
      return;
    }
    this.setState(accepted(reply) ? { data: reply.body, loadError: "" } : { loadError: reply?.body?.message ?? "No answer from the server." });
  };

  scope() {
    const { level, item, data } = this.state;
    if (level === "repo") {
      return "";
    }
    if (level === "team") {
      return this.props.team;
    }
    const open = (data?.items ?? []).filter((i) => i.kind === level);
    return open.some((i) => i.id === item) ? item : open[0]?.id ?? null;
  }

  // Sends one write and reads the list again; a refusal comes back to the caller. `said` is what the status line adds to the server's words, `focusKey` the row to land on.
  // `at` writes at a row's own level and scope, for a rule seen from a narrower level; `fields` are more of the body. On a refusal `onRefused` sees the reply.
  write = async (op, key, text, { said = "", focusKey, at, fields = {}, onRefused } = {}) => {
    if (this.busy) {
      return "The last change is still being sent.";
    }
    this.busy = true;
    const reply = await postRule({ op, team: this.props.team, level: at?.level ?? this.state.level, scope: at ? at.scope : this.scope(), key, ...(text === undefined ? {} : { text }), ...fields }).catch(() => null);
    if (!accepted(reply)) {
      onRefused?.(reply);
      this.busy = false;
      return refusal(reply);
    }
    await this.load();
    this.busy = false;
    if (!this.gone) {
      this.focusRow = focusKey;
      this.setState({ message: `${reply.body.message}${said}`, refused: false, editing: null, rowError: null });
    }
    return "";
  };

  // Off and Remove show what is going, since neither keeps the text.
  act = async (op, row) => {
    const error = await this.write(op, row.key, undefined, { said: row.own && row.text ? `. Its text was: ${row.text}` : "", focusKey: row.key });
    if (error && !this.gone) {
      this.setState({ message: error, refused: true });
    }
  };

  // Accepting is the user's alone: this page's bridge session is the user's proof, and only this button sends it. It names the words shown, so a rule
  // edited since is refused (rule-changed) and read again: the row then shows the current words beside the refusal. A refusal stays on the row.
  accept = async (row) => {
    let changed = false;
    const error = await this.write("accept", row.key, row.text, { focusKey: row.key, at: row, fields: { applies: row.applies, choose: row.choose }, onRefused: (reply) => (changed = reply?.body?.reason === "rule-changed") });
    if (changed) {
      await this.load();
    }
    if (error && !this.gone) {
      this.setState({ rowError: { key: row.key, message: error } });
    }
  };

  pick = (level) => {
    this.setState({ level, editing: null, message: "", refused: false, rowError: null });
  };

  rowView(row, data, level) {
    const { editing } = this.state;
    const id = `ru-${row.key}`;
    if (editing?.key === row.key) {
      return html`<li class="ru-row" key=${row.key} data-key=${row.key}>
        <p class="ru-meta"><code>${row.key}</code></p>
        <${RuleForm} id=${id} fixedKey=${row.key} text=${editing.text} max=${data.max_text} label=${editing.label} action="Save" onSubmit=${(text, key) => this.write(editing.op, key, text, { focusKey: key })} onCancel=${() => { this.focusRow = row.key; this.setState({ editing: null }); }} />
      </li>`;
    }
    const open = (op, label, text) => () => {
      this.focusRow = row.key;
      this.setState({ editing: { key: row.key, op, label, text }, message: "", refused: false, rowError: null });
    };
    const button = (word, onClick) => html`<button class="action" type="button" aria-label=${`${word}: ${row.key}`} onClick=${onClick}>${word}</button>`;
    const where = row.own ? `${WORD[row.level]}, set here` : `${WORD[row.level]}, inherited`;
    const decision = row.kind === "decision" && !row.off;
    const proposed = decision && row.proposed;
    const error = this.state.rowError?.key === row.key ? this.state.rowError.message : "";
    return html`<li class="ru-row" key=${row.key} data-key=${row.key}>
      <p class=${row.off ? "ru-text off" : "ru-text"}>${row.off ? "Switched off" : row.text}</p>
      ${decision ? html`<dl class="ru-decide"><dt>Applies when</dt><dd>${row.applies}</dd><dt>Choose</dt><dd>${row.choose}</dd></dl>` : null}
      <p class="ru-meta">${proposed ? html`<span class="ru-tag">Proposed</span>` : null}<code>${row.key}</code><span>${where}</span>${row.overrides?.length ? html`<span>overrides ${row.overrides.map((l) => WORD[l]).join(", ")}</span>` : null}${decision && !proposed && row.accepted_at ? html`<span>Accepted by ${row.accepted_by === "user" ? "you" : row.accepted_by}, <time datetime=${new Date(row.accepted_at).toISOString()}>${WHEN.format(row.accepted_at)}</time></span>` : null}</p>
      ${proposed ? html`<p class="ru-error ru-row-error" id=${`${id}-refused`} role="alert">${error}</p>` : null}
      <div class="ru-acts">
        ${proposed ? html`<button class="action" type="button" aria-label=${`Accept: ${row.key}`} aria-describedby=${error ? `${id}-refused` : null} onClick=${() => this.accept(row)}>Accept</button>` : null}
        ${row.own && !row.off ? button("Edit", open("edit", "Rule text", row.text)) : null}
        ${row.own && !row.off ? button("Switch off", () => this.act("off", row)) : null}
        ${row.own && row.off ? button("Switch on", open("edit", "Rule text", broaderText(data, row.key, level, this.props.team))) : null}
        ${row.own ? button("Remove", () => this.act("remove", row)) : null}
        ${row.own ? null : button("Override here", open("add", "Rule text for this level", row.off ? broaderText(data, row.key, level, this.props.team) : row.text))}
        ${row.own || row.off ? null : button("Switch off here", () => this.act("off", row))}
      </div>
    </li>`;
  }

  content() {
    const { data, level, item, message, refused } = this.state;
    const scope = this.scope();
    const items = data.items.filter((i) => i.kind === level);
    const all = scope === null ? [] : rowsAt(data, level, scope);
    const rows = all.filter((r) => !standing(r));
    const decided = all.filter(standing);
    return html`
      <fieldset class="ru-levels">
        <legend>Level</legend>
        ${LEVELS.map(([l, word]) => html`<label key=${l} class=${level === l ? "on" : ""}><input type="radio" name="ru-level" value=${l} checked=${level === l} onChange=${() => this.pick(l)} /><span>${word}${l === "team" ? `: ${this.props.team}` : ""}</span></label>`)}
      </fieldset>
      ${level === "project" || level === "goal" ? (items.length > 0
        ? html`<label class="ru-field ru-item"><span>${WORD[level]}</span><select value=${scope} onChange=${(e) => this.setState({ item: e.target.value, editing: null, message: "", refused: false, rowError: null })}>${items.map((i) => html`<option key=${i.id} value=${i.id}>${i.id} ${i.title}</option>`)}</select></label>`
        : html`<p class="ru-empty">No open ${level}s in this team or its repo's backlog.</p>`) : null}
      <p class=${refused ? "ru-status refused" : "ru-status"} role="status" tabindex="-1" ref=${(el) => (this.status = el)}>${message}</p>
      ${scope === null ? null : html`
        ${rows.length > 0 ? html`<ul class="ru-list" aria-label=${`Rules at ${WORD[level]}`}>${rows.map((r) => this.rowView(r, data, level))}</ul>` : decided.length > 0 ? null : html`<p class="ru-empty">No rules apply at this level.</p>`}
        ${decided.length > 0 ? html`<section class="ru-standing" aria-labelledby="ru-standing-h">
          <h3 id="ru-standing-h">Your standing decisions</h3>
          <ul class="ru-list">${decided.map((r) => this.rowView(r, data, level))}</ul>
        </section>` : null}
        <section class="ru-add" aria-label="Add a rule">
          <h3>Add a rule</h3>
          <${RuleForm} key=${`${level}/${scope}`} id="ru-add" max=${data.max_text} label="Rule text" action="Add rule" onSubmit=${(text, key) => this.write("add", key, text)} />
        </section>`}
      <p class="ru-note">${NOTE}</p>`;
  }

  render({ team, onClose }, { data, loadError }) {
    return html`<${Dialog} title="Rules of engagement" cls="rules-dialog" returnTo=".more-btn" onClose=${onClose}>
      <div class="ru-body" ref=${(el) => (this.body = el)}>
        ${!team ? html`<p class="ru-empty">Rules belong to a team's repo, and there is no team here yet.</p>`
          : data ? this.content()
          : loadError ? html`<p class="ru-error" role="alert">${loadError}</p><div class="ru-acts"><button class="action" type="button" onClick=${() => { this.setState({ loadError: "" }); this.load(); }}>Try again</button></div>`
          : html`<p class="ru-empty" role="status">Loading rules…</p>`}
        ${data && loadError ? html`<p class="ru-error" role="alert">The list could not be read again: ${loadError}</p>` : null}
      </div>
    </${Dialog}>`;
  }
}
