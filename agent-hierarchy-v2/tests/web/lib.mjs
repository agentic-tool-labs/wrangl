import { readFileSync, writeFileSync, existsSync, mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import assert from "node:assert/strict";

process.env.TZ = "UTC";

const here = dirname(fileURLToPath(import.meta.url));
export const WEB = join(here, "..", "..", "web", "ui");
export const NOW = Date.parse("2026-10-17T06:29:30Z");

export const fixture = (name) => JSON.parse(readFileSync(join(here, "fixtures", name), "utf8"));

// A document the C# tests commit, read where it is (the tree documents come from there, not from a copy).
export const served = (name) => JSON.parse(readFileSync(join(here, "..", "..", "native", "tests", "Ah.Watch.Tests", "Goldens", name), "utf8"));

// A detail document around a normative fixture, which holds the document's body. Each call is a fresh copy to edit.
export const detail = (name) => ({ body: fixture(`detail-orchestrator-${name}.json`) });
export const top = (doc, key) => doc.body.fields.find((f) => f.key === key);
export const sub = (doc, group, key) => top(doc, group).value.find((f) => f.key === key);

// A golden is a checked-in string. AH_WEB_GOLDEN=update rewrites it; otherwise a missing or different one fails.
export function golden(name, actual) {
  const file = join(here, "goldens", name);
  if (process.env.AH_WEB_GOLDEN === "update") {
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, `${actual}\n`);
    return;
  }
  assert.ok(existsSync(file), `missing golden ${name}; run with AH_WEB_GOLDEN=update`);
  assert.equal(`${actual}\n`, readFileSync(file, "utf8"), `golden ${name} differs`);
}

// One tag per line, so a golden diff reads as a diff of the markup.
export const pretty = (markup) => markup.replace(/></g, ">\n<");
