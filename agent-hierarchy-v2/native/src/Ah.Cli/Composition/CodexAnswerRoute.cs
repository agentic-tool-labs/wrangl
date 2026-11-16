using System.Globalization;
using Ah.Abstractions;
using Ah.Codex;
using Ah.Config;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Cli.Composition;

/// <summary>
/// <c>POST /v1/codex/answer</c>: replies to a server request the Codex adapter holds for a member. The caller is proven here, from its
/// process tree, and only the user or the member's team Orchestrator may answer. The Orchestrator's granting choices pass only inside
/// the bounds below; outside them the reply is <c>needs-user</c> and nothing is sent to the member.
/// </summary>
internal sealed class CodexAnswerRoute(ServiceGraph graph, ICodexRelay relay, Func<RouteRequest, CallerPrincipal>? principalOf = null)
{
    public const string Path = "POST /v1/codex/answer";
    public const string GrantLogName = "codex-grants.jsonl";

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private static RouteResponse Json(int status, JsonObject body) => new(status, NoHeaders, JsonText.ToUtf8(body, 0));

    private static RouteResponse Error(int status, string code, string message) =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    private static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } t ? t.AsString() : null;

    public async Task<RouteResponse> HandleAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonObject body;
        try
        {
            body = request.Body.IsEmpty ? new JsonObject() : JsonText.Parse(request.Body.ToArray()) is { IsMap: true } o ? o.AsObject() : throw new JsonParseException("not an object");
        }
        catch (JsonParseException)
        {
            return Error(400, "bad_request", "body must be a JSON object");
        }

        if (Text(body, "member") is not { Length: > 0 } member || Text(body, "team") is not { Length: > 0 } team || Text(body, "request_id") is not { Length: > 0 } requestId)
        {
            return Error(400, "bad_request", "member, team and request_id are required");
        }

        if (team.Contains('.', StringComparison.Ordinal) || member.Contains('.', StringComparison.Ordinal))
        {
            // the adapter keys members as team.name and never serves a team or member whose name has a dot, so none can be meant
            return Error(400, "bad_request", "team and member names do not contain '.'");
        }

        var answers = new List<KeyValuePair<string, string>>();
        if (body.Get("answers") is { IsMap: true } given)
        {
            var map = given.AsObject();
            foreach (var qid in map.Keys)
            {
                if (map.Get(qid) is not { IsText: true } value)
                {
                    return Error(400, "bad_request", $"the answer to {qid} must be text");
                }

                answers.Add(new(qid, value.AsString()));
            }
        }

        var verdict = MeshModule.OrchestratorOrUser(principalOf?.Invoke(request) ?? graph.Mailbox.PrincipalOf(request.PeerPid), Text(body, "session_id"), team);
        if (verdict.By is not { } by)
        {
            return Error(403, verdict.Code ?? MeshModule.NotTeamOwner, verdict.Message ?? "refused");
        }

        var key = $"{team}.{member}";
        var choice = Text(body, "choice");
        var matches = relay.Pending().Where(p => p.MemberKey == key && p.RequestId == requestId).ToList();
        if (matches.Count > 1)
        {
            return Json(200, new JsonObject { ["status"] = "needs-user", ["reason"] = "both a number and a string request carry this id, so ah will not guess which one is meant" });
        }

        var pending = matches.FirstOrDefault();
        var orchestratorGrant = by == "orchestrator" && pending is not null && IsGranting(pending, choice);
        if (orchestratorGrant)
        {
            var why = Bounds(graph.Config.CodexOrchestratorApprovals(), pending!, choice, WorkRootOf(graph, relay.MemberOf(key)));
            if (why is not null)
            {
                return Json(200, new JsonObject { ["status"] = "needs-user", ["reason"] = why });
            }
        }

        var row = pending is { Kind: not "question" } ? GrantRow(team, member, pending, choice, by) : null;
        if (orchestratorGrant)
        {
            // An Orchestrator grant never exists without its record: the row is written before anything is sent.
            if (AppendRow(row!) is { } failure)
            {
                return Json(200, new JsonObject { ["status"] = "needs-user", ["reason"] = "the grant could not be logged, so nothing was granted", ["log_error"] = failure });
            }
        }

        var result = await relay.AnswerAsync(key, requestId, choice, answers, Text(body, "text"), pending?.IdKey).ConfigureAwait(false);
        if (result.Status == "ambiguous")
        {
            return Json(200, new JsonObject { ["status"] = "needs-user", ["reason"] = result.Detail ?? "more than one request carries this id, so ah will not guess which one is meant" });
        }

        var reply = new JsonObject { ["status"] = result.Status };
        if (result.Detail is not null)
        {
            reply["detail"] = result.Detail;
        }

        if (orchestratorGrant && result.Status != "answered")
        {
            var undone = row!.Clone();
            undone["not_sent"] = result.Status;
            _ = AppendRow(undone);
        }

        if (result.Status == "answered" && row is not null)
        {
            if (!orchestratorGrant && AppendRow(row) is { } failure)
            {
                reply["log_error"] = failure;
            }

            reply["grant"] = row;
        }

        return Json(200, reply);
    }

    /// <summary>
    /// The member's work-tree record: the <c>work_root</c> the worktree gate keeps for it in the team's grants document, or null when
    /// there is none or it cannot be read.
    /// </summary>
    internal static string? WorkRootOf(ServiceGraph graph, CodexMember? member)
    {
        if (member is null)
        {
            return null;
        }

        try
        {
            var home = RosterModule.TeamFileHome(member.TeamFile);
            var dir = home is { Length: > 0 } ? home : System.IO.Path.GetDirectoryName(member.TeamFile) ?? string.Empty;
            var view = ConfigModule.CreateWorktreeGrants(graph.Io.Files, graph.Environment).Read(dir, member.Team);
            return view.Members.TryGetValue(member.Name, out var record) ? record.WorkRoot : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Every choice that is not a plain refusal gives the member something: it is a grant.</summary>
    private static bool IsGranting(CodexPending pending, string? choice) =>
        pending.Kind != "question" && choice is not ("decline" or "cancel");

    /// <summary>Why the Orchestrator may not choose this on its own, or null when it may.</summary>
    internal static string? Bounds(bool approvalsOn, CodexPending pending, string? choice, string? workRoot)
    {
        if (!approvalsOn)
        {
            return "the user has not approved Orchestrator grants (codex_orchestrator_approvals is off)";
        }

        if (choice != "accept" || pending.Kind is not ("command-approval" or "file-change-approval"))
        {
            return "only a one-time accept of a command or file change may be chosen by the Orchestrator";
        }

        if (string.IsNullOrEmpty(workRoot))
        {
            return "the member's work tree is not known";
        }

        var root = workRoot;
        if (pending.Kind == "command-approval")
        {
            return pending.Cwd is { Length: > 0 } cwd && Inside(cwd, root) ? null : "the command's working directory is not inside the member's work tree";
        }

        if (pending.GrantRoot is not null)
        {
            return "the request asks for write access under a root for the rest of the session";
        }

        if (pending.Paths is not { Count: > 0 } paths)
        {
            return "ah has no record of which files this change touches";
        }

        return paths.All(p => Inside(p, root)) ? null : "a changed file is outside the member's work tree";
    }

    /// <summary>Whether <paramref name="path"/>, with symlinks resolved, is <paramref name="root"/> or below it. A path with a <c>..</c> segment is never inside.</summary>
    internal static bool Inside(string path, string root)
    {
        if (!path.StartsWith('/') || path.Split('/').Contains("..") || root.Split('/').Contains(".."))
        {
            return false;
        }

        var real = RealPath(path);
        var top = RealPath(root).TrimEnd('/');
        return real == top || real.StartsWith(top + "/", StringComparison.Ordinal);
    }

    private static string RealPath(string path)
    {
        var current = "/";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            try
            {
                if (new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = target.FullName;
                }
            }
            catch (IOException)
            {
                // a path that does not exist yet has no link to follow
            }
        }

        return current;
    }

    private JsonObject GrantRow(string team, string member, CodexPending pending, string? choice, string by)
    {
        var row = new JsonObject
        {
            ["at"] = graph.Io.Clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["team"] = team,
            ["member"] = member,
            ["request_id"] = pending.RequestId,
            ["kind"] = pending.Kind,
            ["cwd"] = pending.Cwd is null ? JsonValue.Null : pending.Cwd,
            ["choice"] = choice is null ? JsonValue.Null : choice,
            ["by"] = by,
            ["bounds"] = by == "orchestrator" ? "worktree" : JsonValue.Null,
        };
        if (pending.Paths is { } paths)
        {
            var list = new JsonArray();
            foreach (var p in paths)
            {
                list.Add(p);
            }

            row["paths"] = list;
        }

        if (pending.Verbatim is not null && JsonText.Parse(pending.Verbatim) is { IsMap: true } shown && shown.Member("command") is { IsText: true } command)
        {
            row["command"] = command;
        }

        return row;
    }

    /// <summary>Appends the row to the grant log (mode 0600); the failure's message when it could not be written.</summary>
    private string? AppendRow(JsonObject row)
    {
        var file = System.IO.Path.Combine(graph.Environment.AhHome, GrantLogName);
        try
        {
            Directory.CreateDirectory(graph.Environment.AhHome);
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(file, new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, UnixCreateMode = Private }))
            {
                // a log that already existed with wider permissions is narrowed before anything is written to it
                File.SetUnixFileMode(stream.SafeFileHandle, Private);
                var bytes = System.Text.Encoding.UTF8.GetBytes(JsonText.Stringify(row, 0) + "\n");
                stream.Write(bytes, 0, bytes.Length);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}
