// Runs one state-op case (`kind: "state-op"`) two ways and requires that they agree:
//   1. over the route: POST /v1/state/<domain>/<op> (a write) or GET with `args` and the context in the query (a read), to the
//                   native daemon
//   2. one-shot:    `ahd state <domain> <op>` with the request body on stdin (a write only; the verb refuses a read)
// Setup operations run through the one-shot verb. Each way gets its own fresh copy of the case's tree; the main operation's
// response and the tree diff it caused are normalized with one normalizer per run and compared. Imports nothing from hooks/ (G2).

import { spawnSync } from "node:child_process";
import { mkdirSync } from "node:fs";
import { join } from "node:path";
import {
  PLUGIN, buildEnv, extraCtx, materializeTree, resolvePlaceholders, rules, snapshot, treeDiff, withPeerLog, withGatesLog, withLinksView, PEER_LOG_VIEW, withRosterLog, ROSTER_LOG_VIEW,
} from "./lib-case.mjs";
import { createNormalizer } from "./normalize.mjs";
import { request, startDaemon } from "./lib-wire.mjs";

/** The case's request with placeholders resolved for this run: {domain, op, args, context, op_id}. */
function requestOf(def, ctx) {
  const sub = (x) => (x === undefined || x === null ? x : JSON.parse(resolvePlaceholders(JSON.stringify(x), ctx)));
  const b = def.input.body || {};
  return { domain: def.input.domain, op: def.input.op, args: sub(b.args) || {}, context: sub(b.context) || {}, op_id: b.op_id || null };
}

const toResponse = (ok, result, code, message) => (ok ? { ok: true, result: result === undefined ? null : result } : { ok: false, code: code || null, message: message || "" });

/** The route: a read is a GET, a write a POST (the daemon says which with a 400 for the wrong one). */
async function overRoute(rq, daemon, kind) {
  const path = `/v1/state/${rq.domain}/${rq.op}`;
  let res;
  if (kind === "read") {
    const query = { args: JSON.stringify(rq.args), ...Object.fromEntries(Object.entries(rq.context).map(([k, v]) => [k, typeof v === "string" ? v : JSON.stringify(v)])) };
    res = await request(daemon.socket, { method: "GET", path, query });
  } else {
    res = await request(daemon.socket, { method: "POST", path, body: { args: rq.args, context: rq.context, op_id: rq.op_id } });
  }
  const j = res.json;
  if (res.status === 200 && j && "result" in j) return toResponse(true, j.result);
  const e = (j && j.error) || {};
  // an operation's own failure is a 422 `op_failed` carrying the library's code in `error_code` (null when it set none)
  const code = e.code === "op_failed" ? e.error_code || null : e.error_code || e.code || `http ${res.status}`;
  return toResponse(false, null, code, e.message || "");
}

/** The one-shot verb: the same body as the route's, on stdin. */
function oneShot(rq, env, cwd) {
  const r = spawnSync(env.AHD_NATIVE_BIN, ["state", rq.domain, rq.op, "--cwd", cwd], { cwd, env, input: JSON.stringify({ args: rq.args, context: rq.context, op_id: rq.op_id }), encoding: "utf8", timeout: 60000 });
  let j;
  try { j = JSON.parse((r.stdout || "").trim().split("\n").pop()); } catch { return toResponse(false, null, "driver_error", (r.stderr || r.stdout || "no output").slice(0, 300)); }
  if (j && "result" in j) return toResponse(true, j.result);
  const e = (j && j.error) || {};
  // the route's mapping: an op's own failure is `op_failed` carrying the library's code (null when it set none)
  const code = e.code === "op_failed" ? e.error_code || null : e.error_code || e.code || `exit ${r.status}`;
  return toResponse(false, null, code, e.message || "");
}

/**
 * Runs a case with every driver the runtime allows. Returns {actual: {response, tree_diff}, drivers: {name: {response, tree_diff}},
 * disagree: [driver names that differ from the first]} or {skipped}.
 */
