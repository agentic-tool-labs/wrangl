import { test } from "node:test";
import assert from "node:assert/strict";
import { countRenders, settle } from "./dom.mjs";
import { mountApp as mount, until, find, text, member, DIR } from "./mount-app.mjs";

const { placeCard, CARD } = await import("../../web/ui/hover.js");
const { TABS, TAIL } = await import("../../web/ui/expand.js");

const WIN = { innerWidth: 1000, innerHeight: 700 };
const rect = (left, top) => ({ left, top, right: left + 40, bottom: top + 40 });

// ---- where the card goes

test("the card sits beside the element, flips to its left near the right edge, and stays inside the window", () => {
  assert.deepEqual(placeCard(rect(100, 100), WIN, "card"), { x: 148, y: 100 });
  const flipped = placeCard(rect(900, 100), WIN, "card");
  assert.equal(flipped.x, 900 - 8 - CARD.w, "left of the element");
  assert.equal(placeCard(rect(100, 690), WIN, "card").y, 700 - CARD.h - 8, "pulled up from the bottom edge");
  assert.equal(placeCard(rect(100, -50), WIN, "card").y, 0, "never above the window");
});

test("the strip sits below its row and flips above it near the bottom", () => {
  assert.deepEqual(placeCard(rect(50, 100), WIN, "strip"), { x: 50, y: 144 });
  assert.equal(placeCard(rect(50, 650), WIN, "strip").y, 650 - CARD.h - 4);
  assert.equal(placeCard(rect(990, 100), WIN, "strip").x, 1000 - CARD.w - 8, "kept inside on the right");
});

// ---- hover

// A Map leaf names its member in its aria-label ("<role> <name>, <state>, ..."), not in a data-id.
const flowCard = async (m, name) => {
  m.store.setUi({ graphMode: "flow" });
  await until(() => find(m.root, "div.fcard").length === 8, "the Flow cards");
  return find(m.root, "div.fcard").find((c) => c.attrs.get("data-id") === member(name));
};
const node = (m, name) => find(m.root, "g.gm-leaf").find((g) => g.getAttribute("aria-label").split(", ")[0].endsWith(` ${name}`));

test("hovering a graph node shows its card with no request and no subscription change; leaving removes it", async () => {
  const m = await mount();
  await settle();
  const requests = m.log.lists.length;
  const polls = m.log.polls.length;
  const sub = m.log.polls.at(-1);
  node(m, "architect").fire("mouseenter");
  await until(() => find(m.root, "aside.hover-card").length === 1, "the card");
  const card = text(find(m.root, "aside.hover-card")[0]);
  assert.match(card, /architect/);
  assert.match(card, /tokens this turn: —/, "no token source without a lease: a dash");
  await settle();
  assert.equal(m.log.lists.length, requests, "no /list request");
  assert.ok(m.log.polls.slice(polls).every((p) => p === sub), "every poll after the hover names the same subscription");
  assert.equal(m.log.polls.at(-1), sub, "no subscription change");
  node(m, "architect").fire("mouseleave");
  await until(() => find(m.root, "aside.hover-card").length === 0, "the card to go");
});

test("the card shows tokens this turn once the member's transcript header is leased", async () => {
  // Nothing in the Graph view leases the header (the member page and Conversation do); the test holds it as they would.
  const m = await mount();
  m.store.lease("test", [`transcript:${DIR}/sess-architect`]);
  await until(() => m.store.state.slices.has(`transcript:${DIR}/sess-architect`), "the header");
  (await flowCard(m, "architect")).fire("mouseenter");
  await until(() => /tokens this turn: \d+/.test(text(find(m.root, "aside.hover-card")[0] ?? { textContent: "" })), "a token count on the card");
});

test("keyboard focus opens the card and blur closes it", async () => {
  const m = await mount();
  node(m, "reviewer").fire("focusin");
  await until(() => find(m.root, "aside.hover-card").length === 1, "the card on focus");
  node(m, "reviewer").fire("focusout");
  await until(() => find(m.root, "aside.hover-card").length === 0, "the card to go on blur");
});

