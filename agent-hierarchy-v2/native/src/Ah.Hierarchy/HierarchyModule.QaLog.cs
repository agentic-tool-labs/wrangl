using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>The user's question log (the <c>qa_log</c> table): the forwarders the hooks and the CLI build and read its rows through.</summary>
public static partial class HierarchyModule
{
    public static JsonObject QaPromptRow(string id, double atMs, string sessionId, string? team, string text) => QaLog.PromptRow(id, atMs, sessionId, team, text);

    public static JsonObject QaReplyRow(double atMs, string sessionId, string? team, string text, string? promptId, bool typed, bool cut, string? final = null, string? openingId = null) =>
        QaLog.ReplyRow(atMs, sessionId, team, text, promptId, typed, cut, final, openingId);

    public static JsonObject QaDirectRow(double atMs, string sessionId, string? team, string text, string promptId) => QaLog.DirectRow(atMs, sessionId, team, text, promptId);

    public static JsonObject QaAskRow(double atMs, string sessionId, string? team, JsonValue questions, JsonValue response) =>
        QaLog.AskRow(atMs, sessionId, team, questions, response);

    /// <summary>Whether a prompt is text the user typed, not one a harness, agent or plugin injected: what the log records and History shows.</summary>
    public static bool QaIsUserTyped(string prompt) => QaLog.IsUserTyped(prompt);

    public static QaTurnReply QaReplyOf(IFileReader files, string transcriptPath, List<string>? commands = null, Func<string, bool>? isQuestion = null) =>
        QaLog.ReplyOf(files, transcriptPath, commands, isQuestion);

    public static (string Direct, string Question)? QaEarlyStepDirect(IFileReader files, string transcriptPath, string toolUseId, Func<string, bool> isQuestion) =>
        QaLog.EarlyStepDirect(files, transcriptPath, toolUseId, isQuestion);

    /// <summary>
    /// The prompt an Orchestrator's relay of a parked question's answer rests on: the session's latest <c>user-prompt</c> record when it is
    /// newer than the park at <paramref name="parkedAt"/>, else null, which <c>ah msg decision answer</c> refuses as <c>answer_no_user_prompt</c>.
    /// </summary>
    public static string? RelayPromptId(JsonValue? latestUserPrompt, string parkedAt) =>
        latestUserPrompt?.Member("ts") is { IsText: true } ts
        && DateTimeOffset.TryParse(ts.AsString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var typedAt)
        && DateTimeOffset.TryParse(parkedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parked)
        && typedAt > parked && latestUserPrompt?.Member("prompt_id") is { IsText: true } id
            ? id.AsString()
            : null;

    /// <summary>The id of the session's stored question row a transcript boundary's text names, or null (none, or two equally recent).</summary>
    public static string? QaQuestionFor(IReadOnlyList<JsonValue> rows, string sessionId, string boundary) => QaLog.QuestionFor(rows, sessionId, boundary);

    public static IReadOnlyList<QaThread> QaThreads(IReadOnlyList<JsonValue> rows) => QaLog.Threads(rows);

    public static JsonObject QaThreadJson(QaThread thread) => QaLog.ThreadJson(thread);

    /// <summary>The History's one filter (spec qa-history-replies §4.6), for the CLI and the daemon alike: the page of threads a query keeps.</summary>
    public static QaPage QaHistoryPage(IReadOnlyList<QaThread> threads, QaHistoryQuery query) => QaLog.Page(threads, query);

    /// <summary>A cursor's text, <c>&lt;at&gt;:&lt;head id&gt;</c> as a page's <c>next</c> prints it, or null when it is not one.</summary>
    public static QaCursor? QaCursorOf(string text) => QaLog.CursorOf(text);

    /// <summary>What <c>GET /ui/v1/history?id=</c> answers: <c>{threads, dir}</c>.</summary>
    public static JsonObject QaHistoryJson(IEnumerable<QaThread> threads, string dir)
    {
        var rows = new JsonArray();
        foreach (var t in threads)
        {
            rows.Add(QaLog.ThreadJson(t));
        }

        return new JsonObject { ["threads"] = rows, ["dir"] = dir };
    }

    /// <summary>
    /// What <c>ah msg history --json</c> prints and the list read of <c>GET /ui/v1/history</c> answers: <c>{threads, dir, next}</c>. With
    /// <paramref name="heads"/> each reply's <c>text</c> and <c>final</c> is cut to its head, marked <c>cut: true</c>.
    /// </summary>
    public static JsonObject QaHistoryJson(QaPage page, string dir, bool heads)
    {
        var rows = new JsonArray();
        foreach (var t in page.Threads)
        {
            rows.Add(heads ? QaLog.ListThreadJson(t) : QaLog.ThreadJson(t));
        }

        return new JsonObject { ["threads"] = rows, ["dir"] = dir, ["next"] = page.Next is { } next ? next.ToString() : JsonValue.Null };
    }
}
