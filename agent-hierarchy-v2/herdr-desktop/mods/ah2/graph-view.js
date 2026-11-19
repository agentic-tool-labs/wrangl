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
// at every scale, within the keep rule (grip, dragAct, keepPan); a press and release short of it is still a click.
// Map | Flow: the header's mode tabs switch to the web's Flow (graph-model.js flowModel over the shared flow-model.js): cards in four
// role columns, links between them, lanes as bands. Read-only as the Map is. The choice is kept in Herdr's shared store (GRAPH_KEY).

import { div, svg, PathBuilder, Background } from "gpui-kit";
import { shared, setShared, run, terminals } from "herdr";
import { v_flex, h_flex, Tabs, Tab, Button, Slider, SliderState, SliderTrack, SliderIndicator, SliderThumb } from "gpui-base";
import { isDocked } from "./dock.js";
import { graphModel, MAP_PX, KNOB, cursorAt, stepCursor, SCALE_STEP, clampPan, drillTo, rescaleTo, frameOf, LANE_PAD,
  flowModel, graphChoice, flowPan, flowRescale, LEGEND_H, clampScale, wheelAct, easedScale, dragAct, wasDrag,
  cardStyle, edgeStyle, dotColor, ageText, travelsOf, travelClock, dotAt, WEB, webGraphLink, sheetUi, sheetClick, paneAct, memberPageLink, laneStyle, isReviewing, paneOfMember, roleHue, roleIcon } from "./graph-model.js";
import { GRAPH_KEY } from "./settings-model.js";
import { DUTY, DUTY_TITLE, stateWord } from "./select.js";
import { STEP_MS } from "./graph-world.js";
import { loadingOf, terminalFinder, memberName } from "./model.js";
import { target } from "./ah.js";
import { stateMark } from "./marks.js";
import { sheetColumn } from "./board.js";
import { memberModel, graphExtras, SUMMARY_MESSAGES } from "./member-model.js";
import { glyphOf } from "./graph-world.js";
import { streamName } from "./plans-model.js";

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
export function graphInit(v, cx) {
  // focus handles are never made in render (herdr refuses it): the sheet's here, a node's in its click (opens)
  v.graphSheetKey = cx?.focus_handle() ?? null;
  v.graphNodeKeys = new Map();
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
  const stage = (el, pan, clamp, set) => el.when(!held(), (x) => x.cursor_grab()).when(held(), (x) => x.cursor_grabbing())
    .on_mouse_down("left", down).on_mouse_up("left", end).on_mouse_down_out(end)
    .on_hover((_hovered, cx) => end(null, cx)) // leaving ends it; so does entering, should a leave at the window's edge go unreported
    .on_mouse_move(move(pan, clamp, set));
  return { down, end, stage, clicks: () => !wasDrag(v.graphDrag, Date.now()) };
}

// A member's pane (ah2-103): the live terminal terminalFinder gives for the member's served pane, or its ah.name label; else null.
const addressOf = (v, id, pane) => v.graphTerminal?.({ pane, name: memberName(id) }) ?? null;
// A node is a button that opens its member's sheet (spec herdr-graph-agent-details D2, D3): the same node again closes it, another
// swaps it; a press starts its stage's drag (gr: grip) and the release of a drag is not a click (sheetClick). Go to pane is in the
// sheet (D4), the sheet's first focus, so a click then Enter jumps. `rest` is its border at rest: one in accent shows focus in
// foreground. Its focus handle (v.graphNodeKeys) takes focus back when the sheet closes (D7).
const opens = (el, t, v, id, rest, gr) => el.cursor_pointer().hover((s) => s.border_color(t.foreground))
  .focus((s) => s.border_color(rest === t.accent ? t.foreground : t.accent))
  .when(Boolean(v.graphNodeKeys?.get(id)), (x) => x.track_focus(v.graphNodeKeys.get(id)))
  .on_mouse_down("left", gr.down).on_mouse_up("left", gr.end)
  .on_click((_e, cx) => {
    const was = v.graphSheet;
    if (!v.graphNodeKeys.has(id)) v.graphNodeKeys.set(id, cx.focus_handle()); // made in the handler, so the close can focus back
    if (!sheetClick(v, id)) return;
    if (v.graphSheet) v.graphSheetKey?.focus();
    else v.graphNodeKeys?.get(was)?.focus();
    cx.notify();
  });
