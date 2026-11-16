// ah2 in Herdr: the Graph, a Desk tab (props {view:"graph"}): the web UI's Map (web/ui/graph-pack.js) for one team. A tab per running
// team; under it the team, then its lanes, each as packed circles (streams holding members, a busy ring per member, message arcs
// bundled through the circles, a mark per brief in flight), then its links as rows, which say in words what the arcs draw.
// Read-only: nothing here runs anything; the data is graph-model.js over the one poll. spec: spec-herdr-desktop.md § View: graph.
// Colours are theme() tokens only; circles are divs, arcs and rings are native paths (window.paint_path).
// Time: a scrubber under the maps, messages per minute over the run, with Run / 60 / 30 / 10 min windows; arcs outside the window
// fade. It is Herdr's native Slider, so a drag never re-enters this script: the view redraws on release and on an arrow key
// (one minute, STEP_MS), never per drag frame. Zoom: a click on a stream circle shows that stream alone; the crumb (or Esc) walks out.
// Scale: − / + / Fit beside the tabs, ctrl or cmd + wheel about the pointer; a plain wheel pans a map larger than its frame. Herdr
// gives mods no pinch and no drag events (gpui-kit typings: on_scroll_wheel, on_mouse_move, no pressed button), so neither is used.

import { div, PathBuilder, Background } from "gpui-kit";
import { v_flex, h_flex, Tabs, Tab, Button, Slider, SliderState, SliderTrack, SliderIndicator, SliderThumb } from "gpui-base";
import { isDocked } from "./dock.js";
import { graphModel, MAP_PX, KNOB, cursorAt, stepCursor, SCALE_STEP, clampPan, drillTo, fitView, rescaleTo, frameOf, LANE_PAD } from "./graph-model.js";
import { STEP_MS } from "./graph-world.js";
import { loadingOf } from "./model.js";
import { stateMark } from "./marks.js";

const MONO = "Geist Mono";
// Docked, the side column opens 420 px wide: the team's map takes 300 and a lane's 260 (its members' names whole), and every map comes before the links, so a
// lane's map shows without scrolling past the team's links.
const dockSize = (depth) => (depth === 0 ? 300 : 260);
const BAR = 48; // the weight bar's track: a link's messages against the team's busiest link
const HIST = 40; // the scrubber's bars, tallest
const CHIP = 8; // a stream's name chip rises this far above its circle (map(): top(c.y - c.r - 8))
const WHEEL_PX = 100; // ctrl/cmd + wheel travel per zoom step; ponytail: a guess, untuned on a real trackpad

// The scrubber's state on the view: window, cursor (ms, null at now), zoom, and the slider, which must be made outside render.
export function graphInit(v) {
  v.graphTeam = v.team; // the shown team: the tab row changes it
  v.graphWindow = "run";
  v.graphCursor = null;
  drillTo(v, null); // graphZoom, graphScale, graphPan
  v.graphKnob = KNOB; // the knob value this view last set; the slider holding another means a hand is on it
  v.graphSlider = SliderState.new({ min: 0, max: KNOB, step: 1, value: KNOB });
  v.graphSlider.on("release", (value, cx) => {
    if (!v.graphScrub) return;
    v.graphCursor = cursorAt(v.graphScrub, value);
    v.graphKnob = value;
    cx.notify();
  });
}

// The knob follows the cursor (it drifts left as now moves on) unless a hand is on it.
function syncKnob(v, sc) {
  const s = v.graphSlider;
  if (!s || !sc || s.value() !== v.graphKnob || sc.knob === v.graphKnob) return;
  s.set_value(sc.knob);
  v.graphKnob = sc.knob;
}

const stop = (_e, cx) => cx.stop_propagation(); // a press on a control must not drag the window

