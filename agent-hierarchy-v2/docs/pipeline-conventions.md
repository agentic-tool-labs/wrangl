# Pipeline conventions — `.claude/ah2/ah-conventions.json`

A team declares how `/wrangl:pipeline` behaves in its repo in one committed JSON file,
`.claude/ah2/ah-conventions.json`.
Every team-overridable pipeline choice reads from this file and from nowhere else.

## Where it is read from

Always the committed blob on origin's default branch, `<origin-default>:.claude/ah2/ah-conventions.json`,
never the working tree. `<origin-default>` is the first of these that resolves:

1. `refs/remotes/origin/HEAD`, by its symbolic target;
2. `refs/remotes/origin/main`;
3. `refs/remotes/origin/master`.

If none resolves, or the blob is absent, the repo has no conventions.

An agent can edit the working tree freely, and an injected instruction could delete or loosen a
local copy of the file. Editing the file on a branch changes nothing until the change is merged.
ah does not stop a push to the default branch; see [Pushes](#pushes).

Nothing here fetches. `origin`'s refs are as fresh as the last `git fetch origin`.

This is a separate file from `.claude/ah2/agent-hierarchy.json`. That file is the roster config:
`ah roster` writes it, and it may be personal and uncommitted. Conventions are team-shared,
committed, and never rewritten by a CLI.

## Schema, version 1

```json
{
  "version": 1,
  "issues": {
    "trigger_label": "ready-for-agent",
    "trusted_actors": ["octocat"]
  },
  "pr": {
    "draft": true,
    "issue_link": "refs",
    "reviewers": []
  },
  "ci_secrets": "none-on-branches",
  "protected_paths": ["infra/**"],
  "protected_branches": ["release/*"],
  "on_item_done": "my-team:tracker-sync"
}
```

| Key | Type | Default when absent | Meaning |
|---|---|---|---|
| `version` | integer, must be `1` | none; required | Schema version. |
| `issues.trigger_label` | non-empty string | none — issue runs refuse without it | The label that makes an issue eligible. |
| `issues.trusted_actors` | array of GitHub logins | the repo owner's login when the owner is a user account; **required** when it is an organization | Who may apply the trigger label, and whose comments count as input. |
| `pr.draft` | boolean | `true` | Open PRs as drafts. |
| `pr.issue_link` | `"refs"` \| `"closes"` | `"refs"` | Writes `Refs #N` or `Closes #N` in the PR body. |
| `pr.reviewers` | array of logins or `org/team` | `[]` | Reviewers requested after the PR is created. |
| `ci_secrets` | `"none-on-branches"` \| `"skip-ci"` | `"none-on-branches"` | The team's CI-secrets posture. `none-on-branches`: runs triggered by pushes to non-default branches or by pull requests don't use repo secrets. `skip-ci`: every agent commit's subject ends with `[skip ci]`, and the run checks it before each push. A team whose branch or PR runs can reach secrets must declare `skip-ci`. |
| `protected_paths` | array of globs | `[]` | Extends the baseline protected paths below; never replaces them. Planning and decision classification read it; no gate enforces it. |
| `protected_branches` | array of globs | `[]` | Validated with the rest of the file. No gate enforces it. |
| `on_item_done` | skill name | none | A skill the pipeline runs after each item, for the team's own tracker writes. It is invoked at least once per issue whose status is final, and may repeat after an interruption, so it must be idempotent. |

**Validation is strict.** Invalid JSON, a wrong type, `version` other than `1`, or **any unknown key
at any level** is a violation — so a typo such as `protected_path` cannot silently drop a
setting. On a violation issue runs refuse to start with one line naming the violation.
Plan and spec runs are unaffected.

### Globs

Globs in `protected_paths` and `protected_branches` match repo-relative POSIX paths or branch
short names. A pattern must match the **whole** string, anchored at both ends: `release/*` matches
`release/1` but not `release/1/x`; use `release/**` for any depth.

- `*` matches any run of characters except `/`;
- `?` matches one character except `/`;
- a whole `**` segment matches zero or more segments;
- everything else is literal — no negation, character classes or brace expansion. A pattern with
  no wildcard matches exactly.

## Pushes

ah has no push guard: it was removed in ah-simplify, and `ah push-guard` only prints a notice and
exits 0. ah blocks no push or merge; it records them. "Do not push" is an instruction in a member's
brief, and no gate enforces it. The real boundary is on GitHub: branch protection or a ruleset on the
default branch.

### Baseline protected paths

Planning and decision classification treat these as protected; no gate enforces them. Anchored at
the repo root:

- **CI:** `.github/**`, `.gitlab-ci.yml`, `.gitlab/**`, `.circleci/**`, `Jenkinsfile`,
  `.buildkite/**`, `.travis.yml`, `azure-pipelines.yml`, `bitbucket-pipelines.yml`, `.drone.yml`,
  `action.yml`, `action.yaml`.
- **Git and pre-commit hooks:** `.pre-commit-config.yaml`, `.husky/**`, `.githooks/**`,
  `.gitmodules`.
- **Agent config:** `**/.claude/**`, `**/.mcp.json`, `**/.claude-plugin/**`.
- **Plugin hooks:** `D/hooks/**` for every directory `D` (the repo root included) where
  `D/.claude-plugin/plugin.json` exists in the pushed commit or the range base. Ordinary
  `src/hooks/` directories are not flagged.

**This list does not cover code that CI executes** — tests, build scripts, `package.json` scripts,
Makefiles. No path list can. That is what the `ci_secrets` declaration is for.

## Push mode for issue runs

An issue run (`/wrangl:pipeline <issue-ref> ...` or `/wrangl:pipeline --labelled`) pushes after each commit with
no person present — **unattended** mode — only while the pipeline's CI-deploy check did not degrade
the run at bootstrap. Its failure is named `guard2-degraded`, the only value of the run anchor's
`push-mode-reasons` line besides `none`, and the run-start notification lists it.

If the check fails, or its result is unknown, the run is **degraded**: it commits locally,
never pushes during the run, and asks for one approval per item at the end. Plan and spec runs are
unaffected.

The run follows the `ci_secrets` posture, declared or defaulted; it decides only the `[skip ci]`
suffix:

- `"none-on-branches"` (the default when the key is absent): runs triggered by pushes to
  non-default branches or by pull requests are assumed not to use repo secrets, for example
  because secrets are environment-scoped with branch protection. The run adds nothing.
- `"skip-ci"`: every agent commit's subject ends with `[skip ci]`, and the run checks it before
  each push. A branch with a commit whose subject lacks it is not pushed: the item ends
  `local-only`, reason `skip-ci-missing`.

### Residual risk

Unattended mode means code shaped by third-party issue text reaches the remote with no person
present. It rests on three things:

1. the trust gate — a trusted actor applied the trigger label, vouching for what they saw;
2. branch protection or a ruleset on GitHub, which you set up, since ah blocks no push;
3. the team's CI-secrets posture, declared or defaulted.

It does not cover:

- a secret in a pushed commit: `/wrangl:pipeline` runs no secret scan (removed in ah-simplify);
- code that CI executes (tests, build scripts) when the posture is wrong. In particular, **a repo
  that does not declare `ci_secrets` but whose branch- or PR-triggered runs use secrets gets
  unattended pushes with no `[skip ci]` guard**, because the default assumes those runs use no
  secrets. Such a team must declare `"skip-ci"`;
- a model that pushes against its brief, or misuses `gh`: ah records these and stops neither;
- tracker writes through `on_item_done`, which only the skill's instructions govern.

Both defaults are deliberate choices: unattended mode over the more conservative option of
defaulting to degraded mode, and assuming no secrets on branch and PR runs over requiring every
team to declare its posture.
