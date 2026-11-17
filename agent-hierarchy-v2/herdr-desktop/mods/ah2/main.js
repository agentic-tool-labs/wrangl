// ah2 in Herdr: the team sitrep in the sidebar (layout block, props {view:"sitrep"}), one team's full sitrep in a
// window overlay (props {view:"sitrep", team:"<dir_id>/<team>"}), and the Desk's tabs: Q&A (qa.js), Board (board.js), Feed
// (feed.js) and Graph (graph-view.js), each a block of the side column (props {view, dock:true}), or all four in one Desk window
// (props {view:"desk", tab}, dock.js) that swaps them in this one copy.
// The desk (layouts/wrangl.json): the side column's tab row (props {view:"panel-tabs"}, tabs.js), the status bar segment (props
// {view:"status"}: the one poll the desk reads, published as DESK_KEY) and the Needs-you drawer (props {view:"peek"}, floating).
// Toward ahd it polls ahd's loopback bridge, else runs `ah view events` (link-model.js); the Q&A window's Send posts the answer
// the same way (model.js sendAnswer).
// Colours are theme() tokens only.

import { View, div, image, svg } from "gpui-kit";
import { v_flex, h_flex, Button } from "gpui-base";
import { readTextFile, theme, run, props, agents, terminals, setAgentOrder, openOverlay, closeOverlay, notify, shared, setShared, placement, checking, nextCommand } from "herdr";
import { REBUILD_KEY, REBUILD_EVERY_MS, REBUILD_WATCH_MS, homeFor, rebuildOf, rebuildStep, rebuildGaveUp, rebuildNotify } from "./rebuild-model.js";
import { STATES, applyReply, age, counts, deskModel, needsMark, blockedText, sidebarModel, sitrepModel, qaSubs, loadingOf, terminalFinder, STUCK, retryMs } from "./model.js";
import { qaInit, qaPrune, qaTake, qaView } from "./qa.js";
import { boardSubs } from "./board-model.js";
import { boardView } from "./board.js";
import { feedSubs } from "./feed-model.js";
import { feedView } from "./feed.js";
import { graphSubs } from "./graph-model.js";
import { graphView, graphInit } from "./graph-view.js";
import { tabsView, tabsInit, menuView, panelTabsView } from "./tabs.js";
import { openView, closeOf, shownIn, isDocked, deskWindow } from "./dock.js";
import { markCell } from "./marks.js";
import { shownTeams, newAlerts, DESK_KEY, DESK_WINDOW_KEY, DOCKABLE, peekDismissed, peekClosed, peekPressOutside, unionSubs, qaShowing, INSTALL_COMMAND } from "./settings-model.js";
import { settings, settingsView, checkUpdate, installClick } from "./settings.js";
import { openDismiss, dismissInit, dismissView } from "./dismiss.js";
import { ah, target } from "./ah.js";
import { UNRESOLVED, targetKey } from "./target-model.js";
import { Link } from "./link-model.js";

const WAIT_S = "25"; // fits inside the 35 s exec timeout fetch passes
const BACKOFF_MS = [2000, 4000, 8000, 16000, 30000];
const MONO = "Geist Mono"; // ships with Herdr; gives ages and counts even digit widths (GPUI has no tabular-nums)
const AGENT_STATE = { working: "working", blocked: "blocked", done: "done", idle: "idle" };

const openTeam = (id) => openOverlay({ mod: "ah2", props: { view: "sitrep", team: id }, kind: "window", key: "ah2-sitrep", width: 620, height: 600 });

