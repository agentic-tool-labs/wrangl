import { test } from "node:test";
import assert from "node:assert/strict";
import { settle } from "./dom.mjs";
import { mountApp as mount, until, find, text, member } from "./mount-app.mjs";

const { globalKey, GLOBAL_KEYS } = await import("../../web/ui/keys.js");

const press = (m, key, extra = {}, target = globalThis.document.body) => globalKey({ key, target, preventDefault() {}, ...extra }, m.store, globalThis.document);

async function openConversation(m, name = "architect") {
  m.store.setUi({ view: "conversation", selected: member(name) });
  await until(() => find(m.root, "li.tr-row").length > 0, "transcript rows");
  await settle();
}

test("End jumps to live and resumes following in the Conversation", async () => {
  const m = await mount({ view: null });
  await openConversation(m);
  const pane = find(m.root, ".tr-scroll")[0];
  pane.fire("scroll", { currentTarget: { scrollTop: 100 } });
  await until(() => find(m.root, ".jump").length === 1, "paused");
  assert.equal(press(m, "End"), true);
  await until(() => find(m.root, ".jump").length === 0, "following again after End");
});

test("End does nothing off the Conversation (the nearest view that must not take it)", async () => {
  const m = await mount({ view: "graph" });
  assert.equal(press(m, "End"), false);
  assert.equal(m.store.state.ui.liveNonce, undefined);
});

test("End is ignored in a text field and with a modifier", async () => {
  const m = await mount({ view: null });
  await openConversation(m);
  const input = { localName: "input", tagName: "INPUT", getAttribute: () => null, parentNode: null };
  assert.equal(press(m, "End", {}, input), false, "a text field keeps End");
  assert.equal(press(m, "End", { ctrlKey: true }), false);
  assert.equal(press(m, "End", { metaKey: true }), false);
  assert.equal(press(m, "End", { altKey: true }), false);
});

test("End is the page's only in root scope: in the member panel it is not acted on", async () => {
  const m = await mount({ view: null });
  await openConversation(m);
  const panel = { localName: "section", getAttribute: (k) => (k === "class" ? "panel" : null), parentNode: null };
  const inside = { localName: "button", getAttribute: () => null, parentNode: panel };
  assert.equal(press(m, "End", {}, inside), false);
  assert.equal(m.store.state.ui.liveNonce, undefined);
});

test("] toggles the right rail and puts rail-open on the frame body; the toggle button reflects it", async () => {
  const m = await mount({ view: "graph" });
  const toggle = () => find(m.root, "button.rail-toggle")[0];
  assert.equal(toggle().getAttribute("aria-pressed"), "false");
  assert.equal(find(m.root, ".frame-body")[0].getAttribute("class"), "frame-body");
  assert.equal(press(m, "]"), true);
  await until(() => toggle().getAttribute("aria-pressed") === "true", "open");
  assert.match(find(m.root, ".frame-body")[0].getAttribute("class"), /\brail-open\b/);
  toggle().fire("click");
  await until(() => toggle().getAttribute("aria-pressed") === "false", "closed by the button");
});

test("] and End are in the table of page keys, and every page key but Escape yields to the member panel", () => {
  assert.ok(GLOBAL_KEYS.includes("End") && GLOBAL_KEYS.includes("]"));
});

test("the rail collapses behind its toggle only under 1100px", async () => {
  const { readFileSync } = await import("node:fs");
  const css = readFileSync(new URL("../../web/ui/r6-nav.css", import.meta.url), "utf8");
  assert.match(css, /@media \(max-width: 1099px\) \{[^}]*\.rail-toggle \{ display: inline-flex/s);
  assert.match(css, /\.rail-toggle \{ display: none;/);
  assert.match(css, /\.frame-body:not\(\.rail-open\) > \.rail/);
});

test("the help lists every page key above the server's actions, and each key is a real page key", async () => {
  const { KEY_TABLE } = await import("../../web/ui/keys.js");
  const covered = new Set(KEY_TABLE.flatMap((k) => k.keys));
  for (const k of GLOBAL_KEYS) {
    assert.ok(covered.has(k), `${k} is in the help table`);
  }
  for (const k of KEY_TABLE.flatMap((r) => r.keys)) {
    assert.ok(GLOBAL_KEYS.includes(k), `${k} is a page key`);
  }
  assert.ok(KEY_TABLE.some((r) => r.label === "?"), "the help key is listed");
  const m = await mount({ view: "graph" });
  m.store.setUi({ help: true });
  await until(() => find(m.root, ".page-keys").length === 1, "the key table in the help");
  assert.deepEqual(find(find(m.root, ".glossary")[0], "dt").map(text), ["brief", "stale", "dispatched", "waiting-orch", "groom due", "sitrep", "surrogate"]);
  const rows = find(find(m.root, ".page-keys")[0], "kbd").map(text);
  assert.deepEqual(rows, KEY_TABLE.map((r) => r.label));
});
