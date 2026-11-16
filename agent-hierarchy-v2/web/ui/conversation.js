import { html } from "./h.js";
import { endedText } from "./bridge.js";
import { Live } from "./live-component.js";
import { NO_CONTEXT, firstTeam, memberIds, memberOf, openBriefsOf, stateWord } from "./select.js";
import { FALLBACK_VIEW, AT_END_PX, OLDER_PX, HEIGHT_EPSILON, MAX_CORRECTIONS, estimateOf, offsetsOf, windowAt, anchorAt, resolveAnchor } from "./transcript-window.js";
import { MAX_WINDOWS, ageOf, ageText } from "./store.js";
import { Age } from "./ticker.js";
import { memberHref } from "./deeplink.js";
import { idsOf, transcriptRows } from "./transcript-rows.js";
import { internalToggle, showInternal } from "./msg-filter.js";

export { idsOf, transcriptRows };

// The Conversation view: a member rail, the live transcript of the chosen member, and an aside of its files and tests.
// Entries come from the server's transcript list (newest first, paged by byte offset); everything here is read from loaded windows.

export const FILTERS = [
  ["all", "All"],
  ["text", "Text"],
  ["tools", "Tools"],
  ["errors", "Errors"],
];
export const PAGE = 60;
export const LINE_CAP = 400;
const DIFF_TOOLS = new Set(["Edit", "MultiEdit", "Write"]);

const KINDS = { text: ["turn", "brief", "text"], tools: ["tool", "result"] };

// The rows a filter and a search keep. Search runs over the rows given, i.e. the loaded windows only.
export function filterRows(rows, filter, query, internal = true) {
  const q = query.trim().toLowerCase();
  return rows.filter((r) => {
    if (!internal && r.internal) {
      return false;
    }
    const byFilter = filter === "errors" ? r.error === true : filter === "all" || (KINDS[filter] ?? []).includes(r.kind);
    return byFilter && (q === "" || r.label.toLowerCase().includes(q));
  });
}

// Input plus output tokens, and seconds, per turn over the loaded entries; a turn starts at an entry the server marks `turn_start`.
// Seconds average only the turns whose duration the server could read. A message split over several lines counts once.
export function tokensPerTurn(entries) {
  const counted = new Set();
  let turns = 0;
  let total = 0;
  let timed = 0;
  let ms = 0;
  for (const e of entries) {
    if (e.turn_start === true) {
      turns += 1;
      if (e.turn_ms != null) {
        timed += 1;
        ms += e.turn_ms;
      }
    }
    if (!counted.has(e.message_id)) {
      total += (e.usage?.input_tokens ?? 0) + (e.usage?.output_tokens ?? 0);
    }
    if (e.message_id) {
      counted.add(e.message_id);
    }
  }
  return { turns, average: turns === 0 ? null : Math.round(total / turns), seconds: timed === 0 ? null : Math.round(ms / timed / 1000) };
}

// The loaded entries of a transcript list, oldest first: the held windows run newest first, each window newest first.
export function loadedEntries(snap, listId) {
  const windows = snap.lists.get(listId)?.windows ?? [];
  return windows.flatMap((w) => w.items).reverse();
}

// A conversation has ended when a poll reported its header removed; a header merely absent (a reload, a lease not yet served) has not.
export const isEnded = (snap, hid) => hid !== null && snap.gone?.has(hid) === true;

// Drops the expanded bodies whose entry is no longer in a held window.
export function pruneCache(cache, entries) {
  const held = new Set(entries.map((e) => e.offset));
  for (const offset of cache.keys()) {
    if (!held.has(offset)) {
      cache.delete(offset);
    }
  }
}

class MemberRail extends Live {
  select(snap) {
    const team = firstTeam(snap.slices, snap.ui);
    const ids = memberIds(team);
    return { people: JSON.stringify(ids.map((id) => { const m = memberOf(snap.slices, team, id); const c = m.ctx; return [id, m.name, m.state, m.slug ?? "", m.role, c.text, c.title, c.over]; })), selected: ids.includes(snap.ui.selected) ? snap.ui.selected : ids[0] ?? null };
  }

