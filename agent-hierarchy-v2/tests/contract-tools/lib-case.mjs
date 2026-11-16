// Shared by the contract runner and the recorder: sandbox rules, fixture trees, the environment subset, running one
// process under a case, the tree diff, and comparing an actual result with a case's `expect` after normalization.
// Imports nothing from hooks/ (G2). Zero dependencies.

import { execFileSync, spawnSync } from "node:child_process";
import {
  chmodSync, cpSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, readlinkSync, realpathSync,
  rmSync, statSync, symlinkSync, writeFileSync,
} from "node:fs";
import { createHash } from "node:crypto";
import { DatabaseSync } from "node:sqlite";
import { basename, dirname, join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { createNormalizer, loadRules } from "./normalize.mjs";

/** The plugin's version, as the manifest states it. */
export function pluginVersionOf() {
  try { return JSON.parse(readFileSync(join(PLUGIN, ".claude-plugin", "plugin.json"), "utf8")).version; } catch { return ""; }
}

/** The plugin's own name, the namespace its agents and skills are known by; `wrangl` when the manifest names none. */
export function pluginNameOf() {
  try { return JSON.parse(readFileSync(join(PLUGIN, ".claude-plugin", "plugin.json"), "utf8")).name || "wrangl"; } catch { return "wrangl"; }
}

// the recordings name the Node plugin's agents and skills (`ah:architect`, `/ah:agent-role`, `ah:*`, `"ah:"`); the ask-class tag
// `ah:<class>` is a fixed protocol token the product prints as is, in every plugin namespace; so is the allow rule `Bash(ah:*)`
// (and `Bash(<PLUGIN>/bin/ah:*)`), which names the `ah` binary, not the namespace
const RECORDED_NAMESPACE = /(?<![A-Za-z0-9_-])ah:(?!<class>)(?!\*\))(?=[a-z*<{"])/g;

// the recordings keep state in the v1 layout: the state home, the project directory and the fixed names under `.claude`
const RECORDED_LAYOUT = [
  [/(?<![A-Za-z0-9_-])(home|pw|<ROOT:home>)\/\.claude\/hierarchy(?![A-Za-z0-9._-])/g, "$1/.claude/ah2/hierarchy"],
  [/\.claude\/agent-hierarchy(?=\.[A-Za-z0-9_])/g, ".claude/ah2/agent-hierarchy"],
  [/\.claude\/agent-hierarchy(?![A-Za-z0-9._-])/g, ".claude/ah2"],
  [/\.claude\/hierarchy(?![A-Za-z0-9._-])/g, ".claude/ah2"],
  [/\.claude\/ah-conventions\.json/g, ".claude/ah2/ah-conventions.json"],
];

/** `text` with the recorded plugin namespace and state layout rewritten to this plugin's own: the named divergences of the agent prefix and the homes. */
export function toPluginNamespace(text) {
  const own = pluginNameOf();
  if (own === "ah") return text;
  let out = text.replace(RECORDED_NAMESPACE, `${own}:`);
  for (const [re, to] of RECORDED_LAYOUT) out = out.replace(re, to);
  return out;
}

// ah2 reads ah v1's committed conventions path when its own is absent (divergence conventions-v1-fallback): a recorded git log
// whose probe of the conventions blob has no blob read after it (the file was absent) gains the probe of v1's path.
const CONVENTIONS_PROBE = /^(.*\bcat-file -e (\S+?):)\.claude\/ah2\/ah-conventions\.json\n/gm;

/** `value` (a case definition, any depth) with each recorded absent-conventions probe followed by the probe of v1's path. */
export function withV1ConventionsFallback(value) {
  if (typeof value === "string") {
    // scan-v1-paths: allow the replayed v1 trace names v1's conventions path
    const v1Probe = (head) => `${head}.claude/ah-conventions.json\n`;
    return value.replace(CONVENTIONS_PROBE, (line, head, ref) =>
      value.includes(`cat-file blob ${ref}:.claude/ah2/ah-conventions.json`) ? line : line + v1Probe(head));
  }
  if (Array.isArray(value)) return value.map(withV1ConventionsFallback);
  if (value && typeof value === "object") return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, withV1ConventionsFallback(v)]));
  return value;
}

/** The path of a tree file under root `r` as this plugin lays it out. */
export function toPluginTreePath(r, rel) {
  const mapped = toPluginNamespace(`${r}/${rel}`);
  return mapped.slice(r.length + 1);
}

/** The normalizer context every run adds: the runner's pid, the plugin's version and the repository that holds the plugin. */
let runnerStartText = null;
export const extraCtx = () => ({ pid: process.pid, pidStart: (runnerStartText ??= runnerStart()), version: pluginVersionOf(), repo: dirname(join(TOOLS_DIR, "..", "..")) });

export const TOOLS_DIR = dirname(fileURLToPath(import.meta.url));
export const PLUGIN = join(TOOLS_DIR, "..", "..");
export const CONTRACTS = join(PLUGIN, "tests", "contracts");

// ---- G3: sandboxes
const SANDBOX_RE = /^\/(?:private\/)?tmp\/ahprobe\.[A-Za-z0-9]{6}$/;

