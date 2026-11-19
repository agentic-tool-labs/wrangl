#!/usr/bin/env node
// The web UI on mock data: `node web/mock/serve.mjs [--port N] [--no-tick] [--churn] [--turns <member>=<count>]...`.
// --churn: every third tick a worker joins, up to three, then they leave one at a time.
// A second source for the same /ui routes and documents the daemon's bridge serves, bound to 127.0.0.1 only.
// Every response carries `X-Ah-Mock: 1`; the page reads that and shows "MOCK DATA". The daemon never sends it.
import { createServer } from "node:http";
import { readFileSync, statSync } from "node:fs";
import { dirname, extname, join, normalize } from "node:path";
import { fileURLToPath } from "node:url";
import { createMock } from "./model.mjs";
import { allowedKeys } from "./list-keys.mjs";
import { answerReply } from "./questions.mjs";
import { createRules } from "./rules.mjs";
import { TEAM } from "./fixture.mjs";
import { TRANSCRIPT_NAMES, setTurnCount } from "./transcripts.mjs";

const UI = join(dirname(fileURLToPath(import.meta.url)), "..", "ui");
const TYPES = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".svg": "image/svg+xml", ".woff2": "font/woff2" };
const CSP = "default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
const TICK_MS = 4000;
const rules = createRules({ decisions: TEAM });

// The `--turns <member>=<count>` options: how many turns the mock builds for a member (the default is 18).
export function turnsFromArgs(args) {
  return args.flatMap((arg, i) => {
    if (arg !== "--turns") {
      return [];
    }
    const [name, count, ...rest] = (args[i + 1] ?? "").split("=");
    if (!TRANSCRIPT_NAMES.includes(name) || !/^[1-9]\d*$/.test(count ?? "") || rest.length > 0) {
      throw new Error(`--turns wants <member>=<count> with a member of ${TRANSCRIPT_NAMES.join(", ")} and a positive count`);
    }
    return [[name, Number(count)]];
  });
}

const DIRECT = /^\/ui\/v1\/(list\/|actions(\/|$)|read-model$)/;

export function createMockServer({ model = createMock(), host = "127.0.0.1" } = {}) {
  let port = 0;
  const server = createServer(async (req, res) => {
    const headers = { "Content-Security-Policy": CSP, "X-Frame-Options": "DENY", "Referrer-Policy": "no-referrer", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "X-Ah-Mock": "1" };
    const send = (status, type, body) => {
      res.writeHead(status, { ...headers, "Content-Type": type, "Content-Length": Buffer.byteLength(body) });
      res.end(body);
    };
    const json = (status, value) => send(status, "application/json", JSON.stringify(value));
    try {
      const url = new URL(req.url, `http://${host}:${port}`);
      if (req.headers.host !== `${host}:${port}`) {
        return json(421, { error: "host" });
      }
      const site = req.headers["sec-fetch-site"];
      // A page load a tool or a link started may be cross-site; only a top-level document navigation to a static file is let through.
      const pageLoad = req.method === "GET" && req.headers["sec-fetch-mode"] === "navigate" && req.headers["sec-fetch-dest"] === "document" && !url.pathname.startsWith("/ui/v1/");
      if (!pageLoad && ((req.headers.origin && req.headers.origin !== `http://${host}:${port}`) || (site && site !== "same-origin" && site !== "none"))) {
        return json(403, { error: "origin" });
      }
      if (url.pathname === "/ui/v1/team-close") {
        return json(200, { ok: true, preview: true, live: [], unknown: [], held: [], left_running: [] });
      }
      if (url.pathname === "/ui/v1/team-rename") {
        let body;
        try {
          body = JSON.parse(await readBody(req));
        } catch {
          return json(400, { error: "bad-request", detail: "the body must be one JSON object" });
        }
        const name = typeof body.display_name === "string" ? body.display_name.trim() : null;
        return model.rename(body.team, name || null) ? json(200, { ok: true, team: body.team, display_name: name || null, previous: null }) : json(409, { refused: true, reason: "unknown-team", detail: `no team ${body.team}` });
      }
      if (url.pathname === "/ui/v1/rules") {
        if (req.method !== "GET" && req.method !== "POST") {
          return json(405, { error: "method" });
        }
        if (req.method === "GET") {
          return json(...rules.read(url.searchParams.get("team")));
        }
        try {
          return json(...rules.write(JSON.parse(await readBody(req))));
        } catch {
          return json(400, { code: 2, message: "body is not JSON" });
        }
      }
      // The answer write; the mock answers it and writes nothing.
      if (url.pathname === "/ui/v1/answer") {
        if (req.method !== "POST") {
          return json(405, { error: "method" });
        }
        let body;
        try {
          body = JSON.parse(await readBody(req));
        } catch {
          return json(400, { error: "bad-request", detail: "body is not JSON" });
        }
        return json(...answerReply(body));
      }
      if (url.pathname === "/ui/v1/view" || url.pathname.startsWith("/ui/v1/view/") || DIRECT.test(url.pathname)) {
        if (req.method !== "GET") {
          return json(405, { error: "method" });
        }
        return await serveView(url, req, res, model, json);
      }
      if (req.method === "GET" && (url.pathname === "/ui/" || url.pathname.startsWith("/ui/"))) {
        return serveStatic(url.pathname, send, json);
      }
      return json(404, { error: "not_found" });
    } catch {
      return res.headersSent ? res.end() : json(500, { error: "internal" });
    }
  });

  return {
    model,
    server,
    async listen(wanted = 0) {
      await new Promise((ok) => server.listen(wanted, host, ok));
      port = server.address().port;
      return port;
    },
    url: () => `http://${host}:${port}/ui/`,
    close: () => new Promise((ok) => (server.closeAllConnections(), server.close(ok))),
  };
}

