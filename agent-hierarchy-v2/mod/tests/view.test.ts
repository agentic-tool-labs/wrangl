import { ANSWER_IN_WEB, dirIdOf, ownedTeamNames, paneTab, registeredStatusFiles, sitrepSections } from '../view.ts'
import { test, expect } from 'claude-code/testing'
import { fixtures } from './fixtures.ts'
import { vectors } from './vectors.ts'
import { mainVectors } from './main-vectors.ts'
import { statusLanguage } from './status-language.ts'
import { mailCut, mailRecord, mailRecords, mailRefusedToast, mailSection, mailToast, mailUnsavedToast, mainCheckoutRoot, MAIL_KEEP, MAIL_ROWS, MAIL_TEXT_BYTES, heldText, readDue, wrapRows } from '../view.ts'
import { anywayQuestion, bandButtons, bandLine, BORDER_MIN, closeToast, DISMISS_BUTTON, dismissQuestion, failReason, inFlight, ownedTeams, readPlan, teamLabels, scope, toastMs, cut, noViewLine, NO_OWNED_TEAM, nullCause, PANE_BUTTON, keepSeen, paneRows, paneSections, STYLE, statusOf, parseDoc, SIZE_CAP, statusText, utf8Bytes, viewModel } from '../view.ts'

const T0 = '2026-01-01T12:00:00.000Z'
const ms = (iso: string) => Date.parse(iso)

// An inline document: one visible entry at T0, expiring a day later; `over` replaces top-level fields,
// `entry` the entry's.
const docText = (over: Record<string, unknown> = {}, entry: Record<string, unknown> = {}) =>
  JSON.stringify({
    schema: 1, written_at: T0, expires_at: '2026-01-02T12:00:00.000Z', enabled: true, member_sessions: [], teams: [],
    timeline: [{ at: T0, live: 1, blocked: 0, out: 1, overdue: 0, stalled: 0, text: '1 live · 1 out', short: '1/1', tone: 'work', visible: true, ...entry }],
    ...over,
  })
const show = (text: string | null, now = T0, sessionId = 'sess-orch', entryOn = true) => statusText(parseDoc(text), ms(now), sessionId, entryOn)

test('every fixture case has a vector, and every vector names a case', async () => {
  expect(Object.keys(vectors).sort()).toEqual(Object.keys(fixtures).sort())
})

for (const [name, v] of Object.entries(vectors)) {
  test(`vector ${name}: the status entry`, async () => {
    expect(statusText(parseDoc(fixtures[name]), ms(v.now), v.sessionId, true)).toBe(v.statusText)
  })
  test(`vector ${name}: the band, the Pane rows and the toasts`, async () => {
    const view = viewModel(parseDoc(fixtures[name]), ms(v.now), v.sessionId)
    expect(bandLine(view, v.columns)).toEqual(v.band)
    expect(paneRows(view, v.columns)).toEqual(v.pane)
    expect(view === null ? [] : view.toasts).toEqual(v.toasts)
  })
}

test('the entry text has no "ah · " prefix', async () => {
  for (const [name, v] of Object.entries(vectors)) {
    const text = statusText(parseDoc(fixtures[name]), ms(v.now), v.sessionId, true)
    if (text !== undefined) expect(text.startsWith('ah')).toBe(false)
  }
})

test('absent: missing, unparseable, schema 2', async () => {
  expect(show(null)).toBe(undefined)
  expect(show('{"schema": 1,')).toBe(undefined)
  expect(show(docText({ schema: 2 }))).toBe(undefined)
  expect(show(docText())).toBe('1 live · 1 out')
})

test('absent: expired at expires_at, shown just before', async () => {
  expect(show(docText(), '2026-01-02T12:00:00.000Z')).toBe(undefined)
  expect(show(docText(), '2026-01-02T11:59:59.999Z')).toBe('1 live · 1 out')
})

test('absent: visible false', async () => {
  expect(show(docText({}, { visible: false }))).toBe(undefined)
})

test('absent: over 262,144 bytes; exactly 262,144 is read', async () => {
  expect(utf8Bytes('·')).toBe(2)
  const base = docText()
  const exact = base + ' '.repeat(SIZE_CAP - utf8Bytes(base))
  expect(utf8Bytes(exact)).toBe(262144)
  expect(show(exact)).toBe('1 live · 1 out')
  expect(show(exact + ' ')).toBe(undefined)
})

test('absent: a malformed shape', async () => {
  const noMembers = JSON.parse(docText())
  delete noMembers.member_sessions
  expect(show(JSON.stringify(noMembers))).toBe(undefined)
  expect(show(docText({ member_sessions: [1] }))).toBe(undefined)
  expect(show(docText({ timeline: [] }))).toBe(undefined)
  expect(show(docText({ timeline: [{ at: 'yesterday', text: 'x', short: 'x', tone: 'idle', visible: true }] }))).toBe(undefined)
  expect(show(docText({ expires_at: undefined }))).toBe(undefined)
  expect(show(docText({ expires_at: '2026-01-02' }))).toBe(undefined)
  expect(show(docText({}, { visible: 'yes' }))).toBe(undefined)
  expect(show(docText({}, { tone: 3 }))).toBe(undefined)
  expect(show(docText({}, { text: undefined }))).toBe(undefined)
  expect(show(docText({}, { short: undefined }))).toBe(undefined)
})

test('an unknown tone string still shows the entry', async () => {
  expect(show(docText({}, { tone: 'purple' }))).toBe('1 live · 1 out')
})

test('control characters are stripped from the text', async () => {
  expect(show(docText({}, { text: 'a\x1b[31mb\x07c\x9fd\x7fe' }))).toBe('a[31mbcde')
})

test('hidden in a member session, shown in any other', async () => {
  const text = docText({ member_sessions: ['sess-m'] })
  expect(show(text, T0, 'sess-m')).toBe(undefined)
  expect(show(text, T0, 'sess-orch')).toBe('1 live · 1 out')
})

test('the timeline pick: before the first entry, at an entry, just before the next', async () => {
  const two = (first: string) => docText({ timeline: [
    { at: first, text: 'A', short: 'A', tone: 'work', visible: true },
    { at: '2026-01-01T12:01:00.000Z', text: 'B', short: 'B', tone: 'bad', visible: true },
  ] })
  expect(show(two('2026-01-01T12:00:10.000Z'), T0)).toBe('A')
  expect(show(two(T0), '2026-01-01T12:01:00.000Z')).toBe('B')
  expect(show(two(T0), '2026-01-01T12:00:59.999Z')).toBe('A')
})

test('the status_entry toggle: off clears the entry, on shows it', async () => {
  expect(show(docText(), T0, 'sess-orch', false)).toBe(undefined)
  expect(show(docText(), T0, 'sess-orch', true)).toBe('1 live · 1 out')
})

// ---- the band, the Pane and the toasts, on inline documents
const at = (offsetMs: number) => new Date(ms(T0) + offsetMs).toISOString()
const member = (over: Record<string, unknown> = {}) => ({
  name: 'demo-architect', role: 'architect', label: 'architect', kind: 'codex', route: 'pane', session_id: null,
  live: true, activity: 'working', activity_at: at(-60000), blocked_by: null, blocked_note: null, ...over,
})
// A small-eta dispatch sent `sentAgo` ms before T0, on the producer's schedule from then.
const dispatch = (over: Record<string, unknown> = {}, sentAgo = 60000) => ({
  id: 'd1', slug: 'build-step', to: 'architect', to_name: 'demo-architect', member: 'demo-architect', label: 'architect',
  eta: 'small', eta_ms: 300000, created: at(-sentAgo), sent_at: at(-sentAgo), checkins: 0, reported_at: null,
  states: [
    { at: at(-sentAgo), state: 'working' },
    { at: at(300000 - sentAgo), state: 'overdue' },
    { at: at(450000 - sentAgo), state: 'stalled', reason: 'no-report' },
  ],
  ...over,
})
const team = (over: Record<string, unknown> = {}) => ({ team: null, members: [member()], dispatches: [dispatch()], dispatches_truncated: 0, pipeline: null, ...over })
const viewOf = (teams: unknown[], entry: Record<string, unknown> = {}, now = T0) =>
  viewModel(parseDoc(docText({ teams }, { out: 1, blocked: 0, ...entry })), ms(now), 'sess-orch')
const band = (teams: unknown[], entry: Record<string, unknown> = {}, now = T0, columns = 200) => bandLine(viewOf(teams, entry, now), columns)
const rows = (teams: unknown[], columns = 120, now = T0) => paneRows(viewOf(teams, {}, now), columns).map((r) => r.text)

test('the Pane button reserves its drawn `[ Pane ]` width and a gap, so the fitted band text never overlaps it', async () => {
  expect(PANE_BUTTON.width).toBe(`[ ${PANE_BUTTON.label} ]`.length)
  const two = [team({ dispatches: [dispatch({ id: 'a', label: 'reviewer' }, 30000), dispatch({ id: 'b' }, 90000)] })]
  for (let columns = PANE_BUTTON.minColumns; columns <= 120; columns++) {
    const line = band(two, { out: 2 }, T0, columns - PANE_BUTTON.width - PANE_BUTTON.gap)
    expect([...line!.text].length).toBeLessThanOrEqual(columns - 9)
  }
})

test('band: the All teams summary line, toned by the worst status, cut to the width', async () => {
  const two = [team({ dispatches: [dispatch({ id: 'a', label: 'reviewer' }, 30000), dispatch({ id: 'b' }, 90000)] })]
  expect(band(two, { out: 2 })).toEqual({ text: '1 team · 1 of 1 agents up · nothing needs you · 2 in flight', tone: 'work' })
  const cut40 = band(two, { out: 2 }, T0, 40)!
  expect([[...cut40.text].length, cut40.text.endsWith('…'), cut40.tone]).toEqual([40, true, 'work'])
})

const idleEntry = { out: 0, blocked: 0, text: '1 live · 0 out', tone: 'idle' }
test('band, idle: an owned team with nothing in flight still counts its teams and agents, with Pane and Dismiss', async () => {
  const quiet = [team({ dispatches: [], members: [member({ activity: 'idle' })] })]
  expect(band(quiet, idleEntry)).toEqual({ text: '1 team · 1 of 1 agents up · nothing needs you · 0 in flight', tone: 'idle' })
  expect(band(quiet, { ...idleEntry, text: '' })?.text).toBe('1 team · 1 of 1 agents up · nothing needs you · 0 in flight')
  expect(band([team({ dispatches: [] })], idleEntry)?.tone).toBe('work')
  expect(band([team({ dispatches: [], members: [member(), member({ name: 'b', live: false })] })], idleEntry)?.text).toBe('1 team · 1 of 2 agents up · nothing needs you · 0 in flight')
  expect(band([team({ dispatches: [] }), team({ dispatches: [], team: 'other' })], idleEntry)?.text).toBe('2 teams · 2 of 2 agents up · nothing needs you · 0 in flight')
  // An owner-scoped document: the owned team with no dispatches and a silent entry gets a band and both buttons.
  const silent = wdoc((d) => {
    d.teams[0].dispatches = []
    for (const tl of [d.timeline, ...d.owners.map((o: any) => o.timeline)]) for (const e of tl) Object.assign(e, { out: 0, blocked: 0, overdue: 0, stalled: 0, text: '', tone: 'idle' })
  })
  const view = dview(silent)
  expect(bandLine(view, 120)?.text).toBe('1 team · 1 of 1 agents up · nothing needs you · 0 in flight')
  expect(bandButtons(view, 120)).toMatchObject({ pane: true, dismiss: true })
})

test('band, no view: noViewLine names a member or an expired team session, and is null for anything else', async () => {
  expect(noViewLine('member session', 80)).toEqual({ text: 'Wrangl · member session', tone: 'idle' })
  expect(noViewLine('expired', 80)).toEqual({ text: 'Wrangl · status expired', tone: 'idle' })
  for (const why of [NO_OWNED_TEAM, 'hierarchy off', null, undefined, 'unreadable', 'no file', 'not visible', 7]) expect(noViewLine(why, 80)).toBeNull()
  expect(noViewLine('expired', 8)!.text.length).toBeLessThanOrEqual(8)
})

