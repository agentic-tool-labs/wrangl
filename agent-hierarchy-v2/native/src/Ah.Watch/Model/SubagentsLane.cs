using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The running subagents of one team, <c>subagents:&lt;dirId&gt;/&lt;team&gt;</c>: <c>{rows: [{agent_id, agent_type, owner, owner_kind, started_at, ttl_s}]}</c>, built the way
/// <see cref="JobsLane"/> is: composed only while a client holds the slice and re-read on each reconcile tick, file facts only. The rows are the started
/// runs of <see cref="IViewSource.SubagentRuns"/>, the ones the native graph reads, whose session is the team's Orchestrator (<c>owner_kind: orchestrator</c>,
/// <c>owner: orchestrator</c>) or a member of the team (<c>owner_kind: member</c>, <c>owner</c> the member's name); a run of any other session is left out.
/// A started run is listed however old it is: <c>ttl_s</c> is the in-flight bound, so a client drops a lapsed run as it ages a row.
/// The peer records' write counter (<c>version</c>, null when it cannot be read) makes the runs read once per write; owners are resolved on every read.
/// </summary>
internal sealed class SubagentsLane(Func<IReadOnlyList<string>> canonicalDirs, Func<string, bool> subscribed, Func<double, IReadOnlyList<SubagentRun>> probe, Func<IRecordView> records, Func<long?>? version = null) : IModelLane, ILeaseLane
{
    public const string LaneName = "subagents";

    private const string Head = LaneName + ":";

    private static readonly double TtlSeconds = Ah.Hierarchy.HierarchyModule.SubagentInflightTtlMs / 1000;

    private readonly object _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private (long Version, IReadOnlyList<SubagentRun> Runs)? _runs;

    public string Name => LaneName;

    private static string SliceIdOf(string dir, string folder) => Head + ProgressLane.SuffixOf(dir, folder);

    public IReadOnlyList<string> SliceIds(string key)
    {
        var (dir, folder) = ProgressLane.Split(key);
        return [SliceIdOf(dir, folder)];
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            return [.. _held];
        }
    }

    public string? KeyOfToken(string token)
    {
        if (!token.StartsWith(Head, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = token[Head.Length..].Split('/');
        if (parts.Length != 2 || parts.Any(static p => p.Length == 0))
        {
            return null;
        }

        var dir = canonicalDirs().FirstOrDefault(d => ActivityLane.DirId(d) == parts[0]);
        return dir is null ? null : ProgressLane.KeyOf(dir, parts[1]);
    }

    public IReadOnlyList<string> LapsedKeys() => [.. Keys().Where(k => !subscribed(SliceIds(k)[0]))];

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, folder) = ProgressLane.Split(key);
        var sliceId = SliceIdOf(dir, folder);
        if (!subscribed(sliceId))
        {
            lock (_gate)
            {
                _ = _held.Remove(key);
            }

            return new LaneResult([]) { Evict = [sliceId] };
        }

        lock (_gate)
        {
            _ = _held.Add(key);
        }

        return new LaneResult([new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(BodyOf(dir, folder, nowMs)), 0), 0)]);
    }

    /// <summary>Every started run: the held ones while the write counter has not moved, else read again.</summary>
    private IReadOnlyList<SubagentRun> Started(double nowMs)
    {
        // read before the runs: a write between the two leaves the older counter held, so the next read reads again
        if (version?.Invoke() is not { } now)
        {
            return Of(probe(nowMs));
        }

        lock (_gate)
        {
            if (_runs is { } held && held.Version == now)
            {
                return held.Runs;
            }
        }

        var runs = Of(probe(nowMs));
        lock (_gate)
        {
            _runs = (now, runs);
        }

        return runs;
    }

    // a run's record says started or stopped; "lapsed" is the source's clock reading of a started one, and the lane leaves the clock to the client
    private static IReadOnlyList<SubagentRun> Of(IReadOnlyList<SubagentRun> runs) => [.. runs.Where(static r => r.Status != "stopped")];

    /// <summary>Drops the held runs, so the next read reads the records (the on-demand rebuild).</summary>
    internal void Forget()
    {
        lock (_gate)
        {
            _runs = null;
        }
    }

    private JsonObject BodyOf(string dir, string folder, double nowMs)
    {
        var rows = new JsonArray();
        var owners = OwnersOf(records(), dir, folder);
        foreach (var run in Started(nowMs))
        {
            if (!owners.TryGetValue(run.SessionId, out var owner))
            {
                continue;
            }

            rows.Add(new JsonObject
            {
                ["agent_id"] = run.AgentId,
                ["agent_type"] = run.AgentType is { } type ? type : JsonValue.Null,
                ["owner"] = owner.Name,
                ["owner_kind"] = owner.Kind,
                ["started_at"] = run.StartedAt,
                ["ttl_s"] = TtlSeconds,
            });
        }

        return new JsonObject { ["rows"] = JsonValue.FromArray(rows) };
    }

    /// <summary>
    /// The team's sessions by owner, as the native graph resolves them: each member's session from its newest registration, and the Orchestrator's from the
    /// team record. A session that is both a member's and the Orchestrator's is the member's.
    /// </summary>
    internal static Dictionary<string, (string Name, string Kind)> OwnersOf(IRecordView records, string dir, string folder)
    {
        var owners = new Dictionary<string, (string Name, string Kind)>(StringComparer.Ordinal);
        if (TreeData.FindTeam(records, ActivityLane.DirId(dir), ProgressLane.TeamNameOf(folder)) is not { } team)
        {
            return owners;
        }

        foreach (var name in team.MemberNames.Where(static n => n is not TreeIds.Orchestrator and not TreeIds.ReservedName))
        {
            if (TreeData.RegistrationOf(records, team, name) is { } registration && LaneParts.Text(registration.Value, "session_id") is { Length: > 0 } session)
            {
                _ = owners.TryAdd(session, (name, "member"));
            }
        }

        if (team.Value.Member("orchestrator") is { IsMap: true } orchestrator && LaneParts.Text(orchestrator, "session_id") is { Length: > 0 } own)
        {
            _ = owners.TryAdd(own, (TreeIds.ReservedName, "orchestrator"));
        }

        return owners;
    }
}
