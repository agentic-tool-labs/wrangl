import { existsSync, readFileSync, readdirSync } from "node:fs";
import { test } from "node:test";
import assert from "node:assert/strict";
import { BUILTINS, GRAPH_VARS, ROLES, ROLE_TOKENS, cleanType, HOUSE, KEY, apply, backdropDefaults, bootLooks, choose, contrastOf, copyOf, exportLook, importLooks, lookFor, lookVars, nameError, profileOf, panes, readOptics, withoutLook } from "../../web/ui/looks.js";

// The looks registry, storage and apply (ah-ui-looks §2, §3, §5, §7) against a minimal document.

function fakeDoc(theme = null, prefersLight = false) {
  const el = (cls = "") => {
    const attrs = new Map();
    const props = new Map();
    const node = {
      className: cls, children: [], parent: null,
      classList: { contains: (c) => node.className.split(" ").includes(c) },
      style: { setProperty: (k, v) => props.set(k, v), removeProperty: (k) => props.delete(k), props },
      setAttribute: (k, v) => attrs.set(k, String(v)), getAttribute: (k) => (attrs.has(k) ? attrs.get(k) : null),
      removeAttribute: (k) => attrs.delete(k), attrs,
      appendChild: (c) => { node.children.push(c); c.parent = node; },
      prepend: (c) => { node.children.unshift(c); c.parent = node; },
      remove: () => { node.parent.children.splice(node.parent.children.indexOf(node), 1); },
    };
    return node;
  };
  const doc = { documentElement: el(), body: el(), createElement: () => el() };
  doc.documentElement.ownerDocument = doc;
  if (theme) doc.documentElement.setAttribute("data-theme", theme);
  const store = new Map();
  const listeners = [];
  const win = {
    localStorage: { getItem: (k) => (store.has(k) ? store.get(k) : null), setItem: (k, v) => store.set(k, String(v)) },
    matchMedia: () => ({ matches: prefersLight, addEventListener: (_, f) => listeners.push(f) }),
    flip: (light) => { prefersLight = light; listeners.forEach((f) => f()); },
    store,
  };
  return { doc, win };
}
const root = (doc) => doc.documentElement;
const stages = (doc) => doc.body.children.filter((c) => c.className === "look-stage");

test("the lab's formulas: halo, glass and texture variables from the owner's Dark Emissive dual profile", () => {
  const v = lookVars(BUILTINS.find((l) => l.id === "dark-emissive"), "dual");
  assert.equal(v["--halo-blur"], "16px");
  assert.equal(v["--halo-blur-tight"], "6.7px");
  assert.equal(v["--halo-opacity"], "0.45");
  assert.equal(v["--halo-overdrive-opacity"], "0.00");
  assert.equal(v["--halo-stroke"], "0.93px");
  assert.equal(v["--glass-blur"], "18px");
  assert.equal(v["--glass-opacity"], "0.06");
  assert.equal(v["--glass-rgb"], "10, 15, 28");
  assert.equal(v["--glass-texture"], "0.08");
  const over = lookVars({ ...BUILTINS[1], profiles: { shadow: { ...BUILTINS[1].profiles.shadow, sliderHaloIntensity: 160, sliderHaloBlur: 2 } } }, "shadow");
  assert.equal(over["--halo-overdrive-opacity"], "0.57");
  assert.equal(over["--halo-blur-tight"], "1.2px", "the tight blur floors at 1.2px");
});

test("defaults with no ah-optics, bad JSON, a string, an array; the stored value stays until the next save", () => {
  for (const raw of [undefined, "{bad", '"oops"', "[]"]) {
    const { doc, win } = fakeDoc("light");
    if (raw !== undefined) win.localStorage.setItem(KEY, raw);
    assert.doesNotThrow(() => bootLooks(doc, win));
    assert.equal(root(doc).getAttribute("data-look-mode"), "light-ink", `raw ${raw}`);
    assert.equal(root(doc).getAttribute("data-look-tech"), "dual");
    if (raw !== undefined) assert.equal(win.store.get(KEY), raw);
  }
});

test("skin house forces House in both themes whatever active says; skin glass with no active takes the defaults", () => {
  const house = readOptics({ getItem: () => JSON.stringify({ skin: "house", active: { light: "light-emissive", dark: "dark-emissive" } }) });
  assert.ok(lookFor(house, "light") === HOUSE && lookFor(house, "dark") === HOUSE);
  const glass = readOptics({ getItem: () => JSON.stringify({ skin: "glass" }) });
  assert.deepEqual([lookFor(glass, "light").id, lookFor(glass, "dark").id, glass.technique], ["light-inky", "dark-emissive", "shadow"]);
});

test("a slot holding a look of the other theme, or an unknown id, falls back to its default", () => {
  const s = readOptics({ getItem: () => JSON.stringify({ active: { light: "dark-emissive", dark: "deleted-custom" } }) });
  assert.deepEqual([lookFor(s, "light").id, lookFor(s, "dark").id], ["light-inky", "dark-emissive"]);
});

test("House leaves no look attribute, no look variable and no stage; dual on House is still House", () => {
  const { doc, win } = fakeDoc("dark");
  win.localStorage.setItem(KEY, JSON.stringify({ technique: "dual" }));
  bootLooks(doc, win);
  assert.equal(stages(doc).length, 1);
  assert.ok(root(doc).style.props.size > 0);
  apply(HOUSE, "dual", root(doc));
  assert.deepEqual([...root(doc).attrs.keys()].filter((k) => k.startsWith("data-look")), []);
  assert.equal(root(doc).style.props.size, 0);
  assert.equal(stages(doc).length, 0);
});

