# Spec process rules

Standing rules for how a spec round in `agent-hierarchy-v2/docs/specs/` is
written and reconciled. Scoped to `agent-hierarchy` — spec rounds only happen
here, not at the repo root.

## Spec status sections are written last

A spec's status-bearing sections — the test table, NEEDS-EVIDENCE, and open
items — are reconciled as the last step of a round, after the code and tests
have landed, by whoever landed them. They are never dictated in advance of the
change they describe.

Design and rationale prose can be written ahead as normal. This rule applies
specifically to sections that make claims about what is currently true.

**Why:** a spec written before the round it describes is accurate when
written and stale when landed, and commit messages are written from specs —
so a stale claim becomes the permanent record. This happened three times in
the 0035/0036 workstream, in both directions: coverage overstated (0035 r2),
then understated (0035 r3), then a test table describing a superseded test
design and a blind spot as covered (0036). The direction
alternates; the mechanism does not.

## Mutation-testing standard

A test claimed as coverage for a specific behavior must be seen **failing**
against a deliberately broken (mutated) implementation of that behavior
before it is scored as real coverage. A test that only ever runs against
correct code has not demonstrated it can catch the bug it claims to guard
against — it may be testing nothing.

Worked example (0035's T5, review round r3): T5 passed while testing nothing.
Deleting the `-c` flag from `splitCwd` at `layoutAndLaunch`'s
`execFileSync` call (`ah roster:850`) — a real, plausible regression — made
T5 fail, proving it was real coverage of that path. As a control, a different
mutation (deleting the plan-string construction at `ah roster:254`) left T5
passing, confirming T5's failure on the first mutation was specific to what it
actually claimed to cover, not incidental.

Adopted in 0035's review; re-applied throughout 0036 — each of
those reviews' falsifying tests was confirmed failing against the pre-fix
implementation, then restored and confirmed passing, before being scored as
coverage.
