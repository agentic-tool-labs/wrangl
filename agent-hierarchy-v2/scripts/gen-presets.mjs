#!/usr/bin/env node
// Render the theme-studio presets (the theme-studio mod's presets/*.json, read only) to web/ui/presets.css: one
// :root[data-preset] block per preset that repoints ui.css's palette tokens, plus the swatch the Settings menu draws.
// Run from the plugin root: node scripts/gen-presets.mjs <presets dir>. Prints every colour it had to move for contrast.
// Shadows, overlays, radii and type are not a preset's: they stay the mode's (ui.css).
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { contrast, hex } from "../web/ui/color.js";
import { PRESETS } from "../web/ui/presets.js";
import { deltaMatrix, gate2, gate3Preset, PRESET_DEUT_FLOOR } from "../tests/web/glass-gates.mjs";

const dir = process.argv[2];
if (!dir) throw new Error("usage: node scripts/gen-presets.mjs <theme-studio presets dir>");

// --- colour: sRGB triples, OKLCH for lightness moves (hue and chroma held) ---
const lin = (c) => (c / 255 <= 0.04045 ? c / 255 / 12.92 : ((c / 255 + 0.055) / 1.055) ** 2.4);
const gam = (l) => 255 * (l <= 0.0031308 ? 12.92 * l : 1.055 * Math.max(l, 0) ** (1 / 2.4) - 0.055);
function oklch(rgb) {
  const [r, g, b] = rgb.map(lin);
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  const L = 0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s;
  const A = 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s;
  const B = 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s;
  return [L, Math.hypot(A, B), Math.atan2(B, A)];
}
function rgbOf([L, C, h]) {
  for (let c = C; ; c *= 0.97) {
    const A = c * Math.cos(h), B = c * Math.sin(h);
    const l = (L + 0.3963377774 * A + 0.2158037573 * B) ** 3;
    const m = (L - 0.1055613458 * A - 0.0638541728 * B) ** 3;
    const s = (L - 0.0894841775 * A - 1.291485548 * B) ** 3;
    const out = [4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s, -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s, -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s];
    if (out.every((v) => v >= -1e-4 && v <= 1.0001) || c < 1e-3) return out.map((v) => Math.round(Math.min(255, Math.max(0, gam(v)))));
  }
}
const toHex = (rgb) => `#${rgb.map((v) => v.toString(16).padStart(2, "0")).join("")}`;
const mix = (a, b, p) => a.map((c, i) => Math.round(c * p + b[i] * (1 - p)));

// Move `c` in lightness, away from the page (lighter on dark, darker on light), until it meets `min` on every surface.
const moved = new Map();
function lift(name, id, c, surfaces, min, dark) {
  const worst = (x) => Math.min(...surfaces.map((s) => contrast(x, s)));
  if (worst(c) >= min) {
    moved.delete(`${id} ${name}`);
    return c;
  }
  const [L, C, h] = oklch(c);
  for (let d = 0.005; d <= 1; d += 0.005) {
    const x = rgbOf([Math.min(1, Math.max(0, dark ? L + d : L - d)), C, h]);
    if (worst(x) >= min) {
      moved.set(`${id} ${name}`, `${toHex(c)} → ${toHex(x)} (${worst(c).toFixed(2)} → ${worst(x).toFixed(2)}:1)`);
      return x;
    }
  }
  throw new Error(`${id} ${name}: no lightness meets ${min}:1`);
}

