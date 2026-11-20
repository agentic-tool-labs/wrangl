import type { Register } from 'claude-code'
import { anywayQuestion, bandButtons, bandLine, closeToast, closedTeam, dirIdOf, withoutTeams, DISMISS_BUTTON, dismissQuestion, failReason, heldText, HOOKS_OFF_TEXT, inFlight, keepSeen, mailRecord, mailRecords, mailRefusedToast, mailSection, mailToast, mailUnsavedToast, mainCheckoutRoot, NO_OWNED_TEAM, noViewLine, nullCause, ownedTeamNames, ownedTeams, PANE_BUTTON, paneSections, paneTab, parseDoc, readDue, readPlan, REBUILD_EVERY_MS, REBUILD_WATCH_MS, rebuildGaveUp, rebuildOf, rebuildStep, rebuildToast, registeredStatusFiles, scope, sitrepSections, SIZE_CAP, statusText, STYLE, teamLabels, toastMs, VIEW_BUTTON, type DueAnswer, type Doc, viewModel } from './view.ts'

// A tone to Text props: bad and warn take their status's theme key, the same key their glyph takes, and bad is also bold
// so it does not rest on color alone; work and ok draw plain and any other tone draws dim.
const toneProps = (tone: string) =>
  tone === 'bad' ? { color: STYLE.tone.bad, bold: true } : tone === 'warn' ? { color: STYLE.tone.warn } : tone === 'work' || tone === 'ok' ? {} : { dimColor: true }

// A theme key colors; no key draws dim. Every key comes from view.ts's style table.
const colorProps = (key: string | null) => (key === null ? { dimColor: true } : { color: key })

// The one process the mod may run, on a person's press: focus a member's terminal pane. The guard (tests/test-mod-readonly.sh)
// holds this text whole and allows `process` nowhere else in mod/.
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

// The other process the mod may run, on a person's press of Dismiss: close one of the viewer's own teams through the roster
// CLI's plan and close calls. A dialog names what will close and which work is in flight, and a second one confirms losing it,
// before anything closes. The guard (tests/test-mod-readonly.sh) holds this text whole; it is called from the band's Dismiss
// button alone. The team name is checked against the shape the roster CLI gives team names, and the plan token against the shape
// its close plan produces; the command is this plugin's own bin/ah.
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

// The third process the mod may run, on a person's press of the band's View button: ask which view, then hand Web or the TUI to
// this plugin's own bin/ah. Pane and Sitrep are answered to the caller, which opens the Pane as the Pane button does. Web sends the one-shot token in
// the file the daemon keeps (read here on the press, checked for its shape, and sent on standard input alone, never in an argument or
// the environment). One process runs per call at most, the two calls sit on branches that exclude each other, and a run that fails
// is shown as one toast. The guard (tests/test-mod-readonly.sh) holds this text whole and allows `process` nowhere else but the other
// pinned helpers; it is called once, by the band's View button.
export type ViewCtx = { ahHome: string }
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

// The one way the Pane opens on the person's request, for the command and the band's button. A member session
// sees no hierarchy view; its Pane holds the Mail section alone.
const openPane = async ($: any, member: boolean, mode: 'pane' | 'sitrep' = 'sitrep') => {
  await $.state.set({ plugin: 'wrangl', key: 'closed' }, false)
  await $.state.set({ plugin: 'wrangl', key: 'paneMode' }, mode)
  const opened = await $.ui.open({ id: 'ah-status', title: 'Hierarchy' })
  const what = member ? 'mail' : 'hierarchy'
  return opened.isPlaced
    ? { text: `Opened the ${what} Pane.`, placed: true }
    : { text: `The ${what} Pane is open, but this session shows no panes.`, placed: false }
}

// What a press of the band's Pane or View button does with the Pane itself.
const paneShown = async ($: any): Promise<boolean> =>
  (await $.ui.panes()).some((pane: { id: string; isPlaced: boolean }) => pane.id === 'ah-status' && pane.isPlaced)

const paneClose = async ($: any): Promise<void> => {
  await $.state.set({ plugin: 'wrangl', key: 'closed' }, true)
  await $.ui.close({ id: 'ah-status' })
}

const paneOpen = async ($: any, member: boolean, toastFor: number, mode: 'pane' | 'sitrep' = 'sitrep'): Promise<void> => {
  const r = await openPane($, member, mode)
  if (!r.placed && !member && toastFor > 0) await $.ui.toast(r.text, { timeoutMs: toastFor })
}

// The directory the View button's Web choice reads its token under, derived as the mail poll derives it; null when it cannot be.
const viewCtx = async ($: any): Promise<ViewCtx | null> => {
  const env = await mailEnv($)
  return env === null ? null : { ahHome: env.home }
}


// The mail adapter. The mod asks the native CLI what mail is due and hands the answer to the model: through the daemon socket a CLI
// answer reported when the daemon is up (one GET it holds open until something is due, re-sent at once), else by running this plugin's
// own bin/ah on a timer. The guard (tests/test-mod-readonly.sh) holds this text whole and allows `process` nowhere else but the two
// helpers above; it is referenced once, by the timer in mailPoll, and by itself.
const POLL_MS = 5000
// Raise with DueRequest.CurrentModBuild when a daemon needs mods to change: a session keeps the mod it started with, and the daemon logs one sending less.
const MOD_BUILD = 1
// Under the engine's 30 s abort.
const SITREP_WAIT_S = 25
// How often the band reads the owned teams' summaries itself, when the Sitrep's poll is not reading them.
const SUMMARY_EVERY_MS = 30_000
const DUE_WAIT_S = 25
const PUSH_QUERY_MAX = 7168
const BACKOFF_MS = [5000, 10000, 20000, 40000, 60000]
const NO_DOC_RETRY_MS = 5 * 60_000
// How long a status answer with no file behind it is taken as current before the daemon is asked again: between one and two of the
// band's 2 s ticks, so every other tick asks. J2 (b459): was every tick; the band may show a status up to 4 s older.
const STATUS_ASK_MS = 3000

