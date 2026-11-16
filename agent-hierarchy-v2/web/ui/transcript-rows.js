import { firstTeam } from "./select.js";

// The transcript helpers the always-loaded modules (hover, expand, app) share with the lazily loaded Conversation view.

const mb = (bytes) => (bytes / 1_000_000).toFixed(1);

// The drawn lines of a chronological entry list, from each entry's served rows. A tool call merges the chip of its result on the served id
// when that result is loaded, and a result whose call is not loaded stands alone. The turn number is a position in the loaded list.
export function transcriptRows(entries) {
  const results = new Map();
  const uses = new Set();
  for (const e of entries) {
    for (const r of e.rows ?? []) {
      if (r.kind === "result") {
        results.set(r.tool_use_id, { row: r, offset: e.offset });
      } else if (r.kind === "tool") {
        uses.add(r.id);
      }
    }
  }
  const rows = [];
  let turn = 0;
  for (const e of entries) {
    for (const row of e.rows ?? []) {
      const b = e.blocks?.[row.index];
      const key = `${e.offset}:${row.index}`;
      if (row.kind === "large") {
        rows.push({ key: `${e.offset}:big`, offset: e.offset, kind: "large", label: `entry too large (${mb(e.bytes)} MB)` });
      } else if (row.kind === "turn" || row.kind === "brief") {
        // Internal traffic numbers no turn, so a turn keeps its number whether it is shown or not.
        turn += e.class === "internal" ? 0 : 1;
        rows.push({ key, offset: e.offset, kind: row.kind, internal: e.class === "internal", turn, seconds: e.turn_ms == null ? null : Math.round(e.turn_ms / 1000), index: row.index, label: b.excerpt, expandable: true, truncated: row.cut });
      } else if (row.kind === "text") {
        rows.push({ key, offset: e.offset, index: row.index, kind: "text", internal: e.class === "internal", label: b.excerpt, expandable: true, truncated: row.cut });
      } else if (row.kind === "tool") {
        const r = results.get(row.id);
        rows.push({ key, offset: e.offset, kind: "tool", id: row.id, name: b.name, label: b.name, stat: b.diff_stat ?? null, resultOffset: r?.offset ?? null, done: r !== undefined, error: r?.row.error === true, lines: r?.row.lines ?? null, expandable: true });
      } else if (row.kind === "result") {
        if (!uses.has(row.tool_use_id)) {
          rows.push({ key, offset: e.offset, kind: "result", id: row.tool_use_id, name: "result", label: `result · ${row.lines ?? 0} lines`, done: true, error: row.error === true, lines: row.lines ?? null, expandable: true });
        }
      } else if (row.kind === "thinking") {
        rows.push({ key, offset: e.offset, kind: "thinking", label: `thinking · ${b.bytes} B` });
      } else {
        rows.push({ key, offset: e.offset, kind: "other", label: b.kind });
      }
    }
  }
  return rows;
}

export const idsOf = (snap, memberId) => {
  const team = firstTeam(snap.slices, snap.ui);
  const session = snap.slices.get(memberId)?.body.session;
  if (!team || !session) {
    return { team, hid: null, lid: null };
  }
  const hid = `transcript:${team.body.dir_id}/${session}`;
  return { team, hid, lid: `list:${hid}` };
};