/** True when `p` (after realpath) is a sandbox directory this tooling may create files in and remove. */
export function isSandbox(p) {
  try { return SANDBOX_RE.test(realpathSync(p)) || SANDBOX_RE.test(p); } catch { return SANDBOX_RE.test(p); }
}

/** The run sandbox: `T` from the environment when set (refused unless it is a sandbox path), else a fresh mktemp. */
export function makeSandbox(env = process.env) {
  if (env.T) {
    if (!isSandbox(env.T)) throw Object.assign(new Error(`refusing T=${env.T}: not a /tmp/ahprobe.XXXXXX sandbox`), { usage: true });
    mkdirSync(env.T, { recursive: true });
    return env.T;
  }
  const base = existsSync("/private/tmp") ? "/private/tmp" : "/tmp";
  return mkdtempSync(join(base, "ahprobe."));
}

/** Removes the sandbox. Refuses anything that is not one (the only removal this tooling does). */
export function removeSandbox(T) {
  if (!isSandbox(T)) throw new Error(`refusing to remove ${T}: not a /tmp/ahprobe.XXXXXX sandbox`);
  rmSync(T, { recursive: true, force: true });
}

// ---- rules
let rulesCache = null;
export function rules() {
  if (!rulesCache) rulesCache = loadRules(join(CONTRACTS, "normalize.json"));
  return rulesCache;
}

// ---- fixture trees (§3.1)
const ROOT_NAMES = ["home", "ah", "proj"];

function walkFiles(dir, base = dir, out = []) {
  for (const n of readdirSync(dir).sort()) {
    const p = join(dir, n);
    let st;
    try { st = lstatSync(p); } catch (e) { if (e.code === "ENOENT") continue; throw e; } // a file the daemon removes while the tree is walked
    if (st.isDirectory()) walkFiles(p, base, out);
    else out.push(p);
  }
  return out;
}

const looksText = (buf) => !buf.includes(0);

/**
 * The start time of this process as the daemon's process table reads it: `ps -o lstart=` under the C locale and UTC, the text an
 * owner registration or a team file records as `pid_start`. The owner proof walks a command's ancestors and accepts one only when
 * its pid and this start time both match, so a fixture that names the runner as the Orchestrator needs both.
 */
function runnerStart() {
  return processStart(process.pid);
}

/**
 * The start time of process <pid> in the form {@link runnerStart} gives for this one: what a fixture records as that process's
 * `pid_start`. A non-zero `skewSeconds` gives the same form for an instant that much later or earlier: the start a reused pid would
 * have, to a fixture that must not match the process.
 */
export function processStart(pid, skewSeconds = 0) {
  const r = spawnSync("/bin/ps", ["-o", "lstart=", "-p", String(pid)], { encoding: "utf8", env: { PATH: "/usr/bin:/bin", LC_ALL: "C", TZ: "UTC" } });
  if (r.status !== 0 || !r.stdout.trim()) throw new Error(`cannot read the start time of process ${pid} with ps`);
  const start = r.stdout.trim();
  if (!skewSeconds) return start;
  const at = new Date(Date.parse(`${start} UTC`) + skewSeconds * 1000);
  const [weekday, , month] = at.toUTCString().split(" ");
  return `${weekday.slice(0, 3)} ${month} ${String(at.getUTCDate()).padStart(2, " ")} ${at.toISOString().slice(11, 19)} ${at.getUTCFullYear()}`;
}

/**
 * Resolves `<ROOT:x>`, `<T>` and `<PLUGIN>` placeholders in a string to this case's real paths, and `<PID:runner>` to the
 * pid of the process running the case (always alive for the case's life: a team file's live Orchestrator). `<PIDSTART:runner>`
 * is that process's start time, the `pid_start` that makes the runner provable as an Orchestrator to an owner verb. `<NOW>` is the
 * instant the tree is materialized, as an ISO string; `<NOW-5m>` and `<NOW+2h>` offset it by seconds, minutes or hours.
 */
export function resolvePlaceholders(s, ctx) {
  if (typeof s !== "string") return s;
  return s
    .replace(/<ROOT:(home|ah|proj)>/g, (_, n) => ctx.roots[n])
    .replace(/<T>/g, ctx.T)
    .replace(/<PLUGIN>/g, ctx.plugin)
    .replace(/<PID:runner>/g, String(process.pid))
    .replace(/<PIDSTART:runner>/g, () => runnerStart())
    .replace(/<NOW(?:([+-])(\d+)([smh]))?>/g, (_, sign, n, unit) => {
      const unitMs = { s: 1000, m: 60_000, h: 3_600_000 }[unit || "s"];
      return new Date(Date.now() + (sign === "-" ? -1 : 1) * Number(n || 0) * unitMs).toISOString();
    });
}

