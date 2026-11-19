using System.Globalization;
using System.Net;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Ui;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ah.Daemon;

/// <summary>The bridge listener could not be bound, or bound somewhere other than <c>127.0.0.1</c>.</summary>
internal sealed class UiBindException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One bridge listener: a slim host on <c>127.0.0.1</c> with its own request pipeline, its own codes and tokens, and its own
/// close signal. A session is started once and stopped once; the bridge builds a new one for the next start.
/// </summary>
internal sealed class UiSession : IDisposable
{
    /// <summary>The routes the listener serves, exactly. Everything else is 404.</summary>
    internal static readonly IReadOnlyList<string> RouteTable = ["GET /ui/", "GET /ui/{file}", "POST /ui/v1/answer", "GET /ui/v1/answer-status", "GET /ui/v1/history", "POST /ui/v1/team-close", "POST /ui/v1/team-rename", "GET /ui/v1/rules", "POST /ui/v1/rules", "GET /ui/v1/looks", "POST /ui/v1/looks", "POST /ui/v1/looks/merge", "GET /ui/v1/view", "GET /ui/v1/view/{name}", "GET /ui/v1/read-model", "GET /ui/v1/list/{*id}", "GET /ui/v1/actions", "GET /ui/v1/actions/{*id}"];

    internal const int MaxBodyBytes = 4096;

    /// <summary>The largest body the looks routes take; every other route keeps <see cref="MaxBodyBytes"/>.</summary>
    internal const int MaxLooksBytes = 1024 * 1024;

    /// <summary>The longest request line the UI listener takes: a team's poll names every slice it shows in the query, and the default 8 KB does not hold a team of about 40.</summary>
    internal const int MaxRequestLineBytes = 32 * 1024;

    private const string ViewPath = "/ui/v1/view";
    private const string ReadModelPath = "/ui/v1/read-model";

    private const string ListPath = "/ui/v1/list/";
    private const string ActionsPath = "/ui/v1/actions";
    private const string AnswerPath = "/ui/v1/answer";
    private const string AnswerStatusPath = "/ui/v1/answer-status";
    private const string HistoryPath = "/ui/v1/history";
    private const string TeamClosePath = "/ui/v1/team-close";
    private const string TeamRenamePath = "/ui/v1/team-rename";
    private const string RulesPath = "/ui/v1/rules";
    private const string LooksPath = "/ui/v1/looks";
    private const string LooksMergePath = "/ui/v1/looks/merge";

    private readonly UiBridgeOptions _o;
    private readonly UiStatic _static;
    private readonly CancellationTokenSource _close = new();
    private readonly CancellationToken _closeToken;

    /// <summary>One History list read at a time: each parses the dir's whole log, so concurrent ones would hold that many parses live.</summary>
    private readonly SemaphoreSlim _historyList = new(1, 1);
    private int _closing;
    private int _port;
    private long _lastAuthenticatedMs;
    private WebApplication? _app;

    private UiSession(UiBridgeOptions options)
    {
        _o = options;
        _closeToken = _close.Token;
        _static = new UiStatic(options.Files, options.StaticRoot);
        Started = options.Clock.UtcNow;
        Touch();
    }

    internal DateTimeOffset Started { get; }

    internal int Port => Volatile.Read(ref _port);

    /// <summary>The remembered port this listener could not take, or 0.</summary>
    internal int MovedFrom { get; set; }

    internal bool Closing => Volatile.Read(ref _closing) != 0;

    internal CancellationToken CloseToken => _closeToken;

