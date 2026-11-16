using Ah.Commands;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Tests;

/// <summary><c>roster resume --checkpoint</c>: which members are back, what is re-sent, and that nothing is relaunched by default.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string ImplSession = "sess-impl";

    private int _recordCount;

    private static JsonArray Strings(params string[] values)
    {
        var array = new JsonArray();
        array.AddRange(values.Select(v => (JsonValue)v));
        return array;
    }

    /// <summary>Writes a checkpoint record by hand and returns its path; the one member is <see cref="Impl"/>.</summary>
    private string WriteRecord(string? snapshot, string? session = ImplSession, string? request = null, string[]? open = null, string classification = "asked", string team = "proj")
    {
        var path = Path.Combine(_sandbox.Root, $"checkpoint-record-{_recordCount++}.json");
        var record = new JsonObject
        {
            ["ts"] = "2026-10-17T10:00:00.000Z",
            ["team"] = team,
            ["snapshot"] = snapshot is null ? JsonValue.Null : snapshot,
            ["anchor"] = "/anchors/run.md",
            ["rollup"] = JsonValue.Null,
            ["members"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = Impl,
                    ["role"] = "implementor",
                    ["session"] = session is null ? JsonValue.Null : session,
                    ["worktree"] = Project,
                    ["branch"] = "ah/feature",
                    ["head"] = Sha,
                    ["classification"] = classification,
                    ["request"] = request is null ? JsonValue.Null : request,
                    ["open_requests"] = Strings(open ?? []),
                },
            },
            ["orchestrator"] = new JsonObject { ["session"] = JsonValue.Null, ["pid"] = Environment.ProcessId },
            ["supersedes"] = new JsonArray(),
        };
        File.WriteAllText(path, JsonText.Stringify(record, 2));
        return path;
    }

    private void Registered(string? session, bool live)
    {
        _registrations = session is null ? [] : new() { [Impl + "@proj"] = new MemberRegistrationLiveness(session, live, live ? "registration live" : "registration pid 1 is not running as this user") };
    }

    private (int Code, JsonValue Result, string Stderr) ResumeCheckpointOf(string record, params string[] extra)
    {
        _callers ??= new RecordingCallers { StartText = SelfStart };
        var (code, stdout, stderr) = Roster(["resume", "--checkpoint", record, "--team", "proj", "--cwd", Project, "--orchestrator-pid", Self, .. extra]);
        var result = stdout.Length > 0 ? JsonText.Parse(stdout) : new JsonObject();
        AssertEveryRefusalHasAReason(result);
        return (code, result, stdout + stderr);
    }

    /// <summary>A relaunch that was refused or failed always says why; an empty reason is a defect wherever it comes from.</summary>
    private static void AssertEveryRefusalHasAReason(JsonValue result)
    {
        if (result.Member("members") is not { IsArray: true } rows)
        {
            return;
        }

        foreach (var row in rows.AsArray().Where(r => r.Member("status") is { IsText: true } s && s.AsString() is "relaunch-refused" or "relaunch-failed"))
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Member("why") is { IsText: true } why ? why.AsString() : null), $"{row.Member("name")} was {row.Member("status")} with no reason");
        }
    }

    [Fact]
    public void AnUnreadablePaneListingRefusesTheRelaunchWithTheReadersOwnReasonAndAnAnsweredListingNeverSaysItCouldNotBeListed()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Unknown);
        _panes.Reason = "herdr exited 1: socket not found";

        var (_, refused, _) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);
        var why = refused.Member("members")!.Value.AsArray().Single().Member("why")!.Value.AsString();

        Assert.Contains("herdr exited 1: socket not found", why, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be listed", why, StringComparison.Ordinal);

        var answered = PendingMemberWithASnapshot(PaneKnowledge.Present);
        _panes.Harness = true;
        var (_, harness, _) = ResumeCheckpointOf(answered, "--yes", "--relaunch", Impl);
        Assert.DoesNotContain("could not be listed", harness.Member("members")!.Value.AsArray().Single().Member("why")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberWithALiveHeldRecoveryRecordAndNoBareMarkIsBareSoResumeNeitherRelaunchesNorReBriefsIt()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Present);
        _panes.Harness = false;
        var records = Ah.StateDb.StateDbModule.CreateRecoverRecords(_sandbox.AhHome);
        var key = Ah.Watch.WatchModule.RecoverDirKey(ProjectHierarchyDir);
        records.Write(key, "proj", Impl, false, "{\"state\":\"held\",\"pid\":" + Self + ",\"pid_start\":\"" + SelfStart + "\",\"detected_at\":\"2026-10-24T00:00:00Z\"}");

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("bare", StatusOfMember(result));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));

        records.Write(key, "proj", Impl, false, "{\"state\":\"done\",\"pid\":" + Self + ",\"pid_start\":\"" + SelfStart + "\",\"detected_at\":\"2026-10-24T00:00:00Z\"}");
        var (_, other, _) = ResumeCheckpointOf(record);
        Assert.NotEqual("bare", StatusOfMember(other));
    }

    private static string StatusOfMember(JsonValue result) =>
        result.Member("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == Impl).Member("status")!.Value.AsString();

    // ---------------------------------------------------------------- verification

    [Theory]
    [InlineData(ImplSession, true, false, "live")]
    [InlineData(ImplSession, false, false, "missing")]
    [InlineData(ImplSession, false, true, "pending")]
    [InlineData(null, false, true, "pending")]
    [InlineData(null, false, false, "missing")]
    [InlineData("sess-other", true, false, "replaced")]
    public void AMemberIsLivePendingMissingOrReplacedByWhatItsRegistrationAndTheTeamViewSay(string? registeredSession, bool registrationLive, bool peerRowUp, string expected)
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        ScriptCheckpointTrees();
        _panes.Default = peerRowUp ? PaneKnowledge.Present : PaneKnowledge.Absent;
        Registered(registeredSession, registrationLive);

        var (code, result, text) = ResumeCheckpointOf(WriteRecord(null));

        Assert.True(code == 0, text);
        Assert.Equal("planned", result.Member("status")!.Value.AsString());
        Assert.Equal(expected, StatusOfMember(result));
    }

    [Fact]
    public void ADeadRegistrationDoesNotMaskThePaneBeingThereOrBeingGone()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        ScriptCheckpointTrees();
        Registered(ImplSession, live: false);

        _panes.Default = PaneKnowledge.Present;
        var (_, there, _) = ResumeCheckpointOf(WriteRecord(null));
        _panes.Default = PaneKnowledge.Absent;
        var (_, gone, _) = ResumeCheckpointOf(WriteRecord(null));
        _panes.Default = PaneKnowledge.Unknown;
        var (_, unknown, _) = ResumeCheckpointOf(WriteRecord(null));

        Assert.Equal("pending", StatusOfMember(there));
        Assert.Equal("missing", StatusOfMember(gone));
        Assert.Equal("pending", StatusOfMember(unknown));
        Assert.Contains("could not be listed", unknown.Member("members")!.Value.AsArray().Single().Member("why")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PlanningSendsNothingLaunchesNothingAndLeavesTheTeamFileAlone()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        ScriptCheckpointTrees();
        Registered(ImplSession, live: true);
        var before = _sandbox.TeamText(TeamPath("proj"));

        var (code, result, text) = ResumeCheckpointOf(WriteRecord(null, request: "req-1", open: ["open-1"]));

        Assert.True(code == 0, text);
        Assert.Equal("planned", result.Member("status")!.Value.AsString());
        Assert.Empty(_sent);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));
    }

    [Fact]
    public void ANamedRecordIsRequiredToExistAndATeamWithNoRecordIsRefused()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));

        var (code, _, text) = ResumeCheckpointOf(Path.Combine(_sandbox.Root, "absent.json"));
        Assert.NotEqual(0, code);
        Assert.Contains("no such record", text, StringComparison.Ordinal);

        _callers ??= new RecordingCallers { StartText = SelfStart };
        var (noneCode, _, noneErr) = Roster("resume", "--checkpoint", "--team", "proj", "--cwd", Project);
        Assert.NotEqual(0, noneCode);
        Assert.Contains("no checkpoint for", noneErr, StringComparison.Ordinal);
        Assert.DoesNotContain("no snapshot", noneErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordOfAnotherTeamIsRefusedAndAllIsNotOffered()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));

        var (mismatch, _, mismatchText) = ResumeCheckpointOf(WriteRecord(null, team: "other"));
        Assert.NotEqual(0, mismatch);
        Assert.Contains("does not match", mismatchText, StringComparison.Ordinal);

        _callers ??= new RecordingCallers { StartText = SelfStart };
        var (allCode, _, allErr) = Roster("resume", "--checkpoint", "--all", "--cwd", Project);
        Assert.NotEqual(0, allCode);
        Assert.Contains("--all", allErr, StringComparison.Ordinal);
    }

    [Fact]
    public void RelaunchFlagsWithoutCheckpointAreRefusedAndPlainResumeIsUnchanged()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));

        var (relaunch, _, relaunchErr) = Roster("resume", "--relaunch", Impl, "--team", "proj", "--cwd", Project);
        var (missing, _, missingErr) = Roster("resume", "--relaunch-missing", "--team", "proj", "--cwd", Project);
        var (plain, _, plainErr) = Roster("resume", "--team", "proj", "--cwd", Project);

        Assert.NotEqual(0, relaunch);
        Assert.Contains("only go with --checkpoint", relaunchErr, StringComparison.Ordinal);
        Assert.NotEqual(0, missing);
        Assert.Contains("only go with --checkpoint", missingErr, StringComparison.Ordinal);
        Assert.NotEqual(0, plain);
        Assert.Contains("no snapshot of team", plainErr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- re-briefing

    private (string Request, string Response) AnsweredCheckpointRequest()
    {
        ArrangeCheckpoint();
        _panes.Default = PaneKnowledge.Present;
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var taken = Take();
        var response = CheckpointAnswer(taken, $"Checkpoint: ready branch=ah/feature sha={Sha} inflight=none");
        _sent.Clear();
        return (taken.Member("members")!.Value.AsArray().Single().Member("request")!.Value.AsString(), response);
    }

    [Fact]
    public void AMemberWithOpenRequestsIsReBriefedOnceFromItsCheckpointResponseAndOnlyWithYes()
    {
        var (request, response) = AnsweredCheckpointRequest();
        Registered(ImplSession, live: true);
        var record = WriteRecord(null, request: request, open: ["open-1"]);

        var (planCode, _, planText) = ResumeCheckpointOf(record);
        Assert.True(planCode == 0, planText);
        Assert.Empty(_sent);

        var (code, result, text) = ResumeCheckpointOf(record, "--yes");

        Assert.True(code == 0, text);
        var sent = Assert.Single(_sent);
        Assert.Equal(Impl, sent.Member);
        Assert.Equal(MsgRows.PathKey(response), MsgRows.PathKey(sent.Ref));
        Assert.Equal("live", StatusOfMember(result));
    }

    [Fact]
    public void AMemberWithNoOpenRequestsGetsNothingAndNeitherDoesOneThatIsNotLive()
    {
        var (request, _) = AnsweredCheckpointRequest();
        Registered(ImplSession, live: true);

        var (quiet, _, quietText) = ResumeCheckpointOf(WriteRecord(null, request: request, open: [], classification: "idle-clean"), "--yes");
        Assert.True(quiet == 0, quietText);
        Assert.Empty(_sent);

        Registered(ImplSession, live: false);
        var (pending, result, pendingText) = ResumeCheckpointOf(WriteRecord(null, request: request, open: ["open-1"]), "--yes");
        Assert.True(pending == 0, pendingText);
        Assert.Equal("pending", StatusOfMember(result));
        Assert.Empty(_sent);

        Registered("sess-other", live: true);
        var (replaced, replacedResult, replacedText) = ResumeCheckpointOf(WriteRecord(null, request: request, open: ["open-1"]), "--yes");
        Assert.True(replaced == 0, replacedText);
        Assert.Equal("replaced", StatusOfMember(replacedResult));
        Assert.Empty(_sent);
    }

    [Fact]
    public void AnOpenMemberWhoseCheckpointRequestWasNeverAnsweredIsNotReBriefedAndSaysWhy()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var taken = Take();
        _sent.Clear();
        Registered(ImplSession, live: true);
        var record = WriteRecord(null, request: taken.Member("members")!.Value.AsArray().Single().Member("request")!.Value.AsString(), open: ["open-1"]);

        var (code, result, text) = ResumeCheckpointOf(record, "--yes");

        Assert.True(code == 0, text);
        Assert.Empty(_sent);
        var row = result.Member("members")!.Value.AsArray().Single();
        Assert.Contains("no open request file", row.Member("rebrief")!.Value.AsString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- an unanswered member falls back to its open requests

    private const string OpenA = "20261017-100000-aaaa";

    private const string OpenB = "20261017-100100-bbbb";

    private (string Request, string[] Paths) UnansweredWithTwoOpenRequests()
    {
        ArrangeCheckpoint();
        _panes.Default = PaneKnowledge.Present;
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var taken = Take();
        _sent.Clear();
        var paths = new[] { AskRelease(OpenA), AskRelease(OpenB) };
        Registered(ImplSession, live: true);
        return (taken.Member("members")!.Value.AsArray().Single().Member("request")!.Value.AsString(), paths);
    }

    [Fact]
    public void AnUnansweredMemberIsSentOneBriefPerOpenRequestOldestFirstAndNeverTheCheckpointRequest()
    {
        var (request, paths) = UnansweredWithTwoOpenRequests();
        var record = WriteRecord(null, request: request, open: [OpenB, OpenA]);

        var (planCode, plan, planText) = ResumeCheckpointOf(record);
        Assert.True(planCode == 0, planText);
        Assert.Equal("unanswered-fallback", plan.Member("members")!.Value.AsArray().Single().Member("rebrief")!.Value.AsString());
        Assert.Empty(_sent);

        var (code, result, text) = ResumeCheckpointOf(record, "--yes");

        Assert.True(code == 0, text);
        Assert.Equal(paths.Select(MsgRows.PathKey), _sent.Select(s => MsgRows.PathKey(s.Ref)).ToArray());
        Assert.DoesNotContain(_sent, s => s.Ref.Contains("--checkpoint--", StringComparison.Ordinal));
        Assert.Equal("unanswered-fallback", result.Member("members")!.Value.AsArray().Single().Member("rebrief")!.Value.AsString());
    }

    [Fact]
    public void AnUnansweredMemberWithNoOpenRequestsIsSentNothing()
    {
        var (request, _) = UnansweredWithTwoOpenRequests();

        var (code, result, text) = ResumeCheckpointOf(WriteRecord(null, request: request, open: []), "--yes");

        Assert.True(code == 0, text);
        Assert.Empty(_sent);
        Assert.Null(result.Member("members")!.Value.AsArray().Single().Member("rebrief"));
    }

    [Fact]
    public void AnAnsweredMemberStillGetsItsCheckpointResponseAndNotTheOpenRequests()
    {
        var (request, response) = AnsweredCheckpointRequest();
        var paths = new[] { AskRelease(OpenA), AskRelease(OpenB) };
        Registered(ImplSession, live: true);

        var (code, _, text) = ResumeCheckpointOf(WriteRecord(null, request: request, open: [OpenA, OpenB]), "--yes");

        Assert.True(code == 0, text);
        Assert.Equal([MsgRows.PathKey(response)], _sent.Select(s => MsgRows.PathKey(s.Ref)).ToArray());
        Assert.Empty(_sent.Select(s => MsgRows.PathKey(s.Ref)).Intersect(paths.Select(MsgRows.PathKey)));
    }

    // ---------------------------------------------------------------- pending shows its age; relaunch checks at action time

    [Fact]
    public void APendingRowShowsHowLongAgoItsRegistrationWasWrittenAndALiveOneShowsNoAge()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        ScriptCheckpointTrees();
        _panes.Default = PaneKnowledge.Present;
        _registrations = new() { [Impl + "@proj"] = new MemberRegistrationLiveness(ImplSession, false, "registration pid 1 is not running as this user", DateTimeOffset.UtcNow.AddSeconds(-90), null, true) };

        var (_, pending, _) = ResumeCheckpointOf(WriteRecord(null));
        var age = pending.Member("members")!.Value.AsArray().Single().Member("age_seconds")!.Value.AsNumber();
        Assert.InRange(age, 90, 180);

        _registrations = new() { [Impl + "@proj"] = new MemberRegistrationLiveness(ImplSession, true, "registration live", DateTimeOffset.UtcNow.AddSeconds(-90)) };
        var (_, live, _) = ResumeCheckpointOf(WriteRecord(null));
        Assert.Null(live.Member("members")!.Value.AsArray().Single().Member("age_seconds"));
    }

    [Fact]
    public void RelaunchMissingLeavesAPendingMemberAlone()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        ScriptCheckpointTrees();
        _panes.Default = PaneKnowledge.Present;
        Registered(ImplSession, live: false);

        var (code, missing, text) = ResumeCheckpointOf(WriteRecord(null), "--yes", "--relaunch-missing");

        Assert.True(code == 0, text);
        Assert.Equal("pending", StatusOfMember(missing));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    // ---------------------------------------------------------------- relaunching a pending member

    /// <summary>The member keeps its row in the team file (so it has a pane), has no registration, and the pane is in <paramref name="state"/>.</summary>
    private string PendingMemberWithASnapshot(PaneKnowledge state)
    {
        var home = ResumeHome();
        var snapshot = TakeSnapshot(home, FamilyWorkTree("rc2"), "log");
        _registrations = [];
        _panes.Default = state;
        return WriteRecord(snapshot);
    }

    [Fact]
    public void ANamedPendingMemberWithOnlyAPlainShellInItsPaneIsRelaunched()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Present);
        _panes.Harness = false;

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
        Assert.Contains(_runner.Calls, argv => argv.Contains("new-window"));
        Assert.Empty(_detached); // a resume keeps its own registration wait
    }

    [Fact]
    public void ARelaunchNamingOneMemberDoesNotReBriefTheLiveMembers()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        ScriptCheckpointTrees();
        Registered(ImplSession, live: true);
        var path = WriteRecord(null, request: "req-1", open: ["open-1"]);
        var root = JsonText.Parse(File.ReadAllText(path)).AsObject();
        root.Get("members")!.Value.AsArray().Add(JsonValue.FromObject(new JsonObject
        {
            ["name"] = "proj-other",
            ["role"] = "reviewer",
            ["session"] = JsonValue.Null,
            ["worktree"] = Project,
            ["branch"] = "ah/feature",
            ["head"] = Sha,
            ["classification"] = "asked",
            ["request"] = JsonValue.Null,
            ["open_requests"] = Strings(),
        }));
        File.WriteAllText(path, JsonText.Stringify(JsonValue.FromObject(root), 2));

        var (code, plain, plainText) = ResumeCheckpointOf(path);
        var (named, narrowed, narrowedText) = ResumeCheckpointOf(path, "--relaunch", "proj-other");

        Assert.True(code == 0, plainText);
        Assert.True(named == 0, narrowedText);
        Assert.DoesNotContain("touches only", RebriefOf(plain), StringComparison.Ordinal);
        Assert.Contains("--relaunch proj-other touches only that member", RebriefOf(narrowed), StringComparison.Ordinal);
    }

    private static string RebriefOf(JsonValue result) =>
        result.Member("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == Impl).Member("rebrief")?.AsString() ?? string.Empty;

    [Fact]
    public void ARelaunchOfAMemberLaunchesItUnderItsOwnNameAndNeverStepsToAnotherFreeSuffix()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Present);
        _panes.Harness = false;

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
        var launches = string.Join("\n", _runner.Calls.Select(argv => string.Join(' ', argv))).Split('\n').Where(l => l.Contains("--name", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(launches);
        Assert.All(launches, l => Assert.Contains("--name " + Impl, l, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "a harness is running in its pane")]
    [InlineData(null, "the process table could not be read")]
    public void ANamedPendingMemberIsRefusedWhenAHarnessRunsInItsPaneOrTheProcessTableCannotBeRead(bool? harness, string reason)
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Present);
        _panes.Harness = harness;

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        var row = result.Member("members")!.Value.AsArray().Single();
        Assert.Equal("relaunch-refused", row.Member("status")!.Value.AsString());
        Assert.Contains(reason, row.Member("why")!.Value.AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void ANamedPendingMemberWhosePaneCannotBeListedIsPendingAndRefused()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Unknown);

        var (planCode, plan, planText) = ResumeCheckpointOf(record, "--relaunch", Impl);
        Assert.True(planCode == 0, planText);
        Assert.Equal("pending", StatusOfMember(plan));

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        var row = result.Member("members")!.Value.AsArray().Single();
        Assert.Equal("relaunch-refused", row.Member("status")!.Value.AsString());
        Assert.Contains("pane state unknown", row.Member("why")!.Value.AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void APresentPaneWhoseProcessCouldNotBeReadIsPendingAndARelaunchIntoItIsRefused()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Present);
        _panes.NoRoot = true;
        _panes.Harness = false;

        var (planCode, plan, planText) = ResumeCheckpointOf(record, "--relaunch-missing");
        Assert.True(planCode == 0, planText);
        Assert.Equal("pending", StatusOfMember(plan));

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        var row = result.Member("members")!.Value.AsArray().Single();
        Assert.Equal("relaunch-refused", row.Member("status")!.Value.AsString());
        Assert.Contains("pane state unknown", row.Member("why")!.Value.AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void AMissingMemberWhosePaneIsGoneIsRelaunchedWithoutAskingTheProcessTable()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Absent);
        _panes.Harness = true;

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch-missing");

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
    }

    [Fact]
    public void AMemberThatRegistersBetweenThePlanAndTheActIsNotRelaunched()
    {
        var (record, _) = MissingMemberWithASnapshot();
        _registrationsLater[Impl + "@proj"] = new MemberRegistrationLiveness(ImplSession, true, "registration live");

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch-missing");

        Assert.True(code == 0, text);
        var row = result.Member("members")!.Value.AsArray().Single();
        Assert.Equal("relaunch-refused", row.Member("status")!.Value.AsString());
        Assert.Contains("restored meanwhile", row.Member("why")!.Value.AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void L2cAMemberThatRegisteredAgainUndecidedWithEvidenceAMinuteOldIsNotRelaunched()
    {
        var (record, _) = MissingMemberWithASnapshot();
        _registrationsLater[Impl + "@proj"] = new MemberRegistrationLiveness(ImplSession, false, "registration pid not observable", DateTimeOffset.UtcNow.AddSeconds(-60));

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch-missing");

        Assert.True(code == 0, text);
        var row = result.Member("members")!.Value.AsArray().Single();
        Assert.Equal("relaunch-refused", row.Member("status")!.Value.AsString());
        Assert.Contains("restored meanwhile", row.Member("why")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeL2cAMemberThatRegisteredAgainUndecidedWithOldEvidenceOrProvenDeadIsRelaunched()
    {
        foreach (var again in new[]
        {
            new MemberRegistrationLiveness(ImplSession, false, "registration pid not observable", DateTimeOffset.UtcNow.AddHours(-2)),
            new MemberRegistrationLiveness(ImplSession, false, "registration pid is gone", DateTimeOffset.UtcNow.AddSeconds(-60), null, true),
        })
        {
            var (record, _) = MissingMemberWithASnapshot();
            _registrationsLater[Impl + "@proj"] = again;

            var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch-missing");

            Assert.True(code == 0, text);
            Assert.NotEqual("relaunch-refused", result.Member("members")!.Value.AsArray().Single().Member("status")!.Value.AsString());
        }
    }

    // ---------------------------------------------------------------- rev 5: no recorded pane, one listing per phase

    private string HomeTeam => Path.Combine(Project, ".claude", "ah2", "teams", "proj.json");

    [Fact]
    public void AMemberWhoseTeamRowRecordsNoPaneIsPendingAndBothRelaunchFormsRefuseIt()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Absent);
        var team = _sandbox.TeamText(HomeTeam);
        _sandbox.PlantTeam(HomeTeam, System.Text.RegularExpressions.Regex.Replace(team, "\"transport_id\":\"[^\"]*\",?", string.Empty));

        var (planCode, plan, planText) = ResumeCheckpointOf(record);
        Assert.True(planCode == 0, planText);
        var planned = plan.Member("members")!.Value.AsArray().Single();
        Assert.Equal("pending", planned.Member("status")!.Value.AsString());
        Assert.Contains("no recorded pane", planned.Member("why")!.Value.AsString(), StringComparison.Ordinal);

        foreach (var form in new[] { new[] { "--relaunch", Impl }, ["--relaunch-missing"] })
        {
            var (code, result, text) = ResumeCheckpointOf(record, ["--yes", .. form]);
            Assert.True(code == 0, text);
            var row = result.Member("members")!.Value.AsArray().Single();
            Assert.NotEqual("relaunched", row.Member("status")!.Value.AsString());
            Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
        }
    }

    [Fact]
    public void ANamedMemberWithNoRecordedPaneIsRefusedWithTheReason()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Absent);
        var team = _sandbox.TeamText(HomeTeam);
        _sandbox.PlantTeam(HomeTeam, System.Text.RegularExpressions.Regex.Replace(team, "\"transport_id\":\"[^\"]*\",?", string.Empty));

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        var row = result.Member("members")!.Value.AsArray().Single();
        Assert.Equal("relaunch-refused", row.Member("status")!.Value.AsString());
        Assert.Contains("no recorded pane; absence cannot be proven", row.Member("why")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARelaunchRunListsTheMultiplexerTwiceInAllOnceForThePlanAndOnceBeforeTheFirstRelaunch()
    {
        var home = ResumeHome();
        var snapshot = TakeSnapshot(home, FamilyWorkTree("rc2"), "log");
        _registrations = [];
        var lists = new List<string>();
        _graphPanes = new Ah.Cli.Composition.PaneStateReader(
            transport =>
            {
                lists.Add(transport);
                return new Ah.Launch.PaneListing([], null);
            },
            () => []);
        var record = WriteRecord(snapshot);

        var (code, _, text) = ResumeCheckpointOf(record, "--yes", "--relaunch-missing");

        Assert.True(code == 0, text);
        Assert.Equal(2, lists.Count);
    }

    [Fact]
    public void ThreeRelaunchesShareOneActionTimeListingSoTheMultiplexerIsListedTwiceInAll()
    {
        var home = ResumeHome();
        string[] names = ["proj-implementor", "proj-reviewer", "proj-architect"];
        var rows = string.Join(',', names.Select((n, i) => $$"""{"role":"{{n[5..]}}","name":"{{n}}","route":"peer","transport_id":"%{{i + 1}}","model":"sonnet","expected_root":"{{Project}}"}"""));
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", rows));
        var grants = string.Join(',', names.Select((n, i) => "\"" + n + "\":{\"work_root\":\"" + FamilyWorkTree("rc" + i) + "\",\"stream\":null,\"role\":\"" + n[5..] + "\",\"extra\":[]}"));
        WriteHomeGrants(home, "log", "{" + grants + "}");
        var (snapCode, snapOut, snapErr) = Roster("snapshot", "--team", "proj", "--cwd", Project);
        Assert.True(snapCode == 0, snapOut + snapErr);
        var snapshot = JsonText.Parse(snapOut).Member("snapshot")!.Value.AsString();
        _registrations = [];
        var lists = new List<string>();
        _graphPanes = new Ah.Cli.Composition.PaneStateReader(
            transport =>
            {
                lists.Add(transport);
                return new Ah.Launch.PaneListing([], null);
            },
            () => []);
        var record = WriteRecord(snapshot);
        var withThree = File.ReadAllText(record).Replace(
            "\"members\": [",
            "\"members\": [" + string.Join(',', names.Skip(1).Select(n => $$"""{"name":"{{n}}","role":"{{n[5..]}}","session":null,"worktree":"{{Project}}","branch":"ah/feature","head":"{{Sha}}","classification":"asked","request":null,"open_requests":[]}""")) + ",",
            StringComparison.Ordinal);
        File.WriteAllText(record, withThree);

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch-missing");

        Assert.True(code == 0, text);
        Assert.Equal(3, result.Member("members")!.Value.AsArray().Count(m => m.Member("status")!.Value.AsString() == "relaunched"));
        Assert.Equal(2, lists.Count);
    }

    [Fact]
    public void APaneThatAppearsBetweenThePlanAndTheActIsRefusedAtActionTime()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Absent);
        _panes.AfterRefresh = PaneKnowledge.Present;
        _panes.Harness = true;

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch-missing");

        Assert.True(code == 0, text);
        var row = result.Member("members")!.Value.AsArray().Single();
        Assert.Equal("relaunch-refused", row.Member("status")!.Value.AsString());
        Assert.Contains("restore in progress", row.Member("why")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Equal(1, _panes.Refreshed);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void APaneAbsentAtThePlanAndPresentAtTheActIsRefusedAsARestoreInProgressEvenWithNoHarnessYet()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Absent);
        _panes.AfterRefresh = PaneKnowledge.Present;
        _panes.Harness = false;

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch-missing");

        Assert.True(code == 0, text);
        var row = result.Member("members")!.Value.AsArray().Single();
        Assert.Equal("relaunch-refused", row.Member("status")!.Value.AsString());
        Assert.Contains("restore in progress", row.Member("why")!.Value.AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void ANoRelaunchRunNeverRefreshesTheListing()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Absent);

        var (code, _, text) = ResumeCheckpointOf(record, "--yes");

        Assert.True(code == 0, text);
        Assert.Equal(0, _panes.Refreshed);
    }

    // ---------------------------------------------------------------- --all

    private void TwoOwnedTeamsWithRecords()
    {
        ArrangeCheckpoint();
        WriteTeam("two", LiveTeamJson("tmux", Member("implementor", "two-implementor", "%2")));
        PeerUp("two-implementor", "%2");
        var (code, stdout, stderr) = Roster("checkpoint", "--all");
        Assert.True(code == 0, stdout + stderr);
        _sent.Clear();
    }

    [Fact]
    public void AllResumesEveryOwnedTeamPerTeamAndNeverTransfersOwnership()
    {
        TwoOwnedTeamsWithRecords();
        _panes.Default = PaneKnowledge.Present;
        Registered(ImplSession, live: true);
        var proj = _sandbox.TeamText(TeamPath("proj"));
        var two = _sandbox.TeamText(TeamPath("two"));
        _callers ??= new RecordingCallers { StartText = SelfStart };

        var (code, stdout, stderr) = Roster("resume", "--checkpoint", "--all", "--yes", "--cwd", Project, "--orchestrator-pid", Self);

        Assert.True(code == 0, stdout + stderr);
        var teams = JsonText.Parse(stdout).Member("teams")!.Value.AsArray();
        Assert.Equal(["proj", "two"], teams.Select(t => t.Member("team")!.Value.AsString()).Order().ToArray());
        Assert.Equal("live", teams.Single(t => t.Member("team")!.Value.AsString() == "proj").Member("members")!.Value.AsArray().Single().Member("status")!.Value.AsString());
        Assert.Equal("pending", teams.Single(t => t.Member("team")!.Value.AsString() == "two").Member("members")!.Value.AsArray().Single().Member("status")!.Value.AsString());
        Assert.Equal(proj, _sandbox.TeamText(TeamPath("proj")));
        Assert.Equal(two, _sandbox.TeamText(TeamPath("two")));
    }

    [Fact]
    public void AllLeavesOutAnOwnedTeamThatHasNoRecord()
    {
        TwoOwnedTeamsWithRecords();
        WriteTeam("bare", LiveTeamJson("tmux", Member("implementor", "bare-implementor", "%3")));
        Registered(ImplSession, live: true);
        _callers ??= new RecordingCallers { StartText = SelfStart };

        var (code, stdout, stderr) = Roster("resume", "--checkpoint", "--all", "--cwd", Project, "--orchestrator-pid", Self);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(["proj", "two"], JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Select(t => t.Member("team")!.Value.AsString()).Order().ToArray());
    }

    [Fact]
    public void AllWithNoOwnedTeamThatHasARecordSaysSoAndExitsTwoAndOtherFlagsRefuseIt()
    {
        WriteTeam("other", LiveTeamJson("tmux", Member("implementor", "other-implementor", "%2"), pid: "99999999", pidStart: GoneStart));
        _callers ??= new RecordingCallers { StartText = SelfStart };

        var (code, _, stderr) = Roster("resume", "--checkpoint", "--all", "--cwd", Project, "--orchestrator-pid", Self);
        Assert.Equal(2, code);
        Assert.Contains("no owned team has a checkpoint; name one with --team", stderr, StringComparison.Ordinal);

        Assert.NotEqual(0, Roster("resume", "--checkpoint", "--all", "--team", "other", "--cwd", Project).Code);
        Assert.NotEqual(0, Roster("resume", "--checkpoint", "--all", "--relaunch", "x", "--cwd", Project).Code);
    }

    [Fact]
    public void ARefusedReBriefIsReportedWithExitOne()
    {
        var (request, _) = AnsweredCheckpointRequest();
        Registered(ImplSession, live: true);
        _briefFails = true;

        var (code, result, _) = ResumeCheckpointOf(WriteRecord(null, request: request, open: ["open-1"]), "--yes");

        Assert.Equal(1, code);
        Assert.True(result.Member("mail_failed")!.Value.AsBool());
    }

    // ---------------------------------------------------------------- the Orchestrator

    [Fact]
    public void AnOrchestratorTheRestoreAlreadyReboundIsLeftAloneAndOneThatIsNotIsTransferred()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor).Replace("\"session_id\":null,\"pid\":" + Self, "\"session_id\":null,\"pid\":" + Self + ",\"pid_start\":\"" + SelfStart + "\"", StringComparison.Ordinal));
        ScriptCheckpointTrees();
        Registered(ImplSession, live: true);
        var before = _sandbox.TeamText(TeamPath("proj"));

        var (code, result, text) = ResumeCheckpointOf(WriteRecord(null), "--yes");

        Assert.True(code == 0, text);
        Assert.Equal("bound", result.Member("orchestrator")!.Value.AsString());
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));

        ObserverSeesAParent();
        var unbound = JsonText.Parse(LiveTeamJson("tmux", OneImplementor)).AsObject();
        unbound["orchestrator"].AsObject()["pid_start"] = GoneStart;
        unbound["orchestrator"].AsObject()["pid"] = 2147483000;
        WriteTeam("proj", JsonText.Stringify(unbound, 0));
        var (otherCode, other, otherText) = ResumeCheckpointOf(WriteRecord(null), "--yes");
        Assert.True(otherCode == 0, otherText);
        Assert.Equal("transferred", other.Member("orchestrator")!.Value.AsString());
        Assert.True(Ah.Roster.RosterModule.OrchestratorIsClaimed(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("orchestrator")));
        Assert.Contains(SelfStart, _sandbox.TeamText(TeamPath("proj")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheResultNamesTheAnchorTheResumePointAndEveryOpenRequestWithItsOwner()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        ScriptCheckpointTrees();
        Registered(ImplSession, live: true);
        var record = WriteRecord(null, open: ["open-1", "open-2"]);

        var (code, result, text) = ResumeCheckpointOf(record, "--yes");

        Assert.True(code == 0, text);
        Assert.Equal("/anchors/run.md", result.Member("anchor")!.Value.AsString());
        Assert.Equal(record, result.Member("resume_point")!.Value.AsString());
        var open = result.Member("open_requests")!.Value.AsArray();
        Assert.Equal(["open-1", "open-2"], open.Select(o => o.Member("id")!.Value.AsString()).ToArray());
        Assert.All(open, o => Assert.Equal(Impl, o.Member("owner")!.Value.AsString()));
    }

    // ---------------------------------------------------------------- relaunch

    private (string Record, string Worktree) MissingMemberWithASnapshot(string[]? open = null)
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("rc1");
        var snapshot = TakeSnapshot(home, worktree, "log");
        DismissEverything(home);
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", MemberRow(Project)));
        _registrations = [];
        return (WriteRecord(snapshot, open: open ?? []), worktree);
    }

    [Fact]
    public void AMissingMemberIsNotRelaunchedByYesAloneAndIsByRelaunchMissingOrItsName()
    {
        var (record, _) = MissingMemberWithASnapshot();

        var (code, result, text) = ResumeCheckpointOf(record, "--yes");
        Assert.True(code == 0, text);
        Assert.Equal("missing", StatusOfMember(result));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));

        var (otherCode, other, otherText) = ResumeCheckpointOf(record, "--yes", "--relaunch", "proj-someone-else");
        Assert.NotEqual(0, otherCode);
        Assert.Contains("proj-someone-else", otherText, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));

        var (missingCode, missing, missingText) = ResumeCheckpointOf(record, "--yes", "--relaunch-missing");
        Assert.True(missingCode == 0, missingText);
        Assert.Equal("relaunched", StatusOfMember(missing));
        Assert.Contains(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void RelaunchByNameLaunchesThatMemberOnly()
    {
        var (record, _) = MissingMemberWithASnapshot();

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
        Assert.Single(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void ALiveOrPendingOrReplacedMemberIsNeverRelaunchedEvenWhenNamed()
    {
        var (record, _) = MissingMemberWithASnapshot();

        foreach (var (session, live, expected) in new[] { (ImplSession, true, "live"), ("sess-other", true, "replaced") })
        {
            Registered(session, live);
            var (named, _, namedText) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);
            Assert.NotEqual(0, named);
            Assert.Contains("is running; nothing to relaunch", namedText, StringComparison.Ordinal);

            var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch-missing");
            Assert.True(code == 0, text);
            Assert.Equal(expected, StatusOfMember(result));
        }

        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void AFailedRelaunchIsReportedWithItsOpenRequestsListedAsOrphaned()
    {
        var (record, worktree) = MissingMemberWithASnapshot(["open-1"]);
        Directory.Delete(worktree, recursive: true);

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch-missing");

        Assert.True(code == 3, text);
        var row = result.Member("members")!.Value.AsArray().Single();
        Assert.Equal("relaunch-failed", row.Member("status")!.Value.AsString());
        Assert.Contains("work-tree-missing", row.Member("why")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Equal(["open-1"], row.Member("orphaned")!.Value.AsArray().Select(o => o.AsString()).ToArray());
        Assert.Equal($"ah roster resume --checkpoint {record} --relaunch {Impl} --yes", row.Member("next")!.Value.AsString());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }
}
