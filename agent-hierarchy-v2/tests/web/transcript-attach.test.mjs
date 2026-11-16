// ah-ui-ia U3 (review N2): the Conversation pane leases a member's transcript once the member's session is known, even when the
// pane mounted before it was (it used to lease only at mount, so such a pane never loaded). Nodes compared with assert.ok(a === b) only.
import { test } from "node:test";
import assert from "node:assert/strict";
import { settle } from "./dom.mjs";
import { mountApp as mount, until, find, member } from "./mount-app.mjs";

test("a pane that mounts before the member's session is known leases the transcript once it is, and its rows load", async () => {
  const m = await mount({ view: "graph" });
  const id = member("architect");
  await until(() => m.store.state.slices.get(id)?.body.session, "the member slice with its session");
  const held = m.store.state.slices.get(id);
  const { session, ...noSession } = held.body;
  m.model.publish([{ id, as_of: Date.now(), body: noSession }]);
  await until(() => !m.store.state.slices.get(id)?.body.session, "the member slice without a session");
  m.store.setUi({ view: "conversation", selected: id });
  await until(() => find(m.root, "section.conversation").length === 1, "the Conversation view");
  await settle();
  assert.equal(find(m.root, "li.tr-row").length, 0, "no transcript while the session is unknown");
  m.model.publish([{ id, as_of: Date.now() + 1, body: { ...noSession, session } }]);
  await until(() => find(m.root, "li.tr-row").length > 0, "the transcript rows once the session is known");
});
