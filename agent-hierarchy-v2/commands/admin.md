---
description: Make this session the admin session from its own prompt (for Remote Control), or end that with `off`.
argument-hint: "[on|off|show]"
disable-model-invocation: true
---

The user ran `/wrangl:admin` with argument: `$ARGUMENTS`.

The UserPromptSubmit hook has already made this session the admin session, revoked it, or changed nothing; its one-line `wrangl:admin` result says which and why. Relay that line to the user, then run `ah admin show` once to confirm and relay its output. Do nothing else.
