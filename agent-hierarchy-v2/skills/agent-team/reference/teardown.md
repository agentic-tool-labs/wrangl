# teardown — agent-team reference

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Read this before `dismiss`, `disband`, `untrack`, `adopt` or `reap`, or when a close plan comes back empty.

Contents

- `disband`
- An orphan that live sessions depend on
- Plan came back empty — search before you say so
- `dismiss`
- `untrack`
- Misplaced peer: relocation fallback

## `disband`

`disband` **closes every member's session** and drops the team record. It is a
**two-call contract** — plan, then close — so nothing is destroyed before the
user has seen exactly what would be. To drop the record and leave the sessions
running, that is `untrack --all` (§ untrack), never `disband`.

The band's `[ Dismiss ]` button is a user-driven path to the same plan and close,
with its own confirm dialog (and a second one when work is in flight). It does not
change this contract for the Orchestrator.

**No `team.json`?** Plan and `--close` do not no-op: they operate on
the live peer records in `peers.jsonl` instead — the peers `add`/`spawn-one`
brought up, or a Team whose `team.json` was lost. The plan and `--close` work the same
way, and every such output carries `source: "peers"`. When a `team.json`
exists, the plan also lists live peers that are not in it, each row labeled
`source: "peers"`, and `--close` closes that whole set. Only herdr peers are closable this way: checkin records the
herdr pane id, so a tmux peer surfaces with `command: null`.

1. Run `<ah> roster disband` — the plan form, i.e. no `--close`. Read-only — `team.json` is untouched. For the
   herdr transport it resyncs the member list **in memory** first (never
   persisted), so the plan targets each member's *current* pane rather than
   the one it was spawned into — you do **not** need to run `resync` first. It
   returns a `close` array, one entry per member, with a `command` (or `null`
   for terminal-routed, subagent-routed, or a member with no live
   `transport_id`) and, for herdr, a `resync_status` per member plus a
   sibling `resync` key (`{"ok": true, "counts": {...}}`, or `{"ok": false,
   "reason": "..."}` if herdr was unreachable — the plan still comes from the
   stored ids in that case, never blocked).
   If the plan comes back empty — `disbanded: false` with
   `reason: "no active team and no live peers"`, or an empty `close` array —
   you are not done: work through § Plan came back empty below, including its
   `ListAgents` pass, before you report anything.
   The plan also carries `release.statuses`, one per member. Only a member that
   is working now is held (`busy`). Every other member closes, and
   `release.warnings` names what it leaves behind: an open request, uncommitted
   work, or commits not on the run's branch or the release, by branch and sha.
   Closing a pane does not touch the work tree. Tell the user what the warnings
   name; they do not stop the close. `--ignore-release <name>` closes a held member anyway, with a warning: use it only when the user asks.
2. Prompt the user once: "this will close N live sessions — proceed?",
   naming the members. Stop here if they decline. This conversational
   confirmation is still required; step 3's harness prompt does not replace it.
3. Call `<ah> roster disband --close` (one call; `--confirm` and
   `--plan-token` are accepted and ignored). The harness permission prompt
   may also fire. Exit 3 means some members are working now: the others closed,
   and `held` names each held member. Report the held members, and what
   `release.warnings` names. Report, per member, whether its session actually
   closed or the close call failed (e.g. the pane was already gone) — a
   failed close is reported, not fatal.
   `--close` also reconciles the team file, from a fresh read of it — run no
   bookkeeping command afterward. A row goes when its close succeeded, or when
   it had no pane to close and holds no live session (a subagent row, a dead
   paneless member). A row stays, listed in `kept` with a `why`: `close-failed`,
   `live` or `indeterminate` (no pane to close, but its session is or may be
   there), or `added` (it appeared while the closes ran). Nothing kept → the
   file is removed (`team_removed: true`); otherwise it is rewritten with only
   the kept rows. `pruned` names what went. Anything in `kept` → re-plan and
   close the remainder.

**Read the whole answer — plan and close alike.** Both carry `warnings`,
`strays`, `still_live`, `not_closable`, `expected` and `team_files`. Show the
user every `warnings` entry, every `strays` name and every `still_live` name,
in your confirmation (step 2) and again in your report. A plan with `strays`
closes herdr agents that no team record names — matched by name and checkout
only — so name them in the confirmation; the plan's first warning spells out
the risk. `closed` means **verified gone** on a fresh re-query, not that the
close command ran: never report success unless `closed: true` and `still_live`
is empty. `not_closable` lists sessions with no pane; they were not closed.
A team's file lives in the main checkout's hierarchy dir whichever checkout
spawned its members, so the same plan comes back from a worktree and from the
main checkout; `team_files` names what was read.

Never skip the plan call or its confirmation step — folding plan → confirm →
close into fewer calls is exactly what would close sessions before a declined
prompt could be honored. The plan forms (`dismiss --plan`, bare `disband`) never
close anything. Bare `dismiss <name>` is not a plan: it closes a clean member at
once and refuses one that is not clean; `--close` closes whatever the state. Every
close goes through the same permission gate.

