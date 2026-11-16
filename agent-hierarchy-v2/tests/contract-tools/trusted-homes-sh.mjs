// Runs the trusted_homes section of tests/contracts/home-predicate.json through the sh state-home verdict (bin/ah-trust.sh over
// bin/ah-testmode.sh): one row per input, a fresh sandbox per row, exit 0 iff every row gives its expected answer. The C# verdict
// runs the same rows (Ah.Cli.Tests TrustedHomesTableTests).
import { spawnSync } from "node:child_process";
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { randomBytes } from "node:crypto";

const here = dirname(fileURLToPath(import.meta.url));
const testmode = join(here, "..", "..", "bin", "ah-testmode.sh");
const trust = join(here, "..", "..", "bin", "ah-trust.sh");
const table = JSON.parse(readFileSync(join(here, "..", "contracts", "home-predicate.json"), "utf8"));
let failed = 0;

for (const c of table.trusted_homes) {
  const root = mkdtempSync("/tmp/ahprobe.");
  chmodSync(root, 0o700);
  const out = mkdtempSync("/var/tmp/outside.");
  const rand = randomBytes(4).toString("hex");
  const real = (s) => s.replaceAll("$ROOT", realpathSync(root)).replaceAll("$OUT", realpathSync(out)).replaceAll("$RAND", rand);
  const sub = (s) => (s === null || s === undefined ? s : s.replaceAll("$ROOT", root).replaceAll("$OUT", out).replaceAll("$RAND", rand));
  try {
    mkdirSync(join(root, "pw"), { recursive: true });
    for (const op of c.ops ?? []) {
      const path = sub(op.path);
      if (op.op === "mkdir") {
        mkdirSync(path, { recursive: true });
      } else if (op.op === "write") {
        mkdirSync(dirname(path), { recursive: true });
        let content = sub(op.content);
        if (op.pad_to) {
          while (Buffer.byteLength(content) < op.pad_to) content += "#\n";
          content = content.slice(0, op.pad_to);
        }
        writeFileSync(path, content);
      } else if (op.op === "symlink") {
        symlinkSync(sub(op.target), path);
      } else if (op.op === "chmod") {
        chmodSync(path, parseInt(op.mode, 8));
      } else {
        throw new Error(`unknown op ${op.op}`);
      }
    }

    const env = { PATH: process.env.PATH };
    if (c.ah_test !== null && c.ah_test !== undefined) env.AH_TEST = c.ah_test;
    if (c.ah_home !== null && c.ah_home !== undefined) env.AH_HOME = sub(c.ah_home);
    const r = spawnSync("sh", ["-c", '. "$1"; . "$2"; ah_home_verdict "$3"', "sh", testmode, trust, join(root, "pw")], { env, encoding: "utf8", timeout: 60000, cwd: c.cwd ? sub(c.cwd) : undefined });
    const accepted = r.status === 0;
    const problems = [];
    if (accepted !== c.expect.accepted) problems.push(`accepted ${accepted} (exit ${r.status}) ${r.stdout.trim()}`);
    else if (accepted && c.expect.home !== undefined && !c.expect.test_mode && r.stdout !== real(c.expect.home)) problems.push(`home ${r.stdout} (wanted ${real(c.expect.home)})`);
    else if (!accepted && c.expect.reason !== undefined && r.stdout !== c.expect.reason) problems.push(`reason "${r.stdout}"`);
    if (problems.length) {
      failed++;
      console.log(`FAIL ${c.id}: ${problems.join("; ")}`);
    } else {
      console.log(`ok   ${c.id}`);
    }
  } catch (e) {
    failed++;
    console.log(`FAIL ${c.id}: ${e.message}`);
  } finally {
    chmodSync(root, 0o700);
    rmSync(root, { recursive: true, force: true });
    rmSync(out, { recursive: true, force: true });
  }
}

process.exit(failed === 0 ? 0 : 1);
