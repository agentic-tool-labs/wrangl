import { test, expect } from 'claude-code/testing'
import { mailCycle, toolRing } from '../register.tsx'
import { readDue } from '../view.ts'

// The mail poll as the mod runs it: one request to the native CLI (or, once a CLI answer has named a live daemon, one GET the daemon
// holds open), the answer acted on, the next request. What is due, and the prompt, are the native side's: tests/ and
// native/tests/Ah.Mesh.Tests/MailDue*Tests.cs. This file holds what the mod decides: states, claims, release, backoff.

const NOW = 1_790_000_000_000
const SID = 'sess-m'
const SOCK = '/run/ah/ahd.sock'
const ROOT = '/opt/ah'
const CLI = [ROOT + '/bin/ah', 'msg', 'due', '--json']

const answer = (over: Record<string, unknown> = {}) => ({ v: 1, ids: [], prompt: null, idle: true, state: 'S1', acked: [], daemon: null, ...over })
const withMail = (ids: string[], over: Record<string, unknown> = {}) => answer({ ids, prompt: 'ah mail for imp@tm (' + ids.length + '):\n' + ids.map((i) => '- ' + i).join('\n') + '\n[ah-entry ' + ids.join(',') + ']', ...over })
const cliOk = (a: unknown) => ({ exitCode: 0, stdout: JSON.stringify(a), stderr: '' })
const fetchOk = (a: unknown) => ({ ok: true, status: 200, text: JSON.stringify(a) })
const settle = () => new Promise<void>((r) => setTimeout(r, 0))
const held = () => {
  let release!: (v: unknown) => void
  let fail!: (e: unknown) => void
  const promise = new Promise((res, rej) => { release = res; fail = rej })
  return { promise, release, fail }
}

type Timer = { ms: number; fn: () => void; cancelled: boolean; cancel: () => void }
type Handed = { state: 'pending' | 'resolved'; at: number }

// A stand-in `$` and context for the helper alone, so a test can vary what the engine's harness would not (the plugin directory, the
// timer, a request that never answers). `plan` answers each CLI call and each fetch in order.
const rig = (over: { root?: unknown; registered?: boolean; hier?: string | null; toastMs?: number } = {}) => {
  const w = {
    now: NOW,
    registered: over.registered ?? true,
    asks: 0,
    daemonDown: false,
    // A registrations read that answers only when the test closes it with `regClose`.
    regHangs: false,
    regClose: [] as (() => void)[],
    daemonRegistered: undefined as boolean | undefined,
    // the daemon's sessions counter: undefined answers as files mode does, with no token
    regToken: undefined as number | undefined,
    regUrls: [] as string[],
    runs: [] as { argv: string[]; init: any }[],
    fetches: [] as { url: string; init: any }[],
    submits: [] as string[],
    store: new Map<string, unknown>(),
    mtimes: {} as Record<string, number>,
    stateFails: false,
    stateHangs: false,
    toasts: [] as { text: string; init: any }[],
    timers: [] as Timer[],
    limits: [] as Timer[],
    cli: [] as (() => Promise<any> | any)[],
    http: [] as (() => Promise<any> | any)[],
    submit: (_text: string): Promise<unknown> => Promise.resolve({}),
  }
  const $ = {
    session: { id: async () => SID, cwd: async () => '/work/repo' },
    fs: { stat: async (path: string) => { if (!w.registered || path.includes('/registrations/')) throw new Error('no such file'); return path in w.mtimes ? { mtimeMs: w.mtimes[path] } : {} } },
    clock: {
      now: async () => w.now,
      every: (ms: number, fn: () => void) => {
        const t: Timer = { ms, fn, cancelled: false, cancel: () => { t.cancelled = true } }
        // A daemon read's 2000 ms time limit is kept apart from the poll's own timers.
        ;(ms === 2000 ? w.limits : w.timers).push(t)
        return t
      },
    },
    http: { fetch: async (url: string, init: any) => { if (url.startsWith('http://ahd/v1/inspect/registrations?')) { w.asks++; w.regUrls.push(url); if (w.daemonDown) throw new Error('refused'); if (w.regHangs) return new Promise((_res, rej) => { w.regClose.push(() => rej(new Error('closed'))) }); if (w.regToken !== undefined && url.endsWith('since=' + w.regToken)) return fetchOk({ unchanged: true, token: w.regToken }); return fetchOk({ registrations: (w.daemonRegistered ?? w.registered) ? [{ session_id: SID }] : [], ...(w.regToken === undefined ? {} : { token: w.regToken }) }) } w.fetches.push({ url, init }); return (w.http.shift() ?? (() => { throw new Error('unplanned fetch') }))() } },
    process: { run: async (argv: string[], init: any) => { w.runs.push({ argv, init }); return (w.cli.shift() ?? (() => { throw new Error('unplanned run') }))() } },
    prompt: { submit: (a: { text: string }) => { w.submits.push(a.text); return w.submit(a.text) } },
    state: {
      get: async (k: { key: string }) => ({ value: w.store.get(k.key) }),
      set: async (k: { key: string }, v: unknown) => {
        if (w.stateHangs) return new Promise<void>(() => {})
        if (w.stateFails) throw new Error('state write refused')
        w.store.set(k.key, v)
      },
    },
    ui: { toast: async (text: string, init: any) => { w.toasts.push({ text, init }) } },
    plugin: { root: 'root' in over ? over.root : ROOT },
  }
  const first: Timer = { ms: 5000, fn: () => {}, cancelled: false, cancel: () => { first.cancelled = true } }
  w.timers.push(first)
  const ctx = {
    env: { home: '/ah', version: 'mod' },
    busy: { at: null as number | null },
    hier: () => ('hier' in over ? over.hier ?? null : '/work/repo/.claude/ah2'),
    link: { socket: null as string | null },
    handed: new Map<string, Handed>(),
    st: { state: null as string | null, generation: 0, push: false, running: false, failures: 0, notBefore: 0, timer: first as Timer | null },
    notes: { toastMs: over.toastMs ?? 10000, lost: null as string | null },
  }
  const cycle = () => mailCycle($, ctx)
  // Runs one CLI cycle whose answer names the daemon, leaving the rig connected with its next request held open.
  const connect = async (a: Record<string, unknown> = {}) => {
    const open = held()
    w.cli.push(() => cliOk(answer({ daemon: { socket: SOCK }, ...a })))
    w.http.push(() => open.promise)
    await cycle()
    await settle()
    return open
  }
  return { w, $, ctx, cycle, connect }
}

