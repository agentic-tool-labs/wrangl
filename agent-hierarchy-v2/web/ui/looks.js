import { contrast, hex, luminance, over } from "./color.js";

// The looks registry (ah-ui-looks §2-§5): the built-ins, the active choice and saved looks in the daemon's looks.json, and the one
// apply() the app root and the editor preview share. localStorage `ah-optics` is only read: it is what a browser migrates from, and
// the store of a source with no looks route (the mock). Only this module knows the control -> CSS formulas (ported from the look lab's
// updateOpticalParams, 1075-1105); looks.css reads the variables it sets and does nothing without [data-look-mode].

export const KEY = "ah-optics";
export const TECHNIQUES = ["shadow", "dual"];
export const ACCENT_IDS = ["obsidian", "cyan", "amber", "emerald"];

// The lab's controls and their slider ranges [min, max, step].
export const RANGES = {
  sliderHaloBlur: [1, 16, 0.5],
  sliderHaloIntensity: [20, 200, 5],
  sliderGlassBlur: [0, 32, 1],
  sliderGlassOpacity: [0, 92, 1],
  glassTexture: [0, 100, 1],
};
// The optional controls: card fill, card border and lane line strength. A look without them takes its mode's recipe.
export const CARD_RANGES = { sliderBoxOpacity: [0, 100, 1], sliderBoxBorder: [0, 100, 1], sliderLineStrength: [0, 100, 1] };
// Under this glass opacity the surface's blur and saturation fade with its fill, so at 0% it shows what is behind it, sharp.
const GLASS_CLEAR_BELOW = 35;

// The lab's swatch table (lab CSS 36-80), as r,g,b triples.
const ACCENTS = {
  obsidian: { inkCore: [5, 7, 11], inkBloom: [5, 7, 11], inkTint: [15, 23, 42], emissive: [226, 232, 240], hot: [255, 255, 255], lightCore: [5, 7, 11], lightBloom: [148, 163, 184] },
  cyan: { inkCore: [4, 13, 26], inkBloom: [7, 24, 46], inkTint: [8, 47, 73], emissive: [56, 189, 248], hot: [186, 230, 253], lightCore: [8, 47, 73], lightBloom: [56, 189, 248] },
  amber: { inkCore: [15, 7, 2], inkBloom: [31, 14, 3], inkTint: [69, 26, 3], emissive: [245, 158, 11], hot: [254, 240, 138], lightCore: [69, 26, 3], lightBloom: [245, 158, 11] },
  emerald: { inkCore: [2, 16, 12], inkBloom: [3, 28, 21], inkTint: [2, 44, 34], emissive: [16, 185, 129], hot: [167, 243, 208], lightCore: [2, 44, 34], lightBloom: [16, 185, 129] },
};

// Each mode's recipe (lab CSS 228-515): the heading ink, the hairline's alpha (.hairline-box), the fill
// of a box inside the glass (.hairline-box background), the three shadow stops' colours, and the stops' blur and alpha factors (k1, k3,
// a2 for text), and the Dual ghosts' fill, stroke and extra stroke width (lab CSS 285-289, 355-358, 419-423).
const MODES = {
  "light-ink": { theme: "light", recipe: (a) => ({ core: a.inkCore, hair: 0.48, box: [255, 255, 255, 0.36], s1: a.inkBloom, s2: a.inkTint, k1: 0.38, k3: 1.65, a2: 0.88, ghost: a.inkBloom, ghostEdge: a.inkTint, ghostSpread: 0.9 }) },
  "light-emissive": { theme: "light", recipe: (a) => ({ core: a.lightCore, hair: 0.7, box: [255, 255, 255, 0.36], s1: a.lightBloom, s2: a.lightBloom, k1: 0.4, k3: 1.75, a2: 1, ghost: a.lightBloom, ghostEdge: a.lightBloom, ghostSpread: 0.9 }) },
  "dark-emissive": { theme: "dark", recipe: (a) => ({ core: [248, 250, 252], hair: 0.36, box: [15, 23, 42, 0.35], s1: a.hot, s2: a.emissive, k1: 0.4, k3: 1.75, a2: 1, ghost: a.emissive, ghostEdge: a.hot, ghostSpread: 0.7 }) },
};

// A look's backdrop: the lab's Backdrop group (look-lab 1253-1330), ported. Pattern layers over three wash blobs over a base gradient,
// and three static orbs (the lab's "accent glow"). The built-ins paint the lab's defaults for their mode; a custom look carries its own.
export const PATTERNS = ["grid", "dots", "diagonal", "blueprint", "mesh", "noise", "plain"];
export const PATTERN_NAMES = { grid: "Grid", dots: "Dots", diagonal: "Diagonal lines", blueprint: "Blueprint", mesh: "Mesh", noise: "Noise", plain: "Plain" };
export const BD_COLORS = ["patternColor", "baseFrom", "baseTo", "washA", "washB", "washC", "glowA", "glowB", "glowC"];
export const BD_RANGES = { patternOpacity: [0, 100, 0.5], patternSize: [8, 96, 1], baseAngle: [0, 360, 1], washIntensity: [0, 200, 1], glowIntensity: [0, 200, 1] };
const WASH = [["18% 22%", 48], ["82% 26%", 46], ["75% 78%", 52]];
const WEIGHTS = { light: { wash: [0.55, 0.45, 0.5], glow: [0.65, 0.55, 0.6] }, dark: { wash: [0.3, 0.26, 0.2], glow: [0.35, 0.35, 0.2] } };

// The owner's tuned backdrops, one per mode.
export function backdropDefaults(mode) {
  if (mode === "dark-emissive") {
    return { pattern: "grid", patternColor: "#ffffff", patternOpacity: 5.5, patternSize: 28, baseFrom: "#070b14", baseTo: "#070b14", baseAngle: 135,
      washA: "#38bdf8", washB: "#6366f1", washC: "#22d3ee", washIntensity: 0, glow: true, glowA: "#0ea5e9", glowB: "#6366f1", glowC: "#22d3ee", glowIntensity: 100 };
  }
  const [patternOpacity, baseFrom, baseTo] = mode === "light-emissive" ? [24.5, "#648cb4", "#39577f"] : [16, "#bcc9d6", "#b6bdc6"];
  return { pattern: "grid", patternColor: "#0f172a", patternOpacity, patternSize: 28, baseFrom, baseTo, baseAngle: 135,
    washA: "#7dd3fc", washB: "#fdba8c", washC: "#c4b5fd", washIntensity: 100, glow: true, glowA: "#7dd3fc", glowB: "#c4b5fd", glowC: "#fde68a", glowIntensity: 100 };
}

// A static tile at `pct` percent. The CSP is default-src 'self', so a background cannot be a data: image and an opacity cannot be baked
// into one; -webkit-cross-fade fades the file itself. ponytail: Chrome and Safari only; without it the tile is simply not drawn.
const fade = (url, pct) => `-webkit-cross-fade(linear-gradient(transparent, transparent), url("${url}"), ${pct}%)`;

