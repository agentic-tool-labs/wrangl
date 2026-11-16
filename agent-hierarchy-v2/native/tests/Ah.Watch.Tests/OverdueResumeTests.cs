using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.TestKit;

namespace Ah.Watch.Tests;

/// <summary>An idle member whose brief is overdue gets a resume entry in its own box when the Orchestrator is told, at most twice.</summary>
public sealed class OverdueResumeTests
{
    private const string RequestId = "20260301-114000-ab12";

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-03-01T12:00:00Z", CultureInfo.InvariantCulture);

    private sealed class Rig
    {
        internal FakeHost Host { get; } = new() { MemberPicksUpResumes = false };

        internal FakeClock Clock { get; } = new(Start);

        internal WatchTick Tick()
        {
            var files = new NullFiles();
            var env = new MapEnv();
            return new WatchTick(new WatchDependencies(
                Host, env, Clock, files, HierarchyModule.CreateRecovery(files, env, Clock), WatchModule.CreateStatusDirs(), () => true) { SleepGapMs = SleepGapMs });
        }

        internal double SleepGapMs { get; set; } = double.PositiveInfinity;

        internal async Task PassAsync(WatchTick tick, int polls = 2, int stepMs = 15_000)
        {
            for (var i = 0; i < polls; i++)
            {
                await tick.RunAsync(CancellationToken.None);
                Clock.UtcNow = Clock.UtcNow.AddMilliseconds(stepMs);
            }
        }

        /// <summary>Polls, a few minutes apart, until a mail named <paramref name="eventName"/> has gone out.</summary>
        internal async Task PassUntilAsync(WatchTick tick, string eventName)
        {
            for (var i = 0; i < 200 && !Host.Mails.Any(m => m.Event == eventName); i++)
            {
                await PassAsync(tick, polls: 1, stepMs: 120_000);
            }
        }
    }

