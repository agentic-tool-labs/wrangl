import { merge } from "./merge.js";

// The data core: one immutable Map<sliceId, slice>, swapped per /events reply, kept current by one long-poll per tab.
// `get(path, params, signal)` is the bridge's reader; `manifest(name, signal)` reads GET /v1/view/<name>.
// Everything that touches time is injected (now, setTimer, clearTimer, random) so the cadence can be driven without a clock.

export const WAIT_SECONDS = 25;
export const BACKOFF_MS = [500, 1000, 2000, 4000, 8000, 16000, 30000];
export const JITTER = 0.2;
export const MAX_WINDOWS = 3;
export const PAGE_LIMIT = 100;
// A 401 that lasts this long is a stale token, not a restart: the poll ends instead of retrying forever (b276).
export const AUTH_GIVE_UP_MS = 10 * 60 * 1000;

// The client's mirror of the server's view table (ModelViews.PrefixesOf): view name -> slice id prefixes. Edit here only.
export const VIEWS = {
  summary: ["list:teams", "team-summary:"],
  members: ["activity:"],
  tree: ["team:", "member:", "topology:", "worktree:", "list:teams"],
  exchanges: ["exchange:", "list:exchanges:", "list:feed:", "list:open:"],
  status: ["status:"],
  processes: ["processes:"],
  timeline: ["timeline:"],
};

// The delay before retry number `attempt` (0-based), spread by +-JITTER around the schedule.
export function backoffMs(attempt, random = Math.random) {
  const base = BACKOFF_MS[Math.min(attempt, BACKOFF_MS.length - 1)];
  return Math.round(base * (1 + JITTER * (2 * random() - 1)));
}

export const EMPTY = Object.freeze({ slices: new Map(), rev: 0, touched: null, epoch: null, seq: 0, ready: false, pending: new Set(), freshAt: 0, errorSince: null, error: null, retryAt: null, busy: 0, ended: false, endedWhy: null, lists: new Map(), ui: {}, large: false });

