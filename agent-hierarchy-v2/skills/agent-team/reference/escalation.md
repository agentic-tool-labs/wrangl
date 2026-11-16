# escalation — agent-team reference

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Read this when a role cannot launch, for an Ultra-Advisor escalation, or for a stalled peer.

Contents

- When a role can't take the work
- Design, review and implement roles (custom included)
- Ultra-Advisor escalation (advise class, custom included)
- A stalled peer: three pings, then take over
- Declined spawn

## When a role can't take the work

A chain role never runs as a subagent. When a role's
peer can't take the work, follow the ladder for its class. Each step runs only
if the one before it fails. Taking a role over covers the work at hand only;
the next time that role is needed, start again from the top.

**A launch failure** is the only thing that makes a role unreachable:

- `spawn-one` or `spawn-ad-hoc` exited 2 with a launch error, not a structured
  `refused`;
- `create --spawn` reported that member `launch_status: "failed"`, unless its
  `launch_result.reason` is `refused` (handled by its `refused`, below);
- a layout break (`layout-splits` exit 3) left that member without a pane.

These are **not** launch failures; handle each as its own rule says:

- `refused: "team-name-unusable"` and `refused: "member-model-undefined"`;
- `refused: "name-in-use"`: another Herdr agent holds the name — run its
  `rerun`, which adds `--names-in-use <name>`;
- `refused: "harness-cwd-untrusted"`: Codex has not been told to trust the cwd,
  and that is the user's decision — tell them its `message`, and ask with
  AskUserQuestion whether to trust each `needs` item's `root` (a yes runs its
  `command`, then re-run the spawn);
- `refused: "agent-file-not-found"`: the role's agent file is missing, so the
  member was not launched — tell the user its `message`;
- `refused: "advise-model-tier"` (§ Ultra-Advisor escalation);
- a legwork member in `skipped_members`;
- a spawn command the user declined at its permission prompt (§ Declined spawn).

### Design, review and implement roles (custom included)

1. **Live instance.** SendMessage it the brief.
2. **Spawn it.** `spawn-one <role>`, or `spawn-ad-hoc <role>` when the roster
   has no member for the role. A `member-model-undefined` refusal is not a
   failure: handle it (ask for the model, or take its fallback), then retry
   the spawn.
3. **Paneless.** Only when that launch physically fails, do the work yourself,
   in this session, under that role's contract (as the confirm-mode handoff
   gate's "Do it inline" choice does).
   First tell the user one line: "<Role> could not be launched (<reason>);
   doing its work here."

There is no cross-role step: never hand one role's work to another role, such
as implementation to an Architect.

### Ultra-Advisor escalation (advise class, custom included)

**No per-spawn approval.** Inside a `/pipeline` run, the run anchor's
`ultra-advisor:` line (asked once at run start) decides: `yes` → run the ladder;
`no` → no ladder, handle the question with the Architect or inline, and state
plainly what that leaves unadjudicated. Outside a run, run the ladder.

A non-Claude Ultra-Advisor is briefed through `deliver` instead of
SendMessage. Its model needs a declared tier of opus or fable: a
spawn refused with `refused: "advise-model-tier"` is handled by its `message` —
with no tier declared, ask the user (AskUserQuestion, header "Model tier": "How
does `<model>` compare with Claude models?", options fable, opus, sonnet and
haiku), record the answer with `<ah> roster tier set <kind> <model> <tier>`, and
re-run; declared below opus, ask the user for another model. Never declare a
tier yourself.

**The ladder.** Each step runs only if the one before it fails.

1. **An Ultra-Advisor that can be reached.** A live Ultra-Advisor peer gets the
   brief by SendMessage. Otherwise, if a
   roster Ultra-Advisor member is defined with a model, `spawn-one
   ultra-advisor`, then SendMessage it.
2. **Ask the user to spawn one.** For a member with no model, or no member at
   all (`spawn-ad-hoc ultra-advisor`), the `member-model-undefined` refusal
   drives the ask. Its question carries the refusal's options plus
   **"Don't spawn an Ultra-Advisor"**, which goes to step 3. Unattended, take the
   refusal's advise fallback (a borrowed fable/opus chain model) if there is
   one; otherwise go to step 3. A physical launch failure of the spawned
   member also goes to step 3.
3. **The highest-reasoning chain role.** Rank the design, review and implement
   members (custom included) you can see — your live team's members by their
   recorded model, and your roster's members by their stored model — by model
   tier (haiku < sonnet < opus < fable). A non-Claude member ranks by its
   model's declared tier, which status shows as `<kind>:<model>(<tier>)`
   (`?` when none is declared), never by its name. An
   `inherit` model, a non-Claude model with no declared tier, or no model,
   ranks below every tiered model. Ties go to a live member first, then design before
   review before implement, then roster order. Give the top member the **same
   escalation brief**: the spec path, the specific question, and a request to
   adjudicate and advise within its own contract. SendMessage it if it is live;
   otherwise spawn it, then SendMessage it. A pane member is briefed with
   `deliver` instead. No such member, or its launch
   physically fails → step 4.
