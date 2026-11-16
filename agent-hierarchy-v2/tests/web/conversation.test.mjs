import { test } from "node:test";
import assert from "node:assert/strict";
import { countRenders, settle, installDom } from "./dom.mjs";
import { mountApp as mount, until, find, text, member, DIR as D } from "./mount-app.mjs";

const { transcriptRows, filterRows, tokensPerTurn, pruneCache, isEnded, PAGE, TranscriptPane } = await import("../../web/ui/conversation.js");
const { render } = await import("../../web/ui/h.js");
const { MAX_WINDOWS } = await import("../../web/ui/store.js");
const { ROW_HEIGHT, VIEW_HEIGHT, OVERSCAN } = await import("../../web/ui/rows.js");
const { buildTranscript, transcriptWindow, servedEntry } = await import("../../web/mock/transcripts.mjs");
const rowsOf = (entries) => transcriptRows(entries.map(servedEntry));
const { envelope } = await import("../../web/mock/envelope.mjs");
const { readFileSync } = await import("node:fs");

const tid = (n) => `transcript:${D}/sess-${n}`;

test("tool presentation states are exclusive and thinking indicators stay on thinking rows", () => {
  const pane = new TranscriptPane();
  pane.rows = [
    { key: "run", kind: "tool", label: "pending", done: false, error: false, expandable: true },
    { key: "ok", kind: "tool", label: "passed", done: true, error: false, expandable: true },
    { key: "err", kind: "tool", label: "failed", done: true, error: true, expandable: true },
    { key: "think", kind: "thinking", label: "thinking" },
    { key: "other", kind: "other", label: "other" },
  ];
  const root = installDom();
  render(pane.draw({ store: {} }, { filter: "all", query: "", turns: 0, pathOk: true }), root);
  assert.deepEqual(find(root, ".res").map((el) => el.getAttribute("class")), ["chip res run", "chip res ok", "chip res err"]);
  assert.equal(find(root, ".thinking-dots").length, 1);
  assert.equal(find(find(root, ".k-thinking")[0], "i").length, 3);
  assert.equal(find(find(root, ".k-other")[0], ".thinking-dots").length, 0);
  assert.equal(find(find(root, ".k-tool")[0], ".thinking-dots").length, 0);
  render(null, root);
});

async function openConversation(m, name = "architect") {
  m.store.setUi({ view: "conversation", selected: member(name) });
  await until(() => find(m.root, "li.tr-row").length > 0, "transcript rows");
  await settle();
}

// ---- rows

const chron = buildTranscript("x", 3);

test("rows: a user text opens a numbered turn, a brief card is told apart, a tool call carries its result's chip", () => {
  const rows = rowsOf(chron);
  const turns = rows.filter((r) => r.kind === "turn" || r.kind === "brief");
  assert.deepEqual(turns.map((r) => [r.kind, r.turn]), [["brief", 1], ["turn", 2], ["turn", 3]]);
  const tool = rows.find((r) => r.kind === "tool");
  assert.equal(tool.done, true);
  assert.equal(tool.error, false);
  assert.equal(tool.lines, 10, "the chip carries the result's line count, not its byte size");
  assert.equal(turns[0].seconds, 8, "the divider carries the server's turn_ms");
  const plain = rowsOf([{ offset: 0, bytes: 9, type: "user", blocks: [{ kind: "text", excerpt: "not a turn start" }] }]);
  assert.deepEqual(plain.map((r) => r.kind), ["text"], "a user text the server did not mark turn_start is no divider");
  assert.equal(rows.some((r) => r.kind === "result"), false, "a loaded result is shown on its call, not alone");
  assert.ok(rows.some((r) => r.kind === "thinking"));
});

