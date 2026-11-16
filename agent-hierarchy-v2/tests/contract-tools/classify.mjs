#!/usr/bin/env node
// Generates the first version of tests/manifest.json from a grep heuristic over the test files.
//   classify.mjs [--tests DIR] [--plugin DIR] [--timings RESULTS_TXT] [--out FILE]
//
// One entry per tests/test-*.sh: {name, kind, libs, covers, units, lane, reads_source}.
//   libs         the hooks/lib-*.mjs files the test names (import forms and path mentions)
//   kind         B only (spawns a hook or CLI by path) -> contract; A+B -> mixed; A only (lib imports) -> internal; neither -> static
//   covers       the hooks/*.mjs paths the test names, plus hooks/hooks.json and bin/* when it names them
//   lane         serial for the names in run-all.sh's SERIAL_LANE; full for SLOW_FIRST and for a test that took over 60 s in the
//                timings file (a run directory's results.txt: "<name> <status> <seconds>"); otherwise fast
//   reads_source the test reads a hooks/ file as text (grep, cat, sed, readFileSync ... on a hooks/ path)
//   units        the cutover units (native/cutover.json) the test exercises: `hook:<name>` for a registered hook script it covers,
//                `cli:<name>` for gate, issue-intake and usage-report, the families of roster.mjs, msg.mjs and ahd-ctl.mjs it covers,
//                `daemon` for bin/ahd; every unit when the test names the cutover manifest or the dispatch preamble
// A .sh wrapper that runs tests/test-*.mjs is classified on the wrapper plus the .mjs it runs.
// Imports nothing from hooks/ (the tooling must not share code with the implementation it judges).

import { readFileSync, readdirSync, existsSync, writeFileSync } from "node:fs";
import { join, dirname, basename } from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";

const HERE = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (name, dflt) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : dflt; };
const PLUGIN = opt("--plugin", join(HERE, "..", ".."));
const TESTS = opt("--tests", join(PLUGIN, "tests"));
const TIMINGS = opt("--timings", null);
const OUT = opt("--out", join(TESTS, "manifest.json"));

