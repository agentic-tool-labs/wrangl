// The lazily published slices of the mock: transcript headers and their entries, files:, tests:.
// Shapes: the transcript header, the entry and files: are what the C# lanes publish (TranscriptLane, TranscriptEntry, GitLane);
// the entry list through /list, ?expand= and tests: are specified in ui-perf (transcript lane T, lane Q) and have no producer yet.
import { DIR, TEAM, ACTIVITY_NAMES, sessionOf } from "./fixture.mjs";
import { envelope } from "./envelope.mjs";

export const TRANSCRIPT_NAMES = ["orchestrator", ...ACTIVITY_NAMES];
export const MAX_EXPAND = 256 * 1024;

export const transcriptIdOf = (name) => `transcript:${DIR}/${sessionOf(name)}`;
export const transcriptListIdOf = (name) => `list:transcript:${DIR}/${sessionOf(name)}`;
export const filesIdOf = (name) => `files:${DIR}/${TEAM}/${name}`;
export const testsIdOf = (sha) => `tests:${sha}`;

const TOOLS = [
  { name: "Read", input: { file_path: "/work/ah/web/ui/app.js" } },
  { name: "Edit", input: { file_path: "/work/ah/web/ui/app.js", old_string: "before", new_string: "after\nextra" } },
  { name: "Bash", input: { command: "printf 'mock output\\n'" } },
  { name: "Grep", input: { pattern: "mock", path: "/work/ah" } },
  { name: "Write", input: { file_path: "/work/ah/example.txt", content: "alpha\nbeta\ngamma" } },
  { name: "MultiEdit", input: { file_path: "/work/ah/example.txt", edits: [{ old_string: "one\ntwo", new_string: "replacement" }, { old_string: "removed", new_string: "" }] } },
];
const ENCODER = new TextEncoder();
// What the daemon adds to an entry for display: a row per block, naming its block rather than copying it.
const rowOf = (e) => (b, index) => {
  if (b.kind === "text") return { index, kind: e.turn_start !== true ? "text" : b.excerpt.startsWith("[hierarchy-") ? "brief" : "turn", cut: ENCODER.encode(b.excerpt).length >= 2040 };
  if (b.kind === "tool_use") return { index, kind: "tool", id: b.id };
  if (b.kind === "tool_result") return { index, kind: "result", tool_use_id: b.tool_use_id, error: b.is_error === true, lines: b.lines ?? 0 };
  return { index, kind: b.kind === "thinking" ? "thinking" : "other" };
};
export const servedEntry = (e) => (e.type === "oversize" && !e.blocks?.length
  ? { ...e, rows: [{ kind: "large" }] }
  : { ...e, rows: e.blocks.map(rowOf(e)), ...(e.turn_start === true ? { turn_tokens: e.turn_tokens ?? null } : {}) });

const textLines = (text) => (typeof text === "string" && text !== "" ? text.split("\n") : []);
const servedDiff = (name, input) => {
  const edits = name === "MultiEdit" ? input?.edits ?? [] : name === "Edit" ? [input ?? {}] : [{ new_string: input?.content }];
  return edits.flatMap((edit) => [...textLines(edit.old_string).map((text) => ({ kind: "del", text })), ...textLines(edit.new_string).map((text) => ({ kind: "add", text }))]);
};

const usage = (t) => ({ input_tokens: 1200 + t * 10, output_tokens: 300 + t * 5, cache_creation_input_tokens: 0, cache_read_input_tokens: 4000 + t * 50 });

function diffStat({ name, input }) {
  const lines = (text) => text === "" ? 0 : text.split("\n").length;
  if (name === "Edit") return { add: lines(input.new_string), del: lines(input.old_string) };
  if (name === "MultiEdit") return input.edits.reduce((sum, edit) => ({ add: sum.add + lines(edit.new_string), del: sum.del + lines(edit.old_string) }), { add: 0, del: 0 });
  if (name === "Write") return { add: lines(input.content), del: 0 };
  return undefined;
}

