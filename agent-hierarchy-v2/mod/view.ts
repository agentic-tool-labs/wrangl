// The status document read the way every consumer reads it (docs/status-file.md, "Reading it"), and what
// the band, the Pane and the toasts show from it. No engine calls, so the mod's tests drive it directly.
import type { PaneRow, TeamModel, TeamSummary, Tone, View } from './types/index.d.ts'

/** The largest status.json read: a bigger file counts as no document. */
export const SIZE_CAP = 262144

export type Doc = {
  /** `enabled` when the file holds a boolean there, else null. Only the cause shown for an empty Pane reads it. */
  enabled: boolean | null
  expiresMs: number
  memberSessions: readonly string[]
  timeline: Timeline
  teams: readonly unknown[]
  /** One entry per orchestrator process that owns teams in the pool; null when the document has no `owners` (written by an older producer). */
  owners: readonly Owner[] | null
}
type Timeline = readonly { atMs: number; entry: Record<string, unknown> }[]
type Owner = { sessions: readonly string[]; teams: readonly (string | null)[]; timeline: Timeline }

const INSTANT = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})/
const CONTROL = /[\x00-\x1f\x7f-\x9f]/g

/** Milliseconds for an ISO-8601 instant (a date, a time and a zone), else NaN. */
function instantMs(v: unknown): number {
  if (typeof v !== 'string') return NaN
  const m = INSTANT.exec(v)
  return m !== null && m[0].length === v.length ? Date.parse(v) : NaN
}

/** UTF-8 length of a string, which is what the size cap counts. */
export function utf8Bytes(s: string): number {
  let n = 0
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i)
    if (c < 0x80) n += 1
    else if (c < 0x800) n += 2
    else if (c >= 0xd800 && c <= 0xdbff && i + 1 < s.length) { n += 4; i++ }
    else n += 3
  }
  return n
}

/**
 * The document in a status.json's text, or null when it counts as absent: no text, over SIZE_CAP bytes,
 * not JSON, `schema` not 1, or a field the status entry reads that is missing or of the wrong type
 * (`expires_at`, `member_sessions`, `timeline` with every `at`). `teams` is read field by field, later.
 */
export function parseDoc(text: string | null | undefined): Doc | null {
  if (typeof text !== 'string' || utf8Bytes(text) > SIZE_CAP) return null
  let o: unknown
  try { o = JSON.parse(text) } catch { return null }
  if (typeof o !== 'object' || o === null || Array.isArray(o)) return null
  const d = o as Record<string, unknown>
  if (d.schema !== 1) return null
  const expiresMs = instantMs(d.expires_at)
  if (!Number.isFinite(expiresMs)) return null
  const members = d.member_sessions
  if (!Array.isArray(members) || !members.every((s) => typeof s === 'string')) return null
  const timeline = parseTimeline(d.timeline)
  if (timeline === null) return null
  let owners: Owner[] | null = null
  if ('owners' in d) {
    owners = []
    if (Array.isArray(d.owners)) {
      for (const o of d.owners) {
        if (typeof o !== 'object' || o === null || Array.isArray(o)) continue
        const { sessions, teams, timeline: tl } = o as Record<string, unknown>
        const own = parseTimeline(tl)
        if (!Array.isArray(sessions) || !sessions.every((x) => typeof x === 'string')) continue
        if (!Array.isArray(teams) || !teams.every((x) => x === null || typeof x === 'string') || own === null) continue
        owners.push({ sessions, teams, timeline: own })
      }
    }
  }
  return { enabled: typeof d.enabled === 'boolean' ? d.enabled : null, expiresMs, memberSessions: members as string[], timeline, teams: Array.isArray(d.teams) ? d.teams : [], owners }
}

/** A timeline array checked the way every timeline is: non-empty, every entry an object with an `at` instant. Null when it fails. */
function parseTimeline(tl: unknown): Timeline | null {
  if (!Array.isArray(tl) || tl.length === 0) return null
  const timeline = []
  for (const entry of tl) {
    if (typeof entry !== 'object' || entry === null || Array.isArray(entry)) return null
    const atMs = instantMs((entry as Record<string, unknown>).at)
    if (!Number.isFinite(atMs)) return null
    timeline.push({ atMs, entry: entry as Record<string, unknown> })
  }
  return timeline
}

/**
 * The part of `doc` that `sessionId` may see: for a document with `owners`, the first usable owner entry that lists the
 * session, with that entry's timeline and only the teams it names, in document order; null when no entry lists it.
 * A document without `owners` is returned whole.
 */
export function scope(doc: Doc | null, sessionId: string): Doc | null {
  if (doc === null || doc.owners === null) return doc
  const own = doc.owners.find((o) => o.sessions.includes(sessionId))
  if (own === undefined) return null
  return { ...doc, timeline: own.timeline, teams: records(doc.teams).filter((t) => own.teams.includes(t.team as string | null)) }
}

/**
 * The timeline entry current at `nowMs` (the last with `at` ≤ now, else the first), or null when nothing
 * shows: no document, expired, the entry malformed or not visible, or `sessionId` a member session.
 */
function current(doc: Doc | null, nowMs: number, sessionId: string): Record<string, unknown> | null {
  if (doc === null || nowMs >= doc.expiresMs || doc.memberSessions.includes(sessionId)) return null
  const mine = scope(doc, sessionId)
  if (mine === null) return null
  let picked = mine.timeline[0]
  for (const t of mine.timeline) if (t.atMs <= nowMs) picked = t
  const { visible, tone, text, short } = picked.entry
  if (typeof visible !== 'boolean' || typeof tone !== 'string' || typeof text !== 'string' || typeof short !== 'string') return null
  if (!visible) return null
  return picked.entry
}

/**
 * Why there is no view for a document: `unreadable` when it did not parse, else `expired`, `member session`,
 * `hierarchy off` (it says `enabled` is false) or `not visible`. Named in the empty Pane's one line.
 */
export function nullCause(doc: Doc | null, nowMs: number, sessionId: string): string {
  if (doc === null) return 'unreadable'
  if (nowMs >= doc.expiresMs) return doc.memberSessions.includes(sessionId) || scope(doc, sessionId) !== null ? 'expired' : NO_OWNED_TEAM
  if (doc.memberSessions.includes(sessionId)) return 'member session'
  if (doc.enabled === false) return 'hierarchy off'
  if (scope(doc, sessionId) === null) return NO_OWNED_TEAM
  return 'not visible'
}

/** The `nullCause` for a document that lists no team owned by the viewing session. */
export const NO_OWNED_TEAM = 'no team owned by this session'

/** The names of the teams `doc` says `sessionId` owns, whether or not its view shows them: none for a document without `owners`. */
export function ownedTeamNames(doc: Doc | null, sessionId: string): string[] {
  if (doc === null || doc.owners === null) return []
  return records(scope(doc, sessionId)?.teams ?? []).map((t) => str(t.team) || 'default')
}

/** `doc` without the teams `names`, in its team list and in every owner entry, for a team the daemon calls closed: every reader of the view then agrees. */
export function withoutTeams(doc: Doc | null, names: string[]): Doc | null {
  if (doc === null || names.length === 0) return doc
  const gone = (team: unknown) => names.includes(str(team) || 'default')
  return { ...doc, teams: records(doc.teams).filter((t) => !gone(t.team)), owners: doc.owners === null ? null : doc.owners.map((o) => ({ ...o, teams: o.teams.filter((t) => !gone(t)) })) }
}

/** Whether a team's served `team-summary` body says it is closed: the daemon's verdict, which a spawn into a closed team lifts. A body not held yet says nothing. */
export const closedTeam = (summary: unknown): boolean => typeof record(summary)?.closed_at === 'string'

/**
 * The status files of the teams a session's registration (its JSON text) names, in `team_file` then `team_files` order: a team
 * file `<dir>/teams/<team>.json` has its status beside `teams/`. Anything that is not an absolute path of that shape names nothing.
 */
export function registeredStatusFiles(text: unknown): string[] {
  let r: Record<string, unknown> | null = null
  try { r = record(JSON.parse(String(text))) } catch { return [] }
  const out = new Set<string>()
  for (const f of [r?.team_file, ...Object.values(record(r?.team_files) ?? {})]) {
    if (typeof f !== 'string' || !/^(\/|[A-Za-z]:[\\/])/.test(f)) continue
    const parts = f.split(/[\\/]/)
    if (parts.length >= 3 && parts[parts.length - 2] === 'teams') out.add(f.slice(0, f.length - parts[parts.length - 1].length - 'teams/'.length) + 'status.json')
  }
  return [...out]
}

/** What the Pane shows: the Sitrep, or the hierarchy the View button's Pane choice opens. */
export function paneTab(mode: unknown): 'sitrep' | 'pane' {
  return mode === 'pane' ? mode : 'sitrep'
}

