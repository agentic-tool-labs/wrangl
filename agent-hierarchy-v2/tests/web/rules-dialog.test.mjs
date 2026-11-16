// The rules popup against the mock's rules, the same reply shapes as the daemon's (rules-of-engagement §6, P5).
import { test } from "node:test";
import assert from "node:assert/strict";
import { installDom, settle } from "./dom.mjs";
import { createRules } from "../../web/mock/rules.mjs";

installDom();
const { html, render } = await import("../../web/ui/h.js");
const { RulesDialog, rowsAt, suggestKey } = await import("../../web/ui/rules-dialog.js");
const { globalKey } = await import("../../web/ui/keys.js");

const TEAM = "mock-team";
// The test DOM keeps a control's value as its attribute.
const val = (el) => el.value ?? el.getAttribute("value") ?? "";

async function open(opts) {
  const rules = createRules(opts);
  const sent = [];
  globalThis.fetch = async (url, init = {}) => {
    const [status, body] = init.method === "POST" ? rules.write((sent.push(JSON.parse(init.body)), JSON.parse(init.body))) : rules.read(new URL(url, "http://x").searchParams.get("team"));
    return { status, json: async () => body };
  };
  const root = document.createElement("div");
  let closed = 0;
  render(html`<${RulesDialog} team=${TEAM} onClose=${() => (closed += 1)} />`, root);
  const until = async (pred, what) => {
    for (let i = 0; i < 200 && !pred(); i += 1) {
      await settle();
    }
    assert.ok(pred(), `timed out waiting for ${what}`);
  };
  await until(() => root.all(".ru-levels").length === 1, "the list");
  const row = (key) => root.all(".ru-row").find((r) => r.getAttribute("data-key") === key);
  const button = (el, word) => el.all("button").find((b) => b.textContent.trim() === word);
  const level = async (name) => {
    root.all(".ru-levels")[0].all("input").find((i) => i.getAttribute("value") === name).fire("change");
    await settle();
  };
  const type = async (el, value) => {
    el.fire("input", { target: { value } });
    await settle();
  };
  const addForm = () => root.all(".ru-add")[0].all("form")[0];
  const submit = (form) => form.fire("submit");
  return { root, rules, sent, row, button, level, type, addForm, submit, until, closed: () => closed };
}

test("a key is suggested from the text's first words, within the limit", () => {
  assert.equal(suggestKey("Keep each commit reviewable, please."), "keep-each-commit-reviewable");
  assert.equal(suggestKey("!!!"), "");
  assert.ok(suggestKey("a".repeat(60)).length <= 40);
  assert.match(suggestKey("internationalization localization documentation accessibility"), /^[a-z0-9-]{1,40}$/);
});

test("a row is owned only at its own level and scope", () => {
  const [, data] = createRules().read(TEAM);
  assert.deepEqual(rowsAt(data, "team", TEAM).map((r) => [r.key, r.own]).sort(), [["no-new-deps", true], ["ship-small", false]]);
  assert.deepEqual(rowsAt(data, "goal", "mock-12").map((r) => [r.key, r.own, r.overrides]).find((r) => r[0] === "ship-small"), ["ship-small", true, ["repo"]]);
  assert.equal(rowsAt(data, "team", "another-team").find((r) => r.key === "no-new-deps").own, false);
  assert.equal(rowsAt(data, "goal", "not-open").length, 0);
});

test("add sends the suggested key and draws the new row; the note and the counter are there", async () => {
  const d = await open();
  assert.match(d.root.textContent, /Live members see changes at their next start, compaction or brief\./);
  await d.type(d.addForm().all("textarea")[0], "Be kind to reviewers");
  assert.match(d.root.textContent, /20 of 300 characters/);
  assert.equal(val(d.addForm().all("input")[0]), "be-kind-to-reviewers");
  d.submit(d.addForm());
  await d.until(() => d.row("be-kind-to-reviewers"), "the new row");
  assert.deepEqual(d.sent[0], { op: "add", team: TEAM, level: "team", scope: TEAM, key: "be-kind-to-reviewers", text: "Be kind to reviewers" });
  assert.equal(val(d.addForm().all("textarea")[0]), "", "the form is ready for the next rule");
});