test("System theme: the OS flipping light and dark switches the look live; data-theme wins over the OS", () => {
  const { doc, win } = fakeDoc(null, false);
  bootLooks(doc, win);
  assert.equal(root(doc).getAttribute("data-look-mode"), "dark-emissive");
  win.flip(true);
  assert.equal(root(doc).getAttribute("data-look-mode"), "light-ink");
  assert.equal(stages(doc).length, 1, "one stage, re-used");
  root(doc).setAttribute("data-theme", "dark");
  win.flip(true);
  assert.equal(root(doc).getAttribute("data-look-mode"), "dark-emissive");
});

test("choose stores the slot's look and drops a harness skin seed", () => {
  const { doc, win } = fakeDoc("light");
  win.localStorage.setItem(KEY, JSON.stringify({ skin: "house" }));
  choose("light", "light-emissive", doc, win);
  assert.deepEqual(JSON.parse(win.store.get(KEY)).active, { light: "light-emissive", dark: "dark-emissive" });
  assert.equal(root(doc).getAttribute("data-look-mode"), "light-emissive");
});

test("the built-ins carry the owner's tuned values; a look sets no accent, status or role colour, and marks only through its own ink overrides", () => {
  const p = (id, t) => Object.values(BUILTINS.find((l) => l.id === id).profiles[t]).join(" ");
  assert.equal(p("light-inky", "dual"), "cyan #ffffff 27 18 6 14 100 #ffffff 52 48 25");
  assert.equal(p("light-emissive", "dual"), "cyan #ffffff 25 10 6 14 50 #ffffff 52 70 100");
  assert.equal(p("dark-emissive", "dual"), "amber #0a0f1c 8 18 6 16 45 #111c36 36 36 63");
  assert.equal(p("dark-emissive", "shadow"), "obsidian #0a0f1c 0 18 72 5.5 135 #0f172a 35 36 100");
  for (const look of BUILTINS.filter((l) => l.profiles)) {
    const names = Object.keys(lookVars({ ...look, ink: {} }, "shadow"));
    assert.deepEqual(names.filter((n) => /^--(accent|working|attn|ok|err|review|blocked|idle|role-)|-mark$/.test(n)), [], look.id);
  }
});

test("a blocked store (the localStorage getter throws) boots the defaults and a choice still applies for this page", () => {
  const { doc } = fakeDoc("light");
  const win = { matchMedia: () => ({ matches: false, addEventListener() {} }) };
  Object.defineProperty(win, "localStorage", { get() { throw new Error("blocked"); } });
  assert.doesNotThrow(() => bootLooks(doc, win));
  assert.equal(root(doc).getAttribute("data-look-mode"), "light-ink");
  assert.doesNotThrow(() => choose("light", "light-emissive", doc, win));
  assert.equal(root(doc).getAttribute("data-look-mode"), "light-emissive", "the click takes effect for this page");
  bootLooks(doc, win);
  assert.equal(root(doc).getAttribute("data-look-mode"), "light-emissive", "a re-apply (theme change) keeps it");
});

test("an unknown accent or a bad glass colour falls back to the default everywhere, a stored custom look included", () => {
  const base = BUILTINS.find((l) => l.id === "dark-emissive");
  const bad = { ...base, id: "c1", builtin: false, ink: {}, profiles: { shadow: { ...base.profiles.shadow, accent: "x", glassColor: "nope" }, dual: { ...base.profiles.dual, accent: "x" } } };
  const texts = { "--ink": [228, 231, 234] };
  assert.doesNotThrow(() => contrastOf(bad, "shadow", texts));
  assert.doesNotThrow(() => panes(bad, "shadow"));
  assert.equal(lookVars(bad, "shadow")["--glass-rgb"], "10, 15, 28", "the theme default's glass");
  const { doc, win } = fakeDoc("dark");
  win.localStorage.setItem(KEY, JSON.stringify({ active: { dark: "c1" }, custom: [bad] }));
  const state = readOptics(win.localStorage);
  assert.deepEqual([state.custom[0].profiles.shadow.accent, state.custom[0].profiles.shadow.glassColor, state.custom[0].profiles.dual.accent], ["obsidian", "#0a0f1c", "obsidian"]);
  bootLooks(doc, win);
  assert.equal(root(doc).getAttribute("data-look-accent"), "obsidian");
});

// ---- S2: the Looks page's edits ----

const empty = () => readOptics({ getItem: () => null });
const inky = BUILTINS.find((l) => l.id === "light-inky");

test("a store that exists but refuses the write (quota, private mode) still keeps the click, and re-applies keep it", () => {
  const { doc, win } = fakeDoc("light");
  win.localStorage.setItem = () => { throw new Error("QuotaExceededError"); };
  bootLooks(doc, win);
  choose("light", "light-emissive", doc, win);
  assert.equal(root(doc).getAttribute("data-look-mode"), "light-emissive", "the click takes effect");
  bootLooks(doc, win);
  assert.equal(root(doc).getAttribute("data-look-mode"), "light-emissive", "a re-apply (theme change) keeps it");
});

test("a switch removes exactly the variables the last look set, a custom look's own keys included (S1 nit 4)", () => {
  const { doc } = fakeDoc("light");
  apply({ ...inky, id: "c1", builtin: false, ink: { "--x-custom": "#123456", "--ink": "#101010" } }, "shadow", root(doc));
  assert.equal(root(doc).style.props.get("--x-custom"), "#123456");
  apply(BUILTINS.find((l) => l.id === "dark-emissive"), "shadow", root(doc));
  assert.ok(!root(doc).style.props.has("--x-custom") && !root(doc).style.props.has("--ink"));
  apply(HOUSE, "shadow", root(doc));
  assert.equal(root(doc).style.props.size, 0);
});

