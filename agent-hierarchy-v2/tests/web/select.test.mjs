import { test } from "node:test";
import assert from "node:assert/strict";
import { stateWord, busyLabel, workingShareOf, stateOf, recentOf, memberOf, firstTeam, sparksOf, membersOf, needsState, needsText, partyName, summaryRow } from "../../web/ui/select.js";
import { createMock } from "../../web/mock/model.mjs";

test("a summary whose plan is unread says so; a loaded zero, and an older daemon that does not say, read as loaded", () => {
  const row = (body) => summaryRow({ id: "team-summary:d/t", as_of: 1, body: { team: "team:d/t", name: "t", ...body } });
  assert.equal(row({ plan_loaded: false, items_total: 0 }).planLoaded, false);
  assert.equal(row({ plan_loaded: true, items_total: 0 }).planLoaded, true);
  assert.equal(row({ items_total: 0 }).planLoaded, true);
});

test("a member's state: blocked and failed are blocked, idle is idle, everything else working", () => {
  assert.equal(stateOf("working", true), "working");
  assert.equal(stateOf("idle", true), "idle");
  assert.equal(stateOf("blocked", true), "blocked");
  assert.equal(stateOf("failed", true), "blocked");
  assert.equal(stateOf("unknown", true), "working", "an unlisted value renders as working");
  assert.equal(stateOf(undefined, null), "unknown", "no activity record is unknown, never working");
  assert.equal(stateOf(null, true), "unknown");
  assert.equal(stateOf(undefined, false), "gone", "a dead pid is still gone");
  assert.equal(stateOf("busy", true), "working", "the nearest wrong spelling of working is not blocked");
  assert.equal(stateOf("waiting", true), "working", "waiting is not a hook value, so it is not blocked");
});

test("gone means the pid is known not to be live; an unknown pid is not gone", () => {
  assert.equal(stateOf("working", false), "gone");
  assert.equal(stateOf("blocked", false), "gone", "gone wins over blocked");
  assert.equal(stateOf("working", null), "working");
  assert.equal(stateOf("working", true), "working");
});

const allOf = (model) => new Map(["tree", "members", "exchanges", "processes", "summary"].flatMap((kind) => model.manifest(kind).slices).map((s) => [s.id, s]));

test("the mock team reads as 8 members, one gone, and 3 sparks between known members", async () => {
  const model = createMock();
  const map = allOf(model);
  const team = firstTeam(map);
  const members = membersOf(map, team);
  assert.equal(members.length, 8);
  assert.equal(members[0].orchestrator, true);
  assert.deepEqual(members.filter((m) => m.state === "gone").map((m) => m.name), ["ultra-advisor"]);
  assert.equal(members.find((m) => m.name === "reviewer").state, "idle");
  assert.equal(members.find((m) => m.name === "implementor-2").state, "blocked");
  assert.deepEqual(sparksOf(map, team).map((s) => s.slug), ["ui-r6a", "ui-r2-seed", "run-cli-tests"], "one per open exchange in the open list whose addressee is not gone, by exchange id");
  const impl = members.find((m) => m.name === "implementor-1");
  assert.equal(impl.slug, "Task r6-merge-answer", "the sub-label is the title (else the slug) of the newest of the member's last_exchanges");
  assert.equal(impl.recent.split("\n").length, 3, "last_exchanges holds 3");
  assert.equal(members.find((m) => m.name === "docs-writer").recent.split("\n").length, 1, "a member with one exchange has one line");
  assert.equal(memberOf(map, team, "member:a1b2c3d4/ship/nobody").state, "gone", "a member id with no slice is gone");
});

test("a duty block puts the deputy next to the chief, once, marked on duty, and the chief with the user; no duty block, no mark", () => {
  const map = allOf(createMock());
  const team = firstTeam(map);
  const deputy = team.body.members[3];
  const plain = membersOf(map, team);
  assert.ok(plain.every((m) => m.duty === null));
  const onDuty = { ...team, body: { ...team.body, duty: { deputy: deputy.split("/").pop(), since: "2026-11-08T20:00:00Z", cause: "user", member: deputy } } };
  const members = membersOf(map, onDuty);
  assert.deepEqual(members.map((m) => m.id), [team.body.orchestrator, deputy, ...team.body.members.filter((id) => id !== deputy)]);
  assert.deepEqual(members.slice(0, 2).map((m) => [m.orchestrator, m.duty]), [[true, "away"], [false, "on"]]);
  assert.ok(members.slice(2).every((m) => m.duty === null));
});

test("memberOf reads the daemon's member_states; a member it does not list, or a summary without them, is unknown", async () => {
  const model = createMock();
  const map = allOf(model);
  const team = firstTeam(map);
  const id = membersOf(map, team).find((m) => m.name === "reviewer").id;
  const sid = `team-summary:${team.body.dir_id}/${team.body.name}`;
  const served = new Map(map).set(sid, { id: sid, body: { member_states: { [id]: { state: "blocked" } } } });
  assert.equal(memberOf(served, team, id).state, "blocked");
  const other = membersOf(map, team).find((m) => m.name === "implementor-2").id;
  assert.equal(memberOf(served, team, other).state, "unknown");
  assert.equal(memberOf(new Map(map).set(sid, { id: sid, body: {} }), team, id).state, "unknown");
});