const sent = (call: { init: any }) => JSON.parse(call.init.stdin)
const queryOf = (url: string) => JSON.parse(decodeURIComponent(/[?&]in=([^&]*)/.exec(url)![1]))

test('CLI state: one bin/ah msg due --json with the request on stdin and no cwd or env option', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(answer()))

  await r.cycle()

  expect(r.w.runs.length).toBe(1)
  expect(r.w.runs[0].argv).toEqual(CLI)
  expect(Object.keys(r.w.runs[0].init).sort()).toEqual(['stdin', 'timeoutMs'])
  expect(r.w.runs[0].init.timeoutMs).toBe(10000)
  expect(sent(r.w.runs[0])).toEqual({ session: SID, cwd: '/work/repo', hierarchy_dir: '/work/repo/.claude/ah2', now: NOW, busy_at: null, poll_ms: 5000, mod_build: 1, handed: {}, state: null })
  expect(r.w.fetches).toEqual([])
})

test('negative: a session with no registration runs nothing, connects to nothing and submits nothing', async () => {
  const r = rig({ registered: false })

  await r.cycle()

  expect(r.w.runs).toEqual([])
  expect(r.w.fetches).toEqual([])
  expect(r.w.submits).toEqual([])
  expect(r.ctx.st.running).toBe(false)
})

test('with a socket the daemon decides a session is not registered, whatever a file says, asked once a period', async () => {
  const r = rig()
  r.ctx.link.socket = SOCK
  r.w.daemonRegistered = false

  await r.cycle()
  await r.cycle()

  expect(r.w.asks).toBe(1)
  expect(r.w.runs).toEqual([])
})

test('with the daemon down the last answer stands', async () => {
  const r = rig({ registered: false })
  r.ctx.link.socket = SOCK
  r.w.daemonRegistered = true
  r.w.cli.push(() => cliOk(answer({ daemon: null })))

  await r.cycle()
  expect(r.w.runs.length).toBe(1)

  r.w.daemonDown = true
  r.w.now += 5000
  r.ctx.link.socket = SOCK
  r.ctx.st.notBefore = 0
  r.w.cli.push(() => cliOk(answer({ daemon: null })))
  await r.cycle()

  expect(r.w.asks).toBe(2)
  expect(r.w.runs.length).toBe(2)
})

test('an unchanged registrations answer keeps the rows held; negative: a moved token sends new rows', async () => {
  const r = rig()
  r.ctx.link.socket = SOCK
  r.w.daemonRegistered = false
  r.w.regToken = 7

  await r.cycle()
  r.w.daemonRegistered = true // a row the daemon would send, but its counter has not moved
  r.w.now += 5000
  await r.cycle()

  expect(r.w.regUrls).toEqual(['http://ahd/v1/inspect/registrations?since=-1', 'http://ahd/v1/inspect/registrations?since=7'])
  expect(r.ctx.st.registered).toBe(false)
  expect(r.ctx.link.rows).toBe(0)

  r.w.regToken = 8
  r.w.now += 5000
  await r.cycle()

  expect(r.w.regUrls[2]).toBe('http://ahd/v1/inspect/registrations?since=7')
  expect(r.ctx.st.registered).toBe(true)
  expect(r.ctx.link.regToken).toBe(8)
})

test('a CLI answer naming another daemon drops the registrations token; negative: the same daemon keeps it', async () => {
  const kept = async (before: string) => {
    const r = rig()
    r.ctx.link.socket = before
    ;(r.ctx.link as any).regToken = 7
    ;(r.ctx.st as any).regAt = r.w.now // no registrations ask in this cycle
    await r.connect()
    expect(r.ctx.link.socket).toBe(SOCK)
    return (r.ctx.link as any).regToken
  }

  expect(await kept('/old.sock')).toBeUndefined()
  expect(await kept(SOCK)).toBe(7)
})

test('a registrations read the daemon never answers is not asked again until it settles, past its time limit too', async () => {
  const r = rig({ registered: false })
  r.ctx.link.socket = SOCK
  r.w.regHangs = true

  const first = r.cycle()
  await settle()
  expect(r.w.asks).toBe(1)
  // The read's 2000 ms limit runs out: the cycle ends as with the daemon down.
  r.w.limits.find((t) => !t.cancelled)!.fn()
  await first

  r.w.now += 5000
  await r.cycle()
  expect(r.w.asks).toBe(1)

  // negative: once the open read settles, the next period asks again.
  r.w.regClose.shift()!()
  await settle()
  r.w.regHangs = false
  r.w.now += 5000
  await r.cycle()
  expect(r.w.asks).toBe(2)
})

test('negative: before any answer no daemon is asked', async () => {
  const r = rig({ registered: false })

  await r.cycle()

  expect(r.w.asks).toBe(0)
})

test('a mark with no registration file runs the CLI, takes the socket and delivers mail', async () => {
  const r = rig()
  const stat = r.$.fs.stat
  const looked: string[] = []
  r.$.fs.stat = (path: string) => { looked.push(path); return stat(path) }
  const a = { ...withMail(['a1']), daemon: { socket: SOCK } }
  r.w.cli.push(() => cliOk(a), () => cliOk(a))
  r.w.http.push(() => held().promise)

  await r.cycle()
  await settle()

  expect(looked).toEqual(['/ah/session-marks/' + SID])
  expect(r.w.runs.length).toBe(2)
  expect(r.ctx.link.socket).toBe(SOCK)
  expect(r.w.submits).toEqual([a.prompt])
})

test('negative: a stale mark with a daemon that does not list the session submits nothing', async () => {
  const r = rig()
  r.ctx.link.socket = SOCK
  r.w.daemonRegistered = false
  r.w.cli.push(() => cliOk(withMail(['a1'])))

  await r.cycle()

  expect(r.w.runs).toEqual([])
  expect(r.w.submits).toEqual([])
})

test('the hierarchy dir is sent as null when the session has no checkout', async () => {
  const r = rig({ hier: null })
  r.w.cli.push(() => cliOk(answer()))

  await r.cycle()

  expect(sent(r.w.runs[0]).hierarchy_dir).toBeNull()
})

for (const root of [undefined, '', 'relative/dir', './x', 42, null]) {
  test(`negative: the plugin directory ${JSON.stringify(root) ?? 'absent'} is not absolute: no process, nothing handed over`, async () => {
    const r = rig({ root })
    r.w.cli.push(() => cliOk(withMail(['e1'])))

    await r.cycle()

    expect(r.w.runs).toEqual([])
    expect(r.w.submits).toEqual([])
    expect(r.ctx.st.push).toBe(false)
  })
}