// What AH_MOCK_INTERNAL=1 adds to each turn of a busy transcript. `class` here is fixture data, not the rule (MessageClass in the daemon);
// tests/web/msg-filter.test.mjs holds these pairs to its golden.
const RELAY = 'Another Claude session sent a message:\n<cross-session-message from="uds:/tmp/1.sock">\nah mail from ';
const PLUGIN = "The ah2 plugin sent a message:\nah mail for member@team (1):\n";
export const INTERNAL_TEXTS = [
  [`${PLUGIN}- event resume`, "internal"],
  ["<task-notification>\n<task-id>a1</task-id>\n</task-notification>", "internal"],
  [`${RELAY}member@team to orchestrator@team (note): halfway there`, "internal"],
  ['[Cross-session idle notice] "uds:/tmp/1.sock" is idle now', "internal"],
  [`${PLUGIN}- event relay-needed: ah: mail entry 1 was not picked up`, "internal"],
  ["Stop hook feedback: ah: You have 2 messages. Run ah msg next.", "internal"],
  [`${RELAY}member@team to orchestrator@team (note): QUESTION: which branch?`, "needs_you"],
];
const BUSY = globalThis.process?.env?.AH_MOCK_INTERNAL === "1";

const TURN_COUNTS = new Map();

// Sets how many turns the mock builds for a member; null restores the default of 18.
export const setTurnCount = (name, turns) => (turns === null ? TURN_COUNTS.delete(name) : TURN_COUNTS.set(name, turns));

// The transcript of one member, oldest entry first: turns of a user message, thinking, a tool call, its result and a closing text.
export function buildTranscript(name, turns = TURN_COUNTS.get(name) ?? 18) {
  const entries = [];
  let offset = 0;
  const add = (type, blocks, extra = {}) => {
    const bytes = extra.bytes ?? 200 + blocks.reduce((n, b) => n + (b.bytes ?? b.excerpt?.length ?? 40), 0);
    entries.push(servedEntry({ offset, bytes, type, blocks, ...extra }));
    offset += bytes;
  };
  for (let t = 0; t < turns; t += 1) {
    addTurn(entries, add, name, t);
  }
  return entries;
}

function addTurn(entries, add, name, t) {
  const brief = t === 0;
  const tool = TOOLS[t % TOOLS.length];
  const stat = diffStat(tool);
  add("user", [{ kind: "text", excerpt: brief ? `[hierarchy-msg 20261018-000000-${name}] Build step ${t}` : `Turn ${t}: ${name}, next step please` }], { class: "work", turn_start: true, turn_ms: 8000 + t * 500 });
  add("assistant", [{ kind: "thinking", bytes: 900 + t * 7 }], { usage: usage(t) });
  add("assistant", [{ kind: "text", excerpt: `Looking at ${name}'s task, step ${t}.` }, { kind: "tool_use", id: `tu-${name}-${t}`, name: tool.name, ...(stat ? { diff_stat: stat } : {}) }], { usage: usage(t) });
  add("user", [{ kind: "tool_result", tool_use_id: `tu-${name}-${t}`, is_error: t % 7 === 3, bytes: 1200 + t * 311, lines: 10 + t * 3 }]);
  if (t === 9) {
    add("oversize", [], { bytes: 5_000_000 });
  }
  add("assistant", [{ kind: "text", excerpt: `Done with step ${t}.` }], { usage: usage(t) });
  if (BUSY) {
    for (const i of [t, t + 3]) {
      const [excerpt, cls] = INTERNAL_TEXTS[i % INTERNAL_TEXTS.length];
      add("user", [{ kind: "text", excerpt }], { class: cls, turn_start: true, turn_ms: 400 });
    }
  }
}

// A later turn appended to a live transcript.
export function appendTurn(entries, name) {
  const turns = entries.filter((e) => e.type === "user" && e.blocks.some((b) => b.kind === "text")).length;
  let offset = entries.length ? entries.at(-1).offset + entries.at(-1).bytes : 0;
  const add = (type, blocks, extra = {}) => {
    const bytes = extra.bytes ?? 200 + blocks.reduce((n, b) => n + (b.bytes ?? b.excerpt?.length ?? 40), 0);
    entries.push(servedEntry({ offset, bytes, type, blocks, ...extra }));
    offset += bytes;
  };
  addTurn(entries, add, name, turns);
}