// graphModel only when an input changed: the slices (v.sliceGen, bumped by the poll), the shown team, window, cursor, zoom and
// scale, the size (docked or not) and the minute (the scrubber's clock moves in whole minutes). A throw is kept the same way. A pan
// is not an input: it only moves the laid-out map in its frame.
function graphOf(v, docked) {
  const key = [v.sliceGen, v.graphTeam, v.graphWindow, v.graphCursor, v.graphZoom, v.graphScale, docked, Math.floor(Date.now() / STEP_MS)];
  const memo = v.graphMemo;
  if (memo && key.every((k, i) => k === memo.key[i])) return memo;
  let m = null;
  let error = null;
  const s = v.graphScale;
  try {
    m = graphModel(v.st.slices, v.graphTeam, docked ? (d) => dockSize(d) * s : MAP_PX * s, v);
  } catch (e) {
    error = e?.message ?? String(e);
  }
  return (v.graphMemo = { key, m, error });
}

export function graphView(v, t, close) {
  const docked = isDocked();
  const { m: got, error } = graphOf(v, docked);
  const m = got ?? { loaded: true, tabs: [], scrub: null, crumb: null }; // a failed layout draws the frame as when loading
  v.graphScrub = m.scrub;
  syncKnob(v, m.scrub);
  const loading = loadingOf(v, m.loaded);
  const zoom = (z) => drillTo(v, z);
  let body;
  if (v.status === "down") body = [note(t, v.reason, "Retrying")];
  else if (error) body = [note(t, "Graph unavailable", error)];
  else if (loading) body = [note(t, "Loading graph…")];
  else if (m.empty) body = [note(t, "No teams running.")];
  else if (m.closed) body = [note(t, "Team closed.")];
  else {
    const parts = m.groups.map((g) => group(t, g, docked, (node) => zoom({ team: g.id, node }), v, m.groups));
    body = docked ? [...parts.flatMap((p) => p.map), ...parts.flatMap((p) => p.links)] : parts.flatMap((p) => [...p.map, ...p.links]);
  }
  const step = (by, cx) => {
    if (!m.scrub) return;
    v.graphCursor = stepCursor(m.scrub, by);
    cx.notify();
  };

  const tab = (tm, i) => {
    const on = tm.id === m.id;
    return Tab.new(`graph-tab-${i}`).selected(on)
      .h(26).px(10).flex().items_center().rounded(6).cursor_pointer().text_size(12)
      .border(1).border_color(on ? t.raised : t.surface).bg(on ? t.raised : t.surface).text_color(on ? t.foreground : t.muted)
      .hover((s) => s.text_color(t.foreground))
      .focus((s) => s.border_color(t.accent))
      .accessibility_label(`${tm.name} graph`)
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => {
        v.graphTeam = tm.id;
        drillTo(v, null);
        cx.notify();
      })
      // a Tab does not wire tooltip (herdr-app check warns); the label inside it does, and keeps a truncated name whole
      .child(div().id(`graph-tab-${i}-name`).max_w(160).truncate().tooltip(`Show the ${tm.name} graph`).child(tm.name));
  };

  // The room a zoomed map may fill (frameOf): the body's laid-out size (on_size_change), less its side and bottom padding, the
  // map's top margin and its chip.
  const measure = (size, cx) => {
    const room = { w: Math.round(size.width - 32), h: Math.round(size.height - 12 - 12 - CHIP) };
    if (v.graphRoom?.w === room.w && v.graphRoom?.h === room.h) return;
    v.graphRoom = room;
    if (v.graphScale > 1) cx.notify();
  };
  return v_flex().id("root").size_full().min_h(0).bg(t.surface).text_size(12.5)
    .track_focus(v.keys)
    .on_key_down((e, cx) => {
      if (e.keystroke === "escape" && m.crumb) {
        zoom(null);
        cx.notify();
      } else if (e.keystroke === "escape") close();
      else if (e.keystroke === "left") step(-1, cx);
      else if (e.keystroke === "right") step(1, cx);
      else if (e.keystroke === "end") step(Infinity, cx);
    })
    // The Desk's tab row is the header (tabs.js panelTabsView). The team tab row always draws (empty while loading), so nothing
    // above the list moves when the teams arrive; the scale controls sit at its end.
    .child(h_flex().h(34).px(16).pt(8).gap(8).flex_none().items_center()
      .child(Tabs.new("graph-tabs").flex_1().min_w(0).gap(2).flex().items_center()
        .children(loading || v.status === "down" ? [] : m.tabs.map(tab)))
      .children(scaleBar(v, t, m.groups)))
    .when(Boolean(m.crumb), (el) => el.child(crumb(t, m.crumb, (_e, cx) => {
      zoom(null);
      cx.notify();
    })))
    .child(v_flex().id("graph-body").flex_1().min_h(0).px(16).pb(12).overflow_y_scrollbar().on_size_change(measure).children(body))
    .child(scrubber(v, t, loading || v.status === "down" ? null : m.scrub));
}

