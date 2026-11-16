using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>
/// A SessionStart writes nothing at all (no registration, no roster or role record, no session-pids row) unless its parent is
/// a Claude harness above pid 1 and its session is not live on another process.
/// </summary>
public sealed class SessionStartGuardTests
{
    private const string Session = "sess-1";

    private sealed record Result(HookOutcome Outcome, List<CallerRequest> Registered, int Writes, int Errors, string Peers, int DaemonStarts = 0);

    private static Result Run(HarnessSessions? sessions, int parentPid = HarnessSessions.Pid, string? agentType = null, bool held = false)
    {
        var rig = held ? HoldFixture.Rig() : new HooksHostTests.Rig();
        if (held)
        {
            HoldFixture.Orchestrates(rig, Session, "tm");
            HoldFixture.Hold(rig, "tm");
        }

        var dir = rig.Sandbox.Root + "/proj/.claude/ah2";
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/agent-hierarchy.json", """{"version":1,"alwaysOrchestrator":true}""");
        var mailbox = BindingAttestationTests.MailboxFor(rig);
        var registered = new List<CallerRequest>();
        var callers = new BindingAttestationTests.Callers(BindingAttestationTests.Orchestrator, registered.Add);
        var daemon = new BindingAttestationTests.Daemon();
        var advisory = new AdvisoryServices(mailbox, null!, callers, null!, null!, new NoText(), null!, null!, daemon, null!, null!);
        var services = rig.Services with
        {
            Advisory = advisory,
            Sessions = sessions,
            Process = rig.Services.Process with { ParentPid = parentPid },
        };
        var e = new JsonObject { ["hook_event_name"] = "SessionStart", ["session_id"] = Session, ["cwd"] = rig.Sandbox.Root + "/proj", ["source"] = "resume" };
        if (agentType is not null)
        {
            e["agent_type"] = agentType;
        }

        var errors = rig.Errors.Records.Count;
        var outcome = rig.Run("sessionstart", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return new Result(outcome, registered, rig.Writer.Calls.Count, rig.Errors.Records.Count - errors, PeersOf(rig), daemon.EnsureCalls);
    }

    [Fact]
    public void ARegisteredSessionStartStartsTheDaemonAndShowsNoHoldLine()
    {
        var result = Run(new HarnessSessions(), agentType: "wrangl:implementor");

        Assert.Equal(1, result.DaemonStarts);
        Assert.DoesNotContain("IS QUIT", Encoding.UTF8.GetString(result.Outcome.Stdout.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void ARegisteredSessionStartOfAHeldTeamShowsTheHoldLineAndNeverStartsTheDaemon()
    {
        var result = Run(new HarnessSessions(), agentType: "wrangl:implementor", held: true);

        Assert.Equal(0, result.DaemonStarts);
        Assert.Contains("TEAM tm IS QUIT", Encoding.UTF8.GetString(result.Outcome.Stdout.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePeerRowOfASessionStartCarriesTheStartTimeOfItsHarnessProcessWhenItIsKnown()
    {
        var known = Run(new HarnessSessions(), agentType: "wrangl:implementor");
        var sessions = new HarnessSessions();
        _ = sessions.Starts.Remove(HarnessSessions.Pid);
        var unknown = Run(sessions, agentType: "wrangl:implementor");

        Assert.Contains($"\"pid_start\":\"{HarnessSessions.Start}\"", known.Peers, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"up\"", unknown.Peers, StringComparison.Ordinal);
        Assert.DoesNotContain("pid_start", unknown.Peers, StringComparison.Ordinal);
    }

    private static string PeersOf(HooksHostTests.Rig rig) => string.Join("\n", rig.Writer.Arguments);

    private static bool TeamFileSurvives(string orchestratorSession) => RunSweep(orchestratorSession, blockCopy: false).Survives;

    private sealed record Sweep(bool Survives, string StaleDir);

    private static Sweep RunSweep(string orchestratorSession, bool blockCopy, bool livePidWithNoStart = false, bool observerSeesNoParent = false, bool recordStart = true)
    {
        var observer = new HarnessSessions();
        if (!observerSeesNoParent)
        {
            observer.Starts[5000] = HarnessSessions.Start;
            observer.Ancestry[Environment.ProcessId] = [5000, 1];
        }
        else
        {
            observer.Ancestry[Environment.ProcessId] = [1];
        }

        var rig = new HooksHostTests.Rig(processTable: observer.Table);
        var config = rig.Sandbox.Root + "/proj/.claude/ah2";
        _ = Directory.CreateDirectory(config);
        File.WriteAllText(config + "/agent-hierarchy.json", """{"version":1,"alwaysOrchestrator":true}""");
        var hierarchy = rig.Sandbox.Home + "/.claude/ah2/hierarchy/proj";
        _ = Directory.CreateDirectory(hierarchy);
        if (blockCopy)
        {
            File.WriteAllText(hierarchy + "/_stale", "not a directory");
        }

        var created = rig.Services.Clock.UtcNow.AddMinutes(-1).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        var pid = livePidWithNoStart ? 5000 : 999999;
        var start = recordStart && !livePidWithNoStart ? $$""","pid_start":"{{HarnessSessions.Start}}" """.Trim() : string.Empty;
        File.WriteAllText(hierarchy + "/team.json", $$"""{"team_id":"t-legacy","created":"{{created}}","orchestrator":{"pid":{{pid}}{{start}},"session_id":"{{orchestratorSession}}"},"members":[]}""");
        var registered = new List<CallerRequest>();
        var advisory = new AdvisoryServices(BindingAttestationTests.MailboxFor(rig), null!, new BindingAttestationTests.Callers(BindingAttestationTests.Orchestrator, registered.Add), null!, null!, new NoText(), null!, null!, new BindingAttestationTests.Daemon(), null!, null!);
        var services = rig.Services with { Advisory = advisory, Sessions = new HarnessSessions(), Process = rig.Services.Process with { ParentPid = HarnessSessions.Pid } };
        var e = new JsonObject { ["hook_event_name"] = "SessionStart", ["session_id"] = Session, ["cwd"] = rig.Sandbox.Root + "/proj", ["source"] = "resume" };
        _ = rig.Run("sessionstart", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return new Sweep(!rig.Writer.Mutations.Contains("""{"clear":true}"""), hierarchy + "/_stale");
    }

    [Fact]
    public void AnAbandonedTeamIsClearedWithNoFileCopyEvenWhereNoCopyCouldBeMade()
    {
        // b397: the team is a row, so a file copy beside it threw and aborted every clear; a blocked _stale path stands in for that
        var cleared = RunSweep("another-session", blockCopy: false);
        var blocked = RunSweep("another-session", blockCopy: true);

        Assert.False(cleared.Survives);
        Assert.False(Directory.Exists(cleared.StaleDir));
        Assert.False(blocked.Survives);
    }

    [Fact]
    public void ATeamThatNamesTheStartingSessionAsItsOrchestratorIsNotClearedWhateverItsPid() =>
        Assert.True(TeamFileSurvives(Session));

    [Fact]
    public void ATeamOfAnotherSessionWithAProvenDeadOrchestratorIsStillCleared() =>
        Assert.False(TeamFileSurvives("another-session"));

    [Fact]
    public void NegativeADeadOrchestratorPidSeenByAnObserverThatSeesNoOtherProcessIsNotCleared() =>
        Assert.True(RunSweep("another-session", blockCopy: false, observerSeesNoParent: true).Survives);

    [Fact]
    public void NegativeATeamWhoseOrchestratorPidIsRunningWithNoRecordedStartIsNotCleared() =>
        Assert.True(RunSweep("another-session", blockCopy: false, livePidWithNoStart: true).Survives);

    [Fact]
    public void NegativeATeamWhoseDeadOrchestratorPidRecordsNoStartIsNotCleared() =>
        Assert.True(RunSweep("another-session", blockCopy: false, recordStart: false).Survives);

    private static void AssertWroteNothing(Result r)
    {
        Assert.Equal(0, r.Outcome.ExitCode);
        Assert.True(r.Outcome.Stdout.IsEmpty);
        Assert.Empty(r.Registered);
        Assert.Equal(0, r.Writes);
        Assert.Equal(1, r.Errors);
    }

    private static SessionRegistration Reg(string session, int pid, string start = HarnessSessions.Start) => new(session, pid, start, [$"{session}@tm"]);

    [Fact]
    public void AFirstEverSessionStartFromAClaudeHarnessRegisters()
    {
        var r = Run(new HarnessSessions());

        Assert.Single(r.Registered);
        Assert.True(r.Writes > 0);
        Assert.Equal(0, r.Errors);
    }

    [Fact]
    public void ARoleSessionStartFromAClaudeHarnessRegisters()
    {
        var r = Run(new HarnessSessions(), agentType: "wrangl:implementor");

        Assert.Single(r.Registered);
        Assert.True(r.Writes > 0);
    }

    [Fact]
    public void TheRoleIsRecordedWithTheHarnessProcess()
    {
        var services = new HooksHostTests.Rig().Services with { Sessions = new HarnessSessions() };

        var args = Ah.Hooks.Lifecycle.SessionStart.SessionRoleArgs(services, "s1", "implementor");

        Assert.Equal(HarnessSessions.Pid, args.Get("pid")!.Value.AsNumber());
        Assert.Equal(HarnessSessions.Start, args.Get("pid_start")!.Value.AsString());
    }

    [Fact]
    public void AHarnessWhoseStartIsUnknownRecordsNoProcess()
    {
        var sessions = new HarnessSessions();
        _ = sessions.Starts.Remove(HarnessSessions.Pid);
        var services = new HooksHostTests.Rig().Services with { Sessions = sessions };

        var args = Ah.Hooks.Lifecycle.SessionStart.SessionRoleArgs(services, "s1", "implementor");

        Assert.Null(args.Get("pid"));
        Assert.Null(args.Get("pid_start"));
    }

    [Theory]
    [InlineData("-zsh")]
    [InlineData("/bin/sh -c ah hook sessionstart")]
    [InlineData("node /usr/lib/other-tool/cli.js")]
    public void AForgedSessionStartWhoseParentIsNotAClaudeHarnessWritesNothing(string command)
    {
        var sessions = new HarnessSessions();
        sessions.Commands[HarnessSessions.Pid] = command;

        AssertWroteNothing(Run(sessions));
    }

    [Fact]
    public void ARoleSessionStartWhoseParentIsAShellWritesNoRoleRecord()
    {
        var sessions = new HarnessSessions();
        sessions.Commands[HarnessSessions.Pid] = "-zsh";

        AssertWroteNothing(Run(sessions, agentType: "wrangl:implementor"));
    }

    [Fact]
    public void ANodeRunningClaudeCodeIsAHarness()
    {
        var sessions = new HarnessSessions();
        sessions.Commands[HarnessSessions.Pid] = "node /usr/local/lib/node_modules/@anthropic-ai/claude-code/cli.js";

        Assert.Single(Run(sessions).Registered);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void AParentPidNotAboveOneWritesNothing(int parentPid)
    {
        var sessions = new HarnessSessions();
        sessions.Commands[parentPid] = "claude";
        sessions.Starts[parentPid] = HarnessSessions.Start;

        AssertWroteNothing(Run(sessions, parentPid));
    }

    [Fact]
    public void WithNoProcessTableNothingIsWritten()
    {
        AssertWroteNothing(Run(null));
    }

    [Fact]
    public void ASessionLiveOnAnotherProcessIsRefusedAndNothingIsWritten()
    {
        var sessions = new HarnessSessions(Reg(Session, 5000));
        sessions.Starts[5000] = HarnessSessions.Start;

        AssertWroteNothing(Run(sessions));
    }

    [Fact]
    public void ASessionWhosePriorProcessIsGoneIsReRegistered()
    {
        var r = Run(new HarnessSessions(Reg(Session, 5000)));

        Assert.Single(r.Registered);
        Assert.Equal(0, r.Errors);
    }

    [Fact]
    public void ASessionWhosePriorPidWasReusedIsReRegistered()
    {
        var sessions = new HarnessSessions(Reg(Session, 5000, "Fri Oct 16 09:00:00 2026"));
        sessions.Starts[5000] = HarnessSessions.Start;

        Assert.Single(Run(sessions).Registered);
    }

    [Fact]
    public void ASessionAlreadyLiveOnThisProcessIsRefreshed()
    {
        Assert.Single(Run(new HarnessSessions(Reg(Session, HarnessSessions.Pid))).Registered);
    }

    [Fact]
    public void AnotherSessionLiveElsewhereDoesNotRefuse()
    {
        var sessions = new HarnessSessions(Reg("someone-else", 5000));
        sessions.Starts[5000] = HarnessSessions.Start;

        Assert.Single(Run(sessions).Registered);
    }

    internal sealed class NoText : IDirectiveText
    {
        public string? DesignRoutingBlock(JsonObject resolved) => null;

        public string? ContractBlock(string role, JsonObject? resolved, bool peerRoute = false) => null;

        public string BuildDirective(JsonObject fullResolved, string? sessionId, string? hierarchyDirectory = null, string? model = null, bool subOrchestrator = false) => string.Empty;

        public string BuildNudge(JsonObject resolved) => string.Empty;

        public string BuildRoleSessionNotice(string role, string? agentType, JsonObject? resolved = null) => string.Empty;

        public string StatusReport(string? cwd) => string.Empty;
    }

    // ---------------------------------------------------------------- the checklist block

    private static string PlanRow(string item, string ev, string? extra = null) =>
        $"{{\"v\":1,\"ts\":\"2026-10-11T11:00:00Z\",\"team\":\"tm\",\"stream\":\"_run\",\"item\":\"{item}\",\"event\":\"{ev}\"{extra},\"by\":\"o\",\"session\":null}}";

    private static (HooksHostTests.Rig Rig, AdvisoryServices Advisory) Orchestrating(int items, bool registered = true)
    {
        var rig = new HooksHostTests.Rig();
        var dir = rig.Sandbox.Root + "/proj/.claude/ah2";
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/agent-hierarchy.json", """{"version":1,"alwaysOrchestrator":true}""");
        var progress = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj") + "/progress/tm";
        _ = Directory.CreateDirectory(progress);
        File.WriteAllText(
            progress + "/_run.plan.jsonl",
            string.Join('\n', Enumerable.Range(1, items).SelectMany(n => new[] { PlanRow("item-" + n, "add", $",\"title\":\"A reasonably long title for item number {n} to fill the block\""), PlanRow("item-" + n, "next") })) + "\n");
        Ah.TestKit.TestImport.Planted(rig.Services.Environment.AhHome, [progress + "/_run.plan.jsonl"]);
        var mailbox = BindingAttestationTests.MailboxFor(rig);
        if (registered)
        {
            var box = rig.Sandbox.Root + "/box";
            _ = Directory.CreateDirectory(box);
            var file = mailbox.RegistrationFile(Session);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, JsonText.ToUtf8(new JsonObject { ["session_id"] = Session, ["address"] = "orchestrator@tm", ["pid"] = Environment.ProcessId, ["boxes"] = new JsonObject { ["orchestrator@tm"] = box } }, 0));
        }

        var callers = new BindingAttestationTests.Callers(BindingAttestationTests.Orchestrator, _ => { });
        return (rig, new AdvisoryServices(mailbox, null!, callers, null!, null!, new NoText(), null!, null!, new BindingAttestationTests.Daemon(), null!, null!));
    }

    private static string Start(HooksHostTests.Rig rig, AdvisoryServices advisory, string source)
    {
        var services = rig.Services with { Advisory = advisory, Sessions = new HarnessSessions(), Process = rig.Services.Process with { ParentPid = HarnessSessions.Pid } };
        var e = new JsonObject { ["hook_event_name"] = "SessionStart", ["session_id"] = Session, ["cwd"] = rig.Sandbox.Root + "/proj", ["source"] = source };
        return Encoding.UTF8.GetString(rig.Run("sessionstart", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services).Stdout.Span);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("resume")]
    [InlineData("compact")]
    public void AnOrchestratingSessionIsHandedItsChecklistOnStartResumeAndCompact(string source)
    {
        var (rig, advisory) = Orchestrating(3);

        var output = Start(rig, advisory, source);

        Assert.Contains("ah: your checklist", output, StringComparison.Ordinal);
        Assert.Contains("item-1", output, StringComparison.Ordinal);
        Assert.Contains("item-3", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterCompactionTheBlockListsAllFourItemsOfTheReplayIncludingTheLooseEndWaitingOnTheOrchestrator()
    {
        var (rig, advisory) = Orchestrating(0);
        var log = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj") + "/progress/tm/_run.plan.jsonl";
        Ah.TestKit.TestProgressLogs.WriteFile(
            rig.Services.Environment.AhHome,
            log,
            string.Join(
                '\n',
                PlanRow("loose", "add", ",\"title\":\"Answer implementor-37\""), PlanRow("loose", "assign", ",\"holder\":\"implementor-37\""), PlanRow("loose", "wait-orch", ",\"note\":\"asked\""),
                PlanRow("q1", "add", ",\"title\":\"Queued one\""), PlanRow("q1", "next"),
                PlanRow("q2", "add", ",\"title\":\"Queued two\""), PlanRow("q2", "next"),
                PlanRow("q3", "add", ",\"title\":\"Blocked three\""), PlanRow("q3", "block", ",\"note\":\"waiting on a fix\"")) + "\n");

        var output = Start(rig, advisory, "compact");

        foreach (var id in new[] { "loose", "q1", "q2", "q3" })
        {
            Assert.Contains(id, output, StringComparison.Ordinal);
        }

        Assert.Contains("waiting-orch", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionThatOwnsNoTeamIsHandedNoChecklist()
    {
        var (rig, advisory) = Orchestrating(3, registered: false);

        Assert.DoesNotContain("ah: your checklist", Start(rig, advisory, "compact"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyChecklistAddsNoBlock()
    {
        var (rig, advisory) = Orchestrating(0);

        Assert.DoesNotContain("ah: your checklist", Start(rig, advisory, "compact"), StringComparison.Ordinal);
    }

    [Fact]
    public void ALongChecklistIsCutAtFifteenHundredBytesWithTheCountOfWhatIsLeft()
    {
        var (rig, advisory) = Orchestrating(60);

        var block = Ah.Hooks.Lifecycle.ChecklistBlock.Of(rig.Services, advisory, Session, rig.Sandbox.Root + "/proj");

        Assert.NotNull(block);
        Assert.InRange(Encoding.UTF8.GetByteCount(block), 1420, 1500);
        Assert.Matches("… [0-9]+ more: run `.*checklist show`$", block);
        Assert.Contains("item-1", block, StringComparison.Ordinal);
    }

    [Fact]
    public void AChecklistThatFitsIsShownWholeWithNoCutLine()
    {
        var (rig, advisory) = Orchestrating(2);

        var block = Ah.Hooks.Lifecycle.ChecklistBlock.Of(rig.Services, advisory, Session, rig.Sandbox.Root + "/proj");

        Assert.DoesNotContain("more: run", block, StringComparison.Ordinal);
    }
}
