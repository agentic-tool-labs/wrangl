namespace Ah.Cli.Verbs.Msg;

/// <summary>The text <c>msg --help</c> prints: the CLI's own usage block.</summary>
internal static class MsgUsage
{
    internal const string Text = """
        agent-hierarchy — message-file CLI.

          ah msg new --to <role> --from <role> --slug <s> [--to-name <n>] [--from-name <n>]
                      [--parent <id>] [--reason context|second-opinion|parallel]
                      [--eta small|medium|large] [--type request|response] [--id <id>] [--team <name>] [--item <checklist item>]
                      [--serves "<how this serves the team goal>" | --serves "off-goal: <why>"]   (request only; guidance, never refused: cut to 160 characters)
                      [--title <a few plain English words, verb first, at most 60 characters>]   (request only: the human title of the exchange, like "Make UI slick";
                      required from orchestrator and sub-orchestrator; at least two words, not a slug, id or path; no control characters, no " #", no ---, not the word null)
                      [--req <abs request path>]   (response only: write it beside that request)
                      [--body-file <path>|-]   (write this text, or standard input for -, as the body under the frontmatter in the same call, instead of the skeleton;
                      at most 64 KiB, UTF-8, no line that is exactly ---, and more than the skeleton. A repeat of the same call returns the same file)
                      (pass --to-name when the brief is for a specific member; the UI's per-member view relies on it; the member name exactly as in the team file)
          ah msg fill --id <id> --from <role> (--body '<JSON string>' | --body-file <path>|-) [--section <n>] [--request] [--part] [--req <abs request path>] [--evidence <path>]...
                      (replace everything after the frontmatter of the response to request <id>, creating the response first
                      when there is none; the frontmatter is kept byte for byte. --section <n>: replace only the body of `## [n]`,
                      appending it when absent. --request: amend the request itself (its author only). The body is one JSON string: `\n` for a newline, `\u0027` for a
                      single quote, since a literal one cannot sit inside the shell's single quotes. At most 256 KiB, no line
                      that is exactly `---`. May be repeated: each run puts the new body first and keeps the earlier one below it. Prints {id, type, rev, bytes}.)
                      (--part: a partial report, one per deliverable: appends the text under `## part <n> · <time>` and keeps the request open;
                      a plain fill afterwards is the final report and closes it. Ignored with --request)
                      (--evidence <path>: repeatable; copies the file read-only to <ah home>/evidence/<id>/e<k>-<name> and appends a `## evidence`
                      section citing evidence:<id>/e<k>. At most 20 files and 50 MiB per message, 20 MiB per file; a regular, readable file)
          ah msg show <id> [request|response|note] [--section <n>]... [--out <file> [--force]] [--to <name>] [--pool <dir>]
                      (print the message, frontmatter and body; --section prints only those `## [n]` sections)
          ah msg list [--open|--closed|--all] [--to <role>] [--team <name>] [--json] [--plain]
          ah msg downstream [--root-name <name>]
          ah msg index <id> [request|response|note]
          ah msg sweep [--days 7]
          ah msg roster
          ah msg decision add [--team <name>] [--json '<object>' | --input <path>]   (one JSON object from the argument or the file, else stdin: a /wrangl:pipeline run's decision log)
          ah msg decision list [--team <name>] [--run <anchor id>] [--item <slug>] [--summary]
          ah msg decision answer <parked id> --choice <a|b|c|d|other:<text>> [--team <name>]   (the user's answer to a parked decision)
          ah msg decision park --header <h> --question <q> --option <label> --option <label>... --default <text> [--class blocker|soon|eventually]   (the Orchestrator's question for the user, without holding its turn)
          ah msg send <name@team|name> (--ref <id> | --text <s> | --event <name>) [--kind brief|report|note|event]
                      (a --kind note carries --text of at most 1000 characters, --ref <abs file> for anything longer, or both)
                      [--reply-to <entry id>] [--supersedes <entry id>] [--eta <dur>] [--id <entry id>] [--from <address>] [--session <id>] [--json]
          ah msg inbox [--address <name@team>] [--all] [--overdue] [--session <id>] [--json]
          ah msg ack <entry id>... [--session <id>] [--json]
          ah msg status <entry id> [--session <id>] [--json]   (read-only: routed, pending, delivered, acked, superseded or declined, and whether a relay is owed)
          ah msg next [--team <t>] [--peek] [--again] [--all]   (the Orchestrator's waiting messages, one at a time; --all every queued one)
          ah msg release <entry id> [--session <id>] [--json]   (hands a sent relay nobody showed to the plugin prompt)
          ah msg due --json   (the request object on stdin: the Claude Code mod's poll for mail that is due; not for people)
          ah msg history [--search <text>] [--team <name>] [--answered|--unanswered] [--questions] [--limit 20] [--since <ms|instant>] [--before <cursor>] [--all] [--full] [--id <thread>] [--dir <hier dir>] [--json]   (read-only: your prompts to the Orchestrator and its replies, newest first)
          ah msg decline <entry id> --reason already-reported|superseded|withdrawn [--note <text>] [--session <id>] [--json]
                      (the carrier of a relay request ends it; the entry stays unread)
          ah msg withdraw <entry id> --reason <text> [--to <name@team|name>] [--from <address>] [--json]
          ah msg withdraw <exchange id> --reason <text> [--team <name>] [--json]   (the request's sender closes an exchange no mailbox holds)
          ah msg forward <entry id> --to <addr> [--note <text> | --note-ref <path>] [--json]
                      (the Orchestrator, a lane lead or a deputy chief passes on an entry of its own box, with a note, and acks the original for itself;
                      at most 4 hops; a member replies instead)
          ah msg close <request id> --reason superseded|abandoned [--note <text>] [--team <name>] [--json]   (the team's Orchestrator retires a request nobody can answer)
                      (the sender ends a mailed brief, even once it was read: the addressee gets a note and no longer owes it; --to also mails it to another member of the same role)
          ah msg link-request --to <name@team>[,<name@team>] --task <request id> --why <text> [--direction both|out] [--messages N] [--minutes M]
                      (ask the Orchestrator for a direct link to a teammate for one task; prints the request id)
          ah msg link grant <request id> [--messages N] [--minutes M]   (Orchestrator only; also deny <request id> --why <text>, revoke <link id>)
          ah msg link extend-request <link id> --why <text> [--messages N] [--minutes M]   (a peer of the link asks for more)
          ah msg link extend <request id> [--messages N] [--minutes M]   (Orchestrator only)
          ah msg link list | show <link id> [--tail N] [--since N] [--wait S] | post <link id> --text <s>   (post: Orchestrator only)
          ah msg join accept <join id> --basis user-asked|user-approved|rejoin --why <text> [--name <n>] [--role <r>] | join deny <join id> --basis user-declined|no-answer|orchestrator --why <text>   (Orchestrator only: decide a custom harness's request to join the team)
          ah msg send '*@<team>' ...   (broadcast; the team's Orchestrator only)
          ah msg daemon start|status|stop [--json]   (the ahd daemon; send/inbox/ack use it when it is up and fall back to files;
                      aliases of bin/ahd, which also inspects and checks it)

        Every subcommand also accepts `--orchestrator-pid <pid>` (else `CLAUDE_PID`) as a hint at which of
        its live sessions the caller means; a pid this process does not provably hold is refused.
          ah msg route [peers] --session <id>     (chain roles run only as peers)

        Every subcommand takes `--cwd <path>` (default process.cwd()) and resolves
        the runtime dir (the hierarchy directory); output is JSON unless `--plain`. Writers
        never hand-roll ids or skeletons — `new` is the only way a file is born.
        Bad args or a response without its request exit non-zero with one line on
        stderr.

        `--team <name>` scopes `new`/`list`/`roster` to a named team instead of
        the default. Omitted, `new` records no team (`team: null`) and a pid
        hint does not choose one; the recipient's team file is found from the
        message's addressee. `route` writes
        `gates.jsonl`, which is shared and unscoped — no
        `--team` there.
        """;
}