    private static string Iso(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DispatchCandidate Brief(bool open = true) =>
        new(RequestId, "implementor", "the-slug", "impl@beta", "impl", "small", Iso(Start.AddMinutes(-20)), open, "/r.md", false, string.Empty, "/private/tmp/ahprobe.h") { Team = "beta" };

    /// <summary>The brief sent longer ago than the busy ceiling, so a busy member no longer holds its check-in.</summary>
    private static DispatchCandidate PastTheCeiling(DispatchCandidate c) => c with { Created = Iso(Start.AddMinutes(-121)) };

    private static void ReportRead(Rig rig) =>
        rig.Host.SeededPeers.Add(new JsonObject { ["type"] = "report-acked", ["session_id"] = "s1", ["request_id"] = RequestId });

    [Fact]
    public async Task ARunningLaneWhoseReportWasReadStillGetsItsStallEvent()
    {
        var rig = WithBrief();
        rig.Host.Candidates["s1"] = [Brief() with { LaneCharter = true }];
        rig.Host.Activity["m1"] = Compacted(Start.AddMinutes(-40), Start.AddMinutes(-30));
        ReportRead(rig);

        await rig.PassAsync(rig.Tick());

        _ = Assert.Single(StallMails(rig));
        Assert.NotEmpty(Entries(rig));
    }

    [Fact]
    public async Task AnIdleLaneWhoseReportWasReadIsStillCheckedOn()
    {
        var rig = WithBrief();
        rig.Host.Candidates["s1"] = [Brief() with { LaneCharter = true }];
        ReportRead(rig);

        await rig.PassAsync(rig.Tick());

        Assert.Contains(rig.Host.Mails, static m => m.Event == "check-in-due");
    }

    [Fact]
    public async Task AMemberThatIsNoLaneAndWhoseReportWasReadGetsNoStallEvent()
    {
        var rig = WithBrief();
        rig.Host.Activity["m1"] = Compacted(Start.AddMinutes(-40), Start.AddMinutes(-30));
        ReportRead(rig);

        await rig.PassAsync(rig.Tick());

        Assert.Empty(StallMails(rig));
        Assert.Empty(Entries(rig));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheLaneCharterMarkSurvivesTheCandidatesJson(bool charter) =>
        Assert.Equal(charter, DispatchCandidateJson.Read(DispatchCandidateJson.ToJson(Brief() with { LaneCharter = charter })).LaneCharter);

    /// <summary>A session with member <c>impl</c> (session <c>m1</c>) in team <c>beta</c>, in <paramref name="activity"/>, holding an open brief to it.</summary>
    private static Rig WithBrief(string? activity = "idle", string? team = "beta", bool open = true)
    {
        var rig = new Rig();
        rig.Host.Registrations.Add(new WatchRegistration("s1", "claude", 100, "2026-03-01T11:00:00.000Z", [new WatchBox("orchestrator@beta", "/boxes/orchestrator@beta")], "/private/tmp/ahprobe.h"));
        MemberView[] members = [new("impl", new JsonObject { ["role"] = "implementor" }, true, activity, null, null, "m1")];
        rig.Host.Owned["s1"] = [new OwnedTeam(team, new JsonObject(), members)];
        rig.Host.Candidates["s1"] = [Brief(open)];
        if (activity is not null)
        {
            rig.Host.Activity["m1"] = new JsonObject { ["activity"] = activity, ["at"] = Iso(activity == "working" ? Start.AddDays(-1) : Start) };
        }

        rig.Host.FeedBackGates = true;
        rig.Host.Stamp = () => Iso(rig.Clock.UtcNow);
        return rig;
    }

    private static IEnumerable<WatchMail> Entries(Rig rig) => rig.Host.Mails.Where(static m => m.Event == "resume");

    private static JsonObject ChainDownRow(string session, string state) =>
        new() { ["type"] = "chain-down", ["state"] = state, ["session_id"] = session, ["member"] = "impl", ["team"] = "beta" };

    [Fact]
    public async Task AnIdleMemberWithAnActiveChainDownRowGetsNoResumeAndTheCheckInSaysWhy()
    {
        var rig = WithBrief();
        rig.Host.SeededPeers.Add(ChainDownRow("m1", "active"));

        await rig.PassAsync(rig.Tick());

        Assert.Empty(Entries(rig));
        var checkIn = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
        Assert.EndsWith("; not resumed: chain-down", checkIn.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("m2", "active")]
    [InlineData("m1", "resolved")]
    [InlineData("m1", "cleared")]
    public async Task AChainDownRowOfAnotherSessionOrNoLongerActiveStillResumes(string session, string state)
    {
        var rig = WithBrief();
        rig.Host.SeededPeers.Add(ChainDownRow(session, state));

        await rig.PassAsync(rig.Tick());

        _ = Assert.Single(Entries(rig));
    }

    private static bool Told(Rig rig, string eventName) => rig.Host.Mails.Any(m => m.Event == eventName);

    [Theory]
    [InlineData(120, true)]
    [InlineData(30, false)]
    public async Task AStintThatStartedAndEndedBetweenTicksArmsAPlainSend(int stintSeconds, bool armed)
    {
        // idle now; the record keeps the finished stint's working_since, and its end is the idle transition
        var rig = WithBrief();
        var start = Start.AddMinutes(-5);
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(start.AddSeconds(stintSeconds)), ["state_since"] = Iso(start.AddSeconds(stintSeconds)), ["working_since"] = Iso(start), ["working_at"] = Iso(start.AddSeconds(stintSeconds)) };
        rig.Host.Candidates["s1"] = [Brief() with { PlainSend = true, Created = Iso(start.AddMinutes(-1)) }];

        await rig.PassAsync(rig.Tick());

        Assert.Equal(armed, rig.Host.AppendedPeers.Any(static r => r.Str("type") == Liveness.PlainSendArmedRow));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public async Task AnUnarmedPlainSendRaisesNoCheckInOrSilentAlarm(bool plainSend, bool workedAMinuteSinceTheSend, bool told)
    {
        // working, but the record shows no stint since the send: a plain send stays unarmed; past the busy ceiling any other brief alarms.
        // A member already working when the send landed (mid-turn) whose record shows a minute of work since it arms from the send.
        var rig = WithBrief("working");
        if (workedAMinuteSinceTheSend)
        {
            rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "working", ["at"] = Iso(Start.AddDays(-1)), ["working_since"] = Iso(Start.AddDays(-1)), ["working_at"] = Iso(Start) };
        }

        rig.Host.Candidates["s1"] = [PastTheCeiling(Brief()) with { PlainSend = plainSend }];

        await rig.PassAsync(rig.Tick(), polls: 8, stepMs: 120_000);

        Assert.Equal(told, Told(rig, "check-in-due") || Told(rig, "peer-silent"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AnIdleMemberOwesAReportOnlyForABriefItHasRead(bool unread, bool told)
    {
        var rig = WithBrief();
        rig.Host.Candidates["s1"] = [Brief() with { BriefUnread = unread }];

        await rig.PassAsync(rig.Tick(), polls: 3, stepMs: 40_000);

        Assert.Equal(told, rig.Host.Mails.Any(static m => m.Event == "idle-without-report"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AnIdleLaneLeadWaitingOnItsChildTeamIsNotIdleWithoutReport(bool delegating, bool told)
    {
        var rig = WithBrief();
        rig.Host.Candidates["s1"] = [Brief() with { LaneDelegating = delegating }];

        await rig.PassAsync(rig.Tick(), polls: 3, stepMs: 40_000);

        Assert.Equal(told, rig.Host.Mails.Any(static m => m.Event == "idle-without-report"));
    }

    [Fact]
    public async Task AnUnsentBriefToAMemberGetsACheckInSayingToSendItAndNoSilentAlarm()
    {
        var rig = WithBrief();
        rig.Host.Candidates["s1"] = [Brief() with { Undelivered = true }];

        await rig.PassUntilAsync(rig.Tick(), "peer-silent");

        Assert.Contains("No delivery recorded", rig.Host.Mails.First(static m => m.Event == "check-in-due").Text, StringComparison.Ordinal);
        Assert.False(Told(rig, "peer-silent"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnsentBriefToNoLiveMemberRaisesNothing(bool memberWithoutSession)
    {
        var rig = WithBrief();
        MemberView[] members = memberWithoutSession ? [new("impl", new JsonObject { ["role"] = "implementor" }, false, null, null, null, null)] : [];
        rig.Host.Owned["s1"] = [new OwnedTeam("beta", new JsonObject(), members)];
        rig.Host.Candidates["s1"] = [Brief() with { Undelivered = true }];

        await rig.PassUntilAsync(rig.Tick(), "peer-silent");

        Assert.False(Told(rig, "check-in-due"));
        Assert.False(Told(rig, "peer-silent"));
    }

    [Fact]
    public async Task ADeliveredBriefToNoLiveMemberStillAlarms()
    {
        var rig = WithBrief();
        rig.Host.Owned["s1"] = [new OwnedTeam("beta", new JsonObject(), [])];

        await rig.PassUntilAsync(rig.Tick(), "peer-silent");

        Assert.True(Told(rig, "check-in-due"));
        Assert.True(Told(rig, "peer-silent"));
    }

    /// <summary>Polls every two minutes for hours past the cap, refreshing the member's working write when <paramref name="writing"/> says so.</summary>
    private static async Task PassHoursAsync(Rig rig, WatchTick tick, bool writing)
    {
        for (var i = 0; i < 120; i++)
        {
            if (writing)
            {
                rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "working", ["at"] = Iso(Start), ["working_at"] = Iso(rig.Clock.UtcNow) };
            }

            await rig.PassAsync(tick, polls: 1, stepMs: 120_000);
        }
    }

    [Fact]
    public async Task AWedgedMemberIsCalledSilentHoweverOftenTheLeadLooksAtIt()
    {
        var rig = WithBrief("working");
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "working", ["at"] = Iso(Start), ["working_at"] = Iso(Start) };
        _ = rig.Host.BusyPeers.Add("impl");
        var tick = rig.Tick();

        async Task LookAndPass(int polls)
        {
            for (var i = 0; i < polls; i++)
            {
                rig.Host.BusySeen["impl"] = rig.Clock.UtcNow.ToUnixTimeMilliseconds();
                await rig.PassAsync(tick, polls: 1, stepMs: 120_000);
            }
        }

        // its check-in comes at the busy ceiling after the dispatch; a busy look after it holds the silent step (b278)
        await LookAndPass(56);
        Assert.True(Told(rig, "check-in-due"));
        Assert.False(Told(rig, "peer-silent"));

        // until the member has worked the busy ceiling itself
        await LookAndPass(6);
        Assert.Contains("(busy for ", Assert.Single(rig.Host.Mails, static m => m.Event == "peer-silent").Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeAMemberIdleAfterTheBusyLookIsStillCalledSilent()
    {
        var rig = WithBrief("working");
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "working", ["at"] = Iso(Start), ["working_at"] = Iso(Start) };
        _ = rig.Host.BusyPeers.Add("impl");
        var tick = rig.Tick();
        for (var i = 0; i < 60 && !Told(rig, "check-in-due"); i++)
        {
            rig.Host.BusySeen["impl"] = rig.Clock.UtcNow.ToUnixTimeMilliseconds();
            await rig.PassAsync(tick, polls: 1, stepMs: 120_000);
        }

        Assert.True(Told(rig, "check-in-due"));
        rig.Host.BusySeen["impl"] = rig.Clock.UtcNow.ToUnixTimeMilliseconds();
        var idleAt = Iso(rig.Clock.UtcNow.AddSeconds(1));
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = idleAt, ["state_since"] = idleAt, ["working_at"] = Iso(Start) };
        await rig.PassAsync(tick, polls: 3, stepMs: 120_000);

        Assert.True(Told(rig, "peer-silent"));
    }

    [Fact]
    public async Task AMemberThatWentIdleAfterTheLookIsNotHeldByIt()
    {
        var rig = WithBrief("idle");
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(Start), ["state_since"] = Iso(Start) };
        _ = rig.Host.BusyPeers.Add("impl");
        rig.Host.BusySeen["impl"] = Start.AddMinutes(-1).ToUnixTimeMilliseconds();

        await rig.PassUntilAsync(rig.Tick(), "peer-silent");

        Assert.True(Told(rig, "peer-silent"));
        Assert.NotEmpty(Entries(rig));
        Assert.DoesNotContain("(busy for ", rig.Host.Mails.First(static m => m.Event == "peer-silent").Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeALookNewerThanTheIdleRecordHoldsOnlyUpToTheCap()
    {
        var rig = WithBrief("idle");
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(Start), ["state_since"] = Iso(Start) };
        _ = rig.Host.BusyPeers.Add("impl");
        var tick = rig.Tick();

        async Task LookAndPass(int polls)
        {
            for (var i = 0; i < polls; i++)
            {
                rig.Host.BusySeen["impl"] = rig.Clock.UtcNow.ToUnixTimeMilliseconds();
                await rig.PassAsync(tick, polls: 1, stepMs: 120_000);
            }
        }

        // a busy look after the check-in holds the silent step until the member has been busy the ceiling (b278)
        await LookAndPass(59);
        Assert.True(Told(rig, "check-in-due"));
        Assert.False(Told(rig, "peer-silent"));

        await LookAndPass(3);
        Assert.True(Told(rig, "peer-silent"));
    }

    private static Rig PastItsCheckIn(Func<DispatchCandidate, DispatchCandidate> shape)
    {
        var rig = WithBrief("working");
        rig.Host.Candidates["s1"] = [shape(Brief())];
        rig.Host.SeededGates.Add(new JsonObject { ["type"] = "liveness-nudge", ["session_id"] = "s1", ["request_id"] = RequestId, ["ts"] = Iso(Start.AddMinutes(-6)) });
        return rig;
    }

    // b504: a ListAgents look that showed the peer busy, as PostToolUseRoster writes it
    private static JsonObject BusyLook(DateTimeOffset at) =>
        new() { ["type"] = "liveness-checked", ["session_id"] = "s1", ["request_id"] = RequestId, ["peer"] = "impl", ["busy"] = true, ["ts"] = Iso(at) };

    [Fact]
    public async Task ABusyLookAfterTheCheckInHoldsTheSilentStep()
    {
        var rig = PastItsCheckIn(static c => c);
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(Start), ["state_since"] = Iso(Start) };
        rig.Host.SeededGates.Add(BusyLook(Start.AddMinutes(-5)));

        await rig.PassAsync(rig.Tick(), polls: 5, stepMs: 120_000);

        Assert.False(Told(rig, "peer-silent"));
    }

    [Fact]
    public async Task NegativeAPeerLookedBusyPastTheCeilingIsCalledSilentOnce()
    {
        var rig = PastItsCheckIn(static c => c);
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(Start), ["state_since"] = Iso(Start) };
        var tick = rig.Tick();

        // the brief went out 20 min before Start: the busy ceiling falls at Start + 100 min
        async Task LookAndPassUntil(DateTimeOffset until)
        {
            while (rig.Clock.UtcNow < until)
            {
                rig.Host.SeededGates.Add(BusyLook(rig.Clock.UtcNow));
                await rig.PassAsync(tick, polls: 1, stepMs: 120_000);
            }
        }

        await LookAndPassUntil(Start.AddMinutes(98));
        Assert.False(Told(rig, "peer-silent"));

        await LookAndPassUntil(Start.AddMinutes(110));
        _ = Assert.Single(rig.Host.Mails, static m => m.Event == "peer-silent");
    }

    [Fact]
    public async Task NegativeABusyLookBeforeTheCheckInHoldsNothing()
    {
        var rig = PastItsCheckIn(static c => c);
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(Start), ["state_since"] = Iso(Start) };
        rig.Host.SeededGates.Add(BusyLook(Start.AddMinutes(-7)));

        await rig.PassAsync(rig.Tick(), polls: 5, stepMs: 120_000);

        _ = Assert.Single(rig.Host.Mails, static m => m.Event == "peer-silent");
    }

    [Fact]
    public async Task NegativeNoLookAfterTheCheckInIsStillCalledSilent()
    {
        var rig = PastItsCheckIn(static c => c);
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(Start), ["state_since"] = Iso(Start) };

        await rig.PassAsync(rig.Tick(), polls: 5, stepMs: 120_000);

        _ = Assert.Single(rig.Host.Mails, static m => m.Event == "peer-silent");
    }

    [Fact]
    public async Task NegativeABusyLookOnAnEarlierCheckInDoesNotHoldTheNextOne()
    {
        var rig = PastItsCheckIn(static c => c);
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(Start), ["state_since"] = Iso(Start) };
        rig.Host.SeededGates.Add(BusyLook(Start.AddMinutes(-5)));

        // the peer spoke after that look: the clock restarts and the next check-in is a new one
        rig.Host.SeededPeers.Add(new JsonObject { ["type"] = "heard", ["session_id"] = "s1", ["from"] = "impl@beta", ["ts"] = Iso(Start.AddMinutes(-4)) });

        await rig.PassUntilAsync(rig.Tick(), "peer-silent");

        Assert.True(Told(rig, "check-in-due"));
        _ = Assert.Single(rig.Host.Mails, static m => m.Event == "peer-silent");
    }

    [Fact]
    public async Task APausedLaneHoldsItsSilentStepWithoutWritingARow()
    {
        var rig = PastItsCheckIn(static c => c with { PausedLane = true });

        await rig.PassAsync(rig.Tick(), polls: 8, stepMs: 120_000);

        Assert.False(Told(rig, "peer-silent"));
        Assert.DoesNotContain(rig.Host.Gates, static g => g.Row.Str("type") == "silent-held");
    }

    [Fact]
    public async Task APausedMembersOverdueBriefRaisesNothingUntilResumePlusItsEta()
    {
        // b456: idle member, brief 20 min old, eta threshold 10 min
        var rig = WithBrief();
        rig.Host.Candidates["s1"] = [Brief() with { MemberPaused = true }];
        var tick = rig.Tick();

        await rig.PassAsync(tick, polls: 8, stepMs: 120_000);

        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event is "check-in-due" or "peer-silent" or "idle-without-report" or "stalled-after-compact" or "resume");

        var resumedAt = rig.Clock.UtcNow;
        rig.Host.Candidates["s1"] = [Brief() with { MemberResumedMs = resumedAt.ToUnixTimeMilliseconds() }];
        await rig.PassAsync(tick, polls: 4, stepMs: 120_000);

        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event is "check-in-due" or "peer-silent");

        await rig.PassUntilAsync(tick, "check-in-due");

        Assert.True(Told(rig, "check-in-due"));
        Assert.True(rig.Clock.UtcNow >= resumedAt.AddSeconds(600));
    }

    [Fact]
    public async Task NegativeAnUnpausedMembersOverdueBriefIsCheckedIn()
    {
        var rig = WithBrief();

        await rig.PassAsync(rig.Tick(), polls: 8, stepMs: 120_000);

        Assert.True(Told(rig, "check-in-due"));
    }

    [Fact]
    public async Task NegativeAResumeBeforeTheBriefWasSentDelaysNothing()
    {
        var rig = WithBrief();
        rig.Host.Candidates["s1"] = [Brief() with { MemberResumedMs = Start.AddHours(-1).ToUnixTimeMilliseconds() }];

        await rig.PassAsync(rig.Tick(), polls: 2, stepMs: 15_000);

        Assert.True(Told(rig, "check-in-due"));
    }

    [Fact]
    public async Task NegativeAnActiveLaneStillWritesItsRow()
    {
        var rig = PastItsCheckIn(static c => c with { ActiveLane = true });

        await rig.PassAsync(rig.Tick(), polls: 8, stepMs: 120_000);

        Assert.False(Told(rig, "peer-silent"));
        Assert.Contains(rig.Host.Gates, static g => g.Row.Str("type") == "silent-held");
    }

    [Fact]
    public async Task TheCapCountsFromTheClockBaseNotFromWhenTheStretchBegan()
    {
        var rig = WithBrief("working");
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "working", ["at"] = Iso(Start.AddDays(-1)), ["state_since"] = Iso(Start.AddDays(-1)), ["working_at"] = Iso(Start) };
        var tick = rig.Tick();

        await rig.PassAsync(tick, polls: 48, stepMs: 120_000);
        Assert.False(Told(rig, "peer-silent"));

        await rig.PassAsync(tick, polls: 4, stepMs: 120_000);
        Assert.True(Told(rig, "peer-silent"));
    }

    [Fact]
    public async Task AWorkingMemberOfAMediumBriefHoldsItsCheckInUntilTheBusyCeiling()
    {
        var rig = WithBrief("working");
        rig.Host.Candidates["s1"] = [Brief() with { Eta = "medium", Created = Iso(Start) }];
        var tick = rig.Tick();

        async Task WorkUntil(int minutes)
        {
            while (rig.Clock.UtcNow < Start.AddMinutes(minutes))
            {
                rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "working", ["at"] = Iso(Start), ["working_at"] = Iso(rig.Clock.UtcNow) };
                await rig.PassAsync(tick, polls: 1, stepMs: 60_000);
            }
        }

        await WorkUntil(36);
        Assert.False(Told(rig, "check-in-due"));

        await WorkUntil(122);
        _ = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
    }

