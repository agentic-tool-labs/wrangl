// The ah2 Herdr mod's feed data side (spec-herdr-desktop.md § View: feed) over the shipped fixture.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { feedSubs, feedModel, seenStates } from "../../herdr-desktop/mods/ah2/feed-model.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const mod = join(root, "herdr-desktop", "mods", "ah2");
const reply = JSON.parse(readFileSync(join(root, "herdr-desktop", "fixture", "events.json"), "utf8"));
const slices = new Map(reply.slices.map((s) => [s.id, s]));
const NOW = Date.parse(reply.observed_at);

test("the mod's select.js is a byte-identical copy of web/ui/select.js", () => {
  assert.ok(readFileSync(join(mod, "select.js")).equals(readFileSync(join(root, "web", "ui", "select.js"))),
    "copy web/ui/select.js over herdr-desktop/mods/ah2/select.js");
});

test("the feed subscribes each running team's feed list, then its newest exchanges; a closed team adds nothing", () => {
  const lists = new Map([...slices].filter(([id]) => !id.startsWith("list:feed:") && !id.startsWith("exchange:")));
  assert.deepEqual(feedSubs(lists).filter((id) => id.startsWith("list:feed:")).sort(), ["list:feed:team:9f8e7d6c/engram-rc", "list:feed:team:a1b2c3d4/ah2-tm4", "list:feed:team:a1b2c3d4/lane-herdr"]);
  assert.ok(feedSubs(lists).includes("list:plan-changes:a1b2c3d4/lane-herdr") && feedSubs(lists).includes("questions:a1b2c3d4/ah2-tm4"));
  const all = feedSubs(slices);
  assert.ok(all.includes("exchange:a1b2c3d4/20261108-171500-q2ka"));
  assert.ok(!all.some((id) => id.includes("old-spike")));
});

test("the feed is newest first across teams, internal messages left out, overdue marked", () => {
  const m = feedModel(slices, NOW);
  const msgs = m.rows.filter((r) => r.kind === "msg");
  assert.deepEqual(msgs.map((r) => [r.team, r.glyph, r.title]).slice(0, 4), [
    ["lane-herdr", "→", "Build the herdr board view"],
    ["ah2-tm4", "→", "Build the herdr feed view"],
    ["lane-herdr", "←", "Screenshots for the Q&A view"],
    ["ah2-tm4", "→", "Review the questions-model copy"],
  ]);
  assert.ok(!m.rows.some((r) => r.internal));
  assert.ok(feedModel(slices, NOW, { internal: true }).rows.some((r) => r.internal), "the Internal chip brings them back");
  assert.equal(m.overdue, 1);
  assert.equal(msgs[1].sub, "orchestrator → lane-herdr");
  assert.equal(feedModel(new Map([["list:teams", slices.get("list:teams")]]), NOW).empty, true);
});

test("a running team its summary marks closed leaves the feed", () => {
  const sid = "team-summary:a1b2c3d4/lane-herdr";
  const s = slices.get(sid);
  const marked = new Map(slices).set(sid, { ...s, body: { ...s.body, closed_mark: "closed" } });
  assert.ok(!feedSubs(marked).some((id) => id.endsWith("/lane-herdr")));
  assert.ok(!feedModel(marked, NOW).rows.some((r) => r.team === "lane-herdr"));
  assert.ok(feedModel(slices, NOW).rows.some((r) => r.team === "lane-herdr"), "unmarked, the same team stays");
});