test("rows: a result whose call is not loaded stands alone, a call without a result is running, an oversize entry is a large-entry row", () => {
  const onlyResult = chron.filter((e) => e.blocks.some((b) => b.kind === "tool_result")).slice(0, 1);
  assert.deepEqual(rowsOf(onlyResult).map((r) => r.kind), ["result"]);
  const call = chron.filter((e) => e.blocks.some((b) => b.kind === "tool_use")).slice(0, 1);
  const running = rowsOf(call).find((r) => r.kind === "tool");
  assert.equal(running.done, false);
  const big = rowsOf([{ offset: 0, type: "oversize", bytes: 5_000_000 }]);
  assert.deepEqual(big.map((r) => [r.kind, r.label, r.expandable]), [["large", "entry too large (5.0 MB)", undefined]], "a stub has no blocks field");
  const notStub = rowsOf([{ offset: 0, bytes: 5_000_000, type: "assistant", blocks: [{ kind: "text", excerpt: "hi" }] }]);
  assert.deepEqual(notStub.map((r) => r.kind), ["text"], "a big entry that is not a stub is an ordinary row");
});

test("filters keep their kinds only, Errors keeps failed calls, and search matches the loaded rows' text", () => {
  const entries = buildTranscript("x", 8);
  const rows = rowsOf(entries);
  assert.ok(filterRows(rows, "text", "").every((r) => ["turn", "brief", "text"].includes(r.kind)));
  assert.ok(filterRows(rows, "tools", "").every((r) => ["tool", "result"].includes(r.kind)));
  const errors = filterRows(rows, "errors", "");
  assert.ok(errors.length > 0 && errors.every((r) => r.error === true));
  assert.ok(errors.length < filterRows(rows, "tools", "").length, "an ok call is not an error");
  assert.deepEqual(filterRows(rows, "all", "step 3").every((r) => r.label.toLowerCase().includes("step 3")), true);
  assert.equal(filterRows(rows, "all", "no such text anywhere").length, 0);
  assert.equal(filterRows(rows, "all", "  ").length, rows.length, "a blank search keeps everything");
});

test("a message over several lines counts once; negative: another message, or a line with no id, counts", () => {
  const said = (message_id) => ({ message_id, usage: { input_tokens: 10, output_tokens: 1 } });
  const per = tokensPerTurn([{ turn_start: true }, said("m1"), said("m1"), said("m2"), said(undefined), said(undefined)]);
  assert.equal(per.average, 44);
});

test("tokens per turn are input plus output over the loaded turns", () => {
  const per = tokensPerTurn(buildTranscript("x", 2));
  assert.equal(per.turns, 2);
  assert.ok(per.average > 0);
  assert.equal(per.seconds, 8, "turn_ms of 8000 and 8500 average to 8 s");
  assert.deepEqual(tokensPerTurn([]), { turns: 0, average: null, seconds: null });
  const untimed = buildTranscript("x", 2).map((e) => (e.turn_start ? { ...e, turn_ms: null } : e));
  assert.equal(tokensPerTurn(untimed).seconds, null, "turns without a duration give no seconds");
  const one = buildTranscript("x", 2).map((e, i) => (e.turn_start && e.turn_ms === 8000 ? { ...e, turn_ms: null } : e));
  assert.equal(tokensPerTurn(one).seconds, 9, "only timed turns are averaged (8500 rounds to 9)");
});

test("the cache keeps a body only while its entry is held", () => {
  const cache = new Map([[0, "a"], [100, "b"]]);
  pruneCache(cache, [{ offset: 100 }, { offset: 200 }]);
  assert.deepEqual([...cache.keys()], [100]);
});

test("the rail keeps a name containing the old separators intact", async () => {
  const m = await mount();
  await openConversation(m);
  const id = member("reviewer");
  const slice = m.store.state.slices.get(id);
  m.model.publish([{ ...slice, body: { ...slice.body, name: "re|view\nr" } }]);
  await until(() => find(m.root, "li.rail-row").some((li) => text(li).includes("re|view")), "the renamed row");
  assert.equal(find(m.root, "li.rail-row").length, 8, "no row was split");
});

