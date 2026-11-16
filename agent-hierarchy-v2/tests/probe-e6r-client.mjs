#!/usr/bin/env node
// Probe E6r-1 (R09 §3), the second client: a dependency-free WebSocket + JSON-RPC client for `codex app-server`'s unix
// socket. Used only by tests/probe-e6r.sh.
//
//   node tests/probe-e6r-client.mjs <socket> <tui cwd> <work dir> [selftest]
//
// selftest: connect, initialize, thread/start{cwd}, config/read, thread/list, thread/resume, the gate, record, exit (no turn runs).
// Otherwise the scenario of R09 §3 (r9/r10, create then attach) runs in steps: thread/start, the gate, one warm-up turn, thread/resume
// and the gate again, the id to <work>/thread-id, wait for <work>/go-create (the TUI is launched with `codex resume <id> --remote`
// in between), the gate once more, then the turns. After each step the client writes <work>/at-N and waits for the shell
// script to create <work>/go-N (which is when the script captures the TUI's screen). Everything it learns goes to
// <work>/client.json. It never prints a login value; the messages it sends are fixed texts (marker replies and a 300-word request).

import crypto from "node:crypto";
import fs from "node:fs";
import net from "node:net";
import path from "node:path";
import { gateOf } from "./probe-e6r-gate.mjs";

const [sockPath, tuiCwd, work, mode, lever] = process.argv.slice(2);
const log = { steps: {}, errors: [], notifications: {}, serverRequests: [] };
const save = () => fs.writeFileSync(path.join(work, "client.json"), JSON.stringify(log, null, 2) + "\n");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const MARK = (n) => `E6R1-MARKER-${n}`;

function connect(socketPath) {
  return new Promise((resolve, reject) => {
    const c = net.connect(socketPath);
    const key = crypto.randomBytes(16).toString("base64");
    let buf = Buffer.alloc(0);
    let upgraded = false;
    let fragments = [];
    const handlers = { message: () => {}, close: () => {} };
    const api = {
      on: (ev, fn) => (handlers[ev] = fn),
      send(text) {
        const payload = Buffer.from(text);
        const mask = crypto.randomBytes(4);
        let head;
        if (payload.length < 126) head = Buffer.from([0x81, 0x80 | payload.length]);
        else if (payload.length < 65536) {
          head = Buffer.alloc(4);
          head[0] = 0x81;
          head[1] = 0x80 | 126;
          head.writeUInt16BE(payload.length, 2);
        } else {
          head = Buffer.alloc(10);
          head[0] = 0x81;
          head[1] = 0x80 | 127;
          head.writeBigUInt64BE(BigInt(payload.length), 2);
        }
        const masked = Buffer.from(payload);
        for (let i = 0; i < masked.length; i++) masked[i] ^= mask[i % 4];
        c.write(Buffer.concat([head, mask, masked]));
      },
      close: () => c.end(),
    };
    const parse = () => {
      for (;;) {
        if (buf.length < 2) return;
        const fin = buf[0] & 0x80;
        const opcode = buf[0] & 0x0f;
        let len = buf[1] & 0x7f;
        let off = 2;
        if (len === 126) {
          if (buf.length < 4) return;
          len = buf.readUInt16BE(2);
          off = 4;
        } else if (len === 127) {
          if (buf.length < 10) return;
          len = Number(buf.readBigUInt64BE(2));
          off = 10;
        }
        const masked = buf[1] & 0x80;
        const maskKey = masked ? buf.slice(off, off + 4) : null;
        if (masked) off += 4;
        if (buf.length < off + len) return;
        let payload = Buffer.from(buf.slice(off, off + len));
        if (maskKey) for (let i = 0; i < payload.length; i++) payload[i] ^= maskKey[i % 4];
        buf = buf.slice(off + len);
        if (opcode === 8) return handlers.close();
        if (opcode === 9) continue; // ping: the server does not need a pong for this probe's length
        if (opcode === 1 || opcode === 0) {
          fragments.push(payload);
          if (fin) {
            handlers.message(Buffer.concat(fragments).toString());
            fragments = [];
          }
        }
      }
    };
    c.on("connect", () => {
      c.write(`GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`);
    });
    c.on("data", (d) => {
      buf = Buffer.concat([buf, d]);
      if (!upgraded) {
        const i = buf.indexOf("\r\n\r\n");
        if (i < 0) return;
        const head = buf.slice(0, i).toString();
        if (!/^HTTP\/1\.1 101/.test(head)) return reject(new Error("upgrade refused: " + head.split("\r\n")[0]));
        buf = buf.slice(i + 4);
        upgraded = true;
        resolve(api);
      }
      parse();
    });
    c.on("error", reject);
    c.on("close", () => handlers.close());
  });
}

