using Ah.Core.Json;

namespace Ah.Daemon;

/// <summary>
/// One answer request's outcome: the status and body <c>POST /ui/v1/answer</c> replies with (a null body is the 500 the route writes
/// itself), the log outcome, and the fields the body named, for the log line.
/// </summary>
public sealed record UiAnswerResult(int Status, JsonObject? Reply, string Outcome, string? DirId, string? Team, string? Id);

/// <summary>
/// The one request table of <c>POST /ui/v1/answer</c> and <c>ah msg decision ui-answer</c>: the body parsed, the dir resolved, the
/// host's shared answer core run, and its result mapped to the route's reply. Each caller owns only who may call.
/// </summary>
internal static class UiAnswerRoute
{
    /// <summary>The outcome of one answer body. An exception from the core is a 500 with no body, its type in the outcome.</summary>
    public static UiAnswerResult Run(UiAnswerSupport support, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(support);
        var bad = AnswerInput(body, out var input);
        var dirId = input.Str("dir");
        var team = input.Str("team");
        var id = input.Str("id") ?? input.Str("item");
        UiAnswerResult Done(int status, JsonObject? reply, string outcome) => new(status, reply, outcome, dirId, team, id);

        if (bad is not null)
        {
            return Done(400, new JsonObject { ["error"] = "bad-request", ["detail"] = bad }, $"bad-request detail={UiSession.Safe(bad)}");
        }

        if (support.ResolveDir(dirId!) is not { } dir)
        {
            return Done(409, new JsonObject { ["refused"] = true, ["reason"] = "unknown-dir", ["detail"] = dirId! }, "unknown-dir");
        }

        input["dir"] = dir;
        JsonObject result;
        try
        {
            result = support.Answer(input);
        }
#pragma warning disable CA1031 // any failure of the answer is one logged 500, whether or not a line was written
        catch (Exception e)
#pragma warning restore CA1031
        {
            return Done(500, null, $"error detail={UiSession.Safe(e.GetType().Name)}");
        }

        var eventNote = result.Str("event_error") is { } eventError ? $" event=failed: {UiSession.Safe(eventError)}" : string.Empty;
        if (result.Get("refused") is { IsMap: true } refused)
        {
            var reason = JsConvert.ToJsString(refused.Member("reason") ?? JsonValue.Null);
            return Done(409, new JsonObject { ["refused"] = true, ["reason"] = reason, ["detail"] = refused.Member("detail") ?? JsonValue.Null }, UiSession.Safe(reason));
        }

        // what only the item's own options can judge: a letter past them
        if (result.Str("bad_request") is { } refusedChoice)
        {
            return Done(400, new JsonObject { ["error"] = "bad-request", ["detail"] = refusedChoice }, $"bad-request detail={UiSession.Safe(refusedChoice)}");
        }

        var reply = new JsonObject { ["ok"] = true, ["id"] = result.Get("id") ?? JsonValue.Null, ["answers"] = result.Get("answers") ?? JsonValue.Null, ["decision"] = result.Get("decision") ?? JsonValue.Null };
        if (result.Str("event_error") is { } told)
        {
            reply["event_error"] = told;
        }
        else if (result.Str("event_id") is { } eventId)
        {
            reply["event_id"] = eventId;
        }

        return Done(200, reply, "answered" + eventNote);
    }

