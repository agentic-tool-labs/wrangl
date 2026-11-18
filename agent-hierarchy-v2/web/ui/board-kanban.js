import { html } from "./h.js";
import { Live } from "./live-component.js";
import { memberLink } from "./member-summary.js";
import { hashOf } from "./deeplink.js";
import { askerLabel, isQuiet, nameIn, summaryIdOf } from "./select.js";
import { historyView } from "./board-icicle.js";
import { attentionOf, questionsId } from "./questions-model.js";
import { AnswerForm } from "./questions-view.js";
import { COLUMNS, DONE_CAP, WAVES, waveColumns, queueRows, stalledOf, streamName, STATE_LABEL, itemHistory, prefOf, readPref, httpUrl, itemLogId } from "./plans-model.js";
import { ItemDialog, optionsText } from "./item-dialog.js";

// A Backlog row's state that reads as work under way.
const UNDER_WAY = new Set(["in_progress", "in_review", "testing"]);

// Plans · Board as a kanban (ah-ui-ia §2, the board mock, board-r2): one filter bar above the board (the breadcrumb All teams › team ›
// lane › epic › deliverable, and every other filter), then only the state columns, Backlog first, as one canvas: the stage scrolls both axes. A card
// carries its team or lane and epic, which were swimlane rows before board-r2. It reads PlansView's one board model and adds what
// only the kanban draws: the backlog column, the lane partition, the waiting markers and the side panel. Nothing runs at rest.

const NONE = "~";
export const WAVE_CAP = 4;
const LEVELS = ["team", "lane", "epic", "deliverable"];
const REPORT = { working: "Working", blocked: "Blocked", review: "In review", in_review: "In review", done: "Done", dissolved: "Dissolved" };
const SEEN = ", changed since your last visit";

// The board's zoom (board-r2): CSS zoom on the board canvas, 50% to 150%, kept per browser. Not in the URL.
const ZOOM_KEY = "ah.board.zoom";
export const ZOOM_MIN = 0.5;
export const ZOOM_MAX = 1.5;
// The zoom is kept unrounded, so a run of small wheel or pinch deltas adds up; it is rounded only to show and to store.
export const clampZoom = (z) => Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, z));
const round2 = (z) => Math.round(z * 100) / 100;
// A button or key steps to the next 10% mark in its direction.
export const stepZoom = (z, dir) => clampZoom(round2(dir > 0 ? Math.floor(z * 10 + 1e-6) / 10 + 0.1 : Math.ceil(z * 10 - 1e-6) / 10 - 0.1));
// Ctrl/Cmd + wheel (a trackpad pinch sends many small deltas): continuous, about 10% per 100 of deltaY.
export const wheelZoom = (z, deltaY) => clampZoom(z * Math.exp(-deltaY * 0.001));
const readZoom = () => {
  try {
    const z = Number(globalThis.localStorage?.getItem(ZOOM_KEY));
    return z ? clampZoom(round2(z)) : 1;
  } catch {
    return 1;
  }
};

// A served zero while a plan on show is unread: the head says so, never 0.
const NOT_LOADED = "…";

// Kinds that contain work rather than being work: never Backlog column rows (ah-work-items §10).
const CONTAINERS = ["project", "goal"];

// The counts the daemon serves for the teams on show: one team, else every team that is not quiet.
export function servedCounts(slices, teamId, now) {
  const every = [...slices.values()].filter((s) => s.id.startsWith("team-summary:"));
  const summaries = every.filter((s) => (teamId ? s.id === summaryIdOf(teamId) : !isQuiet(Date.parse(s.body?.quiet_at ?? ""), now)));
  return {
    waiting: summaries.reduce((n, s) => n + (s.body?.waiting_on_you ?? 0), 0),
    progress: summaries.reduce((n, s) => n + (s.body?.in_progress_now ?? 0), 0),
    // Over every team when no one team is on show: an unread plan reads as quiet, and it may hold the cards.
    pending: (teamId ? summaries : every).some((s) => s.body?.plan_loaded === false),
  };
}

// A duration as the board says it: "40 min", "46 h", "3 d".
export const durText = (ms) => {
  const m = Math.max(0, Math.round(ms / 60_000));
  return m < 60 ? `${m} min` : m < 2880 ? `${Math.round(m / 60)} h` : `${Math.round(m / 1440)} d`;
};

const teamKeyOf = (c) => `${c.dirId}/${c.team}`;

// The lanes of a manager team, from its `streams[]`: every row that names a sub-orchestrator. A row with a child team is a confirmed
// lane; one without is listed "(not confirmed)" and holds only the manager's items in its stream (§2.2).
export function lanesOf(teamBody) {
  const rows = Array.isArray(teamBody?.streams) ? teamBody.streams : [];
  return rows.filter((s) => typeof s?.sub_orchestrator === "string" && typeof s.name === "string").map((s) => ({
    stream: s.name,
    child: typeof s.child_team === "string" ? `team:${teamBody.dir_id}/${s.child_team}` : null,
    so: s.sub_orchestrator,
    state: s.state ?? "open",
    branch: s.branch ?? null,
    base: s.base ?? null,
  })).sort((a, b) => a.stream.localeCompare(b.stream));
}

// The lane a card belongs to: a child team's card is its lane's; a manager card in a lane's stream is that lane's; else "top".
export function laneOfCard(c, lanes, managerKey) {
  const key = teamKeyOf(c);
  if (key !== managerKey) {
    return lanes.find((l) => l.child === `team:${key}`)?.stream ?? "top";
  }
  return lanes.some((l) => l.stream === c.stream) ? c.stream : "top";
}

// The cards of a lane choice: All (null) is every card, "top" the manager's own work, a stream that lane's.
export const partition = (cards, lanes, managerKey, lane) => (lane ? cards.filter((c) => laneOfCard(c, lanes, managerKey) === lane) : cards);

// A card's deliverable and epic, walking its `parent` up the backlog.
export function chainOf(c, backlog) {
  let deliverable = null;
  let epic = null;
  for (let k = c.parent, n = 0; k && n < 8; n++) {
    const r = backlog.get(k);
    if (!r) {
      break;
    }
    if (r.kind === "deliverable" && !deliverable && !epic) {
      deliverable = r;
    } else if (r.kind === "epic" && !epic) {
      epic = r;
    }
    k = r.parent;
  }
  return { deliverable, epic };
}

// Backlog column: the open rows the daemon does not call planned, by wave (Now, Next, Later, Untriaged), by rank. A planned row is
// drawn as a tier in the other columns instead. Under an epic or deliverable scope it narrows to that row's descendants. Project and
// goal rows stay out unless `containers` asks for them.
export function backlogColumn(body, root = null, containers = false) {
  const byKey = new Map((body?.items ?? []).map((r) => [r.key, r]));
  const under = (r) => {
    for (let k = r.parent, n = 0; k && n < 8; n++, k = byKey.get(k)?.parent) {
      if (k === root) {
        return true;
      }
    }
    return false;
  };
  const { cols } = waveColumns(body);
  return WAVES.map(([w, label]) => ({ wave: w, label, rows: cols[w].filter((r) => !r.planned && (containers || !CONTAINERS.includes(r.kind)) && (!root || under(r))) })).filter((g) => g.rows.length > 0);
}

