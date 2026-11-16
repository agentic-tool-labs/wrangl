import { afterEach } from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { installDom } from "./dom.mjs";

// Mounts the whole page on a mock model through the same start() the page uses, for the view tests.
installDom();
globalThis.requestAnimationFrame = () => 0;
globalThis.setInterval = () => 0;
globalThis.clearInterval = () => {};
// d3 as the page's classic script leaves it, so Map draws at once instead of fetching it.
globalThis.d3 = createRequire(import.meta.url)("../../web/ui/vendor/d3.min.js");

const { createMock } = await import("../../web/mock/model.mjs");
const { start } = await import("../../web/ui/app.js");
const { render } = await import("../../web/ui/h.js");
const { allowedKeys } = await import("../../web/mock/list-keys.mjs");

export const wire = (v) => JSON.parse(JSON.stringify(v));
export const find = (root, sel) => root.all(sel);
export const text = (el) => el.textContent;
export const DIR = "a1b2c3d4";
export const member = (n) => `member:${DIR}/ship/${n}`;

export const until = async (pred, what, ms = 3000) => {
  const end = Date.now() + ms;
  while (!pred()) {
    assert.ok(Date.now() < end, `timed out waiting for ${what}`);
    await new Promise((r) => setTimeout(r, 5));
  }
};

const mounts = [];
afterEach(() => {
  for (const m of mounts.splice(0)) {
    m.store.stop();
    render(null, m.root);
  }
});

export async function mountApp({ view = "graph", ui = null, delay = 0, failFeeds = false } = {}) {
  const model = createMock();
  const log = { lists: [], polls: [], actions: [], signals: [], manifests: [] };
  const get = async (path, params = {}, signal) => {
    if (path === "/events") {
      log.polls.push(params.sub);
      log.signals.push(signal);
      return wire(await model.poll({ since: Number(params.since), epoch: params.epoch, sub: params.sub ? params.sub.split(",") : null, fresh: params.fresh ? params.fresh.split(",") : [], wait: 0.03, signal }));
    }
    if (path === "/actions" || path.startsWith("/actions/")) {
      const who = path === "/actions" ? null : path.slice("/actions/".length);
      log.actions.push(who);
      const doc = model.actions(who);
      if (!doc) {
        throw Object.assign(new Error("no_node"), { status: 404 });
      }
      return wire(doc);
    }
    log.lists.push({ path, params });
    const id = path.slice("/list/".length);
    if (failFeeds && id.startsWith("list:feed:team:")) {
      throw Object.assign(new Error(`view ${path}: 404`), { status: 404 });
    }
    const allowed = allowedKeys(id);
    const unknown = Object.keys(params).find((k) => !allowed.has(k));
    if (unknown !== undefined) {
      throw Object.assign(new Error("bad_filter"), { key: unknown });
    }
    return wire(model.list(id, { after: params.after ?? null, before: params.before ?? null, limit: params.limit, from: params.from_ms ?? null, to: params.to_ms ?? null, expand: params.expand ?? null, member: params.member ?? null, with: params.with ?? null, item: params.item ?? null }));
  };
  get.manifest = async (name, signal) => {
    log.manifests.push(name);
    if (delay > 0) {
      await new Promise((ok, no) => {
        const t = setTimeout(ok, delay);
        signal?.addEventListener("abort", () => (clearTimeout(t), no(Object.assign(new Error("aborted"), { name: "AbortError" }))), { once: true });
      });
    }
    return wire(model.manifest(name));
  };
  const root = installDom();
  const store = start(get, root, true);
  ui ? store.setUi(ui) : view === null || store.setUi({ view });
  const m = { model, store, root, log };
  mounts.push(m);
  await until(() => store.state.ready && (view !== "graph" || find(root, "g.gm-leaf").length === 8), "the page");
  return m;
}
