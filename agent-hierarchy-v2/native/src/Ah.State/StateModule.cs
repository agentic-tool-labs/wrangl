using Ah.Abstractions;

namespace Ah.State;

public static class StateModule
{
    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    private static IStateService? s_service;

    /// <summary>The state request handler the composition root registered with <see cref="Compose"/>; null before.</summary>
    public static IStateService? Service => Volatile.Read(ref s_service);

    /// <summary>Makes <paramref name="service"/> reachable to the one-shot <c>ahd state</c> verb.</summary>
    public static void Compose(IStateService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        Volatile.Write(ref s_service, service);
    }

    /// <summary>The client every native write goes through.</summary>
    /// <param name="domains">The state domains: op kinds, daemon flags, and the caller-side after-write hooks.</param>
    /// <param name="daemon">The native daemon route: a write goes there when one is up.</param>
    /// <param name="hookErrors">Where a hook write that ran out of budget is logged.</param>
    /// <param name="engine">The in-process engine (domain, op, canonical args, context, operation id, lock wait), used when no daemon is up.</param>
    /// <param name="time">The time source a hook write's budget is measured on; the system's when null.</param>
    /// <param name="mayWriteHook">
    /// Whether a hook-mode write may land in a directory (null: any). Test mode supplies the sandbox check, so a hook run pointed at
    /// a real repository or home writes nothing there.
    /// </param>
    /// <param name="lockDirectory">The directory the engine's state locks live in (the home's with a database); null: each domain's state directory.</param>
    public static IStateWriter CreateClient(
        IReadOnlyList<IStateDomain> domains,
        IDaemonClient daemon,
        IHookErrorLog hookErrors,
        Func<string, string, ReadOnlyMemory<byte>, RequestContext, string, TimeSpan, StateResult> engine,
        TimeProvider? time = null,
        Func<string, bool>? mayWriteHook = null,
        string? lockDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(daemon);
        ArgumentNullException.ThrowIfNull(hookErrors);
        ArgumentNullException.ThrowIfNull(engine);
        return new StateClient(daemon, domains, hookErrors, engine, time ?? TimeProvider.System, mayWriteHook, lockDirectory);
    }

    /// <summary>
    /// The state request handler the daemon's <c>/v1/state</c> route and the one-shot verb share: op lookup, the caller
    /// context and path-argument checks, the read or the write through <paramref name="engine"/>, the after-write hook, and
    /// the answer. The daemon's user is <paramref name="daemonUid"/> (<c>getuid()</c>), never derived from a path.
    /// </summary>
    /// <param name="domains">The state domains.</param>
    /// <param name="engine">The write engine (<see cref="CreateEngine"/>).</param>
    /// <param name="files">Reads the request's directories.</param>
    /// <param name="daemonUid">The daemon's own user id (<c>getuid()</c>), never derived from a path.</param>
    /// <param name="policy">Which hierarchy directories a request may claim, and how a path is resolved.</param>
    /// <param name="hookErrors">Where an internal failure is logged; may be null.</param>
    /// <param name="observer">Told of each write that succeeded, after it is durable; null tells no one.</param>
    public static IStateService CreateService(
        IReadOnlyList<IStateDomain> domains,
        Func<string, string, ReadOnlyMemory<byte>, RequestContext, string, TimeSpan, StateResult> engine,
        IFileReader files,
        uint daemonUid,
        IHierarchyDirPolicy policy,
        IHookErrorLog? hookErrors,
        IStateWriteObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(policy);
        var checks = new StateRequestChecks(files, () => daemonUid, policy);
        return new StateService(domains, engine, checks, hookErrors, observer);
    }

    /// <summary>The daemon's minute line of the write ops run since <paramref name="seen"/> was last updated (each daemon keeps its own), or null when none ran.</summary>
    public static string? TakeOpLine(Dictionary<string, (long Runs, long Ticks)> seen) => StateOpLedger.Take(seen);

    /// <summary>How many times the write op <paramref name="key"/> (<c>domain.op</c>) has run in this process.</summary>
    public static long OpCount(string key) => StateOpLedger.Count(key);

    /// <summary>The in-process read side of a service made by <see cref="CreateService"/>; it is not reachable from the service's routes.</summary>
    public static IInternalStateRead InternalReads(IStateService service) =>
        service as IInternalStateRead ?? throw new ArgumentException("the service is not one made by CreateService", nameof(service));

    /// <summary>
    /// The caller-context check the state service runs before an op, for modules that act on a caller's directories outside a
    /// state call. The daemon's user is <paramref name="daemonUid"/> (<c>getuid()</c>).
    /// </summary>
    public static IRequestContextChecker CreateRequestContextChecker(IFileReader files, uint daemonUid, IHierarchyDirPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(policy);
        return new RequestContextChecker(new StateRequestChecks(files, () => daemonUid, policy));
    }

    /// <summary>
    /// The native writer engine, in the shape <see cref="CreateClient"/> takes: (domain, op, canonical args, context,
    /// operation id, lock wait) to the write's result. It runs the op under the domain's lock, once per operation id.
    /// </summary>
    /// <param name="files">Reads the executed-op log and digest targets.</param>
    /// <param name="writer">The raw writer behind every op's write context.</param>
    /// <param name="appender">The single-<c>write(2)</c> append behind every op's write context and the log.</param>
    /// <param name="locks">The domain locks, in the Node lock protocol.</param>
    /// <param name="clock">Stamps the log rows.</param>
    /// <param name="domains">The domains whose write ops the engine may run.</param>
    /// <param name="processId">The pid in the log's temporary file name.</param>
    /// <param name="dbRunner">Runs an op's database body in one transaction; null leaves every op on the files path.</param>
    /// <param name="modes">Reads a message domain's mode; null skips the files path's mode check.</param>
    /// <param name="dbPath">The path (<c>daemon</c> or <c>direct</c>) recorded with a database write.</param>
    public static Func<string, string, ReadOnlyMemory<byte>, RequestContext, string, TimeSpan, StateResult> CreateEngine(
        IFileReader files,
        IRawFileWriter writer,
        IRecordAppender appender,
        ILockProvider locks,
        IClock clock,
        IReadOnlyList<IStateDomain> domains,
        int processId,
        IDbOpRunner? dbRunner = null,
        IMessageModeReader? modes = null,
        string dbPath = "direct")
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(appender);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(domains);
        var engine = new StateEngine(domains, files, writer, appender, locks, clock, processId, dbRunner, modes, dbPath);
        return engine.Run;
    }
}
