#!/usr/bin/env node
// The contract runner.
//   run.mjs [--impl native] [--kind hook|cli|wire|state-op|lifecycle]... [--only <glob>] [--shard K/N] [--list] [--fail-fast] [--allow-skips]
// --list prints the selected cases as `<kind>/<id>`, one per line, in the order they run, and runs nothing. --shard K/N (K from 0 to
// N-1) keeps the cases whose place in that selected list, counted from 0, is K modulo N, so the N shards together are the list, each
// case in exactly one, and a shard is a run of its own with its own sandbox.
// Prints `ok|FAIL|skip <kind>/<id>` per case, then `contracts: <passed>/<total> (<impl>), skipped: <n> (<reason counts>)`.
// Exits 0 when every case that is not skipped passes, 1 on a failure, 2 on usage or a tooling error.
// The only implementation is the native binary ($AHD_NATIVE_BIN, through the plugin's shims): with no binary every case is
// skipped as `no-native-binary` and the run exits 1; with a binary, a unit it does not have yet is skipped as `unit-not-native`
// (recognised by its exact text). Any skip other than a `named-divergence` one exits 1, so a run cannot pass vacuously; a
// named-divergence skip is a ruled difference from the Node oracle and is not a failure. `--allow-skips` lets any skip exit 0
// for an ad hoc look at one case.
// On a failure it writes a diff of the normalized expected and actual values to $T/fail/<kind>/<id>.diff and prints the path;
// the sandbox is kept in that case. Imports nothing from hooks/ (G2).

import { spawnSync } from "node:child_process";
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { PLUGIN, compareExpect, describeDiff, makeSandbox, removeSandbox, runAsHarness } from "./lib-case.mjs";
import { FIELDS, KINDS, executeCase, loadCases, matchesOnly } from "./lib-run.mjs";
import { scopedViews } from "./lib-ledger.mjs";

runAsHarness();

const argv = process.argv.slice(2);
const flag = (n) => argv.includes(n);
const values = (n) => argv.reduce((a, x, i) => (x === n ? [...a, argv[i + 1]] : a), []);
const known = new Set(["--impl", "--kind", "--only", "--shard", "--list", "--fail-fast", "--require-native", "--allow-skips", "--help"]);
for (let i = 0; i < argv.length; i++) {
  if (argv[i].startsWith("--") && !known.has(argv[i])) { console.error(`run: unknown option ${argv[i]}`); process.exit(2); }
  if (["--impl", "--kind", "--only", "--shard"].includes(argv[i])) i++;
}
if (flag("--help")) { console.log("usage: run.mjs [--impl native] [--kind K]... [--only GLOB] [--shard K/N] [--list] [--fail-fast] [--allow-skips]"); process.exit(0); }
const impl = values("--impl")[0] || "native";
if (impl !== "native") { console.error("run: --impl needs native (node and manifest no longer exist)"); process.exit(2); }
const kinds = values("--kind");
for (const k of kinds) if (!KINDS.includes(k)) { console.error(`run: unknown --kind ${k}`); process.exit(2); }
const only = values("--only")[0] || null;
const shardArg = values("--shard")[0];
let shard = null;
if (shardArg !== undefined) {
  const m = /^(\d+)\/(\d+)$/.exec(shardArg);
  if (!m || Number(m[2]) < 1 || Number(m[1]) >= Number(m[2])) { console.error(`run: --shard needs K/N with 0 <= K < N, got ${shardArg}`); process.exit(2); }
  shard = { k: Number(m[1]), n: Number(m[2]) };
}
const failFast = flag("--fail-fast");
const requireNative = !flag("--allow-skips");

const selected = loadCases(kinds.length ? kinds : KINDS).filter((c) => matchesOnly(c, only)).filter((_, i) => !shard || i % shard.n === shard.k);
if (flag("--list")) { for (const c of selected) console.log(`${c.kind}/${c.id}`); process.exit(0); }