// Close the sheet, focus back on its node (D7).
function closeSheet(v) {
  const id = v.graphSheet;
  v.graphSheet = null;
  v.graphNodeKeys?.get(id)?.focus();
}

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

export function graphView(v, t, close, cx) {
  const docked = isDocked();
  v.graphTerminal = terminalFinder(terminals(), target()); // per render, as the sidebar's (main.js)
  const { m: got, error } = graphOf(v, docked);
  const m = got ?? { loaded: true, tabs: [], scrub: null, crumb: null }; // a failed layout draws the frame as when loading
  v.graphScrub = m.scrub;
  syncKnob(v, m.scrub);
  const loading = loadingOf(v, m.loaded);
  const zoom = (z) => drillTo(v, z);
  const flow = v.graphMode === "flow";
  const sheetOn = Boolean(v.graphSheet) && !loading && v.status !== "down" && !error;
  const sheetEl = sheetOn ? agentSheet(v, t, m, docked) : null;
  let body;
  if (v.status === "down") body = [note(t, v.reason, "Retrying")];
  else if (error) body = [note(t, "Graph unavailable", error)];
  else if (loading) body = [note(t, "Loading graph…")];
  else if (m.empty) body = [note(t, "No teams running.")];
  else if (m.closed) body = [note(t, "Team closed.")];
  else if (flow) body = flowBody(t, m, v, cx);
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
        v.graphSheet = null; // another team: its sheet is not this one's
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
      if (e.keystroke === "escape" && v.graphSheet) {
        closeSheet(v); // the sheet first (the Board's Esc layering), then the zoom, then the window
        cx.notify();
      } else if (e.keystroke === "escape" && m.crumb) {
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
      .children(scaleBar(v, t, loading || v.status === "down" ? null : m.id)))
    .when(Boolean(m.crumb), (el) => el.child(crumb(t, m.crumb, (_e, cx) => {
      zoom(null);
      cx.notify();
    })))
    // the agent's sheet beside the graph, or the whole column when docked (the Board's sheet, board.js sheetColumn)
    .child(docked && sheetEl ? sheetEl : h_flex().flex_1().min_h(0)
      .child(v_flex().id("graph-body").flex_1().min_w(0).h_full().min_h(0).px(16).pb(12).overflow_y_scrollbar().on_size_change(measure).children(body))
      .children(sheetEl ? [sheetEl] : []))
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
function scaleBar(v, t, team) {
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
    openWeb(t, webGraphLink(v.link?.port ?? null, team)),
  ];
}

