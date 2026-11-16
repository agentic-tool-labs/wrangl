using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The team body names its plan slice with the plan lane's own id function, so a client never rebuilds the key from the team name.</summary>
public sealed partial class TreeLanesTests
{
    [Fact]
    public void TheTeamBodyNamesThePlanSliceOfTheDefaultTeamWithItsFolderAndNotItsName()
    {
        var (model, _) = Seeded(Truth());
        using var owner = model;

        var plan = Text(JsonText.Parse(Body(model, $"team:{D}/default")), "plan");
        Assert.Equal($"plan:{D}/_default", plan);
        Assert.NotEqual($"plan:{D}/default", plan);
    }

    [Fact]
    public void ThePlanSliceTheTeamBodyNamesIsOneThePlanComposerPublishesForThatTeamsPlanRecord()
    {
        var (model, _) = Seeded(Truth());
        using var owner = model;
        var records = new RecordStore();
        records.Set(ProgressLane.LaneName, ProgressLane.KeyOf(Canon, ProgressLane.FolderOf("default")), new LaneRecord(JsonText.Parse("{}"), 1));
        records.Set(ProgressLane.LaneName, ProgressLane.KeyOf(Canon, ProgressLane.FolderOf("ship")), new LaneRecord(JsonText.Parse("{}"), 1));

        var published = new PlanSummaryComposer().AllIds(records);

        Assert.Contains(Text(JsonText.Parse(Body(model, $"team:{D}/default")), "plan"), published);
        Assert.Contains(ProgressLane.PlanSliceId(Canon, "ship"), published);
        Assert.DoesNotContain(ProgressLane.PlanSliceId(Canon, "other"), published);
    }

    [Fact]
    public void APlanStreamSpellsOutEveryDefaultAndItsTotalLeavesOutDropped()
    {
        var records = new RecordStore();
        records.Set(ProgressLane.LaneName, ProgressLane.KeyOf(Canon, "ship"), new LaneRecord(JsonText.Parse(
            """{"streams":[{"stream":"s","owner":null,"branch":null,"goal":null,"state":"open","source":"checklist","as_of":1,"blockers":0,"warnings":0,"truncated":false,"counts":{"done":1,"in_progress":2,"not_started":0,"next":0,"blocked":0,"waiting_user":0,"waiting_orch":0,"dropped":5}}]}"""), 1));

        var change = new PlanSummaryComposer().Compose(ProgressLane.PlanSliceId(Canon, "ship"), records);
        var stream = JsonText.Parse(System.Text.Encoding.UTF8.GetString(change.Body!)).Member("streams")!.Value.AsArray()[0];

        Assert.Equal(3, stream.Member("total")!.Value.AsNumber());
        Assert.Equal(5, stream.Member("counts")!.Value.Member("dropped")!.Value.AsNumber());
        Assert.Equal(0, stream.Member("counts")!.Value.Member("next")!.Value.AsNumber());
        Assert.Equal(0, stream.Member("blockers")!.Value.AsNumber());
        Assert.False(stream.Member("truncated")!.Value.AsBool());
        Assert.False(stream.Member("review_open")!.Value.AsBool());
        foreach (var field in (string[])["owner", "goal", "branch"])
        {
            Assert.Equal(string.Empty, stream.Member(field)!.Value.AsString());
        }
    }
}