// POSIX path helpers for mainCheckoutRoot; a Windows path never starts with `/`, so it resolves no main checkout.
const normalizePath = (p: string): string => {
  const out: string[] = []
  for (const s of p.split('/')) {
    if (s === '' || s === '.') continue
    if (s === '..') out.pop()
    else out.push(s)
  }
  return '/' + out.join('/')
}
const resolveFrom = (base: string, p: string): string => normalizePath(p.startsWith('/') ? p : base + '/' + p)
const baseName = (p: string): string => p.slice(p.lastIndexOf('/') + 1)
const dirName = (p: string): string => { const i = p.lastIndexOf('/'); return i <= 0 ? '/' : p.slice(0, i) }

/**
 * The main checkout's root when `root` (a directory holding `.git`) is a linked worktree, else null. A mirror of
 * the plugin's own `mainCheckoutRoot`, which the mod cannot import; the cases in tests/main-vectors.ts, recorded from the
 * plugin's answers, pin the two to the same answers. `io.kind` answers `file`, `directory` or null.
 */
export async function mainCheckoutRoot(root: string, io: { kind: (path: string) => Promise<string | null>; read: (path: string) => Promise<string> }): Promise<string | null> {
  if (!root.startsWith('/')) return null
  let raw: string
  try {
    if ((await io.kind(root + '/.git')) !== 'file') return null
    raw = await io.read(root + '/.git')
  } catch {
    return null
  }
  const m = /^gitdir:\s*(.+)/m.exec(raw)
  if (m === null) return null
  const gitdir = resolveFrom(root, m[1].trim())
  if (baseName(dirName(gitdir)) !== 'worktrees') return null
  let commonDir: string
  try {
    commonDir = resolveFrom(gitdir, (await io.read(gitdir + '/commondir')).trim())
  } catch {
    commonDir = dirName(dirName(gitdir))
  }
  if (baseName(commonDir) !== '.git') return null
  const main = dirName(commonDir)
  return main !== '' && main !== root ? main : null
}

/**
 * The status entry's text at `nowMs` for the session `sessionId`, or undefined when nothing should show
 * or the `status_entry` option is off. Control characters are stripped.
 */
export function statusText(doc: Doc | null, nowMs: number, sessionId: string, statusEntry: boolean): string | undefined {
  const entry = statusEntry ? current(doc, nowMs, sessionId) : null
  return entry === null ? undefined : (entry.text as string).replace(CONTROL, '')
}

// The file is untrusted past the fields parseDoc checks: every other field is read here, defaulting
// when missing or of the wrong type, and every string has its control characters stripped.
const str = (v: unknown): string => (typeof v === 'string' ? v.replace(CONTROL, '') : '')
const record = (v: unknown): Record<string, unknown> | null => (typeof v === 'object' && v !== null && !Array.isArray(v) ? (v as Record<string, unknown>) : null)
const records = (v: unknown): Record<string, unknown>[] => (Array.isArray(v) ? v.map(record).filter((x): x is Record<string, unknown> => x !== null) : [])
const count = (v: unknown): number => (typeof v === 'number' && Number.isFinite(v) && v > 0 ? Math.floor(v) : 0)
const seconds = (ms: number): number => (Number.isFinite(ms) && ms > 0 ? Math.floor(ms / 1000) : 0)

/** `{m:ss}`: minutes unbounded, seconds two digits; nothing before 0:00. */
const clock = (ms: number): string => { const s = seconds(ms); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}` }
/** A duration: `{s}s` under a minute, else `{m}m {s}s`. */
const duration = (ms: number): string => { const s = seconds(ms); return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s` }
/** An age: `{s}s`, `{m}m` or `{h}h`. */
const age = (ms: number): string => { const s = seconds(ms); return s < 60 ? `${s}s` : s < 3600 ? `${Math.floor(s / 60)}m` : `${Math.floor(s / 3600)}h` }

type Dispatch = {
  id: string; slug: string; label: string; member: string; toName: string
  etaMs: number; sentMs: number; reportedMs: number; checkins: number; state: string; reason: string
  /** Optional, '' from a producer that predates them; read but not rendered yet. */
  title: string; titleSource: string; item: string; reportLine: string
}
type Member = {
  name: string; label: string; kind: string; route: string; live: unknown
  activity: string; activityMs: number; activityAt: string; blockedBy: string; note: string
  lastTool: string; lastToolMs: number; stream: string; sessionId: string; focusable: boolean; contextPct: number
  /** Optional, '' from a producer that predates them; read but not rendered yet. */
  workTree: string; branch: string; lastReportLine: string; lastReportAt: string
}
type Team = { name: string; isDefault: boolean; pipeline: Record<string, unknown> | null; members: Member[]; dispatches: Dispatch[] }

function readTeams(doc: Doc, nowMs: number): Team[] {
  return records(doc.teams).map((t) => ({
    name: str(t.team) || 'default',
    isDefault: t.team === null,
    pipeline: record(t.pipeline),
    members: records(t.members).map((m) => ({
      name: str(m.name), label: str(m.label), kind: str(m.kind), route: str(m.route), live: m.live,
      activity: str(m.activity), activityMs: instantMs(m.activity_at), activityAt: str(m.activity_at),
      blockedBy: str(m.blocked_by), note: str(m.blocked_note),
      lastTool: str(m.last_tool), lastToolMs: instantMs(m.last_tool_at), stream: str(m.stream), sessionId: str(m.session_id), focusable: m.focusable === true,
      contextPct: typeof m.context_pct === 'number' && Number.isFinite(m.context_pct) ? Math.round(m.context_pct) : NaN,
      workTree: str(m.work_tree), branch: str(m.branch), lastReportLine: str(m.last_report_line), lastReportAt: str(m.last_report_at),
    })),
    dispatches: records(t.dispatches).map((d) => {
      let state = '', reason = ''
      for (const s of records(d.states)) {
        const atMs = instantMs(s.at)
        if (state === '' || atMs <= nowMs) { state = str(s.state); reason = str(s.reason) }
      }
      return {
        id: str(d.id), slug: str(d.slug), label: str(d.label), member: str(d.member), toName: str(d.to_name),
        etaMs: typeof d.eta_ms === 'number' && Number.isFinite(d.eta_ms) && d.eta_ms > 0 ? d.eta_ms : NaN,
        sentMs: instantMs(d.sent_at), reportedMs: instantMs(d.reported_at), checkins: count(d.checkins), state, reason,
        title: str(d.title), titleSource: str(d.title_source), item: str(d.item), reportLine: str(d.report_line),
      }
    }),
  }))
}

const etaText = (d: Dispatch): string | null => (Number.isNaN(d.etaMs) ? null : `${Math.max(1, Math.round(d.etaMs / 60000))}m`)
const oldestFirst = (a: number, b: number): number => (Number.isNaN(a) ? Infinity : a) - (Number.isNaN(b) ? Infinity : b)

/** The check-in phrase of a stalled dispatch with no report. */
function checkinPhrase(d: Dispatch, nowMs: number): string {
  if (d.checkins === 1) return '1 check-in unanswered'
  if (d.checkins > 1) return `${d.checkins} check-ins unanswered`
  return `no report after ${Math.floor(seconds(nowMs - d.sentMs) / 60)}m`
}
const goneName = (d: Dispatch): string => d.member || d.toName || d.label
const stalledWhy = (d: Dispatch, nowMs: number): string => (d.reason === 'member-gone' ? `${goneName(d)} is gone` : checkinPhrase(d, nowMs))

function overdueText(d: Dispatch, nowMs: number): string {
  const eta = etaText(d)
  const head = eta === null ? `${d.label} is past its eta` : `${d.label} is ${duration(nowMs - d.sentMs - d.etaMs)} past its ${eta} eta`
  return d.checkins > 0 ? `${head} · check-in ${d.checkins} sent` : head
}

/**
 * The one table of everything the sectioned Pane adds. `icon` is the status language shared with the native TUI and the
 * web (tests/fixtures/status-language.json holds the same table, and both sides are tested against it): per status, its
 * glyph (one code point, so its shape tells the status with no color), the theme key that colors it (null draws it dim),
 * the plain word shown for it, the row tone, and its sort rank (0 needs attention first). `tone` is the theme key a row's
 * text takes, the same key as its glyph. Then the box colors by section and the palette streams take in turn. The drawing
 * applies these and names no glyph, word or color of its own. Box colors are never the status keys (`success`, `warning`,
 * `error`), which only statuses use.
 */
