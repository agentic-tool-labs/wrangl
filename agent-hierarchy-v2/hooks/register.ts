import type { Register } from 'claude-code'
import { register as registerUi } from '../mod/register.tsx'

// The plugin's one hooks module: the UI mod's hooks (mod/register.tsx), then the bridge that runs every former classic hook
// through mod events. Under organization-managed Claude Code a user-installed plugin's classic hooks are skipped, while these
// mod events still run. Each row of hooks/registry.json still runs `sh <plugin root>/bin/ah-hook <name>` with classic-shaped
// JSON on stdin, its stdout parsed as classic hook output, so the native hook units stay the one implementation. A hook that
// errors or times out never blocks the session: it is logged and the event goes on, as a classic hook failed open.

type Row = { classic: string; matcher: string; hook: string; async: boolean; timeoutSec: number }

// The classic hook's output, the fields this bridge reads.
type Out = {
  decision?: string
  reason?: string
  hookSpecificOutput?: {
    permissionDecision?: string
    permissionDecisionReason?: string
    updatedInput?: Record<string, unknown>
    additionalContext?: string
  }
}

// The one command shape a row may run; any other row is not run.
const COMMAND = /^sh "\$\{CLAUDE_PLUGIN_ROOT\}\/bin\/ah-hook" ([a-z0-9-]+)$/

// The classic nudge marker: the deny reason is shown without it, as mod/register.tsx's relabel does for a classic deny.
const NUDGE = '[ah-nudge] '

// The registry's rows, in its order: one per classic command.
export function parseRegistry(text: string): Row[] {
  const doc = JSON.parse(text)
  const rows: Row[] = []
  for (const [classic, groups] of Object.entries(doc.hooks ?? {}) as [string, any[]][]) {
    for (const g of groups) {
      for (const h of g.hooks ?? []) {
        const m = typeof h.command === 'string' ? h.command.match(COMMAND) : null
        if (m) rows.push({ classic, matcher: g.matcher ?? '*', hook: m[1], async: h.async === true, timeoutSec: h.timeout ?? 60 })
      }
    }
  }
  return rows
}

// A classic matcher: absent or `*` matches all, otherwise a regex over the whole value.
export function matches(matcher: string, value: string): boolean {
  return !matcher || matcher === '*' || new RegExp(`^(?:${matcher})$`).test(value)
}

// The module's own state; a reload starts it over, as it does every module variable.
let registry: Promise<Row[]> | undefined
let originalCwd: string | undefined
let agentType: string | undefined // the main loop's --agent type, as classic hooks receive it
const subagentTypes = new Map<string, string>()
const asks = new Map<string, string>() // tool_use_id → a PreToolUse ask's reason, answered at tool.check
let held: string[] = [] // additionalContext waiting for the next prompt.submit
let starting: { out: Promise<string[]>; ms: Promise<number> } | undefined // the session.start run not yet taken by a prompt.submit, and its rows' summed timeouts
let pendingSource: string | undefined // a SessionStart owed on the next prompt (after /clear, or one that did not run)
let owed = false // pendingSource is a retry of a SessionStart that did not run, not a /clear's first
let stopContinued = false // the last Stop block queued a follow-up turn: classic stop_hook_active

// The text this bridge passed to $.prompt.submit and has not seen arrive, one at a time. It is forgotten when it arrives (once), when
// its submit settles without it, and at the next turn.complete, so a user who later types the same text is never taken for the
// bridge. Another prompt arriving first does not forget it: a prompt already queued at the Stop block would otherwise let the
// bridge's own reason through unmarked, as a user question.
export function ownPrompt() {
  let text: string | undefined
  return {
    expect(t: string) {
      text = t
    },
    // whether the arriving prompt is the one expected; its arrival forgets it, another prompt's does not
    take(t: string): boolean {
      const mine = text !== undefined && text === t
      if (mine) text = undefined
      return mine
    },
    settled(t: string) {
      if (text === t) text = undefined
    },
    clear() {
      text = undefined
    },
  }
}
const own = ownPrompt()
let lastCount: number | undefined

