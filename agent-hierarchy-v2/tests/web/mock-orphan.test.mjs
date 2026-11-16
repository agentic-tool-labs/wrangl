import { test } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

// The mock must end with the process that started it: a harness that crashes after starting it leaves it re-parented, and it
// then ends by itself.

const SERVE = fileURLToPath(new URL("../../web/mock/serve.mjs", import.meta.url));
const alive = (pid) => { try { process.kill(pid, 0); return true; } catch { return false; } };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// runs a parent that starts the mock (detached from our stdio), reports its pid and then dies with an exception
async function startAndAbandon(extraParentCode) {
  const code = `import {spawn} from "node:child_process";
    const c = spawn(process.execPath, [${JSON.stringify(SERVE)}, "--port", "0"], {stdio: "ignore"});
    console.log(c.pid);
    ${extraParentCode}`;
  const parent = spawn(process.execPath, ["--input-type=module", "-e", code], { stdio: ["ignore", "pipe", "ignore"] });
  let out = "";
  parent.stdout.on("data", (d) => { out += d; });
  await new Promise((r) => parent.on("close", r));
  return Number(out.trim());
}

test("a mock whose parent crashed ends by itself", async () => {
  const pid = await startAndAbandon(`setTimeout(() => { throw new Error("boom"); }, 1200);`);
  assert.ok(pid > 1);
  try {
    let gone = false;
    for (let i = 0; i < 40 && !gone; i++) { await sleep(250); gone = !alive(pid); }
    assert.ok(gone, "the mock is still running after its parent died");
  } finally {
    if (alive(pid)) process.kill(pid, "SIGKILL");
  }
});

test("a mock whose parent is still running keeps running (nearest non-match)", async () => {
  const parent = spawn(process.execPath, ["--input-type=module", "-e", `import {spawn} from "node:child_process";
    const c = spawn(process.execPath, [${JSON.stringify(SERVE)}, "--port", "0"], {stdio: "ignore"});
    console.log(c.pid); setInterval(() => {}, 1000);`], { stdio: ["ignore", "pipe", "ignore"] });
  let out = "";
  parent.stdout.on("data", (d) => { out += d; });
  try {
    for (let i = 0; i < 40 && !out.includes("\n"); i++) await sleep(100);
    const pid = Number(out.trim());
    await sleep(2500);
    assert.ok(alive(pid), "the mock ended while its parent was running");
    process.kill(pid, "SIGTERM");
  } finally {
    parent.kill("SIGKILL");
  }
});
