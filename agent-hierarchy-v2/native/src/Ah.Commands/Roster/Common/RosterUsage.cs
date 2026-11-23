namespace Ah.Commands;

/// <summary>
/// The usage block of the roster CLI: what <c>--help</c> and a bare
/// <c>roster</c> print. A verb's own entry is its <c>ah roster &lt;verb&gt;</c> line and the lines indented under it.
/// </summary>
internal static class RosterUsage
{
    private const string FullText = """
agent-hierarchy — the CLI behind both /wrangl:agent-roster (roster template) and
/wrangl:agent-team (live team): deterministic roster + team file I/O.
Modelled on ah msg. Interactive prompting (AskUserQuestion, ListAgents
polling, actually spawning sessions) is the SKILL.md's job; this CLI does
validation and reads/writes only, so validation lives in one place.

  ah roster show   [global|repo|repo-user] [--level L] [--roster <r>] [--cwd <path>]
  ah roster init    [level] [--level L] --route <peer|subagent> [--roster <r>] [--cwd <path>]
  ah roster add     [level] [--level L] [--roster <r>] --role <R> [--model M] [--effort E]
                      [--route peer|subagent|pane] [--kind K] [--args '<json>'] [--auto-mode A]
                      [--on-missing auto|prompt|never] [--cwd <path>]
                      (writes the roster template and spawns NOTHING. To launch the member,
                       use spawn-one, or spawn-ad-hoc.)
  ah roster edit    [level] [--level L] [--roster <r>] --member <NAME> [--role R] [--model M]
                      [--effort E] [--route ...] [--auto-mode A] [--on-missing auto|prompt|never] [--cwd <path>]
                      (--model "" and --effort "" clear the field)
  ah roster remove  [level] [--level L] [--roster <r>] --member <NAME> [--cwd <path>]
  ah roster roster list [--json] [--cwd <path>]
  ah roster roster copy <src> <dst> [--level L] [--dry-run] [--cwd <path>]
  ah roster roster delete <name> [--level L] [--dry-run] [--cwd <path>]
  ah roster roster use <name>|default [--level L] [--cwd <path>]
  ah roster roster use --clear [--level L] [--cwd <path>]
                      Named rosters are `rosters.<name>` blocks. show/init/add/edit/remove and
                      create use --roster <r>, else WRANGL_ROSTER, else the most specific
                      activeRoster, else the default `roster` block; `default` names that block.
                      `use` writes activeRoster (default level: repo-user in a repo, else global).
  ah roster create  [--plan] [--commit --verified <json> --transport <t>
                      (--verified: JSON array of member objects from the spawn/check-in
                      cycle, OR a JSON array of member-name strings hydrated from the
                      --roster-level roster)
                      --roster-level <L> [--partial (accepted, ignored: partial is derived)]
                      [--orchestrator-pid <pid>]] [--cwd <path>]
  ah roster create  --parent <X> --stream <ws> [--plan|--commit|--spawn]
                      (a sub-orchestrator's team: the stream of team X, copied; each flag needs the
                      other; the team is named <X>-<ws> unless --team says otherwise)
  ah roster create  --spawn [--roster-level <L>] [--cwd <path>]
                      (every create phase takes [--team <T>], the team's name — default the
                      repo basename; [--roster <r>], build from `rosters.<r>` — default the
                      `roster` block; [--mode <auto|columns|grid>], the team's layout — an
                      explicit --mode on --spawn/--commit is stored as the default for future teams;
                      and [--member-model <name>=<model>], repeatable, the model a member runs on
                      this time, which --spawn requires for every launched member with none stored;
                      [--param <name>=<value>], repeatable, a value for a param a role declares,
                      written at repo-user (as `param set` would) by --spawn/--commit; --plan lists it unwritten.
                      A legwork member with no model is skipped while task-gopher is installed:
                      `skipped_members` lists it, and its work goes to task-gopher subagents.
                      [--no-legwork-handoff] counts task-gopher as not installed, for a driver that
                      cannot dispatch it; [--names-in-use <name>], repeatable, a live session name
                      a member's derived name is renamed away from)
  (`--spawn` launches only; `--commit` persists. Both are required, in that order.)
  ah roster next-split --mode <auto|columns|grid> --pane-count <N> --self <pane-id>
                      --created '<json array of pane ids>'
                      --geometry '<json array of {pane_id, rect}>'
  ah roster layout-splits --mode <m> --pane-count <n> [--self <id>] [--cwd <p>]
                      (or --next --created <json>, or --apply --target <id> --direction <right|down>)
  ah roster disband [--plan] [--cwd <path>] [--team <T>]
  ah roster disband --close --confirm --plan-token <tok> [--ignore-release <name>]... [--allow-global] [--cwd <path>] [--team <T>]
  ah roster dismiss <name> [--plan] [--cwd <path>] [--team <T>]
  ah roster dismiss <name> --close --confirm --plan-token <tok> [--also-config] [--level L]
                      [--ignore-release <name>]... [--allow-global] [--cwd <path>] [--team <T>]
  ah roster untrack <name>|--all [--plan|--commit] [--keep-sessions] [--also-config] [--level L]
                      [--cwd <path>] [--team <T>]
                      (forgets the record and closes nothing; --keep-sessions leaves the session running)
  ah roster resync  [--dry-run] [--cwd <path>]
  ah roster transfer <member> --to <team> [--stream <ws>] [--dry-run]
                      (hands a member to a lane's sub-orchestrator, or back to this team's Orchestrator with --to <this team>;
                      with --stream the member also moves into that stream's tree. A running member whose session is in another tree is
                      refused (relaunch-needed): dismiss it and spawn it again into the stream)
  ah roster move    <name> --tab <tab_id> [--split right|down]
                      <name> --new-tab [--workspace <id>]
                      <name> --new-workspace
                      <name> --stream <ws> [--team <X>-<ws>]  (a running member whose session is in another tree is refused
                                          (relaunch-needed): dismiss it and spawn it again into the stream)
                      [--dry-run] [--cwd <path>]
  ah roster snapshot [--team <T>] [--cwd <path>]
    Record the team's members (launch fields, session ids, layout) in a snapshot file; the newest 10 per team are kept.
  ah roster checkpoint [--team <T> | --all] [--note <text>] [--cwd <path>]
    Snapshot the team and ask each member that is not idle and clean to commit, stop its processes and answer; writes a record, never waits.
  ah roster checkpoint --status [--team <T> | --all] [<record>] [--cwd <path>]
    Say whether a restart is safe from the newest record: exit 0 when it is, 3 when it is not.
  ah roster quit [--team <T> | --all] [--wait <seconds>] [--note <text>] [--cwd <path>]   (alias: pause)
    Put the team and its lanes on hold at the user's order: every automation for them is off, each member is asked to save its work and write
    a resume plan, and the saved plan's path is printed. --wait is the whole window from the hold's start (default 420). One call waits at most
    100 s: while answers are missing inside the window it prints waiting_on and next, and exits 4; run it again. Exit 1 when a brief was not
    sent or a lane was refused, 4 still winding down, 3 sealed with a member unanswered, 0 otherwise. --all takes every top team this session owns.
  ah roster quit --status [--team <T>] [--cwd <path>]
    Say whether the team is on hold: exit 0 when running, 5 when held.
  ah roster stop --now [--team <T> | --all] [--confirm] [--grace <seconds>] [--reason <text>] [--cwd <path>]
    Emergency stop at the user's order: the team goes on hold, each member is told to stop all its work, and the background jobs a member
    registered with a verified pid are stopped. No session is killed. Without --confirm it prints what it would do and changes nothing.
    Exit 0 when every registered job stopped and every target was signalled (by pane or by mail); 4 when a job survived or a target could be
    signalled neither by pane nor by mail; 2 on a refusal or error. The members' own session processes are listed under session_processes
    and never affect the exit code.
  ah roster resume --all [--yes] [--cwd <path>]
    After a reboot or herdr restart: bring back every top team this session owns, lanes included. A quit hold resumes as --hold does, a stopped team is listed with its
    command, an unheld team is recovered and its missing members relaunched with no brief, each on its own session (session: resumed:<id> or fresh); a member
    left in a bare shell gets a new herdr pane split off that one, which is closed (blocked when it cannot be). Teams it does not own are listed under not_owned, never
    touched. Without --yes it prints the plan. Exit 3 when a relaunch failed or bare members wait on `ah roster recover enable`; 1 when a brief or team failed.
  ah roster resume --hold [--team <T>] [--yes] [--cwd <path>]
    Resume a held team and its lanes (the top Orchestrator, on the user's order). Without --yes it prints the plan and changes nothing. A quit left
    winding down is sealed first; a team whose Orchestrator is gone is taken over. It can pass 120 s: give it a 600 s timeout or run it in the background.
  ah roster resume [<snapshot>] [--team <T>] [--yes] [--cwd <path>]
  ah roster resume --checkpoint [<record> | --team <T> | --all] [--yes] [--relaunch <name> | --relaunch-missing] [--cwd <path>]
    Relaunch a snapshot's members into their sessions. Without --yes it prints the plan and launches nothing.
  ah roster recover show|hold|release|now <member> --team <T> [--cwd <path>]
  ah roster recover enable|disable|status --team <T> [--cwd <path>]
  ah roster recover --team <T> [--relink-lane <child team>]... [--cwd <path>]
    After a restart: the team's owner re-stamps each member row whose process is gone from the one live session in its pane; a row with no such session is reported down with its spawn-one --resume remedy.
    --relink-lane: the owner stamps T's current team_id into the named lane child's parent link (the child's link must name T and T's stream must record a lane lead); a link naming another team_id is overwritten.
    The daemon's relaunch of a member that runs without ah's launch. hold/release/now/show: the team's Orchestrator or the user.
    enable: the user only. disable: the user or the team's Orchestrator. status: any caller. Recovery is off until enabled, and while it is off nothing is stopped or relaunched.
    The switch is an opt-in guard against accidents, not a security boundary.
  ah roster spawn-one <role> [--member <name>] [--model M] [--team <T>] [--cwd <path>] [--dry-run]
                      [--allow-global] [--orchestrator-pid <pid>] [--names-in-use <name>] [--stream <s>]
                      (--model: required when the member has no model stored; this launch only)
                      (--team names the team to join; an unknown name creates that scope —
                       no separate create step, and nothing to ask about.)
  ah roster spawn-ad-hoc <role> [--model M] [--effort E] [--route peer|pane] [--kind K]
                      [--args '<json>'] [--auto-mode A] [--on-missing ...] [--team T]
                      [--dry-run] [--allow-global] [--orchestrator-pid <pid>] [--names-in-use <name>]
                      [--stream <s>] [--cwd <path>]
                      (spawn a member the roster does not define, or one whose
                       parameters diverge from it. Writes ONLY the team file — never the roster.)
                      (--stream on either spawn verb: the member joins stream <s>, launches in the
                       stream's work tree and, under herdr, lands in the stream's tab.)
                      (--allow-main on either spawn verb: let an implementor work in the main
                       checkout of a repository whose worktree gate is on; it is refused otherwise.)
                      (--replace-live on either spawn verb: spawn into a work root whose recorded
                       member is live or of unknown liveness; it is refused otherwise.)
  ah roster stream-open <name> [--branch <b>] [--base <ref>] [--no-worktree] [--dry-run]
                      [--team <T>] [--orchestrator-pid <pid>] [--cwd <path>]
                      Records a stream in the team file; adds its worktree + branch off --base (default: golden when it resolves, else the branch checked out in the caller's own work tree, else, on a detached HEAD, its checked-out commit) at
                      <main checkout>/.claude/worktrees/<name> and, under herdr, its tab. Spawns nothing.
  ah roster stream-status [<name>] [--plain] [--team <T>] [--cwd <path>]
  ah roster stream-list [--cwd <path>]   (read-only: <branch>\t<worktree or -> per live stream, every team)
                      The board: each stream's glyph, worktree, peers and open exchanges.
  ah roster stream-done <name> [--covered <reason> | --merged-into <branch> | --abandon <reason>] [--dry-run] [--team <T>] [--cwd <path>]
                      Marks the stream done (✓); closes no pane. Against golden (git config ah.releaseRef, else origin/HEAD,
                      origin/main|master, main|master) it refuses a branch with unshipped commits unless one flag says why and
                      refuses a dirty worktree; once every member of the stream is gone it removes the clean worktree and moves
                      the branch to refs/archive/<YYYY-MM-DD>/<branch>. Re-run after dismissing the members to archive.
  ah roster lane-caps --stream <ws> [--members <N>] [--streams <N>] [--team <T>] [--cwd <path>]
  ah roster lane-caps --max-lanes <N> [--team <T>] [--cwd <path>]   (the most live sub-orchestrators, 1..16, default 6)
  ah roster lane-caps [--team <T>] [--cwd <path>]   (print max_lanes and the per-lane caps)
                      Sets a lane's member and open-stream caps (1..16) in the managing team's file. Orchestrator only; a lane leader cannot run it.
  ah roster duty on <member> | off [--cause <text>] [--team <T>] [--cwd <path>]
                      Puts a deputy-chief member on duty (it holds orchestrator@<T>, the chief holds chief@<T>) or takes it off.
                      Owner only; refused on a lane team; off with no deputy on duty changes nothing.
  ah roster pause-member <member> | --team <T> [--cwd <path>]
  ah roster resume-member <member> | --team <T> [--cwd <path>]
                      Pause one member (or every member of T): ahd sends it no nudge or re-delivery and raises no alarm on its requests
                      until resume-member, which restarts their clocks. Each member is told by mail. Closes nothing (unlike quit/pause).
  ah roster progress item add|start|done|drop|reopen|retitle|link|note <stream> <item> [--title <T>]
                      [--commit <sha>]... [--branch <b>] [--issue <n>] [--note <T>] [--team <T>]
                      ah roster progress item import <stream> [--team <T>]   (also run as `ah progress item ...`)
                      Records a stream's plan (an owner verb). The stream's Checklist is rendered from it
                      as `- [x]` / `- [ ]` lines; do not edit that block by hand. `done` needs commits that
                      resolve. `import` opts an existing checklist into the plan, once.
  ah roster stream-label <name> [--self-pane <id> --self-state working|idle|blocked] [--team <T>]
                      [--cwd <path>]
                      Re-renders one stream's tab label; what the stream-label hook runs.
  ah roster stream-report --state working|blocked|review|paused|done --summary "<one line>"
                      [--note "<why>"] [--result <abs path to a --response.md>] [--team <T>] [--cwd <path>]
                      A sub-orchestrator's status: writes the workstream record of the sub-team it
                      owns (summary at most 160 characters, no backtick; --result only with done).
  ah roster approve <member> <rule> [--cwd <path>]
                      The Orchestrator lets one held call of a live member through a soft gate.
  ah roster permissions check|rules|add|sync|remove|diagnose <member>|show <member> [--cwd <path>]
                      The lifecycle allow rules, and why a member was denied.
  ah allow on|off|list [--team <name>] [--json] [--cwd <path>]
                      The switch for the launch-only permission rules ah writes for the members it spawns.
  ah roster close-approve --team <child> --verb dismiss|disband [<member>] [--plan-token <t>] [--cwd <path>]
                      The managing orchestrator's green light for a sub-orchestrator's close. Bare: prints
                      the plan and its token; with the current --plan-token: records the grant (30 minutes).
  ah roster teams   [--cwd <path>] [--orchestrator-pid <pid>]
  ah roster teams   --worktrees [--cwd <path>]
                      This checkout's own teams only: name, life (closed|open), member_worktrees. No liveness reads.
  ah roster team show [--team <name>] [--json] [--cwd <path>]
                      Prints the team's record, indented, or on one line with --json. Reads only.
  ah roster status  [--plain] [--now <ISO>] [--cwd <path>]
                      Computes the hierarchy status document from the database and prints it.
  ah roster close <team> [--preview] [--confirm-live <member>]... [--cwd <path>]
  ah roster reopen <team> [--cwd <path>]
                      The user only. Marks a team closed, or removes the mark; nothing is deleted. Running
                      members end only when each is named with --confirm-live.
  ah roster rename  "<display name>" | --clear [--team <T>] [--cwd <path>]
                      The user or the team's Orchestrator. Sets the team's display name (1 to 60 characters, no
                      control characters), a label only; the team's name, addresses and lookups do not change.
  ah roster reap    [--commit] [--cwd <path>]
  ah roster scratch-sweep [--commit] [--cwd <path>]
                      (bare: lists orphaned team records — dead/null orchestrator pid,
                      age never a factor — and deletes nothing; --commit removes them.)
  ah roster history [--cwd <path>]
  ah roster create  --from <id|alias> [--team <T>] [--plan|--commit|--spawn] [--cwd <path>]
  ah roster adopt   --orchestrator-pid <pid> [--team <T>] [--cwd <path>]
                      --orchestrator-pid: cross-checked against this session's proved pid; a mismatch is refused.
  ah roster checkin [--team <T>] [--cwd <path>] [--orchestrator-pid <pid>]
  ah roster wake    [--cwd <path>]
                      Run in the background by a team's Orchestrator: blocks until ahd has an unacknowledged
                      event for it (or 20 minutes pass), prints it, and exits. Exit 3: the daemon is down.
  ah roster overdue [--cwd <path>]
                      Run in the background beside wake: the failsafe that exits when ahd missed a dispatch step.
  ah roster stall-diagnose <request id> [--team <T>] [--cwd <path>]
                      Read-only: why ahd did not wake the Orchestrator for a dispatch (six facts and a verdict).
  ah roster block-triage <tool_use_id|group key> --decision issue:<#N>|upstream:<url>|guidance|outward-confirm|none --note <why> [--team <T>] [--cwd <path>]
                      Owner only: records the decision on an untriaged classifier block (the group key is one `ah roster doctor` prints).
  ah roster checklist show [--all] [--state <S>] [--stale] [--since <ISO|6h>] | show <id> --trail
                    | add <what> [--role <R>] [--stream <S>] [--todo <T>] [--ref <id>]
                    | set <id> [--state <S>] [--owner <M>] [--todo <T>] [--ref <id>] [--note <T>]
                    | done <id> [--note <T>] [--commit <sha>]... [--team <T>] [--cwd <path>]
                      The Orchestrator's durable list of what is outstanding: every plan item of the team's streams plus
                      loose ends in the reserved stream _run. Check it first on every wake. --state takes a display word
                      (queued, dispatched, waiting-orch, blocked, waiting-user, dropped) or a stored name (in_progress,
                      waiting_orch, waiting_user, next, reported, in_review, approved). --trail lists every row of an item with its
                      cause and prints the command that undoes the last automatic move.
  ah roster timing [--item <id|slug> | --batch <sha>] [--plain] [--team <T>] [--cwd <path>]
                      Read-only: per work item and per ship, how long each stage took (spec, build, review, rework, orch,
                      blocked, qa, ship_wait) and which item and stage gated the ship. Bare: the last ship and the open items.
  ah ledger ship <sha> [--branch <b>] [--team <T>] [--cwd <repo>]
                      Records a ship that ran outside a Claude Bash call in the team's ledger, the same row a `git push`
                      leaves, and prints the ship's stage timing when it has one. It marks no item done.
  ah roster job start --kind <test|build|slot|smoke|bg> --label <T> [--eta <small|medium|large>] [--pid <N>]
                      [--tool-use-id <ID>] [--background] [--cap-secs <N>] | end <id> [--exit <code>]
                    | extend <id> --eta <small|medium|large> [--team <T>] [--cwd <path>]
                      Record a long-running run so ahd can time it: start prints the job id; end and extend act only on the
                      caller's own jobs. A job past its eta raises job-overdue to the Orchestrator.
  ah roster whoami  [--team <T>] [--cwd <path>]
                      Read-only: which team member this session's pane is, and its
                      orchestrator's pid / liveness / best-effort reply address.
  ah roster role set <name> [--class C] [--agent A] [--label L] [--description D] [--routes R] [--model M]
                      [--dispatch peer|model] [--shell self-care] [--params a,b] [--level L] [--scaffold repo|user]
                      [--dry-run] [--cwd <path>]
                      Defines or changes a role; --params names the params it declares ("" clears them).
  ah roster role set <name> --from <plugin>@<marketplace>:<role> [--label L] [--description D]
                      [--routes R] [--model M] [--dispatch D] [--level L] (--dry-run | --pin sha256:…)
                      Adopts one role from an installed role pack (a plugin carrying ah-roles.json).
                      The dry run shows what is trusted and prints the pin; the commit needs that pin.
  ah roster role trust <name> [--level L] (--dry-run | --pin sha256:…) [--cwd <path>]
                      Shows what changed in an adopted role's pack since it was trusted, and re-pins it.
  ah roster pack list [--json] [--cwd <path>]
  ah roster pack show <plugin>[@<marketplace>] | --path <dir> [--json] [--cwd <path>]
                      Read-only: installed role packs; one pack's roles, findings, what else the
                      plugin carries, and its content digest.
  ah roster param set <name> <value> [--level repo-user|repo|global] [--cwd <path>]
  ah roster param unset <name> [--level L] [--cwd <path>]
  ah roster param list [--json] [--cwd <path>]
                      The values roles' `params` name (role set --params): per name the most specific
                      level wins (repo-user, repo, global). set defaults to repo-user in a repo, else
                      global; unset to the level the value comes from. Stored as written, never checked.
  ah roster doctor [--cwd <path>] [--check] [--brief] [--fix [--statusline-tap on|off]]
                      Read-only self-check: one JSON object, one row per thing that can be
                      wrong. `--check` exits 1 when any row is red. Writes nothing without --fix.
                      `--brief` prints one text line per check instead (`ok <name>`, or
                      `<status> <name>: <detail>` for any row that is not ok); not with --fix.
                      `--fix` deletes statusline tap files older than 7 days; with
                      `--statusline-tap on` it also wraps the user-level statusLine command so
                      Claude Code's statusline input is copied to a per-session file first
                      (`off` restores the original byte for byte). A plain --fix never installs it.
                      (also re-registers the current session with its current cwd;
                      exits non-zero when still misplaced relative to the team's expected_root.
                      Resolves the session pid the same way as create --commit/teams — process.ppid
                      is the transient Bash-tool shell, never the session, at this call site.)
  ah roster deliver <name> --req <abs request path> [--ping <n>] [--wait-only] [--timeout <s>]
                      [--team <T>] [--cwd <path>]
                      Briefs a non-claude pane member through Herdr and waits for its turn to end;
                      its report comes back only as the response file beside the request.
  ah roster answer  <name> --prompt <blocked_by> --choice <id> --screen-hash <hash> [--team <T>]
                      [--cwd <path>]
                      Sends the answer the user chose to a prompt a non-claude member stopped at:
                      only that row's keys from the kind's table, and only while that screen is up.
  ah roster codex-trust [<dir>] [--yes] [--json]
                      Makes Codex trust the git root of <dir> (default: the cwd) by adding it to
                      Codex's own config.toml. Without --yes it only prints the plan and writes
                      nothing. With --yes it backs the file up as config.toml.ah2-bak, edits only
                      that root's table, and re-reads the result. A member session is refused.
  ah roster codex-trust --preapprove <dir> [--parent] [--via install|bootstrap|cli] [--yes]
                      Records your pre-approval for <dir>'s git root (--parent: every root under
                      <dir>), kept in codex-trust-preapproval.json. Without --yes it only plans.
  ah roster codex-trust --revoke <dir> | --revoke-all [--yes] | --list
                      Removes a pre-approval (--revoke-all deletes the file and needs --yes), or
                      lists trust, pre-approvals and the auto-answer log.
  ah roster pipeline-branch [show] | allow <branch> | revoke [<branch>]
                      The branches you said a pipeline run may work on directly (the repo's default
                      or release branch), per repository, kept in the state database. It refuses
                      nothing: the pipeline bootstrap asks you when the branch is not listed.
  ah roster tier    set <kind> <model> <haiku|sonnet|opus|fable> | remove <kind> <model> | list
                      How a non-Claude model compares with Claude's tiers; global config only.
  ah roster testing show | set <role> <text> [--level repo|global|repo-user]
                      The durable testing orders each member's directive prints (default level repo).

The roster is a read-only TEMPLATE for the whole team lifecycle. `init`/`add`/`edit`/
`remove`/`layout`/`alias` write roster level files and spawn nothing; every other command writes
only a team file. A session that owns a live team is refused a roster edit unless the USER
passes `--allow-roster-edit` — no code path may supply that flag on the user's behalf. A team with
no `--team` lives at `teams/<effective-prefix>.json`, not the shared `team.json`; a `team.json`
gives no scope: the import stores it as the default team, which holds the effective prefix while it is live.

`--team <name>` selects a named team's `teams/<name>.json`
in place of the default `team.json`, on every subcommand above that touches
a team file; omitted, everything resolves the default team.
`create --team <T>` writes `teams/<T>.json`.

`--level`/the positional bare word are equivalent; `add`/`edit`/`remove`
with neither default to the level the roster currently resolves from
(repo-user > repo > global) and print which level they chose.

`create` is two-phase, both file-I/O-only: `--plan` (default) resolves the
roster, refuses a live Team, clears a stale one, and reports the transport
plus each member's derived name and spawn shape — it spawns nothing.
`--commit` persists a Team the caller (the SKILL.md flow) has already
spawned and verified via ListAgents, writing team.json.

`resync` re-derives every peer member's herdr location from herdr's own live
topology and rewrites team.json; `move` executes a `herdr pane move` then
resyncs that one member. Bare `disband`/`--plan` also resyncs first, in
memory only (no write).
""";