// The header slice body: the usage of the current turn is summed from the last user text on.
export function headerOf(entries, asOf) {
  const lastTurn = entries.map((e, i) => (e.type === "user" && e.blocks.some((b) => b.kind === "text") ? i : -1)).filter((i) => i >= 0).at(-1) ?? 0;
  const turn = { input_tokens: 0, output_tokens: 0, cache_creation_input_tokens: 0, cache_read_input_tokens: 0 };
  for (const e of entries.slice(lastTurn)) {
    for (const k of Object.keys(turn)) {
      turn[k] += e.usage?.[k] ?? 0;
    }
  }
  const last = entries.at(-1);
  return { path_ok: true, length: last ? last.offset + last.bytes : 0, newest_offset: last?.offset ?? 0, oldest_held_offset: entries[0]?.offset ?? -1, turn_usage: turn, resets: 0, as_of: asOf };
}

// The full text of an entry's blocks, as `?expand=<offset>` serves it: capped at 256 KB.
export function expandOf(entries, offset) {
  const e = entries.find((x) => x.offset === Number(offset));
  if (!e) {
    return null;
  }
  const truncated = e.type === "oversize" || e.bytes > MAX_EXPAND;
  return {
    offset: e.offset,
    type: e.type,
    ...(e.subtype !== undefined ? { subtype: e.subtype } : {}),
    truncated,
    blocks: truncated ? [] : e.blocks.map((b) => {
      if (b.kind === "text") return { kind: b.kind, text: `${b.excerpt}\n(the full text of the block)` };
      if (b.kind === "thinking") return { kind: b.kind, text: "…" };
      if (b.kind === "tool_use") {
        const input = structuredClone(TOOLS.find((tool) => tool.name === b.name)?.input ?? {});
        return { kind: b.kind, id: b.id, name: b.name, input, ...(["Edit", "MultiEdit", "Write"].includes(b.name) ? { diff: servedDiff(b.name, input) } : {}) };
      }
      if (b.kind === "tool_result") {
        const content = Array.from({ length: b.lines ?? 6 }, (_, i) => `result line ${i + 1}`).join("\n");
        return { kind: b.kind, tool_use_id: b.tool_use_id, is_error: b.is_error, content: b.is_error ? [{ type: "text", text: content }] : content };
      }
      return { kind: b.kind };
    }),
  };
}

// A window of entries by byte-offset cursor, newest first. `after` reads older entries, `before` newer ones.
export function transcriptWindow(entries, { after, before, cap }) {
  let page;
  if (after !== null) {
    page = entries.filter((e) => e.offset < Number(after)).slice(-cap).reverse();
  } else if (before !== null) {
    page = entries.filter((e) => e.offset > Number(before)).slice(0, cap).reverse();
  } else {
    page = entries.slice(-cap).reverse();
  }
  const oldest = page.at(-1);
  return envelope({
    rows: page,
    head: entries.length ? String(entries.at(-1).offset) : null,
    tail: entries.length ? String(entries[0].offset) : null,
    next: oldest ? (entries.some((e) => e.offset < oldest.offset) ? String(oldest.offset) : null) : after,
    prev: page.length ? String(page[0].offset) : before,
  });
}

const HEADS = ["a1f3c9e", "b72d0e4", "c04a8b1", "d9e5f27", "e3b61c8"];

// Every lazily published slice: a transcript header per member, files: for each member, and tests: per head.
export function lazySlices(now, entriesByName) {
  const out = [];
  TRANSCRIPT_NAMES.forEach((name, i) => {
    out.push({ id: transcriptIdOf(name), as_of: now, body: headerOf(entriesByName.get(name), now) });
    const sha = HEADS[i % HEADS.length];
    out.push({
      id: filesIdOf(name),
      as_of: 0,
      body: {
        branch: `ah/${name}`,
        head: sha,
        upstream: `origin/ah/${name}`,
        ahead: i % 3,
        behind: 0,
        files: [
          { path: "agent-hierarchy-v2/web/ui/app.js", xy: " M" },
          { path: "agent-hierarchy-v2/web/ui/conversation.js", xy: "??" },
          { path: "agent-hierarchy-v2/tests/web/app.test.mjs", xy: "M " },
        ],
        files_total: 3,
        truncated: false,
      },
    });
  });
  for (const sha of new Set(HEADS.slice(0, TRANSCRIPT_NAMES.length))) {
    out.push({ id: testsIdOf(sha), as_of: now, body: { sha, sets: [{ set: "native", status: "pass", counts: { passed: 412, failed: 0, skipped: 3 }, flaky: [], finished_at: now }, { set: "shell", status: "fail", counts: { passed: 40, failed: 2, skipped: 0 }, flaky: ["roster-doctor"], finished_at: now }] } });
  }
  return out;
}
