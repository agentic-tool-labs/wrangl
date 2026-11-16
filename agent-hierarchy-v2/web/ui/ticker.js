import { h, Component } from "./h.js";

// The one timer for the whole page's ages. Each subscriber writes its own text node; no component draws on a tick.
export const TICK_MS = 1000;

export function createTicker({ setTimer = setInterval, clearTimer = clearInterval, now = Date.now } = {}) {
  const subs = new Set();
  let timer = null;
  const tick = () => {
    const t = now();
    for (const s of subs) {
      s(t);
    }
  };
  return {
    subscribe(fn) {
      subs.add(fn);
      timer ??= setTimer(tick, TICK_MS);
      fn(now());
      return () => {
        subs.delete(fn);
        if (subs.size === 0 && timer !== null) {
          clearTimer(timer);
          timer = null;
        }
      };
    },
    get size() {
      return subs.size;
    },
  };
}

export const ticker = createTicker();

// An element whose text is `text(now)`, refreshed by the ticker. It never draws again after mounting. `tone(now)`, when given,
// adds a class on the same tick, so a style that follows the time never lags its words.
export class Age extends Component {
  componentDidMount() {
    this.off = (this.props.ticker ?? ticker).subscribe((now) => {
      const next = this.props.text(now);
      if (this.base && this.base.textContent !== next) {
        this.base.textContent = next;
      }
      if (this.base && this.props.tone) {
        const tone = this.props.tone(now);
        const cls = tone ? `${this.props.cls ?? "age"} ${tone}` : this.props.cls ?? "age";
        if (this.base.getAttribute("class") !== cls) {
          this.base.setAttribute("class", cls);
        }
      }
    });
  }

  shouldComponentUpdate() {
    return false;
  }

  componentWillUnmount() {
    this.off?.();
  }

  render({ cls }) {
    return h("span", { class: cls ?? "age" });
  }
}

// An open dispatch's ETA chip. The daemon decides the state and serves due_at and overdue_by; this only words them, and ticks the
// minutes on the one page timer. The glyph and bold on an overdue chip keep it from resting on colour alone.
export const spanText = (ms) => {
  const m = Math.max(1, Math.round(ms / 60_000));
  return m < 120 ? `${m}m` : `${Math.round(m / 60)}h`;
};

export function etaText(b, now) {
  const due = Date.parse(b.due_at ?? "");
  if (b.state === "overdue") {
    return `overdue ${spanText(Math.max((b.overdue_by ?? 0) * 1000, Number.isFinite(due) ? now - due : 0))}`;
  }
  if ((b.state !== "on-time" && b.state !== "due-soon") || !Number.isFinite(due)) {
    return null;
  }
  return due <= now ? "due now" : `due ${spanText(due - now)}`;
}

const ETA_GLYPH = { overdue: "⚠", "due-soon": "◔" };

export function EtaChip({ b }) {
  if (!b || etaText(b, Date.now()) === null) {
    return null;
  }
  return h("span", { class: `eta ${b.state}` }, ETA_GLYPH[b.state] ? h("span", { class: "eta-glyph", "aria-hidden": "true" }, ETA_GLYPH[b.state]) : null, h(Age, { key: `${b.state}|${b.due_at}|${b.overdue_by}`, cls: "eta-text", text: (now) => etaText(b, now) ?? "" }));
}
