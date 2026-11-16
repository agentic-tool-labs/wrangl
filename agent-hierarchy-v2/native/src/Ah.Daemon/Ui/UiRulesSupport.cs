using Ah.Core.Json;

namespace Ah.Daemon;

/// <summary>
/// What <c>GET</c> and <c>POST /ui/v1/rules</c> need from the host. <see cref="Read"/> takes a team's name; <see cref="Write"/> takes the checked body. Both answer with
/// <c>code</c>, the verbs' exit code: 0 ok, 1 not found, 2 refused, 3 conflict.
/// </summary>
public sealed record UiRulesSupport(Func<string, JsonObject> Read, Func<JsonObject, JsonObject> Write);

/// <summary>The request table of the rules routes. Holding a bridge session is the only check, and it is the bridge's own.</summary>
internal static class UiRulesRoute
{
    private static readonly string[] Fields = ["op", "team", "level", "key"];

    /// <summary>The status of a core's answer: its code, as HTTP.</summary>
    internal static int StatusOf(JsonObject reply) => (reply.Get("code") is { IsNumber: true } code ? (int)code.AsNumber() : 2) switch
    {
        0 => 200,
        1 => 404,
        3 => 409,
        _ => 400,
    };

    /// <summary>The status and body for one write. An exception from the core is the caller's 500.</summary>
    internal static (int Status, JsonObject Reply, string Outcome) Write(UiRulesSupport support, ReadOnlySpan<byte> body)
    {
        if (!JsonText.TryParse(body, out var parsed) || parsed.Kind != JsonKind.Map)
        {
            return Bad("the body must be one JSON object");
        }

        var obj = parsed.AsObject();
        var input = new JsonObject();
        foreach (var field in Fields)
        {
            if (obj.Str(field) is not { Length: > 0 } value)
            {
                return Bad($"{field} must be a non-empty string");
            }

            input[field] = value;
        }

        foreach (var field in new[] { "scope", "text", "applies", "choose" })
        {
            if (obj.Get(field) is { } value)
            {
                if (!value.IsText)
                {
                    return Bad($"{field} must be a string");
                }

                input[field] = value;
            }
        }

        var reply = support.Write(input);
        var status = StatusOf(reply);
        return (status, reply, status == 200 ? "ok" : $"refused status={status}");
    }

    private static (int, JsonObject, string) Bad(string detail) => (400, new JsonObject { ["code"] = 2, ["message"] = detail }, "bad-request");
}
