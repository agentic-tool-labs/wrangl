using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.DaemonClient;

public static class DaemonClientModule
{
    private static IDaemonLifecycle? s_lifecycle;

    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    /// <summary>The lifecycle verbs, once the composition root has called <c>Compose</c>; null before.</summary>
    public static IDaemonLifecycle? Lifecycle => Volatile.Read(ref s_lifecycle);

    /// <summary>Builds the daemon client and the lifecycle verbs from <paramref name="dependencies"/> and makes the verbs reachable through <see cref="Lifecycle"/>.</summary>
    public static IDaemonClient Compose(DaemonClientDependencies dependencies) => Compose(dependencies, null);

    /// <summary>
    /// As <see cref="Compose(DaemonClientDependencies)"/>, with the answer <c>status</c> reads from the files when no daemon
    /// serves a path: <paramref name="inspectFiles"/> is given an inspect path (<c>/v1/inspect/registrations</c>, <c>/boxes</c>,
    /// <c>/timers</c>) and returns that document, or null for a path it does not know.
    /// </summary>
    public static IDaemonClient Compose(DaemonClientDependencies dependencies, Func<string, CancellationToken, Task<JsonObject?>>? inspectFiles)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var client = new AhdClient(dependencies);
        Volatile.Write(ref s_lifecycle, new AhdLifecycle(client, inspectFiles));
        return client;
    }

    /// <summary>
    /// The <c>ahd-heal</c> row of <c>roster doctor</c>, read from <c>run/heal-state.json</c> and <c>run/heal.pid</c> only; it starts
    /// nothing. <c>error</c>: the last heal was capped, could not kill ahd, or its restart did not answer. <c>warn</c>: heal restarted
    /// ahd in the last 24 h. <c>error</c> too when the state file cannot be read: heal sends no signal then. <c>ok</c> otherwise, saying when a heal is running now.
    /// </summary>
    public static (string Level, string Detail) HealDoctorRow(IFileReader files, IProcessInspector inspector, string ahHome, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(inspector);
        var run = Path.Combine(ahHome, "run");
        if (HealStateFile.Read(files, Path.Combine(run, "heal-state.json")) is not { } state)
        {
            return ("error", "the heal state file run/heal-state.json cannot be read, so ahd heal cannot judge its restart cap and sends no signal; "
                + "look at the file, then only you can run `ahd heal --force`, which starts a fresh one");
        }

        var keep = HealTiming.Production.StateKeep;
        var recent = state.Restarts.Count(t => now - t < keep);
        var last = state.Last is { } l && now - l.At < keep ? l : null;
        if (last is { Kind: "capped" or "failed-unkillable" or "failed-not-killed" or "failed-restart" })
        {
            return ("error", $"ahd heal {last.Kind}: {last.Detail}"
                + (last.LogCopy.Length > 0 ? $"; the hung daemon's log is copied to {last.LogCopy}" : string.Empty)
                + "; only you can run `ahd heal --force`, which skips the cap and never the proof");
        }

        var running = HealPid(files, run) is { } pid && inspector.PidAlive(pid) ? $"; a heal is running now (pid {pid})" : string.Empty;
        return recent > 0
            ? ("warn", $"ahd heal restarted a hung ahd {recent} time(s) in the last 24 h (cap {HealTiming.Production.MaxRestarts}){(last is null ? string.Empty : "; last: " + last.Detail)}{running}")
            : ("ok", "no heal has restarted ahd in 24 h; `ahd heal` judges a daemon that does not answer and replaces it only when it is proven hung" + running);
    }

    internal static int? HealPid(IFileReader files, string run)
    {
        try
        {
            var path = Path.Combine(run, "heal.pid");
            return files.Exists(path) && int.TryParse(Ah.Core.Text.Utf8Text.Decode(files.ReadBytes(path)).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) ? pid : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the running daemon's health lists <paramref name="sessionId"/> among the sessions whose timers it owns. False when
    /// no daemon is running (none is started), when it does not answer within <paramref name="timeout"/>, or when its answer
    /// carries no such list.
    /// </summary>
    public static async Task<bool> OwnsTimersAsync(
        IDaemonClient client, string sessionId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var reply = await client
            .RequestAsync(new DaemonRequest("GET", "/v1/health", Timeout: timeout, StartIfDown: false), cancellationToken)
            .ConfigureAwait(false);
        if (reply.Status != 200 || !JsonText.TryParse(reply.Body.Span, out var body))
        {
            return false;
        }

        if (body.Member("owns")?.Member("timers") is not { IsArray: true } timers)
        {
            return false;
        }

        foreach (var entry in timers.AsArray())
        {
            if (entry.IsText && entry.AsString() == sessionId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The marker a failed daemon spawn left while it is fresh: why it failed and when clients try again; null otherwise.</summary>
    public static (string Reason, string RetryAt)? ReadSpawnFailed(IDaemonClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client is AhdClient ahd && ahd.ReadSpawnFailed() is { } marker
            ? (marker.Str("reason") ?? "unknown", marker.Str("retry_at") ?? string.Empty)
            : null;
    }

    /// <summary>Orders two dotted versions: negative when <paramref name="a"/> is older, positive when newer, 0 when equal.</summary>
    public static int CompareVersions(string a, string b) => Versions.Compare(a, b);

    /// <summary>The query of one slice-feed poll, appended to the literal route <c>/v1/view?path=%2Fevents</c>.</summary>
    public static string SliceEventsQuery(IReadOnlyCollection<string> sub, IReadOnlyCollection<string> fresh, long since, string? epoch, int waitSeconds) =>
        SliceQuery.Events(sub, fresh, since, epoch, waitSeconds);

    /// <summary><paramref name="value"/> as a URL's form encoding makes it.</summary>
    public static string FormEncode(string value) => SliceQuery.FormEncode(value);

    /// <summary>
    /// A client that can find a daemon and ask it for things but never start, stop or write: its writers, locks and spawner do nothing
    /// or refuse, and the lifecycle verbs are not made reachable. For the terminal view, which only reads.
    /// </summary>
    public static IDaemonClient ComposeReadOnly(
        IFileReader files,
        IUnixHttpTransport transport,
        IProcessInspector inspector,
        IProcessRunner processes,
        IClock clock,
        IAhEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(environment);
        return new AhdClient(new DaemonClientDependencies(
            files,
            new InertWriter(),
            new InertAppender(),
            new InertLocks(),
            transport,
            new RefusingSpawner(),
            processes,
            inspector,
            clock,
            environment,
            new Dictionary<string, string?>(),
            null));
    }
}