const runAll = readFileSync(join(TESTS, "run-all.sh"), "utf8");
const serial = new Set(execFileSync("bash", [join(TESTS, "run-all.sh"), "--serial-lane"], { encoding: "utf8" }).split("\n").filter(Boolean));
const slowBlock = /SLOW_FIRST='\n([\s\S]*?)'/.exec(runAll);
const slowFirst = new Set(slowBlock ? slowBlock[1].split("\n").map((l) => l.replace(/#.*/, "").trim()).filter(Boolean) : []);
const secs = new Map();
if (TIMINGS) for (const l of readFileSync(TIMINGS, "utf8").split("\n")) { const m = /^(\S+) \S+ (\d+)$/.exec(l.trim()); if (m) secs.set(m[1], Number(m[2])); }

// a lib is imported: import("...lib-x.mjs"), import ... from "...lib-x.mjs", require("...lib-x.mjs")
const LIB_RE = /(?:import\s*\(\s*|\bfrom\s+|require\s*\(\s*)["'`][^"'`]*?\b(lib-[a-z0-9-]+\.mjs)["'`]/g;
// a hook or CLI script is run by path: `node [flags] <path>/x.mjs`, or a variable set to such a path
const SPAWN_RE = /\bnode(?:\s+--?[\w=-]+)*\s+["']?[^\s"']*\/((?!lib-)[a-z0-9][a-z0-9-]*)\.mjs\b/g;
const VARSPAWN_RE = /\b[A-Za-z_][A-Za-z0-9_]*=["']?[^\s"']*(?:\/hooks|\$\{?[A-Za-z_]*H(?:OOKS?)?\}?)\/((?!lib-)[a-z0-9][a-z0-9-]*)\.mjs\b/g;
const BIN_RE = /(?:^|[\s"'=\/])(?:\$\{?[A-Za-z_]+\}?\/|\.\/|[A-Za-z0-9_.\/-]*\/)?bin\/(ahd|ah-hook)\b/g;
const HOOKS_PATH_RE = /\bhooks\/[A-Za-z0-9_.-]+/g;
const READ_VERBS = /\b(grep|egrep|fgrep|cat|sed|awk|head|tail|wc|cut|readFileSync|readFile|diff|cmp)\b/;

function slurp(file) {
  let text = readFileSync(file, "utf8");
  // inline the tests/test-*.mjs a wrapper runs
  for (const m of text.matchAll(/\btest-[a-z0-9-]+\.mjs\b/g)) {
    const f = join(TESTS, m[0]);
    if (f !== file && existsSync(f)) text += "\n" + readFileSync(f, "utf8");
  }
  return text;
}

// the cutover units, from the manifest the plugin ships (none when it is absent)
let unitNames = [];
try { unitNames = [...(JSON.parse(readFileSync(join(PLUGIN, "native", "cutover.json"), "utf8")).units || [])].sort(); } catch { unitNames = []; }
const FAMILY_PREFIX = { "roster.mjs": "roster:", "msg.mjs": "msg:", "ahd-ctl.mjs": "ahd:" };
function unitsOf(covers, text) {
  if (unitNames.length === 0) return [];
  if (/\bcutover\.json\b|\blib-dispatch\b/.test(text)) return unitNames;
  const out = new Set();
  for (const c of covers) {
    const m = /^hooks\/([a-z0-9-]+\.mjs)$/.exec(c);
    if (m) {
      const base = m[1].replace(/\.mjs$/, "");
      for (const u of [`hook:${base}`, `cli:${base}`]) if (unitNames.includes(u)) out.add(u);
      if (FAMILY_PREFIX[m[1]]) for (const u of unitNames) if (u.startsWith(FAMILY_PREFIX[m[1]]) || (m[1] === "ahd-ctl.mjs" && u === "tui")) out.add(u);
    } else if (c === "bin/ahd" && unitNames.includes("daemon")) out.add("daemon");
  }
  return [...out].sort();
}
const entries = [];
for (const f of readdirSync(TESTS).filter((n) => /^test-.*\.sh$/.test(n)).sort()) {
  const name = basename(f, ".sh");
  const text = slurp(join(TESTS, f));
  // only libs that exist in hooks/ count (a test names its own lib-*.sh and tool files by the same forms)
  const libs = [...new Set([...text.matchAll(LIB_RE)].map((m) => m[1]))].filter((l) => existsSync(join(PLUGIN, "hooks", l))).sort();
  const spawns = new Set();
  for (const m of text.matchAll(SPAWN_RE)) spawns.add(m[1] + ".mjs");
  for (const m of text.matchAll(VARSPAWN_RE)) spawns.add(m[1] + ".mjs");
  for (const m of text.matchAll(BIN_RE)) spawns.add("bin/" + m[1]);
  // only scripts that exist in hooks/ or bin/ count (a test runs its own tests/*.mjs and fixtures by the same forms)
  for (const s of [...spawns]) {
    const real = s.startsWith("bin/") ? existsSync(join(PLUGIN, s)) : existsSync(join(PLUGIN, "hooks", s));
    if (!real) spawns.delete(s);
  }
  const hasLib = libs.length > 0;
  const hasSpawn = spawns.size > 0;
  const kind = hasSpawn && hasLib ? "mixed" : hasSpawn ? "contract" : hasLib ? "internal" : "static";
  const covers = new Set();
  for (const m of text.matchAll(HOOKS_PATH_RE)) if (/\.(mjs|json)$/.test(m[0]) && existsSync(join(PLUGIN, m[0]))) covers.add(m[0]);
  for (const s of spawns) covers.add(s.startsWith("bin/") ? s : `hooks/${s}`);
  for (const l of libs) covers.add(`hooks/${l}`);
  let readsSource = false;
  for (const line of text.split("\n")) if (READ_VERBS.test(line) && /\bhooks\/[A-Za-z0-9_.-]+\.(mjs|json)\b|\$\{?[A-Za-z_]+\}?\/[A-Za-z0-9_-]+\.mjs\b/.test(line) && !/\bnode\b.*\$\{?[A-Za-z_]+\}?\/[A-Za-z0-9_-]+\.mjs\b/.test(line) && !/import\(/.test(line)) { readsSource = true; break; }
  const slow = slowFirst.has(name) || (secs.get(name) || 0) > 60;
  const lane = serial.has(name) ? "serial" : slow ? "full" : "fast";
  const sortedCovers = [...covers].sort();
  entries.push({ name, kind, libs, covers: sortedCovers, units: unitsOf(sortedCovers, text), lane, reads_source: readsSource });
}
// hand corrections (tests/manifest-corrections.json, explained in tests/manifest-notes.md), applied by suite name
const corrFile = join(TESTS, "manifest-corrections.json");
if (existsSync(corrFile)) {
  const corrDoc = JSON.parse(readFileSync(corrFile, "utf8"));
  const corr = corrDoc.corrections || {};
  for (const [name, patch] of Object.entries(corr)) {
    const e = entries.find((x) => x.name === name);
    if (e) {
      Object.assign(e, patch);
      if (patch.units === "all") e.units = unitNames;
    }
  }
  // globs added to every suite whose name starts with the prefix, after the corrections (families that drive a project's verbs)
  for (const [prefix, globs] of Object.entries(corrDoc.family_covers || {})) {
    for (const e of entries) if (e.name.startsWith(prefix)) e.covers = [...new Set([...e.covers, ...globs])].sort();
  }
}
writeFileSync(OUT, JSON.stringify(entries, null, 2) + "\n");
const count = (k) => entries.filter((e) => e.kind === k).length;
console.log(`manifest: ${entries.length} suites -> ${OUT}`);
console.log(`  contract ${count("contract")}, mixed ${count("mixed")}, internal ${count("internal")}, static ${count("static")}`);
console.log(`  lanes: fast ${entries.filter((e) => e.lane === "fast").length}, full ${entries.filter((e) => e.lane === "full").length}, serial ${entries.filter((e) => e.lane === "serial").length}; reads_source ${entries.filter((e) => e.reads_source).length}`);
