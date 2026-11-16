using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Model;

/// <summary>
/// The items of one stream, <c>plan-items:&lt;dirId&gt;/&lt;team&gt;/&lt;stream&gt;</c>: <c>{stream, items: [{key, title, state, holder?, done_seen_at, work, quiet_at?, column, checklist_rank, work_id?, blocked_by_open?, ext_id?, ext_url?}], truncated}</c>.
/// It is composed only while a client holds the slice; a released one is dropped on the next notice. The items are the ones the progress lane
/// already holds, so only the registry fields cost a read, and the lane re-reads a held stream whenever the progress lane has read its team again, on an exchange write of its
/// directory, and when who is working in the team changes, since each moves an item's work and column. <c>done</c> is the column of every done item seen done: "today" is the viewer's to decide.
/// </summary>
internal sealed class PlanItemsLane(ProgressLane progress, Func<IReadOnlyList<string>> canonicalDirs, Func<string, bool> subscribed, Func<string, string, long, ChecklistLiveness?> liveness, Func<string, string, string, IReadOnlyDictionary<string, WorkCard>>? work = null) : IModelLane, ILeaseLane
{
    public const string LaneName = "plan-items";

    private const string Head = LaneName + ":";

    private readonly object _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    public string Name => LaneName;

    public static string KeyOf(string progressKey, string stream) => progressKey + ProgressLane.Separator + stream;

    private static (string Dir, string Team, string Stream) Split(string key)
    {
        var (dir, rest) = ProgressLane.Split(key);
        var at = rest.IndexOf(ProgressLane.Separator, StringComparison.Ordinal);
        return (dir, rest[..at], rest[(at + 1)..]);
    }

    private static string SliceIdOf(string dir, string team, string stream) => Head + ProgressLane.SuffixOf(dir, team) + "/" + stream;

    public IReadOnlyList<string> SliceIds(string key)
    {
        var (dir, team, stream) = Split(key);
        return [SliceIdOf(dir, team, stream)];
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            return [.. _held];
        }
    }

    /// <summary>The held keys of the team whose progress key is <paramref name="progressKey"/>.</summary>
    public IReadOnlyList<string> HeldOf(string progressKey)
    {
        var head = progressKey + ProgressLane.Separator;
        lock (_gate)
        {
            return [.. _held.Where(k => k.StartsWith(head, StringComparison.Ordinal))];
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
        if (parts.Length != 3 || parts.Any(static p => p.Length == 0))
        {
            return null;
        }

        var dir = canonicalDirs().FirstOrDefault(d => ActivityLane.DirId(d) == parts[0]);
        return dir is null ? null : KeyOf(ProgressLane.KeyOf(dir, parts[1]), parts[2]);
    }

    public IReadOnlyList<string> LapsedKeys() => [.. Keys().Where(k => !subscribed(SliceIds(k)[0]))];

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, team, stream) = Split(key);
        var sliceId = SliceIdOf(dir, team, stream);
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

        var body = progress.ItemsBodyOf(ProgressLane.SuffixOf(dir, team), stream)
            ?? JsonValue.FromObject(new JsonObject { ["stream"] = stream, ["items"] = new JsonArray(), ["truncated"] = false });
        return new LaneResult([new SliceChange(sliceId, JsonText.ToUtf8(Placed(body, liveness(dir, team, nowMs), nowMs, work?.Invoke(dir, team, stream)), 0), 0)]);
    }

    private static double? Ms(JsonValue item, string field) =>
        item.Member(field) is { IsText: true } at && DateTimeOffset.TryParse(at.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when.ToUnixTimeMilliseconds() : null;

    /// <summary>The body with each item's <c>work</c>, <c>column</c> and <c>checklist_rank</c>; a stale item carries the <c>quiet_at</c> that hides it unless finished work is asked for.</summary>
    internal static JsonValue Placed(JsonValue body, ChecklistLiveness? live, double nowMs, IReadOnlyDictionary<string, WorkCard>? cards = null)
    {
        var items = new JsonArray();
        foreach (var item in body.Member("items")!.Value.AsArray())
        {
            var entry = new JsonObject();
            foreach (var name in item.AsObject().Keys)
            {
                entry[name] = item.Member(name)!.Value;
            }

            var state = item.Member("state")?.NonEmptyText;
            var seen = item.Member("done_seen_at") is { IsNumber: true } at && at.AsNumber() > 0;
            var last = Ms(item, "updated_at") ?? Ms(item, "state_since");
            var refs = item.Member("refs") is { IsArray: true } listed ? listed.AsArray().Select(static r => r.AsString()).ToList() : null;
            var work = HierarchyModule.ChecklistWork(state ?? string.Empty, item.Member("owner")?.NonEmptyText, refs, live, last, nowMs);
            entry["work"] = work is null ? JsonValue.Null : work;
            if (work == "stale" && last is { } quiet)
            {
                entry["quiet_at"] = SummaryData.Iso((long)quiet + (long)HierarchyModule.ChecklistStaleMs);
            }

            entry["column"] = ColumnOf(state, seen, work) is { } column ? column : JsonValue.Null;
            entry["checklist_rank"] = ChecklistRank(state) is { } rank ? rank : JsonValue.Null;
            if (item.Member("key")?.NonEmptyText is { } key)
            {
                cards?.GetValueOrDefault(key)?.AddTo(entry);
            }

            items.Add(entry);
        }

        return JsonValue.FromObject(new JsonObject { ["stream"] = body.Member("stream")!.Value, ["items"] = items, ["truncated"] = body.Member("truncated")!.Value });
    }

    /// <summary>The Board column of an item, from its <paramref name="work"/> while it has one (spec ah-ui-focus §5); null for one the Board does not show. In progress is <c>working</c> and nothing else.</summary>
    internal static string? ColumnOf(string? state, bool doneSeen, string? work) => work switch
    {
        "working" => "progress",
        "with_lead" => "review",
        "waiting_you" => "waiting",
        "blocked" or "no_one" or "stale" => "stuck",
        _ => state switch
        {
            "not_started" or "next" => "next",
            "done" when doneSeen => "done",
            _ => null,
        },
    };

    /// <summary>An open item's place in the Orchestrator's checklist, lowest first; null for one that is not on it.</summary>
    internal static int? ChecklistRank(string? state) => state switch
    {
        "waiting_orch" or "reported" => 0,
        "blocked" => 1,
        "waiting_user" => 2,
        "in_progress" => 3,
        "in_review" => 4,
        "testing" => 5,
        "approved" => 6,
        "next" or "not_started" => 7,
        _ => null,
    };
}
