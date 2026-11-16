import { test } from "node:test";
import assert from "node:assert/strict";
import { settle } from "./dom.mjs";
import { mountApp, until } from "./mount-app.mjs";
import { mountPane } from "./pane-mount.mjs";

const card = (root, name) => root.all("li.k-tool").findLast((li) => li.all(".tool-name")[0].textContent === name);
const head = (li) => li.all("button.tool-head")[0];
const expands = (m) => m.log.lists.filter((l) => l.params.expand !== undefined).map((l) => l.params.expand);

async function mounted() {
  const m = await mountApp();
  return { m, ...(await mountPane(m, undefined, 1800)) };
}

async function open(root, name) {
  head(card(root, name)).fire("click");
  await until(() => card(root, name).all("pre").length > 0, `${name} body`);
  await settle();
}

// The id of the loaded call to a tool, for a faked result to answer.
const useIdOf = (m, name) => [...m.store.state.lists.values()].flatMap((l) => l.windows.flatMap((w) => w.items)).flatMap((e) => e.blocks ?? []).find((b) => b.kind === "tool_use" && b.name === name).id;

const answerWith = (m, name, content) => {
  const id = useIdOf(m, name);
  m.store.expand = async (_lid, offset) => ({ offset, type: "user", truncated: false, blocks: [{ kind: "tool_result", tool_use_id: id, is_error: false, content }] });
};

test("the header carries chevron, name, summary, chip and stat in order, with its aria state; collapsed by default", async () => {
  const { root, unmount } = await mounted();
  const li = card(root, "Edit");
  const h = head(li);
  assert.deepEqual(h.children.map((c) => c.getAttribute("class").split(" ")[0]), ["chev", "tool-name", "tool-summary", "chip", "tool-stat"]);
  assert.equal(h.getAttribute("aria-expanded"), "false");
  assert.equal(h.getAttribute("aria-label"), "Expand Edit call");
  assert.equal(li.all("div.tool-body").length, 0);
  h.fire("click");
  await until(() => li.all("div.tool-body").length === 1, "open");
  assert.equal(h.getAttribute("aria-expanded"), "true");
  assert.equal(h.getAttribute("aria-label"), "Collapse Edit call");
  assert.match(li.getAttribute("class"), /\bopen\b/);
  unmount();
});

test("C1: an Edit card shows removed lines then added lines; MultiEdit does it per edit; Write only adds; Bash has no diff", async () => {
  const { root, unmount } = await mounted();
  const lines = (name) => card(root, name).all("pre")[0].all("span").map((s) => [s.getAttribute("class"), s.textContent]);
  await open(root, "Edit");
  assert.deepEqual(lines("Edit"), [["ln del", "before\n"], ["ln add", "after\n"], ["ln add", "extra\n"]]);
  assert.equal(card(root, "Edit").all("pre")[0].getAttribute("class"), "tr-block diff");
  await open(root, "MultiEdit");
  assert.deepEqual(lines("MultiEdit"), [["ln del", "one\n"], ["ln del", "two\n"], ["ln add", "replacement\n"], ["ln del", "removed\n"]]);
  await open(root, "Write");
  assert.deepEqual(lines("Write"), [["ln add", "alpha\n"], ["ln add", "beta\n"], ["ln add", "gamma\n"]]);
  await open(root, "Bash");
  assert.equal(card(root, "Bash").all("pre.diff").length, 0);
  assert.equal(card(root, "Bash").all("span").filter((s) => s.getAttribute("class")?.startsWith("ln")).length, 0);
  unmount();
});

test("C2: an error result marks its whole block and no line; a Bash line starting + or - keeps the default style", async () => {
  const { m, root, unmount } = await mounted();
  await open(root, "MultiEdit");
  const out = card(root, "MultiEdit").all("pre.out")[0];
  assert.equal(out.getAttribute("class"), "tr-block out err");
  assert.equal(out.all("span").length, 0, "no line carries a class");
  answerWith(m, "Bash", "+added\n-removed\nerror: nope");
  await open(root, "Bash");
  const bash = card(root, "Bash").all("pre.out")[0];
  assert.equal(bash.getAttribute("class"), "tr-block out", "an ok result has no error variant");
  assert.equal(bash.all("span").length, 0);
  assert.equal(bash.textContent, "+added\n-removed\nerror: nope");
  unmount();
});