  draw({ store }, { people, selected }) {
    return html`<nav class="member-rail" aria-label="Members"><ul>
      ${JSON.parse(people).map(([id, name, state, slug, role, ctx, ctxTitle, over]) => {
        return html`<li key=${id} data-role=${role || undefined} data-state=${state} class=${`rail-row st-${state}${selected === id ? " selected" : ""}`}><button class="rail-hit" aria-pressed=${selected === id} aria-label=${`Show ${name}'s conversation, ${state}`} onClick=${() => store.setUi({ selected: id })}></button><span class="rail-body"><span class="dot"></span><span class="n"><a href=${memberHref(id)}>${name}</a></span><span class="s">${state !== "working" && (slug || state !== "idle") ? `${state} · ` : ""}${slug || "idle"}</span><span class=${over ? "ctx over" : "ctx"} title=${`context used · ${ctxTitle}`}><span class="sr">context used </span>${ctx}</span></span></li>`;
      })}
    </ul></nav>`;
  }
}

const rowClass = (r, open) => `tr-row k-${r.kind}${r.internal ? " internal" : ""}${r.error ? " err" : ""}${open ? " open" : ""}`;

function expandedText(block) {
  if (block.kind === "text" || block.kind === "thinking") return block.text ?? "";
  if (block.kind === "tool_use") return JSON.stringify(block.input, null, 2) ?? "";
  if (block.kind === "tool_result") {
    if (typeof block.content === "string") return block.content;
    return Array.isArray(block.content)
      ? block.content.map((item) => item.type === "text" ? item.text : `[${item.type}]`).join("\n")
      : "";
  }
  return "";
}

// Reads a size from the DOM: `kind` is "row" for a list item and "view" for the scroll container.
const domMeasure = (el, kind) => (kind === "view" ? el.clientHeight : el.offsetHeight);

const lines = (text) => (text === "" ? [] : text.split("\n"));

// A row label without the leading tool name.
export function summaryOf(label, name) {
  if (label === name) return "";
  return label.startsWith(`${name}: `) || label.startsWith(`${name} `) ? label.slice(name.length + 1).replace(/^ /, "") : label;
}

export class TranscriptPane extends Live {
  state = { following: true, top: 0, unread: 0, open: new Set(), all: new Set(), rev: 0 };
  pending = new Set();
  generation = 0;
  lastNewest = null;
  busy = false;
  cache = new Map();
  heights = new Map();
  anchor = null;
  layout = null;
  corrections = 0;
  correcting = false;
  mounted = [0, 0];

  get measure() {
    return this.props.measure ?? domMeasure;
  }

  viewHeight() {
    return (this.el && this.measure(this.el, "view")) || FALLBACK_VIEW;
  }

  select(snap, { memberId }) {
    const { team, hid, lid } = idsOf(snap, memberId);
    const mem = team ? memberOf(snap.slices, team, memberId) : null;
    const who = snap.slices.get(memberId)?.body;
    const header = hid ? snap.slices.get(hid)?.body : undefined;
    const entries = lid ? loadedEntries(snap, lid) : [];
    const filter = snap.ui.convFilter ?? "all";
    const query = snap.ui.convQuery ?? "";
    const all = transcriptRows(entries);
    const internal = showInternal(snap.ui);
    const rows = filterRows(all, filter, query, internal);
    this.all = all;
    this.rows = rows;
    this.entries = entries;
    const windows = lid ? snap.lists.get(lid)?.windows ?? [] : [];
    return {
      hid,
      lid,
      who: who?.name ?? "",
      state: mem?.state ?? "gone",
      running: mem?.running ?? false,
      tool: mem?.tool ?? null,
      branch: mem?.branch ?? null,
      workRoot: mem?.workRoot ?? null,
      errors: all.filter((r) => r.error).length,
      internal,
      internalCount: all.filter((r) => r.internal).length,
      lastAt: (who?.activity ? snap.slices.get(who.activity)?.as_of : 0) ?? 0,
      ended: isEnded(snap, hid),
      pathOk: header ? header.path_ok : null,
      live: snap.ui.liveNonce ?? 0,
      newest: header?.newest_offset ?? -1,
      key: rows.map((r) => `${r.key}${r.done ? 1 : 0}${r.error ? 1 : 0}`).join(","),
      filter,
      query,
      turns: all.filter((r) => (r.kind === "turn" || r.kind === "brief") && !r.internal).length,
      shown: entries.length,
      more: windows.at(-1)?.next != null,
    };
  }

