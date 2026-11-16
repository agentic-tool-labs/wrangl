# Contract harness

The recorded contract set (`tests/contracts/`) and the tooling that runs it (`tests/contract-tools/`). The tooling imports
nothing from `hooks/`.

## Running

```
node tests/contract-tools/run.mjs   [--impl native] [--kind hook|cli|wire|state-op|lifecycle] [--only GLOB] [--allow-skips]
AHD_NATIVE_BIN=<ahd> node tests/contract-tools/rerecord.mjs [--kind hook|cli|wire|state-op]... [--only GLOB]... [--write]
```

`rerecord.mjs` re-records stored expectations from the native binary after a deliberate product change made them stale. It
lists what would change; `--write` rewrites only the fields that differ. Read the diffs `run.mjs` prints first: re-recording a
defect pins the defect.

A case that carries a `mutant` (the wrong behaviour its expectation exists to catch) is not rewritten by `rerecord.mjs` unless
`--recheck-mutants` is given, because a changed expectation may no longer catch that mutant. With the flag the case is
re-recorded and listed at the end (`mutant re-run: <kind>/<id>`); re-run each listed mutant against the new expectation. A
keyed normalizer rule may bound the numbers it replaces with `min` and `max` (both exclusive); `minute-json` replaces minutes
since the epoch only, and its counterexample holds a seconds-scale value it must leave alone.

The pathless dispatch row older than a day, which the Stop liveness check skips (`PathlessRowMaxAgeMs` in
`StopOrchestratorLiveness.cs`), is pinned by the hook case `hooks/stop-orchestrator-liveness/h3-stale-dispatch-without-path-not-flagged`
(tree `hook-dispatch-stale-nopath`, a row `<NOW-25h>` old).

Every run happens under a fresh `/private/tmp/ahprobe.XXXXXX` (or `/tmp/ahprobe.XXXXXX`) sandbox; the tooling removes only
that path. The cases were first recorded from the Node implementation, which is no longer in the tree.

## What `--impl native` needs

- `AHD_NATIVE_BIN` names the native binary. Without it every case is skipped as `no-native-binary` and the run exits 1
  (`--allow-skips` makes a skip exit 0).
- The dispatch preamble finds the binary through `AH_BIN` (set from `AHD_NATIVE_BIN` under `AH_TEST=1`) and requires a sidecar
  `<binary>.contract` whose `contract` equals `native/cutover.json`'s.
- **The binary's `AhVersion` must equal the plugin version** (`.claude-plugin/plugin.json`). Recorded outputs carry the plugin
  version as `<VERSION>`; the normalizer replaces the plugin's own version string only, so a build with another version shows a
  difference in every CLI and hook output that prints it.
- A unit the binary does not have yet answers `ahd: unit <u> is not implemented natively` (exit 70, CLI) or the deny/silent
  outcome of a hook; the case is skipped as `unit-not-native`. Any other result is judged.

## The runtime under test in a recording

A health document holds `impl` (`node` or `native`). It is recorded as `<IMPL>`: the normalizer rule `impl-json` replaces a
`impl` value that equals the runtime under test. A runtime that answers the other value is left visible and fails against the
recording.

## Owner verbs

`create`, `spawn-one`, `spawn-ad-hoc`, `dismiss`, `disband`, `untrack`, `reap`, `adopt`, `stream-open`, `stream-done` and `checkpoint` refuse a
caller whose process ancestry holds no registered Orchestrator (`refused: owner-unproven`, exit 2). A case that needs one of
them to run proves its owner through that same walk, with no bypass in the product: its tree names the runner process as the
Orchestrator, with both halves the walk compares. `<PID:runner>` is the runner's pid and `<PIDSTART:runner>` its start time as
the daemon reads it (`ps -o lstart=` under the C locale and UTC). A team file records them as `orchestrator.pid` and
`orchestrator.pid_start` (trees `wire-tm-owner`, `cli-stream-owner`); a team-less case registers the runner in
`ah/registrations/<session>.json` with `pid` and `pid_start` (tree `cli-roster-owner`). `cli/roster/untrack/owner-unproven`
runs the same verb in a tree without that proof and records the refusal.

The refusal carries one of two proof values. `no-orchestrator` (the walk finds no registered session) is the one a contract
case can stage, so the lane has one refusal case. `unproven` (the process table cannot be read) cannot be staged from a
case; `RosterOwnerGuardTests.AnUnreadableProcessTableIsUnprovenAndNothingIsWritten` covers it through the owner-proof seam
with a fake that reports the table unreadable, and asserts the refusal and that nothing under the hierarchy directory changes.

## State-op cases

`state/cases/<domain>/<id>.json` (`kind: "state-op"`) are run two ways, each in its own copy of the tree, and must agree
with each other and with `expect`: over the route (`POST|GET /v1/state/<domain>/<op>`) and the one-shot verb
(`ahd state <domain> <op>`, body on stdin).
