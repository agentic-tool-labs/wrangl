#!/usr/bin/env node
// Probe E6r-2: a unix socket listener that records every connection it accepts, so a connect the model's sandbox should have
// refused shows up as a line. Stays up until it is killed.
//
//   node tests/probe-e6r2-listener.mjs <socket path> <log file>

import fs from "node:fs";
import net from "node:net";

const [sockPath, logPath] = process.argv.slice(2);
fs.rmSync(sockPath, { force: true });
const server = net.createServer((c) => {
  fs.appendFileSync(logPath, `connection ${Date.now()}\n`);
  c.end();
});
server.listen(sockPath, () => fs.writeFileSync(logPath + ".ready", "\n"));