test("copy names '<name> copy', de-duplicated; a stored look's ink keeps only text and mark tokens", () => {
  let state = empty();
  const a = copyOf(inky, state);
  assert.equal(a.name, "Light Inky copy");
  assert.deepEqual([a.theme, a.mode, a.builtin, a.backdrop.pattern], ["light", "light-ink", false, "grid"]);
  state = { ...state, custom: [a] };
  assert.equal(copyOf(inky, state).name, "Light Inky copy 2");
  const stored = readOptics({ getItem: () => JSON.stringify({ custom: [{ ...a, ink: { "--accent": "#ff0000", "--dim": "#626972", "--ink": "red" } }] }) });
  assert.deepEqual(stored.custom[0].ink, { "--dim": "#626972" });
});

test("rename: non-empty, at most 40 characters, unique among custom looks", () => {
  const a = { ...copyOf(inky, empty()), id: "a", name: "One" };
  const state = { ...empty(), custom: [a, { ...a, id: "b", name: "Two" }] };
  assert.equal(nameError("  ", "a", state), "A look needs a name.");
  assert.match(nameError("x".repeat(41), "a", state), /40 characters/);
  assert.equal(nameError("Two", "a", state), "Another custom look has that name.");
  assert.equal(nameError("One", "a", state), null, "its own name is fine");
});

test("deleting the active custom look sends its slot back to the default", () => {
  const a = { ...copyOf(BUILTINS.find((l) => l.id === "dark-emissive"), empty()), id: "c-dark" };
  const state = { ...empty(), active: { light: "light-emissive", dark: "c-dark" }, custom: [a] };
  const next = withoutLook(state, "c-dark");
  assert.deepEqual(next.active, { light: "light-emissive", dark: "dark-emissive" });
  assert.equal(next.custom.length, 0);
});

test("export then import round-trips the same controls and backdrop, under the same name", () => {
  const a = copyOf(inky, empty());
  a.profiles.shadow.sliderHaloBlur = 7.5;
  a.profiles.dual.accent = "amber";
  a.backdrop = { ...a.backdrop, pattern: "blueprint", washA: "#112233", glow: false };
  const file = exportLook(a, "shadow", new Date(0));
  assert.deepEqual([file.schema, file.current_mode, file.exported_at], [3, "light-ink", "1970-01-01T00:00:00.000Z"]);
  const out = importLooks(JSON.stringify(file), empty());
  assert.equal(out.looks.length, 1);
  const b = out.looks[0];
  assert.deepEqual([b.name, b.mode, b.theme], ["Light Inky copy", "light-ink", "light"]);
  assert.deepEqual(b.profiles, a.profiles);
  assert.deepEqual(b.backdrop, a.backdrop);
  assert.notEqual(b.id, a.id);
});

test("the lab's Copy-all imports as 3 looks; resolved is dropped; names fall back to '<mode> (imported)', de-duplicated", () => {
  const profile = (c) => ({ controls: c, resolved: { root: { "--x": "1" } } });
  const modes = Object.fromEntries(["light-ink", "light-emissive", "dark-emissive"].map((m) => {
    const p = BUILTINS.find((l) => l.mode === m).profiles;
    return [m, { technique: "shadow", profiles: { shadow: profile(p.shadow), dual: profile(p.dual) }, backdrop: { controls: backdropDefaults(m), resolved: {} } }];
  }));
  const copyAll = JSON.stringify({ schema: 3, current_mode: "light-emissive", exported_at: "x", modes });
  const out = importLooks(copyAll, empty());
  assert.deepEqual(out.looks.map((l) => l.name), ["light-ink (imported)", "light-emissive (imported)", "dark-emissive (imported)"]);
  assert.ok(out.looks.every((l) => !JSON.stringify(l).includes("resolved")));
  const again = importLooks(copyAll, out.state);
  assert.equal(again.looks[0].name, "light-ink (imported) 2");
});

test("import refuses schema 1 and 2, bad JSON and unknown modes, and imports nothing; out-of-range controls clamp", () => {
  const state = empty();
  for (const text of ["{bad", JSON.stringify({ schema: 2, modes: {} }), JSON.stringify({ schema: 1, name: "dark-ink" }), JSON.stringify({ schema: 3, modes: { "dark-ink": {} } })]) {
    const out = importLooks(text, state);
    assert.ok(out.error && !out.looks, text);
  }
  const p = inky.profiles.shadow;
  const hot = importLooks(JSON.stringify({ schema: 3, modes: { "light-ink": { profiles: { shadow: { controls: { ...p, sliderHaloIntensity: 500, sliderGlassOpacity: -5 } }, dual: { controls: p } }, backdrop: { controls: { patternSize: 900, pattern: "zigzag" } } } } }), state);
  const c = hot.looks[0].profiles.shadow;
  assert.deepEqual([c.sliderHaloIntensity, c.sliderGlassOpacity], [200, 0]);
  assert.deepEqual([hot.looks[0].backdrop.patternSize, hot.looks[0].backdrop.pattern], [96, "grid"]);
});

