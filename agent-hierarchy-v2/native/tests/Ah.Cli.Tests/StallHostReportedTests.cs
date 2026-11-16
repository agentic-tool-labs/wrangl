using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Platform;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>The session whose death the stall watch alarmed is kept on disk, so a new daemon process still knows it.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class StallHostReportedTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public bool Cancel { get; init; }

        public ProcessResult Run(ProcessRequest request)
        {
            Requests.Add(request);
            return Cancel && request.Environment?.ContainsKey("AH_ALARM") == true
                ? throw new OperationCanceledException()
                : new ProcessResult(3, null, false, false, default, default);
        }
    }

    [Theory]
    [InlineData(false, false, 3)]
    [InlineData(true, false, -1)]
    [InlineData(false, true, 3)]
    public async Task AStartedCommandAlwaysLeavesAJournalRowWhenCancelledOrWhenTheCallerThrows(bool cancel, bool callerThrows, int exit)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new RecordingRunner { Cancel = cancel }, new FakeDaemonClient());
        var path = graph.Config.UserConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"alarm\":{\"command\":[\"/bin/true\"]}}");
        var host = new StallHost(graph, _sandbox.AhHome, () => true, () => null);
        var got = new TaskCompletionSource<int>();

        Assert.True(host.StartAlarmCommand("the line", "tm", "stall", code =>
        {
            got.SetResult(code);
            if (callerThrows)
            {
                throw new IOException("disk full");
            }
        }));

        Assert.Equal(exit, await got.Task.WaitAsync(TimeSpan.FromSeconds(9), TestContext.Current.CancellationToken));
        if (callerThrows)
        {
            for (var i = 0; i < 90 && StallStore.JournalRows(_sandbox.AhHome).Count == 0; i++)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            var row = Ah.Core.Json.JsonText.Parse(StallStore.JournalRows(_sandbox.AhHome).Single()).AsObject();
            Assert.Equal("IOException", row["error"].AsString());
            Assert.Equal("tm", row["team"].AsString());
            Assert.Equal("stall", row["kind"].AsString());
            Assert.Equal(3, row["exit"].AsNumber());
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("{\"alarm\":{\"command\":\"/bin/sh -c x\"}}", false)]
    [InlineData("{\"alarm\":{\"command\":[\"/usr/bin/curl\",\"-d@-\"]}}", true)]
    public void TheAlarmCommandRunsOnlyWhenTheUserConfigHoldsAnArgvArray(string? config, bool runs)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var runner = new RecordingRunner();
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, runner, new FakeDaemonClient());
        if (config is not null)
        {
            var path = graph.Config.UserConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, config);
        }

        var exit = new StallHost(graph, _sandbox.AhHome, () => true, () => null).RunAlarmCommand("the line");

        Assert.Equal(runs ? 3 : null, exit);
        Assert.Equal(runs ? 1 : 0, runner.Requests.Count(r => r.Environment?.ContainsKey("AH_ALARM") == true));
        if (runs)
        {
            var request = Assert.Single(runner.Requests, r => r.Environment?.ContainsKey("AH_ALARM") == true);
            Assert.Equal(["/usr/bin/curl", "-d@-"], request.Argv);
            Assert.Equal("the line", request.Environment!["AH_ALARM"]);
        }
    }

    [Fact]
    public void TheAlarmCommandReallyRunsWithTheLineOnStdinAndInItsEnvironmentOnly()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["PATH"] = "/usr/bin:/bin", ["SECRET"] = "leak" };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, PlatformModule.CreateProcessRunner(), new FakeDaemonClient());
        var path = graph.Config.UserConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var seen = Path.Combine(_sandbox.Root, "seen");
        var script = $"{{ cat; printf '%s|%s\\n' \\\"$AH_ALARM\\\" \\\"$SECRET\\\"; }} > {seen}; exit 3";
        File.WriteAllText(path, $"{{\"alarm\":{{\"command\":[\"/bin/sh\",\"-c\",\"{script}\"]}}}}");

        var exit = new StallHost(graph, _sandbox.AhHome, () => true, () => null).RunAlarmCommand("the line");

        Assert.Equal(3, exit);
        Assert.Equal("the line\nthe line|\n", File.ReadAllText(seen));
    }

    [Fact]
    public void WithNoAlarmCommandTheLineGoesToAHerdrNotificationAndNotOnceOneIsSet()
    {
        var bin = Path.Combine(_sandbox.Root, "bin");
        Directory.CreateDirectory(bin);
        var seen = Path.Combine(_sandbox.Root, "seen");
        var fake = Path.Combine(bin, "herdr");
        File.WriteAllText(fake, $"#!/bin/sh\nprintf '%s|' \"$@\" > {seen}\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["PATH"] = $"{bin}:/usr/bin:/bin" };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, PlatformModule.CreateProcessRunner(), new FakeDaemonClient());
        var host = new StallHost(graph, _sandbox.AhHome, () => true, () => null);

        Assert.Equal("herdr", host.AlarmChannel());
        Assert.Equal(0, host.RunAlarmCommand("the line"));
        Assert.Equal("notification|show|ah alarm|--body|the line|", File.ReadAllText(seen));

        var path = graph.Config.UserConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"alarm\":{\"command\":[\"/bin/sh\",\"-c\",\"exit 0\"]}}");
        Assert.Equal("alarm.command", host.AlarmChannel());
    }

    [Fact]
    public async Task StartingTheAlarmCommandDoesNotWaitForItAndReportsItsExitLater()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["PATH"] = "/usr/bin:/bin" };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, PlatformModule.CreateProcessRunner(), new FakeDaemonClient());
        var path = graph.Config.UserConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"alarm\":{\"command\":[\"/bin/sh\",\"-c\",\"sleep 2; exit 4\"]}}");
        var host = new StallHost(graph, _sandbox.AhHome, () => true, () => null);
        var exited = new TaskCompletionSource<int>();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(host.StartAlarmCommand("the line", "tm", "stall", exited.SetResult));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"start waited {clock.Elapsed}");
        Assert.False(exited.Task.IsCompleted);

        Assert.Equal(4, await exited.Task.WaitAsync(TimeSpan.FromSeconds(9), TestContext.Current.CancellationToken));
    }

    /// <summary>Stores <paramref name="text"/> as the reported episodes, as another process would have written them.</summary>
    private void PlantReported(string text) =>
        Assert.True(Ah.StateDb.StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES (?1, ?2)", StallStore.Reported, text);
            return true;
        }).Ok);

    private StallHost NewHost()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        return new StallHost(graph, _sandbox.AhHome, () => true, () => null);
    }

    /// <summary>A verdict over a peers row by the shared rules, with these pids running.</summary>
    private sealed class RowJudge(params long[] running) : Ah.Hierarchy.IMemberLiveness
    {
        public Ah.Hierarchy.MemberRegistrationLiveness? ForMember(string memberName, string team) => null;

        public bool PidIdentityLive(int pid, string? recordedStart) => running.Contains(pid);

        public (Ah.Core.Time.LivenessVerdict Verdict, string Why) JudgePeerRow(Ah.Core.Time.PeerRowKind kind, long? pid, string? start, DateTimeOffset? rowAt, double? seenAgeSeconds, DateTimeOffset? lastLifeAt) =>
            Ah.TestKit.PeerRowJudge.Judge(kind, pid, start, rowAt, seenAgeSeconds, lastLifeAt, pid is { } p && running.Contains(p));
    }

    private Ah.Hierarchy.Verdict JudgedFromRow(string row, params long[] running)
    {
        var (host, team, _) = PaneHost("{\"team_id\":\"t-tm\",\"members\":[{\"role\":\"implementor\",\"name\":\"a\",\"route\":\"peer\"}]}");
        _graph!.Hierarchy.Core.Liveness = new RowJudge(running);
        _sandbox.ClearPeers(team.HierarchyDir);
        _sandbox.PlantPeers(team.HierarchyDir, row);
        return host.Judge(team, new StallMember("a", "implementor", null, false)).Verdict.Status;
    }

    private const string RowAt = "\"ts\":\"2026-10-21T08:00:00.000Z\",\"type\":\"peer\",\"name\":\"a\",\"role\":\"implementor\"";

    [Fact]
    public void AnUpRowWithALivePidJudgesLiveInTheStallWatch() =>
        Assert.Equal(Ah.Hierarchy.Verdict.Live, JudgedFromRow("{" + RowAt + ",\"status\":\"up\",\"pid\":77,\"pid_start\":\"s\"}", 77));

    [Fact]
    public void ADownRowWithPidAndStartJudgesDeadInTheStallWatch() =>
        Assert.Equal(Ah.Hierarchy.Verdict.Dead, JudgedFromRow("{" + RowAt + ",\"status\":\"down\",\"pid\":77,\"pid_start\":\"s\"}"));

    [Fact]
    public void NegativeADownRowWithNoPidJudgesUnknownInTheStallWatch() =>
        Assert.Equal(Ah.Hierarchy.Verdict.Unknown, JudgedFromRow("{" + RowAt + ",\"status\":\"down\"}"));

    [Fact]
    public void NegativeABriefedRowDecidesNothingInTheStallWatch() =>
        Assert.Equal(Ah.Hierarchy.Verdict.Unknown, JudgedFromRow("{" + RowAt + ",\"status\":\"briefed\",\"pid\":77,\"pid_start\":\"s\"}"));

    private ServiceGraph? _graph;

    private (StallHost Host, StallTeam Team, List<string> Reads) PaneHost(string teamJson)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        _graph = graph;
        var dir = Path.Combine(_sandbox.Home, "repo", ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(dir, "teams"));
        File.WriteAllText(Path.Combine(dir, "teams", "tm.json"), teamJson);
        var reads = new List<string>();
        var host = new StallHost(graph, _sandbox.AhHome, () => true, () => null, paneTail: (herdr, transport, pane) =>
        {
            reads.Add(herdr.Flavour + " " + transport + " " + pane);
            return "the end";
        });
        return (host, new StallTeam("tm", dir, _sandbox.Home, null, "orch"), reads);
    }

    [Fact]
    public void APaneIsReadOverTheTransportTheTeamRecords()
    {
        var (host, team, reads) = PaneHost("{\"transport\":\"herdr\",\"members\":[]}");

        Assert.Equal("the end", host.PaneTail(team, new StallMember("a", "implementor", "w1:p1", false)));
        Assert.Equal("tui herdr w1:p1", Assert.Single(reads));
    }

    [Fact]
    public void ACoreTeamsPaneIsReadThroughTheCoreFlavour()
    {
        var (host, team, reads) = PaneHost("{\"transport\":\"herdr\",\"herdr_flavour\":\"core\",\"members\":[]}");

        Assert.Equal("the end", host.PaneTail(team, new StallMember("a", "implementor", "t7", false)));
        Assert.Equal("core herdr t7", Assert.Single(reads));
    }

    [Theory]
    [InlineData("core", "hc")]
    [InlineData(null, "herdr")]
    public void AhdsPaneStateForATeamReadsThroughTheFlavourTheTeamStores(string? flavour, string binary)
    {
        var runner = new FakeRunner();
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, runner, new FakeDaemonClient());
        var record = Ah.Core.Json.JsonText.Parse(flavour is null ? "{\"transport\":\"herdr\"}" : $"{{\"transport\":\"herdr\",\"herdr_flavour\":\"{flavour}\"}}");

        _ = graph.Launch.PaneStateFor(record)!.Agents();

        var bins = runner.Calls.Select(c => Path.GetFileName(c[0])).ToList();
        Assert.True(bins.Contains(binary), string.Join(" | ", runner.Calls.Select(c => string.Join(' ', c))));
        Assert.DoesNotContain(binary == "hc" ? "herdr" : "hc", bins);
    }

    [Fact]
    public void AMemberWithNoPaneIsNotRead()
    {
        var (host, team, reads) = PaneHost("{\"transport\":\"herdr\",\"members\":[]}");

        Assert.Null(host.PaneTail(team, new StallMember("a", "implementor", null, false)));
        Assert.Empty(reads);
    }

    [Fact]
    public void ATeamRecordingNoTransportIsNotRead()
    {
        var (host, team, reads) = PaneHost("{\"members\":[]}");

        Assert.Null(host.PaneTail(team, new StallMember("a", "implementor", "w1:p1", false)));
        Assert.Empty(reads);
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555", true)]
    [InlineData("pane:w1:p1", false)]
    public void OnlyASessionIdMayBeNamedInAResumeLine(string session, bool expected) =>
        Assert.Equal(expected, NewHost().IsSessionId(session));

    [Theory]
    [InlineData("implementor", true)]
    [InlineData("x; curl evil", false)]
    [InlineData("", false)]
    public void OnlyARoleNameMayBeNamedInAResumeLine(string role, bool expected) =>
        Assert.Equal(expected, NewHost().IsRoleName(role));

    [Fact]
    public void AReportedSessionIsReadBackByANewHost()
    {
        NewHost().SetReportedSession("tm/impl", "s1");

        Assert.Equal("s1", NewHost().ReportedSession("tm/impl"));
    }

    [Fact]
    public void AMemberNeverReportedHasNoSession()
    {
        NewHost().SetReportedSession("tm/impl", "s1");

        Assert.Null(NewHost().ReportedSession("tm/other"));
    }

    [Fact]
    public void ForgettingASessionLeavesTheOthers()
    {
        var host = NewHost();
        host.SetReportedSession("tm/impl", "s1");
        host.SetReportedSession("tm/rev", "s2");

        host.SetReportedSession("tm/impl", null);

        Assert.Null(NewHost().ReportedSession("tm/impl"));
        Assert.Equal("s2", NewHost().ReportedSession("tm/rev"));
    }

    [Fact]
    public async Task WritersOnAtTheSameTimeLoseNoEntry()
    {
        var host = NewHost();
        var keys = Enumerable.Range(0, 24).Select(i => $"tm/m{i}").ToList();

        await Task.WhenAll(keys.Select(key => Task.Run(() => host.SetReportedSession(key, "s-" + key), TestContext.Current.CancellationToken)));

        var reader = NewHost();
        Assert.All(keys, key => Assert.Equal("s-" + key, reader.ReportedSession(key)));
    }

    [Fact]
    public void AnEntryUnseenForLongerThanTheWindowIsForgottenAndARecentOneKept()
    {
        var old = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow - TimeSpan.FromDays(30));
        var recent = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow - TimeSpan.FromDays(1));
        PlantReported(
            "{\"tm/gone\":{\"episode\":\"s0\",\"at\":\"" + old + "\"},\"tm/live\":{\"episode\":\"s1\",\"at\":\"" + recent + "\"}}");

        NewHost().ForgetStaleReported(TimeSpan.FromDays(7));

        Assert.Null(NewHost().ReportedSession("tm/gone"));
        Assert.Equal("s1", NewHost().ReportedSession("tm/live"));
    }

    [Fact]
    public void ARecordedSessionIsNotForgottenWhileItIsWithinTheWindowEvenIfNothingListsIt()
    {
        var host = NewHost();
        host.SetReportedSession("tm/impl", "s1");

        host.ForgetStaleReported(TimeSpan.FromDays(7));

        Assert.Equal("s1", NewHost().ReportedSession("tm/impl"));
    }

    [Fact]
    public void AnEntryThatRecordsNoTimeIsForgotten()
    {
        PlantReported("{\"tm/impl\":\"s1\"}");

        NewHost().ForgetStaleReported(TimeSpan.FromDays(7));

        Assert.Null(NewHost().ReportedSession("tm/impl"));
    }

    [Fact]
    public void AFileThatCannotBeParsedIsKeptAsBakBeforeTheNextChangeReplacesIt()
    {
        PlantReported("{not json");

        NewHost().SetReportedSession("tm/impl", "s1");

        Assert.Equal("{not json", StallStore.Read(_sandbox.AhHome, StallStore.ReportedUnreadable));
        Assert.Equal("s1", NewHost().ReportedSession("tm/impl"));
    }

    [Fact]
    public void AReadableFileLeavesNoBak()
    {
        var host = NewHost();
        host.SetReportedSession("tm/impl", "s1");

        NewHost().SetReportedSession("tm/other", "s2");

        Assert.Null(StallStore.Read(_sandbox.AhHome, StallStore.ReportedUnreadable));
    }

    [Fact]
    public void AHostReadsTheFileOnceAndWritesEachChangeThrough()
    {
        var host = NewHost();
        host.SetReportedSession("tm/impl", "s1");
        PlantReported("{}");

        Assert.Equal("s1", host.ReportedSession("tm/impl"));
        Assert.Null(NewHost().ReportedSession("tm/impl"));
    }

    [Fact]
    public void TheLastSeenTimeIsTheEvidenceAndTheRegistrationWriteIsOnlyItsFloor()
    {
        var written = new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
        var seen = written + TimeSpan.FromHours(2);

        Assert.Equal(seen, StallHost.Evidence(written, seen));
        Assert.Equal(written, StallHost.Evidence(written, written - TimeSpan.FromHours(1)));
        Assert.Equal(written, StallHost.Evidence(written, null));
        Assert.Equal(seen, StallHost.Evidence(null, seen));
        Assert.Null(StallHost.Evidence(null, null));

        var worked = seen + TimeSpan.FromHours(3);
        Assert.Equal(worked, StallHost.Evidence(written, seen, worked));
        Assert.Equal(seen, StallHost.Evidence(written, seen, written));
        Assert.Equal(worked, StallHost.Evidence(null, null, worked));
    }

    [Fact]
    public void AMemberThatIsNotLiveIsLastAliveWhenItsOwnActivityRecordSaysAndAnotherMembersRecordSaysNothing()
    {
        var (host, team, _) = PaneHost("{\"team_id\":\"t-tm\",\"members\":[{\"role\":\"implementor\",\"name\":\"a\",\"route\":\"peer\"},{\"role\":\"implementor\",\"name\":\"b\",\"route\":\"peer\"}]}");
        _sandbox.PlantActivity(team.HierarchyDir, "pane-a", "{\"activity\":\"working\",\"at\":\"2026-10-21T08:00:00.000Z\",\"working_at\":\"2026-10-21T11:30:00.000Z\",\"tool_at\":\"2026-10-21T10:00:00.000Z\"}");

        var judged = host.Judge(team, new StallMember("a", "implementor", null, false));
        var other = host.Judge(team, new StallMember("b", "implementor", null, false));

        Assert.NotEqual(Ah.Hierarchy.Verdict.Live, judged.Verdict.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 11, 30, 0, TimeSpan.Zero), judged.LastEvidence);
        Assert.Null(other.LastEvidence);
    }

    [Fact]
    public void AnUnreadableMarkerFileReadsAsNothingReported()
    {
        var host = NewHost();
        host.SetReportedSession("tm/impl", "s1");
        PlantReported("{not json");

        Assert.Null(NewHost().ReportedSession("tm/impl"));
    }
}
