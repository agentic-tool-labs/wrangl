import { html } from "./h.js";
import { Live, differ } from "./live-component.js";
import { Age, EtaChip } from "./ticker.js";
import { ageText, ageOf } from "./store.js";
import { teamRows } from "./select.js";
import { TeamCloseButton, TeamCloseNote } from "./team-close.js";
import { memberLink } from "./member-summary.js";
// ponytail: board-r2 made the Board kanban only, so BoardIcicle (board-icicle.js) is unreachable from here; the file stays for
// historyView, which the kanban's panel shares, and for its own tests. Delete the component in a cleanup.
import { BoardKanban, WAVE_CAP, backlogColumn, durText, lanesOf, laneOfCard } from "./board-kanban.js";
import { loadTrack, marksOf, saveTrack, snapshotOf } from "./plans-seen.js";
import { CHECKLIST_STATES, COLUMNS, DAY_MS, DONE_CAP, backlogId, boardOf, checklistRows, queueRows, streamName, STATE_LABEL, CHANGED_LEASE_CAP, GROUPS, STATUS_LABEL, groupsOf, choose, cycle, itemsId, planIdOf, prefOf, readPref, sortTeams, teamModel } from "./plans-model.js";

// Plans: every team's streams (All teams) or one team's. A stream's counts come from its `plan:` slice, which every covered
// team holds while the view is open; its items come from `plan-items:`, held only for the streams the view shows them for.
// Whatever the clock decides (stale, ended) is computed here from timestamps, so no slice changes as time passes.

const ageSince = (ms) => (now) => (ms > 0 ? ageText(ageOf({ as_of: ms }, {}, now)) : "—");

const GLYPH = { done: "✓", in_progress: "◐", blocked: "⚠", waiting: "⚠", review: "◑", not_started: "○" };

export { choose, cycle, prefOf };

// The label every row and card is read by: which team, which repo, which branch.
export const labelOf = (s) => [s.teamLabel ?? s.team, s.repo, s.branch].filter(Boolean).join(" · ");

const percent = (n, total) => (total > 0 ? `${Math.round((n / total) * 1000) / 10}%` : "0%");

const Bar = ({ s }) => {
  const c = s.counts;
  const attn = c.blocked + c.waiting_user;
  return html`<span class="pl-bar" aria-hidden="true">
    <i class="ok" style=${{ width: percent(c.done, s.total) }}></i><i class="work" style=${{ width: percent(c.in_progress + c.waiting_orch, s.total) }}></i><i class="attn" style=${{ width: percent(attn, s.total) }}></i>
  </span>`;
};

const stateChip = (s) => {
  return { cls: s.status, text: `${s.review && s.state !== "done" ? "in review" : s.state === "done" ? "done" : STATUS_LABEL[s.status].toLowerCase()} · ${s.counts.done}/${s.total}` };
};

const SEEN_SUFFIX = ", changed since your last look";
const deltaText = (m) => [m.new > 0 ? `${m.new} new` : null, m.moved > 0 ? `${m.moved} moved` : null, m.done > 0 ? `${m.done} done` : null, m.removed > 0 ? `${m.removed} removed` : null].filter(Boolean).join(" · ");
const Dot = () => html`<span class="pl-mark" aria-hidden="true"></span>`;

const StreamRow = ({ s, showTeam, open, onOpen, onBoard }) => {
  const chip = stateChip(s);
  const delta = deltaText(s.mark);
  return html`<li class=${`pl-row${open ? " open" : ""}${s.state === "done" ? " done" : ""}${s.teamStatus === "live" ? "" : ` ${s.teamStatus}`}`}>
    <button class="pl-main" data-row aria-pressed=${open} aria-label=${`${streamName(s.stream)}, ${labelOf(s)}, ${chip.text}${s.mark.changed ? SEEN_SUFFIX : ""}`} onClick=${() => onOpen(s.id)}>
      <span class=${`pl-glyph st-${s.status}`} aria-hidden="true">${GLYPH[s.status]}</span>
      <span class="pl-name"><b>${s.mark.changed ? html`<${Dot} />` : null}${streamName(s.stream)}</b>${s.goal ? html`<span class="pl-goal">${s.goal}</span>` : null}${delta ? html`<span class="pl-delta">${delta}</span>` : null}</span>
      ${showTeam ? html`<span class="pl-team">${s.teamLabel ?? s.team}</span>` : null}
      <span class="pl-owner" data-role=${s.role || undefined} title=${s.ownerTitle !== s.ownerLabel ? s.ownerTitle : undefined}>${s.ownerLabel || "—"}</span>
      <${Bar} s=${s} />
      <span class=${`chip pl-chip st-${chip.cls}`}>${chip.text}</span>
      <span class="pl-branch mono">${s.branch || "—"}</span>
      <span class="pl-age"><${Age} text=${ageSince(s.asOf)} /></span>
    </button>
    <button type="button" class="tab pl-toboard" aria-label=${`Open ${streamName(s.stream)} in board`} onClick=${() => onBoard(s)}>Open in board</button>
  </li>`;
};

