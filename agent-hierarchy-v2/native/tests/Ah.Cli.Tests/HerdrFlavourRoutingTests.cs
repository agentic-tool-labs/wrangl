using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Core.Mux;
using Ah.Launch;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>A call on an existing agent goes to the herdr server it lives on: its registration's flavour, else its team's, else the env's.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class HerdrFlavourRoutingTests : IDisposable
{
    private const string CoreTeam = """{"transport":"herdr","herdr_flavour":"core","members":[]}""";
    // ah2-145: a tui team records it; an untagged one says nothing and resolves to the env
    private const string TuiTeam = """{"transport":"herdr","herdr_flavour":"tui","members":[]}""";
    private const string UntaggedTeam = """{"transport":"herdr","members":[]}""";

    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private static JsonObject Doc(string json) => JsonText.Parse(json).AsObject();

    [Fact]
    public void TheRegistrationWinsThenTheTeamThenTheEnv()
    {
        Assert.Equal("tui", HerdrFlavour.Resolve(Doc("""{"herdr_flavour":"tui"}"""), JsonText.Parse(CoreTeam), "core"));
        Assert.Equal("core", HerdrFlavour.Resolve(Doc("{}"), JsonText.Parse(CoreTeam), "tui"));
        Assert.Equal("tui", HerdrFlavour.Resolve(null, JsonText.Parse(TuiTeam), "core"));
        Assert.Equal("core", HerdrFlavour.Resolve(null, null, "core"));
        Assert.Null(HerdrFlavour.Resolve(null, null, null));
    }

    [Fact]
    public void AMemberRowTagRoutesADeadOrUnstampedMemberAndAnUntaggedRowIsAsBefore()
    {
        var coreRow = JsonText.Parse("""{"name":"ux","herdr_flavour":"core"}""");
        Assert.Equal("core", HerdrFlavour.Resolve(null, JsonText.Parse(TuiTeam), "tui", coreRow));
        Assert.Equal("core", HerdrFlavour.Resolve(Doc("{}"), JsonText.Parse(TuiTeam), "tui", coreRow));
        Assert.Equal("tui", HerdrFlavour.Resolve(Doc("""{"herdr_flavour":"tui"}"""), JsonText.Parse(CoreTeam), "core", coreRow));
        Assert.Equal("core", HerdrFlavour.Resolve(null, JsonText.Parse(CoreTeam), "tui", JsonText.Parse("""{"name":"ux"}""")));
        Assert.Equal("core", HerdrFlavour.Resolve(null, JsonText.Parse(CoreTeam), "tui", JsonText.Parse("""{"name":"ux","herdr_flavour":"tmux"}""")));
    }

    [Fact]
    public void AnUntaggedTeamResolvesToTheEnvAndARecordedTuiTeamStaysTui()
    {
        Assert.Equal("core", HerdrFlavour.Resolve(null, JsonText.Parse(UntaggedTeam), "core"));
        Assert.Equal("tui", HerdrFlavour.Resolve(null, JsonText.Parse(TuiTeam), "core"));
        Assert.Null(HerdrFlavour.Resolve(null, JsonText.Parse(UntaggedTeam), null));
        // a team flavour that is neither tui nor core says nothing either
        Assert.Equal("core", HerdrFlavour.Resolve(null, JsonText.Parse("""{"herdr_flavour":"tmux","members":[]}"""), "core"));
    }

    [Fact]
    public void TheTeamAndMemberFlavoursAreReadInAnyCase()
    {
        Assert.Equal("core", HerdrFlavour.RecordedOfTeam(JsonText.Parse("""{"herdr_flavour":"Core"}""")));
        Assert.Equal("tui", HerdrFlavour.OfMember(JsonText.Parse("""{"name":"a","herdr_flavour":"TUI"}""")));
        Assert.Equal("core", HerdrFlavour.Resolve(null, JsonText.Parse("""{"herdr_flavour":"CORE"}"""), "tui"));
        // the nearest value that must not match: a near-miss spelling
        Assert.Null(HerdrFlavour.RecordedOfTeam(JsonText.Parse("""{"herdr_flavour":"cores"}""")));
        Assert.Null(HerdrFlavour.OfMember(JsonText.Parse("""{"name":"a","herdr_flavour":" tui"}""")));
    }

    [Fact]
    public void ARegistrationFlavourThatIsNeitherTuiNorCoreIsNotTaken() =>
        Assert.Equal("core", HerdrFlavour.Resolve(Doc("""{"herdr_flavour":"tmux"}"""), JsonText.Parse(CoreTeam), "tui"));

    private (ServiceGraph Graph, FakeRunner Runner) Graph(params (string Name, string Value)[] extra) => Graph(false, extra);

    private (ServiceGraph Graph, FakeRunner Runner) Graph(bool inDaemon, params (string Name, string Value)[] extra)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        foreach (var (name, value) in extra)
        {
            variables[name] = value;
        }

        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var runner = new FakeRunner();
        return (ServiceGraph.Build(PlatformModule.CreateIo(null), env, runner, new FakeDaemonClient(), inDaemon: inDaemon), runner);
    }

    // ah2-156 D4: in ahd an unstamped member of an untagged team closes on tui, never on the server ahd's own env names; a session still uses its env
    [Theory]
    [InlineData(true, "herdr", "hc")]
    [InlineData(false, "hc", "herdr")]
    public void InAhdTeardownNeverTakesTheDaemonsEnvAsTheMembersFlavour(bool inDaemon, string closesOn, string notOn)
    {
        var (graph, runner) = Graph(inDaemon, ("HC_TERMINAL_ID", "t1"));
        var member = new JsonObject { ["name"] = "tm-implementor", ["transport_id"] = "t7" };

        _ = graph.Launch.Teardown.CloseAndVerify([member], "herdr", [], new LaunchContext(_sandbox.Home, "tm", "repo", new JsonObject()), JsonText.Parse(UntaggedTeam));

        Assert.Contains(closesOn, Binaries(runner));
        Assert.DoesNotContain(notOn, Binaries(runner));
    }

    private static List<string> Binaries(FakeRunner runner) => runner.Calls.Select(c => Path.GetFileName(c[0])).ToList();

    private List<string> CloseFrom((string, string) sessionEnv, string team, string? registeredFlavour = null)
    {
        var (graph, runner) = Graph(sessionEnv);
        var member = new JsonObject { ["name"] = "tm-implementor", ["transport_id"] = "t7", ["session_id"] = "s-impl" };
        if (registeredFlavour is not null)
        {
            var box = Path.Combine(_sandbox.Home, "box");
            Directory.CreateDirectory(box);
            Assert.True(graph.Mailbox.WriteRegistration(new Ah.Mesh.RegistrationWrite("s-impl", [new Ah.Mesh.BoxRef("tm-implementor@tm", box)], HerdrFlavour: registeredFlavour)));
        }

        var context = new LaunchContext(_sandbox.Home, "tm", "repo", new JsonObject());
        _ = graph.Launch.Teardown.CloseAndVerify([member], "herdr", [], context, JsonText.Parse(team));
        return Binaries(runner);
    }

    [Fact]
    public void TeardownOfACoreTeamFromATuiSessionClosesOnCore()
    {
        var bins = CloseFrom(("HERDR_ENV", "1"), CoreTeam);

        Assert.Contains("hc", bins);
        Assert.DoesNotContain("herdr", bins);
    }

    [Fact]
    public void TeardownOfATuiTeamFromACoreSessionClosesOnTheTui()
    {
        var bins = CloseFrom(("HC_TERMINAL_ID", "t1"), TuiTeam);

        Assert.Contains("herdr", bins);
        Assert.DoesNotContain("hc", bins);
    }

    [Fact]
    public void AMemberStampedCoreClosesOnCoreWhenItsImplicitTeamNamesNoTransport()
    {
        var (graph, runner) = Graph(("HC_TERMINAL_ID", "t1"));
        var member = new JsonObject { ["name"] = "tm-implementor", ["transport_id"] = "t7", ["herdr_flavour"] = "core" };
        var team = JsonText.Parse("""{"transport":null,"implicit":true,"members":[]}""");

        var result = graph.Launch.Teardown.CloseAndVerify([member], string.Empty, [], new LaunchContext(_sandbox.Home, "tm", "repo", new JsonObject()), team);

        Assert.Contains("hc", Binaries(runner));
        Assert.DoesNotContain("has no addressable pane", result.Results[0].Get("error") is { IsText: true } e ? e.AsString() : string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AMembersRegisteredFlavourOutranksItsTeams()
    {
        var bins = CloseFrom(("HERDR_ENV", "1"), TuiTeam, registeredFlavour: "core");

        Assert.Contains("hc", bins);
        Assert.DoesNotContain("herdr", bins);
    }

    [Fact]
    public void ADismissOfAnUnregisteredMemberTaggedCoreOnATuiTeamClosesOnCore()
    {
        var (graph, runner) = Graph(("HERDR_ENV", "1"));
        var member = new JsonObject { ["name"] = "tm-ux", ["transport_id"] = "t36", ["session_id"] = "s-gone", ["herdr_flavour"] = "core" };

        _ = graph.Launch.Teardown.CloseAndVerify([member], "herdr", [], new LaunchContext(_sandbox.Home, "tm", "repo", new JsonObject()), JsonText.Parse(TuiTeam));

        Assert.Contains("hc", Binaries(runner));
        Assert.DoesNotContain("herdr", Binaries(runner));
    }

    [Fact]
    public void TeamWideTabCallsReachEachMembersServerAndTheTeamsOwn()
    {
        var (graph, _) = Graph(("HERDR_ENV", "1"));
        Ah.Commands.IRosterLaunch launch = graph.Launch;

        Assert.Equal(["tui"], Ah.Commands.IRosterLaunch.TeamHerdrs(launch, JsonText.Parse("""{"members":[{"name":"a"},{"name":"b","herdr_flavour":"tui"}]}"""), "tui").Select(h => h.Flavour));
        Assert.Equal(["tui", "core"], Ah.Commands.IRosterLaunch.TeamHerdrs(launch, JsonText.Parse("""{"members":[{"name":"a"},{"name":"b","herdr_flavour":"core"},{"name":"c","herdr_flavour":"core"}]}"""), "tui").Select(h => h.Flavour));
        // b483: a team that records its own flavour hosts its untagged member there, so the env's server is not asked
        Assert.Equal(["core"], Ah.Commands.IRosterLaunch.TeamHerdrs(launch, JsonText.Parse("""{"herdr_flavour":"core","members":[{"name":"a"}]}"""), "tui").Select(h => h.Flavour));
    }

    [Fact]
    public void AnUntaggedTeamLivesOnTheSessionsHerdrAndATaggedOneOnItsOwn()
    {
        var (graph, _) = Graph(("HC_TERMINAL_ID", "t1"));
        Ah.Commands.IRosterLaunch launch = graph.Launch;
        var untagged = JsonText.Parse("""{"transport":null,"members":[{"name":"a"}]}""");

        // ah2-143: a desktop session with an untagged team asks the desktop only, never the terminal herdr
        Assert.Equal(["core"], Ah.Commands.IRosterLaunch.TeamHerdrs(launch, untagged, "core").Select(h => h.Flavour));
        Assert.Equal(["tui"], Ah.Commands.IRosterLaunch.TeamHerdrs(launch, JsonText.Parse("""{"herdr_flavour":"tui","members":[{"name":"a"}]}"""), "core").Select(h => h.Flavour));
        // a stamped member still adds its server
        Assert.Equal(["core", "tui"], Ah.Commands.IRosterLaunch.TeamHerdrs(launch, JsonText.Parse("""{"members":[{"name":"a"},{"name":"b","herdr_flavour":"tui"}]}"""), "core").Select(h => h.Flavour));
    }

    [Fact]
    public void ASessionOnNoHerdrWithAnUntaggedTeamAsksNoServer()
    {
        var (graph, _) = Graph();
        Ah.Commands.IRosterLaunch launch = graph.Launch;

        Assert.Empty(Ah.Commands.IRosterLaunch.TeamHerdrs(launch, JsonText.Parse("""{"members":[{"name":"a"}]}"""), null));
        Assert.Empty(Ah.Commands.IRosterLaunch.TeamHerdrs(launch, JsonText.Parse("""{"members":[]}"""), null));
        // the nearest case that must still ask: a stamped member names its server
        Assert.Equal(["core"], Ah.Commands.IRosterLaunch.TeamHerdrs(launch, JsonText.Parse("""{"members":[{"name":"a","herdr_flavour":"core"}]}"""), null).Select(h => h.Flavour));
    }

    [Fact]
    public void ATabIdTwoServersListIsActedOnByNeither()
    {
        var (graph, _) = Graph(("HERDR_ENV", "1"));
        Ah.Commands.IRosterLaunch launch = graph.Launch;
        var (tui, core) = (launch.Herdr, launch.HerdrFor("core"));
        var tabs = new Dictionary<string, IHerdr?>(StringComparer.Ordinal);

        Ah.Commands.IRosterLaunch.NoteTab(tabs, "1", tui);
        Ah.Commands.IRosterLaunch.NoteTab(tabs, "1", core);
        Ah.Commands.IRosterLaunch.NoteTab(tabs, "2", core);
        Ah.Commands.IRosterLaunch.NoteTab(tabs, "3", tui);
        Ah.Commands.IRosterLaunch.NoteTab(tabs, "3", tui);

        Assert.Null(tabs["1"]);
        Assert.Same(core, tabs["2"]);
        Assert.Same(tui, tabs["3"]); // the same server listing it twice is no collision
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 0, false)] // the member's pid as the pane's shell alone is no proof
    [InlineData(0, 0, false)] // nor when the shell is itself the foreground
    public void AnUntaggedMemberMissingOnItsTeamsServerIsClosedOnTheOtherOnlyWhenThePanePidIsItsOwn(int pidOffset, int shellOffset, bool closed)
    {
        var (graph, runner) = Graph(("HERDR_ENV", "1"));
        runner.Ps = FakeRunner.SelfOnly;
        // the registration names the team file under the hierarchy dir, not under the caller's cwd
        var hierDir = Path.Combine(_sandbox.Home, ".claude", "ah2");
        FakeRunner.RegisterSelf(graph.Mailbox, "s-ux", graph.Roster.TeamPath(hierDir, "tm"), "tm-ux@tm");
        runner.Exit = argv => Path.GetFileName(argv[0]) == "herdr" && argv.Contains("close") ? 1 : 0;
        runner.Stderr = argv => Path.GetFileName(argv[0]) == "herdr" && argv.Contains("close") ? "pane_not_found" : string.Empty;
        runner.Output = argv => Path.GetFileName(argv[0]) == "hc" && argv.Contains("get")
            ? $$$"""{"address":"t36","argv":["/bin/zsh","-l"],"foreground":{"argv":["claude","--name","tm-ux"],"pid":{{{Environment.ProcessId + pidOffset}}}},"pid":{{{Environment.ProcessId + shellOffset}}},"state":"live"}"""
            : string.Empty;

        // a row from before the session was recorded on it, as on the live team: its registration is found by name
        var member = new JsonObject { ["name"] = "tm-ux", ["transport_id"] = "t36" };

        var warnings = new List<string>();
        _ = graph.Launch.Teardown.CloseAndVerify([member], "herdr", warnings, new LaunchContext(_sandbox.Home, "tm", "repo", new JsonObject()), JsonText.Parse(TuiTeam), hierDir);

        Assert.Equal(closed, runner.Calls.Any(c => Path.GetFileName(c[0]) == "hc" && c.Contains("kill") && c.Contains("t36")));
        Assert.Equal(!closed, warnings.Any(w => w.Contains("could not be tied", StringComparison.Ordinal)));
    }

    [Fact]
    public void AMemberWhoseClaudeExitedIsTiedOnTheOtherServerByItsSpawnLabel()
    {
        var (graph, runner) = Graph(("HERDR_ENV", "1"));
        runner.Exit = argv => Path.GetFileName(argv[0]) == "herdr" && argv.Contains("close") ? 1 : 0;
        runner.Stderr = argv => Path.GetFileName(argv[0]) == "herdr" && argv.Contains("close") ? "pane_not_found" : string.Empty;
        runner.Output = argv => Path.GetFileName(argv[0]) == "hc" && argv.Contains("ah.spawn=n1")
            ? """{"terminals":[{"address":"t40","state":"live"}]}"""
            : string.Empty;

        // no session, no registration: a bare zsh is all that is left of the member
        var member = new JsonObject { ["name"] = "tm-ux", ["transport_id"] = "t31", ["spawn_nonce"] = "n1" };

        var warnings = new List<string>();
        _ = graph.Launch.Teardown.CloseAndVerify([member], "herdr", warnings, new LaunchContext(_sandbox.Home, "tm", "repo", new JsonObject()), JsonText.Parse(TuiTeam));

        Assert.Contains(runner.Calls, c => Path.GetFileName(c[0]) == "hc" && c.Contains("kill") && c.Contains("t40"));
        Assert.DoesNotContain(warnings, w => w.Contains("could not be tied", StringComparison.Ordinal));
    }

    [Fact]
    public void APaneReaderAskedForCoreReadsThroughHcFromATuiSession()
    {
        var (graph, runner) = Graph(("HERDR_ENV", "1"));

        _ = graph.Launch.PaneStateFor(HerdrFlavour.Core)!.Agents();

        Assert.Contains("hc", Binaries(runner));
        Assert.DoesNotContain("herdr", Binaries(runner));
    }
}
