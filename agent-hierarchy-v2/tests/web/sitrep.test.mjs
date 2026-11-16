// ah-ui-ia U6: the Sitrep dialog and the `ah.sitrep.seen` store, on the mock `lanes` scenario ("ship" and two confirmed lanes).
import { test } from "node:test";
import assert from "node:assert/strict";

process.env.AH_MOCK_FIXTURE = "lanes";
const { mountApp, until, find, text, DIR } = await import("./mount-app.mjs");
const { snapOf, loadSeen, saveSeen, marksOf, loadRegions, saveRegions, TEAM_CAP } = await import("../../web/ui/sitrep-seen.js");
const { stalledOf } = await import("../../web/ui/plans-model.js");
const { countText, decisionItem, detailOf, workCount } = await import("../../web/ui/sitrep.js");
const { itemTarget } = await import("../../web/ui/board-kanban.js");

test("a decision row's link finds its team's decision card; negative: another team's, or the decision's own id, finds none", () => {
  const cards = [{ id: "team:d/a|decision" }];
  assert.equal(itemTarget(cards, decisionItem("team:d/a"), new Map()), "team:d/a|decision");
  assert.equal(itemTarget(cards, decisionItem("team:d/b"), new Map()), null);
  assert.equal(itemTarget(cards, "q-1", new Map()), null);
});

const SHIP = `team:${DIR}/ship`;
const memStorage = () => {
  const stored = new Map();
  return { stored, getItem: (k) => stored.get(k) ?? null, setItem: (k, v) => stored.set(k, String(v)) };
};
const body = (c, p, n, asOf = 5) => ({ as_of: asOf, completed: c.map((key) => ({ key })), in_progress: p.map((key) => ({ key })), up_next: n.map((key) => ({ key })) });
const dialogOf = (root) => find(root, "section").find((s) => s.getAttribute("role") === "dialog") ?? null;
const sitrepBtn = (root) => find(root, "button").find((b) => (b.getAttribute("class") ?? "").includes("sitrep-btn")) ?? null;

test("seen store: no snap gives no marks; a new key is new; a column move is moved; keys gone are counted", () => {
  const b = body(["a", "ship:x:main"], ["b"], ["c"]);
  assert.deepEqual([...marksOf(null, b).marks], []);
  assert.equal(marksOf(null, b).changed, false);
  const m = marksOf({ a: "c", b: "n", gone: "p" }, b);
  assert.deepEqual(m.marks.get("ship:x:main"), { kind: "new" });
  assert.deepEqual(m.marks.get("b"), { kind: "moved", from: "n" });
  assert.deepEqual(m.marks.get("c"), { kind: "new" });
  assert.equal(m.marks.has("a"), false);
  assert.equal(m.left, 1);
});

test("seen store: an unchanged reopen gives no marks and the same as_of; storage round-trips per team", () => {
  const s = memStorage();
  const b = body(["a"], ["b"], ["c"], 1234);
  assert.equal(saveSeen(SHIP, b, 10, s), true);
  const snap = loadSeen(SHIP, s);
  assert.deepEqual(snap, { a: "c", b: "p", c: "n" });
  const again = body(["a"], ["b"], ["c"], 1234);
  assert.equal(marksOf(snap, again).changed, false);
  assert.equal(marksOf(snap, again).marks.size, 0);
  assert.equal(again.as_of, b.as_of);
  assert.equal(loadSeen("team:other/x", s), null);
});

test("seen store: no storage, or storage that throws, never throws", () => {
  const throwing = { getItem() { throw new Error("denied"); }, setItem() { throw new Error("denied"); } };
  assert.equal(loadSeen(SHIP, undefined), null);
  assert.equal(loadSeen(SHIP, throwing), null);
  assert.equal(saveSeen(SHIP, body([], [], []), 1, undefined), false);
  assert.equal(saveSeen(SHIP, body([], [], []), 1, throwing), false);
  const junk = memStorage();
  junk.setItem("ah.sitrep.seen", "{not json");
  assert.equal(loadSeen(SHIP, junk), null);
  assert.equal(saveSeen(SHIP, body(["a"], [], []), 1, junk), true);
});

