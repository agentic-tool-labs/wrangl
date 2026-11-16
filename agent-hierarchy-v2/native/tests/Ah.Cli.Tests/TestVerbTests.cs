namespace Ah.Cli.Tests;

/// <summary><c>ah test plan</c>: the facts the bucket's script reads, one tab-separated line each.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class TestVerbTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private (int Code, string[] Lines, string Err) Plan(params string[] args)
    {
        var r = _h.Run(["ah", "test", "plan", .. args]);
        return (r.Code, r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries), r.Stderr);
    }

    private static (int Code, string Out, string Err) Check(bool member, params string[] command)
    {
        using var h = new Harness(extra: member ? new() { [Ah.Mesh.MeshModule.MemberMarkerEnv] = "m1" } : null);
        var r = h.Run(["ah", "test", "plan", "--check", "--cwd", h.Sandbox.Root, "--", .. command]);
        return (r.Code, r.Stdout, r.Stderr);
    }

    [Fact]
    public void TheCheckRefusesAMembersWholeProjectRunWithExit64AndTheHooksText()
    {
        var (code, stdout, stderr) = Check(true, "dotnet", "test", "X.csproj");

        Assert.Equal(64, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(
            "ah: whole-project test runs belong to QA (dotnet test X.csproj). Run a narrow form instead: ah test run --tokens 2 -- dotnet test <project> --filter <expr>; use the runner's filter or one test file.",
            stderr.Trim());
        Assert.Equal(stderr.Trim(), Ah.Watch.WatchModule.TestScopeRefusal(null, null, "dotnet test X.csproj", () => Ah.Watch.WatchModule.TestCallerOf(null)));
    }

    [Theory]
    [InlineData("qa", 0)]
    [InlineData("implementor", 64)]
    public void AMembersRoleComesFromItsTeamRowWhenTheTeamIsOnlyInTheStore(string role, int expected)
    {
        using var h = new Harness(extra: new() { [Ah.Mesh.MeshModule.MemberMarkerEnv] = "t-m1" });
        var hier = Path.Join(h.Sandbox.Root, "repo", ".claude", "ah2");
        Directory.CreateDirectory(Path.Join(hier, "teams"));
        Assert.Null(Ah.StateDb.StateDbModule.EnsureDatabase(h.Sandbox.AhHome));
        var realHier = Ah.Core.Text.PathGuard.RealPath(hier, strict: false)!;
        var teamFile = Path.Join(realHier, "teams", "t.json");
        h.Variables["WRANGL_TEAM_FILE"] = teamFile;

        var graph = Ah.Cli.Composition.ServiceGraph.Build(Ah.Platform.PlatformModule.CreateIo(null), h.Context.Env, h.Runner, new Ah.TestKit.FakeDaemonClient());
        Ah.TestKit.TestPlant.Team(graph.Roster, graph.Writer, teamFile, "{\"version\":1,\"orchestrator\":{\"session_id\":null,\"pid\":null},\"members\":[{\"name\":\"t-m1\",\"role\":\"" + role + "\"}]}");
        Assert.False(File.Exists(teamFile));
        var context = h.Context with { Services = new Lazy<Ah.Cli.Composition.ServiceGraph>(graph) };

        var code = CliApp.Run(["ah", "test", "plan", "--check", "--cwd", h.Sandbox.Root, "--", "dotnet", "test", "x.sln"], context, new StringWriter(), new StringWriter(), []);

        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData("dotnet", "test", "X.csproj", "--filter", "Foo")]
    [InlineData("pytest", "-k", "foo")]
    [InlineData("go", "test", "-run", "Foo", "./...")]
    [InlineData("npx", "jest", "-t", "foo")]
    [InlineData("dotnet", "build", "X.csproj")]
    public void TheCheckLetsAMemberRunAFilteredRunOrANonTestCommand(params string[] command)
    {
        Assert.Equal(0, Check(true, command).Code);
    }

    [Theory]
    [InlineData("dotnet", "test", "X.csproj")]
    [InlineData("pytest")]
    public void TheCheckLetsASessionWithoutAMemberMarkerRunAnything(params string[] command)
    {
        Assert.Equal((0, string.Empty), (Check(false, command).Code, Check(false, command).Err));
    }

    [Fact]
    public void ARowedCommandGetsItsTokensItsEnvironmentAndNoArgumentsWhenItsRunnerHasNoneToAdd()
    {
        var (code, lines, _) = Plan("--capacity", "16", "--cwd", _h.Sandbox.Root, "--", "pytest", "tests");

        Assert.Equal(0, code);
        Assert.Equal(["row\tpytest", "tokens\t8", "env\tPYTEST_XDIST_AUTO_NUM_WORKERS\t8"], lines);
    }

    [Fact]
    public void ARunnerThatTakesArgumentsGetsEveryWordOfTheChangedCommandBack()
    {
        var (_, lines, _) = Plan("--capacity", "16", "--tokens", "3", "--cwd", _h.Sandbox.Root, "--", "go", "test", "./...");

        Assert.Equal(["row\tgo-test", "tokens\t8", "arg\tgo", "arg\ttest", "arg\t-p", "arg\t3", "arg\t./..."], lines);
    }

    [Fact]
    public void ACommandNoRowNamesChangesNothing()
    {
        var (code, lines, _) = Plan("--capacity", "16", "--cwd", _h.Sandbox.Root, "--", "./custom-check.sh", "--all");

        Assert.Equal(0, code);
        Assert.Equal(["row\t-", "tokens\t0"], lines);
    }

    [Fact]
    public void TheTokensAreClampedToTheCapacity()
    {
        var (_, lines, _) = Plan("--capacity", "2", "--tokens", "9", "--cwd", _h.Sandbox.Root, "--", "go", "test");

        Assert.Contains("arg\t2", lines);
    }

    [Theory]
    [InlineData("--", "pytest")]
    [InlineData("--capacity", "0", "--", "pytest")]
    [InlineData("--capacity", "x", "--", "pytest")]
    [InlineData("--capacity", "4")]
    [InlineData("--capacity", "4", "--")]
    [InlineData("--capacity", "4", "--bogus", "--", "pytest")]
    public void ABadCallIsRefusedWithExitTwoAndPrintsNoPlan(params string[] args)
    {
        var (code, lines, err) = Plan(args);

        Assert.Equal(2, code);
        Assert.Empty(lines);
        Assert.Contains("ah test plan", err, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownSubverbIsRefused()
    {
        var r = _h.Run("ah", "test", "bogus");

        Assert.Equal(2, r.Code);
        Assert.Contains("usage", r.Stderr, StringComparison.Ordinal);
    }
}
