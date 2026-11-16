// A copy's link to ahd (herdr-http-link.md §4-§5): the poll and the answer over HTTP to ahd's loopback bridge, with exec through the
// resolver (target-model.js) as the floor. The one HTTP client of the mod. Imports no Herdr code: herdr's fetch, the runner and
// sleep are passed in, so node tests (tests/web/herdr-link-model.test.mjs) drive the state machine against a stub.

import { pollExec, ABANDON_CAP, STUCK } from "./model.js";
import { targetKey } from "./target-model.js";

export const NO_ANSWER = "ahd did not answer";
export const UNREACHABLE = "ahd not reachable";
export const TOO_OLD = "ahd too old for the bridge";
// herdr's fetch has one fixed timeout and no option to set it (gpui-kit fetch.rs TIMEOUT, 30 s). Its error text does not show to be
// a timeout, so a rejection this late is read as one. ponytail: elapsed-time guess; read the error's kind if herdr ever exposes it.
const FETCH_TIMEOUT_MS = 30000;
const late = (ms) => ms >= FETCH_TIMEOUT_MS - 1000;
// So ahd answers a long-poll before that 30 s: an HTTP wait is at most 20 s (the exec poll keeps its own); `wait=0` is unchanged.
const HTTP_WAIT_S = 20;
const httpWait = (wait) => String(Math.min(Number(wait), HTTP_WAIT_S));