test('band, expired doc: an owner or a member keeps the cause, and negative: a session the doc does not name has no team', async () => {
  const late = ms('2026-01-02T12:00:00.000Z')
  const base = JSON.parse(docText())
  const owned = parseDoc(JSON.stringify({ ...base, member_sessions: ['sess-m'], owners: [{ sessions: ['sess-orch'], teams: [], timeline: base.timeline }] }))
  expect(nullCause(owned, late, 'sess-orch')).toBe('expired')
  expect(nullCause(owned, late, 'sess-m')).toBe('expired')
  expect(nullCause(owned, late, 'sess-bare')).toBe(NO_OWNED_TEAM)
  expect(noViewLine(nullCause(owned, late, 'sess-orch'), 80)?.text).toBe('Wrangl · status expired')
  expect(noViewLine(nullCause(owned, late, 'sess-bare'), 80)).toBeNull()
})

test('band, no team: the entry text in its tone, none for an empty one, and an unknown tone reads idle', async () => {
  expect(band([], idleEntry)).toEqual({ text: '1 live · 0 out', tone: 'idle' })
  expect(band([], { ...idleEntry, text: '1 live\x1b · 0 out' })).toEqual({ text: '1 live · 0 out', tone: 'idle' })
  expect(band([], idleEntry, T0, 8)).toEqual({ text: '1 live …', tone: 'idle' })
  expect(band([], { ...idleEntry, text: '' })).toBe(null)
  expect(band([], { ...idleEntry, tone: 'purple' })?.tone).toBe('idle')
  expect(band([], { ...idleEntry, tone: 'warn' })?.tone).toBe('warn')
  expect(band([], { ...idleEntry, tone: 'ok' })?.tone).toBe('ok')
  expect(band([], { ...idleEntry, tone: 'okay' })?.tone).toBe('idle')
})

test('band: a reported task is not in flight', async () => {
  const reported = team({ members: [member({ activity: 'idle' })], dispatches: [dispatch({ states: [{ at: at(-60000), state: 'reported' }] })] })
  expect(band([reported], { out: 1 })).toEqual({ text: '1 team · 1 of 1 agents up · nothing needs you · 0 in flight', tone: 'idle' })
})

test('band: a member session, an invisible entry and an expired document draw none', async () => {
  const doc = (over: Record<string, unknown>, entry: Record<string, unknown> = {}) => parseDoc(docText(over, { out: 0, ...entry }))
  expect(bandLine(viewModel(doc({ member_sessions: ['sess-orch'] }), ms(T0), 'sess-orch'), 120)).toBe(null)
  expect(bandLine(viewModel(doc({}, { visible: false }), ms(T0), 'sess-orch'), 120)).toBe(null)
  expect(bandLine(viewModel(doc({}), ms('2026-01-02T12:00:00.000Z'), 'sess-orch'), 120)).toBe(null)
  expect(bandLine(viewModel(doc({}), ms(T0), 'sess-orch'), 120)).not.toBe(null)
})

const toast = (teams: unknown[], kind: string, entry: Record<string, unknown> = {}, now = T0) =>
  viewOf(teams, entry, now)?.toasts.find((t) => t.key.startsWith(`${kind}:`))?.text

test('stalled: the band counts it as needing you and the toast says the check-in phrase, and who is gone', async () => {
  const stalled = (over: Record<string, unknown>) => [team({ dispatches: [dispatch({ states: [{ at: at(-60000), state: 'stalled', reason: 'no-report' }], ...over })] })]
  expect(band(stalled({}))).toEqual({ text: '1 team · 1 of 1 agents up · 1 needs you · 1 in flight', tone: 'bad' })
  expect(toast(stalled({}), 'stalled')).toBe('architect stalled · build-step · no report after 1m')
  expect(toast(stalled({ checkins: 1 }), 'stalled')).toBe('architect stalled · build-step · 1 check-in unanswered')
  expect(toast(stalled({ checkins: 2 }), 'stalled')).toBe('architect stalled · build-step · 2 check-ins unanswered')
  const gone = (over: Record<string, unknown>) => stalled({ states: [{ at: at(-60000), state: 'stalled', reason: 'member-gone' }], ...over })
  expect(toast(gone({}), 'stalled')).toBe('architect stalled · build-step · demo-architect is gone')
  expect(toast(gone({ member: null }), 'stalled')).toBe('architect stalled · build-step · demo-architect is gone')
  expect(toast(gone({ member: null, to_name: null }), 'stalled')).toBe('architect stalled · build-step · architect is gone')
})

test('overdue: the band counts it as needing you and the toast says the time past the eta and the check-ins sent', async () => {
  const late = (over: Record<string, unknown> = {}) => [team({ dispatches: [dispatch(over, 340000)] })]
  expect(band(late())).toEqual({ text: '1 team · 1 of 1 agents up · 1 needs you · 1 in flight', tone: 'bad' })
  expect(toast(late(), 'overdue')).toBe('architect is 40s past its 5m eta')
  expect(toast(late({ checkins: 2 }), 'overdue', {}, at(100000))).toBe('architect is 2m 20s past its 5m eta · check-in 2 sent')
})

test('band, blocked member: it needs you, in the warn tone', async () => {
  const blocked = [team({ members: [member({ activity: 'blocked' })], dispatches: [] })]
  expect(band(blocked, { out: 0, blocked: 1 })).toEqual({ text: '1 team · 1 of 1 agents up · 1 needs you · 0 in flight', tone: 'warn' })
  expect(toast(blocked, 'blocked', { out: 0, blocked: 1 })).toBe('architect blocked · waiting on a prompt · answer it through the Orchestrator')
  const peer = [team({ members: [member({ activity: 'blocked', route: 'peer' })], dispatches: [] })]
  expect(toast(peer, 'blocked', { out: 0, blocked: 1 })).toBe('architect blocked · waiting on a prompt · answer it in its pane')
})

test('keepSeen keeps a late task\'s toast key while the task is in the doc, and drops it once it is gone', async () => {
  const late = (id: string) => parseDoc(docText({ teams: [team({ dispatches: [dispatch({ id }, 340000)] })] }, { out: 1, blocked: 0 }))!
  expect(keepSeen(['overdue:d1'], late('d1'), ms(T0))).toEqual(['overdue:d1'])
  expect(keepSeen(['overdue:d1'], late('d2'), ms(T0))).toEqual([])
})

test('band: the worst status sets the tone, and needing you counts blocked agents and late or stuck tasks once each', async () => {
  const mixed = [team({
    members: [member({ activity: 'blocked' }), member({ name: 'demo-reviewer', label: 'reviewer' })],
    dispatches: [
      dispatch({ id: 'o1', label: 'reviewer', slug: 'late-1' }, 320000),
      dispatch({ id: 'o2', label: 'reviewer', slug: 'late-2' }, 400000),
      dispatch({ id: 'b1', states: [{ at: at(-60000), state: 'blocked' }] }),
    ],
  })]
  expect(band(mixed, { out: 3, blocked: 1 })).toEqual({ text: '1 team · 2 of 2 agents up · 3 need you · 3 in flight', tone: 'bad' })
})

test('G3: an eta_ms that is not a positive number shows eta —, no bar or pct, no "of {T}m", never NaN', async () => {
  for (const eta_ms of [0, -1, 'large', null, undefined]) {
    const bare = [team({ dispatches: [dispatch({ eta_ms })] })]
    expect(band(bare)?.text).toBe('1 team · 1 of 1 agents up · nothing needs you · 1 in flight')
    expect(rows(bare)).toEqual(['Hierarchy', 'No pipeline run.', 'Team', 'demo-architect · codex · pane · working 1m', 'Dispatches', 'build-step → architect | eta — | 1:00 | working'])
    const late = [team({ dispatches: [dispatch({ eta_ms, states: [{ at: at(-60000), state: 'overdue' }] })] })]
    expect(toast(late, 'overdue')).toBe('architect is past its eta')
    expect(JSON.stringify([band(bare), rows(bare), band(late), rows(late), toast(late, 'overdue')]).includes('NaN')).toBe(false)
  }
})

test('Pane, dispatch rows: the width fallback at 60, 40 and 28 columns', async () => {
  const one = [team({ dispatches: [dispatch({ slug: 's', label: 'a' }, 150000)] })]
  const last = (columns: number) => rows(one, columns).at(-1)
  expect(last(60)).toBe('s → a | eta 5m | █████░░░░░ 50% | 2:30 | working')
  expect(last(59)).toBe('s → a | eta 5m | 50% | 2:30 | working')
  expect(last(40)).toBe('s → a | eta 5m | 50% | 2:30 | working')
  expect(last(39)).toBe('s → a | 50% | 2:30 | working')
  expect(last(28)).toBe('s → a | 50% | 2:30 | working')
  expect(last(27)).toBe('s working')
})

test('Pane, member rows: name · kind · route · state from 40 columns, name state below', async () => {
  expect(rows([team({ members: [member({ name: 'm1' })], dispatches: [] })], 40)[3]).toBe('m1 · codex · pane · working 1m')
  expect(rows([team({ members: [member({ name: 'm1' })], dispatches: [] })], 39)[3]).toBe('m1 working 1m')
})

test('Pane: a long slug or name is cut first, down to 8 characters, then the whole row', async () => {
  expect(rows([team({ dispatches: [] })], 40)[3]).toBe('demo-archit… · codex · pane · working 1m')
  expect(rows([team({ dispatches: [dispatch({}, 150000)] })], 60).at(-1)).toBe('build-s… → architect | eta 5m | █████░░░░░ 50% | 2:30 | wor…')
  const long = [team({ members: [member({ name: 'a-very-long-member-name' })], dispatches: [dispatch({ slug: 'a-very-long-slug-for-this' }, 150000)] })]
  expect(rows(long, 30)).toContain('a-very-long-member… working 1m')
  expect(rows(long, 27).at(-1)).toBe('a-very-long-slug-f… working')
  expect(rows(long, 12).at(-1)).toBe('a-very-… wo…')
})

test('Pane: member states and tones, and the age shown for pane members only', async () => {
  const people = [team({
    dispatches: [],
    members: [
      member({ name: 'p-59s', activity_at: at(-59000) }),
      member({ name: 'p-61s', activity: 'blocked', activity_at: at(-61000) }),
      member({ name: 'p-1h', activity: 'idle', activity_at: at(-3600000) }),
      member({ name: 'peer', route: 'peer', kind: 'claude', activity: 'working' }),
      member({ name: 'gone', live: false, activity: 'working' }),
      member({ name: 'odd', activity: 'thinking' }),
    ],
  })]
  const drawn = paneRows(viewOf(people), 120).slice(3, 9)
  expect(drawn).toEqual([
    { text: 'p-59s · codex · pane · working 59s', tone: 'work' },
    { text: 'p-61s · codex · pane · needs you 1m', tone: 'warn' },
    { text: 'p-1h · codex · pane · idle 1h', tone: 'idle' },
    { text: 'peer · claude · peer · working', tone: 'work' },
    { text: 'gone · codex · pane · ended', tone: 'idle' },
    { text: 'odd · codex · pane · thinking 1m', tone: 'idle' },
  ])
})

test('Pane: a working peer shows its last finished tool from the current turn, and nothing else changes', async () => {
  const peer = (over: Record<string, unknown>) => member({ name: 'peer', route: 'peer', kind: 'claude', activity_at: at(-360000), ...over })
  const state = (m: Record<string, unknown>) => paneRows(viewOf([team({ members: [m], dispatches: [] })]), 120)[3].text.replace('peer · claude · peer · ', '')
  // V1: the tool is from this turn
  expect(state(peer({ last_tool: 'Edit', last_tool_at: at(-12000) }))).toBe('working 6m · last Edit 12s')
  expect(state(peer({ last_tool: 'Edit', last_tool_at: at(-360000) }))).toBe('working 6m · last Edit 6m')
  // V2: no tool
  expect(state(peer({}))).toBe('working')
  expect(state(peer({ last_tool: null, last_tool_at: null }))).toBe('working')
  // V3: an idle member keeps today's text
  expect(state(peer({ activity: 'idle', last_tool: 'Edit', last_tool_at: at(-12000) }))).toBe('idle')
  // V4: a pane member keeps today's text
  expect(state(peer({ route: 'pane', last_tool: 'Edit', last_tool_at: at(-12000) })).replace('peer · claude · pane · ', '')).toBe('working 6m')
  // V5: a tool from the previous turn
  expect(state(peer({ last_tool: 'Edit', last_tool_at: at(-361000) }))).toBe('working')
  // V6: unparseable timestamps, no throw
  expect(state(peer({ last_tool: 'Edit', last_tool_at: 'soon' }))).toBe('working')
  expect(state(peer({ activity_at: 'soon', last_tool: 'Edit', last_tool_at: at(-12000) }))).toBe('working')
  expect(state(peer({ last_tool: 'Edit', last_tool_at: 42 }))).toBe('working')
  // a gone member is gone
  expect(state(peer({ live: false, last_tool: 'Edit', last_tool_at: at(-12000) }))).toBe('ended')
})