function log($: any, text: string) {
  try {
    $.ui.log(`Wrangl: ${text}`, { to: 'debug' })
  } catch {
    // nowhere to log
  }
}

// The rows of one classic event whose matcher fits `value`. An unreadable registry runs nothing.
async function rowsFor($: any, classic: string, value: string): Promise<Row[]> {
  registry ??= $.fs.read(`${$.plugin.root}/hooks/registry.json`).then(parseRegistry)
  try {
    return (await registry!).filter(r => r.classic === classic && matches(r.matcher, value))
  } catch (err) {
    log($, `hooks/registry.json unreadable: ${String(err)}`)
    return []
  }
}

// Classic base fields, as Claude Code passes them to every hook.
async function base($: any, classic: string, agentId?: string): Promise<Record<string, unknown>> {
  const session_id: string = await $.session.id()
  const cwd: string = await $.session.cwd()
  const home = (await $.env.get('CLAUDE_CONFIG_DIR')) || `${await $.env.get('HOME')}/.claude`
  // ponytail: transcript_path is derived from the engine's on-disk layout, as no mod noun gives it; switch when one does.
  const project = `${home}/projects/${(originalCwd ?? cwd).replace(/[^a-zA-Z0-9]/g, '-')}`
  const fields: Record<string, unknown> = { session_id, transcript_path: `${project}/${session_id}.jsonl`, cwd, hook_event_name: classic }
  if (agentId) {
    fields.agent_id = agentId
    const t = subagentTypes.get(agentId)
    if (t) fields.agent_type = t
    fields.agent_transcript_path = `${project}/${session_id}/subagents/agent-${agentId}.jsonl`
  } else if (agentType) fields.agent_type = agentType
  return fields
}

// The bridge: one native hook unit, classic payload in, classic output back. Never throws; `failed` is called when the run failed.
// bridge to bin/ah-hook: 2026-11-11, atp-1592
async function runHook($: any, row: Row, payload: Record<string, unknown>, failed?: () => void): Promise<Out | undefined> {
  try {
    const root: string = $.plugin.root
    const ran = await $.process.run(['sh', `${root}/bin/ah-hook`, row.hook], {
      stdin: JSON.stringify(payload),
      env: { CLAUDE_PLUGIN_ROOT: root, CLAUDE_PROJECT_DIR: String(payload.cwd ?? '') },
      timeoutMs: row.timeoutSec * 1000,
    })
    // Classic exit 2: a blocking error whose reason is stderr.
    if (ran.exitCode !== 0) failed?.()
    if (ran.exitCode === 2) {
      const reason = ran.stderr.trim()
      return { decision: 'block', reason, hookSpecificOutput: { permissionDecision: 'deny', permissionDecisionReason: reason } }
    }
    if (ran.exitCode !== 0) {
      log($, `hook ${row.hook} (${row.classic}) exited ${ran.exitCode}: ${ran.stderr.slice(0, 500)}`)
      return undefined
    }
    const text = ran.stdout.trim()
    return text.startsWith('{') ? JSON.parse(text) : undefined
  } catch (err) {
    failed?.()
    log($, `hook ${row.hook} (${row.classic}) failed: ${String(err)}`)
    return undefined
  }
}

// Runs one classic event's matching rows in order; an async row is not awaited. Returns each awaited output; `failed` as runHook's, awaited rows only.
async function runEvent($: any, classic: string, value: string, payload: Record<string, unknown>, failed?: () => void): Promise<Out[]> {
  const outs: Out[] = []
  for (const row of await rowsFor($, classic, value)) {
    if (row.async) void runHook($, row, payload)
    else {
      const out = await runHook($, row, payload, failed)
      if (out) outs.push(out)
    }
  }
  return outs
}

function contextOf(outs: Out[]): string[] {
  return outs.map(o => o.hookSpecificOutput?.additionalContext).filter(Boolean) as string[]
}

// The notices bin/ah-hook answers SessionStart with when no binary ran: not built (member's or not), or a refused home.
const NOT_BUILT = /^Wrangl is installed but (?:its native binary is )?not built, so its hooks are off/
const REFUSED = /^ah: .*, so ah did not run\. Ask the user to trust this home from a terminal/

