import { test } from "node:test";
import assert from "node:assert/strict";
import { mountApp as mount, until, find, text, member } from "./mount-app.mjs";

const { hierarchyLines, hiddenColumns, memberGroups, resourceLines } = await import("../../web/ui/overview.js");

test("unknown reads as unknown and gone as gone, never Idle; idle is still idle", () => {
  const g = memberGroups(["working", "blocked", "idle", "unknown", "gone"].map((state) => ({ state })));
  assert.deepEqual(Object.fromEntries(Object.entries(g).map(([k, v]) => [k, v.map((m) => m.state)])), { working: ["working"], blocked: ["blocked"], running: [], idle: ["idle"], unknown: ["unknown"], gone: ["gone"] });
});

test("a running Orchestrator is in its own group, in neither Idle nor Unknown", () => {
  const g = memberGroups([{ state: "idle", running: true }, { state: "unknown", running: true }, { state: "idle" }]);
  assert.deepEqual([g.running.length, g.idle.length, g.unknown.length], [2, 1, 0]);
});

const open = async () => {
  const m = await mount({ view: "overview" });
  await until(() => find(m.root, "li.ov-member").length > 0, "the Overview");
  return m;
};
const headings = (m) => find(m.root, "h4").map(text);

test("the members split into Working · N and Idle · N, and the counts add up to the members", async () => {
  const m = await open();
  const [w, i] = headings(m).filter((h) => /^(Working|Idle) · \d+$/.test(h));
  const n = (h) => Number(h.split("· ")[1]);
  assert.ok(w && i, `both groups are shown: ${headings(m)}`);
  const b = headings(m).find((h) => /^Blocked · \d+$/.test(h));
  assert.equal(n(w) + n(i) + (b ? n(b) : 0), find(m.root, "li.ov-member").length);
  const states = find(m.root, "li.ov-member").map((li) => li.attrs.get("data-state"));
  assert.equal(states.filter((s) => s === "working").length, n(w), "Working counts what the status bar calls working");
});

test("an idle member is not listed under Working (the nearest non-match)", async () => {
  const m = await open();
  const lists = find(m.root, "ul").filter((ul) => find(ul, "li.ov-member").length > 0);
  const [working, idle] = [lists[0], lists.at(-1)];
  for (const li of find(working, "li.ov-member")) {
    assert.match(li.attrs.get("data-state"), /^working$/);
  }
  for (const li of find(idle, "li.ov-member")) {
    assert.match(li.attrs.get("data-state"), /^(idle|gone)$/);
  }
});

test("three stat cells: Needs you, In flight, Decisions (the dead Last push tile is cut); Decisions reads the pipeline's count", async () => {
  const m = await open();
  const cells = find(m.root, "div.stat");
  assert.deepEqual(cells.map((c) => text(find(c, "h4")[0])), ["Needs you", "In flight", "Decisions"]);
  assert.doesNotMatch(text(m.root), /Last push/);
  assert.equal(text(find(cells[2], "b")[0]), "14", "the mock's decisions_count");
  assert.doesNotMatch(text(cells[2]), /not published/);
  assert.match(text(cells[2]), /this run/, "the count is the run's, shared by every team on it");
  assert.equal(text(find(cells[1], "b")[0]), String(find(m.root, "div.row").filter((r) => !r.attrs.get("class").includes("head")).length));
});

test("the In flight table has a header row and one row per open brief, with from → to", async () => {
  const m = await open();
  const rows = find(m.root, "div.row");
  // The mock's briefs are all "open" with no branch, so both columns are hidden (L4).
  assert.match(text(rows[0]), /^Item\s*From → to\s*Age$/);
  assert.ok(rows.every((r) => !find(r, "span.status").length && !find(r, "span.mono").length));
  const body = rows.slice(1);
  assert.ok(body.length > 0);
  for (const r of body) {
    assert.match(text(r), / → /);
  }
});

test("a row of the hierarchy is drawn per tree row, indented by depth, and the team row has no branch glyph", () => {
  const lines = hierarchyLines([
    { key: "t", kind: "team", depth: 0, label: "ship", detail: "2 members" },
    { key: "w", kind: "worktree", depth: 1, label: "main", detail: "x" },
    { key: "m", kind: "member", depth: 2, label: "a", detail: "role", state: "idle" },
  ]);
  assert.deepEqual(lines.map((l) => l.branch), ["", "├─ ", "│  ├─ "]);
});

test("the hierarchy shows the team and its members", async () => {
  const m = await open();
  const lines = find(m.root, "div.ln").map(text);
  assert.ok(lines.some((l) => l.includes("ship")));
  assert.ok(lines.some((l) => l.includes("architect")));
});

