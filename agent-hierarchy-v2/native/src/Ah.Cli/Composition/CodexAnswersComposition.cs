using System.Text;
using Ah.Abstractions;
using Ah.Commands;
using Ah.Core.Json;

namespace Ah.Cli.Composition;

/// <summary>The answer verb's route to the daemon: <c>POST /v1/codex/answer</c>, never started on demand, never retried.</summary>
internal sealed class CodexAnswersComposition(ServiceGraph graph) : IRosterCodexAnswers
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public CodexAnswerReply Answer(string team, string member, string requestId, string? choice, IReadOnlyList<KeyValuePair<string, string>> answers, string? text)
    {
        var body = new JsonObject { ["team"] = team, ["member"] = member, ["request_id"] = requestId };
        if (choice is not null)
        {
            body["choice"] = choice;
        }

        if (text is not null)
        {
            body["text"] = text;
        }

        if (answers.Count > 0)
        {
            var map = new JsonObject();
            foreach (var (qid, value) in answers)
            {
                map[qid] = value;
            }

            body["answers"] = map;
        }

        DaemonReply reply;
        try
        {
            reply = graph.Daemon.RequestAsync(new DaemonRequest("POST", "/v1/codex/answer", Encoding.UTF8.GetBytes(JsonText.Stringify(body, 0)), Budget, StartIfDown: false)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new CodexAnswerReply(null, $"daemon-unavailable: {e.Message}");
        }

        if (reply.Status == 0)
        {
            return new CodexAnswerReply(null, $"daemon-unavailable: {reply.FallbackReason ?? "down"}");
        }

        JsonObject? parsed = null;
        try
        {
            parsed = JsonText.Parse(reply.Body.ToArray()) is { IsMap: true } o ? o.AsObject() : null;
        }
        catch (JsonParseException)
        {
            // an unreadable body is reported below with the status
        }

        if (reply.Status is >= 200 and < 300 && parsed is not null)
        {
            return new CodexAnswerReply(parsed, null);
        }

        var error = parsed?.Get("error");
        var code = error?.Member("code") is { IsText: true } c ? c.AsString() : $"http {reply.Status}";
        var message = error?.Member("message") is { IsText: true } m ? m.AsString() : string.Empty;
        return new CodexAnswerReply(null, $"{code}: {message}".TrimEnd(' ', ':'));
    }
}
