#!/usr/bin/env node
// The scoped divergences of lib-ledger.mjs: the kept team's own contribution may differ, nothing else may.
import assert from "node:assert/strict";
import { scopedViews } from "./lib-ledger.mjs";

const agg = (live) => ({ at: "<ISO:1>", visible: live > 0, tone: "idle", live, out: 0, blocked: 0, overdue: 0, stalled: 0, text: `${live} live`, short: `${live}/0` });
const doc = (over = {}) => ({
  schema: 1,
  written_at: "<ISO:2>",
  expires_at: "<ISO:3>",
  enabled: true,
  member_sessions: [],
  teams: [],
  timeline: [agg(0)],
  owners: [],
  ...over,
});
const wrap = (d) => ({ tree_diff: { added: [{ path: "proj/.claude/ah2/status.json", mode: "0644", content: JSON.stringify(d) + "\n" }] } });
const same = (scope, expect, actual) => {
  const v = scopedViews(scope, expect, actual);
  return JSON.stringify(v.expect) === JSON.stringify(v.actual);
};
const T = { kind: "status-team", team: "tm" };
const teamTm = { team: "tm", members: [{ name: "tm-a", live: true, session_id: "s1" }], dispatches: [] };
const teamOther = { team: "other", members: [{ name: "o-a", live: true }], dispatches: [] };

// What T may change.
assert.ok(same(T, wrap(doc()), wrap(doc({ teams: [teamTm], timeline: [agg(1)], owners: [{ sessions: ["sO"], teams: ["tm"], timeline: [agg(1)] }], member_sessions: ["s1"] }))), "T's own contribution is allowed");
assert.ok(same(T, wrap(doc({ teams: [teamOther], owners: [{ sessions: ["sO"], teams: ["other"], timeline: [agg(1)] }], timeline: [agg(1)] })),
  wrap(doc({ teams: [teamOther, teamTm], owners: [{ sessions: ["sO"], teams: ["other", "tm"], timeline: [agg(2)] }], timeline: [agg(2)] }))), "T joining an owner entry that has another team is allowed");

// What it may not: each of these is a difference elsewhere, and widening the mask to a key name at any depth would hide it.
const base = wrap(doc({ teams: [teamOther], owners: [{ sessions: ["sX"], teams: ["other"], timeline: [agg(1)] }], timeline: [agg(1)] }));
const kept = (over) => wrap(doc({ teams: [teamOther, teamTm], owners: [{ sessions: ["sX"], teams: ["other"], timeline: [agg(1)] }, { sessions: ["sO"], teams: ["tm"], timeline: [agg(1)] }], timeline: [agg(2)], ...over }));
assert.ok(same(T, base, kept({})), "fixture: the kept team alone is allowed");
assert.ok(!same(T, base, kept({ owners: [{ sessions: ["sX"], teams: ["other"], timeline: [agg(9)] }, { sessions: ["sO"], teams: ["tm"], timeline: [agg(1)] }] })), "another team's owners entry may not differ");
assert.ok(!same(T, base, kept({ owners: [{ sessions: ["sY"], teams: ["other"], timeline: [agg(1)] }, { sessions: ["sO"], teams: ["tm"], timeline: [agg(1)] }] })), "another team's owner session may not differ");
assert.ok(!same(T, base, kept({ teams: [{ ...teamOther, members: [{ name: "o-a", live: false }] }, teamTm] })), "another team's element may not differ");
assert.ok(!same(T, base, kept({ enabled: false })), "an unrelated key may not differ");
assert.ok(!same(T, base, kept({ timeline: [agg(3)] })), "a count may differ only by T's own contribution");
assert.ok(!same(T, base, kept({ timeline: [{ ...agg(2), blocked: 1 }] })), "a count T does not contribute to may not differ");
assert.ok(!same(T, wrap(doc({ member_sessions: ["a"] })), wrap(doc({ teams: [teamTm], timeline: [agg(1)], member_sessions: ["a", "zzz"] }))), "an extra member session that is not one of T's members' may not appear");
assert.ok(same(T, wrap(doc({ member_sessions: ["a"] })), wrap(doc({ teams: [teamTm], timeline: [agg(1)], member_sessions: ["a", "s1"] }))), "an extra member session of T's own member is allowed");
assert.ok(!same(T, base, kept({ written_at: "2026-01-01T00:00:00Z" })), "written_at is not masked");
assert.ok(!same(T, base, kept({ timeline: [{ ...agg(1), at: "2026-01-01T00:00:00Z" }] })), "the time of a timeline entry is not an aggregate");
assert.ok(!same(T, wrap(doc({ member_sessions: ["s1", "s2"] })), wrap(doc({ member_sessions: ["s2"] }))), "a recorded member session may not go missing");
assert.ok(!same(T, wrap(doc({ member_sessions: ["s1", "s2"] })), wrap(doc({ member_sessions: ["s2", "s1"] }))), "member sessions keep their order");
assert.ok(!same(T, wrap(doc({ member_sessions: ["s1"] })), wrap(doc({ member_sessions: ["s0", "s1", "s3"] }))), "extra member sessions with no kept team behind them may not appear");