// A SessionStart that answered with one of those notices, or failed, did not really run: it is owed on the next prompt, at most one
// try per prompt, until one runs through the binary; the mod's status line says so meanwhile. A retry (`retry`) that still did not
// run adds no context: the session was told at its start.
// ponytail: a machine that never builds pays one `sh bin/ah-hook sessionstart` per prompt for the session's life; nothing else stops it.
async function sessionStart($: any, source: string, retry = false): Promise<string[]> {
  let off = false
  try {
    const context = contextOf(await runEvent($, 'SessionStart', source, { ...(await base($, 'SessionStart')), source }, () => (off = true)))
    if (context.some(c => NOT_BUILT.test(c) || REFUSED.test(c))) off = true
    return off && retry ? [] : context
  } catch (err) {
    off = true
    log($, `SessionStart (${source}) failed: ${String(err)}`)
    return []
  } finally {
    if (off && pendingSource === undefined) {
      pendingSource = source
      owed = true
    }
    try {
      await $.state.set({ plugin: 'wrangl', key: 'hooksOff' }, off)
    } catch {
      // no status line to tell
    }
  }
}

// How long prompt.submit waits on the session.start run: its awaited rows' summed timeouts, cut to the hook's remaining budget less a
// margin so the hook ends its own wait before the engine would (b206 Amendment 2); with no budget given, the rows' sum alone.
const BUDGET_MARGIN_MS = 500
export function waitBound(rowsMs: number, remainingMs: number | undefined): number {
  return typeof remainingMs === 'number' && Number.isFinite(remainingMs) ? Math.min(rowsMs, Math.max(0, remainingMs - BUDGET_MARGIN_MS)) : rowsMs
}

// One module may hook an event once without a matcher, and the UI mod already hooks session.start, prompt.submit,
// turn.complete and tool.call so: the bridge's hooks there carry a matcher that every event fits.
const ANY = /(?:)/

