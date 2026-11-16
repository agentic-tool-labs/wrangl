import { test } from "node:test";
import assert from "node:assert/strict";
import { installDom } from "./dom.mjs";
import { fixture } from "./lib.mjs";
import { TranscriptPane } from "../../web/ui/conversation.js";
import { render } from "../../web/ui/h.js";
import { buildTranscript, expandOf, MAX_EXPAND } from "../../web/mock/transcripts.mjs";

const daemon = fixture("transcript-expand-daemon.json");

const ROWS = [
  { key: "120:0", offset: 120, index: 0, kind: "text", label: "Daemon prose (excerpt)", truncated: true },
  { key: "120:2", offset: 120, kind: "tool", id: "edit-1", name: "Edit", label: "Edit", stat: { add: 1, del: 1 }, resultOffset: 120, done: true, error: false, lines: 3 },
  { key: "120:4", offset: 120, kind: "result", id: "other-1", name: "result", label: "result · 3 lines", done: true, error: true },
];

// Every row open over one cached entry body.
function bodyView(body) {
  const pane = new TranscriptPane();
  pane.rows = ROWS;
  pane.cache.set(120, body);
  pane.state = { ...pane.state, open: new Set(ROWS.map((r) => r.key)) };
  const root = installDom();
  render(pane.draw({ store: {} }, { filter: "all", query: "", turns: 0, pathOk: true }), root);
  return root;
}

test("E1: daemon expand fields render prose, the input-derived diff and both result content shapes", () => {
  const root = bodyView(daemon);
  assert.equal(root.all("p.prose")[0].textContent, "Daemon prose", "the full text replaces the excerpt");
  assert.deepEqual(root.all("pre").map((el) => [el.getAttribute("class"), el.textContent]), [
    ["tr-block diff", "old\nnew\n"],
    ["tr-block out", "stdout\n+plain\n-error"],
    ["tr-block out err", "first\n[image]\nlast"],
  ]);
  render(null, root);
});

test("the mock matches the daemon fixture's block keys and supplies structured tool inputs and diff stats", () => {
  const keys = (value) => Object.keys(value).sort();
  const blockKeys = new Map(daemon.blocks.map((block) => [block.kind, keys(block)]));
  const entries = buildTranscript("fixture");
  const expanded = entries.filter((entry) => entry.type !== "oversize").map((entry) => expandOf(entries, entry.offset));
  for (const body of expanded) {
    assert.deepEqual(keys(body), keys(daemon));
    assert.equal(body.truncated, false);
    for (const block of body.blocks) assert.deepEqual(keys(block).filter((k) => k !== "diff"), blockKeys.get(block.kind).filter((k) => k !== "diff"), block.kind);
  }
  const calls = entries.flatMap((entry) => entry.blocks.filter((block) => block.kind === "tool_use").map((block) => ({ entry, block })));
  for (const [name, expected] of Object.entries({ Edit: { add: 2, del: 1 }, MultiEdit: { add: 1, del: 3 }, Write: { add: 3, del: 0 } })) {
    const call = calls.find(({ block }) => block.name === name);
    assert.ok(call, name);
    assert.deepEqual(call.block.diff_stat, expected, name);
    assert.equal("input" in call.block, false, "list blocks do not carry expanded input");
    const input = expandOf(entries, call.entry.offset).blocks.find((block) => block.kind === "tool_use").input;
    if (name === "Edit") assert.deepEqual([input.old_string, input.new_string], ["before", "after\nextra"]);
    if (name === "MultiEdit") assert.deepEqual(input.edits, [{ old_string: "one\ntwo", new_string: "replacement" }, { old_string: "removed", new_string: "" }]);
    if (name === "Write") assert.equal(input.content, "alpha\nbeta\ngamma");
  }
  const bash = calls.find(({ block }) => block.name === "Bash");
  assert.equal("diff_stat" in bash.block, false);
  assert.equal(typeof expandOf(entries, bash.entry.offset).blocks.find((block) => block.kind === "tool_use").input.command, "string");
  assert.ok(calls.filter(({ block }) => ["Read", "Grep"].includes(block.name)).every(({ block }) => !("diff_stat" in block)));
  const results = expanded.flatMap((body) => body.blocks).filter((block) => block.kind === "tool_result");
  assert.ok(results.some((block) => typeof block.content === "string"));
  assert.ok(results.some((block) => Array.isArray(block.content) && block.content.every((item) => item.type === "text")));
});