// The way back out of a zoom: the team (and lane), each a button to the whole team, then the stream shown.
function crumb(t, names, out) {
  const last = names.length - 1;
  return h_flex().h(28).px(16).gap(4).flex_none().items_center().text_size(12).text_color(t.muted)
    .accessibility_label(`Zoomed into ${names.join(", ")}`)
    .children(names.flatMap((n, i) => [
      ...(i ? [div().text_color(t.dim).child("›")] : []),
      i === last ? div().min_w(0).truncate().text_color(t.foreground).font_medium().child(n)
        : Button.new(`graph-crumb-${i}`).h(24).px(6).flex().flex_none().items_center().rounded(6).cursor_pointer()
          .border(1).border_color(t.surface).focus((s) => s.border_color(t.accent)).hover((s) => s.bg(t.hover).text_color(t.foreground))
          .tooltip(`Back to all of ${names[0]} (esc)`).accessibility_label(`Back to all of ${names[0]}`)
          .on_mouse_down("left", stop).on_click(out).child(div().max_w(140).truncate().child(n)),
    ]));
}

// The scale controls: −, the scale, +, Fit. A step is ×SCALE_STEP about each map's centre; Fit is scale 1, centred.
function scaleBar(v, t, groups) {
  const s = v.graphScale;
  const btn = (id, text, tip, said, act) => Button.new(id).h(24).min_w(24).px(6).flex().flex_none().items_center().justify_center().rounded(6)
    .cursor_pointer().text_size(11.5).text_color(t.foreground).border(1).border_color(t.input)
    .focus((el) => el.border_color(t.accent)).hover((el) => el.bg(t.hover))
    .tooltip(tip).accessibility_label(said)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => {
      act();
      cx.notify();
    })
    .child(text);
  return [
    btn("graph-scale-out", "−", "Zoom out (ctrl or cmd + scroll)", "Zoom out", () => rescaleTo(v, groups, s / SCALE_STEP)),
    div().w(40).flex_none().text_center().font_family(MONO).text_size(11).text_color(t.muted).accessibility_label(`Scale ${Math.round(s * 100)} percent`)
      .child(`${Math.round(s * 100)}%`),
    btn("graph-scale-in", "+", "Zoom in (ctrl or cmd + scroll)", "Zoom in", () => rescaleTo(v, groups, s * SCALE_STEP)),
    btn("graph-scale-fit", "Fit", "Fit the graph to the view", "Fit the graph to the view", () => fitView(v)),
  ];
}