export default class Ah2 extends View {
  init(_props, cx) {
    // A click on the "available" notice (settings-model.js updateNotify) arrives as a command. Which copy herdr hands it to is
    // not documented, so every copy listens; installClick installs once however many receive it.
    if (!checking()) cx.spawn(async () => {
      for (;;) if ((await nextCommand()) === INSTALL_COMMAND) installClick();
    });
    this.team = props()?.team ?? null;
    this.view = props()?.view ?? "sitrep";
    // The Desk window: one copy for all four tabs; `view` is the tab shown (tabs.js draws its header, select switches it).
    this.desk = this.view === "desk";
    if (this.desk) this.view = DOCKABLE.includes(props()?.tab) ? props().tab : "qa";
    if (this.desk) this.max = props()?.max === true; // opened maximized (dock.js resizeDeskWindow)
    if (this.view === "tabs") return tabsInit(cx); // the sidebar's tab strip (tabs.js): the default tab and the update check, no poll
    if (this.view === "panel-tabs") return; // the desk's tab row reads DESK_KEY; no poll
    if (this.view === "peek") { // reads DESK_KEY; no poll. DESK_WINDOW_KEY is persisted: a stale window id would hide the drawer for good
      // ponytail: assumes no Desk window is open when the drawer's copy starts (a layout switch closes windows)
      const w = deskWindow();
      if (w?.overlay != null) setShared(DESK_WINDOW_KEY, { view: w.view, max: w.max });
      return;
    }
    if (this.view === "dismiss") return dismissInit(this, cx); // the Dismiss dialog (dismiss.js): its own bounded task, no poll
    if (this.view === "menu") { // the strip's … popup (tabs.js): keys only, no poll
      this.keys = cx.focus_handle();
      cx.spawn(async (cx) => {
        await cx.sleep(0);
        this.keys.focus();
      });
      return;
    }
    this.subKey = "";
    this.gotKey = null;
    this.ready = false; // model.js loadingOf: set on the first reply that answered the whole sub set; never cleared
    this.sentOrder = null;
    this.st = { slices: new Map(), epoch: null, seq: 0 };
    this.status = "loading"; // loading | up | down
    this.reason = "";
    this.updated = 0;
    this.abandoned = 0; // model.js pollExec: execs given up on that have not settled yet
    this.sliceGen = 0;
    if (this.desk || this.view === "qa" || this.view === "board") qaInit(this); // the board's waiting cards are Q&A cards
    if (this.desk || this.view === "graph") graphInit(this); // the shown team, the scrubber and the zoom (graph-view.js); once, never per switch
    // The Desk window says it is open, and on which tab (cleared in close): the peek drawer and every opener read it.
    if (this.desk) setShared(DESK_WINDOW_KEY, { overlay: (this.overlay = props()?.overlay ?? null), view: this.view, max: this.max });
    if (this.view === "settings") checkUpdate(); // fresh versions while the dialog is open
    if (this.team || ["qa", "board", "feed", "graph", "settings"].includes(this.view)) {
      this.keys = cx.focus_handle();
      // a window overlay does not take the keyboard by itself; Esc must reach us. A docked copy (dock.js) mounts in the layout
      // and leaves the keyboard where it is: it takes it when clicked, like any block.
      if (!props()?.dock) cx.spawn(async (cx) => {
        await cx.sleep(0);
        this.keys.focus();
      });
    }
    cx.timer.every(30000, (cx) => cx.notify()); // ages only; data redraws come from the poll
    cx.spawn((cx) => this.poll(cx));
  }

  // Herdr calls this when the copy goes (an overlay closed, or quit). Only our own id: reopening the same key replaces the window.
  async close() {
    this.gone = true; // ends the rebuild watch (rebuild)
    this.graphAnim?.cancel(); // an eased Graph zoom (graph-view.js easeTo) leaves no task behind
    if (this.overlay != null && deskWindow()?.overlay === this.overlay) setShared(DESK_WINDOW_KEY, { view: this.view, max: this.max }); // its last tab and size
  }

  // The Desk window's tab: switch it, tell the key (without any `want`), and on to Q&A take what the list holds (qa.js qaTake), as a
  // Q&A copy does after a reply. Leaving the Feed makes the next visit a new look (feed.js feedShowing).
  select(view, cx) {
    if (view !== this.view) {
      if (this.view === "feed") this.feedShowing = false;
      this.view = view;
      if (view === "qa") qaTake(this, undefined, cx);
      cx.notify();
    }
    setShared(DESK_WINDOW_KEY, { overlay: this.overlay, view, max: this.max });
  }

  // The Q&A window adds one questions: slice per running team (spec P3.1); the sidebar copy stays on view:summary. The Desk window
  // asks for every tab's at once (one long-poll cannot be cut short, so a switch must not wait on a new one).
  subs() {
    if (this.desk) {
      const s = this.st.slices;
      return unionSubs(["view:summary"], qaSubs(s), boardSubs(s), feedSubs(s), graphSubs(s, this));
    }
    if (this.view === "qa") return ["view:summary", ...qaSubs(this.st.slices)];
    if (this.view === "board") return ["view:summary", ...boardSubs(this.st.slices)];
    if (this.view === "feed") return ["view:summary", ...feedSubs(this.st.slices)];
    if (this.view === "graph") return ["view:summary", ...graphSubs(this.st.slices, this)];
    return this.team ? [`team-summary:${this.team}`, `sitrep:${this.team}`] : ["view:summary"];
  }

  // One reply: from `ah view events`, or from props.fixture (a saved reply, for running without a daemon).
  async fetch(cx) {
    const fixture = props()?.fixture;
    if (fixture) {
      const text = await readTextFile(fixture);
      if (text == null) throw new Error(`no fixture at ${fixture}`);
      return JSON.parse(text);
    }
    // `ah view events` has no fresh: a slice newly subscribed but published before the cursor would never come, so a changed
    // subscription starts again from 0 (one full reply, no wait).
    const sub = this.subs().join(",");
    if (sub !== this.subKey) {
      this.subKey = sub;
      this.st.seq = 0;
    }
    const q = { sub, since: this.st.seq, epoch: this.st.epoch, wait: this.st.seq ? WAIT_S : "0" };
    const timeout = this.st.seq ? 35000 : 10000; // `--wait 25` plus 10 s; a `--wait 0` reply needs no long wait
    // over ahd's loopback bridge, else through ah where it runs (link-model.js)
    this.link ??= new Link({ fetch: globalThis.fetch, runner: ah, sleep: (ms) => cx.sleep(ms), backoff: BACKOFF_MS });
    return this.link.poll(q, timeout, this);
  }

