// Mutation runner: node tests/web/mutate.mjs [name ...]
// Each mutant replaces one exact string in one source file, runs that mutant's test file under a hard timeout, then restores the file.
// A mutant is killed when the run fails or times out. A mutant that survives means the suite cannot fail on that change.
// The timeout kills the whole process group, so a spinning mutant leaves nothing behind. Exit 1 if any mutant survives or times out.
import { spawn } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const ui = join(here, "../../web/ui");
const mock = join(here, "../../web/mock");
const TIMEOUT_MS = 60_000;

// name, file, find (must occur exactly once), replace, test file
export const MUTANTS = [
  ["timeline-named-range-sends-end", join(ui, "timeline.js"), '...(sel.range === "custom" ? { to_ms: sel.to } : null)', "to_ms: sel.to", "timeline-marks.test.mjs"],
  ["timeline-late-answer-shown", join(ui, "timeline.js"), "if (gen === this.gen) {", "if (true) {", "timeline-marks.test.mjs"],
  ["timeline-range-keeps-old", join(ui, "timeline.js"), "this.served = null;", "", "timeline-marks.test.mjs"],
  ["archived-horizon-off", join(mock, "model.mjs"), "Number(to) < now - RETENTION_MS", "false", "timeline.test.mjs"],
  ["epoch-reload-skipped", join(ui, "store.js"), "reply.resync || reply.epoch !== state.epoch", "reply.resync", "store.test.mjs"],
  ["released-slices-kept", join(ui, "store.js"), "released.forEach((id) => (slices.delete(id), lists.delete(id)));", "", "conversation.test.mjs"],
  ["dialog-trap-outside", join(ui, "dialog.js"), "if (!items.includes(active)) {", "if (false) {", "dialog.test.mjs"],
  ["page-reads-old-shape", join(ui, "store.js"), "items: doc.rows, next: doc.next, prev: doc.prev,", "items: doc.items, next: doc.next_cursor, prev: doc.prev_cursor,", "conversation.test.mjs"],
  ["mock-envelope-old-keys", join(mock, "envelope.mjs"), "return { version: 1, total, head_cursor: head, tail_cursor: tail, rows, next, prev, ...extra };", "return { items: rows, next_cursor: next, prev_cursor: prev, ...extra };", "conversation.test.mjs"],
  ["mock-empty-before-not-echoed", join(mock, "transcripts.mjs"), "prev: page.length ? String(page[0].offset) : before,", "prev: page.length ? String(page[0].offset) : null,", "conversation.test.mjs"],
  ["mock-empty-after-not-echoed", join(mock, "transcripts.mjs"), "String(oldest.offset) : null) : after,", "String(oldest.offset) : null) : null,", "conversation.test.mjs"],
  ["paused-pages-past-capacity", join(ui, "conversation.js"), "held.length > 0 && held.length < MAX_WINDOWS", "held.length > 0", "conversation.test.mjs"],
  ["paused-count-without-plus", join(ui, "conversation.js"), "unreadMore: this.sel.newest > newest", "unreadMore: false", "conversation.test.mjs"],
  ["empty-before-adds-window", join(ui, "store.js"), "if (before && doc.rows.length === 0) {", "if (false) {", "conversation.test.mjs"],
  ["seconds-ignored", join(ui, "conversation.js"), "seconds: timed === 0 ? null : Math.round(ms / timed / 1000)", "seconds: null", "conversation.test.mjs"],
  ["chip-shows-bytes", join(ui, "transcript-rows.js"), "lines: r?.row.lines ?? null, expandable: true", "lines: r?.row.bytes ?? null, expandable: true", "conversation.test.mjs"],
  ["cache-never-pruned", join(ui, "conversation.js"), "if (!held.has(offset)) {", "if (false) {", "conversation.test.mjs"],
  ["ended-not-shown", join(ui, "conversation.js"), "hid !== null && snap.gone?.has(hid) === true", "false", "conversation.test.mjs"],
  ["ended-on-absence", join(ui, "conversation.js"), "hid !== null && snap.gone?.has(hid) === true", "hid !== null && snap.slices.get(hid) === undefined", "conversation.test.mjs"],
  ["tombstones-never-recorded", join(ui, "store.js"), "reply.removed.forEach((id) => gone.add(id));", "", "conversation.test.mjs"],
  ["rail-name-split-at-bar", join(ui, "conversation.js"), "[id, m.name, m.state, m.slug ?? \"\"", "[id, m.name.split(\"|\")[0], m.state, m.slug ?? \"\"", "conversation.test.mjs"],
  ["deeplink-never-clears", join(ui, "deeplink.js"), '(want !== "" || uiOf(win.location.hash))', 'want !== ""', "deeplink.test.mjs"],
  ["deeplink-ignored-on-load", join(ui, "deeplink.js"), "  apply(false);\n  win.addEventListener", "  win.addEventListener", "deeplink.test.mjs"],
  ["restart-per-lease", join(ui, "store.js"), "if (!restartQueued) {", "if (true) {", "store.test.mjs"],
  ["perf-poll-interval", join(ui, "perfstrip.js"), "export const POLL_MS = 5000;", "export const POLL_MS = 1000;", "perfstrip.test.mjs"],
  ["perf-polls-when-hidden", join(ui, "perfstrip.js"), "seen ? this.begin() : this.end();", "this.begin();", "perfstrip.test.mjs"],
  ["perf-timers-kept-on-leave", join(ui, "perfstrip.js"), "    env.clearInterval(this.pollTimer);\n    env.clearInterval(this.fpsTimer);\n", "", "perfstrip.test.mjs"],
  ["perf-frame-kept-on-leave", join(ui, "perfstrip.js"), "      env.caf?.(this.frame);\n", "", "perfstrip.test.mjs"],
  ["perf-late-reply-shown", join(ui, "perfstrip.js"), "if (round !== (this.round ?? 0) || !this.visible) {", "if (false) {", "perfstrip.test.mjs"],
  ["perf-negative-rate", join(ui, "perfstrip.js"), "Math.max(0, (retries - prev.retries) / seconds)", "(retries - prev.retries) / seconds", "perfstrip.test.mjs"],
  ["perf-frames-forever", join(ui, "perfstrip.js"), "if (spent >= FPS_WINDOW_MS) {", "if (false) {", "perfstrip.test.mjs"],
  ["perf-failure-hidden", join(ui, "perfstrip.js"), "this.setState({ failed: true });", "", "perfstrip.test.mjs"],
  ["perf-observer-leak", join(ui, "perfstrip.js"), "    this.watch?.disconnect();\n", "", "perfstrip.test.mjs"],
  ["perf-polls-while-in-flight", join(ui, "perfstrip.js"), "if (this.inFlight === round || !this.tabShown()) {", "if (!this.tabShown()) {", "perfstrip.test.mjs"],
  ["perf-polls-in-hidden-tab", join(ui, "perfstrip.js"), "if (this.inFlight === round || !this.tabShown()) {", "if (this.inFlight === round) {", "perfstrip.test.mjs"],
  ["perf-frames-in-hidden-tab", join(ui, "perfstrip.js"), "    if (!this.tabShown()) {\n      return;\n    }\n    const env = this.env;", "    const env = this.env;", "perfstrip.test.mjs"],
  ["all-script-over-budget", join(ui, "expand.js"), 'import { firstTeam, memberOf, partyName } from "./select.js";', `import { firstTeam, memberOf, partyName } from "./select.js";\nconst PAD = "${"x".repeat(1_000_000)}";`, "retired.test.mjs"],
  ["list-accepts-unknown-filter", join(ui, "..", "mock", "serve.mjs"), "if (unsupported !== undefined) {", "if (false) {", "mock-list.test.mjs"],
  ["actions-follow-other-node", join(ui, "actions.js"), "OWN_ROUTES.find(([prefix]) => route === `${prefix}${own}`)", "OWN_ROUTES.find(([prefix]) => route.startsWith(prefix))", "actions.test.mjs"],
  ["actions-inbox-to-overview", join(ui, "actions.js"), '["/inbox", "messages"]', '["/inbox", "overview"]', "actions.test.mjs"],
  ["actions-follow-without-node", join(ui, "actions.js"), 'typeof own !== "string" || own === ""', 'typeof own !== "string"', "actions.test.mjs"],
  ["actions-help-hides-reason", join(ui, "actions.js"), "handlers && !plan(a, handlers).enabled", "false", "actions.test.mjs"],
  ["actions-no-refetch-on-change", join(ui, "member-actions.js"), "if (this.sel.version !== this.fetchedFor) {\n      this.fetch();", "if (false) {\n      this.fetch();", "member-actions.test.mjs"],
  ["actions-refetch-other-member", join(ui, "member-actions.js"), "return { version: snap.slices.get(memberId)?.version ?? 0 };", "return { version: snap.seq ?? [...snap.slices.values()].reduce((n, s) => n + s.version, 0) };", "member-actions.test.mjs"],
  ["actions-no-confirm", join(ui, "member-actions.js"), "(action.confirm ? this.setState({ pending: action }) : this.run(action))", "this.run(action)", "member-actions.test.mjs"],
  ["actions-key-in-text-field", join(ui, "member-actions.js"), "    if (isTextTarget(e.target) || e.ctrlKey || e.metaKey || e.altKey || (e.key === \"Enter\" && isActivatable(e.target))) {", "    if (e.ctrlKey || e.metaKey || e.altKey || (e.key === \"Enter\" && isActivatable(e.target))) {", "member-actions.test.mjs"],
  ["actions-key-runs-disabled", join(ui, "member-actions.js"), "keyMatches(a.key, e.key) && plan(a, env).enabled", "keyMatches(a.key, e.key)", "member-actions.test.mjs"],
  ["help-key-in-text-field", join(ui, "member-actions.js"), 'e.key !== "?" || isTextTarget(e.target) || e.ctrlKey', 'e.key !== "?" || e.ctrlKey', "member-actions.test.mjs"],
  ["help-ignores-open-member", join(ui, "member-actions.js"), "await this.props.store.fetchActions(this.sel.memberId)", "await this.props.store.fetchActions(null)", "member-actions.test.mjs"],
  ["actions-panel-without-bar", join(ui, "app.js"), "      <${MemberActions} store=${store} memberId=${m.id} />\n", "", "member-actions.test.mjs"],
  ["actions-overlay-without-bar", join(ui, "expand.js"), "      <${MemberActions} store=${this.props.store} memberId=${this.props.memberId} />\n", "", "member-actions.test.mjs"],
  ["actions-route-accepts-bare-name", join(ui, "..", "mock", "serve.mjs"), 'if (id !== null && !id.startsWith("member:")) {', "if (false) {", "mock-list.test.mjs"],
  ["actions-route-unknown-member-ok", join(ui, "..", "mock", "serve.mjs"), 'return doc ? json(200, doc) : json(404, { error: "no_node" });\n  }\n  if (path === "/read-model")', 'return json(200, doc ?? { node: { kind: "member", path: "/" }, actions: [] });\n  }\n  if (path === "/read-model")', "mock-list.test.mjs"],
  ["golden-envelope-keys", join(ui, "..", "mock", "envelope.mjs"), '"next", "prev"];', '"next", "prev", "extra"];', "server-golden.test.mjs"],
  ["golden-max-limit", join(ui, "..", "mock", "model.mjs"), "export const MAX_LIST = 200;", "export const MAX_LIST = 100;", "server-golden.test.mjs"],
  ["golden-timeline-filters", join(ui, "..", "mock", "list-keys.mjs"), '["list:timeline:", ["from_ms", "to_ms"]]', '["list:timeline:", ["from_ms"]]', "server-golden.test.mjs"],
  ["golden-open-filter", join(ui, "..", "mock", "list-keys.mjs"), '["list:open:", ["member"]]', '["list:open:", []]', "server-golden.test.mjs"],
  ["golden-row-shape", join(ui, "..", "mock", "model.mjs"), ".map((s) => ({ id: s.id, body: s.body }));", ".map((s) => s);", "server-golden.test.mjs"],
  ["golden-open-list-unserved", join(ui, "..", "mock", "model.mjs"), "(exchanges|feed|open):team:", "(exchanges|feed):team:", "server-golden.test.mjs"],
  ["golden-sparks-one-end", join(ui, "select.js"), ".filter((o) => o.to_member && o.from_member)", ".filter((o) => o.to_member || o.from_member)", "server-golden.test.mjs"],
  ["golden-sparks-wrong-field", join(ui, "select.js"), "from: o.from_member, to: o.to_member", "from: o.to_member, to: o.from_member", "server-golden.test.mjs"],
  ["route-listing-is-a-list", join(ui, "bridge.js"), "(list|actions)(\\/|$)/.test(path)", "(list|actions)/.test(path)", "server-golden.test.mjs"],
  ["route-read-model-via-view", join(ui, "bridge.js"), '|| path === "/read-model"', "|| false", "server-golden.test.mjs"],
  ["route-colon-encoded", join(ui, "bridge.js"), '.replace(/%3A/g, ":")', "", "server-golden.test.mjs"],
  ["route-mock-ignores-direct", join(ui, "..", "mock", "serve.mjs"), "const path = DIRECT.test(url.pathname) ?", "const path = false ?", "server-golden.test.mjs"],
  ["golden-plan-lane-cursor-key", join(ui, "..", "mock", "changes.mjs"), "tail_cursor: rows.length ?", "cursor: rows.length ?", "server-golden.test.mjs"],
  ["golden-plan-lane-rows-key", join(ui, "..", "mock", "changes.mjs"), "return { rows, total: changes.length,", "return { changes: rows, total: changes.length,", "server-golden.test.mjs"],
  ["actions-orchestrator-detail-overlay", join(ui, "member-actions.js"), "if (team?.body.orchestrator === memberId) {", "if (false) {", "member-actions.test.mjs"],
  ["actions-member-detail-panel", join(ui, "member-actions.js"), "if (team?.body.orchestrator === memberId) {", "if (true) {", "member-actions.test.mjs"],
  ["route-at-sign-encoded", join(ui, "bridge.js"), '.replace(/%40/g, "@")', "", "server-golden.test.mjs"],
  ["route-list-not-direct", join(ui, "bridge.js"), "/^\\/(list|actions)(\\/|$)/.test(path)", "/^\\/(actions)(\\/|$)/.test(path)", "server-golden.test.mjs"],
  ["route-actions-not-direct", join(ui, "bridge.js"), "/^\\/(list|actions)(\\/|$)/.test(path)", "/^\\/(list)(\\/|$)/.test(path)", "server-golden.test.mjs"],
  ["timeline-pages-send-from-to", join(ui, "store.js"), "{ from_ms: from } : {}), ...(to !== null ? { to_ms: to }", "{ from } : {}), ...(to !== null ? { to }", "timeline.test.mjs"],
  ["timeline-pages-slice-id", join(ui, "timeline.js"), "const listId = dir ? `list:timeline-spans:${dir}/${team.body.name}` : null;", "const listId = dir ? `timeline:${dir}` : null;", "timeline-marks.test.mjs"],
  ["marks-refresh-uncoalesced", join(ui, "timeline.js"), "(this.props.coalesceMs ?? 1000)", "0", "timeline-marks.test.mjs"],
  ["list-accepts-both-cursors", join(ui, "..", "mock", "serve.mjs"), 'if (q.has("after") && q.has("before")) {', "if (false) {", "mock-list.test.mjs"],
  ["list-accepts-bare-member", join(ui, "..", "mock", "serve.mjs"), "(q.has(\"member\") && !q.get(\"member\").startsWith(\"member:\")) || ", "", "mock-list.test.mjs"],
  ["hover-ignores-inner-focus", join(ui, "hover.js"), "onFocusIn: (e) =>", "onFocus: (e) =>", "expand.test.mjs"],
  ["lane-not-focusable", join(ui, "timeline.js"), ' tabindex="0" ...${hoverProps(ctx.store, lane.id, "strip")}>', ' ...${hoverProps(ctx.store, lane.id, "strip")}>', "expand.test.mjs"],
  ["tail-retries-every-update", join(ui, "expand.js"), "      // A failed read is tried again only when the header's newest entry moves.\n", "      this.tailAt = null;\n", "expand.test.mjs"],
  ["tail-stale-reply-shown", join(ui, "expand.js"), "if (ask === this.tailAsk) {", "if (true) {", "expand.test.mjs"],
  ["hover-survives-view-change", join(ui, "store.js"), "const leaves = patch.view !== undefined", "const leaves = false && patch.view !== undefined", "expand.test.mjs"],
  ["hover-survives-detached-row", join(ui, "hover.js"), "if (!h || (hovered && !globalThis.document?.body?.contains(hovered))) {", "if (!h) {", "expand.test.mjs"],
  ["card-hides-process", join(ui, "hover.js"), "      <div>process: ${s.process}</div>\n", "", "expand.test.mjs"],
  ["overcap-expand-blank", join(ui, "conversation.js"), "body.truncated === true && (body.blocks ?? []).length === 0", "false", "conversation.test.mjs"],
  ["perf-reads-unserved-counter", join(join(ui, ".."), "mock", "model.mjs"), '"coalesce_ms", "projector_faults", "transcript_resets"', '"coalesce_ms", "projector_fault", "transcript_resets"', "perfstrip.test.mjs"],
  ["card-never-flips", join(ui, "hover.js"), "right + CARD.w > win.innerWidth ?", "false ?", "expand.test.mjs"],
  ["card-y-unclamped", join(ui, "hover.js"), "Math.max(0, Math.min(rect.top, win.innerHeight - CARD.h - GAP))", "rect.top", "expand.test.mjs"],
  ["strip-never-flips-up", join(ui, "hover.js"), "below + CARD.h > win.innerHeight ?", "false ?", "expand.test.mjs"],
  ["hover-never-closes", join(ui, "hover.js"), "if (store.state.ui.hover) {", "if (false) {", "expand.test.mjs"],
  ["hover-ignores-focus", join(ui, "hover.js"), "onFocusIn: (e) => hoverOn(store, id, e.currentTarget, kind),", "onFocusIn: () => {},", "expand.test.mjs"],
  ["card-tokens-dropped", join(ui, "hover.js"), "tokens: usage ? usage.input_tokens + usage.output_tokens : null };", "tokens: null };", "expand.test.mjs"],
  ["panel-never-leases", join(ui, "app.js"), "this.props.store.lease(this.owner, [...(hid ? [hid] : []), ...(filesId ? [filesId] : [])]);", "this.props.store.lease(this.owner, []);", "expand.test.mjs"],
  ["panel-never-releases", join(ui, "app.js"), "    super.componentWillUnmount();\n    this.props.store.lease(this.owner, []);", "    super.componentWillUnmount();", "expand.test.mjs"],
  ["overlay-never-releases", join(ui, "expand.js"), "    super.componentWillUnmount();\n    this.props.store.lease(this.owner, []);", "    super.componentWillUnmount();", "expand.test.mjs"],
  ["overlay-tail-unbounded", join(ui, "expand.js"), "transcriptRows([...doc.rows].reverse()).slice(-TAIL)", "transcriptRows([...doc.rows].reverse())", "expand.test.mjs"],
  ["messages-unfiltered", join(ui, "expand.js"), "{ member: this.props.memberId, limit: MESSAGES }", "{ limit: MESSAGES }", "expand.test.mjs"],
  ["conversation-tab-keeps-overlay", join(ui, "expand.js"), 'selected: this.props.memberId, expand: null });', "selected: this.props.memberId });", "expand.test.mjs"],
  ["overlay-tab-not-kept", join(ui, "expand.js"), "this.props.store.setUi({ expandTab: tab });", "", "expand.test.mjs"],
  ["messages-no-fallback", join(ui, "expand.js"), "this.setState({ messages: null, failed: true });", "this.setState({ messages: [], failed: false });", "expand.test.mjs"],
  ["retired-module-imported", join(ui, "app.js"), 'import { PerfStrip } from "./perfstrip.js";', 'import { PerfStrip } from "./perfstrip.js";\nimport "./live.js";', "retired.test.mjs"],
  ["first-paint-over-budget", join(ui, "index.html"), '<link rel="modulepreload" href="/ui/hover.js">', '<link rel="modulepreload" href="/ui/conversation.js">'.repeat(60), "retired.test.mjs"],
  ["pause-never", join(ui, "conversation.js"), "const atEnd = top >= offsets[offsets.length - 1] - this.viewHeight() - AT_END_PX;", "const atEnd = true;", "conversation.test.mjs"],
  ["unread-not-counted", join(ui, "conversation.js"), "this.setState({ unread: this.entries.filter((e) => e.offset > seen).length, unreadMore:", "this.setState({ unread: 0, unreadMore:", "conversation.test.mjs"],
  ["expand-body-not-kept", join(ui, "conversation.js"), "this.cache.set(offset, body);", "this.cache.set(offset, null);", "conversation.test.mjs"],
  ["errors-filter-all", join(ui, "conversation.js"), 'filter === "errors" ? r.error === true :', 'filter === "errors" ? true :', "conversation.test.mjs"],
  ["oversize-expandable", join(ui, "conversation.js"), 'kind: "large", label:', 'kind: "large", expandable: true, label:', "conversation.test.mjs"],
  ["tree-key-random", join(ui, "treeview.js"), "export const rowKey = (r) => r.key;", "export const rowKey = () => Math.random();", "treeview.test.mjs"],
  ["lane-key-random", join(ui, "timeline.js"), "export const laneKey = (l) => l.id;", "export const laneKey = () => Math.random();", "timeline.test.mjs"],
  ["brief-from-sender", join(ui, "select.js"), "list.filter((o) => o.to_member === memberId)", "list.filter((o) => o.to_member === memberId || o.from_member === memberId)", "treeview.test.mjs"],
  ["worktree-count-groups", join(ui, "treeview.js"), "${workRoots(slices, team).size} worktrees`", "${byRoot.size} worktrees`", "treeview.test.mjs"],
  ["custom-unreachable", join(ui, "timeline.js"), "onClick=${this.pickCustom}>Custom", "onClick=${() => {}}>Custom", "views.test.mjs"],
  ["backwards-custom-accepted", join(ui, "timeline.js"), "if (Number.isFinite(ms) && next.from < next.to) {", "if (Number.isFinite(ms)) {", "views.test.mjs"],
  ["retention-constant", join(ui, "timeline.js"), "from < now - days * DAY_MS", "from < now - 7 * DAY_MS", "timeline.test.mjs"],
  ["open-id-bare", join(ui, "select.js"), "slices.get(o.id)?.body.slug ?? o.id", 'slices.get(o.id.split("/").pop())?.body.slug ?? o.id', "select.test.mjs"],
  ["feed-key-random", join(ui, "app.js"), "return { win: win ?? null };", "return { win: win ?? null, n: Math.random() };", "app.test.mjs"],
];

