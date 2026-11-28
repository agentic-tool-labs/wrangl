// PNGs of the herdr Graph from the fixture, for a look before ship (ah2-696): `node herdr-desktop/preview/capture.mjs [OUT_DIR]`.
// Headless Chrome screenshots of render.mjs's HTML preview, so it pictures the preview's mirror of graph-view.js, not Herdr's GPUI
// drawing. No Herdr, no ~/.config/herdr-app. OUT_DIR is created if missing (default: a new temp dir); each written path is printed.
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";
import { devtools, launchChrome, stopChrome } from "../../tests/perf/chrome.mjs";

// The harness owns its scenario: only views render.mjs draws, each in both themes.
const VIEWS = ["graph-lanes", "graph-lanes-flow", "graph-shot-blocked", "dock", "dock-flow"];
const THEMES = ["dark", "light"];
// Viewport height per view (default 900): the Map with a lane needs room for the team's square and the lane's under it.
const HEIGHT = { "graph-lanes": 1800 };
const LIMIT_MS = 90000;

const args = process.argv.slice(2);
if (args.length > 1 || args[0]?.startsWith("-")) {
  console.error("usage: node herdr-desktop/preview/capture.mjs [OUT_DIR]");
  process.exit(2);
}
const out = args[0] ? resolve(args[0]) : mkdtempSync(join(tmpdir(), "ah2-graph-shots-"));
mkdirSync(out, { recursive: true });
const html = join(out, "ah2.html");
const r = spawnSync(process.execPath, [join(import.meta.dirname, "render.mjs"), html], { encoding: "utf8" });
if (r.status !== 0) {
  process.stderr.write(r.stderr ?? "");
  process.exit(1);
}

const profile = mkdtempSync(join(tmpdir(), "ah2-graph-chrome-"));
let proc = null;
const cleanUp = async () => {
  await stopChrome(proc);
  rmSync(profile, { recursive: true, force: true });
};
const timer = setTimeout(async () => {
  console.error(`capture: gave up after ${LIMIT_MS / 1000} s`);
  await cleanUp();
  process.exit(1);
}, LIMIT_MS);

let code = 0;
try {
  proc = launchChrome(profile, ["--hide-scrollbars"], { stdio: ["ignore", "ignore", "pipe"] });
  const { page } = await devtools(proc);
  let loaded = null;
  page.on((m) => m.method === "Page.loadEventFired" && loaded?.());
  const go = (url) => new Promise((ok, bad) => {
    loaded = ok;
    page.send("Page.navigate", { url }).catch(bad);
  });
  await page.send("Page.enable");
  await page.send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "reduce" }] });
  const base = pathToFileURL(html).href;
  for (const view of VIEWS) {
    await page.send("Emulation.setDeviceMetricsOverride", { width: 1440, height: HEIGHT[view] ?? 900, deviceScaleFactor: 2, mobile: false });
    for (const theme of THEMES) {
      await go("about:blank"); // …#dark to …#light on one document is a fragment navigation: the theme script would not run again
      await go(`${base}?view=${view}#${theme}`);
      await page.send("Runtime.evaluate", { expression: "document.fonts.ready.then(() => 1)", awaitPromise: true });
      const shot = await page.send("Page.captureScreenshot", { format: "png" });
      const file = join(out, `${view}-${theme}.png`);
      writeFileSync(file, Buffer.from(shot.data, "base64"));
      console.log(file);
    }
  }
} catch (e) {
  console.error(`capture: ${e.message}`);
  code = 1;
} finally {
  clearTimeout(timer);
  await cleanUp();
}
process.exit(code);
