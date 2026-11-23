// The ah2 herdr mod's settings and self-update rules (atp-1234): mods/ah2/settings-model.js, which imports no Herdr code.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { settingsOf, shownTeams, displayOrder, teamMove, versionNewer, changelogEntry, updateStep, peekDismissed, peekClosed, peekPressOutside, deskRoute, unionSubs, qaShowing, deskWindowOpen, deskWindowSpec, DESK_WINDOW_SIZE, DESK_WINDOW_MAX, ABOUT, aboutVersions, updateNotify, clickFree, INSTALL_COMMAND, BUSY_MS, buildHolds, listen, isInstallCommand, updateDialogOf, outputOf, claimFresh, CLAIM_MS } from "../../herdr-desktop/mods/ah2/settings-model.js";
import { rebuildRead } from "../../herdr-desktop/mods/ah2/rebuild-model.js";

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

// desk-per-team D1: a team's Desk is its own window, keyed by the team; no team is today's all-teams spec exactly.
test("deskWindowSpec with a team keys the window by the team; two teams two keys; no team is today's spec", () => {
  const a = deskWindowSpec("sitrep", false, "d1/ah2");
  assert.deepEqual([a.key, a.props.team, a.props.view, a.props.tab, a.width], ["ah2-desk:d1/ah2", "d1/ah2", "desk", "sitrep", DESK_WINDOW_SIZE.width]);
  assert.equal(deskWindowSpec("sitrep", true, "d1/ah2").width, DESK_WINDOW_MAX.width);
  assert.notEqual(deskWindowSpec("qa", false, "d1/lane-a").key, a.key, "two teams' Desks never share a key");
  // negative: no team (or a null one) gives exactly the all-teams spec
  const today = { mod: "ah2", props: { view: "desk", tab: "qa", max: false }, kind: "window", key: "ah2-desk", ...DESK_WINDOW_SIZE };
  assert.deepEqual(deskWindowSpec("qa", false), today);
  assert.deepEqual(deskWindowSpec("qa", false, null), today);
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
  assert.deepEqual(s, { tab: "agents-list", strip: { desk: false }, hidden: ["d/x"], lanes: "nested", interval: 0, notify: false, autoUpdate: false, autoQa: false, placement: "column", ahOn: "auto", pinned: [], order: [] });
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

// ah2-401 I1, I4: one failed nextCommand() does not end the listener; it waits before asking again; both spellings install.
test("the command listener keeps listening after a rejection", async () => {
  const logged = [], slept = [];
  const replies = [() => Promise.reject(new Error("gone")), () => Promise.resolve("ah2.install")];
  let got;
  const clicked = new Promise((r) => { got = r; });
  listen(() => ((replies.shift() ?? (() => new Promise(() => {})))()), (c) => isInstallCommand(c) && got(c), async (ms) => { slept.push(ms); }, (e) => logged.push(e.message));
  assert.equal(await clicked, "ah2.install");
  assert.deepEqual([logged, slept], [["gone"], [1000]], "logged once, one wait, then listening again");
  assert.equal(isInstallCommand("install"), true);
  assert.equal(isInstallCommand("ah2.other"), false, "no new command");
});

// ah2-401 I2, I3: a check before the sidebar's first read takes the stamp through the sidebar's reader, and the hold knows the version.
test("a check before the sidebar's first read during a build holds the notice", async () => {
  const p = { installed: "2.0.2", bundled: "2.0.3", notes: "" };
  const stamp = JSON.stringify({ version: "2.0.2", last_rebuild: { version: "2.0.3", at: "2026-11-21T00:00:00Z", state: "started" } });
  const { now } = await rebuildRead({ key: null, home: null }, "local", async () => JSON.stringify({ home: "/h" }), async (f) => (f === "/h/install.json" ? stamp : null));
  const t = Date.parse("2026-11-21T00:01:00Z");
  assert.equal(updateStep({}, p, 1000, false, false, buildHolds(now, p.bundled, t)).notice, null);
  assert.equal(buildHolds({ ...now, version: "2.0.4" }, p.bundled, t), true, "a newer build holds too");
});

test("a build for an older version does not hold the notice", () => {
  const p = { installed: "2.0.2", bundled: "2.0.3", notes: "" };
  const older = { version: "2.0.2", at: "2026-11-21T00:00:00Z", state: "started", stamp: null };
  assert.equal(updateStep({}, p, 1000, false, false, buildHolds(older, p.bundled, Date.parse("2026-11-21T00:01:00Z"))).notice?.kind, "available");
});

// ah2-402 b224: a "started" stamp older than REBUILD_WATCH_MS is a crashed build, not one running.
test("an old started stamp does not hold the notice and a young one does", () => {
  const b = { version: "2.0.3", at: "2026-11-21T00:00:00Z", state: "started", stamp: null };
  const at = Date.parse(b.at);
  assert.equal(buildHolds(b, "2.0.3", at + 10 * 60e3), false, "10 minutes on: not running");
  assert.equal(buildHolds(b, "2.0.3", at + 10 * 60e3 - 1), true, "negative: just under 10 minutes still holds");
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

// ah2-192: the update dialog is drawn from REBUILD_KEY and UPDATE_KEY alone.
test("the update dialog: building, then installing, a failed install's reason, and nothing running", () => {
  const now = Date.parse("2026-11-18T12:00:00Z");
  const started = { version: "2.0.13", at: "2026-11-18T11:59:00Z", state: "started" };
  const old = { installed: "2.0.12", bundled: "2.0.12" };
  const states = (d) => d.steps.map((s) => s.state);

  const building = updateDialogOf(started, old, now);
  assert.deepEqual(states(building), ["running", "waiting"]);
  assert.equal(building.steps[0].label, "Build the daemon (ahd 2.0.13)");
  assert.equal(building.steps[0].word, "Running…");
  assert.equal(building.steps[1].label, "Install the herdr mod (2.0.13)", "the version the build makes");
  assert.equal(building.heading, "Updating Wrangl to 2.0.13…");
  assert.equal(building.want, true);

  const ready = { ...started, state: "ok" };
  const installing = updateDialogOf(ready, { installed: "2.0.12", bundled: "2.0.13", installing: now - 5000 }, now);
  assert.deepEqual(states(installing), ["done", "running"]);
  assert.equal(installing.steps[1].label, "Install the herdr mod (2.0.13)");
  assert.equal(installing.heading, "Updating Wrangl to 2.0.13…");

  const failed = updateDialogOf(ready, { installed: "2.0.12", bundled: "2.0.13", failed: { version: "2.0.13", text: "herdr home not writable", at: now - 1000 } }, now, now, "/h");
  assert.deepEqual(states(failed), ["done", "failed"]);
  assert.equal(failed.steps[1].reason, "herdr home not writable");
  assert.equal(failed.heading, "The update did not finish.");
  assert.equal(updateDialogOf({ ...started, state: "failed" }, old, now, now, "/h").steps[0].reason, "The build failed; see /h/logs/rebuild.log");

  const landed = updateDialogOf(ready, { installed: "2.0.13", bundled: "2.0.13", landed: now - 2000 }, now);
  assert.deepEqual([states(landed), landed.heading, landed.finished], [["done", "done"], "Wrangl 2.0.13 is installed.", true]);

  const idle = updateDialogOf({ ...started, state: "ok", at: "2026-11-17T08:00:00Z" }, old, now);
  assert.deepEqual([states(idle), idle.want], [["none", "none"], false], "nothing running: no dialog wanted");
  assert.deepEqual(updateDialogOf(null, null, now).steps.map((s) => s.label), ["Build the daemon", "Install the herdr mod"], "no version: none shown");
});

test("the update dialog offers Install now once the build is done and a newer mod waits for the user, auto-update off (D11)", () => {
  const now = Date.parse("2026-11-18T12:00:00Z");
  const ready = { version: "2.0.13", at: "2026-11-18T11:59:00Z", state: "ok" };
  const newer = { installed: "2.0.12", bundled: "2.0.13" };
  const d = updateDialogOf(ready, newer, now);
  assert.deepEqual([d.offer, d.heading, d.steps[1].word], [true, "ahd 2.0.13 is ready. The herdr mod is not installed yet.", "Waiting for you"]);
  assert.equal(updateDialogOf(ready, newer, now, now, null, true).offer, false, "auto-update on: no offer");
  assert.equal(updateDialogOf(ready, { ...newer, installing: now - 1000 }, now).offer, false, "an install running: no offer");
  assert.equal(updateDialogOf(ready, { installed: "2.0.13", bundled: "2.0.13" }, now).offer, false, "nothing to install: no offer");
  assert.equal(updateDialogOf({ ...ready, state: "started" }, newer, now).offer, false, "the build still runs: no offer");
});

test("the update dialog shows install output only for the update it shows (Amendment 3)", () => {
  const now = Date.parse("2026-11-18T12:00:00Z");
  const started = { version: "2.0.13", at: "2026-11-18T11:59:00Z", state: "started" };
  const earlier = { installed: "2.0.12", bundled: "2.0.12", output: "installed 2.0.12", landed: now - 86400e3 };
  assert.equal(updateDialogOf(started, earlier, now).output, null, "a new build: yesterday's install output is not shown");
  assert.equal(updateDialogOf({ ...started, state: "ok" }, { ...earlier, installed: "2.0.13", bundled: "2.0.13", landed: now - 1000 }, now).output, "installed 2.0.12", "this update's install: shown");
});

test("the update dialog's output keeps only its last lines, the build log's first, and is claimed once", () => {
  const log = Array.from({ length: 400 }, (_, i) => `build ${i}`).join("\n");
  const out = outputOf(log, "installed\nwarn: kept\n", false, 300);
  assert.equal(out.length, 300);
  assert.deepEqual(out.slice(-3), ["build 399", "installed", "warn: kept"]);
  assert.deepEqual(outputOf(null, null, true), ["Installing…"], "an install with no output yet");
  assert.equal(claimFresh({ overlay: "o1", at: 1000 }, 1000 + CLAIM_MS - 1), true);
  assert.equal(claimFresh({ overlay: "o1", at: 1000 }, 1000 + CLAIM_MS), false, "a claim left by a crash goes stale");
  assert.equal(claimFresh(null, 1000), false);
});

// ah2-326 §4.2: pinned first in pin order, then the ordered unpinned, then the rest as served; stale ids skipped, lanes untouched.
test("displayOrder puts pinned teams first, then the chosen order, then new teams in served order", () => {
  const lane = { id: "d/l", lanes: [] };
  const served = ["d/a", "d/b", "d/c", "d/d", "e/a"].map((id) => ({ id, lanes: id === "d/c" ? [lane] : [] }));
  const ids = (list) => list.map((t) => t.id);
  assert.deepEqual(ids(displayOrder(served, settingsOf({}))), ["d/a", "d/b", "d/c", "d/d", "e/a"], "nothing stored: served order exactly");
  const s = settingsOf({ pinned: ["d/d", "gone/x", "d/b"], order: ["d/c", "d/b", "gone/y"] });
  const out = displayOrder(served, s);
  assert.deepEqual(ids(out), ["d/d", "d/b", "d/c", "d/a", "e/a"], "pinned, ordered (a pinned id there skipped), new last; stale skipped");
  assert.equal(out.find((t) => t.id === "d/c").lanes[0], lane, "lanes untouched");
  // odd stored values are read safely: non-arrays, numbers and duplicates dropped
  const odd = settingsOf({ pinned: "d/a", order: ["d/b", 3, null, "d/b", "d/a"] });
  assert.deepEqual([odd.pinned, odd.order], [[], ["d/b", "d/a"]]);
  assert.deepEqual(ids(displayOrder(served, odd)), ["d/b", "d/a", "d/c", "d/d", "e/a"]);
  // negative: the same name in another dir is another team (e/a is not pinned by d/a)
  assert.deepEqual(ids(displayOrder(served, settingsOf({ pinned: ["e/a"] }))), ["e/a", "d/a", "d/b", "d/c", "d/d"]);
  // hidden wins over pinned
  assert.deepEqual(ids(shownTeams(displayOrder(served, settingsOf({ pinned: ["d/d"], hidden: ["d/d"] })), settingsOf({ hidden: ["d/d"] }))), ["d/a", "d/b", "d/c", "e/a"]);
});

// ah2-326 §4.3: the patch each menu choice writes; null is "item not drawn".
test("teamMove pins, unpins, moves past hidden neighbours and keeps stale ids at the end", () => {
  const served = ["d/a", "d/b", "d/c", "d/d"].map((id) => ({ id, lanes: [] }));
  const s = settingsOf({ pinned: ["d/c", "gone/p"], order: ["gone/o"] });
  assert.deepEqual(teamMove(served, s, "d/a", "pin"), { pinned: ["d/c", "gone/p", "d/a"], order: ["gone/o"] });
  assert.equal(teamMove(served, s, "d/c", "pin"), null, "already pinned: no Pin");
  assert.equal(teamMove(served, s, "d/a", "unpin"), null, "not pinned: no Unpin");
  assert.deepEqual(teamMove(served, s, "d/c", "unpin"), { pinned: ["gone/p"], order: ["d/c", "gone/o"] }, "unpinned to the start of the unpinned group");
  // unpinned group in display order: a, b, d
  assert.deepEqual(teamMove(served, s, "d/b", "up"), { order: ["d/b", "d/a", "d/d", "gone/o"] }, "only the moved group's list; stale kept at the end");
  assert.deepEqual(teamMove(served, s, "d/b", "down"), { order: ["d/a", "d/d", "d/b", "gone/o"] });
  assert.equal(teamMove(served, s, "d/a", "up"), null, "first of its group: no Move up");
  assert.equal(teamMove(served, s, "d/d", "down"), null, "last of its group: no Move down");
  assert.equal(teamMove(served, s, "d/c", "up"), null, "the only pinned team: no Move up");
  // a hidden neighbour is skipped and keeps its place
  const h = settingsOf({ hidden: ["d/b"] });
  assert.deepEqual(teamMove(served, h, "d/a", "down"), { order: ["d/c", "d/b", "d/a", "d/d"] });
  assert.equal(teamMove(served, settingsOf({ hidden: ["d/a"] }), "d/b", "up"), null, "only a hidden team above: no Move up");
  // pinned group moves write `pinned` only
  const p = settingsOf({ pinned: ["d/d", "d/a"], order: ["d/b"] });
  assert.deepEqual(teamMove(served, p, "d/a", "up"), { pinned: ["d/a", "d/d"] });
  assert.equal(teamMove(served, p, "d/a", "sideways"), null);
});
