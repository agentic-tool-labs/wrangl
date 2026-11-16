# K5: the daemon lifecycle protocol

What the Node implementation does today, from the code, with `file:line` for each statement (paths relative to
`agent-hierarchy/`, at the baseline this contract was recorded from). Native must reproduce it. The scenario scripts in
`lifecycle/` check the behaviours marked **[scenario n]**. Where a statement has no scenario it is pinned only by this text.

## The socket

- The path is `<AH_HOME>/run/ahd.sock` (`hooks/ahd.mjs:90`, `hooks/lib-ahd-client.mjs:39`).
- Its mode is 0600: the daemon sets `umask(0o177)` before `listen` and restores the old mask in the listen callback
  (`hooks/ahd.mjs:1021`, `:1026-1028`). `run/ahd.json` is created under the same mask.
- The byte length of the path may not exceed 103 (`MAX_SOCKET_PATH`, `hooks/ahd.mjs:43`, `hooks/lib-ahd-client.mjs:22`).
  **[scenario 5]**
  - The daemon logs `socket path <path> is longer than 103 bytes; refusing to start` and exits 1
    (`hooks/ahd.mjs:92-94`).
  - The client warns `socket path is <n> bytes; the limit is 103` and returns null, so the caller uses the files
    (`hooks/lib-ahd-client.mjs:230-233`).
- The daemon never unlinks the socket on its own. `listen` on an existing path fails with `EADDRINUSE`, and the daemon exits.
  The client unlinks a stale socket under `ahd.lock` (`hooks/lib-ahd-client.mjs:193`). **[scenario 7]**

## `run/ahd.json`

- Content: `{v: 1, pid, version, protocol: 1, socket, root, started_at}` (`hooks/ahd.mjs:1031`). Read by `discover`
  (`hooks/lib-ahd-client.mjs:103-114`).
- The path is `<AH_HOME>/run/ahd.json` (`hooks/ahd.mjs:91`).

## The start lock, `run/ahd.lock`

The `lib-lock` link protocol (`hooks/lib-lock.mjs`). Every lock in the plugin uses it, with the parameters of the table
below.

- **Content:** `{pid, at, nonce}` in that key order (`:71`).
- **Taking it:** a tmp file `<lock>.<pid>.<rand>` is written with the content and `linkSync`ed to the lock path; `EEXIST` means
  held (`:72-79`). On success, `<lock>.stale.*` files older than 1 h are swept (`:26`, `:47-63`, `:76`).
- **Fresh:** a lock whose `at` is less than 10,000 ms old is fresh, whatever its pid (`LOCK_FRESH_MS`, `:23`; `:87`).
  The taker returns `{held: false, fresh: true}`. **[scenario 2, 9]**
- **Stale:** the lock is renamed to `<lock>.stale.<pid>.<rand>` (`:88-90`), its content is compared with what was judged
  (`:95`). Equal: the aside file is removed. Different: it is linked back and the taker goes round again (`:97-105`).
- **Rounds:** at most 6 (`MAX_ROUNDS`, `:27`, `:70`).
- **Waiting:** `withLock` retries `takeLock` with a 5 ms pause until `waitMs`, default 5,000 (`LOCK_WAIT_MS`, `:25`,
  `:119-132`). It returns null, and does not run the function, when the lock never cleared.
- **Release:** only when the file's content equals the taker's own `{pid, at, nonce}` (`:111-113`).

| Lock | Path | Stale | Wait | Taken at |
|---|---|---|---|---|
| start lock | `<AH_HOME>/run/ahd.lock` | 10 s | the start's own 3 s poll (`START_WAIT_MS`, `lib-ahd-client.mjs:25`) | `lib-ahd-client.mjs:251` |
| domain lock | `<dir>/.domain.lock` (`LOCK_NAME`, `lib-state.mjs:53`; `:709`) | 10 s | `LOCK_WAIT_MS` plus the sync slack (`lib-state.mjs:876-881`) | `lib-state.mjs` write path. Native: every state domain's writes are database transactions and take no file lock; only a files-path op would lock, at `<AH_HOME>/state/locks/<domain>-<dir key>-.domain.lock` (`StateLocks.cs`, pinned by `FilesPathLockTests`). Scenario 9 (`09-domain-lock.sh`) drove `.state-activity/.domain.lock`, which no longer exists, and was retired. |
| mailbox append lock | `<box>/append.lock` | 10 s | `waitMs` argument, default `LOCK_WAIT_MS` (`lib-mailbox.mjs:314-319`, `:387`, `:628`) | `lib-mailbox.mjs:319` |
| paste lock | `<AH_HOME>/run/paste/<team>-<member>.lock` (`lib-paste.mjs:35`) | 60 s (`PASTE_LOCK_STALE_MS`, `:37`) | `AH_PASTE_LOCK_WAIT_MS`, default 15 s (`:31`, `:38`) | `lib-paste.mjs:37` |