type MailBusy = { at: number | null }
type MailEnv = { home: string; version: string }
type MailHanded = { state: 'pending' | 'resolved'; at: number }
// `row` is this session's row from the daemon's last answer, kept for the status tick's team files; `regToken` is that answer's token.
type MailLink = { socket: string | null; row?: unknown; rows?: number; regToken?: number }
// cliFailures and cliSkipUntil back the CLI request off while it keeps failing (a refused or erroring `ah msg due`): ticks before cliSkipUntil send nothing.
type MailState = { state: string | null; generation: number; push: boolean; running: boolean; failures: number; notBefore: number; timer: { cancel: () => void } | null; cliFailures?: number; cliSkipUntil?: number; toolAt?: number; held?: boolean; heldAt?: number; unread?: number; regAt?: number; registered?: boolean; regInflight?: AhdGate }
// Whether this session has a registration, by the daemon's answer at most every REGISTERED_ASK_MS. With the daemon down it keeps its
// last answer. Before any answer there is no socket to ask (only a CLI answer reports it), so the session's mark decides.
const REGISTERED_ASK_MS = 5000
const registered = async ($: any, ctx: MailCtx, id: string): Promise<boolean> => {
  const st = ctx.st
  const link = ctx.link
  const now = await $.clock.now()
  if (link.socket !== null && now - (st.regAt ?? -REGISTERED_ASK_MS) >= REGISTERED_ASK_MS) {
    st.regAt = now
    try {
      // An unchanged answer means no registration moved since regToken, so the rows already held stand.
      const answer = await ahdGet($, link.socket, 'mod', '/v1/inspect/registrations?since=' + (link.regToken ?? -1), AHD_TIMEOUT_MS, st.regInflight ??= { open: false })
      const rows = answer?.registrations
      if (Array.isArray(rows)) {
        link.row = rows.find((r: any) => r?.session_id === id)
        link.rows = rows.length
        link.regToken = typeof answer.token === 'number' ? answer.token : undefined
        st.registered = link.row !== undefined
      }
    } catch {
      // the daemon is down: a yes stands, and a no gives way to the mark, or the CLI call that would revive the daemon stays gated on it
      if (st.registered === false) st.registered = undefined
    }
  }
  if (st.registered !== undefined) return st.registered
  try {
    await $.fs.stat(ctx.env.home + '/session-marks/' + id)
    return true
  } catch {
    return false
  }
}
// What the mail poll leaves for the person: how long its toasts stay (0 shows none) and why the Mail section cannot be trusted
// (a failed write of its record), null while it can.
type MailNotes = { toastMs: number; lost: string | null }
type MailCtx = { env: MailEnv; busy: MailBusy; hier: () => string | null; link: MailLink; handed: Map<string, MailHanded>; st: MailState; notes: MailNotes }

// The toast for a mail submit, then the record of it for the Mail section. The toast is not waited for, so a toast that never
// settles cannot hold the record back and a state write that never settles cannot hold the toast back; a record that cannot be
// written marks the section unavailable and says so in a second toast. Nothing here throws, so a mail is never held back by it.
const noteMail = async ($: any, ctx: MailCtx, a: DueAnswer, state: 'delivered' | 'not delivered'): Promise<void> => {
  const prompt = a.prompt ?? ''
  const say = (text: string) => {
    if (ctx.notes.toastMs <= 0) return
    try {
      Promise.resolve($.ui.toast(text, { timeoutMs: ctx.notes.toastMs })).catch(() => {})
    } catch {}
  }
  say(state === 'delivered' ? mailToast(prompt, a.ids.length) : mailRefusedToast(prompt))
  try {
    const at = await $.clock.now()
    const held = await $.state.get({ plugin: 'wrangl', key: 'mail' })
    await $.state.set({ plugin: 'wrangl', key: 'mail' }, mailRecords(held.value, mailRecord(at, a, state)))
    ctx.notes.lost = null
  } catch {
    ctx.notes.lost = 'state write failed'
    say(mailUnsavedToast(prompt))
  }
}

// An escalation the daemon could not hand to a lead (this session has none above it) is shown to the person once: the daemon
// raises each escalation once, so nothing here repeats it.
const showEscalation = ($: any, ctx: MailCtx, a: DueAnswer): void => {
  if (a.escalation === undefined || ctx.notes.toastMs <= 0) return
  try {
    Promise.resolve($.ui.toast(a.escalation.text, { timeoutMs: ctx.notes.toastMs })).catch(() => {})
  } catch {}
}

// A held session's unread count, kept for the status tick's line (ah2-180 B8).
export const showHeld = (_$: any, ctx: MailCtx, a: DueAnswer): void => {
  ctx.st.unread = ctx.st.held === true ? a.unread : undefined
}

const TOOL_ASK_MS = 3000

// The engine's refusal after the person's Stop, as engine 2.1.316 words it: it holds every plugin prompt until a prompt enters, so the
// daemon is told to stop ringing. Any other drop is a failed submit, which is rung again and in time goes to the delivery ladder.
const STOP_DROP = /^the session was stopped/

// While held, a submit every limit re-tests the Stop: a drop costs nothing, and no turn has run since the mail went unread.
const HELD_PROBE_MS = 600_000
const HELD_PROBE_TEXT = 'ah: mail waiting, run ah msg next'

