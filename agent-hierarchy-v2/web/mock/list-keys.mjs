// The query keys each kind of /list route accepts: after, before and limit everywhere, plus the key a kind adds.
// Any other key is a 400 bad_filter naming it.
const COMMON = ["after", "before", "limit"];

const EXTRA = [
  ["list:timeline:", ["from_ms", "to_ms"]],
  ["list:timeline-spans:", ["from_ms", "to_ms"]],
  ["list:transcript:", ["expand"]],
  ["list:exchanges:", ["member", "with"]],
  ["list:open:", ["member"]],
  ["list:feed:", ["member", "with"]],
  ["list:plan-changes:", ["item"]],
];

export function allowedKeys(listId) {
  const extra = EXTRA.find(([prefix]) => listId.startsWith(prefix));
  return new Set([...COMMON, ...(extra ? extra[1] : [])]);
}
