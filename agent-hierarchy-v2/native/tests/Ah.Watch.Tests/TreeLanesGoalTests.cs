using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>ah2-53: the team-summary slice carries the goal that applies to a team, its own else its lane parent's, and nothing where none does.</summary>
public sealed partial class TreeLanesTests
{
    private static JsonObject GoalRow(string team, string text) => new() { ["text"] = text, ["done_when"] = JsonValue.Null, ["team"] = team };

    private static (ReadModel Model, Clock Clock) MakeGoals(TreeReader reader, Dictionary<string, JsonObject> goals)
    {
        var clock = new Clock();
        string[] canonical = [Canon];
        var lanes = TreeModel.Lanes(reader, () => canonical, Home, goalOf: (dir, team) => dir == Canon && goals.TryGetValue(team, out var goal) ? goal : null)
            .Append(new MessagesLane(reader, () => canonical)).Append(new StatusLane(reader, () => canonical)).ToList();
        var model = new ReadModel(lanes, new ModelOptions { SeedAtStart = false }, clock.Read, null, "e1", new PathLaneTable(() => canonical, home: Home), TreeModel.Composers(static () => 5_000_000, ProductFacts));
        model.EmitKeySet("teams");
        Drain(model, clock);
        return (model, clock);
    }

    private static JsonValue? GoalOfSummary(ReadModel model, string team) => Slice(model, $"team-summary:{D}/{team}").Member("goal");

    [Fact]
    public void ATeamWithAGoalCarriesItAndATeamWithoutOneCarriesNoGoalMember()
    {
        var reader = LaneTruth(Streams(("s1", "open")), MgrDoc(Streams()));
        var (model, _) = MakeGoals(reader, new() { ["mgr"] = GoalRow("mgr", "ship the goal row") });
        using var owner = model;

        var goal = GoalOfSummary(model, "mgr")!.Value;
        Assert.Equal("ship the goal row", goal.Member("text")!.Value.AsString());
        Assert.True(goal.Member("done_when")!.Value.IsNull);
        Assert.Equal("mgr", goal.Member("team")!.Value.AsString());

        // negative: a team with no goal row has no goal member, not a null one
        Assert.Null(GoalOfSummary(model, "mgr-s1"));
    }

    [Fact]
    public void AConfirmedLaneWithNoGoalOfItsOwnCarriesItsParentsAndOneWithItsOwnCarriesThat()
    {
        var reader = LaneTruth(Streams(("s1", "open")), ChildDoc("mgr", "s1"));
        var (inherits, _) = MakeGoals(reader, new() { ["mgr"] = GoalRow("mgr", "parent aim") });
        using (inherits)
        {
            var goal = GoalOfSummary(inherits, "mgr-s1")!.Value;
            Assert.Equal("parent aim", goal.Member("text")!.Value.AsString());
            Assert.Equal("mgr", goal.Member("team")!.Value.AsString());
        }

        var (own, _) = MakeGoals(reader, new() { ["mgr"] = GoalRow("mgr", "parent aim"), ["mgr-s1"] = GoalRow("mgr-s1", "lane aim") });
        using (own)
        {
            Assert.Equal("mgr-s1", GoalOfSummary(own, "mgr-s1")!.Value.Member("team")!.Value.AsString());
        }
    }

    [Fact]
    public void AnUnconfirmedLaneClaimDoesNotInheritAGoal()
    {
        // negative: the child claims stream s2, which the manager's file does not hold, so it is no lane and inherits nothing
        var reader = LaneTruth(Streams(("s1", "open")), ChildDoc("mgr", "s2"));
        var (model, _) = MakeGoals(reader, new() { ["mgr"] = GoalRow("mgr", "parent aim") });
        using var owner = model;

        Assert.Null(GoalOfSummary(model, "mgr-s1"));
    }
}