// Between the tool calls of a busy turn: one doorbell question at most every TOOL_ASK_MS, answered only by a ring of NEW mail.
// Returns the ring line to attach to a tool result, or null. It marks nothing handed (the line is context, not a submitted
// prompt) and never throws, so a failing daemon or CLI leaves the tool call as it was.
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

// One request of the mail poll and what follows from its answer. CLI state: the timer runs this, one bin/ah call each, and an answer
// that names a live daemon (and a backoff that has passed) moves to push. Push state: the timer is cancelled, one GET is open at a
// time on the socket the last CLI answer reported, and each answer re-arms the next request at once; a request whose encoded input
// is over the daemon's request-line budget goes through the CLI and stays in push. Any failure in push goes back to CLI with the
// generation bumped. The claim rule for `handed` (owner-only): a record is created ONLY at the mark below, in the same step as the
// check of the answer; it then changes ONLY through the submit that owns it (resolve, or restore on a reject or a throw before it)
// or by release, when the answer lists the id as positively acked. Nothing observed as absent releases a record. Right before the
// submit the cycle asks again without waiting (the first answer may be seconds old: a long poll waits, and the entries it names can
// be acked by the time it returns), so a prompt names only entries still due; an unreadable second answer keeps the first. A second copy that slips through reaches the
// prompt hook, which passes it with a note that it repeats mail already delivered. A doorbell ring is not asked for again: the first
// answer claimed it, so a second answer could only say nothing is due and the ring would be lost. For the same reason a ring is handed
// over even if a turn began during the ask: it waits for that turn, whose own busy mark stays.
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
    showHeld($, ctx, a)
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
      if (ctx.link.socket !== a.socket) ctx.link.regToken = undefined // another daemon's counter says nothing about this one's rows
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

// The daemon call the status view makes: GET over a socket, no long-poll. The engine aborts a fetch with no complete answer after
// 30 s, so callers keep one in flight at a time. Callers pass `link.socket`, the socket the mail poll's last CLI answer reported.
// A 404 whose body names the code `not_found` throws this, so a caller can tell "the daemon has no such document" from a failure.
class AhdNotFound extends Error {}

const notFoundBody = (text: unknown): boolean => {
  try { return JSON.parse(String(text))?.error?.code === 'not_found' } catch { return false }
}

// How long a read that does not long-poll waits for the daemon; past it the read fails as an unreachable daemon does.
const AHD_TIMEOUT_MS = 2000

// Set while a read of one kind is open, until its fetch settles, which may be after that read's time limit.
type AhdGate = { open: boolean }

// `timeoutMs` null: no limit of its own, for a long poll whose `wait` the daemon answers within.
// `gate`: one read of that kind at a time; a read that finds one open fails at once, as an unreachable daemon does, so a hung
// daemon is not handed a new socket every tick.
const ahdGet = async ($: any, sock: string, version: string, path: string, timeoutMs: number | null = AHD_TIMEOUT_MS, gate?: AhdGate): Promise<any> => {
  if (gate?.open) throw new Error('an earlier ahd read is still open')
  if (gate) gate.open = true
  let fetched: Promise<any>
  try {
    fetched = Promise.resolve($.http.fetch('http://ahd' + path, { method: 'GET', headers: { 'Ah-Client-Version': version }, socketPath: sock }))
  } catch (err) {
    if (gate) gate.open = false
    throw err
  }
  // A read that lost the race may still fail later; that failure is not this read's.
  fetched.catch(() => {}).finally(() => { if (gate) gate.open = false })
  // The time limit is a timer the read cancels once it settles either way.
  let limit: { cancel: () => void } | null = null
  const late = timeoutMs === null ? null : new Promise<null>((res) => { limit = $.clock.every(timeoutMs, () => res(null)) })
  let r: any
  try {
    r = late === null ? await fetched : await Promise.race([fetched, late])
  } finally {
    limit?.cancel()
  }
  if (r === null) throw new Error(`ahd did not answer in ${timeoutMs} ms`)
  if (!r.ok) throw r.status === 404 && notFoundBody(r.text) ? new AhdNotFound('ahd has no document') : new Error('ahd answered ' + r.status)
  return JSON.parse(r.text)
}

// AH_HOME, else HOME + /.claude/ah2; null when neither is readable.
// The client version header carries a fixed name: the daemon does not read it, so nothing needs the plugin's own version.
const mailEnv = async ($: any): Promise<MailEnv | null> => {
  try {
    const ah = await $.env.get('AH_HOME')
    const hm = await $.env.get('HOME')
    const home = typeof ah === 'string' && ah.trim() !== '' ? ah.trim() : typeof hm === 'string' && hm !== '' ? hm + '/.claude/ah2' : ''
    if (home === '') return null
    return { home, version: 'mod' }
  } catch {
    return null
  }
}

// Starts the mail poll: the timer runs mailCycle every POLL_MS until a CLI answer moves it to push. Off in -p and SDK sessions.
// A session with no registration does nothing (no heartbeat), so senders fall back to SendMessage.
const mailPoll = ($: any, env: MailEnv, busy: MailBusy, hier: () => string | null, link: MailLink, notes: MailNotes): MailCtx => {
  const ctx: MailCtx = { env, busy, hier, link, handed: new Map(), st: { state: null, generation: 0, push: false, running: false, failures: 0, notBefore: 0, timer: null }, notes }
  ctx.st.timer = $.clock.every(POLL_MS, () => mailCycle($, ctx))
  return ctx
}

