// The ah2 herdr mod's settings and self-update rules (atp-1234): mods/ah2/settings-model.js, which imports no Herdr code.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { settingsOf, shownTeams, versionNewer, changelogEntry, updateStep, peekDismissed, peekClosed, peekPressOutside, deskRoute, unionSubs, qaShowing, deskWindowOpen, deskWindowSpec, DESK_WINDOW_SIZE, DESK_WINDOW_MAX, ABOUT, aboutVersions, updateNotify, clickFree, INSTALL_COMMAND, BUSY_MS } from "../../herdr-desktop/mods/ah2/settings-model.js";

// atp-1727: maximize and restore reopen the Desk window on the same tab; the next plain open (the strip toggle, Needs you) reads the
// size from the key's `max`, as dock.js openDeskWindow does.
test("maximize then restore reopens on the same tab at the right size, and the next open keeps that size", () => {
  const size = (s) => ({ width: s.width, height: s.height });
  const open = (key) => deskWindowSpec(key.view, key?.max === true);
  for (const view of ["qa", "graph"]) {
    const max = deskWindowSpec(view, true);
    assert.deepEqual([max.props.tab, max.props.max, max.key, size(max)], [view, true, "ah2-desk", DESK_WINDOW_MAX]);
    assert.deepEqual(size(open({ view, max: true })), DESK_WINDOW_MAX, "the strip toggle after maximize opens maximized");
    const back = deskWindowSpec(view, false);
    assert.deepEqual([back.props.tab, back.props.max, size(back)], [view, false, DESK_WINDOW_SIZE]);
    assert.deepEqual(size(open({ view, max: false })), DESK_WINDOW_SIZE, "after restore: the normal size");
    assert.deepEqual(size(open({ view })), DESK_WINDOW_SIZE, "a key from before atp-1727 (no max) opens at the normal size");
  }
  assert.ok(DESK_WINDOW_MAX.width <= 1 && DESK_WINDOW_MAX.height <= 1, "a share of Herdr's window, not px");
});

// The one-window Desk (atp-1728): where a Desk tab opens, the window's one subscription, and when Q&A counts as showing.
test("a Desk tab opens in the column, a new Desk window, or the open one", () => {
  assert.equal(deskRoute("column", true, false), "dock");
  assert.equal(deskRoute("column", true, true), "dock", "the column placement ignores a window left open");
  assert.equal(deskRoute("column", false, false), "open", "a layout without the column opens the window");
  assert.equal(deskRoute("column", false, true), "switch");
  assert.equal(deskRoute("window", true, false), "open");
  assert.equal(deskRoute("window", true, true), "switch", "never a second window: reopening the key would replace it");
});

test("the Desk window subscribes to every tab's slices once, in first-seen order", () => {
  assert.deepEqual(unionSubs(["view:summary"], ["questions:a", "view:summary"], [], ["questions:a", "plan:a"]), ["view:summary", "questions:a", "plan:a"]);
});

test("Q&A shows in the column or as the open Desk window's tab; a closed window's last tab is not showing", () => {
  assert.equal(qaShowing(true, null), true);
  assert.equal(qaShowing(false, { overlay: "o1", view: "qa" }), true);
  assert.equal(qaShowing(false, { overlay: "o1", view: "board" }), false);
  assert.equal(qaShowing(false, { view: "qa" }), false, "closed: only its last tab is kept");
  assert.equal(deskWindowOpen(null), false);
});