    [Fact]
    public async Task AHeldSilentStepWritesItsRowAtMostOnceAMinute()
    {
        var rig = WithBrief("working");
        var tick = rig.Tick();
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "working", ["at"] = Iso(Start), ["working_at"] = Iso(rig.Clock.UtcNow) };
        await rig.PassUntilAsync(tick, "check-in-due");
        await PassHoursAsync(rig, tick, writing: true);
        var before = rig.Host.Gates.Count(static g => g.Row.Str("type") == "silent-held");
        Assert.True(before > 0);

        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "working", ["at"] = Iso(Start), ["working_at"] = Iso(rig.Clock.UtcNow.AddMinutes(1)) };
        await rig.PassAsync(tick, polls: 8, stepMs: 15_000);

        Assert.Equal(before + 2, rig.Host.Gates.Count(static g => g.Row.Str("type") == "silent-held"));
    }

    [Fact]
    public async Task NegativeASilentStepNotHeldWritesNoRow()
    {
        var rig = WithBrief();

        await rig.PassUntilAsync(rig.Tick(), "peer-silent");

        Assert.DoesNotContain(rig.Host.Gates, static g => g.Row.Str("type") == "silent-held");
    }

    [Fact]
    public async Task AMemberStillWritingIsNeverCalledSilentHoweverLongItRuns()
    {
        var rig = WithBrief("working");

        await PassHoursAsync(rig, rig.Tick(), writing: true);

        Assert.True(Told(rig, "check-in-due"));
        Assert.False(Told(rig, "peer-silent"));
    }

    [Fact]
    public async Task NegativeARecordFrozenInWorkingIsStillCalledSilentPastTheCap()
    {
        var rig = WithBrief("working");

        await PassHoursAsync(rig, rig.Tick(), writing: false);

        var silent = Assert.Single(rig.Host.Mails, static m => m.Event == "peer-silent");
        Assert.Contains("(busy for ", silent.Text, StringComparison.Ordinal);
    }

    private static Rig FreshBrief(string activity, double sleepGapMs)
    {
        var rig = WithBrief(activity);
        rig.SleepGapMs = sleepGapMs;
        rig.Host.Candidates["s1"] = [Brief() with { Created = Iso(rig.Clock.UtcNow) }];
        return rig;
    }

    [Fact]
    public async Task TimeAsleepDoesNotBringACheckInDue()
    {
        var rig = FreshBrief("idle", WatchDependencies.DefaultSleepGapMs);
        var tick = rig.Tick();

        await rig.PassAsync(tick, polls: 1, stepMs: 280 * 60_000);
        await rig.PassAsync(tick, polls: 1);

        Assert.False(Told(rig, "check-in-due"));
    }

    [Fact]
    public async Task NegativeAGapUnderTheSleepLineStillCounts()
    {
        var rig = FreshBrief("idle", WatchDependencies.DefaultSleepGapMs);
        var tick = rig.Tick();

        await rig.PassAsync(tick, polls: 3, stepMs: 299_000);
        await rig.PassAsync(tick);

        Assert.True(Told(rig, "check-in-due"));
    }

    [Fact]
    public async Task NegativeWithNoSleepLineTheSameGapBringsTheCheckInDue()
    {
        var rig = FreshBrief("idle", double.PositiveInfinity);
        var tick = rig.Tick();

        await rig.PassAsync(tick, polls: 1, stepMs: 280 * 60_000);
        await rig.PassAsync(tick, polls: 1);

        Assert.True(Told(rig, "check-in-due"));
    }

    [Fact]
    public async Task NegativeAwakeTimeAfterTheSleepBringsTheCheckInDue()
    {
        var rig = FreshBrief("idle", WatchDependencies.DefaultSleepGapMs);
        var tick = rig.Tick();

        await rig.PassAsync(tick, polls: 1, stepMs: 280 * 60_000);
        await rig.PassAsync(tick, polls: 12, stepMs: 60_000);

        Assert.True(Told(rig, "check-in-due"));
    }

    [Fact]
    public async Task AWorkingMemberIsNotCalledSilentWhenTheMachineWakes()
    {
        Assert.False(await SilentAfterASleepAsync(WatchDependencies.DefaultSleepGapMs));
    }

    [Fact]
    public async Task NegativeWithNoSleepLineTheSameWakeCallsItSilent()
    {
        Assert.True(await SilentAfterASleepAsync(double.PositiveInfinity));
    }

    /// <summary>A member writing up to the moment the machine sleeps 280 minutes, past its check-in, and not yet writing again on waking.</summary>
    private static async Task<bool> SilentAfterASleepAsync(double sleepGapMs)
    {
        var rig = WithBrief("working");
        rig.SleepGapMs = sleepGapMs;
        var tick = rig.Tick();
        await PassHoursAsync(rig, tick, writing: true);
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "working", ["at"] = Iso(Start), ["working_at"] = Iso(rig.Clock.UtcNow) };

        await rig.PassAsync(tick, polls: 1, stepMs: 280 * 60_000);
        await rig.PassAsync(tick);

        return Told(rig, "peer-silent");
    }

    [Fact]
    public void TheAwakeClockCutsOutOnlyGapsOverTheLine()
    {
        var clock = new AwakeClock(100);
        clock.Poll(1_000);
        clock.Poll(1_100);
        clock.Poll(5_100);

        Assert.Equal(200, clock.Of(5_300) - clock.Of(1_100));
        Assert.Equal(50, clock.Of(1_100) - clock.Of(1_050));
        Assert.Equal(0, clock.Of(3_000) - clock.Of(2_000));
    }

    [Fact]
    public async Task AnIdleMemberWithAnOverdueBriefGetsAResumeEntryAndTheCheckInSaysSo()
    {
        var rig = WithBrief();

        await rig.PassAsync(rig.Tick());

        var entry = Assert.Single(Entries(rig));
        Assert.Equal("impl@beta", entry.To);
        Assert.Equal("m1", entry.ToSession);
        Assert.StartsWith($"ah: your brief {RequestId} (the-slug) is overdue (eta small, sent ", entry.Text, StringComparison.Ordinal);
        Assert.EndsWith($". Continue it, or report its status with `ah msg send orchestrator@beta --kind report --ref {RequestId}`. Do not redo side effects.", entry.Text, StringComparison.Ordinal);
        var checkIn = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
        Assert.EndsWith("; resumed impl by mail (pasted if unread) (1/2)", checkIn.Text, StringComparison.Ordinal);
        Assert.True(rig.Host.Mails.IndexOf(entry) < rig.Host.Mails.IndexOf(checkIn));
    }

    [Fact]
    public async Task ACheckInForAChildTeamsBriefGoesToThatTeamsOrchestratorBoxAndNotToTheTops()
    {
        var rig = WithBrief();
        rig.Host.Registrations.Insert(0, new WatchRegistration("s0", "claude", 99, "2026-03-01T11:00:00.000Z", [new WatchBox("orchestrator@top", "/boxes/orchestrator@top")], "/private/tmp/ahprobe.h"));
        rig.Host.Owned["s0"] = [new OwnedTeam("top", new JsonObject(), [])];

        await rig.PassAsync(rig.Tick());

        var checkIn = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
        Assert.Equal("orchestrator@beta", checkIn.To);
        Assert.DoesNotContain(rig.Host.Mails, static m => m.To == "orchestrator@top");
    }

    [Fact]
    public async Task TheNextPeriodStillIdleGetsASecondEntryAndTheSilentEventSaysSo()
    {
        var rig = WithBrief();
        var tick = rig.Tick();

        await rig.PassUntilAsync(tick, "peer-silent");

        Assert.Equal(2, Entries(rig).Count());
        var silent = Assert.Single(rig.Host.Mails, static m => m.Event == "peer-silent");
        Assert.EndsWith("; resumed impl by mail (pasted if unread) (2/2)", silent.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterTheLimitOfEntriesTheCheckInIsAsItWasWithNoEntryAndNoSuffix()
    {
        // fewer-turns S3.3 (M-5): two member nudges per request; the Orchestrator's check-in still fires.
        var rig = WithBrief();
        for (var n = 1; n <= 2; n++)
        {
            rig.Host.SeededPeers.Add(new JsonObject { ["type"] = "overdue-resume", ["session_id"] = "s1", ["request_id"] = RequestId, ["n"] = n });
        }

        await rig.PassAsync(rig.Tick());

        Assert.Empty(Entries(rig));
        var checkIn = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
        Assert.DoesNotContain("resumed impl by mail (pasted if unread)", checkIn.Text, StringComparison.Ordinal);
    }

    // fewer-turns S3.1: M-1 (a launch 15 min ago holds both nudges) and M-2 (one past 2 x the 600 s threshold no longer does).
    [Theory]
    [InlineData(15, false)]
    [InlineData(21, true)]
    public async Task AMemberIdleOnItsOwnBackgroundWorkIsNotNudgedUntilTwiceTheThreshold(int launchedMinutesAgo, bool nudged)
    {
        var rig = WithBrief();
        var launched = Iso(Start.AddMinutes(-launchedMinutesAgo));
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = launched, ["state_since"] = launched, ["bg_open"] = 1, ["bg_since"] = launched };

        await rig.PassAsync(rig.Tick());

        Assert.Equal(nudged, Entries(rig).Any());
        Assert.Equal(nudged, rig.Host.Mails.Any(static m => m.Event == "check-in-due"));
    }

    [Fact]
    public async Task AnotherRequestsEntriesDoNotCountTowardTheLimit()
    {
        var rig = WithBrief();
        for (var n = 1; n <= 4; n++)
        {
            rig.Host.SeededPeers.Add(new JsonObject { ["type"] = "overdue-resume", ["session_id"] = "s1", ["request_id"] = "someone-else", ["n"] = n });
        }

        await rig.PassAsync(rig.Tick());

        Assert.Single(Entries(rig));
    }

    [Theory]
    [InlineData("working")]
    [InlineData("blocked")]
    public async Task ABusyMemberGetsNoEntryAndTheCheckInIsAsItWas(string activity)
    {
        var rig = WithBrief(activity);
        rig.Host.Candidates["s1"] = [PastTheCeiling(Brief())];

        await rig.PassAsync(rig.Tick());

        Assert.Empty(Entries(rig));
        var checkIn = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
        Assert.DoesNotContain("resumed", checkIn.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMemberIdleWithABackgroundTaskOpenGetsNoEntryAndNoCheckIn()
    {
        var rig = WithBrief();
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(Start), ["bg_open"] = 1 };

        await rig.PassAsync(rig.Tick());

        Assert.Empty(Entries(rig));
        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event == "check-in-due");
    }

    [Fact]
    // fewer-turns S3.1 (M-2): waiting 30 min is past 2 x the 600 s threshold, so the member reads idle and its check-in says no "working for".
    public async Task AWaitingMemberPastTwiceTheThresholdIsCheckedInAsIdle()
    {
        var rig = WithBrief();
        rig.Host.Candidates["s1"] = [PastTheCeiling(Brief())];
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(Start.AddMinutes(-30)), ["state_since"] = Iso(Start.AddMinutes(-30)), ["working_since"] = Iso(Start.AddMinutes(-100)), ["working_at"] = Iso(Start.AddMinutes(-30)), ["bg_open"] = 1 };

        await rig.PassAsync(rig.Tick());

        Assert.DoesNotContain("(working for", Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due").Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMemberIdleWithNoBackgroundTaskOpenStillGetsAnEntry()
    {
        var rig = WithBrief();
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "idle", ["at"] = Iso(Start), ["bg_open"] = 0 };

        await rig.PassAsync(rig.Tick());

        Assert.Single(Entries(rig));
    }

    [Fact]
    public async Task AMemberWhoseRecordedActivityIsUnknownGetsNoEntry()
    {
        var rig = WithBrief();
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "unknown", ["at"] = Iso(Start) };

        await rig.PassAsync(rig.Tick());

        Assert.Empty(Entries(rig));
    }

    private static JsonObject Compacted(DateTimeOffset workedAt, DateTimeOffset compactedAt) =>
        new() { ["activity"] = "idle", ["at"] = Iso(compactedAt.AddMinutes(1)), ["working_at"] = Iso(workedAt), ["compacted_at"] = Iso(compactedAt) };

    private static IEnumerable<WatchMail> StallMails(Rig rig) => rig.Host.Mails.Where(static m => m.Event == "stalled-after-compact");

    [Fact]
    public async Task AMemberThatCompactedThenStoppedGetsOneStallEventAcrossPolls()
    {
        var rig = WithBrief();
        rig.Host.Activity["m1"] = Compacted(Start.AddMinutes(-40), Start.AddMinutes(-30));

        await rig.PassAsync(rig.Tick());
        await rig.PassAsync(rig.Tick());

        Assert.Single(StallMails(rig));
        Assert.NotEmpty(Entries(rig));
    }

    [Fact]
    public async Task ACompactionInsideTheDebounceGivesNoStallEvent()
    {
        var rig = WithBrief();
        rig.Host.Activity["m1"] = Compacted(Start.AddMinutes(-5), Start.AddMinutes(-1));

        await rig.PassAsync(rig.Tick());

        Assert.Empty(StallMails(rig));
    }

    [Fact]
    public async Task AMemberThatWorkedAfterTheCompactionGetsNoStallEvent()
    {
        var rig = WithBrief();
        rig.Host.Activity["m1"] = Compacted(Start.AddMinutes(-20), Start.AddMinutes(-30));

        await rig.PassAsync(rig.Tick());

        Assert.Empty(StallMails(rig));
    }

    [Fact]
    public async Task AStalledMemberWhoseBriefIsClosedGetsNoStallEvent()
    {
        var rig = WithBrief(open: false);
        rig.Host.Activity["m1"] = Compacted(Start.AddMinutes(-40), Start.AddMinutes(-30));

        await rig.PassAsync(rig.Tick());

        Assert.Empty(StallMails(rig));
    }

    [Fact]
    public async Task AFailedMemberIsLeftToTheApiErrorPath()
    {
        var rig = WithBrief("failed");
        rig.Host.Activity["m1"] = new JsonObject { ["activity"] = "failed", ["failed_at"] = Iso(Start.AddHours(-1)), ["error"] = "invalid_request" };

        await rig.PassAsync(rig.Tick());

        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event == "resume" && m.Text.Contains("is overdue", StringComparison.Ordinal));
        var checkIn = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
        Assert.DoesNotContain("resumed", checkIn.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABriefThatHasItsResponseIsNotOverdue()
    {
        var rig = WithBrief(open: false);

        await rig.PassAsync(rig.Tick());

        Assert.Empty(Entries(rig));
        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event == "check-in-due");
    }

    [Fact]
    public async Task TheSamePeriodIsNeverResumedTwiceHoweverOftenTheWatchTicks()
    {
        var rig = WithBrief();

        await rig.PassAsync(rig.Tick(), polls: 8);

        Assert.Single(Entries(rig));
        Assert.Single(rig.Host.AppendedPeers, static r => r.Str("type") == "overdue-resume");
    }

    [Fact]
    public async Task AMemberWithNoBoxIsNotResumedAndTheCheckInSaysWhy()
    {
        var rig = WithBrief(team: null);
        rig.Host.Candidates["s1"] = [Brief() with { ToAddr = "impl", Team = null }];

        await rig.PassAsync(rig.Tick());

        Assert.Empty(Entries(rig));
        var checkIn = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
        Assert.EndsWith("; could not resume impl directly: no box", checkIn.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("working")]
    [InlineData("blocked")]
    [InlineData("failed")]
    public async Task ABusyMemberWithNoBoxKeepsTodaysCheckInText(string activity)
    {
        var rig = WithBrief(activity, team: null);
        rig.Host.Candidates["s1"] = [PastTheCeiling(Brief()) with { ToAddr = "impl", Team = null }];

        await rig.PassAsync(rig.Tick());

        var checkIn = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
        Assert.DoesNotContain("could not resume", checkIn.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("resumed", checkIn.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMemberWithNoActivityRecordIsUnknownNotIdleSoItGetsNoEntry()
    {
        var rig = WithBrief(activity: null);

        await rig.PassAsync(rig.Tick());

        Assert.Empty(Entries(rig));
        var checkIn = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
        Assert.EndsWith("; could not resume impl directly: activity unknown", checkIn.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownMemberAlreadyResumedToTheLimitKeepsTodaysCheckInText()
    {
        var rig = WithBrief(activity: null);
        for (var n = 1; n <= 4; n++)
        {
            rig.Host.SeededPeers.Add(new JsonObject { ["type"] = "overdue-resume", ["session_id"] = "s1", ["request_id"] = RequestId, ["n"] = n });
        }

        await rig.PassAsync(rig.Tick());

        var checkIn = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
        Assert.DoesNotContain("could not resume", checkIn.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheEntryCannotBeWrittenTheCheckInSaysSoInsteadOfClaimingAResume()
    {
        var rig = WithBrief();
        rig.Host.FailMail = static mail => mail.Event == "resume";

        await rig.PassAsync(rig.Tick());

        var checkIn = Assert.Single(rig.Host.Mails, static m => m.Event == "check-in-due");
        Assert.EndsWith("; could not resume impl directly: the entry was not written (mailbox full)", checkIn.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("resumed impl by mail (pasted if unread)", checkIn.Text, StringComparison.Ordinal);
    }
}
