#!/usr/bin/env node
// The suite half of the command-form switch (0110 §3.4): every shell suite that ran a Node script the flag-day commit deletes runs
// the shim instead.
//   node "$H/roster.mjs" status          ->  "$PLUGIN/bin/ah" roster status       (msg, gate, usage-report, issue-intake alike)
//   node "$H/ahd-ctl.mjs" status         ->  "$PLUGIN/bin/ahd" status
//   node "$H/pretooluse-x.mjs"           ->  sh "$PLUGIN/bin/ah-hook" pretooluse-x   (any hook unit)
// where H="$PLUGIN/hooks" (or the literal "$PLUGIN/hooks/x.mjs") names the plugin root the shim lives under.
//   cp -R "$PLUGIN/hooks" … "$D/"        ->  the same copy with "$PLUGIN/bin" beside it, so a copied plugin has its shims
// A variable that names a script (CTL="$H/ahd-ctl.mjs") is followed to its uses; the assignment is left as written.
//   rewrite-z1.mjs            rewrite in place and print what is left
//   rewrite-z1.mjs --check    list the files that would change; exit 1 when there are any
// What this does not change (a suite that imports a deleted library, or names a script some other way) is listed as "left" for the
// person or run that has to decide it; nothing is skipped silently. Idempotent: a second run changes nothing.
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const tests = join(root, "tests");
const deleted = new Set(JSON.parse(readFileSync(join(tests, "contracts", "z-deleted-node-files.json"), "utf8")).deleted);

const CLI = new Map([
  ["roster", ["ah", "roster"]],
  ["msg", ["ah", "msg"]],
  ["usage-report", ["ah", "usage-report"]],
  ["issue-intake", ["ah", "issue-intake"]],
  ["ahd-ctl", ["ahd"]],
]);
// Daemon and viewer mains are not hook units and have no shim entry of their own.
const NOT_A_UNIT = new Set(["ahd", "ahd-tui"]);

// q is the quote character as written in the source: a plain double quote, or a backslash-escaped one inside an eval string.
const command = (rootExpr, name, q = '"') => {
  if (CLI.has(name)) {
    const [bin, ...rest] = CLI.get(name);
    return [`${q}${rootExpr}/bin/${bin}${q}`, ...rest].join(" ");
  }
  if (name.startsWith("lib-") || NOT_A_UNIT.has(name)) return null;
  return `sh ${q}${rootExpr}/bin/ah-hook${q} ${name}`;
};

// `<root>/hooks` as written, or a variable known to hold one, as the plugin root expression (null when neither).
const rootOf = (target, hooksDir, script) => {
  // Only a root written as a shell variable is a command to run; a literal path is test data (a string a gate parses).
  let m = /^(\$.*)\/hooks\/([a-z0-9-]+)\.mjs$/.exec(target);
  if (m) return { root: m[1], name: m[2] };
  m = /^\$\{?(\w+)\}?\/([a-z0-9-]+)\.mjs$/.exec(target);
  if (m && hooksDir.has(m[1])) return { root: hooksDir.get(m[1]), name: m[2] };
  m = /^\$\{?(\w+)\}?$/.exec(target);
  if (m && script.has(m[1])) return script.get(m[1]);
  return null;
};

const ASSIGN = /(?:^|[;\s])(?:(?:export|local|readonly)\s+)?([A-Za-z_]\w*)=("(?:[^"\\]|\\.)*"|'[^']*'|[^\s;]+)/g;
const NODE_CALL = /\bnode\s+((?:--?[\w-]+(?:=\S+)?\s+)*)(?:"([^"]+)"|([^\s"';|&)]+))/g;
const NODE_CALL_ESCAPED = /\bnode\s+((?:--?[\w-]+(?:=\S+)?\s+)*)\\"([^"\\]+)\\"/g;

