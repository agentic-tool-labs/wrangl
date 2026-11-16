// Dismiss a team (ah2-45): the dialog, props {view:"dismiss", team:"<dirId>/<team>", name, workRoot}, opened from a team's row in
// the sidebar and from its sitrep window. A team nobody runs goes at once; one still working offers Pause, then quit (members save
// their work, then their sessions close; the team can be resumed) or Force quit (close now). Every wait is bounded and runs in the
// dialog's own task (dismiss-model.js runDismiss); render only draws the state it left. Colours are theme() tokens only.

import { div } from "gpui-kit";
import { v_flex, h_flex, Button } from "gpui-base";
import { props, openOverlay, closeOverlay, notify } from "herdr";
import { ah } from "./ah.js";
import { Link } from "./link-model.js";
import { START, teamClose, runDismiss, buttonsOf } from "./dismiss-model.js";

export const openDismiss = (id, name, workRoot = null) =>
  openOverlay({ mod: "ah2", props: { view: "dismiss", team: id, name, workRoot }, kind: "dialog", key: "ah2-dismiss", width: 440, height: 340 });

const LABEL = { pause: "Pause, then quit", force: "Force quit", dismiss: "Dismiss", cancel: "Cancel", leave: "Leave it", ok: "OK" };
const ENDS = new Set(["cancel", "leave", "ok"]); // these close the dialog; the flow has ended, or ends on the press

export function dismissInit(v, cx) {
  v.keys = cx.focus_handle();
  v.dz = START;
  v.press = null; // the waiting flow's resolver: one press, then gone
  cx.spawn(async (cx) => {
    await cx.sleep(0);
    v.keys.focus();
    const p = props();
    const sleep = (ms) => cx.sleep(ms);
    const link = new Link({ fetch: globalThis.fetch, runner: ah, sleep, backoff: [2000] });
    const last = await runDismiss({
      call: (more, ms) => teamClose(ah().exec, (body, t) => link.teamClose(body, t, sleep), p.team, more, { workRoot: p.workRoot, sleep, ms }),
      sleep,
      emit: (s) => {
        v.dz = s;
        cx.notify();
      },
      wait: () => new Promise((res) => { v.press = res; }),
    });
    // a team nobody ran: one click, a notice, and the dialog goes
    if (last.step === "done" && !last.plan?.busy && !last.note) {
      notify({ title: `Dismissed ${p.name ?? p.team}`, level: "success" });
      closeOverlay(p.overlay);
    }
  });
}

export function dismissView(v, t) {
  const p = props();
  const s = v.dz;
  const name = p.name ?? String(p.team ?? "").split("/").pop();
  const close = () => {
    const r = v.press;
    v.press = null;
    r?.(null);
    closeOverlay(p.overlay);
  };
  const press = (what) => () => {
    const r = v.press;
    v.press = null;
    r?.(what);
    if (ENDS.has(what)) closeOverlay(p.overlay);
  };

  const button = (what, primary) => v.ring(t, Button.new(`dz-${what}`), t.surface)
    .h(28).px(12).flex().flex_none().items_center().rounded(7).cursor_pointer().text_size(12).font_medium()
    .when(primary, (el) => el.bg(t.accent).text_color(t.accent_foreground).hover((x) => x.opacity(0.88)))
    .when(!primary, (el) => el.bg(t.raised).text_color(t.foreground).hover((x) => x.bg(t.hover)))
    .accessibility_label(what === "leave" && s.step === "pausing" ? "Leave the team paused" : LABEL[what])
    .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
    .on_click(press(what))
    .child(what === "leave" && s.step === "pausing" ? "Leave it paused" : LABEL[what]);

  const line = (text, color = t.foreground) => div().mt(8).flex_none().text_color(color).child(text);
  const muted = (text) => line(text, t.muted).text_size(11.5);
  const body = [];
  const plan = s.plan;
  const members = () => (plan?.live ?? []).map((m, i) => h_flex().id(`dz-m-${i}`).min_h(24).gap(8).flex_none().items_center().border_b(1).border_color(t.border)
    .child(div().flex_none().font_medium().text_color(t.foreground).child(m.name))
    .child(div().flex_none().text_size(11.5).text_color(t.muted).child(m.role))
    // uncommitted or unmerged work, as ahd's release check words it
    .child(div().flex_1().min_w(0).truncate().text_size(11.5).text_color(m.work ? t.blocked : t.muted).child(m.work)));

  if (s.step === "reading") body.push(muted("Reading the team…"));
  if (s.step === "confirm") {
    if (s.changed) body.push(line("Someone started after this opened. Read it again.", t.blocked));
    if (plan.busy) body.push(line(`${plan.live.length === 1 ? "1 session is" : `${plan.live.length} sessions are`} still running:`), ...members());
    else body.push(line(`Dismiss ${name}?`), muted("Nothing of this team is running. It leaves the list; bring it back with ah roster reopen."));
    for (const n of plan.notes) body.push(muted(n));
    if (plan.leftRunning.length > 0) body.push(muted(`Left running: ${plan.leftRunning.join(", ")}. An Orchestrator's own session is never closed.`));
    if (plan.busy) body.push(muted("Pause, then quit asks each member to save its work first; the team can be resumed later. Force quit closes the sessions now."));
  }
  if (s.step === "pausing") {
    body.push(line("Pausing…"));
    body.push(muted(s.waitingOn.length > 0 ? `Waiting on ${s.waitingOn.join(", ")} to save their work. This can take a few minutes.` : "Asking the members to save their work."));
  }
  if (s.step === "closing") {
    if (s.note) body.push(muted(s.note));
    body.push(line(plan?.busy ? "Closing the sessions…" : "Dismissing…"));
  }
  if (s.step === "late") body.push(line("Still closing the sessions…"), muted("ahd is working on it. This dialog checks every few seconds."));
  if (s.step === "done") body.push(line(`Dismissed ${name}.`), ...(s.note ? [muted(s.note)] : []));
  if (s.step === "error") {
    body.push(line(s.error, t.blocked));
    if (plan?.busy) body.push(...members());
  }

  const buttons = buttonsOf(s);
  return v_flex().id("root").size_full().min_h(0).bg(t.surface).text_size(12.5)
    .track_focus(v.keys)
    .on_key_down((e, _cx) => {
      if (e.keystroke === "escape") close();
    })
    .child(h_flex().h(44).px(16).gap(10).flex_none().items_center().border_b(1).border_color(t.border)
      .child(div().flex_1().min_w(0).truncate().text_size(13).font_semibold().text_color(t.foreground).child(`Dismiss ${name}`))
      .child(v.closeButton(t, "ah2-dismiss-close", "Close", close)))
    .child(v_flex().id("dz-body").flex_1().min_h(0).px(16).pb(8).overflow_y_scrollbar().on_mouse_down("left", (_e, cx) => cx.stop_propagation())
      .children(body))
    .when(buttons.length > 0, (el) => el.child(h_flex().h(48).px(16).gap(8).flex_none().items_center().justify_end().border_t(1).border_color(t.border)
      .children(buttons.map((b, i) => button(b, i === 0 && b !== "force")))));
}
