import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile, readdir } from "node:fs/promises";
import { createStore, freshness, VIEWS } from "../../web/ui/store.js";
import { START_VIEWS } from "../../web/ui/app.js";

// The first load reads one manifest per view the page names; a view the daemon does not serve fails the whole load.

const served = async () => {
  const source = await readFile(new URL("../../native/src/Ah.Watch/Model/ReadModel.cs", import.meta.url), "utf8");
  const table = source.slice(source.indexOf("PrefixesOf(string view)"));
  return new Set([...table.slice(0, table.indexOf("_ => null")).matchAll(/^\s*"([a-z]+)" =>/gm)].map((m) => m[1]));
};

// The shape of a served manifest: epoch, seq, building, slices of {id, version, as_of, body}, pending.
const manifestOf = (view, seq = 152) => ({
  epoch: "e1",
  seq,
  building: false,
  slices: VIEWS[view].filter((p) => !p.startsWith("list:")).map((p) => ({ id: `${p}x`, version: 1, as_of: 0, body: { name: view } })),
  pending: [],
});

const idle = () => new Promise(() => {});
const quick = (fn) => setTimeout(fn, 0);

async function settle(store, until) {
  for (let i = 0; i < 50 && !until(store.state); i++) {
    await new Promise((r) => setTimeout(r, 5));
  }
}

test("the Overview's layout keys off the view class, which is there before its module arrives, so the rail does not jump", async () => {
  const css = await readFile(new URL("../../web/ui/r6-nav.css", import.meta.url), "utf8");
  assert.ok(css.includes(".frame-body:has(.view-overview) > .rail"));
  assert.ok(!css.includes(":has(.overview)"), "negative: nothing waits for the lazy view's own section");
});

test("the landing page does not fetch the Graph stages, its rail or the Overview; the graph code keys.js needs stays static", async () => {
  const app = await readFile(new URL("../../web/ui/app.js", import.meta.url), "utf8");
  const staticImports = [...app.matchAll(/^import .* from "\.\/([^"]+)";$/gm)].map((m) => m[1]);
  for (const late of ["graph-pack.js", "graph-radial.js", "graph-panel.js", "overview.js"]) {
    assert.ok(!staticImports.includes(late), `${late} is imported at load`);
    assert.ok(app.includes(`import("./${late}")`), `${late} is not loaded on demand`);
  }
  assert.ok(staticImports.includes("graph.js"), "the check reads the static imports");
  const index = await readFile(new URL("../../web/ui/index.html", import.meta.url), "utf8");
  const preloaded = [...index.matchAll(/rel="modulepreload" href="\/ui\/([^"]+)"/g)].map((m) => m[1]);
  for (const late of ["graph-pack.js", "graph-radial.js", "graph-panel.js", "overview.js"]) {
    assert.ok(!preloaded.includes(late), `${late} is preloaded`);
  }
  assert.ok(preloaded.includes("graph.js"), "the check reads the preload list");
});

test("the page asks only for views the daemon serves", async () => {
  const names = await served();
  assert.deepEqual(new Set(Object.keys(VIEWS)), names);
  for (const v of START_VIEWS) {
    assert.ok(names.has(v), `${v} is not served`);
  }
});

test("a captured-shape manifest for every view the page names is accepted and the poll starts", async () => {
  const polls = [];
  const store = createStore({ get: (p, params) => (polls.push([p, params]), idle()), manifest: async (v) => manifestOf(v), setTimer: quick });
  store.start(START_VIEWS);
  await settle(store, (s) => s.ready);
  assert.equal(store.state.ready, true);
  assert.equal(store.state.errorSince, null);
  assert.equal(store.state.freshAt > 0, true);
  assert.equal(store.state.slices.size, START_VIEWS.reduce((n, v) => n + manifestOf(v).slices.length, 0));
  assert.equal(polls[0][0], "/events");
  assert.equal(polls[0][1].since, 152);
  store.stop();
});

test("a view that cannot be read leaves the reason in the state instead of a silent retry", async () => {
  const manifest = async (v) => {
    if (v === "summary") {
      throw new Error("view summary: 404");
    }
    return manifestOf(v);
  };
  const store = createStore({ get: idle, manifest, setTimer: quick });
  store.start(START_VIEWS);
  await settle(store, (s) => s.error !== null);
  assert.equal(store.state.ready, false);
  assert.equal(store.state.error, "view summary: 404");
  assert.match(freshness(store.state, 1000).text, /^reconnecting/);
  store.stop();
});

test("a leased slice the store does not hold is asked for fresh; one it holds, a view and a list are not", async () => {
  const polls = [];
  const store = createStore({ get: (p, params) => (polls.push(params), idle()), manifest: async (v) => manifestOf(v), setTimer: quick });
  store.start(["tree"]);
  await settle(store, (s) => s.ready);
  store.lease("pane", ["transcript:d/s", "list:transcript:d/s", "team:x"]);
  await settle(store, () => polls.some((p) => p.fresh));
  const last = polls.findLast((p) => p.fresh);
  assert.equal(last.fresh, "transcript:d/s", "only the unheld slice id");
  assert.ok(!polls.some((p) => p.fresh?.includes("view:") || p.fresh?.includes("list:")));
  assert.equal(polls[0].fresh, undefined, "nothing leased: nothing fresh");
  store.stop();
});