test("memberOf reads the served context; a member without it reads as not reported", () => {
  const model = createMock();
  const map = allOf(model);
  const team = firstTeam(map);
  const id = membersOf(map, team).find((m) => m.name === "reviewer").id;
  const row = map.get(id);
  const withText = new Map(map).set(id, { ...row, body: { ...row.body, context_text: "27%", context_title: "t", context_over: true, context_at: "2026-06-01T12:00:00Z" } });
  assert.deepEqual(memberOf(withText, team, id).ctx, { text: "27%", title: "t", over: true, at: Date.parse("2026-06-01T12:00:00Z") });
  const unread = new Map(map).set(id, { ...row, body: { ...row.body, context_text: "27%", context_title: "t", context_over: true, context_at: null } });
  assert.equal(memberOf(unread, team, id).ctx.at, 0);
  const { context_text: _text, ...bare } = row.body;
  assert.deepEqual(memberOf(new Map(map).set(id, { ...row, body: bare }), team, id).ctx, { text: "—", title: "context not reported", over: false, at: 0 });
});

test("recentOf skips an exchange id that is not in the store and keeps the order", () => {
  const map = new Map([["exchange:d/a", { body: { slug: "A", open: true, from: "x", to: "y" } }], ["exchange:d/b", { body: { slug: "B", open: false, from: "y", to: "x" } }]]);
  assert.equal(recentOf(map, ["exchange:d/b", "exchange:d/missing", "exchange:d/a"]), "B|0|y|x\nA|1|x|y");
  assert.equal(recentOf(map, undefined), "");
  assert.equal(recentOf(map, []), "");
});

test("sparks read only the open list: a null end draws none, and an exchange not in the list draws none", () => {
  const ex = (id, open) => [`exchange:d/${id}`, { id: `exchange:d/${id}`, body: { open, slug: `s-${id}`, from: "role-a", to: "role-b" } }];
  const map = new Map([
    ex("x1", true), ex("x2", true), ex("x3", true), ex("x4", false),
    ["list:open:team:d/t", { body: { team: "team:d/t", open: [
      { id: "exchange:d/x1", to_member: "member:d/t/b", from_member: "member:d/t/a" },
      { id: "exchange:d/x2", to_member: null, from_member: "member:d/t/a" },
      { id: "exchange:d/x3", to_member: "member:d/t/b", from_member: null },
    ] } }],
  ]);
  const team = { body: { dir_id: "d", name: "t" } };
  assert.deepEqual(sparksOf(map, team), [{ id: "exchange:d/x1", from: "member:d/t/a", to: "member:d/t/b", slug: "s-x1" }], "x2 and x3 each have an unresolved end; x4 is not listed");
  assert.deepEqual(sparksOf(new Map(), team), [], "no open list: no sparks");
  assert.deepEqual(sparksOf(map, null), []);
});

test("a plan not read marks Needs you, never drawn as 0; negative: a loaded zero is 0", () => {
  const sum = (plan_loaded, needs_you) => new Map([["team-summary:d/t", { id: "team-summary:d/t", as_of: 1, body: { team: "team:d/t", name: "t", needs_you, plan_loaded } }]]);
  assert.equal(needsState(sum(false, 0), null).pending, true);
  assert.equal(needsState(sum(true, 0), null).pending, false);
  assert.equal(needsText(0, true), "…");
  assert.equal(needsText(2, true), "2…");
  assert.equal(needsText(0, false), "0");
});

const sinceOf = (memberState, activity) => {
  const id = "member:d/t/dev";
  const slices = new Map([
    [id, { id, as_of: 1, body: { name: "dev", activity: "activity:d/dev", last_exchanges: [] } }],
    ["activity:d/dev", { id: "activity:d/dev", as_of: 9_000, body: activity }],
    ["team-summary:d/t", { id: "team-summary:d/t", as_of: 1, body: { member_states: { [id]: memberState } } }],
  ]);
  return memberOf(slices, { body: { dir_id: "d", name: "t", orchestrator: "x" } }, id).since;
};

test("a member's since is when it entered its state, not its last tool or context reading", () => {
  const at = "2026-06-01T10:00:00.000Z";
  assert.equal(sinceOf({ state: "idle", state_at: at }, { activity: "idle", context: { used: 1 } }), Date.parse(at));
});

test("a member with no served state time falls back to the activity's own times", () => {
  assert.equal(sinceOf({ state: "idle" }, { state_at: 5_000 }), 5_000);
  assert.equal(sinceOf({ state: "idle", state_at: "not a time" }, {}), 9_000, "an unreadable time is not a time");
});