// The timeline aggregates move only in the direction T's presence pushes them, and by no more than T's own counts.
const idle = (over) => ({ ...agg(0), ...over });
const noMembers = { team: "tm", members: [], dispatches: [], pipeline: { id: "p" } };
const noPipeline = { team: "tm", members: [], dispatches: [], pipeline: null };
const dispatching = { team: "tm", members: [{ name: "tm-a", live: true, session_id: "s1" }], dispatches: [{ id: "d1" }], pipeline: null };
const tl = (entry) => wrap(doc({ timeline: [entry] }));
const tlWith = (element, entry) => wrap(doc({ teams: [element], timeline: [entry] }));
assert.ok(same(T, tl(idle({})), tlWith(noMembers, idle({ visible: true }))), "visible may go false to true when T has a pipeline");
assert.ok(!same(T, tl(idle({})), tlWith(noPipeline, idle({ visible: true }))), "visible may not flip when T has no pipeline, no live member and no dispatch");
assert.ok(!same(T, tl(idle({ visible: true })), tlWith(noMembers, idle({ visible: false }))), "visible may not go true to false");
assert.ok(same(T, tl(idle({ stalled: 0 })), tlWith(dispatching, idle({ live: 1, out: 1, stalled: 1, visible: true }))), "live and stalled may rise by T's counts");
assert.ok(!same(T, tl(idle({ stalled: 0 })), tlWith(dispatching, idle({ live: 1, out: 1, stalled: 2, visible: true }))), "stalled may not rise by more than T's dispatches");
assert.ok(!same(T, tl(idle({ live: 2 })), tlWith(dispatching, idle({ live: 4, out: 1, visible: true }))), "live may not rise by more than T's live members");
assert.ok(!same(T, tl(idle({ live: 2, stalled: 1 })), tlWith(dispatching, idle({ live: 3, out: 1, stalled: 0, visible: true }))), "stalled may not fall");
assert.ok(!same(T, tl(idle({ live: 2 })), tlWith(dispatching, idle({ live: 1, out: 1, visible: true }))), "live may not fall");
assert.ok(!same(T, tl(idle({})), tlWith(noPipeline, idle({ live: 1 }))), "a rise with no T member behind it may not appear");
assert.ok(!same(T, tl(idle({ live: 2 })), tlWith(dispatching, idle({ live: 2, out: 1, visible: true }))), "live may not rise by less than T's live members, which would hide a loss elsewhere");

// A status write that did not happen is not hidden: the executed-ops result is outside the scope.
const ops = (written) => ({ tree_diff: { added: [{ path: "proj/.claude/ah2/.state-status/.executed-ops.jsonl", mode: "0644", content: `{"op_id":"<OPID:1>","at":"<ISO:4>","result":{"written":${written}}}\n` }] } });
assert.ok(!same(T, ops(false), ops(true)), "the written flag of an operation is not masked");

