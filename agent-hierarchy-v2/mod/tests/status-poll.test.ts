import { test, expect } from 'claude-code/testing'
import { register } from '../register.tsx'
import { fixtures } from './fixtures.ts'
import { dirIdOf, ownedTeams } from '../view.ts'

// The status poll as the mod runs it: the 2 s tick asks the daemon for the status document once the mail poll has named a socket.
// A dir the daemon does not watch answers 404 not_found; the poll stops asking until something could have changed that.

const NOW = 1_790_000_000_000
const SID = 'sess-orch'
const FILE = '/work/repo/.claude/ah2/status.json'
const SOCK_A = '/run/ah/a.sock'
const SOCK_B = '/run/ah/b.sock'
const DOC = JSON.stringify({ v: 1, rev: 1, doc: null })
const settle = () => new Promise<void>((r) => setTimeout(r, 0))
const answer = (socket: string) => JSON.stringify({ v: 1, ids: [], prompt: null, idle: true, state: 'S1', acked: [], daemon: { socket } })

type Timer = { ms: number; fn: () => unknown; cancelled: boolean; cancel: () => void }
type Reply = { ok: boolean; status: number; text: string } | Error

// `readGate`: the first status file read waits for it.
const rig = async (cli: string[] = [], submit: (p: any) => Promise<unknown> = async () => ({}), rows: any[] = [{ session_id: SID }], readGate: Promise<void> = Promise.resolve()) => {
  let gated = false
  const w = {
    stdin: [] as any[],
    now: NOW,
    sock: SOCK_A,
    mtime: 1 as number | null,
    statusReplies: [] as Reply[],
    status: [] as { url: string; socketPath: string }[],
    rows,
    rowsDown: false,
    due: [] as { fail: (e: unknown) => void }[],
    timers: [] as Timer[],
    store: new Map<string, unknown>(),
    fileText: '{}',
    panes: [] as { id: string; isPlaced: boolean }[],
    views: [] as string[],
    viewReplies: [] as Reply[],
    summaries: [] as string[],
    closed: [] as string[],
    summariesDown: false,
    // A status read that answers only when the test closes it with `statusClose`.
    statusHangs: false,
    statusClose: [] as (() => void)[],
  }
  const hooks: Record<string, any> = {}
  register((event: string, matcher: any, fn: any) => { hooks[event] = fn ?? matcher }, {} as any)
  const $: any = {
    env: { get: async (k: string) => (k === 'AH_HOME' ? '/ah' : undefined) },
    session: { id: async () => SID, cwd: async () => '/work/repo' },
    fs: {
      exists: async (p: string) => p === '/work/repo/.git',
      stat: async (p: string) => {
        if (p === FILE && w.mtime !== null) return { kind: 'file', size: 2, mtimeMs: w.mtime, isLink: false }
        if (p === '/ah/session-marks/' + SID) return {}
        throw new Error('no such file')
      },
      read: async (p: string) => { if (!gated && p === FILE) { gated = true; await readGate } return w.fileText },
    },
    clock: {
      now: async () => w.now,
      every: (ms: number, fn: () => unknown) => {
        const t: Timer = { ms, fn, cancelled: false, cancel: () => { t.cancelled = true } }
        w.timers.push(t)
        return t
      },
    },
    state: {
      get: async (k: { key: string }) => ({ value: w.store.get(k.key) }),
      set: async (k: { key: string }, v: unknown) => { w.store.set(k.key, v) },
    },
    ui: { status: () => {}, open: async () => ({ isPlaced: true }), toast: async () => {}, panes: async () => w.panes },
    command: { register: async () => {} },
    plugin: { root: '/opt/ah' },
    process: { run: async (_argv: string[], init: any) => { w.stdin.push(JSON.parse(init.stdin)); return { exitCode: 0, stdout: cli.shift() ?? answer(w.sock), stderr: '' } } },
    prompt: { submit },
    http: {
      fetch: (url: string, init: any) => {
        if (url.startsWith('http://ahd/v1/due')) return new Promise((_res, rej) => { w.due.push({ fail: rej }) })
        if (url.startsWith('http://ahd/v1/inspect/registrations?')) return w.rowsDown ? Promise.reject(new Error('refused')) : Promise.resolve({ ok: true, status: 200, text: JSON.stringify({ registrations: w.rows }) })
        // The band's once-a-tick read of the owned teams' summaries, kept apart from the Sitrep's long poll.
        if (url.startsWith('http://ahd/v1/view') && /[?&]sub=team-summary/.test(url)) {
          w.summaries.push(url)
          if (w.summariesDown) return Promise.reject(new Error('refused'))
          const sub = new URL(url).searchParams.get('sub') ?? ''
          return Promise.resolve(summariesAnswer(sub, w.closed))
        }
        if (url.startsWith('http://ahd/v1/view')) {
          w.views.push(url)
          const reply = w.viewReplies.shift() ?? { ok: true, status: 200, text: JSON.stringify({ epoch: 'e1', seq: 7, resync: false, slices: [], removed: [] }) }
          return reply instanceof Error ? Promise.reject(reply) : Promise.resolve(reply)
        }
        w.status.push({ url, socketPath: init.socketPath })
        if (w.statusHangs) return new Promise((_res, rej) => { w.statusClose.push(() => rej(new Error('closed'))) })
        const next = w.statusReplies.shift() ?? { ok: true, status: 200, text: DOC }
        return next instanceof Error ? Promise.reject(next) : Promise.resolve(next)
      },
    },
  }
  await hooks['session.start']($, { cwd: '/work/repo', isInteractive: true }, async (e: unknown) => e)
  const tick = async () => { await w.timers.find((t) => t.ms === 2000)!.fn(); await settle() }
  const poll = async () => { await w.timers.filter((t) => t.ms === 5000 && !t.cancelled).at(-1)!.fn(); await settle() }
  await poll()
  return { w, tick, poll, hooks, $ }
}