**Want the bookkeeping cleared without closing anything?** That is
`untrack --all --keep-sessions --commit` (§ untrack) — sessions may hold work
that already cost tokens. Print the member names and `transport_id`s it returns
so the user can close them themselves if they want to.

A stale Team (dead orchestrator pid, or older than the fixed 24h cap) is also
swept automatically on the next plain top-level SessionStart — that sweep
clears `team.json` directly and never runs `<ah> roster disband`, so it is
unaffected by which flag is the default here.

**Recovering an orphaned Team.** A Team whose `orchestrator.pid`
is `null` reads as dead and is on the same
sweep clock — it must be re-owned via
`<ah> roster adopt --orchestrator-pid <pid>` **before the next SessionStart**,
or the sweep deletes it (members, refs, `transport_id`s — everything) before
`adopt` gets a chance to run. `adopt` refuses to touch a Team whose recorded
owner is alive and different — it is recovery for an orphan, not a way to
steal a live Team.

Adopt only when the user asked for it, or after an AskUserQuestion with the
options `Adopt <team>` and `Leave it`. Never adopt on your own initiative: a
team that a new session finds orphaned may belong to someone else's work. A
restarted Orchestrator is rebound to its own team automatically, so an orphan
seen at a new session is not that.

### An orphan that live sessions depend on

For an orphaned team, `teams` and
`reap` list `live_members` (its records that are live sessions) and
`attributed_live` (live sessions launched with its team file that match no
record), each with a best-effort `last_brief_from`. `reap --commit` keeps such
a team instead of clearing it.

- **An orphaned team with `live_members` is adopted, never reaped.** Ask the
  user with AskUserQuestion; never adopt without the answer. Options:
  - `Adopt <team> (Recommended)` first when every live member's
    `last_brief_from` is you (your session name, or your own reply-to): that
    is an Orchestrator restart or resume, which changed its pid. Otherwise
    `Adopt <team>` is offered without the recommendation;
  - `Leave it` (nothing changes).
  - On `Adopt <team>`, run `adopt --orchestrator-pid <your pid> --team <t>`
    (`reap --commit` prints it as `next`).
  - Do not offer reaping: `reap` keeps the team anyway.
- **An orphaned team with only `attributed_live` sessions: leave it.**
  - Never adopt it: that would launch its dead records again, as duplicates
    of the live sessions.
  - Never `disband --close` it: read the plan's `sources`, and never close a
    session that you or another Orchestrator is briefing.
  - Tell the user in one line that you are leaving it. `reap` removes it after
    those sessions exit.
- Adopting never closes or launches anything.

### Plan came back empty — search before you say so

Applies to both `disband` and `dismiss`. **Trigger:** the plan returned
`disbanded: false` / `dismissed: false` with
`reason: "no active team and no live peers"`, or an empty `close` array, or
`dismiss` failed with "no member named". The sessions are very likely up under
a name this scope does not cover. Do not stop here and do not report yet. Work
all three sources, in this order, before the single confirmation.

1. **Other team files.** `<ah> roster teams --cwd <abs cwd>`.
   If a team lists live members, or an `untracked_live` row whose name fits
   `<prefix>-<role>[-<n>]`, re-run the plan against it: `--team <that team>`
   for `disband`, or that row's `pane_id` / `role@sid8` for `dismiss`. This is
   what "disband the team" means when the peers are tracked under an alias.
2. **The CLI's own herdr answer.** Read `sources.herdr` in the plan output:
   `{ok: true, agents, matched, prefix}`, or `{ok: false, reason, prefix}` when
   herdr could not be asked. Note the reason for your report. Do **not** run
   `herdr agent list` yourself — same PATH, same answer, and the CLI already
   queried it.
3. **`ListAgents`.** Call it once and take the rows whose name is
   `<P>-<role>` or `<P>-<role>-<n>` for the `P` in `sources.herdr.prefix`.
   A matching row that is not already in the plan's `close` set (match by
   name) is a **ListAgents-only** session: reachable by `SendMessage`, not by a
   pane close. `route: pane` members never appear there, and a peer started
   outside herdr appears *only* there.

Then **one** confirmation, listing every entry you found with its source
(`team` / `peers` / `herdr` / `ListAgents-only`) and the pane or address that
will be acted on — the same single conversational confirmation, independent of
the harness gate.

On approval: pane rows go through the normal
`--close` step. Each ListAgents-only row gets one
`SendMessage` instead:
`[hierarchy-disband] <team or prefix>: disbanded by the orchestrator — stop work, send no further reports, and end your session if you can.`
Report those as **notified, not closed**. Never drop them silently.

Only once all three sources came back empty, report it in these terms:

> Nothing to disband. Searched with prefix `<P>`: team files in
> `<hierarchy dir>` (N teams, 0 live members), peers.jsonl (0 live),
> herdr agent list (`ok`: M agents, none named `<P>-*` | `unavailable:
> <reason>`), ListAgents (K sessions, none named `<P>-*`). If the sessions
> were started under another name or from another checkout, say which and I
> will retry with `--team <name>`.


## `dismiss`