test("focus on an element inside a timeline lane opens the strip, and the lane itself can take focus", async () => {
  const m = await mount();
  m.store.setUi({ view: "timeline" });
  await until(() => find(m.root, "li.lane").length > 0, "the timeline");
  const lane = find(m.root, "li.lane")[0];
  assert.equal(lane.attrs.get("tabindex"), "0", "a lane is reachable by keyboard");
  const inner = lane.querySelectorAll()[0] ?? lane.childNodes.find((c) => c.nodeType === 1);
  assert.ok(inner, "the lane has an inner element to focus");
  inner.fire("focusin");
  await until(() => find(m.root, "aside.hover-strip").length === 1, "the strip from an inner focus");
  inner.fire("focusout");
  await until(() => find(m.root, "aside.hover-strip").length === 0, "the strip to go on blur");
});

test("switching view closes an open card, and a card whose element left the page is not drawn", async () => {
  const m = await mount();
  node(m, "architect").fire("mouseenter");
  await until(() => find(m.root, "aside.hover-card").length === 1, "the card");
  m.store.setUi({ view: "overview" });
  await until(() => find(m.root, "aside.hover-card").length === 0, "the card to go with the view");
  assert.equal(m.store.state.ui.hover, null);
  await until(() => find(m.root, "li.ov-member").length > 0, "the tree");
  const row = find(m.root, "li.ov-member")[0];
  row.fire("mouseenter");
  await until(() => find(m.root, "aside.hover-strip").length === 1, "the strip");
  row.parentNode.removeChild(row);
  const seq = m.store.state.seq;
  m.model.tick();
  await until(() => m.store.state.seq > seq, "a change");
  await settle();
  assert.equal(find(m.root, "aside.hover-strip").length, 0, "the detached row's strip is gone");
});

test("the card says whether the member's process is running, from the processes entry", async () => {
  const m = await mount();
  node(m, "architect").fire("mouseenter");
  await until(() => find(m.root, "aside.hover-card").length === 1, "the card");
  assert.match(text(find(m.root, "aside.hover-card")[0]), /process: running/);
  node(m, "architect").fire("mouseleave");
  node(m, "ultra-advisor").fire("mouseenter");
  await until(() => /process: /.test(text(find(m.root, "aside.hover-card")[0] ?? { textContent: "" })), "the card");
  assert.match(text(find(m.root, "aside.hover-card")[0]), /process: not running/, "the fixture's gone pid");
});

test("a tree row and a timeline lane open a strip", async () => {
  const m = await mount();
  m.store.setUi({ view: "overview" });
  await until(() => find(m.root, "li.ov-member").length > 0, "the tree");
  find(m.root, "li.ov-member")[0].fire("mouseenter");
  await until(() => find(m.root, "aside.hover-strip").length === 1, "the tree strip");
  find(m.root, "li.ov-member")[0].fire("mouseleave");
  m.store.setUi({ view: "timeline" });
  await until(() => find(m.root, "li.lane").length > 0, "the timeline");
  find(m.root, "li.lane")[0].fire("mouseenter");
  await until(() => find(m.root, "aside.hover-strip").length === 1, "the lane strip");
});

test("C1: an activity change of the hovered member draws the Map and the card, and no other component of the hover", async () => {
  const m = await mount();
  node(m, "architect").fire("mouseenter");
  await until(() => find(m.root, "aside.hover-card").length === 1, "the card");
  await settle();
  const counter = countRenders();
  const seq = m.store.state.seq;
  m.model.tick();
  await until(() => m.store.state.seq > seq, "the change");
  await settle();
  const counts = Object.fromEntries([...counter.counts].sort());
  counter.stop();
  assert.ok((counts.HoverCard ?? 0) <= 1, "at most the card");
  assert.ok((counts.GraphMap ?? 0) <= 1);
});

