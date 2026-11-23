# run anchor — autonomous-pipeline reference

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Read this at bootstrap step 6 to write the anchor, and whenever you need to find it again or close it: before a push, after compaction, at the end of a run. The rule that gates every push on it is in SKILL.md § The run anchor.

Contents

- Why there is an anchor
- Writing the anchor
- Finding it again
- Before writing a new one

## Why there is an anchor

A remembered branch name is not trustworthy after compaction, and a wrong push
writes to a remote. **Bootstrap step 6 writes a run anchor:** a request-type
message file whose body records the resolved branch, and the plan path, in its
`## constraints` section, on a line of the literal form `branch: <name>`.

## Writing the anchor

Every record request, the anchor included, is addressed to the Orchestrator
itself: `<ah> msg new --type request --to orchestrator --from orchestrator --slug <slug> --title "<few plain words, verb first>" --team <team> --cwd <root>`.
The address is load-bearing: an orchestrator-addressed exchange closes on any
response, a member-addressed one only on an authored report.

Write the anchor in two steps:

1. Create the file with the command above, slug `pipeline-run-anchor`; it
   prints the file's `path`.
2. With the Write/Edit tool, replace the file's body (everything under its
   frontmatter) with this section:

   ```
   ## constraints
   branch: <resolved branch name>
   plan: <plan path>
   authority: surrogate | user
   authority-asked-at: <iso>
   ultra-advisor: yes | no
   session-budget: <level> (<N>)
   ```

   `authority:` is step 3c's answer (`user` when unanswered), written here at step 5a and
   never changed; `reference/authority.md` says what each value means for members.

   `ultra-advisor:` is step 3a's answer (`no` when unanswered): `yes` lets this run spawn and brief an Ultra-Advisor freely,
   with no further ask; `no` means the escalation ladder skips it. Written once, for this run only.

   `session-budget:` is the most live members the run's team tree may hold (every
   lane's child-team members included, each session once) when the
   tick starts parallel work (`reference/cadence.md`). Always write the line at
   bootstrap, for example `session-budget: med (12)` or
   `session-budget: max (uncapped)`. The levels are low = 8, med = 12 (default),
   high = 16, xhigh = 20, max = uncapped. When reading it, the level word
   decides; the number in parentheses is only for people to read. An anchor
   without the line, from a run started before the levels existed, reads as
   med (12).

   `Outward: closed (<reason>)` is an optional line, absent while the run's outward channel
   is open. After an outward action is refused, the Orchestrator adds it under `## constraints`
   (`reference/push-regime.md` § When an outward action is refused). Read it before every push
   or ship: while it is there, attempt none and carry on with local work. It is never removed
   during the run, and `<reason>` names the refused command and why.

**Leave this exchange open for the life of the run — never `SendMessage`
it, and never close it until the run finishes.** A finished run closes it: a
plan run after § Completion gate, an issue run at its end of run. The slug
`pipeline-run-anchor` is reserved: no work-item slug may produce it, or the
collision would silently add 1 to that item's round count.

## Finding it again

**Find it again by this procedure, never in the injected state block** (a
capped index with no bodies, where the oldest open exchange, the anchor, falls
out):

1. `<ah> msg list --team <team>` **in JSON mode, not `--plain`,** and
   without `--all`: the default lists open exchanges only, so a closed
   anchor from an earlier run never counts. Only JSON rows include
   `request: <path>`. When the JSON is an object rather than an array, the
   rows are its `exchanges` array.
2. Select rows where `slug === "pipeline-run-anchor"`.
3. **Exactly one match is required.** Read its `request` path and parse
   the `branch:` line from the `## constraints` section.

**Zero or multiple matches halt — never take the newest.** More than one open
anchor means more than one pipeline run is or was live in this repo, and
nothing says which one this session belongs to; zero matches leaves nothing to
trust.

## Before writing a new one

**Before writing an anchor, look for one:** run steps 1 and 2 above. If any
open anchor exists, do not write a second one: halt and notify before any work,
saying that another pipeline run's anchor is still open in this checkout, with
its id and age. Give the command that closes it, ready to paste into the user's
own terminal, every placeholder filled in and `<ah root>` the absolute path on
this session's `ah CLI root` line:
`<ah> msg fill --id <id> --from orchestrator --body-file - --cwd <root>`.
Tell the user to close it only if no other pipeline run is live in this
checkout, then to run again: nothing closes an anchor automatically.

**With `--adopt`, a provably finished anchor is closed instead of halting.**
Exactly one open anchor, and both hold:

- its run's work is on the remote: the tip of its `branch:` is an ancestor
  of `origin/<branch>` or of
  `origin/main`, by `git merge-base --is-ancestor <branch> <remote ref>`
  (exit 0);
- none of its items has an open exchange: no open row of
  `<ah> msg list --team <team>` has the slug of an item in its `plan:` file
  (slugs derived as in SKILL.md § The 3-round cap).

Then ask once with `AskUserQuestion`, header "Old run", naming the anchor's
id, age and branch: "Close the finished run's anchor?", "Yes" first, then
"No". On yes, close it in one step,
`<ah> msg new --type response --req <its request path> --from orchestrator --body-file - --cwd <root>`
with a one-line body saying it was closed by an `--adopt` run, then write the
new anchor. On no, no answer, or when either check fails or cannot be run
(the branch is gone, more than one anchor), halt as above.