test("seen store: capped at TEAM_CAP teams, the oldest look evicted", () => {
  const s = memStorage();
  for (let i = 0; i <= TEAM_CAP; i++) {
    saveSeen(`team:d/t${i}`, body([`k${i}`], [], []), 1000 + i, s);
  }
  const all = JSON.parse(s.stored.get("ah.sitrep.seen"));
  assert.equal(Object.keys(all).length, TEAM_CAP);
  assert.equal("team:d/t0" in all, false);
  assert.ok(`team:d/t${TEAM_CAP}` in all);
  assert.deepEqual(snapOf(body(["x"], ["y"], [])), { x: "c", y: "p" });
});

test("a capped group's heading reads N of M only when the total is more than the rows shown", () => {
  assert.equal(countText(10, 39), "10 of 39");
  assert.equal(countText(4, 4), "4");
  assert.equal(countText(3, null), "3");
  assert.equal(countText(0, 0), "0");
  assert.equal(workCount([{ kind: "ship" }, { kind: "merge" }, { kind: "item" }]), 1, "a record is no item");
  assert.equal(workCount([{ kind: "decision" }, {}]), 2, "negative: a decision and a plain row count");
});

test("seen store: Blocked and Stale rows are their own columns, so a move into Stale is marked", () => {
  const b = { ...body([], ["a"], []), blocked: [{ key: "b" }], stale: [{ key: "s" }] };
  assert.deepEqual(snapOf(b), { a: "p", b: "b", s: "s" });
  const m = marksOf({ a: "p", b: "b", s: "p" }, b);
  assert.deepEqual(m.marks.get("s"), { kind: "moved", from: "p" });
  assert.equal(m.left, 0);
});

test("stalled is the stall time reached with an eligible member idle: the one rule the Board and the Sitrep share", () => {
  assert.equal(stalledOf(100, ["impl-1"], 99), false);
  assert.equal(stalledOf(100, ["impl-1"], 100), true);
  assert.equal(stalledOf(100, [], 200), false);
  assert.equal(stalledOf(null, ["impl-1"], 200), false);
  assert.equal(stalledOf(undefined, ["impl-1"], 200), false);
});

test("a backlog key in item= selects its row in the Board's Backlog column", async () => {
  globalThis.localStorage = memStorage();
  globalThis.localStorage.setItem("ah.plans.layout", "board");
  try {
    const m = await mountApp({ view: "board", ui: { view: "board", team: SHIP } });
    await until(() => find(m.root, "article.kb-brow").length > 0, "the Backlog column");
    const key = find(m.root, "article.kb-brow")[0].getAttribute("data-id").slice("bl:".length);
    m.store.setUi({ item: key, boardSel: null });
    await until(() => find(m.root, "article.kb-brow").some((a) => a.getAttribute("data-id") === `bl:${key}` && a.getAttribute("aria-current") === "true"), "the backlog row selected by address");
    assert.equal(find(m.root, "p.kb-note").filter((p) => /is not on this board/.test(text(p))).length, 0, "no not-found note");
  } finally {
    delete globalThis.localStorage;
  }
});

test("the dialog is absent on a lane team, and sitrep=1 is removed", async () => {
  const lane = `team:${DIR}/ship-lane-board`;
  const m = await mountApp({ view: "overview", ui: { view: "overview", team: lane, sitrep: true } });
  await until(() => m.store.state.ui.sitrep === false, "the param dropped on the lane team");
  assert.ok(dialogOf(m.root) === null);
  assert.ok(sitrepBtn(m.root) === null, "no Sitrep button on a lane team");
});