test('a Stop holds the doorbell until a prompt enters, and only then', async () => {
  const ring = JSON.stringify({ v: 1, ids: ['doorbell:7'], prompt: 'ah: You have 1 message', ring: true, idle: true, state: 'S1', acked: [], daemon: null })
  const quiet = JSON.stringify({ v: 1, ids: [], prompt: null, idle: true, state: 'S1', acked: [], daemon: null })
  const r = await rig([ring, quiet, quiet, quiet], async () => ({ drop: 'the session was stopped and nobody has sent input since' }))
  expect(r.w.stdin[0].held).toBeUndefined()

  await r.poll()
  await r.poll()
  expect(r.w.stdin[1].held).toBe(true)
  expect(r.w.stdin[2].held).toBe(true)

  await r.hooks['prompt.submit'](r.$, { text: 'go on' }, async (e: unknown) => e)
  await r.poll()
  expect(r.w.stdin[3].held).toBeUndefined()
})

const STOPPED = { drop: 'the session was stopped and nobody has sent input since' }
const RING = JSON.stringify({ v: 1, ids: ['doorbell:7'], prompt: 'ah: You have 1 message', ring: true, idle: true, state: 'S1', acked: [], daemon: null })
const QUIET = JSON.stringify({ v: 1, ids: [], prompt: null, idle: true, state: 'S1', acked: [], daemon: null })
const LIMIT = 600_000

test('a subagent ending leaves the main turn busy and the Stop held; the main turn ending clears both', async () => {
  const r = await rig([RING, QUIET, QUIET, QUIET, QUIET, QUIET], async () => STOPPED)
  await r.poll()
  expect(r.w.stdin[1].held).toBe(true)

  await r.hooks['turn.complete'](r.$, { agentId: 'a1' }, async (e: unknown) => e)
  await r.poll()
  expect(r.w.stdin[2].held).toBe(true)

  await r.hooks['turn.complete'](r.$, {}, async (e: unknown) => e)
  await r.poll()
  expect(r.w.stdin[3].held).toBeUndefined()

  await r.hooks['prompt.submit'](r.$, { text: 'go on' }, async (e: unknown) => e)
  await r.poll()
  const busy = r.w.stdin[4].busy_at
  expect(busy).not.toBeNull()

  await r.hooks['turn.complete'](r.$, { agentId: 'a1' }, async (e: unknown) => e)
  await r.poll()
  expect(r.w.stdin[5].busy_at).toBe(busy)

  await r.hooks['turn.complete'](r.$, {}, async (e: unknown) => e)
  await r.poll()
  expect(r.w.stdin[6].busy_at).toBeNull()
})

