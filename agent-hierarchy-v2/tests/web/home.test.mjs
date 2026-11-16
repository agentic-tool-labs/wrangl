// ah-ui-ia U5: Home. On the mock `lanes` scenario: "ship" with two confirmed lane teams.
import { test } from "node:test";
import assert from "node:assert/strict";

process.env.AH_MOCK_FIXTURE = "lanes";
const { mountApp, until, find, text, DIR } = await import("./mount-app.mjs");
const { homeRows, readSnooze, readEnded, stateWord, countsText, railCards } = await import("../../web/ui/teams.js");
const { summaryRow, needsState, isQuiet, firstTeam } = await import("../../web/ui/select.js");
const { globalKey } = await import("../../web/ui/keys.js");

const rowsOf = (root) => find(root, "tr").filter((r) => r.attrs.has("data-row") && !r.attrs.has("data-lane"));
const lanesOf = (root) => find(root, "tr").filter((r) => r.attrs.has("data-lane"));
const summary = (name, body) => ({ id: `team-summary:${DIR}/${name}`, as_of: Date.now(), body: { team: `team:${DIR}/${name}`, name, running: true, members: 1, working: 1, blocked: 0, needs_you: 0, alerts: [], member_states: {}, ...body } });
const fakeStorage = () => {
  const stored = new Map();
  globalThis.localStorage = { getItem: (k) => stored.get(k) ?? null, setItem: (k, v) => stored.set(k, v), removeItem: (k) => stored.delete(k) };
  return stored;
};

test("a lane team is not a row: it is a sub-row of its parent, linking to the parent's Board filtered to it", async () => {
  const m = await mountApp({ view: null });
  await until(() => lanesOf(m.root).length === 2, "the two lane sub-rows");
  assert.deepEqual(rowsOf(m.root).map((r) => text(find(r, "a.name")[0])), ["ship"]);
  const lane = lanesOf(m.root).find((r) => /lane-board/.test(text(r)));
  const link = find(lane, "a")[0];
  assert.equal(link.getAttribute("href"), `#/board?team=${encodeURIComponent(`team:${DIR}/ship`)}&lane=lane-board`);
  assert.match(text(link), /Lane of ship: lane-board/);
  assert.match(text(lane), /Working|Idle|Blocked/);
  await until(() => /sub-orchestrator so-lane-board/.test(text(lane)), "the lane's sub-orchestrator, from the parent team slice");
});

const OLD = new Date(Date.now() - 30 * 3600_000).toISOString();

test("the quiet team is hidden until the toggle is on, and the toggle persists", async () => {
  const stored = fakeStorage();
  try {
    const m = await mountApp({ view: null });
    await until(() => rowsOf(m.root).length === 1, "the ship row");
    m.model.publish([summary("old", { running: false, members: 2, working: 0, state: "ended", updated_at: OLD })]);
    await until(() => find(m.root, ".home-toggle").length === 1, "the quiet toggle");
    assert.match(text(find(m.root, ".home-toggle")[0]), /Show 1 quiet team$/);
    assert.ok(!rowsOf(m.root).some((r) => /old/.test(text(r))), "hidden by default");
    find(find(m.root, ".home-toggle")[0], "input")[0].fire("change", { target: { checked: true } });
    await until(() => rowsOf(m.root).some((r) => /old/.test(text(r)) && /Ended/.test(text(r))), "the ended team shown");
    assert.equal(stored.get("ah.home.ended"), "1");
  } finally {
    delete globalThis.localStorage;
  }
});

test("a team just ended folds at once unless something waits; a held team with nothing waiting folds at once and reads Held", async () => {
  fakeStorage();
  try {
    const m = await mountApp({ view: null });
    await until(() => rowsOf(m.root).length === 1, "the ship row");
    m.model.publish([
      summary("recent", { running: false, members: 2, working: 0, state: "ended", updated_at: new Date().toISOString() }),
      summary("waits", { running: false, members: 0, working: 0, state: "needs_you", waiting_on_you: 1, waiting_rows: [], updated_at: OLD }),
      summary("paused", { held: true, working: 0, updated_at: new Date(Date.now() - 1000).toISOString() }),
    ]);
    await until(() => rowsOf(m.root).some((r) => /waits/.test(text(r))), "a wait with no one running is not quiet");
    assert.ok(!rowsOf(m.root).some((r) => /recent/.test(text(r))), "ended with nothing waiting is quiet at once");
    assert.ok(!rowsOf(m.root).some((r) => /paused/.test(text(r))), "held with nothing waiting is quiet");
    assert.match(text(find(m.root, ".home-toggle")[0]), /Show 2 quiet teams$/);
  } finally {
    delete globalThis.localStorage;
  }
});