// The mutant in flight. One handler for SIGINT, SIGTERM and SIGHUP kills its process group and puts its file back, so
// no signal leaves a mutated source or an orphaned test run behind.
let current = null;
const abandon = (signal) => {
  if (current) {
    try {
      process.kill(-current.child.pid, "SIGKILL");
    } catch {}
    writeFileSync(current.file, current.original);
    current = null;
  }
  process.exit(128 + (signal === "SIGINT" ? 2 : signal === "SIGHUP" ? 1 : 15));
};
for (const signal of ["SIGINT", "SIGTERM", "SIGHUP"]) {
  process.on(signal, () => abandon(signal));
}

function run(testFile, onChild) {
  return new Promise((resolve) => {
    const child = spawn("node", ["--disable-warning=MODULE_TYPELESS_PACKAGE_JSON", "--import", join(here, "register.mjs"), "--test", "--test-timeout=15000", join(here, testFile)], { detached: true, stdio: ["ignore", "pipe", "ignore"] });
    onChild(child);
    const stop = () => {
      try {
        process.kill(-child.pid, "SIGKILL");
      } catch {}
    };
    // The first failing test is enough: a mutant that makes later tests wait out their timeouts is killed as soon as it fails once.
    let failed = false;
    child.stdout.on("data", (chunk) => {
      if (!failed && /^not ok|^\s*✖|^\s+not ok/m.test(chunk.toString())) {
        failed = true;
        stop();
      }
    });
    const timer = setTimeout(() => {
      stop();
      resolve("timeout");
    }, TIMEOUT_MS);
    child.on("exit", (code) => {
      clearTimeout(timer);
      resolve(failed || code !== 0 ? "killed" : "survived");
    });
  });
}

async function main() {
  const only = process.argv.slice(2);
  const chosen = MUTANTS.filter((m) => only.length === 0 || only.includes(m[0]));
  let bad = 0;
  for (const [name, file, find, replace, testFile] of chosen) {
    const original = readFileSync(file, "utf8");
    const count = original.split(find).length - 1;
    if (count !== 1) {
      console.log(`${name}: SKIPPED, the target string occurs ${count} times`);
      bad += 1;
      continue;
    }
    current = { file, original, child: null };
    writeFileSync(file, original.replace(find, () => replace));
    let result;
    try {
      result = await run(testFile, (child) => (current.child = child));
    } finally {
      writeFileSync(file, original);
      current = null;
    }
    if (readFileSync(file, "utf8") !== original) {
      console.log(`${name}: RESTORE FAILED for ${file}`);
      process.exit(2);
    }
    console.log(`${name}: ${result}`);
    if (result !== "killed") {
      bad += 1;
    }
  }
  process.exit(bad === 0 ? 0 : 1);
}

main();
