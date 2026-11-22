import { expect, test } from 'claude-code/testing'
import { ownPrompt, register, waitBound } from '../register.ts'

// A registry of the rows these tests need, in hooks/registry.json's shape.
const cmd = (name: string) => ({ type: 'command', command: `sh "\${CLAUDE_PLUGIN_ROOT}/bin/ah-hook" ${name}` })
const REGISTRY = JSON.stringify({
  hooks: {
    SessionStart: [{ matcher: 'startup|resume|clear|compact|fork', hooks: [cmd('sessionstart')] }],
    PreToolUse: [{ matcher: '*', hooks: [cmd('pretooluse-chain')] }],
    PostToolUse: [{ matcher: 'Bash', hooks: [cmd('posttooluse-commit-log')] }, { matcher: 'SendMessage', hooks: [cmd('posttooluse-peer-resolve')] }],
    Stop: [{ matcher: '*', hooks: [cmd('stop-peer-nudge')] }],
    UserPromptSubmit: [{ hooks: [cmd('userpromptsubmit-peer-tracking')] }],
  },
})

// Stages the engine beneath the plugin: each ah-hook run is logged in `ran` and answered by `answer(name, payload)`.
const stage = (on: any, answer: (name: string, payload: any) => unknown = () => undefined, registry = REGISTRY) => {
  const ran: { name: string; payload: any }[] = []
  const order: string[] = []
  on('fs.read', (_$: any, e: any) => (e.path.endsWith('/hooks/registry.json') ? { value: registry } : { deny: 'no file' }))
  on('process.run', async (_$: any, e: any) => {
    const name = e.argv[2]
    const payload = JSON.parse(e.init?.stdin ?? '{}')
    ran.push({ name, payload })
    order.push(name)
    const out: any = await answer(name, payload)
    if (out?.exitCode !== undefined) return { value: { exitCode: out.exitCode, stdout: '', stderr: 'failed', isStdoutTruncated: false, isStderrTruncated: false } }
    return { value: { exitCode: 0, stdout: out === undefined ? '' : JSON.stringify(out), stderr: '', isStdoutTruncated: false, isStderrTruncated: false } }
  })
  on('session.id', () => ({ value: 'sess-1' }))
  on('session.cwd', () => ({ value: '/work/repo' }))
  on('session.messages', () => ({ value: [] }))
  on('env.get', () => ({ value: undefined }))
  on('clock.every', (_$: any, e: any) => new Promise(r => setTimeout(() => r({ value: undefined }), e.ms))) // the timer ticks when this resolves
  on('session.start', (_$: any, e: any) => ({ cwd: e.cwd }))
  on('command.register', () => ({ value: {} })) // the UI mod's command, registered at session.start
  on('tool.call', () => {
    order.push('tool')
    return { result: { stdout: 'ok', stderr: '', interrupted: false } }
  })
  on('prompt.submit', (_$: any, e: any) => ({ text: e.text, context: e.context }))
  on('turn.complete', (_$: any, e: any) => ({ text: e.answer }))
  const off: unknown[] = [] // each hooksOff the bridge set
  on('state.set', (_$: any, e: any, next: any) => {
    if (e.key === 'hooksOff') off.push(e.value)
    return next(e)
  })
  return { ran, order, off }
}

test('tool.call: a PreToolUse deny refuses the call, its nudge marker dropped', async ($, on) => {
  const { order } = stage(on, name =>
    name === 'pretooluse-chain' ? { hookSpecificOutput: { permissionDecision: 'deny', permissionDecisionReason: '[ah-nudge] use the runner' } } : undefined)
  const r: any = await $.tool.call({ tool: 'Bash', command: 'ls' })
  expect(r.deny).toBe('use the runner')
  expect(order.includes('tool')).toBe(false)
})

test('tool.call: no decision lets the tool run (negative)', async ($, on) => {
  const { order } = stage(on)
  const r: any = await $.tool.call({ tool: 'Bash', command: 'ls' })
  expect(r.deny).toBe(undefined)
  expect(order.includes('tool')).toBe(true)
})