const alpha = (rgb, a) => `rgba(${rgb.map((c) => Math.round(c)).join(", ")}, ${+a.toFixed(3)})`;

// The lab's patternLayers: [image, size] pairs.
function patternLayers(b) {
  const rgb = hex(b.patternColor);
  const c = alpha(rgb, b.patternOpacity / 100);
  const s = b.patternSize;
  const tile = (n) => `${n}px ${n}px`;
  switch (b.pattern) {
    case "grid": return [[`linear-gradient(to right, ${c} 1px, transparent 1px)`, tile(s)], [`linear-gradient(to bottom, ${c} 1px, transparent 1px)`, tile(s)]];
    case "dots": return [[`radial-gradient(circle, ${c} 1.5px, transparent 2px)`, tile(s)]];
    case "diagonal": return [[`repeating-linear-gradient(45deg, ${c} 0px, ${c} 1px, transparent 1px, transparent ${s / 2}px)`, "auto"]];
    case "blueprint": {
      const minor = alpha(rgb, b.patternOpacity / 200);
      return [[`linear-gradient(to right, ${c} 1px, transparent 1px)`, tile(s * 4)], [`linear-gradient(to bottom, ${c} 1px, transparent 1px)`, tile(s * 4)],
        [`linear-gradient(to right, ${minor} 1px, transparent 1px)`, tile(s)], [`linear-gradient(to bottom, ${minor} 1px, transparent 1px)`, tile(s)]];
    }
    case "mesh": return [[`radial-gradient(circle, ${c} 0%, transparent 70%)`, tile(s * 6)]];
    case "noise": {
      // The CSP allows no data: image, so the grain is one of two static tiles. The tile's alpha row is already the lab's 3x gain.
      // ponytail: dark or light grain by the colour's luminance, not its hue; an img-src data: in the CSP would allow the exact tint.
      const file = luminance(rgb) > 0.4 ? "light" : "dark";
      // The tile's own size: scaled with Pattern size, its grain would coarsen.
      return [[fade(`/ui/noise-${file}.svg`, b.patternOpacity), tile(168)]];
    }
    default: return [];
  }
}

// The same pattern seen through a blur of `blur` px, in gradients: a 1px line keeps its mass as a soft triangle 4 blur wide, and once those
// overlap the pattern is its mean tint. A soft enough grain or mesh reads as nothing or as itself.
function softPatternLayers(b, blur, rgb) {
  const a = b.patternOpacity / 100;
  const s = b.patternSize;
  const w = 2 * blur;
  const c = (x) => alpha(rgb, Math.min(1, x));
  const tile = (n) => `${n}px ${n}px`;
  const flat = (x) => [`linear-gradient(${c(x)}, ${c(x)})`, "auto"];
  const line = (dir, p, x) => (2 * w >= p ? flat(x / p) : [`linear-gradient(to ${dir}, ${c(x / w)} 0, transparent ${w}px, transparent ${p - w}px, ${c(x / w)} ${p}px)`, tile(p)]);
  switch (b.pattern) {
    case "grid": return [line("right", s, a), line("bottom", s, a)];
    case "dots": return [2 * w >= s ? flat((a * 9.6) / (s * s)) : [`radial-gradient(circle, ${c((a * 7.2) / (w * w))} 0, transparent ${w}px)`, tile(s)]];
    case "diagonal": {
      const p = s / 2;
      return [2 * w >= p ? flat(a / p) : [`repeating-linear-gradient(45deg, ${c(a / w)} 0, transparent ${w}px, transparent ${p - w}px, ${c(a / w)} ${p}px)`, "auto"]];
    }
    case "blueprint": return [line("right", s * 4, a), line("bottom", s * 4, a), line("right", s, a / 2), line("bottom", s, a / 2)];
    case "mesh": return patternLayers({ ...b, patternColor: `#${rgb.map((v) => Math.round(v).toString(16).padStart(2, "0")).join("")}` });
    default: return [];
  }
}

const profile = (accent, glassColor, glassTexture, sliderGlassBlur, sliderGlassOpacity, sliderHaloBlur, sliderHaloIntensity, boxColor, sliderBoxOpacity, sliderBoxBorder, sliderLineStrength) =>
  ({ accent, glassColor, glassTexture, sliderGlassBlur, sliderGlassOpacity, sliderHaloBlur, sliderHaloIntensity, boxColor, sliderBoxOpacity, sliderBoxBorder, sliderLineStrength });

// The UI language: the named roles a look can restyle, and everywhere each appears reads its --type-<id>-font, -weight, -style and -size, with
// that rule's own value as the fallback. A look sets only the knobs it changed, so a look that changed nothing draws exactly as it did. `font`,
// `bold`, `italic` and `size` here are what the Looks page shows for a knob left alone.
export const ROLE_FONTS = {
  system: ["System", "var(--font-ui)"],
  humanist: ["Humanist", '"Avenir Next", "Segoe UI", Candara, sans-serif'],
  serif: ["Serif", 'ui-serif, "New York", Georgia, serif'],
  rounded: ["Rounded", 'ui-rounded, "SF Pro Rounded", system-ui, sans-serif'],
  mono: ["Monospace", "var(--font-mono)"],
};
export const ROLE_SIZE = [10, 24, 1];
export const ROLES = Object.freeze([
  { id: "card-title", name: "Card title", where: "Board cards, member card titles and headings, and detail cards", font: "system", bold: true, italic: false, size: 13 },
  { id: "column-heading", name: "Column heading", where: "Board and Sitrep column heads, not a folded column's strip", font: "system", bold: true, italic: false, size: 13 },
  { id: "panel-heading", name: "Panel heading", where: "Panel bars, Live feed and the Teams rail", font: "system", bold: true, italic: false, size: 13 },
  { id: "table-heading", name: "Table heading", where: "Teams, Backlog, Checklist, Questions, Plans item and Overview tables", font: "system", bold: true, italic: false, size: 11 },
  { id: "tab", name: "Buttons and tabs", where: "Tabs, action buttons and the header's switchers, not icon buttons or menus", font: "system", bold: false, italic: false, size: 12 },
  { id: "details", name: "Card details", where: "The small line under a card title", font: "system", bold: false, italic: false, size: 11 },
  { id: "code", name: "IDs and code", where: "Branches, refs and ids on the Board, Plans, Backlog, Questions, Sitrep and the Live feed, not the transcript, ages or counts", font: "mono", bold: false, italic: false, size: 12 },
]);
export const ROLE_TOKENS = ROLES.flatMap((r) => ["font", "weight", "style", "size"].map((k) => `--type-${r.id}-${k}`));

