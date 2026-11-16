#!/usr/bin/env node
// Selection for tests/affected-smoke.sh: the test classes and shell suites a change reaches, as a pure function of the diff.
//   affected-select.mjs --repo DIR --base REF [--out DIR] [--why]
// DIR is the plugin root (the directory holding native/ and tests/). Prints the selection; with --out writes
//   DIR/runs.tsv   one `<test project>\t<filter or empty>` row per dotnet test to run (an empty filter runs the whole project)
//   DIR/shell.txt  the shell suites to run (select.mjs --native-covers over the changed paths, plus test-contracts* for a docs change)
// Exit 2: unknown base. Exit 3: the selection is too broad for a smoke (nothing is written to runs.tsv).
// Matching is textual: a test class is selected when its text names a changed type as a word, or a changed non-private member
// as `.Name` or `Name(`. A change reached only through an interface or an inherited type is missed unless the test names it.

import { execFileSync, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const args = process.argv.slice(2);
const opt = (n) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : undefined; };
const REPO = opt("--repo");
const BASE = opt("--base");
const OUT = opt("--out");
const WHY = args.includes("--why");
if (!REPO || !BASE) { console.error("usage: affected-select.mjs --repo DIR --base REF [--out DIR] [--why]"); process.exit(2); }

const ALWAYS_IN = ["Ah.Core.Tests", "Ah.Config.Tests", "Ah.Architecture.Tests"];
const MAX_CLASSES = 250; // the user's ruling (raised from 150 so the c3b1593a incident fits); not derived from a measurement
const MAX_WHOLE_PROJECTS = 3;
const WHOLE_SHARE = 0.8;
const HUB_MAX = 15;
const DOC_PREFIXES = ["agents/", "skills/", "commands/", "docs/", "README", "tests/contracts/", "hooks/"];

const git = (...a) => execFileSync("git", ["-C", REPO, ...a], { encoding: "utf8", maxBuffer: 1 << 28, stdio: ["ignore", "pipe", "pipe"] });
const tryGit = (...a) => { try { return git(...a); } catch { return null; } };

if (tryGit("rev-parse", "--verify", "--quiet", `${BASE}^{commit}`) === null) {
  console.error(`unknown base ${BASE}`);
  process.exit(2);
}
const mergeBase = (tryGit("merge-base", BASE, "HEAD") ?? BASE).trim();

// changed files: committed since the base, uncommitted, and untracked; a rename carries its old path
const changed = new Map();
for (const line of git("diff", "--name-status", "-M", "--relative", mergeBase).split("\n").filter(Boolean)) {
  const [st, a, b] = line.split("\t");
  changed.set(b ?? a, { status: st[0], old: st[0] === "R" ? a : (st[0] === "A" ? null : a) });
}
const untracked = git("ls-files", "--others", "--exclude-standard").split("\n").filter(Boolean);
for (const p of untracked) if (!changed.has(p)) changed.set(p, { status: "A", old: null, untracked: true });

const read = (p) => { try { return readFileSync(join(REPO, p), "utf8"); } catch { return ""; } };
const readOld = (p) => (p ? (tryGit("show", `${mergeBase}:./${p}`) ?? "") : "");

const TYPE_LINE = /^\s*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|unsafe|file|new)\s+)*(?:class|struct|interface|enum|record(?:\s+(?:struct|class))?)\s+([A-Za-z_]\w*)/;
const MEMBER_LINE = /^[+-]\s*(?:\[[^\]]*\]\s*)*(?:public|internal|protected)(?:\s+(?:static|virtual|override|abstract|sealed|readonly|const|new|async|extern|unsafe|partial|required|event))*\s+[\w<>[\],.?\s*]+?\s+([A-Za-z_]\w*)\s*(?:\(|\{|=>|=|;)/;
// every type declared in the text: after any leading attributes, and every declaration on a line (split at `;` and `}`)
const typesIn = (text) => text.split("\n")
  .flatMap((l) => l.replace(/^\s*(?:\[[^\]]*\]\s*)+/, "").split(/[;}]\s*/).map((piece) => TYPE_LINE.exec(piece)?.[1]))
  .filter((n) => n && /^[A-Z]/.test(n));

