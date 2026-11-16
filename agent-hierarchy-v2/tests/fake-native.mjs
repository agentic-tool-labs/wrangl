#!/usr/bin/env node
/**
 * A stand-in for the native `ahd` binary, for the suites of the writer stamp (tests/test-state-writer-stamp.sh).
 * A test wraps it as `<dir>/ahd` (`exec node fake-native.mjs "$@"`) and gives it a `.contract` sidecar.
 *
 *   fake-native.mjs state writer --check        exit 0 (the file <AH_HOME>/fake-native.check holding `fail`: exit 3)
 *   fake-native.mjs serve --ah-home <home>      a daemon that answers /v1/health with impl "native", /v1/state/<d>/<op> with
 *                                               {"result": {"ok": true, "native": true}} and /v1/drain; AHD_FAKE_NATIVE_SERVE=exit1
 *                                               exits 1 at once; AHD_FAKE_NATIVE_NO_IMPL=1 leaves `impl` out of health;
 *                                               AHD_FAKE_NATIVE_DELAY_MS=<n> starts listening n ms late;
 *                                               AHD_FAKE_NATIVE_STATE=refuse answers every state route 422 op_failed
 *                                               (error_code writer_mismatch) after appending the hook-errors row a refusing writer makes
 *   fake-native.mjs state <domain> <op>         the one-shot: reads stdin, prints {"result": {"ok": true, "oneshot": true}}
 *                                               (AHD_FAKE_NATIVE_ONESHOT=<code> exits with that code, printing an error body; 77 also
 *                                               appends the hook-errors row a refusing writer makes)
 * Every call is one line in <AH_HOME>/fake-native.log. A fake daemon exits after 90 s whatever happens.
 */

import { appendFileSync, mkdirSync, readFileSync, renameSync, statSync, unlinkSync, writeFileSync } from "node:fs";
import http from "node:http";
import { join } from "node:path";

const argv = process.argv.slice(2);
const home = process.env.AH_HOME;
const record = (line) => {
  try {
    appendFileSync(join(home, "fake-native.log"), `${line}\n`);
  } catch {
    // a log that cannot be written changes nothing
  }
};
const VERSION = process.env.AHD_VERSION_OVERRIDE || "999.0.0";

if (argv[0] === "state" && argv[1] === "writer" && argv[2] === "--check") {
  record("check");
  let mode = "ok";
  try {
    mode = readFileSync(join(home, "fake-native.check"), "utf8").trim();
  } catch {
    // no mode file: the check passes
  }
  process.exit(mode === "fail" ? 3 : 0);
}

if (argv[0] === "state" && argv.length >= 3) {
  const stdin = readFileSync(0, "utf8");
  record(`oneshot ${argv[1]} ${argv[2]} ${stdin.length} dispatched=${process.env.AH_DISPATCHED || "-"} root=${process.env.AH_PLUGIN_ROOT || "-"}`);
  const code = Number(process.env.AHD_FAKE_NATIVE_ONESHOT || 0);
  if (code === 77) {
    // the process that performs the refused check logs it: one row in the hook-errors file
    try {
      const dir = join(process.env.HOME, ".claude", "ah2", "hierarchy");
      mkdirSync(dir, { recursive: true });
      appendFileSync(join(dir, "hook-errors.jsonl"), JSON.stringify({ ts: new Date().toISOString(), hook: "fake-native", message: "writer_mismatch" }) + "\n");
    } catch {
      // the row is evidence only
    }
  }
  if (code) {
    process.stdout.write(JSON.stringify({ error: { code: "fake", message: `fake exit ${code}` } }) + "\n");
    process.exit(code);
  }
  process.stdout.write(JSON.stringify({ result: { ok: true, oneshot: true } }) + "\n");
  process.exit(0);
}

if (argv[0] === "serve") {
  record(`serve ${argv.slice(1).join(" ")} dispatched=${process.env.AH_DISPATCHED || "-"} root=${process.env.AH_PLUGIN_ROOT || "-"}`);
  if (process.env.AHD_FAKE_NATIVE_SERVE === "exit1") process.exit(1);
  const run = join(home, "run");
  mkdirSync(run, { recursive: true });
  const sock = join(run, "ahd.sock");
  const info = join(run, "ahd.json");
  try {
    unlinkSync(sock);
  } catch {
    // none left over
  }
  const started = new Date().toISOString();
  const reply = (res, status, body) => {
    const text = JSON.stringify(body);
    res.writeHead(status, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(text), "Ah-Daemon-Version": VERSION, "Ah-Protocol": "1" });
    res.end(text);
  };
  const server = http.createServer((req, res) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => {
      const url = new URL(req.url, "http://ahd");
      if (url.pathname === "/v1/health") {
        const body = { pid: process.pid, version: VERSION, protocol: 1, protocols: [1], started_at: started, root: "/fake", owns: { mailbox: true, status: false, timers: [] } };
        if (process.env.AHD_FAKE_NATIVE_NO_IMPL !== "1") body.impl = "native";
        return reply(res, 200, body);
      }
      if (url.pathname === "/v1/drain") {
        reply(res, 200, { ok: true });
        record("drain");
        return setImmediate(() => process.exit(0));
      }
      const m = /^\/v1\/state\/([^/]+)\/([^/]+)$/.exec(url.pathname);
      if (m) {
        record(`state ${m[1]} ${m[2]} ${Buffer.concat(chunks).length}`);
        if (process.env.AHD_FAKE_NATIVE_STATE === "refuse") {
          try {
            const dir = join(process.env.HOME, ".claude", "ah2", "hierarchy");
            mkdirSync(dir, { recursive: true });
            appendFileSync(join(dir, "hook-errors.jsonl"), JSON.stringify({ ts: new Date().toISOString(), hook: "fake-native", message: "writer_mismatch" }) + "\n");
          } catch {
            // the row is evidence only
          }
          return reply(res, 422, { error: { code: "op_failed", message: "ah's state writer is node; restart this session", error_code: "writer_mismatch" } });
        }
        return reply(res, 200, { result: { ok: true, native: true } });
      }
      return reply(res, 404, { error: { code: "not_found", message: "no such path" } });
    });
  });
  const delay = Number(process.env.AHD_FAKE_NATIVE_DELAY_MS || 0);
  setTimeout(() => server.listen(sock, () => {
    const tmp = `${info}.${process.pid}.tmp`;
    writeFileSync(tmp, JSON.stringify({ v: 1, pid: process.pid, version: VERSION, protocol: 1, socket: sock, root: "/fake", started_at: started }) + "\n");
    renameSync(tmp, info);
    record(`listening ${process.pid}`);
    // the daemon this process is, while ahd.json still names it
    setInterval(() => {
      try {
        if (JSON.parse(readFileSync(info, "utf8")).pid !== process.pid) process.exit(0);
        statSync(sock);
      } catch {
        process.exit(0);
      }
    }, 1000);
  }), delay);
  setTimeout(() => process.exit(0), 90_000);
} else {
  process.stderr.write(`fake-native: unknown verb ${argv.join(" ")}\n`);
  process.exit(70);
}