test('an answer with mail marks every id pending and submits its prompt byte for byte', async () => {
  const r = rig()
  const a = withMail(['a1', 'b2'])
  r.w.cli.push(() => cliOk(a))

  await r.cycle()

  expect(r.w.submits).toEqual([a.prompt])
  expect([...r.ctx.handed.entries()]).toEqual([['a1', { state: 'pending', at: NOW }], ['b2', { state: 'pending', at: NOW }]])
  expect(r.ctx.st.state).toBe('S1')
})

test('right before the submit the cycle asks again, and submits only what the second answer still lists', async () => {
  const r = rig()
  const first = withMail(['a1', 'b2'])
  const second = withMail(['b2'])
  r.w.cli.push(() => cliOk(first))
  r.w.cli.push(() => cliOk(second))

  await r.cycle()

  expect(r.w.runs.length).toBe(2)
  expect(r.w.submits).toEqual([second.prompt])
  expect([...r.ctx.handed.keys()]).toEqual(['b2'])
})

test('a second answer with nothing due submits nothing and hands nothing over', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(withMail(['a1'])))
  r.w.cli.push(() => cliOk(answer({ acked: ['a1'] })))

  await r.cycle()

  expect(r.w.submits).toEqual([])
  expect(r.ctx.handed.size).toBe(0)
})

test('negative: a second answer that cannot be read keeps the first and still submits it', async () => {
  const r = rig()
  const first = withMail(['a1'])
  r.w.cli.push(() => cliOk(first))
  r.w.cli.push(() => ({ exitCode: 1, stdout: '', stderr: 'boom' }))

  await r.cycle()

  expect(r.w.submits).toEqual([first.prompt])
})

test('a doorbell ring is submitted from its one answer: the second ask would find it already claimed', async () => {
  const r = rig()
  const line = 'ah: You have 1 message (1 report). Run `ah msg next`.'
  r.w.cli.push(() => cliOk(answer({ ids: ['doorbell:15'], prompt: line, ring: true })))

  await r.cycle()
  await settle()

  expect(r.w.runs.length).toBe(1)
  expect(r.w.submits).toEqual([line])
  expect(r.ctx.busy.at).toBe(NOW)
  expect(r.ctx.handed.get('doorbell:15')?.state).toBe('resolved')
})

test('negative: an answer with ring false is asked again like any mail, so an acked entry is not submitted', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(withMail(['a1'], { ring: false })))
  r.w.cli.push(() => cliOk(answer({ acked: ['a1'] })))

  await r.cycle()

  expect(r.w.runs.length).toBe(2)
  expect(r.w.submits).toEqual([])
})

test('negative: an answer with no mail asks nothing a second time', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(answer()))

  await r.cycle()

  expect(r.w.runs.length).toBe(1)
})

test('a submit that resolves moves its records to resolved at the time it resolved', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(withMail(['a1'])))
  r.w.submit = async () => { r.w.now = NOW + 7000; return {} }

  await r.cycle()
  await settle()

  expect(r.ctx.handed.get('a1')).toEqual({ state: 'resolved', at: NOW + 7000 })
})

test('negative: a submit that is refused puts every record back, and a record that already existed back as it was', async () => {
  const r = rig()
  r.ctx.handed.set('old', { state: 'resolved', at: NOW - 70_000 })
  r.w.cli.push(() => cliOk(withMail(['old', 'new'])))
  r.w.submit = () => Promise.reject(new Error('refused'))

  await r.cycle()
  await settle()

  expect([...r.ctx.handed.entries()]).toEqual([['old', { state: 'resolved', at: NOW - 70_000 }]])
})

test('a resolve that arrives after a later mark of the same id does not overwrite it', async () => {
  const r = rig()
  const gate = held()
  r.w.cli.push(() => cliOk(withMail(['a1'])))
  r.w.submit = () => gate.promise as Promise<unknown>

  await r.cycle()
  const later = { state: 'pending' as const, at: NOW + 1 }
  r.ctx.handed.set('a1', later)
  gate.release({})
  await settle()

  expect(r.ctx.handed.get('a1')).toBe(later)
})

test('the answer listing idle sets the busy flag only when something is submitted', async () => {
  const quiet = rig()
  quiet.w.cli.push(() => cliOk(answer({ idle: true })))
  await quiet.cycle()
  expect(quiet.ctx.busy.at).toBeNull()

  const mail = rig()
  mail.w.cli.push(() => cliOk(withMail(['a1'], { idle: true })))
  await mail.cycle()
  expect(mail.ctx.busy.at).toBe(NOW)

  const busy = rig()
  busy.ctx.busy.at = NOW - 1000
  busy.w.cli.push(() => cliOk(withMail(['a1'], { idle: false })))
  await busy.cycle()
  expect(busy.ctx.busy.at).toBe(NOW - 1000)
})

test('the release: ids the answer lists as acked leave handed, whatever their state, and nothing else does', async () => {
  const r = rig()
  r.ctx.handed.set('done', { state: 'resolved', at: NOW - 1 })
  r.ctx.handed.set('pend', { state: 'pending', at: NOW - 1 })
  r.ctx.handed.set('keep', { state: 'resolved', at: NOW - 1 })
  r.w.cli.push(() => cliOk(answer({ acked: ['done', 'pend', 'unknown'] })))

  await r.cycle()

  expect([...r.ctx.handed.keys()]).toEqual(['keep'])
})

test('negative: nothing is released by an id missing from the answer, and a refused answer releases nothing', async () => {
  const r = rig()
  r.ctx.handed.set('a1', { state: 'resolved', at: NOW - 1 })
  r.w.cli.push(() => cliOk(answer()))
  r.w.cli.push(() => ({ exitCode: 1, stdout: '', stderr: 'boom' }))

  await r.cycle()
  await r.cycle()

  expect([...r.ctx.handed.keys()]).toEqual(['a1'])
})

test('the request carries the handed map and the state of the last answer', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(withMail(['a1'], { state: 'S1' })))
  r.w.cli.push(() => cliOk(withMail(['a1'], { state: 'S1' })))
  r.w.cli.push(() => cliOk(answer({ state: 'S2' })))

  await r.cycle()
  r.w.now = NOW + 5000
  await r.cycle()

  expect(sent(r.w.runs[2]).handed).toEqual({ a1: ['resolved', NOW] })
  expect(sent(r.w.runs[2]).state).toBe('S1')
  expect(r.ctx.st.state).toBe('S2')
})

