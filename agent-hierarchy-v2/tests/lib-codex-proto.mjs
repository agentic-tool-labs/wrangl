/**
 * agent-hierarchy — a client for a Codex app-server (JSON-RPC over WebSocket over a Unix socket), and
 * the per-member hand-over logic that sits on it. Nothing here touches a real Codex: the socket path
 * is a parameter and the member logic takes the client, the clock and the ack and activity sinks as
 * arguments, so the fake Codex server under tests/ drives all of it.
 */

import { createHash, randomBytes } from "node:crypto";
import { lstatSync, mkdirSync, realpathSync } from "node:fs";
import { connect } from "node:net";
import { isAbsolute, join, relative, resolve, sep } from "node:path";

const GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
const OP_TEXT = 1;
const OP_CLOSE = 8;
const OP_PING = 9;
const OP_PONG = 10;

/** How long the upgrade and each request may take before the client gives up and closes. */
export const REQUEST_TIMEOUT_MS = 10_000;
/** The largest WebSocket message (a frame, or the fragments of one message) the client buffers. */
export const MAX_FRAME_BYTES = 8 * 1024 * 1024;
/** The largest HTTP response head the client buffers while waiting for the upgrade to finish. */
export const MAX_HEADER_BYTES = 16 * 1024;

/** The accept key a server must answer an upgrade with. */
export const acceptKey = (key) => createHash("sha1").update(key + GUID).digest("base64");

/** One WebSocket frame carrying `payload`; a client masks (RFC 6455 5.3), a server does not. */
export function encodeFrame(payload, { opcode = OP_TEXT, mask = false } = {}) {
  const body = Buffer.isBuffer(payload) ? payload : Buffer.from(String(payload), "utf8");
  const n = body.length;
  const head = n < 126 ? Buffer.from([0x80 | opcode, (mask ? 0x80 : 0) | n]) : n < 65536 ? Buffer.from([0x80 | opcode, (mask ? 0x80 : 0) | 126, n >> 8, n & 0xff]) : Buffer.concat([Buffer.from([0x80 | opcode, (mask ? 0x80 : 0) | 127]), (() => { const b = Buffer.alloc(8); b.writeBigUInt64BE(BigInt(n)); return b; })()]);
  if (!mask) return Buffer.concat([head, body]);
  const key = randomBytes(4);
  const masked = Buffer.from(body);
  for (let i = 0; i < n; i++) masked[i] ^= key[i % 4];
  return Buffer.concat([head, key, masked]);
}

/**
 * Incremental frame reader: `push(chunk)` returns the complete messages `{opcode, data}` it now holds (fragments joined).
 * It throws when a frame, or the fragments of one message together, would exceed `maxBytes`.
 */
export function frameReader({ maxBytes = MAX_FRAME_BYTES } = {}) {
  let buf = Buffer.alloc(0);
  let frag = null;
  let fragBytes = 0;
  return {
    push(chunk) {
      buf = Buffer.concat([buf, chunk]);
      const out = [];
      for (;;) {
        if (buf.length < 2) break;
        const fin = (buf[0] & 0x80) !== 0;
        const opcode = buf[0] & 0x0f;
        const masked = (buf[1] & 0x80) !== 0;
        let len = buf[1] & 0x7f;
        let off = 2;
        if (len === 126) {
          if (buf.length < 4) break;
          len = buf.readUInt16BE(2);
          off = 4;
        } else if (len === 127) {
          if (buf.length < 10) break;
          len = Number(buf.readBigUInt64BE(2));
          off = 10;
        }
        if (len > maxBytes || fragBytes + len > maxBytes) throw new Error(`websocket message larger than ${maxBytes} bytes`);
        const need = off + (masked ? 4 : 0) + len;
        if (buf.length < need) break;
        let data = Buffer.from(buf.subarray(off + (masked ? 4 : 0), need));
        if (masked) {
          const key = buf.subarray(off, off + 4);
          for (let i = 0; i < data.length; i++) data[i] ^= key[i % 4];
        }
        buf = buf.subarray(need);
        if (opcode === 0 || (opcode === OP_TEXT && !fin)) {
          frag = frag ? { opcode: frag.opcode, parts: [...frag.parts, data] } : { opcode, parts: [data] };
          fragBytes += data.length;
          if (!fin) continue;
          data = Buffer.concat(frag.parts);
          out.push({ opcode: frag.opcode, data });
          frag = null;
          fragBytes = 0;
        } else {
          out.push({ opcode, data });
        }
      }
      return out;
    },
  };
}

