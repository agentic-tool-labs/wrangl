// Scoped divergences for the contract runner.
//
// A divergence row that names a scope counts as `diverged` instead of a failure only when the recorded value and the actual value are
// equal once the scope's allowed differences are removed from both; any other difference still fails the row. Two scopes exist:
//
//   status-team  The status document of a team T the old rule dropped and the new rule keeps. Allowed, and only in a status document
//                (an object with schema, teams, timeline and owners): the element of `teams` for T; T in the `teams` of an `owners`
//                entry (an entry left with no team goes with it); the aggregate fields of the top-level `timeline` entries and of the
//                `timeline` of an `owners` entry that named T; and extra `member_sessions` entries, the recorded ones all still present
//                and in order. Nothing else, at any depth, and no key is masked by name alone.
//   sweep-outcome  A team record the old rule swept or reported orphaned and the new rule keeps; see lib-sweep-outcome.mjs. The status
//                document is then compared through the status-team view for `statusTeam` (null names the legacy team.json team).
//   json-paths   Named paths into the JSON document of `stdout`: `a.b`, with `key[field=value]` choosing the array element whose
//                `field` is `value`. A path names a value that is deleted from both sides.
//
// Placeholders such as <ISO:4> number values by first appearance, so removing a value that held some shifts every later number; both
// sides are renumbered by first appearance after the removal.

import { sweepOutcomeViews } from "./lib-sweep-outcome.mjs";

const DERIVED = ["tone", "text", "short"];

const isObject = (v) => v !== null && typeof v === "object" && !Array.isArray(v);

