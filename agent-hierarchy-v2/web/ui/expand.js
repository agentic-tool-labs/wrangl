import { html } from "./h.js";
import { internalToggle, isInternal, showInternal } from "./msg-filter.js";
import { Live } from "./live-component.js";
import { Age } from "./ticker.js";
import { Dialog } from "./dialog.js";
import { firstTeam, memberOf, partyName, teamOfMember, stateWord } from "./select.js";
import { MemberActions } from "./member-actions.js";
import { idsOf, transcriptRows } from "./transcript-rows.js";
import { sheet } from "./lazy.js";

// The expand overlay for one member: Overview (cards and a live tail of the transcript), Conversation (a link to that view),
// Messages (the member's exchanges, paged by the server's member filter), Files changed and Tests. It holds its own leases
// and its own copy of the windows it shows, and releases both when it closes.

export const TABS = [
  ["overview", "Overview"],
  ["conversation", "Conversation"],
  ["messages", "Messages"],
  ["files", "Files changed"],
  ["tests", "Tests"],
];
export const TAIL = 5;
export const TAIL_ENTRIES = 12;
export const MESSAGES = 50;

// One formatter for every row: toLocaleTimeString builds a new one per call, which the feed paid on each live update.
const HHMM = new Intl.DateTimeFormat([], { hour: "2-digit", minute: "2-digit" });
const hhmm = (iso) => (iso ? HHMM.format(new Date(iso)) : "");

export class ExpandOverlay extends Live {
  state = { tail: [], messages: null, failed: false };
  asked = false;

  select(snap, { memberId }) {
    const team = teamOfMember(snap.slices, firstTeam(snap.slices, snap.ui), memberId);
    const m = team ? memberOf(snap.slices, team, memberId) : null;
    const { hid } = idsOf(snap, memberId);
    const header = hid ? snap.slices.get(hid)?.body : undefined;
    const filesId = team && m ? `files:${team.body.dir_id}/${team.body.name}/${m.name}` : null;
    const files = filesId ? snap.slices.get(filesId)?.body : undefined;
    const tests = files?.head ? snap.slices.get(`tests:${files.head}`)?.body : undefined;
    const usage = header?.turn_usage;
    this.files = files?.files ?? [];
    this.tests = tests?.sets ?? [];
    this.team = team ? `${team.body.dir_id}/${team.body.name}` : null;
    this.recent = m?.recent ?? "";
    return {
      tab: snap.ui.expandTab ?? "overview",
      internal: showInternal(snap.ui),
      name: m?.name ?? memberId.split("/").pop(),
      state: m?.state ?? "gone",
      running: m?.running ?? false,
      role: m?.role ?? "",
      slug: m?.slug ?? null,
      branch: m?.branch ?? null,
      workRoot: m?.workRoot ?? null,
      tool: m?.tool ?? null,
      toolAt: m?.toolAt ?? null,
      tokens: usage ? usage.input_tokens + usage.output_tokens : null,
      hid,
      pathOk: header ? header.path_ok : null,
      newest: header?.newest_offset ?? -1,
      filesId,
      head: files?.head ?? null,
      branchOfFiles: files?.branch ?? null,
      filesKey: this.files.map((f) => `${f.xy}${f.path}`).join("\n"),
      testsKey: this.tests.map((t) => `${t.set}|${t.status}|${t.counts.passed}|${t.counts.failed}`).join("\n"),
    };
  }

  get owner() {
    return `expand:${this.props.memberId}`;
  }

  componentDidMount() {
    super.componentDidMount();
    this.attach();
    // The tail's rows wear the Conversation sheet, so the first read waits for it; a sheet that fails still shows the tail, unstyled.
    const tail = () => !this.gone && this.loadTail();
    sheet("/ui/r6-conversation.css", "/ui/r6-plans.css").then(tail, tail);
  }

  componentWillUnmount() {
    this.gone = true;
    super.componentWillUnmount();
    this.props.store.lease(this.owner, []);
  }

  componentDidUpdate() {
    this.attach();
    if (this.sel.newest !== this.tailAt) {
      this.loadTail();
    }
    if (this.sel.tab === "messages" && !this.asked) {
      this.loadMessages();
    }
  }

  attach() {
    const s = this.sel;
    const ids = [...(s.hid ? [s.hid, `list:${s.hid}`] : []), ...(s.filesId ? [s.filesId] : []), ...(s.head ? [`tests:${s.head}`] : [])];
    this.props.store.lease(this.owner, ids);
  }

  // The newest few entries of the transcript, read again whenever its header moves.
  async loadTail() {
    const s = this.sel;
    if (!s.hid || !s.pathOk) {
      return;
    }
    this.tailAt = s.newest;
    const ask = (this.tailAsk = (this.tailAsk ?? 0) + 1);
    try {
      const doc = await this.props.store.fetchList(`list:${s.hid}`, { limit: TAIL_ENTRIES });
      if (ask === this.tailAsk) {
        this.setState({ tail: transcriptRows([...doc.rows].reverse()) });
      }
    } catch {
      // A failed read is tried again only when the header's newest entry moves.
    }
  }

