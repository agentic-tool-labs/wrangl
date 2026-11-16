using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The Sitrep of one team, <c>sitrep:&lt;dirId&gt;/&lt;team&gt;</c> (ah-ui-ia §3.3, §3.3a): Completed, In progress and Up next across the team and its confirmed lanes.
/// It is composed only while a client holds the slice. The probe answers the body and the progress folders it read (the team and its lane teams), and a
/// change to the progress of any of them (a plan, checklist or ledger write), to the messages of the directory or to the backlog re-reads it; never a timer.
/// </summary>
internal sealed class SitrepLane(
    Func<IReadOnlyList<string>> canonicalDirs, Func<string, bool> subscribed, Func<string, string, double, (JsonValue Body, IReadOnlyList<string> Teams)> probe) : IModelLane, ILeaseLane
{
    public const string LaneName = "sitrep";

    private const string Head = LaneName + ":";

    private readonly object _gate = new();

    /// <summary>Each held key with the progress folders its last read covered.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _held = new(StringComparer.Ordinal);

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
            return [.. _held.Keys];
        }
    }

    /// <summary>The held keys of the canonical directory <paramref name="dir"/>.</summary>
    public IReadOnlyList<string> HeldOfDir(string dir)
    {
        lock (_gate)
        {
            return [.. _held.Keys.Where(k => ProgressLane.Split(k).Dir == dir)];
        }
    }

    /// <summary>The held keys whose last read covered the progress folder of <paramref name="progressKey"/>: its own team, or a lane team of it.</summary>
    public IReadOnlyList<string> HeldCovering(string progressKey)
    {
        var (dir, folder) = ProgressLane.Split(progressKey);
        lock (_gate)
        {
            return [.. _held.Where(h => h.Key == progressKey || (ProgressLane.Split(h.Key).Dir == dir && h.Value.Contains(folder, StringComparer.Ordinal))).Select(static h => h.Key)];
        }
    }

    /// <summary>Asks for a re-read of every held key.</summary>
    public void EmitHeld(Action<string, string> emit)
    {
        foreach (var key in Keys())
        {
            emit(LaneName, key);
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

        var (body, teams) = probe(dir, folder, nowMs);
        lock (_gate)
        {
            _held[key] = teams;
        }

        return new LaneResult([new SliceChange(sliceId, JsonText.ToUtf8(body, 0), 0)]);
    }
}
