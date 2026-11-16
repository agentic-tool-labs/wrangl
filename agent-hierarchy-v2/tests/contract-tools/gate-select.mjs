#!/usr/bin/env node
// The ship gate's affected-only selection (b29 M7): the native test projects and shell suites that BASE..SHA reaches.
//   gate-select.mjs --repo DIR --base REF --sha REF
// DIR is the plugin root (the directory holding native/ and tests/). Exit 0: prints one `dotnet <csproj>` line per test project and one
// `shell <suite>` line per suite to run (maybe none). A changed file inside project X's directory, or under a path X includes from outside
// it (a Compile, None, Content, EmbeddedResource, AdditionalFiles, Analyzer or Import item), selects every *.Tests project that reaches X through ProjectReference,
// transitively, read from the csproj files at SHA. A changed tests/test-*.sh selects that suite.
// Exit 3: prints `full <why>` and the caller runs the full suite, for: an empty change set; no test project selected; the first changed
// path that cannot be mapped (the solution and build files, build and run scripts, shared test helpers: a non-*.Tests project under
// native/tests, tests/lib-*.sh, ...); a path a Directory.Build file pulls into every project; a path-bearing item whose `$(...)` path
// cannot be resolved. Exit 2: usage, or a ref git cannot read.

import { execFileSync } from "node:child_process";
import path from "node:path";

const args = process.argv.slice(2);
const opt = (n) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : undefined; };
const [REPO, BASE, SHA] = ["--repo", "--base", "--sha"].map(opt);
if (!REPO || !BASE || !SHA) { console.error("usage: gate-select.mjs --repo DIR --base REF --sha REF"); process.exit(2); }
const git = (...a) => execFileSync("git", ["-C", REPO, ...a], { encoding: "utf8", maxBuffer: 1 << 28, stdio: ["ignore", "pipe", "pipe"] });

const full = (why) => { console.log(`full ${why}`); process.exit(3); };
let changed, tree;
try {
  changed = git("diff", "--no-renames", "--name-only", "--relative", BASE, SHA).split("\n").filter(Boolean);
  tree = git("ls-tree", "-r", "--name-only", SHA, "--", "native", "tests").split("\n").filter(Boolean);
} catch (e) { console.error(`gate-select: ${String(e.stderr || e.message).trim()}`); process.exit(2); }
if (!changed.length) full("(empty change set)");

// the path-bearing items of an MSBuild file at SHA as [kind, repo-relative path]; a leading $(AhNativeRoot) (native/, set in
// native/Directory.Build.props) or $(MSBuildThisFileDirectory) is resolved, and any other `$(` cannot be followed, so it asks for full
const ITEM = /<(ProjectReference|Compile|None|Content|EmbeddedResource|AdditionalFiles|Analyzer|Import)\b[^>]*?\s(?:Include|Project)="([^"]+)"/g;
const items = (f) => {
  const dir = path.posix.dirname(f);
  return [...git("show", `${SHA}:./${f}`).matchAll(ITEM)].map((m) => {
    const p = m[2].replace(/\\/g, "/").replace(/^\$\(AhNativeRoot\)/, "/native/").replace(/^\$\(MSBuildThisFileDirectory\)/, `/${dir}/`);
    if (p.includes("$(")) full(`${f}: ${m[1]} ${m[2]} cannot be resolved`);
    return [m[1], path.posix.normalize(p.startsWith("/") ? p.slice(1) : path.posix.join(dir, p))];
  });
};
const prefix = ([kind, p]) => (kind === "ProjectReference" ? `${path.posix.dirname(p)}/` : p.replace(/\*.*$/, ""));

// projects at SHA: dir -> { name, refs (dirs), links (path prefixes it includes from outside its dir) }
const projects = new Map();
for (const f of tree.filter((p) => p.startsWith("native/") && p.endsWith(".csproj"))) {
  const dir = path.posix.dirname(f);
  const it = items(f);
  const refs = it.filter(([k]) => k === "ProjectReference").map(([, p]) => path.posix.dirname(p));
  const links = it.filter(([k, p]) => k !== "ProjectReference" && !p.startsWith(`${dir}/`)).map(prefix);
  projects.set(dir, { name: path.posix.basename(f, ".csproj"), refs, links });
}
// what a Directory.Build.* file pulls into every project (Ah.Generators, the sandbox sources): a change there asks for full
const everywhere = tree.filter((p) => p.startsWith("native/") && /(^|\/)Directory\.Build\.(props|targets)$/.test(p)).flatMap((f) => items(f).map(prefix));
const referrers = new Map();
for (const [dir, p] of projects) for (const r of p.refs) referrers.set(r, [...(referrers.get(r) ?? []), dir]);

const isTests = (dir) => dir.startsWith("native/tests/") && projects.get(dir).name.endsWith(".Tests");
const reached = new Set();
const shell = new Set();
for (const f of changed) {
  const suite = /^tests\/(test-[\w.-]+)\.sh$/.exec(f)?.[1];
  if (suite) { if (tree.includes(f)) shell.add(suite); continue; }
  if (everywhere.some((l) => f === l || f.startsWith(l))) full(`${f} (pulled into every project by a Directory.Build file)`);
  const owners = [...projects].filter(([dir, p]) => f.startsWith(dir + "/") || p.links.some((l) => f === l || f.startsWith(l))).map(([dir]) => dir);
  if (!owners.length || owners.some((d) => d.startsWith("native/tests/") && !isTests(d))) full(f);
  for (const d of owners) reached.add(d);
}
for (const d of reached) for (const r of referrers.get(d) ?? []) reached.add(r); // a Set visits what is added during the loop

const tests = [...reached].filter(isTests).sort();
if (!tests.length) full("(no test project selected)");
for (const d of tests) console.log(`dotnet ${d}/${projects.get(d).name}.csproj`);
for (const s of [...shell].sort()) console.log(`shell ${s}`);