  componentDidMount() {
    super.componentDidMount();
    this.liveSeen = this.sel.live;
    this.attach();
    this.settleLayout();
    const Observer = this.props.resizeObserver ?? globalThis.ResizeObserver;
    if (typeof Observer === "function" && this.el) {
      let width = null;
      this.watch = new Observer((entries) => {
        const next = entries.at(-1)?.contentRect?.width;
        if (width !== null && next !== width) {
          this.heights.clear();
          this.corrections = 0;
          this.setState({ rev: this.state.rev + 1 });
        }
        width = next;
      });
      this.watch.observe(this.el);
    }
  }

  componentWillUnmount() {
    super.componentWillUnmount();
    this.unmounted = true;
    this.watch?.disconnect();
    this.props.store.lease(this.owner, []);
  }

  // Each pane holds its own lease, so a pane replaced by the next member's cannot release the lease that replaced it.
  get owner() {
    return `transcript:${this.props.memberId}`;
  }

  componentDidUpdate(prev) {
    // A member whose session was not known at mount has no header id yet; the pane leases it once it is known.
    if (prev.memberId !== this.props.memberId || this.sel.hid !== this.attached) {
      this.attach();
      return;
    }
    const s = this.sel;
    if (s.live !== this.liveSeen) {
      this.liveSeen = s.live;
      this.jump();
    }
    if (this.wantLive) {
      this.wantLive = false;
      this.jump();
    }
    this.pruneCache();
    this.fillOpen();
    if (s.pathOk && s.newest !== this.lastNewest) {
      this.lastNewest = s.newest;
      this.state.following ? this.loadNewest() : this.loadNewer();
    }
    this.settleLayout();
  }

  // After a draw: measures the mounted rows, asks for a correction draw when a height moved, and puts the scroll position
  // where it belongs, at the end while following and at the anchor row while paused.
  settleLayout() {
    const list = this.el?.children[0];
    if (!list || !this.layout) {
      return;
    }
    if (this.correcting) {
      this.correcting = false;
    } else {
      this.corrections = 0;
    }
    const [from] = this.mounted;
    let moved = false;
    Array.from(list.children).forEach((li, i) => {
      const row = this.rows[from + i];
      const h = row ? this.measure(li, "row", row.key) : 0;
      if (h > 0 && Math.abs(h - (this.heights.get(row.key) ?? estimateOf(row))) > HEIGHT_EPSILON) {
        this.heights.set(row.key, h);
        moved = true;
      }
    });
    if (moved && this.corrections < MAX_CORRECTIONS) {
      this.corrections += 1;
      this.correcting = true;
      this.setState({ rev: this.state.rev + 1 });
      return;
    }
    const { offsets, top } = this.layout;
    const total = offsets[offsets.length - 1];
    if (this.state.following) {
      this.el.scrollTop = total;
    } else if (this.anchor && top !== null && Math.abs(this.el.scrollTop - top) > HEIGHT_EPSILON) {
      this.el.scrollTop = top;
      this.state.top = top;
    }
    if (this.heights.size > 2 * this.rows.length + 64) {
      const keep = new Set(this.rows.map((r) => r.key));
      this.heights.forEach((_, k) => keep.has(k) || this.heights.delete(k));
    }
  }

  // An expanded body lives only while its entry is in a held window.
  pruneCache() {
    pruneCache(this.cache, this.entries);
  }