// Open in browser (V9), beside Fit: the web UI's Graph for the shown team in the system browser (cx.open_url, which takes only an
// http(s) URL). Disabled, its tooltip says why, while ahd's page or the team is not known.
function openWeb(t, link) {
  return Button.new("graph-open-web").h(24).min_w(24).px(6).flex().flex_none().items_center().justify_center().rounded(6)
    .text_size(11.5).border(1).border_color(t.input).disabled(!link.url)
    .when(Boolean(link.url), (el) => el.cursor_pointer().text_color(t.foreground).hover((s) => s.bg(t.hover)))
    .when(!link.url, (el) => el.text_color(t.muted))
    .focus((el) => el.border_color(t.accent))
    .tooltip(link.url ? "Open in browser" : link.why).accessibility_label(link.url ? "Open in browser" : link.why)
    .on_mouse_down("left", stop)
    .on_click((_e, cx) => link.url && cx.open_url(link.url))
    .child("↗");
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
export const light = (hex) => {
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
  // ah2-225: a subagent's dashed line to its owner under the circles, then the jobs and subagents beside their owners
  for (const u of m.unders ?? []) if (u.link) els.push(layer(stroke([["M", u.x, u.y], ["L", u.link.x, u.link.y]], 1, true), Background.solid(t.muted), S));
  m.leaves.forEach((n, i) => els.push(leaf(t, g, n, i, v, gr)));
  (m.unders ?? []).forEach((u, i) => els.push(underDot(t, g, u, i)));
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
  const now = () => (v.graphScale === s ? clamp(v.graphPan[g.id]) : null);
  // the frame is CHIP taller, the map drawn CHIP down, so a stream's name chip on a circle at the top edge is not clipped
  return gr.stage(div().id(`gmap-${g.id}`), now, clamp, (p) => { v.graphPan[g.id] = p; })
    .relative().w(box.w).h(box.h + CHIP).flex_none().overflow_hidden().accessibility_label(said).on_scroll_wheel(wheel)
    .child(div().id(`gmap-${g.id}-in`).absolute().left(pan.x).top(pan.y + CHIP).size(S).children(els));
}

// A member: its body, the busy track and ring (the share of the last hour it worked, in its state's tone), then the label tiers
// its radius has room for: context, state mark and name, role, model · state, item. The mark's shape keeps it from resting on colour.
function leaf(t, g, n, i, v, gr) {
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
  const at = opens(Button.new(`gm-${g.id}-${i}`), t, v, n.id, t.border, gr);
  return at.absolute().left(n.x - r).top(n.y - r).size(r * 2).rounded(r)
    .bg(t.surface).border(1).border_color(t.border).when(n.state === "gone", (el) => el.opacity(0.4))
    .tooltip(`${full} · peer session. Click for its details`).accessibility_label(`${full}, peer session. Show its details`)
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

// ah2-225 B1/B3: a job or subagent on the Map, a small circle beside its owner: the job mark ◷ (⚠ when overdue) or a dashed ring for a
// subagent, and, when the circle has room, "subagent" or the time against its estimate. Not a target: the tooltip has the full text.
function underDot(t, g, u, i) {
  const r = u.r;
  return div().id(`gu-${g.id}-${i}`).absolute().left(u.x - r).top(u.y - r).size(r * 2).rounded(r).bg(t.surface)
    .border(u.overdue ? 2 : 1).border_color(u.overdue ? t.blocked : t.muted).when(u.kind === "subagent", (el) => el.border_dashed())
    .tooltip(u.tip).accessibility_label(u.tip)
    .child(v_flex().size_full().items_center().justify_center()
      .child(div().text_size(11).text_color(t.foreground).child(u.overdue ? "⚠" : u.kind === "subagent" ? "◇" : "◷"))
      .when(r >= 24, (el) => el.child(div().max_w(Math.round(r * 1.7)).truncate().text_size(10.5).text_color(t.muted).child(u.kind === "subagent" ? "subagent" : u.run))));
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

// ---- Flow: graph-model.js flowModel, drawn as the web's Flow (graph.js FlowStage, r6-graph.css .gflow) ----
// Neutrals are Herdr's theme() tokens; role, working and review hues are the web's own (graph-model.js WEB, cardStyle, edgeStyle),
// picked by the theme's lightness, as Herdr's theme has none of them (spec herdr-graph-polish D2, Amendment 1).

// The web's 20 px dot grid behind the stage (r6-graph.css .gflow .dot-grid): one native fill path, built again only when the stage
// changes size, so a redraw costs nothing for it.
const GRID = 20;
function gridOf(v, w, h) {
  const g = v.graphGrid;
  if (g?.w === w && g.h === h) return g.path;
  let b = PathBuilder.fill();
  for (let y = GRID / 2; y < h; y += GRID) for (let x = GRID / 2; x < w; x += GRID) b = b.add_polygon([[x - 1, y - 1], [x + 1, y - 1], [x + 1, y + 1], [x - 1, y + 1]], true);
  v.graphGrid = { w, h, path: b.build() };
  return v.graphGrid.path;
}

// The stage (dot grid, bands, links, column labels, cards, the briefs' dots) at its pan, then the legend. Text scales with the drawn
// scale k, as the web's does under its transform; the dots, the legend and "+N more" do not. A brief's dot runs its link from
// sender to receiver at the web's pace, round and round while it is in flight (travelsOf, dotAt; one script clock, travelClock).
function flowBody(t, m, v, cx) {
  if (m.pending) return [quiet(t, "Loading…", 0)];
  if (m.cards.length === 0) return [label(t, "Members", 0, 0), quiet(t, "None", 0)];
  const k = m.k;
  const s = v.graphScale;
  const lt = light(t.surface);
  const w = lt ? WEB.light : WEB.dark;
  const gr = grip(v, "flow");
  const now = Date.now();
  if (cx) travelClock(v, cx, travelsOf(v, m.dots, now));
  const sized = (el) => el.absolute().top(0).left(0).w(m.size.w).h(m.size.h);
  const els = [];
  // V10: each lane's faint band first, behind everything (laid out with the model, so a frame adds nothing)
  (m.lanes ?? []).forEach((l) => els.push(div().absolute().left(l.x - 8 * k).top(l.y - 4 * k).w(Math.max(0, l.w + 16 * k)).h(Math.max(0, l.h)).rounded(6).bg(laneStyle(l.i, lt).band)));
  m.bands.forEach((b, i) => els.push(div().id(`gfb-${i}`).absolute().left(b.x).top(b.y).w(b.w).h(b.h).rounded(6).bg(t.hover)
    .child(div().absolute().left(8).top(2).max_w(Math.max(0, b.w - 16)).truncate().text_size(11 * k).text_color(t.muted).child(b.name))));
  for (const l of m.links) {
    const st = edgeStyle(l.kind, lt);
    els.push(sized(window.paint_path(stroke(l.cmds, Math.max(1, st.width * k), st.dash), st.color ?? t.muted)));
  }
  // column headers as the web's .flabel: small, uppercase, muted (no tracking: the kit has none)
  // V10: the header in its lane's colour, a 2 px rule of that colour under it, as wide as the lane's cards
  m.labels.forEach((l, i) => {
    const ls = laneStyle(i, lt);
    els.push(div().absolute().left(l.x).top(l.y).whitespace_nowrap().text_size(11 * k).text_color(ls.color).child(l.text.toUpperCase()));
    els.push(div().absolute().left(l.x).top(l.y + 16 * k).w(m.lanes?.[i]?.rule ?? 0).h(2).bg(ls.color));
  });
  m.cards.forEach((c, i) => els.push(flowCard(t, c, i, k, v, gr, lt, m.dots.length > 0))); // the clock runs while a dot is drawn
  (m.unders ?? []).forEach((u, i) => els.push(flowUnder(t, u, i, k, lt)));
  m.dots.forEach((d, i) => {
    const p = dotAt(d.pts, v.graphTravels?.get(d.key) ?? now, now, d.review);
    els.push(div().id(`gfd-${i}`).absolute().left(p.x - 3).top(p.y - 3).size(6).rounded(3) // the web's 6 px (graph.js .dot r=3)
      .bg(dotColor(d.review, lt)).border(1).border_color(t.surface).tooltip(d.title).accessibility_label(d.title));
  });
  const clamp = (p) => flowPan(p, m.size, m.stage);
  const pan = clamp(v.graphPan.flow);
  // As the Map's: a scroll zooms, eased about the pointer; a drag pans at every scale (keepPan), and waits for the redraw after
  // a rescale (m is laid out at this render's scale).
  const wheel = (e, cx2) => {
    const act = wheelAct(e);
    if (!act) return;
    cx2.stop_propagation();
    easeTo(v, cx2, { by: act.zoom, wheel: true, at: e.local_position ? { x: e.local_position.x, y: e.local_position.y } : null });
  };
  const drawn = () => (v.graphScale === s ? clamp(v.graphPan.flow) : null);
  const stage = gr.stage(div().id("gflow"), drawn, clamp, (p) => { v.graphPan = { flow: p }; }).relative().mt(12).w(m.stage.w).h(m.stage.h).flex_none().overflow_hidden().accessibility_label(m.said).on_scroll_wheel(wheel)
    .child(window.paint_path(gridOf(v, m.stage.w, m.stage.h), w.lineRow).absolute().top(0).left(0).w(m.stage.w).h(m.stage.h))
    .child(div().id("gflow-in").absolute().left(pan.x).top(pan.y).w(m.size.w).h(m.size.h).children(els))
    .when(m.rest > 0, (el) => el.child(div().absolute().top(8).right(8).text_size(11).text_color(t.muted).child(`+${m.rest} more briefs`)));
  return [stage, flowLegend(t, lt)];
}

// What the links mean, under the stage: the same strokes as the links, at legend size (the web's .flow-legend keys).
function flowLegend(t, lt) {
  const key = (swatch, text) => h_flex().gap(6).flex_none().items_center().child(swatch).child(text);
  const brief = edgeStyle("brief", lt);
  const review = edgeStyle("review", lt);
  const dashed = window.paint_path(stroke([["M", 0, 1], ["L", 14, 1]], 2, true), review.color).w(14).h(2).flex_none();
  return h_flex().id("gflow-legend").h(LEGEND_H).gap(12).flex_none().items_center().text_size(11).text_color(t.muted)
    .accessibility_label("Flow legend: solid accent line with a dot, brief in flight; dashed violet, sent for review; thin muted line, reporting line; dashed muted line and outline, subagent; ◷, job")
    .child(key(div().w(14).h(2).flex_none().bg(brief.color), "brief in flight"))
    .child(key(dashed, "sent for review"))
    .child(key(div().w(14).h(1).flex_none().bg(t.muted), "reporting line"))
    .child(key(window.paint_path(stroke([["M", 0, 1], ["L", 14, 1]], 1, true), t.muted).w(14).h(2).flex_none(), "subagent"))
    .child(key(div().flex_none().child("◷"), "job"))
    .child(div().flex_1().min_w(0).truncate().child("Idle members are one line; a time is the last activity"));
}

// One member, as the web's FlowCard: a card when it is working, blocked or running (its role's name, the tool or state on the right,
// a hairline, then the stats left), else one row (glyph, name, the state or the last activity on the right). A field with no source
// is not drawn. Colours and glyph: cardStyle; the glyph differs by state, so state never rests on colour alone.
function flowCard(t, c, i, k, v, gr, lt, live) {
  const s = c.card;
  const r = c.rect;
  const st = cardStyle(s, c.active, lt, t.surface);
  const reviewing = isReviewing(s, c.active);
  const word = reviewing ? "reviewing" : stateWord(s.state, s.running);
  const duty = DUTY[s.duty] ?? null;
  const ago = s.lastAt > 0 ? ageText(Date.now() - s.lastAt, live) : "";
  const tag = c.active ? (reviewing ? "reviewing" : s.running ? "running" : s.tool ?? s.model)
    : s.state !== "idle" ? stateWord(s.state).toLowerCase() : !duty && ago ? ago : null;
  const head = h_flex().flex_none().px(10 * k).py(8 * k).gap(8 * k).items_center().min_w(0)
    .when(c.active, (el) => el.border_b(1).border_color(t.border))
    .child(div().flex_none().text_size(12 * k).text_color(st.mark ?? t.blocked).child(st.glyph))
    .child(div().flex_1().min_w(0).truncate().text_size(12.5 * k).text_color(st.name ?? t.foreground).when(st.bold, (el) => el.font_semibold()).child(s.name))
    .children(duty ? [div().flex_none().text_size(11 * k).text_color(t.muted).child(duty)] : [])
    .children(tag ? [div().flex_none().text_size(11 * k).text_color(st.tag ?? t.muted).child(tag)] : []);
  const cell = (value, unit) => h_flex().gap(4 * k).flex_none().child(div().font_semibold().text_color(t.foreground).child(String(value))).children(unit ? [unit] : []);
  const stats = c.active ? [
    s.orchestrator ? cell(c.out, "out") : null,
    s.orchestrator && s.decisions !== null ? cell(s.decisions, "dec") : null,
    s.role === "implementor" && s.round ? cell(s.round) : null,
    ago ? h_flex().gap(4 * k).flex_none().child("active").child(div().font_semibold().text_color(t.foreground).child(ago)) : null,
  ].filter(Boolean) : [];
  const said = `${s.name}, ${duty ? `${DUTY_TITLE[s.duty]}, ` : ""}${word}`;
  const rest = st.line ?? t.border;
  const tip = [s.full, s.role, word, ago ? `active ${ago}` : null].filter(Boolean).join(" · ");
  const at = opens(Button.new(`gfc-${i}`), t, v, c.id, rest, gr).flex().flex_col();
  return at.absolute().left(r.x).top(r.y).w(r.w).h(r.h).when(!c.active, (el) => el.justify_center()).overflow_hidden()
    .rounded(6).border(1).border_color(rest).bg(st.fill ?? t.raised)
    .when(s.state === "gone", (el) => el.opacity(0.4))
    .tooltip(`${tip} · peer session. Click for its details`).accessibility_label(`${said}, peer session. Show its details`)
    .child(head)
    .when(stats.length > 0, (el) => el.child(h_flex().px(10 * k).py(6 * k).gap(12 * k).items_center().text_size(11 * k).text_color(t.muted).children(stats)));
}

// ah2-225: a job or subagent in Flow, as the web's FlowUnder: a row under its owner, or a card in Test (the label, then the owner and the
// time); a subagent's outline dashed with the "subagent" tag, a job the mark ◷, an overdue one "⚠ overdue". Not a target: no pane.
function flowUnder(t, u, i, k, lt) {
  const x = u.item;
  const r = u.rect;
  const sub = x.kind === "subagent";
  const late = x.overdue ? [div().flex_none().text_size(11 * k).font_semibold().text_color(t.blocked).child("⚠ overdue")] : [];
  // an overdue row's time carries the mark (⚠, the word in the tooltip): a row has no room for both; a card has both
  const run = div().flex_none().font_family(MONO).text_size(11 * k).when(x.overdue && !u.card, (el) => el.font_semibold().text_color(t.blocked)).when(!(x.overdue && !u.card), (el) => el.text_color(t.muted))
    .child(x.overdue && !u.card ? `⚠ ${u.run}` : u.run);
  const head = h_flex().flex_none().px(10 * k).py(8 * k).gap(8 * k).items_center().min_w(0)
    .when(u.card, (el) => el.border_b(1).border_color(t.border))
    .child(sub ? svg(roleIcon(x.role)).size(12 * k).flex_none().text_color(roleHue(x.role, lt))
      : div().flex_none().text_size(12 * k).text_color(t.muted).child("◷"))
    .child(div().flex_1().min_w(0).truncate().text_size(12 * k).text_color(t.muted).child(x.label))
    .children(sub ? [div().flex_none().text_size(11 * k).text_color(t.muted).child("subagent")] : [])
    .children(u.card ? late : [run]);
  return div().id(`gfu-${i}`).absolute().left(r.x).top(r.y).w(r.w).h(r.h).flex().flex_col().when(!u.card, (el) => el.justify_center()).overflow_hidden()
    .rounded(6).border(1).border_color(sub ? t.muted : t.border).when(sub, (el) => el.border_dashed()).bg(t.raised)
    .tooltip(u.tip).accessibility_label(u.tip)
    .child(head)
    .when(u.card, (el) => el.child(h_flex().px(10 * k).py(6 * k).gap(12 * k).items_center().justify_between().text_size(11 * k).text_color(t.muted)
      .child(div().min_w(0).truncate().child(x.ownerName || "team")).child(run)));
}

// ---- the agent's sheet (spec herdr-graph-agent-details): the web's member summary (member-summary.js MemberSummary, ported in
// member-model.js), in its order and wording: the name with Go to pane and close, role · model, the state and since, where it
// sits, Now, in Map the rail's extras (graph-panel.js), the latest messages, and Open page (in the browser). A member that has
// gone reads as gone; nothing here starts a timer (ages follow the dot clock's rule, ageText).


function agentSheet(v, t, m, full) {
  const id = v.graphSheet;
  const slices = v.st.slices;
  const mm = memberModel(slices, sheetUi(v), id, SUMMARY_MESSAGES);
  const live = (m.dots?.length ?? 0) > 0; // the dot clock redraws: seconds are true
  const since = (ms) => ageText(Date.now() - ms, live).replace(" ago", "");
  const go = paneAct(addressOf(v, id, paneOfMember(slices, id)), mm.label ?? mm.name, mm.gone);
  const page = memberPageLink(v.link?.port ?? null, id);
  const icon = (bid, file, label, act, why) => Button.new(bid).size(24).flex().flex_none().items_center().justify_center().rounded(6)
    .border(1).border_color(t.surface).focus((el) => el.border_color(t.accent)).disabled(Boolean(why))
    .when(!why, (el) => el.cursor_pointer().text_color(t.foreground).hover((s) => s.bg(t.hover)))
    .when(Boolean(why), (el) => el.text_color(t.muted))
    .tooltip(why ?? label).accessibility_label(why ?? label)
    .on_mouse_down("left", stop).on_click((_e, cx) => !why && act(cx))
    .child(svg(file).size(14));
  const goBtn = icon("graph-sheet-pane", "mods/ah2/app-window.svg", "Go to pane", () => run(go.cmd, go.args), go.why)
    .when(Boolean(v.graphSheetKey), (el) => el.track_focus(v.graphSheetKey)); // the sheet's first focus (Amendment 1): click, then Enter
  const shut = icon("graph-sheet-close", "mods/ah2/x.svg", "Close the details (esc)", (cx) => { closeSheet(v); cx.notify(); }, null);
  const dim = (text) => div().text_color(t.muted).child(text);
  const row = (k, val, mono = false) => h_flex().min_h(24).gap(10).items_start().text_size(12)
    .child(div().w(96).flex_none().pt(1).text_size(11).text_color(t.muted).child(k))
    .child(div().flex_1().min_w(0).text_color(t.foreground).when(mono, (el) => el.font_family(MONO)).child(val));
  const head = (text) => div().pt(10).pb(4).text_size(11).font_semibold().text_color(t.muted).child(text);
  const where = [`team ${mm.teamShown ?? mm.team}`, mm.lane ? `lane ${mm.lane.name}` : null, mm.lead ? `takes direction from ${mm.lead.name}` : null].filter(Boolean).join(" · ");
  const now = [
    mm.tool ? row("tool", `${mm.tool}${mm.toolAt ? ` · ${since(Date.parse(mm.toolAt))}` : ""}`) : null,
    mm.item ? row("item", `${mm.item.key}${mm.item.title ? ` · ${mm.item.title}` : ""}`) : null,
    mm.branch ? row("branch", mm.branch) : null,
    mm.workRoot ? row("worktree", mm.workRoot, true) : null,
  ].filter(Boolean);
  const x = v.graphMode !== "flow" && !mm.gone ? graphExtras(slices, v, mm.teamId, id) : null;
  const extras = x ? [
    row("stream", x.stream ? streamName(x.stream) : "—"),
    row("task", x.task),
    row("context used", `${x.ctx}${x.ctxAt > 0 ? ` · ${ageText(Date.now() - x.ctxAt, live)}` : ""}`),
    row(x.busyLabel, x.busy),
    h_flex().min_h(24).gap(10).items_start().text_size(12)
      .child(div().w(96).flex_none().pt(1).text_size(11).text_color(t.muted).child("messages"))
      .child(h_flex().flex_1().min_w(0).gap(4).child(div().text_color(t.foreground).child(String(x.count ?? "—"))).child(dim(x.scope))),
    ...(x.dir ? [row("sent", String(x.dir.sent)), row("received", String(x.dir.received))] : []),
    row("tokens", "—"),
    row("files changed", "—"),
  ] : [];
  // the leased exchanges arrive with the next poll after the sheet opens (a long poll is not cut short): until then, Loading
  const waiting = (slices.get(id)?.body.last_exchanges ?? []).slice(0, SUMMARY_MESSAGES).some((x) => !slices.has(x));
  const msgs = mm.rows.length === 0 ? [div().py(4).text_size(12).text_color(t.muted).child(waiting ? "Loading…" : "No messages yet.")]
    : mm.rows.map((r, i) => h_flex().id(`graph-sheet-msg-${i}`).min_h(24).py(2).gap(8).items_start().text_size(12).border_b(1).border_color(t.border)
      .child(div().w(38).flex_none().pt(1).font_family(MONO).text_size(11).text_color(t.muted).child(r.time))
      .child(div().w(44).flex_none().text_size(11).text_color(t.muted).child(r.kind))
      .child(v_flex().flex_1().min_w(0).child(div().truncate().text_color(t.foreground).child(`${r.from} → ${r.to}`))
        .when(Boolean(r.item), (el) => el.child(div().truncate().text_size(11).text_color(t.muted).child(r.item)))));
  const pageBtn = Button.new("graph-sheet-page").h(28).px(10).mt(10).flex().flex_none().items_center().rounded(6).text_size(12)
    .border(1).border_color(t.border).focus((el) => el.border_color(t.accent)).disabled(!page.url)
    .when(Boolean(page.url), (el) => el.cursor_pointer().text_color(t.foreground).hover((s) => s.bg(t.hover)))
    .when(!page.url, (el) => el.text_color(t.muted))
    .tooltip(page.url ? `Open ${mm.label ?? mm.name}'s page in the browser` : page.why).accessibility_label(page.url ? `Open ${mm.label ?? mm.name}'s page in the browser` : page.why)
    .on_mouse_down("left", stop).on_click((_e, cx) => page.url && cx.open_url(page.url))
    .child("Open page");
  return sheetColumn(t, full, "graph-sheet").accessibility_label(`Member ${mm.title ?? mm.name}`)
    .child(v_flex().px(16).pt(8).pb(10).gap(4).flex_none().border_b(1).border_color(t.border)
      .child(h_flex().h(28).gap(6).items_center()
        .child(div().flex_1().min_w(0).truncate().text_size(13.5).font_semibold().text_color(t.foreground).child(mm.label ?? mm.name))
        .child(goBtn).child(shut))
      .child(h_flex().gap(4).text_size(12).text_color(t.foreground).child(`${mm.role || "member"} ·`)
        .child(mm.model ? div().child(mm.model) : dim("model not reported")))
      .child(h_flex().gap(6).items_center().text_size(12)
        .child(div().flex_none().text_color(t.foreground).child(`${glyphOf(mm.running ? "working" : mm.state)} ${stateWord(mm.state, mm.running)}`))
        .when(mm.since > 0, (el) => el.child(dim(`since ${since(mm.since)}`))))
      .child(div().text_size(12).text_color(t.muted).truncate().child(where)))
    .child(v_flex().id("graph-sheet-body").flex_1().min_h(0).px(16).pb(12).overflow_y_scrollbar()
      .child(head("Now"))
      .children(now) // the web hides each empty row, and so the whole list when nothing is known
      .children(extras)
      .child(head("Latest messages"))
      .children(msgs)
      .child(h_flex().child(pageBtn)));
}
