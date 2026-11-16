using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Hierarchy;

/// <summary>A request as the charter rule sees it: its id, the role and name it is addressed to, the team and time it was written in, and whether its sender withdrew or closed it.</summary>
internal readonly record struct CharterFact(string Id, string To, string? ToName, string? Team, string? Created, bool Closed);

/// <summary>One lane: the sub-orchestrator of a stream of a manager team, and whether the lane is still running.</summary>
internal readonly record struct LaneEntry(string Manager, string Stream, string SubOrchestrator, string? Role, string? Opened, bool Open);

/// <summary>
/// The lanes of a pool and the rule that picks each lane's charter: the oldest request addressed to the lane's sub-orchestrator.
/// A charter stays open for as long as its lane runs, whatever responses it has been given.
/// </summary>
internal sealed class LaneCharters(IReadOnlyList<LaneEntry> lanes)
{
    public static readonly LaneCharters None = new([]);

    public bool IsEmpty => lanes.Count == 0;

    /// <summary>Reads the team files of <paramref name="dirs"/> once and keeps the lanes they declare. Only a file naming a sub-orchestrator or a parent is parsed.</summary>
    public static LaneCharters Build(IFileReader files, IReadOnlyList<string> dirs, IRosterCore? roster = null) => FromDocs(ReadDocs(files, dirs, roster));