/**
 * A JSON-RPC client over one WebSocket on the Unix socket `path`. `open()` upgrades and sends
 * `initialize` then `initialized`. `request(method, params)` resolves with the result or rejects with
 * an Error carrying `.code` and `.data`. `onNotification(fn)` receives `{method, params}`. `onClose(fn)`
 * fires once when the connection ends. The client never answers a server-initiated request: those go
 * to `onServerRequest(fn)` and are otherwise ignored. The upgrade and every request have a deadline (`timeoutMs`): a request
 * that is not answered in time rejects and closes the connection. A message over `maxFrameBytes` or a handshake head over
 * `maxHeaderBytes` closes it too.
 */
export function appServerClient(path, { clientInfo = { name: "ahd", version: "0" }, timeoutMs = REQUEST_TIMEOUT_MS, maxFrameBytes = MAX_FRAME_BYTES, maxHeaderBytes = MAX_HEADER_BYTES } = {}) {
  let sock = null;
  let nextId = 1;
  const pending = new Map();
  const notes = [];
  const serverReqs = [];
  const closers = [];
  let closed = false;
  let isOpen = false;

  const fail = (err) => {
    for (const p of pending.values()) p.reject(err);
    pending.clear();
    if (closed) return;
    closed = true;
    isOpen = false;
    for (const f of closers) f();
  };
  const send = (obj) => sock.write(encodeFrame(JSON.stringify(obj), { mask: true }));
  const onMessage = (text) => {
    let m;
    try { m = JSON.parse(text); } catch { return; }
    if (m && m.id !== undefined && (m.result !== undefined || m.error !== undefined) && pending.has(m.id)) {
      const p = pending.get(m.id);
      pending.delete(m.id);
      if (m.error) p.reject(Object.assign(new Error(m.error.message || "rpc error"), { code: m.error.code, data: m.error.data }));
      else p.resolve(m.result);
    } else if (m && m.method && m.id !== undefined) {
      for (const f of serverReqs) f(m);
    } else if (m && m.method) {
      for (const f of notes) f({ method: m.method, params: m.params });
    }
  };

  const client = {
    get isOpen() { return isOpen; },
    onNotification: (f) => notes.push(f),
    onServerRequest: (f) => serverReqs.push(f),
    onClose: (f) => closers.push(f),
    request(method, params) {
      if (!isOpen && method !== "initialize") return Promise.reject(new Error("not connected"));
      const id = nextId++;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          if (!pending.delete(id)) return;
          reject(new Error(`${method} timed out after ${timeoutMs} ms`));
          client.close();
        }, timeoutMs);
        if (timer.unref) timer.unref();
        pending.set(id, { resolve: (v) => { clearTimeout(timer); resolve(v); }, reject: (e) => { clearTimeout(timer); reject(e); } });
        try { send({ jsonrpc: "2.0", id, method, params }); } catch (e) { pending.delete(id); clearTimeout(timer); reject(e); }
      });
    },
    notify(method, params) { send({ jsonrpc: "2.0", method, params }); },
    close() {
      if (sock) sock.destroy();
      fail(new Error("closed"));
    },
    open() {
      return new Promise((resolve, reject) => {
        const key = randomBytes(16).toString("base64");
        let upgraded = false;
        let head = Buffer.alloc(0);
        const reader = frameReader({ maxBytes: maxFrameBytes });
        sock = connect(path);
        const upgradeTimer = setTimeout(() => {
          if (upgraded) return;
          reject(new Error(`websocket upgrade timed out after ${timeoutMs} ms`));
          sock.destroy();
        }, timeoutMs);
        if (upgradeTimer.unref) upgradeTimer.unref();
        sock.on("error", (e) => { if (!upgraded) reject(e); fail(e); });
        sock.on("close", () => fail(new Error("connection closed")));
        sock.on("connect", () => sock.write(`GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`));
        sock.on("data", (chunk) => {
          if (!upgraded) {
            head = Buffer.concat([head, chunk]);
            const end = head.indexOf("\r\n\r\n");
            if (end < 0) {
              if (head.length > maxHeaderBytes) {
                reject(new Error(`websocket handshake head larger than ${maxHeaderBytes} bytes`));
                sock.destroy();
              }
              return;
            }
            const text = head.subarray(0, end).toString("utf8");
            if (!/^HTTP\/1\.1 101/.test(text) || !text.toLowerCase().includes(`sec-websocket-accept: ${acceptKey(key).toLowerCase()}`)) {
              reject(new Error("websocket upgrade refused"));
              sock.destroy();
              return;
            }
            upgraded = true;
            clearTimeout(upgradeTimer);
            chunk = head.subarray(end + 4);
            head = Buffer.alloc(0);
            client.request("initialize", { clientInfo }).then(() => {
              client.notify("initialized");
              isOpen = true;
              resolve(client);
            }, reject);
            isOpen = true;
            if (!chunk.length) return;
          }
          let frames;
          try {
            frames = reader.push(chunk);
          } catch (e) {
            sock.destroy();
            fail(e);
            return;
          }
          for (const f of frames) {
            if (f.opcode === OP_TEXT) onMessage(f.data.toString("utf8"));
            else if (f.opcode === OP_PING) sock.write(encodeFrame(f.data, { opcode: OP_PONG, mask: true }));
            else if (f.opcode === OP_CLOSE) sock.destroy();
          }
        });
      });
    },
  };
  return client;
}

