# What is coming

This page lists a change that is under way but not available yet. Other pages do not describe it as shipped.

## Becoming the Orchestrator only when you ask

Today every session in a repository with the hierarchy on is made an Orchestrator automatically, by text added at
session start. A planned change will make this opt-in: a session will become the Orchestrator only when you run
`/wrangl:orchestrator`. The design is still being written, and until it ships nothing changes.

You can already use `/wrangl:orchestrator` (or its other name, `/wrangl:orchestrate`) to turn an ordinary session into
an Orchestrator without a restart, and `/wrangl:orchestrator off` to end that.
