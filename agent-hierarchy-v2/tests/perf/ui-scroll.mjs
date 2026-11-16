#!/usr/bin/env node
// The fixed-viewport check: node tests/perf/ui-scroll.mjs [--shots DIR]
// Serves the mock, opens every view in headless Chrome at three window sizes, and fails when the page itself scrolls
// or a scroll region sits inside another one. --shots also saves each view in dark and light at each size.
// It starts its own mock server and its own Chrome on a fresh profile, and stops both before it exits.
import { mkdtempSync, rmSync, readFileSync, existsSync, mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { constants } from "node:os";
import { createMockServer } from "../../web/mock/serve.mjs";
import { addMember } from "../../web/mock/fixture.mjs";
import { alive, launchChrome, stopChrome } from "./chrome.mjs";
// Home, the five team tabs and the two Inbox tabs (ah-ui-ia §1); the old view ids only redirect to these.
const VIEWS = ["teams", "overview", "board", "graph", "timeline", "conversation", "inbox/attention", "inbox/qa"];
const SIZES = [[1440, 900], [1280, 800], [1024, 700]];
const args = process.argv.slice(2);
const shots = args.includes("--shots") ? args[args.indexOf("--shots") + 1] : null;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Runs in the page: whether it scrolls, and every visible scroll region that has a scroll region above it.
const PROBE = `(() => {
  const scrolls = (el) => { const s = getComputedStyle(el); return /auto|scroll/.test(s.overflowX + " " + s.overflowY); };
  const name = (el) => el.tagName.toLowerCase() + (el.classList.length ? "." + [...el.classList].join(".") : "");
  const root = document.documentElement;
  const nested = [];
  for (const el of document.querySelectorAll("body *")) {
    if (el.getClientRects().length === 0 || !scrolls(el)) continue;
    for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
      if (scrolls(p)) { nested.push(name(el) + " inside " + name(p)); break; }
    }
  }
  return JSON.stringify({ page: root.scrollHeight > innerHeight + 1 || root.scrollWidth > innerWidth + 1, nested });
})()`;

if (shots) addMember("sub-orchestrator", "sub-orchestrator");
const mock = createMockServer();
await mock.listen(0);
const profile = mkdtempSync("/tmp/ahux.");
const chrome = launchChrome(profile, ["--window-size=1440,900"], { stdio: "ignore" });
async function cleanup() {
  await stopChrome(chrome);
  await mock.close();
  rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
}
for (const sig of ["SIGINT", "SIGTERM", "SIGHUP"]) {
  process.on(sig, () => cleanup().finally(() => process.exit(128 + constants.signals[sig])));
}
// Whatever ended the process (an uncaught throw included), Chrome does not outlive it.
process.on("exit", () => {
  if (alive(chrome)) chrome.kill("SIGKILL");
  rmSync(profile, { recursive: true, force: true });
});
let failures = 0;
try {
  const portFile = join(profile, "DevToolsActivePort");
  for (let i = 0; i < 100 && !existsSync(portFile); i++) await sleep(100);
  const port = readFileSync(portFile, "utf8").split("\n")[0];
  const page = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find((t) => t.type === "page");
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((r) => ws.addEventListener("open", r));
  let id = 0;
  const pending = new Map();
  ws.addEventListener("message", (e) => { const m = JSON.parse(e.data); pending.get(m.id)?.(m); pending.delete(m.id); });
  const send = (method, params = {}) => new Promise((r) => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  const run = async (expression) => (await send("Runtime.evaluate", { expression, returnByValue: true })).result?.result?.value;
  if (shots) mkdirSync(shots, { recursive: true });
  for (const [w, h] of SIZES) {
    await send("Emulation.setDeviceMetricsOverride", { width: w, height: h, deviceScaleFactor: 1, mobile: false });
    await send("Page.navigate", { url: mock.url() });
    await sleep(1500);
    for (const view of VIEWS) {
      await run(`location.hash = ${JSON.stringify(view === "teams" ? "#/" : `#/${view}`)}`);
      await sleep(900);
      const got = JSON.parse(await run(PROBE));
      const bad = got.page || got.nested.length > 0;
      failures += bad ? 1 : 0;
      console.log(`${bad ? "FAIL" : "ok  "} ${view} ${w}x${h}${got.page ? " the page scrolls" : ""}${got.nested.length ? ` nested: ${got.nested.join("; ")}` : ""}`);
      for (const theme of shots ? ["dark", "light"] : []) {
        await run(`document.documentElement.dataset.theme = "${theme}"`);
        await sleep(150);
        const png = (await send("Page.captureScreenshot", { format: "png" })).result.data;
        writeFileSync(join(shots, `${view.replace("/", "-")}-${w}x${h}-${theme}.png`), Buffer.from(png, "base64"));
      }
    }
  }
  ws.close();
} finally {
  await cleanup();
}
console.log(failures === 0 ? "every view fits the window, with no scroll inside a scroll" : `${failures} view and size pairs fail`);
process.exit(failures === 0 ? 0 : 1);
