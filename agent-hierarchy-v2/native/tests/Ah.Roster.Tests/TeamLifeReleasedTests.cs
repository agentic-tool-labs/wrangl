using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform;

namespace Ah.Roster.Tests;

/// <summary>The Orchestrator's released marker orphans a team only for the incarnation it names.</summary>
public sealed class TeamLifeReleasedTests
{
    private const string OtherStart = "Mon Jan  1 00:00:00 2001";
    private static readonly int Self = Environment.ProcessId;
    private static readonly IProcessTable Table = PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner());
    private static readonly string SelfStart = Table.StartTime(Self)!;

    private static JsonValue Team(int pid, string? start, string? session = null)
    {
        var orchestrator = new JsonObject { ["session_id"] = session is null ? JsonValue.Null : session, ["pid"] = (double)pid };
        if (start is not null)
        {
            orchestrator["pid_start"] = start;
        }

        return new JsonObject { ["version"] = 1, ["members"] = new JsonArray(), ["orchestrator"] = orchestrator };
    }

    private static Func<string, (long Pid, string Start)?> Registered(string session, long pid, string start) =>
        s => s == session ? (pid, start) : null;

    private static JsonValue Released(JsonValue team, long pid, string? start)
    {
        var marker = new JsonObject { ["pid"] = (double)pid, ["at"] = "2026-10-24T12:00:00.000Z" };
        if (start is not null)
        {
            marker["pid_start"] = start;
        }

        var orchestrator = team.Member("orchestrator")!.Value.AsObject().Clone();
        orchestrator["released"] = marker;
        var copy = team.AsObject().Clone();
        copy["orchestrator"] = orchestrator;
        return copy;
    }

    [Fact]
    public void TheMarkerOfTheLiveRegistrationsIncarnationMakesALiveTeamOrphaned()
    {
        var team = Team(1, null, "s1");

        Assert.Equal(TeamLife.OwnedLive, RosterModule.TeamLifeOf(team, Registered("s1", Self, SelfStart), Table));
        Assert.Equal(TeamLife.Orphaned, RosterModule.TeamLifeOf(Released(team, Self, SelfStart), Registered("s1", Self, SelfStart), Table));
    }

    [Fact]
    public void TheMarkerOfTheRecordsOwnPidAndStartMakesItOrphanedWithNoRegistration() =>
        Assert.Equal(TeamLife.Orphaned, RosterModule.TeamLifeOf(Released(Team(Self, SelfStart), Self, SelfStart), null, Table));

    [Fact]
    public void NegativeAMarkerForAnotherIncarnationOfTheSessionIsIgnoredWhenTheNewOneIsLive() =>
        Assert.Equal(TeamLife.OwnedLive, RosterModule.TeamLifeOf(Released(Team(1, null, "s1"), 99999, OtherStart), Registered("s1", Self, SelfStart), Table));

    [Fact]
    public void NegativeAMarkerWithTheLivePidButAnotherStartIsStale() =>
        Assert.Equal(TeamLife.OwnedLive, RosterModule.TeamLifeOf(Released(Team(1, null, "s1"), Self, OtherStart), Registered("s1", Self, SelfStart), Table));

    [Fact]
    public void NegativeAMarkerWithNoStartNamesNoIncarnation()
    {
        Assert.Equal(TeamLife.OwnedLive, RosterModule.TeamLifeOf(Released(Team(1, null, "s1"), Self, null), Registered("s1", Self, SelfStart), Table));
        Assert.Equal(TeamLife.OwnedLive, RosterModule.TeamLifeOf(Released(Team(Self, SelfStart), Self, null), null, Table));
    }

    [Fact]
    public void NegativeAMarkerForAnotherProcessThanTheRecordsOwnIsStale() =>
        Assert.Equal(TeamLife.OwnedLive, RosterModule.TeamLifeOf(Released(Team(Self, SelfStart), 99999, SelfStart), null, Table));
}