test('Pane, dispatches: expired rows hidden, reported kept, newest first, state tones', async () => {
  const many = [team({ dispatches: [
    dispatch({ id: 'w', slug: 'old-work' }, 200000),
    dispatch({ id: 'r', slug: 'done', reported_at: at(-10000), states: [{ at: at(-10000), state: 'reported' }, { at: at(590000), state: 'expired' }] }, 120000),
    dispatch({ id: 'x', slug: 'gone-by', states: [{ at: at(-700000), state: 'reported' }, { at: at(-100000), state: 'expired' }] }, 900000),
    dispatch({ id: 'u', slug: 'odd', states: [{ at: at(-5000), state: 'paused' }] }, 5000),
  ] })]
  expect(paneRows(viewOf(many), 120).slice(5)).toEqual([
    { text: 'odd → architect | eta 5m | ░░░░░░░░░░ 1% | 0:05 | paused', tone: 'idle' },
    { text: 'done → architect | eta 5m | ████░░░░░░ 40% | 2:00 | done', tone: 'ok' },
    { text: 'old-work → architect | eta 5m | ██████░░░░ 66% | 3:20 | working', tone: 'work' },
  ])
})

test('Pane: pipeline lines, waiting decisions, and No pipeline run. whenever no item is open', async () => {
  const pipe = (items: unknown[], waiting = 0) => [team({ pipeline: { anchor_id: 'a', started: at(-600000), round_cap: 3, waiting_on_user: waiting, items } })]
  expect(rows(pipe([{ slug: 's', rounds: 2, open: true, to: 'reviewer' }, { slug: 't', rounds: 1, open: false, to: 'architect' }], 2)).slice(0, 3))
    .toEqual(['Hierarchy', 'round 2/3 · reviewer', '2 decisions waiting for you'])
  expect(rows(pipe([{ slug: 't', rounds: 1, open: false, to: 'architect' }])).slice(0, 2)).toEqual(['Hierarchy', 'No pipeline run.'])
  expect(rows(pipe([], 1)).slice(0, 3)).toEqual(['Hierarchy', 'No pipeline run.', '1 decision waiting for you'])
})

test('Pane: with more than one live team, each heading names its team, null as default', async () => {
  const both = [team({ team: 'alpha', dispatches: [] }), team({ dispatches: [] })]
  expect(rows(both).filter((r) => /^(Hierarchy|Team|Dispatches)/.test(r)))
    .toEqual(['Hierarchy alpha', 'Hierarchy default', 'Team alpha', 'Team default', 'Dispatches alpha', 'Dispatches default'])
})

test('Pane: control characters are stripped from document strings; no view draws one dim line', async () => {
  const dirty = [team({ members: [member({ name: 'demo\x1b[31m-architect' })], dispatches: [dispatch({ slug: 'build\x07-step' })] })]
  const drawn = rows(dirty)
  expect(drawn).toContain('demo[31m-architect · codex · pane · working 1m')
  expect(drawn.at(-1)).toBe('build-step → architect | eta 5m | ██░░░░░░░░ 20% | 1:00 | working')
  expect(paneRows(null, 120)).toEqual([{ text: 'No hierarchy status here.', tone: 'idle' }])
})

test('toasts: reported with its time, blocked by note, permission or prompt, stalled with who is gone', async () => {
  const view = viewOf([team({
    members: [
      member({ name: 'n1', activity: 'blocked', activity_at: at(-1000), blocked_note: 'Allow? (y/n)' }),
      member({ name: 'n2', label: 'reviewer', activity: 'blocked', activity_at: at(-2000), blocked_by: 'permission' }),
      member({ name: 'n3', label: 'implementor', activity: 'blocked', activity_at: at(-3000), blocked_by: 'login' }),
    ],
    dispatches: [
      dispatch({ id: 'r1', reported_at: at(-200000), states: [{ at: at(-200000), state: 'reported' }] }, 600000),
      dispatch({ id: 's1', member: 'demo-reviewer', label: 'reviewer', slug: 'review', states: [{ at: at(-1000), state: 'stalled', reason: 'member-gone' }] }),
    ],
  })], { out: 1, blocked: 3 })
  expect(view?.toasts).toEqual([
    { key: 'reported:r1', text: 'architect reported · build-step · 6m 40s' },
    { key: `blocked:n1:${at(-1000)}`, text: 'architect blocked · Allow? (y/n) · answer it through the Orchestrator' },
    { key: `blocked:n2:${at(-2000)}`, text: 'reviewer blocked · waiting for permission · answer it through the Orchestrator' },
    { key: `blocked:n3:${at(-3000)}`, text: 'implementor blocked · waiting on a prompt · answer it through the Orchestrator' },
    { key: 'stalled:s1', text: 'reviewer stalled · review · demo-reviewer is gone' },
  ])
})

test('cut: cells counted by code point, ending in …', async () => {
  expect(cut('abcdef', 6)).toBe('abcdef')
  expect(cut('abcdef', 4)).toBe('abc…')
  expect(cut('██████', 3)).toBe('██…')
  expect(cut('abc', 0)).toBe('')
})

test('keepSeen: keys whose dispatch or member is still in the doc stay, everything else goes', async () => {
  const doc = parseDoc(docText({ teams: [team()] }, { out: 1, blocked: 0 }))
  expect(doc).not.toBe(null)
  const seen = ['reported:d1', 'stalled:d1', 'stalled:gone', `blocked:demo-architect:${at(-1000)}`, 'blocked:someone:x', 'other:d1', 7, null]
  expect(keepSeen(seen, doc!, ms(T0))).toEqual(['reported:d1', 'stalled:d1', `blocked:demo-architect:${at(-1000)}`])
})

test('a visible entry that lists no team draws one dim line saying so, and the band shows the entry text', async () => {
  const noTeamKey = JSON.stringify({ ...JSON.parse(docText()), teams: undefined })
  for (const text of [docText(), docText({ teams: [1, 'x'] }), noTeamKey]) {
    const view = viewModel(parseDoc(text), ms(T0), 'sess-orch')
    expect(view).not.toBe(null)
    expect(paneRows(view, 80)).toEqual([{ text: 'No live team in the status file.', tone: 'idle' }])
    expect(bandLine(view, 80)).toEqual({ text: '1 live · 1 out', tone: 'work' })
  }
})

test('a held value that is not a view draws the null-view line and no band, and never throws', async () => {
  for (const held of [{}, { pane: 'x' }, { pane: null }, 7, 'x', [], { pane: [1] }, { pane: [{}] }, { band: 3 }, { band: { head: 1, tail: [], tone: 'work' } }, { band: { head: 'h', tail: 'x', tone: 'work' } }]) {
    const rows = paneRows(held as any, 80)
    expect(rows.length).toBe(1)
    expect(typeof rows[0].text).toBe('string')
    expect(bandLine(held as any, 80)).toBe(null)
  }
  expect(paneRows({} as any, 80)).toEqual([{ text: 'No hierarchy status here.', tone: 'idle' }])
  expect(paneRows({ pane: 'x' } as any, 80, 'expired')).toEqual([{ text: 'No hierarchy status here (expired).', tone: 'idle' }])
})

test('the empty line names its cause, and wraps to the width', async () => {
  expect(paneRows(null, 80)).toEqual([{ text: 'No hierarchy status here.', tone: 'idle' }])
  expect(paneRows(null, 80, 'hierarchy off')).toEqual([{ text: 'No hierarchy status here (hierarchy off).', tone: 'idle' }])
  expect(paneRows(null, 20, 'hierarchy off').map((r) => r.text)).toEqual(['No hierarchy status', 'here (hierarchy', 'off).'])
})

test('nullCause names why a document gives no view', async () => {
  expect(nullCause(null, ms(T0), 'sess-orch')).toBe('unreadable')
  expect(nullCause(parseDoc(docText({}, { visible: false })), ms(T0), 'sess-orch')).toBe('not visible')
  expect(nullCause(parseDoc(docText({ enabled: false }, { visible: false })), ms(T0), 'sess-orch')).toBe('hierarchy off')
  expect(nullCause(parseDoc(docText({ member_sessions: ['sess-m'] })), ms(T0), 'sess-m')).toBe('member session')
  expect(nullCause(parseDoc(docText()), ms('2026-01-02T12:00:00.000Z'), 'sess-orch')).toBe('expired')
  // Must NOT change: an expired doc is expired before it is anything else, and a doc with no `enabled` boolean is not "hierarchy off".
  expect(nullCause(parseDoc(docText({ enabled: false, member_sessions: ['sess-m'] }, { visible: false })), ms('2026-01-03T00:00:00.000Z'), 'sess-m')).toBe('expired')
  expect(nullCause(parseDoc(docText({ enabled: 'no' }, { visible: false })), ms(T0), 'sess-orch')).toBe('not visible')
  // A member session sees no view, and the line it gets names no team data.
  expect(viewModel(parseDoc(docText({ member_sessions: ['sess-m'], teams: [team()] })), ms(T0), 'sess-m')).toBe(null)
})

// ---- the sectioned Pane
const peer = (name: string, over: Record<string, unknown> = {}) => member({ name, label: name, kind: 'claude', route: 'peer', ...over })
const secs = (teams: unknown[], columns = 120) => paneSections(viewOf(teams), columns)
const titles = (teams: unknown[], columns = 120) => secs(teams, columns).map((x) => x.title)
const line = (r: { icon: string | null; text: string; indent: number }) => `${' '.repeat(r.indent)}${r.icon === null ? '' : r.icon + ' '}${r.text}`
const lineWidth = (r: { icon: string | null; text: string; indent: number }) => [...line(r)].length
const streamTeam = (over: Record<string, unknown> = {}) => team({
  members: [peer('lead'), peer('builder', { stream: 'api' }), peer('checker', { stream: 'api' }), peer('writer', { stream: 'docs', live: false })],
  dispatches: [dispatch({ id: 'd1', slug: 'fix-auth', member: 'builder', to_name: 'builder', label: 'builder' }), dispatch({ id: 'd2', slug: 'free-task', member: 'lead', to_name: 'lead', label: 'lead' })],
  ...over,
})
const PIPE = { anchor_id: 'a', started: T0, round_cap: 3, waiting_on_user: 0, items: [{ slug: 'ac', rounds: 2, open: true, to: 'reviewer' }] }

test('G1 no streams: one TEAM section with every member, no STREAM', async () => {
  const x = secs([team({ members: [peer('a'), peer('b')], dispatches: [] })])
  expect(x.map((z) => z.section)).toEqual(['team'])
  expect(x[0].rows.map((r) => r.text.split(' ')[0])).toEqual(['a', 'b'])
})

test('G2 streams by name after TEAM, G3 a dispatch to a streamed member sits in its stream after the members, indented 2', async () => {
  const x = secs([team({
    members: [peer('c'), peer('a', { stream: 'b' }), peer('b', { stream: 'a' })],
    dispatches: [dispatch({ member: 'a', slug: 'to-a' })],
  })])
  expect(x.map((z) => z.title)).toEqual(['TEAM default', 'STREAM a', 'STREAM b'])
  const b = x[2].rows
  expect(b.map((r) => r.indent)).toEqual([0, 2])
  expect(b[1].text.startsWith('to-a')).toBe(true)
  expect(b[1].icon).toBe(STYLE.icon.working.glyph)
})

test('G4 a dispatch to an unknown or an unstreamed member goes to DISPATCHES; G5 no leftovers, no DISPATCHES, and no empty section but TEAM', async () => {
  const left = secs([team({ members: [peer('c'), peer('a', { stream: 'x' })], dispatches: [dispatch({ member: 'nobody', slug: 'one' }), dispatch({ id: 'd2', member: 'c', slug: 'two' })] })])
  expect(left.map((z) => z.title)).toEqual(['TEAM default', 'STREAM x', 'DISPATCHES'])
  expect(left[2].rows.map((r) => r.text.split(' ')[0]).sort()).toEqual(['one', 'two'])
  const none = secs([team({ members: [peer('c'), peer('a', { stream: 'x' })], dispatches: [dispatch({ member: 'a' })] })])
  expect(none.map((z) => z.section)).toEqual(['team', 'stream'])
  for (const z of [...left, ...none]) expect(z.rows.length > 0 || z.section === 'team').toBe(true)
})

test('G8 every member streamed: a title-only TEAM box, then the STREAM boxes', async () => {
  const x = secs([team({ members: [peer('a', { stream: 'x' }), peer('b', { stream: 'y' })], dispatches: [] })])
  expect(x.map((z) => z.title)).toEqual(['TEAM default', 'STREAM x', 'STREAM y'])
  expect(x[0].rows).toEqual([])
  expect(x.slice(1).every((z) => z.rows.length === 1)).toBe(true)
})