/** Copies a tree into a case directory, substituting placeholders in text files and applying recorded modes. */
export function materializeTree(treeId, caseDir, ctx) {
  const roots = {};
  for (const r of ROOT_NAMES) { roots[r] = join(caseDir, r); mkdirSync(roots[r], { recursive: true }); }
  ctx.roots = roots;
  if (!treeId) return roots;
  const treeDir = join(CONTRACTS, "trees", treeId);
  if (!existsSync(treeDir)) throw new Error(`tree not found: ${treeId}`);
  let gitInit = null;
  for (const r of ROOT_NAMES) {
    const src = join(treeDir, r);
    if (!existsSync(src)) continue;
    for (const f of walkFiles(src)) {
      const rel = relative(src, f);
      if (r === "proj" && rel === ".git-init") { gitInit = JSON.parse(readFileSync(f, "utf8")); continue; }
      const dest = join(roots[r], toPluginTreePath(r, rel));
      mkdirSync(dirname(dest), { recursive: true });
      const buf = readFileSync(f);
      writeFileSync(dest, looksText(buf) ? resolvePlaceholders(toPluginNamespace(buf.toString("utf8")), ctx) : buf);
    }
  }
  const modesFile = join(CONTRACTS, "trees", `${treeId}.modes.json`);
  if (existsSync(modesFile)) {
    const modes = JSON.parse(readFileSync(modesFile, "utf8"));
    for (const [recorded, mode] of Object.entries(modes)) {
      const rel = toPluginNamespace(recorded);
      const [root, ...rest] = rel.split("/");
      if (roots[root]) chmodSync(join(roots[root], ...rest), parseInt(mode, 8));
    }
  }
  if (gitInit) replayGit(roots.proj, gitInit);
  return roots;
}

/** Repository settings that keep git from writing into a fixture's .git in the background. */
export const QUIET_GIT_CONFIG = [
  ["gc.auto", "0"],
  ["gc.autoDetach", "false"],
  ["maintenance.auto", "false"],
  ["receive.autogc", "false"],
];

function replayGit(proj, spec) {
  const env = { ...process.env, GIT_AUTHOR_NAME: "t", GIT_AUTHOR_EMAIL: "t@t", GIT_COMMITTER_NAME: "t", GIT_COMMITTER_EMAIL: "t@t", GIT_AUTHOR_DATE: "2026-01-01T00:00:00Z", GIT_COMMITTER_DATE: "2026-01-01T00:00:00Z", GIT_CONFIG_GLOBAL: "/dev/null", GIT_CONFIG_SYSTEM: "/dev/null" };
  const git = (...a) => execFileSync("git", ["-C", proj, ...a], { env, stdio: "pipe" });
  git("init", "-q", "-b", spec.branch || "main");
  // A commit may start git's automatic maintenance, which detaches and later writes packs, a multi-pack-index and info/refs into
  // the fixture's .git while a case is running; the tree diff then reports files no case wrote.
  for (const [key, value] of QUIET_GIT_CONFIG) git("config", key, value);
  for (const c of spec.commits || []) {
    if (c.files && c.files.length) git("add", "--", ...c.files); else git("add", "-A");
    git("commit", "-q", "--allow-empty", "-m", c.message || "commit");
  }
}

// ---- environment (§3.2)
const NEVER = /TOKEN|KEY|SECRET|AUTH|PASSWORD/i;
const RECORDED_PREFIX = /^(AH_|AHD_|CLAUDE_|WRANGL_)/;
const RECORDED_NAMES = new Set(["HOME", "PATH", "TMPDIR", "TERM", "NO_COLOR", "COLUMNS", "HERDR_PANE_ID", "HERDR_WORKSPACE_ID", "HERDR_TAB_ID", "HERDR_ENV", "TMUX_PANE", "CODEX_HOME"]);

/** True when a variable name belongs to the recorded subset. */
export function recordedName(name) {
  if (name === "TMUX") return false;
  if (NEVER.test(name)) return false;
  return RECORDED_PREFIX.test(name) || RECORDED_NAMES.has(name);
}

/** The environment a case runs under: empty start, recorded subset with placeholders resolved, host PATH for node, git and sh. */
export function buildEnv(caseEnv, ctx, impl) {
  const env = {};
  for (const [k, v] of Object.entries(caseEnv || {})) if (recordedName(k)) env[k] = resolvePlaceholders(String(v), ctx);
  env.HOME = env.HOME || ctx.roots.home;
  env.AH_HOME = env.AH_HOME || ctx.roots.ah;
  env.TMPDIR = env.TMPDIR || join(ctx.caseDir, "tmp");
  mkdirSync(env.TMPDIR, { recursive: true });
  env.AH_TEST = env.AH_TEST || "1";
  if (!("AH_DAEMON" in env)) env.AH_DAEMON = "off";
  const hostDirs = new Set();
  for (const bin of ["node", "git", "sh"]) {
    try { hostDirs.add(dirname(execFileSync("sh", ["-c", `command -v ${bin}`], { encoding: "utf8" }).trim())); } catch { /* absent */ }
  }
  const own = (env.PATH ? env.PATH.split(":") : []).filter(Boolean);
  env.PATH = [...own, ...hostDirs].join(":");
  env.AH_IMPL = "native";
  // the shims find the binary through AH_BIN under AH_TEST=1
  if (process.env.AHD_NATIVE_BIN) {
    env.AH_BIN = process.env.AHD_NATIVE_BIN;
    // a native daemon is not started from inside the plugin, so it is told where the plugin is (a client's spawn does the same)
    if (!env.AH_PLUGIN_ROOT) env.AH_PLUGIN_ROOT = PLUGIN;
  }
  return env;
}

