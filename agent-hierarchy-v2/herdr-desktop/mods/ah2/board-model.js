// The ah2 mod's Board data side (spec-herdr-desktop.md § View: board): no herdr or gpui imports, so preview/render.mjs runs it.
// The columns and the cards are the web Board's: plans-model.js boardOf over select.js teamRows, both byte-identical copies of
// web/ui's (kept honest by tests/web/herdr-board-model.test.mjs). The Waiting on you column is the Q&A window's own cards (qaModel).
import { COLUMNS, DONE_CAP, STATE_LABEL, boardOf, historyOf, itemsId, planChangesId, planIdOf, streamName, teamModel } from "./plans-model.js";
import { teamRows } from "./select.js";
import { questionsId } from "./questions-model.js";
import { qaModel, age, sidebarModel, within, late, NO_ANSWER } from "./model.js";
import { eventsQuery } from "./link-model.js";

const dayStartOf = (now) => new Date(now).setHours(0, 0, 0, 0);

// Running teams, closed ones left out (as model.js's sidebar does), with their plan as the web's teamModel reads it.
function teams(slices, now) {
  return teamRows(slices)
    .filter((r) => r.running && !slices.get(`team-summary:${r.id.slice("team:".length)}`)?.body?.closed_mark)
    .map((r) => teamModel(r, slices.get(planIdOf(r))?.body, now));
}

// The Board window's extra subscriptions: per running team its plan and questions, and the items of each stream boardOf can draw
// (a stream done before today has no card). They grow as replies arrive: teams, then plans, then items. A quiet team gets no
// plan or items subs, as boardOf draws none of its cards; its questions still feed Waiting on you.
export function boardSubs(slices, now = Date.now()) {
  const day = dayStartOf(now);
  return teams(slices, now).flatMap((t) => [
    questionsId(t.id),
    ...(t.quiet ? [] : [planIdOf(t), ...t.streams.filter((s) => s.state !== "done" || s.asOf >= day).map(itemsId)]),
  ]);
}

// Change marks (shown only as seen while the mod is open). `seen` maps a stream id to its items' states when the board first
// held them; an item not there is new, one in another state moved. Recorded here on first sight, so the board's open is the baseline.
// ponytail: a stream first held after the open has no baseline before it, so its items get no dots until they move.
function marker(seen, shown, itemsOf) {
  if (!seen) return () => null;
  for (const s of shown.flatMap((t) => t.streams)) {
    const items = itemsOf(s);
    if (items && !seen.has(s.id)) seen.set(s.id, new Map(items.map((i) => [i.key, i.state])));
  }
  return (s, item) => {
    if (!seen.has(s.id)) return null;
    const was = seen.get(s.id).get(item.key);
    return was === undefined ? { kind: "new" } : was !== item.state ? { kind: "moved", from: was, to: item.state } : null;
  };
}
const said = (st) => STATE_LABEL[st] ?? st;
const markText = (m) => (!m ? "" : m.kind === "new" ? "New since you opened the board" : `Moved from ${said(m.from)} to ${said(m.to)}`);
const nameOf = (asker) => (asker ?? "").split("@")[0];

// The board: the web's columns in its order. A card is plain ({ key, title, ... }) or a Q&A card ({ qa, tm }) that answers in place.
// Waiting on you holds every Q&A card first; a waiting plan item or a team's decision card the questions slice does not cover stays
// plain, with the way into Q&A. Done today keeps the newest DONE_CAP.
// Filters (the filter row): `hide` team keys (dir/name) left out, `owner` the one holder kept (a Q&A card's asker), `blockedOnly` and
// `changesOnly` as boardOf takes them (a Q&A card stays under Changes only when its own plan item is marked). `seen`: marker's map.
// The most cards a column draws; the header still counts them all and the column ends "+ N more".
export const COLUMN_CAP = 50;