const TeamHead = ({ t, count, expanded, onToggle, changed }) => html`<div class=${`pl-head team ${t.status}`}>
  <span class=${t.status === "live" ? "dot live" : "dot"} aria-hidden="true"></span>
  <b class="pl-title">${t.label}</b>
  <span class="chip">${t.held ? "Held · " : ""}${t.status === "live" ? `live · ${t.members} ${t.members === 1 ? "member" : "members"}` : t.status === "ended" ? "ended" : html`${t.members > 0 ? `${t.members} ${t.members === 1 ? "member" : "members"}` : "no members"} · stale <${Age} text=${ageSince(t.updated)} />`}</span>
  <span class="mono pl-where">${[t.repo, t.branch].filter(Boolean).join(" · ") || "—"}</span>
  <span class="pl-tally">${t.planLoaded === false ? "not loaded yet" : `${t.itemsDone} / ${t.itemsTotal}`}</span>
  ${changed > 0 ? html`<span class="chip pl-changed">${changed} changed</span>` : null}
  ${t.waitingOnYou > 0 ? html`<span class="chip attn">${t.waitingOnYou} waiting on you</span>` : null}
  ${onToggle ? html`<button class="tab pl-fold" aria-expanded=${expanded} onClick=${onToggle}>${expanded ? "▾ hide" : "▸"} ${count} ${count === 1 ? "stream" : "streams"}</button>` : null}
</div>`;

// A plan holder or owner (a member name, or already a member id) as its member id in the stream's team.
const ownerId = (s, who) => (who.startsWith("member:") ? who : s.dirId && s.team ? `member:${s.dirId}/${s.team}/${who}` : null);

const Detail = ({ s, items, truncated, onClose, changesOnly, showAll, onShowAll }) => html`<aside class="pl-detail" aria-label=${`Stream ${streamName(s.stream)}`}>
  <button class="close" aria-label="Close detail" onClick=${onClose}>×</button>
  <p class="pl-where mono">${[s.teamLabel ?? s.team, s.repo].filter(Boolean).join(" · ")}</p>
  <h2>${streamName(s.stream)}</h2>
  ${s.goal ? html`<p class="pl-goal-full">${s.goal}</p>` : null}
  <dl>
    <dt>owner</dt><dd data-role=${s.role || undefined}>${s.owner ? memberLink(ownerId(s, s.owner), s.ownerLabel ?? s.owner) : "—"}</dd>
    <dt>branch</dt><dd class="mono">${s.branch || "—"}</dd>
    <dt>work tree</dt><dd class="mono">${s.workTree ?? "—"}</dd>
    <dt>updated</dt><dd>${s.asOf > 0 ? html`<${Age} text=${ageSince(s.asOf)} />` : "—"}</dd>
  </dl>
  ${s.blockers > 0 ? html`<p class="chip attn">${s.blockers} ${s.blockers === 1 ? "blocker" : "blockers"}</p>` : null}
  ${s.warnings > 0 ? html`<p class="chip warn">${s.warnings} ${s.warnings === 1 ? "warning" : "warnings"}</p>` : null}
  <h3>Items</h3>
  ${s.mark.removed > 0 ? html`<p class="pl-delta">${s.mark.removed} removed</p>` : null}
  ${items !== null && changesOnly && !showAll ? html`<button class="tab pl-showall" onClick=${onShowAll}>Show all items</button>` : null}
  ${items === null
    ? html`<p class="dim">Loading…</p>`
    : items.length === 0
      ? html`<p class="dim">no items</p>`
      : html`<ul class="pl-items">${(changesOnly && !showAll ? items.filter((i) => i.mark) : items).map((i) => html`<li key=${i.key} class=${`st-${i.state}`}><span class="pl-glyph" aria-hidden="true">${GLYPH[i.state === "next" || i.state === "not_started" ? "not_started" : i.state === "blocked" || i.state === "waiting_user" ? "blocked" : i.state === "waiting_orch" ? "in_progress" : i.state] ?? "○"}</span>${i.mark ? html`<${Dot} />` : null}<span class="t">${i.title}</span><span class="sr"> ${STATE_LABEL[i.state] ?? i.state}${i.mark?.kind === "moved" ? `, moved from ${STATE_LABEL[i.mark.from] ?? i.mark.from}` : i.mark ? SEEN_SUFFIX : ""}</span>${i.mark?.kind === "moved" ? html`<span class="pl-moved">${STATE_LABEL[i.mark.from] ?? i.mark.from} → ${STATE_LABEL[i.mark.to] ?? i.mark.to}</span>` : i.mark ? html`<span class="pl-moved">${i.mark.kind === "new" ? "new" : "done since"}</span>` : null}${i.holder ? html`<span class="mono dim">${memberLink(ownerId(s, i.holder), i.holder)}</span>` : null}</li>`)}</ul>`}
  ${truncated ? html`<p class="dim">Only the first items are listed.</p>` : null}
</aside>`;

// The List's item columns (§2.5): State and Item always; the rest only when some visible row has a value (C1).
const ITEM_COLS = { owner: "Owner", role: "Role", todo: "Next action", since: "Since", updated: "Updated", refs: "Request", waiting: "Waiting", could: "Could take it", last: "Last sent" };
const BY_STATE = new Map(CHECKLIST_STATES.map((x) => [x.state, x]));
const NONE = "—";

