// Runs tests/contracts/home-predicate.json through the sh predicate (bin/ah-testmode.sh): one row per input, a fresh
// sandbox per row, exit 0 iff every row gives its expected answer. The C# predicate runs the same table
// (Ah.Platform.Tests HomePredicateTableTests).
import { spawnSync } from "node:child_process";
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, rmdirSync, symlinkSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { randomBytes } from "node:crypto";

const here = dirname(fileURLToPath(import.meta.url));
const lib = join(here, "..", "..", "bin", "ah-testmode.sh");
const table = JSON.parse(readFileSync(join(here, "..", "contracts", "home-predicate.json"), "utf8"));
let failed = 0;

for (const c of table.cases) {
  const root = mkdtempSync("/tmp/ahprobe.");
  chmodSync(root, 0o700);
  const out = mkdtempSync("/var/tmp/outside.");
  const rand = randomBytes(4).toString("hex");
  const sub = (s) => (s === null ? null : s.replaceAll("$ROOT", root).replaceAll("$OUT", out).replaceAll("$RAND", rand));
  const made = [];
  try {
    for (const op of c.ops ?? []) {
      const path = sub(op.path);
      if (op.op === "mkdir") {
        mkdirSync(path, { recursive: true });
        if (path.startsWith("/tmp/")) made.push({ path, dir: true });
      } else if (op.op === "touch") {
        writeFileSync(path, "");
        made.push({ path, dir: false });
      } else if (op.op === "symlink") {
        symlinkSync(sub(op.target), path);
        if (path.startsWith("/tmp/")) made.push({ path, dir: false });
      } else if (op.op === "chmod") {
        chmodSync(path, parseInt(op.mode, 8));
      }
    }

    const env = { PATH: process.env.PATH };
    if (c.ah_test !== null) env.AH_TEST = c.ah_test;
    if (c.ah_home !== null) env.AH_HOME = sub(c.ah_home);
    const r = spawnSync("sh", ["-c", '. "$1"; ah_test_mode', "sh", lib], { env, encoding: "utf8", cwd: c.cwd ? sub(c.cwd) : undefined, timeout: 60000 });
    const got = r.status === 0;
    if (got !== c.expect) {
      failed++;
      console.log(`FAIL ${c.id}: expected ${c.expect}, sh said ${got} (exit ${r.status}) ${r.stderr.trim()}`);
    } else {
      console.log(`ok   ${c.id}`);
    }
  } finally {
    for (const m of made) {
      try {
        if (m.dir) rmdirSync(m.path);
        else rmSync(m.path);
      } catch {
        // already gone
      }
    }
    chmodSync(root, 0o700);
    rmSync(root, { recursive: true, force: true });
    rmSync(out, { recursive: true, force: true });
  }
}

process.exit(failed === 0 ? 0 : 1);