test("isQuiet and the first team read only the served instant", () => {
  assert.equal(isQuiet(0, 5), false, "no instant");
  assert.equal(isQuiet(6, 5), false, "before");
  assert.equal(isQuiet(5, 5), true, "at");
  const past = "2020-01-01T00:00:00.000Z";
  const team = (n) => ({ id: `team:${DIR}/${n}`, body: {} });
  const sum = (n, quiet_at) => ({ id: `team-summary:${DIR}/${n}`, body: { quiet_at } });
  const map = (...s) => new Map(s.map((x) => [x.id, x]));
  const live = map(team("a"), sum("a", past), team("b"), sum("b", null));
  assert.equal(firstTeam(live, {}).id, `team:${DIR}/b`, "the first not quiet");
  assert.equal(firstTeam(live, { team: `team:${DIR}/a` }).id, `team:${DIR}/a`, "ui.team wins, quiet or not");
  assert.equal(firstTeam(map(team("a"), sum("a", past), team("b"), sum("b", past)), {}).id, `team:${DIR}/a`, "all quiet: the first");
  assert.equal(firstTeam(map(team("a")), {}).id, `team:${DIR}/a`, "no summary yet: not quiet");
  assert.equal(firstTeam(new Map(), {}), null);
});

test("Held is a served word, first; a team not held reads its state", () => {
  assert.equal(stateWord(summaryRow(summary("h", { held: true, state: "working" }))), "Held");
  assert.notEqual(stateWord(summaryRow(summary("h", { held: false, state: "working" }))), "Held");
  assert.notEqual(stateWord(summaryRow(summary("h", { state: "working" }))), "Held");
});

test("a team that only claims a lane is a top-level row with the not-confirmed note and no Sitrep button", async () => {
  const m = await mountApp({ view: null });
  await until(() => rowsOf(m.root).length === 1, "the ship row");
  m.model.publish([summary("rogue", { lane_claim: { parent_team: "ship" } })]);
  await until(() => rowsOf(m.root).some((r) => /rogue/.test(text(r))), "the rogue row");
  const rogue = rowsOf(m.root).find((r) => /rogue/.test(text(r)));
  assert.match(text(rogue), /says it is a lane of ship \(not confirmed\)/);
  assert.equal(find(rogue, "button").filter((b) => text(b) === "Sitrep").length, 0, "no Sitrep button");
  assert.equal(find(rowsOf(m.root).find((r) => /ship/.test(text(find(r, "a")[0]))), "button").filter((b) => text(b) === "Sitrep").length, 1, "ship keeps its Sitrep");
});

test("state words and member counts", () => {
  const row = (body) => summaryRow(summary("t", body));
  assert.equal(stateWord(row({ state: "ended", ended: true, running: false })), "Ended");
  assert.equal(stateWord(row({ working: 1, blocked: 1 })), "Working");
  assert.equal(stateWord(row({ working: 0, blocked: 1 })), "Blocked");
  assert.equal(stateWord(row({ working: 0 })), "Idle");
  const states = { a: "working", b: "working", c: "idle", d: "blocked", e: "unknown", f: "gone" };
  assert.equal(countsText(row({ member_states: Object.fromEntries(Object.entries(states).map(([k, st]) => [`member:${DIR}/t/${k}`, { state: st }])) })), "2 working · 1 blocked · 1 idle · 1 no signal");
  assert.equal(countsText(row({ members: 4, working: 2, blocked: 1, member_states: undefined })), "2 working · 1 blocked · 1 idle");
});

