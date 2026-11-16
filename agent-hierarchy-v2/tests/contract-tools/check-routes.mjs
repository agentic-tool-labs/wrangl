#!/usr/bin/env node
// K1 completeness: tests/contracts/wire/routes.json lists exactly the routes a daemon answers.
//   check-routes.mjs [--routes FILE] [--impl node|native]
// (a) every listed route (method + path, `{param}` filled with a probe value) is answered by the daemon as a route:
//     a status other than 404, or a 404 whose message is not "no such path" (an unknown join id or state op is a route's own 404);
// (b) the daemon's own contract document (GET /v1/contract) lists exactly the non-hidden routes of the file;
// (c) an unlisted /v1 path is 404 "no such path".
// Exit 0 when all hold, 1 otherwise, 2 on usage. Imports nothing from hooks/ (G2).

import { readFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { CONTRACTS, buildEnv, makeSandbox, removeSandbox } from "./lib-case.mjs";
import { request, startDaemon } from "./lib-wire.mjs";

const argv = process.argv.slice(2);
const val = (n, d) => { const i = argv.indexOf(n); return i >= 0 ? argv[i + 1] : d; };
const routesFile = val("--routes", join(CONTRACTS, "wire", "routes.json"));
const impl = val("--impl", "node");
if (!["node", "native"].includes(impl)) { console.error("check-routes: --impl needs node or native"); process.exit(2); }
const doc = JSON.parse(readFileSync(routesFile, "utf8"));
const routes = Array.isArray(doc) ? doc : doc.routes;

let T;
try { T = makeSandbox(); } catch (e) { console.error(`check-routes: ${e.message}`); process.exit(2); }
const failures = [];
try {
  const roots = { home: join(T, "home"), ah: join(T, "ah"), proj: join(T, "proj") };
  for (const d of Object.values(roots)) mkdirSync(d, { recursive: true });
  const env = buildEnv({}, { roots, T, caseDir: T, plugin: "" }, impl);
  const daemon = await startDaemon({ impl, ah: roots.ah, env });
  try {
    // a drain ends the daemon, so it is probed last
    const ordered = [...routes.filter((r) => r.path !== "/v1/drain"), ...routes.filter((r) => r.path === "/v1/drain")];
    for (const r of ordered) {
      const path = r.path.replace("{*id}", "member:probe/tm/probe").replace("{id}", "j-probe").replace("{domain}", "peers").replace("{op}", "read");
      const body = r.method === "POST" ? {} : null;
      const res = await request(daemon.socket, { method: r.method, path, body });
      const missing = res.status === 404 && res.json && res.json.error && /^no such path/.test(res.json.error.message || "");
      if (missing) failures.push(`${r.method} ${r.path}: listed, but the daemon answers "no such path"`);
    }
  } finally {
    await daemon.stop();
  }
  // (b) and (c) on a fresh daemon: the drain above ended the first
  const d2 = await startDaemon({ impl, ah: roots.ah, env });
  try {
    const c = await request(d2.socket, { method: "GET", path: "/v1/contract" });
    // the contract document names one state write (msgs/create) and the generic read; routes.json has the generic path for both methods
    const generic = (p) => (/^\/v1\/state\/[^/]+\/[^/]+$/.test(p) ? "/v1/state/{domain}/{op}" : p);
    const served = new Set((c.json.routes || []).map((r) => `${r.method} ${generic(r.path)}`));
    const listed = new Set(routes.filter((r) => !r.hidden).map((r) => `${r.method} ${r.path}`));
    for (const k of served) if (!listed.has(k)) failures.push(`${k}: in the daemon's contract but missing from routes.json`);
    for (const k of listed) if (!served.has(k)) failures.push(`${k}: in routes.json but not in the daemon's contract`);
    const u = await request(d2.socket, { method: "GET", path: "/v1/__not_listed__" });
    if (!(u.status === 404 && u.json && u.json.error && /^no such path/.test(u.json.error.message || ""))) failures.push(`/v1/__not_listed__: expected 404 "no such path", got ${u.status}`);
  } finally { await d2.stop(); }
} catch (e) {
  failures.push(`tooling: ${e.message}`);
} finally {
  try { removeSandbox(T); } catch { /* gone */ }
}
if (failures.length) { for (const f of failures) console.log("FAIL " + f); process.exit(1); }
console.log(`check-routes: ${routes.length} routes listed, all answered; the contract document and routes.json agree`);
