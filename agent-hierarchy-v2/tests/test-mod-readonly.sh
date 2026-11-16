#!/bin/bash
# agent-hierarchy — the mod acts only through an allow-list. Module source under mod/ (its tests and
# types aside) calls only the allowed `$` methods; names `on` only as register's first parameter and as
# the callee of on('<event>', …), each event with its exact matcher and an inline hook whose first
# parameter is `$`; imports values only from ./view; and holds none of the banned words that would let a
# return answer a prompt, send a message or draw an input. `claude plugin validate` must report nothing
# beyond the lists. Planted cases in a scratch copy show each forbidden form caught by the lexer alone,
# and each allowed form passed.
#
# THE INVARIANT: the mod runs exactly four pinned helpers, each with a fixed argv and no shell, each pinned whole
# (HELPER..HELPER4 below), one pinned tool.call relabel (NUDGE_HOOK below) and one pinned doorbell ring hook (RING_REG below) — all matched whole in mod/register.tsx, the only places
# `process` may appear, and the only place `deny` or `tool.call` may appear:
#   - the focus helper: a click on a member's name in the Pane focuses its terminal pane;
#   - the dismiss helper: the band's [ Dismiss ] button disbands one of the viewer's own teams through the plugin's own bin/ah.
#     The user approved this site: a dialog naming what will close, plus a second one when work is in flight, stands in
#     for the chat confirmation and the harness prompt that guard the Orchestrator's own `disband --close`;
#   - the mail helper: while the daemon push is not connected it runs this plugin's own `bin/ah msg due --json` from the 5 s timer,
#     and while it is connected only for a request too large for the daemon's request line. While connected its only daemon call is
#     one GET of /v1/due on the socket the last CLI answer reported, never one taken from the environment.
#   - the nudge relabel: for SendMessage, Agent, Task, Bash and AskUserQuestion it awaits `next(e)` once and, only when the classic hook
#     already denied with a reason starting `[ah-nudge] `, returns that same reason as a mod deny without the marker so the transcript
#     shows no "hook error" label. It never returns `result`, never decides, never calls `next` twice, and returns every other outcome as is.
#     The user approved this site: ruling 20261025-134451-1dcn.
#   - the doorbell ring: the second and last tool.call hook, matcher-less. It awaits next(e) once with the input unchanged, never denies
#     or blocks, never alters the result body, and only adds the model-only `context` line; a failure returns the result as it came.
#     The user approved this site: ruling 2026-10-25, ah:R7 mod-tool-call-ring-hook ("Allow, as limited").
# A seventh pinned text, or any change to a pinned argv, init, dialog or the relabel's matchers or returns, needs a new security ruling, not a guard edit.
# Usage: bash tests/test-mod-readonly.sh   (exits 0 iff all cases pass)

. "$(dirname "$0")/lib-hermetic.sh"
. "$(cd "$(dirname "$0")" && pwd)/lib-test-env.sh"
PLUGIN="$(cd "$(dirname "$0")/.." && pwd)"
SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/agent-hierarchy-mod-readonly-test.XXXXXX")"
[ -n "$SANDBOX" ] && [ -d "$SANDBOX" ] || { echo "mktemp failed"; exit 1; }
hermetic_on_exit 'rm -rf "$SANDBOX"'
PASS=0; FAIL=0

check() {
  local name=$1; shift
  if eval "$@"; then PASS=$((PASS+1)); echo "PASS: $name"; else FAIL=$((FAIL+1)); echo "FAIL: $name (OUT=${OUT:0:400})"; fi
}

# The one copy of what the mod may do, from the 2.1.289 types. Calls are `$.<noun>.<method>`; events are
# what `on(...)` may name; an event listed in MATCHERS must carry exactly one of its matchers (k=v pairs,
# keys sorted), any other takes none. RENDER_ELEMENTS is every RenderElement type; the banned words are
# RETURN_KEYS plus every element type not in DRAWABLE. BAND_DRAWABLE is also drawable, but only inside the
# ui.render hook matched by component=AbovePrompt, and only with an inline arrow onPress.
# ui.panes and ui.close only list and close this plugin's own panes: the band button toggles the Pane with them.
# The mail adapter adds two calls outside its pinned helper, each pinned below to the one argument form it may take, in register.tsx only:
# env.get (AH_HOME, HOME) and http.fetch (the status view's GET to the socket the mail poll's CLI answer reported). The mail prompt itself
# is `$.prompt.submit` inside the pinned mail helper, so a call of it anywhere else fails.
ALLOWED_CALLS="session.cwd session.id fs.stat fs.read fs.exists clock.every clock.now state.get state.set ui.status ui.toast ui.open ui.panes ui.close ui.resolve command.register env.get http.fetch prompt.submit"
ALLOWED_EVENTS="session.start ui.close command.run ui.render prompt.submit turn.complete"
MATCHERS="command.run:command=wrangl-hierarchy-pane ui.render:component=Pane,requestId=ah-status ui.render:component=AbovePrompt ui.close:id=ah-status"
RENDER_ELEMENTS="Box Text engine Button Input Select Link Code Markdown Client Svg Raster Image"
DRAWABLE="Box Text engine"
BAND_DRAWABLE="Button"
RETURN_KEYS="context exitCode press client raster deny result"
BANNED_TOKENS="globalThis eval Reflect Proxy arguments this"
# Every literal name that can obtain or rebind a prototype (so the pinned pattern check cannot be voided). Matched as whole words on the
# comment-blanked source with strings kept, so x['__proto__'] fails too; the computed-name ceiling (RegExp[a + b]) stays.
PROTO_TOKENS="prototype __proto__ getPrototypeOf setPrototypeOf defineProperty defineProperties getOwnPropertyDescriptor getOwnPropertyDescriptors"
# The one process the mod may run (spec: the focus helper). Held whole: any edit to the helper means editing this text, so
# both always show in one diff. Matched once in mod/register.tsx on the comment-blanked source (strings kept), then cut out
# by offset before the rest of the lexer reads the file; `process.run` is deliberately not in ALLOWED_CALLS.
PINNED_NAME="focusMember"
PINNED_NAME2="dismissTeam"
PINNED_NAME3="mailCycle"
PINNED_NAME4="viewOpen"
PINNED_NAME5="nudgeRelabel"
# What validate prints for the pinned helpers once a hook calls them, `|`-separated. Only the validate net accepts them, and only in exactly
# these forms; the lexer allows the exec by the pinned text alone (process.run, ui.ask and plugin.root are not in ALLOWED_CALLS).
NET_EXTRA_CALLS="process.run (via dismissTeam, focusMember, mailCycle, toolRing, viewOpen)|process.run (via dismissTeam, focusMember, mailCycle, viewOpen)|process.run (via dismissTeam, focusMember, viewOpen)|ui.ask (via dismissTeam, viewOpen)"
# What validate prints for the pinned nudge relabel's registration loop (it cannot resolve the loop's tool, so it prints `?`); the validate
# net accepts this exact item and no other tool.call. The tools are held by the pinned registration text, not by validate.
NET_NUDGE_HOOKS="tool.call{tool=?}|tool.call"
# What validate prints for the hook bridge (hooks/register.ts, the plugin's one module): its hooks and the calls only it makes.
# bridge to bin/ah-hook: 2026-11-11, atp-1592. Its one process call is pinned by bridge_ok below.
NET_BRIDGE_HOOKS='session.start{cwd=/"(?:)"/}|prompt.submit{text=/"(?:)"/}|turn.step|tool.call{tool=/"(?:)"/}|tool.check|agent.spawn|turn.complete{turnId=/"(?:)"/}|session.end'
NET_EXTRA_CALLS="$NET_EXTRA_CALLS|process.run (via dismissTeam, focusMember, mailCycle, runHook, toolRing, viewOpen)|session.append|session.messages|ui.log (via log)"
read -r -d '' HELPER <<'HELPER_END'
export const focusMember = async ($: any, name: unknown): Promise<boolean> => {
  if (typeof name !== 'string' || !/^[a-z][a-z0-9_-]{0,31}$/.test(name)) {
    await $.ui.toast('Could not focus that member.')
    return false
  }
  try {
    const run = await $.process.run(['herdr', 'agent', 'focus', name], { timeoutMs: 5000 })
    if (run.exitCode === 0) return true
  } catch {
  }
  await $.ui.toast(`Could not focus ${name}.`)
  return false
}
HELPER_END
# The dismiss helper, held whole. Its dialogs are asked and its two processes run only from here.
read -r -d '' HELPER2 <<'HELPER2_END'
export const dismissTeam = async ($: any): Promise<void> => {
  const flag = { plugin: 'wrangl', key: 'dismissing' }
  if ((await $.state.get(flag)).value === true) return
  await $.state.set(flag, true)
  try {
    const teams = ownedTeams((await $.state.get({ plugin: 'wrangl', key: 'view' })).value)
    if (teams.length === 0) return
    if (teams.length > 3) {
      await $.ui.toast('Too many teams to choose here; use the agent-team skill to disband.')
      return
    }
    const labels = teamLabels(teams)
    let pick = 0
    if (teams.length > 1) {
      const chosen = await $.ui.ask('Dismiss which team?', { header: 'Dismiss', options: ['Cancel', ...labels] })
      if (chosen === 'Cancel' || !labels.includes(chosen)) return
      pick = labels.indexOf(chosen)
    }
    const team = teams[pick]
    const label = labels[pick]
    if (!team.isDefault && !/^[A-Za-z0-9][A-Za-z0-9-]{0,31}$/.test(team.name)) {
      await $.ui.toast('Could not dismiss that team.')
      return
    }
    const root = $.plugin.root
    const cwd = await $.session.cwd()
    if (typeof root !== 'string' || !/^(\/|[A-Za-z]:[\\/])/.test(root)) {
      await $.ui.toast(`Could not dismiss ${label}: plugin path unavailable`)
      return
    }
    if (typeof cwd !== 'string' || !/^(\/|[A-Za-z]:[\\/])/.test(cwd)) {
      await $.ui.toast(`Could not dismiss ${label}: working directory unavailable`)
      return
    }
    const roster = [root + '/bin/ah', 'roster', 'disband']
    const target = ['--team', team.isDefault ? '@default' : team.name, '--cwd', cwd]
    let run
    try {
      run = await $.process.run([...roster, '--plan', ...target], { timeoutMs: 15000 })
    } catch {
      await $.ui.toast(`Could not dismiss ${label}: plan failed`)
      return
    }
    if (run.exitCode !== 0) {
      await $.ui.toast(`Could not dismiss ${label}: ${failReason(run.stderr, 'plan failed')}`)
      return
    }
    const read = readPlan(run.stdout)
    if (read.kind === 'nothing') {
      await $.ui.toast(`Nothing to dismiss: ${read.reason}`)
      return
    }
    if (read.kind === 'failed') {
      await $.ui.toast(`Could not dismiss ${label}: ${read.reason}`)
      return
    }
    if (!/^[0-9a-f]{16}$/.test(read.plan.token)) {
      await $.ui.toast(`Could not dismiss ${label}: plan failed`)
      return
    }
    const yes = `Dismiss ${label}`
    const answer = await $.ui.ask(dismissQuestion(label, team.summary, read.plan), { header: 'Dismiss', options: ['Cancel', yes] })
    if (answer !== yes) return
    if (inFlight(team.summary)) {
      const sure = await $.ui.ask(anywayQuestion(label, team.summary), { header: 'Dismiss', options: ['Cancel', 'Dismiss anyway'] })
      if (sure !== 'Dismiss anyway') return
    }
    try {
      run = await $.process.run([...roster, '--close', '--confirm', '--plan-token', read.plan.token, ...target], { timeoutMs: 30000 })
    } catch {
      await $.ui.toast(`Could not dismiss ${label}: close failed`)
      return
    }
    await $.ui.toast(closeToast(label, run.exitCode, run.stdout, run.stderr))
  } catch {
  } finally {
    await $.state.set(flag, false)
  }
}
HELPER2_END
# The mail helper, held whole. Its one process and its one daemon call run only from here, and it is referenced once outside its own
# text: by the timer in mailPoll.
read -r -d '' HELPER3 <<'HELPER3_END'
export const mailCycle = async ($: any, ctx: MailCtx): Promise<void> => {
  const st = ctx.st
  if (st.running) return
  st.running = true
  const gen = st.generation
  let again = false
  let submitting = false
  const marks: { id: string; prev: MailHanded | undefined; rec: MailHanded }[] = []
  const restore = () => {
    for (const m of marks) {
      if (ctx.handed.get(m.id) !== m.rec) continue
      if (m.prev === undefined) ctx.handed.delete(m.id)
      else ctx.handed.set(m.id, m.prev)
    }
  }
  try {
    if (!st.push && (st.cliSkipUntil ?? 0) > (await $.clock.now())) return
    const id = await $.session.id()
    if (!(await registered($, ctx, id))) {
      if (st.push) throw new Error('no registration')
      return
    }
    const sent = await $.clock.now()
    if (st.held === true && sent >= (st.heldAt ?? 0) + HELD_PROBE_MS) {
      st.heldAt = sent
      try {
        $.prompt.submit({ text: HELD_PROBE_TEXT }).then((entered: { drop?: unknown } | undefined) => { if (typeof entered?.drop !== 'string' || !STOP_DROP.test(entered.drop)) st.held = false }, () => {})
      } catch {}
    }
    const busyAt = ctx.busy.at
    const cwd = await $.session.cwd()
    const snapshot: Record<string, [string, number]> = {}
    for (const [k, v] of ctx.handed) snapshot[k] = [v.state, v.at]
    const text = JSON.stringify({ session: id, cwd: typeof cwd === 'string' ? cwd : '', hierarchy_dir: ctx.hier(), now: sent, busy_at: busyAt, poll_ms: POLL_MS, mod_build: MOD_BUILD, handed: snapshot, state: st.state, held: st.held || undefined })
    const pushed = st.push && ctx.link.socket !== null && encodeURIComponent(text).length <= PUSH_QUERY_MAX
    let raw: unknown
    if (pushed) {
      const r = await $.http.fetch('http://ahd/v1/due?in=' + encodeURIComponent(text) + '&wait=' + DUE_WAIT_S, { method: 'GET', headers: { 'Ah-Client-Version': 'mod' }, socketPath: ctx.link.socket })
      if (!r.ok) throw new Error('ahd answered ' + r.status)
      raw = JSON.parse(r.text)
    } else {
      const root = $.plugin.root
      if (typeof root !== 'string' || !/^(\/|[A-Za-z]:[\\/])/.test(root)) throw new Error('plugin path unavailable')
      const run = await $.process.run([root + '/bin/ah', 'msg', 'due', '--json'], { timeoutMs: 10000, stdin: text })
      if (run.exitCode !== 0) throw new Error('ah exited ' + run.exitCode)
      raw = JSON.parse(run.stdout)
    }
    const t = await $.clock.now()
    let a = readDue(raw)
    if (a === null) throw new Error('unreadable answer')
    showEscalation($, ctx, a)
    st.cliFailures = 0
    st.cliSkipUntil = 0
    if (gen !== st.generation) return
    if (ctx.busy.at !== busyAt && !a.ring) {
      again = st.push
      return
    }
    st.state = a.state
    for (const x of a.acked) ctx.handed.delete(x)
    if (a.prompt !== null && !a.ring) {
      const root = $.plugin.root
      const viaSocket = st.push && ctx.link.socket !== null
      if (viaSocket || (typeof root === 'string' && /^(\/|[A-Za-z]:[\\/])/.test(root))) {
        const current: Record<string, [string, number]> = {}
        for (const [k, v] of ctx.handed) current[k] = [v.state, v.at]
        const body = JSON.stringify({ session: id, cwd: typeof cwd === 'string' ? cwd : '', hierarchy_dir: ctx.hier(), now: await $.clock.now(), busy_at: busyAt, poll_ms: POLL_MS, mod_build: MOD_BUILD, handed: current, state: st.state, held: st.held || undefined })
        let b: ReturnType<typeof readDue> = null
        try {
          if (viaSocket && encodeURIComponent(body).length <= PUSH_QUERY_MAX) {
            const r = await $.http.fetch('http://ahd/v1/due?in=' + encodeURIComponent(body), { method: 'GET', headers: { 'Ah-Client-Version': 'mod' }, socketPath: ctx.link.socket })
            b = r.ok ? readDue(JSON.parse(r.text)) : null
          } else if (typeof root === 'string' && /^(\/|[A-Za-z]:[\\/])/.test(root)) {
            const run = await $.process.run([root + '/bin/ah', 'msg', 'due', '--json'], { timeoutMs: 10000, stdin: body })
            b = run.exitCode === 0 ? readDue(JSON.parse(run.stdout)) : null
          }
        } catch {
          b = null
        }
        if (gen !== st.generation) return
        if (ctx.busy.at !== busyAt && b?.ring !== true) {
          again = st.push
          return
        }
        if (b !== null) {
          for (const x of b.acked) ctx.handed.delete(x)
          a = b
          st.state = b.state
        }
      }
    }
    for (const x of a.ids) {
      const rec: MailHanded = { state: 'pending', at: t }
      marks.push({ id: x, prev: ctx.handed.get(x), rec })
      ctx.handed.set(x, rec)
    }
    if (a.prompt !== null) {
      submitting = true
      const before = ctx.busy.at
      const marked = a.idle && before === busyAt
      if (marked) ctx.busy.at = t
      const unmark = () => { if (marked && ctx.busy.at === t) ctx.busy.at = before }
      try {
        $.prompt.submit({ text: a.compact ?? a.prompt }).then(
          async (entered: { drop?: unknown } | undefined) => {
            if (typeof entered?.drop === 'string') {
              const at0 = await $.clock.now()
              restore()
              unmark()
              st.held = STOP_DROP.test(entered.drop)
              st.heldAt = at0
              await noteMail($, ctx, a, 'not delivered')
              return
            }
            const at = await $.clock.now()
            for (const m of marks) if (ctx.handed.get(m.id) === m.rec) ctx.handed.set(m.id, { state: 'resolved', at })
            await noteMail($, ctx, a, 'delivered')
          },
          async () => {
            restore()
            unmark()
            await noteMail($, ctx, a, 'not delivered')
          },
        )
      } catch {
        restore()
        unmark()
        await noteMail($, ctx, a, 'not delivered')
      }
    }
    if (st.push) {
      st.failures = 0
      again = true
    } else {
      if (ctx.link.socket !== a.socket) ctx.link.regToken = undefined
      ctx.link.socket = a.socket
      if (a.socket !== null && t >= st.notBefore) {
        st.push = true
        if (st.timer !== null) {
          st.timer.cancel()
          st.timer = null
        }
        again = true
      }
    }
  } catch {
    if (!submitting) restore()
    if (st.push && gen === st.generation) {
      st.push = false
      st.generation++
      st.notBefore = (await $.clock.now()) + BACKOFF_MS[Math.min(st.failures, BACKOFF_MS.length - 1)]
      st.failures++
      if (st.timer === null) st.timer = $.clock.every(POLL_MS, () => mailCycle($, ctx))
    } else if (!st.push) {
      const n = st.cliFailures ?? 0
      st.cliSkipUntil = (await $.clock.now()) + BACKOFF_MS[Math.min(n, BACKOFF_MS.length - 1)]
      st.cliFailures = n + 1
    }
  } finally {
    st.running = false
    if (again) mailCycle($, ctx)
  }
}
HELPER3_END
# The view helper, held whole. The band's View button calls it once; it asks which view and runs bin/ah for Web or the TUI.
read -r -d '' HELPER4 <<'HELPER4_END'
export const viewOpen = async ($: any, ctx: ViewCtx): Promise<'pane' | 'sitrep' | void> => {
  const choice = await $.ui.ask('Open view', { options: ['Pane', 'Sitrep', 'TUI', 'Web'] })
  if (choice === 'Pane') return 'pane'
  if (choice === 'Sitrep') return 'sitrep'
  const root = $.plugin.root
  if ((choice !== 'TUI' && choice !== 'Web') || typeof root !== 'string' || !/^(\/|[A-Za-z]:[\\/])/.test(root)) return
  const ah = root + '/bin/ah'
  if (choice === 'Web') {
    let token = ''
    try {
      token = String(await $.fs.read(ctx.ahHome + '/run/band.token'))
    } catch {
    }
    if (!/^[0-9a-f]{64}$/.test(token)) {
      await $.ui.toast('Web is unavailable (ahd not running?); run `ahd ui start` in a terminal')
      return
    }
    try {
      const run = await $.process.run([ah, 'view', 'open', '--kind', 'web'], { stdin: JSON.stringify({ token }) + '\n', timeoutMs: 15000 })
      if (run.exitCode === 0) return
      const line = String(run.stdout ?? '').split('\n')[0].trim()
      await $.ui.toast(line !== '' ? line : 'Web did not open; run `ahd ui start` in a terminal')
    } catch {
      await $.ui.toast('Web did not open; run `ahd ui start` in a terminal')
    }
    return
  }
  try {
    const run = await $.process.run([ah, 'view', 'open', '--kind', 'tui', '--session', String(await $.session.id())], { timeoutMs: 10000 })
    if (run.exitCode === 0) return
    const line = String(run.stdout ?? '').split('\n')[0].trim()
    await $.ui.toast(line !== '' ? line : 'TUI did not open; run `ahd tui` in a terminal')
  } catch {
    await $.ui.toast('TUI did not open; run `ahd tui` in a terminal')
  }
}
HELPER4_END
# The nudge relabel, held whole with its registrations (the only tool.call hooks, and the only place `deny` is returned). It
# calls next once, and returns that outcome itself, or the same reason with the marker taken off: a plain stdout result for Bash,
# a deny for every other tool.
read -r -d '' NUDGE_HOOK <<'NUDGE_HOOK_END'
export const nudgeRelabel = async (_$: any, e: any, next: any) => {
  const ran = await next(e)
  try {
    if (ran.isError !== true || typeof ran.text !== 'string') return ran
    const marker = '[ah-nudge] '
    const head = /^PreToolUse:[A-Za-z]+ hook error: /.exec(ran.text)
    if (head === null || !ran.text.startsWith(marker, head[0].length)) return ran
    const stripped = ran.text.slice(head[0].length + marker.length)
    return e.tool === 'Bash' ? { result: { stdout: stripped, stderr: '', interrupted: false } } : { deny: stripped }
  } catch {
    return ran
  }
}
NUDGE_HOOK_END
# Its registrations, held whole inside register (the validator wants the handler declared at the top of the file, so the two
# pinned texts are one site: neither may change without the other, and no other tool.call hook may exist).
IFS= read -r -d '' NUDGE_REG <<'NUDGE_REG_END'
  const nudgeTools = [
    'SendMessage',
    'Agent',
    'Task',
    'Bash',
    'AskUserQuestion',
    'ExitPlanMode',
    'SendUserFile',
    'PushNotification',
    'Write',
    'Edit',
    'MultiEdit',
    'NotebookEdit',
    'EnterWorktree',
  ]
  for (const tool of nudgeTools) on('tool.call', { tool }, nudgeRelabel)