// ui.css's own tokens per mode: the role hues a preset starts from, and the mode's tint strengths.
const css = readFileSync(new URL("../web/ui/ui.css", import.meta.url), "utf8");
const block = (opening) => {
  const at = css.indexOf("{", css.indexOf(opening));
  return Object.fromEntries([...css.slice(at + 1, css.indexOf("}", at)).matchAll(/(--[\w-]+):\s*(#[0-9a-f]{6});/gi)].map((m) => [m[1], hex(m[2])]));
};
const HOUSE = { dark: block(":root {"), light: { ...block(":root {"), ...block(':root[data-theme="light"] {') } };
// ui.css's --tint-* strengths per mode.
const TINT = { dark: { working: 0.12, review: 0.12, attn: 0.12, err: 0.12 }, light: { working: 0.09, review: 0.09, attn: 0.1, err: 0.09 } };
const STATES = ["working", "attn", "ok", "err", "review"];
const ROLES = ["sub-orchestrator", "architect", "implementor", "ux-engineer", "reviewer", "ultra-advisor", "qa", "neutral"];

function tokens(p) {
  const j = JSON.parse(readFileSync(join(dir, `${p.file}.json`), "utf8"));
  const dark = p.mode === "dark";
  const a = j.ansi.map(hex);
  const sem = Object.fromEntries(Object.entries(j.colors ?? {}).map(([k, v]) => [k, hex(v)]));
  const bg = sem.canvas ?? hex(j.background);
  const fg = sem.foreground ?? hex(j.foreground);
  const f = (p) => mix(fg, bg, p);
  // Surfaces step from the page towards the ink; a light page steps darker by the same share.
  const t = {
    "--bg": bg,
    "--bar": sem.chrome ?? f(dark ? 0.035 : 0.03),
    "--side": sem.surface ?? f(dark ? 0.025 : 0.035),
    "--control": sem.raised ?? f(dark ? 0.07 : 0.06),
    "--tab-on": f(dark ? 0.12 : 0.1),
    "--row-hi": sem.hover ?? f(dark ? 0.045 : 0.03),
    "--line": sem.border ?? f(dark ? 0.15 : 0.15),
    "--line-row": f(dark ? 0.1 : 0.09),
    "--line-control": sem.input ?? f(dark ? 0.2 : 0.21),
    "--edge": f(dark ? 0.18 : 0.21),
    "--tool-bg": f(dark ? 0.03 : 0.03),
    "--tool-line": f(dark ? 0.12 : 0.15),
    "--kb-card": f(dark ? 0.055 : 0.0),
  };
  const accent = sem.accent ?? (oklch(a[12])[1] > oklch(a[4])[1] ? a[12] : a[4]);
  t["--row-sel"] = sem.selected ?? mix(accent, bg, dark ? 0.16 : 0.1);
  // States from the terminal's own hues: blue, magenta, yellow, red, green, each the more vivid of its normal and bright slot
  // (Solarized's bright slots are its greys).
  const pick = (n) => (oklch(a[n + 8])[1] > oklch(a[n])[1] ? a[n + 8] : a[n]);
  const st = { working: pick(4), review: pick(5), attn: pick(3), err: sem.blocked ?? pick(1), ok: sem.done ?? pick(2) };
  const tintOf = (c, k) => mix(c, bg, TINT[p.mode][k]);
  const surfaces = (x) => [x["--bg"], x["--bar"], x["--side"], x["--control"], x["--row-hi"], x["--row-sel"], x["--kb-card"], x["--tool-bg"], x["--brief-bg"]];
  t["--brief-bg"] = mix(st.attn, bg, dark ? 0.08 : 0.1);
  // The tints are ui.css's color-mix of the state over --bg; text sits on them too.
  const all = () => [...surfaces(t), ...["working", "review", "attn", "err"].map((k) => tintOf(t[`--${k}`] ?? st[k], k))];
  const T = (name, c) => (t[name] = lift(name, p.id, c, all(), 4.5, dark));
  for (const [k, v] of Object.entries(st)) t[`--${k}`] = v;
  // Tints move with their state, so the text pass runs until nothing moves; then the states are pulled apart in lightness (ΔE 20, and
  // under deuteranopia ΔE min(10, house): tests/web/glass-gates.mjs gate2, gate3Preset), which can move a tint again.
  const text = () => {
    for (let pass = 0; pass < 3; pass++) {
      T("--ink", fg);
      T("--ink-2", mix(fg, bg, 0.82));
      T("--muted", sem.muted ?? mix(fg, bg, 0.66));
      T("--dim", sem.dim ?? mix(fg, bg, 0.6));
      T("--accent", accent);
      for (const [k, v] of Object.entries(st)) T(`--${k}`, v);
      for (const r of ROLES) T(`--role-${r}`, HOUSE[p.mode][`--role-${r}`]);
    }
  };
  const st0 = { ...st };
  const houseSt = Object.fromEntries(STATES.map((k) => [k, HOUSE[p.mode][`--${k}`]]));
  const fails = () => {
    const c = Object.fromEntries(STATES.map((k) => [k, t[`--${k}`]]));
    return [...gate2(c), ...gate3Preset(c, houseSt)];
  };
  const edge = (x) => (dark ? oklch(x)[0] < 0.96 : oklch(x)[0] > 0.3);
  // How far the states are from passing: the summed shortfall of every pair under its ΔE limit.
  const houseDeut = deltaMatrix(houseSt, "deuteranopia");
  const deficit = () => {
    const c = Object.fromEntries(STATES.map((k) => [k, t[`--${k}`]]));
    const n = deltaMatrix(c), d = deltaMatrix(c, "deuteranopia");
    return Object.keys(n).reduce((sum, pr) => sum + Math.max(0, 20 - n[pr]) + Math.max(0, Math.min(PRESET_DEUT_FLOOR, houseDeut[pr] ?? PRESET_DEUT_FLOOR) - d[pr]), 0);
  };
  for (let round = 0; round < 8; round++) {
    text();
    if (!fails().length) break;
    // Coordinate search: each state in turn takes the lightness (0.01 steps), and only if that is not enough a hue within 24° of its
    // source (4° steps), that most cuts the shortfall while keeping 4.5:1 on every surface; the shortfall first, then the fewest steps from the source.
    for (let sweep = 0; sweep < 24 && fails().length; sweep++) {
      for (const k of STATES) {
        const [L0, C0, h0] = oklch(st0[k]);
        const was = t[`--${k}`];
        let best = { x: was, v: deficit() * 1000 + 0.02 * (Math.abs(oklch(was)[0] - L0) * 100) + 0.5 * Math.abs(((oklch(was)[2] - h0 + 3 * Math.PI) % (2 * Math.PI)) - Math.PI) * 180 / Math.PI / 4 };
        for (let dh = -24; dh <= 24; dh += 4) for (let L = 0.3; L <= 0.96; L += 0.01) {
          const x = rgbOf([L, C0, h0 + (dh * Math.PI) / 180]);
          // never washed out: a state keeps 55% of its source's chroma
          if (!edge(x) || oklch(x)[1] < 0.55 * C0 || Math.min(...all().map((sf) => contrast(x, sf))) < 4.5) continue;
          t[`--${k}`] = x;
          const v = deficit() * 1000 + 0.02 * Math.abs(L - L0) * 100 + 0.5 * Math.abs(dh) / 4;
          if (v < best.v) best = { x, v };
        }
        t[`--${k}`] = best.x;
      }
      // A pair that only parts when both move (one up, one down) is out of reach one state at a time: search its two lightnesses together.
      const f = fails()[0];
      if (!f) break;
      const [a, b] = f.split(" ")[0].split("|");
      const [, Ca, ha] = oklch(t[`--${a}`]);
      const [, Cb, hb] = oklch(t[`--${b}`]);
      const keep = [t[`--${a}`], t[`--${b}`]];
      let pair = { x: keep, v: deficit() };
      for (let La = 0.3; La <= 0.96; La += 0.01) {
        const xa = rgbOf([La, Ca, ha]);
        if (!edge(xa) || oklch(xa)[1] < 0.55 * oklch(st0[a])[1] || Math.min(...all().map((sf) => contrast(xa, sf))) < 4.5) continue;
        for (let Lb = 0.3; Lb <= 0.96; Lb += 0.01) {
          const xb = rgbOf([Lb, Cb, hb]);
          if (!edge(xb) || oklch(xb)[1] < 0.55 * oklch(st0[b])[1] || Math.min(...all().map((sf) => contrast(xb, sf))) < 4.5) continue;
          t[`--${a}`] = xa;
          t[`--${b}`] = xb;
          const v = deficit();
          if (v < pair.v) pair = { x: [xa, xb], v };
        }
      }
      [t[`--${a}`], t[`--${b}`]] = pair.x;
    }
    for (const k of STATES) st[k] = t[`--${k}`];
  }
  text();
  if (fails().length) throw new Error(`${p.id}: states not apart: ${fails().join("; ")} ${STATES.map((k) => `${k} ${toHex(t[`--${k}`])} L${oklch(t[`--${k}`])[0].toFixed(2)}`).join(" ")}`);
  for (const k of STATES) {
    const was = st0[k], now = t[`--${k}`];
    if (toHex(was) === toHex(now)) moved.delete(`${p.id} --${k}`);
    else moved.set(`${p.id} --${k}`, `${toHex(was)} → ${toHex(now)} (${Math.min(...all().map((s) => contrast(was, s))).toFixed(2)} → ${Math.min(...all().map((s) => contrast(now, s))).toFixed(2)}:1, and apart)`);
  }
  t["--review-edge"] = t["--review"];
  t["--err-ink"] = t["--err"];
  t["--err-line"] = mix(t["--err"], bg, dark ? 0.25 : 0.22);
  // On the accent fill: the darkest or lightest end of the preset, whichever reads; else the accent moves.
  const ends = [dark ? bg : fg, dark ? fg : bg, [0, 0, 0], [255, 255, 255]];
  const on = sem.accent_foreground && contrast(sem.accent_foreground, t["--accent"]) >= 4.5 ? sem.accent_foreground : ends.find((e) => contrast(e, t["--accent"]) >= 4.5);
  if (!on) throw new Error(`${p.id}: nothing reads on the accent`);
  t["--on-accent"] = on;
  return t;
}

const rgba = (c, a) => `rgb(${c.join(" ")} / ${a})`;
const out = [
  "/* Generated by scripts/gen-presets.mjs from the theme-studio presets (the theme-studio mod's presets, see SOURCES.md",
  "   there); do not edit by hand. shell.js sets :root[data-preset] to the preset of the shown mode; the Default preset sets nothing, so",
  "   ui.css (and the stored look) is what it shows. A swatch carries data-preset too and draws from the same tokens. */",
];
for (const p of PRESETS.filter((p) => p.file)) {
  const t = tokens(p);
  const body = Object.entries(t).map(([k, v]) => `  ${k}: ${toHex(v)};`);
  body.push(`  --kb-col: ${rgba(t["--bar"], p.mode === "dark" ? 0.72 : 0.78)};`, `  --dialog-glass-fill: ${rgba(t["--bar"], p.mode === "dark" ? 0.78 : 0.92)};`,
    `  --board-grid: ${rgba(t["--ink"], p.mode === "dark" ? 0.04 : 0.06)};`);
  out.push("", `:root[data-preset="${p.id}"], .preset-swatch[data-preset="${p.id}"] {`, `  color-scheme: ${p.mode};`, ...body, "}");
}
// The Default preset's swatches: ui.css's own page, line and accent per mode.
for (const mode of ["dark", "light"]) {
  const h = HOUSE[mode];
  out.push("", `.preset-swatch[data-preset="default-${mode}"] { --bg: ${toHex(h["--bg"])}; --line-control: ${toHex(h["--line-control"])}; --accent: ${toHex(h["--accent"])}; --ink: ${toHex(h["--ink"])}; }`);
}
writeFileSync(new URL("../web/ui/presets.css", import.meta.url), `${out.join("\n")}\n`);
console.log([...moved].map(([k, v]) => `${k} ${v}`).join("\n") || "nothing moved");