function parseDoc(text) {
  if (typeof text !== "string" || !/^\s*\{/.test(text)) return null;
  try { return { doc: JSON.parse(text), tail: text.endsWith("\n") ? "\n" : "" }; } catch { return null; }
}

function isStatusDocument(doc) {
  return isObject(doc) && Array.isArray(doc.teams) && Array.isArray(doc.timeline) && Array.isArray(doc.owners) && "schema" in doc;
}

/**
 * What team T adds to the aggregates over all teams: its members that are live, its dispatches, and the sessions of its members. Read
 * from T's own element of the actual document, so an aggregate may differ by exactly that much and no more.
 */
function contributionOf(doc, team) {
  const element = doc.teams.find((t) => isObject(t) && (t.team === team || t.name === team));
  const members = isObject(element) && Array.isArray(element.members) ? element.members.filter(isObject) : [];
  return {
    live: members.filter((m) => m.live !== false).length,
    pipeline: isObject(element) && element.pipeline !== null && element.pipeline !== undefined,
    out: isObject(element) && Array.isArray(element.dispatches) ? element.dispatches.length : 0,
    sessions: new Set(members.map((m) => m.session_id).filter((s) => typeof s === "string" && s.length > 0)),
  };
}

/**
 * The timeline fields of one entry pair (recorded `e`, actual `a`) that T's presence may change, and only in the direction T's presence
 * pushes them: `live` rises by exactly T's live members, and `stalled` may rise by at most T's dispatches; `visible` may go false to true when T has a
 * pipeline or a live member or a dispatch, which is what makes an entry visible. A fall, a larger rise, or a flip with nothing of T's to
 * explain it is not allowed, and the field is then compared as it is.
 */
function allowedFields(e, a, contribution) {
  const fields = new Set();
  if (!isObject(e) || !isObject(a)) return fields;
  const rises = (key, bound) => typeof e[key] === "number" && typeof a[key] === "number" && a[key] - e[key] >= 0 && a[key] - e[key] <= bound;
  if (typeof e.live === "number" && typeof a.live === "number" && a.live - e.live === contribution.live) fields.add("live");
  if (rises("stalled", contribution.out)) fields.add("stalled");
  const explained = contribution.pipeline || contribution.live > 0 || contribution.out > 0;
  if (e.visible === false && a.visible === true && explained) fields.add("visible");
  return fields;
}

/** The per-entry allowed fields of two timelines compared entry by entry, or none when their lengths differ. */
function timelineAllowance(recorded, actual, contribution) {
  if (!Array.isArray(recorded) || !Array.isArray(actual) || recorded.length !== actual.length) return null;
  return recorded.map((e, j) => allowedFields(e, actual[j], contribution));
}

/**
 * The timeline entries with T's contribution taken off the `out` count it changes (the actual side), the allowed fields of
 * `allowance` masked, and the fields derived from the counts blanked wherever the contribution is not zero (both sides). Every other
 * field of an entry is compared as it is.
 */
function withoutContribution(entries, contribution, subtract, allowance) {
  const changes = contribution.live !== 0 || contribution.out !== 0;
  return entries.map((e, j) => {
    if (!isObject(e)) return e;
    const copy = { ...e };
    if (subtract && typeof copy.out === "number") copy.out -= contribution.out;
    // A live count that did not rise by exactly T's members is compared with T's members taken off, so a rise that falls short shows.
    if (subtract && typeof copy.live === "number" && !allowance?.[j]?.has("live")) copy.live -= contribution.live;
    for (const k of allowance?.[j] ?? []) if (k in copy) copy[k] = "<allowed>";
    if (changes) for (const k of DERIVED) if (k in copy) copy[k] = "<derived>";
    return copy;
  });
}

function isSubsequence(small, big) {
  let i = 0;
  for (const x of big) if (i < small.length && JSON.stringify(small[i]) === JSON.stringify(x)) i++;
  return i === small.length;
}

/** The entries of `big` that a match of `small` as a subsequence leaves over, or null when `small` is not one. */
function leftOver(small, big) {
  const used = new Array(big.length).fill(false);
  let i = 0;
  big.forEach((x, j) => {
    if (i < small.length && JSON.stringify(small[i]) === JSON.stringify(x)) { used[j] = true; i++; }
  });
  return i === small.length ? big.filter((_, j) => !used[j]) : null;
}

const ownerKey = (o) => JSON.stringify(isObject(o) ? o.sessions : null);

/** The owner sessions of the entries that name team T, which T's presence changes. */
function ownersNaming(doc, team) {
  return doc.owners.filter((o) => isObject(o) && Array.isArray(o.teams) && o.teams.includes(team)).map(ownerKey);
}

/**
 * The status document with team T's contribution removed. `sessions` is the member_sessions to compare against, or null; `touched` the
 * owner sessions (either side) whose entry names T, whose timeline then includes T's contribution; `contribution` T's, read from the
 * actual document; `subtract` whether this is the actual side, whose counts carry it.
 */
function withoutTeam(doc, team, sessions, touched, contribution, subtract, allowance) {
  const owners = [];
  for (const o of doc.owners) {
    const named = isObject(o) && Array.isArray(o.teams) && o.teams.includes(team);
    const rest = named ? o.teams.filter((t) => t !== team) : o?.teams;
    if (named && rest.length === 0) continue;
    const includes = isObject(o) && Array.isArray(o.timeline) && touched.has(ownerKey(o));
    const timeline = includes ? withoutContribution(o.timeline, contribution, subtract && named, allowance.owners.get(ownerKey(o))) : o?.timeline;
    owners.push(isObject(o) ? { ...o, teams: rest ?? o.teams, timeline } : o);
  }

  const out = {
    ...doc,
    teams: doc.teams.filter((t) => !(isObject(t) && (t.team === team || t.name === team))),
    owners,
    timeline: withoutContribution(doc.timeline, contribution, subtract, allowance.top),
  };
  if (Array.isArray(doc.member_sessions)) out.member_sessions = sessions ?? doc.member_sessions;
  return out;
}

/** The member_sessions of every status document inside `value`, in walk order. */
function sessionsOf(value, found = []) {
  if (typeof value === "string") {
    const parsed = parseDoc(value);
    if (parsed && isStatusDocument(parsed.doc)) found.push(Array.isArray(parsed.doc.member_sessions) ? parsed.doc.member_sessions : null);
  } else if (Array.isArray(value)) {
    for (const x of value) sessionsOf(x, found);
  } else if (isObject(value)) {
    for (const v of Object.values(value)) sessionsOf(v, found);
  }
  return found;
}

function mapDocuments(value, replace, counter = { i: 0 }) {
  if (typeof value === "string") {
    const parsed = parseDoc(value);
    if (parsed && isStatusDocument(parsed.doc)) return JSON.stringify(replace(parsed.doc, counter.i++)) + parsed.tail;
    return value;
  }
  if (Array.isArray(value)) return value.map((x) => mapDocuments(x, replace, counter));
  if (isObject(value)) return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, mapDocuments(v, replace, counter)]));
  return value;
}

