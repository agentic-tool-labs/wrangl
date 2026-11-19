// The feed's data side (spec-herdr-desktop.md § View: feed; P1 Live feed, atp-1496): no herdr or gpui imports, so the fixture
// render runs it too. A Now strip (who is working on what), then one stream, newest first, grouped by minute: briefs and reports,
// plan moves, alerts, questions waiting on you, and member state changes the mod saw while it was open (ahd keeps no list of
// those, so they are labelled as seen here). The team and party reads are the web's own select.js, the plan reads plans-model.js,
// both byte-identical copies (kept honest by tests). Message rows are the web's Live feed rows (web/ui/app.js FeedRow).
import { askerLabel, teamRows, partyName } from "./select.js";
import { STATES, age, sidebarModel, qaModel, qaSubs } from "./model.js";
import { planChangesId, STATE_LABEL, msOf } from "./plans-model.js";

// The newest exchanges and plan changes read per team, the rows the stream keeps, the Now strip's rows, the seen changes kept.
export const FEED_PER_TEAM = 10;
export const PLAN_PER_TEAM = 20;
export const FEED_SHOWN = 60;
export const NOW_SHOWN = 6;
export const SEEN_CAP = 50;
// The kind filter chips, in order. Internal messages have their own chip, off by default, as the web hides them.
export const KINDS = [["msg", "Messages"], ["plan", "Plan"], ["alert", "Alerts"], ["ask", "Questions"], ["seen", "Members"]];

const feedIdOf = (r) => `list:feed:team:${r.dirId}/${r.name}`;
const closedMark = (slices, r) => slices.get(`team-summary:${r.id.slice("team:".length)}`)?.body?.closed_mark;
// Running teams in the team list's order; a team its summary marks closed is left out, as the sidebar and the Board leave it out.
const running = (slices) => teamRows(slices).filter((r) => r.running && !closedMark(slices, r));
// A feed list holds bare exchange ids, newest first; each one's slice is exchange:<dirId>/<id> (MessagesLane.SliceIdOfDirId).
const newestIds = (slices, r) => (slices.get(feedIdOf(r))?.body?.ids ?? []).slice(0, FEED_PER_TEAM).map((x) => `exchange:${r.dirId}/${x}`);

// The feed's extra subscriptions: per running team its feed list, the exchange slice of each of its newest ids, its plan changes,
// and its questions. The set grows in two steps (the lists name the ids), as the Q&A window's does.
export function feedSubs(slices) {
  const out = [];
  for (const r of running(slices)) out.push(feedIdOf(r), ...newestIds(slices, r), planChangesId(r.dirId, r.name));
  return [...out, ...qaSubs(slices, false)];
}

const hhmm = (ms) => {
  const d = new Date(ms);
  return ms > 0 ? `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}` : "";
};
const word = (state) => (STATES[state] ?? STATES.unknown).word.toLowerCase();
const flat = (teams) => teams.flatMap((t) => [t, ...flat(t.lanes)]);
// "orchestrator@ah2-tm4" -> ["orchestrator", "ah2-tm4"]
const split = (who) => {
  const at = (who ?? "").indexOf("@");
  return at < 0 ? [who ?? "", null] : [who.slice(0, at), who.slice(at + 1)];
};

// The member a row's Focus terminal goes to: the one the event is about (a brief's or report's worker, the plan mover, the asker,
// the alert's member). A party that names a lane (a brief to a sub-orchestrator) is that lane's orchestrator. null when unknown.
function memberIndex(teams) {
  const byTeam = new Map(teams.map((t) => [`${t.dir}/${t.name}`, t]));
  const byKey = new Map(teams.flatMap((t) => t.members.map((m) => [m.key, m])));
  const of = (dir, team, name) => {
    const tm = byTeam.get(`${dir}/${team}`);
    const mb = tm?.members.find((m) => m.name === name);
    if (mb) return mb;
    return byTeam.get(`${dir}/${name}`)?.members.find((m) => m.name === "orchestrator") ?? null;
  };
  return { of, byKey };
}

