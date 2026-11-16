import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { join, dirname } from "node:path";
import { settle } from "./dom.mjs";
import { mountApp, until, find, text } from "./mount-app.mjs";
import { buildSlices, openListId } from "../../web/mock/fixture.mjs";
import { etaText } from "../../web/ui/ticker.js";
import { fixture } from "./lib.mjs";

// The ETA chip words what the daemon serves (state, due_at, overdue_by) and decides nothing; the mock serves the daemon's shape.

const native = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "native", "src");
const source = (file) => readFileSync(join(native, file), "utf8");
const exchanges = (now) => buildSlices(now).filter((s) => s.id.startsWith("exchange:"));

test("an open exchange in the mock carries exactly the fields the daemon adds, and an answered one carries none", () => {
  const added = [...source("Ah.Watch/Model/EtaProjection.cs").matchAll(/\["(state|due_at|overdue_by|next_nudge)"\] =/g)].map((m) => m[1]);
  assert.deepEqual([...new Set(added)].sort(), ["due_at", "next_nudge", "overdue_by", "state"]);
  const clock = source("Ah.Hierarchy/Support/EtaClock.cs");
  const states = ["on-time", "due-soon", "overdue"];
  for (const s of states) {
    assert.ok(clock.includes(`"${s}"`), `the daemon names the state ${s}`);
  }
  const now = Date.now();
  const open = exchanges(now).filter((s) => s.body.open);
  assert.ok(open.length >= 3);
  for (const { body } of open) {
    assert.ok(states.includes(body.state), body.state);
    assert.match(body.due_at, /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/);
    assert.equal(Number.isInteger(body.overdue_by) && body.overdue_by >= 0, body.state === "overdue");
    assert.equal(body.state === "overdue" ? body.overdue_by === Math.floor((now - Date.parse(body.due_at)) / 1000) : body.overdue_by, body.state === "overdue" ? true : null);
    assert.deepEqual(Object.keys(body.next_nudge), ["step", "at"]);
    assert.ok(["check-in", "silent"].includes(body.next_nudge.step));
  }
  assert.deepEqual([...new Set(open.map((s) => s.body.state))].sort(), states.slice().sort(), "the mock reaches every state");
  for (const { body } of exchanges(now).filter((s) => !s.body.open)) {
    assert.equal("state" in body || "due_at" in body || "overdue_by" in body || "next_nudge" in body, false);
  }
  const list = buildSlices(now).find((s) => s.id === openListId());
  assert.equal(list.body.overdue, open.filter((s) => s.body.state === "overdue").length);
});

test("the chip reads due Nm for on-time and due-soon, overdue Nm for overdue, and nothing for anything else", () => {
  const now = Date.parse("2026-10-21T12:00:00Z");
  assert.equal(etaText({ state: "on-time", due_at: "2026-10-21T12:12:00Z" }, now), "due 12m");
  assert.equal(etaText({ state: "due-soon", due_at: "2026-10-21T12:01:00Z" }, now), "due 1m");
  assert.equal(etaText({ state: "due-soon", due_at: "2026-10-21T12:01:00Z" }, now + 61_000), "due now", "past due before the next poll reads due now, not due 1m");
  assert.equal(etaText({ state: "overdue", due_at: "2026-10-21T11:55:00Z", overdue_by: 300 }, now), "overdue 5m");
  assert.equal(etaText({ state: "overdue", due_at: "2026-10-21T11:55:00Z", overdue_by: 300 }, now + 120_000), "overdue 7m", "the minutes tick on the page clock");
  assert.equal(etaText({ state: "overdue", due_at: "2026-10-21T08:00:00Z", overdue_by: 14400 }, now), "overdue 4h");
  assert.equal(etaText({ state: "answered" }, now), null);
  assert.equal(etaText({}, now), null);
});

test("the live feed shows a chip on each open exchange row, bold with a glyph when overdue, and none on an answered row", async () => {
  const m = await mountApp({ view: "overview" });
  await until(() => find(m.root, "li.feed-row").length > 0, "the feed");
  await settle();
  const rows = find(m.root, "li.feed-row");
  const open = rows.filter((r) => r.className.includes("open"));
  assert.ok(open.length >= 3);
  for (const r of rows) {
    assert.equal(r.all("span.eta").length, open.includes(r) ? 1 : 0);
  }
  const chips = open.map((r) => r.all("span.eta")[0]);
  assert.ok(chips.some((c) => /\boverdue\b/.test(c.className) && /^⚠\s*overdue \d+[mh]$/.test(text(c))), "an overdue chip carries a glyph");
  assert.ok(chips.some((c) => /\bdue-soon\b/.test(c.className) && /due \d+m/.test(text(c))));
  assert.ok(chips.some((c) => /\bon-time\b/.test(c.className) && /^due \d+m$/.test(text(c))));
  assert.match(text(find(m.root, "section.feed")[0].all("h2")[0]), /2 overdue/);
});

const channel = (hex) => {
  const v = parseInt(hex.slice(1), 16);
  return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
};
const lum = ([r, g, b]) => 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
const lin = (c) => ((c /= 255) <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
const ratio = (a, b) => {
  const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};
const mix = (fg, bg, pct) => fg.map((c, i) => Math.round(c * pct + bg[i] * (1 - pct)));

test("overdue text on its tint, due-soon text and muted text on the chip face meet 4.5:1 in both themes", () => {
  const tokens = fixture("visual-tokens.json");
  for (const [theme, set, pct] of [["dark", tokens.dark, 0.12], ["light", tokens.light, 0.09]]) {
    const bg = channel(set.bg);
    const overdue = ratio(channel(set["err-ink"]), mix(channel(set.err), bg, pct));
    const soon = ratio(channel(set.attn), channel(set.control));
    const muted = ratio(channel(set.muted), channel(set.control));
    assert.ok(overdue >= 4.5, `${theme} overdue ${overdue.toFixed(2)}`);
    assert.ok(soon >= 4.5, `${theme} due-soon ${soon.toFixed(2)}`);
    assert.ok(muted >= 4.5, `${theme} on-time ${muted.toFixed(2)}`);
  }
});
