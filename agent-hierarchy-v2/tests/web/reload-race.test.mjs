import { test } from "node:test";
import assert from "node:assert/strict";
import { settle } from "./dom.mjs";
import { mountApp as mount, until, find, text, member } from "./mount-app.mjs";

// A reload keeps the address, so the selection (#/conversation/<member>) is set before the first manifest is read.

test("a conversation selected before the first load reads its transcript", async () => {
  const m = await mount({ view: null, delay: 20, ui: { view: "conversation", selected: member("architect") } });
  await until(() => find(m.root, "li.tr-row").length > 0, "transcript rows");
  await settle();
  assert.equal(find(m.root, "section.transcript").length, 1);
  assert.doesNotMatch(text(find(m.root, "section.transcript")[0]), /Loading…/);
});
