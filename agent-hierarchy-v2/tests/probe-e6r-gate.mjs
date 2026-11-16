#!/usr/bin/env node
// The E6r-1 safety gate (R09 r9/r10), used by tests/probe-e6r-client.mjs: the effective mode of the thread as the app-server
// reports it. Pass only for sandbox readOnly, network off (networkAccess exactly false) and approval never. Every clause is its
// own entry so that each one can be mutated on its own.
//
//   node tests/probe-e6r-gate.mjs     self-check, no codex needed: the gate on synthetic reports, and one mutant per clause
//                                     (plus a weakened network check) that the synthetic reports must kill. Exit 0 iff all hold.

export const CLAUSES = {
  sandbox: (f) => Boolean(f.sandbox && f.sandbox.type === "readOnly"),
  network: (f) => Boolean(f.sandbox && f.sandbox.networkAccess === false),
  approval: (f) => f.approvalPolicy === "never",
};

export function gateOf(fields, clauses = CLAUSES) {
  const f = fields && typeof fields === "object" ? fields : {};
  const failed = Object.entries(clauses)
    .filter(([, fn]) => !fn(f))
    .map(([name]) => name);
  return {
    pass: failed.length === 0,
    failed,
    sandbox: f.sandbox === undefined ? null : f.sandbox,
    approvalPolicy: f.approvalPolicy === undefined ? null : f.approvalPolicy,
  };
}

// Synthetic reports: the nearest inputs that must not pass, each failing one clause, plus the one that passes.
export const SYNTHETIC = {
  good: { sandbox: { type: "readOnly", networkAccess: false }, approvalPolicy: "never" },
  network_on: { sandbox: { type: "readOnly", networkAccess: true }, approvalPolicy: "never" },
  network_missing: { sandbox: { type: "readOnly" }, approvalPolicy: "never" },
  workspace_write: { sandbox: { type: "workspaceWrite", networkAccess: false }, approvalPolicy: "never" },
  danger_full_access: { sandbox: { type: "dangerFullAccess" }, approvalPolicy: "never" },
  approval_on_request: { sandbox: { type: "readOnly", networkAccess: false }, approvalPolicy: "on-request" },
  approval_missing: { sandbox: { type: "readOnly", networkAccess: false } },
  no_sandbox: { approvalPolicy: "never" },
  empty: {},
};

const MUTANTS = {
  "drop sandbox clause": { ...CLAUSES, sandbox: () => true },
  "drop network clause": { ...CLAUSES, network: () => true },
  "network clause: not-true instead of exactly-false": {
    ...CLAUSES,
    network: (f) => Boolean(f.sandbox) && f.sandbox.networkAccess !== true,
  },
  "drop approval clause": { ...CLAUSES, approval: () => true },
  "drop all clauses (vacuous gate)": {},
};

export function selfcheck() {
  const out = { gate: {}, mutants: {}, ok: true };
  for (const [name, fields] of Object.entries(SYNTHETIC)) {
    const r = gateOf(fields);
    const expected = name === "good";
    out.gate[name] = { pass: r.pass, failed: r.failed, as_required: r.pass === expected };
    if (r.pass !== expected) out.ok = false;
  }
  for (const [name, clauses] of Object.entries(MUTANTS)) {
    const killedBy = Object.entries(SYNTHETIC)
      .filter(([, fields]) => gateOf(fields, clauses).pass !== gateOf(fields).pass)
      .map(([input]) => input);
    out.mutants[name] = { killed_by: killedBy, killed: killedBy.length > 0 };
    if (killedBy.length === 0) out.ok = false;
  }
  return out;
}

import { realpathSync } from "node:fs";
import { fileURLToPath } from "node:url";
if (process.argv[1] && fileURLToPath(import.meta.url) === realpathSync(process.argv[1])) {
  const r = selfcheck();
  process.stdout.write(JSON.stringify(r, null, 2) + "\n");
  process.exit(r.ok ? 0 : 1);
}