// The scrubber: the window choice, the readout and Now; then the slider, its track drawn as messages per bar (in the window: accent
// on a band), the knob a line through it; then the span's ends. The frame keeps its height before data, so nothing moves.
function scrubber(v, t, sc) {
  const s = v.graphSlider;
  const win = (w, i) => {
    const on = sc?.win === w.id;
    return Tab.new(`graph-win-${i}`).selected(on)
      .h(24).px(8).flex().flex_none().items_center().whitespace_nowrap().rounded(6).cursor_pointer().text_size(11.5)
      .border(1).border_color(on ? t.raised : t.surface).bg(on ? t.raised : t.surface).text_color(on ? t.foreground : t.muted)
      .hover((el) => el.text_color(t.foreground)).focus((el) => el.border_color(t.accent))
      .accessibility_label(`${w.label} window`)
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => {
        v.graphWindow = w.id;
        cx.notify();
      })
      .child(w.label);
  };
  const per = sc && sc.per > 1 ? `messages per ${sc.per} min` : "messages per minute";
  const head = h_flex().h(26).gap(8).items_center()
    .child(Tabs.new("graph-wins").gap(2).flex().flex_none().items_center().children(sc ? sc.wins.map(win) : []))
    .child(div().flex_1().min_w(0).truncate().font_family(MONO).text_size(11.5).text_color(t.foreground)
      .tooltip(sc ? [sc.range, sc.say].filter(Boolean).join(" · ") : "").child(sc ? sc.say : ""))
    .when(Boolean(sc && !sc.live), (el) => el.child(Button.new("graph-now").h(24).px(8).flex().flex_none().items_center().rounded(6).cursor_pointer()
      .text_size(11.5).text_color(t.foreground).border(1).border_color(t.input).focus((s2) => s2.border_color(t.accent)).hover((s2) => s2.bg(t.hover))
      .tooltip("Back to now (end)").accessibility_label("Back to now")
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => {
        v.graphCursor = null;
        cx.notify();
      })
      .child("Now")));
  const bars = sc ? sc.bars.map((b, i) => v_flex().id(`graph-bar-${i}`).flex_1().h_full().justify_end().when(b.in && sc.win !== "run", (el) => el.bg(t.selected))
    .child(div().h(b.n ? Math.max(2, Math.round((HIST * b.n) / sc.peak)) : 0).rounded(1).bg(b.in ? t.accent : t.muted))) : [];
  const track = Slider.new(s).accessibility_label(sc ? `Time, ${per}: ${sc.at}. Left and right arrows step a minute.` : "Time: no message history yet")
    .child(SliderTrack.new(s).relative().h(HIST + 4).w_full().cursor_pointer()
      .child(h_flex().absolute().top(0).left(0).size_full().gap(1).items_end().border_b(1).border_color(t.border).children(bars))
      .child(SliderIndicator.new(s).relative().w_full().h(HIST + 4)
        .child(SliderThumb.new(s).id("graph-knob").w(12).h(HIST + 4).ml(-6).flex().flex_col().items_center()
          .child(div().w(2).flex_1().bg(t.foreground))
          .child(div().size(10).flex_none().rounded(5).border(2).border_color(t.foreground).bg(t.surface)))));
  return v_flex().id("graph-scrub").flex_none().h(104).px(16).pt(8).pb(8).gap(6).border_t(1).border_color(t.border)
    .on_mouse_down("left", stop)
    .child(head)
    .when(Boolean(sc), (el) => el.child(track)
      .child(h_flex().h(14).justify_between().font_family(MONO).text_size(10.5).text_color(t.muted)
        .child(div().child(sc.from)).child(div().child(per)).child(div().child(sc.to))))
    .when(!sc, (el) => el.child(div().text_size(11).text_color(t.muted).child("No message history yet.")));
}

function note(t, text, detail) {
  return v_flex().py(12).gap(2)
    .child(div().text_size(12).text_color(t.muted).child(text))
    .when(Boolean(detail), (el) => el.child(div().text_size(11).text_color(t.muted).child(detail)));
}

function label(t, text, n, pad) {
  return h_flex().mt(12).h(20).pl(pad).gap(6).items_center().text_size(11).font_semibold().text_color(t.muted)
    .child(text).child(div().font_family(MONO).font_medium().child(String(n)));
}

const quiet = (t, text, pad) => div().pl(pad).py(4).text_size(11).text_color(t.muted).child(text);