// The background rebuild of ahd (ah2-111): the stamp <AH_HOME>/install.json is read after session start's hooks (one of them
// may start the build), on reload (session.start again) and on each prompt; while a build runs it is re-read every
// REBUILD_EVERY_MS until it ends or REBUILD_WATCH_MS passes. One read at a time, so a toast cannot show twice. Reads only; never throws.
type RebuildCtx = { busy: boolean; watch: { cancel: () => void } | null; toastMs: number }
const rebuildCheck = async ($: any, ctx: RebuildCtx, until: number | null = null): Promise<void> => {
  if (ctx.busy) return
  ctx.busy = true
  try {
    const held = (await $.state.get({ plugin: 'wrangl', key: 'rebuild' })).value ?? null
    if (until !== null && (await $.clock.now()) >= until) {
      ctx.watch?.cancel()
      ctx.watch = null
      const gaveUp = rebuildGaveUp(held)
      if (gaveUp !== held) await $.state.set({ plugin: 'wrangl', key: 'rebuild' }, gaveUp)
      return
    }
    const env = await mailEnv($)
    if (env === null) return
    // The read has the mail poll's bound (AHD_TIMEOUT_MS, as `registered` reads): one that never answers is abandoned as unreadable,
    // so the busy mark clears and a later check runs.
    let now = null
    let limit: { cancel: () => void } | null = null
    const late = new Promise<null>((res) => { limit = $.clock.every(AHD_TIMEOUT_MS, () => res(null)) })
    try {
      const text = await Promise.race([Promise.resolve($.fs.read(env.home + '/install.json')), late])
      if (text !== null) now = rebuildOf(text)
    } catch {
    } finally {
      limit?.cancel()
    }
    const step = rebuildStep(held, now)
    // Kept before toasting, so a toast that fails is not shown again.
    if (step.keep !== held) await $.state.set({ plugin: 'wrangl', key: 'rebuild' }, step.keep)
    if (step.notice !== null && ctx.toastMs > 0) void Promise.resolve($.ui.toast(rebuildToast(step.notice, env.home), { timeoutMs: ctx.toastMs })).catch(() => {})
    if (step.watch && ctx.watch === null) {
      const end = (await $.clock.now()) + REBUILD_WATCH_MS
      ctx.watch = $.clock.every(REBUILD_EVERY_MS, () => rebuildCheck($, ctx, end))
    } else if (!step.watch && ctx.watch !== null) {
      ctx.watch.cancel()
      ctx.watch = null
    }
  } catch {
  } finally {
    ctx.busy = false
  }
}

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