test("C3: two cards open at once fetch each offset once, and a re-open is served from the cache", async () => {
  const { m, root, unmount } = await mounted();
  await open(root, "Edit");
  await open(root, "Bash");
  assert.equal(card(root, "Edit").all("div.tool-body").length + card(root, "Bash").all("div.tool-body").length, 2);
  const first = expands(m);
  assert.ok(first.length >= 2 && first.length <= 4, `${first.length} fetches`);
  assert.equal(new Set(first).size, first.length, "no offset twice");
  head(card(root, "Edit")).fire("click");
  await settle();
  head(card(root, "Edit")).fire("click");
  await settle();
  await settle();
  assert.deepEqual(expands(m), first);
  unmount();
});

test("C4: a body over 400 lines shows 400 and a show-all control that shows all; exactly 400 has no control", async () => {
  const many = (n) => Array.from({ length: n }, (_, i) => `row ${i}`).join("\n");
  const first = await mounted();
  answerWith(first.m, "Bash", many(450));
  await open(first.root, "Bash");
  const pre = () => card(first.root, "Bash").all("pre")[0];
  assert.equal(pre().textContent.split("\n").length, 400);
  const more = card(first.root, "Bash").all("button.show-all");
  assert.equal(more.length, 1);
  assert.equal(more[0].textContent, "Show all 450 lines");
  more[0].fire("click");
  await settle();
  assert.equal(pre().textContent.split("\n").length, 450);
  assert.equal(card(first.root, "Bash").all("button.show-all").length, 0);
  first.unmount();
  const exact = await mounted();
  answerWith(exact.m, "Bash", many(400));
  await open(exact.root, "Bash");
  assert.equal(card(exact.root, "Bash").all("pre")[0].textContent.split("\n").length, 400);
  assert.equal(card(exact.root, "Bash").all("button.show-all").length, 0, "400 lines is not over the cap");
  exact.unmount();
});

test("C5: there is no detail aside", async () => {
  const { root, unmount } = await mounted();
  await open(root, "Edit");
  assert.equal(root.all("aside.tr-detail").length, 0);
  unmount();
});

test("C6: the header shows +a −d exactly when the row has diff_stat", async () => {
  const { root, unmount } = await mounted();
  const cards = root.all("li.k-tool");
  assert.ok(cards.length >= 6);
  for (const li of cards) {
    const name = li.all(".tool-name")[0].textContent;
    assert.equal(li.all(".tool-stat").length, ["Edit", "Write", "MultiEdit"].includes(name) ? 1 : 0, name);
  }
  const stat = (name) => card(root, name).all(".tool-stat")[0].textContent;
  assert.deepEqual([stat("Edit"), stat("Write"), stat("MultiEdit")], ["+2 −1", "+3 −0", "+1 −3"]);
  unmount();
});

test("a text row served as cut offers the full text; one that is not does not", async () => {
  const { pane, root, unmount } = await mounted();
  const entry = [...pane.entries].reverse().find((e) => e.blocks.some((b) => b.kind === "text") && e.turn_start !== true);
  const row = entry.rows.find((r) => r.kind === "text");
  const redraw = async (cut) => {
    row.cut = cut;
    pane.forceUpdate();
    await settle();
  };
  assert.equal(root.all("button.show-full").length, 0);
  await redraw(true);
  assert.equal(root.all("button.show-full").length, 1, "served as cut");
  await redraw(false);
  assert.equal(root.all("button.show-full").length, 0, "served as whole, whatever its length");
  await redraw(true);
  root.all("button.show-full")[0].fire("click");
  await until(() => root.all("p.prose").some((p) => p.textContent.includes("(the full text of the block)")), "the full text");
  assert.equal(root.all("button.show-full").length, 0);
  unmount();
});

const answerCall = (m, name, input) => {
  const id = useIdOf(m, name);
  const real = m.store.expand;
  m.store.expand = async (lid, offset) => {
    const body = await real(lid, offset);
    return { ...body, blocks: body.blocks.map((b) => (b.kind === "tool_use" && b.id === id ? { ...b, input } : b)) };
  };
};

test("a non-diff card shows its input first: Bash as $ command, other tools as JSON; Edit, MultiEdit and Write have none", async () => {
  const { root, unmount } = await mounted();
  await open(root, "Bash");
  await open(root, "Grep");
  await open(root, "Edit");
  const classes = (name) => card(root, name).all("pre").map((p) => p.getAttribute("class"));
  await until(() => classes("Bash").length === 2 && classes("Grep").length === 2, "input and output");
  assert.deepEqual(classes("Bash"), ["tr-block in", "tr-block out"], "the input comes before the output");
  assert.equal(card(root, "Bash").all("pre.in")[0].textContent, "$ printf 'mock output\\n'");
  assert.equal(card(root, "Grep").all("pre.in")[0].textContent, JSON.stringify({ pattern: "mock", path: "/work/ah" }, null, 2));
  assert.equal(card(root, "Edit").all("pre.in").length, 0);
  for (const name of ["MultiEdit", "Write"]) {
    await open(root, name);
    assert.equal(card(root, name).all("pre.in").length, 0, name);
  }
  unmount();
});

