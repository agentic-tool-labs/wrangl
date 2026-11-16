import { test } from "node:test";
import assert from "node:assert/strict";
import { settle, installDom } from "./dom.mjs";
import { mountApp as mount, until, find, text, member } from "./mount-app.mjs";

const { helpKey, isTextTarget, keyMatches, MemberActions } = await import("../../web/ui/member-actions.js");
const { html, render } = await import("../../web/ui/h.js");

const written = [];
Object.defineProperty(globalThis, "navigator", { value: { clipboard: { writeText: async (t) => written.push(t) } }, configurable: true });

const label = (m, name) => find(m.root, "button.action").find((b) => text(b).includes(name));
const panelBar = (m) => find(m.root, "article.member-page")[0]?.all("button.action") ?? [];

async function openPanel(m, name = "implementor-1") {
  m.store.setUi({ view: "member", selected: member(name) });
  await until(() => panelBar(m).length > 0, "the actions on the member page");
}

test("opening a member's panel reads its served action list once; hovering reads nothing", async () => {
  const m = await mount();
  assert.deepEqual(m.log.actions, []);
  await openPanel(m);
  assert.deepEqual(m.log.actions, [member("implementor-1")]);
  m.store.setUi({ view: "graph", selected: null });
  await until(() => find(m.root, "g.gm-leaf").length > 0, "the graph again");
  const node = find(m.root, "g.gm-leaf")[0];
  node.fire("mouseenter");
  node.fire("mouseleave");
  await settle();
  assert.equal(m.log.actions.length, 1, "hover made no request");
});

test("the list is read again when the member's slice changes while its panel is open, and not for a change to another member", async () => {
  const id = member("implementor-1");
  const other = member("reviewer");
  const slices = new Map([
    [id, { id, version: 1, body: {} }],
    [other, { id: other, version: 1, body: {} }],
  ]);
  const listeners = new Set();
  let reads = 0;
  const store = {
    state: { slices, ui: {} },
    subscribe: (fn) => (listeners.add(fn), () => listeners.delete(fn)),
    setUi() {},
    fetchActions: async () => (reads += 1, { node: { kind: "member", path: "/o/o.1/t/ship/m/x" }, actions: [] }),
  };
  const bump = (which) => {
    slices.set(which, { ...slices.get(which), version: slices.get(which).version + 1 });
    listeners.forEach((fn) => fn(store.state));
  };
  const root = installDom();
  render(html`<${MemberActions} store=${store} memberId=${id} />`, root);
  await settle();
  assert.equal(reads, 1, "read when it opens");
  bump(other);
  await settle();
  assert.equal(reads, 1, "another member's change does not read it again");
  bump(id);
  await settle();
  assert.equal(reads, 2, "its own change does");
  bump(id);
  bump(id);
  await settle();
  assert.equal(reads, 3, "two changes drawn together read once");
  render(null, root);
});

test("a copy action copies its served text, and an ask action asks to confirm first and then copies the command", async () => {
  const m = await mount();
  await openPanel(m);
  written.length = 0;
  label(m, "Copy name").fire("click");
  await until(() => written.length === 1, "the copy");
  assert.equal(written[0], "implementor-1");
  label(m, "Ask: dismiss").fire("click");
  await until(() => find(m.root, "p.confirm").length === 1, "the confirmation");
  assert.equal(written.length, 1, "nothing is copied before the confirmation");
  find(m.root, "p.confirm")[0].all("button").find((b) => text(b) === "Confirm").fire("click");
  await until(() => written.length === 2, "the command");
  assert.match(written[1], /ah roster dismiss implementor-1 --plan --team ship/);
});

test("Detail opens this member's page, Show mail its Messages tab; no action reads a pane", async () => {
  const m = await mount();
  m.store.setUi({ view: "graph", selected: member("implementor-1") });
  await until(() => (find(m.root, "section.msum")[0]?.all("button.action") ?? []).length > 0, "the actions in the Graph rail");
  const railLabel = (text_) => find(m.root, "section.msum")[0].all("button.action").find((b) => text(b).includes(text_));
  railLabel("Detail").fire("click");
  await until(() => m.store.state.ui.view === "member" && m.store.state.ui.selected === member("implementor-1"), "Detail lands on the member page");
  assert.ok(!m.store.state.ui.expand, "no expand overlay");
  await openPanel(m);
  assert.equal(label(m, "Go live"), undefined);
  assert.equal(label(m, "Show pane"), undefined);
  label(m, "Show mail").fire("click");
  await until(() => m.store.state.ui.expandTab === "messages", "the messages tab");
  assert.equal(m.store.state.ui.expand, member("implementor-1"));
});