NUDGE_REG_END
# The doorbell ring: the second and last tool.call hook, matcher-less. It awaits next(e) once, never passes a changed input, never
# denies or blocks and never alters the result body; the only thing it may add is the model-only `context` line, and any failure
# returns the result as it came. Held whole inside register.
IFS= read -r -d '' RING_REG <<'RING_REG_END'
  let mailRun: MailCtx | null = null
  on('tool.call', async ($, e, next) => {
    const result = await next(e)
    try {
      if (mailRun === null || 'deny' in result) return result
      const line = await toolRing($, mailRun)
      return line === null ? result : { ...result, context: [line] }
    } catch {
      return result
    }
  })
RING_REG_END
# What the ring asks: the same one fixed `bin/ah msg due --json` argv and the same one GET of /v1/due as the mail helper, with the
# request marked carrier tool. Held whole; it is the only other place `process` may appear.
read -r -d '' RING_HELPER <<'RING_HELPER_END'
export const toolRing = async ($: any, ctx: MailCtx): Promise<string | null> => {
  try {
    const busyAt = ctx.busy.at
    if (busyAt === null) return null
    const now = await $.clock.now()
    if (now - (ctx.st.toolAt ?? 0) < TOOL_ASK_MS) return null
    ctx.st.toolAt = now
    const id = await $.session.id()
    if (!(await registered($, ctx, id))) return null
    const cwd = await $.session.cwd()
    const snapshot: Record<string, [string, number]> = {}
    for (const [k, v] of ctx.handed) snapshot[k] = [v.state, v.at]
    const text = JSON.stringify({ session: id, cwd: typeof cwd === 'string' ? cwd : '', hierarchy_dir: ctx.hier(), now, busy_at: busyAt, poll_ms: POLL_MS, mod_build: MOD_BUILD, handed: snapshot, state: ctx.st.state, carrier: 'tool' })
    let raw: unknown
    if (ctx.st.push && ctx.link.socket !== null && encodeURIComponent(text).length <= PUSH_QUERY_MAX) {
      const r = await $.http.fetch('http://ahd/v1/due?in=' + encodeURIComponent(text), { method: 'GET', headers: { 'Ah-Client-Version': 'mod' }, socketPath: ctx.link.socket })
      if (!r.ok) return null
      raw = JSON.parse(r.text)
    } else {
      const root = $.plugin.root
      if (typeof root !== 'string' || !/^(\/|[A-Za-z]:[\\/])/.test(root)) return null
      const run = await $.process.run([root + '/bin/ah', 'msg', 'due', '--json'], { timeoutMs: 10000, stdin: text })
      if (run.exitCode !== 0) return null
      raw = JSON.parse(run.stdout)
    }
    const a = readDue(raw)
    if (a === null) return null
    showEscalation($, ctx, a)
    return a.prompt
  } catch {
    return null
  }
}
RING_HELPER_END
# The one View Button onPress the real register.tsx makes. The lexer holds it whole (whitespace folded, string text ignored).
read -r -d '' VIEW_PRESS <<'VIEW_PRESS_END'
async () => {
  try {
    if (await paneShown($)) {
      await paneClose($)
      return
    }
    const ctx = await viewCtx($)
    if (ctx === null) {
      await $.ui.toast('View is unavailable here.')
      return
    }
    const opened = await viewOpen($, ctx)
    if (opened !== undefined) await paneOpen($, member, toastFor, opened)
  } catch {}
}
VIEW_PRESS_END