test("the rail marks the member on show with nothing selected, and says a state that is not working", async () => {
  const m = await mount();
  await openConversation(m);
  const rows = find(m.root, "li.rail-row");
  assert.equal(rows.filter((li) => li.attrs.get("class").includes("selected")).length, 1, "the first member is on show");
  assert.match(text(rows.find((li) => li.attrs.get("data-state") === "blocked")), /blocked ·/);
  assert.doesNotMatch(text(rows.find((li) => li.attrs.get("data-state") === "working")), /working ·/, "negative: working is the plain case");
});

test("an ended conversation (the header withdrawn) says so and keeps the rows already loaded", async () => {
  const m = await mount();
  await openConversation(m);
  const rows = find(m.root, "li.tr-row").length;
  assert.equal(find(m.root, ".ended").length, 0);
  m.model.endTranscript("architect");
  await until(() => find(m.root, ".ended").length === 1, "conversation ended");
  assert.match(text(find(m.root, ".ended")[0]), /conversation ended/);
  assert.equal(find(m.root, "li.tr-row").length, rows, "the loaded rows stay");
});

test("ended means a tombstone: a header that is merely absent (an epoch reload) is not an end", async () => {
  assert.equal(isEnded({ slices: new Map(), gone: new Set() }, "transcript:d/s"), false, "absent, no tombstone");
  assert.equal(isEnded({ slices: new Map() }, "transcript:d/s"), false, "no tombstone record at all");
  assert.equal(isEnded({ slices: new Map(), gone: new Set(["transcript:d/s"]) }, "transcript:d/s"), true);
  assert.equal(isEnded({ gone: new Set(["transcript:d/other"]) }, "transcript:d/s"), false, "another id's tombstone");
  assert.equal(isEnded({ gone: new Set(["x"]) }, null), false);
  const m = await mount();
  await openConversation(m);
  m.model.restart();
  const seen = m.store.state.epoch;
  await until(() => m.store.state.epoch !== seen, "the new epoch");
  assert.equal(m.store.state.gone.size, 0, "a reload clears the tombstones it never saw");
});

test("paused through 200 appended turns: the store keeps its windows, the rows under the reader keep their elements, and the count says N+", async () => {
  const m = await mount();
  await openConversation(m);
  find(m.root, ".tr-scroll")[0].fire("scroll", { currentTarget: { scrollTop: 100 } });
  await until(() => find(m.root, ".jump").length === 1, "Paused");
  const lid = `list:transcript:${D}/sess-architect`;
  const firstRow = find(m.root, "li.tr-row")[0];
  const label = firstRow.textContent;
  const heldBefore = m.store.state.lists.get(lid).windows[0].items.at(-1).offset;
  for (let round = 0; round < 8; round += 1) {
    for (let i = 0; i < 25; i += 1) {
      m.model.appendTranscript("architect");
    }
    await new Promise((r) => setTimeout(r, 60));
  }
  await until(() => /\+ new/.test(text(find(m.root, ".jump")[0])), "an N+ count");
  const windows = m.store.state.lists.get(lid).windows;
  assert.ok(windows.length <= MAX_WINDOWS, "never more than the store's capacity");
  assert.ok(windows.some((w) => w.items.some((e) => e.offset === heldBefore)), "the window the reader was on is still held");
  assert.equal(find(m.root, "li.tr-row")[0], firstRow, "the first visible row is the same element");
  assert.equal(find(m.root, "li.tr-row")[0].textContent, label);
  assert.ok(m.log.lists.filter((l) => l.path === lid && l.params.before).length <= MAX_WINDOWS - 1, "at most the capacity's worth of newer pages was read");
});

test("an empty `before` page adds no window (a live tick with nothing new)", async () => {
  const m = await mount();
  await openConversation(m);
  const lid = `list:transcript:${D}/sess-architect`;
  const windows = m.store.state.lists.get(lid).windows.length;
  const head = m.store.state.lists.get(lid).windows[0];
  await m.store.page(lid, { before: head.prev, limit: PAGE });
  assert.equal(m.store.state.lists.get(lid).windows.length, windows);
});

