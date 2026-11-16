#!/usr/bin/env node
// Suite selection for tests/run-all.sh: --lane and --changed, from tests/manifest.json.
//   select.mjs --manifest FILE --names FILE [--lane fast|full|all] [--with-serial] [--changed-list "<paths, one per line>"]
// --names lists the candidate suite names (one per line, after any PREFIX); the selected names are printed one per line.
// --changed-list holds plugin-relative paths of the changed files (one per line). A path selects the suites whose `covers`
// globs match it. A `native/**` path selects every suite with a non-empty `units` (until SS-1 maps a project to its units).
// A suite with an empty `covers` is never selected by --changed. Imports nothing from hooks/.

import { readFileSync } from "node:fs";

const args = process.argv.slice(2);
const opt = (n, d) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : d; };
const manifest = JSON.parse(readFileSync(opt("--manifest"), "utf8"));
const names = readFileSync(opt("--names"), "utf8").split("\n").filter(Boolean);
const lane = opt("--lane", "all");
const withSerial = args.includes("--with-serial");
// --native-covers (opt-in; tests/contract-tools/affected-select.mjs and run-set.sh's affected set pass it): a native/ path selects the suites whose covers match it, and one
// no covers glob matches selects no suite and is reported on stderr as `no shell suite covers <path>`. Without it, any native/ path selects every suite with units.
const nativeCovers = args.includes("--native-covers");
const changedList = opt("--changed-list", null);
const byName = new Map(manifest.map((e) => [e.name, e]));

/** Glob to RegExp: ** crosses directories, * stays within a segment, ? is one character. */
function globRe(g) {
  let re = "";
  for (let i = 0; i < g.length; i++) {
    const c = g[i];
    if (c === "*") {
      if (g[i + 1] === "*") { re += ".*"; i++; if (g[i + 1] === "/") i++; } else re += "[^/]*";
    } else if (c === "?") re += "[^/]";
    else re += c.replace(/[.+^${}()|[\]\\]/g, "\\$&");
  }
  return new RegExp(`^${re}$`);
}

const changed = changedList !== null ? changedList.split("\n").filter(Boolean) : null;
const out = [];
for (const n of names) {
  const e = byName.get(n);
  if (!e) {
    if (lane === "all" && changed === null) out.push(n); // no manifest-based narrowing asked for
    else console.error(`select: ${n} is not in the manifest; not selected`);
    continue;
  }
  if (lane !== "all") {
    if (e.lane === "serial") { if (!withSerial) continue; } else if (e.lane !== lane) continue;
  }
  if (changed !== null) {
    let hit = false;
    for (const p of changed) {
      if (p.startsWith("native/") && !nativeCovers) { if (e.units && e.units.length > 0) { hit = true; break; } continue; }
      if (e.covers.some((g) => globRe(g).test(p))) { hit = true; break; }
    }
    if (!hit) continue;
  }
  out.push(n);
}
if (nativeCovers && changed !== null) {
  const eligible = names.map((n) => byName.get(n)).filter((e) => e && (lane === "all" || e.lane === lane || (e.lane === "serial" && withSerial)));
  for (const p of changed) if (p.startsWith("native/") && !eligible.some((e) => e.covers.some((g) => globRe(g).test(p)))) console.error(`no shell suite covers ${p}`);
}
process.stdout.write(out.map((n) => n + "\n").join(""));