# <mod dir>: every lexer violation in the module source under it, as file:line: reason; empty when clean.
violations() {
  local out rc
  out=$(node - "$1" "$ALLOWED_CALLS" "$ALLOWED_EVENTS" "$MATCHERS" "$RENDER_ELEMENTS" "$DRAWABLE" "$RETURN_KEYS" "$BANNED_TOKENS" "$BAND_DRAWABLE" "$PINNED_NAME" "$HELPER" "$PROTO_TOKENS" "$PINNED_NAME2" "$HELPER2" "$PINNED_NAME3" "$HELPER3" "$PINNED_NAME4" "$HELPER4" "$VIEW_PRESS" "$NUDGE_HOOK" "$NUDGE_REG" "$PINNED_NAME5" "$RING_REG" "$RING_HELPER" 2>&1 <<'JS'
const fs = require("fs"), path = require("path");
const [dir, callsArg, eventsArg, matchersArg, elementsArg, drawableArg, keysArg, tokensArg, bandDrawableArg, pinnedName, helperText, protoArg, pinnedName2, helperText2, pinnedName3, helperText3, pinnedName4, helperText4, viewPress, helperText5, helperText6, pinnedName5, helperText7, helperText8] = process.argv.slice(2);
const set = (s) => new Set(s.split(" "));
const CALLS = set(callsArg), EVENTS = set(eventsArg), TOKENS = set(tokensArg), DRAWABLE = set(drawableArg), BAND_DRAWABLE = set(bandDrawableArg);
const BAND_MATCHER = "component=AbovePrompt", PANE_MATCHER = "component=Pane,requestId=ah-status";
const BANNED = new Set([...set(keysArg), ...[...set(elementsArg)].filter((x) => !DRAWABLE.has(x))]);
const MATCHERS = {};
for (const m of matchersArg.split(" ")) { const [ev, canon] = m.split(/:(.*)/s); (MATCHERS[ev] = MATCHERS[ev] || []).push(canon); }
let modules = [];
try { modules = JSON.parse(fs.readFileSync(path.join(dir, "hooks.json"), "utf8")).modules || []; } catch {}
modules = modules.map((m) => path.normalize(m));
const files = [];
(function walk(d, top) {
  for (const ent of fs.readdirSync(d, { withFileTypes: true })) {
    const p = path.join(d, ent.name);
    if (ent.isDirectory()) { if (!(top && (ent.name === "tests" || ent.name === "types"))) walk(p, false); }
    else if (/\.(ts|tsx|jsx|js|mjs|cjs|mts|cts)$/.test(ent.name)) files.push(p);
  }
})(dir, true);

// The source with comments dropped and literal text blanked, same length and lines, so offsets match;
// `${…}` code inside a template stays. String literals, and templates without a `${…}`, are kept by
// their opening offset.
// ponytail: no regex-literal or JSX-text handling; a quote or `//` in either can mis-blank code, and words in JSX
// text are read as code (write such text as a {'…'} string). A real tokenizer if mod code ever needs one.
function lex(src) {
  const sp = (s) => s.replace(/[^\n]/g, " ");
  const strings = new Map(), tpl = [], frames = [];
  let out = "", kept = "", i = 0, depth = 0, inTpl = false;
  while (i < src.length) {
    const c = src[i], n = src[i + 1];
    if (inTpl) {
      const f = frames[frames.length - 1];
      if (c === "\\") { f.text += src[i + 1] || ""; out += sp(src.slice(i, i + 2)); kept += src.slice(i, i + 2); i += 2; }
      else if (c === "`") { frames.pop(); if (!f.sub) strings.set(f.start, f.text); out += c; kept += c; i++; inTpl = false; }
      else if (c === "$" && n === "{") { f.sub = true; out += "  "; kept += "${"; i += 2; tpl.push(depth); inTpl = false; }
      else { f.text += c; out += sp(c); kept += c; i++; }
      continue;
    }
    if (c === "/" && n === "/") { let e = src.indexOf("\n", i); if (e < 0) e = src.length; out += sp(src.slice(i, e)); kept += sp(src.slice(i, e)); i = e; continue; }
    if (c === "/" && n === "*") { let e = src.indexOf("*/", i + 2); e = e < 0 ? src.length : e + 2; out += sp(src.slice(i, e)); kept += sp(src.slice(i, e)); i = e; continue; }
    if (c === "'" || c === '"') {
      let j = i + 1, val = "";
      while (j < src.length && src[j] !== c && src[j] !== "\n") { if (src[j] === "\\") { val += src[j + 1] || ""; j += 2; } else val += src[j++]; }
      const closed = src[j] === c;
      strings.set(i, val);
      out += c + sp(src.slice(i + 1, j)) + (closed ? c : "");
      kept += src.slice(i, closed ? j + 1 : j);
      i = closed ? j + 1 : j;
      continue;
    }
    if (c === "`") { frames.push({ start: i, text: "", sub: false }); out += c; kept += c; i++; inTpl = true; continue; }
    if (c === "}" && tpl.length && tpl[tpl.length - 1] === depth) { tpl.pop(); out += " "; kept += "}"; i++; inTpl = true; continue; }
    if (c === "{") depth++;
    else if (c === "}") depth--;
    out += c; kept += c; i++;
  }
  return { code: out, strings, kept };
}

// The top-level argument spans of the call whose "(" is at `open`, and the index of its ")"; null if unclosed.
function callArgs(code, open) {
  const list = []; let d = 0, start = open + 1;
  for (let k = open; k < code.length; k++) {
    const ch = code[k];
    if (ch === "(" || ch === "[" || ch === "{") d++;
    else if (ch === ")" || ch === "]" || ch === "}") {
      if (--d === 0) { if (code.slice(start, k).trim()) list.push([start, k]); return { list, close: k }; }
    } else if (ch === "," && d === 1) { list.push([start, k]); start = k + 1; }
  }
  return null;
}

// The parameters of a function literal beginning at `s`, as [text, offset] pairs, or null when none begins there.
function fnParams(code, s) {
  const lead = /^\s*/.exec(code.slice(s))[0].length, t = code.slice(s + lead);
  const fn = /^(?:async\s+)?function\b[^(]*\(/.exec(t), arrow = /^(?:async\s*)?\(/.exec(t);
  const m = fn || arrow;
  if (m) {
    const a = callArgs(code, s + lead + m[0].length - 1);
    if (!a) return null;
    if (!fn && !/^\s*(?::[^=;]*)?=>/.test(code.slice(a.close + 1))) return null;
    return a.list.map(([x, y]) => { const w = /^\s*/.exec(code.slice(x, y))[0].length; return [code.slice(x, y).trim(), x + w]; });
  }
  const one = /^((?:async\s+)?)([A-Za-z_$][\w$]*)\s*=>/.exec(t);
  return one ? [[one[2], s + lead + one[1].length]] : null;
}
const paramName = (p) => p.replace(/\s*[:=][\s\S]*$/, "").trim();

// `$` at i is a first parameter: first in a parenthesised list that is not a call, closing into `=>`, `{` or a return type.
function isParam(code, i, rest) {
  if (!/^\s*[,:)]/.test(rest)) return false;
  const before = code.slice(0, i).trimEnd();
  if (!before.endsWith("(")) return false;
  const pre = before.slice(0, -1).trimEnd();
  if (!(pre === "" || /[^\w$)\].]$/.test(pre) || /\basync$/.test(pre) || /\bfunction(\s+[\w$]+)?$/.test(pre))) return false;
  const a = callArgs(code, before.length - 1);
  return Boolean(a) && /^\s*(=>|\{|:)/.test(code.slice(a.close + 1));
}

const found = [];
// Mailbox adapter pins. Argument text, whitespace folded, read from the source: the heartbeat file in the session's own mailbox
// directory, a GET to the daemon over the one socket path, one mail prompt that is never asUser, and three env names.
const PINS = {
  "http.fetch": ["'http://ahd' + path, { method: 'GET', headers: { 'Ah-Client-Version': version }, socketPath: sock }"],
  "prompt.submit": [],
  "env.get": ["'AH_HOME'", "'HOME'"],
};
// ahdGet's socket is always the one the mail poll's last CLI answer reported (null until one has, and then no call is made).
const CALLERS = { ahdGet: ["link.socket"] };
const ONCE = new Set(["http.fetch"]);
for (const f of files) {
  const rel = path.relative(dir, f), src = fs.readFileSync(f, "utf8");
  let { code, strings, kept } = lex(src);
  const seenCalls = { "http.fetch": 0 };
  const at = (i, why) => found.push(`${rel}:${code.slice(0, i).split("\n").length}: ${why}`);
  // The pinned focus helper: found whole in the comment-blanked source, counted only where it is live code (not inside a
  // string), then cut out by offset so nothing else in this file is read as part of it. A copy elsewhere than register.tsx,
  // or a count other than one there, is a violation.
  let pinnedLive = false, pinned2Live = false, pinned3Live = false, pinned4Live = false, pinned5Live = false;
  const legitRefs = new Set(), legitRefs2 = new Set(), legitRefs3 = new Set(), legitRefs4 = new Set();
  {
    for (const [ptext, label] of [[helperText, "focus"], [helperText2, "dismiss"], [helperText3, "mail"], [helperText4, "view"], [helperText5, "nudge"], [helperText6, "nudge registration"], [helperText7, "tool ring"], [helperText8, "tool ring request"]]) {
      const pinned = new RegExp(ptext.split("\n").map((l) => l.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("[ \\t]*\\r?\\n"), "g");
      const hits = [...kept.matchAll(pinned)].filter((h) => code.slice(h.index, h.index + 24) === kept.slice(h.index, h.index + 24));
      if (rel === "register.tsx") {
        if (hits.length !== 1) at(0, hits.length ? `the pinned ${label} helper appears more than once` : `the pinned ${label} helper text is missing or altered`);
      } else if (hits.length) at(hits[0].index, `the pinned ${label} helper outside register.tsx`);
      if (rel === "register.tsx" && hits.length === 1) {
        const s0 = hits[0].index, e0 = s0 + hits[0][0].length, blank = (t) => t.replace(/[^\n]/g, " ");
        code = code.slice(0, s0) + blank(code.slice(s0, e0)) + code.slice(e0);
        kept = kept.slice(0, s0) + blank(kept.slice(s0, e0)) + kept.slice(e0);
        for (const k of [...strings.keys()]) if (k >= s0 && k < e0) strings.delete(k);
        if (label === "focus") pinnedLive = true; else if (label === "dismiss") pinned2Live = true; else if (label === "mail") pinned3Live = true; else if (label === "view") pinned4Live = true; else if (label === "nudge") pinned5Live = true;
      }
    }
    for (const m of kept.matchAll(/(?<![\w$])process(?![\w$])/g)) at(m.index, "the word process outside the pinned helpers");
    for (const tok of protoArg.split(" ")) for (const m of kept.matchAll(new RegExp(`(?<![\\w$])${tok}(?![\\w$])`, "g"))) at(m.index, `banned token ${tok}`);
  }
  // The value of the string literal that is the whole argument span, else null.
  const literalAt = ([s, e]) => { const k = s + /^\s*/.exec(code.slice(s))[0].length; return strings.has(k) && /^(['"])\s*\1$/.test(code.slice(k, e).trim()) ? strings.get(k) : null; };

  // A matcher object's canonical form (k=v pairs, keys sorted), or null when it is not plain string pairs.
  const canonOf = ([ms, me]) => {
    const body = /^\s*\{([\s\S]*)\}\s*$/.exec(src.slice(ms, me));
    const pairs = body ? body[1].split(",").map((x) => x.trim()).filter(Boolean).map((x) => /^(?:(['"])(\w+)\1|(\w+))\s*:\s*(['"])([^'"]*)\4$/.exec(x)) : [null];
    return pairs.every(Boolean) ? pairs.map((p) => `${p[2] || p[3]}=${p[5]}`).sort().join(",") : null;
  };
  // The hook spans of the on('ui.render', { component: 'AbovePrompt' }, hook) registrations: the only place BAND_DRAWABLE may be drawn.
  const bandSpans = [], paneSpans = [];
  for (const m of code.matchAll(/(?<![\w$.])on\s*\(/g)) {
    const a = callArgs(code, m.index + m[0].length - 1);
    if (a && a.list.length === 3 && literalAt(a.list[0]) === "ui.render" && canonOf(a.list[1]) === BAND_MATCHER) bandSpans.push(a.list[2]);
    if (a && a.list.length === 3 && literalAt(a.list[0]) === "ui.render" && canonOf(a.list[1]) === PANE_MATCHER) paneSpans.push(a.list[2]);
  }
  // Every on(...) call's span, so a registration nested inside the band hook does not inherit its exemption.
  const onSpans = [];
  for (const m of code.matchAll(/(?<![\w$.])on\s*\(/g)) { const a = callArgs(code, m.index + m[0].length - 1); if (a) onSpans.push([m.index, a.close]); }
  const inSpans = (spans, i) => spans.some(([s, e]) => i >= s && i < e && !onSpans.some(([os, oe]) => os >= s && os < e && i >= os && i <= oe));
  const inBand = (i) => inSpans(bandSpans, i) || inSpans(paneSpans, i);

  for (const m of src.matchAll(/\\u/g)) at(m.index, "a \\u escape");
  for (const m of code.matchAll(/(?<![\w$.])[A-Za-z_$][\w$]*/g)) if (TOKENS.has(m[0])) at(m.index, `banned token ${m[0]}`);
  for (const m of code.matchAll(/\bFunction\s*\(/g)) at(m.index, "banned token Function(");
  // The banned words, by token: identifiers, property names and JSX tags here; strings and plain templates below.
  for (const m of code.matchAll(/(?<![\w$])[A-Za-z_$][\w$]*(?![\w$])/g)) if (BANNED.has(m[0]) && !(BAND_DRAWABLE.has(m[0]) && inBand(m.index))) at(m.index, `banned word ${m[0]}`);
  for (const [k, v] of strings) if (BANNED.has(v)) at(k, `banned word '${v}'`);

  // Imports: register.tsx takes values only from ./view; any other import is `import type`, from
  // 'claude-code' or ./types/; nothing comes from ./tests/.
  for (const m of code.matchAll(/\b(import|export)\b(\s+type\b)?[^;'"]*?(['"])/g)) {
    const kw = m[1], typeOnly = Boolean(m[2]), spec = strings.get(m.index + m[0].length - 1);
    if (spec === undefined) continue;
    const head = code.slice(m.index, m.index + m[0].length);
    if (kw === "export" && !/\bfrom\s*['"]$/.test(head)) continue;
    const local = spec.replace(/\.(ts|tsx)$/, "");
    if (/(^|\/)tests(\/|$)/.test(local)) at(m.index, `an import from ${spec}`);
    else if (typeOnly) { if (!(spec === "claude-code" || local.startsWith("./types/"))) at(m.index, `a type import from ${spec}, not 'claude-code' or ./types/`); }
    else if (!(rel === "register.tsx" && local === "./view")) at(m.index, `a value ${kw} from ${spec}${rel === "register.tsx" ? ", not ./view" : ""}`);
  }

  // Functions with parameters, by name, so one passed to a `$` call by name is caught too.
  const withParams = new Set();
  for (const m of code.matchAll(/(?<![\w$.])(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*(?::[^=;]+)?=(?!=)/g)) { const p = fnParams(code, m.index + m[0].length); if (p && p.length) withParams.add(m[1]); }
  for (const m of code.matchAll(/\bfunction\s+([A-Za-z_$][\w$]*)\s*\(/g)) { const a = callArgs(code, m.index + m[0].length - 1); if (a && a.list.length) withParams.add(m[1]); }

  // Position rule: in a band hook, Button stands only as an unrenamed `const { … } = $.ui.resolve(x)` property at the hook's own
  // function depth, or as a JSX tag in the hook's own return (or expression body) with no function literal before it, no call paren
  // open around it and no assignment in that return. So a Button value or element cannot leave the hook by a binding.
  const GROUP = new Set(["return", "if", "while", "for", "switch", "typeof", "await", "void"]);
  const CTRL = new Set(["if", "for", "while", "switch", "catch", "with"]);
  const matchOpen = (close) => { let d = 0; for (let k = close; k >= 0; k--) { const ch = code[k]; if (")]}".includes(ch)) d++; else if ("([{".includes(ch) && --d === 0) return k; } return -1; };
  const isFnBlock = (b) => {
    const before = code.slice(0, b).trimEnd();
    if (before.endsWith("=>") || /\)\s*:\s*[\w$.<>[\], |&]*$/.test(before)) return true;
    if (!before.endsWith(")")) return false;
    const open = matchOpen(before.length - 1);
    if (open < 0) return false;
    const pre = code.slice(0, open).trimEnd(), w = /([\w$]+)$/.exec(pre);
    return pre.endsWith("*") || !(w && CTRL.has(w[1]));
  };
  const hookBody = (hs) => {
    const k = hs + /^\s*/.exec(code.slice(hs))[0].length, t = code.slice(k);
    const m = /^(?:async\s+)?function\b[^(]*\(/.exec(t) || /^(?:async\s*)?\(/.exec(t);
    const after = m ? callArgs(code, k + m[0].length - 1).close + 1 : k + /^(?:async\s+)?[A-Za-z_$][\w$]*\s*/.exec(t)[0].length;
    const bs = after + /^\s*(?::[^=;{]*)?(?:=>)?\s*/.exec(code.slice(after))[0].length;
    return { bs, block: code[bs] === "{" };
  };
  // Whether an operand may start after `prefix`: a `<` there opens a JSX tag, anywhere else it is the less-than operator.
  const startsOperand = (prefix) => {
    const t = prefix.trimEnd();
    return t === "" || /[(,{}?:>&|=[;!]$/.test(t) || /(?:^|[^\w$.])(?:return|typeof|await|void|yield|case|else)$/.test(t);
  };
  // Whether `text` assigns outside a JSX attribute name= and outside an onPress body.
  const hasAssign = (text) => {
    let inTag = false, bd = 0;
    for (let k = 0; k < text.length; k++) {
      const ch = text[k], nx = text[k + 1], pv = text[k - 1] || "";
      if (inTag) {
        if (ch === "{") {
          if (bd === 0 && /onPress\s*=\s*$/.test(text.slice(0, k))) { let d = 0; for (; k < text.length; k++) { if (text[k] === "{") d++; else if (text[k] === "}" && --d === 0) break; } continue; }
          bd++; continue;
        }
        if (ch === "}") { bd--; continue; }
        if (bd === 0 && ch === ">") { inTag = false; continue; }
        if (bd === 0 && ch === "=") continue;
      } else if (ch === "<" && /[A-Za-z]/.test(nx || "") && startsOperand(text.slice(0, k))) { inTag = true; bd = 0; continue; }
      if (ch !== "=") continue;
      if (nx === "=") { k++; if (text[k + 1] === "=") k++; continue; }
      if (nx === ">") continue;
      if (/[!<>]/.test(pv) && !/<<|>>/.test(text.slice(k - 2, k))) continue;
      return true;
    }
    return false;
  };
  // `t` with each completed onPress={…} value blanked: an earlier Button's handler is not a function literal around the next tag.
  const settled = (t) => {
    let out = t;
    for (const m of [...t.matchAll(/(?<![\w$.])onPress\s*=\s*\{/g)]) {
      let d = 0, e = -1;
      for (let k = m.index + m[0].length - 1; k < t.length; k++) { if (t[k] === "{") d++; else if (t[k] === "}" && --d === 0) { e = k; break; } }
      if (e >= 0) out = out.slice(0, m.index) + out.slice(m.index, e + 1).replace(/[^\n]/g, " ") + out.slice(e + 1);
    }
    return out;
  };
  for (const [hs, he, isPane] of [...bandSpans.map((x) => [...x, false]), ...paneSpans.map((x) => [...x, true])]) {
    const { bs, block } = hookBody(hs);
    const fnRegions = [];
    for (let k = bs + (block ? 1 : 0); k < he; k++) if (code[k] === "{" && isFnBlock(k)) { const a = callArgs(code, k); if (a) fnRegions.push([k, a.close]); }
    const own = (i) => !fnRegions.some(([x, y]) => i > x && i < y);
    const returns = block ? [...code.slice(bs, he).matchAll(/(?<![\w$.])return(?![\w$])/g)].map((r) => bs + r.index).filter(own) : [bs];
    const argStart = (r) => (block ? r + 6 : r);
    const argEnd = (r) => {
      if (!block) return he;
      let d = 0;
      for (let k = r + 6; k < he; k++) { const ch = code[k]; if ("([{".includes(ch)) d++; else if (")]}".includes(ch)) { if (--d < 0) return k; } else if (ch === ";" && d === 0) return k; }
      return he;
    };
    for (const m of code.slice(hs, he).matchAll(/(?<![\w$.])Button(?![\w$])/g)) {
      const i = hs + m.index;
      if (!(isPane ? inSpans(paneSpans, i) : inSpans(bandSpans, i))) continue;
      const before = code.slice(0, i), rest = code.slice(i + 6), tag = /<(\/?)\s*$/.exec(before);
      if (!tag) {
        const pat = /\bconst\s*\{[^{}]*$/.exec(before), end = rest.indexOf("}");
        const ok = pat && own(pat.index) && /[{,]\s*$/.test(before) && /^\s*[,}]/.test(rest) && end >= 0 &&
          /^\s*=\s*\$\s*\.\s*ui\s*\.\s*resolve\s*\(\s*[\w$]+\s*\)/.test(rest.slice(end + 1));
        if (!ok) at(i, "Button outside an unrenamed $.ui.resolve destructure at the band hook's own depth, or a JSX tag in its own return");
        continue;
      }
      const tpos = i - tag[0].length, r = returns.filter((x) => x < tpos).pop();
      let why = null, innermost = null;
      if (r === undefined || tpos >= argEnd(r)) why = "tag is not inside the band hook's own return";
      else {
        const seg = code.slice(argStart(r), tpos), open = [];
        for (let k = 0; k < seg.length; k++) { if (seg[k] === "(") open.push(k); else if (seg[k] === ")") open.pop(); }
        // The Pane hook alone: function literals before the tag may be at most two expression-bodied arrow callbacks, each the sole
        // argument of a literal `.map(` call, with no `$` in its parameters. mapParens holds the "(" of each such call.
        const mapParens = new Set();
        let fnWhy = null;
        if (tag[1] !== "/" && isPane) {
          const base = argStart(r);
          if (/\bfunction\b/.test(seg)) fnWhy = "tag sits behind a function keyword callback";
          // A finished onPress or literal .map( call before the tag holds no tag of this one, so its callback is not counted; every other callback is counted and shape-checked.
          const live = settled(seg), inner = new Map(), stack = [];
          for (let k = 0; k < live.length; k++) { if (live[k] === "(") stack.push(k); else if (live[k] === ")") stack.pop(); else if (live.startsWith("=>", k)) inner.set(k, stack[stack.length - 1]); }
          const finishedMap = (p) => p !== undefined && !open.includes(p) && live.slice(0, p).endsWith(".map");
          const arrows = [...live.matchAll(/=>/g)].filter((a) => !finishedMap(inner.get(a.index)));
          if (!fnWhy && arrows.length > 2) fnWhy = "more than two callbacks before the tag";
          for (const ar of arrows) {
            if (fnWhy) break;
            const before = seg.slice(0, ar.index).trimEnd();
            const ps = before.endsWith(")") ? matchOpen(base + before.length - 1) - base : (() => { const w = /([\w$]+)$/.exec(before); return w ? before.length - w[1].length : -1; })();
            const paramsText = before.endsWith(")") ? before.slice(ps) : before.slice(ps);
            const head = seg.slice(0, ps).trimEnd();
            if (ps < 0 || !head.endsWith(".map(") || seg.slice(0, ps).length !== head.length) fnWhy = "a callback before the tag is not the first thing in a literal .map( call";
            else if (/\$/.test(paramsText)) fnWhy = "a map callback's parameters contain $";
            else if (/^\s*\{/.test(seg.slice(ar.index + 2))) fnWhy = "a map callback has a block body";
            else {
              const open = base + head.length - 1, call = callArgs(code, open);
              if (!call || call.list.length !== 1) fnWhy = "a map callback is not the sole argument of its .map( call";
              else { mapParens.add(head.length - 1); innermost = paramsText; }
            }
          }
        }
        if (tag[1] !== "/" && !isPane && /=>|\bfunction\b/.test(settled(seg))) why = "tag sits inside a function literal";
        else if (fnWhy) why = fnWhy;
        else if (open.some((k) => { if (mapParens.has(k)) return false; const pre = seg.slice(0, k).trimEnd(), w = /([\w$]+)$/.exec(pre); return w ? !GROUP.has(w[1]) : /[)\]]$/.test(pre) || /\?\.$/.test(pre) || /[\w$>]>$/.test(pre); })) why = "tag sits inside a call's arguments";
        else if ((seg.match(/`/g) || []).length % 2) why = "tag sits inside a template literal";
        else if (hasAssign(code.slice(argStart(r), argEnd(r)))) why = "the return holds an assignment";
      }
      if (why) at(i, `Button ${why}`);
      else if (isPane && tag[1] !== "/") {
        // W3: the Pane Button is plain, labels with a bare identifier the innermost map callback destructures, and its onPress is
        // exactly one call of the focus helper with that same identifier.
        let end = -1, d = 0;
        for (let k = i + 6; k < code.length; k++) { const ch = code[k]; if (ch === "{") d++; else if (ch === "}") d--; else if (d === 0 && ch === ">" && code[k - 1] !== "=") { end = k; break; } }
        const attrs = end < 0 ? "" : code.slice(i + 6, end).replace(/\/\s*$/, "");
        let flat = attrs; for (let g = 0; g < 8; g++) flat = flat.replace(/\{[^{}]*\}/g, "{}");
        const labels = [...attrs.matchAll(/(?:^|\s)label\s*=\s*\{\s*([A-Za-z_$][\w$]*)\s*\}/g)], labelCount = (flat.match(/(?:^|\s)label\s*=/g) || []).length;
        const press = /(?:^|\s)onPress\s*=\s*\{/.exec(attrs);
        let pressText = null;
        if (press) { let dd = 0; for (let k = press.index + press[0].length - 1; k < attrs.length; k++) { if (attrs[k] === "{") dd++; else if (attrs[k] === "}" && --dd === 0) { pressText = attrs.slice(press.index + press[0].length, k); break; } } }
        const id = labels.length === 1 && labelCount === 1 ? labels[0][1] : null, esc = (x) => x.replace(/\$/g, "\\$");
        const call = id === null ? null : new RegExp(`^\\s*(?:async\\s*)?\\(\\s*\\)\\s*=>\\s*(?:\\{\\s*(?:try\\s*\\{\\s*)?(?:await\\s+)?${pinnedName}\\(\\s*\\$\\s*,\\s*${esc(id)}\\s*\\)\\s*;?\\s*(?:\\}\\s*catch\\s*(?:\\(\\s*[\\w$]*\\s*\\))?\\s*\\{\\s*\\}\\s*)?\\}|(?:await\\s+)?${pinnedName}\\(\\s*\\$\\s*,\\s*${esc(id)}\\s*\\))\\s*$`);
        if (call !== null && pressText !== null && call.test(pressText)) legitRefs.add(i + 6 + press.index + press[0].length + pressText.search(new RegExp(`(?<![\\w$])${pinnedName}(?![\\w$])`)));
        // Or a Pane tab: its onPress is exactly one write of the paneMode state with the `tab` the innermost map callback destructures, which only picks what the Pane draws.
        const keptPress = press && pressText !== null ? kept.slice(i + 6 + press.index + press[0].length, i + 6 + press.index + press[0].length + pressText.length) : null;
        const isTab = keptPress !== null && /^\s*\(\s*\)\s*=>\s*\$\.state\.set\(\s*\{\s*plugin:\s*'wrangl'\s*,\s*key:\s*'paneMode'\s*\}\s*,\s*tab\s*\)\s*$/.test(keptPress) && innermost !== null && /^\(\s*\{[^}]*(?<![\w$])tab(?![\w$])[^}]*\}\s*\)$/.test(innermost.replace(/\s+/g, " "));
        if (!/(?:^|\s)plain(?=\s|=|$)/.test(flat)) at(i, "a Pane Button without plain");
        else if (id === null) at(i, "a Pane Button whose label is not a bare identifier");
        else if (!isTab && (pressText === null || !call.test(pressText))) at(i, `a Pane Button onPress that is not exactly one ${pinnedName}($, <label>) call`);
        else if (!(innermost !== null && new RegExp(`^\\(\\s*\\{[^}]*(?<![\\w$])${esc(id)}(?![\\w$])[^}]*\\}\\s*\\)$`).test(innermost.replace(/\s+/g, " ")))) at(i, "a Pane Button label the innermost map callback does not destructure");
      }
      else if (!isPane && tag[1] !== "/") {
        // The band's Dismiss Button (key "ah-dismiss"): not plain, and its onPress is exactly one call of the dismiss helper.
        let end = -1, d = 0;
        for (let k = i + 6; k < code.length; k++) { const ch = code[k]; if (ch === "{") d++; else if (ch === "}") d--; else if (d === 0 && ch === ">" && code[k - 1] !== "=") { end = k; break; } }
        const attrs = end < 0 ? "" : code.slice(i + 6, end).replace(/\/\s*$/, ""), keptAttrs = end < 0 ? "" : kept.slice(i + 6, end);
        let flat = attrs; for (let g = 0; g < 8; g++) flat = flat.replace(/\{[^{}]*\}/g, "{}");
        if (/(?:^|\s)key\s*=\s*(['"])ah-dismiss\1/.test(keptAttrs.replace(/\{[^{}]*\}/g, "{}"))) {
          const press = /(?:^|\s)onPress\s*=\s*\{/.exec(attrs);
          let pressText = null;
          if (press) { let dd = 0; for (let k = press.index + press[0].length - 1; k < attrs.length; k++) { if (attrs[k] === "{") dd++; else if (attrs[k] === "}" && --dd === 0) { pressText = attrs.slice(press.index + press[0].length, k); break; } } }
          const call = new RegExp(`^\\s*(?:async\\s*)?\\(\\s*\\)\\s*=>\\s*(?:\\{\\s*(?:try\\s*\\{\\s*)?(?:await\\s+)?${pinnedName2}\\(\\s*\\$\\s*\\)\\s*;?\\s*(?:\\}\\s*catch\\s*(?:\\(\\s*[\\w$]*\\s*\\))?\\s*\\{\\s*\\}\\s*)?\\}|(?:await\\s+)?${pinnedName2}\\(\\s*\\$\\s*\\))\\s*$`);
          if (pressText !== null && call.test(pressText)) legitRefs2.add(i + 6 + press.index + press[0].length + pressText.search(new RegExp(`(?<![\\w$])${pinnedName2}(?![\\w$])`)));
          if (/(?:^|\s)plain(?=\s|=|$)/.test(flat)) at(i, "a Dismiss Button with plain");
          else if (pressText === null || !call.test(pressText)) at(i, `a Dismiss Button onPress that is not exactly one ${pinnedName2}($) call`);
        }
        // The band's View Button (key "ah-view"): not plain, and its onPress is exactly the pinned handler, which calls the view helper once.
        if (/(?:^|\s)key\s*=\s*(['"])ah-view\1/.test(keptAttrs.replace(/\{[^{}]*\}/g, "{}"))) {
          const press = /(?:^|\s)onPress\s*=\s*\{/.exec(attrs);
          let pressText = null;
          if (press) { let dd = 0; for (let k = press.index + press[0].length - 1; k < attrs.length; k++) { if (attrs[k] === "{") dd++; else if (attrs[k] === "}" && --dd === 0) { pressText = attrs.slice(press.index + press[0].length, k); break; } } }
          const fold = (x) => x.replace(/\s+/g, " ").trim(), pinnedPress = fold(lex(viewPress).code);
          if (pressText !== null && fold(pressText) === pinnedPress) legitRefs4.add(i + 6 + press.index + press[0].length + pressText.search(new RegExp(`(?<![\\w$])${pinnedName4}(?![\\w$])`)));
          if (/(?:^|\s)plain(?=\s|=|$)/.test(flat)) at(i, "a View Button with plain");
          else if (pressText === null || fold(pressText) !== pinnedPress) at(i, `a View Button onPress that is not the pinned handler of ${pinnedName4}`);
        }
      }
    }
  }

  // The focus helper is referenced exactly once outside its pinned text: as the callee of the Pane Button's onPress. Counted on code with
  // comments and strings blanked, so a mention there is no reference; a call from another hook, an alias, a value pass, an export or a second
  // call site all fail, and so does a pinned helper that nothing calls.
  for (const h of code.matchAll(new RegExp(`(?<![\\w$])${pinnedName}(?![\\w$])`, "g"))) if (!legitRefs.has(h.index)) at(h.index, "the focus helper is referenced outside the one Pane onPress call");
  if (rel === "register.tsx" && pinnedLive && legitRefs.size === 0) at(0, "the pinned focus helper is never called by the Pane (an unused exec site)");
  if (legitRefs.size > 1) at([...legitRefs][1], "the focus helper is called from more than one Pane onPress");
  // The dismiss helper the same way: referenced exactly once outside its pinned text, as the callee of the band's Dismiss Button onPress.
  for (const h of code.matchAll(new RegExp(`(?<![\\w$])${pinnedName2}(?![\\w$])`, "g"))) if (!legitRefs2.has(h.index)) at(h.index, "the dismiss helper is referenced outside the one band Dismiss onPress call");
  if (rel === "register.tsx" && pinned2Live && legitRefs2.size === 0) at(0, "the pinned dismiss helper is never called by the band (an unused exec site)");
  if (legitRefs2.size > 1) at([...legitRefs2][1], "the dismiss helper is called from more than one band onPress");
  // The view helper the same way: referenced exactly once outside its pinned text, as the callee inside the band's View Button onPress.
  for (const h of code.matchAll(new RegExp(`(?<![\\w$])${pinnedName4}(?![\\w$])`, "g"))) if (!legitRefs4.has(h.index)) at(h.index, "the view helper is referenced outside the one band View onPress");
  if (rel === "register.tsx" && pinned4Live && legitRefs4.size === 0) at(0, "the pinned view helper is never called by the band (an unused exec site)");
  if (legitRefs4.size > 1) at([...legitRefs4][1], "the view helper is called from more than one band onPress");
  // The mail helper is referenced exactly once outside its pinned text, as the callback of the timer `$.clock.every(POLL_MS, () => mailCycle($, ctx))`
  // in mailPoll, never inside an on(...) hook (so never from a press or an event): it re-arms itself from inside its own text.
  if (rel === "register.tsx" && pinned3Live) {
    const timer = new RegExp(`(?<![\\w$.])\\$\\s*\\.\\s*clock\\s*\\.\\s*every\\s*\\(\\s*POLL_MS\\s*,\\s*\\(\\s*\\)\\s*=>\\s*(${pinnedName3})\\s*\\(\\s*\\$\\s*,\\s*ctx\\s*\\)\\s*\\)`, "g");
    for (const m of code.matchAll(timer)) if (!onSpans.some(([s0, e0]) => m.index >= s0 && m.index <= e0)) legitRefs3.add(m.index + m[0].indexOf(pinnedName3));
  }
  for (const h of code.matchAll(new RegExp(`(?<![\\w$])${pinnedName3}(?![\\w$])`, "g"))) if (!legitRefs3.has(h.index)) at(h.index, "the mail helper is referenced outside the one timer call in mailPoll");
  if (rel === "register.tsx" && pinned3Live && legitRefs3.size === 0) at(0, "the pinned mail helper is never called by the timer (an unused exec site)");
  if (legitRefs3.size > 1) at([...legitRefs3][1], "the mail helper is called from more than one timer");

  // A Button's onPress is an inline arrow whose parameter, if any, is not named $; its body is lexed like any other code.
  for (const m of code.matchAll(/(?<![\w$.])onPress\s*(?:=\s*\{|:)\s*/g)) {
    const s = m.index + m[0].length, p = fnParams(code, s);
    if (!p || /^\s*(?:async\s+)?function\b/.test(code.slice(s))) at(m.index, "onPress is not an inline arrow function");
    else if (p.some(([t]) => paramName(t) === "$")) at(m.index, "onPress takes a parameter named $");
  }

  // A same-file helper that may be handed $: declared exactly once, as const <name> = (async)? ($ …) => …, and never bound again.
  // `before` ends in a member access, whatever whitespace sits between the dot and the name.
  const isMember = (before) => /(?<!\.\.)\??\.\s*$/.test(before) && !/\.\.\.\s*$/.test(before);
  const helperOk = (name) => {
    let decls = 0, other = 0;
    for (const h of code.matchAll(new RegExp(`(?<![\\w$])${name.replace(/\$/g, "\\$")}(?![\\w$])`, "g"))) {
      const before = code.slice(0, h.index), after = code.slice(h.index + name.length);
      if (isMember(before)) continue;
      const eq = (/\s$/.test(before) && /(?<![\w$.])const$/.test(before.trimEnd())) ? /^\s*(?::[^=;]+)?=(?!=)/.exec(after) : null;
      if (eq) {
        const at0 = h.index + name.length + eq[0].length, p = fnParams(code, at0);
        if (p && p.length && paramName(p[0][0]) === "$" && !/^\s*(?:async\s+)?function\b/.test(code.slice(at0))) decls++; else other++;
      } else if (/^\s*\(/.test(after)) {
        // A call site, unless the parenthesis closes into a body: a function, generator or method declaration binds the name.
        const a = callArgs(code, h.index + name.length + /^\s*/.exec(after)[0].length);
        if (!a || /^\s*(?::[^;{}=]*)?\{/.test(code.slice(a.close + 1)) || /\bfunction\s*\*?\s*$/.test(before)) other++;
      } else other++;
    }
    if (name === pinnedName) return pinnedLive && decls === 0 && other === 0;
    if (name === pinnedName2) return pinned2Live && decls === 0 && other === 0;
    if (name === pinnedName3) return pinned3Live && decls === 0 && other === 0;
    if (name === pinnedName4) return pinned4Live && decls === 0 && other === 0;
    return decls === 1 && other === 0;
  };
  // A pinned helper's name is bound once, inside the cut-out text: any other binding of it is a violation, call or no call.
  for (const [pinName, live, label] of [[pinnedName, pinnedLive, "focus"], [pinnedName2, pinned2Live, "dismiss"], [pinnedName3, pinned3Live, "mail"], [pinnedName4, pinned4Live, "view"], [pinnedName5, pinned5Live, "nudge"]]) {
    if (!live) continue;
    const pn = pinName.replace(/\$/g, "\\$");
    for (const h of code.matchAll(new RegExp(`(?<![\\w$])${pn}(?![\\w$])`, "g"))) {
      const before = code.slice(0, h.index), after = code.slice(h.index + pinName.length);
      if (isMember(before)) continue;
      const binds = (/\s$/.test(before) && /(?<![\w$.])(?:const|let|var|function\*?|class)$/.test(before.trimEnd())) || /^\s*(?::[^=;]+)?=(?!=)/.test(after) || /^\s*\(/.test(after) && (() => { const a = callArgs(code, h.index + pinName.length + /^\s*/.exec(after)[0].length); return !a || /^\s*(?::[^;{}=]*)?\{/.test(code.slice(a.close + 1)); })();
      if (binds) at(h.index, `the pinned ${label} helper's name is bound again`);
    }
  }
  // The relabel's name occurs only inside its two cut-out texts (the handler and the registration loop that names it): any other
  // occurrence, bound or only referenced, is a violation.
  if (pinned5Live) {
    for (const h of code.matchAll(new RegExp(`(?<![\\w$])${pinnedName5}(?![\\w$])`, "g"))) if (!isMember(code.slice(0, h.index))) at(h.index, "the nudge relabel is named outside its pinned text");
  }

  // The callers of the two helpers that hold a pinned call: the value that decides where a write or a fetch goes is
  // pinned at each call site, so a new caller cannot send the heartbeat or a request anywhere else. This is a
  // tripwire, not a proof: it matches the argument's text, so it catches a changed or added call, not what a name
  // such as `b.dir` or `env.sock` holds at run time (that is held by loadReg/boxUnder and mailEnv, and by their tests).
  for (const [fn, okArgs] of Object.entries(CALLERS)) {
    for (const m of code.matchAll(new RegExp(`(?<![\\w$.])${fn}\\s*\\(`, "g"))) {
      const open = m.index + m[0].length - 1, a = callArgs(code, open);
      const arg = a && a.list[1] ? src.slice(a.list[1][0], a.list[1][1]).replace(/\s+/g, " ").trim() : null;
      if (rel !== "register.tsx") at(m.index, `${fn} called outside register.tsx`);
      else if (arg === null || !okArgs.includes(arg)) at(m.index, `${fn} called with ${arg === null ? "no second argument" : arg}, not one of ${okArgs.join(" | ")}`);
    }
  }

  for (const m of code.matchAll(/(?<![\w$])\$(?![\w$])/g)) {
    const i = m.index, rest = code.slice(i + 1);
    if (rel === "view.ts") { at(i, "view.ts holds a $"); continue; }
    const call = /^\s*\.\s*([A-Za-z_$][\w$]*)\s*\.\s*([A-Za-z_$][\w$]*)\s*/.exec(rest);
    if (call) {
      if (!CALLS.has(`${call[1]}.${call[2]}`)) at(i, `$.${call[1]}.${call[2]} is not an allowed call`);
      const open = i + 1 + call[0].length;
      const a = code[open] === "(" ? callArgs(code, open) : null;
      // The pinned calls: only register.tsx, only the exact argument text, and the one-per-file ones once.
      const pinKey = `${call[1]}.${call[2]}`;
      if (PINS[pinKey]) {
        const argText = a ? src.slice(open + 1, a.close).replace(/\s+/g, " ").trim() : null;
        if (rel !== "register.tsx") at(i, `$.${pinKey} outside register.tsx`);
        else if (argText === null || !PINS[pinKey].some((p) => p === argText)) at(i, `$.${pinKey} with arguments other than its pinned form`);
        if (ONCE.has(pinKey) && rel === "register.tsx" && ++seenCalls[pinKey] > 1) at(i, `$.${pinKey} more than once`);
      }
      for (const span of a ? a.list : []) {
        const p = fnParams(code, span[0]), id = code.slice(...span).trim();
        if ((p && p.length) || withParams.has(id)) at(span[0], `a callback given to $.${call[1]}.${call[2]} declares parameters`);
      }
      continue;
    }
    if (/^\s*\./.test(rest)) { at(i, "$.<noun> without a method"); continue; }
    if (isParam(code, i, rest)) continue;
    // name($ …): $ is the first argument of a call to a once-declared, $-first helper of this file.
    const callee = /^\s*[,)]/.test(rest) ? /(?<![\w$])([A-Za-z_$][\w$]*)\s*\(\s*$/.exec(code.slice(0, i)) : null;
    if (callee && !isMember(code.slice(0, callee.index)) && !(/\s$/.test(code.slice(0, callee.index)) && /\b(?:new|function)$/.test(code.slice(0, callee.index).trimEnd())) && helperOk(callee[1])) continue;
    at(i, "$ used other than as a first parameter, $.<noun>.<method> or the first argument of a $-first helper of this file");
  }

  // register's definitions: (on) or (on, options). Their `on` is the one place `on` may stand other than as a callee.
  const onParams = new Set();
  let registers = 0;
  for (const m of code.matchAll(/(?<![\w$.])register\b/g)) {
    const i = m.index, after = code.slice(i + "register".length);
    const def = /^\s*(?::\s*[\w$.<>[\], ]+)?\s*=(?!=)\s*/.exec(after), decl = /\bfunction\s*$/.test(code.slice(0, i));
    if (!def && !decl) continue;
    registers++;
    const p = def ? fnParams(code, i + "register".length + def[0].length) : fnParams(code, code.slice(0, i).search(/\bfunction\s*$/));
    if (!p) { at(i, "register is not defined as a function literal"); continue; }
    if (p.length >= 1 && paramName(p[0][0]) === "on") onParams.add(p[0][1]);
    if (paramName((p[0] || [""])[0]) !== "on" || p.length > 2 || (p.length === 2 && paramName(p[1][0]) !== "options")) at(i, "register's parameters must be (on) or (on, options)");
  }
  if (modules.includes(path.normalize(rel)) && !registers) at(0, "the hooks module defines no register");

  for (const m of code.matchAll(/(?<![\w$.])on(?![\w$])/g)) {
    const i = m.index;
    if (onParams.has(i)) continue;
    const open = /^\s*\(/.exec(code.slice(i + 2));
    if (!open) { at(i, "on used other than as register's first parameter or the callee of on('<event>', …)"); continue; }
    const a = callArgs(code, i + 2 + open[0].length - 1);
    if (!a || !a.list.length) { at(i, "on( without arguments"); continue; }
    const ev = literalAt(a.list[0]);
    if (ev === null) { at(i, "on( with an event that is not a string literal"); continue; }
    if (!EVENTS.has(ev)) at(i, `on('${ev}') is not an allowed registration`);
    const wants = MATCHERS[ev] ? 3 : 2;
    if (a.list.length !== wants) { at(i, `on('${ev}') takes ${MATCHERS[ev] ? "exactly one of its matchers and" : "no matcher, only"} an inline hook`); continue; }
    if (MATCHERS[ev]) {
      const canon = canonOf(a.list[1]);
      if (!MATCHERS[ev].includes(canon)) at(a.list[1][0], `on('${ev}') matcher is not one of ${MATCHERS[ev].join(" | ")}`);
    }
    const hook = a.list[a.list.length - 1], p = fnParams(code, hook[0]);
    if (!p) at(hook[0], `on('${ev}') hook is not an inline arrow or function literal`);
    else if (p.length && paramName(p[0][0]) !== "$") at(hook[0], `on('${ev}') hook's first parameter must be $ (or none)`);
  }
}
process.stdout.write(found.join("\n"));
JS
  ); rc=$?
  if [ "$rc" != 0 ]; then echo "the lexer crashed (exit $rc): ${out:0:300}"; else printf '%s' "$out"; fi
}

# <plugin dir>: the validate net's verdict on it: each printed registration or call outside the lists,
# or why validate failed; empty when clean. A registration prints as `event` or `event{k=v, …}`.
valnet() {
  local out rc
  mkdir -p "$SANDBOX/home/.claude/ah2"
  out=$(cd "$SANDBOX" && HOME="$SANDBOX/home" CLAUDE_CONFIG_DIR="$SANDBOX/home/.claude" perl -e 'alarm shift; exec @ARGV' 120 claude plugin validate "$1" 2>&1); rc=$?
  if [ "$rc" != 0 ]; then echo "validate failed (exit $rc): ${out:0:300}"; return; fi
  printf '%s\n' "$out" | node -e '
    const [calls, events, matchers] = process.argv.slice(1, 4).map((s) => s.split(" "));
    const net = process.argv[4].split("|"), netHooks = process.argv[5].split("|");
    const M = {};
    for (const m of matchers) { const [ev, canon] = m.split(/:(.*)/s); (M[ev] = M[ev] || []).push(canon); }
    const lines = require("fs").readFileSync(0, "utf8").split("\n");
    const bad = [];
    let hooks = 0;
    for (const line of lines) {
      const h = / \.\/\S+ hooks: (.*)$/.exec(line), c = / \.\/\S+ calls: (.*)$/.exec(line);
      if (h) {
        const items = []; let d = 0, cur = "";
        // a newer validate adds a note after a hook printed with no matcher: "ui.render (default matcher key: plugin, component)"
        for (const ch of h[1].replace(/ \(default matcher key: [^)]*\)/g, "")) { if (ch === "{") d++; if (ch === "}") d--; if (ch === "," && d === 0) { items.push(cur); cur = ""; } else cur += ch; }
        items.push(cur);
        for (const item of items.map((x) => x.trim()).filter(Boolean)) {
          hooks++;
          if (netHooks.includes(item)) continue;
          const m = /^([^{]+?)(?:\{(.*)\})?$/.exec(item), ev = m[1].trim();
          if (!events.includes(ev)) { bad.push(`hook ${item}: not an allowed event`); continue; }
          if (m[2] === undefined) { if (M[ev]) bad.push(`hook ${item}: needs one of its matchers`); continue; }
          const canon = m[2].split(",").map((x) => x.trim()).filter(Boolean).sort().join(",");
          if (!(M[ev] || []).includes(canon)) bad.push(`hook ${item}: matcher is not one of ${(M[ev] || ["(none)"]).join(" | ")}`);
        }
      }
      if (c && c[1].trim() !== "nothing on $") {
        // Items are comma-separated, but a `(via a, b)` note holds commas of its own.
        const items = []; let pd = 0, cur = "";
        for (const ch of c[1]) { if (ch === "(") pd++; if (ch === ")") pd--; if (ch === "," && pd === 0) { items.push(cur); cur = ""; } else cur += ch; }
        items.push(cur);
        for (const x of items.map((s) => s.trim().replace(/^\$\./, "")).filter(Boolean)) if (!calls.includes(x.replace(/ \(via [\w$, ]+\)$/, "")) && !net.includes(x)) bad.push(`call ${x}: not an allowed call`);
      }
    }
    if (!hooks) bad.push("validate printed no hooks");
    process.stdout.write(bad.join("\n"));
  ' "$ALLOWED_CALLS" "$ALLOWED_EVENTS" "$MATCHERS" "$NET_EXTRA_CALLS" "$NET_NUDGE_HOOKS|$NET_BRIDGE_HOOKS"
}

check "the mod's hooks module exists, so the guard has source to read" '[ -f "$PLUGIN/mod/register.tsx" ]'
OUT=$(violations "$PLUGIN/mod")
check "the module source passes the lexer" '[ -z "$OUT" ]'
OUT=$(valnet "$PLUGIN")
check "validate passes, and every hook and call it reports is on the lists, matchers included" '[ -z "$OUT" ]'

# The one Pane Button call the real register.tsx makes, as a scratch hook.
PANE_OK="on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async (\$, e, next) => { const { Box, Button } = \$.ui.resolve(e); return (<Box>{e.props.rows.map(({ name }) => <Box key={name}><Button plain label={name} onPress={() => focusMember(\$, name)} /></Box>)}</Box>) })"
# The one Dismiss Button call the real register.tsx makes, as a scratch band hook.
DISMISS_OK="on('ui.render', { component: 'AbovePrompt' }, async (\$, e, next) => { const { Box, Button } = \$.ui.resolve(e); return (<Box><Button key=\"ah-dismiss\" label=\"Dismiss\" onPress={() => dismissTeam(\$)} /></Box>) })"
# The one View Button call the real register.tsx makes, as a scratch band hook.
VIEW_OK="on('ui.render', { component: 'AbovePrompt' }, async (\$, e, next) => { const { Box, Button } = \$.ui.resolve(e); return (<Box><Button key=\"ah-view\" label=\"View\" onPress={$VIEW_PRESS} /></Box>) })"
# The same-file helpers the View Button's onPress hands $ to, as scratch stubs.
VIEW_SUPPORT="const paneShown = async (\$: any) => false; const paneClose = async (\$: any) => {}; const paneOpen = async (\$: any, member: boolean, toastFor: number) => {}; const viewCtx = async (\$: any) => null"
# The one call of the mail helper the real register.tsx makes (the timer in mailPoll), as a scratch line outside register.
SHOW_OK="const showEscalation = (\$: any, ctx: any, a: any): void => { ctx.st.escalation = a.escalation }"
NOTE_OK="const noteMail = async (\$: any, ctx: any, a: any, s: string): Promise<void> => { ctx.st.escalation = s }"
MAIL_OK="const mailPoll = (\$: any, ctx: any) => { ctx.st.timer = \$.clock.every(POLL_MS, () => mailCycle(\$, ctx)) }"
# <line>...: the lexer's verdict on a scratch module holding only those lines (in register) and the pinned helpers.
paneonly() {
  copy_mod
  { echo "import type { Register } from 'claude-code'"; echo "export const register: Register = (on, options) => {"; printf '  %s\n' "$@" "$DISMISS_OK" "$VIEW_OK"; printf '%s\n' "$NUDGE_REG" "$RING_REG"; echo "}"; printf '%s\n' "$RING_HELPER" "$NUDGE_HOOK" "$HELPER" "$HELPER2" "$HELPER3" "$HELPER4" "$MAIL_OK" "$VIEW_SUPPORT"; } > "$SANDBOX/mod/register.tsx"
  violations "$SANDBOX/mod"
}
# <line>...: the lexer's verdict on a scratch copy of mod/ with those lines appended to register.tsx.
copy_mod() { rm -rf "${SANDBOX:?}/mod"; cp -R "$PLUGIN/mod" "$SANDBOX/mod"; }
planted() { copy_mod; printf '%s\n' "$@" >> "$SANDBOX/mod/register.tsx"; violations "$SANDBOX/mod"; }
# Each planted case must fail the lexer on its own; validate never runs on these copies.
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(planted "$line")
  check "lexer alone catches: $line  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
done <<'EOF'
$.session.authorize()
$.session.append()
$.prompt.fill()
$.ui.dismiss()
$.turn.abort()
$.mcp.call()
$.command.run()
$.config.set()
$.http.fetch()
$.store.set()
$.ui.copy()
$.process.run(['herdr','agent','get'])
const { session } = $
const s = $.session
$['session']
f($)
globalThis
on('classic.PermissionRequest', ($, e, next) => next(e))
on('*', ($, e, next) => next(e))
on('tool.check', ($, e, next) => next(e))
on('fs.write', ($, e, next) => next(e))
on(name, ($, e, next) => next(e))
on('session.start', (env, e, next) => env.session.authorize())
on('session.start', ({ session }, e, next) => session.authorize())
const help = (x) => x.prompt.fill(); on('session.start', (env, e, next) => help(env))
export const register = r => { r('tool.check', ($, e, next) => next(e)) }
on('session.start', (env, e, next) => (env.session as any)[k]())
on('session.start', (env, e, next) => env['session']['append']())
on('session.start', (env, e, next) => { const s = env.session; s.authorize() })
\u0024.session.authorize()
\u006fn('tool.check', ($, e, next) => next(e))
on('\u0074ool.check', ($, e, next) => next(e))
on('session.start', h)
$.clock.every(2000, (env) => env.prompt.fill())
const tick = (env) => env.prompt.fill(); on('session.start', ($, e, next) => { $.clock.every(2000, tick); return next(e) })
on('session.start', function ($, e, next) { return arguments[0].prompt.fill() })
on('session.start', function ($, e, next) { return this.prompt.fill() })
on('command.run', ($, e, next) => ({ text: 'ok' }))
on('command.run', { command: 'wrangl-hierarchy-pane' }, ($, e, next) => ({ text: 'ok', context: ['x'] }))
on('command.run', { command: 'wrangl-hierarchy-pane' }, ($, e, next) => ({ text: 'ok', exitCode: 0 }))
on('command.run', { command: 'other' }, ($, e, next) => ({ text: 'ok' }))
on('ui.render', ($, e, next) => next(e))
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, ($, e, next) => <Link url="x">y</Link>)
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, ($, e, next) => <Button>b</Button>)
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, ($, e, next) => ({ type: 'Link', url: 'x' }))
const extra = (r) => r('tool.check', ($, e, next) => next(e)); extra(on)
const pass = (x, r) => 0; pass(1, on)
import { fixtures } from './tests/fixtures.ts'
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, ($, e, next) => h(els.Link, { url: 'x' }, 'y'))
on('session.start', async ($, e, next) => { const { Button } = $.ui.resolve(e); return next(e) })
on('command.run', { command: 'wrangl-hierarchy-pane' }, ($, e, next) => ({ text: 'ok', [`context`]: ['x'] }))
on('ui.close', ($, e, next) => ({ deny: 'x' }))
on('ui.close', ($, e, next) => next(e))
on('ui.close', { id: 'other' }, ($, e, next) => next(e))
import { update } from 'claude-code'
on('ui.render', { component: 'Pane', requestId: 'other' }, ($, e, next) => next(e))
on('command.run', { command: 'wrangl-hierarchy-pane' }, async ($, e, next) => { const { Button } = $.ui.resolve(e); return ({ text: 'ok' }) })
on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => <Button onPress={() => $.session.authorize()}>x</Button>)
on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => <Button onPress={($) => $.ui.toast('x')}>x</Button>)
on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => <Button onPress={go}>x</Button>)
on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => <Input />)
JSON.stringify($)
x.f($)
const f = ($) => 1; const f = ($) => 2; f($)
const f = ($) => 1; const g = (f) => f; f($)
const f = (env) => env.session.authorize(); f($)
const f = ($, e) => 1; f(e, $)
const h = ($) => 0; const x = { h(env) { return env.session.authorize() } }; x. h($)
const h = ($) => 0; class K { h(env) { return env.session.authorize() } }; new K(). h($)
const h = ($) => 0; { function* h(env) { env.session.authorize() } h($).next() }
const h = ($) => 0; { function *h(env) { env.session.authorize() } h($).next() }
const h = ($) => 0; { async function* h(env) { env.session.authorize() } h($).next() }
on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => { on('ui.render', { component: 'Pane', requestId: 'ah-status' }, ($, e, next) => <Button onPress={() => 0} />); return next(e) })
let B; on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => { const { Button } = $.ui.resolve(e); B = Button; return next(e) }); on('ui.render', { component: 'Pane', requestId: 'ah-status' }, ($, e, next) => <B onPress={() => 0} />)
let mk; on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => { const { Button } = $.ui.resolve(e); mk = () => <Button onPress={() => 0} />; return next(e) }); on('ui.render', { component: 'Pane', requestId: 'ah-status' }, ($, e, next) => mk())
let el; on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => { const { Button } = $.ui.resolve(e); el = <Button onPress={() => 0} />; return next(e) }); on('ui.render', { component: 'Pane', requestId: 'ah-status' }, ($, e, next) => el)
let el; on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => { const { Button } = $.ui.resolve(e); return (el = <Box><Button onPress={() => 0} /></Box>) }); on('ui.render', { component: 'Pane', requestId: 'ah-status' }, ($, e, next) => el)
const keep = (x) => x; on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => { const { Button } = $.ui.resolve(e); return keep(<Box><Button onPress={() => 0} /></Box>) })
let B; on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => { const { Button: B } = $.ui.resolve(e); return <B onPress={() => 0} /> })
let x; on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => { const { Button } = $.ui.resolve(e); return <Button onPress={() => { x = <Button onPress={() => 0} /> }} /> })
let s; on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => { const { Button } = $.ui.resolve(e); const q = 1, w = 2; return (q <w, s = <Button onPress={() => 0} />) }); on('ui.render', { component: 'Pane', requestId: 'ah-status' }, ($, e, next) => s)
let s; const keep = (q, x) => { s = x; return x }; on('ui.render', { component: 'AbovePrompt' }, ($, e, next) => { const { Button } = $.ui.resolve(e); return keep`${<Button />}` }); on('ui.render', { component: 'Pane', requestId: 'ah-status' }, ($, e, next) => s)
await $.fs.write(box + '/acks/x', 't')
await $.fs.write('/tmp/elsewhere', text)
await $.fs.write(box + '/adapters/' + id + '.json', text)
await $.fs.write(box + '/adapter.json', text)
await $.http.fetch('http://ahd' + path, { method: 'GET', headers: { 'Ah-Client-Version': version }, socketPath: '/tmp/other.sock' })
await $.http.fetch('http://ahd' + path, { method: 'GET', headers: { 'Ah-Client-Version': version }, socketPath: sock })
$.prompt.submit({ text: 'x' })
$.prompt.submit({ text: buildPrompt(groups), asUser: true })
$.prompt.submit({ text: buildPrompt(groups) })
await $.env.get('PATH')
await $.env.get(name)
await writeHeartbeat($, '/anywhere', 'x')
await writeHeartbeat($, box, 'x')
await writeHeartbeat($)
await ahdGet($, '/tmp/other.sock', version, '/v1/x')
await ahdGet($, sock, version, '/v1/x')
await ahdGet($)
import { x } from '../hooks/lib-mailbox.mjs'
import { x } from '../hooks/lib-mail-core.mjs.evil'
on('prompt.submit', { origin: 'x' }, ($, e, next) => next(e))
EOF

# The pinned focus helper (W4): the one process, matched whole. Each planted form must fail the lexer on its own.
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(planted "$line")
  check "lexer alone catches: $line  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
done <<'EOF'
$.process.spawn(['herdr'])
await $.process.run(['herdr', 'agent', 'focus', 'x'])
const openPane2 = async ($) => { await $.process.run(['herdr']) }
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { await $.process.run(['herdr', 'agent', 'focus', 'x']); return next(e) })
const p = $.process
const { run } = $.process
$['process']
const q = "process"
RegExp.prototype.test = () => true
const focusMember = ($) => 0
function focusMember(x) { return x }
let focusMember = 1
focusMember = null
EOF
# <from> <to> [line...]: the lexer's verdict on a scratch copy whose pinned helper has <from> replaced by <to>, with the lines appended.
altered() {
  copy_mod
  node -e 'const fs = require("fs"), p = process.argv[1], [from, to] = [process.argv[2], process.argv[3]]; const t = fs.readFileSync(p, "utf8"); const i = t.indexOf("export const focusMember"); if (i < 0 || !t.slice(i).includes(from)) { console.error("no such text in the helper"); process.exit(2); } fs.writeFileSync(p, t.slice(0, i) + t.slice(i).replace(from, () => to));' "$SANDBOX/mod/register.tsx" "$1" "$2" || { echo "scratch edit failed"; return; }
  shift 2; [ $# -gt 0 ] && printf '%s\n' "$@" >> "$SANDBOX/mod/register.tsx"
  violations "$SANDBOX/mod"
}
altcase() { # <label> <from> <to> [line...]
  local label=$1; shift
  OUT=$(altered "$@")
  check "lexer alone catches a changed helper: $label  [${OUT%%$'\n'*}]" '[ -n "$OUT" ] && [ "$OUT" != "scratch edit failed" ]'
}
altcase "3a argv element changed" "'focus'" "'get'"
altcase "3b a fifth argv element" "'focus', name]" "'focus', name, 'x']"
altcase "3c a spread" "['herdr', 'agent', 'focus', name]" "[...['herdr', 'agent', 'focus'], name]"
altcase "3d a template string" "'focus'" '`focus`'
altcase "3e sh -c" "['herdr', 'agent', 'focus', name]" "['sh', '-c', name]"
altcase "3f a second init key" "{ timeoutMs: 5000 }" "{ timeoutMs: 5000, cwd: '/' }"
altcase "3g the pattern check removed" "typeof name !== 'string' || !/^[a-z][a-z0-9_-]{0,31}\$/.test(name)" "typeof name !== 'string'"
altcase "3h success on any exit" "run.exitCode === 0" "run.exitCode >= 0"
altcase "3i the timeout changed" "5000" "600000"
altcase "8 the pattern as a constant outside the pinned text" "/^[a-z][a-z0-9_-]{0,31}\$/.test(name)" "NAME_RE.test(name)" "const NAME_RE = /.*/"
OUT=$(planted "$HELPER")
check "lexer alone catches: the helper text present twice  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "more than once"'
OUT=$(altered "'focus'" "'get'" "/* $HELPER */")
check "lexer alone catches: an altered live helper with the pinned text in a comment  [${OUT%%$'\n'*}]" '[ -n "$OUT" ] && printf "%s" "$OUT" | grep -q "altered"'
copy_mod; sed -i.bak "s#^export const focusMember#const focusMember#" "$SANDBOX/mod/register.tsx"; rm -f "$SANDBOX/mod/register.tsx.bak"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: the helper without its export (a changed text)  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
copy_mod; printf '%s\n' "$HELPER" > "$SANDBOX/mod/other.ts"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: the pinned text in a file other than register.tsx  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "outside register.tsx"'
OUT=$(violations "$PLUGIN/mod")
check "the repo's own register.tsx holds the helper exactly once and the word process nowhere else" '[ -z "$OUT" ] && [ "$(grep -c "export const focusMember" "$PLUGIN/mod/register.tsx")" = 1 ]'

# The Pane hook may draw a Button per row (W3): plain, labelled by a bare identifier the innermost .map callback destructures, and
# an onPress that is exactly one focus-helper call with that identifier. Each other form must fail the lexer on its own.
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(paneonly "$line")
  check "lexer alone catches: $line  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
done <<'EOF'
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ a, b }) => <Button plain label={a} onPress={() => focusMember($, b)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => <Button label={name} onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => <Button plain label={String(name)} onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => <Button plain label={`x`} onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => <Button plain label="x" onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => <Button plain label={name} onPress={() => { focusMember($, name); other() }} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => <Button plain label={name} onPress={() => focusMember($, name, $)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => <Button plain label={name} onPress={() => x.focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => <Button plain label={name} onPress={() => $.ui.toast(name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map((r) => <Button plain label={r} onPress={() => focusMember($, r)} />)}</Box> })
on('session.start', async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => <Button plain label={name} onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); const draw = ({ name }) => <Button plain label={name} onPress={() => focusMember($, name)} />; return <Box>{e.props.rows.map(draw)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.a.map((s) => e.props.b.map((t) => e.props.c.map(({ name }) => <Button plain label={name} onPress={() => focusMember($, name)} />)))}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.filter(({ name }) => <Button plain label={name} onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.flatMap(({ name }) => <Button plain label={name} onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.forEach(({ name }) => <Button plain label={name} onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); const name = 'x'; return <Box>{(() => <Button plain label={name} onPress={() => focusMember($, name)} />)()}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(function ({ name }) { return <Button plain label={name} onPress={() => focusMember($, name)} /> })}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => { return <Button plain label={name} onPress={() => focusMember($, name)} /> })}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows['map'](({ name }) => <Button plain label={name} onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(($) => <Button plain label={$} onPress={() => focusMember($, $)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ $x, name }) => <Button plain label={name} onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => <Button plain label={name} onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => drawRow(Button, name))}</Box> })
let B; on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); B = Button; return <Box /> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); const B = Button; return <Box>{e.props.rows.map(({ name }) => <B plain label={name} onPress={() => focusMember($, name)} />)}</Box> })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return <Box>{e.props.rows.map(({ name }) => <Button plain label={name} onPress={() => focusMember($, name)} />).concat(e.props.more.map(({ name }) => <Button plain label={name} onPress={() => focusMember($, name)} />))}</Box> })
EOF

