using System.Globalization;
using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>What a board card shows of an item's registry row (§10): its id, its external id and URL, and how many blockers are still open.</summary>
/// <param name="Id">The work id.</param>
/// <param name="ExtId">The external id, or null.</param>
/// <param name="ExtUrl">The external id's URL, or null.</param>
/// <param name="BlockedByOpen">How many of its blockers are still open.</param>
public sealed record WorkCard(string Id, string? ExtId, string? ExtUrl, int BlockedByOpen)
{
    /// <summary>Adds the fields to an item's JSON, each external one only when set.</summary>
    internal void AddTo(JsonObject item)
    {
        item["work_id"] = Id;
        item["blocked_by_open"] = BlockedByOpen;
        if (ExtId is not null)
        {
            item["ext_id"] = ExtId;
        }

        if (ExtUrl is not null)
        {
            item["ext_url"] = ExtUrl;
        }
    }
}

/// <summary>
/// One item's history, <c>item-log:&lt;dirId&gt;/&lt;n&gt;</c>, on demand: what the probe reads from the registry. It is a lease lane rather than a composer because a composer reads only
/// lane records and this reads the database. A key is one held item; a key that is only a directory re-reads every held item of it, since a write can touch several.
/// The same lane named <see cref="DescName"/> serves <c>item-desc:&lt;dirId&gt;/&lt;n&gt;</c>, <c>{id, description}</c> alone, for the cards that show only that.
/// </summary>
internal sealed class ItemLogLane(Func<IReadOnlyList<string>> canonicalDirs, Func<string, bool> subscribed, Func<string, long, JsonObject?> probe, string name = ItemLogLane.LaneName) : IModelLane, ILeaseLane
{
    public const string LaneName = "item-log";

    /// <summary>A waiting card's description slice: small, where the whole log can be 200 x 8 KiB.</summary>
    public const string DescName = "item-desc";

    private readonly string Head = name + ":";

    private readonly object _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    public string Name => name;

    private static string KeyOf(string dir, long n) => dir + ProgressLane.Separator + n.ToString(CultureInfo.InvariantCulture);

    private static (string Dir, long N) Split(string key)
    {
        var at = key.IndexOf(ProgressLane.Separator, StringComparison.Ordinal);
        return (key[..at], long.Parse(key[(at + 1)..], CultureInfo.InvariantCulture));
    }

    private string SliceIdOf(string key)
    {
        var (dir, n) = Split(key);
        return Head + ActivityLane.DirId(dir) + "/" + n.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsDir(string key) => !key.Contains(ProgressLane.Separator, StringComparison.Ordinal);

    public IReadOnlyList<string> SliceIds(string key) => IsDir(key) ? [.. HeldOfDir(key).Select(SliceIdOf)] : [SliceIdOf(key)];

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
            return [.. _held.Where(k => Split(k).Dir == dir)];
        }
    }

    public string? KeyOfToken(string token)
    {
        if (!token.StartsWith(Head, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = token[Head.Length..].Split('/');
        if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return null;
        }

        var dir = canonicalDirs().FirstOrDefault(d => ActivityLane.DirId(d) == parts[0]);
        return dir is null ? null : KeyOf(dir, n);
    }

    public IReadOnlyList<string> LapsedKeys() => [.. Keys().Where(k => !subscribed(SliceIdOf(k)))];

    public LaneResult Reread(string key, long nowMs)
    {
        var changes = new List<SliceChange>();
        var evict = new List<string>();
        foreach (var one in IsDir(key) ? HeldOfDir(key) : [key])
        {
            var sliceId = SliceIdOf(one);
            lock (_gate)
            {
                if (!subscribed(sliceId))
                {
                    _ = _held.Remove(one);
                    evict.Add(sliceId);
                    continue;
                }

                _ = _held.Add(one);
            }

            var (dir, n) = Split(one);
            // No such item is a null body, not no slice: a client asking for it would wait on an answer that never comes.
            changes.Add(new SliceChange(sliceId, JsonText.ToUtf8(probe(dir, n) is { } body ? JsonValue.FromObject(body) : JsonValue.Null, 0), 0));
        }

        return new LaneResult(changes) { Evict = evict };
    }
}