// One stream's open items, in a table whose columns line up with every other stream's; the first table of a group shows its head.
const ItemTable = ({ s, cols, first, lastHead, target }) => html`<li class="pl-itemrow">
  ${s.items === null
    ? html`<p class="pl-none pl-sub">Loading…</p>`
    : html`<table class="pl-itable" aria-label=${`${streamName(s.stream)}: open items`}>
      <colgroup><col class="c-state" /><col class="c-item" />${cols.map((k) => html`<col key=${k} class=${`c-${k}`} />`)}</colgroup>
      <thead class=${first ? "" : "sr"}><tr><th scope="col">State</th><th scope="col">Item</th>${cols.map((k) => html`<th key=${k} scope="col">${k === "last" ? lastHead : ITEM_COLS[k]}</th>`)}</tr></thead>
      <tbody>${s.items.map((r) => {
        const st = BY_STATE.get(r.state);
        const cell = {
          owner: r.owner ? memberLink(r.ownerId, r.owner) : r.queued ? `waiting for ${r.queued.role ?? "a member"}` : NONE,
          role: r.role ?? NONE,
          todo: r.todo ?? NONE,
          since: r.stateSince ? html`<${Age} key=${r.stateSince} text=${ageSince(r.stateSince)} />` : NONE,
          updated: r.updatedAt ? html`<${Age} key=${r.updatedAt} text=${ageSince(r.updatedAt)} />` : NONE,
          refs: r.refs.length > 0 || r.eta ? html`${r.refs.length > 0 ? html`<span class="mono" title=${r.refs.join(", ")}>${r.refs.join(" ")}</span>` : null}${r.eta ? html` <${EtaChip} b=${r.eta} />` : null}` : NONE,
          waiting: r.queued ? html`<span class=${r.queued.stalled ? "pl-stalled" : ""}>${r.queued.wait}</span>` : NONE,
          could: r.queued ? (r.queued.could.length > 0 ? r.queued.could.join(", ") : "none idle") : NONE,
          last: r.queued ? r.queued.last : NONE,
        };
        return html`<tr key=${r.key} class=${`pl-irow st-${r.state}${r.key === target ? " on" : ""}`} data-key=${r.key} aria-current=${r.key === target ? "true" : undefined}>
          <td class=${st.attn ? "pl-istate attn" : "pl-istate"}><span aria-hidden="true">${st.glyph} </span>${st.label}</td>
          <td class="pl-ititle">${r.mark ? html`<${Dot} />` : null}${r.title}${r.mark ? html`<span class="sr">${r.mark.kind === "moved" ? `, moved from ${STATE_LABEL[r.mark.from] ?? r.mark.from}` : SEEN_SUFFIX}</span>` : null}</td>
          ${cols.map((k) => html`<td key=${k} class=${`pl-i${k}`}>${cell[k]}</td>`)}
        </tr>`;
      })}</tbody>
    </table>`}
</li>`;

const listHead = (h) => [`${h.open} open ${h.open === 1 ? "item" : "items"}`, h.blocked ? `${h.blocked} blocked` : null, h.waitingOrch ? `${h.waitingOrch} waiting-orch` : null, h.waitingYou ? `${h.waitingYou} waiting on you` : null, `${h.dispatched} dispatched`, `${h.queued} queued`].filter(Boolean).join(" · ");

export class PlansView extends Live {
  stored = { plansGroup: readPref("plansGroup"), plansLayout: readPref("plansLayout"), plansChanges: readPref("plansChanges"), boardFinished: readPref("boardFinished") };

  // What the viewer saw last time, read once when the view opens and held fixed, so a mark does not go as the viewer looks at it.
  track = loadTrack(Date.now());
  cache = new Map();
  opened = new Set();
  handledSeq = this.props.store.state.ui.plansMarkSeen ?? 0;