test('tool.call: PostToolUse runs after the tool, with its response, only on a matching tool', async ($, on) => {
  const { ran, order } = stage(on)
  await $.tool.call({ tool: 'Bash', command: 'ls' })
  expect(order).toEqual(['pretooluse-chain', 'tool', 'posttooluse-commit-log'])
  expect(ran[1].payload.hook_event_name).toBe('PostToolUse')
  expect(ran[1].payload.tool_response.stdout).toBe('ok')
  expect(ran.some(r => r.name === 'posttooluse-peer-resolve')).toBe(false)
})

test('prompt.submit: SessionStart context is carried once', async ($, on) => {
  stage(on, name => (name === 'sessionstart' ? { hookSpecificOutput: { additionalContext: 'AH DIRECTIVE' } } : undefined))
  await $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  const first: any = await $.prompt.submit({ text: 'hello' })
  const second: any = await $.prompt.submit({ text: 'again' })
  expect(first.text).toBe('AH DIRECTIVE\n\nhello')
  expect(second.text).toBe('again')
})

// A SessionStart row held open until `release()`: the brief mailed seconds after a spawn comes while it still runs (b206).
// `entered` resolves once the row is running.
const slowRow = (text?: string) => {
  let release = () => {}
  let enter = () => {}
  const gate = new Promise<void>(r => (release = r))
  const entered = new Promise<void>(r => (enter = r))
  const answer = async (name: string) => {
    if (name !== 'sessionstart') return undefined
    enter()
    await gate
    return text === undefined ? undefined : { hookSpecificOutput: { additionalContext: text } }
  }
  return { answer, entered, release: () => release() }
}
const settled = (p: Promise<unknown>) => Promise.race([p.then(() => true), new Promise(r => setTimeout(() => r(false), 50))])

test('prompt.submit: a prompt submitted while SessionStart still runs waits for it and carries its text, once', async ($, on) => {
  const row = slowRow('Rules of engagement, set by the user.')
  stage(on, row.answer)
  const started = $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  await row.entered
  const first: Promise<any> = $.prompt.submit({ text: 'brief' })
  expect(await settled(first)).toBe(false) // still waiting on the row
  row.release()
  await started
  expect((await first).text).toBe('Rules of engagement, set by the user.\n\nbrief')
  expect(((await $.prompt.submit({ text: 'again' })) as any).text).toBe('again')
})

test('prompt.submit: two prompts that wait on the same run carry its text once between them', async ($, on) => {
  const row = slowRow('AH RULES')
  stage(on, row.answer)
  const started = $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  await row.entered
  const a: Promise<any> = $.prompt.submit({ text: 'mail' })
  const b: Promise<any> = $.prompt.submit({ text: 'typed' })
  row.release()
  await started
  const texts = [(await a).text, (await b).text]
  expect(texts.filter(t => t.startsWith('AH RULES')).length).toBe(1)
})

test('negative: a slow SessionStart row with no output leaves the prompt text unchanged', async ($, on) => {
  const row = slowRow()
  stage(on, row.answer)
  const started = $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  await row.entered
  const first: Promise<any> = $.prompt.submit({ text: 'brief' })
  row.release()
  await started
  expect((await first).text).toBe('brief')
})

test('prompt.submit: a row slower than the hook budget lets the prompt go on unchanged within it, and the next prompt carries the text once', { timeoutMs: 30_000 }, async ($, on) => {
  const row = slowRow('AH RULES') // the default 60 s row timeout: the hook's own budget sets the bound
  stage(on, row.answer)
  const started = $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  await row.entered
  const at = Date.now()
  const first: any = await $.prompt.submit({ text: 'brief' })
  const waited = Date.now() - at
  row.release()
  await started
  expect(first.text).toBe('brief')
  expect(waited).toBeLessThan(10_000)
  expect(((await $.prompt.submit({ text: 'next' })) as any).text).toBe('AH RULES\n\nnext')
  expect(((await $.prompt.submit({ text: 'third' })) as any).text).toBe('third')
})

