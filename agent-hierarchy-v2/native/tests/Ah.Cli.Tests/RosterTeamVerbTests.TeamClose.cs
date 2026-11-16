using Ah.Commands;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>Close and reopen a team (ah-ui-focus §6): a marker, nothing deleted, and live sessions end only when every one was confirmed.</summary>
public sealed partial class RosterTeamVerbTests
{
    private JsonObject TeamClose(string team, params (string Key, JsonValue Value)[] more)
    {
        var input = new JsonObject { ["dir"] = Hierarchy, ["team"] = team, ["by"] = "ui" };
        foreach (var (key, value) in more)
        {
            input[key] = value;
        }

        return CommandsModule.TeamClose(ComposeServices(), input);
    }

    private JsonValue TeamFile(string name) => JsonText.Parse(_sandbox.TeamText(TeamPath(name)));

    private static JsonArray Names(params string[] names)
    {
        var array = new JsonArray();
        array.AddRange(names.Select(static n => (JsonValue)n));
        return array;
    }

    private void DeadOrchestratorTeam(string members)
    {
        ObserverSeesAParent();
        WriteTeam("proj", LiveTeamJson("tmux", members, pid: "99999999", pidStart: GoneStart));
    }

    [Fact]
    public void ClosingATeamNobodyRunsWritesTheMarkerAloneAndReopenRemovesIt()
    {
        DeadOrchestratorTeam(MemberAt(Closer, "%1", TreeDir("t-closer")));
        AppendGoneRow(Closer, "proj");

        var closed = TeamClose("proj");

        Assert.True(closed.Get("closed")!.Value.AsBool());
        Assert.Empty(closed.Get("ended")!.Value.AsArray());
        var record = TeamFile("proj");
        Assert.Equal("ui", record.Member("closed")!.Value.Member("by")!.Value.AsString());
        Assert.Equal(closed.Get("at")!.Value.AsString(), record.Member("closed")!.Value.Member("at")!.Value.AsString());
        Assert.Equal(Closer, Assert.Single(record.Member("members")!.Value.AsArray()).Member("name")!.Value.AsString());

        Assert.True(TeamClose("proj", ("reopen", true)).Get("reopened")!.Value.AsBool());
        record = TeamFile("proj");
        Assert.Null(record.Member("closed"));
        Assert.Single(record.Member("members")!.Value.AsArray());
    }

    [Fact]
    public void APreviewWritesNothingAndNamesWhoIsRunningAndWhoIsUnknownAndHoldsNoDeadDirtyMember()
    {
        var dirty = TreeDir("t-dirty");
        DeadOrchestratorTeam(string.Join(",", MemberAt(LiveAsked, "%2", TreeDir("t-live")), MemberAt(DeadDirty, "%3", dirty), MemberAt(NoRow, "%4", TreeDir("t-norow"))));
        PeerUp(LiveAsked, "%2", session: "sess-live");
        AppendGoneRow(DeadDirty, "proj");
        _sandbox.PlantPeers(
            Hierarchy,
            $"{{\"type\":\"peer\",\"status\":\"seen\",\"name\":\"{NoRow}\",\"role\":\"implementor\",\"session_id\":\"s-norow\",\"team\":\"proj\",\"ts\":\"{DateTimeOffset.UtcNow.AddHours(-2):yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\"}}\n");
        DirtyOnly(dirty);

        var preview = TeamClose("proj", ("preview", true));

        Assert.Equal(LiveAsked, Assert.Single(preview.Get("live")!.Value.AsArray()).Member("member")!.Value.AsString());
        Assert.Equal(NoRow, Assert.Single(preview.Get("unknown")!.Value.AsArray()).Member("member")!.Value.AsString());
        Assert.Empty(preview.Get("held")!.Value.AsArray());
        Assert.Null(TeamFile("proj").Member("closed"));
    }