test('while held a fixed line re-tests the Stop once a limit; a drop stays held and an entered prompt clears it', async () => {
  const texts: string[] = []
  let answer: unknown = STOPPED
  const r = await rig([RING, QUIET, QUIET, QUIET, QUIET, QUIET], async (p: { text: string }) => { texts.push(p.text); return answer })
  await r.poll()
  expect(texts.length).toBe(1)

  r.w.now += LIMIT - 1000
  await r.poll()
  expect(texts.length).toBe(1)

  r.w.now += 1000
  await r.poll()
  expect(texts.slice(1)).toEqual(['ah: mail waiting, run ah msg next'])
  await r.poll()
  expect(r.w.stdin.at(-1).held).toBe(true)
  expect(texts.length).toBe(2)

  answer = {}
  r.w.now += LIMIT
  await r.poll()
  expect(texts.length).toBe(3)
  await r.poll()
  expect(r.w.stdin.at(-1).held).toBeUndefined()
})

test('a session never held sends no probe however long it waits', async () => {
  const texts: string[] = []
  const r = await rig([QUIET, QUIET], async (p: { text: string }) => { texts.push(p.text); return {} })
  r.w.now += LIMIT * 2
  await r.poll()
  expect(texts).toEqual([])
})

const held: Reply = { ok: true, status: 200, text: JSON.stringify({ v: 1, rev: 5, doc: { schema: 1, expires_at: '2030-01-01T00:00:00Z', member_sessions: [], timeline: [{ at: '2026-01-01T00:00:00Z' }] } }) }
const notFound = (code = 'not_found'): Reply => ({ ok: false, status: 404, text: JSON.stringify({ error: { code, message: 'x' } }) })

test('session.start returns while its first tick waits on a read that never answers; negative: that tick still does its work once the read answers', async () => {
  let open!: () => void
  const gate = new Promise<void>((res) => { open = res })
  const r = await Promise.race([rig([], undefined, undefined, gate), new Promise<null>((res) => setTimeout(() => res(null), 1000))])
  expect(r).not.toBeNull()
  expect(r!.w.store.has('why')).toBe(false)

  // The hung first tick does not keep the scheduled ticks off the daemon.
  await r!.tick()
  expect(r!.w.status.length).toBe(1)
  r!.w.store.delete('why')

  open()
  await settle()
  await settle()
  expect(r!.w.store.get('why')).toBe('unreadable')
})

test('session.start returns while the build check waits on a stamp read that never answers, with no toast; negative: after the bound a later check runs', async () => {
  const r = await rig()
  const toasts: unknown[] = []
  const started = JSON.stringify({ version: '2.0.3', last_rebuild: { version: '2.0.4', at: '2026-11-17T17:00:00.000Z', state: 'started' } })
  let hangs = true
  const $s = { ...r.$, ui: { ...r.$.ui, toast: async (t: unknown) => { toasts.push(t) } },
    fs: { ...r.$.fs, read: (p: string) => (p === '/ah/install.json' ? (hangs ? new Promise(() => {}) : Promise.resolve(started)) : r.$.fs.read(p)) } }
  const from = r.w.timers.length
  // A reload runs session.start again; its build check's stamp read is the one that hangs.
  const back = await Promise.race([r.hooks['session.start']($s, { cwd: '/work/repo', isInteractive: true }, async (e: unknown) => e).then(() => true), new Promise<boolean>((res) => setTimeout(() => res(false), 1000))])
  expect(back).toBe(true)
  await settle()
  hangs = false
  await r.hooks['prompt.submit']($s, { text: 'go on' }, async (e: unknown) => e)
  await settle()
  expect(toasts).toEqual([])

  // The read's bound passes: the hung read is abandoned, and the next check reads the stamp and shows its toast.
  await r.w.timers.slice(from).filter((t) => t.ms === 2000 && !t.cancelled).at(-1)!.fn()
  await settle()
  await r.hooks['prompt.submit']($s, { text: 'go on' }, async (e: unknown) => e)
  await settle()
  expect(toasts.length).toBe(1)
})

