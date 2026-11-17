// ah2 in Herdr: the Graph, a Desk tab (props {view:"graph"}): the web UI's Map (web/ui/graph-pack.js) for one team. A tab per running
// team; under it the team, then its lanes, each as packed circles (streams holding members, a busy ring per member, message arcs
// bundled through the circles, a mark per brief in flight), then its links as rows, which say in words what the arcs draw.
// Read-only: nothing here runs anything; the data is graph-model.js over the one poll. spec: spec-herdr-desktop.md § View: graph.
// Colours are theme() tokens only; circles are divs, arcs and rings are native paths (window.paint_path).
// Time: a scrubber under the maps, messages per minute over the run, with Run / 60 / 30 / 10 min windows; arcs outside the window
// fade. It is Herdr's native Slider, so a drag never re-enters this script: the view redraws on release and on an arrow key
// (one minute, STEP_MS), never per drag frame. Zoom: a click on a stream circle shows that stream alone; the crumb (or Esc) walks out.
// Scale: − / + / Fit beside the tabs, and scroll to zoom: a mouse wheel or a trackpad's two-finger scroll, about the pointer, eased
// out only, in step with the hand (graph-model.js wheelAct, WHEEL_EASE_MS). Drag to pan: a left press on a map or the Flow's stage, moved past DRAG_PX, pans
// content larger than its frame (grip, dragAct); a press and release short of it is still a click.
// Map | Flow: the header's mode tabs switch to the web's Flow (graph-model.js flowModel over the shared flow-model.js): cards in four
// role columns, links between them, lanes as bands. Read-only as the Map is. The choice is kept in Herdr's shared store (GRAPH_KEY).

import { div, PathBuilder, Background } from "gpui-kit";
import { shared, setShared, run, terminals } from "herdr";
import { v_flex, h_flex, Tabs, Tab, Button, Slider, SliderState, SliderTrack, SliderIndicator, SliderThumb } from "gpui-base";
import { isDocked } from "./dock.js";
import { graphModel, MAP_PX, KNOB, cursorAt, stepCursor, SCALE_STEP, clampPan, drillTo, rescaleTo, frameOf, LANE_PAD,
  flowModel, graphChoice, flowPan, flowRescale, LEGEND_H, clampScale, wheelAct, easedScale, dragAct, wasDrag } from "./graph-model.js";
import { GRAPH_KEY } from "./settings-model.js";
import { DUTY, DUTY_TITLE, stateWord } from "./select.js";
import { STEP_MS } from "./graph-world.js";
import { STATES, age, loadingOf, terminalFinder, memberName } from "./model.js";
import { target } from "./ah.js";
import { stateMark } from "./marks.js";

const MONO = "Geist Mono";
// Docked, the side column opens 420 px wide: the team's map takes 300 and a lane's 260 (its members' names whole), and every map comes before the links, so a
// lane's map shows without scrolling past the team's links.
const dockSize = (depth) => (depth === 0 ? 300 : 260);
const BAR = 48; // the weight bar's track: a link's messages against the team's busiest link
const HIST = 40; // the scrubber's bars, tallest
const CHIP = 8; // a stream's name chip rises this far above its circle (map(): top(c.y - c.r - 8))
const FRAME_MS = 16; // an eased zoom's frame

