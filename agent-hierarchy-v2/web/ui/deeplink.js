// The page's address. `#/` is Home (all teams); `#/<tab>[/<member id>][?team=<team id>&lane=…&item=…&owner=…&sitrep=1]` is a
// team tab, where the team may be left out when a member names it; `#/member/<member id>` is a member's page;
// `#/inbox/attention` and `#/inbox/qa`, with an optional `?team=`, are the Inbox; `#/looks` is the Looks page; `#/board?team=all` is the Board of all teams.
// A param is written only when it differs from its default, and unknown params are ignored. The old view ids of the
// eleven-tab UI are rewritten once, with `history.replaceState`, so Back skips them.

export const TEAM_VIEWS = ["overview", "board", "graph", "timeline", "conversation"];
const TEAM_VIEW_SET = new Set(TEAM_VIEWS);
export const INBOX_TABS = ["attention", "qa"];
// The tables the Board folded in, each at `#/table/<view>` until U4 deletes them.
const TABLE_VIEWS = ["backlog", "checklist", "queue"];

// The old tab ids and the view each now lives in (ah-ui-ia §5).
const OLD_VIEWS = { plans: "board", checklist: "board", backlog: "board", queue: "board", lanes: "board", attention: "inbox/attention", questions: "inbox/qa" };
// The Board's scope (lane, then epic and deliverable: the breadcrumb), its focused item and its owner filter (ah-ui-ia §2.2, §2.6).
const BOARD_PARAMS = ["lane", "epic", "deliverable", "item", "owner"];

// The team a member id belongs to: member:<dir>/<team>/<name> -> team:<dir>/<team>.
export const teamOfMember = (id) => (/^member:[^/]+\/.+\/[^/]+$/.test(id) ? id.replace(/^member:/, "team:").replace(/\/[^/]+$/, "") : null);

// The address of a member's page; every link to a member goes through here.
export const memberHref = (memberId) => hashOf({ view: "member", selected: memberId });

const query = (pairs) => {
  const q = pairs.filter(([, v]) => v !== null && v !== undefined && v !== false && v !== "").map(([k, v]) => `${k}=${v === true ? "1" : encodeURIComponent(v)}`);
  return q.length > 0 ? `?${q.join("&")}` : "";
};

export function hashOf(ui) {
  if (ui.view === undefined) {
    return "";
  }
  if (ui.view === "teams") {
    return "#/";
  }
  if (ui.view === "allplans") {
    return `#/board${query([["team", "all"], ...BOARD_PARAMS.filter((k) => k !== "lane").map((k) => [k, ui[k]])])}`;
  }
  if (ui.view === "looks") {
    return "#/looks";
  }
  if (TABLE_VIEWS.includes(ui.view)) {
    return `#/table/${ui.view}${query([["team", ui.team]])}`;
  }
  if (ui.view === "inbox") {
    return `#/inbox/${INBOX_TABS.includes(ui.inboxTab) ? ui.inboxTab : "attention"}${query([["team", ui.team]])}`;
  }
  if (ui.view === "member") {
    return ui.selected && teamOfMember(ui.selected) ? `#/member/${encodeURIComponent(ui.selected)}` : "#/";
  }
  const member = ui.selected && teamOfMember(ui.selected) ? `/${encodeURIComponent(ui.selected)}` : "";
  const board = ui.view === "board" ? BOARD_PARAMS.map((k) => [k, ui[k]]) : [];
  return `#/${ui.view}${member}${query([["team", member ? null : ui.team], ...board, ["sitrep", ui.sitrep === true]])}`;
}