test('a status read the daemon never answers fails after 2000 ms, and no second one opens until it settles', async () => {
  const r = await rig()
  r.w.statusHangs = true
  const tickTimer = r.w.timers.find((t) => t.ms === 2000)!
  // The read's time limit: a 2000 ms timer of its own, cancelled once the read settles.
  const limits = () => r.w.timers.filter((t) => t !== tickTimer && t.ms === 2000 && !t.cancelled)

  // This tick waits on the read until its time limit runs out.
  const first = tickTimer.fn()
  await settle()
  expect(r.w.status.length).toBe(1)
  expect(limits().length).toBe(1)
  await r.tick()
  expect(r.w.status.length).toBe(1)

  limits()[0].fn()
  await first
  expect(limits().length).toBe(0)
  // The limit ran out but the fetch is still open: ticks read the file and open no second socket.
  await r.tick()
  expect(r.w.status.length).toBe(1)

  // negative: once the fetch settles, the next tick asks again.
  r.w.statusClose.shift()!()
  await settle()
  r.w.statusHangs = false
  await r.tick()
  expect(r.w.status.length).toBe(2)
})

test('after a 404 not_found the status request is not repeated while the file is unchanged', async () => {
  const r = await rig()
  r.w.statusReplies.push(notFound())

  await r.tick()
  await r.tick()
  await r.tick()
  await r.tick()

  expect(r.w.status.length).toBe(1)
  expect(r.w.status[0].socketPath).toBe(SOCK_A)
})

test('after a document arrives the daemon is not asked again while the file is unchanged', async () => {
  const r = await rig()
  r.w.statusReplies.push(held, held, held, held)

  await r.tick()
  await r.tick()
  await r.tick()
  await r.tick()

  expect(r.w.status.length).toBe(1)
})

test('after a document arrives a changed mtime asks the daemon once more', async () => {
  const r = await rig()
  r.w.statusReplies.push(held, held, held, held)
  await r.tick()
  await r.tick()

  r.w.mtime = 2
  await r.tick()
  await r.tick()

  expect(r.w.status.length).toBe(2)
})

test('after a 404 not_found a changed mtime does not ask the daemon again', async () => {
  const r = await rig()
  r.w.statusReplies.push(notFound(), notFound())
  await r.tick()
  await r.tick()

  r.w.mtime = 2
  await r.tick()
  r.w.mtime = 3
  await r.tick()
  await r.tick()

  expect(r.w.status.length).toBe(1)
})

test('a status file that appears, or goes, asks the daemon once more', async () => {
  const r = await rig()
  r.w.mtime = null
  r.w.statusReplies.push(notFound(), notFound(), notFound())
  await r.tick()
  await r.tick()
  expect(r.w.status.length).toBe(1)

  r.w.mtime = 5
  await r.tick()
  await r.tick()
  expect(r.w.status.length).toBe(2)

  r.w.mtime = null
  await r.tick()
  await r.tick()
  expect(r.w.status.length).toBe(3)
})

test('a new daemon socket asks the daemon once more', async () => {
  const r = await rig()
  r.w.statusReplies.push(notFound(), notFound())
  await r.tick()
  await r.tick()
  expect(r.w.status.length).toBe(1)

  r.w.sock = SOCK_B
  r.w.due[0].fail(new Error('daemon went away'))
  await settle()
  r.w.now += 120_000
  await r.poll()
  await r.tick()
  await r.tick()

  expect(r.w.status.map((s) => s.socketPath)).toEqual([SOCK_A, SOCK_B])
})

