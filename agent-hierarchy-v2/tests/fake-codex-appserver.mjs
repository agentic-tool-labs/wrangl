#!/usr/bin/env node
// A stand-in Codex app-server for tests: WebSocket over a Unix socket, JSON-RPC text frames, replaying the
// message shapes seen in a live run (initialize, thread/start, turn/start, turn/steer, item and turn
// notifications). It touches nothing but its socket and its log.
// Usage: node fake-codex-appserver.mjs <socket path> <log file> [options-json] [the real server's argv ...]
//   Anything after the options is read as the `codex` argv a real server would get: its `-c sandbox_mode`, `-c approval_policy`
//   and `-c sandbox_workspace_write.network_access` values become what thread/start and thread/resume report.
//   options: turnMs (a turn lasts this long, default 300), noItems (never report the user message item),
//            steerBoundaryMs (a steered message is reported this long after the steer, default 0),
//            itemDelayMs (the user message item of a turn/start is reported this long after the turn starts, default 0),
//            stall (a method name, or "all": requests for it are logged and never answered),
//            stallUpgrade (the upgrade request is never answered),
//            hugeHeader (the upgrade answer is an endless head, never finished),
//            hugeLength (right after the upgrade, a frame header declaring a 4 GiB payload is sent),
//            threadStartError (thread/start answers with an error), turnError (turn/start answers with an error),
//            neverComplete (a turn starts and is never completed), noRollout (a completed turn still leaves no rollout),
//            sandboxType / networkAccess / approval (override what thread/start reports), noNetworkField (omit networkAccess),
//            excludeSlashTmp / excludeTmpdirEnvVar (override what a workspace-write report says about the system temp locations),
//            threadIdText (the id thread/start reports instead of th-N),
//            resumeSandboxType / resumeApproval (override what thread/resume reports), sentinel (a string every error message
//            and report carries, for tests that look for it in places it must not reach)
// A thread has no rollout until one turn has completed on it: thread/resume says "no rollout found" before then.
// Control methods a test may call as a normal client: fake/approval (pushes a server-initiated approval
// request), fake/fail (ends the current turn with an error status), fake/reset (forget the current turn).
// Every request received is appended to the log as one JSON line `{method, params}`.

import { appendFileSync, rmSync } from "node:fs";
import { createServer } from "node:net";

import { acceptKey, encodeFrame, frameReader } from "./lib-codex-proto.mjs";

const [sockPath, logPath, optText] = process.argv.slice(2);
const opts = { turnMs: 300, noItems: false, steerBoundaryMs: 0, itemDelayMs: 0, ...(optText ? JSON.parse(optText) : {}) };
// the real server's pins, from its argv
const serverPins = {};
{
  const rest = process.argv.slice(5);
  for (let i = 0; i < rest.length; i++) {
    if (rest[i] !== "-c" || i + 1 >= rest.length) continue;
    const m = /^([A-Za-z0-9_.]+)=(.*)$/.exec(rest[i + 1]);
    if (m) serverPins[m[1]] = m[2].replace(/^"(.*)"$/, "$1");
  }
}
const SANDBOX_TYPE = { "read-only": "readOnly", "workspace-write": "workspaceWrite", "danger-full-access": "dangerFullAccess" };
const sandboxReport = (kind) => {
  const type = (kind === "resume" && opts.resumeSandboxType) || opts.sandboxType || SANDBOX_TYPE[serverPins.sandbox_mode] || "workspaceWrite";
  const net = opts.networkAccess !== undefined ? opts.networkAccess : serverPins["sandbox_workspace_write.network_access"] === "false" ? false : true;
  const excl = (key, opt) => (opts[opt] !== undefined ? opts[opt] : serverPins[`sandbox_workspace_write.${key}`] === "true");
  return {
    type,
    ...(opts.noNetworkField ? {} : { networkAccess: net }),
    ...(type === "workspaceWrite" ? { excludeSlashTmp: excl("exclude_slash_tmp", "excludeSlashTmp"), excludeTmpdirEnvVar: excl("exclude_tmpdir_env_var", "excludeTmpdirEnvVar") } : {}),
  };
};
const modeReport = (kind) => ({
  sandbox: sandboxReport(kind),
  approvalPolicy: (kind === "resume" && opts.resumeApproval) || opts.approval || serverPins.approval_policy || "on-request",
  ...(opts.sentinel ? { note: opts.sentinel } : {}),
});
const errText = (m) => (opts.sentinel ? `${m} ${opts.sentinel}` : m);
try { rmSync(sockPath, { force: true }); } catch {}

let seq = 0;
const clients = new Set();
let turn = null;
let timer = null;
const threads = [];
let clock = 0;

const push = (obj) => { for (const c of clients) c.write(encodeFrame(JSON.stringify(obj))); };
const note = (method, params) => push({ jsonrpc: "2.0", method, params });
const item = (text) => ({ id: `it-${++seq}`, type: "userMessage", content: [{ type: "text", text }] });
const endTurn = (status = opts.turnStatus || "completed") => {
  if (!turn) return;
  const t = turn;
  turn = null;
  clearTimeout(timer);
  const th = threads.find((x) => x.id === t.threadId);
  if (th && !opts.noRollout) th.rollout = true;
  note("turn/completed", { threadId: t.threadId, turn: { id: t.id, status } });
};
const report = (threadId, text, delayMs = 0) => {
  if (opts.noItems) return;
  const turnId = turn && turn.id;
  const emit = () => {
    const it = item(text);
    note("item/started", { threadId, turnId, item: it });
    note("item/completed", { threadId, turnId, item: it });
  };
  if (delayMs > 0) setTimeout(emit, delayMs);
  else emit();
};

