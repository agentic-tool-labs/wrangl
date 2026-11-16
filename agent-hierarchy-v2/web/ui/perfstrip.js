import { html, Component } from "./h.js";

// The performance strip: read-model counters polled every 5 s and a client frame rate sampled for 1 s every 10 s,
// both only while the strip is on screen. With no IntersectionObserver the strip is never visible and nothing runs.

export const POLL_MS = 5000;
export const FPS_EVERY_MS = 10000;
export const FPS_WINDOW_MS = 1000;

// The read-model counters the strip reads.
export const COUNTERS_READ = ["reread_retry", "drift", "coalesce_ms", "projector_faults"];

// What the strip shows from one read-model document: `prev` is the previous sample {at, retries}, for the reread rate.
export function summarize(doc, prev, at) {
  const c = doc?.counters ?? {};
  const retries = typeof c.reread_retry === "number" ? c.reread_retry : null;
  const seconds = prev && at > prev.at ? (at - prev.at) / 1000 : 0;
  const rate = retries !== null && prev?.retries != null && seconds > 0 ? Math.max(0, (retries - prev.retries) / seconds) : null;
  return {
    rereads: rate,
    drift: typeof c.drift === "number" ? c.drift : null,
    coalesce: typeof c.coalesce_ms === "number" ? c.coalesce_ms : null,
    faults: typeof c.projector_faults === "number" ? c.projector_faults : null,
    sample: { at, retries },
  };
}

const num = (v, unit = "") => (v === null || v === undefined ? "—" : `${Number.isInteger(v) ? v : v.toFixed(1)}${unit}`);

export class PerfStrip extends Component {
  state = { shown: null, fps: null, failed: false };
  visible = false;
  prev = null;
  frames = 0;

  get env() {
    const p = this.props;
    return {
      Observer: p.observer ?? globalThis.IntersectionObserver,
      setInterval: p.setInterval ?? globalThis.setInterval?.bind(globalThis),
      clearInterval: p.clearInterval ?? globalThis.clearInterval?.bind(globalThis),
      raf: p.raf ?? globalThis.requestAnimationFrame?.bind(globalThis),
      caf: p.caf ?? globalThis.cancelAnimationFrame?.bind(globalThis),
      now: p.now ?? (() => Date.now()),
    };
  }

  componentDidMount() {
    const { Observer } = this.env;
    if (typeof Observer !== "function" || !this.el) {
      return;
    }
    this.watch = new Observer((entries) => {
      const seen = entries.at(-1)?.isIntersecting === true;
      if (seen !== this.visible) {
        this.visible = seen;
        seen ? this.begin() : this.end();
      }
    });
    this.watch.observe(this.el);
  }

  componentWillUnmount() {
    this.watch?.disconnect();
    this.visible = false;
    this.end();
  }

  begin() {
    const env = this.env;
    this.sample();
    this.measure();
    this.pollTimer = env.setInterval(() => this.sample(), POLL_MS);
    this.fpsTimer = env.setInterval(() => this.measure(), FPS_EVERY_MS);
  }

  end() {
    const env = this.env;
    env.clearInterval(this.pollTimer);
    env.clearInterval(this.fpsTimer);
    this.pollTimer = null;
    this.fpsTimer = null;
    if (this.frame != null) {
      env.caf?.(this.frame);
      this.frame = null;
    }
    this.prev = null;
    this.round = (this.round ?? 0) + 1;
  }

  // A hidden tab neither polls nor samples frames; a tick that finds the previous read still in flight is skipped.
  tabShown() {
    return (this.props.tabVisible ?? (() => globalThis.document?.visibilityState !== "hidden"))();
  }

  async sample() {
    const round = this.round ?? 0;
    if (this.inFlight === round || !this.tabShown()) {
      return;
    }
    this.inFlight = round;
    try {
      const doc = await this.props.read();
      if (round !== (this.round ?? 0) || !this.visible) {
        return;
      }
      const at = this.env.now();
      const shown = summarize(doc, this.prev, at);
      this.prev = shown.sample;
      this.setState({ shown, failed: false });
    } catch {
      if (round === (this.round ?? 0) && this.visible) {
        this.setState({ failed: true });
      }
    } finally {
      if (this.inFlight === round) {
        this.inFlight = null;
      }
    }
  }

  // Counts animation frames for one second, then stops asking for more.
  measure() {
    if (!this.tabShown()) {
      return;
    }
    const env = this.env;
    const round = this.round ?? 0;
    const start = env.now();
    let frames = 0;
    const tick = () => {
      if (round !== (this.round ?? 0)) {
        return;
      }
      frames += 1;
      const spent = env.now() - start;
      if (spent >= FPS_WINDOW_MS) {
        this.frame = null;
        this.setState({ fps: Math.round((frames * 1000) / spent) });
        return;
      }
      this.frame = env.raf(tick);
    };
    this.frame = env.raf(tick);
  }

  render(_, { shown, fps, failed }) {
    return html`<footer class="perf-strip" aria-label="Performance" ref=${(el) => (this.el = el)}>
      ${failed
        ? html`<span class="dim">read model unavailable</span>`
        : html`<span class="metric"><span class="metric-label">rereads/s</span> <span class="metric-value">${num(shown?.rereads ?? null)}</span></span>
            <span class="metric"><span class="metric-label">drift</span> <span class="metric-value">${num(shown?.drift ?? null)}</span></span>
            <span class="metric"><span class="metric-label">coalesce</span> <span class="metric-value">${num(shown?.coalesce ?? null, " ms")}</span></span>
            <span class="metric"><span class="metric-label">projector faults</span> <span class="metric-value">${num(shown?.faults ?? null)}</span></span>`}
      <span class="metric"><span class="metric-label">fps</span> <span class="metric-value">${num(fps)}</span></span>
    </footer>`;
  }
}