// The card an `item=` names: a plan item key (bare or stream-qualified), or an exchange slice id mapped to its item by the exchange's
// own `item` field. Null when nothing on the board matches.
export function itemTarget(cards, item, slices) {
  if (!item) {
    return null;
  }
  const key = item.startsWith("exchange:") ? slices.get(item)?.body?.item ?? null : item;
  if (!key) {
    return null;
  }
  return cards.find((c) => c.id === key || c.key === key || `${c.stream}:${c.key}` === key || `${c.stream}/${c.key}` === key)?.id ?? null;
}

// A card's marker (§2.3): blocked since, or a Next card's queue wait. Stalled is the server's stall time reached with an idle
// eligible member; the UI never sets its own threshold.
export function markerOf(c, q, now) {
  if (c.state === "blocked") {
    return { cls: "attn", text: `▲ blocked${c.since ? ` ${durText(now - c.since)}` : ""}`, tip: "Blocked" };
  }
  if (q && c.column === "next") {
    const age = q.queuedSince ? ` ${durText(now - q.queuedSince)}` : "";
    return stalledOf(q.stallAt, q.eligible, now)
      ? { cls: "err", text: `■ stalled${age} · no owner, ${q.eligible.length} idle`, tip: "Ready, unclaimed, and an eligible member is idle" }
      : { cls: "wait", text: `◷ waiting${age}${q.eligible.length === 0 ? ` · no idle ${q.role ?? "member"}` : ""}`, tip: "Ready and not yet claimed" };
  }
  if (c.state === "waiting_orch") {
    return { cls: "wait", text: "◷ waiting on the Orchestrator", tip: "Waiting on the Orchestrator" };
  }
  return null;
}

