# decisions — autonomous-pipeline reference

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Read this before classifying a question or dispatching a decider. The trigger,
the default-deny rule, the two keys and parked questions are in SKILL.md
§ Decisions on the user's behalf.

Contents

- Safe or dangerous
- Operational: decide, never ask
- Routing ladder
- The decider
- The brief
- The reply, parsed fail-closed
- Log first, then apply
- The report

### Safe or dangerous

A question is **safe** only when every one of its options passes all four:
- **S1 Contained.** Its effects stay inside the run's branch (working-tree
  files and commits on it) and the run's own `.claude/ah2/` records.
  - **Nothing the run itself will execute changes.** No option adds,
    removes or edits dependency manifests or lockfiles; package, build or
    test scripts or their config; container or tool-version files; env
    files; git attributes; git config; or any file a hook, CI or the test
    runner loads. Except O7: retiring a test that duplicates another test
    still present is operational.
  - **No other ref:** no new branch, tag, worktree or stash.
  - Pushing the branch, and its draft PR, under § Push regime is inside the
    branch, not an escape.
- **S2 Reversible on the branch.** Editing or reverting commits on the
  branch undoes it, and nothing has been merged.
- **S3 In scope.** It implements, tests or documents an item already in the
  plan. It adds no item, AC, feature or third-party dependency, drops no AC
  and changes no AC's meaning.
- **S4 Not reserved.** It touches none of the classes D1–D6.

The reserved classes, every one dangerous (the lists are examples, not limits):
- **D1 Destructive or irreversible:** deleting or overwriting data or files
  beyond what the item's spec changes; history rewrites, force-push,
  deleting branches or tags; real data stores or migrations; killing,
  dismissing or abandoning in-flight work. Killing a process of dead or hung
  work (O1) is operational.
- **D2 Remote and merge:** any push outside § Push regime, the
  degraded-mode approval included; merging, approving, readying or
  retargeting a PR; tracker writes; posting anywhere outside the repo. A
  merge done under the user's own per-run merge authorisation (§ Merge on
  your approval) is not a decision: the plan answers it, so it is never
  classified, decided, or logged as a `decided` line.
- **D3 Security and trust:** permissions and settings, CLAUDE.md, hooks,
  agent definitions; role-pack adoption, trust and pins; credentials and
  secrets, auth logic, network egress; CI config and protected paths
  (`settings.protected_paths` and the pipeline-conventions baseline); the
  repo's build, test, dependency and tool configuration; git config and the
  hooks path; any permission prompt.
- **D4 Cost:** spawning top-tier members the roster doesn't have; answering
  the Ultra-Advisor gate; raising a cap; re-running a scan.
- **D5 Scope:** anything S3 excludes.
- **D6 The run's own rules:** a cap, a guard, the push regime, the
  completion gate or a round count; a 4th round; anything this skill or a
  gate keeps for the user.

**Classification replaces none of them.** It judges each option as
described. The diff that option produces still goes through the review
loop. ah has no push guard and no secret scan, so D3 is prose in every repo:
ah blocks no push of a protected path. The real boundary is branch protection
or a ruleset on GitHub.

**After a merge,** S2 can't hold for that item: any further question on it
is parked as `dangerous`. **Review flag:** a safe question that a role
marked as "the user's call" (product behaviour, UX, a public interface or
format) is still decided, and always flagged for the user's review
(`review: yes`).

### Operational: decide, never ask

The Orchestrator decides these itself and never sends them to the user. Each is
logged with `<ah> msg decision add` as `class: "operational"`, `o: "O1".."O10"`,
`decider: {role: "orchestrator", self: true}`, `question`, `decision`,
`rationale` and `revert`. Operational lines are outside the 4/20 caps, never
parked, and the writer refuses one that carries `why_user`, `dangerous` or an
R/D class. Outside a run the line goes to the team's log
(`pipeline/team-<team>/decisions.jsonl`, read by `decision list --summary`);
also state the call in the reply as
`Decided (operational O<n>): <what> — <why>; revert: <how>`.

`<ah> msg decision list --waiting` lists everything waiting on the user, run or
no run: open d<k>, open q<k>, and waiting-user items no open decision names,
each with the command that answers it. A waiting-user item is answered with
`<ah> msg decision answer <stream>:<item> --choice <a|b|c|d|other:text>`.