test('waitBound: the rows\' sum cut to the budget left less 500 ms, never below 0; the sum alone with no budget', () => {
  expect(waitBound(240_000, 10_000)).toBe(9_500)
  expect(waitBound(1_000, 10_000)).toBe(1_000)
  expect(waitBound(240_000, 300)).toBe(0)
  expect(waitBound(240_000, undefined)).toBe(240_000)
  expect(waitBound(240_000, Number.NaN)).toBe(240_000)
})

test('prompt.submit: a SessionStart row that never returns holds the prompt for its own timeout and no longer', async ($, on) => {
  const quick = JSON.stringify({ hooks: { SessionStart: [{ matcher: 'startup|resume|clear|compact|fork', hooks: [{ ...cmd('sessionstart'), timeout: 1 }] }] } })
  const row = slowRow('never') // never released
  stage(on, row.answer, quick)
  void $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  await row.entered
  const at = Date.now()
  const first: any = await $.prompt.submit({ text: 'brief' })
  const waited = Date.now() - at
  expect(first.text).toBe('brief')
  expect(waited).toBeGreaterThanOrEqual(900)
  expect(waited).toBeLessThan(5000)
})

// The bridge's hooks called directly, so the `next` they get is a bare function: no signal, no budget.
test('prompt.submit: a next with no signal does not throw, and the start text lands', async () => {
  const hooks: Record<string, any> = {} // the bridge's own hooks: the ones registered with a matcher
  register((event: string, matcher: any, fn: any) => { if (fn) hooks[event] = fn }, {} as any)
  const pass = async (e: any) => e
  const out = JSON.stringify({ hookSpecificOutput: { additionalContext: 'AH DIRECTIVE' } })
  const $p = {
    plugin: { root: '/p' },
    env: { get: async () => undefined },
    fs: { read: async () => REGISTRY },
    session: { id: async () => 'sess-1', cwd: async () => '/work/repo', messages: async () => [] },
    process: { run: async (argv: string[]) => ({ exitCode: 0, stdout: argv[2] === 'sessionstart' ? out : '', stderr: '' }) },
    clock: { every: () => ({ cancel: () => {} }) },
    state: { set: async () => {} },
    ui: { log: () => {} },
  }
  await hooks['session.start']($p, { cwd: '/work/repo', surface: null, isInteractive: false }, pass)
  const first: any = await hooks['prompt.submit']($p, { text: 'hello' }, pass)
  expect(first.text).toBe('AH DIRECTIVE\n\nhello')
})

test('prompt.submit: two session.start runs before the first prompt both land on it, first run\'s first', async ($, on) => {
  let n = 0
  stage(on, name => (name === 'sessionstart' ? { hookSpecificOutput: { additionalContext: `RUN ${++n}` } } : undefined))
  await $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  await $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  const first: any = await $.prompt.submit({ text: 'brief' })
  expect(first.text).toBe('RUN 1\n\nRUN 2\n\nbrief')
  expect(((await $.prompt.submit({ text: 'again' })) as any).text).toBe('again')
})

// The notice bin/ah-hook gives a non-member SessionStart with no binary.
const NOT_BUILT = 'Wrangl is installed but its native binary is not built, so its hooks are off: this session is not registered or tracked and has no Orchestrator directive. Ask the user to run sh "/p/scripts/install.sh" --apply --auto-rebuild in a terminal, then start a new session or send another prompt.'
const notice = (text: string) => ({ systemMessage: text, hookSpecificOutput: { hookEventName: 'SessionStart', additionalContext: text } })
const starts = (ran: { name: string; payload: any }[]) => ran.filter(r => r.name === 'sessionstart')

