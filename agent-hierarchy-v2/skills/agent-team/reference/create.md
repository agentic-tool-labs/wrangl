# create — agent-team reference

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Read this before `create`, or before setting up a whole team.

Contents

- Reuse a recent team
- Several teams in one request
- Step 0: Layout
- Step 1: Plan
- Step 2: `manual` mode placement
- Step 3: Spawn (phases 3a and 3b)
- Step 4: Check in
- Step 5: Commit
- Create manual-mode layout

## Create

`create [auto|manual]` (default `auto`) instantiates the resolved roster as a
live Team, verified via check-in. `<ah> roster create` only ever does file
I/O; spawning sessions and calling `ListAgents` are things only this skill's
running session can do — drive the sequence yourself:

**Reuse a recent team.** Before planning a Team from the roster
files, offer to reuse a recent one: run `<ah> roster history --json`, and if it returns any entries, present them via
**AskUserQuestion** (label, role list, active/idle, last-used) alongside a
"start fresh from the roster" option. If the user picks an entry, run
`<ah> roster create --from <id> --commit --spawn` (its own id, not the alias)
in place of the roster-driven plan below — same downstream steps (spawn,
check-in) apply unchanged.

**Several teams in one request.** When the user asks for more than one Team at
once ("create team foo from roster foo-named-roster and create team bar from
roster bar-named-roster"), run the steps below once per Team, driven by this list:

1. **Read the request into a list of Teams first,** each with its roster, before
   any command runs:
   - "team foo from roster X": foo builds from X.
   - Several Teams and several rosters in one phrase ("teams foo and bar from
     rosters X and Y", "set up foo and bar using the foo and bar rosters"): pair
     them in the order given.
   - One roster for several Teams ("both from X", "same roster for both"): every
     Team uses it.
   - One Team per named roster ("a team per roster", "a team for each of X and
     Y"): each Team takes its roster's name.
   - "The foo team from its roster", or "from the foo roster" with no other
     roster word: the roster is `foo`.
   - A Team with no roster named: the single-Team rule (the selected roster; the
     roster question only when the plan lists `named_rosters`).
   - Counts that don't pair (two Teams, three rosters): one roster question per
     Team, in the round of step 4.
   - A Team per roster with no rosters named: one multi-select question listing
     the names from `<ah> roster roster list --json`, the selected roster first.
   - The same Team name twice: ask once for a different name for the later one.
   - Never ask the team-name question for a Team the user named, nor the roster
     question for a Team whose roster the user named:
     never ask what the request already says.
2. **Make one `ListAgents` call for the whole request.** Each Team's
   names-in-use set comes from that one result, by its own `<team>-` prefix;
   capture it once per Team and pass it unchanged to that Team's phases.
3. **Plan each Team, one after another** (a plan can write: it clears a stale
   Team): `create --plan --team <T> [--roster <r>] [--names-in-use …]`, always
   with `--team`, even when it is the name the plan would pick anyway.
   - A plan that refuses because the roster is missing lists the defined
     rosters: that Team's roster joins the step 4 round, the defined rosters as
     options and the selected one first.
   - A plan that refuses because a live Team of that name exists: tell the
     user, and put "another name for <T>, or skip <T>" in the step 4 round.
   - A plan that refuses with `team-name-unusable`: offer its `suggestion` in
     the step 4 round, as for a single Team.
4. **Ask one question round for every team.** It holds only the Team names the
   user didn't give, the rosters still unsettled, each Team's
   `members_needing_model`, and the layout if the user asked to change it (one
   `--mode` for every Team unless the user named one per Team). At most 4
   questions per AskUserQuestion call, with more calls as needed, Team by Team;
   every question's header and text name its Team. Re-plan each Team whose
   flags changed.
5. **Spawn, then check in and commit, team by team, never in parallel.** Run
   each Team's `--spawn` in turn; the next Team's spawn may start while the
   earlier Team's sessions boot. Then each Team's check-in and `--commit`, each
   with that Team's own `--team`, `--roster`, `--names-in-use` and
   `--member-model` flags. A Team that fails doesn't undo the others: report it,
   and ask once whether to retry it.
6. **Report one line per Team:** "Team foo (roster X): foo-architect,
   foo-implementor, …". Then: "You now own N teams. Name the team when you ask
   for something ('disband foo'); a member's name already says its team
   ('dismiss bar-reviewer')."

0. **Layout — ask nothing by default.** The Team's pane layout is `create`'s
   `--mode`, which defaults to the stored global preference (`teamLayout`),
   else `auto`; a plan reports what it will use as `layout: {mode, source}`.
   Do not ask about it. When the user names a layout ("spawn a team in
   grid"), pass it as `--mode` without asking. Only when the user asks to
   change the layout, ask once with AskUserQuestion — `auto` (columns for 1-2
   members, grid beyond), `columns` (one vertical column per member), `grid`
   (balanced quadrants), the plan's current value marked "(current default)"
   — and pass the answer as `--mode`. An explicit `--mode` on `--spawn` or
   `--commit` becomes the default for future teams; say so when it changes.
1. **Plan.** Run a bare `<ah> roster create --plan` (with `--team <name>` only
   when the user already named the team). It resolves the roster, refuses if
   a live Team already exists (tell the user to `disband` first), clears an
   already-stale one automatically, detects the transport (`herdr` if
   `HERDR_ENV=1`, else `tmux` if a tmux server is reachable, else
   `terminal`), and returns each member's `name` (renamed or not), role, model,
   effort, route, and — for peer-routed members — a `spawn` shape (`layout`
   and `launch` command lists for the detected transport, plus how to thread
   the target id from one to the other). If it errors because no roster
   resolves, hand off to § Init in the agent-roster skill (`skills/agent-roster/SKILL.md`).

   **Names already in use — before the team-name question.** Call
   `ListAgents` once, before asking the team-name question, and keep its
   result. For the plan's team name, and for any team name the user then
   chooses, collect every live session name that begins with `<team>-`, with
   any trailing `[ref]` stripped. Only exact names count: `x-architect-2` in
   use does not touch a member named `x-architect`.
   - Pass each collected name as `--names-in-use <name>` (repeated, one per
     name) to that team name's plan. A member whose name is in use comes back
     renamed, listed in the plan's `renamed_members`; never work out a suffix
     yourself.
   - In the team-name question, the option for a name with renames carries
     them in its label, e.g. "claudetools (architect →
     claudetools-architect-2)".
   - Once the team name is settled, capture that name's set **once** and
     pass it **unchanged** to every later phase (the re-run plan, `--spawn`,
     `--commit`), carried like `--roster <r>`.
   - Never re-read `ListAgents` for the set after `--spawn`: the team's own
     new sessions are live by then, and a fresh set would rename them again
     at `--commit`. Step 4's check-in calls do not change it.

   **Renamed members — `renamed_members`.** After any create phase whose
   output has `renamed_members`, tell the user one line per team and ask
   nothing: "Renamed <renamed_from> → <name> (and …): another session
   already holds that name." The same line follows a `spawn-one` or
   `spawn-ad-hoc` whose output has `renamed_members`.

   **Team name — one question, every create.** A Team's name is chosen here
   and belongs to that Team only; nothing stores it for the next one. When the
   user already named the team, skip this question. Otherwise ask with one
   AskUserQuestion, built from the bare plan's result:
   - The plan succeeded: first option is the plan's team name — show the
     member names it produces (`<name>-architect`, …, renamed or not), marked
     "(Recommended)".
   - The plan refused with `refused: "team-name-unusable"` and
     `needs_user_choice: true`: first option is "Use `<suggestion>`
     (Recommended)", the refusal's own `suggestion`. Never compute or
     sanitize a name yourself.
   - Then up to two recent names from `<ah> roster history` for this repo
     (their `alias` field) that differ from the first; free text via Other.

   A plan that refused with `needs_user_choice: false` has no name to offer:
   tell the user its `message` and stop. If the user keeps a successful bare
   plan's name, keep every later phase bare. Otherwise re-run the plan with
   `--team <chosen>` and pass the same `--team` to `--spawn` and `--commit`;
   a chosen name that is refused again gets the question again.

   **Roster — only when the plan lists `named_rosters`.** A create with no
   `--roster` builds from the selected roster — `WRANGL_ROSTER`, else
   `activeRoster` (set with `/agent-roster use`), else the default block. The
   plan's `selection` says which and why (a plan without one is the default
   block); name the roster when you show the plan. `--roster <r>` on every
   create phase overrides it (`default` is the default block); a team is
   never built from `rosters.<name>` because of its `--team`. When the bare
   plan carries `named_rosters`, ask which roster to build from as a second
   question in the **same** AskUserQuestion call as the team name: the
   selected roster first, then up to three of the others (the default block
   among them), with Other for the rest. A different choice adds `--roster
   <r>` to every later phase (re-run the plan with it). Without
   `named_rosters`, do not ask.

   **Models — only when the plan lists `members_needing_model`.** Each entry
   is a member about to launch with no model, and its model is the user's
   choice. Ask one question per listed member in the **same** AskUserQuestion
   round as the team name and roster questions (at most 4 questions per call;
   further calls if needed):
   - First, the entry's `fallback` when it has one, labelled with its source
     and not marked "(Recommended)", e.g. "opus — highest defined
     (myrepo-architect)" — except for a legwork role (task-runner or a custom legwork role),
     whose first option is `haiku (Recommended)`, then the fallback, then `allowed`.
   - Then the entry's `allowed` models, up to 4 options in all; a legwork
     member lists haiku before the reasoning models. Other takes the rest.
   - Each answer adds `--member-model <name>=<answer>` to every later phase
     (the re-run plan, `--spawn`, `--commit`), carried the same way as
     `--roster <r>`.

   **Codex trust — only when the plan has `needs` items of kind
   `codex-trust`.** Each item names a `root` Codex does not trust yet and the
   `command` that grants it. Granting is the user's decision, so ask in the
   **same** AskUserQuestion round as the questions above, one question per
   distinct `root`: "Trust Codex for `<root>`?" with options "Trust it" (run
   the item's `command`, which backs up Codex's `config.toml` first and edits
   only that root's table) and "Don't trust it". A "no" writes nothing: the
   Codex members of that root are not launched — list them as skipped,
   `codex-untrusted`, and do not move them to another harness. Ask again at
   the next create; the answer is not remembered. After a "yes", run the
   `command`, then re-run the plan: the item is gone once the root is trusted.
   `<ah> roster doctor` reports the same root as a `codex-trust` row; `--fix`
   never grants it.
   - Never launch from a plan that still lists `members_needing_model`:
     re-run the plan with the flags first.
   - If you cannot ask (a top-level session with no one to ask — `claude -p`,
     the SDK, headless), use each entry's `fallback`: `--member-model
     <name>=<fallback.model>`. If any fallback is null, stop and report the
     members. A subagent never does this; it returns the plan to its caller.

   **Skipped members — `skipped_members`.** A legwork member with no model is
   not launched while task-gopher is installed; every phase lists it under
   `skipped_members` and it is never recorded. Never ask about it: send its
   legwork to `task-gopher:task-gopher` subagents.

   **Before the bare `--plan`:** if `task-gopher:task-gopher` is not among
   your available agent types (for example the plugin is installed but not
   enabled), add `--no-legwork-handoff` to every create phase, carried like
   `--roster <r>`. Model-less legwork members then arrive in
   `members_needing_model` and are asked about like any other member.

   `--spawn` refuses with `refused: "member-model-undefined"` if a launched
   member still has no model: follow its `message` as above.

   **Second-Team collision.** A bare `create` (no `--team`)
   can fail because a *different*, live orchestrator already owns the default
   Team here — the CLI cannot read stdin to ask, so it refuses and hands back
   the live Team's name and pid plus an auto-derived candidate team name. Do
   not retry with `--team <candidate>` on your own judgment: surface it to the
   user with **AskUserQuestion**, offering the candidate as the first option
   ("Start a second Team named `<candidate>`") and free-text override as the
   second ("Use a different name"). Re-run `create --plan --team <name>` (the
   accepted or overridden name) only after the user answers — the candidate
   never applies unconfirmed. Every later step then needs that same
   `--team <name>` (SKILL.md § Global flags and `--team`).
2. **`manual` mode**: before each spawn, show the intended placement (name,
   role, transport) and let the user override it (different pane, skip it,
   change the name) before proceeding. `auto` mode spawns straight through.

   `--spawn` only launches the panes — it writes nothing; the Team does not
   exist until the follow-up `create --commit --verified <json> --transport
   <t> --roster-level <L>` call persists `team.json`, and until then
   `resync`, `move`, and `disband` cannot see the members at all.
3. **Spawn.**

   **`auto` mode:** run one command:

       <ah> roster create --spawn [--team <name>] [--roster <r>] [--member-model <name>=<m>]... [--mode <m>] [--roster-level <L>] --cwd <repo root>

   with the same `--team`/`--roster`/`--member-model` as the plan, and `--mode` only when § 0 gave one.

   It resolves the roster, runs the layout phase, asserts one distinct
   non-empty target id per peer-routed member before launching anything, then
   launches every peer-routed member's `agent start`/`send-keys` concurrently
   (herdr retries a single `pane_not_available`-class failure once, in-process;
   tmux and terminal never retry). It returns one JSON result: `level`,
   `transport`, and `members[]` — each entry carries `role`, `name`, `kind`,
   `args`, `model`, `route`, `autoMode`, `transport_id`, and `launch_status`
   (`ready`|`dispatched`|`blocked-at-startup`|`failed`; `null` for
   subagent-routed members), plus `error` when `failed`. `blocked-at-startup`
   is a **success**, not a failure: the agent is live and queryable but is
   sitting on its own first-run prompt (a non-Claude kind's "do you trust this
   directory?" gate). A non-Claude member stopped at a prompt spawn
   recognises carries a `blocked` object: relay it (§ Relaying a prompt). A
   Claude member's is resolved deliberately with `herdr agent read <name>`
   then `herdr agent send-keys <name> <key>` — nothing answers it for you, by
   design. `partial: true` iff any peer-routed member's
   `launch_status` is `failed` — a `dispatched` member (tmux only) is not
   partial, see step 4. Skip straight to step 4 with this `members[]` — do not
   recompute placements or drive `layout-splits`/`layout` commands yourself in
   `auto` mode. A member whose launch `failed` follows § When a role
   can't take the work.

   Each member is launched in its work tree; the output's `members[].work_root`
   shows which. `create --cwd <work tree>` binds the whole team to one work
   tree; to give a stream's members the stream's worktree, open the stream and
   spawn each member with `spawn-one <role> --stream <name>`. A role that
   writes code is refused in the main checkout once the team's gate document
   exists, unless `--allow-main` is given.

   **`manual` mode:** spawn every peer-routed member in two batched phases
   yourself, exactly as below. Do not run one member's full sequence before
   starting the next — that serializes an `agent start` wait per member.

   **3a — Layout.** For the `herdr` transport, `spawn.layout` is empty and the
   plan carries a top-level `layout_plan`. Run **one** command:

       <ah> roster layout-splits --mode <layout_plan.mode> --pane-count <layout_plan.pane_count> --cwd <repo root>

   It inspects the live geometry, computes every decision, and performs every
   split itself. Read `panes` from its JSON — the new pane ids, in creation
   order.

   - **exit 0** — `complete: true`, you have all `pane_count` ids.
   - **exit 3** — partial. `panes` holds the ids that *did* get created and
     they are real: use them. `failed_at` and `error` say which split failed
     and why, and `attempted` carries the decision it was about to run. Retry
     that one split with `layout-splits --apply --target … --direction …`; if
     it fails again, carry the members that have no pane into step 5's
     partial handling. **Do not discard the panes you already have** — they
     cost real work and their members can still be launched.
   - **exit 2** — nothing was done; the message says why. No panes were
     created; treat the layout phase as failed and stop before 3b.

   **Do not drive the split loop yourself, and do not compute targets or
   directions yourself.** `layout-splits` owns both. The `layout_plan`
   command templates in the plan document the contract and are the fallback
   if `layout-splits` is unavailable; they are not a second way to do this.

   **Your own pane is one of the panes being laid out**, and may be split
   more than once. That is correct; do not "protect" it.

   In `manual` mode, drive it one iteration at a time with `--next` /
   `--apply` — see § Create manual-mode layout below.

   For `tmux`, phase 3a: issue every member's
   `spawn.layout` commands **in a single message**, one tool call per member,
   so they run concurrently. Extract each target id using its
   `target_source`: `kind: "json"` means read the given path out of the JSON
   response; `kind: "stdout"` means take stdout and trim it.

   For `terminal`, there is no layout phase.

   **Assert before continuing:** you must hold one non-empty target id per
   peer-routed member — for herdr, exactly `layout_plan.pane_count` distinct
   pane ids, minus any that failed after a retry or were deliberately skipped
   in `manual` mode; for tmux, one per member with a `layout`. If a tmux
   target is missing, empty, or duplicated, re-run that member's `layout`
   commands **one at a time**; if a member still yields no target, treat it
   as a member that did not come up and carry it into step 5's partial
   handling.

   Assign the pane/target ids to peer-routed members in plan order, then
   continue to 3b unchanged.

   If any peer-routed member's `auto_mode` is `bypassPermissions`, say once
   before launching that it can leave that session stuck at a startup
   confirmation screen instead of ready, and suggest `auto` instead.

   **3b — Launch.** Substitute each member's target id for its
   `target_placeholder` inside its `launch` commands, then issue every member's
   `launch` **in a single message**, one tool call per member. Members with
   `target_placeholder: null` need no substitution.

   If a launch reports that the pane is not available or not at a prompt, retry
   that one member's launch once before treating it as failed — the shell may not
   have reached its prompt yet.

   Subagent-routed members are never spawned here — they stay ordinary Agent-tool
   dispatches, recorded in the Team with `name`/`ref`/`transport_id` null.

   In `manual` mode, show the intended placement and allow an override
   **before** phase 3a, per member; never a per-member pause between 3a and 3b.
4. **Check in.** Call `ListAgents` and match each member's `name` as
   `--spawn` printed it (renamed or not). **Poll every 2 seconds, give up at 60 seconds** — fixed interval,
   not backoff; this is not configurable. A member `--spawn` reported as
   `dispatched` (tmux only — `send-keys` has no readiness signal to wait on)
   is *expected* to still be checking in here; it is not a partial and needs
   no special handling — poll it exactly like a `ready` member. A non-Claude
   member is never in `ListAgents`: it is checked in once `herdr agent get
   <name>` says it is live — `blocked` included; readiness is not required.
5. **Commit.** Build the `verified` member array (one object per roster
   member: `role`, `name`, `ref` from ListAgents, `route`, `kind`, `args`, `model`,
   `effort`, `auto_mode`, `transport_id`, `checked_in`; subagent-routed members
   get `name`/`ref`/`transport_id` null and `checked_in` set now). One member,
   as a sample (`kind` and `args` appear only for a non-claude member):

   ```
   {"role": "architect", "name": "<member name>", "ref": "<ref from ListAgents>", "route": "peer", "model": "<model>", "effort": "<effort>", "auto_mode": "<auto mode>", "transport_id": "<id>", "checked_in": "<iso time>"}
   ```
   **`route: pane` members never appear in `ListAgents`** — they are not
   Claude sessions and have no `ref`; check them in with `herdr agent get
   <name>` instead and leave `ref` null. **Carry `kind` and `args` through
   verbatim** (both are absent for a `claude` member, and absent is correct —
   do not write `kind: "claude"`). A committed member missing its `kind` reads
   as `claude` and then reports dead forever. In `auto` mode, build this directly
   from `--spawn`'s `members[]` (`role`, `name`, `route`, `kind`, `args`, `model`,
   `autoMode`, `transport_id` are already there) plus each member's
   `ref` from `ListAgents` — do not recompute the rest by hand. Run
   `<ah> roster create --commit --transport <t> --roster-level <L> --verified
   '<json>'` with the same `--team`/`--roster`/`--mode` as the spawn. The
   orchestrator pid it records is your own Claude session, which the verb proves
   from the process above the command, so pass no pid. `--orchestrator-pid` and
   `CLAUDE_PID` are only checked against it: one that names another process is
   refused (`orchestrator-pid-mismatch`), and a plain terminal or a member's
   session is refused (`owner-unproven`, `member-cannot-create`).
   On a full success, report the Team id and every member. **On partial
   success — the default — commit anyway, and tell the user exactly which member(s) never checked in and that the
   Team is degraded**; do not silently pretend a missing member exists, and
   do not block or tear down on a partial check-in unless the user says to.

**§ Create manual-mode layout.** In `manual` mode, run the layout phase one
iteration at a time. For each of the `layout_plan.pane_count` iterations:

1. `<ah> roster layout-splits --next --mode <m> --pane-count <n> --created '<ids so far, JSON>'`
   — this reads the live geometry and returns the decision **without
   splitting anything**.
2. Show the user: the iteration (`split 2 of 4`), the target pane id and its
   current size in cells, whether it is this session's own pane, the
   direction, and the mode that chose it.
3. Offer: **accept**, **change direction** (`right`/`down`), **change
   target** (any pane id in the returned geometry — including panes this
   loop did not create), or **skip this split**.
4. Unless skipped, run
   `<ah> roster layout-splits --apply --target <id> --direction <dir> --cwd <repo root>`
   and append the returned `pane_id` to your list.

The next iteration re-reads geometry, so an amendment is absorbed rather than
compounding.

**Skip** means that member gets no pane: it is carried into step 5's partial
handling as a member that did not come up, exactly as a failed split would
be. Say so when offering the option — a skip is not free, it degrades the
Team.

This pause is inside 3a, before each split — never a pause between 3a and 3b,
which would re-serialize the batched launch phase.

If you hit a genuinely ambiguous case here beyond a plain partial check-in —
a transport that silently no-ops, a member that comes up under an unexpected
name, anything the plan above doesn't cover — stop and report it upward
rather than improvising; this area is an escalation candidate, not a place
for invented judgment calls.

## Question tag

Every `AskUserQuestion` in this file carries `metadata.source` `ah:bootstrap:agent-team:create`; the hook refuses an untagged ask from the Orchestrator.
