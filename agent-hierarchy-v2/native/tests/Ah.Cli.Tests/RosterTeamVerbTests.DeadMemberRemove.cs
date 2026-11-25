using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>ah2-592: ahd removes a member proven dead through dismiss's close, after reading its work, and says so in the member-down mail.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string DeadBrief = "20261016-010000-dead";

    /// <summary>The implementor on tmux pane %1, its registration proven dead, with a request to it linked to checklist item _run/dead-work; <paramref name="listed"/> is what tmux lists after any close.</summary>
    private void ArrangeProvenDeadImplementor(string listed = "")
    {
        _branch = "ah/feature";
        ArrangeImplementor(live: false);
        var git = _runner.Output;
        _runner.Output = argv => argv.Contains("list-panes") ? listed : git?.Invoke(argv) ?? string.Empty;
        _registrations = new() { [Impl + "@proj"] = new MemberRegistrationLiveness(ImplSession, false, "registration pid 4242 is another process now", ProvenDead: true) };
        _ = AskRelease(DeadBrief, slug: "work");
        var seeded = false;
        _seedGraph = graph =>
        {
            if (seeded)
            {
                return;
            }

            seeded = true;
            Assert.Null(graph.Hierarchy.Progress.RecordChecklist(Hierarchy, "proj", "_run", "orchestrator@proj", [new PlanInput("dead-work", "add", "Do dead work")]).Refused);
            Assert.Null(HierarchyModule.ChecklistOnDispatch(graph.Hierarchy.Progress, Hierarchy, "proj", "_run/dead-work", "implementor", DeadBrief, "orchestrator@proj", null));
        };
    }

    /// <summary>One stall sweep over team proj whose implementor reads dead, removing it through the real removal; the mails it sent.</summary>
    private List<WatchMail> SweepDeadImplementor(int sweeps = 1, int failingMails = 0)
    {
        var removal = CommandsModule.CreateMemberRemoval(ComposeServices());
        var host = new DeadMemberHost(new StallTeam("proj", Hierarchy, Project, "orchestrator@proj", "sess-orch"), new StallMember(Impl, "implementor", "%1", false), ImplSession, removal) { Failing = failingMails };
        var jobs = new SweepJobs();
        WatchModule.RegisterStallWatch(jobs, host, StallSettings.Defaults with { Mode = StallMode.On, Down = TimeSpan.Zero, WarmUp = TimeSpan.Zero }, () => DateTimeOffset.UtcNow);
        for (var i = 0; i < sweeps; i++)
        {
            jobs.Run();
        }

        return host.Mails;
    }

    /// <summary>A request the implementor itself sent, which the close of its row does not abandon.</summary>
    private const string SentBrief = "20261016-010000-sent";

    private void SentByImplementor()
    {
        var path = MsgPath(SentBrief, "reviewer", "check", "request");
        _ = TestMsgStore.Plant(
            _sandbox.AhHome,
            path,
            $"---\nid: {SentBrief}\ntype: request\nto: reviewer\nfrom: implementor\nslug: check\nparent: null\nreason: null\neta: null\nto_name: proj-reviewer\nfrom_name: {Impl}\nteam: proj\ncreated: 2026-10-16T01:00:00-04:00\n---\n\n## [0] tldr\nCheck.\n");
    }

    private bool ImplementorRowStays() =>
        ComposeServices().Roster.ReadTeamRecord(Hierarchy, "proj").Member("members") is { IsArray: true } rows
        && rows.AsArray().Any(m => m.Member("name")?.AsString() == Impl);

    [Fact]
    public void ASweepRemovesAProvenDeadMemberClosesItsPaneOnceAbandonsItsBriefToTheOrchestratorAndMailsTheCapture()
    {
        ArrangeProvenDeadImplementor();
        SentByImplementor();

        var mail = Assert.Single(SweepDeadImplementor());

        Assert.False(ImplementorRowStays());
        Assert.Contains($"1 open request(s) to it ({DeadBrief}), now abandoned", mail.Text, StringComparison.Ordinal);
        Assert.Contains($"1 request(s) still open ({SentBrief})", mail.Text, StringComparison.Ordinal);
        Assert.Contains(ComposeServices().Hierarchy.Messages.OpenExchanges(Hierarchy, "proj"), e => e.Member("id")?.AsString() == SentBrief);
        Assert.Equal(1, _runner.Calls.Count(argv => argv.Contains("kill-pane") && argv.Contains("%1")));
        Assert.DoesNotContain(ComposeServices().Hierarchy.Messages.OpenExchanges(Hierarchy, "proj"), e => e.Member("id")?.AsString() == DeadBrief);
        var trail = Roster("checklist", "show", "_run/dead-work", "--trail", "--team", "proj");
        Assert.Equal("waiting-orch", JsonText.Parse(trail.Stdout).Member("state")?.AsString());
        Assert.Equal("member-down", mail.Event);
        Assert.True(mail.Data.Get("removed") is { IsTruthy: true }, mail.Data.ToString());
        var capture = mail.Data.Get("capture")!.Value;
        Assert.Equal(Impl, capture.Member("name")?.AsString());
        Assert.Equal([DeadBrief, SentBrief], capture.Member("open_requests")!.Value.AsArray().Select(static id => id.AsString()).Order(StringComparer.Ordinal));
        Assert.Contains("removed " + Impl, mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ah roster resume", mail.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ARemovedMembersMailThatFailsIsSentOnceByALaterSweepWithTheCapture(int failing)
    {
        ArrangeProvenDeadImplementor();

        var mails = SweepDeadImplementor(sweeps: 3, failingMails: failing);

        var mail = Assert.Single(mails);
        Assert.False(ImplementorRowStays());
        Assert.True(mail.Data.Get("removed") is { IsTruthy: true }, mail.Data.ToString());
        Assert.Equal(Impl, mail.Data.Get("capture")!.Value.Member("name")?.AsString());
    }

    [Fact]
    public void ACloseThatThrowsAfterTheRowIsGoneStillMailsRemoved()
    {
        ArrangeProvenDeadImplementor();
        _wrapStateCalls = inner =>
        {
            var proxy = System.Reflection.DispatchProxy.Create<Ah.Hierarchy.StateCalls.IHierarchyStateCalls, FailingUnassign>();
            ((FailingUnassign)(object)proxy).Inner = inner;
            return proxy;
        };

        var mail = Assert.Single(SweepDeadImplementor());

        Assert.False(ImplementorRowStays());
        Assert.True(mail.Data.Get("removed") is { IsTruthy: true }, mail.Data.ToString());
    }

    /// <summary>State calls whose grants <c>unassign</c>, run after the row is dropped, finds the store unavailable.</summary>
    public class FailingUnassign : System.Reflection.DispatchProxy
    {
        internal Ah.Hierarchy.StateCalls.IHierarchyStateCalls? Inner { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "Grants" && args is [string op, ..] && op == "unassign")
            {
                throw new StoreUnavailableException("the store is unavailable");
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is { } inner)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
        }
    }

    [Fact]
    public void ASweepWhosePaneCloseIsNotVerifiedKeepsTheRowAndMailsWithoutRemoved()
    {
        ArrangeProvenDeadImplementor(listed: "%1\n");

        var mail = Assert.Single(SweepDeadImplementor());

        Assert.True(ImplementorRowStays());
        Assert.Null(mail.Data.Get("removed"));
        Assert.Null(mail.Data.Get("capture"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHeldTeamsNotRunningMemberIsNotRemovedAndAnUnheldOneIs(bool held)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ArrangeProvenDeadImplementor();
        var registered = DateTimeOffset.UtcNow.AddHours(-1);
        _registrations![Impl + "@proj"] = _registrations[Impl + "@proj"] with { At = registered };
        if (held)
        {
            PutHold(ComposeServices().Roster.HoldPath(Hierarchy, "proj"), "{\"kind\":\"quit\",\"phase\":\"held\",\"since\":\"2026-10-24T00:00:00Z\"}");
        }

        var graph = JobGraph();
        graph.Hierarchy.Core.Liveness = new ScriptedRows(_registrations, _registrationsLater, PlatformModule.CreateIo(null).Processes);
        var spy = new RemovalSpy();
        var host = new StallHost(graph, _sandbox.AhHome, () => true, () => null, memberRemoval: spy);
        var dead = new StallJudgement(new MemberVerdict(Verdict.Dead, "registration pid 4242 is another process now"), ImplSession, registered);

        var capture = host.RemoveDead(new StallTeam("proj", Hierarchy, Project, "orchestrator@proj", "sess-orch"), new StallMember(Impl, "implementor", "%1", false), dead, DateTimeOffset.UtcNow);

        Assert.Equal(held, capture is null);
        Assert.Equal(held ? 0 : 1, spy.Calls);
        Assert.True(ImplementorRowStays());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("kill-pane"));
    }

    [Fact]
    public void TheCaptureIsJournaledAsMemberRemoveBeforeTheCloseAndACloseThatFailsAfterItIsJournaledToo()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ArrangeProvenDeadImplementor();
        var registered = DateTimeOffset.UtcNow.AddHours(-1);
        _registrations![Impl + "@proj"] = _registrations[Impl + "@proj"] with { At = registered };
        var graph = JobGraph();
        graph.Hierarchy.Core.Liveness = new ScriptedRows(_registrations, _registrationsLater, PlatformModule.CreateIo(null).Processes);
        var host = new StallHost(graph, _sandbox.AhHome, () => true, () => null, memberRemoval: new CaptureThenFailedClose());
        var dead = new StallJudgement(new MemberVerdict(Verdict.Dead, "registration pid 4242 is another process now"), ImplSession, registered);

        var removal = host.RemoveDead(new StallTeam("proj", Hierarchy, Project, "orchestrator@proj", "sess-orch"), new StallMember(Impl, "implementor", "%1", false), dead, DateTimeOffset.UtcNow);

        Assert.Null(removal);
        var rows = StallStore.JournalRows(_sandbox.AhHome).Select(static r => JsonText.Parse(r).AsObject()).Where(static r => r["kind"].AsString().StartsWith("member-remove", StringComparison.Ordinal)).ToList();
        Assert.Equal(["member-remove", "member-remove-failed"], rows.Select(static r => r["kind"].AsString()));
        Assert.Equal(Impl, rows[0]["subject"].AsString());
        Assert.Equal("proj", rows[0]["team"].AsString());
        Assert.Equal(Impl, rows[0]["capture"].Member("name")?.AsString());
        Assert.Equal("the close was not verified", rows[1]["why"].AsString());
    }

    /// <summary>A removal that hands over its capture, then reports the close as failed with the row still there.</summary>
    private sealed class CaptureThenFailedClose : IMemberRemoval
    {
        public MemberRemovalResult Remove(string hierarchyDir, string projectRoot, string team, string member, Action<JsonObject>? captured = null)
        {
            var capture = new JsonObject { ["name"] = member };
            captured?.Invoke(capture);
            return new MemberRemovalResult(false, capture, "the close was not verified");
        }
    }

    private sealed class RemovalSpy : IMemberRemoval
    {
        public int Calls { get; private set; }

        public MemberRemovalResult Remove(string hierarchyDir, string projectRoot, string team, string member, Action<JsonObject>? captured = null)
        {
            Calls++;
            return new MemberRemovalResult(true, new JsonObject(), null);
        }
    }

    private sealed class SweepJobs : Ah.Abstractions.IJobRegistry
    {
        private readonly List<Func<CancellationToken, Task>> _handlers = [];

        public void Add(string name, TimeSpan period, Func<CancellationToken, Task> handler) => _handlers.Add(handler);

        public void Run() => Assert.Single(_handlers)(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>A stall host with one team and one member that reads dead, whose removal is the real one.</summary>
    private sealed class DeadMemberHost(StallTeam team, StallMember member, string session, IMemberRemoval removal) : IStallHost
    {
        public List<WatchMail> Mails { get; } = [];

        public bool Owner() => true;

        public IReadOnlyList<StallTeam> Teams() => [team];

        private bool _removed;

        /// <summary>The member until it is removed: its row is gone then, as in the team file.</summary>
        public IReadOnlyList<StallMember> Members(StallTeam t) => _removed ? [] : [member];

        public StallJudgement Judge(StallTeam t, StallMember m) =>
            new(new MemberVerdict(Verdict.Dead, "registration pid 4242 is another process now"), session, DateTimeOffset.UtcNow.AddHours(-1));

        public IReadOnlyList<string> OpenWork(StallTeam t, StallMember m) => [];

        /// <summary>How many mails throw before one is taken.</summary>
        public int Failing { get; set; }

        public void Mail(WatchMail mail)
        {
            if (Failing > 0)
            {
                Failing--;
                throw new InvalidOperationException("the mailbox refused it");
            }

            Mails.Add(mail);
        }

        public void Journal(JsonObject row)
        {
        }

        public string? ReportedSession(string key) => null;

        public void SetReportedSession(string key, string? session)
        {
        }

        public void BeginSweep()
        {
        }

        public void ForgetStaleReported(TimeSpan unseenFor)
        {
        }

        public StallRemoval? RemoveDead(StallTeam t, StallMember m, StallJudgement judged, DateTimeOffset decidedAt)
        {
            if (removal.Remove(t.HierarchyDir, t.Cwd, t.Name, m.Name) is not { Removed: true, Capture: { } capture } removed)
            {
                return null;
            }

            _removed = true;
            return new StallRemoval(capture, removed.Abandoned);
        }
    }
}