`remove --member <NAME>` edits the roster **config** (the template
for future Teams); `dismiss <name>` edits the **live Team's `team.json`** —
they write different stores, and each names the store it wrote in its output.
`dismiss` mirrors `disband`'s plan/close split, scoped to one member. A name that is not in `team.json` — or no `team.json` at all — is looked up in
the live peer records, output carrying `source: "peers"`; `<name>` accepts any identifier the user can see for such a session —
a `pane_id`, a `session_id` or a unique 8+ character prefix of one, the
`role@sid8` form `teams` prints, or the herdr display name. An ambiguous
identifier fails and lists every candidate rather than guessing.

**Several members at once.** `dismiss <a> <b> …` plans them together. The
plan lists each under `members` with `closable`; when one may not,
`failures` says who is held and why. One `--close` call closes the whole set,
so the user is asked once. Members close in the order given and the first failure
stops the rest (exit 1); `results` marks each `closed`, `failed` or
`not-attempted`. A name given twice is refused (exit 2) and nothing closes.
`--ignore-release <name>` works per name.

1. `<ah> roster dismiss <name>` — the plan form, i.e. no `--close` —
   read-only, resyncs that one member in memory for herdr, and returns
   `member`/`live`/`remaining`. `live` reads the check-in
   registry; a stale-registry member can still report a non-null `command`.
   `release.statuses` gives the member's release status; only a member working
   now is not closed (exit 3, `held`), and `release.warnings` names what any
   other leaves behind, as in § `disband` step 1.
   If it fails with "no member named", or reports
   `reason: "no active team and no live peers"`, work through § Plan came back
   empty — including its `ListAgents` pass — before reporting anything.
2. If `live` is true and the session should actually close, prompt the user,
   then call `<ah> roster dismiss <name> --close` (one call).
   On a successful close the `team.json` row is removed too (`untracked: true`);
   if the close failed, the row stays, because a live session with no record is
   exactly the orphan this flow exists to prevent. A member that is already
   dead has nothing to close — prune its record with `untrack` instead.

`--also-config` (with `--close`) additionally removes the matching roster
config entry, so a future `create`/`spawn-one` doesn't rebuild the instance just
dismissed. Default off — plain `dismiss` never touches the config. Removing a
non-last same-role config entry re-ordinals later siblings' derived names — the CLI warns and reports it (`config.reordinaled`); live
`team.json` records keep their original names regardless.

`dismiss --close` returns the same `closed` (verified gone), `still_live`,
`strays` and `warnings` as `disband --close`: show them, and report success
only when `closed: true` and `still_live` is empty.

A verified close of a member also releases its work-tree record and removes its tmp
(only under the team's `tmp/`; a failure is a warning); `disband --close`
does the same for every member it closes.

Dismissing the last member ends the Team: the plan reports
`team_will_be_removed: true` (say so when you ask the user), and the close
removes the team file and reports `team_removed: true`.

## `untrack`

`untrack` is the **only** verb that forgets a record without
touching a session, and it is the answer to exactly two situations:

- the user explicitly wants the session kept — "leave it running", "just stop
  tracking it", "forget the team but don't close anything";
- the target is already gone, and its record is stale bookkeeping to prune.

Anything else — "dismiss", "remove", "drop", "kick", "close", "disband", "tear
down", "get rid of" — means `dismiss`/`disband`, which CLOSE the session. When
the user's words are genuinely ambiguous, ask; do not pick.

1. Bare `<ah> roster untrack <name>|--all` (or with `--plan`) is read-only:
   it reports what would be forgotten and each target's liveness.
2. `--commit` removes the record. A target that is live, or whose liveness
   cannot be determined, is REFUSED unless `--keep-sessions` is passed — the
   refusal names both remedies (`dismiss` to close it, or `--keep-sessions` to
   leave it running untracked) and says the record cannot be recovered.

Untracking the last member ends the Team: the team file is removed and the
output reports `team_removed: true`. The session itself is left running.

`--all` forgets the whole team file instead of one member. Untracking
something already gone succeeds with `already_untracked: true`, so a retry is
never an error. `--also-config` (single member only) additionally removes the
roster template row, with the same ordinal-shift warning `dismiss` gives.

## Misplaced peer: relocation fallback

A Team records `expected_root`, the directory a peer's session should run in.
SessionStart marks a peer running elsewhere `misplaced: true` and tells that
peer how to relocate; it never refuses to register a misplaced peer.
`checkin` re-checks the current cwd and exits non-zero while still misplaced.
`misplaced_unattributed` (on `<ah> roster teams`) counts misplaced peers this
session could not safely attribute to one Team member, because a role is shared
by more than one member or a row has no recorded Team: it means do not guess
which member.

**Fallback, when the peer reports that relocation was refused, denied or
impossible:** `<ah> roster dismiss <name>`, then
`<ah> roster spawn-one <name> --cwd <expected_root>`. This **discards the
dismissed peer's context**: use it only after relocation has genuinely failed,
never as a first resort, and never against a peer `misplaced_unattributed`
could not confidently name.

A dismissed member's tmp is removed at the verified close.

## Question tag

Every `AskUserQuestion` in this file carries `metadata.source` `ah:bootstrap:agent-team:teardown`; the hook refuses an untagged ask from the Orchestrator.
