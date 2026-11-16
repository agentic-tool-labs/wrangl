using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.State;

/// <summary>
/// The answer to one state request, as the daemon's <c>/v1/state</c> route sends it and the one-shot verb prints it.
/// <paramref name="Code"/> is the underlying outcome (<c>ok</c>, <c>op_failed</c>, <c>lock_timeout</c>,
/// <c>bad_request</c>, <c>not_found</c>, <c>internal</c>, or the op's own error code), which the one-shot verb turns into
/// an exit code.
/// </summary>
// Value: the op's own result value when it handed one out (OpResult.Value); the in-process reader uses it in place of parsing Body.
// A Value that is not a JsonValue is something the op handed out beside its result; the in-process reader keeps it with the document it parses.
public sealed record StateReply(int Status, ReadOnlyMemory<byte> Body, string Code, object? Value = null);

/// <summary>
/// One state request, end to end: the op lookup, the caller context and path-argument checks, the read or the write
/// through the engine, the after-write hook, and the answer's status and body. The daemon's route and the one-shot verb
/// both call it, so the two answer a request alike (the one place a state request is validated).
/// </summary>
public interface IStateService
{
    /// <summary>
    /// A request as the route receives it: <paramref name="method"/> <c>GET</c> reads <c>args</c> and the context from
    /// <paramref name="query"/>; <c>POST</c> reads <c>args</c>, <c>context</c> and <c>op_id</c> from the JSON
    /// <paramref name="body"/>. <paramref name="scheduleWrite"/> runs a write on the domain's own worker.
    /// </summary>
    Task<StateReply> HandleAsync(
        string method,
        string domain,
        string op,
        IReadOnlyDictionary<string, string> query,
        ReadOnlyMemory<byte> body,
        Func<string, Func<StateResult>, Task<StateResult>> scheduleWrite);

    /// <summary>
    /// A request as the one-shot verb receives it: the same checks and the same answer. <paramref name="viaPost"/> says the
    /// caller means a write (<c>POST</c>); a read op through it, or a write through a read, is a <c>bad_request</c>.
    /// <paramref name="lockWait"/> is how long a write waits for its domain lock (default: the stale age plus 2 s).
    /// </summary>
    Task<StateReply> InvokeAsync(
        string domain,
        string op,
        bool viaPost,
        JsonValue? args,
        JsonValue? context,
        string? opId,
        Func<string, Func<StateResult>, Task<StateResult>> scheduleWrite,
        TimeSpan? lockWait = null);

    /// <summary>Whether <paramref name="domain"/> registers an op named <paramref name="op"/>.</summary>
    bool HasOp(string domain, string op);
}

/// <summary>
/// The daemon's own in-process reads. It is a separate interface from <see cref="IStateService"/> on purpose: the daemon's routes and
/// the one-shot verb are built on <see cref="IStateService"/>, so they have no way to name this member.
/// </summary>
public interface IInternalStateRead
{
    /// <summary>
    /// A read the daemon makes for its own view: <paramref name="hierarchyDir"/> is the pool the read is about and is not claimed
    /// through a <c>cwd</c>. Only a read op is served; a write op is a <c>bad_request</c>.
    /// </summary>
    Task<StateReply> ReadInternalAsync(string domain, string op, JsonValue? args, string hierarchyDir);
}

/// <summary>
/// One state request, end to end: the op lookup, the caller context and path-argument checks, the read or the write
/// through the engine, the after-write hook, and the answer's status and body. The daemon's route and the one-shot verb
/// both call it, so the two answer a request alike (the one place a state request is validated).
/// </summary>
internal sealed class StateService : IStateService, IInternalStateRead
{
    private static readonly TimeSpan LockWait = ILockProvider.CliWait;

    private readonly IReadOnlyList<IStateDomain> _domains;
    private readonly Func<string, string, ReadOnlyMemory<byte>, RequestContext, string, TimeSpan, StateResult> _engine;
    private readonly StateRequestChecks _checks;
    private readonly IHookErrorLog? _hookErrors;
    private readonly IStateWriteObserver? _observer;

    internal StateService(
        IReadOnlyList<IStateDomain> domains,
        Func<string, string, ReadOnlyMemory<byte>, RequestContext, string, TimeSpan, StateResult> engine,
        StateRequestChecks checks,
        IHookErrorLog? hookErrors,
        IStateWriteObserver? observer = null)
    {
        _observer = observer;
        _domains = domains;
        _engine = engine;
        _checks = checks;
        _hookErrors = hookErrors;
    }

