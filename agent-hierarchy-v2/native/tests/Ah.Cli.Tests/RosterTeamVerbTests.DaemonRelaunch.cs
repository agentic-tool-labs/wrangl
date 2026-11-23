using Ah.Commands;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The daemon's in-process relaunch of one member: it needs no session above it and changes nothing else; a command line cannot claim it.</summary>
public sealed partial class RosterTeamVerbTests
{
    /// <summary>A pending member with a snapshot, and its checkpoint stored where the team's newest one is read from.</summary>
    private string ProjectHierarchyDir => Path.Combine(Project, ".claude", "ah2");

    private void StoredCheckpointOfAPendingMember()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Present);
        _panes.Harness = false;
        var folder = Path.Combine(ProjectHierarchyDir, "checkpoints", "proj");
        Directory.CreateDirectory(folder);
        File.Copy(record, Path.Combine(folder, "2026-10-17T10-00-00.000Z.json"));

        // a team's newest-record read takes no file (files-off-disk §4): the import pass takes it, as an upgrade would (an earlier compose's pass took the dir with no record yet)
        _ = Ah.StateDb.StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("DELETE FROM meta WHERE key = ?1", "checkpoints.taken:" + Ah.Core.Text.PathGuard.RealPath(ProjectHierarchyDir, strict: false));
            return 0;
        });
        _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(_sandbox.AhHome, ProjectHierarchyDir, force: true);
        RecordExpectedRoot(FamilyWorkTree("rc2"));
    }

    private static string Spelled(string path) => path.Replace("/private/tmp/", "/tmp/", StringComparison.Ordinal);

    private const string RelaunchSession = "0b4e7c2a-1d3f-4a5b-8c6d-7e8f9a0b1c2d";

    private string? DaemonRelaunch(string member = Impl, string sessionId = RelaunchSession) =>
        CommandsModule.CreateMemberRelaunch(ComposeServices()).Relaunch(ProjectHierarchyDir, Project, "proj", member, sessionId);

    /// <summary>A herdr team whose implementor sits in pane pA of tab tA, as herdr restores it: no checkpoint record exists.</summary>
    private HerdrFake RestoredHerdrMember(PaneKnowledge pane = PaneKnowledge.Present, bool daemonEnvironment = false, string? recordedTransport = "herdr")
    {
        _ = ResumeHome();
        var herdr = OnHerdr(string.Empty);
        if (daemonEnvironment)
        {
            foreach (var name in new[] { "HERDR_ENV", "HERDR_PANE_ID", "HERDR_TAB_ID" })
            {
                _ = _extraEnv.Remove(name);
            }
        }

        _sandbox.PlantTeam(
            ProjectTeamFile,
            LiveTeamJson(recordedTransport ?? "none", "{\"role\":\"implementor\",\"name\":\"" + Impl + "\",\"route\":\"peer\",\"transport_id\":\"pA\",\"tab_id\":\"tA\",\"model\":\"sonnet\",\"effort\":\"low\",\"expected_root\":\"" + FamilyWorkTree("rc2") + "\"}"));
        if (recordedTransport is null)
        {
            _sandbox.PlantTeam(ProjectTeamFile, _sandbox.TeamText(ProjectTeamFile).Replace("\"transport\":\"none\",", string.Empty, StringComparison.Ordinal));
        }

        herdr.Tabs.Add(("tA", "proj: implementor"));
        herdr.Panes.Add(("pA", "tA"));
        herdr.Agents["pA"] = Impl;
        _registrations = [];
        _panes.Default = pane;
        _panes.Harness = false;
        _runner.Calls.Clear();
        return herdr;
    }

    private string RecordedPane() =>
        JsonText.Parse(_sandbox.TeamText(ProjectTeamFile)).AsObject().Get("members")!.Value.AsArray()
            .Single(r => r.AsObject().Get("name")!.Value.AsString() == Impl).AsObject().Get("transport_id")!.Value.AsString();

    private List<string> LaunchLines() =>
        _runner.Calls.Where(argv => argv.Length > 0 && argv[0] == "/bin/sh").Select(argv => string.Join(' ', argv)).ToList();

    [Fact]
    public void TheDaemonRelaunchOfARestoredMemberWithNoCheckpointResumesTheGivenSessionInItsRecordedPaneWithTheRoleFlags()
    {
        var herdr = RestoredHerdrMember();
        Assert.False(ComposeServices().Roster.ReadTeamRecord(ProjectHierarchyDir, "proj").IsNull, "fixture team unreadable");

        var failure = DaemonRelaunch();

        Assert.True(failure is null, failure);
        var launch = Assert.Single(LaunchLines());
        Assert.Contains("--pane pA", launch, StringComparison.Ordinal);
        Assert.Contains("--resume " + RelaunchSession, launch, StringComparison.Ordinal);
        Assert.Contains("--agent wrangl:implementor", launch, StringComparison.Ordinal);
        Assert.Contains("--model", launch, StringComparison.Ordinal);
        Assert.Contains("--effort", launch, StringComparison.Ordinal);
        Assert.Contains("--settings", launch, StringComparison.Ordinal);
        Assert.Contains("agent start " + Impl, launch, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 2 && argv[0] == "herdr" && argv[1] == "pane" && argv[2] == "split");
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 2 && argv[0] == "herdr" && argv[1] == "tab" && argv[2] == "create");
        Assert.Equal("pA", RecordedPane());
        Assert.DoesNotContain(herdr.Panes, p => p.Pane != "pA" && p.Pane != "pOrch");
    }

    [Fact]
    public void TheDaemonDoesNotRelaunchAHeadlessMemberAndLaunchesNothing()
    {
        _ = RestoredHerdrMember();
        _sandbox.PlantTeam(ProjectTeamFile, _sandbox.TeamText(ProjectTeamFile).Replace("\"transport_id\":\"pA\"", "\"transport_id\":\"headless:4242\"", StringComparison.Ordinal));

        var failure = DaemonRelaunch();

        Assert.NotNull(failure);
        Assert.StartsWith(Ah.Watch.WatchModule.RecoveryRefusalPrefix, failure, StringComparison.Ordinal);
        Assert.Contains("headless", failure, StringComparison.Ordinal);
        Assert.Empty(LaunchLines());
    }

    [Fact]
    public void TheDaemonStillRelaunchesAMemberWhoseTransportIdMerelyStartsLikeHeadless()
    {
        _ = RestoredHerdrMember();
        _sandbox.PlantTeam(ProjectTeamFile, _sandbox.TeamText(ProjectTeamFile).Replace("\"transport_id\":\"pA\"", "\"transport_id\":\"headlessness\"", StringComparison.Ordinal));

        var failure = DaemonRelaunch();

        Assert.DoesNotContain("runs headless", failure ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDaemonRelaunchWhoseStartIsRefusedForTheNameCarriesTheRefusalPrefixAndNeverClosesTheRecordedPane()
    {
        var herdr = RestoredHerdrMember();
        herdr.NameTaken = true;

        var failure = DaemonRelaunch();

        Assert.NotNull(failure);
        Assert.StartsWith(Ah.Watch.WatchModule.RecoveryRefusalPrefix, failure, StringComparison.Ordinal);
        Assert.Contains("pA", herdr.Panes.Select(p => p.Pane));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 2 && argv[0] == "herdr" && argv[1] == "pane" && argv[2] == "close");
    }

    [Fact]
    public void TheDaemonRelaunchOfATeamHeldWhileTheLaunchRunsReturnsTheHoldRefusalUnprefixed()
    {
        _ = RestoredHerdrMember();
        var holdPath = ComposeServices().Roster.HoldPath(ProjectHierarchyDir, "proj");
        PutHold(holdPath, "{\"kind\":\"quit\",\"phase\":\"held\",\"since\":\"2026-10-24T00:00:00Z\"}");

        var failure = DaemonRelaunch();

        Assert.NotNull(failure);
        Assert.StartsWith("team_held", failure, StringComparison.Ordinal);
        Assert.False(RanAnAgentStart());
    }

    [Fact]
    public void TheDaemonRelaunchThatSucceedsCarriesNoRefusalPrefix()
    {
        _ = RestoredHerdrMember();

        Assert.Null(DaemonRelaunch());
    }

    [Fact]
    public void TheDaemonRelaunchOfAMemberWhoseRestoredPaneIsGoneLaunchesThroughTheNormalAnchorIntoANewPane()
    {
        _ = RestoredHerdrMember(PaneKnowledge.Absent);

        var failure = DaemonRelaunch();

        Assert.Null(failure);
        var launch = Assert.Single(LaunchLines());
        Assert.DoesNotContain("--pane pA ", launch + " ", StringComparison.Ordinal);
        Assert.NotEqual("pA", RecordedPane());
    }

    [Fact]
    public void TheDaemonRelaunchOfAMemberWhoseRestoredPaneCannotBeReadLaunchesNothingAndCarriesTheReadersReason()
    {
        _ = RestoredHerdrMember(PaneKnowledge.Unknown);
        _panes.Reason = "herdr exited 1: socket not found";

        var failure = DaemonRelaunch();

        Assert.NotNull(failure);
        Assert.StartsWith(Ah.Watch.WatchModule.RecoveryPaneUnknownPrefix, failure, StringComparison.Ordinal);
        Assert.Contains("herdr exited 1: socket not found", failure, StringComparison.Ordinal);
        Assert.Empty(LaunchLines());
        Assert.Equal("pA", RecordedPane());
    }

    [Fact]
    public void TheDaemonRelaunchThatWouldStartFreshAlthoughASessionIsGivenLaunchesNothing()
    {
        _ = RestoredHerdrMember();

        var failure = DaemonRelaunch(sessionId: "short");

        Assert.NotNull(failure);
        Assert.Contains("would start fresh although session short is known", failure, StringComparison.Ordinal);
        Assert.Empty(LaunchLines());
    }

    private string ProjectTeamFile => Path.Combine(ProjectHierarchyDir, "teams", "proj.json");

    private bool RanAnAgentStart() => _runner.Calls.Any(argv => string.Join(' ', argv).Contains("agent start", StringComparison.Ordinal));

    [Fact]
    public void TheDaemonRelaunchOfAHerdrTeamUsesHerdrWhenTheDaemonsOwnEnvironmentHasNoHerdr()
    {
        _ = RestoredHerdrMember(daemonEnvironment: true);

        var failure = DaemonRelaunch();

        Assert.True(failure is null, failure);
        var launch = Assert.Single(LaunchLines());
        Assert.Contains("agent start " + Impl, launch, StringComparison.Ordinal);
        Assert.Contains("--kind claude --pane pA", launch, StringComparison.Ordinal);
        Assert.Contains("--resume " + RelaunchSession, launch, StringComparison.Ordinal);
        Assert.Contains("--agent wrangl:implementor", launch, StringComparison.Ordinal);
        Assert.Contains("--settings", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("--bg", launch, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 2 && argv[0] == "herdr" && argv[1] == "pane" && argv[2] == "split");
        Assert.Equal("pA", RecordedPane());
    }

    [Fact]
    public void TheDaemonRelaunchOfAHerdrTeamStaysOnHerdrWhenTheDaemonsEnvironmentLooksLikeTmux()
    {
        _ = RestoredHerdrMember(daemonEnvironment: true);
        _extraEnv["TMUX"] = "/tmp/tmux-1/default,1,0";
        _extraEnv["TMUX_PANE"] = "%0";

        var failure = DaemonRelaunch();

        Assert.True(failure is null, failure);
        Assert.True(RanAnAgentStart());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 1 && argv[0] == "tmux" && argv[1] is "send-keys" or "new-window" or "split-window");
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData(null)]
    [InlineData("zellij")]
    public void TheDaemonRelaunchOfATeamThatIsNotOnHerdrOrTmuxLaunchesNothingAndAsks(string? recorded)
    {
        _ = RestoredHerdrMember(daemonEnvironment: true, recordedTransport: recorded);

        var failure = DaemonRelaunch();

        Assert.NotNull(failure);
        Assert.StartsWith(Ah.Watch.WatchModule.RecoveryPaneUnknownPrefix, failure, StringComparison.Ordinal);
        Assert.Contains($"team proj records transport {recorded ?? "none"}; ahd relaunches only into herdr or tmux panes; resume it with ah roster team resume", failure, StringComparison.Ordinal);
        Assert.Empty(LaunchLines());
        Assert.False(RanAnAgentStart());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 0 && argv.Any(a => a.Contains("claude", StringComparison.Ordinal)));
        Assert.Equal("pA", RecordedPane());
    }

    [Fact]
    public void TheDaemonRelaunchOfATmuxTeamDoesNotFollowTheDaemonsHerdrEnvironment()
    {
        _ = RestoredHerdrMember(recordedTransport: "tmux");
        var answer = _runner.Output;
        _runner.Output = argv => argv.Length > 1 && argv[0] == "tmux" && argv.Contains("new-window") ? "%7" : answer!(argv);

        var failure = DaemonRelaunch();

        Assert.True(failure is null, failure);
        Assert.False(RanAnAgentStart());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 2 && argv[0] == "herdr" && argv[1] == "pane" && argv[2] == "split");
        Assert.Contains(_runner.Calls, argv => argv.Length > 0 && argv[0] == "tmux" && argv.Any(a => a is "new-window" or "send-keys" or "split-window"));
    }

    [Fact]
    public void TheDaemonRelaunchesAMemberWithNoSessionAboveItAndAsksForNoProof()
    {
        StoredCheckpointOfAPendingMember();
        _nearest = new NearestSession(NearestKind.None, null, null, 0);

        var failure = DaemonRelaunch();

        Assert.Null(failure);
        Assert.Equal(0, _nearestAsked);
        Assert.Contains(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void TheDaemonRelaunchLeavesTheTeamsOrchestratorOwnershipAndEveryOtherFieldAsTheyWere()
    {
        StoredCheckpointOfAPendingMember();
        var before = JsonText.Parse(_sandbox.TeamText(ProjectTeamFile)).AsObject();

        var failure = DaemonRelaunch();

        Assert.Null(failure);
        var after = JsonText.Parse(_sandbox.TeamText(ProjectTeamFile)).AsObject();
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var key in before.Keys.Where(k => k != "members"))
        {
            Assert.Equal(JsonText.Stringify(before.Get(key)!.Value, 0), JsonText.Stringify(after.Get(key)!.Value, 0));
        }

        var beforeRows = before.Get("members")!.Value.AsArray();
        var afterRows = after.Get("members")!.Value.AsArray();
        Assert.Equal(beforeRows.Count, afterRows.Count);
        for (var i = 0; i < beforeRows.Count; i++)
        {
            var was = beforeRows[i].AsObject();
            var now = afterRows[i].AsObject();
            var changed = was.Keys.Union(now.Keys).Where(k => Spelled(JsonText.Stringify(was.Get(k) ?? JsonValue.Null, 0)) != Spelled(JsonText.Stringify(now.Get(k) ?? JsonValue.Null, 0))).ToList();
            if (was.Get("name")!.Value.AsString() == Impl)
            {
                // a relaunch re-stamps where, in which pane and when the member now runs; nothing else of its row changes
                Assert.All(changed, k => Assert.Contains(k, RelaunchedRowMayChange));
            }
            else
            {
                Assert.Empty(changed);
            }
        }
    }

    // ah2-570: a row with no claude_config_dir gets the value its relaunch resolved
    private static readonly string[] RelaunchedRowMayChange = ["tmp", "transport_id", "spawned_at", "claude_config_dir", "claude_securestorage_config_dir"];

    private void RecordExpectedRoot(string root, string? pane = null)
    {
        var team = JsonText.Parse(_sandbox.TeamText(ProjectTeamFile)).AsObject().Clone();
        var rows = new JsonArray();
        foreach (var row in team.Get("members")!.Value.AsArray())
        {
            var copy = row.AsObject().Clone();
            if (copy.Get("name")!.Value.AsString() == Impl)
            {
                copy["expected_root"] = root;
                if (pane is not null)
                {
                    copy["transport_id"] = pane;
                }
            }

            rows.Add(JsonValue.FromObject(copy));
        }

        team["members"] = JsonValue.FromArray(rows);
        _sandbox.PlantTeam(ProjectTeamFile, JsonText.Stringify(JsonValue.FromObject(team), 2));
    }

    /// <summary>Records on the member's live row the stream it belongs to; the daemon relaunch reads it from the row, not from a stored snapshot.</summary>
    private void PutMemberInStream(string stream)
    {
        var team = JsonText.Parse(_sandbox.TeamText(ProjectTeamFile)).AsObject().Clone();
        var rows = new JsonArray();
        foreach (var row in team.Get("members")!.Value.AsArray())
        {
            var copy = row.AsObject().Clone();
            if (copy.Get("name")!.Value.AsString() == Impl)
            {
                copy["stream"] = stream;
            }

            rows.Add(JsonValue.FromObject(copy));
        }

        team["members"] = JsonValue.FromArray(rows);
        _sandbox.PlantTeam(ProjectTeamFile, JsonText.Stringify(JsonValue.FromObject(team), 2));
    }

    private string ExpectedRootInTeamFile() =>
        JsonText.Parse(_sandbox.TeamText(ProjectTeamFile)).AsObject().Get("members")!.Value.AsArray()
            .Single(r => r.AsObject().Get("name")!.Value.AsString() == Impl).AsObject().Get("expected_root")!.Value.AsString();

    [Fact]
    public void TheDaemonRelaunchKeepsTheRecordedExpectedRootOfAMemberOnAWorkTreeAndLaunchesThere()
    {
        StoredCheckpointOfAPendingMember();
        var tree = FamilyWorkTree("rc2");
        RecordExpectedRoot(tree);

        var failure = DaemonRelaunch();

        Assert.Null(failure);
        Assert.Equal(Spelled(tree), Spelled(ExpectedRootInTeamFile()));
        Assert.NotEqual(Spelled(Project), Spelled(ExpectedRootInTeamFile()));
    }

    /// <summary>A pending member of the open stream "s1" whose work tree is <paramref name="streamTree"/>, its checkpoint stored for the daemon.</summary>
    private void StoredCheckpointOfAStreamMember(string streamTree, string streamState = "open")
    {
        var home = ResumeHome();
        var snapshot = PatchSnapshot(TakeSnapshot(home, streamTree, "log"), member => member["stream"] = JsonValue.FromString("s1"));
        var teamPath = Path.Combine(home, "teams", "proj.json");
        var team = JsonText.Parse(_sandbox.TeamText(teamPath)).AsObject().Clone();
        team["streams"] = JsonText.Parse(TeamWithStream(streamTree).Replace("\"state\":\"open\"", $"\"state\":\"{streamState}\"", StringComparison.Ordinal)).AsObject().Get("streams")!.Value;
        _sandbox.PlantTeam(teamPath, JsonText.Stringify(JsonValue.FromObject(team), 2));
        RecordExpectedRoot(streamTree);
        PutMemberInStream("s1");
        _registrations = [];
        _panes.Default = PaneKnowledge.Present;
        var record = WriteRecord(snapshot);
        _panes.Harness = false;
        var folder = Path.Combine(ProjectHierarchyDir, "checkpoints", "proj");
        Directory.CreateDirectory(folder);
        File.Copy(record, Path.Combine(folder, "2026-10-17T10-00-00.000Z.json"));
        _runner.Calls.Clear();
    }

    [Fact]
    public void TheDaemonRelaunchOfAStreamMemberWhoseRecordedRootIsTheStreamTreeLaunchesThereAndKeepsIt()
    {
        var tree = FamilyWorkTree("s1");
        StoredCheckpointOfAStreamMember(tree);
        RecordExpectedRoot(tree, "%1");

        var failure = DaemonRelaunch();

        Assert.Null(failure);
        Assert.Contains(_runner.Calls, argv => argv.Contains("new-window"));
        Assert.Equal(Spelled(tree), Spelled(ExpectedRootInTeamFile()));
    }

    [Fact]
    public void TheDaemonRelaunchOfAStreamMemberWhoseRecordedRootIsNotTheStreamTreeIsRefusedAndLaunchesNothing()
    {
        var tree = FamilyWorkTree("s1");
        var other = FamilyWorkTree("w3");
        StoredCheckpointOfAStreamMember(tree);
        RecordExpectedRoot(other, "%1");

        var failure = DaemonRelaunch();

        Assert.NotNull(failure);
        Assert.Contains("expected-root-differs", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
        Assert.Equal(Spelled(other), Spelled(ExpectedRootInTeamFile()));
    }

    [Fact]
    public void TheDaemonRelaunchOfAMemberWhoseStreamIsClosedAndWhoseRecordedRootIsNotItsRecordedTreeIsRefusedAndLaunchesNothing()
    {
        var tree = FamilyWorkTree("s1");
        var other = FamilyWorkTree("w3");
        StoredCheckpointOfAStreamMember(tree, "done");
        RecordExpectedRoot(other);

        var failure = DaemonRelaunch();

        Assert.NotNull(failure);
        Assert.Contains("is done", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
        Assert.Equal(Spelled(other), Spelled(ExpectedRootInTeamFile()));
    }

    [Fact]
    public void TheDaemonRelaunchOfAMemberWhoseRecordedRootIsGoneIsRefusedAndLaunchesNothing()
    {
        StoredCheckpointOfAPendingMember();
        var gone = Path.Combine(Project, "gone-tree");
        RecordExpectedRoot(gone);
        var before = _runner.Calls.Count;

        var failure = DaemonRelaunch();

        Assert.NotNull(failure);
        Assert.Contains("does not exist", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls.Skip(before), argv => argv.Contains("new-window"));
        Assert.Equal(gone, ExpectedRootInTeamFile());
    }

    [Fact]
    public void TheDaemonRelaunchOfAMemberNotInTheCheckpointIsRefusedAndLaunchesNothing()
    {
        StoredCheckpointOfAPendingMember();

        var failure = DaemonRelaunch("proj-nobody");

        Assert.Contains("not in the record", failure, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    private string? DaemonPreflight(string member = Impl, string sessionId = RelaunchSession) =>
        CommandsModule.CreateMemberRelaunch(ComposeServices()).Preflight(ProjectHierarchyDir, Project, "proj", member, sessionId);

    private bool StartedOrChangedAnything() =>
        _runner.Calls.Any(argv => argv.Length > 0 && (argv[0] == "/bin/sh"
            || (argv.Length > 2 && argv[0] == "herdr" && argv[1] is "pane" or "tab" or "agent" && argv[2] is "split" or "create" or "close" or "run" or "send-keys" or "start")
            || argv.Contains("send-keys") || argv.Contains("new-window")));

    private void MemberIsLive() =>
        _registrations = new() { [Impl + "@proj"] = new Ah.Hierarchy.MemberRegistrationLiveness(RelaunchSession, true, "registration live", DateTimeOffset.UtcNow.AddSeconds(-90)) };

    [Fact]
    public void TheRealRelaunchOfAMemberThatIsStillLiveLaunchesNothingAndAnswersWhyWhilePreflightIsTheOnlyPathThatLooksPastLiveness()
    {
        _ = RestoredHerdrMember();
        MemberIsLive();
        _runner.Calls.Clear();

        var real = DaemonRelaunch();
        var preflight = DaemonPreflight();

        Assert.NotNull(real);
        Assert.Null(preflight);
        Assert.Empty(LaunchLines());
    }

    [Fact]
    public void ThePreflightOfAStillLiveImplementorWhoseRowRecordsTheMainCheckoutReturnsTheMainCheckoutRefusalAndStartsAndWritesNothing()
    {
        _ = RestoredHerdrMember();
        RecordExpectedRoot(Project);
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        TestGrants.Plant(_sandbox.AhHome, _sandbox.Home, ProjectHierarchyDir, "proj", """{"version":1,"team":"proj","mode":"enforce","audit_hash":null,"members":{}}""");
        MemberIsLive();
        var teamBefore = _sandbox.TeamText(ProjectTeamFile);
        var grantsBefore = GrantsText(ProjectHierarchyDir);
        _runner.Calls.Clear();

        var failure = DaemonPreflight();

        Assert.NotNull(failure);
        Assert.Contains("main checkout", failure, StringComparison.Ordinal);
        Assert.False(StartedOrChangedAnything(), string.Join(Environment.NewLine, _runner.Calls.Select(c => string.Join(' ', c))));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Length > 0 && argv[0] == "herdr");
        Assert.Equal(teamBefore, _sandbox.TeamText(ProjectTeamFile));
        Assert.Equal(grantsBefore, GrantsText(ProjectHierarchyDir));
    }

    [Fact]
    public void ThePreflightOfAStillLiveMemberWhoseRecordedRootIsGoneReturnsThatRefusalNotAnAliveAnswerAndStartsAndWritesNothing()
    {
        _ = RestoredHerdrMember();
        RecordExpectedRoot(Path.Combine(Project, "gone-tree"));
        MemberIsLive();
        var before = _sandbox.TeamText(ProjectTeamFile);
        _runner.Calls.Clear();

        var failure = DaemonPreflight();

        Assert.NotNull(failure);
        Assert.Contains("does not exist", failure, StringComparison.Ordinal);
        Assert.False(StartedOrChangedAnything(), string.Join(Environment.NewLine, _runner.Calls.Select(c => string.Join(' ', c))));
        Assert.Equal(before, _sandbox.TeamText(ProjectTeamFile));
    }

    [Fact]
    public void ThePreflightOfAMemberWhoseLaunchWouldGoAheadReturnsNullAndStartsAndWritesNothing()
    {
        _ = RestoredHerdrMember();
        var before = _sandbox.TeamText(ProjectTeamFile);
        _runner.Calls.Clear();

        var failure = DaemonPreflight();

        Assert.True(failure is null, failure);
        Assert.False(StartedOrChangedAnything());
        Assert.Equal(before, _sandbox.TeamText(ProjectTeamFile));
    }

    [Fact]
    public void ThePreflightOfAMemberWhosePaneCannotBeReadReturnsTheReadersReasonAndStartsNothing()
    {
        _ = RestoredHerdrMember(PaneKnowledge.Unknown);
        _panes.Reason = "herdr exited 1: socket not found";

        var failure = DaemonPreflight();

        Assert.NotNull(failure);
        Assert.StartsWith(Ah.Watch.WatchModule.RecoveryPaneUnknownPrefix, failure, StringComparison.Ordinal);
        Assert.False(StartedOrChangedAnything());
    }

    [Fact]
    public void ThePreflightOfAStreamMemberWhoseRecordedTreeIsNotTheStreamTreeIsRefusedAndWritesNothing()
    {
        var tree = FamilyWorkTree("s1");
        StoredCheckpointOfAStreamMember(tree);
        RecordExpectedRoot(FamilyWorkTree("w3"), "%1");
        var before = _sandbox.TeamText(ProjectTeamFile);

        var failure = DaemonPreflight();

        Assert.NotNull(failure);
        Assert.Contains("expected-root-differs", failure, StringComparison.Ordinal);
        Assert.False(StartedOrChangedAnything());
        Assert.Equal(before, _sandbox.TeamText(ProjectTeamFile));
    }

    [Fact]
    public void ThePreflightOfAStreamMemberWhoseStreamIsMissingFromTheTeamFileWritesNothingToIt()
    {
        var tree = FamilyWorkTree("s1");
        StoredCheckpointOfAStreamMember(tree);
        RecordExpectedRoot(tree, "%1");
        var team = JsonText.Parse(_sandbox.TeamText(ProjectTeamFile)).AsObject().Clone();
        _ = team.Remove("streams");
        _sandbox.PlantTeam(ProjectTeamFile, JsonText.Stringify(JsonValue.FromObject(team), 2));
        var before = _sandbox.TeamText(ProjectTeamFile);

        _ = DaemonPreflight();

        Assert.Equal(before, _sandbox.TeamText(ProjectTeamFile));
        Assert.False(StartedOrChangedAnything());
    }

    [Fact]
    public void ACommandLineCannotClaimTheDaemonPrincipalAndIsStillRefusedWithNoSessionAboveIt()
    {
        StoredCheckpointOfAPendingMember();
        _nearest = new NearestSession(NearestKind.None, null, null, 0);
        _callers ??= new RecordingCallers { StartText = SelfStart };

        var (code, _, text) = Roster("resume", "--checkpoint", "--team", "proj", "--relaunch", Impl, "--yes", "--cwd", Project);
        var (flagCode, _, flagText) = Roster("resume", "--checkpoint", "--team", "proj", "--relaunch", Impl, "--yes", "--daemon", "--cwd", Project);

        Assert.NotEqual(0, code);
        Assert.Contains("could not prove which Claude session", text, StringComparison.Ordinal);
        Assert.NotEqual(0, flagCode);
        Assert.Contains("unrecognized flag --daemon", flagText, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }
}