export function createStore({ get, manifest, now = Date.now, setTimer = setTimeout, clearTimer = clearTimeout, random = Math.random }) {
  let state = EMPTY;
  const listeners = new Set();
  let views = [];
  let extra = [];
  let abort = null;
  let generation = 0;
  let stopped = false;

  // A listener that throws is its own fault, not the poll's: the others still run and the poll goes on. Each listener's distinct
  // message is logged once, up to MAX_REPORTED distinct messages per listener (a message that varies stops being reported after that).
  // `fault` is the latest message of the listeners whose last notification threw, and is null once none does; the listeners are told
  // again when it changes so the page can show or drop it.
  const MAX_REPORTED = 20;
  const reported = new WeakMap();
  const failing = new Map();
  const set = (patch) => {
    state = { ...state, ...patch };
    for (const l of [...listeners]) {
      try {
        l(state);
        failing.delete(l);
      } catch (e) {
        const message = e instanceof Error ? e.message : String(e);
        const seen = reported.get(l) ?? new Set();
        reported.set(l, seen);
        if (!seen.has(message) && seen.size < MAX_REPORTED) {
          seen.add(message);
          console.error("ui: a store listener threw:", e);
          failing.set(l, message);
        } else if (seen.has(message)) {
          failing.set(l, message);
        }
      }
    }
    const fault = [...failing.values()].at(-1) ?? null;
    if (fault !== (state.fault ?? null)) {
      state = { ...state, fault };
      for (const l of [...listeners]) {
        try {
          l(state);
        } catch {
          // reported by the pass above, or beyond the cap
        }
      }
    }
  };

  // What the poll subscribes to: the view names plus the extra slice ids (a selected member's, one leased transcript).
  const leases = new Map();
  let restartQueued = false;
  let batchBefore = null;
  // After the server refused a request line as too long, the leases shrink to the team shell, its member rows and their activity:
  // worktrees and exchange labels are then fetched cold by the parts that show them.
  let reduced = false;
  const REDUCED_OUT = /^(worktree|exchange):/;
  const subscription = () => [...views.map((v) => `view:${v}`), ...new Set([...extra, ...[...leases.values()].flat()].filter((id) => !(reduced && REDUCED_OUT.test(id))))];

  // Replaces the store wholesale from the manifests of the current views. UI state stays as it was.
  async function reload(signal) {
    const docs = await Promise.all(views.map((v) => manifest(v, signal)));
    const slices = new Map();
    const pending = new Set();
    for (const doc of docs) {
      for (const s of doc.slices) {
        slices.set(s.id, merge(state.slices.get(s.id), s));
      }
      for (const id of doc.pending ?? []) {
        pending.add(id);
      }
    }
    const first = docs[0];
    set({ slices, rev: state.rev + 1, pending, epoch: first?.epoch ?? null, seq: Math.min(...docs.map((d) => d.seq)), ready: true, freshAt: now(), dataStale: first?.data_stale === true, observedAt: first?.observed_at ?? null, errorSince: null, error: null, retryAt: null, lists: new Map(), gone: new Set() });
  }

  // A poll's slices are written into the store's map in place: copying the whole map each poll cost time in proportion to
  // everything held. `rev` counts the map's changes, and `touched` names the ids the latest one changed, for readers that
  // update instead of re-reading; a reader compares `rev`, never the map's identity.
  function apply(reply) {
    const slices = state.slices === EMPTY.slices ? new Map() : state.slices;
    const ids = [];
    for (const s of reply.slices) {
      const kept = merge(slices.get(s.id), s);
      if (kept !== slices.get(s.id)) {
        slices.set(s.id, kept);
        ids.push(s.id);
      }
    }
    for (const id of reply.removed) {
      slices.delete(id) && ids.push(id);
    }
    const changed = ids.length > 0;
    const pending = new Set([...state.pending].filter((id) => !slices.has(id)));
    // The ids a poll reported removed, until published again: absence alone (after a reload, say) is not a removal.
    let gone = state.gone ?? new Set();
    if (reply.removed.length > 0 || reply.slices.some((s) => gone.has(s.id))) {
      gone = new Set(gone);
      reply.slices.forEach((s) => gone.delete(s.id));
      reply.removed.forEach((id) => gone.add(id));
    }
    const rev = changed ? state.rev + 1 : state.rev;
    set({ slices, rev, touched: changed ? { rev, ids } : state.touched, pending, gone, seq: reply.seq, freshAt: now(), dataStale: reply.data_stale === true, observedAt: reply.observed_at ?? null, errorSince: null, error: null, retryAt: null });
  }

  async function run(mine, signal) {
    let attempt = 0;
    while (!signal.aborted && mine === generation && !stopped) {
      try {
        if (!state.ready) {
          await reload(signal);
          if (signal.aborted || mine !== generation) {
            return;
          }
        }
        const ids = subscription();
        // A slice named but not held is asked for outright: one published before the cursor would never be sent otherwise.
        // The open and feed lists are slices; the other lists are paged and never held in the map.
        const fresh = ids.filter((id) => !id.startsWith("view:") && (!id.startsWith("list:") || /^list:(open|feed):/.test(id)) && !state.slices.has(id));
        // When every exact slice id of the subscription is asked for (the first poll after a load), `*` says so once.
        const exact = ids.filter((id) => !id.startsWith("view:") && !id.startsWith("list:"));
        const all = exact.length > 0 && exact.every((id) => fresh.includes(id));
        const asked = all ? ["*", ...fresh.filter((id) => id.startsWith("list:"))] : fresh;
        // A retry does not wait: the chip must clear as soon as the daemon answers, not a long-poll later.
        const reply = await get("/events", { since: state.seq, epoch: state.epoch, sub: ids.join(","), ...(asked.length > 0 ? { fresh: asked.join(",") } : null), wait: attempt > 0 ? 0 : WAIT_SECONDS }, signal);
        if (signal.aborted || mine !== generation) {
          return;
        }
        if (reply.resync || reply.epoch !== state.epoch) {
          await reload(signal);
        } else {
          apply(reply);
        }
        attempt = 0;
      } catch (e) {
        if (signal.aborted || mine !== generation) {
          return;
        }
        // An Ended with a status is a 401 or 503 from a daemon restarting: the poll retries through it. Only one without is final.
        if ((e?.name === "Ended" || e?.constructor?.name === "Ended") && e.status === undefined) {
          set({ ended: true, endedWhy: e.message || null });
          return;
        }
        if (e?.status === 401 && state.errorSince !== null && now() - state.errorSince >= AUTH_GIVE_UP_MS) {
          set({ ended: true, endedWhy: "401" });
          return;
        }
        const tooLong = e?.status === 414 || e?.status === 431;
        if (tooLong && !reduced) {
          reduced = true;
          set({ large: true });
          continue;
        }
        const delay = backoffMs(attempt, random);
        set({ errorSince: state.errorSince ?? now(), error: tooLong ? `subscription too large (${subscription().length} ids)` : e instanceof Error ? e.message : String(e), retryAt: now() + delay });
        await new Promise((done) => {
          const t = setTimer(done, delay);
          signal.addEventListener("abort", () => (clearTimer(t), done()), { once: true });
        });
        attempt += 1;
      }
    }
  }

  function restart() {
    abort?.abort();
    abort = new AbortController();
    generation += 1;
    run(generation, abort.signal);
  }

  let leaveGuard = null;

  return {
    get state() {
      return state;
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => (failing.delete(listener), listeners.delete(listener));
    },
    // Starts reading `names`. Changing the views or the extra ids restarts the one poll; it never opens a second.
    start(names, ids = []) {
      stopped = false;
      reduced = false;
      state = { ...state, large: false };
      views = [...names];
      extra = [...ids];
      state = { ...state, ready: false };
      restart();
    },
    // Another view needs loading: reload its manifest into the store and subscribe to it.
    show(names) {
      views = [...names];
      state = { ...state, ready: false };
      restart();
    },
    subscribeIds(ids) {
      if (ids.join() !== extra.join()) {
        extra = [...ids];
        restart();
      }
    },
    // The slice ids one part of the page holds (a leased transcript, a popup's plan); an empty list releases them.
    // Each owner's ids are replaced as a whole. The change is applied once per batch of calls, in a microtask: the ids the
    // subscription held before the batch's first call and lacks after its last are dropped, and the poll restarts once. An id one
    // owner gives up and another takes in the same batch is neither dropped nor asked for again.
    lease(owner, ids) {
      batchBefore ??= new Set(subscription());
      ids.length === 0 ? leases.delete(owner) : leases.set(owner, [...ids]);
      if (!restartQueued) {
        restartQueued = true;
        queueMicrotask(() => {
          restartQueued = false;
          const before = batchBefore;
          batchBefore = null;
          const after = new Set(subscription());
          if (before.size === after.size && [...before].every((id) => after.has(id))) {
            return;
          }
          const released = [...before].filter((id) => !after.has(id) && !id.startsWith("view:"));
          if (released.length > 0) {
            const slices = new Map(state.slices);
            const lists = new Map(state.lists);
            released.forEach((id) => (slices.delete(id), lists.delete(id)));
            set({ slices, lists, rev: state.rev + 1, touched: { rev: state.rev + 1, ids: released } });
          }
          restart();
        });
      }
    },
    // The full body of one list entry (`?expand=`), read cold and never stored in the slice map.
    expand: (id, offset) => get(`/list/${id}`, { expand: offset }),
    // One window of a list for a caller that keeps it itself (an overlay's own filtered view); the store holds nothing of it.
    fetchList: (id, params = {}) => get(`/list/${id}`, params),
    // The served action list of a member (its full slice id), or the root list with no id.
    fetchActions: (id = null) => get(id ? `/actions/${id}` : "/actions"),
    // The read-model counters, computed by the daemon at request time and never stored.
    readModel: () => get("/read-model"),
    // UI state (view, selection, scroll anchors, open tab) lives beside the slices and survives every resync.
    setUi(patch) {
      // A page with unsaved work (the Looks page) may hold a navigation away to ask first; it re-sends the patch to leave.
      if (patch.view !== undefined && patch.view !== state.ui.view && leaveGuard?.(patch)) {
        return;
      }
      // A hover card belongs to the view it was opened in.
      const leaves = patch.view !== undefined && patch.view !== state.ui.view && !("hover" in patch) && Boolean(state.ui.hover);
      set({ ui: { ...state.ui, ...patch, ...(leaves ? { hover: null } : null) } });
    },
    stop() {
      stopped = true;
      abort?.abort();
    },
    // One guard at a time; null clears it.
    setLeaveGuard(fn) {
      leaveGuard = fn;
    },
    lists: (id) => listOf(id),
    page,
  };

  function listOf(id) {
    return state.lists.get(id) ?? { windows: [], loading: false };
  }

  // Loads one window of a list: the newest, or `after`/`before` a cursor. At most MAX_WINDOWS are held;
  // the one farthest from the window just loaded is dropped.
  async function page(id, { after = null, before = null, limit = PAGE_LIMIT, from = null, to = null } = {}) {
    const params = { limit, ...(after ? { after } : {}), ...(before ? { before } : {}), ...(from !== null ? { from_ms: from } : {}), ...(to !== null ? { to_ms: to } : {}) };
    set({ busy: state.busy + 1 });
    let doc;
    try {
      doc = await get(`/list/${id}`, params);
    } finally {
      set({ busy: state.busy - 1 });
    }
    const cur = listOf(id);
    if (before && doc.rows.length === 0) {
      return { items: [], next: null, prev: doc.prev, archived: false, seed: null };
    }
    const win = { items: doc.rows, next: doc.next, prev: doc.prev, archived: doc.archived === true, seed: doc.seed ?? null };
    let windows = after ? [...cur.windows, win] : before ? [win, ...cur.windows] : [win];
    while (windows.length > MAX_WINDOWS) {
      windows = after ? windows.slice(1) : windows.slice(0, MAX_WINDOWS);
    }
    const lists = new Map(state.lists);
    lists.set(id, { windows, loading: false });
    set({ lists });
    return win;
  }
}