const handle = (c, m) => {
  const reply = (result) => c.write(encodeFrame(JSON.stringify({ jsonrpc: "2.0", id: m.id, result })));
  const error = (message) => c.write(encodeFrame(JSON.stringify({ jsonrpc: "2.0", id: m.id, error: { code: opts.errorCode !== undefined ? opts.errorCode : -32600, message } })));
  if (!m.method) return void appendFileSync(logPath, JSON.stringify({ response: m }) + "\n");
  appendFileSync(logPath, JSON.stringify({ method: m.method, params: m.params }) + "\n");
  if (m.id === undefined) return;
  if (opts.stall === "all" || opts.stall === m.method) return;
  const text = m.params && Array.isArray(m.params.input) ? m.params.input.map((p) => p.text).join("") : "";
  switch (m.method) {
    case "initialize":
      if (opts.initializeError) return error(errText("initialize failed"));
      return reply({ userAgent: "fake-codex-appserver" });
    case "thread/start": {
      if (opts.threadStartError) return error(errText("thread/start failed"));
      const n = ++seq;
      const id = opts.threadIdText || `th-${n}`;
      threads.push({ id, cwd: (m.params && m.params.cwd) || null, updatedAt: ++clock, rollout: false });
      reply({ thread: { id }, cwd: (m.params && m.params.cwd) || null, ...modeReport("start") });
      return note("thread/started", { thread: { id } });
    }
    case "thread/list": return reply({ data: threads.map((t) => ({ ...t })), nextCursor: null });
    case "thread/resume": {
      const t = threads.find((x) => x.id === (m.params && m.params.threadId));
      if (!t) return error(errText("no such thread"));
      if (!t.rollout) return error(errText(`no rollout found for thread id ${t.id}`));
      return reply({ thread: { id: t.id, cwd: t.cwd }, ...modeReport("resume") });
    }
    case "fake/seed": threads.push({ id: m.params.id, cwd: m.params.cwd, updatedAt: m.params.updatedAt ?? ++clock, rollout: true }); return reply({});
    case "turn/start": {
      if (opts.turnError) return error(errText(opts.turnError === true ? "turn/start failed" : String(opts.turnError)));
      if (turn) return error("a turn is already running");
      turn = { id: `tu-${++seq}`, threadId: m.params.threadId };
      reply({ turn: { id: turn.id, status: "inProgress" } });
      note("turn/started", { threadId: turn.threadId, turn: { id: turn.id } });
      report(turn.threadId, text, opts.itemDelayMs);
      if (!opts.neverComplete) timer = setTimeout(() => endTurn(), opts.turnMs);
      return undefined;
    }
    case "turn/steer": {
      if (!turn || m.params.expectedTurnId !== turn.id) return error("expectedTurnId mismatch");
      reply({ turnId: turn.id });
      const t = turn;
      setTimeout(() => { if (turn === t) report(t.threadId, text); }, opts.steerBoundaryMs);
      return undefined;
    }
    case "fake/approval":
      reply({});
      return push({ jsonrpc: "2.0", id: 9000 + ++seq, method: opts.approvalMethod || "item/commandExecution/requestApproval", params: { threadId: m.params && m.params.threadId } });
    case "fake/fail": reply({}); return endTurn("failed");
    case "fake/reset": turn = null; clearTimeout(timer); return reply({});
    default: return error(`unknown method ${m.method}`);
  }
};

const server = createServer((c) => {
  let head = Buffer.alloc(0);
  let up = false;
  const reader = frameReader();
  c.on("error", () => {});
  c.on("close", () => clients.delete(c));
  c.on("data", (chunk) => {
    if (!up) {
      head = Buffer.concat([head, chunk]);
      const end = head.indexOf("\r\n\r\n");
      if (end < 0) return;
      const key = /sec-websocket-key:\s*(\S+)/i.exec(head.subarray(0, end).toString("utf8"));
      if (opts.stallUpgrade) return;
      if (opts.hugeHeader) {
        c.write("HTTP/1.1 101 Switching Protocols\r\n" + "X-Junk: " + "a".repeat(32 * 1024));
        return;
      }
      c.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${acceptKey(key[1])}\r\n\r\n`);
      up = true;
      clients.add(c);
      if (opts.hugeLength) c.write(Buffer.from([0x81, 127, 0, 0, 0, 1, 0, 0, 0, 0]));
      chunk = head.subarray(end + 4);
      if (!chunk.length) return;
    }
    for (const f of reader.push(chunk)) {
      if (f.opcode !== 1) continue;
      try { handle(c, JSON.parse(f.data.toString("utf8"))); } catch (e) { appendFileSync(logPath, JSON.stringify({ fakeError: String(e) }) + "\n"); }
    }
  });
});
server.listen(sockPath, () => process.stdout.write("listening\n"));
const stop = () => { server.close(); try { rmSync(sockPath, { force: true }); } catch {} process.exit(0); };
process.on("SIGTERM", stop);
process.on("SIGINT", stop);
