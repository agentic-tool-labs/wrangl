#!/usr/bin/env node
// Imports the E-4 JSON byte fixture into tests/contracts/state/samples/json-bytes/ through its generator, re-run against Node.
//   import-json-bytes.mjs --rev <git rev> [--out DIR] [--check]      (the rev may also come from AH_JSON_PARITY_REV)
// The generator (native/spikes/ev-b/json-parity/parity.mjs) is read from the given revision with `git show`; it writes the 71
// input values and, for each indent 0, 1 and 2, what `JSON.stringify(JSON.parse(text), null, n) + "\n"` gives (a `.err` file
// holds the error class for an input Node rejects). Nothing is copied by hand. Imports nothing from hooks/ (G2).
// Exit 3 (nothing was compared) when no revision is given or the revision is not in this repository: the committed files are
// data, and only a re-import needs the generator's revision.

import { execFileSync, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { PLUGIN, makeSandbox, removeSandbox } from "./lib-case.mjs";

const argv = process.argv.slice(2);
const val = (n, d) => { const i = argv.indexOf(n); return i >= 0 ? argv[i + 1] : d; };
const REV = val("--rev", process.env.AH_JSON_PARITY_REV || "");
const OUT = val("--out", join(PLUGIN, "tests", "contracts", "state", "samples", "json-bytes"));
const check = argv.includes("--check");
const GEN_PATH = "native/spikes/ev-b/json-parity/parity.mjs";

if (!REV) {
  console.log("json-bytes: no generator revision given (--rev <git rev> or AH_JSON_PARITY_REV); nothing compared");
  process.exit(3);
}
let top, sha, gen;
try {
  top = execFileSync("git", ["-C", PLUGIN, "rev-parse", "--show-toplevel"], { encoding: "utf8", stdio: "pipe" }).trim();
  sha = execFileSync("git", ["-C", top, "rev-parse", "--short", `${REV}^{commit}`], { encoding: "utf8", stdio: "pipe" }).trim();
  gen = execFileSync("git", ["-C", top, "show", `${REV}:${GEN_PATH}`], { encoding: "utf8", stdio: "pipe", maxBuffer: 16 * 1024 * 1024 });
} catch {
  console.log(`json-bytes: revision ${REV} (or its ${GEN_PATH}) is not in this repository; nothing compared`);
  process.exit(3);
}

const T = makeSandbox();
try {
  const g = join(T, "gen.mjs");
  writeFileSync(g, gen);
  const run = (...a) => { const r = spawnSync(process.execPath, [g, ...a], { encoding: "utf8" }); if (r.status !== 0) throw new Error(`generator failed: ${r.stderr}`); return r.stdout; };
  const inDir = join(T, "in");
  run("gen", inDir);
  const names = readdirSync(inDir).sort();
  const indexRows = [];
  const files = new Map(); // relative path -> Buffer
  for (const n of names) files.set(`in/${n}`, readFileSync(join(inDir, n)));
  for (const indent of [0, 1, 2]) {
    const exp = join(T, `exp${indent}`);
    run("expect", inDir, exp, String(indent));
    for (const n of readdirSync(exp).sort()) files.set(`expected-indent${indent}/${n}`, readFileSync(join(exp, n)));
  }
  for (const n of names) indexRows.push({ name: n.replace(/\.json$/, ""), rejected_by_node: files.has(`expected-indent0/${n}.err`) });
  files.set("index.json", Buffer.from(JSON.stringify({ source: { rev: sha, path: GEN_PATH, note: "generator re-run against Node; the values are not copied by hand" }, cases: indexRows.sort((a, b) => (a.name < b.name ? -1 : 1)) }, null, 2) + "\n"));
  if (check) {
    const drift = [];
    for (const [rel, buf] of files) {
      const f = join(OUT, rel);
      if (!existsSync(f) || !readFileSync(f).equals(buf)) drift.push(rel);
    }
    if (drift.length) { console.log("json-bytes drift:\n  " + drift.join("\n  ")); process.exit(1); }
    console.log(`json-bytes: ${files.size} files, no drift`);
  } else {
    for (const [rel, buf] of files) {
      const f = join(OUT, rel);
      mkdirSync(dirname(f), { recursive: true });
      writeFileSync(f, buf);
    }
    console.log(`json-bytes: wrote ${files.size} files (${names.length} values) from ${sha}`);
  }
} finally {
  removeSandbox(T);
}
