# Live checks

Six things this plugin depends on cannot be unit-tested: they are properties of
the running Claude Code session, not of any file in this repo. Run them once per
machine after an update — each is one line, and each has a right answer.

`<AH_ROOT>` below is on this session's `ah CLI root` line; with no such line, use
the recipe in [cli-tools.md](./cli-tools.md).

| # | Question | How to check | Expected |
|---|---|---|---|
| 1 | Is `CLAUDE_PID` this session's own pid? | `<AH_ROOT>/bin/ah roster doctor --cwd <abs cwd> --check` | exit 0; the `identity` row is `ok` and names a live pid |
| 2 | Does `${CLAUDE_PLUGIN_ROOT}` expand inside `agents/*.md` and `skills/*/SKILL.md`? | dispatch any role and ask it to echo the CLI path it was given | an absolute path, not the literal placeholder |
| 3 | Is `CLAUDE_PLUGIN_ROOT` set in the Bash tool's environment? | `echo "[${CLAUDE_PLUGIN_ROOT}]"` through the Bash tool | `[]` — it is EMPTY, which is why every hook message spells the absolute path |
| 4 | Do subagents receive the root line? | spawn any subagent and ask what its `ah CLI root` line says | the line, with the version token, from the running root |
| 5 | Do two checkouts / a worktree share one runtime dir? | `ah roster doctor --cwd <abs cwd>` in each | the `runtime-dir` rows agree, or you intended them not to |
| 6 | After `/reload-plugins`, does the next prompt name the NEW version? | run `/reload-plugins`, send any prompt, read the newest `ah CLI root` line | `(v<new version>)` and the new directory; the most recent line wins |

Anything that disagrees is a real finding: the plugin's own tests cannot see any
of it.

## Daemon checks

The daemon's tests drive it against fakes. These need the real tool, so run them once per machine after an
update, and for the Codex check whenever you want to try a new Codex version. Do them in a throwaway
workspace that is never your own (see the next section).

| # | Question | How to check | Expected |
|---|---|---|---|
| 7 | Does a paste from the daemon reach a real herdr pane? | in a throwaway herdr workspace, a member with a recorded, idle pane that is not a Claude session; `ah msg send <name@team> --text "hello"` and leave it unacknowledged; watch `ah msg inbox --address <name@team>` as the [delivery ladder](./daemon.md#how-mail-is-delivered) runs | the text appears once in that pane and the entry ends `delivered`; a busy or blocked pane makes it wait and try again later, never type twice |
| 8 | The same on a real tmux pane | as 7, with a member whose recorded transport is tmux | as 7 |
| 9 | Does the [Codex app-server adapter](./daemon.md#the-codex-app-server-adapter) work against a real Codex? | `ah roster add --level repo --role implementor --kind codex --route pane --adapter appserver`, then `ah roster spawn-one implementor --member <name>` on the herdr transport | today, on `codex-cli 0.162.0-alpha.2`: the member gets the adapter (the row has `appserver_pid` and `appserver_thread_id`, no note); on any other version it launches the ordinary way and the spawn output carries `outside the tested app-server range`. For a version you are trying, on a throwaway Codex home with one login copy and a hostile `config.toml` (`sandbox_mode = "danger-full-access"`): the spawn's thread passes the gate; a warm-up asking for the instruction marker from `-c model_instructions_file` returns it; after `codex resume <thread> --remote <socket>` attaches, one turn is asked to connect to a socket under the ah home (denied), to write a file there (denied), to write into a directory outside every root under the same temp location, also through the `/tmp` spelling (both denied), to write into the server's own system temp directory (denied), to write under the one `--add-dir` folder (allowed), and to run `mktemp` (allowed, leaving a file in the member's temp directory); the thread report must show both temp exclusions on; mail then reaches the running turn, the entry is acknowledged, and what was typed in the composer is untouched. Only then is the version added to the tested list, as the version and the next prerelease above it |
| 10 | Does a Docs-Writer member of another kind launch the way an Implementor of that kind does, without the shipped `sonnet`? | on a machine with a Codex pane: `ah roster add --level repo --role docs-writer --kind codex --route pane`, then `ah roster create --plan` and `ah roster spawn-one docs-writer --member <name>` | the plan entry has no `model` and no `model_note`, and `members_needing_model` lists it with kind `codex` as it lists an Implementor of kind `codex`; the spawn launches it as it launches that Implementor |
| 11 | Does a member working in a git worktree of the Orchestrator's repo get resumed after an API error? | start a team whose member runs in a worktree, kill that member's connection mid-turn (a StopFailure), and wait for the daemon's resume | the member's `activity/<session>.json` exists in both the worktree's `.claude/ah2` and the main checkout's, and the daemon resumes the member |

## Terminal checks for `ahd tui`

The TUI's tests drive it with synthetic key and mouse reports. These need a real terminal each, so run them once per terminal listed, after a change to the mouse or drawing code. Where a check fails, `AH_TUI_MOUSE=off` turns the mouse off.

| # | Question | How to check | Expected |
|---|---|---|---|
| 11 | Does a click act and a drag do nothing in iTerm2 and in Ghostty? | in each terminal run `ahd tui`; click a team; drag the pointer down over three rows; then select text while holding Shift | the click opens the team, the drag changes nothing, and the terminal's own selection works with Shift |
| 12 | Does the work item popup stay on screen in a small terminal? | in a 60 by 16 terminal run `ahd tui`, open a team, click a work item, then press `esc` | the popup is drawn inside the screen without scrolling or wrapping it, and `esc` leaves every row as it was |
| 13 | Does a team stay readable at 40 columns in a herdr pane? | split a herdr pane to 40 columns and run `ahd tui`; move with the arrows, press enter on a team, then click a Sitrep heading | no line wraps, the rightmost columns are the ones dropped, and the heading folds its region |

## Probes that start a multiplexer server

A sandboxed multiplexer server must not restore the user's saved session. Before
starting it, point every state path it reads at an empty temp dir: the socket,
the config, and the saved-session file. After it starts, confirm it lists zero
panes before doing anything else. Never kill a sandbox server that lists a pane
the probe did not create: stop and report instead. A restored pane can relaunch
a copy of a real member session under the same session id, and that copy's
session end would otherwise reach the real pool.
