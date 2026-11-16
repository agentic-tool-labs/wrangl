// What the daemon's TimelineSpansPager serves, for the mock: one lane per member with its spans and message marks over a window.

const wordOf = (activity) => (activity == null || activity === "unknown" ? "unknown" : activity === "idle" ? "idle" : activity === "blocked" || activity === "failed" ? "blocked" : "working");

function spansOf(rows, from, to, seed, tail) {
  const out = [];
  const push = (state, a, b) => {
    const start = Math.max(a, from);
    const end = Math.min(b, to);
    if (end > start) {
      out.push({ state: wordOf(state), from: start, to: end });
    }
  };
  if (rows.length === 0) {
    push(seed ?? tail, from, to);
    return out;
  }
  let state = seed ?? rows[0].from_state;
  let at = from;
  for (const r of rows) {
    push(state, at, r.at);
    state = r.to_state;
    at = r.at;
  }
  push(state, at, to);
  return out;
}

function marksOf(exchanges, from, to) {
  const out = [];
  for (const { id, body } of exchanges) {
    const created = Date.parse(body.created);
    const slug = body.title || body.slug;
    if (created >= from && created <= to) {
      out.push({ at: created, kind: "brief", slug, class: body.class ?? null, key: `brief:${id}` });
    }
    const responded = body.has_response ? Date.parse(body.response_created) : NaN;
    if (responded >= from && responded <= to) {
      out.push({ at: responded, kind: "response", slug, class: body.class ?? null, key: `response:${id}` });
    }
  }
  return out.sort((a, b) => a.at - b.at);
}

// members: [{id, name, session, tail, exchanges}]; rows: transitions newest first, of any subject.
export function lanesOf({ members, rows, from, to, tailed }) {
  const inWindow = rows.filter((r) => r.at >= from && r.at <= to).sort((a, b) => a.at - b.at);
  return members.map((m) => {
    const before = rows.filter((r) => r.subject === m.session && r.at < from).sort((a, b) => b.at - a.at)[0];
    return {
      id: m.id,
      name: m.name,
      spans: spansOf(inWindow.filter((r) => r.subject === m.session), from, to, before?.to_state ?? null, tailed ? m.tail : null),
      marks: marksOf(m.exchanges, from, to),
    };
  });
}
