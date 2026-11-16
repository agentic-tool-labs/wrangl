// Folds a new served document into the one already held, so that every subtree that did not change keeps its old
// object identity and a component given it by reference can skip drawing.
//
// Array elements are matched by their stable id (id, path, node, key, or a pair such as team/name); elements with
// none, or lists whose ids repeat, are matched by position.

const idOf = (x) => {
  if (x === null || typeof x !== "object" || Array.isArray(x)) {
    return undefined;
  }
  const own = x.id ?? x.path ?? x.node ?? x.key;
  if (own !== undefined && own !== null) {
    return String(own);
  }
  if (x.team !== undefined && x.name !== undefined) {
    return `${x.team}/${x.name}`;
  }
  if (x.stream !== undefined && x.item !== undefined) {
    return `${x.stream}/${x.item}`;
  }
  const single = x.item ?? x.tree ?? x.box;
  return single === undefined || single === null ? undefined : String(single);
};

function byId(list) {
  const map = new Map();
  for (const x of list) {
    const id = idOf(x);
    if (id === undefined || map.has(id)) {
      return null;
    }
    map.set(id, x);
  }
  return map;
}

function mergeArray(prev, next) {
  // A list of ids or other primitives has no subtrees to keep; comparing it element by element only costs its length.
  if (next.length > 0 && (next[0] === null || typeof next[0] !== "object")) {
    return next;
  }
  const known = byId(prev);
  const out = next.map((x, i) => {
    const id = idOf(x);
    const before = known !== null && id !== undefined ? known.get(id) : known === null ? prev[i] : undefined;
    return before === undefined ? x : merge(before, x);
  });
  return out.length === prev.length && out.every((x, i) => x === prev[i]) ? prev : out;
}

export function merge(prev, next) {
  if (prev === next || prev === null || prev === undefined || next === null || typeof next !== "object" || typeof prev !== "object") {
    return next;
  }
  if (Array.isArray(next)) {
    return Array.isArray(prev) ? mergeArray(prev, next) : next;
  }
  if (Array.isArray(prev)) {
    return next;
  }
  const keys = Object.keys(next);
  const out = {};
  let same = keys.length === Object.keys(prev).length;
  for (const k of keys) {
    out[k] = merge(prev[k], next[k]);
    same &&= k in prev && out[k] === prev[k];
  }
  return same ? prev : out;
}