test('G9 a pipeline draws a HIERARCHY box with a row; no pipeline, no box; a team with nothing draws nothing of its own', async () => {
  const x = secs([team({ pipeline: { ...PIPE, waiting_on_user: 1 }, members: [peer('a')], dispatches: [] })])
  expect(x[0].section).toBe('hierarchy')
  expect(x[0].rows.map((r) => [r.icon, r.text])).toEqual([[null, 'round 2/3 · reviewer'], [null, '1 decision waiting for you']])
  expect(secs([team({ pipeline: { ...PIPE, items: [] }, members: [peer('a')], dispatches: [] })])[0].rows.map((r) => r.text)).toEqual(['Pipeline idle.'])
  expect(titles([team({ members: [peer('a')], dispatches: [] })])).toEqual(['TEAM default'])
  expect(secs([team({ members: [], dispatches: [] })]).map((z) => z.section)).toEqual(['none'])
})

test('I5 each team colors its streams from its own index; G6 a stream that is not a non-empty string is no stream', async () => {
  const x = secs([
    team({ team: 'one', members: [peer('a', { stream: 'x' }), peer('b', { stream: 'y' })], dispatches: [] }),
    team({ team: 'two', members: [peer('c', { stream: 'y' })], dispatches: [] }),
  ])
  expect(x.filter((z) => z.section === 'stream').map((z) => [z.title, z.color])).toEqual([['STREAM x', 'suggestion'], ['STREAM y', 'remember'], ['STREAM y', 'suggestion']])
  const odd = secs([team({ members: [peer('a', { stream: 42 }), peer('b', { stream: '' }), peer('c', { stream: null }), peer('d', { stream: ['x'] })], dispatches: [] })])
  expect(odd.map((z) => z.section)).toEqual(['team'])
  expect(odd[0].rows.length).toBe(4)
})

test('I1 icons and icon colors come from the one table; the text tone is unchanged', async () => {
  const members = [peer('w'), peer('i', { activity: 'idle' }), peer('b', { activity: 'blocked' }), peer('g', { live: false }), peer('u', { activity: 'thinking' })]
  const x = secs([team({ members, dispatches: [] })])[0].rows
  expect(x.map((r) => [r.icon, r.iconColor, r.tone])).toEqual([
    ['●', 'success', 'work'], ['○', null, 'idle'], ['■', 'warning', 'warn'], ['×', null, 'idle'], ['?', null, 'idle'],
  ])
  const sent = (state: string, i: number, extra: Record<string, unknown> = {}) => dispatch({ id: `d${i}`, slug: `s${i}`, member: 'zz', states: [{ at: at(-60000), state, ...extra }] })
  const ds = [sent('working', 0), sent('blocked', 1), sent('overdue', 2), sent('stalled', 3, { reason: 'no-report' }), sent('reported', 4)]
  const d = secs([team({ members: [], dispatches: ds })])[0].rows
  expect(d.map((r) => [r.text.split(' ')[0], r.icon, r.iconColor, r.tone]).sort()).toEqual([
    ['s0', '●', 'success', 'work'], ['s1', '■', 'warning', 'warn'], ['s2', '▲', 'error', 'bad'], ['s3', '▲', 'error', 'bad'], ['s4', '✓', 'success', 'ok'],
  ])
})

test('I2 every icon is one code point; I4 streams past the palette cycle with no undefined color; no box is a status color', async () => {
  for (const { glyph } of Object.values(STYLE.icon)) expect([...glyph].length).toBe(1)
  const x = secs([team({ members: ['a', 'b', 'c', 'd', 'e', 'f'].map((n) => peer(n, { stream: n })), dispatches: [] })])
  expect(x.filter((z) => z.section === 'stream').map((z) => z.color)).toEqual(['suggestion', 'remember', 'ide', 'planMode', 'suggestion', 'remember'])
  const status = ['success', 'warning', 'error']
  for (const z of secs([streamTeam({ pipeline: PIPE })])) expect(status.includes(z.color as string)).toBe(false)
  for (const c of [...Object.values(STYLE.box), ...STYLE.streams]) expect(status.includes(c as string)).toBe(false)
})

test('D1 from 40 columns every section is bordered and every row fits the inner width; D3 40 is the boundary', async () => {
  expect(BORDER_MIN).toBe(40)
  for (const columns of [40, 41, 60, 120]) {
    const x = secs([streamTeam({ pipeline: PIPE })], columns)
    expect(x.every((z) => z.bordered)).toBe(true)
    for (const z of x) {
      expect([...z.title].length).toBeLessThanOrEqual(columns - 2)
      for (const r of z.rows) expect(lineWidth(r)).toBeLessThanOrEqual(columns - 2)
    }
  }
  expect(secs([streamTeam()], 39).some((z) => z.bordered)).toBe(false)
})

test('D2 below 40 columns: no borders, lines fit, the icon and indent stay, and the last-tool part is dropped', async () => {
  const working = peer('builder', { stream: 'api', last_tool: 'Edit', last_tool_at: at(-12000), activity_at: at(-360000) })
  const wide = secs([team({ members: [working], dispatches: [dispatch({ member: 'builder' })] })], 120)
  expect(wide[1].rows[0].text).toContain(' · last Edit 12s')
  for (const columns of [39, 30, 24, 10]) {
    const x = secs([team({ members: [working, peer('lead')], dispatches: [dispatch({ member: 'builder', slug: 'fix-auth' })] })], columns)
    expect(x.some((z) => z.bordered)).toBe(false)
    const lines = x.flatMap((z) => z.rows)
    for (const r of lines) expect(lineWidth(r)).toBeLessThanOrEqual(columns)
    for (const z of x) expect([...z.title].length).toBeLessThanOrEqual(columns)
    expect(lines.some((r) => r.text.includes(' · last'))).toBe(false)
    expect(lines.every((r) => r.icon !== null)).toBe(true)
    expect(lines.some((r) => r.indent === 2)).toBe(true)
  }
  const x = secs([team({ members: [working], dispatches: [dispatch({ member: 'builder', slug: 'fix-auth' })] })], 30)
  expect(x[1].rows.map(line)).toEqual(['● builder working', '  ● fix-auth working'])
})

test('D4 no view, a malformed view, an empty pane and a view from before teams give one borderless section and never throw', async () => {
  const views: any[] = [null, undefined, 3, 'x', {}, { pane: 'x' }, { pane: [], teams: [] }, { pane: [{ row: 'text', tone: 'idle', text: 'older view' }] }, { pane: [], teams: 'x' }, { teams: [{ name: 'x', members: 3 }], pane: [] }]
  for (const view of views) {
    const x = paneSections(view, 60)
    expect(x.length).toBe(1)
    expect(x[0].bordered).toBe(false)
    expect(x[0].rows.length).toBeGreaterThan(0)
  }
  expect(paneSections({ pane: [{ row: 'text', tone: 'idle', text: 'older view' }] } as any, 60)[0].rows[0].text).toBe('older view')
  expect(paneSections(null, 60, 'no file')[0].rows[0].text).toBe('No hierarchy status here (no file).')
})

test('D5 the 60-column sample', async () => {
  const x = secs([streamTeam({ pipeline: PIPE })], 60)
  expect(x.map((z) => [z.title, z.color, z.rows.map(line)])).toEqual([
    ['HIERARCHY', 'claude', ['round 2/3 · reviewer']],
    ['TEAM default', 'permission', ['● lead · claude · peer · working']],
    ['STREAM api', 'suggestion', ['● builder · claude · peer · working', '● checker · claude · peer · working', '  ● fix-auth → builder | eta 5m | ██░░░░░░░░ 20% | 1:00 |…']],
    ['STREAM docs', 'remember', ['× writer · claude · peer · ended']],
    ['DISPATCHES', null, ['● free-ta… → lead | eta 5m | ██░░░░░░░░ 20% | 1:00 | work…']],
  ])
})

test('a focusable member row splits into the name (the click target) and the rest; a cut name is no target; others carry no focus', async () => {
  const mk = (over: Record<string, unknown>) => team({ members: [peer('builder', { focusable: true, ...over })], dispatches: [] })
  const row = (columns: number, over: Record<string, unknown> = {}) => secs([mk(over)], columns)[0].rows[0]
  expect([row(60).focus, row(60).text]).toEqual(['builder', ' · claude · peer · working'])
  expect([row(30).focus, row(30).text]).toEqual(['builder', ' working'])
  const long = 'a-very-long-member-name-here'
  expect(secs([team({ members: [peer(long, { focusable: true })], dispatches: [] })], 20)[0].rows[0].focus).toBe(null)
  for (const over of [{ focusable: false }, { focusable: 'true' }, { live: false }, { session_id: 'sess-orch' }]) expect(row(60, over).focus).toBe(null)
  expect(row(60, { session_id: 'someone-else' }).focus).toBe('builder')
  expect(secs([team({ members: [peer('builder')], dispatches: [] })])[0].rows[0].focus).toBe(null)
})

test('S13 a session listed in member_sessions draws no band, toast, Pane view or status entry, while its member is marked gone', async () => {
  const gone = team({ members: [member({ name: 'demo-architect', session_id: 'sess-orch', live: false, activity: 'idle' })], dispatches: [dispatch({ states: [{ at: at(-60000), state: 'stalled', reason: 'member-gone' }] })] })
  const text = docText({ member_sessions: ['sess-orch'], teams: [gone] }, { out: 1, blocked: 0, stalled: 1, tone: 'bad' })
  expect(statusText(parseDoc(text), ms(T0), 'sess-orch', true)).toBe(undefined)
  expect(viewModel(parseDoc(text), ms(T0), 'sess-orch')).toBe(null)
  expect(bandLine(viewModel(parseDoc(text), ms(T0), 'sess-orch'), 120)).toBe(null)
  expect(paneRows(viewModel(parseDoc(text), ms(T0), 'sess-orch'), 120, nullCause(parseDoc(text), ms(T0), 'sess-orch')).map((r) => r.text)).toEqual(['No hierarchy status here (member session).'])
  expect(statusText(parseDoc(text), ms(T0), 'some-other-session', true)).not.toBe(undefined)
})

// ---- toast_seconds
const MS = (...vals: unknown[]) => vals.map(toastMs)
test('T1 a missing or non-numeric value is the default', async () => {
  expect(MS(undefined, null, '', 'abc', NaN, Infinity, -Infinity, true, {})).toEqual(Array(9).fill(10000))
})
test('T2 ten is ten seconds', async () => { expect(MS(10, '10')).toEqual([10000, 10000]) })
test('T3 a number or a plain decimal string, trimmed, in range', async () => { expect(MS(30, ' 30 ', '30')).toEqual([30000, 30000, 30000]) })
test('T4 a fraction rounds to a whole second', async () => { expect(MS(2.4, 2.6)).toEqual([2000, 3000]) })
test('T5 below two seconds is two', async () => { expect(MS(1, '1', 1.4)).toEqual([2000, 2000, 2000]) })
test('T6 above 60 seconds is 60 (the host rejects a longer toast)', async () => { expect(MS(61, 121, 1e9, '120')).toEqual([60000, 60000, 60000, 60000]) })
test('T7 the result is 0 or a whole number of seconds from 2 to 60, for any input', async () => {
  const inputs: unknown[] = [undefined, null, '', 0, '0', -1, 0.4, 1, 2, 59.5, 120, 121, 1e300, -1e300, 'x', [], {}, [1], '12', ' 7.5 ', true, false, NaN]
  for (const v of inputs) {
    const ms = toastMs(v)
    expect(ms === 0 || (Number.isInteger(ms) && ms % 1000 === 0 && ms >= 2000 && ms <= 60000)).toBe(true)
  }
})
test('T8 exactly zero is off', async () => { expect(MS(0, '0', ' 0 ', '0.0', -0)).toEqual([0, 0, 0, 0, 0]) })
test('T9 any negative is the default, not off', async () => { expect(MS(-1, -5, '-30', -0.4, '-0.4')).toEqual(Array(5).fill(10000)) })
test('T10 a positive that rounds below two is two, not off', async () => { expect(MS(0.4, '0.4')).toEqual([2000, 2000]) })
test('T11 values Number() would coerce, or that are not plain decimals, are the default and never off', async () => {
  expect(MS('', '  ', false, [], [0], '1e3', '0x10', '10s', '+5', '.5', '5.', '1,5')).toEqual(Array(12).fill(10000))
})