    /// <summary>
    /// The answer body as the support's input (<c>{dir: &lt;dirId&gt;, team, run, id, choice | choices}</c>, <c>run</c> optional for a <c>q&lt;k&gt;</c> id), or why it is a bad request:
    /// every field a non-empty string, and exactly one of <c>choice</c> (a string) and <c>choices</c> (one or more strings). The fields
    /// that did parse are in <paramref name="input"/> either way, for the log.
    /// </summary>
    private static string? AnswerInput(ReadOnlySpan<byte> body, out JsonObject input)
    {
        input = new JsonObject();
        if (!JsonText.TryParse(body, out var parsed) || parsed.Kind != JsonKind.Map)
        {
            return "the body must be one JSON object";
        }

        var obj = parsed.AsObject();
        string? missing = null;
        var isItem = obj.ContainsKey("item");
        // ah2-377 P6: a parked question (q<k>) needs no run; every other decision id still does
        var runless = !isItem && !obj.ContainsKey("run") && Ah.Hierarchy.ParkedQuestions.IsId(obj.Str("id"));
        foreach (var k in isItem ? ItemFields : DecisionFields)
        {
            if (k == "run" && runless)
            {
                continue;
            }

            if (obj.Str(k) is { Length: > 0 } value)
            {
                input[k] = value;
            }
            else
            {
                missing ??= $"{k} must be a non-empty string";
            }
        }

        if (missing is not null)
        {
            return missing;
        }

        if (obj.ContainsKey("dismiss"))
        {
            return DismissInput(obj, input, isItem);
        }

        if (isItem)
        {
            return ItemAnswerInput(obj, input);
        }

        var hasChoice = obj.ContainsKey("choice");
        if (hasChoice == obj.ContainsKey("choices"))
        {
            return "give exactly one of choice and choices";
        }

        if (hasChoice)
        {
            if (obj.Str("choice") is not { Length: > 0 } choice)
            {
                return "choice must be a non-empty string";
            }

            input["choice"] = choice;
            return null;
        }

        if (obj["choices"] is not { IsArray: true } choices || choices.AsArray().Count == 0 || !choices.AsArray().All(static c => c.IsText && c.AsString().Length > 0))
        {
            return "choices must be one or more non-empty strings";
        }

        input["choices"] = choices;
        return null;
    }

    /// <summary>The longest dismissal reason, in UTF-8 bytes (spec ah-ui-answer §6).</summary>
    private const int DismissMaxBytes = 64;

    /// <summary>A dismissal in place of an answer: <c>dismiss</c> a non-blank reason of at most <see cref="DismissMaxBytes"/> bytes, with no <c>choice</c> or <c>choices</c>; an item's body still carries no decision field.</summary>
    private static string? DismissInput(JsonObject obj, JsonObject input, bool isItem)
    {
        if (obj.ContainsKey("choice") || obj.ContainsKey("choices"))
        {
            return "give dismiss in place of choice and choices";
        }

        if (isItem && NotItemFields.FirstOrDefault(obj.ContainsKey) is { } stray)
        {
            return $"{stray} is not part of an item answer";
        }

        if (isItem && (input.Str("item") is not { } key || key.IndexOf(':', StringComparison.Ordinal) is <= 0 || key.EndsWith(':')))
        {
            return "item must be <stream>:<item>";
        }

        if (obj.Str("dismiss") is not { } reason || string.IsNullOrWhiteSpace(reason) || System.Text.Encoding.UTF8.GetByteCount(reason) > DismissMaxBytes)
        {
            return $"dismiss must be a reason of 1 to {DismissMaxBytes} bytes";
        }

        input["dismiss"] = reason;
        return null;
    }

    private static readonly string[] DecisionFields = ["dir", "team", "run", "id"];

    private static readonly string[] ItemFields = ["dir", "team", "item", "since"];

    private static readonly string[] NotItemFields = ["run", "id", "choices"];

    /// <summary>The rest of an item answer's body: no field of the decision form, a <c>&lt;stream&gt;:&lt;item&gt;</c> key, and one <c>choice</c>. Whether the choice names an option is the host's to say.</summary>
    private static string? ItemAnswerInput(JsonObject obj, JsonObject input)
    {
        if (NotItemFields.FirstOrDefault(obj.ContainsKey) is { } stray)
        {
            return $"{stray} is not part of an item answer";
        }

        if (input.Str("item") is not { } key || key.IndexOf(':', StringComparison.Ordinal) is <= 0 || key.EndsWith(':'))
        {
            return "item must be <stream>:<item>";
        }

        if (obj.Str("choice") is not { Length: > 0 } choice)
        {
            return "choice must be a non-empty string";
        }

        input["choice"] = choice;
        return null;
    }
}