test("an unhovered member's activity change does not draw the card", async () => {
  const m = await mount();
  await settle();
  const counter = countRenders();
  const seq = m.store.state.seq;
  m.model.tick();
  await until(() => m.store.state.seq > seq, "the change");
  await settle();
  const drew = counter.counts.get("HoverCard") ?? 0;
  counter.stop();
  assert.equal(drew, 0, "a closed card is not redrawn");
});

// ---- click: the panel adds subscriptions

const SESS = (n) => `sess-${n}`;

test("a member's page leases its files and transcript header; leaving the page drops them", async () => {
  const m = await mount();
  const files = `files:${DIR}/ship/architect`;
  const header = `transcript:${DIR}/${SESS("architect")}`;
  assert.equal(m.store.state.slices.has(files), false);
  m.store.setUi({ view: "member", selected: member("architect") });
  await until(() => m.store.state.slices.has(files) && m.store.state.slices.has(header), "files and header leased");
  await until(() => /this turn\s*\d/.test(text(find(m.root, "article.member-page")[0] ?? { textContent: "" })), "tokens from the leased header");
  m.store.setUi({ view: "graph", selected: null });
  await until(() => !m.store.state.slices.has(files) && !m.store.state.slices.has(header), "released on leaving");
});

// ---- expand

async function expand(m, name = "architect") {
  m.store.setUi({ selected: member(name) });
  await until(() => find(m.root, "section.msum").length === 1, "the member summary");
  m.store.setUi({ expand: m.store.state.ui.selected }); // the overlay opens from the Detail action; the rail summary has no Expand button
  await until(() => find(m.root, "section.expand").length === 1, "the overlay");
  await settle();
}

test("a failed tail read is not repeated on every update, only when the transcript's newest entry moves; a stale reply is dropped", async () => {
  const m = await mount();
  const real = m.store.fetchList;
  const tails = [];
  let fail = true;
  m.store.fetchList = (id, params) => {
    if (!id.startsWith("list:transcript:")) {
      return real(id, params);
    }
    tails.push(id);
    return fail ? Promise.reject(new Error("down")) : real(id, params);
  };
  await expand(m);
  const first = tails.length;
  assert.ok(first >= 1, "the overlay asked once");
  for (const t of ["files", "tests", "overview"]) {
    m.store.setUi({ expandTab: t });
    await settle();
  }
  m.model.tick();
  await settle();
  assert.equal(tails.length, first, "updates with the same newest entry do not retry");
  fail = false;
  m.model.appendTranscript("architect");
  await until(() => tails.length > first, "a retry once the newest entry moved");
  assert.equal(tails.length, first + 1, "exactly one retry");
});

test("a tail reply that arrives after a newer read has been answered is dropped", async () => {
  const m = await mount();
  const real = m.store.fetchList;
  const oldDoc = await real(`list:transcript:${DIR}/sess-architect`, { limit: 40 });
  let calls = 0;
  let late;
  m.store.fetchList = (id, params) => {
    if (!id.startsWith("list:transcript:")) {
      return real(id, params);
    }
    calls += 1;
    return calls === 1 ? new Promise((r) => (late = r)) : real(id, params);
  };
  await expand(m);
  m.model.appendTranscript("architect");
  await until(() => calls >= 2, "the second read");
  await settle();
  const fresh = text(find(m.root, "section.expand")[0]);
  late(oldDoc);
  await settle();
  assert.equal(text(find(m.root, "section.expand")[0]), fresh, "the older reply did not replace the newer tail");
});

const tab = (m, label) => find(m.root, "button.etab").find((b) => text(b) === label);

test("Expand opens a dialog with five tabs; Overview shows the cards and a live tail of at most 5 rows", async () => {
  const m = await mount();
  await expand(m);
  assert.deepEqual(find(m.root, "button.etab").map(text), TABS.map((t) => t[1]));
  assert.equal(find(m.root, "section.expand")[0].attrs.get("aria-modal"), "true");
  await until(() => find(m.root, "li.tail-row").length > 0, "the tail");
  assert.ok(find(m.root, "li.tail-row").length <= TAIL);
  assert.match(text(find(m.root, "section.expand")[0]), /tokens this turn/);
});