  // The member's exchanges through the server's member filter; without it the tab falls back to the member's last three.
  async loadMessages() {
    this.asked = true;
    const team = this.team;
    if (!team) {
      return;
    }
    try {
      const doc = await this.props.store.fetchList(`list:exchanges:team:${team}`, { member: this.props.memberId, limit: MESSAGES });
      this.setState({ messages: doc.rows, failed: false });
    } catch {
      this.setState({ messages: null, failed: true });
    }
  }

  pick = (tab) => {
    if (tab === "conversation") {
      this.props.store.setUi({ view: "conversation", selected: this.props.memberId, expand: null });
      return;
    }
    this.props.store.setUi({ expandTab: tab });
  };

  inFeed = () => this.props.store.setUi({ expand: null, selected: null });

  draw({ onClose }, s) {
    const elapsed = (now) => (s.toolAt ? `${Math.max(0, Math.floor((now - Date.parse(s.toolAt)) / 1000))}s` : "");
    const { tail, messages, failed } = this.state;
    const shown = s.internal || messages === null ? messages ?? [] : messages.filter((x) => !isInternal(x.body));
    return html`<${Dialog} title=${`${s.name} · expand`} cls="expand" onClose=${onClose}>
      <${MemberActions} store=${this.props.store} memberId=${this.props.memberId} />
      <div class="tabs" role="tablist" aria-label="Member">
        ${TABS.map(([k, label]) => html`<button key=${k} role="tab" class=${s.tab === k ? "tab etab on" : "tab etab"} aria-selected=${s.tab === k} onClick=${() => this.pick(k)}>${label}</button>`)}
      </div>
      ${s.tab === "overview"
        ? html`<div class="cards">
            <section class="card"><h3 data-text=${s.name}>${s.name}</h3><p class=${`pill st-${s.state}`}>${stateWord(s.state, s.running)}</p><p>${s.role || "—"}</p></section>
            <section class="card"><h3 data-text="Task">Task</h3><p>${s.slug ?? "idle"}</p><p class="mono dim">${s.branch ?? "—"}</p><p class="mono dim">${s.workRoot ?? "—"}</p></section>
            <section class="card"><h3 data-text="Now">Now</h3><p>${s.tool ?? "no tool"}${s.toolAt ? html` · <${Age} text=${elapsed} />` : null}</p><p>tokens this turn: ${s.tokens ?? "—"}</p></section>
            <section class="card tail"><h3 data-text="Live tail">Live tail</h3>${tail.length === 0 ? html`<p class="dim">nothing yet</p>` : html`<ul>${(s.internal ? tail : tail.filter((r) => !r.internal)).slice(-TAIL).map((r) => html`<li key=${r.key} class=${`tr-row tail-row k-${r.kind}`}>${r.label}</li>`)}</ul>`}</section>
          </div>`
        : null}
      ${s.tab === "messages"
        ? html`<section class="messages" aria-label="Messages">
            ${internalToggle(this.props.store, s.internal, (messages ?? []).filter((x) => isInternal(x.body)).length)}
            ${messages !== null
              ? shown.length === 0
                ? html`<p class="dim">no messages</p>`
                : html`<ul>${shown.map((x) => html`<li key=${x.id} class="msg-row"><span class=${`kind ${x.body.open ? "brief" : "response"}`}>${x.body.open ? "brief" : "done"}</span> ${hhmm(x.body.created)} ${x.body.title || x.body.slug} <span class="route">${partyName(x.body, "from")} → ${partyName(x.body, "to")}</span></li>`)}</ul>`
              : failed
                ? html`<p class="dim">The server has no per-member message list yet. Latest messages:</p>
                    ${this.recent === "" ? html`<p class="dim">none</p>` : html`<ul>${this.recent.split("\n").map((line) => { const [slug, , from, to] = line.split("|"); return html`<li key=${`${slug}|${from}|${to}`}>${slug} <span class="route">${from} → ${to}</span></li>`; })}</ul>`}
                    <button class="link" onClick=${this.inFeed}>view in feed</button>`
                : html`<p class="dim">Loading…</p>`}
          </section>`
        : null}
      ${s.tab === "files"
        ? html`<section aria-label="Files changed"><h3>${s.branchOfFiles ?? s.branch ?? ""}</h3>${s.filesKey === "" ? html`<p class="dim">none</p>` : html`<ul class="files">${s.filesKey.split("\n").map((l) => html`<li key=${l} class="file-row"><span class="xy mono">${l.slice(0, 2)}</span> ${l.slice(2)}</li>`)}</ul>`}</section>`
        : null}
      ${s.tab === "tests"
        ? html`<section aria-label="Tests">${s.testsKey === "" ? html`<p class="dim">no results</p>` : html`<ul class="tests">${s.testsKey.split("\n").map((l) => { const [set, status, passed, failed] = l.split("|"); return html`<li key=${set} class=${`t-${status}`}>${set}: ${status} · ${passed} passed${Number(failed) > 0 ? `, ${failed} failed` : ""}</li>`; })}</ul>`}</section>`
        : null}
    <//>`;
  }
}