const ws = await connect(sockPath);
let nextId = 1;
const pending = new Map();
const waiters = [];
ws.on("message", (text) => {
  let m;
  try {
    m = JSON.parse(text);
  } catch {
    return;
  }
  if (m.id !== undefined && (m.result !== undefined || m.error !== undefined) && pending.has(m.id)) {
    pending.get(m.id)(m);
    pending.delete(m.id);
  } else if (m.method && m.id !== undefined) {
    log.serverRequests.push({ method: m.method });
  } else if (m.method) {
    log.notifications[m.method] = (log.notifications[m.method] || 0) + 1;
    for (const w of [...waiters]) if (w.match(m)) { waiters.splice(waiters.indexOf(w), 1); w.resolve(m); }
  }
});
ws.on("close", () => {});
const request = (method, params) =>
  new Promise((resolve) => {
    const id = nextId++;
    pending.set(id, resolve);
    ws.send(JSON.stringify({ jsonrpc: "2.0", id, method, params }));
  });
const notify = (method, params) => ws.send(JSON.stringify(params === undefined ? { jsonrpc: "2.0", method } : { jsonrpc: "2.0", method, params }));
const waitFor = (match, ms) =>
  new Promise((resolve) => {
    const w = { match, resolve };
    waiters.push(w);
    setTimeout(() => { const i = waiters.indexOf(w); if (i >= 0) { waiters.splice(i, 1); resolve(null); } }, ms);
  });
const step = async (n, label) => {
  fs.writeFileSync(path.join(work, `at-${n}`), label + "\n");
  for (let i = 0; i < 2400 && !fs.existsSync(path.join(work, `go-${n}`)); i++) await sleep(100);
};
const short = (r) => (r.error ? { error: r.error } : { ok: true, keys: Object.keys(r.result || {}) });

// ---- connect and initialize
const init = await request("initialize", { clientInfo: { name: "ah-probe-e6r", title: "ah probe", version: "0" } });
log.initialize = short(init);
if (init.error) { log.errors.push("initialize: " + JSON.stringify(init.error)); save(); process.exit(0); }
notify("initialized");

// ---- the thread: `thread/start{cwd}` and nothing else (R09 r9: ahd creates the thread, the TUI attaches to it)
const pick = (o, out = {}, prefix = "", depth = 0) => {
  if (!o || typeof o !== "object" || depth > 4) return out;
  for (const [k, v] of Object.entries(o)) {
    const key = prefix ? prefix + "." + k : k;
    if (/sandbox|approval|network|writable|permission|profile/i.test(k)) out[key] = v;
    else if (v && typeof v === "object" && !Array.isArray(v)) pick(v, out, key, depth + 1);
  }
  return out;
};
const norm = (p) => (typeof p === "string" ? p.replace(/^\/private/, "") : p);
// The hard gate (R09 r9, noflags run): the resumed thread's effective mode as the server reports it, before any turn.
// Pass only for sandbox readOnly, network off, approval never (tests/probe-e6r-gate.mjs, which has its own mutant check). `lever` is a dry-run variant: "approval-param" sends
// approvalPolicy in thread/start (informational; the ruled call is cwd only).
const startParams = lever === "approval-param" ? { cwd: tuiCwd, approvalPolicy: "never" } : { cwd: tuiCwd };
const started = await request("thread/start", startParams);
log.threadCreate = { ...short(started), fields: started.error ? undefined : pick(started.result) };
const threadId = started.error ? null : started.result?.thread?.id || started.result?.threadId || started.result?.id || null;
log.threadCreated = Boolean(threadId);
log.threadStartCwd = started.error ? null : norm(started.result?.cwd || started.result?.thread?.cwd || null);
const cfg = await request("config/read", {});
log.configRead = { ...short(cfg), fields: cfg.error ? undefined : pick(cfg.result) };
const list = await request("thread/list", {});
log.threadList = short(list);
if (!list.error) log.threadListCount = (Object.values(list.result || {}).find(Array.isArray) || []).length;
log.gate = { at_start: log.threadCreate.fields ? gateOf(log.threadCreate.fields) : { pass: false, error: log.threadCreate.error } };
const abortOnGate = (where) => {
  log.gateAbort = where;
  save();
  fs.writeFileSync(path.join(work, "gate-abort"), where + "\n");
  process.exit(3);
};
if (threadId) {
  // what the server now says about the thread (a resume read; no turn, no model call)
  const re = await request("thread/resume", { threadId });
  log.threadResumeRead = { ...short(re), fields: re.error ? undefined : pick(re.result) };
  if (!re.error) log.gate.at_resume_read = gateOf(log.threadResumeRead.fields);
}
// A thread with no turn has no rollout, so thread/resume cannot read it ("no rollout found"): then thread/start's own report is the
// effective mode. A resume read that does return fields must pass too.
log.gate.pass = Boolean(log.gate.at_start.pass && (log.gate.at_resume_read ? log.gate.at_resume_read.pass : true));
if (mode === "selftest" || !threadId) { save(); process.exit(0); }
if (!log.gate.pass) abortOnGate("after thread create, before the warm-up turn");

