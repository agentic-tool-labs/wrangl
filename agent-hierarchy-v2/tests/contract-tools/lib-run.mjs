// Case discovery and execution, shared by run.mjs and record.mjs. Imports nothing from hooks/ (G2).

import { executeStateOp } from "./lib-stateop.mjs";
import { spawnSync } from "node:child_process";
import {
  CONTRACTS, PLUGIN, extraCtx, buildEnv, compareExpect, existsSync, join, materializeTree, mkdirSync, normalizeRun, readFileSync, readdirSync,
  relative, resolvePlaceholders, rules, runProcess, seedPlantedTree, snapshot, statSync, toPluginNamespace, treeDiff, withV1ConventionsFallback,
} from "./lib-case.mjs";
import { createNormalizer } from "./normalize.mjs";
import { request, startDaemon } from "./lib-wire.mjs";

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export const KINDS = ["hook", "cli", "wire", "state-op", "lifecycle"];

function listJson(dir, out = []) {
  if (!existsSync(dir)) return out;
  for (const n of readdirSync(dir).sort()) {
    const p = join(dir, n);
    if (statSync(p).isDirectory()) listJson(p, out);
    else if (n.endsWith(".json")) out.push(p);
  }
  return out;
}

/** Every case file of the given kinds, sorted by path (byte order). Each: {kind, id, file, def}. */
export function loadCases(kinds = KINDS) {
  const roots = {
    hook: join(CONTRACTS, "hooks"),
    cli: join(CONTRACTS, "cli", "cases"),
    wire: join(CONTRACTS, "wire", "cases"),
    "state-op": join(CONTRACTS, "state", "cases"),
  };
  const cases = [];
  for (const kind of kinds) {
    if (kind === "lifecycle") {
      const dir = join(CONTRACTS, "lifecycle");
      if (!existsSync(dir)) continue;
      for (const n of readdirSync(dir).sort().filter((x) => x.endsWith(".sh") && !x.startsWith("_"))) {
        const file = join(dir, n);
        const head = readFileSync(file, "utf8").split("\n").slice(0, 12).join("\n");
        const pending = /^# pending: (\S+)/m.exec(head);
        cases.push({ kind: "lifecycle", id: n.replace(/\.sh$/, ""), file, def: { kind: "lifecycle", id: n.replace(/\.sh$/, ""), pending: pending ? pending[1] : null } });
      }
      continue;
    }
    for (const file of listJson(roots[kind])) {
      const def = withV1ConventionsFallback(JSON.parse(toPluginNamespace(readFileSync(file, "utf8"))));
      if (def.kind !== kind) continue;
      // the display id is the path under the kind's root (cases of different scripts and verbs may share a file name)
      cases.push({ kind, id: relative(roots[kind], file).replace(/\.json$/, ""), file, def });
    }
  }
  return cases;
}

/** `<kind>/<id>` matched against a glob (* = any run of characters). */
export function matchesOnly(c, only) {
  if (!only) return true;
  const re = new RegExp("^" + only.split("*").map((s) => s.replace(/[.+?^${}()|[\]\\]/g, "\\$&")).join(".*") + "$");
  return re.test(`${c.kind}/${c.id}`) || re.test(c.id);
}

let divergenceSet = null;
/**
 * Every key a divergence ledger lists under `excludedCases`. A key is `kind/script/id`, which names every case that shares the id,
 * or `kind/<path>` (the case's display id: its path under the kind's root), which names exactly one case.
 */
function namedDivergences() {
  if (divergenceSet) return divergenceSet;
  divergenceSet = new Set();
  const dir = join(CONTRACTS, "divergence");
  if (existsSync(dir)) {
    for (const f of readdirSync(dir)) {
      if (!f.endsWith(".json")) continue;
      const ledger = JSON.parse(readFileSync(join(dir, f), "utf8"));
      for (const id of ledger.excludedCases || []) divergenceSet.add(id);
    }
  }
  return divergenceSet;
}

const sortKeys = (v) => (Array.isArray(v) ? v.map(sortKeys) : v && typeof v === "object" ? Object.fromEntries(Object.keys(v).sort().map((k) => [k, sortKeys(v[k])])) : v);

