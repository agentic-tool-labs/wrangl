import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { WEB } from "./lib.mjs";

const css = ["r6.css", "r6-plans.css", "r6-plans-view.css", "r6-conversation.css"].map((name) => readFileSync(join(WEB, name), "utf8")).join("\n");
const rules = [...css.replace(/\/\*[\s\S]*?\*\//g, "").matchAll(/([^{}]+)\{([^{}]*)\}/g)].flatMap((m) => m[1].split(",").map((selector) => [selector.trim(), m[2]]));
const rule = (selector) => rules.filter(([name]) => name === selector).at(-1)?.[1] ?? "";

test("connection dots and selected-control treatments exclude their nearest inactive cases", () => {
  assert.match(rule('.r6 .frame-head .chip[role="status"]::before'), /background: var\(--accent\)/);
  assert.doesNotMatch(rule('.r6 .frame-head .chip[role="status"]::before'), /animation/);
  assert.match(rule('.r6 .frame-head .chip.amber[role="status"]::before'), /animation: none/);
  assert.equal(rule(".r6 .frame-head .chip::before"), "");
  assert.match(rule(".r6 .views .tab.on"), /box-shadow: var\(--shadow-control\)/);
  assert.doesNotMatch(rule(".r6 .views .tab"), /box-shadow/);
  assert.match(rule(".r6 .toast"), /z-index: 30/);
});

test("graph selection and motion rules exclude idle halos and gone animations", () => {
  assert.match(rule(".r6 .node.selected .halo"), /stroke: var\(--ring\)/);
  assert.match(rule(".r6 .node .halo"), /stroke: none/);
  assert.equal(rule(".r6 .node.st-working.breathe .orb"), "");
  assert.match(rule(".r6 .node.breathe .orb"), /animation: none/);
  assert.match(rule(".r6 .node.hub:not(.st-blocked):not(.st-gone) .orb"), /animation: breathe 4s/);
  assert.match(rule(".r6 .node.st-gone .orb"), /animation: none/);
  assert.match(rule(".r6 .offscreen .spark"), /animation: none !important/);
  assert.match(rule(".r6 .spark"), /stroke-dasharray: 2 18/);
  assert.doesNotMatch(rule(".r6 .edge"), /animation/);
});

test("the dialog tab marks only the open tab", () => {
  assert.match(rule(".dialog .etab.on"), /border-color: var\(--accent-ink\)/);
  assert.doesNotMatch(rule(".dialog .etab"), /border-color: var\(--accent-ink\)/);
});

test("conversation-only layout and running indicators exclude dashboard and finished tools", () => {
  assert.match(rule(".r6 .frame-body:has(.conversation) > .rail"), /display: none/);
  assert.doesNotMatch(rule(".r6 .rail"), /display: none/);
  assert.match(css, /@media \(max-width: 760px\)/);
  assert.doesNotMatch(css, /@media \(max-width: 761px\)/);
  assert.match(rule(".member-rail .rail-body"), /min-height: 44px/);
  assert.match(rule(".chip.res.run::before"), /animation: spin .9s/);
  assert.equal(rule(".chip.res.ok::before"), "");
  assert.equal(rule(".chip.res.err::before"), "");
  assert.match(rule(".thinking-dots i:nth-child(2)"), /animation-delay: .2s/);
  assert.match(rule(".thinking-dots i:nth-child(3)"), /animation-delay: .4s/);
  assert.doesNotMatch(rule(".tr-row"), /(^|[\s;])height:/);
  assert.match(rule(".tr-row"), /min-height: 28px/);
});

test("the transcript's scroll container opts out of browser scroll anchoring, which would fight the pane's own anchor", () => {
  assert.match(rule(".tr-scroll"), /overflow-anchor: none/);
});
