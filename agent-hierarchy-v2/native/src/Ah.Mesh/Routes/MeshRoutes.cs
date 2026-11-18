using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>What the mesh routes of the daemon stand on; the composition root builds each from the one set of modules the daemon runs.</summary>
/// <param name="Contract">The participation contract's renderings.</param>
/// <param name="Roles">The behavioural contract of the built-in chain roles.</param>
/// <param name="Lookups">The team and join lookups of a participant that has not registered.</param>
/// <param name="Sender">The send under the mesh policy.</param>
/// <param name="Joins">Join requests and their decisions.</param>
/// <param name="Links">Link commands, for the daemon's own auto-grant of an easy link request.</param>
/// <param name="Mailbox">Gives the team lookup of a request's context.</param>
/// <param name="Checker">Validates the caller context a request carries.</param>
/// <param name="Log">The daemon log; a send slower than <paramref name="SlowSendMs"/> writes one "send slow" line with its laps.</param>
/// <param name="SlowSendMs">The route time past which a send is logged.</param>
public sealed record MeshRouteDependencies(
    IContractCore Contract,
    IContractRoles Roles,
    IContractLookups Lookups,
    IMeshSender Sender,
    IJoinCommands Joins,
    ILinkCommands Links,
    IMailbox Mailbox,
    IRequestContextChecker Checker,
    Action<string>? Log = null,
    long SlowSendMs = 2000);