export async function executeStateOp(c, { impl, T }) {
  const d = c.def;
  const native = impl === "native";
  if (native && !process.env.AHD_NATIVE_BIN) return { skipped: "no-native-binary" };
  const opKind = d.input.kind; // "read" or "write", from the registry when the case was seeded
  // the one-shot verb is the writer's: it refuses a read operation (`use GET`), so a read has the route alone
  const drivers = opKind === "read" ? ["route"] : ["route", "one-shot"];
  const results = {};
  for (const name of drivers) {
    // short: the daemon's socket path inside it must stay under the 103-byte limit
    const caseDir = join(T, `s${Math.random().toString(36).slice(2, 8)}`);
    mkdirSync(caseDir, { recursive: true });
    const ctx = { T, plugin: PLUGIN, caseDir, roots: {} };
    materializeTree(d.tree, caseDir, ctx);
    const env = buildEnv(d.env, ctx, impl);
    if (native && process.env.AHD_NATIVE_BIN) env.AHD_NATIVE_BIN = process.env.AHD_NATIVE_BIN;
    const cwd = resolvePlaceholders(d.cwd || "<ROOT:proj>", ctx);
    mkdirSync(cwd, { recursive: true });
    const ignore = rules().ignore_paths;
    let daemon = null;
    try {
      if (name === "route") daemon = await startDaemon({ impl, ah: ctx.roots.ah, env });
      // setup operations run in every sandbox, so each driver starts from the same state
      for (const s of d.input.setup || []) {
        const rs = requestOf({ input: s }, ctx);
        if (daemon) await overRoute(rs, daemon, s.kind);
        else if (s.kind !== "read") oneShot(rs, env, cwd);
      }
      const before = withLinksView(withRosterLog(withGatesLog(withPeerLog(snapshot(ctx.roots, ignore), ctx), ctx), ctx), ctx);
      const rq = requestOf(d, ctx);
      let response;
      if (name === "route") response = await overRoute(rq, daemon, opKind);
      else response = oneShot(rq, env, cwd);
      const after = withLinksView(withRosterLog(withGatesLog(withPeerLog(snapshot(ctx.roots, ignore), ctx), ctx), ctx), ctx);
      const norm = createNormalizer(rules(), { T, roots: ctx.roots, plugin: PLUGIN, impl: native ? "native" : "node", ...extraCtx() });
      const response_n = JSON.parse(norm.text("stdout", JSON.stringify(response)));
      // The status refresh that follows a write (`afterWrite`) differs between the in-process call and the daemon; it is the
      // status store's own contract, so a case may leave its files out of the comparison. They are dropped before the
      // normalizer sees them: it numbers timestamps by first appearance, so a file left out afterwards would still shift
      // every number after it.
      const side = (p) => /(^|\/)\.state-status\//.test(p) || /(^|\/)status\.json$/.test(p);
      // The state database is created by ahd when it starts (before the snapshot) and by the one-shot writer at the first msgs
      // write (after it), so its files appear for one driver only; the stored rows are pinned by a read case (msgs/list).
      // The peer-log view (withPeerLog) is kept: it is the rows, not the database's files, and is the same for both drivers.
      const store = (p) => /^ah\/state\//.test(p) && p !== PEER_LOG_VIEW && p !== ROSTER_LOG_VIEW;
      const keep = (snap) => new Map([...snap].filter(([p]) => !store(p) && !(d.input.ignore_status && side(p))));
      const tree = treeDiff(keep(before), keep(after), norm);
      results[name] = { response: response_n, tree_diff: tree };
      // `readback`: `ah` commands run after the operation in the same sandbox, so a write the store lost changes what they print
      if (d.input.readback) {
        results[name].readback = d.input.readback.map((argv) => {
          const r = spawnSync("sh", [join(PLUGIN, "bin", "ah"), ...JSON.parse(resolvePlaceholders(JSON.stringify(argv), ctx))], { cwd, env, encoding: "utf8", timeout: 60000 });
          return { stdout: norm.text("stdout", r.stdout || ""), exit: r.status };
        });
      }
    } catch (e) {
      if (e && e.unitNotNative) return { skipped: "unit-not-native" };
      throw e;
    } finally {
      if (daemon) await daemon.stop();
    }
  }
  const first = drivers[0];
  const disagree = drivers.slice(1).filter((n) => JSON.stringify(results[n]) !== JSON.stringify(results[first]));
  return { actual: results[first], drivers: results, disagree, oneShotSkipped: !native };
}
