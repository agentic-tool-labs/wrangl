using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>ahd judges a lane lead the way the hooks do: at its own lane team first, then as a member of its manager's team.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class WatchLaneLeadChainTests : IDisposable
{
    private const string Session = "sess-lead";
    private const string LeadStart = "Sat Oct 17 12:00:00 2026";
    private const string ManagerStart = "Sat Oct 17 11:00:00 2026";
    private const int ManagerPid = 5000;

    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed class Table(string? leadCommand) : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => [1];

        public bool? HasAncestor(int pid, int candidate) => false;

        public string? StartTime(long pid) => pid == Environment.ProcessId ? LeadStart : pid == ManagerPid ? ManagerStart : null;

        public string? Command(int pid) => pid == Environment.ProcessId ? leadCommand : null;
    }

    private sealed class Host(ServiceGraph graph, IClock clock, IProcessTable table) : WatchHostAdapter(graph, RosterModule.CreateScreenRules(), clock, WatchHostSeams.Unavailable)
    {
        internal override IProcessTable ProcessTree => table;
    }

    /// <summary>
    /// The lead (a live registration, this test process) is the sub-orchestrator of stream s in manager team tm, whose Orchestrator is live,
    /// and owns the lane team c, which records <paramref name="recordedPid"/> as its Orchestrator's process.
    /// </summary>
    private (Host Host, WatchRegistration Lead) Arrange(int recordedPid, string? leadCommand)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, new FakeRunner(), new FakeDaemonClient());
        var hierarchy = _sandbox.Home + "/repo/.claude/ah2";
        _ = Directory.CreateDirectory(hierarchy + "/teams");
        var managerFile = hierarchy + "/teams/tm.json";
        TestPlant.Team(
            graph.Roster,
            graph.Writer,
            managerFile,
            $$$"""
            {"version":1,"team_id":"tm","created":"2026-10-20T09:00:00Z",
             "orchestrator":{"session_id":null,"pid":{{{ManagerPid}}},"pid_start":"{{{ManagerStart}}}"},
             "members":[{"name":"c-so-s","role":"sub-orchestrator"}],
             "streams":{"s":{"sub_orchestrator":"c-so-s","state":"open"} } }
            """);
        TestPlant.Team(
            graph.Roster,
            graph.Writer,
            hierarchy + "/teams/c.json",
            $$$"""
            {"version":1,"members":[],"parent":{"team":"tm","stream":"s","team_id":"tm"},
             "orchestrator":{"session_id":"{{{Session}}}","pid":{{{recordedPid}}},"pid_start":"{{{(recordedPid == Environment.ProcessId ? LeadStart : "Mon Jan  1 00:00:00 2001")}}}"}}
            """);
        var box = _sandbox.Home + "/lead-box";
        _ = Directory.CreateDirectory(box);
        Assert.True(graph.Mailbox.WriteRegistration(new Ah.Mesh.RegistrationWrite(
            Session, [new Ah.Mesh.BoxRef("c-so-s@tm", box)], managerFile, "claude", Environment.ProcessId, null, _sandbox.Home, LeadStart,
            TeamFiles: new Dictionary<string, string> { ["tm"] = managerFile })));
        var host = new Host(graph, io.Clock, new Table(leadCommand));
        return (host, host.LiveRegistrations().Single(r => r.SessionId == Session));
    }

    [Fact]
    public void T3iALeadRestoredWithoutAhsLaunchIsDownAtItsOwnLaneTeamEvenThoughItsManagersChainIsUp()
    {
        var (host, lead) = Arrange(recordedPid: 99999, leadCommand: "claude --agent wrangl:sub-orchestrator");

        var chain = host.ChainOf(lead);

        Assert.Equal(ChainKind.Down, chain.Kind);
        Assert.Equal("restored-bare", chain.Why);
        Assert.Equal("c", chain.Link);
    }

    [Fact]
    public void T3jALeadStillRunningInTheProcessItsLaneTeamNamesIsUp()
    {
        var (host, lead) = Arrange(recordedPid: Environment.ProcessId, leadCommand: "claude --agent wrangl:sub-orchestrator");

        Assert.Equal(ChainKind.Up, host.ChainOf(lead).Kind);
    }

    [Fact]
    public void NegativeALeadWhoseCommandLineCannotBeReadIsNeverDown()
    {
        var (host, lead) = Arrange(recordedPid: 99999, leadCommand: null);

        Assert.NotEqual(ChainKind.Down, host.ChainOf(lead).Kind);
    }
}