test("the mock's transcript pages follow the cursor rules: prev is never null on a page with rows, an empty page echoes the asked cursor", () => {
  const entries = buildTranscript("x", 4);
  const newest = transcriptWindow(entries, { after: null, before: null, cap: 5 });
  assert.equal(newest.rows.length, 5);
  assert.equal(newest.prev, newest.head_cursor, "at head, prev equals head_cursor and is not null");
  assert.equal(newest.next, String(newest.rows.at(-1).offset));
  const caught = transcriptWindow(entries, { after: null, before: newest.prev, cap: 5 });
  assert.deepEqual(caught.rows, []);
  assert.equal(caught.prev, newest.prev, "an empty before page echoes before");
  assert.equal(caught.next, null);
  const past = transcriptWindow(entries, { after: String(entries[0].offset), before: null, cap: 5 });
  assert.deepEqual(past.rows, []);
  assert.equal(past.next, String(entries[0].offset), "an empty after page echoes after");
  assert.equal(past.prev, null);
  const none = transcriptWindow([], { after: null, before: null, cap: 5 });
  assert.deepEqual([none.next, none.prev, none.head_cursor, none.tail_cursor], [null, null, null, null]);
});

test("catching up with before=prev returns every appended row exactly once, nearest the cursor first", () => {
  const entries = buildTranscript("x", 3);
  const head = transcriptWindow(entries, { after: null, before: null, cap: 5 });
  const had = new Set(entries.map((e) => e.offset));
  const more = buildTranscript("x", 3).map((e) => ({ ...e, offset: e.offset + 1_000_000 }));
  const all = [...entries, ...more];
  const seen = [];
  let cursor = head.prev;
  for (;;) {
    const page = transcriptWindow(all, { after: null, before: cursor, cap: 4 });
    if (page.rows.length === 0) {
      break;
    }
    seen.push(...page.rows.map((e) => e.offset));
    cursor = page.prev;
  }
  const expected = all.filter((e) => e.offset > Number(head.prev)).map((e) => e.offset).sort((a, b) => a - b);
  assert.deepEqual([...seen].sort((a, b) => a - b), expected);
  assert.equal(new Set(seen).size, seen.length);
  assert.ok(!seen.some((o) => had.has(o) && o <= Number(head.prev)));
});

test("a list page is the envelope the server writes, keys and nothing else", () => {
  const keys = JSON.parse(readFileSync(new URL("./goldens/list-envelope-keys.json", import.meta.url), "utf8"));
  assert.deepEqual(Object.keys(transcriptWindow(buildTranscript("x", 2), { after: null, before: null, cap: 5 })), keys);
  assert.deepEqual(Object.keys(envelope({ rows: [] })), keys);
});

test("the view lists the 8 members, leases the chosen transcript, and draws a window of rows", async () => {
  const m = await mount();
  await openConversation(m);
  assert.equal(find(m.root, "li.rail-row").length, 8);
  assert.ok(m.log.polls.some((s) => s?.includes(tid("architect"))), "the poll names the transcript");
  const rows = find(m.root, "li.tr-row").length;
  assert.ok(rows > 0 && rows <= Math.ceil(VIEW_HEIGHT / ROW_HEIGHT) + 2 * OVERSCAN + 1, `${rows} rows mounted`);
  assert.match(text(find(m.root, ".scope")[0]), /searching \d+ loaded turns/);
  assert.equal(find(m.root, ".tr-list")[0].style.paddingBottom, "0px", "following: the window sits at the newest row");
});

