import { memberIds, memberOf, openBriefsOf, partyName } from "./select.js";

// The Tree view: team -> worktrees (grouped by work_root) -> members -> the open briefs addressed to each member,
// flattened to one list of rows with stable keys, so a long team is a virtual window.

const NO_ROOT = "(no worktree)";
const base = (path) => (path === NO_ROOT ? path : path.split("/").filter(Boolean).slice(-1)[0] ?? path);

// Distinct work roots of the team's worktree slices: the header's worktree count comes from here, never from a constant.
export function workRoots(slices, team) {
  const roots = new Set();
  for (const s of slices.values()) {
    if (s.id.startsWith(`worktree:${team.body.dir_id}/${team.body.name}/`) && s.body.work_root) {
      roots.add(s.body.work_root);
    }
  }
  return roots;
}

// The rows of the expanded tree. `collapsed` is a Set of row keys whose children are hidden.
export function flattenTree(slices, team, collapsed = new Set()) {
  if (!team) {
    return [];
  }
  const t = team.body;
  const members = memberIds(team).map((id) => memberOf(slices, team, id));
  const byRoot = new Map();
  for (const m of members) {
    const root = m.workRoot ?? NO_ROOT;
    byRoot.set(root, [...(byRoot.get(root) ?? []), { m, branch: m.branch }]);
  }
  const rows = [];
  const teamKey = team.id;
  rows.push({ key: teamKey, kind: "team", depth: 0, label: t.name, detail: `${members.length} members · ${Math.max(slices.get(`team-summary:${t.dir_id}/${t.name}`)?.body.worktrees ?? 0, workRoots(slices, team).size)} worktrees`, open: !collapsed.has(teamKey), expandable: true });
  if (collapsed.has(teamKey)) {
    return rows;
  }
  for (const root of [...byRoot.keys()].sort()) {
    const wtKey = `wr:${root}`;
    const inRoot = byRoot.get(root).sort((a, b) => (a.m.name < b.m.name ? -1 : 1));
    rows.push({ key: wtKey, kind: "worktree", depth: 1, label: base(root), detail: `${[...new Set(inRoot.map((x) => x.branch).filter(Boolean))].join(", ") || "—"} · ${inRoot.length}`, open: !collapsed.has(wtKey), expandable: true });
    if (collapsed.has(wtKey)) {
      continue;
    }
    for (const { m } of inRoot) {
      const open = openBriefsOf(slices, team, m.id).sort((a, b) => (a.id < b.id ? -1 : 1));
      const mKey = `m:${m.id}`;
      rows.push({ key: mKey, kind: "member", depth: 2, id: m.id, label: m.name, detail: m.role, role: m.role, ...ctxFields(m), state: m.state, open: !collapsed.has(mKey), expandable: open.length > 0, count: open.length });
      if (!collapsed.has(mKey)) {
        for (const b of open) {
          rows.push({ key: `b:${m.id}:${b.id}`, kind: "brief", depth: 3, label: b.body.slug, detail: `${partyName(b.body, "from")} → ${partyName(b.body, "to")}`, state: "open" });
        }
      }
    }
  }
  return rows;
}

export const rowKey = (r) => r.key;

const ctxFields = (m) => {
  const c = m.ctx;
  return { ctx: c.text, ctxTitle: c.title, ctxOver: c.over };
};