export const STYLE = {
  icon: {
    stuck: { glyph: '▲', color: 'error', word: 'stuck', tone: 'bad', rank: 0 },
    late: { glyph: '▲', color: 'error', word: 'late', tone: 'bad', rank: 1 },
    needsYou: { glyph: '■', color: 'warning', word: 'needs you', tone: 'warn', rank: 2 },
    dueSoon: { glyph: '◔', color: 'warning', word: 'due soon', tone: 'warn', rank: 3 },
    working: { glyph: '●', color: 'success', word: 'working', tone: 'work', rank: 4 },
    waitingOrch: { glyph: '◐', color: null, word: 'waiting for the orchestrator', tone: 'idle', rank: 5 },
    idle: { glyph: '○', color: null, word: 'idle', tone: 'idle', rank: 6 },
    done: { glyph: '✓', color: null, word: 'done', tone: 'idle', rank: 7 },
    ended: { glyph: '×', color: null, word: 'ended', tone: 'idle', rank: 8 },
    unknown: { glyph: '?', color: null, word: 'unknown', tone: 'idle', rank: 9 },
  },
  from: {
    working: 'working', idle: 'idle', blocked: 'needsYou', needs_you: 'needsYou', ended: 'ended', waiting_user: 'needsYou', waiting_orch: 'waitingOrch', stalled: 'stuck',
    overdue: 'late', 'due-soon': 'dueSoon', reported: 'done', gone: 'ended', orphaned: 'ended', stale: 'ended', expired: 'ended',
  },
  tone: { bad: 'error', warn: 'warning' },
  box: { hierarchy: 'claude', team: 'permission', dispatches: null },
  streams: ['suggestion', 'remember', 'ide', 'planMode'],
} as const
type IconKey = keyof typeof STYLE.icon
/** The status a raw member activity or dispatch state reads as; anything the table does not name is `unknown`. */
export const statusOf = (raw: string): IconKey => (STYLE.from as Record<string, IconKey>)[raw] ?? 'unknown'
const iconOf = (key: IconKey) => ({ icon: STYLE.icon[key].glyph as string, iconColor: STYLE.icon[key].color as string | null })
const wordOf = (raw: string): string => (statusOf(raw) === 'unknown' ? raw : STYLE.icon[statusOf(raw)].word)

/** A dispatch counts as open while it is working, overdue, stalled or blocked; reported and expired ones are closed. */
const OPEN_STATES = ['working', 'overdue', 'stalled', 'blocked']

/** What dismissing a team would interrupt, from the same parsed activity and dispatch states the Pane rows draw. */
function summaryOf(t: Team): TeamSummary {
  const live = t.members.filter((m) => m.live !== false)
  return {
    members: t.members.length,
    busy: live.filter((m) => m.activity === 'working').map((m) => m.name),
    blocked: live.filter((m) => m.activity === 'blocked').map((m) => m.name),
    open: t.dispatches.filter((d) => OPEN_STATES.includes(d.state)).map((d) => ({ member: goneName(d), state: wordOf(d.state) })),
  }
}

const memberIconKey = (m: Member): IconKey => (m.live === false ? 'ended' : statusOf(m.activity))
const memberTone = (m: Member): Tone => STYLE.icon[memberIconKey(m)].tone
const dispatchTone = (state: string): Tone => STYLE.icon[statusOf(state)].tone

/**
 * What the band, the Pane and the toasts show at `nowMs` for `sessionId`, or null whenever the status
 * entry would show nothing for the document itself (absent, expired, invisible, a member session).
 */
export function viewModel(doc: Doc | null, nowMs: number, sessionId: string): View | null {
  const entry = current(doc, nowMs, sessionId)
  const mine = scope(doc, sessionId)
  if (entry === null || mine === null) return null
  const teams = readTeams(mine, nowMs)
  const dispatches = teams.flatMap((t) => t.dispatches)
  const members = teams.flatMap((t) => t.members)

  // The band is the All teams summary line, in the native TUI's words, toned by the worst status in it; the worst event
  // itself is a toast.
  let band: View['band'] = null
  if (teams.length > 0) {
    const live = members.filter((m) => m.live !== false)
    const open = dispatches.filter((d) => OPEN_STATES.includes(d.state))
    const need = live.filter((m) => statusOf(m.activity) === 'needsYou').length + open.filter((d) => dispatchTone(d.state) === 'bad').length
    const keys = [...members.map(memberIconKey), ...open.map((d) => statusOf(d.state))]
    const worst = keys.sort((a, b) => STYLE.icon[a].rank - STYLE.icon[b].rank)[0]
    const needs = need === 0 ? 'nothing needs you' : need === 1 ? '1 needs you' : `${need} need you`
    band = {
      tone: worst === undefined ? 'idle' : STYLE.icon[worst].tone,
      head: `${teams.length === 1 ? '1 team' : `${teams.length} teams`} · ${live.length} of ${members.length} agents up · ${needs} · ${open.length} in flight`,
      tail: [],
    }
  } else {
    const head = str(entry.text)
    const tone = (['bad', 'warn', 'work', 'idle'] as const).find((t) => t === entry.tone) ?? 'idle'
    if (head !== '') band = { tone, head, tail: [] }
  }

  const multi = teams.length > 1
  const heading = (name: string, t: Team): PaneRow => ({ row: 'text', tone: 'idle', text: multi ? `${name} ${t.name}` : name })
  const pipelineRows = (t: Team, none = 'No pipeline run.'): PaneRow[] => {
    const rows: PaneRow[] = []
    const open = t.pipeline === null ? [] : records(t.pipeline.items).filter((item) => item.open === true)
    const cap = t.pipeline === null ? 0 : count(t.pipeline.round_cap)
    if (!open.length) rows.push({ row: 'text', tone: 'idle', text: none })
    for (const item of open) rows.push({ row: 'text', tone: 'work', text: `round ${count(item.rounds)}/${cap} · ${str(item.to)}` })
    const waiting = t.pipeline === null ? 0 : count(t.pipeline.waiting_on_user)
    if (waiting > 0) rows.push({ row: 'text', tone: 'warn', text: `${waiting} ${waiting === 1 ? 'decision' : 'decisions'} waiting for you` })
    return rows
  }
  const memberRow = (m: Member): PaneRow => {
    const progress = m.route !== 'pane' && m.activity === 'working' && m.lastTool !== '' && !Number.isNaN(m.activityMs) && !Number.isNaN(m.lastToolMs) && m.lastToolMs >= m.activityMs
    const base = m.live === false ? STYLE.icon.ended.word : `${wordOf(m.activity)}${m.route === 'pane' && !Number.isNaN(m.activityMs) ? ` ${age(nowMs - m.activityMs)}` : ''}`
    const worked = m.live !== false && progress ? `working ${age(nowMs - m.activityMs)} · last ${m.lastTool} ${age(nowMs - m.lastToolMs)}` : base
    const state = m.live !== false && !Number.isNaN(m.contextPct) ? `${worked} · ${m.contextPct}%` : worked
    // A click focuses the member's terminal pane: only a member the status file marks focusable (exactly true), still live, and not this session.
    const focus = m.focusable && m.live !== false && (m.sessionId === '' || m.sessionId !== sessionId)
    return { row: 'member', tone: memberTone(m), name: m.name, kind: m.kind, route: m.route, state, base, focus, ...iconOf(memberIconKey(m)) }
  }
  const shownOf = (t: Team): Dispatch[] => t.dispatches.filter((d) => d.state !== '' && d.state !== 'expired').sort((a, b) => oldestFirst(b.sentMs, a.sentMs))
  const dispatchRow = (d: Dispatch): PaneRow => {
    const eta = etaText(d)
    const pct = eta === null ? null : Math.min(100, Math.max(0, Math.floor(((nowMs - d.sentMs) / d.etaMs) * 100))) || 0
    const tone = dispatchTone(d.state)
    return { row: 'dispatch', tone, slug: d.slug, label: d.label, eta, pct, elapsed: clock(nowMs - d.sentMs), state: wordOf(d.state), ...iconOf(statusOf(d.state)) }
  }
  const pane: PaneRow[] = []
  for (const t of teams) {
    pane.push(heading('Hierarchy', t))
    pane.push(...pipelineRows(t))
  }
  for (const t of teams) {
    pane.push(heading('Team', t))
    for (const m of t.members) pane.push(memberRow(m))
  }
  for (const t of teams) {
    pane.push(heading('Dispatches', t))
    const shown = shownOf(t)
    for (const d of shown) pane.push(dispatchRow(d))
    if (!shown.length) pane.push({ row: 'text', tone: 'idle', text: 'None outstanding.' })
  }
  const teamModels: TeamModel[] = teams.map((t) => ({
    name: t.name,
    isDefault: t.isDefault,
    summary: summaryOf(t),
    pipeline: t.pipeline === null ? null : pipelineRows(t, 'Pipeline idle.'),
    members: t.members.map((m) => ({ stream: m.stream, row: memberRow(m) })),
    dispatches: shownOf(t).map((d) => ({ member: d.member, row: dispatchRow(d) })),
  }))

  const toasts: View['toasts'] = []
  for (const d of dispatches) {
    if (d.state !== 'reported') continue
    const took = Number.isNaN(d.reportedMs) || Number.isNaN(d.sentMs) ? '' : ` · ${duration(d.reportedMs - d.sentMs)}`
    toasts.push({ key: `reported:${d.id}`, text: `${d.label} reported · ${d.slug}${took}` })
  }
  for (const m of members) {
    if (m.activity !== 'blocked') continue
    const why = m.note || (m.blockedBy === 'permission' ? 'waiting for permission' : 'waiting on a prompt')
    const where = m.route === 'pane' ? 'answer it through the Orchestrator' : 'answer it in its pane'
    toasts.push({ key: `blocked:${m.name}:${m.activityAt}`, text: `${m.label} blocked · ${why} · ${where}` })
  }
  for (const d of dispatches) {
    if (d.state === 'stalled') toasts.push({ key: `stalled:${d.id}`, text: `${d.label} stalled · ${d.slug} · ${stalledWhy(d, nowMs)}` })
    if (d.state === 'overdue') toasts.push({ key: `overdue:${d.id}`, text: overdueText(d, nowMs) })
  }
  return { band, pane, teams: teamModels, toasts, owned: mine.owners !== null }
}