4. **Adjudicate yourself**, on this session's model. First tell the user one
   line: "No Ultra-Advisor or <role> could take this (<reasons>); adjudicating
   here on this session's model."

### A stalled peer: three pings, then take over

- **Stalled** means both: the peer **owes you a reply** (your brief or last
  message is the latest in that exchange, so a peer waiting on your answer to
  its own question is not stalled), and its ListAgents row shows it **idle**.
  A **busy** peer is working: never ping it, and it never counts toward the
  three.
- **A ping** is one SendMessage to that peer with `notify_when_idle: true`:
  "Ping n/3: you owe a report on task <slug> — SendMessage it back to the
  sender." Send the next ping only when that idle notice arrives with no
  reply. A subscription that expires without a notice means the peer stayed
  busy: keep waiting, without adding to the count. A peer that has left
  ListAgents is **gone**, not stalled: treat it as missing and use the ladder
  above.
- **A response** is any SendMessage from that peer to you, or a response file
  for the brief's request id. The report itself ends this. A non-report reply,
  such as an acknowledgement, means the peer is not stalled at that moment.
  The count is **per brief and never resets**: at most three pings per brief.
- **Take over** after the third ping's idle notice arrives with no reply:
  design, review or implement → do the work yourself as in step 3 of its
  ladder, with the same one-line notice; Ultra-Advisor → continue the
  escalation ladder **from step 3**, never spawning a second Ultra-Advisor
  beside the stalled one.
- **A `route: pane` member** is pinged with `deliver --ping <n>`, never
  SendMessage. `no-report` is its "idle, no reply": the next step is
  `--ping <n+1>`. `busy`, `timeout`, `not-sent` and `blocked` never count
  toward the three, just as a busy peer is never pinged: re-run the **same**
  command after `busy` (nothing was sent), `--wait-only` after `timeout`, and
  the brief without `--wait-only` after `not-sent`; relay a `blocked` prompt
  (§ Relaying a prompt), never retry it on a loop, and once `answer` returns
  `answered` or the user says they answered it in the pane, re-run the same
  command if it had `sent: false`, `--wait-only` if `sent: true`. When
  `--ping 3` returns `no-report`, take over as below.
- **Leave the stalled pane running**; do not close it. Tell the user it is
  still up and can be closed with `dismiss`.
- **Surface a late report**: a report that arrives after you took over is shown
  to the user, not dropped.

### The exceptions to never granting for a member

The rule is: never answer Yes or a granting option for a member on your own.
It has exactly two exceptions. Each rests on a decision the user recorded, and
each is enforced in code, not by this text; everything else — every pane-screen
prompt, every Claude member prompt — stays the user's.

1. **A Codex trust screen under a recorded pre-approval.** The user ran
   `<ah> roster codex-trust --preapprove <dir> [--parent]` (a yes to the
   bootstrap or create question). When Codex's trust screen is exactly its own
   for a root that pre-approval covers, launch answers it with one Enter and
   verifies Codex saved the trust; `<ah> roster answer` lets you pick "trust" on
   that screen without `--user-choice` only under such a covering scope.
   Anything else — another prompt, another path, another option list, no
   pre-approval — is relayed. Every use is logged to
   `$AH_HOME/codex-trust-autoanswer.jsonl`, the spawn result carries it as
   `trust_autoanswer` (tell the user: the setup step missed that root), and
   `<ah> roster doctor` counts the last 7 days. `--revoke <dir>` or
   `--revoke-all --yes` removes a pre-approval.
2. **One-time Codex approvals inside the member's work tree, under the user's
   `codex.orchestrator_approvals` setting.** This arrives with the Codex
   question relay and is not in this release; until it ships, every Codex
   approval is the user's.

Without a covering pre-approval, a trust pick is sent only when the user picked
it in the AskUserQuestion; then pass `--user-choice`. `answer` replies
`needs-user` and sends nothing otherwise. Never pass `--user-choice` for a pick
the user did not make.

### A denied change the user asked for

When the user has explicitly asked for a change and a member's attempt at it is
denied (by the auto-mode classifier, or by a policy file members may not edit,
such as `agent-hierarchy-v2/policy/member-surface.json`), the Orchestrator makes
that exact edit itself and logs it as a decision on the user's behalf
(`reference/decisions.md`). A Sub-Orchestrator does the same in its own lane
when the change traces to the user's instruction as the Orchestrator relayed
it, and reports the edit upward as a note. There is no one-shot command for the
user to run instead.

This covers only what the user asked for. A denial of anything the user did not
ask for stays the user's call: relay it, do not make the edit.

### Declined spawn

When the user declines a spawn command's permission prompt, that is their
decision, not a launch failure. Ask with AskUserQuestion: "Do the <Role> work
here" or "Stop".

## Question tag

Every `AskUserQuestion` in this file carries `metadata.source` `ah:bootstrap:agent-team:escalation`; the hook refuses an untagged ask from the Orchestrator.