// ---- teams owned by other orchestrators
const entryOf = (live: number, out: number, extra: Record<string, unknown> = {}) =>
  ({ at: T0, live, blocked: 0, out, overdue: 0, stalled: 0, text: `${live} live · ${out} out`, short: `${live}/${out}`, tone: out > 0 ? 'work' : 'idle', visible: true, ...extra })
const mine = team({ team: 'mine', members: [member({ name: 'mine-a', label: 'mine' })], dispatches: [] })
const theirs = team({
  team: 'theirs',
  members: [member({ name: 'their-a', label: 'theirs', activity: 'blocked', blocked_by: 'permission', activity_at: at(-1000) })],
  dispatches: [dispatch({ id: 'dx', slug: 'foreign-slug', label: 'theirs', member: 'their-a' }, 600000), dispatch({ id: 'dy', slug: 'foreign-done', label: 'theirs', member: 'their-a', reported_at: at(-1000), states: [{ at: at(-5000), state: 'reported' }] })],
})
const ownedDoc = (owners: unknown, over: Record<string, unknown> = {}) =>
  docText({ teams: [mine, theirs], owners, ...over }, { out: 3, stalled: 1, blocked: 1, live: 2, text: '2 live · 3 out · 1 stalled · 1 blocked', tone: 'bad' })
const OWNERS = [
  { sessions: ['sess-orch'], teams: ['mine'], timeline: [entryOf(1, 0)] },
  { sessions: ['sess-other'], teams: ['theirs'], timeline: [entryOf(1, 3, { stalled: 1, blocked: 1, text: '1 live · 3 out · 1 stalled · 1 blocked', tone: 'bad' })] },
]
const everything = (v: ReturnType<typeof viewModel>) => JSON.stringify([v, v === null ? null : paneSections(v, 120)])

test('another orchestrator\'s team is hidden everywhere: Pane, band, toasts and status text', async () => {
  const doc = parseDoc(ownedDoc(OWNERS))
  const view = viewModel(doc, ms(T0), 'sess-orch')
  expect(view).not.toBe(null)
  expect(statusText(doc, ms(T0), 'sess-orch', true)).toBe('1 live · 0 out')
  expect(bandLine(view, 200)?.text).toBe('1 team · 1 of 1 agents up · nothing needs you · 0 in flight')
  expect(view!.toasts).toEqual([])
  for (const gone of ['their-a', 'theirs', 'foreign-slug', 'foreign-done', 'dx', 'dy']) expect(everything(view)).not.toContain(gone)
  expect(everything(view)).toContain('mine-a')
  // the other viewer sees theirs and not mine
  const other = viewModel(doc, ms(T0), 'sess-other')
  expect(everything(other)).toContain('their-a')
  expect(everything(other)).not.toContain('mine-a')
  expect(other!.toasts.map((t) => t.key).sort()).toEqual([`blocked:their-a:${at(-1000)}`, 'reported:dy', 'stalled:dx'])
})

test('a viewer that owns nothing gets no status text, band, Pane rows or toasts', async () => {
  const doc = parseDoc(ownedDoc(OWNERS))
  expect(statusText(doc, ms(T0), 'sess-nobody', true)).toBe(undefined)
  const view = viewModel(doc, ms(T0), 'sess-nobody')
  expect(view).toBe(null)
  expect(bandLine(view, 200)).toBe(null)
  expect(paneRows(view, 120, nullCause(doc, ms(T0), 'sess-nobody'))).toEqual([{ text: 'No hierarchy status here (no team owned by this session).', tone: 'idle' }])
  expect(paneSections(view, 120).map((s) => s.section)).toEqual(['none'])
})

test('a viewer that owns two teams sees both, with the team headings', async () => {
  const doc = parseDoc(ownedDoc([{ sessions: ['sess-orch'], teams: ['mine', 'theirs'], timeline: [entryOf(2, 3)] }, { sessions: ['sess-other'], teams: [], timeline: [entryOf(0, 0)] }]))
  const view = viewModel(doc, ms(T0), 'sess-orch')
  expect(paneSections(view, 120).filter((s) => s.section === 'team').map((s) => s.title)).toEqual(['TEAM mine', 'TEAM theirs'])
  expect(statusText(doc, ms(T0), 'sess-orch', true)).toBe('2 live · 3 out')
})

test('a member session that leads no team gets no view', async () => {
  const doc = parseDoc(ownedDoc(OWNERS, { member_sessions: ['sess-m'] }))
  expect(viewModel(doc, ms(T0), 'sess-m')).toBe(null)
  expect(nullCause(doc, ms(T0), 'sess-m')).toBe('member session')
})

test('ah2-367: a member session an owner entry lists (a Sub-Orchestrator) gets the view of the team it leads', async () => {
  const doc = parseDoc(ownedDoc(OWNERS, { member_sessions: ['sess-orch'] }))
  expect(paneSections(viewModel(doc, ms(T0), 'sess-orch'), 120).filter((s) => s.section === 'team').map((s) => s.title)).toEqual(['TEAM mine'])
})

test('owners that is not an array, or holds only unusable entries, gives no view; a document without owners is pool-wide', async () => {
  const bad = [{ sessions: 'sess-orch', teams: ['mine'], timeline: [entryOf(1, 0)] }, { sessions: ['sess-orch'], teams: [3], timeline: [entryOf(1, 0)] }, { sessions: ['sess-orch'], teams: ['mine'], timeline: [] }, { sessions: ['sess-orch'], teams: ['mine'], timeline: [{ at: 'x' }] }, 7, null]
  for (const owners of ['sess-orch', {}, 7, null, [], bad]) expect(viewModel(parseDoc(ownedDoc(owners)), ms(T0), 'sess-orch')).toBe(null)
  const legacy = parseDoc(ownedDoc(OWNERS).replace(/"owners":.*\]\}\]/, '"x":1'))
  expect(legacy!.owners).toBe(null)
  expect(everything(viewModel(legacy, ms(T0), 'sess-orch'))).toContain('their-a')
})

test('the first usable owner entry listing the session wins; an invisible entry for the viewer\'s group gives no view', async () => {
  const dup = [{ sessions: ['sess-orch'], teams: ['theirs'], timeline: [entryOf(1, 3)] }, { sessions: ['sess-orch'], teams: ['mine'], timeline: [entryOf(1, 0)] }]
  expect(everything(viewModel(parseDoc(ownedDoc(dup)), ms(T0), 'sess-orch'))).toContain('their-a')
  const hidden = [{ sessions: ['sess-orch'], teams: ['mine'], timeline: [entryOf(0, 0, { visible: false })] }]
  expect(viewModel(parseDoc(ownedDoc(hidden)), ms(T0), 'sess-orch')).toBe(null)
  expect(scope(parseDoc(ownedDoc(OWNERS)), 'sess-nobody')).toBe(null)
})

test('keepSeen on a scoped document drops the keys of another orchestrator\'s teams', async () => {
  const doc = parseDoc(ownedDoc(OWNERS))
  const seen = ['stalled:dx', `blocked:their-a:${at(-1000)}`, `blocked:mine-a:${at(-1000)}`]
  expect(keepSeen(seen, scope(doc, 'sess-orch')!, ms(T0))).toEqual([`blocked:mine-a:${at(-1000)}`])
})

// ---- Dismiss: the owned mark, the per-team summary, the button layout and the dialogs' text.
const wdoc = (edit: (d: any) => void = () => {}) => { const d = JSON.parse(fixtures.work); edit(d); return JSON.stringify(d) }
const DNOWMS = ms(vectors.work.now)
const dview = (text: string, id = 'sess-orch') => viewModel(parseDoc(text), DNOWMS, id)

test('the view is marked owned for a document with owners that lists the viewer, and not for a legacy document', async () => {
  expect(dview(wdoc())!.owned).toBe(true)
  expect(dview(wdoc((d) => { delete d.owners }))!.owned).toBe(false)
  expect(dview(wdoc(), 'someone-else')).toBe(null)
  expect(dview(wdoc((d) => { d.member_sessions = ['sess-m'] }), 'sess-m')).toBe(null)
})

test('a team carries whether it is the default team, from its key and not its shown name', async () => {
  const named = dview(wdoc((d) => { d.teams[0].team = 'default'; d.owners[0].teams = ['default'] }))!.teams[0]
  const dflt = dview(wdoc())!.teams[0]
  expect([dflt.name, dflt.isDefault, named.name, named.isDefault]).toEqual(['default', true, 'default', false])
})

test('the summary: busy is a live working member, blocked a live blocked one, a gone member neither', async () => {
  const sum = (activity: string, live: unknown = true) => dview(wdoc((d) => { d.teams[0].members[0].activity = activity; d.teams[0].members[0].live = live }))!.teams[0].summary
  expect(sum('working')).toMatchObject({ members: 1, busy: ['demo-architect'], blocked: [] })
  expect(sum('blocked')).toMatchObject({ busy: [], blocked: ['demo-architect'] })
  expect(sum('idle')).toMatchObject({ busy: [], blocked: [] })
  expect(sum('working', false)).toMatchObject({ busy: [], blocked: [] })
})

test('the summary: working, overdue, stalled and blocked dispatches are open; reported and expired are not', async () => {
  const open = (state: string) => dview(wdoc((d) => { d.teams[0].dispatches[0].states = [{ at: '2026-01-01T11:59:00.000Z', state }] }))!.teams[0].summary.open
  for (const [state, word] of [['working', 'working'], ['overdue', 'late'], ['stalled', 'stuck'], ['blocked', 'needs you']]) expect(open(state)).toEqual([{ member: 'demo-architect', state: word }])
  for (const state of ['reported', 'expired']) expect(open(state)).toEqual([])
})

test('the summary reads only the viewer\'s own teams: a foreign team\'s members never appear', async () => {
  const text = wdoc((d) => {
    const other = JSON.parse(JSON.stringify(d.teams[0]))
    other.team = 'theirs'
    other.members[0].name = 'their-member'
    d.teams.push(other)
    d.owners.push({ sessions: ['someone-else'], teams: ['theirs'], timeline: d.owners[0].timeline })
  })
  const view = dview(text)!
  expect(view.teams.map((t) => t.name)).toEqual(['default'])
  expect(JSON.stringify(view)).not.toContain('their-member')
})

test('ownedTeams is empty unless the view is marked owned, and drops a team whose fields are of the wrong type', async () => {
  const view: any = dview(wdoc())
  expect(ownedTeams(view).map((t) => t.name)).toEqual(['default'])
  for (const v of [null, undefined, {}, 'x', { ...view, owned: false }, { ...view, owned: 'true' }, { ...view, teams: 'x' }]) expect(ownedTeams(v)).toEqual([])
  expect(ownedTeams({ owned: true, teams: [{ name: 3, isDefault: true, summary: view.teams[0].summary }, { name: 'a', isDefault: 'no', summary: view.teams[0].summary }, { name: 'a', isDefault: true }, null] })).toEqual([])
  expect(ownedTeams({ owned: true, teams: [{ name: 'a', isDefault: false, summary: { members: 2, busy: [1], blocked: 'x', open: [{ member: 'm' }, { member: 'm', state: 's' }] } }] })[0].summary).toEqual({ members: 2, busy: [], blocked: [], open: [{ member: 'm', state: 's' }] })
})

test('the Dismiss button needs 52 columns, an owned view and a team; the band text gives up room for each button', async () => {
  const view = dview(wdoc())!
  expect(DISMISS_BUTTON.minColumns).toBe(PANE_BUTTON.minColumns + 12)
  expect(bandButtons(view, 39)).toEqual({ pane: false, dismiss: false, textColumns: 39 })
  expect(bandButtons(view, 40)).toEqual({ pane: true, dismiss: false, textColumns: 40 - PANE_BUTTON.width - PANE_BUTTON.gap })
  expect(bandButtons(view, 51).dismiss).toBe(false)
  expect(bandButtons(view, 52)).toEqual({ pane: true, dismiss: true, textColumns: 52 - PANE_BUTTON.width - PANE_BUTTON.gap - DISMISS_BUTTON.width - DISMISS_BUTTON.gap })
  expect(bandButtons(dview(wdoc((d) => { delete d.owners }))!, 120).dismiss).toBe(false)
  expect(bandButtons({ ...view, teams: [] }, 120).dismiss).toBe(false)
  expect(bandButtons(null, 120)).toEqual({ pane: true, dismiss: false, textColumns: 120 - PANE_BUTTON.width - PANE_BUTTON.gap })
})