/** Runs one hook, CLI or wire case under `impl`; returns {actual} in the shape of `expect`, or {skipped: reason}. */
export async function executeCase(c, { impl, T }) {
  const d = c.def;
  // A case the native answer deliberately differs on is named in the divergence ledger and excluded from the byte-equality diff;
  // its native answer is pinned by a unit row, not by this recording.
  if (impl === "native" && [`${d.kind}/${d.input?.script ?? d.input?.cli ?? ""}/${d.id}`, `${c.kind}/${c.id}`].some((key) => namedDivergences().has(key))) return { skipped: "named-divergence" };
  // a unit run with AH_IMPL=native and no binary exits 70 (tests only), so nothing is compared (and it is never run under Node)
  if ((d.kind === "hook" || d.kind === "cli") && impl === "native" && !process.env.AHD_NATIVE_BIN) return { skipped: "no-native-binary" };
  if (d.kind === "hook") {
    const run = runProcess({ script: join(PLUGIN, "hooks", `${d.input.script}.mjs`), stdin: JSON.stringify(d.input.event) }, { caseDef: d, T, impl });
    if (impl === "native" && notImplementedNatively("hook", run)) return { skipped: "unit-not-native" };
    return { actual: normalizeRun(run) };
  }
  if (d.kind === "cli") {
    const run = runProcess({ script: join(PLUGIN, "hooks", `${d.input.cli}.mjs`), argv: d.input.argv, stdin: d.input.stdin }, { caseDef: d, T, impl });
    if (impl === "native" && notImplementedNatively("cli", run)) return { skipped: "unit-not-native" };
    // `ahd tui` execs ahd-tui from beside the binary; a plain `dotnet build` output has none (install.sh publishes both), so
    // the tui cases are judged only when AHD_NATIVE_BIN is a published or installed ahd.
    if (impl === "native" && run.exit === 70 && /^ahd: tui needs ahd-tui beside /.test(run.stderr)) return { skipped: "no-tui-binary" };
    return { actual: normalizeRun(run) };
  }
  if (d.kind === "wire") return executeWire(d, { impl, T });
  if (d.kind === "state-op") return executeStateOp(c, { impl, T });
  return { skipped: `kind ${d.kind} is not run by this tool yet` };
}

const STATE_NAMES = JSON.parse(readFileSync(new URL("./state-names.json", import.meta.url), "utf8"));

/**
 * True when the run is the exact "not implemented natively" outcome of a unit the binary does not have yet: for a CLI, exit 70
 * with `ahd: unit <u> is not implemented natively`; for a hook, the deny JSON of a gate (`ah: hook <name> is not implemented
 * natively (<reason>)`) or a silent exit 0 with a hook error row saying so. Anything else is judged as a result.
 */
function notImplementedNatively(kind, run) {
  if (kind === "cli") return run.exit === 70 && /ahd: unit \S+ is not implemented natively/.test(`${run.stdout}\n${run.stderr}`);
  if (run.exit !== 0) return false;
  try {
    const j = JSON.parse(run.stdout);
    const why = j && j.hookSpecificOutput && j.hookSpecificOutput.permissionDecisionReason;
    if (typeof why === "string" && /^ah: hook \S+ is not implemented natively \(.*\)$/s.test(why)) return true;
  } catch { /* not JSON */ }
  if (run.stdout.trim() !== "") return false;
  try {
    const rows = readFileSync(join(run.ctx.roots.home, ...STATE_NAMES.hookErrorsLog.split("/")), "utf8");
    return /is not implemented natively/.test(rows);
  } catch { return false; }
}

