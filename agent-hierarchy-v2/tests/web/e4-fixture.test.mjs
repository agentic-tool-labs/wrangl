import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { PRESETS } from "../../web/mock/presets.mjs";
import { LIMITS, baselineEntry, failureLine, fixtureNames, labelOf, realisticRows, spreadOf } from "../perf/e4-gates.mjs";

const harness = new URL("../perf/ui-e4.mjs", import.meta.url);

const entry = (over = {}) => ({
  diagnostics: "off",
  request_line_max_bytes: 9000,
  first_load_bytes: 1000,
  long_poll: { idle: { body_bytes: 2000 } },
  idle: { page_cpu_pct: 9 },
  fixture: { preset: "realistic" },
  ...over,
});
const base = { first_load_bytes: 1000, long_poll: { idle: { body_bytes: 2000 } } };
const rowOf = (rows, metric) => rows.find((r) => r.metric === metric);

test("the preset table has small and realistic, and the harness carries no counts of its own", () => {
  assert.deepEqual([PRESETS.small.AH_MOCK_MEMBERS, PRESETS.small.AH_MOCK_OPEN, PRESETS.small.AH_MOCK_DONE], [10, 8, 33]);
  assert.deepEqual([PRESETS.realistic.AH_MOCK_MEMBERS, PRESETS.realistic.AH_MOCK_OPEN, PRESETS.realistic.AH_MOCK_DONE], [50, 40, 1500]);
  const source = readFileSync(harness, "utf8");
  assert.doesNotMatch(source, /process\.env\.AH_MOCK_(MEMBERS|OPEN|DONE)\s*=/);
  assert.doesNotMatch(source, /=\s*["'](8|33)["']/);
});

test("the small preset builds the fixture the old override did", async () => {
  const count = async (env, tag) => {
    const before = { ...process.env };
    Object.assign(process.env, env);
    try {
      const f = await import(`../../web/mock/fixture.mjs?${tag}`);
      return f.buildSlices(Date.now()).filter((s) => s.id.startsWith("exchange:")).length;
    } finally {
      for (const key of Object.keys(env)) {
        before[key] === undefined ? delete process.env[key] : (process.env[key] = before[key]);
      }
    }
  };
  const small = await count({ AH_MOCK_FIXTURE: "small" }, "e4-small");
  const explicit = await count({ AH_MOCK_FIXTURE: "", AH_MOCK_MEMBERS: "10", AH_MOCK_OPEN: "8", AH_MOCK_DONE: "33" }, "e4-explicit");
  const plain = await count({ AH_MOCK_FIXTURE: "", AH_MOCK_MEMBERS: "", AH_MOCK_OPEN: "", AH_MOCK_DONE: "" }, "e4-plain");
  assert.equal(small, explicit);
  assert.ok(small > plain, `small ${small} should add its open and answered exchanges to the default ${plain}`);
});

test("--fixture parsing: a name runs that preset, both and no flag run all, the environment applies without a flag, an unknown name is an error", () => {
  assert.deepEqual(fixtureNames("small", undefined, PRESETS), ["small"]);
  assert.deepEqual(fixtureNames("realistic", undefined, PRESETS), ["realistic"]);
  assert.deepEqual(fixtureNames("both", undefined, PRESETS), ["small", "realistic"]);
  assert.deepEqual(fixtureNames(undefined, undefined, PRESETS), ["small", "realistic"]);
  assert.deepEqual(fixtureNames(undefined, "", PRESETS), ["small", "realistic"]);
  assert.deepEqual(fixtureNames(undefined, "realistic", PRESETS), ["realistic"]);
  assert.deepEqual(fixtureNames("small", "realistic", PRESETS), ["small"]);
  assert.throws(() => fixtureNames("huge", undefined, PRESETS), /unknown fixture "huge"; valid: small, realistic, both/);
  assert.throws(() => fixtureNames(undefined, "huge", PRESETS), /unknown fixture "huge"/);
});

test("the harness refuses an unknown preset before it starts a browser", () => {
  const done = spawnSync(process.execPath, [harness.pathname, "--fixture=huge"], { encoding: "utf8", timeout: 20000 });
  assert.equal(done.status, 2);
  assert.match(done.stderr, /unknown fixture "huge"; valid: small, realistic, both/);
});

test("a result is labelled with its preset and counts", () => {
  assert.equal(labelOf("small", PRESETS.small), "fixture=small(10/8/33)");
  assert.equal(labelOf("realistic", PRESETS.realistic), "fixture=realistic(50/40/1500)");
});

test("the request line gates at 16 KB with or without a baseline; one byte under passes", () => {
  const label = labelOf("realistic", PRESETS.realistic);
  const over = realisticRows(label, "off", entry({ request_line_max_bytes: LIMITS.requestLineBytes + 1 }), null);
  const at = realisticRows(label, "off", entry({ request_line_max_bytes: LIMITS.requestLineBytes }), null);
  assert.equal(rowOf(over, "request line bytes").ok, false);
  assert.equal(rowOf(at, "request line bytes").ok, true);
  assert.equal(failureLine(rowOf(over, "request line bytes")), "fixture=realistic(50/40/1500): diag off request line bytes 16385 exceeds the limit 16384");
});

test("first-load bytes gate at the baseline x1.10 and idle bytes at x1.15", () => {
  const label = labelOf("realistic", PRESETS.realistic);
  const ok = realisticRows(label, "off", entry({ first_load_bytes: 1100, long_poll: { idle: { body_bytes: 2300 } } }), base);
  assert.equal(rowOf(ok, "first load bytes").ok, true);
  assert.equal(rowOf(ok, "idle long-poll bytes").ok, true);
  const bad = realisticRows(label, "off", entry({ first_load_bytes: 1101, long_poll: { idle: { body_bytes: 2301 } } }), base);
  assert.equal(rowOf(bad, "first load bytes").ok, false);
  assert.equal(rowOf(bad, "idle long-poll bytes").ok, false);
});

test("with no baseline the relative rows report and pass, and CPU is never gated", () => {
  const rows = realisticRows("fixture=realistic(50/40/1500)", "off", entry({ first_load_bytes: 9e9, long_poll: { idle: { body_bytes: 9e9 } }, idle: { page_cpu_pct: 99 } }), null);
  assert.deepEqual(rows.filter((r) => !r.ok), []);
  assert.equal(rowOf(rows, "first load bytes").note, "no baseline");
  assert.equal(rowOf(rows, "idle page cpu %").gated, false);
});

test("an idle-bytes baseline whose spread is over 10% is informational", () => {
  const rows = realisticRows("fixture=realistic(50/40/1500)", "off", entry({ long_poll: { idle: { body_bytes: 9e9 } } }), { ...base, idle_bytes_gated: false });
  assert.equal(rowOf(rows, "idle long-poll bytes").gated, false);
  assert.equal(rowOf(rows, "idle long-poll bytes").ok, true);
});

test("the recorded baseline carries the spread and the E2 verdict", () => {
  const run = (idle) => ({ diagnostics: "off", fixture: { preset: "realistic" }, long_poll: { idle: { body_bytes: idle } } });
  const wide = { runs_per_mode: 3, runs: [run(1000), run(1000), run(1300)], median: [run(1000)] };
  const tight = { runs_per_mode: 3, runs: [run(1000), run(1050), run(1100)], median: [run(1050)] };
  assert.equal(spreadOf([1, 4, 2]), 3);
  assert.equal(baselineEntry(wide, "abc1234", "2026-10-20").idle_bytes_gated, false);
  const recorded = baselineEntry(tight, "abc1234", "2026-10-20");
  assert.equal(recorded.idle_bytes_gated, true);
  assert.equal(recorded.sha, "abc1234");
  assert.equal(recorded.fixture.preset, "realistic");
});

test("the plan: row gates at 10 KB when the run held plan slices; one byte under passes", () => {
  const label = labelOf("realistic", PRESETS.realistic);
  const over = realisticRows(label, "off", entry({ plan_slice_max_bytes: LIMITS.planSliceBytes + 1 }), null);
  const at = realisticRows(label, "off", entry({ plan_slice_max_bytes: LIMITS.planSliceBytes }), null);
  assert.equal(rowOf(over, "plan slice bytes").ok, false);
  assert.equal(rowOf(over, "plan slice bytes").gated, true);
  assert.equal(rowOf(at, "plan slice bytes").ok, true);
});

test("the plan: row is left out when the preset carries no plan streams", () => {
  const label = labelOf("realistic", PRESETS.realistic);
  assert.equal(rowOf(realisticRows(label, "off", entry({ plan_slice_max_bytes: null }), null), "plan slice bytes"), undefined);
  assert.equal(rowOf(realisticRows(label, "off", entry(), null), "plan slice bytes"), undefined);
});

test("a removed or unknown flag exits 2 and names the valid flags", () => {
  const done = spawnSync(process.execPath, [harness.pathname, "--members", "50"], { encoding: "utf8", timeout: 20000 });
  assert.equal(done.status, 2);
  assert.match(done.stderr, /unknown flag --members; valid: --fixture, --label/);
  const typo = spawnSync(process.execPath, [harness.pathname, "--fixture=small", "--runz=2"], { encoding: "utf8", timeout: 20000 });
  assert.equal(typo.status, 2);
  assert.match(typo.stderr, /unknown flag --runz=2/);
});