// the types a test file declares at its own top level and not private: the nested helpers of one test class (Rig, Scenario) are
// that class's own and say nothing about other classes
const topTypesIn = (text) => {
  const found = [];
  for (const l of text.split("\n")) {
    const stripped = l.replace(/^\s*(?:\[[^\]]*\]\s*)+/, "");
    const m = TYPE_LINE.exec(stripped);
    if (m && /^[A-Z]/.test(m[1])) found.push({ name: m[1], indent: l.length - l.trimStart().length, priv: /\bprivate\b/.test(stripped.slice(0, m.index + m[0].length)) });
  }
  const top = Math.min(...found.map((f) => f.indent));
  return found.filter((f) => f.indent === top && !f.priv).map((f) => f.name);
};

// changed C# files under native/src and native/tests both feed the type list; a path that contributes no type, class or suite is reported
const csChanged = [...changed].filter(([p]) => /^native\/(src|tests)\/.*\.cs$/.test(p));
const srcChanged = csChanged.filter(([p]) => p.startsWith("native/src/"));
const types = new Set();
const typeless = new Set(csChanged.map(([p]) => p));
for (const [p, c] of csChanged) {
  const declared = p.startsWith("native/tests/") ? topTypesIn : typesIn;
  for (const t of [...declared(read(p)), ...declared(readOld(c.old ?? p))]) { types.add(t); typeless.delete(p); }
  const stem = p.split("/").pop().replace(/\.cs$/, "").split(".")[0];
  if (stem && /^[A-Z]/.test(stem)) { types.add(stem); typeless.delete(p); }
}

// one reverse hop through src: the types that wrap a changed type join the list; a type with more than HUB_MAX referrer files
// is matched directly only, and a wrapper name declared in more than one src project is ambiguous and dropped
const srcFiles = [];
const walkSrc = (dir, project) => {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.name === "bin" || e.name === "obj") continue;
    if (e.isDirectory()) { walkSrc(join(dir, e.name), project ?? e.name); continue; }
    if (!project || !e.name.endsWith(".cs")) continue;
    const text = readFileSync(join(dir, e.name), "utf8");
    srcFiles.push({ project, declared: new Set(typesIn(text)), words: new Set(text.match(/[A-Za-z_]\w*/g) ?? []) });
  }
};
const srcDir = join(REPO, "native", "src");
if (existsSync(srcDir)) walkSrc(srcDir);
const projectsOf = new Map();
for (const f of srcFiles) for (const t of f.declared) projectsOf.set(t, (projectsOf.get(t) ?? new Set()).add(f.project));
const via = new Map();
const hubs = [];
for (const t of [...types]) {
  const refs = srcFiles.filter((f) => !f.declared.has(t) && f.words.has(t));
  if (refs.length > HUB_MAX) { hubs.push(`hub ${t} (${refs.length} referrers): direct matches only`); continue; }
  for (const f of refs) for (const c of f.declared) if (!types.has(c) && projectsOf.get(c).size === 1 && !via.has(c)) via.set(c, t);
}
const direct = new Set(types);
for (const c of via.keys()) types.add(c);

const members = new Set();
const addMember = (line) => { const m = MEMBER_LINE.exec(line); if (m && m[1].length >= 4) members.add(m[1]); };
if (srcChanged.length) {
  for (const l of git("diff", "-U0", "-M", "--relative", mergeBase, "--", "native/src").split("\n")) {
    if (/^(\+\+\+|---)/.test(l)) continue;
    addMember(l);
  }
  for (const [p, c] of srcChanged) if (c.untracked) for (const l of read(p).split("\n")) addMember("+" + l);
}