// The variables for the knobs a look changed.
const roleVars = (look) => {
  const vars = {};
  for (const { id } of ROLES) {
    const t = look.type?.[id];
    if (!t) continue;
    if (ROLE_FONTS[t.font]) vars[`--type-${id}-font`] = ROLE_FONTS[t.font][1];
    if (typeof t.bold === "boolean") vars[`--type-${id}-weight`] = t.bold ? "600" : "400";
    if (typeof t.italic === "boolean") vars[`--type-${id}-style`] = t.italic ? "italic" : "normal";
    if (Number.isFinite(t.size)) vars[`--type-${id}-size`] = `${t.size}px`;
  }
  return vars;
};

export const HOUSE = Object.freeze({ id: "house", name: "House", theme: null, builtin: true });

// The owner's tuned looks (§2, v2 retune). `ink` overrides the text tokens (4.5:1) and marks (3:1) a look needs on its panes (§4, Q3
// extended to marks for built-ins); the glass values are his.
export const BUILTINS = Object.freeze([
  HOUSE,
  {
    id: "light-inky", name: "Light Inky", theme: "light", mode: "light-ink", builtin: true,
    profile: "dual",
    profiles: { shadow: profile("obsidian", "#ffffff", 0, 18, 72, 5.5, 135, "#ffffff", 36, 48, 100), dual: profile("cyan", "#ffffff", 27, 18, 6, 14, 100, "#ffffff", 52, 48, 25) },
    ink: { "--ink": "#05070b", "--ink-2": "#3d444d", "--muted": "#414851", "--dim": "#44484e", "--accent-ink": "#064590", "--working-mark": "#095ec4", "--blocked-mark": "#845800", "--brief-mark": "#845800", "--report-mark": "#845800", "--idle-mark": "#5c626a" },
  },
  {
    id: "light-emissive", name: "Light Emissive", theme: "light", mode: "light-emissive", builtin: true,
    profile: "dual",
    profiles: { shadow: profile("cyan", "#ffffff", 0, 18, 72, 5.5, 135, "#ffffff", 36, 70, 100), dual: profile("cyan", "#ffffff", 25, 10, 6, 14, 50, "#ffffff", 52, 70, 100) },
    ink: { "--ink": "#05070b", "--ink-2": "#3d444d", "--muted": "#404750", "--dim": "#42474c", "--accent-ink": "#06448e", "--working-mark": "#085cc0", "--blocked-mark": "#825700", "--brief-mark": "#825700", "--report-mark": "#825700", "--idle-mark": "#5b6169" },
  },
  {
    id: "dark-emissive", name: "Dark Emissive", theme: "dark", mode: "dark-emissive", builtin: true,
    profile: "dual",
    profiles: { shadow: profile("obsidian", "#0a0f1c", 0, 18, 72, 5.5, 135, "#0f172a", 35, 36, 100), dual: profile("amber", "#0a0f1c", 8, 18, 6, 16, 45, "#111c36", 36, 36, 63) },
    ink: { "--muted": "#a4abb3", "--dim": "#a5abb2", "--accent-ink": "#6cacff", "--idle-mark": "#828a94" },
  },
].map((l) => Object.freeze(l)));

export const DEFAULTS = Object.freeze({ light: "light-inky", dark: "dark-emissive" });

// The lab's feTurbulence frost (lab CSS 170-182, web/ui/frost.svg): overlay-blended on the glass, inside the glass's own group. FROST
// is the tile as it renders, 0 to 1: its mean grey and the spread contrast allows for (measured from the lab's card).
const frost = (opacity) => (opacity <= 0 ? "none" : fade("/ui/frost.svg", opacity * 100));
const FROST = { mean: 0.73, spread: 0.1 };

// One point of a frosted pane over `stage`, as looks.css paints it: the grain `s` at opacity `t` over glass colour `g` at alpha `a` over the stage.
// ponytail: plain alpha, not the lab's overlay inside the glass's own group, which needs a second painted layer per surface; within 2 levels at
// the built-ins' 6% glass, and off by up to 40 levels at high glass with full grain. Add that layer if a look there must match the lab.
const frosted = (g, a, t, s, stage) => g.map((c, i) => {
  const under = a * (c / 255) + (1 - a) * (stage[i] / 255);
  return 255 * (t * s + (1 - t) * under);
});

const rgbOf = (c) => (typeof c === "string" ? hex(c) : c);
const HEX = /^#[0-9a-f]{6}$/i;
// An unknown accent or a malformed glass colour falls back to obsidian and the theme default's glass, everywhere.
const accentOf = (c) => ACCENTS[c.accent] ?? ACCENTS.obsidian;
const defaultGlass = (mode) => BUILTINS.find((l) => l.id === DEFAULTS[MODES[mode].theme]).profiles.shadow.glassColor;
const glassOf = (look, c) => hex(HEX.test(c.glassColor ?? "") ? c.glassColor : defaultGlass(look.mode));
const csv = (rgb) => rgb.map((c) => Math.round(c)).join(", ");
// The profile a look draws with: its saved one, as the built-ins say Dual; a look with none reads as Shadow.
export const profileOf = (look) => (TECHNIQUES.includes(look?.profile) ? look.profile : "shadow");
const controlsOf = (look, technique) => look.profiles[technique] ?? look.profiles.shadow;
const headingOf = (look, c) => (look.ink?.["--ink-strong"] ? hex(look.ink["--ink-strong"]) : MODES[look.mode].recipe(accentOf(c)).core);

// A surface's backdrop saturation (the lab's glass cards), per mode. The filter is linear, so it can go on each colour of the stage's layers.
const saturateOf = (mode) => (mode === "dark-emissive" ? 1.6 : 1.8);
const clearOf = (c) => Math.min(1, c.sliderGlassOpacity / GLASS_CLEAR_BELOW);
const viewSaturateOf = (look, c) => 1 + (saturateOf(look.mode) - 1) * clearOf(c);
const saturated = (rgb, s) => [[0.213 + 0.787 * s, 0.715 - 0.715 * s, 0.072 - 0.072 * s], [0.213 - 0.213 * s, 0.715 + 0.285 * s, 0.072 - 0.072 * s], [0.213 - 0.213 * s, 0.715 - 0.715 * s, 0.072 + 0.928 * s]]
  .map(([r, g, b]) => Math.min(255, Math.max(0, r * rgb[0] + g * rgb[1] + b * rgb[2])));

// The three orbs, [x, y, radius in px], and their own blur: discs under a Gaussian, which a radial gradient's stops draw without a filter.
const ORBS = [["168px", "160px", 128], ["calc(100% - 184px)", "calc(100% - 176px)", 144], ["calc(75% - 96px)", "calc(33.333% + 96px)", 96]];
const ORB_BLUR = 40;
const ORB_STOPS = [[-2, 0.977], [-1, 0.841], [0, 0.5], [1, 0.159], [2, 0.023], [3, 0]];
const orbLayer = ([x, y, r], rgba, blur) => {
  const s = Math.hypot(ORB_BLUR, blur);
  const peak = rgba[3] * (1 - Math.exp((-r * r) / (2 * s * s)));
  const stops = ORB_STOPS.map(([k, cover]) => `${alpha(rgba.slice(0, 3), peak * cover)} ${Math.max(0, r + k * s).toFixed(1)}px`);
  return [`radial-gradient(circle ${(r + 3 * s).toFixed(1)}px at ${x} ${y}, ${stops.join(", ")})`, "auto"];
};