test("a custom backdrop paints the stage and counts in contrast: pattern layers, plain, glow off, base colour", () => {
  const copy = copyOf(inky, empty());
  const a = { ...copy, profiles: { ...copy.profiles, shadow: { ...copy.profiles.shadow, glassTexture: 0 } } }; // no frost: full frost hides the stage
  const grid = lookVars(a, "shadow");
  assert.match(grid["--look-stage"], /^linear-gradient\(to right/);
  assert.match(grid["--look-stage-size"], /^28px 28px, 28px 28px, auto/);
  const plain = lookVars({ ...a, backdrop: { ...a.backdrop, pattern: "plain", glow: false } }, "shadow");
  assert.match(plain["--look-stage"], /^radial-gradient/);
  assert.doesNotMatch(plain["--look-stage"], /circle [\d.]+px/, "glow off paints no orbs");
  assert.match(grid["--look-stage"], /circle [\d.]+px/, "glow on paints them, under the grid");
  const dark = { ...a, backdrop: { ...a.backdrop, baseFrom: "#000000", baseTo: "#000000", washIntensity: 0, glow: false } };
  const ink = { "--ink": [20, 20, 20] };
  assert.ok(contrastOf(dark, "shadow", ink)[0].ratio < contrastOf(a, "shadow", ink)[0].ratio, "a black base darkens the panes");
});

test("a copy of every built-in starts identical to it: every variable, both profiles, and the same contrast (rework 1, finding 3)", () => {
  for (const look of BUILTINS.filter((l) => l.profiles)) {
    const copy = copyOf(look, empty());
    for (const t of ["shadow", "dual"]) assert.deepEqual(lookVars(copy, t), lookVars(look, t), `${look.id} ${t}`);
    assert.deepEqual(panes(copy, "shadow"), panes(look, "shadow"), look.id);
  }
  const stored = readOptics({ getItem: () => JSON.stringify({ custom: [copyOf(BUILTINS.find((l) => l.id === "dark-emissive"), empty())] }) });
  assert.equal(stored.custom[0].backdrop.builtin, undefined);
  assert.deepEqual(lookVars(stored.custom[0], "shadow"), lookVars(BUILTINS.find((l) => l.id === "dark-emissive"), "shadow"), "and it survives a save");
});

test("every look rule skips a Looks-page preview that shows House, so a glass :root never reaches into it (rework 1, finding 1)", async () => {
  const { readFileSync } = await import("node:fs");
  const css = readFileSync(new URL("../../web/ui/looks.css", import.meta.url), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
  const topLevel = (sel) => { const out = [""]; let depth = 0; for (const ch of sel) { if (ch === "(") depth += 1; if (ch === ")") depth -= 1; if (ch === "," && depth === 0) out.push(""); else out[out.length - 1] += ch; } return out.map((s) => s.trim()); };
  const selectors = [...css.matchAll(/([^{}]+)\{[^{}]*\}/g)].flatMap((m) => topLevel(m[1])).filter((s) => s.startsWith("[data-look-") && !s.endsWith("> .look-stage"));
  assert.ok(selectors.length >= 9, `found ${selectors.length}`);
  for (const s of selectors) assert.ok(s.endsWith(":not(.looks-pv:not([data-look-mode]) *)"), s);
});

test("the stage stays sharp: no filter on it, ever; a surface's blur is worked into its gradients, with no backdrop-filter on layers 1 and 2", async () => {
  const { readFileSync } = await import("node:fs");
  const css = readFileSync(new URL("../../web/ui/looks.css", import.meta.url), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
  const stage = [...css.replace(/\/\*[\s\S]*?\*\//g, "").matchAll(/([^{}]*\.look-stage[^{}]*)\{([^}]*)\}/g)].map((m) => m[2]).join("\n");
  assert.doesNotMatch(stage, /filter|blur/);
  assert.match(stage, /inset: 0/);
  const layer1 = /:is\(\.stage-wrap, \.rail, \.shell-head[^{]*\{([^}]*)\}/.exec(css)[1];
  assert.doesNotMatch(layer1, /backdrop-filter/);
  assert.match(layer1, /background-attachment: fixed/);
  const layer2 = /:is\(\.panel, \.member-rail[^{]*\{([^}]*)\}/.exec(css)[1];
  assert.doesNotMatch(layer2, /filter|background-image/);
});

test("the glass clears as its fill goes: under 35% the blur and saturation fade, and at 0% a surface sees the stage's own sharp lines", () => {
  const dark = BUILTINS.find((l) => l.id === "dark-emissive");
  const at = (sliderGlassOpacity, sliderGlassBlur = 18) => lookVars({ ...dark, profiles: { ...dark.profiles, dual: { ...dark.profiles.dual, sliderGlassOpacity, sliderGlassBlur } } }, "dual");
  assert.deepEqual([at(35)["--look-view-blur"], at(35)["--look-view-saturate"]], ["18px", "160%"]);
  assert.deepEqual([at(6)["--look-view-blur"], at(6)["--look-view-saturate"]], ["3.09px", "110.3%"]);
  assert.deepEqual([at(0)["--look-view-blur"], at(0)["--look-view-saturate"]], ["0px", "100%"]);
  assert.match(at(0)["--look-view"], /^linear-gradient\(to right, rgba\(255, 255, 255, 0\.055\) 1px/, "0% glass: the grid, sharp");
  assert.match(at(72, 0)["--look-view"], /1px, transparent 1px/, "Glass blur at 0: the grid, sharp");
  assert.doesNotMatch(at(72)["--look-view"], /1px, transparent 1px/, "full blur: the lines are gone");
});

test("the grid is over the colour, on the stage and through the glass; custom card and line controls reach the page, and a look without them takes its mode's", () => {
  const dark = BUILTINS.find((l) => l.id === "dark-emissive");
  const v = lookVars(dark, "dual");
  assert.ok(v["--look-stage"].indexOf("linear-gradient(to right") < v["--look-stage"].indexOf("circle "), "grid above orbs");
  assert.equal(v["--look-box"], "rgba(17, 28, 54, 0.36)");
  assert.equal(v["--look-hair"], "0.36");
  assert.equal(v["--look-line"], "0.63");
  const { boxColor: _c, sliderBoxOpacity: _o, sliderBoxBorder: _b, sliderLineStrength: _l, ...bare } = dark.profiles.dual;
  const plain = lookVars({ ...dark, profiles: { ...dark.profiles, dual: bare } }, "dual");
  assert.deepEqual([plain["--look-box"], plain["--look-hair"], plain["--look-line"]], ["rgba(15, 23, 42, 0.35)", "0.36", "1"]);
});

test("no look paints a data: image, which the page's CSP refuses: frost and the noise pattern come from static files that exist (S4)", () => {
  const noise = (color) => {
    const copy = copyOf(inky, empty());
    return { ...copy, backdrop: { ...copy.backdrop, pattern: "noise", patternColor: color } };
  };
  for (const look of [...BUILTINS.filter((l) => l.profiles), noise("#000000"), noise("#ffffff")]) {
    for (const t of ["shadow", "dual"]) {
      const v = lookVars(look, t);
      assert.doesNotMatch(Object.values(v).join(" "), /data:/, look.id);
      for (const [, file] of Object.values(v).join(" ").matchAll(/url\("\/ui\/([^"]+)"\)/g)) assert.ok(existsSync(new URL(`../../web/ui/${file}`, import.meta.url)), file);
    }
  }
  assert.match(lookVars(inky, "dual")["--look-frost"], /\/ui\/frost\.svg.*27%/);
  assert.equal(lookVars({ ...inky, profiles: { ...inky.profiles, shadow: { ...inky.profiles.shadow, glassTexture: 0 } } }, "shadow")["--look-frost"], "none");
  assert.match(lookVars(noise("#000000"), "shadow")["--look-stage"], /noise-dark\.svg/);
  assert.match(lookVars(noise("#ffffff"), "shadow")["--look-stage"], /noise-light\.svg/);
});

test("text colours live in the look: all six survive a store and an export, the heading colour drives the halo ink and each scores (S4)", () => {
  const ink = { "--ink": "#111111", "--ink-2": "#222222", "--muted": "#333333", "--dim": "#444444", "--ink-strong": "#550000", "--accent-ink": "#000066" };
  const look = { ...copyOf(inky, empty()), ink };
  const stored = readOptics({ getItem: () => JSON.stringify({ custom: [{ ...look, ink: { ...ink, "--accent": "#ff0000", "--dim": "red" } }] }) }).custom[0];
  assert.deepEqual(stored.ink, Object.fromEntries(Object.entries(ink).filter(([k]) => k !== "--dim")), "a non-hex colour and a non-text token are dropped");
  const again = importLooks(JSON.stringify(exportLook(look, "shadow")), empty()).looks[0];
  assert.deepEqual(again.ink, ink);
  const v = lookVars(look, "shadow");
  assert.equal(v["--accent-ink"], "#000066");
  assert.equal(v["--ink-strong"], "#550000");
  assert.equal(v["--look-core"], "rgb(85, 0, 0)");
  const rows = contrastOf(look, "shadow", { "--ink": [0, 0, 0], "--accent-ink": [0, 0, 0] });
  assert.deepEqual(rows.find((r) => r.token === "--accent-ink").rgb, [0, 0, 102]);
  const pale = contrastOf({ ...look, ink: { "--ink-strong": "#dddddd" } }, "shadow", {}).find((r) => r.token === "--ink-strong");
  assert.equal(pale.ok, false, "a pale heading on a light pane warns");
});

test("the noise tiles paint neutral grain in their own colour, and the pattern opacity reaches them unscaled as the lab's does (S4 rework)", () => {
  for (const [file, level] of [["noise-light.svg", 1], ["noise-dark.svg", 0]]) {
    const svg = readFileSync(new URL(`../../web/ui/${file}`, import.meta.url), "utf8");
    const m = /feColorMatrix values='([^']+)'/.exec(svg)[1].trim().split(/\s+/).map(Number);
    assert.equal(m.length, 20, file);
    for (let row = 0; row < 3; row++) assert.deepEqual(m.slice(row * 5, row * 5 + 5), [0, 0, 0, 0, level], `${file} channel ${row}`);
    assert.deepEqual(m.slice(15), [3, 0, 0, 0, -1.5], `${file} alpha is the lab's gain`);
  }
  const copy = copyOf(inky, empty());
  const at = (patternOpacity) => lookVars({ ...copy, backdrop: { ...copy.backdrop, pattern: "noise", patternOpacity } }, "shadow")["--look-stage"];
  assert.match(at(10), /noise-dark\.svg"\), 10%\)/);
  assert.match(at(60), /noise-dark\.svg"\), 60%\)/);
});

const dualLook = (extra = {}) => ({ ...copyOf(BUILTINS.find((l) => l.id === "dark-emissive"), empty()), id: "c1", ...extra });
const booted = (look, stored = {}) => {
  const { doc, win } = fakeDoc("dark");
  win.store.set(KEY, JSON.stringify({ active: { dark: look.id }, custom: [look], ...stored }));
  bootLooks(doc, win);
  return root(doc);
};

test("the app draws a look with its saved profile; no profile or a bad one reads as Shadow, whatever the old global technique says", () => {
  assert.equal(booted(dualLook({ profile: "dual" })).getAttribute("data-look-tech"), "dual");
  assert.equal(booted(dualLook({ profile: "shadow" }), { technique: "dual" }).getAttribute("data-look-tech"), "shadow");
  const { profile: _, ...none } = dualLook();
  assert.equal(booted(none, { technique: "dual" }).getAttribute("data-look-tech"), "shadow");
  assert.equal(booted(dualLook({ profile: "ghosts" })).getAttribute("data-look-tech"), "shadow");
  assert.equal(profileOf(BUILTINS.find((l) => l.id === "dark-emissive")), "dual");
});

test("the graph option is off by default, saved with the look, and only marks the root under Dual", () => {
  assert.equal(dualLook().graphShadow, false);
  assert.equal(booted(dualLook({ profile: "dual" })).getAttribute("data-look-graph"), null);
  assert.equal(booted(dualLook({ profile: "dual", graphShadow: true })).getAttribute("data-look-graph"), "shadow");
  assert.equal(booted(dualLook({ profile: "shadow", graphShadow: true })).getAttribute("data-look-graph"), null);
  assert.equal(booted(dualLook({ profile: "dual", graphShadow: "yes" })).getAttribute("data-look-graph"), null);
  const { doc } = fakeDoc("dark");
  const on = dualLook({ profile: "dual", graphShadow: true });
  apply(on, "dual", root(doc));
  apply({ ...on, graphShadow: false }, "dual", root(doc));
  assert.equal(root(doc).getAttribute("data-look-graph"), null);
  apply(on, "dual", root(doc));
  apply(HOUSE, "shadow", root(doc));
  assert.equal(root(doc).getAttribute("data-look-graph"), null);
});

test("the graph option gives the Graph stage every value the Shadow profile changes, and goes when the option does", () => {
  const look = dualLook({ profile: "dual", graphShadow: true });
  look.profiles = { shadow: { accent: "cyan", glassColor: "#112233", glassTexture: 11, sliderGlassBlur: 5, sliderGlassOpacity: 20, sliderHaloBlur: 2, sliderHaloIntensity: 150, boxColor: "#101010", sliderBoxOpacity: 10, sliderBoxBorder: 10, sliderLineStrength: 10 },
    dual: { accent: "amber", glassColor: "#445566", glassTexture: 77, sliderGlassBlur: 25, sliderGlassOpacity: 90, sliderHaloBlur: 12, sliderHaloIntensity: 40, boxColor: "#202020", sliderBoxOpacity: 20, sliderBoxBorder: 20, sliderLineStrength: 20 } };
  const [s, d] = [lookVars(look, "shadow"), lookVars(look, "dual")];
  const changed = (l) => Object.keys(lookVars(l, "shadow")).filter((k) => lookVars(l, "shadow")[k] !== lookVars(l, "dual")[k] && !k.startsWith("--look-ghost"));
  const light = { ...copyOf(inky, empty()), profiles: look.profiles }; // the heading ink follows the accent only on the light modes
  assert.deepEqual([...new Set([...changed(look), ...changed(light)])].sort(), [...GRAPH_VARS].sort());
  const css = readFileSync(new URL("../../web/ui/looks.css", import.meta.url), "utf8");
  const { doc } = fakeDoc("dark");
  apply(look, "dual", root(doc));
  for (const k of GRAPH_VARS) {
    assert.ok(css.includes(`${k}: var(--look-graph-${k.slice(2)});`), k);
    assert.equal(root(doc).style.props.get(`--look-graph-${k.slice(2)}`), s[k]);
    assert.equal(root(doc).style.props.get(k), d[k]);
  }
  apply({ ...look, graphShadow: false }, "dual", root(doc));
  assert.equal(root(doc).style.props.get("--look-graph-halo-blur"), undefined);
});

test("a copy keeps the look's profile and graph option, and export then import round-trips them", () => {
  const a = dualLook({ profile: "dual", graphShadow: true });
  const copy = copyOf(a, empty());
  assert.deepEqual([copy.profile, copy.graphShadow], ["dual", true]);
  const [b] = importLooks(JSON.stringify(exportLook(a, "dual")), empty()).looks;
  assert.deepEqual([b.profile, b.graphShadow], ["dual", true]);
  const [lab] = importLooks(JSON.stringify({ schema: 3, modes: { "dark-emissive": { profiles: a.profiles } } }), empty()).looks;
  assert.deepEqual([lab.profile, lab.graphShadow], ["shadow", false]);
});

test("the ghosts take the lab's colours per mode, and a preview takes its own technique, never the app's", () => {
  const vars = (id) => lookVars(BUILTINS.find((l) => l.id === id), "dual");
  assert.deepEqual([vars("dark-emissive")["--look-ghost-rgb"], vars("dark-emissive")["--look-ghost-edge-rgb"], vars("dark-emissive")["--look-ghost-spread"]], ["245, 158, 11", "254, 240, 138", "0.7px"]);
  assert.equal(vars("light-inky")["--look-ghost-spread"], "0.9px");
  const css = readFileSync(new URL("../../web/ui/looks.css", import.meta.url), "utf8");
  assert.match(css, /:root\[data-look-tech="dual"\] \.r6 :is\([^)]*\):not\(\.looks-pv \*\):not\(:root\[data-look-graph="shadow"\] \.view-graph \*\)/);
  assert.match(css, /\.looks-pv\[data-look-tech="dual"\] \.r6 :is\([^)]*\)\)\[data-text\]/);
  // The Live feed bar keeps its sticky position (r6-shell.css) under Dual, and its ghosts sit with its centred letters.
  assert.match(css, /&:not\(\.rail \.feed > h2\) \{ position: relative; z-index: 0; \}/);
  assert.equal(css.match(/^\s*position: relative;/gm), null);
  assert.match(css, /&:is\(\.rail \.feed > h2, \.teams-rail h2, \.sr-col h3\)::before, &:is\(\.rail \.feed > h2, \.teams-rail h2, \.sr-col h3\)::after \{ display: flex; align-items: center; \}/);
  assert.match(css, /&:is\(\.kb-title, \.kb-label, \.sr-col h3, \.rail \.feed > h2, \.teams-rail h2\)::before \{ filter: blur\(min\(var\(--halo-blur-tight\), 3px\)\)/, "a Sitrep heading's ghost stays narrow, or it smudges");
  assert.doesNotMatch(css, /&:is\(\.kb-title, [^)]*\.panel-head/, "negative: a pane heading keeps its wide halo");
});

test("a preview's slab paints no viewport-fixed view: layer 1's rule leaves previews out, so their own override applies", () => {
  const css = readFileSync(new URL("../../web/ui/looks.css", import.meta.url), "utf8");
  const layer1 = /\[data-look-mode\] \.r6 :is\(\.stage-wrap, \.rail, \.shell-head[^{]*\{[^}]*background-attachment: fixed/.exec(css)[0];
  assert.match(layer1, /:not\(\.looks-pv\[data-look-mode\] \*\):not/);
  const pv = /\.looks-pv\[data-look-mode\] \.r6 :is\(\.stage-wrap, \.rail, \.shell-head, \.frame-head, \.shell-foot\) \{([^}]*)\}/.exec(css)[1];
  assert.doesNotMatch(pv, /--look-view\)|attachment: fixed/);
});

test("the slab's edge is a border: an outline repaints the whole slab each time anything inside it paints, such as an age ticking", () => {
  const css = readFileSync(new URL("../../web/ui/looks.css", import.meta.url), "utf8");
  const slab = /\[data-look-mode\] \.r6 :is\(\.stage-wrap, \.rail\):not\([^{]*\{([^}]*)\}/.exec(css)[1];
  assert.match(slab, /border: 1px solid var\(--layer-1-edge\)/);
  assert.doesNotMatch(slab, /outline/);
});

// b125: the preview draws what the app applies.
test("a built-in's preview opens on the profile the app draws, and its chip only browses", async () => {
  const { LooksPage } = await import("../../web/ui/looks-page.js");
  const inky = BUILTINS.find((l) => l.id === "light-inky");
  const tab = (state, look) => LooksPage.prototype.tab.call({ state }, look);
  assert.equal(tab({ tab: null }, inky), profileOf(inky));
  assert.equal(tab({ tab: "shadow" }, inky), "shadow");
  assert.equal(tab({ tab: "shadow" }, { ...inky, builtin: false }), profileOf(inky));
});

test("the preview draws the Board's own card and head and the app's own bars, and styles only its size", () => {
  const page = readFileSync(new URL("../../web/ui/looks-page.js", import.meta.url), "utf8");
  for (const real of ["kbCard(x)", "kbHead(", "<${Header}", "<${Footer}"]) assert.ok(page.includes(real), real);
  assert.ok(!/class="[^"]*\bkb-title\b/.test(page), "a card title written by hand is a copy of the Board's");
  const css = readFileSync(new URL("../../web/ui/looks-page.css", import.meta.url), "utf8");
  const visual = /\b(background|color|font|border|box-shadow|text-shadow|backdrop-filter)[\w-]*\s*:/;
  const copies = [...css.matchAll(/([^{}]*\.looks-pv \.r6[^{}]*)\{([^}]*)\}/g)].filter((m) => visual.test(m[2])).map((m) => m[1].trim());
  assert.deepEqual(copies, []);
});

test("a preview's slabs drop the theme's solid colour, or no look shows through them", () => {
  const css = readFileSync(new URL("../../web/ui/looks.css", import.meta.url), "utf8");
  const rule = /\.looks-pv\[data-look-mode\] \.r6 :is\(\.stage-wrap, \.rail, \.shell-head, \.frame-head, \.shell-foot\) \{([^}]*)\}/.exec(css);
  assert.ok(rule, "the preview's layer 1 rule");
  assert.match(rule[1], /background-color: transparent/);
});

// b124: the UI language.
const myLook = () => copyOf(BUILTINS.find((l) => l.id === "light-inky"), { custom: [] });
const uiCss = () => readdirSync(new URL("../../web/ui/", import.meta.url)).filter((f) => f.startsWith("r6") && f.endsWith(".css")).map((f) => readFileSync(new URL(`../../web/ui/${f}`, import.meta.url), "utf8")).join("\n");

test("a role's knobs reach only the tokens the look changed", () => {
  const vars = lookVars({ ...myLook(), type: { "card-title": { font: "serif", italic: true, size: 16 } } }, "dual");
  assert.equal(vars["--type-card-title-size"], "16px");
  assert.equal(vars["--type-card-title-style"], "italic");
  assert.match(vars["--type-card-title-font"], /serif/);
  assert.equal(vars["--type-card-title-weight"], undefined);
  assert.equal(Object.keys(lookVars(myLook(), "dual")).filter((k) => k.startsWith("--type-")).length, 0);
});

test("every role token is read by a real view's rule, and the preview resets it", () => {
  const css = uiCss();
  const tokens = readFileSync(new URL("../../web/ui/ui.css", import.meta.url), "utf8");
  for (const t of ROLE_TOKENS) {
    assert.ok(css.includes(`var(${t}`), `no view reads ${t}`);
    assert.ok(tokens.includes(`${t}: initial`), `the preview would take the app's ${t}`);
  }
});

test("each role reaches the rules that draw it, everywhere it appears", () => {
  const css = uiCss();
  const WHERE = {
    "card-title": [".r6 .kb-title", ".r6 .mp-card h3", ".r6 .mp-card h2"], "column-heading": [".r6 .kb-label", ".sitrep .sr-col h3"], "panel-heading": [".r6 .teams-rail h2"],
    "table-heading": [".r6 .teams-table th", ".r6 .bl-table th", ".r6 .cl-table th", ".r6 .qa-table th", ".ov-table .row.head", ".r6 .pl-itable th"],
    tab: [".r6 .tab, .r6 .action", ".r6 .kb-bar .tab"], details: [".r6 .kb-meta"], code: [".r6 .pl-history .mono", ".r6 .pl-branch", ".r6 .pl-where", ".r6 .kb-where", ".r6 .kb-who", ".r6 .bl-id", ".r6 .qa-id", ".sitrep code", ".r6 .feed-row .t", ".r6 .feed-row .slug"],
  };
  for (const { id } of ROLES) {
    assert.ok(WHERE[id], `say where ${id} appears`);
    for (const sel of WHERE[id]) {
      const rules = [...css.matchAll(new RegExp(`^\\s*${sel.replace(/[.*+?^\${}()|[\]\\]/g, "\\$&")}\\s*\\{([^}]*)\\}`, "gm"))];
      assert.ok(rules.some((m) => m[1].includes(`var(--type-${id}-size`)), `${sel} does not read ${id}'s size`);
    }
  }
  // Font, bold and italics reach the header's two switchers as well.
  for (const sel of [/\.r6 \.switch-btn \{[^}]*\}/g, /\.r6 \.more-btn \{[^}]*\}/g]) {
    const rules = [...css.matchAll(sel)].map((m) => m[0]).join(" ");
    for (const k of ["font", "weight", "style"]) assert.ok(rules.includes(`var(--type-tab-${k}`), `${sel} does not read the tab role's ${k}`);
  }
});

test("role settings are untrusted: unknown roles, knobs and fonts go, sizes clamp, and an unchanged look has no type field", () => {
  assert.deepEqual(cleanType({ "card-title": { font: "url(x)", bold: "yes", size: 900, evil: 1 }, nope: { size: 12 }, tab: null }), { "card-title": { size: 24 } });
  assert.deepEqual(cleanType({ tab: { font: ["serif"], italic: true } }), { tab: { italic: true } });
  const { font } = ROLES[0];
  assert.equal(font, "system");
  const saved = (l) => readOptics({ getItem: () => JSON.stringify({ custom: [l] }) }).custom[0];
  assert.ok(!("type" in saved(myLook())), "a look saved before roles must stay what the file holds");
  assert.deepEqual(saved({ ...myLook(), type: { tab: { italic: true } } }).type, { tab: { italic: true } });
});

test("role settings travel with a copy and through export and import", () => {
  const look = { ...myLook(), type: { "card-title": { size: 15 } } };
  assert.deepEqual(copyOf(look, { custom: [] }).type, look.type);
  const out = importLooks(JSON.stringify(exportLook(look)), { custom: [], active: {} });
  assert.deepEqual(out.looks[0].type, look.type);
});

test("the chrome sits on the slab like a card, not as a solid theme colour", () => {
  const css = readFileSync(new URL("../../web/ui/looks.css", import.meta.url), "utf8");
  const rule = /\.tab:not\(\.on, \.primary, \.views \.tab, \.panel > \.tab\), \.action:not\(\.primary, \.danger, :disabled\)[^{]*\{([^}]*)\}/.exec(css);
  assert.ok(rule, "the chrome rule");
  assert.match(rule[1], /background: var\(--layer-2-fill\)/);
  assert.match(rule[1], /border-color: var\(--layer-2-edge\)/);
  const chip = /\.chip:not\(([^)]*\)?[^)]*)\), \.switch-btn/.exec(css)?.[1] ?? "";
  for (const state of [".amber", ".attn", ".res", ".live", "[aria-pressed"]) assert.ok(chip.includes(state), `a ${state} chip must keep its own colours`);
});

