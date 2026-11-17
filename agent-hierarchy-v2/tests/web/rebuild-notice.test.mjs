// The background rebuild's notices (ah2-111): the one rule, in both copies (mod/view.ts for Claude Code, the herdr mod's
// rebuild-model.js), driven through the same cases.
import { test } from "node:test";
import assert from "node:assert/strict";
import * as claude from "../../mod/view.ts";
import * as herdr from "../../herdr-desktop/mods/ah2/rebuild-model.js";

const stamp = (version, state, at, stampVersion = "2.0.3") => JSON.stringify({ version: stampVersion, last_rebuild: { version, at, state } });
const T0 = "2026-11-17T17:00:00.000Z";
const T1 = "2026-11-17T17:04:00.000Z";

for (const [name, m] of [["claude", claude], ["herdr", herdr]]) {
  const say = (before, text) => m.rebuildStep(before, m.rebuildOf(text));

  test(`${name}: started -> building, then ok -> ready, once each`, () => {
    const a = say(null, stamp("2.0.4", "started", T0));
    assert.deepEqual([a.notice, a.watch], [{ kind: "building", version: "2.0.4" }, true]);
    const again = say(a.keep, stamp("2.0.4", "started", T0));
    assert.deepEqual([again.notice, again.keep, again.watch], [null, a.keep, true], "same rebuild seen twice: nothing, nothing to write");
    const b = say(a.keep, stamp("2.0.4", "ok", T1, "2.0.4"));
    assert.deepEqual([b.notice, b.watch], [{ kind: "ready", version: "2.0.4" }, false]);
    assert.equal(say(b.keep, stamp("2.0.4", "ok", T1, "2.0.4")).notice, null);
  });

  test(`${name}: started then failed -> failed`, () => {
    const a = say(null, stamp("2.0.4", "started", T0));
    assert.deepEqual(say(a.keep, stamp("2.0.4", "failed", T1)).notice, { kind: "failed", version: "2.0.4" });
  });

  test(`${name}: nothing for ok or failed at load, ok for another version, malformed or missing stamps, or after giving up`, () => {
    assert.equal(say(null, stamp("2.0.4", "ok", T1, "2.0.4")).notice, null, "ok at load");
    assert.equal(say(null, stamp("2.0.4", "failed", T1)).notice, null, "failed at load");
    const a = say(null, stamp("2.0.4", "started", T0));
    assert.equal(say(a.keep, stamp("2.0.4", "ok", T1, "2.0.3")).notice, null, "ok, but the stamp's version did not move");
    for (const bad of ["{", "null", "{}", JSON.stringify({ last_rebuild: { version: "2.0.4", state: "started" } })]) {
      const s = say(a.keep, bad);
      assert.deepEqual([s.notice, s.keep], [null, a.keep], `malformed: ${bad}`);
    }
    const gaveUp = m.rebuildGaveUp(a.keep);
    assert.equal(say(gaveUp, stamp("2.0.4", "started", T0)).watch, false, "a gave-up build is not watched again");
    assert.equal(say(gaveUp, stamp("2.0.4", "ok", T1, "2.0.4")).notice, null, "nor told ready later");
  });
}

test("the herdr mod takes ah's home from an `ah where` reply and the stamp from the file read", () => {
  const where = (extra) => `${JSON.stringify({ binary: { version: "2.0.4" }, plugin: null, compatible: false, login: "off", ui: { port: null, live: false }, ...extra })}\n`;
  assert.equal(herdr.homeOf(where({ home: "/h/ah2" })), "/h/ah2");
  assert.deepEqual(herdr.rebuildOf(stamp("2.0.4", "started", T0)), { version: "2.0.4", at: T0, state: "started", stamp: "2.0.3" });
  // the nearest replies that must name no home: an older binary without the field, an empty or non-string one, no reply, not JSON
  for (const bad of [where({}), where({ home: "" }), where({ home: 7 }), null, "", "{", "null"]) assert.equal(herdr.homeOf(bad), null, `no home: ${bad}`);
  // with no home, or a read that returns null (a WSL target), there is no stamp: nothing is said
  assert.equal(herdr.rebuildStep(null, null).notice, null, "an unknown home or an unreadable file says nothing");
});

test("the herdr mod asks `ah where` again after a reply with no home and after a target change, not otherwise", async () => {
  const replies = [null, JSON.stringify({ home: "/h/a" }), JSON.stringify({ home: "/h/b" })];
  let asked = 0;
  const lookup = async () => { asked++; return replies.shift(); };
  const cache = { key: null, home: null };
  assert.equal(await herdr.homeFor(cache, "local", lookup), null, "a failed lookup");
  assert.equal(await herdr.homeFor(cache, "local", lookup), "/h/a", "is retried on the next check");
  assert.equal(await herdr.homeFor(cache, "local", lookup), "/h/a", "a home held for the same target is not asked again");
  assert.equal(asked, 2);
  assert.equal(await herdr.homeFor(cache, "machine:m1", lookup), "/h/b", "a target change drops the cached home");
  assert.equal(asked, 3);
});
