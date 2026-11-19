# Testing Wrangl on your own Mac

Step-by-step for installing the native build (Wrangl 2.0.0), trying its terminal view, and rolling back to v1. Every command in
sections 1, 2, 4 and 6 was run in a throwaway sandbox (`HOME` under `/private/tmp/ahprobe.*`) and the output shown is what
came back; section 7 lists what only your real setup can show.

Sections 1-4 and 6 were proven against branch tip `38f8ee26` (macOS, Apple silicon, Claude Code 2.1.296). The terminal
view has been cut down to three screens since then: section 5 describes what the merged code does, read from it and from
its tests, and has not yet been run by hand on a real session. Those are the checks to run, and the first time you do, tell
us what differs.

## 0. What to expect, in one table

| Area | State at the proven tip | How you will see it |
|---|---|---|
| Load from a local checkout (`claude --plugin-dir`, v1 left alone) | works | section 1.2 |
| Native `ahd` and `ahd-tui` build | works (`install.sh`, one shared publish script) | section 1.3 |
| Keep v1 safe / roll back | works | section 2 |
| Daemon, `ahd tui`, `ahd tui --once` | works | section 4 |
| Teams, team and agent screens, keys, mouse | wired; to be run by hand | section 5 |

## 1. Build and install ah2

### 1.1 Get the source

Wrangl is not on `main` yet. Use a separate worktree so your main checkout stays on `main`:

```sh
cd <your wrangl checkout>
git fetch private
git branch -r --list 'private/ah/*native-v2*'            # one line: the branch to use
git worktree add ~/ah2-src <that private/ah/... branch>   # detached at its tip is fine
cd ~/ah2-src && git log -1 --format='%h %s'              # note this sha for bug reports
```

You need a .NET 10 SDK and Xcode command line tools (`xcode-select --install`) because the binaries are AOT-published.
`dotnet --list-sdks` should show a 10.x line.

### 1.2 Load the plugin (recommended: per session, nothing installed or changed)

```sh
claude --plugin-dir ~/ah2-src/agent-hierarchy-v2 \
       --settings '{"enabledPlugins":{"ah@agent-tools":false}}'
```

- `--plugin-dir` loads Wrangl for this session only, as `wrangl@inline`. No marketplace, no install, no plugin settings written;
  closing the session is the rollback.
- The `--settings` half switches v1 (`ah`) off **for this session only**. Without it v1 stays enabled and every hook runs
  twice. Your saved settings are not touched: v1 is still enabled in every other session.
- Check it from a terminal: `claude --settings '{"enabledPlugins":{"ah@agent-tools":false}}' --plugin-dir ~/ah2-src/agent-hierarchy-v2 plugin list`
  shows `ah  ✘ disabled` and, under "Session-only plugins", `wrangl@inline  ✔ loaded`. (Proved with `plugin list`; confirm
  in your first real session with `/plugin`.)

A shell alias saves retyping:

```sh
alias claude-ah2="claude --plugin-dir ~/ah2-src/agent-hierarchy-v2 --settings '{\"enabledPlugins\":{\"ah@agent-tools\":false}}'"
```

**Alternative: install it, persistently, through a marketplace.** Use this only if you want Wrangl in every new session.

```sh
claude plugin marketplace add ~/ah2-src
claude plugin install wrangl@wrangl
claude plugin list          # wrangl@wrangl  Version: 2.0.0  Read from: …/ah2-src/agent-hierarchy-v2  ✔ enabled
```

The checkout is its own marketplace, named `wrangl`, so adding it leaves the `agent-tools` marketplace and v1 alone.
Do not combine this with the `--plugin-dir` line above: the hooks would load twice.

### 1.3 Build the native binary

Dry run first; it changes nothing and prints each step:

```sh
sh ~/ah2-src/agent-hierarchy-v2/scripts/install.sh
```

Then, from a terminal you opened (the installer asks before editing your shell startup file there):

```sh
sh ~/ah2-src/agent-hierarchy-v2/scripts/install.sh --apply
```