test('SessionStart answered by the not-built notice is run again on the next prompt, and not again once it ran through the binary', async ($, on) => {
  let built = false
  const { ran, off } = stage(on, name => (name === 'sessionstart' ? (built ? { hookSpecificOutput: { additionalContext: 'AH DIRECTIVE' } } : notice(NOT_BUILT)) : undefined))
  await $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  expect(off).toEqual([true])
  const first: any = await $.prompt.submit({ text: 'one' }) // still not built: tried once more, owed again
  expect(starts(ran).length).toBe(2)
  built = true
  const second: any = await $.prompt.submit({ text: 'two' })
  const third: any = await $.prompt.submit({ text: 'three' })
  expect(starts(ran).length).toBe(3)
  expect(starts(ran).map(r => r.payload.source)).toEqual(['startup', 'startup', 'startup'])
  expect(first.text).toBe(`${NOT_BUILT}\n\none`) // the start's notice once; the retry that still did not run adds nothing
  expect(second.text).toBe('AH DIRECTIVE\n\ntwo')
  expect(third.text).toBe('three')
  expect(off).toEqual([true, true, false])
})

test('SessionStart that failed is owed to the next prompt', async ($, on) => {
  let fail = true
  const { ran } = stage(on, name => (name === 'sessionstart' ? (fail ? { exitCode: 1 } : { hookSpecificOutput: { additionalContext: 'AH DIRECTIVE' } }) : undefined))
  await $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  fail = false
  const first: any = await $.prompt.submit({ text: 'one' })
  expect(starts(ran).length).toBe(2)
  expect(first.text).toBe('AH DIRECTIVE\n\none')
})

test('negative: a SessionStart that ran through the binary, with or without context, is not run again', async ($, on) => {
  const { ran, off } = stage(on) // the binary answered with nothing
  await $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  await $.prompt.submit({ text: 'one' })
  await $.prompt.submit({ text: 'two' })
  expect(starts(ran).length).toBe(1)
  expect(off).toEqual([false])
})

test('a refused home is owed like a missing binary: retried silently, and its directive arrives once the home is trusted', async ($, on) => {
  const refused = 'ah: AH_HOME is not the default home and is not trusted, so ah did not run. Ask the user to trust this home from a terminal, or to unset AH_HOME.'
  let trusted = false
  const { ran, off } = stage(on, name => (name === 'sessionstart' ? (trusted ? { hookSpecificOutput: { additionalContext: 'AH DIRECTIVE' } } : notice(refused)) : undefined))
  await $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  const first: any = await $.prompt.submit({ text: 'one' })
  trusted = true
  const second: any = await $.prompt.submit({ text: 'two' })
  expect(starts(ran).length).toBe(3)
  expect(first.text).toBe(`${refused}\n\none`)
  expect(second.text).toBe('AH DIRECTIVE\n\ntwo')
  expect(off).toEqual([true, true, false])
})

test("negative: a /clear's first SessionStart answered by the notice is told (not a retry); only its retry is silent", async ($, on) => {
  const { ran } = stage(on, name => (name === 'sessionstart' ? notice(NOT_BUILT) : undefined))
  on('session.end', () => ({ sessionId: 'sess-1' }))
  await $.session.end({ reason: 'clear', sessionId: 'sess-1' } as any)
  const first: any = await $.prompt.submit({ text: 'one' })
  const second: any = await $.prompt.submit({ text: 'two' })
  expect(starts(ran).map(r => r.payload.source)).toEqual(['clear', 'clear'])
  expect(first.text).toBe(`${NOT_BUILT}\n\none`)
  expect(second.text).toBe('two')
})

test('negative: context that only mentions a refusal mid-text is not owed', async ($, on) => {
  const { ran } = stage(on, name => (name === 'sessionstart' ? { hookSpecificOutput: { additionalContext: 'Earlier: ah: x, so ah did not run. Ask the user to trust this home from a terminal.' } } : undefined))
  await $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  await $.prompt.submit({ text: 'one' })
  expect(starts(ran).length).toBe(1)
})