/** The one derivation of a member's `<team>-<name>` key: its socket and its temp directory share it. */
const memberKey = (team, name) => `${team}-${name}`;

/** The socket a member's own app-server listens on: directory `<ahHome>/run/codex/` (mode 0700), one socket per team member. */
export const appserverSocket = (ahHome, team, name) => join(ahHome, "run", "codex", `${memberKey(team, name)}.sock`);

/**
 * The directory a member's commands use for temporary files: `<ahHome>/run/codex/tmp/<team>-<name>`. It is the only temp space
 * the sandbox makes writable (the system temp locations are excluded), so a member never writes where other sessions do.
 */
export const memberTmpDir = (ahHome, team, name) => join(ahHome, "run", "codex", "tmp", memberKey(team, name));

/**
 * Creates a member's temp directory (recursive, mode 0700) and returns its path, or null when it cannot be made or is not safe.
 * The sandbox's writable root follows whatever stands at the path, so: the path must be in its normal form and lie under
 * `ahHome` (one path is computed, checked, created and returned; a `..` after a symlink would otherwise walk the link's target);
 * no component below `ahHome` may be a symlink (nothing is created through one); and what stands at the end must be a real
 * directory owned by this user. `ahHome` itself may be reached through a symlink. Never removes anything.
 */
export function ensureMemberTmp(dir, ahHome) {
  try {
    const rel = relative(ahHome, dir);
    if (!rel || rel === ".." || rel.startsWith(`..${sep}`) || isAbsolute(rel)) return null;
    const norm = join(ahHome, rel);
    if (dir !== norm) return null;
    dir = norm;
    let p = ahHome;
    for (const part of rel.split(sep)) {
      p = join(p, part);
      let st;
      try {
        st = lstatSync(p);
      } catch {
        break;
      }
      if (st.isSymbolicLink()) return null;
    }
    mkdirSync(dir, { recursive: true, mode: 0o700 });
    const st = lstatSync(dir);
    const mine = typeof process.getuid !== "function" || st.uid === process.getuid();
    return st.isDirectory() && !st.isSymbolicLink() && mine ? dir : null;
  } catch {
    return null;
  }
}

/** The `codex` arguments that attach a terminal UI to a member's app-server socket. */
export const remoteArgs = (sock) => ["--remote", `unix://${sock}`];

/** The terminal UI's whole argument list: it resumes the thread the spawn created and attaches to the member's server. Nothing else
 * goes on it: a remote resume refuses permission flags, so the mode and every setting live on the server's argv. */
export const tuiArgs = (threadId, sock) => ["resume", threadId, ...remoteArgs(sock)];

/** The sandbox modes a member may run in under the adapter: its commands can then neither write ah's state nor reach another member's server. */
const SANDBOX_MODES = ["workspace-write", "read-only"];
/**
 * The approval values the tested codex accepts for `--ask-for-approval` on a launch. `untrusted` and `on-failure` are refused by
 * that version, so a member that sets either launches plain. When the tested range widens, re-check this list against that
 * version's `codex --help`.
 */
const APPROVAL_MODES = ["on-request", "never"];
/** The `-c` keys a member's launch arguments may set; none of them touches the sandbox. */
const CONFIG_KEYS = ["model_instructions_file", "approvals_reviewer", "model_reasoning_effort"];

