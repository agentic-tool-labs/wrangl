// Scans the plugin's TypeScript and JavaScript for ah v1 paths, written whole or in segments: ah2 keeps nothing directly under
// `~/.claude` or `<repo>/.claude` (its own directories are `.claude/ah2`). The native sources have their own syntax-level scan
// (Ah.Architecture.Tests HomeLayoutScanTests); this one covers mod/, tests/ and scripts/. A v1 spelling is
//   (a) a string or template literal holding `.claude/hierarchy`, `.claude/agent-hierarchy` or `.claude/ah-conventions.json`, or
//   (b) a `.claude` literal whose next literal (this line or the next two) is a v1 name:
//       `join(join(join(root, '.claude'), 'hierarchy'), 'status.json')`.
// A line is exempt when the line before it holds the marker `scan-v1-paths: allow` and a reason: the sites that name a v1 path on
// purpose (the replay of a recorded v1 trace).
// Usage: node scan-v1-paths.mjs <root>   (prints one `file:line: text` per violation; exit 1 if any)
//        node scan-v1-paths.mjs --self-test   (planted negatives; exit 1 if the scan misses one or flags a clean line)
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";

// The scanner's own file holds the planted spellings and so is not scanned.
const SKIP_DIRS = new Set(["node_modules", "native", "artifacts", ".git", "docs"]);
const EXT = /\.(ts|tsx|js|mjs|cjs)$/;
const WHOLE = /\.claude[\\/](hierarchy|agent-hierarchy|ah-conventions\.json)(?![A-Za-z0-9._-])|\.claude[\\/]agent-hierarchy\./;
const V1_NAME = /^(hierarchy|agent-hierarchy|ah-conventions\.json|agent-hierarchy\..+)$/;
const LITERAL = /'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)"|`((?:[^`\\]|\\.)*)`/g;

const literalsOf = (line) => [...line.matchAll(LITERAL)].map((m) => m[1] ?? m[2] ?? m[3] ?? "");

/** The 1-based line numbers of `source` that spell a v1 path. */
export const violations = (source) => {
  const lines = source.split("\n");
  const found = new Set();
  lines.forEach((line, i) => {
    if (i > 0 && lines[i - 1].includes("scan-v1-paths: allow")) return;
    const own = literalsOf(line);
    if (own.some((l) => WHOLE.test(l))) found.add(i + 1);
    own.forEach((lit, k) => {
      if (lit !== ".claude") return;
      const after = [...own.slice(k + 1), ...literalsOf(lines[i + 1] ?? ""), ...literalsOf(lines[i + 2] ?? "")];
      if (after.length > 0 && V1_NAME.test(after[0])) found.add(i + 1);
    });
  });
  return [...found].sort((a, b) => a - b);
};

const walk = (dir, out = []) => {
  for (const name of readdirSync(dir)) {
    if (SKIP_DIRS.has(name) || name === "scan-v1-paths.mjs") continue;
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p, out);
    else if (EXT.test(name)) out.push(p);
  }
  return out;
};

const selfTest = () => {
  const planted = [
    "const p = join(join(join(root, '.claude'), 'hierarchy'), 'status.json')",
    "const p = join(home, \".claude\", \"agent-hierarchy.gate.json\")",
    "const p = `${home}/.claude/hierarchy/hook-errors.jsonl`",
    "const p = root + '/.claude/ah-conventions.json'",
    "join(home,\n  '.claude',\n  'agent-hierarchy')",
    "// scan-v1-paths: allow the replay\nconst a = '/.claude/hierarchy'\nconst b = '/.claude/hierarchy'",
  ];
  const clean = [
    "const p = join(join(join(root, '.claude'), 'ah2'), 'status.json')",
    "const p = join(home, '.claude', 'plugins', 'installed_plugins.json')",
    "const p = `${home}/.claude/ah2/hierarchy/hook-errors.jsonl`",
    "const p = join(home, '.claude', 'agents') // the hierarchy of agents",
    "const s = 'hierarchy'",
    "// scan-v1-paths: allow the replay\nconst a = '/.claude/hierarchy'",
  ];
  let bad = 0;
  // the marker exempts the next line only: of the two spellings above, the second is still found
  if (violations(planted[planted.length - 1]).join() !== "3") { console.log("MARKER: exempts more or less than the next line"); bad++; }
  for (const code of planted.slice(0, -1)) if (violations(code).length === 0) { console.log(`MISSED: ${code.replace(/\n/g, " ")}`); bad++; }
  for (const code of clean) if (violations(code).length !== 0) { console.log(`FALSE POSITIVE: ${code}`); bad++; }
  return bad === 0 ? 0 : 1;
};

if (process.argv[2] === "--self-test") process.exit(selfTest());
const root = process.argv[2];
if (!root) { console.error("usage: scan-v1-paths.mjs <root> | --self-test"); process.exit(2); }
let count = 0;
for (const file of walk(root)) {
  const lines = readFileSync(file, "utf8").split("\n");
  for (const n of violations(lines.join("\n"))) {
    console.log(`${relative(root, file)}:${n}: ${lines[n - 1].trim().slice(0, 160)}`);
    count++;
  }
}
process.exit(count === 0 ? 0 : 1);
