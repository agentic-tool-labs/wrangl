#!/usr/bin/env node
/**
 * The stage-2 static check of the state hub, driven by the registry (hooks/lib-state.mjs), so a new domain needs
 * no change here. A grep for raw reads misses `const f = join(dir, "teams"); ... readFileSync(f)`, so the check
 * removes the way to name a domain's files outside its library instead. For every domain it scans the given roots
 * (default: ../hooks, ../mod and ../tests: `tests/` is not exempt) and reports, outside the domain's own library
 * and outside the registry file:
 *   - any string or template literal containing one of the domain's `pathFragments` as a path segment;
 *   - any use of a primitive writer (a word-boundary match in code).
 * Calling the library's path helpers or read functions from outside is allowed until reads move behind the library, when the helpers
 * are banned too; raw access to the files is still caught by the fragments.
 * Comments never count; literals are found by tokenising, not by lines. A domain's `allow` entry names a file (matched
 * by path suffix) and the trimmed text of each line it exempts for that domain, with its reason in the registry; a
 * listed line that no longer occurs in the file is itself a violation.
 * Ceiling: the tokeniser is a small JavaScript scanner. It does not parse JSX text, so a `.tsx` file whose text
 * holds an apostrophe can misread what follows it; regex literals are recognised by the token before the slash.
 *
 * Usage: node tests/check-state-static.mjs [--root <dir>]... [--roots] [--excluded]
 *   --roots prints the default roots; --excluded prints the state files that are deliberately not domains, as JSON lines.
 *   Exits 1 and prints the violations as JSON lines when there are any.
 */

