// ah2 in Herdr: the member sheet (spec herdr-board-parity Part C), the one the Graph opens on an agent and the Board opens on an
// owner or a member name, and the one sheet frame every Board and Graph sheet sits in. Colours are theme() tokens only.

import { div, svg } from "gpui-kit";
import { v_flex, h_flex, Button } from "gpui-base";
import { run } from "herdr";
import { ageText, paneAct, memberPageLink } from "./graph-model.js";
import { stateWord } from "./select.js";
import { glyphOf } from "./graph-world.js";
import { streamName } from "./plans-model.js";
import { memberModel, SUMMARY_MESSAGES } from "./member-model.js";

const MONO = "Geist Mono";
const stop = (_e, cx) => cx.stop_propagation(); // a press on a control must not drag the window

const SHEET_W = 300;
// The one sheet frame (the Board's item, backlog, group and member sheets, and the Graph's member sheet): SHEET_W beside the
// content, or the whole column when docked (`full`).
export const sheetColumn = (t, full, id) => v_flex().id(id).when(full, (el) => el.w_full()).when(!full, (el) => el.w(SHEET_W).border_l(1).border_color(t.border))
  .flex_none().h_full().min_h(0).bg(t.surface);

// ---- the member sheet (specs herdr-graph-agent-details, herdr-board-parity C1): the web's member summary (member-summary.js
// MemberSummary, ported in member-model.js), in its order and wording: the name with Go to pane and close, role · model, the
// state and since, where it sits, Now, the Graph's Map rail extras (graph-panel.js) when given, the latest messages, the Board's
// History when given, and Open page (in the browser). A member that has gone reads as gone; nothing here starts a timer (ages
// follow the dot clock's rule, ageText). Its inputs: `id` the member, `ui` memberModel's team scope (graph-model.js sheetUi
// shape), `close(cx)`, `focus` the first focus (Go to pane) or null, `live` whether seconds are true (the Graph's dot clock),
// `address` the terminal Go to pane focuses, `extras(mm)` the rail extras (the Graph's in Map; the Board passes none), and
// `history` the items it holds as [{ key, title, label, act }] (the Board's; the Graph passes none, so it draws no History).
export function memberSheet(v, t, { id, ui, close, focus = null, live = false, address = null, extras = null, history = null }, full) {
  const slices = v.st.slices;
  const mm = memberModel(slices, ui, id, SUMMARY_MESSAGES);
  const since = (ms) => ageText(Date.now() - ms, live).replace(" ago", "");
  const go = paneAct(address, mm.label ?? mm.name, mm.gone);
  const page = memberPageLink(v.link?.port ?? null, id);
  const icon = (bid, file, label, act, why) => Button.new(bid).size(24).flex().flex_none().items_center().justify_center().rounded(6)
    .border(1).border_color(t.surface).focus((el) => el.border_color(t.accent)).disabled(Boolean(why))
    .when(!why, (el) => el.cursor_pointer().text_color(t.foreground).hover((s) => s.bg(t.hover)))
    .when(Boolean(why), (el) => el.text_color(t.muted))
    .tooltip(why ?? label).accessibility_label(why ?? label)
    .on_mouse_down("left", stop).on_click((_e, cx) => !why && act(cx))
    .child(svg(file).size(14));
  const goBtn = icon("graph-sheet-pane", "mods/ah2/app-window.svg", "Go to pane", () => run(go.cmd, go.args), go.why)
    .when(Boolean(focus), (el) => el.track_focus(focus)); // the sheet's first focus (Amendment 1): click, then Enter
  const shut = icon("graph-sheet-close", "mods/ah2/x.svg", "Close the details (esc)", (cx) => { close(cx); cx.notify(); }, null);
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
  const x = extras && !mm.gone ? extras(mm) : null;
  const extraRows = x ? [
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
      .children(extraRows)
      .child(head("Latest messages"))
      .children(msgs)
      // the web member page's History: the items it holds, each opening its item sheet (the Board's only)
      .children(history ? [head("History"), ...(history.length === 0 ? [dim("No items").text_size(12).py(4)]
        : history.map((h, i) => h_flex().min_h(24).gap(6).items_center().text_size(12)
          .child(h.act ? Button.new(`graph-sheet-hist-${i}`).flex().min_w(0).cursor_pointer().text_color(t.accent)
            .hover((s) => s.underline()).focus((s) => s.underline())
            .accessibility_label(`${h.key}${h.title ? ` · ${h.title}` : ""}, open its sheet`).on_mouse_down("left", stop).on_click((_e, cx) => h.act(cx))
            .child(div().min_w(0).truncate().child(`${h.key}${h.title ? ` · ${h.title}` : ""}`))
            : div().min_w(0).truncate().text_color(t.foreground).child(`${h.key}${h.title ? ` · ${h.title}` : ""}`))
          .child(div().flex_none().text_size(11).text_color(t.muted).child(h.label))))] : [])
      .child(h_flex().child(pageBtn)));
}
