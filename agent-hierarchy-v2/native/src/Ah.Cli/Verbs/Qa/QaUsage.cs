namespace Ah.Cli.Verbs.Qa;

/// <summary>The usage <c>ah qa --help</c> prints.</summary>
internal static class QaUsage
{
    internal const string Text = """
        usage: ah qa <verb> [args]
          ah qa submit --repo <abs> --sha <40hex> --set <name> [--base <sha>] [--priority gate|lane|adhoc] [--fresh] [--session <id>] [--json]
          ah qa next [--max-set full|other] [--session <id>] [--json]
          ah qa done <id> (--result <path> | --error <one line>) [--session <id>] [--json]
          ah qa release <id> [--session <id>] [--json]
          ah qa withdraw <id> [--reason <text>] [--session <id>] [--json]
          ah qa status [<id>] [--json]
          ah qa list [--all] [--json]
        """;
}