import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { DOMAINS, EXCLUDED_STATE } from "../hooks/lib-state.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const REGISTRY = resolve(HERE, "..", "hooks", "lib-state.mjs");
const EXT = /\.(mjs|js|ts|tsx)$/;
const DEFAULT_ROOTS = [resolve(HERE, "..", "hooks"), resolve(HERE, "..", "mod"), resolve(HERE, "..", "tests")];
const REGEX_PREV = /[(,=:[!&|?{};+\-*%<>~^]/;

/**
 * Tokenise `src`: `literals` are the string and template-literal texts (with the offset where each starts) and
 * `code` is the source with comments and literal contents blanked, line breaks kept.
 */
export function scanSource(src) {
  const literals = [];
  const code = src.split("");
  const blank = (from, to) => {
    for (let k = from; k < to && k < code.length; k++) if (code[k] !== "\n") code[k] = " ";
  };
  function scan(i, endOnBrace) {
    let depth = 0;
    let prev = "";
    while (i < src.length) {
      const c = src[i];
      if (c === "/" && src[i + 1] === "/") {
        const s = i;
        while (i < src.length && src[i] !== "\n") i++;
        blank(s, i);
        continue;
      }
      if (c === "/" && src[i + 1] === "*") {
        const e = src.indexOf("*/", i + 2);
        const end = e < 0 ? src.length : e + 2;
        blank(i, end);
        i = end;
        continue;
      }
      if (c === '"' || c === "'") {
        let j = i + 1;
        let text = "";
        while (j < src.length && src[j] !== c && src[j] !== "\n") {
          if (src[j] === "\\") {
            text += src[j + 1] ?? "";
            j += 2;
          } else text += src[j++];
        }
        literals.push({ text, at: i });
        blank(i + 1, j);
        i = j + 1;
        prev = c;
        continue;
      }
      if (c === "`") {
        i = template(i + 1);
        prev = "`";
        continue;
      }
      if (c === "/" && (prev === "" || REGEX_PREV.test(prev))) {
        let j = i + 1;
        let inClass = false;
        while (j < src.length && src[j] !== "\n" && (src[j] !== "/" || inClass)) {
          if (src[j] === "\\") j++;
          else if (src[j] === "[") inClass = true;
          else if (src[j] === "]") inClass = false;
          j++;
        }
        i = j + 1;
        prev = "/";
        continue;
      }
      if (c === "{") depth++;
      if (c === "}") {
        if (endOnBrace && depth === 0) return i + 1;
        depth--;
      }
      if (!/\s/.test(c)) prev = c;
      i++;
    }
    return i;
  }
  function template(i) {
    const start = i;
    let text = "";
    let from = i;
    while (i < src.length && src[i] !== "`") {
      if (src[i] === "\\") {
        text += src[i + 1] ?? "";
        i += 2;
        continue;
      }
      if (src[i] === "$" && src[i + 1] === "{") {
        literals.push({ text, at: from });
        blank(from, i);
        text = "";
        i = scan(i + 2, true);
        from = i;
        continue;
      }
      text += src[i++];
    }
    literals.push({ text, at: from });
    blank(from, i);
    return Math.max(i + 1, start);
  }
  scan(0, false);
  return { literals, code: code.join("") };
}

/** Whether `text` contains `fragment` as a path segment (or run of segments). */
export const hasPathSegment = (text, fragment) => `/${text.replace(/\\/g, "/")}/`.includes(`/${fragment}/`);

function* files(dir) {
  let names = [];
  try {
    names = readdirSync(dir);
  } catch {
    return;
  }
  for (const n of names) {
    if (n === "node_modules" || n.startsWith(".")) continue;
    const p = join(dir, n);
    let st;
    try {
      st = statSync(p);
    } catch {
      continue;
    }
    if (st.isDirectory()) yield* files(p);
    else if (EXT.test(n)) yield p;
  }
}

if (process.argv.includes("--roots")) {
  process.stdout.write(DEFAULT_ROOTS.join("\n") + "\n");
  process.exit(0);
}
if (process.argv.includes("--excluded")) {
  for (const e of EXCLUDED_STATE) process.stdout.write(JSON.stringify(e) + "\n");
  process.exit(0);
}

const roots = [];
for (let i = 2; i < process.argv.length; i++) if (process.argv[i] === "--root") roots.push(resolve(process.argv[++i]));
if (!roots.length) roots.push(...DEFAULT_ROOTS);

const violations = [];
for (const root of roots) {
  for (const file of files(root)) {
    if (file === REGISTRY || file === fileURLToPath(import.meta.url)) continue;
    const src = readFileSync(file, "utf8");
    const srcLines = src.split("\n").map((l) => l.trim());
    const lineAt = (at) => src.slice(0, at).split("\n").length;
    let scanned = null;
    for (const [domain, row] of Object.entries(DOMAINS)) {
      // A domain's library and its `owners` (files that are the routed path itself, such as the daemon) may use its primitives and paths.
      if (file === resolve(row.lib) || (row.owners || []).some((o) => file === resolve(HERE, "..", o) || file.endsWith(`/${o}`))) continue;
      // An allow entry exempts only the lines whose trimmed text it lists, and must still match one: a listed line
      // that is gone from the file is a stale exemption.
      const exempt = new Set();
      for (const a of (row.allow || []).filter((a) => file === a.file || file.endsWith(`/${a.file}`))) {
        for (const text of a.lines || []) {
          exempt.add(text);
          if (!srcLines.includes(text)) violations.push({ domain, file, line: 1, why: `the ${domain} allow entry for ${a.file} lists a line that is no longer in the file: ${JSON.stringify(text)}` });
        }
      }
      const allowed = (line) => exempt.has(srcLines[line - 1]);
      scanned ||= scanSource(src);
      for (const lit of scanned.literals) {
        const line = lineAt(lit.at);
        if (allowed(line)) continue;
        for (const fragment of row.pathFragments) {
          if (hasPathSegment(lit.text, fragment)) violations.push({ domain, file, line, why: `a literal names the ${domain} path segment "${fragment}"; use the library's operations through the helper` });
        }
      }
      scanned.code.split("\n").forEach((text, i) => {
        if (allowed(i + 1)) return;
        for (const name of row.primitives) {
          if (new RegExp(`\\b${name}\\b`).test(text)) violations.push({ domain, file, line: i + 1, why: `uses ${name}, which only the ${domain} library may use` });
        }
      });
    }
  }
}
for (const v of violations) process.stdout.write(JSON.stringify(v) + "\n");
process.exit(violations.length ? 1 : 0);