/**
 * The seen toast keys whose subject is still in the doc: the dispatch of a `reported:`, `stalled:` or `overdue:` key, the
 * member of a `blocked:` key. Anything else is dropped.
 */
export function keepSeen(seen: readonly unknown[], doc: Doc, nowMs: number): string[] {
  const teams = readTeams(doc, nowMs)
  const dispatchKeys = new Set(teams.flatMap((t) => t.dispatches.flatMap((d) => [`reported:${d.id}`, `stalled:${d.id}`, `overdue:${d.id}`])))
  const memberPrefixes = teams.flatMap((t) => t.members.map((m) => `blocked:${m.name}:`))
  return seen.filter((k): k is string => typeof k === 'string' && (dispatchKeys.has(k) || memberPrefixes.some((p) => k.startsWith(p))))
}

/** Display width, one cell per code point: the band, the Pane and the cut all count this way. */
const width = (s: string): number => [...s].length

/** `s` cut to `n` cells, ending in `…` when anything was cut. */
export function cut(s: string, n: number): string {
  const c = [...s]
  return c.length <= n ? s : n <= 0 ? '' : c.slice(0, n - 1).join('') + '…'
}

/** The band's Pane button: its label, its drawn width (`[ Pane ]`), the gap before it, and the narrowest band that draws it. */
export const PANE_BUTTON = { label: 'Pane', width: 8, gap: 1, minColumns: 40 }

/** The band's View button, drawn in place of Pane in a session that is not a member's: same drawn width (`[ View ]`), gap and narrowest band. */
export const VIEW_BUTTON = { label: 'View', width: PANE_BUTTON.width, gap: PANE_BUTTON.gap, minColumns: PANE_BUTTON.minColumns }

/** The band's Dismiss button, drawn right of Pane: its label, its drawn width (`[ Dismiss ]`), the gap before it, and the narrowest band that draws both buttons. */
export const DISMISS_BUTTON = { label: 'Dismiss', width: 11, gap: 1, minColumns: PANE_BUTTON.minColumns + 12 }

/**
 * The teams of a held view that the viewer owns, each with the fields the Dismiss dialogs read: none unless the view is marked
 * `owned`. A value held in state can come from another version of this module, so every field is checked, not assumed.
 */
export function ownedTeams(view: unknown): TeamModel[] {
  const v = record(view)
  if (v === null || v.owned !== true || !Array.isArray(v.teams)) return []
  const list = (x: unknown): string[] => (Array.isArray(x) && x.every((n) => typeof n === 'string') ? (x as string[]) : [])
  const out: TeamModel[] = []
  for (const t of v.teams) {
    const r = record(t), sm = r === null ? null : record(r.summary)
    if (r === null || sm === null || typeof r.name !== 'string' || typeof r.isDefault !== 'boolean' || typeof sm.members !== 'number') continue
    const open = (Array.isArray(sm.open) ? sm.open : []).map(record).filter((d): d is Record<string, unknown> => d !== null && typeof d.member === 'string' && typeof d.state === 'string')
    out.push({ ...(r as unknown as TeamModel), summary: { members: sm.members, busy: list(sm.busy), blocked: list(sm.blocked), open: open.map((d) => ({ member: d.member as string, state: d.state as string })) } })
  }
  return out
}

/**
 * Which buttons the band draws at `columns` and how wide its text may be. Pane shows from PANE_BUTTON.minColumns; Dismiss
 * also needs DISMISS_BUTTON.minColumns and a view that lists at least one team the session owns (an `owners` document).
 */
export function bandButtons(view: View | null, columns: number): { pane: boolean; dismiss: boolean; textColumns: number } {
  const pane = columns >= PANE_BUTTON.minColumns
  const dismiss = pane && columns >= DISMISS_BUTTON.minColumns && ownedTeams(view).length > 0
  const taken = (pane ? PANE_BUTTON.width + PANE_BUTTON.gap : 0) + (dismiss ? DISMISS_BUTTON.width + DISMISS_BUTTON.gap : 0)
  return { pane, dismiss, textColumns: columns - taken }
}

/** The band's one line at `columns` and its tone, or null when there is no band. */
export function bandLine(view: View | null, columns: number): { text: string; tone: Tone } | null {
  // A value held in state can come from another version of this module, so its shape is checked, not assumed.
  const band = typeof view === 'object' && view !== null ? (view as { band?: unknown }).band : null
  if (typeof band !== 'object' || band === null) return null
  const { head, tail, tone } = band as { head?: unknown; tail?: unknown; tone?: unknown }
  if (typeof head !== 'string' || typeof tone !== 'string' || !Array.isArray(tail)) return null
  let text = head
  for (const item of tail) {
    const longer = `${text} · ${String(item)}`
    if (width(longer) > columns) break
    text = longer
  }
  return { text: cut(text, columns), tone: tone as Tone }
}

/** The band's line when there is no view, for the only causes that name a session of a team; null for any other, which draws no band. */
export function noViewLine(why: unknown, columns: number): { text: string; tone: Tone } | null {
  const text = why === 'member session' ? 'member session' : why === 'expired' ? 'status expired' : null
  return text === null ? null : { text: cut(`Wrangl · ${text}`, columns), tone: 'idle' }
}

/**
 * The Pane's rows at `columns`, each with its tone. Never empty: no view, or a held value that is not a view,
 * is a dim message (naming `why` when given), and a view that lists no team says so in a dim message. A message wraps to
 * `columns`; the band and the table rows are cut to one row.
 */
export function paneRows(view: View | null, columns: number, why: string | null = null): { text: string; tone: Tone }[] {
  const message = (text: string) => wrapRows(text, columns).map((t) => ({ text: t, tone: 'idle' as Tone }))
  const none = message(`No hierarchy status here${typeof why === 'string' && why ? ` (${why})` : ''}.`)
  const pane = typeof view === 'object' && view !== null ? (view as { pane?: unknown }).pane : null
  if (!Array.isArray(pane)) return none
  if (pane.length === 0) return message('No live team in the status file.')
  try {
    return (pane as PaneRow[]).map((r) => ({ text: drawRow(r, columns), tone: r.tone }))
  } catch {
    return none
  }
}

const bar = (pct: number): string => '█'.repeat(Math.floor(pct / 10)) + '░'.repeat(10 - Math.floor(pct / 10))

/** One Pane row at `columns`, after the width fallback and then the cuts. */
function drawRow(r: PaneRow, columns: number): string {
  if (r.row === 'text') return cut(r.text, columns)
  if (r.row === 'member') {
    const state = r.state
    return fitCut(r.name, columns, (name) => (columns < 40 ? `${name} ${state}` : `${name} · ${r.kind} · ${r.route} · ${state}`))
  }
  return fitCut(r.slug, columns, (slug) => (columns < 28 ? `${slug} ${r.state}` : dispatchLine(r, slug, columns)))
}

/** A dispatch row's full form at `columns` (28 or more), which picks how much of it shows. */
function dispatchLine(r: Extract<PaneRow, { row: 'dispatch' }>, slug: string, columns: number): string {
  const parts = [`${slug} → ${r.label}`]
  if (columns >= 40) parts.push(`eta ${r.eta ?? '—'}`)
  if (r.pct !== null) parts.push(columns >= 60 ? `${bar(r.pct)} ${r.pct}%` : `${r.pct}%`)
  parts.push(r.elapsed, r.state)
  return parts.join(' | ')
}

/** Cuts `name` (down to 8 cells) until `row(name)` fits `columns`, then cuts the whole row. */
function fitCut(name: string, columns: number, row: (name: string) => string): string {
  const full = row(name)
  const over = width(full) - columns
  if (over <= 0) return full
  return cut(width(name) > 8 ? row(cut(name, Math.max(8, width(name) - over))) : full, columns)
}