A lane's report may carry such a line for a call its sub-orchestrator made. The
Orchestrator logs it once, in a run, with
`decider: {role: "sub-orchestrator", name: <lane>, relayed_by: "orchestrator"}`,
taking `question`, `decision`, `rationale` and `revert` from that line. The
sub-orchestrator never writes to the log itself.

| # | Call | Bounded by |
|---|---|---|
| O1 | Clean up dead or hung work: kill a process whose owning agent is Dead or whose job is past its hard cap; remove leftovers the run created (temp dirs, stale slot dirs, scratch, unpushed worktrees and branches of dead work) | Never a live member's agent session outside a run; inside a run the user's CLAUDE.md pipeline exception governs |
| O2 | Staffing: spawn (from the roster), replace, stand down, re-spawn or reassign members | Never a top-tier member the roster lacks (D4) |
| O3 | Assignment: which member takes which item; resume order (respawn and continue, review first, or park an item) | Never dropping an item or AC (D5) |
| O4 | Retry or re-run a test, gate, build or scan step after a transient failure | Within the run's caps (D6); never editing the gate |
| O5 | Merge reviewed work into the run's own train/integration branch, locally | Never a PR merge, main, the default or a protected ref (D2) |
| O6 | Push `ah/wip/*` backup refs | Only as § Push regime allows |
| O7 | Retire a test that duplicates another test still present (same assertions, same subject), naming the survivor | Never lowering coverage, never touching runner config |
| O8 | Close superseded or abandoned requests (`<ah> msg close`); ack events | — |
| O9 | Choose a timing, threshold or default that config names and the user can override | Never a default that changes product behaviour |
| O10 | Workflow reshape: take the move a `<ah> roster timing` signal names (M0 backlog item, M4 batching, M5 QA/perf off the critical path, M6 gate rerun, M7 affected-only gate), or undo it | At most one move per signal per batch; never M7b, M8, or a new gate, deny, refusal, wait or rule (those are parked for the user) |

Never operational (each is a classed question): anything that leaves the
machine except pushes § Push regime allows (D2); money (D4); deleting user data
(D1); product behaviour, UX, a public interface or format (`product`);
permissions, settings, hooks, gates (D3); main, default or protected refs;
anything irreversible outside the run's branches (D1); a standing rule the user
reserved; the run's own caps and rules (D6). `policy` names a tradeoff the user
owns that fits none of those, for example whether alarm text may leave the
machine.

### Routing ladder

Every question the Orchestrator hits goes through these steps; the first that
applies decides it.
1. **Operational (O1-O10):** decide at once and log it.
2. **Design** (how to build an item; spec gaps): the item's designer, meaning
   the Architect that wrote its spec when that member is live; otherwise the
   decider below.
3. **Hard or high-stakes** (a fork the designer could not resolve, security,
   concurrency, a public interface, anything hard to reverse): the
   Ultra-Advisor when its gate is `session`, else the highest-tier live
   member, by the tier rule in § The decider. Never decided in the
   Orchestrator's own context.
4. **Reserved** (R1-R9, D1-D6, `product`, `policy`): in a run (an open run
   anchor) it is parked with a `parked` line and lands in the end-of-run
   "Waiting for you" list, never asked mid-run; outside a run it goes on a
   waiting-user item or in your report and you keep working, and it is asked
   only when nothing else can move or waiting would do harm.

Every AskUserQuestion carries `metadata.source` = `ah:<class>[;ref=d<k>][;why=<text>]`
(for example `ah:R8` outside a run, `ah:R8;ref=d7` to clear parked `d7`). A header or
first line that starts with `[ah:<class>]` is read when the call has no
`metadata.source`; `metadata.source` wins when both are present. "In a run"
means the one open run anchor with the pools reporting it live; a closed
anchor, or a pool that cannot be read, is no run. Do not ask the pipeline
bootstrap questions (`decisions`, `codex-trust`, `auto-merge`, `authority`) once
a run is live; `issue-scope`, the end-of-run push approval, may still be asked.
`<class>` is `R1`-`R9`, `D1`-`D6`, `product`, `policy`, `ambiguous` (needs
`why=` naming one O-class and one reserved class; in a run it is treated as
step 3, and a `dangerous` classification parks it) or
`bootstrap:<skill>:<step>` (a question a skill requires). One question that carries
both an authority choice and a decision takes `ambiguous`, with `why=` naming the
O-class and the reserved class it mixes. `ref=d<k>` names a
parked, unanswered decision of this run and is the only way a reserved question
is asked mid-run (the user is present and clears it now). Asks are recorded
with their tag in `<hier>/asks.jsonl`, not refused: an untagged or `op`/`O*` ask
goes through, so tagging and deciding operational calls yourself are on you.

