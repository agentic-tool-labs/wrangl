# role packs — agent-role reference

Read this to install, update, uninstall, adopt from or trust a role pack, or when a role shows `UNAVAILABLE` with a `pack-*` reason.

Contents

- install <source>
- adopt
- update <plugin>
- trust <name>
- uninstall <plugin>
- `pack-*` reasons

A role pack is a Claude Code plugin that also carries `ah-roles.json`. Claude
Code installs, updates and removes it; ah adopts its roles one at a time and
pins each to the exact content the user reviewed. `docs/custom-roles.md`
§ Role packs has the format. Show every `claude plugin …` command to the user
before running it; their permission settings govern it. An adopted row holds
only `from`, `pin` and the user's own fields: its class and agent come from
the pack, so edit can't change them (remove it and define your own instead).

**install <source>** — a git URL, `owner/repo`, or a local directory:
1. `claude plugin marketplace add <source>`.
2. Find the plugin's directory: the marketplace's `installLocation` in
   `~/.claude/plugins/known_marketplaces.json` (a local directory stays where
   it is; a git or GitHub source is cloned there when it is added), then the
   plugin entry's `source` in its `.claude-plugin/marketplace.json`. If that
   `source` is itself remote, shallow-clone it into a scratch directory. Run
   `R pack show --path <dir>`.
3. Show the roles and everything under "also in this plugin" — hooks, MCP
   servers, commands and skills run in every session once the plugin is
   installed, whatever ah does. Ask: install, or cancel.
4. `claude plugin install <plugin>@<marketplace> --scope user`, then
   `R pack show <plugin>@<marketplace> --json` and compare its `digest` with
   step 2's. If they differ, tell the user that what got installed isn't what
   they reviewed, offer to uninstall it, and adopt nothing.
5. Offer to add the pack to a roster (`R roster roster pack <name> --add
   <plugin>@<marketplace>`), or to create one with it (`R roster init --level
   <L> --route peer --roster <name> --pack <plugin>@<marketplace>`). Every
   role of the pack is then a role of any team built from that roster.
6. Say that sessions already running need a restart to dispatch the new
   agents as subagents; peers spawned from now on are fine.

**adopt** — one role, `<name>` chosen by the user (it may differ from the
pack's name; a built-in's name replaces that built-in's agent):
1. `R role set <name> --from <plugin>@<marketplace>:<role> --dry-run` (add
   `--level` and the user's own `--label`/`--description`/`--routes`/`--model`
   if they gave any). Show the fields, the findings, the tools with their
   flags, "also in this plugin", the unattended-runs line when there is one,
   and the pin.
2. Errors → say so and stop; a pack's file is never edited.
3. Commit with exactly the printed pin: `R role set <name> --from … --pin
   <pin>`. The user approves it in Claude Code's own prompt; it is refused in
   role, peer and subagent sessions and while a pipeline run is live.

**update <plugin>**:
1. `claude plugin marketplace update <marketplace>`, then `claude plugin update
   <plugin>@<marketplace>`.
2. `R pack show <plugin>@<marketplace>`.
3. Any change in the plugin makes every role adopted from it `pack-changed`.
   For each, run **trust**, and ask: trust, leave it unavailable, or remove
   the role.

**trust <name>**: `R role trust <name> --dry-run` shows the field changes,
the agent file diff, and the files added, removed and changed since it was
trusted (the whole file and file list when this machine has no stored copy).
Show all of it, then commit with the printed pin: `R role trust <name> --pin
<pin>` (approved like adopt). The user's own fields survive.

**uninstall <plugin>**:
1. List the roles adopted from it (`R role list --json`, rows whose `from`
   starts with `<plugin>@`).
2. Ask: remove those roles too (Recommended), or keep them (they become
   unavailable, `pack-missing`).
3. `R role remove <name>` for each chosen; if roster members still use one,
   name the roster and point at `/wrangl:agent-roster`.
4. `claude plugin uninstall <plugin>@<marketplace>`, and optionally `claude
   plugin marketplace remove <marketplace>`.

An adopted role shows `UNAVAILABLE` in `role list` with one of these reasons:
`pack-missing` (not installed, or the role is gone), `pack-invalid`,
`pack-ambiguous` (two installs of the plugin differ), `pack-symlink`,
`pack-changed` (run trust), or `pack-untrusted-here` (nobody on this machine
trusted it yet; run trust).