/** The narrowest Pane that draws its sections in borders; below it the borders go and the titles stay. */
export const BORDER_MIN = 40

/** One line of a section: an optional icon cell (with the theme key coloring it, null for dim), its tone, its text already cut to fit, and its indent. */
export type SectionRow = { icon: string | null; iconColor: string | null; tone: Tone; text: string; indent: number; focus: string | null }
/** One Pane section: a box (or, below BORDER_MIN, a title line) holding rows. `color` is the box and title theme key, null for dim. */
export type Section = { section: 'hierarchy' | 'team' | 'stream' | 'dispatches' | 'mail' | 'none' | 'sitrep'; key: string; title: string; color: string | null; bordered: boolean; rows: SectionRow[] }

/** A member or dispatch row's text at `columns`, cut to `room` cells. */
function sectionText(r: PaneRow, columns: number, room: number): string {
  if (r.row === 'text') return cut(r.text, room)
  if (r.row === 'member') {
    const base = typeof r.base === 'string' ? r.base : r.state
    return fitCut(r.name, room, (name) => (columns < BORDER_MIN ? `${name} ${base}` : `${name} · ${r.kind} · ${r.route} · ${r.state}`))
  }
  return fitCut(r.slug, room, (slug) => (columns < BORDER_MIN ? `${slug} ${r.state}` : dispatchLine(r, slug, columns)))
}

/**
 * The Pane as sections at `columns`: per team, HIERARCHY (when it has a pipeline), TEAM (when it has a member; the
 * members with no stream), one STREAM per stream by name (its members, then the dispatches sent to them, indented),
 * and DISPATCHES (the rest). A section with no rows is left out, except a TEAM whose members are all in streams,
 * which stays as a title. Never empty and never throws: no view, a held value that is not a view, or one from a
 * version without teams gives one borderless section of paneRows' lines.
 */
export function paneSections(view: View | null, columns: number, why: string | null = null): Section[] {
  const flat = (): Section[] => [{
    section: 'none', key: 'none', title: '', color: null, bordered: false,
    rows: paneRows(view, columns, why).map((r) => ({ icon: null, iconColor: null, tone: r.tone, text: r.text, indent: 0, focus: null })),
  }]
  const teams = typeof view === 'object' && view !== null ? (view as { teams?: unknown }).teams : null
  if (!Array.isArray(teams) || teams.length === 0) return flat()
  const bordered = columns >= BORDER_MIN
  const inner = Math.max(1, columns - (bordered ? 2 : 0))
  try {
    const row = (r: PaneRow, indent: number): SectionRow => {
      const iconed = r.row !== 'text'
      let text = sectionText(r, columns, Math.max(0, inner - indent - (iconed ? 2 : 0)))
      // A focusable member's name is drawn as the click target and the rest of the row after it. A name the cut shortened is
      // no target: what is shown must be what is focused, so that row is drawn whole and is not clickable.
      let focus: string | null = null
      if (r.row === 'member' && r.focus === true && text.startsWith(r.name)) { focus = r.name; text = text.slice(r.name.length) }
      return { icon: iconed ? r.icon : null, iconColor: iconed ? r.iconColor : null, tone: r.tone, indent, focus, text }
    }
    const out: Section[] = []
    const multi = teams.length > 1
    const add = (section: Section['section'], key: string, title: string, color: string | null, rows: SectionRow[]) => out.push({ section, key, title: cut(title, inner), color, bordered, rows })
    for (const [ti, t] of (teams as TeamModel[]).entries()) {
      const suffix = multi ? ` ${t.name}` : ''
      if (t.pipeline !== null) add('hierarchy', `hierarchy:${ti}`, `HIERARCHY${suffix}`, STYLE.box.hierarchy, t.pipeline.map((r) => row(r, 0)))
      const streams = [...new Set(t.members.map((m) => m.stream).filter((n) => typeof n === 'string' && n !== ''))].sort()
      if (t.members.length) add('team', `team:${ti}`, `TEAM ${t.name}`, STYLE.box.team, t.members.filter((m) => !streams.includes(m.stream)).map((m) => row(m.row, 0)))
      const claimed = new Set<number>()
      for (const [si, stream] of streams.entries()) {
        const names = new Set(t.members.filter((m) => m.stream === stream).map((m) => (m.row.row === 'member' ? m.row.name : '')))
        const mine = t.dispatches.flatMap((d, i) => (names.has(d.member) ? (claimed.add(i), [row(d.row, 2)]) : []))
        add('stream', `stream:${ti}:${stream}`, `STREAM ${stream}`, STYLE.streams[si % STYLE.streams.length],
          [...t.members.filter((m) => m.stream === stream).map((m) => row(m.row, 0)), ...mine])
      }
      const rest = t.dispatches.filter((_, i) => !claimed.has(i))
      if (rest.length) add('dispatches', `dispatches:${ti}`, `DISPATCHES${suffix}`, STYLE.box.dispatches, rest.map((d) => row(d.row, 0)))
    }
    return out.length ? out : flat()
  } catch {
    return flat()
  }
}

/** The toast_seconds the status toasts and the band notice last: 10 s by default. */
export const TOAST_DEFAULT_MS = 10000
/** The longest toast the host accepts, in seconds: ui.toast drops a timeoutMs above 60000. */
export const TOAST_MAX_S = 60
// A string, not a regex literal: the guard's lexer reads a $ in a regex literal as code.
const PLAIN_DECIMAL = new RegExp('^\\s*-?\\d+(\\.\\d+)?\\s*$')

/**
 * How long a toast stays, in ms, from the raw `toast_seconds` option: 0 turns toasts off, else a whole number of
 * seconds from 2 to 60 (the host rejects a toast longer than 60 s). A value counts as a number only when it is a finite number or a plain decimal string (a
 * value set through configure arrives as a string); Number() is never used, since it reads '' and [] as 0 and would
 * switch toasts off. Only an exact 0 is off; a negative or anything non-numeric is the default.
 */
export function toastMs(raw: unknown): number {
  const n = typeof raw === 'number' ? raw : typeof raw === 'string' && PLAIN_DECIMAL.test(raw) ? parseFloat(raw) : NaN
  if (!Number.isFinite(n) || n < 0) return TOAST_DEFAULT_MS
  if (n === 0) return 0
  return Math.min(TOAST_MAX_S, Math.max(2, Math.round(n))) * 1000
}

/** The labels the Dismiss dialogs use for `teams`: the shown name, except that the default team is `@default` when another team shows the same name. */
export function teamLabels(teams: readonly { name: string; isDefault: boolean }[]): string[] {
  return teams.map((t) => (t.isDefault && teams.some((o) => o !== t && o.name === t.name) ? '@default' : t.name))
}

/** A disband plan as the Dismiss dialog reads it. */
export type Plan = { token: string; close: string[]; warnings: string[]; strays: string[]; notClosable: string[] }
export type PlanReading = { kind: 'plan'; plan: Plan } | { kind: 'nothing'; reason: string } | { kind: 'failed'; reason: string }

const words = (v: unknown): string[] => (Array.isArray(v) ? v.map(str).filter((x) => x !== '') : [])
const nameOf = (v: unknown): string => {
  const r = record(v)
  return r === null ? str(v) : str(r.name) || str(r.role)
}

/** The first line of `stderr` without roster's own prefix, cut to 120 cells; `fallback` when there is none. */
export function failReason(stderr: unknown, fallback: string): string {
  const line = (typeof stderr === 'string' ? stderr : '').split('\n').map((l) => str(l.replace(/^roster\.mjs: /, '')).trim()).find((l) => l !== '')
  return line === undefined ? fallback : cut(line, 120)
}

/**
 * The output of `disband` (plan). A plan counts only when it came from a team file: a plan over the pool's live
 * peers (the team file is gone) carries a `source` key and no `team_files`, and is an error here, never something to close.
 */
export function readPlan(stdout: unknown): PlanReading {
  let o: unknown
  try { o = JSON.parse(typeof stdout === 'string' ? stdout : '') } catch { return { kind: 'failed', reason: 'plan failed' } }
  const d = record(o)
  if (d === null) return { kind: 'failed', reason: 'plan failed' }
  if (d.disbanded === false) return { kind: 'nothing', reason: cut(str(d.reason) || 'nothing to close', 120) }
  if ('source' in d || !Array.isArray(d.team_files) || d.team_files.length === 0) return { kind: 'failed', reason: 'no team file' }
  const token = d.close_token
  if (typeof token !== 'string' || token.length !== 16 || /[^0-9a-f]/.test(token) || !Array.isArray(d.close)) return { kind: 'failed', reason: 'plan failed' }
  return { kind: 'plan', plan: { token, close: d.close.map(nameOf), warnings: words(d.warnings), strays: words(d.strays), notClosable: words((Array.isArray(d.not_closable) ? d.not_closable : []).map(nameOf)) } }
}