test("on the orchestrator Detail opens its member page too, not the expand overlay", async () => {
  const m = await mount();
  m.store.setUi({ view: "member", selected: member("implementor-1"), expand: null });
  await until(() => panelBar(m).length > 0, "a member's actions");
  const detail = label(m, "Detail");
  assert.ok(detail, "a member has Detail");
  m.store.setUi({ view: "member", selected: member("@orchestrator"), expand: null });
  await until(() => label(m, "Explode into graph") !== undefined && label(m, "Detail") !== undefined, "the orchestrator's actions");
  assert.ok(find(m.root, "article.member-page").length === 1);
  m.store.setUi({ view: "graph" });
  await until(() => (find(m.root, "section.msum")[0]?.all("button.action") ?? []).some((b) => text(b).includes("Detail")), "the orchestrator's actions in the Graph rail");
  find(m.root, "section.msum")[0].all("button.action").find((b) => text(b).includes("Detail")).fire("click");
  await until(() => m.store.state.ui.view === "member", "the member page");
  assert.equal(m.store.state.ui.selected, member("@orchestrator"));
  assert.ok(!m.store.state.ui.expand, "no expand overlay");
  assert.equal(find(m.root, "section.expand").length, 0);
});

test("the overlay header carries the same action list", async () => {
  const m = await mount();
  await openPanel(m);
  m.store.setUi({ expand: member("implementor-1") });
  await until(() => find(m.root, "section.expand").length === 1, "the overlay");
  await until(() => (find(m.root, "section.expand")[0].all("button.action") ?? []).length > 0, "actions in the overlay");
  assert.ok(m.log.actions.length >= 2);
});

test("the orchestrator's Plan opens the plan popup, and Explode goes to the Graph view", async () => {
  const m = await mount();
  m.store.setUi({ view: "member", selected: member("@orchestrator") });
  await until(() => panelBar(m).length > 0, "the orchestrator's actions");
  label(m, "Plan").fire("click");
  await until(() => m.store.state.ui.view === "board" && m.store.state.ui.team === "team:a1b2c3d4/ship", "the team's Plans view");
  m.store.setUi({ view: "member", selected: member("@orchestrator") });
  await until(() => panelBar(m).length > 0, "the orchestrator's page again");
  label(m, "Explode into graph").fire("click");
  await until(() => m.store.state.ui.view === "graph", "the graph view");
});

test("a failed read of the list says the actions are unavailable", async () => {
  const m = await mount();
  m.store.fetchActions = async () => {
    throw new Error("404 no_node");
  };
  m.store.setUi({ view: "member", selected: member("reviewer") });
  await until(() => /actions unavailable/.test(text(find(m.root, "article.member-page")[0] ?? { textContent: "" })), "the message");
});

test("a key acts on the enabled action that names it, but not in a text field, with a modifier, or for a disabled action", async () => {
  const m = await mount();
  await openPanel(m);
  written.length = 0;
  const bar = () => find(m.root, "div.member-actions")[0];
  const press = (key, extra = {}) => bar().fire("keydown", { key, target: { localName: "div" }, ...extra });
  press("y");
  await until(() => written.length === 1, "the copy by key");
  assert.equal(written[0], "implementor-1");
  press("y", { target: { localName: "input" } });
  press("y", { ctrlKey: true });
  press("i");
  press("zz");
  await settle();
  assert.equal(written.length, 1, "a text field, a modifier and an unknown key do nothing");
  assert.equal(find(m.root, "p.confirm").length, 0, "and ask for nothing");
  press("D");
  await until(() => find(m.root, "p.confirm").length === 1, "an ask key asks to confirm first");
  assert.equal(written.length, 1);
});

