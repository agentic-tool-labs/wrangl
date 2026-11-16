# kinds — agent-roster reference

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Read this when `--kind` or `--args` is on an `add` or `edit`: a non-Claude member.

## `--kind`: non-Claude members

`--kind <k>` (`kind` on `<ah> roster add`) picks which agent CLI Herdr starts
for this member. **Omitted means `claude`**, including for every roster file
written before this key existed, and an explicit `--kind claude` is not
written to the file at all — the default is total.

Herdr owns the list of installed kinds and it differs per machine and per
Herdr version, so this codebase validates only the *shape*
(`[a-z][a-z0-9-]*`) and never a membership list. An unknown kind is accepted
at `add` and fails at spawn with Herdr's own error, unmodified. Run
`herdr agent` to see what an install actually has.

Choosing a non-claude kind changes these, all enforced at `add`/`edit`:

| field | requirement |
|---|---|
| `route` | must be `pane` — `peer` and `subagent` are hard errors |
| `model` | a kind with a model mapping (`codex`) takes any model, checked by shape only (no whitespace or control characters) — the harness checks the name at its first turn. A kind with no mapping refuses it, naming `args` as the way to pass that harness's own model flag |
| `effort` | must be absent — it is the literal `--effort` Claude CLI flag and means nothing to another CLI |
| `auto-mode` | allowed for a kind with a permission mapping (translated to that CLI's own approvals setting); refused otherwise |
| `args` | optional; native CLI arguments, passed verbatim after Herdr's `--`. Refused on an advise-class member, and where they set the model or approvals a second time |
| transport | spawning needs a Herdr session (`HERDR_ENV=1`); `add`/`edit` still work anywhere |

`--args '<json-array>'` (`args` on `<ah> roster add`, a real array there) is how
a non-claude member gets any flag beyond the mapped `model` and `auto-mode`.
Each element is one argument and is shell-quoted before it reaches the launch
line. `args` is a **hard error for `kind: claude`**: for a Claude member the
validated `--model`/`--effort`/`--auto-mode` already fill that slot, and a
second unvalidated channel would let `args: ["--model","haiku"]` defeat the
ultra-advisor top-tier rule. It is rejected at `add`/`edit` and again at spawn.

No pre-screen exists for whether an `args` value keeps the agent interactive.
A flag that makes the target run and exit produces a Herdr startup timeout, and
the failure names the args as the likely cause. A pane is left open only when an
agent may still be live in it or the close itself failed: close it with
`herdr pane close <id>`.

An advise-class member (the Ultra-Advisor) of a non-claude kind needs a kind
with a model mapping and a model declared at the `opus` or `fable` tier
(`<ah> roster tier set <kind> <model> <opus|fable>`); an unmapped kind is
refused. It is briefed only through `<ah> roster deliver`, which is gated like
SendMessage, never by typing into its pane.

A member's `role` still picks its derived name and its roster slot, but the
role's `agents/*.md` contract is **not** loaded into a non-Claude agent. Put
whatever the role would have told it into the prompt instead.