// The stage a look paints: `layers` ([image, size], top first), `view(blur, saturate)` the same stage as a glass surface sees it, and for
// contrast the base stops, the blobs and the orbs as [r, g, b, a].
const stageOf = (look) => {
  const b = look.backdrop ?? backdropDefaults(look.mode);
  const w = WEIGHTS[MODES[look.mode].theme];
  const blobs = WASH.map(([at, stop], i) => [at, [...hex(b[`wash${"ABC"[i]}`]), (w.wash[i] * b.washIntensity) / 100], stop]);
  const orbs = b.glow ? WASH.map((_, i) => [...hex(b[`glow${"ABC"[i]}`]), Math.min(1, (w.glow[i] * b.glowIntensity) / 100)]) : [];
  const paint = (blur, s) => {
    const t = (c) => [...saturated(c.slice(0, 3), s), ...c.slice(3)];
    const rgbHex = (c) => `#${t(hex(c)).map((v) => Math.round(v).toString(16).padStart(2, "0")).join("")}`;
    return [
      ...(blur >= 0.5 ? softPatternLayers(b, blur, t(hex(b.patternColor))) : patternLayers(b)),
      ...orbs.map((o, i) => orbLayer(ORBS[i], t(o), blur)),
      ...blobs.map(([at, c, stop]) => [`radial-gradient(circle at ${at}, ${alpha(t(c).slice(0, 3), c[3])} 0%, transparent ${stop}%)`, "auto"]),
      [`linear-gradient(${b.baseAngle}deg, ${rgbHex(b.baseFrom)} 0%, ${rgbHex(b.baseTo)} 100%)`, "auto"],
    ];
  };
  return {
    layers: paint(0, 1),
    view: paint,
    base: [hex(b.baseFrom), hex(b.baseTo)],
    blobs: blobs.map((x) => [...x[1].slice(0, 3), Math.min(1, x[1][3])]),
    orbs,
  };
};

// The layer 2 fill as [r, g, b, a]: the look's own, else its mode's.
export const boxOf = (look, c) => {
  const box = MODES[look.mode].recipe(accentOf(c)).box;
  return [...(HEX.test(c.boxColor ?? "") ? hex(c.boxColor) : box.slice(0, 3)), Number.isFinite(c.sliderBoxOpacity) ? c.sliderBoxOpacity / 100 : box[3]];
};

// Every variable apply() sets, from one look and technique.
export function lookVars(look, technique) {
  const c = controlsOf(look, technique);
  const a = accentOf(c);
  const r = MODES[look.mode].recipe(a);
  const hInt = c.sliderHaloIntensity;
  const st = stageOf(look);
  const viewBlur = c.sliderGlassBlur * clearOf(c);
  const view = st.view(viewBlur, viewSaturateOf(look, c));
  const box = boxOf(look, c);
  const vars = {
    "--halo-blur": `${c.sliderHaloBlur}px`,
    "--halo-blur-tight": `${Math.max(1.2, c.sliderHaloBlur * 0.42).toFixed(1)}px`,
    "--halo-opacity": Math.min(1, hInt / 100).toFixed(2),
    "--look-title-ghost": String(+Math.min(1, 3 / c.sliderHaloBlur).toFixed(3)),
    "--halo-overdrive-opacity": (hInt > 100 ? ((hInt - 100) / 100) * 0.95 : 0).toFixed(2),
    "--halo-stroke": `${(0.55 + (hInt / 100) * 0.85).toFixed(2)}px`,
    "--glass-blur": `${c.sliderGlassBlur}px`,
    "--glass-opacity": String(c.sliderGlassOpacity / 100),
    "--glass-rgb": csv(glassOf(look, c)),
    "--glass-texture": String(c.glassTexture / 100),
    "--look-core": `rgb(${csv(headingOf(look, c))})`,
    "--look-hair": String(Number.isFinite(c.sliderBoxBorder) ? c.sliderBoxBorder / 100 : r.hair),
    "--look-line": String(Number.isFinite(c.sliderLineStrength) ? c.sliderLineStrength / 100 : 1),
    "--look-box": alpha(box.slice(0, 3), box[3]),
    "--look-box-under": String(+(1 - box[3]).toFixed(3)),
    "--look-s1-rgb": csv(r.s1),
    "--look-s2-rgb": csv(r.s2),
    "--look-k1": String(r.k1),
    "--look-k3": String(r.k3),
    "--look-a2": String(r.a2),
    "--look-ghost-rgb": csv(r.ghost),
    "--look-ghost-edge-rgb": csv(r.ghostEdge),
    "--look-ghost-spread": `${r.ghostSpread}px`,
    "--look-frost": frost(c.glassTexture / 100),
    "--look-edge-rgb": csv(MODES[look.mode].theme === "dark" ? a.hot : [255, 255, 255]),
    "--look-pane": `rgb(${csv(paneSolid(look, technique))})`,
    "--look-stage": st.layers.map((l) => l[0]).join(", "),
    "--look-stage-size": st.layers.map((l) => l[1]).join(", "),
    "--look-view": view.map((l) => l[0]).join(", "),
    "--look-view-size": view.map((l) => l[1]).join(", "),
    "--look-saturate": `${saturateOf(look.mode) * 100}%`,
    "--look-view-blur": `${+viewBlur.toFixed(2)}px`,
    "--look-view-saturate": `${+(viewSaturateOf(look, c) * 100).toFixed(1)}%`,
  };
  for (const [token, value] of Object.entries(look.ink ?? {})) vars[token] = value;
  return { ...vars, ...roleVars(look) };
}

export const TEXT_TOKENS = ["--ink", "--ink-2", "--muted", "--dim", "--ink-strong", "--accent-ink"];
export const MARK_TOKENS = ["--working-mark", "--blocked-mark", "--brief-mark", "--report-mark", "--idle-mark"];
// What a profile changes on screen, less the ghosts: "Draw the graph with Shadow" gives the Graph stage these from the Shadow profile, as
// --look-graph-*, and looks.css hands them back under their own names there.
export const GRAPH_VARS = ["--halo-blur", "--halo-blur-tight", "--halo-opacity", "--halo-overdrive-opacity", "--halo-stroke", "--glass-blur", "--glass-opacity", "--glass-rgb",
  "--glass-texture", "--look-core", "--look-s1-rgb", "--look-s2-rgb", "--look-frost", "--look-edge-rgb", "--look-pane", "--look-box", "--look-box-under", "--look-title-ghost", "--look-hair", "--look-line", "--look-view", "--look-view-size", "--look-view-blur", "--look-view-saturate"];
// The variables apply() set on each target, so a switch removes exactly those, a custom look's own `ink` keys included.
const setOn = new WeakMap();
const ATTRS = ["data-look-mode", "data-look-accent", "data-look-tech", "data-look-graph"];