// ---- the warm-up turn (R09 r10): a thread with no turn has no rollout, and `codex resume` resumes a rollout. Fixed, harmless text,
// no tool use expected; it runs only after the gate above passed.
const WARMUP = `Reply with the single word ${MARK(0)} and nothing else. Do not run any tools.`;
const wDone = waitFor((m) => m.method === "turn/completed", 180000);
const wStart = await request("turn/start", { threadId, input: [{ type: "text", text: WARMUP }] });
log.warmup = { start: short(wStart) };
const wCompleted = wStart.error ? null : await wDone;
log.warmup.completed = Boolean(wCompleted);
const usage = log.notifications["thread/tokenUsage/updated"] || 0;
log.warmup.token_usage_notifications = usage;
const rr = await request("thread/resume", { threadId });
log.warmup.resume_read = { ...short(rr), fields: rr.error ? undefined : pick(rr.result) };
log.warmup.rollout_after = !rr.error;
if (rr.error) {
  log.stop = "no-rollout-after-turn";
  save();
  fs.writeFileSync(path.join(work, "stop-no-rollout"), String(rr.error.message || "") + "\n");
  process.exit(5);
}
log.gate.after_warmup = gateOf(log.warmup.resume_read.fields);
if (!log.gate.after_warmup.pass) abortOnGate("after the warm-up turn");
fs.writeFileSync(path.join(work, "thread-id"), threadId + "\n");
save();

// ---- wait for the script to launch the TUI against the id, then ask whether the server holds the thread as loaded
for (let i = 0; i < 1800 && !fs.existsSync(path.join(work, "go-create")); i++) await sleep(100);
const reread = await request("thread/resume", { threadId });
log.threadResumeReadAfterAttach = { ...short(reread), fields: reread.error ? undefined : pick(reread.result) };
log.gate.after_attach = reread.error ? { pass: false, error: reread.error } : gateOf(log.threadResumeReadAfterAttach.fields);
if (!log.gate.after_attach.pass) abortOnGate("after the TUI attached");
const loaded = await request("thread/loaded/list", {});
log.threadLoadedList = short(loaded);
if (!loaded.error) {
  const ids = (Object.values(loaded.result || {}).find(Array.isArray) || []).map((x) => (typeof x === "string" ? x : x.id));
  log.loadedThreadIds = ids.length;
  log.idLoadedOnServer = ids.includes(threadId);
}
log.foundVia = "created (B)";
save();
await step(1, "attached");

// ---- step 2: a turn while the TUI is idle
const w1 = waitFor((m) => m.method === "turn/completed", 120000);
const t1 = await request("turn/start", { threadId, input: [{ type: "text", text: `Reply with the single word ${MARK(1)} and nothing else.` }] });
log.steps.turn1_start = short(t1);
const done1 = t1.error ? null : await w1;
log.steps.turn1_completed_delivered = Boolean(done1);
save();
await step(2, "turn 1 done");

// ---- step 3: a long turn, steered mid-way
const w2 = waitFor((m) => m.method === "turn/completed", 180000);
const t2 = await request("turn/start", { threadId, input: [{ type: "text", text: "Write 300 words about rivers. No tools." }] });
log.steps.turn2_start = short(t2);
const turnId = t2.result && (t2.result.turn?.id || t2.result.turnId);
await sleep(2500);
const steer = turnId
  ? await request("turn/steer", { threadId, expectedTurnId: turnId, input: [{ type: "text", text: `After that, also say ${MARK(2)}.` }] })
  : { error: { message: "no turn id from turn/start" } };
log.steps.turn2_steer = short(steer);
const done2 = t2.error ? null : await w2;
log.steps.turn2_completed_delivered = Boolean(done2);
save();
await step(3, "turn 2 steered");

// ---- step 4 and 5: a turn started while a human has typed a draft in the TUI
await step(4, "draft typed by the script, turn 3 next");
const w3 = waitFor((m) => m.method === "turn/completed", 120000);
const t3 = await request("turn/start", { threadId, input: [{ type: "text", text: `Reply with the single word ${MARK(3)} and nothing else.` }] });
log.steps.turn3_start = short(t3);
const done3 = t3.error ? null : await w3;
log.steps.turn3_completed_delivered = Boolean(done3);
save();
await step(5, "turn 3 done");
log.finished = true;
save();
ws.close();
process.exit(0);