// The store's ui for an address, every routed field set so a new address clears the old one's; null when it is not one of ours.
// `notice` carries the note an address that cannot be honoured leaves on Home (a malformed member id).
export function uiOf(hash) {
  if (hash === "#/") {
    return { view: "teams", selected: null };
  }
  const m = /^#\/([a-z]+)(?:\/([^?]*))?(?:\?(.*))?$/.exec(hash);
  if (!m) {
    return null;
  }
  let selected;
  try {
    selected = m[2] ? decodeURIComponent(m[2]) : null;
  } catch {
    return m[1] === "member" ? { view: "teams", selected: null, notice: "Member not found" } : null;
  }
  const params = new URLSearchParams(m[3] ?? "");
  const param = (k) => params.get(k) || null;
  const none = { ...Object.fromEntries(BOARD_PARAMS.map((k) => [k, null])), sitrep: false };
  if (m[1] === "member") {
    const team = selected ? teamOfMember(selected) : null;
    return team ? { view: "member", selected, team, ...none } : { view: "teams", selected: null, notice: "Member not found" };
  }
  // The Looks page (ah-ui-looks §6) belongs to no team; the team stays in the store for the way back.
  if (m[1] === "looks") {
    return { view: "looks", selected: null, ...none };
  }
  // The standalone tables, linked from nothing: what the type regression check reads. Their old hashes still go to the Board.
  if (m[1] === "table") {
    return TABLE_VIEWS.includes(selected) ? { view: selected, team: param("team"), selected: null, ...none } : null;
  }
  if (m[1] === "inbox") {
    return { view: "inbox", inboxTab: INBOX_TABS.includes(selected) ? selected : "attention", team: param("team"), selected: null, ...none };
  }
  if (!TEAM_VIEW_SET.has(m[1])) {
    return null;
  }
  const team = param("team") ?? (selected ? teamOfMember(selected) : null);
  if (m[1] === "board" && team === "all") {
    const all = Object.fromEntries(BOARD_PARAMS.filter((k) => k !== "lane").map((k) => [k, param(k)]));
    return { view: "allplans", selected: null, ...none, ...all, deliverable: all.epic ? all.deliverable : null };
  }
  // A deliverable is a level under an epic: without one it scopes nothing, so it is dropped.
  const scoped = (o) => (o.epic ? o : { ...o, deliverable: null });
  const board = m[1] === "board" ? scoped(Object.fromEntries(BOARD_PARAMS.map((k) => [k, param(k)]))) : {};
  // A Sitrep needs a team (ah-ui-ia §6); without one the param is dropped.
  return { view: m[1], selected, team, ...none, ...board, sitrep: team !== null && params.get("sitrep") === "1" };
}

// The address an old or non-canonical one is rewritten to, or null when it stands. Old view ids keep their member segment
// and `team=`; the Inbox has no member segment, so there the member's team is kept instead.
export function redirectOf(hash) {
  if (hash === "#/teams") {
    return "#/";
  }
  if (hash === "#/teams/plans") {
    return "#/board?team=all";
  }
  const old = /^#\/([a-z]+)((?:\/[^?]*)?)((?:\?.*)?)$/.exec(hash);
  if (old && OLD_VIEWS[old[1]]) {
    const to = OLD_VIEWS[old[1]];
    if (to === "board") {
      return redirectOf(`#/board${old[2]}${old[3]}`) ?? `#/board${old[2]}${old[3]}`;
    }
    const ui = uiOf(`#/overview${old[2]}${old[3]}`);
    return `#/${to}${query([["team", ui?.team ?? null]])}`;
  }
  // Any other address of ours is normalised to its canonical form (param order, encoding, unknown and dropped params), so the
  // subscriber below never has to rewrite it with a push.
  const ui = uiOf(hash);
  if (!ui) {
    return null;
  }
  const want = hashOf(ui);
  return want !== hash ? want : null;
}

// Params that open or close something over the page rather than move to a new one: the Sitrep dialog and, on a team tab, the
// panel's member selection. A store change that moves only these rewrites the address in place (replaceState), so Back never
// re-opens what was closed and never lands on an address the page drops again. A change of view, team or any other field is a
// navigation and pushes. (expand and pair are overlays too, but are not in the address, so they never reach the subscriber.)
export const TRANSIENT_PARAMS = ["sitrep", "selected"];

// Whether two routed uis differ only in transient params. On the member page the member is the route, not a selection.
export function transientOnly(from, to) {
  if (!from || !to || from.view !== to.view || to.view === "member") {
    return false;
  }
  const keys = new Set([...Object.keys(from), ...Object.keys(to)]);
  return [...keys].every((k) => TRANSIENT_PARAMS.includes(k) || (from[k] ?? null) === (to[k] ?? null));
}

// Reads the hash once, then keeps the hash and the store's routed fields in step in both directions. An old address is first
// rewritten in place (replaceState, so Back skips it).
export function bindHash(store, win) {
  const apply = (back) => {
    const notice = uiOf(win.location.hash)?.notice ?? null;
    const to = redirectOf(win.location.hash);
    if (to !== null) {
      win.history.replaceState(win.history.state, "", to);
    }
    // Back to the empty address is Home: replaced in place, so the subscriber does not push "#/" on top of it (a Back trap).
    if (back && ["", "#"].includes(win.location.hash)) {
      win.history.replaceState(win.history.state, "", "#/");
    }
    const patch = uiOf(win.location.hash);
    if (patch) {
      store.setUi(notice ? { ...patch, notice } : patch);
    }
  };
  apply(false);
  win.addEventListener("hashchange", () => apply(true));
  return store.subscribe(() => {
    const want = hashOf(store.state.ui);
    if (win.location.hash !== want && (want !== "" || uiOf(win.location.hash))) {
      if (transientOnly(uiOf(win.location.hash), uiOf(want))) {
        win.history.replaceState(win.history.state, "", want);
      } else {
        win.location.hash = want;
      }
    }
  });
}
