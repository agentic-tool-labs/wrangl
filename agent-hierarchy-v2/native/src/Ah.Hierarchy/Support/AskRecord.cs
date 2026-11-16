using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// The one shape of a row in <c>asks.jsonl</c> and of the <c>ruling</c> ledger event that goes with it. The hook that records a harness
/// answer and the verb that records an answer given outside the harness both build theirs here, so a reader cannot tell them apart.
/// </summary>
public static class AskRecord
{
    /// <summary>The longest <c>metadata.source</c> a row keeps; nothing else of the call's metadata is stored.</summary>
    public const int MaxSourceChars = 200;

    /// <summary>One asks row: what was asked and what came back, both as received.</summary>
    public static JsonObject Row(string at, string writer, JsonValue? sessionId, JsonValue questions, JsonValue response, string? source)
    {
        var row = new JsonObject
        {
            ["at"] = at,
            ["writer"] = writer,
            ["session_id"] = sessionId ?? JsonValue.Null,
            ["questions"] = questions,
            ["response"] = response,
        };
        if (source is not null)
        {
            row["metadata"] = new JsonObject { ["source"] = source.Length <= MaxSourceChars ? source : source[..MaxSourceChars] };
        }

        return row;
    }

    /// <summary>The ledger <c>ruling</c> for an answer: each question's header and the label(s) the user chose.</summary>
    public static LedgerEvent Ruling(JsonValue questions, JsonValue response, string? toolUseId)
    {
        var answers = response.Member("answers");
        var parts = new List<string>();
        foreach (var q in questions.AsArray())
        {
            var question = q.Member("question") is { IsText: true } qt ? qt.AsString() : string.Empty;
            var header = q.Member("header") is { IsText: true } ht && ht.AsString().Length > 0 ? ht.AsString() : question;
            var chosen = answers?.Member(question) switch
            {
                { IsText: true } one => one.AsString(),
                { IsArray: true } many => string.Join(", ", many.AsArray().Where(a => a.IsText).Select(a => a.AsString())),
                _ => string.Empty,
            };
            parts.Add(chosen.Length > 0 ? header + ": " + chosen : header);
        }

        return new LedgerEvent("ruling", Text: string.Join("; ", parts), SourceType: "tool", ToolUseId: toolUseId);
    }
}
