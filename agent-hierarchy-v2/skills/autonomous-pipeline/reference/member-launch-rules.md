# Member launch rules

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Member launch rules are on by default. Nobody is asked for them: not at bootstrap, not when a version changes, and a
spawn does not wait on an answer. There is no bootstrap step for them.

**A member may run any shell command.** The rules only stop permission prompts; they are not a limit on what a member can do.

What each member launched gets:
- A launch-only settings file. ah never writes your own settings files.
- An allow list and nothing else, the same for every role: `Bash(*)`, the common programs by name (git, dotnet, node, npm, python3, bash and others), cleanup of
  ah's own scratch, ah's own commands, the tools `Read`, `Edit`, `Write`, `Glob`, `Grep`, `NotebookEdit` and `SendMessage`, and any allow rules from your own settings.
- No deny rule, no ask rule and no sandbox. A member's commands run unconfined, with your user's access to files, credentials and the network.
- Not pushing is an instruction, not a rule. A member's brief says "commit on this branch, do not push", and nothing enforces it. The same goes for `gh pr`,
  publishing and launching `claude`: members are told to leave these to the Orchestrator.
- The auto-mode classifier can still stop a member's command, whatever the list allows.
- Your own deny rules still win. See a member's file with the roster permissions show verb (<ah> roster permissions show <member>).

Opting out: the user runs `ah allow off` from their own terminal (any Orchestrator may run it too, since off narrows). After it, members launch with their
inline settings. `ah allow list --json` shows the state: `given`, `declined` (an answer recorded before this default; it opts out like `off`) or `off`. `ah allow on`, run by the user after a decline or an `off`, turns the rules back on; a decline recorded after that `on` holds again. Do not ask the user whether to turn the rules on or off, and do not
treat an unset state as missing consent.

Turning them back on widens, so `ah allow on` needs the user at a terminal. To run it yourself after the user asked for it, first put one `AskUserQuestion` (tag
`ah:R7;allow=on:member-launch-rules` in `metadata.source`), question "Widen member launch rules?", options "Allow" and "Not now". `ah allow on` accepts only that
question's fresh Allow answer.