export function boardModel(slices, now, { hide = new Set(), owner = "", blockedOnly = false, changesOnly = false, seen = null } = {}) {
  const running = teams(slices, now);
  const keyOf = (t) => `${t.dirId}/${t.name}`;
  const shown = running.filter((t) => !hide.has(keyOf(t)));
  const itemsOf = (s) => slices.get(itemsId(s))?.body?.items ?? null;
  const b = boardOf(shown, { itemsOf, lanes: "none", dayStart: dayStartOf(now), markOf: marker(seen, running, itemsOf), blockedOnly, changesOnly });
  const cells = b.lanes[0]?.cells ?? {};
  const qa = qaModel(slices, now);
  const ownerOk = (o) => !owner || o === owner;
  // A team's decision card is covered only when the slice holds as many cards as the team's waiting_on_you (items included:
  // waiting_rows may not list them); a decision the slice does not hold keeps the plain card with the way into Q&A.
  const held = new Map(qa.teams.map((tm) => [tm.id, tm.cards.length]));
  const waits = new Map(shown.map((t) => [`${t.dirId}/${t.name}`, t.waitingOnYou ?? 0]));
  const asked = new Set(qa.teams.flatMap((tm) => tm.cards.map((c) => `${tm.id}|${c.q.id}`)));
  const covered = (c) => {
    const k = `${c.dirId}/${c.team}`;
    return c.decision ? (held.get(k) ?? 0) >= (waits.get(k) ?? Infinity) : asked.has(`${k}|${c.stream}:${c.key}`);
  };
  const plain = (c) => ({
    key: c.id,
    mark: markText(c.mark),
    // the item sheet's selection: a plan item only (a decision card answers in Q&A, a changed stream has no single item)
    sel: c.decision || c.changed ? null : { key: c.id, dirId: c.dirId, team: c.team, stream: c.stream, item: c.key, title: c.title, owner: c.owner || "" },
    title: c.title,
    id: c.decision ? "" : c.key,
    ref: c.decision ? "" : `${streamName(c.stream)} / ${c.key}`,
    team: c.team,
    owner: c.owner || "",
    done: c.column === "done",
    blocked: c.state === "blocked" ? `blocked${c.since ? ` ${age(now - c.since)}` : ""}` : "",
    ask: c.column === "waiting",
  });
  const columns = COLUMNS.map(([key, label]) => {
    const all = (cells[key] ?? []).filter((c) => ownerOk(c.owner || ""));
    let cards;
    if (key === "waiting") {
      const marked = new Set(all.map((c) => `${c.dirId}/${c.team}|${c.stream}:${c.key}`));
      const asks = qa.teams.filter((tm) => !hide.has(tm.id)).flatMap((tm) => tm.cards
        .filter((c) => ownerOk(nameOf(c.asker)) && (!changesOnly || marked.has(`${tm.id}|${c.q.id}`)))
        .map((c) => ({ qa: c, tm })));
      cards = [...asks, ...all.filter((c) => !covered(c)).map(plain)];
    }
    else cards = all.map(plain);
    // every column draws at most its cap (Done today its newest DONE_CAP): each card is elements built on Herdr's UI thread
    const cap = key === "done" ? DONE_CAP : COLUMN_CAP;
    return { key, label, cards: cards.slice(0, cap), count: cards.length, more: Math.max(0, cards.length - cap) };
  });
  // The filter row's choices: every running team (a chip never vanishes when switched off), and the holders on the cards in reach.
  const lane = (t) => Boolean(slices.get(`team-summary:${keyOf(t)}`)?.body?.lane?.parent_team);
  const owners = new Set([...Object.values(cells).flat().map((c) => c.owner), ...qa.teams.flatMap((tm) => tm.cards.map((c) => nameOf(c.asker)))]);
  if (owner) owners.add(owner);
  owners.delete("");
  return {
    loaded: qa.loaded, count: qa.count, columns, empty: columns.every((c) => c.cards.length === 0),
    filtered: hide.size > 0 || Boolean(owner) || blockedOnly || changesOnly,
    teams: running.map((t) => ({ key: keyOf(t), name: t.name, lane: lane(t), on: !hide.has(keyOf(t)) })),
    owners: [...owners].sort(),
  };
}

// The item sheet's owner: the member of that team with the owner's name, as the sidebar draws it (state, word, age, pane); null if
// the team lists no such member.
export function ownerOf(slices, sel, now) {
  if (!sel?.owner) return null;
  const find = (list) => list.reduce((hit, t) => hit ?? (`${t.dir}/${t.name}` === `${sel.dirId}/${sel.team}` ? t : find(t.lanes)), null);
  return find(sidebarModel(slices, now).teams)?.members.find((m) => m.name === sel.owner) ?? null;
}

// The item sheet's log: one read of its team's plan changes (newest 200), on the card's click, never on render or poll. `exec` is
// the runner's (ah.js: herdr's exec, run where ah runs; passed in, so this file stays free of herdr). `read` (link-model.js Link
// read) asks ahd's bridge the same poll first, so no ah starts while the bridge is up. Resolves to historyOf rows (newest first),
// or { error }.
export async function readItemLog(exec, sel, sleep = null, read = null) {
  const id = planChangesId(sel.dirId, sel.team);
  let text = null;
  try {
    if (read) text = await read(`/ui/v1/view?${eventsQuery({ sub: id, since: 0, epoch: null, wait: "0" })}`, 15000);
    if (text === null) {
      const r = await within(exec("ah", ["view", "events", "--sub", id, "--since", "0", "--wait", "0"], { timeout: 15000 }), sleep, 15000);
      if (r.code !== 0) return { error: "ahd not running" };
      text = r.stdout;
    }
  } catch (e) {
    return { error: e?.down ?? (late(e) ? NO_ANSWER : "ah is not on Herdr's PATH") };
  }
  try {
    return { rows: itemLog(JSON.parse(text), sel) };
  } catch {
    return { error: "Could not read the log" };
  }
}

export const itemLog = (reply, sel) =>
  historyOf((reply.slices ?? []).find((s) => s.id === planChangesId(sel.dirId, sel.team))?.body?.rows?.filter((r) => r.item === sel.item) ?? [], sel.stream, said);