  // The long-poll. A redraw only when the data or the up/down state changed (every redraw is a whole-window redraw).
  async poll(cx) {
    let fails = 0;
    let downSince = null; // when the current run of failed polls began
    for (;;) {
      // The sidebar copy reads the rebuild stamp on load and on every turn of the poll (rebuild below); never in a check.
      if (this.view === "sitrep" && !this.team && !this.rebuildBusy && !checking()) cx.spawn((cx) => this.rebuild(cx));
      // A docked copy stays alive while its column is hidden or shows another view: it asks nothing until shown again.
      if (props()?.dock && !shownIn(this.view)) {
        await cx.sleep(2000);
        continue;
      }
      try {
        const reply = await this.fetch(cx);
        const changed = applyReply(this.st, reply);
        if (changed) this.sliceGen++; // graph-view.js keeps its model until this moves
        if (changed && (this.desk || this.view === "board")) qaPrune(this); // the Desk window holds Q&A cards on any tab
        // the sub set this reply answered (the Q&A window grows its set after the first reply, so it loads until this catches up)
        const got = props()?.fixture ? this.subs().join(",") : this.subKey;
        const caught = got !== this.gotKey;
        this.gotKey = got;
        // A window loads only until the first reply that answered the whole sub set; a later grown set does not blink it.
        if (got === this.subs().join(",")) this.ready = true;
        const was = this.status;
        this.status = "up";
        this.updated = Date.now();
        fails = 0;
        downSince = null;
        if (this.view === "qa" && (changed || caught)) qaTake(this, undefined, cx); // after ready and status: a whole answer only (qa.js)
        if (changed && this.view === "sitrep" && !this.team) this.alarms();
        if (this.view === "status") this.publishDesk();
        if (changed || caught || was !== "up") cx.notify();
        if (props()?.fixture) return;
        // The Refresh setting: at most one reply per interval (Live, 0: the long-poll alone paces it).
        const every = settings().interval;
        if (every) await cx.sleep(every * 1000);
      } catch (e) {
        const reason = e?.message ?? "ahd not running";
        downSince ??= Date.now();
        if (this.status !== "down" || this.reason !== reason) {
          this.status = "down";
          this.reason = reason;
          if (this.view === "status") this.publishDesk();
          cx.notify();
        }
        if (reason === UNRESOLVED) { // nothing to run ah with: no retry until the target changes (the setting, or herdr's machines)
          const was = targetKey(target());
          while (targetKey(target()) === was) await cx.sleep(2000);
          fails = 0;
          downSince = null;
          continue;
        }
        if (reason === STUCK) fails = BACKOFF_MS.length - 1; // held execs: recheck on the 30 s step until one settles
        await cx.sleep(reason === STUCK ? BACKOFF_MS[fails++] : retryMs(BACKOFF_MS, fails++, Date.now() - downSince));
      }
    }
  }

  render(cx) {
    const t = theme();
    if (this.desk) return this.drawDesk(t, cx);
    if (this.view === "tabs") return tabsView(t);
    if (this.view === "menu") return menuView(this, t);
    if (this.view === "panel-tabs") return panelTabsView(t);
    if (this.view === "status") return this.statusSegment(t);
    if (this.view === "peek") return this.peek(t);
    // Q&A, Board, Feed and Graph draw the same in a window or docked in the side column (dock.js); close hides whichever it is.
    if (this.view === "qa") return qaView(this, t, closeOf());
    if (this.view === "board") return boardView(this, t, closeOf());
    if (this.view === "feed") return feedView(this, t, closeOf());
    if (this.view === "graph") return graphView(this, t, closeOf());
    if (this.view === "settings") return settingsView(this, t, () => closeOverlay(props().overlay));
    if (this.view === "dismiss") return dismissView(this, t);
    return this.team ? this.sitrep(t) : this.sidebar(t);
  }

  // The Desk window: the Desk's tab row, then the shown tab's view; × and Esc close the window. An opener elsewhere asks for a
  // tab with `want` in the key (dock.js): taken once, after this render (a shared write redraws every mod, this one too), with
  // the keyboard.
  drawDesk(t, cx) {
    const w = deskWindow();
    if (w?.want && w.overlay === this.overlay && !this.wanting) {
      this.wanting = true;
      cx.spawn(async (cx) => {
        await cx.sleep(0);
        this.wanting = false;
        const want = deskWindow()?.want;
        if (DOCKABLE.includes(want)) this.select(want, cx);
        else this.select(this.view, cx); // an odd `want` is dropped, not retried
        this.keys.focus();
      });
    }
    const close = () => closeOverlay(props().overlay);
    const body = { qa: qaView, board: boardView, feed: feedView, graph: graphView }[this.view];
    return v_flex().id("ah2-desk-window").size_full().min_h(0).bg(t.surface)
      .child(panelTabsView(t, { view: this.view, select: (view, cx) => this.select(view, cx), close, max: this.max }))
      // a press in the body stays in the body (text selection); only the tab row above drags the window
      .child(div().id(`ah2-desk-${this.view}-body`).flex_1().min_h(0).on_mouse_down("left", (_e, cx) => cx.stop_propagation()).child(body(this, t, close)));
  }