  // Everything the view draws and holds, from one snapshot. Both the selection and the leases read it, so it is made once per snapshot.
  derive(snap, { teamId }) {
    if (this.memo?.snap === snap && this.memo.teamId === teamId) {
      return this.memo.out;
    }
    const now = Math.floor(Date.now() / 60_000) * 60_000;
    const { ui, slices } = snap;
    const group = prefOf(ui, "plansGroup", this.stored);
    const layout = prefOf(ui, "plansLayout", this.stored);
    const hideEnded = ui.plansHideEnded !== false && !teamId;
    const query = ui.plansQuery ?? "";
    // A team's Board also holds its lanes' child teams (§2.2), so All and each lane can show their plan items.
    const laneTeams = new Set(teamId ? lanesOf(slices.get(teamId)?.body).map((l) => l.child).filter(Boolean) : []);
    const rows = teamRows(slices).filter((r) => !teamId || r.id === teamId || laneTeams.has(r.id));
    const all = rows.map((r) => teamModel(r, slices.get(planIdOf(r))?.body, now));
    const dup = (shown) => all.filter((t) => t.shown === shown).length > 1;
    const teams = sortTeams(all.filter((t) => !(hideEnded && t.status === "ended"))).map((t) => ({ ...t, label: dup(t.shown) ? `${t.shown} · ${t.repo ?? t.dirId.slice(0, 6)}` : t.shown }));
    const changesOnly = prefOf(ui, "plansChanges", this.stored) === "on";
    const scopeOf = (s) => `${s.dirId}/${s.team}`;
    const sinceOf = (s) => (this.track.blocked ? null : this.track.look[scopeOf(s)] ?? null);
    const held = (s) => {
      const items = slices.get(itemsId(s))?.body.items;
      if (items) {
        this.cache.set(s.id, items);
      }
      return items ?? this.cache.get(s.id) ?? null;
    };
    const marks = new Map(teams.flatMap((t) => t.streams).map((s) => [s.id, marksOf(s, { since: sinceOf(s), snapshot: this.track.seen[scopeOf(s)]?.[s.stream], items: held(s) })]));
    const withMark = (s) => {
      const m = marks.get(s.id);
      return { ...s, mark: { changed: m.changed, stream: m.stream, new: [...m.items.values()].filter((x) => x.kind === "new").length, moved: [...m.items.values()].filter((x) => x.kind === "moved").length, done: [...m.items.values()].filter((x) => x.kind === "done").length, removed: m.removed } };
    };
    const marked = teams.map((t) => ({ ...t, streams: t.streams.map(withMark) }));
    this.covered = marked;
    // Stream aging (§2.4): a stream done before today, or empty with no holder and no update for 24 h, is hidden unless the viewer
    // shows finished or empty streams. A stream done today stays, so its items reach Done today.
    const showFinished = prefOf(ui, "boardFinished", this.stored) === "on";
    const dayStart0 = new Date(now).setHours(0, 0, 0, 0);
    const aged = (s) => (s.state === "done" && s.asOf < dayStart0) || (s.total === 0 && !s.owner && now - s.asOf > DAY_MS);
    const agedCount = marked.reduce((n, t) => n + t.streams.filter(aged).length, 0);
    const kept = showFinished ? marked : marked.map((t) => ({ ...t, streams: t.streams.filter((s) => !aged(s)) }));
    const shownTeams = changesOnly ? kept.map((t) => ({ ...t, streams: t.streams.filter((s) => s.mark.changed) })) : kept;
    const streams = marked.flatMap((t) => t.streams);
    const open = streams.find((s) => s.id === ui.plansOpen) ?? null;
    const titlesOf = (s) => slices.get(itemsId(s))?.body.items?.map((i) => i.title) ?? null;
    const sinces = marked.map((t) => (this.track.blocked ? null : this.track.look[`${t.dirId}/${t.name}`] ?? null));
    const since = sinces.includes(null) ? null : Math.min(...sinces);
    const itemMark = (s, item) => marks.get(s.id)?.items.get(item.key) ?? null;
    const staleOpen = ui.plansStale ?? [];
    const dayStart = new Date(now).setHours(0, 0, 0, 0);
    const blockedOnly = ui.plansBlocked === true;
    const projects = ui.plansProjects === true;
    // The board's cards by state column, from the one board model: one lane, Done capped as on every board.
    const lane = layout === "board" ? boardOf(shownTeams, { itemsOf: held, lanes: "none", query, dayStart, expanded: staleOpen, markOf: itemMark, changesOnly, blockedOnly, showFinished }) : null;
    const cards = lane?.lanes[0] ?? null;
    // The kanban draws its own scope (team, lane, epic, deliverable) from every card.
    const lanes = teamId ? lanesOf(slices.get(teamId)?.body) : [];
    const cells = lane ? Object.fromEntries(COLUMNS.map(([c]) => [c, cards?.cells[c] ?? []])) : null;
    const board = lane && {
      // Uncapped: the board filters first and caps Done after, so its head counts what the filter leaves.
      cells,
      caps: { done: DONE_CAP },
      stale: lane.stale,
      count: Object.values(cells).reduce((n, cs) => n + cs.length, 0),
    };
    // The List (§2.5): the streams as group heads, each with its open items in the Checklist's attention order, joined to the queue
    // (Waiting, Could take it) and to the open exchanges its refs name (the ETA chip). The lane filter cuts its streams as the Board's.
    const mk = teamId ? teamId.slice("team:".length) : null;
    const listTeams = layout === "list" && lanes.length > 0 && ui.lane ? shownTeams.map((t) => ({ ...t, streams: t.streams.filter((s) => laneOfCard({ dirId: s.dirId, team: s.team, stream: s.stream }, lanes, mk) === ui.lane) })) : shownTeams;
    const queueBy = new Map();
    const openIds = new Set();
    const listKeys = layout === "list" ? [...new Set(listTeams.map((t) => `${t.dirId}/${t.name}`))] : [];
    for (const k of listKeys) {
      for (const q of queueRows(slices.get(`queue:${k}`)?.body ?? [], now)) {
        queueBy.set(`${k}|${q.item}`, q);
      }
      for (const o of slices.get(`list:open:team:${k}`)?.body.open ?? []) {
        openIds.add(o.id);
      }
    }
    const linked = (s, refs) => refs.map((ref) => `exchange:${s.dirId}/${ref}`).filter((id) => openIds.has(id));
    const etaOf = (s, refs) => {
      const b = linked(s, refs).map((id) => slices.get(id)?.body).find((x) => x?.state);
      return b ? { state: b.state, due_at: b.due_at, overdue_by: b.overdue_by } : null;
    };
    const itemRowsOf = (s) => {
      const items = held(s);
      if (items === null) {
        return null;
      }
      return checklistRows([s], () => items, etaOf).map((r) => {
        const raw = r.key.slice(s.id.length + 1);
        const bare = raw.startsWith(`${s.stream}:`) ? raw.slice(s.stream.length + 1) : raw;
        const q = queueBy.get(`${s.dirId}/${s.team}|${s.stream}/${bare}`) ?? null;
        const stalled = q ? q.stalled && q.eligible.length > 0 : false;
        // Waiting: stalled since, or waiting with the time left to the server's stall time (the Queue's countdown), or none served.
        const wait = q ? `${stalled ? "stalled" : "waiting"}${q.queuedSince ? ` ${durText(now - q.queuedSince)}` : ""}${stalled ? "" : q.stallAt === null ? " · no stall time" : q.stallAt > now ? ` · stalls in ${durText(q.stallAt - now)}` : ""}` : null;
        return { ...r, raw, bare, ownerId: r.owner ? ownerId(s, r.owner) : null, mark: itemMark(s, { key: raw }), queued: q ? { wait, stalled, role: q.role, could: q.eligible, last: q.lastDispatch ? `${durText(now - q.lastDispatch)} ago` : "never" } : null };
      }).filter((r) => !changesOnly || r.mark);
    };
    const withItems = (s) => ({ ...s, items: itemRowsOf(s) });
    const groups = layout === "list" ? groupsOf(group, listTeams, { query, titlesOf, onlyMatches: changesOnly }).map((g) => ({ ...(g.team ? { ...g, team: { ...g.team, streams: undefined } } : g), rows: g.rows.map(withItems), done: g.done.map(withItems) })) : [];
    const listRows = groups.flatMap((g) => g.rows.flatMap((x) => x.items ?? []));
    const has = { owner: (r) => r.owner || r.queued, role: (r) => r.role, todo: (r) => r.todo, since: (r) => r.stateSince, updated: (r) => r.updatedAt, refs: (r) => r.refs.length > 0 || r.eta, waiting: (r) => r.queued, could: (r) => r.queued, last: (r) => r.queued };
    // "Last sent to <role>" (§2.3, the Queue's Last dispatch): the role when every queued row waits for the same one.
    const lastRoles = [...new Set(listRows.filter((r) => r.queued).map((r) => r.queued.role).filter(Boolean))];
    // item= in the List (§2.6): the row it names, scrolled to; an exchange id goes through its own item field.
    const want = ui.item?.startsWith("exchange:") ? slices.get(ui.item)?.body?.item ?? null : ui.item ?? null;
    const listTarget = layout === "list" && want ? groups.flatMap((g) => [...g.rows, ...g.done]).flatMap((x) => (x.items ?? []).map((r) => ({ x, r }))).find(({ x, r }) => [r.raw, r.bare, `${x.stream}:${r.bare}`, `${x.stream}/${r.bare}`].includes(want))?.r.key ?? null : null;
    const listMissing = layout === "list" && ui.item && !listTarget && snap.ready && listRows.length > 0 && (!ui.item.startsWith("exchange:") || slices.has(ui.item) || snap.gone?.has(ui.item) === true) ? ui.item : null;
    const count = (f) => listRows.filter(f).length;
    // The Backlog group, first: the same rows as the Board's Backlog column, whole directory.
    const blDir = mk ? mk.split("/")[0] : listTeams[0]?.dirId ?? null;
    const q0 = query.trim().toLowerCase();
    // Each wave capped as the Board's Backlog column is (WAVE_CAP, "N of M"), sharing its Show all choice.
    const blAll = ui.boardAll ?? [];
    const blRows = layout === "list" && blDir && !changesOnly ? backlogColumn(slices.get(backlogId(blDir))?.body).map((g) => {
      const rows = g.rows.filter((r) => !q0 || [r.key, r.title].some((v) => v.toLowerCase().includes(q0)));
      const shown = blAll.includes(`wave:${g.wave}`) ? rows : rows.slice(0, WAVE_CAP);
      return { wave: g.wave, label: g.label, total: rows.length, open: blAll.includes(`wave:${g.wave}`), rows: shown.map((r) => ({ key: r.key, title: r.title, state: r.state, size: r.size ?? null, holder: r.holder ?? null, holderId: r.holder && mk ? `member:${mk}/${r.holder}` : null })) };
    }).filter((g) => g.total > 0) : [];
    const list = layout === "list" ? {
      cols: Object.keys(has).filter((k) => listRows.some(has[k])),
      lastHead: `Last sent to ${lastRoles.length === 1 ? lastRoles[0] : "role"}`,
      target: listTarget,
      missing: listMissing,
      head: { open: listRows.length, blocked: count((r) => r.state === "blocked"), waitingOrch: count((r) => r.state === "waiting_orch"), waitingYou: count((r) => r.state === "waiting_user"), dispatched: count((r) => r.state === "in_progress"), queued: count((r) => r.state === "next" || r.state === "not_started") },
      backlog: blRows,
      truncated: listTeams.some((t) => t.streams.some((x) => slices.get(itemsId(x))?.body.truncated === true)),
    } : null;
    const itemsBody = open ? slices.get(itemsId(open))?.body : undefined;
    const out = {
      leases: [...new Set([
        ...teams.map((t) => `plan:${t.dirId}/${t.name}`),
        ...[...laneTeams].map((id) => `plan:${id.slice("team:".length)}`),
        ...teams.filter((t) => t.status === "live" || staleOpen.includes(t.id)).flatMap((t) => t.streams.filter((s) => s.state !== "done" || s.asOf >= dayStart).map(itemsId)),
        ...listKeys.flatMap((k) => [`queue:${k}`, `list:open:team:${k}`]),
        ...(layout === "list" && blDir ? [backlogId(blDir)] : []),
        ...listTeams.flatMap((t) => t.streams.flatMap((x) => (held(x) ?? []).flatMap((i) => linked(x, i.refs ?? [])))),
        ...(open ? [itemsId(open)] : []),
        ...(layout === "list" ? streams.filter((s) => s.mark.stream && !this.opened.has(s.id) && s.state !== "done").sort((a, b) => b.asOf - a.asOf).slice(0, CHANGED_LEASE_CAP).map(itemsId) : []),
      ])],
      model: {
        group,
        layout,
        hideEnded,
        query,
        hiddenEnded: all.length - teams.length,
        groups,
        list,
        open,
        items: open ? (held(open)?.map((i) => ({ ...i, mark: itemMark(open, i) })) ?? null) : null,
        changesOnly,
        showAll: ui.plansAllItems === true,
        tracking: !this.track.blocked,
        since,
        truncated: itemsBody?.truncated === true,
        folded: ui.plansDone ?? [],
        staleOpen,
        blockedOnly,
        projects,
        board,
        // Finished lanes count too, unless their plan stream is already counted as aged.
        agedCount: agedCount + lanes.filter((l) => l.state === "done" && !streams.some((x) => x.stream === l.stream && aged(x))).length,
        showFinished,
        lanes: layout === "list" ? lanes.filter((l) => showFinished || l.state !== "done" || l.stream === ui.lane).map((l) => [l.stream, `${l.stream}${l.child ? "" : " (not confirmed)"}`]) : [],
        lane: ui.lane ?? "",
        anyMarked: streams.some((s) => s.mark.changed),
        empty: streams.length === 0,
        ready: snap.ready,
      },
    };
    this.memo = { snap, teamId, out };
    return out;
  }

