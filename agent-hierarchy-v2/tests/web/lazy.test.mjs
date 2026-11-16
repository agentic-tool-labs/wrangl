import { test } from "node:test";
import assert from "node:assert/strict";
import { installDom, settle } from "./dom.mjs";

installDom();
const { lazy, sheet } = await import("../../web/ui/lazy.js");
const { render, html } = await import("../../web/ui/h.js");

const mountIn = (View, props = {}) => {
  const root = document.createElement("div");
  render(html`<${View} ...${props} />`, root);
  return root;
};
const Inner = ({ n }) => html`<b>view ${n}</b>`;

test("the module is not loaded until the view is first shown, and shows Loading… meanwhile", async () => {
  let loads = 0;
  const View = lazy(() => { loads += 1; return Promise.resolve({ Inner }); }, (m) => m.Inner);
  assert.equal(loads, 0, "defining the view loads nothing");
  const root = mountIn(View, { n: 1 });
  assert.match(root.textContent, /Loading…/);
  await settle();
  assert.match(root.textContent, /view 1/);
  assert.equal(loads, 1);
});

test("a second mount draws the cached view at once and does not load again", async () => {
  let loads = 0;
  const View = lazy(() => { loads += 1; return Promise.resolve({ Inner }); }, (m) => m.Inner);
  mountIn(View, { n: 1 });
  await settle();
  const second = mountIn(View, { n: 2 });
  assert.match(second.textContent, /view 2/, "no Loading… on the second mount");
  assert.equal(loads, 1);
});

test("a failed load says so instead of staying on Loading…", async () => {
  const View = lazy(() => Promise.reject(new Error("x")), (m) => m.Inner);
  const root = mountIn(View);
  await settle();
  assert.match(root.textContent, /Could not load this view/);
});

// A head that holds its links without loading them, so a test decides when each one loads or fails.
function holdingHead() {
  installDom();
  const head = globalThis.document.head;
  const held = [];
  head.insertBefore = (c) => {
    head.appendChild(c);
    held.push(c);
    return c;
  };
  return { head, held };
}

test("a stylesheet that fails to load rejects, leaves no link behind, and the next ask loads it afresh", async () => {
  const { head, held } = holdingHead();
  const first = sheet("/ui/fails-once.css", "/ui/r6-plans.css");
  held[0].fire("error");
  await assert.rejects(first, /did not load/);
  assert.equal(head.childNodes.length, 0, "the failed link is taken out");

  const second = sheet("/ui/fails-once.css", "/ui/r6-plans.css");
  assert.equal(held.length, 2, "a fresh link, not a wait on the dead one");
  held[1].fire("load");
  await second;
});

test("asks for a sheet while it loads share one link and one answer", async () => {
  const { held } = holdingHead();
  const a = sheet("/ui/shared.css", "/ui/r6-plans.css");
  const b = sheet("/ui/shared.css", "/ui/r6-plans.css");
  assert.equal(a, b);
  assert.equal(held.length, 1);
  held[0].fire("load");
  await a;
});

test("a view whose sheet failed says so, and opening it again loads the sheet and draws the view", async () => {
  const { held } = holdingHead();
  const View = lazy(() => Promise.resolve({ Inner }), (m) => m.Inner, ["/ui/view.css", "/ui/r6-plans.css"]);
  const first = mountIn(View, { n: 1 });
  held[0].fire("error");
  await settle();
  assert.match(first.textContent, /Could not load this view/);

  const again = mountIn(View, { n: 2 });
  assert.equal(held.length, 2, "the second open asks for the sheet again");
  held[1].fire("load");
  await settle();
  assert.match(again.textContent, /view 2/);
});
