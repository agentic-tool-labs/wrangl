# named rosters — agent-roster reference

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Read this for `roster list`, `roster copy`, `roster delete` and `roster use`.

Contents

- List
- Copy
- Delete
- Use or clear

These are `<ah> roster roster list|copy|delete|use …`.

## List

`roster list`: print its text — each roster with its level, what
it shadows, member count, route and the teams using it, the selected one
starred.

## Copy

`roster copy <src> <dst> [--level L]`: `src` defaults to the
selection, `default` for the default block; it writes at `src`'s level unless
the user names one. An empty new roster is `init --roster <name>` instead.

## Delete

`roster delete <name> [--level L]`: confirm with
AskUserQuestion (delete, or cancel) first. The CLI refuses a roster a team
still uses or that `activeRoster` selects; relay the refusal and its fix.

## Use or clear

`roster use <name>|default [--level L]`, `roster use
--clear [--level L]`: sets which roster later commands pick, in the user's
own file for this repo (`repo-user`) unless they ask for `repo` (shared,
committed) or `global`. Say the resulting selection back in one line, with
any warning that others reading that level can't see the roster. A live Team
keeps the roster it was built from.

## Question tag

Every `AskUserQuestion` in this file carries `metadata.source` `ah:bootstrap:agent-roster:named-rosters`; the hook refuses an untagged ask from the Orchestrator.