test('the mark clears after five minutes: no request before, one at five minutes, then the mark is set again', async () => {
  const r = await rig()
  r.w.statusReplies.push(notFound(), notFound())
  await r.tick()
  expect(r.w.status.length).toBe(1)

  r.w.now += 5 * 60_000 - 1
  await r.tick()
  await r.tick()
  expect(r.w.status.length).toBe(1)

  r.w.now += 1
  await r.tick()
  expect(r.w.status.length).toBe(2)

  r.w.now += 5 * 60_000 - 1
  await r.tick()
  expect(r.w.status.length).toBe(2)
})

test('negative: a 200 answer after the mark clears leaves nothing marked', async () => {
  const r = await rig()
  r.w.statusReplies.push(notFound())
  await r.tick()

  r.w.now += 5 * 60_000
  await r.tick()
  await r.tick()
  await r.tick()

  expect(r.w.status.length).toBe(4)
})

const everyTick: [string, Reply][] = [
  ['a 500', { ok: false, status: 500, text: '{"error":{"code":"internal","message":"x"}}' }],
  ['a refused connection', new Error('connect ECONNREFUSED')],
  ['an unparsable 200 body', { ok: true, status: 200, text: 'not json' }],
  ['a 404 with another error code', notFound('gone')],
  ['a 404 whose body is not JSON', { ok: false, status: 404, text: 'Not Found' }],
  ['a 200 with a null document', { ok: true, status: 200, text: DOC }],
]
for (const [name, reply] of everyTick) {
  test(`negative: ${name} is asked again every tick`, async () => {
    const r = await rig()
    for (let i = 0; i < 3; i++) r.w.statusReplies.push(reply)

    await r.tick()
    await r.tick()
    await r.tick()

    expect(r.w.status.length).toBe(3)
  })
}

// The Sitrep in the Pane: one long poll of the daemon's slice while the Pane shows it, and nothing derived here.
const FIXTURE_AT = Date.parse('2026-01-01T12:00:00.000Z')
const sitrepOn = (r: any) => {
  r.w.now = FIXTURE_AT
  r.w.store.set('paneMode', 'sitrep')
  r.w.panes = [{ id: 'ah-status', isPlaced: true }]
}
const teamDoc: Reply = { ok: true, status: 200, text: JSON.stringify({ v: 1, rev: 2, doc: JSON.parse(fixtures.work) }) }

test('the Sitrep is read from the daemon slice of the owned team, held, and re-read from its cursor', async () => {
  const r = await rig()
  sitrepOn(r)
  r.w.statusReplies.push(teamDoc)
  await r.tick()
  expect(r.w.views.length).toBe(1)
  const first = new URL(r.w.views[0])
  const sub = first.searchParams.get('sub')!
  expect(sub.startsWith('sitrep:' + (await dirIdOf('/work/repo/.claude/ah2')) + '/')).toBe(true)
  expect(first.searchParams.get('path')).toBe('/events')
  expect(first.searchParams.get('since')).toBe('0')
  expect(first.searchParams.get('fresh')).toBe('*')
  expect(first.searchParams.get('wait')).toBe('25')

  r.w.viewReplies.push({ ok: true, status: 200, text: JSON.stringify({ epoch: 'e1', seq: 9, resync: false, slices: [{ id: sub.split(',')[0], body: { completed_total: 3 } }], removed: [] }) })
  await r.tick()
  const second = new URL(r.w.views[1])
  expect(second.searchParams.get('since')).toBe('7')
  expect(second.searchParams.get('epoch')).toBe('e1')
  expect(second.searchParams.get('fresh')).toBe(null)
  const held: any = r.w.store.get('sitrep')
  expect(held.why).toBe(null)
  expect(held.teams[0].body).toEqual({ completed_total: 3 })
})