// A scope that names statusWrite allows the status-write op's false→true, and only when the actual tree shows a status document with T.
const SW = { ...T, statusWrite: true };
const opsLine = (id, written) => `{"op_id":"${id}","at":"<ISO:4>","result":{"written":${written}}}`;
const withOps = (statusDoc, lines) => ({ tree_diff: { added: [
  ...(statusDoc ? [{ path: "proj/.claude/ah2/status.json", mode: "0644", content: JSON.stringify(statusDoc) + "\n" }] : []),
  { path: "proj/.claude/ah2/.state-status/.executed-ops.jsonl", mode: "0644", content: lines.join("\n") + "\n" },
] } });
const noT = doc();
const withT = doc({ teams: [teamTm], timeline: [agg(1)], member_sessions: ["s1"] });
assert.ok(same(SW, withOps(noT, [opsLine("a", false)]), withOps(withT, [opsLine("a", true)])), "the status write false→true is allowed beside the kept team");
assert.ok(!same(SW, withOps(noT, [opsLine("a", false)]), withOps(noT, [opsLine("a", true)])), "a write that stays true without T is a regression");
assert.ok(!same(SW, withOps(noT, [opsLine("a", true)]), withOps(withT, [opsLine("a", false)])), "true→false is not covered");
assert.ok(same(SW, withOps(noT, [opsLine("a", false), opsLine("b", false)]), withOps(withT, [opsLine("a", true), opsLine("b", true)])), "every status-write line may flip");
assert.ok(!same(T, withOps(noT, [opsLine("a", false)]), withOps(withT, [opsLine("a", true)])), "without statusWrite the flag is not masked");
{
  // An op in the status file whose result is not the status write's single `written` key is another operation.
  const otherOp = (written) => ({ tree_diff: { added: [
    { path: "proj/.claude/ah2/status.json", mode: "0644", content: JSON.stringify(written ? withT : noT) + "\n" },
    { path: "proj/.claude/ah2/.state-status/.executed-ops.jsonl", mode: "0644", content: `{"op_id":"a","at":"<ISO:4>","result":{"written":${written},"count":1}}\n` },
  ] } });
  assert.ok(!same(SW, otherOp(false), otherOp(true)), "an op with another result shape in the status file is not masked");
}
{
  const other = (written) => ({ tree_diff: { added: [
    { path: "proj/.claude/ah2/status.json", mode: "0644", content: JSON.stringify(written ? withT : noT) + "\n" },
    { path: "proj/.claude/ah2/.state-other/.executed-ops.jsonl", mode: "0644", content: opsLine("a", written) + "\n" },
  ] } });
  assert.ok(!same(SW, other(false), other(true)), "another op's written flag is not masked even beside the kept team");
}

// Another document that merely has a `teams` key is not a status document.
assert.ok(!same(T, { stdout: JSON.stringify({ teams: [teamTm] }) }, { stdout: JSON.stringify({ teams: [] }) }), "only a status document is scoped");

// json-paths.
const P = { kind: "json-paths", paths: ["live", "live_unknown"] };
const plan = (over) => ({ stdout: JSON.stringify({ member: { name: "m" }, live: false, close_token: "t", ...over }) + "\n" });
assert.ok(same(P, plan({}), plan({ live: null, live_unknown: "no live peers.jsonl record" })), "the named paths may differ");
assert.ok(!same(P, plan({}), plan({ live: null, close_token: "u" })), "another path may not differ");
assert.ok(!same(P, plan({}), plan({ member: { name: "other" } })), "a nested key is not the named path");
const L = { kind: "json-paths", paths: ["teams[name=tm].members"] };
const list = (a, b) => ({ stdout: JSON.stringify({ teams: [{ name: "tm", members: a }, { name: "x", members: b }] }) });
assert.ok(same(L, list(0, 1), list(2, 1)), "the named team's members may differ");
assert.ok(!same(L, list(0, 1), list(0, 2)), "another team's members may not differ");

