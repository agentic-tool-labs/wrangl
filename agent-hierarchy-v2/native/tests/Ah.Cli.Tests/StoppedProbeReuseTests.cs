using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// ah2-52 item 3: the processes lane's member facts ask herdr about a member that is not live once per <see cref="StoppedProbeReuse.StoppedProbeReuseMs"/>,
/// and again at once when its registration or its team's record changed. A live member is never asked. Real mailbox, team store and judge;
/// only the processes and herdr's answer are staged.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class StoppedProbeReuseTests : IDisposable
{
    private const string Team = "tm";

    private readonly Sandbox _sandbox = new();
    private readonly EvidenceProcesses _processes = new();
    private readonly ServiceGraph _graph;
    private readonly string _dir;
    private readonly List<string> _asked = [];
    private long _now = 1_000_000;
    private volatile bool _herdrFails;
    private volatile bool _serverDown;
    private volatile bool _tuiLive;

    public StoppedProbeReuseTests()
    {
        Directory.CreateDirectory(_sandbox.Bin);
        foreach (var name in new[] { "herdr", "hc" })
        {
            var path = Path.Combine(_sandbox.Bin, name);
            File.WriteAllText(path, "#!/bin/sh\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var runner = new FakeRunner
        {
            // the core server is down: any hc call fails with server_not_running
            Exit = argv => Path.GetFileName(argv[0]) == "hc" ? 1 : 0,
            Output = argv =>
            {
                if (Path.GetFileName(argv[0]) == "hc")
                {
                    lock (_asked)
                    {
                        _asked.Add("core:" + (argv.FirstOrDefault(a => a.StartsWith("ah.name=", StringComparison.Ordinal)) ?? string.Join(' ', argv)));
                    }

                    return "{\"error\":{\"code\":\"server_not_running\",\"message\":\"no herdr server is running\"}}";
                }

                if (!argv.Contains("agent"))
                {
                    return string.Empty;
                }

                lock (_asked)
                {
                    _asked.Add(argv.FirstOrDefault(a => a is "live" or "stopped" or "gone") ?? string.Join(' ', argv));
                }

                return _herdrFails ? "not json"
                    : _tuiLive ? "{\"result\":{\"agent\":{\"agent_status\":\"idle\"}}}"
                    : _serverDown ? "{\"id\":\"cli:agent:get\",\"error\":{\"code\":\"server_not_running\",\"message\":\"no herdr server is running\"}}"
                    : "{\"error\":{\"code\":\"agent_not_found\"}}";
            },
        };
        _dir = Path.Combine(_sandbox.Home, "repo", ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(_dir, "teams"));
        using (var open = Ah.StateDb.StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(Ah.StateDb.StateDbAvailability.Ready, open.Status.Availability);
        }

        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["PATH"] = _sandbox.Bin, ["WRANGL_HIERARCHY_DIR"] = _dir };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, runner, new FakeDaemonClient());
        _graph.Hierarchy.Core.Liveness = MeshModule.CreateMemberLiveness(_graph.Mailbox, _processes, _processes);
        PlantTeam("orchestrator");
        Register("ref", "s-ref", EvidenceProcesses.Reference);
        Register("live", "s-live", 101);
        // 102 is not running: the registration is proven dead, so herdr is asked about its pane
        Register("stopped", "s-stopped", 102);
    }

    public void Dispose() => _sandbox.Dispose();

    // goneOnStoppedPane: the two members that are not live share one pane id, as a stale row and a current one can
    // stamps: the herdr flavour each named member row was stamped with at spawn; the team itself records none
    private void PlantTeam(string orchestratorSession, bool stoppedHasPane = true, bool goneOnStoppedPane = false, Dictionary<string, string>? stamps = null)
    {
        JsonObject Member(string name, string? pane)
        {
            var row = new JsonObject { ["name"] = name, ["role"] = "implementor", ["kind"] = "codex" };
            if (pane is not null)
            {
                row["transport_id"] = "w1:p" + pane;
            }

            if (stamps?.GetValueOrDefault(name) is { } stamp)
            {
                row["herdr_flavour"] = stamp;
            }

            return row;
        }

        TestPlant.Team(_graph.Roster, _graph.Writer, _graph.Roster.TeamPath(_dir, Team), new JsonObject
        {
            ["team_id"] = "t-tm",
            ["orchestrator"] = new JsonObject { ["session_id"] = orchestratorSession },
            ["members"] = new JsonArray { Member("live", "live"), Member("stopped", stoppedHasPane ? "stopped" : null), Member("gone", goneOnStoppedPane ? "stopped" : "gone") },
        });
    }

    private void Register(string name, string session, long pid)
    {
        var teamFile = _graph.Roster.TeamPath(_dir, Team);
        Assert.True(_graph.Mailbox.WriteRegistration(new RegistrationWrite(session, [new BoxRef($"{name}@{Team}", _graph.Mailbox.BoxDirFor(teamFile, name))], teamFile, "claude", pid, null, _sandbox.Home, EvidenceProcesses.Start)));
        _graph.Hierarchy.Status.AppendSessionPid(_dir, JsonValue.FromString(session), JsonValue.FromNumber(pid));
    }

    private string[] Pass(StoppedProbeReuse? reuse)
    {
        lock (_asked)
        {
            _asked.Clear();
        }

        _ = MemberFactsOf.Dir(_graph, _dir, reuse: reuse);
        lock (_asked)
        {
            return [.. _asked.Order(StringComparer.Ordinal)];
        }
    }

    [Fact]
    public void AMemberThatIsNotLiveIsAskedOnceInTheWindowAndAgainAfterItOrAfterItsRegistrationOrTeamChanged()
    {
        var reuse = new StoppedProbeReuse(() => _now);

        Assert.Equal(["gone", "stopped"], Pass(reuse));
        Assert.Empty(Pass(reuse));
        _now += StoppedProbeReuse.StoppedProbeReuseMs - 1;
        Assert.Empty(Pass(reuse));

        // the window ends
        _now += 1;
        Assert.Equal(["gone", "stopped"], Pass(reuse));
        Assert.Empty(Pass(reuse));

        // the stopped member registers again, still not running: only it is asked
        Register("stopped", "s-stopped-2", 103);
        Assert.Equal(["stopped"], Pass(reuse));
        Assert.Empty(Pass(reuse));

        // the team's record changes: both are asked
        PlantTeam("s-ref");
        Assert.Equal(["gone", "stopped"], Pass(reuse));
        Assert.Empty(Pass(reuse));
    }

    [Fact]
    public void ALiveMemberIsNeverAskedWithOrWithoutTheReuseAndNoReuseAsksEveryPass()
    {
        Assert.Equal(["gone", "stopped"], Pass(null));
        Assert.Equal(["gone", "stopped"], Pass(null));

        var reuse = new StoppedProbeReuse(() => _now);
        Assert.DoesNotContain("live", Pass(reuse));
        Assert.DoesNotContain("live", Pass(null));
    }

    [Fact]
    public void NegativeAPaneNoLongerListedIsDroppedAndAnswersPastTheWindowAreNotKept()
    {
        var reuse = new StoppedProbeReuse(() => _now);
        _ = Pass(reuse);
        Assert.Equal(2, reuse.Count);

        PlantTeam("orchestrator", stoppedHasPane: false);
        Assert.Equal(["gone"], Pass(reuse));
        Assert.Equal(1, reuse.Count);

        _now += StoppedProbeReuse.StoppedProbeReuseMs;
        reuse.Keep("/elsewhere", new HashSet<(string, string, string)>());
        Assert.Equal(0, reuse.Count);
    }

    [Fact]
    public void HerdrsServerNotRunningIsAskedOnceForTheTeamAndHeldForTheWindow()
    {
        _serverDown = true;
        var reuse = new StoppedProbeReuse(() => _now);

        // the first member's answer stands for the team's other one: one spawn, both held
        Assert.Single(Pass(reuse));
        Assert.Equal(2, reuse.Count);
        Assert.Empty(Pass(reuse));

        _now += StoppedProbeReuse.StoppedProbeReuseMs;
        Assert.Single(Pass(reuse));

        // without the reuse nothing changes: each member is asked
        Assert.Equal(["gone", "stopped"], Pass(null));
    }

    // ah2-156: on an untagged team each member is asked on the server it is stamped on, and core being down does not blank the tui member
    [Fact]
    public void EachMemberIsAskedOnItsOwnServerAndOneServerDownLeavesTheOthersMembersAlone()
    {
        PlantTeam("orchestrator", stamps: new() { ["stopped"] = "core", ["gone"] = "tui" });
        _tuiLive = true;
        lock (_asked)
        {
            _asked.Clear();
        }

        var facts = MemberFactsOf.Dir(_graph, _dir, reuse: new StoppedProbeReuse(() => _now));

        lock (_asked)
        {
            Assert.Equal(["core:ah.name=stopped", "gone"], _asked.Order(StringComparer.Ordinal));
        }

        Assert.True(facts.Panes.ContainsKey("gone"));
        Assert.False(facts.Panes.ContainsKey("stopped"));
    }

    [Fact]
    public void TwoMembersOnOnePaneAreEachAskedAndEachKeepTheirOwnAnswer()
    {
        PlantTeam("orchestrator", goneOnStoppedPane: true);
        var reuse = new StoppedProbeReuse(() => _now);

        Assert.Equal(["gone", "stopped"], Pass(reuse));
        Assert.Equal(2, reuse.Count);
        Assert.Empty(Pass(reuse));
    }

    [Fact]
    public void TheRebuildsForgetAsksEveryoneAgain()
    {
        var reuse = new StoppedProbeReuse(() => _now);
        _ = Pass(reuse);
        Assert.Empty(Pass(reuse));

        reuse.Forget();

        Assert.Equal(["gone", "stopped"], Pass(reuse));
    }

    [Fact]
    public void NegativeAnAnswerHerdrCouldNotGiveIsNotHeldAndAClockSteppedBackIsNotInTheWindow()
    {
        var reuse = new StoppedProbeReuse(() => _now);
        _herdrFails = true;
        Assert.Equal(["gone", "stopped"], Pass(reuse));
        Assert.Equal(0, reuse.Count);
        Assert.Equal(["gone", "stopped"], Pass(reuse));

        _herdrFails = false;
        Assert.Equal(["gone", "stopped"], Pass(reuse));
        Assert.Empty(Pass(reuse));

        // a held answer whose age reads below zero is not fresh
        _now -= 1;
        Assert.Equal(["gone", "stopped"], Pass(reuse));
    }
}