  select(snap, props) {
    const { model } = this.derive(snap, props);
    return { scope: props.teamId ?? "", model: JSON.stringify(model), seq: snap.ui.plansMarkSeen ?? 0 };
  }

  leaseIds(snap, props) {
    return this.derive(snap, props).leases;
  }

  // Once a minute the view works out its snapshot again with the clock as it is: the day boundary, the stale line and the leases
  // that follow from them move with time. It reads only what the store holds and asks for nothing.
  tick = () => {
    this.memo = null;
    const store = this.props.store;
    const next = this.select(store.state, this.props);
    if (differ(next, this.sel ?? {})) {
      this.sel = next;
      this.forceUpdate();
    }
    this.holdLease(store.state);
  };

  componentDidMount() {
    super.componentDidMount();
    this.timer = setInterval(this.tick, 60_000);
    globalThis.addEventListener?.("pagehide", this.save);
  }

  componentDidUpdate() {
    super.componentDidUpdate();
    if (this.sel.seq !== this.handledSeq) {
      this.handledSeq = this.sel.seq;
      this.markAllSeen();
    }
    // An item deep link in the List scrolls its row into view once, when the row is first drawn.
    const target = this.derive(this.props.store.state, this.props).model.list?.target ?? null;
    if (target && this.scrolledTo !== target) {
      const row = [...(globalThis.document?.querySelectorAll?.("tr.pl-irow") ?? [])].find((tr) => tr.getAttribute("data-key") === target);
      if (row) {
        this.scrolledTo = target;
        row.scrollIntoView?.({ block: "center" });
      }
    }
  }