export const register: Register = (on, options) => {
  registerUi(on, options)

  on('session.start', { cwd: ANY }, async ($, e, next) => {
    originalCwd = e.cwd
    const sourced = (async () => {
      agentType = (await $.env.get('CLAUDE_CODE_AGENT')) || undefined
      try {
        if ((await $.session.messages({ as: 'api' })).length > 0) return 'resume'
      } catch {
        // unreadable: a fresh start
      }
      return 'startup'
    })()
    // Set before any await, so a prompt submitted while the rows run (a brief mailed seconds after spawn) waits for their text (b206).
    const out = sourced.then(source => sessionStart($, source)).catch(err => {
      log($, `session.start failed: ${String(err)}`)
      return [] as string[]
    })
    const ms = sourced.then(async source => (await rowsFor($, 'SessionStart', source)).filter(r => !r.async).reduce((sum, r) => sum + r.timeoutSec * 1000, 0), () => 0)
    // A second run before the first prompt is chained on the first: the prompt waits for both and carries both texts, first run's first.
    const prev = starting
    starting = prev
      ? { out: Promise.all([prev.out, out]).then(([a, b]) => [...a, ...b]), ms: Promise.all([prev.ms, ms]).then(([a, b]) => a + b) }
      : { out, ms }
    await out
    return next(e)
  })

  // prompt.submit is the main loop's alone. SessionStart's held context and UserPromptSubmit's go in as one block before the
  // prompt text; a blocking UserPromptSubmit drops the prompt.
  on('prompt.submit', { text: ANY }, async ($, e, next) => {
    let added: string[] = []
    try {
      // The session.start run's text, taken once; waited for at most its rows' own timeouts and this hook's own budget (waitBound),
      // past which the prompt goes on and the text rides on a later prompt.
      if (starting) {
        const s = starting
        // a timer that cannot be set ends the wait at once: the prompt goes on as before this wait existed.
        // clock.every used as a one-shot (b206 Amendment 3): cancelled on its first tick, or when the run settles first.
        const bound = s.ms.then(ms => new Promise<undefined>(resolve => {
          try {
            const timer = $.clock.every(waitBound(ms, next.budget?.remainingMs), () => {
              timer.cancel()
              resolve(undefined)
            })
            void s.out.then(() => timer.cancel())
          } catch {
            resolve(undefined)
          }
        }))
        const out = await Promise.race([s.out, bound])
        // cut while it waited (its budget ran out, or a hook above settled): the prompt went on without this hook, so the text
        // stays for the next prompt
        if (next.signal?.aborted) return next(e) // a next with no signal is not aborted
        // claimed once: a second prompt that waited on the same run adds nothing
        if (out && starting === s) {
          starting = undefined
          held.push(...out)
        }
      }
      if (pendingSource) {
        const source = pendingSource
        const retry = owed
        pendingSource = undefined
        owed = false
        held.push(...(await sessionStart($, source, retry)))
      }
      // A prompt this bridge submitted itself is marked, so the units never take it for the user's.
      const mine = own.take(e.text)
      const payload: Record<string, unknown> = { ...(await base($, 'UserPromptSubmit')), prompt: e.text }
      if (mine) payload.plugin_submitted = true
      const outs = await runEvent($, 'UserPromptSubmit', '*', payload)
      const block = outs.find(o => o.decision === 'block')
      if (block) return { drop: block.reason || 'blocked by a Wrangl hook' }
      added = [...held, ...contextOf(outs)]
      held = []
    } catch (err) {
      log($, `prompt.submit failed: ${String(err)}`)
    }
    if (!added.length) return next(e)
    // A slash command's text is not rewritten; its context is attached beside it instead.
    if (e.command) return next({ ...e, context: [...(e.context ?? []), ...added] })
    return next({ ...e, text: `${added.join('\n\n')}\n\n${e.text}` })
  })

  // A compaction mid-turn: a main-loop request carrying fewer messages than the last runs SessionStart's compact rows, their
  // context appended as a row of its own (session.compact is skipped under managed Claude Code; turn.step is not).
  on('turn.step', async function* ($, e, next) {
    const shrank = !e.agentId && lastCount !== undefined && e.messageCount < lastCount
    if (!e.agentId) lastCount = e.messageCount
    if (shrank) {
      const text = (await sessionStart($, 'compact')).join('\n\n')
      if (text) {
        try {
          await $.session.append({ message: { type: 'user', content: [{ type: 'text', text }] } })
        } catch (err) {
          log($, `compact context not appended: ${String(err)}`)
        }
      }
    }
    return yield* next(e)
  })

  // PreToolUse before the tool; PostToolUse, PostToolUseFailure or PermissionDenied after it.
  on('tool.call', { tool: ANY }, async ($, e, next) => {
    const { tool, tool_use_id, agentId, consent: _consent, requestMeta: _meta, ...tool_input } = e as any
    let fields: Record<string, unknown> | undefined
    let input: Record<string, unknown> = tool_input
    try {
      fields = await base($, 'PreToolUse', agentId)
      for (const row of await rowsFor($, 'PreToolUse', tool)) {
        const out = await runHook($, row, { ...fields, tool_name: tool, tool_input: input, tool_use_id })
        const hso = out?.hookSpecificOutput
        const decision = hso?.permissionDecision ?? (out?.decision === 'block' ? 'deny' : undefined)
        const reason = hso?.permissionDecisionReason || out?.reason || ''
        if (decision === 'deny') {
          const text = reason.startsWith(NUDGE) ? reason.slice(NUDGE.length) : reason
          return { deny: text || 'denied by a Wrangl hook' }
        }
        if (decision === 'ask' && tool_use_id) asks.set(tool_use_id, reason)
        if (hso?.updatedInput) input = hso.updatedInput
      }
    } catch (err) {
      log($, `PreToolUse failed: ${String(err)}`)
    }
    const r: any = await next(input === tool_input ? e : ({ ...e, ...input } as any))
    asks.delete(tool_use_id)
    if (!fields) return r
    try {
      const post = { ...fields, tool_name: tool, tool_input: input, tool_use_id }
      if (r?.deny !== undefined) await runEvent($, 'PermissionDenied', tool, { ...post, hook_event_name: 'PermissionDenied', reason: r.deny })
      else if (r?.isError) await runEvent($, 'PostToolUseFailure', tool, { ...post, hook_event_name: 'PostToolUseFailure', error: r.text ?? '' })
      else await runEvent($, 'PostToolUse', tool, { ...post, hook_event_name: 'PostToolUse', tool_response: r?.result })
    } catch (err) {
      log($, `PostToolUse failed: ${String(err)}`)
    }
    return r
  })

  // tool.call has no ask form: a PreToolUse ask is answered here, where the permission decision is made. A verdict that puts
  // the call to a person is the classic permission_prompt Notification.
  on('tool.check', async ($, e, next) => {
    const reason = e.tool_use_id !== undefined ? asks.get(e.tool_use_id) : undefined
    const r = reason !== undefined ? { decision: 'ask' as const, reason } : await next(e)
    if (r.decision === 'ask' && e.approver === 'person' && e.tool_use_id !== undefined) {
      try {
        const message = r.reason ?? `Claude needs your permission to use ${e.tool}`
        await runEvent($, 'Notification', 'permission_prompt', { ...(await base($, 'Notification', e.agentId)), notification_type: 'permission_prompt', message })
      } catch (err) {
        log($, `Notification failed: ${String(err)}`)
      }
    }
    return r
  })

  // SubagentStart once the agent has an id; its context joins the subagent's own conversation.
  on('agent.spawn', async ($, e, next) => {
    const r = await next(e)
    if (!r.agentId) return r
    try {
      subagentTypes.set(r.agentId, e.subagentType)
      const text = contextOf(await runEvent($, 'SubagentStart', e.subagentType, await base($, 'SubagentStart', r.agentId))).join('\n\n')
      if (text) await $.session.append({ message: { type: 'user', content: [{ type: 'text', text }] }, agentId: r.agentId })
    } catch (err) {
      log($, `SubagentStart failed: ${String(err)}`)
    }
    return r
  })

  // A subagent's end is SubagentStop; the main loop's is Stop, or StopFailure on an API error. An interrupt runs neither.
  on('turn.complete', { turnId: ANY }, async ($, e, next) => {
    // the main loop's next turn end forgets a submit of the bridge's own that never arrived
    if (!e.agentId) own.clear()
    const r = await next(e)
    try {
      if (e.agentId) {
        const payload = { ...(await base($, 'SubagentStop', e.agentId)), stop_hook_active: false, last_assistant_message: e.answer }
        // ponytail: a SubagentStop block cannot continue a finished subagent from a mod; it is logged, not enforced.
        for (const out of await runEvent($, 'SubagentStop', subagentTypes.get(e.agentId) ?? '*', payload)) {
          if (out.decision === 'block') log($, `SubagentStop block not enforced: ${out.reason ?? ''}`)
        }
      } else if (e.reason === 'error') {
        await runEvent($, 'StopFailure', 'unknown', { ...(await base($, 'StopFailure')), error: 'unknown', last_assistant_message: e.answer })
      } else if (e.reason !== 'aborted') {
        const payload = { ...(await base($, 'Stop')), stop_hook_active: stopContinued, last_assistant_message: e.answer }
        const block = (await runEvent($, 'Stop', '*', payload)).find(o => o.decision === 'block')
        stopContinued = !!block
        // A blocking Stop continues the session with its reason as the next turn; not awaited, as it runs once idle.
        if (block) {
          const text = block.reason || 'Continue.'
          own.expect(text)
          const forget = () => own.settled(text)
          try {
            $.prompt.submit({ text }).then(forget, forget)
          } catch {
            forget()
          }
        }
      }
    } catch (err) {
      log($, `turn.complete failed: ${String(err)}`)
    }
    return r
  })

  on('session.end', async ($, e, next) => {
    try {
      await runEvent($, 'SessionEnd', e.reason, { ...(await base($, 'SessionEnd')), reason: e.reason })
    } catch (err) {
      log($, `SessionEnd failed: ${String(err)}`)
    }
    // A /clear goes on under a new session id with no session.start: its SessionStart runs on the next prompt.
    if (e.reason === 'clear') {
      pendingSource = 'clear'
      owed = false
      lastCount = undefined
    }
    return next(e)
  })
}