# The same hook, in the allowed form, passes: nested maps (sections, then rows), a keyed Box around the Button, plain, a bare label identifier destructured by the
# innermost callback, and an onPress that is one helper call, awaited or wrapped in a try with an empty catch.
OUT=$(paneonly "on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async (\$, e, next) => { const { Box, Button, Text } = \$.ui.resolve(e); return (<Box>{e.props.sections.map((s) => <Box key={s.key}>{s.rows.map(({ name, text }) => <Box key={name}><Button plain label={name} hover={{ underline: true }} onPress={() => focusMember(\$, name)} /><Text>{text}</Text></Box>)}</Box>)}</Box>) })")
check "lexer passes the Pane Button form: nested .map callbacks, a keyed Box, plain, a destructured label, one focus call" '[ -z "$OUT" ]'
OUT=$(paneonly "on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async (\$, e, next) => { const { Box, Button } = \$.ui.resolve(e); return (<Box>{e.props.rows.map(({ name }) => <Box key={name}><Button plain label={name} onPress={async () => { try { await focusMember(\$, name) } catch {} }} /></Box>)}</Box>) })")
check "lexer passes a Pane Button whose onPress awaits the focus call inside a try with an empty catch" '[ -z "$OUT" ]'
OUT=$(paneonly "on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async (\$, e, next) => { const { Box, Button } = \$.ui.resolve(e); return (<Box>{e.props.rows.map(({ name }) => <Box key={name}><Button plain label={name} onPress={() => focusMember(\$, name)} /></Box>)}</Box>) })")
check "lexer passes a one-level .map Pane Button" '[ -z "$OUT" ]'
# A Pane tab: one press, one write of paneMode with the tab the map callback destructures, and nothing nearer than that passes.
tab_pane() { paneonly "on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async (\$, e, next) => { const { Box, Button } = \$.ui.resolve(e); return (<Box>{e.props.tabs.map(({ tab, label }) => <Box key={tab}><Button plain label={label} onPress={$1} /></Box>)}</Box>) })" "$PANE_OK"; }
OUT=$(tab_pane "() => \$.state.set({ plugin: 'wrangl', key: 'paneMode' }, tab)")
check "lexer passes a Pane tab Button whose onPress is one write of paneMode with its tab" '[ -z "$OUT" ]'
OUT=$(tab_pane "() => \$.state.set({ plugin: 'wrangl', key: 'closed' }, tab)")
check "lexer catches a Pane tab writing another state key" 'printf "%s" "$OUT" | grep -q "a Pane Button onPress"'
OUT=$(tab_pane "() => \$.state.set({ plugin: 'other', key: 'paneMode' }, tab)")
check "lexer catches a Pane tab writing another plugin's state" 'printf "%s" "$OUT" | grep -q "a Pane Button onPress"'
OUT=$(tab_pane "() => \$.state.set({ plugin: 'wrangl', key: 'paneMode' }, label)")
check "lexer catches a Pane tab writing anything but its tab" 'printf "%s" "$OUT" | grep -q "a Pane Button onPress"'
OUT=$(tab_pane "() => { \$.state.set({ plugin: 'wrangl', key: 'paneMode' }, tab); \$.ui.toast('x') }")
check "lexer catches a Pane tab that does more than the one write" 'printf "%s" "$OUT" | grep -q "a Pane Button onPress"'
# What stands before the one focus Button: finished literal .map( calls are not counted; any other callback is, and so is a third open one.
before_focus() { paneonly "on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async (\$, e, next) => { const { Box, Button } = \$.ui.resolve(e); return (<Box>$1{e.props.rows.map(({ name }) => <Box key={name}><Button plain label={name} onPress={() => focusMember(\$, name)} /></Box>)}</Box>) })"; }
OUT=$(before_focus "{e.props.a.map((x) => <Box key={x} />)}{e.props.b.map((x) => <Box key={x} />)}{e.props.c.map((x) => <Box key={x} />)}")
check "lexer passes three finished .map calls before a Pane Button" '[ -z "$OUT" ]'
OUT=$(before_focus "{foo(() => 1)}")
check "lexer catches a finished non-map callback before a Pane Button" 'printf "%s" "$OUT" | grep -q "not the first thing in a literal .map("'
OUT=$(paneonly "on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async (\$, e, next) => { const { Box, Button } = \$.ui.resolve(e); return (<Box>{e.props.a.map((x) => <Box key={x}>{x.b.map((y) => <Box key={y}>{y.c.map(({ name }) => <Box key={name}><Button plain label={name} onPress={() => focusMember(\$, name)} /></Box>)}</Box>)}</Box>)}</Box>) })")
check "lexer catches a Pane Button behind three open .map callbacks" 'printf "%s" "$OUT" | grep -q "more than two callbacks before the tag"'

