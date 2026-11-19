// ah2-218 B: a team's display name in the web UI. The one name function (select.js, shared with the herdr mod), the rule the rename
// dialog shows, and the dialog against a stubbed route.
import { test } from "node:test";
import assert from "node:assert/strict";

process.env.AH_MOCK_FIXTURE = "lanes";
const { mountApp, until, find, text, DIR } = await import("./mount-app.mjs");
const { teamLabel, displayNameOf, realNameNote, displayNameProblem, summaryRow, parentLabel, teamRows } = await import("../../web/ui/select.js");

const row = (name, body = {}) => summaryRow({ id: `team-summary:${DIR}/${name}`, as_of: 1, body: { team: `team:${DIR}/${name}`, name, ...body } });

test("the name a team is shown by: its display name when set, its real name otherwise", () => {
  assert.equal(teamLabel({ name: "ah2-tm4", display_name: "Payments" }), "Payments");
  assert.equal(teamLabel({ name: "ah2-tm4", display_name: null }), "ah2-tm4");
  assert.equal(teamLabel({ name: "ah2-tm4" }), "ah2-tm4", "a record from an older version has no field");
  assert.equal(teamLabel({ name: "ah2-tm4", display_name: "   " }), "ah2-tm4", "blank after trimming is none (Amendment 1)");
  assert.equal(teamLabel({ name: "ah2-tm4", display_name: "  Pay‮me\x1b[31m  " }), "Payme", "inert: no bidi or escape sequences, trimmed");
  assert.equal(realNameNote({ name: "ah2-tm4", display_name: "Payments" }), "ah2-tm4");
  assert.equal(realNameNote({ name: "ah2-tm4" }), null);
  assert.equal(displayNameOf({ name: "x", display_name: "" }), null);
});

test("a row keeps its real name as the key and shows the label; a lane's label is its own, never its parent's", () => {
  const top = row("ship", { display_name: "Flagship" });
  const lane = row("ship-ui", { lane: { parent_team: "ship", stream: "ui" } });
  assert.deepEqual([top.name, top.label, top.realNote], ["ship", "Flagship", "ship"]);
  assert.deepEqual([lane.label, lane.displayName], ["ship-ui", null], "nothing is inherited");
  assert.equal(parentLabel([top, lane], lane), "Flagship", "a lane names its parent by the parent's display name");
  assert.equal(parentLabel([lane], lane), "ship", "the served parent name while the parent's row is not held");
  const named = row("ship-ui", { display_name: "Interface", lane: { parent_team: "ship", stream: "ui" } });
  assert.equal(named.label, "Interface");
  // negative: an unnamed team reads exactly as before
  assert.deepEqual([row("q").label, row("q").realNote], ["q", null]);
});

test("the rule as the dialog shows it: empty after trimming, too long, a line break; 60 characters is fine", () => {
  assert.equal(displayNameProblem("Payments"), null);
  assert.equal(displayNameProblem("  Payments  "), null, "trimmed before it is judged");
  assert.match(displayNameProblem("   "), /^Empty/);
  assert.match(displayNameProblem(""), /^Empty/);
  assert.equal(displayNameProblem("x".repeat(60)), null);
  assert.equal(displayNameProblem("x".repeat(61)), "61 characters. At most 60.");
  assert.equal(displayNameProblem("😀".repeat(60)), null, "characters, not UTF-16 units, as the daemon counts runes");
  assert.match(displayNameProblem("a\nb"), /line breaks/);
  assert.match(displayNameProblem("a\tb"), /control characters/);
});

// The route's replies in order; each request body is kept.
function stubRoute(...replies) {
  const sent = [];
  const real = globalThis.fetch;
  globalThis.fetch = async (url, init) => {
    if (url !== "/ui/v1/team-rename") {
      return real(url, init);
    }
    sent.push(JSON.parse(init.body));
    const [status, body] = replies.shift();
    return { status, json: async () => body };
  };
  return { sent, restore: () => (globalThis.fetch = real) };
}

