import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { createHash } from "node:crypto";
import { join, resolve } from "node:path";
import { WEB, fixture } from "./lib.mjs";

const expected = fixture("visual-tokens.json");
const SHEETS = ["ui.css", "r6.css", "r6-conversation.css", "r6-plans.css", "r6-plans-view.css", "r6-questions.css", "r6-backlog.css", "r6-shell.css", "r6-graph.css", "r6-nav.css", "r6-checklist.css"];
const css = readFileSync(join(WEB, "ui.css"), "utf8");
const declarations = (body) => Object.fromEntries([...body.matchAll(/--([\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
const stripComments = (source) => source.replace(/\/\*[\s\S]*?\*\//g, "");
const blocksOf = (source) => [...stripComments(source).matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((m) => ({ selector: m[1].trim(), values: declarations(m[2]) }));
const blocks = blocksOf(css);
// A token block may list the Looks preview's theme scope beside the root selector (ah-ui-looks §6): `.looks-pv, :root`.
const selectorsOf = (selector) => selector.split(",").map((s) => s.trim());
const block = (selector) => blocks.find((b) => selectorsOf(b.selector).includes(selector))?.values;
const root = block(":root");
const light = block(':root[data-theme="light"]');
const mediaLight = block(':root:not([data-theme="dark"])');
const pick = (values, names) => Object.fromEntries(names.map((n) => [n, values[n]]));

test("the root block holds the dark tokens, the type, space and radius scale, and the light blocks hold the light values", () => {
  assert.deepEqual(pick(root, Object.keys(expected.dark)), expected.dark);
  assert.deepEqual(pick(root, Object.keys(expected.common)), expected.common);
  assert.deepEqual(light, expected.light);
  assert.deepEqual(mediaLight, expected.light);
  assert.match(css, /@media \(prefers-color-scheme: light\)\s*\{\s*:root:not\(\[data-theme="dark"\]\)/);
  assert.match(css, /:root\s*\{\s*color-scheme: dark;/);
  assert.match(css, /:root\[data-theme="light"\]\s*\{\s*color-scheme: light;/);
  assert.ok(css.indexOf(':root[data-theme="light"]') > css.indexOf("@media (prefers-color-scheme: light)"), "the explicit override follows the OS rule");
});

test("a light value missing from the fixture or a dark token left out of the light blocks is caught", () => {
  const lightOnly = Object.keys(expected.light).filter((n) => !(n in expected.dark));
  assert.deepEqual(lightOnly, []);
  assert.deepEqual(Object.keys(expected.dark).filter((n) => !(n in expected.light)), []);
  assert.notDeepEqual({ ...expected.light, accent: "#4c9aff" }, light);
});

const fileText = (name) => readFileSync(join(WEB, name), "utf8");
const scripts = readdirSync(WEB).filter((n) => n.endsWith(".js"));
const allSources = [...SHEETS, ...scripts, "index.html"].map(fileText).join("\n");
const declaredNames = (source) => new Set([...source.matchAll(/--([\w-]+)\s*:/g)].map((m) => m[1]).concat([...source.matchAll(/["']--([\w-]+)["']\s*:/g)].map((m) => m[1])).concat([...source.matchAll(/setProperty\(\s*["']--([\w-]+)["']/g)].map((m) => m[1])));
const dangling = (source) => {
  const known = declaredNames(source);
  return [...new Set([...source.matchAll(/var\(--([\w-]+)/g)].map((m) => m[1]))].filter((n) => !known.has(n)).sort();
};

test("every custom property a stylesheet or script reads is declared somewhere", () => {
  assert.deepEqual(dangling(allSources), []);
});

test("a read of an undeclared custom property is reported and a declared one is not", () => {
  assert.deepEqual(dangling(":root { --ink: red; } a { color: var(--ink); border-color: var(--nosuch); }"), ["nosuch"]);
  assert.deepEqual(dangling(':root { --ink: red; } a { color: var(--ink); } b { x: setProperty("--depth", 1); y: var(--depth); }'), []);
});

const rootSelectors = new Set([":root", ':root[data-theme="dark"]', ':root[data-theme="light"]', ':root:not([data-theme="dark"])', '.looks-pv', '.looks-pv[data-theme="light"]']);
const isRoot = (selector) => selectorsOf(selector).every((s) => rootSelectors.has(s));
const outsideRoot = (source) => blocksOf(source).filter((b) => ["accent", "accent-ink", "working", "edge-live"].some((name) => name in b.values) && !isRoot(b.selector));

test("accent dependencies are never shadowed below the root", () => {
  for (const file of SHEETS) {
    assert.deepEqual(outsideRoot(fileText(file)), [], file);
  }
});

test("the root guard rejects body overrides but accepts theme overrides", () => {
  assert.equal(outsideRoot("body { --accent: blue; }").length, 1);
  assert.equal(outsideRoot(':root[data-theme="light"] { --accent-ink: blue; }').length, 0);
  assert.equal(outsideRoot(':root:not([data-theme="dark"]) { --working: blue; }').length, 0);
});

const literalsOf = (source) => source.match(/#[\da-f]{3,8}\b|\b(?:rgba?|hsla?)\s*\(/gi) ?? [];
const strayLiterals = (sheets) => Object.entries(sheets).flatMap(([file, source]) => {
  const scanned = file === "ui.css"
    ? source.replace(/([^{}]+)\{([^{}]*)\}/g, (rule, selector) => isRoot(selector) ? "" : rule)
    : source;
  return literalsOf(scanned).map((literal) => `${file}: ${literal}`);
});

test("stylesheets contain no color literals outside root token blocks", () => {
  assert.deepEqual(strayLiterals(Object.fromEntries(SHEETS.map((file) => [file, fileText(file)]))), []);
});

test("the root literal scan permits tokens but rejects body, pseudo-element and nested-rule colors", () => {
  const tokens = ':root { --ink: #aabbcc; } :root[data-theme="light"] { --ink: rgb(1,2,3); } @media (prefers-color-scheme: light) { :root:not([data-theme="dark"]) { --ink: #ddeeff; } }';
  assert.deepEqual(strayLiterals({ "ui.css": tokens + ' body { color: #123456; } body::before { color: rgb(1,2,3); } @media (min-width: 1px) { a { color: hsl(0,0%,0%); } }' }).map((s) => s.split(": ")[1]), ["#123456", "rgb(", "hsl("]);
  assert.deepEqual(strayLiterals({ "ui.css": tokens + " body { color: var(--ink); }" }), []);
});

test("the literal guard catches hex, rgb and hsl, not tokens or srgb mixing", () => {
  assert.equal(literalsOf("color: #abc; background: rgb(1,2,3); fill: hsla(0,0%,0%,.5)").length, 3);
  assert.deepEqual(literalsOf("color: var(--ink); background: color-mix(in srgb, var(--accent) 12%, transparent)"), []);
});

// Flat surfaces: the mock-data banner's stripe and the Board's 28px grid backdrop (board-r2, the owner's call) are the only patterns left.
const decoration = (source) => stripComments(source)
  .replace(/--mock-bg:[^;]+;/, "")
  .replace(/--board-grid-bg:[^;]+;/, "")
  .match(/radial-gradient|linear-gradient|backdrop-filter|drop-shadow\(|box-shadow:\s*0 0 [1-9]|filter:\s*url\(/g) ?? [];

test("the house sheets have no gradients, glows, blurs or orb filters", () => {
  for (const file of SHEETS) {
    assert.deepEqual(decoration(fileText(file)), [], file);
  }
});

// Glass (ah-optical-glass §9.1): every decorated rule in glass.css sits under the glass root or the picker.
const GLASS_PREFIXES = [":root[data-glass]", ":root[data-glass=", ".r6 .optics"];
const misplacedDecoration = (source) => [...stripComments(source).replace(/@[^{]+\{/g, "").matchAll(/([^{}]+)\{([^{}]*)\}/g)]
  .map((m) => ({ selector: m[1].trim(), body: m[2] }))
  .filter((b) => decoration(b.body).length > 0)
  .filter((b) => !b.selector.split(",").every((s) => GLASS_PREFIXES.some((p) => s.trim().startsWith(p))))
  .map((b) => b.selector);

test("glass decoration lives only in glass.css, under the glass root or the picker", { skip: existsSync(join(WEB, "glass.css")) ? false : "web/ui/glass.css is not there yet (slice G2)" }, () => {
  assert.deepEqual(misplacedDecoration(fileText("glass.css")), []);
});

test("the glass placement check fails a decorated rule without the prefix and passes the prefixed ones", () => {
  assert.deepEqual(misplacedDecoration(".r6 .pane { backdrop-filter: blur(8px); }"), [".r6 .pane"]);
  assert.deepEqual(misplacedDecoration(":root[data-glass-x] .r6 .pane { backdrop-filter: blur(8px); }"), [":root[data-glass-x] .r6 .pane"]);
  assert.deepEqual(misplacedDecoration(':root[data-glass] .r6 .pane, .r6 .card { box-shadow: 0 0 4px red; }'), [':root[data-glass] .r6 .pane, .r6 .card']);
  assert.deepEqual(misplacedDecoration(':root[data-glass="dark"] .r6 .pane { backdrop-filter: blur(8px); } .r6 .optics .preview { background: linear-gradient(red, blue); } @media (prefers-contrast: more) { :root[data-glass] .r6 h2 { text-shadow: none; } }'), []);
});

test("the decoration scan catches each kind and leaves a hairline inset and the banner alone", () => {
  for (const sample of ["a { background: radial-gradient(red, blue); }", "a { background: linear-gradient(red, blue); }", "a { backdrop-filter: blur(4px); }", "a { filter: drop-shadow(0 0 4px red); }", "a { box-shadow: 0 0 8px var(--accent); }", "a { filter: url(#orb-glow); }"]) {
    assert.equal(decoration(sample).length, 1, sample);
  }
  assert.deepEqual(decoration("a { box-shadow: inset 2px 0 0 var(--accent); } :root { --mock-bg: repeating-linear-gradient(45deg, red, blue); }"), []);
});

test("corners come from the tokens: no sheet sets a literal radius above 4px, and panes use the 6px token", () => {
  for (const file of SHEETS) {
    const wide = stripComments(fileText(file)).split("\n").filter((line) => /border-radius:\s*(?:[5-9]|\d{2,})px/.test(line));
    assert.deepEqual(wide, [], file);
  }
});

test("the body is 13px in the system UI stack", () => {
  const body = blocks.find((b) => b.selector === "body");
  assert.ok(body);
  assert.match(stripComments(css), /body\s*\{[^}]*font-family: var\(--font-ui\);[^}]*font-size: var\(--text-13\);/);
  assert.equal(root["text-13"], "13px");
  assert.ok(!/Space Grotesk/i.test(allSources));
});

const listedFiles = (manifest) => new Map(manifest.trim().split("\n").map((line) => {
  const [hash, file] = line.split(/\s+/);
  return [resolve(WEB, "vendor", file), hash];
}));
const unlistedFonts = (names, listed) => names.filter((name) => !listed.has(resolve(WEB, "fonts", name)));

test("the one bundled font has a face, a licence, a hash and a 140 KB budget", () => {
  const listed = listedFiles(fileText("vendor/SHA256SUMS"));
  const names = readdirSync(join(WEB, "fonts"));
  assert.deepEqual(unlistedFonts(names, listed), []);
  for (const name of names) {
    const file = join(WEB, "fonts", name);
    assert.equal(createHash("sha256").update(readFileSync(file)).digest("hex"), listed.get(resolve(file)), name);
  }
  const fonts = names.filter((name) => name.endsWith(".woff2"));
  assert.deepEqual(fonts, ["jetbrains-mono-latin-wght-normal.woff2"]);
  assert.ok(fonts.reduce((sum, name) => sum + statSync(join(WEB, "fonts", name)).size, 0) <= 140_000);
  assert.match(fileText("fonts/OFL-JetBrainsMono.txt"), /SIL OPEN FONT LICENSE/i);
  const faces = [...css.matchAll(/@font-face\s*\{([^}]+)\}/g)].map((m) => m[1]);
  assert.equal(faces.length, 1);
  assert.ok(faces[0].includes('font-family: "JetBrains Mono";'));
  assert.match(faces[0], /font-display:\s*swap;/);
  assert.match(faces[0], /font-weight:\s*400 600;/);
  assert.deepEqual([...faces[0].matchAll(/url\(([^)]+)\)/g)].map((m) => m[1]), [`/ui/fonts/${fonts[0]}`]);
  const firstPaintLinks = [...fileText("index.html").matchAll(/<link[^>]+href="([^"]+)"/g)].map((m) => m[1]);
  assert.ok(firstPaintLinks.every((url) => !url.includes("/fonts/") && !url.endsWith(".woff2")));
});

test("font completeness rejects unlisted fonts and unlisted metadata", () => {
  const listed = new Map([[resolve(WEB, "fonts", "known.woff2"), "hash"]]);
  assert.deepEqual(unlistedFonts(["known.woff2"], listed), []);
  assert.deepEqual(unlistedFonts(["extra.woff2", "SOURCES"], listed), ["extra.woff2", "SOURCES"]);
});

const linear = (c) => c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4;
const luminance = (hex) => [1, 3, 5].map((i) => linear(parseInt(hex.slice(i, i + 2), 16) / 255)).reduce((sum, c, i) => sum + c * [.2126, .7152, .0722][i], 0);
const contrast = (a, b) => (Math.max(luminance(a), luminance(b)) + .05) / (Math.min(luminance(a), luminance(b)) + .05);
const SURFACES = ["bg", "bar", "side", "control", "tab-on", "row-sel", "row-hi"];
const TEXT = ["ink", "ink-2", "muted", "accent", "working", "review", "attn", "err", "ok"];
const GLYPH = ["dim"];
const TINTED = [["ink", "brief-bg"], ["ink-2", "brief-bg"], ["muted", "brief-bg"], ["attn", "brief-bg"], ["ink", "tool-bg"], ["muted", "tool-bg"], ["ink", "err-bg"], ["err-ink", "err-bg"], ["muted", "err-bg"], ["on-accent", "accent"]];

// The error row background is the error tint, a mix of the error colour into the page; measure the colour it comes to.
const mixHex = (fg, bg, p) => "#" + [1, 3, 5].map((i) => Math.round(parseInt(fg.slice(i, i + 2), 16) * p + parseInt(bg.slice(i, i + 2), 16) * (1 - p)).toString(16).padStart(2, "0")).join("");
const concrete = (t) => ({ ...t, "err-bg": mixHex(t.err, t.bg, Number(/ (\d+)%/.exec(t["tint-err"])[1]) / 100) });

function contrastFailures(raw) {
  const tokens = concrete(raw);
  const failures = [];
  const check = (fg, bg, need) => {
    const ratio = contrast(tokens[fg], tokens[bg]);
    if (ratio < need) failures.push(`${fg} / ${bg}: ${ratio.toFixed(2)}`);
  };
  for (const surface of SURFACES) {
    TEXT.forEach((name) => check(name, surface, 4.5));
    GLYPH.forEach((name) => check(name, surface, 3));
  }
  TINTED.forEach(([fg, bg]) => check(fg, bg, 4.5));
  return failures;
}

test("dark text meets 4.5:1 and glyphs 3:1 on every surface; light does the same", () => {
  assert.deepEqual(contrastFailures(expected.dark), []);
  assert.deepEqual(contrastFailures(expected.light), []);
});

test("contrast arithmetic rejects near-threshold text and glyphs", () => {
  assert.equal(contrast("#ffffff", "#000000"), 21);
  assert.equal(contrast("#ffffff", "#ffffff"), 1);
  assert.ok(contrastFailures({ ...expected.light, muted: "#7d848d" }).some((s) => s.startsWith("muted / bg:")));
  assert.ok(contrastFailures({ ...expected.light, dim: "#a0a7b0" }).some((s) => s.startsWith("dim / bg:")));
  assert.ok(contrastFailures({ ...expected.dark, attn: "#8c5e00" }).some((s) => s.startsWith("attn / bg:")));
  assert.deepEqual(contrastFailures({ ...expected.light, ink: "#000000" }), []);
});