// The stage layer: one fixed element, first in the host.
function stageIn(target, make) {
  const doc = target.ownerDocument ?? globalThis.document;
  const host = target === doc?.documentElement ? doc.body : target;
  if (!host) return;
  let stage = [...(host.children ?? [])].find((e) => e.classList?.contains("look-stage"));
  if (!make) {
    stage?.remove();
    return;
  }
  if (stage) return;
  stage = doc.createElement("div");
  stage.className = "look-stage";
  stage.setAttribute("aria-hidden", "true");
  host.prepend(stage);
}

// Apply a look to `target` (document.documentElement for the app; the preview container in the editor). House removes every look
// variable and attribute and the stage, so the page is today's UI.
export function apply(look, technique, target) {
  if (!target) return;
  for (const name of setOn.get(target) ?? []) target.style.removeProperty(name);
  setOn.delete(target);
  if (!look || look.id === HOUSE.id || !look.profiles) {
    for (const a of ATTRS) target.removeAttribute(a);
    stageIn(target, false);
    return;
  }
  const tech = TECHNIQUES.includes(technique) ? technique : "shadow";
  const vars = lookVars(look, tech);
  const graphShadow = tech === "dual" && look.graphShadow === true;
  if (graphShadow) {
    const shadow = lookVars(look, "shadow");
    for (const k of GRAPH_VARS) vars[`--look-graph-${k.slice(2)}`] = shadow[k];
  }
  for (const [k, v] of Object.entries(vars)) target.style.setProperty(k, v);
  setOn.set(target, Object.keys(vars));
  target.setAttribute("data-look-mode", look.mode);
  target.setAttribute("data-look-accent", ACCENTS[controlsOf(look, tech).accent] ? controlsOf(look, tech).accent : "obsidian");
  target.setAttribute("data-look-tech", tech);
  if (graphShadow) target.setAttribute("data-look-graph", "shadow");
  else target.removeAttribute("data-look-graph");
  stageIn(target, true);
}

// ---- Contrast (§4) ----

// The stage points a pane can sit over: each base stop, each blob and orb over it, and each orb over each blob over it.
export function stagePoints(look, technique) {
  const st = stageOf(look);
  const lay = (c, bg) => over(c.slice(0, 3), bg, c[3]);
  const points = [];
  for (const base of st.base) {
    points.push(base);
    for (const blob of st.blobs) points.push(lay(blob, base));
    for (const orb of st.orbs) {
      points.push(lay(orb, base));
      for (const blob of st.blobs) points.push(lay(orb, lay(blob, base)));
    }
  }
  return points;
}

// The surface colours text sits on: the frosted glass over every stage point at the grain's two extremes (a pane), and the mode's box
// fill over that pane, once or twice (a card on the slab, a card on a pane on the slab).
export function surfacesOf(look, technique) {
  const c = controlsOf(look, technique);
  const g = glassOf(look, c);
  const box = boxOf(look, c);
  const points = stagePoints(look, technique).map((p) => saturated(p, viewSaturateOf(look, c)));
  const slab = [FROST.mean - FROST.spread, FROST.mean + FROST.spread].flatMap((s) => points.map((p) => frosted(g, c.sliderGlassOpacity / 100, c.glassTexture / 100, s, p)));
  const card = slab.map((p) => over(box.slice(0, 3), p, box[3]));
  const pane = paneSolid(look, technique);
  const overlays = points.map((p) => over(box.slice(0, 3), over(pane, p, 0.92), box[3]));
  return { slab, cards: [...card, ...card.map((p) => over(box.slice(0, 3), p, box[3]))], overlays };
}
export const panes = (look, technique) => { const { slab, cards } = surfacesOf(look, technique); return [...slab, ...cards]; };

// The frosted glass over the stage's first base stop: the opaque stand-in sticky bars and overlays use.
export function paneSolid(look, technique) {
  const c = controlsOf(look, technique);
  return frosted(glassOf(look, c), c.sliderGlassOpacity / 100, c.glassTexture / 100, FROST.mean, stageOf(look).base[0]);
}

// The worst ratio of each text colour (4.5:1) and mark (3:1) on the look's cards and overlays, which is what `ok` gates, and on the bare slab, reported as `slab`:
// a light look at low glass can have no colour that passes there (the owner's call, until the chrome has its own design). `colours` maps a token to [r, g, b] as the theme
// resolves it; the look's own `ink` overrides and its heading ink are applied here. Returns [{token, rgb, ratio, overlay, slab, worst, where, need, ok}]; `worst` and `where` are the gated low and its surface.
export function contrastOf(look, technique, texts, marks = {}) {
  const { slab, cards, overlays } = surfacesOf(look, technique);
  const c = controlsOf(look, technique);
  const own = (set) => Object.fromEntries(Object.entries(set).map(([k, rgb]) => [k, look.ink?.[k] ? rgbOf(look.ink[k]) : rgb]));
  const all = [
    ...Object.entries({ ...own(texts), "--ink-strong": headingOf(look, c) }).map(([t, rgb]) => [t, rgb, 4.5]),
    ...Object.entries(own(marks)).map(([t, rgb]) => [t, rgb, 3]),
  ];
  return all.map(([token, rgb, need]) => {
    const worst = (set) => Math.round(Math.min(...set.map((s) => contrast(rgb, s))) * 100) / 100;
    const ratio = worst(cards);
    const overlay = worst(overlays);
    const low = Math.min(ratio, overlay);
    return { token, rgb, ratio, overlay, slab: worst(slab), worst: low, where: overlay < ratio ? "menus" : "cards", need, ok: low >= need };
  });
}

// ---- Choice and storage (§5) ----

// The page's storage, or null when the browser blocks it (the getter itself throws then).
const storeOf = (win = globalThis) => {
  try {
    return win.localStorage ?? null;
  } catch {
    return null;
  }
};

const clamp = (v, [lo, hi]) => Math.min(hi, Math.max(lo, v));
const num = (v, range, fallback) => (Number.isFinite(v) ? clamp(v, range) : fallback);
const colour = (v, fallback) => (HEX.test(v ?? "") ? v.toLowerCase() : fallback);

// Untrusted controls (storage, an import): an unknown accent, a malformed colour or an out-of-range number falls back or clamps;
// never throws.
function clean(look, c) {
  const d = BUILTINS.find((l) => l.id === DEFAULTS[MODES[look.mode].theme]).profiles.shadow;
  const o = c !== null && typeof c === "object" ? c : {};
  const out = { accent: ACCENTS[o.accent] ? o.accent : "obsidian", glassColor: colour(o.glassColor, d.glassColor) };
  for (const [k, range] of Object.entries(RANGES)) out[k] = num(o[k], range, d[k]);
  if (HEX.test(o.boxColor ?? "")) out.boxColor = o.boxColor.toLowerCase();
  for (const [k, range] of Object.entries(CARD_RANGES)) if (Number.isFinite(o[k])) out[k] = clamp(o[k], range);
  return out;
}

