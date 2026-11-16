// Height and window math of the transcript list. Pure: no DOM access.

export const FALLBACK_VIEW = 560;
export const OVERSCAN_PX = 600;
export const AT_END_PX = 32;
export const OLDER_PX = 200;
export const HEIGHT_EPSILON = 0.5;
export const MAX_CORRECTIONS = 2;

const ESTIMATES = { text: 56, tool: 36, result: 36 };

// The height a row is assumed to have until it has been measured.
export const estimateOf = (row) => ESTIMATES[row.kind] ?? 28;

// Prefix sums of the row heights: offsets[i] is the top of row i, offsets[rows.length] the total.
export function offsetsOf(rows, heights) {
  const offsets = new Float64Array(rows.length + 1);
  for (let i = 0; i < rows.length; i += 1) {
    offsets[i + 1] = offsets[i] + (heights.get(rows[i].key) ?? estimateOf(rows[i]));
  }
  return offsets;
}

// The first index in [0, count] whose predicate holds, for a predicate that is false then true.
function firstWhere(count, holds) {
  let lo = 0;
  let hi = count;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (holds(mid)) {
      hi = mid;
    } else {
      lo = mid + 1;
    }
  }
  return lo;
}

// The rows [from, to) that intersect [top - margin, top + view + margin].
export function windowAt(offsets, top, view, margin = OVERSCAN_PX) {
  const count = offsets.length - 1;
  const low = top - margin;
  const high = top + view + margin;
  const from = firstWhere(count, (i) => offsets[i + 1] > low);
  const to = Math.max(from, firstWhere(count, (i) => offsets[i] >= high));
  return [from, to];
}

// The row a paused reader is looking at: the first whose bottom lies below `top`, and how far `top` is into it.
export function anchorAt(rows, offsets, top) {
  const i = firstWhere(rows.length, (j) => offsets[j + 1] > top);
  return i >= rows.length ? null : { key: rows[i].key, delta: top - offsets[i] };
}

// The scroll position that puts the anchor row back where it was, or null when that row is no longer listed.
export function topOfAnchor(rows, offsets, anchor) {
  if (anchor === null) {
    return null;
  }
  const i = rows.findIndex((r) => r.key === anchor.key);
  return i < 0 ? null : offsets[i] + anchor.delta;
}

// Where a paused reader's anchor row goes after the listed rows changed (a filter or a search). The row itself when it is still listed;
// else the nearest earlier row of `all` that is, shown from its top; else "live", when nothing before it survives.
// Returns {top, anchor} (the scroll position and the anchor to keep), "live", or null when there is no anchor.
export function resolveAnchor(all, rows, offsets, anchor) {
  if (anchor === null) {
    return null;
  }
  const kept = rows.findIndex((r) => r.key === anchor.key);
  if (kept >= 0) {
    return { top: offsets[kept] + anchor.delta, anchor };
  }
  const listed = new Map(rows.map((r, i) => [r.key, i]));
  for (let j = all.findIndex((r) => r.key === anchor.key) - 1; j >= 0; j -= 1) {
    const at = listed.get(all[j].key);
    if (at !== undefined) {
      return { top: offsets[at], anchor: { key: rows[at].key, delta: 0 } };
    }
  }
  return "live";
}