test("the served word keys enter, left and right match the browser's Enter, ArrowLeft and ArrowRight, and a plain letter still matches itself", () => {
  assert.equal(keyMatches("enter", "Enter"), true);
  assert.equal(keyMatches("right", "ArrowRight"), true);
  assert.equal(keyMatches("left", "ArrowLeft"), true);
  assert.equal(keyMatches("y", "y"), true);
  assert.equal(keyMatches("enter", "enter"), false, "the word itself is not a browser key");
  assert.equal(keyMatches("right", "ArrowLeft"), false);
  assert.equal(keyMatches("y", "Y"), false);
  assert.equal(keyMatches(null, "Enter"), false);
});

test("pressing Enter in a member's panel fires its enter action (Detail)", async () => {
  const m = await mount();
  m.store.setUi({ view: "graph", selected: member("implementor-1") });
  const bar = () => find(m.root, "section.msum")[0]?.all("div.member-actions")[0];
  await until(() => bar() && bar().all("button.action").length > 0, "the actions in the Graph rail");
  bar().fire("keydown", { key: "Enter", target: { localName: "div" } });
  await until(() => m.store.state.ui.view === "member", "Detail opened by Enter: the member page");
  assert.equal(m.store.state.ui.selected, member("implementor-1"));
});

test("Enter on a focused button or link is left to that control: no Detail, no preventDefault; a letter on a button still acts", async () => {
  const m = await mount();
  await openPanel(m);
  written.length = 0;
  const bar = find(m.root, "div.member-actions")[0];
  for (const localName of ["button", "a"]) {
    let prevented = false;
    bar.fire("keydown", { key: "Enter", target: { localName }, preventDefault: () => { prevented = true; } });
    await settle();
    assert.equal(prevented, false, `${localName}: preventDefault is not called`);
    assert.equal(find(m.root, "section.expand").length, 0, `${localName}: Detail does not open`);
  }
  bar.fire("keydown", { key: "y", target: { localName: "button" } });
  await until(() => written.length === 1, "a letter on a button still copies");
});

const openHelp = async (m) => {
  find(m.root, "button.more-btn").find((b) => text(b) === "Settings").fire("click");
  await until(() => find(m.root, "ul.menu").length === 1, "the menu");
  find(m.root, "ul.menu").flatMap((u) => u.all("button")).find((b) => text(b).trim() === "Keys and actions").fire("click");
};

test("the Keys and actions menu item opens Help over the open member's actions, or the root list when none is open; a failed read says so", async () => {
  const m = await mount();
  await openHelp(m);
  await until(() => find(m.root, "aside.help").length === 1, "Help");
  assert.deepEqual(m.log.actions, [null], "the root list");
  assert.match(text(find(m.root, "aside.help")[0]), /Refresh/);
  m.store.setUi({ help: false, selected: member("implementor-1") });
  await until(() => find(m.root, "aside.help").length === 0, "Help closed");
  await openHelp(m);
  await until(() => /Show mail/.test(text(find(m.root, "aside.help")[0] ?? { textContent: "" })), "the member's list");
  assert.match(text(find(m.root, "aside.help")[0]), /not available in the web/, "an entry the web cannot run gives its reason");
});

test("the ? key toggles Help except in a text field or with a modifier", () => {
  const calls = [];
  const store = { state: { ui: {} }, setUi: (p) => calls.push(p) };
  assert.equal(helpKey({ key: "?", target: { localName: "div" } }, store), true);
  assert.deepEqual(calls, [{ help: true }]);
  store.state.ui = { help: true };
  helpKey({ key: "?", target: { localName: "div" } }, store);
  assert.deepEqual(calls.at(-1), { help: false });
  const n = calls.length;
  for (const target of [{ localName: "input" }, { localName: "textarea" }, { localName: "select" }, { isContentEditable: true }]) {
    assert.equal(helpKey({ key: "?", target }, store), false);
  }
  assert.equal(helpKey({ key: "?", target: { localName: "div" }, ctrlKey: true }, store), false);
  assert.equal(helpKey({ key: "/", target: { localName: "div" } }, store), false);
  assert.equal(calls.length, n);
  assert.equal(isTextTarget({ localName: "button" }), false);
});

test("the page posts nothing for an action: opening a panel and running a copy are GETs through the one reader", async () => {
  const m = await mount();
  await openPanel(m);
  label(m, "Copy name").fire("click");
  await settle();
  assert.deepEqual(m.log.actions, [member("implementor-1")], "one read, no other call");
});
