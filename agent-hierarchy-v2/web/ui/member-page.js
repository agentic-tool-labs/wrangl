import { html } from "./h.js";
import { internalToggle, isInternal, shownOf, showInternal } from "./msg-filter.js";
import { Live } from "./live-component.js";
import { partyIdOf, partyName, teamSummary } from "./select.js";
import { hashOf } from "./deeplink.js";
import { MemberActions } from "./member-actions.js";
import { MemberHead, MemberNow, memberLeaseIds, memberModel, teamFor } from "./member-summary.js";
import { idsOf } from "./transcript-rows.js";
import { tokensPerTurn } from "./conversation.js";
import { itemsId, planStreams } from "./graph-world.js";
import { STATE_LABEL } from "./plans-model.js";

// The member page, `#/member/<id>` (ah-ui-ia §1.3, §4): the member summary's header and Now, expanded with the current brief, the latest
// report, the member's messages, files and tests, context, the items it has held, links and actions. Every section reads an existing
// slice or list; nothing here is new on the server. A member that is gone renders from its last slices, its actions disabled.

export const MESSAGES = 50;
export const LEASED_ROWS = 20;
export const TURN_ENTRIES = 60;
const GONE = "member is gone";

const HHMM = new Intl.DateTimeFormat([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
const when = (iso) => (iso && Number.isFinite(Date.parse(iso)) ? HHMM.format(new Date(iso)) : "—");
const bodyIdOf = (exchangeId) => `exchange-body:${exchangeId.slice("exchange:".length)}`;
const filesIdOf = (m) => `files:${m.dir}/${m.team}/${m.name}`;
const itemAt = (i) => Date.parse(i.updated_at ?? i.state_since ?? i.done_seen_at ?? "") || 0;

// The current brief: what the filter shows, else a hidden one, so a member on a release check has one. The run anchor is no one's brief.
export const briefOf = (shownToMe, allToMe) => shownToMe.find((r) => r.b.open) ?? allToMe.find((r) => r.b.open && r.b.slug !== "pipeline-run-anchor");

export class MemberPage extends Live {
  state = { rows: null, failed: false, turns: null };

  // The page's own leases: the summary's, the transcript header and its list, files and tests at head, every stream's items (History),
  // the exchanges shown (so a response arriving updates its row) and the excerpts of the current brief and the latest report.
  leaseIds(snap, { id }) {
    const team = teamFor(snap.slices, snap.ui, id);
    if (!team) {
      return [];
    }
    const s = this.sel;
    const { hid } = idsOf(snap, id);
    return [
      ...memberLeaseIds(snap.slices, snap.ui, id),
      ...(hid ? [hid, `list:${hid}`] : []),
      ...(s?.filesId ? [s.filesId] : []),
      ...(s?.head ? [`tests:${s.head}`] : []),
      ...planStreams(snap.slices, team.body).map((st) => itemsId(team.body, st)),
      ...(this.state.rows ?? []).slice(0, LEASED_ROWS).map((r) => r.id),
      ...(s?.briefId ? [bodyIdOf(s.briefId)] : []),
      ...(s?.reportId ? [bodyIdOf(s.reportId)] : []),
    ];
  }

  select(snap, { id }) {
    const slices = snap.slices;
    const m = memberModel(slices, snap.ui, id);
    if (!m) {
      return { model: "null" };
    }
    // With no team held, memberModel gives the gone fallback; nothing team-scoped is read.
    const t = teamFor(slices, snap.ui, id)?.body ?? null;
    const { hid } = idsOf(snap, id);
    const header = hid ? slices.get(hid)?.body : undefined;
    const usage = header?.turn_usage;
    const filesId = filesIdOf(m);
    const files = slices.get(filesId)?.body;
    const tests = files?.head ? slices.get(`tests:${files.head}`)?.body : undefined;
    const live = (r) => slices.get(r.id)?.body ?? r.body;
    const rows = (this.state.rows ?? []).map((r) => ({ id: r.id, b: live(r) })).filter((r) => !r.b.withdrawn);
    const internal = showInternal(snap.ui);
    const shown = shownOf(rows, snap.ui, (r) => r.b);
    const toMe = shown.filter((r) => partyIdOf(r.b, "to") === id);
    const brief = briefOf(toMe, rows.filter((r) => partyIdOf(r.b, "to") === id));
    const report = toMe.find((r) => r.b.response_created);
    const text = (r, field) => {
      const x = r ? slices.get(bodyIdOf(r.id))?.body : undefined;
      return x ? (x.gone ? { gone: true } : { text: x[field] ?? null, truncated: x.excerpt_truncated === true }) : null;
    };
    const held = (t ? planStreams(slices, t) : []).flatMap((st) => (slices.get(itemsId(t, st))?.body.items ?? []).map((i) => ({ ...i, stream: st })));
    const history = held
      .filter((i) => i.holder === m.name || i.holder === id)
      .sort((a, b) => itemAt(b) - itemAt(a))
      .map((i) => ({ key: i.key, title: i.title ?? "", state: i.state, label: STATE_LABEL[i.state] ?? i.state, href: hashOf({ view: "board", team: m.teamId, item: i.key }) }));
    const model = {
      ...m,
      hid,
      pathOk: header ? header.path_ok : null,
      tokens: usage ? usage.input_tokens + usage.output_tokens : null,
      files: (files?.files ?? []).map((f) => [f.xy, f.path]),
      filesTotal: files?.files_total ?? (files?.files ?? []).length,
      filesBranch: files?.branch ?? m.branch,
      filesTruncated: files?.truncated === true,
      head: files?.head ?? null,
      tests: (tests?.sets ?? []).map((x) => [x.set, x.status, x.counts?.passed ?? 0, x.counts?.failed ?? 0]),
      brief: brief ? { id: brief.id, slug: brief.b.title || brief.b.slug, created: brief.b.created, from: partyName(brief.b, "from"), text: text(brief, "request_excerpt") } : null,
      report: report ? { id: report.id, slug: report.b.title || report.b.slug, created: report.b.response_created, text: text(report, "response_excerpt") } : null,
      internal,
      internalCount: rows.filter((r) => isInternal(r.b)).length,
      messages: shown.map((r) => {
        const out = partyIdOf(r.b, "from") === id;
        return { id: r.id, created: r.b.created, slug: r.b.title || r.b.slug, open: r.b.open === true, from: partyName(r.b, "from"), to: partyName(r.b, "to"), peer: partyIdOf(r.b, out ? "to" : "from") };
      }),
      history,
      links: {
        conversation: hashOf({ view: "conversation", selected: id }),
        graph: hashOf({ view: "graph", selected: id }),
        timeline: hashOf({ view: "timeline", selected: id }),
        board: hashOf({ view: "board", team: m.teamId, owner: m.name }),
      },
    };
    return {
      model: JSON.stringify(model),
      newest: teamSummary(slices, m.teamId)?.feedNewest ?? 0,
      turnAt: header?.newest_offset ?? -1,
      listId: t ? `list:exchanges:team:${t.dir_id}/${t.name}` : null,
      filesId,
      head: files?.head ?? null,
      briefId: brief?.id ?? null,
      reportId: report?.id ?? null,
    };
  }

  componentDidMount() {
    super.componentDidMount();
    this.loadMessages();
    this.loadTurns();
  }

  componentDidUpdate() {
    super.componentDidUpdate();
    if (this.sel.newest !== this.seen || this.sel.listId !== this.listAt) {
      this.loadMessages();
    }
    if (this.sel.turnAt !== this.turnAt) {
      this.loadTurns();
    }
  }

  componentWillUnmount() {
    this.gone = true;
    super.componentWillUnmount();
  }

  // The member's exchanges, newest first, through the server's member filter; read again whenever the team's feed moves.
  // Read again when the team's feed moves, and once the team (so the list) is known.
  async loadMessages() {
    this.seen = this.sel.newest;
    this.listAt = this.sel.listId;
    if (!this.sel.listId) {
      this.setState({ rows: [] });
      return;
    }
    const ask = (this.msgAsk = (this.msgAsk ?? 0) + 1);
    try {
      const doc = await this.props.store.fetchList(this.sel.listId, { member: this.props.id, limit: MESSAGES });
      !this.gone && ask === this.msgAsk && this.setState({ rows: doc.rows, failed: false });
    } catch {
      !this.gone && ask === this.msgAsk && this.setState({ failed: true });
    }
  }

  // ponytail: per turn and turn time average the newest TURN_ENTRIES transcript entries only, as Conversation's Details average its loaded windows.
  async loadTurns() {
    const m = JSON.parse(this.sel.model);
    this.turnAt = this.sel.turnAt;
    if (!m?.hid || !m.pathOk) {
      return;
    }
    const ask = (this.turnAsk = (this.turnAsk ?? 0) + 1);
    try {
      const doc = await this.props.store.fetchList(`list:${m.hid}`, { limit: TURN_ENTRIES });
      !this.gone && ask === this.turnAsk && this.setState({ turns: tokensPerTurn([...doc.rows].reverse()) });
    } catch {
      // read again when the header's newest entry moves
    }
  }

  draw({ store }, s) {
    const m = JSON.parse(s.model);
    if (!m) {
      return html`<p class="dim skeleton">Loading…</p>`;
    }
    const { rows, failed, turns } = this.state;
    const excerpt = (x, none) => (x === null
      ? html`<p class="dim" aria-busy="true">Loading…</p>`
      : x.gone
        ? html`<p class="dim">no longer available</p>`
        : html`<p class="prose">${x.text ?? none}</p>${x.truncated ? html`<p class="dim">Excerpt only: the daemon does not serve the full text.</p>` : null}`);
    return html`<article class="member-page" data-role=${m.role || undefined} data-state=${m.state} aria-label=${`Member ${m.name}`}>
      <p class="mp-back"><a href=${m.teamHref}>← ${m.team} · Overview</a></p>
      <header class="mp-head panel">
        <${MemberHead} m=${m} Tag="h1" />
      </header>
      <div class="mp-grid">
        <section class="mp-card" aria-labelledby="mp-now"><h2 id="mp-now">Now</h2><${MemberNow} m=${m} /></section>
        <section class="mp-card" aria-labelledby="mp-ctx"><h2 id="mp-ctx">Context</h2>
          <dl>
            <dt>context used</dt><dd class=${m.ctxOver ? "mp-ctxv over" : "mp-ctxv"} title=${m.ctxTitle}>${m.ctx}${m.ctx !== "—" ? html` <span class="dim">${m.ctxTitle}</span>` : null}</dd>
            <dt>this turn</dt><dd>${m.tokens ?? "—"}${m.tokens !== null ? html` <span class="dim">tokens</span>` : null}</dd>
            <dt>per turn</dt><dd>${turns?.average ?? "—"}${turns ? html` <span class="dim">over ${turns.turns} recent turns</span>` : null}</dd>
            <dt>turn time</dt><dd>${turns?.seconds != null ? `${turns.seconds} s` : "—"}${turns?.seconds != null ? html` <span class="dim">avg</span>` : null}</dd>
          </dl>
        </section>
        <section class="mp-card mp-wide" aria-labelledby="mp-brief"><h2 id="mp-brief">Current brief</h2>
          ${rows === null && !failed ? html`<p class="dim">Loading…</p>` : m.brief
            ? html`<p class="mp-meta"><b>${m.brief.slug}</b> <span class="dim">from ${m.brief.from} · ${when(m.brief.created)}</span></p>${excerpt(m.brief.text, "—")}`
            : html`<p class="dim">No open brief.</p>`}
        </section>
        <section class="mp-card mp-wide" aria-labelledby="mp-report"><h2 id="mp-report">Latest report</h2>
          ${rows === null && !failed ? html`<p class="dim">Loading…</p>` : m.report
            ? html`<p class="mp-meta"><b>${m.report.slug}</b> <span class="dim">${when(m.report.created)}</span></p>${excerpt(m.report.text, "—")}`
            : html`<p class="dim">No report yet.</p>`}
        </section>
        <section class="mp-card mp-wide" aria-labelledby="mp-msgs"><h2 id="mp-msgs">Messages${m.messages.length ? html` <span class="dim">· ${m.messages.length}</span>` : null}</h2>
          ${failed ? html`<p class="dim">Messages unavailable.</p>` : rows === null ? html`<p class="dim">Loading…</p>` : m.messages.length === 0 ? html`<p class="dim">No messages.</p>`
            : html`<ul class="mp-msgs">${m.messages.map((x) => html`<li key=${x.id}>${x.peer
              ? html`<button type="button" class="mp-msg" aria-label=${`${when(x.created)}, ${x.open ? "brief" : "done"}, ${x.slug}, ${x.from} to ${x.to}. Open the messages between them`} onClick=${() => store.setUi({ pair: [m.id, x.peer] })}>${row(x)}</button>`
              : html`<div class="mp-msg">${row(x)}</div>`}</li>`)}</ul>`}
          ${internalToggle(store, m.internal, m.internalCount)}
        </section>
        <section class="mp-card" aria-labelledby="mp-files"><h2 id="mp-files">Files changed · ${m.filesTotal}${m.filesBranch ? html` <span class="dim mono">${m.filesBranch}</span>` : null}</h2>
          ${m.files.length === 0 ? html`<p class="dim">none</p>` : html`<ul class="files">${m.files.map(([xy, path]) => html`<li key=${path}><span class="xy mono">${xy}</span> <span class="mono">${path}</span></li>`)}</ul>`}
          ${m.filesTruncated ? html`<p class="dim">showing the first ${m.files.length} of ${m.filesTotal}</p>` : null}
          <h3>Tests${m.head ? html` <span class="dim mono">at ${m.head.slice(0, 9)}</span>` : null}</h3>
          ${m.tests.length === 0 ? html`<p class="dim">no results</p>` : html`<ul class="tests">${m.tests.map(([set, status, passed, failedN]) => html`<li key=${set} class=${`t-${status}`}>${set}: ${status} · ${passed} passed${failedN > 0 ? `, ${failedN} failed` : ""}</li>`)}</ul>`}
        </section>
        <section class="mp-card" aria-labelledby="mp-hist"><h2 id="mp-hist">History${m.history.length ? html` <span class="dim">· ${m.history.length}</span>` : null}</h2>
          ${m.history.length === 0 ? html`<p class="dim">No items held.</p>` : html`<ul class="mp-hist">${m.history.map((i) => html`<li key=${i.key} class=${`st-${i.state}`}><a href=${i.href}><span class="mono">${i.key}</span>${i.title ? ` · ${i.title}` : ""}</a> <span class="chip">${i.label}</span></li>`)}</ul>`}
        </section>
        <section class="mp-card" aria-labelledby="mp-links"><h2 id="mp-links">Links</h2>
          <ul class="mp-links">
            <li><a href=${m.links.conversation}>Open conversation</a></li>
            <li><a href=${m.links.graph}>Show on graph</a></li>
            <li><a href=${m.links.timeline}>Show on timeline</a></li>
            <li><a href=${m.links.board}>Board, filtered to ${m.name}</a></li>
          </ul>
        </section>
        <section class="mp-card" aria-labelledby="mp-act"><h2 id="mp-act">Actions</h2>
          <${MemberActions} store=${store} memberId=${m.id} disabled=${m.gone ? GONE : null} />
        </section>
      </div>
    </article>`;
  }
}

const row = (x) => html`<time class="mono">${when(x.created)}</time><span class=${`kind ${x.open ? "brief" : "response"}`}>${x.open ? "brief" : "done"}</span><span class="slug">${x.slug}</span><span class="route">${x.from} → ${x.to}</span>`;
