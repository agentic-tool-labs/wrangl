using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The queued items of one team, <c>queue:&lt;dirId&gt;/&lt;team&gt;</c>: <c>[{item, role, queued_since, stall_at, eligible_members, last_dispatch_at, title, idle_eligible}]</c>, the times as UTC ISO strings
/// and <c>last_dispatch_at</c> null when the role was never sent a dispatch, in display order: the soonest stall first, so whatever has stalled comes first, the longest stalled at the top. It is composed only while a client holds the slice, and the probe is not asked otherwise;
/// a released one is dropped on the next notice. A re-read follows a change of the team's progress, messages or activity, never a timer: a view tells a stall from
/// <c>stall_at</c> and its own clock.
/// </summary>
internal sealed class QueueLane(Func<IReadOnlyList<string>> canonicalDirs, Func<string, bool> subscribed, Func<string, string, double, IReadOnlyList<QueueRow>> probe) : IModelLane, ILeaseLane
{
    public const string LaneName = "queue";

    private const string Head = LaneName + ":";

    private readonly object _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

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

    /// <summary>The held keys of the canonical directory <paramref name="dir"/>.</summary>
    public IReadOnlyList<string> HeldOfDir(string dir)
    {
        lock (_gate)
        {
            return [.. _held.Where(k => ProgressLane.Split(k).Dir == dir)];
        }
    }

    /// <summary>The key of the team whose progress key is <paramref name="progressKey"/>, if it is held.</summary>
    public IReadOnlyList<string> HeldOf(string progressKey)
    {
        lock (_gate)
        {
            return _held.Contains(progressKey) ? [progressKey] : [];
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

        var rows = new JsonArray();
        foreach (var row in probe(dir, folder, nowMs).OrderBy(static r => r.StallAtMs))
        {
            rows.Add(WatchModule.QueueRowToJson(row));
        }

        return new LaneResult([new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromArray(rows), 0), 0)]);
    }
}
