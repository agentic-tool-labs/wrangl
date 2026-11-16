#!/usr/bin/env node
// Checks tests/manifest.json against the tree: it must be complete, current and consistent.
//   check-manifest.mjs [--manifest FILE] [--plugin DIR]
// Prints one line per problem and exits 1 when there is any:
//   - a tests/test-*.sh with no entry, or an entry with no file, or two entries of one name;
//   - an unknown kind or lane, or a unit that native/cutover.json does not list;
//   - an entry that the classifier (plus the hand corrections) no longer derives: kind, libs, covers or units differ;
//   - a hooks/*.mjs entry script that no suite covers (a change to it would select nothing).
// `lane` is not compared: it comes from timings of a full run, which a check cannot reproduce. Imports nothing from hooks/.

import { execFileSync } from "node:child_process";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { makeSandbox, removeSandbox } from "./lib-case.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : d; };
const PLUGIN = opt("--plugin", join(HERE, "..", ".."));
const MANIFEST = opt("--manifest", join(PLUGIN, "tests", "manifest.json"));
const problems = [];

const manifest = JSON.parse(readFileSync(MANIFEST, "utf8"));
const suites = readdirSync(join(PLUGIN, "tests")).filter((n) => /^test-.*\.sh$/.test(n)).map((n) => n.replace(/\.sh$/, "")).sort();
const seen = new Map();
for (const e of manifest) seen.set(e.name, (seen.get(e.name) || 0) + 1);
for (const s of suites) if (!seen.has(s)) problems.push(`no manifest entry for tests/${s}.sh`);
for (const [n, c] of seen) {
  if (!suites.includes(n)) problems.push(`manifest entry ${n} has no tests/${n}.sh`);
  if (c > 1) problems.push(`manifest lists ${n} ${c} times`);
}

let units = null;
try { units = new Set(JSON.parse(readFileSync(join(PLUGIN, "native", "cutover.json"), "utf8")).units || []); } catch { units = null; }
const KINDS = new Set(["contract", "mixed", "internal", "static"]);
const LANES = new Set(["fast", "full", "serial"]);
for (const e of manifest) {
  if (!KINDS.has(e.kind)) problems.push(`${e.name}: unknown kind ${JSON.stringify(e.kind)}`);
  if (!LANES.has(e.lane)) problems.push(`${e.name}: unknown lane ${JSON.stringify(e.lane)}`);
  if (units) for (const u of e.units || []) if (!units.has(u)) problems.push(`${e.name}: unit ${u} is not in native/cutover.json`);
}

// what the classifier derives now, without timings
const tmp = makeSandbox();
try {
  const out = join(tmp, "m.json");
  execFileSync(process.execPath, [join(HERE, "classify.mjs"), "--plugin", PLUGIN, "--out", out], { stdio: "pipe" });
  const fresh = new Map(JSON.parse(readFileSync(out, "utf8")).map((e) => [e.name, e]));
  const keys = ["kind", "libs", "covers", "units", "reads_source"];
  for (const e of manifest) {
    const f = fresh.get(e.name);
    if (!f) continue;
    for (const k of keys) if (JSON.stringify(e[k]) !== JSON.stringify(f[k])) problems.push(`${e.name}: ${k} is stale (run classify.mjs); committed ${JSON.stringify(e[k])}, derived ${JSON.stringify(f[k])}`.slice(0, 300));
  }
} finally {
  removeSandbox(tmp);
}

// a hook or CLI entry script that no suite covers
const covered = new Set(manifest.flatMap((e) => e.covers || []));
const globRe = (g) => new RegExp("^" + g.replace(/[.+^${}()|[\]\\]/g, "\\$&").replace(/\*\*/g, "\u0000").replace(/\*/g, "[^/]*").replace(/\u0000/g, ".*") + "$");
const globs = [...covered].filter((c) => c.includes("*")).map(globRe);
for (const f of readdirSync(join(PLUGIN, "hooks")).filter((n) => n.endsWith(".mjs")).sort()) {
  const p = `hooks/${f}`;
  if (!covered.has(p) && !globs.some((re) => re.test(p))) problems.push(`${p} is covered by no suite`);
}

if (problems.length) {
  for (const p of problems) console.log(p);
  process.exit(1);
}
console.log(`manifest: ${manifest.length} entries, ${suites.length} suites, complete and current`);