  // A new member: its transcript is leased in place of the old one, and the view starts at the live end.
  attach() {
    const s = this.sel;
    this.attached = s.hid;
    this.lastNewest = null;
    this.cache.clear();
    this.pending.clear();
    this.generation += 1;
    this.heights.clear();
    this.anchor = null;
    this.setState({ following: true, top: 0, unread: 0, unreadMore: false, open: new Set(), all: new Set(), loadingOlder: false });
    this.props.store.lease(this.owner, s.hid ? [s.hid, s.lid] : []);
  }

  async loadNewest() {
    await this.fetch(() => this.props.store.page(this.sel.lid, { limit: PAGE }));
    this.setState({ unread: 0 });
  }

  // While paused: one `before` page at most, and none once the store holds its capacity of windows, since another would
  // evict the ones the reader is looking at. Anything beyond what is held is counted from the header and shown as "N+".
  async loadNewer() {
    const { store } = this.props;
    const held = store.state.lists.get(this.sel.lid)?.windows ?? [];
    if (held.length > 0 && held.length < MAX_WINDOWS) {
      await this.fetch(() => store.page(this.sel.lid, { before: held[0].prev, limit: PAGE }));
    }
    const seen = this.pausedAt ?? -1;
    const newest = this.entries.at(-1)?.offset ?? -1;
    this.setState({ unread: this.entries.filter((e) => e.offset > seen).length, unreadMore: this.sel.newest > newest });
  }

  async loadOlder() {
    const last = this.props.store.state.lists.get(this.sel.lid)?.windows.at(-1);
    if (!last?.next || this.busy) {
      return;
    }
    this.setState({ loadingOlder: true });
    try {
      await this.fetch(() => this.props.store.page(this.sel.lid, { after: last.next, limit: PAGE }));
    } finally {
      if (!this.unmounted) {
        this.setState({ loadingOlder: false });
      }
    }
  }

  // The newest end stays where it is: following stops, with the row at the top of the view kept as the anchor.
  pause = () => {
    const top = this.el?.scrollTop ?? 0;
    this.anchor = anchorAt(this.rows, offsetsOf(this.rows, this.heights), top);
    this.pausedAt = this.entries.at(-1)?.offset ?? null;
    this.setState({ following: false, top });
  };

  async fetch(read) {
    if (this.busy) {
      this.missed = true;
      return;
    }
    if (!this.sel.lid) {
      return;
    }
    this.busy = true;
    try {
      const got = await read();
      if (this.state.failure) {
        this.setState({ failure: null });
      }
      return got;
    } catch (e) {
      // shown in the pane; the next header change retries
      this.setState({ failure: e?.constructor?.name === "Ended" ? endedText(e.message) : e instanceof Error ? e.message || "failed" : String(e) });
    } finally {
      this.busy = false;
      if (this.missed) {
        this.missed = false;
        this.lastNewest = null;
        this.setState({ rev: this.state.rev + 1 });
      }
    }
  }

  onScroll = (e) => {
    const top = e.currentTarget.scrollTop;
    const offsets = offsetsOf(this.rows, this.heights);
    const atEnd = top >= offsets[offsets.length - 1] - this.viewHeight() - AT_END_PX;
    this.anchor = atEnd ? null : anchorAt(this.rows, offsets, top);
    if (atEnd && !this.state.following) {
      this.pausedAt = null;
      this.setState({ following: true, top, unread: 0, unreadMore: false });
    } else if (!atEnd && this.state.following) {
      this.pausedAt = this.entries.at(-1)?.offset ?? null;
      this.setState({ following: false, top });
    } else if (!this.state.following) {
      this.setState({ top });
      if (top < OLDER_PX && this.sel.more) {
        this.loadOlder();
      }
    }
  };

  jump = () => {
    this.pausedAt = null;
    this.setState({ following: true, unread: 0, unreadMore: false });
    this.loadNewest();
  };

  // The entry offsets a row's body is made of: the call's input and its result's content.
  offsetsFor(r) {
    if (r.kind === "tool") {
      return [r.offset, ...(r.resultOffset == null ? [] : [r.resultOffset])];
    }
    return ["result", "text", "turn", "brief"].includes(r.kind) ? [r.offset] : [];
  }

