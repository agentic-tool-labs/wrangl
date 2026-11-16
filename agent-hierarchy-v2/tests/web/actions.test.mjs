import { test } from "node:test";
import assert from "node:assert/strict";
import { fixture } from "./lib.mjs";
import { render as toString } from "./vendor/preact-render-to-string.js";
import { html } from "../../web/ui/h.js";
import { ActionBar, Help, plan, perform, getTarget, BROWSER } from "../../web/ui/actions.js";

const draw = (vnode) => toString(vnode);
const HOSTILE = "<img src=x onerror=alert(1)>";
// True when a real tag carries an on* attribute (quoted values are skipped, so escaped text is not a handler).
const TAG = /<[a-z][a-z0-9-]*((?:\s+[^\s=>"']+(?:=(?:"[^"]*"|'[^']*'|[^\s>"']+))?)*)\s*\/?>/gi;
const ATTR = /\s+([^\s=>"']+)(?:=(?:"[^"]*"|'[^']*'|[^\s>"']+))?/g;
const liveHandler = (markup) => [...markup.matchAll(TAG)].some((tag) => [...tag[1].matchAll(ATTR)].some((a) => /^on/i.test(a[1])));

const memberActions = () => fixture("actions-member.json").body.actions;

const clip = () => {
  const written = [];
  return { written, clipboard: { writeText: async (t) => written.push(t) }, go: () => assert.fail("no navigation"), view: {} };
};
const byId = (id, list = memberActions()) => list.find((a) => a.id === id);

test("a copy action writes its served text to the clipboard", async () => {
  const env = clip();
  const result = await perform(byId("member.copy_pid"), env);
  assert.deepEqual(env.written, ["4242"]);
  assert.equal(result.done, true);
});

test("an ask-the-orchestrator action copies the command, not the request text", async () => {
  const env = clip();
  await perform(byId("member.ask_dismiss"), env);
  assert.deepEqual(env.written, ["ah roster dismiss impl --plan --team alpha"]);
});

test("the daemon serves no terminal or pane action, and one from an older daemon is disabled and does nothing", async () => {
  const served = [...memberActions(), ...fixture("actions-orchestrator.json").body.actions];
  assert.equal(served.filter((a) => a.group === "terminal" || a.invoke?.terminal !== undefined || a.invoke?.get?.startsWith("/pane/")).length, 0);
  const NOT_IN_WEB = { enabled: false, reason: "not available in the web" };
  assert.deepEqual(plan({ invoke: { terminal: "live" } }), NOT_IN_WEB);
  assert.deepEqual(plan({ invoke: { get: "/pane/o/o.101" } }, { node: "/o/o.101" }), NOT_IN_WEB);
  const env = clip();
  assert.equal((await perform({ invoke: { terminal: "live" } }, env)).done, false);
  assert.deepEqual(env.written, []);
});

const MEMBER = "/o/o.101/t/boxes/m/impl";
const ORCH = "/o/o.101";
const memberEnv = (extra = {}) => ({ node: MEMBER, view: {}, ...extra });

test("status and stream status are not available in the web", () => {
  const orch = fixture("actions-orchestrator.json").body.actions;
  assert.equal(plan(byId("orchestrator.status", orch), { node: ORCH }).enabled, false);
  assert.match(plan(byId("orchestrator.status", orch), { node: ORCH }).reason, /not available in the web/);
  assert.equal(plan({ invoke: { get: `/stream-status${MEMBER}` } }, memberEnv()).enabled, false);
});

test("a member's own detail, inbox and graph routes are followed; a route naming another node, or a list with no node, is not", () => {
  assert.deepEqual(getTarget(`/detail${MEMBER}`, MEMBER), { go: "member" });
  assert.deepEqual(getTarget(`/inbox${MEMBER}`, MEMBER), { go: "messages" });
  assert.deepEqual(getTarget(`/graph${ORCH}`, ORCH), { go: "graph" });
  assert.equal(getTarget("/detail/o/o.101/t/boxes/m/other", MEMBER), null, "another member");
  assert.equal(getTarget(`/detail${MEMBER}/extra`, MEMBER), null, "a longer path");
  assert.equal(getTarget(`/pane${MEMBER}`, MEMBER), null);
  assert.equal(getTarget(`/detail${MEMBER}`, undefined), null, "the served node is unknown");
  assert.equal(getTarget(`/detail${MEMBER}`, ""), null);
  assert.equal(getTarget("/detail", ""), null, "an empty served path never matches a bare route");
  assert.equal(getTarget(null, MEMBER), null);
  assert.equal(plan(byId("member.detail"), memberEnv()).enabled, true);
  assert.equal(plan(byId("member.mail"), memberEnv()).enabled, true);
  assert.equal(plan(byId("member.detail"), { node: "/o/o.101/t/boxes/m/other", view: {} }).enabled, false, "the list belongs to another node");
  assert.equal(plan(byId("member.detail")).enabled, false, "without the served node nothing is followed");
});

test("performing a followed route asks the page to go to that place, and nothing else navigates", async () => {
  const went = [];
  const env = memberEnv({ clipboard: {}, go: (t) => went.push(t) });
  await perform(byId("member.detail"), env);
  await perform(byId("member.mail"), env);
  const orch = fixture("actions-orchestrator.json").body.actions;
  await perform(byId("orchestrator.explode", orch), { ...env, node: ORCH });
  assert.deepEqual(went, [{ go: "member" }, { go: "messages" }, { go: "graph" }]);
});