test("an old Later list reads as snoozed for 24 h from this load and is written back as {key: until}", () => {
  const stored = fakeStorage();
  try {
    stored.set("ah-later", JSON.stringify(["a", "b"]));
    const now = 1_000_000;
    assert.deepEqual(readSnooze(now), { a: now + 86_400_000, b: now + 86_400_000 });
    assert.deepEqual(JSON.parse(stored.get("ah-later")), { a: now + 86_400_000, b: now + 86_400_000 });
    assert.deepEqual(readSnooze(now + 86_400_001), {}, "expired snoozes drop");
    delete globalThis.localStorage;
    assert.deepEqual(readSnooze(now), {}, "no storage, no throw");
    for (const bad of ["{", '"x"', '{"a":"soon"}', "null", "7"]) {
      fakeStorage().set("ah-later", bad);
      assert.deepEqual(readSnooze(now), {}, `corrupt ah-later ${bad}`);
    }
    fakeStorage().set("ah-later", JSON.stringify({ k: 1e300 }));
    assert.deepEqual(readSnooze(now), { k: now + 86_400_000 }, "a snooze is never longer than 24 h");
    for (const bad of ["{", "true", "yes", "0"]) {
      fakeStorage().set("ah.home.ended", bad);
      assert.equal(readEnded(), false, `corrupt ah.home.ended ${bad}`);
    }
  } finally {
    delete globalThis.localStorage;
  }
});

const TEAM = `team:${DIR}/ship`;
// moveRow's lookup on the fake DOM, which has no document.querySelectorAll
const docOf = (m) => ({ body: globalThis.document.body, get activeElement() { return globalThis.document.activeElement; }, querySelectorAll: (sel) => (sel === "[data-row]" ? find(m.root, "tr").filter((r) => r.attrs.has("data-row")) : []) });

test("F1: ArrowDown on Home focuses the first row, then its lane rows; Enter on a focused row opens it", async () => {
  const m = await mountApp({ view: null });
  await until(() => lanesOf(m.root).length === 2, "the lane rows");
  globalThis.document.activeElement = null;
  const doc = docOf(m);
  const down = () => globalKey({ key: "ArrowDown", target: globalThis.document.body, preventDefault() {} }, m.store, doc);
  down();
  const first = globalThis.document.activeElement;
  assert.ok(first === rowsOf(m.root)[0], "the ship row has focus");
  assert.equal(first.getAttribute("tabindex"), "0", "a focusable row");
  down();
  assert.ok(globalThis.document.activeElement === lanesOf(m.root)[0], "then its first lane");
  assert.equal(lanesOf(m.root)[0].getAttribute("tabindex"), "0");
  lanesOf(m.root)[0].fire("keydown", { key: "Enter", target: lanesOf(m.root)[0], currentTarget: lanesOf(m.root)[0] });
  assert.equal(m.store.state.ui.view, "board");
  assert.equal(m.store.state.ui.lane, "lane-board");
  m.store.setUi({ view: "teams", team: null, lane: null });
  await until(() => rowsOf(m.root).length === 1, "Home again");
  const row = rowsOf(m.root)[0];
  row.fire("keydown", { key: "Enter", target: row, currentTarget: row });
  assert.equal(m.store.state.ui.view, "overview");
  assert.equal(m.store.state.ui.team, TEAM);
});

test("F2: the rail's cards are the Inbox's set: a team's open decisions get a card, and the counts agree", async () => {
  const stored = fakeStorage();
  try {
    const m = await mountApp({ view: null });
    // the lanes mock: ship has 4 open questions, served in its summary, and one lane member is blocked
    await until(() => find(m.root, "article.need-card").some((c) => /Waiting on you · 4/.test(text(c))), "the decision card from the summary");
    const cards = railCards(m.store.state.slices);
    const n = cards.reduce((k, c) => k + (c.kind === "Decision" ? Number(/· (\d+)/.exec(c.title)[1]) : 1), 0);
    // atp-1736 (Orchestrator ruling, request 20261113-152814-1cyj): the cards are what waits on you plus what is blocked
    const st = needsState(m.store.state.slices, null);
    assert.equal(n, st.n + st.blocked, "cards cover exactly Needs you + blocked");
    for (;;) {
      const card = find(m.root, "article.need-card")[0];
      if (!card) break;
      find(card, "button").find((b) => text(b) === "Snooze 24 h").fire("click");
      await until(() => !find(m.root, "article.need-card").includes(card), "the card to go");
    }
    const empty = text(find(m.root, "aside.teams-rail")[0]);
    assert.match(empty, new RegExp(`All ${cards.length} snoozed\\.`));
    assert.doesNotMatch(empty, /Nothing is waiting on you/);
    assert.ok(stored.get("ah-later"));
  } finally {
    delete globalThis.localStorage;
  }
});

