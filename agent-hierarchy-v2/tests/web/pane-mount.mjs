import assert from "node:assert/strict";
import { settle, installDom } from "./dom.mjs";
import { until, member } from "./mount-app.mjs";

const { TranscriptPane } = await import("../../web/ui/conversation.js");
const { render, html } = await import("../../web/ui/h.js");

export const VIEW = 560;
export const hash = (s) => [...s].reduce((n, c) => (n * 31 + c.charCodeAt(0)) >>> 0, 7);
export const injected = (key) => 30 + (hash(key) % 5) * 12;

// Mounts a TranscriptPane on the app's store with heights read from `heightOf(key)` instead of the DOM.
export async function mountPane(m, heightOf = injected, view = VIEW) {
  m.store.setUi({ view: "graph" });
  const root = installDom();
  const ref = {};
  const measure = (_el, kind, key) => (kind === "view" ? view : heightOf(key));
  render(html`<${TranscriptPane} ref=${(p) => (ref.pane = p)} store=${m.store} memberId=${member("architect")} measure=${measure} />`, root);
  await until(() => root.all("li.tr-row").length > 0, "rows");
  await settle();
  await settle();
  return { pane: ref.pane, root, unmount: () => render(null, root) };
}

export const px = (v) => parseFloat(v);
// Where the DOM puts a row, as the padding plus the heights of the mounted rows above it, less the scroll position.
export function viewportOffset({ pane, root }, key, heightOf = injected) {
  const [from] = pane.mounted;
  const i = pane.rows.findIndex((r) => r.key === key);
  assert.ok(i >= from && i < pane.mounted[1], `row ${key} is mounted`);
  let top = px(root.all(".tr-list")[0].style.paddingTop);
  for (let j = from; j < i; j += 1) {
    top += heightOf(pane.rows[j].key);
  }
  return top - pane.el.scrollTop;
}

export const scrollTo = async (pane, top) => {
  pane.el.scrollTop = top;
  pane.el.fire("scroll");
  await settle();
  await settle();
};