/// <summary>
/// The mesh routes: the participation contract and what a participant reads before it registers, <c>POST /v1/send</c>, and the
/// join request and its poll. Each is a thin adapter from the host's request to the module that owns the rule.
/// </summary>
internal static partial class MeshRoutes
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> MarkdownHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = "text/markdown; charset=utf-8" };

    private static readonly IReadOnlyDictionary<string, string> RetryHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["Retry-After"] = "1" };

    private static readonly string[] ContextKeys = ["cwd", "hierarchy_dir", "main_hierarchy_dir", "messaging_socket_dir"];

    private static readonly Lazy<string[]> SendKeys = new(() =>
        JsonText.Parse(ContractData.DocumentJson).AsObject()["enums"].AsObject()["send_keys"].AsArray().Select(k => k.AsString()).ToArray());

    /// <summary>The HTTP status of a refusal code; a code this table does not name answers 400.</summary>
    private static int StatusOf(string code) => code switch
    {
        "bad_request" or "bad_address" => 400,
        "unknown_address" or "not_found" or "unknown_team" or "unknown_request" => 404,
        "not_admitted" or "from_mismatch" or "outside_team" or "not_linked" or "link_off_task" or "link_needs_ref" or "link_exhausted" or "broadcast_orchestrator_only" or "orchestrator_only" or "removed_member" or "team_gone" or "team_mismatch" or "team_unreadable" or "team_unresolved" => 403,
        "ambiguous_address" or "no_orchestrator" or "not_extendable" or "already_decided" => 409,
        "draining" or "mirror_failed" => 503,
        "busy" => 423,
        "write_failed" => 500,
        "join_cooldown" => 429,
        _ => 400,
    };

    private static RouteResponse Json(int status, JsonValue value) =>
        new(status, NoHeaders, JsonText.ToUtf8(value, 0));

    private static RouteResponse Error(
        int status, string code, string message, string? field = null, IReadOnlyList<string>? candidates = null, JsonObject? extra = null, bool retry = false)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (field is not null)
        {
            error["field"] = field;
        }

        if (candidates is not null)
        {
            var list = new JsonArray();
            foreach (var c in candidates)
            {
                list.Add(c);
            }

            error["candidates"] = list;
        }

        if (extra is not null)
        {
            foreach (var key in extra.Keys)
            {
                error[key] = extra[key];
            }
        }

        return new RouteResponse(status, retry ? RetryHeaders : NoHeaders, JsonText.ToUtf8(new JsonObject { ["error"] = error }, 0));
    }

    private static RouteResponse Refusal(string code, string message, IReadOnlyList<string>? candidates = null, JsonObject? extra = null) =>
        Error(StatusOf(code), code, message, null, candidates, extra, retry: code == "busy");

    /// <summary>The query string as <c>URLSearchParams</c> reads it: <c>+</c> is a space, escapes are decoded, the last repeat wins.</summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var name = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? string.Empty : pair[(eq + 1)..];
            all[Uri.UnescapeDataString(name.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return all;
    }

    /// <summary>The JSON object a POST body is: an empty body is <c>{}</c>; anything else must be an object.</summary>
    private static JsonObject? ParseBody(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
        {
            return new JsonObject();
        }

        try
        {
            return JsonText.Parse(body.ToArray()) is { IsMap: true } o ? o.AsObject() : null;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }

    private static JsonObject ContextObject(Dictionary<string, string> query)
    {
        var o = new JsonObject();
        foreach (var key in ContextKeys)
        {
            if (query.TryGetValue(key, out var v))
            {
                o[key] = v;
            }
        }

        return o;
    }

    private static readonly RequestContext NoContext = new(string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// The caller context of <paramref name="source"/>, validated; a refusal is the answer to give. <paramref name="optional"/> makes
    /// a request that names neither <c>cwd</c> nor <c>hierarchy_dir</c> carry no context (the contract lookups).
    /// </summary>
    private static (RequestContext? Context, RouteResponse? Refused) ContextOf(JsonObject source, IRequestContextChecker checker, bool optional)
    {
        if (optional && source.Get("cwd") is null && source.Get("hierarchy_dir") is null)
        {
            return (NoContext, null);
        }

        var context = Mailbox.ContextOf(source);
        return checker.Check(context) is { } problem
            ? (null, Error(400, "bad_request", problem.Message, problem.Field))
            : (context, null);
    }

    private static string? Text(JsonObject o, string key) => o.Get(key) is { IsText: true } t ? t.AsString() : null;

    internal static void Register(IRouteRegistry routes, MeshRouteDependencies d)
    {
        RegisterContract(routes, d.Contract, d.Roles);
        routes.Add("GET /v1/contract/teams", (request, ct) => Teams(request, d, ct));
        routes.Add("GET /v1/contract/join", (request, ct) => JoinValues(request, d, ct));
        routes.Add("POST /v1/send", (request, ct) => Send(request, d, ct));
        routes.Add("POST /v1/join", (request, ct) => JoinOpen(request, d, ct));
        routes.Add("GET /v1/join/{id}", (request, ct) => JoinPoll(request, d, ct));
    }

    internal static void RegisterContract(IRouteRegistry routes, IContractCore contract, IContractRoles roles)
    {
        routes.Add("GET /v1/contract", (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RouteResponse(200, NoHeaders, Encoding.UTF8.GetBytes(contract.RenderJson())));
        });
        routes.Add("GET /v1/contract/guide", (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RouteResponse(200, MarkdownHeaders, Encoding.UTF8.GetBytes(contract.RenderGuide())));
        });
        routes.Add("GET /v1/contract/role", (request, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var role = ParseQuery(request.Query).TryGetValue("role", out var value) ? value : null;
            if (roles.Role(role) is { } found)
            {
                return Task.FromResult(Json(200, found));
            }

            var shown = role is null ? "null" : JsonText.Stringify(role, 0);
            return Task.FromResult(Error(404, "not_found", $"no behavioural contract for role {shown}"));
        });
    }

    private static Task<RouteResponse> Teams(RouteRequest request, MeshRouteDependencies d, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (context, refused) = ContextOf(ContextObject(ParseQuery(request.Query)), d.Checker, optional: true);
        return Task.FromResult(refused ?? Json(200, d.Lookups.Teams(context!)));
    }

    private static Task<RouteResponse> JoinValues(RouteRequest request, MeshRouteDependencies d, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = ParseQuery(request.Query);
        var address = query.GetValueOrDefault("address");
        if (d.Mailbox.SplitAddress(address) is null)
        {
            return Task.FromResult(Error(400, "bad_address", "address must be name@team"));
        }

        var (context, refused) = ContextOf(ContextObject(query), d.Checker, optional: true);
        if (refused is not null)
        {
            return Task.FromResult(refused);
        }

        return Task.FromResult(
            d.Lookups.Join(address!, context!) is { } values
                ? Json(200, values)
                : Error(404, "not_admitted", $"{address} is not an admitted custom member of a team the daemon can see"));
    }

    private static async Task<RouteResponse> Send(RouteRequest request, MeshRouteDependencies d, CancellationToken ct)
    {
        var phases = new SendPhases();
        try
        {
            return await SendTimed(request, d, phases, ct).ConfigureAwait(false);
        }
        finally
        {
            if (d.Log is { } log && phases.TotalMs > d.SlowSendMs)
            {
                log($"send slow: {phases.TotalMs.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms: {phases.Text}");
            }
        }
    }

    private static async Task<RouteResponse> SendTimed(RouteRequest request, MeshRouteDependencies d, SendPhases phases, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ParseBody(request.Body) is not { } body)
        {
            return Error(400, "bad_request", "body must be a JSON object");
        }

        var (context, refused) = ContextOf(body, d.Checker, optional: false);
        phases.Lap("context");
        if (refused is not null)
        {
            return refused;
        }

        var req = new JsonObject();
        foreach (var key in SendKeys.Value)
        {
            if (body.Get(key) is { } v)
            {
                req[key] = v;
            }
        }

        // The daemon's own events carry this sender in process and never arrive here, so no caller of the route may claim it.
        if (Text(req, "from") == "ahd")
        {
            return Refusal("from_mismatch", "--from ahd is reserved for the daemon's own events");
        }

        var (principal, choice, notCaller) = CallerOf(request, d.Mailbox, Text(req, "session_id"), write: true);
        phases.Lap("caller");
        if (notCaller is not null)
        {
            return notCaller;
        }

        // the sender is the session the peer's process tree proves; the body's session_id only chose among those
        var from = Text(req, "from");
        if (principal.Kind == PrincipalKind.User)
        {
            // ah2-150 D4: the admin may send as an address its own named session holds; the session and its addresses are the
            // process tree's registrations (CallerIdentity.Choose), the body's session_id only names one of them
            var asOwnSession = from is not null && from != "user" && Text(req, "session_id") is { Length: > 0 }
                && choice.Session is not null && choice.Addresses.Contains(from, StringComparer.Ordinal);
            if (from is not null && from != "user" && !asOwnSession)
            {
                return Refusal("from_mismatch", $"--from {from} is not the user's address; a caller with no session sends as user");
            }

            if (asOwnSession)
            {
                req["session_id"] = choice.Session!.SessionId;
            }
            else
            {
                req.Remove("session_id");
            }
        }
        else
        {
            if (from is not null && !choice.Addresses.Contains(from, StringComparer.Ordinal))
            {
                return Refusal("from_mismatch", $"--from {from} is not one of this session's proven addresses ({string.Join(", ", choice.Addresses)})");
            }

            req["session_id"] = choice.Session!.SessionId;
            if (from is null && choice.Addresses.Count == 1)
            {
                req["from"] = choice.Addresses[0];
            }
        }

        req["cwd"] = context!.Cwd;
        req["hierarchy_dir"] = context.HierarchyDirectory;
        if (context.MainHierarchyDirectory.Length > 0)
        {
            req["main_hierarchy_dir"] = context.MainHierarchyDirectory;
        }

        if (context.SocketDirectory.Length > 0)
        {
            req["messaging_socket_dir"] = context.SocketDirectory;
        }

        phases.Lap("prepare");
        var sent = await Task.Run(() =>
        {
            var outcome = d.Sender.SendMarked(req, phases.Lap);
            phases.Lap("send");
            return outcome;
        }, ct).ConfigureAwait(false);
        if (sent.Failure is { } failure)
        {
            return Refusal(failure.Code, failure.Message, failure.Candidates);
        }

        if (Text(req, "event") == "link-request")
        {
            await GrantIfEasyAsync(req, context, d, ct).ConfigureAwait(false);
            phases.Lap("grant");
        }

        return Json(200, sent.Value!);
    }

    /// <summary>
    /// A <c>link-request</c> event that landed is granted at once when it qualifies (no Orchestrator turn). A failure here never
    /// fails the send, which has already been delivered.
    /// </summary>
    private static async Task GrantIfEasyAsync(JsonObject req, RequestContext context, MeshRouteDependencies d, CancellationToken ct)
    {
        try
        {
            var text = Text(req, "text") ?? "{}";
            var parsed = JsonText.Parse(text);
            var id = parsed.IsMap && parsed.AsObject().Get("request") is { IsText: true } r ? r.AsString() : null;
            var team = d.Mailbox.SplitAddress(Text(req, "to"))?.Team;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(team))
            {
                return;
            }

            await d.Links.AutoGrantAsync(
                new LinkContext(context.HierarchyDirectory, context.MainHierarchyDirectory), d.Mailbox.TeamLookup(context), team, id, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Nothing to report to: the request is already delivered, and the Orchestrator still decides it by hand.
        }
    }

    private static async Task<RouteResponse> JoinOpen(RouteRequest request, MeshRouteDependencies d, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ParseBody(request.Body) is not { } body)
        {
            return Error(400, "bad_request", "body must be a JSON object");
        }

        var (context, refused) = ContextOf(body, d.Checker, optional: true);
        if (refused is not null)
        {
            return refused;
        }

        var outcome = await d.Joins.OpenAsync(context!, body, ct).ConfigureAwait(false);
        if (outcome.Failure is { } f)
        {
            return Refusal(f.Code, f.Message, null, f.Extra);
        }

        var value = outcome.Value!;
        var existing = value.Get("existing") is { IsTruthy: true };
        var answer = new JsonObject();
        foreach (var key in value.Keys.Where(k => k != "existing"))
        {
            answer[key] = value[key];
        }

        return Json(existing ? 200 : 202, answer);
    }

    private static async Task<RouteResponse> JoinPoll(RouteRequest request, MeshRouteDependencies d, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = ParseQuery(request.Query);
        var (context, refused) = ContextOf(ContextObject(query), d.Checker, optional: true);
        if (refused is not null)
        {
            return refused;
        }

        var outcome = await d.Joins.PollAsync(context!, request.PathParameters["id"], query.GetValueOrDefault("wait"), ct).ConfigureAwait(false);
        return outcome.Failure is { } f ? Refusal(f.Code, f.Message, null, f.Extra) : Json(200, outcome.Value!);
    }
}