test("a role's fallbacks leave a bold id and the Looks page's values as they were with no look", () => {
  assert.match(uiCss(), /\.r6 b\.mono \{ font-weight: var\(--type-code-weight, 700\); \}/);
  assert.match(uiCss(), /:where\(\.r6 \.mono\) \{[^}]*font-size: var\(--type-code-size, inherit\)/);
  assert.doesNotMatch(uiCss(), /(^|\n)\.r6 \.mono \{[^}]*font-size/);
});

test("a per-knob reset removes only that knob, and the last one removes the role and the field", async () => {
  const { LooksPage } = await import("../../web/ui/looks-page.js");
  let d = { ...myLook(), type: { tab: { size: 14, italic: true } } };
  const run = (role, key, value) => {
    const self = new LooksPage({});
    self.edit = (change) => { d = change(d); };
    self.setRole(role, key, value);
  };
  run("tab", "size", null);
  assert.deepEqual(d.type, { tab: { italic: true } });
  run("tab", "italic", null);
  assert.ok(!("type" in d));
});

test("an untouched role's knob shows the size most of its places really have, so a first drag does not jump (b131)", () => {
  const all = readdirSync(new URL("../../web/ui/", import.meta.url)).filter((f) => f.endsWith(".css")).map((f) => readFileSync(new URL(`../../web/ui/${f}`, import.meta.url), "utf8")).join("\n");
  const px = (s) => Number(s === "--text-14" ? 13 : s.match(/\d+/)[0]);
  for (const { id, size } of ROLES) {
    const seen = [...all.matchAll(new RegExp(`var\\(--type-${id}-size, *(var\\((--text-\\d+)\\)|\\d+px)`, "g"))].map((m) => px(m[2] ?? m[1]));
    const count = (n) => seen.filter((x) => x === n).length;
    assert.ok(seen.every((n) => count(n) <= count(size)), `${id} shows ${size}; its places have ${seen}`);
  }
});