// Two owned teams, as a viewer with a closed team and a live one has.
const twoTeams = () => {
  const doc = JSON.parse(fixtures.work)
  const first = doc.owners[0].teams[0]
  doc.teams.push({ ...doc.teams.find((t: any) => t.team === first), team: 'closed-one' })
  doc.owners[0].teams.push('closed-one')
  return { reply: { ok: true, status: 200, text: JSON.stringify({ v: 1, rev: 2, doc }) } as Reply, open: first ?? 'default' }
}
const summariesAnswer = (sub: string, closed: string[]): Reply => ({ ok: true, status: 200, text: JSON.stringify({ epoch: 'e1', seq: 9, resync: false, removed: [], slices: sub.split(',').filter((id) => id.startsWith('team-summary:')).map((id) => ({ id, body: closed.includes(id.slice(id.lastIndexOf('/') + 1)) ? { closed_at: '2026-01-01T11:00:00.000Z' } : { status: 'live' } })) }) })

test('a closed team leaves the Sitrep, and negative: an open team stays shown', async () => {
  const r = await rig()
  sitrepOn(r)
  const { reply, open } = twoTeams()
  r.w.statusReplies.push(reply)
  await r.tick()
  const sub = new URL(r.w.views[0]).searchParams.get('sub')!
  r.w.viewReplies.push(summariesAnswer(sub, ['closed-one']))
  await r.tick()
  const held: any = r.w.store.get('sitrep')
  expect(held.why).toBe(null)
  expect(held.teams.map((t: any) => t.name)).toEqual([open])
})

test('negative: with both teams open, both show', async () => {
  const r = await rig()
  sitrepOn(r)
  const { reply, open } = twoTeams()
  r.w.statusReplies.push(reply)
  await r.tick()
  r.w.viewReplies.push(summariesAnswer(new URL(r.w.views[0]).searchParams.get('sub')!, []))
  await r.tick()
  expect((r.w.store.get('sitrep') as any).teams.map((t: any) => t.name)).toEqual([open, 'closed-one'])
})

test('a closed team leaves the band, Dismiss and the view, and negative: an open team stays in them', async () => {
  const r = await rig()
  r.w.now = FIXTURE_AT
  const { reply, open } = twoTeams()
  r.w.closed = ['closed-one']
  r.w.statusReplies.push(reply)
  await r.tick()
  expect(r.w.summaries.length).toBeGreaterThan(0)
  const view: any = r.w.store.get('view')
  expect(view.band.head.startsWith('1 team ·')).toBe(true)
  expect(ownedTeams(view).map((t) => t.name)).toEqual([open])
  // at most one read in 30 s: the next ticks ask nothing
  await r.tick()
  await r.tick()
  expect(r.w.summaries.length).toBe(1)
  r.w.now += 30_000
  await r.tick()
  expect(r.w.summaries.length).toBe(2)
})

test('a summary read that fails is not retried on the next ticks, only after 30 s', async () => {
  const r = await rig()
  r.w.now = FIXTURE_AT
  const { reply } = twoTeams()
  r.w.summariesDown = true
  r.w.statusReplies.push(reply)
  await r.tick()
  expect(r.w.summaries.length).toBe(1)
  await r.tick()
  await r.tick()
  expect(r.w.summaries.length).toBe(1)
  r.w.now += 30_000
  await r.tick()
  expect(r.w.summaries.length).toBe(2)
})

test('negative: with both teams open, the band counts both and Dismiss offers both', async () => {
  const r = await rig()
  r.w.now = FIXTURE_AT
  const { reply, open } = twoTeams()
  r.w.statusReplies.push(reply)
  await r.tick()
  const view: any = r.w.store.get('view')
  expect(view.band.head.startsWith('2 teams ·')).toBe(true)
  expect(ownedTeams(view).map((t) => t.name)).toEqual([open, 'closed-one'])
})

test('with the Sitrep polling, the band takes its verdicts from that poll and asks nothing of its own', async () => {
  const r = await rig()
  sitrepOn(r)
  const { reply, open } = twoTeams()
  r.w.statusReplies.push(reply)
  await r.tick()
  r.w.viewReplies.push(summariesAnswer(new URL(r.w.views[0]).searchParams.get('sub')!, ['closed-one']))
  await r.tick()
  const before = r.w.summaries.length
  await r.tick()
  await r.tick()
  expect(r.w.summaries.length).toBe(before)
  expect(ownedTeams(r.w.store.get('view')).map((t) => t.name)).toEqual([open])
})

