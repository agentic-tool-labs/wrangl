// The History tab's search and filters (spec qa-history-search-ui, ah2-56): the filter, the GET /ui/v1/history list read it sends
// (the daemon filters; the mod never filters loaded rows, D1), and the paging and newest-answer-wins state of that read. With no
// filter set the tab keeps its slice list and sends nothing (A1); with one, it reads each running team's dir and merges (A2).

import { threadOf } from "./questions-model.js";
import { within, late, NO_ANSWER } from "./model.js";

export const FILTER0 = Object.freeze({ search: "", team: null, time: "all", state: "all" });
export const TIMES = [["all", "All time"], ["today", "Today"], ["week", "Last 7 days"]];
export const STATES = [["all", "All"], ["answered", "Answered"], ["unanswered", "Not answered"]];
export const NO_MATCH = "No questions match.";
export const SCOPE = "Searching questions and replies. Answered decisions are not included.";
export const DEBOUNCE_MS = 300;

const DAY = 86_400_000;

export const filterActive = (f) => f.search.trim() !== "" || f.team !== null || f.time !== "all" || f.state !== "all";

// `since` in epoch ms: Today from local midnight, Last 7 days from seven days before now; All time sends none.
export function sinceOf(time, now) {
  if (time === "today") {
    const d = new Date(now);
    d.setHours(0, 0, 0, 0);
    return d.getTime();
  }
  return time === "week" ? now - 7 * DAY : null;
}

// The filter's parameters, only those that are set, under the daemon's names.
function paramsOf(f, now, before) {
  const since = sinceOf(f.time, now);
  return [["q", f.search.trim() || null], ["team", f.team], ["since", since === null ? null : String(since)],
    ["state", f.state === "all" ? null : f.state], ["before", before]].filter(([, x]) => x !== null);
}

// The list read's path for one hierarchy dir, each value URL-encoded (`q` is the person's text).
export const historyPath = (dirId, f, now, before = null) =>
  `/ui/v1/history?${[["dir", dirId], ...paramsOf(f, now, before)].map(([k, x]) => `${k}=${encodeURIComponent(x)}`).join("&")}`;

// The dirs to read: each running team's ({name, dirId, hierDir}) dir once, or only the chosen team's. A dir is known by its hierDir
// when it has one, else by its dirId (the bridge's name for it), A12.
export const dirKey = (d) => d.hierDir ?? d.dirId;
export function historyDirs(teams, team) {
  const out = new Map();
  for (const tm of teams) if (dirKey(tm) && (team === null || tm.name === team) && !out.has(dirKey(tm))) out.set(dirKey(tm), tm);
  return [...out.values()];
}

// The same list read through `ah msg history --json` in one hierarchy dir (A6), the filter's flags as argv (the search text one
// element): the fallback when the bridge is not up. ah never takes a value that starts with "--" (A9): the search text's leading
// dashes go as one, and any other such value (a team, the dir) throws, so that dir gives the error note.
// ponytail: "--force" is searched as "-force", a substring of it, so a few extra rows may match; exact needs a --search= form in ah.
const VALUED = new Set(["--dir", "--search", "--team", "--since", "--before"]);
export function historyArgv(hierDir, f, now, before = null) {
  const since = sinceOf(f.time, now);
  const search = f.search.trim().replace(/^-{2,}/, "-");
  const argv = ["msg", "history", "--json", "--dir", hierDir, ...(search ? ["--search", search] : []), ...(f.team !== null ? ["--team", f.team] : []),
    ...(since !== null ? ["--since", String(since)] : []), ...(f.state === "all" ? [] : [`--${f.state}`]), ...(before !== null ? ["--before", before] : [])];
  argv.forEach((a, i) => {
    if (VALUED.has(a) && argv[i + 1].startsWith("--")) throw new Error(`ah cannot take ${a} "${argv[i + 1]}"`);
  });
  return argv;
}