  // One fetch per offset, cached for as long as its entry is held; a failure is cached as null and not retried.
  ensure(offset) {
    if (this.cache.has(offset) || this.pending.has(offset)) {
      return;
    }
    const generation = this.generation;
    this.pending.add(offset);
    this.props.store.expand(this.sel.lid, offset).catch(() => null).then((body) => {
      if (generation === this.generation) {
        this.pending.delete(offset);
        this.cache.set(offset, body);
        this.setState({ rev: this.state.rev + 1 });
      }
    });
  }

  // Fetches what the open rows still lack, e.g. the result of a call that was running when it was opened.
  fillOpen() {
    for (const key of this.state.open) {
      const row = this.rows.find((r) => r.key === key);
      row && this.offsetsFor(row).forEach((o) => this.ensure(o));
    }
  }

  toggle = (r) => {
    const open = new Set(this.state.open);
    open.delete(r.key) || open.add(r.key);
    this.setState({ open });
  };

  showAll = (key) => this.setState({ all: new Set(this.state.all).add(key) });

  // A cached entry: its blocks, or why there are none.
  bodyOf(offset) {
    if (!this.cache.has(offset)) {
      return { note: "Loading…" };
    }
    const body = this.cache.get(offset);
    if (body === null) {
      return { note: "body unavailable" };
    }
    return body.truncated === true && (body.blocks ?? []).length === 0 ? { note: "entry too large to expand" } : { blocks: body.blocks ?? [] };
  }

  // A pre of at most LINE_CAP lines and, past it, the control that shows the rest.
  block(r, part, all, cls, content) {
    const id = `${r.key}#${part}`;
    const shown = this.state.all.has(id) ? all : all.slice(0, LINE_CAP);
    return [
      html`<pre key=${part} class=${cls}>${content(shown)}</pre>`,
      shown.length < all.length ? html`<button key=${`${part}-all`} class="link show-all" onClick=${() => this.showAll(id)}>Show all ${all.length} lines</button>` : null,
    ];
  }

  toolBody(r) {
    const parts = [];
    const notes = new Set();
    if (r.kind === "tool") {
      const call = this.bodyOf(r.offset);
      const used = call.blocks?.find((b) => b.kind === "tool_use" && b.id === r.id);
      const input = used?.input;
      const changed = used?.diff ?? [];
      if (call.note) {
        notes.add(call.note);
      } else if (!DIFF_TOOLS.has(r.name) && input !== undefined) {
        const shown = r.name === "Bash" && typeof input.command === "string" ? `$ ${input.command}` : JSON.stringify(input, null, 2);
        parts.push(...this.block(r, "in", lines(shown), "tr-block in", (ls) => ls.join("\n")));
      }
      !call.note && changed.length > 0 && parts.push(...this.block(r, "diff", changed, "tr-block diff", (ls) => ls.map((l, i) => html`<span key=${i} class=${`ln ${l.kind}`}>${`${l.text}\n`}</span>`)));
    }
    const at = r.kind === "result" ? r.offset : r.resultOffset;
    if (at != null) {
      const out = this.bodyOf(at);
      const block = out.blocks?.find((b) => b.kind === "tool_result" && b.tool_use_id === r.id);
      out.note ? notes.add(out.note) : block && parts.push(...this.block(r, "out", lines(expandedText(block)), `tr-block out${block.is_error === true ? " err" : ""}`, (ls) => ls.join("\n")));
    }
    return html`<div class="tool-body">${parts}${[...notes].map((n) => html`<p key=${n} class="dim">${n}</p>`)}</div>`;
  }

  toolRow(r, open) {
    const stat = r.stat;
    return html`<li key=${r.key} class=${rowClass(r, open)}>
      <button class="tool-head" aria-expanded=${open} aria-label=${`${open ? "Collapse" : "Expand"} ${r.name} call`} onClick=${() => this.toggle(r)}>
        <span class="chev" aria-hidden="true">▸</span>
        <span class="tool-name">${r.name}</span>
        <span class="tool-summary">${summaryOf(r.label, r.name)}</span>
        ${r.kind === "tool" ? html`<span class=${`chip res${r.error ? " err" : r.done ? " ok" : " run"}`}>${r.done ? (r.error ? "error" : "ok") : "running"}${r.lines != null ? ` · ${r.lines} lines` : ""}</span>` : null}
        ${stat ? html`<span class="tool-stat"><span class="add">+${stat.add}</span> <span class="del">−${stat.del}</span></span>` : null}
      </button>
      ${open ? this.toolBody(r) : null}
    </li>`;
  }

