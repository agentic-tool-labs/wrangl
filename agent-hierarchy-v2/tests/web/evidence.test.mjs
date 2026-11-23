// Report evidence lines (spec artifact-review A8, Amendment 3 F2): the line parser and the kind table.
import { test } from "node:test";
import assert from "node:assert/strict";

const { evidenceParts, kindOf } = await import("../../web/ui/evidence.js");
const ID = "20261123-035032-rm1y";

test("an evidence line becomes a figure, a name may hold ' · ', and a k that does not match its ref stays prose", () => {
  const body = `VERDICT PASS x@-\n\n## evidence\n- [e1] a · b.png · image/png · 3 KiB · evidence:${ID}/e1\n- [e2] c.txt · text/plain · 1 KiB · evidence:${ID}/e9\n`;
  const parts = evidenceParts(body);
  assert.deepEqual(parts[0], { prose: "VERDICT PASS x@-" });
  assert.deepEqual(parts[1], { k: 1, name: "a · b.png", type: "image/png", size: "3 KiB", url: `/ui/v1/evidence/${ID}/e1` });
  assert.equal(parts.filter((p) => p.k !== undefined).length, 1);
  assert.match(parts.at(-1).prose, /evidence:.*\/e9$/);
  const plain = "no evidence here\n";
  assert.deepEqual(evidenceParts(plain), [{ prose: plain }]);
});

test("svg is a download, never an image; the other types map to their widget", () => {
  assert.equal(kindOf("image/svg+xml"), "file");
  assert.deepEqual(["image/png", "audio/wav", "video/mp4", "text/csv", "application/json", "application/pdf", "application/octet-stream"].map(kindOf),
    ["image", "audio", "video", "text", "text", "file", "file"]);
});
