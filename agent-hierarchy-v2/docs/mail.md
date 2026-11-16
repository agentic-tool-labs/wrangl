# Mail between sessions

This page is for the person running a team who wants to know how a message from one session (the Orchestrator, a
member, a peer) reaches another. After reading it you will know what you see when mail arrives, where to read it in
full, and what to do when a message seems stuck. **Mail** and the **doorbell** are defined in the
[Glossary](glossary.md). The exact format and the rules for who may message whom are in
[comms-protocol.md](./comms-protocol.md).

Run the commands below in a terminal in your repository, or ask the Orchestrator to run them. Every command is in the
[CLI reference](cli-tools.md).

## How a message arrives

Each mail entry has exactly **one carrier**, chosen when the entry is created, so a message never shows up twice:

| Carrier | How it arrives | What you see |
|---|---|---|
| relay | the sending agent makes one `SendMessage` to the receiver | a message from the other session, beginning `ah mail from <from> to <to> (<kind>): `, with the text in it |
| plugin prompt | ah's mod delivers it as a prompt, when the receiver is not a live Claude session or the relay did not work | a short row, `ah mail for <address> (<n>):`, with one `- <kind> from <sender>` line per entry |

The Orchestrator is the exception. When its session is being served by the doorbell, a message takes the plugin
prompt and the Orchestrator sees one line, such as `ah: You have 2 messages (1 question, 1 report). Run ah msg next.`
The line is rung once after
about 20 seconds of quiet (at most 60 seconds later, and no more than once a minute); urgent mail, such as a failed
delivery or a member that is down, rings at once. `ah msg next` then shows one message at a time.

A relay is used when the receiver is a live Claude session. The agent that sends
is told whether to make the `SendMessage`. If it tries a second time for the
same entry, the second one is denied.

A `SendMessage` that carries only a mail entry's `[hierarchy-msg <id>]` pointer, without the
`[ah-entry …]` marker of that entry, is denied whenever a mail entry for that message exists in any mailbox, whatever
address the message goes to. The denial reads `ah: entry <entry id> carries [hierarchy-msg <id>] to <box>;
it is delivered by <carrier> … Do not SendMessage it.` It is there because a session could otherwise
send the pointer itself (to a cross-session address, for instance) and the receiver would see the
entry twice. A relay that ah asks for carries the marker and is not affected. The line `ah msg send`
prints for an entry that the plugin prompt delivers ends with `the plugin prompt delivers it; do not
SendMessage it`. The check is off when `single_carrier` is `false`.

## Falling back

A relay falls back to the plugin prompt, once, when:

- it was not started within `relay_lease_ms` (default 180000, three minutes), and
  the receiver is idle; or
- it failed, with positive evidence (an error result).

A relay that was started but cannot be confirmed (a result that could not be
read, or a confirmation that could not be written) is assumed sent. It is never
timed out and never falls back, because showing a message twice is worse than
showing it late. `claim_lease_ms` (default 60000, plus a 60-second grace) is how
long a started relay may go unsettled before it is treated as sent and left
alone.

## Reading mail

- **In the transcript**, a plugin-prompt row is short. The text of the entries is
  not in the transcript.
- **The Mail section of the Hierarchy pane** shows the full text of the last 10
  deliveries, newest first. Open it with `/wrangl-hierarchy-pane` or the band's Pane
  button. A member's own pane holds the Mail section alone. Longer text ends with
  a pointer to `ah msg inbox --all --address <addr>`.
- **`ah msg inbox`** lists the entries a session has not acknowledged, oldest
  first. It does not acknowledge them. A relay that is claimed or sent is tagged.
- **A toast** names the sender and where the full text is when mail arrives.
  Wrangl's `toast_seconds` option sets how long it stays (10 seconds by default); `0` turns toasts off.

## When a message seems stuck

```
ah roster doctor
```

A relay that was sent more than ten minutes ago and that nothing acknowledged is
listed in the `held-relays` row ("sent, not acknowledged"). Its receiver may be
busy, gone, or the message may have been lost on the way.

```
ah msg release <entry id>
```

hands that entry to the plugin prompt. A message that was only delayed will then
show twice. `release` works on a receiver's own mailboxes, or for the team's
Orchestrator. Anything else is refused.

If `ah msg send`, `ack` or another mail command fails with `busy: the mailbox state lock did not clear
in time`, another process is holding the mailbox's lock. A lock whose holder died is taken over once it
is 10 seconds old, and the commands wait up to 12 seconds for that, so a dead holder no longer causes
this error. If you still see it, a live process is holding the lock: run the command again. A hook gives up much
sooner, within its 1.5 second budget.

## A relay that is not needed any more

The Orchestrator, or the member that sent a message, is sometimes asked to relay a message that its receiver has
already got: the receiver read it in the meantime, or its work made the message moot. `ah` checks before it asks.

- **Already picked up.** When the message was acknowledged, its delivery ended, or a relay already holds it, the
  request is shown as one line, `ah: relay of <entry> no longer needed: <why>`, and nothing is owed. This is checked
  every time the request is shown: in the prompt, in its short and full forms, and at the end of a turn.
- **Facts.** A request that is still owed can say that the receiver has sent a report since (`reported_since`, with
  `same_request` when the message names that request), or that a newer unread message from the same sender exists
  (`newer_from_sender`). These do not cancel the request: a newer message can add an instruction the report does not
  cover. They are there so the carrier can decide.
- **Declining.** The carrier, and only the carrier, can end the relay with
  `ah msg decline <entry id> --reason already-reported|superseded|withdrawn [--note <text>]`. The decline is recorded
  on the message's delivery ladder, the relay never fires again, the message is **not** acknowledged (its receiver
  still finds it unread), and the carrier's next end-of-turn line lists the decline.
- **Superseding.** A sender that replaces its own unread message sends the new one with
  `ah msg send ... --supersedes <entry id>`. The old message's ladder ends, and any relay asked for it shows as no
  longer needed. The new message has its own ladder. If the old message's ladder row cannot be written, the new message is still sent;
  `ah msg send` prints `superseded row for <old id> not written: <reason>` on stderr and exits 1.
- **When you do relay.** The line to send now begins `(relayed: entry <id>, sent <time>; if you have already done
  this, reply "already done" and do not repeat it)`, so a receiver that did get the first copy ignores the second.

## Settings

All three go in an `agent-hierarchy.json` file: your own, `~/.claude/ah2/agent-hierarchy.json`, or the repository's,
`.claude/ah2/agent-hierarchy.json`. The repository's file wins over yours.

| Setting | Default | Meaning |
|---|---|---|
| `single_carrier` | `true` | one carrier per entry. `false` returns to the earlier delivery ladder (a relay request, then paste into the pane), and the claim check does nothing. |
| `relay_lease_ms` | `180000` | how long a relay may go unstarted before the plugin prompt is used. `0` makes the plugin prompt the carrier from the start. |
| `claim_lease_ms` | `60000` | how long a started relay may go unsettled before it is treated as sent. |

## After an upgrade

The daemon and the hooks must be the same build. A daemon of an older build
delivers relay mail as a plugin prompt too, so the message shows twice.
`ah roster doctor` is red on `daemon-contract` until you run `ahd restart`. See
[Upgrading](./upgrading.md).

A relay line written by the previous build does not match the line this build
computes, so an entry that was already in flight at the upgrade may show a second
time. It is never dropped.
