# Reinstall the pushed build (dogfood)

After every release push, run `scripts/dogfood.sh <pushed sha>` from the repository, without asking, then report it in one line. `--plan` prints every step and changes nothing.

## What the script does

1. Follows one line of history, anchored at the last sha in `$AH_HOME/dogfood/builds.jsonl`, and checks in this order:
   - no earlier build: build;
   - the same commit: `unchanged`;
   - an ancestor of the last build: `unchanged (older than installed)`, never a downgrade;
   - a commit that does not descend from the last build (another branch, a rewritten line, or a last build the repository no longer has): **refused**, naming both shas, and nothing is touched. Open a tracked issue; choosing a new line is the user's decision, never yours.
2. Builds the version `<release version>.<commit count>` (for example `2.0.1-pre.1.1873`). Every build sorts above the one before it.
3. Refuses when the build's native contract differs from the installed one. Nothing is installed then: report "contract `<a>` → `<b>`: the reinstall needs every session restarted; not done automatically", and leave it for the user to do from a terminal when sessions can restart.
4. Exports the commit with `git archive` into `$AH_HOME/dogfood/marketplace/wrangl` (the repo working tree is never touched), stamps the version in that copy only, and keeps the previous export as `ah2.prev`.
5. Runs `claude plugin marketplace update wrangl-dogfood` and `claude plugin update wrangl@wrangl-dogfood`. Claude Code adds a new cache dir and keeps the old one for sessions that already loaded it. If a call fails, the previous export is put back and the script stops with the install untouched.
6. Runs the new dir's `scripts/install.sh --apply --rebuild-only --no-daemon-restart`. There is one binary per AH_HOME, swapped by rename; every plugin dir's shim runs it.
7. Checks `install.json` names the new version and the same contract, records the build in `builds.jsonl`, and prints the report line and the live peers.

## One-time move

`scripts/dogfood.sh --migrate <sha>` replaces the install from the marketplace it was first installed from (`AH_DOGFOOD_OLD_MARKET`, default `old-marketplace`) with one from the dogfood marketplace, once. It snapshots the plugin's options, uninstalls, installs, restores the options and checks their count. **It edits the user's Claude Code settings, which an agent session may not do: ask the user to run it from their terminal** (it refuses under an agent session). A run that stopped after the uninstall prints the snapshot path and `claude plugin install ah2@old-marketplace` to go back; running the same command again resumes. The plain script refuses until the move is done. After the move, no settings write is needed, so every later build stays automatic.

## After the script

- **Reload the peers.** The script lists the live peers. New hooks reach a running session only through `/reload-plugins`: send each peer `herdr agent prompt <name> "/reload-plugins"` when it goes idle, never mid-turn.
- **Swap the daemon at a quiet point:** `scripts/dogfood.sh --swap`. It waits up to 30 minutes for no live member `working` or `blocked` in `<ah> roster status` (a pending Codex question reads as `blocked`), then runs `ahd restart` and checks the daemon reports the installed version. It is never forced: exit 3 means no quiet point came, so report "daemon deferred: `<why>`". Nothing retries it: run it again when the next idle notice arrives. Deferred does not hold the old daemon: the next client on the newer version drains it and starts the installed one, any schema migration included.
- **Read the rejections.** `--swap` waits 60 s and quotes every `bad_request` row since the restart from `$AH_HOME/logs/rejected.jsonl` and its rotated `.1` file (`client_version`, `caller`, `path`, `field`). Any row opens or updates a tracked issue with those client versions: it is a client the new daemon refused. The route probe `<ah> msg due` sends (GET `/v1/due`, "session must be a non-empty string") is answered 400 by design and is counted as `probe <n>`, not as an error; a daemon with the fix stops logging it.
- **Permissions.** Rules and the commands agents type name one path, `$HOME/.local/bin/ah`, which every placing install re-points at the new plugin dir (a temporary link, then a rename, after the stamp is written). The path is used only while that link leads to the stamped plugin dir; a link to anything else is left alone, and the plugin dir's own `bin/ah` is used instead. With two AH_HOMEs sharing one plugin cache, the link follows the last install, and the runtime check keeps the other home on the plugin-root form. The report line says `permissions sync needed` or `no`. It is needed when the permission-rule source changed since the last build, when a rule the ledger recorded does not start with the current command path, or when there is no earlier build; when the link cannot be used the line adds why. If needed, ask the user to run `<ah> roster permissions sync` from a terminal; an agent session may not. `--migrate` runs that sync itself, from the user's terminal.
- **Typing the command.** Agent and skill text writes the command as `<ah>`, the `ah` command in the `ah CLI root` line of your context (newest wins).
- **Never uninstall** outside the one-time move. `claude plugin update` does nothing when the version is unchanged, which is why every build has its own version.
- **Reinstalling at the same version.** `scripts/dogfood-reinstall.sh [--dry-run]` refreshes the marketplace, uninstalls and installs Wrangl (the marketplace is read from the installed plugin record), runs the new build's `install.sh --apply --auto-rebuild`, then names `/reload-plugins`. It stops at the first failing step and pushes and tags nothing. After an install, `claude plugin install` prints "N userConfig options not yet set": that line is informational (it means the options were never set explicitly, the defaults apply); `/plugin configure wrangl@<marketplace>` shows the values in force.

## Report

One line per reinstall: `dogfood <old version> → <new version> (<sha7>): plugin <dir>, binary placed, daemon <swapped|deferred: why>, permissions sync <needed|no>, post-swap errors <n>`, then the peers still to reload. The script prints the line with the daemon deferred; `--swap` prints the daemon and error fields.

## Manual fallback

If the script cannot run: `/plugin` update then `/reload-plugins`; `install.sh --apply --rebuild-only`; verify with `ahd where --json`.