test("a transcript read the daemon refuses is said in the pane, not left as an empty view (nearest non-match: a read that works shows no message)", async () => {
  const ok = await mount();
  await openConversation(ok);
  assert.equal(find(ok.root, "section.transcript p.dim").length, 0);

  const m = await mount();
  const list = m.model.list.bind(m.model);
  m.model.list = (id, ...rest) => {
    if (id.startsWith("list:transcript:")) {
      throw new Error("view list: 404");
    }
    return list(id, ...rest);
  };
  m.store.setUi({ view: "conversation", selected: member("architect") });
  await until(() => /could not be read: view list: 404/.test(text(find(m.root, "section.transcript")[0] ?? { textContent: "" })), "the failure line");
});

test("a member with no session says so in the pane instead of showing an empty list", async () => {
  const m = await mount();
  const id = member("reviewer");
  const slice = m.store.state.slices.get(id);
  m.model.publish([{ ...slice, body: { ...slice.body, session: null } }]);
  await until(() => m.store.state.slices.get(id)?.body.session === null, "the member without a session");
  m.store.setUi({ view: "conversation", selected: id });
  await until(() => /no live session/.test(text(find(m.root, "section.transcript")[0] ?? { textContent: "" })), "the no-session line");
  assert.match(text(find(m.root, "section.transcript")[0]), /^reviewer has no live session\. It was last active .+ ago\. Its conversation appears here when it next starts a turn\.$/);
  assert.equal(find(m.root, "li.tr-row").length, 0);
});

test("switching members: A loads, then B loads, then A loads again, each showing its own rows", async () => {
  const m = await mount();
  const pick = async (name) => {
    m.store.setUi({ selected: member(name), view: "conversation" });
    await until(() => find(m.root, "li.tr-row").length > 0 && m.store.state.ui.selected === member(name), `${name}'s rows`);
    await settle();
    return m.store.state.lists.get(`list:transcript:${D}/${m.store.state.slices.get(member(name)).body.session}`)?.windows[0]?.items.length ?? 0;
  };
  assert.ok((await pick("architect")) > 0);
  assert.ok((await pick("reviewer")) > 0);
  assert.ok((await pick("implementor-1")) > 0);
  assert.ok((await pick("architect")) > 0);
  assert.equal(find(m.root, "section.transcript p.dim").length, 0);
});

test("an idle, loaded page shows no loading bar (nearest non-match of the bar that shows while a read is out)", async () => {
  const m = await mount();
  await openConversation(m);
  await until(() => m.store.state.busy === 0 && find(m.root, ".loading-bar").length === 0, "an idle page with no bar");
});

test("a failed page read does not leave the loading bar up", async () => {
  const m = await mount();
  m.model.list = () => {
    throw new Error("view list: 404");
  };
  await assert.rejects(m.store.page("list:transcript:x/y", { limit: 5 }), /404/);
  assert.equal(m.store.state.busy, 0);
});

test("no call reads a whole transcript: every list read is a bounded window or one expand", async () => {
  const m = await mount();
  await openConversation(m);
  const reads = m.log.lists.filter((l) => l.path.includes("list:transcript:"));
  assert.ok(reads.length > 0);
  assert.ok(reads.every((l) => Number(l.params.limit) === PAGE || l.params.expand !== undefined));
});

test("the lease moves with the member and lapses when the view is left", async () => {
  const m = await mount();
  await openConversation(m, "architect");
  await until(() => m.store.state.slices.has(tid("architect")), "the architect header");
  m.store.setUi({ selected: member("reviewer") });
  await until(() => m.store.state.slices.has(tid("reviewer")) && !m.store.state.slices.has(tid("architect")), "the lease to move");
  m.store.setUi({ view: "graph" });
  assert.ok(m.store.state.slices.has(tid("reviewer")), "the selected member's panel still holds its transcript header");
  m.store.setUi({ selected: null });
  await until(() => !m.store.state.slices.has(tid("reviewer")), "the lease to lapse");
});