### The decider

Each time, run the agent-team escalation ladder (the escalation reference
SKILL.md has you read before dispatching a decider) like this:
- Step 2 was settled at bootstrap step 3a; never ask it mid-run.
- Step 1, the Ultra-Advisor, only when the gate is `session`.
- Before any dispatch, the tier rule: skip a candidate whose tier is below
  yours or unknown; dispatch one at your own tier with `reason: context`,
  so it decides from a clean context, apart from the session driving the
  run.
- No candidate left → step 4, but **the Orchestrator never decides in its
  own context.** Dispatch a fresh Agent-tool subagent of type
  `general-purpose` on your own model (never an
  Orchestrator-type subagent), with the brief below as its prompt; log it as `decider: { role: "orchestrator", name: <its id>,
  model }` with `exchange: null`, and parse `## decisions` from its final
  report by the rules below. If no fresh subagent can be dispatched, park
  the question as `unsure`. Nobody self-decides.

### The brief

A request through `<ah> msg new --to <role> --from orchestrator --slug dec-<anchor suffix>-<k> --eta small|medium|large --title "<few plain words, verb first>" --team <team> --cwd <abs cwd>`:
the slug's suffix is the anchor id's last hyphen segment, `<k>` the first log id
the batch will take. `--eta` set honestly. **The `dec-` prefix is reserved:** no work-item slug may start
with it, so a decision exchange never adds to an item's round count.

Body:
- `## constraints`: the run anchor id; the item slug, or `run`; the
  statement "The user has delegated this decision to you for this run;
  answer it on their behalf within your own contract"; the reply format
  below; this section, by path and heading.
- 1–4 questions, `q1`–`q4`, each with: the question in plain words; 2–4
  options, `a)` to `d)`, each with its consequence; the default, if the
  raising role gave one; who raised it; your classification, and why it is
  safe.
- Context as paths: the plan, the item's spec, the item's commit range.
  **Issue runs: no snapshot or trace path and no issue text** (as for
  Implementors). A decider must not be argued into an option by third-party
  text.

### The reply, parsed fail-closed

The decider's response has a `## decisions` section with exactly these four
lines per question:

```
q<n> decision: a | b | c | d | other: <one line> | unsure | dangerous
q<n> rationale: <one line>
q<n> revert: <one line: how to undo it>
q<n> review: yes | no
```

- A letter is a decision; the log stores that option's text, so near-miss
  wording can't slip through.
- `other:` in a plan run: re-classify it; if it fails, park the question as
  dangerous. `other:` always means `review: yes`.
- **`other:` in an issue run is always parked as `unsure`:** you would
  re-classify it, and you have seen the issue text.
- `unsure` or `dangerous` → parked, with that as the reason.
- A missing, malformed or extra line for a question, or no reply at all →
  that question is parked as `unsure`.
- **Never re-ask.** A parked question is never sent to another decider, or
  to the same one again, compaction included. Before dispatching, check
  `<ah> msg decision list --team <team> --item <slug>`. Asking until you get an answer is not
  deciding.

### Log first, then apply

The one exception is the decision log: what was decided can't be derived
from slug counts. It is `<hier>/pipeline/<anchor id>/decisions.jsonl`, written
and read only through `<ah> msg decision add|list` (fields and refusals:
`docs/cli-tools.md`).
1. Log it first: write the line's JSON with the Write tool to
   `<hier>/pipeline/<anchor id>/decision-input.json`, overwriting it, then
   run `<ah> msg decision add --team <team> --input <hier>/pipeline/<anchor id>/decision-input.json --cwd <root>`.
   Never pipe or heredoc it: only the `--input` form runs without a
   permission prompt.