test("the dialog on a top-level team: regions stacked, counts equal rows, one-line rows, records in words, the item dialog over it, focus in and back", async () => {
  const s = memStorage();
  globalThis.localStorage = s;
  try {
    const m = await mountApp({ view: "overview", ui: { view: "overview", team: SHIP } });
    await until(() => sitrepBtn(m.root) !== null, "the header Sitrep button");
    const slice = () => m.store.state.slices.get(`sitrep:${DIR}/ship`);
    // A baseline of everything but the first record, so that record (and only it) is new.
    m.store.setUi({ sitrep: true });
    await until(() => slice() && find(dialogOf(m.root) ?? m.root, "li.sr-item").length > 0, "the Sitrep rows");
    const b = slice().body;
    const firstRecord = b.completed.find((r) => r.kind !== "item");
    assert.ok(firstRecord, "the fixture has records");
    m.store.setUi({ sitrep: false });
    await until(() => dialogOf(m.root) === null, "closed");
    const snap = JSON.parse(s.stored.get("ah.sitrep.seen"))[SHIP].snap;
    delete snap[firstRecord.key];
    s.setItem("ah.sitrep.seen", JSON.stringify({ [SHIP]: { looked: 1, snap } }));

    const btn = sitrepBtn(m.root);
    btn.focus();
    btn.fire("click");
    await until(() => dialogOf(m.root) && find(dialogOf(m.root), "li.sr-item").length > 0, "reopened");
    const d = dialogOf(m.root);
    assert.ok(d.contains(globalThis.document.activeElement), "focus moved into the dialog");

    const regions = () => find(dialogOf(m.root), "section").filter((x) => (x.getAttribute("class") ?? "").includes("sr-col"));
    const named = (label) => regions().find((c) => find(c, "h3")[0].getAttribute("data-text") === label);
    const labels = regions().map((c) => find(c, "h3")[0].getAttribute("data-text"));
    assert.deepEqual(labels.slice(0, 3), ["Completed", "In progress", "Up next"], "the owner's three first, in their order");
    assert.ok(labels.includes("Blocked") && labels.includes("Stale"), "the groups outside the three still show");
    assert.equal(find(named("Stale"), "li.sr-item").length, 0, "Stale starts closed");
    assert.equal(find(named("Stale"), "button.sr-head")[0].getAttribute("aria-expanded"), "false");
    find(named("Stale"), "button.sr-head")[0].fire("click");
    await until(() => find(named("Stale"), "li.sr-item").length > 0, "its head opens it");
    for (const [label, list, total] of [["Completed", b.completed, null], ["In progress", b.in_progress, b.in_progress_total], ["Up next", b.up_next, null], ["Blocked", b.blocked, b.blocked_total], ["Stale", b.stale, b.stale_total]]) {
      assert.equal(find(named(label), "li.sr-item").length, list.length, label);
      assert.equal(text(find(named(label), "span.num")[0]), countText(workCount(list), total), `${label} counts the items drawn`);
    }
    assert.ok(b.blocked.length > 0 && b.stale.length > 0, "the fixture has blocked and stale rows");
    const inProgressKeys = new Set(b.in_progress.map((r) => r.key));
    assert.ok(b.stale.every((r) => !inProgressKeys.has(r.key)), "a stale item is not in progress");
    assert.match(text(find(d, "p.sr-status")[0]), new RegExp(`${b.completed_total} items? completed in total`));

    // A row is one control holding its title and nothing more.
    for (const r of b.up_next) {
      const li = find(named("Up next"), "li.sr-item").find((x) => text(x) === r.title);
      assert.ok(li, `the row of ${r.key} reads exactly its title`);
      assert.equal(find(li, "button.sr-row")[0].getAttribute("title"), r.title, "the full title stays reachable when it truncates");
    }
    assert.equal(find(dialogOf(m.root), "span.sr-state").length + find(dialogOf(m.root), "span.sr-mark").length + find(dialogOf(m.root), "span.sr-meta").length, 0, "no second line, pill or mark");

    const records = find(named("Completed"), "span.sr-record");
    assert.equal(records.length, b.completed.filter((r) => r.kind !== "item").length);
    for (const el of records) {
      assert.equal(el.localName, "span", "a record is neither link nor button");
      assert.match(text(el), /^(Shipped \S+ · [0-9a-f]{7}|Merged \S+ into \S+ · [0-9a-f]{7})/);
    }
    assert.ok(records.some((el) => /unconfirmed/.test(text(el))), "the unconfirmed ship says so");

    // Enter or click opens the one detail dialog over the Sitrep; Escape closes only it, and focus goes back to the row.
    const done = b.completed.find((r) => r.kind === "item");
    const row = find(named("Completed"), "button.sr-row").find((x) => text(x) === done.title);
    row.focus();
    row.fire("click");
    const detail = () => find(m.root, "section.item-detail")[0] ?? null;
    await until(detail, "the item dialog");
    assert.equal(text(find(detail(), "h2")[0]), done.title);
    assert.match(text(detail()), /RegionCompleted/);
    assert.ok(detail().contains(globalThis.document.activeElement), "focus moved into the item dialog");
    assert.equal(detail().getAttribute("aria-modal"), "true");
    assert.ok(find(detail(), "button.close").length === 1, "a close control");
    detail().fire("keydown", { key: "Escape" });
    await until(() => detail() === null, "Escape closes the item dialog");
    assert.ok(dialogOf(m.root), "the Sitrep stays open");
    assert.ok(globalThis.document.activeElement === find(named("Completed"), "button.sr-row").find((x) => text(x) === done.title), "focus back on the row");

    d.fire("keydown", { key: "Escape" });
    await until(() => dialogOf(m.root) === null, "Escape closes");
    assert.equal(m.store.state.ui.sitrep, false);
    assert.ok(globalThis.document.activeElement === sitrepBtn(m.root), "focus returned to the Sitrep button");
    assert.equal(JSON.parse(s.stored.get("ah.sitrep.seen"))[SHIP].snap[firstRecord.key], "c", "the close wrote what was seen");
  } finally {
    delete globalThis.localStorage;
  }
});

