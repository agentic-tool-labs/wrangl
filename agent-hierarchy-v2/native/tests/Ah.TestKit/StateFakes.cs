using Ah.Abstractions;

namespace Ah.TestKit;

/// <summary>A daemon client with a scripted answer per request. Honours cancellation when <see cref="Hang"/> is set.</summary>
public sealed class FakeDaemonClient : IDaemonClient
{
    public DaemonInfo? Info { get; set; }

    /// <summary>A health answer that lists the protocols a current daemon serves, protocol 2 (mail claim records) included.</summary>
    public static DaemonReply CurrentHealth { get; } = new(200, "{\"protocols\":[1,2]}"u8.ToArray());

    public Func<DaemonRequest, DaemonReply> Reply { get; set; } = request => request.PathAndQuery == "/v1/health" ? CurrentHealth : new DaemonReply(200, default);

    /// <summary>When true, a request never completes on its own: it waits for the cancellation token.</summary>
    public bool Hang { get; set; }

    public Exception? ThrowOnDiscover { get; set; }

    /// <summary>What <see cref="DaemonPresent"/> answers: a daemon process or socket exists.</summary>
    public bool Present { get; set; }

    public bool DaemonPresent() => Present;

    public List<DaemonRequest> Requests { get; } = [];

    public List<(string Verb, string Reason)> Fallbacks { get; } = [];

    public int DiscoverCalls { get; private set; }

    public Task<DaemonInfo?> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        DiscoverCalls++;
        if (ThrowOnDiscover is not null)
        {
            throw ThrowOnDiscover;
        }

        return Task.FromResult(Info);
    }

    public Task<DaemonInfo?> EnsureDaemonAsync(TimeSpan budget, CancellationToken cancellationToken = default) => Task.FromResult(Info);

    public async Task<DaemonReply> RequestAsync(DaemonRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        return Reply(request);
    }

    public Task DrainAsync(DaemonInfo daemon, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void RecordFallback(string verb, string reason) => Fallbacks.Add((verb, reason));

    public static DaemonInfo Up() => new(1, 4242, "0.0.0", 1, "/tmp/none.sock", "/tmp/none", "2026-01-01T00:00:00.000Z");
}

/// <summary>A process runner with a scripted result; keeps every request.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public Func<ProcessRequest, ProcessResult> Result { get; set; } = _ => Exited(0, "{\"result\":{}}");

    public List<ProcessRequest> Requests { get; } = [];

    public ProcessResult Run(ProcessRequest request)
    {
        Requests.Add(request);
        return Result(request);
    }

    public static ProcessResult Exited(int code, string stdout) =>
        new(code, null, false, false, System.Text.Encoding.UTF8.GetBytes(stdout), default);

    public static ProcessResult TimedOut() => new(null, null, true, false, default, default);
}

/// <summary>A state domain whose ops are whatever the test lists; keeps every after-write call.</summary>
public sealed class FakeStateDomain(string name, params StateOp[] ops) : IStateDomain
{
    public string Name { get; } = name;

    public IReadOnlyList<string> StoreNames => [];

    public IReadOnlyList<StateOp> Ops { get; } = ops;

    public List<(OpCall Call, OpResult Result)> AfterWrites { get; } = [];

    public string StateDirectory(RequestContext context) => context.HierarchyDirectory;

    public void AfterWrite(OpCall operation, OpResult result) => AfterWrites.Add((operation, result));

    public static StateOp Write(string op, bool daemon = true, bool digestPrecondition = false) =>
        new(op, OpKind.Write, OpStyle.Overwrite, false, null, daemon, (_, _) => new OpResult(true, default), digestPrecondition);

    public static StateOp Read(string op, Func<OpCall, OpResult>? body = null) =>
        new(op, OpKind.Read, null, false, null, false, (call, _) => (body ?? (_ => new OpResult(true, "{\"read\":true}"u8.ToArray())))(call));
}