    /// <summary>
    /// A request as the route receives it: <paramref name="method"/> <c>GET</c> reads <c>args</c> and the context from
    /// <paramref name="query"/>; <c>POST</c> reads <c>args</c>, <c>context</c> and <c>op_id</c> from the JSON
    /// <paramref name="body"/>. <paramref name="scheduleWrite"/> runs a write on the domain's own worker.
    /// </summary>
    public Task<StateReply> HandleAsync(
        string method,
        string domain,
        string op,
        IReadOnlyDictionary<string, string> query,
        ReadOnlyMemory<byte> body,
        Func<string, Func<StateResult>, Task<StateResult>> scheduleWrite)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scheduleWrite);
        var get = method == "GET";
        JsonValue? args = null;
        JsonValue? context = null;
        string? opId = null;
        if (get)
        {
            if (query.TryGetValue("args", out var raw))
            {
                try
                {
                    args = JsonText.Parse(raw);
                }
                catch (JsonParseException)
                {
                    return Task.FromResult(Problem(400, "bad_request", "args must be a JSON object", null));
                }
            }

            var fields = new JsonObject();
            foreach (var name in new[] { "cwd", "hierarchy_dir", "main_hierarchy_dir", "messaging_socket_dir" })
            {
                if (query.TryGetValue(name, out var value))
                {
                    fields[name] = value;
                }
            }

            context = JsonValue.FromObject(fields);
        }
        else
        {
            JsonValue parsed;
            try
            {
                parsed = body.IsEmpty ? JsonValue.FromObject(new JsonObject()) : JsonText.Parse(body.Span);
            }
            catch (JsonParseException)
            {
                return Task.FromResult(Problem(400, "bad_request", "the request body is not JSON", null));
            }

            if (!parsed.IsMap)
            {
                return Task.FromResult(Problem(400, "bad_request", "the request body must be a JSON object", null));
            }

            args = parsed.Member("args");
            context = parsed.Member("context") is { IsMap: true } c ? c : JsonValue.FromObject(new JsonObject());
            opId = parsed.Member("op_id") is { IsText: true } id ? id.AsString() : null;
        }

        return InvokeAsync(domain, op, !get, args, context, opId, scheduleWrite);
    }

    /// <summary>
    /// A request as the one-shot verb receives it: the same checks and the same answer. <paramref name="viaPost"/> says the
    /// caller means a write (<c>POST</c>); a read op through it, or a write through a read, is a <c>bad_request</c>.
    /// </summary>
    public Task<StateReply> InvokeAsync(
        string domain,
        string op,
        bool viaPost,
        JsonValue? args,
        JsonValue? context,
        string? opId,
        Func<string, Func<StateResult>, Task<StateResult>> scheduleWrite,
        TimeSpan? lockWait = null) =>
        InvokeCoreAsync(domain, op, viaPost, args, context, opId, scheduleWrite, lockWait, null);

    public bool HasOp(string domain, string op) => _domains.FirstOrDefault(d => d.Name == domain)?.Ops.Any(o => o.Name == op) == true;

    /// <summary>
    /// The one dispatch. <paramref name="internalDir"/> is set only by <see cref="ReadInternalAsync"/>: the request then carries that
    /// hierarchy directory in place of a caller-supplied context: the whole context check (<c>CheckContext</c>, claim check included) is
    /// left out and the lexical <c>CheckInternalDir</c> takes its place.
    /// </summary>
    private async Task<StateReply> InvokeCoreAsync(
        string domain,
        string op,
        bool viaPost,
        JsonValue? args,
        JsonValue? context,
        string? opId,
        Func<string, Func<StateResult>, Task<StateResult>> scheduleWrite,
        TimeSpan? lockWait,
        string? internalDir)
    {
        ArgumentNullException.ThrowIfNull(scheduleWrite);
        var owner = _domains.FirstOrDefault(d => d.Name == domain);
        var spec = owner?.Ops.FirstOrDefault(o => o.Name == op);
        if (spec is null)
        {
            return Problem(404, "not_found", $"no state operation {domain}.{op}", null);
        }

        var read = spec.Kind == OpKind.Read;
        if (internalDir is not null && !read)
        {
            // a write through the internal reader is refused for being a write, whatever its Daemon flag says
            return Problem(400, "bad_request", $"{domain}.{op} is a write operation: use POST", null);
        }

        // the Daemon flag keeps an op off the route surface; the in-process read of a read op does not consult it
        if (!spec.Daemon && internalDir is null)
        {
            return Problem(400, "bad_request", $"{domain}.{op} is not served by the daemon", null);
        }

        if (viaPost == read)
        {
            return Problem(400, "bad_request", $"{domain}.{op} is a {(read ? "read" : "write")} operation: use {(read ? "GET" : "POST")}", null);
        }

        var argsObject = args is { } given ? given : JsonValue.FromObject(new JsonObject());
        if (!argsObject.IsMap)
        {
            return Problem(400, "bad_request", "args must be a JSON object", null);
        }

        RequestContext request;
        if (internalDir is not null)
        {
            if (StateRequestChecks.CheckInternalDir(internalDir) is { } badDir)
            {
                return Problem(400, "bad_request", badDir.Message, badDir.Field);
            }

            request = new RequestContext(internalDir, internalDir, string.Empty, string.Empty);
        }
        else
        {
            if (_checks.CheckContext(context ?? JsonValue.FromObject(new JsonObject()), owner!.HomeScoped, owner.DbOnly) is { } bad)
            {
                return Problem(400, "bad_request", bad.Message, bad.Field);
            }

            request = StateRequestChecks.Context(context ?? JsonValue.FromObject(new JsonObject()));
        }
        if (_checks.CheckPathArgs(spec.PathArgs ?? [], argsObject, request) is { } outside)
        {
            return Problem(400, "bad_request", outside.Message, outside.Field);
        }

        var argsJson = JsonText.ToUtf8(argsObject, 0);
        if (read)
        {
            return RunRead(spec, new OpCall(op, argsJson, request));
        }

        if (string.IsNullOrEmpty(opId))
        {
            return Problem(400, "bad_request", "a state write needs an op_id", null);
        }

        StateResult result;
        try
        {
            result = spec.BusyAfter is { } busyAfter
                ? await WriteBoundedAsync(domain, spec, busyAfter, argsObject, argsJson, request, opId, lockWait, scheduleWrite).ConfigureAwait(false)
                : await scheduleWrite(domain, () => _engine(domain, op, argsJson, request, opId, lockWait ?? LockWait)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _hookErrors?.Log("state-service", error.Message);
            return Internal("internal");
        }

        if (!result.Ok)
        {
            return Failure(result);
        }

        StateRules.AfterWrite(_domains, domain, op, argsJson, result.ResultJson, request);
        try
        {
            _observer?.Wrote(domain, op, argsJson, result.ResultJson);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _hookErrors?.Log("state-write-observer", error.Message);
        }

        return Success(result.ResultJson);
    }

    private const int Waiting = 0;
    private const int Started = 1;
    private const int Abandoned = 2;

    /// <summary>
    /// A write that must not leave its caller waiting: it waits at most <paramref name="busyAfter"/> for its domain's worker and at
    /// most <paramref name="busyAfter"/> more for its lock, so the longest refusal comes at about twice <paramref name="busyAfter"/>.
    /// A write still queued at that point is abandoned (the worker skips it when it comes up, so a refused write never lands later);
    /// one already running keeps going but waits for its lock no longer than <paramref name="busyAfter"/>. Either way the answer is
    /// <c>busy</c>, naming the directory and which wait ran out.
    /// </summary>
    private async Task<StateResult> WriteBoundedAsync(
        string domain,
        StateOp spec,
        TimeSpan busyAfter,
        JsonValue args,
        ReadOnlyMemory<byte> argsJson,
        RequestContext request,
        string opId,
        TimeSpan? lockWait,
        Func<string, Func<StateResult>, Task<StateResult>> scheduleWrite)
    {
        var phase = Waiting;
        var wait = lockWait is { } given && given < busyAfter ? given : busyAfter;
        var dir = args.Member("dir") is { IsText: true } d ? d.AsString() : request.HierarchyDirectory;
        var scheduled = scheduleWrite(domain, () =>
            Interlocked.CompareExchange(ref phase, Started, Waiting) == Waiting
                ? _engine(domain, spec.Name, argsJson, request, opId, wait)
                : StateRules.Fail("busy", string.Empty));
        using var timer = new CancellationTokenSource();
        var expired = Task.Delay(busyAfter, timer.Token);
        if (await Task.WhenAny(scheduled, expired).ConfigureAwait(false) == scheduled)
        {
            await timer.CancelAsync().ConfigureAwait(false);
        }
        else if (Interlocked.CompareExchange(ref phase, Abandoned, Waiting) == Waiting)
        {
            return StateRules.Fail("busy", $"{domain}.{spec.Name}: busy: for {dir} the {domain} worker took no new write within {(int)busyAfter.TotalMilliseconds} ms (earlier writes still running); nothing was written");
        }

        var result = await scheduled.ConfigureAwait(false);
        return result.ErrorCode == "lock_timeout"
            ? StateRules.Fail("busy", $"{domain}.{spec.Name}: busy: for {dir} the {domain} state lock did not clear within {(int)wait.TotalMilliseconds} ms; nothing was written")
            : result;
    }

    /// <summary>
    /// A read as the daemon's own view makes it, in-process: the op lookup, the read-kind check, the path-argument containment and
    /// <see cref="RunRead"/> are the route's own. The whole context check (<c>CheckContext</c>, which includes the claim check of a
    /// hierarchy directory derived from a <c>cwd</c> or trusted) is left out and the lexical <c>CheckInternalDir</c> takes its place,
    /// because the view reads every registered hierarchy directory and no <c>cwd</c> stands behind them.
    /// A write op is a <c>bad_request</c>. Nothing on the HTTP surface calls this.
    /// </summary>
    // TODO: when peer-proof phase B adds a caller principal and op authority to dispatch, this call becomes the internal
    // principal in that authority check (reads allowed, writes refused), not a bypass of it.
    public Task<StateReply> ReadInternalAsync(string domain, string op, JsonValue? args, string hierarchyDir)
    {
        ArgumentNullException.ThrowIfNull(hierarchyDir);
        return InvokeCoreAsync(domain, op, viaPost: false, args, null, null, static (_, work) => Task.FromResult(work()), null, hierarchyDir);
    }

    private StateReply RunRead(StateOp spec, OpCall call)
    {
        OpResult outcome;
        try
        {
            outcome = spec.Body(call, null);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _hookErrors?.Log("state-service", error.Message);
            return Internal("internal");
        }

        return outcome.Ok
            ? Success(outcome.ResultJson) with { Value = outcome.Value }
            : Failure(new StateResult(false, ReadOnlyMemory<byte>.Empty, outcome.Error, outcome.ErrorCode));
    }

    private StateReply Failure(StateResult result)
    {
        var message = result.Error ?? "the operation failed";
        switch (result.ErrorCode)
        {
            case "bad_request":
                return Problem(400, "bad_request", message, null);
            case "lock_timeout":
            case "internal":
                _hookErrors?.Log("state-service", message);
                return Internal(result.ErrorCode);
            default:
                // The operation's own failure is final: the client raises it and does not run the operation again.
                var code = result.ErrorCode is null or "op_failed" ? null : result.ErrorCode;
                var error = new JsonObject();
                error["code"] = "op_failed";
                error["message"] = message;
                error["error_code"] = code is null ? JsonValue.Null : JsonValue.FromString(code);
                var body = new JsonObject();
                body["error"] = error;
                return new StateReply(422, JsonText.ToUtf8(JsonValue.FromObject(body), 0), code ?? "op_failed");
        }
    }

    private static StateReply Success(ReadOnlyMemory<byte> resultJson)
    {
        var prefix = "{\"result\":"u8;
        var value = resultJson.IsEmpty ? "null"u8 : resultJson.Span;
        var body = new byte[prefix.Length + value.Length + 1];
        prefix.CopyTo(body);
        value.CopyTo(body.AsSpan(prefix.Length));
        body[^1] = (byte)'}';
        return new StateReply(200, body, "ok");
    }

    private static StateReply Problem(int status, string code, string message, string? field)
    {
        var error = new JsonObject();
        error["code"] = code;
        error["message"] = message;
        if (field is not null)
        {
            error["field"] = field;
        }

        var body = new JsonObject();
        body["error"] = error;
        return new StateReply(status, JsonText.ToUtf8(JsonValue.FromObject(body), 0), code);
    }

    private static StateReply Internal(string code)
    {
        var reply = Problem(500, "internal", "internal error", null);
        return reply with { Code = code };
    }
}
