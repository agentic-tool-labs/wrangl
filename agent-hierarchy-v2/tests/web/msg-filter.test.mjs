import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const { isInternal, setShowInternal, shownOf, showInternal } = await import("../../web/ui/msg-filter.js");
const { transcriptRows } = await import("../../web/ui/transcript-rows.js");
const { filterRows } = await import("../../web/ui/conversation.js");
const { INTERNAL_TEXTS, servedEntry } = await import("../../web/mock/transcripts.mjs");
const rowsOf = (entries) => transcriptRows(entries.map(servedEntry));
const { INTERNAL_SLUGS } = await import("../../web/mock/fixture.mjs");

const GOLDENS = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "native", "tests", "Ah.Watch.Tests", "Goldens");
const golden = JSON.parse(readFileSync(join(GOLDENS, "message-classes.json"), "utf8"));

test("only the internal class hides: work, needs_you, a kind this client does not know and no class all show", () => {
  const items = [{ class: "work" }, { class: "internal" }, { class: "needs_you" }, { class: "brand-new" }, {}];
  assert.deepEqual(items.filter((x) => isInternal(x)).length, 1);
  assert.deepEqual(shownOf(items, { showInternal: false }), items.filter((x) => x.class !== "internal"));
  assert.deepEqual(shownOf(items, { showInternal: true }), items);
});

test("the default hides internal messages, and the choice is remembered for the viewer", () => {
  const stored = new Map();
  const had = globalThis.localStorage;
  globalThis.localStorage = { getItem: (k) => stored.get(k) ?? null, setItem: (k, v) => stored.set(k, v) };
  try {
    assert.equal(showInternal({}), false);
    const seen = [];
    setShowInternal({ setUi: (x) => seen.push(x) }, true);
    assert.deepEqual(seen, [{ showInternal: true }]);
    assert.equal(stored.get("ah.messages.internal"), "on");
    assert.equal(showInternal({}), true);
    setShowInternal({ setUi: () => {} }, false);
    assert.equal(showInternal({}), false);
  } finally {
    globalThis.localStorage = had;
  }
});

test("the Conversation hides internal texts and keeps every other row; needs_you stays", () => {
  const text = (offset, cls, excerpt) => ({ offset, type: "user", class: cls, turn_start: true, blocks: [{ kind: "text", excerpt }] });
  const rows = rowsOf([text(0, "work", "mine"), text(1, "internal", "alarm"), text(2, "needs_you", "QUESTION"), { offset: 3, type: "assistant", blocks: [{ kind: "text", excerpt: "reply" }] }]);
  assert.deepEqual(filterRows(rows, "all", "", false).map((r) => r.label), ["mine", "QUESTION", "reply"]);
  assert.equal(filterRows(rows, "all", "", true).length, 4);
  assert.equal(filterRows(rows, "all", "").length, 4, "the filter is off unless a caller asks");
});

test("the classes the mock serves are the ones the daemon's classifier gives", () => {
  for (const [text, cls] of INTERNAL_TEXTS) {
    assert.equal(golden.texts[text], cls, text);
  }
  for (const slug of INTERNAL_SLUGS) {
    assert.equal(golden.slugs[slug], "internal", slug);
  }
});

test("the Teams rail's recent list follows the choice", async () => {
  const { recentAcross } = await import("../../web/ui/teams.js");
  const row = { name: "ship", dirId: "d" };
  const item = (id, cls, slug) => ({ id, body: { class: cls, slug, created: "2026-11-01T00:00:00Z", from: "a", to: "b" } });
  const windows = [{ items: [item("b", "internal", "release"), item("a", "work", "ui-fix")] }];
  const lists = { get: () => ({ windows }) };
  assert.deepEqual(recentAcross(lists, [row], { showInternal: false }).map((r) => r.id), ["a"]);
  assert.deepEqual(recentAcross(lists, [row], { showInternal: true }).map((r) => r.id), ["b", "a"]);
});