// ---- §3.6: nothing is marked or submitted, the state does not move, nothing is shown
const BAD = [
  ['a CLI that exits non-zero', () => ({ exitCode: 3, stdout: JSON.stringify(withMail(['a1'])), stderr: 'x' })],
  ['a CLI that cannot be run', () => { throw new Error('spawn failed') }],
  ['stdout that is not JSON', () => ({ exitCode: 0, stdout: '{"v":1,"ids":["a1"', stderr: '' })],
  ['another version', () => cliOk(withMail(['a1'], { v: 2 }))],
  ['a missing field', () => { const a: any = withMail(['a1']); delete a.acked; return cliOk(a) }],
  ['ids that are not strings', () => cliOk(withMail(['a1'], { ids: [1] }))],
  ['a prompt with no ids', () => cliOk(answer({ prompt: 'x' }))],
  ['ids with no prompt', () => cliOk(answer({ ids: ['a1'] }))],
  ['a daemon with a relative socket', () => cliOk(answer({ daemon: { socket: 'ahd.sock' } }))],
  ['an array', () => cliOk([])],
] as [string, () => unknown][]
for (const [label, reply] of BAD) {
  test(`negative: ${label} changes nothing and says nothing`, async () => {
    const r = rig()
    r.ctx.handed.set('x', { state: 'resolved', at: NOW - 1 })
    r.ctx.st.state = 'S0'
    r.w.cli.push(reply)

    await r.cycle()

    expect(r.w.submits).toEqual([])
    expect([...r.ctx.handed.keys()]).toEqual(['x'])
    expect(r.ctx.st.state).toBe('S0')
    expect(r.ctx.st.push).toBe(false)
    expect(r.ctx.link.socket).toBeNull()
    expect(r.ctx.st.timer?.cancelled).toBe(false)
  })
}

test('a CLI cycle that is still running makes a tick do nothing', async () => {
  const r = rig()
  const gate = held()
  r.w.cli.push(() => gate.promise)

  const slow = r.cycle()
  await r.cycle()
  await settle()
  expect(r.w.runs.length).toBe(1)
  gate.release(cliOk(answer()))
  await slow
})

// ---- CLI -> push
test('a CLI answer that names no daemon stays in CLI state with the timer running', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(answer()))

  await r.cycle()

  expect(r.ctx.st.push).toBe(false)
  expect(r.ctx.link.socket).toBeNull()
  expect(r.ctx.st.timer?.cancelled).toBe(false)
  expect(r.w.fetches).toEqual([])
})

test('a CLI answer that names a daemon cancels the timer and sends the next request as a GET on that socket at once', async () => {
  const r = rig()
  const open = await r.connect({ state: 'S9' })

  expect(r.ctx.st.push).toBe(true)
  expect(r.ctx.link.socket).toBe(SOCK)
  expect(r.w.timers[0].cancelled).toBe(true)
  expect(r.ctx.st.timer).toBeNull()
  expect(r.w.runs.length).toBe(1)
  expect(r.w.fetches.length).toBe(1)
  const f = r.w.fetches[0]
  expect(f.url.startsWith('http://ahd/v1/due?in=')).toBe(true)
  expect(f.url.endsWith('&wait=25')).toBe(true)
  expect(f.init).toEqual({ method: 'GET', headers: { 'Ah-Client-Version': 'mod' }, socketPath: SOCK })
  expect(queryOf(f.url)).toMatchObject({ session: SID, state: 'S9', poll_ms: 5000, handed: {} })
  open.release(fetchOk(answer()))
})

test('while connected a tick makes no process and no second request', async () => {
  const r = rig()
  const open = await r.connect()

  r.w.timers[0].fn()
  await settle()

  expect(r.w.runs.length).toBe(1)
  expect(r.w.fetches.length).toBe(1)
  open.release(fetchOk(answer()))
})

test('a push answer is handled and the next request goes out at once, with no process', async () => {
  const r = rig()
  const first = await r.connect()
  const recheck = withMail(['p1'], { state: 'S5' })
  r.w.http.push(() => fetchOk(recheck))
  const second = held()
  r.w.http.push(() => second.promise)

  first.release(fetchOk(withMail(['p1'], { state: 'S5' })))
  await settle()

  expect(r.w.submits).toEqual([recheck.prompt])
  expect(r.w.fetches.length).toBe(3)
  expect(r.w.fetches[1].url).not.toContain('wait=')
  expect(queryOf(r.w.fetches[2].url).handed).toEqual({ p1: ['resolved', NOW] })
  expect(queryOf(r.w.fetches[2].url).state).toBe('S5')
  expect(r.w.runs.length).toBe(1)
  second.release(fetchOk(answer()))
})

test('a push answer that names a daemon is not acted on: no socket change and no state change from it', async () => {
  const r = rig()
  const first = await r.connect()
  const second = held()
  r.w.http.push(() => second.promise)

  first.release(fetchOk(answer({ daemon: { socket: '/other.sock' }, state: 'S2' })))
  await settle()

  expect(r.ctx.link.socket).toBe(SOCK)
  expect(r.w.fetches[1].init.socketPath).toBe(SOCK)
  second.release(fetchOk(answer()))
})

// ---- push -> CLI, each cause
const DISCONNECTS = [
  ['a fetch that rejects', 'fail', new Error('connect ENOENT')],
  ['the host aborting a fetch after 30 s', 'fail', new Error('aborted')],
  ['a non-200 answer', 'ok', { ok: false, status: 503, text: '' }],
  ['an answer that is not the version 1 shape', 'ok', fetchOk({ v: 1, ids: 'x' })],
  ['an answer that is not JSON', 'ok', { ok: true, status: 200, text: '<html>' }],
] as [string, 'fail' | 'ok', unknown][]
for (const [label, how, payload] of DISCONNECTS) {
  test(`${label} goes back to CLI state, bumps the generation and starts the timer again`, async () => {
    const r = rig()
    const open = await r.connect()
    r.ctx.handed.set('x', { state: 'resolved', at: NOW - 1 })

    if (how === 'fail') open.fail(payload)
    else open.release(payload)
    await settle()

    expect(r.ctx.st.push).toBe(false)
    expect(r.ctx.st.generation).toBe(1)
    expect(r.ctx.st.timer).not.toBeNull()
    expect(r.ctx.st.timer?.cancelled).toBe(false)
    expect(r.w.timers.length).toBe(2)
    expect(r.w.timers[1].ms).toBe(5000)
    expect([...r.ctx.handed.keys()]).toEqual(['x'])
    expect(r.w.submits).toEqual([])
  })
}