/** The mode of a launch that sets none: the same as autoMode acceptEdits. */
export const DEFAULT_SANDBOX = "workspace-write";
export const DEFAULT_APPROVAL = "on-request";

/** The sandbox and approval values a checked launch (`checkLaunch`'s result) runs under: what it set, else the defaults. */
export const launchModes = (checked) => ({ sandbox: (checked && checked.sandbox) || DEFAULT_SANDBOX, approval: (checked && checked.approval) || DEFAULT_APPROVAL });

/**
 * The arguments that start a member's own app-server. All policy and every thread setting live here, because the server creates
 * the thread: the sandbox mode, no network from the sandbox, the writable roots (the member's temp directory `tmp`, then the
 * checked `--add-dir` paths), the approval policy, the exclusion of the system temp locations with `TMPDIR` pointed at `tmp`,
 * then the checked list's model and `-c` settings that used to sit on the terminal's argv. They go before the subcommand, and
 * they are set on every thread the server creates whatever the user's Codex config says: pinning needs no knowledge of that
 * config, which ah never reads.
 */
export function serverArgs(sock, checked, tmp) {
  if (typeof tmp !== "string" || !isAbsolute(tmp)) throw new Error("serverArgs needs the member's temp directory as an absolute path");
  const { sandbox, approval } = launchModes(checked);
  const c = checked || {};
  const roots = [tmp, ...(Array.isArray(c.roots) ? c.roots : [])].map((r) => JSON.stringify(r));
  const settings = [];
  for (const s of Array.isArray(c.settings) ? c.settings : []) settings.push("-c", s.key === "model" ? `model=${JSON.stringify(s.value)}` : `${s.key}=${s.value}`);
  return [
    "-c", `sandbox_mode=${JSON.stringify(sandbox)}`,
    "-c", "sandbox_workspace_write.network_access=false",
    "-c", `sandbox_workspace_write.writable_roots=[${roots.join(",")}]`,
    "-c", `approval_policy=${JSON.stringify(approval)}`,
    "-c", "sandbox_workspace_write.exclude_slash_tmp=true",
    "-c", "sandbox_workspace_write.exclude_tmpdir_env_var=true",
    "-c", `shell_environment_policy.set.TMPDIR=${JSON.stringify(tmp)}`,
    ...settings,
    "app-server", "--listen", `unix://${sock}`,
  ];
}

/** Whether `p` and `q` are the same path or one is below the other. */
function related(p, q) {
  const within = (a, b) => {
    const r = relative(a, b);
    return r === "" || (!r.startsWith("..") && !isAbsolute(r));
  };
  return within(p, q) || within(q, p);
}

const realOrSelf = (p) => {
  try {
    return realpathSync(p);
  } catch {
    return p;
  }
};

/** Whether `dir` is an absolute path unrelated to `ahHome`: not it, not under it, not an ancestor of it, compared lexically and by real path. */
function addDirOk(dir, ahHome) {
  if (!isAbsolute(dir)) return false;
  const forms = (p) => [resolve(p), realOrSelf(resolve(p))];
  const home = forms(ahHome);
  return !forms(dir).some((p) => home.some((q) => related(p, q)));
}

/**
 * Check the launch arguments of a member that asked for the app-server adapter against the allow-list: each token must be one
 * of the forms below, anything else is a violation. Returns `{violations, sandbox, approval, roots, settings}`: the violations
 * as note items that never carry a value; the `--sandbox` and `--ask-for-approval` values the arguments set (null when none);
 * the `--add-dir` paths; and the model and `-c` settings as `{key, value}`, in order, for the server's argv.
 *
 *   --model/-m <v> (once)   --sandbox/-s workspace-write|read-only (once)   --ask-for-approval/-a <mode> (once)
 *   --add-dir <absolute path unrelated to ahHome>   -c/--config <key>=<v> for a key in CONFIG_KEYS
 *
 * Long flags take `--flag value` or `--flag=value`, short flags the separate form only. A value that is missing or starts with
 * `-` is a bad value. An unknown flag takes the next token that does not start with `-` as its own value, so a value is never
 * reported as a positional argument. Reads no file and no environment.
 */