test("switch.plan and help are performed when the page registers them; show_subagents and highlight_edges only when it does", () => {
  const orch = fixture("actions-orchestrator.json").body.actions;
  const plain = { node: ORCH, view: {} };
  const planAction = { id: "node.plan", label: "Plan", kind: "view", invoke: { view: "switch.plan", target: ORCH } };
  assert.equal(plan(planAction, plain).enabled, false);
  assert.equal(plan(planAction, { node: ORCH, view: { "switch.plan": () => {} } }).enabled, true);
  assert.equal(plan({ invoke: { view: "help" } }, { view: { help: () => {} } }).enabled, true);
  assert.equal(plan(byId("node.show_subagents", orch), plain).enabled, false);
  assert.equal(plan(byId("node.show_subagents", orch), { node: ORCH, view: { show_subagents: () => {} } }).enabled, true);
  assert.equal(plan(byId("node.highlight_edges", orch), { node: ORCH, view: { show_subagents: () => {} } }).enabled, false, "the other view action is still not registered");
});

test("help shows the reason beside an entry the web cannot run, and nothing extra when no handlers are given", () => {
  const withReasons = draw(html`<${Help} actions=${memberActions()} handlers=${memberEnv()} />`);
  assert.match(withReasons, /not available in the web/);
  assert.doesNotMatch(draw(html`<${Help} actions=${[byId("member.detail")]} handlers=${memberEnv()} />`), /unavailable/, "an entry the web can run has no reason");
  assert.doesNotMatch(draw(html`<${Help} actions=${memberActions()} />`), /class="note unavailable"/);
});

test("a view action is performed only when the page names it; quit and open_file are the browser's", async () => {
  const called = [];
  const env = { clipboard: {}, go() {}, view: { help: async () => called.push("help") } };
  assert.equal(plan({ invoke: { view: "help" } }, env).enabled, true);
  assert.equal(plan({ invoke: { view: "zoom_in" } }, env).enabled, false);
  assert.deepEqual(plan({ invoke: { view: "quit" } }, env), { enabled: false, reason: BROWSER });
  assert.deepEqual(plan({ invoke: { view: "open_file", path: "x" } }, env), { enabled: false, reason: BROWSER });
  await perform({ invoke: { view: "help" } }, env);
  assert.deepEqual(called, ["help"]);
  assert.deepEqual(await perform({ invoke: { view: "zoom_in" } }, env), { done: false, reason: "not available in the web" });
});

test("an unknown invoke is disabled, not run", async () => {
  assert.equal(plan({ invoke: { teleport: 1 } }).enabled, false);
  assert.equal(plan({}).enabled, false);
  assert.equal(plan(null).enabled, false);
  assert.equal((await perform({ invoke: { teleport: 1 } }, clip())).done, false);
});

test("the action bar draws a button per served action, disabled ones with their reason, owner ones marked", () => {
  const markup = draw(html`<${ActionBar} actions=${memberActions()} handlers=${{}} pending=${null} onPick=${() => {}} />`);
  assert.equal((markup.match(/<button/g) ?? []).length, 8);
  assert.match(markup, /disabled[^>]*>.*Show subagents/s);
  assert.match(markup, /action-owner/);
  assert.match(markup, /not available in the web/);
  assert.match(markup, /<kbd>y<\/kbd>/);
  assert.equal(draw(html`<${ActionBar} actions=${[]} />`), "");
});

test("a pending action shows its served confirmation text with Confirm and Cancel; none pending shows none", () => {
  const pending = byId("member.ask_dismiss");
  const markup = draw(html`<${ActionBar} actions=${memberActions()} handlers=${{}} pending=${pending} onPick=${() => {}} onConfirm=${() => {}} onCancel=${() => {}} />`);
  assert.match(markup, /Only the Orchestrator can run this\. This copies the command; nothing is run from here\./);
  assert.match(markup, /Confirm/);
  assert.doesNotMatch(draw(html`<${ActionBar} actions=${memberActions()} handlers=${{}} pending=${null} onPick=${() => {}} />`), /Confirm/);
});

test("help lists each served action's key and label, grouped by kind", () => {
  const markup = draw(html`<${Help} actions=${memberActions()} />`);
  for (const a of memberActions()) {
    assert.ok(markup.includes(a.label.replaceAll("&", "&amp;")) || markup.includes(a.label), a.label);
  }
  assert.ok(markup.indexOf("<h3>read</h3>") < markup.indexOf("<h3>copy</h3>"));
  assert.ok(markup.indexOf("<h3>copy</h3>") < markup.indexOf("<h3>owner</h3>"));
  const orch = draw(html`<${Help} actions=${fixture("actions-orchestrator.json").body.actions} />`);
  assert.match(orch, /<h3>view<\/h3>/);
  assert.doesNotMatch(orch, /<h3>other<\/h3>/);
  assert.match(draw(html`<${Help} actions=${[{ id: "x", label: "Odd", kind: "mystery", invoke: {} }]} />`), /<h3>other<\/h3>/);
  assert.doesNotMatch(draw(html`<${Help} actions=${[]} />`), /<h3>/);
});

test("hostile served strings render as literal text in the action bar and help", () => {
  const actions = memberActions().map((a) => ({ ...a, label: HOSTILE, help: HOSTILE, key: HOSTILE, kind: 'copy" onclick="x', confirm: HOSTILE }));
  const bar = draw(html`<${ActionBar} actions=${actions} handlers=${{}} pending=${actions[0]} onPick=${() => {}} onConfirm=${() => {}} onCancel=${() => {}} />`) + draw(html`<${Help} actions=${actions} />`);
  assert.doesNotMatch(bar, /<img/);
  assert.ok(!liveHandler(bar));
});
