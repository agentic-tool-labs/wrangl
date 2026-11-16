import { html, Component } from "./h.js";
import { getAnswerStatus, postAnswer } from "./bridge.js";
import { Live } from "./live-component.js";
import { Block } from "./item-log.js";
import { descriptionBlocks, itemDescId } from "./plans-model.js";
import { Age } from "./ticker.js";
import { ageText, ageOf } from "./store.js";
import { ATTENTION_EMPTY, DIRECTION, DISMISS, FACETS, NO_RUN, OTHER_MAX, SEEN, SEEN_POLL, answerBody, answerText, dismissBody, otherOver, answerCommand, attentionOf, replyState, seenOf, startPick, toggle, defaultAt, letterOf, sameText, listRow, qaEmpty, qaListId, qaRows, questionsId, REPLY_LINES, threadListId, threadOf, classLabel } from "./questions-model.js";

// The two question tabs of a team, over its open pipeline run: Needs your attention (the open questions, oldest first, each
// answerable in place through POST /ui/v1/answer or by the shown `ah msg decision answer` command, and the recently answered) and
// the read-only Q&A log (every question asked and decided, newest first).

const QA_PAGE = 50;
// Only the Answer button answers (F3): Enter in a pick or in Other would submit the form implicitly, and with the default picked
// (L6) that posts an answer nobody chose to send.
const noEnter = (e) => {
  if (e.key === "Enter") {
    e.preventDefault();
  }
};
const since = (ms) => (now) => (ms > 0 ? ageText(ageOf({ as_of: ms }, {}, now)) : "—");
const teamName = (teamId) => teamId.split("/").pop();
const WHEN = new Intl.DateTimeFormat([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
const when = (ms) => (ms > 0 ? WHEN.format(new Date(ms)) : "—");

// Each option with the letter `--choice` takes for it; the chosen one (the default, or the answer given) is marked in words.
const Options = ({ options, chosen, mark }) => (options.length === 0 ? null : html`<ol class="qa-options">
  ${options.map((o, i) => html`<li key=${o.label} class=${i === chosen ? "on" : ""}><span class="qa-letter mono" aria-hidden="true">${letterOf(i)}</span><span class="qa-label">${o.label}</span>${i === chosen ? html`<span class="qa-default">${mark}</span>` : null}</li>`)}
</ol>`);

// The one answer form (spec ah-ui-answer §5), drawn by the Inbox card, a waiting Board card and the Board panel: radios (checkboxes on
// a multi question) for the lettered options and Other, the Answer button and the two Dismiss buttons. Answer asks to confirm the
// answer in words; Confirm locks the form and collapses it to one status line (Sending…, then Recorded) that stays until the slice
// refresh moves the row. Dismiss (§6) is one click, no confirm, the label its reason. A refusal or a failed send unlocks the form with
// the reason. The status line is the one live region, rendered in every phase so each change is announced. Each host gives the
// heading the pick group is named by (`labelledBy`) and an id prefix unique on the page (`uid`); with `team` the
// `ah msg decision answer` line shows too.
export class AnswerForm extends Component {
  // The default option starts picked (L6), so Answer takes it in two presses: Answer, then Confirm.
  state = { pick: startPick(this.props.q), phase: "edit", reply: null, given: "", body: null };

  set = (i) => this.setState(({ pick }) => ({ pick: toggle(this.props.q, pick, i), reply: null }));

  // the host's card marks itself sent once the answer is recorded
  componentDidUpdate(_, prev) {
    if (prev.phase !== this.state.phase) {
      this.props.onPhase?.(this.state.phase);
    }
  }

  componentWillUnmount() {
    this.gone = true;
    clearTimeout(this.timer);
    clearTimeout(this.holdTimer);
    globalThis.document?.removeEventListener?.("visibilitychange", this.onVisible);
  }

  // After Recorded: poll whether the Orchestrator has seen the answer's event, every SEEN_POLL.every while the tab is visible (a
  // hidden tab parks the poll until it shows again), until seen, a stop, an unmount or SEEN_POLL.cap. A 5xx or no reply polls again.
  watch(event, { dir, team }) {
    const end = Date.now() + SEEN_POLL.cap;
    const tick = async () => {
      this.timer = null;
      if (this.gone || Date.now() > end) {
        return;
      }
      if (globalThis.document?.hidden) {
        this.parked = tick;
        return;
      }
      let next;
      try {
        const { status, body } = await getAnswerStatus({ dir, team, event });
        next = seenOf(status, body);
      } catch {
        next = seenOf(0, null);
      }
      if (this.gone) {
        return;
      }
      if (next.kind === "seen") {
        this.setState({ phase: "seen" });
      } else if (next.kind === "wait") {
        this.timer = setTimeout(tick, SEEN_POLL.every);
      } else {
        this.props.onRelease?.();
      }
    };
    this.onVisible = () => {
      if (!globalThis.document.hidden && this.parked) {
        const go = this.parked;
        this.parked = null;
        go();
      }
    };
    globalThis.document?.addEventListener?.("visibilitychange", this.onVisible);
    this.timer = setTimeout(tick, SEEN_POLL.every);
  }

  // Answer: freeze the post body and its words together and ask to confirm them, so the words shown are the words sent.
  ask = (e) => {
    e.preventDefault();
    const { q, teamId, run } = this.props;
    const body = answerBody(teamId, run, q, this.state.pick);
    if (this.state.phase !== "edit" || !body) {
      return;
    }
    this.setState({ phase: "confirm", reply: null, given: answerText(q, this.state.pick), body }, () => this.confirmEl?.focus());
  };

  back = () => this.setState({ phase: "edit" }, () => this.answerEl?.focus());

  // Confirm: one send per confirm.
  send = () => this.state.phase === "confirm" && this.post(this.state.body);

  // Dismiss: one click sends the reason in place of a choice; the status line says what was sent.
  dismiss = (reason) => {
    const { q, teamId, run } = this.props;
    const body = dismissBody(teamId, run, q, reason);
    if (this.state.phase !== "edit" || !body) {
      return;
    }
    this.setState({ given: `Dismissed: ${reason}`, dismissed: true });
    this.post(body);
  };

  // `inflight` is set before any render, so a second press can never post twice.
  post = async (body) => {
    if (!body || this.inflight) {
      return;
    }
    this.inflight = true;
    this.setState({ phase: "sending", reply: null }, () => this.statusEl?.focus());
    let reply;
    try {
      const { status, body: got } = await postAnswer(body);
      reply = replyState(status, got);
    } catch (err) {
      reply = replyState(0, { detail: err.message });
    }
    this.inflight = false;
    this.setState(({ dismissed }) => ({ phase: reply.done ? "recorded" : "edit", reply, dismissed: reply.done && dismissed }));
    if (reply.event) {
      // kept in the list past the slice's drop until SEEN_POLL.hold, or until a stop rule lets it go
      this.props.onHold?.();
      this.holdTimer = setTimeout(() => this.props.onRelease?.(), SEEN_POLL.hold);
      this.watch(reply.event, body);
    }
  };

  render({ q, team = null, teamId, run, onFull = null, uid = q.id, labelledBy }, { pick, phase, reply, given, dismissed }) {
    const kind = q.multi ? "checkbox" : "radio";
    const open = phase === "edit" || phase === "confirm";
    const ready = answerBody(teamId, run, q, pick) !== null;
    const canDismiss = dismissBody(teamId, run, q, DISMISS[0]) !== null;
    const def = defaultAt(q);
    const over = otherOver(pick);
    const name = `qa-pick-${uid}`;
    const shown = dismissed ? given : reply?.answer || given;
    const status = phase === "sending" ? "Sending…" : phase === "seen" ? SEEN : reply?.text ?? "";
    return html`<div class="qa-answer">
    ${!open ? null : html`<form class="qa-answer-form" onSubmit=${this.ask}>
      <fieldset class="qa-pick" aria-labelledby=${labelledBy} aria-describedby=${q.multi ? `qm-${uid}` : undefined} disabled=${phase !== "edit"}>
        ${q.multi ? html`<p id=${`qm-${uid}`} class="qa-multi">Pick one or more</p>` : null}
        ${q.options.map((o, i) => (letterOf(i) ? html`<label key=${o.label} class=${pick.picked.includes(i) ? "qa-opt on" : "qa-opt"}>
          <input type=${kind} name=${name} value=${letterOf(i)} checked=${pick.picked.includes(i)} onChange=${() => this.set(i)} onKeyDown=${noEnter} />
          <span class="qa-letter mono" aria-hidden="true">${letterOf(i)}</span><span class="qa-label">${o.label}</span>${i === def ? html`<span class="qa-default">(default)</span>` : null}
        </label>` : null))}
        ${q.kind === "item" && q.options.length === 0 ? null : html`<label class=${pick.other ? "qa-opt on" : "qa-opt"}>
          <input type=${kind} name=${name} value="other" checked=${pick.other} onChange=${() => this.set("other")} onKeyDown=${noEnter} />
          <span class="qa-letter mono" aria-hidden="true"></span><span class="qa-label">Other</span>
        </label>`}
        ${pick.other ? html`<input class="qa-other" type="text" maxlength=${OTHER_MAX} value=${pick.text} aria-label=${`Other answer to ${q.id}`} placeholder="Your answer"
          aria-invalid=${over ? "true" : undefined} aria-describedby=${over ? `qo-${uid}` : undefined} onKeyDown=${noEnter}
          onInput=${(e) => this.setState({ pick: { ...this.state.pick, text: e.target.value }, reply: null })} />` : null}
        ${over ? html`<p id=${`qo-${uid}`} class="qa-hint">Too long: at most ${OTHER_MAX} bytes. Shorten the answer.</p>` : null}
      </fieldset>
      ${(q.more > 0 || q.cut) && onFull ? html`<button type="button" class="tab qa-full-btn" aria-label=${`${q.more > 0 ? `${q.more} more options` : "Full text"}: open ${q.id} in the Q&A log`} onClick=${() => onFull(q.id)}>${q.more > 0 ? `+${q.more} more` : "Full text"}</button>` : null}
      ${phase === "confirm" ? html`<div class="qa-send qa-confirm">
        <p id=${`qc-${uid}`} class="qa-ask">Send this answer? <span class="qa-given" title=${given}>${given}</span></p>
        <button type="button" class="action primary" aria-describedby=${`qc-${uid}`} ref=${(el) => (this.confirmEl = el)} onClick=${this.send}>Confirm</button>
        <button type="button" class="action" onClick=${this.back}>Back</button>
      </div>` : html`<div class="qa-send">
        <button type="submit" class="action primary" disabled=${!ready} ref=${(el) => (this.answerEl = el)}>Answer</button>
        <span class="qa-dismiss" role="group" aria-label=${`Dismiss ${q.id}`}>${DISMISS.map((r) => html`<button key=${r} type="button" class="action" disabled=${!canDismiss} onClick=${() => this.dismiss(r)}>${r}</button>`)}</span>
      </div>`}
    </form>`}
    <p class=${`qa-status${open ? "" : " qa-progress"}${reply && !reply.done ? " err" : ""}`} role="status" tabindex="-1" ref=${(el) => (this.statusEl = el)}>${open ? status : html`<span class=${phase === "sending" ? "qa-mark spin" : "qa-mark ok"} aria-hidden="true"></span><span class="qa-state">${status}</span> <span class="qa-given" title=${shown}>${shown}</span>${reply?.note ? html` <span class="qa-note">${reply.note}</span>` : null}`}</p>
    ${!team || q.kind === "item" || !open ? null : html`<label class="qa-cmd"><span>Answer with</span><input readonly class="mono" value=${answerCommand(q, team, run, pick)} aria-label=${`Command to answer ${q.id}`} onFocus=${(e) => e.target.select()} /></label>`}
  </div>`;
  }
}

// A waiting item's description under its note, from the item's item-desc: slice (leased while the card is mounted): folded to four lines
// with More when it runs longer, so the card carries the whole question. Nothing is drawn while it loads or when there is none.
// The fold is measured once per text, after it draws; the button shows only when the folded box clips.
class CardDescription extends Live {
  state = { open: false, over: false };

  leaseIds(_, { id }) {
    return [id];
  }

  select(snap, { id }) {
    const text = snap.slices.get(id)?.body?.description;
    return { text: typeof text === "string" && text.trim() !== "" ? text : null };
  }

  measure() {
    const el = this.box;
    const over = !!el && !this.state.open && el.scrollHeight > el.clientHeight + 1;
    if (el && !this.state.open && over !== this.state.over) {
      this.setState({ over });
    }
  }

  componentDidMount() {
    super.componentDidMount();
    this.measure();
  }

  componentDidUpdate() {
    super.componentDidUpdate();
    this.measure();
  }

  draw({ uid }, { text }) {
    if (text === null) {
      return null;
    }
    const { open, over } = this.state;
    return html`<div class="qa-desc-wrap">
      <div id=${`qd-${uid}`} class=${open ? "qa-desc" : "qa-desc folded"} ref=${(el) => (this.box = el)}>${descriptionBlocks(text).map((b) => html`<${Block} b=${b} />`)}</div>
      ${over || open ? html`<button type="button" class="tab qa-desc-more" aria-expanded=${open ? "true" : "false"} aria-controls=${`qd-${uid}`} onClick=${() => this.setState({ open: !open })}>${open ? "Less" : "More"}</button>` : null}
    </div>`;
  }
}

// An open question in the Inbox: its meta line and question over the one form. `onHold`/`onRelease` keep a recorded card drawn until
// seen; the card reads "sent" once the form has collapsed to its status.
class OpenQuestion extends Component {
  state = { sent: false };

  phase = (p) => this.setState({ sent: p === "recorded" || p === "seen" });

  render({ q, team, teamId, run, onFull, onHold, onRelease, store }, { sent }) {
    // the item-desc: slice lives under the team's dir id, the part of `team:<dirId>/<team>` before its last "/"
    const log = itemDescId(teamId.slice("team:".length).replace(/\/[^/]*$/, ""), q.workId);
    return html`<li class=${sent ? "qa-card sent" : "qa-card"}>
  <article aria-labelledby=${`qh-${q.id}`}>
    <p class="qa-meta"><span class=${`qa-class qa-class-${q.cls}`}>${classLabel(q.cls)}</span>${q.kind === "item" ? null : html`<span class="qa-id mono">${q.id}</span>`}<span>${team}</span>${q.asker ? html`<span>asked by ${q.asker}</span>` : null}${q.item && q.item !== q.text ? html`<span class=${q.kind === "item" ? undefined : "mono"} title="Plan item">${q.item}</span>` : null}<span class="qa-age"><${Age} text=${since(q.at)} /></span></p>
    <h3 id=${`qh-${q.id}`} class="qa-header">${q.text}</h3>
    ${log ? html`<${CardDescription} store=${store} uid=${q.id} id=${log} />` : null}
    <${AnswerForm} q=${q} team=${team} teamId=${teamId} run=${run} onFull=${onFull} onHold=${onHold} onRelease=${onRelease} onPhase=${this.phase} labelledBy=${`qh-${q.id}`} />
  </article>
</li>`;
  }
}

const keyOf = (q) => (q.kind === "item" ? `${q.id}@${q.since}` : q.id);
const oldestFirst = (x, y) => (x.at ?? Infinity) - (y.at ?? Infinity);

export class AttentionView extends Live {
  // Answered cards waiting to be seen, by key: drawn even after the slice drops their rows, until each lets itself go.
  state = { kept: new Map() };

  hold = (q) => this.setState(({ kept }) => ({ kept: new Map(kept).set(keyOf(q), q) }));

  release = (q) => this.setState(({ kept }) => {
    if (!kept.has(keyOf(q))) {
      return null;
    }
    const next = new Map(kept);
    next.delete(keyOf(q));
    return { kept: next };
  });

  select(snap, { teamId }) {
    return { v: snap.slices.get(questionsId(teamId))?.version ?? -1 };
  }

  leaseIds(_, { teamId }) {
    return [questionsId(teamId)];
  }

  draw({ store, teamId }) {
    const a = attentionOf(store.state.slices.get(questionsId(teamId))?.body);
    const team = teamName(teamId);
    const held = a ? new Set(a.open.map(keyOf)) : new Set();
    const cards = a ? [...a.open, ...[...this.state.kept.values()].filter((q) => !held.has(keyOf(q)))].sort(oldestFirst) : [];
    return html`<section class="qa" aria-label="Needs your attention">
      <div class="qa-body">
        <div class="qa-main" aria-busy=${a ? "false" : "true"}>
          <h2 class="qa-title">Needs your attention${a && a.count > 0 ? html` <span class="qa-count">${a.count}</span>` : null}</h2>
          ${!a ? html`<p class="dim skeleton">Loading…</p>`
            : cards.length === 0 ? html`<p class="dim qa-none">${a.run ? ATTENTION_EMPTY : NO_RUN}</p>`
            : html`<ol class="qa-cards">${cards.map((q) => html`<${OpenQuestion} key=${keyOf(q)} q=${q} store=${store} team=${team} teamId=${teamId} run=${a.run} onHold=${() => this.hold(q)} onRelease=${() => this.release(q)} onFull=${(id) => store.setUi({ view: "inbox", inboxTab: "qa", qaOpen: id })} />`)}</ol>`}
          ${a && a.hidden > 0 ? html`<p class="dim qa-none">Showing ${a.open.length} of ${a.count}, oldest first. <button class="tab" onClick=${() => store.setUi({ view: "inbox", inboxTab: "qa" })}>All in the Q&A log</button></p>` : null}
        </div>
        <aside class="qa-side" aria-label="Recently answered">
          <h2 class="qa-title">Recently answered</h2>
          ${!a ? null : a.recent.length === 0 ? html`<p class="dim qa-none">None yet</p>` : html`<ul class="qa-recent">${a.recent.map((r) => html`<li key=${r.id}>
            <p class="qa-meta"><span class="qa-id mono">${r.id}</span><span>${r.by ?? DIRECTION[r.dir] ?? ""}</span>${r.question ? html`<span class="qa-badge">question</span>` : null}<span class="qa-age"><${Age} text=${since(r.at)} /></span></p>
            <p class="qa-text">${r.text}</p>
            <p class="qa-answer">${r.dir === "jim" ? `${r.replies} ${r.replies === 1 ? "reply" : "replies"}` : r.answer}</p>
          </li>`)}</ul>`}
        </aside>
      </div>
    </section>`;
  }
}

const Detail = ({ r, onClose, closeRef }) => html`<section class="qa-detail" aria-label=${`Question ${r.id}`} onKeyDown=${(e) => e.key === "Escape" && (e.stopPropagation(), onClose())}>
  <button class="close" ref=${closeRef} onClick=${onClose} aria-label="Close question">×</button>
  <p class="qa-meta"><span class="qa-id mono">${r.id}</span><span>${DIRECTION[r.dir] ?? r.dir}</span></p>
  <h2 class="qa-full">${r.text}</h2>
  ${r.options.length > 0 ? html`<h3>Options</h3><${Options} options=${r.options.map((label) => ({ label }))} chosen=${r.options.findIndex((label) => sameText(label, r.answer))} mark="answer" />` : null}
  <dl>
    ${r.item ? html`<dt>item</dt><dd class="mono">${r.item}</dd>` : null}
    <dt>answer</dt><dd>${r.answer ?? "open"}</dd>
    <dt>by</dt><dd>${r.by ?? "—"}</dd>
    <dt>asked</dt><dd class="num">${when(r.at)}</dd>
    ${r.answeredAt ? html`<dt>answered</dt><dd class="num">${when(r.answeredAt)}</dd>` : null}
    ${r.run ? html`<dt>run</dt><dd class="mono">${r.run}</dd>` : null}
    ${r.cls ? html`<dt>class</dt><dd class="mono">${r.cls}</dd>` : null}
  </dl>
  ${r.rationale ? html`<h3>Rationale</h3><p class="qa-full">${r.rationale}</p>` : null}
</section>`;

// A reply past REPLY_LINES lines shows its head, and the rest behind "show all".
const Reply = ({ text }) => {
  const lines = text.split("\n");
  return lines.length <= REPLY_LINES ? html`<p class="qa-full">${text}</p>`
    : html`<p class="qa-full">${lines.slice(0, REPLY_LINES).join("\n")}</p><details class="qa-more-reply"><summary>show all</summary><p class="qa-full">${lines.slice(REPLY_LINES).join("\n")}</p></details>`;
};

// The web panel draws no Final reply or More from this turn yet (a later item), so the own turn's text stays a later reply here,
// as it was before threadOf split it out; when the reply already is that turn's final, nothing is added, as before.
const webThread = (t) => ({ ...t, later: [...(t.replyFinal || !(t.more ?? t.final) ? [] : [{ at: t.moreAt ?? t.finalAt, text: t.more ?? t.final }]), ...t.later] });

// A question-log thread: the prompt (or the terminal dialog and its answer), the reply of its own turn, then the later replies folded.
// A row whose replies were cut to heads reads its thread whole once the panel opens; until then (or if that fails) the heads show.
class Thread extends Component {
  state = { full: null };

  componentDidMount() {
    const { r, store, teamId } = this.props;
    if (r.cut && store.fetchList) {
      store.fetchList(threadListId(teamId, r.id), {}).then((doc) => this.setState({ full: webThread(threadOf(doc)) }), () => {});
    }
  }

  render({ r, onClose, closeRef }, { full }) {
    return html`<${ThreadPanel} r=${full ? { ...r, ...full } : r} onClose=${onClose} closeRef=${closeRef} />`;
  }
}

const ThreadPanel = ({ r, onClose, closeRef }) => html`<section class="qa-detail" aria-label=${`Thread ${r.id}`} onKeyDown=${(e) => e.key === "Escape" && (e.stopPropagation(), onClose())}>
  <button class="close" ref=${closeRef} onClick=${onClose} aria-label="Close thread">×</button>
  <p class="qa-meta"><span>${DIRECTION[r.dir] ?? r.dir}</span>${r.question ? html`<span class="qa-badge">question</span>` : null}<span class="num">${when(r.at)}</span></p>
  ${r.item ? html`<p class="qa-meta mono">${r.item}</p>` : null}
  <h2 class="qa-full">${r.text}</h2>
  ${r.dir === "terminal" ? html`<h3>Answer</h3><p class="qa-full">${r.answer ?? "—"}</p>`
    : r.reply ? html`<h3>Reply <span class="dim num">${when(r.replyAt)}</span></h3><${Reply} text=${r.reply} />` : html`<p class="dim">No reply in that turn.</p>`}
  ${r.later.length > 0 ? html`<details class="qa-later"><summary>${r.later.length} later ${r.later.length === 1 ? "reply" : "replies"}</summary>
    ${r.later.map((l, i) => html`<div key=${i}><h3 class="dim num">${when(l.at)}</h3><${Reply} text=${l.text} /></div>`)}
  </details>` : null}
</section>`;

const LOG_DIRS = new Set(["jim", "terminal", "unprompted"]);

export class QAView extends Live {
  state = { facet: "all", query: "", open: null };

  select(snap, { teamId }) {
    const list = snap.lists.get(qaListId(teamId));
    return { windows: list?.windows ?? null, loading: list?.loading === true };
  }

  // The Attention tab sends a capped question here whole: it opens on arrival, once.
  constructor(props) {
    super(props);
    this.state = { ...this.state, open: props.store.state.ui.qaOpen ?? null };
  }

  componentDidMount() {
    super.componentDidMount();
    if (this.state.open) {
      this.props.store.setUi({ qaOpen: null });
    }
    this.load(null);
  }

  componentDidUpdate(prev) {
    super.componentDidUpdate();
    if (prev.teamId !== this.props.teamId) {
      this.setState({ open: null });
      this.load(null);
    }
  }

  load(after) {
    this.failed = false;
    this.props.store.page(qaListId(this.props.teamId), { limit: QA_PAGE, ...(after ? { after } : {}) }).catch(() => {
      this.failed = true;
      this.forceUpdate();
    });
  }

  // Escape or × closes the panel and gives focus back to the row that opened it.
  close = () => this.setState({ open: null }, () => this.opener?.focus());

  pick = (e, id) => {
    this.opener = e.currentTarget;
    // focus goes into the opened panel, so Escape (handled there) closes it
    this.setState({ open: id === this.state.open ? null : id }, () => this.state.open && this.closeEl?.focus());
  };

  draw(_, s) {
    const { facet, query, open } = this.state;
    const all = (s.windows ?? []).flatMap((w) => w.items).map(listRow);
    const rows = qaRows(all, facet, query);
    const next = s.windows?.at(-1)?.next ?? null;
    const empty = this.failed ? "Questions unavailable." : qaEmpty(s.windows !== null, all.length, rows.length);
    const shown = open ? all.find((r) => r.id === open) : null;
    return html`<section class="qa" aria-label="Q&A log">
      <div class="qa-bar">
        <input data-search type="search" placeholder="Filter questions" aria-label="Filter questions" value=${query} onInput=${(e) => this.setState({ query: e.target.value })} />
        <span class="qa-facets" role="group" aria-label="Show">${FACETS.map(([k, label]) => html`<button key=${k} class=${facet === k ? "tab on" : "tab"} aria-pressed=${facet === k} onClick=${() => this.setState({ facet: k })}>${label}</button>`)}</span>
        <span class="qa-tally" role="status">${s.windows === null ? "" : `${rows.length} of ${all.length}${next ? "+" : ""}`}</span>
      </div>
      <div class="qa-body">
        <div class="qa-main">
          ${empty ? html`<p class="dim qa-none">${empty}</p>` : html`<table class="qa-table">
            <thead><tr><th scope="col">Id</th><th scope="col">Direction</th><th scope="col">Question</th><th scope="col">Answer</th><th scope="col">Answered by</th><th scope="col">When</th></tr></thead>
            <tbody>${rows.map((r) => html`<tr key=${r.id} class=${r.id === open ? "on" : ""}>
              <td class="mono qa-id" title=${r.id}>${r.id}</td>
              <td>${DIRECTION[r.dir] ?? r.dir}${r.question ? html` <span class="qa-badge">question</span>` : null}</td>
              <td class="qa-q"><button class="qa-open" data-qa=${r.id} aria-pressed=${r.id === open} title=${r.text} onClick=${(e) => this.pick(e, r.id)}>${r.text}</button></td>
              <td class="qa-a" title=${r.answer ?? r.reply ?? ""}>${r.answer ?? r.reply ?? html`<span class="dim">${r.dir === "jim" ? "no reply" : "open"}</span>`}</td>
              <td>${r.by ?? "—"}</td>
              <td class="num">${when(r.answeredAt || r.at)}</td>
            </tr>`)}</tbody>
          </table>`}
          ${next && !s.loading ? html`<p class="qa-more"><button class="tab" onClick=${() => this.load(next)}>load older</button></p>` : null}
        </div>
        ${shown ? html`<${LOG_DIRS.has(shown.dir) ? Thread : Detail} key=${shown.id} r=${shown} store=${this.props.store} teamId=${this.props.teamId} onClose=${this.close} closeRef=${(el) => (this.closeEl = el)} />` : null}
      </div>
    </section>`;
  }
}