export function checkLaunch(args, ahHome) {
  const violations = [];
  const seen = new Set();
  let sandbox = null;
  let approval = null;
  const roots = [];
  const settings = [];
  const list = Array.isArray(args) ? args : [];
  for (let i = 0; i < list.length; i++) {
    const tok = list[i];
    if (typeof tok !== "string" || !tok.startsWith("-") || tok === "-") {
      violations.push(`positional argument ${i + 1}`);
      continue;
    }
    const long = tok.startsWith("--");
    const eq = tok.indexOf("=");
    // A short flag with its value attached (-mVALUE, -cKEY=VALUE) is reported as the flag alone: a user-written value never reaches a note.
    const flag = !long && tok.length > 2 ? `${tok.slice(0, 2)} (attached value)` : eq < 0 ? tok : tok.slice(0, eq);
    let value;
    let separate = false;
    if (eq >= 0 && long) value = tok.slice(eq + 1);
    else if (eq < 0) {
      const next = list[i + 1];
      value = typeof next === "string" && !next.startsWith("-") ? next : undefined;
      separate = value !== undefined;
    }
    const known = { "--model": "model", "-m": "model", "--sandbox": "sandbox", "-s": "sandbox", "--ask-for-approval": "approval", "-a": "approval", "--add-dir": "dir", "--config": "config", "-c": "config" }[flag];
    const knownForm = known && (long ? true : eq < 0);
    if (!knownForm) {
      violations.push(flag);
      if (separate) i++;
      continue;
    }
    if (separate) i++;
    if (known === "config") {
      const at = typeof value === "string" ? value.indexOf("=") : -1;
      const key = at < 0 ? null : value.slice(0, at).trim();
      if (key === null) violations.push("-c");
      else if (!/^[A-Za-z0-9_]+$/.test(key) || !CONFIG_KEYS.includes(key)) violations.push(`-c ${key}`);
      else settings.push({ key, value: value.slice(at + 1) });
      continue;
    }
    const good =
      value !== undefined &&
      value !== "" &&
      !value.startsWith("-") &&
      (known === "model" || (known === "sandbox" && SANDBOX_MODES.includes(value)) || (known === "approval" && APPROVAL_MODES.includes(value)) || (known === "dir" && addDirOk(value, ahHome)));
    if (!good) violations.push(`${flag} (bad value)`);
    else if (known !== "dir" && seen.has(known)) violations.push(`${flag} (repeated)`);
    else {
      if (known !== "dir") seen.add(known);
      if (known === "sandbox") sandbox = value;
      if (known === "approval") approval = value;
      if (known === "dir") roots.push(value);
      if (known === "model") settings.push({ key: "model", value });
    }
  }
  return { violations, sandbox, approval, roots, settings };
}

/** The violations of `checkLaunch`: empty means the launch arguments are admitted. */
export const launchContractViolations = (args, ahHome) => checkLaunch(args, ahHome).violations;

/**
 * Codex versions the app-server adapter has been proven on, as `{min, max}` (min inclusive, max exclusive): exactly the one
 * version a live probe of the production pin set passed on, up to the next prerelease. Any other version launches plain.
 * Widening it needs that probe run on the new version.
 */
export const TESTED_RANGES = [{ min: "0.162.0-alpha.2", max: "0.162.0-alpha.3" }];

/**
 * The ranges in force: TESTED_RANGES, except that a test sandbox (`AH_TEST=1`) may set `AH_CODEX_APPSERVER_RANGE` to
 * `min,max` so the in-range path can be exercised against a stub codex, or to `none` for no range at all. Outside a test
 * sandbox the override is ignored.
 */
export function testedRanges(env = process.env) {
  if (env.AH_TEST !== "1") return TESTED_RANGES;
  if (env.AH_CODEX_APPSERVER_RANGE === "none") return [];
  const m = /^(\d+\.\d+\.\d+),(\d+\.\d+\.\d+)$/.exec(env.AH_CODEX_APPSERVER_RANGE || "");
  return m ? [{ min: m[1], max: m[2] }] : TESTED_RANGES;
}