    /// <summary>When the listener was bound or last served an API request.</summary>
    internal DateTimeOffset LastAuthenticated => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _lastAuthenticatedMs));

    /// <summary>Records activity now: the listener is bound, a lease was added, or an API request was served.</summary>
    internal void Touch() => Interlocked.Exchange(ref _lastAuthenticatedMs, _o.Clock.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Builds and starts a listener on <paramref name="port"/> (0 for any free one) and reads its port back. Throws <see cref="UiBindException"/> when it cannot.</summary>
    internal static async Task<UiSession> StartAsync(UiBridgeOptions options, int port = 0)
    {
        var session = new UiSession(options);
        // no config reload watcher: its sync() on busy disks stalled every bridge bind past the holder's lease wait
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, Args = [DaemonServer.NoConfigReload] });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, HostLifetime>();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = MaxBodyBytes;
            k.Limits.MaxRequestLineSize = MaxRequestLineBytes;
            k.Limits.MinRequestBodyDataRate = null;
            k.Limits.RequestHeadersTimeout = options.RequestHeaders;
            k.Limits.KeepAliveTimeout = options.KeepAlive;
            k.Listen(IPAddress.Loopback, port);
        });
        var app = builder.Build();
        app.Run(session.HandleAsync);
        session._app = app;
        try
        {
            await app.StartAsync().ConfigureAwait(false);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            if (addresses.Count != 1
                || !Uri.TryCreate(addresses.First(), UriKind.Absolute, out var bound)
                || bound.Scheme != "http" || bound.Host != "127.0.0.1" || bound.Port <= 0 || bound.AbsolutePath != "/")
            {
                throw new UiBindException($"the listener bound {string.Join(',', addresses)}, not one 127.0.0.1 address");
            }

            Volatile.Write(ref session._port, bound.Port);
            return session;
        }
        catch (Exception e) when (e is not UiBindException)
        {
            await session.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            session.Dispose();
            throw new UiBindException(e.Message, e);
        }
        catch (UiBindException)
        {
            await session.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            session.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _close.Dispose();
        _historyList.Dispose();
    }

    /// <summary>Marks the session closing, so every new request gets 503, and signals every held request.</summary>
    internal void BeginClose()
    {
        if (Interlocked.Exchange(ref _closing, 1) == 0)
        {
            _close.Cancel();
        }
    }

    /// <summary>Stops the host with <paramref name="deadline"/> for the requests still open, then disposes it.</summary>
    internal async Task StopAsync(TimeSpan deadline)
    {
        BeginClose();
        var app = Interlocked.Exchange(ref _app, null);
        if (app is null)
        {
            return;
        }

        using var stop = new CancellationTokenSource(deadline);
        try
        {
            await app.StopAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // requests still open are dropped with the listener
        }

        await app.DisposeAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- requests

    private enum Kind
    {
        None,
        Static,
        View,
        Manifest,
        ReadModel,
        List,
        Actions,
        Answer,
        AnswerStatus,
        History,
        TeamClose,
        TeamRename,
        Rules,
        Looks,
        LooksMerge,
    }

    private static Kind Classify(string path) => path switch
    {
        AnswerPath => Kind.Answer,
        AnswerStatusPath => Kind.AnswerStatus,
        HistoryPath => Kind.History,
        TeamClosePath => Kind.TeamClose,
        TeamRenamePath => Kind.TeamRename,
        RulesPath => Kind.Rules,
        LooksPath => Kind.Looks,
        LooksMergePath => Kind.LooksMerge,
        ViewPath or ViewPath + "/" => Kind.View,
        ReadModelPath => Kind.ReadModel,
        _ when ManifestName(path) is not null => Kind.Manifest,
        _ when path.StartsWith(ListPath, StringComparison.Ordinal) && path.Length > ListPath.Length => Kind.List,
        ActionsPath or ActionsPath + "/" => Kind.Actions,
        _ when path.StartsWith(ActionsPath + "/", StringComparison.Ordinal) => Kind.Actions,
        _ when path.StartsWith("/ui/", StringComparison.Ordinal) && !path.StartsWith("/ui/v1/", StringComparison.Ordinal) => Kind.Static,
        _ => Kind.None,
    };

    /// <summary>The view name of <c>/ui/v1/view/{name}</c>, or null when <paramref name="path"/> is not of that form.</summary>
    private static string? ManifestName(string path)
    {
        if (!path.StartsWith(ViewPath + "/", StringComparison.Ordinal))
        {
            return null;
        }

        var name = path[(ViewPath.Length + 1)..];
        return name.Length is > 0 and <= 64 && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? name : null;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        try
        {
            await ServeAsync(ctx).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // the client went away
        }
        catch (IOException)
        {
            // the client went away mid-write
        }
        catch (Exception e)
        {
            _o.Log($"ui request failed: {e.GetType().Name}");
            if (!ctx.Response.HasStarted)
            {
                await ReplyAsync(ctx, 500, "internal").ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        res.Headers["X-Frame-Options"] = "DENY";
        res.Headers["Referrer-Policy"] = "no-referrer";
        res.Headers["Cache-Control"] = "no-store";
        res.Headers["X-Content-Type-Options"] = "nosniff";

        var path = req.Path.Value ?? "/";
        var hosts = new[] { $"127.0.0.1:{Port}", $"localhost:{Port}", $"[::1]:{Port}" };

        if (req.Headers.Host.Count != 1 || !hosts.Contains(req.Headers.Host[0]))
        {
            await RejectAsync(ctx, 421, "host", path).ConfigureAwait(false);
            return;
        }

        if ((req.Headers.TryGetValue("Origin", out var o) && (o.Count != 1 || !hosts.Any(h => o[0] == "http://" + h)))
            || (req.Headers.TryGetValue("Sec-Fetch-Site", out var site) && (site.Count != 1 || site[0] is not ("same-origin" or "none"))))
        {
            await RejectAsync(ctx, 403, "origin", path).ConfigureAwait(false);
            return;
        }

        if (Closing)
        {
            await ReplyAsync(ctx, 503, "closing").ConfigureAwait(false);
            return;
        }

        var kind = Classify(path);
        if (kind == Kind.None)
        {
            await ReplyAsync(ctx, 404, "not_found").ConfigureAwait(false);
            return;
        }

        var allowed = kind is Kind.Answer or Kind.TeamClose or Kind.TeamRename or Kind.LooksMerge ? "POST" : "GET";
        if (req.Method != allowed && !(kind is Kind.Looks or Kind.Rules && req.Method == "POST"))
        {
            res.Headers["Allow"] = kind is Kind.Looks or Kind.Rules ? "GET, POST" : allowed;
            await ReplyAsync(ctx, 405, "method").ConfigureAwait(false);
            return;
        }

        if (kind is Kind.View or Kind.Manifest or Kind.ReadModel or Kind.List or Kind.Actions)
        {
            Touch();
            await ServeViewAsync(ctx, kind == Kind.Manifest ? ManifestName(path) : null, kind == Kind.ReadModel, kind == Kind.List ? path[ListPath.Length..] : null, kind == Kind.Actions ? path[ActionsPath.Length..].TrimStart('/') : null).ConfigureAwait(false);
            return;
        }

        if (kind == Kind.Answer)
        {
            Touch();
            await ServeAnswerAsync(ctx).ConfigureAwait(false);
            return;
        }

        if (kind == Kind.AnswerStatus)
        {
            Touch();
            await ServeAnswerStatusAsync(ctx).ConfigureAwait(false);
            return;
        }

        if (kind == Kind.History)
        {
            Touch();
            await ServeHistoryAsync(ctx).ConfigureAwait(false);
            return;
        }

        if (kind == Kind.TeamClose)
        {
            Touch();
            await ServeTeamCloseAsync(ctx, TeamClosePath, "team-close", _o.TeamClose, TeamCloseRoute.Run).ConfigureAwait(false);
            return;
        }

        if (kind == Kind.TeamRename)
        {
            Touch();
            await ServeTeamCloseAsync(ctx, TeamRenamePath, "team-rename", _o.TeamClose is { Rename: not null } s ? s : null, TeamRenameRoute.Run).ConfigureAwait(false);
            return;
        }

        if (kind == Kind.Rules)
        {
            Touch();
            await ServeRulesAsync(ctx).ConfigureAwait(false);
            return;
        }

        if (kind is Kind.Looks or Kind.LooksMerge)
        {
            Touch();
            await ServeLooksAsync(ctx, kind == Kind.LooksMerge).ConfigureAwait(false);
            return;
        }

        if (_static.Read(path) is { } file)
        {
            await WriteAsync(ctx, 200, file.ContentType, file.Bytes).ConfigureAwait(false);
        }
        else
        {
            await ReplyAsync(ctx, 404, "not_found").ConfigureAwait(false);
        }
    }

    private async Task ServeViewAsync(HttpContext ctx, string? manifest, bool readModel, string? list = null, string? actions = null)
    {
        var req = ctx.Request;
        var headers = req.Headers
            .Where(static h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(static h => h.Key.ToLowerInvariant(), static h => h.Value.ToString(), StringComparer.Ordinal);
        var request = new RouteRequest(
            "GET",
            readModel ? "/v1/read-model" : actions is not null ? (actions.Length == 0 ? "/v1/actions" : "/v1/actions/" + actions) : list is not null ? "/v1/list/" + list : manifest is null ? "/v1/view" : "/v1/view/" + manifest,
            req.QueryString.HasValue ? req.QueryString.Value![1..] : string.Empty,
            headers,
            default,
            manifest is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["name"] = manifest });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, CloseToken, _o.DrainToken);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = linked.Token.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), ended);
        var handler = _o.View(request, linked.Token);

        // A handler that does not look at its token cannot hold the answer, or the stop, past the close.
        if (await Task.WhenAny(handler, ended.Task).ConfigureAwait(false) != handler)
        {
            _ = handler.ContinueWith(
                static t => t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            if (!ctx.RequestAborted.IsCancellationRequested)
            {
                await ReplyAsync(ctx, 503, "closing").ConfigureAwait(false);
            }

            return;
        }

        RouteResponse response;
        try
        {
            response = await handler.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await ReplyAsync(ctx, 503, "closing").ConfigureAwait(false);
            return;
        }

        if (Closing)
        {
            await ReplyAsync(ctx, 503, "closing").ConfigureAwait(false);
            return;
        }

        await WriteAsync(ctx, response.Status, "application/json", response.Body, response.Headers).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /ui/v1/answer</c>: the user's answer to a parked decision. The request guard already passed is the proof that the user sent
    /// it; the host's <see cref="UiAnswerSupport"/> runs the shared answer core. One log line per outcome.
    /// </summary>
    private async Task ServeAnswerAsync(HttpContext ctx)
    {
        string? dirId = null;
        string? team = null;
        string? id = null;

        // one line per outcome, naming each field once the body has given it
        void Log(string outcome) =>
            _o.Log($"ui answer: route={AnswerPath} dir={Safe(dirId ?? "-")} team={Safe(team ?? "-")} id={Safe(id ?? "-")} outcome={outcome}");

        if (_o.Answer is not { } support)
        {
            Log("unavailable");
            await WriteJsonAsync(ctx, 503, new JsonObject { ["error"] = "unavailable", ["detail"] = "answer not wired" }).ConfigureAwait(false);
            return;
        }

        if (!IsJson(ctx.Request.ContentType))
        {
            Log("content_type");
            await ReplyAsync(ctx, 415, "content_type").ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
        if (body is null)
        {
            Log("body-refused");
            return;
        }

        // not cancelled by the client going away: once started, the answer runs to its end, and the line is the record
        var r = await Task.Run(() => UiAnswerRoute.Run(support, body), CancellationToken.None).ConfigureAwait(false);
        (dirId, team, id) = (r.DirId, r.Team, r.Id);
        Log(r.Outcome);
        if (r.Status is 200 or 500 && ctx.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        await (r.Reply is { } reply ? WriteJsonAsync(ctx, r.Status, reply) : ReplyAsync(ctx, r.Status, "internal")).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /ui/v1/answer-status?dir=&amp;team=&amp;event=</c>: whether the Orchestrator has seen the <c>question-answered</c> event an answer
    /// sent. A dirId that resolves to nothing, and any id that is not such an event in that team's Orchestrator box, is the same 404, so
    /// the route tells nothing of other mail. Reads only.
    /// </summary>
    private async Task ServeAnswerStatusAsync(HttpContext ctx)
    {
        var query = ctx.Request.Query;
        string? One(string key) => query.TryGetValue(key, out var v) && v.Count == 1 && !string.IsNullOrEmpty(v[0]) ? v[0] : null;
        var (dirId, team, eventId) = (One("dir"), One("team"), One("event"));

        if (_o.Answer is not { Status: { } status } support)
        {
            await WriteJsonAsync(ctx, 503, new JsonObject { ["error"] = "unavailable", ["detail"] = "answer status not wired" }).ConfigureAwait(false);
            return;
        }

        var seen = dirId is not null && team is not null && eventId is not null && support.ResolveDir(dirId) is not null
            ? await Task.Run(() => status(team, eventId), ctx.RequestAborted).ConfigureAwait(false)
            : null;
        if (seen is null)
        {
            await WriteJsonAsync(ctx, 404, new JsonObject { ["error"] = "not-found" }).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(ctx, 200, seen).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET /ui/v1/history?dir=&amp;id=</c>: one thread of the user's question log, the body <c>ah msg history --json --id</c> prints.
    /// The client names the dir by its dirId, as for answer-status; a dirId that resolves to nothing, or an empty id, is the same 404. With
    /// no <c>id</c> it is the list read (spec qa-history-replies §4.6): <c>q, team, since, state, questions, before, limit</c> filter and page
    /// the dir's threads, and a malformed one is a 400 <c>bad-query</c>. Reads only.
    /// </summary>
    private async Task ServeHistoryAsync(HttpContext ctx)
    {
        var query = ctx.Request.Query;
        string? One(string key) => query.TryGetValue(key, out var v) && v.Count == 1 && !string.IsNullOrEmpty(v[0]) ? v[0] : null;
        var (dirId, id) = (One("dir"), One("id"));

        if (_o.Answer is not { History: { } history } support)
        {
            await WriteJsonAsync(ctx, 503, new JsonObject { ["error"] = "unavailable", ["detail"] = "history not wired" }).ConfigureAwait(false);
            return;
        }

        var listed = !query.ContainsKey("id");
        if (dirId is null || (!listed && id is null) || support.ResolveDir(dirId) is not { } dir)
        {
            await WriteJsonAsync(ctx, 404, new JsonObject { ["error"] = "not-found" }).ConfigureAwait(false);
            return;
        }

        var q = listed ? HistoryQuery(query) : new Ah.Hierarchy.QaHistoryQuery { Id = id };
        if (q is null)
        {
            await WriteJsonAsync(ctx, 400, new JsonObject { ["error"] = "bad-query" }).ConfigureAwait(false);
            return;
        }

        if (!listed)
        {
            await WriteJsonAsync(ctx, 200, await Task.Run(() => history(dir, q), ctx.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false);
            return;
        }

        // a request whose client left while it waited never parses
        try
        {
            await _historyList.WaitAsync(ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        JsonObject body;
        try
        {
            body = await Task.Run(() => history(dir, q), ctx.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            _ = _historyList.Release();
        }

        await WriteJsonAsync(ctx, 200, body).ConfigureAwait(false);
    }

    /// <summary>
    /// The list read's filter from its query, or null when a parameter is malformed: given twice, <c>since</c> not epoch ms, <c>state</c> not
    /// <c>answered</c> or <c>unanswered</c>, <c>questions</c> not <c>1</c>, <c>true</c>, <c>0</c> or <c>false</c>, <c>before</c> not a
    /// cursor, or <c>limit</c> not a positive integer. An empty parameter is absent; <c>q</c> is plain text.
    /// </summary>
    internal static Ah.Hierarchy.QaHistoryQuery? HistoryQuery(IQueryCollection query)
    {
        var bad = false;
        string? Param(string key)
        {
            if (!query.TryGetValue(key, out var v) || v.Count == 0)
            {
                return null;
            }

            bad |= v.Count > 1;
            return string.IsNullOrEmpty(v[0]) ? null : v[0];
        }

        var (search, team, since, state, questions, before, limit) = (Param("q"), Param("team"), Param("since"), Param("state"), Param("questions"), Param("before"), Param("limit"));
        double sinceMs = 0;
        var n = 0;
        var cursor = before is null ? null : Ah.Hierarchy.HierarchyModule.QaCursorOf(before);
        if (bad
            || (since is not null && !double.TryParse(since, NumberStyles.None, CultureInfo.InvariantCulture, out sinceMs))
            || state is not (null or "answered" or "unanswered")
            || questions is not (null or "1" or "true" or "0" or "false")
            || (before is not null && cursor is null)
            || (limit is not null && !(int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0)))
        {
            return null;
        }

        var q = new Ah.Hierarchy.QaHistoryQuery
        {
            Search = search,
            Team = team,
            Since = since is null ? null : sinceMs,
            Answered = state is null ? null : state == "answered",
            Questions = questions is "1" or "true",
            Before = cursor,
        };
        return limit is null ? q : q with { Limit = n };
    }

    /// <summary><c>GET</c> and <c>POST /ui/v1/rules</c>: the request guard is the proof of the user, as for team close. One log line per outcome.</summary>
    private async Task ServeRulesAsync(HttpContext ctx)
    {
        void Log(string outcome) => _o.Log($"ui rules: route={RulesPath} outcome={Safe(outcome)}");

        if (_o.Rules is not { } support)
        {
            Log("unavailable");
            await WriteJsonAsync(ctx, 503, new JsonObject { ["error"] = "unavailable", ["detail"] = "rules not wired" }).ConfigureAwait(false);
            return;
        }

        var isPost = ctx.Request.Method == "POST";
        if (isPost && !IsJson(ctx.Request.ContentType))
        {
            Log("content_type");
            await ReplyAsync(ctx, 415, "content_type").ConfigureAwait(false);
            return;
        }

        var body = isPost ? await ReadBodyAsync(ctx).ConfigureAwait(false) : null;
        if (isPost && body is null)
        {
            Log("body-refused");
            return;
        }

        var team = ctx.Request.Query["team"].ToString();
        if (!isPost && team.Length == 0)
        {
            Log("no-team");
            await WriteJsonAsync(ctx, 400, new JsonObject { ["code"] = 2, ["message"] = "team is required" }).ConfigureAwait(false);
            return;
        }

        try
        {
            var (status, reply, outcome) = await Task.Run(
                () =>
                {
                    if (isPost)
                    {
                        return UiRulesRoute.Write(support, body!);
                    }

                    var read = support.Read(team);
                    return (UiRulesRoute.StatusOf(read), read, "read");
                },
                CancellationToken.None).ConfigureAwait(false);
            Log(outcome);
            if (!ctx.RequestAborted.IsCancellationRequested)
            {
                await WriteJsonAsync(ctx, status, reply).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // any failure of a rules call is one logged 500
        catch (Exception e)
#pragma warning restore CA1031
        {
            Log($"error detail={e.GetType().Name}");
            if (!ctx.RequestAborted.IsCancellationRequested)
            {
                await ReplyAsync(ctx, 500, "internal").ConfigureAwait(false);
            }
        }
    }

    private delegate (int Status, JsonObject Reply, string Outcome) TeamRouteRun(UiTeamCloseSupport support, ReadOnlySpan<byte> body, string by);

    /// <summary><c>POST /ui/v1/team-close</c> and <c>POST /ui/v1/team-rename</c>: the request guard is the proof of the user, as for the answer route. One log line per outcome.</summary>
    private async Task ServeTeamCloseAsync(HttpContext ctx, string path, string name, UiTeamCloseSupport? wired, TeamRouteRun run)
    {
        void Log(string outcome) => _o.Log($"ui {name}: route={path} outcome={Safe(outcome)}");

        if (wired is not { } support)
        {
            Log("unavailable");
            await WriteJsonAsync(ctx, 503, new JsonObject { ["error"] = "unavailable", ["detail"] = $"{name.Replace('-', ' ')} not wired" }).ConfigureAwait(false);
            return;
        }

        if (!IsJson(ctx.Request.ContentType))
        {
            Log("content_type");
            await ReplyAsync(ctx, 415, "content_type").ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(ctx).ConfigureAwait(false);
        if (body is null)
        {
            Log("body-refused");
            return;
        }

        // not cancelled by the client going away: sessions may already be closing, and the line is the record
        try
        {
            var (status, reply, outcome) = await Task.Run(() => run(support, body, "ui"), CancellationToken.None).ConfigureAwait(false);
            Log(outcome);
            if (!ctx.RequestAborted.IsCancellationRequested)
            {
                await WriteJsonAsync(ctx, status, reply).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // any failure of the close is one logged 500
        catch (Exception e)
#pragma warning restore CA1031
        {
            Log($"error detail={e.GetType().Name}");
            if (!ctx.RequestAborted.IsCancellationRequested)
            {
                await ReplyAsync(ctx, 500, "internal").ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The looks file: <c>GET</c> reads it, <c>POST</c> replaces it from <c>{rev, looks}</c> when <c>rev</c> is still current, and the merge
    /// route adds a looks object's custom looks and takes nothing away. A file that does not read as looks is refused, and left alone.
    /// </summary>
    private async Task ServeLooksAsync(HttpContext ctx, bool merge)
    {
        LooksReply reply;
        if (ctx.Request.Method == "GET")
        {
            reply = await Task.Run(() => LooksFile.Read(_o.Home), CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            if (!IsJson(ctx.Request.ContentType))
            {
                await ReplyAsync(ctx, 415, "content_type").ConfigureAwait(false);
                return;
            }

            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = MaxLooksBytes;
            }

            var body = await ReadBodyAsync(ctx, MaxLooksBytes).ConfigureAwait(false);
            if (body is null)
            {
                return;
            }

            if (!JsonText.TryParse(body, out var parsed) || !parsed.IsMap)
            {
                await WriteJsonAsync(ctx, 400, new JsonObject { ["error"] = "bad-request", ["detail"] = "the body must be one JSON object" }).ConfigureAwait(false);
                return;
            }

            var input = parsed.AsObject();
            reply = await Task.Run(
                () => merge
                    ? LooksFile.Merge(_o.Home, parsed)
                    : LooksFile.Write(_o.Home, input.Str("rev") ?? string.Empty, input.Get("looks") ?? JsonValue.Null),
                CancellationToken.None).ConfigureAwait(false);
            _o.Log($"ui looks: route={(merge ? LooksMergePath : LooksPath)} outcome={reply.Outcome}");
        }

        var doc = new JsonObject { ["path"] = LooksFile.PathOf(_o.Home), ["rev"] = reply.Rev, ["corrupt"] = reply.Outcome == LooksOutcome.Corrupt };
        doc["looks"] = reply.Looks is { } looks ? looks : JsonValue.Null;
        switch (reply.Outcome)
        {
            case LooksOutcome.Invalid:
                await WriteJsonAsync(ctx, 400, new JsonObject { ["error"] = "bad-request", ["detail"] = reply.Detail ?? "body" }).ConfigureAwait(false);
                break;
            case LooksOutcome.Stale:
                doc["refused"] = true;
                doc["reason"] = "stale";
                await WriteJsonAsync(ctx, 409, doc).ConfigureAwait(false);
                break;
            case LooksOutcome.Corrupt when ctx.Request.Method != "GET":
                doc["refused"] = true;
                doc["reason"] = "corrupt";
                await WriteJsonAsync(ctx, 409, doc).ConfigureAwait(false);
                break;
            default:
                if (merge)
                {
                    doc["added"] = reply.Added;
                    doc["already"] = reply.Already;
                    doc["skipped"] = reply.Skipped;
                }

                await WriteJsonAsync(ctx, 200, doc).ConfigureAwait(false);
                break;
        }
    }

    private static Task WriteJsonAsync(HttpContext ctx, int status, JsonObject body) =>
        WriteAsync(ctx, status, "application/json", JsonText.ToUtf8(body, 0));

    /// <summary>The request body, up to the cap. Null when the request was answered (413, or 408 for a body that did not arrive).</summary>
    private async Task<byte[]?> ReadBodyAsync(HttpContext ctx, int cap = MaxBodyBytes)
    {
        var req = ctx.Request;
        if (req.ContentLength > cap)
        {
            await RefuseTooLargeAsync(ctx).ConfigureAwait(false);
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, CloseToken);
        timeout.CancelAfter(_o.RequestRead);
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        try
        {
            int read;
            while ((read = await req.Body.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > cap)
                {
                    await RefuseTooLargeAsync(ctx).ConfigureAwait(false);
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await RefuseTooLargeAsync(ctx).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await ReplyAsync(ctx, Closing ? 503 : 408, Closing ? "closing" : "timeout").ConfigureAwait(false);
            return null;
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Answers 413 and lets the refused body finish arriving, for at most the largest cap plus 64 KiB or two seconds, before the connection
    /// goes: a client still writing would otherwise get a reset before it could read the answer. A body not yet read has its server limit
    /// lifted first, or the read below would stop at that limit at once.
    /// </summary>
    private static async Task RefuseTooLargeAsync(HttpContext ctx)
    {
        ctx.Response.Headers.Connection = "close";
        await ReplyAsync(ctx, 413, "too_large").ConfigureAwait(false);
        await ctx.Response.CompleteAsync().ConfigureAwait(false);
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }

        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var buffer = new byte[4096];
        long discarded = 0;
        try
        {
            int read;
            while (discarded < MaxLooksBytes + (64 * 1024) && (read = await ctx.Request.Body.ReadAsync(buffer, window.Token).ConfigureAwait(false)) > 0)
            {
                discarded += read;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or BadHttpRequestException)
        {
            // the client closed, or the window passed
        }
    }

    /// <summary><c>application/json</c> with an optional <c>; charset=utf-8</c> and nothing else.</summary>
    private static bool IsJson(string? contentType)
    {
        if (contentType is null)
        {
            return false;
        }

        var parts = contentType.Split(';');
        return parts.Length is 1 or 2
            && parts[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase)
            && (parts.Length == 1 || parts[1].Trim().Equals("charset=utf-8", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RejectAsync(HttpContext ctx, int status, string error, string path)
    {
        _o.Log($"ui request rejected: route={Safe(path)} status={status.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        await ReplyAsync(ctx, status, error).ConfigureAwait(false);
    }

    private static Task ReplyAsync(HttpContext ctx, int status, string error) =>
        WriteAsync(ctx, status, "application/json", JsonText.ToUtf8(new JsonObject { ["error"] = error }, 0));

    private static async Task WriteAsync(
        HttpContext ctx, int status, string contentType, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var res = ctx.Response;
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength = body.Length;
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                res.Headers[k] = v;
            }
        }

        await res.Body.WriteAsync(body, ctx.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>A request path for the log: printable ASCII only, and not longer than 80 characters.</summary>
    internal static string Safe(string path)
    {
        var shown = path.Length > 80 ? path[..80] : path;
        return string.Concat(shown.Select(static c => c is >= ' ' and <= '~' ? c : '?'));
    }
}