// Member state changes seen while the feed is open: compare each member's state with the last render's. The first call only
// records (nothing was seen before it). Returns a new memo { states, events }, events newest first, at most SEEN_CAP.
export function seenStates(memo, slices, now) {
  const states = new Map();
  const events = [];
  for (const tm of flat(sidebarModel(slices, now).teams)) {
    for (const mb of tm.members) {
      states.set(mb.key, mb.state);
      const was = memo?.states?.get(mb.key);
      if (was && was !== mb.state) events.push({ key: `seen|${mb.key}|${now}`, at: now, dir: tm.dir, team: tm.name, name: mb.name, label: mb.label ?? mb.name, from: was, to: mb.state });
    }
  }
  return { states, events: [...events, ...(memo?.events ?? [])].slice(0, SEEN_CAP) };
}

// The feed: the Now strip, the stream grouped by minute with the "new since you looked" divider, and what the chips offer.
// opts: looked (ms, 0 = never), off (kind keys hidden), internal (show internal messages), team (`dir/name` or null), seen (seenStates events).
export function feedModel(slices, now, { looked = 0, off = [], internal = false, team = null, seen = [] } = {}) {
  const side = sidebarModel(slices, now);
  const teams = flat(side.teams);
  const { of, byKey } = memberIndex(teams);
  const lanes = new Set(teams.filter((t) => t.parent).map((t) => `${t.dir}/${t.name}`));
  const all = [];
  // `team` is what the row shows: the team's display name (ah2-218); `teamId` stays the key
  const shown = new Map(teams.map((t) => [`${t.dir}/${t.name}`, t.label ?? t.name]));
  const add = (dir, name, e) => all.push({ ...e, teamId: `${dir}/${name}`, team: shown.get(`${dir}/${name}`) ?? name, lane: lanes.has(`${dir}/${name}`), clock: hhmm(e.at), age: e.at ? age(now - e.at) : "" });

  for (const r of running(slices)) {
    for (const id of newestIds(slices, r)) {
      const b = slices.get(id)?.body;
      if (!b) continue;
      const at = Date.parse(b.created ?? "") || 0;
      // ah2-220 Amendment 2: the labels are for display; the terminal lookup (`of`) takes the real name
      const from = partyName(b, "from", slices) ?? "";
      const to = partyName(b, "to", slices) ?? "";
      const toName = partyName(b, "to") ?? "";
      // the daemon's ETA state (EtaProjection.cs), served on an open brief the Orchestrator sent; only overdue is drawn
      const late = b.open === true && b.state === "overdue";
      const isInternal = b.class === "internal";
      add(r.dirId, r.name, {
        key: id, kind: "msg", at, internal: isInternal, late,
        glyph: b.open ? "→" : "←", tone: b.open ? "muted" : "done",
        title: b.title || b.slug || "",
        sub: [late && "overdue", b.open ? `${from} → ${to}` : `${to} reported`, isInternal && "internal"].filter(Boolean).join(" · "),
        mb: of(r.dirId, b.team ?? r.name, toName),
      });
    }
    for (const c of (slices.get(planChangesId(r.dirId, r.name))?.body?.rows ?? []).slice(0, PLAN_PER_TEAM)) {
      const move = c.kind === "state" ? `moved to ${STATE_LABEL[c.to] ?? c.to}` : c.kind === "added" || c.kind === "dropped" ? c.kind : null;
      if (!move) continue; // a priority change is not a move
      const [by] = split(c.by);
      add(r.dirId, r.name, {
        key: `plan|${r.dirId}/${r.name}|${c.stream}|${c.seq}`, kind: "plan", at: msOf(c.at) ?? 0,
        glyph: c.to === "done" ? "✓" : c.kind === "dropped" ? "×" : c.kind === "added" ? "+" : "↗",
        tone: c.to === "done" ? "done" : c.kind === "state" ? "working" : "muted",
        title: c.item, sub: by ? `${move} · ${by}` : move, mb: by ? of(r.dirId, r.name, by) : null,
      });
    }
  }
  for (const t of teams) {
    const b = slices.get(`team-summary:${t.dir}/${t.name}`)?.body;
    // Orchestrator-audience alerts are the orchestrator's, not the user's (model.js sidebarModel, select.js summaryRow).
    for (const a of (b?.alerts ?? []).filter((x) => x.audience !== "orchestrator")) {
      add(t.dir, t.name, {
        key: `alert|${t.dir}/${t.name}|${a.kind}|${a.since ?? a.text}`, kind: "alert", at: msOf(a.since) ?? 0,
        glyph: "⚠", tone: "blocked", title: a.text || a.title || a.kind, sub: "", mb: byKey.get(a.member) ?? null,
      });
    }
  }
  for (const tm of qaModel(slices, now).teams) {
    const [dir, name] = [tm.id.slice(0, tm.id.indexOf("/")), tm.id.slice(tm.id.indexOf("/") + 1)];
    for (const c of tm.cards) {
      const [asker, askTeam] = split(c.asker);
      add(dir, name, {
        key: `ask|${c.key}`, kind: "ask", at: c.q.at ?? 0, glyph: "?", tone: "blocked",
        title: c.text, sub: asker ? `${askerLabel(slices, dir, askTeam ?? name, asker)?.label ?? asker} asks you` : "waiting on you", mb: asker ? of(dir, askTeam ?? name, asker) : null,
      });
    }
  }
  for (const s of seen) {
    add(s.dir, s.team, {
      key: s.key, kind: "seen", at: s.at, mark: s.to,
      title: `${s.label ?? s.name}: ${word(s.from)} → ${word(s.to)}`, sub: "seen while open", mb: of(s.dir, s.team, s.name),
    });
  }

  const hidden = new Set(off);
  const rows = all
    .filter((e) => !hidden.has(e.kind) && (internal || !e.internal) && (!team || e.teamId === team))
    .sort((a, b) => b.at - a.at || (a.key < b.key ? 1 : -1))
    .slice(0, FEED_SHOWN);
  const newCount = looked > 0 ? rows.filter((e) => e.at > looked).length : 0;

  // Minute groups, newest first; the divider sits after the last new row, so a minute it cuts becomes two groups.
  const groups = [];
  let g = null;
  rows.forEach((e, i) => {
    if (newCount > 0 && i === newCount) {
      groups.push({ key: "new", divider: newCount });
      g = null;
    }
    if (!g || g.clock !== e.clock) groups.push((g = { key: `${e.clock}|${i}`, clock: e.clock, rows: [] }));
    g.rows.push(e);
  });
  if (newCount > 0 && newCount === rows.length) groups.push({ key: "new", divider: newCount });

  const shownTeams = team ? teams.filter((t) => `${t.dir}/${t.name}` === team) : teams;
  const busy = shownTeams.flatMap((t) => t.members.filter((m) => m.state === "working" || m.state === "blocked").map((m) => ({ ...m, team: t.label ?? t.name })))
    .sort((a, b) => Number(a.state === "blocked") - Number(b.state === "blocked"));
  return {
    loaded: slices.has("list:teams"),
    now: { rows: busy.slice(0, NOW_SHOWN), more: Math.max(0, busy.length - NOW_SHOWN), working: busy.filter((m) => m.state === "working").length, blocked: busy.filter((m) => m.state === "blocked").length },
    groups, rows, newCount,
    empty: all.length === 0, filteredOut: all.length > 0 && rows.length === 0,
    overdue: rows.filter((x) => x.late).length,
    teams: teams.map((t) => ({ id: `${t.dir}/${t.name}`, name: t.label ?? t.name })),
  };
}
