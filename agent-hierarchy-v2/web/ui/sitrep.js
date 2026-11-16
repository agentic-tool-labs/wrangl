import { html } from "./h.js";
import { Live } from "./live-component.js";
import { Dialog } from "./dialog.js";
import { Age } from "./ticker.js";
import { ageOf, ageText } from "./store.js";
import { hashOf } from "./deeplink.js";
import { ItemDialog } from "./item-dialog.js";
import { STATE_LABEL, itemLogId, stalledOf, streamName } from "./plans-model.js";
import { loadRegions, loadSeen, marksOf, saveRegions, saveSeen } from "./sitrep-seen.js";
import { teamSummary } from "./select.js";
import { teamGoal } from "./overview.js";

// The Sitrep (ah-ui-ia §3, ah-ui-focus §5): Completed, In progress and Up next stacked, each collapsible, then any other group that holds rows, for one top-level team and
// its lanes, from the daemon's one `sitrep:` slice. A row is its title; it opens the one item dialog. What the viewer saw when they last closed it (sitrep-seen.js) says only
// whether anything changed; the baseline is read once at open, and what is on show is written back on close, however it closes.

// "3 m" from a time: the age model's text without " ago"; "—" without a time.
const dur = (ms, now) => (ms ? ageText(ageOf({ as_of: ms }, {}, now)).replace(" ago", "") : "—");
const sha7 = (sha) => String(sha ?? "").slice(0, 7);

// A holder as the dialog's owner: a member id as served, or a bare name under the row's team.
const holderOf = (dir, row, h) => ({ id: h.startsWith("member:") ? h : row.team ? `member:${dir}/${row.team}/${h}` : null, name: h.split("/").pop() });

// A queue row's stall, by the one rule (plans-model.js); `could_take` is the queue row's idle eligible members.
const stalledNow = (row, now) => row.source === "queue" && stalledOf(row.stall_at, row.could_take ?? [], now);

const waitText = (row) => (now) => {
  if (stalledNow(row, now)) {
    return `stalled ${dur(row.queued_since, now)}${row.could_take?.length ? ` · no owner, ${row.could_take.length} idle` : ""}`;
  }
  return `waiting ${dur(row.queued_since, now)}${(row.could_take ?? []).length > 0 ? "" : ` · no idle ${row.role ?? "member"}`}`;
};

// A ship or merge record (§3.3a): words, not a work item, and skipped by row navigation.
const recordText = (r) =>
  r.kind === "ship"
    ? html`Shipped <code>${r.ref}</code> · <code>${sha7(r.sha)}</code>${r.tag ? " · tag" : ""}${r.confirmed === false ? " · unconfirmed" : ""}`
    : html`Merged <code>${r.ref}</code> into <code>${r.into}</code> · <code>${sha7(r.sha)}</code>`;

// The Board's one decision card of a team: decisions have no card each.
export const decisionItem = (team) => `${team}|decision`;

// One row, its title and nothing more. A work item opens its detail; a decision is no work item, so it links to its card on the Board.
function Row({ row, team, onOpen }) {
  if (row.kind === "ship" || row.kind === "merge") {
    return html`<li class="sr-item"><span class="sr-row sr-record" title=${`${row.ref} · ${row.sha}`}>${recordText(row)}</span></li>`;
  }
  return html`<li class="sr-item">${row.kind === "decision"
    ? html`<a class="sr-row" data-srow href=${hashOf({ view: "board", team, item: decisionItem(team) })} title=${row.title}>${row.title}</a>`
    : html`<button type="button" class="sr-row" data-srow aria-haspopup="dialog" title=${row.title} onClick=${() => onOpen(row)}>${row.title}</button>`}</li>`;
}

// The item's detail model for the one dialog: the row as served, with the state, holder, note and work id of its plan or backlog row once held.
const age = (at, text) => html`<${Age} key=${at} text=${text} />`;
export function detailOf(row, col, label, source, dir) {
  const held = source?.items?.find((i) => i.key === row.key || `${row.stream}:${i.key}` === row.key) ?? null;
  const state = held?.state ?? row.state;
  const holder = held?.holder ?? row.holder ?? null;
  const when = [
    row.done_at ? ["Done", age(row.done_at, (now) => `${dur(row.done_at, now)} ago`)] : null,
    row.since ? [col === "s" ? "Idle" : "For", col === "s" && row.last_activity ? age(row.last_activity, (now) => dur(row.last_activity, now)) : age(row.since, (now) => dur(row.since, now))] : null,
    row.queued_since ? ["Queue", html`<${Age} key=${row.queued_since} cls="sr-wait" tone=${(now) => (stalledNow(row, now) ? "err" : null)} text=${waitText(row)} />`] : null,
  ];
  const workId = held?.work_id ?? null;
  return {
    kind: `Item · ${workId ?? row.key}`,
    title: row.title,
    rows: [
      ["Region", label],
      ["State", state ? STATE_LABEL[state] ?? state : null],
      ["Work", row.work === "with_lead" ? "Report in" : row.work === "no_one" ? "No one on it" : null],
      ...when.filter(Boolean),
      ["Waiting for", col === "n" || col === "o" ? row.role : null],
      ["From", row.source === "backlog" ? "backlog" : null],
      ["Stream", row.stream ? streamName(row.stream) : null, "mono"],
      ["Lane", row.lane],
      ["Team", row.team],
      ["Ext id", held?.ext_id ?? null, "mono"],
    ].filter(([, v]) => v),
    holder: holder ? holderOf(dir, row, holder) : null,
    note: held?.note ?? row.note ?? null,
    log: itemLogId(dir, workId),
  };
}

