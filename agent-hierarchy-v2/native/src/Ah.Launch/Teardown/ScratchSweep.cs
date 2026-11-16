using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Roster;

namespace Ah.Launch;

/// <summary>
/// The one scratch sweep: <c>ah roster scratch-sweep</c> and the daemon's job both call <see cref="Run"/>. A member's scratch is
/// judged by its own member: it is kept while a readable team record lists the member and does not prove its session gone, and
/// while the record that should list it cannot be read. A directory no record names has no proven owner, so it goes only when
/// nothing under it has changed within the retention.
/// </summary>
internal static class ScratchSweep
{
    /// <summary>How long nothing may have changed in a scratch tree before a sweep takes it.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(72);

    /// <summary>
    /// Finds the sweepable scratch under <paramref name="teamHome"/>, and deletes it when <paramref name="commit"/> is true.
    /// <paramref name="grants"/> is the grants state call (<c>status</c> by team and member), which names a legacy member's tmp.
    /// </summary>
    internal static ScratchSweepReport Run(
        IScratchDirectories scratch,
        ITeardown teardown,
        IRosterCore roster,
        IFileReader files,
        Func<string, JsonObject, JsonValue> grants,
        string dir,
        string teamHome,
        DateTimeOffset now,
        bool commit)
    {
        var teams = roster.ListTeamNames(teamHome).Where(t => !string.IsNullOrEmpty(t)).ToList();
        var records = new Dictionary<string, JsonValue>(StringComparer.Ordinal);

        JsonValue Record(string team)
        {
            if (!records.TryGetValue(team, out var record))
            {
                records[team] = record = roster.ReadTeamRecord(teamHome, team);
            }

            return record;
        }

        static JsonValue? Row(JsonValue record, string member)
        {
            if (record.Member("members") is { IsArray: true } list)
            {
                foreach (var m in list.AsArray())
                {
                    if (m.Member("name") is { IsText: true } n && n.AsString() == member)
                    {
                        return m;
                    }
                }
            }

            return null;
        }

        ScratchOwner Judge(string team, JsonValue row) =>
            JsConvert.ToJsString(teardown.MemberLiveness(dir, row, team).Get("liveness")) == "proven_dead" ? ScratchOwner.ProvenDead : ScratchOwner.Live;

        bool IsMemberName(string name)
        {
            if (teams.Any(t => Row(Record(t), name) is not null))
            {
                return true;
            }

            var legacy = Path.Combine(teamHome, "tmp", name);
            return teams.Any(t =>
            {
                try
                {
                    var status = grants("status", new JsonObject { ["dir"] = teamHome, ["team"] = t, ["member"] = name });
                    return status.IsMap && status.Member("tmp") is { IsMap: true } tmp && tmp.Member("root") is { IsText: true } root && root.AsString() == legacy;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    return false;
                }
            });
        }

        ScratchOwner OwnerOf(string? team, string member)
        {
            if (team is null)
            {
                foreach (var t in teams)
                {
                    if (Row(Record(t), member) is { } listed)
                    {
                        return Judge(t, listed);
                    }
                }

                return ScratchOwner.Unowned;
            }

            var record = Record(team);
            if (record.IsNull)
            {
                return roster.TeamExistsAt(roster.TeamPath(teamHome, team)) ? ScratchOwner.Live : ScratchOwner.Unowned;
            }

            return Row(record, member) is { } row ? Judge(team, row) : ScratchOwner.Unowned;
        }

        return scratch.Sweep(teamHome, now, Retention, IsMemberName, OwnerOf, commit);
    }
}