test("one stream: plan moves, questions, alerts and seen state changes by minute, the new divider, filters, the Now strip", () => {
  const m = feedModel(slices, NOW, { looked: NOW - 10 * 60000 });
  const kinds = new Set(m.rows.map((r) => r.kind));
  for (const k of ["msg", "plan", "ask", "alert"]) assert.ok(kinds.has(k), k);
  const move = m.rows.find((r) => r.key.includes("|herdr|3"));
  assert.deepEqual([move.title, move.sub, move.mb?.name], ["atp-1185", "moved to in progress · ux-engineer", "ux-engineer"]);
  // newest first, minute groups, the divider after the last row newer than the last look
  assert.ok(m.rows.every((r, i) => i === 0 || m.rows[i - 1].at >= r.at));
  const at = m.groups.findIndex((g) => g.divider);
  assert.equal(m.groups[at].divider, m.newCount);
  assert.equal(m.groups.slice(0, at).reduce((n, g) => n + g.rows.length, 0), m.newCount);
  assert.equal(feedModel(slices, NOW).newCount, 0, "never looked: no divider");
  // filters
  assert.ok(!feedModel(slices, NOW, { off: ["plan"] }).rows.some((r) => r.kind === "plan"));
  assert.ok(feedModel(slices, NOW, { team: "a1b2c3d4/lane-herdr" }).rows.every((r) => r.team === "lane-herdr"));
  assert.equal(feedModel(slices, NOW, { off: ["msg", "plan", "alert", "ask", "seen"] }).filteredOut, true);
  assert.deepEqual(m.now.rows.map((r) => r.state).at(-1), "blocked", "working first, then blocked");
  // a state change shows only once seen: the first look records, the next one that differs is an event
  const first = seenStates(null, slices, NOW);
  assert.equal(first.events.length, 0);
  const sid = "team-summary:a1b2c3d4/ah2-tm4";
  const b = slices.get(sid).body;
  const moved = new Map(slices).set(sid, { ...slices.get(sid), body: { ...b, member_states: { ...b.member_states, "member:a1b2c3d4/ah2-tm4/implementor": { ...b.member_states["member:a1b2c3d4/ah2-tm4/implementor"], state: "blocked" } } } });
  const seen = seenStates(first, moved, NOW + 1000);
  const row = feedModel(moved, NOW + 1000, { seen: seen.events }).rows.find((r) => r.kind === "seen");
  assert.deepEqual([row.title, row.sub, row.mark], ["implementor: working → blocked", "seen while open", "blocked"]);
});

test("the feed view runs nothing", () => {
  for (const f of ["feed.js", "feed-model.js"]) assert.doesNotMatch(readFileSync(join(mod, f), "utf8"), /\bexec\b/);
});

// ah2-220 Amendment 2: the label is for display; a lane Orchestrator's rows keep the member and terminal their lookup finds by name.
test("a lane orchestrator's seen event and a message to it read Sub-Orchestrator and keep a member and a terminal target", async () => {
  const { terminalFinder } = await import("../../herdr-desktop/mods/ah2/model.js");
  const sid = "team-summary:a1b2c3d4/lane-herdr";
  const key = "member:a1b2c3d4/lane-herdr/@orchestrator";
  const list = "list:feed:team:a1b2c3d4/lane-herdr";
  const ex = { id: "exchange:a1b2c3d4/20261108-171900-zz01", body: { id: "exchange:a1b2c3d4/20261108-171900-zz01", dir_id: "a1b2c3d4", team: "lane-herdr", from: "architect", from_name: "architect", to: "orchestrator", open: false, created: "2026-11-08T17:19:00.000Z", slug: "lane-report", title: "lane report" } };
  const base = new Map(slices).set(list, { ...slices.get(list), body: { ...slices.get(list).body, ids: ["20261108-171900-zz01", ...slices.get(list).body.ids] } }).set(ex.id, ex);
  const first = seenStates(null, base, NOW);
  const b = base.get(sid).body;
  const moved = new Map(base).set(sid, { ...base.get(sid), body: { ...b, member_states: { ...b.member_states, [key]: { ...b.member_states[key], state: "blocked" } } } });
  const seen = seenStates(first, moved, NOW + 1000);
  const m = feedModel(moved, NOW + 1000, { seen: seen.events });
  const focus = terminalFinder([{ state: "live", address: "t7", labels: {} }]);
  const seenRow = m.rows.find((r) => r.kind === "seen" && r.mb?.key === key);
  const msgRow = m.rows.find((r) => r.key === ex.id);
  assert.equal(seenRow?.title, "Sub-Orchestrator: working → blocked");
  assert.equal(msgRow?.sub, "Sub-Orchestrator reported");
  for (const r of [seenRow, msgRow]) {
    assert.equal(r.mb?.key, key, "the row still names the lane's orchestrator");
    assert.equal(focus(r.mb), "t7", "and keeps its Focus terminal");
  }
});