// Retry gate for reads a view repeats on every snapshot: after a failure a key waits base, 2x base, ... up to cap before it may be
// asked again, so a failing read can never run faster than the gate allows however often the store notifies.
export function retryGate({ base = 2000, cap = 60000, now = () => Date.now() } = {}) {
  const held = new Map();
  return {
    ready: (key) => now() >= (held.get(key)?.until ?? 0),
    fail(key) {
      const n = (held.get(key)?.n ?? 0) + 1;
      held.set(key, { n, until: now() + Math.min(cap, base * 2 ** (n - 1)) });
    },
    ok: (key) => held.delete(key),
  };
}

// ---- the age model: ages are computed only here, from now and a timestamp

// A slice's age text: as_of = 0 reads as the stream's freshness ("live"), anything else as now minus as_of.
export function ageOf(slice, store, now) {
  const asOf = slice?.as_of ?? 0;
  const since = asOf === 0 ? store.freshAt : asOf;
  const seconds = since ? Math.max(0, Math.floor((now - since) / 1000)) : null;
  return { live: asOf === 0, seconds };
}

export const ageText = ({ live, seconds }) => (seconds === null ? "—" : live ? `live · ${seconds} s` : seconds < 90 ? `${seconds} s ago` : seconds < 5400 ? `${Math.round(seconds / 60)} m ago` : `${Math.round(seconds / 3600)} h ago`);