// The lab's normalizeBackdrop: anything missing or bad takes the mode's default.
export function cleanBackdrop(mode, b) {
  const d = backdropDefaults(mode);
  const o = b !== null && typeof b === "object" ? b : {};
  const out = { ...d, pattern: PATTERNS.includes(o.pattern) ? o.pattern : d.pattern, glow: typeof o.glow === "boolean" ? o.glow : d.glow };
  for (const k of BD_COLORS) out[k] = colour(o[k], d[k]);
  for (const [k, range] of Object.entries(BD_RANGES)) out[k] = num(o[k], range, d[k]);
  return out;
}

// A look may override only text and mark tokens, with a #rrggbb colour.
const cleanInk = (ink) => Object.fromEntries(Object.entries(ink !== null && typeof ink === "object" ? ink : {})
  .filter(([k, v]) => (TEXT_TOKENS.includes(k) || MARK_TOKENS.includes(k)) && HEX.test(v ?? "")));

// Untrusted role settings: unknown roles and knobs go, a size clamps. The result is empty for a look that changed nothing.
export function cleanType(type) {
  const out = {};
  for (const { id } of ROLES) {
    const t = type?.[id];
    if (t === null || typeof t !== "object") continue;
    const knobs = {};
    if (typeof t.font === "string" && Object.hasOwn(ROLE_FONTS, t.font)) knobs.font = t.font;
    if (typeof t.bold === "boolean") knobs.bold = t.bold;
    if (typeof t.italic === "boolean") knobs.italic = t.italic;
    if (Number.isFinite(t.size)) knobs.size = clamp(Math.round(t.size), ROLE_SIZE);
    if (Object.keys(knobs).length > 0) out[id] = knobs;
  }
  return out;
}
// A look with no role settings has no `type` at all, so a look saved before there were roles stays byte for byte what the file holds.
const typeField = (type) => {
  const t = cleanType(type);
  return Object.keys(t).length > 0 ? { type: t } : null;
};

const cleanCustom = (l) => ({
  id: l.id, name: typeof l.name === "string" && l.name.trim() ? l.name.trim().slice(0, NAME_MAX) : "Custom look",
  theme: MODES[l.mode].theme, mode: l.mode, builtin: false,
  profile: profileOf(l), graphShadow: l.graphShadow === true,
  profiles: { shadow: clean(l, l.profiles.shadow), dual: clean(l, l.profiles.dual) },
  backdrop: cleanBackdrop(l.mode, l.backdrop), ink: cleanInk(l.ink), ...typeField(l.type),
});

const rawOptics = (storage) => {
  let raw = null;
  try {
    raw = JSON.parse(storage?.getItem(KEY) ?? "null");
  } catch {
    raw = null;
  }
  return raw !== null && typeof raw === "object" && !Array.isArray(raw) ? raw : {};
};

export const readOptics = (storage = storeOf()) => opticsOf(rawOptics(storage));

function opticsOf(o) {
  const active = o.active !== null && typeof o.active === "object" ? o.active : {};
  return {
    active: { light: typeof active.light === "string" ? active.light : DEFAULTS.light, dark: typeof active.dark === "string" ? active.dark : DEFAULTS.dark },
    technique: TECHNIQUES.includes(o.technique) ? o.technique : "shadow",
    custom: Array.isArray(o.custom)
      ? o.custom.filter((l) => l && typeof l.id === "string" && MODES[l.mode] && l.profiles?.shadow && l.profiles?.dual).map(cleanCustom)
      : [],
    skin: o.skin === "house" || o.skin === "glass" ? o.skin : undefined,
  };
}

// A user's choice replaces a harness `skin` seed.
export function writeOptics(state, storage = storeOf()) {
  try {
    storage?.setItem(KEY, JSON.stringify({ active: state.active, technique: state.technique, custom: state.custom }));
  } catch {
    // a blocked or full store keeps the choice for this page only
  }
}

export const looksFor = (state, theme) => [HOUSE, ...BUILTINS.slice(1), ...state.custom].filter((l) => l.theme === null || l.theme === theme);

// The look a theme slot shows: House under a `skin: "house"` seed; else the active id when it exists and fits the slot; else the default.
export function lookFor(state, theme) {
  if (state.skin === "house") return HOUSE;
  const id = state.active[theme];
  const look = looksFor(state, theme).find((l) => l.id === id);
  return look ?? BUILTINS.find((l) => l.id === DEFAULTS[theme]);
}

export function themeOf(doc = globalThis.document, win = globalThis) {
  const set = doc?.documentElement?.getAttribute?.("data-theme");
  if (set === "light" || set === "dark") return set;
  return win.matchMedia?.("(prefers-color-scheme: light)").matches ? "light" : "dark";
}

// The page's choice. A save is held here for the page's life, so a store that is blocked or refuses the write (quota, some private
// modes) still keeps the click, and later re-applies (theme flips) keep it.
const held = new WeakMap();

export const stateOf = (win = globalThis) => held.get(win) ?? readOptics(storeOf(win));

// Apply the current choice to the app root now.
export function applyStored(doc = globalThis.document, win = globalThis) {
  const state = stateOf(win);
  const look = lookFor(state, themeOf(doc, win));
  apply(look, profileOf(look), doc?.documentElement);
  return state;
}