    /// <summary>The teams of <paramref name="dirs"/> that can declare a lane, parsed, by name, from the rows once the database holds them: the input of <see cref="FromDocs"/>.</summary>
    public static List<(string Name, JsonValue Doc)> ReadDocs(IFileReader files, IReadOnlyList<string> dirs, IRosterCore? roster = null)
    {
        var teams = new List<(string Name, JsonValue Doc)>();
        foreach (var dir in dirs)
        {
            if (roster?.TeamNamesIn(dir) is { } names)
            {
                foreach (var name in names)
                {
                    var doc = roster.ReadTeamFileAt(NodePath.Join(dir, "teams", name + ".json"));
                    if (!doc.IsNull && (doc.Member("parent") is not null || JsonText.Stringify(doc, 0).Contains("sub_orchestrator", StringComparison.Ordinal)))
                    {
                        teams.Add((name, doc));
                    }
                }

                continue;
            }

            var folder = NodePath.Join(dir, "teams");
            if (!files.Exists(folder))
            {
                continue;
            }

            foreach (var file in files.List(folder))
            {
                if (!file.EndsWith(".json", StringComparison.Ordinal))
                {
                    continue;
                }

                string text;
                try
                {
                    text = files.ReadText(NodePath.Join(folder, file));
                }
                catch (Exception)
                {
                    continue;
                }

                if (!text.Contains("sub_orchestrator", StringComparison.Ordinal) && !text.Contains("\"parent\"", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    teams.Add((file[..^".json".Length], JsonText.Parse(text)));
                }
                catch (JsonParseException)
                {
                    // an unreadable team file declares no lane
                }
            }
        }

        return teams;
    }

    /// <summary>The lanes the parsed team files <paramref name="teams"/> declare, in their order.</summary>
    public static LaneCharters FromDocs(IReadOnlyList<(string Name, JsonValue Doc)> teams)
    {
        var doneChildren = new HashSet<(string Team, string Stream)>();
        foreach (var (_, doc) in teams)
        {
            if (doc.Member("parent") is { IsMap: true } p && p.Member("team") is { IsText: true } pt && p.Member("stream") is { IsText: true } ps
                && doc.Member("workstream")?.Member("state") is { IsText: true } ws && ws.AsString() == "done")
            {
                _ = doneChildren.Add((pt.AsString(), ps.AsString()));
            }
        }

        var lanes = new List<LaneEntry>();
        foreach (var (name, doc) in teams)
        {
            if (!doc.IsMap || doc.Member("streams") is not { IsMap: true } streams)
            {
                continue;
            }

            foreach (var stream in streams.AsObject().Keys)
            {
                var record = streams.AsObject()[stream];
                if (record.Member("sub_orchestrator") is not { IsText: true } so || so.AsString().Length == 0)
                {
                    continue;
                }

                var streamClosed = record.Member("state") is { IsText: true } st && st.AsString() == "done";
                var childDone = doneChildren.Contains((name, stream));
                var role = doc.Member("members") is { IsArray: true } rows
                    ? rows.AsArray().Select(r => r.Member("name") is { IsText: true } n && n.AsString() == so.AsString() && r.Member("role") is { IsText: true } rt ? rt.AsString() : null).FirstOrDefault(r => r is not null)
                    : null;
                var opened = record.Member("opened") is { IsText: true } op && op.AsString().Length > 0 ? op.AsString() : null;
                lanes.Add(new LaneEntry(name, stream, so.AsString(), role, opened, !streamClosed && !childDone));
            }
        }

        return new LaneCharters(lanes);
    }

    private static DateTimeOffset? Instant(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    /// <summary>Whether a request addressed to role <paramref name="to"/> can be the charter of a running lane; the rest need not be read.</summary>
    public bool CouldBeCharter(string to) => lanes.Any(l => l.Open && (to == "sub-orchestrator" || to == l.Role));

    /// <summary>
    /// The ids of the requests that are the charter of a lane that is still running: per lane, the oldest request written in the manager team,
    /// addressed to its sub-orchestrator and at or after the stream opened, whatever its state; that request is reported closed when its sender withdrew or closed it, and no later request takes its place.
    /// </summary>
    public IReadOnlySet<string> OpenCharterIds(IReadOnlyList<CharterFact> requests)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var created = requests.Select(static r => Instant(r.Created)).ToArray();
        foreach (var lane in lanes.Where(l => l.Open))
        {
            var opened = Instant(lane.Opened);
            string? oldest = null;
            var oldestClosed = false;
            for (var i = 0; i < requests.Count; i++)
            {
                var r = requests[i];
                if (Takes(lane, opened, r, created[i]) && (oldest is null || string.CompareOrdinal(r.Id, oldest) < 0))
                {
                    oldest = r.Id;
                    oldestClosed = r.Closed;
                }
            }

            if (oldest is not null && !oldestClosed)
            {
                _ = ids.Add(oldest);
            }
        }

        return ids;
    }

    /// <summary>
    /// <see cref="OpenCharterIds(IReadOnlyList{CharterFact})"/> where <paramref name="candidates"/> gives each running lane's possible charters
    /// in ascending id order, a superset of the requests that rule takes for that lane: the first it takes is the oldest, and no later one is read.
    /// </summary>
    public IReadOnlySet<string> OpenCharterIds(Func<LaneEntry, IEnumerable<CharterFact>> candidates)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in lanes.Where(l => l.Open))
        {
            var opened = Instant(lane.Opened);
            foreach (var r in candidates(lane))
            {
                if (Takes(lane, opened, r, Instant(r.Created)))
                {
                    if (!r.Closed)
                    {
                        _ = ids.Add(r.Id);
                    }

                    break;
                }
            }
        }

        return ids;
    }

    /// <summary>The roles a request can be addressed to and still be a running lane's charter (<see cref="CouldBeCharter"/>).</summary>
    public IReadOnlyList<string> CharterRoles => ["sub-orchestrator", .. lanes.Where(static l => l.Open && l.Role is not null && l.Role != "sub-orchestrator").Select(static l => l.Role!).Distinct(StringComparer.Ordinal)];

    /// <summary>When <paramref name="lane"/> opened, in Unix milliseconds; null when it has no time that parses.</summary>
    public static long? OpenedMs(LaneEntry lane) => Instant(lane.Opened)?.ToUnixTimeMilliseconds();

    private static bool Takes(LaneEntry lane, DateTimeOffset? opened, CharterFact r, DateTimeOffset? created)
    {
        var addressed = r.ToName is { } n ? n == lane.SubOrchestrator : r.To == "sub-orchestrator";
        var afterOpen = created is not null && (opened is null || created >= opened);
        return addressed && r.Team == lane.Manager && afterOpen;
    }
}