test('an only owned team that is closed reads as no open team', async () => {
  const r = await rig()
  sitrepOn(r)
  const { reply, open } = twoTeams()
  r.w.statusReplies.push(reply)
  await r.tick()
  r.w.viewReplies.push(summariesAnswer(new URL(r.w.views[0]).searchParams.get('sub')!, [open, 'closed-one']))
  await r.tick()
  expect(r.w.store.get('sitrep')).toMatchObject({ why: 'no team', answered: true, teams: [] })
})

test('negative: with no team of the viewer nothing is asked and the Pane is told why', async () => {
  const r = await rig()
  sitrepOn(r)
  await r.tick()
  expect(r.w.views).toEqual([])
  expect(r.w.store.get('sitrep')).toMatchObject({ why: 'no team', answered: false, teams: [] })
})

test('negative: a daemon that fails the read says down, and the next read starts over', async () => {
  const r = await rig()
  sitrepOn(r)
  r.w.statusReplies.push(teamDoc)
  r.w.viewReplies.push(new Error('connection refused'))
  await r.tick()
  expect((r.w.store.get('sitrep') as any).why).toBe('down')
  await r.tick()
  expect(new URL(r.w.views[1]).searchParams.get('since')).toBe('0')
  expect((r.w.store.get('sitrep') as any).why).toBe(null)
})

test('negative: no daemon socket at all says down with no request', async () => {
  const r = await rig([QUIET])
  sitrepOn(r)
  r.w.fileText = fixtures.work
  r.w.mtime = 2
  await r.tick()
  expect(r.w.views).toEqual([])
  expect((r.w.store.get('sitrep') as any).why).toBe('down')
})

test('the reason names a missing socket, so a screenshot tells that from a missing row', async () => {
  const r = await rig([QUIET])
  sitrepOn(r)
  await r.tick()
  expect((r.w.store.get('sitrep') as any).detail).toContain('socket none; registrations not read; own row no')
})

test('negative: the reason says what was looked at', async () => {
  const r = await withRows([])
  sitrepOn(r)
  await r.poll()
  await r.tick()
  const detail = (r.w.store.get('sitrep') as any).detail
  expect(detail).toContain('registrations 0')
  expect(detail).toContain('own row no')
  expect(detail).toContain('session ' + SID.slice(0, 8))
  expect(detail).toContain(FILE)
})

test('a closed Pane or the hierarchy mode polls nothing', async () => {
  const r = await rig()
  sitrepOn(r)
  r.w.statusReplies.push(teamDoc)
  r.w.panes = []
  await r.tick()
  r.w.panes = [{ id: 'ah-status', isPlaced: true }]
  r.w.store.set('paneMode', 'pane')
  await r.tick()
  expect(r.w.views).toEqual([])
})

// A session whose team lives in another repository than its cwd: its row in the daemon's answer names the team file, and no registrations/ directory exists.
const OTHER = '/other/repo/.claude/ah2'
const readsOf = (r: any) => {
  const paths: string[] = []
  const read = r.$.fs.read
  r.$.fs.read = async (p: string) => {
    paths.push(p)
    if (p.includes('/registrations/')) throw new Error('no such file')
    return read(p)
  }
  return paths
}
const rowsIn = (dir: string) => [{ session_id: SID, team_file: dir + '/teams/t.json', team_files: { 'orchestrator@t': dir + '/teams/t.json' } }]
const withRows = (rows: any[]) => rig([], undefined, rows)
const emptyDoc: Reply = { ok: true, status: 200, text: DOC }
const served = (text: string): Reply => ({ ok: true, status: 200, text: JSON.stringify({ v: 1, rev: 2, doc: JSON.parse(text) }) })