# The focus helper is referenced exactly once outside its pinned text, as the Pane onPress callee: a process cannot run without a press (r8).
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(planted "$line")
  check "lexer alone catches a second reference to the focus helper: $line  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "referenced outside the one Pane onPress call\|more than one Pane"'
done <<'EOF'
on('session.start', async ($, e, next) => { await focusMember($, 'x'); return next(e) })
on('session.start', async ($, e, next) => { $.clock.every(1000, () => focusMember($, 'x')); return next(e) })
const openPane2 = async ($, member) => { await focusMember($, 'x') }
export const wrapper = ($, n) => focusMember($, n)
const f = focusMember
const g = { run: focusMember }
export { focusMember as run }
const callIt = (fn) => fn; callIt(focusMember)
await focusMember($, 'x')
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { await focusMember($, 'x'); return next(e) })
on('command.run', { command: 'wrangl-hierarchy-pane' }, async ($, e, next) => { await focusMember($, 'x'); return ({ text: 'ok' }) })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box>{e.props.rows.map(({ name }) => <Box key={name}><Button plain label={name} onPress={() => focusMember($, name)} /></Box>)}</Box>) })
const k = [focusMember]
const m = focusMember.bind(null, $)
EOF
OUT=$(paneonly "on('session.start', (\$, e, next) => next(e))")
check "lexer alone catches: a pinned helper that the Pane never calls  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "never called"'
OUT=$(paneonly "$PANE_OK" "// focusMember is the only helper; see focusMember above" "/* focusMember( */" "const note = 'focusMember'")
check "lexer passes the one Pane call plus a comment and a string that name the helper" '[ -z "$OUT" ]'
OUT=$(paneonly "$PANE_OK")
check "lexer passes the one Pane call alone" '[ -z "$OUT" ]'

