// The ah2 mod uses no name of its own modules that it did not import or declare (b235): main.js read `ah` after b45abf0
// cut it from main.js's import, and the Wrangl pane threw on every refresh in 2.0.39 to 2.0.41. A regex pass over the
// source, not a parser, and it never loads the mod. Known limit: a name no mod module exports is not checked.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const dir = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "herdr-desktop", "mods", "ah2");

// Comments go; string, template and regex literal text becomes blanks (a template's ${...} stays code). Line breaks stay.
const REGEX_AFTER = /(^|[(,=:[!&|?{};+\-*%<>~^]|\b(?:return|typeof|case|in|of|void|delete|new|throw|yield|await))\s*$/;
function strip(src) {
  let out = "";
  const braces = []; // per open `{`: true when it opened a template's ${
  let i = 0;
  const blank = (s) => s.replace(/[^\n]/g, " ");
  const ends = (j) => { if (j >= src.length) throw new Error(`unterminated literal or comment at offset ${i}`); };
  const template = () => { // at a backtick or a `}` closing ${ (the caller wrote it): copy text as blanks until ` or ${
    let j = i + 1;
    while (src[j] !== "`" && !(src[j] === "$" && src[j + 1] === "{")) { ends(j); j += src[j] === "\\" ? 2 : 1; }
    out += blank(src.slice(i + 1, j));
    if (src[j] === "`") { out += "`"; i = j + 1; } else { out += "${"; braces.push(true); i = j + 2; }
  };
  while (i < src.length) {
    const c = src[i], d = src[i + 1];
    if (c === "/" && d === "/") { const j = src.indexOf("\n", i); i = j < 0 ? src.length : j; }
    else if (c === "/" && d === "*") { const j = src.indexOf("*/", i + 2); ends(j < 0 ? src.length : j); out += blank(src.slice(i, j + 2)); i = j + 2; }
    else if (c === '"' || c === "'") {
      let j = i + 1;
      while (src[j] !== c) { ends(j); j += src[j] === "\\" ? 2 : 1; }
      out += c + blank(src.slice(i + 1, j)) + c; i = j + 1;
    }
    else if (c === "`") { out += "`"; template(); }
    else if (c === "/" && REGEX_AFTER.test(out)) {
      let j = i + 1, cls = false;
      while (src[j] !== "/" || cls) { ends(j); if (src[j] === "[") cls = true; else if (src[j] === "]") cls = false; j += src[j] === "\\" ? 2 : 1; }
      out += "/" + blank(src.slice(i + 1, j)) + "/"; i = j + 1;
    }
    else if (c === "{") { braces.push(false); out += c; i++; }
    else if (c === "}") { out += c; if (braces.pop()) template(); else i++; }
    else { out += c; i++; }
  }
  return out;
}

// The text between the bracket at `at` and its match, or null when s holds no match.
const inner = (s, at) => {
  const open = s[at], close = { "(": ")", "{": "}", "[": "]" }[open];
  let depth = 0;
  for (let j = at; j < s.length; j++) {
    if (s[j] === open) depth++;
    else if (s[j] === close && --depth === 0) return s.slice(at + 1, j);
  }
  return null;
};

// Split at top-level commas.
const parts = (s) => {
  const out = [];
  let depth = 0, from = 0;
  for (let j = 0; j < s.length; j++) {
    if ("([{".includes(s[j])) depth++;
    else if (")]}".includes(s[j])) depth--;
    else if (s[j] === "," && depth === 0) { out.push(s.slice(from, j)); from = j + 1; }
  }
  return [...out, s.slice(from)];
};

// The index of the first top-level `ch` in s, or -1 (`=` here never matches `=>`, `==`, `<=`, `>=`, `!=`).
const topLevel = (s, ch) => {
  let depth = 0;
  for (let j = 0; j < s.length; j++) {
    if ("([{".includes(s[j])) depth++;
    else if (")]}".includes(s[j])) depth--;
    else if (s[j] === ch && depth === 0 && (ch !== "=" || (!"=<>!".includes(s[j - 1]) && !"=>".includes(s[j + 1])))) return j;
  }
  return -1;
};

// The names a parameter list or destructuring pattern binds: `a`, `b: c` binds c, `d = 1` binds d, `...e`, nested.
function bindings(pattern) {
  const names = [];
  for (let p of parts(pattern)) {
    p = p.trim().replace(/^\.\.\./, "");
    const eq = topLevel(p, "=");
    if (eq >= 0) p = p.slice(0, eq).trim();
    const colon = topLevel(p, ":");
    if (colon >= 0) p = p.slice(colon + 1).trim();
    if (p[0] === "{" || p[0] === "[") names.push(...bindings(inner(p, 0) ?? ""));
    else if (/^[\w$]+$/.test(p)) names.push(p);
  }
  return names;
}

const KEYWORDS = new Set(["if", "for", "while", "switch", "catch", "function", "return", "with"]);

// What a stripped file imports, declares and exports, and the code left once import and export lists are cut.
function scan(code) {
  const imported = new Set(), declared = new Set(), exported = new Set();
  for (const m of code.matchAll(/\bimport\s+([^;]*?)\s*\bfrom\s*["']/g)) {
    const spec = m[1];
    for (const n of spec.match(/^[\w$]+/) ?? []) imported.add(n);
    for (const n of spec.match(/\*\s*as\s+([\w$]+)/)?.slice(1) ?? []) imported.add(n);
    const list = spec.match(/\{([^}]*)\}/)?.[1] ?? "";
    for (const p of list.split(",")) { const n = p.trim().split(/\s+as\s+/).pop(); if (n) imported.add(n); }
  }
  for (const m of code.matchAll(/\bexport\s*\{([^}]*)\}/g))
    for (const p of m[1].split(",")) { const n = p.trim().split(/\s+as\s+/).pop(); if (n) exported.add(n); }
  for (const m of code.matchAll(/\bexport\s+(?:async\s+)?(?:function\s*\*?|const|let|var|class)\s+([\w$]+)/g)) exported.add(m[1]);
  // Cut the import and export lists: their names are not uses.
  const rest = code.replace(/\bimport\s+[^;]*?\s*\bfrom\s*["'][^"']*["']/g, (s) => s.replace(/[^\n]/g, " "))
    .replace(/\bexport\s*\{[^}]*\}(\s*from\s*["'][^"']*["'])?/g, (s) => s.replace(/[^\n]/g, " "));
  for (const m of rest.matchAll(/\b(?:const|let|var|function\s*\*?|class)\s+([\w$]+)/g)) declared.add(m[1]);
  for (const m of rest.matchAll(/\b(?:const|let|var)\s*(?=[{[])/g)) for (const n of bindings(inner(rest, m.index + m[0].length) ?? "")) declared.add(n);
  for (const m of rest.matchAll(/([\w$]+)\s*=>/g)) declared.add(m[1]);
  for (const m of rest.matchAll(/\(/g)) {
    const before = rest.slice(Math.max(0, m.index - 300), m.index), args = inner(rest, m.index);
    if (args === null) continue;
    const after = rest.slice(m.index + args.length + 2, m.index + args.length + 12);
    const word = before.match(/([\w$]+)\s*$/)?.[1];
    const isParams = /^\s*=>/.test(after) || /\bfunction\s*\*?\s*[\w$]*\s*$/.test(before) || word === "catch" ||
      (word && !KEYWORDS.has(word) && /^\s*\{/.test(after)); // a method: name(params) {
    if (isParams) for (const n of bindings(args)) declared.add(n);
  }
  return { imported, declared, exported, rest };
}

// Each name of `names` the code reads as a bare identifier: not after `.` (a spread `...` is a use), not an object key.
function bareUses(rest, names) {
  const used = new Set();
  for (const m of rest.matchAll(/[\w$]+/g)) {
    const n = m[0];
    if (!names.has(n) || used.has(n)) continue;
    const before = rest.slice(Math.max(0, m.index - 300), m.index), after = rest.slice(m.index + n.length, m.index + n.length + 3000);
    if (/(?<!\.\.)\.\s*$/.test(before)) continue;
    // `name:` is a key; `a ? name : b` and `case name:` are uses
    if (/^\s*:/.test(after) && !/(?<!\?)\?(?![.?])[^?:;{,]*$/.test(before) && !/\bcase\s+$/.test(before)) continue;
    const call = after.match(/^\s*\(/), args = call && inner(after, call[0].length - 1);
    if (args !== null && /^\s*\{/.test(after.slice(call[0].length + args.length + 1))) continue; // a method: name(…) {
    used.add(n);
  }
  return used;
}

function undeclared(files) {
  const scans = new Map([...files].map(([f, src]) => [f, scan(strip(src))]));
  const ours = new Set([...scans.values()].flatMap((s) => [...s.exported]));
  const found = [];
  for (const [f, s] of scans)
    for (const n of bareUses(s.rest, ours)) if (!s.imported.has(n) && !s.declared.has(n)) found.push(`${f}: ${n}`);
  return found;
}

test("the rule: a bare use of another module's export with no import fails; comments, strings, keys, members, bindings do not", () => {
  const lib = "export const ah = () => 1;\nexport function next() {}\nexport const KINDS = [];\nconst x = 1;\nexport { x as block };";
  const ok = [
    'import { ah } from "./ah.js";\nah();', // imported
    'import { ah as ah } from "./ah.js";\nconst r = { runner: ah };',
    "// ah() in a comment\n/* next() */", // comments
    'const s = "ah next"; const t = \'ah\'; const u = `ah ${1 + 2} next`;', // string and template text
    "const o = { ah: 1, next: 2 }; o.ah(); o?.next; o\n  .block;", // keys and members
    "const { ah, KINDS: k } = o; let [next] = []; function f(block) {} try {} catch (KINDS) {}", // bindings
    "const g = (ah, { next = 2 }) => ah; const h = block => block;", // parameters
    "const m = { next(x) { return x; } };", // a method named like an export
    "const re = /ah\\/next['\"`]/g;", // regex literal text
    "const v = c ? { ah: 1 } : 0;", // a key inside a ternary branch
    "const { a = `x${1}y`, ah } = o; ah();", // a binding after a template default
  ];
  for (const src of ok) assert.deepEqual(undeclared(new Map([["lib.js", lib], ["t.js", src]])), [], src);
  const bad = [
    ["const r = { runner: ah };", "t.js: ah"], // the b235 shape
    ['import { KINDS } from "./x.js";\nnext(KINDS);', "t.js: next"],
    ["const s = `${block}`;", "t.js: block"], // template code is code
    ["const a = [...KINDS];", "t.js: KINDS"], // a spread is a use
    ["const v = c ? ah : 0;", "t.js: ah"], // a ternary is not a key
    ["const { a = ah } = o;", "t.js: ah"], // a default value is not a binding
    ["switch (x) { case ah: break; }", "t.js: ah"], // a case label is not a key
    [`ah(\n  { a: [${"1, ".repeat(2000)}] },\n);`, "t.js: ah"], // a call too long to judge is a use, not a method
  ];
  for (const [src, want] of bad) assert.deepEqual(undeclared(new Map([["lib.js", lib], ["t.js", src]])), [want], src);
  for (const src of ['const s = "ah', "/* ah", "const r = /ah"]) assert.throws(() => strip(src), /unterminated/, src);
});

test("every ah2 mod file imports or declares each name of the mod's own modules that it uses", () => {
  const files = new Map(readdirSync(dir).filter((f) => f.endsWith(".js")).map((f) => [f, readFileSync(join(dir, f), "utf8")]));
  assert.deepEqual(undeclared(files), []);
});