  componentWillUnmount() {
    clearInterval(this.timer);
    globalThis.removeEventListener?.("pagehide", this.save);
    this.save();
    super.componentWillUnmount();
  }

  // Writes the time of this look for each team whose plan was held, and the items held of each of its streams. A stream whose items
  // were not held keeps what was stored before, unless `reset` says everything is seen: then it is recorded with no items. Only
  // `reset` changes what this visit compares against; any other save writes a copy, so a page that comes back from the
  // back-forward cache still shows its marks.
  save = (reset = false) => {
    const now = Date.now();
    const out = reset === true ? this.track : { look: { ...this.track.look }, seen: JSON.parse(JSON.stringify(this.track.seen)) };
    for (const t of this.covered ?? []) {
      if (!t.hasPlan) {
        continue;
      }
      const scope = `${t.dirId}/${t.name}`;
      out.look[scope] = now;
      for (const s of t.streams) {
        const items = this.cache.get(s.id) ?? null;
        if (items !== null || reset === true) {
          ((out.seen[scope] ??= {})[s.stream] = snapshotOf(s.stream, s.asOf, items));
        }
      }
    }
    if (reset === true) {
      this.track.mountedAt = now;
    }
    return saveTrack(out);
  };

  // Every mark clears and the clearing is stored at once.
  markAllSeen = () => {
    this.save(true);
    this.tick();
  };

  setUi = (patch) => this.props.store.setUi(patch);
  // List → Board: the board filtered to the stream, its team selected at All teams so its tiles light up in every column.
  toBoard = (s) => {
    choose(this.props.store, "plansLayout", "board");
    this.setUi({ plansStream: `s:${s.dirId}/${s.team}|${s.stream}`, plansFocus: null, plansSel: this.props.teamId ? null : `t:${s.dirId}/${s.team}`, plansOpen: null });
  };
  open = (id) => {
    this.opened.add(id);
    this.setUi({ plansOpen: this.props.store.state.ui.plansOpen === id ? null : id, plansAllItems: false });
  };
  fold = (key) => {
    const cur = new Set(this.props.store.state.ui.plansDone ?? []);
    cur.has(key) ? cur.delete(key) : cur.add(key);
    this.setUi({ plansDone: [...cur] });
  };

