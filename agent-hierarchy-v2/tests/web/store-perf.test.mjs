import { test } from "node:test";
import assert from "node:assert/strict";

const { merge } = await import("../../web/ui/merge.js");

test("a list of primitives is taken as sent; arrays of objects still keep unchanged members by reference", () => {
  const ids = ["a", "b"];
  const next = ["a", "b"];
  assert.equal(merge({ ids }, { ids: next }).ids, next);
  const a = { id: "x", v: 1 };
  const out = merge({ rows: [a, { id: "y", v: 1 }] }, { rows: [{ id: "x", v: 1 }, { id: "y", v: 2 }] });
  assert.equal(out.rows[0], a, "the unchanged row keeps its reference");
  assert.equal(out.rows[1].v, 2);
  assert.deepEqual(merge({ e: [] }, { e: [] }).e, []);
});