  // The sidebar copy only: alarms not seen before become herdr notices when the setting is on. The first reply only seeds.
  alarms() {
    const { fresh, seen } = newAlerts(sidebarModel(this.st.slices, Date.now()).teams, this.seenAlerts ?? null);
    this.seenAlerts = seen;
    if (!settings().notify) return;
    for (const x of fresh.slice(0, 3)) {
      notify({ title: `Wrangl alarm · ${x.team.name}`, body: x.alert.text, level: "warning", action: { name: "layout.switch", args: { name: "wrangl" } } });
    }
  }

  // The background rebuild of ahd (ah2-111, rebuild-model.js): while the stamp says a build runs it is re-read every
  // REBUILD_EVERY_MS until the build ends, this copy closes, or REBUILD_WATCH_MS passes. One task at a time. Notices always show:
  // the "Alarms as herdr notices" setting covers team alarms only (settings.js).
  async rebuild(cx) {
    if (this.rebuildBusy) return;
    this.rebuildBusy = true;
    try {
      const end = Date.now() + REBUILD_WATCH_MS;
      while (!this.gone && (await this.rebuildRead())) {
        if (Date.now() >= end) {
          const held = shared(REBUILD_KEY) ?? null;
          const gaveUp = rebuildGaveUp(held);
          if (gaveUp !== held) setShared(REBUILD_KEY, gaveUp);
          return;
        }
        await cx.sleep(REBUILD_EVERY_MS);
      }
    } finally {
      this.rebuildBusy = false;
    }
  }

  // One read of the stamp where ah runs: keeps what it says, posts the notice it calls for, and answers whether to keep watching.
  // ah's home comes from `ah where`, held per pane for the runner's target (homeFor: a target change or a reply with no home
  // asks again); the stamp is then read as a file, no process. No home or no read (a WSL target: target-model.js read) says nothing.
  async rebuildRead() {
    this.ahHome ??= { key: null, home: null };
    const home = await homeFor(this.ahHome, JSON.stringify(target()), async () => {
      const r = await ah().exec("ah", ["where"], { timeout: 10000 }).catch(() => null);
      return r?.code === 0 ? r.stdout : null;
    });
    const text = home === null ? null : await ah().read(`${home}/install.json`).catch(() => null);
    const now = text == null ? null : rebuildOf(text);
    const held = shared(REBUILD_KEY) ?? null;
    const step = rebuildStep(held, now);
    if (step.keep !== held) setShared(REBUILD_KEY, step.keep);
    if (step.notice !== null) notify(rebuildNotify(step.notice, home));
    if (step.notice?.kind === "ready") checkUpdate(); // the mod update offer, held during the build, follows the ready notice
    return step.watch;
  }

  // ---- the desk: status segment and Needs-you drawer ----

  // The status copy is the desk's one poll: what it shows goes to Herdr's shared store for the drawer and the desk's tab row,
  // and only when it changed (a shared write redraws every mod).
  publishDesk() {
    const d = this.status === "up" ? { status: "up", ...deskModel(this.st.slices, Date.now()) } : { status: this.status, reason: this.reason };
    const key = JSON.stringify(d);
    if (key === this.deskKey) return;
    this.deskKey = key;
    setShared(DESK_KEY, d);
  }

  // Herdr's status bar, right of workspace-status: working, blocked, Needs you (the way into Q&A) and the newest event.
  // The working mark is the running dot: nothing in the bar animates.
  statusSegment(t) {
    const d = shared(DESK_KEY);
    const bar = h_flex().id("ah2-status-seg").size_full().pl(12).pr(8).gap(12).items_center().bg(t.chrome).border_l(1).border_color(t.border)
      .text_size(11.5).text_color(t.muted);
    if (d?.status !== "up") return bar.child(div().min_w(0).truncate().child(d?.status === "down" ? `Wrangl · ${d.reason}` : "Wrangl · connecting"));
    const stat = (state, n, word) => h_flex().flex_none().gap(6).items_center().child(markCell(t, state))
      .child(div().font_family(MONO).text_color(t.foreground).child(String(n))).child(word);
    const ev = d.newest;
    const full = ev ? `${ev.team} · ${ev.text} · ${age(Date.now() - ev.at)}` : "";
    return bar
      .when(d.working > 0, (el) => el.child(stat("running", d.working, "working")))
      .when(d.blocked > 0, (el) => el.child(stat("blocked", d.blocked, "blocked")))
      .when(d.needsYou > 0, (el) => el.child(this.needsButton(t, d.needsYou, t.chrome, d.blockers)))
      .child(h_flex().id("ah2-status-ev").flex_1().min_w(0).gap(6).items_center().justify_end()
        .when(Boolean(ev), (el) => el.tooltip(full).accessibility_label(`Newest: ${full}`)
          .child(div().flex_none().font_family(MONO).text_size(11).child(ev ? age(Date.now() - ev.at) : ""))
          .child(div().min_w(0).truncate().text_color(t.foreground).child(ev?.text ?? ""))));
  }

