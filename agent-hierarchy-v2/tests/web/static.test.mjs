import { test } from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { createHash } from "node:crypto";
import { join, relative, extname, dirname, resolve } from "node:path";
import { WEB } from "./lib.mjs";

// The extensions the bridge's static reader serves (UiStatic.ContentTypes).
const SERVED = new Set([".html", ".js", ".css", ".svg", ".woff2"]);
const VENDOR = join(WEB, "vendor");

function walk(dir) {
  return readdirSync(dir).flatMap((name) => {
    const p = join(dir, name);
    return statSync(p).isDirectory() ? walk(p) : [p];
  });
}

const ownFiles = () => walk(WEB).filter((p) => !p.startsWith(`${VENDOR}/`));

const stylesheetsOf = (page) => [...page.matchAll(/<link rel="stylesheet" href="([^"]+)">/g)].map((m) => m[1]);

test("the eight shared stylesheets load statically in token-first order, the presets right after the tokens, the looks last (ah-ui-looks §3)", () => {
  assert.deepEqual(stylesheetsOf(readFileSync(join(WEB, "index.html"), "utf8")), [
    "/ui/ui.css", "/ui/presets.css", "/ui/r6.css", "/ui/r6-plans.css", "/ui/r6-shell.css", "/ui/r6-graph.css", "/ui/r6-nav.css", "/ui/looks.css",
  ]);
  assert.doesNotMatch(readFileSync(join(WEB, "app.js"), "utf8"), /document\.createElement\(["']link["']\)/);
});

test("the Conversation, Plans and questions sheets load with their views, each at its old place in the cascade", () => {
  const app = readFileSync(join(WEB, "app.js"), "utf8");
  assert.match(app, /import\("\.\/conversation\.js"\), \(m\) => m\.ConversationView, \["\/ui\/r6-conversation\.css", "\/ui\/r6-plans\.css"\]\)/);
  // the Plans view also awaits the questions sheet (the Board's waiting cards draw the Inbox's answer form), at the Inbox's place: before r6-shell
  assert.match(app, /const PlansView = lazy\(\(\) => Promise\.all\(\[import\("\.\/plans-view\.js"\), sheet\("\/ui\/r6-questions\.css", "\/ui\/r6-shell\.css"\)\]\)\.then\(\(\[m\]\) => m\), \(m\) => m\.PlansView, \["\/ui\/r6-plans-view\.css", "\/ui\/r6-shell\.css"\]\)/);
  assert.match(app, /import\("\.\/inbox\.js"\), \(m\) => m\.InboxPage, \["\/ui\/r6-questions\.css", "\/ui\/r6-shell\.css"\]\)/);
  assert.doesNotMatch(app, /from "\.\/conversation\.js"/);
});

test("the stylesheet scan excludes module preloads and scripts", () => {
  assert.deepEqual(stylesheetsOf('<link rel="modulepreload" href="/ui/app.js"><script src="/ui/ui.js"></script>'), []);
});

const reducedMotionOf = (css) => css.match(/@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{\s*\*,\s*\*::before,\s*\*::after\s*\{([^}]+)\}/)?.[1];

test("reduced motion disables animation and transitions including pseudo-elements", () => {
  const rules = reducedMotionOf(readFileSync(join(WEB, "ui.css"), "utf8"));
  assert.ok(rules);
  assert.match(rules, /animation:\s*none\s*!important;/);
  assert.match(rules, /transition:\s*none\s*!important;/);
});

test("the reduced-motion guard rejects no-preference and element-only rules", () => {
  assert.equal(reducedMotionOf('@media (prefers-reduced-motion: no-preference) { *, *::before, *::after { animation: none !important; } }'), undefined);
  assert.equal(reducedMotionOf('@media (prefers-reduced-motion: reduce) { * { animation: none !important; } }'), undefined);
});

// ---- W4

export const BANNED = [
  ["innerHTML", /\binnerHTML\b/],
  ["insertAdjacentHTML", /\binsertAdjacentHTML\b/],
  ["eval", /\beval\b/],
  ["dangerouslySetInnerHTML", /\bdangerouslySetInnerHTML\b/],
];

const hits = (source) => BANNED.filter(([, re]) => re.test(source)).map(([name]) => name);

test("W4: no page script uses innerHTML, insertAdjacentHTML, eval or dangerouslySetInnerHTML", () => {
  const scripts = ownFiles().filter((p) => extname(p) === ".js");
  assert.ok(scripts.length >= 8, `the scan saw only ${scripts.length} scripts`);
  assert.ok(scripts.some((p) => p.endsWith("app.js")));
  for (const p of scripts) {
    assert.deepEqual(hits(readFileSync(p, "utf8")), [], relative(WEB, p));
  }
});

test("W4: the scan flags each banned API and passes the nearest harmless text", () => {
  assert.deepEqual(hits("el.innerHTML = x"), ["innerHTML"]);
  assert.deepEqual(hits("el.insertAdjacentHTML('beforeend', x)"), ["insertAdjacentHTML"]);
  assert.deepEqual(hits("eval('1')"), ["eval"]);
  assert.deepEqual(hits("h('div', { dangerouslySetInnerHTML: { __html: x } })"), ["dangerouslySetInnerHTML"]);
  assert.deepEqual(hits("el.textContent = x; const retrieval = evaluate(y);"), []);
});

test("W4: the vendored libraries are exactly the pinned files, so exempting them cannot hide an edit", () => {
  const sums = readFileSync(join(VENDOR, "SHA256SUMS"), "utf8").trim().split("\n");
  assert.ok(sums.length >= 4);
  for (const line of sums) {
    const [sum, file] = line.split(/\s+/);
    assert.equal(createHash("sha256").update(readFileSync(join(VENDOR, file))).digest("hex"), sum, file);
  }
  const listed = new Set(sums.map((l) => l.split(/\s+/)[1]));
  for (const p of walk(VENDOR)) {
    const name = relative(VENDOR, p);
    assert.ok(name === "SHA256SUMS" || name === "VENDOR.md" || listed.has(name), `${name} is not in SHA256SUMS`);
  }
});

// ---- the stylesheet carries no rule for a class no script or page uses

test("every class a rule of ui.css names as its whole selector list is used by a script or the page", () => {
  const css = readFileSync(join(WEB, "ui.css"), "utf8");
  const used = ownFiles().filter((p) => [".js", ".html"].includes(extname(p))).map((p) => readFileSync(p, "utf8")).join("\n");
  const unused = [...css.matchAll(/(^|\})\s*((?:\.[\w-]+)(?:\s*,\s*\.[\w-]+)*)\s*\{/g)].flatMap((m) => m[2].split(",").map((s) => s.trim().slice(1))).filter((c) => !used.includes(c));
  assert.deepEqual(unused, []);
});

test("the unused-class scan finds a class only the stylesheet names", () => {
  const css = ".gone { color: red; }\n.kept { color: blue; }";
  const found = [...css.matchAll(/(^|\})\s*((?:\.[\w-]+)(?:\s*,\s*\.[\w-]+)*)\s*\{/g)].map((m) => m[2]);
  assert.deepEqual(found, [".gone", ".kept"]);
});

// ---- what the bridge can serve and its policy allows

test("every file the page can load has an extension the bridge serves", () => {
  for (const p of walk(WEB)) {
    const ext = extname(p);
    const referenced = ![".txt", ".md"].includes(ext) && !p.endsWith("SHA256SUMS");
    if (referenced) {
      assert.ok(SERVED.has(ext), `${relative(WEB, p)} would be refused by the static reader`);
    }
  }
});

const specifiersOf = (source) => [...source.matchAll(/^\s*(?:import|export)\b[^;]*?\bfrom\s*["']([^"']+)["']/gm), ...source.matchAll(/\bimport\(\s*["']([^"']+)["']\s*\)/g)].map((m) => m[1]);

test("the import scan finds static and dynamic specifiers, and not prose that says `from`", () => {
  assert.deepEqual(specifiersOf('import { a } from "preact";\nexport { b } from "./b.js";\nconst x = await import("./c.js");'), ["preact", "./b.js", "./c.js"]);
  assert.deepEqual(specifiersOf('const s = d ? "from" : "to";'), []);
});

test("every import is a relative path to a file that exists, so the page needs no import map or bundler", () => {
  for (const p of walk(WEB).filter((f) => extname(f) === ".js")) {
    const source = readFileSync(p, "utf8");
    for (const spec of specifiersOf(source)) {
      assert.match(spec, /^\.\.?\//, `${relative(WEB, p)} imports the bare specifier ${spec}`);
      assert.ok(statSync(resolve(dirname(p), spec), { throwIfNoEntry: false })?.isFile(), `${relative(WEB, p)}: ${spec} does not exist`);
    }
  }
});

test("the page has no inline script, inline style or handler attribute (the bridge's CSP forbids them)", () => {
  const page = readFileSync(join(WEB, "index.html"), "utf8");
  assert.doesNotMatch(page, /<script(?![^>]*\ssrc=)/);
  assert.doesNotMatch(page, /\sstyle=/);
  assert.doesNotMatch(page, /\son\w+=/);
  assert.match(page, /<script type="module" src="\/ui\/ui\.js">/);
});

test("the script never writes a style attribute or a string handler (CSP, and no string-built handlers)", () => {
  for (const p of ownFiles().filter((f) => extname(f) === ".js")) {
    const source = readFileSync(p, "utf8");
    assert.doesNotMatch(source, /setAttribute\(\s*["']style["']/, relative(WEB, p));
    assert.doesNotMatch(source, /\son(?:click|error|load)=\$\{["'`]/, relative(WEB, p));
  }
});