  // The text of a cut row: the full block once it is open and loaded, else the excerpt; and under it, why the full block
  // is not shown yet, or the control that opens it.
  fullText(r, open) {
    const body = open ? this.bodyOf(r.offset) : {};
    const more = body.note ? html`<p class="dim note">${body.note}</p>` : r.truncated && !open ? html`<button class="link show-full" onClick=${() => this.toggle(r)}>show full text</button>` : null;
    return [body.blocks?.[r.index]?.text ?? r.label, more];
  }

  textRow(r, open) {
    const [text, more] = this.fullText(r, open);
    return html`<li key=${r.key} class=${rowClass(r, open)}>
      <p class="prose">${text}</p>
      ${more}
    </li>`;
  }

  turnRow(r, open) {
    const [text, more] = this.fullText(r, open);
    return html`<li key=${r.key} class=${rowClass(r, open)}>
      ${r.internal ? null : html`<span class="turn-no">turn ${r.turn}${r.seconds == null ? "" : ` · ${r.seconds}s`}</span>`}
      <span class="label">${text}</span>
      ${more}
    </li>`;
  }

  draw({ store }, s) {
    const { following, top, unread, open } = this.state;
    const rows = this.rows;
    const view = this.viewHeight();
    const offsets = offsetsOf(rows, this.heights);
    const total = offsets[rows.length];
    const found = following ? null : resolveAnchor(this.all, rows, offsets, this.anchor);
    if (found === "live") {
      this.wantLive = true;
    } else if (found) {
      this.anchor = found.anchor;
    }
    const anchored = found && found !== "live" ? found.top : null;
    const at = following ? Math.max(0, total - view) : anchored ?? top;
    const [from, to] = windowAt(offsets, at, view);
    this.layout = { offsets, top: anchored };
    this.mounted = [from, to];
    const plain = !s.hid && s.shown === 0 ? html`${s.who || "This member"} has no live session.${s.lastAt > 0 ? html` It was last active <${Age} text=${(now) => ageText(ageOf({ as_of: s.lastAt }, {}, now))} />.` : ""} Its conversation appears here when it next starts a turn.` : s.pathOk === false ? "The transcript is not available for this member." : this.state.failure ? `The conversation could not be read: ${this.state.failure}` : s.pathOk === null && s.shown === 0 ? "Loading…" : null;
    if (plain) {
      return html`<section class="transcript" aria-label="Transcript"><p class="dim">${plain}</p></section>`;
    }
    return html`<section class="transcript" aria-label="Transcript">
      <div class="cv-head"><b>${s.who}</b><span class=${`cv-state st-${s.state}`}>${s.state === "working" ? "◐" : "○"} ${stateWord(s.state, s.running)}${s.tool ? ` · ${s.tool}` : ""}</span>${s.workRoot || s.branch ? html`<span class="mono dim">${[s.workRoot, s.branch].filter(Boolean).join(" · ")}</span>` : null}<span class="grow"></span>${s.ended ? html`<span class="chip ended">conversation ended</span>` : null}<span class="dim">${s.turns} turns${s.lastAt > 0 ? html` · <${Age} text=${(now) => ageText(ageOf({ as_of: s.lastAt }, {}, now))} />` : null}</span></div>
      <div class="cv-bar">
        <span class="filters seg" role="group" aria-label="Show">${FILTERS.map(([k, label]) => html`<button key=${k} class=${s.filter === k ? "tab on" : "tab"} aria-pressed=${s.filter === k} onClick=${() => store.setUi({ convFilter: k })}>${label}${k === "errors" && s.errors > 0 ? html` <span class="err-count">${s.errors}</span>` : null}</button>`)}</span>
        ${internalToggle(store, s.internal, s.internalCount)}
        <label class="cv-search"><span aria-hidden="true">⌕</span><input class="search" data-search type="search" placeholder="Search this conversation  /" aria-label="Search this conversation" value=${s.query} onInput=${(e) => store.setUi({ convQuery: e.currentTarget.value })} /></label>
        <span class="chip scope">searching ${s.turns} loaded turns${s.more ? html` · <button class="link" onClick=${() => this.loadOlder()}>load older</button>` : null}</span>
        <label class="cv-follow"><input type="checkbox" checked=${following} onChange=${(e) => (e.currentTarget.checked ? this.jump() : this.pause())} />Follow</label>
      </div>
      <div class="tr-scroll" onScroll=${this.onScroll} ref=${(el) => (this.el = el)}>
        <ul class="tr-list" style=${{ paddingTop: `${offsets[from]}px`, paddingBottom: `${total - offsets[to]}px` }}>
          ${rows.slice(from, to).map((r) => (r.kind === "tool" || r.kind === "result" ? this.toolRow(r, open.has(r.key)) : r.kind === "text" ? this.textRow(r, open.has(r.key)) : r.kind === "turn" || r.kind === "brief" ? this.turnRow(r, open.has(r.key)) : html`<li key=${r.key} class=${rowClass(r, open.has(r.key))}>
            <span class="label">${r.label}${r.kind === "thinking" ? html`<span class="thinking-dots" aria-hidden="true"><i></i><i></i><i></i></span>` : null}</span>
          </li>`))}
        </ul>
      </div>
      ${s.more && this.state.loadingOlder ? html`<div class="cv-older" role="status"><i class="spin" aria-hidden="true"></i>Loading older turns…</div>` : null}
      ${following ? null : html`<button class="cv-jump jump" onClick=${this.jump}>↓ Jump to live${unread > 0 ? ` · ${unread}${this.state.unreadMore ? "+" : ""} new` : ""}</button>`}
    </section>`;
  }
}