  // The floating drawer on the right edge (peek 44): a count you can see past the terminals, and on hover the teams that need you.
  // One fixed layout, never changed on hover (Herdr's rule); the edge side of each row carries what peeks. Hidden (nothing drawn)
  // with nothing needing you, ahd down, or Q&A showing (docked, or the Desk window's tab). A row choice hides it until the count changes or
  // that Q&A closes (atp-1704; herdr shuts a drawer only on hover leave, which a mod cannot force); so do its × and a press outside it
  // while it is open (b507). No Esc: the drawer never holds keyboard focus (herdr hands it back to the terminal).
  peek(t) {
    const d = shared(DESK_KEY);
    const qa = qaShowing(shownIn("qa"), deskWindow());
    if (d?.status === "up") this.dismissed = peekDismissed(this.dismissed, d.needsYou, qa);
    if (d?.status !== "up" || !(d.needsYou > 0) || qa || this.dismissed) return div();
    const p = placement();
    const right = p?.edge !== "left";
    const row = (id, label, edge, rest, tip) => Button.new(id)
      .w_full().h(36).px(8).gap(8).flex().items_center().rounded(6).cursor_pointer()
      .when(!right, (el) => el.flex_row_reverse())
      .border(1).border_color(t.popover).focus((s) => s.border_color(t.accent)).hover((el) => el.bg(t.hover))
      .tooltip(tip).accessibility_label(label)
      .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
      .on_click((_e, cx) => {
        this.dismissed = peekClosed(d.needsYou);
        openView("qa");
        cx.notify();
      })
      .child(div().flex_1().min_w(0).truncate().when(right, (el) => el.text_right()).child(rest))
      .child(edge);
    // a blocker among them: "n!" on a filled pill
    const badge = (n, size, blockers = 0) => div().size(size).flex_none().flex().items_center().justify_center().rounded(size / 2)
      .border(1.5).border_color(t.blocked).when(blockers > 0, (el) => el.bg(t.blocked)).font_family(MONO).text_size(size > 24 ? 12 : 10.5).font_semibold().text_color(t.foreground).child(needsMark(n, blockers));
    const blocking = (b) => (b > 0 ? `, ${b} blocking` : "");
    const close = Button.new("ah2-peek-close").size(24).flex().flex_none().items_center().justify_center().rounded(6).cursor_pointer()
      .text_color(t.muted).hover((el) => el.bg(t.hover).text_color(t.foreground))
      .tooltip("Close").accessibility_label("Close the Needs-you drawer")
      .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
      .on_click((_e, cx) => {
        this.dismissed = peekClosed(d.needsYou);
        cx.notify();
      })
      .child(svg("mods/ah2/x.svg").size(14));
    return v_flex().id("ah2-peek").w(240).py(2).gap(2).bg(t.popover).text_size(12).text_color(t.foreground)
      .on_mouse_down_out((_e, cx) => {
        const next = peekPressOutside(this.dismissed, placement()?.hovered === true, d.needsYou);
        if (next === this.dismissed) return;
        this.dismissed = next;
        cx.notify();
      })
      // the × on the side away from the edge, so the peek shows what it did
      .child(h_flex().w_full().gap(2).items_center().when(!right, (el) => el.flex_row_reverse()).child(close)
        .child(div().flex_1().min_w(0).child(row("ah2-peek-qa", `Needs you ${d.needsYou}${blocking(d.blockers)}${blockedText(d.blocked)}. Open Q&A`, badge(d.needsYou, 28, d.blockers),
          v_flex().child(div().font_semibold().child(`Needs you${blockedText(d.blocked)}`)).child(div().text_size(11).text_color(t.muted).child("Open Q&A")), "Open Q&A"))))
      .children(d.needing.map((tm) => row(`ah2-peek-${tm.id}`, `${tm.name}: ${tm.n} need you${blocking(tm.blockers)}${blockedText(tm.blocked)}. Open Q&A`,
        h_flex().w(28).flex_none().justify_center().child(badge(tm.n, 20, tm.blockers)), `${tm.name}${blockedText(tm.blocked)}`, `${tm.name} · ${tm.n} need you${blocking(tm.blockers)}${blockedText(tm.blocked)}`)));
  }

  // ---- shared pieces ----

  // Every "Needs you" counts waiting_on_you, what Q&A lists (atp-1724, atp-1736), as the web's; blocked rows show as "blocked".
  // A blocker among them marks the count "n!" on a filled pill; the served blocked members follow as " · B blocked" (atp-1749).
  needs(t, n, label = "Needs you", blockers = 0, blocked = 0) {
    return div().flex_none().h(18).px(6).rounded(9).border(1).border_color(t.blocked).when(blockers > 0, (el) => el.bg(t.blocked)).flex().items_center()
      .text_size(10.5).font_semibold().text_color(t.foreground).child(`${label} ${needsMark(n, blockers)}${blockedText(blocked)}`);
  }

  // The Needs-you badge as the way into Q&A (spec P3.3): a 24 px hit target around the 18 px pill.
  needsButton(t, n, rest, blockers = 0, blocked = 0) {
    return Button.new("ah2-needs").h(24).px(2).flex().flex_none().items_center().rounded(12).cursor_pointer()
      .border(1).border_color(rest).focus((s) => s.border_color(t.accent))
      .tooltip("Open Q&A")
      .accessibility_label(`Needs you ${n}${blockers > 0 ? `, ${blockers} blocking` : ""}${blockedText(blocked)}. Open Q&A`)
      .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
      .on_click(() => openView("qa"))
      .child(this.needs(t, n, "Needs you", blockers, blocked));
  }

