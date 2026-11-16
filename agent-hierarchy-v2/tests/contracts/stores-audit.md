# Stores audit

Every hit of `writeFileSync|appendFileSync|renameSync|atomicWriteJson|openSync(.*"a"|linkSync|writeTempExclusive` under
`hooks/*.mjs` at the recorded baseline, with its disposition: the store of `stores.json` it writes, or `not-a-store` with
the reason. A hit that is an import line or a removal (`unlinkSync`) belongs to the writer it supports. The grep was run as:

    grep -rnE 'writeFileSync|appendFileSync|renameSync|atomicWriteJson|openSync\(.*"a"|linkSync|writeTempExclusive' hooks --include='*.mjs'

111 hits in 21 files. Lines are `file:line`.

| File | Lines | Disposition |
|---|---|---|
| `lib-ahd-ctl.mjs` | 15 (import), 225, 238, 324 | **not-a-store**: the `check` verb seeds a team file and a `status.json` inside its own throwaway sandbox (`mkdtemp`); nothing outside it is touched |
| `lib-ahd-conform.mjs` | 12 (import), 80, 161, 269 | **not-a-store**: the `conform` verb's sandbox: a seeded team file, the child harness's log, and a message file it makes to test the harness |
| `lib-ahd-client.mjs` | 13 (import), 156, 193, 199, 346, 350 | `daemon-log` (156 rotate, 199 open append), `daemon-socket` (193 unlink of a stale socket), `fallback-log` (346 rotate, 350 append) |
| `ahd.mjs` | 16 (import), 524, 961, 1031, 1032 | 524 **not-a-store**: a test-only build log (`AH_TEST=1` and `AHD_VIEW_BUILD_LOG`); 961 and 1031-1032 `daemon-info` (unlink at shutdown, tmp-and-rename write) |
| `lib-lock.mjs` | 20 (import), 41, 73, 75, 90, 100 | `daemon-lock`, and every lock of the plugin: the tmp file, the `link`, the aside rename and the put-back |
| `lib-mailbox.mjs` | 29 (import), 122, 133, 324, 353, 499, 500, 531 | `registrations` (122, 133), `mailbox-entries` (324), `mailbox-attempts` (353), `mailbox-adapters` (499-500), `mailbox-acks` (531) |
| `lib-state.mjs` | 25 (import), 326, 327, 667, 671, 672, 958 | 326-327 **not-a-store**: string literals of the static check's allow list, not writes; `executed-ops` (667 append, 671-672 prune rewrite, 958 row append) |
| `lib-roster.mjs` | 16 (import), 1044-1062, 1069-1076, 1093, 1117, 1148, 1617 | `team` (atomicWriteJson at 1044-1062, writeTeamFile 1076, clearTeam 1093, team.write 1117), `team-snapshots` (1148), `team-history` (1617) |
| `lib-status.mjs` | 21 (import), 96-102, 180-181, 195, 213, 529-530 | `activity` (180-181 write, 195 and 213 remove), `status` (529-530); 96-102 is the tmp-exclusive helper both use. `session-pids` is written by `appendSessionPid` (438-446), whose append goes through `appendJsonl` in `lib-hier.mjs:108-111`, so it is the same write as `lib-hier.mjs:110` |
| `lib-hier.mjs` | 21 (import), 73, 110, 470-476, 1096, 1102-1103, 1109 | 73 `hierarchy-gitignore`; 110 `peers`, `gates` and `session-pids` (one append helper, `appendJsonl`); `msgs` (470-476 replace, 1096 create-exclusive, 1102-1103 settle, 1109 archive rename) |
| `lib-decisions.mjs` | 9 (import), 245 | `decisions` |
| `lib-peer.mjs` | 36 (import), 253 | `peer-pending` |
| `lib-usage.mjs` | 13 (import), 96, 97, 110 | `usage` (110 append, 96-97 prune by tmp-and-rename) |
| `lib-gate.mjs` | 19 (import), 94, 95 | `ultra-gate` |
| `lib-session-role.mjs` | 20 (import), 56, 57 | `session-roles` |
| `lib-links.mjs` | 21 (import), 70, 71 | `links` |
| `lib-config.mjs` | 29 (import), 117, 119, 2065 | `hook-errors` (117 rotate, 119 append); `trusted-role` (2065) |
| `lib-paste.mjs` | 12 (import), 129, 138 | `paste-payload` (129 write mode 0600, 138 remove) |
| `lib-mux.mjs` | 14 (import), 61 | **not-a-store**: a tripwire log written only when the test variable `AH_TEST_TRIPWIRE` names a file |
| `usage-report.mjs` | 32 (import), 98, 99 | `usage-cache` |
| `issue-intake.mjs` | 13 (import), 402 | `issue-snapshot` |
| `roster.mjs` | 161 (import), 539, 544, 797, 798, 1207, 2174, 2708, 4121 | 539, 544 `role-scaffold`; 797-798 **not-a-store**: two temporary files a diff is computed from; 1207 `level-file` (removal of an emptied file), 2174 `level-file`; 2708 **not-a-store**: unlink of a launch-time socket in a per-member temp directory; 4121 `instructions` |

## Stores with no sample in this wave, and why

- `registrations`, `mailbox-adapters`, `mailbox-entries`, `mailbox-attempts`, `mailbox-acks`: written only by a daemon that
  has a registered session and a box; wave 0's sample generator runs the CLIs and the state-call driver and does not host
  one. SS-2 (0103) turns these into hub ops and adds their K8 cases and samples in the same format.
- `team-snapshots`, `decisions`, `trusted-role`, `instructions`, `role-scaffold`, `issue-snapshot`, `hook-errors`,
  `fallback-log`, `usage-cache`, `hierarchy-gitignore`: each needs a launch, a role pack, a failing hook or a skill run, or is
  free text; none has a sample yet.
- The `L` stores (`daemon-*`, `paste-payload`) are pinned by `lifecycle.md` and the K5 scenarios, not by samples.

## Facts the audit recorded

- Four tmp-name forms are in use, as 0100 A4 says: `.${pid}.tmp` appended to the target (links, status, lib-hier settle, usage,
  usage-cache, executed-ops, daemon-info), `.tmp-${pid}` appended (ultra-gate, session-roles),
  `.${pid}.${hex}.tmp` appended (team files, snapshots, history), and `.${basename}.${pid}.tmp` in the target's directory
  (a replaced message). The adapter heartbeat uses `${name}.json.${pid}.tmp`, a fifth form: it is the target name plus the
  first form, written in the `adapters/` directory.
- `JSON.stringify` indents measured from the samples: team, team-history, level files, ultra-gate and session-roles use 2;
  links uses 1; status, activity, every JSONL row and the executed-ops log are compact; every one ends with a newline.
- A level file is written directly with `writeFileSync` (no tmp and no rename); a crash mid-write can leave it torn.
  Recorded as it is.
- Nothing fsyncs (0100 A9).