// An eased zoom (spec herdr-graph-wheel-zoom D8): `by` multiplies the target (the one in motion, else the drawn scale), clamped, so
// travel at a limit is dropped, not banked; `to` sets it (Fit). The drawn scale, graphScale, which the layout reads, moves to it (a button
// or Fit along the web's curve, a wheel, wheel true, eased out only over WHEEL_EASE_MS: spec herdr-graph-wheel-follow), relaid out
// once per frame about `at` (Map {id, x, y}, Flow {x, y}, null: each centre). Travel mid-move retargets from the drawn scale, with
// its own rule. A frame waits for the redraw at the drawn scale (graphMemo's key[5]); the loop ends on the target, when fitView
// clears graphEase, or a second after a frame nobody drew (a closed pane), and the view's close() cancels it.
export function easeTo(v, cx, { by = 1, to = null, at = null, wheel = false }) {
  const target = clampScale(to ?? (v.graphEase?.to ?? v.graphScale) * by);
  if (target === (v.graphEase?.to ?? v.graphScale)) {
    if (to !== null && !v.graphEase) { // Fit at scale 1 still recentres (fitView's pan reset)
      v.graphPan = {};
      cx.notify();
    }
    return;
  }
  v.graphEase = { from: v.graphScale, to: target, t0: Date.now(), at, fit: to !== null, wheel };
  if (v.graphAnim && !v.graphAnim.is_done()) return; // the running loop takes the new target
  v.graphAnim = cx.spawn(async (cx) => {
    let drawn = Date.now();
    while (v.graphEase) {
      await cx.sleep(FRAME_MS);
      const e = v.graphEase;
      const memo = v.graphMemo;
      if (!e) break;
      if (memo?.key[5] !== v.graphScale || !memo.m) {
        if (Date.now() - drawn > 1000) v.graphEase = null;
        continue;
      }
      drawn = Date.now();
      const s2 = easedScale(e.from, e.to, e.t0, drawn, e.wheel);
      if (s2 === e.to) v.graphEase = null;
      if (v.graphMode === "flow") flowRescale(v, memo.m, s2, e.at?.x ?? null, e.at?.y ?? null);
      else rescaleTo(v, memo.m.groups, s2, e.at);
      if (s2 === e.to && e.fit) v.graphPan = {}; // Fit lands centred, as fitView does
      cx.notify();
    }
  });
}