test("a region outside the three shows only while it holds rows; the three show with 0 when empty", async () => {
  globalThis.localStorage = memStorage();
  try {
    const m = await mountApp({ view: "overview", ui: { view: "overview", team: SHIP, sitrep: true } });
    const id = `sitrep:${DIR}/ship`;
    await until(() => m.store.state.slices.get(id) && dialogOf(m.root), "the Sitrep open");
    const region = (cls) => find(dialogOf(m.root), "section").find((s) => (s.getAttribute("class") ?? "").split(" ").includes(cls));
    const body = { ...m.store.state.slices.get(id).body, completed: [], completed_total: 0, in_progress: [], in_progress_total: 0, up_next: [], up_next_old: [], up_next_old_total: 0 };
    m.model.publish([{ id, as_of: Date.now(), body }]);
    await until(() => /Nothing completed yet\./.test(text(region("sr-c"))), "Completed empty");
    for (const [cls, label, line] of [["sr-c", "Completed", "Nothing completed yet."], ["sr-p", "In progress", "Nothing in progress."], ["sr-n", "Up next", "Nothing queued."]]) {
      assert.equal(text(find(region(cls), "h3")[0]).replace("▾", "").replace(/\s+/g, " ").trim(), `${label} 0`);
      assert.match(text(region(cls)), new RegExp(line.replace(".", "\\.")));
    }
    assert.equal(region("sr-o"), undefined, "an empty Up next, old is not drawn");
    const row = { kind: "item", key: "s:old", title: "Old item", state: "ready", role: "implementor" };
    m.model.publish([{ id, as_of: Date.now(), body: { ...body, up_next_old: [row], up_next_old_total: 1 } }]);
    await until(() => region("sr-o"), "it shows with a row");
    assert.equal(find(region("sr-o"), "li.sr-item").length, 0, "closed at first");
    find(region("sr-o"), "button.sr-head")[0].fire("click");
    await until(() => find(region("sr-o"), "li.sr-item").length === 1, "the row");
    assert.equal(text(find(region("sr-o"), "li.sr-item")[0]), "Old item");
    const order = () => find(dialogOf(m.root), "section").map((s) => (s.getAttribute("class") ?? "").split(" ").find((c) => /^sr-[a-z]$/.test(c)));
    m.model.publish([{ id, as_of: Date.now(), body: { ...body, regions: [...body.regions].reverse() } }]);
    await until(() => order().indexOf("sr-n") < order().indexOf("sr-p") && order().indexOf("sr-p") < order().indexOf("sr-c"), "the served order, not one of its own");
    m.model.publish([{ id, as_of: Date.now(), body: { ...body, up_next: [row], regions: [] } }]);
    await until(() => order().length === 0, "negative: no served region draws no region, even with rows");
  } finally {
    delete globalThis.localStorage;
  }
});