test('a team in another repository than the session cwd is found through the registration, and its Sitrep read from its dir', async () => {
  const r = await withRows(rowsIn(OTHER))
  sitrepOn(r)
  readsOf(r)
  r.w.statusReplies.push(emptyDoc, teamDoc)
  await r.tick()
  expect(decodeURIComponent(r.w.status[1].url)).toContain('dir=' + OTHER + '&')
  expect(r.w.store.get('view')).toMatchObject({ owned: true })
  expect(new URL(r.w.views[0]).searchParams.get('sub')!.startsWith('sitrep:' + (await dirIdOf(OTHER)) + '/')).toBe(true)
})

test('negative: a registered team file whose status does not list this session is no team', async () => {
  const r = await withRows(rowsIn(OTHER))
  sitrepOn(r)
  readsOf(r)
  r.w.statusReplies.push(emptyDoc, served(fixtures.foreign))
  await r.tick()
  expect(r.w.views).toEqual([])
  expect(r.w.store.get('sitrep')).toMatchObject({ why: 'no team', answered: false, teams: [] })
})

test('negative: a session the checkout already knows does not use its row', async () => {
  const r = await withRows(rowsIn(OTHER))
  sitrepOn(r)
  const paths = readsOf(r)
  r.w.statusReplies.push(teamDoc)
  await r.tick()
  expect(r.w.status.length).toBe(1)
  expect(paths.filter((p) => p.includes('/registrations/'))).toEqual([])
})

test('negative: another session\'s row is not used', async () => {
  const r = await withRows([{ session_id: 'someone-else', team_file: OTHER + '/teams/t.json' }, { session_id: SID }])
  sitrepOn(r)
  r.w.statusReplies.push(emptyDoc, teamDoc)
  await r.tick()
  expect(r.w.status.length).toBe(1)
})

for (const [name, row] of [
  ['no team files', {}],
  ['a relative path', { team_file: 'repo/.claude/ah2/teams/t.json' }],
  ['a parent that is not teams', { team_file: OTHER + '/other/t.json', team_files: { 'a@t': OTHER + '/other/t.json' } }],
  ['team_files that is no map', { team_files: 'nonsense' }],
] as [string, Record<string, unknown>][]) {
  test(`negative: a row with ${name} finds nothing and does not throw`, async () => {
    const r = await withRows([{ session_id: SID, ...row }])
    sitrepOn(r)
    r.w.statusReplies.push(emptyDoc, teamDoc)
    await r.tick()
    expect(r.w.status.length).toBe(1)
    expect(r.w.store.get('sitrep')).toMatchObject({ why: 'no team', answered: false, teams: [] })
  })
}

test('with the daemon down the last row stands', async () => {
  const r = await withRows(rowsIn(OTHER))
  sitrepOn(r)
  readsOf(r)
  r.w.statusReplies.push(emptyDoc, teamDoc)
  await r.tick()
  r.w.rowsDown = true
  r.w.now += 6000
  const asked = r.w.status.length
  r.w.mtime = (r.w.mtime ?? 0) + 1
  r.w.statusReplies.push(emptyDoc, teamDoc)
  await r.tick()
  expect(r.w.status.slice(asked).some((s: { url: string }) => decodeURIComponent(s.url).includes('dir=' + OTHER + '&'))).toBe(true)
})

test('an owner whose band is hidden still has a Sitrep', async () => {
  const hidden = JSON.parse(fixtures.work)
  for (const o of hidden.owners) for (const t of o.timeline) t.visible = false
  const r = await rig()
  sitrepOn(r)
  r.w.statusReplies.push(served(JSON.stringify(hidden)))
  await r.tick()
  expect(r.w.store.get('view') ?? null).toBe(null)
  expect(r.w.views.length).toBe(1)
})

test('the Sitrep is the default: a Pane with no mode set polls it, and an old Mail mode still does', async () => {
  const r = await rig()
  sitrepOn(r)
  r.w.store.delete('paneMode')
  r.w.statusReplies.push(teamDoc)
  await r.tick()
  expect(r.w.views.length).toBe(1)
  r.w.store.set('paneMode', 'mail')
  r.w.views.length = 0
  await r.tick()
  expect(r.w.views.length).toBe(1)
})
