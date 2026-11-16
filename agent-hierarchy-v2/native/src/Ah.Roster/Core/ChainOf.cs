using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Roster;

/// <summary>Whether a member's chain of command is whole.</summary>
public enum ChainKind
{
    /// <summary>Every link is shown to hold.</summary>
    Up,

    /// <summary>A link is proven broken; <see cref="ChainState.Link"/> names its team.</summary>
    Down,

    /// <summary>No link is proven broken, and at least one cannot be judged. Nothing may be done on this.</summary>
    Unknown,
}

/// <summary>The chain of command of a member: <see cref="Link"/> is the team whose owner link is broken (Down), and <see cref="Why"/> says why.</summary>
public sealed record ChainState(ChainKind Kind, string? Link, string Why);

internal static class ChainWalk
{
    /// <summary>The most teams a chain is followed through; a longer chain is not judged.</summary>
    internal const int MaxDepth = 4;

    /// <summary>
    /// The chain of the members of <paramref name="memberTeam"/>: that team's Orchestrator, and when the team is a valid lane child,
    /// the lane lead's own chain in its manager team, and so on. Only proof breaks a link: an Orchestrator that is orphaned or that
    /// nobody claimed, or one that is a restored session (the registered session with a new pid) whose process <paramref name="ownerHasAhLaunch"/>
    /// shows was not started by ah. An Orchestrator that cannot be judged leaves the chain Unknown, never Down.
    /// <c>ownerHasAhLaunch</c> answers whether the process with a pid was started by ah (true), was not (false), or cannot be told (null).
    /// </summary>
    internal static ChainState Of(
        IRosterCore roster,
        string hierarchyDirectory,
        string memberTeam,
        Func<string, (long Pid, string Start)?>? liveRegistration,
        IProcessTable table,
        DateTimeOffset now,
        int observerPid,
        Func<int, string, string, string, string?, string?, bool?> ownerHasAhLaunch,
        Func<string, bool> isOrchestrateRole,
        IEnumerable<(long Pid, string? Start)>? references = null)
    {
        string? unknown = null;
        var team = memberTeam;
        for (var depth = 1; ; depth++)
        {
            if (depth > MaxDepth)
            {
                return new ChainState(ChainKind.Unknown, null, "chain too deep");
            }

            var record = roster.ReadTeamRecord(hierarchyDirectory, team);
            if (record.IsNull)
            {
                unknown ??= $"{team}: there is no team record";
                break;
            }

            if (!TeamLives.IsClaimed(record.Member("orchestrator")))
            {
                return new ChainState(ChainKind.Down, team, "unclaimed");
            }

            var (life, why) = TeamLives.Judge(record, liveRegistration, table, now, observerPid, references);
            if (life == TeamLife.Orphaned)
            {
                return new ChainState(ChainKind.Down, team, why);
            }

            if (life == TeamLife.Indeterminate)
            {
                unknown ??= $"{team}: {why}";
            }

            var lane = ManagerOfLaneChild(roster, hierarchyDirectory, team, record, isOrchestrateRole);
            if (life != TeamLife.Indeterminate && lane is not null && RestoredBare(roster, hierarchyDirectory, record, lane, liveRegistration, ownerHasAhLaunch))
            {
                return new ChainState(ChainKind.Down, team, "restored-bare");
            }

            if (lane is null)
            {
                break;
            }

            team = lane.Manager;
        }

        return unknown is null ? new ChainState(ChainKind.Up, null, "every link holds") : new ChainState(ChainKind.Unknown, null, unknown);
    }

    /// <summary>The lane leader when <paramref name="record"/> is a valid lane child (its <c>parent</c> a map, a lane leader recorded for it, the manager's current incarnation); else null.</summary>
    private static LaneLeader? ManagerOfLaneChild(IRosterCore roster, string dir, string team, JsonValue record, Func<string, bool> isOrchestrateRole)
    {
        if (record.Member("parent") is not { IsMap: true })
        {
            return null;
        }

        var lane = RosterModule.LaneLeaderOf(roster, dir, team, name => roster.ReadTeamRecord(dir, name), isOrchestrateRole);
        return lane is not null && RosterModule.SameIncarnation(record, roster.ReadTeamRecord(dir, lane.Manager)) ? lane : null;
    }

    /// <summary>
    /// Whether the lane lead of <paramref name="record"/>'s team, registered under the recorded session, runs in a process other than the
    /// one the record names, and that process was not started by ah as the lead's member of the manager team: a lead restored without
    /// ah's flags. The launch is judged against the manager team's file and the lead's member row there, never the child team's.
    /// </summary>
    private static bool RestoredBare(
        IRosterCore roster,
        string dir,
        JsonValue record,
        LaneLeader lane,
        Func<string, (long Pid, string Start)?>? liveRegistration,
        Func<int, string, string, string, string?, string?, bool?> ownerHasAhLaunch)
    {
        if (!(record.Member("orchestrator") is { } orchestrator
            && orchestrator.Member("session_id") is { IsText: true } session && session.AsString().Length > 0
            && JsConvert.AsInteger(orchestrator.Member("pid")) is { } recorded
            && liveRegistration?.Invoke(session.AsString()) is { } registration
            && registration.Pid != recorded))
        {
            return false;
        }

        var row = roster.ReadTeamRecord(dir, lane.Manager).Member("members") is { IsArray: true } members
            ? members.AsArray().FirstOrDefault(m => m.Member("name") is { IsText: true } n && n.AsString() == lane.Member)
            : (JsonValue?)null;
        if (row is not { } member)
        {
            return false;
        }

        string? Text(string key) => member.Member(key) is { IsText: true } t ? t.AsString() : null;
        return ownerHasAhLaunch((int)registration.Pid, Text("agent") ?? Text("role") ?? string.Empty, lane.Member, roster.TeamPath(dir, lane.Manager), Text("model"), Text("effort")) == false;
    }
}
