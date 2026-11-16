// The E4 harness's fixture selection and gates, kept apart from the browser driver so they can be tested without one.

export const LIMITS = {
  requestLineBytes: 16 * 1024,
  planSliceBytes: 10 * 1024,
  questionsSliceBytes: 10 * 1024,
  firstLoadFactor: 1.1,
  idleBytesFactor: 1.15,
  spreadPct: 10,
};

// Which presets to run: the --fixture value, else AH_MOCK_FIXTURE, else all of them.
export function fixtureNames(arg, env, presets) {
  const valid = Object.keys(presets);
  const want = arg ?? (env ? env : "both");
  if (want === "both") {
    return valid;
  }
  if (!valid.includes(want)) {
    throw new Error(`unknown fixture "${want}"; valid: ${[...valid, "both"].join(", ")}`);
  }
  return [want];
}

export const labelOf = (name, preset) => `fixture=${name}(${preset.AH_MOCK_MEMBERS}/${preset.AH_MOCK_OPEN}/${preset.AH_MOCK_DONE})`;

// The rows of the realistic fixture for one diagnostics setting. `now` is a median entry of a run, `base` the recorded
// realistic baseline entry for the same setting or null. A row with gated=false never fails; CPU is only reported.
// The plan: slice size gates at 10 KB when the run held any plan slice, and has no row when the preset carries no plan streams.
// The questions: slice gates the same way, only when the run held one.
export function realisticRows(label, diag, now, base) {
  const rows = [];
  const absolute = (metric, value, limit) => rows.push({ label, diag, metric, value, limit, gated: true, ok: value <= limit });
  const relative = (metric, value, baseValue, factor) => {
    if (typeof baseValue !== "number") {
      rows.push({ label, diag, metric, value, limit: null, gated: false, ok: true, note: "no baseline" });
    } else {
      const limit = Math.floor(baseValue * factor);
      rows.push({ label, diag, metric, value, limit, gated: true, ok: value <= limit });
    }
  };
  absolute("request line bytes", now.request_line_max_bytes, LIMITS.requestLineBytes);
  if (typeof now.plan_slice_max_bytes === "number") {
    absolute("plan slice bytes", now.plan_slice_max_bytes, LIMITS.planSliceBytes);
  }
  if (typeof now.questions_slice_max_bytes === "number") {
    absolute("questions slice bytes", now.questions_slice_max_bytes, LIMITS.questionsSliceBytes);
  }
  relative("first load bytes", now.first_load_bytes, base?.first_load_bytes, LIMITS.firstLoadFactor);
  if (base?.idle_bytes_gated === false) {
    rows.push({ label, diag, metric: "idle long-poll bytes", value: now.long_poll.idle.body_bytes, limit: null, gated: false, ok: true, note: "informational: the baseline's idle spread is over the limit" });
  } else {
    relative("idle long-poll bytes", now.long_poll.idle.body_bytes, base?.long_poll?.idle?.body_bytes, LIMITS.idleBytesFactor);
  }
  rows.push({ label, diag, metric: "idle page cpu %", value: now.idle.page_cpu_pct, limit: null, gated: false, ok: true, note: "informational" });
  return rows;
}

// Only the rows that need no baseline, for a run judged on its own.
export const absoluteRows = (rows) => rows.filter((r) => r.gated && (r.metric === "request line bytes" || r.metric === "plan slice bytes" || r.metric === "questions slice bytes"));

export const failureLine = (r) => `${r.label}: diag ${r.diag} ${r.metric} ${r.value} exceeds the limit ${r.limit}`;

export const rowLine = (r) => {
  if (!r.ok) {
    return `REGRESSION  ${failureLine(r)}`;
  }
  const how = r.gated ? `limit ${r.limit}` : r.note;
  return `ok          ${r.label}: diag ${r.diag} ${r.metric} ${r.value} (${how})`;
};

// max − min of every number across runs, in the shape of the runs.
export function spreadOf(list) {
  const [first] = list;
  if (typeof first === "number") {
    return Math.round((Math.max(...list) - Math.min(...list)) * 100) / 100;
  }
  if (first && typeof first === "object" && !Array.isArray(first)) {
    return Object.fromEntries(Object.keys(first).map((k) => [k, spreadOf(list.map((x) => x[k]))]));
  }
  return first;
}

// A metric passes when it is within 10% of the baseline, or when it moved by no more than an absolute epsilon in its unit:
// near-zero metrics swing by tens of percent between identical runs.
export function judge(b, n, eps, higherIsBetter = false) {
  const change = higherIsBetter ? b - n : n - b;
  const pct = b === 0 ? (change > 0 ? Infinity : 0) : (change / b) * 100;
  if (pct <= 10) return { ok: true, rule: "relative", pct };
  if (change <= eps) return { ok: true, rule: "floor", pct };
  return { ok: false, rule: "fail", pct };
}