// A list-read thread ({prompt, replies, ask}) -> the row historyRow draws, the same fields the slice's question-log row carries.
export function threadRow(doc, dir, teamName) {
  const head = doc.prompt ?? doc.ask ?? doc.replies?.[0] ?? {};
  const base = { id: head.id ?? "", at: head.at ?? null, by: null, team: head.team ?? teamName, hierDir: dir.hierDir, dirId: dir.dirId, replyAt: null, preview: null };
  if (doc.ask) {
    const qs = doc.ask.questions ?? [];
    const answers = doc.ask.answers ?? {};
    return { ...base, dir: "terminal", header: qs.map((q) => q.header).filter(Boolean).join(" · ") || null, text: qs.map((q) => q.question ?? "").join("\n"),
      answer: qs.map((q) => answers[q.question ?? ""]).filter((a) => typeof a === "string" && a !== "").join("\n"), question: false, replies: 0 };
  }
  const th = threadOf(doc);
  return { ...base, dir: doc.prompt ? "jim" : "unprompted", header: null, text: doc.prompt?.text ?? "", answer: "", question: doc.prompt?.question === true,
    replies: th.later.length, replyAt: th.replyAt, preview: typeof th.reply === "string" && th.reply.trim() !== "" ? th.reply.replace(/\s+/g, " ").trim() : null };
}

const newest = (a, b) => (b.at ?? -Infinity) - (a.at ?? -Infinity);

// One round of reads: every dir in `dirs` (each with the cursor `next` holds for it on Load more), together. Resolves to
// {rows, next} (next: dirKey -> cursor, for the dirs that have more) or {error}. `read(path)` is ahd's bridge (Link read: the text,
// null when the bridge is not up, a throw when ahd did not answer); on null, `exec` runs `ah msg history` in the dir's hierDir,
// bounded by 15 s as readThread's is (`sleep` is herdr's cx.sleep). Both give the same {threads, next} body; the search is the
// daemon's or ah's (D1), never the mod's.
// ponytail: merged pages are each dir's newest page; a dir far behind another can show its older rows only after Load more.
export async function historyRead(read, dirs, f, now, next = null, exec = null, sleep = null) {
  try {
    const pages = await Promise.all(dirs.filter((d) => !next || next[dirKey(d)]).map(async (d) => {
      const before = next ? next[dirKey(d)] : null;
      let text = read && d.dirId ? await read(historyPath(d.dirId, f, now, before)) : null;
      if (text === null) {
        if (!exec || !d.hierDir) throw new Error("ahd is not reachable");
        const r = await within(exec("ah", historyArgv(d.hierDir, f, now, before), { timeout: 15000 }), sleep, 15000);
        if (r.code !== 0) throw new Error((r.stderr ?? "").trim().split("\n")[0] || `ah exited ${r.code}`);
        text = r.stdout;
      }
      const doc = JSON.parse(text);
      return { d, threads: doc.threads ?? [], next: doc.next ?? null };
    }));
    const rows = pages.flatMap((p) => p.threads.map((th) => threadRow(th, p.d, p.d.name))).sort(newest);
    return { rows, next: Object.fromEntries(pages.filter((p) => p.next).map((p) => [dirKey(p.d), p.next])) };
  } catch (e) {
    return { error: `Could not read: ${e?.down ?? (late(e) ? NO_ANSWER : e?.message ?? String(e))}` };
  }
}

// The read's state: `seq` names the newest request; a new filter starts the first page over and drops the old rows; a page for an
// older request is dropped; Load more keeps the filter and appends. `next` maps dirId -> cursor; empty means no more.
export const historyInit = () => ({ seq: 0, filter: FILTER0, rows: [], next: {}, loading: false, error: null, more: false });

export const historyAsk = (h, filter) => ({ ...h, seq: h.seq + 1, filter, rows: [], next: {}, loading: true, error: null, more: false });

export const historyMore = (h) => ({ ...h, seq: h.seq + 1, loading: true, error: null, more: true });

// The background refresh (a poll took new slices): the first page again under the same filter, the rows kept until it answers.
export const historyRefresh = (h) => ({ ...h, seq: h.seq + 1, loading: true, error: null, more: false });

export const hasMore =(h) => Object.keys(h.next).length > 0;

// A failed read keeps the rows and the filter.
export function historyTake(h, seq, got) {
  if (seq !== h.seq) return h;
  if (got.error) return { ...h, loading: false, error: got.error };
  return { ...h, loading: false, error: null, rows: h.more ? [...h.rows, ...got.rows].sort(newest) : got.rows, next: got.next };
}
