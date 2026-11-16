using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Daemon;

/// <summary>
/// <c>GET /v1/peers/pending?dir=&lt;hierarchy dir&gt;&amp;session=&lt;id&gt;[&amp;count=1]</c>: the rows pending for a session over the peer log, answered by the
/// daemon's own warm read of it. A <c>dir</c> the daemon does not serve is a 404 and nothing is opened for it.
/// </summary>
internal static class PeerPendingRoute
{
    /// <summary>The route key.</summary>
    internal const string Key = "GET /v1/peers/pending";

    /// <summary>The handler: <paramref name="served"/> says whether the daemon serves a hierarchy dir, <paramref name="pendingFor"/> gives the pending rows of a session.</summary>
    internal static Func<RouteRequest, CancellationToken, Task<RouteResponse>> Handler(
        Func<string, bool> served,
        Func<int?, string, bool> mayRead,
        Func<JsonValue, IReadOnlyList<JsonValue>> pendingFor)
    {
        ArgumentNullException.ThrowIfNull(served);
        ArgumentNullException.ThrowIfNull(mayRead);
        ArgumentNullException.ThrowIfNull(pendingFor);
        return (request, _) =>
        {
            var query = QueryString.Parse(request.Query);
            var dir = query.GetValueOrDefault("dir");
            if (dir is null || !dir.StartsWith('/'))
            {
                return Task.FromResult(ViewService.BadRequest("dir must be an absolute hierarchy dir"));
            }

            if (query.GetValueOrDefault("session") is not { Length: > 0 } session)
            {
                return Task.FromResult(ViewService.BadRequest("session is required"));
            }

            if (!served(dir))
            {
                return Task.FromResult(ViewService.Error(404, "not_found", $"no peer log is served for {dir}"));
            }

            if (!mayRead(request.PeerPid, session))
            {
                return Task.FromResult(ViewService.Error(403, "forbidden", "the caller is neither the user nor the session asked about"));
            }

            var rows = pendingFor(JsonValue.FromString(session));
            if (query.GetValueOrDefault("count") == "1")
            {
                return Task.FromResult(ViewService.Json(200, new JsonObject { ["count"] = (double)rows.Count }));
            }

            JsonArray array = [.. rows];
            return Task.FromResult(ViewService.Json(200, new JsonObject { ["rows"] = array }));
        };
    }
}
