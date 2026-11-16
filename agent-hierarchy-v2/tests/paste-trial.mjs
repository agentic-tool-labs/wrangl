// One paste through the shipped hardened-paste code, for the live probe (tests/probe-e4r.sh paste).
// Usage: node paste-trial.mjs <team file> <member name> <payload>
// Reads the member from the team file, so placement resolves the way the product resolves it.
import { readFileSync } from "node:fs";
import { paste } from "../hooks/lib-paste.mjs";

const [teamFile, name, payload] = process.argv.slice(2);
const team = JSON.parse(readFileSync(teamFile, "utf8"));
const member = team.members.find((m) => m.name === name);
if (!member) {
  console.error(`no member ${name} in ${teamFile}`);
  process.exit(1);
}
const res = await paste({ member, teamName: team.team_id || "probe", teamTransport: team.transport, payload });
process.stdout.write(JSON.stringify(res));