test("E2: the mock caps the whole entry above 256 KiB but accepts the boundary", () => {
  const entries = [
    { offset: 0, bytes: MAX_EXPAND + 1, type: "assistant", blocks: [{ kind: "text", excerpt: "over cap" }] },
    { offset: 1, bytes: MAX_EXPAND, type: "assistant", subtype: "note", blocks: [{ kind: "text", excerpt: "at cap" }] },
  ];
  const oversized = expandOf(entries, 0);
  assert.deepEqual(oversized, { offset: 0, type: "assistant", truncated: true, blocks: [] });
  const root = bodyView(oversized);
  assert.match(root.textContent, /entry too large to expand/);
  assert.equal(root.all("pre").length, 0);
  assert.equal(root.all("p.dim").filter((p) => /too large/.test(p.textContent)).length, 3, "one note per body: the text row, the call and the standalone result");
  render(null, root);
  const boundary = expandOf(entries, 1);
  assert.equal(boundary.truncated, false);
  assert.equal(boundary.subtype, "note");
  assert.equal(boundary.blocks[0].text, "at cap\n(the full text of the block)");
  assert.equal(expandOf(entries, 2), null);
  const mock = buildTranscript("fixture");
  const stub = mock.find((entry) => entry.type === "oversize");
  assert.deepEqual(expandOf(mock, stub.offset), { offset: stub.offset, type: "oversize", truncated: true, blocks: [] });
});

test("expand rendering does not confuse legacy body fields, other kinds or non-array content with daemon text", () => {
  const root = bodyView({ ...daemon, blocks: [
    { kind: "text", body: "legacy prose" },
    { kind: "thinking", body: "legacy thought" },
    { kind: "tool_use", id: "edit-1", name: "Edit", body: "legacy input" },
    { kind: "tool_result", tool_use_id: "edit-1", content: { type: "text", text: "not an array" } },
    { kind: "tool_result", tool_use_id: "other-1", content: { type: "text", text: "not an array" } },
  ] });
  assert.equal(root.all("p.prose")[0].textContent, "Daemon prose (excerpt)", "no text field: the excerpt stays");
  assert.deepEqual(root.all("pre").map((el) => el.textContent), ["", ""], "no diff without input; non-array content is empty");
  assert.doesNotMatch(root.textContent, /legacy|not an array|entry too large to expand/);
  render(null, root);
  const empty = bodyView({ ...daemon, blocks: [] });
  assert.equal(empty.all("pre").length, 0);
  assert.doesNotMatch(empty.textContent, /entry too large to expand/);
  render(null, empty);
});

test("mock expansion preserves unknown kinds and does not share mutable input objects", () => {
  const entries = buildTranscript("fixture");
  const edit = entries.find((entry) => entry.blocks.some((block) => block.kind === "tool_use" && block.name === "Edit"));
  const first = expandOf(entries, edit.offset).blocks.find((block) => block.kind === "tool_use");
  first.input.old_string = "changed by a reader";
  const second = expandOf(entries, edit.offset).blocks.find((block) => block.kind === "tool_use");
  assert.equal(second.input.old_string, "before");
  const unknown = [{ offset: 0, type: "assistant", bytes: 1, blocks: [{ kind: "other", text: "not prose" }, { kind: "tool_use", id: "future", name: "FutureTool" }] }];
  assert.deepEqual(expandOf(unknown, 0).blocks, [{ kind: "other" }, { kind: "tool_use", id: "future", name: "FutureTool", input: {} }]);
});