test("scrolling away from the newest row pauses; new entries grow an unread count and leave the scroll alone; Jump to live resumes", async () => {
  const m = await mount();
  await openConversation(m);
  assert.equal(find(m.root, ".jump").length, 0);
  const pane = find(m.root, ".tr-scroll")[0];
  pane.fire("scroll", { currentTarget: { scrollTop: 100 } });
  await until(() => find(m.root, ".jump").length === 1, "Paused");
  const before = find(m.root, "li.tr-row").map((li) => li.textContent).join("|");
  m.model.appendTranscript("architect");
  await until(() => /\d+\+? new/.test(text(find(m.root, ".jump")[0])), "the unread count");
  assert.equal(find(m.root, "li.tr-row").map((li) => li.textContent).join("|"), before, "the rows under the reader did not move");
  const first = Number(text(find(m.root, ".jump")[0]).match(/(\d+)\+? new/)[1]);
  m.model.appendTranscript("architect");
  await until(() => Number((text(find(m.root, ".jump")[0]).match(/(\d+)\+? new/) ?? [0, 0])[1]) > first, "the count to grow");
  find(m.root, ".jump")[0].fire("click");
  await until(() => find(m.root, ".jump").length === 0, "following again");
});

test("scrolling back to the end resumes following", async () => {
  const m = await mount();
  await openConversation(m);
  const pane = find(m.root, ".tr-scroll")[0];
  pane.fire("scroll", { currentTarget: { scrollTop: 0 } });
  await until(() => find(m.root, ".jump").length === 1, "Paused");
  pane.fire("scroll", { currentTarget: { scrollTop: 1_000_000 } });
  await until(() => find(m.root, ".jump").length === 0, "following");
});

test("opening a tool row fetches its body once (one expand per offset, at most 256 KB) and shows it; a large entry cannot be opened", async () => {
  const m = await mount();
  await openConversation(m);
  const head = find(m.root, "li.k-tool")[0].all("button.tool-head")[0];
  head.fire("click");
  const pres = () => find(m.root, "div.tool-body")[0]?.all("pre") ?? [];
  await until(() => pres().length > 0, "the body");
  const expands = m.log.lists.filter((l) => l.params.expand !== undefined);
  assert.ok(expands.length >= 1 && expands.length <= 2);
  assert.equal(new Set(expands.map((l) => l.params.expand)).size, expands.length, "one fetch per offset");
  assert.ok(text(pres()[0]).length <= 256 * 1024);
  head.fire("click");
  await until(() => find(m.root, "div.tool-body").length === 0, "closed");
  head.fire("click");
  await until(() => pres().length > 0, "reopened");
  assert.equal(m.log.lists.filter((l) => l.params.expand !== undefined).length, expands.length, "the second open is served from the cache");
  assert.ok(find(m.root, "li.k-large").every((li) => li.all("button").length === 0), "a large entry has no open button");
});

test("an expand the server refuses as over its cap says the entry is too large to expand; an ordinary body still shows its text", async () => {
  const m = await mount();
  await openConversation(m);
  const real = m.store.expand;
  m.store.expand = async () => ({ type: "other", blocks: [], truncated: true });
  find(m.root, "li.k-tool")[0].all("button.tool-head")[0].fire("click");
  const body = () => find(m.root, "div.tool-body")[0];
  await until(() => /entry too large to expand/.test(text(body() ?? { textContent: "" })), "the refusal");
  assert.equal(body().all("pre").length, 0);
  find(m.root, "li.k-tool")[0].all("button.tool-head")[0].fire("click");
  await until(() => find(m.root, "div.tool-body").length === 0, "closed");
  m.store.expand = real;
  find(m.root, "li.k-tool")[1].all("button.tool-head")[0].fire("click");
  await until(() => (find(m.root, "li.k-tool")[1].all("pre")).length > 0, "an ordinary body");
  assert.equal(/too large/.test(text(find(m.root, "li.k-tool")[1])), false);
});

