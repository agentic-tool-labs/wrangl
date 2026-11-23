using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;

namespace Ah.Cli.Composition;

/// <summary>
/// The <c>POST /v1/qa/*</c> routes of the QA queue: submit, next, done, release, withdraw, status and list. Each body is a JSON object. A call
/// that changes a job needs a session the peer's process tree proves; <c>status</c> and <c>list</c> accept the user too, and
/// <c>release</c> also accepts the user and the orchestrator of the claimer's team. Every refusal is
/// <c>{error:{code,message}}</c>.
/// </summary>
internal sealed class QaRoutes(IHierarchyQa qa, Func<int?, CallerPrincipal> principalOf, Func<string, string?> teamFileOf, IFileReader files)
{
    public const string Unproven = "qa-caller-unproven";

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    public void Register(IRouteRegistry routes)
    {
        routes.Add("POST /v1/qa/submit", (request, ct) => Handle(request, Submit, ct));
        routes.Add("POST /v1/qa/next", (request, ct) => Handle(request, Next, ct));
        routes.Add("POST /v1/qa/done", (request, ct) => Handle(request, Done, ct));
        routes.Add("POST /v1/qa/release", (request, ct) => Handle(request, Release, ct));
        routes.Add("POST /v1/qa/withdraw", (request, ct) => Handle(request, Withdraw, ct));
        routes.Add("POST /v1/qa/status", (request, ct) => Handle(request, Status, ct));
        routes.Add("POST /v1/qa/list", (request, ct) => Handle(request, List, ct));
    }

    private static RouteResponse Json(int status, JsonObject body) => new(status, NoHeaders, JsonText.ToUtf8(body, 0));

    private static RouteResponse Error(int status, string code, string message) =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    private static int StatusOf(string code) => code switch
    {
        Unproven or "not-claimer" or "not-allowed" => 403,
        "qa-unknown-entry" => 404,
        "qa-not-claimed" or "qa-claimed" => 409,
        "qa-store-busy" => 503,
        _ => 400,
    };

    private static JsonValue Number(long? value) => value is { } v ? JsonValue.FromNumber(v) : JsonValue.Null;

    private static JsonValue Text(string? value) => value is null ? JsonValue.Null : value;

    private static JsonObject JobJson(QaJob job)
    {
        var requesters = new JsonArray();
        foreach (var requester in job.Requesters)
        {
            requesters.Add(requester);
        }

        return new JsonObject
        {
            ["id"] = job.Id,
            ["sha"] = job.Sha,
            ["base"] = Text(job.Base),
            ["set"] = job.Set,
            ["repo"] = job.Repo,
            ["fresh"] = job.Fresh,
            ["priority"] = job.Priority,
            ["requesters"] = requesters,
            ["team"] = Text(job.Team),
            ["created"] = Number(job.Created),
            ["state"] = job.State,
            ["claimed_by"] = Text(job.ClaimedBy),
            ["claimed_at"] = Number(job.ClaimedAt),
            ["result_path"] = Text(job.ResultPath),
            ["verdict"] = Text(job.Verdict),
            ["ended_at"] = Number(job.EndedAt),
            ["reason"] = Text(job.Reason),
        };
    }

    private static RouteResponse Reply(QaReply reply) =>
        reply.Code is { } code
            ? Error(StatusOf(code), code, reply.Message ?? code)
            : Json(200, new JsonObject { ["entry"] = reply.Job is { } job ? JobJson(job) : JsonValue.Null, ["reason"] = Text(reply.Reason) });

    private static string? Str(JsonObject body, string key) => body.Get(key) is { IsText: true } v ? v.AsString() : null;

    private static Task<RouteResponse> Handle(RouteRequest request, Func<RouteRequest, JsonObject, RouteResponse> run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        JsonObject body;
        try
        {
            body = request.Body.IsEmpty ? new JsonObject() : JsonText.Parse(request.Body.ToArray()) is { IsMap: true } o ? o.AsObject() : throw new JsonParseException("not an object");
        }
        catch (JsonParseException)
        {
            return Task.FromResult(Error(400, "bad_request", "body must be a JSON object"));
        }

        return Task.FromResult(run(request, body));
    }

    /// <summary>The proven session behind a call that changes a job: its principal and primary address, or the refusal.</summary>
    private (CallerPrincipal Principal, SessionChoice Choice, string? Address, RouteResponse? Refused) Session(RouteRequest request, JsonObject body)
    {
        var principal = principalOf(request.PeerPid);
        var choice = MeshModule.ChooseSession(principal, Str(body, "session_id"), write: true);
        var address = principal.Kind == PrincipalKind.Session && choice.Refusal is null ? choice.Addresses.Count > 0 ? choice.Addresses[0] : null : null;
        return (principal, choice, address, address is null ? Error(403, Unproven, "the daemon cannot prove which session is calling") : null);
    }

