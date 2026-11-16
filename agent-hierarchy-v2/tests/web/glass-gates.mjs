// Token resolution and the glass gates (ah-optical-glass §4.3, §9.2): read by contrast.test.mjs and the palette probe.
import { contrast, deltaE, hex, luminance, over, simulate } from "./color.mjs";

export const declarations = (body) => Object.fromEntries([...body.matchAll(/(--[\w-]+):\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
const stripComments = (s) => s.replace(/\/\*[\s\S]*?\*\//g, "");
// Every top-level rule's selector list and custom properties. Rules inside an at-rule block (@media, @supports) are skipped:
// they are conditional, so they must not merge into the unconditional token set.
const dropAtBlocks = (s) => {
  let out = "";
  for (let i = 0; i < s.length; i++) {
    const m = s[i] === "@" && s.slice(i).match(/^@[^{};]*\{/);
    if (!m) { out += s[i]; continue; }
    let depth = 1, j = i + m[0].length;
    for (; j < s.length && depth; j++) depth += s[j] === "{" ? 1 : s[j] === "}" ? -1 : 0;
    i = j - 1;
  }
  return out;
};
export const rulesOf = (css) => [...dropAtBlocks(stripComments(css)).matchAll(/([^{}]+)\{([^{}]*)\}/g)].map((m) => ({ selector: m[1].trim(), vars: declarations(m[2]) }));

// Split on commas outside parentheses.
export function splitTop(s) {
  const out = [];
  let depth = 0, start = 0;
  for (let i = 0; i < s.length; i++) {
    if (s[i] === "(") depth++;
    else if (s[i] === ")") depth--;
    else if (s[i] === "," && depth === 0) {
      out.push(s.slice(start, i).trim());
      start = i + 1;
    }
  }
  out.push(s.slice(start).trim());
  return out;
}

const num = (vars, v) => {
  const t = v.trim();
  const ref = t.match(/^var\((--[\w-]+)\)$/);
  if (ref) return num(vars, vars[ref[1]] ?? "NaN");
  const calc = t.match(/^calc\((.+)\)$/);
  if (calc) {
    const m = calc[1].match(/^(.+?)\s*([+\-*])\s*([\d.]+)$/);
    if (!m) throw new Error(`calc not understood: ${t}`);
    const a = num(vars, m[1]);
    const k = Number(m[3]);
    return m[2] === "+" ? a + k : m[2] === "-" ? a - k : a * k;
  }
  const n = t.endsWith("%") ? Number(t.slice(0, -1)) / 100 : Number(t);
  if (Number.isNaN(n)) throw new Error(`not a number: ${t}`);
  return n;
};

// Substitute var() references inside channel lists ("var(--sw-emissive) / .3" -> "56 189 248 / .3").
const expand = (vars, s) => s.replace(/var\((--[\w-]+)\)/g, (all, name) => (name in vars && !/^(?:#|rgb|color-mix|var)/.test(vars[name].trim()) && /^[\d.\s,]+$/.test(vars[name].trim()) ? vars[name].trim() : all));

// A colour token to [r, g, b, a]: #rgb/#rrggbb, rgb()/rgba() with spaces or commas and an optional alpha (a number, a percent,
// var() or calc()), channel tokens, var() chains and color-mix(in srgb, A p%, B).
export function resolveRgba(vars, value) {
  const v = value.trim();
  const ref = v.match(/^var\((--[\w-]+)\)$/);
  if (ref) {
    if (!(ref[1] in vars)) throw new Error(`undeclared ${ref[1]}`);
    return resolveRgba(vars, vars[ref[1]]);
  }
  if (v === "transparent") return [0, 0, 0, 0];
  if (/^#[0-9a-f]{3}$/i.test(v)) return [...v.slice(1)].map((c) => parseInt(c + c, 16)).concat(1);
  if (/^#[0-9a-f]{6}$/i.test(v)) return hex(v).concat(1);
  const mix = v.match(/^color-mix\(in srgb, (.+?) ([\d.]+)%, (.+)\)$/);
  if (mix) {
    const fg = resolveRgba(vars, mix[1]);
    const bg = resolveRgba(vars, mix[3]);
    const p = Number(mix[2]) / 100;
    return fg.slice(0, 3).map((c, i) => Math.round(c * p + bg[i] * (1 - p))).concat(fg[3] * p + bg[3] * (1 - p));
  }
  const fn = v.match(/^rgba?\((.+)\)$/);
  if (fn) {
    const body = expand(vars, fn[1]);
    const [chan, alpha] = body.includes("/") ? body.split("/") : [body, null];
    const parts = chan.split(/[\s,]+/).filter(Boolean);
    const a = alpha !== null ? num(vars, alpha) : parts.length === 4 ? num(vars, parts[3]) : 1;
    return parts.slice(0, 3).map((p) => num(vars, p)).concat(a);
  }
  throw new Error(`not a colour: ${v}`);
}

// The colour stops of a gradient layer, as [r, g, b, a].
export function stopsOf(vars, layer) {
  const m = layer.trim().match(/^(?:repeating-)?(?:radial|linear)-gradient\((.+)\)$/);
  if (!m) throw new Error(`not a gradient: ${layer}`);
  return splitTop(m[1]).map((part) => part.replace(/\s+-?[\d.]+(?:%|px|deg)?(?:\s+-?[\d.]+(?:%|px))?$/, "").trim()).filter((c) => /^(?:#|rgb|var|color-mix|transparent)/.test(c)).map((c) => resolveRgba(vars, c));
}

// The backdrop point that is worst for text: every orb's peak stop over every base stop (and the bare base), the darkest when
// `dark` is false (light mode: dark ink), the brightest when true. Layers are listed top first; the last is the base.
export function worstBackdrop(vars, backdrop, dark) {
  const layers = splitTop(backdrop);
  const base = stopsOf(vars, layers.at(-1)).map((c) => c.slice(0, 3));
  const orbs = layers.slice(0, -1).map((l) => stopsOf(vars, l)[0]);
  const points = base.flatMap((b) => [b, ...orbs.map((o) => over(o.slice(0, 3), b, o[3]))]);
  return points.sort((x, y) => luminance(x) - luminance(y))[dark ? points.length - 1 : 0];
}

// The glass surface at opacity a over that point.
export const surfaceAt = (vars, worst, opacity) => {
  const s = resolveRgba({ ...vars, "--glass-opacity": String(opacity) }, "var(--surface)");
  return over(s.slice(0, 3), worst, s[3]);
};

const solid = (vars, name) => resolveRgba(vars, `var(${name})`).slice(0, 3);

// Gate 1: every text token at 4.5:1 and every mark at 3:1 on each surface. Returns the failures as strings.
export function gate1(vars, surfaces, texts, marks) {
  const fail = [];
  for (const [sName, s] of Object.entries(surfaces)) {
    for (const t of texts) {
      const r = contrast(solid(vars, t), s);
      if (r < 4.5) fail.push(`${t} on ${sName} ${r.toFixed(2)}:1`);
    }
    for (const t of marks) {
      const r = contrast(solid(vars, t), s);
      if (r < 3) fail.push(`${t} on ${sName} ${r.toFixed(2)}:1`);
    }
  }
  return fail;
}

const pairs = (names) => names.flatMap((a, i) => names.slice(i + 1).map((b) => [a, b]));

// The CIEDE2000 matrix of a set of colours ({name: [r, g, b]}), optionally through a colour-vision simulation.
export function deltaMatrix(colours, sim = null) {
  const f = sim ? (c) => simulate(sim, c) : (c) => c;
  return Object.fromEntries(pairs(Object.keys(colours)).map(([a, b]) => [`${a}|${b}`, Math.round(deltaE(f(colours[a]), f(colours[b])) * 10) / 10]));
}

// Gate 2: every pair at ΔE ≥ 20.
export const gate2 = (colours) => Object.entries(deltaMatrix(colours)).filter(([, d]) => d < 20).map(([p, d]) => `${p} ΔE ${d}`);

// Gate 3: under deuteranopia every pair at ΔE ≥ min(15, the house value for that pair, when the house has it). A pair the house
// already has under 15 fails only when glass is worse than house; at or above house it is reported. Returns { fail, report }.
export function gate3(colours, houseColours) {
  const now = deltaMatrix(colours, "deuteranopia");
  const house = deltaMatrix(Object.fromEntries(Object.entries(houseColours).filter(([k]) => k in colours)), "deuteranopia");
  const fail = [];
  const report = [];
  for (const [p, d] of Object.entries(now)) {
    const h = house[p];
    const limit = h !== undefined ? Math.min(15, h) : 15;
    const line = `${p} deut ΔE ${d}${h !== undefined ? ` (house ${h})` : ""}`;
    if (d < limit) fail.push(line);
    else if (d < 15) report.push(line);
  }
  return { fail, report, protanopia: deltaMatrix(colours, "protanopia") };
}