    [Fact]
    public void ARunningMemberTheCallerDidNotConfirmStopsTheCloseWithTheFreshTableAndNoMarker()
    {
        DeadOrchestratorTeam(string.Join(",", MemberAt(LiveAsked, "%2", TreeDir("t-live")), MemberAt(Closer, "%1", TreeDir("t-closer"))));
        PeerUp(LiveAsked, "%2", session: "sess-live");
        AppendGoneRow(Closer, "proj");

        // the nearest input to a good confirmation: a list that names someone, just not the one running
        var result = TeamClose("proj", ("confirm_live", Names(Closer)));

        Assert.Equal("live-changed", result.Get("refused")!.Value.Member("reason")!.Value.AsString());
        Assert.Equal(LiveAsked, Assert.Single(result.Get("preview")!.Value.Member("live")!.Value.AsArray()).Member("member")!.Value.AsString());
        Assert.Null(TeamFile("proj").Member("closed"));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("kill-pane"));
    }

    [Fact]
    public void AConfirmedRunningMemberIsClosedTheMarkerIsWrittenAndItsRowStays()
    {
        DeadOrchestratorTeam(string.Join(",", MemberAt(LiveAsked, "%2", TreeDir("t-live")), MemberAt(Closer, "%1", TreeDir("t-closer"))));
        PeerUp(LiveAsked, "%2", session: "sess-live");
        AppendGoneRow(Closer, "proj");

        var result = TeamClose("proj", ("confirm_live", Names(LiveAsked)));

        Assert.True(result.Get("closed")!.Value.AsBool());
        var kills = _runner.Calls.Where(argv => argv.Contains("kill-pane")).ToList();
        Assert.Contains(Assert.Single(kills), arg => arg == "%2");
        var record = TeamFile("proj");
        Assert.NotNull(record.Member("closed"));
        Assert.Equal(2, record.Member("members")!.Value.AsArray().Count);
    }

    [Fact]
    public void ALiveOrchestratorIsNeverEndedAndIsNamedAsLeftRunning()
    {
        WriteTeam("proj", LiveTeamJson("tmux", MemberAt(Closer, "%1", TreeDir("t-closer"))));
        AppendGoneRow(Closer, "proj");

        var result = TeamClose("proj");

        Assert.True(result.Get("closed")!.Value.AsBool());
        Assert.Equal("orchestrator", Assert.Single(result.Get("left_running")!.Value.AsArray()).AsString());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("kill-pane"));
    }

    [Fact]
    public void TheMarkerSurvivesAMemberAddAndAReopenStillFindsTheMember()
    {
        _callers = new RecordingCallers { Started = Started };
        SessionPidsRow("sess-1", Environment.ProcessId);
        OwnedTeamFile(Environment.ProcessId, "sess-1");
        Assert.True(TeamClose("proj").Get("closed")!.Value.AsBool());

        var (code, _, stderr) = AdHoc();

        Assert.True(code == 0, stderr);
        var record = TeamFile("proj");
        Assert.NotNull(record.Member("closed"));
        Assert.Single(record.Member("members")!.Value.AsArray());

        _ = TeamClose("proj", ("reopen", true));
        record = TeamFile("proj");
        Assert.Null(record.Member("closed"));
        Assert.Single(record.Member("members")!.Value.AsArray());
    }

    [Fact]
    public void ClosingTwiceKeepsTheFirstMarkerAndReopeningAnOpenTeamWritesNothing()
    {
        DeadOrchestratorTeam(MemberAt(Closer, "%1", TreeDir("t-closer")));
        AppendGoneRow(Closer, "proj");
        Assert.True(TeamClose("proj", ("reopen", true)).Get("reopened")!.Value.AsBool());
        Assert.Null(TeamFile("proj").Member("closed"));

        _ = TeamClose("proj");
        var first = _sandbox.TeamText(TeamPath("proj"));
        _ = TeamClose("proj");

        Assert.Equal(first, _sandbox.TeamText(TeamPath("proj")));
    }

    [Theory]
    [InlineData("close", "--bogus", "unrecognized flag --bogus")]
    [InlineData("reopen", "--preview", "name one team")]
    public void TheVerbsAreRegisteredAndCheckTheirWords(string verb, string word, string expected)
    {
        var (code, _, stderr) = Roster(verb, word);

        Assert.Equal(2, code);
        Assert.Contains(expected, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AReopenAskedOnlyForItsPreviewIsRefusedAndKeepsTheMarker()
    {
        DeadOrchestratorTeam(MemberAt(Closer, "%1", TreeDir("t-closer")));
        AppendGoneRow(Closer, "proj");
        _ = TeamClose("proj");

        var result = TeamClose("proj", ("reopen", true), ("preview", true));

        Assert.Equal("bad-request", result.Get("refused")!.Value.Member("reason")!.Value.AsString());
        Assert.NotNull(TeamFile("proj").Member("closed"));
    }

    [Fact]
    public void AStrayAgentOfTheSameCheckoutIsNeitherOfferedNorEnded()
    {
        // named for the checkout (proj), not the team (mine), and in no team file; an agent named for the team stays in the set
        const string Stray = "proj-reviewer";
        ObserverSeesAParent();
        PutHerdrOnPath();
        // the real shape, <checkout>/.claude/ah2: the core runs in the checkout it derives from the dir, and only there does a stray match
        var dir = Path.Combine(Project, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(dir, "teams"));
        _sandbox.PlantTeam(Path.Combine(dir, "teams", "mine.json"), LiveTeamJson("herdr", string.Empty, pid: "99999999", pidStart: GoneStart));
        _sandbox.PlantPeers(
            dir,
            $"{{\"type\":\"peer\",\"status\":\"up\",\"name\":\"{Stray}\",\"role\":\"reviewer\",\"session_id\":\"sess-stray\",\"pid\":{LivePidOfAnotherProcess()},\"pane_id\":\"p9\",\"cwd\":\"{Project}\",\"ts\":\"{Stamp(DateTimeOffset.UtcNow)}\"}}\n");
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        _runner.Output = argv => argv.Contains("agent") && argv.Contains("list")
            ? $"{{\"result\":{{\"agents\":[{{\"name\":\"{Stray}\",\"pane_id\":\"p9\",\"cwd\":\"{Project}\"}},{{\"name\":\"mine-implementor\",\"pane_id\":\"p8\",\"cwd\":\"{Project}\"}}]}}}}"
            : argv.Contains("--show-toplevel") ? Project + "\n" : string.Empty;

        var preview = TeamClose("mine", ("dir", dir), ("preview", true));

        string Rows(string key) => string.Join(",", preview.Get(key)!.Value.AsArray().Select(static r => r.Member("member")!.Value.AsString()));
        Assert.DoesNotContain(Stray, Rows("live"), StringComparison.Ordinal);
        Assert.DoesNotContain(Stray, Rows("unknown"), StringComparison.Ordinal);
        Assert.DoesNotContain(Stray, Rows("held"), StringComparison.Ordinal);

        // naming it in the confirmation does not reach it either
        Assert.True(TeamClose("mine", ("dir", dir), ("confirm_live", Names(Stray, "mine-implementor"))).Get("closed")!.Value.AsBool());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("p9"));
    }

    [Fact]
    public void AnUnknownTeamIsRefused() =>
        Assert.Equal("unknown-team", TeamClose("nobody").Get("refused")!.Value.Member("reason")!.Value.AsString());

    [Fact]
    public void ClosingAManagerMarksItsConfirmedLaneChildAndReopeningClearsIt()
    {
        ParentLane(LaneMember);
        AppendGoneRow("proj-s1-reviewer", "proj-s1");

        var closed = TeamClose("proj");

        Assert.Equal("proj-s1", Assert.Single(closed.Get("lanes")!.Value.AsArray()).AsString());
        Assert.NotNull(TeamFile("proj-s1").Member("closed"));

        _ = TeamClose("proj", ("reopen", true));
        Assert.Null(TeamFile("proj-s1").Member("closed"));
    }

    private static long At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    [Fact]
    public void RetiringWritesTheSameMarkerByRetiredAndEndsNobody()
    {
        DeadOrchestratorTeam(MemberAt(Closer, "%1", TreeDir("t-closer")));
        AppendGoneRow(Closer, "proj");

        Assert.Null(CommandsModule.TeamRetire(ComposeServices(), Hierarchy, "proj", 1));

        var record = TeamFile("proj");
        Assert.Equal("retired", record.Member("closed")!.Value.Member("by")!.Value.AsString());
        Assert.Equal(Closer, Assert.Single(record.Member("members")!.Value.AsArray()).Member("name")!.Value.AsString());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("kill-pane"));
        Assert.True(TeamClose("proj", ("reopen", true)).Get("reopened")!.Value.AsBool());
        Assert.Null(TeamFile("proj").Member("closed"));
    }

    [Fact]
    public void RetiringLeavesAHoldAsItWas()
    {
        DeadOrchestratorTeam(MemberAt(Closer, "%1", TreeDir("t-closer")));
        var services = ComposeServices();
        var holdPath = services.Roster.HoldPath(Hierarchy, "proj");
        const string Hold = "{\"kind\":\"quit\",\"phase\":\"held\",\"since\":\"2026-10-24T00:00:00Z\"}";
        PutHold(holdPath, Hold);

        Assert.Null(CommandsModule.TeamRetire(services, Hierarchy, "proj", 1));

        Assert.Equal(Hold, HoldBody(holdPath));
        Assert.Equal(("quit", "held"), services.Roster.HoldOf(Hierarchy, "proj") is { } hold ? (hold.Kind, hold.Phase) : default);
        Assert.NotNull(TeamFile("proj").Member("closed"));
    }

    [Fact]
    public void AReopenRecordsItselfAndHoldsOffARetireWhoseWorkIsOlder()
    {
        DeadOrchestratorTeam(MemberAt(Closer, "%1", TreeDir("t-closer")));
        AppendGoneRow(Closer, "proj");
        Assert.True(TeamClose("proj").Get("closed")!.Value.AsBool());
        Assert.True(TeamClose("proj", ("reopen", true)).Get("reopened")!.Value.AsBool());
        var reopened = TeamFile("proj").Member("reopened")!.Value;
        Assert.Equal("ui", reopened.Member("by")!.Value.AsString());
        var at = At(reopened.Member("at")!.Value.AsString());

        Assert.Null(CommandsModule.TeamRetire(ComposeServices(), Hierarchy, "proj", at));
        Assert.Null(TeamFile("proj").Member("closed"));

        Assert.Null(CommandsModule.TeamRetire(ComposeServices(), Hierarchy, "proj", at + 1));
        Assert.NotNull(TeamFile("proj").Member("closed"));
    }

    [Fact]
    public void RetiringATeamLeavesItsLanesAndALaneRetiresAndReopensOnItsOwn()
    {
        ParentLane(LaneMember);

        Assert.Null(CommandsModule.TeamRetire(ComposeServices(), Hierarchy, "proj", 1));
        Assert.NotNull(TeamFile("proj").Member("closed"));
        Assert.Null(TeamFile("proj-s1").Member("closed"));

        Assert.Null(CommandsModule.TeamRetire(ComposeServices(), Hierarchy, "proj-s1", 1));
        Assert.NotNull(TeamFile("proj-s1").Member("closed"));

        Assert.True(TeamClose("proj-s1", ("reopen", true)).Get("reopened")!.Value.AsBool());
        Assert.Null(TeamFile("proj-s1").Member("closed"));
        Assert.NotNull(TeamFile("proj").Member("closed"));
    }

    [Fact]
    public void ALaneOnItsOwnIsRefused()
    {
        ParentLane(LaneMember);

        var result = TeamClose("proj-s1");

        Assert.Equal("lane", result.Get("refused")!.Value.Member("reason")!.Value.AsString());
        Assert.Null(TeamFile("proj-s1").Member("closed"));
    }

    [Fact]
    public void ReapCommitSkipsAClosedOrphanAndStillRemovesAnOpenOne()
    {
        ObserverSeesAParent();
        WriteTeam("kept", LiveTeamJson("terminal", TwoPeers, pid: "99999999", pidStart: GoneStart).TrimEnd('}') + ",\"closed\":{\"at\":\"2026-11-02T00:00:00.000Z\",\"by\":\"ui\"}}");
        WriteTeam("gone", LiveTeamJson("terminal", TwoPeers, pid: "99999999", pidStart: GoneStart));

        var (code, stdout, _) = Roster("reap", "--commit");

        Assert.Equal(0, code);
        Assert.True(_sandbox.TeamExists(TeamPath("kept")));
        Assert.False(_sandbox.TeamExists(TeamPath("gone")));
        var skipped = Assert.Single(JsonText.Parse(stdout).Member("skipped")!.Value.AsArray());
        Assert.Equal(("kept", "closed"), (skipped.Member("team")!.Value.AsString(), skipped.Member("why")!.Value.AsString()));
    }

    [Fact]
    public void ReapCommitClearsARetiredOrphanAndKeepsOneAUserClosed()
    {
        ObserverSeesAParent();
        string Marked(string marker) => LiveTeamJson("terminal", TwoPeers, pid: "99999999", pidStart: GoneStart).TrimEnd('}') + ",\"closed\":" + marker + "}";
        WriteTeam("old", Marked("{\"at\":\"2026-11-02T00:00:00.000Z\",\"by\":\"retired\"}"));
        WriteTeam("user", Marked("{\"at\":\"2026-11-02T00:00:00.000Z\",\"by\":\"user\"}"));
        WriteTeam("nameless", Marked("{\"at\":\"2026-11-02T00:00:00.000Z\"}"));

        var (planCode, plan, _) = Roster("reap");

        Assert.Equal(0, planCode);
        Assert.Equal("old", Assert.Single(JsonText.Parse(plan).Member("orphans")!.Value.AsArray()).Member("name")!.Value.AsString());
        Assert.True(_sandbox.TeamExists(TeamPath("old")));

        var (code, stdout, _) = Roster("reap", "--commit");

        Assert.Equal(0, code);
        Assert.False(_sandbox.TeamExists(TeamPath("old")));
        Assert.True(_sandbox.TeamExists(TeamPath("user")));
        Assert.True(_sandbox.TeamExists(TeamPath("nameless")));
        Assert.Equal(["nameless", "user"], JsonText.Parse(stdout).Member("skipped")!.Value.AsArray().Select(static s => s.Member("team")!.Value.AsString()).Order());
    }

    /// <summary>Runs <c>Before</c> on each UpdateTeam; an answer it gives replaces the real call.</summary>
    public class HookedUpdateTeam : System.Reflection.DispatchProxy
    {
        public Ah.Hierarchy.StateCalls.IHierarchyStateCalls Inner { get; set; } = null!;

        public Func<string?, Ah.Abstractions.StateUpdateResult?> Before { get; set; } = static _ => null;

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(Ah.Hierarchy.StateCalls.IHierarchyStateCalls.UpdateTeam) && Before((string?)args![1]) is { } answered)
            {
                return answered;
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }
        }
    }

    private void HookUpdateTeam(Func<string?, Ah.Abstractions.StateUpdateResult?> before) =>
        _wrapStateCalls = inner =>
        {
            var proxy = System.Reflection.DispatchProxy.Create<Ah.Hierarchy.StateCalls.IHierarchyStateCalls, HookedUpdateTeam>();
            ((HookedUpdateTeam)(object)proxy).Inner = inner;
            ((HookedUpdateTeam)(object)proxy).Before = before;
            return proxy;
        };

    [Fact]
    public void ReapCommitSparesATeamAUserClosedAfterThePlanRead()
    {
        ObserverSeesAParent();
        string Marked(string by) => LiveTeamJson("terminal", TwoPeers, pid: "99999999", pidStart: GoneStart).TrimEnd('}') + ",\"closed\":{\"at\":\"2026-11-02T00:00:00.000Z\",\"by\":\"" + by + "\"}}";
        WriteTeam("raced", Marked("retired"));
        WriteTeam("old", Marked("retired"));
        HookUpdateTeam(team =>
        {
            if (team == "raced")
            {
                WriteTeam("raced", Marked("ui"));
            }

            return null;
        });

        var (code, stdout, _) = Roster("reap", "--commit");

        Assert.Equal(0, code);
        Assert.True(_sandbox.TeamExists(TeamPath("raced")));
        Assert.False(_sandbox.TeamExists(TeamPath("old")));
        var result = JsonText.Parse(stdout);
        Assert.Equal("old", Assert.Single(result.Member("reaped")!.Value.AsArray()).Member("name")!.Value.AsString());
        var skipped = Assert.Single(result.Member("skipped")!.Value.AsArray());
        Assert.Equal(("raced", "closed"), (skipped.Member("team")!.Value.AsString(), skipped.Member("why")!.Value.AsString()));
    }

    [Fact]
    public void AMarkerThatCouldNotBeWrittenIsAWarning()
    {
        DeadOrchestratorTeam(MemberAt(Closer, "%1", TreeDir("t-closer")));
        AppendGoneRow(Closer, "proj");
        HookUpdateTeam(static _ => new Ah.Abstractions.StateUpdateResult(false, new Ah.Abstractions.StateResult(false, ReadOnlyMemory<byte>.Empty, "disk full", "io")));

        var result = TeamClose("proj");

        Assert.Contains(result.Get("warnings")!.Value.AsArray(), w => w.AsString().Contains("disk full", StringComparison.Ordinal));
    }

    [Fact]
    public void AMarkerThatWasWrittenIsNoWarning()
    {
        DeadOrchestratorTeam(MemberAt(Closer, "%1", TreeDir("t-closer")));
        AppendGoneRow(Closer, "proj");

        var result = TeamClose("proj");

        Assert.DoesNotContain(result.Get("warnings")!.Value.AsArray(), w => w.AsString().Contains("marker", StringComparison.Ordinal));
    }

    [Fact]
    public void ASecondRetireKeepsTheFirstMarker()
    {
        WriteTeam("old", LiveTeamJson("terminal", TwoPeers, pid: "99999999", pidStart: GoneStart).TrimEnd('}') + ",\"closed\":{\"at\":\"2026-11-02T00:00:00.000Z\",\"by\":\"retired\"}}");

        Assert.Null(CommandsModule.TeamRetire(ComposeServices(), Hierarchy, "old", 1));

        Assert.Equal("2026-11-02T00:00:00.000Z", TeamFile("old").Member("closed")!.Value.Member("at")!.Value.AsString());
    }

    [Fact]
    public void AUsersCloseTakesARetiredMarkerOverAndARetireNeverTakesAUsersOver()
    {
        DeadOrchestratorTeam(MemberAt(Closer, "%1", TreeDir("t-closer")));
        AppendGoneRow(Closer, "proj");
        Assert.Null(CommandsModule.TeamRetire(ComposeServices(), Hierarchy, "proj", 1));

        Assert.True(TeamClose("proj").Get("closed")!.Value.AsBool());

        Assert.Equal("ui", TeamFile("proj").Member("closed")!.Value.Member("by")!.Value.AsString());

        Assert.Null(CommandsModule.TeamRetire(ComposeServices(), Hierarchy, "proj", 1));

        Assert.Equal("ui", TeamFile("proj").Member("closed")!.Value.Member("by")!.Value.AsString());
    }

    [Fact]
    public void AHoldPausesTheTeamAndClosesNothingAndASecondCallSealsOnceTheMemberAnswered()
    {
        ArrangeQuit();

        var first = TeamClose("proj", ("hold", true));

        Assert.True(first.Get("hold")!.Value.AsBool());
        Assert.Equal("winding-down", first.Get("phase")!.Value.AsString());
        Assert.Equal(Impl, first.Get("waiting_on")!.Value.AsArray().Single().AsString());
        Assert.Equal("winding-down", QuitHold("proj").Member("phase")!.Value.AsString());

        AnswerOnFirstSleep("proj");
        var second = TeamClose("proj", ("hold", true));

        Assert.True(second.Get("sealed")!.Value.AsBool());
        Assert.Equal("held", QuitHold("proj").Member("phase")!.Value.AsString());
        // a hold pauses; it never closes the team or ends a session
        Assert.Null(TeamFile("proj").Member("closed"));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("kill-pane"));
    }

    [Fact]
    public void NegativeAHoldWithAPreviewIsABadRequestAndWritesNoHold()
    {
        ArrangeQuit();

        var result = TeamClose("proj", ("hold", true), ("preview", true));

        Assert.Equal("bad-request", result.Get("refused")!.Value.Member("reason")!.Value.AsString());
        Assert.Null(HoldBody(QuitHoldPath("proj")));
    }
}