const plural = (n: number, one: string, many: string): string => `${n} ${n === 1 ? one : many}`
const names = (xs: readonly string[]): string => xs.join(', ')

/** The confirm dialog's question for `label`, from the team's summary and the plan. */
export function dismissQuestion(label: string, s: TeamSummary, plan: Plan): string {
  const open = s.open.length
  const lines = [`Dismiss team ${label}? This closes ${plural(plan.close.length, 'member session', 'member sessions')}${plan.close.length ? `: ${names(plan.close)}` : ''}.`]
  lines.push(s.busy.length ? `BUSY now: ${names(s.busy)}.` : 'No member is busy.')
  if (s.blocked.length) lines.push(`Blocked: ${names(s.blocked)}.`)
  lines.push(open ? `Open dispatches: ${open} (${s.open.map((d) => `${d.member} ${d.state}`).join(', ')}).` : 'No open dispatches.')
  if (s.busy.length || open) lines.push('Their in-flight work will be lost.')
  lines.push(...plan.warnings)
  if (plan.strays.length) lines.push(`Strays: ${names(plan.strays)}.`)
  if (plan.notClosable.length) lines.push(`Not closed (no pane): ${names(plan.notClosable)}.`)
  lines.push('Worktrees, branches and messages are left as they are.')
  return lines.join(' ')
}

/** Whether closing the team would lose work in flight: a live member is working, or a dispatch is open. */
export const inFlight = (s: TeamSummary): boolean => s.busy.length > 0 || s.open.length > 0

/** The second dialog's question, asked when `inFlight`. */
export function anywayQuestion(label: string, s: TeamSummary): string {
  const parts = []
  if (s.busy.length) parts.push(`${names(s.busy)} busy`)
  if (s.open.length) parts.push(plural(s.open.length, 'open dispatch', 'open dispatches'))
  return `${label} has work in flight: ${parts.join('; ')}. Close it anyway and lose that work?`
}

/** The toast for the outcome of `disband --close`. */
export function closeToast(label: string, status: unknown, stdout: unknown, stderr: unknown): string {
  if (status === 2 && /does not match the current close plan/.test(typeof stderr === 'string' ? stderr : '')) return `${label} changed since the check; press Dismiss again.`
  let d: Record<string, unknown> | null = null
  if (status === 0) try { d = record(JSON.parse(typeof stdout === 'string' ? stdout : '')) } catch { d = null }
  if (d === null) return `Could not dismiss ${label}: ${failReason(stderr, 'close failed')}`
  const still = words(d.still_live)
  if (d.closed === true && still.length === 0) return `Dismissed ${label}.`
  const left = still.length ? still : words((Array.isArray(d.kept) ? d.kept : []).map(nameOf))
  return `${label}: not all sessions closed${left.length ? ` (${names(left)})` : ''}.`
}

/** One answer of the mail poll, as the mod acts on it; `socket` is the daemon a CLI answer says serves the push route. */
export type DueAnswer = { ids: string[]; prompt: string | null; compact: string | null; idle: boolean; state: string; acked: string[]; socket: string | null; ring: boolean; escalation?: { kind: string; text: string }; unread?: number }

const isStrings = (x: unknown): x is string[] => Array.isArray(x) && x.every((s) => typeof s === 'string')
const absolute = (p: string): boolean => /^(\/|[A-Za-z]:[\\/])/.test(p)

/**
 * The answer in `raw` (the daemon's or the CLI's, parsed), or null when it is not exactly the version 1 shape: `ids` and `acked`
 * arrays of strings, `prompt` a string exactly when `ids` is not empty, `idle` a boolean, `state` a string, and `daemon` null or an
 * object with an absolute `socket`. Anything else is not acted on. `compact` is the short form of the prompt: a non-empty string
 * is kept; absent, null or any other type is no short form, not an unreadable answer.
 */
export function readDue(raw: unknown): DueAnswer | null {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return null
  const a = raw as Record<string, unknown>
  if (a.v !== 1 || !isStrings(a.ids) || !isStrings(a.acked) || typeof a.idle !== 'boolean' || typeof a.state !== 'string') return null
  if (a.ids.length === 0 ? a.prompt !== null : typeof a.prompt !== 'string' || a.prompt === '') return null
  let socket: string | null = null
  if (a.daemon !== null) {
    if (typeof a.daemon !== 'object' || Array.isArray(a.daemon)) return null
    const s = (a.daemon as Record<string, unknown>).socket
    if (typeof s !== 'string' || !absolute(s)) return null
    socket = s
  }
  const compact = typeof a.compact === 'string' && a.compact !== '' ? a.compact : null
  const answer: DueAnswer = { ids: a.ids, prompt: a.prompt as string | null, compact, idle: a.idle, state: a.state, acked: a.acked, socket, ring: a.ring === true }
  const e = a.escalation
  if (typeof e === 'object' && e !== null && !Array.isArray(e)) {
    const { kind, text } = e as Record<string, unknown>
    if (typeof kind === 'string' && typeof text === 'string' && text !== '') answer.escalation = { kind, text }
  }
  // A held session's count of unread items (ah2-180 D2); any other value is no count, not an unreadable answer.
  if (typeof a.unread === 'number' && Number.isInteger(a.unread) && a.unread >= 0) answer.unread = a.unread
  return answer
}

/** The words a held session with unread mail shows in its status line and toasts (ah2-180 D2); undefined when there is nothing to say. */
export const heldText = (held: boolean, unread: number | undefined): string | undefined =>
  held && unread !== undefined && unread > 0 ? `ah: stopped with ${unread} unread; type anything to resume` : undefined

/** The status line while the last SessionStart did not run through the binary (ah2-206 D5); hooks/register.ts keeps the `hooksOff` flag. */
export const HOOKS_OFF_TEXT = 'Wrangl: not built, hooks off'

/** One delivery of mail as the Mail section shows it: when, which entries, the header's address, the full prompt text and how the submit ended. */
export type MailRecord = { at: number; ids: string[]; address: string; text: string; state: 'delivered' | 'not delivered' }

/** Deliveries the plugin state keeps, newest first. */
export const MAIL_KEEP = 10
/** The most UTF-8 bytes of one delivery's text the plugin state keeps. */
export const MAIL_TEXT_BYTES = 4096

// The host names the plugin; `ah2` was its id before the rename and is still in older transcripts.
const MAIL_FRAMES = ['The wrangl plugin sent a message:', 'The ah2 plugin sent a message:']
// A string, not a regex literal: the guard's lexer reads a $ in a regex literal as code.
const MAIL_HEADER = new RegExp('^ah mail for (\\S+) \\(\\d+\\):$')

/** The lines of a mail prompt from its header on, after any host frame line and blank lines; empty when it has no header. */
function mailLines(text: string): string[] {
  const all = text.split('\n')
  let i = 0
  while (i < all.length && (all[i].trim() === '' || MAIL_FRAMES.includes(all[i]))) i++
  return i < all.length && MAIL_HEADER.test(all[i]) ? all.slice(i) : []
}

/** The address a mail prompt's header names; empty when it has none. */
export function mailAddress(text: string): string {
  const lines = mailLines(text)
  const m = lines.length > 0 ? MAIL_HEADER.exec(lines[0]) : null
  return m === null ? '' : m[1]
}

/** `text` cut to MAIL_TEXT_BYTES, by code point, ending with where the rest can be read. */
export function mailCut(text: string, address: string): string {
  if (utf8Bytes(text) <= MAIL_TEXT_BYTES) return text
  const tail = '… (cut; ah msg inbox --all --address ' + address + ')'
  const room = MAIL_TEXT_BYTES - utf8Bytes(tail)
  let out = ''
  let used = 0
  for (const ch of text) {
    const n = utf8Bytes(ch)
    if (used + n > room) break
    out += ch
    used += n
  }
  return out + tail
}

/** The record of a delivery of `a`, its text cut. */
export function mailRecord(at: number, a: { ids: string[]; prompt: string | null }, state: MailRecord['state']): MailRecord {
  const text = a.prompt ?? ''
  const address = mailAddress(text)
  return { at, ids: a.ids, address, text: mailCut(text, address), state }
}

const isMailRecord = (x: unknown): x is MailRecord => {
  const r = record(x)
  return r !== null && typeof r.at === 'number' && isStrings(r.ids) && typeof r.address === 'string' && typeof r.text === 'string'
    && (r.state === 'delivered' || r.state === 'not delivered')
}

/** The held records with `rec` first, at most MAIL_KEEP; anything held that is not a record is dropped. */
export function mailRecords(held: unknown, rec: MailRecord): MailRecord[] {
  const prior = Array.isArray(held) ? held.filter(isMailRecord) : []
  return [rec, ...prior].slice(0, MAIL_KEEP)
}

