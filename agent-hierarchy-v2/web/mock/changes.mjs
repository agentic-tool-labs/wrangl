import { envelope } from "./envelope.mjs";

// The plan-changes pager: rows newest first, ordered by (at, stream, seq), with that tuple as the cursor. `after` reads rows older than
// the cursor, `before` newer ones; `item` keeps one item's changes.

const tuple = (c) => [Date.parse(c.at), c.stream ?? "", c.seq];
export const changeCursor = (c) => tuple(c).join("|");

function parse(cursor) {
  const [at, stream, seq] = String(cursor).split("|");
  return Number.isFinite(Number(at)) && seq !== undefined ? [Number(at), stream, Number(seq)] : null;
}

// Negative when `a` sorts before `b` in the list order (newer first).
const order = (a, b) => b[0] - a[0] || (a[1] < b[1] ? 1 : a[1] > b[1] ? -1 : 0) || b[2] - a[2];

export const SLICE_ROWS = 200;

// The lane's slice body: the newest SLICE_ROWS changes, the count of every change held, and the cursor of the oldest row shown.
export function changesBody(changes) {
  const rows = [...changes].sort((x, y) => order(tuple(x), tuple(y))).slice(0, SLICE_ROWS);
  return { rows, total: changes.length, tail_cursor: rows.length ? changeCursor(rows.at(-1)) : null };
}

export function changesWindow(changes, { after = null, before = null, cap, item = null }) {
  const all = changes.filter((c) => item === null || c.item === item).sort((x, y) => order(tuple(x), tuple(y)));
  let page;
  let more;
  if (after !== null) {
    const cut = parse(after);
    const rest = cut ? all.filter((c) => order(tuple(c), cut) > 0) : [];
    page = rest.slice(0, cap);
    more = rest.length > cap;
  } else if (before !== null) {
    const cut = parse(before);
    const newer = cut ? all.filter((c) => order(tuple(c), cut) < 0) : [];
    page = newer.slice(-cap);
    more = true;
  } else {
    page = all.slice(0, cap);
    more = all.length > cap;
  }
  return envelope({
    rows: page,
    total: all.length,
    head: all.length ? changeCursor(all[0]) : null,
    tail: all.length ? changeCursor(all.at(-1)) : null,
    next: page.length ? (more ? changeCursor(page.at(-1)) : null) : after,
    prev: page.length ? changeCursor(page[0]) : before,
  });
}
