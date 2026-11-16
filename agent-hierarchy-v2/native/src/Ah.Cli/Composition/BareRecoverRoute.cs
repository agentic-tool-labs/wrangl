using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Composition;

/// <summary>
/// <c>POST /v1/bare/recover</c>: the owner's <c>recover show|hold|release|now</c> for one bare member. The sender is proven here, from its
/// process tree: only the user or the team's Orchestrator is served, so a member (a bare session runs with no gates) cannot hold or
/// hurry its own recovery, or read it.
/// </summary>
internal sealed class BareRecoverRoute(ServiceGraph graph, Func<BareHost?> host, Func<RouteRequest, CallerPrincipal>? principalOf = null)
{
    public const string Path = "POST /v1/bare/recover";

    private static readonly string[] Ops = ["show", "hold", "release", "now", "enable", "disable", "status"];

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private static RouteResponse Json(int status, JsonObject body) => new(status, NoHeaders, JsonText.ToUtf8(body, 0));

    private static RouteResponse Error(int status, string code, string message) =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    private static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } t ? t.AsString() : null;

    public Task<RouteResponse> HandleAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Handle(request));
    }

    private RouteResponse Handle(RouteRequest request)
    {
        JsonObject body;
        try
        {
            body = request.Body.IsEmpty ? new JsonObject() : JsonText.Parse(request.Body.ToArray()) is { IsMap: true } o ? o.AsObject() : throw new JsonParseException("not an object");
        }
        catch (JsonParseException)
        {
            return Error(400, "bad_request", "body must be a JSON object");
        }

        if (Text(body, "hierarchy_dir") is not { Length: > 0 } dir || Text(body, "member") is not { Length: > 0 } member || Text(body, "op") is not { } op || !Ops.Contains(op))
        {
            return Error(400, "bad_request", "hierarchy_dir, member and op (show, hold, release, now, enable, disable or status) are required");
        }

        if (Text(body, "team") is not { Length: > 0 } team)
        {
            return Error(400, "bad_request", "the daemon watches named teams only; pass --team <name>");
        }

        var principal = principalOf?.Invoke(request) ?? graph.Mailbox.PrincipalOf(request.PeerPid);
        var verdict = MeshModule.OrchestratorOrUser(principal, Text(body, "session_id"), team);
        if (op != "status" && verdict.By is null)
        {
            return Error(403, verdict.Code ?? MeshModule.NotTeamOwner, verdict.Message ?? "refused");
        }

        if (op == "enable" && verdict.By != "user")
        {
            return Error(403, MeshModule.NotTeamOwner, "recover enable is for the user only");
        }

        if (host() is not { } bare)
        {
            return Error(503, "unavailable", "the bare watch is not running");
        }

        if (op != "status" && verdict.By == "orchestrator" && (bare.Team(dir, team) is not { } watched || !principal.Sessions.Any(s => s.SessionId == watched.OrchestratorSession)))
        {
            return Error(403, MeshModule.NotTeamOwner, $"the caller is not the orchestrating session of team {team} in {dir}");
        }

        var owner = verdict.By == "user"
            || (verdict.By == "orchestrator" && bare.Team(dir, team) is { } own && principal.Sessions.Any(s => s.SessionId == own.OrchestratorSession));
        var by = owner ? verdict.By! : "member";
        var session = by == "orchestrator" ? bare.Team(dir, team)?.OrchestratorSession : null;
        return bare.Ask(dir, team, member, op, by, session, out var problem) is { } answer
            ? Json(200, answer)
            : Error(404, "not_found", problem);
    }
}
