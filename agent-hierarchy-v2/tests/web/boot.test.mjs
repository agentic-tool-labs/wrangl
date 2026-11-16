import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import vm from "node:vm";

// A module that fails to load must not leave the page saying "Connecting" for ever: the boot watchdog says what failed.

const source = await readFile(new URL("../../web/ui/boot.js", import.meta.url), "utf8");
const html = await readFile(new URL("../../web/ui/index.html", import.meta.url), "utf8");

// boot.js runs in <head>, so the page has no #status when it loads; `mounted: true` adds it right after, as the parser does.
function boot({ mounted = true } = {}) {
  const listeners = { capture: {}, bubble: {} };
  const status = { textContent: "Connecting" };
  let inDocument = false;
  const timers = [];
  const window = {
    addEventListener(type, fn, capture) {
      (capture ? listeners.capture : listeners.bubble)[type] = fn;
    },
  };
  vm.runInNewContext(source, {
    window,
    document: { getElementById: (id) => (id === "status" && inDocument ? status : null) },
    setTimeout: (fn) => timers.push(fn),
    String,
    Error,
  });
  const page = { window, status, timers, listeners, mount: () => (inDocument = true) };
  if (mounted) {
    page.mount();
  }
  return page;
}

test("the page loads the watchdog as a classic script before the module entry, with no inline script the CSP would block", () => {
  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/g)];

  assert.deepEqual(scripts.map((s) => s[1].trim()), ['src="/ui/boot.js"', 'type="module" src="/ui/ui.js"']);
  assert.ok(scripts.every((s) => s[2] === ""), "no inline script text");
});

test("a module whose import cannot be resolved replaces the status with the first error", async (t) => {
  const dir = await mkdtemp(join(tmpdir(), "ah-boot-"));
  t.after(() => rm(dir, { recursive: true, force: true }));
  await writeFile(join(dir, "broken.mjs"), 'import "./does-not-exist.mjs";\n');
  const page = boot();

  await import(join(dir, "broken.mjs")).catch((reason) => page.listeners.bubble.unhandledrejection({ reason }));
  page.listeners.capture.error({ target: { tagName: "SCRIPT", src: "/ui/app.js" } });

  assert.match(page.status.textContent, /^The page's scripts failed to load: .*does-not-exist\.mjs/);
});

test("a script element that fails to load is named, and a stylesheet that fails is not taken for a script failure", () => {
  const page = boot();

  page.listeners.capture.error({ target: { tagName: "LINK", rel: "stylesheet", href: "/ui/ui.css" }, message: "" });
  assert.equal(page.status.textContent, "Connecting");

  page.listeners.capture.error({ target: { tagName: "SCRIPT", src: "/ui/app.js" } });
  assert.equal(page.status.textContent, "The page's scripts failed to load: could not load /ui/app.js");
});

test("a runtime script error before the page starts is shown, and only the first error is kept", () => {
  const page = boot();

  page.listeners.capture.error({ message: "SyntaxError: Unexpected token" });
  page.listeners.bubble.unhandledrejection({ reason: new Error("second") });

  assert.equal(page.status.textContent, "The page's scripts failed to load: SyntaxError: Unexpected token");
});

test("the status line is found when an error arrives, not when the script loads in the head", () => {
  const early = boot({ mounted: false });
  early.listeners.capture.error({ message: "before the body exists" });
  assert.equal(early.status.textContent, "Connecting");

  early.mount();
  early.listeners.bubble.unhandledrejection({ reason: new Error("after the body exists") });
  assert.equal(early.status.textContent, "The page's scripts failed to load: after the body exists");

  const timed = boot({ mounted: false });
  timed.mount();
  timed.timers[0]();
  assert.match(timed.status.textContent, /did not start within 10 seconds/);
});

test("ten silent seconds say the page did not start, and a started page is never overwritten", () => {
  const silent = boot();
  silent.timers[0]();
  assert.equal(silent.status.textContent, "The page's scripts failed to load: the page did not start within 10 seconds");

  const alive = boot();
  alive.window.ahBoot.started();
  alive.timers[0]();
  alive.listeners.bubble.unhandledrejection({ reason: new Error("late") });
  assert.equal(alive.status.textContent, "Connecting");
});
