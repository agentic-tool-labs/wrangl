import { test, expect } from 'claude-code/testing'
import { nudgeRelabel, register } from '../register.tsx'

const MARKER = '[ah-nudge] '
const classic = (tool: string, reason: string) => ({ ref: 1, result: `Error: PreToolUse:${tool} hook error: ${reason}`, text: `PreToolUse:${tool} hook error: ${reason}`, isError: true })

// Runs the relabel against a settled `next`, and counts how often it was called.
const relabel = async (ran: any, tool = 'SendMessage') => {
  let calls = 0
  const out = await nudgeRelabel(undefined, { tool }, async () => { calls++; return ran })
  return { out, calls }
}

const TOOLS = ['SendMessage', 'Agent', 'Task', 'Bash', 'AskUserQuestion', 'ExitPlanMode', 'SendUserFile', 'PushNotification', 'Write', 'Edit', 'MultiEdit', 'NotebookEdit', 'EnterWorktree']

for (const tool of TOOLS.filter((t) => t !== 'Bash')) {
  test(`a classic deny starting with the marker on ${tool} is re-delivered as a plain deny without the marker or the label`, async () => {
    const { out, calls } = await relabel(classic(tool, `${MARKER}ah: do the thing first`), tool)
    expect(out).toEqual({ deny: 'ah: do the thing first' })
    expect(calls).toBe(1)
  })
}

test('a classic deny starting with the marker on Bash becomes a plain result with that stdout and no deny', async () => {
  const { out, calls } = await relabel(classic('Bash', `${MARKER}NOT DONE: x`), 'Bash')
  expect(out).toEqual({ result: { stdout: 'NOT DONE: x', stderr: '', interrupted: false } })
  expect(calls).toBe(1)
})

test('negative: a Bash error without the marker comes back as the very same outcome', async () => {
  const ran = classic('Bash', '[task-gopher] NOT RUN.')
  expect((await relabel(ran, 'Bash')).out).toBe(ran)
})

test('the relabel registers on exactly the soft-rule tools, each once, and on no other event', async () => {
  const seen: { event: string; tool: string | undefined; fn: unknown }[] = []
  register((event: string, matcher: any, fn: any) => { if (event === 'tool.call') seen.push({ event, tool: matcher?.tool, fn: fn ?? matcher }) }, {} as any)
  const relabels = seen.filter((s) => s.tool !== undefined)
  expect(relabels.map((s) => s.tool)).toEqual(TOOLS)
  expect(relabels.every((s) => s.fn === nudgeRelabel)).toBe(true)
})

// The one matcher-less tool.call hook is the doorbell ring.
const ringHook = () => {
  const found: any[] = []
  register((event: string, matcher: any, fn: any) => { if (event === 'tool.call' && fn === undefined) found.push(matcher) }, {} as any)
  return found
}

test('exactly one matcher-less tool.call hook is registered: the doorbell ring', () => {
  expect(ringHook().length).toBe(1)
})

test('the ring passes the tool input to next untouched and returns the result as it came, with no deny, when the session has no mail poll', async () => {
  const [ring] = ringHook()
  const e = { tool: 'Bash', input: { command: 'ls' } }
  const ran = { ref: 1, result: 'hi', text: 'hi', isError: false }
  let seenInput: unknown = null
  const out = await ring(undefined, e, async (x: unknown) => { seenInput = x; return ran })
  expect(seenInput).toBe(e)
  expect(out).toBe(ran)
})

