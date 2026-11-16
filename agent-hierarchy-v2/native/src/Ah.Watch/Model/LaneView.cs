using System.Security.Cryptography;
using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>Where a member's messages go and why: <c>lead</c> (its valid lead), <c>dissolved-lane</c> (its lane is dissolved, so its manager's Orchestrator) or <c>none</c> (its lead is not usable, so its home Orchestrator).</summary>
public sealed record LaneRoute(string Team, string Name, string Via);

/// <summary>
/// The questions about lanes the view cannot answer itself, answered by the host with the code the routing runs: whether a team is the child of
/// a manager's lane, what a lane's child team is called, and where a member's messages go. The view serves the answers and decides nothing.
/// </summary>
public interface ILaneFacts
{
    /// <summary>The stream of <paramref name="managerName"/> that <paramref name="childName"/> is the confirmed child team of, or null.</summary>
    string? ConfirmedStream(string managerName, JsonValue managerRecord, string childName, JsonValue childRecord);

    /// <summary>The name of the child team of <paramref name="stream"/> of <paramref name="manager"/>.</summary>
    string ChildName(string manager, string stream);

    /// <summary>The route of a member of a team in a hierarchy directory, or null when it has no lead and is in no dissolved lane.</summary>
    LaneRoute? RouteOf(string dir, string team, string member);
}

/// <summary>
/// What the team slice says about lanes. A child's own file only claims a parent, so <c>lane</c> is served only when the host confirms the
/// claim against the manager's file in the same directory (the predicate the routing asks); otherwise only <c>lane_claim</c> is.
/// <c>streams</c> and the stream states come from the manager's file; <c>lane.reported</c> is what the sub-orchestrator wrote about itself.
/// </summary>
internal static class LaneView
{
    public const int MaxStreams = 100;

    public static void Apply(JsonObject body, TeamEntry team, IReadOnlyList<TeamEntry> sameDir, ILaneFacts? facts)
    {
        if (team.Value.Member("parent") is { IsMap: true } parent)
        {
            if (LaneParts.Text(parent, "team") is not { } parentTeam)
            {
                return;
            }

            var manager = sameDir.FirstOrDefault(t => t.Name == parentTeam);
            if (manager is not null && facts?.ConfirmedStream(manager.Name, manager.Value, team.Name, team.Value) is { } stream)
            {
                var lane = new JsonObject
                {
                    ["stream"] = stream,
                    ["parent_team"] = parentTeam,
                    ["state"] = manager.Value.Member("streams") is { IsMap: true } streams ? LaneParts.TextOrNull(streams.AsObject().Get(stream) ?? JsonValue.Null, "state") : JsonValue.Null,
                    ["manager_rev"] = LaneParts.TextOrNull(manager.Value, "streams_rev"),
                };
                if (team.Value.Member("workstream") is { IsMap: true } reported)
                {
                    lane["reported"] = reported;
                }

                body["lane"] = lane;
            }
            else
            {
                body["lane_claim"] = new JsonObject { ["parent_team"] = parentTeam };
            }

            return;
        }

        if (team.Value.Member("streams") is not { IsMap: true } rows || rows.AsObject().Keys.Count == 0)
        {
            return;
        }

        var all = rows.AsObject();
        var list = new JsonArray();
        foreach (var name in all.Keys.Order(StringComparer.Ordinal).Take(MaxStreams))
        {
            var record = all[name];
            var row = new JsonObject
            {
                ["name"] = name,
                ["state"] = LaneParts.TextOrNull(record, "state"),
                ["branch"] = LaneParts.TextOrNull(record, "branch"),
                ["base"] = LaneParts.TextOrNull(record, "base"),
                ["sub_orchestrator"] = LaneParts.TextOrNull(record, "sub_orchestrator"),
            };
            if (record.Member("lane") is { IsMap: true } caps)
            {
                row["lane"] = caps;
            }

            var childName = facts?.ChildName(team.Name, name);
            if (childName is not null && sameDir.FirstOrDefault(t => t.Name == childName) is { } child && facts!.ConfirmedStream(team.Name, team.Value, child.Name, child.Value) == name)
            {
                row["child_team"] = childName;
            }

            list.Add(JsonValue.FromObject(row));
        }

        body["streams"] = JsonValue.FromArray(list);
        if (all.Keys.Count > MaxStreams)
        {
            body["streams_total"] = JsonValue.FromNumber(all.Keys.Count);
        }
    }

    /// <summary>A content stamp of a manager's stream records: it changes when any served field of any stream does.</summary>
    public static string Revision(JsonObject streams) => Convert.ToHexString(SHA256.HashData(JsonText.ToUtf8(JsonValue.FromObject(streams), 0)))[..12].ToLowerInvariant();

    /// <summary>
    /// The team records whose slices a change of one team record reaches: the record before and the record after the change (a removed member
    /// is only in the first), and the current records of the manager it names as parent and of the teams that name it as parent, in the same directory.
    /// </summary>
    public static IReadOnlyList<TeamEntry> TeamsAround(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records)
    {
        var found = new List<TeamEntry>();
        var all = TreeData.Teams(records);
        foreach (var record in new[] { previous, current }.Where(static r => r is { IsAbsent: false }))
        {
            var entry = TreeData.Entry(changed.Key, record!);
            found.Add(entry);
            var parent = entry.Value.Member("parent") is { IsMap: true } p ? LaneParts.Text(p, "team") : null;
            found.AddRange(all.Where(t => t.Dir == entry.Dir && t.Name != entry.Name
                && (t.Name == parent || LaneParts.Text(t.Value.Member("parent") ?? JsonValue.Null, "team") == entry.Name)));
        }

        return found;
    }
}
