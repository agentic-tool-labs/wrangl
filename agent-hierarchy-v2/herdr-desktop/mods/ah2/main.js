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
import { v_flex, h_flex, Button, Popover } from "gpui-base";
import { readTextFile, theme, run, props, agents, terminals, setAgentOrder, closeOverlay, notify, shared, setShared, placement, checking, nextCommand } from "herdr";
import { REBUILD_KEY, REBUILD_EVERY_MS, REBUILD_WATCH_MS, rebuildStep, rebuildGaveUp, rebuildNotify } from "./rebuild-model.js";
import { STATES, applyReply, age, counts, deskModel, needsMark, blockedText, sidebarModel, sitrepModel, qaModel, qaSubs, loadingOf, terminalFinder, STUCK, retryMs, activitySubs, dotSubs, rowTrack, paneTree, teamTotals } from "./model.js";
import { runText, underTip } from "./flow-model.js";
import { qaInit, qaPrune, qaTake, qaView, qaScope } from "./qa.js";
import { boardSubs } from "./board-model.js";
import { boardInit, boardLeave, boardView } from "./board.js";
import { feedSubs } from "./feed-model.js";
import { feedView } from "./feed.js";
import { graphSubs, leaveGraph, rowMark, roleHue, laneHue, roleIcon, dotColor, dotAt, trackOf, travelsOf, travelClock, DOT_MS } from "./graph-model.js";
import { graphView, graphInit, light as isLight } from "./graph-view.js";
import { tabsView, tabsInit, menuView, panelTabsView } from "./tabs.js";
import { openView, closeOf, shownIn, isDocked, deskWindow, teamDesk, setTeamDesk, openTeamDesk } from "./dock.js";
import { teamLabel, realNameNote } from "./select.js";
import { markCell } from "./marks.js";
import { shownTeams, displayOrder, teamMove, newAlerts, DESK_KEY, DESK_WINDOW_KEY, TEAM_DESKS_KEY, DOCKABLE, peekDismissed, peekClosed, peekPressOutside, unionSubs, qaShowing, isInstallCommand, listen } from "./settings-model.js";
import { settings, setSetting, settingsView, checkUpdate, installClick, readRebuild } from "./settings.js";
import { openDismiss, dismissInit, dismissView } from "./dismiss.js";
import { openRename, renameInit, renameView } from "./rename.js";
import { openUpdate, updateOpen, updateInit, updateClose, updateView } from "./update.js";
import { ah, target } from "./ah.js";
import { UNRESOLVED, targetKey } from "./target-model.js";
import { Link } from "./link-model.js";

const WAIT_S = "25"; // fits inside the 35 s exec timeout fetch passes
const BACKOFF_MS = [2000, 4000, 8000, 16000, 30000];
const MONO = "Geist Mono"; // ships with Herdr; gives ages and counts even digit widths (GPUI has no tabular-nums)
const AGENT_STATE = { working: "working", blocked: "blocked", done: "done", idle: "idle" };
// ah2-207 D2/D3: the tree's indent step (one connector column, and the disclosure's column); the roles' icons are graph-model.js
// roleIcon. The folds live in Herdr's shared store, keyed "<team id>|<node key>".
const STEP = 16;
const TREE_KEY = "ah2-sidebar-tree";

export default class Ah2 extends View {
  init(_props, cx) {
    // A click on the "available" notice (settings-model.js updateNotify) arrives as a command. Which copy herdr hands it to is
    // not documented, so every copy listens; installClick installs once however many receive it.
    // ah2-401 I1: a failed nextCommand() or click does not end the listener (settings-model.js listen).
    // The mod has nowhere to log, so a failure is swallowed (no console, no new notice) and the listener keeps listening.
    if (!checking()) cx.spawn((cx) => listen(nextCommand, (c) => (isInstallCommand(c) ? installClick() : null), (ms) => cx.sleep(ms), () => {}));
    this.team = props()?.team ?? null;
    this.view = props()?.view ?? "sitrep";
    // The Desk window: one copy for all four tabs; `view` is the tab shown (tabs.js draws its header, select switches it).
    this.desk = this.view === "desk";
    if (this.desk) this.view = this.deskTabs().includes(props()?.tab) ? props().tab : "qa";
    if (this.desk) this.max = props()?.max === true; // opened maximized (dock.js resizeDeskWindow)
    if (this.view === "tabs") return tabsInit(cx); // the sidebar's tab strip (tabs.js): the default tab and the update check, no poll
    if (this.view === "panel-tabs") return; // the desk's tab row reads DESK_KEY; no poll
    if (this.view === "peek") { // reads DESK_KEY; no poll. DESK_WINDOW_KEY is persisted: a stale window id would hide the drawer for good
      // ponytail: assumes no Desk window is open when the drawer's copy starts (a layout switch closes windows)
      const w = deskWindow();
      if (w?.overlay != null) setShared(DESK_WINDOW_KEY, { view: w.view, max: w.max });
      // and the team Desks' (desk-per-team D3): every entry drops its `overlay`, on the same assumption
      const desks = shared(TEAM_DESKS_KEY);
      if (desks && Object.values(desks).some((d) => d?.overlay != null))
        setShared(TEAM_DESKS_KEY, Object.fromEntries(Object.entries(desks).map(([id, d]) => [id, { view: d?.view, max: d?.max }])));
      return;
    }
    if (this.view === "dismiss") return dismissInit(this, cx); // the Dismiss dialog (dismiss.js): its own bounded task, no poll
    if (this.view === "rename") return renameInit(this, cx); // the Rename dialog (rename.js): its field and focus, no poll
    if (this.view === "update") return updateInit(this, cx); // the update dialog (update.js): its own re-read, no poll
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
    if (this.desk || this.view === "board") boardInit(this);
    if (this.desk || this.view === "graph") graphInit(this, cx); // the shown team, the scrubber and the zoom (graph-view.js); once, never per switch
    // The Desk window says it is open, and on which tab (cleared in close): the peek drawer and every opener read it.
    // A team Desk (props.team, desk-per-team D1) writes its own entry in TEAM_DESKS_KEY instead (deskRec, setDeskRec).
    if (this.desk) this.setDeskRec({ overlay: (this.overlay = props()?.overlay ?? null), view: this.view, max: this.max });
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
    this.ageGen = 0; // the age clock: the tree model (model.js paneTree) is rebuilt on each turn, so its ages stay current
    cx.timer.every(30000, (cx) => { this.ageGen++; cx.notify(); }); // ages only; data redraws come from the poll
    cx.spawn((cx) => this.poll(cx));
  }

