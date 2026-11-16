# capacity — agent-team reference

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Read this before a spawn, a reuse or a dismiss. These are rules the Orchestrator follows; nothing enforces them. The close itself is `reference/teardown.md` § `dismiss`.

- **One member, one item.** A member is spawned for one backlog or work item and keeps it through its review rounds, rework, spec amendments and NEEDS-EVIDENCE probes.
- **Reuse versus fresh spawn.** Reuse the member that did item N, if it is still alive, only when the new work is rework or another round on N, depends on N (builds on N's commits on the same branch), or changes the same files N's diff changed. Anything else gets a freshly spawned member, even when idle members exist.
- **How many.** At most one implement-class member (implementor, ux-engineer, docs-writer) per ready item, and only for items whose files do not overlap another in-flight item; overlapping items queue. Architects and reviewers are spawned per item as it needs them; QA is one per ship batch. Total live members, the Orchestrator included, stay within the session budget: a pipeline run's anchor `session-budget`, otherwise a budget that starts at 12. At the budget, queue: do not spawn, and do not reuse an idle member for unrelated work; dismiss a finished one instead.
- **When to dismiss.** In the same turn the item is signed off (review passed, or the Orchestrator accepted it), committed, and the member is idle. An Architect stays until the item its spec drives is signed off, then goes. Only an open request to the member, open review or rework on its item, or a reuse follow-up dispatched to it in this turn keeps a finished member alive.
- **Default team shape.** A team starts with the Orchestrator alone. Members are spawned as items become ready: an Architect if the item needs design, then one implement-class member, then a Reviewer when the diff lands, then QA at a ship batch. Scale out by running items in parallel, not by keeping standing seats; lanes follow the existing lane caps. A roster lists the roles available, never a standing count.

## Budget adjustment

Before every spawn, and on a `machine-overloaded` or `machine-load-ok` mail, read `<ah> msg daemon status`:

- Lower the budget by 2, at most once per 10 min, floor 4, while `load.state` is overloaded, `mem_pressure` is warn/critical, or `api_errors_10m.rate_limit` is 3 or more.
- Raise it by 2, at most once per 30 min, up to the starting value, after 30 min of `state` ok, `mem_pressure` normal and `rate_limit` 0.
- Lowering never stops a working member; it only stops spawns until sign-off dismissals bring live members under the new budget.
- Record each change as `budget N→M (<trigger>)` with `<ah> work record … --type decision` on the run item.
