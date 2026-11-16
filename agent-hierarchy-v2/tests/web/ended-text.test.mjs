import { test } from "node:test";
import assert from "node:assert/strict";

const { endedText } = await import("../../web/ui/bridge.js");

test("endedText: 503 the daemon, any other reason the UI closed", () => {
  assert.equal(endedText("503"), "The daemon isn't running.");
  assert.match(endedText("401"), /^The UI closed/);
  assert.match(endedText(null), /^The UI closed/);
});