test('a request that rejects (the host abort) is a disconnect and the next tick runs the CLI', async () => {
  const r = rig()
  const open = await r.connect()

  open.fail(new Error('aborted'))
  await settle()
  r.w.now = NOW + 5000
  r.w.cli.push(() => cliOk(withMail(['c1'])))
  r.w.cli.push(() => cliOk(withMail(['c1'])))
  r.w.timers[1].fn()
  await settle()

  expect(r.ctx.st.push).toBe(false)
  expect(r.w.runs.length).toBe(3)
  expect(r.w.submits.length).toBe(1)
})

test('a registration that goes away while connected is a disconnect, and nothing is requested', async () => {
  const r = rig()
  const open = await r.connect()
  open.release(fetchOk(answer()))
  const next = held()
  r.w.http.push(() => next.promise)
  await settle()
  expect(r.w.fetches.length).toBe(2)

  r.w.registered = false
  r.w.now += 5000
  next.release(fetchOk(answer()))
  await settle()

  expect(r.w.fetches.length).toBe(2)
  expect(r.ctx.st.push).toBe(false)
  expect(r.ctx.st.timer).not.toBeNull()
})

// ---- backoff
test('the reconnect backoff is 5, 10, 20, 40, then 60 s, and a connection that answers resets it', async () => {
  const r = rig()
  const waits: number[] = []
  for (let i = 0; i < 6; i++) {
    r.w.now += 100_000
    const open = await r.connect()
    open.fail(new Error('down'))
    await settle()
    waits.push(r.ctx.st.notBefore - r.w.now)
  }
  expect(waits).toEqual([5000, 10000, 20000, 40000, 60000, 60000])

  r.w.now += 100_000
  const good = await r.connect()
  const again = held()
  r.w.http.push(() => again.promise)
  good.release(fetchOk(answer()))
  await settle()
  expect(r.ctx.st.failures).toBe(0)
  again.fail(new Error('down'))
  await settle()
  expect(r.ctx.st.notBefore - r.w.now).toBe(5000)
})

test('negative: a CLI answer inside the backoff names a daemon but does not connect; the timer keeps running', async () => {
  const r = rig()
  r.ctx.st.notBefore = NOW + 5000
  r.w.cli.push(() => cliOk(answer({ daemon: { socket: SOCK } })))

  await r.cycle()

  expect(r.ctx.st.push).toBe(false)
  expect(r.ctx.link.socket).toBe(SOCK)
  expect(r.w.timers[0].cancelled).toBe(false)
  expect(r.w.fetches).toEqual([])
})

test('a CLI answer after the backoff connects', async () => {
  const r = rig()
  r.ctx.st.notBefore = NOW
  const open = await r.connect()

  expect(r.ctx.st.push).toBe(true)
  open.release(fetchOk(answer()))
})

// ---- discards
test('a late answer from an older generation is discarded: no marks, no submit, no state, no release', async () => {
  const r = rig()
  const open = await r.connect()
  r.ctx.handed.set('x', { state: 'resolved', at: NOW - 1 })
  r.ctx.st.state = 'S0'

  r.ctx.st.generation++
  open.release(fetchOk(withMail(['late'], { acked: ['x'], state: 'S7' })))
  await settle()

  expect(r.w.submits).toEqual([])
  expect([...r.ctx.handed.keys()]).toEqual(['x'])
  expect(r.ctx.st.state).toBe('S0')
})

test('an answer that arrives after the busy flag changed is discarded, and in push state the next request goes out at once', async () => {
  const r = rig()
  const open = await r.connect()
  const next = held()
  r.w.http.push(() => next.promise)
  r.ctx.handed.set('x', { state: 'resolved', at: NOW - 1 })

  r.ctx.busy.at = NOW + 1
  open.release(fetchOk(withMail(['m1'], { acked: ['x'] })))
  await settle()

  expect(r.w.submits).toEqual([])
  expect([...r.ctx.handed.keys()]).toEqual(['x'])
  expect(r.w.fetches.length).toBe(2)
  next.release(fetchOk(answer()))
})

test('a CLI answer that arrives after the busy flag changed is discarded and waits for the next tick', async () => {
  const r = rig()
  const gate = held()
  r.w.cli.push(() => gate.promise)

  const slow = r.cycle()
  await settle()
  r.ctx.busy.at = NOW + 1
  gate.release(cliOk(withMail(['m1'])))
  await slow

  expect(r.w.submits).toEqual([])
  expect(r.w.runs.length).toBe(1)
  expect(r.ctx.st.running).toBe(false)
})

const RUNG = 'ah: You have 1 alarm. Run `ah msg next`.'

test('F1: a ring that arrives after a turn began is still submitted, and that turn keeps its busy mark', async () => {
  const r = rig()
  const gate = held()
  r.w.cli.push(() => gate.promise)

  const slow = r.cycle()
  await settle()
  r.ctx.busy.at = NOW + 1
  gate.release(cliOk(answer({ ids: ['doorbell:7'], prompt: RUNG, ring: true })))
  await slow
  await settle()

  expect(r.w.submits).toEqual([RUNG])
  expect(r.ctx.busy.at).toBe(NOW + 1)
  expect(r.w.runs.length).toBe(1)
})

test('F1: a ring in the second answer is submitted though a turn began during that ask', async () => {
  const r = rig()
  const gate = held()
  r.w.cli.push(() => cliOk(withMail(['a1'])))
  r.w.cli.push(() => gate.promise)

  const slow = r.cycle()
  await settle()
  r.ctx.busy.at = NOW + 1
  gate.release(cliOk(answer({ ids: ['doorbell:7'], prompt: RUNG, ring: true })))
  await slow
  await settle()

  expect(r.w.submits).toEqual([RUNG])
  expect(r.ctx.busy.at).toBe(NOW + 1)
})

test('negative: mail in the second answer is discarded when a turn began during that ask', async () => {
  const r = rig()
  const gate = held()
  r.w.cli.push(() => cliOk(withMail(['a1'])))
  r.w.cli.push(() => gate.promise)

  const slow = r.cycle()
  await settle()
  r.ctx.busy.at = NOW + 1
  gate.release(cliOk(withMail(['a1'])))
  await slow

  expect(r.w.submits).toEqual([])
})

test('F2: a refused submit puts the busy mark back, so the idle session can be rung again', async () => {
  const r = rig()
  r.w.submit = () => Promise.reject(new Error('refused'))
  r.w.cli.push(() => cliOk(answer({ ids: ['doorbell:7'], prompt: RUNG, ring: true })))

  await r.cycle()
  await settle()

  expect(r.ctx.busy.at).toBeNull()
  expect(r.ctx.handed.size).toBe(0)
})