test("the store counts page reads in flight: busy while one is out, zero after it answers or fails", async () => {
  let release;
  const gate = new Promise((r) => (release = r));
  let fail = false;
  const get = async (p) => {
    if (p === "/events") {
      return idle();
    }
    await gate;
    if (fail) {
      throw new Error("view list: 404");
    }
    return { rows: [], next: null, prev: null };
  };
  const store = createStore({ get, manifest: async (v) => manifestOf(v), setTimer: quick });
  store.start(["tree"]);
  await settle(store, (s) => s.ready);
  assert.equal(store.state.busy, 0);
  const first = store.page("list:feed:x", { limit: 1 });
  const second = store.page("list:feed:y", { limit: 1 });
  assert.equal(store.state.busy, 2);
  release();
  await Promise.all([first, second]);
  assert.equal(store.state.busy, 0);
  fail = true;
  await assert.rejects(store.page("list:feed:z", { limit: 1 }), /404/);
  assert.equal(store.state.busy, 0);
  store.stop();
});

test("the reason clears once a read succeeds", async () => {
  let fail = true;
  const manifest = async (v) => {
    if (fail) {
      fail = false;
      throw new Error("view tree: 500");
    }
    return manifestOf(v);
  };
  const store = createStore({ get: idle, manifest, setTimer: quick });
  store.start(["tree"]);
  await settle(store, (s) => s.ready);
  assert.equal(store.state.error, null);
  assert.equal(store.state.errorSince, null);
  store.stop();
});

test("an ended session is named as ended, not as reconnecting", () => {
  assert.equal(freshness({ ended: true, errorSince: null, freshAt: 0 }, 5).text, "session ended");
  assert.match(freshness({ ended: false, errorSince: 1, freshAt: 0 }, 5000).text, /^reconnecting/);
});

// Every list and slice id the client builds, and every route it calls, must be one the daemon serves.

async function serverSource() {
  const dir = new URL("../../native/src/Ah.Watch/Model/", import.meta.url);
  const names = (await readdir(dir)).filter((n) => n.endsWith(".cs"));
  const text = await Promise.all(names.map((n) => readFile(new URL(n, dir), "utf8")));
  const session = await readFile(new URL("../../native/src/Ah.Daemon/Ui/UiSession.cs", import.meta.url), "utf8");
  return { model: text.join("\n"), session };
}

async function clientSource() {
  const dir = new URL("../../web/ui/", import.meta.url);
  const names = (await readdir(dir)).filter((n) => n.endsWith(".js") && n !== "boot.js");
  return (await Promise.all(names.map((n) => readFile(new URL(n, dir), "utf8")))).join("\n");
}

test("every list kind the client builds has a pager prefix on the daemon", async () => {
  const { model } = await serverSource();
  const client = await clientSource();
  const kinds = new Set([...client.matchAll(/list:([a-z]+)/g)].map((m) => m[1]));
  assert.ok(client.includes("lid: `list:${hid}`") && client.includes("const hid = `transcript:"), "the transcript list id is list: plus the header id");
  kinds.add("transcript");
  for (const kind of kinds) {
    assert.ok(model.includes(`"list:${kind}`), `no server list prefix list:${kind}`);
  }
  assert.ok(!model.includes('"list:nosuchkind'), "the check can fail");
});

test("every slice id head the client reads or leases is published by a lane on the daemon", async () => {
  const { model } = await serverSource();
  const heads = ["transcript", "files", "tests", "activity", "processes", "timeline", "status", "member", "team", "topology", "worktree", "exchange"];
  const client = await clientSource();
  for (const head of heads) {
    if (client.includes(`${head}:`)) {
      assert.ok(model.includes(`"${head}:`) || model.includes(`"${head}"`), `no server slice ${head}:`);
    }
  }
});

test("every route class the client calls is in the listener's route table", async () => {
  const { session } = await serverSource();
  const table = session.slice(session.indexOf("RouteTable"), session.indexOf("];", session.indexOf("RouteTable")));
  const { urlOf } = await import("../../web/ui/bridge.js");
  const calls = [urlOf("/events", { since: 1 }), urlOf("/list/list:transcript:d/s", { limit: 1 }), urlOf("/actions"), urlOf("/actions/member:d/t/m"), urlOf("/read-model")];
  const served = (url) => {
    const path = new URL(url, "http://x").pathname;
    return [...table.matchAll(/"[A-Z]+ ([^"]+)"/g)].some(([, pattern]) => new RegExp(`^${pattern.replace(/\{\*[a-z]+\}/g, ".+").replace(/\{[a-z]+\}/g, "[^/]+")}$`).test(path));
  };
  for (const url of calls) {
    assert.ok(served(url), `${url} is not a listener route`);
  }
  assert.ok(!served("/ui/v1/nosuch"), "the check can fail");
});
