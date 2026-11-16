// The mock of GET and POST /ui/v1/rules: the same reply shapes as the daemon's, on rows kept in memory so a dialog can be
// driven through add, edit, override, switch off and remove. The team named "ghost" is the unknown team. The real resolver is RulesOfEngagement.Resolve; this is a fixture's copy.
const RANK = { goal: 3, project: 2, team: 1, repo: 0 };
const LIVE = "members see this at their next session start, compaction or brief";
const MAX_TEXT = 300;
const ITEMS = [
  { id: "mock-3", kind: "project", title: "Ship the beta" },
  { id: "mock-12", kind: "goal", title: "Faster reviews", parent: "mock-3" },
];

function row(level, scope, key, text, off = false, by = "user", said = null) {
  return { key, text, off, level, scope, by, said, created_ms: 1, updated_ms: 1 };
}

// A decision rule (spec-b19 slice 3): proposed until accepted_at is set. The daemon adds these fields to a decision row only.
function decision(level, scope, key, text, applies, choose, acceptedAt = null) {
  return { ...row(level, scope, key, text, false, "orchestrator"), kind: "decision", applies, choose, proposed: acceptedAt === null, accepted_at: acceptedAt, accepted_by: acceptedAt === null ? null : "user" };
}
const DECIDE = ({ kind, applies, choose, proposed, accepted_at, accepted_by }) => (kind ? { kind, applies, choose, proposed, accepted_at, accepted_by } : {});

function chainOf(id) {
  const item = ITEMS.find((i) => i.id === id);
  return item ? [item, ...chainOf(item.parent)] : [];
}

function resolve(rows, applies) {
  const best = new Map();
  for (const r of rows.filter(applies)) {
    best.set(r.key, [...(best.get(r.key) ?? []), r].sort((a, b) => RANK[b.level] - RANK[a.level]));
  }
  const rules = [];
  const off = [];
  for (const [key, [winner, ...rest]] of best) {
    if (winner.off) {
      off.push({ key, level: winner.level, scope: winner.scope });
    } else {
      const { key: _k, text, level, scope, by, said, updated_ms } = winner;
      rules.push({ key, text, level, scope, by, said, updated_ms, overrides: [...new Set(rest.map((r) => r.level))], ...DECIDE(winner) });
    }
  }
  const order = (a, b) => RANK[b.level] - RANK[a.level] || a.key.localeCompare(b.key);
  return { rules: rules.sort(order), off: off.sort(order) };
}