test("clicking a member selects it, and clicking it again clears the selection", async () => {
  const m = await open();
  const li = find(m.root, "li.ov-member")[0];
  find(li, "button")[0].fire("click");
  await until(() => m.store.state.ui.selected !== null, "selected");
  const picked = m.store.state.ui.selected;
  find(find(m.root, "li.ov-member").find((l) => l.attrs.get("class").includes("selected")), "button")[0].fire("click");
  await until(() => m.store.state.ui.selected === null, "cleared");
  assert.ok(picked);
});

test("a member whose process is gone is not listed, so the groups count only the 7 live of the mock's 8", async () => {
  const m = await open();
  const rows = find(m.root, "li.ov-member");
  assert.equal(rows.length, 7);
  assert.equal(rows.filter((li) => li.attrs.get("data-state") === "gone").length, 0);
  assert.match(text(find(m.root, "nav").find((n) => n.attrs.get("class") === "ov-members")), /7 · 1 gone/, "the header explains 7 of the team's 8");
  const [w, i] = headings(m).filter((h) => /^(Working|Idle) · \d+$/.test(h));
  const b = headings(m).find((h) => /^Blocked · \d+$/.test(h));
  assert.equal(Number(w.split("· ")[1]) + Number(i.split("· ")[1]) + (b ? Number(b.split("· ")[1]) : 0), 7);
});

test("the In flight cell counts the open briefs to live members and says how many more are stale; no other cell does", async () => {
  const m = await open();
  const cells = find(m.root, "div.stat");
  assert.equal(text(find(cells[1], "b")[0]), "3", "4 open in the mock, one of them addressed to the gone member");
  assert.match(text(cells[1]), / · 1 stale/);
  for (const other of [cells[0], cells[2]]) {
    assert.doesNotMatch(text(other), /stale/);
  }
  assert.equal(find(m.root, "div.row").filter((r) => !r.attrs.get("class").includes("head")).length, 3, "the table lists only the in-flight briefs");
});

test("hiddenColumns: Branch hides only when every row has none, Status only when every row reads the same word", () => {
  const b = (state, branch) => ({ state, branch });
  assert.deepEqual(hiddenColumns([b("idle", null), b("idle", null)]), { branch: true, status: true });
  assert.deepEqual(hiddenColumns([b("idle", null), b("idle", "ah/x")]), { branch: false, status: true });
  assert.deepEqual(hiddenColumns([b("working", null), b("idle", null)]), { branch: true, status: false });
  assert.deepEqual(hiddenColumns([b("working", "ah/x"), b("working", "ah/x")]), { branch: false, status: true }, "a uniform branch is still shown");
  assert.deepEqual(hiddenColumns([b("blocked", ""), b("idle", null)]), { branch: true, status: true }, "blocked and idle both read open");
  assert.deepEqual(hiddenColumns([]), { branch: true, status: true });
});

test("a blocked member has its own group, so Working agrees with the status bar", async () => {
  const m = await open();
  const li = find(m.root, "li.ov-member");
  const blocked = li.filter((x) => x.attrs.get("data-state") === "blocked").length;
  assert.ok(blocked > 0, "the fixture has one");
  assert.ok(headings(m).includes(`Blocked · ${blocked}`), headings(m).join());
});

test("a running Orchestrator shows no idle glyph, and a field with no source draws nothing", async () => {
  const m = await open();
  const id = member("@orchestrator");
  const act = m.store.state.slices.get(m.store.state.slices.get(id).body.activity);
  m.model.publish([{ id: act.id, as_of: Date.now(), body: { ...act.body, activity: "idle", tool: null } }]);
  const row = () => find(m.root, "li.ov-member").find((x) => text(x).includes("orchestrator"));
  await until(() => headings(m).some((h) => h.startsWith("Running ·")), "the Running group");
  assert.equal(text(row().all("span.g")[0]), "◐");
  assert.doesNotMatch(text(row().all("span.since")[0]), /—/);
});

test("resourceLines: at most two lines, name then the waiting count then the holder; +n more apart; the title carries outside and every waiter", () => {
  const h = (name, extra = {}) => ({ name, who: "impl", outside: false, since: 0, waiting: [], ...extra });
  const two = resourceLines([h("a"), h("b", { waiting: [{ who: "qa", since: 0 }] })]);
  assert.deepEqual([two.lines.map((r) => [r.name, r.wait, r.who]), two.more], [[["a", null, "impl"], ["b", "1 waiting", "impl"]], 0]);
  const four = resourceLines([h("a"), h("b"), h("c"), h("d")]);
  assert.deepEqual([four.lines.map((r) => r.name), four.more], [["a", "b"], 2]);
  const [r] = resourceLines([h("gpu", { outside: true, waiting: [{ who: "qa", since: 0 }, { who: "ux", since: 0 }] })]).lines;
  assert.equal(r.wait, "2 waiting");
  assert.match(r.title, /^gpu: held by impl \(outside this team\) since \d\d:\d\d; waiting: qa since \d\d:\d\d, ux since \d\d:\d\d$/);
});