// Boot: apply, then re-apply when data-theme changes or the OS scheme flips. Never throws.
export function bootLooks(doc = globalThis.document, win = globalThis) {
  try {
    applyStored(doc, win);
    void loadLooks(doc, win);
    win.matchMedia?.("(prefers-color-scheme: light)")?.addEventListener?.("change", () => applyStored(doc, win));
    if (typeof win.MutationObserver === "function" && doc?.documentElement) {
      new win.MutationObserver(() => applyStored(doc, win)).observe(doc.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    }
  } catch (e) {
    console.error("looks: boot failed, House stays", e);
  }
}

// Store a new choice (a user action, so it drops a harness seed), hold it for the page, and apply it.
export function save(state, doc = globalThis.document, win = globalThis) {
  const next = { active: state.active, technique: state.technique, custom: state.custom };
  held.set(win, next);
  persist(next, doc, win);
  return applyStored(doc, win);
}

// ---- The looks file: <ah home>/looks.json, through the daemon. ----

const ROUTE = "/ui/v1/looks";
const NOTE = {
  down: "The daemon did not answer, so the built-in looks are showing. Changes here wait in this browser until it does.",
  unsaved: "A look change is not saved yet: the daemon refused it or did not answer. It waits in this browser and goes in when the page next loads, or with your next change.",
  corrupt: (path) => `${path} could not be read, so the built-in looks are showing. The file is left as it is for you to fix (a recent copy may be in looks.json.bak beside it); changes here wait in this browser until then.`,
};
const TIMEOUT_MS = 4000;

// A change waits here, per page, until the daemon confirms it, so a reload or a closed tab loses nothing: { tab: { base, next } }, the
// file's state the change was made from and the state it made. Only ever a queue: the next load replays it onto the file and clears it.
const PENDING = "ah-optics-pending";
const slim = (s) => ({ active: s.active, technique: s.technique, custom: s.custom });

function readPending(storage) {
  try {
    const raw = JSON.parse(storage?.getItem(PENDING) ?? "null");
    return raw !== null && typeof raw === "object" && !Array.isArray(raw) ? raw : {};
  } catch {
    return {};
  }
}

function writePending(storage, pending) {
  try {
    if (Object.keys(pending).length > 0) storage?.setItem(PENDING, JSON.stringify(pending));
    else storage?.removeItem(PENDING);
  } catch {
    // a blocked or full store: the change is still sent, and held for this page's life
  }
}

// `mode`: "none" (no looks route: the mock, or a page not yet loaded), "loading", "file", "down" (a write still tries), "corrupt".
const files = new WeakMap();
const fileOf = (win) => {
  if (!files.has(win)) {
    const tab = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
    files.set(win, { mode: "none", tab, rev: "", base: null, seed: null, madeFrom: null, queued: null, flying: null, note: null, loaded: null, listeners: new Set() });
  }
  return files.get(win);
};

function journal(win, f, next) {
  const storage = storeOf(win);
  writePending(storage, { ...readPending(storage), [f.tab]: { base: slim(f.madeFrom), next: slim(next) } });
}

function confirmed(win, f) {
  const storage = storeOf(win);
  const { [f.tab]: _mine, ...rest } = readPending(storage);
  writePending(storage, rest);
  f.madeFrom = null;
}

const told = (f, note) => {
  f.note = note;
  for (const fn of f.listeners) fn();
};

// What the Looks page says about the file, or null.
export const looksNote = (win = globalThis) => fileOf(win).note;

// `fn` runs when the state or the note changes outside a save (the file arrived, another tab saved). Returns the unsubscribe.
export function onLooks(fn, win = globalThis) {
  const f = fileOf(win);
  f.listeners.add(fn);
  return () => f.listeners.delete(fn);
}

async function ask(win, path, body) {
  const signal = AbortSignal.timeout(TIMEOUT_MS);
  const reply = await win.fetch(path, body === undefined ? { signal } : { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body), signal });
  let doc = null;
  try {
    doc = await reply.json();
  } catch {
    doc = null;
  }
  return { status: reply.status, doc };
}

// The load's end, and the only place a change made before it meets the file: every change waiting (another page's from the journal, and
// this page's own since the load began) is rebuilt on the file's state, and sent only after this. The journal now holds them as one.
// A harness `skin` seed in storage outlives the file's state, until a user's choice drops it as ever.
function adopt(got, doc, win, f) {
  const filed = opticsOf(got.looks ?? {});
  const storage = storeOf(win);
  const { [f.tab]: _mine, ...others } = readPending(storage);
  let state = filed;
  for (const entry of Object.values(others)) {
    if (entry?.base && entry.next) state = rebase(opticsOf(entry.base), opticsOf(entry.next), state);
  }
  if (f.queued) state = rebase(f.seed, f.queued, state);
  f.rev = got.rev ?? "";
  f.base = filed;
  if (f.queued || Object.keys(others).length > 0) {
    f.queued = state;
    f.madeFrom = filed;
    writePending(storage, { [f.tab]: { base: slim(filed), next: slim(state) } });
  } else {
    const skin = rawOptics(storage).skin;
    if (skin === "house" || skin === "glass") state.skin = skin;
  }
  held.set(win, state);
  applyStored(doc, win);
}

// A change made during the load still rebases from what it was made from, so it cannot revert what the file holds.
function builtinsOnly(doc, win, f, note) {
  f.base = f.queued ? f.seed : opticsOf({});
  held.set(win, f.queued ?? f.base);
  applyStored(doc, win);
  told(f, note);
}

// What this browser's ah-optics holds for the file to take, or null. Sent on every load: the daemon's merge remembers what it has taken.
function browserLooks(storage) {
  const raw = rawOptics(storage);
  const { custom } = opticsOf(raw);
  const out = { custom };
  if (raw.active !== null && typeof raw.active === "object") out.active = raw.active;
  if (TECHNIQUES.includes(raw.technique)) out.technique = raw.technique;
  return custom.length > 0 || out.active || out.technique ? out : null;
}

async function pull(doc, win, f) {
  const got = await ask(win, ROUTE);
  if (got.status === 404) {
    f.mode = "none";
    return;
  }
  if (got.status !== 200 || !got.doc) throw new Error(`looks: ${got.status}`);
  if (got.doc.corrupt) {
    f.mode = "corrupt";
    builtinsOnly(doc, win, f, NOTE.corrupt(got.doc.path));
    return;
  }
  let filed = got.doc;
  const local = browserLooks(storeOf(win));
  if (local) {
    const merged = await ask(win, `${ROUTE}/merge`, local);
    if (merged.status === 200 && merged.doc) filed = merged.doc;
    else console.warn(`looks: this browser's looks were not merged (${merged.status}); the next load tries again`);
  }
  adopt(filed, doc, win, f);
  f.mode = "file";
  told(f, null);
}

// Load the file once for this page. Never rejects. A page with no fetch or no address has no route to ask.
export function loadLooks(doc = globalThis.document, win = globalThis) {
  const f = fileOf(win);
  if (f.loaded) return f.loaded;
  if (typeof win.fetch !== "function" || !win.location) {
    f.loaded = Promise.resolve();
    return f.loaded;
  }
  f.seed = stateOf(win);
  f.mode = "loading";
  f.loaded = pull(doc, win, f).catch((e) => {
    console.warn(e);
    f.mode = "down";
    if (!f.base) builtinsOnly(doc, win, f, NOTE.down);
    else told(f, NOTE.down);
  }).then(() => {
    if (f.mode === "none" && f.queued) {
      writeOptics(f.queued, storeOf(win));
      confirmed(win, f);
      f.queued = null;
    }
    if (f.mode === "corrupt") f.queued = null;
    if (f.queued) f.flying ??= flush(doc, win, f);
  });
  return f.loaded;
}

// The journal first, so nothing that follows can lose the change. Nothing is sent while the file is loading or corrupt.
function persist(next, doc, win) {
  const f = fileOf(win);
  if (f.mode === "none") {
    writeOptics(next, storeOf(win));
    return;
  }
  f.madeFrom ??= f.base ?? f.seed;
  journal(win, f, next);
  if (f.mode === "corrupt") return;
  f.queued = next;
  if (f.mode !== "loading") f.flying ??= flush(doc, win, f);
}

