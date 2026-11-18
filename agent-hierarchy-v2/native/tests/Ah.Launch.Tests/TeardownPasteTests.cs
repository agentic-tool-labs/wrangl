using System.Security.Cryptography;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Launch.Tests;

/// <summary>The verified close, peer targets, the reconcile of a team file, and the hardened paste path against a scripted multiplexer.</summary>
public sealed class TeardownPasteTests
{
    private static string Text(JsonValue? v) => v is { } value ? Ah.Core.Json.JsConvert.ToJsString(value) : "undefined";


    internal static Teardown TeardownOver(ScenarioRig rig, FakeRowStore? rows = null, Func<string, string?>? checkoutRoot = null) => new(
        rig.Env,
        rig.Io.Files,
        rig.Config,
        rig.Roster,
        rig.Registry,
        rig.Hierarchy.Roster,
        rig.Hierarchy.Messages,
        rig.Herdr,
        new HerdrTopology(rig.Herdr),
        rig.MuxRunner,
        rig.Clock,
        rig.Sleeper,
        rig.Appserver,
        rows ?? new FakeRowStore(),
        checkoutRoot ?? (root => root));

    private static JsonObject Target(string name, string pane, bool stray = false)
    {
        var o = new JsonObject { ["name"] = name, ["transport_id"] = pane };
        if (stray)
        {
            o["stray"] = true;
        }

        return o;
    }

    /// <summary>Scripts <c>herdr agent list</c> from a mutable pane-to-name map; <c>pane close</c> removes the pane unless it is in <paramref name="stuck"/>.</summary>
    private static Dictionary<string, string> LivePanes(ScenarioRig rig, IEnumerable<string>? stuck = null, Dictionary<string, string>? sessions = null)
    {
        var live = new Dictionary<string, string>(StringComparer.Ordinal);
        var keep = new HashSet<string>(stuck ?? [], StringComparer.Ordinal);
        rig.Mux.Override = r =>
        {
            var a = r.Argv;
            if (a.Count > 2 && a[0] == "herdr" && a[1] == "agent" && a[2] == "list")
            {
                var agents = new JsonArray();
                foreach (var (pane, name) in live)
                {
                    var row = new JsonObject { ["name"] = name, ["pane_id"] = pane };
                    if (sessions is not null && sessions.TryGetValue(pane, out var session))
                    {
                        row["agent_session"] = new JsonObject { ["value"] = session };
                    }

                    agents.Add(row);
                }

                return FakeMux.Out(JsonText.Stringify(new JsonObject { ["result"] = new JsonObject { ["agents"] = agents } }, 0));
            }

            if (a.Count > 3 && a[0] == "herdr" && a[1] == "pane" && a[2] == "close")
            {
                if (!keep.Contains(a[3]))
                {
                    live.Remove(a[3]);
                }

                return FakeMux.Out("{}");
            }

            return null;
        };
        return live;
    }

    private static JsonObject WithPid(JsonObject target, JsonValue pid)
    {
        target["appserver_pid"] = pid;
        return target;
    }

    [Theory]
    [InlineData("codex -c sandbox_mode=\"workspace-write\" app-server --listen unix:///h/run/codex/t-m.sock", "/h/run/codex/t-m.sock", true)]
    [InlineData("codex app-server --listen unix:///h/run/codex/t-other.sock", "/h/run/codex/t-m.sock", false)]
    [InlineData("codex app-server --listen unix:///h/run/codex/t-m.sock.bak", "/h/run/codex/t-m.sock", false)]
    [InlineData("vim /h/run/codex/t-m.sock", "/h/run/codex/t-m.sock", false)]
    [InlineData("codex --listen unix:///h/run/codex/t-m.sock", "/h/run/codex/t-m.sock", false)]
    [InlineData("", "/h/run/codex/t-m.sock", false)]
    public void ACommandLineIsTheAppserverOnlyWhenItListensOnTheMembersOwnSocket(string line, string socket, bool expected) =>
        Assert.Equal(expected, LaunchModule.IsAppserverOf(line, socket));