// One team: a lane gets its own header (the tab names the top team), then Members (the map) and Links, indented by depth.
// { map, links }: docked, every team's map comes first, then the links, each section headed by its lane.
function group(t, g, docked, zoomTo, v, groups) {
  const pad = g.depth * LANE_PAD;
  const head = (mt) => (g.depth > 0 ? [h_flex().mt(mt).h(20).pl(pad - 14).gap(4).items_center().text_size(11).font_semibold().text_color(t.muted)
    .child(div().text_color(t.dim).child("↳")).child(div().min_w(0).truncate().child(g.name))] : []);
  const out = head(16);
  if (g.pending) return { map: [...out, quiet(t, "Loading…", pad)], links: [] };
  if (g.members.length === 0) out.push(label(t, "Members", 0, pad), quiet(t, "None", pad));
  else out.push(h_flex().mt(12).pl(pad).flex_none().justify_center().child(map(t, g, zoomTo, v, groups)));
  const links = docked && g.depth > 0 ? head(16) : [];
  links.push(label(t, g.windowed ? "Links in the window" : g.run ? "Links" : "Links, last hour", g.links.length, pad));
  if (g.links.length === 0) links.push(quiet(t, g.windowed ? "No messages in the window." : g.run ? "No messages yet this run." : "No open run.", pad));
  g.links.forEach((l, i) => links.push(link(t, g, l, i, pad, docked)));
  if (g.overflow > 0) links.push(quiet(t, `+ ${g.overflow} more between other members`, pad));
  return { map: out, links };
}

// ---- the Map: graph-model.js mapOf, drawn as the web draws it (r6-graph.css .gpack) in Herdr tokens ----

// A light theme draws the arcs stronger, as the web does (--arc-alpha .75 light; .6 dark where the web has .55, for 3:1 on raised).
const light = (hex) => {
  const v = /^#?([0-9a-f]{6})/i.exec(hex ?? "")?.[1];
  if (!v) return false;
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(v.slice(i, i + 2), 16));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b > 128;
};

// Commands from graph-layout.js bundle() into a native stroke; a report runs dashed (5 4), as on the web.
function stroke(cmds, width, dash) {
  let b = PathBuilder.stroke(width);
  if (dash) b = b.dash_array([5, 4]);
  for (const c of cmds) {
    if (c[0] === "M") b = b.move_to(c[1], c[2]);
    else if (c[0] === "L") b = b.line_to(c[1], c[2]);
    else b = b.cubic_bezier_to(c[5], c[6], c[1], c[2], c[3], c[4]);
  }
  return b.build();
}

// A ring of radius r around (c, c), clockwise from 12 o'clock through `share` of the turn; a full turn in two halves.
function ring(c, r, share, width) {
  const at = (f) => [c + r * Math.sin(f * 2 * Math.PI), c - r * Math.cos(f * 2 * Math.PI)];
  let b = PathBuilder.stroke(width).move_to(...at(0));
  if (share >= 0.999) b = b.arc_to(r, r, 0, false, true, ...at(0.5)).arc_to(r, r, 0, false, true, ...at(0.999));
  else b = b.arc_to(r, r, 0, share > 0.5, true, ...at(share));
  return b.build();
}

const layer = (path, color, size) => window.paint_path(path, color).absolute().top(0).left(0).size(size);

