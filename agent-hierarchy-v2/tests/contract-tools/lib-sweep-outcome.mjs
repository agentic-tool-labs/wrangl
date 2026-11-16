// The sweep-outcome divergence scope.
//
// A team record whose Orchestrator is only undecided is kept by native and was swept (or reported orphaned) by the recorded Node
// run. The scope names exactly what that one difference changes on the recorded side, and nothing else:
//
//   stdoutLines   lines of the recorded `stdout` text (each followed by its line break) that the kept team's absence of a sweep
//                 or of an orphan notice removes. A line that is still in the actual stdout is not masked by this: only the
//                 recorded side loses it, so an actual line that differs, or an extra one, still fails the row.
//   keptStdoutLines  lines of the actual `stdout` text that report the kept team (a team the recorded run had already swept); only
//                 the actual side loses them.
//   treePaths     paths in the recorded `tree_diff` (an entry of added, changed or removed) that only a sweep writes or removes; a
//                 trailing `*` makes the path a prefix.
//   opLines       operation-log files in the recorded `tree_diff` that lose their lines whose `result` is exactly `{"written":false}`
//                 (the status refresh a sweep follows with).
//
// The status document the kept team changes is compared through the status-team view (see lib-ledger.mjs) for `statusTeam`; this
// file does not touch it.

const isObject = (v) => v !== null && typeof v === "object" && !Array.isArray(v);

const matches = (path, wanted) => wanted.some((w) => (w.endsWith("*") ? path.startsWith(w.slice(0, -1)) : path === w));

// The stdout text holds its line breaks as the two characters backslash and n. A line is whole when it starts the text or follows
// such a break (or the quote that opens the string) and is itself followed by one; the first whole line equal to the entry goes, once.
function withoutLines(text, lines) {
  let out = text;
  for (const line of lines) {
    const target = `${line}\\n`;
    let from = 0;
    for (;;) {
      const at = out.indexOf(target, from);
      if (at < 0) break;
      const before = at === 0 ? "" : out[at - 1];
      if (at === 0 || before === "\"" || (before === "n" && out[at - 2] === "\\")) {
        out = out.slice(0, at) + out.slice(at + target.length);
        break;
      }

      from = at + 1;
    }
  }

  return out;
}

function withoutUnwrittenOps(content) {
  return content
    .split("\n")
    .filter((l) => {
      if (l === "") return true;
      try {
        const result = JSON.parse(l)?.result;
        return !(isObject(result) && Object.keys(result).length === 1 && result.written === false);
      } catch {
        return true;
      }
    })
    .join("\n");
}

function actualWithoutKeptLines(actual, lines) {
  if (!isObject(actual) || typeof actual.stdout !== "string" || lines.length === 0) return actual;
  return { ...actual, stdout: withoutLines(actual.stdout, lines) };
}

/** The recorded value with the sweep's own outputs removed and the actual value without the lines that report the kept team. */
export function sweepOutcomeViews(scope, expect, rawActual) {
  const actual = actualWithoutKeptLines(rawActual, scope.keptStdoutLines ?? []);
  if (!isObject(expect)) return { expect, actual };
  const out = { ...expect };
  const lines = scope.stdoutLines ?? [];
  if (typeof out.stdout === "string" && lines.length > 0) out.stdout = withoutLines(out.stdout, lines);

  const tree = out.tree_diff;
  if (isObject(tree)) {
    const paths = scope.treePaths ?? [];
    const ops = scope.opLines ?? [];
    const kept = { ...tree };
    for (const key of ["added", "changed"]) {
      if (!Array.isArray(tree[key])) continue;
      kept[key] = tree[key]
        .filter((e) => !(isObject(e) && typeof e.path === "string" && matches(e.path, paths)))
        .map((e) => (isObject(e) && typeof e.content === "string" && ops.includes(e.path) ? { ...e, content: withoutUnwrittenOps(e.content) } : e));
    }

    if (Array.isArray(tree.removed)) kept.removed = tree.removed.filter((p) => !(typeof p === "string" && matches(p, paths)));
    out.tree_diff = kept;
  }

  return { expect: out, actual };
}
