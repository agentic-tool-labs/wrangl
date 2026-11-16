#!/usr/bin/env node
// The prose half of the command-form switch (0110 §3.3, 0114 §4c): every instruction in the living docs that runs a Node script
// the flag-day commit deletes becomes the native form. Idempotent: a second run changes nothing, which is what proves none is left.
//   rewrite-z-docs.mjs          rewrite in place
//   rewrite-z-docs.mjs --check  list the files that would change; exit 1 when there are any
//
// Scope: agents/, commands/, skills/, README.md and the top level of docs/. docs/specs and docs/retired are
// records of what was decided or found at the time, not instructions, and CHANGELOG.md's history is never rewritten.
// A script with no native form yet is left as written and listed in NO_NATIVE_FORM_YET (none today).
import { readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");

export const NO_NATIVE_FORM_YET = [];

// The scripts the cutover deletes, by name: the hooks directory no longer holds them.
const hookScripts = JSON.parse(readFileSync(join(root, "tests", "contracts", "z-deleted-node-files.json"), "utf8")).deleted
  .filter((n) => !n.startsWith("lib-"));
const CLI = new Map([
  ["roster", "ah roster"],
  ["msg", "ah msg"],
  ["usage-report", "ah usage-report"],
  ["issue-intake", "ah issue-intake"],
  ["ahd-ctl", "ahd"],
]);
const HOOKS = hookScripts.filter((n) => !CLI.has(n) && !NO_NATIVE_FORM_YET.includes(n) && n !== "ahd" && n !== "ahd-tui");

function* walk(dir) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) yield* walk(p);
    else yield p;
  }
}

const files = [];
for (const d of ["agents", "commands", "skills"]) for (const f of walk(join(root, d))) if (/\.(md|html)$/.test(f)) files.push(f);
for (const name of readdirSync(join(root, "docs"))) {
  const p = join(root, "docs", name);
  if (statSync(p).isFile() && /\.(md|html)$/.test(name)) files.push(p);
}
files.push(join(root, "README.md"));

// docs/participation.md is the committed render of the contract the daemon serves; the daemon's own text decides its wording.
const RENDERS = new Set([join(root, "docs", "participation.md")]);

// `node <path>/hooks/<cli>.mjs`, quoted or not, with the path as a variable, a placeholder or an ellipsis.
const PREFIX = "(?:\\$\\{CLAUDE_PLUGIN_ROOT\\}|<[^<>`]+>|[^\\s\"'`<>]*?)";
const NODE_CLI = new RegExp(`(?:node\\s+)?(["']?)(${PREFIX})(?:/)?hooks/(roster|msg|gate|ahd-ctl|usage-report|issue-intake)\\.mjs\\1`, "g");
const NODE_PLACEHOLDER = /node\s+(["'])<gate\.mjs>\1/g;
const NODE_GUARD = new RegExp(`node\\s+(["']?)(${PREFIX})(?:/)?hooks/pretooluse-push-guard\\.mjs\\1(?=\\s+(?:check|merge-check|protection)\\b)`, "g");
const BARE_CLI = /(?<![\w/.-])(roster|msg|gate|ahd-ctl|usage-report|issue-intake)\.mjs\b/g;
const HOOK_FILE = new RegExp(`(?:\\.{3}/|[\\w$\\{\\}<>.-]*/)?hooks/(${HOOKS.join("|")})\\.mjs|(?<![\\w/.-])(${HOOKS.join("|")})\\.mjs\\b`, "g");

function native(prefix, cli, quote) {
  const p = prefix === "" ? "" : `${prefix}/`;
  const bin = cli === "ahd" || cli === "ahd " ? `${p}bin/ahd` : `${p}bin/ah`;
  const word = cli;
  return word === "ahd" ? `${quote}${bin}${quote}` : `${quote}${bin}${quote} ${word.replace(/^ah /, "")}`;
}

export function rewrite(text) {
  let t = text;
  t = t.replace(NODE_GUARD, (_m, q, prefix) => `${native(prefix, "ah push-guard", q)}`);
  t = t.replace(NODE_PLACEHOLDER, () => "ah gate");
  t = t.replace(NODE_CLI, (_m, q, prefix, name) => native(prefix, CLI.get(name), q));
  t = t.replace(BARE_CLI, (_m, name) => CLI.get(name));
  t = t.replace(HOOK_FILE, (_m, a, b) => `ah-hook ${a ?? b}`);
  return t;
}

const check = process.argv.includes("--check");
let changed = 0;
for (const f of files) {
  if (RENDERS.has(f)) continue;
  const before = readFileSync(f, "utf8");
  const after = rewrite(before);
  if (after === before) continue;
  changed++;
  if (check) console.log(relative(root, f));
  else writeFileSync(f, after);
}
if (check) process.exit(changed > 0 ? 1 : 0);
else console.log(`rewrote ${changed} file(s)`);
