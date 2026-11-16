#!/usr/bin/env node
// Re-records the stored expectation of recorded cases from the native binary's normalized output.
//   AHD_NATIVE_BIN=<ahd> node tests/contract-tools/rerecord.mjs [--kind hook|cli|wire|state-op]... [--only GLOB]... [--fields key,key] [--write] [--recheck-mutants]
// Without --write it only lists what would change. With --fields only the named top-level expect keys are replaced (for example
// `--fields stdout` leaves a stored tree_diff exactly as it is); the other keys stay byte for byte, and a case whose named keys
// match is left alone. Name the cases with --only: a case a divergence ledger rules on differs from
// its recording on purpose, and this tool does not know that. A case is re-recorded only when its output differs from its `expect`, and
// only the differing fields are replaced in the stored case file, so unchanged fields keep their stored spelling and the file
// keeps its layout. Use it when a deliberate product change made a recording stale; read the listed diffs first (run.mjs prints
// them), because re-recording a defect pins the defect. Skipped cases (named divergences, units the binary lacks) are not touched.
// A case that carries a `mutant` (the wrong behaviour its expectation exists to catch) is left alone unless --recheck-mutants is
// given; with it the case is re-recorded and listed at the end, so each mutant can be re-run against the new expectation.
// Lifecycle scenarios are shell scripts with no stored expectation and are not handled.

import { readFileSync, writeFileSync } from "node:fs";
import { FIELDS, KINDS, compareExpect, executeCase, loadCases, matchesOnly } from "./lib-run.mjs";
import { makeSandbox, removeSandbox, runAsHarness, toPluginNamespace, withV1ConventionsFallback } from "./lib-case.mjs";

runAsHarness();

const argv = process.argv.slice(2);
const values = (n) => argv.reduce((a, x, i) => (x === n ? [...a, argv[i + 1]] : a), []);
const known = new Set(["--kind", "--only", "--fields", "--write", "--recheck-mutants", "--help"]);
for (let i = 0; i < argv.length; i++) {
  if (argv[i].startsWith("--") && !known.has(argv[i])) { console.error(`rerecord: unknown option ${argv[i]}`); process.exit(2); }
  if (["--kind", "--only", "--fields"].includes(argv[i])) i++;
}
if (argv.includes("--help")) { console.log("usage: rerecord.mjs [--kind hook|cli|wire|state-op]... [--only GLOB]... [--fields key,key] [--write] [--recheck-mutants]"); process.exit(0); }
if (!process.env.AHD_NATIVE_BIN) { console.error("rerecord: AHD_NATIVE_BIN must name the native binary"); process.exit(2); }

const kinds = values("--kind").length ? values("--kind") : KINDS.filter((k) => k !== "lifecycle");
for (const k of kinds) if (!KINDS.includes(k) || k === "lifecycle") { console.error(`rerecord: unsupported --kind ${k}`); process.exit(2); }
const onlys = values("--only");
const write = argv.includes("--write");
const recheckMutants = argv.includes("--recheck-mutants");
const only = values("--fields").flatMap((v) => v.split(",")).filter(Boolean);
const recheck = [];

const T = makeSandbox();
let changed = 0, same = 0, skipped = 0;
try {
  for (const c of loadCases(kinds).filter((x) => onlys.length === 0 || onlys.some((o) => matchesOnly(x, o)))) {
    const tag = `${c.kind}/${c.id}`;
    if (!c.def.expect) { console.log(`skip ${tag}: no stored expect`); skipped++; continue; }
    const r = await executeCase(c, { impl: "native", T });
    if (!r.actual) { console.log(`skip ${tag}: ${r.skipped ?? r.failure ?? "no result"}`); skipped++; continue; }
    const diffs = compareExpect(c.def.expect, r.actual, FIELDS[c.kind]).filter((f) => only.length === 0 || only.includes(f));
    if (diffs.length === 0) { same++; continue; }
    if (c.def.mutant && !recheckMutants) { console.log(`skip ${tag}: carries a mutant; re-run with --recheck-mutants to re-record it and list it for a mutant re-run`); skipped++; continue; }
    const text = readFileSync(c.file, "utf8");
    const raw = JSON.parse(text);
    if (JSON.stringify(raw, null, 2) + "\n" !== text) { console.log(`skip ${tag}: rewriting would reformat the file; edit it by hand`); skipped++; continue; }
    for (const f of diffs) raw.expect[f] = r.actual[f];
    // the runner reads a stored case through the namespace and layout translation, so a recording is exact only when what it reads
    // back equals what was run; text the translation would rewrite (a literal `ah:` the binary prints) cannot be pinned this way
    const readBack = withV1ConventionsFallback(JSON.parse(toPluginNamespace(JSON.stringify(raw, null, 2) + "\n")));
    const inexact = compareExpect(readBack.expect, r.actual, diffs);
    if (inexact.length) { console.log(`skip ${tag}: ${inexact.join(", ")} would not read back as recorded (the load-time translation rewrites it); fix the text or add a named divergence`); skipped++; continue; }
    console.log(`${write ? "rewrote" : "would rewrite"} ${tag}: ${diffs.join(", ")}`);
    if (write) writeFileSync(c.file, JSON.stringify(raw, null, 2) + "\n");
    if (c.def.mutant) recheck.push(tag);
    changed++;
  }
} finally {
  removeSandbox(T);
}
for (const tag of recheck) console.log(`mutant re-run: ${tag}`);
console.log(`rerecord: ${changed} ${write ? "rewritten" : "would change"}, ${same} unchanged, ${skipped} skipped`);