// ---- tree snapshot and diff (§3.3)
function ignored(rel, patterns) {
  for (const g of patterns) {
    const re = new RegExp("^" + g.split("*").map((s) => s.replace(/[.+?^${}()|[\]\\]/g, "\\$&")).join("[^/]*") + (g.endsWith("/") ? ".*" : "$"));
    if (re.test(rel)) return true;
    // a pattern with no slash matches a file name anywhere
    if (!g.includes("/") && re.test(rel.split("/").pop())) return true;
  }
  return false;
}

export function snapshot(roots, ignore) {
  const snap = new Map();
  for (const [name, dir] of Object.entries(roots)) {
    if (!existsSync(dir)) continue;
    for (const f of walkFiles(dir)) {
      const rel = `${name}/${relative(dir, f).split(sep).join("/")}`;
      if (ignored(rel, ignore)) continue;
      try {
        const st = lstatSync(f);
        if (st.isSymbolicLink()) { snap.set(rel, { mode: "link", bytes: Buffer.from(readlinkSync(f)) }); continue; }
        if (!st.isFile()) continue; // a socket or a fifo (a running daemon's) has no bytes to compare
        snap.set(rel, { mode: (st.mode & 0o7777).toString(8).padStart(4, "0"), bytes: readFileSync(f) });
      } catch (e) { if (e.code !== "ENOENT") throw e; }
    }
  }
  return snap;
}

function contentOf(bytes, norm, path) {
  if (bytes.length <= 65536 && looksText(bytes)) {
    const text = bytes.toString("utf8");
    if (Buffer.from(text, "utf8").equals(bytes)) return norm.text("file", text, { path });
  }
  return { sha256: createHash("sha256").update(bytes).digest("hex") };
}

/** {added, removed, changed} between two snapshots, normalized with `norm` (placeholder numbering follows path order). */
export function treeDiff(before, after, norm) {
  const added = [], removed = [], changed = [];
  const paths = [...new Set([...before.keys(), ...after.keys()])].sort();
  for (const p of paths) {
    const b = before.get(p), a = after.get(p);
    if (!b && a) added.push({ path: norm.text("path", p), mode: a.mode, content: contentOf(a.bytes, norm, p) });
    else if (b && !a) removed.push(norm.text("path", p));
    else if (!b.bytes.equals(a.bytes) || b.mode !== a.mode) changed.push({ path: norm.text("path", p), mode: a.mode, content: contentOf(a.bytes, norm, p) });
  }
  return { added, removed, changed };
}

// ---- running a case's process
/**
 * The shim command that serves a recorded script name. The recorded cases name the Node script they were taken from
 * (`hooks/<name>.mjs`); the same invocation runs through the plugin's entry points: a CLI through `bin/ah` or `bin/ahd`, a hook
 * through `bin/ah-hook`, and the push guard's three CLI modes through `ah push-guard`.
 */
export function shimCommand(script, argv) {
  const name = basename(script).replace(/\.mjs$/, "");
  const bin = (n) => join(PLUGIN, "bin", n);
  if (name === "ahd-ctl") return ["sh", [bin("ahd"), ...argv]];
  if (name === "pretooluse-push-guard" && ["check", "merge-check", "protection"].includes(argv[0])) return ["sh", [bin("ah"), "push-guard", ...argv]];
  if (["roster", "msg", "usage-report", "issue-intake"].includes(name)) return ["sh", [bin("ah"), name, ...argv]];
  return ["sh", [bin("ah-hook"), name, ...argv]];
}

/**
 * Re-runs this tool as a Claude harness process (argv[0] `claude`) unless it already is one. SessionStart refuses a parent that
 * is not a harness, and `<PID:runner>`/`<PIDSTART:runner>` name this process, so the runner is the harness the cases' records
 * name.
 */
export function runAsHarness() {
  if (process.env.AH_CONTRACT_HARNESS === "1") return;
  const r = spawnSync("/bin/bash", ["-c", 'exec -a claude "$0" "$@"', process.execPath, ...process.argv.slice(1)], {
    stdio: "inherit",
    env: { ...process.env, AH_CONTRACT_HARNESS: "1" },
  });
  process.exit(r.status === null ? 2 : r.status);
}

/** A plain `node` process that runs its arguments as one command: the parent of a case with `"parent": "node"`. */
const PLAIN_NODE_PARENT =
  'const r = require("node:child_process").spawnSync(process.argv[1], process.argv.slice(2), { stdio: "inherit" }); process.exit(r.status === null ? 1 : r.status);';

/**
 * A case whose tree plants message files (`…/msgs/*.md`) has them stored before the snapshot through the test-only
 * `ah db test-seed msgs` (ah-msgs-db-only §17): the store never reads a hand-planted file. A case that plants none seeds nothing.
 */
