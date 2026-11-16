---
name: write-ticket
description: Write or edit an issue/ticket (any tracker) so a human who wasn't in the session can read it once and understand what is asked, why, and how they'll know it's done. Use when creating, rewriting, or reviewing a ticket, story, bug, or task description.
---

# Writing a ticket

A ticket is written for a human first. Test it: could a new teammate read it once, top to bottom, and explain the work and the finish line to someone else? If not, fix coherence before adding anything.

## Style

- Plain words, short sentences, one term per concept. Define any jargon once, inline.
- Tell it as a story: intent, then reason, then behavior, then proof of done.
- Never cite internal markers: decision labels (D3, Q1, H1), step numbers, spec section numbers, finding IDs. If the reader would need another document to decode a token, rewrite it in plain language.
- The ticket must stand alone. Linking a spec as optional background is fine; depending on it to make sense is not.

## Form (in this order, omit sections that don't apply)

1. **Summary** - one or two sentences of intent.
2. **Why this matters** - the problem or value, who feels it.
3. **What should happen** - the desired behavior in plain terms, not implementation.
4. **Decision needed first** - open questions in plain words, with the alternative and its cost.
5. **Background** - terms and context, defined once.
6. **Dependencies and order** - what must land or deploy before/after.
7. **Scope** - what changes.
8. **Acceptance criteria** - few, in human terms; each says what to check, where, and what evidence proves it.
9. **Residual risk** - what could still go wrong after this is done.
10. **Out of scope** - what this deliberately does not do.

If the work is operational (something to run or check) rather than a code change, say so in a bold banner at the top and in the title, so nobody mistakes it for a build.

## Substance

- State intent and outcome, not a task list of steps.
- Acceptance criteria are observable outcomes, not implementation checks.
- Surface unresolved decisions instead of burying an assumption.
- Detail aimed at implementers or LLMs (design rationale, test matrices, exact signatures, assumption lists, file lists, repro logs) goes in an attached markdown spec, or a collapsible "Agent notes" section at the end. Not in the body.

## Hygiene

- Leave fields you weren't asked to change alone (assignee, priority, status, labels). Never reassign.
- Check for an existing duplicate before creating.
- Prefix the title consistently if the team uses a convention for kinds of work.
