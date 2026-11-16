// What the viewer last saw of each team's Sitrep, kept in the browser (ah-ui-ia §3.4): `ah.sitrep.seen` =
// {<team id>: {looked: <ms>, snap: {<row key>: "c"|"p"|"b"|"s"|"n"}}}, capped at TEAM_CAP teams, oldest `looked` evicted first. The
// shape and the wrapping follow plans-seen.js, without sharing its keys: storage that is absent or throws means no marks.

const KEY = "ah.sitrep.seen";
export const TEAM_CAP = 50;
const COLUMNS = [["waiting_on_you", "w"], ["completed", "c"], ["in_progress", "p"], ["blocked", "b"], ["not_moving", "m"], ["stale", "s"], ["up_next", "n"]];

const storageOf = () => {
  try {
    return globalThis.localStorage;
  } catch {
    return undefined;
  }
};

const object = (v) => (v !== null && typeof v === "object" && !Array.isArray(v) ? v : {});

// Every row key the body shows, with its column letter. Ship and merge records go in by their own key, under "c".
export const snapOf = (body) => Object.fromEntries(COLUMNS.flatMap(([list, code]) => (body?.[list] ?? []).map((r) => [r.key, code])));

// The stored snap for one team, or null when there is none or storage cannot be read.
export function loadSeen(teamId, storage = storageOf()) {
  try {
    const snap = object(JSON.parse(storage.getItem(KEY) ?? "{}"))[teamId]?.snap;
    return snap && typeof snap === "object" ? snap : null;
  } catch {
    return null;
  }
}

// Writes what the viewer saw now, keeping the TEAM_CAP most recently looked teams. False when storage cannot be written.
export function saveSeen(teamId, body, now, storage = storageOf()) {
  try {
    let all;
    try {
      all = object(JSON.parse(storage.getItem(KEY) ?? "{}"));
    } catch {
      all = {};
    }
    all[teamId] = { looked: now, snap: snapOf(body) };
    const kept = Object.entries(all).sort(([, a], [, b]) => (b?.looked ?? 0) - (a?.looked ?? 0)).slice(0, TEAM_CAP);
    storage.setItem(KEY, JSON.stringify(Object.fromEntries(kept)));
    return true;
  } catch {
    return false;
  }
}

// Each row against the at-open baseline: "new" when its key is not in the snap, "moved" (with the column it was in) when it
// was in another column, nothing otherwise. `left` counts snap keys in none of the lists. No baseline, no marks.
export function marksOf(snap, body) {
  const marks = new Map();
  if (!snap) {
    return { marks, left: 0, changed: false };
  }
  const now = snapOf(body);
  for (const [key, code] of Object.entries(now)) {
    if (!(key in snap)) {
      marks.set(key, { kind: "new" });
    } else if (snap[key] !== code) {
      marks.set(key, { kind: "moved", from: snap[key] });
    }
  }
  const left = Object.keys(snap).filter((k) => !(k in now)).length;
  return { marks, left, changed: marks.size > 0 || left > 0 };
}

// Which Sitrep regions this viewer has opened or closed: `ah.sitrep.regions` = {<column letter>: true|false}. Absent or throwing storage means the defaults.
const REGIONS_KEY = "ah.sitrep.regions";

export function loadRegions(storage = storageOf()) {
  try {
    return object(JSON.parse(storage.getItem(REGIONS_KEY) ?? "{}"));
  } catch {
    return {};
  }
}

export function saveRegions(regions, storage = storageOf()) {
  try {
    storage.setItem(REGIONS_KEY, JSON.stringify(regions));
    return true;
  } catch {
    return false;
  }
}