test("stored settings read back whole: odd or missing fields take their defaults", () => {
  const s = settingsOf({ tab: "agents-list", strip: { desk: false, feed: false }, hidden: ["d/x", 3], lanes: "sideways", interval: 7, notify: "yes", docked: ["graph", "files", "qa"] });
  // an old per-view `docked` list is not read: the whole Desk sits in the column until moved (atp-1728)
  assert.deepEqual(s, { tab: "agents-list", strip: { desk: false }, hidden: ["d/x"], lanes: "nested", interval: 0, notify: false, autoUpdate: false, autoQa: false, placement: "column", ahOn: "auto" });
  assert.equal(settingsOf({ placement: "window" }).placement, "window");
  assert.equal(settingsOf({ placement: "floating" }).placement, "column", "an odd placement is the column");
  assert.equal(settingsOf({ ahOn: "machine:wsl" }).ahOn, "machine:wsl");
  assert.equal(settingsOf({ ahOn: "wsl:Ubuntu" }).ahOn, "wsl:Ubuntu");
  assert.equal(settingsOf({ ahOn: "machine:" }).ahOn, "auto", "a machine with no name is not a setting");
  assert.equal(settingsOf({ ahOn: "remote" }).ahOn, "auto");
  assert.equal(settingsOf(null).placement, "column", "the Desk opens in the column until moved to a window");
  const lane = { id: "d/l", lanes: [] };
  assert.deepEqual(shownTeams([{ id: "d/a", lanes: [lane] }, { id: "d/x", lanes: [] }], settingsOf({ hidden: ["d/l", "d/x"] })), [{ id: "d/a", lanes: [] }]);
});

test("versions order as semver, a release after its pre-releases", () => {
  assert.ok(versionNewer("2.5.0", "2.5.0-pre.1"));
  assert.ok(versionNewer("2.5.0-pre.1.7411", "2.5.0-pre.1.7410"));
  assert.ok(versionNewer("2.10.0", "2.9.9"));
  assert.ok(!versionNewer("2.5.0-pre.1", "2.5.0"));
  assert.ok(!versionNewer("2.5.0", "2.5.0"));
  assert.ok(!versionNewer("2.5.0", null));
});

test("What's new is the CHANGELOG entry of that version only", () => {
  const md = "# Changelog\n\n## [2.6.0] - x\n\nNew gear.\n\n### Fixed\n- a\n\n## [2.5.0] - y\n\nOld.\n";
  assert.equal(changelogEntry(md, "2.6.0"), "New gear.\n\n### Fixed\n- a");
  assert.equal(changelogEntry(md, "9.9.9"), "");
});

test("a newer plugin is told once, auto-installs unless it failed before, and an install that landed says so", () => {
  const p = { installed: "2.5.0", bundled: "2.6.0", notes: "" };
  const first = updateStep({}, p, 1000, false);
  assert.deepEqual([first.newer, first.install, first.notice?.kind], [true, false, "available"]);
  assert.equal(updateStep(first.next, p, 2000, false).notice, null, "once per version");
  assert.equal(updateStep({}, p, 1000, true).install, true);
  assert.equal(updateStep({ failed: { version: "2.6.0" } }, p, 1000, true).install, false, "no auto retry of a failed version");
  const landed = updateStep({ installing: 900, installed: "2.5.0" }, { ...p, installed: "2.6.0" }, 1000, false);
  assert.deepEqual([landed.notice?.kind, landed.notice?.again, landed.next.installing], ["updated", false, 0]);
});

// ah2-111 Amendment 2: the "available" notice installs on click, waits for a running ahd build, and a click installs once.
test("the update offer carries the install click, once per version, held while ahd builds, none when current", () => {
  const p = { installed: "2.0.2", bundled: "2.0.3", notes: "" };
  const first = updateStep({}, p, 1000, false);
  assert.deepEqual(updateNotify(first.notice).action, { name: `ah2.${INSTALL_COMMAND}` }, "older marker, no build: the notice carries the action");
  assert.match(updateNotify(first.notice).body, /Click to install\.$/);
  assert.equal(updateStep(first.next, p, 2000, false).notice, null, "the same plugin version twice: one notice");
  const held = updateStep({}, p, 1000, false, false, true);
  assert.equal(held.notice, null, "a build still running: held");
  assert.equal(updateStep(held.next, p, 2000, false).notice?.kind, "available", "offered once the build ends");
  assert.equal(updateStep({}, { ...p, installed: "2.0.3" }, 1000, false).notice, null, "marker current: nothing");
});

test("a second click while a clicked install runs does nothing", () => {
  assert.equal(clickFree(0, 1000), true);
  assert.equal(clickFree(1000, 1000 + BUSY_MS - 1), false, "a second click while an install runs: nothing");
  assert.equal(clickFree(1000, 1000 + BUSY_MS), true, "a claim past BUSY_MS no longer holds");
});