test("a running tool shows its input; the input block is capped at 400 lines like the others", async () => {
  const { m, root, pane, unmount } = await mounted();
  const id = useIdOf(m, "Bash");
  const resultEntry = pane.entries.find((e) => e.blocks.some((b) => b.kind === "tool_result" && b.tool_use_id === id));
  resultEntry.blocks = [];
  resultEntry.rows = [];
  pane.forceUpdate();
  await settle();
  assert.match(card(root, "Bash").all(".chip")[0].textContent, /running/);
  const many = Array.from({ length: 450 }, (_, i) => `echo ${i}`).join("\n");
  answerCall(m, "Bash", { command: many });
  await open(root, "Bash");
  const li = card(root, "Bash");
  assert.deepEqual(li.all("pre").map((p) => p.getAttribute("class")), ["tr-block in"], "no result, no output block");
  assert.equal(li.all("pre")[0].textContent.split("\n").length, 400);
  assert.equal(li.all("button.show-all")[0].textContent, "Show all 450 lines");
  unmount();
});

test("rows without a body never open: their label is no button and a click leaves no open class", async () => {
  const { root, unmount } = await mounted();
  for (const kind of ["turn", "brief", "thinking", "other", "large"]) {
    for (const li of root.all(`li.k-${kind}`)) {
      assert.equal(li.all("button").length, 0, kind);
      li.all(".label")[0].fire("click");
      await settle();
      assert.doesNotMatch(li.getAttribute("class"), /\bopen\b/, kind);
    }
  }
  assert.ok(root.all("li.k-turn").length > 0 && root.all("li.k-thinking").length > 0);
  unmount();
});

test("a turn or brief row served as cut offers its full text too; an uncut one does not", async () => {
  const { pane, root, unmount } = await mounted();
  const entry = [...pane.entries].reverse().find((e) => e.blocks.some((b) => b.kind === "text") && e.turn_start !== true);
  entry.turn_start = true;
  const row = entry.rows.find((r) => r.kind === "text");
  row.kind = "turn";
  const turnRows = () => root.all("li").filter((li) => /\bk-(turn|brief)\b/.test(li.getAttribute("class") ?? ""));
  const turnRow = () => turnRows().find((li) => li.all("button.show-full").length > 0);
  const redraw = async (cut) => {
    row.cut = cut;
    pane.forceUpdate();
    await settle();
  };
  await redraw(false);
  assert.equal(turnRow(), undefined, "not served as cut");
  await redraw(true);
  const li = turnRow();
  assert.ok(li, "the cut turn row has the control");
  assert.equal(li.all(".turn-no").length, 1, "it keeps its turn number");
  li.all("button.show-full")[0].fire("click");
  await until(() => turnRows().some((r) => r.all(".label")[0].textContent.includes("(the full text of the block)")), "the full text");
  assert.equal(turnRow(), undefined);
  unmount();
});

test("an open cut text row says why its full text is missing: a failed fetch, an entry too large; a loaded one shows no note", async () => {
  const cases = [
    [async () => { throw new Error("down"); }, "body unavailable"],
    [async (_lid, offset) => ({ offset, type: "assistant", truncated: true, blocks: [] }), "entry too large to expand"],
  ];
  for (const [expand, note] of cases) {
    const { m, pane, root, unmount } = await mounted();
    const entry = [...pane.entries].reverse().find((e) => e.blocks.some((b) => b.kind === "text") && e.turn_start !== true);
    entry.rows.find((r) => r.kind === "text").cut = true;
    m.store.expand = expand;
    pane.forceUpdate();
    await settle();
    root.all("button.show-full")[0].fire("click");
    await until(() => root.all("p.note").some((p) => p.textContent === note), note);
    unmount();
  }
  const { pane, root, unmount } = await mounted();
  const entry = [...pane.entries].reverse().find((e) => e.blocks.some((b) => b.kind === "text") && e.turn_start !== true);
  entry.rows.find((r) => r.kind === "text").cut = true;
  pane.forceUpdate();
  await settle();
  root.all("button.show-full")[0].fire("click");
  await until(() => root.all("p.prose").some((p) => p.textContent.includes("(the full text of the block)")), "the full text");
  assert.equal(root.all("p.note").length, 0);
  unmount();
});