function seedPlantedMessages(ctx, env) {
  const pools = new Set();
  for (const root of Object.values(ctx.roots)) {
    if (!existsSync(root)) continue;
    for (const f of walkFiles(root)) {
      const planted = /^(.*?)[\\/]msgs[\\/](?:.+[\\/])?[^\\/]+\.md$/.exec(f);
      if (planted) pools.add(planted[1]);
    }
  }
  if (pools.size === 0) return;
  const args = [...pools].flatMap((pool) => ["--pool", pool]);
  const seeded = spawnSync("sh", [join(PLUGIN, "bin", "ah"), "db", "test-seed", "msgs", ...args], { env, cwd: ctx.roots.ah, encoding: "utf8", timeout: 60000 });
  if (seeded.status !== 0) throw new Error(`could not store the planted messages under ${ctx.roots.ah}: ${(seeded.stderr || seeded.stdout || "").trim()}`);
}

/**
 * A tree that opts in with `trees/<tree>.seed.json` holding `{"teams": true}` has each planted team file (`…/teams/<name>.json` in a
 * hierarchy dir) stored before the snapshot through the test-only `ah db test-seed teams` (db-only-stores): the team store, like the
 * message store, never reads a hand-planted file.
 */
const seedOptIn = (treeId, key) => {
  const optIn = join(CONTRACTS, "trees", `${treeId}.seed.json`);
  return existsSync(optIn) && JSON.parse(readFileSync(optIn, "utf8"))[key] === true;
};

function seedPlantedTeams(treeId, ctx, env, all) {
  if (!all && !seedOptIn(treeId, "teams")) return;
  for (const root of Object.values(ctx.roots)) {
    if (!existsSync(root)) continue;
    for (const f of walkFiles(root)) {
      if (!/[\\/]\.claude[\\/](?:hierarchy|ah2)[\\/]teams[\\/][^\\/]+\.json$/.test(f)) continue;
      const seeded = spawnSync("sh", [join(PLUGIN, "bin", "ah"), "db", "test-seed", "teams", "--path", f], { env: { ...env, AH_DAEMON: "off" }, cwd: ctx.roots.ah, input: readFileSync(f), encoding: "utf8", timeout: 60000 });
      if (seeded.status !== 0) throw new Error(`could not store the planted team ${f}: ${(seeded.stderr || seeded.stdout || "").trim()}`);
    }
  }
}

/**
 * A tree that opts in with `trees/<tree>.seed.json` holding `{"peers": true}` has each row of each planted peer log
 * (`…/agent-hierarchy.peer-pending.jsonl`) stored before the snapshot through the test-only `ah db test-seed peer-rows`: the peer log,
 * like the team and message stores, never reads a hand-planted file.
 */
function seedPlantedPeers(treeId, ctx, env, all) {
  if (!all && !seedOptIn(treeId, "peers")) return;
  for (const root of Object.values(ctx.roots)) {
    if (!existsSync(root)) continue;
    for (const f of walkFiles(root)) {
      if (!/[\\/]agent-hierarchy\.peer-pending\.jsonl$/.test(f)) continue;
      for (const row of readFileSync(f, "utf8").split("\n").filter((l) => l.trim())) {
        const seeded = spawnSync("sh", [join(PLUGIN, "bin", "ah"), "db", "test-seed", "peer-rows"], { env: { ...env, AH_DAEMON: "off" }, cwd: ctx.roots.ah, input: row, encoding: "utf8", timeout: 60000 });
        if (seeded.status !== 0) throw new Error(`could not store the planted peer row from ${f}: ${(seeded.stderr || seeded.stdout || "").trim()}`);
      }
    }
  }
}

/**
 * A tree that opts in with `trees/<tree>.seed.json` holding `{"mailbox": true}` has its planted registrations
 * (`ah/registrations/<session>.json`, its planted `at` kept), mail entries (`ah/mailbox/<box>/entries.jsonl`) and acks
 * (`ah/mailbox/<box>/acks/<id>`) stored before the snapshot through `ah db test-seed sessions|mail|acks`.
 */
function seedPlantedMailbox(treeId, ctx, env, all) {
  if ((!all && !seedOptIn(treeId, "mailbox")) || !ctx.roots.ah || !existsSync(ctx.roots.ah)) return;
  const run = (argv, input, what) => {
    const r = spawnSync("sh", argv, { env: { ...env, AH_DAEMON: "off" }, cwd: ctx.roots.ah, input, encoding: "utf8", timeout: 60000 });
    if (r.status !== 0) throw new Error(`could not store the planted ${what}: ${(r.stderr || r.stdout || "").trim()}`);
  };
  const ah = join(PLUGIN, "bin", "ah");
  const files = walkFiles(ctx.roots.ah);
  for (const f of files.filter((p) => /[\\/]registrations[\\/][^\\/]+\.json$/.test(p))) {
    const reg = JSON.parse(readFileSync(f, "utf8"));
    const { boxes, address, addresses, v, ...rest } = reg;
    run([ah, "db", "test-seed", "sessions"], JSON.stringify({ ...rest, boxes: Object.entries(boxes || {}).map(([a, dir]) => ({ address: a, dir })) }), `registration ${f}`);
  }
  for (const f of files.filter((p) => /[\\/]mailbox[\\/].+[\\/]entries\.jsonl$/.test(p))) {
    for (const row of readFileSync(f, "utf8").split("\n").filter((l) => l.trim())) run([ah, "db", "test-seed", "mail", "--dir", dirname(f)], row, `mail entry in ${f}`);
  }
  for (const f of files.filter((p) => /[\\/]mailbox[\\/].+[\\/]acks[\\/][^\\/]+$/.test(p))) {
    run([ah, "db", "test-seed", "acks", "--dir", dirname(dirname(f))], JSON.stringify({ ...JSON.parse(readFileSync(f, "utf8")), id: basename(f) }), `ack ${f}`);
  }
}

