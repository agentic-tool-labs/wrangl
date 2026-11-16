# Coordinating by files when the message store or ahd is down

Guidance only. No command, gate or fallback path backs this; you and the members follow it by hand. There is no files mode to switch to.

## When

`ah` prints `ah: message store unavailable: <why>` and exits 1, or `ahd` will not start. First tell the user what you saw. Do not open or repair `state/ah.db` by hand: recovery is the user's call, and copies live in `state/backups/`.

If the work can wait for the store, wait. Use the rest of this page only when it cannot.

## How

1. Pick one folder outside `msgs/`, such as `<project>/.claude/ah2/by-hand/`. Never write under `msgs/`: the `msgs-files` gate bounces file tools there, and nothing reads files there.
2. Write each brief as a plain file, `<slug>--brief.md`: the goal, the work tree, the constraints, and what to send back.
3. Send the member its absolute path with SendMessage. Do not use `[hierarchy-msg]`, which needs the store. A brief with no `[hierarchy-msg]` is answered by SendMessage, so the member's first line is `DONE <slug>` or `BLOCKED <slug>`.
4. Longer reports go beside the brief as `<slug>--report.md`, and the reply names that path.

Hooks that read the store may fail or stay silent while it is down. Do not count on nudges or the view: track open work in the folder yourself. If a member cannot be reached, tell the user.

## Afterwards

Nothing imports the folder. Once `ah msg list` works again, re-file only what is still open as ordinary messages (`ah msg new`, `ah msg send`), then tell the user the folder exists. Leave it in place: the user decides whether to delete it.