// test classes: a class is one `<Subject>Tests` type, its partial files together
const testsDir = join(REPO, "native", "tests");
const classes = new Map();
const walk = (dir, rel, project) => {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.name === "bin" || e.name === "obj") continue;
    if (e.isDirectory()) { walk(join(dir, e.name), `${rel}/${e.name}`, project ?? e.name); continue; }
    if (!project || !e.name.endsWith(".cs")) continue;
    const text = readFileSync(join(dir, e.name), "utf8");
    const file = `${rel}/${e.name}`;
    const stem = e.name.replace(/\.cs$/, "").split(".")[0];
    const named = [...new Set(typesIn(text).filter((n) => /Tests$/.test(n)))];
    const attach = (t) => {
      const key = `${project}\u0000${t}`;
      const c = classes.get(key) ?? { project, name: t, files: [], text: "" };
      c.files.push(file);
      c.text += "\n" + text;
      classes.set(key, c);
    };
    if (/\[(Fact|Theory)\b/.test(text)) for (const t of named) attach(t);
    else pending.push({ project, stem, named, attach });
  }
};
const pending = [];
if (existsSync(testsDir)) for (const e of readdirSync(testsDir, { withFileTypes: true })) if (e.isDirectory() && /\.Tests$/.test(e.name)) walk(join(testsDir, e.name), `native/tests/${e.name}`, e.name);
// a file with no [Fact] (a partial's helper half) belongs to the class it declares, or the one its name starts with
for (const f of pending) for (const t of new Set([...f.named, f.stem])) if (classes.has(`${f.project}\u0000${t}`)) f.attach(t);

const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const typeRe = types.size ? new RegExp(`\\b(?:${[...types].map(esc).join("|")})\\b`, "g") : null;
const memberRe = members.size ? new RegExp(`\\.(${[...members].map(esc).join("|")})\\b|\\b(${[...members].map(esc).join("|")})\\(`, "g") : null;

const selected = [];
for (const c of classes.values()) {
  const why = [];
  if (c.files.some((f) => changed.has(f))) why.push("test file changed");
  if (direct.has(c.name.replace(/Tests$/, ""))) why.push(`${c.name}`);
  if (typeRe) {
    const hits = [...new Set([...c.text.matchAll(typeRe)].map((m) => m[0]))];
    if (hits.length) why.push(`names ${hits.slice(0, 3).map((h) => (via.has(h) ? `${h} (wraps ${via.get(h)})` : h)).join(", ")}`);
  }
  if (memberRe) { const hits = [...new Set([...c.text.matchAll(memberRe)].map((m) => m[1] ?? m[2]))]; if (hits.length) why.push(`uses ${hits.slice(0, 3).join(", ")}`); }
  if (why.length) selected.push({ ...c, why });
}
selected.sort((a, b) => (a.project + a.name).localeCompare(b.project + b.name));

const perProject = new Map();
for (const c of classes.values()) perProject.set(c.project, (perProject.get(c.project) ?? 0) + 1);
const outside = selected.filter((c) => !ALWAYS_IN.includes(c.project));
const picked = new Map();
for (const c of outside) picked.set(c.project, (picked.get(c.project) ?? 0) + 1);
const whole = [...picked].filter(([p, n]) => n / perProject.get(p) > WHOLE_SHARE).map(([p]) => p);
const tooBroad = outside.length > MAX_CLASSES || whole.length >= MAX_WHOLE_PROJECTS;

const docs = [...changed.keys()].some((p) => DOC_PREFIXES.some((d) => p.startsWith(d)));