// ah's own form encoding (Ah.DaemonClient SliceQuery.FormEncode): letters, digits and *-._ as they are, a space as +, the rest %XX.
export const formEncode = (s) => encodeURIComponent(s).replace(/[!'()~]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`).replace(/%20/g, "+");

// The query `ah view events` sends to GET /v1/view (SliceQuery.Events); the bridge passes it through to the same handler.
export const eventsQuery = ({ sub, since, epoch, wait }) =>
  `path=%2Fevents&since=${since}${epoch ? `&epoch=${formEncode(epoch)}` : ""}&wait=${wait}&sub=${formEncode(sub)}`;

// The argv of the same poll through ah.
export const eventsArgs = ({ sub, since, epoch, wait }) => ["view", "events", "--sub", sub, "--since", String(since), "--wait", String(wait), ...(epoch ? ["--epoch", epoch] : [])];

const ahdShaped = (doc) => doc !== null && typeof doc === "object" && "epoch" in doc && typeof doc.seq === "number";
const json = (text) => {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
};

export class Link {
  // fetch: herdr's global fetch. runner: () => this copy's runner now (ah.js ah). sleep: ms => Promise. backoff: main.js BACKOFF_MS.
  constructor({ fetch, runner, sleep, backoff, now = () => Date.now() }) {
    Object.assign(this, { fetch, runner, sleep, backoff, now });
    this.reset(null);
  }

  // A new target (the setting, or herdr's machines, changed): what was learnt about the old one goes.
  reset(key) {
    this.key = key;
    this.port = null;
    // set: exec mode for the rest of this target's life, and why
    this.execWhy = typeof this.fetch === "function" ? null : "herdr has no HTTP client";
    this.odd = null; // the port a non-ahd reply came from
    this.refused = { port: null, n: 0 }; // refusals of a port `ah where` reported live
    this.fails = 0;
    this.nextFind = 0;
  }

  // What Settings shows: null while HTTP is in use or not yet tried.
  get execReason() {
    return this.execWhy;
  }

  toExec(why) {
    this.execWhy = why;
    this.port = null;
  }

  runnerNow() {
    const r = this.runner();
    const key = targetKey(r.target);
    if (key !== this.key) this.reset(key);
    return r;
  }

  // One poll ({sub, since, epoch, wait}) within `timeout` ms; `held` counts given-up requests (model.js pollExec). Resolves to the
  // reply document; throws an Error whose message is the down reason.
  async poll(q, timeout, held, again = false) {
    const r = this.runnerNow();
    const viaExec = async () => JSON.parse((await pollExec(() => r.exec("ah", eventsArgs(q), { timeout }), this.sleep, timeout, held)).stdout);
    if (!this.execWhy && this.port === null && r.target.kind !== "unresolved") await this.discover(r);
    if (this.execWhy || this.port === null) return viaExec();
    const port = this.port;
    let res;
    try {
      res = await this.request(held, timeout, this.sleep, `http://127.0.0.1:${port}/ui/v1/view?${eventsQuery({ ...q, wait: httpWait(q.wait) })}`, {});
    } catch (e) {
      if (e.message !== UNREACHABLE) throw e; // did not answer, stuck or too large: the same port, after the backoff
      this.port = null;
      // this computer: back to discovery (exec when the bridge is not live), so the link comes back with the bridge
      if (r.target.kind === "local") {
        if (again) throw e;
        return this.poll(q, timeout, held, true);
      }
      this.refused = { port, n: this.refused.port === port ? this.refused.n + 1 : 1 };
      if (this.refused.n < 2) throw e;
      this.toExec(r.target.kind === "wsl" ? `127.0.0.1:${port} not reachable from Windows (WSL localhost forwarding?)` : `127.0.0.1:${port} not reachable`);
      return viaExec();
    }
    if (res.status === 503) { // closing (ahd stopping or restarting): not down if the next try answers
      this.port = null;
      if (again) throw new Error(UNREACHABLE);
      await this.sleep(2000);
      return this.poll(q, timeout, held, true);
    }
    if (res.status === 421 || res.status === 403) {
      this.toExec(`bridge refused this client (${res.status})`);
      return viaExec();
    }
    if (res.status === 404) {
      this.toExec(TOO_OLD);
      return viaExec();
    }
    const doc = json(res.text);
    if (res.status === 200 && ahdShaped(doc)) {
      this.odd = null;
      this.refused = { port: null, n: 0 };
      return doc;
    }
    if (res.status !== 200 && typeof doc?.error === "string") throw new Error(`ahd answered ${res.status}`); // ahd's own error reply
    // not ahd: another program on a reused port. Rediscover; the same port twice is exec mode.
    this.port = null;
    if (this.odd === port) {
      this.toExec(`port ${port} is another program`);
      return viaExec();
    }
    this.odd = port;
    throw new Error(UNREACHABLE);
  }

  // The bridge's port, through `ah where`; when it reports none live and ah runs elsewhere (a herdr machine, WSL), the service lease
  // (`ahd ui start --no-open`, which also starts ahd), then `ah where` again. This computer never takes a lease or starts ahd: no live
  // bridge is the exec poll, as before. Paced by the backoff while it finds nothing.
  async discover(r) {
    if (this.now() < this.nextFind) return;
    if (await this.find(r)) {
      this.fails = 0;
      this.nextFind = 0;
    } else {
      this.nextFind = this.now() + this.backoff[Math.min(this.fails++, this.backoff.length - 1)];
    }
  }

  async find(r) {
    const live = async () => {
      const w = await r.exec("ah", ["where"], { timeout: 10000 }).catch(() => null);
      const ui = w?.code === 0 ? json(w.stdout)?.ui : null;
      return ui?.live && Number.isInteger(ui.port) ? ui.port : null;
    };
    let port = await live();
    if (port === null && r.target.kind !== "local") {
      const lease = await r.exec("ahd", ["ui", "start", "--no-open", "--json"], { timeout: 30000 }).catch(() => null);
      if (/browser UI disabled/.test(lease?.stderr ?? "")) this.toExec("AH_UI=off");
      if (lease?.code === 0) port = await live();
    }
    this.port = port;
    return port !== null;
  }

  // One request, bounded as model.js pollExec bounds an exec: a deadline `timeout` + 5 s, a given-up request counted in `held` until
  // it settles. Resolves to {status, text}; throws NO_ANSWER, UNREACHABLE, STUCK or the too-large reason.
  async request(held, timeout, sleep, url, init) {
    if (held.abandoned >= ABANDON_CAP) throw new Error(STUCK);
    const t0 = this.now();
    const call = Promise.resolve().then(() => this.fetch(url, init))
      .then(async (res) => ({ res: { status: res.status, text: await res.text() } }), (e) => ({ e }));
    const won = await Promise.race([call, sleep(timeout + 5000).then(() => null)]);
    if (!won) {
      held.abandoned++;
      call.then(() => held.abandoned--);
      throw new Error(NO_ANSWER);
    }
    if (won.e) {
      const why = won.e?.message ?? String(won.e);
      if (/byte limit/.test(why)) throw new Error("ahd's reply is over 8 MiB");
      throw Object.assign(new Error(/timed? ?out/i.test(why) || late(this.now() - t0) ? NO_ANSWER : UNREACHABLE), { raw: why });
    }
    return won.res;
  }

  // One read on a click (a History thread, an item's log): GET `pathQuery` on the bridge, bounded as a poll is. Resolves to the 200
  // reply's text; null when the read goes through ah instead (no bridge, exec mode, not reachable, or any other status: an older ahd
  // has no such route, and ah then gives the reason); throws when ahd took the request and did not answer, so no caller waits twice.
  async read(pathQuery, timeout, sleep = this.sleep) {
    this.runnerNow();
    if (this.execWhy || this.port === null) return null;
    let res;
    try {
      res = await this.request({ abandoned: 0 }, timeout, sleep, `http://127.0.0.1:${this.port}${pathQuery}`, {});
    } catch (e) {
      if (e.message !== UNREACHABLE) throw e;
      this.port = null;
      return null;
    }
    return res.status === 200 ? res.text : null;
  }

  // The answer (model.js sendAnswer): POST /ui/v1/answer with the web's own body, once. Resolves to {status, reply} for any reply,
  // null when nothing reached ahd (no bridge, exec mode, a refused connection, or the bridge turned this client away) so the caller
  // sends through ah instead. Any other failure may have reached ahd: it throws NO_ANSWER and is never sent again.
  // `sleep` is the caller's (a click's own task), for the deadline.
  answer(body, sleep = this.sleep) {
    return this.post("/ui/v1/answer", body, 30000, sleep);
  }

  // Dismiss a team (dismiss-model.js teamClose): POST /ui/v1/team-close, by the answer's rules. A fresh copy (the dialog) has no
  // port yet, so it looks once.
  async teamClose(body, timeout, sleep = this.sleep) {
    const r = this.runnerNow();
    if (!this.execWhy && this.port === null && r.target.kind !== "unresolved") await this.discover(r);
    return this.post("/ui/v1/team-close", body, timeout, sleep);
  }

  async post(path, body, timeout, sleep) {
    this.runnerNow();
    if (this.execWhy || this.port === null) return null;
    let res;
    try {
      res = await this.request({ abandoned: 0 }, timeout, sleep, `http://127.0.0.1:${this.port}${path}`,
        { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    } catch (e) {
      // only a refused connect is known not to have reached ahd (assumption: herdr's error text names it; unprobed, E1)
      if (e.message === UNREACHABLE && /connection refused/i.test(e.raw ?? "")) {
        this.port = null;
        return null;
      }
      throw new Error(NO_ANSWER);
    }
    if (res.status === 421 || res.status === 403) {
      this.toExec(`bridge refused this client (${res.status})`);
      return null;
    }
    if (res.status === 404) {
      this.toExec(TOO_OLD);
      return null;
    }
    return { status: res.status, reply: json(res.text) };
  }
}