function documentsOf(value) {
  const docs = [];
  mapDocuments(value, (doc) => { docs.push(doc); return doc; });
  return docs;
}

function viewStatus(expect, actual, team) {
  // The actual member_sessions may carry extra entries (the kept team's) when every recorded one is still there, in order.
  const recordedDocs = documentsOf(expect);
  const actualDocs = documentsOf(actual);
  const touched = (i) => new Set([...(recordedDocs[i] ? ownersNaming(recordedDocs[i], team) : []), ...(actualDocs[i] ? ownersNaming(actualDocs[i], team) : [])]);
  const recorded = recordedDocs.map((d) => (Array.isArray(d.member_sessions) ? d.member_sessions : null));
  const contribution = (i) => (actualDocs[i] ? contributionOf(actualDocs[i], team) : { live: 0, out: 0, sessions: new Set() });
  // Extra member_sessions entries are allowed only when they are sessions of T's own members.
  const sessionsFor = (doc, i) => {
    const extra = recorded[i] && Array.isArray(doc.member_sessions) ? leftOver(recorded[i], doc.member_sessions) : null;
    return extra && extra.every((s) => contribution(i).sessions.has(s)) ? recorded[i] : null;
  };
  // The fields T's presence may move, decided once per document pair so that both sides mask the same ones.
  const allowance = (i) => {
    const r = recordedDocs[i];
    const a = actualDocs[i];
    const owners = new Map();
    if (!r || !a) return { top: null, owners };
    for (const ro of r.owners) {
      const ao = a.owners.find((x) => isObject(x) && isObject(ro) && ownerKey(x) === ownerKey(ro));
      if (isObject(ro) && ao && touched(i).has(ownerKey(ro))) owners.set(ownerKey(ro), timelineAllowance(ro.timeline, ao.timeline, contribution(i)));
    }
    return { top: timelineAllowance(r.timeline, a.timeline, contribution(i)), owners };
  };
  return {
    expect: mapDocuments(expect, (doc, i) => withoutTeam(doc, team, null, touched(i), contribution(i), false, allowance(i))),
    actual: mapDocuments(actual, (doc, i) => withoutTeam(doc, team, sessionsFor(doc, i), touched(i), contribution(i), true, allowance(i))),
  };
}

function deletePath(doc, path) {
  const segments = path.split(".");
  let nodes = [doc];
  for (let i = 0; i < segments.length; i++) {
    const m = /^([^[\]]+)(?:\[([^=\]]+)=([^\]]*)\])?$/.exec(segments[i]);
    if (!m) return;
    const [, key, field, wanted] = m;
    const last = i === segments.length - 1;
    const next = [];
    for (const n of nodes) {
      if (!isObject(n)) continue;
      if (last && field === undefined) { delete n[key]; continue; }
      const child = n[key];
      if (field === undefined) { next.push(child); continue; }
      if (!Array.isArray(child)) continue;
      const picked = child.filter((e) => isObject(e) && String(e[field]) === wanted);
      if (last) { for (const e of picked) child.splice(child.indexOf(e), 1); } else next.push(...picked);
    }
    nodes = next;
  }
}