    [Fact]
    public void AMissingCommandLineIsNotTheAppserver() => Assert.False(LaunchModule.IsAppserverOf(null, "/h/run/codex/t-m.sock"));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(4194304, true)]
    [InlineData(4194305, false)]
    public void OnlyAPidAboveOneAndWithinPidMaxMaybeSignalled(int pid, bool expected) =>
        Assert.Equal(expected, LaunchModule.IsSignalablePid(pid));

    [Fact]
    public void ARecordedAppserverThatIsVerifiedIsSignalledOnceWithItsOwnSocket()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p2"] = "alpha-implementor";
        rig.Appserver.Verified = [4321];
        var warnings = new List<string>();

        var result = TeardownOver(rig).CloseAndVerify([WithPid(Target("alpha-implementor", "w1:p2"), JsonValue.FromNumber(4321))], "herdr", warnings, rig.Context);

        Assert.Equal([4321], rig.Appserver.Terminated);
        Assert.Single(rig.Appserver.TerminateRequests);
        Assert.EndsWith("/run/codex/alpha-alpha-implementor.sock", rig.Appserver.TerminateRequests[0].Socket, StringComparison.Ordinal);
        Assert.True(result.Results[0].Get("appserver_stopped") is { IsTruthy: true });
        Assert.Null(result.Results[0].Get("appserver_unverified"));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(4194305)]
    [InlineData(double.PositiveInfinity)]
    public void ARecordedPidThatIsNotAProcessIdIsNeverSignalledAndItsRowIsFlagged(double pid)
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p2"] = "alpha-implementor";
        var warnings = new List<string>();

        var result = TeardownOver(rig).CloseAndVerify([WithPid(Target("alpha-implementor", "w1:p2"), JsonValue.FromNumber(pid))], "herdr", warnings, rig.Context);

        Assert.Empty(rig.Appserver.TerminateRequests);
        Assert.Empty(rig.Appserver.Terminated);
        Assert.True(result.Results[0].Get("appserver_unverified") is { IsTruthy: true });
        Assert.Null(result.Results[0].Get("appserver_stopped"));
        Assert.Contains(warnings, w => w.StartsWith("appserver pid not verified: alpha-implementor", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("4321")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void ARecordedPidThatIsAStringIsNeverSignalled(string pid)
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p2"] = "alpha-implementor";
        var warnings = new List<string>();

        var result = TeardownOver(rig).CloseAndVerify([WithPid(Target("alpha-implementor", "w1:p2"), JsonValue.FromString(pid))], "herdr", warnings, rig.Context);

        Assert.Empty(rig.Appserver.TerminateRequests);
        Assert.True(result.Results[0].Get("appserver_unverified") is { IsTruthy: true });
        Assert.Contains(warnings, w => w.Contains("is not a process id", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4194304)]
    public void TheSmallestAndLargestAcceptedPidsAreAskedAbout(int pid)
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p2"] = "alpha-implementor";
        rig.Appserver.Verified = [pid];

        var result = TeardownOver(rig).CloseAndVerify([WithPid(Target("alpha-implementor", "w1:p2"), JsonValue.FromNumber(pid))], "herdr", [], rig.Context);

        Assert.Equal([pid], rig.Appserver.Terminated);
        Assert.True(result.Results[0].Get("appserver_stopped") is { IsTruthy: true });
    }

    [Fact]
    public void AReusedPidThatIsNotTheAppserverIsNotSignalledAndWarnsThatItsRowStays()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p2"] = "alpha-implementor";
        rig.Appserver.Verified = [];
        var warnings = new List<string>();

        var result = TeardownOver(rig).CloseAndVerify([WithPid(Target("alpha-implementor", "w1:p2"), JsonValue.FromNumber(4321))], "herdr", warnings, rig.Context);

        Assert.Single(rig.Appserver.TerminateRequests);
        Assert.Empty(rig.Appserver.Terminated);
        Assert.True(result.Results[0].Get("appserver_unverified") is { IsTruthy: true });
        Assert.True(result.Results[0].Get("closed") is { IsTruthy: true });
        Assert.Contains(warnings, w => w.Contains("appserver pid not verified", StringComparison.Ordinal) && w.Contains("Its team row stays.", StringComparison.Ordinal));
    }

    [Fact]
    public void AReconcileKeepsTheRowOfAMemberWhoseAppserverWasNotVerified()
    {
        var rig = ScenarioRig.Create();
        var team = JsonText.Parse(
            "{\"team_id\":\"t\",\"members\":[{\"name\":\"alpha-implementor\",\"role\":\"implementor\"},{\"name\":\"alpha-reviewer\",\"role\":\"reviewer\"}]}");
        var rows = new FakeRowStore { Fresh = team.AsObject() };
        var results = new List<JsonObject>
        {
            new() { ["name"] = "alpha-implementor", ["transport_id"] = "w1:p2", ["closed"] = true, ["appserver_unverified"] = true },
            new() { ["name"] = "alpha-reviewer", ["transport_id"] = "w1:p3", ["closed"] = true },
        };

        var outcome = TeardownOver(rig, rows).ReconcileAfterClose("/d", team, results, "/d/teams/alpha.json", rig.Context);

        Assert.Equal(["alpha-reviewer"], outcome.Pruned);
        Assert.Single(outcome.Kept);
        Assert.Equal("appserver-unverified", Text(outcome.Kept[0].Get("why")));
    }

    [Fact]
    public void ClosedPanesAreVerifiedGone()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p2"] = "alpha-implementor";
        live["w1:p3"] = "alpha-reviewer";
        var warnings = new List<string>();

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "w1:p2"), Target("alpha-reviewer", "w1:p3")], "herdr", warnings, rig.Context);

        Assert.All(result.Results, r => Assert.True(r.Get("closed") is { IsTruthy: true }));
        Assert.Empty(result.StillLive);
        Assert.Empty(warnings);
        Assert.Empty(live);
    }

    [Fact]
    public void ACloseThatTheTransportStillListsIsNotReportedClosed()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig, stuck: ["w1:p2"]);
        live["w1:p2"] = "alpha-implementor";
        var warnings = new List<string>();

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "w1:p2")], "herdr", warnings, rig.Context);

        Assert.False(result.Results[0].Get("closed") is { IsTruthy: true });
        Assert.Equal("still listed after the close command", Text(result.Results[0].Get("error")));
        Assert.Equal(["alpha-implementor"], result.StillLive);
        Assert.Contains("After close, 1 is still live: alpha-implementor.", warnings);
        Assert.Equal(12, rig.Sleeper.Sleeps.Count);
    }

    [Fact]
    public void APaneNowCarryingAnotherRoleIsNotClosed()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p2"] = "alpha-reviewer";
        var warnings = new List<string>();

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "w1:p2")], "herdr", warnings, rig.Context);

        Assert.False(result.Results[0].Get("closed") is { IsTruthy: true });
        Assert.Equal("pane now carries alpha-reviewer", Text(result.Results[0].Get("error")));
        Assert.Contains("Pane w1:p2 now carries alpha-reviewer, not alpha-implementor. It was not closed.", warnings);
        Assert.DoesNotContain("herdr pane close w1:p2", rig.Mux.Calls);
    }

    [Fact]
    public void APaneCarryingAnotherNameForTheSameAgentSessionIsClosed()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig, sessions: new Dictionary<string, string> { ["w1:p2"] = "328c219e-aaaa" });
        live["w1:p2"] = "alpha-reviewer";
        var target = Target("implementor@328c219e", "w1:p2");
        target["session_id"] = "328c219e-aaaa";

        var result = TeardownOver(rig).CloseAndVerify([target], "herdr", [], rig.Context);

        Assert.True(result.Results[0].Get("closed") is { IsTruthy: true });
    }

    [Fact]
    public void APaneCarryingAnotherNameAndAnotherAgentSessionIsNotClosed()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig, sessions: new Dictionary<string, string> { ["w1:p2"] = "ffffffff-bbbb" });
        live["w1:p2"] = "alpha-reviewer";
        var target = Target("alpha-implementor", "w1:p2");
        target["session_id"] = "328c219e-aaaa";

        var result = TeardownOver(rig).CloseAndVerify([target], "herdr", [], rig.Context);

        Assert.False(result.Results[0].Get("closed") is { IsTruthy: true });
        Assert.DoesNotContain("herdr pane close w1:p2", rig.Mux.Calls);
    }

    [Fact]
    public void APaneNowCarryingTheSameRoleUnderTheSamePrefixIsClosed()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p2"] = "alpha-implementor-2";

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "w1:p2")], "herdr", [], rig.Context);

        Assert.True(result.Results[0].Get("closed") is { IsTruthy: true });
    }

    [Fact]
    public void AStrayIsClosedOnlyWhenItsExactNameIsConfirmedAgain()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p2"] = "alpha-implementor-2";
        var warnings = new List<string>();

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "w1:p2", stray: true)], "herdr", warnings, rig.Context);

        Assert.False(result.Results[0].Get("closed") is { IsTruthy: true });
        Assert.True(result.Results[0].Get("stray") is { IsTruthy: true });
        Assert.DoesNotContain("herdr pane close w1:p2", rig.Mux.Calls);
    }

    [Fact]
    public void AStrayWhoseNameCannotBeReCheckedIsNotClosed()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Override = r => r.Argv.Count > 2 && r.Argv[1] == "agent" && r.Argv[2] == "list" ? FakeMux.Exit(1, string.Empty, "daemon down") : null;
        var warnings = new List<string>();

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "w1:p2", stray: true)], "herdr", warnings, rig.Context);

        Assert.Equal("pane could not be re-checked", Text(result.Results[0].Get("error")));
        Assert.Single(warnings, w => w.StartsWith("Pane w1:p2 could not be re-checked before closing it", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonStrayWhoseTopologyCannotBeReadIsStillClosedButUnverified()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Override = r => r.Argv.Count > 2 && r.Argv[1] == "agent" && r.Argv[2] == "list" ? FakeMux.Exit(1, string.Empty, "daemon down") : null;
        var warnings = new List<string>();

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "w1:p2")], "herdr", warnings, rig.Context);

        Assert.Contains("herdr pane close w1:p2", rig.Mux.Calls);
        Assert.False(result.Results[0].Get("closed") is { IsTruthy: true });
        Assert.StartsWith("could not verify the close:", Text(result.Results[0].Get("error")), StringComparison.Ordinal);
        Assert.Contains(warnings, w => w.Contains("herdr could not be queried", StringComparison.Ordinal));
    }

    [Fact]
    public void TmuxPanesAreClosedByArgvAndVerifiedAgainstTheListedPanes()
    {
        var rig = ScenarioRig.Create(env: v => v.Remove("HERDR_ENV"));
        var live = new HashSet<string> { "%1", "%2" };
        rig.Mux.Tmux = argv =>
        {
            if (argv.Count > 3 && argv[1] == "kill-pane")
            {
                live.Remove(argv[3]);
                return FakeMux.Exit(0);
            }

            return argv.Count > 1 && argv[1] == "list-panes" ? FakeMux.Out(string.Join('\n', live) + "\n") : null;
        };

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "%1")], "tmux", [], rig.Context);

        Assert.True(result.Results[0].Get("closed") is { IsTruthy: true });
        Assert.Contains("tmux kill-pane -t %1", rig.Mux.Calls);
        Assert.Equal(["%2"], live);
    }

    [Theory]
    [InlineData("notes")]
    [InlineData("")]
    public void AMemberIsNotClosedWhenItsPaneNowCarriesANameThatDoesNotParseOrNoName(string current)
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p2"] = current;
        var warnings = new List<string>();

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "w1:p2")], "herdr", warnings, rig.Context);

        Assert.False(result.Results[0].Get("closed") is { IsTruthy: true });
        Assert.DoesNotContain("herdr pane close w1:p2", rig.Mux.Calls);
        Assert.Contains(warnings, w => w.StartsWith("Pane w1:p2 now carries ", StringComparison.Ordinal) && w.EndsWith("It was not closed.", StringComparison.Ordinal));
    }

    [Fact]
    public void AMemberIsClosedWhenItsPaneIsGoneOrStillCarriesItsOwnName()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p3"] = "alpha-reviewer";

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "w1:p2"), Target("alpha-reviewer", "w1:p3")], "herdr", [], rig.Context);

        Assert.All(result.Results, r => Assert.True(r.Get("closed") is { IsTruthy: true }));
        Assert.Contains("herdr pane close w1:p3", rig.Mux.Calls);
    }

    [Theory]
    [InlineData("other:")]
    [InlineData("-t")]
    [InlineData("%")]
    [InlineData("%1x")]
    [InlineData("w1:p2")]
    public void ATmuxTargetThatIsNotAPaneIdIsNeverPassedToTmux(string id)
    {
        var rig = ScenarioRig.Create();
        var live = new HashSet<string> { "%1" };
        rig.Mux.Tmux = argv =>
        {
            if (argv.Count > 3 && argv[1] == "kill-pane")
            {
                live.Remove(argv[3]);
                return FakeMux.Exit(0);
            }

            return argv.Count > 1 && argv[1] == "list-panes" ? FakeMux.Out(string.Join('\n', live) + "\n") : null;
        };

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", id)], "tmux", [], rig.Context);

        Assert.False(result.Results[0].Get("closed") is { IsTruthy: true });
        Assert.Contains("is not a tmux pane id", Text(result.Results[0].Get("error")), StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Mux.Calls, call => call.Contains("kill-pane", StringComparison.Ordinal));
    }

    [Fact]
    public void ATmuxKillPaneIsBoundedAtTenSeconds()
    {
        var rig = ScenarioRig.Create();
        var live = new HashSet<string> { "%1" };
        rig.Mux.Tmux = argv =>
        {
            if (argv.Count > 3 && argv[1] == "kill-pane")
            {
                live.Remove(argv[3]);
                return FakeMux.Exit(0);
            }

            return argv.Count > 1 && argv[1] == "list-panes" ? FakeMux.Out(string.Join('\n', live) + "\n") : null;
        };

        TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "%1")], "tmux", [], rig.Context);

        var kill = rig.Mux.Requests.Single(r => r.Argv.Contains("kill-pane"));
        Assert.Equal(TimeSpan.FromSeconds(10), kill.Timeout);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("w1:2")]
    [InlineData("w1K:t1Q")]
    [InlineData("p23")]
    [InlineData("w1K:")]
    [InlineData(":p1")]
    [InlineData("w1-x:p1")]
    [InlineData("w1K:p")]
    [InlineData("w1K:p2:3")]
    public void AHerdrTargetThatIsNotAPaneIdIsNeverPassedToHerdr(string id)
    {
        var rig = ScenarioRig.Create();
        LivePanes(rig);

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", id)], "herdr", [], rig.Context);

        Assert.False(result.Results[0].Get("closed") is { IsTruthy: true });
        Assert.DoesNotContain(rig.Mux.Calls, call => call.StartsWith("herdr pane close", StringComparison.Ordinal));
    }

    [Fact]
    public void ATerminalTransportHasNoPaneToClose()
    {
        var rig = ScenarioRig.Create();

        var result = TeardownOver(rig).CloseAndVerify([Target("alpha-implementor", "w1:p2")], "terminal", [], rig.Context);

        Assert.False(result.Results[0].Get("closed") is { IsTruthy: true });
        Assert.Contains("has no addressable pane to close", Text(result.Results[0].Get("error")), StringComparison.Ordinal);
    }

    [Fact]
    public void APlanTokenBindsTheTeamAndTheSortedPaneIds()
    {
        var rig = ScenarioRig.Create();
        var td = TeardownOver(rig);
        JsonValue[] Rows(params string[] panes) => panes.Select(p => (JsonValue)new JsonObject { ["transport_id"] = p }).ToArray();

        var token = td.CloseToken("t-1", Rows("%2", "%1"));

        Assert.Equal(token, td.CloseToken("t-1", Rows("%1", "%2")));
        Assert.NotEqual(token, td.CloseToken("t-2", Rows("%1", "%2")));
        Assert.NotEqual(token, td.CloseToken("t-1", Rows("%1", "%2", "%3")));
        Assert.Equal(16, token.Length);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("{\"team_id\":\"t-1\",\"ids\":[\"%1\",\"%2\"]}"))).ToLowerInvariant()[..16];
        Assert.Equal(expected, token);
    }

    [Fact]
    public void PlanTokenIgnoresRowsWithNoPane()
    {
        var td = TeardownOver(ScenarioRig.Create());
        var withNull = new JsonValue[] { new JsonObject { ["transport_id"] = "%1" }, new JsonObject { ["transport_id"] = JsonValue.Null }, new JsonObject { ["name"] = "x" } };
        var without = new JsonValue[] { new JsonObject { ["transport_id"] = "%1" } };

        Assert.Equal(td.CloseToken("t", without), td.CloseToken("t", withNull));
    }

    [Fact]
    public void ClosableMembersNeedAPaneOrMailRouteAndAnAddress()
    {
        var td = TeardownOver(ScenarioRig.Create());
        JsonValue Row(string route, JsonValue pane) => new JsonObject { ["route"] = route, ["transport_id"] = pane };

        var closable = td.ClosableMembers([Row("peer", "%1"), Row("pane", "%2"), Row("mail", "%3"), Row("subagent", "%4"), Row("peer", JsonValue.Null)]);

        Assert.Equal(["%1", "%2", "%3"], closable.Select(m => Text(m.Member("transport_id"))));
    }

    [Fact]
    public void PeerTargetsResolveByPaneThenSessionThenPrefixThenName()
    {
        var rig = ScenarioRig.Create();
        var td = TeardownOver(rig);
        JsonValue[] Peers() =>
        [
            new JsonObject { ["name"] = "alpha-implementor", ["transport_id"] = "%1", ["session_id"] = "sess-0123456789" },
            new JsonObject { ["name"] = "alpha-reviewer", ["transport_id"] = "%2", ["session_id"] = "sess-9876543210" },
        ];

        Assert.Equal("alpha-implementor", Text(td.ResolvePeerTarget(Peers(), "%1", "tmux", rig.Context).Member?.Member("name")));
        Assert.Equal("alpha-reviewer", Text(td.ResolvePeerTarget(Peers(), "sess-9876543210", "tmux", rig.Context).Member?.Member("name")));
        Assert.Equal("alpha-implementor", Text(td.ResolvePeerTarget(Peers(), "sess-0123", "tmux", rig.Context).Member?.Member("name")));
        Assert.Equal("alpha-reviewer", Text(td.ResolvePeerTarget(Peers(), "alpha-reviewer", "tmux", rig.Context).Member?.Member("name")));
        Assert.Null(td.ResolvePeerTarget(Peers(), "nobody", "tmux", rig.Context).Member);
    }

    [Fact]
    public void ASessionPrefixUnderEightCharactersIsNotAMatch()
    {
        var rig = ScenarioRig.Create();
        var td = TeardownOver(rig);
        JsonValue[] peers = [new JsonObject { ["name"] = "a", ["session_id"] = "sess-0123456789" }];

        Assert.Null(td.ResolvePeerTarget(peers, "sess-01", "tmux", rig.Context).Member);
    }

    [Fact]
    public void TwoMatchesNeverPickAndNameEveryCandidate()
    {
        var rig = ScenarioRig.Create();
        var td = TeardownOver(rig);
        JsonValue[] peers =
        [
            new JsonObject { ["name"] = "alpha-implementor", ["transport_id"] = "%1", ["session_id"] = "sess-0123456789", ["role"] = "implementor" },
            new JsonObject { ["name"] = "other-implementor", ["transport_id"] = "%2", ["session_id"] = "sess-01234ffff1", ["role"] = "implementor" },
        ];

        var target = td.ResolvePeerTarget(peers, "sess-01234", "tmux", rig.Context);

        Assert.Null(target.Member);
        Assert.StartsWith("dismiss: \"sess-01234\" is ambiguous — 2 live peers match: ", target.Fail, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"alpha-implementor\"", target.Fail, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"other-implementor\"", target.Fail, StringComparison.Ordinal);
        Assert.EndsWith("— re-run with a pane_id or a full session_id; nothing was closed", target.Fail, StringComparison.Ordinal);
    }

    [Fact]
    public void AHerdrDisplayNameResolvesLastAndOnlyOnAHerdrTeam()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p5"] = "display-name";
        var td = TeardownOver(rig);
        JsonValue[] peers = [new JsonObject { ["name"] = "alpha-implementor", ["transport_id"] = "w1:p5" }];

        var viaHerdr = td.ResolvePeerTarget(peers, "display-name", "herdr", rig.Context);
        var viaTmux = td.ResolvePeerTarget(peers, "display-name", "tmux", rig.Context);

        Assert.Equal("alpha-implementor", Text(viaHerdr.Member?.Member("name")));
        Assert.Null(viaTmux.Member);
    }

    [Fact]
    public void ReconcileKeepsTheRowsWhoseSessionMayStillExistWithTheReason()
    {
        var rig = ScenarioRig.Create();
        var store = new FakeRowStore
        {
            Fresh = JsonText.Parse(
                """
                {"members":[
                  {"name":"alpha-implementor","role":"implementor","route":"peer"},
                  {"name":"alpha-reviewer","role":"reviewer","route":"peer"},
                  {"name":"alpha-late","role":"architect","route":"peer"},
                  {"name":"alpha-silent","role":"architect","route":"peer"}
                ]}
                """).AsObject(),
        };
        var snapshot = JsonText.Parse("{\"members\":[{\"name\":\"alpha-implementor\"},{\"name\":\"alpha-reviewer\"},{\"name\":\"alpha-silent\"}]}");
        var results = new[]
        {
            new JsonObject { ["name"] = "alpha-implementor", ["closed"] = true },
            new JsonObject { ["name"] = "alpha-reviewer", ["closed"] = false },
        };

        var outcome = TeardownOver(rig, store).ReconcileAfterClose(rig.Cwd, snapshot, results, rig.Cwd + "/team.json", rig.Context);

        Assert.Equal(["alpha-implementor"], outcome.Pruned);
        Assert.Equal(
            [("alpha-reviewer", "close-failed"), ("alpha-late", "added"), ("alpha-silent", "indeterminate")],
            outcome.Kept.Select(k => (Text(k.Get("name")), Text(k.Get("why")))).ToArray());
        Assert.False(outcome.TeamRemoved);
    }

    [Fact]
    public void ReconcileOfAWholeTeamClosedClearsTheFile()
    {
        var rig = ScenarioRig.Create();
        var store = new FakeRowStore { Fresh = JsonText.Parse("{\"members\":[{\"name\":\"alpha-implementor\",\"role\":\"implementor\"}]}").AsObject() };
        var snapshot = JsonText.Parse("{\"members\":[{\"name\":\"alpha-implementor\"}]}");

        var outcome = TeardownOver(rig, store).ReconcileAfterClose(rig.Cwd, snapshot, [new JsonObject { ["name"] = "alpha-implementor", ["closed"] = true }], rig.Cwd + "/team.json", rig.Context);

        Assert.True(outcome.TeamRemoved);
        Assert.Equal(["alpha-implementor"], outcome.Pruned);
        Assert.Empty(outcome.Kept);
    }

    // ---- paste

    private const string IdleClaude = "  earlier output\n\n────────────────────────────────────────\n❯ \n────────────────────────────────────────\n  ? for shortcuts";

    private static string Staged(string payload) =>
        "  earlier output\n\n────────────────────────────────────────\n❯ " + payload + "\n────────────────────────────────────────\n  ? for shortcuts";

    [Fact]
    public void AKnownDeadRowDoesNotHideALiveAgentOfTheSameNameButAKnownLiveRowDoes()
    {
        var rig = ScenarioRig.Create();
        var live = LivePanes(rig);
        live["w1:p5"] = "alpha-implementor";
        var td = TeardownOver(rig);
        var dir = rig.Cwd + "/.claude/ah2";
        JsonValue Known(bool alive) => new JsonObject { ["name"] = "alpha-implementor", ["pane_id"] = "w1:p5", ["live"] = alive };

        var withDeadRow = td.LiveRegistrySlots(dir, new TeamScope("alpha"), rig.Context, [Known(false)]);
        var withLiveRow = td.LiveRegistrySlots(dir, new TeamScope("alpha"), rig.Context, [Known(true)]);

        Assert.Contains(withDeadRow, s => Text(s.Member("name")) == "alpha-implementor");
        Assert.DoesNotContain(withLiveRow, s => Text(s.Member("name")) == "alpha-implementor");
    }

    private static PasteService PasteOver(ScenarioRig rig, ILockProvider? locks = null) => new(
        rig.Env,
        rig.Io.Files,
        rig.Io.Writer,
        rig.Clock,
        rig.Mux,
        rig.Config,
        rig.Screens,
        rig.Harnesses,
        rig.Mailbox,
        locks ?? PlatformModule.CreateLockProvider(rig.Clock, new Ah.TestKit.SequenceRandom(), PasteService.LockStaleAfter),
        rig.Sleeper,
        4242,
        rig.Herdr);

    private static JsonObject Member(string name = "alpha-implementor", string? pane = "%1", JsonObject? extra = null)
    {
        var m = new JsonObject { ["name"] = name, ["role"] = "implementor", ["route"] = "pane" };
        if (pane is not null)
        {
            m["transport_id"] = pane;
        }

        if (extra is not null)
        {
            foreach (var key in extra.Keys)
            {
                m[key] = extra[key];
            }
        }

        return m;
    }

    /// <summary>A tmux whose pane shows <c>screen</c>, takes a staged paste into the composer and returns to idle on Enter.</summary>
    private static Func<string> TmuxPane(ScenarioRig rig, string initial, bool submits = true, bool staging = true)
    {
        var screen = initial;
        rig.Mux.Tmux = argv =>
        {
            var sub = argv.Count > 1 ? argv[1] : string.Empty;
            switch (sub)
            {
                case "capture-pane":
                    return FakeMux.Out(screen);
                case "load-buffer":
                    rig.Mux.Buffers[argv[3]] = File.ReadAllText(argv[4]);
                    return FakeMux.Exit(0);
                case "paste-buffer":
                    if (staging)
                    {
                        screen = Staged(rig.Mux.Buffers[argv[6]]);
                    }

                    return FakeMux.Exit(0);
                case "send-keys":
                    if (submits)
                    {
                        screen = IdleClaude;
                    }

                    return FakeMux.Exit(0);
                default:
                    return null;
            }
        };
        return () => screen;
    }

    private static string LastAttempt(ScenarioRig rig)
    {
        var path = rig.Sandbox.Root + "/ah/run/paste/attempts.jsonl";
        return File.ReadAllLines(path).Last();
    }

    private static PasteOptions Options(JsonObject member, string payload, string transport = "tmux", PasteEntry? entry = null, string? activity = null, bool wait = false) =>
        new(member, "alpha", transport, payload, entry, activity, wait);

    [Theory]
    [InlineData("")]
    [InlineData("two\nlines")]
    [InlineData("carriage\rreturn")]
    public void BadPayloadsAreRefusedBeforeAnythingIsRead(string payload)
    {
        var rig = ScenarioRig.Create();

        var result = PasteOver(rig).Paste(Options(Member(), payload));

        Assert.Equal("refused", Text(result.Get("outcome")));
        Assert.Equal("bad_request", Text(result.Get("reason")));
        Assert.Empty(rig.Mux.Calls);
    }

    [Theory]
    [InlineData("../x", "alpha-implementor")]
    [InlineData("a/b", "alpha-implementor")]
    [InlineData("alpha", "../x")]
    [InlineData("alpha", "a/b")]
    [InlineData("alpha", "")]
    public void ATeamOrMemberNameThatCouldLeaveTheLockDirectoryIsRefusedBeforeAnyLockIsMade(string team, string member)
    {
        var rig = ScenarioRig.Create(env: v => v["AH_PASTE_VERIFY_MS"] = "0");
        TmuxPane(rig, IdleClaude);

        var result = PasteOver(rig).Paste(new PasteOptions(Member(member), team, "tmux", "hello"));

        Assert.Equal("refused", Text(result.Get("outcome")));
        Assert.Equal("bad_request", Text(result.Get("reason")));
        Assert.False(Directory.Exists(rig.Sandbox.Root + "/ah/run/paste") && Directory.EnumerateFiles(rig.Sandbox.Root + "/ah/run/paste", "*.lock").Any());
        Assert.False(File.Exists(rig.Sandbox.Root + "/ah/run/x-alpha-implementor.lock"));
    }

    [Theory]
    [InlineData("tmux", "%12", true)]
    [InlineData("tmux", "main:0.1", false)]
    [InlineData("tmux", "%", false)]
    [InlineData("tmux", "%1:2", false)]
    [InlineData("herdr", "w1:p1", true)]
    [InlineData("herdr", "w1K:p23", true)]
    [InlineData("herdr", "w1K:p2C", true)]
    [InlineData("herdr", "w1K:t1Q", false)]
    [InlineData("herdr", "p23", false)]
    [InlineData("herdr", "w1K:", false)]
    [InlineData("herdr", ":p1", false)]
    [InlineData("herdr", "w1-p1", false)]
    [InlineData("herdr", "-x", false)]
    [InlineData("herdr", "w1 p1", false)]
    public void OnlyAPaneIdOfTheTransportsOwnShapeIsPasteable(string transport, string pane, bool expected)
    {
        var rig = ScenarioRig.Create();

        var place = PasteOver(rig).PlacementOf(Member(pane: pane), transport);

        Assert.Equal(expected, place.Get("pasteable") is { IsTruthy: true });
        Assert.Equal(expected ? pane : null, place.Get("pane") is { IsText: true } p ? p.AsString() : null);
    }

    [Theory]
    [InlineData("alpha", "alpha-implementor", true)]
    [InlineData("20261009-113441-meee", "a.b_c-1", true)]
    [InlineData("a..b", "m", false)]
    [InlineData("a/b", "m", false)]
    [InlineData("a b", "m", false)]
    [InlineData("", "m", false)]
    [InlineData("a", "\\m", false)]
    public void OnlyPlainNamesFormALockFileName(string team, string member, bool expected) =>
        Assert.Equal(expected, LaunchModule.IsLockNamePart(team) && LaunchModule.IsLockNamePart(member));

    [Fact]
    public void ThePayloadLimitIsInclusive()
    {
        var rig = ScenarioRig.Create(env: v => v["AH_PASTE_VERIFY_MS"] = "0");
        TmuxPane(rig, IdleClaude);

        var atLimit = PasteOver(rig).Paste(Options(Member(), new string('x', 500)));
        var over = PasteOver(rig).Paste(Options(Member(), new string('x', 501)));

        Assert.NotEqual("bad_request", Text(atLimit.Get("reason")));
        Assert.Equal("bad_request", Text(over.Get("reason")));
        Assert.Equal(501, (int)over.Get("chars")!.Value.AsNumber());
    }

    [Fact]
    public void ALongerLimitMovesTheInclusiveEdgeByExactlyThatMuch()
    {
        var rig = ScenarioRig.Create(env: v => v["AH_PASTE_VERIFY_MS"] = "0");
        TmuxPane(rig, IdleClaude);
        var header = 45;

        var atLimit = PasteOver(rig).Paste(Options(Member(), new string('x', 500 + header)) with { MaxChars = PasteOptions.DefaultMaxChars + header });
        var over = PasteOver(rig).Paste(Options(Member(), new string('x', 501 + header)) with { MaxChars = PasteOptions.DefaultMaxChars + header });

        Assert.NotEqual("bad_request", Text(atLimit.Get("reason")));
        Assert.Equal("bad_request", Text(over.Get("reason")));
        Assert.Equal(501 + header, (int)over.Get("chars")!.Value.AsNumber());
    }

    [Fact]
    public void AMemberWithPasteDisabledIsRefused()
    {
        var rig = ScenarioRig.Create();

        var result = PasteOver(rig).Paste(Options(Member(extra: new JsonObject { ["paste"] = false }), "hello"));

        Assert.Equal("paste-disabled", Text(result.Get("reason")));
        Assert.Empty(rig.Mux.Calls);
    }

    [Fact]
    public void AMemberWithPasteSetTrueIsNotDisabled()
    {
        var rig = ScenarioRig.Create(env: v => v["AH_PASTE_VERIFY_MS"] = "0");
        TmuxPane(rig, IdleClaude);

        var result = PasteOver(rig).Paste(Options(Member(extra: new JsonObject { ["paste"] = true }), "hello"));

        Assert.NotEqual("paste-disabled", Text(result.Get("reason")));
    }

    [Fact]
    public void AMemberWithNoPaneOrOnATerminalTeamIsRefusedAsNoPane()
    {
        var rig = ScenarioRig.Create();

        var noPane = PasteOver(rig).Paste(Options(Member(pane: null), "hello"));
        var terminal = PasteOver(rig).Paste(Options(Member(), "hello", transport: "terminal"));

        Assert.Equal("no-pane", Text(noPane.Get("reason")));
        Assert.Equal("no-pane", Text(terminal.Get("reason")));
        Assert.Empty(rig.Mux.Calls);
    }

    [Fact]
    public void ARowWithASourceIsOnHerdrWhateverTheTeamTransport()
    {
        var rig = ScenarioRig.Create();

        var place = PasteOver(rig).PlacementOf(Member(pane: "w1:p1", extra: new JsonObject { ["source"] = "herdr" }), "tmux");

        Assert.Equal("herdr", Text(place.Get("transport")));
        Assert.True(place.Get("pasteable") is { IsTruthy: true });
    }

    private sealed class NeverFree : ILockProvider
    {
        public IDisposable? TryAcquire(string lockPath) => null;

        public IDisposable? Acquire(string lockPath, TimeSpan wait) => null;
    }

    [Fact]
    public void ALockThatNeverClearsRefusesAsLockBusy()
    {
        var rig = ScenarioRig.Create();

        var result = PasteOver(rig, new NeverFree()).Paste(Options(Member(), "hello"));

        Assert.Equal("lock-busy", Text(result.Get("reason")));
        Assert.Empty(rig.Mux.Calls);
    }

    private static void SeedPasteLock(ScenarioRig rig, int secondsOld)
    {
        var path = $"{rig.Env.AhHome}/run/paste/alpha-alpha-implementor.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var at = rig.Clock.UtcNow.ToUnixTimeMilliseconds() - (secondsOld * 1000L);
        File.WriteAllText(path, $"{{\"pid\":999999,\"at\":{at.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"nonce\":\"other\"}}");
    }

    [Fact]
    public void APasteLockHeldForLessThanTheStaleAgeRefusesAsLockBusy()
    {
        var rig = ScenarioRig.Create();
        SeedPasteLock(rig, secondsOld: 59);

        var result = PasteOver(rig).Paste(Options(Member(), "hello"));

        Assert.Equal("lock-busy", Text(result.Get("reason")));
        Assert.Empty(rig.Mux.Calls);
    }

    [Fact]
    public void APasteLockOlderThanTheStaleAgeIsTakenOverAndThePasteGoesOn()
    {
        var rig = ScenarioRig.Create();
        SeedPasteLock(rig, secondsOld: 61);

        var result = PasteOver(rig).Paste(Options(Member(), "hello"));

        Assert.NotEqual("lock-busy", Text(result.Get("reason")));
        Assert.NotEmpty(rig.Mux.Calls);
    }

    [Fact]
    public void HerdrAgentNotFoundIsNotLive()
    {
        var rig = ScenarioRig.Create();

        var result = PasteOver(rig).Paste(Options(Member(pane: "w1:p1"), "hello", transport: "herdr"));

        Assert.Equal("refused", Text(result.Get("outcome")));
        Assert.Equal("not-live", Text(result.Get("reason")));
    }

    [Fact]
    public void AWorkingAgentIsBusyAndNothingIsTyped()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Agents["alpha-implementor"] = "working";
        rig.Mux.Screens["alpha-implementor"] = IdleClaude;

        var result = PasteOver(rig).Paste(Options(Member(pane: "w1:p1"), "hello", transport: "herdr"));

        Assert.Equal("busy", Text(result.Get("reason")));
        Assert.DoesNotContain(rig.Mux.Calls, c => c.Contains("agent prompt", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordedWorkingActivityBlocksAnIdleLookingAgent()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Agents["alpha-implementor"] = "idle";
        rig.Mux.Screens["alpha-implementor"] = IdleClaude;

        var result = PasteOver(rig).Paste(Options(Member(pane: "w1:p1"), "hello", transport: "herdr", activity: "working"));

        Assert.Equal("busy", Text(result.Get("reason")));
    }

    [Fact]
    public void ABlockedAgentIsRefusedWithItsPromptAndNothingIsTyped()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Agents["alpha-implementor"] = "blocked";
        rig.Mux.Screens["alpha-implementor"] = "something odd\n";

        var result = PasteOver(rig).Paste(Options(Member(pane: "w1:p1"), "hello", transport: "herdr"));

        Assert.Equal("blocked", Text(result.Get("reason")));
        Assert.Equal("harness-prompt", Text(result.Get("prompt")));
        Assert.DoesNotContain(rig.Mux.Calls, c => c.Contains("agent prompt", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnrecognisedScreenIsIndeterminate()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Agents["alpha-implementor"] = "idle";
        rig.Mux.Screens["alpha-implementor"] = "Loading...\n";

        var result = PasteOver(rig).Paste(Options(Member(pane: "w1:p1"), "hello", transport: "herdr"));

        Assert.Equal("indeterminate", Text(result.Get("reason")));
        Assert.Equal("the screen is not a recognised idle, empty composer", Text(result.Get("why")));
    }

    [Fact]
    public void HerdrPasteIsConfirmedWhenTheTurnStarts()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Agents["alpha-implementor"] = "idle";
        rig.Mux.Screens["alpha-implementor"] = IdleClaude;
        rig.Mux.Override = r =>
        {
            if (r.Argv.Count > 4 && r.Argv[1] == "agent" && r.Argv[2] == "prompt")
            {
                rig.Mux.Agents[r.Argv[3]] = "working";
                return FakeMux.Out("{}");
            }

            return null;
        };

        var result = PasteOver(rig).Paste(Options(Member(pane: "w1:p1"), "hello there", transport: "herdr"));

        Assert.Equal("confirmed", Text(result.Get("outcome")));
        Assert.Equal("turn-started", Text(result.Get("reason")));
        Assert.Equal("already", Text(result.Get("ack")));
        Assert.Contains("herdr agent prompt alpha-implementor hello there", rig.Mux.Calls);
        var row = JsonText.Parse(LastAttempt(rig));
        Assert.Equal("confirmed", Text(row.Member("outcome")));
        Assert.Equal("ready", Text(row.Member("readiness")));
        Assert.Equal("ladder", Text(row.Member("source")));
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "")]
    [InlineData(false, ",\"agent_status\":\"unknown\"")]
    public void AFreshHerdrPaneIsPastedOnlyOnceItHasAStatusElseLeftToTheMailbox(bool gainsStatus, string statusField)
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Agents["alpha-implementor"] = "idle";
        rig.Mux.Screens["alpha-implementor"] = IdleClaude;
        var gets = 0;
        rig.Mux.Override = r => r.Argv.Count > 3 && r.Argv[1] == "agent" && r.Argv[2] == "get" && (++gets == 1 || !gainsStatus)
            ? FakeMux.Out("{\"result\":{\"agent\":{\"name\":\"alpha-implementor\"" + statusField + "}}}")
            : null;

        var result = PasteOver(rig).Paste(Options(Member(pane: "w1:p1"), "hello", transport: "herdr"));

        Assert.Equal(gainsStatus, rig.Mux.Calls.Contains("herdr agent prompt alpha-implementor hello"));
        if (!gainsStatus)
        {
            Assert.Equal("mailed", Text(result.Get("outcome")));
            Assert.DoesNotContain(rig.Mux.Calls, c => c.Contains("send-keys", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void HerdrPromptFailureIsUnconfirmedAsTypeFailedWithTheTruncatedReason()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Agents["alpha-implementor"] = "idle";
        rig.Mux.Screens["alpha-implementor"] = IdleClaude;
        rig.Mux.Override = r => r.Argv.Count > 2 && r.Argv[1] == "agent" && r.Argv[2] == "prompt" ? FakeMux.Exit(1, string.Empty, new string('e', 300)) : null;

        var result = PasteOver(rig).Paste(Options(Member(pane: "w1:p1"), "hello", transport: "herdr"));

        Assert.Equal("unconfirmed", Text(result.Get("outcome")));
        Assert.Equal("type-failed", Text(result.Get("reason")));
        Assert.Equal(200, Text(result.Get("why")).Length);
    }

    [Fact]
    public void HerdrPasteWithNoActivityIsUnconfirmedAfterTheVerifyWindowAndNeverRetyped()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Agents["alpha-implementor"] = "idle";
        rig.Mux.Screens["alpha-implementor"] = IdleClaude;

        var result = PasteOver(rig).Paste(Options(Member(pane: "w1:p1"), "hello", transport: "herdr"));

        Assert.Equal("unconfirmed", Text(result.Get("outcome")));
        Assert.Equal("no-activity", Text(result.Get("reason")));
        Assert.Equal(1, rig.Mux.Calls.Count(c => c.Contains("agent prompt", StringComparison.Ordinal)));
        Assert.Equal(25, rig.Sleeper.Sleeps.Count);
    }

    [Fact]
    public void HerdrStagedPayloadGetsExactlyOneEnter()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Agents["alpha-implementor"] = "idle";
        rig.Mux.Screens["alpha-implementor"] = IdleClaude;
        rig.Mux.Override = r =>
        {
            if (r.Argv.Count > 4 && r.Argv[1] == "agent" && r.Argv[2] == "prompt")
            {
                rig.Mux.Screens[r.Argv[3]] = Staged(r.Argv[4]);
                return FakeMux.Out("{}");
            }

            return null;
        };

        var result = PasteOver(rig).Paste(Options(Member(pane: "w1:p1"), "hello", transport: "herdr"));

        Assert.Equal(1, rig.Mux.Calls.Count(c => c == "herdr agent send-keys alpha-implementor Enter"));
        Assert.Equal("no-activity", Text(result.Get("reason")));
        Assert.True(result.Get("entered") is { IsTruthy: true });
    }

    [Fact]
    public void TmuxPasteIsStagedEnteredOnceAndConfirmedWhenTheComposerEmpties()
    {
        var rig = ScenarioRig.Create();
        TmuxPane(rig, IdleClaude);

        var result = PasteOver(rig).Paste(Options(Member(), "hello there"));

        Assert.Equal("confirmed", Text(result.Get("outcome")));
        Assert.Equal("turn-started", Text(result.Get("reason")));
        Assert.Equal(1, rig.Mux.Calls.Count(c => c == "tmux send-keys -t %1 Enter"));
        var load = rig.Mux.Calls.Single(c => c.StartsWith("tmux load-buffer -b ah-paste-4242-", StringComparison.Ordinal));
        Assert.Contains("tmux paste-buffer -d -r -p -b ", string.Join('\n', rig.Mux.Calls), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(rig.Sandbox.Root + "/ah/run", "ah-paste-*"));
        Assert.Contains(".txt", load, StringComparison.Ordinal);
    }

    [Fact]
    public void TmuxStagedFileIsPrivateAndRemovedEvenWhenTheLoadFails()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Tmux = argv => argv.Count > 1 && argv[1] == "load-buffer" ? FakeMux.Exit(1, string.Empty, "no server") : argv.Count > 1 && argv[1] == "capture-pane" ? FakeMux.Out(IdleClaude) : null;

        var result = PasteOver(rig).Paste(Options(Member(), "hello"));

        Assert.Equal("unconfirmed", Text(result.Get("outcome")));
        Assert.Equal("type-failed", Text(result.Get("reason")));
        Assert.Equal("tmux load-buffer failed: no server", Text(result.Get("why")));
        Assert.Empty(Directory.GetFiles(rig.Sandbox.Root + "/ah/run", "ah-paste-*"));
    }

    [Fact]
    public void ATmuxPasteThatNeverLandsIsNotStagedAndNoEnterIsPressed()
    {
        var rig = ScenarioRig.Create();
        TmuxPane(rig, IdleClaude, staging: false);

        var result = PasteOver(rig).Paste(Options(Member(), "hello"));

        Assert.Equal("not-staged", Text(result.Get("reason")));
        Assert.DoesNotContain("tmux send-keys -t %1 Enter", rig.Mux.Calls);
    }

    [Fact]
    public void APersonsTextInTheComposerIsNeverSubmitted()
    {
        var rig = ScenarioRig.Create();
        var screen = IdleClaude;
        rig.Mux.Tmux = argv =>
        {
            var sub = argv.Count > 1 ? argv[1] : string.Empty;
            if (sub == "capture-pane")
            {
                return FakeMux.Out(screen);
            }

            if (sub == "paste-buffer")
            {
                screen = Staged("something else entirely");
            }

            return FakeMux.Exit(0);
        };

        var result = PasteOver(rig).Paste(Options(Member(), "hello"));

        Assert.Equal("composer-changed", Text(result.Get("reason")));
        Assert.DoesNotContain("tmux send-keys -t %1 Enter", rig.Mux.Calls);
    }

    [Fact]
    public void ATmuxPasteThatDoesNotSubmitIsUnconfirmedAsNoActivity()
    {
        var rig = ScenarioRig.Create();
        TmuxPane(rig, IdleClaude, submits: false);

        var result = PasteOver(rig).Paste(Options(Member(), "hello"));

        Assert.Equal("no-activity", Text(result.Get("reason")));
        Assert.True(result.Get("entered") is { IsTruthy: true });
        Assert.Equal(2, rig.Mux.Calls.Count(c => c == "tmux send-keys -t %1 Enter"));
    }

    private const string BusyClaude = "  ✻ Working… (esc to interrupt)\n\n────────────────────────────────────────\n❯ \n────────────────────────────────────────\n  ? for shortcuts";

    /// <summary>A tmux that stages the paste and ignores Enter until it has seen <c>enterThatSubmits</c> of them (0 = never), then turns busy.</summary>
    private static void TmuxIgnoringEnters(ScenarioRig rig, int enterThatSubmits)
    {
        var screen = IdleClaude;
        var enters = 0;
        rig.Mux.Tmux = argv =>
        {
            switch (argv.Count > 1 ? argv[1] : string.Empty)
            {
                case "capture-pane":
                    return FakeMux.Out(screen);
                case "load-buffer":
                    rig.Mux.Buffers[argv[3]] = File.ReadAllText(argv[4]);
                    return FakeMux.Exit(0);
                case "paste-buffer":
                    screen = Staged(rig.Mux.Buffers[argv[6]]);
                    return FakeMux.Exit(0);
                case "send-keys":
                    enters++;
                    if (enterThatSubmits > 0 && enters >= enterThatSubmits)
                    {
                        screen = BusyClaude;
                    }

                    return FakeMux.Exit(0);
                default:
                    return null;
            }
        };
    }

    [Fact]
    public void ATmuxComposerStillHoldingThePayloadGetsExactlyOneMoreEnterAndIsConfirmedWhenItGoesBusy()
    {
        var rig = ScenarioRig.Create();
        TmuxIgnoringEnters(rig, enterThatSubmits: 2);

        var result = PasteOver(rig).Paste(Options(Member(), "hello there"));

        Assert.Equal("confirmed", Text(result.Get("outcome")));
        Assert.Equal(2, rig.Mux.Calls.Count(c => c == "tmux send-keys -t %1 Enter"));
    }

    [Fact]
    public void ATmuxComposerThatNeverSubmitsIsUnconfirmedAfterTwoEntersNeverThree()
    {
        var rig = ScenarioRig.Create();
        TmuxIgnoringEnters(rig, enterThatSubmits: 0);

        var result = PasteOver(rig).Paste(Options(Member(), "hello there"));

        Assert.Equal("unconfirmed", Text(result.Get("outcome")));
        Assert.Equal(2, rig.Mux.Calls.Count(c => c == "tmux send-keys -t %1 Enter"));
    }

    [Fact]
    public void ARefusedPastesAttemptRowCarriesTheWhy()
    {
        var rig = ScenarioRig.Create();
        TmuxPane(rig, "  nothing recognisable here");

        var result = PasteOver(rig).Paste(Options(Member(), "hello"));

        Assert.Equal("indeterminate", Text(result.Get("reason")));
        var row = JsonText.Parse(LastAttempt(rig));
        Assert.Equal("the screen is not a recognised idle, empty composer", Text(row.Member("why")));
    }

    [Fact]
    public void AnAckFromTheRecipientsHookConfirmsWithoutPressingEnter()
    {
        var rig = ScenarioRig.Create();
        var box = rig.Sandbox.Root + "/box";
        Directory.CreateDirectory(box);
        File.WriteAllText(box + "/entries.jsonl", "{\"id\":\"entry-1\",\"from\":\"alpha-orchestrator\",\"text\":\"hi\"}\n");
        rig.Mailbox.AckIds(box, ["entry-1"], "hook");
        TmuxPane(rig, IdleClaude);

        var result = PasteOver(rig).Paste(Options(Member(), "hello", entry: new PasteEntry("entry-1", box)));

        Assert.Equal("confirmed", Text(result.Get("outcome")));
        Assert.Equal("hook-ack", Text(result.Get("reason")));
        Assert.DoesNotContain("tmux send-keys -t %1 Enter", rig.Mux.Calls);
    }

    [Fact]
    public void AConfirmedPasteAcksItsEntryByPaste()
    {
        var rig = ScenarioRig.Create();
        var box = rig.Sandbox.Root + "/box";
        Directory.CreateDirectory(box);
        File.WriteAllText(box + "/entries.jsonl", "{\"id\":\"entry-2\",\"from\":\"alpha-orchestrator\",\"text\":\"hi\"}\n");
        TmuxPane(rig, IdleClaude);

        var result = PasteOver(rig).Paste(Options(Member(), "hello", entry: new PasteEntry("entry-2", box)));

        Assert.Equal("paste", Text(result.Get("ack")));
        Assert.True(rig.Mailbox.IsAcked(box, "entry-2"));
        Assert.True(File.Exists(box + "/attempts.jsonl"));
    }

    [Fact]
    public void AnExplicitDeliverAttemptIsLoggedUnderTheRunDirectory()
    {
        var rig = ScenarioRig.Create();
        TmuxPane(rig, IdleClaude);

        PasteOver(rig).Paste(new PasteOptions(Member(), "alpha", "tmux", "hello", Source: "deliver"));

        var row = JsonText.Parse(LastAttempt(rig));
        Assert.Equal("deliver", Text(row.Member("source")));
        Assert.Equal("alpha-implementor", Text(row.Member("member")));
        Assert.Equal("%1", Text(row.Member("pane")));
    }

    [Fact]
    public void CodexStagedPayloadIsReadFromTheRowThreeFromTheBottomAndEnteredOnce()
    {
        var rig = ScenarioRig.Create();
        var codexIdle = "  earlier\n\n› Ask Codex to do anything\n\n  gpt-5 · ~/repo";
        var screen = codexIdle;
        rig.Mux.Tmux = argv =>
        {
            var sub = argv.Count > 1 ? argv[1] : string.Empty;
            switch (sub)
            {
                case "capture-pane":
                    return FakeMux.Out(screen);
                case "load-buffer":
                    rig.Mux.Buffers[argv[3]] = File.ReadAllText(argv[4]);
                    return FakeMux.Exit(0);
                case "paste-buffer":
                    screen = "  earlier\n\n› " + rig.Mux.Buffers[argv[6]] + "\n\n  gpt-5 · ~/repo";
                    return FakeMux.Exit(0);
                case "send-keys":
                    screen = codexIdle;
                    return FakeMux.Exit(0);
                default:
                    return null;
            }
        };
        var member = Member(extra: new JsonObject { ["kind"] = "codex" });

        var result = PasteOver(rig).Paste(Options(member, "a long brief"));

        Assert.Equal("unconfirmed", Text(result.Get("outcome")));
        Assert.Equal("no-activity", Text(result.Get("reason")));
        Assert.True(result.Get("entered") is { IsTruthy: true });
        Assert.Equal(1, rig.Mux.Calls.Count(c => c == "tmux send-keys -t %1 Enter"));
    }

    private static Func<IReadOnlyList<string>, ProcessResult?> CodexComposer(ScenarioRig rig, Func<string, string> afterPaste, Action<string>? onLoad = null)
    {
        const string Idle = "  earlier\n\n› Ask Codex to do anything\n\n  gpt-5 · ~/repo";
        string Shown(string text) => "  earlier\n\n› " + text + "\n\n  gpt-5 · ~/repo";
        var screen = Idle;
        var pasted = string.Empty;
        return argv =>
        {
            var sub = argv.Count > 1 ? argv[1] : string.Empty;
            switch (sub)
            {
                case "capture-pane":
                    var current = screen;
                    if (pasted.Length > 0)
                    {
                        screen = Shown(afterPaste(pasted));
                    }

                    return FakeMux.Out(current);
                case "load-buffer":
                    onLoad?.Invoke(argv[4]);
                    rig.Mux.Buffers[argv[3]] = File.ReadAllText(argv[4]);
                    return FakeMux.Exit(0);
                case "paste-buffer":
                    pasted = rig.Mux.Buffers[argv[6]];
                    screen = Shown(afterPaste(pasted));
                    return FakeMux.Exit(0);
                case "send-keys":
                    pasted = string.Empty;
                    screen = Idle;
                    return FakeMux.Exit(0);
                default:
                    return null;
            }
        };
    }

    [Fact]
    public void AComposerHoldingOnlyAPrefixOfThePayloadIsStillTypingNotChanged()
    {
        var rig = ScenarioRig.Create();
        var reads = 0;
        rig.Mux.Tmux = CodexComposer(rig, full =>
        {
            reads++;
            return reads == 1 ? full[..6] : full;
        });
        var member = Member(extra: new JsonObject { ["kind"] = "codex" });

        var result = PasteOver(rig).Paste(Options(member, "a long brief"));

        Assert.NotEqual("composer-changed", Text(result.Get("reason")));
        Assert.True(result.Get("entered") is { IsTruthy: true });
        Assert.Equal(1, rig.Mux.Calls.Count(c => c == "tmux send-keys -t %1 Enter"));
    }

    [Fact]
    public void AComposerHoldingSomethingElseIsReportedChangedAndNothingIsEntered()
    {
        var rig = ScenarioRig.Create();
        rig.Mux.Tmux = CodexComposer(rig, _ => "someone typed this");
        var member = Member(extra: new JsonObject { ["kind"] = "codex" });

        var result = PasteOver(rig).Paste(Options(member, "a long brief"));

        Assert.Equal("composer-changed", Text(result.Get("reason")));
        Assert.DoesNotContain("tmux send-keys -t %1 Enter", rig.Mux.Calls);
    }

    [Fact]
    public void TheStagedPayloadFileIsReadableByItsOwnerOnly()
    {
        var rig = ScenarioRig.Create();
        UnixFileMode? mode = null;
        rig.Mux.Tmux = CodexComposer(rig, full => full, path => mode = File.GetUnixFileMode(path));
        var member = Member(extra: new JsonObject { ["kind"] = "codex" });

        PasteOver(rig).Paste(Options(member, "a long brief"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
