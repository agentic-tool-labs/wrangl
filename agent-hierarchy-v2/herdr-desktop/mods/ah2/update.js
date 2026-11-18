// The update dialog (ah2-192), props {view:"update", opened}: the Wrangl logo, the heading, the daemon build and the mod install
// with their state (settings-model.js updateDialogOf, from REBUILD_KEY and UPDATE_KEY alone), and what they printed: the tail of
// <home>/logs/rebuild.log, then `ah herdr install`'s output (herdr's exec returns it only when the command ends). Opened when a build
// starts (main.js rebuildRead) or an install starts (settings.js step). Closing it stops nothing. Colours are theme() tokens only.

import { div, image } from "gpui-kit";
import { v_flex, h_flex, Button, Scrollbar, v_virtual_list, VirtualListScrollHandle } from "gpui-base";
import { props, openOverlay, closeOverlay, shared, setShared } from "herdr";
import { ah, target } from "./ah.js";
import { REBUILD_KEY, homeFor } from "./rebuild-model.js";
import { UPDATE_KEY, UPDATE_DIALOG_KEY, CLAIM_REFRESH_MS, claimFresh, updateDialogOf, outputOf } from "./settings-model.js";
import { markCell } from "./marks.js";
import { settings, installClick } from "./settings.js";

const MONO = "Geist Mono";
const LINE = 16; // one output line, px
const MARK = { running: "working", done: "done", failed: "blocked", waiting: "idle" };

// Opens the dialog unless one is open. Every copy may call it; the check and the claim run in one go on the UI thread, so one
// opens it. A stale claim (claimFresh) is a dialog gone without its close: it is not closed by id, since herdr reuses ids.
// Answers whether a dialog now shows the update: false when a claim declines it but no dialog of this window is open (a claim a
// crash left, or another window's), so the caller still posts its notice. Mod state is per window (herdr window.rs: one runtime each).
let openHere = 0; // update dialogs open in this window
export function openUpdate() {
  const now = Date.now();
  if (claimFresh(shared(UPDATE_DIALOG_KEY), now)) return openHere > 0;
  const overlay = openOverlay({ mod: "ah2", props: { view: "update", opened: now }, kind: "dialog", width: 560, height: 460 });
  setShared(UPDATE_DIALOG_KEY, { overlay, at: now });
  return true;
}
export const updateOpen = () => claimFresh(shared(UPDATE_DIALOG_KEY), Date.now());

export function updateInit(v, cx) {
  v.keys = cx.focus_handle();
  v.out = VirtualListScrollHandle.new();
  v.follow = true; // the newest line stays in view until the user scrolls up
  v.lines = [];
  v.log = null;
  v.home = null;
  v.opened = props()?.opened ?? Date.now();
  const mine = props()?.overlay ?? null;
  // a mod reload keeps the dialog open and runs init again (herdr reload_now): the claim stays this dialog's
  setShared(UPDATE_DIALOG_KEY, { overlay: mine, at: Date.now() });
  openHere++;
  v.counted = true;
  cx.spawn(async (cx) => {
    await cx.sleep(0);
    v.keys.focus();
    const cache = { key: null, home: null };
    const lookup = async () => {
      const r = await ah().exec("ah", ["where"], { timeout: 10000 }).catch(() => null);
      return r?.code === 0 ? r.stdout : null;
    };
    let shown = null;
    let building = true; // read the log once on open, then while the build runs, then once after it ends
    while (!v.gone) {
      const d = updateDialogOf(shared(REBUILD_KEY), shared(UPDATE_KEY), Date.now(), v.opened, v.home, settings().autoUpdate);
      const now = d.steps[0].state === "running";
      if (building || now) {
        v.home = await homeFor(cache, JSON.stringify(target()), lookup);
        const text = v.home ? await ah().read(`${v.home}/logs/rebuild.log`).catch(() => null) : null;
        if (text != null) v.log = text;
      }
      building = now;
      const lines = outputOf(v.log, d.output, d.steps[1].state === "running");
      const key = JSON.stringify([d.heading, d.offer, d.steps.map((s) => s.state)]);
      const grew = lines.length !== v.lines.length || lines.at(-1) !== v.lines.at(-1);
      if (grew) {
        v.lines = lines;
        if (v.follow) v.out.scroll_to_bottom();
      }
      // a step's state can change with time alone (an install past BUSY_MS); shared writes redraw by themselves
      if (grew || key !== shown) cx.notify();
      shown = key;
      if (v.gone) break; // closed during a read above: its claim is cleared and must stay so
      const c = shared(UPDATE_DIALOG_KEY);
      // re-claimed when empty or stale too (a reload's close may have cleared it), so no second dialog opens
      const ours = c?.overlay === mine && Date.now() - c.at <= CLAIM_REFRESH_MS;
      if (!ours && (c?.overlay === mine || !claimFresh(c, Date.now()))) setShared(UPDATE_DIALOG_KEY, { overlay: mine, at: Date.now() });
      await cx.sleep(1000);
    }
  });
}

