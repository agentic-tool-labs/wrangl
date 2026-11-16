# push regime — autonomous-pipeline reference

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Read this before the first push of a run. The things that are never done are in SKILL.md § Push regime.

Contents

- Push commands
- The CI-deploy check
- Outward commands
- When an outward action is refused

## Push commands

Commit after each step; push after each commit
(`git push --set-upstream origin <branch>` the first time, `git push origin <branch>`
after) to the run's feature branch. Feature branches merge on the remote (PRs into the
integration branch) with no stop; only a merge into `main` can wait for the user (SKILL.md
§ Bootstrap step 3b). ah records every push and merge; it blocks none.

## The CI-deploy check

**Once at bootstrap.** Establish that the
remote's CI does not deploy from arbitrary branches. If this is unknown, or
the answer is yes: **degraded mode** — commit locally, do not push, and take
one end-of-run push approval from the user instead. Unknown counts as yes.

## Outward commands

An outward command is one that leaves the machine: a push, a ship, a backup push. Four rules hold for all of them, in every run.

1. **Bare canonical forms only.** Issue the command exactly in its canonical form: `bash ~/bin/ship.sh <sha> native` for a ship, plain `git push origin <sha>:refs/heads/ah/wip/<team>/<member>/<sha12>` for a backup. The ordinary `git push origin <branch>` form (§ Push commands) is already bare. No redirect, pipe, `;`, `&&`, `cd … &&`, env prefix, `$(…)`, or wrapping in `bash -c`. A rule that allows the canonical form does not allow a wrapped one, and a wrapped one that is refused counts against the session. The script writes its own log, so no capture is needed.
2. **Never edit a test, ship script, hook, settings or policy file during a run** to make an outward action pass. If one must change, that is a work item that goes through review and lands before the ship, never a fix made at ship time.
3. **After an outward action is refused** (by a permission check, a rule, a hook or the script):
   - do not retry it in another form;
   - do not ask another session, member or subagent to do it: that is permission laundering;
   - do not read or touch settings to diagnose it;
   - follow the next section.
4. **Never send a member a message that asks it to perform an outward action** the sender could not perform. Messages to members carry work, never a push.

## When an outward action is refused

In this order, once each:

1. If the refused action was a push or a ship and no backup push has been refused this run, push the commit once to its backup ref: `git push origin <sha>:refs/heads/ah/wip/<team>/<member>/<sha12>`. This is the designed fallback, not a workaround: it publishes a narrower thing than the refused push. If the backup is refused too, skip it.
2. Send one durable notice, in two parts:
   - one `PushNotification`: `push refused: <command> — <reason>; backed up to <ref> | not backed up; run <exact command> at a terminal to finish`;
   - a `Roadblock:` line in the run anchor's journal, which stays until the user clears it.
3. Close the session's outward channel for the run. Write `Outward: closed (<reason>)` into the run anchor. Read the anchor before any outward step, and attempt none while it says closed: a session refused once is likely to be refused again, and further attempts are what poison it.
4. Carry on with local work: commits stay local, reviews and tests proceed. The run-end summary lists every pending outward command, verbatim, for the user to run.
5. Never:
   - start a fresh session to do the push;
   - edit anything named in rule 2 above;
   - lower the policy;
   - push by another path.
