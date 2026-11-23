# Glossary

The words Wrangl uses, each in one or two sentences. Every other page uses these terms in this sense. New to Wrangl?
Read [How it works](how-it-works.md) for the bigger picture.

## People and sessions

- **Orchestrator** (also **Chief Orchestrator**): Your own Claude Code session once the hierarchy is turned on. It splits a job into pieces, hands each piece to a member, and puts the answers together. It is the only member that may ask you questions. Its mailbox is `orchestrator@<team>`.

- **Role**: A kind of job with fixed rules, such as Architect, Implementor or Reviewer. Wrangl ships eleven roles; you can add your own ([Custom roles](custom-roles.md)).

- **Member**: One running session that fills a role on a team, for example `myrepo-implementor` (the team's name, then the role).

- **Peer**: A member that runs as its own Claude Code session, in its own pane. Most roles run as peers.

- **Subagent**: A short-lived helper that runs inside another session and ends when its job is done. The Task-Runner and the Backlog-Groomer run this way.

- **Sub-Orchestrator** (also **lane lead**): A member that runs one lane for the Orchestrator, with a small team of its own. It reports back; it never merges.

- **Deputy Chief**: A member that can run the team while the Orchestrator is away. It never asks you questions. It is put on duty with `ah roster duty on <member>`.

- **Admin session**: One Claude session that Wrangl treats as you. You name it by its process id with `ah admin grant <pid>` in a plain terminal, or type `/wrangl:admin` in the session itself. See [`ah admin`](cli-tools.md#ah-admin).

## Teams and work

- **Roster**: Your template: which roles you want and which model each one runs. It is a file, and changing it starts nothing. See [Rosters](rosters.md).

- **Team**: The live set of member sessions built from a roster. One Orchestrator can own several teams.

- **Stream**: One line of work with its own git branch and its own folder (a work tree) under `.claude/worktrees/`.

- **Work tree**: A second checkout of your repository, made with `git worktree`. Each stream gets one, so members never edit the same files at the same time and your main checkout stays clean.

- **Lane**: A stream run by a Sub-Orchestrator instead of directly by the Orchestrator. Used for big jobs with several parallel parts.

- **Brief**: The written instructions for one piece of work: who it is for, the goal, the files, and what counts as done. A brief is a request file under `.claude/ah2/msgs/` in your repository.

- **Report** (also **response**): The member's written answer to a brief, filed beside it. It starts with a status: done, partial or blocked.

- **Spec**: The design file the Architect writes. The Implementor builds what the spec says, and the Reviewer checks the work against it.

- **Team goal**: One sentence that says what the team is working towards, set on your word with `ah rules goal`. Every member sees it. It is guidance; nothing is refused because of it.

- **Rules of engagement**: Your standing operating principles, kept with `ah rules add`. A rule can be global, for one roster, or for one repo, team, project or goal; the most specific wins. Members see them at start, after a compaction and with each brief. A rule never widens what a member may do.

- **Checklist**: The Orchestrator's written list of everything still outstanding, with a state and an owner for each item.

- **Work item**: One entry on the checklist or the backlog, such as "add the login page".

- **Backlog**: The project's ranked list of work, kept with `ah backlog`. There is one per project, not one per team.

- **Sitrep**: Short for "situation report": the one-screen answer to "where are we?", built from the checklist. You see it on the [web page](web-ui.md) and in the [terminal view](tui.md).

## Messages

- **Mail**: The short notes that tell a member "you have a brief" or tell the Orchestrator "a report is ready". Mail points at a file; the file holds the content. See [Mail](mail.md).

- **Doorbell**: The one line that tells the Orchestrator it has mail, such as "You have 2 messages (1 question, 1 report). Run `ah msg next`".

- **Link**: Permission for two members to message each other directly. Members assigned to the same work are linked automatically. Any other pair asks for a link with `ah msg link-request`. Every link is on record, and the Orchestrator can read and revoke it.

- **Event**: An automatic alarm the daemon mails to the Orchestrator, for example `member-blocked` or `peer-silent`.

## The programs

- **`ah`**: The command for teams, members, mail and work. Inside Claude Code it is on the PATH by itself; in a terminal, the installer links it from `~/.local/bin`. See the [CLI reference](cli-tools.md).

- **Daemon** (`ahd`): A small background program, one per user, that delivers mail, keeps the clocks on open briefs, raises alarms and serves the live views. It starts when needed and exits after 30 minutes with nothing to do. See [The daemon](daemon.md).

- **Hook**: A check Claude Code runs before or after a tool call or at a session event. Wrangl's hooks keep members in bounds and help deliver mail.

- **herdr**: An optional terminal multiplexer for coding agents. With it, each member gets its own pane and each stream its own tab.

- **Role pack**: A Claude Code plugin that offers extra roles to Wrangl, through a file named `ah-roles.json`. See [Custom roles](custom-roles.md#6-role-packs).

## Places on disk

- **Home**: `~/.claude/ah2`. Wrangl's per-user state: the database, logs and the daemon's run files.

- **Hierarchy folder**: `<repo>/.claude/ah2`. A repository's own Wrangl files: the roster, team files and the briefs and reports in `msgs/`.

- **Snapshot** and **checkpoint**: A snapshot records a team's members and their conversations so `ah roster resume` can bring them back. A checkpoint takes a snapshot and also asks each busy member to save its work, before a planned restart.