- **`lockOptional`.** An append whose registry row sets `lockOptional: true` proceeds without the lock when it cannot be taken
  within the wait (`hooks/lib-state.mjs:64`, `:120`, `:180`, `:201`, `:279`, `:355`, `:529`; `:746`). The registry refuses
  the flag on anything but an unpreconditioned append (`:562`).

## Starting the daemon (`ensureDaemon`, `hooks/lib-ahd-client.mjs:222-294`)

1. `AH_DAEMON=off` (exactly) disables discovery and start altogether (`:32`, `:225`).
2. A socket path over 103 bytes ends in null with the warning above (`:230-233`).
3. `discover` reads `ahd.json`, sends `GET /v1/health` with a 500 ms timeout (`DISCOVER_TIMEOUT_MS`, `:23`, `:111`) and
   requires the health `pid` to equal the info's `pid`. A 503 `draining` returns `{draining: true, pid}` (`:112`).
   A draining daemon is waited for (`DRAIN_WAIT_MS` 5,000, `:26`, `:242`); no replacement starts while its pid lives (`:188`).
4. With no daemon and `start` allowed: `run/` is created 0700 (`:249-250`), the start lock is taken (`:251`).
   - Held: `startLocked` re-discovers under the lock, signals a hung daemon (`:172`), unlinks a stale socket (`:193`),
     rotates `ahd.log` past 5 MB (`LOG_ROTATE_BYTES`, `:27`, `:154-156`, `:198`), spawns
     `node ahd.mjs --ah-home <home>` detached (`:204`), and polls for up to 3,000 ms (`START_WAIT_MS`, `:25`) for an
     `ahd.json` whose pid is the child's (`:212`).
   - Fresh lock held by another process: the caller polls discovery for up to 3,000 ms (`:258-265`).
5. The daemon's protocols must include 1 (`:271-275`).

## The handshake (`compareVersions`, `hooks/lib-ahd-client.mjs:43-51`; use at `:276-291`)

- The comparison splits both versions on `.` and `-` and compares the numeric parts left to right; a missing part is 0.
- **A client that is not newer** (`cmp <= 0`) accepts the daemon (`:277`). **[scenario 4]**
- **A newer hook** sends a drain and does not wait, warns, and falls back to the files (`:282-287`).
- **A newer non-hook client** drains it, waits up to 5,000 ms, and goes round again to start the new one (`:288-291`).
  A second round that still finds an older daemon ends in null (`:288-289`). **[scenario 4]**
- A daemon that does not list protocol 1 is refused (`:271-275`).
- `AHD_VERSION_OVERRIDE` replaces the version only under `AH_TEST=1`, for the client (`:35`) and for the daemon
  (`hooks/ahd.mjs:50`).
- `ahd-ctl start` reports a running daemon as it is and does not go through the comparison unless it is asked to upgrade
  (`hooks/lib-ahd-ctl.mjs:78-86`); a verb that reaches the daemon through `request` does.

## Fencing

- Every second the daemon checks that `ahd.json` names its pid and that its socket file is the inode it bound
  (`hooks/ahd.mjs:219`, `:990-1003`, `:1047`). If either check fails it exits without removing anything. **[scenario 3]**
- The same check runs during shutdown before it removes `ahd.json` and the socket (`:977`, `:984`).

## Idle exit

- After `AHD_IDLE_EXIT_MS` (default 30 min, `hooks/ahd.mjs:55`) with no request, no timer and no live registration,
  checked every second, the daemon exits (`:1048`). **[scenario 6]**

## Shutdown

- SIGTERM and SIGINT call `shutdown(0)` (`hooks/ahd.mjs:1007-1008`). The daemon refuses new requests with 503 `draining`,
  finishes the ones in flight for at most 5,000 ms (`DRAIN_HARD_MS`, `:48`, `:980`), then removes the socket and `ahd.json`
  only if it still owns them (`:971-990`).
- `POST /v1/drain` does the same (`:719-723`).

## The writer stamp (0100 A1; arrives with W2)

- The file is `<AH_HOME>/state-writer.json`; a missing file means `node`. The daemon's runtime equals `stamp.writer`, and
  `ensureDaemon` drains a daemon whose `health.impl` differs. **Not present in this baseline: [scenario 8], pending-W2.**

## The hook entry point and the trust gates **[scenario 14]**

The commands of `native/cutover-hooks.json` (the `hooks.json` the flag-day commit installs) are the real entry point: `sh
"${CLAUDE_PLUGIN_ROOT}/bin/ah-hook" <unit>` execs the binary's `hook <unit>`. Scenario 14 runs those commands verbatim with a
PreToolUse payload on stdin: `ahd home trust` and `untrust` (any permission mode) and a command naming a trust file are denied by
the Bash chain, a neighbouring name is not, and a Write to a trust file is denied by the Write chain for a session with no role.
The trust gate has no Node script, so the template is the only route to it.
