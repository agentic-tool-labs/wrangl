# The model question for a legwork role

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

The one place this rule is written. `SKILL.md` and `agent-team` point here.

## Who it applies to

A legwork role: `task-runner`, or a custom role whose class is `legwork` in
`<ah> roster role list`. Every other role keeps the ordinary model question
(`Undefined — choose at each spawn` first, nothing marked `(Recommended)`).

## Detect task-gopher

It is installed iff `task-gopher:task-gopher` is among your available agent
types. This is the same check `/hierarchy init` makes.

## Installed

Ask with AskUserQuestion, options in this order:

1. `Defer to task-gopher (Recommended)` — adds the member with no model;
   `<ah> roster create` skips it and legwork goes to `task-gopher:task-gopher`
   subagents.
2. `haiku`, `sonnet`, `opus` — each described as:
   replaces task-gopher with a dedicated peer session.

The rest of the models come via Other. There is no separate `Undefined` option
here, because Defer writes the same thing (no model).

## Not installed

Ask with AskUserQuestion, options in this order:

1. `haiku (Recommended)`
2. `Undefined — choose at each spawn`
3. `sonnet`, `opus`

The rest of the models come via Other. Say in one line that the optional
`/plugin marketplace add agentic-tool-labs/agent-tools` then
`/plugin install task-gopher@agent-tools` (run by the user, in that order; an
agent cannot run `/plugin`) would let legwork go to task-gopher instead; until
then the bundled `wrangl:task-runner` does it.

## Writing the answer

- `Defer to task-gopher` or `Undefined`: `<ah> roster add --role <role>` with no
  `--model`.
- A model: `<ah> roster add --role <role> --model <model>`. While task-gopher is
  installed the CLI warns that this model defeats the handoff; the row is
  still written.

## Question tag

Every `AskUserQuestion` in this file carries `metadata.source` `ah:bootstrap:agent-roster:legwork`; the hook refuses an untagged ask from the Orchestrator.