  staleToggle = (id) => {
    const cur = new Set(this.props.store.state.ui.plansStale ?? []);
    cur.has(id) ? cur.delete(id) : cur.add(id);
    this.setUi({ plansStale: [...cur] });
  };

  // What an empty list or board says: not loaded yet, nothing matches the filter, tracking is off, or nothing changed.
  nothing(m, noMatch, none) {
    if (!m.ready) {
      return "Loading…";
    }
    if (m.changesOnly && !m.tracking) {
      return "Change tracking needs browser storage";
    }
    if (m.changesOnly && !m.query) {
      return m.since === null ? "Nothing to compare yet: this is your first visit" : html`Nothing changed since <${Age} text=${ageSince(m.since)} />`;
    }
    return m.query ? noMatch : none;
  }

  // The filters: in the List's bar, or drawn by the Board into its one bar above the columns (with Show finished, the List's footer).
  barControls(m) {
    const { store, teamId } = this.props;
    return html`<input data-search type="search" class="plans-search" placeholder="Filter streams" aria-label="Filter streams" value=${m.query} onInput=${(e) => this.setUi({ plansQuery: e.target.value })} />
        <span class="plans-layout" role="group" aria-label="Layout">${[["list", "List"], ["board", "Board"]].map(([k, label]) => html`<button key=${k} class=${m.layout === k ? "tab on" : "tab"} aria-pressed=${m.layout === k} onClick=${() => {
          this.setUi({ item: null });
          choose(store, "plansLayout", k);
        }}>${label}</button>`)}</span>
        ${m.layout === "list"
          ? html`<span class="plans-group" role="group" aria-label="Group by"><span class="dim plans-group-label" aria-hidden="true">Group by</span>${GROUPS.map(([k, label]) => html`<button key=${k} class=${m.group === k ? "tab on" : "tab"} aria-pressed=${m.group === k} onClick=${() => choose(store, "plansGroup", k)}>${label}</button>`)}</span>`
          : null}
        <button class=${m.changesOnly ? "tab on" : "tab"} aria-pressed=${m.changesOnly} onClick=${() => choose(store, "plansChanges", m.changesOnly ? "off" : "on")}>Changed since last visit</button>
        ${m.lanes.length > 0 && m.layout === "list" ? html`<label class="plans-hide">Work of <select value=${m.lane} onChange=${(e) => this.setUi({ lane: e.target.value || null })}><option value="">All</option><option value="top">Top team</option>${m.lanes.map(([k, label]) => html`<option key=${k} value=${k}>${label}</option>`)}</select></label>` : null}
        ${m.layout === "board" ? html`<button class=${m.blockedOnly ? "tab on" : "tab"} aria-pressed=${m.blockedOnly} onClick=${() => this.setUi({ plansBlocked: !m.blockedOnly })}>Blocked only</button>` : null}
        ${m.layout === "board" ? html`<button class=${m.projects ? "tab on" : "tab"} aria-pressed=${m.projects} onClick=${() => this.setUi({ plansProjects: !m.projects })}>Projects and goals</button>` : null}
        ${m.changesOnly && m.anyMarked ? html`<button class="tab" title="Mark all seen (Shift+A)" onClick=${this.markAllSeen}>Mark all seen</button>` : null}
        ${teamId ? null : html`<label class="plans-hide"><input type="checkbox" checked=${m.hideEnded} onChange=${(e) => this.setUi({ plansHideEnded: e.target.checked })} /> Hide ended teams${m.hideEnded && m.hiddenEnded > 0 ? ` (${m.hiddenEnded})` : ""}</label>`}
        ${m.agedCount > 0 && m.layout === "board" ? html`<button type="button" class="tab" aria-pressed=${m.showFinished} onClick=${() => choose(store, "boardFinished", m.showFinished ? "off" : "on")}>${m.showFinished ? "Hide older streams" : `Show ${m.agedCount} older ${m.agedCount === 1 ? "stream" : "streams"}`}</button>` : null}
        ${teamId && m.layout === "board" ? html`<${TeamCloseButton} store=${store} teamId=${teamId} word=" team" /><${TeamCloseNote} store=${store} />` : null}`;
  }

  boardBar = () => this.barControls(JSON.parse(this.sel.model));

