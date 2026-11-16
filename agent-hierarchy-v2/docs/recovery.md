# Taking over a team

This page is for you when the Orchestrator session that owns a team has died, was closed or was restarted, or the
machine rebooted. After reading it you will be able to give the team back to a session, bring its members back, and
get a team ready before a planned restart.

First, how ownership works. A team's file records its Orchestrator: the session
and the process it runs in. A team whose recorded process is dead is
**orphaned**. ah never hands an orphan to a new session on its own, because that
could take a team from someone who is still using it. You choose how it is
claimed.

Run the commands below in your Orchestrator's Claude session (type them after a leading `!`, or ask the
Orchestrator), in the team's repository. Every command is in the [CLI reference](./cli-tools.md).

## See what you have

```
ah roster teams
```

Each team shows whether you own it, and an orphaned team lists the members that
are still alive.

## Which way to take it back

| Situation | Do this |
|---|---|
| The same Claude session was restarted | `claude --resume <session id>`. Nothing else. |
| A different session should run the team, and its members are still alive | `ah roster adopt --orchestrator-pid <pid> --team T` |
| The members are gone (reboot, closed terminal) | `ah roster resume --team T --yes`, which also makes the running session the Orchestrator |
| The team record is junk and nobody wants it | `ah roster reap`, then `ah roster reap --commit` |

### Restart the same session

Start it again with `claude --resume <session id>` (or `claude --resume` and
pick it from the list). When the session starts, and again at its next Stop, the
team's recorded process follows the new one. No `adopt` is needed.

### A different session: `adopt`

```
ah roster adopt --orchestrator-pid <pid> --team T
```

`<pid>` is the process id of the Claude session that should own the team (its
Bash tool exposes it as `CLAUDE_PID`). `adopt` changes only who the team belongs
to. It is one call per team. It refuses:

- a pid that is not a live process;
- a team that a different live process still owns. It is for orphaned teams,
  not for taking a team from a running session.

A new session never takes an orphan by itself. Its start-up note tells it to ask
you, and then to run `adopt`.

### Bring the members back: `resume`

When the members' sessions are gone, `resume` relaunches them from the newest
snapshot. Without `--yes` it only prints the plan.

```
ah roster resume --team T
ah roster resume --team T --yes
```

Each member goes back into its own recorded session, so its conversation
continues, from the directory it worked in. The session that runs `resume`
becomes the team's Orchestrator. If another live Orchestrator already holds the
team, `resume` refuses and launches nothing.

Run `resume --yes` inside the Orchestrator's own Claude session, from its Bash
tool, in the directory the session started in (or one that shares its
hierarchy directory): the session's start is recorded in that directory's
state, and from another repository `resume` may not find it and refuses with
`owner-unproven`. `resume` finds that session from the process above the command; the pid in
`CLAUDE_PID` or `--orchestrator-pid` is only checked against it, never used in
its place. It records the session's pid with the process's start time, so the
owner-only verbs below work in that session straight away. It refuses, writes
nothing and launches nothing when:

| Refusal (`refused` in the JSON) | Why | What to do |
|---|---|---|
| `owner-unproven` | No Claude session above the command could be proved: a plain terminal, or the process table could not be read. | Start or resume a Claude session and run `resume` there. |
| `member-cannot-resume` | The command runs under a member's session, or the shell carries a member marker (`proof` says which: `member-ancestor` or `member-marker`). | Run it from the Orchestrator's session. |
| `orchestrator-pid-mismatch` | `--orchestrator-pid` or `CLAUDE_PID` names a pid that is not the session running the command. | Drop the flag, or give the session's own pid. |

Each refusal also prints an `owner proof:` line saying which step failed, and the
JSON carries `proof` with its code. A live holder whose record has no start time still counts as holding the team:
restart that session, or run `resume` from it.