// A registered, busy session whose every `ah msg due` answers `due`, with its doorbell hook.
const ringingSession = async (due: unknown) => {
  const hooks: Record<string, any> = {}
  let ring: any = null
  register((event: string, matcher: any, fn: any) => {
    if (event === 'tool.call' && fn === undefined) ring = matcher
    else hooks[event] = fn ?? matcher
  }, {} as any)
  const $: any = {
    env: { get: async (k: string) => (k === 'AH_HOME' ? '/ah' : undefined) },
    session: { id: async () => 'sid', cwd: async () => '/work/repo' },
    fs: { exists: async () => false, stat: async () => ({}), read: async () => '{}' },
    clock: { now: async () => 1_790_000_000_000, every: () => ({ cancel: () => {} }) },
    state: { get: async () => ({ value: undefined }), set: async () => {} },
    ui: { status: () => {}, open: async () => ({ isPlaced: true }), toast: async () => {}, panes: async () => [] },
    command: { register: async () => {} },
    plugin: { root: '/opt/ah' },
    process: { run: async () => ({ exitCode: 0, stdout: JSON.stringify(due), stderr: '' }) },
  }
  await hooks['session.start']($, { cwd: '/work/repo', isInteractive: true }, async (e: unknown) => e)
  await hooks['prompt.submit']($, { text: 'go' }, async (e: unknown) => e)
  return { ring, $ }
}
const dueAnswer = (over: Record<string, unknown> = {}) => ({ v: 1, ids: [], prompt: null, idle: false, state: 'S', acked: [], daemon: null, ...over })

test('a ring between tool calls adds a context the engine accepts: a list of texts', async () => {
  const line = 'ah: You have 1 message (1 report). Run `ah msg next`.'
  const { ring, $ } = await ringingSession(dueAnswer({ ids: ['doorbell:3'], prompt: line, ring: true }))
  const ran = { ref: 1, result: 'hi', text: 'hi', isError: false }
  expect(await ring($, { tool: 'Bash' }, async () => ran)).toEqual({ ...ran, context: [line] })
})

test('negative: no ring between tool calls adds no context', async () => {
  const { ring, $ } = await ringingSession(dueAnswer())
  const ran = { ref: 1, result: 'hi', text: 'hi', isError: false }
  expect(await ring($, { tool: 'Bash' }, async () => ran)).toBe(ran)
})

test('negative: a result that already denies comes back as the very same outcome', async () => {
  const [ring] = ringHook()
  const denied = { deny: 'no' }
  expect(await ring(undefined, { tool: 'Bash' }, async () => denied)).toBe(denied)
})

test('a classic deny without the marker comes back as the very same outcome', async () => {
  const ran = classic('SendMessage', 'ah: a hard gate refused this')
  const { out, calls } = await relabel(ran)
  expect(out).toBe(ran)
  expect(calls).toBe(1)
})

test('an errored result whose own text starts with the marker, with no classic prefix, comes back as the very same outcome', async () => {
  const ran = { ref: 1, result: `${MARKER}x`, text: `${MARKER}x`, isError: true }
  expect((await relabel(ran)).out).toBe(ran)
})

test('a marker that is not at the start of the reason is not relabelled', async () => {
  const mid = classic('SendMessage', `ah: see ${MARKER}later`)
  const other = classic('Bash', `[task-gopher] NOT RUN ${MARKER}`)
  expect((await relabel(mid)).out).toBe(mid)
  expect((await relabel(other)).out).toBe(other)
})

test('a call that ran, or any outcome that is not an error, comes back as the very same outcome', async () => {
  const ran = { ref: 1, result: 'hi', text: 'hi', isError: false }
  const marked = { ref: 1, result: `${MARKER}x`, text: `${MARKER}x`, isError: false }
  expect((await relabel(ran)).out).toBe(ran)
  expect((await relabel(marked)).out).toBe(marked)
})

test('a relabel that throws returns the settled outcome and still called next once', async () => {
  const ran = { isError: true, get text(): string { throw new Error('boom') } }
  const { out, calls } = await relabel(ran)
  expect(out === ran).toBe(true)
  expect(calls).toBe(1)
})

test('whatever the outcome looks like, the relabel returns it as is or an object whose only key is deny', async () => {
  const shapes: any[] = [
    classic('Bash', `${MARKER}x`),
    classic('Bash', 'x'),
    classic('Bash', `y ${MARKER}x`),
    { isError: false, text: `${MARKER}x` },
    { isError: true, text: undefined },
    { isError: true },
    { text: `${MARKER}x` },
    undefined,
    null,
  ]
  for (const ran of shapes) {
    const { out } = await relabel(ran)
    const isDeny = out !== ran && out !== null && typeof out === 'object' && Object.keys(out).join() === 'deny'
    expect(out === ran || isDeny).toBe(true)
  }
})
