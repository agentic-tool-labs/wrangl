# role packs — agent-role reference

Read this to install, update or uninstall a role pack, or when a role shows `UNAVAILABLE` with a `pack-*` reason.

Contents

- install <source>
- update <plugin>
- uninstall <plugin>
- legacy rows
- `pack-*` reasons

A role pack is a Claude Code plugin that also carries `ah-roles.json`. Claude
Code installs, updates and removes it. Installing it is the trust decision
(adr-46): ah has no adopt or trust step. A roster names its packs, and every
role of those packs is a role of any team built from that roster; a pack role
named after a built-in replaces that built-in's agent there.
`docs/custom-roles.md` § Role packs has the format. Show every `claude plugin …`
command to the user before running it; their permission settings govern it.

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
   they reviewed, and offer to uninstall it.
5. Offer to add the pack to a roster (`R roster roster pack <name> --add
   <plugin>@<marketplace>`), or to create one with it (`R roster init --level
   <L> --route peer --roster <name> --pack <plugin>@<marketplace>`). Every
   role of the pack is then a role of any team built from that roster.
6. Say that sessions already running need a restart to dispatch the new
   agents as subagents; peers spawned from now on are fine.

**update <plugin>**:
1. `claude plugin marketplace update <marketplace>`, then `claude plugin update
   <plugin>@<marketplace>`.
2. `R pack show <plugin>@<marketplace>`.
3. Nothing needs re-approving: the update is reported, never blocking. It
   takes effect at each member's next start.

**uninstall <plugin>**:
1. List where it is used: `R pack list` shows, for each role, the rosters
   that name the pack and any legacy rows.
2. Ask: remove the pack from those rosters too (Recommended), or keep it
   listed (its roles are then absent from those rosters, with a warning).
3. `R roster roster pack <roster> --remove <plugin>@<marketplace>` for each
   roster chosen, and `R role remove <name>` for each legacy row; if roster
   members still use one of its roles, name the roster and point at
   `/wrangl:agent-roster`.
4. `claude plugin uninstall <plugin>@<marketplace>`, and optionally `claude
   plugin marketplace remove <marketplace>`.

**legacy rows** — a roles row with `from` (adopted before adr-46) keeps
working; its `pin` and `slots` are ignored. Moving to the roster form is
optional: `R roster roster pack <roster> --add <plugin>@<marketplace>`, then
`R role remove <name>` for each legacy row of the same name. A renamed legacy
row (its name isn't the pack role's) needs its roster members renamed with
`roster edit`; ah does not do it. `~/.claude/ah2/trusted/` is no longer read
and may be deleted.

A pack role shows `UNAVAILABLE` in `role list` with one of these reasons:
`pack-missing` (not installed, or the role is gone), `pack-invalid` (the
manifest or the role's class is unusable, or its agent file is missing), or
`pack-ambiguous` (two installs of the plugin differ: uninstall all but one).
Anything else wrong with a pack agent (its frontmatter, its `tools` list, an
MCP server grant, its class contract) is a warning in `role list` and
`pack show`, and the role stays.
