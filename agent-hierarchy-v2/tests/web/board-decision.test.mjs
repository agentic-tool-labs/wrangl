import { test } from "node:test";
import assert from "node:assert/strict";

// The Board's decision card on the mock's `team` scenario (one decision waiting): the question, not just a count.
process.env.AH_MOCK_FIXTURE = "team";
const { mountApp: mount, until, find, text, DIR } = await import("./mount-app.mjs");
const { hashOf } = await import("../../web/ui/deeplink.js");

Object.defineProperty(globalThis, "localStorage", { value: { getItem: (k) => (k === "ah.plans.layout" ? "board" : null), setItem() {} }, configurable: true });
const qid = `questions:${DIR}/ship`;
const href = hashOf({ view: "inbox", inboxTab: "attention", team: `team:${DIR}/ship` });
const card = (m) => find(m.root, "article.kb-card").find((c) => /\bis-ask\b/.test(c.getAttribute("class") ?? "") || find(c, "a.kb-answer").length > 0);
const labels = (n) => find(n, "span.qa-label").map(text);

// S2 (ah-ui-answer §5): the card answers its question in place with the Inbox's form; the link to the Inbox stays for the rest.
test("a decision card answers the oldest open question in place, keeps typed text over a slice update, and links the rest to the Inbox; the panel draws the form too", async () => {
  const m = await mount({ view: "board" });
  await until(() => card(m) && find(card(m), "form.qa-answer-form").length === 1, "the form on the card");
  const c = card(m);
  const title = find(c, "h3.kb-title")[0];
  assert.match(text(title), /^Item 18\(1\) says reports wake the device/);
  assert.match(text(c), /asked by sub-orchestrator/);
  assert.deepEqual(labels(c), ["Reports still wake", "Wait for unlock", "Only urgent ones wake", "Other"]);
  assert.equal(find(c, "fieldset")[0].getAttribute("aria-labelledby"), title.getAttribute("id"), "the pick group is named by the card's question");
  assert.deepEqual(find(c, "button").map(text), ["Answer", "Already done", "No longer needed"]);
  assert.equal(find(c, "a.kb-answer").length, 0, "one question, answered here: no link");
  assert.equal(find(c, "p.kb-opts").length, 0, "the form replaces the options line");

  // typed text survives the slice moving
  const radios = () => find(card(m), "input").filter((i) => i.getAttribute("type") === "radio");
  radios().at(-1).fire("change");
  await until(() => find(card(m), "input.qa-other").length === 1, "the Other box");
  find(card(m), "input.qa-other")[0].fire("input", { target: { value: "Only when charging" } });
  const body = m.store.state.slices.get(qid).body;
  m.model.publish([{ id: qid, as_of: Date.now(), body: { ...body, open: [...body.open, { id: "d99", at: new Date().toISOString(), asker: "architect", text: "A second question?", options: [{ label: "Yes" }] }], open_total: 2 } }]);
  await until(() => text(find(card(m), "a.kb-answer")[0] ?? { textContent: "" }) === "Answer 2 in Inbox", "the link counts every open question");
  assert.equal(find(card(m), "a.kb-answer")[0].getAttribute("href"), href);
  const other = find(card(m), "input.qa-other")[0];
  assert.equal(other.value ?? other.getAttribute("value"), "Only when charging", "the typed text is kept");

  m.store.setUi({ item: card(m).getAttribute("data-id") });
  await until(() => find(m.root, "section.kb-panel").length === 1, "the panel");
  const p = find(m.root, "section.kb-panel")[0];
  assert.match(text(find(p, "h2")[0]), /^Item 18\(1\)/);
  assert.match(text(p), /Asked bysub-orchestrator/);
  assert.equal(find(p, "form.qa-answer-form").length, 1, "the panel answers with the same form");
  assert.equal(find(p, "fieldset")[0].getAttribute("aria-labelledby"), find(p, "h2")[0].getAttribute("id"));
  assert.match(text(p), /Also waiting/);
  assert.match(text(p), /A second question\? asked by architect/);
  assert.equal(find(p, "a.kb-answer")[0].getAttribute("href"), href);

  // With no open question to show, the count is the best there is, and the link does not promise an answer.
  m.model.publish([{ id: qid, as_of: Date.now(), body: { ...body, open: [], open_total: 0 } }]);
  await until(() => text(find(card(m), "h3.kb-title")[0]).startsWith("Waiting on you ·"), "the summary's count");
  assert.equal(text(find(card(m), "a.kb-answer")[0]), "Open Inbox");
  assert.equal(find(card(m), "form").length, 0);
});