export const register: Register = (on, options) => {
  // Only an explicit true turns the entry on; a missing or any other value counts as off.
  const statusEntry = options?.status_entry === true || options?.status_entry === 'true'
  // The band is on unless the person set the option to false.
  const bandOn = !(options?.band === false || options?.band === 'false')
  // How long the status toasts and the band notice stay, from one read of the option; 0 makes no toast at all.
  const toastFor = toastMs(options?.toast_seconds)
  const mailNotes: MailNotes = { toastMs: toastFor, lost: null }
  // Whether the last doc read lists this session, set by every tick. It decides what the Pane holds (Mail alone for a member),
  // whether the tick fills the Sitrep, and the band's buttons. A reload runs session.start again, whose first tick sets it
  // before any command can arrive.
  let member = false
  // Busy from any prompt entering to the turn completing; the mail poll hands over a repeat only while idle.
  const busy: MailBusy = { at: null }

  const rebuild: RebuildCtx = { busy: false, watch: null, toastMs: toastFor }

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
  on('prompt.submit', async ($, e, next) => {
    try { busy.at = await $.clock.now() } catch {
    }
    void rebuildCheck($, rebuild)
    if (mailRun !== null) mailRun.st.held = false
    return next(e)
  })

  // A subagent's end fires this too, with its agentId, while the main turn runs (probed on engine 2.1.316).
  on('turn.complete', async ($, e, next) => {
    if (e.agentId === undefined) {
      busy.at = null
      if (mailRun !== null) mailRun.st.held = false
    }
    return next(e)
  })

  // The mail poll of this session, set by session.start; a tool call during a busy turn can carry the doorbell's ring as context.
  // The hook below never denies and never alters the tool's result: it only adds model-only context after the call, and any
  // failure passes the result through untouched.
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

  on('session.start', async ($, e, next) => {
    const mail = await mailEnv($)
    // The socket of the daemon the mail poll's last CLI answer reported; null until one has.
    const link: MailLink = { socket: null }
    let cwd: string | undefined
    let places: { pool: string; main: string | null } | null = null
    // Each status file is held with its mtime and parse, and re-read only when the file changes. `lost` says why
    // `doc` is null: `no file` or `unreadable`.
    // `none` is set when the daemon answered that it has no document for the dir: the socket asked, the file's mtime then (null
    // when absent) and when. The daemon serves a dir by whether its file exists, not by when it changed, so while the socket and the
    // file's presence are as they were, and NO_DOC_RETRY_MS has not passed, the daemon is not asked again.
    type Held = { seenMtime: number | undefined; askedMtime: number | null | undefined; rev: number | undefined; doc: Doc | null; lost: string; none: { socket: string; mtime: number | null; at: number } | null; askedAt?: number }
    const poolHeld: Held = { seenMtime: undefined, askedMtime: undefined, rev: undefined, doc: null, lost: 'no file', none: null }
    const mainHeld: Held = { seenMtime: undefined, askedMtime: undefined, rev: undefined, doc: null, lost: 'no file', none: null }
    // One per status file the session's registration names outside its own checkout.
    const registeredHeld = new Map<string, Held>()
    let doc: Doc | null = null
    let lost = 'no file'
    // null until the first tick, so a fresh environment always sets the entry, clearing any left by the last one.
    let shown: string | undefined | null = null

    // The status files of the git checkout holding `dir`: the first ancestor with a `.git` entry, the way
    // ah's writer finds it, and, when that is a linked worktree, the main checkout's too. ponytail:
    // WRANGL_HIERARCHY_DIR, ah's non-git fallback dir and the project dir of a repository that is the user's home
    // (~/.claude/ah2/project) are not followed.
    const locate = async (dir: string): Promise<{ pool: string; main: string | null } | null> => {
      const sep = dir.includes('/') || !dir.includes('\\') ? '/' : '\\'
      const join = (a: string, b: string) => (a.endsWith(sep) ? a + b : a + sep + b)
      const statusOf = (root: string) => join(join(join(root, '.claude'), 'ah2'), 'status.json')
      for (let d = dir; ; ) {
        if (await $.fs.exists(join(d, '.git'))) {
          const root = await mainCheckoutRoot(d, {
            kind: async (p) => { try { return (await $.fs.stat(p)).kind } catch { return null } },
            read: (p) => $.fs.read(p),
          })
          return { pool: statusOf(d), main: root === null ? null : statusOf(root) }
        }
        const cut = d.lastIndexOf(sep)
        const parent = cut > 0 ? d.slice(0, cut) : cut === 0 && d.length > 1 ? sep : d
        if (parent === d) return null
        d = parent
      }
    }

    // Brings `h` up to date from the daemon's status document, or returns false when it is unreachable and the file is read instead.
    // `h.rev` is the revision last parsed, so an unchanged one is not parsed again.
    // While one status fetch is pending, ticks read the file: a hung daemon must not freeze the band or stack fetches.
    let statusInflight = false
    // Open from the status fetch until it settles, past its time limit too: ticks read the file until then.
    const statusFetch: AhdGate = { open: false }
    // The first tick reads the file only; session.start does not wait for it, and the daemon is asked from the first
    // scheduled tick on, so a first tick that never ends does not leave the session on files.
    let daemonReady = false
    const fromDaemon = async (path: string, h: Held): Promise<boolean> => {
      if (link.socket === null || statusInflight || statusFetch.open || !daemonReady) return false
      const socket = link.socket
      statusInflight = true
      let mtime: number | null = null
      let asked = 0
      try {
        try { mtime = (await $.fs.stat(path)).mtimeMs ?? null } catch { mtime = null }
        asked = await $.clock.now()
        // The daemon's document is the file's: while the file's mtime is the one last asked about, the answer held is current.
        if (mtime !== null && h.rev !== undefined && h.doc !== null && h.askedMtime === mtime && h.none === null) return true
        // No file to watch (the status lives in the database): the answer held is current for STATUS_ASK_MS.
        if (mtime === null && h.askedMtime === null && h.rev !== undefined && h.doc !== null && h.none === null && h.askedAt !== undefined && asked - h.askedAt >= 0 && asked - h.askedAt < STATUS_ASK_MS) return true
        if (h.none !== null) {
          if (h.none.socket === socket && (h.none.mtime === null) === (mtime === null) && asked - h.none.at < NO_DOC_RETRY_MS) return false
          h.none = null
        }
        const sep = path.includes('/') || !path.includes('\\') ? '/' : '\\'
        const r = await ahdGet($, link.socket, 'mod', '/v1/status?dir=' + encodeURIComponent(path.slice(0, path.lastIndexOf(sep))) + '&since=' + (h.rev ?? 0) + '&wait=0', AHD_TIMEOUT_MS, statusFetch)
        if (!r || typeof r.rev !== 'number') return false
        h.askedMtime = mtime
        h.askedAt = asked
        if (r.doc === null || r.doc === undefined) { h.doc = null; h.lost = 'no file'; h.seenMtime = undefined; h.rev = r.rev }
        else if (r.rev !== h.rev || h.doc === null) { h.doc = parseDoc(JSON.stringify(r.doc)); h.lost = 'unreadable'; h.seenMtime = undefined; h.rev = r.rev }
        return true
      } catch (err) {
        if (err instanceof AhdNotFound) h.none = { socket, mtime, at: asked }
        return false
      } finally {
        statusInflight = false
      }
    }

    // Brings `h` up to date with `file`: stat, then read only when the mtime moved.
    const load = async (file: string, h: Held) => {
      h.rev = undefined
      try {
        let st: any = null
        try { st = await $.fs.stat(file) } catch { h.doc = null; h.seenMtime = undefined; h.lost = 'no file' }
        if (st !== null) {
          // ponytail: stat then read by path; a live local process could swap the path between the two calls. A committed file cannot race, and read's own cap bounds a swapped file. Close it if $.fs gains a handle or no-follow read.
          if (st.isLink || st.kind !== 'file' || st.size === 0 || st.size > SIZE_CAP) { h.doc = null; h.seenMtime = undefined; h.lost = 'unreadable' }
          else if (st.mtimeMs !== h.seenMtime) { h.seenMtime = st.mtimeMs; h.doc = parseDoc(await $.fs.read(file)); h.lost = 'unreadable' }
        }
      } catch {
        h.doc = null
        h.seenMtime = undefined
        h.lost = 'unreadable'
      }
    }

    // The daemon's document when it answers, else the file.
    const read = async (file: string, h: Held) => {
      if (!(await fromDaemon(file, h))) await load(file, h)
    }

    // The Sitrep: the daemon's `sitrep:` slice of each team the viewer owns that its `team-summary:` slice does not call closed, read as the web UI reads it, one long poll at a time
    // while the Pane shows it (the poll holds the slice's lease; a held slice is re-read only on a change). The engine aborts a fetch at 30 s.
    let sitrepInflight = false
    let sitrepSub = ''
    let sitrepSeq = 0
    let sitrepEpoch = ''
    let sitrepBodies: Record<string, unknown> = {}
    // The latest `team-summary:` bodies, by slice id, from the Sitrep's poll or the band's own read: one map both read.
    const summaries: Record<string, unknown> = {}
    // When the Sitrep's poll last answered.
    let sitrepAt = -Infinity
    const sitrepRead = async (dir: string | null, names: string[], looked: string) => {
      if (sitrepInflight) return
      sitrepInflight = true
      const put = (value: unknown) => $.state.set({ plugin: 'wrangl', key: 'sitrep' }, value)
      try {
        if (dir === null || names.length === 0 || link.socket === null) {
          sitrepSub = ''
          await put({ why: dir === null || names.length === 0 ? 'no team' : 'down', detail: looked, answered: false, teams: [] })
          return
        }
        const id = await dirIdOf(dir)
        const slice = (name: string) => `sitrep:${id}/${name}`
        const asked = (name: string) => `questions:${id}/${name}`
        const summary = (name: string) => `team-summary:${id}/${name}`
        const shown = (answered: boolean) => {
          const open = names.filter((name) => !closedTeam(sitrepBodies[summary(name)]))
          return open.length === 0
            ? { why: 'no team', detail: looked, answered, teams: [] }
            : { why: null, answered, teams: open.map((name) => ({ name, body: sitrepBodies[slice(name)] ?? null, questions: sitrepBodies[asked(name)] ?? null })) }
        }
        const sub = names.flatMap((name) => [slice(name), asked(name), summary(name)]).join(',')
        if (sub !== sitrepSub) {
          sitrepSub = sub
          sitrepSeq = 0
          sitrepEpoch = ''
          sitrepBodies = {}
          await put(shown(false))
        }
        const r = await ahdGet($, link.socket, 'mod', '/v1/view?path=/events&sub=' + encodeURIComponent(sub) + '&since=' + sitrepSeq + (sitrepSeq === 0 ? '&fresh=*' : '') + (sitrepEpoch === '' ? '' : '&epoch=' + encodeURIComponent(sitrepEpoch)) + '&wait=' + SITREP_WAIT_S, null)
        // A restarted daemon answers resync with no slices: the next read starts over.
        sitrepSeq = r.resync === true || typeof r.seq !== 'number' ? 0 : r.seq
        sitrepEpoch = r.resync === true || typeof r.epoch !== 'string' ? '' : r.epoch
        for (const c of Array.isArray(r.slices) ? r.slices : []) if (typeof c?.id === 'string') {
          sitrepBodies[c.id] = c.body
          if (c.id.startsWith('team-summary:')) summaries[c.id] = c.body
        }
        sitrepAt = await $.clock.now()
        // Written on every answer, so the ages redraw at least once a long poll.
        await put(shown(true))
      } catch {
        sitrepSub = ''
        await put({ why: 'down', detail: looked, answered: false, teams: [] }).catch(() => {})
      } finally {
        sitrepInflight = false
      }
    }

    // The owned teams the daemon calls closed, from their `team-summary:` slices in `summaries`, so the band, Dismiss and the
    // hierarchy tab leave them out as the Sitrep does. The Sitrep's long poll fills `summaries` while it runs; with it not
    // covering these teams, one wait=0 read every SUMMARY_EVERY_MS (or at once for a new set of teams) does. A failed read keeps the last verdicts.
    let summaryAskedAt = -Infinity
    let summaryAskedSub = ''
    const closedOf = async (dir: string, names: string[], now: number): Promise<string[]> => {
      if (names.length === 0 || link.socket === null) return []
      const did = await dirIdOf(dir)
      const ids = names.map((name) => `team-summary:${did}/${name}`)
      const sub = ids.join(',')
      const polled = sitrepSub.split(',')
      const covered = now - sitrepAt < SUMMARY_EVERY_MS && ids.every((x) => polled.includes(x))
      if (!covered && (sub !== summaryAskedSub || now - summaryAskedAt >= SUMMARY_EVERY_MS)) {
        summaryAskedAt = now
        summaryAskedSub = sub
        try {
          const r = await ahdGet($, link.socket, 'mod', '/v1/view?path=/events&sub=' + encodeURIComponent(sub) + '&since=0&fresh=*&wait=0')
          for (const c of Array.isArray(r.slices) ? r.slices : []) if (typeof c?.id === 'string') summaries[c.id] = c.body
        } catch {
          // A failed read keeps the last verdicts; summaryAskedAt holds the next try off for SUMMARY_EVERY_MS.
        }
      }
      return names.filter((_, i) => closedTeam(summaries[ids[i]]))
    }

    // Whether `d` is readable, current, and lists the viewer as a member or an owner.
    const knows = (d: Doc | null, now: number, id: string) => d !== null && ![NO_OWNED_TEAM, 'unreadable', 'expired'].includes(nullCause(d, now, id))

    const tick = async () => {
      // The id is read every tick: /clear keeps this environment but starts a new session.
      const now = await $.clock.now()
      const id = await $.session.id()
      // Each status file read this tick, in the order asked.
      const tried: { file: string; h: Held }[] = []
      try {
        const where = await $.session.cwd()
        if (where !== cwd) {
          cwd = where
          places = await locate(where)
          for (const h of [poolHeld, mainHeld]) { h.seenMtime = undefined; h.askedMtime = undefined; h.askedAt = undefined; h.rev = undefined; h.doc = null; h.none = null }
        }
        if (places === null) { poolHeld.doc = null; poolHeld.lost = 'no file' }
        else {
          // The document of the pool the session works in comes first: its exchanges and dispatches are the ones
          // this session made. Only where it does not know the viewer is the main checkout's looked up.
          await read(places.pool, poolHeld)
          tried.push({ file: places.pool, h: poolHeld })
          if (!knows(poolHeld.doc, now, id) && places.main !== null) {
            await read(places.main, mainHeld)
            tried.push({ file: places.main, h: mainHeld })
          }
        }
        // A team can live in another repository than the session's cwd: where the checkout does not know the viewer,
        // the session's row in the daemon's last answer names its team files, as it does for its mail.
        if (!tried.some((t) => knows(t.h.doc, now, id))) {
          for (const file of registeredStatusFiles(JSON.stringify(link.row))) {
            if (tried.some((t) => t.file === file)) continue
            const h = registeredHeld.get(file) ?? { seenMtime: undefined, askedMtime: undefined, rev: undefined, doc: null, lost: 'no file', none: null }
            registeredHeld.set(file, h)
            await read(file, h)
            tried.push({ file, h })
            if (knows(h.doc, now, id)) break
          }
        }
        const found = tried.find((t) => knows(t.h.doc, now, id))
        const h = found?.h ?? poolHeld
        doc = found === undefined ? h.doc : withoutTeams(h.doc, await closedOf(found.file.slice(0, Math.max(found.file.lastIndexOf('/'), found.file.lastIndexOf('\\'))), ownedTeamNames(h.doc, id), now))
        lost = h.lost
      } catch {
        doc = null
        lost = 'unreadable'
      }
      // Expiry and visibility do not matter here, only that a shape-valid doc lists this session.
      member = doc !== null && doc.memberSessions.includes(id)
      // Written only on change, since every write redraws the band and the Pane. Compared with the held value,
      // not a closure copy, because the state outlives a reload of this module.
      const view = viewModel(doc, now, id)
      // The cause is written before the view, so the empty Pane already names it when the view changes to null.
      const why = view === null ? (doc === null ? lost : nullCause(doc, now, id)) : null
      const heldWhy = await $.state.get({ plugin: 'wrangl', key: 'why' })
      if ((heldWhy.value ?? null) !== why) await $.state.set({ plugin: 'wrangl', key: 'why' }, why)
      const held = await $.state.get({ plugin: 'wrangl', key: 'view' })
      if (JSON.stringify(held.value) !== JSON.stringify(view)) await $.state.set({ plugin: 'wrangl', key: 'view' }, view)
      // Hooks off says so first (ah2-206 D5); then a held session with unread mail (ah2-180 D2); then the usual entry.
      const off = (await $.state.get({ plugin: 'wrangl', key: 'hooksOff' })).value === true
      const text = (off ? HOOKS_OFF_TEXT : undefined) ?? heldText(mailRun?.st.held === true, mailRun?.st.unread) ?? statusText(doc, now, id, statusEntry)
      if (text !== shown) { shown = text; $.ui.status(text) }
      // Each toast key shows at most once per session. The first view a session sees only seeds the set, so old
      // events are not replayed. Nothing changes on a tick with no view: an unreadable or expired file for one
      // tick must not empty the set and replay everything when the file comes back.
      const mine = scope(doc, id)
      if (view !== null && mine !== null) {
        const held = await $.state.get({ plugin: 'wrangl', key: 'seen' })
        if (held.value === undefined) await $.state.set({ plugin: 'wrangl', key: 'seen' }, view.toasts.map((t) => t.key))
        else {
          const kept = keepSeen(held.value, mine, now)
          const fresh = view.toasts.filter((t) => !kept.includes(t.key))
          const seen = [...kept, ...fresh.map((t) => t.key)]
          // Written before toasting, so a toast that fails is not shown again.
          if (JSON.stringify(seen) !== JSON.stringify(held.value)) await $.state.set({ plugin: 'wrangl', key: 'seen' }, seen)
          if (toastFor > 0) for (const t of fresh) await $.ui.toast(t.text, { timeoutMs: toastFor })
        }
      }
      if (paneTab((await $.state.get({ plugin: 'wrangl', key: 'paneMode' })).value) === 'sitrep' && !member && await paneShown($)) {
        // From any file read this tick that names the viewer's teams, shown or not: an owner whose band is hidden or expired still owns them.
        const owner = tried.find((t) => ownedTeamNames(t.h.doc, id).length > 0)
        // Not awaited: the poll is held open, and a tick must not wait for it.
        void sitrepRead(owner === undefined ? null : owner.file.slice(0, Math.max(owner.file.lastIndexOf('/'), owner.file.lastIndexOf('\\'))), owner === undefined ? [] : ownedTeamNames(owner.h.doc, id), `(${[
          `session ${id.slice(0, 8)}`,
          `socket ${link.socket ?? 'none'}`,
          `registrations ${link.rows ?? 'not read'}`,
          `own row ${link.row === undefined ? 'no' : 'yes'}`,
          ...tried.map((t) => `${t.file}: ${t.h.doc === null ? t.h.lost : nullCause(t.h.doc, now, id)}`),
        ].join('; ')})`)
      }
      // Opened unasked once per session, the first time there is something to show, unless the person has
      // closed it. Below the engine's width floor the open waits undrawn; that still counts as opened.
      if (view !== null) {
        const opened = await $.state.get({ plugin: 'wrangl', key: 'opened' })
        const closed = await $.state.get({ plugin: 'wrangl', key: 'closed' })
        if (opened.value !== true && closed.value !== true) {
          await $.state.set({ plugin: 'wrangl', key: 'opened' }, true)
          await $.ui.open({ id: 'ah-status', title: 'Hierarchy' })
        }
      }
    }

    await $.command.register({ name: 'wrangl-hierarchy-pane', description: 'Open the hierarchy status Pane' })
    // SessionStart (every settings and plugin hook below this one) must not wait on the first tick, which reads files and may
    // ask the daemon.
    void tick().catch(() => {})
    $.clock.every(2000, () => { daemonReady = true; return tick() })
    if (mail !== null && e.isInteractive !== false) mailRun = mailPoll($, mail, busy, () => (places === null ? null : places.pool.slice(0, Math.max(places.pool.lastIndexOf('/'), places.pool.lastIndexOf('\\')))), link, mailNotes)
    const passed = await next(e)
    void rebuildCheck($, rebuild)
    return passed
  })

  // The band goes above whatever the rest of the chain draws there, never in its place, and yields to a survey.
  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    if (e.props.hasSurvey || !bandOn) return next(e)
    const { value } = await $.state.get({ plugin: 'wrangl', key: 'view' })
    const { pane: wide, dismiss, textColumns } = bandButtons(value ?? null, e.props.bodyColumns)
    // With no view a member or a team owner still gets a short line naming why; any other session gets no band.
    const line = bandLine(value ?? null, textColumns) ?? noViewLine((await $.state.get({ plugin: 'wrangl', key: 'why' })).value, textColumns)
    if (line === null) return next(e)
    const { Box, Button, Text } = $.ui.resolve(e)
    return (
      <Box flexDirection="column">
        {wide ? (
          <Box flexDirection="row">
            <Text {...toneProps(line.tone)}>{line.text}</Text>
            <Box marginLeft={PANE_BUTTON.gap}>
              {member ? (
                <Button
                  key="ah-pane"
                  label={PANE_BUTTON.label}
                  onPress={async () => {
                    try {
                      if (await paneShown($)) {
                        await paneClose($)
                        return
                      }
                      await paneOpen($, member, toastFor)
                    } catch {}
                  }}
                />
              ) : (
                <Button
                  key="ah-view"
                  label={VIEW_BUTTON.label}
                  onPress={async () => {
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
                  }}
                />
              )}
            </Box>
            {dismiss ? (
              <Box marginLeft={DISMISS_BUTTON.gap}>
                <Button key="ah-dismiss" label={DISMISS_BUTTON.label} onPress={() => dismissTeam($)} />
              </Box>
            ) : null}
          </Box>
        ) : (
          <Text {...toneProps(line.tone)}>{line.text}</Text>
        )}
        {await next(e)}
      </Box>
    )
  })

  // Asked for, so the engine places it at any width; not placed means this session's surfaces show no panes.
  // Answers with text alone and never runs another command. A member session's Pane holds the Mail section alone.
  on('command.run', { command: 'wrangl-hierarchy-pane' }, async ($) => {
    return { text: (await openPane($, member)).text }
  })

  // A close by the person keeps the Pane closed for the session. Always passes the close on: answering without
  // next would keep the Pane open.
  on('ui.close', { id: 'ah-status' }, async ($, e, next) => {
    if (e.origin.kind === 'person') await $.state.set({ plugin: 'wrangl', key: 'closed' }, true)
    return next(e)
  })

  on('ui.render', { component: 'Pane', requestId: 'ah-status' }, async ($, e) => {
    const { value } = await $.state.get({ plugin: 'wrangl', key: 'view' })
    const cause = await $.state.get({ plugin: 'wrangl', key: 'why' })
    let mail: unknown
    let lost: string | null = mailNotes.lost
    try {
      mail = (await $.state.get({ plugin: 'wrangl', key: 'mail' })).value
    } catch {
      lost = 'state read failed'
    }
    const mode = paneTab((await $.state.get({ plugin: 'wrangl', key: 'paneMode' })).value)
    const held = mode === 'sitrep' ? (await $.state.get({ plugin: 'wrangl', key: 'sitrep' })).value : undefined
    const now = await $.clock.now()
    const { Box, Button, Text } = $.ui.resolve(e)
    const sections = member ? [mailSection(mail, lost, e.props.bodyColumns)]
      : mode === 'sitrep' ? sitrepSections(held, now, e.props.bodyColumns)
      : paneSections(value ?? null, e.props.bodyColumns, typeof cause.value === 'string' ? cause.value : null)
    return (
      <Box flexDirection="column">
        {sections.map((sec) => (
          <Box key={sec.key} flexDirection="column" {...(sec.bordered ? { borderStyle: 'round', ...(sec.color === null ? { borderDimColor: true } : { borderColor: sec.color }) } : {})}>
            {sec.title !== '' ? <Text bold {...colorProps(sec.color)}>{sec.title}</Text> : null}
            {sec.rows.map(({ indent, icon, iconColor, focus, tone, text }) => (
              <Box flexDirection="row">
                {indent > 0 ? <Text>{' '.repeat(indent)}</Text> : null}
                {icon !== null ? <Text {...colorProps(iconColor)}>{icon + ' '}</Text> : null}
                {focus !== null ? <Box key={'ah-focus-' + focus}><Button key={'ah-name-' + focus} plain label={focus} dimColor={tone === 'idle'} hover={{ underline: true }} onPress={() => focusMember($, focus)} /></Box> : null}
                <Text {...toneProps(tone)}>{text}</Text>
              </Box>
            ))}
          </Box>
        ))}
      </Box>
    )
  })
}
