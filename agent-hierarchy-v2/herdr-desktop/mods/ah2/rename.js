// Rename a team (ah2-218 B2): the dialog, props {view:"rename", team:"<dirId>/<team>", label, current, real, workRoot}, opened from
// the sitrep window's header beside Dismiss. One field prefilled with the display name; Save (Enter), Clear when one is set, Cancel
// (Esc). The call runs in a click's or a submit's own task (rename-model.js teamRename); render only draws the state it left.
// Colours are theme() tokens only.

import { div } from "gpui-kit";
import { v_flex, h_flex, Button, Input, InputState } from "gpui-base";
import { props, openOverlay, closeOverlay } from "herdr";
import { ah } from "./ah.js";
import { Link } from "./link-model.js";
import { teamRename, renameForm, renameOutcome } from "./rename-model.js";

export const openRename = (id, label, current, real, workRoot = null) =>
  openOverlay({ mod: "ah2", props: { view: "rename", team: id, label, current: current ?? "", real, workRoot }, kind: "dialog", key: "ah2-rename", width: 400, height: 228 });

export function renameInit(v, cx) {
  const p = props();
  v.keys = cx.focus_handle();
  v.rn = { touched: false, busy: false, error: null };
  v.rnInput = InputState.new({ placeholder: "Display name", value: p.current ?? "" });
  v.rnInput.on("change", (_e, cx) => {
    v.rn = { ...v.rn, touched: true };
    cx.notify();
  });
  v.rnInput.on("submit", (_e, cx) => renameSend(v, cx, v.rnInput.value()));
  v.rnFocus = false;
  v.rnInput.on("focus", (_e, cx) => {
    v.rnFocus = true;
    cx.notify();
  });
  v.rnInput.on("blur", (_e, cx) => {
    v.rnFocus = false;
    cx.notify();
  });
  cx.spawn(async (cx) => {
    await cx.sleep(0);
    v.rnInput.focus();
  });
}

// Save (text) or Clear (null): one bounded call in its own task; done closes the dialog, a failure keeps it open with the reason.
function renameSend(v, cx, text) {
  const p = props();
  if (v.rn.busy) return;
  if (text !== null && !renameForm(text).canSave) {
    v.rn = { ...v.rn, touched: true };
    cx.notify();
    return;
  }
  v.rn = { ...v.rn, busy: true, error: null };
  cx.notify();
  cx.spawn(async (cx) => {
    const sleep = (ms) => cx.sleep(ms);
    const link = new Link({ fetch: globalThis.fetch, runner: ah, sleep, backoff: [2000] });
    const got = await teamRename(ah().exec, (body, t) => link.teamRename(body, t, sleep), p.team, text === null ? null : text.trim(), { workRoot: p.workRoot, sleep });
    const out = renameOutcome(got);
    if (out.done) {
      closeOverlay(p.overlay);
      return;
    }
    v.rn = { ...v.rn, busy: false, error: out.error };
    cx.notify();
  });
}

export function renameView(v, t) {
  const p = props();
  const f = renameForm(v.rnInput.value(), { touched: v.rn.touched, current: p.current ?? "" });
  const close = () => closeOverlay(p.overlay);

  const button = (id, label, primary, onPress) => v.ring(t, Button.new(`rn-${id}`), t.surface)
    .h(28).px(12).flex().flex_none().items_center().rounded(7).cursor_pointer().text_size(12).font_medium()
    .when(primary, (el) => el.bg(t.accent).text_color(t.accent_foreground).hover((x) => x.opacity(0.88)))
    .when(!primary, (el) => el.bg(t.raised).text_color(t.foreground).hover((x) => x.bg(t.hover)))
    .when(v.rn.busy && id !== "cancel", (el) => el.opacity(0.6))
    .accessibility_label(label)
    .on_mouse_down("left", (_e, cx) => cx.stop_propagation())
    .on_click(onPress)
    .child(label);

  return v_flex().id("root").size_full().min_h(0).bg(t.surface).text_size(12.5)
    .track_focus(v.keys)
    .on_key_down((e, _cx) => {
      if (e.keystroke === "escape") close();
    })
    .child(h_flex().h(44).px(16).gap(10).flex_none().items_center().border_b(1).border_color(t.border)
      .child(div().flex_1().min_w(0).truncate().text_size(13).font_semibold().text_color(t.foreground).child(`Rename ${p.label}`))
      .child(v.closeButton(t, "ah2-rename-close", "Close", close)))
    .child(v_flex().id("rn-body").flex_1().min_h(0).px(16).pt(12).gap(4).on_mouse_down("left", (_e, cx) => cx.stop_propagation())
      .child(div().text_size(11.5).text_color(t.muted).child("Display name"))
      .child(h_flex().h(28).px(8).flex_none().items_center().rounded(6).border(1).border_color(f.problem ? t.blocked : v.rnFocus ? t.accent : t.border).bg(t.surface)
        .accessibility_label(f.problem ? `Display name. ${f.problem}` : "Display name")
        .child(Input.new(v.rnInput).flex_1().h_full().text_size(12.5).text_color(t.foreground)))
      .child(div().min_h(16).text_size(11.5).text_color(t.blocked).child(f.problem ?? ""))
      .child(div().text_size(11.5).text_color(t.muted).child(`Shown wherever the team is named. Commands and addresses keep ${p.real}.`))
      .when(Boolean(v.rn.error), (el) => el.child(div().mt(4).text_size(11.5).text_color(t.blocked).child(v.rn.error))))
    .child(h_flex().h(48).px(16).gap(8).flex_none().items_center().justify_end().border_t(1).border_color(t.border)
      .child(button("save", "Save", true, (_e, cx) => renameSend(v, cx, v.rnInput.value())))
      .when(f.clear, (el) => el.child(button("clear", "Clear", false, (_e, cx) => renameSend(v, cx, null))))
      .child(button("cancel", "Cancel", false, close)));
}