test("the busy share decays on an open screen and says the time it covers", () => {
  const hour = 3_600_000;
  const now = 100 * hour;
  const body = { working_1h: 1, working_window_ms: hour, working_seen_from: now - hour, working_spans: [[now - hour, now]] };
  assert.deepEqual(workingShareOf(body, now), { share: 1, shareWindowMs: hour });
  assert.deepEqual(workingShareOf(body, now + hour / 2), { share: 0.5, shareWindowMs: hour });
  assert.deepEqual(workingShareOf(body, now + hour), { share: 0, shareWindowMs: hour }, "a span ending at the window's start counts nothing");
  const fresh = { working_seen_from: now, working_spans: [[now, null]] };
  assert.deepEqual(workingShareOf(fresh, now + 600_000), { share: 1, shareWindowMs: 600_000 }, "an open span runs to now, over the ten minutes seen");
  assert.equal(busyLabel(600_000), "busy, last 10 min");
  assert.equal(busyLabel(hour), "busy, last hour");
  assert.equal(busyLabel(null), "busy, last hour", "an older daemon that does not say");
  assert.equal(busyLabel(0), "busy");
});

test("a body with no spans keeps the share it was served", () => {
  assert.deepEqual(workingShareOf({ working_1h: 0.4, working_window_ms: 60_000 }, 5), { share: 0.4, shareWindowMs: 60_000 });
  assert.deepEqual(workingShareOf(undefined, 5), { share: null, shareWindowMs: null });
});

test("partyName shows the server's name for a party and falls back to the role", () => {
  assert.equal(partyName({ to: "developer", to_name: "dev" }, "to"), "dev");
  assert.equal(partyName({ from: "orchestrator", from_name: null }, "from"), "orchestrator");
  assert.equal(partyName({ from: "orchestrator" }, "from"), "orchestrator");
});

test("a message row names its title, never the raw _run/<slug> item", async () => {
  const { exchangeRows } = await import("../../web/ui/member-summary.js");
  const slices = new Map([["x", { id: "x", body: { item: "_run/ship-it", title: "ship it", slug: "s", created: "2026-11-02T11:00:00Z" } }]]);
  assert.equal(exchangeRows(slices, ["x"])[0].item, "ship it");
});

test("a live Orchestrator idle or unknown reads Running; a dead one, or one with a state, or a member keeps what it shows", () => {
  const O = "member:d/t/@orchestrator";
  const read = (id, served) => {
    const slices = new Map([[id, { id, body: { name: id.split("/").pop() } }], ["team-summary:d/t", { id: "team-summary:d/t", body: { member_states: { [id]: { state_at: "2026-11-02T11:00:00Z", ...served } } } }]]);
    const m = memberOf(slices, { body: { dir_id: "d", name: "t", orchestrator: O } }, id);
    return [stateWord(m.state, m.running), m.since > 0];
  };
  assert.deepEqual(read(O, { state: "idle", liveness: "live" }), ["Running", false]);
  assert.deepEqual(read(O, { state: "unknown", liveness: "live" }), ["Running", false]);
  assert.deepEqual(read(O, { state: "idle", liveness: "unknown" }), ["Idle", true]);
  assert.deepEqual(read(O, { state: "unknown", liveness: "proven_dead" }), ["No signal", true]);
  assert.deepEqual(read(O, { state: "blocked", liveness: "live" }), ["Blocked", true]);
  assert.deepEqual(read("member:d/t/dev", { state: "idle", liveness: "live" }), ["Idle", true]);
  const row = summaryRow({ id: "team-summary:d/t", as_of: 1, body: { team: "team:d/t", name: "t", member_states: { [O]: { state: "idle", liveness: "live" }, "member:d/t/dev": { state: "idle", liveness: "live" } } } });
  assert.deepEqual(row.memberStates.map(([, st]) => st), ["running", "idle"]);
});

// ah2-62: the summary says when the Orchestrator's turn is open.
test("a live Orchestrator idle or unknown with an open turn reads Working; blocked, not live or a member's flag keeps what it shows", () => {
  const O = "member:d/t/@orchestrator";
  const read = (id, served) => {
    const slices = new Map([[id, { id, body: { name: id.split("/").pop() } }], ["team-summary:d/t", { id: "team-summary:d/t", body: { member_states: { [id]: { state_at: "2026-11-02T11:00:00Z", ...served } } } }]]);
    const m = memberOf(slices, { body: { dir_id: "d", name: "t", orchestrator: O } }, id);
    return stateWord(m.state, m.running);
  };
  assert.equal(read(O, { state: "idle", liveness: "live", turn_open: true }), "Working");
  assert.equal(read(O, { state: "unknown", liveness: "live", turn_open: true }), "Working");
  assert.equal(read(O, { state: "blocked", liveness: "live", turn_open: true }), "Blocked");
  assert.equal(read(O, { state: "idle", liveness: "unknown", turn_open: true }), "Idle");
  assert.equal(read("member:d/t/dev", { state: "idle", liveness: "live", turn_open: true }), "Idle");
  const row = summaryRow({ id: "team-summary:d/t", as_of: 1, body: { team: "team:d/t", name: "t", member_states: { [O]: { state: "idle", liveness: "live", turn_open: true }, "member:d/t/dev": { state: "idle", liveness: "live", turn_open: true } } } });
  assert.deepEqual(row.memberStates.map(([, st]) => st), ["working", "idle"]);
});