// The views --view can measure: the hash to land on, the localStorage seed, and a page expression true only on that view.
export const VIEWS = {
  map: { hash: "#/graph", seed: {}, check: `!!document.querySelector(".stage-wrap.view-graph") && !document.querySelector(".stage-wrap .gr-body")` },
  radial: { hash: "#/graph", seed: { "ah-graph": JSON.stringify({ mode: "radial" }) }, check: `!!document.querySelector(".stage-wrap.view-graph .gr-body")` },
  board: { hash: "#/plans", seed: { "ah.plans.layout": "board" }, check: `!!document.querySelector(".stage-wrap:is(.view-plans, .view-board) :is(.kb-card, .ice-tile)")` },
  overview: { hash: "#/overview", seed: {}, check: `!!document.querySelector(".stage-wrap.view-overview")` },
};

// The glass budgets (ah-optical-glass §8): one glass median entry against the house median entry of the same run set, same
// fixture, diagnostics, view, theme and DPR. GPU rows need DPR 2; the motion GPU row and the motion gates apply to the
// views with motion (map, radial, board); the renderer-CPU row to the Graph views.
export function lookRows(house, glass) {
  const rows = [];
  const label = `${glass.fixture?.label ?? "fixture"} view=${glass.view} theme=${glass.theme} dpr=${glass.dpr}`;
  const cap = (metric, value, limit) => rows.push({ label, diag: glass.diagnostics, metric, value, limit: Math.round(limit * 100) / 100, gated: true, ok: value <= limit });
  const graph = glass.view === "map" || glass.view === "radial";
  const motionView = graph || glass.view === "board";
  if (glass.dpr === 2) {
    cap("idle gpu cpu % (glass vs house + 1.0)", glass.idle.gpu_cpu_pct, house.idle.gpu_cpu_pct + 1.0);
    if (motionView) cap("motion gpu cpu % (glass vs house + 2.0)", glass.motion.gpu_cpu_pct, house.motion.gpu_cpu_pct + 2.0);
  }
  if (graph) cap("idle page cpu % (glass vs house x 1.10 + 0.2)", glass.idle.page_cpu_pct, house.idle.page_cpu_pct * 1.1 + 0.2);
  const rel = (metric, b, n, eps, higher) => {
    const v = judge(b, n, eps, higher);
    rows.push({ label, diag: glass.diagnostics, metric: `${metric} (house ${b})`, value: n, limit: null, gated: true, ok: v.ok, note: `${v.rule}, ${Math.round(v.pct * 10) / 10}% worse` });
  };
  if (motionView) {
    rel("motion fps", house.motion.fps, glass.motion.fps, 1, true);
    rel("motion frame p95 ms", house.motion.frame_ms.p95, glass.motion.frame_ms.p95, 1);
    rel("motion frames over 20 ms", house.motion.over_20ms, glass.motion.over_20ms, 1);
  }
  rel("scroll frame p95 ms", house.scroll.frame_ms.p95, glass.scroll.frame_ms.p95, 1);
  rel("scroll frame p99 ms", house.scroll.frame_ms.p99, glass.scroll.frame_ms.p99, 1);
  rows.push({ label, diag: glass.diagnostics, metric: "dom nodes (house / glass)", value: `${house.idle.dom_nodes} / ${glass.idle.dom_nodes}`, limit: null, gated: false, ok: true, note: "reported" });
  return rows;
}

// The realistic baseline entry for a run file: the preset and counts, the SHA and date, the medians and the spreads per
// diagnostics setting, and the idle-bytes spread as a percentage of its median (the E2 check).
export function baselineEntry(run, sha, date) {
  const medians = run.median.filter((m) => m.fixture?.preset === "realistic");
  if (medians.length === 0) {
    throw new Error("the run has no realistic entries");
  }
  const spreads = medians.map((m) => spreadOf(run.runs.filter((r) => r.diagnostics === m.diagnostics && r.fixture?.preset === "realistic")));
  const idleSpreadPct = medians.map((m, i) => ({ diagnostics: m.diagnostics, pct: m.long_poll.idle.body_bytes === 0 ? 0 : Math.round((spreads[i].long_poll.idle.body_bytes / m.long_poll.idle.body_bytes) * 1000) / 10 }));
  return {
    fixture: medians[0].fixture,
    sha,
    date,
    runs_per_mode: run.runs_per_mode,
    median: medians,
    spread: spreads,
    idle_bytes_spread_pct: idleSpreadPct,
    idle_bytes_gated: idleSpreadPct.every((s) => s.pct <= LIMITS.spreadPct),
  };
}
