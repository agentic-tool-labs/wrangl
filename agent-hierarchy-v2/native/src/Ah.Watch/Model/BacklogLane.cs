using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Model;

/// <summary>What the backlog slice of one hierarchy directory is made from: its items in list order, its groom health (null when it cannot be read) and the origin URL issue links are built on.</summary>
/// <param name="Items">The backlog items, in list order.</param>
/// <param name="Health">The backlog's groom health, or null when the database cannot be read.</param>
/// <param name="Origin">The origin remote's URL, or null.</param>
public sealed record BacklogFacts(IReadOnlyList<PlanItem> Items, BacklogHealth? Health, string? Origin)
{
    /// <summary>The backlog keys planned by the directory's checklist items: the rows <c>HierarchyModule.PlannedBacklogKeys</c> names.</summary>
    public IReadOnlyList<string> PlannedParents { get; init; } = [];

    /// <summary>The registered rows' board fields, by item key.</summary>
    public IReadOnlyDictionary<string, WorkCard> Work { get; init; } = new Dictionary<string, WorkCard>();
}

/// <summary>
/// The backlog of one hierarchy directory, <c>backlog:&lt;dirId&gt;</c>: <c>{items: [{key, title, state, holder?, wave?, size?, theme?, rank?, outcome?, ext?, issue?: {url}, work_id?, blocked_by_open?, ext_id?, ext_url?}],
/// health: {untriaged, stale, due, due_since?}}</c>, each optional field sent only when set. It is composed only while a client holds the slice,
/// re-read on each backlog write, on each watcher overflow and every <see cref="RefreshPeriod"/> while a client is connected, since its groom health is derived from the time.
/// </summary>
internal sealed class BacklogLane(Func<IReadOnlyList<string>> canonicalDirs, Func<string, bool> subscribed, Func<string, long, BacklogFacts> probe) : IModelLane, ILeaseLane
{
    public const string LaneName = "backlog";

    private const string Head = LaneName + ":";

    private readonly object _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    public string Name => LaneName;

    private static string SliceIdOf(string dir) => Head + ActivityLane.DirId(dir);

    public IReadOnlyList<string> SliceIds(string key) => [SliceIdOf(key)];

    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            return [.. _held];
        }
    }

    public string? KeyOfToken(string token)
    {
        if (!token.StartsWith(Head, StringComparison.Ordinal) || token.Length == Head.Length)
        {
            return null;
        }

        var dirId = token[Head.Length..];
        return canonicalDirs().FirstOrDefault(d => ActivityLane.DirId(d) == dirId);
    }

    /// <summary>How often a held backlog is re-read while a client is connected.</summary>
    public static readonly TimeSpan RefreshPeriod = TimeSpan.FromMinutes(1);

    /// <summary>Emits every held key, so its time-derived health is recomposed.</summary>
    public void EmitHeld(Action<string, string> emit)
    {
        foreach (var key in Keys())
        {
            emit(LaneName, key);
        }
    }

    public IReadOnlyList<string> LapsedKeys() => [.. Keys().Where(k => !subscribed(SliceIdOf(k)))];

    public LaneResult Reread(string key, long nowMs)
    {
        var sliceId = SliceIdOf(key);
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

        return new LaneResult([new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(BodyOf(probe(key, nowMs))), 0), 0)]);
    }

    /// <summary>The wave an item is in: one of the three a groom sets, whatever its case, else untriaged.</summary>
    internal static string WaveOf(string? wave) =>
        wave?.ToLowerInvariant() is { } known && known is "now" or "next" or "later" ? known : "untriaged";

    /// <summary>Keys in the order people read them: <c>b9</c> before <c>b10</c>, whatever the case.</summary>
    internal static readonly Comparer<string> NaturalOrder = Comparer<string>.Create(static (a, b) =>
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var (startA, startB) = (i, j);
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }

                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }

                var (x, y) = (a[startA..i].TrimStart('0'), b[startB..j].TrimStart('0'));
                var byNumber = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
                if (byNumber != 0)
                {
                    return byNumber;
                }

                continue;
            }

            var byChar = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (byChar != 0)
            {
                return byChar;
            }

            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    });

    /// <summary>The rows the Board draws as a tier rather than in Backlog: each row an open checklist item names as parent, and that row's ancestors.</summary>
    private static HashSet<string> PlannedOf(BacklogFacts facts)
    {
        var parentOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in facts.Items)
        {
            parentOf[item.Item] = item.Parent;
        }

        var planned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parent in facts.PlannedParents)
        {
            // Add is false on a key already walked, so a cycle in the file ends here.
            for (var key = parent; key is not null && planned.Add(key);)
            {
                key = parentOf.GetValueOrDefault(key);
            }
        }

        return planned;
    }

    internal static JsonObject BodyOf(BacklogFacts facts)
    {
        var items = new JsonArray();
        var open = 0;
        var planned = PlannedOf(facts);
        foreach (var item in facts.Items.OrderBy(static i => i.Rank ?? double.MaxValue).ThenBy(static i => i.Item, NaturalOrder))
        {
            var isOpen = item.State is not ("done" or "dropped");
            open += isOpen ? 1 : 0;
            var json = new JsonObject { ["key"] = item.Item, ["title"] = item.Title, ["state"] = item.State, ["open"] = isOpen, ["wave"] = WaveOf(item.Wave), ["planned"] = planned.Contains(item.Item) };
            void Set(string name, string? value)
            {
                if (value is not null)
                {
                    json[name] = value;
                }
            }

            Set("holder", item.Holder);
            Set("size", item.Size);
            Set("theme", item.Area);
            if (item.Rank is { } rank)
            {
                json["rank"] = rank;
            }

            Set("outcome", item.Outcome);
            Set("ext", item.Ext);
            Set("kind", item.Kind);
            Set("parent", item.Parent);
            if (item.Issue is { } number && PlanLinkage.IssueUrl(facts.Origin, number) is { } url)
            {
                json["issue"] = new JsonObject { ["url"] = url };
            }

            facts.Work.GetValueOrDefault(item.Item)?.AddTo(json);
            items.Add(json);
        }

        var health = new JsonObject { ["untriaged"] = 0, ["stale"] = 0, ["due"] = false };
        if (facts.Health is { } known)
        {
            var short_ = HierarchyModule.BacklogHealthJson(known, full: false);
            foreach (var name in (string[])["untriaged", "stale", "due", "due_since"])
            {
                if (short_.Get(name) is { IsNull: false } value)
                {
                    health[name] = value;
                }
            }
        }

        return new JsonObject { ["items"] = JsonValue.FromArray(items), ["open"] = open, ["total"] = items.Count, ["health"] = health };
    }
}
