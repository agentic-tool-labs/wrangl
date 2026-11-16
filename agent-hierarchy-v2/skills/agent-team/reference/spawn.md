# spawn — agent-team reference

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Read this for `spawn-one` and `spawn-ad-hoc` details, `resync` and `move`.

Contents

- `resync` / `move`
- `spawn-one`
- `spawn-ad-hoc`

## `resync` / `move`

`team.json`'s recorded pane/tab/workspace location can go stale —
the user drags a pane in the Herdr UI, or an orchestrator-issued move happens
— without `<ah> roster` ever being told. Both verbs are additive; nothing else
changes, and neither is part of the normal roster-building flow above.

- **`<ah> roster resync [--dry-run]`** — queries herdr's live topology once,
  matches each peer member by herdr agent name first (falls back to pane id),
  and rewrites `team.json` with each member's current `transport_id`,
  `tab_id`, `workspace_id`. A member with no live match is left with its ids
  **unchanged** and gets `transport_stale: true` — a dead pane's close is a
  harmless no-op later, whereas clearing the id would leak a still-live one.
  `--dry-run` computes and prints the plan without writing. Non-herdr
  transports are a clean no-op. Run it any time the recorded location might
  be wrong.
- **`<ah> roster move <name> --tab <id> --split right|down | --new-tab
  [--workspace <id>] | --new-workspace [--dry-run]`** — runs `herdr pane
  move` for that member, then resyncs its record from a fresh topology query
  (the move's own response body is ignored). `--dry-run` prints the `herdr
  pane move …` command and runs nothing. Failure paths: an unresolved member
  name, or a herdr move that itself fails, both `fail()` with `team.json`
  untouched — the pane never moved, so the record is still correct as-is.
  `--split` is required whenever `--tab` is given — herdr rejects the move
  without it, so `<ah> roster` `fail()`s locally before
  calling herdr rather than forwarding a call that cannot succeed.
  herdr will not move a pane out of a zoomed tab, so `move` reads the source tab's
  zoom first. If a pane is zoomed it zooms that pane off, moves, and zooms the same
  pane back on, even when the move failed. If the moved pane was the zoomed one it
  is not zoomed back, and `rezoom` says `skipped (moved pane was zoomed)`. If the
  zoom cannot be turned off, nothing moves. `moved` is true only when the pane's
  recorded tab changed; otherwise `moved` is false and `reason` is herdr's own text.

## `spawn-one`

`<ah> roster create` refuses to run against a live Team
(`create --spawn`/`--commit`/`--plan` all `fail()` when one already exists),
so once a Team exists and one role has died — or was never launched — there
is no supported way to stand up just that role. `spawn-one` closes that one
gap; it is not a lighter-weight alternative to Create for a full team.

- **`<ah> roster spawn-one <role> [--member <name>] [--team <T>] [--cwd <path>] [--dry-run] [--allow-global]`**
  — resolves the roster, finds `<role>`'s member, and:
  - bare `spawn-one <role>` picks the first member of that role that is not
    live; `--member <name>` targets one specific same-role instance by its
    final name, as spawn output or the team record shows it (renamed or not).
  - a live team member for that role already exists → no-op,
    `{spawned:false, reason:"already live"}`.
  - otherwise → places one pane, launches and verifies it the same way
    `create --spawn` does, then merge-writes `team.json`: every other
    member is preserved, only this role's record is replaced or appended.
  - `--dry-run` prints the resolved member, layout mode, and launch command;
    executes and writes nothing.
  - `--team <T>` names the team to join. A name that matches no existing team
    creates that scope as part of the same call — there is no separate create
    step and nothing to ask the user about. The AskUserQuestion mandates above
    (team name, second-team collision) are `create`'s, and none of them apply
    here: a user who named a team has already answered the only question, so
    `spawn-one <role> --team <what they said>` is the whole command. The
    exceptions are a `team-name-unusable` refusal and a
    `member-model-undefined` refusal, which always go back to the user (SKILL.md
    § Refusals).
  - `--model <M>` launches the member on `M` this time only; the roster row is
    not changed. It is how a `member-model-undefined` refusal's `rerun` gives
    the answer.
  - `--cwd <path>`: with no `--stream`, the directory the member starts in and
    its work tree (its git top level). With `--stream <name>`, `--cwd` only
    locates the team; the member starts in, and is bound to, the stream's
    worktree. The output's `work_root` is the directory the gate will allow.
    Add `--allow-main` to put a code-writing role in the main checkout.
    A spawn into a work root whose recorded member is live (or whose liveness cannot be checked) is refused; add `--replace-live` to spawn there anyway.
  - Each Claude and Codex member gets its own tmp directory, reached as
    `$TMPDIR` (the team home's `tmp/<team>/<member>`; `--dry-run` only names it). A
    member launched with a custom `--command` harness gets no tmp and keeps
    the machine's own `TMPDIR`.

  Prefer `spawn-one` over Create whenever a Team already (partially) exists —
  Create's whole-team flow is the `/agent-roster` skill's job for building a
  fresh Team, never for patching one member into an existing one.

**Where the pane goes (herdr).** New members never go in your own tab.

- With `--stream <name>`, the member lands in the stream's tab.
- Without `--stream`, it joins the tab of a live member of the same role, or opens a
  new tab labelled `<team>: <role>`. A same-role member whose tab is your own tab
  does not count, so a new tab opens.
- A pane or tab is created in the member's work directory (the stream's worktree for a
  stream spawn), not in yours.
- `--split` splits your own pane instead. It is the only way to get that; never pass it
  in an autonomous run.
- tmux and the terminal transport have no tabs, so they split as before; there is no
  separate marker on the output for that.
- If the role's tab is zoomed, the spawn zooms it off, splits, and zooms it back on.

**A member that starts in the wrong directory** (`roster teams` lists it under
`misplaced_members`) cannot move itself: `EnterWorktree` is refused for every role.
Respawn it in its stream (`dismiss`, then `spawn-one … --stream <s>`); until then it
works by absolute path.

**`--allow-global`** is accepted as a no-op by every verb that lists it, so
existing command strings keep running; a roster at the `global` level needs
no flag and no confirm. `spawn-ad-hoc` never reads the global roster.

**Chain roles run only as peers.** Never dispatch one with the Agent tool.
SendMessage its live peer, or run `<ah> roster spawn-one <role>` (`spawn-ad-hoc`
when the roster has no member for the role), then SendMessage the name it
prints. If it fails to launch,
follow § When a role can't take the work. If its output has
`renamed_members`, tell the user the one-line rename notice (§ Create).


## `spawn-ad-hoc`

`spawn-ad-hoc <role>` is `spawn-one` for a member the roster does not
describe. Use it whenever one peer is wanted and the roster (if any) has no
live member for it — a second implementor on a different model, a codex member,
a role the roster never defined, or a repo with no roster at all. It reads the
repo-level roster only (never the global one), for the team's route
default, and writes only the team file.

1. **The name is derived, not chosen.** It uses the team's own prefix and the
   next free ordinal for that role, exactly as `create`/`spawn-one` do
   (`myrepo-implementor`, then `myrepo-implementor-2`). A suffix is never handed
   out twice: the next one is above the highest the team has used, counting
   dismissed members through their mailbox directories and the team history. If the
   derived name is already taken in the team file, the command refuses rather than
   overwriting the existing member's record.
2. **Every member field is accepted** — `--model`, `--effort`, `--kind`,
   `--route`, `--args`, `--auto-mode`, `--on-missing` — and validated by the
   same rules `add` uses. There are no relaxed rules for ad hoc members.
3. **The route must have a pane** (`peer`, or `pane` for a non-Claude kind).
   A `subagent` member is dispatched on demand and has nothing to launch.
4. It launches through the same `spawn-one` machinery, so `--dry-run`, the
   herdr-vs-terminal transport choice, and the blocked-at-startup reporting all
   behave identically. See § spawn-one for those; only the member's source
   differs. A roster that resolves at the global level is treated as no roster.

Report the `name` it printed (renamed or not) back to the user in one line — they did not choose it,
and they need it for a later `dismiss`.

## Question tag

Every `AskUserQuestion` in this file carries `metadata.source` `ah:bootstrap:agent-team:spawn`; the hook refuses an untagged ask from the Orchestrator.
