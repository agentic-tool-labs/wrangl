using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Launch;
using Ah.Platform;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>The bare mark on a member's row: how it is written, and that a matched one of a live process reads Live (bare) to the stall watch.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class BareHostTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private static readonly BareMark Matched = new(BareMark.Matched, 100, "t0", "sess-1", DateTimeOffset.Parse("2026-10-21T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture), []);

    private static JsonValue Team(string aExtra = "") =>
        JsonText.Parse("{\"members\":[{\"name\":\"a\",\"role\":\"implementor\",\"transport_id\":\"w1:p1\",\"model\":\"sonnet\"" + aExtra + "},{\"name\":\"b\",\"role\":\"reviewer\",\"transport_id\":\"w1:p2\"}],\"transport\":\"herdr\"}");

    [Fact]
    public void WritingAMarkSetsOnlyThatMembersBareFieldAndLeavesTheRestOfTheRecord()
    {
        var outcome = BareHost.Write(Team(), "a", Matched)!;

        var members = outcome.Record!.Value.Member("members")!.Value.AsArray();
        Assert.Equal(BareMark.Matched, BareMark.From(members[0].Member("bare"))!.State);
        Assert.Equal(100, BareMark.From(members[0].Member("bare"))!.Pid);
        Assert.Equal("sonnet", members[0].Member("model")!.Value.AsString());
        Assert.Null(members[1].Member("bare"));
        Assert.Equal("herdr", outcome.Record!.Value.Member("transport")!.Value.AsString());
    }

    [Fact]
    public void ClearingAMarkRemovesTheFieldAndClearingNoMarkChangesNothing()
    {
        var withMark = BareHost.Write(Team(), "a", Matched)!.Record!.Value;

        var cleared = BareHost.Write(withMark, "a", null)!.Record!.Value;

        Assert.Null(cleared.Member("members")!.Value.AsArray()[0].Member("bare"));
        Assert.Null(BareHost.Write(Team(), "a", null));
        Assert.Null(BareHost.Write(Team(), "nobody", Matched));
    }

    [Fact]
    public void AMarkSurvivesAWriteAndReadBack()
    {
        var read = BareMark.From(Matched.ToJson());

        Assert.Equal(Matched, read! with { Failed = [] });
        Assert.Equal(["M3", "M4"], BareMark.From((Matched with { State = BareMark.Unmatched, Failed = ["M3", "M4"] }).ToJson())!.Failed);
    }

    private StallHost NewHost(string teamJson, out StallTeam team, Func<int, string?, bool>? processIsThere = null, string dirName = "proj")
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        var dir = Path.Combine(_sandbox.Home, dirName, ".claude", "ah2");
        _sandbox.PlantTeam(Path.Combine(dir, "teams", "alpha.json"), teamJson);
        team = new StallTeam("alpha", dir, Path.Combine(_sandbox.Home, dirName), null, "orch");
        return new StallHost(graph, _sandbox.AhHome, () => true, () => null, processIsThere);
    }

    private const string TeamWithAHeadlessMember =
        "{\"members\":[{\"name\":\"a\",\"role\":\"implementor\",\"transport_id\":\"w1:p1\"},{\"name\":\"h\",\"role\":\"implementor\",\"transport_id\":\"headless:4242\"},{\"name\":\"n\",\"role\":\"reviewer\",\"transport_id\":\"headlessness\"}],\"transport\":\"herdr\"}";

    [Fact]
    public void AHeadlessMemberIsNotARecoveryRowSoNoRelaunchOrTellCanFollowFromIt()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        _ = NewHost(TeamWithAHeadlessMember, out var team);
        var host = new BareHost(graph, new StallHost(graph, _sandbox.AhHome, () => true, () => null));

        var names = host.Rows(team).Select(r => r.Name).ToList();

        Assert.DoesNotContain("h", names);
        Assert.Contains("a", names);
        Assert.Contains("n", names);
    }

    [Fact]
    public void AHeadlessMemberHasNoPaneForTheStallWatchAndAPaneIdThatMerelyStartsLikeItKeepsItsPane()
    {
        var host = NewHost(TeamWithAHeadlessMember, out var team);

        var members = host.Members(team);

        Assert.Null(members.Single(m => m.Name == "h").PaneId);
        Assert.Equal("w1:p1", members.Single(m => m.Name == "a").PaneId);
        Assert.Equal("headlessness", members.Single(m => m.Name == "n").PaneId);
    }

    private static string WithMark(BareMark mark) => JsonText.Stringify(Team(",\"bare\":" + JsonText.Stringify(JsonValue.FromObject(mark.ToJson()), 0)), 0);

    private string JudgeA(BareMark mark, Func<int, string?, bool> there, out string plainWhy)
    {
        var host = NewHost(WithMark(mark), out var team, there);
        var members = host.Members(team);
        plainWhy = host.Judge(team, members.Single(m => m.Name == "b")).Verdict.Why;
        return host.Judge(team, members.Single(m => m.Name == "a")).Verdict.Why;
    }

    [Fact]
    public void AMemberWithAMatchedBareMarkOfAProcessThatIsThereReadsLiveBareAndOneWithoutDoesNot()
    {
        var asked = new List<(int Pid, string? Start)>();

        var why = JudgeA(Matched, (pid, start) =>
        {
            asked.Add((pid, start));
            return true;
        }, out var plain);

        Assert.Contains("Live (bare)", why, StringComparison.Ordinal);
        Assert.DoesNotContain("Live (bare)", plain, StringComparison.Ordinal);
        Assert.Equal([(100, "t0")], asked);
    }

    [Fact]
    public void AMatchedMarkWhoseProcessIsGoneOrWhosePidWasReusedDoesNotReadLive()
    {
        Assert.DoesNotContain("Live (bare)", JudgeA(Matched, (_, _) => false, out _), StringComparison.Ordinal);
        Assert.DoesNotContain("Live (bare)", JudgeA(Matched, (pid, start) => pid == 100 && start == "another-start", out _), StringComparison.Ordinal);
        Assert.DoesNotContain("Live (bare)", JudgeA(Matched with { Pid = null }, (_, _) => true, out _), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmatchedMarkDoesNotMakeAMemberLive()
    {
        var unmatched = Matched with { State = BareMark.Unmatched, Failed = ["M3"] };

        Assert.DoesNotContain("Live (bare)", JudgeA(unmatched, (_, _) => true, out _), StringComparison.Ordinal);
    }

    [Fact]
    public void AProcessThatAnotherRowOfAnotherTeamRecordsAsItsOwnIsClaimedAndOneWithADifferentStartOrOnlyItsOwnRowIsNot()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        StallTeam MakeTeam(string name, string project, string member)
        {
            var dir = Path.Combine(_sandbox.Home, project, ".claude", "ah2");
            Directory.CreateDirectory(Path.Combine(dir, "teams"));
            File.WriteAllText(Path.Combine(dir, "teams", name + ".json"), "{\"members\":[{\"name\":\"" + member + "\",\"pid\":4242,\"pid_start\":\"S1\"}]}");
            return new StallTeam(name, dir, Path.Combine(_sandbox.Home, project), null, "orch");
        }

        var alpha = MakeTeam("alpha", "p1", "a");
        var beta = MakeTeam("beta", "p2", "x");
        var host = new BareHost(graph, new StallHost(graph, _sandbox.AhHome, () => true, () => null), () => [alpha, beta]);

        Assert.True(host.Claimed(null, 4242, "S1", "alpha", "a"));
        Assert.False(host.Claimed(null, 4242, "S2", "alpha", "a"));
        Assert.False(host.Claimed(null, 4243, "S1", "alpha", "a"));
        Assert.False(host.Claimed(null, 4242, null, "alpha", "a"));
        var onlyBeta = new BareHost(graph, new StallHost(graph, _sandbox.AhHome, () => true, () => null), () => [beta]);
        Assert.False(onlyBeta.Claimed(null, 4242, "S1", "beta", "x"));
    }

    private const string AgentList =
        "{\"result\":{\"agents\":[" +
        "{\"name\":\"p-architect\",\"terminal_title_stripped\":\"claude\",\"agent_session\":{\"value\":\"sess-1\"},\"pane_id\":\"1-1\",\"tab_id\":\"1\",\"workspace_id\":\"w1\",\"cwd\":\"/repo\",\"agent_status\":\"idle\"}," +
        "{\"name\":\"p-implementor\",\"terminal_title_stripped\":\"claude\",\"agent_session\":{\"value\":\"sess-2\"},\"pane_id\":\"1-2\",\"tab_id\":\"1\",\"workspace_id\":\"w1\",\"foreground_cwd\":\"/repo/wt\",\"agent_status\":\"working\"}," +
        "{\"name\":\"ah2-tm3-implementor-36\",\"pane_id\":\"w1K:p61\",\"agent_status\":\"idle\"}]}}";

    private string JournalText() => string.Join('\n', StallStore.JournalRows(_sandbox.AhHome));

    [Fact]
    public void AMembersIdleStatusIsReadFromTheAgentListEntryWhosePaneIdIsTheRowsTransportIdAndAMissingPaneIsJournaledOnce()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var runner = new FakeRunner { Output = argv => argv.Contains("agent") && argv.Contains("list") ? AgentList : string.Empty };
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, runner, new FakeDaemonClient());
        var host = new BareHost(graph, new StallHost(graph, _sandbox.AhHome, () => true, () => null));
        var team = new StallTeam("alpha", Path.Combine(_sandbox.Home, "p", ".claude", "ah2"), Path.Combine(_sandbox.Home, "p"), null, "orch");
        BareRow RowIn(string pane) => new("a", "implementor", new BareMember("alpha", "a", pane, null, new AhLaunchExpectation("wrangl:implementor", "a", "/t/teams/alpha.json", null, null)), null);

        Assert.Equal("idle", host.AgentStatus(team, RowIn("1-1")));
        Assert.Equal("working", host.AgentStatus(team, RowIn("1-2")));
        Assert.Equal("idle", host.AgentStatus(team, RowIn("w1K:p61")));
        Assert.Null(host.AgentStatus(team, RowIn("w1:p9")));
        Assert.Null(host.AgentStatus(team, RowIn("w1:p9")));

        var missing = JournalText().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.Contains("bare-pane-missing", StringComparison.Ordinal)).ToList();
        Assert.Single(missing);
        Assert.Contains("w1:p9", missing[0], StringComparison.Ordinal);
    }

    /// <summary>A relaunch whose only effect is <paramref name="move"/>: the team's new record, planted through the store.</summary>
    private sealed class MovingRelaunch(Action? move) : Ah.Commands.IMemberRelaunch
    {
        public string? Relaunch(string hierarchyDir, string projectRoot, string team, string member, string sessionId)
        {
            move?.Invoke();
            return null;
        }

        public string? Preflight(string hierarchyDir, string projectRoot, string team, string member, string sessionId) => null;

        public string? ReBrief(string hierarchyDir, string projectRoot, string team, string member) => null;
    }

    [Theory]
    [InlineData("w1:p9", true)]
    [InlineData(null, false)]
    public void ARelaunchThatLandedInAnotherPaneIsJournaledAndOneThatStayedIsNot(string? movedTo, bool journaled)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        const string record = "{\"members\":[{\"name\":\"a\",\"role\":\"implementor\",\"transport_id\":\"w1:p1\"}],\"transport\":\"herdr\"}";
        var stall = NewHost(record, out var team);
        var teamFile = Path.Combine(team.HierarchyDir, "teams", "alpha.json");
        var host = new BareHost(graph, stall, null, null, new MovingRelaunch(movedTo is null ? null : () => _sandbox.PlantTeam(teamFile, record.Replace("w1:p1", movedTo, StringComparison.Ordinal))));
        var row = new BareRow("a", "implementor", new BareMember("alpha", "a", "w1:p1", null, new AhLaunchExpectation("wrangl:implementor", "a", "/t/teams/alpha.json", null, null)), null);

        Assert.Null(host.Relaunch(team, row, "sess-1"));

        var lines = JournalText().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.Contains("restored pane", StringComparison.Ordinal)).ToList();
        Assert.Equal(journaled, lines.Count == 1);
        if (journaled)
        {
            Assert.Contains("restored pane w1:p1 was gone; launched in a new pane w1:p9", lines[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ParallelJournalWritersLoseNoRow()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        var stall = new StallHost(graph, _sandbox.AhHome, () => true, () => null);

        Parallel.For(0, 8, TestContext.Current.CancellationToken == default ? new ParallelOptions() : new ParallelOptions { CancellationToken = TestContext.Current.CancellationToken }, writer =>
        {
            for (var i = 0; i < 50; i++)
            {
                stall.Journal(new JsonObject { ["kind"] = "t", ["writer"] = writer, ["n"] = i });
            }
        });

        var lines = StallStore.JournalRows(_sandbox.AhHome);
        Assert.Equal(400, lines.Count);
        Assert.All(lines, l => Assert.Equal("t", JsonText.Parse(l).Member("kind")!.Value.AsString()));
    }

    [Fact]
    public void L1eALiveProcessWhoseCommandLineIsNotTheHarnessIsNotTakenAsExited()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, PlatformModule.CreateProcessRunner(), new FakeDaemonClient());
        var host = new BareHost(graph, new StallHost(graph, _sandbox.AhHome, () => true, () => null));
        var other =System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sleep", ["60"]) { UseShellExecute = false })!;
        try
        {
            Assert.False(host.WaitExit(other.Id, null, TimeSpan.FromMilliseconds(300)));
            Assert.False(host.WaitExit(other.Id, "not a start time", TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            other.Kill();
            other.WaitForExit();
        }

        Assert.True(host.WaitExit(other.Id, null, TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("held", true)]
    [InlineData("detected", true)]
    [InlineData("waiting-idle", true)]
    [InlineData("stopping", true)]
    [InlineData("launching", true)]
    [InlineData("done", false)]
    [InlineData("failed", false)]
    [InlineData("asked", false)]
    [InlineData("cleared", false)]
    public void TheStallWatchLeavesAMemberToTheRecoveryWhileItsRecordIsPendingOrHeldAndNoLonger(string state, bool covered)
    {
        var host = NewHost(JsonText.Stringify(Team(), 0), out var team);
        var member = host.Members(team).Single(m => m.Name == "b");
        Assert.False(host.RecoveryCovers(team, member));
        Ah.StateDb.StateDbModule.CreateRecoverRecords(_sandbox.AhHome).Write(WatchModule.RecoverDirKey(team.HierarchyDir), team.Name, "b", false, "{\"state\":\"" + state + "\"}");

        Assert.Equal(covered, host.RecoveryCovers(team, member));
    }

    [Fact]
    public void TheStallWatchLeavesAMemberWithAMatchedBareMarkToTheRecoveryEvenWhenItsProcessIsGone()
    {
        var host = NewHost(WithMark(Matched), out var team, (_, _) => false);
        var members = host.Members(team);

        Assert.True(host.RecoveryCovers(team, members.Single(m => m.Name == "a")));
        Assert.False(host.RecoveryCovers(team, members.Single(m => m.Name == "b")));
    }

    [Theory]
    [InlineData(null, true, false)]
    [InlineData("{\"on\":false,\"by\":\"user\"}", false, false)]
    [InlineData("{\"on\":false,\"by\":\"agent\"}", false, false)]
    [InlineData("{\"on\":true,\"by\":\"user\"}", true, false)]
    [InlineData("{\"on\":true,\"by\":\"agent\"}", true, false)]
    [InlineData("{\"on\":\"yes\"}", false, true)]
    [InlineData("not json", false, true)]
    [InlineData("[1,2]", false, true)]
    public void RecoveryIsOnWithNoSwitchRowOffWhenTheRowSaysOffAndOffWithAProblemWhenTheRowCannotBeUsed(string? row, bool on, bool problem)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        var host = new BareHost(graph, new StallHost(graph, _sandbox.AhHome, () => true, () => null));
        if (row is not null)
        {
            TestHomeDocs.Put(_sandbox.AhHome, Ah.Watch.WatchModule.RecoverySwitchKey, row);
        }

        Assert.Equal(on, host.RecoveryEnabled);
        Assert.Equal(problem, host.RecoveryProblem is not null);
        Assert.False(Directory.Exists(Path.Combine(_sandbox.AhHome, "recover")));
    }

    [Fact]
    public void TheDefaultTestOfTheProcessIsTheRegistrationsOwnIdentityTestAndAnUnknownPidFailsIt()
    {
        var host = NewHost(WithMark(Matched with { Pid = 999_999 }), out var team);
        var members = host.Members(team);

        Assert.DoesNotContain("Live (bare)", host.Judge(team, members.Single(m => m.Name == "a")).Verdict.Why, StringComparison.Ordinal);
    }
}