let T;
try { T = makeSandbox(); } catch (e) { console.error(`run: ${e.message}`); process.exit(2); }

const runImpl = impl;

let passed = 0, total = 0, failed = 0, diverged = 0, skippedTotal = 0;

// A recorded row that a divergence file names (`rowsExercisingNode`, beside the divergence's `id`) is a ruled difference from the
// Node oracle: when it fails it is counted as `diverged` with that id, never as a failure.
const divergence = new Map();
// A scoped divergence (`scoped`: an array of rows, each `{row, ...scope}`; see lib-ledger.mjs) is a ruled difference only inside its
// scope: its row counts as `diverged` when the recorded and the actual values are equal once the scope's allowed differences are
// removed from both, and fails otherwise.
const scopeOf = new Map();
const scopedEqual = (tag, expect, actual, fields) => {
  const views = scopedViews(scopeOf.get(tag), expect, actual);
  return compareExpect(views.expect, views.actual, fields).length === 0;
};
(function loadDivergences() {
  const dir = join(PLUGIN, "tests", "contracts", "divergence");
  const walk = (v, id) => {
    if (Array.isArray(v)) { for (const x of v) walk(x, id); return; }
    if (!v || typeof v !== "object") return;
    const own = typeof v.id === "string" ? v.id : id;
    if (Array.isArray(v.rowsExercisingNode)) for (const row of v.rowsExercisingNode) if (typeof row === "string") divergence.set(row, own);
    if (Array.isArray(v.scoped)) for (const { row, ...scope } of v.scoped) if (typeof row === "string") { divergence.set(row, own); scopeOf.set(row, scope); }
    for (const x of Object.values(v)) walk(x, own);
  };
  let names = [];
  try { names = readdirSync(dir).filter((n) => n.endsWith(".json")); } catch { return; }
  for (const n of names) {
    try { walk(JSON.parse(readFileSync(join(dir, n), "utf8")), null); } catch { /* an unreadable table names nothing */ }
  }
})();

