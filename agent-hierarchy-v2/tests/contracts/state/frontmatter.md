# The message file's frontmatter: line format

From the recorded samples (`../msgs-request/*.md`, `../msgs-response/*.md`), written by `msg.mjs new` and `msg.mjs fill`.

- The file starts with a line `---`, then one `key: value` line per key, then a line `---`, then a blank line, then the body.
- **Key order is fixed:** `id`, `type`, `to`, `from`, `slug`, `parent`, `reason`, `eta`, `to_name`, `from_name`, `team`,
  `team_file`, `team_guide`, `created`.
- **A value is written bare:** no quotes, whatever it holds (ids, slugs, paths, an offset timestamp).
- **Null is spelled `null`** (not empty, not `~`): `parent`, `reason`, `eta`, `to_name`, `from_name`, `team`, `team_file`,
  `team_guide` when unset.
- `created` is a local-offset ISO string (`2026-01-01T00:00:00-04:00`), the same kind the `iso-local` normalizer rule maps.
- `fill` keeps the frontmatter byte for byte and replaces everything after the closing `---` line.
- The body of a fresh request is the seven-section skeleton (`## [0] tldr` with the six numbered bullets, then
  `## [1] goal` through `## [6] want_back`, each holding `- none`).
- The file ends with a newline.