test('team labels: the default team is @default only when another team shows the same name', async () => {
  expect(teamLabels([{ name: 'default', isDefault: true }])).toEqual(['default'])
  expect(teamLabels([{ name: 'default', isDefault: true }, { name: 'beta', isDefault: false }])).toEqual(['default', 'beta'])
  expect(teamLabels([{ name: 'default', isDefault: true }, { name: 'default', isDefault: false }])).toEqual(['@default', 'default'])
})

const SUM = { members: 2, busy: [] as string[], blocked: [] as string[], open: [] as { member: string; state: string }[] }
const PLAN = { token: '0123456789abcdef', close: ['a', 'b'], warnings: [] as string[], strays: [] as string[], notClosable: [] as string[] }

test('the confirm question: the idle wording, the in-flight wording, and the extra lines only when present', async () => {
  expect(dismissQuestion('alpha', SUM, PLAN)).toBe('Dismiss team alpha? This closes 2 member sessions: a, b. No member is busy. No open dispatches. Worktrees, branches and messages are left as they are.')
  const busy = { ...SUM, busy: ['a'], blocked: ['b'], open: [{ member: 'a', state: 'overdue' }, { member: 'b', state: 'stalled' }] }
  const q = dismissQuestion('alpha', busy, { ...PLAN, close: ['a'], warnings: ['w1.'], strays: ['s1'], notClosable: ['p1'] })
  expect(q).toBe('Dismiss team alpha? This closes 1 member session: a. BUSY now: a. Blocked: b. Open dispatches: 2 (a overdue, b stalled). Their in-flight work will be lost. w1. Strays: s1. Not closed (no pane): p1. Worktrees, branches and messages are left as they are.')
  expect(dismissQuestion('alpha', { ...SUM, open: [{ member: 'a', state: 'working' }] }, PLAN)).toContain('Their in-flight work will be lost.')
  expect(dismissQuestion('alpha', SUM, PLAN)).not.toContain('in-flight')
})

test('the second question names busy members and the dispatch count, leaving out a clause that is empty', async () => {
  expect(anywayQuestion('alpha', { ...SUM, busy: ['a', 'b'], open: [{ member: 'a', state: 'working' }, { member: 'b', state: 'blocked' }] })).toBe('alpha has work in flight: a, b busy; 2 open dispatches. Close it anyway and lose that work?')
  expect(anywayQuestion('alpha', { ...SUM, busy: ['a'] })).toBe('alpha has work in flight: a busy. Close it anyway and lose that work?')
  expect(anywayQuestion('alpha', { ...SUM, open: [{ member: 'a', state: 'working' }] })).toBe('alpha has work in flight: 1 open dispatch. Close it anyway and lose that work?')
})

test('in flight means a busy member or an open dispatch, and blocked alone is not', async () => {
  expect([inFlight(SUM), inFlight({ ...SUM, blocked: ['a'] }), inFlight({ ...SUM, busy: ['a'] }), inFlight({ ...SUM, open: [{ member: 'a', state: 'working' }] })]).toEqual([false, false, true, true])
})

test('readPlan: a plan from a team file reads; every other output is an error or nothing to do', async () => {
  const ok = { close: [{ role: 'architect', name: 'n1' }, { role: 'reviewer', name: null }], close_token: '0123456789abcdef', team_files: ['/t'], warnings: ['w'], strays: ['s'], not_closable: [{ name: 'p' }] }
  expect(readPlan(JSON.stringify(ok))).toEqual({ kind: 'plan', plan: { token: '0123456789abcdef', close: ['n1', 'reviewer'], warnings: ['w'], strays: ['s'], notClosable: ['p'] } })
  expect(readPlan(JSON.stringify({ disbanded: false, reason: 'gone' }))).toEqual({ kind: 'nothing', reason: 'gone' })
  for (const bad of [{ ...ok, source: 'peers' }, { ...ok, team_files: [] }, { ...ok, team_files: 'x' }, { ...ok, team_files: undefined }]) expect(readPlan(JSON.stringify(bad))).toEqual({ kind: 'failed', reason: 'no team file' })
  for (const bad of ['', 'nope', '[]', '"x"', JSON.stringify({ ...ok, close_token: 'ABCDEF0123456789' }), JSON.stringify({ ...ok, close_token: '0123456789abcde' }), JSON.stringify({ ...ok, close_token: '0123456789abcdef0' }), JSON.stringify({ ...ok, close: 'x' }), JSON.stringify({ ...ok, close_token: undefined }), undefined, 5]) expect(readPlan(bad)).toEqual({ kind: 'failed', reason: 'plan failed' })
})

test('failReason: the first non-empty line without the roster prefix, control characters stripped, cut to 120', async () => {
  expect(failReason('roster.mjs: bad thing\nsecond', 'x')).toBe('bad thing')
  expect(failReason('\n\n  spaced  \n', 'x')).toBe('spaced')
  expect(failReason('a\x1b[31mb\x07c', 'x')).toBe('a[31mbc')
  expect(failReason('y'.repeat(300), 'x').length).toBe(120)
  for (const none of ['', '  \n', undefined, null, 3]) expect(failReason(none, 'fallback')).toBe('fallback')
})

test('closeToast: dismissed only for exit 0 with closed true and nothing still live', async () => {
  const j = (o: unknown) => JSON.stringify(o)
  expect(closeToast('t', 0, j({ closed: true, still_live: [] }), '')).toBe('Dismissed t.')
  expect(closeToast('t', 0, j({ closed: true }), '')).toBe('Dismissed t.')
  expect(closeToast('t', 0, j({ closed: false, still_live: ['a', 'b'] }), '')).toBe('t: not all sessions closed (a, b).')
  expect(closeToast('t', 0, j({ closed: false, kept: [{ name: 'k' }] }), '')).toBe('t: not all sessions closed (k).')
  expect(closeToast('t', 0, j({ closed: false }), '')).toBe('t: not all sessions closed.')
  expect(closeToast('t', 0, j({ closed: true, still_live: ['z'] }), '')).toBe('t: not all sessions closed (z).')
  expect(closeToast('t', 2, '', 'roster.mjs: disband --close: --plan-token does not match the current close plan (x)')).toBe('t changed since the check; press Dismiss again.')
  expect(closeToast('t', 2, '', 'roster.mjs: other')).toBe('Could not dismiss t: other')
  expect(closeToast('t', 1, j({ closed: true }), '')).toBe('Could not dismiss t: close failed')
  expect(closeToast('t', 0, 'not json', '')).toBe('Could not dismiss t: close failed')
  expect(closeToast('t', 0, '[]', '')).toBe('Could not dismiss t: close failed')
})

// The shared cases recorded from the plugin's `mainCheckoutRoot`: the mod's mirror gives the same answer for each.
for (const v of mainVectors) {
  test(`mainCheckoutRoot mirrors the plugin's: ${v.name}`, async () => {
    const io = {
      kind: async (p: string) => (p in v.files ? 'file' : v.dirs.includes(p) ? 'directory' : null),
      read: async (p: string) => { if (!(p in v.files)) throw new Error('ENOENT'); return v.files[p] },
    }
    expect(await mainCheckoutRoot(v.root, io)).toBe(v.expected)
  })
}

test('mainCheckoutRoot: the vectors include both a root and a null answer, so the loop cannot pass vacuously', () => {
  expect(mainVectors.some((v) => v.expected !== null)).toBe(true)
  expect(mainVectors.some((v) => v.expected === null)).toBe(true)
})

test('mainCheckoutRoot: a non-POSIX root resolves no main checkout', async () => {
  expect(await mainCheckoutRoot('C:\\work\\wt', { kind: async () => 'file', read: async () => 'gitdir: C:\\main\\.git\\worktrees\\wt' })).toBe(null)
})

// ---- the compact prompt, the mail record and the Mail section
const DUE = { v: 1, ids: ['e1'], prompt: 'ah mail for imp@tm (1):\n- note from user: hi\n[ah-entry e1]', idle: true, state: 'S', acked: [], daemon: null }

test('readDueKeepsAStringCompactAndReadsAnyOtherCompactAsNone', () => {
  const compact = 'ah mail for imp@tm (1):\n- note from user\n[ah-entry e1]'
  expect(readDue({ ...DUE, compact })?.compact).toBe(compact)
  expect(readDue({ ...DUE })?.compact).toBeNull()
  expect(readDue({ ...DUE, compact: null })?.compact).toBeNull()
  expect(readDue({ ...DUE, compact: '' })?.compact).toBeNull()
  expect(readDue({ ...DUE, compact: 7 })?.compact).toBeNull()
  expect(readDue({ ...DUE, compact: { x: 1 } })?.compact).toBeNull()
  // a wrong-typed compact is no short form, never an unreadable answer
  expect(readDue({ ...DUE, compact: 7 })).not.toBeNull()
})

test('heldTextShowsAHeldSessionsUnreadAndClearsWhenHeldClears', () => {
  const held = readDue({ ...DUE, ids: [], prompt: null, unread: 3 })
  expect(held?.unread).toBe(3)
  expect(heldText(true, held?.unread)).toBe('ah: stopped with 3 unread; type anything to resume')
  // negatives: held cleared, nothing unread, no count, a malformed count
  expect(heldText(false, 3)).toBeUndefined()
  expect(heldText(true, 0)).toBeUndefined()
  expect(heldText(true, readDue({ ...DUE })?.unread)).toBeUndefined()
  expect(readDue({ ...DUE, unread: 'x' })?.unread).toBeUndefined()
})

test('mailCutKeepsATextWithinTheBoundAndCutsALongOneByCodePointWithTheInboxLine', () => {
  const addr = 'imp@tm'
  expect(mailCut('short', addr)).toBe('short')
  const exact = 'a'.repeat(MAIL_TEXT_BYTES)
  expect(mailCut(exact, addr)).toBe(exact)
  const long = 'a'.repeat(MAIL_TEXT_BYTES + 1)
  const cutLong = mailCut(long, addr)
  expect(cutLong.endsWith('… (cut; ah msg inbox --all --address imp@tm)')).toBe(true)
  expect(new TextEncoder().encode(cutLong).length).toBeLessThanOrEqual(MAIL_TEXT_BYTES)
  // a four-byte character is never split at the cut
  const emoji = '😀'.repeat(MAIL_TEXT_BYTES)
  const cutEmoji = mailCut(emoji, addr)
  expect(new TextEncoder().encode(cutEmoji).length).toBeLessThanOrEqual(MAIL_TEXT_BYTES)
  expect(cutEmoji.includes('\ufffd')).toBe(false)
  expect([...cutEmoji.split('…')[0]].every((c) => c === '😀')).toBe(true)
})

test('mailRecordsKeepsTheNewestTenAndDropsWhatIsNotARecord', () => {
  let held: unknown = ['junk', 3, { at: 'x' }]
  for (let i = 0; i < MAIL_KEEP + 2; i++) held = mailRecords(held, mailRecord(i, { ids: ['e' + i], prompt: 'ah mail for imp@tm (1):\n- note from u\n[ah-entry e' + i + ']' }, 'delivered'))
  const list = held as { at: number }[]
  expect(list.length).toBe(MAIL_KEEP)
  expect(list[0].at).toBe(MAIL_KEEP + 1)
  expect(list[MAIL_KEEP - 1].at).toBe(2)
  expect(mailRecord(1, { ids: ['a'], prompt: 'ah mail for imp@tm (1):\n- x' }, 'not delivered').address).toBe('imp@tm')
})

test('mailToastNamesTheFirstSenderAndTheCommandThatShowsTheFullText', () => {
  const one = 'ah mail for imp@tm (1):\n- brief from orchestrator@tm\n[ah-entry e1]'
  expect(mailToast(one, 1)).toBe('Mail from orchestrator@tm (brief). Full text: /wrangl-hierarchy-pane')
  expect(mailToast('ah mail for imp@tm (2):\n- report from a@tm\n- note from b@tm\n[ah-entry e1,e2]', 2)).toBe('2 mails from a@tm and others. Full text: /wrangl-hierarchy-pane')
  expect(mailToast('ah mail for imp@tm (1):\n- note from user: hello there\n[ah-entry e1]', 1)).toBe('Mail from user (note). Full text: /wrangl-hierarchy-pane')
  expect(mailUnsavedToast(one)).toBe('Mail text could not be saved for the pane. Full text: ah msg inbox --all --address imp@tm')
  expect(mailRefusedToast(one)).toBe('Mail for imp@tm was not delivered; it will be offered again.')
})

