using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Tests;

/// <summary>dismiss --close retires the open briefs to the dismissed member, so nothing keeps timing them.</summary>
public sealed partial class RosterTeamVerbTests
{
    [Fact]
    public void DismissCloseAbandonsTheMembersOpenBriefsButNotAnothersOrARunAnchor()
    {
        _branch = "ah/feature";
        ArrangeImplementor();
        _ = AskRelease("20261016-010000-aaaa", slug: "work");
        _ = AskRelease("20261016-010000-bbbb", member: "proj-other", slug: "work");
        _ = AskRelease("20261016-010000-cccc", slug: Ah.Hierarchy.HierarchyModule.PipelineAnchorSlug);

        var (code, stdout, stderr) = CloseImplementor();

        Assert.True(code == 0, stdout + stderr);
        var closed = JsonText.Parse(stdout).Member("briefs_abandoned")?.AsArray().Select(static id => id.AsString());
        Assert.Equal(["20261016-010000-aaaa"], closed);
    }

    [Fact]
    public void DisbandCloseAbandonsTheClosedMembersOpenBriefsButNotAnothersOrARunAnchor()
    {
        _branch = "ah/feature";
        ArrangeImplementor();
        _ = AskRelease("20261016-010000-aaaa", slug: "work");
        _ = AskRelease("20261016-010000-bbbb", member: "proj-other", slug: "work");
        _ = AskRelease("20261016-010000-cccc", slug: Ah.Hierarchy.HierarchyModule.PipelineAnchorSlug);

        var plan = Roster(["disband", "--team", "proj"]);
        var token = JsonText.Parse(plan.Stdout).Member("close_token") is { IsText: true } t ? t.AsString() : "none";
        var (code, stdout, stderr) = Roster(["disband", "--close", "--confirm", "--plan-token", token, "--team", "proj"]);

        Assert.True(code == 0, stdout + stderr);
        var closed = JsonText.Parse(stdout).Member("briefs_abandoned")?.AsArray().Select(static id => id.AsString());
        Assert.Equal(["20261016-010000-aaaa"], closed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DismissCloseAbandonsARoleBriefOnlyForTheRolesSoleMemberAndLeavesItsChecklistItemToTheOrchestrator(bool sole)
    {
        _branch = "ah/feature";
        ArrangeImplementor();
        if (!sole)
        {
            WriteTeam("proj", LiveTeamJson("tmux", OneImplementor + "," + Member("implementor", "proj-implementor-2", "%3")));
        }

        _ = AskRelease("20261016-010000-dddd", member: "null", slug: "work");
        var seeded = false;
        _seedGraph = graph =>
        {
            if (seeded)
            {
                return;
            }

            seeded = true;
            Assert.Null(graph.Hierarchy.Progress.RecordChecklist(Hierarchy, "proj", "_run", "orchestrator@proj", [new PlanInput("role-work", "add", "Do role work")]).Refused);
            Assert.Null(HierarchyModule.ChecklistOnDispatch(graph.Hierarchy.Progress, Hierarchy, "proj", "_run/role-work", "implementor", "20261016-010000-dddd", "orchestrator@proj", null));
        };

        var (code, stdout, stderr) = CloseImplementor();

        Assert.True(code == 0, stdout + stderr);
        var closed = JsonText.Parse(stdout).Member("briefs_abandoned")?.AsArray().Select(static id => id.AsString()).ToList() ?? [];
        Assert.Equal(sole, closed.Contains("20261016-010000-dddd"));
        var trail = Roster("checklist", "show", "_run/role-work", "--trail", "--team", "proj");
        Assert.Equal(sole, JsonText.Parse(trail.Stdout).Member("state")?.AsString() == "waiting-orch");
    }
}
