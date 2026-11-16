using Ah.Core.Json;

namespace Ah.Roster.Tests;

/// <summary>Whether a lane child belongs to the current incarnation of its manager team.</summary>
public sealed class SameIncarnationTests
{
    private static JsonValue Child(string? parentTeamId = null, string? created = null)
    {
        var parent = new JsonObject { ["team"] = "m", ["stream"] = "s" };
        if (parentTeamId is not null)
        {
            parent["team_id"] = parentTeamId;
        }

        var child = new JsonObject { ["parent"] = parent };
        if (created is not null)
        {
            child["created"] = created;
        }

        return child;
    }

    private static JsonValue Manager(string? teamId = null, string? created = null)
    {
        var manager = new JsonObject();
        if (teamId is not null)
        {
            manager["team_id"] = teamId;
        }

        if (created is not null)
        {
            manager["created"] = created;
        }

        return manager;
    }

    [Fact]
    public void AChildRecordingTheManagersTeamIdIsTheSameIncarnation() =>
        Assert.True(RosterModule.SameIncarnation(Child(parentTeamId: "t1"), Manager(teamId: "t1")));

    [Fact]
    public void NegativeAChildRecordingAnotherTeamIdIsNot() =>
        Assert.False(RosterModule.SameIncarnation(Child(parentTeamId: "t1"), Manager(teamId: "t2")));

    [Fact]
    public void NegativeAChildRecordingATeamIdAgainstAManagerWithNoneIsNot() =>
        Assert.False(RosterModule.SameIncarnation(Child(parentTeamId: "t1"), Manager()));

    [Fact]
    public void AnOlderChildIsAcceptedWhenItIsNotOlderThanTheManager()
    {
        Assert.True(RosterModule.SameIncarnation(Child(created: "2026-10-20T10:00:00Z"), Manager(created: "2026-10-20T10:00:00Z")));
        Assert.True(RosterModule.SameIncarnation(Child(created: "2026-10-20T10:00:01Z"), Manager(created: "2026-10-20T10:00:00Z")));
    }

    [Fact]
    public void NegativeAChildOlderThanTheManagerIsNot() =>
        Assert.False(RosterModule.SameIncarnation(Child(created: "2026-10-20T09:59:59Z"), Manager(created: "2026-10-20T10:00:00Z")));

    [Theory]
    [InlineData(null, "2026-10-20T10:00:00Z")]
    [InlineData("2026-10-20T10:00:00Z", null)]
    [InlineData("not a time", "2026-10-20T10:00:00Z")]
    public void NegativeAMissingOrUnreadableCreatedTimeIsNot(string? child, string? manager) =>
        Assert.False(RosterModule.SameIncarnation(Child(created: child), Manager(created: manager)));
}