const readBody = (req) =>
  new Promise((ok, fail) => {
    let text = "";
    req.on("data", (c) => {
      text += c;
      if (text.length > 4096) {
        fail(new Error("too large"));
      }
    });
    req.on("end", () => ok(text));
  });

async function serveView(url, req, res, model, json) {
  const manifest = url.pathname.slice("/ui/v1/view/".length);
  if (url.pathname.startsWith("/ui/v1/view/") && manifest) {
    const doc = model.manifest(manifest);
    return doc ? json(200, doc) : json(404, { error: "not_found" });
  }
  const q = url.searchParams;
  // The daemon serves lists, actions and the read model at their own routes; the view route also takes them as ?path=.
  const path = DIRECT.test(url.pathname) ? url.pathname.slice("/ui/v1".length) : (q.get("path") ?? "/");
  if (path === "/events") {
    const abort = new AbortController();
    res.on("close", () => abort.abort());
    const sub = q.get("sub");
    const page = await model.poll({
      since: q.has("since") ? Number(q.get("since")) : 0,
      epoch: q.get("epoch"),
      sub: sub === null ? null : sub.split(",").map((s) => s.trim()).filter(Boolean),
      fresh: (q.get("fresh") ?? "").split(",").filter(Boolean),
      wait: Math.min(25, Number(q.get("wait") ?? 0) || 0),
      signal: abort.signal,
    });
    return res.writableEnded ? undefined : json(200, page);
  }
  if (path === "/actions" || path.startsWith("/actions/")) {
    const id = path === "/actions" ? null : decodeURIComponent(path.slice("/actions/".length));
    if (id !== null && !id.startsWith("member:")) {
      return json(400, { error: "bad_member" });
    }
    const doc = model.actions(id);
    return doc ? json(200, doc) : json(404, { error: "no_node" });
  }
  if (path === "/read-model") {
    const doc = model.readModel();
    return doc ? json(200, doc) : json(404, { error: "no_read_model" });
  }
  if (path.startsWith("/list/")) {
    const allowed = allowedKeys(decodeURIComponent(path.slice(6)));
    const unsupported = [...q.keys()].find((key) => key !== "path" && !allowed.has(key));
    if (unsupported !== undefined) {
      return json(400, { error: "bad_filter", key: unsupported });
    }
    if (q.has("after") && q.has("before")) {
      return json(400, { error: "bad_cursor" });
    }
    if ((q.has("member") && !q.get("member").startsWith("member:")) || (q.has("with") && (!q.has("member") || !q.get("with").startsWith("member:")))) {
      return json(400, { error: "bad_member" });
    }
    const doc = model.list(decodeURIComponent(path.slice(6)), { after: q.get("after"), before: q.get("before"), limit: q.get("limit") ?? 50, from: q.get("from_ms"), to: q.get("to_ms"), expand: q.get("expand"), member: q.get("member"), with: q.get("with"), item: q.get("item") });
    return doc ? json(200, doc) : json(404, { error: "not_found" });
  }
  return json(404, { error: "not_found" });
}

function serveStatic(pathname, send, json) {
  const rel = pathname === "/ui/" ? "index.html" : pathname.slice(4);
  const file = normalize(join(UI, rel));
  if (!file.startsWith(UI + "/") || !TYPES[extname(file)]) {
    return json(404, { error: "not_found" });
  }
  try {
    if (!statSync(file).isFile()) {
      return json(404, { error: "not_found" });
    }
    return send(200, TYPES[extname(file)], readFileSync(file));
  } catch {
    return json(404, { error: "not_found" });
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const wanted = args.includes("--port") ? Number(args[args.indexOf("--port") + 1]) : 0;
  for (const [name, count] of turnsFromArgs(args)) {
    setTurnCount(name, count);
  }
  const mock = createMockServer();
  await mock.listen(wanted);
  if (!args.includes("--no-tick")) {
    let ticks = 0;
    setInterval(() => {
      mock.model.tick();
      mock.model.traffic();
      if (args.includes("--churn") && ++ticks % 3 === 0) {
        mock.model.churn();
      }
    }, TICK_MS).unref();
  }
  console.log(`MOCK DATA: not the daemon. Open ${mock.url()}`);
  const stop = () => mock.close().then(() => process.exit(0));
  process.on("SIGINT", stop);
  process.on("SIGTERM", stop);
  // A mock started by a harness or a member must not outlive it: when the parent is gone this process is re-parented, so a changed
  // ppid ends it. One started with no parent to lose (ppid 1) is left alone.
  const parent = process.ppid;
  if (parent > 1) {
    setInterval(() => { if (process.ppid !== parent) stop(); }, 1000).unref();
  }
}