test("the tail follows the transcript: a new turn changes its rows", async () => {
  const m = await mount();
  await expand(m);
  await until(() => find(m.root, "li.tail-row").length > 0, "the tail");
  const before = find(m.root, "li.tail-row").map(text).join("|");
  m.model.appendTranscript("architect");
  await until(() => find(m.root, "li.tail-row").map(text).join("|") !== before, "a new tail");
});

test("Messages reads the member's exchanges through the member filter, only those of that member", async () => {
  const m = await mount();
  await expand(m, "reviewer");
  tab(m, "Messages").fire("click");
  await until(() => find(m.root, "li.msg-row").length > 0, "the messages");
  const asked = m.log.lists.filter((l) => l.params.member);
  assert.equal(asked.length, 1);
  assert.equal(asked[0].params.member, member("reviewer"));
  assert.ok(find(m.root, "li.msg-row").every((li) => /reviewer/.test(text(li))), "every row names the member");
});

test("without a server filter the Messages tab falls back to the member's latest and a feed link", async () => {
  const m = await mount();
  const real = m.model.list;
  m.model.list = (id, params) => {
    if (params.member) {
      throw new Error("no member filter on the server");
    }
    return real(id, params);
  };
  await expand(m, "reviewer");
  tab(m, "Messages").fire("click");
  await until(() => find(m.root, "section.messages").length === 1 && /no per-member message list/.test(text(find(m.root, "section.messages")[0])), "the fallback");
  assert.match(text(find(m.root, "section.messages")[0]), /Latest messages/);
  assert.equal(find(m.root, "button.link").length, 1);
});

test("the Conversation tab switches to the Conversation view on that member and closes the overlay", async () => {
  const m = await mount();
  await expand(m, "reviewer");
  tab(m, "Conversation").fire("click");
  await until(() => find(m.root, "section.expand").length === 0, "the overlay to close");
  assert.equal(m.store.state.ui.view, "conversation");
  assert.equal(m.store.state.ui.selected, member("reviewer"));
});

test("Files changed and Tests read the leased slices", async () => {
  const m = await mount();
  await expand(m);
  tab(m, "Files changed").fire("click");
  await until(() => find(m.root, "li.file-row").length > 0, "the files");
  tab(m, "Tests").fire("click");
  await until(() => /passed/.test(text(find(m.root, "section.expand")[0])), "the tests");
});

test("the overlay's tab survives a close and reopen, and Escape closes it, returning focus to the opener", async () => {
  const m = await mount();
  await expand(m);
  tab(m, "Tests").fire("click");
  await until(() => m.store.state.ui.expandTab === "tests", "the tab");
  find(m.root, "section.expand")[0].fire("keydown", { key: "Escape", stopPropagation() {} });
  await until(() => find(m.root, "section.expand").length === 0, "closed by Escape");
  m.store.setUi({ expand: m.store.state.ui.selected }); // the overlay opens from the Detail action; the rail summary has no Expand button
  await until(() => find(m.root, "section.expand").length === 1, "reopened");
  assert.equal(m.store.state.ui.expandTab, "tests");
});

test("closing the overlay releases its leases: the Graph rail's summary keeps its own, nothing else is held", async () => {
  const m = await mount();
  await expand(m, "reviewer");
  const held = () => [...m.store.state.slices.keys()].filter((id) => /^(transcript|files|tests):/.test(id)).sort().join(",");
  await until(() => m.store.state.slices.has(`tests:${[...m.store.state.slices.values()].find((s) => s.id.startsWith("files:"))?.body.head}`), "tests leased");
  find(m.root, "button.close").at(-1).fire("click");
  await until(() => !held().includes("tests:"), "the overlay's tests lease released");
  find(m.root, "button.close")[0].fire("click");
  await until(() => held() === "", "every transcript, files and tests slice released");
});