// The scrubber's state on the view: window, cursor (ms, null at now), zoom, and the slider, which must be made outside render.
export function graphInit(v) {
  v.graphTeam = v.team; // the shown team: the tab row changes it
  v.graphWindow = "run";
  v.graphCursor = null;
  drillTo(v, null); // graphZoom, graphScale, graphPan
  const c = graphChoice(shared(GRAPH_KEY));
  v.graphMode = c.mode;
  v.graphOrient = c.orient; // remembered, or null: the stage's shape picks it once per pane (graphPicked)
  v.graphPicked = null;
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

// Drag to pan (spec herdr-graph-gestures), one drag at a time in v.graphDrag {key, x0, y0, x, y, dragged, live, end}, `key` the
// stage (a map's group id, or "flow"). A left press on the stage, or on a member or stream circle on it, starts it and stops the press
// (the window must not drag); the stage's move pans once it passes DRAG_PX (dragAct). It ends on the release, when the pointer leaves
// the stage (on_hover: a release outside is never delivered here, and on_mouse_move carries no button), when it enters it again,
// on a press outside it (on_mouse_down_out), and when the view resets (fitView); an ended drag never pans again. A button's click
// after a drag is dropped (clicks, wasDrag).
function grip(v, key) {
  const down = (e, cx) => {
    cx.stop_propagation();
    const { x, y } = e.position;
    v.graphDrag = { key, x0: x, y0: y, x, y, dragged: false, live: true, end: 0 };
  };
  const end = (_e, cx) => {
    const d = v.graphDrag;
    if (!d?.live || d.key !== key) return;
    d.live = false;
    d.end = Date.now();
    if (d.dragged) cx.notify(); // the grabbing hand goes back
  };
  const held = () => Boolean(v.graphDrag?.live && v.graphDrag.dragged && v.graphDrag.key === key);
  // the stage's move: `pan` gives the drawn pan now (null while a rescale awaits its redraw), `set` stores one. While a zoom eases
  // the drag waits (D8): the pointer's travel then is dropped, and the drag goes on from where the pointer is when the zoom lands.
  const move = (pan, clamp, set) => (e, cx) => {
    const d = v.graphDrag;
    if (!d?.live || d.key !== key) return;
    const p = v.graphEase ? null : pan(); // null: a rescale not yet redrawn
    if (!p) {
      if (d.dragged) Object.assign(d, e.position);
      return;
    }
    const act = dragAct(d, e.position, p, clamp);
    if (!act.dragged) return;
    Object.assign(d, e.position, { dragged: true });
    set(act.pan);
    cx.notify();
  };
  const stage = (el, pannable, pan, clamp, set) => el.when(pannable && !held(), (x) => x.cursor_grab()).when(held(), (x) => x.cursor_grabbing())
    .on_mouse_down("left", down).on_mouse_up("left", end).on_mouse_down_out(end)
    .on_hover((_hovered, cx) => end(null, cx)) // leaving ends it; so does entering, should a leave at the window's edge go unreported
    .on_mouse_move(move(pan, clamp, set));
  return { down, end, stage, clicks: () => !wasDrag(v.graphDrag, Date.now()) };
}

// ah2-103: a member opens its pane by the sidebar's own path (main.js member): the live terminal terminalFinder gives for the
// member's served pane, or its ah.name label; none, and the member is inert. `rest` is its border at rest: one already in accent
// shows focus in foreground. A press on it starts its stage's drag (gr: grip), and the release of a drag does not open it.
const addressOf = (v, id, pane) => v.graphTerminal?.({ pane, name: memberName(id) }) ?? null;
const opens = (el, t, address, rest, gr) => el.cursor_pointer().hover((s) => s.border_color(t.foreground))
  .focus((s) => s.border_color(rest === t.accent ? t.foreground : t.accent))
  .on_mouse_down("left", gr.down).on_mouse_up("left", gr.end)
  .on_click(() => gr.clicks() && run("pane.focus", { terminal: address }));

// graphModel (flowModel in Flow) only when an input changed: the slices (v.sliceGen, bumped by the poll), the shown team, window,
// cursor, zoom and scale, the size (docked or not), the minute (the scrubber's clock moves in whole minutes), the mode and orientation,
// and in Flow the room (Flow fits its stage). A throw is kept the same way. A pan is not an input: it only moves what is laid out.
const orientOf = (v) => v.graphOrient ?? v.graphPicked;
function graphOf(v, docked) {
  const flow = v.graphMode === "flow";
  const key = [v.sliceGen, v.graphTeam, v.graphWindow, v.graphCursor, v.graphZoom, v.graphScale, docked, Math.floor(Date.now() / STEP_MS),
    v.graphMode, orientOf(v), flow ? v.graphRoom?.w : null, flow ? v.graphRoom?.h : null];
  const memo = v.graphMemo;
  if (memo && key.every((k, i) => k === memo.key[i])) return memo;
  let m = null;
  let error = null;
  const s = v.graphScale;
  try {
    m = flow ? flowModel(v.st.slices, v.graphTeam, orientOf(v) ?? null, s, v.graphRoom ?? null)
      : graphModel(v.st.slices, v.graphTeam, docked ? (d) => dockSize(d) * s : MAP_PX * s, v);
  } catch (e) {
    error = e?.message ?? String(e);
  }
  // the stage's shape picked the orientation: it holds for this pane from now on
  if (m?.picked && !orientOf(v)) v.graphPicked = m.picked;
  return (v.graphMemo = { key, m, error });
}

// Map | Flow, or Flow's other orientation: back at fit (Map's drill and Flow's pan never carry across), and remembered.
function choose(v, patch, cx) {
  Object.assign(v, patch);
  drillTo(v, null);
  setShared(GRAPH_KEY, { mode: v.graphMode, orient: v.graphOrient });
  cx.notify();
}

export function graphView(v, t, close) {
  const docked = isDocked();
  v.graphTerminal = terminalFinder(terminals(), target()); // per render, as the sidebar's (main.js)
  const { m: got, error } = graphOf(v, docked);
  const m = got ?? { loaded: true, tabs: [], scrub: null, crumb: null }; // a failed layout draws the frame as when loading
  v.graphScrub = m.scrub;
  syncKnob(v, m.scrub);
  const loading = loadingOf(v, m.loaded);
  const zoom = (z) => drillTo(v, z);
  const flow = v.graphMode === "flow";
  let body;
  if (v.status === "down") body = [note(t, v.reason, "Retrying")];
  else if (error) body = [note(t, "Graph unavailable", error)];
  else if (loading) body = [note(t, "Loading graph…")];
  else if (m.empty) body = [note(t, "No teams running.")];
  else if (m.closed) body = [note(t, "Team closed.")];
  else if (flow) body = flowBody(t, m, v);
  else {
    const parts = m.groups.map((g) => group(t, g, docked, (node) => zoom({ team: g.id, node }), v));
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
  // Flow's stage is the same room less its legend row (flowModel), so Flow redraws on every change of it.
  const measure = (size, cx) => {
    const room = { w: Math.round(size.width - 32), h: Math.round(size.height - 12 - 12 - CHIP) };
    if (v.graphRoom?.w === room.w && v.graphRoom?.h === room.h) return;
    v.graphRoom = room;
    if (v.graphScale > 1 || v.graphMode === "flow") cx.notify();
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
      .child(modeTabs(v, t))
      .children(flow ? [orientButton(v, t)] : [])
      .children(scaleBar(v, t)))
    .when(Boolean(m.crumb), (el) => el.child(crumb(t, m.crumb, (_e, cx) => {
      zoom(null);
      cx.notify();
    })))
    .child(v_flex().id("graph-body").flex_1().min_h(0).px(16).pb(12).overflow_y_scrollbar().on_size_change(measure).children(body))
    .children(flow ? [] : [scrubber(v, t, loading || v.status === "down" ? null : m.scrub)]);
}

// Map | Flow, styled as the scrubber's window tabs.
function modeTabs(v, t) {
  const tab = (mode, name) => {
    const on = v.graphMode === mode;
    return Tab.new(`graph-mode-${mode}`).selected(on)
      .h(24).px(8).flex().flex_none().items_center().whitespace_nowrap().rounded(6).cursor_pointer().text_size(11.5)
      .border(1).border_color(on ? t.raised : t.surface).bg(on ? t.raised : t.surface).text_color(on ? t.foreground : t.muted)
      .hover((el) => el.text_color(t.foreground)).focus((el) => el.border_color(t.accent))
      .accessibility_label(`${name} layout`)
      .on_mouse_down("left", stop)
      .on_click((_e, cx) => (on ? null : choose(v, { graphMode: mode }, cx)))
      // a Tab does not wire tooltip (herdr-app check warns); the label inside it does
      .child(div().id(`graph-mode-${mode}-name`).tooltip(`Show the ${name}`).child(name));
  };
  return Tabs.new("graph-modes").gap(2).flex().flex_none().items_center().children([tab("map", "Map"), tab("flow", "Flow")]);
}

// Flow's orientation, as the web's toolbar button: ⇄ swaps horizontal and vertical.
function orientButton(v, t) {
  const vertical = orientOf(v) === "vertical";
  return Button.new("graph-orient").h(24).min_w(24).px(6).flex().flex_none().items_center().justify_center().rounded(6)
    .cursor_pointer().text_size(11.5).text_color(t.foreground).border(1).border_color(t.input)
    .focus((el) => el.border_color(t.accent)).hover((el) => el.bg(t.hover))
    .tooltip(vertical ? "Vertical ⇄ Horizontal" : "Horizontal ⇄ Vertical").accessibility_label("Switch between horizontal and vertical")
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => choose(v, { graphOrient: vertical ? "horizontal" : "vertical" }, cx))
    .child("⇄");
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

// The scale controls: −, the scale, +, Fit. A step is ×SCALE_STEP about each map's centre; Fit is scale 1, centred. Both ease (easeTo).
function scaleBar(v, t) {
  const s = v.graphScale;
  const btn = (id, text, tip, said, act) => Button.new(id).h(24).min_w(24).px(6).flex().flex_none().items_center().justify_center().rounded(6)
    .cursor_pointer().text_size(11.5).text_color(t.foreground).border(1).border_color(t.input)
    .focus((el) => el.border_color(t.accent)).hover((el) => el.bg(t.hover))
    .tooltip(tip).accessibility_label(said)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => act(cx))
    .child(text);
  return [
    btn("graph-scale-out", "−", "Zoom out (scroll to zoom, drag to pan)", "Zoom out", (cx) => easeTo(v, cx, { by: 1 / SCALE_STEP })),
    div().w(40).flex_none().text_center().font_family(MONO).text_size(11).text_color(t.muted).accessibility_label(`Scale ${Math.round(s * 100)} percent`)
      .child(`${Math.round(s * 100)}%`),
    btn("graph-scale-in", "+", "Zoom in (scroll to zoom, drag to pan)", "Zoom in", (cx) => easeTo(v, cx, { by: SCALE_STEP })),
    btn("graph-scale-fit", "Fit", "Fit the graph to the view", "Fit the graph to the view", (cx) => easeTo(v, cx, { to: 1 })),
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
function group(t, g, docked, zoomTo, v) {
  const pad = g.depth * LANE_PAD;
  const head = (mt) => (g.depth > 0 ? [h_flex().mt(mt).h(20).pl(pad - 14).gap(4).items_center().text_size(11).font_semibold().text_color(t.muted)
    .child(div().text_color(t.dim).child("↳")).child(div().min_w(0).truncate().child(g.name))] : []);
  const out = head(16);
  if (g.pending) return { map: [...out, quiet(t, "Loading…", pad)], links: [] };
  if (g.members.length === 0) out.push(label(t, "Members", 0, pad), quiet(t, "None", pad));
  else out.push(h_flex().mt(12).pl(pad).flex_none().justify_center().child(map(t, g, zoomTo, v)));
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

// Commands from graph-layout.js bundle() or flow-model.js flowCmds into a native stroke; a report runs dashed (5 4), as on the web.
function stroke(cmds, width, dash) {
  let b = PathBuilder.stroke(width);
  if (dash) b = b.dash_array([5, 4]);
  for (const c of cmds) {
    if (c[0] === "M") b = b.move_to(c[1], c[2]);
    else if (c[0] === "L") b = b.line_to(c[1], c[2]);
    else if (c[0] === "Q") b = b.curve_to(c[3], c[4], c[1], c[2]);
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

function map(t, g, zoomTo, v) {
  const m = g.map;
  const S = m.size;
  const s = v.graphScale;
  const F = S / s; // the frame: the map's square at scale 1
  const base = light(t.surface) ? 0.75 : 0.6;
  const gr = grip(v, g.id);
  const els = [];
  // streams: raised, ringed in muted (the web's idle mark is under 3:1 here); the team's circle is not drawn. A stream that is not
  // the one shown is a button: it zooms in.
  m.containers.forEach((c, i) => {
    const disc = (el) => el.absolute().left(c.x - c.r).top(c.y - c.r).size(c.r * 2).rounded(c.r).bg(t.raised).border(1).border_color(t.muted);
    if (!c.zoom) return els.push(disc(div().id(`gc-${g.id}-${i}`)).tooltip(c.title).accessibility_label(c.title));
    els.push(disc(Button.new(`gc-${g.id}-${i}`)).cursor_pointer().hover((s) => s.border_color(t.foreground)).focus((s) => s.border_color(t.accent))
      .tooltip(`${c.title}. Click to zoom in`).accessibility_label(`Zoom into ${c.title}`)
      .on_mouse_down("left", gr.down).on_mouse_up("left", gr.end)
      .on_click((_e, cx) => {
        if (!gr.clicks()) return;
        zoomTo(c.id);
        cx.notify();
      }));
  });
  for (const a of m.arcs) els.push(layer(stroke(a.cmds, a.width, a.kind === 1), Background.solid(t.accent).opacity(a.fade ?? base), S));
  m.leaves.forEach((n, i) => els.push(leaf(t, g, n, i, addressOf(v, n.id, n.pane), gr)));
  m.flights.forEach((f, i) => els.push(div().id(`gf-${g.id}-${i}`).absolute().left(f.x - 4).top(f.y - 4).size(8).rounded(4)
    .bg(t.accent).border(1).border_color(t.surface).tooltip(f.title).accessibility_label(f.title)));
  // container names sit on the circle's top edge, on a surface chip that stands in for the web's label halo
  m.containers.forEach((c, i) => c.label && els.push(h_flex().absolute().left(c.x - c.r).top(c.y - c.r - 8).w(c.r * 2).h(16).justify_center()
    .child(div().id(`gcl-${g.id}-${i}`).max_w(c.r * 1.6).h(16).px(4).flex().items_center().rounded(4).bg(t.surface)
      .text_size(11).font_semibold().text_color(t.muted).truncate().child(c.name))));
  const said = `${g.name} map: ${m.leaves.length} members${m.containers.length ? ` in ${m.containers.length} circles` : ""}, ${m.arcs.length} message arcs. The links say who messages whom.`;
  const box = frameOf(F, s, v.graphRoom, g.depth * LANE_PAD);
  const clamp = (p) => clampPan(p, F, s, box);
  const pan = clamp(v.graphPan[g.id]);
  // A scroll zooms (wheelAct), eased about the pointer (easeTo). A positive delta.y is a scroll up (gpui-kit
  // shell/src/tests/render.rs:10714, -200 scrolls a list down), so up zooms in. A drag pans (grip); it waits for the redraw after a
  // rescale (F and box are this render's).
  const wheel = (e, cx) => {
    const act = wheelAct(e);
    if (!act) return;
    cx.stop_propagation();
    easeTo(v, cx, { by: act.zoom, wheel: true, at: e.local_position ? { id: g.id, x: e.local_position.x, y: e.local_position.y - CHIP } : null });
  };
  const pannable = S > box.w || S > box.h;
  const now = () => (v.graphScale === s ? clamp(v.graphPan[g.id]) : null);
  // the frame is CHIP taller, the map drawn CHIP down, so a stream's name chip on a circle at the top edge is not clipped
  return gr.stage(div().id(`gmap-${g.id}`), pannable, now, clamp, (p) => { v.graphPan[g.id] = p; })
    .relative().w(box.w).h(box.h + CHIP).flex_none().overflow_hidden().accessibility_label(said).on_scroll_wheel(wheel)
    .child(div().id(`gmap-${g.id}-in`).absolute().left(pan.x).top(pan.y + CHIP).size(S).children(els));
}

// A member: its body, the busy track and ring (the share of the last hour it worked, in its state's tone), then the label tiers
// its radius has room for: context, state mark and name, role, model · state, item. The mark's shape keeps it from resting on colour.
function leaf(t, g, n, i, address, gr) {
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
  const at = address ? opens(Button.new(`gm-${g.id}-${i}`), t, address, t.border, gr) : div().id(`gm-${g.id}-${i}`);
  return at.absolute().left(n.x - r).top(n.y - r).size(r * 2).rounded(r)
    .bg(t.surface).border(1).border_color(t.border).when(n.state === "gone", (el) => el.opacity(0.4))
    .tooltip(address ? `${full}. Click to open its pane` : full).accessibility_label(address ? `${full}. Open its pane` : full)
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

// ---- Flow: graph-model.js flowModel, drawn as the web's Flow (graph.js FlowStage, r6-graph.css .gflow) in Herdr tokens ----

// The stage (bands, links, column labels, cards, the in-flight marks) at its pan, then the legend. Text scales with the drawn scale k,
// as the web's does under its transform; the marks, the legend and "+N more" do not.
function flowBody(t, m, v) {
  if (m.pending) return [quiet(t, "Loading…", 0)];
  if (m.cards.length === 0) return [label(t, "Members", 0, 0), quiet(t, "None", 0)];
  const k = m.k;
  const s = v.graphScale;
  const gr = grip(v, "flow");
  const sized = (el) => el.absolute().top(0).left(0).w(m.size.w).h(m.size.h);
  const els = [];
  m.bands.forEach((b, i) => els.push(div().id(`gfb-${i}`).absolute().left(b.x).top(b.y).w(b.w).h(b.h).rounded(6).bg(t.hover)
    .child(div().absolute().left(8).top(2).max_w(Math.max(0, b.w - 16)).truncate().text_size(11 * k).text_color(t.muted).child(b.name))));
  for (const l of m.links) {
    const w = Math.max(1, (l.kind === "edge" ? 1 : 2) * k);
    els.push(sized(window.paint_path(stroke(l.cmds, w, l.kind === "review"), l.kind === "edge" ? t.muted : t.accent)));
  }
  m.labels.forEach((l) => els.push(div().absolute().left(l.x).top(l.y).whitespace_nowrap().text_size(11 * k).font_semibold().text_color(t.muted).child(l.text)));
  m.cards.forEach((c, i) => els.push(flowCard(t, c, i, k, addressOf(v, c.id, c.pane), gr)));
  m.dots.forEach((d, i) => els.push(div().id(`gfd-${i}`).absolute().left(d.x - 4).top(d.y - 4).size(8).rounded(4)
    .bg(t.accent).border(1).border_color(t.surface).tooltip(d.title).accessibility_label(d.title)));
  const clamp = (p) => flowPan(p, m.size, m.stage);
  const pan = clamp(v.graphPan.flow);
  const big = m.size.w > m.stage.w || m.size.h > m.stage.h;
  // As the Map's: a scroll zooms, eased about the pointer; a drag pans content larger than the stage, and waits for the redraw after
  // a rescale (m is laid out at this render's scale).
  const wheel = (e, cx) => {
    const act = wheelAct(e);
    if (!act) return;
    cx.stop_propagation();
    easeTo(v, cx, { by: act.zoom, wheel: true, at: e.local_position ? { x: e.local_position.x, y: e.local_position.y } : null });
  };
  const now = () => (v.graphScale === s ? clamp(v.graphPan.flow) : null);
  const stage = gr.stage(div().id("gflow"), big, now, clamp, (p) => { v.graphPan = { flow: p }; }).relative().mt(12).w(m.stage.w).h(m.stage.h).flex_none().overflow_hidden().accessibility_label(m.said).on_scroll_wheel(wheel)
    .child(div().id("gflow-in").absolute().left(pan.x).top(pan.y).w(m.size.w).h(m.size.h).children(els))
    .when(m.rest > 0, (el) => el.child(div().absolute().top(8).right(8).text_size(11).text_color(t.muted).child(`+${m.rest} more briefs`)));
  return [stage, flowLegend(t)];
}

// What the links mean, under the stage: the same strokes as the links, at legend size.
function flowLegend(t) {
  const key = (swatch, text) => h_flex().gap(6).flex_none().items_center().child(swatch).child(text);
  const dashed = window.paint_path(stroke([["M", 0, 1], ["L", 14, 1]], 2, true), t.accent).w(14).h(2).flex_none();
  return h_flex().id("gflow-legend").h(LEGEND_H).gap(12).flex_none().items_center().text_size(11).text_color(t.muted)
    .accessibility_label("Flow legend: accent line, brief in flight; dashed accent, sent for review; muted line, reporting line")
    .child(key(div().w(14).h(2).flex_none().bg(t.accent), "brief in flight"))
    .child(key(dashed, "sent for review"))
    .child(key(div().w(14).h(1).flex_none().bg(t.muted), "reporting line"))
    .child(div().flex_1().min_w(0).truncate().child("Idle members are one line; a time is the last activity"));
}

// One member: a card when it is working, blocked or running (accent border, the tag, a stats line), else one line. A field with no
// source is not drawn (the web's FlowCard).
function flowCard(t, c, i, k, address, gr) {
  const s = c.card;
  const r = c.rect;
  const reviewing = s.role === "reviewer" && s.state === "working";
  const state = s.running ? "running" : STATES[s.state] ? s.state : "unknown";
  const word = reviewing ? "reviewing" : stateWord(s.state, s.running);
  const duty = DUTY[s.duty] ?? null;
  const ago = s.lastAt > 0 ? age(Date.now() - s.lastAt) : "";
  const tag = c.active ? (reviewing ? "reviewing" : s.running ? "running" : s.tool ?? s.model)
    : s.state !== "idle" ? stateWord(s.state).toLowerCase() : !duty && ago ? ago : null;
  const head = h_flex().gap(8 * k).items_center().min_w(0)
    .child(stateMark(t, state))
    .child(div().flex_1().min_w(0).truncate().text_size(12.5 * k).text_color(t.foreground).when(c.active, (el) => el.font_semibold()).child(s.name))
    .children(duty ? [div().flex_none().text_size(11 * k).text_color(t.muted).child(duty)] : [])
    .children(tag ? [div().flex_none().text_size(11 * k).text_color(c.active ? t.accent : t.muted).child(tag)] : []);
  const cell = (value, unit) => h_flex().gap(4 * k).flex_none().child(div().font_semibold().text_color(t.foreground).child(String(value))).children(unit ? [unit] : []);
  const stats = c.active ? [
    s.orchestrator ? cell(c.out, "out") : null,
    s.orchestrator && s.decisions !== null ? cell(s.decisions, "dec") : null,
    s.role === "implementor" && s.round ? cell(s.round) : null,
    ago ? h_flex().gap(4 * k).flex_none().child("active").child(div().font_semibold().text_color(t.foreground).child(ago)) : null,
  ].filter(Boolean) : [];
  const said = `${s.name}, ${duty ? `${DUTY_TITLE[s.duty]}, ` : ""}${word}`;
  const rest = c.active ? t.accent : t.border;
  const tip = [s.full, s.role, word].filter(Boolean).join(" · ");
  const at = address ? opens(Button.new(`gfc-${i}`), t, address, rest, gr).flex().flex_col() : v_flex().id(`gfc-${i}`);
  return at.absolute().left(r.x).top(r.y).w(r.w).h(r.h).px(10 * k).py(8 * k).gap(8 * k).when(!c.active, (el) => el.justify_center()).overflow_hidden()
    .rounded(6).border(1).border_color(rest).bg(c.active ? t.selected : t.raised)
    .when(s.state === "gone", (el) => el.opacity(0.4))
    .tooltip(address ? `${tip}. Click to open its pane` : tip).accessibility_label(address ? `${said}. Open its pane` : said)
    .child(head)
    .when(c.active, (el) => el.child(div().h(1).flex_none().bg(t.border))
      .when(stats.length > 0, (el2) => el2.child(h_flex().gap(8 * k).items_center().text_size(11 * k).text_color(t.muted).children(stats))));
}
