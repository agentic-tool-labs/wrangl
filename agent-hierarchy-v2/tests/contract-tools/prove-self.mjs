#!/usr/bin/env node
// Records one process as a provable member of a team, the way a real session is: a live registration under AH_HOME carrying the
// process's pid and its pid_start, and, for the Orchestrator, the same pid and pid_start in the team. Both go through `ah db test-seed`,
// so they land where the home's store is. The start time comes from lib-case.mjs, the one formatter the contract runner uses for <PIDSTART:runner>.
//   prove-self.mjs <name> <team> <pid> <ah home> <team file or ""> <box dir> [<session id>]
// Prints the session id the registration is under (proof-<pid> unless one is given).
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, realpathSync, writeFileSync } from "node:fs";
import { basename, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { processStart } from "./lib-case.mjs";

const [name, team, pidText, ahHome, teamFile, boxDir, sessionArg] = process.argv.slice(2);
if (!name || !team || !/^[0-9]+$/.test(pidText ?? "") || !ahHome || boxDir === undefined) {
  console.error("usage: prove-self.mjs <name> <team> <pid> <ah home> <team file or \"\"> <box dir> [<session id>]");
  process.exit(2);
}

const plugin = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const env = { ...process.env, AH_HOME: ahHome };

function run(bin, args, input) {
  const done = spawnSync("sh", [join(plugin, "bin", bin), ...args], { input, env, encoding: "utf8" });
  if (done.status !== 0) {
    console.error(`prove-self.mjs: ${bin} ${args.join(" ")}: ${done.stderr}`);
    process.exit(1);
  }
  return done.stdout;
}

function seed(args, doc) {
  run("ah", ["db", "test-seed", ...args], JSON.stringify(doc));
}

// a team a suite planted as a file is taken from it; one already stored is read back from the store
function teamDocument(path) {
  if (existsSync(path)) {
    return JSON.parse(readFileSync(path, "utf8"));
  }
  const legacy = basename(path) === "team.json" && basename(dirname(path)) !== "teams";
  const hierarchy = realpathSync(legacy ? dirname(path) : dirname(dirname(path))).replaceAll("'", "''");
  const team = legacy ? "team.json" : basename(path, ".json").replaceAll("'", "''");
  const read = spawnSync("sqlite3", ["-cmd", ".timeout 5000", "-readonly", join(ahHome, "state", "ah.db"), `SELECT doc FROM teams WHERE hier_dir = '${hierarchy}' AND team = '${team}'`], { encoding: "utf8" });
  return read.status === 0 && read.stdout.trim() ? JSON.parse(read.stdout) : null;
}

const pid = Number(pidText);
const pidStart = processStart(pid);
const session = sessionArg || `proof-${pid}`;
const address = `${name}@${team}`;

if (teamFile && name === "orchestrator") {
  const doc = teamDocument(teamFile);
  if (doc?.orchestrator && typeof doc.orchestrator === "object") {
    doc.orchestrator = { ...doc.orchestrator, pid, pid_start: pidStart };
    seed(["teams", "--path", teamFile], doc);
  }
}

// one process proves several teams: the registration keeps every box and its file; register replaces, so what it held is kept here
const heldDir = join(ahHome, "state", "prove-self");
mkdirSync(heldDir, { recursive: true });
const heldFile = join(heldDir, `${session}.json`);
let held = null;
try { held = JSON.parse(readFileSync(heldFile, "utf8")); } catch { held = null; }
const boxes = { ...(held?.boxes ?? {}), [address]: boxDir };
const teamFiles = { ...(held?.team_files ?? {}), ...(teamFile ? { [address]: teamFile } : {}) };
const firstTeamFile = held?.team_file ?? (teamFile || null);
writeFileSync(heldFile, JSON.stringify({ boxes, team_files: teamFiles, team_file: firstTeamFile }) + "\n");
seed(["sessions"], {
  session_id: session,
  boxes: Object.entries(boxes).map(([boxAddress, dir]) => ({ address: boxAddress, dir })),
  team_file: firstTeamFile,
  ...(Object.keys(teamFiles).length ? { team_files: teamFiles } : {}),
  harness: null,
  pid,
  pane_id: null,
  pid_start: pidStart,
  bound: false,
});
process.stdout.write(session);