// The detail of one row: leases the plan or backlog slice that holds its work id, and the log through it.
class RowDetail extends Live {
  sourceId({ row, dir }) {
    return row.source === "backlog" ? `backlog:${dir}` : row.team && row.stream ? `plan-items:${dir}/${row.team}/${row.stream}` : null;
  }

  leaseIds(_snap, props) {
    return [this.sourceId(props)].filter(Boolean);
  }

  select(snap, props) {
    this.source = snap.slices.get(this.sourceId(props))?.body ?? null;
    return { v: snap.slices.get(this.sourceId(props))?.version ?? -1 };
  }

  draw({ row, col, label, dir, store, onClose }) {
    return html`<${ItemDialog} p=${detailOf(row, col, label, this.source, dir)} store=${store} onClose=${onClose} returnTo=".sitrep .sr-head" />`;
  }
}

// A group's heading count: the rows shown, or "10 of 39" when a capped group's total is more.
// A ship or merge record is shown among the completed rows but is no item, and the total counts items.
export const workCount = (rows) => rows.filter((r) => r.kind !== "ship" && r.kind !== "merge").length;
export const countText = (shown, total) => (total !== null && total !== undefined && total > shown ? `${shown} of ${total}` : String(shown));

const EMPTY = { w: "Nothing waiting on you.", c: "Nothing completed yet.", p: "Nothing in progress.", b: "Nothing blocked.", m: "Nothing stopped.", s: "Nothing stale.", n: "Nothing queued.", o: "Nothing old in the queue." };

// Up and Down move between the region heads and the rows, in reading order; records are not stops.
const onRowKey = (e) => {
  if (e.key !== "ArrowDown" && e.key !== "ArrowUp") {
    return;
  }
  const rows = [...e.currentTarget.querySelectorAll("[data-srow]")];
  const i = rows.indexOf(globalThis.document?.activeElement);
  const next = rows[i < 0 ? 0 : Math.min(rows.length - 1, Math.max(0, i + (e.key === "ArrowDown" ? 1 : -1)))];
  if (next) {
    e.preventDefault();
    next.focus();
  }
};

export class SitrepDialog extends Live {
  // the at-open baseline: read once, so live pushes re-mark against the same snap
  seen = loadSeen(this.props.teamId);
  state = { regions: loadRegions(), detail: null };

  select(snap, { teamId }) {
    const slice = snap.slices.get(`sitrep:${teamId.slice("team:".length)}`);
    this.body = slice?.body ?? null;
    return { version: slice?.version ?? -1, has: Boolean(slice), goal: JSON.stringify(teamSummary(snap.slices, teamId)?.goal ?? null) };
  }

  leaseIds(_snap, { teamId }) {
    return [`sitrep:${teamId.slice("team:".length)}`];
  }

  componentWillUnmount() {
    if (this.body) {
      saveSeen(this.props.teamId, this.body, Date.now());
    }
    super.componentWillUnmount();
  }

  draw({ teamId, name, onClose }, s) {
    const b = this.body;
    const dir = teamId.slice("team:".length).split("/")[0];
    const { left, changed } = b ? marksOf(this.seen, b) : { left: 0, changed: false };
    const cols = (b?.regions ?? [])
      .map((r) => ({ ...r, rows: b[r.list] ?? [], count: r.total ? b[r.total] ?? null : null }))
      .filter((r) => !r.optional || (r.count ?? r.rows.length) > 0);
    const open = ({ col, folded }) => this.state.regions[col] ?? !folded;
    const toggle = (r) => {
      const regions = { ...this.state.regions, [r.col]: !open(r) };
      saveRegions(regions);
      this.setState({ regions });
    };
    const opened = this.state.detail;
    const status = !b ? "Loading…" : html`${b.as_of ? html`updated <${Age} key=${b.as_of} text=${(now) => `${dur(b.as_of, now)} ago`} />` : "nothing yet"} · <span class="num">${b.completed_total ?? 0}</span> ${b.completed_total === 1 ? "item" : "items"} completed in total${this.seen && !changed ? " · no change since you last looked" : ""}`;
    return html`<${Dialog} title=${`Sitrep · ${name}`} cls="sitrep" returnTo=".sitrep-btn" onClose=${onClose}>
      <p class="sr-status" role="status">${status}</p>
      ${teamGoal(JSON.parse(s.goal), name, "sr-goal")}
      <div class="sr-cols" onKeyDown=${onRowKey}>
        ${cols.map((region) => {
          const { col, label, rows, count } = region;
          return html`<section key=${col} class=${`sr-col sr-${col}`} aria-labelledby=${`sr-h-${col}`}>
          <h3 id=${`sr-h-${col}`} data-text=${label}><button type="button" class="sr-head" data-srow aria-expanded=${open(region) ? "true" : "false"} aria-controls=${`sr-r-${col}`} onClick=${() => toggle(region)}><span>${label}</span> <span class="num">${countText(workCount(rows), count)}</span><span class="sr-chev" aria-hidden="true">▾</span></button></h3>
          <div id=${`sr-r-${col}`} hidden=${!open(region)}>${!open(region)
            ? null
            : rows.length > 0
            ? html`<ul class="sr-list">${rows.map((r) => html`<${Row} key=${r.key} row=${r} team=${teamId} onOpen=${(row) => this.setState({ detail: { row, col, label } })} />`)}</ul>`
            : html`<p class="dim sr-empty">${EMPTY[col]}</p>`}</div>
        </section>`;
        })}
      </div>
      ${left > 0 ? html`<p class="sr-foot dim">${left} left the view</p>` : null}
    </${Dialog}>
    ${opened ? html`<${RowDetail} key=${opened.row.key} ...${opened} dir=${dir} store=${this.props.store} onClose=${() => this.setState({ detail: null })} />` : null}`;
  }
}