# The prototype-reaching names (r8): each fails whole-word on the comment-blanked source, strings kept.
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(planted "$line")
  check "lexer alone catches: $line  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "banned token"'
done <<'EOF'
RegExp.prototype.test = () => true
/a/.__proto__.test = () => true
const x = {}; x['__proto__']
Object.getPrototypeOf(/a/).test = () => true
Object['getPrototypeOf'](x)
Object.setPrototypeOf(x, null)
Object.defineProperty(RegExp, 'a', {})
Object.defineProperties(x, {})
Object.getOwnPropertyDescriptor(x, 'a')
Object.getOwnPropertyDescriptors(x)
EOF

# The band hook may draw a Button with an inline arrow onPress; a $-first helper declared once may be called with $ from two hooks.
OUT=$(planted "const help = async (\$, x) => { await \$.state.set(x, 1) }" \
  "on('ui.render', { component: 'AbovePrompt' }, async (\$, e, next) => { const { Box, Button, Text } = \$.ui.resolve(e); return <Box><Text>x</Text><Button key=\"k\" label=\"L\" onPress={async () => { await help(\$, 1); await \$.ui.toast('x') }} /></Box> })" \
  "on('command.run', { command: 'wrangl-hierarchy-pane' }, async (\$, e, next) => { await help(\$, 2); return ({ text: 'ok' }) })")
check "lexer passes a band Button with an inline arrow onPress and a \$-first helper called from two hooks" '[ -z "$OUT" ]'

# The band button may list this plugin's panes and close its own.
OUT=$(planted "on('ui.render', { component: 'AbovePrompt' }, async (\$, e, next) => { const { Box, Button } = \$.ui.resolve(e); return <Box><Button key=\"k\" label=\"L\" onPress={async () => { if ((await \$.ui.panes()).length) await \$.ui.close({ id: 'ah-status' }) }} /></Box> })")
check "lexer passes \$.ui.panes and \$.ui.close in the band Button's onPress" '[ -z "$OUT" ]'

# The position rule still passes a band Button with children, in a conditional return, after an early return.
OUT=$(planted "on('ui.render', { component: 'AbovePrompt' }, async (\$, e, next) => { if (e.props.hasSurvey) return next(e); const { Box, Button } = \$.ui.resolve(e); return (<Box>{e.props.ok ? (<Button onPress={() => \$.ui.toast('x')}>Go</Button>) : null}</Box>) })" \
  "on('ui.render', { component: 'AbovePrompt' }, async (\$, e, next) => { const { Button } = \$.ui.resolve(e); return <Button onPress={() => \$.ui.toast('x')}>Go</Button> })")
check "lexer passes a band Button with children, in a conditional return, after an early return" '[ -z "$OUT" ]'

# The mod has no UserMessage render hook, so the matcher list refuses one outright.
OUT=$(planted "on('ui.render', { component: 'UserMessage' }, async (\$, e, next) => next(e))")
check "lexer rejects a UserMessage render hook as an unlisted matcher [${OUT%%$'\n'*}]" '[ -n "$OUT" ] && [[ "$OUT" == *"matcher is not one of"* ]]'

# The import rule closes a helper kept outside the scan: mod/types/ is not scanned, so a value import from
# it is the violation.
copy_mod
printf '%s\n' "export const extra = (r: any) => r('tool.check', (\$: any, e: any, next: any) => next(e))" > "$SANDBOX/mod/types/extra.ts"
printf '%s\n' "import { extra } from './types/extra.ts'" >> "$SANDBOX/mod/register.tsx"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: a helper in mod/types/extra.ts imported as a value  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'

# The \u cases must hold the escape itself; a decoded character would plant a different case.
OUT=$(grep -c '^\\u0024\.session\|^\\u006fn(\|^on(.\\u0074ool' "$0")
check "the three \\u planted cases hold a literal backslash-u in this file" '[ "$OUT" = 3 ]'

OUT=$(planted 'const r = ($, e, next) => $.fs.read(p)' '$.ui.status(t)' 'const t = `${x}`' \
  "export const register: Register = (on, options) => { on('session.start', (\$, e, next) => next(e)) }" \
  "on('command.run', { command: 'wrangl-hierarchy-pane' }, (\$, e, next) => ({ text: 'ok' }))" \
  "on('ui.render', { component: 'Pane', requestId: 'ah-status' }, (\$, e, next) => <Box><Text>x</Text></Box>)" \
  '$.clock.every(2000, () => tick())' \
  'const strip = /[\x00-\x1f\x7f-\x9f]/g')
check "lexer passes the allowed forms: \$.fs.read(p), \$.ui.status(t), (\$, e, next) =>, \`\${x}\`, (on, options), the matched command.run {text}, a Box/Text Pane, a parameterless every callback, \\x escapes" '[ -z "$OUT" ]'

# A module with every P3 matcher passes the lexer and the validate net; one without its matcher fails the net.
p3() {
  rm -rf "${SANDBOX:?}/p3"; mkdir -p "$SANDBOX/p3"; cp -R "$PLUGIN/.claude-plugin" "$SANDBOX/p3/"; copy_mod; cp -R "$SANDBOX/mod" "$SANDBOX/p3/mod"
  # plugin.json names hooks/hooks.json, the plugin's one module (the bridge); here the module is the scratch register alone
  echo '{ "modules": ["./register.tsx"] }' > "$SANDBOX/p3/mod/hooks.json"
  node -e 'const f=process.argv[1], fs=require("fs"), j=JSON.parse(fs.readFileSync(f,"utf8")); j.hooks="./mod/hooks.json"; fs.writeFileSync(f, JSON.stringify(j, null, 2))' "$SANDBOX/p3/.claude-plugin/plugin.json"
  # the mail helpers pass $ to registered(), and the validate net follows $ only into a function the module holds; this case is about matchers, so a stand-in does
  local registered_src="const registered = async (\$: any, ctx: any, id: string): Promise<boolean> => (await \$.clock.now()) < 0"
  { echo "import type { Register } from 'claude-code'"; echo "export const register: Register = (on, options) => {"; printf '  %s\n' "$@" "$DISMISS_OK" "$VIEW_OK"; printf '%s\n' "$NUDGE_REG" "$RING_REG" "  on('turn.complete', (\$, e, next) => { mailPoll(\$, mailRun); return next(e) })"; echo "}"; printf '%s\n' "$RING_HELPER" "$NUDGE_HOOK" "$HELPER" "$HELPER2" "$HELPER3" "$HELPER4" "$MAIL_OK" "$SHOW_OK" "$NOTE_OK" "$VIEW_SUPPORT" "$registered_src"; } > "$SANDBOX/p3/mod/register.tsx"
}
p3 "on('session.start', (\$, e, next) => next(e))" \
   "on('command.run', { command: 'wrangl-hierarchy-pane' }, (\$, e, next) => ({ text: 'ok' }))" \
   "on('ui.render', { component: 'Pane', requestId: 'ah-status' }, (\$, e, next) => next(e))" \
   "on('ui.render', { component: 'AbovePrompt' }, (\$, e, next) => next(e))" \
   "on('ui.close', { id: 'ah-status' }, (\$, e, next) => next(e))" \
   "$PANE_OK"