test('a ring the engine drops after a Stop is not delivered, and the next ask says the session is held', async () => {
  const r = rig()
  r.w.submit = async () => ({ drop: 'the session was stopped and nobody has sent input since' })
  r.w.cli.push(() => cliOk(answer({ ids: ['doorbell:7'], prompt: RUNG, ring: true })))

  await r.cycle()
  await settle()

  expect(r.ctx.busy.at).toBeNull()
  expect(r.ctx.handed.size).toBe(0)
  expect(r.ctx.st.held).toBe(true)
  r.w.cli.push(() => cliOk(answer()))
  await r.cycle()
  expect(sent(r.w.runs.at(-1)!).held).toBe(true)
})

test('negative: another drop is not delivered either, but does not hold the doorbell', async () => {
  const r = rig()
  r.w.submit = async () => ({ drop: 'a hook answered without passing the prompt on' })
  r.w.cli.push(() => cliOk(answer({ ids: ['doorbell:7'], prompt: RUNG, ring: true })))

  await r.cycle()
  await settle()

  expect(r.ctx.busy.at).toBeNull()
  expect(r.ctx.handed.size).toBe(0)
  expect(r.ctx.st.held).toBe(false)
})

test('negative: a prompt that enters holds nothing and stays handed over', async () => {
  const r = rig()
  r.w.submit = async () => ({ text: RUNG })
  r.w.cli.push(() => cliOk(answer({ ids: ['doorbell:7'], prompt: RUNG, ring: true })))

  await r.cycle()
  await settle()

  expect(r.ctx.busy.at).not.toBeNull()
  expect(r.ctx.handed.get('doorbell:7')?.state).toBe('resolved')
  expect(r.ctx.st.held).toBeUndefined()
})

test('F2: a submit that throws puts the busy mark back', async () => {
  const r = rig()
  r.w.submit = () => { throw new Error('refused') }
  r.w.cli.push(() => cliOk(answer({ ids: ['doorbell:7'], prompt: RUNG, ring: true })))

  await r.cycle()
  await settle()

  expect(r.ctx.busy.at).toBeNull()
})

test('negative: a refused submit leaves the mark of a turn that began after it', async () => {
  const r = rig()
  const gate = held()
  r.w.submit = () => gate.promise
  r.w.cli.push(() => cliOk(answer({ ids: ['doorbell:7'], prompt: RUNG, ring: true })))

  await r.cycle()
  expect(r.ctx.busy.at).toBe(NOW)
  r.ctx.busy.at = NOW + 1
  gate.fail(new Error('refused'))
  await settle()

  expect(r.ctx.busy.at).toBe(NOW + 1)
})

// ---- the request-line budget
test('a request whose encoded input is over 7 KiB goes through the CLI and stays connected; the next fitting one is a GET again', async () => {
  const r = rig()
  const open = await r.connect()
  const ids = Array.from({ length: 300 }, (_, i) => 'entry-' + String(i).padStart(4, '0') + '-with-a-long-enough-id')
  for (const id of ids) r.ctx.handed.set(id, { state: 'resolved', at: NOW - 1000 })
  const next = held()
  r.w.cli.push(() => cliOk(answer({ acked: ids, daemon: { socket: '/other.sock' }, state: 'S3' })))
  r.w.http.push(() => next.promise)

  open.release(fetchOk(answer()))
  await settle()
  await settle()

  expect(r.w.runs.length).toBe(2)
  expect(r.w.runs[1].argv).toEqual(CLI)
  expect(r.ctx.st.push).toBe(true)
  expect(r.ctx.link.socket).toBe(SOCK)
  expect(r.ctx.handed.size).toBe(0)
  expect(r.w.fetches.length).toBe(2)
  expect(r.w.fetches[1].init.socketPath).toBe(SOCK)
  expect(queryOf(r.w.fetches[1].url).state).toBe('S3')
  next.release(fetchOk(answer()))
})

test('negative: an input just inside 7 KiB is still a GET', async () => {
  const r = rig()
  const open = await r.connect()
  let n = 0
  const room = () => encodeURIComponent(JSON.stringify({ session: SID, cwd: '/work/repo', hierarchy_dir: '/work/repo/.claude/ah2', now: NOW, busy_at: null, poll_ms: 5000, mod_build: 1, handed: Object.fromEntries([...r.ctx.handed].map(([k, v]) => [k, [v.state, v.at]])), state: 'S1' })).length
  while (room() < 7168 - 120) r.ctx.handed.set('id-' + String(n++).padStart(5, '0'), { state: 'resolved', at: NOW })
  const next = held()
  r.w.http.push(() => next.promise)
  r.ctx.st.state = 'S1'

  open.release(fetchOk(answer()))
  await settle()

  expect(r.w.runs.length).toBe(1)
  expect(r.w.fetches.length).toBe(2)
  expect(r.w.fetches[1].url.length).toBeLessThan(7168 + 120)
  next.release(fetchOk(answer()))
})

// ---- the answer reader
test('readDue accepts exactly the version 1 shape', () => {
  expect(readDue(answer())).toEqual({ ids: [], prompt: null, compact: null, idle: true, state: 'S1', acked: [], socket: null, ring: false })
  expect(readDue(answer({ ring: true }))?.ring).toBe(true)
  expect(readDue(answer({ ring: 'true' }))?.ring).toBe(false)
  expect(readDue(withMail(['a'], { daemon: { socket: '/s.sock' } }))?.socket).toBe('/s.sock')
  expect(readDue(answer({ daemon: { socket: 'C:\\run\\ahd.sock' } }))?.socket).toBe('C:\\run\\ahd.sock')
  for (const bad of [null, 'x', 3, [], {}, answer({ v: '1' }), answer({ idle: 'yes' }), answer({ state: 1 }), answer({ acked: [1] }), answer({ daemon: 'x' }), answer({ daemon: {} }), answer({ daemon: { socket: '' } }), answer({ daemon: { socket: './x' } }), withMail(['a'], { prompt: '' })]) {
    expect(readDue(bad)).toBeNull()
  }
})

// ---- the compact prompt, the record and the toast
const COMPACT = 'ah mail for imp@tm (1):\n- brief from orchestrator@tm\n[ah-entry e1]'
const FULL = 'ah mail for imp@tm (1):\n- brief from orchestrator@tm: [hierarchy-msg /m/e1--request.md]\n  Reply when done: ah msg send orchestrator@tm --ref <your response path>\n[ah-entry e1]'
const compactMail = (over: Record<string, unknown> = {}) => answer({ ids: ['e1'], prompt: FULL, compact: COMPACT, ...over })
const records = (r: ReturnType<typeof rig>) => r.w.store.get('mail') as any[]

