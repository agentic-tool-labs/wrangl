# Workstreams: handing a stream to a sub-orchestrator

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Use this when one stream is large enough to run as its own team. You stay the manager (the Orchestrator of team `<X>`); a
sub-orchestrator runs the stream, owns the sub-team, and reports to you.

## Open

1. `<ah> roster stream-open <ws>` (the stream, its work tree and its tab).
2. `<ah> roster spawn-one sub-orchestrator --stream <ws>`. The member is `<X>-so-<ws>`. A refusal names its code
   (`stream-required`, `sub-orchestrator-depth`, `sub-orchestrator-name`, `sub-orchestrator-exists`, `sub-orchestrator-breadth`)
   and nothing was started. `sub-orchestrator-name` means the stream name collides with a derived member name: a stream ending in
   `-<digits>` (like `lane-434`) or in `-<role>` is refused, so name it without that ending (`lane-434a`).
3. Write the brief with `<ah> msg new --title "<few plain words, verb first>"` addressed to `<X>-so-<ws>`: the goal, the scope and what not to touch, the work tree and
   branch, the acceptance criteria, the sub-team roster, whether it may push or open a draft PR (default neither), the `eta`, and
   that it must wind its sub-team down before reporting `done`. Send it with `<ah> msg send <X>-so-<ws>@<X> --ref <id>`.

The sub-orchestrator opens its own team and runs it. The start sequence that works:

    <ah> roster create --parent <X> --stream <ws> --transport herdr --roster-level global --commit

`--transport` is the manager team's transport (`herdr` above). Each member it then spawns into the child team takes
`--cwd <the stream's work tree>`: a launch directory outside the lane's work tree is refused `cwd-outside-lane`.

## Status

The `workstream` line of each stream in HIERARCHY STATE shows the sub-orchestrator, whether it is live, the state
(`working`, `blocked`, `review`, `done`), its one-line summary, its age and, once done, the path of its result. A request to a
sub-orchestrator counts its check-in clock from the later of the dispatch and its last report.

## Result

The sub-orchestrator writes a response beside the brief, runs `stream-report --state done --result <path>`, then sends the
response with `<ah> msg send orchestrator@<X> --kind report --ref <id>`. Read the response named by the `result` field (`ah msg show <id> response`).

## Close

The sub-orchestrator closes its own sub-team once its members are idle. It never uses `--ignore-release` or `--kill`.

## A lane: staff it in the child team

A lane is a stream of your team plus the sub-orchestrator that runs it and that sub-orchestrator's child team `<X>-<ws>`.

    <ah> roster stream-open lane-stab --team <X> --base <integration branch>
    <ah> roster spawn-one sub-orchestrator --stream lane-stab --team <X> --lane-members 4 --lane-streams 8

From then on the sub-orchestrator opens streams named `lane-stab-…` in its child team (branched from the lane base), spawns members into them up to the caps, reassigns an idle member with `move`, writes briefs, and receives reports. Its members and it message each other directly. It never needs a link, and you take no action after the spawn.

- Staff the lane in the child team, never from your own team: members of `<X>` stay with you.
- The lead runs one plain `<ah> roster …` per shell command. A sandboxed lead runs each `<ah>` command alone, with no pipe or redirection; only an unsandboxed top-level session may pipe or redirect (`2>&1 | tail -15`). Variables, `NAME=value` prefixes, `$(…)`, backticks, `sh -c`, `eval` and wrappers such as `nohup` are not.
- The sub-orchestrator never pushes, merges, tags or asks the user. It may park a pick inside its charter on Needs-you with `<ah> roster checklist set <item> --team <X>-<ws> --state waiting-user --note … --option …` (files go on with `<ah> work attach`). You are told at park, and the answer comes to the lead with a copy to you. Reserved, policy, product-direction, dangerous and design questions still come up to you as `QUESTION [<class>]`; no code checks that, and you can withdraw a park by setting the item back. A finished item comes back as a report naming the branch, the SHA, the suite lines and the review verdict, and you merge it.
- `stream-done` for a lane stream stays with you. It refuses `unshipped-commits` until the lane's work is in golden or a true flag records why; after the lane's members are dismissed, re-run it to archive the branch.