It publishes `ahd` and `ahd-tui` (the terminal view) into `~/.claude/ah2/bin` with a `.contract` sidecar for each, links
`~/.local/bin/ahd`, writes `~/.claude/ah2/install.json`, and (only if you say yes) adds a PATH block to your rc file. Plugin
updates rebuild both when auto-rebuild is on. Inside Claude Code the same thing is `/wrangl:hierarchy install`, which asks
first. `ahd version` should print `"client":{"version":"2.0.0"}`, and `ahd tui --once` should print a frame or
`ahd is not running` (exit 3), never `ahd: tui needs ahd-tui beside …` (exit 70, which means the pair was not installed together;
run `install.sh --apply` again).

In a plain terminal `ah` is not on your PATH (Claude Code only puts a plugin's `bin/` on the PATH of its own Bash
tool), and `ahd` is only on it after the installer's link or PATH step. The commands below use these two lines; paste
them once per terminal:

```sh
alias ah='sh ~/ah2-src/agent-hierarchy-v2/bin/ah'
export PATH="$HOME/.local/bin:$HOME/.claude/ah2/bin:$PATH"     # for ahd, if the installer did not add it
```

## 2. Keep v1 safe, and roll back

Running v1 and Wrangl together doubles every hook. With the recommended launch in 1.2 the `--settings` flag already keeps v1
off for that session and nothing is saved, so there is nothing to undo: close the session and you are on v1 as before.
v1 and Wrangl keep separate state (`~/.claude/agent-hierarchy` for v1, `~/.claude/ah2` for Wrangl); nothing here deletes either.

**Only if you took the marketplace alternative**, v1 has to be disabled in your saved settings:

```sh
claude plugin disable ah@agent-tools        # ✔ Successfully disabled plugin: ah
claude plugin list                          # ah  ✘ disabled, ah2  ✔ enabled
```

Start a new Claude Code session afterwards; hooks are read at session start. To roll back from that state:

```sh
claude plugin disable wrangl@wrangl
claude plugin enable ah@agent-tools
ahd stop                                    # the ah2 daemon, if you started it
```

then a new session. The daemon is the one thing a closed session leaves behind: `ahd stop` ends it either way.

To drop Wrangl entirely: `claude plugin uninstall wrangl@wrangl` (add `--keep-data` to keep its plugin data) if you
installed it, `ahd login off` if you turned the login item on, and `rm -r ~/.claude/ah2` (its state: logs,
config, the binary). The machine-wide names Wrangl uses are `ahd` in `~/.local/bin`, the launchd label
`io.github.agent-tools.ahd` and the `# >>> ah >>>` block in your rc file; v1 creates none of them.

## 4. Start the daemon and the terminal view

```sh
ahd start          # {"ok":true,…,"status":"started","pid":…,"version":"2.0.0"}
ahd status         # "up":true, ladder counts, registrations
ahd health         # "status":"up","impl":"native"
ahd tui            # the interactive view; q quits
ahd tui --once     # one plain frame (pipe-safe); exit 3 and "ahd is not running" when the daemon is down
ahd stop
```

With no team the view says `No teams. Run /wrangl:hierarchy init in a repo.`; start a Claude Code session with Wrangl enabled and a
team and it appears within a couple of seconds. The view never starts the daemon: with none running it says
`ahd is not running` and retries.

A daemon started by `ahd start` from a bare terminal knows where the plugin is: `ahd status` shows its path as `root`
(an empty `"root":""` would mean the daemon cannot tick its dispatch clock, and `ahd.log` would say
`ahd tick failed for <session>: … the plugin location is not known`).

Smoke result in a pseudo-terminal: `ahd tui` entered the alternate screen with mouse reporting on, `?` opened help, `q`
left with exit 0 and both the screen and the mouse mode restored.

## 5. What to try

Run these in a real Claude Code session with Wrangl enabled so the daemon has something to show.

The screens, keys and mouse are on the [terminal view page](tui.md). `docs/tui-test-checklist.md` is the list to tick, and
`docs/tui-eyes-check.md` is a longer table for Terminal.app, iTerm2 and herdr.