// Herdr's close: the claim goes with this dialog.
export function updateClose(v) {
  v.gone = true;
  if (v.counted) openHere--;
  v.counted = false;
  if (shared(UPDATE_DIALOG_KEY)?.overlay === (props()?.overlay ?? null)) setShared(UPDATE_DIALOG_KEY, null);
}

export function updateView(v, t) {
  const p = props();
  const d = updateDialogOf(shared(REBUILD_KEY), shared(UPDATE_KEY), Date.now(), v.opened, v.home, settings().autoUpdate);
  const close = () => closeOverlay(p.overlay);
  // Install now (D11): the "available" notice's path, nothing else; gone from the click until that install settles
  const offer = d.offer && !v.asked;
  const installNow = (_e, cx) => {
    const run = installClick();
    if (!run) return; // another copy's clicked install is running
    v.asked = true;
    cx.notify();
    run.finally(() => {
      v.asked = false;
      cx.notify();
    });
  };
  const label = d.finished ? "Done" : "Close";

  const step = (s) => v_flex().id(`upd-${s.key}`).flex_none().py(4).border_b(1).border_color(t.border)
    .child(h_flex().min_h(24).gap(8).items_center()
      .child(s.state === "none" ? h_flex().w(14).flex_none().justify_center().text_color(t.muted).child("–") : markCell(t, MARK[s.state]))
      .child(div().flex_1().min_w(0).truncate().text_color(s.state === "none" ? t.muted : t.foreground).child(s.label))
      .child(div().flex_none().text_size(11.5).text_color(s.state === "failed" ? t.blocked : t.muted).child(s.word)))
    .when(Boolean(s.reason), (el) => el.child(div().id(`upd-${s.key}-why`).pl(22).pb(4).min_w(0).truncate()
      .text_size(11.5).text_color(t.blocked).tooltip(s.reason).child(s.reason)));

  const lines = v.lines;
  const output = v_flex().id("upd-out").relative().mt(12).flex_1().min_h(0).py(4).rounded(8).border(1).border_color(t.border).bg(t.chrome)
    .font_family(MONO).text_size(11.5).text_color(t.foreground).accessibility_label("Installer output")
    .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
    // ponytail: scrolling up stops following, scrolling down follows again; herdr gives no scroll offset to tell "at the bottom"
    .on_scroll_wheel((e, _cx) => {
      if (e.delta.y !== 0) v.follow = e.delta.y < 0;
    })
    .child(lines.length === 0
      ? div().px(8).text_color(t.muted).child("No output yet.")
      : v_virtual_list("upd-out-list", lines.length, LINE, (i) => String(i), (r) => {
        const rows = [];
        for (let i = r.start; i < r.end; i++) {
          rows.push(div().id(`upd-l-${i}`).w_full().h(LINE).px(8).min_w(0).truncate().tooltip(lines[i]).child(lines[i]));
        }
        return rows;
      }).track_scroll(v.out).flex_1().min_h(0))
    .when(lines.length > 0, (el) => el.child(Scrollbar.vertical("upd-out-list").absolute().inset_0()));

  return v_flex().id("root").size_full().min_h(0).bg(t.surface).text_size(12.5)
    .track_focus(v.keys)
    .on_key_down((e, _cx) => {
      if (e.keystroke === "escape") close();
    })
    .child(h_flex().px(16).pt(16).pb(12).gap(12).flex_none().items_center().border_b(1).border_color(t.border)
      .child(image("mods/ah2/wrangl-color.svg").size(40).flex_none())
      .child(v_flex().flex_1().min_w(0).gap(4)
        .child(div().text_size(11).font_semibold().text_color(t.muted).child("Updating Wrangl"))
        .child(div().text_size(14).font_semibold().text_color(t.foreground).child(d.heading))))
    .child(v_flex().id("upd-body").flex_1().min_h(0).px(16).pt(4).pb(12)
      .children(d.steps.map(step))
      .child(output))
    .child(h_flex().h(48).px(16).gap(8).flex_none().items_center().justify_end().border_t(1).border_color(t.border)
      .when(offer, (el) => el.child(v.ring(t, Button.new("upd-install"), t.surface)
        .h(28).px(12).flex().flex_none().items_center().rounded(7).cursor_pointer().text_size(12).font_medium()
        .bg(t.accent).text_color(t.accent_foreground).hover((x) => x.opacity(0.88))
        .accessibility_label(`Install the herdr mod ${d.steps[1].version ?? ""}`.trim())
        .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
        .on_click(installNow)
        .child("Install now")))
      .child(v.ring(t, Button.new("upd-close"), t.surface)
        .h(28).px(12).flex().flex_none().items_center().rounded(7).cursor_pointer().text_size(12).font_medium()
        .when(d.finished, (el) => el.bg(t.accent).text_color(t.accent_foreground).hover((x) => x.opacity(0.88)))
        .when(!d.finished, (el) => el.bg(t.raised).text_color(t.foreground).hover((x) => x.bg(t.hover)))
        .accessibility_label(d.finished ? "Done" : "Close; the update carries on")
        .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
        .on_click(close)
        .child(label)));
}
