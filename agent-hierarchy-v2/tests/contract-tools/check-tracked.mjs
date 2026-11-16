#!/usr/bin/env node
// Fails when a file under tests/contracts/ is untracked or ignored by git: a fixture that .gitignore hides passes only in the
// worktree that holds it, and is missing from every clone and archive.
//   check-tracked.mjs [--root PLUGIN_DIR]     exit 0 clean, 1 with the offending paths, 3 not inside a git work tree
// Imports nothing from hooks/ (G2).

import { execFileSync } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const args = process.argv.slice(2);
const i = args.indexOf("--root");
const root = i >= 0 ? args[i + 1] : join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const git = (...a) => execFileSync("git", ["-C", root, ...a], { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
try {
  git("rev-parse", "--is-inside-work-tree");
} catch {
  console.log("check-tracked: not inside a git work tree; nothing checked");
  process.exit(3);
}
const untracked = git("ls-files", "--others", "--exclude-standard", "--", "tests/contracts").split("\n").filter(Boolean);
const ignored = git("ls-files", "--others", "--ignored", "--exclude-standard", "--", "tests/contracts").split("\n").filter(Boolean);
for (const p of untracked) console.log(`untracked: ${p}`);
for (const p of ignored) console.log(`ignored: ${p}`);
if (untracked.length || ignored.length) process.exit(1);
console.log("check-tracked: every file under tests/contracts is tracked");
