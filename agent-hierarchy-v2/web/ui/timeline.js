import { internalToggle, isInternal, showInternal } from "./msg-filter.js";
import { html } from "./h.js";
import { memberHref } from "./deeplink.js";
import { Rows } from "./rows.js";
import { Live } from "./live-component.js";
import { memberIds } from "./select.js";
import { hoverProps } from "./hover.js";

// The Timeline view: one lane per member, with the spans and message marks the daemon serves for the chosen range through
// list:timeline-spans:<dir>/<team>. The axis is computed here.

export const RANGES = [
  ["15m", "15 m", 15 * 60_000],
  ["1h", "1 h", 60 * 60_000],
  ["6h", "6 h", 6 * 60 * 60_000],
  ["24h", "24 h", 24 * 60 * 60_000],
  ["7d", "7 d", 7 * 24 * 60 * 60_000],
];

export const AXIS_W = 1000;
const LANE_LIMIT = 200;
const LANE_PAGES = 3;
const DAY_MS = 24 * 60 * 60_000;

// The [from, to] of a range key at `now`: a named range ends now; "custom" keeps the ui's own from/to.
export function rangeOf(key, now, custom = null) {
  if (key === "custom" && custom) {
    return { from: custom.from, to: custom.to };
  }
  const span = (RANGES.find((r) => r[0] === key) ?? RANGES[1])[2];
  return { from: now - span, to: now };
}

// Whether a range starts before the server's retention boundary (now minus retention_days). With no retention known, nothing is archived.
export const archivedPart = (from, now, days) => days != null && from < now - days * DAY_MS;

const STEPS = [60_000, 5 * 60_000, 15 * 60_000, 30 * 60_000, 3_600_000, 2 * 3_600_000, 6 * 3_600_000, 12 * 3_600_000, 86_400_000];

// Axis ticks: at most 8, on round times.
export function ticksOf(from, to) {
  const step = STEPS.find((s) => (to - from) / s <= 8) ?? STEPS.at(-1);
  const out = [];
  for (let t = Math.ceil(from / step) * step; t <= to; t += step) {
    out.push({ at: t, x: ((t - from) / (to - from)) * AXIS_W, long: step >= 86_400_000 });
  }
  return out;
}

const x = (t, from, to) => Math.round(((t - from) / (to - from)) * AXIS_W * 10) / 10;
const clock = (t, long) => (long ? new Date(t).toLocaleDateString([], { month: "short", day: "numeric" }) : new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }));

export function MessageMark({ mark, at }) {
  const cls = `mark ${mark.kind}`;
  const title = `${mark.kind}: ${mark.slug}`;
  if (mark.kind === "brief") {
    return html`<rect class=${cls} x=${at - 4} y="6" width="8" height="8" rx="2"><title>${title}</title></rect>`;
  }
  if (mark.kind === "report") {
    return html`<rect class=${cls} x=${at - 4} y="6" width="8" height="8" transform=${`rotate(45 ${at} 10)`}><title>${title}</title></rect>`;
  }
  return html`<circle class=${cls} cx=${at} cy="10" r="4"><title>${title}</title></circle>`;
}

// A lane's row: its name and one svg of spans and marks on the shared axis (units 0..AXIS_W; no per-frame work).
const laneRow = (lane, ctx) => html`<li class="lane" tabindex="0" ...${hoverProps(ctx.store, lane.id, "strip")}>
  <a class="lane-name" href=${memberHref(lane.id)}>${lane.name}</a>${lane.band != null ? html`<span class="lane-note">earlier messages not shown</span>` : null}
  <svg class="lane-svg" viewBox=${`0 0 ${AXIS_W} 20`} preserveAspectRatio="none" role="img" aria-label=${`${lane.name}: ${lane.spans.length} spans, ${lane.marks.length} messages${lane.band != null ? " (earlier messages not shown)" : ""}`}>
    <defs><pattern id="tl-hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><line x1="0" y1="0" x2="0" y2="6" class="hatch-line" /></pattern></defs>
    ${lane.band != null ? html`<rect class="band" x="0" y="2" width=${Math.max(0, x(lane.band, ctx.from, ctx.to))} height="16" fill="url(#tl-hatch)"><title>earlier messages not shown</title></rect>` : null}
    ${lane.spans.map((s) => html`<rect key=${`${s.from}`} class=${`span st-${s.state}`} x=${x(s.from, ctx.from, ctx.to)} y="5" width=${Math.max(1, x(s.to, ctx.from, ctx.to) - x(s.from, ctx.from, ctx.to))} height="10"><title>${s.state}</title></rect>`)}
    ${lane.marks.map((m) => html`<${MessageMark} key=${m.key} mark=${m} at=${x(m.at, ctx.from, ctx.to)} />`)}
  </svg>
</li>`;