const button = (root, label) => find(root, "button").find((b) => b.getAttribute("aria-label") === label || text(b) === label);
const field = (root) => find(root, "input").find((i) => i.getAttribute("id") === "tr-input");
const type = (root, value) => field(root).fire("input", { target: { value } });
const submit = (root) => find(root, "form.tr-form")[0].fire("submit");

test("Rename on the team's header: Save sends the trimmed name for the real team; a failure keeps the dialog open with the reason", async () => {
  const route = stubRoute([400, { error: "invalid", detail: "the display name is 61 characters; at most 60" }], [200, { ok: true, team: "ship", display_name: "Flagship", previous: null }]);
  try {
    const m = await mountApp({ view: null, ui: { view: "overview", team: `team:${DIR}/ship` } });
    await until(() => button(m.root, "Rename ship"), "the Rename button");
    button(m.root, "Rename ship").fire("click");
    await until(() => find(m.root, ".team-rename-dialog").length === 1, "the dialog");
    assert.equal(field(m.root).value ?? field(m.root).getAttribute("value"), "", "no display name yet: the field is empty");
    assert.equal(button(m.root, "Clear"), undefined, "no Clear while none is set");
    assert.equal(text(find(m.root, ".tr-problem")[0]), "", "an untouched empty field is not an error");

    submit(m.root);
    await until(() => /^Empty/.test(text(find(m.root, ".tr-problem")[0])), "Save on an empty field shows the rule");
    assert.equal(route.sent.length, 0, "nothing is sent while the rule is broken");

    type(m.root, "x".repeat(61));
    await until(() => /61 characters/.test(text(find(m.root, ".tr-problem")[0])), "too long shows as it is typed");
    assert.equal(field(m.root).getAttribute("aria-invalid"), "true");

    type(m.root, "  Flagship ");
    await until(() => text(find(m.root, ".tr-problem")[0]) === "", "the rule clears");
    submit(m.root);
    await until(() => find(m.root, ".team-close-err").length === 1, "the server's reason");
    assert.match(text(find(m.root, ".team-close-err")[0]), /Could not rename: the display name is 61 characters/);
    assert.equal(find(m.root, ".team-rename-dialog").length, 1, "the dialog stays open");

    submit(m.root);
    await until(() => find(m.root, ".team-rename-dialog").length === 0, "a saved rename closes the dialog");
    assert.deepEqual(route.sent.at(-1), { dir: DIR, team: "ship", display_name: "Flagship" });
  } finally {
    route.restore();
  }
});

test("Clear sends null when a display name is set, and the header and Home show it with the real name as the tooltip", async () => {
  const route = stubRoute([200, { ok: true, team: "ship", display_name: null, previous: "Flagship" }]);
  try {
    const m = await mountApp({ view: null, ui: { view: "overview", team: `team:${DIR}/ship` } });
    const id = `team-summary:${DIR}/ship`;
    await until(() => m.store.state.slices.get(id), "the summary");
    const s = m.store.state.slices.get(id);
    m.store.state.slices.set(id, { ...s, body: { ...s.body, display_name: "Flagship" } });
    m.store.setUi({ menu: null });
    await until(() => button(m.root, "Rename Flagship"), "the header names the display name");
    const sw = find(m.root, ".switch-btn")[0];
    assert.match(text(sw), /^Flagship/);
    assert.equal(sw.getAttribute("title"), "ship", "the real name, which commands use, is the tooltip");
    assert.equal(teamRows(m.store.state.slices).find((r) => r.id === `team:${DIR}/ship`).name, "ship", "the key keeps the real name");

    button(m.root, "Rename Flagship").fire("click");
    await until(() => button(m.root, "Clear"), "Clear, since one is set");
    assert.equal(field(m.root).value ?? field(m.root).getAttribute("value"), "Flagship", "prefilled with the current display name");
    button(m.root, "Clear").fire("click");
    await until(() => find(m.root, ".team-rename-dialog").length === 0, "the dialog closes");
    assert.deepEqual(route.sent, [{ dir: DIR, team: "ship", display_name: null }]);
  } finally {
    route.restore();
  }
});