const markText = (m) => (m?.kind === "moved" ? `${STATE_LABEL[m.from] ?? m.from} → ${STATE_LABEL[m.to] ?? m.to}` : m?.kind === "new" ? "New since your last visit" : m?.kind === "done" ? "Done since your last visit" : "Changed since your last visit");
const toggled = (list, k) => (list.includes(k) ? list.filter((x) => x !== k) : [...list, k]);
const plural = (n, one, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;
// Open blockers are not the `blocked` state: nothing is gated, so the marker keeps the quiet waiting tone.
const blockedBy = (n) => (n > 0 ? html`<div class="kb-flag wait" title=${`${plural(n, "blocker")} still open`}>⊘ blocked by ${n}</div>` : null);
const workLabel = (id, n) => `${id ? `, ${id}` : ""}${n > 0 ? `, blocked by ${n}` : ""}`;

// A state card and a column head, shared by the Board and the Looks page's preview, which must look exactly like it. A Backlog row is blCard.
// A click on the card itself, not on a link or the answer form. A form control can re-render away before its click reaches the card
// (Preact renders between listeners), so a target no longer in the page is the form's, never the card's.
const onCard = (e) => e.target.isConnected !== false && !e.target.closest?.("a, .qa-answer");

export const kbCard = (c, { on = false, focusable = false, pick = null } = {}) =>
  html`<article key=${c.id} class=${`kb-card glass${c.ask ? " is-ask" : ""}${c.flag?.cls === "attn" ? " is-attn" : c.flag?.cls === "err" ? " is-stalled" : ""}`} data-nav data-id=${c.id} tabindex=${focusable ? 0 : -1}
      aria-current=${on ? "true" : undefined} aria-label=${`${c.title}${c.asker ? `, asked by ${c.askerTitle}` : ""}${c.ref ? `, ${c.ref}` : ""}, ${c.where.kind} ${c.where.name}${c.epic ? `, epic ${c.epic.name}` : ""}${c.owner ? `, owner ${c.ownerTitle ?? c.owner}` : ""}${c.flag ? `, ${c.flag.text.slice(2)}` : ""}${workLabel(c.workId, c.blockedOpen)}${c.mark ? SEEN : ""}`}
      onClick=${(e) => onCard(e) && pick?.(c.id)}>
      <h3 class="kb-title" id=${`kt-${c.id}`} title=${c.title} data-text=${c.title}>${c.title}</h3>
      <div class="kb-meta">
        ${c.workId ? html`<span class="chip mono" title=${`Work id ${c.workId}`}>${c.workId}</span>` : null}
        <span class=${`kb-where is-${c.where.kind}`} title=${c.where.tip}>${c.where.name}</span>
        ${c.asker ? html`<span title=${`Asked by ${c.askerTitle}`}>asked by ${c.askerShown}</span>` : null}
        ${c.epic ? html`<span class="kb-epic" title=${`${c.epic.name} (${c.epic.tip})`}>${c.epic.name}</span>` : null}
        ${c.owner ? html`<span class="kb-who" title=${c.ownerTitle ?? c.owner}>${memberLink(c.ownerId, c.ownerLabel ?? c.owner)}</span>` : null}
        ${c.size ? html`<span class="chip" title=${c.sizeFrom ? `Size ${c.size}, from deliverable ${c.sizeFrom}` : `Size ${c.size}`}>${c.size}</span>` : null}
        ${c.msgs ? html`<span class="num">${plural(c.msgs, "msg")}</span>` : null}
        ${c.mark ? html`<span class="kb-changed" title=${c.mark}>Changed</span>` : null}
        ${c.ref ? html`<span class="kb-ref mono" title=${c.ref}>${c.ref}</span>` : null}
      </div>
      ${c.question ? html`<p class="kb-ask" id=${`kq-${c.id}`}>${c.question}</p>` : null}
      ${c.form ? html`<${AnswerForm} key=${`${c.form.q.id}@${c.form.q.since ?? ""}`} q=${c.form.q} teamId=${c.form.teamId} run=${c.form.run} uid=${`kc-${c.id}`} labelledBy=${c.question ? `kt-${c.id} kq-${c.id}` : `kt-${c.id}`} />`
        : c.options ? html`<p class="kb-opts">${c.options}</p>` : null}
      ${c.answer ? html`<a class="kb-answer" href=${c.answer.href}>${c.answer.text}</a>` : null}
      ${c.flag ? html`<div class=${`kb-flag ${c.flag.cls}`} title=${c.flag.tip}>${c.flag.text}</div>` : null}
      ${blockedBy(c.blockedOpen)}
    </article>`;

// The dialog's answer form, over the item's question when it has one; the pick group is named by the dialog's title.
const panelForm = (p) => (p.form ? html`${p.question ? html`<p class="kb-ask" id="kb-panel-ask">${p.question}</p>` : null}
  <${AnswerForm} key=${`${p.form.q.id}@${p.form.q.since ?? ""}`} q=${p.form.q} teamId=${p.form.teamId} run=${p.form.run} uid="kb-panel" labelledBy=${p.question ? `${PANEL_TITLE} kb-panel-ask` : PANEL_TITLE} />` : null);
const PANEL_TITLE = "kb-panel-title";

export const kbHead = (c, capped, blScope, fold) => html`<div class="kb-head">
          <span class="kb-label" data-text=${c.label}>${c.key === "backlog" ? html`<button type="button" class="kb-tw" aria-expanded="true" aria-label="Fold Backlog to a strip" onClick=${() => fold("col:backlog")}><span aria-hidden="true">▾</span></button>` : null}${c.label}${c.key === "backlog" && blScope ? html`<span class="kb-scope" title="· whole directory">· whole directory</span>` : null}</span>
          <span class="kb-count num" aria-label=${capped ? `${c.count} of ${c.total} shown` : c.head === NOT_LOADED ? "not loaded yet" : plural(c.head ?? c.count, "card")}>${capped ? html`${c.count} <span class="kb-of">of ${c.total}</span>` : c.head ?? c.count}</span>
        </div>`;

export class BoardKanban extends Live {
  stored = { boardFinished: readPref("boardFinished") };
  state = { history: null };
  zoom = readZoom();

  parsed(json) {
    if (this.boardFor !== json) {
      this.boardFor = json;
      this.board = JSON.parse(json ?? "null") ?? { cells: {} };
    }
    return this.board;
  }

  managerKey(props) {
    return props.teamId ? props.teamId.slice("team:".length) : null;
  }

  leaseIds(snap, props) {
    const cards = Object.values(this.parsed(props.board).cells).flat();
    const keys = [...new Set(cards.filter((c) => c.team && c.dirId).map(teamKeyOf))].sort();
    const dirs = [...new Set(cards.map((c) => c.dirId).filter(Boolean))];
    const mk = this.managerKey(props);
    if (mk) {
      dirs.push(mk.split("/")[0]);
    }
    const lanes = mk ? lanesOf(snap.slices.get(props.teamId)?.body) : [];
    const item = snap.ui.item;
    return [...new Set([
      ...keys.map((k) => `queue:${k}`),
      ...keys.map((k) => `item-msgs:${k}`),
      // Only a team with a card waiting on you pays for its questions: the card answers them in place.
      ...cards.filter((c) => c.column === "waiting" && c.team && c.dirId).map((c) => questionsId(`team:${teamKeyOf(c)}`)),
      ...dirs.map((d) => `backlog:${d}`),
      ...(props.teamId ? [props.teamId] : []),
      ...lanes.map((l) => l.child).filter(Boolean),
      ...(item?.startsWith("exchange:") ? [item] : []),
    ])];
  }

  select(snap, props) {
    const { ui, slices } = snap;
    const now = Math.floor(Date.now() / 60_000) * 60_000;
    const board = this.parsed(props.board);
    const mk = this.managerKey(props);
    const teamBody = props.teamId ? slices.get(props.teamId)?.body : null;
    const showFinished = prefOf(ui, "boardFinished", this.stored) === "on";
    const allLanes = lanesOf(teamBody);
    const lanes = allLanes.filter((l) => showFinished || l.state !== "done" || l.stream === ui.lane);
    const all = COLUMNS.flatMap(([col]) => board.cells[col] ?? []);
    const dir = mk ? mk.split("/")[0] : all[0]?.dirId ?? null;
    const blBody = dir ? slices.get(`backlog:${dir}`)?.body : null;
    const bl = new Map((blBody?.items ?? []).map((r) => [r.key, r]));
    const owner = ui.owner ?? null;
    const lane = mk && allLanes.length > 0 && ui.lane ? ui.lane : null;
    const scopeEpic = ui.epic ?? null;
    const scopeDeliv = scopeEpic ? ui.deliverable ?? null : null;
    const chained = all.map((c) => {
      const { deliverable, epic } = chainOf(c, bl);
      return { ...c, epic: epic?.key ?? NONE, deliv: deliverable?.key ?? NONE, size: c.size ?? deliverable?.size ?? null, sizeFrom: c.size ? null : deliverable?.key ?? null };
    });
    // List → Open in board: only that stream's cards (the chip in the bar clears it).
    const only = /^s:(.+)\|(.+)$/.exec(ui.plansStream ?? "");
    const inLane = (mk ? partition(chained, allLanes, mk, lane) : chained).filter((c) => !only || (teamKeyOf(c) === only[1] && c.stream === only[2]));
    const inEpic = scopeEpic ? inLane.filter((c) => c.epic === scopeEpic) : inLane;
    const inScope = scopeDeliv ? inEpic.filter((c) => c.deliv === scopeDeliv) : inEpic;
    const shown = owner ? inScope.filter((c) => c.owner === owner) : inScope;
    // Queue rows by stream/item; message counts by team.
    const queue = new Map();
    for (const k of new Set(shown.map(teamKeyOf))) {
      for (const q of queueRows(slices.get(`queue:${k}`)?.body ?? [], now)) {
        queue.set(`${k}|${q.item}`, q);
      }
    }
    const msgsOf = (c) => slices.get(`item-msgs:${teamKeyOf(c)}`)?.body?.counts?.[c.key] ?? null;
    const showAll = ui.boardAll ?? [];
    // Done today is capped across the board: the newest DONE_CAP, the rest behind "Show all".
    const doneAll = shown.filter((c) => c.column === "done").sort((a, b) => b.at - a.at);
    const doneShown = new Set((showAll.includes("done") ? doneAll : doneAll.slice(0, DONE_CAP)).map((c) => c.id));
    const drawn = shown.filter((c) => c.column !== "done" || doneShown.has(c.id));
    const filtersOn = props.blockedOnly || props.changesOnly;
    const waves = filtersOn || owner ? [] : backlogColumn(blBody, scopeDeliv ?? scopeEpic, props.projects === true);
    const blTotal = waves.reduce((s, g) => s + g.rows.length, 0);
    const blFolded = (ui.boardCol ?? []).includes("col:backlog");
    const cardOut = (c) => {
      const q = queue.get(`${teamKeyOf(c)}|${c.stream}/${c.key}`) ?? null;
      const flag = c.decision || c.changed ? null : markerOf(c, q, now);
      // A decision card shows the oldest open question; until that is held, or with none open, the summary's count is the best there is.
      const waiting = c.column === "waiting";
      const teamId = `team:${teamKeyOf(c)}`;
      const asked = waiting ? attentionOf(slices.get(questionsId(teamId))?.body) : null;
      // a waiting item has its own card, so a decision card reads decisions alone
      const ask = c.decision ? asked?.decisions[0] ?? null : null;
      // the row the card answers in place; an item past the slice's cap, or before it is held, keeps the link to the Inbox
      const mine = c.decision ? ask : waiting ? asked?.open.find((q) => q.kind === "item" && q.id === `${c.stream}:${c.key}`) ?? null : null;
      const inbox = hashOf({ view: "inbox", inboxTab: "attention", team: teamId });
      return {
        id: c.id,
        ref: c.decision || c.changed ? null : `${streamName(c.stream)} / ${c.key}`,
        workId: c.workId ?? null,
        blockedOpen: c.blockedOpen ?? 0,
        title: ask?.text || c.title,
        asker: ask?.asker ?? null,
        // ah2-220 Amendment 1: a lane's orchestrator asking reads Sub-Orchestrator (select.js askerLabel); else the asker as served
        ...(({ label, title }) => ({ askerShown: label, askerTitle: title }))(askerLabel(slices, c.dirId, c.team, ask?.asker) ?? { label: ask?.asker ?? null, title: ask?.asker ?? null }),
        options: ask ? optionsText(ask) : null,
        // the link stays for the rest: more questions, or options or text the slice cut (the Inbox opens them whole)
        answer: c.decision ? (ask && asked.decisionCount <= 1 && ask.more === 0 && !ask.cut ? null : { href: inbox, text: !ask ? "Open Inbox" : asked.decisionCount > 1 ? `Answer ${asked.decisionCount} in Inbox` : "Answer in Inbox" })
          : waiting && !mine ? { href: inbox, text: "Answer in Inbox" } : null,
        form: mine ? { q: mine, teamId, run: asked.run } : null,
        // an item's question is its note, under the item's own title
        question: mine?.kind === "item" && mine.text !== c.title ? mine.text : null,
        ask,
        also: c.decision ? asked?.decisions.slice(1) ?? [] : [],
        owner: c.owner || null,
        // ah2-220: how the owner reads (a lane's orchestrator is its Sub-Orchestrator); `owner` stays the key filters match
        ...(c.owner ? (({ label, title }) => ({ ownerLabel: label, ownerTitle: title }))(nameIn(slices, c.dirId, c.team, c.owner)) : {}),
        ownerId: c.owner && c.dirId && c.team ? `member:${c.dirId}/${c.team}/${c.owner}` : null,
        size: c.size,
        sizeFrom: c.sizeFrom,
        msgs: msgsOf(c),
        flag,
        mark: c.mark && c.mark.kind !== "stream" ? markText(c.mark) : c.mark ? "Changed since your last visit" : null,
      };
    };
    // A card's team or lane and epic (they were the swimlane and epic rows): cards keep that order inside each column, team first,
    // then lanes, then epic by backlog rank.
    const whereOf = (c) => {
      const l = mk ? laneOfCard(c, allLanes, mk) : "top";
      const ln = l === "top" ? null : allLanes.find((x) => x.stream === l);
      return ln ? { id: `kl:${l}`, name: ln.child ? ln.child.split("/").pop() : l, kind: "lane", tip: `lane · runs ${l}${ln.child ? "" : " (not confirmed)"}`, order: 1 } : { id: `kt:${teamKeyOf(c)}`, name: c.team, kind: "team", tip: `team ${c.team}`, order: 0 };
    };
    const rank = (k) => (k === NONE ? Infinity : bl.get(k)?.rank ?? 1e9);
    const placed = drawn.map((c) => ({ c, w: whereOf(c) })).sort((a, b) => a.w.order - b.w.order || a.w.name.localeCompare(b.w.name) || rank(a.c.epic) - rank(b.c.epic) || a.c.epic.localeCompare(b.c.epic));
    const withWhere = ({ c, w }) => ({ ...cardOut(c), where: { kind: w.kind, name: w.name, tip: w.tip }, epic: c.epic === NONE ? null : { name: bl.get(c.epic)?.title ?? c.epic, tip: `epic ${c.epic}${bl.get(c.epic)?.size ? ` · size ${bl.get(c.epic).size}` : ""}` } });
    const cells = Object.fromEntries(COLUMNS.map(([col]) => [col, placed.filter(({ c }) => c.column === col).map(withWhere)]));
    const counts = Object.fromEntries(COLUMNS.map(([col]) => [col, drawn.filter((c) => c.column === col).length]));
    // Waiting on you and In progress are the served counts over the teams on show, as in the TUI, unless the bar narrowed the cards.
    const served = !owner && !scopeEpic && !scopeDeliv && !only && !lane && !filtersOn ? servedCounts(slices, props.teamId, now) : {};
    const cols = [
      { key: "backlog", label: "Backlog", count: blFolded ? 0 : waves.reduce((s, g) => s + (showAll.includes(`wave:${g.wave}`) ? g.rows.length : Math.min(WAVE_CAP, g.rows.length)), 0), total: blTotal, folded: blFolded },
      ...COLUMNS.map(([col, label]) => ({ key: col, label, count: counts[col], total: col === "done" ? doneAll.length : counts[col], head: served[col] === 0 && served.pending ? NOT_LOADED : served[col] ?? null })),
    ];
    const backlog = waves.map((g) => {
      const open = showAll.includes(`wave:${g.wave}`);
      const rows = open ? g.rows : g.rows.slice(0, WAVE_CAP);
      return { wave: g.wave, label: g.label, total: g.rows.length, open, rows: rows.map((r) => ({ id: `bl:${r.key}`, key: r.key, kind: r.kind ?? null, title: r.title, size: r.size ?? null, state: r.state, holder: r.holder ?? null, holderId: r.holder && mk ? `member:${mk}/${r.holder}` : null, workId: r.work_id ?? null, blockedOpen: r.blocked_by_open ?? 0 })) };
    });
    // The breadcrumb, and the next level's choices for the menu on its last pill.
    const epicName = (k) => (k === NONE ? "No epic" : bl.get(k)?.title ?? k);
    const delivName = (k) => (k === NONE ? "No deliverable" : bl.get(k)?.title ?? k);
    const laneName = (l) => (l === "top" ? "Top team" : l);
    const crumbs = [{ depth: 0, kind: "", name: "All teams", href: props.teamId ? hashOf({ view: "allplans", team: null }) : null }];
    if (props.teamId) {
      crumbs.push({ depth: 1, kind: "team", name: teamBody?.name ?? mk.split("/").pop() });
      if (lane) {
        crumbs.push({ depth: 2, kind: lane === "top" ? "" : "lane", name: laneName(lane) });
      }
    }
    if (scopeEpic) {
      crumbs.push({ depth: 3, kind: "epic", name: epicName(scopeEpic) });
    }
    if (scopeDeliv) {
      crumbs.push({ depth: 4, kind: "deliverable", name: delivName(scopeDeliv) });
    }
    const depth = crumbs.at(-1).depth;
    const count = (list, f) => list.filter(f).length;
    let menu = [];
    let menuHead = "";
    if (depth === 0) {
      menuHead = "Teams";
      menu = [...new Set(all.map(teamKeyOf))].sort().map((k) => ({ key: k, name: k.split("/").pop(), n: count(all, (c) => teamKeyOf(c) === k), href: hashOf({ view: "board", team: `team:${k}` }) }));
    } else if ((depth === 1 && allLanes.length > 0)) {
      menuHead = "Lanes";
      menu = [{ key: "top", name: "Top team", kind: "", n: partition(chained, allLanes, mk, "top").length }, ...lanes.map((l) => ({ key: l.stream, name: `${l.stream}${l.child ? "" : " (not confirmed)"}${l.state === "done" ? " (finished)" : ""}`, kind: "lane", n: partition(chained, allLanes, mk, l.stream).length }))];
    } else if (depth < 3) {
      menuHead = "Epics";
      menu = [...new Set(inLane.map((c) => c.epic))].sort((a, b) => rank(a) - rank(b)).map((k) => ({ key: k, name: epicName(k), n: count(inLane, (c) => c.epic === k) }));
    } else if (depth === 3) {
      menuHead = "Deliverables";
      menu = [...new Set(inEpic.map((c) => c.deliv))].sort((a, b) => rank(a) - rank(b)).map((k) => ({ key: k, name: delivName(k), n: count(inEpic, (c) => c.deliv === k) }));
    }
    // The lane header: one line about the chosen lane (§2.2).
    const ln = lane && lane !== "top" ? allLanes.find((l) => l.stream === lane) : null;
    const child = ln?.child ? slices.get(ln.child)?.body : null;
    const laneHead = ln ? {
      stream: ln.stream,
      so: { id: `member:${mk}/${ln.so}`, name: ln.so },
      members: (Array.isArray(child?.members) ? child.members : []).filter((id) => typeof id === "string").map((id) => ({ id, name: slices.get(id)?.body?.name ?? id.split("/").pop() })),
      branch: ln.branch,
      base: ln.base,
      state: ln.state,
      report: child?.lane?.reported?.state ? REPORT[child.lane.reported.state] ?? child.lane.reported.state : ln.child ? null : "Not confirmed",
    } : null;
    // The item deep link, then the selection; the panel shows what is selected.
    // a backlog key selects its row in the Backlog column (the Sitrep's "from backlog" rows link here)
    const target = itemTarget(all, ui.item, slices) ?? (ui.item && bl.has(ui.item) ? `bl:${ui.item}` : null);
    // An exchange id resolves once its slice is held (with or without an item) or the daemon says it is gone; then a miss is said.
    const notFound = ui.item && !target && props.ready !== false && all.length > 0 && (!ui.item.startsWith("exchange:") || slices.has(ui.item) || snap.gone?.has(ui.item) === true) ? ui.item : null;
    const sel = ui.boardSel ?? target ?? null;
    return {
      model: JSON.stringify({
        cols,
        cells,
        backlog,
        blScope: scopeEpic ? null : "Whole directory",
        crumbs,
        menu,
        menuHead,
        laneHead,
        owner,
        owners: [...new Set([...inScope.map((c) => c.owner).filter(Boolean), ...(owner ? [owner] : [])])].sort(),
        stream: only ? streamName(only[2]) : null,
        notFound,
        sel,
        target,
        panel: sel ? this.panel(sel, { chained, bl, dir, mk, allLanes, msgsOf, cardOut, slices, whereOf }) : null,
        empty: drawn.length === 0 && blTotal === 0,
        finished: allLanes.filter((l) => l.state === "done").length,
        showFinished,
        total: inScope.length,
      }),
    };
  }

  // What the side panel shows for a selection: an item, a backlog row (epic, deliverable or task), or a lane or team.
  panel(sel, { chained, bl, dir, mk, allLanes, msgsOf, cardOut, slices, whereOf }) {
    const col = (k) => COLUMNS.find(([c]) => c === k)?.[1] ?? k;
    const items = (cs) => cs.map((c) => ({ id: c.id, text: `${c.key} · ${c.title}`, col: col(c.column) }));
    if (sel.startsWith("bl:")) {
      const r = bl.get(sel.slice(3));
      if (!r) {
        return null;
      }
      const kids = [...bl.values()].filter((x) => x.parent === r.key);
      return {
        kind: r.kind ?? "Backlog row",
        title: r.title,
        outcome: r.outcome ?? null,
        note: r.note ?? null,
        rows: [["Key", r.key, "mono"], ["Work id", r.work_id, "mono"], ["Blocked by", r.blocked_by_open > 0 ? `${r.blocked_by_open} open` : null], ["State", STATE_LABEL[r.state] ?? r.state], ["Size", r.size], ["Wave", r.wave], ["Theme", r.theme]].filter(([, v]) => v),
        holder: r.holder && mk ? { id: `member:${mk}/${r.holder}`, name: r.holder } : null,
        parent: r.parent && bl.has(r.parent) ? { id: `bl:${r.parent}`, text: bl.get(r.parent).title } : null,
        kids: kids.map((k) => ({ id: `bl:${k.key}`, text: `${k.key} · ${k.title}` })),
        items: items(chained.filter((c) => c.parent === r.key || c.deliv === r.key || c.epic === r.key)),
        ext: r.ext_id ? { id: r.ext_id, url: httpUrl(r.ext_url) ?? r.issue?.url ?? null } : null,
        log: itemLogId(dir, r.work_id),
      };
    }
    if (sel.startsWith("kl:") || sel.startsWith("kt:")) {
      const l = sel.startsWith("kl:") ? allLanes.find((x) => x.stream === sel.slice(3)) : null;
      const teamId = l ? l.child : `team:${sel.slice(3)}`;
      const body = teamId ? slices.get(teamId)?.body : null;
      const cs = chained.filter((c) => (l ? laneOfCard(c, allLanes, mk) === l.stream : `kt:${teamKeyOf(c)}` === sel));
      return {
        kind: l ? "Lane" : "Team",
        title: l ? l.stream : body?.name ?? sel.split("/").pop(),
        rows: l ? [["Branch", l.branch ? `${l.branch} → ${l.base ?? "main"}` : null, "mono"], ["State", l.state], ["Sub-orchestrator", null]].filter(([k, v]) => v || k === "Sub-orchestrator") : [],
        holder: l ? { id: `member:${mk}/${l.so}`, name: l.so } : null,
        members: (Array.isArray(body?.members) ? body.members : []).filter((id) => typeof id === "string").map((id) => ({ id, name: slices.get(id)?.body?.name ?? id.split("/").pop() })),
        items: items(cs),
      };
    }
    const c = chained.find((x) => x.id === sel);
    if (!c) {
      return null;
    }
    const out = cardOut(c);
    const w = whereOf(c);
    return {
      kind: c.decision ? "Decision" : `Item · ${out.ref ?? ""}`,
      title: out.title,
      answer: out.answer,
      form: out.form,
      question: out.question,
      // with the form in the dialog, its options are not listed twice
      ask: out.form ? null : out.ask,
      note: c.note ?? null,
      also: out.also,
      rows: [
        ["Asked by", out.askerShown],
        ["Plan item", out.ask?.item, "mono"],
        ["Work id", c.workId, "mono"],
        ["Blocked by", c.blockedOpen > 0 ? `${c.blockedOpen} open` : null],
        ["Column", col(c.column)],
        ["State", STATE_LABEL[c.state] && !c.decision ? STATE_LABEL[c.state] : null],
        ["Marker", out.flag?.text],
        ["Messages", msgsOf(c) === null ? null : String(msgsOf(c))],
        ["Size", c.size ? `${c.size}${c.sizeFrom ? " (deliverable)" : ""}` : null],
        ["Stream", c.stream ? streamName(c.stream) : null, "mono"],
        ["Team", w.kind === "team" ? null : c.team],
        ["Branch", c.branch, "mono"],
        ["Changes", out.mark],
      ].filter(([, v]) => v),
      holder: out.ownerId ? { id: out.ownerId, name: out.ownerLabel ?? out.owner } : null,
      // The team or lane opens its own panel, as its swimlane row's name did.
      group: { id: w.id, text: w.name, label: w.kind === "lane" ? "Lane" : "Team" },
      parent: c.deliv !== NONE ? { id: `bl:${c.deliv}`, text: bl.get(c.deliv)?.title ?? c.deliv, label: "Deliverable" } : null,
      epic: c.epic !== NONE ? { id: `bl:${c.epic}`, text: bl.get(c.epic)?.title ?? c.epic } : null,
      outcome: c.goal ?? null,
      outcomeLabel: "Stream goal",
      kids: [],
      items: null,
      hist: c.decision || c.changed ? null : { dir: c.dirId, team: c.team, stream: c.stream, item: c.key, state: c.state },
      ext: c.extId || c.extUrl ? { id: c.extId, url: c.extUrl } : null,
      log: itemLogId(c.dirId, c.workId),
    };
  }

  componentDidUpdate() {
    super.componentDidUpdate();
    // An item deep link scrolls its card into view once, when it is first drawn.
    const m = this.m;
    if (m?.target && this.scrolledTo !== m.target) {
      const el = this.find(m.target);
      if (el) {
        this.scrolledTo = m.target;
        el.scrollIntoView?.({ block: "center", inline: "nearest" });
        this.cur = m.target;
      }
    }
    if (this.refocus) {
      this.find(this.refocus)?.focus();
      this.refocus = null;
    }
    this.loadHistory(m?.panel?.hist ?? null);
  }

  componentDidMount() {
    super.componentDidMount();
    globalThis.addEventListener?.("keydown", this.onZoomKey);
  }

  componentWillUnmount() {
    this.gone = true;
    globalThis.removeEventListener?.("keydown", this.onZoomKey);
    super.componentWillUnmount();
  }

  // Sets the zoom, keeping the board point under (cx, cy) in the stage where it was (the stage's centre by default).
  setZoom(z, cx = null, cy = null) {
    const next = clampZoom(z);
    const st = this.box;
    const kb = st?.querySelector(".kb");
    if (next === this.zoom || !kb) {
      return;
    }
    const x = cx ?? st.clientWidth / 2;
    const y = cy ?? st.clientHeight / 2;
    const px = (st.scrollLeft + x) / this.zoom;
    const py = (st.scrollTop + y) / this.zoom;
    this.zoom = next;
    kb.style.zoom = String(next);
    st.style.setProperty("--kb-zoom", String(next));
    st.scrollLeft = px * next - x;
    st.scrollTop = py * next - y;
    try {
      globalThis.localStorage?.setItem(ZOOM_KEY, String(round2(next)));
    } catch {
      // storage blocked: the level lasts this visit only
    }
    this.forceUpdate();
  }

  // Ctrl/Cmd + wheel over the board zooms about the cursor; a plain wheel scrolls. The listener is not passive, so it can stop the
  // browser's page zoom.
  onWheel = (e) => {
    if (!(e.ctrlKey || e.metaKey)) {
      return;
    }
    e.preventDefault();
    const r = this.box.getBoundingClientRect();
    this.setZoom(wheelZoom(this.zoom, e.deltaY), e.clientX - r.left, e.clientY - r.top);
  };

  // Ctrl/Cmd + "+", "-" or "0" while the pointer is over the board or focus is in it.
  onZoomKey = (e) => {
    if (!(e.ctrlKey || e.metaKey) || e.altKey || !this.box || !(this.hovered || this.box.contains(globalThis.document?.activeElement))) {
      return;
    }
    const dir = { "+": 1, "=": 1, "-": -1, _: -1, 0: 0 }[e.key];
    if (dir === undefined) {
      return;
    }
    e.preventDefault();
    this.setZoom(dir === 0 ? 1 : stepZoom(this.zoom, dir));
  };

  stageRef = (el) => {
    if (el === this.box) {
      return;
    }
    this.box?.removeEventListener("wheel", this.onWheel);
    this.box = el;
    el?.addEventListener("wheel", this.onWheel, { passive: false });
  };

  // The selected item's history, read again when the item or its state changes (the icicle's loader, shared).
  async loadHistory(hist) {
    const key = hist ? JSON.stringify(hist) : null;
    if (key === this.histFor) {
      return;
    }
    this.histFor = key;
    this.setState({ history: null });
    if (!hist) {
      return;
    }
    const history = await itemHistory(this.props.store, hist);
    if (this.histFor === key && !this.gone) {
      this.setState({ history });
    }
  }

  find(id) {
    return [...(this.box?.querySelectorAll("[data-nav]") ?? [])].find((n) => n.getAttribute("data-id") === id) ?? null;
  }

  setUi = (patch) => this.props.store.setUi(patch);

  pick = (id) => {
    if (!id.startsWith("kl:") && !id.startsWith("kt:")) {
      this.cur = id;
    }
    this.setUi({ boardSel: id });
  };

  close = () => {
    this.refocus = this.cur ?? null;
    this.setUi({ boardSel: null, item: null });
  };

  fold = (k) => this.setUi({ boardCol: toggled(this.props.store.state.ui.boardCol ?? [], k) });
  more = (k) => this.setUi({ boardAll: toggled(this.props.store.state.ui.boardAll ?? [], k) });

  // A breadcrumb pill goes up to its level; the menu goes one level down.
  up = (depth) => {
    const patch = { boardSel: null, item: null };
    if (depth < 4) {
      patch.deliverable = null;
    }
    if (depth < 3) {
      patch.epic = null;
    }
    if (depth < 2) {
      patch.lane = null;
    }
    this.setUi(patch);
  };

  down = (key) => {
    const m = this.m;
    const depth = m.crumbs.at(-1).depth;
    this.menuEl?.hidePopover?.();
    const patch = m.menuHead === "Lanes" ? { lane: key } : depth === 3 ? { deliverable: key } : { epic: key };
    this.setUi({ ...patch, boardSel: null, item: null });
    this.refocusCrumb = true;
  };

  onMenuToggle = (e) => {
    if (e.newState !== "open") {
      return;
    }
    const r = this.crumbBtn?.getBoundingClientRect();
    if (r) {
      this.menuEl.style.left = `${Math.max(8, Math.min(r.left, globalThis.innerWidth - 336))}px`;
      this.menuEl.style.top = `${r.bottom + 4}px`;
    }
    this.menuEl.querySelector("[role=menuitem]")?.focus();
  };

  onMenuKey = (e) => {
    const its = [...this.menuEl.querySelectorAll("[role=menuitem]")];
    const i = its.indexOf(document.activeElement);
    const to = { ArrowDown: (i + 1) % its.length, ArrowUp: (i - 1 + its.length) % its.length, Home: 0, End: its.length - 1 }[e.key];
    if (to !== undefined) {
      e.preventDefault();
      its[to]?.focus();
    }
  };

  // Arrows move to the nearest card or header in that direction (one tab stop, roving); Enter or Space opens it; the dialog owns Esc.
  onKey = (e) => {
    if (e.target.closest?.(".kb-menu")) {
      return;
    }
    const cur = e.target.closest?.("[data-nav]");
    if (!cur || e.target.closest?.(".qa-answer")) {
      return;
    }
    if ((e.key === "Enter" || e.key === " ") && e.target === cur) {
      e.preventDefault();
      e.stopPropagation();
      this.pick(cur.getAttribute("data-id"));
      return;
    }
    const dir = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
    if (!dir) {
      return;
    }
    e.preventDefault();
    e.stopPropagation();
    const a = cur.getBoundingClientRect();
    const ax = a.x + a.width / 2;
    const ay = a.y + a.height / 2;
    let best = null;
    let score = Infinity;
    // Reads only: one layout, then every box.
    for (const n of this.box.querySelectorAll("[data-nav]")) {
      if (n === cur) {
        continue;
      }
      const b = n.getBoundingClientRect();
      const dx = b.x + b.width / 2 - ax;
      const dy = b.y + b.height / 2 - ay;
      const along = dx * dir[0] + dy * dir[1];
      if (along <= 4) {
        continue;
      }
      const sc = along + Math.abs(dir[0] ? dy : dx) * 3;
      if (sc < score) {
        score = sc;
        best = n;
      }
    }
    if (best) {
      cur.tabIndex = -1;
      best.tabIndex = 0;
      this.cur = best.getAttribute("data-id");
      best.focus();
      best.scrollIntoView?.({ block: "nearest", inline: "nearest" });
    }
  };

  onFocusIn = (e) => {
    const n = e.target.closest?.("[data-nav]");
    if (n && n.getAttribute("data-id") !== this.cur) {
      this.box.querySelectorAll("[data-nav][tabindex='0']").forEach((x) => (x.tabIndex = -1));
      n.tabIndex = 0;
      this.cur = n.getAttribute("data-id");
    }
  };

  card(c, m) {
    return kbCard(c, { on: m.sel === c.id, focusable: c.id === this.tab, pick: (id) => this.pick(id) });
  }

  blCard(r, m) {
    return html`<article key=${r.id} class="kb-card kb-brow glass" data-nav data-id=${r.id} tabindex=${r.id === this.tab ? 0 : -1} aria-current=${m.sel === r.id ? "true" : undefined}
      aria-label=${`Backlog ${r.key}, ${r.title}${UNDER_WAY.has(r.state) ? `, ${STATE_LABEL[r.state]}` : ""}${workLabel(r.workId, r.blockedOpen)}`} onClick=${(e) => !e.target.closest?.("a") && this.pick(r.id)}>
      <div class="kb-top"><span class="kb-ref mono" title=${r.key}>${r.key}${r.kind ? ` / ${r.kind}` : ""}</span>${r.workId ? html`<span class="chip mono" title=${`Work id ${r.workId}`}>${r.workId}</span>` : null}${r.size ? html`<span class="chip" title=${`Size ${r.size}`}>${r.size}</span>` : null}</div>
      <h3 class="kb-title" title=${r.title} data-text=${r.title}>${r.title}</h3>
      ${UNDER_WAY.has(r.state) ? html`<div class="kb-meta"><span class="kb-flag wait">${STATE_LABEL[r.state]}</span>${r.holder ? html`<span class="kb-who">${memberLink(r.holderId, r.holder)}</span>` : null}</div>` : null}
      ${blockedBy(r.blockedOpen)}
    </article>`;
  }

  // The board: only the state columns, one canvas. The stage is the one scroll container (both axes); a column grows with its
  // cards and its head sticks to the top of the stage. An empty column is a 40px strip.
  grid(m) {
    const live = Object.fromEntries(m.cols.map((c) => [c.key, c.count > 0]));
    const tmpl = m.cols.map((c) => (live[c.key] ? "minmax(var(--kb-col-min), 1fr)" : "40px")).join(" ");
    const done = m.cols.find((c) => c.key === "done");
    const out = m.cols.map((c) => {
      if (!live[c.key]) {
        const label = c.key === "backlog" && c.total > 0 ? `Backlog, ${c.total} rows folded` : `${c.label}, nothing here`;
        return c.key === "backlog" && c.total > 0
          ? html`<button key=${`strip-${c.key}`} type="button" class="kb-strip" aria-expanded="false" aria-label=${`Show Backlog, ${c.total} rows`} title=${label} onClick=${() => this.fold("col:backlog")}><span class="kb-stripin"><span class="kb-vl">${c.label}</span><span class="num">${c.total}</span></span></button>`
          : html`<div key=${`strip-${c.key}`} class="kb-strip" title=${label} aria-label=${label} role="note"><span class="kb-stripin" aria-hidden="true"><span class="kb-vl">${c.label}</span><span class="num">0</span></span></div>`;
      }
      const capped = c.count < c.total;
      const body = c.key === "backlog"
        ? m.backlog.map((g) => html`<div key=${g.wave} class="kb-wave"><span>${g.label}</span><span class="num">${g.rows.length === g.total ? g.total : `${g.rows.length} of ${g.total}`}</span></div>
          ${g.rows.map((r) => this.blCard(r, m))}
          ${g.total > WAVE_CAP ? html`<div class="kb-more"><button type="button" class="tab" onClick=${() => this.more(`wave:${g.wave}`)}>${g.open ? "Show fewer" : `Show all ${g.total}`}</button></div>` : null}`)
        : [...m.cells[c.key].map((x) => this.card(x, m)),
          c.key === "done" && (done.count < done.total || done.total > DONE_CAP) ? html`<div key="done-more" class="kb-more"><button type="button" class="tab" onClick=${() => this.more("done")}>${done.count < done.total ? `Show all ${done.total} done today` : "Show fewer"}</button></div>` : null];
      return html`<section key=${`col-${c.key}`} class=${`kb-col glass${c.key === "backlog" ? " kb-blcol" : ""}`} aria-label=${capped ? `${c.label}, ${c.count} of ${c.total} shown` : `${c.label}, ${plural(c.count, "card")}`}>
        ${kbHead(c, capped, m.blScope, (k) => this.fold(k))}
        <div class="kb-cards">${body}</div>
      </section>`;
    });
    return html`<div class="kb" role="region" aria-label="Board, by state" style=${`grid-template-columns:${tmpl};zoom:${this.zoom}`}>${out}</div>`;
  }

  crumbs(m) {
    const last = m.crumbs.length - 1;
    const kids = m.menu.length > 0;
    return html`<nav class="kb-crumbs" aria-label="Board scope"><ol>
      ${m.crumbs.map((it, i) => {
        const inner = html`${it.kind ? html`<span class="kb-kind">${it.kind}</span>` : null}<span class="kb-name">${it.name}</span>`;
        const here = i === last;
        return html`<li key=${it.depth} aria-current=${here ? "location" : undefined}>${i ? html`<span class="kb-sep" aria-hidden="true">›</span>` : null}${here
          ? kids
            ? html`<button type="button" class="kb-pill glass here" ref=${(el) => (this.crumbBtn = el)} popovertarget="kb-menu" aria-haspopup="menu" aria-label=${`${it.name}, you are here. Open the next level`} title=${it.name}>${inner}<span class="kb-caret" aria-hidden="true">▾</span></button>`
            : html`<span class="kb-pill glass here" title=${it.name}>${inner}</span>`
          : it.href
            ? html`<a class="kb-pill glass" href=${it.href} aria-label=${`Go up to ${it.name}`} title=${it.name}>${inner}</a>`
            : html`<button type="button" class="kb-pill glass" aria-label=${`Go up to ${it.name}`} title=${it.name} onClick=${() => this.up(it.depth)}>${inner}</button>`}</li>`;
      })}
    </ol>
    <div id="kb-menu" class="kb-menu" popover="auto" role="menu" aria-label=${m.menuHead} ref=${(el) => {
      if (el && el !== this.menuEl) {
        this.menuEl = el;
        el.addEventListener("toggle", this.onMenuToggle);
      }
    }} onKeyDown=${this.onMenuKey}>
      <div class="kb-mhead">${m.menuHead}</div>
      ${m.menu.map((c) => (c.href
        ? html`<a key=${c.key} role="menuitem" href=${c.href} title=${c.name}><span class="t">${c.name}</span><span class="num">${c.n}</span></a>`
        : html`<button key=${c.key} type="button" role="menuitem" title=${c.name} onClick=${() => this.down(c.key)}><span class="t">${c.kind ? html`<span class="kb-kind">${c.kind}</span> ` : null}${c.name}</span><span class="num">${c.n}</span></button>`))}
    </div></nav>`;
  }

  // `bar` draws PlansView's own filters into this one bar (barKey says when they change), so every filter sits above the board.
  draw({ note = null, bar = null }, s) {
    const m = JSON.parse(s.model);
    this.m = m;
    // One tab stop: the card last moved to, else the selection, else the first.
    const ids = [...m.backlog.flatMap((g) => g.rows.map((r) => r.id)), ...Object.values(m.cells).flat().map((c) => c.id)];
    this.tab = [this.cur, m.sel, ids[0]].find((x) => x && ids.includes(x)) ?? null;
    if (this.refocusCrumb) {
      this.refocusCrumb = false;
      this.refocus = null;
      queueMicrotask(() => this.crumbBtn?.focus());
    }
    return html`<div class="kb-wrap" onKeyDown=${this.onKey}>
      <div class="kb-bar">
        ${this.crumbs(m)}
        ${m.stream ? html`<button type="button" class="tab on" aria-label=${`Showing stream ${m.stream} only. Clear the stream filter`} onClick=${() => this.setUi({ plansStream: null })}>Stream: ${m.stream} <span aria-hidden="true">×</span></button>` : null}
        ${m.owners.length > 0 ? html`<label class="kb-owner">Owner <select value=${m.owner ?? ""} onChange=${(e) => this.setUi({ owner: e.target.value || null })}><option value="">everyone</option>${m.owners.map((o) => html`<option key=${o} value=${o}>${o}</option>`)}</select></label>` : null}
        ${bar ? bar() : null}
        <span class="kb-zoom" role="group" aria-label="Board zoom">
          <button type="button" class="tab" aria-label="Zoom out" title="Zoom out (Ctrl or Cmd and -)" disabled=${this.zoom <= ZOOM_MIN} onClick=${() => this.setZoom(stepZoom(this.zoom, -1))}><span aria-hidden="true">−</span></button>
          <button type="button" class="tab kb-zoomlvl num" aria-label=${`Reset zoom to 100%, now ${Math.round(this.zoom * 100)}%`} title="Reset zoom (Ctrl or Cmd and 0)" onClick=${() => this.setZoom(1)}>${Math.round(this.zoom * 100)}%</button>
          <button type="button" class="tab" aria-label="Zoom in" title="Zoom in (Ctrl or Cmd and +)" disabled=${this.zoom >= ZOOM_MAX} onClick=${() => this.setZoom(stepZoom(this.zoom, 1))}><span aria-hidden="true">+</span></button>
        </span>
      </div>
      ${m.laneHead ? html`<p class="kb-lanehead">
        <b class="mono">${m.laneHead.stream}</b>
        <span>sub-orchestrator ${memberLink(m.laneHead.so.id, m.laneHead.so.name)}</span>
        ${m.laneHead.members.length ? html`<span>members ${m.laneHead.members.map((x, i) => html`${i ? ", " : ""}${memberLink(x.id, x.name)}`)}</span>` : null}
        ${m.laneHead.branch ? html`<span class="mono">${m.laneHead.branch} → ${m.laneHead.base ?? "main"}</span>` : null}
        <span>${m.laneHead.state}</span>
        ${m.laneHead.report ? html`<span class="chip">${m.laneHead.report}</span>` : null}
      </p>` : null}
      ${m.notFound ? html`<p class="kb-note" role="status">Item ${m.notFound} is not on this board.</p>` : null}
      ${note && m.empty ? html`<p class="kb-note kb-sub" role="status">${note}</p>` : null}
      <div class="kb-body">
        <div class="kb-stage" style=${`--kb-zoom:${this.zoom}`} ref=${this.stageRef} onFocusIn=${this.onFocusIn} onPointerEnter=${() => (this.hovered = true)} onPointerLeave=${() => (this.hovered = false)}>${m.empty ? null : this.grid(m)}</div>
      </div>
      ${m.panel ? html`<${ItemDialog} p=${m.panel} store=${this.props.store} onClose=${this.close} onPick=${this.pick} returnTo=".kb-stage [data-nav]" lead=${panelForm(m.panel)}>${m.panel.hist ? historyView(this.state.history) : null}</${ItemDialog}>` : null}
    </div>`;
  }
}
