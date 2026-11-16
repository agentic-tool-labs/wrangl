// ah2 in Herdr: the live feed (props {view:"feed"}, P1 atp-1496), a tab of the Desk (side column or Desk window, dock.js). A Now strip (who is
// working on what), filter chips, then one stream grouped by minute (feed-model.js) with "N new since you looked". A row whose
// member has a live terminal is a button: hover shows Focus terminal, click or Enter focuses it (pane.focus, as the sidebar).
// The chips and the last look persist in Herdr's shared store (FEED_KEY). Colours are theme() tokens. Nothing here sends.

import { div } from "gpui-kit";
import { v_flex, h_flex, Button } from "gpui-base";
import { run, terminals, shared, setShared } from "herdr";
import { isDocked, shownIn } from "./dock.js";
import { feedModel, seenStates, KINDS } from "./feed-model.js";
import { loadingOf, terminalFinder, counts } from "./model.js";
import { target } from "./ah.js";
import { markCell } from "./marks.js";

const MONO = "Geist Mono";
const FEED_KEY = "ah2-feed";

const prefs = () => ({ looked: 0, off: [], internal: false, team: null, ...(shared(FEED_KEY) ?? {}) });
const setPrefs = (patch, cx) => {
  setShared(FEED_KEY, { ...prefs(), ...patch });
  cx?.notify();
};
const stop = (_e, cx) => cx.stop_propagation();

export function feedView(v, t, close) {
  const now = Date.now();
  const p = prefs();
  // "Since you looked": each time the feed comes into view (the column shows it, or the Desk window switches to it: main.js
  // clears feedShowing on leaving), the last look is read for the divider and the new look stored.
  const showing = isDocked() ? shownIn("feed") : v.view === "feed";
  if (showing && !v.feedShowing) {
    v.feedLooked = p.looked;
    setPrefs({ looked: now });
  }
  v.feedShowing = showing;

  const loading = loadingOf(v, v.st.slices.has("list:teams"));
  if (!loading) v.feedSeen = seenStates(v.feedSeen, v.st.slices, now);
  const m = feedModel(v.st.slices, now, { ...p, looked: v.feedLooked ?? 0, seen: v.feedSeen?.events ?? [] });
  const terminalOf = terminalFinder(terminals(), target());

  let body;
  if (v.status === "down") body = [note(t, v.reason, "Retrying")];
  else if (loading) body = [note(t, "Loading feed…")];
  else if (m.empty) body = [note(t, "Nothing has happened yet.", "Briefs, reports, plan moves, alerts and questions show here as they happen.")];
  else if (m.filteredOut) body = [note(t, "Nothing matches these filters.", "Turn a filter back on above.")];
  else body = m.groups.map((g) => (g.divider ? divider(t, g.divider) : minute(v, t, g, terminalOf)));

  return v_flex().id("root").size_full().min_h(0).bg(t.surface).text_size(12.5)
    .track_focus(v.keys)
    .on_key_down((e, _cx) => {
      if (e.keystroke === "escape") close();
    })
    // the Desk's tab row is the header, in the column and in the Desk window (tabs.js panelTabsView)
    .when(!loading && v.status !== "down" && m.now.rows.length > 0, (el) => el.child(nowStrip(v, t, m.now, terminalOf)))
    .when(!loading && v.status !== "down" && !m.empty, (el) => el.child(chips(v, t, m, p)))
    .child(v_flex().id("feed-body").flex_1().min_h(0).px(12).pb(12).pt(4).overflow_y_scrollbar().children(body));
}

function note(t, text, detail) {
  return v_flex().py(12).px(4).gap(2)
    .child(div().text_size(12).text_color(t.muted).child(text))
    .when(Boolean(detail), (el) => el.child(div().text_size(11).text_color(t.muted).child(detail)));
}

// A row that focuses its member's terminal when one is live, else a plain row; every row has the ring border so columns line up.
function focusable(v, t, id, address, label) {
  const el = v.ring(t, address ? Button.new(id) : h_flex().id(id), t.surface);
  return address
    ? el.cursor_pointer().hover((s) => s.bg(t.hover)).accessibility_label(`${label}. Focus its terminal`)
      .on_click(() => run("pane.focus", { terminal: address }))
    : el.accessibility_label(label);
}

// Who is working on what, across the shown teams: working first, then blocked.
function nowStrip(v, t, n, terminalOf) {
  return v_flex().id("feed-now").flex_none().px(12).pt(8).pb(8).border_b(1).border_color(t.border)
    .child(h_flex().h(20).px(6).items_center().text_size(11).text_color(t.muted)
      .child(div().flex_1().font_medium().child("Now"))
      .child(div().child(counts({ working: n.working, blocked: n.blocked }))))
    .children(n.rows.map((mb) => {
      const full = `${mb.name} · ${mb.team} · ${mb.word} · ${mb.what}${mb.age ? ` · ${mb.age}` : ""}`;
      return focusable(v, t, `feed-now-${mb.key}`, terminalOf(mb), full)
        .h(24).px(6).gap(8).flex().items_center().rounded(5).text_size(12).tooltip(full)
        .child(markCell(t, mb.state))
        .child(div().w(92).flex_none().truncate().font_medium().text_color(t.foreground).child(mb.name))
        .child(div().flex_1().min_w(0).truncate().text_color(t.muted).child(mb.what))
        .child(div().w(28).flex_none().text_right().font_family(MONO).text_size(11).text_color(t.muted).child(mb.age))
        // the team in the stream rows' team column
        .child(div().w(96).flex_none().text_right().truncate().text_size(11).text_color(t.muted).child(mb.team));
    }))
    .when(n.more > 0, (el) => el.child(div().h(20).px(6).text_size(11).text_color(t.muted).child(`+ ${n.more} more`)));
}