2. **Only on exit 0** route the answer to the role that raised the question,
   quoting the printed id: "decided on the user's behalf by `<decider>`
   (d<k>)". That role carries on: it amends the spec, or builds.
   Implementor commits for the decision name `d<k>` in the body.
3. Exit 2 → nothing is routed; a refused line must never have acted. By the
   refusal's `reason`:
   - `cap` → add a `parked` line with `why_user: "cap"`;
   - `parked-before` → add nothing: the earlier `parked` line stands;
   - `decided-before` → add nothing: find the earlier decision with
     `<ah> msg decision list --team <team> --item <slug>` and route that one, quoting its `d<k>`.
     Never route a second, different answer;
   - a field refusal (`invalid`) → add a `parked` line with
     `why_user: "unsure"` and the refusal text as its `rationale`, built
     from the fields that were valid;
   - if that `parked` add is refused as well → stop the item exactly as for
     a parked question, put both refusal texts in the stop notice (issue
     runs: the `needs-user` record), and don't try again.

A `parked` line names the decider that answered `unsure` or `dangerous`, or
has `decider: null` when the question was parked before any dispatch
(classified dangerous by you, or over a cap).

**Caps:** at most **4** decided per item and **20** per run. Check
`<ah> msg decision list --team <team> --summary` before dispatching; over a cap → park with `cap`.
The writer enforces both, and parked lines don't count.

### The report

**"Decisions made on your behalf"**, built from `<ah> msg decision list`, never from
memory:
- first, **"Operational calls":** every `operational` line, one line each: id,
  O-class, the decision, and the lane's name for a relayed line;
- grouped by item, in id order within each item; flagged lines first,
  marked "review";
- each line: id; the question in one line; the decision; the decider
  (role, name, model); the rationale; the revert hint; the item's branch,
  and the commits naming `d<k>`, if any;
- then **"Waiting for you":** every parked line that has no `answered` line, with its item,
  reason, question and options, so the user can answer and re-run;
- then **"Answered by you":** every `answered` line, with its question and the choice
  (`decision`). When the user answers a parked decision in the session, the Orchestrator
  records it with `<ah> msg decision answer <id> --choice <a|b|c|d|other:<text>> --team <team>`;
  that closes the parked line, and the waiting count drops by one. No other role runs it.
  The user can also answer in the UI (web Attention, TUI Attention panel):
  1. A `question-answered` event for a `d<k>` id means the user answered in the UI; the answer is
     already recorded. Do **not** run `decision answer`; read it (`decision list`) and act as on a
     chat answer.
  2. `decision answer` refused `already-closed` while recording a chat answer means the user also
     answered in the UI first. Read the recorded answer; if it equals the chat answer, carry on; if
     it differs, tell the user both and ask which stands (the log is append-only; the first line
     stays the record unless the user says otherwise).
  3. (producers) A parked question whose answer may be several options sets `multi: true`; record a
     chat answer with one `--choice` per picked option;
- then **"Answered by your rules":** every `decided` line whose decider is
  `{role: "user-rule", rule: <key>}`, with the rule key, the question and the choice. These are
  counted under `by_rule` (keys in `rules`) in `decision list --summary`, never under `decided`.
  Write one with `<ah> msg decision add --rule <key> …` (a `decided` object; a null `choice` takes
  the rule's `choose`). Only a rule the user accepted (`ah rules accept <key>`, or Accept in the web
  rules dialog) decides. A proposed rule is refused `rule-not-accepted`, an unknown key
  `unknown-rule`, and a `dangerous` question `rule-not-for-dangerous`. Outside a run, the line goes to
  the team log;
- the totals line and the log path.

It goes in the run's final message (plan runs: § Completion gate; issue
runs: § End of run), in each item's PR body in issue runs (§ PR creation),
and in a halt notification's session message.

## Surrogate instructions

Under `authority: surrogate` in the run anchor, an Orchestrator instruction to a member for a delegable class (D1-D5) opens
"As the user's surrogate (class Dn):". The classes D1-D5 and R1-R9 are defined in the members' Surrogate authority block
(their session notice).
