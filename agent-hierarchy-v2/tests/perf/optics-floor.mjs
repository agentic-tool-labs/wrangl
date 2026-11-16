#!/usr/bin/env node
// The readable floor (ah-optical-glass §5.4, §9.3): for each glass mode × swatch × grid cell, the worst body-text contrast
// (tiers T2 = --ink/--ink-2 text, T3 = --muted/--dim text) on the realistic fixture's Overview and Board List, measured on
// the rendered page by R's method: hide the ink, keep the halo, take the 98th-percentile worst halo pixel inside each label
// box, and compare it with the label's ink colour. Cells under 4.5:1 go to web/ui/optics-floor.json.
// Usage: node tests/perf/optics-floor.mjs [--check] [--web DIR] [--out FILE] [--modes light,emissive,...] [--swatches obsidian,...]
//   [--cells "haloBlur,density,opacity,glassBlur;..."] [--all]
//   --check   fail (exit 1) when the committed table differs from a fresh run by more than 0.1 in any ratio, or in its cells.
//   --all     write every measured cell, not only the failing ones (probes).
//   --optics '<json>'  further ah-optics fields applied with every cell (probes).
// The page must expose globalThis.ahOptics (web/ui/optics-boot.js); each cell is applied through ahOptics.apply.
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { constants, tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { alive, devtools, launchChrome, stopChrome } from "./chrome.mjs";
import { decodePng, pixelsIn } from "./png.mjs";
import { contrast } from "../web/color.mjs";

const args = process.argv.slice(2);
const flag = (name, fallback) => (args.includes(`--${name}`) ? args[args.indexOf(`--${name}`) + 1] : fallback);
const WEB = resolve(flag("web", join(import.meta.dirname, "..", "..", "web")));
const OUT = resolve(flag("out", join(WEB, "ui", "optics-floor.json")));
const CHECK = args.includes("--check");
const ALL = args.includes("--all");
// Further ah-optics fields for every cell (probes); the cell, mode and swatch win over them.
let EXTRA;
try {
  EXTRA = JSON.parse(flag("optics", "{}"));
  if (!EXTRA || typeof EXTRA !== "object" || Array.isArray(EXTRA)) throw new Error("not an object");
} catch (e) {
  console.error(`--optics needs a JSON object: ${e.message}`);
  process.exit(2);
}

export const MODES = {
  light: { theme: "light", optics: {} },
  emissive: { theme: "dark", optics: { dark: "emissive" } },
  "ink-bright": { theme: "dark", optics: { dark: "ink", inkText: "bright" } },
  "ink-etched": { theme: "dark", optics: { dark: "ink", inkText: "etched" } },
};
export const SWATCHES = ["obsidian", "midnight", "espresso", "forest"];
export const GRID = { haloBlur: [1, 4, 7, 10, 13, 16], density: [20, 50, 80, 110, 140, 170, 200], opacity: [35, 60, 85, 92], glassBlur: [18] };
const VIEWS = { overview: { hash: "#/overview", layout: null }, "board-list": { hash: "#/plans", layout: "list" } };
const FLOOR = 4.5;
const PERCENTILE = 0.98;

const pick = (name, all) => (flag(name, null)?.split(",") ?? all).filter((x) => all.includes(x));
const modes = pick("modes", Object.keys(MODES));
const swatches = pick("swatches", SWATCHES);
const cells = flag("cells", null)
  ? flag("cells").split(";").map((c) => Object.fromEntries(c.split(",").map(Number).map((v, i) => [["haloBlur", "density", "opacity", "glassBlur"][i], v])))
  : GRID.haloBlur.flatMap((haloBlur) => GRID.density.flatMap((density) => GRID.opacity.flatMap((opacity) => GRID.glassBlur.map((glassBlur) => ({ haloBlur, density, opacity, glassBlur })))));

// Ink hidden, halo kept: text colour transparent (a text-shadow keeps its own colour; an aria-hidden ghost copy is the halo
// and is left alone), SVG out of the way.
const INK = ".r6 *:not([aria-hidden=true], [aria-hidden=true] *)";
const HIDE_INK = `${INK},${INK}::before,${INK}::after{color:transparent!important;-webkit-text-fill-color:transparent!important;caret-color:transparent!important} .r6 svg,.r6 img{visibility:hidden!important}`;

// In the page: the T2/T3 labels (elements with their own text, coloured by a body token, visible in the viewport) and their
// text boxes, then the ink-hiding sheet.
const PROBE = `(() => {
  const tok = {};
  for (const [tier, names] of Object.entries({ T2: ["--ink", "--ink-2"], T3: ["--muted", "--dim"] })) {
    for (const t of names) { const p = document.createElement("span"); p.style.color = "var(" + t + ")"; document.body.append(p); tok[getComputedStyle(p).color] ??= tier; p.remove(); }
  }
  const labels = [];
  for (const e of document.querySelectorAll(".r6 *")) {
    if (e.closest("svg,[aria-hidden=true]")) continue;
    const texts = [...e.childNodes].filter((n) => n.nodeType === 3 && n.textContent.trim());
    if (!texts.length) continue;
    const cs = getComputedStyle(e);
    const tier = tok[cs.color];
    if (!tier || cs.visibility !== "visible" || Number(cs.opacity) === 0) continue;
    for (const n of texts) {
      const r = document.createRange(); r.selectNodeContents(n);
      for (const b of r.getClientRects()) {
        if (b.width < 2 || b.height < 2 || b.right <= 0 || b.bottom <= 0 || b.left >= innerWidth || b.top >= innerHeight) continue;
        labels.push({ tier, ink: cs.color, box: [b.left, b.top, b.right, b.bottom] });
      }
    }
  }
  return labels;
})()`;

const sleep = (ms) => new Promise((ok) => setTimeout(ok, ms));
const rgbOf = (s) => s.slice(s.indexOf("(") + 1, s.indexOf(")")).split(/[\s,/]+/).filter(Boolean).slice(0, 3).map(Number);
const round = (n) => Math.round(n * 100) / 100;
const cellKey = (c) => `${c.haloBlur}/${c.density}/${c.opacity}/${c.glassBlur}`;

// The worst label ratio per tier from one screenshot with the ink hidden.
export function worstByTier(img, labels, scale = 1) {
  const worst = {};
  for (const { tier, ink, box } of labels) {
    const fg = rgbOf(ink);
    const ratios = pixelsIn(img, box, scale).map((px) => contrast(fg, px)).sort((a, b) => a - b);
    if (!ratios.length) continue;
    const r = ratios[Math.floor(ratios.length * (1 - PERCENTILE))];
    worst[tier] = Math.min(worst[tier] ?? Infinity, r);
  }
  return Object.fromEntries(Object.entries(worst).map(([k, v]) => [k, round(v)]));
}

async function measure() {
  process.env.AH_MOCK_FIXTURE = "realistic";
  const { createMockServer } = await import(pathToFileURL(join(WEB, "mock", "serve.mjs")).href);
  const tmp = mkdtempSync(join(tmpdir(), "ah-optics-floor."));
  let chrome = null;
  let mock = null;
  const cleanup = async () => {
    await stopChrome(chrome);
    await mock?.close();
    rmSync(tmp, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
  };
  for (const sig of ["SIGINT", "SIGTERM", "SIGHUP"]) process.on(sig, () => cleanup().then(() => process.exit(128 + constants.signals[sig])));
  process.on("exit", () => { if (alive(chrome)) chrome.kill("SIGKILL"); });
  try {
    mock = createMockServer();
    await mock.listen(0);
    chrome = launchChrome(join(tmp, "profile"), ["--disable-extensions", "--window-size=1440,900"], { stdio: ["ignore", "ignore", "pipe"] });
    const { browser, page } = await devtools(chrome);
    const evalIn = async (expression) => {
      const r = await page.send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
      if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? "evaluate failed");
      return r.result.value;
    };
    await page.send("Page.enable");
    await page.send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
    await page.send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "reduce" }] });
    const result = {};
    for (const [view, { hash, layout }] of Object.entries(VIEWS)) {
      const init = await page.send("Page.addScriptToEvaluateOnNewDocument", { source: `try { ${layout ? `localStorage.setItem("ah.plans.layout", ${JSON.stringify(layout)});` : ""} } catch {}` });
      await page.send("Page.navigate", { url: mock.url() });
      const settle = async (test, what) => {
        for (let i = 0; i < 100; i++) {
          if (await evalIn(test)) return;
          await sleep(300);
        }
        throw new Error(`timed out waiting for ${what}`);
      };
      await settle(`!!document.body?.classList.contains("r6") && !!document.querySelector(".shell-head") && !/loading/i.test(document.body.innerText)`, "the app shell");
      await evalIn(`location.hash = ${JSON.stringify(hash)}; 0`);
      await settle(`location.hash.startsWith(${JSON.stringify(hash)}) && !document.querySelector(".teams") && !/loading/i.test(document.body.innerText)`, `the ${view} view`);
      await sleep(2000);
      if (!(await evalIn(`typeof globalThis.ahOptics?.apply === "function"`))) {
        throw new Error("the page has no globalThis.ahOptics.apply (web/ui/optics-boot.js, slice G2); nothing to measure");
      }
      for (const mode of modes) {
        for (const swatch of swatches) {
          for (const cell of cells) {
            const state = { ...EXTRA, skin: "glass", swatch, ...MODES[mode].optics, ...cell };
            await evalIn(`(() => { const s = document.adoptedStyleSheets; document.adoptedStyleSheets = s.filter((x) => !x.__floor); try { localStorage.setItem("ah-theme", ${JSON.stringify(MODES[mode].theme)}); } catch {} ahOptics.apply({ ...ahOptics.DEFAULTS, ...${JSON.stringify(state)} }); return 0; })()`);
            await sleep(150);
            const labels = await evalIn(PROBE);
            if (!labels.length) throw new Error(`no T2/T3 labels found on ${view} (${mode}/${swatch}); the token colours match no text`);
            await evalIn(`(() => { const st = new CSSStyleSheet(); st.__floor = true; st.replaceSync(${JSON.stringify(HIDE_INK)}); document.adoptedStyleSheets = [...document.adoptedStyleSheets, st]; return 0; })()`);
            await sleep(100);
            const shot = await page.send("Page.captureScreenshot", { format: "png" });
            const worst = worstByTier(decodePng(Buffer.from(shot.data, "base64")), labels);
            const row = (result[`${mode}/${swatch}`] ??= {});
            const prev = row[cellKey(cell)] ?? {};
            row[cellKey(cell)] = Object.fromEntries(["T2", "T3"].filter((t) => t in worst || t in prev).map((t) => [t, Math.min(worst[t] ?? Infinity, prev[t] ?? Infinity)]));
          }
        }
      }
      await page.send("Page.removeScriptToEvaluateOnNewDocument", { identifier: init.identifier });
    }
    page.close();
    browser.close();
    return result;
  } finally {
    await cleanup();
  }
}

