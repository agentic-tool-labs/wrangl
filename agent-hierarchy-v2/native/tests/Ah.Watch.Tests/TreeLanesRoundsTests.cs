using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The team slice's pipeline carries the review rounds the status document records for that team, and nothing when it records none.</summary>
public sealed partial class TreeLanesTests
{
    private static string TeamSliceId => TreeIds.Team(Canon, "default");

    private static string StatusDoc(string team, string pipeline) =>
        "{\"schema\":1,\"enabled\":true,\"teams\":[{\"team\":\"" + team + "\",\"members\":[],\"dispatches\":[],\"pipeline\":" + pipeline + "}]}";

    private const string TwoItems =
        "{\"anchor_id\":\"a1\",\"started\":\"2026-06-01T10:00:00Z\",\"round_cap\":3,\"waiting_on_user\":1,"
        + "\"items\":[{\"slug\":\"ui-p3d\",\"rounds\":2,\"open\":true,\"to\":\"implementor\"},{\"slug\":\"x\",\"rounds\":1,\"open\":false,\"to\":\"reviewer\"}]}";

    private static (ReadModel Model, Clock Clock) SeededWithStatus(TreeReader reader)
    {
        var (model, clock) = Seeded(reader);
        model.EmitKeySet("status");
        Drain(model, clock);
        return (model, clock);
    }

    [Fact]
    public void TheTeamPipelineCarriesTheRoundsOfEachItemWithTheRoundCap()
    {
        var reader = Truth();
        reader.Statuses[Canon] = StatusDoc("default", TwoItems);
        var (model, _) = SeededWithStatus(reader);
        using var owner = model;

        Assert.Contains("\"items\":[{\"slug\":\"ui-p3d\",\"rounds\":2,\"round_cap\":3},{\"slug\":\"x\",\"rounds\":1,\"round_cap\":3}]", Body(model, TeamSliceId), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTeamPipelineCarriesWaitingOnUserAsTheStatusCountsIt()
    {
        var reader = Truth();
        reader.Statuses[Canon] = StatusDoc("default", TwoItems);
        var (model, _) = SeededWithStatus(reader);
        using var owner = model;
        Assert.Contains("\"waiting_on_user\":1", Body(model, TeamSliceId), StringComparison.Ordinal);

        var none = Truth();
        none.Statuses[Canon] = StatusDoc("default", TwoItems.Replace("\"waiting_on_user\":1", "\"waiting_on_user\":0", StringComparison.Ordinal));
        var (zeroModel, _) = SeededWithStatus(none);
        using var zeroOwner = zeroModel;
        Assert.Contains("\"waiting_on_user\":0", Body(zeroModel, TeamSliceId), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTeamPipelineHasNoWaitingOnUserWhenTheStatusHasNoneForTheTeam()
    {
        var other = Truth();
        other.Statuses[Canon] = StatusDoc("other", TwoItems);
        var (otherModel, _) = SeededWithStatus(other);
        using var otherOwner = otherModel;
        Assert.DoesNotContain("waiting_on_user", Body(otherModel, TeamSliceId), StringComparison.Ordinal);

        var absent = Truth();
        absent.Statuses[Canon] = StatusDoc("default", "{\"anchor_id\":\"a1\",\"round_cap\":3,\"items\":[]}");
        var (absentModel, _) = SeededWithStatus(absent);
        using var absentOwner = absentModel;
        Assert.DoesNotContain("waiting_on_user", Body(absentModel, TeamSliceId), StringComparison.Ordinal);

        var noStatus = Truth();
        var (noModel, _) = SeededWithStatus(noStatus);
        using var noOwner = noModel;
        Assert.DoesNotContain("waiting_on_user", Body(noModel, TeamSliceId), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTeamPipelineHasNoItemsWhenTheStatusIsAnotherTeamsOrAbsentOrHasNoUsableItem()
    {
        var other = Truth();
        other.Statuses[Canon] = StatusDoc("other", TwoItems);
        var (model, _) = SeededWithStatus(other);
        using var ownerOther = model;
        Assert.DoesNotContain("\"items\"", Body(model, TeamSliceId), StringComparison.Ordinal);

        var none = Truth();
        var (noStatus, _) = SeededWithStatus(none);
        using var ownerNone = noStatus;
        Assert.DoesNotContain("\"items\"", Body(noStatus, TeamSliceId), StringComparison.Ordinal);

        var empty = Truth();
        empty.Statuses[Canon] = StatusDoc("default", "{\"anchor_id\":\"a1\",\"round_cap\":3,\"items\":[{\"rounds\":2}]}");
        var (emptyModel, _) = SeededWithStatus(empty);
        using var ownerEmpty = emptyModel;
        Assert.DoesNotContain("\"items\"", Body(emptyModel, TeamSliceId), StringComparison.Ordinal);

        var nullPipeline = Truth();
        nullPipeline.Statuses[Canon] = StatusDoc("default", "null");
        var (nullModel, _) = SeededWithStatus(nullPipeline);
        using var ownerNull = nullModel;
        Assert.DoesNotContain("\"items\"", Body(nullModel, TeamSliceId), StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedStatusRecomposesTheTeamSliceAndEqualsARebuild()
    {
        var reader = Truth();
        reader.Statuses[Canon] = StatusDoc("default", TwoItems);
        var (model, clock) = SeededWithStatus(reader);
        using var owner = model;
        Assert.Contains("\"slug\":\"ui-p3d\",\"rounds\":2", Body(model, TeamSliceId), StringComparison.Ordinal);

        reader.Statuses[Canon] = StatusDoc("default", TwoItems.Replace("\"rounds\":2", "\"rounds\":3", StringComparison.Ordinal));
        model.Emit("status", Canon);
        Drain(model, clock);

        Assert.Contains("\"slug\":\"ui-p3d\",\"rounds\":3", Body(model, TeamSliceId), StringComparison.Ordinal);
        var (fresh, _) = SeededWithStatus(reader);
        using var freshOwner = fresh;
        Assert.Equal(Body(fresh, TeamSliceId), Body(model, TeamSliceId));
    }
}
