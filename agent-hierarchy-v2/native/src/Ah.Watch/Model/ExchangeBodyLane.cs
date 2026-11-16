using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The excerpts of one exchange, <c>exchange-body:&lt;dirId&gt;/&lt;id&gt;</c>: <c>{id, request_excerpt, response_excerpt, excerpt_truncated}</c>.
/// It is composed only while a client holds the slice: an exchange nobody opened is never read and nothing is published for it, and a
/// released one is dropped on the next notice. An exchange that is not in the pool (archived, or never there) is held as <c>{id, gone: true}</c>
/// so the client can say so instead of waiting. It shares its keys with <see cref="MessagesLane"/>, whose rereads tell it to read again.
/// </summary>
internal sealed class ExchangeBodyLane(IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs, Func<string, bool> subscribed) : IModelLane, ILeaseLane
{
    public const string LaneName = "exchange-body";

    private const string Head = LaneName + ":";

    private readonly object _gate = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    public string Name => LaneName;

    public static string SliceIdOf(string dirId, string id) => Head + dirId + "/" + id;

    public IReadOnlyList<string> SliceIds(string key)
    {
        var (dir, id) = MessagesLane.Split(key);
        return [SliceIdOf(ActivityLane.DirId(dir), id)];
    }

    /// <summary>The keys held now: a body is read only after a client asked for it.</summary>
    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            return [.. _held];
        }
    }

    /// <summary>Whether the body of <paramref name="key"/> is held, so a messages notice for an exchange nobody opened costs nothing.</summary>
    public bool Holds(string key)
    {
        lock (_gate)
        {
            return _held.Contains(key);
        }
    }

    public string? KeyOfToken(string token)
    {
        if (!token.StartsWith(Head, StringComparison.Ordinal))
        {
            return null;
        }

        var slash = token.IndexOf('/', Head.Length);
        if (slash < 0 || slash == token.Length - 1)
        {
            return null;
        }

        var dirId = token[Head.Length..slash];
        var dir = canonicalDirs().FirstOrDefault(d => ActivityLane.DirId(d) == dirId);
        return dir is null ? null : MessagesLane.KeyOf(dir, token[(slash + 1)..]);
    }

    public IReadOnlyList<string> LapsedKeys() => [.. Keys().Where(k => !subscribed(SliceIds(k)[0]))];

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, id) = MessagesLane.Split(key);
        var sliceId = SliceIdOf(ActivityLane.DirId(dir), id);
        if (!subscribed(sliceId))
        {
            return Drop(key, sliceId);
        }

        var exchange = reader.Read("msgs", "read", dir, new JsonObject { ["dir"] = dir, ["id"] = id });
        lock (_gate)
        {
            _ = _held.Add(key);
        }

        if (!exchange.IsMap)
        {
            var gone = new JsonObject { ["id"] = id, ["gone"] = true };
            return new LaneResult([new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(gone), 0), 0)]);
        }

        var body = new JsonObject
        {
            ["id"] = id,
            ["request_excerpt"] = exchange.Member("request_excerpt") is { IsText: true } request ? request : JsonValue.Null,
            ["response_excerpt"] = exchange.Member("response_excerpt") is { IsText: true } response ? response : JsonValue.Null,
            ["excerpt_truncated"] = exchange.Member("excerpt_truncated") is { IsBool: true } cut && cut.AsBool(),
        };
        return new LaneResult([new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(body), 0), MessagesLane.AsOf(exchange))]);
    }

    private LaneResult Drop(string key, string sliceId)
    {
        lock (_gate)
        {
            _ = _held.Remove(key);
        }

        return new LaneResult([]) { Evict = [sliceId] };
    }
}
