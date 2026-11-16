# The web page

The web page is the easiest way to see your whole team at once: who is working, what is waiting on you, and how far
each stream has got. It runs on your machine only and shows live data from the daemon. This page tells you how to open
it, find your way around, and answer what is waiting on you.

## Open it

From a terminal:

```
ahd ui
```

Your browser opens at a local address on `127.0.0.1`. There is no sign-in: the page is served only on your machine. Leave the command running;
press Ctrl-C to close the page. To keep the page up in the background instead:

```
ahd ui start
ahd ui status
ahd ui stop
```

Inside a Claude Code session with the band showing, the band's **View** button offers Web. If the page cannot open
that way it tells you to run `ahd ui start` in a terminal. (There is no `ah ui`; the command is `ahd ui`.)

The page closes by itself after 15 minutes with no request, unless you started it with `ahd ui start`.

## Finding your way

The top bar has the team switcher, five team tabs, a **Sitrep** button, an **Inbox** button with a count, and a
**Settings** menu. Press `?` on any page for the list of keys.

| Key | Tab | What it shows |
|---|---|---|
| `1` | Overview | members grouped by state, the team's numbers, briefs in flight |
| `2` | Board | every stream and its items, as a list or as cards |
| `3` | Graph | the team as a map, a radial view or a flow (`Shift+M` switches) |
| `4` | Timeline | what happened, in order |
| `5` | Conversation | what a member said and did; `End` jumps to the live end |

Pages with no number key: **Home** (one row per team, with lanes nested under their team), the **All-teams board**
(`#/board?team=all`), the **Inbox** (two tabs: Attention, and Q&A), a **Member** page for one member, and the
**Looks** page.

An Orchestrator's row reads **Working** while its turn is open and **Running** while it is live and at rest between
turns. Other members read Working, Idle, Blocked, Gone or No signal.

In the **Inbox**, **Attention** holds the questions waiting on you, and **Q&A** is the log of questions and answers.
Open a row for something you asked to see its thread: the **Reply** that answered it comes first, and the rest of that
turn and anything said later is folded underneath, under "N later replies".

Other keys: `Up`/`Down` change the selected member (or row), `Enter` opens the selected member's conversation,
`/` focuses search, `Esc` closes a menu then clears the selection, `Shift+T` opens the team switcher, `Shift+D`
shows a performance strip, and `]` shows or hides the right-hand panel on a narrow window.

## The Board

The Board lists your streams with their items and progress. `Shift+L` switches between a list and a card board;
in the list, `Shift+G` changes how it is grouped (by team, stream, repo or status). `Shift+C` shows only what
changed since you last looked, and `Shift+A` marks everything seen. On the card board, `Shift+G` jumps to the
Graph for that stream instead.

Items on the Board come from the stream checklists ([Progress records](progress.md)). The page only reads them.

## The Sitrep

Press the **Sitrep** button (top-level teams only) for the one-screen answer to "where are we?". Three regions stack
top to bottom: **Completed**, **In progress** and **Up next**. **Waiting on you**, **Blocked**, **Report in, no one
on it**, **Stale** and **Up next, old** follow while they hold rows; the last three start closed. Each region's head collapses it, and
in-progress and other capped regions say "10 of 39" when there are more. A row is one line, the item's title: click it or press
Enter to open the item's detail.

Which regions you collapsed is kept in this browser, as is what the status line compares with to say whether anything changed since you last closed the
Sitrep. The Sitrep is built from the checklist, the ship records and the queue; see [How it works](how-it-works.md#the-checklist-and-the-sitrep).

## Work item detail

Everywhere a work item opens, on the Board or from the Sitrep, the same glass dialog fills the middle of the page: state, owner,
notes and the item's log. Esc or × closes it and focus returns to where you were.

## Rules

**Settings → Rules…** opens the rules of engagement for the current team. Pick a level: Repo, Team, Project or Goal. The
list shows what applies there, with each rule's text, key and the level it comes from.

- **A rule set at this level:** Edit, Switch off or on, and Remove. Switching off clears the rule's own text, so the
  status line shows it once, and Switch on asks for text again, starting from the team's or repo's rule of that key.
- **An inherited rule:** Override here, and Switch off here.
- **Add a rule:** up to 300 characters. The key is suggested from the text and you can change it. A refusal, such as a
  key already in use, shows under the form.

Live members see changes at their next start, compaction or brief. The Orchestrator can add rules too; see
`ah rules list`.

## Looks

A **look** is a named style for the page's glass panels, glow and background. Choose one in **Settings**: pick a
**Light look** and a **Dark look**. Each theme remembers its own. The built-in looks are House (the plain default),
Light Inky, Light Emissive and Dark Emissive.

**Settings → Theme** sets Light, Dark, or System (follows your computer).

### The look editor

Open **Settings → Looks…** (or go to `#/looks`). The list on the left shows the built-in looks and your own.
Built-in looks cannot be edited; copy one first.

1. Select a look and press **Copy**. You get an unsaved draft called "<name> copy".
2. Change the controls. The preview on the right updates as you go. The page also checks contrast and warns when
   text or marks fall below the target.
3. Press **Save**, then **Use for light** or **Use for dark**.

Controls are grouped as Halo (blur, intensity, tint), Glass (colour, opacity, frost, blur) and Backdrop (pattern,
colours, glow). Each look has two profiles, **Shadow** and **Dual**, on separate tabs.

Other actions: **Rename**, **Delete** (a look in use falls back to the default for its theme), **Export** (saves one
look as a `.json` file) and **Import…** (reads such a file). Leaving the page with an unsaved draft asks first.
There is no "reset all". To go back, choose House or a built-in look, or delete your own.

Looks, the theme, Board choices and the Sitrep regions are stored **in your browser**, not in the daemon. A different
browser, or a different `ahd ui` port, starts fresh. Export a look to keep it.

## Large teams

The page loads only what is on screen. A very large team shows "Large team: some live details refresh on open." If
that is not enough you see "subscription too large (N ids)". See [Troubleshooting](troubleshooting.md).

See also: [terminal view](tui.md), [what is coming](coming-soon.md).