The outcome per member (`resumed`, `fresh`, `alive`, `no-worktree`, `skipped`, `failed`) is
explained in [Upgrading](./upgrading.md#4-move-the-running-members-onto-the-new-hooks).

`resume` does not run by itself. Mail nobody had acknowledged is not resent and
not lost: it is still in the member's mailbox, and the resumed session picks it
up when it starts.

### Clean up orphans: `reap`

`ah roster reap` on its own lists orphaned team records and deletes nothing.
`ah roster reap --commit` removes them. A team that still has live members is
kept, and the result says which `adopt` command to run for it.

`reap` judges a team by its recorded process alone. Adopt a team you still want
before you reap.

### A member the close held

`ah roster dismiss --close` and `disband --close` leave a member alone that is working now, and every member while the message
store cannot be read, and exit 3. The result lists each held member under `held` with its status. `busy`: wait for its turn to end.
`store-unreadable`: wait for the store. To close a held member anyway, pass `--ignore-release <name>`; a false `busy` from a member
interrupted with Esc is the usual reason. Every other member closes, and `release.warnings` names what it leaves behind: an open
request, uncommitted work, or unshipped commits by branch and sha. That work stays in its tree. The check is in
the [CLI reference](./cli-tools.md#release-check).

## Snapshots

A snapshot is a file listing the team's members and the session each one was in.
It is taken:

- by `ah roster snapshot --team T`, whenever you ask;
- by `ah roster disband --close`, before it closes anything;
- when an Orchestrator session ends while it owns a team that has members.

The newest 10 per team are kept, in the `snapshots/` folder of the hierarchy home
that holds the team file. A member whose session id was not known is relaunched
fresh. Codex members are recorded but relaunched fresh. The file format is in
[team-file.md](./team-file.md#snapshots).

## Before a restart: `checkpoint`

`ah roster checkpoint --team T` (or `--all` for every team you own) gets a team ready for a
restart of the machine or the terminal. It saves a snapshot, then sends a request to each
member that is not idle and clean, asking it to commit its work as `wip(resume): …` on its
own branch, to stop every background process it started, and to answer with one line:
`Checkpoint: ready branch=<b> sha=<sha> inflight=none` or `Checkpoint: not-ready reason=<text>`.
A member that is idle, has no open request and has a clean work tree on a branch is asked
nothing. The verb writes a record under `checkpoints/<team>/` in the hierarchy home and
adds one `Resume point:` line to the team's progress rollup. It never waits, pushes or
closes anything. Where the mail send prints a `delivery: relay` line, the verb repeats it
in its `relay` list; send that message yourself.

`ah roster checkpoint --status --team T` then says whether a restart is safe. It exits 0 when
every member is `ready`, `idle-clean` or gone with a clean tree and your own work tree is
clean, and 3 otherwise; the output names each blocker. Restarting anyway is your call, not
the verb's. The record format and the statuses are in
the [CLI reference](./cli-tools.md#checkpoint).

## Verbs that only the Orchestrator can run

Every verb that changes a team runs only in the Claude session that owns
it, proved by the process above the command, not by a flag: `create`, `spawn-one`,
`spawn-ad-hoc`, `dismiss`, `disband`, `untrack`, `reap`, `adopt`, `stream-open`,
`stream-done`, `checkpoint`, `quit`, `stop`, `resume` and the others listed under
[Invocation rules](./cli-tools.md#invocation-rules). A verb on a team
that already exists needs the session to be that team's Orchestrator; `disband` of a
team whose Orchestrator is gone, and `adopt` of a team whose holder is gone, need no
owner. A subagent is refused before the command runs. A member, a plain
shell, or a session that is not recognised is refused with the verb's name, and a second line, `owner proof: …`, says which step of
the proof failed (for example that the session is the Orchestrator of another
team, or that the team's recorded process is not this session). If that is your
own Orchestrator session right after a `resume`, run `ah roster resume --team T --yes` once more in it, or see
[Troubleshooting](./troubleshooting.md#after-an-upgrade-a-resume-or-the-gate).
