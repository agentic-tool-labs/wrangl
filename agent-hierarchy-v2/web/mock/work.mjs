// The work registry's served fields (AH_MOCK_WORK=1; not a PRESETS row, so the E4 harness does not run it): work ids, external ids and open
// blockers on board items, a project and a goal row in the backlog, and the `item-log:` slices. Among the external URLs one is not http(s),
// because the field is untrusted. ah-100 is the worst case the slice can hold: 200 entries of 8 KiB and a description at the cap.
const MIN = 60_000;
const BODY_8K = "A line of what happened, as the member recorded it.\n".repeat(158);

// A description in the write-ticket form. ah-100 carries it at the store's 8 KiB cap.
const TICKET = `**Summary**
Let a person read what a work item is for, right where they open it.

**Why this matters**
Titles are short and written in a hurry. Someone opening an item a week later cannot tell what was asked, why, or when it is done, and has to ask the agent that wrote it.

**What should happen**
The item's detail shows its description above its notes, on the Board and over the Sitrep alike.
An item with no description shows nothing extra.

**Decision needed first**
None.

**Acceptance criteria**
- Open an item with a description on the Board: **Description** sits above Notes, with its headings and lists readable.
- Open one with none: there is no empty section.
- Text an agent wrote cannot run or draw anything: <img src=x onerror=alert(1)> shows as these characters.

**Out of scope**
Links, tables and any other markdown.`;
const criterion = (n) => `\n- Check ${n}: the description reads the same after the board redraws, and the line keeps its place in the list.`;
const TICKET_8K = `${TICKET}\n\n**More checks**${Array.from({ length: 66 }, (_, i) => criterion(i + 1)).join("")}`;

const logBody = (id, now, heavy, length = heavy ? 200 : 4) => ({
  id,
  description: heavy ? TICKET_8K : id === "ah-101" ? TICKET : null,
  entries: Array.from({ length }, (_, i) => ({
    ts_ms: now - (length - i) * 37 * MIN,
    type: ["state", "dispatch", "coding", "note"][i % 4],
    title: ["Moved to In progress", "Brief sent to implementor-1", "feat(work): render the id chip", "Picked up after a restart"][i % 4],
    by: i % 4 === 0 ? null : "implementor-1",
    body: heavy ? BODY_8K : i % 2 ? "The body of this entry, as recorded.\nA second line." : null,
  })),
  more: heavy ? 37 : 0,
  links: { blocked_by: ["ah-3", "ah-7"], blocks: ["ah-102"] },
  attachments: [
    { ref: "docs/specs/ah-work-items.md", kind: "spec", title: null, sha: "f2d7a9d9c1" },
    { ref: "notes/draft.md", kind: "file", title: "Draft notes", sha: null },
    { ref: "https://example.com/design", kind: "url", title: "Design", sha: null },
    { ref: "javascript:alert(1)", kind: "url", title: "Not a link", sha: null },
  ],
  children: ["ah-104", "ah-105"],
});

const EXT = [
  { ext_id: "linear:ABC-1", ext_url: "https://linear.app/example/issue/ABC-1" },
  { ext_id: "evil:1", ext_url: "javascript:alert(1)" },
  { ext_id: "jira:OPS-9" },
];

// A waiting item's question in the form the Orchestrator writes it (AH_MOCK_WAITING=1 too): what is asked, why it reaches the user, what each
// option and Not needed do, with a path and a command, longer than the card's four-line fold.
const QUESTION = `## What is asked
Which monospace font the 2.5 upgrade guide uses for its code samples. It reaches you because the guide is the first page new users read.

## Options
- **JetBrains Mono**: bundled with the guide (\`docs/fonts/JetBrainsMono.woff2\`, 92 KB); every sample renders the same everywhere.
- **System monospace**: no download; samples follow the reader's own font, so column alignment can differ.
- **Not needed**: the guide keeps today's font and this item closes.

## To check
\`\`\`
ah docs build --guide upgrade-2.5
open docs/out/upgrade-2.5.html
\`\`\``;

export function withWork(out, slice, dir, now) {
  let n = 100;
  const logs = [];
  const work = (id, i, at) => {
    logs.push(slice(`item-log:${dir}/${id.slice(3)}`, logBody(id, now, id === "ah-100"), at));
    return { work_id: id, blocked_by_open: i % 4 === 1 ? 2 : 0, ...EXT[i] };
  };
  return [...out.map((s) => {
    if (s.id.startsWith(`plan-items:${dir}/`)) {
      return { ...s, body: { ...s.body, items: s.body.items.map((item, i) => ({ ...item, ...work(`ah-${n++}`, i, now) })) } };
    }
    if (s.id === `backlog:${dir}`) {
      const row = (key, title, kind, rank, parent) => ({ key, title, kind, rank, parent, state: "not_started", wave: "now", work_id: `ah-${key.slice(1)}`, blocked_by_open: 0 });
      return { ...s, body: { ...s.body, items: [row("b90", "The beta", "project", 0), row("b91", "Ship it", "goal", 0, "b90"), ...s.body.items.map((item, i) => ({ ...item, ...work(`ah-${item.key.slice(1)}`, i, now) }))] } };
    }
    if (s.id.startsWith(`questions:${dir}/`) && s.body.items?.length) {
      logs.push(slice(`item-desc:${dir}/900`, { id: "ah-900", description: QUESTION }, now));
      return { ...s, body: { ...s.body, items: s.body.items.map((item) => ({ ...item, work_id: "ah-900" })) } };
    }
    return s;
  }), ...logs];
}