test("an install that exits 0 but still reads older is recorded stuck and never auto-installed again", () => {
  const p = { installed: "0.0.0-dev", bundled: "2.6.0", notes: "" };
  const after = updateStep({ installing: 900 }, p, 1000, true, true);
  assert.deepEqual([after.install, after.notice?.kind, after.next.failed?.stuck, after.next.installing], [false, "stuck", true, 0]);
  const hourLater = updateStep(after.next, p, 1000 + 3600e3, true);
  assert.deepEqual([hourLater.install, hourLater.notice], [false, null], "no loop, no repeat notice");
  assert.equal(updateStep(after.next, { ...p, bundled: "2.7.0" }, 2000, true).install, true, "a newer version is tried again");
});

// b507: the drawer's ×, a row choice, and a press outside it while open close it; a press outside a shut drawer does nothing.
test("the Needs-you drawer closes on its ×, a row choice, or a press outside while open, and still opens as before (b507)", () => {
  const closed = peekClosed(2);
  assert.deepEqual(closed, { n: 2, qa: false });
  assert.equal(peekDismissed(closed, 2, false), closed, "closed: hidden at the same count");
  assert.equal(peekDismissed(closed, 3, false), null, "a new count opens it again");
  assert.deepEqual(peekPressOutside(null, true, 2), closed, "a press outside the open drawer closes it");
  assert.equal(peekPressOutside(null, false, 2), null, "negative: a press outside a shut drawer leaves it as it was");
  assert.equal(peekDismissed(peekPressOutside(null, false, 2), 2, false), null, "negative: so the drawer still opens as today");
  const src = readFileSync(join(dirname(fileURLToPath(import.meta.url)), "..", "..", "herdr-desktop", "mods", "ah2", "main.js"), "utf8");
  const peek = src.slice(src.indexOf("  peek(t) {"), src.indexOf("  // ---- shared pieces ----"));
  assert.match(peek, /Button\.new\("ah2-peek-close"\)[\s\S]*?on_click\(\(_e, cx\) => \{\s*this\.dismissed = peekClosed\(d\.needsYou\);/, "the × closes it");
  assert.match(peek, /this\.dismissed = peekClosed\(d\.needsYou\);\s*openView\("qa"\);/, "a row runs its action and closes it");
  assert.match(peek, /on_mouse_down_out\([\s\S]*?peekPressOutside\(this\.dismissed, placement\(\)\?\.hovered === true, d\.needsYou\)/, "a press outside, gated on herdr's hovered");
});

test("a Needs-you row choice hides the drawer until the count changes or Q&A closes (atp-1704)", () => {
  const chose = { n: 2, qa: false };
  assert.equal(peekDismissed(null, 2, false), null, "not dismissed stays not dismissed");
  assert.equal(peekDismissed(chose, 2, false), chose, "same count, Q&A not open yet: still hidden");
  const seen = peekDismissed(chose, 2, true);
  assert.deepEqual(seen, { n: 2, qa: true }, "Q&A open: still hidden, and seen");
  assert.equal(peekDismissed(seen, 2, true), seen, "Q&A still open: still hidden");
  assert.equal(peekDismissed(seen, 2, false), null, "Q&A closed: the drawer is back");
  assert.equal(peekDismissed(chose, 3, false), null, "the count changed: the drawer is back");
  assert.equal(peekDismissed(seen, 1, true), null, "a count change clears it even while Q&A shows");
  assert.deepEqual(chose, { n: 2, qa: false }, "the choice record is not changed in place");
});

test("the About section names the product and shows the versions the update check holds, or a dash", () => {
  assert.equal(ABOUT.title, "Wrangl: Agent Hierarchy");
  assert.match(ABOUT.url, /^https:\/\/github\.com\/agentic-tool-labs\/wrangl$/);
  assert.equal(aboutVersions({ installed: "2.0.0", bundled: "2.0.1" }), "mod v2.0.0 · ah v2.0.1");
  assert.equal(aboutVersions(undefined), "mod — · ah —");
});
