using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    /// <summary>
    /// <c>history</c>: the user's question log, newest thread first: each typed prompt with every reply that followed it, and each terminal
    /// dialog with its answer. Read-only. Replies with no typed prompt show only with <c>--all</c>.
    /// </summary>
    private int History()
    {
        // --dir names a hierarchy dir directly: the herdr mod's History runs this with the dir its slice names, not from a checkout
        var dir = _opts.Text("dir") ?? HierarchyDir;
        var limit = int.TryParse(_opts.Text("limit"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 20;
        double? since = null;
        if (_opts.Text("since") is { } s)
        {
            since = double.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms
                : Hier.Decisions.TryParseInstant(s, out var at) ? at.ToUnixTimeMilliseconds()
                : throw _out.Fail("--since takes epoch milliseconds or an RFC 3339 instant");
        }

        if (_opts.Has("answered") && _opts.Has("unanswered"))
        {
            throw _out.Fail("--answered and --unanswered cannot be given together");
        }

        QaCursor? before = null;
        if (_opts.Has("before"))
        {
            before = (_opts.Text("before") is { } b ? HierarchyModule.QaCursorOf(b) : null)
                ?? throw _out.Fail("--before takes a cursor, <at>:<id>, the next a --json read printed");
        }

        var query = new QaHistoryQuery
        {
            Search = _opts.Text("search"),
            Team = _opts.Text("team"),
            Since = since,
            Answered = _opts.Has("answered") ? true : _opts.Has("unanswered") ? false : null,
            Questions = _opts.Has("questions"),
            All = _opts.Has("all"),
            Id = _opts.Text("id"),
            Before = before,
            Limit = limit,
        };
        var page = HierarchyModule.QaHistoryPage(HierarchyModule.QaThreads(Hier.Decisions.QaLogRows(dir)), query);

        // with --id the body is exactly what GET /ui/v1/history?id= answers, so it carries no next
        if (_opts.Has("json"))
        {
            _out.Json(query.Id is null ? HierarchyModule.QaHistoryJson(page, dir, heads: false) : HierarchyModule.QaHistoryJson(page.Threads, dir));
            return 0;
        }

        if (page.Threads.Count == 0)
        {
            var filtered = !string.IsNullOrEmpty(query.Search) || query.Team is not null || query.Since is not null || query.Answered is not null
                || query.Questions || query.Id is not null || query.Before is not null;
            _out.Text(filtered ? "no questions match" : "no questions logged");
            return 0;
        }

        foreach (var t in page.Threads)
        {
            _out.Text(HistoryText(t, _opts.Has("full")));
        }

        return 0;
    }

    /// <summary>
    /// A thread as text: the prompt, then each reply. A direct reply, and a turn row with no <c>final</c> (every row written before replies
    /// had parts), print their text as <c>reply</c>; a turn row with a <c>final</c> prints it as <c>final reply</c>, and its whole text only
    /// with <paramref name="full"/>. A reply after a later turn is marked <c>later</c>.
    /// </summary>
    private static string HistoryText(QaThread t, bool full)
    {
        var lines = new List<string>();
        if (t.Ask is { } ask)
        {
            lines.Add($"{When(t.At)}  terminal");
            var answers = ask.Member("answers");
            foreach (var q in ask.Member("questions") is { IsArray: true } qs ? qs.AsArray() : [])
            {
                var question = FrontText(q.Member("question")) ?? string.Empty;
                lines.Add("  " + (FrontText(q.Member("header")) is { } h ? h + ": " : string.Empty) + question);
                lines.Add("  → " + (answers?.Member(question) is { IsNull: false } a ? JsConvert.ToJsString(a) : "(no answer)"));
            }
        }
        else
        {
            var p = t.Prompt;
            lines.Add($"{When(t.At)}  {(p is null ? "Orchestrator, unprompted" : "asked by you")}{(p?.Member("question") is { IsBool: true } q && q.AsBool() ? "  [question]" : string.Empty)}");
            if (p is not null)
            {
                lines.Add(Indent(FrontText(p.Value.Member("text")) ?? string.Empty, "  > "));
            }

            foreach (var r in t.Replies)
            {
                var at = r.Member("at") is { IsNumber: true } ms ? ms.AsNumber() : 0;
                var later = FrontText(r.Member("trigger")) == "typed" ? string.Empty : "later ";
                var cut = r.Member("truncated") is { IsTruthy: true } ? " (truncated)" : string.Empty;
                if (FrontText(r.Member("part")) != "direct" && FrontText(r.Member("final")) is { Length: > 0 } final)
                {
                    lines.Add($"  {later}final reply {When(at)}{(r.Member("final_truncated") is { IsTruthy: true } ? " (truncated)" : string.Empty)}:");
                    lines.Add(Indent(final, "    "));
                    if (full)
                    {
                        lines.Add($"  {later}whole turn {When(at)}{cut}:");
                        lines.Add(Indent(FrontText(r.Member("text")) ?? string.Empty, "    "));
                    }

                    continue;
                }

                lines.Add($"  {later}reply {When(at)}{cut}:");
                lines.Add(Indent(FrontText(r.Member("text")) ?? string.Empty, "    "));
            }
        }

        return string.Join('\n', lines) + "\n";
    }

    private static string When(double ms) => DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Indent(string text, string prefix) => string.Join('\n', text.Split('\n').Select(l => prefix + l));
}
