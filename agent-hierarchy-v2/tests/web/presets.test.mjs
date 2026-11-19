import { test } from "node:test";
import assert from "node:assert/strict";

const { applyPreset, choosePreset, storedPresets, KEY } = await import("../../web/ui/presets.js");

// A window with a store and an OS scheme, and a root that keeps its attributes.
function page(scheme = "dark", stored = {}) {
  const items = new Map(Object.entries(stored));
  const attrs = new Map();
  const win = {
    localStorage: { getItem: (k) => items.get(k) ?? null, setItem: (k, v) => items.set(k, String(v)), removeItem: (k) => items.delete(k) },
    matchMedia: (q) => ({ matches: q.includes("light") ? scheme === "light" : scheme === "dark" }),
  };
  const doc = { documentElement: { getAttribute: (k) => attrs.get(k) ?? null, setAttribute: (k, v) => attrs.set(k, v), removeAttribute: (k) => attrs.delete(k) } };
  return { win, doc, items, attrs };
}

test("nothing stored, or anything unknown, is Default in both modes, and Default sets no attribute", () => {
  for (const raw of [undefined, "not json", JSON.stringify({ dark: "nosuch", light: "nord" })]) {
    const p = page("dark", raw === undefined ? {} : { [KEY]: raw });
    assert.deepEqual(storedPresets(p.win), { dark: "default", light: "default" }, String(raw));
    assert.equal(applyPreset(p.doc, p.win), "default");
    assert.equal(p.attrs.has("data-preset"), false);
  }
});

test("a paired preset sets both modes, so the toggle moves to its pair; any other pick leaves the other mode alone", () => {
  const p = page("dark");
  assert.deepEqual(choosePreset("dark", "catppuccin-mocha", p.win), { dark: "catppuccin-mocha", light: "catppuccin-latte" });
  assert.equal(applyPreset(p.doc, p.win), "catppuccin-mocha");
  p.attrs.set("data-theme", "light");
  assert.equal(applyPreset(p.doc, p.win), "catppuccin-latte");
  assert.equal(p.attrs.get("data-preset"), "catppuccin-latte");
  assert.deepEqual(choosePreset("dark", "nord", p.win), { dark: "nord", light: "catppuccin-latte" }, "Nord has no light pair");
  assert.deepEqual(JSON.parse(p.items.get(KEY)), { dark: "nord", light: "catppuccin-latte" }, "stored");
  assert.deepEqual(choosePreset("light", "default", p.win), { dark: "nord", light: "default" }, "a look (Default) leaves the other mode alone");
  assert.equal(applyPreset(p.doc, p.win), "default");
  assert.equal(p.attrs.has("data-preset"), false);
  assert.deepEqual(choosePreset("light", "nord", p.win), { dark: "nord", light: "default" }, "a dark preset is not offered for light");
});