Things to check by eye: `NO_COLOR=1 ahd tui`; a narrow window, then back; stop the daemon while the view is open
(`ahd is not running`, then it repopulates after `ahd start`).

## 6. Logs, errors and how to report a bug

| What | Where |
|---|---|
| Daemon log | `~/.claude/ah2/logs/ahd.log`; `ahd logs -n 100` prints the last lines |
| Daemon socket and info | `~/.claude/ah2/run/ahd.sock`, `ahd.json` |
| Hook errors | `~/.claude/ah2/hierarchy/hook-errors.jsonl` (rotated to `.1` at 1 MiB) |
| Install stamp | `~/.claude/ah2/install.json` |
| Terminal view | no log file; errors go to stderr of `ahd tui` |
| Health of the whole setup | `ah roster doctor` (version, install, rebuild-lock and other rows) |

A probe that runs a hook by hand (`ah-hook <unit>`, `ah hook <unit>`) from a Claude Code Bash tool is denied by
`pretooluse-hook-entry-gate` unless the command sets `AH_TEST=1` and an `AH_HOME` under `/tmp/ahprobe.<alnum>` (or
`/private/tmp/ahprobe.<alnum>`) on every invocation. In that mode the hook rebinds only team files that are themselves under
such a sandbox directory; a team file in a real repository is left alone and a `hook-errors.jsonl` row says so. A test-mode hook
writes no state of any kind outside such a directory (project directories, `session-pids`, peers and the rest are skipped, each with a
`test mode write outside the sandbox skipped: <dir>` row); CLI writes are unaffected. The repo's
shell suites run hooks from their own scripts, outside the Bash tool, so the gate never sees them.

A crashed rebuild leaves `~/.claude/ah2/bin/.rebuild.lock`; `ah roster doctor` names it in a `rebuild-lock` row. Once it is
over 10 minutes old the next install takes it over when its pid is gone. On macOS (no flock(1)), when its pid is still running,
the install fails naming the lock and pid (a background rebuild records `failed`); stop that process or remove the file.
Without flock(1) two installers can both judge the same stale lock;
it is safe, because each moves the lock aside and compares it with what it judged, so the loser treats the round as held and
no lock is deleted that its taker did not judge.

A bug report that can be acted on:

```sh
{
  echo "## sha";            git -C ~/ah2-src log -1 --format='%H %s'
  echo "## versions";       ahd version; claude --version; sw_vers -productVersion
  echo "## terminal";       echo "$TERM_PROGRAM $TERM_PROGRAM_VERSION TERM=$TERM TMUX=${TMUX:+set}"; env | grep -i "^herdr" | cut -d= -f1
  echo "## plugins";        claude plugin list
  echo "## status";         ahd status
  echo "## doctor";         ah roster doctor
  echo "## daemon log";     ahd logs -n 200
  echo "## hook errors";    tail -n 20 ~/.claude/ah2/hierarchy/hook-errors.jsonl 2>/dev/null
} > ~/Desktop/ah2-bug.txt 2>&1
```

Add: what you pressed or typed, what you expected, what happened, and whether `ahd tui --once` shows the same thing.
Hand the file and your description to the Orchestrator session.

## 7. What only your real setup can show

The sandbox proof cannot cover these; treat them as the actual test:

- `install.sh --apply` itself: it refuses any `$HOME` that is not your passwd home (even in test mode), so the placement of
  `~/.claude/ah2/bin/ahd` and `ahd-tui`, the `~/.local/bin/ahd` link, the PATH block and the stamp were only run as a dry run
  here. The build step (the shared publish script, with the installer's flags) and the resulting layout (both binaries,
  both sidecars, `ahd` reached through a symlink) were run separately and work.
- Hooks firing inside a live Claude Code session, the orchestrator's registration, and teams appearing in the view.
- The login item (`ahd login on` / `status` / `off`, launchd label `io.github.agent-tools.ahd`).
- How the view behaves under tmux and herdr, and how it looks in your terminal (colour, glyphs).
