---
name: add-work-item
description: Records a new requirement or piece of work as a work item at once, rather than remembering it. For the Orchestrator. Use when the user says "add this", "new requirement", "we also need", "put on the backlog", "add a work item", when they state a requirement in passing ("it should also…") with no ask to track it, or when one is buried in a long message about something else, even in the middle of other work.
argument-hint: "[the work, in the user's words]"
---

# add-work-item

`<ah>` = the `ah` command in your context's `ah CLI root` line (newest wins).

Record it now, before you go on with anything else. An item in `ah work` survives a compaction; something you meant to remember does not. Do not ask for a wave, size or theme: grooming sets those. A message with several requirements gets one item each: run the steps once per requirement.

1. **Search first.** `<ah> work list --state open --limit 1000 --json`, and read the titles for the same work. If the user gave an external id, also try `<ah> work show <external id>`. If the item exists, skip step 2 and step 3, and use its id.
2. **Add it.** `<ah> work add "<title>" --body-file -` prints the new id. The title is a few plain words taken from what the user said. It goes at the root: pass `--parent <id>` only if the user named where it belongs. Stdin is the item's description, written with the write-ticket skill from what is already known. An unknown goes under "Decision needed first" and is not a question to the user now. A small ask gets a few lines, not every section.
3. **External id, only if given.** `<ah> work set <id> --ext <external id>`, with `--url <url>` if they gave one too.
4. **Write the history.** The body is the user's own words, unedited, on stdin: the description is the rewrite, this is the source. The title gives the source and the date:

   ```
   <ah> work record <id> --type note --title "From <source>, <YYYY-MM-DD>" --body-file -
   ```

   The source is who said it and where, such as `the user, this session` or an issue link. On an item that already existed, this entry is all you write. A body over 8 KiB is refused: keep the item, and split the words over several entries, or put them in a file and `<ah> work attach <id> <path>`.
5. **Answer in one line:** `Added <id>: <title>`, or `Added to <id>'s log: <title>` when it already existed.
