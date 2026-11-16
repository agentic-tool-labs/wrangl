import { test } from "node:test";
import assert from "node:assert/strict";

const { trapTarget } = await import("../../web/ui/dialog.js");

test("the focus trap wraps Tab from the last control to the first and Shift+Tab the other way; it leaves the middle alone", () => {
  const [a, b, c] = [{}, {}, {}];
  assert.equal(trapTarget([a, b, c], c, false), a);
  assert.equal(trapTarget([a, b, c], a, true), c);
  assert.equal(trapTarget([a, b, c], b, false), null);
  assert.equal(trapTarget([a, b, c], b, true), null);
  assert.equal(trapTarget([a, b, c], {}, false), a, "focus outside the dialog is pulled in");
  assert.equal(trapTarget([], a, false), null);
});