function map(t, g, zoomTo, v, groups) {
  const m = g.map;
  const S = m.size;
  const s = v.graphScale;
  const F = S / s; // the frame: the map's square at scale 1
  const base = light(t.surface) ? 0.75 : 0.6;
  const els = [];
  // streams: raised, ringed in muted (the web's idle mark is under 3:1 here); the team's circle is not drawn. A stream that is not
  // the one shown is a button: it zooms in.
  m.containers.forEach((c, i) => {
    const disc = (el) => el.absolute().left(c.x - c.r).top(c.y - c.r).size(c.r * 2).rounded(c.r).bg(t.raised).border(1).border_color(t.muted);
    if (!c.zoom) return els.push(disc(div().id(`gc-${g.id}-${i}`)).tooltip(c.title).accessibility_label(c.title));
    els.push(disc(Button.new(`gc-${g.id}-${i}`)).cursor_pointer().hover((s) => s.border_color(t.foreground)).focus((s) => s.border_color(t.accent))
      .tooltip(`${c.title}. Click to zoom in`).accessibility_label(`Zoom into ${c.title}`)
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => {
        zoomTo(c.id);
        cx.notify();
      }));
  });
  for (const a of m.arcs) els.push(layer(stroke(a.cmds, a.width, a.kind === 1), Background.solid(t.accent).opacity(a.fade ?? base), S));
  m.leaves.forEach((n, i) => els.push(leaf(t, g, n, i)));
  m.flights.forEach((f, i) => els.push(div().id(`gf-${g.id}-${i}`).absolute().left(f.x - 4).top(f.y - 4).size(8).rounded(4)
    .bg(t.accent).border(1).border_color(t.surface).tooltip(f.title).accessibility_label(f.title)));
  // container names sit on the circle's top edge, on a surface chip that stands in for the web's label halo
  m.containers.forEach((c, i) => c.label && els.push(h_flex().absolute().left(c.x - c.r).top(c.y - c.r - 8).w(c.r * 2).h(16).justify_center()
    .child(div().id(`gcl-${g.id}-${i}`).max_w(c.r * 1.6).h(16).px(4).flex().items_center().rounded(4).bg(t.surface)
      .text_size(11).font_semibold().text_color(t.muted).truncate().child(c.name))));
  const said = `${g.name} map: ${m.leaves.length} members${m.containers.length ? ` in ${m.containers.length} circles` : ""}, ${m.arcs.length} message arcs. The links say who messages whom.`;
  const box = frameOf(F, s, v.graphRoom, g.depth * LANE_PAD);
  const pan = clampPan(v.graphPan[g.id], F, s, box);
  // ctrl or cmd + wheel scales about the pointer in whole SCALE_STEPs (WHEEL_PX of wheel each), so a gesture relays the map out once
  // per step, not once per wheel event; a plain wheel pans a map larger than its frame, else scrolls the body as before. Both read
  // the view's state, not this render's; after a step they wait for its redraw (groups and F are laid out at the old scale).
  // A positive delta.y is a scroll up (gpui-kit shell/src/tests/render.rs:10714, -200 scrolls a list down), so up zooms in.
  const wheel = (e, cx) => {
    const stale = v.graphScale !== s;
    if (e.modifiers.control || e.modifiers.platform) {
      cx.stop_propagation();
      v.graphWheel = (v.graphWheel ?? 0) + e.delta.y;
      const n = Math.trunc(v.graphWheel / WHEEL_PX);
      if (!n || stale) return;
      v.graphWheel -= n * WHEEL_PX;
      const at = e.local_position ? { id: g.id, x: e.local_position.x, y: e.local_position.y - CHIP } : null;
      rescaleTo(v, groups, s * SCALE_STEP ** n, at) && cx.notify();
    } else if (s > 1 && !stale) {
      const p = clampPan(v.graphPan[g.id], F, s, box);
      v.graphPan[g.id] = clampPan({ x: p.x + e.delta.x, y: p.y + e.delta.y }, F, s, box);
      cx.stop_propagation();
      cx.notify();
    }
  };
  // the frame is CHIP taller, the map drawn CHIP down, so a stream's name chip on a circle at the top edge is not clipped
  return div().id(`gmap-${g.id}`).relative().w(box.w).h(box.h + CHIP).flex_none().overflow_hidden().accessibility_label(said).on_scroll_wheel(wheel)
    .child(div().id(`gmap-${g.id}-in`).absolute().left(pan.x).top(pan.y + CHIP).size(S).children(els));
}

