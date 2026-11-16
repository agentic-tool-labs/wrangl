// The wrangl layout's sidebar tabs (b374): one stack holding Herdr's agents list and the ah2 sitrep, its header the ah2 mod's
// tab strip. tabs.js imports Herdr's runtime, so its stack and tab ids are read from its source and held to the layout.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "herdr-desktop");
const layout = JSON.parse(readFileSync(join(root, "layouts", "wrangl.json"), "utf8"));
const tabsSrc = readFileSync(join(root, "mods", "ah2", "tabs.js"), "utf8");
const find = (n, id) => (n?.id === id ? n : (n?.children ?? []).reduce((hit, c) => hit ?? find(c, id), null));

test("the sidebar stack shows Herdr's agents or the ah2 sitrep, one at a time, under the ah2 tab strip, ids as tabs.js names them", () => {
  const stack = find(layout, /export const STACK = "([^"]+)"/.exec(tabsSrc)[1]);
  assert.equal(stack?.type, "stack");
  assert.deepEqual(stack.header.content, { mod: "ah2", props: { view: "tabs" } });
  const tabs = [...tabsSrc.matchAll(/\["([^"]+)", "([^"]+)"\]/g)].map((m) => m[1]);
  assert.deepEqual(stack.children.map((c) => c.id), tabs);
  assert.deepEqual(stack.children.map((c) => c.content), [{ mod: "agents" }, { mod: "ah2", props: { view: "sitrep" } }]);
  assert.ok(tabs.includes(stack.active), "the stack starts on one of the tabs");
});

// atp-1257: the dock is the layout's own hidden side column, apart from Herdr's panel (its tab bar restores that one per space,
// and only to Files or Changes); one block per dockable view, drawn as that view with dock set.
test("the ah2 dock column holds one block per dockable view, beside Herdr's own panel, ids as dock.js names them", async () => {
  const { DOCKABLE, DOCK, DOCK_BODY } = await import("../../herdr-desktop/mods/ah2/settings-model.js");
  const id = (name) => ({ DOCK, DOCK_BODY })[name];
  const column = find(layout, id("DOCK"));
  assert.deepEqual([column?.type, column?.hidden], ["column", true]);
  const body = find(column, id("DOCK_BODY"));
  assert.equal(body?.type, "stack");
  assert.deepEqual(body.children.map((c) => [c.id, c.content]), DOCKABLE.map((v) => [`ah2-${v}`, { mod: "ah2", props: { view: v, dock: true } }]));
  assert.ok(!find(find(layout, "panel"), id("DOCK_BODY")), "not inside Herdr's panel");
});

// Herdr starts a block's copy only once it shows, then keeps it alive hidden (herdr-app window.rs visible()); a docked copy asks
// `ah view events` only while the dock shows it, so a closed column or another docked view costs no poll.
test("a docked view polls only while the open dock shows it", async () => {
  const { dockShown } = await import("../../herdr-desktop/mods/ah2/settings-model.js");
  const shown = structuredClone(layout);
  assert.equal(dockShown(layout, "qa"), false, "the column ships hidden");
  delete find(shown, "ah2-panel").hidden; // layout.show
  find(shown, "ah2-panel-body").active = "ah2-graph"; // layout.select
  assert.deepEqual(["qa", "graph"].map((v) => dockShown(shown, v)), [false, true]);
  find(shown, "ah2-panel").hidden = true; // layout.hide
  assert.equal(dockShown(shown, "graph"), false);
});

// The desk (atp-1495): the dock column's tab row, an ah2 segment in the status bar, the Needs-you drawer peeking on the right.
test("the desk: the dock stack's header is the ah2 tab row, the status bar holds the ah2 segment, the drawer peeks 44 px on the right", () => {
  assert.deepEqual(find(layout, "ah2-panel-body").header.content, { mod: "ah2", props: { view: "panel-tabs" } });
  const status = find(layout, "status");
  assert.deepEqual(status.children.map((c) => c.content), [{ mod: "workspace-status" }, { mod: "ah2", props: { view: "status" } }]);
  const [drawer] = find(layout, "main").floating;
  assert.deepEqual([drawer.content, drawer.edge, drawer.peek], [{ mod: "ah2", props: { view: "peek" } }, "right", 44]);
});

// atp-1727: the maximize/restore button sits only in the Desk window's tab row; the column's row (docked) has none.
test("the desk's tab row draws maximize/restore in the window only", () => {
  const row = tabsSrc.slice(tabsSrc.indexOf("export function panelTabsView"));
  const [win, column] = row.split("return row.size_full()");
  assert.match(win, /ah2-desk-maximize/);
  assert.match(win, /ah2-desk-restore/);
  assert.doesNotMatch(column, /resizeDeskWindow|ah2-desk-maximize|ah2-desk-restore/);
});