// The filter row: the team (a click steps to the next running team, then back to all), one chip per kind, Internal (off by default).
function chips(v, t, m, p) {
  const chip = (id, text, on, label, click) => v.ring(t, Button.new(`feed-chip-${id}`), t.border)
    .h(24).px(8).gap(4).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(11.5)
    .bg(on ? t.selected : t.surface).text_color(on ? t.foreground : t.muted)
    .hover((s) => s.border_color(t.muted))
    .tooltip(label).accessibility_label(label)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => click(cx))
    .when(on, (el) => el.child(div().text_size(10).text_color(t.accent).child("✓")))
    .child(text);
  const ids = m.teams.map((x) => x.id);
  const team = ids.includes(p.team) ? p.team : null;
  const teamName = team ? m.teams.find((x) => x.id === team).name : "All teams";
  const nextTeam = team === null ? ids[0] ?? null : ids[ids.indexOf(team) + 1] ?? null;
  const nextName = nextTeam ? m.teams.find((x) => x.id === nextTeam).name : "all teams";
  // wraps in the 420 px column rather than clipping a chip
  return h_flex().id("feed-chips").min_h(40).flex_none().flex_wrap().px(12).py(8).gap(6).items_center().border_b(1).border_color(t.border)
    .child(chip("team", teamName, team !== null, `Team: ${teamName}. Show ${nextName}`, (cx) => setPrefs({ team: nextTeam }, cx)))
    .children(KINDS.map(([k, text]) => {
      const on = !p.off.includes(k);
      return chip(k, text, on, `${text}, ${on ? "shown" : "hidden"}. ${on ? "Hide" : "Show"} them`,
        (cx) => setPrefs({ off: on ? [...p.off, k] : p.off.filter((x) => x !== k) }, cx));
    }))
    .child(chip("internal", "Internal", p.internal, `Internal messages, ${p.internal ? "shown" : "hidden"}. ${p.internal ? "Hide" : "Show"} them`,
      (cx) => setPrefs({ internal: !p.internal }, cx)));
}

function divider(t, n) {
  const line = () => div().h(1).bg(t.accent);
  return h_flex().id("feed-new").h(24).gap(8).items_center().text_size(11).text_color(t.foreground)
    .accessibility_label(`${n} new since you looked`)
    .child(line().w(24).flex_none())
    .child(div().flex_none().child(`${n} new since you looked`))
    .child(line().flex_1());
}

// One minute: its clock beside its rows.
function minute(v, t, g, terminalOf) {
  return h_flex().id(`feed-m-${g.key}`).pt(4).items_start()
    .child(div().w(44).h(24).flex_none().flex().items_center().font_family(MONO).text_size(11).text_color(t.muted).child(g.clock))
    .child(v_flex().flex_1().min_w(0).children(g.rows.map((r) => row(v, t, r, terminalOf))));
}

// One event: glyph · title, then its detail muted · the team, which hover swaps for Focus terminal when the member has one.
function row(v, t, r, terminalOf) {
  const address = r.mb ? terminalOf(r.mb) : null;
  const hov = Boolean(address) && v.feedHover === r.key;
  const full = [r.clock, r.title, r.sub, r.team, r.age && `${r.age} ago`].filter(Boolean).join(" · ");
  const glyph = r.mark ? markCell(t, r.mark)
    : div().w(14).flex_none().text_center().text_size(11).text_color(t[r.tone] ?? t.muted).child(r.glyph);
  return focusable(v, t, `feed-${r.key}`, address, full)
    .h(24).px(6).gap(8).flex().items_center().rounded(5).text_size(12).tooltip(full)
    .when(Boolean(address), (el) => el.on_hover((on, cx) => {
      if (on) v.feedHover = r.key;
      else if (v.feedHover === r.key) v.feedHover = null;
      cx.notify();
    }))
    .child(glyph)
    .child(div().min_w(0).truncate().text_color(t.foreground).child(r.title))
    .when(Boolean(r.sub), (el) => el.child(div().flex_1().min_w(0).truncate().text_color(t.muted).child(r.sub)))
    .when(!r.sub, (el) => el.child(div().flex_1()))
    // the same width either way, so hover moves nothing
    .child(h_flex().w(96).flex_none().justify_end().items_center()
      .child(hov
        ? div().h(20).px(6).flex().items_center().rounded(5).border(1).border_color(t.border).bg(t.surface).text_size(11).text_color(t.foreground).child("Focus terminal")
        : h_flex().min_w(0).gap(4).text_size(11).text_color(t.muted)
          .when(r.lane, (el) => el.child(div().flex_none().text_color(t.dim).child("↳")))
          .child(div().min_w(0).truncate().child(r.team))));
}