test("F3: with quiet teams hidden, a quiet team with a live lane stays (word Ended) and only its quiet lanes go", async () => {
  const m = await mountApp({ view: null });
  await until(() => rowsOf(m.root).length === 1, "the ship row");
  m.model.publish([
    summary("p", { running: false, working: 0, state: "ended", updated_at: OLD }),
    summary("p-live", { lane: { parent_team: "p", stream: "live" } }),
    summary("p-done", { running: false, working: 0, state: "ended", updated_at: OLD, lane: { parent_team: "p", stream: "done" } }),
  ]);
  await until(() => rowsOf(m.root).some((r) => /^p\b/.test(text(find(r, "a")[0]))), "the ended parent kept for its live lane");
  const p = rowsOf(m.root).find((r) => /^p\b/.test(text(find(r, "a")[0])));
  assert.match(text(p), /Ended/);
  assert.ok(lanesOf(m.root).some((r) => /live/.test(text(r))), "the live lane shows");
  assert.ok(!lanesOf(m.root).some((r) => /Lane of p: done/.test(text(r))), "the ended lane is hidden");
  assert.match(text(find(m.root, ".home-toggle")[0]), /Show 1 quiet team$/, "only the hidden lane is counted");
});

test("the row names the queued items nobody can take, and shows no count at zero", async () => {
  const m = await mountApp({ view: null });
  await until(() => find(m.root, "tbody").length > 0, "the table");
  m.model.publish([summary("queued", { queue_waiting: 2 }), summary("clear", { queue_waiting: 0 })]);
  const row = (name) => find(m.root, "tbody").find((b) => text(b).includes(name));
  await until(() => row("queued") && row("clear"), "both teams");
  assert.match(text(row("queued")), /2 waiting/);
  assert.match(text(row("clear")), /Nothing waiting/);
  assert.doesNotMatch(text(row("clear")), /\d waiting/);
});

// atp-1736 (Orchestrator ruling, request 20261113-152814-1cyj): needs_you with nothing waiting on you shows as blocked.
test("F4: a team that stopped with needs_you still set (state needs_you, running false) has ended and shows it as blocked", () => {
  const body = { running: false, ended: true, working: 0, members: 0, needs_you: 1, state: "needs_you", quiet_at: "2026-11-01T00:00:00Z", member_states: { [`member:${DIR}/old/a`]: { state: "gone" } }, alerts: [{ kind: "needs_you", since: "2026-11-01T00:00:00Z", text: "Decision waiting · 1" }] };
  const row = summaryRow(summary("old", body));
  assert.equal(row.ended, true);
  assert.equal(stateWord(row), "Ended");
  assert.equal(summaryRow(summary("live", { ...body, running: true, ended: false })).ended, false, "the row reads what the daemon served");
  const slices = new Map([summary("old", body)].map((s) => [s.id, s]));
  const { quiet, rows } = homeRows(slices, { now: Date.parse(body.quiet_at) });
  assert.equal(quiet, 1, "counted in Show N quiet teams");
  assert.deepEqual([rows[0].needs, rows[0].blocked], [0, 1], "shown as blocked when the row is, not as Needs you");
  assert.equal(rows[0].counts, "1 member", "an ended row names its members, not idle counts");
});

test("the team table draws no dash: nothing waiting, no items and zero in flight say so", async () => {
  const stored = fakeStorage();
  try {
    const m = await mountApp({ view: null });
    await until(() => find(m.root, "tbody").length > 0, "the table");
    m.model.publish([summary("calm", { needs_you: 0, waiting_on_you: 0, blocked: 0, alerts: [], in_flight: 0, items_total: 0, items_done: 0, work_root: null, branch: null })]);
    const row = () => find(m.root, "tbody").find((b) => text(b).includes("calm"));
    await until(row, "the calm team");
    assert.match(text(row()), /Nothing waiting/);
    assert.match(text(row()), /No items/);
    assert.doesNotMatch(text(row()), /—/);
  } finally {
    delete globalThis.localStorage;
  }
});
