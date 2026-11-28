// A member's state mark, as Herdr's own agents list draws one (herdr-app defaults/mods/agents/main.js status()): working spins
// Herdr's spinner (spinner.svg, copied from its workspace-chrome), blocked is a ! on its tint, idle a ring, done a dot. The
// shape carries the state; colour only adds to it. Ah2's own states: running (a live orchestrator between turns) is the done
// dot in the working tone, so nothing looks like a stuck spinner; gone ×, no signal ?. The row's text and tooltip say the
// state in words as well. The idle ring and the ×/? are muted, not Herdr's idle grey, which is under 3:1 on the light sidebar.

import { div, svg } from "gpui-kit";
import { h_flex } from "gpui-base";
import { shared } from "herdr";
import { SETTINGS_KEY, settingsOf } from "./settings-model.js";

export function stateMark(t, state) {
  switch (state) {
    case "working": {
      // settings.js reducedMotion()'s rule, read here: settings.js imports update.js, which imports this file
      const spinner = svg("mods/ah2/spinner.svg").size(12).flex_none().text_color(t.working);
      return settingsOf(shared(SETTINGS_KEY)).motion === "reduced" ? spinner : spinner.spin(900);
    }
    case "running":
      return div().size(7).flex_none().rounded(4).bg(t.working);
    case "blocked":
      return div().size(14).flex_none().rounded(4).bg(`${t.blocked}26`).text_size(10.5).font_bold().text_color(t.blocked)
        .flex().items_center().justify_center().child("!");
    case "idle":
      return div().size(7).flex_none().rounded(4).border(1.5).border_color(t.muted);
    case "done":
      return div().size(7).flex_none().rounded(4).bg(t.done);
    default:
      return div().flex_none().text_size(10).text_color(t.muted).child(state === "gone" ? "×" : "?");
  }
}

// The mark centred in a 14 px column, so rows line up whatever the state.
export const markCell = (t, state) => h_flex().w(14).flex_none().justify_center().child(stateMark(t, state));