/**
 * Each row of each planted gate log (`…/gates.jsonl` in a hierarchy dir) is stored before the snapshot through the test-only
 * `ah db test-seed gates`, and the file removed: the gate log is a table (per-dir-files-to-db S3), so nothing reads the file. A line that
 * is not JSON was never a row a reader kept, so it is dropped.
 */
function seedPlantedGates(ctx, env) {
  for (const root of Object.values(ctx.roots)) {
    if (!existsSync(root)) continue;
    for (const f of walkFiles(root)) {
      if (!/[\\/]\.claude[\\/](?:hierarchy|ah2)[\\/]gates\.jsonl$/.test(f)) continue;
      for (const row of readFileSync(f, "utf8").split("\n").filter((l) => l.trim())) {
        try {
          JSON.parse(row);
        } catch {
          continue;
        }
        const seeded = spawnSync("sh", [join(PLUGIN, "bin", "ah"), "db", "test-seed", "gates", "--dir", dirname(f)], { env: { ...env, AH_DAEMON: "off" }, cwd: ctx.roots.ah, input: row, encoding: "utf8", timeout: 60000 });
        if (seeded.status !== 0) throw new Error(`could not store the planted gate row from ${f}: ${(seeded.stderr || seeded.stdout || "").trim()}`);
      }
      rmSync(f);
    }
  }
}

/**
 * A planted ultra-gate or session-roles document (`…/.claude/ah2/agent-hierarchy.{gate,session-roles}.json`) and a planted usage log
 * (`…/.claude/ah2/agent-hierarchy.usage.jsonl`) are stored as rows before the snapshot through the test-only
 * `ah db test-seed home-docs|usage-rows`, verbatim, and the file removed: these are `meta` and `usage_rows` rows (b397 group 2), so
 * nothing reads the files (a tree's recorded v1 names are already moved under `ah2/` when it is materialized).
 */
function seedPlantedHomeRows(ctx, env) {
  const kinds = [
    [/[\\/]\.claude[\\/]ah2[\\/]agent-hierarchy\.gate\.json$/, ["home-docs", "--key", "home.ultra-gate"]],
    [/[\\/]\.claude[\\/]ah2[\\/]agent-hierarchy\.session-roles\.json$/, ["home-docs", "--key", "home.session-roles"]],
    [/[\\/]\.claude[\\/]ah2[\\/]agent-hierarchy\.usage\.jsonl$/, ["usage-rows"]],
  ];
  let any = false;
  for (const root of Object.values(ctx.roots)) {
    if (!existsSync(root)) continue;
    for (const f of walkFiles(root)) {
      const kind = kinds.find(([re]) => re.test(f));
      if (!kind) continue;
      const seeded = spawnSync("sh", [join(PLUGIN, "bin", "ah"), "db", "test-seed", ...kind[1]], { env: { ...env, AH_DAEMON: "off" }, cwd: ctx.roots.ah, input: readFileSync(f), encoding: "utf8", timeout: 60000 });
      if (seeded.status !== 0) throw new Error(`could not store the planted ${f}: ${(seeded.stderr || seeded.stdout || "").trim()}`);
      rmSync(f);
      any = true;
    }
  }
  return any;
}

/** Where a tree snapshot shows the store's gate log: one row body per line, by dir then append order. */
export const GATES_LOG_VIEW = "ah/state/ah.db.gates.jsonl";

/** Adds the store's gates rows to `snap` at GATES_LOG_VIEW; nothing when there is no database or no table. */
export function withGatesLog(snap, ctx) {
  return withDbView(snap, ctx, "SELECT body AS raw FROM gates ORDER BY hier_dir, seq", GATES_LOG_VIEW);
}

/**
 * Each row of each planted peer roster (`…/peers.jsonl` in a hierarchy dir) is stored before the snapshot through the test-only
 * `ah db test-seed peers`, and the file removed: the roster is `roster_records` rows (per-dir-files-to-db S7). A line that is not JSON
 * was never a row a reader kept, so it is dropped.
 */
function seedPlantedRoster(ctx, env) {
  for (const root of Object.values(ctx.roots)) {
    if (!existsSync(root)) continue;
    for (const f of walkFiles(root)) {
      if (!/[\\/]\.claude[\\/](?:hierarchy|ah2)[\\/]peers\.jsonl$/.test(f)) continue;
      const rows = readFileSync(f, "utf8").split("\n").filter((l) => l.trim());
      // removed first: a seed's status refresh reads the roster, and a file still there would be taken as well
      rmSync(f);
      for (const row of rows) {
        try {
          JSON.parse(row);
        } catch {
          continue;
        }
        const seeded = spawnSync("sh", [join(PLUGIN, "bin", "ah"), "db", "test-seed", "peers", "--dir", dirname(f)], { env: { ...env, AH_DAEMON: "off" }, cwd: ctx.roots.ah, input: row, encoding: "utf8", timeout: 60000 });
        if (seeded.status !== 0) throw new Error(`could not store the planted roster row from ${f}: ${(seeded.stderr || seeded.stdout || "").trim()}`);
      }
    }
  }
}