/** `X.Y.Z[-pre]` out of `codex --version` text, as `{nums: [x, y, z], pre}`, or null. */
export function parseCodexVersion(text) {
  const m = /(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?/.exec(String(text || ""));
  return m ? { nums: [Number(m[1]), Number(m[2]), Number(m[3])], pre: m[4] || null, text: m[0] } : null;
}

// A prerelease sorts before its release (semver); two prereleases compare as text.
function compareVersions(a, b) {
  for (let i = 0; i < 3; i++) if (a.nums[i] !== b.nums[i]) return a.nums[i] < b.nums[i] ? -1 : 1;
  if (a.pre === b.pre) return 0;
  if (a.pre === null) return 1;
  if (b.pre === null) return -1;
  return a.pre < b.pre ? -1 : 1;
}

/**
 * Whether `codex --version` output (`versionText`) is inside `ranges`. `note` is the sentence the spawn prints and the
 * member row keeps when it is not; `ok` is false for an unparseable version too. With no range at all the sentence ends at
 * "adapter": there is no tested codex to be outside of, so there is no opt-out to point at.
 */
export function adapterGate(versionText, ranges = TESTED_RANGES) {
  const v = parseCodexVersion(versionText);
  const shown = ranges.length ? ranges.map((r) => `>=${r.min} <${r.max}`).join(" || ") : "(none yet)";
  if (v && ranges.some((r) => compareVersions(v, parseCodexVersion(r.min)) >= 0 && compareVersions(v, parseCodexVersion(r.max)) < 0)) return { ok: true, version: v.text, note: null };
  if (!ranges.length) return { ok: false, version: v ? v.text : null, note: `codex ${v ? v.text : "(version unreadable)"} is outside the tested app-server range ${shown}; launched without the app-server adapter` };
  return {
    ok: false,
    version: v ? v.text : null,
    note: `codex ${v ? v.text : "(version unknown)"} is outside the tested app-server range ${shown}; launched without the app-server adapter (mail is pasted into its pane). Set adapter "off" to silence this.`,
  };
}

/**
 * Whether a codex member on the herdr transport is to get the adapter, from its roster `adapter` field: `"off"` never,
 * `"appserver"` always (the version gate then decides), and absent only once a tested range exists.
 */
export function adapterWanted(adapter, ranges = TESTED_RANGES) {
  if (adapter === "off") return false;
  if (adapter === "appserver") return true;
  return ranges.length > 0;
}

/**
 * Whether a team row is a codex member that runs on its own app-server: it asked for the adapter, or it carries the pid of
 * the server its spawn started, and it did not opt out. The row's own `adapter` field is only what the member's roster
 * setting was, so a member that got the adapter by default has none.
 */
export function rowAdapted(row) {
  return Boolean(row) && row.kind === "codex" && row.adapter !== "off" && (row.adapter === "appserver" || row.appserver_pid !== undefined);
}

/** How long the spawn waits for the warm-up turn to complete. */
export const WARMUP_TIMEOUT_MS = 90 * 1000;
/** The fixed text of the warm-up turn: harmless, and no tool use is expected. */
export const WARMUP_TEXT = "Reply with the single word READY and nothing else. Do not run any tools.";

const SANDBOX_TYPE = { "read-only": "readOnly", "workspace-write": "workspaceWrite" };

/**
 * The gate's clauses, one entry each so a test can mutate one at a time. Each takes a thread report and the modes ah pinned.
 * A missing or partial field never passes: the network clause wants the field to be exactly `false`.
 */
export const GATE_CLAUSES = {
  sandbox: (report, modes) => Boolean(report && report.sandbox && SANDBOX_TYPE[modes.sandbox] !== undefined && report.sandbox.type === SANDBOX_TYPE[modes.sandbox]),
  network: (report) => Boolean(report && report.sandbox && report.sandbox.networkAccess === false),
  approval: (report, modes) => Boolean(report) && report.approvalPolicy === modes.approval,
  // Under workspace-write the system temp locations must be excluded: a member's temp space is only its own directory.
  tmpExclusions: (report, modes) => modes.sandbox !== "workspace-write" || Boolean(report && report.sandbox && report.sandbox.excludeSlashTmp === true && report.sandbox.excludeTmpdirEnvVar === true),
};

/**
 * The gate, used at spawn and on every reconnect: the effective mode a `thread/start` or `thread/resume` report gives must equal
 * the values ah pinned (`modes = {sandbox, approval}`). Returns `{ok, failed}`, `failed` naming the clauses that did not hold;
 * a report that is missing, partial or of another shape fails every clause it cannot show.
 */
export function gateReport(report, modes, clauses = GATE_CLAUSES) {
  const failed = Object.entries(clauses).filter(([, fn]) => !fn(report && typeof report === "object" ? report : null, modes)).map(([name]) => name);
  return { ok: failed.length === 0, failed };
}

/** The JSON-RPC error codes errorClass names; any other code a server sends is "server-error". */
const RPC_CODES = new Set([-32700, -32600, -32601, -32602, -32603]);
/** The system error codes errorClass names; any other code is "server-error". */
const ERRNO_CODES = new Set(["ECONNREFUSED", "ECONNRESET", "ENOENT", "EPIPE", "ETIMEDOUT", "EACCES", "ENOTSOCK"]);

/**
 * A fixed label for an error that may carry the server's own words: a JSON-RPC code or a system error code from a short fixed
 * list, one of a few fixed words, or the constant "server-error" for any other code. Never the message and never a code the
 * server chose.
 */
export function errorClass(err) {
  if (err && err.code !== undefined && err.code !== null) {
    if (typeof err.code === "number" && RPC_CODES.has(err.code)) return `rpc-error ${err.code}`;
    if (typeof err.code === "string" && ERRNO_CODES.has(err.code)) return err.code;
    return "server-error";
  }
  const text = String(err && err.message);
  if (/timed out/i.test(text)) return "timeout";
  if (/closed|not connected/i.test(text)) return "closed";
  if (/upgrade|handshake/i.test(text)) return "handshake";
  return "error";
}

/** What an approval request is recorded as: ah's own word for each method it knows, never the server's string. */
const APPROVAL_LABELS = {
  "item/commandExecution/requestApproval": "command-approval",
  "item/fileChange/requestApproval": "file-change-approval",
  execCommandApproval: "command-approval",
  applyPatchApproval: "file-change-approval",
};
export const approvalLabel = (method) => (typeof method === "string" && Object.hasOwn(APPROVAL_LABELS, method) ? APPROVAL_LABELS[method] : "unknown-approval");

/** The classes a failed warm-up turn is reported as: from this list only, never server text. */
export const WARMUP_CLASSES = ["not-logged-in", "rate-limit", "network", "timeout", "error"];

function warmupClass(err) {
  const text = `${err && err.code !== undefined ? err.code : ""} ${err && err.message ? err.message : ""}`;
  if (/timed out|timeout/i.test(text)) return "timeout";
  if (/log.?in|logged|auth|unauthor|credential|sign.?in|401|403/i.test(text)) return "not-logged-in";
  if (/rate|limit|quota|429|usage/i.test(text)) return "rate-limit";
  if (/network|econn|enotfound|etimedout|fetch|offline|connect/i.test(text)) return "network";
  return "error";
}

/** The note a refusal carries: one fixed phrase per step, no value from the server. */
export const BRING_UP_NOTES = {
  start: "thread/start failed",
  pins: "the app-server did not apply ah's pins",
  rollout: "no rollout after warm-up",
};
export const warmupNote = (cls) => `warm-up turn failed (${WARMUP_CLASSES.includes(cls) ? cls : "error"})`;

/**
 * The spawn's steps 3-6 against an open `client`: `thread/start{cwd}` (cwd only), the gate on its report, one warm-up turn (the
 * fixed text, completion awaited), `thread/resume` (it must succeed, which shows a rollout exists), and the gate on that report.
 * Resolves `{ok: true, threadId}` or `{ok: false, step, note}` with a value-free note. Reads only the gate fields and the turn
 * status from the server, and never the reply of the warm-up turn.
 */
export async function bringUpThread(client, { cwd, modes, warmupMs = WARMUP_TIMEOUT_MS, text = WARMUP_TEXT }) {
  const refuse = (step, note) => ({ ok: false, step, note });
  let started;
  try {
    started = await client.request("thread/start", { cwd });
  } catch {
    return refuse("start", BRING_UP_NOTES.start);
  }
  const threadId = started && started.thread && typeof started.thread.id === "string" ? started.thread.id : null;
  if (!threadId) return refuse("start", BRING_UP_NOTES.start);
  if (!gateReport(started, modes).ok) return refuse("gate", BRING_UP_NOTES.pins);

  const done = new Promise((resolve) => {
    client.onNotification(({ method, params }) => {
      if (method !== "turn/completed") return;
      const ev = params && (params.threadId || (params.turn && params.turn.threadId));
      if (ev !== threadId) return;
      const status = params.turn && params.turn.status ? String(params.turn.status) : "";
      resolve(status === "completed" ? { ok: true } : { ok: false, cls: "error" });
    });
    client.onClose(() => resolve({ ok: false, cls: "network" }));
  });
  let timer;
  const timeout = new Promise((resolve) => { timer = setTimeout(() => resolve({ ok: false, cls: "timeout" }), warmupMs); if (timer.unref) timer.unref(); });
  let turn;
  try {
    turn = await client.request("turn/start", { threadId, input: [{ type: "text", text }] });
  } catch (e) {
    clearTimeout(timer);
    return refuse("warmup", warmupNote(warmupClass(e)));
  }
  void turn;
  const outcome = await Promise.race([done, timeout]);
  clearTimeout(timer);
  if (!outcome.ok) return refuse("warmup", warmupNote(outcome.cls));

  let resumed;
  try {
    resumed = await client.request("thread/resume", { threadId });
  } catch {
    return refuse("rollout", BRING_UP_NOTES.rollout);
  }
  if (!gateReport(resumed, modes).ok) return refuse("gate", BRING_UP_NOTES.pins);
  return { ok: true, threadId };
}

export const ACK_WAIT_MS = 60 * 1000;

/**
 * One member's hand-over state. `client` is an open appServerClient; `threadId` is the thread the
 * member's terminal UI works in. `text` handed to `handOver(ids, text)` goes out as one turn/start when
 * no turn is current and as one turn/steer otherwise, one after the other. An entry id is acked (`ack(ids)`)
 * when the server reports the user message item carrying that exact text, and never if it does not within
 * ACK_WAIT_MS. Activity (`activity(state, blockedBy)`) follows turn/started, turn/completed and approval
 * requests. This object never answers an approval request and never re-sends.
 */
export function memberSession({ client, threadId, ack, activity, now = Date.now, setTimer = setTimeout, clearTimer = clearTimeout, ackWaitMs = ACK_WAIT_MS }) {
  let turnId = null;
  let tail = Promise.resolve();
  const waiting = [];

  const textOf = (item) => {
    if (!item || item.type !== "userMessage") return null;
    const parts = Array.isArray(item.content) ? item.content : Array.isArray(item.input) ? item.input : [];
    return parts.filter((p) => p && p.type === "text").map((p) => p.text).join("");
  };
  const settle = (w, acked) => {
    const i = waiting.indexOf(w);
    if (i < 0) return;
    waiting.splice(i, 1);
    clearTimer(w.timer);
    if (acked) ack(w.ids);
  };

  client.onNotification(({ method, params }) => {
    const eventThread = params && (params.threadId || (params.turn && params.turn.threadId) || (params.item && params.item.threadId));
    if (!eventThread || eventThread !== threadId) return;
    if (method === "turn/started") {
      turnId = (params.turn && params.turn.id) || params.turnId || null;
      activity("working", null);
    } else if (method === "turn/completed") {
      turnId = null;
      const failed = params.turn && params.turn.status && /error|fail/i.test(String(params.turn.status));
      activity(failed ? "failed" : "idle", null);
    } else if (method === "item/started" || method === "item/completed") {
      const text = textOf(params.item);
      if (text === null) return;
      const w = waiting.find((x) => x.text === text);
      if (w) settle(w, true);
    }
  });
  client.onServerRequest((req) => {
    if (/approval/i.test(String(req.method || ""))) activity("blocked", approvalLabel(req.method));
  });

  const start = (text) => client.request("turn/start", { threadId, input: [{ type: "text", text }] }).then((r) => {
    if (r && r.turn && r.turn.id && !turnId) turnId = r.turn.id;
  });
  const steer = (text) => client.request("turn/steer", { threadId, expectedTurnId: turnId, input: [{ type: "text", text }] }).catch((e) => {
    if (/expected|mismatch|no active turn/i.test(String(e && e.message))) {
      turnId = null;
      return start(text);
    }
    throw e;
  });

  return {
    get turnId() { return turnId; },
    /** Queue one hand-over; resolves once the server answered it (not once it is acked). */
    handOver(ids, text) {
      const w = { ids, text, timer: null };
      waiting.push(w);
      w.timer = setTimer(() => settle(w, false), ackWaitMs);
      if (w.timer && w.timer.unref) w.timer.unref();
      const run = () => (turnId ? steer(text) : start(text));
      const p = tail.then(run);
      tail = p.catch(() => {});
      p.catch(() => settle(w, false));
      return p;
    },
    pendingAcks: () => waiting.length,
  };
}