// sweep-outcome.
{
  const ORPHAN = "ah: 1 orphaned team record(s) (default) — gone";
  const SWEPT = "cleared stale team t-legacy";
  const SO = {
    kind: "sweep-outcome",
    statusTeam: null,
    stdoutLines: [`${SWEPT}\\n`, ORPHAN],
    keptStdoutLines: ["Team t-legacy (authoritative): x"],
    treePaths: ["proj/.claude/ah2/team.json", "proj/.claude/ah2/_stale/team.*"],
    opLines: ["proj/.claude/ah2/.state-status/.executed-ops.jsonl"],
  };
  const text = (...ls) => ls.join("\\n") + "\\n";
  const run = ({ out, added = [], removed = [] }) => ({ stdout: out, tree_diff: { added, changed: [], removed } });
  const ops = (...written) => written.map((w) => JSON.stringify({ op_id: "<OPID:1>", at: "<ISO:1>", result: { written: w } })).join("\n") + "\n";
  const opFile = (content) => ({ path: "proj/.claude/ah2/.state-status/.executed-ops.jsonl", mode: "0644", content });
  const legacy = { team: null, members: [{ name: "legacy-implementor", live: false }], dispatches: [] };
  const statusFile = (teams) => ({ path: "proj/.claude/ah2/status.json", mode: "0644", content: JSON.stringify(doc({ teams })) + "\n" });
  const swept = run({
    out: text("a", SWEPT, "", "b"),
    added: [opFile(ops(true, false)), { path: "proj/.claude/ah2/_stale/team.<STAMP:1>.json", mode: "0644", content: "{}" }, statusFile([])],
    removed: ["proj/.claude/ah2/team.json"],
  });
  const keptRun = (over = {}) => run({ out: text("a", "Team t-legacy (authoritative): x", "b"), added: [opFile(ops(true)), statusFile([legacy])], ...over });
  assert.ok(same(SO, swept, keptRun()), "the whole sweep outcome is allowed");
  const statusWith = (teams, timeline) => ({ path: "proj/.claude/ah2/status.json", mode: "0644", content: JSON.stringify(doc({ teams, timeline })) + "\n" });
  const liveLegacy = [{ ...legacy, members: [{ name: "legacy-implementor", live: true }] }];
  assert.ok(!same(SO, swept, keptRun({ added: [opFile(ops(true)), statusWith(liveLegacy, [agg(5)])] })), "the kept team's own live member changes the aggregates, which only its contribution may");
  assert.ok(same(SO, swept, keptRun({ added: [opFile(ops(true)), statusWith(liveLegacy, [agg(1)])] })), "the kept team's own live member may raise the aggregates by its own count");
  assert.ok(!same(SO, swept, keptRun({ added: [opFile(ops(true)), statusFile([legacy, { team: "other", members: [], dispatches: [] }])] })), "another team in the status document is not masked");
  assert.ok(same({ ...SO, stdoutLines: [`${SWEPT}\\n`] }, run({ out: text("a", SWEPT, "", "b") }), run({ out: text("a", "b") })), "the swept line goes with its blank line");
  assert.ok(!same({ ...SO, stdoutLines: [`${SWEPT}\\n`] }, run({ out: text("a", SWEPT, "", "b") }), run({ out: text("a", "", "b") })), "the blank line is not left behind");
  assert.ok(!same({ ...SO, stdoutLines: [`${SWEPT}\\n`] }, run({ out: text("a", SWEPT, "", "b") }), run({ out: text("a", "other line", "", "b") })), "an actual line that differs still fails");
  assert.ok(!same({ ...SO, stdoutLines: [`${SWEPT}\\n`] }, run({ out: text("a", `${SWEPT} x`, "b") }), run({ out: text("a", "b") })), "a recorded line that only starts like the named one is not masked");
  assert.ok(!same({ ...SO, stdoutLines: [ORPHAN] }, run({ out: text("a", `x ${ORPHAN}`, "b") }), run({ out: text("a", "x b") })), "a line that merely ends with the masked text is not masked");
  assert.ok(same({ ...SO, stdoutLines: [ORPHAN] }, run({ out: text("a", ORPHAN, "b", ORPHAN, "c") }), run({ out: text("a", "b", ORPHAN, "c") })), "only the first whole copy goes, so a copy later in the text stays on both sides");
  assert.ok(!same({ ...SO, stdoutLines: [ORPHAN] }, run({ out: text("a", ORPHAN, ORPHAN, "b") }), run({ out: text("a", "b") })), "a second copy of the masked line is not masked");
  assert.ok(same({ ...SO, stdoutLines: [ORPHAN] }, run({ out: text("a", ORPHAN, "b") }), run({ out: text("a", "b") })), "the orphan notice goes");
  assert.ok(!same({ ...SO, stdoutLines: [ORPHAN] }, run({ out: text("a", "other notice", "b") }), run({ out: text("a", "b") })), "another recorded line is not masked");
  assert.ok(!same({ ...SO, stdoutLines: [ORPHAN] }, run({ out: text("a", "b") }), run({ out: text("a", ORPHAN, "b") })), "the actual side is never masked by a recorded line");
  assert.ok(!same({ ...SO, keptStdoutLines: [] }, run({ out: text("a", "b") }), run({ out: text("a", "Team t-legacy (authoritative): x", "b") })), "a kept-team line is masked only when named");
  assert.ok(!same(SO, run({ out: "a", added: [{ path: "proj/.claude/ah2/other.json", mode: "0644", content: "{}" }] }), run({ out: "a" })), "a tree entry that is not named is not masked");
  assert.ok(!same(SO, run({ out: "a", removed: ["proj/.claude/ah2/peers.jsonl"] }), run({ out: "a" })), "another removed path is not masked");
  assert.ok(!same(SO, run({ out: "a", added: [opFile(ops(true, true))] }), run({ out: "a", added: [opFile(ops(true))] })), "an op line other than written:false is not masked");
}

console.log("ledger scope: ok");
