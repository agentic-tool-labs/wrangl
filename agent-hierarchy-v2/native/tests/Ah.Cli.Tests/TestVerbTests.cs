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

    private static (int Code, string Out, string Err) Check(bool member, params string[] command) => Check(member, true, command);

    private static (int Code, string Out, string Err) Check(bool member, bool busy, params string[] command)
    {
        using var h = new Harness(extra: member ? new() { [Ah.Mesh.MeshModule.MemberMarkerEnv] = "m1" } : null);
        BucketOf(h, busy);
        var r = h.Run(["ah", "test", "plan", "--check", "--cwd", h.Sandbox.Root, "--", .. command]);
        return (r.Code, r.Stdout, r.Stderr);
    }

    /// <summary>Points the harness at a bucket of its own, of 8 tokens: busy is a live lease of 2 held by this test process.</summary>
    private static void BucketOf(Harness h, bool busy)
    {
        var slots = Path.Join(h.Sandbox.Root, "slots");
        h.Variables["AH_SLOTS_DIR"] = slots;
        h.Runner.Ps = FakeRunner.SelfOnly;
        Directory.CreateDirectory(Path.Join(slots, "held"));
        File.WriteAllText(Path.Join(slots, "capacity"), "8\n");
        if (busy)
        {
            Directory.CreateDirectory(Path.Join(slots, "held", "1-busy"));
            File.WriteAllText(Path.Join(slots, "held", "1-busy", "holder"), $"{{\"label\": \"other\", \"pid\": {Environment.ProcessId}, \"pid_start\": \"{FakeRunner.SelfStart}\", \"tokens\": 2, \"since\": 1}}\n");
        }
    }

    [Fact]
    public void TheCheckRefusesAMembersWholeProjectRunWithExit64AndTheHooksText()
    {
        var (code, stdout, stderr) = Check(true, "dotnet", "test", "X.csproj");

        Assert.Equal(64, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(
            "ah: a whole-project test run by an implementor needs an idle test bucket (2/8 tokens held, 0 waiting) (dotnet test X.csproj). Run a narrow form now: ah test run -- dotnet test <project> --filter <expr>; or retry when the bucket is idle; or report NEEDS-QA.",
            stderr.Trim());
        Assert.Equal(stderr.Trim(), Ah.Watch.WatchModule.TestScopeRefusal(null, null, "dotnet test X.csproj", () => Ah.Watch.WatchModule.TestCallerOf(null), () => new Ah.Watch.BucketStatus(8, 2, 1, 0, 0, false)));
    }

    [Fact]
    public void AnImplementorsWholeProjectRunIsAllowedOnAnIdleBucketAndRefusedWithoutATokensHintOnABusyOne()
    {
        var implementor = () => new Ah.Watch.TestCaller("implementor", "implement");

        var idle = Ah.Watch.WatchModule.TestScopeRefusal(null, null, "dotnet test X.csproj", implementor, () => new Ah.Watch.BucketStatus(8, 0, 0, 0, 0, false));
        var waiting = Ah.Watch.WatchModule.TestScopeRefusal(null, null, "dotnet test X.csproj", implementor, () => new Ah.Watch.BucketStatus(8, 0, 0, 1, 0, false));

        Assert.Null(idle);
        Assert.Contains("idle test bucket", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("--tokens 2", waiting, StringComparison.Ordinal);
        Assert.Equal(0, Check(true, false, "dotnet", "test", "X.csproj").Code);
    }

    [Theory]
    [InlineData("qa", 0)]
    [InlineData("implementor", 64)]
    public void AMembersRoleComesFromItsTeamRowWhenTheTeamIsOnlyInTheStore(string role, int expected)
    {
        using var h = new Harness(extra: new() { [Ah.Mesh.MeshModule.MemberMarkerEnv] = "t-m1" });
        BucketOf(h, true);
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
        Assert.Equal(["row\tpytest", "tokens\t8", "elastic\t1", "env\tPYTEST_XDIST_AUTO_NUM_WORKERS\t8"], lines);
    }

    [Fact]
    public void ARowSizedInTokensIsNotElastic()
    {
        var (_, lines, _) = Plan("--capacity", "16", "--cwd", _h.Sandbox.Root, "--", "godot", "--headless", "-s", "x.gd");

        Assert.Equal(["row\tgodot-headless", "tokens\t2"], lines);
    }

    [Fact]
    public void ARunnerThatTakesArgumentsGetsEveryWordOfTheChangedCommandBack()
    {
        var (_, lines, _) = Plan("--capacity", "16", "--tokens", "3", "--cwd", _h.Sandbox.Root, "--", "go", "test", "./...");

        Assert.Equal(["row\tgo-test", "tokens\t8", "elastic\t1", "arg\tgo", "arg\ttest", "arg\t-p", "arg\t3", "arg\t./..."], lines);
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
