#!/usr/bin/env node
/**
 * A raw HTTP client for the 413 drain (tests/test-state-oneshot-caps.sh): `node tests/w2-raw.mjs <socket> <path> <declared> <sent> [headers-only]`.
 * Posts `declared` as Content-Length and writes `sent` body bytes (all at once, unless `headers-only`), reads whatever
 * the daemon answers, and prints one JSON line: {status, connection, writeErr, written, closed, ms, bodyHead}.
 * `writeErr` is the error name when the client's own write failed (EPIPE, ECONNRESET): the failure the drain exists to prevent.
 */

import net from "node:net";

const [, , sock, path, declared, sent, mode] = process.argv;
const t0 = Date.now();
const out = { status: null, connection: null, writeErr: null, written: 0, closed: false, ms: null, bodyHead: null };
let raw = "";
let finished = false;
const finish = () => {
  if (finished) return;
  finished = true;
  out.ms = Date.now() - t0;
  process.stdout.write(JSON.stringify(out) + "\n");
  process.exit(0);
};
const c = net.connect(sock);
c.on("connect", () => {
  c.write(`POST ${path} HTTP/1.1\r\nHost: ahd\r\nContent-Type: application/json\r\nContent-Length: ${declared}\r\nConnection: close\r\n\r\n`);
  if (mode === "headers-only") return;
  const body = Buffer.alloc(Number(sent), 0x78);
  c.write(body, (err) => {
    if (err) out.writeErr = err.code || err.message;
    else out.written = body.length;
  });
});
c.on("data", (d) => {
  raw += d.toString("utf8");
  const m = /^HTTP\/1\.1 (\d+)/.exec(raw);
  if (m && out.status === null) {
    out.status = Number(m[1]);
    const conn = /\r\nconnection: ([^\r]+)/i.exec(raw);
    out.connection = conn ? conn[1].toLowerCase() : null;
  }
  const end = raw.indexOf("\r\n\r\n");
  if (end >= 0) out.bodyHead = raw.slice(end + 4, end + 204);
  if (mode === "headers-only" && out.status !== null) {
    // the answer came with no body sent: that is the point of the case
    setTimeout(finish, 50);
  }
});
c.on("error", (err) => {
  if (!out.writeErr) out.writeErr = err.code || err.message;
});
c.on("close", () => {
  out.closed = true;
  setTimeout(finish, 20);
});
setTimeout(finish, 6000);