OUT="$(violations "$SANDBOX/p3/mod")$(valnet "$SANDBOX/p3")"
check "a register with the ui.close matcher, both ui.render matchers and the command.run matcher passes the lexer and the validate net" '[ -z "$OUT" ]'
p3 "on('session.start', (\$, e, next) => next(e))" "on('ui.render', (\$, e, next) => next(e))"
OUT=$(valnet "$SANDBOX/p3")
check "the validate net rejects ui.render printed with no matcher" 'printf "%s" "$OUT" | grep -q "needs one of its matchers"'

copy_mod; mkdir -p "$SANDBOX/mod/tests"
printf '%s\n' '$.fs.write(p, t)' "on('fs.write', (env, e, next) => env.prompt.fill())" > "$SANDBOX/mod/tests/x.test.ts"
OUT=$(violations "$SANDBOX/mod")
check "lexer ignores anything under tests/" '[ -z "$OUT" ]'
printf '%s\n' 'export const pure = (s: string) => `${s}`' 'const x = $' > "$SANDBOX/mod/view.ts"
OUT=$(violations "$SANDBOX/mod")
check "view.ts may hold no \$ at all" '[ -n "$OUT" ] && printf "%s" "$OUT" | grep -q "^view.ts:"'

# The mail poll's `handed` records change only at the claim mark, the resolve path, the restore path (reject / pre-submit
# throw) and the release of an id an answer lists as positively acked (the poll's answer and the re-check's answer). Any other
# write would let something observed release a record.
handed_writes() { grep -oE 'ctx\.handed\.(set|delete)\([^)]*\)?' "$1" | sort | uniq -c | sed -E 's/^ +//' | sort; }
OUT=$(handed_writes "$PLUGIN/mod/register.tsx")
WANT=$(printf '%s\n' "1 ctx.handed.delete(m.id)" "2 ctx.handed.delete(x)" "1 ctx.handed.set(m.id, m.prev)" "1 ctx.handed.set(m.id, { state: 'resolved', at })" "1 ctx.handed.set(x, rec)" | sort)
check "register.tsx writes handed only at the mark, the resolve and the restore" '[ "$OUT" = "$WANT" ]'
cp "$PLUGIN/mod/register.tsx" "$SANDBOX/register.tsx"
printf '%s\n' "for (const k of []) ctx.handed.delete(k)" >> "$SANDBOX/register.tsx"
OUT=$(handed_writes "$SANDBOX/register.tsx")
check "negative: a handed.delete anywhere else changes the list" '[ "$OUT" != "$WANT" ]'

# ---- The dismiss helper (the second pinned exec site). The same forms that fail for the focus helper fail for it.
# <line>...: the lexer's verdict on a scratch module holding the Pane focus call, those lines, and both pinned helpers (no Dismiss Button unless a line draws one).
dismissonly() {
  copy_mod
  { echo "import type { Register } from 'claude-code'"; echo "export const register: Register = (on, options) => {"; printf '  %s\n' "$PANE_OK" "$VIEW_OK" "$@"; printf '%s\n' "$NUDGE_REG" "$RING_REG"; echo "}"; printf '%s\n' "$RING_HELPER" "$NUDGE_HOOK" "$HELPER" "$HELPER2" "$HELPER3" "$HELPER4" "$MAIL_OK" "$VIEW_SUPPORT"; } > "$SANDBOX/mod/register.tsx"
  violations "$SANDBOX/mod"
}
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(planted "$line")
  check "lexer alone catches: $line  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
done <<'EOF'
await $.ui.ask('x', ['a', 'b'])
$.ui.ask('x')
const r = $.plugin.root
const { root } = $.plugin
$.plugin
const q = $['plugin']
EOF

# The dismiss helper is referenced exactly once outside its pinned text, as the callee of the band's Dismiss Button onPress.
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(planted "$line")
  check "lexer alone catches a second reference to the dismiss helper: $line  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "referenced outside the one band Dismiss onPress call\|more than one band"'
done <<'EOF'
await dismissTeam($)
on('session.start', async ($, e, next) => { await dismissTeam($); return next(e) })
on('command.run', { command: 'wrangl-hierarchy-pane' }, async ($, e, next) => { await dismissTeam($); return ({ text: 'ok' }) })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { await dismissTeam($); return next(e) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { await dismissTeam($); return next(e) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="other" label="x" onPress={() => dismissTeam($)} /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-dismiss" label="Dismiss" onPress={() => dismissTeam($)} /></Box>) })
$.clock.every(1000, () => dismissTeam($))
const f = dismissTeam
const g = { run: dismissTeam }
export { dismissTeam as run }
const callIt = (fn) => fn; callIt(dismissTeam)
const k = [dismissTeam]
const m = dismissTeam.bind(null, $)
EOF
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(planted "$line")
  check "lexer alone catches a rebinding of the dismiss helper: $line  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
done <<'EOF'
const dismissTeam = ($) => 0
function dismissTeam(x) { return x }
let dismissTeam = 1
dismissTeam = null
EOF

# Each Dismiss Button that is not exactly the one allowed form fails the lexer on its own.
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(dismissonly "$line")
  check "lexer alone catches: $line  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
done <<'EOF'
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-dismiss" label="Dismiss" onPress={() => { dismissTeam($); other() }} /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-dismiss" label="Dismiss" onPress={() => dismissTeam($, 1)} /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-dismiss" label="Dismiss" onPress={() => $.ui.toast('x')} /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-dismiss" label="Dismiss" onPress={() => x.dismissTeam($)} /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-dismiss" plain label="Dismiss" onPress={() => dismissTeam($)} /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-dismiss" label="Dismiss" /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-dismiss" label="Dismiss" onPress={dismissTeam} /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-dismiss" label="Dismiss" onPress={() => dismissTeam(e)} /></Box>) })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-dismiss" label="Dismiss" onPress={() => dismissTeam($)} /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box>{[1, 2].map((n) => <Button key="ah-dismiss" label="Dismiss" onPress={() => dismissTeam($)} />)}</Box>) })
EOF
OUT=$(dismissonly "$DISMISS_OK")
check "lexer passes the one Dismiss Button call" '[ -z "$OUT" ]'
OUT=$(dismissonly "on('ui.render', { component: 'AbovePrompt' }, async (\$, e, next) => { const { Box, Button } = \$.ui.resolve(e); return (<Box><Button key=\"ah-dismiss\" label=\"Dismiss\" onPress={async () => { try { await dismissTeam(\$) } catch {} }} /></Box>) })")
check "lexer passes a Dismiss Button whose onPress awaits the helper inside a try with an empty catch" '[ -z "$OUT" ]'
OUT=$(dismissonly "on('ui.render', { component: 'AbovePrompt' }, async (\$, e, next) => { const { Box, Button } = \$.ui.resolve(e); return (<Box><Button key=\"ah-pane\" label=\"Pane\" onPress={async () => { try { await \$.ui.panes() } catch {} }} />{e.props.ok ? (<Box><Button key=\"ah-dismiss\" label=\"Dismiss\" onPress={() => dismissTeam(\$)} /></Box>) : null}</Box>) })")
check "lexer passes a Dismiss Button after the Pane Button, inside a conditional" '[ -z "$OUT" ]'
OUT=$(dismissonly)
check "lexer alone catches: a pinned dismiss helper that the band never calls  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "never called by the band"'
OUT=$(paneonly "$PANE_OK")
check "the standard scratch module (Pane call plus Dismiss call) passes" '[ -z "$OUT" ]'

# <from> <to> [line...]: the lexer's verdict on a scratch copy whose pinned dismiss helper has <from> replaced by <to>.
altered2() {
  copy_mod
  node -e 'const fs = require("fs"), p = process.argv[1], [from, to] = [process.argv[2], process.argv[3]]; const t = fs.readFileSync(p, "utf8"); const i = t.indexOf("export const dismissTeam"); if (i < 0 || !t.slice(i).includes(from)) { console.error("no such text in the helper"); process.exit(2); } fs.writeFileSync(p, t.slice(0, i) + t.slice(i).replace(from, () => to));' "$SANDBOX/mod/register.tsx" "$1" "$2" || { echo "scratch edit failed"; return; }
  shift 2; [ $# -gt 0 ] && printf '%s\n' "$@" >> "$SANDBOX/mod/register.tsx"
  violations "$SANDBOX/mod"
}
altcase2() { # <label> <from> <to> [line...]
  local label=$1; shift
  OUT=$(altered2 "$@")
  check "lexer alone catches a changed dismiss helper: $label  [${OUT%%$'\n'*}]" '[ -n "$OUT" ] && [ "$OUT" != "scratch edit failed" ]'
}
altcase2 "plan flag changed" "'--plan'" "'--list'"
altcase2 "close flag changed" "'--close'" "'--list'"
altcase2 "confirm removed" "'--confirm', " ""
altcase2 "token from elsewhere" "read.plan.token" "'0123456789abcdef'"
altcase2 "team taken from the display name" "team.isDefault ? '@default' : team.name" "label"
altcase2 "executable changed" "root + '/bin/ah'" "root + '/bin/sh'"
altcase2 "executable from elsewhere" "root + '/bin/ah'" "'/tmp/x'"
altcase2 "subcommand changed" "'roster', 'disband'" "'msg', 'disband'"
altcase2 "plugin root removed" "\$.plugin.root" "'/opt/ah'"
altcase2 "name shape widened" "{0,31}\$/.test(team.name)" "{0,310}\$/.test(team.name)"
altcase2 "plan timeout changed" "15000" "600000"
altcase2 "close timeout changed" "30000" "600000"
altcase2 "confirm answer loosened" "if (answer !== yes) return" "if (answer === 'Cancel') return"
altcase2 "second dialog removed" "if (inFlight(team.summary)) {" "if (false) {"
altcase2 "second dialog answer loosened" "if (sure !== 'Dismiss anyway') return" "if (sure === 'Cancel') return"
altcase2 "Cancel not first" "options: ['Cancel', yes]" "options: [yes, 'Cancel']"
altcase2 "picker accepts Cancel" "if (chosen === 'Cancel' || !labels.includes(chosen)) return" "if (!labels.includes(chosen)) return"
altcase2 "re-entrancy flag dropped" "if ((await \$.state.get(flag)).value === true) return" ""
altcase2 "token shape re-check dropped" "if (!/^[0-9a-f]{16}\$/.test(read.plan.token)) {" "if (false) {"
altcase2 "stale view" "ownedTeams((await \$.state.get({ plugin: 'wrangl', key: 'view' })).value)" "[]"
altcase2 "a third process" "await \$.ui.toast(closeToast(label, run.exitCode, run.stdout, run.stderr))" "await \$.process.run(['rm', '-rf', cwd])"
altcase2 "a shell string" "[root + '/bin/ah', 'roster', 'disband']" "['sh', '-c', root]"
OUT=$(planted "$HELPER2")
check "lexer alone catches: the dismiss helper text present twice  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "dismiss helper appears more than once"'
OUT=$(altered2 "'--plan'" "'--list'" "/* $HELPER2 */")
check "lexer alone catches: an altered live dismiss helper with the pinned text in a comment  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "dismiss helper text is missing or altered"'
copy_mod; sed -i.bak "s#^export const dismissTeam#const dismissTeam#" "$SANDBOX/mod/register.tsx"; rm -f "$SANDBOX/mod/register.tsx.bak"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: the dismiss helper without its export (a changed text)  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
copy_mod; printf '%s\n' "$HELPER2" > "$SANDBOX/mod/other.ts"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: the pinned dismiss text in a file other than register.tsx  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "dismiss helper outside register.tsx"'
OUT=$(violations "$PLUGIN/mod")
check "the repo's own register.tsx holds the dismiss helper exactly once" '[ -z "$OUT" ] && [ "$(grep -c "export const dismissTeam" "$PLUGIN/mod/register.tsx")" = 1 ]'

# ---- The mail helper (the third pinned exec site). The same forms that fail for the other two fail for it.
# The mail helper is referenced exactly once outside its pinned text: the timer callback in mailPoll, never inside a hook.
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(planted "$line")
  check "lexer alone catches a second reference to the mail helper: $line  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "mail helper is referenced outside\|more than one timer"'
done <<'EOF'
await mailCycle($, ctx)
on('session.start', async ($, e, next) => { await mailCycle($, ctx); return next(e) })
on('command.run', { command: 'wrangl-hierarchy-pane' }, async ($, e, next) => { await mailCycle($, ctx); return ({ text: 'ok' }) })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { await mailCycle($, ctx); return next(e) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="x" label="x" onPress={() => mailCycle($, ctx)} /></Box>) })
on('session.start', async ($, e, next) => { ctx.st.timer = $.clock.every(POLL_MS, () => mailCycle($, ctx)); return next(e) })
ctx.st.timer = $.clock.every(POLL_MS, () => mailCycle($, ctx))
$.clock.every(1000, () => mailCycle($, ctx))
const f = mailCycle
const g = { run: mailCycle }
export { mailCycle as run }
const callIt = (fn) => fn; callIt(mailCycle)
const k = [mailCycle]
const m = mailCycle.bind(null, $)
EOF
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(planted "$line")
  check "lexer alone catches a rebinding of the mail helper: $line  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
done <<'EOF'
const mailCycle = ($) => 0
function mailCycle(x) { return x }
let mailCycle = 1
mailCycle = null
EOF
# A mail exec, or its daemon call, written anywhere but the pinned text fails on its own.
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(planted "$line")
  check "lexer alone catches: $line  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
