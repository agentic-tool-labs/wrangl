using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary><c>create --parent &lt;team&gt; --stream &lt;ws&gt;</c>: the team of a sub-orchestrator.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string ChildStream =
        "\"s1\":{\"state\":\"open\",\"branch\":\"feat/s1\",\"base\":\"main\",\"worktree\":\"/wt/s1\",\"tab_id\":\"tab-9\",\"opened\":\"2026-10-11T00:00:00.000Z\"}";

    private (int Code, string Stdout, string Stderr) CreateChild(params string[] extra)
    {
        var verified = "[{\"role\":\"implementor\",\"name\":\"proj-s1-implementor\",\"route\":\"peer\",\"expected_root\":\"" + Project + "\"}]";
        return Roster(["create", "--commit", "--verified", verified, "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self, .. extra]);
    }

    private JsonValue ChildFile(string name = "proj-s1") => JsonText.Parse(_sandbox.TeamText(TeamPath(name)));

    [Fact]
    public void ACommitWithAParentWritesTheParentTheStreamCopyAndAWorkstreamAndAPlainOneWritesNone()
    {
        WriteTeam("proj", SoTeam(ChildStream));

        var (code, _, stderr) = CreateChild("--parent", "proj", "--stream", "s1");

        Assert.True(code == 0, stderr);
        var team = ChildFile();
        Assert.Equal("proj", team.Member("parent")!.Value.Member("team")!.Value.AsString());
        Assert.Equal("s1", team.Member("parent")!.Value.Member("stream")!.Value.AsString());
        var copy = team.Member("streams")!.Value.Member("s1")!.Value;
        Assert.Equal("open", copy.Member("state")!.Value.AsString());
        Assert.Equal("feat/s1", copy.Member("branch")!.Value.AsString());
        Assert.Equal("main", copy.Member("base")!.Value.AsString());
        Assert.Equal("/wt/s1", copy.Member("worktree")!.Value.AsString());
        Assert.Equal("tab-9", copy.Member("tab_id")!.Value.AsString());
        Assert.NotEqual("2026-10-11T00:00:00.000Z", copy.Member("opened")!.Value.AsString());
        var workstream = team.Member("workstream")!.Value;
        Assert.Equal("working", workstream.Member("state")!.Value.AsString());
        Assert.Equal(string.Empty, workstream.Member("summary")!.Value.AsString());
        Assert.False(workstream.Member("updated")!.Value.AsString().Length == 0);
    }

    [Fact]
    public void ATeamCreatedWithoutAParentHasNoParentStreamsOrWorkstream()
    {
        var verified = "[{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"expected_root\":\"" + Project + "\"}]";

        var (code, _, stderr) = Roster("create", "--commit", "--verified", verified, "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self);

        Assert.True(code == 0, stderr);
        var team = ChildFile("proj");
        Assert.Null(team.Member("parent"));
        Assert.Null(team.Member("streams"));
        Assert.Null(team.Member("workstream"));
    }

    [Fact]
    public void TheDefaultNameIsParentDashStreamAndTeamOverridesIt()
    {
        WriteTeam("proj", SoTeam(ChildStream));

        var (code, _, stderr) = CreateChild("--parent", "proj", "--stream", "s1", "--team", "kid");

        Assert.True(code == 0, stderr);
        Assert.True(_sandbox.TeamExists(TeamPath("kid")));
        Assert.False(_sandbox.TeamExists(TeamPath("proj-s1")));
    }

    [Fact]
    public void ThePlanShowsTheParentAndStream()
    {
        WriteTeam("proj", SoTeam(ChildStream));
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"version":1,"enabled":true,"roster":{"route":"peer","members":[{"role":"implementor","model":"sonnet"}]}}""");

        var (code, stdout, stderr) = Roster("create", "--plan", "--parent", "proj", "--stream", "s1", "--team", "kid", "--orchestrator-pid", Self);
        var (plainCode, plain, _) = Roster("create", "--plan", "--team", "kid", "--orchestrator-pid", Self);

        Assert.True(code == 0, stderr);
        var parent = JsonText.Parse(stdout).Member("parent")!.Value;
        Assert.Equal("proj", parent.Member("team")!.Value.AsString());
        Assert.Equal("s1", parent.Member("stream")!.Value.AsString());
        Assert.Equal(0, plainCode);
        Assert.Null(JsonText.Parse(plain).Member("parent"));
    }

    [Theory]
    [InlineData("--parent", "proj")]
    [InlineData("--stream", "s1")]
    public void EachFlagIsValidOnlyWithTheOther(string flag, string value)
    {
        WriteTeam("proj", SoTeam(ChildStream));

        var (code, _, stderr) = CreateChild(flag, value);

        Assert.NotEqual(0, code);
        Assert.Contains("go together", stderr, StringComparison.Ordinal);
        Assert.False(_sandbox.TeamExists(TeamPath("proj-s1")));
    }

    [Fact]
    public void AParentThatDoesNotExistAndAStreamItDoesNotHaveAreRefusedAndNothingIsWritten()
    {
        WriteTeam("proj", SoTeam(ChildStream));

        var (missingCode, _, missingErr) = CreateChild("--parent", "nowhere", "--stream", "s1");
        var (noStreamCode, _, noStreamErr) = CreateChild("--parent", "proj", "--stream", "other");

        Assert.NotEqual(0, missingCode);
        Assert.Contains("does not exist", missingErr, StringComparison.Ordinal);
        Assert.NotEqual(0, noStreamCode);
        Assert.Contains("no open stream named other", noStreamErr, StringComparison.Ordinal);
        Assert.False(_sandbox.TeamExists(TeamPath("nowhere-s1")));
        Assert.False(_sandbox.TeamExists(TeamPath("proj-other")));
    }

    [Fact]
    public void ADoneStreamIsRefusedAndAnOpenOneIsNot()
    {
        WriteTeam("proj", SoTeam(ChildStream.Replace("\"state\":\"open\"", "\"state\":\"done\"", StringComparison.Ordinal)));
        var (doneCode, _, doneErr) = CreateChild("--parent", "proj", "--stream", "s1");
        WriteTeam("proj", SoTeam(ChildStream));
        var (openCode, _, openErr) = CreateChild("--parent", "proj", "--stream", "s1");

        Assert.NotEqual(0, doneCode);
        Assert.Contains("no open stream", doneErr, StringComparison.Ordinal);
        Assert.True(openCode == 0, openErr);
    }

    [Fact]
    public void AParentThatIsItselfAChildIsRefused()
    {
        WriteTeam("proj", SoTeam(ChildStream, extra: ",\"parent\":{\"team\":\"top\",\"stream\":\"s1\"}"));

        var (code, _, stderr) = CreateChild("--parent", "proj", "--stream", "s1");

        Assert.NotEqual(0, code);
        Assert.Contains("one level deep", stderr, StringComparison.Ordinal);
        Assert.False(_sandbox.TeamExists(TeamPath("proj-s1")));
    }

    [Fact]
    public void ACallerThatAlreadyOwnsAChildTeamIsRefusedAndOneThatOwnsOnlyAPlainTeamIsNot()
    {
        _callers = new RecordingCallers { StartText = SelfStart };
        WriteTeam("proj", SoTeam(ChildStream));
        Assert.True(CreateChild("--parent", "proj", "--stream", "s1").Code == 0);
        WriteTeam("proj", SoTeam(ChildStream + "," + ChildStream.Replace("\"s1\"", "\"s2\"", StringComparison.Ordinal)));

        var (code, _, stderr) = CreateChild("--parent", "proj", "--stream", "s2", "--team", "proj-s2");

        Assert.NotEqual(0, code);
        Assert.Contains("already own a sub-team", stderr, StringComparison.Ordinal);
        Assert.False(_sandbox.TeamExists(TeamPath("proj-s2")));
    }

    [Theory]
    [InlineData("enforce")]
    [InlineData("log")]
    [InlineData("off")]
    public void TheChildGrantsDocumentTakesTheParentsGateMode(string mode)
    {
        WriteTeam("proj", SoTeam(ChildStream));
        WriteGrants(mode);

        var (code, _, stderr) = CreateChild("--parent", "proj", "--stream", "s1");

        Assert.True(code == 0, stderr);
        Assert.Equal(mode, JsonText.Parse(GrantsText(Hierarchy, "proj-s1")!).Member("mode")!.Value.AsString());
    }

    [Fact]
    public void AParentWithNoGrantsDocumentLeavesTheChildWithNone()
    {
        WriteTeam("proj", SoTeam(ChildStream));

        var (code, _, stderr) = CreateChild("--parent", "proj", "--stream", "s1");

        Assert.True(code == 0, stderr);
        Assert.False(HasGrants(Hierarchy, "proj-s1"));
    }
}