    private RouteResponse Submit(RouteRequest request, JsonObject body)
    {
        var (_, choice, requester, refused) = Session(request, body);
        if (refused is not null)
        {
            return refused;
        }

        var repo = Str(body, "repo");
        var sha = Str(body, "sha");
        var set = Str(body, "set");
        if (repo is null || sha is null || set is null)
        {
            return Error(400, "qa-bad-request", "repo, sha and set are required");
        }

        if (!RepoAllowed(choice, repo))
        {
            return Error(400, "qa-bad-request", "repo must be an absolute path to the team's repository or one of its worktrees");
        }

        return Reply(qa.Submit(repo, sha, Str(body, "base"), set, body.Get("fresh") is { IsTruthy: true }, Str(body, "priority"), requester!));
    }

    private RouteResponse Next(RouteRequest request, JsonObject body)
    {
        var (_, _, claimer, refused) = Session(request, body);
        if (refused is not null)
        {
            return refused;
        }

        var maxSet = Str(body, "max_set");
        return maxSet is not (null or "full" or "other")
            ? Error(400, "qa-bad-request", "max_set must be full or other")
            : Reply(qa.Claim(claimer!, maxSet != "other"));
    }

    private RouteResponse Done(RouteRequest request, JsonObject body)
    {
        var (_, _, caller, refused) = Session(request, body);
        if (refused is not null)
        {
            return refused;
        }

        return Str(body, "id") is not { } id
            ? Error(400, "qa-bad-request", "id is required")
            : Reply(qa.Done(id, caller!, Str(body, "result"), Str(body, "error")));
    }

    private RouteResponse Release(RouteRequest request, JsonObject body)
    {
        var principal = principalOf(request.PeerPid);
        if (principal.Kind is not (PrincipalKind.User or PrincipalKind.Session))
        {
            return Error(403, Unproven, "the daemon cannot prove who is calling");
        }

        if (Str(body, "id") is not { } id)
        {
            return Error(400, "qa-bad-request", "id is required");
        }

        var choice = MeshModule.ChooseSession(principal, Str(body, "session_id"), write: true);
        if (choice.Refusal is not null)
        {
            return Error(403, Unproven, "the daemon cannot prove which session is calling");
        }

        bool May(string claimer)
        {
            if (principal.Kind == PrincipalKind.User || choice.Addresses.Contains(claimer, StringComparer.Ordinal))
            {
                return true;
            }

            var at = claimer.IndexOf('@', StringComparison.Ordinal);
            return at > 0 && MeshModule.OrchestratorOrUser(principal, Str(body, "session_id"), claimer[(at + 1)..]).By is not null;
        }

        return Reply(qa.Release(id, May));
    }

    /// <summary>Whoever may submit for the entry's repository may withdraw it (ah2-493).</summary>
    private RouteResponse Withdraw(RouteRequest request, JsonObject body)
    {
        var (_, choice, _, refused) = Session(request, body);
        if (refused is not null)
        {
            return refused;
        }

        return Str(body, "id") is not { } id
            ? Error(400, "qa-bad-request", "id is required")
            : Reply(qa.Withdraw(id, Str(body, "reason"), repo => RepoAllowed(choice, repo)));
    }

    private RouteResponse Status(RouteRequest request, JsonObject body)
    {
        if (principalOf(request.PeerPid).Kind is not (PrincipalKind.User or PrincipalKind.Session))
        {
            return Error(403, Unproven, "the daemon cannot prove who is calling");
        }

        return Str(body, "id") is { } id ? Reply(qa.Find(id)) : ListJson(qa.List(false));
    }

    private RouteResponse List(RouteRequest request, JsonObject body) =>
        principalOf(request.PeerPid).Kind is not (PrincipalKind.User or PrincipalKind.Session)
            ? Error(403, Unproven, "the daemon cannot prove who is calling")
            : ListJson(qa.List(body.Get("all") is { IsTruthy: true }));

    private static RouteResponse ListJson(IReadOnlyList<QaJob> jobs)
    {
        var entries = new JsonArray();
        foreach (var job in jobs)
        {
            entries.Add(JobJson(job));
        }

        return Json(200, new JsonObject { ["entries"] = entries });
    }

    /// <summary>
    /// Whether <paramref name="repo"/> is, once symbolic links are resolved, the repository that holds the team file of the calling
    /// session or one of that repository's git worktrees, as git records them in <c>.git/worktrees/*/gitdir</c>.
    /// </summary>
    private bool RepoAllowed(SessionChoice choice, string repo)
    {
        if (!Path.IsPathRooted(repo) || choice.Session is not { } session || teamFileOf(session.SessionId) is not { } teamFile)
        {
            return false;
        }

        var teams = Path.GetDirectoryName(teamFile);
        var root = teams is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(teams)));
        if (root is null || !teams!.EndsWith(Path.Combine(".claude", "ah2", "teams"), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var real = files.RealPath(repo);
            var realRoot = files.RealPath(root);
            if (real == realRoot)
            {
                return true;
            }

            var worktrees = Path.Combine(realRoot, ".git", "worktrees");
            foreach (var name in files.Exists(worktrees) ? files.List(worktrees) : [])
            {
                var gitdir = Path.Combine(worktrees, Path.GetFileName(name), "gitdir");
                if (files.Exists(gitdir) && Path.GetDirectoryName(files.ReadText(gitdir).Trim()) is { Length: > 0 } tree && files.Exists(tree) && files.RealPath(tree) == real)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
