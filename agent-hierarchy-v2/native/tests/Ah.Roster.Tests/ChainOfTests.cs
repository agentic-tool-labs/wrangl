using Ah.Abstractions;
using Ah.Config;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Roster.Tests;

/// <summary>Whether a member's chain of command holds: proof breaks a link, doubt never does.</summary>
public sealed class ChainOfTests
{
    private const string OtherStart = "Mon Jan  1 00:00:00 2001";
    private static readonly int Self = Environment.ProcessId;
    private static readonly IProcessTable Table = PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner());
    private static readonly string SelfStart = Table.StartTime(Self)!;

    private sealed class World
    {
        private readonly string _dir;

        public World()
        {
            var sandbox = UnitSandbox.Create();
            _dir = Path.Combine(sandbox.Root, ".claude", "ah2");
            Directory.CreateDirectory(Path.Combine(_dir, "teams"));
            var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = sandbox.Home });
            var io = PlatformModule.CreateIo(null);
            IRosterCore roster = null!;
            var config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(sandbox.Root, 1));
            Roster = roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);
        }

        public IRosterCore Roster { get; }

        public void Team(string name, string orchestrator, string extra = "") =>
            File.WriteAllText(Path.Combine(_dir, "teams", name + ".json"), $"{{\"version\":1,\"members\":[{Leads}],\"orchestrator\":{orchestrator}{extra}}}");

        /// <summary>The sub-orchestrator rows any fixture manager may name for a stream; a name not in this list is a lead the manager does not have.</summary>
        private static readonly string Leads = string.Join(",", new[] { "c-so-s", "L0-so-s", "L1-so-s", "L2-so-s", "L3-so-s", "L4-so-s", "L5-so-s" }
            .Select(n => $"{{\"name\":\"{n}\",\"role\":\"sub-orchestrator\"}}"));

        /// <summary>Every launch question the chain asked: the pid and the expectation it was asked against.</summary>
        public List<(int Pid, string Agent, string Name, string TeamFile)> Asked { get; } = [];

        public string TeamFile(string name) => Path.Combine(_dir, "teams", name + ".json");

        public ChainState Chain(string team, Func<string, (long Pid, string Start)?>? registered = null, Func<string, string, bool?>? launched = null) =>
            RosterModule.ChainOf(
                Roster,
                _dir,
                team,
                registered,
                Table,
                DateTimeOffset.UtcNow,
                Self,
                (pid, agent, name, teamFile, _, _) =>
                {
                    Asked.Add((pid, agent, name, teamFile));
                    return launched is null ? true : launched(name, teamFile);
                },
                role => role == "sub-orchestrator");
    }

    private static Func<string, string, bool?> Launch(bool? answer) => (_, _) => answer;

    private static string Live => $"{{\"session_id\":null,\"pid\":{Self},\"pid_start\":\"{SelfStart}\"}}";

    private static string Orphaned => $"{{\"session_id\":null,\"pid\":{Self},\"pid_start\":\"{OtherStart}\"}}";

    private static string Indeterminate => $"{{\"session_id\":null,\"pid\":{Self}}}";

    private static string Parent(string manager, string teamId = "tm") => $",\"parent\":{{\"team\":\"{manager}\",\"stream\":\"s\",\"team_id\":\"{teamId}\"}}";

    private static string Managing(string stream = "s") => $",\"team_id\":\"tm\",\"created\":\"2026-10-20T09:00:00Z\",\"streams\":{{\"{stream}\":{{\"sub_orchestrator\":\"X\",\"state\":\"open\"}}}}";

    /// <summary>A manager team <c>m</c> whose stream <c>s</c> is run by the member named after the child.</summary>
    private static string ManagerOf(string child) => Managing().Replace("\"X\"", $"\"{child}-so-s\"", StringComparison.Ordinal);

    [Fact]
    public void T2aAnOrphanedOrchestratorBreaksTheLinkAtItsTeam()
    {
        var world = new World();
        world.Team("t", Orphaned);

        var state = world.Chain("t");

        Assert.Equal(ChainKind.Down, state.Kind);
        Assert.Equal("t", state.Link);
    }

    [Fact]
    public void T2aATeamNobodyClaimedIsDownAsUnclaimed()
    {
        var world = new World();
        world.Team("t", "null");

        var state = world.Chain("t");

        Assert.Equal((ChainKind.Down, "t", "unclaimed"), (state.Kind, state.Link, state.Why));
    }

    [Fact]
    public void T2bALaneMemberWhoseLeadIsLiveAndWhoseManagersOrchestratorIsOrphanedIsDownAtTheManager()
    {
        var world = new World();
        world.Team("m", Orphaned, ManagerOf("c"));
        world.Team("c", Live, Parent("m"));

        var state = world.Chain("c");

        Assert.Equal(ChainKind.Down, state.Kind);
        Assert.Equal("m", state.Link);
    }

    [Fact]
    public void T2cALaneLeadRestoredWithoutAhsLaunchIsDownAtItsOwnTeam()
    {
        var world = new World();
        world.Team("m", Live, ManagerOf("c"));
        world.Team("c", "{\"session_id\":\"s1\",\"pid\":99999,\"pid_start\":\"" + OtherStart + "\"}", Parent("m"));

        var state = world.Chain("c", registered: s => s == "s1" ? (Self, SelfStart) : null, launched: Launch(false));

        Assert.Equal((ChainKind.Down, "c", "restored-bare"), (state.Kind, state.Link, state.Why));
        Assert.Equal((Self, "c-so-s", world.TeamFile("m")), (world.Asked.Single().Pid, world.Asked.Single().Name, world.Asked.Single().TeamFile));
    }

    [Fact]
    public void T2cSecondALaneLeadWhoseLaunchIsAhsForTheManagerTeamIsUp()
    {
        var world = new World();
        world.Team("m", Live, ManagerOf("c"));
        world.Team("c", "{\"session_id\":\"s1\",\"pid\":99999,\"pid_start\":\"" + OtherStart + "\"}", Parent("m"));

        var state = world.Chain("c", registered: s => s == "s1" ? (Self, SelfStart) : null, launched: (name, file) => name == "c-so-s" && file == world.TeamFile("m"));

        Assert.Equal(ChainKind.Up, state.Kind);
    }

    [Fact]
    public void T2hARestoredTopOrchestratorOnABareProcessIsUpAndNothingIsAsked()
    {
        var world = new World();
        world.Team("t", "{\"session_id\":\"s1\",\"pid\":99999,\"pid_start\":\"" + OtherStart + "\"}");

        var state = world.Chain("t", registered: s => s == "s1" ? (Self, SelfStart) : null, launched: Launch(false));

        Assert.Equal(ChainKind.Up, state.Kind);
        Assert.Empty(world.Asked);
    }

    [Fact]
    public void T2iALeadWhoseLaunchCarriesItsOwnTeamFileButNotTheManagersIsDown()
    {
        var world = new World();
        world.Team("m", Live, ManagerOf("c"));
        world.Team("c", "{\"session_id\":\"s1\",\"pid\":99999,\"pid_start\":\"" + OtherStart + "\"}", Parent("m"));

        var state = world.Chain("c", registered: s => s == "s1" ? (Self, SelfStart) : null, launched: (_, file) => file == world.TeamFile("c"));

        Assert.Equal((ChainKind.Down, "c", "restored-bare"), (state.Kind, state.Link, state.Why));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void NegativeT2cARestoredLeadWhoseLaunchIsAhsOrCannotBeToldIsNotBroken(bool? launchedByAh)
    {
        var world = new World();
        world.Team("m", Live, ManagerOf("c"));
        world.Team("c", "{\"session_id\":\"s1\",\"pid\":99999,\"pid_start\":\"" + OtherStart + "\"}", Parent("m"));

        var state = world.Chain("c", registered: s => s == "s1" ? (Self, SelfStart) : null, launched: Launch(launchedByAh));

        Assert.Equal(ChainKind.Up, state.Kind);
    }

    [Fact]
    public void NegativeT2cALeadWhoseRegistrationHoldsTheRecordedPidIsNotRestored()
    {
        var world = new World();
        world.Team("t", $"{{\"session_id\":\"s1\",\"pid\":{Self},\"pid_start\":\"{SelfStart}\"}}");

        var state = world.Chain("t", registered: s => s == "s1" ? (Self, SelfStart) : null, launched: Launch(false));

        Assert.Equal(ChainKind.Up, state.Kind);
    }

    [Fact]
    public void T2dAnIndeterminateManagerLeavesTheChainUnknownNotDown()
    {
        var world = new World();
        world.Team("m", Indeterminate, ManagerOf("c"));
        world.Team("c", Live, Parent("m"));

        Assert.Equal(ChainKind.Unknown, world.Chain("c").Kind);
    }

    [Fact]
    public void NegativeT2dAnIndeterminateLinkDoesNotHideALaterProvenBreak()
    {
        var world = new World();
        world.Team("m", Orphaned, ManagerOf("c"));
        world.Team("c", Indeterminate, Parent("m"));

        var state = world.Chain("c");

        Assert.Equal((ChainKind.Down, "m"), (state.Kind, state.Link));
    }

    [Fact]
    public void T2eAStaleLinkChildStopsAtItsOwnOrchestrator()
    {
        var world = new World();
        world.Team("m", Orphaned, ManagerOf("c"));
        world.Team("c", Live, Parent("m", teamId: "an-older-incarnation"));

        Assert.Equal(ChainKind.Up, world.Chain("c").Kind);
    }

    [Fact]
    public void NegativeT2eAParentLinkTheManagerDoesNotRecogniseIsNotFollowed()
    {
        var world = new World();
        world.Team("m", Orphaned, Managing().Replace("\"X\"", "\"somebody-else\"", StringComparison.Ordinal));
        world.Team("c", Live, Parent("m"));

        Assert.Equal(ChainKind.Up, world.Chain("c").Kind);
    }

    [Fact]
    public void T2fEveryLinkLiveIsUp()
    {
        var world = new World();
        world.Team("m", Live, ManagerOf("c"));
        world.Team("c", Live, Parent("m"));

        Assert.Equal(ChainKind.Up, world.Chain("c").Kind);
    }

    [Fact]
    public void ATeamWithNoRecordIsUnknown() => Assert.Equal(ChainKind.Unknown, new World().Chain("missing").Kind);

    private static void Ladder(World world, int teams)
    {
        for (var i = 1; i <= teams; i++)
        {
            var extra = (i > 1 ? Managing().Replace("\"X\"", $"\"L{i - 1}-so-s\"", StringComparison.Ordinal) : string.Empty)
                + (i < teams ? Parent($"L{i + 1}") : string.Empty);
            world.Team($"L{i}", Live, extra);
        }
    }

    [Fact]
    public void T2gAChainOfFourTeamsIsFollowedToTheTopAndFiveIsTooDeepToJudge()
    {
        var four = new World();
        Ladder(four, 4);
        Assert.Equal(ChainKind.Up, four.Chain("L1").Kind);

        var five = new World();
        Ladder(five, 5);
        var state = five.Chain("L1");
        Assert.Equal((ChainKind.Unknown, "chain too deep"), (state.Kind, state.Why));
    }
}