  // Herdr calls this when the copy goes (an overlay closed, or quit). Only our own id: reopening the same key replaces the window.
  async close() {
    this.gone = true; // ends the rebuild watch (rebuild)
    if (this.view === "update") updateClose(this); // its claim goes with it (update.js)
    if (this.view === "board") boardLeave(this); // the Board's visit ends: store what it saw (board.js)
    this.graphAnim?.cancel(); // an eased Graph zoom (graph-view.js easeTo) leaves no task behind
    this.graphTravel?.cancel(); // a brief's dot in travel (graph-model.js travelClock) leaves no task behind
    this.treeTravel?.graphTravel?.cancel(); // and the tree's (ah2-239), on its own state
    if (this.overlay != null && this.deskRec()?.overlay === this.overlay) this.setDeskRec({ view: this.view, max: this.max }); // its last tab and size
  }

  // The Desk window's tabs: a team's Desk adds Sitrep first (desk-per-team D6); the all-teams one keeps DOCKABLE, so any other
  // tab (Sitrep too) falls back to Q&A and an odd `want` is dropped.
  deskTabs() {
    return this.team ? ["sitrep", ...DOCKABLE] : DOCKABLE;
  }

  // The Desk window's open record: DESK_WINDOW_KEY for the all-teams Desk, its own TEAM_DESKS_KEY entry for a team's (D3).
  deskRec() {
    return this.team ? teamDesk(this.team) : deskWindow();
  }
  setDeskRec(rec) {
    if (this.team) setTeamDesk(this.team, rec);
    else setShared(DESK_WINDOW_KEY, rec);
  }

  // The Desk window's tab: switch it, tell the key (without any `want`), and on to Q&A take what the list holds (qa.js qaTake), as a
  // Q&A copy does after a reply. Leaving the Feed makes the next visit a new look (feed.js feedShowing).
  select(view, cx) {
    if (view !== this.view) {
      if (this.view === "feed") this.feedShowing = false;
      if (this.view === "board") boardLeave(this); // leaving the Board ends its visit (board.js)
      if (this.view === "graph") leaveGraph(this); // the dot clock stops and the agent sheet closes (graph-model.js)
      this.view = view;
      if (view === "qa") qaTake(this, undefined, cx);
      cx.notify();
    }
    this.setDeskRec({ overlay: this.overlay, view, max: this.max });
  }