test('theToastsDrawControlCharactersAsPicturesNotRawEscapes', () => {
  const esc = String.fromCharCode(27)
  const prompt = `ah mail for imp@tm (1):\n- note from evil${esc}[31m@tm\n[ah-entry e1]`
  for (const t of [mailToast(prompt, 1), mailToast(prompt, 2), mailUnsavedToast(prompt), mailRefusedToast(prompt)]) expect(t.includes(esc)).toBe(false)
  expect(mailToast(prompt, 1)).toContain('evil␛[31m@tm')
  const kindEsc = `ah mail for imp@tm (1):\n- no${esc}te from u\n[ah-entry e1]`
  expect(mailToast(kindEsc, 1)).toContain('(no␛te)')
})

test('aSenderWithABareColonIsTheSenderOfTheToast', () => {
  expect(mailToast('ah mail for imp@tm (1):\n- note from a:b\n[ah-entry e1]', 1)).toBe('Mail from a:b (note). Full text: /wrangl-hierarchy-pane')
})

test('theMailSectionHasItsEmptyRecordsAndUnavailableStatesAndAlwaysTheInboxLine', () => {
  const inbox = 'Older or earlier-session mail: ah msg inbox --all --address '
  const empty = mailSection([], null, 80)
  expect(empty.title).toBe('Mail (newest first)')
  expect(empty.rows.map((r) => r.text)).toEqual(['No mail this session.', inbox + '<address>'])
  const rec = { at: Date.UTC(2026, 0, 1, 12, 0, 0), ids: ['e1', 'e2'], address: 'imp@tm', text: 'ah mail for imp@tm (2):\n- a\n- b', state: 'delivered' as const }
  const some = mailSection([rec], null, 80)
  expect(some.rows.map((r) => r.text).slice(1)).toEqual(['ah mail for imp@tm (2):', '- a', '- b', inbox + 'imp@tm'])
  expect(some.rows[0].text.endsWith(' · delivered · e1,e2')).toBe(true)
  expect(some.rows[0].tone).toBe('idle')
  for (const bad of [mailSection([], 'state write failed', 120), mailSection({ not: 'a list' }, null, 120)]) {
    expect(bad.rows[0].text.startsWith('Mail text unavailable (')).toBe(true)
    expect(bad.rows[0].text.endsWith('). Run: ah msg inbox --all --address <address>')).toBe(true)
    expect(bad.rows[bad.rows.length - 1].text).toBe(inbox + '<address>')
  }
  expect(mailSection([rec], 'state read failed', 120).rows[0].text).toBe('Mail text unavailable (state read failed). Run: ah msg inbox --all --address imp@tm')
})

test('theMailSectionDrawsControlCharactersAsPicturesAndCutsEachLineToTheWidth', () => {
  const rec = { at: 0, ids: ['e1'], address: 'imp@tm', text: 'ah mail for imp@tm (1):\n- note from x: \u001b[31mred\u001b[0m\t' + 'z'.repeat(200), state: 'delivered' as const }
  const sec = mailSection([rec], null, 40)
  const texts = sec.rows.map((r) => r.text)
  expect(texts.some((t) => t.includes('\u001b'))).toBe(false)
  expect(texts.some((t) => t.includes('␛'))).toBe(true)
  expect(sec.bordered).toBe(true)
  expect(Math.max(...texts.map((t) => [...t].length))).toBeLessThanOrEqual(38)
  expect(mailSection([rec], null, 39).bordered).toBe(false)
})

// ---- wrapping in the Mail section
const rec = (text: string, address = 'imp@tm') => ({ at: Date.UTC(2026, 0, 1, 12, 0, 0), ids: ['e1'], address, text, state: 'delivered' as const })
const bodyRows = (sec: { rows: { text: string }[] }) => {
  const texts = sec.rows.map((r) => r.text)
  return texts.slice(1, texts.findIndex((t) => t.startsWith('Older or earlier-session mail:')))
}

test('aLongOneLineNoteIsWrappedWithEveryCharacterPresentInOrderAndNoEllipsis', () => {
  const words = Array.from({ length: 60 }, (_, i) => 'w' + String(i).padStart(3, '0')).join(' ')
  expect(words.length).toBeGreaterThan(290)
  const rows = bodyRows(mailSection([rec(words)], null, 40))
  expect(rows.length).toBeGreaterThan(5)
  expect(rows.every((r) => [...r].length <= 38)).toBe(true)
  expect(rows.join(' ')).toBe(words)
  expect(rows.join('').includes('…')).toBe(false)
})

test('aPathWithNoSpaceIsSplitAtTheWidthAndTheRowsJoinedAreThePath', () => {
  const path = '/abs/' + 'dir/'.repeat(37) + 'file--request.md'
  expect([...path].length).toBeGreaterThan(150)
  const rows = bodyRows(mailSection([rec(path)], null, 40))
  expect(rows.length).toBeGreaterThan(3)
  expect(rows.every((r) => [...r].length <= 38)).toBe(true)
  expect(rows.join('')).toBe(path)
})

test('aFourKilobyteRecordAtFortyColumnsEndsInTheMorePointerAtSixtyRows', () => {
  const text = mailRecord(0, { ids: ['e1'], prompt: 'ah mail for imp@tm (1):\n- note from u: ' + 'lorem ipsum '.repeat(500) }, 'delivered').text
  const rows = bodyRows(mailSection([rec(text)], null, 40))
  expect(rows.length).toBe(MAIL_ROWS)
  expect(/^… \d+ more rows: ah msg inbox --all --address imp@tm$/.test(rows[MAIL_ROWS - 1])).toBe(true)
  expect(rows.slice(0, MAIL_ROWS - 1).some((r) => r.includes('…'))).toBe(false)
})

test('anAstralCharacterAtTheWidthBoundaryIsNeverSplit', () => {
  const rows = wrapRows('a'.repeat(9) + '😀'.repeat(5) + 'b'.repeat(7), 10)
  expect(rows.join('')).toBe('a'.repeat(9) + '😀'.repeat(5) + 'b'.repeat(7))
  for (const r of rows) {
    expect([...r].length).toBeLessThanOrEqual(10)
    expect(/[\ud800-\udbff]$/.test(r) || /^[\udc00-\udfff]/.test(r)).toBe(false)
  }
})

test('wrapRowsDropsOneSpaceAtABreakKeepsLeadingSpacesAndKeepsAnEmptyLine', () => {
  expect(wrapRows('aaa bbb ccc', 7)).toEqual(['aaa bbb', 'ccc'])
  expect(wrapRows('    indented text here', 12)).toEqual(['    indented', 'text here'])
  expect(wrapRows('', 10)).toEqual([''])
  expect(wrapRows('short', 10)).toEqual(['short'])
})

test('wrapRowsAtWidthOneAndTwoLosesNothingAndNeverExceedsTheWidth', () => {
  expect(wrapRows('ab c', 1)).toEqual(['a', 'b', 'c'])
  expect(wrapRows('ab cd', 2)).toEqual(['ab', 'cd'])
  for (const w of [1, 2]) {
    const rows = wrapRows('abc de fgh', w)
    expect(rows.every((r) => [...r].length <= w)).toBe(true)
    expect(rows.join('').replace(/ /g, '')).toBe('abcdefgh')
  }
})

test('twoSpacesAtABreakDropOneAndTheSecondStartsTheNextRow', () => {
  expect(wrapRows('aaa  bbb', 3)).toEqual(['aaa', ' bb', 'b'])
  expect(wrapRows('aaa  bbb', 4)).toEqual(['aaa ', 'bbb'])
  expect(wrapRows('aaa  bbb', 5)).toEqual(['aaa ', 'bbb'])
})

// The rows joined, with the one space dropped at a space break put back, are the line.
const unwrapped = (rows: string[], line: string): string => {
  let out = ''
  let at = 0
  for (const [i, r] of rows.entries()) {
    expect(line.startsWith(r, at)).toBe(true)
    out += r
    at += r.length
    if (i < rows.length - 1 && line[at] === ' ') { out += ' '; at++ }
  }
  return out
}

test('wrapRowsBreaksAfterAPunctuationCharacterWhenNoSpaceFits', () => {
  expect(wrapRows('ah2-tm3-reviewer@ah2-tm3:', 12)).toEqual(['ah2-tm3-', 'reviewer@', 'ah2-tm3:'])
  expect(wrapRows('/Users/someone1/git/repos/agent-tools-extras', 20)).toEqual(['/Users/someone1/git/', 'repos/agent-tools-', 'extras'])
  expect(wrapRows('a,b.c:d@e-f/gggggg', 6)).toEqual(['a,b.c:', 'd@e-f/', 'gggggg'])
})

test('wrapRowsPrefersASpaceToAPunctuationCharacter', () => {
  expect(wrapRows('ab-cd ef-gh', 8)).toEqual(['ab-cd', 'ef-gh'])
  expect(wrapRows('x /a/b/c/d', 6)).toEqual(['x', '/a/b/', 'c/d'])
})

test('negative: wrapRowsSplitsARunWithNoBreakCharacterAtTheWidth', () => {
  expect(wrapRows('a'.repeat(30), 12)).toEqual(['a'.repeat(12), 'a'.repeat(12), 'a'.repeat(6)])
})

test('wrapRowsLosesNothingAndNeverExceedsTheWidth', () => {
  const corpus = ['ah2-tm3-reviewer@ah2-tm3:', '/a/b/c/d/e/f/g/h', 'one two  three', '  lead-ing /x', 'zzzzzzzzzzzzzzzzzzzz', 'a-b-c-d-e-f-g-h-i-j', '😀😀😀-😀😀😀/😀😀😀', '']
  for (const line of corpus) {
    for (const w of [1, 2, 3, 5, 8, 12, 40]) {
      const rows = wrapRows(line, w)
      expect(rows.every((r) => [...r].length <= w)).toBe(true)
      expect(rows.join('').replace(/ /g, '')).toBe(line.replace(/ /g, ''))
      if (w >= 3) expect(unwrapped(rows, line)).toBe(line)
    }
  }
})

test('paneRows: the no-status line wraps and keeps every word, with no cut mark', () => {
  const rows = paneRows(null, 28, 'no file')
  expect(rows.length).toBeGreaterThan(1)
  expect(rows.every((r) => r.tone === 'idle' && [...r.text].length <= 28 && !r.text.includes('…'))).toBe(true)
  expect(rows.map((r) => r.text).join(' ')).toBe('No hierarchy status here (no file).')
})

test('paneRows: the empty-team line wraps', () => {
  const rows = paneRows({ pane: [] } as any, 12)
  expect(rows.map((r) => r.text)).toEqual(['No live team', 'in the', 'status file.'])
})

test('negative: paneRows still cuts a member row to one row with the cut mark', () => {
  const drawn = paneRows(viewOf([team({ members: [member({ name: 'a-very-long-member-name-that-cannot-fit' })] })], {}, T0), 20).filter((r) => r.text.startsWith('a-very-l'))
  expect(drawn.length).toBe(1)
  expect(drawn[0].text.includes('…')).toBe(true)
  expect([...drawn[0].text].length).toBeLessThanOrEqual(20)
})
test('negative: aHeldRecordFromBeforeStillCarryingFilesDrawsItsPlainLine', () => {
  const held = [{ ...rec('ah mail for imp@tm (1):\n- note from u'), files: [{ path: '/m/a.md', mtime: 5 }] }]
  const dated = mailSection(held, null, 80).rows.map((r) => r.text).filter((t) => t.includes(' · delivered · '))
  expect(dated[0].endsWith('· delivered · e1')).toBe(true)
})

test('thePaneDrawsTheShowCommandOfAnIdToken', () => {
  const text = 'ah mail for imp@tm (1):\n- brief from orchestrator@tm: [hierarchy-msg 20261103-005522-12h1] read it with `ah msg show 20261103-005522-12h1`'
  const rows = mailSection([mailRecord(0, { ids: ['e1'], prompt: text }, 'delivered')], null, 200).rows.map((r) => r.text)
  expect(rows.some((r) => r.includes('ah msg show 20261103-005522-12h1'))).toBe(true)
})

test('negative: aMailRecordKeepsNoFiles', () => {
  expect('files' in mailRecord(0, { ids: ['e1'], prompt: 'ah mail for imp@tm (1):\n- a: [hierarchy-msg /m/a.md]' }, 'delivered')).toBe(false)
})

test('S1 the status table is the shared status language: same glyph, word, tone and rank per status, same mapping from raw states', async () => {
  const color: Record<string, string | null> = { bad: 'error', warn: 'warning', work: 'success', ok: 'success', idle: null }
  const want = Object.fromEntries(Object.entries(statusLanguage.states).map(([k, v]) => [k, { glyph: v.glyph, color: color[v.tone], word: v.word, tone: v.tone, rank: v.rank }]))
  expect(STYLE.icon).toEqual(want)
  expect(STYLE.from).toEqual(statusLanguage.from)
  expect(STYLE.tone).toEqual({ bad: color.bad, warn: color.warn, ok: color.ok })
  expect(statusOf('thinking')).toBe('unknown')
})

