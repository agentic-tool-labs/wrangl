using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Activity;

/// <summary>Reads what the tool result of a SendMessage says about whether the message reached the recipient's inbox.</summary>
internal static class RelayResults
{
    private static readonly string[] FailureMarkers = ["no inbox", "unknown recipient", "no such session", "not found", "no session"];

    /// <summary>
    /// <see cref="RelayResult.Sent"/> only when the result says so (<c>success: true</c>, or text that names the inbox), and
    /// <see cref="RelayResult.Failed"/> only when it says the call errored or found no inbox; anything else is
    /// <see cref="RelayResult.Unknown"/>, which moves no claim.
    /// </summary>
    public static RelayResult Of(JsonValue? response, out string? reason)
    {
        reason = null;
        if (response is not { } value || value.IsNull)
        {
            return RelayResult.Unknown;
        }

        if (value.IsMap)
        {
            var o = value.AsObject();
            var text = TextOf(value);
            if (o.Get("is_error") is { IsBool: true } isError && isError.AsBool()
                || o.Get("success") is { IsBool: true } success && !success.AsBool()
                || o.Get("error") is { IsText: true } error && error.AsString().Length > 0)
            {
                reason = (o.Get("error") is { IsText: true } e ? e.AsString() : text) is { Length: > 0 } why ? why : "tool error";
                return RelayResult.Failed;
            }

            if (o.Get("success") is { IsBool: true } ok && ok.AsBool())
            {
                return RelayResult.Sent;
            }

            return Classify(text, out reason);
        }

        return Classify(TextOf(value), out reason);
    }

    private static RelayResult Classify(string text, out string? reason)
    {
        reason = null;
        var lower = text.ToLowerInvariant();
        if (FailureMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
        {
            reason = text.Length > 200 ? text[..200] : text;
            return RelayResult.Failed;
        }

        return lower.Contains("inbox", StringComparison.Ordinal) ? RelayResult.Sent : RelayResult.Unknown;
    }

    private static string TextOf(JsonValue value)
    {
        if (value.IsText)
        {
            return value.AsString();
        }

        if (value.IsArray)
        {
            return string.Join("\n", value.AsArray().Select(TextOf).Where(t => t.Length > 0));
        }

        if (!value.IsMap)
        {
            return string.Empty;
        }

        var o = value.AsObject();
        foreach (var key in new[] { "message", "content", "result", "text", "output" })
        {
            if (o.Get(key) is { } v && TextOf(v) is { Length: > 0 } found)
            {
                return found;
            }
        }

        return string.Empty;
    }
}
