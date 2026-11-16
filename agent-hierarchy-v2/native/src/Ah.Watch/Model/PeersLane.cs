using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The peers lane: one key per canonical hierarchy directory, one slice per key, <c>peers:&lt;dirId&gt;</c>, whose body maps each member
/// name to the newest peers.jsonl row that names it, reduced to the evidence a verdict reads: <c>{status, pid?, pid_start?, at?, session_id?}</c>.
/// The newest row per member is the one the state reader's <c>peers.latest</c> returns, the same reading the roster verdicts use, so nothing
/// here parses the file. An appended row that leaves every member's newest row as it was produces the same body and publishes nothing.
/// It is an input to the composers; the slice's <c>as_of</c> is 0.
/// </summary>
internal sealed class PeersLane(IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs) : IModelLane
{
    public const string LaneName = "peers";

    public string Name => LaneName;

    public static string SliceIdOf(string dir) => "peers:" + ActivityLane.DirId(dir);

    public IReadOnlyList<string> SliceIds(string key) => [SliceIdOf(key)];

    public IReadOnlyList<string> Keys() => [.. canonicalDirs().Distinct(StringComparer.Ordinal)];

    public LaneResult Reread(string key, long nowMs)
    {
        var id = SliceIdOf(key);
        if (!canonicalDirs().Contains(key, StringComparer.Ordinal))
        {
            return new LaneResult([new SliceChange(id, null)]) { Record = LaneRecord.Absent };
        }

        var rows = reader.Read("peers", "latest", key, new JsonObject { ["dir"] = key });
        var body = new JsonObject();
        if (rows.IsArray)
        {
            foreach (var row in rows.AsArray())
            {
                if (row.Member("name") is not { IsText: true } name || name.AsString().Length == 0)
                {
                    continue;
                }

                var evidence = new JsonObject();
                foreach (var field in new[] { "status", "pid", "pid_start", "session_id" })
                {
                    if (row.Member(field) is { IsNull: false } value)
                    {
                        evidence[field] = value;
                    }
                }

                if (row.Member("ts") is { IsText: true } at)
                {
                    evidence["at"] = at;
                }

                body[name.AsString()] = JsonValue.FromObject(evidence);
            }
        }

        return new LaneResult([new SliceChange(id, JsonText.ToUtf8(body, 0), 0)]) { Record = new LaneRecord(JsonValue.FromObject(body), 0) };
    }
}
