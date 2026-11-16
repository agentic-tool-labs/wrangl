// The one headless Chrome launch the perf scripts share: the binary, the flags, and the stop.
// --use-mock-keychain: on a fresh --user-data-dir, macOS Chrome tries to create its "Chrome Safe Storage" keychain item and
// pops a "Keychain Not Found" dialog on the desktop. tests/test-chrome-mock-keychain.sh fails a launch without it.
import { spawn } from "node:child_process";
import { readdirSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { join, sep } from "node:path";

const APP = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
const CACHES = [join(homedir(), ".cache", "puppeteer"), join(homedir(), "Library", "Caches", "ms-playwright")];
const VERSION = /-(\d+(?:\.\d+)*)$/; // chromium_headless_shell-1248, mac_arm-131.0.6778.85
const hostArch = (rel) => (process.arch === "arm64") === /arm/.test(rel);
const dirs = (d) => {
  try {
    return readdirSync(d, { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => join(d, e.name));
  } catch {
    return [];
  }
};
const version = (rel) => rel.split(sep).map((s) => VERSION.exec(s)?.[1]).find(Boolean) ?? "0";

// The newest build of `leaf` up to three directories below a cache, this machine's architecture first (any if it has none),
// in the first cache (puppeteer, then Playwright) that has one; or undefined.
function newest(leaf) {
  for (const cache of CACHES) {
    const found = [];
    let level = [cache];
    for (let depth = 0; depth < 3; depth++) {
      level = level.flatMap(dirs);
      found.push(...level.map((d) => join(d, leaf)).filter((f) => statSync(f, { throwIfNoEntry: false })?.isFile()).map((f) => f.slice(cache.length)));
    }
    const pick = found.some(hostArch) ? found.filter(hostArch) : found;
    const best = pick.sort((a, b) => version(a).localeCompare(version(b), undefined, { numeric: true })).at(-1);
    if (best) return cache + best;
  }
}

// While a headless run uses the installed Chrome.app, macOS treats the app as running and the user cannot open their own
// Chrome. So: $AH_CHROME, then $CHROME_BIN, then chrome-headless-shell, then Chrome for Testing, and Chrome.app last.
// Resolved on first use, so a run that launches nothing never warns.
let chrome;
export function chromeBinary() {
  chrome ??= process.env.AH_CHROME || process.env.CHROME_BIN || newest("chrome-headless-shell")
    || newest(join("Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing"));
  if (!chrome) {
    console.error("warning: no chrome-headless-shell or Chrome for Testing found (set AH_CHROME); using Google Chrome.app, which blocks opening your own Chrome while it runs");
    chrome = APP;
  }
  return chrome;
}

export const launchChrome = (profile, extra, opts) => spawn(chromeBinary(), [
  "--headless=new", "--use-mock-keychain", "--remote-debugging-port=0", `--user-data-dir=${profile}`, "--no-first-run", "--no-default-browser-check",
  ...extra, "about:blank",
], opts);

const open = (url) => new Promise((ok, bad) => {
  const ws = new WebSocket(url);
  ws.onopen = () => ok(ws);
  ws.onerror = () => bad(new Error(`cannot connect to ${url}`));
});

// A DevTools protocol client over one socket: send(method, params) and on(handler) for events.
export function client(ws) {
  let id = 0;
  const pending = new Map();
  const handlers = [];
  ws.onmessage = (e) => {
    const m = JSON.parse(e.data);
    if (m.id) {
      const p = pending.get(m.id);
      pending.delete(m.id);
      m.error ? p.bad(new Error(`${m.error.message}`)) : p.ok(m.result);
    } else {
      handlers.forEach((h) => h(m));
    }
  };
  return {
    send: (method, params = {}) => new Promise((ok, bad) => { pending.set(++id, { ok, bad }); ws.send(JSON.stringify({ id, method, params })); }),
    on: (h) => handlers.push(h),
    close: () => ws.close(),
  };
}

// The browser and first-page clients of a Chrome started by launchChrome with stderr piped.
export async function devtools(proc) {
  const endpoint = await new Promise((ok, bad) => {
    let text = "";
    proc.stderr.on("data", (c) => {
      text += c;
      const m = /DevTools listening on (ws:\/\/\S+)/.exec(text);
      if (m) ok(m[1]);
    });
    proc.once("exit", () => bad(new Error("chrome exited before it listened")));
    setTimeout(() => bad(new Error("chrome did not start in 20 s")), 20000);
  });
  const port = new URL(endpoint).port;
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const page = targets.find((t) => t.type === "page");
  return { browser: client(await open(endpoint)), page: client(await open(page.webSocketDebuggerUrl)) };
}

export const alive = (proc) => proc !== null && proc.exitCode === null && proc.signalCode === null;

// TERM, up to 3 s for it to go, then KILL.
export async function stopChrome(proc) {
  if (!alive(proc)) return;
  proc.kill("SIGTERM");
  await Promise.race([new Promise((ok) => proc.once("exit", ok)), new Promise((ok) => setTimeout(ok, 3000))]);
  if (alive(proc)) proc.kill("SIGKILL");
}