    private const string VerbLinePrefix = "  ah roster ";

    /// <summary>A verb's line under its own name, such as <c>ah ledger ship</c>, for a verb ah also runs without <c>roster</c>.</summary>
    private const string AliasLinePrefix = "  ah ";

    /// <summary>The verb a line of the block starts, or null when it starts none.</summary>
    private static string? VerbOf(string line)
    {
        var prefix = line.StartsWith(VerbLinePrefix, StringComparison.Ordinal) ? VerbLinePrefix
            : line.StartsWith(AliasLinePrefix, StringComparison.Ordinal) ? AliasLinePrefix
            : null;
        if (prefix is null || line.Length <= prefix.Length || line[prefix.Length] == ' ')
        {
            return null;
        }

        var end = line.IndexOfAny([' ', '\t'], prefix.Length);
        return end < 0 ? line[prefix.Length..] : line[prefix.Length..end];
    }

    /// <summary>The text for <paramref name="verb"/> (null for the whole block), without the trailing newline.</summary>
    internal static string For(string? verb)
    {
        if (verb is null)
        {
            return FullText;
        }

        var picked = new List<string>();
        var taking = false;
        foreach (var line in FullText.Split('\n'))
        {
            if (VerbOf(line) is { } token)
            {
                taking = token == verb;
            }
            else if (taking && !(line.Length >= 4 && line.AsSpan(0, 4).IndexOfAnyExcept(' ') < 0))
            {
                taking = false;
            }

            if (taking)
            {
                picked.Add(line);
            }
        }

        return picked.Count > 0 ? string.Join('\n', picked) : FullText;
    }
}
