using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The user-alarms lane (ah2-180 Amendment 2): one key, the stall watch's raised user alarms as its store keeps them (<c>alarm/&lt;team&gt;/&lt;kind&gt;</c>
/// rows with a <c>kind</c> and a <c>line</c>). <paramref name="probe"/> reads that store's text, null when it cannot be read; a cleared alarm's row is gone from it.
/// </summary>
internal sealed class UserAlarmsLane(Func<string?> probe) : IModelLane
{
    public const string LaneName = "user-alarms";

    public const string Key = "alarms";

    private const string KeyPrefix = "alarm/";

    public string Name => LaneName;

    public IReadOnlyList<string> SliceIds(string key) => [];

    public IReadOnlyList<string> Keys() => [Key];

    public LaneResult Reread(string key, long nowMs)
    {
        // a store that cannot be read is no reading: the projector keeps the record it has and reads again
        var text = probe() ?? throw new InvalidOperationException("the stall store could not be read");
        var rows = Rows(text);
        return new LaneResult([]) { Record = rows.Count == 0 ? LaneRecord.Absent : new LaneRecord(JsonValue.FromObject(new JsonObject { ["alarms"] = rows }), 0) };
    }

    /// <summary>The raised alarms in the store text <paramref name="text"/>, each <c>{team, kind, line, raised_at}</c>, ordered by key; an unparsable text holds none.</summary>
    internal static JsonArray Rows(string text)
    {
        var rows = new JsonArray();
        JsonValue doc;
        try
        {
            doc = JsonText.Parse(text);
        }
        catch (JsonParseException)
        {
            return rows;
        }

        if (!doc.IsMap)
        {
            return rows;
        }

        var map = doc.AsObject();
        foreach (var name in map.Keys.Where(static k => k.StartsWith(KeyPrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            if (map.Get(name) is not { IsMap: true } entry
                || LaneParts.Text(entry, "kind") is not { Length: > 0 } kind
                || LaneParts.Text(entry, "line") is not { Length: > 0 } line
                || !name.EndsWith("/" + kind, StringComparison.Ordinal)
                || name.Length <= KeyPrefix.Length + kind.Length + 1)
            {
                continue;
            }

            rows.Add(JsonValue.FromObject(new JsonObject
            {
                ["team"] = name[KeyPrefix.Length..^(kind.Length + 1)],
                ["kind"] = kind,
                ["line"] = line,
                ["raised_at"] = LaneParts.Text(entry, "raised_at") is { } raised ? raised : JsonValue.Null,
            }));
        }

        return rows;
    }

    /// <summary>The raised alarms of <paramref name="team"/> the lane last read; none when it holds no record.</summary>
    public static IReadOnlyList<JsonValue> Of(IRecordView records, string team) =>
        records.Get(LaneName, Key) is { IsAbsent: false } record && record.Value.Member("alarms") is { IsArray: true } alarms
            ? [.. alarms.AsArray().Where(a => LaneParts.Text(a, "team") == team)]
            : [];
}
