import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { settle } from "./dom.mjs";
import { served } from "./lib.mjs";
import { mountApp, until, find, text, DIR } from "./mount-app.mjs";
import { buildSlices } from "../../web/mock/fixture.mjs";
import { queueRows } from "../../web/ui/plans-model.js";
import { servedQueue } from "../../web/mock/model.mjs";

// The rows as the daemon serves them, in its order.
const rowsOf = (rows, now) => queueRows(servedQueue(rows), now);
import { globalKey } from "../../web/ui/keys.js";

// The Queue view lists the queue slice the daemon serves; the row shape is pinned to the body the Ah.Watch tests record.

const ISO = /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/;
const sliceId = `queue:${DIR}/ship`;

test("the mock's queue slice is the daemon's recorded body: an array of rows with exactly the golden's fields in its order, times as UTC ISO and last_dispatch_at null when never", () => {
  const golden = served("queue-slice.json");
  assert.ok(Array.isArray(golden) && golden.length >= 2);
  const body = buildSlices(Date.now()).find((s) => s.id === sliceId).body;
  assert.ok(Array.isArray(body));
  assert.deepEqual(body.map((r) => Object.keys(r)), golden.map((r) => Object.keys(r)));
  assert.deepEqual(body.map((r) => [r.item, r.role, r.eligible_members]), golden.map((r) => [r.item, r.role, r.eligible_members]));
  for (const row of body) {
    assert.match(row.queued_since, ISO);
    assert.match(row.stall_at, ISO);
    assert.ok(row.last_dispatch_at === null || ISO.test(row.last_dispatch_at));
  }
  assert.deepEqual(body.map((r) => r.last_dispatch_at === null), golden.map((r) => r.last_dispatch_at === null));
  assert.ok(body.some((r) => r.last_dispatch_at === null) && body.some((r) => r.last_dispatch_at !== null));
});

test("the queue puts stalled items first, longest stalled first, by comparing the clock with stall_at, and holds no threshold of its own", () => {
  const now = Date.parse("2026-10-21T12:00:00Z");
  const row = (item, stall) => ({ item, role: "implementor", queued_since: "2026-10-21T11:00:00Z", stall_at: stall, eligible_members: [], last_dispatch_at: null });
  const rows = rowsOf([row("a/1", "2026-10-21T12:10:00Z"), row("a/2", "2026-10-21T11:50:00Z"), row("a/3", "2026-10-21T11:55:00Z"), row("a/4", "2026-10-21T12:05:00Z")], now);
  assert.deepEqual(rows.map((r) => r.item), ["a/2", "a/3", "a/4", "a/1"]);
  assert.deepEqual(rows.map((r) => r.stalled), [true, true, false, false]);
  assert.equal(rowsOf([row("a/1", "2026-10-21T12:00:00Z")], now)[0].stalled, true, "at stall_at it is stalled");
});

test("the Queue view shows the rows with the stall state by word and glyph, and an empty queue reads as empty, not as an error", async () => {
  const m = await mountApp({ view: "queue" });
  await until(() => find(m.root, "tr.cl-row").length === 2, "the queue rows");
  await settle();
  const rows = find(m.root, "tr.cl-row");
  assert.match(text(rows[0].all("td")[0]), /⚠\s*stalled \d+m/);
  assert.match(text(rows[0].all("td.cl-owner")[0]), /impl-1, impl-2/);
  assert.match(rows[0].all("td")[0].className, /\battn\b/, "a stalled item is bold, not colour alone");
  assert.match(text(rows[1].all("td")[0]), /◔\s*stalls in \d+m/);
  assert.match(text(rows[0].all("td.cl-title")[0]), /s\/a/);
  assert.match(text(rows[0].all("td.cl-updated")[0]), /never/, "a role never sent a dispatch reads never");
  assert.match(text(rows[1].all("td.cl-owner")[0]), /none idle/);
  assert.match(text(find(m.root, "div.cl-summary")[0]), /2 queued · 1 stalled/);

  m.model.publish([{ id: sliceId, as_of: Date.now(), body: [] }]);
  await until(() => find(m.root, "tr.cl-row").length === 0 && /Nothing is queued/.test(text(find(m.root, "section.queue")[0])), "the empty queue");
  assert.equal(find(m.root, "section.queue")[0].all("p.dim").length, 1);
});

test("a queue the daemon does not serve reads as such once the page is ready, not as loading for ever", async () => {
  const m = await mountApp({ view: "queue" });
  await until(() => find(m.root, "tr.cl-row").length === 2, "the queue rows");
  m.model.publish([{ id: sliceId, as_of: Date.now(), body: { id: "ship", gone: true } }]);
  await until(() => /serves no queue for this team/.test(text(find(m.root, "section.queue")[0])), "the no-queue state");
  assert.equal(find(m.root, "tr.cl-row").length, 0);
});

const rgb = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
const lin = (c) => ((c /= 255) <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
const lum = (c) => c.reduce((n, v, i) => n + [0.2126, 0.7152, 0.0722][i] * lin(v), 0);
const ratio = (a, b) => (Math.max(lum(a), lum(b)) + 0.05) / (Math.min(lum(a), lum(b)) + 0.05);

test("text on a stalled row meets 4.5:1 on its tint and on the hover background in both themes", async () => {
  const { fixture } = await import("./lib.mjs");
  const tokens = fixture("visual-tokens.json");
  for (const theme of ["dark", "light"]) {
    const set = tokens[theme];
    const pct = Number(/attn\) (\d+)%/.exec(tokens.dark["tint-attn"])[1]) / 100;
    const tint = rgb(set.attn).map((c, i) => Math.round(c * pct + rgb(set.bg)[i] * (1 - pct)));
    for (const [name, bg] of [["tint", tint], ["hover", rgb(set["row-hi"])]]) {
      for (const fg of ["ink", "ink-2", "attn", "muted"]) {
        assert.ok(ratio(rgb(set[fg]), bg) >= 4.5, `${theme} ${fg} on the stalled row's ${name}`);
      }
    }
  }
});

test("the hover rule follows the stalled rule at the same specificity, so a hovered stalled row shows the hover background", () => {
  const css = readFileSync(new URL("../../web/ui/r6-checklist.css", import.meta.url), "utf8");
  const stalled = css.indexOf(".r6 .cl-row.q-stalled {");
  const hover = css.indexOf(".r6 .cl-row:hover {");
  assert.ok(stalled >= 0 && hover > stalled);
});