  // A team's work root, for the Dismiss dialog's `ah` fallback (dismiss.js): the summary slice names it.
  workRootOf(id) {
    return this.st.slices.get(`team-summary:${id}`)?.body?.work_root ?? null;
  }

  closeButton(t, id, label, close) {
    if (isDocked()) return div(); // docked: the desk's tab row closes the column (tabs.js panelTabsView)
    return this.ring(t, Button.new(id), t.surface)
      .size(24).flex().flex_none().items_center().justify_center().rounded(6).cursor_pointer().text_color(t.muted)
      .hover((el) => el.bg(t.hover).text_color(t.foreground))
      .tooltip("Close (esc)")
      .accessibility_label(label)
      .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
      .on_click(close)
      .child(svg("mods/ah2/x.svg").size(14));
  }

  // Keyboard focus shows as an accent border; at rest the border is the background it sits on, so nothing moves.
  ring(t, el, rest) {
    return el.border(1).border_color(rest).focus((s) => s.border_color(t.accent));
  }

  // A member's state as Herdr's agents list marks it (marks.js).
  glyph(t, s) {
    return markCell(t, s.state);
  }

  note(t, text, detail) {
    return v_flex().px(8).py(6).gap(2)
      .child(div().text_size(12).text_color(t.muted).child(text))
      .when(Boolean(detail), (el) => el.child(div().text_size(11).text_color(t.muted).child(detail)));
  }

  // ---- sidebar ----