test("card details grow their line with their size, and negative: the default stays the 16px line (b131)", () => {
  const css = readFileSync(new URL("../../web/ui/r6-plans-view.css", import.meta.url), "utf8");
  for (const sel of [".r6 .kb-meta > *", ".r6 .kb-meta .chip"]) {
    assert.match(css.split("\n").find((l) => l.startsWith(`${sel} {`)), /line-height: max\(16px, 1\.3em\)/, sel);
  }
  assert.equal(Math.max(16, 11 * 1.3), 16);
});

test("noise grain keeps its tile whatever Pattern size says, and negative: a grid's tile still follows it (b41)", () => {
  const at = (pattern, patternSize) => {
    const copy = copyOf(BUILTINS[1], { custom: [] });
    return JSON.stringify(lookVars({ ...copy, backdrop: { ...copy.backdrop, pattern, patternSize } }, "shadow"));
  };
  assert.equal(at("noise", 16), at("noise", 64));
  assert.notEqual(at("grid", 16), at("grid", 64));
});

test("the Look page works out a profile's contrasts once a render (b41)", () => {
  const src = readFileSync(new URL("../../web/ui/looks-page.js", import.meta.url), "utf8");
  assert.equal(src.split("this.contrasts(look, tab)").length - 1, 1);
});
