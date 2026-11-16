using Ah.Platform;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>A team's work root is its team file's, and its branch is read from that root's git head, when no Orchestrator grant says otherwise.</summary>
public sealed partial class TreeLanesTests
{
    private static TreeReader RootedAt(string root)
    {
        var reader = FiveStates();
        reader.Teams[TeamPath] = Doc("so", "dev").TrimEnd('}') + ",\"expected_root\":\"" + root + "\"}";
        return reader;
    }

    [Fact]
    public void ASummaryServesTheTeamFilesRoot()
    {
        var (model, _) = SeededWithActivity(RootedAt("/w/root"));
        using var owner = model;

        Assert.Equal("/w/root", Summary(model).Member("work_root")!.Value.AsString());
    }

    [Fact]
    public void AMemberWithNoRootOfItsOwnAndTheOrchestratorWorkInTheTeamsRoot()
    {
        var (model, _) = SeededWithActivity(RootedAt("/w/root"), branchOf: static _ => "from-head");
        using var owner = model;

        var body = Summary(model);
        var states = body.Member("member_states")!.Value;
        foreach (var member in new[] { TreeIds.Orchestrator, "dev" })
        {
            Assert.Equal("/w/root", Text(states.Member(MemberId(model, member))!.Value, "work_root"));
            Assert.Equal("from-head", Text(states.Member(MemberId(model, member))!.Value, "branch"));
        }

        Assert.Equal(1, Num(body, "worktrees"));
    }

    private static string OrchestratorGrant(string row) =>
        "{\"present\":true,\"mode\":\"enforce\",\"members\":{\"" + TreeIds.ReservedName + "\":{" + row + "}}}";

    [Fact]
    public void AGrantRowWinsOverTheTeamFile()
    {
        var reader = RootedAt("/w/root");
        reader.Grants[(Canon, "default")] = OrchestratorGrant("\"work_root\":\"/wt/so\",\"stream\":\"granted\"");
        var (model, _) = SeededWithActivity(reader, branchOf: static _ => "from-head");
        using var owner = model;

        Assert.Equal("/wt/so", Summary(model).Member("work_root")!.Value.AsString());
        Assert.Equal("granted", Summary(model).Member("branch")!.Value.AsString());
    }

    [Fact]
    public void NegativeARootGrantedWithNoStreamTakesNoBranchFromTheTeamFilesCheckout()
    {
        var reader = RootedAt("/w/root");
        reader.Grants[(Canon, "default")] = OrchestratorGrant("\"work_root\":\"/wt/so\"");
        var (model, _) = SeededWithActivity(reader, branchOf: static _ => "from-head");
        using var owner = model;

        Assert.Equal("/wt/so", Summary(model).Member("work_root")!.Value.AsString());
        Assert.True(Summary(model).Member("branch")!.Value.IsNull);
    }

    [Fact]
    public void ARootFromTheTeamFileServesTheBranchOfThatRoot()
    {
        var (model, _) = SeededWithActivity(RootedAt("/w/root"), branchOf: static root => root == "/w/root" ? "from-head" : null);
        using var owner = model;

        Assert.Equal("from-head", Summary(model).Member("branch")!.Value.AsString());
    }

    [Fact]
    public void NegativeATeamFileWithNoRootServesNoRootOrBranch()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;

        Assert.True(Summary(model).Member("work_root")!.Value.IsNull);
        Assert.True(Summary(model).Member("branch")!.Value.IsNull);
    }

    [Fact]
    public void TheTeamRecordCarriesTheBranchOfItsRoot()
    {
        var lane = new TeamLane(RootedAt("/w/root"), () => [Canon], null, static root => root == "/w/root" ? "ah/x" : null);

        var record = lane.Reread(TeamLane.KeyOf(Canon, TeamPath), 0).Record!.Value;

        Assert.Equal("ah/x", record.Member("branch")!.Value.AsString());
    }

    [Fact]
    public void NegativeARootOnNoBranchAddsNoBranch()
    {
        var lane = new TeamLane(RootedAt("/w/root"), () => [Canon], null, static _ => null);

        var record = lane.Reread(TeamLane.KeyOf(Canon, TeamPath), 0).Record!.Value;

        Assert.Null(record.Member("branch"));
    }
}

public sealed class GitHeadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("githead-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string? Branch() => GitHead.BranchOf(PlatformModule.CreateIo(null).Files, _root);

    private void Head(string text)
    {
        _ = Directory.CreateDirectory(Path.Join(_root, ".git"));
        File.WriteAllText(Path.Join(_root, ".git", "HEAD"), text);
    }

    [Fact]
    public void AHeadOnABranchNamesIt()
    {
        Head("ref: refs/heads/ah/ui-data-fixes\n");

        Assert.Equal("ah/ui-data-fixes", Branch());
    }

    [Fact]
    public void ALinkedWorktreesGitFileIsFollowed()
    {
        var real = Directory.CreateDirectory(Path.Join(_root, "gitdirs", "wt")).FullName;
        File.WriteAllText(Path.Join(real, "HEAD"), "ref: refs/heads/feature\n");
        File.WriteAllText(Path.Join(_root, ".git"), "gitdir: gitdirs/wt\n");

        Assert.Equal("feature", Branch());
    }

    [Fact]
    public void NegativeADetachedHeadHasNoBranch()
    {
        Head("0123456789abcdef0123456789abcdef01234567\n");

        Assert.Null(Branch());
    }

    [Fact]
    public void NegativeARootThatIsNoRepositoryHasNoBranch() => Assert.Null(Branch());
}