test("a taken key and bad input are refused inline, and the draft stays", async () => {
  const d = await open();
  await d.type(d.addForm().all("textarea")[0], "Again");
  await d.type(d.addForm().all("input")[0], "no-new-deps");
  d.submit(d.addForm());
  await d.until(() => /Refused: rule no-new-deps already exists/.test(d.addForm().textContent), "the key-taken refusal");
  assert.equal(val(d.addForm().all("textarea")[0]), "Again");
  assert.equal(d.addForm().all(".ru-error")[0].getAttribute("role"), "alert");
  await d.type(d.addForm().all("input")[0], "Not A Key");
  d.submit(d.addForm());
  await d.until(() => /Refused: a rule key is 1 to 40/.test(d.addForm().textContent), "the bad-key refusal");
  await d.type(d.addForm().all("input")[0], "long-one");
  await d.type(d.addForm().all("textarea")[0], "x".repeat(301));
  assert.match(d.addForm().textContent, /301 of 300 characters, too long/);
  d.submit(d.addForm());
  await d.until(() => /Refused: a rule's text is at most 300/.test(d.addForm().textContent), "the too-long refusal");
  assert.equal(d.rules.read(TEAM)[1].rows.length, 3, "nothing was written");
});

test("an owned row offers Edit, Switch off and Remove; edit saves through the row's own form", async () => {
  const d = await open();
  const words = (key) => d.row(key).all("button").map((b) => b.textContent.trim());
  assert.deepEqual(words("no-new-deps"), ["Edit", "Switch off", "Remove"]);
  d.button(d.row("no-new-deps"), "Edit").fire("click");
  await settle();
  const form = d.row("no-new-deps").all("form")[0];
  assert.equal(val(form.all("textarea")[0]), "Do not add dependencies without asking.");
  assert.equal(form.all("input").length, 0, "the key of an existing row is not editable");
  await d.type(form.all("textarea")[0], "Ask first.");
  d.submit(d.row("no-new-deps").all("form")[0]);
  await d.until(() => /^Ask first\./.test(d.row("no-new-deps").textContent), "the edited text");
  assert.equal(d.sent[0].op, "edit");
});

test("an inherited row offers Override here and Switch off here; override wins, remove shows the broader rule again", async () => {
  const d = await open();
  const words = (key) => d.row(key).all("button").map((b) => b.textContent.trim());
  assert.deepEqual(words("ship-small"), ["Override here", "Switch off here"]);
  assert.match(d.row("ship-small").textContent, /Repo, inherited/);
  d.button(d.row("ship-small"), "Override here").fire("click");
  await settle();
  assert.equal(val(d.row("ship-small").all("textarea")[0]), "Keep each commit reviewable.", "starts from the text it replaces");
  await d.type(d.row("ship-small").all("textarea")[0], "Smaller still.");
  d.submit(d.row("ship-small").all("form")[0]);
  await d.until(() => /^Smaller still\./.test(d.row("ship-small").textContent), "the override");
  assert.equal(d.sent[0].op, "add");
  assert.match(d.row("ship-small").textContent, /Team, set here/);
  assert.match(d.row("ship-small").textContent, /overrides Repo/);
  d.button(d.row("ship-small"), "Remove").fire("click");
  await d.until(() => /^Keep each commit/.test(d.row("ship-small").textContent), "the broader rule again");
  assert.match(d.root.all(".ru-status")[0].textContent, /Its text was: Smaller still\./);
});

test("switch off here takes the row out of force; switch on starts from the broader text and Remove inherits again", async () => {
  const d = await open();
  d.button(d.row("ship-small"), "Switch off here").fire("click");
  await d.until(() => /Switched off/.test(d.row("ship-small").textContent), "the off row");
  assert.deepEqual(d.sent[0], { op: "off", team: TEAM, level: "team", scope: TEAM, key: "ship-small" });
  assert.deepEqual(d.row("ship-small").all("button").map((b) => b.textContent.trim()), ["Switch on", "Remove"]);
  d.button(d.row("ship-small"), "Switch on").fire("click");
  await settle();
  assert.equal(val(d.row("ship-small").all("textarea")[0]), "Keep each commit reviewable.");
  d.submit(d.row("ship-small").all("form")[0]);
  await d.until(() => /^Keep each commit/.test(d.row("ship-small").textContent), "back on");
  assert.equal(d.sent[1].op, "edit");
});

test("Project and Goal list open items of that kind and write to the chosen one; Repo sends no scope", async () => {
  const d = await open();
  await d.level("goal");
  assert.deepEqual(d.root.all("select")[0].all("option").map((o) => o.getAttribute("value")), ["mock-12"]);
  assert.match(d.row("ship-small").textContent, /One reviewer per change/);
  await d.type(d.addForm().all("textarea")[0], "Goal rule");
  d.submit(d.addForm());
  await d.until(() => d.row("goal-rule"), "the goal row");
  assert.equal(d.sent[0].scope, "mock-12");
  await d.level("repo");
  assert.equal(d.root.all("select").length, 0);
  assert.equal(d.row("no-new-deps"), undefined, "a team's rule is not the repo's");
  await d.type(d.addForm().all("textarea")[0], "Repo rule");
  d.submit(d.addForm());
  await d.until(() => d.row("repo-rule"), "the repo row");
  assert.equal(d.sent[1].scope, "");
});

test("a read that fails says so and offers another try; an unknown team writes nothing", async () => {
  globalThis.fetch = async () => ({ status: 400, json: async () => ({ code: 2, message: "no team ghost" }) });
  const root = document.createElement("div");
  render(html`<${RulesDialog} team="ghost" onClose=${() => {}} />`, root);
  for (let i = 0; i < 50 && !/no team ghost/.test(root.textContent); i += 1) {
    await settle();
  }
  assert.match(root.textContent, /no team ghost/);
  assert.equal(root.all("button").some((b) => b.textContent.trim() === "Try again"), true);
  assert.equal(root.all(".ru-add").length, 0);
});

test("with no team there is nothing to read, and it says why", async () => {
  let reads = 0;
  globalThis.fetch = async () => {
    reads += 1;
    return { status: 400, json: async () => ({}) };
  };
  const root = document.createElement("div");
  render(html`<${RulesDialog} team=${null} onClose=${() => {}} />`, root);
  await settle();
  assert.match(root.textContent, /no team here yet/);
  assert.equal(root.all(".ru-levels").length, 0);
  assert.equal(reads, 0);
});

test("Escape in a row's form leaves the form and not the dialog", async () => {
  const d = await open();
  d.button(d.row("no-new-deps"), "Edit").fire("click");
  await settle();
  d.row("no-new-deps").all("form")[0].fire("keydown", { key: "Escape" });
  await settle();
  assert.equal(d.row("no-new-deps").all("form").length, 0);
  assert.equal(d.closed(), 0);
  d.root.all("section.dialog")[0].fire("keydown", { key: "Escape" });
  assert.equal(d.closed(), 1);
});

test("a page key does nothing inside the popup", () => {
  const store = { state: { ui: { rules: true }, slices: new Map() }, setUi() { assert.fail("a page key acted"); } };
  assert.equal(globalKey({ key: "1", target: { closest: () => null, tagName: "BUTTON" }, preventDefault() {} }, store), false);
});

test("a proposed decision rule is accepted only by its button and moves to the standing decisions", async () => {
  const d = await open({ decisions: TEAM });
  assert.equal(d.sent.length, 0, "nothing is accepted on load");
  assert.ok(d.row("retry-flaky").textContent.includes("Proposed"));
  const standingKeys = () => d.root.all(".ru-standing")[0].all(".ru-row").map((r) => r.getAttribute("data-key")).sort().join();
  assert.equal(standingKeys(), "small-fixes");
  d.button(d.row("retry-flaky"), "Accept").fire("click");
  await d.until(() => standingKeys() === "retry-flaky,small-fixes", "the accepted row");
  assert.deepEqual(d.sent.at(-1), { op: "accept", team: TEAM, level: "team", scope: TEAM, key: "retry-flaky", text: "Rerun a flaky test once before calling it a failure.", applies: "A test fails once and passes on a rerun", choose: "Rerun it once; report it as flaky, not failed" });
  assert.ok(d.root.all(".ru-standing")[0].textContent.includes("Accepted by you"));
});

test("an accept the server refuses shows its reason on the row", async () => {
  const d = await open({ decisions: TEAM });
  d.rules.write({ op: "remove", team: TEAM, level: "team", scope: TEAM, key: "retry-flaky" });
  d.button(d.row("retry-flaky"), "Accept").fire("click");
  await d.until(() => d.row("retry-flaky").all(".ru-row-error")[0]?.textContent, "the refusal");
  assert.equal(d.row("retry-flaky").all(".ru-row-error")[0].textContent, "Refused: no rule retry-flaky at this team");
});

test("a rule edited after the page read it is refused, and the row shows its current words", async () => {
  const d = await open({ decisions: TEAM });
  d.rules.write({ op: "edit", team: TEAM, level: "team", scope: TEAM, key: "retry-flaky", text: "Rerun every failing test three times." });
  d.button(d.row("retry-flaky"), "Accept").fire("click");
  await d.until(() => d.row("retry-flaky").all(".ru-row-error")[0]?.textContent, "the refusal");
  assert.match(d.row("retry-flaky").all(".ru-row-error")[0].textContent, /changed since it was shown.*three times/);
  assert.equal(d.row("retry-flaky").all(".ru-text")[0].textContent, "Rerun every failing test three times.");
  assert.equal(d.rules.read(TEAM)[1].rows.find((r) => r.key === "retry-flaky").proposed, true, "nothing was accepted");
});
