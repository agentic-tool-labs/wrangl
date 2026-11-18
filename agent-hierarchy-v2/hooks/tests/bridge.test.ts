import { expect, test } from 'claude-code/testing'
import { ownPrompt } from '../register.ts'

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
const stage = (on: any, answer: (name: string, payload: any) => unknown = () => undefined) => {
  const ran: { name: string; payload: any }[] = []
  const order: string[] = []
  on('fs.read', (_$: any, e: any) => (e.path.endsWith('/hooks/registry.json') ? { value: REGISTRY } : { deny: 'no file' }))
  on('process.run', (_$: any, e: any) => {
    const name = e.argv[2]
    const payload = JSON.parse(e.init?.stdin ?? '{}')
    ran.push({ name, payload })
    order.push(name)
    const out: any = answer(name, payload)
    if (out?.exitCode !== undefined) return { value: { exitCode: out.exitCode, stdout: '', stderr: 'failed', isStdoutTruncated: false, isStderrTruncated: false } }
    return { value: { exitCode: 0, stdout: out === undefined ? '' : JSON.stringify(out), stderr: '', isStdoutTruncated: false, isStderrTruncated: false } }
  })
  on('session.id', () => ({ value: 'sess-1' }))
  on('session.cwd', () => ({ value: '/work/repo' }))
  on('session.messages', () => ({ value: [] }))
  on('env.get', () => ({ value: undefined }))
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