/** Where a tree snapshot shows the store's peer roster: one row per line, by dir then append order. */
export const ROSTER_LOG_VIEW = "ah/state/ah.db.roster_records.jsonl";

/** Adds the store's roster_records rows to `snap` at ROSTER_LOG_VIEW; nothing when there is no database or no row. */
export function withRosterLog(snap, ctx) {
  return withDbView(snap, ctx, "SELECT CAST(raw AS TEXT) AS raw FROM roster_records ORDER BY pool_dir, seq", ROSTER_LOG_VIEW);
}

/** Where a tree snapshot shows the store's links records (per-dir-files-to-db S11b): one document per team home and team, in key order. */
export const LINKS_VIEW = "ah/state/ah.db.links.jsonl";

/** Adds the store's links rows to `snap` at LINKS_VIEW; nothing when there is no database or no table. */
export function withLinksView(snap, ctx) {
  return withDbView(snap, ctx, "SELECT rtrim(body, char(10)) AS raw FROM links ORDER BY team_home, team", LINKS_VIEW);
}

/** Where a tree snapshot shows the store's peer log: its rows live in ah.db, which the snapshot ignores, so a write to them stays visible. */
export const PEER_LOG_VIEW = "ah/state/ah.db.peer_records.jsonl";

/** Where a CLI snapshot shows the store's mail entries: like the peer log, they live only in ah.db. */
export const MAIL_LOG_VIEW = "ah/state/ah.db.mail_entries.jsonl";

/** Adds the store's peer_records rows, one raw row per line in seq order, to `snap` at PEER_LOG_VIEW; nothing when there is no database. */
export function withPeerLog(snap, ctx) {
  return withDbView(snap, ctx, "SELECT raw FROM peer_records ORDER BY seq", PEER_LOG_VIEW);
}

/** Adds the store's mail_entries rows, one raw entry per line in insert order, to `snap` at MAIL_LOG_VIEW. */
export function withMailLog(snap, ctx) {
  return withDbView(snap, ctx, "SELECT raw FROM mail_entries ORDER BY pk", MAIL_LOG_VIEW);
}

/**
 * Stores that moved from files into ah.db (b524): each is shown in a snapshot at its view path, one row body per line, so a write
 * to them stays visible to a tree check. [view path, SQL selecting `raw`].
 */
export const DB_VIEWS = [
  ["ah/state/ah.db.usage_rows.jsonl", "SELECT body AS raw FROM usage_rows ORDER BY seq"],
  ["ah/state/ah.db.decisions.jsonl", "SELECT body AS raw FROM decisions ORDER BY hier_dir, run, seq"],
  ["ah/state/ah.db.progress_logs.jsonl", "SELECT body AS raw FROM progress_logs ORDER BY hier_dir, team, log, seq"],
  // a sweep marker's op_id ends in the sweep's epoch milliseconds, so the view leaves that op_id out
  ["ah/state/ah.db.asks.jsonl", "SELECT CASE WHEN json_extract(body, '$.kind') = 'swept' THEN json_remove(body, '$.op_id') ELSE body END AS raw FROM asks ORDER BY hier_dir, log, seq"],
  // a qa_log row's `id` is random per run, so the view leaves it out
  ["ah/state/ah.db.qa_log.jsonl", "SELECT json_remove(body, '$.id') AS raw FROM qa_log ORDER BY hier_dir, seq"],
  // one snapshot document per row, with its trailing newline trimmed so each takes one line's end
  ["ah/state/ah.db.team_snapshots.jsonl", "SELECT rtrim(body, char(10)) AS raw FROM team_snapshots ORDER BY home, team, ts"],
  // the home documents kept as meta keys: session roles, and one known-dirs entry per hierarchy dir; `<key> <value>` per line
  ["ah/state/ah.db.meta-home.txt", "SELECT key || ' ' || value AS raw FROM meta WHERE key = 'home.session-roles' OR key LIKE 'known-dir:%' ORDER BY key"],
];

/** Adds every DB_VIEWS view to `snap`; a table that does not exist adds nothing. */
export function withDbViews(snap, ctx) {
  return DB_VIEWS.reduce((s, [view, sql]) => withDbView(s, ctx, sql, view), snap);
}

function withDbView(snap, ctx, sql, view) {
  const db = ctx.roots.ah ? join(ctx.roots.ah, "state", "ah.db") : null;
  if (!db || !existsSync(db)) return snap;
  const conn = new DatabaseSync(db, { readOnly: true });
  try {
    const rows = conn.prepare(sql).all();
    if (rows.length) snap.set(view, { mode: "0644", bytes: Buffer.concat(rows.flatMap((r) => [Buffer.from(r.raw), Buffer.from("\n")])) });
  } catch (e) {
    if (!/no such table/.test(String(e?.message))) throw e;
  } finally {
    conn.close();
  }
  return snap;
}