  // The Q&A window adds one questions: slice per running team (spec P3.1); the sidebar copy stays on view:summary. The Desk window
  // asks for every tab's at once (one long-poll cannot be cut short, so a switch must not wait on a new one).
  subs() {
    if (this.desk) {
      const s = this.st.slices;
      // a team's Desk also asks for its Sitrep tab's two slices (D6)
      return unionSubs(["view:summary"], this.team ? [`team-summary:${this.team}`, `sitrep:${this.team}`] : [], qaSubs(s), boardSubs(s, Date.now(), { open: this.kb?.open, log: this.view === "board" ? this.kb?.sel?.log : null, shown: this.view === "board", team: this.kb?.team, hide: this.kb?.hide, member: this.view === "board" ? this.kb?.sel : null }), feedSubs(s), graphSubs(s, this));
    }
    if (this.view === "qa") return ["view:summary", ...qaSubs(this.st.slices)];
    if (this.view === "board") return ["view:summary", ...boardSubs(this.st.slices, Date.now(), { open: this.kb?.open, log: this.kb?.sel?.log, shown: true, team: this.kb?.team, hide: this.kb?.hide, member: this.kb?.sel })];
    if (this.view === "feed") return ["view:summary", ...feedSubs(this.st.slices)];
    if (this.view === "graph") return ["view:summary", ...graphSubs(this.st.slices, this)];
    return this.team ? [`team-summary:${this.team}`, `sitrep:${this.team}`] : ["view:summary", ...activitySubs(this.st.slices), ...dotSubs(this.st.slices)];
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
    this.renders = (this.renders ?? 0) + 1; // graph-view.js grip: a held drag ends if its stage missed a render (b93)
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
    if (this.view === "graph") return graphView(this, t, closeOf(), cx);
    if (this.view === "settings") return settingsView(this, t, () => closeOverlay(props().overlay));
    if (this.view === "dismiss") return dismissView(this, t);
    if (this.view === "rename") return renameView(this, t);
    if (this.view === "update") return updateView(this, t);
    return this.team ? this.sitrep(t) : this.sidebar(t, cx);
  }

  // The Desk window: the Desk's tab row, then the shown tab's view; × and Esc close the window. An opener elsewhere asks for a
  // tab with `want` in the key (dock.js): taken once, after this render (a shared write redraws every mod, this one too), with
  // the keyboard.
  drawDesk(t, cx) {
    const w = this.deskRec();
    if (w?.want && w.overlay === this.overlay && !this.wanting) {
      this.wanting = true;
      cx.spawn(async (cx) => {
        await cx.sleep(0);
        this.wanting = false;
        const want = this.deskRec()?.want;
        if (this.deskTabs().includes(want)) this.select(want, cx);
        else this.select(this.view, cx); // an odd `want` is dropped, not retried
        this.keys.focus();
      });
    }
    const close = () => closeOverlay(props().overlay);
    const body = { sitrep: (v, t, close) => v.sitrep(t, close), qa: qaView, board: boardView, feed: feedView, graph: graphView }[this.view];
    return v_flex().id("ah2-desk-window").size_full().min_h(0).bg(t.surface)
      .child(panelTabsView(t, { view: this.view, select: (view, cx) => this.select(view, cx), close, max: this.max, ...this.deskTeam() }))
      // a press in the body stays in the body (text selection); only the tab row above drags the window
      .child(div().id(`ah2-desk-${this.view}-body`).flex_1().min_h(0).on_mouse_down("left", (_e, cx) => cx.stop_propagation()).child(body(this, t, close, cx)));
  }

  // A team Desk's tab row (tabs.js panelTabsView, D5): its team, the display name and the real one, as the sitrep title reads them.
  deskTeam() {
    if (!this.team) return {};
    const sum = this.st.slices.get(`team-summary:${this.team}`)?.body ?? null;
    // `waiting`: the Q&A tab's count, this Desk's own scope's, not DESK_KEY's all-teams one (desk-per-team Amendment 2)
    return { team: this.team, name: sum ? teamLabel(sum) : this.team.split("/").pop(), real: sum ? realNameNote(sum) : null,
      waiting: qaModel(this.st.slices, Date.now(), qaScope(this)).count };
  }