// A member: its body, the busy track and ring (the share of the last hour it worked, in its state's tone), then the label tiers
// its radius has room for: context, state mark and name, role, model · state, item. The mark's shape keeps it from resting on colour.
function leaf(t, g, n, i) {
  const r = n.r;
  const tone = t[n.tone] ?? t.unknown;
  const full = n.full;
  const w = Math.round(r * 1.62); // the ring's inner edge across
  const line = (text, size, color, mono = false) => div().max_w(w).truncate().text_size(size).text_color(color).when(mono, (el) => el.font_family(MONO).font_semibold()).child(text);
  // A name too long to sit beside its state mark gets the circle's whole width, and the mark moves up beside the context (or
  // onto its own line), so a docked map still reads "orchestrator" whole. 6.2 px a character is Geist at 12 px, rounded up.
  const across = Math.round(r * 2 - 8);
  const tight = n.name.length * 6.2 + 18 > across;
  const mark = stateMark(t, n.state);
  return div().id(`gm-${g.id}-${i}`).absolute().left(n.x - r).top(n.y - r).size(r * 2).rounded(r)
    .bg(t.surface).border(1).border_color(t.border).when(n.state === "gone", (el) => el.opacity(0.4))
    .tooltip(full).accessibility_label(full)
    .child(layer(ring(r, r * 0.86, 1, r * 0.1), t.border, r * 2))
    .child(layer(ring(r, r * 0.86, Math.max(0.04, n.share ?? 0), r * 0.1), tone, r * 2))
    .child(v_flex().size_full().items_center().justify_center().gap(1)
      .when(n.tier >= 2 && tight, (el) => el.child(h_flex().gap(4).items_center().child(mark)
        .when(Boolean(n.ctx), (r2) => r2.child(line(n.ctx, 11, t.foreground, true)))))
      .when(n.tier >= 1 && Boolean(n.ctx) && !(n.tier >= 2 && tight), (el) => el.child(line(n.ctx, 11, t.foreground, true)))
      .when(n.tier >= 2, (el) => el.child(h_flex().max_w(tight ? Math.round(r * 2) : across).gap(4).items_center() // the name may cross the ring, as the web's does
        .when(!tight, (r2) => r2.child(mark))
        .child(div().min_w(0).truncate().text_size(12).text_color(t.foreground).child(n.name))))
      .when(n.tier >= 3 && Boolean(n.role), (el) => el.child(line(n.role, 11, t.muted)))
      .when(n.tier >= 4, (el) => el.child(line([n.model, n.word.toLowerCase()].filter(Boolean).join(" · "), 11, t.muted)))
      .when(n.tier >= 5, (el) => el.child(line(n.item || "no item", 11, t.muted))));
}

// A link: the busier sender first, → one way or ⇄ both, what is in flight, then the weight bar and the count (mono, right).
function link(t, g, l, i, pad, docked) {
  const bar = docked ? 32 : BAR;
  const fill = l.n > 0 ? Math.max(2, Math.round((bar * l.n) / g.max)) : 0;
  const nameW = docked ? 80 : 120; // docked, 420 px: narrower names and bar leave "1 in flight, review" room to read whole
  const said = `${l.a} ${l.arrow === "⇄" ? "and" : "to"} ${l.b}: ${l.n} ${l.n === 1 ? "message" : "messages"} ${g.windowed ? "in the window" : "this run"}${l.inFlight ? `, ${l.inFlight}` : ""}`;
  return h_flex().id(`gl-${g.id}-${i}`).h(28).pl(pad).gap(8).items_center().border_b(1).border_color(t.border)
    .tooltip(l.title).accessibility_label(said)
    .child(div().w(nameW).flex_none().truncate().text_color(t.foreground).child(l.a))
    .child(div().w(14).flex_none().text_center().text_color(t.muted).child(l.arrow))
    .child(div().w(nameW).flex_none().truncate().text_color(t.foreground).child(l.b))
    .child(div().flex_1().min_w(0).truncate().text_size(11).text_color(t.muted).child(l.inFlight))
    .child(div().w(bar).h(4).flex_none().rounded(2).bg(t.hover)
      .when(fill > 0, (el) => el.child(div().w(fill).h(4).rounded(2).bg(t.muted))))
    .child(div().w(28).flex_none().text_right().font_family(MONO).text_size(11).text_color(t.muted).child(String(l.n)));
}