/**
 * Stores what a materialized tree planted (messages, and the teams, peers and mailbox its seed.json opts into) before its snapshot.
 * `all` stores the teams, peers and mailbox with no opt-in: a wire case's daemon reads callers and teams from the store only.
 */
export function seedPlantedTree(treeId, ctx, env, all = false) {
  seedPlantedMessages(ctx, env);
  // a seeded home row creates the database, after which teams are read from rows only: the tree's teams go in with it
  const homeRows = seedPlantedHomeRows(ctx, env);
  seedPlantedGates(ctx, env);
  seedPlantedRoster(ctx, env);
  seedPlantedTeams(treeId, ctx, env, all || homeRows);
  seedPlantedPeers(treeId, ctx, env, all);
  seedPlantedMailbox(treeId, ctx, env, all);
}

/** Runs one CLI or hook invocation under a case. Returns raw stdout, stderr, exit and the tree diff. */
export function runProcess(spec, { caseDef, T, impl, timeoutMs = 60000 }) {
  const caseDir = join(T, caseDef.id.replace(/[^A-Za-z0-9._-]/g, "_") + "-" + Math.random().toString(36).slice(2, 8));
  mkdirSync(caseDir, { recursive: true });
  const ctx = { T, plugin: PLUGIN, caseDir, roots: {} };
  materializeTree(caseDef.tree, caseDir, ctx);
  const env = buildEnv(caseDef.env, ctx, impl);
  const cwd = resolvePlaceholders(caseDef.cwd || "<ROOT:proj>", ctx);
  mkdirSync(cwd, { recursive: true });
  seedPlantedTree(caseDef.tree, ctx, env);
  const ignore = rules().ignore_paths;
  const before = withDbViews(withLinksView(withRosterLog(withGatesLog(withMailLog(withPeerLog(snapshot(ctx.roots, ignore), ctx), ctx), ctx), ctx), ctx), ctx);
  const argv = (spec.argv || []).map((a) => resolvePlaceholders(String(a), ctx));
  const stdin = spec.stdin === undefined || spec.stdin === null ? "" : typeof spec.stdin === "string" ? resolvePlaceholders(spec.stdin, ctx) : JSON.stringify(spec.stdin);
  const [shimProgram, shimArgv] = shimCommand(spec.script, argv);
  const [program, programArgv] = caseDef.parent === "node" ? [process.execPath, ["-e", PLAIN_NODE_PARENT, shimProgram, ...shimArgv]] : [shimProgram, shimArgv];
  const res = spawnSync(program, programArgv, { cwd, env, input: stdin, encoding: "utf8", timeout: timeoutMs, maxBuffer: 64 * 1024 * 1024 });
  const after = withDbViews(withLinksView(withRosterLog(withGatesLog(withMailLog(withPeerLog(snapshot(ctx.roots, ignore), ctx), ctx), ctx), ctx), ctx), ctx);
  return { impl, ctx, env, cwd, stdout: res.stdout || "", stderr: res.stderr || "", exit: res.status === null ? (res.signal ? `signal:${res.signal}` : "timeout") : res.status, before, after, ignore };
}

/** Normalizes one run into the shape stored in a case's `expect`. A fresh normalizer per case keeps placeholder numbering per case. */
export function normalizeRun(run, { withTree = true } = {}) {
  const norm = createNormalizer(rules(), { T: run.ctx.T, roots: run.ctx.roots, plugin: run.ctx.plugin, impl: run.impl === "native" ? "native" : "node", ...extraCtx() });
  const out = {
    stdout: norm.text("stdout", run.stdout),
    stderr: norm.text("stderr", run.stderr),
    exit: run.exit,
  };
  if (withTree) out.tree_diff = treeDiff(run.before, run.after, norm);
  return out;
}

// ---- comparison
/** Unified-ish diff of two JSON-able values for a failure report: the first differing line of each pretty-printed form, plus both. */
export function describeDiff(expected, actual) {
  const e = JSON.stringify(expected, null, 2).split("\n");
  const a = JSON.stringify(actual, null, 2).split("\n");
  const out = ["--- expected", "+++ actual"];
  const n = Math.max(e.length, a.length);
  for (let i = 0; i < n; i++) {
    if (e[i] === a[i]) out.push(" " + (e[i] ?? ""));
    else { if (e[i] !== undefined) out.push("-" + e[i]); if (a[i] !== undefined) out.push("+" + a[i]); }
  }
  return out.join("\n") + "\n";
}

export const same = (x, y) => JSON.stringify(x) === JSON.stringify(y);

/** Compares a normalized run with a case's `expect`. `fields` lists which parts are compared. */
export function compareExpect(expect, actual, fields = ["stdout", "stderr", "exit", "tree_diff"]) {
  const diffs = [];
  for (const f of fields) {
    if (f === "tree_diff" && expect.tree_diff === undefined) continue;
    if (!same(expect[f], actual[f])) diffs.push(f);
  }
  return diffs;
}

export { existsSync, readFileSync, writeFileSync, mkdirSync, readdirSync, statSync, join, relative, dirname };