export const laneKey = (l) => l.id;

// A datetime-local input's value for a time: local, to the minute.
const localInput = (ms) => new Date(ms - new Date(ms).getTimezoneOffset() * 60_000).toISOString().slice(0, 16);

export class TimelineView extends Live {
  componentDidMount() {
    super.componentDidMount();
    this.reload();
  }

  componentWillUnmount() {
    super.componentWillUnmount();
    this.gen = (this.gen ?? 0) + 1;
    clearTimeout(this.reloadTimer);
    this.reloadTimer = null;
  }

  componentDidUpdate() {
    this.reload();
  }

  // A new range or roster reads at once and drops what was shown. New transitions or exchanges read again, at most once a second.
  reload() {
    const sel = this.select(this.props.store.state, this.props);
    if (!sel.listId) {
      return;
    }
    const windowKey = `${sel.listId}|${sel.range}|${sel.from}|${sel.to}`;
    const liveKey = `${sel.tlVersion}|${sel.sigKey}`;
    if (this.loadedWindow !== windowKey) {
      const rangeMoved = this.loadedRange !== `${sel.listId}|${sel.rangeKey}`;
      this.loadedWindow = windowKey;
      this.loadedRange = `${sel.listId}|${sel.rangeKey}`;
      this.loadedLive = liveKey;
      if (rangeMoved) {
        this.served = null;
      }
      this.load(sel);
      if (rangeMoved) {
        this.forceUpdate();
      }
      return;
    }
    if (this.loadedLive !== liveKey && this.reloadTimer == null) {
      this.loadedLive = liveKey;
      const wait = Math.max(0, (this.lastRead ?? -Infinity) + (this.props.coalesceMs ?? 1000) - Date.now());
      this.reloadTimer = setTimeout(() => {
        this.reloadTimer = null;
        this.load(this.select(this.props.store.state, this.props));
      }, wait);
    }
  }

  // Reads the range's lanes, every page up to LANE_PAGES. A named range sends no `to`: it ends now, so the daemon holds a quiet member at its current activity.
  async load(sel) {
    const gen = (this.gen = (this.gen ?? 0) + 1);
    this.lastRead = Date.now();
    const params = { limit: LANE_LIMIT, from_ms: sel.from, ...(sel.range === "custom" ? { to_ms: sel.to } : null) };
    try {
      let doc = await this.props.store.fetchList(sel.listId, params);
      const rows = [...doc.rows];
      const first = doc;
      for (let i = 1; doc.next && i < LANE_PAGES; i += 1) {
        doc = await this.props.store.fetchList(sel.listId, { ...params, after: doc.next });
        rows.push(...doc.rows);
      }
      if (gen === this.gen) {
        this.served = { rows, from: first.from_ms, to: first.to_ms, archived: first.archived === true, more: first.more === true };
        this.forceUpdate();
      }
    } catch {
      // what was served last stays on show
    }
  }