test('compact: the short form is what is submitted, and the record keeps the full text', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(compactMail()))

  await r.cycle()
  await settle()

  expect(r.w.submits).toEqual([COMPACT])
  expect(records(r)).toEqual([{ at: NOW, ids: ['e1'], address: 'imp@tm', text: FULL, state: 'delivered' }])
  expect(r.w.toasts).toEqual([{ text: 'Mail from orchestrator@tm (brief). Full text: /wrangl-hierarchy-pane', init: { timeoutMs: 10000 } }])
})

test('negative: a named file with a readable mtime leaves the record without the field', async () => {
  const r = rig()
  r.w.mtimes['/m/e1--request.md'] = 1234
  r.w.cli.push(() => cliOk(compactMail()))

  await r.cycle()
  await settle()

  expect(records(r)).toEqual([{ at: NOW, ids: ['e1'], address: 'imp@tm', text: FULL, state: 'delivered' }])
})

test('negative: a named file with no readable mtime leaves the record without the field', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(compactMail()))

  await r.cycle()
  await settle()

  expect('files' in records(r)[0]).toBe(false)
})

test('negative: a prompt that names no file leaves the record without the field', async () => {
  const r = rig()
  r.w.mtimes['/m/e1--request.md'] = 1234
  r.w.cli.push(() => cliOk(answer({ ids: ['e1'], prompt: COMPACT })))

  await r.cycle()
  await settle()

  expect('files' in records(r)[0]).toBe(false)
})

test('negative: with no compact (an old daemon, or null) the full prompt is submitted', async () => {
  for (const over of [{ compact: undefined }, { compact: null }, { compact: 7 }]) {
    const r = rig()
    r.w.cli.push(() => cliOk(compactMail(over)))
    await r.cycle()
    await settle()
    expect(r.w.submits).toEqual([FULL])
  }
})

test('a rejected submit records not delivered, toasts that it will be offered again and restores the claim', async () => {
  const r = rig()
  r.w.submit = () => Promise.reject(new Error('refused'))
  r.w.cli.push(() => cliOk(compactMail()))

  await r.cycle()
  await settle()

  expect(records(r)).toEqual([{ at: NOW, ids: ['e1'], address: 'imp@tm', text: FULL, state: 'not delivered' }])
  expect(r.w.toasts.map((t) => t.text)).toEqual(['Mail for imp@tm was not delivered; it will be offered again.'])
  expect(r.ctx.handed.has('e1')).toBe(false)
})

test('a submit that throws at once records not delivered too', async () => {
  const r = rig()
  r.w.submit = () => { throw new Error('boom') }
  r.w.cli.push(() => cliOk(compactMail()))

  await r.cycle()
  await settle()

  expect(records(r)[0].state).toBe('not delivered')
  expect(r.ctx.handed.has('e1')).toBe(false)
})

test('the record list keeps ten, newest first', async () => {
  const r = rig()
  for (let i = 0; i < 12; i++) {
    r.w.now = NOW + i
    r.w.cli.push(() => cliOk(compactMail({ ids: ['e' + i], prompt: FULL.replace(/e1/g, 'e' + i), compact: COMPACT.replace(/e1/g, 'e' + i), state: 'S' + i })))
    await r.cycle()
    await settle()
    r.ctx.handed.clear()
  }

  const list = records(r)
  expect(list.length).toBe(10)
  expect(list[0].ids).toEqual(['e11'])
  expect(list[9].ids).toEqual(['e2'])
})

test('a state write that throws still submits the mail, marks the section unavailable and toasts twice, the second naming the inbox line', async () => {
  const r = rig()
  r.w.stateFails = true
  r.w.cli.push(() => cliOk(compactMail()))

  await r.cycle()
  await settle()

  expect(r.w.submits).toEqual([COMPACT])
  expect(r.ctx.notes.lost).toBe('state write failed')
  expect(r.w.toasts.map((t) => t.text)).toEqual([
    'Mail from orchestrator@tm (brief). Full text: /wrangl-hierarchy-pane',
    'Mail text could not be saved for the pane. Full text: ah msg inbox --all --address imp@tm',
  ])
  expect(r.w.toasts[1].text).toContain('ah msg inbox --all --address')
  expect(r.ctx.handed.get('e1')?.state).toBe('resolved')
})

test('a state write that never settles still gets the first toast, and no second one', async () => {
  const r = rig()
  r.w.stateHangs = true
  r.w.cli.push(() => cliOk(compactMail()))

  await r.cycle()
  await settle()

  expect(r.w.submits).toEqual([COMPACT])
  expect(r.w.toasts.map((t) => t.text)).toEqual(['Mail from orchestrator@tm (brief). Full text: /wrangl-hierarchy-pane'])
})

test('a toast that never settles or throws does not hold the record back', async () => {
  for (const toast of [() => new Promise<void>(() => {}), () => { throw new Error('toast refused') }]) {
    const r = rig()
    ;(r.$ as any).ui.toast = async () => toast()
    r.w.cli.push(() => cliOk(compactMail()))

    await r.cycle()
    await settle()

    expect(records(r).length).toBe(1)
    expect(r.ctx.notes.lost).toBeNull()
  }
})

test('toast_seconds 0 shows no toast and still records', async () => {
  const r = rig({ toastMs: 0 })
  r.w.cli.push(() => cliOk(compactMail()))

  await r.cycle()
  await settle()

  expect(r.w.toasts).toEqual([])
  expect(records(r).length).toBe(1)
})

test('two entries toast the count and the first sender', async () => {
  const r = rig()
  const two = 'ah mail for imp@tm (2):\n- report from a@tm\n- note from b@tm\n[ah-entry e1,e2]'
  r.w.cli.push(() => cliOk(answer({ ids: ['e1', 'e2'], prompt: two.replace('report from a@tm', 'report from a@tm: x'), compact: two })))

  await r.cycle()
  await settle()

  expect(r.w.toasts[0].text).toBe('2 mails from a@tm and others. Full text: /wrangl-hierarchy-pane')
})