  sidebar(t) {
    const now = Date.now();
    const m = sidebarModel(this.st.slices, now);
    const set = settings();
    const teams = shownTeams(m.teams, set);
    const shown = (list) => list.reduce((n, x) => n + 1 + shown(x.lanes), 0);
    const hiddenN = m.count - shown(teams);
    const terminalOf = terminalFinder(terminals(), target());
    const order = [];
    const linked = new Set();
    const rows = [];

    const team = (tm, lane) => {
      const pad = lane ? 22 : 8;
      // ah2-53: a team with a goal of its own shows it as a second muted line under the name, cut by the ellipsis; the row grows to fit it
      const goalTip = tm.goal ? ` · goal: ${tm.goalFull}` : "";
      rows.push(this.ring(t, Button.new(`team-${tm.id}`), t.chrome)
        .when(!tm.goal, (el) => el.h(30)).when(Boolean(tm.goal), (el) => el.min_h(30).py(4))
        .pl(pad).pr(8).gap(4).flex().items_center().rounded(6).cursor_pointer()
        .text_size(11).font_semibold().text_color(t.muted)
        .hover((s) => s.bg(t.hover).text_color(t.foreground))
        .tooltip(`Open the ${tm.name} sitrep${counts(tm) ? ` · ${counts(tm)}` : ""}${goalTip}`)
        .accessibility_label(`${tm.name}${lane ? ", lane" : ""}. ${counts(tm) || "nothing running"}${goalTip}. Open its sitrep`)
        .on_click(() => openTeam(tm.id))
        .when(lane, (el) => el.child(div().flex_none().text_color(t.dim).child("↳")))
        // the name keeps its width: working/blocked shrinks first (its full text is in the label), needs you never does
        .child(tm.goal
          ? v_flex().flex_auto().min_w(0)
            .child(div().min_w(0).truncate().child(tm.name))
            .child(div().min_w(0).truncate().text_size(10.5).font_medium().text_color(t.muted).child(tm.goal))
          : div().flex_auto().min_w(0).truncate().child(tm.name))
        // needs you as a count pill, so the name keeps its room (the words are in the tooltip and the label)
        .when(tm.needsYou > 0, (el) => el.child(div().h(16).px(5).flex().flex_none().items_center().rounded(8).border(1).border_color(t.blocked)
          .font_family(MONO).text_size(10.5).font_medium().text_color(t.foreground).child(String(tm.needsYou))))
        .when(tm.working > 0 || tm.blocked > 0, (el) => el.child(div().flex_shrink(100).min_w(0).truncate().font_medium().text_color(t.muted)
          .child(counts({ working: tm.working, blocked: tm.blocked }))))
        // this team's sitrep as a pill on its row (atp-1712; the foot strip's all-teams Sitrep is gone)
        .child(this.ring(t, Button.new(`team-sitrep-${tm.id}`), t.border)
          .h(18).px(6).flex().flex_none().items_center().rounded(9).cursor_pointer()
          .text_size(10.5).font_medium().text_color(t.muted)
          .hover((s) => s.bg(t.raised).text_color(t.foreground))
          .tooltip(`Open the ${tm.name} sitrep`)
          .accessibility_label(`Open the ${tm.name} sitrep`)
          .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
          .on_click(() => openTeam(tm.id))
          .child("Sitrep"))
        .when(!lane, (el) => el.child(this.ring(t, Button.new(`team-dismiss-${tm.id}`), t.chrome)
          .size(18).flex().flex_none().items_center().justify_center().rounded(9).cursor_pointer().text_color(t.muted)
          .hover((s) => s.bg(t.raised).text_color(t.foreground))
          .tooltip(`Dismiss ${tm.name}`)
          .accessibility_label(`Dismiss team ${tm.name}`)
          .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
          .on_click(() => openDismiss(tm.id, tm.name, this.workRootOf(tm.id)))
          .child(svg("mods/ah2/x.svg").size(11))))
      );
      // Collapsed lanes keep their header row (and alerts); their members are not drawn.
      if (!(lane && set.lanes === "collapsed")) for (const mb of tm.members) rows.push(this.member(t, mb, pad + 6, terminalOf, order, linked));
      for (const a of tm.alerts) {
        rows.push(h_flex().id(`alert-${tm.id}-${rows.length}`).ml(pad + 26).mt(2).mb(4).mr(8).gap(4).items_center().text_size(11)
          .tooltip(a.text).accessibility_label(`Alert: ${a.text}`)
          .child(div().flex_none().text_color(t.blocked).child("⚠"))
          .child(div().flex_1().min_w(0).truncate().text_color(t.muted).child(a.text)));
      }
      for (const l of tm.lanes) team(l, true);
    };

    let body;
    if (this.status === "loading") body = [this.note(t, "Loading teams…")];
    else if (this.status === "down" && m.count === 0) body = [this.note(t, this.reason, "Retrying")];
    else if (m.count === 0) body = [this.note(t, "No teams running.")];
    else if (teams.length === 0) body = [this.note(t, `${m.count === 1 ? "The running team is" : `All ${m.count} running teams are`} hidden.`, "Show them in Wrangl settings (the … menu).")];
    else {
      teams.forEach((tm) => team(tm, false));
      body = rows;
    }

    // A hidden team's or collapsed lane's members are ah2's too: hiding them must not move them under Other agents.
    const claim = (list) => list.forEach((tm) => { tm.members.forEach((mb) => terminalOf(mb) && linked.add(terminalOf(mb))); claim(tm.lanes); });
    claim(m.teams);
    // Everything the built-in list showed stays: agents with no ah2 row go under Other agents.
    const others = agents().filter((a) => !linked.has(a.address));
    const otherRows = others.length === 0 || this.status === "loading" ? [] : [
      h_flex().h(30).px(8).items_center().text_size(11).font_semibold().text_color(t.muted).child("Other agents"),
      ...others.map((a) => {
        const state = AGENT_STATE[a.status] ?? "unknown";
        const s = { state, ...STATES[state] };
        order.push(a.address);
        const name = a.program?.name || a.title || a.address;
        return this.ring(t, Button.new(`other-${a.address}`), t.chrome)
          .h(30).pl(14).pr(8).gap(8).flex().items_center().rounded(6).cursor_pointer().text_size(12.5)
          .when(a.focused, (el) => el.bg(t.selected))
          .hover((el) => el.bg(t.hover))
          .tooltip(`${name} · ${a.space_name} · ${s.word}`)
          .accessibility_label(`${name} in ${a.space_name}, ${s.word}. Focus its terminal`)
          .on_click(() => run("pane.focus", { pane: a.pane }))
          .child(this.glyph(t, s))
          .child(div().w(86).flex_none().truncate().text_color(t.foreground).child(name))
          .child(div().flex_1().min_w(0).truncate().text_size(12).text_color(t.muted).child(a.space_name || ""));
      }),
    ];
    // only when the order changed: every render would otherwise send it again
    if (!this.sentOrder || this.sentOrder.length !== order.length || order.some((a, i) => a !== this.sentOrder[i])) {
      setAgentOrder(order);
      this.sentOrder = order;
    }

    const foot = this.status === "down" ? `${this.reason} · retrying`
      : this.status === "loading" ? "ahd · connecting"
      : `ahd · ${m.count} ${m.count === 1 ? "team" : "teams"}${hiddenN ? ` · ${hiddenN} hidden` : ""} · updated ${age(now - this.updated)}`;

    return v_flex().id("root").size_full().min_h(0).bg(t.chrome).border_t(1).border_color(t.border).text_size(13)
      .child(h_flex().h(41).pt(14).pb(5).pl(16).pr(8).gap(6).flex_none().items_center()
        .child(image("mods/ah2/wrangl-color.svg").size(14).flex_none())
        .child(div().flex_1().text_size(11.5).font_medium().text_color(t.muted).child("Wrangl"))
        .when(m.needsYou > 0, (el) => el.child(this.needsButton(t, m.needsYou, t.chrome, 0, m.blocked))))
      .child(v_flex().id("ah2-list").flex_1().min_h(0).px(8).overflow_y_scrollbar().children([...body, ...otherRows]))
      .child(h_flex().id("ah2-status").h(28).px(16).flex_none().items_center().border_t(1).border_color(t.border)
        .text_size(11).text_color(t.muted).child(div().truncate().child(foot)));
  }