test("a collapsed region stays collapsed for this viewer, in another open of the Sitrep; the three start open", async () => {
  const s = memStorage();
  globalThis.localStorage = s;
  try {
    const m = await mountApp({ view: "overview", ui: { view: "overview", team: SHIP, sitrep: true } });
    const head = () => find(find(dialogOf(m.root), "section").find((x) => (x.getAttribute("class") ?? "").split(" ").includes("sr-c")), "button.sr-head")[0];
    await until(() => dialogOf(m.root) && find(dialogOf(m.root), "li.sr-item").length > 0, "the rows");
    assert.equal(head().getAttribute("aria-expanded"), "true", "open at first");
    assert.equal(head().getAttribute("aria-controls"), "sr-r-c");
    head().fire("click");
    await until(() => head().getAttribute("aria-expanded") === "false", "collapsed");
    m.store.setUi({ sitrep: false });
    await until(() => dialogOf(m.root) === null, "closed");
    m.store.setUi({ sitrep: true });
    await until(() => dialogOf(m.root) && head(), "reopened");
    assert.equal(head().getAttribute("aria-expanded"), "false", "remembered");
    assert.equal(find(find(dialogOf(m.root), "section").find((x) => (x.getAttribute("class") ?? "").split(" ").includes("sr-c")), "li.sr-item").length, 0);
  } finally {
    delete globalThis.localStorage;
  }
});

test("the regions store: junk, absent or throwing storage means the defaults and never throws", () => {
  const throwing = { getItem() { throw new Error("denied"); }, setItem() { throw new Error("denied"); } };
  assert.deepEqual(loadRegions(undefined), {});
  assert.deepEqual(loadRegions(throwing), {});
  assert.equal(saveRegions({ c: false }, throwing), false);
  const junk = memStorage();
  junk.setItem("ah.sitrep.regions", "{not json");
  assert.deepEqual(loadRegions(junk), {});
  assert.equal(saveRegions({ c: false }, junk), true);
  assert.deepEqual(loadRegions(junk), { c: false });
});

test("the detail reads state, holder, note and log from the row's plan item; without one it says what the row knows and has no log", () => {
  const row = { key: "s:i1", title: "T", team: "ship", stream: "s", holder: "impl", state: "in_progress" };
  const held = detailOf(row, "p", "In progress", { items: [{ key: "i1", state: "blocked", holder: "qa", note: "A note", work_id: "ah-12" }] }, "d");
  assert.equal(held.log, "item-log:d/12");
  assert.equal(held.note, "A note");
  assert.deepEqual(held.holder, { id: "member:d/ship/qa", name: "qa" });
  assert.ok(held.rows.some(([k, v]) => k === "State" && v === "blocked"), "the plan's state wins and reads as a word");
  assert.equal(held.kind, "Item · ah-12");
  const bare = detailOf(row, "p", "In progress", { items: [{ key: "other", work_id: "ah-9" }] }, "d");
  assert.equal(bare.log, null, "another item's work id is never borrowed");
  assert.equal(bare.note, null);
  assert.ok(bare.rows.some(([k, v]) => k === "State" && v === "in progress"));
  assert.equal(detailOf({ ...row, state: undefined, holder: null }, "n", "Up next", null, "d").holder, null);
});

test("a capped group with more open rows than it shows reads N of M in its heading", async () => {
  globalThis.localStorage = memStorage();
  try {
    const m = await mountApp({ view: "overview", ui: { view: "overview", team: SHIP, sitrep: true } });
    const id = `sitrep:${DIR}/ship`;
    await until(() => m.store.state.slices.get(id) && dialogOf(m.root), "the Sitrep open");
    const rows = Array.from({ length: 10 }, (_, i) => ({ kind: "item", key: `s:${i}`, title: `Item ${i}`, state: "in_progress", since: Date.now() - 60_000 }));
    const body = { ...m.store.state.slices.get(id).body, in_progress: rows, in_progress_total: 39 };
    m.model.publish([{ id, as_of: Date.now(), body }]);
    const head = () => find(find(dialogOf(m.root), "section").find((s) => (s.getAttribute("class") ?? "").includes("sr-p")), "h3")[0];
    await until(() => head() && /10 of 39/.test(text(head())), "the In progress heading reads 10 of 39");
  } finally {
    delete globalThis.localStorage;
  }
});