// The table the picker reads: per mode/swatch, the cells whose worst body tier is under the floor, with that ratio.
export function floorTable(measured, all = false) {
  const table = {};
  for (const [ms, row] of Object.entries(measured)) {
    for (const [cell, tiers] of Object.entries(row)) {
      const ratio = Math.min(...Object.values(tiers));
      if (all || ratio < FLOOR) (table[ms] ??= {})[cell] = ratio;
    }
  }
  return table;
}

// The differences between two tables: a cell in one and not the other, or a ratio more than 0.1 apart.
export function tableDiff(a, b) {
  const out = [];
  for (const ms of new Set([...Object.keys(a), ...Object.keys(b)])) {
    for (const cell of new Set([...Object.keys(a[ms] ?? {}), ...Object.keys(b[ms] ?? {})])) {
      const [x, y] = [a[ms]?.[cell], b[ms]?.[cell]];
      if (x === undefined || y === undefined || Math.round(Math.abs(x - y) * 1000) / 1000 > 0.1) out.push(`${ms} ${cell}: ${x ?? "absent"} vs ${y ?? "absent"}`);
    }
  }
  return out;
}

if (import.meta.main) {
  const measured = await measure();
  const doc = { floor: FLOOR, grid: GRID, percentile: PERCENTILE, views: Object.keys(VIEWS), cells: floorTable(measured, ALL) };
  for (const [ms, row] of Object.entries(measured)) {
    const worst = Object.fromEntries(["T2", "T3"].map((t) => [t, Math.min(...Object.values(row).map((x) => x[t] ?? Infinity))]));
    console.log(`${ms}: worst T2 ${worst.T2}, T3 ${worst.T3}`);
  }
  if (CHECK) {
    const diff = tableDiff(JSON.parse(readFileSync(OUT, "utf8")).cells, doc.cells);
    diff.forEach((d) => console.error(`differs: ${d}`));
    process.exit(diff.length ? 1 : 0);
  }
  writeFileSync(OUT, JSON.stringify(doc, null, 2) + "\n");
  console.log(`wrote ${OUT}`);
}