  member(t, mb, pad, terminalOf, order, linked) {
    const address = terminalOf(mb);
    if (address && !linked.has(address)) {
      order.push(address);
      linked.add(address);
    }
    const quiet = mb.state === "idle" || mb.state === "gone" || mb.state === "unknown";
    const full = `${mb.name} · ${mb.word} · ${mb.what}${mb.age ? ` · ${mb.age}` : ""}`;
    // Every row has the ring border, linked or not, so their columns line up.
    const row = this.ring(t, address ? Button.new(`m-${mb.key}`) : h_flex().id(`m-${mb.key}`), t.chrome)
      .h(30).pl(pad).pr(8).gap(8).flex().items_center().rounded(6).text_size(12.5)
      .tooltip(full)
      .accessibility_label(address ? `${full}. Focus its terminal` : full)
      .child(this.glyph(t, mb))
      .child(div().w(86).flex_none().truncate().text_color(quiet ? t.muted : t.foreground).child(mb.name))
      .child(div().flex_1().min_w(0).truncate().text_size(12).text_color(t.muted).child(mb.what))
      .child(div().w(28).flex_none().text_right().font_family(MONO).text_size(11).text_color(t.muted).child(mb.age));
    return address
      ? row.cursor_pointer().hover((el) => el.bg(t.hover)).on_click(() => run("pane.focus", { terminal: address }))
      : row;
  }

  // ---- window overlay: one team's full sitrep ----

  sitrep(t) {
    const now = Date.now();
    const s = sitrepModel(this.st.slices, this.team, now, this.st.gone);
    const name = s?.name ?? this.team.split("/").pop();
    const close = () => closeOverlay(props().overlay);

    let body;
    if (s?.closed) body = [this.note(t, "Team closed.")];
    else if (!s && this.status === "down") body = [this.note(t, this.reason, "Retrying")];
    else if (!s) body = [this.note(t, "Loading sitrep…")];
    else body = s.regions.map((r) => this.region(t, r));

    return v_flex().id("root").size_full().min_h(0).bg(t.surface).text_size(12.5)
      .track_focus(this.keys)
      .on_key_down((e, _cx) => {
        if (e.keystroke === "escape") close();
      })
      .child(h_flex().h(44).px(16).gap(10).flex_none().items_center().border_b(1).border_color(t.border)
        .child(div().min_w(0).truncate().text_size(13).font_semibold().text_color(t.foreground).child(`${name} · sitrep`))
        .when((s?.needsYou ?? 0) > 0, (el) => el.child(this.needs(t, s.needsYou, "Needs you", 0, s.blocked)))
        .child(div().flex_1())
        .when(Boolean(s) && !s.closed && !s.lane, (el) => el.child(this.ring(t, Button.new("ah2-sitrep-dismiss"), t.surface)
          .h(24).px(8).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(12).text_color(t.muted)
          .hover((x) => x.bg(t.hover).text_color(t.foreground))
          .tooltip(`Dismiss ${name}`)
          .accessibility_label(`Dismiss team ${name}`)
          .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
          .on_click(() => openDismiss(this.team, name, this.workRootOf(this.team)))
          .child("Dismiss")))
        .child(this.closeButton(t, "ah2-sitrep-close", "Close the sitrep", close)))
      .child(v_flex().id("ov-body").flex_1().min_h(0).px(16).pb(12).overflow_y_scrollbar().on_mouse_down("left", (_e, cx) => cx.stop_propagation()).children(body)); // the header drags; the body selects
  }

  region(t, r, top = 12) {
    return v_flex().mt(top).flex_none()
      .child(h_flex().h(20).gap(6).items_center().text_size(11).font_semibold().text_color(t.muted)
        .child(r.label).child(div().font_family(MONO).font_medium().text_color(t.muted).child(String(r.total))))
      .when(r.rows.length === 0, (el) => el.child(div().py(4).text_size(11).text_color(t.muted).child("None")))
      .children(r.rows.map((x, i) => (x.question ? this.question(t, r, x, i) : this.item(t, r, x, i))))
      .when(r.more > 0, (el) => el.child(div().py(4).text_size(11).text_color(t.muted).child(`+ ${r.more} more`)));
  }

  item(t, r, x, i) {
    return h_flex().id(`it-${r.list}-${i}`).min_h(28).py(4).gap(8).items_center().border_b(1).border_color(t.border)
      .tooltip(`${x.title}${x.who ? ` · ${x.who}` : ""}`)
      .child(div().w(64).flex_none().truncate().font_family(MONO).text_size(11).text_color(x.done ? t.done : t.muted).child(x.key))
      .child(div().flex_1().min_w(0).truncate().text_color(t.foreground).child(x.title))
      .child(div().flex_none().text_size(11).text_color(t.muted).child(x.who));
  }

  // A decision waiting on the person. It is answered in the Q&A window; the card itself has no action, only the link.
  question(t, r, x, i) {
    return v_flex().id(`q-${r.list}-${i}`).mt(4).px(12).py(10).gap(4).rounded(8).bg(t.raised)
      .child(div().text_color(t.foreground).child(x.title))
      .child(h_flex().gap(8).items_center()
        .child(div().flex_1().min_w(0).truncate().text_size(11).text_color(t.muted).child([x.key, x.who].filter(Boolean).join(" · ")))
        .child(this.ring(t, Button.new(`q-${r.list}-${i}-qa`), t.raised)
          .h(24).px(6).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(11).text_color(t.muted)
          .hover((el) => el.bg(t.hover).text_color(t.foreground))
          .accessibility_label(`Answer ${x.key ?? "this question"} in Q&A`)
          .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
          .on_click(() => openView("qa"))
          .child("Answer in Q&A")));
  }
}
