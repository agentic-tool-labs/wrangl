// What the viewer has already seen of each plan, kept in the browser: the last time they looked at a team's plans, and for each
// stream the state of every item they held. Marks are the difference between that and what is held now. Every storage access
// is wrapped: a store that is absent or throws means there is nothing to remember, and the page says so.

const LOOK_KEY = "ah.plans.look";
const SEEN_KEY = "ah.plans.seen";
export const SEEN_CAP = 200_000;

// An item's state is stored as one letter, its key without the stream prefix.
const CODE = { done: "d", in_progress: "p", not_started: "n", next: "x", blocked: "b", waiting_user: "w", waiting_orch: "o", dropped: "r" };
const STATE = Object.fromEntries(Object.entries(CODE).map(([state, code]) => [code, state]));
export const stateOf = (code) => STATE[code] ?? code;

const storageOf = () => {
  try {
    return globalThis.localStorage;
  } catch {
    return undefined;
  }
};

const object = (v) => (v !== null && typeof v === "object" && !Array.isArray(v) ? v : {});

// The item's key without its stream, which is the part that identifies it.
export const hashOf = (stream, key) => (key.startsWith(`${stream}:`) ? key.slice(stream.length + 1) : key);

// What the viewer saw of one stream: when its plan last moved, and each item's state. `i: null` says the stream was marked
// seen without its items being held.
export function snapshotOf(stream, asOf, items) {
  return { a: asOf, i: items === null ? null : Object.fromEntries(items.map((it) => [hashOf(stream, it.key), CODE[it.state] ?? it.state])) };
}

// Reads both values. `blocked` says storage could not be read at all; stored nonsense reads as nothing stored.
export function loadTrack(now, storage = storageOf()) {
  const empty = { look: {}, seen: {}, mountedAt: now };
  let raw;
  try {
    raw = [storage.getItem(LOOK_KEY), storage.getItem(SEEN_KEY)];
  } catch {
    return { blocked: true, ...empty };
  }
  try {
    const look = object(JSON.parse(raw[0] ?? "{}"));
    return { blocked: false, look: Object.fromEntries(Object.entries(look).filter(([, v]) => Number.isFinite(v))), seen: object(JSON.parse(raw[1] ?? "{}")), mountedAt: now };
  } catch {
    return { blocked: false, ...empty };
  }
}

// `seen` kept under the cap: the streams with the oldest plan time go first.
export function capped(seen, cap = SEEN_CAP) {
  const out = JSON.parse(JSON.stringify(seen));
  while (JSON.stringify(out).length > cap) {
    let oldest = null;
    for (const [scope, streams] of Object.entries(out)) {
      for (const [stream, snap] of Object.entries(streams)) {
        if (oldest === null || snap.a < oldest.a) {
          oldest = { scope, stream, a: snap.a };
        }
      }
    }
    if (oldest === null) {
      break;
    }
    delete out[oldest.scope][oldest.stream];
    if (Object.keys(out[oldest.scope]).length === 0) {
      delete out[oldest.scope];
    }
  }
  return out;
}

export function saveTrack(track, storage = storageOf()) {
  try {
    storage.setItem(LOOK_KEY, JSON.stringify(track.look));
    storage.setItem(SEEN_KEY, JSON.stringify(capped(track.seen)));
    return true;
  } catch {
    return false;
  }
}

// What has changed in one stream since the viewer last looked.
//   since: the last look of the stream's team (null: never looked, nothing is marked)
//   snapshot: what the viewer saw of this stream, or undefined
//   items: the stream's held items, or null while they are not held
// Returns {changed, stream, items: Map(item key -> {kind, from?, to?}), removed}.
export function marksOf(stream, { since, snapshot, items }) {
  const marks = new Map();
  const known = snapshot?.i ?? null;
  const streamChanged = since !== null && stream.asOf > since;
  let removed = 0;
  if (items !== null) {
    const present = new Set();
    for (const item of items) {
      const h = hashOf(stream.stream, item.key);
      present.add(h);
      if (known !== null && !(h in known)) {
        marks.set(item.key, { kind: "new" });
      } else if (known !== null && stateOf(known[h]) !== item.state) {
        marks.set(item.key, { kind: "moved", from: stateOf(known[h]), to: item.state });
      } else if (since !== null && item.done_seen_at > since) {
        marks.set(item.key, { kind: "done" });
      }
    }
    if (known !== null) {
      removed = Object.keys(known).filter((h) => !present.has(h)).length;
    }
  }
  return { changed: streamChanged || marks.size > 0 || removed > 0, stream: streamChanged, items: marks, removed };
}