const NOW_SR = 1_000_000_000
const srTexts = (held: unknown, columns: number) => sitrepSections(held, NOW_SR, columns).flatMap((s) => [s.title, ...s.rows.map((r) => ' '.repeat(r.indent) + r.text)])

const SR_REGIONS = [
  { col: 'c', list: 'completed', total: 'completed_total', label: 'Completed', optional: false, folded: false },
  { col: 'p', list: 'in_progress', total: 'in_progress_total', label: 'In progress', optional: false, folded: false },
  { col: 'n', list: 'up_next', total: null, label: 'Up next', optional: false, folded: false },
  { col: 'w', list: 'waiting_on_you', total: 'waiting_on_you_total', label: 'Waiting on you', optional: true, folded: false },
  { col: 'b', list: 'blocked', total: 'blocked_total', label: 'Blocked', optional: true, folded: false },
  { col: 'm', list: 'not_moving', total: 'not_moving_total', label: 'Report in, no one on it', optional: true, folded: true },
  { col: 's', list: 'stale', total: 'stale_total', label: 'Stale', optional: true, folded: true },
  { col: 'o', list: 'up_next_old', total: 'up_next_old_total', label: 'Up next, old', optional: true, folded: true },
]
const SR_BODY = {
    regions: SR_REGIONS,
    as_of: NOW_SR - 120_000, completed_total: 267, in_progress_total: 12, up_next_old_total: 0, stale_total: 0, blocked_total: 0, not_moving_total: 0, waiting_on_you_total: 1,
    waiting_on_you: [{ kind: 'decision', key: 'd1', title: 'Pick one', team: 't', since: NOW_SR - 60_000, note: 'needs you' }],
    in_progress: [{ key: '_run:a', title: 'Item A', stream: '_run', holder: 't/impl-1', state: 'in_progress', since: NOW_SR - 300_000 }],
    blocked: [],
    up_next: [{ key: 'b1', title: 'Backlog B', source: 'backlog' }, { key: 's:q', title: 'Queued Q', stream: 's', role: 'reviewer', source: 'queue', queued_since: NOW_SR - 60_000, stall_at: NOW_SR - 1, could_take: ['r1', 'r2'] }],
    up_next_old: [],
    completed: [{ kind: 'ship', key: 'x', ref: 'main', sha: 'abcdef123456', tag: 'v1', at: NOW_SR - 3_600_000 }, { kind: 'merge', key: 'y', ref: 'br', into: 'main', sha: '123456789', at: NOW_SR - 60_000 }, { kind: 'item', key: 'z', title: 'Done Z', stream: 's', holder: 'h', done_at: NOW_SR - 7_200_000 }],
    not_moving: [{ key: 'm', title: 'Stopped M', stream: 's', work: 'with_lead', since: NOW_SR - 60_000 }],
    stale: [],
}
const SR_HELD = { why: null, answered: true, teams: [{ name: 'rc4', body: SR_BODY }] }

test('sitrepSections: Completed, In progress and Up next first, the rest only with rows, one line an item, every region open', () => {
  expect(srTexts(SR_HELD, 120)).toEqual([
    'Sitrep · rc4',
    'updated 2m ago · 267 completed in total',
    'Completed 3 of 267',
    '  Shipped main · abcdef1 · tag',
    '  Merged br into main · 1234567',
    '  Done Z',
    'In progress 1 of 12',
    '  Item A',
    'Up next 2',
    '  Backlog B',
    '  Queued Q',
    'Waiting on you 1',
    '  Pick one',
    'Report in, no one on it 1',
    '  Stopped M',
  ])
})

test('sitrepSections: the three show at 0; negative: an empty other region is left out', () => {
  expect(srTexts({ why: null, answered: true, teams: [{ name: 't', body: { regions: SR_REGIONS } }] }, 60).slice(2)).toEqual(['Completed 0', 'In progress 0', 'Up next 0'])
})

test('sitrepSections: negative: a body serving no regions draws no heading even with rows', () => {
  expect(srTexts({ why: null, answered: true, teams: [{ name: 't', body: { in_progress: [{ key: 'k', title: 'Unlisted' }] } }] }, 60).slice(2)).toEqual([])
})

test('sitrepSections: a narrow Pane keeps an item to one row inside its width', () => {
  const title = 'A long title that cannot fit a narrow pane on one line'
  const held = { why: null, answered: true, teams: [{ name: 't', body: { regions: SR_REGIONS, in_progress: [{ key: 'k', title, stream: 'a-stream', holder: 'someone', since: NOW_SR - 1000 }] } }] }
  for (const columns of [24, 39, 40, 50]) {
    const [sec] = sitrepSections(held, NOW_SR, columns)
    const inner = columns - (columns >= 40 ? 2 : 0)
    for (const r of sec.rows) expect(r.indent + [...r.text].length).toBeLessThanOrEqual(inner)
    expect(sec.bordered).toBe(columns >= 40)
    expect(sec.rows.filter((r) => r.indent === 2).length).toBe(1)
  }
})

test('negative sitrepSections: no team, daemon down, loading, no slice and a malformed body each draw one plain line or empty groups', () => {
  expect(srTexts({ why: 'no team', answered: false, teams: [] }, 60)).toEqual(['Sitrep', 'No open team of yours here, so no sitrep.'])
  expect(srTexts({ why: 'down', answered: false, teams: [] }, 60)).toEqual(['Sitrep', 'The daemon is not answering, so no sitrep.'])
  expect(srTexts(undefined, 60)).toEqual(['Sitrep', 'Loading…'])
  expect(srTexts({ why: null, answered: false, teams: [{ name: 't', body: null }] }, 60)).toEqual(['Sitrep · t', 'Loading…'])
  expect(srTexts({ why: null, answered: true, teams: [{ name: 't', body: null }] }, 60)).toEqual(['Sitrep · t', 'The daemon has no sitrep for this team.'])
  expect(srTexts({ why: null, answered: true, teams: [{ name: 't', body: { regions: SR_REGIONS, in_progress: 'x', blocked: [3] } }] }, 60)).toContain('In progress 0')
})

test('dirIdOf is the daemon DirId: the first 8 hex digits of the SHA-256 of the dir', async () => {
  expect(await dirIdOf('/Users/example/git/repos/agent-tools/.claude/ah2')).toBe('25e51a9c')
})

test('registeredStatusFiles names the status beside each team file, once per dir', () => {
  const text = JSON.stringify({ team_file: '/a/.claude/ah2/teams/x.json', team_files: { 'o@x': '/a/.claude/ah2/teams/x.json', 'o@y': '/a/.claude/ah2/teams/y.json', 'o@z': '/b/.claude/ah2/teams/z.json' } })
  expect(registeredStatusFiles(text)).toEqual(['/a/.claude/ah2/status.json', '/b/.claude/ah2/status.json'])
  expect(registeredStatusFiles(JSON.stringify({ team_file: 'C:\\r\\.claude\\ah2\\teams\\x.json' }))).toEqual(['C:\\r\\.claude\\ah2\\status.json'])
})

test('negative: registeredStatusFiles names nothing for a relative path, a file outside teams/, a non-string, or text that is not JSON', () => {
  expect(registeredStatusFiles(JSON.stringify({ team_file: 'a/teams/x.json' }))).toEqual([])
  expect(registeredStatusFiles(JSON.stringify({ team_file: '/a/x.json', team_files: { 'o@x': 7 } }))).toEqual([])
  expect(registeredStatusFiles(JSON.stringify({ team_file: '/a/teams/sub/x.json' }))).toEqual([])
  expect(registeredStatusFiles('{')).toEqual([])
  expect(registeredStatusFiles(JSON.stringify([]))).toEqual([])
})

test('ownedTeamNames lists the viewer\'s teams, and negative: none for a foreign viewer or a document without owners', () => {
  const doc = parseDoc(fixtures.work)
  const sid = doc!.owners![0].sessions[0]
  expect(ownedTeamNames(doc, sid).length).toBeGreaterThan(0)
  expect(ownedTeamNames(doc, 'someone-else')).toEqual([])
  expect(ownedTeamNames({ ...doc!, owners: null }, sid)).toEqual([])
  expect(ownedTeamNames(null, sid)).toEqual([])
})

test('paneTab selects Sitrep unless the mode names the hierarchy; negative: the old Mail tab is gone', () => {
  expect(paneTab(undefined)).toBe('sitrep')
  expect(paneTab('bogus')).toBe('sitrep')
  expect(paneTab('mail')).toBe('sitrep')
  expect(paneTab('pane')).toBe('pane')
})

test('sitrepSections: pending questions sit above the Sitrep, one row each with what was not listed counted', () => {
  const held = { why: null, answered: true, teams: [{ name: 't', body: {}, questions: { waiting_total: 3, open: [{ text: 'Ship it?' }], items: [{ title: 'Which theme?' }] } }] }
  const [q, sitrep] = sitrepSections(held, NOW_SR, 60)
  expect(q.title).toBe('Questions · 3')
  expect(q.rows.map((r) => r.text)).toEqual(['Ship it?', 'Which theme?', '1 more', ANSWER_IN_WEB])
  expect(sitrep.title).toBe('Sitrep · t')
})

test('negative sitrepSections: nothing waiting says so, and a body with no questions slice yet draws no Questions section', () => {
  const none = sitrepSections({ why: null, answered: true, teams: [{ name: 't', body: {}, questions: { waiting_total: 0, open: [], items: [] } }] }, NOW_SR, 60)
  expect(none[0].title).toBe('Questions')
  expect(none[0].rows.map((r) => r.text)).toEqual(['No questions waiting.'])
  expect(none.flatMap((s) => s.rows).map((r) => r.text)).not.toContain(ANSWER_IN_WEB)
  expect(sitrepSections({ why: null, answered: true, teams: [{ name: 't', body: {}, questions: null }] }, NOW_SR, 60).map((s) => s.title)).toEqual(['Sitrep · t'])
})

test('sitrepSections: the empty state says what was looked at', () => {
  const [sec] = sitrepSections({ why: 'no team', detail: '(socket none; registrations not read)', answered: false, teams: [] }, NOW_SR, 200)
  expect(sec.rows.map((r) => r.text).join(' ')).toContain('No open team of yours here, so no sitrep. (socket none; registrations not read)')
})

// pane-subagents D4 and Amendment 1: a member's running subagents sit under its row, the viewer's own at the top
test('subagent rows follow their member at indent 2, and negative: a member without the field gets none', () => {
  const at = (s: number) => new Date(ms(T0) - s * 1000).toISOString()
  const member = (name: string, session: string, subagents?: unknown) => ({ name, role: name, label: name, kind: 'claude', route: 'peer', session_id: session, live: true, activity: 'working', ...(subagents ? { subagents } : {}) })
  const doc = parseDoc(docText({
    teams: [{ team: 't', members: [member('impl', 's-impl', [{ agent_type: 'Explore', started_at: at(90) }, { agent_type: null, started_at: at(30) }]), member('rev', 's-rev')], dispatches: [] }],
    owners: [{ sessions: ['sess-orch'], teams: ['t'], timeline: [{ at: T0, live: 1, blocked: 0, out: 0, overdue: 0, stalled: 0, text: '1 live · 0 out', short: '1/0', tone: 'work', visible: true }], subagents: [{ agent_type: 'wrangl:qa', started_at: at(5) }] }],
  }))
  const view = viewModel(doc, ms(T0), 'sess-orch')
  const rows = paneSections(view, 80).flatMap((s) => s.rows.map((r) => `${r.indent}|${r.text.trim()}`))
  expect(rows.slice(0, 1)).toEqual(['0|└─ wrangl:qa · 5s'])
  const i = rows.findIndex((r) => r.includes('impl'))
  expect(rows.slice(i + 1, i + 3)).toEqual(['2|├─ Explore · 1m', '2|└─ subagent · 30s'])
  expect(rows[i + 3]).toContain('rev')
  expect(paneRows(view, 80).map((r) => r.text)).toContain('├─ Explore · 1m')
  // a held view from an older module: members carry no subagent array
  const held = { ...view!, lead: undefined, teams: view!.teams.map((t) => ({ ...t, members: t.members.map((m) => ({ stream: m.stream, row: m.row })) })) }
  expect(paneSections(held, 80).flatMap((s) => s.rows).some((r) => r.text.includes('─'))).toBe(false)
  expect(ownedTeams({ ...held, owned: true }).length).toBe(1)
})