test('CLI state: a failing request backs the timer off 5, 10, 20 s and one answer ends the backoff', async () => {
  const r = rig()
  const refused = () => ({ exitCode: 1, stdout: '', stderr: 'refused' })
  r.w.cli.push(refused)
  await r.cycle()
  expect(r.w.runs.length).toBe(1)

  r.w.now = NOW + 4_999
  await r.cycle()
  expect(r.w.runs.length).toBe(1)

  r.w.now = NOW + 5_000
  r.w.cli.push(refused)
  await r.cycle()
  expect(r.w.runs.length).toBe(2)

  r.w.now = NOW + 14_999
  await r.cycle()
  expect(r.w.runs.length).toBe(2)

  r.w.now = NOW + 15_000
  r.w.cli.push(() => cliOk(answer()))
  await r.cycle()
  expect(r.w.runs.length).toBe(3)

  r.w.now = NOW + 15_001
  r.w.cli.push(() => cliOk(answer()))
  await r.cycle()
  expect(r.w.runs.length).toBe(4)
})

// The doorbell is an ordinary answer to the mod: its one line is the prompt and a synthetic id stands for the ring.
const BELL = 'ah: You have 3 messages (3 reports). Run `ah msg next`.'
const bell = (id: string, text = BELL) => answer({ ids: [id], prompt: text })

test('a doorbell answer submits its one line, with no entry marker, and hands its ring id over', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(bell('doorbell:1')))

  await r.cycle()

  expect(r.w.submits).toEqual([BELL])
  expect(BELL).not.toContain('[ah-entry')
  expect([...r.ctx.handed.entries()]).toEqual([['doorbell:1', { state: 'pending', at: NOW }]])
})

test('a single inline item is submitted as its entry text under its own id', async () => {
  const r = rig()
  const a = withMail(['e9'])
  r.w.cli.push(() => cliOk(a))

  await r.cycle()

  expect(r.w.submits).toEqual([a.prompt])
  expect(r.w.submits[0]).toContain('[ah-entry e9]')
})

test('a re-ring carries a new ring id and the earlier one leaves handed once the answer acks it', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(bell('doorbell:1')))
  await r.cycle()
  r.ctx.handed.set('doorbell:1', { state: 'resolved', at: NOW })

  r.w.now = NOW + 30_000
  r.w.cli.push(() => cliOk(bell('doorbell:2', 'ah: 2 new; 5 unread (5 reports). Run `ah msg next`.')))
  r.w.cli.push(() => cliOk(bell('doorbell:2', 'ah: 2 new; 5 unread (5 reports). Run `ah msg next`.')))
  await r.cycle()
  expect(r.w.submits[1]).toBe('ah: 2 new; 5 unread (5 reports). Run `ah msg next`.')

  r.w.now = NOW + 60_000
  r.w.cli.push(() => cliOk(answer({ acked: ['doorbell:1', 'doorbell:2'] })))
  await r.cycle()
  expect(r.ctx.handed.has('doorbell:1')).toBe(false)
  expect(r.ctx.handed.has('doorbell:2')).toBe(false)
})

test('negative: an answer that is not a doorbell and lists no ring id leaves the handed map alone', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(bell('doorbell:1')))
  await r.cycle()

  r.w.now = NOW + 6000
  r.w.cli.push(() => cliOk(answer()))
  await r.cycle()

  expect(r.ctx.handed.has('doorbell:1')).toBe(true)
})

const RING = 'ah: You have 2 messages (2 reports). Run `ah msg next`.'

test('tool call: a busy session asks with the tool carrier and gets the ring line back, marking nothing handed', async () => {
  const r = rig()
  r.ctx.busy.at = NOW - 5000
  r.w.cli.push(() => cliOk(answer({ ids: ['doorbell:3'], prompt: RING, idle: false })))

  const line = await toolRing(r.$, r.ctx)

  expect(line).toBe(RING)
  expect(sent(r.w.runs[0]).carrier).toBe('tool')
  expect(sent(r.w.runs[0]).busy_at).toBe(NOW - 5000)
  expect(r.ctx.handed.size).toBe(0)
  expect(r.w.submits).toEqual([])
})

test('negative: an idle session is never asked by a tool call', async () => {
  const r = rig()

  expect(await toolRing(r.$, r.ctx)).toBeNull()
  expect(r.w.runs).toEqual([])
})

test('negative: a second tool call within three seconds sends nothing', async () => {
  const r = rig()
  r.ctx.busy.at = NOW - 5000
  r.w.cli.push(() => cliOk(answer()))
  await toolRing(r.$, r.ctx)

  r.w.now = NOW + 2000
  r.w.cli.push(() => cliOk(answer({ ids: ['doorbell:3'], prompt: RING, idle: false })))

  expect(await toolRing(r.$, r.ctx)).toBeNull()
  expect(r.w.runs.length).toBe(1)

  r.w.now = NOW + 3500
  expect(await toolRing(r.$, r.ctx)).toBe(RING)
})

test('negative: a failing request or an unreadable answer gives no line and does not throw', async () => {
  const r = rig()
  r.ctx.busy.at = NOW - 5000
  r.w.cli.push(() => { throw new Error('boom') })
  expect(await toolRing(r.$, r.ctx)).toBeNull()

  r.w.now = NOW + 4000
  r.w.cli.push(() => cliOk({ v: 2 }))
  expect(await toolRing(r.$, r.ctx)).toBeNull()
})

test('tool call: a daemon-connected session asks the socket without waiting', async () => {
  const r = rig()
  r.ctx.busy.at = NOW - 5000
  r.ctx.st.push = true
  r.ctx.link.socket = SOCK
  r.w.http.push(() => fetchOk(answer({ ids: ['doorbell:3'], prompt: RING, idle: false })))

  expect(await toolRing(r.$, r.ctx)).toBe(RING)
  expect(r.w.fetches[0].url).not.toContain('wait=')
  expect(queryOf(r.w.fetches[0].url).carrier).toBe('tool')
})

test('escalation: a mail-unacked answer is shown once as a toast', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(answer({ escalation: { kind: 'mail-unacked', address: 'orchestrator@tm', text: 'orchestrator@tm has not read its doorbell after 3 re-rings; 4 unread.' } })))

  await r.cycle()

  expect(r.w.toasts.map((t) => t.text)).toContain('orchestrator@tm has not read its doorbell after 3 re-rings; 4 unread.')
})

test('negative: an answer without an escalation shows no escalation toast', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(answer()))

  await r.cycle()

  expect(r.w.toasts).toEqual([])
})

test('negative: a malformed escalation is ignored and the answer is still read', async () => {
  const r = rig()
  r.w.cli.push(() => cliOk(answer({ escalation: { kind: 7 } })))

  await r.cycle()

  expect(r.w.toasts).toEqual([])
  expect(r.ctx.st.state).toBe('S1')
})