// One write at a time, and only the newest: a slider's drag does not queue a write per step.
async function flush(doc, win, f) {
  let stale = 0;
  while (f.queued && stale < 4) {
    const next = f.queued;
    f.queued = null;
    let got;
    try {
      got = await ask(win, ROUTE, { rev: f.rev, looks: { active: next.active, technique: next.technique, custom: next.custom } });
    } catch {
      got = { status: 0, doc: null };
    }
    if (got.status === 200 && got.doc) {
      f.rev = got.doc.rev;
      f.base = next;
      f.mode = "file";
      stale = 0;
      if (!f.queued) confirmed(win, f);
      if (f.note) told(f, null);
    } else if (got.status === 409 && got.doc?.reason === "stale") {
      const theirs = opticsOf(got.doc.looks ?? {});
      f.queued = rebase(f.base, f.queued ?? next, theirs);
      f.base = theirs;
      f.rev = got.doc.rev ?? "";
      f.madeFrom = theirs;
      journal(win, f, f.queued);
      held.set(win, f.queued);
      applyStored(doc, win);
      told(f, f.note);
      stale += 1;
    } else if (got.status === 409 && got.doc?.reason === "corrupt") {
      f.mode = "corrupt";
      f.queued = null;
      told(f, NOTE.corrupt(got.doc.path));
    } else {
      f.queued ??= next;
      told(f, NOTE.unsaved);
      break;
    }
  }
  if (f.queued && stale >= 4) told(f, NOTE.unsaved);
  f.flying = null;
}

// `next` was made from `base` and the file has since become `theirs` (another tab): this page's own changes, applied to theirs.
export function rebase(base, next, theirs) {
  const was = new Map(base.custom.map((l) => [l.id, JSON.stringify(l)]));
  const mine = new Map(next.custom.map((l) => [l.id, l]));
  const kept = theirs.custom.filter((l) => !was.has(l.id) || mine.has(l.id))
    .map((l) => (mine.has(l.id) && JSON.stringify(mine.get(l.id)) !== was.get(l.id) ? mine.get(l.id) : l));
  const have = new Set(kept.map((l) => l.id));
  // New here, or edited here and deleted by them: either way this page's change survives.
  const fresh = next.custom.filter((l) => !have.has(l.id) && JSON.stringify(l) !== was.get(l.id));
  const pick = (a, b, c) => (a === b ? c : a);
  return {
    active: { light: pick(next.active.light, base.active.light, theirs.active.light), dark: pick(next.active.dark, base.active.dark, theirs.active.dark) },
    technique: pick(next.technique, base.technique, theirs.technique),
    custom: [...kept, ...fresh],
  };
}

// The header menu's quick switch: make `id` the look for `theme` and apply it.
export function choose(theme, id, doc = globalThis.document, win = globalThis) {
  const state = stateOf(win);
  return save({ ...state, active: { ...state.active, [theme]: id } }, doc, win);
}

// ---- The Looks page's edits (§6). Pure: each takes a state and returns a new one or a value. ----

export const NAME_MAX = 40;

// `base`, or `base 2`, `base 3`… so no custom look shares it.
export function uniqueName(base, state, exceptId = null) {
  const taken = new Set(state.custom.filter((l) => l.id !== exceptId).map((l) => l.name));
  if (!taken.has(base)) return base;
  let n = 2;
  while (taken.has(`${base} ${n}`)) n += 1;
  return `${base} ${n}`;
}

// Why a rename is refused, or null when it is fine.
export function nameError(name, id, state) {
  const t = name.trim();
  if (!t) return "A look needs a name.";
  if (t.length > NAME_MAX) return `Keep the name to ${NAME_MAX} characters.`;
  if (state.custom.some((l) => l.id !== id && l.name === t)) return "Another custom look has that name.";
  return null;
}

let seq = 0;
const newId = (state) => {
  let id;
  do id = `custom-${Date.now().toString(36)}-${(seq += 1)}`; while (state.custom.some((l) => l.id === id));
  return id;
};

const copyProfiles = (p) => ({ shadow: { ...p.shadow }, dual: { ...p.dual } });

// A custom copy of any look: same mode and theme, its own name; it starts identical to the look it copies.
export function copyOf(look, state) {
  return {
    id: newId(state), name: uniqueName(`${look.name} copy`.slice(0, NAME_MAX), state), theme: look.theme, mode: look.mode, builtin: false, profile: profileOf(look), graphShadow: look.graphShadow === true,
    profiles: copyProfiles(look.profiles), backdrop: look.backdrop ? { ...look.backdrop } : backdropDefaults(look.mode), ink: { ...(look.ink ?? {}) }, ...typeField(look.type),
  };
}

// The state without custom look `id`; a slot that showed it falls back to its default.
export function withoutLook(state, id) {
  const gone = state.custom.find((l) => l.id === id);
  const active = { ...state.active };
  if (gone && active[gone.theme] === id) active[gone.theme] = DEFAULTS[gone.theme];
  return { ...state, active, custom: state.custom.filter((l) => l.id !== id) };
}

// One look as the lab's schema 3 (`name` and `ink` are the additions; the lab ignores them).
export function exportLook(look, technique = "shadow", now = new Date()) {
  return {
    schema: 3, current_mode: look.mode, exported_at: now.toISOString(),
    modes: { [look.mode]: { technique, graphShadow: look.graphShadow === true, name: look.name, profiles: { shadow: { controls: { ...look.profiles.shadow } }, dual: { controls: { ...look.profiles.dual } } }, backdrop: { controls: { ...(look.backdrop ?? backdropDefaults(look.mode)) } }, ink: { ...(look.ink ?? {}) }, ...typeField(look.type) } },
  };
}

// A schema-3 file's text as new custom looks, or an error and nothing. `resolved` is dropped; controls clamp to the ranges.
export function importLooks(text, state) {
  let doc;
  try {
    doc = JSON.parse(text);
  } catch {
    return { error: "That file is not JSON." };
  }
  if (doc === null || typeof doc !== "object" || doc.schema !== 3) return { error: "Only look-lab schema 3 files can be imported." };
  const modes = doc.modes !== null && typeof doc.modes === "object" ? Object.entries(doc.modes) : [];
  if (modes.length === 0) return { error: "The file has no looks in it." };
  const unknown = modes.find(([m]) => !MODES[m]);
  if (unknown) return { error: `Unknown look mode "${unknown[0]}".` };
  const un = (x) => (x !== null && typeof x === "object" && "controls" in x ? x.controls : x);
  const looks = [];
  let next = state;
  for (const [mode, body] of modes) {
    const b = body !== null && typeof body === "object" ? body : {};
    const raw = { id: newId(next), mode, profile: b.technique, graphShadow: b.graphShadow, profiles: { shadow: un(b.profiles?.shadow), dual: un(b.profiles?.dual) }, backdrop: un(b.backdrop), ink: b.ink, type: b.type };
    const look = cleanCustom({ ...raw, name: "x" });
    const name = typeof b.name === "string" && b.name.trim() ? b.name.trim().slice(0, NAME_MAX) : `${mode} (imported)`;
    look.name = uniqueName(name, next);
    looks.push(look);
    next = { ...next, custom: [...next.custom, look] };
  }
  return { looks, state: next };
}
