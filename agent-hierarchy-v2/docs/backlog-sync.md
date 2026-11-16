# Syncing the backlog with an outside tracker

Read this only when the user asks to sync the backlog with a tracker (GitHub, Linear, …). ahd has no
tracker code: you run the sync with the tracker's own CLI or MCP, through the `ah backlog` verbs.

1. **Agree with the user** (AskUserQuestion): which tracker; direction (push only, pull only, or both);
   which items (for example all but `untriaged`); what a done or dropped item does in the tracker; and
   cadence (each groom, or on request).
2. **Record the agreement** in `<project>/.claude/ah2/backlog-sync.md`: the tracker, the CLI or MCP to use,
   direction, scope, the done/drop mapping, cadence, and the last `as_of_ms`. The file existing is what
   "the user asked for sync" means; deleting it turns sync off.
3. **Push.** `ah backlog export --since <last as_of_ms> --json`, then create or update each item in the
   tracker with its own CLI or MCP (for example `gh issue create`/`edit`/`close`, or the Linear MCP). For
   a new tracker issue, write the link back with `ah backlog set <id> --ext <ref>`. Store the export's
   `as_of_ms` in the record file.
4. **Pull.** List the tracker's open issues and run `ah backlog add --ext <ref> --title …` for each. It is
   an upsert, so a re-run makes no duplicates. For a new row, which is the one the add nudges about, give it a
   description from the tracker's text: `ah work record <id> --type description --title Description --body-file -`.
5. **Never rewrite a tracker title or body a person wrote** unless the agreement says so. Labels and state
   are fine.
6. **Delegation.** The backlog-groomer runs steps 3-4 only when the record file exists, and follows it
   exactly.

## Mirroring every work item

`ah backlog export` carries backlog rows only. To mirror team items too, and to see a change that only
wrote to an item's history, the Orchestrator uses `ah work`. Sync stays opt-in: the record file decides,
and nothing here calls a tracker.

- `ah work export --since <last as_of_ms> --json`: every changed item with its id, aliases, title, state,
  kind, holder, team, stream, parent, `blocked_by`, `blocks`, `ext_id`, `ext_url`, attachments, `log_count`
  and `updated_ms`. No bodies. Store `as_of_ms` for the next run. Nothing changed: exit 1.
- `ah work log <id> --since <ms>`: the history behind a changed `log_count`, when the tracker should show it.
- `ah work set <id> --ext <ref> [--url <https-url>]`: record the tracker's id on the item. On a backlog row
  it also writes the row's `ext`, so `ah backlog export` stays right. `--ext ""` clears both, `--url ""`
  clears the URL. An id another item holds is exit 3 and names the holder.
