using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.State;

/// <summary>
/// The native state client (0105 §5): one entry point for every native write. A write goes to the running native daemon
/// (a POST with an <c>op_id</c>) when <c>discover()</c> finds one that answers protocol 1, and otherwise runs in this
/// process through the engine, under the same domain lock. A route's failure is final: no path change on a 413, a 4xx, a
/// 422 or a timeout. Reads run in-process. The client never calls itself from an op body.
/// </summary>
internal sealed class StateClient(
    IDaemonClient daemon,
    IReadOnlyList<IStateDomain> domains,
    IHookErrorLog hookErrors,
    Func<string, string, ReadOnlyMemory<byte>, RequestContext, string, TimeSpan, StateResult> engine,
    TimeProvider time,
    Func<string, bool>? mayWriteHook = null,
    string? lockDirectory = null) : IStateWriter
{
    internal static readonly TimeSpan HookWait = TimeSpan.FromMilliseconds(300);
    internal static readonly TimeSpan HookBudget = TimeSpan.FromMilliseconds(1500);

    /// <summary>What a hook write still gets when its budget is spent: a spent budget never drops a write untried. The slowest hook write under 44 writers took 122 ms.</summary>
    internal static readonly TimeSpan HookWriteFloor = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan CliDaemonWait = ILockProvider.DaemonWait;
    internal static readonly TimeSpan CliLockWait = ILockProvider.CliWait;
    internal const int UpdateAttempts = 5;

    public bool IsHomeScoped(string domain) => domains.Any(d => d.Name == domain && d.HomeScoped);

    public bool IsDbOnly(string domain) => domains.Any(d => d.Name == domain && d.DbOnly);

    /// <summary>
    /// The first directory a hook-mode write would land in that the confinement refuses, or null: the lock directory the engine
    /// uses (the home's state locks with a database, else the domain's own state directory), and every path argument the op names.
    /// </summary>
    private string? RefusedTarget(string domain, StateOp? spec, JsonValue? args, RequestContext context, StateCallOptions options)
    {
        if (mayWriteHook is null || !options.Hook)
        {
            return null;
        }

        var targets = new List<string>();
        if (domains.FirstOrDefault(d => d.Name == domain) is { } owner)
        {
            try
            {
                // the engine falls back to the state directory when the home's locks are lost at run time, so both are listed
                targets.Add(owner.StateDirectory(context));
                if (lockDirectory is not null)
                {
                    targets.Add(lockDirectory);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return "(unknown)";
            }
        }

        if (spec is not null && args is { IsMap: true } map)
        {
            foreach (var name in spec.PathArgs ?? [])
            {
                if (map.Member(name) is { IsText: true } text && text.AsString().Length > 0)
                {
                    targets.Add(text.AsString());
                }
            }
        }

        return targets.FirstOrDefault(t => !mayWriteHook(t));
    }

    private StateResult? ConfinedHookWrite(string domain, StateOp? spec, JsonValue? args, RequestContext context, StateCallOptions options)
    {
        if (RefusedTarget(domain, spec, args, context, options) is not { } refused)
        {
            return null;
        }

        hookErrors.Log("state-client", "test-mode write outside the sandbox skipped: " + refused);
        return new StateResult(true, "{}"u8.ToArray());
    }

    public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null)
    {
        options ??= new StateCallOptions();
        var started = time.GetTimestamp();
        var spec = FindOp(domain, op);
        if (spec is null)
        {
            return StateRules.Fail("bad_request", $"unknown state operation {domain}.{op}");
        }

        JsonValue args;
        try
        {
            args = argsJson.IsEmpty ? new JsonObject() : JsonText.Parse(argsJson.Span);
        }
        catch (JsonParseException)
        {
            return StateRules.Fail("bad_request", $"{domain}.{op}: args are not JSON");
        }

        if (spec.Kind == OpKind.Read)
        {
            return RunRead(domain, spec, argsJson, context);
        }

        if (ConfinedHookWrite(domain, spec, args, context, options) is { } confined)
        {
            return confined;
        }

        var preflight = StateRules.Preflight(domain, op, spec, args);
        if (preflight is not null)
        {
            return preflight;
        }

        var opId = string.IsNullOrEmpty(options.OperationId) ? Guid.NewGuid().ToString("D") : options.OperationId;
        var result = Write(domain, op, spec, args, context, opId, options, started, out var daemonAnswered);

        // a write the daemon answered ran its AfterWrite there (StateService); the daemon owns it, so it is not run again here
        if (result.Ok && !daemonAnswered)
        {
            StateRules.AfterWrite(domains, domain, op, argsJson, result.ResultJson, context);
        }

        return result;
    }

    public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new StateCallOptions();
        if (ConfinedHookWrite(domain, null, null, context, options) is { } confined)
        {
            return new StateUpdateResult(false, confined);
        }

        var started = time.GetTimestamp();
        for (var attempt = 1; attempt <= UpdateAttempts; attempt++)
        {
            var doc = plan.Read();
            var outcome = plan.Mutate(doc);
            if (outcome is null)
            {
                return new StateUpdateResult(false, new StateResult(true, "null"u8.ToArray()));
            }

            var call = plan.Build(outcome.Value, doc.Digest);
            var attemptOptions = options with { OperationId = null };
            if (options.Hook)
            {
                var left = (options.Budget ?? HookBudget) - time.GetElapsedTime(started);
                attemptOptions = attemptOptions with { Budget = left > TimeSpan.Zero ? left : HookWriteFloor };
            }

            var result = Invoke(domain, call.Op, call.ArgsJson, context, attemptOptions);
            if (!result.Ok || !StateRules.IsConflict(result.ResultJson))
            {
                return new StateUpdateResult(result.Ok, result);
            }
        }

        return new StateUpdateResult(
            false,
            StateRules.Fail("conflict", $"the file changed {UpdateAttempts} times while it was being updated"));
    }

    // ---------------------------------------------------------------- the write

    private StateResult Write(string domain, string op, StateOp spec, JsonValue args, RequestContext context, string opId, StateCallOptions options, long started, out bool daemonAnswered)
    {
        daemonAnswered = false;
        var budget = options.Hook ? (options.Budget ?? HookBudget) : (TimeSpan?)null;
        if (spec.Daemon)
        {
            var wait = options.Hook ? Max(TimeSpan.FromMilliseconds(1), Min(budget!.Value, HookWait)) : CliDaemonWait;
            var viaDaemon = TryDaemon(domain, op, args, context, opId, wait, out var replayable);
            if (viaDaemon is { ErrorCode: "state_timeout" } && !options.Hook)
            {
                // the daemon may have applied the write after the client stopped waiting; its executed-op log makes this
                // replay under the same op id a no-op that reports the write as landed
                var confirm = ReplayInProcess(domain, op, args, context, opId, options, null, started);
                return confirm.Ok ? confirm with { Error = "confirmed after timeout" } : viaDaemon;
            }

            if (viaDaemon is not null && !(options.Hook && replayable))
            {
                daemonAnswered = true;
                return viaDaemon;
            }

            if (viaDaemon is not null)
            {
                // a hook write the daemon did not answer in time or answered 5xx runs once in-process under the same op id: the
                // engine's executed-op log, which the daemon writes too, makes a write the daemon did apply a no-op here
                var replay = ReplayInProcess(domain, op, args, context, opId, options, budget!.Value, started);
                return replay.Ok
                    ? replay
                    : StateRules.Fail(replay.ErrorCode ?? "state_failed", $"daemon: {viaDaemon.Error}; in-process: {replay.Error}");
            }
        }

        return ReplayInProcess(domain, op, args, context, opId, options, budget, started);
    }

    /// <summary>The write through the in-process engine, within what is left of a hook's budget.</summary>
    private StateResult ReplayInProcess(
        string domain, string op, JsonValue args, RequestContext context, string opId, StateCallOptions options, TimeSpan? budget, long started)
    {
        var lockWait = options.LockWait ?? CliLockWait;
        if (options.Hook)
        {
            lockWait = Max(budget!.Value - time.GetElapsedTime(started), HookWriteFloor);
        }

        var engineStart = Ah.Core.Time.PhaseTrace.Begin();
        var result = engine(domain, op, JsonText.ToUtf8(args, 0), context, opId, lockWait);
        Ah.Core.Time.PhaseTrace.End("state.in-process-engine", engineStart);
        if (options.Hook && !result.Ok && lockWait == HookWriteFloor)
        {
            hookErrors.Log("state-client", $"the hook's time budget ran out and {domain}.{op} was not written: {result.ErrorCode}: {result.Error}");
        }

        return result;
    }

    /// <summary>The write through a running native daemon; null when there is none to use (down, draining, not native, no protocol 1).</summary>
    private StateResult? TryDaemon(string domain, string op, JsonValue args, RequestContext context, string opId, TimeSpan wait, out bool replayable)
    {
        replayable = false;
        using var cts = new CancellationTokenSource(wait);
        try
        {
            var discoverStart = Ah.Core.Time.PhaseTrace.Begin();
            var info = Block(daemon.DiscoverAsync(wait, cts.Token));
            Ah.Core.Time.PhaseTrace.End("daemon.discover", discoverStart);
            if (info is null)
            {
                return null;
            }

            var healthStart = Ah.Core.Time.PhaseTrace.Begin();
            var healthReply = Block(daemon.RequestAsync(new DaemonRequest("GET", "/v1/health", default, wait, StartIfDown: false), cts.Token));
            Ah.Core.Time.PhaseTrace.End("daemon.health", healthStart);
            if (healthReply.Status != 200)
            {
                return null;
            }

            var health = JsonText.Parse(healthReply.Body.Span);
            var impl = health.Member("impl") is { IsText: true } i ? i.AsString() : string.Empty;
            var draining = health.Member("draining") is { IsTruthy: true };
            var protocols = health.Member("protocols") is { IsArray: true } list
                ? list.AsArray().Select(p => p.IsNumber ? (int)p.AsNumber() : -1).ToList()
                : [health.Member("protocol") is { IsNumber: true } one ? (int)one.AsNumber() : -1];
            if (impl != "native" || draining || !protocols.Contains(1))
            {
                return null;
            }

            var body = new JsonObject { ["args"] = args, ["context"] = ContextJson(context), ["op_id"] = opId };
            var postStart = Ah.Core.Time.PhaseTrace.Begin();
            var reply = Block(daemon.RequestAsync(
                new DaemonRequest("POST", $"/v1/state/{domain}/{op}", JsonText.ToUtf8(body, 0), wait, StartIfDown: false),
                cts.Token));
            Ah.Core.Time.PhaseTrace.End("daemon.state-post", postStart);
            replayable = reply.Status >= 500;
            return FromRoute(reply.Status, reply.Body.Span);
        }
        catch (OperationCanceledException)
        {
            replayable = true;
            return StateRules.Fail("state_timeout", $"{domain}.{op} did not answer within {(int)wait.TotalMilliseconds} ms");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Net.Sockets.SocketException or JsonParseException)
        {
            return null;
        }
    }

    private static T Block<T>(Task<T> task) => task.GetAwaiter().GetResult();

    /// <summary>The body of a <c>/v1/state</c> reply as a result: 200 with <c>result</c> is success; every other answer fails and is final.</summary>
    private static StateResult FromRoute(int status, ReadOnlySpan<byte> body)
    {
        JsonValue doc;
        try
        {
            doc = JsonText.Parse(body);
        }
        catch (JsonParseException)
        {
            return StateRules.Fail("internal", $"unreadable reply (status {status})");
        }

        if (status == 200 && doc.Member("result") is { } result)
        {
            return new StateResult(true, JsonText.ToUtf8(result, 0));
        }

        var error = doc.Member("error");
        var code = error?.Member("code") is { IsText: true } c ? c.AsString() : $"http_{status}";
        var message = error?.Member("message") is { IsText: true } m ? m.AsString() : $"state route answered {status}";
        if (status == 422 && code == "op_failed" && error?.Member("error_code") is { IsText: true } errorCode)
        {
            code = errorCode.AsString();
        }

        return StateRules.Fail(code, message);
    }

    // ---------------------------------------------------------------- reads

    private StateOp? FindOp(string domain, string op) =>
        domains.FirstOrDefault(d => d.Name == domain)?.Ops.FirstOrDefault(o => o.Name == op);

    private static StateResult RunRead(string domain, StateOp spec, ReadOnlyMemory<byte> argsJson, RequestContext context)
    {
        var outcome = spec.Body(new OpCall(spec.Name, argsJson, context), null);
        return outcome.Ok
            ? new StateResult(true, outcome.ResultJson)
            : StateRules.Fail(outcome.ErrorCode ?? "op_failed", outcome.Error ?? $"{domain}.{spec.Name} failed");
    }

    /// <summary>The request's context as the route reads it: an absent directory is null, never an empty string.</summary>
    private static JsonObject ContextJson(RequestContext c)
    {
        static JsonValue Dir(string value) => value.Length == 0 ? JsonValue.Null : JsonValue.FromString(value);
        return new JsonObject
        {
            ["cwd"] = c.Cwd,
            ["hierarchy_dir"] = c.HierarchyDirectory,
            ["main_hierarchy_dir"] = Dir(c.MainHierarchyDirectory),
            ["messaging_socket_dir"] = Dir(c.SocketDirectory),
        };
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
