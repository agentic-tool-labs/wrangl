# The UX-Engineer and its design skills

The UX-Engineer (`ux-engineer`) is a built-in role for web UI work: CSS, markup,
visual tokens, layout, interaction and accessibility. It ships with the plugin, so
you do not define it. It works with no extra skills installed; the skills below
only give it more to draw on.

## When it is used

It is an alternative to the Implementor for web UI work, the same
way the Docs-Writer is for documentation. Class `implement`, model `opus` by
default. Its route text is "Web UI work: CSS, markup, visual tokens, layout,
interaction and accessibility under `web/` (not daemon, data or protocol work)".

- Work on how the web UI looks and behaves goes to the UX-Engineer.
- Daemon, data and protocol work stays with the Implementor, even when a UI shows it.
- A spec that mixes both is split into phases.

Its diffs are reviewed by the same Reviewer and pass the same gates as the
Implementor's. See `ah roster role list` for the row.

## What it proves

A UX change is not done when the code compiles. The role is required to show:

- **Screenshots in both themes.** Each changed view is captured dark and light at
  1440x900 and 1024x700. It serves the mock UI (`web/mock/serve.mjs`), never your
  live UI, in a headless browser with a throwaway profile, and it removes the
  browser, the server and the profile afterwards.
- **Contrast.** The computed ratio for every new or changed colour pair
  (WCAG 2.2 AA: 4.5:1 for text, 3:1 for non-text).
- **The performance gate.** Any change that touches rendering runs the perf harness
  (`tests/perf/ui-e4.mjs`) and reports its numbers. A regression past the gate is a
  failure.

## Design skills

Design skills are `SKILL.md` folders that Claude Code loads on request. The
UX-Engineer loads any it finds in the session's skill list. Where a skill disagrees
with the web UI's own tokens, the tokens win.

### Installed by default: `frontend-design`

`scripts/install.sh --apply` installs Anthropic's `frontend-design` skill
(Apache-2.0) into `~/.claude/skills/frontend-design` (or `$CLAUDE_CONFIG_DIR/skills/`
when that is set). It is downloaded from a pinned commit of `anthropics/skills`, and
every file's sha256 is checked before anything is written. A failed download is
reported and does not fail the install. It needs no question and runs without a
terminal.

It never touches a copy that is yours:

- If the folder does not exist, it is created.
- If the folder holds exactly what the installer wrote earlier, it is replaced when
  the pin moves.
- Any other folder, including one you edited or installed yourself, is left alone.
- Nothing is ever deleted. To remove it, delete the folder.

Skip it with `--no-design-skills` (this also skips the offer below). Inside an agent
session (a Claude Code session) the installer skips it and tells you to run it from
a terminal.

### Optional, third-party: ask first

These are third-party, are never bundled with Wrangl, and are never installed without a
yes or `--with-ux-skills`:

| Source | Gives you | Install |
|---|---|---|
| `vercel-labs/agent-skills` | `web-design-guidelines`, `composition-patterns` | By hand only: `npx skills add vercel-labs/agent-skills`. Wrangl never runs `npx`. |
| `jakubkrehel/skills` | plugin `interfaces`: `better-interface`, `better-layout`, `better-typography`, `better-colors`, `better-accessibility`, `interface-review` | Claude Code plugin |
| `addyosmani/web-quality-skills` | plugin `web-quality-skills`: `accessibility`, `performance`, `core-web-vitals`, `web-quality-audit` | Claude Code plugin |

At a terminal, `install.sh --apply` lists the ones you are missing and asks once
(`[Y/n]`, Enter means yes). Yes runs `claude plugin marketplace add` and
`claude plugin install` for the two plugins, and prints the manual step for
vercel-labs. Without a terminal it installs nothing and prints how. A failed install
is reported and the rest of the install carries on.

To add them later, re-run with the flag, or run these inside Claude Code:

```
/plugin marketplace add jakubkrehel/skills
/plugin install interfaces@interfaces
/plugin marketplace add addyosmani/web-quality-skills
/plugin install web-quality-skills@addy-web-quality-skills
```

The licence shown for the three third-party sources is MIT, as the installer prints
it. Check each repository's own licence before you rely on that.

### Flags

```
install.sh --apply --no-design-skills   # no frontend-design, no offer
install.sh --apply --with-ux-skills     # install the missing third-party sources, no question
install.sh --apply --no-ux-skills       # do not offer them
```

- `--with-ux-skills` together with `--no-ux-skills` or `--no-design-skills` is a
  usage error (exit 2).
- `--with-ux-skills` is refused when run from inside a Claude Code session: an agent
  does not install software on your machine. Run `install.sh` from a terminal.

### Check what is installed

```
ah roster doctor
```

The `ux-skills` row counts four sources: `frontend-design`, `vercel-labs`,
`jakubkrehel` and `addyosmani`. All four is `ok`. Fewer is a warning that names each
missing one with how to add it, and ends "ux-engineer works without them". A warning
here needs no action unless you want the skills.
