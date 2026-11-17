using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>The in-process holder of the watcher's coverage (ah2-102 F2): no file, so a daemon that starts has an empty set.</summary>
internal sealed class WatchCoverage : IWatchCoverage
{
    private readonly object _gate = new();
    private HashSet<string> _sessions = new(StringComparer.Ordinal);

    /// <summary>Told after a publish that changed the set; the read model sets it to re-read the coverage lane.</summary>
    public Action? Changed { get; set; }

    public IReadOnlySet<string> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions;
            }
        }
    }

    public void Publish(IReadOnlySet<string> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        lock (_gate)
        {
            if (_sessions.SetEquals(sessions))
            {
                return;
            }

            _sessions = new HashSet<string>(sessions, StringComparer.Ordinal);
        }

        Changed?.Invoke();
    }
}

/// <summary>
/// The coverage lane: one key, no slices of its own. Its record is <c>{sessions: [..]}</c>, the session ids the watcher's blocked pass reads,
/// for <see cref="SummaryData.Members"/> (ah2-102 F5). A change re-composes every team summary.
/// </summary>
internal sealed class CoverageLane(IWatchCoverage coverage) : IModelLane
{
    public const string LaneName = "coverage";

    public const string Key = "watch";

    public string Name => LaneName;

    public IReadOnlyList<string> SliceIds(string key) => [];

    public IReadOnlyList<string> Keys() => [Key];

    public LaneResult Reread(string key, long nowMs)
    {
        var sessions = new JsonArray();
        foreach (var session in coverage.Sessions.Order(StringComparer.Ordinal))
        {
            sessions.Add(session);
        }

        return new LaneResult([]) { Record = new LaneRecord(JsonValue.FromObject(new JsonObject { ["sessions"] = JsonValue.FromArray(sessions) }), 0) };
    }

    /// <summary>Whether <paramref name="session"/> is in the coverage the lane last read; false when the lane holds no record.</summary>
    public static bool Covers(IRecordView records, string session) =>
        records.Get(LaneName, Key) is { IsAbsent: false } record && record.Value.Member("sessions") is { IsArray: true } sessions
        && sessions.AsArray().Any(s => s.IsText && s.AsString() == session);
}
