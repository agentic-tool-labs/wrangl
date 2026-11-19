import { html } from "./h.js";
import { Live } from "./live-component.js";
import { Dialog } from "./dialog.js";
import { postTeamRename } from "./bridge.js";
import { teamSummary, displayNameProblem } from "./select.js";
import { failureText } from "./team-close.js";

// Give a team a display name (ah2-218 B2): a button on the team's header, and its dialog, which the app draws (`ui.rename` names the
// team) so it is not held inside the header. The name is display only: the request names the team by its real name, and the header
// shows the new name when the served summary carries it.

const bodyOf = (teamId, displayName) => {
  const [dir, team] = teamId.slice("team:".length).split(/\/(?=[^/]*$)/);
  return { dir, team, display_name: displayName };
};

const rowOf = (snap, teamId) => {
  const row = teamSummary(snap.slices, teamId);
  return { name: row?.name ?? "", label: row?.label ?? "", current: row?.displayName ?? "", noRecord: row?.noRecord === true };
};

export class TeamRenameButton extends Live {
  select(snap, { teamId }) {
    return rowOf(snap, teamId);
  }

  draw({ store, teamId }, s) {
    if (!s.name || s.noRecord) {
      return null;
    }
    return html`<button type="button" class="tab" aria-label=${`Rename ${s.label}`} title="Give this team a display name" onClick=${() => store.setUi({ rename: teamId })}>Rename</button>`;
  }
}

export class TeamRenameDialog extends Live {
  state = { text: null, touched: false, error: null, busy: false };

  select(snap, { teamId }) {
    return rowOf(snap, teamId);
  }

  componentDidMount() {
    super.componentDidMount();
    // the Dialog focuses its first control (×) as it mounts; the field is what a rename starts from
    this.input?.focus();
    this.input?.select?.();
  }

  close = () => this.props.store.setUi({ rename: null });

  send = async (displayName) => {
    this.setState({ busy: true, error: null });
    const reply = await postTeamRename(bodyOf(this.props.teamId, displayName)).catch(() => null);
    if (reply?.status === 200) {
      this.close();
    } else {
      this.setState({ busy: false, error: `Could not rename: ${failureText(reply)}` });
    }
  };

  save = (e) => {
    e.preventDefault();
    const text = this.state.text ?? this.sel.current;
    if (!this.state.busy && displayNameProblem(text) === null) {
      this.send(text.trim());
    } else {
      this.setState({ touched: true });
    }
  };

  draw(_, s) {
    const { touched, error, busy } = this.state;
    const text = this.state.text ?? s.current;
    const problem = displayNameProblem(text);
    // an empty field is only a problem once the user has typed in it or saved; too long or a line break shows at once
    const shown = problem && (touched || text.trim() !== "") ? problem : null;
    return html`<${Dialog} title=${`Rename ${s.label}`} cls="team-rename-dialog" onClose=${this.close}>
      <form class="tr-form" onSubmit=${this.save}>
        <label class="tr-field" for="tr-input">Display name</label>
        <input id="tr-input" type="text" autocomplete="off" spellcheck="false" value=${text} ref=${(el) => (this.input = el)}
          aria-invalid=${shown ? "true" : "false"} aria-describedby="tr-problem tr-hint"
          onInput=${(e) => this.setState({ text: e.target.value, touched: true })} />
        <p id="tr-problem" class="tr-problem" aria-live="polite">${shown ?? ""}</p>
        <p id="tr-hint" class="tr-hint">Shown wherever the team is named. Commands and addresses keep <span class="mono">${s.name}</span>.</p>
        ${error ? html`<p class="team-close-err" role="alert">${error}</p>` : null}
        <div class="need-actions">
          <button type="submit" class="tab primary" disabled=${busy}>Save</button>
          ${s.current ? html`<button type="button" class="tab" disabled=${busy} onClick=${() => this.send(null)}>Clear</button>` : null}
          <button type="button" class="tab" onClick=${this.close}>Cancel</button>
        </div>
      </form>
    <//>`;
  }
}