// The connection chip: amber and "reconnecting" once a read has failed, "disconnected" with the retry countdown once nothing has
// been read for DISCONNECT_MS, else "live", or "updated N s ago" once the last reply is over FRESH_S old (C12). A 401 names the one
// thing that mends it; the poll goes on, and recovers once any tab has signed in again.
export const DISCONNECT_MS = 30000;
export const FRESH_S = 10;
// What a calm chip vouches for: data_stale watches the processes reading, and a file event the daemon misses waits for its reconcile.
const LIVE_COVERS = "The server is answering and its process reading is current. A file change it missed can take up to 5 min to show.";
export function freshness(store, now) {
  if (store.ended) {
    return { amber: true, text: "session ended" };
  }
  if (store.error === "401") {
    return { amber: true, text: "session expired · run ahd ui start" };
  }
  if (store.errorSince !== null) {
    if (store.retryAt != null && now - (store.freshAt || store.errorSince) > DISCONNECT_MS) {
      return { amber: true, text: store.retryAt <= now ? "✕ disconnected · retrying…" : `✕ disconnected · retry in ${Math.ceil((store.retryAt - now) / 1000)} s` };
    }
    return { amber: true, text: `reconnecting · data ${Math.max(0, Math.floor((now - store.freshAt) / 1000))} s old` };
  }
  // A reply arriving says the connection is up; only the server knows whether what it holds is current.
  if (store.dataStale) {
    const observed = Date.parse(store.observedAt ?? "");
    return { amber: true, text: observed > 0 ? `data stale · observed ${ageText({ live: false, seconds: Math.max(0, Math.floor((now - observed) / 1000)) })}` : "data stale" };
  }
  const seconds = Math.max(0, Math.floor((now - store.freshAt) / 1000));
  return { amber: false, title: LIVE_COVERS, text: !store.freshAt ? "connecting" : seconds <= FRESH_S ? "live" : `updated ${ageText({ live: false, seconds })}` };
}