async function executeWire(d, { impl, T }) {
  if (impl === "native" && !process.env.AHD_NATIVE_BIN) return { skipped: "no-native-binary" };
  if (d.pending) return { skipped: d.pending };
  const caseDir = join(T, d.id.replace(/[^A-Za-z0-9._-]/g, "_") + "-" + Math.random().toString(36).slice(2, 8));
  mkdirSync(caseDir, { recursive: true });
  const ctx = { T, plugin: PLUGIN, caseDir, roots: {} };
  materializeTree(d.tree, caseDir, ctx);
  const env = buildEnv(d.env, ctx, impl);
  seedPlantedTree(d.tree, ctx, env, true);
  const before = snapshot(ctx.roots, rules().ignore_paths);
  let daemon;
  try {
    daemon = await startDaemon({ impl, ah: ctx.roots.ah, env });
  } catch (e) {
    if (e && e.unitNotNative) return { skipped: "unit-not-native" };
    throw e;
  }
  try {
    const inp = d.input;
    // Paths and `{{name}}` captures: a `before` step may carry `capture: {name: "json.a.b"}`; later requests use {{name}}.
    const vars = {};
    const subStr = (s) => resolvePlaceholders(s, ctx).replace(/\{\{([A-Za-z0-9_]+)\}\}/g, (_, n) => (n in vars ? String(vars[n]) : `{{${n}}}`));
    const sub = (x) => (x === undefined || x === null ? x : typeof x === "string" ? subStr(x) : JSON.parse(subStr(JSON.stringify(x))));
    const pick = (obj, dotted) => dotted.split(".").reduce((o, k) => (o == null ? o : o[k]), obj);
    const send = (s) => request(daemon.socket, { method: s.method, path: sub(s.path), query: sub(s.query), headers: s.headers, body: sub(s.body) });
    for (const step of inp.before || []) {
      const r = await send(step);
      for (const [name, where] of Object.entries(step.capture || {})) vars[name] = pick({ json: r.json, status: r.status }, where);
    }
    const bodyIn = inp.body && inp.body.raw_bytes !== undefined ? Buffer.alloc(inp.body.raw_bytes, 0x20) : sub(inp.body);
    let during = null;
    if (inp.during) during = sleep(inp.during.after_ms ?? 300).then(() => send(inp.during.request));
    const t0 = Date.now();
    let res;
    try {
      res = await request(daemon.socket, { method: inp.method, path: sub(inp.path), query: sub(inp.query), headers: inp.headers, body: bodyIn });
    } catch (e) {
      res = { status: `error:${e.code || e.message}`, headers: {}, raw: Buffer.alloc(0), json: null };
    }
    if (during) await during;
    const norm = createNormalizer(rules(), { T, roots: ctx.roots, plugin: PLUGIN, impl: impl === "native" ? "native" : "node", ...extraCtx() });
    // only the headers a case lists are compared (a recorded case lists them in expect.headers; a skeleton in expect_headers)
    const wantedNames = [...Object.keys((d.expect && d.expect.headers) || {}), ...(d.expect_headers || [])];
    const headers = {};
    for (const name of wantedNames) headers[name.toLowerCase()] = res.headers[name.toLowerCase()] ?? null;
    const actual = { status: res.status, headers, json: res.json === null ? null : sortKeys(norm.json(res.json)), elapsed_ms: Date.now() - t0 };
    if (d.expect && d.expect.tree_diff !== undefined) {
      const after = snapshot(ctx.roots, rules().ignore_paths);
      actual.tree_diff = treeDiff(before, after, norm);
    }
    return { actual };
  } finally {
    await daemon.stop();
  }
}

/** Fields compared for each kind. */
export const FIELDS = {
  hook: ["stdout", "stderr", "exit", "tree_diff"],
  cli: ["stdout", "stderr", "exit", "tree_diff"],
  wire: ["status", "headers", "json", "tree_diff"],
  "state-op": ["response", "tree_diff", "readback"],
};

/** The part of a result that is stored as `expect` (wire adds a floor for elapsed time only in the case, never compared). */
export function toExpect(kind, actual, prevExpect) {
  if (kind === "state-op") return { response: actual.response, tree_diff: actual.tree_diff, ...(actual.readback !== undefined ? { readback: actual.readback } : {}) };
  if (kind === "wire") {
    const e = { status: actual.status, headers: actual.headers, json: actual.json };
    if (actual.tree_diff !== undefined) e.tree_diff = actual.tree_diff;
    return e;
  }
  const e = { stdout: actual.stdout, stderr: actual.stderr, exit: actual.exit };
  if (actual.tree_diff) e.tree_diff = actual.tree_diff;
  return e;
}

export { compareExpect };
export function pluginVersion() {
  try { return JSON.parse(readFileSync(join(PLUGIN, ".claude-plugin", "plugin.json"), "utf8")).version; } catch { return "unknown"; }
}
export { spawnSync };