// `decisions` names a team to seed with one proposed and one accepted decision rule; the served mock has them, the unit tests' default does not.
export function createRules({ decisions = null } = {}) {
  const rows = [
    row("repo", "", "ship-small", "Keep each commit reviewable."),
    row("team", "mock-team", "no-new-deps", "Do not add dependencies without asking.", false, "orchestrator", "ask me before adding a dependency"),
    row("goal", "mock-12", "ship-small", "One reviewer per change on this goal."),
    ...(decisions ? [
      decision("team", decisions, "retry-flaky", "Rerun a flaky test once before calling it a failure.", "A test fails once and passes on a rerun", "Rerun it once; report it as flaky, not failed"),
      decision("team", decisions, "small-fixes", "Ship a reviewed one-line fix without asking.", "A reviewed fix touches one line and no public interface", "Ship it", Date.UTC(2026, 10, 7, 15, 42)),
    ] : []),
  ];
  const refuse = (status, code, message) => [status, { code, message }];
  const atRepo = (r) => r.level === "repo";
  const atTeam = (team) => (r) => atRepo(r) || (r.level === "team" && r.scope === team);
  const atItem = (team, id) => {
    const chain = chainOf(id).map((i) => i.id);
    return (r) => atTeam(team)(r) || ((r.level === "goal" || r.level === "project") && chain.includes(r.scope));
  };

  function read(team) {
    if (!team) {
      return refuse(400, 2, "team is required");
    }
    if (team === "ghost") {
      return refuse(400, 2, `no team ${team}`);
    }
    return [200, {
      code: 0,
      team,
      rows: rows.map((r) => ({ ...r })),
      resolved: {
        repo: resolve(rows, atRepo),
        team: resolve(rows, atTeam(team)),
        items: Object.fromEntries(ITEMS.map((i) => [i.id, resolve(rows, atItem(team, i.id))])),
      },
      items: ITEMS.map(({ id, kind, title }) => ({ id, kind, title })),
      max_text: MAX_TEXT,
      live: LIVE,
    }];
  }

  function write({ op, team, level, scope, key, text, applies, choose } = {}) {
    if (team === "ghost") {
      return refuse(400, 2, `no team ${team}`);
    }
    if (!team || !op || !key || !(level in RANK) || !["add", "edit", "off", "remove", "accept"].includes(op)) {
      return refuse(400, 2, "op, team, level and key are required");
    }
    const at = level === "repo" ? "" : scope || (level === "team" ? team : "");
    if (level === "goal" || level === "project") {
      const item = ITEMS.find((i) => i.id === at);
      if (!item) {
        return refuse(404, 1, `no work item ${at}`);
      }
      if (item.kind !== level) {
        return refuse(400, 2, `${at} is a ${item.kind}, not a ${level}`);
      }
    }
    if (!/^[a-z0-9-]{1,40}$/.test(key)) {
      return refuse(400, 2, "a rule key is 1 to 40 of a-z, 0-9 and -");
    }
    if ((op === "add" || op === "edit") && !text?.trim()) {
      return refuse(400, 2, "a rule needs text");
    }
    if ((op === "add" || op === "edit") && text.length > MAX_TEXT) {
      return refuse(400, 2, `a rule's text is at most ${MAX_TEXT} characters; this has ${text.length}`);
    }
    const index = rows.findIndex((r) => r.level === level && r.scope === at && r.key === key);
    const done = (message, rule) => [200, { code: 0, message, rule, live: LIVE }];
    if (op === "add") {
      if (index >= 0) {
        return refuse(409, 3, `rule ${key} already exists at this ${level}; use edit`);
      }
      const added = row(level, at, key, text);
      rows.push(added);
      return done(`added rule ${key}`, added);
    }
    if (op === "off") {
      if (index >= 0) {
        rows[index] = { ...rows[index], text: "", off: true };
        return done(`switched off rule ${key}`, rows[index]);
      }
      if (!rows.some((r) => r.key === key && RANK[r.level] < RANK[level])) {
        return refuse(404, 1, `rule ${key} applies at no broader level and has no row here`);
      }
      const added = row(level, at, key, "", true);
      rows.push(added);
      return done(`switched off rule ${key}`, added);
    }
    if (index < 0) {
      return refuse(404, 1, `no rule ${key} at this ${level}`);
    }
    if (op === "accept") {
      if (rows[index].kind !== "decision") {
        return refuse(400, 2, `rule ${key} is a guide rule; only a decision rule is accepted`);
      }
      const was = rows[index];
      if ((text !== undefined && text !== was.text) || (applies !== undefined && applies !== was.applies) || (choose !== undefined && choose !== was.choose)) {
        return [409, { code: 3, reason: "rule-changed", message: `rule ${key} changed since it was shown, so it was not accepted; it now reads: when ${was.applies}, choose ${was.choose}. ${was.text}`, rule: was }];
      }
      rows[index] = { ...rows[index], proposed: false, accepted_at: Date.now(), accepted_by: "user" };
      return [200, { code: 0, message: `accepted rule ${key}`, rule: rows[index], live: LIVE, via: "web" }];
    }
    if (op === "edit") {
      // An edited decision rule returns to proposed.
      rows[index] = { ...rows[index], text, off: false, ...(rows[index].kind ? { proposed: true, accepted_at: null, accepted_by: null } : {}) };
      return done(`edited rule ${key}`, rows[index]);
    }
    const [removed] = rows.splice(index, 1);
    return done(`removed rule ${key}`, removed);
  }

  return { read, write };
}