// Whether the native binary serves at all. A binary that answers `serve` with the "not implemented natively" outcome has no
// daemon for the lifecycle scenarios to drive; they are skipped as unit-not-native rather than judged. A daemon that serves
// is still running when the probe's time is up.
let nativeServes = null;
function nativeCanServe() {
  if (nativeServes !== null) return nativeServes;
  const probe = join(T, "serve-probe");
  mkdirSync(probe, { recursive: true });
  const r = spawnSync(process.env.AHD_NATIVE_BIN, ["serve", "--ah-home", probe], { encoding: "utf8", timeout: 3000, killSignal: "SIGKILL" });
  nativeServes = !(r.status === 70 && /is not implemented natively/.test(`${r.stdout || ""}${r.stderr || ""}`));
  return nativeServes;
}
const skipped = new Map();
const cases = selected;
try {
  for (const c of cases) {
    const tag = `${c.kind}/${c.id}`;
    let result;
    try {
      if (c.kind === "lifecycle") {
        if (runImpl === "native" && !process.env.AHD_NATIVE_BIN) result = { skipped: "no-native-binary" };
        else if (runImpl === "native" && !nativeCanServe()) result = { skipped: "unit-not-native" };
        else if (c.def.pending) result = { skipped: `pending-${c.def.pending}` };
        else {
          const cd = process.env.CONTRACT_SCENARIO_IMPL || runImpl;
          const r = spawnSync("bash", [c.file], { cwd: PLUGIN, env: { ...process.env, IMPL_CLIENT: cd, IMPL_DAEMON: cd, CONTRACT_PLUGIN: PLUGIN, AH_TEST: "1" }, encoding: "utf8", timeout: 180000 });
          // exit 77 is the scenario saying the runtime it was asked to drive is not built
          result = r.status === 77 ? { skipped: runImpl === "native" ? "unit-not-native" : "runtime-not-built" } : r.status === 0 ? { actual: { exit: 0 }, lifecycle: true } : { lifecycle: true, failure: `exit ${r.status}\n${(r.stdout || "").slice(-1500)}\n${(r.stderr || "").slice(-1500)}` };
        }
      } else {
        result = await executeCase(c, { impl: runImpl, T });
      }
    } catch (e) {
      result = { failure: `tooling error: ${e.stack || e.message}` };
    }
    if (result.skipped) {
      skipped.set(result.skipped, (skipped.get(result.skipped) || 0) + 1);
      console.log(`skip ${tag}  (${result.skipped})`);
      continue;
    }
    total++;
    let ok = false, report = "";
    if (result.disagree && result.disagree.length) {
      const names = Object.keys(result.drivers);
      result.failure = `the drivers disagree: ${result.disagree.join(", ")} differ from ${names[0]}\n` + describeDiff(result.drivers[names[0]], result.drivers[result.disagree[0]]);
    }
    if (result.failure) report = result.failure + "\n";
    else if (result.lifecycle) ok = true;
    else if (!c.def.expect) report = "case has no `expect`: record it with record.mjs\n";
    else {
      const diffs = compareExpect(c.def.expect, result.actual, FIELDS[c.kind]);
      ok = diffs.length === 0;
      if (!ok) {
        const exp = {}, act = {};
        for (const f of diffs) { exp[f] = c.def.expect[f]; act[f] = result.actual[f]; }
        report = `fields differing: ${diffs.join(", ")}\n` + describeDiff(exp, act);
      }
    }
    if (ok) { passed++; console.log(`ok ${tag}`); }
    else if (!result.failure && divergence.has(tag) && !(scopeOf.has(tag) && !scopedEqual(tag, c.def.expect, result.actual, FIELDS[c.kind]))) {
      diverged++;
      const out = join(T, "diverged", c.kind, `${c.id}.diff`);
      mkdirSync(dirname(out), { recursive: true });
      writeFileSync(out, report);
      console.log(`diverged ${tag}  (${divergence.get(tag)})  diff: ${out}`);
    }
    else {
      failed++;
      if (!result.failure && divergence.has(tag) && scopeOf.has(tag) && c.def.expect) {
        const { expect: exp, actual: act } = scopedViews(scopeOf.get(tag), c.def.expect, result.actual);
        const rest = compareExpect(exp, act, FIELDS[c.kind]);
        const e = {}, a = {};
        for (const f of rest) { e[f] = exp[f]; a[f] = act[f]; }
        report = `differs beyond the ruled scope (${scopeOf.get(tag).kind}): ${rest.join(", ")}\n` + describeDiff(e, a);
      }
      const out = join(T, "fail", c.kind, `${c.id}.diff`);
      mkdirSync(dirname(out), { recursive: true });
      writeFileSync(out, report);
      console.log(`FAIL ${tag}  diff: ${out}`);
      if (failFast) break;
    }
  }
} finally {
  // one reason prints bare, several print with their counts
  const sk = skipped.size === 1 ? [...skipped.keys()][0] : [...skipped.entries()].map(([r, n]) => `${r} ${n}`).join(", ");
  const nsk = [...skipped.values()].reduce((a, b) => a + b, 0);
  skippedTotal = nsk;
  console.log(`contracts: ${passed}/${total} (${impl}), skipped: ${nsk}${sk ? ` (${sk})` : ""}`);
  if (diverged > 0) console.log(`contracts: ${passed} pass + ${diverged} diverged of ${total}`);
  if (failed === 0) { try { removeSandbox(T); } catch { /* already gone */ } }
  else console.log(`contracts: sandbox kept at ${T}`);
}
const unnamedSkips = skippedTotal - (skipped.get("named-divergence") || 0);
if (requireNative && unnamedSkips > 0 && failed === 0) console.log(`contracts: --require-native: ${unnamedSkips} case(s) were skipped`);
process.exit(failed === 0 && !(requireNative && unnamedSkips > 0) ? 0 : 1);