done <<'EOF'
const run = await $.process.run(['node', root + '/hooks/msg.mjs', 'due'], { timeoutMs: 10000 })
const run = await $.process.run([root + '/bin/ah', 'msg', 'due', '--json'], { timeoutMs: 10000, stdin: text })
await $.http.fetch('http://ahd/v1/due?in=x&wait=25', { method: 'GET', headers: {}, socketPath: env.sock })
await $.http.fetch('http://ahd/v1/due?in=x&wait=25', { method: 'POST', headers: {}, socketPath: link.socket })
await ahdGet($, env.sock, 'mod', '/v1/x')
await ahdGet($, mail.sock, 'mod', '/v1/x')
import { cases } from '../hooks/lib-mail-core.mjs'
import { x } from '../hooks/lib-anything.mjs'
await $.fs.write(p, t)
EOF
# <from> <to> [line...]: the lexer's verdict on a scratch copy whose pinned mail helper has <from> replaced by <to>.
altered3() {
  copy_mod
  node -e 'const fs = require("fs"), p = process.argv[1], [from, to] = [process.argv[2], process.argv[3]]; const t = fs.readFileSync(p, "utf8"); const i = t.indexOf("export const mailCycle"); if (i < 0 || !t.slice(i).includes(from)) { console.error("no such text in the helper"); process.exit(2); } fs.writeFileSync(p, t.slice(0, i) + t.slice(i).replace(from, () => to));' "$SANDBOX/mod/register.tsx" "$1" "$2" || { echo "scratch edit failed"; return; }
  shift 2; [ $# -gt 0 ] && printf '%s\n' "$@" >> "$SANDBOX/mod/register.tsx"
  violations "$SANDBOX/mod"
}
altcase3() { # <label> <from> <to> [line...]
  local label=$1; shift
  OUT=$(altered3 "$@")
  check "lexer alone catches a changed mail helper: $label  [${OUT%%$'\n'*}]" '[ -n "$OUT" ] && [ "$OUT" != "scratch edit failed" ]'
}
altcase3 "executable changed to node" "root + '/bin/ah'" "'node'"
altcase3 "executable under hooks" "root + '/bin/ah', 'msg', 'due', '--json'" "'node', root + '/hooks/msg.mjs', 'due'"
altcase3 "executable from elsewhere" "root + '/bin/ah'" "'/tmp/x'"
altcase3 "subcommand changed" "'msg', 'due'" "'msg', 'send'"
altcase3 "json flag removed" "'due', '--json'" "'due'"
altcase3 "a shell string" "[root + '/bin/ah', 'msg', 'due', '--json']" "['sh', '-c', text]"
altcase3 "a cwd option" "{ timeoutMs: 10000, stdin: text }" "{ timeoutMs: 10000, stdin: text, cwd: '/' }"
altcase3 "an env option" "{ timeoutMs: 10000, stdin: text }" "{ timeoutMs: 10000, stdin: text, env: { AH_HOME: '/x' } }"
altcase3 "stdin from elsewhere" "stdin: text" "stdin: id"
altcase3 "timeout changed" "timeoutMs: 10000" "timeoutMs: 600000"
altcase3 "a process spawn" "\$.process.run(" "\$.process.spawn("
altcase3 "the daemon call a POST" "method: 'GET'" "method: 'POST'"
altcase3 "the socket from the environment" "socketPath: ctx.link.socket" "socketPath: ctx.env.home + '/run/ahd.sock'"
altcase3 "the route changed" "'http://ahd/v1/due?in='" "'http://ahd/v1/inbox?in='"
altcase3 "the request-line budget dropped" " && encodeURIComponent(text).length <= PUSH_QUERY_MAX" ""
altcase3 "the registration gate dropped" "(await registered(\$, ctx, id))" "(await \$.clock.now())"
altcase3 "asUser added to the prompt" "\$.prompt.submit({ text: a.compact ?? a.prompt })" "\$.prompt.submit({ text: a.compact ?? a.prompt, asUser: true })"
altcase3 "a prompt other than the answer's" "\$.prompt.submit({ text: a.compact ?? a.prompt })" "\$.prompt.submit({ text: text })"
altcase3 "a release from a discarded answer" "if (gen !== st.generation) return" "for (const x of a.acked) ctx.handed.delete(x); if (gen !== st.generation) return"
altcase3 "a release not driven by the answer" "for (const x of a.acked) ctx.handed.delete(x)" "for (const [x] of ctx.handed) ctx.handed.delete(x)"
altcase3 "a mark before the check" "if (gen !== st.generation) return" "ctx.handed.set('x', { state: 'resolved', at: 0 }); if (gen !== st.generation) return"
altcase3 "the generation check dropped" "if (gen !== st.generation) return" ""
altcase3 "the busy check dropped" "if (ctx.busy.at !== busyAt && !a.ring) {" "if (false) {"
altcase3 "the timer kept while connected" "st.timer.cancel()" "0"
altcase3 "a failure that stays connected" "if (st.push && gen === st.generation) {" "if (false) {"
altcase3 "the backoff dropped" "st.notBefore = (await \$.clock.now()) + BACKOFF_MS[Math.min(st.failures, BACKOFF_MS.length - 1)]" "st.notBefore = 0"
altcase3 "a socket taken from the answer while connected" "ctx.link.socket = a.socket" "ctx.link.socket = a.socket; st.push = true"
OUT=$(planted "$HELPER3")
check "lexer alone catches: the mail helper text present twice  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "mail helper appears more than once"'
OUT=$(altered3 "'--json'" "'--plain'" "/* $HELPER3 */")
check "lexer alone catches: an altered live mail helper with the pinned text in a comment  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "mail helper text is missing or altered"'
copy_mod; sed -i.bak "s#^export const mailCycle#const mailCycle#" "$SANDBOX/mod/register.tsx"; rm -f "$SANDBOX/mod/register.tsx.bak"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: the mail helper without its export (a changed text)  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
copy_mod; printf '%s\n' "$HELPER3" > "$SANDBOX/mod/other.ts"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: the pinned mail text in a file other than register.tsx  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "mail helper outside register.tsx"'
copy_mod
{ echo "import type { Register } from 'claude-code'"; echo "export const register: Register = (on, options) => {"; printf '  %s\n' "$PANE_OK" "$DISMISS_OK" "$VIEW_OK"; printf '%s\n' "$NUDGE_REG" "$RING_REG"; echo "}"; printf '%s\n' "$RING_HELPER" "$NUDGE_HOOK" "$HELPER" "$HELPER2" "$HELPER3" "$HELPER4" "$VIEW_SUPPORT"; } > "$SANDBOX/mod/register.tsx"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: a pinned mail helper that nothing calls  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "never called by the timer"'
OUT=$(paneonly "$PANE_OK")
check "the standard scratch module (Pane call, Dismiss call, the mail timer) passes" '[ -z "$OUT" ]'
OUT=$(violations "$PLUGIN/mod")
check "the repo's own register.tsx holds the mail helper exactly once and calls it from one timer" '[ -z "$OUT" ] && [ "$(grep -c "export const mailCycle" "$PLUGIN/mod/register.tsx")" = 1 ] && [ "$(grep -c "\$.clock.every(POLL_MS, () => mailCycle(\$, ctx))" "$PLUGIN/mod/register.tsx")" = 2 ]'
# The release is the one handed.delete that is not the restore, and it iterates the answer's acked ids and nothing else; the one write
# of the socket the push uses stores what a CLI answer reported.
check "register.tsx releases handed only for the answer's acked ids" '[ "$(grep -c "for (const x of a.acked) ctx.handed.delete(x)" "$PLUGIN/mod/register.tsx")" = 1 ]'
check "register.tsx stores the push socket from the CLI answer alone" '[ "$(grep -c "link.socket = " "$PLUGIN/mod/register.tsx")" = 1 ] && grep -q "ctx.link.socket = a.socket" "$PLUGIN/mod/register.tsx"'

# ---- The view helper (the fourth pinned exec site). The same forms that fail for the other three fail for it.
# <line>...: the lexer's verdict on a scratch module holding the standard hooks, those lines, and the pinned helpers (View Button call included).
viewonly() {
  copy_mod
  { echo "import type { Register } from 'claude-code'"; echo "export const register: Register = (on, options) => {"; printf '  %s\n' "$PANE_OK" "$DISMISS_OK" "$@"; printf '%s\n' "$NUDGE_REG" "$RING_REG"; echo "}"; printf '%s\n' "$RING_HELPER" "$NUDGE_HOOK" "$HELPER" "$HELPER2" "$HELPER3" "$HELPER4" "$MAIL_OK" "$VIEW_SUPPORT"; } > "$SANDBOX/mod/register.tsx"
  violations "$SANDBOX/mod"
}
OUT=$(viewonly "$VIEW_OK")
check "lexer passes the one View Button call" '[ -z "$OUT" ]'
OUT=$(viewonly)
check "lexer alone catches: a pinned view helper that the band never calls  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "view helper is never called by the band"'
# Each View Button that is not exactly the pinned handler fails the lexer on its own.
while IFS= read -r line; do
  [ -n "$line" ] || continue
  OUT=$(viewonly "$line")
  check "lexer alone catches: $line  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
done <<'EOF'
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-view" label="View" onPress={() => viewOpen($, { ahHome: '/x' })} /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-view" label="View" /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-view" label="View" onPress={viewOpen} /></Box>) })
on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-view" plain label="View" onPress={() => $.ui.toast('x')} /></Box>) })
on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e, next) => { const { Box, Button } = $.ui.resolve(e); return (<Box><Button key="ah-view" label="View" onPress={() => viewOpen($, { ahHome: '/x' })} /></Box>) })
viewOpen($, { ahHome: '/x' })
on('session.start', async ($, e, next) => { await viewOpen($, { ahHome: '/x' }); return next(e) })
on('command.run', { command: 'wrangl-hierarchy-pane' }, async ($, e, next) => { await viewOpen($, { ahHome: '/x' }); return ({ text: 'ok' }) })
const timer = ($) => $.clock.every(5000, () => viewOpen($, { ahHome: '/x' }))
const run = ($) => $.process.run(['sh', '-c', 'x'])
const viewOpen = ($) => 0
function viewOpen(x) { return x }
let viewOpen = 1
viewOpen = null
export { viewOpen as run }
const k = [viewOpen]
EOF
# <from> <to> [line...]: the lexer's verdict on a scratch copy whose pinned view helper has <from> replaced by <to>.
altered4() {
  copy_mod
  node -e 'const fs = require("fs"), p = process.argv[1], [from, to] = [process.argv[2], process.argv[3]]; const t = fs.readFileSync(p, "utf8"); const i = t.indexOf("export const viewOpen"); if (i < 0 || !t.slice(i).includes(from)) { console.error("no such text in the helper"); process.exit(2); } fs.writeFileSync(p, t.slice(0, i) + t.slice(i).replace(from, () => to));' "$SANDBOX/mod/register.tsx" "$1" "$2" || { echo "scratch edit failed"; return; }
  shift 2; [ $# -gt 0 ] && printf '%s\n' "$@" >> "$SANDBOX/mod/register.tsx"
  violations "$SANDBOX/mod"
}
altcase4() { # <label> <from> <to> [line...]
  local label=$1; shift
  OUT=$(altered4 "$@")
  check "lexer alone catches a changed view helper: $label  [${OUT%%$'\n'*}]" '[ -n "$OUT" ] && [ "$OUT" != "scratch edit failed" ]'
}
altcase4 "a second process.run" "if (choice === 'Web') {" "await \$.process.run(['rm', '-rf', '/']); if (choice === 'Web') {"
altcase4 "an env option on Web" "timeoutMs: 15000 }" "timeoutMs: 15000, env: { AH_HOME: '/x' } }"
altcase4 "a cwd option on Web" "timeoutMs: 15000 }" "timeoutMs: 15000, cwd: '/' }"
altcase4 "an env option on the TUI" "timeoutMs: 10000 }" "timeoutMs: 10000, env: { AH_HOME: '/x' } }"
altcase4 "the token in argv" "'--kind', 'web']" "'--kind', 'web', token]"
altcase4 "the token in argv as a flag" "'--kind', 'web']" "'--kind', 'web', '--token', token]"
altcase4 "the token not on stdin" "{ stdin: JSON.stringify({ token }) + '\\n', timeoutMs: 15000 }" "{ timeoutMs: 15000 }"
altcase4 "stdin from elsewhere" "stdin: JSON.stringify({ token })" "stdin: ctx.ahHome"
altcase4 "executable changed" "root + '/bin/ah'" "'node'"
altcase4 "executable from elsewhere" "root + '/bin/ah'" "'/tmp/x'"
altcase4 "subcommand changed" "'view', 'open', '--kind', 'web'" "'msg', 'send', '--kind', 'web'"
altcase4 "a shell string" "[ah, 'view', 'open', '--kind', 'web']" "['sh', '-c', token]"
altcase4 "the token shape check dropped" "if (!/^[0-9a-f]{64}\$/.test(token)) {" "if (false) {"
altcase4 "the token shape check loosened" "/^[0-9a-f]{64}\$/" "/^[0-9a-f]+\$/"
altcase4 "the token read from another file" "'/run/band.token'" "'/run/ahd.json'"
altcase4 "the token read from elsewhere" "ctx.ahHome + '/run/band.token'" "'/tmp/band.token'"
altcase4 "a write instead of a read" "\$.fs.read(" "\$.fs.write("
altcase4 "web timeout changed" "timeoutMs: 15000" "timeoutMs: 600000"
altcase4 "tui timeout changed" "timeoutMs: 10000" "timeoutMs: 600000"
altcase4 "the plugin root dropped" "\$.plugin.root" "'/opt/ah'"
altcase4 "a choice other than the three" "(choice !== 'TUI' && choice !== 'Web')" "(choice === 'Cancel')"
altcase4 "Pane no longer returned to the caller" "if (choice === 'Pane') return 'pane'" "if (choice === 'Pane') return"
altcase4 "a process spawn" "\$.process.run(" "\$.process.spawn("
OUT=$(planted "$HELPER4")
check "lexer alone catches: the view helper text present twice  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "view helper appears more than once"'
OUT=$(altered4 "'--kind', 'web'" "'--kind', 'x'" "/* $HELPER4 */")
check "lexer alone catches: an altered live view helper with the pinned text in a comment  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "view helper text is missing or altered"'
copy_mod; sed -i.bak "s#^export const viewOpen#const viewOpen#" "$SANDBOX/mod/register.tsx"; rm -f "$SANDBOX/mod/register.tsx.bak"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: the view helper without its export (a changed text)  [${OUT%%$'\n'*}]" '[ -n "$OUT" ]'
copy_mod; printf '%s\n' "$HELPER4" > "$SANDBOX/mod/other.ts"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: the pinned view text in a file other than register.tsx  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "view helper outside register.tsx"'
OUT=$(violations "$PLUGIN/mod")
check "the repo's own register.tsx holds the view helper exactly once and calls it from one View onPress" '[ -z "$OUT" ] && [ "$(grep -c "export const viewOpen" "$PLUGIN/mod/register.tsx")" = 1 ] && [ "$(grep -c "viewOpen(\$, ctx)" "$PLUGIN/mod/register.tsx")" = 1 ]'

# The nudge relabel: planted cases in a scratch copy, each caught by the lexer alone.
# <from> <to>: the lexer's verdict on a scratch copy whose nudge relabel has <from> replaced by <to>.
alterednudge() {
  copy_mod
  node -e 'const fs = require("fs"), p = process.argv[1], [from, to] = [process.argv[2], process.argv[3]]; const t = fs.readFileSync(p, "utf8"); const i = t.indexOf("export const nudgeRelabel"); if (i < 0 || !t.slice(i).includes(from)) { console.error("no such text in the relabel"); process.exit(2); } fs.writeFileSync(p, t.slice(0, i) + t.slice(i).replace(from, () => to));' "$SANDBOX/mod/register.tsx" "$1" "$2" || { echo "scratch edit failed"; return; }
  violations "$SANDBOX/mod"
}
nudgecase() { # <label> <from> <to>
  local label=$1; shift
  OUT=$(alterednudge "$@")
  check "lexer alone catches a changed nudge relabel: $label  [${OUT%%$'\n'*}]" '[ -n "$OUT" ] && [ "$OUT" != "scratch edit failed" ]'
}
nudgecase "a fabricated result" "{ stdout: stripped," "{ stdout: 'ok',"
nudgecase "a result for every tool" "e.tool === 'Bash'" "true"
nudgecase "a result marked successful text" "stderr: ''" "stderr: 'done'"
nudgecase "next called twice" "const ran = await next(e)" "const ran = await next(e); await next(e)"
nudgecase "a deny without the marker check" "if (head === null || !ran.text.startsWith(marker, head[0].length)) return ran" "if (false) return ran"
nudgecase "the classic prefix made optional" "if (head === null || !ran.text.startsWith(marker, head[0].length)) return ran" "if (!ran.text.includes(marker)) return ran"
nudgecase "a deny of anything" ": { deny: stripped }" ": { deny: 'no' }"
nudgecase "a call on \$" "const ran = await next(e)" "const ran = await next(e); await _\$.process.run(['rm'])"
# The handler's name is held: a second binding of it, or any mention of it outside the two pinned texts, is caught.
nudgename() { # <label> <line inserted before the pinned registration text, or '' to append at the end> <line appended>
  copy_mod
  node -e 'const fs = require("fs"), p = process.argv[1], [ins, app] = [process.argv[2], process.argv[3]]; let t = fs.readFileSync(p, "utf8"); const k = "  const nudgeTools = ["; if (!t.includes(k)) { console.error("no registration"); process.exit(2); } if (ins) t = t.replace(k, () => ins + "\n" + k); if (app) t += app + "\n"; fs.writeFileSync(p, t);' "$SANDBOX/mod/register.tsx" "$1" "$2" || { echo "scratch edit failed"; return; }
  violations "$SANDBOX/mod"
}
nudgenamecase() { # <label> <inserted> <appended>
  local label=$1; shift
  OUT=$(nudgename "$@")
  check "lexer alone catches the nudge relabel's name used again: $label  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "nudge relabel"'
}
nudgenamecase "a block const bound before the registration loop" "  const nudgeRelabel = async (_\$: any, e: any, next: any) => next(e)" ""
nudgenamecase "a function declaration in register" "  function nudgeRelabel(_\$: any, e: any, next: any) { return next(e) }" ""
nudgenamecase "the name handed to another registration" "" "on('prompt.submit', nudgeRelabel)"
nudgenamecase "the name assigned a new body" "  let nudgeRelabel2 = 0" "nudgeRelabel = async (_\$: any, e: any, next: any) => next(e)"
copy_mod; printf '%s\n' "on('tool.call', { tool: 'Read' }, async (\$, e, next) => next(e))" >> "$SANDBOX/mod/register.tsx"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: a second tool.call registration  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "tool.call"'
copy_mod; printf '%s\n' "$NUDGE_HOOK" > "$SANDBOX/mod/other.ts"
OUT=$(violations "$SANDBOX/mod")
check "lexer alone catches: the pinned nudge text in a file other than register.tsx  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "nudge helper outside register.tsx"'
check "mod/register.tsx names no registrations/ path: a session's team files come from the daemon's row" \
  '! grep -qF "registrations/" "$PLUGIN/mod/register.tsx"'
check "the marker literal is in mod/ exactly once, in the pinned relabel, and is the one the C# gates emit" \
  '[ "$(grep -rF "[ah-nudge] " "$PLUGIN/mod" --include=*.ts --include=*.tsx --exclude-dir=tests --exclude-dir=types | wc -l | tr -d " ")" = 1 ] && grep -qF "NudgeMarker = \"[ah-nudge] \";" "$PLUGIN/native/src/Ah.Hooks/Plumbing/HookEmit.cs"'

# The hook bridge, hooks/register.ts (the plugin's one module, which calls mod/register.tsx's register first): its one process
# call runs exactly the binary the classic hooks ran, `sh <plugin root>/bin/ah-hook <name>`, the name a row of hooks/registry.json
# in the one command shape. bridge to bin/ah-hook: 2026-11-11, atp-1592. No other process call in hooks/.
BRIDGE_RUN="const ran = await \$.process.run(['sh', \`\${root}/bin/ah-hook\`, row.hook], {"
BRIDGE_SHAPE='const COMMAND = /^sh "\$\{CLAUDE_PLUGIN_ROOT\}\/bin\/ah-hook" ([a-z0-9-]+)$/'
bridge_ok() { # <hooks dir>: silent when the bridge holds its one pinned process call and its one command shape
  local d=$1 n
  n=$(grep -rn "process" "$d" --include=*.ts --include=*.tsx --exclude-dir=tests | wc -l | tr -d ' ')
  [ "$n" = 1 ] || echo "hooks/ names process $n times, not once"
  [ "$(grep -cF -- "$BRIDGE_RUN" "$d/register.ts")" = 1 ] || echo "the pinned ah-hook run is not in hooks/register.ts exactly once"
  [ "$(grep -cF -- "$BRIDGE_SHAPE" "$d/register.ts")" = 1 ] || echo "the pinned command shape is not in hooks/register.ts exactly once"
  grep -q "bridge to bin/ah-hook: 2026-11-11, atp-1592" "$d/register.ts" || echo "the ruling comment is missing"
  node -e 'const d=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")); const bad=[]; for (const gs of Object.values(d.hooks)) for (const g of gs) for (const h of g.hooks) if (!/^sh "\$\{CLAUDE_PLUGIN_ROOT\}\/bin\/ah-hook" [a-z0-9-]+$/.test(h.command)) bad.push(h.command); if (bad.length) console.log("registry rows off the command shape: "+bad.join(", "))' "$d/registry.json"
}
OUT=$(bridge_ok "$PLUGIN/hooks")
check "the hook bridge runs only sh <root>/bin/ah-hook <registry row>  [${OUT%%$'\n'*}]" '[ -z "$OUT" ]'
mkdir -p "$SANDBOX/hooks-planted" && cp "$PLUGIN/hooks/register.ts" "$PLUGIN/hooks/registry.json" "$SANDBOX/hooks-planted/"
printf '%s\n' "const x = (\$: any) => \$.process.run(['curl', 'x'])" >> "$SANDBOX/hooks-planted/register.ts"
OUT=$(bridge_ok "$SANDBOX/hooks-planted")
check "negative: a second process call in hooks/ is caught  [${OUT%%$'\n'*}]" 'printf "%s" "$OUT" | grep -q "process 2 times"'

echo "---"
echo "$PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