  draw({ store, teamId }, s) {
    const m = JSON.parse(s.model);
    const folded = new Set(m.folded);
    const cols = m.list?.cols ?? [];
    const rowOf = (r, first) => [html`<${StreamRow} key=${r.id} s=${r} showTeam=${m.group !== "team"} open=${m.open?.id === r.id} onOpen=${this.open} onBoard=${this.toBoard} />`, r.items === null || r.items?.length > 0 ? html`<${ItemTable} key=${`${r.id}|items`} s=${r} cols=${cols} first=${first} lastHead=${m.list.lastHead} target=${m.list.target} />` : null];
    const streamRowsOf = (g) => {
      let first = true;
      const once = (r) => {
        const f = first && (r.items === null || r.items?.length > 0);
        first = first && !f;
        return rowOf(r, f);
      };
      const out = g.rows.flatMap(once);
      if (g.done.length > 0) {
        const showing = folded.has(g.key);
        out.push(html`<li key="more" class="pl-more"><button class="tab" aria-expanded=${showing} onClick=${() => this.fold(g.key)}>${showing ? "▾" : "▸"} ${g.done.length} more ${g.done.length === 1 ? "stream" : "streams"} done</button></li>`);
        if (showing) {
          out.push(...g.done.flatMap(once));
        }
      }
      return out;
    };
    return html`<section class="plans" aria-label=${teamId ? "Plans" : "All plans"}>
      ${m.layout === "board" ? null : html`<div class="plans-bar">${this.barControls(m)}</div>`}
      <div class=${`plans-body${m.open && m.layout === "list" ? " with-detail" : ""}`}>
        <div class=${m.layout === "board" ? "plans-main kb-main" : "plans-main"}>
          ${m.layout === "board"
            ? html`<${BoardKanban} store=${store} teamId=${teamId ?? null} board=${JSON.stringify(m.board)} ready=${m.ready} blockedOnly=${m.blockedOnly} projects=${m.projects} changesOnly=${m.changesOnly}
              bar=${this.boardBar} barKey=${s.model}
              note=${m.board.count > 0 ? null : m.blockedOnly && !m.query ? "Nothing is blocked." : this.nothing(m, "No card matches.", "Nothing on the board.")} />`
            : m.groups.length === 0 && m.list.backlog.length === 0
            ? html`<p class="dim skeleton">${this.nothing(m, "No stream matches.", m.empty ? "No plans yet." : "Nothing to show.")}</p>`
            : [html`<p key="c4" class="pl-c4" role="status">${listHead(m.list.head)}${m.list.truncated ? " · the newest items only" : ""}</p>`,
              m.list.missing ? html`<p key="missing" class="pl-c4" role="status">Item ${m.list.missing} is not in this list.</p>` : null,
              m.list.backlog.length > 0 ? html`<section key="backlog" class="pl-backlog" aria-label="Backlog, whole directory">
                <div class="pl-bhead"><b class="pl-btitle">Backlog · whole directory</b><span class="pl-tally">${m.list.backlog.reduce((n, g) => n + g.rows.length, 0)}</span></div>
                <table class="pl-itable pl-btable">
                  <colgroup><col class="c-wave" /><col class="c-item" /><col class="c-bstate" /><col class="c-owner" /><col class="c-size" /></colgroup>
                  <thead><tr><th scope="col">Wave</th><th scope="col">Item</th><th scope="col">State</th><th scope="col">Owner</th><th scope="col">Size</th></tr></thead>
                  ${m.list.backlog.map((g) => html`<tbody key=${g.wave}>${g.rows.map((r) => html`<tr key=${r.key} class="pl-irow"><td>${g.label}</td><td class="pl-ititle" title=${r.title}><span class="mono pl-sub">${r.key}</span> ${r.title}</td><td>${STATE_LABEL[r.state] ?? r.state}</td><td>${r.holder ? memberLink(r.holderId, r.holder) : NONE}</td><td>${r.size ?? NONE}</td></tr>`)}
                    ${g.total > WAVE_CAP ? html`<tr class="pl-bmore"><td class="num">${g.rows.length} of ${g.total}</td><td colspan="4"><button type="button" class="tab" onClick=${() => this.setUi({ boardAll: (store.state.ui.boardAll ?? []).includes(`wave:${g.wave}`) ? store.state.ui.boardAll.filter((x) => x !== `wave:${g.wave}`) : [...(store.state.ui.boardAll ?? []), `wave:${g.wave}`] })}>${g.open ? `Show fewer in ${g.label}` : `Show all ${g.total} in ${g.label}`}</button></td></tr>` : null}</tbody>`)}
                </table>
              </section>` : null,
              ...m.groups.map((g) => {
              const count = g.rows.length + g.done.length;
              const changed = [...g.rows, ...g.done].filter((r) => r.mark.changed).length;
              const quiet = !teamId && g.team && g.team.status !== "live";
              const shown = !quiet || m.staleOpen.includes(g.key);
              return html`<section key=${g.key} class=${`pl-group${quiet ? ` ${g.team.status}` : ""}`} aria-label=${g.label}>
                ${g.team ? html`<${TeamHead} t=${g.team} count=${count} expanded=${shown} changed=${changed} onToggle=${quiet && count > 0 ? () => this.staleToggle(g.key) : null} />` : html`<div class="pl-head"><b class="pl-title">${g.label}</b>${changed > 0 ? html`<span class="chip pl-changed">${changed} changed</span>` : null}<span class="pl-tally">${count}</span></div>`}
                ${!shown ? null : count === 0 ? html`<p class="dim pl-none">${g.team?.hasPlan ? "No streams." : "No plan yet."}</p>` : html`<ul class="pl-list">${streamRowsOf(g)}</ul>`}
              </section>`;
            })]}
        </div>
        ${m.open && m.layout === "list" ? html`<${Detail} s=${m.open} items=${m.items} truncated=${m.truncated} changesOnly=${m.changesOnly} showAll=${m.showAll} onShowAll=${() => this.setUi({ plansAllItems: true })} onClose=${() => this.setUi({ plansOpen: null })} />` : null}
      </div>
      ${m.agedCount > 0 && m.layout !== "board" ? html`<p class="pl-foot"><button type="button" class="tab" aria-pressed=${m.showFinished} onClick=${() => choose(store, "boardFinished", m.showFinished ? "off" : "on")}>${m.showFinished ? "Hide older streams" : `Show ${m.agedCount} older ${m.agedCount === 1 ? "stream" : "streams"}`}</button></p>` : null}
    </section>`;
  }
}
