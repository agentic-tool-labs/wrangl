---
name: agent-role
description: Defines, edits, checks, or removes a user-defined (custom) hierarchy ROLE and its agent file, or install, update, adopt from, trust or uninstall a role pack. Use for /wrangl:agent-role, for "add a custom role", "define my own agent/role", "make a ui-implementor", "custom reviewer/implementer/architect", "edit/remove a role", "which roles exist", "why is my role unavailable", or "install/update/trust a role pack". Adding a role to the roster afterwards is the agent-roster skill; standing up a live team is agent-team.
argument-hint: "[list|add|edit|remove|check|install|update|uninstall|adopt|trust] [name]"
---

# agent-role

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

A custom role is a `roles.<name>` row in `agent-hierarchy.json` with a `class`
(advise, design, review, implement, legwork) and an agent file.
`<ah> roster role …` does every read, check and
write; never hand-edit the JSON or re-implement its checks.

`R` below is `<ah> roster`, always with
`--cwd <abs cwd>`. `R role set <name>` takes `--class <c>`, `--agent <ref>`,
`--label <l>`, `--description <d>`, `--routes <r>`, `--model <m>`,
`--dispatch peer|model` (`model` only for a legwork role), `--level
global|repo|repo-user`, `--scaffold repo|user` and `--dry-run` (reference:
`docs/cli-tools.md`).

## What a class means

| Class | Step it can stand in for | Tool contract (errors) | Conventions (warnings) |
|---|---|---|---|
| advise | Escalate (Ultra-Advisor); every dispatch needs the user's approval | Read, SendMessage; explicit fable/opus model | no Edit, NotebookEdit, advisor |
| design | Design (Architect); tier rule applies | Read, SendMessage, Write | no Bash, NotebookEdit, advisor |
| review | Review (Reviewer) | Read, SendMessage; must NOT have Edit, Write, NotebookEdit | has Bash; no advisor |
| implement | Implement (Implementor) | Read, SendMessage, Edit or Write, Write or Bash | has Bash; no advisor |
| legwork | none (errands, dispatchable by any role) | none | none |

A row with `routes` is an in-chain **alternative** to its step's built-in for
that work; without it, a **side role**, dispatched only when the user asks or
its description fits.

## list

Run `R role list`. One line per role: name, built-in/custom, class, agent,
model, chain placement and contract status (`ok`, `n warnings`,
`UNAVAILABLE: n errors`, `UNAVAILABLE → reverted to wrangl:<role>`, `shipped`);
excluded rows with their reasons.

## check [name]

Run `R role list --json`; for each role (or just `name`) with findings, run
the fix loop.

## add

1. **Name.** Suggest one from the user's words, plus up to two variants. The
   peer name `<team-prefix>-<name>` is capped by Herdr at 32 characters
   (`[a-z][a-z0-9_-]`): suggest only names that fit, short for a global role.
2. **Class.** Offer the inferred class first (the fifth via Other), each
   option one line: its gates and tool contract (table above).
3. **Early check.** `R role set <name> --class <c> --dry-run`; a row-level
   refusal (reserved name, alias collision, bad charset) is re-asked now.
4. **Chain placement** (chain classes only): "Alternative to <Builtin> for
   specific work" or "Side role". An alternative needs `routes`: offer a
   drafted sentence, a tighter one, or Other.
5. **Agent file**, from the dry-run's `agent_file` status:
   - found: "Use existing <path> (Recommended)" or "Point at a different agent";
   - not found: "Scaffold ~/.claude/agents/<name>.md — passes the contract,
     usable in every repo (Recommended)", "Scaffold .claude/agents/<name>.md in
     this repo only", "I'll write my own file first" (stop; the user re-runs
     add or check), or "Use an existing agent" via Other.
6. **Model.** The class allowlist, class default first; advise needs an
   explicit fable or opus.
7. **Description.** "Use the agent file's description (Recommended)" when it
   has one, or "Write a one-line description".
8. **Level.** global (Recommended), repo, or repo-user — but when the agent
   file is repo-level, recommend repo (a global row pointing at it is
   unavailable elsewhere) and say so when the level does not match.
9. **Pre-flight.** The full `R role set … --dry-run`; errors → the fix loop,
   then dry-run again until none.
10. **Commit.** Show the exact command, run `R role set …`, report the row,
    the file path and any warnings.
11. **Specialisation** (scaffolded files only): ask, then Edit only the
    placeholder line under `## Specialisation`.
12. Offer "Add it to the roster now?" → `/wrangl:agent-roster add`.

## Fix loop (add, edit, check)

A row of `R role list --json` carries `findings`: `{level: "error"|"warn",
code, path, field, message, fix: [{kind, detail}]}`; a `fix` kind is
`edit-frontmatter` (the edit, in `detail`), `change-class` (the suggested
class) or `remove-file`.

For each **error** finding, one AskUserQuestion built from its `fix` list —
never invent a fix:

- "Fix the agent file for me (Recommended)" — only for `edit-frontmatter`
  fixes and only when the file is a user-owned bare-name file (repo or user
  level), never a plugin agent's. Apply exactly that edit with one Edit (add
  or remove a tool, remove `model:`, fix `name:`), then re-validate. To
  restrict a tool, remove it from `tools` or add it to `disallowedTools` —
  never a scoped entry like `Bash(git:*)`, which grants the whole tool.
- "Change the role's class to <suggested>" — when a `change-class` fix is
  present (a review-class file with Edit suggests implement).
- "I'll fix it myself" — stops the loop.

`remove-file` fixes are instructions only: show them, never act on them, and
never offer "Fix … for me" for a finding whose only fix is `remove-file`.

Warnings: list them, then one question — "Fix warnings too?" (all or none) or
"Leave them". The file body is never edited, except the scaffold placeholder.

## edit

1. Pick the role.
2. Ask which fields to change, `multiSelect: true` in batches of at most 4
   options (a question holds no more): routes, description, model, agent,
   class. For a built-in only agent and model.
3. Ask each chosen field as in add.
4. Dry-run and the fix loop.
5. `R role set <name>` with only the changed flags.

## remove

1. Confirm.
2. `R role remove <name>`; if roster members use the role it refuses: offer
   removing them via `/wrangl:agent-roster`, or cancel.
3. Offer to delete the agent file, keyed on where it resolved (`role list`
   shows the path); the default is keep, and this is the only place this skill
   deletes one:
   - bare repo-level file: "Keep it (Recommended)" / "Delete it";
   - bare user-level file: say it may be live in other repos, then
     "Keep it (Recommended)" / "Delete it anyway";
   - `plugin:agent`: never offered; say the file belongs to the plugin;
   - a built-in override: the same rules, on its file after `role remove`.

## Role packs

`install`, `update`, `uninstall`, `adopt`, `trust`, or an `UNAVAILABLE` `pack-*`
reason: read `reference/role-packs.md`.

Ask tag: `ah:bootstrap:agent-role:fix`.
