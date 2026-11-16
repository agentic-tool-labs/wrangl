using Ah.Core.Json;

namespace Ah.Daemon;

/// <summary>What the close routes need from the host: a dir id (or a known dir) resolved to its dir, and the shared close core.</summary>
public sealed record UiTeamCloseSupport(Func<string, string?> ResolveDir, Func<JsonObject, JsonObject> Close);

/// <summary>The one request table of <c>POST /v1/team/close</c> and <c>POST /ui/v1/team-close</c>; each route owns only who may call.</summary>
internal static class TeamCloseRoute
{
    /// <summary>The status and body for one request. An exception from the core is the caller's 500.</summary>
    internal static (int Status, JsonObject Reply, string Outcome) Run(UiTeamCloseSupport support, ReadOnlySpan<byte> body, string by)
    {
        if (!JsonText.TryParse(body, out var parsed) || parsed.Kind != JsonKind.Map)
        {
            return Bad("the body must be one JSON object");
        }

        var obj = parsed.AsObject();
        if (obj.Str("dir") is not { Length: > 0 } dirId)
        {
            return Bad("dir must be a non-empty string");
        }

        if (obj.Str("team") is not { Length: > 0 } team)
        {
            return Bad("team must be a non-empty string");
        }

        var input = new JsonObject { ["team"] = team, ["by"] = by };
        foreach (var flag in new[] { "reopen", "preview", "hold" })
        {
            if (obj.Get(flag) is { } value)
            {
                if (!value.IsBool)
                {
                    return Bad($"{flag} must be true or false");
                }

                input[flag] = value;
            }
        }

        if (obj.Get("confirm_live") is { } live)
        {
            if (!live.IsArray || !live.AsArray().All(static v => v.IsText))
            {
                return Bad("confirm_live must be a list of member names");
            }

            input["confirm_live"] = live;
        }

        if (support.ResolveDir(dirId) is not { } dir)
        {
            return (409, new JsonObject { ["refused"] = true, ["reason"] = "unknown-dir", ["detail"] = dirId }, "unknown-dir");
        }

        input["dir"] = dir;
        var result = support.Close(input);
        if (result.Get("refused") is not { IsMap: true } refused)
        {
            return (200, result, result.Get("preview") is { IsTruthy: true } ? "preview" : result.Get("reopened") is { IsTruthy: true } ? "reopened" : result.Get("hold") is { IsTruthy: true } ? "hold" : "closed");
        }

        var reason = refused.Member("reason") is { IsText: true } r ? r.AsString() : "refused";
        var reply = new JsonObject { ["refused"] = true, ["reason"] = reason, ["detail"] = refused.Member("detail") ?? JsonValue.Null };
        if (result.Get("preview") is { } preview)
        {
            reply["preview"] = preview;
        }

        return (409, reply, reason);
    }

    private static (int, JsonObject, string) Bad(string detail) => (400, new JsonObject { ["error"] = "bad-request", ["detail"] = detail }, "bad-request");
}