test("negative: a member's own not-built notice is owed too; other context naming the words mid-text is not", async ($, on) => {
  const member = 'Wrangl is installed but not built, so its hooks are off. Ask the user to run x in a terminal, then start a new session.'
  let answer: unknown = notice(member)
  const { ran } = stage(on, name => (name === 'sessionstart' ? answer : undefined))
  await $.session.start({ cwd: '/work/repo', surface: null, isInteractive: false })
  answer = { hookSpecificOutput: { additionalContext: `Note: ${member}` } }
  await $.prompt.submit({ text: 'one' })
  await $.prompt.submit({ text: 'two' })
  expect(starts(ran).length).toBe(2)
})

test("turn.complete: a Stop block's reason, when it comes back as a prompt, is marked as the bridge's own once; the user's same text after it is not (negative)", async ($, on) => {
  const { ran } = stage(on, name => (name === 'stop-peer-nudge' ? { decision: 'block', reason: 'report first' } : undefined))
  const prompts = () => ran.filter(r => r.payload.hook_event_name === 'UserPromptSubmit')
  await $.turn.complete({ answer: 'done', durationMs: 1, isAborted: false, turnId: 't1', reason: 'answer' } as any)
  // the bridge's own $.prompt.submit runs once the session is idle
  for (let i = 0; i < 200 && prompts().length === 0; i++) await new Promise(r => setTimeout(r, 5))
  expect(prompts().map(r => [r.payload.prompt, r.payload.plugin_submitted])).toEqual([['report first', true]])
  await $.prompt.submit({ text: 'report first' })
  expect(prompts().map(r => r.payload.plugin_submitted)).toEqual([true, undefined])
})

test("negative: after a turn end the bridge's submit never came back from, the user's same text is not marked", async ($, on) => {
  let blocks = 1
  const { ran } = stage(on, name => (name === 'stop-peer-nudge' && blocks-- > 0 ? { decision: 'block', reason: 'Continue.' } : undefined))
  const prompts = () => ran.filter(r => r.payload.hook_event_name === 'UserPromptSubmit')
  await $.turn.complete({ answer: 'done', durationMs: 1, isAborted: false, turnId: 't1', reason: 'answer' } as any)
  await $.turn.complete({ answer: 'done', durationMs: 1, isAborted: false, turnId: 't2', reason: 'answer' } as any)
  const after = prompts().length
  await $.prompt.submit({ text: 'Continue.' })
  for (let i = 0; i < 40; i++) await new Promise(r => setTimeout(r, 5))
  expect(prompts().slice(after).filter(r => r.payload.plugin_submitted)).toEqual([])
})

test("negative: a dropped or rejected submit, or a turn end, leaves the user's same text unmarked; another prompt first does not unmark the bridge's own", () => {
  const own = ownPrompt()
  own.expect('Continue.')
  own.settled('Continue.') // the submit settled and the prompt never arrived
  expect(own.take('Continue.')).toBe(false)
  own.expect('Continue.')
  expect(own.take('something else')).toBe(false) // a prompt already queued arrives first
  expect(own.take('Continue.')).toBe(true) // the bridge's own is still marked when it arrives
  own.expect('Continue.')
  own.clear() // the next turn end
  expect(own.take('Continue.')).toBe(false)
  own.expect('Continue.')
  expect([own.take('Continue.'), own.take('Continue.')]).toEqual([true, false]) // it arrives, once
})

test('turn.complete: Stop runs on an answer, not on an interrupt (negative)', async ($, on) => {
  const { ran } = stage(on)
  await $.turn.complete({ answer: 'done', durationMs: 1, isAborted: false, turnId: 't1', reason: 'answer' } as any)
  expect(ran.filter(r => r.name === 'stop-peer-nudge').length).toBe(1)
  expect(ran[0].payload.last_assistant_message).toBe('done')
  await $.turn.complete({ answer: '', durationMs: 1, isAborted: true, turnId: 't2', reason: 'aborted' } as any)
  expect(ran.filter(r => r.name === 'stop-peer-nudge').length).toBe(1)
})
