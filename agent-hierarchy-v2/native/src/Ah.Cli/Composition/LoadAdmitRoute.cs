using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>
/// <c>POST /v1/load/admit-spawn</c>: whether a new spawn may start now. Body <c>{team, member, dry_run}</c>; the answer is
/// <c>{admitted, next_at, triggers}</c>. It paces spawns and protects nothing, so it asks for no proof of the caller; the spawn's own
/// owner proof still applies. An admitted spawn is recorded unless <c>dry_run</c>.
/// </summary>
internal sealed class LoadAdmitRoute(ILoadWatch load)
{
    public const string Path = "POST /v1/load/admit-spawn";

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private static RouteResponse Json(int status, JsonObject body) => new(status, NoHeaders, JsonText.ToUtf8(body, 0));

    public Task<RouteResponse> HandleAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool dryRun;
        try
        {
            var body = request.Body.IsEmpty ? new JsonObject() : JsonText.Parse(request.Body.ToArray()) is { IsMap: true } o ? o.AsObject() : throw new JsonParseException("not an object");
            dryRun = body.Get("dry_run") is { IsTruthy: true };
        }
        catch (JsonParseException)
        {
            return Task.FromResult(Json(400, new JsonObject { ["error"] = new JsonObject { ["code"] = "bad_request", ["message"] = "body must be a JSON object" } }));
        }

        var admission = load.AdmitSpawn(dryRun);
        var triggers = new JsonArray();
        foreach (var trigger in admission.Triggers)
        {
            triggers.Add(trigger);
        }

        return Task.FromResult(Json(200, new JsonObject
        {
            ["admitted"] = admission.Admitted,
            ["next_at"] = admission.NextAt is { } next ? next.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : JsonValue.Null,
            ["triggers"] = triggers,
        }));
    }
}