export function rewrite(text) {
  const hooksDir = new Map();
  // A sourced suite library may define the hooks directory variable (lib-status-pool.sh: H="$PLUGIN/hooks").
  for (const lib of text.matchAll(/^\s*\.\s+"\$PLUGIN\/tests\/(lib-[a-z0-9-]+\.sh)"/gm)) {
    let body = "";
    try { body = readFileSync(join(tests, lib[1]), "utf8"); } catch { continue; }
    for (const a of body.matchAll(ASSIGN)) {
      const value = a[2].replace(/^["']|["']$/g, "");
      const m = /^(\$.*)\/hooks\/?$/.exec(value);
      if (m) hooksDir.set(a[1], m[1]);
    }
  }
  const script = new Map();
  const assignedAt = new Map();
  const lines = text.split("\n");
  let out = lines.map((line, i) => {
    if (/^\s*#/.test(line)) return line;
    // Assignments first, in order, so a later line sees an earlier variable.
    for (const a of line.matchAll(ASSIGN)) {
      const value = a[2].replace(/^["']|["']$/g, "");
      const m = /^(.*)\/hooks\/?$/.exec(value);
      if (m && m[1] !== "") { hooksDir.set(a[1], m[1]); continue; }
      const r = rootOf(value, hooksDir, script);
      if (r && deleted.has(r.name)) { script.set(a[1], r); assignedAt.set(a[1], i); }
    }
    let next = line.replace(NODE_CALL, (whole, flags, quoted, bare) => {
      const r = rootOf(quoted ?? bare, hooksDir, script);
      if (!r || !deleted.has(r.name) || flags.trim() !== "") return whole;
      return command(r.root, r.name) ?? whole;
    });
    next = next.replace(NODE_CALL_ESCAPED, (whole, flags, target) => {
      const r = rootOf(target, hooksDir, script);
      if (!r || !deleted.has(r.name) || flags.trim() !== "") return whole;
      return command(r.root, r.name, '\\"') ?? whole;
    });
    if (/\bcp\s+-[a-zA-Z]*[rR][a-zA-Z]*\s+"(\$\{?\w+\}?)\/hooks"/.test(next) && !/\/bin"/.test(next)) {
      next = next.replace(/(cp\s+-[a-zA-Z]*[rR][a-zA-Z]*\s+)"(\$\{?\w+\}?)\/hooks"/, '$1"$2/hooks" "$2/bin"');
    }
    return next;
  });
  // A script variable that nothing refers to any more loses its assignment line (a line that is only that assignment).
  for (const [name, at] of assignedAt) {
    const use = new RegExp(`\\$\\{?${name}\\b`);
    const only = new RegExp(`^\\s*(?:(?:export|local|readonly)\\s+)?${name}=("[^"]*"|'[^']*'|\\S+)\\s*$`);
    if (only.test(out[at]) && !out.some((l, j) => j !== at && use.test(l))) out[at] = null;
  }
  out = out.filter((l) => l !== null);
  const left = [];
  out.forEach((line, i) => {
    if (/^\s*#/.test(line)) return;
    for (const m of line.matchAll(/\/([a-z0-9-]+)\.mjs\b/g)) {
      if (deleted.has(m[1])) { left.push({ line: i + 1, name: m[1] }); break; }
    }
  });
  return { text: out.join("\n"), left };
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const check = process.argv.includes("--check");
  const files = readdirSync(tests).filter((f) => f.endsWith(".sh")).sort().map((f) => join(tests, f));
  let changed = 0;
  const leftBy = [];
  for (const f of files) {
    const before = readFileSync(f, "utf8");
    const r = rewrite(before);
    if (r.left.length > 0) leftBy.push(`${relative(root, f)}: ${[...new Set(r.left.map((x) => x.name))].join(",")} (first at line ${r.left[0].line})`);
    if (r.text === before) continue;
    changed++;
    if (check) console.log(relative(root, f));
    else writeFileSync(f, r.text);
  }
  if (!check) {
    console.log(`rewrote ${changed} file(s); ${leftBy.length} still name a deleted script:`);
    for (const l of leftBy) console.log(`  left: ${l}`);
  }
  process.exit(check && changed > 0 ? 1 : 0);
}