/** The kind and sender of the first entry line of a mail prompt (an event is from ah), or null when it has none. */
export function mailGlance(text: string): { kind: string; sender: string } | null {
  for (const line of mailLines(text).slice(1)) {
    if (!line.startsWith('- ')) continue
    const from = /^- (\S+) from (.+)/.exec(line)
    if (from !== null && from[1] !== 'event') {
      const colon = from[2].indexOf(': ')
      return { kind: from[1], sender: colon < 0 ? from[2] : from[2].slice(0, colon) }
    }
    if (line.startsWith('- event ')) return { kind: 'event', sender: 'ah' }
  }
  return null
}

/** `text` with control characters other than the line break and tab drawn as their pictures. */
const pictured = (text: string): string => text.replace(MAIL_CONTROL, picture)

/** The toast for a delivered submit of `count` entries: it names the first sender and the Pane command that shows the full text. */
export function mailToast(prompt: string, count: number): string {
  const g = mailGlance(prompt)
  const sender = pictured(g === null ? 'ah' : g.sender)
  return count > 1
    ? `${count} mails from ${sender} and others. Full text: /wrangl-hierarchy-pane`
    : `Mail from ${sender} (${pictured(g === null ? 'mail' : g.kind)}). Full text: /wrangl-hierarchy-pane`
}

/** The second toast, for a delivery whose record could not be written: the full text is only in the mailbox. */
export function mailUnsavedToast(prompt: string): string {
  return `Mail text could not be saved for the pane. Full text: ah msg inbox --all --address ${pictured(mailAddress(prompt) || '<address>')}`
}

/** The toast for a submit that did not go through. */
export function mailRefusedToast(prompt: string): string {
  return `Mail for ${pictured(mailAddress(prompt) || '<address>')} was not delivered; it will be offered again.`
}

// Control characters other than the line break and tab, drawn as their pictures so peer text cannot send the terminal an escape.
const MAIL_CONTROL = /[\x00-\x08\x0b-\x1f\x7f]/g
const picture = (c: string): string => (c === '\x7f' ? '␡' : String.fromCharCode(0x2400 + c.charCodeAt(0)))

const wallClock = (ms: number): string => {
  const d = new Date(ms)
  const two = (n: number) => String(n).padStart(2, '0')
  return `${two(d.getHours())}:${two(d.getMinutes())}:${two(d.getSeconds())}`
}

/** The most rows one delivery takes in the Mail section; the last is the pointer when the text is longer. */
export const MAIL_ROWS = 60

/**
 * `line` as rows of at most `width` code points. A row ends after the last space that fits and drops that space; failing that,
 * after the last of `/ - @ : , .` that fits, which stays on the row; failing that, at the width. Nothing is added and nothing
 * is cut, so the rows joined (with the dropped spaces put back) are the line. Leading spaces stay on the first row.
 */
export function wrapRows(line: string, width: number): string[] {
  const c = [...line]
  const w = Math.max(1, width)
  const rows: string[] = []
  let start = 0
  while (c.length - start > w) {
    let first = start
    while (first < c.length && c[first] === ' ') first++
    let at = -1
    for (let i = start + w; i > first; i--) {
      if (c[i] === ' ') { at = i; break }
    }
    if (at >= 0) {
      rows.push(c.slice(start, at).join(''))
      start = at + 1
      continue
    }
    let end = start + w
    for (let i = start + w - 1; i >= first; i--) {
      if ('/-@:,.'.includes(c[i])) { end = i + 1; break }
    }
    rows.push(c.slice(start, end).join(''))
    start = end
  }
  rows.push(c.slice(start).join(''))
  return rows
}

/**
 * The Mail section of the Pane at `columns`: the held deliveries newest first, each with a dim line (time, how it ended, ids) and
 * its text wrapped to the section's width (at most MAIL_ROWS rows, the last a pointer to the mailbox when there is more), then the
 * line that says where older mail is. `lost` is why the held records cannot be trusted (a failed write or read); a held value
 * that is not a list counts the same.
 */
export function mailSection(held: unknown, lost: string | null, columns: number): Section {
  const bordered = columns >= BORDER_MIN
  const inner = Math.max(1, columns - (bordered ? 2 : 0))
  const list = held === undefined || held === null ? [] : Array.isArray(held) ? held.filter(isMailRecord) : null
  const why = lost !== null ? lost : list === null ? 'state unreadable' : null
  const address = list !== null && list.length > 0 && list[0].address !== '' ? list[0].address : '<address>'
  const row = (tone: Tone, text: string): SectionRow => ({ icon: null, iconColor: null, tone, text, indent: 0, focus: null })
  const wrapped = (tone: Tone, text: string): SectionRow[] => wrapRows(text, inner).map((t) => row(tone, t))
  const rows: SectionRow[] = []
  if (why !== null) {
    rows.push(...wrapped('warn', `Mail text unavailable (${why}). Run: ah msg inbox --all --address ${address}`))
  } else if (list === null || list.length === 0) {
    rows.push(...wrapped('idle', 'No mail this session.'))
  } else {
    for (const r of list) {
      rows.push(...wrapped('idle', `${wallClock(r.at)} · ${r.state} · ${r.ids.join(',')}`))
      const body = r.text.replace(MAIL_CONTROL, picture).split('\n').flatMap((l) => wrapRows(l, inner))
      if (body.length > MAIL_ROWS) {
        for (const t of body.slice(0, MAIL_ROWS - 1)) rows.push(row('work', t))
        rows.push(row('work', `… ${body.length - (MAIL_ROWS - 1)} more rows: ah msg inbox --all --address ${r.address !== '' ? r.address : '<address>'}`))
      } else {
        for (const t of body) rows.push(row('work', t))
      }
    }
  }
  rows.push(...wrapped('idle', `Older or earlier-session mail: ah msg inbox --all --address ${address}`))
  return { section: 'mail', key: 'mail', title: 'Mail (newest first)', color: null, bordered, rows }
}

/** What the Pane holds of the Sitrep: why there is none (`detail` says what was looked at), or each team's bodies as the daemon served them (null until it has). `answered`: the daemon has replied once. */
export type SitrepHeld = { why: 'no team' | 'down' | null; detail?: string; answered: boolean; teams: { name: string; body: unknown; questions?: unknown }[] }

/** The daemon's id of a hierarchy directory (ActivityLane.DirId): the first 8 hex digits of its SHA-256. */
export async function dirIdOf(dir: string): Promise<string> {
  const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(dir)))
  return [...hash.slice(0, 4)].map((b) => b.toString(16).padStart(2, '0')).join('')
}

/** The mod is read-only, so a question is answered in the web UI, opened by the band's View button. */
export const ANSWER_IN_WEB = 'Answer these in the web UI: View → Web → Inbox.'

const SITREP_STATE: Record<string, string> ={ not_started: 'not started', next: 'next', blocked: 'blocked', waiting_user: 'waiting on you', waiting_orch: 'waiting on Orchestrator', reported: 'reported, waiting on the Orchestrator', in_review: 'in review', approved: 'approved', done: 'done', dropped: 'dropped' }
const millis = (v: unknown): number | null => (typeof v === 'number' && v > 0 ? v : null)

/** One Sitrep row as its lines: the title (or a ship or merge record's words), which is all a region shows, then what its detail adds: the note and the row's facts. */
function sitrepRow(r: Record<string, unknown>, col: string, now: number): { tone: Tone; text: string; indent: number }[] {
  const ago = (ms: number | null) => (ms === null ? '' : age(now - ms))
  const facts = (...parts: string[]) => parts.filter((p) => p !== '').join(' · ')
  const lane = str(r.lane) !== '' ? `lane ${str(r.lane)}` : ''
  if (r.kind === 'ship' || r.kind === 'merge') {
    const sha = str(r.sha).slice(0, 7)
    const words = r.kind === 'ship'
      ? facts(`Shipped ${str(r.ref)}`, sha, r.tag ? 'tag' : '', r.confirmed === false ? 'unconfirmed' : '')
      : facts(`Merged ${str(r.ref)} into ${str(r.into)}`, sha)
    return [{ tone: 'work', text: words, indent: 0 }, { tone: 'idle', text: facts(lane, millis(r.at) === null ? '' : `${ago(millis(r.at))} ago`), indent: 2 }]
  }
  const backlog = r.source === 'backlog'
  const queueRow = col === 'n' || col === 'o'
  const stalled = !backlog && millis(r.stall_at) !== null && now >= (r.stall_at as number) && Array.isArray(r.could_take) && r.could_take.length > 0
  const stream = str(r.stream)
  const where = backlog ? 'from backlog' : (str(r.lane) !== '' && stream === str(r.lane)) ? '' : stream === '_run' ? 'Team to-dos' : stream
  const who = queueRow ? (backlog ? '' : `waiting for ${str(r.role) || 'a member'}`) : str(r.holder).split('/').pop() ?? ''
  const state = queueRow ? '' : SITREP_STATE[str(r.state)] ?? str(r.state).replace(/_/g, ' ')
  const takers = Array.isArray(r.could_take) ? r.could_take.length : 0
  const when = col === 'c' ? (millis(r.done_at) === null ? '' : `${ago(millis(r.done_at))} ago`)
    : col === 's' ? (millis(r.last_activity) === null ? '' : `idle ${ago(millis(r.last_activity))}`)
    : !queueRow ? (millis(r.since) === null ? '' : `for ${ago(millis(r.since))}`)
    : backlog || millis(r.queued_since) === null ? ''
    : stalled ? facts(`stalled ${ago(millis(r.queued_since))}`, `no owner, ${takers} idle`)
    : facts(`waiting ${ago(millis(r.queued_since))}`, takers > 0 ? '' : `no idle ${str(r.role) || 'member'}`)
  const lines: { tone: Tone; text: string; indent: number }[] = [{ tone: stalled ? 'bad' : 'work', text: str(r.title), indent: 0 }]
  if (str(r.note) !== '') lines.push({ tone: 'idle', text: str(r.note), indent: 2 })
  lines.push({ tone: 'idle', text: facts(col === 'm' ? (r.work === 'with_lead' ? 'Report in' : 'No one on it') : '', state, where, lane, who, when), indent: 2 })
  return lines
}