class ContextAside extends Live {
  select(snap, { memberId }) {
    const team = firstTeam(snap.slices, snap.ui);
    const m = team ? memberOf(snap.slices, team, memberId) : null;
    const { hid, lid } = idsOf(snap, memberId);
    const files = team && m ? snap.slices.get(`files:${team.body.dir_id}/${team.body.name}/${m.name}`)?.body : undefined;
    const tests = files?.head ? snap.slices.get(`tests:${files.head}`)?.body : undefined;
    const turnUsage = hid ? snap.slices.get(hid)?.body.turn_usage : undefined;
    const per = tokensPerTurn(lid ? loadedEntries(snap, lid) : []);
    const ctx = m ? m.ctx : NO_CONTEXT;
    const item = m?.slug ? team?.body.pipeline?.items?.find((i) => i.slug === m.slug) : null;
    const brief = team && m ? Date.parse(openBriefsOf(snap.slices, team, memberId)[0]?.body.created ?? "") || 0 : 0;
    this.files = files?.files ?? [];
    this.tests = tests?.sets ?? [];
    return {
      name: m?.name ?? "",
      tool: m?.tool ?? null,
      slug: m?.slug ?? null,
      round: item && Number.isFinite(item.rounds) ? `${item.rounds} of ${item.round_cap ?? 3}` : null,
      brief,
      model: m?.model ?? null,
      ctx: ctx.text,
      ctxTitle: ctx.title,
      ctxOver: ctx.over,
      ctxAt: ctx.at ?? 0,
      session: snap.slices.get(memberId)?.body.session ?? null,
      workRoot: m?.workRoot ?? null,
      filesId: team && m ? `files:${team.body.dir_id}/${team.body.name}/${m.name}` : null,
      head: files?.head ?? null,
      branch: files?.branch ?? null,
      filesTotal: files?.files_total ?? 0,
      truncated: files?.truncated ?? false,
      filesKey: this.files.map((f) => `${f.xy}${f.path}`).join("\n"),
      testsKey: this.tests.map((t) => `${t.set}|${t.status}|${t.counts.passed}|${t.counts.failed}`).join("\n"),
      live: turnUsage ? turnUsage.input_tokens + turnUsage.output_tokens : null,
      turns: per.turns,
      average: per.average,
      seconds: per.seconds,
    };
  }

