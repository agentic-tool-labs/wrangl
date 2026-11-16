// The /list page envelope. `extra` members (a seed, archived) sit beside the fixed keys.
export const ENVELOPE_KEYS = ["version", "total", "head_cursor", "tail_cursor", "rows", "next", "prev"];

export function envelope({ rows, next = null, prev = null, total = null, head = null, tail = null, extra = {} }) {
  return { version: 1, total, head_cursor: head, tail_cursor: tail, rows, next, prev, ...extra };
}