function pathsView(value, paths) {
  if (!isObject(value)) return value;
  const out = { ...value };
  if (typeof out.stdout === "string") {
    const parsed = parseDoc(out.stdout);
    if (parsed) {
      for (const p of paths) deletePath(parsed.doc, p);
      out.stdout = JSON.stringify(parsed.doc, null, 2) + parsed.tail;
    }
  }
  return out;
}

function renumber(value) {
  const seen = new Map();
  return JSON.parse(JSON.stringify(value).replace(/<([A-Z]+):(\d+)>/g, (m, kind, n) => {
    const id = `${kind}:${n}`;
    if (!seen.has(id)) seen.set(id, [...seen.keys()].filter((k) => k.startsWith(`${kind}:`)).length + 1);
    return `<${kind}:${seen.get(id)}>`;
  }));
}

/**
 * The status-write operation's result: the write-if-changed flag of `.state-status/.executed-ops.jsonl` entries. Recorded false and
 * actual true is the one difference allowed, and only when the actual tree holds a status document that carries team T, which is
 * what changed the document so that it was written.
 */
function withStatusWriteAllowed(expect, actual, team) {
  const addedOf = (v) => (isObject(v) && isObject(v.tree_diff) && Array.isArray(v.tree_diff.added) ? v.tree_diff.added : []);
  const keptTeamShown = addedOf(actual).some((e) => {
    const parsed = isObject(e) ? parseDoc(e.content) : null;
    return parsed && isStatusDocument(parsed.doc) && parsed.doc.teams.some((t) => isObject(t) && (t.team === team || t.name === team));
  });
  if (!keptTeamShown) return { expect, actual };

  const isStatusOps = (e) => isObject(e) && typeof e.path === "string" && e.path.endsWith(".state-status/.executed-ops.jsonl") && typeof e.content === "string";
  const lines = (e) => e.content.split("\n").map((l) => { try { return l ? JSON.parse(l) : null; } catch { return undefined; } });
  const exp = structuredClone(expect);
  const act = structuredClone(actual);
  for (const e of addedOf(exp).filter(isStatusOps)) {
    const a = addedOf(act).find((x) => isStatusOps(x) && x.path === e.path);
    if (!a) continue;
    const el = lines(e);
    const al = lines(a);
    if (el.length !== al.length || el.includes(undefined) || al.includes(undefined)) continue;
    // The status-write operation's result holds the one key `written`; a result with any other key belongs to another operation.
    const isStatusWrite = (line) => isObject(line?.result) && Object.keys(line.result).length === 1 && "written" in line.result;
    for (let i = 0; i < el.length; i++) {
      if (isStatusWrite(el[i]) && isStatusWrite(al[i]) && el[i].result.written === false && al[i].result.written === true) { el[i].result.written = "<kept-team>"; al[i].result.written = "<kept-team>"; }
    }

    const join = (ls) => ls.map((l) => (l === null ? "" : JSON.stringify(l))).join("\n");
    e.content = join(el);
    a.content = join(al);
  }

  return { expect: exp, actual: act };
}

/** The recorded and the actual value with the scope's allowed differences removed from both. */
export function scopedViews(scope, expect, actual) {
  if (scope.kind === "status-team") {
    const allowed = scope.statusWrite ? withStatusWriteAllowed(expect, actual, scope.team) : { expect, actual };
    const { expect: e, actual: a } = viewStatus(allowed.expect, allowed.actual, scope.team);
    return { expect: renumber(e), actual: renumber(a) };
  }
  if (scope.kind === "sweep-outcome") {
    const swept = sweepOutcomeViews(scope, expect, actual);
    const { expect: e, actual: a } = viewStatus(swept.expect, swept.actual, scope.statusTeam ?? null);
    return { expect: renumber(e), actual: renumber(a) };
  }
  if (scope.kind === "json-paths") {
    return { expect: renumber(pathsView(expect, scope.paths)), actual: renumber(pathsView(actual, scope.paths)) };
  }
  throw new Error(`unknown divergence scope ${scope.kind}`);
}
