# db-perf.py — state-DB perf harness

Measures ah2's message operations and DB-backed read routes with messages in ahd's state DB (`mode.msgs=db`):
CLI verbs as agents run them, the daemon's HTTP reads, raw SQL with `EXPLAIN QUERY PLAN`, scale, concurrent writers,
and cold start.

## Run

```
bash tests/publish-bin.sh --out "$TMPDIR/ahbin"       # or scripts/publish-native.sh (see below)
python3 tests/perf/db-perf.py --ahd "$TMPDIR/ahbin/ahd" --out "$TMPDIR/db-perf" \
    [--scales 1,11,53] [--runs 30] [--conc 4,8,16] [--per-writer 10] [--keep]
```

Writes `<out>/results.json` (one object per scale: `inject`, `size`, `start`, `read_model`, `sql`, `cli`, `http`,
`concurrency`, `log_hits`). Times are milliseconds, as `{n, p50, p95, p99, max}`.

## What it does

- Copies the live DB once with `sqlite3 -readonly <live> .backup` (default `~/.claude/ah2/state/ah.db`). It never opens the
  live DB otherwise, and never talks to the live daemon.
- Starts its own ahd. `AH_HOME` goes in `/tmp/ahprobe.*` (test mode), and `HOME` points at a fake home, whose msgs mode is db.
- Copies only the `msg_*` rows into that DB. Paths are rewritten from the live repos root into the sandbox, and each message
  file is written from its body.
- The pools live under `/tmp/ahprobe.*` because a test-mode daemon refuses paths outside it. Message paths are resolved part by
  part and `PathGuard.OnDiskCase` lists every parent directory, so a crowded `/tmp` adds to the numbers: compare runs, and
  check `ls /tmp | wc -l` before reading them as absolutes.
- Scale `xK` adds K-1 clones of the live rows: pk offset, ids re-dated, files written.
- Identities come from `tests/contract-tools/prove-self.mjs` on the harness's own pid (Orchestrator), or on a forked
  child's pid for members.
- `cli.spans` holds the `AH_TRACE_SPANS=1` phases of `msg list`. `process_floor_ah_version` is the cold process floor.
- At exit it stops the daemon (TERM, then KILL) and records any survivor in `leftover_ahd`. It deletes the sandbox unless
  you pass `--keep`.

`tests/publish-bin.sh` ends with `dotnet build-server shutdown`. When that must not run (other builds are in flight), call
`scripts/publish-native.sh --dotnet dotnet --native native --rid osx-arm64 --out DIR --version V --contract C --name ah2`
directly. Then write `ahd.contract` yourself.
