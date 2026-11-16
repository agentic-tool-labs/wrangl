// ah-ui-focus §3, ah-ui-answer §2: a team's needs read the summary's served counts; the client recounts nothing. atp-1736 (Orchestrator
// ruling, request 20261113-152814-1cyj): Needs you (`n`) is waiting_on_you; `blocked` is the alert rows, or what needs_you holds past it.
import { test } from "node:test";
import assert from "node:assert/strict";
import { teamNeeds } from "../../web/ui/select.js";

const row = (over) => ({ needs: 0, waitingOnYou: 0, waitingRows: [], items: [], ...over });
const item = { kind: "item", key: "s:a", title: "A" };
const decision = { kind: "decision", key: "d1", title: "Which?" };

test("the counts are served, past the rows the summary caps too", () => {
  const n = teamNeeds(row({ needs: 4, waitingOnYou: 3, waitingRows: [item, decision], items: [{ kind: "Blocked" }] }));
  assert.equal(n.n, 3);
  assert.equal(n.blocked, 1);
  assert.equal(n.waitingOnYou, 3);
  assert.equal(n.alerts, 1);
  assert.equal("decisions" in n, false, "no subtraction left to read");
});

test("negative: a Decision card is not an alert, and nothing is blocked", () => {
  const n = teamNeeds(row({ needs: 1, waitingOnYou: 1, waitingRows: [decision], items: [{ kind: "Decision" }] }));
  assert.equal(n.waitingOnYou, 1);
  assert.equal(n.alerts, 0);
  assert.equal(n.blocked, 0);
});

test("negative: a blocked member is blocked, not Needs you, when nothing waits on you", () => {
  const n = teamNeeds(row({ needs: 1, items: [{ kind: "Blocked" }] }));
  assert.equal(n.n, 0);
  assert.equal(n.blocked, 1);
  assert.equal(n.alerts, 1);
});

test("an ended team with needs_you still set and no alert row shows it as blocked", () => {
  const n = teamNeeds(row({ needs: 1 }));
  assert.deepEqual([n.n, n.blocked], [0, 1]);
});