test("an opened tool row reads each of its bodies once, not on every redraw", async () => {
  const m = await mount();
  await openConversation(m);
  const real = m.store.expand;
  const calls = new Map();
  m.store.expand = (id, offset) => {
    calls.set(offset, (calls.get(offset) ?? 0) + 1);
    if (calls.get(offset) > 3) {
      throw new Error(`offset ${offset} was fetched again and again`);
    }
    return real(id, offset);
  };
  find(m.root, "li.k-tool")[0].all("button.tool-head")[0].fire("click");
  await until(() => find(m.root, "li.k-tool")[0].all("pre").length > 0, "the body");
  await settle();
  assert.ok(calls.size > 0 && [...calls.values()].every((n) => n === 1), `one read per offset: ${JSON.stringify([...calls])}`);
});

test("search narrows the rows and says it covers the loaded turns", async () => {
  const m = await mount();
  await openConversation(m);
  const before = find(m.root, "li.tr-row").length;
  m.store.setUi({ convQuery: "step 17" });
  await until(() => find(m.root, "li.tr-row").length < before, "fewer rows");
  assert.ok(find(m.root, "li.tr-row").every((li) => /step 17/i.test(text(li))));
  assert.match(text(find(m.root, ".scope")[0]), /loaded turns/);
  m.store.setUi({ convQuery: "" });
  m.store.setUi({ convFilter: "errors" });
  await until(() => find(m.root, "li.tr-row").every((li) => li.attrs.get("class").includes("err")), "only errors");
});

test("the aside shows the member's changed files and tests from the leased slices", async () => {
  const m = await mount();
  await openConversation(m);
  const items = (cls) => find(m.root, `ul.${cls}`)[0]?.all("li") ?? [];
  await until(() => items("files").length === 3, "the files");
  await until(() => items("tests").length === 2, "the tests");
  assert.match(text(find(m.root, "aside.ctx")[0]), /over \d+ loaded turns/);
});

test("C1: an activity change of a member not shown draws the rail and nothing in the transcript", async () => {
  const m = await mount();
  await openConversation(m, "reviewer");
  await until(() => (find(m.root, "ul.files")[0]?.all("li") ?? []).length === 3, "the aside");
  const counter = countRenders();
  const seq = m.store.state.seq;
  m.model.tick();
  await until(() => m.store.state.seq > seq, "the change");
  await settle();
  const drawn = Object.fromEntries([...counter.counts].sort());
  counter.stop();
  assert.deepEqual(drawn, { Footer: 1, MemberRail: 1 }, "the changed member's state and sub-label are in the rail, its state in the status bar's counts; nothing else draws");
});

test("C1: a store change that touches nothing the rail shows does not draw the rail, and negative a change of selection does", async () => {
  const m = await mount();
  await openConversation(m, "reviewer");
  await until(() => (find(m.root, "ul.files")[0]?.all("li") ?? []).length === 3, "the aside");
  const counter = countRenders();
  m.store.setUi({ hover: null });
  await settle();
  const quiet = counter.counts.get("MemberRail") ?? 0;
  m.store.setUi({ selected: member("architect") });
  await settle();
  const moved = counter.counts.get("MemberRail") ?? 0;
  counter.stop();
  assert.equal(quiet, 0);
  assert.ok(moved >= 1);
});

test("C3: the transcript mounts a window of rows, however many are loaded", async () => {
  const m = await mount();
  await openConversation(m);
  const lid = `list:${tid("architect")}`;
  await m.store.page(lid, { limit: 20 });
  for (let i = 0; i < 2; i += 1) {
    await m.store.page(lid, { after: m.store.state.lists.get(lid).windows.at(-1).next, limit: 20 });
  }
  await settle();
  assert.equal(m.store.state.lists.get(`list:${tid("architect")}`).windows.length, 3);
  assert.ok(find(m.root, "li.tr-row").length <= Math.ceil(VIEW_HEIGHT / ROW_HEIGHT) + 2 * OVERSCAN + 1);
});