  componentDidMount() {
    super.componentDidMount();
    this.lease();
  }

  componentWillUnmount() {
    super.componentWillUnmount();
    this.props.store.lease(`aside:${this.props.memberId}`, []);
  }

  componentDidUpdate() {
    this.lease();
  }

  lease() {
    const s = this.sel;
    this.props.store.lease(`aside:${this.props.memberId}`, s.filesId ? [s.filesId, ...(s.head ? [`tests:${s.head}`] : [])] : []);
  }

  draw(_, s) {
    return html`<aside class="ctx" aria-label="Details"><header class="ctx-head">Details</header>
      <dl class="ctx-dl">
        <dt>Item</dt><dd>${s.slug ?? s.tool ?? "idle"}</dd>
        <dt>Round</dt><dd>${s.round ?? "—"}</dd>
        <dt>Brief age</dt><dd>${s.brief > 0 ? html`<${Age} text=${(now) => ageText(ageOf({ as_of: s.brief }, {}, now)).replace(" ago", "")} />` : "—"}</dd>
        <dt>Model</dt><dd>${s.model ?? "—"}</dd>
        <dt>Context used</dt><dd class=${s.ctxOver ? "ctx-used over" : "ctx-used"} title=${s.ctxTitle}>${s.ctx}${s.ctxAt > 0 ? html` · <${Age} text=${(now) => ageText(ageOf({ as_of: s.ctxAt }, {}, now))} />` : ""}</dd>
        <dt>Session</dt><dd class="mono">${s.session ? s.session.slice(0, 8) : "—"}</dd>
      </dl>
      <h3>Files changed · ${s.filesTotal}${s.branch ? html` <span class="dim mono">${s.branch}</span>` : null}</h3>
      ${s.filesKey === "" ? html`<p class="dim">none</p>` : html`<ul class="files">${s.filesKey.split("\n").map((l) => html`<li key=${l}><span class="xy mono">${l.slice(0, 2)}</span> ${l.slice(2)}</li>`)}</ul>`}
      ${s.truncated ? html`<p class="dim">showing the first ${this.files.length} of ${s.filesTotal}</p>` : null}
      <h3>Tests${s.head ? html` <span class="dim mono">${s.head}</span>` : null}</h3>
      ${s.testsKey === "" ? html`<p class="dim">no results</p>` : html`<ul class="tests">${s.testsKey.split("\n").map((l) => { const [set, status, passed, failed] = l.split("|"); return html`<li key=${set} class=${`t-${status}`}>${set}: ${status} · ${passed} passed${Number(failed) > 0 ? `, ${failed} failed` : ""}</li>`; })}</ul>`}
      <h3>Usage</h3>
      <dl class="ctx-dl">
        <dt>This turn</dt><dd>${s.live ?? "—"}</dd>
        <dt>Per turn</dt><dd>${s.average === null ? "—" : s.average} <span class="dim">over ${s.turns} loaded turns</span></dd>
        <dt>Turn time</dt><dd>${s.seconds === null ? "—" : `${s.seconds} s`} <span class="dim">avg</span></dd>
      </dl>
    </aside>`;
  }
}

export class ConversationView extends Live {
  select(snap) {
    const team = firstTeam(snap.slices, snap.ui);
    const ids = memberIds(team);
    const wanted = snap.ui.selected ?? null;
    return { memberId: wanted && ids.includes(wanted) ? wanted : ids[0] ?? null };
  }

  draw({ store }, { memberId }) {
    if (!memberId) {
      return html`<p class="dim">Loading…</p>`;
    }
    return html`<section class="conversation" aria-label="Conversation">
      <${MemberRail} store=${store} />
      <${TranscriptPane} key=${memberId} store=${store} memberId=${memberId} />
      <${ContextAside} key=${memberId} store=${store} memberId=${memberId} />
    </section>`;
  }
}
