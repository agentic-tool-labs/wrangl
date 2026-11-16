// Wire (K1) support for the contract tooling: start a daemon of either implementation on a sandbox AH_HOME, and make one
// HTTP request to its unix socket. Imports nothing from hooks/ (G2): the daemon is started as a child process.

import { spawn } from "node:child_process";
import http from "node:http";
import { existsSync, readFileSync, openSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { PLUGIN } from "./lib-case.mjs";

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/**
 * Starts the native daemon for AH_HOME `ah`: `ahd serve --ah-home <ah>`, the binary named by AHD_NATIVE_BIN.
 * Resolves {pid, socket, child, stop}.
 */
export async function startDaemon({ ah, env }) {
  mkdirSync(join(ah, "run"), { recursive: true });
  const logfd = openSync(join(ah, "daemon.out"), "a");
  const cmd = process.env.AHD_NATIVE_BIN || env.AHD_NATIVE_BIN || "ahd";
  const args = ["serve", "--ah-home", ah];
  const child = spawn(cmd, args, { env, stdio: ["ignore", logfd, logfd], detached: false });
  let exited = false;
  let spawnError = null;
  child.on("exit", () => { exited = true; });
  child.on("error", (e) => { spawnError = e; exited = true; });
  const infoPath = join(ah, "run", "ahd.json");
  const t0 = Date.now();
  let info = null;
  while (Date.now() - t0 < 8000) {
    if (spawnError) throw new Error(`daemon could not be started: ${spawnError.message}`);
    if (exited) {
      let said = "";
      try { said = readFileSync(join(ah, "daemon.out"), "utf8"); } catch { said = ""; }
      throw Object.assign(new Error(`daemon exited early (see ${join(ah, "daemon.out")})`), { unitNotNative: /is not implemented natively/.test(said) });
    }
    if (existsSync(infoPath)) {
      try { info = JSON.parse(readFileSync(infoPath, "utf8")); } catch { info = null; }
      if (info && info.socket) {
        const r = await request(info.socket, { method: "GET", path: "/v1/health" }).catch(() => null);
        if (r && r.status === 200) break;
      }
    }
    await sleep(40);
  }
  if (!info) throw new Error("daemon did not come up within 8 s");
  const stop = async () => {
    if (exited) return;
    child.kill("SIGTERM");
    const t1 = Date.now();
    while (!exited && Date.now() - t1 < 6000) await sleep(40);
    if (!exited) child.kill("SIGKILL");
  };
  return { pid: child.pid, socket: info.socket, child, stop, info };
}

/** One HTTP request over a unix socket. `body` is a string, a Buffer, an object (sent as JSON) or null. */
export function request(socket, { method, path, query, headers, body }) {
  return new Promise((resolve, reject) => {
    let p = path;
    if (query && Object.keys(query).length) p += "?" + new URLSearchParams(Object.entries(query).map(([k, v]) => [k, String(v)])).toString();
    let payload = null;
    const h = { ...(headers || {}) };
    if (body !== null && body !== undefined) {
      payload = Buffer.isBuffer(body) ? body : typeof body === "string" ? Buffer.from(body) : Buffer.from(JSON.stringify(body));
      if (!Object.keys(h).some((k) => k.toLowerCase() === "content-type")) h["Content-Type"] = "application/json";
      h["Content-Length"] = String(payload.length);
    }
    const req = http.request({ socketPath: socket, method, path: p, headers: h, agent: false }, (res) => {
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => {
        const raw = Buffer.concat(chunks);
        let json = null;
        try { json = JSON.parse(raw.toString("utf8")); } catch { /* not JSON */ }
        resolve({ status: res.statusCode, headers: res.headers, raw, json });
      });
    });
    req.on("error", reject);
    req.setTimeout(35000, () => req.destroy(new Error("request timeout")));
    if (payload) req.write(payload);
    req.end();
  });
}
