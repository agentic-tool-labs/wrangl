import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { contrast, deltaE, deltaE2000, luminance, simulate } from "./color.mjs";
import { deltaMatrix, gate1, gate2, gate3, gate3Preset, resolveRgba, rulesOf, stopsOf, surfaceAt, worstBackdrop } from "./glass-gates.mjs";

// The colour tokens as ui.css declares them, resolved to sRGB, and the contrast of every text colour and role hue on every surface and tint.

const css = readFileSync(new URL("../../web/ui/ui.css", import.meta.url), "utf8");

const declarations = (body) => Object.fromEntries([...body.matchAll(/(--[\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
const blockAfter = (opening) => {
  const at = css.indexOf(opening);
  assert.ok(at >= 0, `ui.css has ${opening}`);
  const open = css.indexOf("{", at);
  return declarations(css.slice(open + 1, css.indexOf("}", open)));
};

const dark = blockAfter(":root {");
const lightMedia = { ...dark, ...blockAfter(':root:not([data-theme="dark"]) {') };
const lightAttr = { ...dark, ...blockAfter(':root[data-theme="light"] {') };

const hex = (c) => [1, 3, 5].map((i) => parseInt(c.slice(i, i + 2), 16));

function resolve(vars, value) {
  const v = value.trim();
  const ref = v.match(/^var\((--[\w-]+)\)$/);
  if (ref) {
    return resolve(vars, vars[ref[1]]);
  }
  const mix = v.match(/^color-mix\(in srgb, (.+?) ([\d.]+)%, (.+)\)$/);
  if (mix) {
    const fg = resolve(vars, mix[1]);
    const bg = resolve(vars, mix[3]);
    const p = Number(mix[2]) / 100;
    return fg.map((c, i) => Math.round(c * p + bg[i] * (1 - p)));
  }
  assert.match(v, /^#[0-9a-f]{6}$/i, `a colour: ${v}`);
  return hex(v);
}


const ROLES = ["orchestrator", "sub-orchestrator", "architect", "implementor", "ux-engineer", "reviewer", "ultra-advisor", "qa", "neutral"];
const TINTS = ["working", "review", "attn", "err"];
const MARKS = ["--working-mark", "--blocked-mark", "--brief-mark", "--report-mark", "--idle-mark"];
const THEMES = { dark, "light (media query)": lightMedia, "light (data-theme)": lightAttr };

test("the contrast helper measures the extremes and can fail a pair", () => {
  assert.equal(Math.round(contrast([0, 0, 0], [255, 255, 255])), 21);
  assert.ok(contrast(hex("#777777"), [255, 255, 255]) < 4.5, "#777 on white is 4.48:1");
});

for (const [theme, vars] of Object.entries(THEMES)) {
  test(`${theme}: every text colour and role hue meets 4.5:1 on the page, the side, a control, the hovered and selected rows and every tint`, () => {
    const surfaces = { "--bg": "var(--bg)", "--side": "var(--side)", "--control": "var(--control)", "--row-hi": "var(--row-hi)", "--row-sel": "var(--row-sel)", ...Object.fromEntries(TINTS.map((t) => [`--tint-${t}`, `var(--tint-${t})`])) };
    const texts = ["--ink", "--ink-2", "--muted", ...ROLES.map((r) => `--role-${r}`)];
    for (const [sName, sValue] of Object.entries(surfaces)) {
      const surface = resolve(vars, sValue);
      for (const t of texts) {
        const ratio = contrast(resolve(vars, `var(${t})`), surface);
        assert.ok(ratio >= 4.5, `${t} on ${sName} is ${ratio.toFixed(2)}:1`);
      }
    }
  });

  test(`${theme}: kind tags read at 4.5:1 or better on their own background`, () => {
    const r6 = readFileSync(new URL("../../web/ui/r6.css", import.meta.url), "utf8");
    for (const selector of [".r6 .kind.brief, .r6 .kind.response, .r6 .kind.report {"]) {
      const body = r6.slice(r6.indexOf(selector)).split("}")[0];
      const fg = /[;{\s]color: (var\([^)]+\))/.exec(body)?.[1];
      const bg = /background: (var\([^)]+\))/.exec(body)?.[1];
      assert.ok(fg && bg, `${selector} sets a colour and a background`);
      const ratio = contrast(resolve(vars, fg), resolve(vars, bg));
      assert.ok(ratio >= 4.5, `${fg} on ${bg} in ${selector} is ${ratio.toFixed(2)}:1`);
    }
  });

  test(`${theme}: a tint is a visible but soft mix, and the error row background is the error tint`, () => {
    const bg = resolve(vars, "var(--bg)");
    for (const t of TINTS) {
      const tint = resolve(vars, `var(--tint-${t})`);
      assert.notDeepEqual(tint, bg, `--tint-${t} differs from the page`);
      assert.ok(contrast(tint, bg) < 1.4, `--tint-${t} stays soft`);
    }
    assert.deepEqual(resolve(vars, "var(--err-bg)"), resolve(vars, "var(--tint-err)"));
  });

  test(`${theme}: every state and kind mark meets 3:1 (non-text) on the page and the first surface`, () => {
    for (const surface of ["--page", "--surface-1"]) {
      const bg = resolve(vars, `var(${surface})`);
      for (const mark of MARKS) {
        const ratio = contrast(resolve(vars, `var(${mark})`), bg);
        assert.ok(ratio >= 3, `${mark} on ${surface} is ${ratio.toFixed(2)}:1`);
      }
    }
  });
}

test("the 3:1 mark check can fail: a mark one step too light is caught", () => {
  assert.ok(contrast(hex("#959ba3"), hex("#f3f4f6")) < 3, "#959ba3 on #f3f4f6 is under 3:1");
});

test("the two light blocks carry the same tint and role-hue values", () => {
  for (const t of [...TINTS.map((n) => `--tint-${n}`), ...ROLES.filter((r) => r !== "orchestrator").map((r) => `--role-${r}`)]) {
    assert.deepEqual(resolve(lightMedia, `var(${t})`), resolve(lightAttr, `var(${t})`), t);
  }
});

test("controls are 4px and panes 6px", () => {
  assert.equal(dark["--radius"], "4px");
  assert.equal(dark["--radius-pane"], "6px");
});

test("panes are cards on the page: 6px corners and an 8px gap between and around them", () => {
  const r6 = readFileSync(new URL("../../web/ui/r6.css", import.meta.url), "utf8");
  assert.match(r6, /\.r6 \.frame-body \{[^}]*gap: var\(--sp-8\); padding: var\(--sp-8\)/);
  assert.match(r6, /\.r6 \.stage-wrap \{[^}]*border-radius: var\(--radius-pane\)/);
  assert.match(r6, /\.r6 \.rail \{[^}]*border-radius: var\(--radius-pane\)/);
  const conversation = readFileSync(new URL("../../web/ui/r6-conversation.css", import.meta.url), "utf8");
  assert.match(conversation, /\.member-rail, \.transcript \{[^}]*border-radius: var\(--radius-pane\)/);
});

test("a tinted row still answers to hover: the hover rule follows the tint rules and outranks them", () => {
  const r6 = readFileSync(new URL("../../web/ui/r6.css", import.meta.url), "utf8");
  const tint = r6.indexOf('.r6 .rail-row[data-state="blocked"]:not(.selected)');
  const hover = r6.indexOf(".r6 .rail-row[data-state]:hover:not(.selected)");
  assert.ok(tint > 0 && hover > tint, "the hover rule comes after the tint rules");
  assert.match(r6, /\.r6 \.rail-row\[data-state\]:hover:not\(\.selected\)/);
  const parts = (sel) => [...sel.matchAll(/\.[\w-]+|\[[^\]]+\]|:hover|:not\([^)]*\)/g)].length;
  assert.ok(parts(".r6 .rail-row[data-state]:hover:not(.selected)") > parts('.r6 .rail-row[data-state="blocked"]:not(.selected)'), "it has more simple selectors than the tint rule");
});

// Map arcs at rest (--arc-alpha, r6-graph.css) composited over what they cross: the page, the first surface, and a stream circle
// (--surface-1 at .55 over the page). Each mark must stay at 3:1. The windowed fade and the selection dim go below on purpose.
const graphCss = readFileSync(new URL("../../web/ui/r6-graph.css", import.meta.url), "utf8");
const alphaIn = (selector) => {
  const m = graphCss.match(new RegExp(`${selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\s*\\{\\s*--arc-alpha:\\s*([\\d.]+)`));
  assert.ok(m, `--arc-alpha under ${selector}`);
  return Number(m[1]);
};
const ARC_ALPHA = {
  dark: alphaIn(".r6 .gpack"),
  "light (media query)": alphaIn(':root:not([data-theme="dark"]) .r6 .gpack'),
  "light (data-theme)": alphaIn(':root[data-theme="light"] .r6 .gpack'),
};
const over = (fg, bg, a) => fg.map((c, i) => Math.round(c * a + bg[i] * (1 - a)));
for (const [theme, vars] of Object.entries(THEMES)) {
  test(`${theme}: Map arcs at rest keep 3:1 over the page, the first surface and a stream circle`, () => {
    const a = ARC_ALPHA[theme];
    const page = resolve(vars, "var(--page)");
    const surface = resolve(vars, "var(--surface-1)");
    const grounds = { "--page": page, "--surface-1": surface, "a stream circle": over(surface, page, 0.55) };
    for (const mark of ["--brief-mark", "--report-mark"]) {
      for (const [name, bg] of Object.entries(grounds)) {
        const ratio = contrast(over(resolve(vars, `var(${mark})`), bg, a), bg);
        assert.ok(ratio >= 3, `${mark} at ${a} over ${name} is ${ratio.toFixed(2)}:1`);
      }
    }
  });
}

test("the arc floor check can fail: dark arcs at .4 are under 3:1", () => {
  const page = resolve(dark, "var(--page)");
  assert.ok(contrast(over(resolve(dark, "var(--brief-mark)"), page, 0.4), page) < 3);
});

// ---- Glass (ah-optical-glass §4.3, §9.2) ----

test("the glass resolver reads rgb() with an alpha, channel tokens, calc() on the opacity lever and gradient stops", () => {
  const vars = { "--glass-opacity": "0.85", "--sw": "56 189 248", "--surface": "rgb(10 15 28 / var(--glass-opacity))" };
  assert.deepEqual(resolveRgba(vars, "rgb(255 255 255 / .95)"), [255, 255, 255, 0.95]);
  assert.deepEqual(resolveRgba(vars, "rgba(99, 102, 241, 0.26)"), [99, 102, 241, 0.26]);
  assert.deepEqual(resolveRgba(vars, "rgb(var(--sw) / .3)"), [56, 189, 248, 0.3]);
  assert.deepEqual(resolveRgba(vars, "rgb(10 15 28 / calc(var(--glass-opacity) + 0.05))").map((x) => Math.round(x * 100) / 100), [10, 15, 28, 0.9]);
  assert.deepEqual(resolveRgba(vars, "var(--surface)"), [10, 15, 28, 0.85]);
  assert.deepEqual(stopsOf(vars, "radial-gradient(circle at 20% 24%, rgb(var(--sw) / .30) 0%, transparent 48%)"), [[56, 189, 248, 0.3], [0, 0, 0, 0]]);
  assert.deepEqual(stopsOf(vars, "linear-gradient(135deg, #f8fafc 0%, #eef2f6 50%, #e2e8f0 100%)"), [hex("#f8fafc").concat(1), hex("#eef2f6").concat(1), hex("#e2e8f0").concat(1)]);
  assert.throws(() => resolveRgba(vars, "oklch(0.5 0.1 200)"), /not a colour/);
});

test("the worst backdrop point is the darkest for light and the brightest for dark", () => {
  const bd = "radial-gradient(circle, rgba(125, 211, 252, 0.55) 0%, transparent 48%), linear-gradient(135deg, #f8fafc 0%, #e2e8f0 100%)";
  const darkest = worstBackdrop({}, bd, false);
  assert.ok(luminance(darkest) <= luminance(hex("#e2e8f0")));
  assert.deepEqual(worstBackdrop({}, "radial-gradient(#334155 0%, #0f172a 100%)", true), hex("#334155"));
});

test("CIEDE2000 matches Sharma's reference pairs and the deuteranopia simulation merges red and green", () => {
  assert.equal(Math.round(deltaE2000([50, 2.6772, -79.7751], [50, 0, -82.7485]) * 1e4) / 1e4, 2.0425);
  assert.equal(Math.round(deltaE2000([50, 2.5, 0], [73, 25, -18]) * 1e4) / 1e4, 27.1492);
  assert.equal(Math.round(deltaE2000([2.0776, 0.0795, -1.135], [0.9033, -0.0636, -0.5514]) * 1e4) / 1e4, 0.9082);
  assert.ok(deltaE(simulate("deuteranopia", [220, 50, 50]), simulate("deuteranopia", [50, 160, 50])) < deltaE([220, 50, 50], [50, 160, 50]));
});

test("gate 2 fails a palette whose attn is the Espresso emissive amber", () => {
  const colours = { working: hex("#8fa8ff"), attn: [245, 158, 11], ok: hex("#a3d65c"), err: hex("#ff6b6b"), review: hex("#c792ea"), swatch: [245, 158, 11] };
  assert.ok(gate2(colours).some((f) => f.startsWith("attn|swatch")), gate2(colours).join("; "));
  assert.deepEqual(gate2({ a: [0, 0, 0], b: [255, 255, 255] }), []);
});

test("gate 3 fails a pair the simulation merges and only reports one the house already had under 15", () => {
  const merged = { a: [200, 60, 60], b: [120, 120, 40] };
  const d = deltaMatrix(merged, "deuteranopia")["a|b"];
  assert.ok(d < 15, `the sample pair is merged: ${d}`);
  assert.equal(gate3(merged, {}).fail.length, 1);
  assert.deepEqual(gate3(merged, merged), { ...gate3(merged, merged), fail: [] });
  assert.equal(gate3(merged, merged).report.length, 1);
});

test("gate 3 fails a pair worse than a house pair that was already under 15", () => {
  const house = { a: [200, 60, 60], b: [140, 60, 40] };
  const glass = { a: [200, 60, 60], b: [160, 80, 40] };
  assert.equal(deltaMatrix(house, "deuteranopia")["a|b"], 12);
  assert.equal(deltaMatrix(glass, "deuteranopia")["a|b"], 5.1);
  assert.equal(gate3(glass, house).fail.length, 1, "house 12, glass 5.1: under min(15, house)");
  assert.deepEqual(gate3(glass, house).report, []);
  assert.deepEqual(gate3(house, house).fail, [], "glass equal to house only reports");
});

test("rulesOf skips rules inside @media and @supports, so media-scoped tokens never reach glassVars", () => {
  const css = ":root[data-glass] { --a: 1; } @media (prefers-contrast: more) { :root[data-glass] { --a: 2; --b: 3; } } @supports (x: y) { :root { --c: 4; } } :root { --d: 5; }";
  assert.deepEqual(rulesOf(css), [{ selector: ":root[data-glass]", vars: { "--a": "1" } }, { selector: ":root", vars: { "--d": "5" } }]);
});

test("gate 1 fails a near-threshold surface", () => {
  const vars = { "--ink": "#767676", "--mark": "#949494" };
  assert.deepEqual(gate1(vars, { white: [255, 255, 255] }, ["--ink"], ["--mark"]), []);
  assert.deepEqual(gate1(vars, { "off-white": [250, 250, 250] }, ["--ink"], ["--mark"]).map((f) => f.split(" on ")[0]), ["--ink", "--mark"], "#767676 is under 4.5:1 and #949494 under 3:1 on #fafafa");
});

// The glass tokens are in web/ui/glass.css (slice G2). Until it exists these tests have nothing to read.
const glassUrl = new URL("../../web/ui/glass.css", import.meta.url);
const glassCss = existsSync(glassUrl) ? readFileSync(glassUrl, "utf8") : null;
const NO_GLASS = glassCss ? false : "web/ui/glass.css is not there yet (slice G2)";
const GLASS_MODES = {
  light: { base: lightAttr, dark: false, attrs: { "data-glass": "light" } },
  emissive: { base: dark, dark: true, attrs: { "data-glass": "dark", "data-dark": "emissive" } },
  "ink-bright": { base: dark, dark: true, attrs: { "data-glass": "dark", "data-dark": "ink", "data-ink-text": "bright" } },
  "ink-etched": { base: dark, dark: true, attrs: { "data-glass": "dark", "data-dark": "ink", "data-ink-text": "etched" } },
};
const GLASS_SWATCHES = ["obsidian", "midnight", "espresso", "forest"];
const OPACITIES = [0.35, 0.85, 0.92];
// A root selector (":root" plus attribute tests) holds when every attribute it names is set as named.
const rootHolds = (selector, attrs) => {
  const m = selector.match(/^:root((?:\[[\w-]+(?:="?[\w-]+"?)?\])*)$/);
  if (!m) return false;
  return [...m[1].matchAll(/\[([\w-]+)(?:="?([\w-]+)"?)?\]/g)].every(([, k, v]) => k in attrs && (v === undefined || attrs[k] === v));
};
const glassVars = (mode, swatch) => {
  const attrs = { ...GLASS_MODES[mode].attrs, "data-swatch": swatch };
  const vars = { ...GLASS_MODES[mode].base };
  for (const rule of rulesOf(glassCss)) {
    if (rule.selector.split(",").some((s) => rootHolds(s.trim(), attrs))) Object.assign(vars, rule.vars);
  }
  return vars;
};
const STATES = ["--working", "--attn", "--ok", "--err", "--review"];
const houseStates = (base) => Object.fromEntries(STATES.map((t) => [t.slice(2), resolve(base, `var(${t})`)]));

test("the root selector match reads attribute tests and refuses anything below the root", () => {
  assert.ok(rootHolds(':root[data-glass="dark"][data-dark="ink"]', { "data-glass": "dark", "data-dark": "ink" }));
  assert.ok(rootHolds(":root[data-glass][data-swatch=forest]", { "data-glass": "light", "data-swatch": "forest" }));
  assert.ok(!rootHolds(':root[data-glass="light"]', { "data-glass": "dark" }));
  assert.ok(!rootHolds(":root[data-glass] .r6 .pane", { "data-glass": "dark" }));
});

for (const mode of Object.keys(GLASS_MODES)) {
  for (const swatch of GLASS_SWATCHES) {
    test(`glass ${mode} / ${swatch}: text at 4.5:1 and marks at 3:1 on the glass over the worst backdrop at opacity .35, .85, .92`, { skip: NO_GLASS }, () => {
      const vars = glassVars(mode, swatch);
      const worst = worstBackdrop(vars, vars["--glass-backdrop"], GLASS_MODES[mode].dark);
      const surfaces = Object.fromEntries(OPACITIES.map((o) => [`glass ${o}`, surfaceAt(vars, worst, o)]));
      const texts = ["--ink", "--ink-2", "--muted", "--dim", ...ROLES.map((r) => `--role-${r}`)];
      const textFails = gate1(vars, surfaces, texts, []);
      const markFails = gate1(vars, surfaces, [], MARKS);
      if (mode === "ink-etched") {
        if (textFails.length) console.log(`report only (etched, §5.3): ${textFails.join("; ")}`);
        assert.deepEqual(markFails, []);
      } else {
        assert.deepEqual([...textFails, ...markFails], []);
      }
    });
    if (mode === "ink-etched") continue;
    test(`glass ${mode} / ${swatch}: states and the swatch stay apart (ΔE 20) and under deuteranopia (§4.3 gates 2, 3)`, { skip: NO_GLASS }, () => {
      const vars = glassVars(mode, swatch);
      const colours = { ...Object.fromEntries(STATES.map((t) => [t.slice(2), resolve(vars, `var(${t})`)])), swatch: resolveRgba(vars, `rgb(var(${GLASS_MODES[mode].dark ? "--swatch-emissive" : "--swatch-light-tint"}))`).slice(0, 3) };
      const g2 = gate2(colours);
      const g3 = gate3(colours, houseStates(GLASS_MODES[mode].base));
      if (g3.report.length) console.log(`report only (OQ-3): ${g3.report.join("; ")}`);
      assert.deepEqual([...g2, ...g3.fail], [], JSON.stringify({ deltaE: deltaMatrix(colours), deuteranopia: deltaMatrix(colours, "deuteranopia"), protanopia: g3.protanopia }));
    });
  }
}

// The built-in looks (ah-ui-looks §4): every text token at 4.5:1 and every mark at 3:1 on the look's panes, the glass composited over
// each stage colour. The numbers come from looks.js's own contrastOf, the one the Looks editor warns with.
const { BUILTINS, contrastOf } = await import("../../web/ui/looks.js");
const LOOK_TEXTS = ["--ink", "--ink-2", "--muted", "--dim", "--accent-ink"];
for (const look of BUILTINS.filter((l) => l.profiles)) {
  for (const technique of ["shadow", "dual"]) {
    test(`look ${look.id} / ${technique}: text at 4.5:1 and marks at 3:1 on its panes over every stage colour`, () => {
      const base = look.theme === "dark" ? dark : lightAttr;
      const pick = (tokens) => Object.fromEntries(tokens.map((t) => [t, resolve(base, `var(${t})`)]));
      const fails = contrastOf(look, technique, pick(LOOK_TEXTS), pick(MARKS)).filter((r) => !r.ok);
      assert.deepEqual(fails, []);
    });
  }
}

test("the look contrast check can fail: a white glass at .92 under the light ink-2 passes, and a black glass fails it", () => {
  const glass = (color) => ({ ...BUILTINS[1], ink: {}, profiles: { shadow: { ...BUILTINS[1].profiles.shadow, glassColor: color, sliderGlassOpacity: 92 } } });
  const ink2 = { "--ink-2": resolve(lightAttr, "var(--ink-2)") };
  assert.equal(contrastOf(glass("#ffffff"), "shadow", ink2).every((r) => r.ok), true);
  assert.equal(contrastOf(glass("#000000"), "shadow", ink2).find((r) => r.token === "--ink-2").ok, false);
});

test("the look contrast covers cards: every stage point gives a pane and a card on that pane (S3)", async () => {
  const { panes, stagePoints } = await import("../../web/ui/looks.js");
  for (const look of BUILTINS.filter((l) => l.profiles)) {
    const n = stagePoints(look, "shadow").length;
    const all = panes(look, "shadow");
    assert.equal(all.length, 6 * n, look.id); // the grain's two extremes, each as the slab, a card on it, and a card on a pane on it
    assert.notDeepEqual(all[2 * n], all[0], `${look.id}: the card is the lab's box over the pane, not the pane again`);
  }
});

test("the frost model stays near the lab at the built-ins' values: the lab's overlay inside the glass's group against the plain alpha looks.css paints (S4)", async () => {
  const { backdropDefaults, paneSolid } = await import("../../web/ui/looks.js");
  const overlay = (b, s) => (b < 0.5 ? 2 * b * s : 1 - 2 * (1 - b) * (1 - s));
  const lab = (g, a, t, s, stage) => g.map((c, i) => { const b = c / 255; return 255 * (t * ((1 - a) * s + a * overlay(b, s)) + a * (1 - t) * b + (1 - t - a * (1 - t)) * (stage[i] / 255)); });
  const hexRgb = (h) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16));
  for (const look of BUILTINS.filter((l) => l.profiles)) {
    const c = look.profiles.dual;
    const want = lab(hexRgb(c.glassColor), c.sliderGlassOpacity / 100, c.glassTexture / 100, 0.73, hexRgb(backdropDefaults(look.mode).baseFrom));
    paneSolid(look, "dual").forEach((v, i) => assert.ok(Math.abs(v - want[i]) < 3, `${look.id}: ${v} against ${want[i]}`));
  }
});

test("frost counts in contrast: the same ink on the same glass scores lower on a light look once the tile paints (S4)", () => {
  const look = BUILTINS[1];
  const at = (tex) => contrastOf({ ...look, ink: {}, profiles: { shadow: { ...look.profiles.shadow, glassTexture: tex }, dual: look.profiles.dual } }, "shadow", { "--ink": [90, 90, 90] }).find((r) => r.token === "--ink").ratio;
  assert.ok(at(100) < at(0), `${at(100)} against ${at(0)}`);
});

test("overlays are gated: a grey that passes on cards but not on a menu fails the look, and says where", async () => {
  const { BUILTINS, contrastOf, backdropDefaults } = await import("../../web/ui/looks.js");
  const light = BUILTINS.find((l) => l.id === "light-inky");
  // A near-opaque white slab over a black stage: a menu lets 8% of the raw stage through, so it is darker than any card.
  const look = JSON.parse(JSON.stringify(light));
  look.ink = {};
  look.backdrop = backdropDefaults("dark-emissive");
  Object.assign(look.profiles.dual, { sliderGlassOpacity: 92, sliderBoxOpacity: 0 });
  const rows = [...Array(256).keys()].map((v) => contrastOf(look, "dual", { "--x": [v, v, v] }).find((r) => r.token === "--x"));
  const onlyMenu = rows.find((r) => r.ratio >= 4.5 && r.overlay < 4.5);
  assert.ok(onlyMenu, "a grey exists that passes on cards and fails on a menu");
  assert.equal(onlyMenu.ok, false);
  assert.equal(onlyMenu.where, "menus");
  assert.equal(onlyMenu.worst, onlyMenu.overlay);
});

test("the default light look's muted text reads at 4.5:1 on its bare backdrop; negative: the dark look's backdrop is untouched", async () => {
  const { BUILTINS, contrastOf, backdropDefaults } = await import("../../web/ui/looks.js");
  const light = BUILTINS.find((l) => l.id === "light-inky");
  const [muted] = contrastOf(light, "dual", { "--muted": [0, 0, 0] });
  assert.ok(muted.slab >= 4.5, `slab ${muted.slab}`);
  assert.equal(backdropDefaults("dark-emissive").baseFrom, "#070b14");
});

// The palette presets (ah2-276 R6): each preset block of presets.css over its mode's ui.css tokens. Text at 4.5:1 on every surface and
// tint, marks at 3:1, the accent fill's label at 4.5:1, the states apart (gate 2, and gate3Preset under deuteranopia), and every role pair at
// least as far apart as the house has it (or ΔE 20).
const presetsCss = readFileSync(new URL("../../web/ui/presets.css", import.meta.url), "utf8");
const { PRESETS } = await import("../../web/ui/presets.js");
const presetBlocks = Object.fromEntries([...presetsCss.matchAll(/:root\[data-preset="([\w-]+)"\][^{]*\{([^}]*)\}/g)].map((m) => [m[1], declarations(m[2])]));

test("every preset the menu offers has a block in presets.css, and every block is offered", () => {
  assert.deepEqual(Object.keys(presetBlocks).sort(), PRESETS.filter((p) => p.file).map((p) => p.id).sort());
});

const rolePairsOk = (vars, base) => {
  const of = (v) => Object.fromEntries(ROLES.filter((r) => r !== "orchestrator").map((r) => [r, resolve(v, `var(--role-${r})`)]));
  const now = deltaMatrix(of(vars));
  const house = deltaMatrix(of(base));
  return Object.entries(now).filter(([p, d]) => d < Math.min(20, house[p] - 1)).map(([p, d]) => `${p} ΔE ${d} (house ${house[p]})`);
};

for (const p of PRESETS.filter((x) => x.file)) {
  const base = p.mode === "dark" ? dark : lightAttr;
  const vars = { ...base, ...presetBlocks[p.id] };
  test(`preset ${p.id} (${p.mode}): text 4.5:1 on every surface and tint, marks 3:1, the accent label 4.5:1`, () => {
    const surfaces = ["--bg", "--bar", "--side", "--control", "--row-hi", "--row-sel", "--kb-card", "--tool-bg", "--brief-bg", ...TINTS.map((t) => `--tint-${t}`)];
    const texts = ["--ink", "--ink-2", "--muted", "--dim", "--accent", "--working", "--review", "--attn", "--err-ink", "--ok", ...ROLES.map((r) => `--role-${r}`)];
    const fails = [];
    for (const s of surfaces) for (const t of texts) {
      const r = contrast(resolve(vars, `var(${t})`), resolve(vars, `var(${s})`));
      if (r < 4.5) fails.push(`${t} on ${s} ${r.toFixed(2)}`);
    }
    for (const s of ["--bg", "--side"]) for (const t of MARKS) {
      const r = contrast(resolve(vars, `var(${t})`), resolve(vars, `var(${s})`));
      if (r < 3) fails.push(`${t} on ${s} ${r.toFixed(2)}`);
    }
    const label = contrast(resolve(vars, "var(--on-accent)"), resolve(vars, "var(--accent)"));
    if (label < 4.5) fails.push(`--on-accent on --accent ${label.toFixed(2)}`);
    assert.deepEqual(fails, []);
  });
  test(`preset ${p.id} (${p.mode}): states and roles stay apart`, () => {
    const colours = houseStates(vars);
    assert.deepEqual([...gate2(colours), ...gate3Preset(colours, houseStates(base)), ...rolePairsOk(vars, base)], [], JSON.stringify({ deltaE: deltaMatrix(colours), deuteranopia: deltaMatrix(colours, "deuteranopia") }));
  });
}