// the shell suites run-all.sh --lane fast --changed BASE selects, by the same selector
let shell = [];
const noCover = [];
let shellRan = false;
const manifest = join(REPO, "tests", "manifest.json");
const selector = join(REPO, "tests", "contract-tools", "select.mjs");
if (existsSync(manifest) && existsSync(selector)) {
  const list = [...changed.keys()].join("\n");
  if (list) {
    const names = JSON.parse(readFileSync(manifest, "utf8")).map((e) => e.name).join("\n") + "\n";
    const namesDir = mkdtempSync(join(tmpdir(), "affected-names."));
    const namesFile = join(namesDir, "names");
    writeFileSync(namesFile, names);
    try {
      const r = spawnSync(process.execPath, [selector, "--manifest", manifest, "--names", namesFile, "--lane", "fast", "--native-covers", "--changed-list", list], { encoding: "utf8" });
      if (r.error || r.status !== 0) {
        console.error(`select.mjs failed (${r.error ? r.error.message : `exit ${r.status}`}); the shell selection is unknown, so nothing is run\n${r.stderr ?? ""}`);
        process.exit(2);
      }
      shell = r.stdout.split("\n").filter(Boolean);
      shellRan = true;
      for (const l of r.stderr.split("\n")) if (l.startsWith("no shell suite covers ")) noCover.push(l);
    } finally {
      rmSync(namesDir, { recursive: true, force: true });
    }
    if (docs) for (const e of JSON.parse(readFileSync(manifest, "utf8"))) if (/^test-contracts(-s\d+)?$/.test(e.name) && e.lane === "fast" && !shell.includes(e.name)) shell.push(e.name);
  }
}

// a native/ path that gave no type, no test class and no shell suite: silence must never read as "nothing to test"
const uncovered = new Set(noCover.map((l) => l.slice("no shell suite covers ".length)));
const notMapped = [...changed.keys()].filter((p) => p.startsWith("native/") && (typeless.has(p) || (!/\.cs$/.test(p) && (!shellRan || uncovered.has(p))) || (/\.cs$/.test(p) && !/^native\/(src|tests)\//.test(p))));

const lines = [`affected-smoke: base ${BASE} (merge-base ${mergeBase.slice(0, 8)})`];
lines.push(`always in, in full: ${ALWAYS_IN.join(" ")}`);
if (WHY) {
  lines.push(`changed types: ${[...types].sort().join(" ") || "none"}`);
  lines.push(`bounds: ${MAX_CLASSES} classes (the user's ruling), ${MAX_WHOLE_PROJECTS} projects over ${WHOLE_SHARE * 100}% selected; one reverse hop, hubs over ${HUB_MAX} referrer files not expanded`);
  for (const t of direct) { const w = [...via].filter(([, o]) => o === t).map(([c]) => c); if (w.length) lines.push(`wrappers of ${t}: ${w.sort().join(" ")}`); }
  lines.push(`changed members: ${[...members].sort().join(" ") || "none"}`);
}
for (const h of hubs) lines.push(h);
lines.push(`selected C# test classes: ${outside.length}${whole.length ? ` (whole projects: ${whole.join(" ")})` : ""}`);
for (const c of selected) lines.push(`  ${c.project}.${c.name}${WHY ? `   <- ${c.why.join("; ")}` : ""}`);
lines.push(`shell suites (by manifest covers; a docs or contract change adds test-contracts*): ${shell.length ? shell.join(" ") : "none"}`);
for (const l of noCover) lines.push(l);
for (const p of notMapped) lines.push(`not mapped: ${p}`);
if (tooBroad) lines.push(`selection too broad (${outside.length} classes); this is QA's full suite, not a smoke`);
console.log(lines.join("\n"));

if (tooBroad) process.exit(3);

if (OUT) {
  mkdirSync(OUT, { recursive: true });
  const rows = ALWAYS_IN.map((p) => `${p}\t`);
  const byProject = new Map();
  for (const c of selected) if (!ALWAYS_IN.includes(c.project)) byProject.set(c.project, [...(byProject.get(c.project) ?? []), c.name]);
  for (const [p, names] of byProject) rows.push(`${p}\t${names.map((n) => `FullyQualifiedName~${p}.${n}`).join("|")}`);
  writeFileSync(join(OUT, "runs.tsv"), rows.join("\n") + "\n");
  writeFileSync(join(OUT, "shell.txt"), shell.length ? shell.join("\n") + "\n" : "");
}
