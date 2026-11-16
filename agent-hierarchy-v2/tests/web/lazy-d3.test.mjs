import test from "node:test";
import assert from "node:assert/strict";
import { installDom } from "./dom.mjs";

// loadD3: one classic script for every ask while it loads, the vendored path, and a failed load forgotten so the next ask tries again.
installDom();
delete globalThis.d3;
const { loadD3 } = await import("../../web/ui/lazy.js");

const scripts = () => (globalThis.document.head.children ?? globalThis.document.head.childNodes ?? []).filter((c) => c.localName === "script");

test("asks while d3 loads share one script; an error rejects and removes it; the next ask loads afresh and resolves to globalThis.d3", async () => {
  const a = loadD3();
  const b = loadD3();
  assert.equal(a, b, "one promise");
  assert.equal(scripts().length, 1);
  const s = scripts()[0];
  assert.equal(s.src, "/ui/vendor/d3.min.js");
  s.fire("error");
  await assert.rejects(a, /d3 did not load/);
  assert.equal(scripts().length, 0, "the failed script is gone");
  const c = loadD3();
  assert.notEqual(c, a, "a fresh try");
  assert.equal(scripts().length, 1);
  globalThis.d3 = { version: "7.9.0" };
  scripts()[0].fire("load");
  assert.equal((await c).version, "7.9.0");
  assert.equal((await loadD3()).version, "7.9.0", "once loaded, no script is added");
  assert.equal(scripts().length, 1);
  delete globalThis.d3;
});