/**
 * The Pane's Sitrep: one section per team, a heading for each region and one row an item (the engine scrolls a Pane taller than its room). Every region is open: a Pane Button may
 * only focus a member or pick a tab, so nothing here folds or opens. Status is the daemon's alone: nothing here derives it.
 */
export function sitrepSections(held: unknown, now: number, columns: number): Section[] {
  const bordered = columns >= BORDER_MIN
  const inner = Math.max(1, columns - (bordered ? 2 : 0))
  const line = (tone: Tone, text: string, indent = 0): SectionRow[] =>
    wrapRows(text, Math.max(1, inner - indent)).map((t) => ({ icon: null, iconColor: null, tone, text: t, indent, focus: null }))
  const single = (tone: Tone, text: string, indent: number): SectionRow =>
    ({ icon: null, iconColor: null, tone, text: cut(text, Math.max(1, inner - indent)), indent, focus: null })
  const notice = (name: string, tone: Tone, text: string): Section => ({ section: 'sitrep', key: 'sitrep-' + name, title: name === '' ? 'Sitrep' : `Sitrep · ${name}`, color: null, bordered, rows: line(tone, text) })
  const h = record(held)
  if (h === null) return [notice('', 'idle', 'Loading…')]
  const why = (text: string) => (str(h.detail) === '' ? text : `${text} ${str(h.detail)}`)
  if (h.why === 'no team') return [notice('', 'idle', why('No open team of yours here, so no sitrep.'))]
  if (h.why === 'down') return [notice('', 'warn', why('The daemon is not answering, so no sitrep.'))]
  const teams = (Array.isArray(h.teams) ? h.teams : []).map(record).filter((t): t is Record<string, unknown> => t !== null)
  if (teams.length === 0) return [notice('', 'idle', 'Loading…')]
  // What waits on the person, from the slice the web Inbox reads: shown only, and above the Sitrep so a long one does not push it out of sight.
  const asked = teams.flatMap((t) => {
    const q = record(t.questions)
    if (q === null) return []
    const rowsOf = (key: string) => (Array.isArray(q[key]) ? (q[key] as unknown[]) : []).map(record).filter((r): r is Record<string, unknown> => r !== null)
    const open = [...rowsOf('open'), ...rowsOf('items')].map((r) => str(r.text) || str(r.title))
    const count = typeof q.waiting_total === 'number' ? q.waiting_total : open.length
    return [{ name: str(t.name), open, count }]
  })
  const waiting = asked.reduce((n, a) => n + a.count, 0)
  const questions: Section[] = asked.length === 0 ? [] : [{ section: 'sitrep', key: 'sitrep-questions', title: waiting > 0 ? `Questions · ${waiting}` : 'Questions', color: null, bordered, rows: waiting === 0 ? line('idle', 'No questions waiting.') : asked.flatMap((a) => [
    ...a.open.map((text) => single('warn', teams.length > 1 ? `${text} · ${a.name}` : text, 0)),
    ...(a.count > a.open.length ? line('idle', `${a.count - a.open.length} more${teams.length > 1 ? ` in ${a.name}` : ''}`) : []),
  ]).concat(line('idle', ANSWER_IN_WEB)) }]
  return [...questions, ...teams.map((t) => {
    const name = str(t.name)
    const b = record(t.body)
    if (b === null) return h.answered === true ? notice(name, 'idle', 'The daemon has no sitrep for this team.') : notice(name, 'idle', 'Loading…')
    const listOf = (rowsKey: string) => (Array.isArray(b[rowsKey]) ? (b[rowsKey] as unknown[]) : []).map(record).filter((r): r is Record<string, unknown> => r !== null)
    const rows = line('idle', `${millis(b.as_of) === null ? 'nothing yet' : `updated ${age(now - (b.as_of as number))} ago`} · ${typeof b.completed_total === 'number' ? b.completed_total : 0} completed in total`)
    for (const g of listOf('regions')) {
      const col = str(g.col)
      const list = listOf(str(g.list))
      if (g.optional === true && list.length === 0) continue
      const count = b[str(g.total)]
      const total = typeof count === 'number' ? count : null
      rows.push(single(col === 'w' ? 'warn' : 'work', `${str(g.label)} ${total !== null && total > list.length ? `${list.length} of ${total}` : list.length}`, 0))
      for (const r of list) rows.push(single(sitrepRow(r, col, now)[0].tone, sitrepRow(r, col, now)[0].text, 2))
    }
    return { section: 'sitrep', key: 'sitrep-' + name, title: `Sitrep · ${name}`, color: null, bordered, rows } as Section
  })]
}

// The background rebuild of ahd (ah2-111), from <AH_HOME>/install.json alone. The herdr mod holds the same rule in
// herdr-desktop/mods/ah2/rebuild-model.js (the mod guard lets register.tsx import values from ./view only); tests/web/rebuild-notice.test.mjs
// drives both copies through the same cases.
export type Rebuild = { version: string; at: string; state: string; stamp: string | null }
export type RebuildNotice = { kind: 'building' | 'ready' | 'failed'; version: string }
// How often a running build is re-read, and for how long before the watch gives up and says nothing more.
export const REBUILD_EVERY_MS = 3000
export const REBUILD_WATCH_MS = 10 * 60_000

// The stamp's text -> its last_rebuild, with the stamp's own version; null when the text is not a stamp with one.
export function rebuildOf(text: unknown): Rebuild | null {
  let j: any
  try { j = JSON.parse(String(text)) } catch { return null }
  const r = j !== null && typeof j === 'object' ? j.last_rebuild : null
  if (r === null || typeof r !== 'object' || typeof r.version !== 'string' || typeof r.at !== 'string' || typeof r.state !== 'string') return null
  return { version: r.version, at: r.at, state: r.state, stamp: typeof j.version === 'string' ? j.version : null }
}

// Given the rebuild this surface last kept (`before`, null: none) and the one the stamp records now (null: unreadable): what to
// say, what to keep (`before` itself when nothing changed, so a caller writes only when keep !== before), and whether to keep
// watching. A rebuild is keyed on version + at, so one already kept says nothing; "ready" and "failed" need its "started" kept first.
export function rebuildStep(before: unknown, now: Rebuild | null): { notice: RebuildNotice | null; keep: unknown; watch: boolean } {
  const b = before !== null && typeof before === 'object' ? (before as Partial<Rebuild>) : null
  const watching = (r: Partial<Rebuild> | null) => r !== null && r.state === 'started'
  if (now === null || (b !== null && b.version === now.version && b.at === now.at)) return { notice: null, keep: before, watch: watching(b) }
  const ended = b !== null && b.state === 'started' && b.version === now.version
  const notice: RebuildNotice | null = now.state === 'started' ? { kind: 'building', version: now.version }
    : ended && now.state === 'ok' && now.stamp === now.version ? { kind: 'ready', version: now.version }
    : ended && now.state === 'failed' ? { kind: 'failed', version: now.version }
    : null
  return { notice, keep: now, watch: watching(now) }
}

// The kept rebuild once the watch gave up: same key, so it says nothing more, and not "started", so it is not watched again.
export function rebuildGaveUp(kept: unknown): unknown {
  return kept !== null && typeof kept === 'object' && (kept as Partial<Rebuild>).state === 'started' ? { ...kept, state: 'gave-up' } : kept
}

// The Claude Code toast's text.
export function rebuildToast(n: RebuildNotice, home: string): string {
  if (n.kind === 'building') return `Wrangl: building ahd ${n.version}…`
  if (n.kind === 'ready') return `Wrangl: ahd ${n.version} is ready`
  return `Wrangl: the ahd ${n.version} build failed; see ${home}/logs/rebuild.log`
}