  select(snap, { teamId }) {
    const team = snap.slices.get(teamId);
    const dir = team?.body.dir_id;
    // The slice `timeline:<dir>` carries the retention and the count; the lanes are `list:timeline-spans:<dir>/<team>`.
    const tlId = dir ? `timeline:${dir}` : null;
    const listId = dir ? `list:timeline-spans:${dir}/${team.body.name}` : null;
    const now = Math.floor(Date.now() / 60_000) * 60_000;
    const range = snap.ui.range ?? "1h";
    const { from, to } = rangeOf(range, now, snap.ui.custom ?? null);
    const ids = memberIds(team);
    const rangeKey = range === "custom" ? `${from}-${to}` : range;
    const internal = showInternal(snap.ui);
    const served = this.served ?? null;
    const byId = new Map((served?.rows ?? []).map((r) => [r.id, r]));
    let internalCount = 0;
    const prev = this.kept ?? new Map();
    const kept = new Map();
    // A lane whose content is unchanged keeps its object, so its memo'd Row skips drawing.
    const lanes = ids.map((id) => {
      const row = byId.get(id);
      internalCount += (row?.marks ?? []).filter(isInternal).length;
      const l = { id, name: row?.name ?? snap.slices.get(id)?.body.name ?? id.split("/").pop(), spans: row?.spans ?? [], marks: internal ? row?.marks ?? [] : (row?.marks ?? []).filter((m) => !isInternal(m)), ...(row?.band != null ? { band: row.band } : null) };
      const json = JSON.stringify(l);
      const old = prev.get(id);
      const lane = old?.json === json ? old.lane : l;
      kept.set(id, { json, lane });
      return lane;
    });
    this.kept = kept;
    this.lanes = lanes;
    const days = snap.slices.get(tlId)?.body.retention_days ?? null;
    const sigKey = ids.map((id) => `${id}=${(snap.slices.get(id)?.body.last_exchanges ?? []).join(",")}`).join(";");
    return { internal, internalCount, idsKey: ids.join("\n"), rangeKey, sigKey, listId, tlId, tlVersion: snap.slices.get(tlId)?.version ?? 0, range, from, to, axisFrom: served?.from ?? from, axisTo: served?.to ?? to, now, days, archived: served?.archived === true, more: served?.more === true, key: [...kept.values()].map((k) => k.json).join(), total: snap.slices.get(tlId)?.body.total ?? 0 };
  }

  pick = (range) => this.props.store.setUi({ range });

  // Custom keeps the bounds on screen when picked; an edit that would leave the range empty or backwards is ignored.
  pickCustom = () => this.props.store.setUi({ range: "custom", custom: this.props.store.state.ui.custom ?? { from: this.sel.from, to: this.sel.to } });

  setCustom = (side, e) => {
    const ms = Date.parse(e.currentTarget.value);
    const cur = this.props.store.state.ui.custom ?? { from: this.sel.from, to: this.sel.to };
    const next = { ...cur, [side]: ms };
    if (Number.isFinite(ms) && next.from < next.to) {
      this.props.store.setUi({ custom: next });
    }
  };

  draw(_, s) {
    const ticks = ticksOf(s.axisFrom, s.axisTo);
    return html`<section class="timeline" aria-label="Timeline">
      <header class="tl-head">
        <span class="ranges" role="group" aria-label="Range">${RANGES.map(([k, label]) => html`<button key=${k} class=${s.range === k ? "tab on" : "tab"} aria-pressed=${s.range === k} onClick=${() => this.pick(k)}>${label}</button>`)}
          <button class=${s.range === "custom" ? "tab on" : "tab"} aria-pressed=${s.range === "custom"} onClick=${this.pickCustom}>Custom</button></span>
        ${s.range === "custom" ? html`<span class="custom-range"><input type="datetime-local" aria-label="From" value=${localInput(s.from)} onChange=${(e) => this.setCustom("from", e)} /><input type="datetime-local" aria-label="To" value=${localInput(s.to)} onChange=${(e) => this.setCustom("to", e)} /></span>` : null}
        ${internalToggle(this.props.store, s.internal, s.internalCount)}
        <span class="chip" title=${`${s.total} transitions kept`}>${s.days === null ? "retention unknown" : `keeps ${s.days} ${s.days === 1 ? "day" : "days"}`}</span>
        ${archivedPart(s.from, s.now, s.days) || s.archived ? html`<span class="chip amber archived">archived</span>` : null}
        ${s.more ? html`<span class="chip">more transitions than loaded windows: narrow the range</span>` : null}
      </header>
      <div class="axis" aria-hidden="true">${ticks.map((t) => html`<span key=${t.at} class="tick" style=${{ left: `${t.x / 10}%` }}>${clock(t.at, t.long)}</span>`)}</div>
      <${Rows} cls="lanes" rows=${this.lanes} keyOf=${laneKey} row=${laneRow} ctx=${{ from: s.axisFrom, to: s.axisTo, store: this.props.store }} />
    </section>`;
  }
}