  // The sidebar copy only: alarms not seen before become herdr notices when the setting is on. The first reply only seeds.
  alarms() {
    const { fresh, seen } = newAlerts(sidebarModel(this.st.slices, Date.now()).teams, this.seenAlerts ?? null);
    this.seenAlerts = seen;
    if (!settings().notify) return;
    for (const x of fresh.slice(0, 3)) {
      notify({ title: `Wrangl alarm · ${x.team.label ?? x.team.name}`, body: x.alert.text, level: "warning", action: { name: "layout.switch", args: { name: "wrangl" } } });
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
  // asks again); the stamp is then read as a file, no process. No home or no read says nothing.
  async rebuildRead() {
    this.ahHome ??= { key: null, home: null };
    const { home, now } = await readRebuild(this.ahHome);
    const held = shared(REBUILD_KEY) ?? null;
    const step = rebuildStep(held, now);
    if (step.keep !== held) setShared(REBUILD_KEY, step.keep);
    // ah2-192: a build start opens the update dialog instead of its notice; "ready" is not posted while the dialog shows it; a
    // failure is posted as well, in case the dialog was closed
    const inDialog = step.notice?.kind === "building" && openUpdate(); // false: a claim with no dialog here, so the notice still goes
    if (!inDialog && step.notice !== null && !(step.notice.kind === "ready" && updateOpen())) notify(rebuildNotify(step.notice, home));
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

  sidebar(t, cx) {
    const now = Date.now();
    const set = settings();
    const folds = shared(TREE_KEY) ?? {};
    // ah2-252: the model is built once per change of its inputs (model.js paneTree); a dot frame reuses it and computes only dotAt
    // (the kit consumes elements each render, gpui-kit.d.ts Element, so the rows themselves are rebuilt)
    // a node starts open, a lane closed under the Lanes: Collapsed setting, until its disclosure is used (kept per team and node)
    const { m, teams, trees } = paneTree(this.treeMemo ??= {}, [this.sliceGen, this.ageGen, JSON.stringify(folds), set.lanes, set.hidden.join("\n"), set.pinned.join("\n"), set.order.join("\n")], this.st.slices, now, {
      shown: (list) => shownTeams(displayOrder(list, set), set),
      open: (tm) => (n) => folds[`${tm.id}|${n.key}`] ?? !(n.kind === "lane" && set.lanes === "collapsed"),
      track: (r, back) => trackOf(rowTrack(r, STEP), back),
    });
    const treeOf = new Map(trees.map((x) => [x.tm, x.rows]));
    // ah2-239: the briefs in flight to the drawn rows, each a dot on its row's elbow; the Graph's clock function on the tree's own state
    const travel = (this.treeTravel ??= {});
    const dots = [];
    const shown = (list) => list.reduce((n, x) => n + 1 + shown(x.lanes), 0);
    const hiddenN = m.count - shown(teams);
    const terminalOf = terminalFinder(terminals(), target());
    const order = [];
    const linked = new Set();
    const rows = [];

    // ah2-207: each team draws its header, then its tree (model.js teamTree, treeRows); a lane's header is a node of its parent's tree.
    const lt = isLight(t.surface);
    // ah2-326/325: a top-level team's pin, place and collapse (settings-model.js teamMove; the fold under `${id}|@team`, false = collapsed)
    const collapsedOf = (tm) => folds[`${tm.id}|@team`] === false;
    const fold = (tm, cx) => {
      setShared(TREE_KEY, { ...(shared(TREE_KEY) ?? {}), [`${tm.id}|@team`]: collapsedOf(tm) });
      cx.notify();
    };
    const menu = (tm) => {
      const item = (key, label, a11y, choose) => this.ring(t, Button.new(`team-menu-${key}-${tm.id}`), t.popover)
        .h(26).px(8).flex().items_center().rounded(6).cursor_pointer().text_size(12).text_color(t.foreground)
        .hover((s) => s.bg(t.hover))
        .accessibility_label(a11y)
        .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
        .on_click((_e, cx) => {
          this.teamMenu = null;
          choose(cx);
        })
        .child(label);
      const move = (action) => teamMove(m.teams, set, tm.id, action);
      const items = [
        ...[["pin", "Pin to top", `Pin ${tm.label} to the top`], ["unpin", "Unpin", `Unpin ${tm.label}`], ["up", "Move up", `Move ${tm.label} up`], ["down", "Move down", `Move ${tm.label} down`]]
          .map(([a, label, a11y]) => [move(a), a, label, a11y]).filter(([p]) => p)
          .map(([p, a, label, a11y]) => item(a, label, a11y, (cx) => setSetting(p, cx))),
        collapsedOf(tm) ? item("expand", "Expand", `Expand ${tm.label}`, (cx) => fold(tm, cx)) : item("collapse", "Collapse", `Collapse ${tm.label}`, (cx) => fold(tm, cx)),
        // team-order Amendment 4: the header's × moved here; it opens the Dismiss dialog (dismiss.js), the confirm it always had
        item("dismiss", "Dismiss", `Dismiss team ${tm.label}`, (cx) => {
          openDismiss(tm.id, tm.label, this.workRootOf(tm.id), tm.parent ? tm.name : null);
          cx.notify();
        }),
      ];
      return Popover.new(`team-menu-${tm.id}`).open(this.teamMenu === tm.id)
        .on_open_change((open, cx) => {
          this.teamMenu = open ? tm.id : null;
          cx.notify();
        })
        .trigger(this.ring(t, Button.new(`team-options-${tm.id}`), t.chrome)
          .size(18).flex().flex_none().items_center().justify_center().rounded(9).cursor_pointer().text_color(t.muted)
          .hover((s) => s.bg(t.raised).text_color(t.foreground))
          .tooltip("Team options")
          .accessibility_label(`Team options for ${tm.label}`)
          // no stop_propagation here: the Popover's own press handler sits above the trigger, opens the menu and stops the press itself
          .child(svg("mods/ah2/ellipsis.svg").size(12)))
        .content(v_flex().id(`team-menu-list-${tm.id}`).p(4).gap(2).min_w(140).rounded(8).border(1).border_color(t.border).bg(t.popover).shadow_md()
          .children(items));
    };
    const header = (tm, lane, lead) => {
      const collapsed = !lane && collapsedOf(tm);
      const pinned = !lane && set.pinned.includes(tm.id);
      // ah2-325 D8: collapsed, the counts are the team's and its lanes', so nothing folded away is lost
      const c = collapsed ? { ...tm, ...teamTotals(tm) } : tm;
      // ah2-53: a team with a goal of its own shows it as a second muted line under the name, cut by the ellipsis; the row grows to fit it
      const goalTip = tm.goal ? ` · goal: ${tm.goalFull}` : "";
      // ah2-218: shown by its display name; the real name, which commands and addresses use, is in the tooltip
      const realTip = tm.real ? ` · ${tm.real}` : "";
      const pinTip = pinned ? ", pinned" : "";
      const name = (el) => (pinned ? h_flex().flex_auto().min_w(0).gap(4).items_center().child(svg("mods/ah2/pin.svg").size(11).flex_none().text_color(t.muted)).child(el.flex_1()) : el);
      // the row itself opens nothing, so it is no button: no pointer, no hover, no focus stop; the Sitrep pill opens the sitrep
      // (team-order Amendment 2). The ring border stays so its columns line up with the rows that are buttons.
      const row = this.ring(t, h_flex().id(`team-${tm.id}`), t.chrome)
        .when(!tm.goal, (el) => el.h(30)).when(Boolean(tm.goal), (el) => el.min_h(30).py(4))
        .when(lane, (el) => el.flex_1().min_w(0).pl(4)).when(!lane, (el) => el.pl(2))
        .pr(8).gap(4).flex().items_center().rounded(6)
        .text_size(11).font_semibold().text_color(t.muted)
        .tooltip(`${tm.label}${pinTip}${realTip}${counts(c) ? ` · ${counts(c)}` : ""}${goalTip}`)
        .accessibility_label(`${tm.label}${lane ? ", lane" : ""}${pinTip}. ${counts(c) || "nothing running"}${goalTip}`)
        // the disclosure: collapse the team to this header line, or expand it
        .when(!lane, (el) => el.child(Button.new(`team-fold-${tm.id}`).w(16).h(24).flex().flex_none().items_center().justify_center().rounded(4).cursor_pointer()
          .text_size(10).text_color(t.muted).border(1).border_color(t.chrome).focus((s) => s.border_color(t.accent))
          .hover((s) => s.bg(t.raised).text_color(t.foreground))
          .tooltip(collapsed ? "Expand" : "Collapse")
          .accessibility_label(`${collapsed ? "Expand" : "Collapse"} ${tm.label}`)
          .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
          .on_click((_e, cx) => fold(tm, cx))
          .child(collapsed ? "▸" : "▾")))
        // the name keeps its width: working/blocked shrinks first (its full text is in the label), needs you never does
        .child(name(tm.goal
          ? v_flex().flex_auto().min_w(0)
            .child(div().min_w(0).truncate().child(tm.label))
            .child(div().min_w(0).truncate().text_size(10.5).font_medium().text_color(t.muted).child(tm.goal))
          : div().flex_auto().min_w(0).truncate().child(tm.label)))
        // needs you as a count pill, so the name keeps its room (the words are in the tooltip and the label)
        .when(c.needsYou > 0, (el) => el.child(div().h(16).px(5).flex().flex_none().items_center().rounded(8).border(1).border_color(t.blocked)
          .font_family(MONO).text_size(10.5).font_medium().text_color(t.foreground).child(String(c.needsYou))))
        .when(c.working > 0 || c.blocked > 0, (el) => el.child(div().flex_shrink(100).min_w(0).truncate().font_medium().text_color(t.muted)
          .child(counts({ working: c.working, blocked: c.blocked }))))
        // this team's own Desk, on its Sitrep tab (desk-per-team D9; was the atp-1712 Sitrep pill), drawn as the ⋯ button beside it
        .child(this.ring(t, Button.new(`team-sitrep-${tm.id}`), t.chrome)
          .size(18).flex().flex_none().items_center().justify_center().rounded(9).cursor_pointer().text_color(t.muted)
          .hover((s) => s.bg(t.raised).text_color(t.foreground))
          .tooltip(`Open the ${tm.label} desk`)
          .accessibility_label(`Open the ${tm.label} desk, on its sitrep`)
          .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
          .on_click(() => openTeamDesk(tm.id, "sitrep"))
          .child(svg("mods/ah2/app-window.svg").size(12)))
        .when(!lane, (el) => el.child(menu(tm)));
      return lead ? div().flex().w_full().gap(4).child(lead).child(row) : row;
    };
    // r: the tree row the alert sits under, whose lines run on through it; null under a team's header
    const alert = (id, a, r) => {
      const el = h_flex().id(`alert-${id}-${rows.length}`).flex_1().min_w(0).ml(r ? 5 : 34).mt(2).mb(4).mr(8).gap(4).items_center().text_size(11)
        .tooltip(a.text).accessibility_label(`Alert: ${a.text}`)
        .child(div().flex_none().text_color(t.blocked).child("⚠"))
        .child(div().flex_1().min_w(0).truncate().text_color(t.muted).child(a.text));
      // always inside a row: bare in the list's column, flex_1 would grow it down the pane and leave a tall gap
      return r ? div().flex().w_full().gap(4).child(this.treeLead(t, r, null)).child(el) : div().flex().w_full().child(el);
    };
    // A team's own alerts sit under its header, so they never read as a member's (ah2-133 D4); so does a member alert whose member has no row.
    // team-order Amendment 3: the served "Waiting on you · N" alert (kind needs_you) is not drawn; the header's count pill says it
    const drawn = (a) => a.kind !== "needs_you";
    const teamAlerts = (tm, r) => {
      const keys = new Set(tm.members.map((mb) => mb.key));
      for (const a of tm.alerts) if (drawn(a) && (!a.member || !keys.has(a.member))) rows.push(alert(tm.id, a, r));
    };
    // A folded node's hidden members keep their alerts in sight, under it (a collapsed lane always kept them).
    const hidden = (n) => n.children.flatMap((c) => [...(c.alerts ?? []), ...(c.kind === "lane" ? c.team.alerts.filter((a) => !a.member && drawn(a)) : []), ...hidden(c)]);
    // ah2-325 D8: a collapsed team keeps every alert of its own, its members' and its lanes' under its header
    const allAlerts = (tm) => [...tm.alerts.filter(drawn), ...tm.lanes.flatMap(allAlerts)];
    const team = (tm) => {
      rows.push(header(tm, false, null));
      if (collapsedOf(tm)) {
        for (const a of allAlerts(tm)) rows.push(alert(tm.id, a, null));
        return;
      }
      teamAlerts(tm, null);
      for (const { r, d, pts, b, bpts, parent } of treeOf.get(tm)) {
        const n = r.node;
        if (d) dots.push(d);
        if (b) dots.push(b);
        // ah2-265: the brief coming in (D9: the receiver's lane colour), then the report going back up the same track at the review pace
        // in the member's lane colour, started half a period on (D4) so the two never ride at one phase
        const marks = [
          d && { hue: d.review ? dotColor(true, lt) : laneHue(d.role, d.column, lt) ?? t.foreground, at: dotAt(pts, travel.graphTravels?.get(d.key) ?? now, now, d.review) },
          b && { hue: laneHue(b.role, b.column, lt) ?? t.foreground, at: dotAt(bpts, (travel.graphTravels?.get(b.key) ?? now) - DOT_MS.review / 2, now, true) },
        ].filter(Boolean);
        const lead = this.treeLead(t, r, `${tm.id}|${n.key}`, n.kind === "lane" ? n.team.name : n.label, marks);
        if (n.kind === "lane") {
          rows.push(header(n.team, true, lead));
          teamAlerts(n.team, r);
        } else if (n.item) {
          rows.push(this.under(t, n.item, lead, parent, lt, now));
        } else {
          rows.push(this.member(t, n, lead, lt, terminalOf, order, linked));
          for (const a of n.alerts) rows.push(alert(tm.id, a, r));
        }
        if (r.kids && !r.open) for (const a of hidden(n)) rows.push(alert(tm.id, a, r));
      }
    };

    let body;
    if (this.status === "loading") body = [this.note(t, "Loading teams…")];
    else if (this.status === "down" && m.count === 0) body = [this.note(t, this.reason, "Retrying")];
    else if (m.count === 0) body = [this.note(t, "No teams running.")];
    else if (teams.length === 0) body = [this.note(t, `${m.count === 1 ? "The running team is" : `All ${m.count} running teams are`} hidden.`, "Show them in Wrangl settings (the … menu).")];
    else {
      teams.forEach((tm) => team(tm));
      body = rows;
    }

    // the clock runs only while a drawn row has a dot (graph-model.js travelClock: and ends a second after the pane stops drawing)
    travelClock(travel, cx, travelsOf(travel, dots, now), 33);

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

  // A tree row's lead (ah2-207 D2): a column per level, each with its ancestor's rail where that runs on, the row's own elbow from its
  // parent's line, then the disclosure (a row with children) or, at a leaf, the elbow carried through to the icon. Lines in theme().border.
  // The lead stretches to its row's height, so the lines run on unbroken. The disclosure is 16 x 24, its centre on the line to its
  // children, and stands clear of the row's own target (WCAG 2.2 target spacing). `fold` null: the lead of an alert under the row,
  // only the lines that run on through it (the row's own, and the one down to its children when they show).
  // ah2-239: `dots` [{ hue, at }], a brief in flight to the row and (ah2-265) a report going back: a 5 px dot at each `at` on the row's
  // elbow, the elbow in the first one's hue at the Graph's arc strength; the rails past other rows stay t.border.
  treeLead(t, r, fold, name = "", dots = []) {
    const dot = dots[0] ?? null;
    const line = () => div().absolute().bg(t.border);
    const deep = r.node.depth > 0;
    const cols = fold === null
      ? [...(deep ? [...r.rails, !r.last] : []), r.open].map((on) => [on, false])
      : deep ? [...r.rails.map((on) => [on, false]), [!r.last, true]] : [];
    // ah2-225 B3: a subagent's own elbow is dashed; the lines that run on past it stay solid
    const dash = fold !== null && r.node.kind === "subagent";
    const seg = (el, across) => (dash ? el.border_dashed().border_color(t.border).when(across, (e) => e.border_t(1)).when(!across, (e) => e.border_l(1))
      : dot ? el.bg(dot.hue).opacity(isLight(t.surface) ? 0.75 : 0.6) : el.bg(t.border));
    const piece = () => div().absolute();
    const col = ([down, elbow]) => div().relative().w(STEP).flex_none()
      .when(down, (el) => el.child(line().left(7).top(0).w(1).h_full()))
      .when(elbow, (el) => el.child(seg(piece().left(7).top(0).w(1).h(15), false)).child(seg(piece().left(7).top(14).w(STEP - 7).h(1), true)));
    const end = fold === null ? [] : [r.kids
      ? div().w(STEP).flex_none().flex().flex_col().pt(3)
        .child(Button.new(`fold-${fold}`).w(STEP).h(24).flex().flex_none().items_center().justify_center().rounded(4).cursor_pointer()
          .text_size(10).text_color(t.muted).border(1).border_color(t.chrome).focus((s) => s.border_color(t.accent))
          .hover((el) => el.bg(t.hover).text_color(t.foreground))
          .tooltip(r.open ? "Collapse" : "Expand")
          .accessibility_label(`${r.open ? "Collapse" : "Expand"} ${name}`)
          .on_click((_e, cx) => {
            // ponytail: folds of ended teams stay in the store; prune by the running teams if it ever matters
            setShared(TREE_KEY, { ...(shared(TREE_KEY) ?? {}), [fold]: !r.open });
            cx.notify();
          })
          .child(r.open ? "▾" : "▸"))
        // the line down to its children, under the disclosure, to the row's foot
        .when(r.open, (el) => el.child(div().flex_1().ml(7).w(1).bg(t.border)))
      : div().relative().w(STEP).flex_none().when(deep, (el) => el.child(seg(piece().left(0).top(14).w(STEP - 2).h(1), true)))];
    const mark = dots.map((m) => div().absolute().left(m.at.x - 2.5).top(m.at.y - 2.5).size(5).rounded(3).bg(m.hue));
    return div().relative().flex().flex_none().children([...cols.map(col), ...end, ...mark]);
  }

  // A member's row (ah2-207 D3/D4): its role's icon in its Flow lane's colour (ah2-265 D6, graph-model.js laneHue), drawn at the Graph's
  // gone strength when gone or with no signal (D8), its state's glyph and mark as the Graph gives them (graph-model.js
  // rowMark), the name, what it is on, the age. The glyph and the words carry the state; the colour only adds to it.
  member(t, mb, lead, lt, terminalOf, order, linked) {
    const address = terminalOf(mb);
    if (address && !linked.has(address)) {
      order.push(address);
      linked.add(address);
    }
    const mk = rowMark({ role: mb.role, state: mb.state, running: mb.state === "running" }, lt);
    const quiet = mb.state === "idle" || mb.state === "gone" || mb.state === "unknown";
    const word = mb.what === mb.word.toLowerCase() || mb.what === "blocked"; // model.js memberRow: no item, the state's word
    // ah2-220: a lane's orchestrator reads Sub-Orchestrator; its tooltip names the lane (select.js memberLabel)
    const who = mb.title !== mb.name ? mb.title : `${mb.name}${mb.role && mb.role !== mb.name ? ` (${mb.role})` : ""}`;
    const full = `${who} · ${mb.word} · ${mb.what}${mb.age ? ` · ${mb.age}` : ""}`;
    // Every row has the ring border, linked or not, so their columns line up.
    const row = this.ring(t, address ? Button.new(`m-${mb.key}`) : h_flex().id(`m-${mb.key}`), t.chrome)
      .flex_1().min_w(0).h(30).pl(4).pr(8).gap(6).flex().items_center().rounded(6).text_size(12.5)
      .tooltip(`${full} · peer session`) // ah2-225 B4: told apart from a subagent row without colour
      .accessibility_label(address ? `${full}, peer session. Focus its terminal` : `${full}, peer session`)
      .child(svg(roleIcon(mb.role)).size(14).flex_none().text_color(mb.role === "orchestrator" ? (lt ? "#8a6100" : "#e6b84a") // ah2-274: the Orchestrator's crown is gold
          : laneHue(mb.role, mb.column, lt) ?? t.foreground)
        .when(mb.state === "gone" || mb.state === "unknown", (el) => el.opacity(0.4)))
      .child(div().w(14).flex_none().flex().justify_center().text_size(11).text_color(mk.mark ?? t.blocked).child(mk.glyph))
      // what it is on gives way first, then the name (to 40 px); a bare state word ("idle") never does, so the state is never lost
      .child(div().flex_auto().min_w(40).truncate().text_color(quiet ? t.muted : t.foreground).child(mb.label))
      .child(div().when(word, (el) => el.flex_none()).when(!word, (el) => el.flex_shrink(100).min_w(0)).truncate().text_size(12).text_color(t.muted).child(mb.what))
      .child(div().w(28).flex_none().text_right().font_family(MONO).text_size(11).text_color(t.muted).child(mb.age));
    return div().flex().w_full().gap(4).child(lead).child(address
      ? row.cursor_pointer().hover((el) => el.bg(t.hover)).on_click(() => run("pane.focus", { terminal: address }))
      : row);
  }

  // ah2-225 B1/B3: a job's or subagent's leaf row under its parent: the job mark or the subagent's role icon (the generic one when its
  // type is no role), the label, the "subagent" tag, the time against its estimate (or the run's age), and an overdue mark that is a
  // glyph and a word. No pane to go to, so not a button: the tooltip carries the full text. The same 30 px height as a member row.
  under(t, x, lead, parent, lt, now) {
    const tip = underTip(x, parent, now);
    const sub = x.kind === "subagent";
    const row = this.ring(t, h_flex().id(`u-${x.key}`), t.chrome)
      .flex_1().min_w(0).h(30).pl(4).pr(8).gap(6).flex().items_center().rounded(6).text_size(12)
      .tooltip(tip).accessibility_label(tip)
      .child(sub ? svg(roleIcon(x.role)).size(14).flex_none().text_color(roleHue(x.role, lt) ?? t.foreground)
        : div().w(14).flex_none().flex().justify_center().text_size(12).text_color(t.muted).child("◷"))
      .child(div().flex_auto().min_w(40).truncate().text_color(t.muted).child(x.label))
      .when(sub, (el) => el.child(div().h(16).px(5).flex().flex_none().items_center().rounded(4).border(1).border_dashed().border_color(t.muted)
        .text_size(10.5).text_color(t.muted).child("subagent")))
      // the time keeps its width (only "waiting for the test slot" gives way); an overdue one carries ⚠ in the blocked tone, the word in the tooltip
      .child(div().when(x.queued, (el) => el.flex_shrink(100).min_w(0)).when(!x.queued, (el) => el.flex_none()).truncate().font_family(MONO).text_size(11)
        .when(x.overdue, (el) => el.font_semibold().text_color(t.blocked)).when(!x.overdue, (el) => el.text_color(t.muted))
        .child(x.overdue ? `⚠ ${runText(x, now)}` : runText(x, now)));
    return div().flex().w_full().gap(4).child(lead).child(row);
  }

  // ---- window overlay: one team's full sitrep ----

  // In a team's Desk (its Sitrep tab, desk-per-team D6, `deskClose` given) the tab row has the name and ×: a 36 px toolbar instead.
  sitrep(t, deskClose = null) {
    const now = Date.now();
    const s = sitrepModel(this.st.slices, this.team, now, this.st.gone);
    const name = s?.name ?? this.team.split("/").pop();
    const close = deskClose ?? (() => closeOverlay(props().overlay));

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
      .child(h_flex().h(deskClose ? 36 : 44).px(16).gap(10).flex_none().items_center().border_b(1).border_color(t.border)
        .when(!deskClose, (el) => el.child(div().id("ah2-sitrep-title").min_w(0).truncate().text_size(13).font_semibold().text_color(t.foreground).when(Boolean(s?.real), (el) => el.tooltip(s.real)).child(`${name} · sitrep`)))
        .when((s?.needsYou ?? 0) > 0, (el) => el.child(this.needs(t, s.needsYou, "Needs you", 0, s.blocked)))
        .child(div().flex_1())
        .when(Boolean(s) && !s.closed && !s.lane, (el) => el.child(this.ring(t, Button.new("ah2-sitrep-dismiss"), t.surface)
          .h(24).px(8).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(12).text_color(t.muted)
          .hover((x) => x.bg(t.hover).text_color(t.foreground))
          .tooltip(`Dismiss ${name}`)
          .accessibility_label(`Dismiss team ${name}`)
          .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
          .on_click(() => openDismiss(this.team, name, this.workRootOf(this.team), s?.lane ? name : null))
          .child("Dismiss")))
        // ah2-218 B2: beside Dismiss; a lane is renamed too (the dialog acts as the user)
        .when(Boolean(s) && !s.closed, (el) => el.child(this.ring(t, Button.new("ah2-sitrep-rename"), t.surface)
          .h(24).px(8).flex().flex_none().items_center().rounded(6).cursor_pointer().text_size(12).text_color(t.muted)
          .hover((x) => x.bg(t.hover).text_color(t.foreground))
          .tooltip(`Give ${name} a display name`)
          .accessibility_label(`Rename team ${name}`)
          .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
          .on_click(() => openRename(this.team, name, s.displayName, s.realName, this.workRootOf(this.team)))
          .child("Rename")))
        .when(!deskClose, (el) => el.child(this.closeButton(t, "ah2-sitrep-close", "Close the sitrep", close))))
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
          // in a team's Desk, this same window's Q&A tab (D6)
          .on_click((_e, cx) => (this.desk ? this.select("qa", cx) : openView("qa")))
          .child("Answer in Q&A")));
  }
}
